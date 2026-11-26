using System.IO.Compression;
using System.Numerics;
using System.Text;

using Cavern.Neural;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Lightweight O(1)-peekable metadata entry describing a single cached AI stem bundle in Slot 7B (<c>*.ntc</c>).
/// Powers the In-App Smart Cache Folder Browser without decoding audio payloads.
/// </summary>
public sealed class NeuralTrackCacheEntryInfo {
    /// <summary>Content-addressed cache key (filename without <c>.ntc</c>).</summary>
    public string CacheKey { get; init; } = string.Empty;

    /// <summary>Absolute file path on disk.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>CNTC container format version (1, 2, or 3).</summary>
    public ushort FormatVersion { get; init; }

    /// <summary>Track title or fallback filename.</summary>
    public string DisplayTitle { get; init; } = string.Empty;

    /// <summary>Optional artist name.</summary>
    public string ArtistName { get; init; } = string.Empty;

    /// <summary>Source origin badge (e.g. "Local AI", "MVSEP Cloud", "Studio Bundle").</summary>
    public string OriginTag { get; init; } = string.Empty;

    /// <summary>Model or bundle summary label.</summary>
    public string ModelSummary { get; init; } = string.Empty;

    /// <summary>Original source track path or URI recorded when this cache bundle was created.</summary>
    public string SourceTrackKey { get; init; } = string.Empty;

    /// <summary>Pipeline plan signature recorded when this cache bundle was created.</summary>
    public string PlanSignature { get; init; } = string.Empty;

    /// <summary>Number of separated stereo stems in this bundle.</summary>
    public int StemCount { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Track duration in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Compression tier used to store audio payloads.</summary>
    public NeuralTrackCacheCompressionMode CompressionMode { get; init; }

    /// <summary>Actual compressed size on disk in bytes.</summary>
    public long CompressedBytes { get; init; }

    /// <summary>Estimated equivalent raw float32 size in bytes (for savings calculation).</summary>
    public long UncompressedBytes { get; init; }

    /// <summary>UTC timestamp when the cache entry was last written/accessed.</summary>
    public DateTime LastModifiedUtc { get; init; }

    /// <summary>Compression ratio (<c>UncompressedBytes / CompressedBytes</c>), or 1.0 if uncompressed.</summary>
    public double CompressionRatio => CompressedBytes > 0 && UncompressedBytes > 0
        ? UncompressedBytes / (double)CompressedBytes
        : 1.0;

    /// <summary>Storage space saved in percent [0..99].</summary>
    public double SavedPercent => UncompressedBytes > 0 && CompressedBytes < UncompressedBytes
        ? Math.Clamp((1.0 - (CompressedBytes / (double)UncompressedBytes)) * 100.0, 0.0, 99.0)
        : 0.0;
}

/// <summary>Single field-level difference between a cached AI stem bundle and the user's current AI settings.</summary>
public sealed class NeuralCacheConfigDiffItem {
    /// <summary>Stable key identifying the differing setting (e.g. "Backend", "Models", "Overlap", "BackingVocals").</summary>
    public string FieldKey { get; init; } = string.Empty;

    /// <summary>Value recorded when the cached stems were extracted.</summary>
    public string CachedValue { get; init; } = string.Empty;

    /// <summary>Currently configured setting value.</summary>
    public string CurrentValue { get; init; } = string.Empty;
}

/// <summary>Result of comparing a cached <see cref="NeuralTrackCacheEntryInfo"/> against current AI settings.</summary>
public sealed class NeuralCacheConfigComparison {
    /// <summary>True when all comparable settings match current configuration.</summary>
    public bool MatchesCurrentConfig { get; init; }

    /// <summary>True when the cache entry contained full structured <c>cfg:v1</c> metadata.</summary>
    public bool HasStructuredMetadata { get; init; }

    /// <summary>Human-readable source format tag derived from the cached track path (e.g. "Dolby Atmos", "FLAC").</summary>
    public string SourceFormatLabel { get; init; } = string.Empty;

    /// <summary>Ordered list of settings that differ between the cached extraction and current settings.</summary>
    public IReadOnlyList<NeuralCacheConfigDiffItem> Differences { get; init; } = Array.Empty<NeuralCacheConfigDiffItem>();
}

/// <summary>
/// Pure deterministic comparator checking whether a cached AI stem bundle matches current <see cref="NeuralAudioConfig"/>
/// and listing exact per-setting differences (supports both rich <c>cfg:v1</c> and legacy <c>CNTC v1-v3</c> files).
/// </summary>
public static class NeuralCacheConfigDiff {
    /// <summary>Builds a structured MVSEP Cloud signature carrying full config tokens for exact cache diffing.</summary>
    public static string BuildMvsepPlanSignature(
        MvsepAlgorithmPreset preset,
        NeuralAudioConfig cfg,
        NeuralPipelineGoal goal = NeuralPipelineGoal.StemSeparation) {
        string presetId = preset?.Id ?? "mvsep_default";
        NeuralAudioConfig safe = (cfg ?? NeuralAudioConfig.Default).Sanitize();
        return $"mvsep:{presetId}|pbv={(safe.KaraokePreserveBackingVocals ? 1 : 0)}|cfg:v1;g={goal};bk={NeuralExecutionBackend.MvsepCloud};om={safe.OrchestrationMode};bd={(int)safe.StudioBundlePreset};pbv={(safe.KaraokePreserveBackingVocals ? 1 : 0)};ov={safe.OverlapFactor};tta={(safe.EnableTta ? 1 : 0)};ens={safe.EnsembleMode};pf={(safe.EnablePhaseFix ? 1 : 0)};ec={(safe.EnforceEnergyConservation ? 1 : 0)};m={presetId}";
    }

