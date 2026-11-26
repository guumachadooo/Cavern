using System.Runtime.CompilerServices;
using System.Text;

using Android.Content;

using Cavern.Spatial;

using Cavernize.Logic.Spatial;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Slot 7B disk-backed LRU cache for pre-separated <see cref="NeuralSeparatedTrack"/> bundles stored under
/// <c>CacheDir/neural_tracks/*.ntc</c>. Delegates serialization to <see cref="NeuralTrackCacheCodec"/> (<c>CNTC v3</c>
/// lossless Mid/Side + LPC-delta + ZLib compression with 100% <c>v1/v2</c> read compatibility), supports O(1)
/// header peeking for the In-App Cache Folder Browser, atomic temp-file writes, and configurable LRU budgets.
/// </summary>
public sealed class NeuralTrackCacheService : INeuralTrackCache {
    const string FileExtension = ".ntc";

    readonly string cacheDir;
    readonly object cacheLock = new();
    readonly ConditionalWeakTable<NeuralSeparatedTrack, SpatialChoreoTrack> attachedChoreoTracks = new();
    long maxBytes;
    volatile bool isEnabled = true;
    volatile int compressionModeInt = (int)NeuralTrackCacheCompressionMode.Compact16Bit;

    public NeuralTrackCacheService(Context context, long maxBytes = 512L * 1024L * 1024L) {
        ArgumentNullException.ThrowIfNull(context);
        cacheDir = IOPath.Combine(context.CacheDir.AbsolutePath, "neural_tracks");
        Directory.CreateDirectory(cacheDir);
        this.maxBytes = ClampBudget(maxBytes);
    }

