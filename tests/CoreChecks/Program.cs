using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QobuzDownloaderX;
using QobuzDownloaderX.Helpers;
using QopenAPI;

class Program
{
    static readonly List<(string name, Func<Task> run)> Tests = new();
    static string Temporary = Path.Combine(Path.GetTempPath(), "qbdlx-checks-" + Guid.NewGuid().ToString("N"));
    static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static Task Sync(Action action) { action(); return Task.CompletedTask; }
    static Item Track(int depth = 16, double rate = 44.1) => new Item { Id = 1, Duration = 1, MaximumBitDepth = depth, MaximumSamplingRate = rate };
    static QopenAPI.Stream StreamInfo(int depth = 16, string rate = "44.1", int format = 6, int id = 1) =>
        new QopenAPI.Stream { TrackID = id, FormatID = format, BitDepth = depth.ToString(), SampleRate = rate, StreamURL = "https://audio.invalid/track" };
    static string Fixture(string ext) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone" + ext);
    static HttpResponseMessage Response(HttpStatusCode status, HttpContent content = null)
    { var r = new HttpResponseMessage(status) { Content = content ?? new ByteArrayContent(new byte[] { 1, 2, 3 }) }; r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return r; }
    static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) => new HttpClient(new Handler(action));
    static Task<HttpResponseMessage> Ready(HttpResponseMessage response) => Task.FromResult(response);
    static void Add(string name, Func<Task> run) => Tests.Add((name, run));

    static async Task<int> Main()
    {
        Directory.CreateDirectory(Temporary);
        if (Environment.GetEnvironmentVariable("QBDLX_REQUIRE_FLAC") == "1" &&
            !File.Exists(Environment.GetEnvironmentVariable("QBDLX_TEST_FLAC")))
        { Console.Error.WriteLine("CI requires the official FLAC decoder; these checks cannot be skipped."); return 1; }
        Add("429 and 503 retry, then complete byte-identical download", async () =>
        {
            int calls = 0; byte[] data = File.ReadAllBytes(Fixture(".flac"));
            using var client = Client((r, t) => Ready(Response(++calls == 1 ? (HttpStatusCode)429 : calls == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, new ByteArrayContent(data))));
            string path = Path.Combine(Temporary, "retry.flac");
            await ReliableHttp.DownloadAsync("https://audio.invalid/test", path, TimeSpan.FromSeconds(10), default, client: client);
            Check(calls == 3 && File.ReadAllBytes(path).SequenceEqual(data));
        });
        Add("permanent 404 is not retried", async () =>
        {
            int calls = 0; using var client = Client((r,t) => { calls++; return Ready(Response(HttpStatusCode.NotFound)); });
            await Reject<HttpStatusException>(() => ReliableHttp.GetAsync("https://audio.invalid/test", default, client: client)); Check(calls == 1);
        });
        Add("temporary errors stop after three attempts", async () =>
        {
            int calls = 0; using var client = Client((r,t) => { calls++; return Ready(Response(HttpStatusCode.ServiceUnavailable)); });
            await Reject<HttpStatusException>(() => ReliableHttp.GetAsync("https://audio.invalid/test", default, client: client)); Check(calls == 3);
        });
        Add("long Retry-After is respected without hammering", async () =>
        {
            int calls = 0; using var client = Client((r,t) => { calls++; var x=Response((HttpStatusCode)429); x.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)); return Ready(x); });
            await Reject<HttpStatusException>(() => ReliableHttp.GetAsync("https://audio.invalid/test", default, client: client)); Check(calls == 1);
        });
        Add("chunked response without Content-Length succeeds", async () =>
        {
            byte[] data = File.ReadAllBytes(Fixture(".flac"));
            using var client = Client((r,t) => Ready(Response(HttpStatusCode.OK,new StreamContent(new NonSeek(data)))));
            string path=Path.Combine(Temporary,"chunked.flac");
            await ReliableHttp.DownloadAsync("https://audio.invalid/test",path,TimeSpan.FromSeconds(5),default,client:client);
            Check(File.ReadAllBytes(path).SequenceEqual(data));
        });
        Add("truncated response is retried and ultimately fails", async () =>
        {
            int calls=0; using var client=Client((r,t)=> {calls++; var response=Response(HttpStatusCode.OK,new ByteArrayContent(new byte[]{1,2})); response.Content.Headers.ContentLength=100; return Ready(response);});
            await Reject<InvalidDataException>(()=>ReliableHttp.DownloadAsync("https://audio.invalid/test",Path.Combine(Temporary,"truncated.flac"),TimeSpan.FromSeconds(5),default,client:client)); Check(calls==3);
        });
        Add("midstream failure restarts from zero rather than appending",async()=>
        {
            int calls=0; byte[] data=File.ReadAllBytes(Fixture(".flac"));
            using var client=Client((r,t)=>Ready(Response(HttpStatusCode.OK,++calls==1?new StreamContent(new BrokenRead(data)):new ByteArrayContent(data))));
            string path=Path.Combine(Temporary,"restart.flac"); await ReliableHttp.DownloadAsync("https://audio.invalid/test",path,TimeSpan.FromSeconds(5),default,client:client);
            Check(calls==2 && File.ReadAllBytes(path).SequenceEqual(data));
        });
        Add("empty body never counts as a complete file",async()=>
        {
            using var client=Client((r,t)=>Ready(Response(HttpStatusCode.OK,new ByteArrayContent(Array.Empty<byte>()))));
            await Reject<InvalidDataException>(()=>ReliableHttp.DownloadAsync("https://audio.invalid/test",Path.Combine(Temporary,"empty.flac"),TimeSpan.FromSeconds(5),default,client:client));
        });
        Add("cancel while waiting for response headers",async()=>
        {
            using var cts=new CancellationTokenSource(50); int calls=0;
            using var client=Client(async(r,t)=>{calls++;await Task.Delay(Timeout.Infinite,t);return Response(HttpStatusCode.OK);});
            await Reject<OperationCanceledException>(()=>ReliableHttp.DownloadAsync("https://audio.invalid/test",Path.Combine(Temporary,"cancel.flac"),TimeSpan.FromSeconds(5),cts.Token,client:client)); Check(calls==1);
        });
        Add("cancel disposes a stream that ignores ReadAsync cancellation",async()=>
        {
            var stream=new BlockingRead(); using var cts=new CancellationTokenSource(50); var clock=Stopwatch.StartNew();
            using var client=Client((r,t)=>Ready(Response(HttpStatusCode.OK,new StreamContent(stream))));
            await Reject<OperationCanceledException>(()=>ReliableHttp.DownloadAsync("https://audio.invalid/test",Path.Combine(Temporary,"cancel-body.flac"),TimeSpan.FromSeconds(5),cts.Token,client:client));
            Check(stream.Disposed && clock.ElapsedMilliseconds<2000);
        });
        Add("a timed-out result cannot overwrite the next request",async()=>
        {
            using var gate=new ManualResetEventSlim();
            var late=IsolatedRequest.RunAsync(t=>{gate.Wait();return "old album";},TimeSpan.FromMilliseconds(30),default);
            await Reject<TimeoutException>(()=>late);
            string next=await IsolatedRequest.RunAsync(t=>"new album",TimeSpan.FromSeconds(1),default);
            gate.Set(); await Task.Delay(30); Check(next=="new album");
        });
        Add("isolated request cancellation propagates",async()=>
        {
            using var cts=new CancellationTokenSource(30);
            await Reject<OperationCanceledException>(()=>IsolatedRequest.RunAsync(t=>{t.WaitHandle.WaitOne();t.ThrowIfCancellationRequested();return 1;},TimeSpan.FromSeconds(5),cts.Token));
        });
        Add("wrong song identity is rejected",()=>Reject<InvalidDataException>(()=>Sync(()=>AudioQuality.FromResponse(StreamInfo(id:2),Track(),"27"))));
        Add("silent quality downgrade is rejected",()=>Reject<InvalidDataException>(()=>Sync(()=>AudioQuality.FromResponse(StreamInfo(),Track(24,192),"27"))));
        Add("source CD quality is accepted for highest quality selection",()=>Sync(()=>Check(AudioQuality.FromResponse(StreamInfo(),Track(),"27").SampleRate==44100)));
        Add("96 kHz selection accounts for 176.4 kHz source",()=>Sync(()=>Check(AudioQuality.FromResponse(StreamInfo(24,"88.2",7),Track(24,176.4),"7").SampleRate==88200)));
        Add("missing quality metadata is rejected",()=>Reject<InvalidDataException>(()=>Sync(()=>AudioQuality.FromResponse(StreamInfo(rate:""),Track(),"27"))));
        Add("format and HTTPS checks reject wrong format/address",async()=>
        {
            await Reject<InvalidDataException>(()=>Sync(()=>AudioQuality.FromResponse(StreamInfo(format:5),Track(),"27")));
            var x=StreamInfo();x.StreamURL="http://audio.invalid/test";await Reject<InvalidDataException>(()=>Sync(()=>AudioQuality.FromResponse(x,Track(),"27")));
        });
        Add("actual FLAC parameters and duration are inspected",async()=>
        {
            AudioVerification.Inspect(Fixture(".flac"),Track(),new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100});
            await Reject<InvalidDataException>(()=>Sync(()=>AudioVerification.Inspect(Fixture(".flac"),Track(),new AudioQuality{IsFlac=true,BitDepth=24,SampleRate=44100})));
            var x=Track();x.Duration=100;await Reject<InvalidDataException>(()=>Sync(()=>AudioVerification.Inspect(Fixture(".flac"),x,new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100})));
        });
        Add("MP3 fixture is structurally checked at selected bitrate",()=>Sync(()=>AudioVerification.Inspect(Fixture(".mp3"),Track(),new AudioQuality{IsFlac=false,SampleRate=44100})));
        Add("missing/corrupt/unverified files cannot be skipped",()=>Sync(()=>
        {
            string p=Path.Combine(Temporary,"unverified.flac");var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100};
            Check(!AudioVerification.CanSkip(p,Track(),"27",q)); File.WriteAllBytes(p,new byte[]{1});Check(!AudioVerification.CanSkip(p,Track(),"27",q));
            File.Copy(Fixture(".flac"),p,true);Check(!AudioVerification.CanSkip(p,Track(),"27",q));
        }));
        Add("receipt identifies song and quality and detects changed bytes",()=>Sync(()=>
        {
            string p=Path.Combine(Temporary,"verified.flac");File.Copy(Fixture(".flac"),p);var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100};
            AudioVerification.SaveReceipt(p,Track(),"27",q);Check(AudioVerification.CanSkip(p,Track(),"27",q));
            var other=Track();other.Id=2;Check(!AudioVerification.CanSkip(p,other,"27",q));Check(!AudioVerification.CanSkip(p,Track(),"6",q));
            byte[] data=File.ReadAllBytes(p);data[data.Length-1]^=1;File.WriteAllBytes(p,data);Check(!AudioVerification.CanSkip(p,Track(),"27",q));
        }));
        Add("different songs and quality selections have distinct paths",()=>Sync(()=>
        {
            string p=Path.Combine(Temporary,"song.flac");var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100};var other=Track();other.Id=2;
            string a=AudioVerification.IdentityPath(p,Track(),"27",q);Check(a!=AudioVerification.IdentityPath(p,other,"27",q));Check(a!=AudioVerification.IdentityPath(p,Track(),"6",q));
            string longPath=AudioVerification.IdentityPath(Path.Combine(Temporary,new string('a',250)+".flac"),Track(),"27",q);Check(Path.GetFileName(longPath).Length<=255);
        }));
        Add("templates cannot escape download root",async()=>
        {
            await Reject<InvalidDataException>(()=>Sync(()=>DownloadPaths.SafePath(Temporary,Path.Combine(Temporary,"..","outside"))));
            Check(DownloadPaths.SafePath(Temporary,Path.Combine(Temporary,"album","song.flac")).StartsWith(Temporary));
        });
        Add("credential protection failure never returns plaintext",()=>Sync(()=>
        {
            Check(CredentialProtection.Encrypt("test credential",bytes=>throw new CryptographicException())=="");
            Check(CredentialProtection.Encrypt("test credential",bytes=>new byte[]{1,2})==CredentialProtection.Prefix+"AQI=");
        }));
        Add("Base64-looking legacy passwords and keys keep their literal values",()=>Sync(()=>
        {
            foreach (string value in new[] { "cGFzc3dvcmQ=", "12345678", "0123456789abcdef0123456789abcdef" })
            {
                string read = CredentialProtection.Read(value, bytes => bytes, bytes => throw new Exception("Plaintext mistaken for ciphertext"), out string saved);
                Check(read == value && saved.StartsWith(CredentialProtection.Prefix));
                Check(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(saved.Substring(CredentialProtection.Prefix.Length))) == value);
            }
        }));
        Add("new protected credentials restore without repeated encryption",()=>Sync(()=>
        {
            string saved = CredentialProtection.Encrypt("literal password", bytes => bytes);
            string read = CredentialProtection.Read(saved, bytes => throw new Exception("Unexpected encryption"), bytes => bytes, out string replacement);
            Check(read == "literal password" && replacement == saved);
        }));
        Add("legacy DPAPI credentials receive a marker without losing the value",()=>Sync(()=>
        {
            byte[] header = {1,0,0,0,0xd0,0x8c,0x9d,0xdf,1,0x15,0xd1,0x11,0x8c,0x7a,0,0xc0,0x4f,0xc2,0x97,0xeb};
            string saved = Convert.ToBase64String(header.Concat(System.Text.Encoding.UTF8.GetBytes("old secret")).ToArray());
            string read = CredentialProtection.Read(saved, bytes => throw new Exception("Unexpected encryption"), bytes => bytes.Skip(header.Length).ToArray(), out string replacement);
            Check(read == "old secret" && replacement == CredentialProtection.Prefix + saved);
        }));
        Add("unreadable protected credentials are cleared instead of reused as passwords",()=>Sync(()=>
        {
            foreach (string saved in new[] { CredentialProtection.Prefix + "AQI=", CredentialProtection.Prefix + "invalid!", "AQAAANCMnd8BFdERjHoAwE/Cl+s=" })
            {
                string read = CredentialProtection.Read(saved, bytes => throw new Exception("Unexpected encryption"), bytes => throw new CryptographicException(), out string replacement);
                Check(read == "" && replacement == "");
            }
        }));
        Add("failed legacy migration clears storage while retaining only the current input",()=>Sync(()=>
        {
            string read = CredentialProtection.Read("cGFzc3dvcmQ=", bytes => throw new CryptographicException(), bytes => throw new Exception(), out string replacement);
            Check(read == "cGFzc3dvcmQ=" && replacement == "");
        }));
        Add("long audio names leave enough space for receipts and temporary writes",()=>Sync(()=>
        {
            var q = new AudioQuality { IsFlac = true, BitDepth = 16, SampleRate = 44100 };
            string path = AudioVerification.IdentityPath(Path.Combine(Temporary, new string('a', 300) + ".flac"), Track(), "27", q);
            Check(Path.GetFileName(path).Length + " (100000)".Length + AudioVerification.ReceiptExtension.Length <= 255);
            File.Copy(Fixture(".flac"), path);
            AudioVerification.SaveReceipt(path, Track(), "27", q);
            AudioVerification.SaveReceipt(path, Track(), "27", q);
            Check(AudioVerification.CanSkip(path, Track(), "27", q));
            Check(!Directory.EnumerateFiles(Temporary, ".qbdlx-receipt-*.tmp").Any());
        }));
        Add("logs remove credentials and signed query parameters",()=>Sync(()=>
        {
            string s=SensitiveLog.Redact("password=test-token https://audio.invalid/file?sig=test-token user_auth_token=another-value https://someone:unknown-secret@host.invalid/private-address", "test-token");
            Check(!s.Contains("test-token")&&!s.Contains("another-value")&&!s.Contains("sig=")&&!s.Contains("unknown-secret")&&!s.Contains("private-address"));
        }));
        Add("failed item is separate from successful/skipped counts",()=>Sync(()=>
        {
            var stats=new DownloadStats();stats.Success();stats.Skip();stats.Failure("1","timeout");
            Check(stats.Succeeded==1&&stats.Skipped==1&&stats.Failed==1&&stats.Failures.Count==1);
        }));
        Add("API query encoding and returned identity are checked",async()=>
        {
            using var client=Client((r,t)=>
            {
                Check(r.RequestUri.Host=="www.qobuz.com");Check(r.RequestUri.Query.Contains("user_auth_token=a%26b"));
                return Ready(Response(HttpStatusCode.OK,new StringContent("{\"id\":2}")));
            });
            var api=new ReliableQobuzService(default,client);
            await Reject<InvalidDataException>(()=>Sync(()=>api.TrackGetWithAuth("test-app","1","a&b")));
        });
        Add("incomplete pagination cannot return a partial album",async()=>
        {
            int calls=0;using var client=Client((r,t)=>
            {
                string json=++calls==1?"{\"id\":\"album\",\"tracks\":{\"total\":2,\"items\":[{\"id\":1}]}}":"{\"id\":\"album\",\"tracks\":{\"total\":2,\"items\":[]}}";
                return Ready(Response(HttpStatusCode.OK,new StringContent(json)));
            });
            await Reject<InvalidDataException>(()=>Sync(()=>new GetInfo(new ReliableQobuzService(default,client)).getAlbumInfoLabels("test-app","album","test-auth")));Check(calls==2);
        });
        Add("successful pagination includes the entire album",()=>Sync(()=>
        {
            int calls=0;using var client=Client((r,t)=>
            {
                string json="{\"id\":\"album\",\"tracks\":{\"total\":2,\"items\":[{\"id\":"+(++calls)+"}]}}";
                return Ready(Response(HttpStatusCode.OK,new StringContent(json)));
            });
            var album=new GetInfo(new ReliableQobuzService(default,client)).getAlbumInfoLabels("test-app","album","test-auth");Check(album.Tracks.Items.Count==2&&calls==2);
        }));
        foreach (string kind in new[] { "album", "playlist", "artist", "label", "favorite-albums", "favorite-tracks", "favorite-artists" })
        {
            Add(kind + " pagination follows short pages without skipping entries",()=>Sync(()=>
            {
                int calls = 0;
                string list = kind == "artist" || kind == "label" ? "albums" : kind.StartsWith("favorite-") ? kind.Substring("favorite-".Length) : "tracks";
                using var client = Client((request, token) =>
                {
                    int offset = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"[?&]offset=(\d+)").Groups[1].Value);
                    Check(offset == calls && calls < 3, "Skipped entries after a short page: " + request.RequestUri.Query);
                    calls++;
                    var page = new Dictionary<string, object> { ["id"] = "123", [list] = new { total = 3, items = new[] { new { id = kind == "playlist" ? 42 : offset + 1, position = offset + 1 } } } };
                    return Ready(Response(HttpStatusCode.OK, new StringContent(JsonConvert.SerializeObject(page))));
                });
                var info = new GetInfo(new ReliableQobuzService(default, client));
                List<Item> items;
                if (kind == "album") items = info.getAlbumInfoLabels("app", "123", "auth").Tracks.Items;
                else if (kind == "playlist") items = info.getPlaylistInfoLabels("app", "123", "auth").Tracks.Items;
                else if (kind == "artist") items = info.getArtistInfo("app", "123", "auth").Albums.Items;
                else if (kind == "label") items = info.getLabelInfo("app", "123", "auth").Albums.Items;
                else
                {
                    var favorites = info.getFavoritesInfo("app", "123", list, "auth");
                    items = list == "albums" ? favorites.Albums.Items : list == "tracks" ? favorites.Tracks.Items : favorites.Artists.Items;
                }
                Check(calls == 3 && items.Count == 3);
                Check(items.Select(item => item.Position).SequenceEqual(new[] { 1, 2, 3 }));
                if (kind == "playlist") Check(items.All(item => item.Id.ToString() == "42"), "Legitimate repeated playlist song rejected");
            }));
        }
        Add("wrong playlist artist and label identities are rejected",async()=>
        {
            using var client = Client((request, token) => Ready(Response(HttpStatusCode.OK, new StringContent("{\"id\":456}"))));
            var service = new ReliableQobuzService(default, client);
            await Reject<InvalidDataException>(() => Sync(() => service.PlaylistGetWithAuth("app", "auth", "123", "tracks")));
            await Reject<InvalidDataException>(() => Sync(() => service.ArtistGetWithAuth("app", "123", "auth", "albums")));
            await Reject<InvalidDataException>(() => Sync(() => service.LabelGetWithAuth("app", "123", "albums", "auth")));
        });
        Add("a collection edited during pagination is not reported as complete",async()=>
        {
            int calls = 0;
            using var client = Client((request, token) => Ready(Response(HttpStatusCode.OK, new StringContent(JsonConvert.SerializeObject(new { id = "123", tracks = new { total = ++calls == 1 ? 2 : 3, items = new[] { new { id = calls } } } })))));
            await Reject<InvalidDataException>(() => Sync(() => new GetInfo(new ReliableQobuzService(default, client)).getAlbumInfoLabels("app", "123", "auth")));
            Check(calls == 2);
        });
        Add("artist release pagination advances by the number actually returned",()=>Sync(()=>
        {
            int calls = 0;
            using var client = Client((request, token) =>
            {
                int offset = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"[?&]offset=(\d+)").Groups[1].Value);
                Check(offset == calls && calls < 3); calls++;
                return Ready(Response(HttpStatusCode.OK, new StringContent(JsonConvert.SerializeObject(new { has_more = calls < 3, items = new[] { new { id = calls.ToString() } } }))));
            });
            var ids = new GetInfo(new ReliableQobuzService(default, client)).GetArtistReleaseTypeIds("app", "123", "album", "auth");
            Check(calls == 3 && ids.SetEquals(new[] { "1", "2", "3" }));
        }));
        Add("empty artist release page with more pending cannot silently drop releases",async()=>
        {
            using var client = Client((request, token) => Ready(Response(HttpStatusCode.OK, new StringContent("{\"has_more\":true,\"items\":[]}"))));
            await Reject<InvalidDataException>(() => Sync(() => new GetInfo(new ReliableQobuzService(default, client)).GetArtistReleaseTypeIds("app", "123", "album", "auth")));
        });
        Add("repeated artist release pages cannot run forever",async()=>
        {
            int calls = 0;
            using var client = Client((request, token) => { calls++; return Ready(Response(HttpStatusCode.OK, new StringContent("{\"has_more\":true,\"items\":[{\"id\":\"one\"}]}"))); });
            await Reject<InvalidDataException>(() => Sync(() => new GetInfo(new ReliableQobuzService(default, client)).GetArtistReleaseTypeIds("app", "123", "album", "auth")));
            Check(calls == 2);
        });
        if (!OperatingSystem.IsWindows())
        {
            Add("nonzero FLAC verifier exit fails the download",async()=>
            {
                string tool=WriteTool("reject", "exit 7");
                await Reject<InvalidDataException>(()=>new FixMD5(tool).ValidateAsync(Fixture(".flac"),false,default));
            });
            Add("cancel kills the running verifier",async()=>
            {
                string tool=WriteTool("wait", "exec /bin/sleep 10"); using var cts=new CancellationTokenSource(100);var clock=Stopwatch.StartNew();
                await Reject<OperationCanceledException>(()=>new FixMD5(tool).ValidateAsync(Fixture(".flac"),false,cts.Token));Check(clock.ElapsedMilliseconds<2000);
            });
            Add("missing verifier is an explicit failure",()=>Reject<IOException>(()=>new FixMD5(Path.Combine(Temporary,"missing-tool")).ValidateAsync(Fixture(".flac"),false,default)));
        }
        Add("quality labels use the confirmed file, not source maxima",()=>Sync(()=>
        {
            var q=AudioQuality.FromResponse(StreamInfo(),Track(24,192),"6");
            Check(AudioQuality.FormatLabel(".flac",q.BitDepth,q.SampleRate/1000.0)=="FLAC (16bit-44.1kHz)");
        }));
        Add("a verified replacement can be skipped while preserving the bad original",()=>Sync(()=>
        {
            string p=Path.Combine(Temporary,"preserved.flac");File.WriteAllBytes(p,new byte[]{1});
            string copy=Path.Combine(Temporary,"preserved (1).flac");File.Copy(Fixture(".flac"),copy);
            var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100};AudioVerification.SaveReceipt(copy,Track(),"27",q);
            Check(AudioVerification.CanSkipAnyVerifiedCopy(p,Track(),"27",q));Check(File.ReadAllBytes(p).Length==1);
        }));
        Add("duplicate page identities cannot silently replace missing songs",async()=>
        {
            using var client=Client((r,t)=>Ready(Response(HttpStatusCode.OK,new StringContent("{\"id\":\"album\",\"tracks\":{\"total\":2,\"items\":[{\"id\":1},{\"id\":1}]}}"))));
            await Reject<InvalidDataException>(()=>Sync(()=>new GetInfo(new ReliableQobuzService(default,client)).getAlbumInfoLabels("test-app","album","test-auth")));
        });
        Add("empty counts do not generate invalid filename padding",()=>Sync(()=>
        {
            var p=new PaddingNumbers();Check(p.padTracks(new Album{TracksCount=0})==2);Check(p.padTracks(new Album{TracksCount=1000})==4);Check(p.padDiscs(new Album{MediaCount=0})==2);
        }));
        Add("language packs and built-in fallback remain valid JSON",()=>Sync(()=>
        {
            string repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
            foreach(string file in Directory.GetFiles(Path.Combine(repo,"QobuzDownloaderX/Resources/Languages"),"*.json"))
            { var data=JsonConvert.DeserializeObject<Dictionary<string,string>>(File.ReadAllText(file));Check(data.ContainsKey("downloadResultSummary")&&data.ContainsKey("processed")); }
            string source=File.ReadAllText(Path.Combine(repo,"QobuzDownloaderX/Helpers/Theming.cs"));
            string json=System.Text.RegularExpressions.Regex.Match(source,@"public const string defaultLanguage = @""([\s\S]*?)"";").Groups[1].Value.Replace("\"\"","\"");
            Check(JsonConvert.DeserializeObject<Dictionary<string,string>>(json).ContainsKey("downloadResultSummary"));
        }));
        string realTool=Environment.GetEnvironmentVariable("QBDLX_TEST_FLAC");
        if(!string.IsNullOrEmpty(realTool))
        {
            Add("official FLAC decoder validates every frame without recompressing a valid file",async()=>
            {
                string p=Path.Combine(Temporary,"real-tool.flac");File.Copy(Fixture(".flac"),p);byte[] before=File.ReadAllBytes(p);
                await new FixMD5(realTool).ValidateAsync(p,true,default);Check(before.SequenceEqual(File.ReadAllBytes(p)));
            });
            Add("official FLAC decoder rejects corrupted audio payload",async()=>
            {
                string p=Path.Combine(Temporary,"corrupt-payload.flac");byte[] data=File.ReadAllBytes(Fixture(".flac"));data[data.Length-20]^=127;File.WriteAllBytes(p,data);
                await Reject<InvalidDataException>(()=>new FixMD5(realTool).ValidateAsync(p,false,default));
            });
            Add("only an unset FLAC MD5 is repaired and the repair is verified",async()=>
            {
                string p=Path.Combine(Temporary,"unset-md5.flac");byte[] data=File.ReadAllBytes(Fixture(".flac"));Array.Clear(data,26,16);File.WriteAllBytes(p,data);
                await new FixMD5(realTool).ValidateAsync(p,true,default);Check(File.ReadAllBytes(p).Skip(26).Take(16).Any(b=>b!=0));
            });
        }
        Add("cancelled retry backoff stops further network requests", async () =>
        {
            int calls=0; using var cts=new CancellationTokenSource(80);
            using var client=Client((r,t)=>{ calls++; var response=Response(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)); return Ready(response); });
            await Reject<OperationCanceledException>(()=>ReliableHttp.GetAsync("https://audio.invalid/backoff",cts.Token,client:client));
            Check(calls==1);
        });
        Add("408 retries but authentication rejection does not", async () =>
        {
            int calls=0; using var transient=Client((r,t)=>Ready(Response(++calls==1?HttpStatusCode.RequestTimeout:HttpStatusCode.OK)));
            using var response=await ReliableHttp.GetAsync("https://audio.invalid/timeout",default,client:transient); Check(calls==2);
            int denied=0; using var permanent=Client((r,t)=>{denied++;return Ready(Response(HttpStatusCode.Unauthorized));});
            await Reject<HttpStatusException>(()=>ReliableHttp.GetAsync("https://audio.invalid/denied",default,client:permanent)); Check(denied==1);
        });
        Add("a corrupt receipt is rejected and atomic replacement leaves no temporary files",()=>Sync(()=>
        {
            string p=Path.Combine(Temporary,"replace-receipt.flac");File.Copy(Fixture(".flac"),p);
            var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100}; File.WriteAllText(p+".qbdlx.json","{broken");
            Check(!AudioVerification.CanSkip(p,Track(),"27",q)); AudioVerification.SaveReceipt(p,Track(),"27",q);
            AudioVerification.SaveReceipt(p,Track(),"27",q); Check(AudioVerification.CanSkip(p,Track(),"27",q));
            Check(!Directory.EnumerateFiles(Temporary,"replace-receipt.flac.*.tmp").Any());
        }));
        Add("200 sequential transfers preserve each song's bytes, tags, identity and failure counts",async()=>
        {
            string folder=Path.Combine(Temporary,"batch");Directory.CreateDirectory(folder);
            var stats=new DownloadStats(); var q=new AudioQuality{IsFlac=true,BitDepth=16,SampleRate=44100};
            var attempts=new Dictionary<int,int>(); int current=0; byte[] body=Array.Empty<byte>();
            using var client=Client((r,t)=>
            {
                Check(r.RequestUri.AbsolutePath=="/"+current);
                int count=attempts.TryGetValue(current,out var previous)?previous+1:1;attempts[current]=count;
                return Ready(Response(current%17==0?HttpStatusCode.NotFound:current%10==0&&count==1?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK,new ByteArrayContent(body)));
            });
            var expected=new Dictionary<string,int>();
            for(current=1;current<=200;current++)
            {
                var track=Track();track.Id=current;string staged=Path.Combine(folder,"staged.flac");File.Copy(Fixture(".flac"),staged,true);
                using(var tagged=TagLib.File.Create(staged)){tagged.Tag.Title="song-"+current;tagged.Save();} body=File.ReadAllBytes(staged);File.Delete(staged);
                string destination=AudioVerification.IdentityPath(Path.Combine(folder,"same title.flac"),track,"27",q);
                try
                {
                    await ReliableHttp.DownloadAsync("https://audio.invalid/"+current,staged,TimeSpan.FromSeconds(5),default,client:client);
                    Check(File.ReadAllBytes(staged).SequenceEqual(body));AudioVerification.Inspect(staged,track,q);File.Move(staged,destination);
                    AudioVerification.SaveReceipt(destination,track,"27",q);stats.Success();expected.Add(destination,current);
                }
                catch(HttpStatusException ex) when(ex.StatusCode==404){stats.Failure(current.ToString(),"HTTP 404");Check(!File.Exists(destination));}
                finally{if(File.Exists(staged))File.Delete(staged);}
            }
            Check(stats.Failed==11&&stats.Succeeded==189&&stats.Skipped==0&&expected.Count==189);
            foreach(var entry in expected)
            {
                var track=Track();track.Id=entry.Value;Check(AudioVerification.CanSkip(entry.Key,track,"27",q));
                using var tagged=TagLib.File.Create(entry.Key);Check(tagged.Tag.Title=="song-"+entry.Value);
                track.Id=entry.Value+1;Check(!AudioVerification.CanSkip(entry.Key,track,"27",q));
            }
            Check(attempts.Count==200&&!Directory.EnumerateFiles(folder,"*.tmp").Any());
        });
        int failed=0; var report=new CheckReport();
        try
        {
            foreach(var test in Tests)
            {
                var clock=Stopwatch.StartNew(); Exception error=null;
                try { await test.run();Console.WriteLine("PASS "+test.name); }
                catch(Exception ex){error=ex;failed++;Console.WriteLine("FAIL "+test.name+": "+ex);}
                report.Add(test.name,clock.Elapsed,error);
            }
            Console.WriteLine($"{Tests.Count-failed} passed, {failed} failed, {Tests.Count} executed. HTTP is simulated; real Windows/Qobuz GUI tests were not run.");
            report.Save("Core download checks"); return failed==0?0:1;
        }
        finally { Directory.Delete(Temporary,true); }
    }
    static string WriteTool(string name,string command)
    { if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException();string p=Path.Combine(Temporary,name);File.WriteAllText(p,"#!/bin/sh\n"+command+"\n");File.SetUnixFileMode(p,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);return p; }
}
class Handler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run;
    public Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run){this.run=run;}
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>run(request,token);
}
class NonSeek : System.IO.Stream
{
    protected readonly MemoryStream inner;public NonSeek(byte[] data){inner=new MemoryStream(data);}
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int c)=>inner.Read(b,o,c);
    public override Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken t)=>inner.ReadAsync(b,o,c,t);
    public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
}
class BrokenRead : NonSeek
{
    int reads;public BrokenRead(byte[] data):base(data){}
    public override Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken t)=>++reads==1?base.ReadAsync(b,o,Math.Min(c,100),t):Task.FromException<int>(new IOException("connection interrupted"));
}
class BlockingRead : NonSeek
{
    readonly TaskCompletionSource<int> pending=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Disposed;public BlockingRead():base(Array.Empty<byte>()){}
    public override Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken t)=>pending.Task;
    protected override void Dispose(bool disposing){Disposed=true;pending.TrySetException(new IOException("disposed"));base.Dispose(disposing);}
}
// UI adapters only: all networking, identity, quality, file verification, crypto failure handling and pagination above use production files.
namespace QobuzDownloaderX
{
    class qbdlxForm{public static qbdlxForm _qbdlxForm=new();public Logger logger=new();public TextBoxAdapter downloadOutput=new();public Language languageManager=new();}
    class Logger{public void Debug(string s){}public void Error(string s){}public void Warning(string s){}}
    class TextBoxAdapter{public string TextValue="";public string Text{get=>TextValue;set=>TextValue=value;}}
    class Language{public string GetTranslation(string key)=>"succeeded {0}, failed {1}, skipped {2}";}
}
namespace QobuzDownloaderX.Helpers
{
    static class Miscellaneous{public static string GetCheckedDownloadFromArtistTypes()=>"all";public static void update(qbdlxForm f,string text){f.downloadOutput.Text=text;}}
}
