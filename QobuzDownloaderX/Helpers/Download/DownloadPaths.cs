using System;
using System.IO;

namespace QobuzDownloaderX.Helpers
{
    internal static class DownloadPaths
    {
        internal static string Truncate(string name, int reservedLength)
        {
            if (reservedLength < 0 || reservedLength >= 255) throw new ArgumentOutOfRangeException(nameof(reservedLength));
            int length = 255 - reservedLength;
            if (string.IsNullOrEmpty(name) || name.Length <= length) return name;
            // Do not split a UTF-16 surrogate pair at the filename boundary.
            int prefix = length - 1;
            if (prefix > 0 && char.IsHighSurrogate(name[prefix - 1])) prefix--;
            return name.Substring(0, prefix) + "…";
        }
        internal static string SafePath(string root, string path)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The filename template points outside the download folder.");
            return path;
        }
    }
}
