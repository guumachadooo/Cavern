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
