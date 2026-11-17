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
    /// Decodes the first audio track of <paramref name="inputPath"/> into <paramref name="outputWavPath"/>.
    /// </summary>
    public static string DecodeToWav(string inputPath, string outputFolder, Action<double> onProgress = null) {
        Directory.CreateDirectory(outputFolder);
        string wavPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(inputPath) + "_decoded.wav");

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
        bool isFloat = false;

        using MediaCodec codec = MediaCodec.CreateDecoderByType(mime);
        codec.Configure(trackFormat, null, null, 0);
        codec.Start();

        using FileStream fs = File.Create(wavPath);
        WriteWavPlaceholderHeader(fs, channels, sampleRate, isFloat ? (short)32 : (short)16, isFloat);

        MediaCodec.BufferInfo info = new();
        bool inputDone = false;
        bool outputDone = false;
        long totalPcmBytes = 0;

        while (!outputDone) {
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
                MediaFormat outFmt = codec.OutputFormat;
                if (outFmt.ContainsKey(MediaFormat.KeySampleRate)) {
                    sampleRate = outFmt.GetInteger(MediaFormat.KeySampleRate);
                }
                if (outFmt.ContainsKey(MediaFormat.KeyChannelCount)) {
                    channels = outFmt.GetInteger(MediaFormat.KeyChannelCount);
                }
                if (outFmt.ContainsKey(MediaFormat.KeyPcmEncoding)) {
                    int pcmEnc = outFmt.GetInteger(MediaFormat.KeyPcmEncoding);
                    isFloat = pcmEnc == (int)AudioEncoding.PcmFloat;
                }
            } else if (outIdx >= 0) {
                if (info.Size > 0) {
                    ByteBuffer outBuf = codec.GetOutputBuffer(outIdx);
                    outBuf.Position(info.Offset);
                    outBuf.Limit(info.Offset + info.Size);
                    byte[] pcm = new byte[info.Size];
                    outBuf.Get(pcm);
                    fs.Write(pcm, 0, pcm.Length);
                    totalPcmBytes += pcm.Length;
                }
                codec.ReleaseOutputBuffer(outIdx, false);
                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) {
                    outputDone = true;
                }
            }
        }

        codec.Stop();
        PatchWavHeader(fs, channels, sampleRate, isFloat ? (short)32 : (short)16, isFloat, totalPcmBytes);
        onProgress?.Invoke(1);
        return wavPath;
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
