using System;
using System.IO;

namespace QobuzDownloaderX.Helpers
{
    internal static class AppPaths
    {
        private static readonly Lazy<string> directory = new Lazy<string>(ChooseDirectory);
        internal static string TestDataDirectory { get; set; }
        internal static string DataDirectory => TestDataDirectory ?? directory.Value;
        internal static string LogDirectory => Path.Combine(DataDirectory, "logs");
        private static string ChooseDirectory()
        {
            string preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QobuzDownloaderX");
            try { CheckWritable(preferred); return preferred; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Select the fallback once, so reads and writes always use the same location.
                string fallback = Path.Combine(Path.GetTempPath(), "QobuzDownloaderX-" + Environment.UserName);
                CheckWritable(fallback); return fallback;
            }
        }
        private static void CheckWritable(string path)
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, ".write-" + Guid.NewGuid().ToString("N"));
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }
}
