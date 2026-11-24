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
        int bps = (reader.Bits == BitDepth.Int24 || reader.Bits == BitDepth.Float32) ? 24 : 16;
        long totalSamples = reader.Length;
        int blockSize = (int)Math.Min(DefaultBlockSize, Math.Max(16, totalSamples));

        using FlacStreamingEncoder encoder = new(flacPath, channels, sampleRate, bps, blockSize);
        float[] interleaved = new float[blockSize * channels];
        long remaining = totalSamples;
        while (remaining > 0) {
            int n = (int)Math.Min(blockSize, remaining);
            reader.ReadBlock(interleaved, 0, (long)n * channels);
            encoder.WriteInterleaved(interleaved, 0, n);
            remaining -= n;
        }
        encoder.Complete();
    }

    internal static void WriteStreamInfo(
        Stream stream,
        int minBlockSize,
        int maxBlockSize,
        int sampleRate,
        int channels,
        int bps,
        long totalSamples,
        byte[] md5) {
        // Metadata block header: Last=1 (0x80) | Type=0 -> 0x80, length=34 (0x000022)
        byte[] header = [0x80, 0x00, 0x00, 34];
        stream.Write(header);

        byte[] info = new byte[34];
        info[0] = (byte)(minBlockSize >> 8);
        info[1] = (byte)(minBlockSize & 0xFF);
        info[2] = (byte)(maxBlockSize >> 8);
        info[3] = (byte)(maxBlockSize & 0xFF);
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
        stream.Write(info);
    }

    internal static void EncodeFrame(
        BitWriter bw,
        int[][] channelsData,
        int blockSize,
        int channels,
        int sampleRate,
        int bps,
        long frameNumber,
        int[] residualScratch) {
        WriteFrame(bw, channelsData, blockSize, channels, sampleRate, bps, frameNumber, residualScratch);
    }

    static void WriteFrame(BitWriter bw, int[][] channelsData, int blockSize, int channels, int sampleRate, int bps, long frameNumber, int[] residualScratch) {
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
            WriteSubframe(bw, channelsData[c], blockSize, bps, residualScratch);
        }

        bw.AlignToByte();
        ushort crc16 = bw.CurrentCrc16;
        bw.WriteRawByte((byte)(crc16 >> 8));
        bw.WriteRawByte((byte)(crc16 & 0xFF));
    }

    static void WriteSubframe(BitWriter bw, int[] samples, int n, int bps, int[] residualScratch) {
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
        if (n >= 4 && TryEstimateFixedOrder1(samples, n, residualScratch, out int riceParam)) {
            // Subframe type: FIXED order 1 -> 0b001001 => byte = 0b0_001001_0 = 0x12
            bw.WriteBits(0b0_001001_0, 8);
            bw.WriteSignedBits(samples[0], bps);
            // Residual coding method 00 (4-bit rice parameter) + partition order 0000 (4 bits) -> 6 bits total
            bw.WriteBits(0, 2); // 4-bit rice
            bw.WriteBits(0, 4); // partition order = 0 (single partition of n - 1 residuals)
            bw.WriteBits((uint)riceParam, 4);
            for (int i = 1; i < n; i++) {
                bw.WriteRiceSigned(residualScratch[i], riceParam);
            }
            return;
        }

        // Verbatim subframe: 0b0_000001_0 = 0x02
        bw.WriteBits(0b0_000001_0, 8);
        for (int i = 0; i < n; i++) {
            bw.WriteSignedBits(samples[i], bps);
        }
    }

    static bool TryEstimateFixedOrder1(int[] samples, int n, int[] residuals, out int bestParam) {
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

    internal sealed class BitWriter(Stream stream) {
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

/// <summary>
/// Chunked zero-temp-WAV streaming FLAC encoder supporting 1–8 channels and 16/24-bit PCM.
/// Accumulates incoming interleaved blocks into fixed-size FLAC frames and backpatches <c>STREAMINFO</c> on completion.
/// </summary>
public sealed class FlacStreamingEncoder : IDisposable {
    readonly Stream outStream;
    readonly bool ownsStream;
    readonly long streamInfoPos;
    readonly int channels;
    readonly int sampleRate;
    readonly int bps;
    readonly int blockSize;
    readonly int[][] chSamples;
    readonly byte[] md5Chunk;
    readonly int[] residualScratch;
    readonly IncrementalHash md5;
    readonly FlacAudioConverter.BitWriter bitWriter;
    int bufferedFrames;
    int minBlockSizeSeen = int.MaxValue;
    int maxBlockSizeSeen;
    long frameNumber;
    long totalFramesWritten;
    bool completed;
    bool disposed;

    /// <summary>Total frames written so far (including buffered frames once flushed).</summary>
    public long FramesWritten => totalFramesWritten;

    /// <summary>Creates a streaming FLAC encoder writing directly to <paramref name="flacPath"/>.</summary>
    public FlacStreamingEncoder(string flacPath, int channels, int sampleRate, int bitsPerSample = 24, int blockSize = 4096)
        : this(CreateOutputStream(flacPath), ownsStream: true, channels, sampleRate, bitsPerSample, blockSize) { }

    /// <summary>Creates a streaming FLAC encoder writing to <paramref name="outputStream"/>.</summary>
    public FlacStreamingEncoder(Stream outputStream, bool ownsStream, int channels, int sampleRate, int bitsPerSample = 24, int blockSize = 4096) {
        outStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        this.ownsStream = ownsStream;
        if (channels < 1 || channels > 8) {
            throw new NotSupportedException($"FLAC supports 1 to 8 channels (got {channels}).");
        }
        if (sampleRate <= 0) {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        this.channels = channels;
        this.sampleRate = sampleRate;
        bps = bitsPerSample >= 24 ? 24 : 16;
        this.blockSize = Math.Clamp(blockSize, 16, 4096);

        chSamples = new int[channels][];
        for (int c = 0; c < channels; c++) {
            chSamples[c] = new int[this.blockSize];
        }
        md5Chunk = new byte[this.blockSize * channels * (bps / 8)];
        residualScratch = new int[this.blockSize];
        md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        bitWriter = new FlacAudioConverter.BitWriter(outStream);

        outStream.Write("fLaC"u8);
        streamInfoPos = outStream.Position;
        outStream.Write(new byte[38]);
    }

    static FileStream CreateOutputStream(string flacPath) {
        if (string.IsNullOrWhiteSpace(flacPath)) {
            throw new ArgumentException("Output FLAC path cannot be empty.", nameof(flacPath));
        }
        string dir = Path.GetDirectoryName(flacPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }
        return File.Create(flacPath);
    }

    /// <summary>Appends <paramref name="frameCount"/> interleaved float frames starting at <paramref name="frameOffset"/>.</summary>
    public void WriteInterleaved(float[] interleaved, int frameOffset, int frameCount) {
        if (disposed || completed) {
            throw new ObjectDisposedException(nameof(FlacStreamingEncoder));
        }
        if (interleaved == null || frameCount <= 0) {
            return;
        }

        int remaining = frameCount;
        int srcFrame = frameOffset;
        while (remaining > 0) {
            int space = blockSize - bufferedFrames;
            int take = Math.Min(space, remaining);
            for (int s = 0; s < take; s++) {
                int baseIdx = (srcFrame + s) * channels;
                int dstIdx = bufferedFrames + s;
                for (int c = 0; c < channels; c++) {
                    float raw = baseIdx + c < interleaved.Length ? interleaved[baseIdx + c] : 0f;
                    float f = float.IsFinite(raw) ? Math.Clamp(raw, -1f, 1f) : 0f;
                    int q = bps == 16
                        ? Math.Clamp((int)Math.Round(f * 32767f), short.MinValue, short.MaxValue)
                        : Math.Clamp((int)Math.Round(f * 8388607f), -8388608, 8388607);
                    chSamples[c][dstIdx] = q;
                }
            }
            bufferedFrames += take;
            srcFrame += take;
            remaining -= take;
            totalFramesWritten += take;

            if (bufferedFrames == blockSize) {
                FlushBufferedBlock();
            }
        }
    }

    /// <summary>Flushes any trailing partial block and backpatches <c>STREAMINFO</c> (sample count + MD5).</summary>
    public void Complete() {
        if (completed || disposed) {
            return;
        }
        completed = true;
        if (bufferedFrames > 0) {
            FlushBufferedBlock();
        }
        byte[] digest = md5.GetHashAndReset();
        int minBs = minBlockSizeSeen == int.MaxValue ? blockSize : Math.Max(16, minBlockSizeSeen);
        int maxBs = maxBlockSizeSeen > 0 ? maxBlockSizeSeen : blockSize;
        if (minBs > maxBs) {
            minBs = maxBs;
        }
        if (outStream.CanSeek) {
            long endPos = outStream.Position;
            outStream.Position = streamInfoPos;
            FlacAudioConverter.WriteStreamInfo(outStream, minBs, maxBs, sampleRate, channels, bps, totalFramesWritten, digest);
            outStream.Position = endPos;
        }
        outStream.Flush();
    }

    void FlushBufferedBlock() {
        int n = bufferedFrames;
        int md5Bytes = 0;
        if (bps == 16) {
            for (int s = 0; s < n; s++) {
                for (int c = 0; c < channels; c++) {
                    int val = chSamples[c][s];
                    md5Chunk[md5Bytes++] = (byte)(val & 0xFF);
                    md5Chunk[md5Bytes++] = (byte)((val >> 8) & 0xFF);
                }
            }
        } else {
            for (int s = 0; s < n; s++) {
                for (int c = 0; c < channels; c++) {
                    int val = chSamples[c][s];
                    md5Chunk[md5Bytes++] = (byte)(val & 0xFF);
                    md5Chunk[md5Bytes++] = (byte)((val >> 8) & 0xFF);
                    md5Chunk[md5Bytes++] = (byte)((val >> 16) & 0xFF);
                }
            }
        }
        md5.AppendData(md5Chunk, 0, md5Bytes);
        FlacAudioConverter.EncodeFrame(bitWriter, chSamples, n, channels, sampleRate, bps, frameNumber++, residualScratch);
        if (n < minBlockSizeSeen) {
            minBlockSizeSeen = n;
        }
        if (n > maxBlockSizeSeen) {
            maxBlockSizeSeen = n;
        }
        bufferedFrames = 0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (disposed) {
            return;
        }
        try {
            Complete();
        } finally {
            disposed = true;
            md5.Dispose();
            if (ownsStream) {
                outStream.Dispose();
            }
        }
    }
}
