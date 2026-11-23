using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Android.Content;
using Android.Media;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Strict three-tier spatial hierarchy used for variant ordering and intelligent album continuity.
/// </summary>
public enum AudioTierCategory {
    Atmos = 0,
    Surround = 1,
    Stereo = 2
}

/// <summary>
/// Multi-factor acoustic and codec quality report for a single media file.
/// Combines header/psychoacoustic codec efficiency with real PCM/FFT spectral, bit-depth, dynamic-range, and clipping measurements.
/// </summary>
public sealed class AudioQualityReport {
    public string CacheKey { get; set; } = "";
    public AudioTierCategory Category { get; set; } = AudioTierCategory.Stereo;
    public double Score { get; set; }
    public double BaseCodecScore { get; set; }
    public string CodecFamily { get; set; } = "";
    public bool IsLossless { get; set; }
    public int BitrateKbps { get; set; }
    public int SampleRateHz { get; set; }
    public int Channels { get; set; }
    public int DeclaredBitDepth { get; set; }
    public int EffectiveBitDepth { get; set; }
    public bool IsFake24Bit { get; set; }
    public int SpectralCutoffHz { get; set; }
    public bool IsFakeLossless { get; set; }
    public bool IsUpsampledHiRes { get; set; }
    public double DynamicRangeDb { get; set; }
    public double ClippedPercent { get; set; }
    public bool DeepAnalyzed { get; set; }
}

/// <summary>
/// Scientific multi-factor audio quality ranking engine:
/// 1. Strict 3-tier classification: Atmos -> Surround -> Stereo.
/// 2. Psychoacoustic codec efficiency curves (e.g. Opus 192k / AAC 256k > MP3 320k; TrueHD Atmos > E-AC-3 JOC 768k > 448k).
/// 3. Real on-device PCM/FFT signal inspection (cached on disk):
///    - True effective bit-depth (detects zero-padded fake 24-bit).
///    - 4096-pt Hann FFT spectral cutoff detection (detects fake lossless transcoded from lossy &amp; upsampled Hi-Res).
///    - Dynamic Range (crest factor) and consecutive full-scale hard-clipping detection.
/// 4. Smart Album Continuity fallback chain across tracks.
/// </summary>
public static class AudioQualityAnalyzer {
    const string CacheFileName = "quality_analysis_v1.json";
    const int MaxCacheEntries = 4000;

    static readonly ConcurrentDictionary<string, AudioQualityReport> memCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<string> cacheLru = new();
    static readonly Dictionary<string, LinkedListNode<string>> cacheLruNodes = new(StringComparer.OrdinalIgnoreCase);
    static readonly object cacheLruLock = new();
    static readonly object diskLock = new();
    static bool diskLoaded;
    static string cachedDiskPath;

    /// <summary>
    /// Classifies a track into one of the three strict audio tiers: Atmos, Surround, or Stereo.
    /// </summary>
    public static AudioTierCategory ClassifyCategory(LibraryTrackItem item) {
        if (item == null) {
            return AudioTierCategory.Stereo;
        }
        string badge = (item.AudioBadge ?? "").ToUpperInvariant();
        string fmt = (item.FormatBadge ?? "").ToUpperInvariant();
        if (item.IsSpatial ||
            badge.Contains("ATMOS", StringComparison.Ordinal) ||
            badge.Contains("JOC", StringComparison.Ordinal) ||
            badge.Contains("DAMF", StringComparison.Ordinal) ||
            badge.Contains("ADM", StringComparison.Ordinal) ||
            badge.Contains("3D", StringComparison.Ordinal) ||
            fmt is "ATMOS" or "DAMF" or "LAF") {
            return AudioTierCategory.Atmos;
        }
        if (item.IsSurround ||
            item.ChannelCount > 2 ||
            badge.Contains("5.1", StringComparison.Ordinal) ||
            badge.Contains("7.1", StringComparison.Ordinal) ||
            badge.Contains("4.0", StringComparison.Ordinal) ||
            badge.Contains("SURROUND", StringComparison.Ordinal) ||
            badge.Contains("TRUEHD", StringComparison.Ordinal) ||
            badge.Contains("DTS", StringComparison.Ordinal) ||
            fmt is "AC3" or "EAC3" or "EC3" or "THD" or "MLP") {
            return AudioTierCategory.Surround;
        }
        return AudioTierCategory.Stereo;
    }

    /// <summary>
    /// Builds a stable disk cache key that invalidates automatically if file size or modified time changes.
    /// </summary>
    public static string BuildCacheKey(LibraryTrackItem item) {
        if (item == null) return "";
        string id = !string.IsNullOrEmpty(item.FilePath) ? item.FilePath : item.ContentUriString;
        return $"{id}|{item.FileSizeBytes}|{item.LastModifiedTicks}";
    }

    /// <summary>
    /// Loads the disk cache once into memory (safe to call repeatedly).
    /// </summary>
    public static void EnsureCacheLoaded(Context ctx) {
        if (diskLoaded || ctx == null) {
            return;
        }
        lock (diskLock) {
            if (diskLoaded) return;
            try {
                string root = ctx.FilesDir?.AbsolutePath ?? ctx.CacheDir?.AbsolutePath;
                if (string.IsNullOrEmpty(root)) {
                    return;
                }
                cachedDiskPath = IOPath.Combine(root, CacheFileName);
                if (IOFile.Exists(cachedDiskPath)) {
                    try {
                        string json = IOFile.ReadAllText(cachedDiskPath);
                        List<AudioQualityReport> list = JsonSerializer.Deserialize<List<AudioQualityReport>>(json);
                        if (list != null) {
                            for (int i = 0; i < list.Count; i++) {
                                if (!string.IsNullOrEmpty(list[i]?.CacheKey)) {
                                    StoreCacheEntry(list[i].CacheKey, list[i]);
                                }
                            }
                        }
                    } catch (JsonException jex) {
                        AppLogger.Warn($"AudioQualityAnalyzer corrupt cache removed: {jex.Message}");
                        try { IOFile.Delete(cachedDiskPath); } catch { }
                    }
                }
            } catch (Exception ex) {
                AppLogger.Warn($"AudioQualityAnalyzer cache read skipped: {ex.Message}");
            } finally {
                diskLoaded = true;
            }
        }
    }

    static void StoreCacheEntry(string key, AudioQualityReport report) {
        if (string.IsNullOrEmpty(key) || report == null) {
            return;
        }
        memCache[key] = report;
        lock (cacheLruLock) {
            if (cacheLruNodes.TryGetValue(key, out LinkedListNode<string> node)) {
                cacheLru.Remove(node);
                cacheLru.AddLast(node);
            } else {
                node = cacheLru.AddLast(key);
                cacheLruNodes[key] = node;
            }
            while (cacheLru.Count > MaxCacheEntries) {
                LinkedListNode<string> oldest = cacheLru.First;
                if (oldest == null) break;
                cacheLru.RemoveFirst();
                cacheLruNodes.Remove(oldest.Value);
                memCache.TryRemove(oldest.Value, out _);
            }
        }
    }

    static void TouchCacheEntry(string key) {
        if (string.IsNullOrEmpty(key)) {
            return;
        }
        lock (cacheLruLock) {
            if (cacheLruNodes.TryGetValue(key, out LinkedListNode<string> node)) {
                cacheLru.Remove(node);
                cacheLru.AddLast(node);
            }
        }
    }

