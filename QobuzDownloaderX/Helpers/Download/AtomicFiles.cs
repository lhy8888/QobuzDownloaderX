using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal static class AtomicFiles
    {
        internal static string LockName(string path)
        {
            using (var hash = SHA256.Create())
                return "QobuzDLX-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        }
        internal static void Acquire(Mutex mutex, CancellationToken token)
        {
            try
            {
                while (true) { token.ThrowIfCancellationRequested(); if (mutex.WaitOne(100)) return; }
            }
            catch (AbandonedMutexException) { } // The lock was acquired.
        }
        internal static async Task CopyAsync(string source, string destination, CancellationToken token)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
        }
        internal static void WriteText(string path, string contents)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(file, new UTF8Encoding(false), 4096, true)) { writer.Write(contents); writer.Flush(); }
                file.Flush(true);
            }
        }
        internal static bool TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }
    }
}
