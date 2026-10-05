using QopenAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Net.Http;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class GetInfo
    {
        public ReliableQobuzService QoService;
        private readonly bool silent;
        public GetInfo(CancellationToken token = default(CancellationToken), bool silent = false, HttpClient client = null)
        { QoService = new ReliableQobuzService(token, client); this.silent = silent; }
        internal GetInfo(ReliableQobuzService service) { QoService = service; silent = true; }
        public User QoUser = new User();
        public Artist QoArtist;
        public Album QoAlbum;
        public Item QoItem;
        public Favorites QoFavorites;
        public Playlist QoPlaylist;
        public QopenAPI.Label QoLabel;

        public string outputText { get; set; }

        public HashSet<string> GetArtistReleaseTypeIds(string app_id, string artist_id, string selectedTypes, string user_auth_token)
        {
            if (string.IsNullOrEmpty(selectedTypes))
            {
                qbdlxForm._qbdlxForm.logger.Error("No release types selected.");
                return new HashSet<string>();
            }

            var allIds = new HashSet<string>();
            try
            {
                outputText = null;
                qbdlxForm._qbdlxForm.logger.Debug("Fetching selected release types IDs…");

                int limit = 100;
                int offset = 0;

                ReleasesList list;

                do
                {
                    list = QoService.GetReleaseListWithAuth(app_id, artist_id, selectedTypes, user_auth_token, track_size: 1, limit: limit, offset: offset);

                    if (list == null || list.Items == null) throw new InvalidDataException("Release list data is missing.");
                    if (list.Items.Count == 0)
                    {
                        if (list.HasMore) throw new InvalidDataException("Release list ended before its final page.");
                        break;
                    }
                    int previous = allIds.Count;
                    foreach (var r in list.Items)
                    {
                        if (string.IsNullOrEmpty(r?.Id)) throw new InvalidDataException("Release identity is missing.");
                        allIds.Add(r.Id);
                    }
                    if (allIds.Count == previous) throw new InvalidDataException("Release pagination did not advance.");
                    offset += list.Items.Count;
                    if (offset > 100000 && list.HasMore) throw new InvalidDataException("Release pagination limit exceeded.");

                } while (list.HasMore);

                return allIds;
            }
            catch (Exception ex)
            {
                updateDownloadOutput("\r\nQuery failed: " + ex.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to fetch artist release types IDs, error below:\r\n" + ex.GetType().Name);
                throw;
            }
        }

        public Artist getArtistInfo(string app_id, string artist_id, string user_auth_token)
        {
            try

            {
                // Grab artist info with auth
                outputText = null;
                qbdlxForm._qbdlxForm.logger.Debug("Getting artist Info…");

                int limit = 500;
                int offset = 0;

                // 1) First request (initial page)
                QoArtist = QoService.ArtistGetWithAuth(app_id, artist_id, user_auth_token, "albums%2Calbums_with_last_release", limit, offset);

                if (QoArtist == null || QoArtist.Albums == null || QoArtist.Albums.Items == null)
                {
                    qbdlxForm._qbdlxForm.logger.Warning("Artist has no albums or retrieval failed.");
                    throw new InvalidDataException("Artist data is missing.");
                }

                var allItems = QoArtist.Albums.Items.Cast<object>().ToList();
                int total = QoArtist.Albums.Total;

                offset = allItems.Count;

                // 2) Pagination loop - keep requesting pages until all albums are collected
                while (allItems.Count < total)
                {
                    var page = QoService.ArtistGetWithAuth(app_id, artist_id, user_auth_token, "albums%2Calbums_with_last_release", limit, offset);

                    if (page == null || page.Albums == null || page.Albums.Items == null || page.Albums.Items.Count == 0)
                        break;

                    if (page.Albums.Total != total) throw new InvalidDataException("The collection changed during pagination; retry the request.");
                    allItems.AddRange(page.Albums.Items.Cast<object>());
                    offset = allItems.Count;

                    // Safety break to prevent infinite loop
                    if (offset > 100000) break;
                }

                EnsureComplete(allItems.Count, total);
                EnsureUnique(allItems.Cast<Item>());
                string selectedTypes = Miscellaneous.GetCheckedDownloadFromArtistTypes();
                if (selectedTypes == "all")
                {
                    QoArtist.Albums.Items = allItems.Cast<Item>()
                                                    .OrderByDescending(a => a.Artist?.Id.ToString() == artist_id)
                                                    .ToList();
                }
                else
                {
                    var allowedIds = GetArtistReleaseTypeIds(app_id, artist_id, selectedTypes, user_auth_token);

                    QoArtist.Albums.Items = allItems.Cast<Item>()
                                                    .Where(a => allowedIds.Contains(a.Id.ToString()))
                                                    .OrderByDescending(a => a.Artist?.Id.ToString() == artist_id)
                                                    .ToList();
                }

                return QoArtist;
            }
            catch (Exception getArtistInfoEx)
            {
                updateDownloadOutput("\r\nQuery failed: " + getArtistInfoEx.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to get artist info, error below:\r\n" + getArtistInfoEx.GetType().Name);
                throw;
            }
        }

        public QopenAPI.Label getLabelInfo(string app_id, string label_id, string user_auth_token)
        {
            try
            {
                qbdlxForm._qbdlxForm.logger.Debug("Getting label info…");
                outputText = null;

                int limit = 500;
                int offset = 0;

                // 1) First request (initial page)
                QoLabel = QoService.LabelGetWithAuth(app_id, label_id, "albums", user_auth_token, limit, offset);

                if (QoLabel == null || QoLabel.Albums == null || QoLabel.Albums.Items == null)
                    throw new InvalidDataException("Label data is missing.");

                // Store all collected items here
                var allItems = QoLabel.Albums.Items.Cast<object>().ToList();

                int total = 0;
                try { total = QoLabel.Albums.Total; } catch { }

                offset = allItems.Count;

                // 2) Pagination loop - keep requesting pages until all items are collected
                while ((total == 0 && QoLabel.Albums.Items.Count > 0)
                    || (total > 0 && allItems.Count < total))
                {
                    var page = QoService.LabelGetWithAuth(app_id, label_id, "albums", user_auth_token, limit, offset);

                    if (page == null || page.Albums == null || page.Albums.Items == null || page.Albums.Items.Count == 0)
                        break;

                    // Add items from the page
                    if (page.Albums.Total != total) throw new InvalidDataException("The collection changed during pagination; retry the request.");
                    allItems.AddRange(page.Albums.Items.Cast<object>());

                    offset = allItems.Count;

                    if (offset > 100000) break; // safety cutoff
                }

                EnsureComplete(allItems.Count, total);
                EnsureUnique(allItems.Cast<Item>());
                QoLabel.Albums.Items = allItems.Cast<Item>().ToList();
                return QoLabel;
            }
            catch (Exception getLabelInfoEx)
            {
                updateDownloadOutput("\r\nQuery failed: " + getLabelInfoEx.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to get label info, error below:\r\n" + getLabelInfoEx.GetType().Name);
                throw;
            }
        }

        public Favorites getFavoritesInfo(string app_id, string user_id, string type, string user_auth_token)
        {
            try
            {
                qbdlxForm._qbdlxForm.logger.Debug("Getting favorites Info…");
                outputText = null;

                int limit = 500;
                int offset = 0;

                // 1) First request (initial page)
                QoFavorites = QoService.FavoriteGetUserFavoritesWithAuth(
                    app_id, user_id, type, user_auth_token, limit, offset);

                if (QoFavorites == null)
                    return QoFavorites;

                // Detect the correct list depending on type
                List<object> allItems;

                if (type == "albums")
                    allItems = QoFavorites.Albums.Items.Cast<object>().ToList();
                else if (type == "tracks")
                    allItems = QoFavorites.Tracks.Items.Cast<object>().ToList();
                else if (type == "artists")
                    allItems = QoFavorites.Artists.Items.Cast<object>().ToList();
                else
                    return QoFavorites;

                int total = 0;
                try
                {
                    if (type == "albums") total = QoFavorites.Albums.Total;
                    if (type == "tracks") total = QoFavorites.Tracks.Total;
                    if (type == "artists") total = QoFavorites.Artists.Total;
                }
                catch { }

                offset = allItems.Count;

                // 2) Pagination loop - keep requesting pages until all items are collected
                while (total == 0 || allItems.Count < total)
                {
                    var page = QoService.FavoriteGetUserFavoritesWithAuth(
                        app_id, user_id, type, user_auth_token, limit, offset);

                    if (page == null)
                        break;

                    List<object> pageItems;

                    if (type == "albums")
                        pageItems = page.Albums.Items.Cast<object>().ToList();
                    else if (type == "tracks")
                        pageItems = page.Tracks.Items.Cast<object>().ToList();
                    else // artists
                        pageItems = page.Artists.Items.Cast<object>().ToList();

                    if (pageItems.Count == 0)
                        break;

                    int pageTotal = type == "albums" ? page.Albums.Total : type == "tracks" ? page.Tracks.Total : page.Artists.Total;
                    if (pageTotal != total) throw new InvalidDataException("The collection changed during pagination; retry the request.");
                    allItems.AddRange(pageItems);

                    offset = allItems.Count;
                    if (offset > 1000000) break;
                }

                EnsureComplete(allItems.Count, total);
                EnsureUnique(allItems.Cast<Item>());
                if (type == "albums")
                    QoFavorites.Albums.Items = allItems.Cast<Item>().ToList();
                else if (type == "tracks")
                    QoFavorites.Tracks.Items = allItems.Cast<Item>().ToList();
                else
                    QoFavorites.Artists.Items = allItems.Cast<Item>().ToList();

                return QoFavorites;
            }
            catch (Exception getFavoritesInfoEx)
            {
                updateDownloadOutput("\r\nQuery failed: " + getFavoritesInfoEx.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to get favorites info, error below:\r\n" + getFavoritesInfoEx.GetType().Name);
                throw;
            }
        }

        public Item getTrackInfoLabels(string app_id, string track_id, string user_auth_token)
        {
            QoItem = QoService.TrackGetWithAuth(app_id, track_id, user_auth_token);
            if (QoItem?.Album?.Id == null) throw new InvalidDataException("Track album data is missing.");
            // A single track needs album tags/counts, not a fresh download of the album's entire track list.
            QoAlbum = QoService.AlbumGetWithAuth(app_id, QoItem.Album.Id, user_auth_token, limit: 1);
            return QoItem;
        }

        public Album getAlbumInfoLabels(string app_id, string album_id, string user_auth_token)
        {
            try
            {
                // Grab album info with auth
                outputText = null;
                qbdlxForm._qbdlxForm.logger.Debug("Getting album Info…");

                // Pagination variables
                int limit = 500;
                int offset = 0;

                // 1) First request (initial page)
                QoAlbum = QoService.AlbumGetWithAuth(app_id, album_id, user_auth_token, limit, offset);

                if (QoAlbum == null || QoAlbum.Tracks == null || QoAlbum.Tracks.Items == null)
                {
                    qbdlxForm._qbdlxForm.logger.Warning("Album has no tracks or retrieval failed.");
                    throw new InvalidDataException("Album data is missing.");
                }

                var allItems = QoAlbum.Tracks.Items.Cast<object>().ToList();
                int total = QoAlbum.Tracks.Total;

                offset = allItems.Count;

                // 2) Pagination loop - keep requesting pages until all tracks are collected
                while (allItems.Count < total)
                {
                    var page = QoService.AlbumGetWithAuth(app_id, album_id, user_auth_token, limit, offset);

                    if (page == null || page.Tracks == null || page.Tracks.Items == null || page.Tracks.Items.Count == 0)
                        break;

                    if (page.Tracks.Total != total) throw new InvalidDataException("The collection changed during pagination; retry the request.");
                    allItems.AddRange(page.Tracks.Items.Cast<object>());
                    offset = allItems.Count;

                    // Safety break to prevent infinite loop
                    if (offset > 100000) break;
                }

                EnsureComplete(allItems.Count, total);
                EnsureUnique(allItems.Cast<Item>());
                QoAlbum.Tracks.Items = allItems.Cast<Item>().ToList();
                return QoAlbum;
            }
            catch (Exception getAlbumInfoLabelsEx)
            {
                updateDownloadOutput("\r\nQuery failed: " + getAlbumInfoLabelsEx.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to get album info, error below:\r\n" + getAlbumInfoLabelsEx.GetType().Name);
                throw;
            }
        }

        public Playlist getPlaylistInfoLabels(string app_id, string playlist_id, string user_auth_token)
        {
            try
            {
                qbdlxForm._qbdlxForm.logger.Debug("Getting playlist Info…");
                outputText = null;

                int limit = 500;
                int offset = 0;

                // 1) First request (initial page)
                QoPlaylist = QoService.PlaylistGetWithAuth(app_id, user_auth_token, playlist_id, "tracks", limit, offset);

                if (QoPlaylist == null || QoPlaylist.Tracks == null || QoPlaylist.Tracks.Items == null)
                    throw new InvalidDataException("Playlist data is missing.");

                var allItems = QoPlaylist.Tracks.Items.Cast<object>().ToList();

                int total = QoPlaylist.Tracks.Total;

                offset = allItems.Count;

                // 2) Pagination loop - keep requesting pages until all items are collected
                while (total == 0 || allItems.Count < total)
                {
                    var page = QoService.PlaylistGetWithAuth(app_id, user_auth_token, playlist_id, "tracks", limit, offset);

                    if (page == null || page.Tracks == null || page.Tracks.Items == null)
                        break;

                    if (page.Tracks.Items.Count == 0)
                        break;

                    if (page.Tracks.Total != total) throw new InvalidDataException("The collection changed during pagination; retry the request.");
                    allItems.AddRange(page.Tracks.Items.Cast<object>());

                    offset = allItems.Count;
                    if (offset > 1000000) break;
                }

                EnsureComplete(allItems.Count, total);
                if (allItems.Cast<Item>().Select(i => i.Position).Distinct().Count() != allItems.Count)
                    throw new InvalidDataException("Playlist positions are duplicated.");
                QoPlaylist.Tracks.Items = allItems.Cast<Item>().ToList();
                return QoPlaylist;
            }
            catch (Exception getPlaylistInfoLabelsEx)
            {
                updateDownloadOutput("\r\nQuery failed: " + getPlaylistInfoLabelsEx.GetType().Name);
                qbdlxForm._qbdlxForm.logger.Error("Failed to get playlist info, error below:\r\n" + getPlaylistInfoLabelsEx.GetType().Name);
                throw;
            }
        }

        private static void EnsureUnique(IEnumerable<Item> items)
        {
            var ids = items.Select(i => i?.Id?.ToString()).ToList();
            if (ids.Any(string.IsNullOrEmpty) || ids.Distinct().Count() != ids.Count)
                throw new InvalidDataException("The service returned duplicate or missing item identities.");
        }

        private static void EnsureComplete(int count, int total)
        {
            if (count != total) throw new InvalidDataException("The service returned an incomplete item list.");
        }

        public void updateDownloadOutput(string text)
        {
            if (silent) return;
            var form = qbdlxForm._qbdlxForm;
            form.InvokeOutput(() =>
            {
                if (text == null) form.downloadOutput.Clear();
                else form.downloadOutput.AppendText(text);
                outputText = form.downloadOutput.Text;
            });
        }

    }
}
