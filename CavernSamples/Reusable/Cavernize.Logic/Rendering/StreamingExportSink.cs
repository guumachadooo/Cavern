using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

using Cavern.Channels;
using Cavern.Format;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Commercial compression tiers for Studio spatial exports.
/// </summary>
public enum StudioCompressionTier {
    /// <summary>Uncompressed studio master PCM (<c>.wav</c>, <c>.amb</c>, or <c>.iamf</c> LPCM).</summary>
    MasterPcm = 0,
    /// <summary>Lossless compressed container (<c>.flac</c> up to 8ch, <c>.wv</c> WavPack for 12ch/16ch, or IAMF FLAC).</summary>
    LosslessCompressed = 1,
    /// <summary>Portable lossy delivery (<c>.opus</c> RFC 8486, <c>.m4a</c> AAC, or IAMF Opus).</summary>
    PortableLossy = 2
}

/// <summary>
/// Zero-temp-WAV chunked streaming export sink writing bounded interleaved PCM blocks
/// (<c>&lt;= 4096</c> frames, <c>&lt;= 256 KB</c> per block) directly to disk or encoder stdin pipes.
/// </summary>
public interface IStreamingExportSink : IDisposable {
    /// <summary>Writes <paramref name="frameCount"/> interleaved float frames (<c>frameCount * ChannelCount</c> floats).</summary>
    void WriteInterleavedBlock(float[] interleaved, int frameCount);

    /// <summary>Output channel count.</summary>
    int ChannelCount { get; }

    /// <summary>Output sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Total frames written to this sink.</summary>
    long FramesWritten { get; }
}

/// <summary>
/// Target codec for <see cref="FfmpegStdinPipeSink"/>.
/// </summary>
public enum FfmpegPipeCodec {
    /// <summary>Lossless WavPack (<c>.wv</c>) supporting up to 256 channels (12ch 7.1.4 &amp; 16ch HOA).</summary>
    WavPack = 0,
    /// <summary>Opus (<c>.opus</c>) supporting stereo, 7.1.4, and RFC 8486 Ambisonics (<c>mapping_family</c> 2/3).</summary>
    Opus = 1,
    /// <summary>Fragmented AAC MP4 (<c>.m4a</c>) with <c>+frag_keyframe+empty_moov</c>.</summary>
    AacM4a = 2,
    /// <summary>FFmpeg FLAC (<c>.flac</c>).</summary>
    Flac = 3,
    /// <summary>AOMedia IAMF (<c>.iamf</c>) with lossless <c>fLaC</c> substreams.</summary>
    IamfFlac = 4,
    /// <summary>AOMedia IAMF (<c>.iamf</c>) with <c>libopus</c> substreams.</summary>
    IamfOpus = 5
}

internal static class StreamingExportBufferPool {
    public const int MaxBlockFrames = 4096;
    public const int MaxBlockBytes = 256 * 1024;
    const int MaxPooledPerSize = 8;

    static readonly object PoolLock = new();
    static readonly Dictionary<int, Stack<float[]>> FloatPools = new();
    static readonly Dictionary<int, Stack<byte[]>> BytePools = new();

    public static int ComputeMaxFramesForChannels(int channelCount, int bytesPerChannelSample) {
        int strideBytes = Math.Max(1, channelCount) * Math.Max(1, bytesPerChannelSample);
        int maxByBytes = Math.Max(1, MaxBlockBytes / strideBytes);
        return Math.Min(MaxBlockFrames, maxByBytes);
    }

    public static float[] RentFloatBlock(int minLength) {
        int cap = Math.Min(MaxBlockBytes / sizeof(float), Math.Max(1, minLength));
        lock (PoolLock) {
            if (FloatPools.TryGetValue(cap, out Stack<float[]> stack) && stack.Count > 0) {
                return stack.Pop();
            }
        }
        return new float[cap];
    }

    public static void ReturnFloatBlock(float[] buffer) {
        if (buffer == null || buffer.Length == 0 || buffer.Length > MaxBlockBytes / sizeof(float)) {
            return;
        }
        lock (PoolLock) {
            if (!FloatPools.TryGetValue(buffer.Length, out Stack<float[]> stack)) {
                stack = new Stack<float[]>(MaxPooledPerSize);
                FloatPools[buffer.Length] = stack;
            }
            if (stack.Count < MaxPooledPerSize) {
                stack.Push(buffer);
            }
        }
    }

