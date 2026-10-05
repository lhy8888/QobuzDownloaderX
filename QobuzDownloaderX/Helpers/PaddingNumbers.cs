using System;
using QopenAPI;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class PaddingNumbers
    {
        private static int Digits(int count) => Math.Max(2, Math.Max(1, count).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        public int padTracks(Album album) => Digits(album.TracksCount);
        public int padPlaylistTracks(Playlist playlist) => Digits(playlist.TracksCount);
        public int padDiscs(Album album) => Digits(album.MediaCount);
    }
}
