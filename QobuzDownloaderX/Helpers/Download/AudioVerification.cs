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
        public int Version { get; set; }
        public string TrackId { get; set; }
        public string Format { get; set; }
        public int BitDepth { get; set; }
        public int SampleRate { get; set; }
        public string Sha256 { get; set; }
    }

    internal static class AudioVerification
    {
        internal const string ReceiptExtension = ".qbdlx.json";
        private const int DuplicateNameReserve = 12;
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
        internal static async Task<string> HashAsync(string path, CancellationToken token)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                return await HashAsync(file, token).ConfigureAwait(false);
        }
        internal static async Task<string> HashAsync(System.IO.Stream stream, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[81920];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (count == 0) break;
                    sha.TransformBlock(buffer, 0, count, null, 0);
                }
                token.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        internal static bool CanSkipAnyVerifiedCopy(string path, Item track, string format, AudioQuality quality) =>
            CanSkipAnyVerifiedCopyAsync(path, track, format, quality, CancellationToken.None).GetAwaiter().GetResult();
        internal static async Task<bool> CanSkipAnyVerifiedCopyAsync(string path, Item track, string format, AudioQuality quality, CancellationToken token)
        {
            if (await CanSkipAsync(path, track, format, quality, token).ConfigureAwait(false)) return true;
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) return false;
            string name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            foreach (string receipt in Directory.EnumerateFiles(directory, name + " (*)" + ext + ReceiptExtension))
            {
                token.ThrowIfCancellationRequested();
                string candidate = receipt.Substring(0, receipt.Length - ReceiptExtension.Length);
                if (await CanSkipAsync(candidate, track, format, quality, token).ConfigureAwait(false)) return true;
            }
            return false;
        }
        internal static bool CanSkip(string path, Item track, string format, AudioQuality quality) =>
            CanSkipAsync(path, track, format, quality, CancellationToken.None).GetAwaiter().GetResult();
        internal static async Task<bool> CanSkipAsync(string path, Item track, string format, AudioQuality quality, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path) || !File.Exists(path + ReceiptExtension)) return false;
                var receipt = JsonConvert.DeserializeObject<AudioReceipt>(File.ReadAllText(path + ReceiptExtension));
                if (receipt == null || receipt.TrackId != track.Id.ToString() || receipt.Format != format ||
                    receipt.BitDepth != quality.BitDepth || receipt.SampleRate != quality.SampleRate ||
                    receipt.Sha256 != await HashAsync(path, token).ConfigureAwait(false)) return false;
                Inspect(path, track, quality);
                if (!quality.IsFlac && receipt.Version < 2) await Mp3Verification.ValidateAsync(path, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException || ex is TagLib.CorruptFileException || ex is TagLib.UnsupportedFormatException)
            { return false; }
        }
        internal static async Task<string> CreateReceiptAsync(string path, Item track, string format, AudioQuality quality, CancellationToken token)
        {
            var receipt = new AudioReceipt { Version = 2, TrackId = track.Id.ToString(), Format = format,
                BitDepth = quality.BitDepth, SampleRate = quality.SampleRate, Sha256 = await HashAsync(path, token).ConfigureAwait(false) };
            return JsonConvert.SerializeObject(receipt);
        }
        internal static void SaveReceipt(string path, Item track, string format, AudioQuality quality) =>
            SaveReceiptAsync(path, track, format, quality, CancellationToken.None).GetAwaiter().GetResult();
        internal static async Task SaveReceiptAsync(string path, Item track, string format, AudioQuality quality, CancellationToken token)
        {
            string json = await CreateReceiptAsync(path, track, format, quality, token).ConfigureAwait(false);
            string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".qbdlx-receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                AtomicFiles.WriteText(temporary, json);
                token.ThrowIfCancellationRequested();
                if (File.Exists(path + ReceiptExtension)) File.Replace(temporary, path + ReceiptExtension, null);
                else File.Move(temporary, path + ReceiptExtension);
            }
            finally { AtomicFiles.TryDelete(temporary); }
        }
        internal static string IdentityPath(string path, Item track, string format, AudioQuality quality)
        {
            string suffix = " [ID" + track.Id + "-F" + format + (quality.IsFlac ? "-" + quality.BitDepth + "bit-" + quality.SampleRate + "Hz" : "-320kbps") + "]";
            string fileName = Path.GetFileName(path);
            string ext = Path.GetExtension(fileName);
            string name = DownloadPaths.Truncate(Path.GetFileNameWithoutExtension(fileName), ext.Length + suffix.Length + ReceiptExtension.Length + DuplicateNameReserve);
            // Windows validates every component in GetDirectoryName, including
            // the original overlong song name. Extract the directory prefix
            // before shortening that name; the final path is checked separately.
            string directory = path.Substring(0, path.Length - fileName.Length);
            return Path.Combine(directory, name + suffix + ext);
        }
    }
}