    public static byte[] RentByteBlock(int minLength) {
        int cap = Math.Min(MaxBlockBytes, Math.Max(1, minLength));
        lock (PoolLock) {
            if (BytePools.TryGetValue(cap, out Stack<byte[]> stack) && stack.Count > 0) {
                return stack.Pop();
            }
        }
        return new byte[cap];
    }

    public static void ReturnByteBlock(byte[] buffer) {
        if (buffer == null || buffer.Length == 0 || buffer.Length > MaxBlockBytes) {
            return;
        }
        lock (PoolLock) {
            if (!BytePools.TryGetValue(buffer.Length, out Stack<byte[]> stack)) {
                stack = new Stack<byte[]>(MaxPooledPerSize);
                BytePools[buffer.Length] = stack;
            }
            if (stack.Count < MaxPooledPerSize) {
                stack.Push(buffer);
            }
        }
    }

    public static void SanitizeAndClamp(float[] src, int srcOffset, float[] dst, int sampleCount) {
        for (int i = 0; i < sampleCount; i++) {
            float v = src[srcOffset + i];
            dst[i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
        }
    }
}

/// <summary>
/// Streams interleaved PCM chunks directly to disk without temp WAV intermediates,
/// backpatching RIFF/RF64 (<c>ds64</c>) sizes deterministically on <see cref="Dispose"/>.
/// </summary>
public sealed class DirectPcmStreamingSink : IStreamingExportSink {
    const int RiffId = 0x46464952;
    const int Rf64Id = 0x34364652;
    const int WaveId = 0x45564157;
    const int JunkId = 0x4B4E554A;
    const int Ds64Id = 0x34367364;
    const int FmtId = 0x20746D66;
    const int DataId = 0x61746164;

    static readonly byte[] StandardGuidTail = [0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113];
    static readonly byte[] AmbisonicGuidTail = [0x00, 0x00, 0x21, 0x07, 0xD3, 0x11, 0x86, 0x44, 0xC8, 0xC1, 0xCA, 0x00, 0x00, 0x00];

    readonly FileStream stream;
    readonly BitDepth bitDepth;
    readonly int bytesPerSample;
    readonly int maxChunkFrames;
    readonly long junkChunkOffset;
    readonly long dataSizeOffset;
    readonly bool reservedRf64Slot;
    bool disposed;

    /// <inheritdoc/>
    public int ChannelCount { get; }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public long FramesWritten { get; private set; }

    /// <summary>Creates a direct-to-disk RIFF/RF64 WAV streaming sink.</summary>
    public DirectPcmStreamingSink(
        string outputPath,
        int channelCount,
        int sampleRate,
        BitDepth bitDepth = BitDepth.Int24,
        ReferenceChannel[] layout = null,
        long expectedFrames = 0,
        bool useAmbisonicSubFormatGuid = false) {
        if (string.IsNullOrWhiteSpace(outputPath)) {
            throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
        }
        if (channelCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }
        if (sampleRate <= 0) {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        string dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        ChannelCount = channelCount;
        SampleRate = sampleRate;
        this.bitDepth = bitDepth;
        bytesPerSample = Math.Max(1, (int)bitDepth / 8);
        maxChunkFrames = StreamingExportBufferPool.ComputeMaxFramesForChannels(channelCount, Math.Max(sizeof(float), bytesPerSample));

        int channelMask = layout != null ? BuildChannelMask(layout) : -1;
        bool useExtensible = channelMask != -1 || useAmbisonicSubFormatGuid;
        long expectedDataBytes = expectedFrames > 0 ? expectedFrames * channelCount * bytesPerSample : 0;
        // Reserve a 28-byte JUNK placeholder when length is unknown or potentially >= 4 GB so RF64 upgrade is in-place.
        reservedRf64Slot = expectedFrames <= 0 || expectedDataBytes >= 0xF0000000L;

        stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.SequentialScan);
        WritePlaceholderHeader(useExtensible, useAmbisonicSubFormatGuid, channelMask, out junkChunkOffset, out dataSizeOffset);
    }

