using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QobuzDownloaderX;
using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;
using ZetaLongPaths;
using Image = System.Drawing.Image;

internal static class Program
{
    private static readonly List<Tuple<string, Action>> tests = new List<Tuple<string, Action>>();
    private static readonly string temporary = Path.Combine(Path.GetTempPath(), "qbdlx-windows-" + Guid.NewGuid().ToString("N"));
    private static qbdlxForm form;
    private static string Fixture(string ext) => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "tone" + ext);
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Add(string name, Action run) => tests.Add(Tuple.Create(name, run));
    private static Item Track(int id = 1) => new Item { Id = id, Title = "测试歌曲 " + id, Duration = 1, TrackNumber = 1, MediaNumber = 1, MaximumBitDepth = 16, MaximumSamplingRate = 44.1 };
    private static Album Album(string id = "album-one") => new Album { Id = id, Title = "同名专辑", TracksCount = 1, MediaCount = 1 };
    private static readonly AudioQuality quality = new AudioQuality { IsFlac = true, BitDepth = 16, SampleRate = 44100 };
    private static string CopyFixture(string name, string ext = ".flac")
    { string path = Path.Combine(temporary, name + ext); File.Copy(Fixture(ext), path); return path; }
    private static void Validate(string path) => new FixMD5(Environment.GetEnvironmentVariable("QBDLX_TEST_FLAC")).ValidateAsync(path, false, CancellationToken.None).GetAwaiter().GetResult();

    [STAThread]
    private static int Main()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.Error.WriteLine("WindowsChecks requires Windows."); return 1; }
        if (!File.Exists(Environment.GetEnvironmentVariable("QBDLX_TEST_FLAC"))) { Console.Error.WriteLine("The official FLAC decoder is required."); return 1; }
        Directory.CreateDirectory(temporary);
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = temporary;
        var report = new CheckReport();
        Add("packaged runtime dependencies load", () =>
        {
            foreach (string name in new[] { "System.Resources.Extensions", "System.Formats.Nrbf", "System.Reflection.Metadata", "System.Collections.Immutable", "System.Memory", "System.Buffers", "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors", "Microsoft.Bcl.HashCode" })
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".dll");
                Check(File.Exists(path), "Missing runtime dependency " + name);
                Check(System.Reflection.Assembly.LoadFrom(path).GetName().Name == name);
            }
        });
        Add("embedded window images and icons deserialize on .NET Framework", () =>
        {
            var assembly = typeof(qbdlxForm).Assembly; int count = 0;
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".resources")))
            {
                var manager = new ResourceManager(name.Substring(0, name.Length - ".resources".Length), assembly);
                using (var resources = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true))
                {
                    Check(resources != null);
                    foreach (DictionaryEntry item in resources)
                    { object value = item.Value; if (value is Image image) { Check(image.Width > 0 && image.Height > 0); count++; } else if (value is Icon icon) { Check(icon.Width > 0); count++; } }
                }
            }
            Check(count > 0, "No window graphics loaded");
        });
        Add("login and main window controls construct without login/network", () =>
        {
            // LoginForm constructs and owns a main form field. Reuse that
            // instance so both forms exercise the application's real startup
            // construction without opening the same timestamped log twice.
            using (var login = new LoginForm())
            { Check(login.Controls.Count > 0); form = qbdlxForm._qbdlxForm; Check(form != null && form.Controls.Count > 0); }
            form.languageManager = new LanguageManager();
            form.languageManager.LoadLanguage(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "languages", "en.json"));
        });
        Add("Windows user protection encrypts and restores credentials", () =>
        {
            const string secret = "synthetic test credential";
            string encrypted = CredentialProtection.Encrypt(secret, data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));
            Check(encrypted.Length > 0 && encrypted != secret);
            Check(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser)) == secret);
            Check(CredentialProtection.Encrypt(secret, data => { throw new CryptographicException(); }) == "");
        });
        Add("missing optional album artists and genre do not break FLAC tags", () =>
        {
            string path = CopyFixture("optional-fields"); var track = Track();
            TagFile.WriteToFile(path, null, Album(), track); AudioVerification.Inspect(path, track, quality); Validate(path);
            using (var file = TagLib.File.Create(path)) Check(file.Tag.Title == track.Title && file.Tag.Album == "同名专辑");
        });
        Add("MP3 tags tolerate an invalid optional release year", () =>
        {
            string path = CopyFixture("mp3-tags", ".mp3"); var album = Album(); album.ReleaseDateOriginal = "unknown";
            TagFile.WriteToFile(path, null, album, Track());
            AudioVerification.Inspect(path, Track(), new AudioQuality { IsFlac = false, SampleRate = 44100 });
            using (var file = TagLib.File.Create(path)) Check(file.Tag.Title == Track().Title);
        });
        Add("embedded cover survives saving and does not change decoded audio", () =>
        {
            string cover = Path.Combine(temporary, "cover.jpg");
            using (var image = new Bitmap(16, 16)) { using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Blue); image.Save(cover, ImageFormat.Jpeg); }
            string path = CopyFixture("with-cover"); byte[] before = File.ReadAllBytes(path).Skip(26).Take(16).ToArray();
            TagFile.WriteToFile(path, cover, Album(), Track()); Validate(path);
            Check(before.SequenceEqual(File.ReadAllBytes(path).Skip(26).Take(16)), "Decoded audio MD5 changed");
            using (var file = TagLib.File.Create(path))
            { Check(file.Tag.Pictures.Length == 1); using (var stream = new MemoryStream(file.Tag.Pictures[0].Data.Data)) using (var image = Image.FromStream(stream)) Check(image.Width == 16 && image.Height == 16); }
        });
        Add("same album titles with different album IDs stay in different directories", () =>
        {
            using (var download = new DownloadFile())
            {
                string a = download.createPath(temporary, "artist", "%AlbumTitle%", "%TrackTitle%", "", "", 2, 2, Album("one"), Track(), null, "27", quality).GetAwaiter().GetResult();
                string b = download.createPath(temporary, "artist", "%AlbumTitle%", "%TrackTitle%", "", "", 2, 2, Album("two"), Track(), null, "27", quality).GetAwaiter().GetResult();
                Check(a != b && a.Contains("[IDone]") && b.Contains("[IDtwo]"));
                Check(DownloadPaths.SafePath(temporary, a) == Path.GetFullPath(a));
            }
        });
        Add("actual Windows file moves preserve existing files and verified receipts", () =>
        {
            string source = CopyFixture("move-source"); string destination = CopyFixture("move-destination");
            byte[] before = File.ReadAllBytes(destination); bool rejected = false;
            try { ZlpIOHelper.MoveFile(source, destination, overwriteExisting: false); } catch { rejected = true; }
            Check(rejected && File.Exists(source) && before.SequenceEqual(File.ReadAllBytes(destination)));
            string replacement = Miscellaneous.GetDuplicateFileName(destination); Check(replacement != destination);
            ZlpIOHelper.MoveFile(source, replacement, overwriteExisting: false); Check(!File.Exists(source));
            AudioVerification.SaveReceipt(replacement, Track(), "27", quality);
            AudioVerification.SaveReceipt(replacement, Track(), "27", quality);
            Check(AudioVerification.CanSkipAnyVerifiedCopy(destination, Track(), "27", quality));
        });
        Add("a locked Windows destination cannot be reported as a successful write", () =>
        {
            string path = CopyFixture("locked");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            { Reject<IOException>(() => File.WriteAllBytes(path, new byte[] { 1 })); Check(!AudioVerification.CanSkip(path, Track(), "27", quality)); }
            Validate(path);
        });
        Add("100 sequential tag writes preserve titles, album IDs, covers and audio", () =>
        {
            var paths = new HashSet<string>();
            for (int i = 1; i <= 100; i++)
            {
                var track = Track(i); var album = Album("album-" + i); album.Title += " " + i;
                string path = AudioVerification.IdentityPath(Path.Combine(temporary, "same name.flac"), track, "27", quality);
                Check(paths.Add(path)); File.Copy(Fixture(".flac"), path);
                TagFile.WriteToFile(path, i == 1 ? Path.Combine(temporary, "cover.jpg") : null, album, track); Validate(path);
                AudioVerification.SaveReceipt(path, track, "27", quality);
                using (var file = TagLib.File.Create(path))
                { Check(file.Tag.Title == track.Title && file.Tag.Album == album.Title); Check(file.Tag.Pictures.Length == (i == 1 ? 1 : 0), "Cover leaked to next song"); }
                Check(AudioVerification.CanSkip(path, track, "27", quality));
                Check(!AudioVerification.CanSkip(path, Track(i + 1), "27", quality));
            }
            Check(paths.Count == 100 && !Directory.EnumerateFiles(temporary, "*.tmp").Any());
        });
        Add("buffered log files do not retain credentials or signed URLs", () =>
        {
            string path = Path.Combine(temporary, "synthetic.log");
            using (var logger = new BufferedLogger(path)) logger.Error("password=synthetic-password https://audio.invalid/private?signature=synthetic-signature");
            string text = File.ReadAllText(path); Check(!text.Contains("synthetic-password") && !text.Contains("synthetic-signature") && !text.Contains("/private"));
        });
        Add("older language files receive usable download result messages", () =>
        {
            string path = Path.Combine(temporary, "old-language.json"); File.WriteAllText(path, "{}");
            var language = new LanguageManager(); language.LoadLanguage(path);
            string summary = language.GetTranslation("downloadResultSummary");
            Check(summary.Contains("{0}") && summary.Contains("{1}") && summary.Contains("{2}"));
            Check(language.GetTranslation("processed") == "processed");
        });
        int failed = 0;
        try
        {
            foreach (var test in tests)
            {
                var clock = Stopwatch.StartNew(); Exception error = null;
                try { test.Item2(); Console.WriteLine("PASS " + test.Item1); }
                catch (Exception ex) { error = ex; failed++; Console.WriteLine("FAIL " + test.Item1 + ": " + ex); }
                report.Add(test.Item1, clock.Elapsed, error);
            }
            report.Save("Windows desktop integration checks");
            Console.WriteLine((tests.Count - failed) + " passed, " + failed + " failed, " + tests.Count + " executed. No authenticated Qobuz downloads.");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            form?.logger?.Dispose(); form?.Dispose();
            Environment.CurrentDirectory = previous; Directory.Delete(temporary, true);
        }
    }
}