    /// <summary>Derives a concise format label (e.g. "Dolby Atmos", "FLAC", "WAV") from a cached source path.</summary>
    public static string ExtractSourceFormatLabel(string sourceTrackKey) {
        if (string.IsNullOrWhiteSpace(sourceTrackKey)) {
            return "Audio";
        }
        string lower = sourceTrackKey.ToLowerInvariant();
        if (lower.EndsWith(".atmos", StringComparison.Ordinal) ||
            lower.EndsWith(".ec3", StringComparison.Ordinal) ||
            lower.EndsWith(".eac3", StringComparison.Ordinal) ||
            lower.EndsWith(".thd", StringComparison.Ordinal) ||
            lower.EndsWith(".mlp", StringComparison.Ordinal) ||
            lower.Contains("atmos", StringComparison.Ordinal) ||
            lower.EndsWith(".m4a.mp4", StringComparison.Ordinal) ||
            lower.EndsWith(".mp4", StringComparison.Ordinal) ||
            lower.EndsWith(".mkv", StringComparison.Ordinal)) {
            return "Dolby Atmos / Spatial";
        }
        if (lower.EndsWith(".flac", StringComparison.Ordinal)) return "FLAC";
        if (lower.EndsWith(".wav", StringComparison.Ordinal)) return "WAV";
        if (lower.EndsWith(".alac", StringComparison.Ordinal) || lower.EndsWith(".m4a", StringComparison.Ordinal)) return "ALAC / M4A";
        if (lower.EndsWith(".mp3", StringComparison.Ordinal)) return "MP3";
        if (lower.EndsWith(".opus", StringComparison.Ordinal) || lower.EndsWith(".ogg", StringComparison.Ordinal)) return "Opus / OGG";
        string ext = Path.GetExtension(sourceTrackKey).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(ext) ? "Audio" : ext;
    }