    static void PersistCache() {
        if (string.IsNullOrEmpty(cachedDiskPath)) {
            return;
        }
        lock (diskLock) {
            try {
                List<string> keysSnapshot;
                lock (cacheLruLock) {
                    keysSnapshot = [.. cacheLru];
                }
                List<AudioQualityReport> snapshot = new(Math.Min(keysSnapshot.Count, MaxCacheEntries));
                for (int i = Math.Max(0, keysSnapshot.Count - MaxCacheEntries); i < keysSnapshot.Count; i++) {
                    if (memCache.TryGetValue(keysSnapshot[i], out AudioQualityReport rep) && rep != null && !string.IsNullOrEmpty(rep.CacheKey)) {
                        snapshot.Add(rep);
                    }
                }
                if (snapshot.Count == 0 && !memCache.IsEmpty) {
                    snapshot = memCache.Values
                        .Where(r => r != null && !string.IsNullOrEmpty(r.CacheKey))
                        .Take(MaxCacheEntries)
                        .ToList();
                }
                string json = JsonSerializer.Serialize(snapshot);
                MediaServiceUtils.WriteTextAtomic(cachedDiskPath, json);
            } catch {
                // Non-critical cache write failure
            }
        }
    }

    /// <summary>
    /// Instant non-blocking quality evaluation (returns cached deep spectral analysis when available,
    /// or pure in-memory codec evaluation immediately unless <paramref name="allowIo"/> is true on a background thread).
    /// </summary>
    public static AudioQualityReport EvaluateFast(LibraryTrackItem item, Context ctx = null, bool allowIo = false) {
        if (ctx != null) {
            if (allowIo) {
                EnsureCacheLoaded(ctx);
            } else if (!diskLoaded) {
                Context bgCtx = ctx.ApplicationContext ?? ctx;
                _ = Task.Run(() => EnsureCacheLoaded(bgCtx));
            }
        }
        if (item == null) {
            return new AudioQualityReport();
        }
        string key = BuildCacheKey(item);
        if (memCache.TryGetValue(key, out AudioQualityReport cached)) {
            TouchCacheEntry(key);
            return cached;
        }

        AudioQualityReport report = BuildFastHeuristicReport(ctx, item, key, allowIo);
        StoreCacheEntry(key, report);
        return report;
    }

