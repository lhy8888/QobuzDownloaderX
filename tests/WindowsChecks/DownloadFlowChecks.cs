using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using QobuzDownloaderX;
using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;

internal static class DownloadFlowChecks
{
    private static void Check(bool condition, string message = "Download flow assertion failed") { if (!condition) throw new Exception(message); }
    private static void Pump(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(3)) { Application.DoEvents(); Thread.Sleep(1); }
        Check(task.IsCompleted, "The actual download pipeline did not finish");
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static void Reject<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Fixture(string ext) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "tone" + ext);
    private static HttpResponseMessage Response(HttpStatusCode status, HttpContent content)
    {
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return response;
    }
    private static HttpResponseMessage Json(object value) => Response(HttpStatusCode.OK, new StringContent(JsonConvert.SerializeObject(value)));
    private static int Id(HttpRequestMessage request) => int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"[?&]track_id=(\d+)").Groups[1].Value);
    private static Album Album(string id, int count) => JsonConvert.DeserializeObject<Album>(JsonConvert.SerializeObject(new
    {
        id, title = "同名专辑", streamable = true, tracks_count = count, media_count = 1,
        maximum_bit_depth = 16, maximum_sampling_rate = 44.1,
        artists = new object[0], image = new { large = "https://cover.invalid/cover_600.jpg" },
        tracks = new { total = count, items = Enumerable.Range(1, count).Select(n => new { id = n, title = "同名歌曲", track_number = n, media_number = 1 }) }
    }));
    private static Item Detail(int id, Album album) => new Item
    {
        Id = id, Title = "同名歌曲", TrackNumber = id, MediaNumber = 1, Duration = 1,
        MaximumBitDepth = 16, MaximumSamplingRate = 44.1, Streamable = true, Album = album
    };
    private static QopenAPI.Stream Url(int id, bool mp3 = false) => new QopenAPI.Stream
    { TrackID = id, FormatID = mp3 ? 5 : 6, BitDepth = mp3 ? "0" : "16", SampleRate = "44.1", StreamURL = "https://audio.invalid/" + id };
    private static Task DownloadAlbum(HttpClient client, Album album, string folder, DownloadStats stats, Action<int> processed = null) =>
        Task.Run(() => new DownloadAlbum(client).DownloadAlbumAsync("app", album.Id, "27", ".flac", "auth", "secret", folder,
            "artist", "%AlbumTitle%", "%TrackTitle%", album, null,
            processed == null ? null : new DirectProgress<(int current, int total)>(p => processed(p.current)), stats, CancellationToken.None));
    internal static void Register(Action<string, Action> add, Func<qbdlxForm> getForm, string temporary)
    {
        add("background output reads and writes run on the real Windows UI thread", () =>
        {
            var form = getForm(); _ = form.Handle; _ = form.downloadOutput.Handle; _ = form.progressLabel.Handle;
            form.downloadOutput.Clear();
            Pump(Task.Run(() => { new GetInfo().updateDownloadOutput("first"); new GetInfo().updateDownloadOutput("second"); }));
            Check(form.downloadOutput.Text == "firstsecond");
        });
        add("actual MP3 tagging tolerates .flac in a parent folder and missing artist roles", () =>
        {
            using (new SavedSettings())
            {
                Settings.Default.mergeArtistNames = true;
                string directory = Path.Combine(temporary, "music.flac"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "actual.mp3"); File.Copy(Fixture(".mp3"), path);
                var album = Album("optional", 1); album.Artists.Add(new ArtistsList { Name = "No role", Roles = null });
                TagFile.WriteToFile(path, null, album, Detail(1, album));
                Pump(Mp3Verification.ValidateAsync(path, default(CancellationToken)));
                using (var file = TagLib.File.Create(path)) Check(file.Tag.Title == "同名歌曲");
            }
        });
        add("actual 200-track queue preserves every identity and continues after network failures", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var album = Album("actual-200", 200); string folder = Path.Combine(temporary, "actual-200");
                Directory.CreateDirectory(folder); getForm().downloadLocation = folder;
                var sources = new Dictionary<int, byte[]>(); string scratch = Path.Combine(folder, "source.flac");
                for (int n = 1; n <= 200; n++)
                {
                    File.Copy(Fixture(".flac"), scratch, true);
                    using (var file = TagLib.File.Create(scratch))
                    { ((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true)).SetField("TEST_SOURCE_ID", n.ToString()); file.Save(); }
                    sources[n] = File.ReadAllBytes(scratch);
                }
                File.Delete(scratch); var attempts = new Dictionary<int, int>(); int metadata = 0;
                using (var client = new HttpClient(new Handler((request, token) =>
                {
                    if (request.RequestUri.AbsolutePath.EndsWith("/track/get")) { metadata++; return Task.FromResult(Json(Detail(Id(request), album))); }
                    if (request.RequestUri.AbsolutePath.EndsWith("/track/getFileUrl")) return Task.FromResult(Json(Url(Id(request))));
                    int id = int.Parse(request.RequestUri.AbsolutePath.Trim('/'));
                    int attempt = attempts.TryGetValue(id, out var old) ? old + 1 : 1; attempts[id] = attempt;
                    return Task.FromResult(Response(id % 17 == 0 ? HttpStatusCode.NotFound : id % 10 == 0 && attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, new ByteArrayContent(sources[id])));
                })))
                using (var stats = new DownloadStats())
                {
                    Pump(DownloadAlbum(client, album, folder, stats));
                    Check(metadata == 200 && stats.Succeeded == 189 && stats.Failed == 11 && stats.Skipped == 0);
                    Check(Enumerable.Range(1, 200).Where(id => id % 17 == 0).All(id => stats.Failures.Any(f => f.StartsWith(id + ":"))));
                    string[] receipts = Directory.GetFiles(folder, "*" + AudioVerification.ReceiptExtension, SearchOption.AllDirectories); Check(receipts.Length == 189);
                    foreach (string receiptPath in receipts)
                    {
                        var receipt = JsonConvert.DeserializeObject<AudioReceipt>(File.ReadAllText(receiptPath)); int id = int.Parse(receipt.TrackId);
                        string path = receiptPath.Substring(0, receiptPath.Length - AudioVerification.ReceiptExtension.Length);
                        Check(path.Contains("[ID" + id + "-F27-16bit-44100Hz]"));
                        Check(AudioVerification.CanSkip(path, Detail(id, album), "27", new AudioQuality { IsFlac = true, BitDepth = 16, SampleRate = 44100 }));
                        using (var file = TagLib.File.Create(path))
                        { Check(((TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph)).GetFirstField("TEST_SOURCE_ID") == id.ToString()); Check(file.Tag.Title == "同名歌曲"); }
                    }
                    Check(!Directory.GetFiles(folder, ".qbdlx-*", SearchOption.AllDirectories).Any());
                }
            }
        });
        add("skip on an album's final track never skips the next album", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var first = Album("skip-last", 1); var second = Album("after-skip", 3); Album current = first;
                using (var client = SuccessfulClient(() => current))
                using (var stats = new DownloadStats())
                {
                    Pump(DownloadAlbum(client, first, Path.Combine(temporary, "skip-last"), stats, n => qbdlxForm.skipCurrentAlbum = true));
                    Check(!qbdlxForm.skipCurrentAlbum); current = second;
                    Pump(DownloadAlbum(client, second, Path.Combine(temporary, "after-skip"), stats));
                    Check(stats.Succeeded == 4 && stats.SkippedByUser == 0 && stats.Failed == 0);
                }
            }
        });
        add("skip during an album counts remaining tracks as manual skips", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var album = Album("skip-remaining", 3);
                using (var client = SuccessfulClient(() => album))
                using (var stats = new DownloadStats())
                {
                    Pump(DownloadAlbum(client, album, Path.Combine(temporary, "skip-remaining"), stats, n => qbdlxForm.skipCurrentAlbum = true));
                    Check(stats.Succeeded == 1 && stats.SkippedByUser == 2 && stats.Skipped == 0 && !qbdlxForm.skipCurrentAlbum);
                    Check(stats.Summary().Contains("Skipped by you: 2"));
                }
            }
        });
        add("track metadata failures record the song ID and allow the next song", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var album = Album("metadata-error", 2);
                using (var client = new HttpClient(new Handler((request, token) =>
                {
                    if (request.RequestUri.AbsolutePath.EndsWith("/track/get")) return Task.FromResult(Id(request) == 1 ? Response(HttpStatusCode.NotFound, new StringContent("missing")) : Json(Detail(2, album)));
                    if (request.RequestUri.AbsolutePath.EndsWith("/track/getFileUrl")) return Task.FromResult(Json(Url(2)));
                    return Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(File.ReadAllBytes(Fixture(".flac")))));
                })))
                using (var stats = new DownloadStats())
                {
                    Pump(DownloadAlbum(client, album, Path.Combine(temporary, "metadata-error"), stats));
                    Check(stats.Succeeded == 1 && stats.Failed == 1 && stats.Failures[0].StartsWith("1:"));
                }
            }
        });
        add("actual cover embedding downloads once for three songs at the same size", () =>
        {
            using (new SavedSettings())
            {
                Configure(); Settings.Default.imageTag = true; Settings.Default.dontSaveArtworkToDisk = false;
                var form = getForm(); form.embeddedArtSize = "600"; form.savedArtSize = "600";
                var album = Album("cached-cover", 3); int requests = 0; byte[] jpeg;
                using (var image = new Bitmap(16, 16)) using (var buffer = new MemoryStream()) { image.Save(buffer, ImageFormat.Jpeg); jpeg = buffer.ToArray(); }
                using (var client = SuccessfulClient(() => album, () => { requests++; return jpeg; }))
                using (var stats = new DownloadStats())
                {
                    string folder = Path.Combine(temporary, "cached-cover"); Pump(DownloadAlbum(client, album, folder, stats));
                    Check(stats.Succeeded == 3 && stats.Failed == 0 && requests == 1);
                    foreach (string path in Directory.GetFiles(folder, "*.flac", SearchOption.AllDirectories))
                        using (var file = TagLib.File.Create(path)) Check(file.Tag.Pictures.Length == 1);
                }
            }
        });
        add("actual failed receipt saving publishes no new audio and counts no success", () =>
        {
            using (new SavedSettings())
            {
                Configure(); string folder = Path.Combine(temporary, "receipt-fail"); Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "song.mp3"); Directory.CreateDirectory(path + AudioVerification.ReceiptExtension);
                using (var client = new HttpClient(new Handler((r, t) => Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(File.ReadAllBytes(Fixture(".mp3"))))))))
                using (var download = new DownloadFile(client))
                using (var stats = new DownloadStats())
                {
                    var album = Album("receipt-fail", 1);
                    Reject<IOException>(() => Pump(Task.Run(() => download.DownloadStream("track", "https://audio.invalid/1", folder, path, ".mp3", album, Detail(1, album), new GetInfo(), default(CancellationToken), stats, new AudioQuality { SampleRate = 44100 }, "5"))));
                    Check(stats.Succeeded == 0 && !File.Exists(path) && !Directory.GetFiles(folder, ".qbdlx-*", SearchOption.AllDirectories).Any());
                }
            }
        });
        add("Windows locked receipt rolls back replacement of both old files", () =>
        {
            string folder = Path.Combine(temporary, "locked-commit"); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "song.flac"), staged = Path.Combine(folder, "stage.flac");
            File.WriteAllText(path, "old audio"); File.WriteAllText(path + AudioVerification.ReceiptExtension, "old receipt"); File.WriteAllText(staged, "new audio");
            using (var locked = new FileStream(path + AudioVerification.ReceiptExtension, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => VerifiedAudioCommit.Commit(staged, "new receipt", path, true, default(CancellationToken)));
            Check(File.ReadAllText(path) == "old audio" && File.ReadAllText(path + AudioVerification.ReceiptExtension) == "old receipt" && File.Exists(staged));
            Check(!Directory.GetFiles(folder, ".qbdlx-*", SearchOption.AllDirectories).Any());
        });
        add("actual MP3 queue rejects damaged middle audio then downloads the next song", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var album = Album("mp3-middle", 2); byte[] damaged = File.ReadAllBytes(Fixture(".mp3")); Array.Clear(damaged, damaged.Length / 2, 8192);
                using (var client = new HttpClient(new Handler((request, token) =>
                {
                    if (request.RequestUri.AbsolutePath.EndsWith("/track/getFileUrl")) return Task.FromResult(Json(Url(Id(request), true)));
                    int id = int.Parse(request.RequestUri.AbsolutePath.Trim('/'));
                    return Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(id == 1 ? damaged : File.ReadAllBytes(Fixture(".mp3")))));
                })))
                using (var stats = new DownloadStats())
                {
                    string folder = Path.Combine(temporary, "mp3-middle");
                    Pump(Task.Run(async () =>
                    {
                        var download = new DownloadTrack(client);
                        for (int id = 1; id <= 2; id++) await download.DownloadTrackAsync("track", "app", album.Id, "5", ".mp3", "auth", "secret", folder, "artist", "%AlbumTitle%", "%TrackTitle%", album, Detail(id, album), null, stats, default(CancellationToken));
                    }));
                    Check(stats.Failed == 1 && stats.Succeeded == 1 && stats.Failures[0].StartsWith("1:"));
                    Check(Directory.GetFiles(folder, "*.mp3", SearchOption.AllDirectories).Length == 1);
                }
            }
        });
        add("stop cancels an actual album queue without counting interrupted songs as failures", () =>
        {
            using (new SavedSettings())
            {
                Configure(); var album = Album("cancel-queue", 3); int requests = 0;
                using (var cancellation = new CancellationTokenSource(100))
                using (var client = new HttpClient(new Handler(async (request, token) =>
                { requests++; await Task.Delay(Timeout.Infinite, token); return Json(new { }); })))
                using (var stats = new DownloadStats())
                {
                    Reject<OperationCanceledException>(() => Pump(Task.Run(() => new DownloadAlbum(client).DownloadAlbumAsync("app", album.Id, "27", ".flac", "auth", "secret", temporary, "artist", "album", "track", album, null, null, stats, cancellation.Token))));
                    Check(requests == 1 && stats.Succeeded == 0 && stats.Failed == 0 && !qbdlxForm.skipCurrentAlbum);
                }
            }
        });
        add("slow search thumbnails keep the Windows message loop responsive and cancel cleanly", () =>
        {
            using (var picture = new PictureBox())
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient(new Handler(async (request, token) => { await Task.Delay(Timeout.Infinite, token); return Json(new { }); })))
            {
                getForm().Controls.Add(picture); _ = picture.Handle; bool tick = false;
                using (var timer = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    timer.Tick += (s, e) => { tick = true; cancellation.Cancel(); }; timer.Start();
                    Pump(SearchPanelHelper.LoadThumbnailAsync(picture, "https://cover.invalid/slow.jpg", cancellation.Token, client));
                    Check(tick && picture.Image == null);
                }
            }
        });
        add("search thumbnail bitmaps survive disposal of the network stream", () =>
        {
            byte[] jpeg; using (var image = new Bitmap(8, 8)) using (var buffer = new MemoryStream()) { image.Save(buffer, ImageFormat.Jpeg); jpeg = buffer.ToArray(); }
            using (var picture = new PictureBox())
            using (var client = new HttpClient(new Handler((r, t) => Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(jpeg))))))
            {
                getForm().Controls.Add(picture); _ = picture.Handle;
                Pump(Task.Run(() => SearchPanelHelper.LoadThumbnailAsync(picture, "https://cover.invalid/image.jpg", default(CancellationToken), client)));
                Check(picture.Image != null && picture.Image.Width == 8); picture.Image.Dispose(); picture.Image = null;
            }
        });
        add("Windows settings writes preserve the previous file when it is locked", () =>
        {
            string path = Path.Combine(temporary, "locked.config"); var store = new SettingsStore(path);
            store.Update(new Dictionary<string, string> { ["quality"] = "27" });
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject<IOException>(() => store.Update(new Dictionary<string, string> { ["quality"] = "5" }));
            Check(store.Read()["quality"] == "27");
        });
        add("actual settings provider merges dirty properties and ignores the current directory", () =>
        {
            string directory = Path.Combine(temporary, "provider"); var provider = new FlexibleSettingsProvider(directory); provider.Initialize("test", null);
            var properties = new SettingsPropertyCollection(); var a = new SettingsProperty("quality") { PropertyType = typeof(string), DefaultValue = "27", Provider = provider, SerializeAs = SettingsSerializeAs.String };
            var b = new SettingsProperty("folder") { PropertyType = typeof(string), DefaultValue = "", Provider = provider, SerializeAs = SettingsSerializeAs.String }; properties.Add(a); properties.Add(b);
            var first = provider.GetPropertyValues(new SettingsContext(), properties); var second = provider.GetPropertyValues(new SettingsContext(), properties);
            first["quality"].PropertyValue = "5"; provider.SetPropertyValues(new SettingsContext(), first);
            second["folder"].PropertyValue = "music"; provider.SetPropertyValues(new SettingsContext(), second);
            var stored = provider.GetPropertyValues(new SettingsContext(), properties);
            Check(stored["quality"].PropertyValue.ToString() == "5" && stored["folder"].PropertyValue.ToString() == "music");
            Check(provider.ConfigFileFullName == Path.Combine(directory, "user.config"));
        });
        foreach (string kind in new[] { "playlist", "artist", "label", "favorite-albums", "favorite-tracks" })
        {
            string collectionKind = kind;
            add("actual " + kind + " failures report the item ID rather than the collection ID", () =>
            {
                using (new SavedSettings())
                {
                    Configure(); Settings.Default.downloadAllFromArtist = true;
                    var previous = getForm(); qbdlxForm window = null;
                    using (var client = new HttpClient(new Handler((request, token) =>
                    {
                        string endpoint = request.RequestUri.AbsolutePath;
                        object[] items = { new { id = 71, title = "Failed item", position = 1 }, new { id = 72, title = "Failed item", position = 2 } };
                        var list = new { total = 2, items };
                        if (endpoint.EndsWith("/playlist/get")) return Task.FromResult(Json(new { id = 999, name = "test", owner = new { name = "tester" }, tracks = list }));
                        if (endpoint.EndsWith("/artist/get") || endpoint.EndsWith("/label/get")) return Task.FromResult(Json(new { id = 999, albums = list }));
                        if (endpoint.EndsWith("/favorite/getUserFavorites")) return Task.FromResult(Json(new { albums = list, tracks = list }));
                        return Task.FromResult(Response(HttpStatusCode.NotFound, new StringContent("missing metadata")));
                    })))
                    using (var stats = new DownloadStats())
                    {
                        try
                        {
                            window = new qbdlxForm(client); window.languageManager = previous.languageManager;
                            window.downloadLocation = Path.Combine(temporary, collectionKind); Directory.CreateDirectory(window.downloadLocation);
                            window.app_id = "app"; window.user_auth_token = "auth"; window.user_id = "1";
                            _ = window.Handle; _ = window.downloadOutput.Handle;
                            string url = collectionKind.StartsWith("favorite-") ? "https://play.qobuz.com/user/library/" + collectionKind.Substring(9) : "HTTP://PLAY.QOBUZ.COM/" + collectionKind.ToUpperInvariant() + "/999?source=test";
                            window.inputTextBox.Text = url;
                            Pump(Miscellaneous.downloadButtonAsyncWork(window, stats));
                            Check(stats.Failed == 2 && stats.Succeeded == 0, string.Join(" | ", stats.Failures));
                            Check(stats.Failures.Any(f => f.StartsWith("71:")) && stats.Failures.Any(f => f.StartsWith("72:")), string.Join(" | ", stats.Failures));
                        }
                        finally { window?.logger.Dispose(); window?.Dispose(); qbdlxForm._qbdlxForm = previous; }
                    }
                }
            });
        }
        add("unsupported URLs count as failed downloads in the actual button flow", () =>
        {
            var form = getForm(); string previous = form.inputTextBox.Text;
            using (var stats = new DownloadStats())
            {
                try
                {
                    form.downloadLocation = temporary; form.inputTextBox.Text = "https://play.qobuz.com/search/123";
                    Pump(Miscellaneous.downloadButtonAsyncWork(form, stats)); Check(stats.Failed == 1 && stats.Succeeded == 0);
                }
                finally { form.inputTextBox.Text = previous; }
            }
        });
    }
    private static void Configure()
    {
        Settings.Default.mergeArtistNames = true; Settings.Default.imageTag = false;
        Settings.Default.dontSaveArtworkToDisk = true; Settings.Default.downloadGoodies = false;
        Settings.Default.streamableCheck = true; qbdlxForm.duplicateFileMode = DuplicateFileMode.AutoRename;
        qbdlxForm.skipCurrentAlbum = false;
    }
    private static HttpClient SuccessfulClient(Func<Album> album, Func<byte[]> cover = null) => new HttpClient(new Handler((request, token) =>
    {
        if (request.RequestUri.Host == "cover.invalid") return Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(cover())));
        if (request.RequestUri.AbsolutePath.EndsWith("/track/get")) return Task.FromResult(Json(Detail(Id(request), album())));
        if (request.RequestUri.AbsolutePath.EndsWith("/track/getFileUrl")) return Task.FromResult(Json(Url(Id(request))));
        return Task.FromResult(Response(HttpStatusCode.OK, new ByteArrayContent(File.ReadAllBytes(Fixture(".flac")))));
    }));
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) { this.run = run; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => run(request, token);
    }
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> report;
        internal DirectProgress(Action<T> report) { this.report = report; }
        public void Report(T value) => report(value);
    }
    private sealed class SavedSettings : IDisposable
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>();
        private readonly DuplicateFileMode mode = qbdlxForm.duplicateFileMode;
        internal SavedSettings() { foreach (SettingsProperty property in Settings.Default.Properties) values[property.Name] = Settings.Default[property.Name]; }
        public void Dispose() { foreach (var pair in values) Settings.Default[pair.Key] = pair.Value; qbdlxForm.duplicateFileMode = mode; qbdlxForm.skipCurrentAlbum = false; }
    }
}