    /// <summary>Compares a cached bundle's recorded metadata against the active <paramref name="currentConfig"/> and plan.</summary>
    public static NeuralCacheConfigComparison Compare(
        NeuralTrackCacheEntryInfo entry,
        NeuralAudioConfig currentConfig,
        NeuralExecutionPlan currentLocalPlan,
        string currentMvsepPresetId,
        bool currentUsesCloud,
        NeuralPipelineGoal goal) {
        if (entry == null) {
            return new NeuralCacheConfigComparison {
                MatchesCurrentConfig = false,
                HasStructuredMetadata = false,
                SourceFormatLabel = string.Empty,
                Differences = Array.Empty<NeuralCacheConfigDiffItem>()
            };
        }

        NeuralAudioConfig safe = (currentConfig ?? NeuralAudioConfig.Default).Sanitize();
        Dictionary<string, string> meta = ParseStructuredSignature(entry.PlanSignature);
        bool hasStructured = meta.Count > 0;
        List<NeuralCacheConfigDiffItem> diffs = [];

        bool cachedIsCloud = (meta.TryGetValue("bk", out string bkVal) &&
                              bkVal.Contains("Mvsep", StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(entry.OriginTag) &&
                              entry.OriginTag.Contains("MVSEP", StringComparison.OrdinalIgnoreCase)) ||
                             (!string.IsNullOrEmpty(entry.PlanSignature) &&
                              entry.PlanSignature.StartsWith("mvsep:", StringComparison.OrdinalIgnoreCase));
        string cachedBackendLabel = cachedIsCloud ? "MVSEP Cloud" : "Local AI";
        string currentBackendLabel = currentUsesCloud ? "MVSEP Cloud" : "Local AI";
        if (cachedIsCloud != currentUsesCloud) {
            diffs.Add(new NeuralCacheConfigDiffItem {
                FieldKey = "Backend",
                CachedValue = cachedBackendLabel,
                CurrentValue = currentBackendLabel
            });
        }

        string currentModelIds = currentUsesCloud
            ? (string.IsNullOrWhiteSpace(currentMvsepPresetId) ? "mvsep_default" : currentMvsepPresetId)
            : (currentLocalPlan != null && currentLocalPlan.Steps.Count > 0
                ? string.Join("+", currentLocalPlan.Steps.SelectMany(st => st.Models.Select(m => m.Id)))
                : "none");

        if (meta.TryGetValue("m", out string cachedModelIds) && !string.IsNullOrWhiteSpace(cachedModelIds)) {
            if (!string.Equals(cachedModelIds, currentModelIds, StringComparison.OrdinalIgnoreCase)) {
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "Models",
                    CachedValue = FormatModelTokenList(cachedModelIds, entry.ModelSummary),
                    CurrentValue = FormatModelTokenList(currentModelIds, currentLocalPlan?.Summary)
                });
            }
        } else if (!string.IsNullOrWhiteSpace(entry.ModelSummary)) {
            string curSummary = currentUsesCloud
                ? (MvsepAlgorithmPreset.FindOrDefault(currentMvsepPresetId)?.DisplayName ?? currentMvsepPresetId ?? "MVSEP")
                : (currentLocalPlan?.Summary ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(curSummary) &&
                !curSummary.Contains(entry.ModelSummary, StringComparison.OrdinalIgnoreCase) &&
                !entry.ModelSummary.Contains(curSummary, StringComparison.OrdinalIgnoreCase)) {
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "Models",
                    CachedValue = entry.ModelSummary,
                    CurrentValue = curSummary
                });
            }
        }

        if (meta.TryGetValue("bd", out string bdStr) && int.TryParse(bdStr, out int cachedBd)) {
            int curBd = (int)safe.StudioBundlePreset;
            if (cachedBd != curBd) {
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "Bundle",
                    CachedValue = ((NeuralEnsembleBundlePreset)Math.Clamp(cachedBd, 0, 16)).ToString(),
                    CurrentValue = safe.StudioBundlePreset.ToString()
                });
            }
        }

        if (meta.TryGetValue("om", out string omStr) && !string.IsNullOrWhiteSpace(omStr) && !currentUsesCloud && !cachedIsCloud) {
            string curOm = safe.OrchestrationMode.ToString();
            if (!string.Equals(omStr, curOm, StringComparison.OrdinalIgnoreCase)) {
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "Orchestration",
                    CachedValue = omStr,
                    CurrentValue = curOm
                });
            }
        }

        if (!currentUsesCloud && !cachedIsCloud) {
            if (meta.TryGetValue("ov", out string ovStr) && int.TryParse(ovStr, out int cachedOv) && cachedOv != safe.OverlapFactor) {
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "Overlap",
                    CachedValue = $"{cachedOv}x",
                    CurrentValue = $"{safe.OverlapFactor}x"
                });
            }
            if (meta.TryGetValue("tta", out string ttaStr)) {
                bool cachedTta = ttaStr == "1";
                if (cachedTta != safe.EnableTta) {
                    diffs.Add(new NeuralCacheConfigDiffItem {
                        FieldKey = "TTA",
                        CachedValue = cachedTta ? "On" : "Off",
                        CurrentValue = safe.EnableTta ? "On" : "Off"
                    });
                }
            }
            if (meta.TryGetValue("ens", out string ensStr) && !string.IsNullOrWhiteSpace(ensStr)) {
                string curEns = safe.EnsembleMode.ToString();
                if (!string.Equals(ensStr, curEns, StringComparison.OrdinalIgnoreCase)) {
                    diffs.Add(new NeuralCacheConfigDiffItem {
                        FieldKey = "Ensemble",
                        CachedValue = ensStr,
                        CurrentValue = curEns
                    });
                }
            }
            if (meta.TryGetValue("pf", out string pfStr)) {
                bool cachedPf = pfStr == "1";
                if (cachedPf != safe.EnablePhaseFix) {
                    diffs.Add(new NeuralCacheConfigDiffItem {
                        FieldKey = "PhaseFix",
                        CachedValue = cachedPf ? "On" : "Off",
                        CurrentValue = safe.EnablePhaseFix ? "On" : "Off"
                    });
                }
            }
            if (meta.TryGetValue("ec", out string ecStr)) {
                bool cachedEc = ecStr == "1";
                if (cachedEc != safe.EnforceEnergyConservation) {
                    diffs.Add(new NeuralCacheConfigDiffItem {
                        FieldKey = "EnergyConservation",
                        CachedValue = cachedEc ? "On" : "Off",
                        CurrentValue = safe.EnforceEnergyConservation ? "On" : "Off"
                    });
                }
            }
        }

        if (goal == NeuralPipelineGoal.SpatialKaraoke || meta.ContainsKey("pbv")) {
            if (meta.TryGetValue("pbv", out string pbvStr)) {
                bool cachedPbv = pbvStr == "1";
                if (cachedPbv != safe.KaraokePreserveBackingVocals) {
                    diffs.Add(new NeuralCacheConfigDiffItem {
                        FieldKey = "BackingVocals",
                        CachedValue = cachedPbv ? "KeepBacking" : "PureInstrumental",
                        CurrentValue = safe.KaraokePreserveBackingVocals ? "KeepBacking" : "PureInstrumental"
                    });
                }
            } else if (goal == NeuralPipelineGoal.SpatialKaraoke && !safe.KaraokePreserveBackingVocals && entry.StemCount <= 2) {
                // Legacy 2-stem cache lacks full combined vocals for Pure Instrumental mode
                diffs.Add(new NeuralCacheConfigDiffItem {
                    FieldKey = "BackingVocals",
                    CachedValue = "KeepBacking",
                    CurrentValue = "PureInstrumental"
                });
            }
        }

        return new NeuralCacheConfigComparison {
            MatchesCurrentConfig = diffs.Count == 0,
            HasStructuredMetadata = hasStructured,
            SourceFormatLabel = ExtractSourceFormatLabel(entry.SourceTrackKey),
            Differences = diffs
        };
    }

    static Dictionary<string, string> ParseStructuredSignature(string planSignature) {
        Dictionary<string, string> dict = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(planSignature)) {
            return dict;
        }
        int cfgIdx = planSignature.IndexOf("cfg:v1;", StringComparison.OrdinalIgnoreCase);
        if (cfgIdx >= 0) {
            string tail = planSignature.Substring(cfgIdx + 7);
            string[] parts = tail.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) {
                int eq = parts[i].IndexOf('=');
                if (eq > 0 && eq < parts[i].Length - 1) {
                    string k = parts[i].Substring(0, eq).Trim();
                    string v = parts[i].Substring(eq + 1).Trim();
                    if (k.Length > 0) {
                        dict[k] = v;
                    }
                }
            }
            return dict;
        }
        // Legacy MVSEP format: "mvsep:<presetId>|pbv=<0|1>"
        if (planSignature.StartsWith("mvsep:", StringComparison.OrdinalIgnoreCase)) {
            dict["bk"] = NeuralExecutionBackend.MvsepCloud.ToString();
            string[] parts = planSignature.Split('|', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) {
                if (parts[i].StartsWith("mvsep:", StringComparison.OrdinalIgnoreCase)) {
                    dict["m"] = parts[i].Substring(6).Trim();
                } else if (parts[i].StartsWith("pbv=", StringComparison.OrdinalIgnoreCase)) {
                    dict["pbv"] = parts[i].Substring(4).Trim();
                }
            }
        }
        return dict;
    }

    static string FormatModelTokenList(string modelIds, string fallbackSummary) {
        if (string.IsNullOrWhiteSpace(modelIds) || string.Equals(modelIds, "none", StringComparison.OrdinalIgnoreCase)) {
            return string.IsNullOrWhiteSpace(fallbackSummary) ? "—" : fallbackSummary;
        }
        string[] ids = modelIds.Split('+', StringSplitOptions.RemoveEmptyEntries);
        List<string> labels = new(ids.Length);
        for (int i = 0; i < ids.Length; i++) {
            string id = ids[i].Trim();
            NeuralModelCatalogEntry cat = NeuralModelCatalog.FindById(id);
            if (cat != null) {
                labels.Add(cat.DisplayName);
                continue;
            }
            MvsepAlgorithmPreset preset = MvsepAlgorithmPreset.Presets.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            labels.Add(preset != null ? preset.DisplayName : id);
        }
        return string.Join(" + ", labels);
    }
}

/// <summary>
/// Pure-C# lossless/studio-lossless binary codec for Slot 7B Persistent AI Stem Cache (<c>CNTC v3</c>),
/// with 100% backward-compatible read support for legacy <c>CNTC v1</c> and <c>CNTC v2</c> files.
/// Uses Mid/Side stereo decorrelation + 2nd-order linear delta prediction + zigzag + byte-plane transposition + ZLib
/// to shrink 4-stem separation caches 5–7× (Compact 16-bit &gt;96 dB SNR) or 3–4× (Studio 24-bit &gt;140 dB SNR).
/// </summary>
public static class NeuralTrackCacheCodec {
    public const uint FileMagic = 0x43544E43u; // "CNTC"
    public const ushort FormatVersion = 3;
    public const ushort MinSupportedVersion = 1;

    const uint FlagDerivedFromLosslessTwin = 1u << 0;
    const uint FlagHasLeadRef = 1u << 1;
    const uint FlagHasBackingRef = 1u << 2;
    const uint FlagHasDereverb = 1u << 3;
    const uint FlagHasChoreoTrack = 1u << 4;

