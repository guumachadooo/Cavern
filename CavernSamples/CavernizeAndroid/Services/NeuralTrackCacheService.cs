using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

using Android.Content;

using Cavern.Neural;
using Cavern.Spatial;

using Cavernize.Logic.Spatial;

using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Slot 7B disk-backed LRU cache for pre-separated <see cref="NeuralSeparatedTrack"/> bundles stored under
/// <c>CacheDir/neural_tracks/*.ntc</c>. Uses atomic temp-file writes, content-addressed cache keys,
/// cancellation checks, and configurable LRU byte limits shared across Slots 2B, 2D, and 8.
/// </summary>
public sealed class NeuralTrackCacheService : INeuralTrackCache {
    const uint FileMagic = 0x43544E43u; // "CNTC" (Cavern Neural Track Cache)
    const ushort FormatVersion = 2;
    const ushort MinSupportedVersion = 1;
    const uint FlagDerivedFromLosslessTwin = 1u << 0;
    const uint FlagHasLeadRef = 1u << 1;
    const uint FlagHasBackingRef = 1u << 2;
    const uint FlagHasDereverb = 1u << 3;
    const uint FlagHasChoreoTrack = 1u << 4;
    const string FileExtension = ".ntc";

    readonly string cacheDir;
    readonly object cacheLock = new();
    readonly ConditionalWeakTable<NeuralSeparatedTrack, SpatialChoreoTrack> attachedChoreoTracks = new();
    long maxBytes;

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

    /// <inheritdoc/>
    public NeuralTrackCacheStats GetStats() {
        lock (cacheLock) {
            return ComputeStatsLocked();
        }
    }

    /// <inheritdoc/>
    public bool TryLoad(string cacheKey, out NeuralSeparatedTrack track, CancellationToken token = default) {
        return TryLoad(cacheKey, out track, out _, token);
    }