    public static string GetCacheDir(Context context) {
        string dir = IOPath.Combine(context.CacheDir.AbsolutePath, "neural_tracks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Absolute directory path on device where cached <c>*.ntc</c> bundles live.</summary>
    public string CacheDirectoryPath => cacheDir;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => isEnabled;
        set => isEnabled = value;
    }

    /// <inheritdoc/>
    public NeuralTrackCacheCompressionMode CompressionMode {
        get => (NeuralTrackCacheCompressionMode)Math.Clamp(compressionModeInt, 0, 2);
        set => compressionModeInt = Math.Clamp((int)value, 0, 2);
    }

    /// <inheritdoc/>
    public NeuralTrackCacheStats GetStats() {
        lock (cacheLock) {
            return ComputeStatsLocked();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<NeuralTrackCacheEntryInfo> ListEntries() {
        lock (cacheLock) {
            List<NeuralTrackCacheEntryInfo> list = [];
            try {
                if (!Directory.Exists(cacheDir)) {
                    return list;
                }
                foreach (string file in Directory.GetFiles(cacheDir, "*" + FileExtension)) {
                    NeuralTrackCacheEntryInfo info = NeuralTrackCacheCodec.PeekHeader(file);
                    if (info != null) {
                        list.Add(info);
                    }
                }
                list.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
            } catch (Exception ex) {
                AppLogger.Warn($"Slot 7B neural track cache list warning: {ex.Message}");
            }
            return list;
        }
    }

    /// <inheritdoc/>
    public bool DeleteEntry(string cacheKey) {
        if (string.IsNullOrWhiteSpace(cacheKey)) {
            return false;
        }
        string path = GetEntryPath(cacheKey);
        lock (cacheLock) {
            try {
                if (IOFile.Exists(path)) {
                    IOFile.Delete(path);
                    return true;
                }
            } catch (Exception ex) {
                AppLogger.Warn($"Slot 7B neural track cache delete failed ({cacheKey}): {ex.Message}");
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public bool TryLoad(string cacheKey, out NeuralSeparatedTrack track, CancellationToken token = default) {
        return TryLoad(cacheKey, out track, out _, token);
    }

    /// <summary>
    /// Attempts to load a cached <see cref="NeuralSeparatedTrack"/> (CNTC v1, v2, or v3) along with its optional
    /// persisted BP3 <see cref="SpatialChoreoTrack"/> (<c>null</c> for legacy v1 cache files).
    /// </summary>
    public bool TryLoad(
        string cacheKey,
        out NeuralSeparatedTrack track,
        out SpatialChoreoTrack choreoTrack,
        CancellationToken token = default) {
        track = null;
        choreoTrack = null;
        if (string.IsNullOrWhiteSpace(cacheKey)) {
            return false;
        }
        string path = GetEntryPath(cacheKey);
        lock (cacheLock) {
            if (!IOFile.Exists(path)) {
                return false;
            }
            try {
                token.ThrowIfCancellationRequested();
                track = NeuralTrackCacheCodec.ReadTrackFile(path, out choreoTrack, token);
                if (track == null) {
                    TryDeleteQuietly(path);
                    return false;
                }
                if (choreoTrack != null) {
                    attachedChoreoTracks.AddOrUpdate(track, choreoTrack);
                }
                // Touch last-write time for LRU recency tracking
                try { IOFile.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
                return true;
            } catch (System.OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                AppLogger.Warn($"Slot 7B neural track cache entry corrupted ({IOPath.GetFileName(path)}): {ex.Message}");
                TryDeleteQuietly(path);
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public bool TryLoadBestForSourceTrack(
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        NeuralPipelineGoal goal,
        out NeuralSeparatedTrack track,
        CancellationToken token = default) {
        track = null;
        if (!isEnabled) {
            return false;
        }
        IReadOnlyList<NeuralTrackCacheEntryInfo> entries = ListEntries();
        if (entries == null || entries.Count == 0) {
            return false;
        }

        NeuralTrackCacheEntryInfo best = null;
        int bestScore = 0;
        for (int i = 0; i < entries.Count; i++) {
            token.ThrowIfCancellationRequested();
            NeuralTrackCacheEntryInfo entry = entries[i];
            int score = ScoreCacheEntryMatch(entry, sourceTrackKey, displayTitle, artistName, goal);
            if (score > bestScore ||
                (score > 0 && score == bestScore && best != null && entry.LastModifiedUtc > best.LastModifiedUtc)) {
                bestScore = score;
                best = entry;
            }
        }
        if (best == null || bestScore <= 0) {
            return false;
        }
        return TryLoad(best.CacheKey, out track, token);
    }

    /// <summary>
    /// Returns all cached entries matching the given song across variants (e.g. Dolby Atmos &lt;-&gt; FLAC),
    /// sorted by descending match quality and recency.
    /// </summary>
    public IReadOnlyList<NeuralTrackCacheEntryInfo> FindEntriesForTrack(
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        NeuralPipelineGoal goal = NeuralPipelineGoal.StemSeparation) {
        IReadOnlyList<NeuralTrackCacheEntryInfo> entries = ListEntries();
        if (entries == null || entries.Count == 0) {
            return Array.Empty<NeuralTrackCacheEntryInfo>();
        }
        List<(NeuralTrackCacheEntryInfo Entry, int Score)> scored = [];
        for (int i = 0; i < entries.Count; i++) {
            int s = ScoreCacheEntryMatch(entries[i], sourceTrackKey, displayTitle, artistName, goal);
            if (s > 0) {
                scored.Add((entries[i], s));
            }
        }
        scored.Sort((a, b) => {
            int cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : b.Entry.LastModifiedUtc.CompareTo(a.Entry.LastModifiedUtc);
        });
        List<NeuralTrackCacheEntryInfo> result = new(scored.Count);
        for (int i = 0; i < scored.Count; i++) {
            result.Add(scored[i].Entry);
        }
        return result;
    }

    /// <summary>
    /// Deletes all cached stem bundles matching the specified song (across both Atmos and FLAC variants) and returns the count removed.
    /// </summary>
    public int DeleteEntriesForTrack(string sourceTrackKey, string displayTitle, string artistName) {
        IReadOnlyList<NeuralTrackCacheEntryInfo> all = ListEntries();
        if (all == null || all.Count == 0) {
            return 0;
        }
        HashSet<string> keysToDelete = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < all.Count; i++) {
            NeuralTrackCacheEntryInfo e = all[i];
            int scoreStem = ScoreCacheEntryMatch(e, sourceTrackKey, displayTitle, artistName, NeuralPipelineGoal.StemSeparation);
            int scoreKara = ScoreCacheEntryMatch(e, sourceTrackKey, displayTitle, artistName, NeuralPipelineGoal.SpatialKaraoke);
            if (scoreStem > 0 || scoreKara > 0) {
                keysToDelete.Add(e.CacheKey);
            }
        }
        int removed = 0;
        foreach (string key in keysToDelete) {
            if (DeleteEntry(key)) {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Searches all cached variants of the same song for an already-separated vocal stem (e.g., from a 4/6-stem
    /// Play separation or prior vocal split) so Karaoke / Lead+Backing extraction can skip Stage 1 completely.
    /// </summary>
    public bool TryFindBestReusableVocalTrack(
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        int expectedSampleRate,
        out NeuralSeparatedTrack reusableTrack,
        out NeuralTrackCacheEntryInfo entryInfo,
        CancellationToken token = default) {
        reusableTrack = null;
        entryInfo = null;
        if (!isEnabled) {
            return false;
        }
        IReadOnlyList<NeuralTrackCacheEntryInfo> candidates = FindEntriesForTrack(
            sourceTrackKey,
            displayTitle,
            artistName,
            NeuralPipelineGoal.StemSeparation);
        if (candidates == null || candidates.Count == 0) {
            return false;
        }
        for (int i = 0; i < candidates.Count; i++) {
            token.ThrowIfCancellationRequested();
            NeuralTrackCacheEntryInfo info = candidates[i];
            if (info == null || info.StemCount < 2) {
                continue;
            }
            if (expectedSampleRate >= 8000 && info.SampleRate >= 8000 && info.SampleRate != expectedSampleRate) {
                continue;
            }
            if (TryLoad(info.CacheKey, out NeuralSeparatedTrack loaded, token) &&
                loaded != null &&
                (loaded.FindStem("vocals") != null || loaded.FindStem("lead_vocal") != null)) {
                reusableTrack = loaded;
                entryInfo = info;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Losslessly compacts legacy or sub-optimally packed <c>*.ntc</c> entries in-place using CNTC v3 mono-ref
    /// deduplication and optimal ZLib entropy coding without removing any cached variants.
    /// </summary>
    public (int CompactedCount, long ReclaimedBytes) OptimizeCacheStorage(CancellationToken token = default) {
        NeuralTrackCacheCompressionMode targetMode = CompressionMode == NeuralTrackCacheCompressionMode.RawFloat32
            ? NeuralTrackCacheCompressionMode.Compact16Bit
            : CompressionMode;
        int compacted = 0;
        long reclaimed = 0L;
        lock (cacheLock) {
            if (!Directory.Exists(cacheDir)) {
                return (0, 0L);
            }
            foreach (string file in Directory.GetFiles(cacheDir, "*" + FileExtension)) {
                token.ThrowIfCancellationRequested();
                try {
                    NeuralTrackCacheEntryInfo info = NeuralTrackCacheCodec.PeekHeader(file);
                    if (info == null) {
                        continue;
                    }
                    // Preserve Studio24Bit precision when entry was explicitly saved in Studio24Bit
                    NeuralTrackCacheCompressionMode entryTarget = info.CompressionMode == NeuralTrackCacheCompressionMode.Studio24Bit
                        ? NeuralTrackCacheCompressionMode.Studio24Bit
                        : targetMode;
                    long saved = NeuralTrackCacheCodec.CompactFileIfBeneficial(file, entryTarget, token);
                    if (saved > 0L) {
                        compacted++;
                        reclaimed += saved;
                    }
                } catch (System.OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    AppLogger.Warn($"Slot 7B neural cache compact warning ({IOPath.GetFileName(file)}): {ex.Message}");
                }
            }
        }
        return (compacted, reclaimed);
    }

    /// <summary>
    /// Pure deterministic matcher enforcing unified cross-variant (Dolby Atmos &lt;-&gt; FLAC) cache-first stem reuse
    /// while strictly preventing cross-song pollution (returns 0 on mismatch or insufficient stem count).
    /// </summary>
    public static int ScoreCacheEntryMatch(
        NeuralTrackCacheEntryInfo entry,
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        NeuralPipelineGoal goal) {
        if (entry == null || entry.StemCount <= 0) {
            return 0;
        }
        if (goal == NeuralPipelineGoal.LosslessResidualRestore) {
            // Restoration caches are stereo mixes rather than multi-stem separation bundles
            if (string.IsNullOrEmpty(entry.PlanSignature) ||
                !entry.PlanSignature.Contains("LosslessResidualRestore", StringComparison.OrdinalIgnoreCase)) {
                return 0;
            }
        } else if (entry.StemCount < 2) {
            return 0;
        }

        string normQueryPath = NormalizeTrackPath(sourceTrackKey);
        string normEntryPath = NormalizeTrackPath(entry.SourceTrackKey);
        string normQueryFile = ExtractNormalizedFileStem(sourceTrackKey);
        string normEntryFile = ExtractNormalizedFileStem(entry.SourceTrackKey);
        string canonicalQueryFile = ExtractCanonicalSongStem(sourceTrackKey);
        string canonicalEntryFile = ExtractCanonicalSongStem(entry.SourceTrackKey);
        string canonicalQueryTitle = ExtractCanonicalSongStem(displayTitle);
        string canonicalEntryTitle = ExtractCanonicalSongStem(entry.DisplayTitle);
        string normQueryArtist = NormalizeMetaToken(artistName);
        string normEntryArtist = NormalizeMetaToken(entry.ArtistName);

        // Reject cross-song collision if both entries declare distinct non-empty artists
        bool artistConflict = !string.IsNullOrEmpty(normQueryArtist) &&
                              !string.IsNullOrEmpty(normEntryArtist) &&
                              !string.Equals(normQueryArtist, normEntryArtist, StringComparison.Ordinal) &&
                              !normQueryArtist.Contains(normEntryArtist, StringComparison.Ordinal) &&
                              !normEntryArtist.Contains(normQueryArtist, StringComparison.Ordinal);

        int identityScore = 0;
        if (!string.IsNullOrEmpty(normQueryPath) &&
            !string.IsNullOrEmpty(normEntryPath) &&
            string.Equals(normQueryPath, normEntryPath, StringComparison.OrdinalIgnoreCase)) {
            identityScore = 1000;
        } else if (!artistConflict &&
                   !string.IsNullOrEmpty(normQueryFile) &&
                   !string.IsNullOrEmpty(normEntryFile) &&
                   string.Equals(normQueryFile, normEntryFile, StringComparison.Ordinal)) {
            identityScore = 750;
        } else if (!artistConflict &&
                   !string.IsNullOrEmpty(canonicalQueryFile) &&
                   !string.IsNullOrEmpty(canonicalEntryFile) &&
                   string.Equals(canonicalQueryFile, canonicalEntryFile, StringComparison.Ordinal)) {
            // Cross-variant filename match: e.g. "01. Lavender Haze.m4a.mp4" <-> "01 - Lavender Haze.flac"
            identityScore = 650;
        } else if (!artistConflict &&
                   !string.IsNullOrEmpty(canonicalQueryTitle) &&
                   !string.IsNullOrEmpty(canonicalEntryTitle) &&
                   string.Equals(canonicalQueryTitle, canonicalEntryTitle, StringComparison.Ordinal)) {
            bool bothArtistsMatch = !string.IsNullOrEmpty(normQueryArtist) && !string.IsNullOrEmpty(normEntryArtist);
            bool canonicalFileCorroborates = (!string.IsNullOrEmpty(canonicalQueryFile) &&
                                              string.Equals(canonicalQueryFile, canonicalEntryTitle, StringComparison.Ordinal)) ||
                                             (!string.IsNullOrEmpty(canonicalEntryFile) &&
                                              string.Equals(canonicalEntryFile, canonicalQueryTitle, StringComparison.Ordinal));
            if (bothArtistsMatch || canonicalFileCorroborates || canonicalQueryTitle.Length >= 4) {
                identityScore = bothArtistsMatch ? 600 : 520;
            } else {
                return 0;
            }
        } else if (!artistConflict &&
                   !string.IsNullOrEmpty(canonicalQueryTitle) &&
                   !string.IsNullOrEmpty(canonicalEntryFile) &&
                   string.Equals(canonicalQueryTitle, canonicalEntryFile, StringComparison.Ordinal)) {
            identityScore = 540;
        } else if (!artistConflict &&
                   !string.IsNullOrEmpty(canonicalQueryFile) &&
                   !string.IsNullOrEmpty(canonicalEntryTitle) &&
                   string.Equals(canonicalQueryFile, canonicalEntryTitle, StringComparison.Ordinal)) {
            identityScore = 540;
        } else {
            return 0;
        }

        int goalBonus = Math.Min(entry.StemCount, 8) * 35;
        if (goal == NeuralPipelineGoal.SpatialKaraoke) {
            bool karaokeTagged = !string.IsNullOrEmpty(entry.PlanSignature) &&
                (entry.PlanSignature.Contains("SpatialKaraoke", StringComparison.OrdinalIgnoreCase) ||
                 entry.PlanSignature.Contains("karaoke", StringComparison.OrdinalIgnoreCase) ||
                 entry.PlanSignature.Contains("pbv=", StringComparison.OrdinalIgnoreCase));
            if (karaokeTagged) {
                goalBonus += 120;
            }
        } else if (goal == NeuralPipelineGoal.StemSeparation && entry.StemCount >= 4) {
            goalBonus += 90;
        }

        return identityScore + goalBonus;
    }

    static readonly string[] KnownMediaExtensions = [
        ".mp4", ".m4a", ".m4v", ".flac", ".wav", ".mkv", ".mka", ".eac3", ".ec3",
        ".ac3", ".thd", ".mlp", ".mp3", ".aac", ".ogg", ".opus", ".alac", ".aiff",
        ".aif", ".atmos", ".webm", ".weba", ".wv", ".ape", ".tta"
    ];

    static readonly string[] FormatEditionTags = [
        "dolby atmos", "atmos", "spatial audio", "spatial", "flac", "lossless",
        "hi res", "hires", "24bit", "24 bit", "16bit", "16 bit", "master", "mqa",
        "eac3", "truehd", "alac", "explicit"
    ];

    /// <summary>
    /// Extracts a canonical cross-variant song stem so Dolby Atmos and FLAC files of the same song
    /// (e.g. <c>"02. Maroon.m4a (1).mp4"</c> and <c>"02 - Maroon.flac"</c>) normalize identically.
    /// </summary>
    public static string ExtractCanonicalSongStem(string pathOrTitle) {
        if (string.IsNullOrWhiteSpace(pathOrTitle)) {
            return string.Empty;
        }
        string cleaned = pathOrTitle.Trim().Replace('\\', '/');
        int slash = cleaned.LastIndexOf('/');
        if (slash >= 0 && slash < cleaned.Length - 1) {
            cleaned = cleaned[(slash + 1)..];
        }

        // Iteratively strip trailing media extensions and browser duplicate counters like " (1)" / " (2)"
        bool changed = true;
        int guard = 0;
        while (changed && guard++ < 6) {
            changed = false;
            cleaned = cleaned.Trim();
            for (int i = 0; i < KnownMediaExtensions.Length; i++) {
                if (cleaned.EndsWith(KnownMediaExtensions[i], StringComparison.OrdinalIgnoreCase) &&
                    cleaned.Length > KnownMediaExtensions[i].Length) {
                    cleaned = cleaned[..^KnownMediaExtensions[i].Length].Trim();
                    changed = true;
                    break;
                }
            }
            if (cleaned.EndsWith(')')) {
                int open = cleaned.LastIndexOf('(');
                if (open > 0) {
                    string inside = cleaned.Substring(open + 1, cleaned.Length - open - 2).Trim();
                    bool allDigits = inside.Length > 0 && inside.Length <= 2 && inside.All(char.IsDigit);
                    bool isFormatTag = IsFormatEditionTag(inside);
                    if (allDigits || isFormatTag) {
                        cleaned = cleaned[..open].Trim();
                        changed = true;
                    }
                }
            } else if (cleaned.EndsWith(']')) {
                int open = cleaned.LastIndexOf('[');
                if (open > 0) {
                    string inside = cleaned.Substring(open + 1, cleaned.Length - open - 2).Trim();
                    if (IsFormatEditionTag(inside)) {
                        cleaned = cleaned[..open].Trim();
                        changed = true;
                    }
                }
            }
        }

        // Strip leading track numbers like "01. ", "01 - ", "1-02 ", "09_"
        cleaned = StripLeadingTrackNumber(cleaned);
        return NormalizeMetaToken(cleaned);
    }

    static bool IsFormatEditionTag(string token) {
        if (string.IsNullOrWhiteSpace(token)) {
            return false;
        }
        string norm = NormalizeMetaToken(token);
        for (int i = 0; i < FormatEditionTags.Length; i++) {
            if (string.Equals(norm, FormatEditionTags[i], StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }

    static string StripLeadingTrackNumber(string s) {
        if (string.IsNullOrEmpty(s)) {
            return string.Empty;
        }
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int startDigits = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == startDigits || i - startDigits > 3) {
            return s;
        }
        // Optional disc-track separator like "1-02"
        if (i < s.Length && s[i] == '-') {
            int j = i + 1;
            int d2 = j;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (j > d2 && j - d2 <= 3) {
                i = j;
            }
        }
        int sepStart = i;
        while (i < s.Length && (s[i] == '.' || s[i] == '-' || s[i] == '_' || s[i] == ')' || char.IsWhiteSpace(s[i]))) {
            i++;
        }
        // Only strip if there was an explicit separator/whitespace and meaningful title remains
        if (i > sepStart && i < s.Length) {
            return s[i..].TrimStart();
        }
        return s;
    }

    static string NormalizeTrackPath(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return string.Empty;
        }
        return path.Trim().Replace('\\', '/');
    }

    static string ExtractNormalizedFileStem(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return string.Empty;
        }
        try {
            string cleaned = path.Trim().Replace('\\', '/');
            int slash = cleaned.LastIndexOf('/');
            string name = slash >= 0 ? cleaned[(slash + 1)..] : cleaned;
            string stem = IOPath.GetFileNameWithoutExtension(name);
            return NormalizeMetaToken(stem);
        } catch {
            return string.Empty;
        }
    }

    static string NormalizeMetaToken(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }
        string formD = value.Trim().Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(formD.Length);
        bool prevSpace = true;
        for (int i = 0; i < formD.Length; i++) {
            char c = formD[i];
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) {
                continue;
            }
            if (char.IsLetterOrDigit(c)) {
                sb.Append(char.ToLowerInvariant(c));
                prevSpace = false;
            } else if (!prevSpace) {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <inheritdoc/>
    public void Save(string cacheKey, NeuralSeparatedTrack track, CancellationToken token = default) {
        SpatialChoreoTrack attached = null;
        if (track != null) {
            TryGetAttachedChoreoTrack(track, out attached);
        }
        Save(cacheKey, track, attached, token);
    }

    /// <summary>
    /// Atomically writes <paramref name="track"/> and optional <paramref name="choreoTrack"/> in <c>CNTC v3</c>
    /// compressed lossless format and enforces the configured LRU disk budget.
    /// </summary>
    public void Save(
        string cacheKey,
        NeuralSeparatedTrack track,
        SpatialChoreoTrack choreoTrack,
        CancellationToken token = default) {
        if (!isEnabled || string.IsNullOrWhiteSpace(cacheKey) || track == null || track.Stems == null || track.Stems.Count == 0) {
            return;
        }
        token.ThrowIfCancellationRequested();
        string destPath = GetEntryPath(cacheKey);
        string tmpPath = destPath + $".{Guid.NewGuid():N}.tmp";

        lock (cacheLock) {
            try {
                if (choreoTrack != null) {
                    attachedChoreoTracks.AddOrUpdate(track, choreoTrack);
                }
                Directory.CreateDirectory(cacheDir);
                NeuralTrackCacheCodec.WriteTrackFile(tmpPath, track, choreoTrack, CompressionMode, token);
                token.ThrowIfCancellationRequested();
                if (IOFile.Exists(destPath)) {
                    IOFile.Delete(destPath);
                }
                IOFile.Move(tmpPath, destPath);
                EvictToLimitLocked(maxBytes);
            } catch (System.OperationCanceledException) {
                TryDeleteQuietly(tmpPath);
                throw;
            } catch (Exception ex) {
                TryDeleteQuietly(tmpPath);
                AppLogger.Warn($"Slot 7B neural track cache write failed: {ex.Message}");
            }
        }
    }

    /// <summary>Associates a <see cref="SpatialChoreoTrack"/> with <paramref name="track"/> in memory and for subsequent saves.</summary>
    public void AttachChoreoTrack(NeuralSeparatedTrack track, SpatialChoreoTrack choreoTrack) {
        if (track == null) {
            return;
        }
        lock (cacheLock) {
            if (choreoTrack == null) {
                attachedChoreoTracks.Remove(track);
            } else {
                attachedChoreoTracks.AddOrUpdate(track, choreoTrack);
            }
        }
    }

    /// <summary>Retrieves a cached or loaded <see cref="SpatialChoreoTrack"/> associated with <paramref name="track"/>.</summary>
    public bool TryGetAttachedChoreoTrack(NeuralSeparatedTrack track, out SpatialChoreoTrack choreoTrack) {
        choreoTrack = null;
        if (track == null) {
            return false;
        }
        lock (cacheLock) {
            return attachedChoreoTracks.TryGetValue(track, out choreoTrack) && choreoTrack != null;
        }
    }

    /// <inheritdoc/>
    public void EvictToLimit(long maxBytes) {
        lock (cacheLock) {
            this.maxBytes = ClampBudget(maxBytes);
            EvictToLimitLocked(this.maxBytes);
        }
    }

    /// <inheritdoc/>
    public void Clear() {
        lock (cacheLock) {
            try {
                if (!Directory.Exists(cacheDir)) {
                    return;
                }
                foreach (string file in Directory.GetFiles(cacheDir)) {
                    TryDeleteQuietly(file);
                }
            } catch (Exception ex) {
                AppLogger.Warn($"Slot 7B neural track cache clear failed: {ex.Message}");
            }
        }
    }

    NeuralTrackCacheStats ComputeStatsLocked() {
        try {
            if (!Directory.Exists(cacheDir)) {
                return new NeuralTrackCacheStats {
                    EntryCount = 0,
                    TotalBytes = 0L,
                    MaxBytes = maxBytes,
                    TotalUncompressedBytes = 0L
                };
            }
            int count = 0;
            long total = 0L;
            long totalUncompressed = 0L;
            foreach (string file in Directory.GetFiles(cacheDir, "*" + FileExtension)) {
                try {
                    long len = new FileInfo(file).Length;
                    if (len > 0) {
                        count++;
                        total += len;
                        NeuralTrackCacheEntryInfo info = NeuralTrackCacheCodec.PeekHeader(file);
                        totalUncompressed += info != null && info.UncompressedBytes > 0 ? info.UncompressedBytes : len;
                    }
                } catch { }
            }
            return new NeuralTrackCacheStats {
                EntryCount = count,
                TotalBytes = total,
                MaxBytes = maxBytes,
                TotalUncompressedBytes = totalUncompressed
            };
        } catch {
            return new NeuralTrackCacheStats {
                EntryCount = 0,
                TotalBytes = 0L,
                MaxBytes = maxBytes,
                TotalUncompressedBytes = 0L
            };
        }
    }

    void EvictToLimitLocked(long limitBytes) {
        try {
            if (!Directory.Exists(cacheDir)) {
                return;
            }
            // Sweep any abandoned temp files first
            foreach (string tmp in Directory.GetFiles(cacheDir, "*.tmp")) {
                TryDeleteQuietly(tmp);
            }

            List<FileInfo> entries = [];
            long totalBytes = 0L;
            foreach (string file in Directory.GetFiles(cacheDir, "*" + FileExtension)) {
                try {
                    FileInfo fi = new(file);
                    entries.Add(fi);
                    totalBytes += fi.Length;
                } catch { }
            }
            if (totalBytes <= limitBytes) {
                return;
            }

            // Oldest LastWriteTimeUtc first (LRU)
            entries.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < entries.Count && totalBytes > limitBytes; i++) {
                long len = entries[i].Length;
                TryDeleteQuietly(entries[i].FullName);
                totalBytes -= len;
            }
        } catch (Exception ex) {
            AppLogger.Warn($"Slot 7B neural track cache LRU eviction warning: {ex.Message}");
        }
    }

    string GetEntryPath(string cacheKey) {
        StringBuilder sb = new(cacheKey.Length);
        for (int i = 0; i < cacheKey.Length; i++) {
            char c = cacheKey[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-') {
                sb.Append(c);
            } else {
                sb.Append('_');
            }
        }
        string safe = sb.Length > 0 ? sb.ToString() : "track";
        return IOPath.Combine(cacheDir, safe + FileExtension);
    }

    static long ClampBudget(long bytes) =>
        Math.Clamp(bytes, 128L * 1024L * 1024L, 4096L * 1024L * 1024L);

    static void TryDeleteQuietly(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && IOFile.Exists(path)) {
                IOFile.Delete(path);
            }
        } catch { }
    }
}