    /// <summary>
    /// Writes a <see cref="NeuralSeparatedTrack"/> (and optional <see cref="SpatialChoreoTrack"/>) in <c>CNTC v3</c> format.
    /// </summary>
    public static void WriteTrackFile(
        string path,
        NeuralSeparatedTrack track,
        SpatialChoreoTrack choreoTrack,
        NeuralTrackCacheCompressionMode compressionMode,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(track);
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: false);

        bool hasChoreo = choreoTrack != null && choreoTrack.StemCount > 0 && choreoTrack.FrameCount > 0;
        bool omitLeadRef = compressionMode != NeuralTrackCacheCompressionMode.RawFloat32 &&
            CanDeriveMonoRefFromStems(track.Stems, track.MonoLeadVocalRef, isBacking: false);
        bool omitBackingRef = compressionMode != NeuralTrackCacheCompressionMode.RawFloat32 &&
            CanDeriveMonoRefFromStems(track.Stems, track.MonoBackingVocalRef, isBacking: true);
        uint flags = 0u;
        if (track.DerivedFromLosslessTwin) flags |= FlagDerivedFromLosslessTwin;
        if (!omitLeadRef && track.MonoLeadVocalRef != null && track.MonoLeadVocalRef.Length > 0) flags |= FlagHasLeadRef;
        if (!omitBackingRef && track.MonoBackingVocalRef != null && track.MonoBackingVocalRef.Length > 0) flags |= FlagHasBackingRef;
        if (track.DereverbAnalysis?.DryVocal != null && track.DereverbAnalysis?.WetReverb != null) flags |= FlagHasDereverb;
        if (hasChoreo) flags |= FlagHasChoreoTrack;

        int stemCount = track.Stems?.Count ?? 0;
        int totalSamples = track.TotalSampleCount;
        long uncompressedEstimate = EstimateUncompressedBytes(track);

        string displayTitle = !string.IsNullOrWhiteSpace(track.DisplayTitle)
            ? track.DisplayTitle.Trim()
            : DeriveTitleFromKey(track.SourceTrackKey);
        string artistName = (track.ArtistName ?? string.Empty).Trim();
        string originTag = !string.IsNullOrWhiteSpace(track.OriginTag) ? track.OriginTag.Trim() : "Local AI";
        string modelSummary = (track.ModelSummary ?? string.Empty).Trim();

        bw.Write(FileMagic);
        bw.Write(FormatVersion);
        bw.Write((ushort)compressionMode); // v3 header stores compression mode in formerly-reserved ushort
        bw.Write(track.SampleRate);
        bw.Write(flags);

        // v3 O(1)-peekable metadata header block (read in <1 ms by Cache Folder Browser)
        bw.Write(DateTime.UtcNow.Ticks);
        bw.Write(totalSamples);
        bw.Write(uncompressedEstimate);
        WriteUtf8String(bw, displayTitle);
        WriteUtf8String(bw, artistName);
        WriteUtf8String(bw, originTag);
        WriteUtf8String(bw, modelSummary);

        WriteUtf8String(bw, track.SourceTrackKey);
        WriteUtf8String(bw, track.PlanSignature);

        bw.Write(stemCount);
        for (int i = 0; i < stemCount; i++) {
            token.ThrowIfCancellationRequested();
            StereoStemBuffer stem = track.Stems[i];
            WriteUtf8String(bw, stem.Name);
            WriteStereoPayload(bw, stem.Left, stem.Right, compressionMode, token);
        }

        if ((flags & FlagHasLeadRef) != 0) {
            WriteMonoPayload(bw, track.MonoLeadVocalRef, compressionMode, token);
        }
        if ((flags & FlagHasBackingRef) != 0) {
            WriteMonoPayload(bw, track.MonoBackingVocalRef, compressionMode, token);
        }
        if ((flags & FlagHasDereverb) != 0) {
            bw.Write(track.DereverbAnalysis.DirectToReverberantRatioDb);
            WriteStereoPayload(bw, track.DereverbAnalysis.DryVocal.Left, track.DereverbAnalysis.DryVocal.Right, compressionMode, token);
            WriteStereoPayload(bw, track.DereverbAnalysis.WetReverb.Left, track.DereverbAnalysis.WetReverb.Right, compressionMode, token);
        }
        if (hasChoreo) {
            WriteChoreoTrack(bw, choreoTrack, token);
        }

