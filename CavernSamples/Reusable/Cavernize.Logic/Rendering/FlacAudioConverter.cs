using System.Security.Cryptography;

using Cavern.Format;
using Cavern.Format.Common;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Pure-C# lossless FLAC encoder used as a zero-dependency fallback when FFmpeg is unavailable
/// or blocked by Android execution policies, supporting 1–8 channels and 16/24-bit PCM.
/// </summary>
public static class FlacAudioConverter {
    const int DefaultBlockSize = 4096;

    /// <summary>
    /// Converts a rendered RIFF WAV file (<paramref name="wavPath"/>) losslessly into a FLAC file (<paramref name="flacPath"/>).
    /// </summary>
    public static void ConvertWavToFlac(string wavPath, string flacPath) {
        using RIFFWaveReader reader = new(wavPath);
        reader.ReadHeader();

        int channels = reader.ChannelCount;
        if (channels < 1 || channels > 8) {
            throw new NotSupportedException($"FLAC supports 1 to 8 channels (got {channels}).");
        }
        int sampleRate = reader.SampleRate;
        int bps = reader.Bits == BitDepth.Int24 ? 24 : 16;
        long totalSamples = reader.Length;

        Directory.CreateDirectory(Path.GetDirectoryName(flacPath) ?? ".");
        using FileStream outFs = File.Create(flacPath);

        // 1. Magic "fLaC" + placeholder STREAMINFO block (4-byte metadata header + 34-byte payload)
        outFs.Write("fLaC"u8);
        long streamInfoPos = outFs.Position;
        outFs.Write(new byte[38]);

        int blockSize = (int)Math.Min(DefaultBlockSize, Math.Max(16, totalSamples));
        int[][] chSamples = new int[channels][];
        for (int c = 0; c < channels; c++) {
            chSamples[c] = new int[blockSize];
        }

        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        float[] interleaved = new float[blockSize * channels];
        byte[] md5Chunk = new byte[blockSize * channels * (bps / 8)];
        long remaining = totalSamples;
        long frameNumber = 0;
        BitWriter bw = new(outFs);

        while (remaining > 0) {
            int n = (int)Math.Min(blockSize, remaining);
            long samplesNeeded = (long)n * channels;
            reader.ReadBlock(interleaved, 0, samplesNeeded);

            int md5Bytes = 0;
            if (bps == 16) {
                for (int s = 0; s < n; s++) {
                    for (int c = 0; c < channels; c++) {
                        float f = Math.Clamp(interleaved[s * channels + c], -1f, 1f);
                        int val = Math.Clamp((int)Math.Round(f * 32767f), short.MinValue, short.MaxValue);
                        chSamples[c][s] = val;
                        md5Chunk[md5Bytes++] = (byte)(val & 0xFF);
                        md5Chunk[md5Bytes++] = (byte)((val >> 8) & 0xFF);
                    }
                }
            } else {
                for (int s = 0; s < n; s++) {
                    for (int c = 0; c < channels; c++) {
                        float f = Math.Clamp(interleaved[s * channels + c], -1f, 1f);
                        int val = Math.Clamp((int)Math.Round(f * 8388607f), -8388608, 8388607);
                        chSamples[c][s] = val;
                        md5Chunk[md5Bytes++] = (byte)(val & 0xFF);
                        md5Chunk[md5Bytes++] = (byte)((val >> 8) & 0xFF);
                        md5Chunk[md5Bytes++] = (byte)((val >> 16) & 0xFF);
                    }
                }
            }
            md5.AppendData(md5Chunk, 0, md5Bytes);

            WriteFrame(bw, chSamples, n, channels, sampleRate, bps, frameNumber++);
            remaining -= n;
        }

        byte[] digest = md5.GetHashAndReset();
        outFs.Position = streamInfoPos;
        WriteStreamInfo(outFs, blockSize, sampleRate, channels, bps, totalSamples, digest);
        outFs.Flush();
    }