    void WritePlaceholderHeader(
        bool useExtensible,
        bool useAmbisonicGuid,
        int channelMask,
        out long junkOffset,
        out long dataSizePos) {
        Span<byte> hdr = stackalloc byte[128];
        int pos = 0;

        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), RiffId); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(pos, 4), 0); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), WaveId); pos += 4;

        junkOffset = -1;
        if (reservedRf64Slot) {
            junkOffset = pos;
            BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), JunkId); pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), 28); pos += 4;
            hdr.Slice(pos, 28).Clear(); pos += 28;
        }

        int fmtLen = useExtensible ? 40 : 16;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), FmtId); pos += 4;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), fmtLen); pos += 4;

        short baseFmt = useExtensible ? (short)-2 : (bitDepth == BitDepth.Float32 ? (short)3 : (short)1);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), baseFmt); pos += 2;
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), (short)ChannelCount); pos += 2;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), SampleRate); pos += 4;
        int blockAlign = ChannelCount * bytesPerSample;
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), SampleRate * blockAlign); pos += 4;
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), (short)blockAlign); pos += 2;
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), (short)bitDepth); pos += 2;

        if (useExtensible) {
            BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), 22); pos += 2;
            BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), (short)bitDepth); pos += 2;
            BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), useAmbisonicGuid ? 0 : channelMask); pos += 4;
            BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(pos, 2), bitDepth == BitDepth.Float32 ? (short)3 : (short)1); pos += 2;
            byte[] tail = useAmbisonicGuid ? AmbisonicGuidTail : StandardGuidTail;
            tail.CopyTo(hdr.Slice(pos, 14));
            pos += 14;
        }

        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(pos, 4), DataId); pos += 4;
        dataSizePos = pos;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(pos, 4), 0); pos += 4;

        stream.Write(hdr.Slice(0, pos));
    }

    /// <inheritdoc/>
    public void WriteInterleavedBlock(float[] interleaved, int frameCount) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (interleaved == null || frameCount <= 0) {
            return;
        }

        int offsetFrames = 0;
        while (offsetFrames < frameCount) {
            int chunkFrames = Math.Min(maxChunkFrames, frameCount - offsetFrames);
            int sampleCount = chunkFrames * ChannelCount;
            int byteCount = sampleCount * bytesPerSample;
            float[] clean = StreamingExportBufferPool.RentFloatBlock(sampleCount);
            byte[] pcmBytes = StreamingExportBufferPool.RentByteBlock(byteCount);
            try {
                StreamingExportBufferPool.SanitizeAndClamp(interleaved, offsetFrames * ChannelCount, clean, sampleCount);
                EncodePcm(clean, sampleCount, pcmBytes);
                stream.Write(pcmBytes, 0, byteCount);
                FramesWritten += chunkFrames;
                offsetFrames += chunkFrames;
            } finally {
                StreamingExportBufferPool.ReturnFloatBlock(clean);
                StreamingExportBufferPool.ReturnByteBlock(pcmBytes);
            }
        }
    }

    void EncodePcm(float[] samples, int sampleCount, byte[] dst) {
        switch (bitDepth) {
            case BitDepth.Int8:
                for (int i = 0; i < sampleCount; i++) {
                    int val = Math.Clamp((int)Math.Round(samples[i] * 127f), sbyte.MinValue, sbyte.MaxValue);
                    dst[i] = (byte)(sbyte)val;
                }
                break;
            case BitDepth.Int16:
                for (int i = 0; i < sampleCount; i++) {
                    short val = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
                    dst[2 * i] = (byte)(val & 0xFF);
                    dst[2 * i + 1] = (byte)((val >> 8) & 0xFF);
                }
                break;
            case BitDepth.Int24:
                for (int i = 0; i < sampleCount; i++) {
                    int val = Math.Clamp((int)Math.Round(samples[i] * 8388607.0), -8388608, 8388607);
                    dst[3 * i] = (byte)(val & 0xFF);
                    dst[3 * i + 1] = (byte)((val >> 8) & 0xFF);
                    dst[3 * i + 2] = (byte)((val >> 16) & 0xFF);
                }
                break;
            case BitDepth.Float32:
                for (int i = 0; i < sampleCount; i++) {
                    int bits = BitConverter.SingleToInt32Bits(samples[i]);
                    dst[4 * i] = (byte)(bits & 0xFF);
                    dst[4 * i + 1] = (byte)((bits >> 8) & 0xFF);
                    dst[4 * i + 2] = (byte)((bits >> 16) & 0xFF);
                    dst[4 * i + 3] = (byte)((bits >> 24) & 0xFF);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported PCM bit depth: {bitDepth}");
        }
    }

    /// <summary>Backpatches RIFF/RF64 header sizes and closes the output stream.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        try {
            if (stream.CanWrite && stream.CanSeek) {
                long dataBytes = FramesWritten * ChannelCount * bytesPerSample;
                long contentSize = stream.Position - 8;
                Span<byte> b4 = stackalloc byte[4];

                if (contentSize > uint.MaxValue || dataBytes > uint.MaxValue) {
                    if (reservedRf64Slot && junkChunkOffset >= 0) {
                        stream.Position = 0;
                        BinaryPrimitives.WriteInt32LittleEndian(b4, Rf64Id); stream.Write(b4);
                        BinaryPrimitives.WriteUInt32LittleEndian(b4, 0xFFFFFFFFu); stream.Write(b4);

                        stream.Position = junkChunkOffset;
                        Span<byte> ds64 = stackalloc byte[36];
                        BinaryPrimitives.WriteInt32LittleEndian(ds64[..4], Ds64Id);
                        BinaryPrimitives.WriteInt32LittleEndian(ds64.Slice(4, 4), 28);
                        BinaryPrimitives.WriteInt64LittleEndian(ds64.Slice(8, 8), contentSize);
                        BinaryPrimitives.WriteInt64LittleEndian(ds64.Slice(16, 8), dataBytes);
                        BinaryPrimitives.WriteInt64LittleEndian(ds64.Slice(24, 8), FramesWritten);
                        BinaryPrimitives.WriteInt32LittleEndian(ds64.Slice(32, 4), 0);
                        stream.Write(ds64);

                        stream.Position = dataSizeOffset;
                        BinaryPrimitives.WriteUInt32LittleEndian(b4, 0xFFFFFFFFu); stream.Write(b4);
                    }
                } else {
                    stream.Position = 4;
                    BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)contentSize); stream.Write(b4);
                    stream.Position = dataSizeOffset;
                    BinaryPrimitives.WriteUInt32LittleEndian(b4, (uint)dataBytes); stream.Write(b4);
                }
                stream.Flush();
            }
        } finally {
            stream.Dispose();
        }
    }

    static int BuildChannelMask(ReferenceChannel[] channels) {
        if (channels == null || channels.Length == 0) {
            return -1;
        }
        int mask = 0;
        int prevBit = 0;
        for (int i = 0; i < channels.Length; i++) {
            int bit = channels[i] switch {
                ReferenceChannel.FrontLeft => 0x1,
                ReferenceChannel.FrontRight => 0x2,
                ReferenceChannel.FrontCenter => 0x4,
                ReferenceChannel.ScreenLFE => 0x8,
                ReferenceChannel.RearLeft => 0x10,
                ReferenceChannel.RearRight => 0x20,
                ReferenceChannel.FrontLeftCenter => 0x40,
                ReferenceChannel.FrontRightCenter => 0x80,
                ReferenceChannel.RearCenter => 0x100,
                ReferenceChannel.SideLeft => 0x200,
                ReferenceChannel.SideRight => 0x400,
                ReferenceChannel.GodsVoice => 0x800,
                ReferenceChannel.TopFrontLeft => 0x1000,
                ReferenceChannel.TopFrontCenter => 0x2000,
                ReferenceChannel.TopFrontRight => 0x4000,
                ReferenceChannel.TopRearLeft => 0x8000,
                ReferenceChannel.TopRearCenter => 0x10000,
                ReferenceChannel.TopRearRight => 0x20000,
                _ => -1
            };
            if (bit <= 0 || (mask & bit) != 0 || bit < prevBit) {
                return -1;
            }
            mask |= bit;
            prevBit = bit;
        }
        return mask;
    }
}

