using System.Security.Cryptography;
using System.Text;

using Android.Media;

using Java.Nio;

using AudioEncoding = Android.Media.Encoding;
using Path = System.IO.Path;

namespace CavernizeAndroid.Audio;

/// <summary>
/// Decodes non-Cavern-native audio files (such as MP3, FLAC, AAC, OGG, Opus) to a temporary PCM WAV file
/// using Android's native <see cref="MediaExtractor"/> and <see cref="MediaCodec"/>.
/// </summary>
public static class AndroidMediaFallbackDecoder {
    /// <summary>
    /// Decodes the first audio track of <paramref name="inputPath"/> into a collision-free cached WAV file
    /// inside <paramref name="outputFolder"/>.
    /// </summary>
    public static string DecodeToWav(
        string inputPath,
        string outputFolder,
        Action<double> onProgress = null,
        CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputFolder);
        string wavPath = BuildUniqueWavCachePath(inputPath, outputFolder);

        using MediaExtractor extractor = new();
        extractor.SetDataSource(inputPath);

        int audioTrackIndex = -1;
        MediaFormat trackFormat = null;
        string mime = null;
        for (int i = 0; i < extractor.TrackCount; i++) {
            MediaFormat fmt = extractor.GetTrackFormat(i);
            string m = fmt.GetString(MediaFormat.KeyMime);
            if (!string.IsNullOrEmpty(m) && m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
                audioTrackIndex = i;
                trackFormat = fmt;
                mime = m;
                break;
            }
        }

        if (audioTrackIndex < 0 || trackFormat == null) {
            throw new NotSupportedException("No decodable audio track found in file.");
        }

        extractor.SelectTrack(audioTrackIndex);
        long durationUs = trackFormat.ContainsKey(MediaFormat.KeyDuration) ? trackFormat.GetLong(MediaFormat.KeyDuration) : 0;
        int sampleRate = trackFormat.ContainsKey(MediaFormat.KeySampleRate) ? trackFormat.GetInteger(MediaFormat.KeySampleRate) : 48000;
        int channels = trackFormat.ContainsKey(MediaFormat.KeyChannelCount) ? trackFormat.GetInteger(MediaFormat.KeyChannelCount) : 2;
        int pcmEncoding = trackFormat.ContainsKey(MediaFormat.KeyPcmEncoding)
            ? trackFormat.GetInteger(MediaFormat.KeyPcmEncoding)
            : (int)AudioEncoding.Pcm16bit;
        ResolveWavFormat(pcmEncoding, out short wavBitsPerSample, out bool wavIsFloat, out bool convertInt32ToFloat32);

        using MediaCodec codec = MediaCodec.CreateDecoderByType(mime);
        codec.Configure(trackFormat, null, null, 0);
        codec.Start();

        bool succeeded = false;
        try {
            using FileStream fs = File.Create(wavPath);
            WriteWavPlaceholderHeader(fs, channels, sampleRate, wavBitsPerSample, wavIsFloat);

            MediaCodec.BufferInfo info = new();
            byte[] pcmScratch = Array.Empty<byte>();
            bool inputDone = false;
            bool outputDone = false;
            long totalPcmBytes = 0;
            int idleDrainRetries = 0;

            while (!outputDone) {
                token.ThrowIfCancellationRequested();

                if (!inputDone) {
                    int inIdx = codec.DequeueInputBuffer(10000);
                    if (inIdx >= 0) {
                        ByteBuffer inputBuf = codec.GetInputBuffer(inIdx);
                        inputBuf.Clear();
                        int sampleSize = extractor.ReadSampleData(inputBuf, 0);
                        if (sampleSize < 0) {
                            codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        } else {
                            long pts = extractor.SampleTime;
                            codec.QueueInputBuffer(inIdx, 0, sampleSize, pts, 0);
                            extractor.Advance();
                            if (durationUs > 0 && pts >= 0) {
                                onProgress?.Invoke(Math.Clamp(pts / (double)durationUs, 0, 1));
                            }
                        }
                    }
                }

                int outIdx = codec.DequeueOutputBuffer(info, 10000);
                if (outIdx == -2) { // INFO_OUTPUT_FORMAT_CHANGED
                    idleDrainRetries = 0;
                    MediaFormat outFmt = codec.OutputFormat;
                    if (outFmt.ContainsKey(MediaFormat.KeySampleRate)) {
                        sampleRate = outFmt.GetInteger(MediaFormat.KeySampleRate);
                    }
                    if (outFmt.ContainsKey(MediaFormat.KeyChannelCount)) {
                        channels = outFmt.GetInteger(MediaFormat.KeyChannelCount);
                    }
                    if (outFmt.ContainsKey(MediaFormat.KeyPcmEncoding)) {
                        pcmEncoding = outFmt.GetInteger(MediaFormat.KeyPcmEncoding);
                        ResolveWavFormat(pcmEncoding, out wavBitsPerSample, out wavIsFloat, out convertInt32ToFloat32);
                    }
                } else if (outIdx >= 0) {
                    idleDrainRetries = 0;
                    if (info.Size > 0) {
                        ByteBuffer outBuf = codec.GetOutputBuffer(outIdx);
                        outBuf.Position(info.Offset);
                        outBuf.Limit(info.Offset + info.Size);
                        if (pcmScratch.Length < info.Size) {
                            pcmScratch = new byte[info.Size];
                        }
                        outBuf.Get(pcmScratch, 0, info.Size);
                        if (convertInt32ToFloat32) {
                            ConvertSignedInt32LeToFloat32LeInPlace(pcmScratch, info.Size);
                        }
                        fs.Write(pcmScratch, 0, info.Size);
                        totalPcmBytes += info.Size;
                    }
                    codec.ReleaseOutputBuffer(outIdx, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) {
                        outputDone = true;
                    }
                } else if (inputDone) {
                    if (++idleDrainRetries > 200) {
                        outputDone = true;
                    }
                }
            }

            PatchWavHeader(fs, channels, sampleRate, wavBitsPerSample, wavIsFloat, totalPcmBytes);
            succeeded = true;
        } finally {
            try {
                codec.Stop();
            } catch {
                // Best-effort MediaCodec stop on cancellation or decode fault
            }
            if (!succeeded) {
                try {
                    if (File.Exists(wavPath)) {
                        File.Delete(wavPath);
                    }
                } catch {
                    // Best-effort cleanup of partial WAV
                }
            }
        }

