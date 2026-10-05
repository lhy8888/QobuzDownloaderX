using Newtonsoft.Json;
using QopenAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    // Same read-only endpoints and signing scheme as the bundled API, with bounded retries/cancellation.
    internal sealed class ReliableQobuzService
    {
        private readonly CancellationToken token;
        private readonly HttpClient client;
        internal ReliableQobuzService(CancellationToken token = default(CancellationToken), HttpClient client = null) { this.token = token; this.client = client; }
        private Dictionary<string, string> Params(string app, string auth, int limit = 500, int offset = 0) =>
            new Dictionary<string, string> { { "app_id", app }, { "user_auth_token", auth },
                { "limit", limit.ToString(CultureInfo.InvariantCulture) }, { "offset", offset.ToString(CultureInfo.InvariantCulture) } };
        private async Task<T> GetAsync<T>(string endpoint, Dictionary<string, string> parameters)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                string query = string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value ?? "")));
                using (var response = await ReliableHttp.GetAsync("https://www.qobuz.com/api.json/0.2/" + endpoint + "?" + query, deadline.Token, client: client).ConfigureAwait(false))
                {
                    var result = JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (result == null) throw new InvalidDataException("The service returned no data.");
                    return result;
                }
            }
        }
        public Album AlbumGetWithAuth(string app, string id, string auth, int limit = 500, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["album_id"] = id; var album = GetAsync<Album>("album/get", p).GetAwaiter().GetResult(); if (album.Id != id) throw new InvalidDataException("Album identity mismatch."); return album; }
        public Item TrackGetWithAuth(string app, string id, string auth)
        { var p = Params(app, auth); p["track_id"] = id; var item = GetAsync<Item>("track/get", p).GetAwaiter().GetResult(); if (item.Id.ToString() != id) throw new InvalidDataException("Track identity mismatch."); return item; }
        public Artist ArtistGetWithAuth(string app, string id, string auth, string extra, int limit = 500, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["artist_id"] = id; p["extra"] = Uri.UnescapeDataString(extra); p["sort"] = "release_desc"; var result = GetAsync<Artist>("artist/get", p).GetAwaiter().GetResult(); EnsureIdentity(result.Id, id); return result; }
        public QopenAPI.Label LabelGetWithAuth(string app, string id, string extra, string auth, int limit = 500, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["label_id"] = id; p["extra"] = extra; var result = GetAsync<QopenAPI.Label>("label/get", p).GetAwaiter().GetResult(); EnsureIdentity(result.Id, id); return result; }
        public Favorites FavoriteGetUserFavoritesWithAuth(string app, string id, string type, string auth, int limit = 500, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["user_id"] = id; p["type"] = type; return GetAsync<Favorites>("favorite/getUserFavorites", p).GetAwaiter().GetResult(); }
        public Playlist PlaylistGetWithAuth(string app, string auth, string id, string extra, int limit = 500, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["playlist_id"] = id; p["extra"] = extra; var result = GetAsync<Playlist>("playlist/get", p).GetAwaiter().GetResult(); EnsureIdentity(result.Id, id); return result; }
        private static void EnsureIdentity(object actual, string expected)
        {
            string value = Convert.ToString(actual, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(value) || value != expected)
                throw new InvalidDataException("The service returned a different collection identity.");
        }
        public ReleasesList GetReleaseListWithAuth(string app, string id, string types, string auth, string sort = "release_date", int track_size = 10, int limit = 100, int offset = 0)
        { var p = Params(app, auth, limit, offset); p["artist_id"] = id; p["release_type"] = types; p["sort"] = sort; p["track_size"] = track_size.ToString(CultureInfo.InvariantCulture); return GetAsync<ReleasesList>("artist/getReleasesList", p).GetAwaiter().GetResult(); }
        public Task<QopenAPI.Stream> TrackGetFileUrlAsync(string id, string format, string app, string auth, string secret)
        {
            string timestamp = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
            string signature;
            using (var md5 = MD5.Create())
                signature = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes("trackgetFileUrlformat_id" + format + "intentstreamtrack_id" + id + timestamp + secret))).Replace("-", "").ToLowerInvariant();
            var p = Params(app, auth); p["track_id"] = id; p["format_id"] = format; p["intent"] = "stream";
            p["request_ts"] = timestamp; p["request_sig"] = signature;
            p.Remove("limit"); p.Remove("offset");
            return GetAsync<QopenAPI.Stream>("track/getFileUrl", p);
        }
    }
}