/// <summary>
/// Streams 1–8 channel PCM blocks directly into <see cref="FlacStreamingEncoder"/> (<c>.flac</c>, 0 temp WAV).
/// </summary>
public sealed class DirectFlacStreamingSink : IStreamingExportSink {
    readonly FlacStreamingEncoder encoder;
    readonly int maxChunkFrames;
    bool disposed;

    /// <inheritdoc/>
    public int ChannelCount { get; }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public long FramesWritten => encoder.FramesWritten;

    /// <summary>Creates a direct streaming FLAC sink for 1–8 channels.</summary>
    public DirectFlacStreamingSink(string outputPath, int channelCount, int sampleRate, BitDepth bitDepth = BitDepth.Int24, int blockSize = 4096) {
        if (channelCount < 1 || channelCount > 8) {
            throw new NotSupportedException($"FLAC supports 1 to 8 channels (got {channelCount}). Use WavPack (.wv) for {channelCount}ch lossless.");
        }
        ChannelCount = channelCount;
        SampleRate = sampleRate;
        int bps = bitDepth == BitDepth.Int16 ? 16 : 24;
        maxChunkFrames = StreamingExportBufferPool.ComputeMaxFramesForChannels(channelCount, sizeof(float));
        encoder = new FlacStreamingEncoder(outputPath, channelCount, sampleRate, bps, Math.Min(4096, Math.Max(16, blockSize)));
    }