    /// <summary>
    /// Sorts a list of track variants strictly by tier (Atmos -> Surround -> Stereo)
    /// and within each tier by descending scientific audio quality score (highest quality first).
    /// </summary>
    public static void SortVariantsIntelligently(List<LibraryTrackItem> variants, Context ctx = null) {
        if (variants == null || variants.Count <= 1) {
            return;
        }
        if (ctx != null && !diskLoaded) {
            Context bgCtx = ctx.ApplicationContext ?? ctx;
            _ = Task.Run(() => EnsureCacheLoaded(bgCtx));
        }
        Dictionary<LibraryTrackItem, AudioQualityReport> scores = new(variants.Count);
        for (int i = 0; i < variants.Count; i++) {
            scores[variants[i]] = EvaluateFast(variants[i], ctx);
        }
        variants.Sort((a, b) => {
            AudioQualityReport ra = scores[a];
            AudioQualityReport rb = scores[b];
            int catCmp = ((int)ra.Category).CompareTo((int)rb.Category);
            if (catCmp != 0) return catCmp;
            int scoreCmp = rb.Score.CompareTo(ra.Score);
            if (scoreCmp != 0) return scoreCmp;
            int sizeCmp = b.FileSizeBytes.CompareTo(a.FileSizeBytes);
            if (sizeCmp != 0) return sizeCmp;
            return string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Picks the optimal variant for the next track in an album/queue according to the user's sticky category rule:
    /// - Atmos -> Atmos -> Surround -> Stereo
    /// - Surround -> Surround -> Atmos -> Stereo
    /// - Stereo -> Stereo -> Atmos -> Surround
    /// Always selects the highest-ranked quality variant within the matched category.
    /// </summary>
    public static LibraryTrackItem PickBestVariantForNextTrack(
        IReadOnlyList<LibraryTrackItem> variants,
        AudioTierCategory preferredCategory,
        Context ctx = null) {
        if (variants == null || variants.Count == 0) {
            return null;
        }
        List<LibraryTrackItem> sorted = [.. variants];
        SortVariantsIntelligently(sorted, ctx);

        AudioTierCategory[] fallbackOrder = preferredCategory switch {
            AudioTierCategory.Atmos => [AudioTierCategory.Atmos, AudioTierCategory.Surround, AudioTierCategory.Stereo],
            AudioTierCategory.Surround => [AudioTierCategory.Surround, AudioTierCategory.Atmos, AudioTierCategory.Stereo],
            _ => [AudioTierCategory.Stereo, AudioTierCategory.Atmos, AudioTierCategory.Surround]
        };

        for (int c = 0; c < fallbackOrder.Length; c++) {
            AudioTierCategory wanted = fallbackOrder[c];
            for (int i = 0; i < sorted.Count; i++) {
                if (ClassifyCategory(sorted[i]) == wanted) {
                    return sorted[i];
                }
            }
        }
        return sorted[0];
    }

    /// <summary>
    /// Runs deep PCM / FFT / bit-depth / dynamic-range / clipping analysis in the background
    /// for all variants that have not been deep-analyzed yet, then invokes <paramref name="onAnyUpdated"/> if any report changed.
    /// </summary>
    public static void AnalyzeVariantsInBackground(
        Context ctx,
        IReadOnlyList<LibraryTrackItem> variants,
        string ffmpegPath,
        Action onAnyUpdated,
        CancellationToken ct = default) {
        if (ctx == null || variants == null || variants.Count == 0 || ct.IsCancellationRequested) {
            return;
        }
        EnsureCacheLoaded(ctx);
        string safeFfmpeg = ResolveSafeFfmpegPath(ctx, ffmpegPath);
        List<(LibraryTrackItem Item, string Key)> pending = [];
        for (int i = 0; i < variants.Count; i++) {
            if (ct.IsCancellationRequested) break;
            LibraryTrackItem v = variants[i];
            if (v == null || !HasReadableSource(v)) continue;
            string k = BuildCacheKey(v);
            if (memCache.TryGetValue(k, out AudioQualityReport rep) && rep.DeepAnalyzed) continue;
            if (inFlight.TryAdd(k, 1)) {
                pending.Add((v, k));
            }
        }
        if (pending.Count == 0) {
            return;
        }

        Task.Run(() => {
            bool updated = false;
            try {
                for (int i = 0; i < pending.Count; i++) {
                    if (ct.IsCancellationRequested) {
                        break;
                    }
                    var (item, key) = pending[i];
                    try {
                        AudioQualityReport deep = RunDeepAnalysis(ctx, item, safeFfmpeg, ct);
                        if (deep != null) {
                            StoreCacheEntry(key, deep);
                            updated = true;
                        }
                    } catch (OperationCanceledException) {
                        break;
                    } catch (Exception ex) {
                        AppLogger.Warn($"Deep quality analysis skipped for '{GetTrackDisplayLabel(ctx, item)}': {ex.Message}");
                    } finally {
                        inFlight.TryRemove(key, out _);
                    }
                }
                if (updated) {
                    PersistCache();
                    if (!ct.IsCancellationRequested) {
                        onAnyUpdated?.Invoke();
                    }
                }
            } catch {
                // Keep playback undisturbed
            } finally {
                for (int i = 0; i < pending.Count; i++) {
                    inFlight.TryRemove(pending[i].Key, out _);
                }
            }
        });
    }

    // ============================================================
    // FAST HEADER + PSYCHOACOUSTIC CODEC SCORING
    // ============================================================
    static AudioQualityReport BuildFastHeuristicReport(Context ctx, LibraryTrackItem item, string cacheKey, bool allowIo) {
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        AudioTierCategory cat = ClassifyCategory(item);
        string ext = GetTrackExtension(effectiveCtx, item);
        string badge = (item.AudioBadge ?? "").ToUpperInvariant();
        string fmt = (item.FormatBadge ?? "").ToUpperInvariant();

        int sr = 48000;
        int bitDepth = 16;
        int channels = item.ChannelCount > 0 ? item.ChannelCount : (cat == AudioTierCategory.Stereo ? 2 : 6);
        int bitrateKbps = EstimateBitrateKbps(item);
        string codecFamily = DetectCodecFamily(ext, fmt, badge, out bool isLossless);

        // Perform binary header / MediaExtractor probing ONLY when explicitly permitted on a background thread
        if (allowIo) {
            try {
                if (HasReadableSource(item)) {
                    if (ext == ".flac") {
                        TryReadFlacHeader(effectiveCtx, item, ref sr, ref bitDepth, ref channels);
                        isLossless = true;
                        codecFamily = "FLAC";
                    } else if (ext == ".wav") {
                        TryReadWavHeader(effectiveCtx, item, ref sr, ref bitDepth, ref channels);
                        isLossless = true;
                        codecFamily = "WAV/PCM";
                    } else if (ext is ".m4a" or ".mp4" or ".mka" or ".mkv" or ".ogg" or ".opus" or ".mp3" or ".aac") {
                        TryReadMediaExtractorMeta(effectiveCtx, item, ref sr, ref bitDepth, ref channels, ref bitrateKbps, ref codecFamily, ref isLossless);
                    }
                }
            } catch {
                // Fall back to estimated metadata
            }
        }

        double baseScore = ComputePsychoacousticBaseScore(cat, codecFamily, isLossless, bitrateKbps, sr, bitDepth, channels, badge);
        return new AudioQualityReport {
            CacheKey = cacheKey,
            Category = cat,
            Score = baseScore,
            BaseCodecScore = baseScore,
            CodecFamily = codecFamily,
            IsLossless = isLossless,
            BitrateKbps = bitrateKbps,
            SampleRateHz = sr,
            Channels = channels,
            DeclaredBitDepth = bitDepth,
            EffectiveBitDepth = bitDepth,
            DeepAnalyzed = false
        };
    }

    static int EstimateBitrateKbps(LibraryTrackItem item) {
        if (item == null || item.FileSizeBytes <= 0 || item.DurationMs <= 1000) {
            return 0;
        }
        // Subtract a conservative ~120 KB estimate for embedded album cover art on audio files
        long payloadBytes = item.FileSizeBytes > 400_000 ? item.FileSizeBytes - 120_000 : item.FileSizeBytes;
        double sec = item.DurationMs / 1000.0;
        return Math.Clamp((int)Math.Round(payloadBytes * 8.0 / sec / 1000.0), 16, 25000);
    }

    static string DetectCodecFamily(string ext, string fmt, string badge, out bool isLossless) {
        isLossless = false;
        if (badge.Contains("DAMF", StringComparison.Ordinal) || badge.Contains("ADM", StringComparison.Ordinal) || ext == ".atmos") {
            isLossless = true;
            return "DAMF/ADM";
        }
        if (badge.Contains("TRUEHD", StringComparison.Ordinal) || ext is ".thd" or ".mlp" || fmt is "THD" or "MLP") {
            isLossless = true;
            return badge.Contains("ATMOS", StringComparison.Ordinal) ? "TrueHD Atmos" : "TrueHD";
        }
        if (badge.Contains("DTS-HD MA", StringComparison.Ordinal) || badge.Contains("DTS:X", StringComparison.Ordinal)) {
            isLossless = true;
            return "DTS-HD MA";
        }
        if (ext == ".flac" || fmt == "FLAC") {
            isLossless = true;
            return "FLAC";
        }
        if (ext == ".wav" || fmt == "WAV" || fmt == "PCM") {
            isLossless = true;
            return "WAV/PCM";
        }
        if (ext == ".laf" || fmt == "LAF") {
            isLossless = true;
            return "LAF";
        }
        if (badge.Contains("ALAC", StringComparison.Ordinal)) {
            isLossless = true;
            return "ALAC";
        }
        if (badge.Contains("AC-4", StringComparison.Ordinal) || badge.Contains("AC4", StringComparison.Ordinal)) {
            return "AC-4";
        }
        if (badge.Contains("JOC", StringComparison.Ordinal) || badge.Contains("E-AC-3", StringComparison.Ordinal) ||
            badge.Contains("DD+", StringComparison.Ordinal) || ext is ".eac3" or ".ec3") {
            return "E-AC-3";
        }
        if (badge.Contains("AC-3", StringComparison.Ordinal) || ext == ".ac3") {
            return "AC-3";
        }
        if (badge.Contains("DTS", StringComparison.Ordinal)) {
            return "DTS";
        }
        if (ext == ".opus" || fmt == "OPUS" || badge.Contains("OPUS", StringComparison.Ordinal)) {
            return "Opus";
        }
        if (ext == ".ogg" || fmt == "OGG" || badge.Contains("VORBIS", StringComparison.Ordinal)) {
            return "Vorbis";
        }
        if (ext is ".m4a" or ".aac" or ".mp4" or ".caf" || fmt is "M4A" or "AAC" || badge.Contains("AAC", StringComparison.Ordinal)) {
            return "AAC";
        }
        if (ext == ".mp3" || fmt == "MP3") {
            return "MP3";
        }
        return string.IsNullOrWhiteSpace(fmt) ? "AUDIO" : fmt;
    }

    static double ComputePsychoacousticBaseScore(
        AudioTierCategory cat,
        string codec,
        bool isLossless,
        int bitrateKbps,
        int sampleRateHz,
        int bitDepth,
        int channels,
        string badge) {
        double score;
        if (isLossless) {
            if (cat == AudioTierCategory.Atmos) {
                score = codec.Contains("DAMF", StringComparison.OrdinalIgnoreCase) ? 965.0
                    : codec.Contains("TrueHD", StringComparison.OrdinalIgnoreCase) ? 950.0
                    : 935.0;
            } else if (cat == AudioTierCategory.Surround) {
                score = codec.Contains("TrueHD", StringComparison.OrdinalIgnoreCase) ? 930.0
                    : codec.Contains("DTS-HD", StringComparison.OrdinalIgnoreCase) ? 925.0
                    : 910.0;
            } else {
                score = 885.0;
            }

            // Modest provisional resolution credits (verified/corrected once deep analysis inspects real bits & spectrum)
            if (bitDepth >= 24) score += 20.0;
            if (sampleRateHz >= 88200) score += 12.0;
            else if (sampleRateHz >= 48000) score += 4.0;
            return score;
        }

        // Lossy codec psychoacoustic efficiency multiplier (MP3 = 1.00 reference)
        double efficiency = codec.ToUpperInvariant() switch {
            "OPUS" => 1.95,
            "AC-4" => 1.70,
            "XHE-AAC" => 1.70,
            "AAC" => 1.42,
            "VORBIS" => 1.35,
            "E-AC-3" => 1.35,
            "DTS" => 0.90,
            "AC-3" => 0.88,
            "MP3" => 1.00,
            _ => 1.05
        };

        int effectiveCh = Math.Max(1, channels);
        int br = bitrateKbps > 0 ? bitrateKbps : (cat == AudioTierCategory.Atmos ? 768 : (cat == AudioTierCategory.Surround ? 448 : 256));

        // Channel coupling factor: 6ch surround doesn't need 3x stereo bitrate due to LFE + inter-channel correlation
        double chFactor = effectiveCh <= 2 ? 1.0 : (1.0 + (effectiveCh - 2) * 0.48);
        double stereoEqBitrate = (br / chFactor) * efficiency;

        // Smooth psychoacoustic transparency curve (asymptotically approaches ~845 for ultra-high lossy, never beating clean lossless)
        double curve = 845.0 * (1.0 - Math.Exp(-stereoEqBitrate / 165.0));
        score = Math.Clamp(curve, 220.0, 842.0);

        // Atmos object bitrate weighting (e.g. 768k E-AC-3 JOC > 448k E-AC-3 JOC > 384k)
        if (cat == AudioTierCategory.Atmos) {
            double atmosBonus = Math.Clamp((br - 256.0) / 512.0 * 34.0, -20.0, 42.0);
            score = Math.Clamp(score + atmosBonus, 300.0, 855.0);
        } else if (cat == AudioTierCategory.Surround && channels >= 8) {
            score += 14.0; // 7.1 bed advantage over 5.1 at comparable per-channel fidelity
        }

        // Tiny sample-rate tie-breaker (48k > 44.1k for modern codecs like Opus/E-AC-3)
        if (sampleRateHz >= 48000) score += 2.0;
        return Math.Round(score, 1);
    }

    static void TryReadFlacHeader(Context ctx, LibraryTrackItem item, ref int sr, ref int bitDepth, ref int channels) {
        using System.IO.Stream fs = OpenTrackStream(ctx, item);
        if (fs == null) return;
        byte[] buf = new byte[42];
        if (!ReadExactBytes(fs, buf)) return;
        if (buf[0] != 'f' || buf[1] != 'L' || buf[2] != 'a' || buf[3] != 'C') return;
        // STREAMINFO starts at offset 8 (4 bytes header + 4 bytes block header)
        if ((buf[4] & 0x7F) != 0) return;
        int o = 8 + 10; // skip min/max block & frame sizes (2+2+3+3 = 10 bytes)
        ulong packed = ((ulong)buf[o] << 24) | ((ulong)buf[o + 1] << 16) | ((ulong)buf[o + 2] << 8) | buf[o + 3];
        int parsedSr = (int)(packed >> 12);
        int parsedCh = (int)((packed >> 9) & 0x07) + 1;
        int parsedBits = (int)((packed >> 4) & 0x1F) + 1;
        if (parsedSr is >= 8000 and <= 384000) sr = parsedSr;
        if (parsedCh is >= 1 and <= 16) channels = parsedCh;
        if (parsedBits is >= 8 and <= 32) bitDepth = parsedBits;
    }

    static bool TryFindWavHeaderAndData(
        System.IO.Stream fs,
        out int audioFmt,
        out int channels,
        out int sr,
        out int bits,
        out long dataOffset,
        out long dataLen,
        bool stopAtFmtOnly) {
        audioFmt = 1;
        channels = 0;
        sr = 0;
        bits = 0;
        dataOffset = -1;
        dataLen = 0;
        if (fs == null) return false;
        if (fs.CanSeek && fs.Length < 44) return false;

        Span<byte> riffHdr = stackalloc byte[12];
        if (!ReadExactBytes(fs, riffHdr)) return false;
        bool isRiff = riffHdr[0] == 'R' && riffHdr[1] == 'I' && riffHdr[2] == 'F' && riffHdr[3] == 'F';
        bool isRf64 = riffHdr[0] == 'R' && riffHdr[1] == 'F' && riffHdr[2] == '6' && riffHdr[3] == '4';
        if ((!isRiff && !isRf64) || riffHdr[8] != 'W' || riffHdr[9] != 'A' || riffHdr[10] != 'V' || riffHdr[11] != 'E') {
            return false;
        }

        bool foundFmt = false;
        long rf64DataSize = -1;
        Span<byte> chunkHdr = stackalloc byte[8];
        Span<byte> fmtBuf = stackalloc byte[40];
        Span<byte> ds64Buf = stackalloc byte[24];
        int maxChunks = 512;

        if (!fs.CanSeek) {
            long bytesScanned = 12;
            while (maxChunks-- > 0 && bytesScanned < 262144) {
                if (!ReadExactBytes(fs, chunkHdr)) break;
                bytesScanned += 8;
                uint szU32 = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..8]);
                bool isDs64 = chunkHdr[0] == 'd' && chunkHdr[1] == 's' && chunkHdr[2] == '6' && chunkHdr[3] == '4';
                bool isFmt = chunkHdr[0] == 'f' && chunkHdr[1] == 'm' && chunkHdr[2] == 't' && chunkHdr[3] == ' ';
                bool isData = chunkHdr[0] == 'd' && chunkHdr[1] == 'a' && chunkHdr[2] == 't' && chunkHdr[3] == 'a';
                long consumed = 0;

                if (isDs64 && szU32 >= 24) {
                    if (ReadExactBytes(fs, ds64Buf)) {
                        rf64DataSize = BinaryPrimitives.ReadInt64LittleEndian(ds64Buf.Slice(8, 8));
                    }
                    consumed = 24;
                } else if (isFmt && szU32 >= 16) {
                    int toRead = (int)Math.Min(40, szU32);
                    if (ReadExactBytes(fs, fmtBuf[..toRead])) {
                        audioFmt = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf[..2]);
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(2, 2));
                        sr = BinaryPrimitives.ReadInt32LittleEndian(fmtBuf.Slice(4, 4));
                        bits = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(14, 2));
                        foundFmt = true;
                        if (stopAtFmtOnly) return true;
                    }
                    consumed = toRead;
                } else if (isData) {
                    if (foundFmt) return true;
                }

                long chunkLen = (szU32 == 0xFFFFFFFF && isData && rf64DataSize > 0) ? rf64DataSize : szU32;
                long paddedLen = chunkLen + (chunkLen & 1L);
                long skip = paddedLen - consumed;
                if (skip > 262144 || !SkipBytes(fs, skip)) break;
                bytesScanned += paddedLen;
            }
            return foundFmt;
        }

        while (maxChunks-- > 0 && fs.Position + 8 <= fs.Length) {
            if (!ReadExactBytes(fs, chunkHdr)) break;
            uint szU32 = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr[4..8]);
            long chunkDataStart = fs.Position;

            bool isDs64 = chunkHdr[0] == 'd' && chunkHdr[1] == 's' && chunkHdr[2] == '6' && chunkHdr[3] == '4';
            bool isFmt = chunkHdr[0] == 'f' && chunkHdr[1] == 'm' && chunkHdr[2] == 't' && chunkHdr[3] == ' ';
            bool isData = chunkHdr[0] == 'd' && chunkHdr[1] == 'a' && chunkHdr[2] == 't' && chunkHdr[3] == 'a';

            if (isDs64 && szU32 >= 24 && chunkDataStart + 24 <= fs.Length) {
                if (ReadExactBytes(fs, ds64Buf)) {
                    rf64DataSize = BinaryPrimitives.ReadInt64LittleEndian(ds64Buf.Slice(8, 8));
                }
            } else if (isFmt && szU32 >= 16 && chunkDataStart + 16 <= fs.Length) {
                int toRead = (int)Math.Min(40, Math.Min(szU32, (ulong)(fs.Length - chunkDataStart)));
                if (ReadExactBytes(fs, fmtBuf[..toRead])) {
                    audioFmt = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf[..2]);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(2, 2));
                    sr = BinaryPrimitives.ReadInt32LittleEndian(fmtBuf.Slice(4, 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.Slice(14, 2));
                    foundFmt = true;
                    if (stopAtFmtOnly) {
                        return true;
                    }
                }
            } else if (isData) {
                dataOffset = chunkDataStart;
                long avail = fs.Length - dataOffset;
                if (szU32 == 0xFFFFFFFF && rf64DataSize > 0) {
                    dataLen = Math.Min(rf64DataSize, avail);
                } else if (szU32 > 0) {
                    dataLen = Math.Min(szU32, avail);
                } else {
                    dataLen = avail;
                }
                if (foundFmt) {
                    return true;
                }
            }

            long chunkLen = (szU32 == 0xFFFFFFFF && isData && rf64DataSize > 0) ? rf64DataSize : szU32;
            long paddedLen = chunkLen + (chunkLen & 1L);
            long nextPos = chunkDataStart + paddedLen;
            if (nextPos <= chunkDataStart || nextPos > fs.Length) break;
            fs.Position = nextPos;
        }
        return foundFmt;
    }

    static void TryReadWavHeader(Context ctx, LibraryTrackItem item, ref int sr, ref int bitDepth, ref int channels) {
        using System.IO.Stream fs = OpenTrackStream(ctx, item);
        if (fs == null || !TryFindWavHeaderAndData(fs, out _, out int ch, out int s, out int b, out _, out _, stopAtFmtOnly: true)) {
            return;
        }
        if (ch is >= 1 and <= 32) channels = ch;
        if (s is >= 8000 and <= 384000) sr = s;
        if (b is >= 8 and <= 64) bitDepth = b;
    }

    static void TryReadMediaExtractorMeta(
        Context ctx,
        LibraryTrackItem item,
        ref int sr,
        ref int bitDepth,
        ref int channels,
        ref int bitrateKbps,
        ref string codecFamily,
        ref bool isLossless) {
        MediaExtractor extractor = null;
        try {
            extractor = new MediaExtractor();
            if (!TrySetExtractorSource(extractor, ctx, item)) {
                return;
            }
            for (int i = 0; i < extractor.TrackCount; i++) {
                MediaFormat fmt = extractor.GetTrackFormat(i);
                string mime = fmt.GetString(MediaFormat.KeyMime) ?? "";
                if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;

                if (fmt.ContainsKey(MediaFormat.KeySampleRate)) {
                    int s = fmt.GetInteger(MediaFormat.KeySampleRate);
                    if (s > 0) sr = s;
                }
                if (fmt.ContainsKey(MediaFormat.KeyChannelCount)) {
                    int c = fmt.GetInteger(MediaFormat.KeyChannelCount);
                    if (c > 0) channels = c;
                }
                if (fmt.ContainsKey(MediaFormat.KeyBitRate)) {
                    int br = fmt.GetInteger(MediaFormat.KeyBitRate);
                    if (br > 8000) bitrateKbps = br / 1000;
                }
                if (fmt.ContainsKey("bits-per-sample")) {
                    int b = fmt.GetInteger("bits-per-sample");
                    if (b is >= 8 and <= 32) bitDepth = b;
                }

                string m = mime.ToLowerInvariant();
                if (m.Contains("alac")) { codecFamily = "ALAC"; isLossless = true; }
                else if (m.Contains("flac")) { codecFamily = "FLAC"; isLossless = true; }
                else if (m.Contains("truehd")) { codecFamily = "TrueHD"; isLossless = true; }
                else if (m.Contains("opus")) { codecFamily = "Opus"; isLossless = false; }
                else if (m.Contains("vorbis")) { codecFamily = "Vorbis"; isLossless = false; }
                else if (m.Contains("ac4")) { codecFamily = "AC-4"; isLossless = false; }
                else if (m.Contains("eac3")) { codecFamily = "E-AC-3"; isLossless = false; }
                else if (m.Contains("ac3")) { codecFamily = "AC-3"; isLossless = false; }
                else if (m.Contains("mp4a")) { codecFamily = "AAC"; isLossless = false; }
                else if (m.Contains("mpeg")) { codecFamily = "MP3"; isLossless = false; }
                break;
            }
        } finally {
            try { extractor?.Release(); } catch { }
            try { extractor?.Dispose(); } catch { }
        }
    }

    // ============================================================
    // DEEP PCM + FFT + EFFECTIVE BIT-DEPTH + DR + CLIPPING ENGINE
    // ============================================================
    static AudioQualityReport RunDeepAnalysis(Context ctx, LibraryTrackItem item, string ffmpegPath, CancellationToken ct = default) {
        Context effectiveCtx = ctx ?? Android.App.Application.Context;
        string safeFfmpeg = ResolveSafeFfmpegPath(effectiveCtx, ffmpegPath);
        string key = BuildCacheKey(item);
        AudioQualityReport report = BuildFastHeuristicReport(effectiveCtx, item, key, allowIo: true);

        // Extract ~2.2s representative mono-summed float window + raw int32 LSB check (for 24-bit files)
        float[] monoSamples = null;
        int decodedSr = report.SampleRateHz > 0 ? report.SampleRateHz : 48000;
        bool inspectedRaw24Bit = false;
        bool lsbZeroPadded = false;

        string ext = GetTrackExtension(effectiveCtx, item);
        string localPath = CanOpenFileForRead(item.FilePath) ? item.FilePath : null;
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);

        if (ext == ".wav") {
            if (localPath == null && uri != null) {
                localPath = TryResolveReadablePath(effectiveCtx, uri);
            }
            if (!string.IsNullOrEmpty(localPath)) {
                monoSamples = DecodeWavWindow(localPath, out int wavSr, out int wavBits, out inspectedRaw24Bit, out lsbZeroPadded);
                if (monoSamples != null && wavSr > 0) decodedSr = wavSr;
                if (wavBits > 0) report.DeclaredBitDepth = wavBits;
            }
        }

        // If 24-bit FLAC/ALAC/TrueHD and FFmpeg is available, read exact 32-bit PCM so we can genuinely detect fake 24-bit
        if (monoSamples == null && !string.IsNullOrEmpty(safeFfmpeg) && IOFile.Exists(safeFfmpeg) &&
            (report.DeclaredBitDepth >= 24 || ext is ".thd" or ".mlp" or ".atmos" or ".dts")) {
            if (localPath == null && uri != null) {
                localPath = TryResolveReadablePath(effectiveCtx, uri);
            }
            if (!string.IsNullOrEmpty(localPath)) {
                monoSamples = DecodeWithFfmpegS32Window(localPath, safeFfmpeg, item.DurationMs, decodedSr, ct, out int ffSr, out inspectedRaw24Bit, out lsbZeroPadded);
                if (monoSamples != null && ffSr > 0) decodedSr = ffSr;
            }
        }

        // Standard Android hardware/software MediaExtractor + MediaCodec decode for all other formats
        if (monoSamples == null) {
            ct.ThrowIfCancellationRequested();
            monoSamples = DecodeWithMediaCodecWindow(effectiveCtx, item, item.DurationMs, out int mcSr, out inspectedRaw24Bit, out lsbZeroPadded);
            if (monoSamples != null && mcSr > 0) decodedSr = mcSr;
        }

        if (monoSamples == null || monoSamples.Length < 4096) {
            report.DeepAnalyzed = true;
            return report;
        }

        report.SampleRateHz = decodedSr;

        // 1. Effective Bit-Depth Check (Fake 24-bit detector)
        if (report.DeclaredBitDepth >= 24 && inspectedRaw24Bit) {
            report.IsFake24Bit = lsbZeroPadded;
            report.EffectiveBitDepth = lsbZeroPadded ? 16 : 24;
        } else {
            report.EffectiveBitDepth = report.DeclaredBitDepth;
        }

        // 2. Dynamic Range (Crest Factor / Pseudo-DR) & Hard-Clipping Analysis
        ComputeDynamicsAndClipping(monoSamples, decodedSr, out double drDb, out double clippedPct);
        report.DynamicRangeDb = Math.Round(drDb, 1);
        report.ClippedPercent = Math.Round(clippedPct, 3);

        // 3. 4096-pt Hann-Windowed FFT Spectral Bandwidth & Brickwall Cutoff Analysis
        AnalyzeSpectrum(monoSamples, decodedSr, out int cutoffHz, out bool sharpBrickwall, out double midDb, out int activeFrames);
        report.SpectralCutoffHz = cutoffHz;

        // Detect Fake Lossless (lossless container with steep lossy lowpass shelf)
        if (report.IsLossless && sharpBrickwall && cutoffHz > 0) {
            if (cutoffHz <= 17200) {
                report.IsFakeLossless = true; // Transcoded from ~128–160k MP3/AAC
            } else if (cutoffHz <= 19400 && decodedSr >= 44100) {
                report.IsFakeLossless = true; // Transcoded from ~192–256k lossy
            }
        }

        // Detect Upsampled Hi-Res (88.2 / 96 / 192 kHz file carrying strictly CD-band <= 22.5 kHz audio),
        // guarding quiet acoustic/classical passages so gentle natural treble rolloff is not false-flagged
        if (report.IsLossless && decodedSr >= 88200 && cutoffHz > 0 && cutoffHz <= 22500 &&
            (sharpBrickwall || (activeFrames >= 6 && midDb >= -36.0))) {
            report.IsUpsampledHiRes = true;
        }

        // 4. Composite Multi-Factor Final Score
        double finalScore = report.BaseCodecScore;

        // Bit-depth adjustment
        if (report.IsLossless && report.DeclaredBitDepth >= 24) {
            if (report.IsFake24Bit) {
                finalScore -= 24.0; // Remove provisional 24-bit bonus + small honesty penalty
            } else if (inspectedRaw24Bit) {
                finalScore += 8.0; // Verified true 24-bit resolution
            }
        }

        // Spectral honesty & bandwidth adjustment
        if (report.IsFakeLossless) {
            finalScore -= cutoffHz <= 17200 ? 225.0 : 145.0;
        } else if (report.IsUpsampledHiRes) {
            finalScore -= 14.0;
        } else if (report.IsLossless && decodedSr >= 88200 && cutoffHz >= 26000) {
            finalScore += 10.0; // Genuine ultrasonic Hi-Res content verified
        } else if (!report.IsLossless && cutoffHz > 0) {
            // For lossy files, reward extended un-truncated high-frequency bandwidth (e.g. Opus/AAC 20 kHz vs MP3 16 kHz)
            if (cutoffHz < 16000) finalScore -= 42.0;
            else if (cutoffHz < 17800) finalScore -= 18.0;
            else if (cutoffHz >= 19800) finalScore += 10.0;
        }

        // Dynamic Range adjustment (clean dynamic master can beat brickwalled higher-bitrate master!)
        if (drDb > 0.5) {
            if (drDb >= 12.0) finalScore += 28.0;
            else if (drDb >= 9.5) finalScore += 14.0;
            else if (drDb <= 5.5) finalScore -= 36.0;
            else if (drDb <= 7.0) finalScore -= 16.0;
        }

        // Hard-clipping distortion penalty
        if (clippedPct >= 0.8) finalScore -= 52.0;
        else if (clippedPct >= 0.25) finalScore -= 26.0;
        else if (clippedPct >= 0.08) finalScore -= 10.0;

        report.Score = Math.Round(Math.Clamp(finalScore, 50.0, 1000.0), 1);
        report.DeepAnalyzed = true;
        return report;
    }

    static float[] DecodeWavWindow(string path, out int sr, out int bits, out bool inspected24, out bool lsbZero) {
        sr = 48000;
        bits = 16;
        inspected24 = false;
        lsbZero = false;
        try {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!TryFindWavHeaderAndData(fs, out int audioFmt, out int channels, out int parsedSr, out int parsedBits, out long dataOffset, out long dataLen, stopAtFmtOnly: false)) {
                return null;
            }
            if (parsedSr is >= 8000 and <= 384000) sr = parsedSr;
            if (parsedBits is >= 8 and <= 64) bits = parsedBits;
            if (dataOffset < 0 || channels <= 0 || bits < 16 || (audioFmt != 1 && audioFmt != 3 && audioFmt != 0xFFFE)) return null;

            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            long totalFrames = dataLen / frameBytes;
            if (totalFrames < 4096) return null;

            int wantFrames = (int)Math.Min(totalFrames, (long)(sr * 2.2));
            long startFrame = totalFrames > wantFrames * 2 ? (long)(totalFrames * 0.35) : 0;
            fs.Seek(dataOffset + startFrame * frameBytes, SeekOrigin.Begin);

            byte[] raw = new byte[wantFrames * frameBytes];
            int got = fs.Read(raw, 0, raw.Length);
            int gotFrames = got / frameBytes;
            if (gotFrames < 4096) return null;

            float[] mono = new float[gotFrames];
            long nonSilent = 0;
            long zeroLsbCount = 0;
            bool check24 = bits == 24 && (audioFmt == 1 || audioFmt == 0xFFFE);

            for (int f = 0; f < gotFrames; f++) {
                float sum = 0f;
                int baseOff = f * frameBytes;
                for (int c = 0; c < channels; c++) {
                    int off = baseOff + c * bytesPerSample;
                    float s = 0f;
                    if (bits == 16) {
                        s = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(off, 2)) / 32768f;
                    } else if (bits == 24) {
                        byte b0 = raw[off];
                        int v = b0 | (raw[off + 1] << 8) | ((sbyte)raw[off + 2] << 16);
                        if (check24 && Math.Abs(v) > 512) {
                            nonSilent++;
                            if (b0 == 0) zeroLsbCount++;
                        }
                        s = v / 8388608f;
                    } else if (bits == 32 && audioFmt == 3) {
                        s = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(off, 4));
                    } else if (bits == 32) {
                        s = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off, 4)) / 2147483648f;
                    }
                    sum += s;
                }
                mono[f] = sum / channels;
            }

            if (check24 && nonSilent >= 2048) {
                inspected24 = true;
                lsbZero = (double)zeroLsbCount / nonSilent > 0.992;
            }
            return mono;
        } catch {
            return null;
        }
    }

    static float[] DecodeWithFfmpegS32Window(
        string path,
        string ffmpegPath,
        long durationMs,
        int sr,
        CancellationToken ct,
        out int actualSr,
        out bool inspected24,
        out bool lsbZero) {
        actualSr = sr > 0 ? sr : 48000;
        inspected24 = false;
        lsbZero = false;
        try {
            ct.ThrowIfCancellationRequested();
            double seekSec = durationMs > 12000 ? (durationMs / 1000.0) * 0.35 : 0.0;
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
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(seekSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("2.0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("pcm_s32le");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("wav");
            psi.ArgumentList.Add("-");
            FFmpegInstallerService.ConfigureProcessStartInfo(psi, ffmpegPath);

            using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            using CancellationTokenRegistration ctr = ct.Register(() => {
                try {
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                } catch { }
            });

            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            using MemoryStream ms = new();
            Task copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            if (!copyTask.Wait(4500)) {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { stderrTask.Wait(300); } catch { }
                return null;
            }
            try { proc.WaitForExit(1000); } catch { }
            try { stderrTask.Wait(300); } catch { }

            byte[] raw = ms.ToArray();
            if (raw.Length < 44 || raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F') {
                return null;
            }

            int pos = 12;
            int dataStart = -1;
            while (pos + 8 <= raw.Length) {
                uint sz = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(pos + 4, 4));
                if (raw[pos] == 'f' && raw[pos + 1] == 'm' && raw[pos + 2] == 't' && raw[pos + 3] == ' ' && pos + 24 <= raw.Length) {
                    int parsedSr = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(pos + 12, 4));
                    if (parsedSr is >= 8000 and <= 384000) actualSr = parsedSr;
                } else if (raw[pos] == 'd' && raw[pos + 1] == 'a' && raw[pos + 2] == 't' && raw[pos + 3] == 'a') {
                    dataStart = pos + 8;
                    break;
                }
                if (sz > (uint)(raw.Length - pos - 8)) break;
                pos += 8 + (int)sz + ((int)sz & 1);
            }
            if (dataStart < 0) return null;

            int frames = (raw.Length - dataStart) / 8; // 2ch * 4 bytes (s32le)
            if (frames < 4096) return null;

            float[] mono = new float[frames];
            long nonSilent = 0;
            long zero16Lsb = 0;
            for (int f = 0; f < frames; f++) {
                int off = dataStart + f * 8;
                int l = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off, 4));
                int r = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(off + 4, 4));
                // In s32le, true 24-bit occupies top 24 bits (bits 8..31); fake 24-bit (16-bit padded) has bottom 16 bits == 0
                if (Math.Abs(l) > 65536) {
                    nonSilent++;
                    if ((l & 0xFFFF) == 0) zero16Lsb++;
                }
                if (Math.Abs(r) > 65536) {
                    nonSilent++;
                    if ((r & 0xFFFF) == 0) zero16Lsb++;
                }
                mono[f] = (l / 2147483648f + r / 2147483648f) * 0.5f;
            }
            if (nonSilent >= 2048) {
                inspected24 = true;
                lsbZero = (double)zero16Lsb / nonSilent > 0.992;
            }
            return mono;
        } catch {
            return null;
        }
    }

    static float[] DecodeWithMediaCodecWindow(Context ctx, LibraryTrackItem item, long durationMs, out int sampleRate, out bool inspected24, out bool lsbZero) {
        sampleRate = 48000;
        inspected24 = false;
        lsbZero = false;
        MediaExtractor extractor = null;
        MediaCodec codec = null;
        try {
            extractor = new MediaExtractor();
            if (!TrySetExtractorSource(extractor, ctx, item)) {
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
            if (format.ContainsKey(MediaFormat.KeySampleRate)) {
                sampleRate = Math.Max(8000, format.GetInteger(MediaFormat.KeySampleRate));
            }
            int channels = format.ContainsKey(MediaFormat.KeyChannelCount) ? Math.Max(1, format.GetInteger(MediaFormat.KeyChannelCount)) : 2;
            long seekUs = durationMs > 12000 ? (long)(durationMs * 350L) : 0L; // 35% into track
            if (seekUs > 0) {
                extractor.SeekTo(seekUs, MediaExtractorSeekTo.ClosestSync);
            }

            string trackMime = format.GetString(MediaFormat.KeyMime);
            codec = MediaCodec.CreateDecoderByType(trackMime);
            codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            codec.Start();

            int targetSamples = (int)Math.Min(sampleRate * 2.2, 210_000);
            List<float> mono = new(targetSamples);
            MediaCodec.BufferInfo info = new();
            bool inputDone = false;
            int pcmEncoding = (int)Android.Media.Encoding.Pcm16bit;
            int outChannels = channels;
            long nonSilent = 0;
            long zeroLsb = 0;
            int safetyIterations = 0;

            while (mono.Count < targetSamples && safetyIterations++ < 600) {
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
                    MediaFormat outFmt = codec.OutputFormat;
                    if (outFmt.ContainsKey(MediaFormat.KeySampleRate)) sampleRate = Math.Max(8000, outFmt.GetInteger(MediaFormat.KeySampleRate));
                    if (outFmt.ContainsKey(MediaFormat.KeyChannelCount)) outChannels = Math.Max(1, outFmt.GetInteger(MediaFormat.KeyChannelCount));
                    if (outFmt.ContainsKey(MediaFormat.KeyPcmEncoding)) pcmEncoding = outFmt.GetInteger(MediaFormat.KeyPcmEncoding);
                } else if (outIdx >= 0) {
                    if (info.Size > 0) {
                        Java.Nio.ByteBuffer outBuf = codec.GetOutputBuffer(outIdx);
                        outBuf.Position(info.Offset);
                        byte[] chunk = new byte[info.Size];
                        outBuf.Get(chunk);

                        if (pcmEncoding == (int)Android.Media.Encoding.PcmFloat) {
                            int frames = chunk.Length / (4 * outChannels);
                            for (int f = 0; f < frames && mono.Count < targetSamples; f++) {
                                float sum = 0f;
                                for (int c = 0; c < outChannels; c++) {
                                    sum += BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan((f * outChannels + c) * 4, 4));
                                }
                                mono.Add(sum / outChannels);
                            }
                        } else if (OperatingSystem.IsAndroidVersionAtLeast(31) && pcmEncoding == (int)Android.Media.Encoding.Pcm24bitPacked) {
                            int frames = chunk.Length / (3 * outChannels);
                            for (int f = 0; f < frames && mono.Count < targetSamples; f++) {
                                float sum = 0f;
                                for (int c = 0; c < outChannels; c++) {
                                    int off = (f * outChannels + c) * 3;
                                    byte b0 = chunk[off];
                                    int v = b0 | (chunk[off + 1] << 8) | ((sbyte)chunk[off + 2] << 16);
                                    if (Math.Abs(v) > 512) {
                                        nonSilent++;
                                        if (b0 == 0) zeroLsb++;
                                    }
                                    sum += v / 8388608f;
                                }
                                mono.Add(sum / outChannels);
                            }
                        } else {
                            int frames = chunk.Length / (2 * outChannels);
                            for (int f = 0; f < frames && mono.Count < targetSamples; f++) {
                                float sum = 0f;
                                for (int c = 0; c < outChannels; c++) {
                                    sum += BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan((f * outChannels + c) * 2, 2)) / 32768f;
                                }
                                mono.Add(sum / outChannels);
                            }
                        }
                    }
                    codec.ReleaseOutputBuffer(outIdx, false);
                    if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) break;
                }
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(31) && pcmEncoding == (int)Android.Media.Encoding.Pcm24bitPacked && nonSilent >= 2048) {
                inspected24 = true;
                lsbZero = (double)zeroLsb / nonSilent > 0.992;
            }
            return mono.Count >= 4096 ? mono.ToArray() : null;
        } catch {
            return null;
        } finally {
            try { codec?.Stop(); } catch { }
            try { codec?.Release(); } catch { }
            try { codec?.Dispose(); } catch { }
            try { extractor?.Release(); } catch { }
            try { extractor?.Dispose(); } catch { }
        }
    }

    static void ComputeDynamicsAndClipping(float[] samples, int sr, out double drDb, out double clippedPct) {
        int blockSize = Math.Max(1024, sr / 5); // 200 ms windows
        List<double> blockRms = [];
        List<double> blockPeak = [];
        int consecutiveClip = 0;
        long clippedSamples = 0;

        for (int i = 0; i < samples.Length; i++) {
            float a = MathF.Abs(samples[i]);
            if (a >= 0.9985f) {
                consecutiveClip++;
                if (consecutiveClip >= 3) {
                    clippedSamples++;
                }
            } else {
                consecutiveClip = 0;
            }
        }
        clippedPct = samples.Length > 0 ? (double)clippedSamples / samples.Length * 100.0 : 0.0;

        for (int start = 0; start + blockSize <= samples.Length; start += blockSize) {
            double sumSq = 0;
            double peak = 0;
            for (int i = 0; i < blockSize; i++) {
                double v = Math.Abs(samples[start + i]);
                sumSq += v * v;
                if (v > peak) peak = v;
            }
            double rms = Math.Sqrt(sumSq / blockSize);
            if (rms > 1e-4 && peak > 1e-3) {
                blockRms.Add(rms);
                blockPeak.Add(peak);
            }
        }

        if (blockRms.Count == 0) {
            drDb = 0;
            return;
        }

        blockRms.Sort();
        blockPeak.Sort();
        int topCount = Math.Max(1, blockRms.Count / 3);
        double topRmsSq = 0;
        for (int i = blockRms.Count - topCount; i < blockRms.Count; i++) {
            topRmsSq += blockRms[i] * blockRms[i];
        }
        double topRms = Math.Sqrt(topRmsSq / topCount);
        double refPeak = blockPeak[Math.Max(0, blockPeak.Count - 2)];
        drDb = topRms > 1e-6 && refPeak > topRms ? 20.0 * Math.Log10(refPeak / topRms) : 0;
    }

    static void AnalyzeSpectrum(float[] samples, int sr, out int cutoffHz, out bool sharpBrickwall, out double midDb, out int activeFrames) {
        cutoffHz = sr / 2;
        sharpBrickwall = false;
        midDb = -100.0;
        activeFrames = 0;
        const int fftSize = 4096;
        int half = fftSize / 2;
        if (samples.Length < fftSize) return;

        double[] powerAvg = new double[half];
        double[] hann = new double[fftSize];
        for (int n = 0; n < fftSize; n++) {
            hann[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (fftSize - 1)));
        }

        double[] re = new double[fftSize];
        double[] im = new double[fftSize];
        int hop = fftSize / 2;
        int frames = 0;

        for (int start = 0; start + fftSize <= samples.Length && frames < 48; start += hop) {
            double frameEnergy = 0;
            for (int n = 0; n < fftSize; n++) {
                double v = samples[start + n] * hann[n];
                re[n] = v;
                im[n] = 0;
                frameEnergy += v * v;
            }
            if (frameEnergy < 1e-5) continue;
            Radix2Fft(re, im);
            for (int k = 1; k < half; k++) {
                powerAvg[k] += re[k] * re[k] + im[k] * im[k];
            }
            frames++;
        }

        activeFrames = frames;
        if (frames < 3) return;
        for (int k = 1; k < half; k++) {
            powerAvg[k] /= frames;
        }

        double binHz = (double)sr / fftSize;
        double BandDb(double fLow, double fHigh) {
            int k0 = Math.Clamp((int)Math.Round(fLow / binHz), 1, half - 1);
            int k1 = Math.Clamp((int)Math.Round(fHigh / binHz), k0, half - 1);
            double sum = 0;
            for (int k = k0; k <= k1; k++) sum += powerAvg[k];
            double avg = sum / Math.Max(1, k1 - k0 + 1);
            return 10.0 * Math.Log10(Math.Max(avg, 1e-18));
        }

        midDb = BandDb(1000, 6000);
        if (midDb < -48.0) return;

        // Scan 500 Hz bands from 10 kHz up to Nyquist to detect effective upper bandwidth and sharp brickwall cliffs
        double nyquist = sr * 0.5;
        int maxBandHz = Math.Min((int)nyquist - 500, 44000);
        int detectedCutoff = (int)nyquist;
        double prevDb = BandDb(10000, 10500);
        int bestCliffHz = 0;
        double bestCliffDrop = 0;

        for (int f = 10500; f <= maxBandHz; f += 500) {
            double curDb = BandDb(f, f + 500);
            double drop = prevDb - curDb;
            // A true encoder brickwall lowpass drops sharply (> 20 dB within 1 kHz) and stays near noise floor above it
            if (drop > bestCliffDrop && f >= 14000) {
                double aboveDb = BandDb(Math.Min(f + 1000, nyquist - 500), Math.Min(f + 2500, nyquist - 100));
                if (midDb - aboveDb > 52.0 && curDb - midDb < -44.0) {
                    bestCliffDrop = drop;
                    bestCliffHz = f;
                }
            }
            prevDb = curDb;
        }

        if (bestCliffDrop >= 18.0 && bestCliffHz >= 14000) {
            detectedCutoff = bestCliffHz;
            sharpBrickwall = true;
        } else {
            // Find highest 500 Hz band with energy within -62 dB of midrange
            detectedCutoff = 10000;
            for (int f = maxBandHz; f >= 10000; f -= 500) {
                if (BandDb(f, f + 500) >= midDb - 62.0) {
                    detectedCutoff = f + 500;
                    break;
                }
            }
        }
        cutoffHz = Math.Clamp(detectedCutoff, 8000, sr / 2);
    }

    static void Radix2Fft(double[] re, double[] im) {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1) {
            double ang = -2.0 * Math.PI / len;
            double wlenRe = Math.Cos(ang);
            double wlenIm = Math.Sin(ang);
            int halfLen = len >> 1;
            for (int i = 0; i < n; i += len) {
                double wRe = 1.0, wIm = 0.0;
                for (int j = 0; j < halfLen; j++) {
                    int u = i + j;
                    int v = u + halfLen;
                    double tRe = re[v] * wRe - im[v] * wIm;
                    double tIm = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] += tRe;
                    im[u] += tIm;
                    double nextRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextRe;
                }
            }
        }
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

    static bool HasReadableSource(LibraryTrackItem item) {
        if (item == null) return false;
        if (item.ContentUri != null || IsUriString(item.FilePath)) return true;
        return CanOpenFileForRead(item.FilePath);
    }

    static string TryResolveReadablePath(Context ctx, Android.Net.Uri uri) {
        if (uri == null || ctx == null) return null;
        try {
            string resolved = AndroidFileHelper.ResolveReadablePath(ctx, uri);
            return CanOpenFileForRead(resolved) ? resolved : null;
        } catch {
            return null;
        }
    }

    static string GetTrackExtension(Context ctx, LibraryTrackItem item) {
        if (item == null) return "";
        if (!string.IsNullOrEmpty(item.FilePath) && !IsUriString(item.FilePath)) {
            string ext = IOPath.GetExtension(item.FilePath).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext)) return ext;
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        if (uri != null && ctx != null) {
            string disp = AndroidFileHelper.GetDisplayName(ctx, uri);
            if (!string.IsNullOrEmpty(disp)) {
                string ext = IOPath.GetExtension(disp).ToLowerInvariant();
                if (!string.IsNullOrEmpty(ext)) return ext;
            }
        }
        return !string.IsNullOrEmpty(item.FormatBadge)
            ? "." + item.FormatBadge.TrimStart('.').ToLowerInvariant()
            : "";
    }

    static string GetTrackDisplayLabel(Context ctx, LibraryTrackItem item) {
        if (item == null) return "";
        if (!string.IsNullOrEmpty(item.FilePath) && !IsUriString(item.FilePath)) {
            return IOPath.GetFileName(item.FilePath);
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        if (uri != null && ctx != null) {
            string disp = AndroidFileHelper.GetDisplayName(ctx, uri);
            if (!string.IsNullOrEmpty(disp)) return disp;
        }
        return !string.IsNullOrEmpty(item.Title) ? item.Title : (item.FilePath ?? "");
    }

    static System.IO.Stream OpenTrackStream(Context ctx, LibraryTrackItem item) {
        if (item == null) return null;
        if (CanOpenFileForRead(item.FilePath)) {
            try {
                return new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            } catch {
                // Fall through to ContentUri stream
            }
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        return uri != null && ctx != null ? AndroidFileHelper.OpenReadableStream(ctx, uri) : null;
    }

    static bool TrySetExtractorSource(MediaExtractor extractor, Context ctx, LibraryTrackItem item) {
        if (extractor == null || item == null) return false;
        if (CanOpenFileForRead(item.FilePath)) {
            extractor.SetDataSource(item.FilePath);
            return true;
        }
        Android.Net.Uri uri = item.ContentUri ?? TryParseUri(item.FilePath);
        if (uri != null && ctx != null) {
            extractor.SetDataSource(ctx, uri, null);
            return true;
        }
        return false;
    }

    static bool ReadExactBytes(System.IO.Stream stream, Span<byte> dest) {
        int total = 0;
        while (total < dest.Length) {
            int n = stream.Read(dest[total..]);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }

    static bool SkipBytes(System.IO.Stream stream, long count) {
        if (count <= 0) return true;
        byte[] scratch = new byte[4096];
        long remaining = count;
        while (remaining > 0) {
            int want = (int)Math.Min(scratch.Length, remaining);
            int got = stream.Read(scratch, 0, want);
            if (got <= 0) return false;
            remaining -= got;
        }
        return true;
    }
}