    static void WriteStreamInfo(FileStream fs, int blockSize, int sampleRate, int channels, int bps, long totalSamples, byte[] md5) {
        // Metadata block header: Last=1 (0x80) | Type=0 -> 0x80, length=34 (0x000022)
        byte[] header = [0x80, 0x00, 0x00, 34];
        fs.Write(header);

        byte[] info = new byte[34];
        // min/max block size (equal for fixed blocksize stream)
        info[0] = (byte)(blockSize >> 8);
        info[1] = (byte)(blockSize & 0xFF);
        info[2] = info[0];
        info[3] = info[1];
        // min/max frame size (0 = unknown)
        // sampleRate (20 bits) | channels-1 (3 bits) | bps-1 (5 bits) | totalSamples (36 bits) = 8 bytes (info[10..17])
        ulong packed = ((ulong)(sampleRate & 0xFFFFF) << 44)
                     | ((ulong)((channels - 1) & 0x7) << 41)
                     | ((ulong)((bps - 1) & 0x1F) << 36)
                     | ((ulong)totalSamples & 0xFFFFFFFFFUL);
        for (int i = 0; i < 8; i++) {
            info[10 + i] = (byte)((packed >> ((7 - i) * 8)) & 0xFF);
        }
        Buffer.BlockCopy(md5, 0, info, 18, 16);
        fs.Write(info);
    }

    static void WriteFrame(BitWriter bw, int[][] channelsData, int blockSize, int channels, int sampleRate, int bps, long frameNumber) {
        bw.ResetFrameCrc();

        // Sync code 0x3FFE (14 bits) + reserved 0 (1 bit) + blocking strategy 0 (fixed, 1 bit) = 0xFFF8
        bw.WriteBits(0xFFF8, 16);

        int bsCode = blockSize == 4096 ? 0b1100 : 0b0111; // 0111 => 16-bit (blockSize - 1) follows
        int srCode = GetSampleRateCode(sampleRate);
        bw.WriteBits((uint)((bsCode << 4) | srCode), 8);

        int chAssign = channels - 1; // Independent channels 0..7
        int ssCode = bps == 16 ? 0b100 : bps == 24 ? 0b110 : 0b000;
        bw.WriteBits((uint)((chAssign << 4) | (ssCode << 1) | 0), 8);

        bw.WriteUtf8Number((ulong)frameNumber);
        if (bsCode == 0b0111) {
            bw.WriteBits((uint)(blockSize - 1), 16);
        }

        byte crc8 = bw.CurrentCrc8;
        bw.WriteBits(crc8, 8);

        for (int c = 0; c < channels; c++) {
            WriteSubframe(bw, channelsData[c], blockSize, bps);
        }

        bw.AlignToByte();
        ushort crc16 = bw.CurrentCrc16;
        bw.WriteRawByte((byte)(crc16 >> 8));
        bw.WriteRawByte((byte)(crc16 & 0xFF));
    }

    static void WriteSubframe(BitWriter bw, int[] samples, int n, int bps) {
        int first = samples[0];
        bool allConst = true;
        for (int i = 1; i < n; i++) {
            if (samples[i] != first) {
                allConst = false;
                break;
            }
        }
        if (allConst) {
            bw.WriteBits(0b0_00000_0, 8); // Constant subframe, no wasted bits
            bw.WriteSignedBits(first, bps);
            return;
        }

        // Try Fixed Order 1 with Rice partition (order 0) if residuals stay comfortably bounded
        if (n >= 4 && TryEstimateFixedOrder1(samples, n, out int riceParam, out int[] residuals)) {
            // Subframe type: FIXED order 1 -> 0b001001 => byte = 0b0_001001_0 = 0x12
            bw.WriteBits(0b0_001001_0, 8);
            bw.WriteSignedBits(samples[0], bps);
            // Residual coding method 00 (4-bit rice parameter) + partition order 0000 (4 bits) -> 6 bits total
            bw.WriteBits(0, 2); // 4-bit rice
            bw.WriteBits(0, 4); // partition order = 0 (single partition of n - 1 residuals)
            bw.WriteBits((uint)riceParam, 4);
            for (int i = 1; i < n; i++) {
                bw.WriteRiceSigned(residuals[i], riceParam);
            }
            return;
        }

        // Verbatim subframe: 0b0_000001_0 = 0x02
        bw.WriteBits(0b0_000001_0, 8);
        for (int i = 0; i < n; i++) {
            bw.WriteSignedBits(samples[i], bps);
        }
    }

