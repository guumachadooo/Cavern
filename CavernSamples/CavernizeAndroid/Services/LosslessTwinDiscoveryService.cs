using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;
using Android.Media;

using Cavern.Format.Common;
using Cavern.Restoration;

using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Android Slot 1 Universal Lossless Twin discovery &amp; alignment service (W09).
/// Auto-discovers verified lossless <c>FLAC</c>/<c>WAV</c>/<c>ALAC</c> twins in the media library (or same directory),
/// auto-bypasses natively lossless streams (<c>TrueHD</c>/<c>DAMF</c>/<c>ADM</c>/<c>FLAC</c>/<c>WAV</c>/<c>LAF</c>)
/// and fake/upsampled lossless files via <see cref="AudioQualityAnalyzer"/>, decodes unified <c>float32 [-1, +1]</c>
/// PCM, and runs 3-stage sub-sample alignment via <see cref="UniversalLosslessTwinProvider"/> without disturbing live playback.
/// </summary>
public static partial class LosslessTwinDiscoveryService {
    [GeneratedRegex(@"(?<![a-z0-9])(?:dolby[\s._-]*atmos|atmos|lossless|hi[\s._-]*res|flac|e[\s._-]*ac[\s._-]*3|eac3|24[\s._-]*bit|16[\s._-]*bit|96[\s._-]*khz|48[\s._-]*khz|44\.?1[\s._-]*khz|192[\s._-]*khz)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NormalizeStripTokenRegex();

    const double AtmosProbeSeconds = 24.0;
    const int MaxDecodedTwinFrames = 48000 * 60 * 18; // Cap at ~18 min stereo (~414 MB float32 upper guard; typical song ~50 MB)

    /// <summary>
    /// Returns true when the active <see cref="CavernizeTrack"/> or file path is natively lossless and should auto-bypass LGSR.
    /// Guards against Android MediaCodec / FFmpeg fallback WAV caches decoded from lossy MP3/AAC/M4A/OGG/Opus sources.
    /// </summary>
    public static bool IsTrackAlreadyLossless(CavernizeTrack track, string mediaPath) {
        if (track != null && track.IsLossySource) {
            return false;
        }
        string probePath = !string.IsNullOrEmpty(mediaPath) ? mediaPath : (track?.OriginalSourcePath ?? track?.Path ?? "");
        string ext = GetPathOrUriExtension(Android.App.Application.Context, probePath);
        if (ext is ".mp3" or ".aac" or ".ogg" or ".opus" or ".wma" or ".ac3" or ".ec3" or ".eac3") {
            return false;
        }
        if (track != null) {
            Codec c = track.SourceCodec != Codec.Unknown ? track.SourceCodec : track.Codec;
            if (c is Codec.TrueHD or Codec.DAMF or Codec.ADM_BWF or Codec.ADM_BWF_Atmos or
                Codec.LimitlessAudio or Codec.FLAC or Codec.DTS_HD) {
                return true;
            }
            if (c is Codec.PCM_LE or Codec.PCM_Float) {
                // Only treat PCM as natively lossless if the original source file is actually PCM/WAV/AIFF/CAF,
                // not a decoded fallback WAV from a lossy M4A/MP4/MKV container.
                if (ext is ".wav" or ".aif" or ".aiff" or ".caf" or ".flac" or ".laf" or "") {
                    string trackPath = track.Path ?? "";
                    bool isFallbackCacheWav = trackPath.Contains("decoded_fallback", StringComparison.OrdinalIgnoreCase) ||
                        trackPath.EndsWith("decoded.wav", StringComparison.OrdinalIgnoreCase);
                    if (!isFallbackCacheWav) {
                        return true;
                    }
                }
            }
        }
        return ext is ".flac" or ".wav" or ".laf" or ".atmos" or ".thd" or ".mlp";
    }

    /// <summary>
    /// Decodes an audio/video file (lossless or lossy) into unified stereo <c>float32 [-1, +1]</c> PCM arrays
    /// for background Slot 2B neural stem separation without disturbing live playback.
    /// </summary>
    public static bool TryDecodeTrackToStereoFloat(
        string filePath,
        int preferredSampleRate,
        string ffmpegPath,
        CancellationToken ct,
        out float[] left,
        out float[] right,
        out int sampleRate) {
        left = null;
        right = null;
        sampleRate = preferredSampleRate >= 8000 ? preferredSampleRate : 48000;
        Context effectiveCtx = Android.App.Application.Context;
        string readablePath = ResolveReadableMediaPath(effectiveCtx, TryParseUri(filePath), filePath);
        if (string.IsNullOrEmpty(readablePath)) {
            return false;
        }
        string safeFfmpeg = ResolveSafeFfmpegPath(effectiveCtx, ffmpegPath);
        AudioQualityReport syntheticReport = new() {
            SampleRateHz = sampleRate,
            DeclaredBitDepth = 24
        };
        return TryDecodeLosslessStereo(readablePath, syntheticReport, safeFfmpeg, ct, out left, out right, out sampleRate);
    }

    /// <summary>
    /// Asynchronously discovers, verifies, decodes, rate-matches, and sub-sample-syncs a lossless stereo twin
    /// for <paramref name="activeTrack"/>. Always returns a valid <see cref="UniversalLosslessTwinProvider"/> reflecting
    /// the resolved verification state (<c>Synced</c>, <c>BypassedAlreadyLossless</c>, <c>BypassedFakeLossless</c>, etc.).
    /// </summary>
    public static async Task<UniversalLosslessTwinProvider> DiscoverAndSyncAsync(
        Context ctx,
        MediaLibrarySnapshot snapshot,
        CavernizeTrack activeTrack,
        string activeMediaPath,
        string title,
        string artist,
        string album,
        LgsrConfig config,
        string ffmpegPath,
        CancellationToken ct) {
        UniversalLosslessTwinProvider provider = new();
        LgsrConfig safeCfg = (config ?? LgsrConfig.Default).Sanitize();

        if (!safeCfg.Enabled || !safeCfg.AutoDiscoverTwin) {
            provider.SetState(LosslessTwinVerificationState.None);
            return provider;
        }

        if (safeCfg.BypassOnAlreadyLossless && IsTrackAlreadyLossless(activeTrack, activeMediaPath)) {
            provider.SetState(LosslessTwinVerificationState.BypassedAlreadyLossless);
            return provider;
        }

        provider.SetState(LosslessTwinVerificationState.Searching);
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        string safeFfmpeg = ResolveSafeFfmpegPath(effectiveCtx, ffmpegPath);
        return await Task.Run(() => {
            ct.ThrowIfCancellationRequested();

            List<LibraryTrackItem> candidates = FindLosslessCandidates(
                effectiveCtx, snapshot, activeMediaPath, title, artist, album, safeCfg, out bool rejectedFake);
            if (candidates.Count == 0) {
                provider.SetState(rejectedFake
                    ? LosslessTwinVerificationState.BypassedFakeLossless
                    : LosslessTwinVerificationState.None);
                return provider;
            }

            int targetSr = activeTrack != null && activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;

            // Decode a lightweight 24 s mono probe from the active lossy Atmos file without touching live playback
            float[] atmosProbe = DecodeMonoProbe(effectiveCtx, activeMediaPath, targetSr, AtmosProbeSeconds, safeFfmpeg, ct);
            if (atmosProbe == null || atmosProbe.Length < SubSampleSyncEngine.GccFftSize) {
                AppLogger.Warn($"W09 LGSR: unable to extract mono sync probe for '{IOPath.GetFileName(activeMediaPath ?? "")}'.");
                provider.SetState(LosslessTwinVerificationState.BypassedLowConfidence);
                return provider;
            }

            for (int i = 0; i < candidates.Count; i++) {
                ct.ThrowIfCancellationRequested();
                LibraryTrackItem item = candidates[i];
                AudioQualityReport report = AudioQualityAnalyzer.EvaluateFast(item, effectiveCtx);
                string candDisplayName = GetTrackFileName(effectiveCtx, item, item.FilePath);

                if (safeCfg.RejectFakeLosslessTwins && report.IsFakeLossless) {
                    AppLogger.Info($"W09 LGSR: rejected fake lossless candidate '{candDisplayName}' (cutoff {report.SpectralCutoffHz} Hz).");
                    rejectedFake = true;
                    continue;
                }

                string candPath = ResolveReadableTrackPath(effectiveCtx, item);
                if (string.IsNullOrEmpty(candPath) ||
                    !TryDecodeLosslessStereo(candPath, report, safeFfmpeg, ct, out float[] twinL, out float[] twinR, out int twinSr)) {
                    AppLogger.Warn($"W09 LGSR: failed to decode lossless twin '{candDisplayName}'.");
                    continue;
                }

                LosslessTwinMetadata meta = new() {
                    FilePath = candPath,
                    FileName = candDisplayName,
                    CodecFamily = string.IsNullOrEmpty(report.CodecFamily) ? "FLAC" : report.CodecFamily,
                    NativeSampleRate = twinSr,
                    EffectiveBitDepth = report.EffectiveBitDepth > 0 ? report.EffectiveBitDepth : Math.Max(16, report.DeclaredBitDepth),
                    IsUpsampledHiRes = report.IsUpsampledHiRes,
                    SpectralCutoffHz = report.SpectralCutoffHz,
                    DynamicRangeDb = report.DynamicRangeDb
                };

                bool synced = provider.InitializeAndSync(
                    meta,
                    atmosProbe,
                    atmosProbeStartSample: 0L,
                    atmosSampleRate: targetSr,
                    losslessLeft: twinL,
                    losslessRight: twinR,
                    losslessSampleRate: twinSr,
                    minConfidenceGate: safeCfg.MinSyncConfidence);

                if (synced) {
                    SubSampleSyncResult sr = provider.SyncResult;
                    AppLogger.Info(
                        $"W09 LGSR synced '{meta.FileName}' ({meta.FormatSummary}): " +
                        $"offset={sr.BaseOffsetSamples:0.00} samp ({sr.BaseOffsetSamples * 1000.0 / targetSr:0.00} ms), " +
                        $"drift={sr.DriftPpm:+0.00;-0.00;0.00} ppm, conf={sr.Confidence:0.00}.");
                    return provider;
                }
            }

            provider.SetState(rejectedFake
                ? LosslessTwinVerificationState.BypassedFakeLossless
                : LosslessTwinVerificationState.BypassedLowConfidence);
            return provider;
        }, ct).ConfigureAwait(false);
    }

    static List<LibraryTrackItem> FindLosslessCandidates(
        Context ctx,
        MediaLibrarySnapshot snapshot,
        string activeMediaPath,
        string title,
        string artist,
        string album,
        LgsrConfig cfg,
        out bool rejectedFake) {
        rejectedFake = false;
        List<LibraryTrackItem> pool = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(activeMediaPath)) {
            seen.Add(activeMediaPath);
        }

        if (snapshot != null && !string.IsNullOrEmpty(activeMediaPath)) {
            List<LibraryTrackItem> variants = MediaLibraryService.FindVariantsForTrack(
                snapshot, activeMediaPath, title, artist, album);
            for (int i = 0; i < variants.Count; i++) {
                LibraryTrackItem v = variants[i];
                if (v == null || (v.ContentUri == null && !IsUriString(v.FilePath) && !CanOpenFileForRead(v.FilePath))) {
                    continue;
                }
                if (seen.Add(v.CacheKey)) {
                    pool.Add(v);
                }
            }
        }

        // Also inspect sibling files in the same directory for standalone opened files outside MediaStore
        if (!string.IsNullOrEmpty(activeMediaPath)) {
            try {
                Android.Net.Uri activeUri = TryParseUri(activeMediaPath);
                string dir = AndroidFileHelper.TryGetOriginalDirectory(ctx, activeUri, activeMediaPath);
                string stemSource = !IsUriString(activeMediaPath)
                    ? IOPath.GetFileNameWithoutExtension(activeMediaPath)
                    : (activeUri != null && ctx != null ? IOPath.GetFileNameWithoutExtension(AndroidFileHelper.GetDisplayName(ctx, activeUri) ?? "") : "");
                string baseStem = NormalizeBaseName(stemSource);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !string.IsNullOrEmpty(baseStem)) {
                    foreach (string file in Directory.EnumerateFiles(dir)) {
                        string ext = IOPath.GetExtension(file).ToLowerInvariant();
                        if (ext is not (".flac" or ".wav" or ".m4a")) {
                            continue;
                        }
                        if (!CanOpenFileForRead(file) || !seen.Add(file)) {
                            continue;
                        }
                        string candBase = NormalizeBaseName(IOPath.GetFileNameWithoutExtension(file));
                        if (!string.Equals(candBase, baseStem, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }
                        FileInfo fi = new(file);
                        pool.Add(new LibraryTrackItem {
                            FilePath = file,
                            Title = IOPath.GetFileNameWithoutExtension(file),
                            FormatBadge = ext.TrimStart('.').ToUpperInvariant(),
                            ChannelCount = 2,
                            FileSizeBytes = fi.Length,
                            LastModifiedTicks = fi.LastWriteTimeUtc.Ticks
                        });
                    }
                }
            } catch {
                // Directory read may be restricted by scoped storage; MediaLibrary snapshot remains primary
            }
        }

        List<(LibraryTrackItem item, AudioQualityReport report)> valid = [];
        for (int i = 0; i < pool.Count; i++) {
            LibraryTrackItem item = pool[i];
            AudioQualityReport rep = AudioQualityAnalyzer.EvaluateFast(item, ctx);
            if (!rep.IsLossless) {
                continue;
            }
            if (rep.Channels > 2 && rep.Category != AudioTierCategory.Stereo) {
                continue;
            }
            if (cfg.RejectFakeLosslessTwins && rep.IsFakeLossless) {
                rejectedFake = true;
                continue;
            }
            valid.Add((item, rep));
        }

        valid.Sort((a, b) => {
            int fakeCmp = a.report.IsUpsampledHiRes.CompareTo(b.report.IsUpsampledHiRes);
            if (fakeCmp != 0) return fakeCmp;
            int scoreCmp = b.report.Score.CompareTo(a.report.Score);
            if (scoreCmp != 0) return scoreCmp;
            return b.report.EffectiveBitDepth.CompareTo(a.report.EffectiveBitDepth);
        });

        List<LibraryTrackItem> result = new(valid.Count);
        for (int i = 0; i < valid.Count; i++) {
            result.Add(valid[i].item);
        }
        return result;
    }

    static bool TryDecodeLosslessStereo(
        string filePath,
        AudioQualityReport report,
        string ffmpegPath,
        CancellationToken ct,
        out float[] left,
        out float[] right,
        out int sampleRate) {
        left = null;
        right = null;
        sampleRate = report?.SampleRateHz > 0 ? report.SampleRateHz : 48000;

        string ext = IOPath.GetExtension(filePath ?? "").ToLowerInvariant();
        if (ext == ".wav" && TryDecodeWavStereo(filePath, ct, out left, out right, out sampleRate)) {
            return true;
        }

        // Prefer FFmpeg float32 decode for 24-bit FLAC/ALAC so full 24-bit precision is preserved even on Android API < 31
        bool preferFfmpeg = !string.IsNullOrEmpty(ffmpegPath) && IOFile.Exists(ffmpegPath) &&
            ((report?.DeclaredBitDepth ?? 16) >= 24 || ext == ".m4a");
        if (preferFfmpeg && TryDecodeFfmpegStereoFloat(filePath, ffmpegPath, sampleRate, ct, out left, out right)) {
            return true;
        }

        if (TryDecodeMediaCodecStereoFloat(filePath, ct, out left, out right, out sampleRate)) {
            return true;
        }

        if (!preferFfmpeg && !string.IsNullOrEmpty(ffmpegPath) && IOFile.Exists(ffmpegPath) &&
            TryDecodeFfmpegStereoFloat(filePath, ffmpegPath, sampleRate, ct, out left, out right)) {
            return true;
        }

        return false;
    }

    static readonly byte[] WaveExtensibleGuidTail = [
        0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
    ];

    static bool TryDecodeWavStereo(
        string path,
        CancellationToken ct,
        out float[] left,
        out float[] right,
        out int sampleRate) {
        left = null;
        right = null;
        sampleRate = 48000;
        try {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hdr = new byte[Math.Min(65536, (int)Math.Min(fs.Length, 65536))];
            int hRead = fs.Read(hdr, 0, hdr.Length);
            if (hRead < 44 || hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F') {
                return false;
            }

            int channels = 2;
            int audioFmt = 1;
            int bits = 16;
            bool isFloat = false;
            bool fmtValid = false;
            int pos = 12;
            long dataOffset = -1;
            long dataLen = 0;

            while (pos + 8 <= hRead) {
                string id = System.Text.Encoding.ASCII.GetString(hdr, pos, 4);
                int sz = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(pos + 4, 4));
                if (id == "fmt " && pos + 24 <= hRead) {
                    audioFmt = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(pos + 8, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(pos + 10, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(pos + 12, 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(pos + 22, 2));
                    if (audioFmt == 1) {
                        isFloat = false;
                        fmtValid = true;
                    } else if (audioFmt == 3) {
                        isFloat = bits == 32;
                        fmtValid = bits == 32;
                    } else if (audioFmt == 0xFFFE && sz >= 40 && pos + 8 + 40 <= hRead) {
                        // WAVE_FORMAT_EXTENSIBLE SubFormat GUID sits at fmt payload offset +24 (pos + 32)
                        uint subFormatData1 = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(pos + 32, 4));
                        bool tailMatches = hdr.AsSpan(pos + 36, 12).SequenceEqual(WaveExtensibleGuidTail);
                        if (tailMatches && subFormatData1 == 3u && bits == 32) {
                            isFloat = true;
                            fmtValid = true;
                        } else if (tailMatches && subFormatData1 == 1u) {
                            isFloat = false;
                            fmtValid = true;
                        }
                    }
                } else if (id == "data") {
                    dataOffset = pos + 8;
                    dataLen = sz > 0 ? Math.Min(sz, fs.Length - dataOffset) : (fs.Length - dataOffset);
                    break;
                }
                if (sz < 0) break;
                pos += 8 + sz + (sz & 1);
            }

            if (!fmtValid || dataOffset < 0 || channels <= 0 || bits < 16) {
                return false;
            }
            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            long totalFrames = Math.Min(dataLen / frameBytes, MaxDecodedTwinFrames);
            if (totalFrames < SubSampleSyncEngine.GccFftSize) {
                return false;
            }

            ct.ThrowIfCancellationRequested();
            fs.Seek(dataOffset, SeekOrigin.Begin);
            int targetFrames = (int)totalFrames;
            left = new float[targetFrames];
            right = new float[targetFrames];

            const int chunkFrames = 16384;
            byte[] chunk = new byte[chunkFrames * frameBytes];
            int framesDone = 0;
            int carry = 0;

            while (framesDone < targetFrames) {
                ct.ThrowIfCancellationRequested();
                int remainBytes = (targetFrames - framesDone) * frameBytes;
                int toRead = Math.Min(chunk.Length - carry, remainBytes);
                if (toRead <= 0) break;
                int got = fs.Read(chunk, carry, toRead);
                if (got <= 0) break;

                int totalBytes = carry + got;
                int readyFrames = Math.Min(totalBytes / frameBytes, targetFrames - framesDone);
                for (int f = 0; f < readyFrames; f++) {
                    int baseOff = f * frameBytes;
                    float l = ReadWavSampleNormalized(chunk, baseOff, bits, isFloat);
                    float r = channels > 1
                        ? ReadWavSampleNormalized(chunk, baseOff + bytesPerSample, bits, isFloat)
                        : l;
                    left[framesDone + f] = SanitizeFloat(l);
                    right[framesDone + f] = SanitizeFloat(r);
                }
                framesDone += readyFrames;

                int consumed = readyFrames * frameBytes;
                carry = totalBytes - consumed;
                if (carry > 0) {
                    Buffer.BlockCopy(chunk, consumed, chunk, 0, carry);
                }
            }

            if (framesDone < SubSampleSyncEngine.GccFftSize) {
                left = null;
                right = null;
                return false;
            }
            if (framesDone < targetFrames) {
                Array.Resize(ref left, framesDone);
                Array.Resize(ref right, framesDone);
            }
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        }
    }

    static float ReadWavSampleNormalized(byte[] buf, int off, int bits, bool isFloat) {
        if (isFloat && bits == 32) {
            return BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off, 4));
        }
        if (bits == 16) {
            return BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off, 2)) * (1f / 32768f);
        }
        if (bits == 24) {
            int v = buf[off] | (buf[off + 1] << 8) | ((sbyte)buf[off + 2] << 16);
            return v * (1f / 8388608f);
        }
        if (bits == 32) {
            return (float)(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off, 4)) * (1.0 / 2147483648.0));
        }
        return 0f;
    }

    static float SanitizeFloat(float v) {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
        if (v > 2f) return 2f;
        if (v < -2f) return -2f;
        return v;
    }

    static bool TryDecodeFfmpegStereoFloat(
        string path,
        string ffmpegPath,
        int sampleRate,
        CancellationToken ct,
        out float[] left,
        out float[] right) {
        left = null;
        right = null;
        try {
            ct.ThrowIfCancellationRequested();
            ProcessStartInfo psi = new() {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("-");
            FFmpegInstallerService.ConfigureProcessStartInfo(psi, ffmpegPath);

            using Process proc = Process.Start(psi);
            if (proc == null) return false;

            using CancellationTokenRegistration ctr = ct.Register(() => {
                try {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                } catch { }
            });

            // Drain stderr asynchronously so FFmpeg never deadlocks on a full 64 KB pipe buffer
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

            const int blockFrames = 65536;
            List<(float[] L, float[] R, int Count)> blocks = [];
            float[] curL = new float[blockFrames];
            float[] curR = new float[blockFrames];
            int curCount = 0;
            int totalFrames = 0;

            byte[] buf = new byte[65536];
            int carry = 0;
            try {
                while (totalFrames < MaxDecodedTwinFrames) {
                    ct.ThrowIfCancellationRequested();
                    int read = proc.StandardOutput.BaseStream.Read(buf, carry, buf.Length - carry);
                    if (read <= 0) break;

                    int totalBytes = carry + read;
                    int readyFrames = Math.Min(totalBytes / 8, MaxDecodedTwinFrames - totalFrames);
                    for (int f = 0; f < readyFrames; f++) {
                        int off = f * 8;
                        curL[curCount] = SanitizeFloat(BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off, 4)));
                        curR[curCount] = SanitizeFloat(BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(off + 4, 4)));
                        curCount++;
                        totalFrames++;
                        if (curCount == blockFrames) {
                            blocks.Add((curL, curR, curCount));
                            curL = new float[blockFrames];
                            curR = new float[blockFrames];
                            curCount = 0;
                        }
                    }

                    int consumed = readyFrames * 8;
                    carry = totalBytes - consumed;
                    if (carry > 0) {
                        Buffer.BlockCopy(buf, consumed, buf, 0, carry);
                    }
                    if (totalFrames >= MaxDecodedTwinFrames) {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                }
            } catch (OperationCanceledException) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            } finally {
                try { proc.WaitForExit(2000); } catch { }
                try { stderrTask.Wait(400); } catch { }
            }

            if (curCount > 0) {
                blocks.Add((curL, curR, curCount));
            }
            if (totalFrames < SubSampleSyncEngine.GccFftSize) {
                return false;
            }

            left = new float[totalFrames];
            right = new float[totalFrames];
            int dst = 0;
            for (int b = 0; b < blocks.Count; b++) {
                var (bL, bR, cnt) = blocks[b];
                Array.Copy(bL, 0, left, dst, cnt);
                Array.Copy(bR, 0, right, dst, cnt);
                dst += cnt;
                blocks[b] = default;
            }
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        }
    }

    static bool TryDecodeMediaCodecStereoFloat(
        string path,
        CancellationToken ct,
        out float[] left,
        out float[] right,
        out int sampleRate) {
        left = null;
        right = null;
        sampleRate = 48000;
        MediaCodec codec = null;
        try {
            using MediaExtractor extractor = new();
            extractor.SetDataSource(path);
            int audioIdx = -1;
            MediaFormat format = null;
            for (int i = 0; i < extractor.TrackCount; i++) {
                MediaFormat f = extractor.GetTrackFormat(i);
                string mime = f.GetString(MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
                    audioIdx = i;
                    format = f;
                    break;
                }
            }
            if (audioIdx < 0 || format == null) return false;

            extractor.SelectTrack(audioIdx);
            if (format.ContainsKey(MediaFormat.KeySampleRate)) {
                sampleRate = Math.Max(8000, format.GetInteger(MediaFormat.KeySampleRate));
            }
            int outChannels = format.ContainsKey(MediaFormat.KeyChannelCount) ? Math.Max(1, format.GetInteger(MediaFormat.KeyChannelCount)) : 2;
            string trackMime = format.GetString(MediaFormat.KeyMime);

            codec = MediaCodec.CreateDecoderByType(trackMime);
            codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            List<float> listL = new(sampleRate * 60);
            List<float> listR = new(sampleRate * 60);
            MediaCodec.BufferInfo info = new();
            bool inputDone = false;
            int pcmEncoding = (int)Android.Media.Encoding.Pcm16bit;
            int drainRetries = 0;

            while (listL.Count < MaxDecodedTwinFrames) {
                ct.ThrowIfCancellationRequested();
                if (!inputDone) {
                    int inIdx = codec.DequeueInputBuffer(10000);
                    if (inIdx >= 0) {
                        Java.Nio.ByteBuffer inBuf = codec.GetInputBuffer(inIdx);
                        int read = extractor.ReadSampleData(inBuf, 0);
                        if (read < 0) {
                            codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        } else {
                            codec.QueueInputBuffer(inIdx, 0, read, extractor.SampleTime, MediaCodecBufferFlags.None);
                            extractor.Advance();
                        }
                    }
                }

                int outIdx = codec.DequeueOutputBuffer(info, 10000);
                if (outIdx == (int)MediaCodecInfoState.OutputFormatChanged) {
                    drainRetries = 0;
                    MediaFormat outFmt = codec.OutputFormat;
                    if (outFmt.ContainsKey(MediaFormat.KeySampleRate)) sampleRate = Math.Max(8000, outFmt.GetInteger(MediaFormat.KeySampleRate));
                    if (outFmt.ContainsKey(MediaFormat.KeyChannelCount)) outChannels = Math.Max(1, outFmt.GetInteger(MediaFormat.KeyChannelCount));
                    if (outFmt.ContainsKey(MediaFormat.KeyPcmEncoding)) pcmEncoding = outFmt.GetInteger(MediaFormat.KeyPcmEncoding);
                } else if (outIdx >= 0) {
                    drainRetries = 0;
                    if (info.Size > 0) {
                        Java.Nio.ByteBuffer outBuf = codec.GetOutputBuffer(outIdx);
                        outBuf.Position(info.Offset);
                        byte[] chunk = new byte[info.Size];
                        outBuf.Get(chunk);

                        ResolveMediaCodecPcmFormat(pcmEncoding, out int bits, out bool isFloat);
                        int frameBytes = (bits / 8) * outChannels;
                        int frames = frameBytes > 0 ? chunk.Length / frameBytes : 0;
                        if (frames > 0) {
                            KaiserSincResampler.UnifyPcmBytesToStereoFloat(chunk, frames * frameBytes, outChannels, bits, isFloat, out float[] cL, out float[] cR);
                            int take = Math.Min(frames, MaxDecodedTwinFrames - listL.Count);
                            for (int s = 0; s < take; s++) {
                                listL.Add(cL[s]);
                                listR.Add(cR[s]);
                            }
                        }
                    }
                    codec.ReleaseOutputBuffer(outIdx, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) {
                        break;
                    }
                } else if (inputDone && outIdx == (int)MediaCodecInfoState.TryAgainLater) {
                    if (++drainRetries >= 40) {
                        break;
                    }
                }
            }

            if (listL.Count < SubSampleSyncEngine.GccFftSize) {
                return false;
            }
            left = listL.ToArray();
            right = listR.ToArray();
            return true;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return false;
        } finally {
            try { codec?.Stop(); } catch { }
            try { codec?.Release(); } catch { }
        }
    }

    static float[] DecodeMonoProbe(
        Context ctx,
        string mediaPath,
        int targetSampleRate,
        double seconds,
        string ffmpegPath,
        CancellationToken ct) {
        Android.Net.Uri mediaUri = TryParseUri(mediaPath);
        string localMediaPath = CanOpenFileForRead(mediaPath) ? mediaPath : null;
        if (localMediaPath == null && mediaUri == null) {
            return null;
        }

        // Fast, non-disruptive FFmpeg mono probe (supports E-AC-3 JOC, AC-3, DTS, Opus, MKV, MP4 without touching live track)
        string ffInputPath = !string.IsNullOrEmpty(ffmpegPath) && IOFile.Exists(ffmpegPath)
            ? (localMediaPath ?? ResolveReadableMediaPath(ctx, mediaUri, mediaPath))
            : null;
        if (!string.IsNullOrEmpty(ffInputPath)) {
            try {
                ct.ThrowIfCancellationRequested();
                string durStr = seconds.ToString("0.0", CultureInfo.InvariantCulture);
                ProcessStartInfo psi = new() {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(durStr);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(ffInputPath);
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-ar");
                psi.ArgumentList.Add(targetSampleRate.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("f32le");
                psi.ArgumentList.Add("-");
                FFmpegInstallerService.ConfigureProcessStartInfo(psi, ffmpegPath);

                using Process proc = Process.Start(psi);
                if (proc != null) {
                    using CancellationTokenRegistration ctr = ct.Register(() => {
                        try {
                            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                        } catch { }
                    });

                    Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                    int expectedSamples = Math.Max(SubSampleSyncEngine.GccFftSize, (int)Math.Ceiling(seconds * targetSampleRate) + 1024);
                    List<float> monoList = new(expectedSamples);
                    byte[] buf = new byte[32768];
                    int carry = 0;
                    try {
                        int read;
                        while ((read = proc.StandardOutput.BaseStream.Read(buf, carry, buf.Length - carry)) > 0) {
                            ct.ThrowIfCancellationRequested();
                            int totalBytes = carry + read;
                            int ready = totalBytes / 4;
                            for (int i = 0; i < ready; i++) {
                                monoList.Add(SanitizeFloat(BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(i * 4, 4))));
                            }
                            int consumed = ready * 4;
                            carry = totalBytes - consumed;
                            if (carry > 0) {
                                Buffer.BlockCopy(buf, consumed, buf, 0, carry);
                            }
                        }
                    } catch (OperationCanceledException) {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        throw;
                    } finally {
                        try { proc.WaitForExit(1500); } catch { }
                        try { stderrTask.Wait(400); } catch { }
                    }
                    if (monoList.Count >= SubSampleSyncEngine.GccFftSize) {
                        return monoList.ToArray();
                    }
                }
            } catch (OperationCanceledException) {
                throw;
            } catch {
                // Fall through to Android MediaCodec probe
            }
        }

        // Fallback: Android native MediaExtractor + MediaCodec probe (works for MP4/M4A/EAC3/AAC/Opus when FFmpeg isn't installed)
        MediaCodec codec = null;
        try {
            using MediaExtractor extractor = new();
            if (localMediaPath != null) {
                extractor.SetDataSource(localMediaPath);
            } else if (mediaUri != null && ctx != null) {
                extractor.SetDataSource(ctx, mediaUri, null);
            } else {
                return null;
            }
            int audioIdx = -1;
            MediaFormat format = null;
            for (int i = 0; i < extractor.TrackCount; i++) {
                MediaFormat f = extractor.GetTrackFormat(i);
                string mime = f.GetString(MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
                    audioIdx = i;
                    format = f;
                    break;
                }
            }
            if (audioIdx < 0 || format == null) return null;

            extractor.SelectTrack(audioIdx);
            int srcSr = format.ContainsKey(MediaFormat.KeySampleRate) ? Math.Max(8000, format.GetInteger(MediaFormat.KeySampleRate)) : targetSampleRate;
            int outChannels = format.ContainsKey(MediaFormat.KeyChannelCount) ? Math.Max(1, format.GetInteger(MediaFormat.KeyChannelCount)) : 2;
            string trackMime = format.GetString(MediaFormat.KeyMime);

            codec = MediaCodec.CreateDecoderByType(trackMime);
            codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            int wantSamples = (int)Math.Ceiling(srcSr * seconds);
            List<float> mono = new(wantSamples);
            MediaCodec.BufferInfo info = new();
            bool inputDone = false;
            int pcmEncoding = (int)Android.Media.Encoding.Pcm16bit;
            int guard = 0;
            int drainRetries = 0;

            while (mono.Count < wantSamples && guard++ < 4000) {
                ct.ThrowIfCancellationRequested();
                if (!inputDone) {
                    int inIdx = codec.DequeueInputBuffer(8000);
                    if (inIdx >= 0) {
                        Java.Nio.ByteBuffer inBuf = codec.GetInputBuffer(inIdx);
                        int read = extractor.ReadSampleData(inBuf, 0);
                        if (read < 0) {
                            codec.QueueInputBuffer(inIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            inputDone = true;
                        } else {
                            codec.QueueInputBuffer(inIdx, 0, read, extractor.SampleTime, MediaCodecBufferFlags.None);
                            extractor.Advance();
                        }
                    }
                }

                int outIdx = codec.DequeueOutputBuffer(info, 8000);
                if (outIdx == (int)MediaCodecInfoState.OutputFormatChanged) {
                    drainRetries = 0;
                    MediaFormat outFmt = codec.OutputFormat;
                    if (outFmt.ContainsKey(MediaFormat.KeySampleRate)) srcSr = Math.Max(8000, outFmt.GetInteger(MediaFormat.KeySampleRate));
                    if (outFmt.ContainsKey(MediaFormat.KeyChannelCount)) outChannels = Math.Max(1, outFmt.GetInteger(MediaFormat.KeyChannelCount));
                    if (outFmt.ContainsKey(MediaFormat.KeyPcmEncoding)) pcmEncoding = outFmt.GetInteger(MediaFormat.KeyPcmEncoding);
                } else if (outIdx >= 0) {
                    drainRetries = 0;
                    if (info.Size > 0) {
                        Java.Nio.ByteBuffer outBuf = codec.GetOutputBuffer(outIdx);
                        outBuf.Position(info.Offset);
                        byte[] chunk = new byte[info.Size];
                        outBuf.Get(chunk);

                        ResolveMediaCodecPcmFormat(pcmEncoding, out int bits, out bool isFloat);
                        int bytesPerSample = bits / 8;
                        int frameBytes = bytesPerSample * outChannels;
                        int frames = frameBytes > 0 ? chunk.Length / frameBytes : 0;
                        if (frames > 0) {
                            KaiserSincResampler.UnifyPcmBytesToStereoFloat(chunk, frames * frameBytes, outChannels, bits, isFloat, out float[] cL, out float[] cR);
                            int take = Math.Min(frames, wantSamples - mono.Count);
                            for (int f = 0; f < take; f++) {
                                mono.Add(0.5f * (cL[f] + cR[f]));
                            }
                        }
                    }
                    codec.ReleaseOutputBuffer(outIdx, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) break;
                } else if (inputDone && outIdx == (int)MediaCodecInfoState.TryAgainLater) {
                    if (++drainRetries >= 40) break;
                }
            }

            if (mono.Count < SubSampleSyncEngine.GccFftSize) {
                return null;
            }
            float[] rawMono = mono.ToArray();
            return srcSr != targetSampleRate
                ? new KaiserSincResampler(srcSr, targetSampleRate).ResampleMono(rawMono)
                : rawMono;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            return null;
        } finally {
            try { codec?.Stop(); } catch { }
            try { codec?.Release(); } catch { }
        }
    }

    static void ResolveMediaCodecPcmFormat(int pcmEncoding, out int bits, out bool isFloat) {
        if (pcmEncoding == (int)Android.Media.Encoding.PcmFloat) {
            bits = 32;
            isFloat = true;
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            if (pcmEncoding == (int)Android.Media.Encoding.Pcm24bitPacked) {
                bits = 24;
                isFloat = false;
                return;
            }
            if (pcmEncoding == (int)Android.Media.Encoding.Pcm32bit) {
                bits = 32;
                isFloat = false;
                return;
            }
        }
        bits = 16;
        isFloat = false;
    }

    static string NormalizeBaseName(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        StringBuilder sb = new(raw.Length);
        string cleaned = NormalizeStripTokenRegex().Replace(raw, " ");
        for (int i = 0; i < cleaned.Length; i++) {
            char c = cleaned[i];
            if (char.IsLetterOrDigit(c)) {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    static string ResolveSafeFfmpegPath(Context ctx, string ffmpegPath) {
        string resolved = FFmpegInstallerService.ResolveExistingFFmpegPath(ctx, null);
        if (!string.IsNullOrEmpty(resolved) && IOFile.Exists(resolved)) {
            return resolved;
        }
        if (!string.IsNullOrEmpty(ffmpegPath) && !IsWritableAppDataPath(ctx, ffmpegPath) && IOFile.Exists(ffmpegPath)) {
            return ffmpegPath;
        }
        return "";
    }

    static bool IsWritableAppDataPath(Context ctx, string path) {
        if (string.IsNullOrEmpty(path)) return false;
        try {
            string full = IOPath.GetFullPath(path);
            string files = ctx?.FilesDir?.AbsolutePath;
            string cache = ctx?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(files) && full.StartsWith(IOPath.GetFullPath(files), StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (!string.IsNullOrEmpty(cache) && full.StartsWith(IOPath.GetFullPath(cache), StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            return full.Replace('\\', '/').Contains("/files/bin/", StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static bool IsUriString(string value) =>
        !string.IsNullOrEmpty(value) &&
        (value.StartsWith("content://", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    static Android.Net.Uri TryParseUri(string pathOrUri) {
        if (IsUriString(pathOrUri)) {
            try {
                return Android.Net.Uri.Parse(pathOrUri);
            } catch {
                return null;
            }
        }
        return null;
    }

    static bool CanOpenFileForRead(string path) {
        if (string.IsNullOrEmpty(path) || IsUriString(path)) return false;
        try {
            if (!IOFile.Exists(path)) return false;
            using FileStream fs = IOFile.OpenRead(path);
            return fs.CanRead;
        } catch {
            return false;
        }
    }

    static string ResolveReadableMediaPath(Context ctx, Android.Net.Uri uri, string path) {
        if (CanOpenFileForRead(path)) {
            return path;
        }
        if (uri != null && ctx != null) {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(ctx, uri);
                if (CanOpenFileForRead(resolved)) {
                    return resolved;
                }
            } catch {
                // Unreadable scoped storage URI
            }
        }
        return "";
    }

    static string ResolveReadableTrackPath(Context ctx, LibraryTrackItem item) {
        if (item == null) return "";
        if (CanOpenFileForRead(item.FilePath)) {
            return item.FilePath;
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        return ResolveReadableMediaPath(ctx, uri, item.FilePath);
    }

    static string GetPathOrUriExtension(Context ctx, string pathOrUri) {
        if (string.IsNullOrEmpty(pathOrUri)) return "";
        if (!IsUriString(pathOrUri)) {
            return IOPath.GetExtension(pathOrUri).ToLowerInvariant();
        }
        Android.Net.Uri uri = TryParseUri(pathOrUri);
        if (uri != null && ctx != null) {
            string disp = AndroidFileHelper.GetDisplayName(ctx, uri);
            if (!string.IsNullOrEmpty(disp)) {
                return IOPath.GetExtension(disp).ToLowerInvariant();
            }
        }
        return "";
    }

    static string GetTrackFileName(Context ctx, LibraryTrackItem item, string fallbackPath) {
        if (item != null && !string.IsNullOrEmpty(item.FilePath) && !IsUriString(item.FilePath)) {
            return IOPath.GetFileName(item.FilePath);
        }
        Android.Net.Uri uri = item?.ContentUri ?? TryParseUri(fallbackPath);
        if (uri != null && ctx != null) {
            string disp = AndroidFileHelper.GetDisplayName(ctx, uri);
            if (!string.IsNullOrEmpty(disp)) return disp;
        }
        return !string.IsNullOrEmpty(item?.Title) ? item.Title : IOPath.GetFileName(fallbackPath ?? "");
    }
}