    /// <inheritdoc/>
    public void WriteInterleavedBlock(float[] interleaved, int frameCount) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (interleaved == null || frameCount <= 0) {
            return;
        }
        int offsetFrames = 0;
        while (offsetFrames < frameCount) {
            int chunkFrames = Math.Min(maxChunkFrames, frameCount - offsetFrames);
            int sampleCount = chunkFrames * ChannelCount;
            float[] clean = StreamingExportBufferPool.RentFloatBlock(sampleCount);
            try {
                StreamingExportBufferPool.SanitizeAndClamp(interleaved, offsetFrames * ChannelCount, clean, sampleCount);
                encoder.WriteInterleaved(clean, 0, chunkFrames);
                offsetFrames += chunkFrames;
            } finally {
                StreamingExportBufferPool.ReturnFloatBlock(clean);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        encoder.Dispose();
    }
}

/// <summary>
/// Streams sanitized <c>f32le</c> PCM frames directly into <c>libffmpeg.so</c> <c>stdin</c> (<c>pipe:0</c>)
/// with <c>SIGPIPE</c> shielding and async bounded stdout/stderr ring-buffer draining (never deadlocks).
/// Supports WavPack (<c>.wv</c>), RFC 8486 Opus (<c>.opus</c>), fragmented AAC (<c>.m4a</c>), FLAC (<c>.flac</c>),
/// and multi-mono IAMF (<c>-f iamf</c>).
/// </summary>
public sealed class FfmpegStdinPipeSink : IStreamingExportSink {
    const int MaxStderrLines = 64;
    const int MaxStderrChars = 16384;

    readonly Process process;
    readonly Stream stdinStream;
    readonly Task stdoutDrainTask;
    readonly Task stderrDrainTask;
    readonly Queue<string> stderrRing = new(MaxStderrLines);
    readonly object stderrLock = new();
    readonly int maxChunkFrames;
    readonly string tempMetadataPath;
    readonly string tempCoverPath;
    int stderrCharCount;
    bool disposed;

    /// <inheritdoc/>
    public int ChannelCount { get; }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <inheritdoc/>
    public long FramesWritten { get; private set; }

    /// <summary>Last captured FFmpeg stderr tail lines for diagnostics.</summary>
    public string StderrTail {
        get {
            lock (stderrLock) {
                return string.Join(Environment.NewLine, stderrRing);
            }
        }
    }

