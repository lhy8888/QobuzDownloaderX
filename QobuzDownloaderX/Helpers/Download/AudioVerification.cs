using Newtonsoft.Json;
using QopenAPI;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class AudioQuality
    {
        internal int BitDepth { get; set; }
        internal int SampleRate { get; set; }
        internal bool IsFlac { get; set; }
        internal static string FormatLabel(string extension, int depth, double rate)
        {
            string format = extension.ToUpperInvariant().TrimStart('.');
            return depth > 0 && rate > 0 ? format + " (" + depth + "bit-" + rate.ToString(CultureInfo.InvariantCulture) + "kHz)" : format;
        }
        internal static int Hertz(double rate) => (int)Math.Round(rate < 1000 ? rate * 1000 : rate);
        internal static AudioQuality FromResponse(QopenAPI.Stream stream, Item track, string format)
        {
            if (format != "5" && format != "6" && format != "7" && format != "27") throw new InvalidDataException("Unknown selected audio quality.");
            if (stream == null || stream.TrackID.ToString(CultureInfo.InvariantCulture) != track.Id.ToString())
                throw new InvalidDataException("The service returned a different track.");
            if (!Uri.TryCreate(stream.StreamURL, UriKind.Absolute, out var url) || url.Scheme != "https")
                throw new InvalidDataException("The service returned no secure audio address.");
            bool flac = format != "5";
            if ((flac && stream.FormatID != 6 && stream.FormatID != 7 && stream.FormatID != 27) || (!flac && stream.FormatID != 5))
                throw new InvalidDataException("The service returned a different audio format.");
            if (!double.TryParse(stream.SampleRate, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) || rate <= 0)
                throw new InvalidDataException("The service did not confirm the sample rate.");
            int depth = 0;
            if (flac && (!int.TryParse(stream.BitDepth, out depth) || depth <= 0))
                throw new InvalidDataException("The service did not confirm the bit depth.");
            var quality = new AudioQuality { IsFlac = flac, BitDepth = depth, SampleRate = Hertz(rate) };
            if (flac)
            {
                int sourceRate = Hertz(track.MaximumSamplingRate);
                int sourceDepth = track.MaximumBitDepth;
                if (sourceRate <= 0 || sourceDepth <= 0) throw new InvalidDataException("The source quality is unknown.");
                int expectedDepth = format == "6" ? Math.Min(16, sourceDepth) : sourceDepth;
                int expectedRate = sourceRate;
                if (format == "6") expectedRate = Math.Min(sourceRate, 44100);
                if (format == "7" && sourceRate > 96000) expectedRate = sourceRate == 176400 ? 88200 : 96000;
                if (depth != expectedDepth || quality.SampleRate != expectedRate)
                    throw new InvalidDataException("The returned audio quality does not match the selected quality and source.");
            }
            return quality;
        }
    }

    internal sealed class AudioReceipt
    {
        public string TrackId { get; set; }
        public string Format { get; set; }
        public int BitDepth { get; set; }
        public int SampleRate { get; set; }
        public string Sha256 { get; set; }
    }

    internal static class AudioVerification
    {
        internal static void Inspect(string path, Item track, AudioQuality quality)
        {
            using (var file = TagLib.File.Create(path))
            {
                var p = file.Properties;
                if (p.Duration.TotalSeconds <= 0 || p.AudioSampleRate != quality.SampleRate ||
                    (quality.IsFlac && p.BitsPerSample != quality.BitDepth) || (!quality.IsFlac && p.AudioBitrate < 300))
                    throw new InvalidDataException("The audio file does not match the confirmed quality.");
                if (track.Duration > 0 && Math.Abs(p.Duration.TotalSeconds - track.Duration) > 2)
                    throw new InvalidDataException("The audio duration does not match the track.");
            }
        }
        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        internal static bool CanSkipAnyVerifiedCopy(string path, Item track, string format, AudioQuality quality)
        {
            if (CanSkip(path, track, format, quality)) return true;
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) return false;
            // A damaged/unverified original is preserved, and a verified replacement may have an auto-renamed name.
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            foreach (string receipt in Directory.EnumerateFiles(directory, name + " (*)" + ext + ".qbdlx.json"))
            {
                string candidate = receipt.Substring(0, receipt.Length - ".qbdlx.json".Length);
                if (CanSkip(candidate, track, format, quality)) return true;
            }
            return false;
        }
        internal static bool CanSkip(string path, Item track, string format, AudioQuality quality)
        {
            try
            {
                if (!File.Exists(path) || !File.Exists(path + ".qbdlx.json")) return false;
                var receipt = JsonConvert.DeserializeObject<AudioReceipt>(File.ReadAllText(path + ".qbdlx.json"));
                if (receipt == null || receipt.TrackId != track.Id.ToString() || receipt.Format != format ||
                    receipt.BitDepth != quality.BitDepth || receipt.SampleRate != quality.SampleRate || receipt.Sha256 != Hash(path)) return false;
                Inspect(path, track, quality);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is TagLib.CorruptFileException || ex is TagLib.UnsupportedFormatException)
            { return false; }
        }
        internal static void SaveReceipt(string path, Item track, string format, AudioQuality quality)
        {
            var receipt = new AudioReceipt { TrackId = track.Id.ToString(), Format = format,
                BitDepth = quality.BitDepth, SampleRate = quality.SampleRate, Sha256 = Hash(path) };
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(receipt));
                if (File.Exists(path + ".qbdlx.json")) File.Replace(temporary, path + ".qbdlx.json", null);
                else File.Move(temporary, path + ".qbdlx.json");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static string IdentityPath(string path, Item track, string format, AudioQuality quality)
        {
            string suffix = " [ID" + track.Id + "-F" + format + (quality.IsFlac ? "-" + quality.BitDepth + "bit-" + quality.SampleRate + "Hz" : "-320kbps") + "]";
            string ext = Path.GetExtension(path);
            string name = DownloadPaths.Truncate(Path.GetFileNameWithoutExtension(path), (ext.Length + suffix.Length));
            return Path.Combine(Path.GetDirectoryName(path), name + suffix + ext);
        }
    }
}
