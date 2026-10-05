using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class QobuzLink
    {
        internal string Type { get; private set; }
        internal string Id { get; private set; }
        internal static QobuzLink Parse(string text)
        {
            if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !(uri.Host.Equals("qobuz.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".qobuz.com", StringComparison.OrdinalIgnoreCase)) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Invalid Qobuz URL.");
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            string type = parts[0].ToLowerInvariant(), id = null;
            if (parts.Length == 3 && type == "artist" && parts[2].Equals("releases", StringComparison.OrdinalIgnoreCase)) parts = parts.Take(2).ToArray();
            if (parts.Length == 2 && new[] { "album", "track", "artist", "playlist", "label" }.Contains(type)) id = parts[1];
            else if (parts.Length == 4 && (parts[1].Equals("album", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("interpreter", StringComparison.OrdinalIgnoreCase)))
            { type = parts[1].Equals("album", StringComparison.OrdinalIgnoreCase) ? "album" : "artist"; id = parts[3]; }
            else if (type == "user" && parts.Length >= 3 && new[] { "albums", "tracks", "artists" }.Contains(parts[parts.Length - 1].ToLowerInvariant()))
            { id = string.Join("/", parts.Skip(1)).ToLowerInvariant(); }
            if (string.IsNullOrWhiteSpace(id) || (type != "user" && !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) ||
                (type == "user" && parts.Skip(1).Any(p => !Regex.IsMatch(p, "^[A-Za-z0-9_-]+$"))))
                throw new InvalidDataException("This Qobuz URL does not identify a supported album, track, artist, playlist, label or favorites list.");
            return new QobuzLink { Type = type, Id = id };
        }
    }
}
