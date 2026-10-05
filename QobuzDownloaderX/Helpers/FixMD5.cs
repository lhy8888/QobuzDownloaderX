using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class FixMD5
    {
        private readonly string tool;
        internal FixMD5(string tool = "flac") { this.tool = tool; }
        internal async Task ValidateAsync(string path, bool repairMissingMd5, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Path.GetFullPath(path).Length >= 260)
            {
                // The .NET application accepts long paths, but the official
                // Windows CLI still uses CRT filename handling with MAX_PATH.
                // Check a unique short copy; replace the original only when an
                // unset MD5 was repaired and the repaired copy passed validation.
                string staged = Path.Combine(Path.GetTempPath(), "qbdlx-flac-" + Guid.NewGuid().ToString("N") + ".flac");
                if (Path.GetFullPath(staged).Length >= 260)
                    throw new IOException("FLAC verification requires a shorter writable temporary directory for this long path.");
                string replacement = null;
                try
                {
                    await CopyAsync(path, staged, token).ConfigureAwait(false);
                    bool repairNeeded = repairMissingMd5 && HasUnsetMd5(staged);
                    await ValidateAsync(staged, repairMissingMd5, token).ConfigureAwait(false);
                    if (repairNeeded)
                    {
                        replacement = Path.Combine(Path.GetDirectoryName(path), ".qbdlx-md5-" + Guid.NewGuid().ToString("N") + ".flac");
                        await CopyAsync(staged, replacement, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        File.Replace(replacement, path, null);
                    }
                }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                    if (replacement != null && File.Exists(replacement)) File.Delete(replacement);
                }
                return;
            }
            await RunAsync("-t -- \"" + path + "\"", token).ConfigureAwait(false);
            // Re-encode only files with an unset STREAMINFO MD5, never every FLAC.
            if (repairMissingMd5 && HasUnsetMd5(path))
            {
                await RunAsync("-f -8 -- \"" + path + "\"", token).ConfigureAwait(false);
                await RunAsync("-t -- \"" + path + "\"", token).ConfigureAwait(false);
                if (HasUnsetMd5(path)) throw new InvalidDataException("FLAC MD5 repair did not complete.");
            }
        }
        private static async Task CopyAsync(string source, string destination, CancellationToken token)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
        }
        private static bool HasUnsetMd5(string path)
        {
            using (var file = File.OpenRead(path))
            {
                byte[] header = new byte[42];
                if (file.Read(header, 0, header.Length) != header.Length || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C' ||
                    (header[4] & 127) != 0 || header[5] != 0 || header[6] != 0 || header[7] != 34)
                    throw new InvalidDataException("Invalid FLAC STREAMINFO header.");
                for (int i = 26; i < 42; i++) if (header[i] != 0) return false;
                return true;
            }
        }
        private async Task RunAsync(string arguments, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = new Process())
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                process.StartInfo = new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true };
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) => exited.TrySetResult(true);
                try { process.Start(); }
                catch (Win32Exception) { throw new IOException("FLAC verification requires flac.exe on PATH; install the official FLAC tool before downloading."); }
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                using (deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { } }))
                {
                    await exited.Task.ConfigureAwait(false);
                    await Task.WhenAll(error, output).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidDataException("FLAC verification/repair failed (exit " + process.ExitCode + ").");
                }
            }
        }
    }
}
