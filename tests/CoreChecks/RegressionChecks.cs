using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QobuzDownloaderX;
using QobuzDownloaderX.Helpers;
using QopenAPI;

internal static class RegressionChecks
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Regression assertion failed"); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    internal static void Register(Action<string, Func<Task>> add, string temporary, Func<string, string> fixture)
    {
        add("full MP3 decoder accepts original and tagged audio without rewriting it", async () =>
        {
            string p = Path.Combine(temporary, "full.mp3"); File.Copy(fixture(".mp3"), p);
            await Mp3Verification.ValidateAsync(p, default);
            using (var tag = TagLib.File.Create(p)) { tag.Tag.Title = "验证音频"; tag.Tag.Performers = new[] { "歌手" }; tag.Save(); }
            byte[] before = File.ReadAllBytes(p);
            await Mp3Verification.ValidateAsync(p, default); Check(before.SequenceEqual(File.ReadAllBytes(p)));
        });
        add("middle MP3 corruption formerly accepted by metadata inspection is rejected", async () =>
        {
            byte[] bytes = File.ReadAllBytes(fixture(".mp3")); Array.Clear(bytes, bytes.Length / 2, Math.Min(8192, bytes.Length / 4));
            string p = Path.Combine(temporary, "middle.mp3"); File.WriteAllBytes(p, bytes);
            AudioVerification.Inspect(p, new Item { Id = 1, Duration = 1 }, new AudioQuality { SampleRate = 44100 });
            await Reject<InvalidDataException>(() => Mp3Verification.ValidateAsync(p, default));
        });
        add("truncated MP3 frames and trailing junk cannot pass whole-file checks", async () =>
        {
            byte[] bytes = File.ReadAllBytes(fixture(".mp3")); string p = Path.Combine(temporary, "partial.mp3");
            File.WriteAllBytes(p, bytes.Take(bytes.Length - 17).ToArray());
            await Reject<InvalidDataException>(() => Mp3Verification.ValidateAsync(p, default));
            File.WriteAllBytes(p, bytes.Concat(new byte[] { 0, 1, 2, 3 }).ToArray());
            await Reject<InvalidDataException>(() => Mp3Verification.ValidateAsync(p, default));
        });
        add("MP3 whole-file checks honor stop requests", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Reject<OperationCanceledException>(() => Mp3Verification.ValidateAsync(fixture(".mp3"), cancellation.Token));
        });
        add("old MP3 receipts cannot bless middle corruption", async () =>
        {
            string p = Path.Combine(temporary, "old-mp3.mp3"); byte[] bytes = File.ReadAllBytes(fixture(".mp3"));
            Array.Clear(bytes, bytes.Length / 2, 1024); File.WriteAllBytes(p, bytes);
            var track = new Item { Id = 1, Duration = 1 }; var quality = new AudioQuality { SampleRate = 44100 };
            await AudioVerification.SaveReceiptAsync(p, track, "5", quality, default);
            var receipt = JsonConvert.DeserializeObject<AudioReceipt>(File.ReadAllText(p + AudioVerification.ReceiptExtension));
            receipt.Version = 0; File.WriteAllText(p + AudioVerification.ReceiptExtension, JsonConvert.SerializeObject(receipt));
            Check(!await AudioVerification.CanSkipAsync(p, track, "5", quality, default));
        });
        add("stop interrupts hashing rather than waiting for the entire file", async () =>
        {
            using var cancellation = new CancellationTokenSource(50);
            using var stream = new SlowRead();
            await Reject<OperationCanceledException>(() => AudioVerification.HashAsync(stream, cancellation.Token));
            Check(stream.Reads <= 1);
        });
        add("stop while verifying an existing file propagates to its queue", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Reject<OperationCanceledException>(() => AudioVerification.CanSkipAnyVerifiedCopyAsync(fixture(".flac"), new Item { Id = 1 }, "27", new AudioQuality(), cancellation.Token));
        });
        add("receipt destination failure leaves the old audio intact", async () =>
        {
            string p = Path.Combine(temporary, "commit-old.flac"), staged = Path.Combine(temporary, "commit-stage.flac");
            File.WriteAllText(p, "original"); File.WriteAllText(staged, "replacement"); Directory.CreateDirectory(p + AudioVerification.ReceiptExtension);
            await Reject<IOException>(() => Task.Run(() => VerifiedAudioCommit.Commit(staged, "{}", p, true, default)));
            Check(File.ReadAllText(p) == "original" && File.ReadAllText(staged) == "replacement");
            Check(!Directory.EnumerateFiles(temporary, ".qbdlx-receipt-*.tmp").Any());
        });
        add("cancel before final save preserves audio and verification records", async () =>
        {
            string p = Path.Combine(temporary, "commit-cancel.flac"), staged = Path.Combine(temporary, "cancel-stage.flac");
            File.WriteAllText(p, "old"); File.WriteAllText(p + AudioVerification.ReceiptExtension, "old receipt"); File.WriteAllText(staged, "new");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Reject<OperationCanceledException>(() => Task.Run(() => VerifiedAudioCommit.Commit(staged, "new receipt", p, true, cancellation.Token)));
            Check(File.ReadAllText(p) == "old" && File.ReadAllText(p + AudioVerification.ReceiptExtension) == "old receipt");
        });
        add("overlapping saves allocate distinct audio-record pairs", async () =>
        {
            string folder = Path.Combine(temporary, "overlap"); Directory.CreateDirectory(folder);
            var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(n => Task.Run(() =>
            {
                string staged = Path.Combine(folder, "stage-" + n); File.WriteAllText(staged, n.ToString());
                return VerifiedAudioCommit.Commit(staged, n.ToString(), Path.Combine(folder, "same.mp3"), false, default);
            })));
            Check(results.Distinct().Count() == 20);
            foreach (string result in results) Check(File.ReadAllText(result) == File.ReadAllText(result + AudioVerification.ReceiptExtension));
        });
        add("a queue reuses one cover download across many tracks", async () =>
        {
            using var cache = new ArtworkCache(); int calls = 0;
            async Task Download(string p, CancellationToken t) { Interlocked.Increment(ref calls); await Task.Delay(10, t); File.WriteAllText(p, "image"); }
            string[] paths = await Task.WhenAll(Enumerable.Range(1, 20).Select(_ => cache.GetAsync("same album and size", Download, default)));
            Check(calls == 1 && paths.Distinct().Count() == 1 && File.Exists(paths[0]));
        });
        add("failed cover attempts are retried and caches are isolated between queues", async () =>
        {
            using var a = new ArtworkCache(); using var b = new ArtworkCache();
            await Reject<IOException>(() => a.GetAsync("cover", (p, t) => throw new IOException(), default));
            Task Download(string p, CancellationToken t) { File.WriteAllText(p, "ok"); return Task.CompletedTask; }
            string first = await a.GetAsync("cover", Download, default), second = await b.GetAsync("cover", Download, default);
            Check(first != second); a.Dispose(); Check(!File.Exists(first) && File.Exists(second));
        });
        add("manual skipping is separate from verified existing-file skipping", () =>
        {
            using var stats = new DownloadStats(); stats.Skip(); stats.SkipByUser(3);
            Check(stats.Skipped == 1 && stats.SkippedByUser == 3 && stats.Failed == 0); return Task.CompletedTask;
        });
        add("HTTP uppercase and query-bearing Qobuz links identify the same song", () =>
        {
            foreach (string url in new[] { "http://PLAY.QOBUZ.COM/TRACK/123?ref=album#song", "https://play.qobuz.com/track/123/" })
            { var link = QobuzLink.Parse(url); Check(link.Type == "track" && link.Id == "123"); }
            var album = QobuzLink.Parse("HTTPS://WWW.QOBUZ.COM/GB-EN/ALBUM/title/Abc123?utm=1"); Check(album.Type == "album" && album.Id == "Abc123");
            var artist = QobuzLink.Parse("https://play.qobuz.com/ARTIST/12/RELEASES?sort=new"); Check(artist.Type == "artist" && artist.Id == "12");
            return Task.CompletedTask;
        });
        add("favorites links accept queries without mixing types", () =>
        {
            var link = QobuzLink.Parse("https://play.qobuz.com/user/library/TRACKS?filter=new"); Check(link.Type == "user" && link.Id == "library/tracks");
            return Task.CompletedTask;
        });
        add("unsupported and spoofed links fail explicitly", async () =>
        {
            foreach (string url in new[] { "https://qobuz.com.evil.invalid/track/123", "https://play.qobuz.com/search/123", "https://play.qobuz.com/track/123%2f456", "https://evil@play.qobuz.com/track/123", "https://play.qobuz.com/track/" })
                await Reject<InvalidDataException>(() => Task.Run(() => QobuzLink.Parse(url)));
        });
        add("settings migration uses the old program file and survives directory changes", () =>
        {
            string old = Path.Combine(temporary, "old-user.config"), path = Path.Combine(temporary, "fixed", "user.config");
            File.WriteAllText(old, "<settings><folder>music</folder></settings>"); var store = new SettingsStore(path, old);
            Check(store.Read()["folder"] == "music"); store.Update(new Dictionary<string, string> { ["quality"] = "27" });
            Check(store.Read()["folder"] == "music" && store.Read()["quality"] == "27"); Check(File.ReadAllText(old).Contains("music"));
            return Task.CompletedTask;
        });
        add("simultaneous settings changes merge instead of erasing other changes", async () =>
        {
            string path = Path.Combine(temporary, "merged", "user.config");
            await Task.WhenAll(Enumerable.Range(1, 20).Select(n => Task.Run(() => new SettingsStore(path).Update(new Dictionary<string, string> { ["setting" + n] = n.ToString() }))));
            Check(new SettingsStore(path).Read().Count == 20); Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.tmp").Any());
        });
        add("damaged settings recover from a good backup without overwriting that backup", () =>
        {
            string path = Path.Combine(temporary, "recover.config"); var store = new SettingsStore(path);
            store.Update(new Dictionary<string, string> { ["quality"] = "27" }); store.Update(new Dictionary<string, string> { ["folder"] = "music" });
            File.WriteAllText(path, "<broken"); Check(store.Read()["quality"] == "27");
            store.Update(new Dictionary<string, string> { ["folder"] = "restored" }); Check(store.Read()["folder"] == "restored");
            Check(File.ReadAllText(path + ".bak").Contains("27") && Directory.EnumerateFiles(temporary, "recover.config.damaged-*").Any());
            return Task.CompletedTask;
        });
        add("settings without a recovery copy report corruption explicitly", async () =>
        {
            string path = Path.Combine(temporary, "bad.config"); File.WriteAllText(path, "<bad");
            await Reject<InvalidDataException>(() => Task.Run(() => new SettingsStore(path).Read())); Check(File.ReadAllText(path) == "<bad");
        });
    }
    private sealed class SlowRead : MemoryStream
    {
        internal int Reads;
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { Reads++; await Task.Delay(2000, token); return 1; }
    }
}