        onProgress?.Invoke(1);
        return wavPath;
    }

    static string BuildUniqueWavCachePath(string inputPath, string outputFolder) {
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(baseName)) {
            baseName = "track";
        }
        string identity = inputPath ?? string.Empty;
        try {
            FileInfo fi = new(inputPath);
            if (fi.Exists) {
                identity = $"{Path.GetFullPath(inputPath)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            }
        } catch {
            // Fall back to raw path string
        }
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        string shortHash = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        return Path.Combine(outputFolder, $"{baseName}_{shortHash}_decoded.wav");
    }

    static void ResolveWavFormat(int pcmEncoding, out short bitsPerSample, out bool isFloat, out bool convertInt32ToFloat32) {
        if (pcmEncoding == (int)AudioEncoding.PcmFloat) {
            bitsPerSample = 32;
            isFloat = true;
            convertInt32ToFloat32 = false;
        } else if (OperatingSystem.IsAndroidVersionAtLeast(31) && pcmEncoding == (int)AudioEncoding.Pcm24bitPacked) {
            bitsPerSample = 24;
            isFloat = false;
            convertInt32ToFloat32 = false;
        } else if (OperatingSystem.IsAndroidVersionAtLeast(31) && pcmEncoding == (int)AudioEncoding.Pcm32bit) {
            // RIFFWaveDecoder supports 8/16/24-bit integer PCM and 32-bit IEEE float.
            // Convert 32-bit signed int LE losslessly to IEEE Float32 LE so Cavern decodes full precision.
            bitsPerSample = 32;
            isFloat = true;
            convertInt32ToFloat32 = true;
        } else if (pcmEncoding == (int)AudioEncoding.Pcm8bit) {
            bitsPerSample = 8;
            isFloat = false;
            convertInt32ToFloat32 = false;
        } else {
            bitsPerSample = 16;
            isFloat = false;
            convertInt32ToFloat32 = false;
        }
    }

    static void ConvertSignedInt32LeToFloat32LeInPlace(byte[] buffer, int byteCount) {
        int usable = byteCount & ~3;
        for (int i = 0; i < usable; i += 4) {
            int s32 = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16) | (buffer[i + 3] << 24);
            float f = s32 / 2147483648f;
            int bits = BitConverter.SingleToInt32Bits(f);
            buffer[i] = (byte)(bits & 0xFF);
            buffer[i + 1] = (byte)((bits >> 8) & 0xFF);
            buffer[i + 2] = (byte)((bits >> 16) & 0xFF);
            buffer[i + 3] = (byte)((bits >> 24) & 0xFF);
        }
    }

    static void WriteWavPlaceholderHeader(FileStream fs, int channels, int sampleRate, short bitsPerSample, bool isFloat) {
        byte[] header = new byte[44];
        WriteWavHeaderBytes(header, channels, sampleRate, bitsPerSample, isFloat, 0);
        fs.Write(header, 0, 44);
    }

    static void PatchWavHeader(FileStream fs, int channels, int sampleRate, short bitsPerSample, bool isFloat, long dataBytes) {
        byte[] header = new byte[44];
        WriteWavHeaderBytes(header, channels, sampleRate, bitsPerSample, isFloat, dataBytes);
        fs.Position = 0;
        fs.Write(header, 0, 44);
        fs.Flush();
    }

    static void WriteWavHeaderBytes(byte[] h, int channels, int sampleRate, short bitsPerSample, bool isFloat, long dataBytes) {
        uint dataLen = (uint)Math.Min(dataBytes, uint.MaxValue - 44);
        uint riffLen = dataLen + 36;
        short formatCode = isFloat ? (short)3 : (short)1;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;

        System.Text.Encoding.ASCII.GetBytes("RIFF", 0, 4, h, 0);
        BitConverter.GetBytes(riffLen).CopyTo(h, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVE", 0, 4, h, 8);
        System.Text.Encoding.ASCII.GetBytes("fmt ", 0, 4, h, 12);
        BitConverter.GetBytes(16).CopyTo(h, 16);
        BitConverter.GetBytes(formatCode).CopyTo(h, 20);
        BitConverter.GetBytes((short)channels).CopyTo(h, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(h, 24);
        BitConverter.GetBytes(byteRate).CopyTo(h, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(h, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(h, 34);
        System.Text.Encoding.ASCII.GetBytes("data", 0, 4, h, 36);
        BitConverter.GetBytes(dataLen).CopyTo(h, 40);
    }
}
