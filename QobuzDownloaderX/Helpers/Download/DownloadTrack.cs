using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX
{
    internal sealed class DownloadTrack
    {
        internal string LastDirectory { get; private set; }
        readonly GetInfo getInfo = new GetInfo();
        public void clearOutputText() => getInfo.outputText = null;

        public Task DownloadTrackAsync(string downloadType, string appId, string albumId, string format, string extension,
            string auth, string secret, string root, string artistTemplate, string albumTemplate, string trackTemplate,
            Album album, Item item, IProgress<int> progress, DownloadStats stats, CancellationToken token) =>
            DownloadAsync(downloadType, appId, format, auth, secret, root, artistTemplate, albumTemplate, trackTemplate, null, album, item, null, progress, stats, token);

        public Task DownloadPlaylistTrackAsync(string downloadType, string appId, string format, string extension,
            string auth, string secret, string root, string trackTemplate, string playlistTemplate, Album album, Item item,
            Playlist playlist, IProgress<int> progress, DownloadStats stats, CancellationToken token) =>
            DownloadAsync(downloadType, appId, format, auth, secret, root, null, null, trackTemplate, playlistTemplate, album, item, playlist, progress, stats, token);

        private async Task DownloadAsync(string type, string appId, string format, string auth, string secret, string root,
            string artistTemplate, string albumTemplate, string trackTemplate, string playlistTemplate,
            Album album, Item item, Playlist playlist, IProgress<int> progress, DownloadStats stats, CancellationToken token)
        {
            string path = root;
            try
            {
                token.ThrowIfCancellationRequested();
                if (album == null || item?.Id == null) throw new InvalidDataException("Track or album information is missing.");
                if (!item.Streamable && Settings.Default.streamableCheck) throw new InvalidDataException("This track is not streamable for the account.");
                var service = new ReliableQobuzService(token);
                var stream = await service.TrackGetFileUrlAsync(item.Id.ToString(), format, appId, auth, secret).ConfigureAwait(false);
                var quality = AudioQuality.FromResponse(stream, item, format);
                string extension = quality.IsFlac ? ".flac" : ".mp3";
                var padding = new PaddingNumbers();
                int tracks = playlist == null ? padding.padTracks(album) : padding.padPlaylistTracks(playlist);
                int discs = playlist == null ? padding.padDiscs(album) : 2;
                using (var files = new DownloadFile())
                {
                    path = await files.createPath(root, artistTemplate, albumTemplate, trackTemplate, playlistTemplate, null,
                        tracks, discs, album, item, playlist, format, quality).ConfigureAwait(false);
                    LastDirectory = path;
                    var naming = new RenameTemplates();
                    string name = naming.renameTemplates(trackTemplate, tracks, discs, extension, album, item, playlist,
                        format, quality.BitDepth, quality.SampleRate / 1000.0);
                    if (playlist == null && type != "track" && album.MediaCount > 1 && !string.IsNullOrWhiteSpace(Settings.Default.savedCdTemplate))
                        path = Path.Combine(path, qbdlxForm.discNumberRegex.Replace(Settings.Default.savedCdTemplate, item.MediaNumber.ToString()));
                    string destination = DownloadFile.SafePath(root, Path.Combine(path, name.TrimEnd() + extension));
                    destination = AudioVerification.IdentityPath(destination, item, format, quality);
                    path = Path.GetDirectoryName(destination) + Path.DirectorySeparatorChar;
                    if (qbdlxForm.duplicateFileMode == DuplicateFileMode.SkipDownloads && AudioVerification.CanSkipAnyVerifiedCopy(destination, item, format, quality))
                    {
                        stats?.Skip();
                        getInfo.updateDownloadOutput(qbdlxForm._qbdlxForm.downloadOutputFileExists.Replace("{TrackNumber}", item.TrackNumber.ToString()) + "\r\n");
                        progress?.Report(100);
                        return;
                    }
                    try { await files.DownloadArtwork(path, album, token, separateCovers: playlist != null).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { qbdlxForm._qbdlxForm.logger.Warning("Cover artwork could not be downloaded; audio download will continue."); }
                    getInfo.updateDownloadOutput(qbdlxForm._qbdlxForm.downloadOutputDownloading + " - " + item.Id + " " + item.Title + "…");
                    // Refresh a rejected/expired signed URL once, without weakening identity/quality validation.
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await files.DownloadStream(type, stream.StreamURL, path, destination, extension, album, item,
                                getInfo, token, stats, quality, format).ConfigureAwait(false);
                            break;
                        }
                        catch (HttpStatusException ex) when (attempt == 0 && (ex.StatusCode == 401 || ex.StatusCode == 403))
                        {
                            stream = await service.TrackGetFileUrlAsync(item.Id.ToString(), format, appId, auth, secret).ConfigureAwait(false);
                            var refreshed = AudioQuality.FromResponse(stream, item, format);
                            if (refreshed.BitDepth != quality.BitDepth || refreshed.SampleRate != quality.SampleRate)
                                throw new InvalidDataException("Audio quality changed while refreshing the address.");
                        }
                    }
                    progress?.Report(100);
                }
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (OperationCanceledException) { RecordFailure(path, item, "The service request timed out.", stats); }
            catch (Exception ex)
            {
                // Network library exceptions can contain signed addresses. Do not persist their raw messages.
                string message = SensitiveLog.Redact(ex.Message);
                RecordFailure(path, item, message, stats);
            }
        }
        private void RecordFailure(string path, Item item, string message, DownloadStats stats)
        {
            stats?.Failure(item?.Id?.ToString() ?? "unknown track", message);
            getInfo.updateDownloadOutput("\r\nERROR [" + item?.Id + "]: " + message + "\r\n");
            qbdlxForm._qbdlxForm.logger.Error(message);
            Miscellaneous.LogFailedDownloadStreamEntry(path, item, message);
        }
    }
}