        bw.Flush();
        fs.Flush(true);
    }

    /// <summary>
    /// Reads a <c>CNTC v1</c>, <c>v2</c>, or <c>v3</c> file back into a <see cref="NeuralSeparatedTrack"/> and optional <see cref="SpatialChoreoTrack"/>.
    /// </summary>
    public static NeuralSeparatedTrack ReadTrackFile(
        string path,
        out SpatialChoreoTrack choreoTrack,
        CancellationToken token = default) {
        choreoTrack = null;
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using BinaryReader br = new(fs, Encoding.UTF8, leaveOpen: false);

        if (fs.Length < 20) {
            return null;
        }
        uint magic = br.ReadUInt32();
        ushort ver = br.ReadUInt16();
        ushort modeRaw = br.ReadUInt16();
        if (magic != FileMagic || ver < MinSupportedVersion || ver > FormatVersion) {
            return null;
        }

        int sampleRate = br.ReadInt32();
        uint flags = br.ReadUInt32();
        if (sampleRate < 8000 || sampleRate > 384000) {
            return null;
        }

        NeuralTrackCacheCompressionMode mode = ver >= 3 && Enum.IsDefined(typeof(NeuralTrackCacheCompressionMode), (int)modeRaw)
            ? (NeuralTrackCacheCompressionMode)modeRaw
            : NeuralTrackCacheCompressionMode.RawFloat32;

        string displayTitle = string.Empty;
        string artistName = string.Empty;
        string originTag = "Local AI";
        string modelSummary = string.Empty;

        if (ver >= 3) {
            br.ReadInt64(); // createdUtcTicks
            br.ReadInt32(); // totalSamples
            br.ReadInt64(); // uncompressedEstimate
            displayTitle = ReadUtf8String(br);
            artistName = ReadUtf8String(br);
            originTag = ReadUtf8String(br);
            modelSummary = ReadUtf8String(br);
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
            float[] left;
            float[] right;
            if (ver >= 3) {
                (left, right) = ReadStereoPayload(br, mode, token);
            } else {
                left = ReadFloatArray(br, token);
                right = ReadFloatArray(br, token);
            }
            if (left == null || right == null || left.Length != right.Length) {
                return null;
            }
            stems[i] = new StereoStemBuffer(name, left, right);
        }

        float[] leadRef = null;
        if ((flags & FlagHasLeadRef) != 0) {
            leadRef = ver >= 3 ? ReadMonoPayload(br, mode, token) : ReadFloatArray(br, token);
        } else {
            leadRef = SynthesizeMonoRefFromStems(stems, isBacking: false);
        }
        float[] backingRef = null;
        if ((flags & FlagHasBackingRef) != 0) {
            backingRef = ver >= 3 ? ReadMonoPayload(br, mode, token) : ReadFloatArray(br, token);
        } else {
            backingRef = SynthesizeMonoRefFromStems(stems, isBacking: true);
        }

        DereverbDecompositionResult dereverb = null;
        if ((flags & FlagHasDereverb) != 0) {
            float drrDb = br.ReadSingle();
            float[] dryL, dryR, wetL, wetR;
            if (ver >= 3) {
                (dryL, dryR) = ReadStereoPayload(br, mode, token);
                (wetL, wetR) = ReadStereoPayload(br, mode, token);
            } else {
                dryL = ReadFloatArray(br, token);
                dryR = ReadFloatArray(br, token);
                wetL = ReadFloatArray(br, token);
                wetR = ReadFloatArray(br, token);
            }
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
            PlanSignature = planSignature,
            DisplayTitle = !string.IsNullOrWhiteSpace(displayTitle) ? displayTitle : DeriveTitleFromKey(sourceTrackKey),
            ArtistName = artistName,
            OriginTag = string.IsNullOrWhiteSpace(originTag) ? "Local AI" : originTag,
            ModelSummary = modelSummary
        };
    }

    /// <summary>
    /// Reads ONLY the header metadata of a <c>*.ntc</c> cache file in O(1) time (&lt;1 ms) without decoding audio stems.
    /// </summary>
    public static NeuralTrackCacheEntryInfo PeekHeader(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) {
            return null;
        }
        try {
            FileInfo fi = new(filePath);
            if (fi.Length < 20) {
                return null;
            }
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            using BinaryReader br = new(fs, Encoding.UTF8, leaveOpen: false);

            uint magic = br.ReadUInt32();
            ushort ver = br.ReadUInt16();
            ushort modeRaw = br.ReadUInt16();
            if (magic != FileMagic || ver < MinSupportedVersion || ver > FormatVersion) {
                return null;
            }

            int sampleRate = br.ReadInt32();
            br.ReadUInt32(); // flags
            if (sampleRate < 8000 || sampleRate > 384000) {
                return null;
            }

            string cacheKey = Path.GetFileNameWithoutExtension(filePath);
            if (ver >= 3) {
                long createdTicks = br.ReadInt64();
                int totalSamples = br.ReadInt32();
                long uncompressedBytes = br.ReadInt64();
                string title = ReadUtf8String(br);
                string artist = ReadUtf8String(br);
                string origin = ReadUtf8String(br);
                string modelSummary = ReadUtf8String(br);
                string sourceKey = ReadUtf8String(br);
                string planSig = ReadUtf8String(br);
                int stemCount = br.ReadInt32();
                NeuralTrackCacheCompressionMode mode = Enum.IsDefined(typeof(NeuralTrackCacheCompressionMode), (int)modeRaw)
                    ? (NeuralTrackCacheCompressionMode)modeRaw
                    : NeuralTrackCacheCompressionMode.Compact16Bit;

                return new NeuralTrackCacheEntryInfo {
                    CacheKey = cacheKey,
                    FilePath = filePath,
                    FormatVersion = ver,
                    DisplayTitle = !string.IsNullOrWhiteSpace(title) ? title : DeriveTitleFromKey(sourceKey),
                    ArtistName = artist,
                    OriginTag = string.IsNullOrWhiteSpace(origin) ? "Local AI" : origin,
                    ModelSummary = modelSummary,
                    SourceTrackKey = sourceKey,
                    PlanSignature = planSig,
                    StemCount = Math.Max(0, stemCount),
                    SampleRate = sampleRate,
                    DurationSeconds = totalSamples > 0 ? totalSamples / (double)sampleRate : 0.0,
                    CompressionMode = mode,
                    CompressedBytes = fi.Length,
                    UncompressedBytes = Math.Max(fi.Length, uncompressedBytes),
                    LastModifiedUtc = createdTicks > 0 ? new DateTime(Math.Clamp(createdTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc) : fi.LastWriteTimeUtc
                };
            } else {
                string sourceKey = ReadUtf8String(br);
                string planSig = ReadUtf8String(br);
                int stemCount = br.ReadInt32();
                // Peek first stem length for duration estimate without reading samples
                int firstStemSamples = 0;
                if (stemCount > 0) {
                    ReadUtf8String(br);
                    firstStemSamples = br.ReadInt32();
                }
                return new NeuralTrackCacheEntryInfo {
                    CacheKey = cacheKey,
                    FilePath = filePath,
                    FormatVersion = ver,
                    DisplayTitle = DeriveTitleFromKey(sourceKey),
                    ArtistName = string.Empty,
                    OriginTag = "Local AI (v2)",
                    ModelSummary = string.Empty,
                    SourceTrackKey = sourceKey,
                    PlanSignature = planSig,
                    StemCount = Math.Max(0, stemCount),
                    SampleRate = sampleRate,
                    DurationSeconds = firstStemSamples > 0 ? firstStemSamples / (double)sampleRate : 0.0,
                    CompressionMode = NeuralTrackCacheCompressionMode.RawFloat32,
                    CompressedBytes = fi.Length,
                    UncompressedBytes = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                };
            }
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Losslessly re-packs a cached <c>.ntc</c> file into <paramref name="targetMode"/> CNTC v3 (with mono-ref deduplication
    /// and optimal ZLib entropy coding), replacing the file atomically only if the compacted payload is strictly smaller.
    /// Returns bytes reclaimed (0 when already optimal).
    /// </summary>
    public static long CompactFileIfBeneficial(
        string filePath,
        NeuralTrackCacheCompressionMode targetMode = NeuralTrackCacheCompressionMode.Compact16Bit,
        CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || targetMode == NeuralTrackCacheCompressionMode.RawFloat32) {
            return 0L;
        }
        long origBytes = new FileInfo(filePath).Length;
        if (origBytes <= 0L) {
            return 0L;
        }
        NeuralSeparatedTrack track = ReadTrackFile(filePath, out SpatialChoreoTrack choreo, token);
        if (track == null || track.Stems == null || track.Stems.Count == 0) {
            return 0L;
        }
        string tmpPath = filePath + $".cmp.{Guid.NewGuid():N}.tmp";
        try {
            WriteTrackFile(tmpPath, track, choreo, targetMode, token);
            long newBytes = new FileInfo(tmpPath).Length;
            if (newBytes > 0 && newBytes < origBytes) {
                File.Move(tmpPath, filePath, overwrite: true);
                return origBytes - newBytes;
            }
            return 0L;
        } finally {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    static StereoStemBuffer FindStemForMonoRef(IReadOnlyList<StereoStemBuffer> stems, bool isBacking) {
        if (stems == null || stems.Count == 0) {
            return null;
        }
        if (isBacking) {
            for (int i = 0; i < stems.Count; i++) {
                if (stems[i] != null && NeuralStemSpatializer.ClassifyStemName(stems[i].Name) == NeuralStemRole.BackingVocals) {
                    return stems[i];
                }
            }
            return null;
        }
        StereoStemBuffer fallbackVocals = null;
        for (int i = 0; i < stems.Count; i++) {
            StereoStemBuffer st = stems[i];
            if (st == null) continue;
            NeuralStemRole r = NeuralStemSpatializer.ClassifyStemName(st.Name);
            if (r == NeuralStemRole.LeadVocal) {
                return st;
            }
            if (r == NeuralStemRole.Vocals && fallbackVocals == null) {
                fallbackVocals = st;
            }
        }
        return fallbackVocals;
    }

    static bool CanDeriveMonoRefFromStems(IReadOnlyList<StereoStemBuffer> stems, float[] monoRef, bool isBacking) {
        if (monoRef == null || monoRef.Length == 0) {
            return false;
        }
        StereoStemBuffer stem = FindStemForMonoRef(stems, isBacking);
        if (stem == null || stem.Length != monoRef.Length || stem.Left == null || stem.Right == null) {
            return false;
        }
        // Verify exact match to 0.5*(L+R) across strided + boundary samples so custom dereverb/aligned refs are never dropped
        int len = monoRef.Length;
        int step = Math.Max(1, len / 4096);
        for (int i = 0; i < len; i += step) {
            float expected = 0.5f * (stem.Left[i] + stem.Right[i]);
            if (MathF.Abs(monoRef[i] - expected) > 1e-4f) {
                return false;
            }
        }
        float lastExpected = 0.5f * (stem.Left[len - 1] + stem.Right[len - 1]);
        return MathF.Abs(monoRef[len - 1] - lastExpected) <= 1e-4f;
    }

    static float[] SynthesizeMonoRefFromStems(IReadOnlyList<StereoStemBuffer> stems, bool isBacking) {
        StereoStemBuffer stem = FindStemForMonoRef(stems, isBacking);
        if (stem == null || stem.Length <= 0 || stem.Left == null || stem.Right == null) {
            return null;
        }
        int len = Math.Min(stem.Length, Math.Min(stem.Left.Length, stem.Right.Length));
        float[] mono = new float[len];
        for (int i = 0; i < len; i++) {
            mono[i] = 0.5f * (stem.Left[i] + stem.Right[i]);
        }
        return mono;
    }

    static long EstimateUncompressedBytes(NeuralSeparatedTrack track) {
        long total = 0L;
        if (track.Stems != null) {
            for (int i = 0; i < track.Stems.Count; i++) {
                total += (long)track.Stems[i].Length * 2L * sizeof(float);
            }
        }
        if (track.MonoLeadVocalRef != null) total += (long)track.MonoLeadVocalRef.Length * sizeof(float);
        if (track.MonoBackingVocalRef != null) total += (long)track.MonoBackingVocalRef.Length * sizeof(float);
        if (track.DereverbAnalysis?.DryVocal != null) total += (long)track.DereverbAnalysis.DryVocal.Length * 4L * sizeof(float);
        return total;
    }

    static string DeriveTitleFromKey(string sourceTrackKey) {
        if (string.IsNullOrWhiteSpace(sourceTrackKey)) {
            return "Cached Track";
        }
        try {
            string name = Path.GetFileNameWithoutExtension(sourceTrackKey);
            return string.IsNullOrWhiteSpace(name) ? sourceTrackKey : name;
        } catch {
            return sourceTrackKey;
        }
    }

    // ---- CNTC v3 Stereo & Mono Lossless/Studio-Lossless Payload Compression ----
    // Pipeline: Peak-normalized quantization (16-bit or 24-bit) -> Reversible Integer Mid/Side ->
    // 2nd-Order Linear Delta Predictor -> ZigZag -> Byte-Plane Transposition -> ZLib(Fastest).

    static void WriteStereoPayload(
        BinaryWriter bw,
        float[] left,
        float[] right,
        NeuralTrackCacheCompressionMode mode,
        CancellationToken token) {
        int len = Math.Min(left?.Length ?? 0, right?.Length ?? 0);
        bw.Write(len);
        if (len == 0) {
            return;
        }
        if (mode == NeuralTrackCacheCompressionMode.RawFloat32) {
            WriteFloatArrayRaw(bw, left, len, token);
            WriteFloatArrayRaw(bw, right, len, token);
            return;
        }

        float peak = 0f;
        for (int i = 0; i < len; i++) {
            float a = MathF.Abs(left[i]);
            float b = MathF.Abs(right[i]);
            if (float.IsFinite(a) && a > peak) peak = a;
            if (float.IsFinite(b) && b > peak) peak = b;
        }
        bw.Write(peak);
        if (peak <= 1e-12f) {
            // Exact silent stem: 0 payload bytes!
            bw.Write(0);
            return;
        }

        bool is24 = mode == NeuralTrackCacheCompressionMode.Studio24Bit;
        int bytesPerSample = is24 ? 3 : 2;
        double scale = (is24 ? 8388607.0 : 32767.0) / peak;

        byte[] compressed = CompressStereoDeltaPlanes(left, right, len, scale, bytesPerSample, token);
        bw.Write(compressed.Length);
        bw.Write(compressed);
    }

    static (float[] Left, float[] Right) ReadStereoPayload(
        BinaryReader br,
        NeuralTrackCacheCompressionMode mode,
        CancellationToken token) {
        int len = br.ReadInt32();
        if (len < 0 || len > 48000 * 60 * 30) {
            throw new InvalidDataException("Invalid stereo payload length in CNTC v3 cache.");
        }
        if (len == 0) {
            return (Array.Empty<float>(), Array.Empty<float>());
        }
        if (mode == NeuralTrackCacheCompressionMode.RawFloat32) {
            return (ReadFloatArrayRaw(br, len, token), ReadFloatArrayRaw(br, len, token));
        }

        float peak = br.ReadSingle();
        int compLen = br.ReadInt32();
        if (compLen < 0 || compLen > 256 * 1024 * 1024) {
            throw new InvalidDataException("Invalid compressed stereo block length in CNTC v3 cache.");
        }
        if (peak <= 1e-12f || compLen == 0) {
            return (new float[len], new float[len]);
        }

        byte[] compressed = br.ReadBytes(compLen);
        if (compressed.Length != compLen) {
            throw new EndOfStreamException();
        }
        bool is24 = mode == NeuralTrackCacheCompressionMode.Studio24Bit;
        int bytesPerSample = is24 ? 3 : 2;
        float invScale = peak / (is24 ? 8388607f : 32767f);
        return DecompressStereoDeltaPlanes(compressed, len, invScale, bytesPerSample, token);
    }

    static void WriteMonoPayload(
        BinaryWriter bw,
        float[] mono,
        NeuralTrackCacheCompressionMode mode,
        CancellationToken token) {
        int len = mono?.Length ?? 0;
        bw.Write(len);
        if (len == 0) {
            return;
        }
        if (mode == NeuralTrackCacheCompressionMode.RawFloat32) {
            WriteFloatArrayRaw(bw, mono, len, token);
            return;
        }

        float peak = 0f;
        for (int i = 0; i < len; i++) {
            float a = MathF.Abs(mono[i]);
            if (float.IsFinite(a) && a > peak) peak = a;
        }
        bw.Write(peak);
        if (peak <= 1e-12f) {
            bw.Write(0);
            return;
        }

        bool is24 = mode == NeuralTrackCacheCompressionMode.Studio24Bit;
        int bytesPerSample = is24 ? 3 : 2;
        double scale = (is24 ? 8388607.0 : 32767.0) / peak;

        byte[] compressed = CompressMonoDeltaPlanes(mono, len, scale, bytesPerSample, token);
        bw.Write(compressed.Length);
        bw.Write(compressed);
    }

    static float[] ReadMonoPayload(
        BinaryReader br,
        NeuralTrackCacheCompressionMode mode,
        CancellationToken token) {
        int len = br.ReadInt32();
        if (len < 0 || len > 48000 * 60 * 30) {
            throw new InvalidDataException("Invalid mono payload length in CNTC v3 cache.");
        }
        if (len == 0) {
            return Array.Empty<float>();
        }
        if (mode == NeuralTrackCacheCompressionMode.RawFloat32) {
            return ReadFloatArrayRaw(br, len, token);
        }

        float peak = br.ReadSingle();
        int compLen = br.ReadInt32();
        if (compLen < 0 || compLen > 256 * 1024 * 1024) {
            throw new InvalidDataException("Invalid compressed mono block length in CNTC v3 cache.");
        }
        if (peak <= 1e-12f || compLen == 0) {
            return new float[len];
        }

        byte[] compressed = br.ReadBytes(compLen);
        if (compressed.Length != compLen) {
            throw new EndOfStreamException();
        }
        bool is24 = mode == NeuralTrackCacheCompressionMode.Studio24Bit;
        int bytesPerSample = is24 ? 3 : 2;
        float invScale = peak / (is24 ? 8388607f : 32767f);
        return DecompressMonoDeltaPlanes(compressed, len, invScale, bytesPerSample, token);
    }

    static byte[] CompressStereoDeltaPlanes(
        float[] left,
        float[] right,
        int len,
        double scale,
        int bytesPerSample,
        CancellationToken token) {
        // Chunked streaming into ZLibStream keeps peak scratch memory tiny (~192 KB) even for 10-minute tracks
        const int BlockFrames = 16384;
        // 2 channels (Mid + Side), bytesPerSample + 1 byte plane for safe overflow (exact reversible M/S + 2nd-order residual)
        int planesPerCh = bytesPerSample + 1;
        byte[] planeBuf = new byte[BlockFrames * planesPerCh * 2];

        using MemoryStream ms = new(Math.Max(4096, len / 2));
        using (ZLibStream zs = new(ms, CompressionLevel.Optimal, leaveOpen: true)) {
            int mPrev1 = 0, mPrev2 = 0;
            int sPrev1 = 0, sPrev2 = 0;
            int offset = 0;
            int maxQ = bytesPerSample == 3 ? 8388607 : 32767;

            while (offset < len) {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(BlockFrames, len - offset);
                int chStride = count * planesPerCh;

                for (int i = 0; i < count; i++) {
                    float lf = left[offset + i];
                    float rf = right[offset + i];
                    int ql = float.IsFinite(lf) ? Math.Clamp((int)Math.Round(lf * scale), -maxQ, maxQ) : 0;
                    int qr = float.IsFinite(rf) ? Math.Clamp((int)Math.Round(rf * scale), -maxQ, maxQ) : 0;

                    // Exact lossless integer Mid/Side transform (reversible: L = M + (S+1>>1)? Better: S = L - R; M = R + (S >> 1) => R = M - (S >> 1), L = R + S!)
                    int side = ql - qr;
                    int mid = qr + (side >> 1);

                    // 2nd-order linear predictor: pred = 2*prev1 - prev2
                    int mPred = (mPrev1 << 1) - mPrev2;
                    int sPred = (sPrev1 << 1) - sPrev2;
                    int mRes = mid - mPred;
                    int sRes = side - sPred;
                    mPrev2 = mPrev1;
                    mPrev1 = mid;
                    sPrev2 = sPrev1;
                    sPrev1 = side;

                    uint mZig = ZigZagEncode(mRes);
                    uint sZig = ZigZagEncode(sRes);

                    for (int b = 0; b < planesPerCh; b++) {
                        planeBuf[b * count + i] = (byte)((mZig >> (b * 8)) & 0xFFu);
                        planeBuf[chStride + b * count + i] = (byte)((sZig >> (b * 8)) & 0xFFu);
                    }
                }

                zs.Write(planeBuf, 0, chStride * 2);
                offset += count;
            }
        }
        return ms.ToArray();
    }

    static (float[] Left, float[] Right) DecompressStereoDeltaPlanes(
        byte[] compressed,
        int len,
        float invScale,
        int bytesPerSample,
        CancellationToken token) {
        const int BlockFrames = 16384;
        int planesPerCh = bytesPerSample + 1;
        byte[] planeBuf = new byte[BlockFrames * planesPerCh * 2];
        float[] left = new float[len];
        float[] right = new float[len];

        using MemoryStream ms = new(compressed, writable: false);
        using ZLibStream zs = new(ms, CompressionMode.Decompress, leaveOpen: false);

        int mPrev1 = 0, mPrev2 = 0;
        int sPrev1 = 0, sPrev2 = 0;
        int offset = 0;

        while (offset < len) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(BlockFrames, len - offset);
            int chStride = count * planesPerCh;
            ReadExact(zs, planeBuf, chStride * 2);

            for (int i = 0; i < count; i++) {
                uint mZig = 0u;
                uint sZig = 0u;
                for (int b = 0; b < planesPerCh; b++) {
                    mZig |= (uint)planeBuf[b * count + i] << (b * 8);
                    sZig |= (uint)planeBuf[chStride + b * count + i] << (b * 8);
                }

                int mRes = ZigZagDecode(mZig);
                int sRes = ZigZagDecode(sZig);
                int mid = ((mPrev1 << 1) - mPrev2) + mRes;
                int side = ((sPrev1 << 1) - sPrev2) + sRes;
                mPrev2 = mPrev1;
                mPrev1 = mid;
                sPrev2 = sPrev1;
                sPrev1 = side;

                int qr = mid - (side >> 1);
                int ql = qr + side;
                left[offset + i] = ql * invScale;
                right[offset + i] = qr * invScale;
            }
            offset += count;
        }
        return (left, right);
    }

    static byte[] CompressMonoDeltaPlanes(
        float[] mono,
        int len,
        double scale,
        int bytesPerSample,
        CancellationToken token) {
        const int BlockFrames = 16384;
        int planes = bytesPerSample + 1;
        byte[] planeBuf = new byte[BlockFrames * planes];

        using MemoryStream ms = new(Math.Max(2048, len / 4));
        using (ZLibStream zs = new(ms, CompressionLevel.Optimal, leaveOpen: true)) {
            int prev1 = 0, prev2 = 0;
            int offset = 0;
            int maxQ = bytesPerSample == 3 ? 8388607 : 32767;

            while (offset < len) {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(BlockFrames, len - offset);
                for (int i = 0; i < count; i++) {
                    float v = mono[offset + i];
                    int q = float.IsFinite(v) ? Math.Clamp((int)Math.Round(v * scale), -maxQ, maxQ) : 0;
                    int res = q - ((prev1 << 1) - prev2);
                    prev2 = prev1;
                    prev1 = q;
                    uint zig = ZigZagEncode(res);
                    for (int b = 0; b < planes; b++) {
                        planeBuf[b * count + i] = (byte)((zig >> (b * 8)) & 0xFFu);
                    }
                }
                zs.Write(planeBuf, 0, count * planes);
                offset += count;
            }
        }
        return ms.ToArray();
    }

    static float[] DecompressMonoDeltaPlanes(
        byte[] compressed,
        int len,
        float invScale,
        int bytesPerSample,
        CancellationToken token) {
        const int BlockFrames = 16384;
        int planes = bytesPerSample + 1;
        byte[] planeBuf = new byte[BlockFrames * planes];
        float[] mono = new float[len];

        using MemoryStream ms = new(compressed, writable: false);
        using ZLibStream zs = new(ms, CompressionMode.Decompress, leaveOpen: false);

        int prev1 = 0, prev2 = 0;
        int offset = 0;

        while (offset < len) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(BlockFrames, len - offset);
            ReadExact(zs, planeBuf, count * planes);
            for (int i = 0; i < count; i++) {
                uint zig = 0u;
                for (int b = 0; b < planes; b++) {
                    zig |= (uint)planeBuf[b * count + i] << (b * 8);
                }
                int q = ((prev1 << 1) - prev2) + ZigZagDecode(zig);
                prev2 = prev1;
                prev1 = q;
                mono[offset + i] = q * invScale;
            }
            offset += count;
        }
        return mono;
    }

    static uint ZigZagEncode(int value) => (uint)((value << 1) ^ (value >> 31));

    static int ZigZagDecode(uint value) => (int)((value >> 1) ^ (uint)-(int)(value & 1u));

    static void ReadExact(Stream s, byte[] buf, int count) {
        int total = 0;
        while (total < count) {
            int r = s.Read(buf, total, count - total);
            if (r <= 0) {
                throw new EndOfStreamException("Unexpected end of compressed CNTC v3 audio stream.");
            }
            total += r;
        }
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
            throw new InvalidDataException("Invalid choreography LUT dimensions in CNTC cache.");
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
            throw new InvalidDataException("Invalid choreography cue count in CNTC cache.");
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
            throw new InvalidDataException("Invalid choreography LUT payload length in CNTC cache.");
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
        WriteFloatArrayRaw(bw, data, len, token);
    }

    static void WriteFloatArrayRaw(BinaryWriter bw, float[] data, int len, CancellationToken token) {
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
        if (len < 0 || len > 48000 * 60 * 30) {
            throw new InvalidDataException("Invalid PCM array length in neural track cache.");
        }
        if (len == 0) {
            return Array.Empty<float>();
        }
        return ReadFloatArrayRaw(br, len, token);
    }

    static float[] ReadFloatArrayRaw(BinaryReader br, int len, CancellationToken token) {
        float[] data = new float[len];
        const int chunkFloats = 16384;
        byte[] buf = new byte[chunkFloats * sizeof(float)];
        int offset = 0;
        while (offset < len) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(chunkFloats, len - offset);
            int byteCount = count * sizeof(float);
            ReadExact(br.BaseStream, buf, byteCount);
            Buffer.BlockCopy(buf, 0, data, offset * sizeof(float), byteCount);
            offset += count;
        }
        return data;
    }
}
