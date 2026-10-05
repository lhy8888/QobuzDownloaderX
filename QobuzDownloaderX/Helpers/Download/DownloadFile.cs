using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;
using System;
using System.Diagnostics;
using System.Drawing;
using Image = System.Drawing.Image;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZetaLongPaths;

namespace QobuzDownloaderX
{
    internal sealed class DownloadFile : IDisposable
    {
        private readonly RenameTemplates renameTemplates = new RenameTemplates();
        private readonly HttpClient client;
        private readonly ArtworkCache artwork;
        private readonly bool ownsArtwork;
        internal DownloadFile(HttpClient client = null, ArtworkCache artwork = null)
        { this.client = client; this.artwork = artwork ?? new ArtworkCache(); ownsArtwork = artwork == null; }
        public string embeddedArtworkPath { get; private set; }

        public Task<string> createPath(string downloadLocation, string artistTemplate, string albumTemplate, string trackTemplate,
            string playlistTemplate, string favoritesTemplate, int paddedTrackLength, int paddedDiscLength,
            Album album, Item item, Playlist playlist, string formatId = null, AudioQuality quality = null)
        {
            string extension = formatId == "5" ? ".mp3" : ".flac";
            Func<string, string> convert = template => renameTemplates.renameTemplates(template, paddedTrackLength,
                paddedDiscLength, extension, album, playlist == null ? null : item, playlist, formatId, quality?.BitDepth, quality == null ? (double?)null : quality.SampleRate / 1000.0);
            string path;
            if (playlist == null)
            {
                string directory = convert(albumTemplate);
                string suffix = " [ID" + renameTemplates.GetSafeFilename(album.Id) + "]";
                string leaf = DownloadPaths.Truncate(Path.GetFileName(directory), suffix.Length) + suffix;
                path = Path.Combine(downloadLocation, convert(artistTemplate), Path.GetDirectoryName(directory) ?? "", leaf);
            }
            else path = Path.Combine(downloadLocation, convert(playlistTemplate));
            path = SafePath(downloadLocation, path);
            return Task.FromResult(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        }

        internal static string SafePath(string root, string path) => DownloadPaths.SafePath(root, path);

        public async Task DownloadStream(string downloadType, string streamUrl, string downloadPath, string filePath,
            string audioFormat, Album album, Item item, GetInfo getInfo, CancellationToken token, DownloadStats stats,
            AudioQuality quality, string requestedFormat)
        {
            ZlpIOHelper.CreateDirectory(Path.GetDirectoryName(filePath));
            // Same destination volume, unique per attempt, with the real extension for the audio parser.
            string temporary = Path.Combine(Path.GetDirectoryName(filePath), ".qbdlx-" + Guid.NewGuid().ToString("N") + audioFormat);
            long lastReceived = 0;
            long lastDisplay = 0;
            var displayWatch = Stopwatch.StartNew();
            try
            {
                if (stats?.SpeedWatch != null && !stats.SpeedWatch.IsRunning) stats.SpeedWatch.Start();
                await ReliableHttp.DownloadAsync(streamUrl, temporary,
                    TimeSpan.FromMinutes(quality.IsFlac ? 10 : 5), token, (received, total) =>
                    {
                        if (received < lastReceived) lastReceived = 0; // A retry starts from byte zero.
                        if (stats != null) stats.CumulativeBytesRead += received - lastReceived;
                        lastReceived = received;
                        long elapsed = stats?.SpeedWatch?.ElapsedMilliseconds ?? 0;
                        if (stats?.SpeedWatch != null && elapsed - stats.LastUiTimeMs >= 250)
                        {
                            double bytesPerSecond = (stats.CumulativeBytesRead - stats.LastUiBytes) * 1000.0 / (elapsed - stats.LastUiTimeMs);
                            stats.LastSpeedText = bytesPerSecond >= 1048576
                                ? (bytesPerSecond / 1048576).ToString("F2", CultureInfo.CurrentCulture) + " MB/s"
                                : (bytesPerSecond / 1024).ToString("F2", CultureInfo.CurrentCulture) + " KB/s";
                            stats.LastUiBytes = stats.CumulativeBytesRead;
                            stats.LastUiTimeMs = elapsed;
                        }
                        if (displayWatch.ElapsedMilliseconds - lastDisplay >= 250)
                        {
                            lastDisplay = displayWatch.ElapsedMilliseconds;
                            int percent = total > 0 ? (int)Math.Min(100, received * 100 / total) : 0;
                            qbdlxForm._qbdlxForm.BeginInvoke(new Action(() => qbdlxForm._qbdlxForm.progressLabel.Text =
                                qbdlxForm._qbdlxForm.progressLabelActive + " - " + percent + "%" + (stats?.SpeedWatch == null ? "" : " [" + stats.LastSpeedText + "]")));
                        }
                    }, client).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                AudioVerification.Inspect(temporary, item, quality);
                TagFile.WriteToFile(temporary, embeddedArtworkPath, album, item);
                if (quality.IsFlac) await new FixMD5().ValidateAsync(temporary, Settings.Default.fixMD5s, token).ConfigureAwait(false);
                else await Mp3Verification.ValidateAsync(temporary, token).ConfigureAwait(false);
                AudioVerification.Inspect(temporary, item, quality);
                token.ThrowIfCancellationRequested();
                string json = await AudioVerification.CreateReceiptAsync(temporary, item, requestedFormat, quality, token).ConfigureAwait(false);
                VerifiedAudioCommit.Commit(temporary, json, filePath,
                    qbdlxForm.duplicateFileMode == DuplicateFileMode.OverwriteExistingFiles, token);
                stats?.Success();
                getInfo.updateDownloadOutput(" " + qbdlxForm._qbdlxForm.downloadOutputDone + "\r\n");
            }
            finally { if (ZlpIOHelper.FileExists(temporary)) ZlpIOHelper.DeleteFile(temporary); }
        }

        private async Task DownloadImage(string url, string path, CancellationToken token)
        {
            ZlpIOHelper.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await ReliableHttp.DownloadAsync(url, temporary, TimeSpan.FromMinutes(2), token, client: client).ConfigureAwait(false);
                using (var image = System.Drawing.Image.FromFile(temporary))
                    if (image.Width <= 0 || image.Height <= 0) throw new InvalidDataException("Invalid cover image.");
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { /* Another validated image won the race. */ }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private Task<string> CachedImage(Album album, string size, CancellationToken token)
        {
            string url = album.Image.Large.Replace("_600", "_" + size);
            string key = album.Id + "\n" + size + "\n" + url;
            return artwork.GetAsync(key, (path, cancellation) => DownloadImage(url, path, cancellation), token);
        }
        public async Task DownloadArtwork(string downloadPath, Album album, CancellationToken token = default(CancellationToken), bool separateCovers = false)
        {
            if (string.IsNullOrWhiteSpace(album?.Image?.Large)) return;
            if (!Settings.Default.dontSaveArtworkToDisk)
            {
                string cover = Path.Combine(downloadPath, separateCovers ? "Cover-" + renameTemplates.GetSafeFilename(album.Id) + ".jpg" : "Cover.jpg");
                bool valid = false;
                try { using (var image = Image.FromFile(cover)) valid = image.Width > 0 && image.Height > 0; }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OutOfMemoryException) { }
                if (!valid)
                {
                    string cached = await CachedImage(album, qbdlxForm._qbdlxForm.savedArtSize, token).ConfigureAwait(false);
                    Directory.CreateDirectory(downloadPath);
                    string temporary = Path.Combine(downloadPath, ".qbdlx-cover-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await AtomicFiles.CopyAsync(cached, temporary, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(cover)) File.Replace(temporary, cover, null);
                        else File.Move(temporary, cover);
                    }
                    finally { AtomicFiles.TryDelete(temporary); }
                }
            }
            if (Settings.Default.imageTag)
                embeddedArtworkPath = await CachedImage(album, qbdlxForm._qbdlxForm.embeddedArtSize, token).ConfigureAwait(false);
        }

        public async Task DownloadGoody(string downloadPath, Album album, Goody goody, GetInfo getInfo, CancellationToken token)
        {
            string extension = Path.GetExtension(new Uri(goody.Url).AbsolutePath);
            string destination = Path.Combine(downloadPath, renameTemplates.GetSafeFilename(goody.Description ?? album.Title) + " (" + goody.Id + ")" + extension);
            ZlpIOHelper.CreateDirectory(downloadPath);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await ReliableHttp.DownloadAsync(goody.Url, temporary, TimeSpan.FromMinutes(5), token, client: client).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ZlpIOHelper.MoveFile(temporary, destination, overwriteExisting: true);
            }
            finally { if (ZlpIOHelper.FileExists(temporary)) ZlpIOHelper.DeleteFile(temporary); }
        }
        public void Dispose() { if (ownsArtwork) artwork.Dispose(); }
    }
}
