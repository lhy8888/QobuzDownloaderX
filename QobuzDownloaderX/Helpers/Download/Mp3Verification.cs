using NLayer;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers
{
    internal static class Mp3Verification
    {
        // MpegFile resynchronizes after bad frames. Validation must instead decode every contiguous frame.
        internal static async Task ValidateAsync(string path, CancellationToken token)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                long end = input.Length;
                byte[] header = new byte[10];
                await Read(input, header, 10, token).ConfigureAwait(false);
                while (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
                {
                    if (header[3] < 2 || header[3] > 4 || (header[6] | header[7] | header[8] | header[9]) >= 128)
                        throw new InvalidDataException("Invalid MP3 metadata header.");
                    long size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
                    long next = input.Position + size + (header[3] == 4 && (header[5] & 16) != 0 ? 10 : 0);
                    if (next + 4 > end) throw new InvalidDataException("Truncated MP3 metadata.");
                    input.Position = next;
                    await Read(input, header, 10, token).ConfigureAwait(false);
                }
                long start = input.Position - 10;
                if (end >= 128)
                {
                    input.Position = end - 128;
                    await Read(input, header, 3, token).ConfigureAwait(false);
                    if (header[0] == 'T' && header[1] == 'A' && header[2] == 'G') end -= 128;
                }
                if (end - start >= 32)
                {
                    byte[] footer = new byte[32]; input.Position = end - 32;
                    await Read(input, footer, 32, token).ConfigureAwait(false);
                    if (System.Text.Encoding.ASCII.GetString(footer, 0, 8) == "APETAGEX")
                    {
                        uint size = BitConverter.ToUInt32(footer, 12);
                        if (size < 32 || size > end - start) throw new InvalidDataException("Invalid MP3 trailing metadata.");
                        end -= size;
                        if ((footer[23] & 128) != 0) end -= 32; // Optional APE header.
                    }
                }
                input.Position = start;
                var decoder = new MpegFrameDecoder();
                float[] samples = new float[2304];
                int frames = 0, sampleRate = 0, channels = 0;
                while (input.Position < end)
                {
                    token.ThrowIfCancellationRequested();
                    await Read(input, header, 4, token).ConfigureAwait(false);
                    var frame = new Frame(header);
                    if (input.Position - 4 + frame.FrameLength > end) throw new InvalidDataException("Truncated MP3 frame.");
                    if (sampleRate != 0 && (sampleRate != frame.SampleRate || channels != frame.Channels))
                        throw new InvalidDataException("MP3 parameters change inside the audio.");
                    sampleRate = frame.SampleRate; channels = frame.Channels;
                    await Read(input, frame.Data, frame.FrameLength - 4, token).ConfigureAwait(false);
                    frame.ValidateCrc();
                    int count;
                    try { count = decoder.DecodeFrame(frame, samples, 0); }
                    catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is EndOfStreamException)
                    { throw new InvalidDataException("MP3 frame cannot be fully decoded.", ex); }
                    if (count != frame.SampleCount * channels) throw new InvalidDataException("Incomplete MP3 audio frame.");
                    for (int n = 0; n < count; n++)
                        if (float.IsNaN(samples[n]) || float.IsInfinity(samples[n])) throw new InvalidDataException("Invalid decoded MP3 audio.");
                    frames++;
                }
                if (frames < 2 || input.Position != end) throw new InvalidDataException("No complete MP3 audio.");
                token.ThrowIfCancellationRequested();
            }
        }
        private static async Task Read(Stream input, byte[] data, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await input.ReadAsync(data, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException("Unexpected end of MP3 file.");
                offset += read;
            }
        }
        private sealed class Frame : IMpegFrame
        {
            private readonly uint header;
            internal readonly byte[] Data;
            private int bit;
            internal Frame(byte[] bytes)
            {
                header = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                int version = (int)(header >> 19 & 3);
                if ((header & 0xffe00000) != 0xffe00000 || version == 1 || (header >> 17 & 3) != 1 ||
                    BitRateIndex == 0 || BitRateIndex == 15 || SampleRateIndex == 3 || (header & 3) == 2)
                    throw new InvalidDataException("Invalid MP3 frame header.");
                Version = version == 3 ? MpegVersion.Version1 : version == 2 ? MpegVersion.Version2 : MpegVersion.Version25;
                SampleRate = new[] { 44100, 48000, 32000 }[SampleRateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
                BitRate = (version == 3 ? new[] { 0,32,40,48,56,64,80,96,112,128,160,192,224,256,320 } :
                    new[] { 0,8,16,24,32,40,48,56,64,80,96,112,128,144,160 })[BitRateIndex] * 1000;
                FrameLength = (version == 3 ? 144 : 72) * BitRate / SampleRate + (int)(header >> 9 & 1);
                Data = new byte[FrameLength - 4];
            }
            public int SampleRate { get; }
            public int SampleRateIndex => (int)(header >> 10 & 3);
            public int FrameLength { get; }
            public int BitRate { get; }
            public MpegVersion Version { get; }
            public MpegLayer Layer => MpegLayer.LayerIII;
            public MpegChannelMode ChannelMode => (MpegChannelMode)(header >> 6 & 3);
            internal int Channels => ChannelMode == MpegChannelMode.Mono ? 1 : 2;
            public int ChannelModeExtension => (int)(header >> 4 & 3);
            public int SampleCount => Version == MpegVersion.Version1 ? 1152 : 576;
            public int BitRateIndex => (int)(header >> 12 & 15);
            public bool IsCopyrighted => (header & 8) != 0;
            public bool HasCrc => (header & 0x10000) == 0;
            public bool IsCorrupted => false;
            public void Reset() { bit = HasCrc ? 16 : 0; }
            public int ReadBits(int count)
            {
                if (count < 0 || count > 32 || bit + count > Data.Length * 8) return -1;
                uint value = 0;
                for (int n = 0; n < count; n++, bit++) value = (value << 1) | (uint)(Data[bit / 8] >> (7 - bit % 8) & 1);
                return (int)value;
            }
            internal void ValidateCrc()
            {
                if (!HasCrc) return;
                int side = Version == MpegVersion.Version1 ? (Channels == 1 ? 17 : 32) : (Channels == 1 ? 9 : 17);
                if (Data.Length < side + 2) throw new InvalidDataException("Truncated MP3 side information.");
                int crc = 0xffff;
                Action<int> add = value => { for (int n = 7; n >= 0; n--) { bool mix = ((crc >> 15) ^ (value >> n)) % 2 != 0; crc = (crc << 1) & 0xffff; if (mix) crc ^= 0x8005; } };
                add((int)(header >> 8 & 255)); add((int)(header & 255));
                for (int n = 2; n < side + 2; n++) add(Data[n]);
                if (crc != (Data[0] << 8 | Data[1])) throw new InvalidDataException("MP3 frame checksum failed.");
            }
        }
    }
}
