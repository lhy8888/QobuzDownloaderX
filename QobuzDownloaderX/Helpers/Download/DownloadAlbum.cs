using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX
{
    internal sealed class DownloadAlbum
    {
        internal async Task DownloadAlbumAsync(string appId, string albumId, string format, string extension, string auth,
            string secret, string root, string artistTemplate, string albumTemplate, string trackTemplate,
            Album album, IProgress<int> progress, IProgress<(int current, int total)> trackCounter,
            DownloadStats stats, CancellationToken token)
        {
            if (album?.Tracks?.Items == null || album.Id != albumId || album.Tracks.Items.Count != album.Tracks.Total)
                throw new InvalidDataException("Album identity or track list is incomplete.");
            if (!album.Streamable && Settings.Default.streamableCheck)
                throw new InvalidDataException("This album is not streamable for the account.");
            var service = new ReliableQobuzService(token);
            var tracks = new DownloadTrack();
            int total = album.Tracks.Items.Count;
            int index = 0;
            int initialSuccess = (stats?.Succeeded ?? 0) + (stats?.Skipped ?? 0);
            foreach (var item in album.Tracks.Items)
            {
                token.ThrowIfCancellationRequested();
                if (qbdlxForm.skipCurrentAlbum)
                {
                    qbdlxForm.skipCurrentAlbum = false;
                    for (int remaining = index; remaining < total; remaining++) stats?.Skip();
                    break;
                }
                int position = index++;
                try
                {
                    var detail = service.TrackGetWithAuth(appId, item.Id.ToString(), auth);
                    if (detail.Album?.Id != album.Id) throw new InvalidDataException("Track belongs to a different album.");
                    await tracks.DownloadTrackAsync("album", appId, albumId, format, extension, auth, secret, root,
                        artistTemplate, albumTemplate, trackTemplate, album, detail,
                        new Progress<int>(value => progress?.Report((int)((position + value / 100.0) * 100 / Math.Max(1, total)))), stats, token).ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (Exception ex)
                {
                    string message = "Track information failed (" + ex.GetType().Name + ").";
                    stats?.Failure(item.Id.ToString(), message);
                    Miscellaneous.LogFailedDownloadStreamEntry(root, item, message);
                }
                trackCounter?.Report(((stats?.Succeeded ?? 0) + (stats?.Skipped ?? 0) - initialSuccess, total));
            }
            token.ThrowIfCancellationRequested();
            if (Settings.Default.downloadGoodies && album.Goodies != null)
            {
                using (var files = new DownloadFile())
                {
                    var pad = new PaddingNumbers();
                    string path = tracks.LastDirectory ?? await files.createPath(root, artistTemplate, albumTemplate, trackTemplate,
                        null, null, pad.padTracks(album), pad.padDiscs(album), album, null, null, format).ConfigureAwait(false);
                    foreach (var goody in album.Goodies)
                    {
                        token.ThrowIfCancellationRequested();
                        if (goody.FileFormatId == 52) continue;
                        try
                        {
                            if (string.IsNullOrEmpty(goody.Url)) throw new InvalidDataException("Attachment has no download address.");
                            await files.DownloadGoody(path, album, goody, new GetInfo(), token).ConfigureAwait(false);
                        }
                        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                        catch (Exception ex)
                        {
                            stats?.Failure("attachment " + goody.Id, "Attachment failed (" + ex.GetType().Name + ").");
                        }
                    }
                }
            }
        }
    }
}