    /// <summary>
    /// Attempts to load a cached <see cref="NeuralSeparatedTrack"/> (CNTC v1 or v2) along with its optional
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
                track = ReadTrackFile(path, out choreoTrack, token);
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
    /// Atomically writes <paramref name="track"/> and optional <paramref name="choreoTrack"/> in CNTC v2 format
    /// and enforces the configured LRU disk budget.
    /// </summary>
    public void Save(
        string cacheKey,
        NeuralSeparatedTrack track,
        SpatialChoreoTrack choreoTrack,
        CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(cacheKey) || track == null || track.Stems == null || track.Stems.Count == 0) {
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
                WriteTrackFile(tmpPath, track, choreoTrack, token);
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
                return new NeuralTrackCacheStats { EntryCount = 0, TotalBytes = 0L, MaxBytes = maxBytes };
            }
            int count = 0;
            long total = 0L;
            foreach (string file in Directory.GetFiles(cacheDir, "*" + FileExtension)) {
                try {
                    long len = new FileInfo(file).Length;
                    if (len > 0) {
                        count++;
                        total += len;
                    }
                } catch { }
            }
            return new NeuralTrackCacheStats {
                EntryCount = count,
                TotalBytes = total,
                MaxBytes = maxBytes
            };
        } catch {
            return new NeuralTrackCacheStats { EntryCount = 0, TotalBytes = 0L, MaxBytes = maxBytes };
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

    static void WriteTrackFile(string path, NeuralSeparatedTrack track, SpatialChoreoTrack choreoTrack, CancellationToken token) {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: false);

        bool hasChoreo = choreoTrack != null && choreoTrack.StemCount > 0 && choreoTrack.FrameCount > 0;
        uint flags = 0u;
        if (track.DerivedFromLosslessTwin) flags |= FlagDerivedFromLosslessTwin;
        if (track.MonoLeadVocalRef != null && track.MonoLeadVocalRef.Length > 0) flags |= FlagHasLeadRef;
        if (track.MonoBackingVocalRef != null && track.MonoBackingVocalRef.Length > 0) flags |= FlagHasBackingRef;
        if (track.DereverbAnalysis?.DryVocal != null && track.DereverbAnalysis?.WetReverb != null) flags |= FlagHasDereverb;
        if (hasChoreo) flags |= FlagHasChoreoTrack;

        bw.Write(FileMagic);
        bw.Write(FormatVersion);
        bw.Write((ushort)0); // reserved
        bw.Write(track.SampleRate);
        bw.Write(flags);
        WriteUtf8String(bw, track.SourceTrackKey);
        WriteUtf8String(bw, track.PlanSignature);

        int stemCount = track.Stems.Count;
        bw.Write(stemCount);
        for (int i = 0; i < stemCount; i++) {
            token.ThrowIfCancellationRequested();
            StereoStemBuffer stem = track.Stems[i];
            WriteUtf8String(bw, stem.Name);
            WriteFloatArray(bw, stem.Left, token);
            WriteFloatArray(bw, stem.Right, token);
        }

        if ((flags & FlagHasLeadRef) != 0) {
            WriteFloatArray(bw, track.MonoLeadVocalRef, token);
        }
        if ((flags & FlagHasBackingRef) != 0) {
            WriteFloatArray(bw, track.MonoBackingVocalRef, token);
        }
        if ((flags & FlagHasDereverb) != 0) {
            bw.Write(track.DereverbAnalysis.DirectToReverberantRatioDb);
            WriteFloatArray(bw, track.DereverbAnalysis.DryVocal.Left, token);
            WriteFloatArray(bw, track.DereverbAnalysis.DryVocal.Right, token);
            WriteFloatArray(bw, track.DereverbAnalysis.WetReverb.Left, token);
            WriteFloatArray(bw, track.DereverbAnalysis.WetReverb.Right, token);
        }
        if (hasChoreo) {
            WriteChoreoTrack(bw, choreoTrack, token);
        }

        bw.Flush();
        fs.Flush(true);
    }

    static NeuralSeparatedTrack ReadTrackFile(string path, out SpatialChoreoTrack choreoTrack, CancellationToken token) {
        choreoTrack = null;
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using BinaryReader br = new(fs, Encoding.UTF8, leaveOpen: false);

        if (fs.Length < 20) {
            return null;
        }
        uint magic = br.ReadUInt32();
        ushort ver = br.ReadUInt16();
        br.ReadUInt16(); // reserved
        if (magic != FileMagic || ver < MinSupportedVersion || ver > FormatVersion) {
            return null;
        }

        int sampleRate = br.ReadInt32();
        uint flags = br.ReadUInt32();
        if (sampleRate < 8000 || sampleRate > 384000) {
            return null;
        }

        string sourceTrackKey = ReadUtf8String(br);
        string planSignature = ReadUtf8String(br);
        int stemCount = br.ReadInt32();
        if (stemCount <= 0 || stemCount > 32) {
            return null;
        }

        StereoStemBuffer[] stems = new StereoStemBuffer[stemCount];
        for (int i = 0; i < stemCount; i++) {
            token.ThrowIfCancellationRequested();
            string name = ReadUtf8String(br);
            float[] left = ReadFloatArray(br, token);
            float[] right = ReadFloatArray(br, token);
            if (left == null || right == null || left.Length != right.Length) {
                return null;
            }
            stems[i] = new StereoStemBuffer(name, left, right);
        }

        float[] leadRef = (flags & FlagHasLeadRef) != 0 ? ReadFloatArray(br, token) : null;
        float[] backingRef = (flags & FlagHasBackingRef) != 0 ? ReadFloatArray(br, token) : null;

        DereverbDecompositionResult dereverb = null;
        if ((flags & FlagHasDereverb) != 0) {
            float drrDb = br.ReadSingle();
            float[] dryL = ReadFloatArray(br, token);
            float[] dryR = ReadFloatArray(br, token);
            float[] wetL = ReadFloatArray(br, token);
            float[] wetR = ReadFloatArray(br, token);
            if (dryL != null && dryR != null && wetL != null && wetR != null &&
                dryL.Length == dryR.Length && wetL.Length == wetR.Length) {
                dereverb = new DereverbDecompositionResult(
                    new StereoStemBuffer("dry_vocal", dryL, dryR),
                    new StereoStemBuffer("wet_reverb", wetL, wetR),
                    drrDb);
            }
        }

        if (ver >= 2 && (flags & FlagHasChoreoTrack) != 0) {
            choreoTrack = ReadChoreoTrack(br, token);
        }

        return new NeuralSeparatedTrack {
            SourceTrackKey = sourceTrackKey,
            SampleRate = sampleRate,
            Stems = stems,
            MonoLeadVocalRef = leadRef,
            MonoBackingVocalRef = backingRef,
            DereverbAnalysis = dereverb,
            DerivedFromLosslessTwin = (flags & FlagDerivedFromLosslessTwin) != 0,
            PlanSignature = planSignature
        };
    }

    static void WriteChoreoTrack(BinaryWriter bw, SpatialChoreoTrack choreo, CancellationToken token) {
        bw.Write((int)choreo.Mode);
        bw.Write(choreo.StepSec);
        bw.Write(choreo.DurationSec);
        bw.Write(choreo.StemCount);
        bw.Write(choreo.FrameCount);

        for (int i = 0; i < choreo.StemCount; i++) {
            bw.Write((int)choreo.StemRoles[i]);
            WriteUtf8String(bw, choreo.StemNames[i]);
        }

        int cueCount = choreo.DirectorCues.Count;
        bw.Write(cueCount);
        for (int c = 0; c < cueCount; c++) {
            DirectorCue cue = choreo.DirectorCues[c];
            bw.Write(cue.TimeSec);
            bw.Write((int)cue.StemRole);
            bw.Write(cue.TargetAzDeg);
            bw.Write(cue.TargetElDeg);
            bw.Write(cue.Radius);
            bw.Write(cue.Energy01);
        }

        Vector4[] lutVec = new Vector4[choreo.LutEntryCount];
        int copied = choreo.CopyLutTo(lutVec);
        float[] packed = new float[copied * 4];
        for (int i = 0; i < copied; i++) {
            int p = i * 4;
            packed[p] = lutVec[i].X;
            packed[p + 1] = lutVec[i].Y;
            packed[p + 2] = lutVec[i].Z;
            packed[p + 3] = lutVec[i].W;
        }
        WriteFloatArray(bw, packed, token);
    }

    static SpatialChoreoTrack ReadChoreoTrack(BinaryReader br, CancellationToken token) {
        int modeInt = br.ReadInt32();
        double stepSec = br.ReadDouble();
        double durationSec = br.ReadDouble();
        int stemCount = br.ReadInt32();
        int frameCount = br.ReadInt32();
        if (stemCount <= 0 || stemCount > 32 || frameCount <= 0 || frameCount > 360000) {
            throw new InvalidDataException("Invalid choreography LUT dimensions in CNTC v2 cache.");
        }

        NeuralStemRole[] roles = new NeuralStemRole[stemCount];
        string[] names = new string[stemCount];
        for (int i = 0; i < stemCount; i++) {
            int r = br.ReadInt32();
            roles[i] = Enum.IsDefined(typeof(NeuralStemRole), r) ? (NeuralStemRole)r : NeuralStemRole.Other;
            names[i] = ReadUtf8String(br);
        }

        int cueCount = br.ReadInt32();
        if (cueCount < 0 || cueCount > 8192) {
            throw new InvalidDataException("Invalid choreography cue count in CNTC v2 cache.");
        }
        DirectorCue[] cues = new DirectorCue[cueCount];
        for (int c = 0; c < cueCount; c++) {
            double t = br.ReadDouble();
            int r = br.ReadInt32();
            float az = br.ReadSingle();
            float el = br.ReadSingle();
            float rad = br.ReadSingle();
            float en = br.ReadSingle();
            NeuralStemRole role = Enum.IsDefined(typeof(NeuralStemRole), r) ? (NeuralStemRole)r : NeuralStemRole.Other;
            cues[c] = new DirectorCue(t, role, az, el, rad, en);
        }

        float[] packed = ReadFloatArray(br, token);
        int expectedEntries = stemCount * frameCount;
        if (packed == null || packed.Length != expectedEntries * 4) {
            throw new InvalidDataException("Invalid choreography LUT payload length in CNTC v2 cache.");
        }
        Vector4[] lut = new Vector4[expectedEntries];
        for (int i = 0; i < expectedEntries; i++) {
            int p = i * 4;
            lut[i] = new Vector4(packed[p], packed[p + 1], packed[p + 2], packed[p + 3]);
        }

        SpatialChoreoMode mode = Enum.IsDefined(typeof(SpatialChoreoMode), modeInt)
            ? (SpatialChoreoMode)modeInt
            : SpatialChoreoMode.Musical;
        return SpatialChoreoTrack.FromSerialized(mode, stepSec, durationSec, roles, names, cues, lut, frameCount);
    }

    static void WriteUtf8String(BinaryWriter bw, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        bw.Write(bytes.Length);
        if (bytes.Length > 0) {
            bw.Write(bytes);
        }
    }

    static string ReadUtf8String(BinaryReader br) {
        int len = br.ReadInt32();
        if (len < 0 || len > 1_048_576) {
            throw new InvalidDataException("Invalid string length in neural track cache.");
        }
        if (len == 0) {
            return string.Empty;
        }
        byte[] bytes = br.ReadBytes(len);
        if (bytes.Length != len) {
            throw new EndOfStreamException();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    static void WriteFloatArray(BinaryWriter bw, float[] data, CancellationToken token) {
        int len = data?.Length ?? 0;
        bw.Write(len);
        if (len == 0) {
            return;
        }
        const int chunkFloats = 16384;
        byte[] buf = new byte[chunkFloats * sizeof(float)];
        int offset = 0;
        while (offset < len) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(chunkFloats, len - offset);
            Buffer.BlockCopy(data, offset * sizeof(float), buf, 0, count * sizeof(float));
            bw.Write(buf, 0, count * sizeof(float));
            offset += count;
        }
    }

    static float[] ReadFloatArray(BinaryReader br, CancellationToken token) {
        int len = br.ReadInt32();
        if (len < 0 || len > 48000 * 60 * 30) { // max 30 min @ 48 kHz guard
            throw new InvalidDataException("Invalid PCM array length in neural track cache.");
        }
        float[] data = new float[len];
        if (len == 0) {
            return data;
        }
        const int chunkFloats = 16384;
        byte[] buf = new byte[chunkFloats * sizeof(float)];
        int offset = 0;
        while (offset < len) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(chunkFloats, len - offset);
            int byteCount = count * sizeof(float);
            int readTotal = 0;
            while (readTotal < byteCount) {
                int r = br.BaseStream.Read(buf, readTotal, byteCount - readTotal);
                if (r <= 0) {
                    throw new EndOfStreamException();
                }
                readTotal += r;
            }
            Buffer.BlockCopy(buf, 0, data, offset * sizeof(float), byteCount);
            offset += count;
        }
        return data;
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