    static bool TryEstimateFixedOrder1(int[] samples, int n, out int bestParam, out int[] residuals) {
        residuals = new int[n];
        long absSum = 0;
        for (int i = 1; i < n; i++) {
            int r = samples[i] - samples[i - 1];
            residuals[i] = r;
            absSum += Math.Abs((long)r);
            if (Math.Abs(r) > (1 << 20)) {
                bestParam = 0;
                return false;
            }
        }
        double meanAbs = absSum / (double)Math.Max(1, n - 1);
        int k = 0;
        while (k < 14 && (1 << k) < meanAbs) {
            k++;
        }
        bestParam = k;
        // Check unary quotient won't explode compared to verbatim
        long estBits = (n - 1L) * (k + 2);
        long maxUnary = 0;
        for (int i = 1; i < n; i++) {
            uint zig = (uint)(residuals[i] >= 0 ? (residuals[i] << 1) : ((-residuals[i] << 1) - 1));
            long q = zig >> k;
            if (q > 4096) {
                return false;
            }
            maxUnary += q;
        }
        return (estBits + maxUnary) < (n * 15L);
    }

    static int GetSampleRateCode(int sampleRate) => sampleRate switch {
        88200 => 0b0001,
        176400 => 0b0010,
        192000 => 0b0011,
        8000 => 0b0100,
        16000 => 0b0101,
        22050 => 0b0110,
        24000 => 0b0111,
        32000 => 0b1000,
        44100 => 0b1001,
        48000 => 0b1010,
        96000 => 0b1011,
        _ => 0b0000 // Use STREAMINFO
    };

    sealed class BitWriter(Stream stream) {
        static readonly byte[] Crc8Table = BuildCrc8Table();
        static readonly ushort[] Crc16Table = BuildCrc16Table();

        ulong bitBuf;
        int bitCount;
        byte crc8;
        ushort crc16;

        public byte CurrentCrc8 => crc8;
        public ushort CurrentCrc16 => crc16;

        public void ResetFrameCrc() {
            bitBuf = 0;
            bitCount = 0;
            crc8 = 0;
            crc16 = 0;
        }

        public void WriteBits(uint value, int count) {
            ulong masked = count == 32 ? value : value & ((1UL << count) - 1);
            bitBuf = (bitBuf << count) | masked;
            bitCount += count;
            while (bitCount >= 8) {
                int shift = bitCount - 8;
                byte b = (byte)(bitBuf >> shift);
                bitCount -= 8;
                bitBuf &= (1UL << bitCount) - 1;
                EmitByte(b);
            }
        }

        public void WriteSignedBits(int value, int bits) {
            WriteBits((uint)value, bits);
        }

        public void WriteRiceSigned(int val, int k) {
            uint zig = (uint)(val >= 0 ? (val << 1) : ((-val << 1) - 1));
            uint q = zig >> k;
            while (q >= 32) {
                WriteBits(0, 32);
                q -= 32;
            }
            if (q > 0) {
                WriteBits(0, (int)q);
            }
            WriteBits(1, 1);
            if (k > 0) {
                WriteBits(zig, k);
            }
        }

        public void WriteUtf8Number(ulong v) {
            if (v < 0x80) {
                WriteBits((uint)v, 8);
                return;
            }
            int len = v < 0x800 ? 2 : v < 0x10000 ? 3 : v < 0x200000 ? 4 : v < 0x4000000 ? 5 : 6;
            byte first = (byte)((0xFF << (8 - len)) | (int)(v >> ((len - 1) * 6)));
            WriteBits(first, 8);
            for (int i = len - 2; i >= 0; i--) {
                byte cont = (byte)(0x80 | ((v >> (i * 6)) & 0x3F));
                WriteBits(cont, 8);
            }
        }

        public void AlignToByte() {
            if (bitCount > 0) {
                WriteBits(0, 8 - bitCount);
            }
        }

        public void WriteRawByte(byte b) {
            stream.WriteByte(b);
        }

        void EmitByte(byte b) {
            stream.WriteByte(b);
            crc8 = Crc8Table[crc8 ^ b];
            crc16 = (ushort)((crc16 << 8) ^ Crc16Table[(crc16 >> 8) ^ b]);
        }

        static byte[] BuildCrc8Table() {
            byte[] t = new byte[256];
            for (int i = 0; i < 256; i++) {
                int c = i;
                for (int b = 0; b < 8; b++) {
                    c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) : (c << 1);
                }
                t[i] = (byte)c;
            }
            return t;
        }

        static ushort[] BuildCrc16Table() {
            ushort[] t = new ushort[256];
            for (int i = 0; i < 256; i++) {
                int c = i << 8;
                for (int b = 0; b < 8; b++) {
                    c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) : (c << 1);
                }
                t[i] = (ushort)c;
            }
            return t;
        }
    }
}