    /// <summary>Creates a streaming FFmpeg stdin pipe sink.</summary>
    public FfmpegStdinPipeSink(
        string ffmpegExecutablePath,
        string outputPath,
        int channelCount,
        int sampleRate,
        FfmpegPipeCodec codec,
        Action onSigpipeShield = null,
        bool isAmbisonics = false,
        int opusMappingFamily = -1,
        int bitrateKbps = 0,
        ExportMetadataPackage metadata = null) {
        if (string.IsNullOrWhiteSpace(ffmpegExecutablePath) || !File.Exists(ffmpegExecutablePath)) {
            throw new FileNotFoundException("FFmpeg executable is unavailable for streaming pipe export.", ffmpegExecutablePath);
        }
        if (string.IsNullOrWhiteSpace(outputPath)) {
            throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
        }
        if (channelCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }
        if (sampleRate <= 0) {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        string dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        onSigpipeShield?.Invoke();

        ChannelCount = channelCount;
        SampleRate = sampleRate;
        maxChunkFrames = StreamingExportBufferPool.ComputeMaxFramesForChannels(channelCount, sizeof(float));

        bool supportsFfMeta = codec is FfmpegPipeCodec.Opus or FfmpegPipeCodec.WavPack or FfmpegPipeCodec.AacM4a or FfmpegPipeCodec.Flac;
        if (supportsFfMeta && metadata != null && metadata.HasAnythingToEmbed) {
            try {
                tempMetadataPath = ExportMetadataEmbedder.WriteFfmpegMetadataFile(
                    metadata,
                    includeOpusBase64Picture: codec == FfmpegPipeCodec.Opus,
                    tempDirectory: dir);
                if (codec == FfmpegPipeCodec.AacM4a &&
                    metadata.HasCoverArt &&
                    metadata.CoverArtBytes.Length <= ExportMetadataEmbedder.MaxCoverArtBytes) {
                    string ext = string.Equals(metadata.CoverArtMimeType, "image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                    string coverDir = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : Path.GetTempPath();
                    tempCoverPath = Path.Combine(coverDir, $".cavern_cov_{Guid.NewGuid():N}{ext}");
                    File.WriteAllBytes(tempCoverPath, metadata.CoverArtBytes);
                }
            } catch {
                TryDeleteTempFile(tempMetadataPath);
                TryDeleteTempFile(tempCoverPath);
                tempMetadataPath = null;
                tempCoverPath = null;
            }
        }

        ProcessStartInfo psi = new() {
            FileName = ffmpegExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureLibraryPath(psi, ffmpegExecutablePath);
        BuildArguments(psi.ArgumentList, outputPath, channelCount, sampleRate, codec, isAmbisonics, opusMappingFamily, bitrateKbps, tempMetadataPath, tempCoverPath);

        try {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg pipe process.");
            stdinStream = process.StandardInput.BaseStream;
            stdoutDrainTask = DrainStreamAsync(process.StandardOutput, captureRing: false);
            stderrDrainTask = DrainStreamAsync(process.StandardError, captureRing: true);
        } catch {
            TryDeleteTempFile(tempMetadataPath);
            TryDeleteTempFile(tempCoverPath);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteInterleavedBlock(float[] interleaved, int frameCount) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (interleaved == null || frameCount <= 0) {
            return;
        }

        int offsetFrames = 0;
        while (offsetFrames < frameCount) {
            int chunkFrames = Math.Min(maxChunkFrames, frameCount - offsetFrames);
            int sampleCount = chunkFrames * ChannelCount;
            int byteCount = sampleCount * sizeof(float);
            float[] clean = StreamingExportBufferPool.RentFloatBlock(sampleCount);
            byte[] pcmBytes = StreamingExportBufferPool.RentByteBlock(byteCount);
            try {
                StreamingExportBufferPool.SanitizeAndClamp(interleaved, offsetFrames * ChannelCount, clean, sampleCount);
                for (int i = 0; i < sampleCount; i++) {
                    int bits = BitConverter.SingleToInt32Bits(clean[i]);
                    int b = i * 4;
                    pcmBytes[b] = (byte)(bits & 0xFF);
                    pcmBytes[b + 1] = (byte)((bits >> 8) & 0xFF);
                    pcmBytes[b + 2] = (byte)((bits >> 16) & 0xFF);
                    pcmBytes[b + 3] = (byte)((bits >> 24) & 0xFF);
                }
                stdinStream.Write(pcmBytes, 0, byteCount);
                FramesWritten += chunkFrames;
                offsetFrames += chunkFrames;
            } catch (IOException ex) {
                throw new IOException($"FFmpeg stdin pipe broken: {ex.Message}. {StderrTail}", ex);
            } finally {
                StreamingExportBufferPool.ReturnFloatBlock(clean);
                StreamingExportBufferPool.ReturnByteBlock(pcmBytes);
            }
        }
    }

    /// <summary>
    /// Muxes pre-rendered mono WAV/PCM channel files into an AOMedia IAMF container using
    /// <c>-f iamf</c> and explicit <c>-streamid 0:0 -streamid 1:1 ...</c> mapping.
    /// </summary>
    public static void MuxMonoStreamsToIamf(
        string ffmpegExecutablePath,
        IReadOnlyList<string> monoInputPaths,
        string outputIamfPath,
        bool useOpus,
        bool isAmbisonics,
        int bitrateKbps = 0,
        Action onSigpipeShield = null) {
        if (string.IsNullOrWhiteSpace(ffmpegExecutablePath) || !File.Exists(ffmpegExecutablePath)) {
            throw new FileNotFoundException("FFmpeg executable not found for IAMF muxing.", ffmpegExecutablePath);
        }
        if (monoInputPaths == null || monoInputPaths.Count == 0) {
            throw new ArgumentException("At least one mono stream is required for IAMF muxing.", nameof(monoInputPaths));
        }

        onSigpipeShield?.Invoke();
        ProcessStartInfo psi = new() {
            FileName = ffmpegExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureLibraryPath(psi, ffmpegExecutablePath);

        IList<string> args = psi.ArgumentList;
        args.Add("-y");
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("warning");
        for (int i = 0; i < monoInputPaths.Count; i++) {
            args.Add("-i");
            args.Add(monoInputPaths[i]);
        }
        for (int i = 0; i < monoInputPaths.Count; i++) {
            args.Add("-map");
            args.Add($"{i}:a:0");
            args.Add("-streamid");
            args.Add($"{i}:{i}");
        }

        StringBuilder elemGrp = new("type=iamf_audio_element:id=1:");
        elemGrp.Append(isAmbisonics ? "audio_element_type=scene_based:ambisonics_mode=mono" : "audio_element_type=channel");
        for (int i = 0; i < monoInputPaths.Count; i++) {
            elemGrp.Append(":st=").Append(i);
        }
        args.Add("-stream_group");
        args.Add(elemGrp.ToString());
        args.Add("-stream_group");
        args.Add("type=iamf_mix_presentation:id=2:stg=1");

        if (useOpus) {
            int safeBr = bitrateKbps > 0 ? bitrateKbps : Math.Clamp(monoInputPaths.Count * 48, 128, 1024);
            args.Add("-c:a");
            args.Add("libopus");
            args.Add("-b:a");
            args.Add($"{safeBr}k");
        } else {
            args.Add("-c:a");
            args.Add("flac");
        }
        args.Add("-f");
        args.Add("iamf");
        args.Add(outputIamfPath);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg IAMF muxer.");
        string stderr = proc.StandardError.ReadToEnd();
        _ = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0) {
            throw new IOException($"FFmpeg IAMF mux failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    static void BuildArguments(
        IList<string> args,
        string outputPath,
        int channelCount,
        int sampleRate,
        FfmpegPipeCodec codec,
        bool isAmbisonics,
        int opusMappingFamily,
        int bitrateKbps,
        string metadataFilePath = null,
        string coverArtFilePath = null) {
        args.Add("-y");
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("warning");
        args.Add("-f");
        args.Add("f32le");
        args.Add("-ar");
        args.Add(sampleRate.ToString());
        args.Add("-ac");
        args.Add(channelCount.ToString());
        args.Add("-i");
        args.Add("pipe:0");

        bool hasMetaInput = !string.IsNullOrWhiteSpace(metadataFilePath) && File.Exists(metadataFilePath);
        bool hasCoverInput = hasMetaInput &&
                             codec == FfmpegPipeCodec.AacM4a &&
                             !string.IsNullOrWhiteSpace(coverArtFilePath) &&
                             File.Exists(coverArtFilePath);
        if (hasMetaInput) {
            args.Add("-f");
            args.Add("ffmetadata");
            args.Add("-i");
            args.Add(metadataFilePath);
        }
        if (hasCoverInput) {
            args.Add("-i");
            args.Add(coverArtFilePath);
        }

        switch (codec) {
            case FfmpegPipeCodec.WavPack:
                if (hasMetaInput) {
                    args.Add("-map"); args.Add("0:a");
                    args.Add("-map_metadata"); args.Add("1");
                }
                args.Add("-c:a");
                args.Add("wavpack");
                args.Add("-compression_level");
                args.Add("1");
                args.Add(outputPath);
                break;

            case FfmpegPipeCodec.Opus: {
                int br = bitrateKbps > 0 ? bitrateKbps : Math.Clamp(channelCount * 64, 128, 1024);
                int mapFamily = opusMappingFamily >= 0
                    ? opusMappingFamily
                    : (isAmbisonics ? 2 : (channelCount > 8 ? 255 : -1));
                if (hasMetaInput) {
                    args.Add("-map"); args.Add("0:a");
                    args.Add("-map_metadata"); args.Add("1");
                }
                args.Add("-c:a");
                args.Add("libopus");
                args.Add("-b:a");
                args.Add($"{br}k");
                if (mapFamily >= 0) {
                    args.Add("-mapping_family");
                    args.Add(mapFamily.ToString());
                }
                args.Add(outputPath);
                break;
            }

            case FfmpegPipeCodec.AacM4a: {
                int br = bitrateKbps > 0 ? bitrateKbps : Math.Clamp(channelCount * 96, 192, 768);
                if (hasMetaInput) {
                    args.Add("-map"); args.Add("0:a");
                    if (hasCoverInput) {
                        args.Add("-map"); args.Add("2:v");
                    }
                    args.Add("-map_metadata"); args.Add("1");
                }
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add($"{br}k");
                if (hasCoverInput) {
                    args.Add("-c:v"); args.Add("copy");
                    args.Add("-disposition:v:0"); args.Add("attached_pic");
                }
                args.Add("-movflags");
                args.Add("+frag_keyframe+empty_moov");
                args.Add(outputPath);
                break;
            }

            case FfmpegPipeCodec.Flac:
                if (hasMetaInput) {
                    args.Add("-map"); args.Add("0:a");
                    args.Add("-map_metadata"); args.Add("1");
                }
                args.Add("-c:a");
                args.Add("flac");
                args.Add(outputPath);
                break;

            case FfmpegPipeCodec.IamfFlac:
            case FfmpegPipeCodec.IamfOpus: {
                StringBuilder split = new($"[0:a]channelsplit=channel_layout={channelCount}c");
                for (int i = 0; i < channelCount; i++) {
                    split.Append($"[c{i}]");
                }
                args.Add("-filter_complex");
                args.Add(split.ToString());
                for (int i = 0; i < channelCount; i++) {
                    args.Add("-map");
                    args.Add($"[c{i}]");
                    args.Add("-streamid");
                    args.Add($"{i}:{i}");
                }
                StringBuilder grp = new("type=iamf_audio_element:id=1:");
                grp.Append(isAmbisonics ? "audio_element_type=scene_based:ambisonics_mode=mono" : "audio_element_type=channel");
                for (int i = 0; i < channelCount; i++) {
                    grp.Append(":st=").Append(i);
                }
                args.Add("-stream_group");
                args.Add(grp.ToString());
                args.Add("-stream_group");
                args.Add("type=iamf_mix_presentation:id=2:stg=1");
                args.Add("-c:a");
                if (codec == FfmpegPipeCodec.IamfOpus) {
                    int br = bitrateKbps > 0 ? bitrateKbps : Math.Clamp(channelCount * 48, 128, 1024);
                    args.Add("libopus");
                    args.Add("-b:a");
                    args.Add($"{br}k");
                } else {
                    args.Add("flac");
                }
                args.Add("-f");
                args.Add("iamf");
                args.Add(outputPath);
                break;
            }

            default:
                throw new NotSupportedException($"Unsupported FFmpeg pipe codec: {codec}");
        }
    }

    static void ConfigureLibraryPath(ProcessStartInfo psi, string executablePath) {
        try {
            string libDir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(libDir) || !Directory.Exists(libDir)) {
                return;
            }
            string existing = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out string cur) ? cur : string.Empty;
            psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libDir : $"{libDir}:{existing}";
        } catch {
            // Best-effort environment setup
        }
    }

    async Task DrainStreamAsync(StreamReader reader, bool captureRing) {
        try {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
                if (!captureRing || string.IsNullOrEmpty(line)) {
                    continue;
                }
                lock (stderrLock) {
                    stderrRing.Enqueue(line);
                    stderrCharCount += line.Length + 1;
                    while (stderrRing.Count > MaxStderrLines || stderrCharCount > MaxStderrChars) {
                        if (stderrRing.Count == 0) {
                            break;
                        }
                        string dropped = stderrRing.Dequeue();
                        stderrCharCount -= dropped.Length + 1;
                    }
                }
            }
        } catch {
            // Process exited or stream closed
        }
    }

    /// <summary>Closes stdin so FFmpeg finalizes the container, drains stdout/stderr, and verifies exit code 0.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;

        Exception finalizeError = null;
        try {
            try {
                stdinStream.Flush();
            } catch {
                // Pipe may already be closed
            }
            try {
                stdinStream.Dispose();
            } catch {
                // Ignore
            }

            if (!process.WaitForExit(120_000)) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch {
                    // Best-effort kill
                }
                finalizeError = new TimeoutException($"FFmpeg pipe timed out finalizing output. {StderrTail}");
            } else {
                process.WaitForExit();
            }

            try {
                Task.WaitAll([stdoutDrainTask, stderrDrainTask], 3000);
            } catch {
                // Ignore drain wait failures
            }

            if (finalizeError == null && process.HasExited && process.ExitCode != 0) {
                finalizeError = new IOException($"FFmpeg exited with code {process.ExitCode}: {StderrTail}");
            }
        } finally {
            process.Dispose();
            TryDeleteTempFile(tempMetadataPath);
            TryDeleteTempFile(tempCoverPath);
        }

        if (finalizeError != null) {
            throw finalizeError;
        }
    }

    static void TryDeleteTempFile(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Best-effort cleanup
        }
    }
}
