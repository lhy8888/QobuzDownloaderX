using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    // One cache per queue, private to this process. Entries are published only after image validation.
    internal sealed class ArtworkCache : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "QobuzDownloaderX", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> entries = new Dictionary<string, string>();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        internal async Task<string> GetAsync(string key, Func<string, CancellationToken, Task> download, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (entries.TryGetValue(key, out string existing) && File.Exists(existing)) return existing;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    await download(path, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    entries[key] = path;
                    return path;
                }
                catch { AtomicFiles.TryDelete(path); throw; }
            }
            finally { gate.Release(); }
        }
        public void Dispose()
        {
            gate.Dispose();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
