using Cavern.Neural;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Hardware execution provider preference for the managed ARM64 ONNX Runtime in <c>FilesDir/bin/onnxruntime/</c>.
/// </summary>
public enum NeuralExecutionProvider {
    /// <summary>Automatic priority cascade: Snapdragon QNN HTP NPU -> XNNPACK ARM64 SIMD -> CPU fallback.</summary>
    Auto = 0,

    /// <summary>Qualcomm Snapdragon QNN Hexagon Tensor Processor (HTP NPU) with XNNPACK/CPU fallback.</summary>
    QnnHtp = 1,

    /// <summary>ARM64 NEON/FP16 XNNPACK Execution Provider with CPU fallback.</summary>
    Xnnpack = 2,

    /// <summary>Standard ONNX Runtime CPU Execution Provider.</summary>
    Cpu = 3
}

/// <summary>
/// Functional specialization of a registered ONNX audio model in Slot 2B.
/// </summary>
public enum NeuralModelTask {
    /// <summary>4-stem music source separation (Vocals, Drums, Bass, Other) — fast mobile (<c>SCNet</c>).</summary>
    StemSeparation4 = 0,

    /// <summary>6-stem studio separation (Vocals, Drums, Bass, Guitar, Piano, Other) — <c>BS-RoFormer</c>.</summary>
    StemSeparation6 = 1,

    /// <summary>High-SDR vocal vs. instrumental separation — <c>Mel-Band RoFormer</c>.</summary>
    VocalSeparation = 2,

    /// <summary>Karaoke Lead-vs-Backing vocal separation (attenuates lead vocal 0–100% while keeping backing harmonies).</summary>
    KaraokeLeadBacking = 3,

    /// <summary>Vocal dereverb / de-echo dry-wet separation (feeds W11 Vocal Mix-Match RIR/DRR analysis).</summary>
    Dereverb = 4,

    /// <summary>Optional stem bleed &amp; noise cleanup (<c>Mel-Band RoFormer Debleed/Denoise</c>).</summary>
    DebleedDenoise = 5,

    /// <summary>Band-split lossy codec artifact restoration (<c>Apollo</c> for MP3/AAC/E-AC-3 cleanup &amp; W09 residuals).</summary>
    CodecRestoreApollo = 6,

    /// <summary>High-frequency audio bandwidth extension (<c>AP-BWE</c> / <c>FlashSR</c> 16/24 kHz -> 48 kHz).</summary>
    BandwidthExtension = 7
}

/// <summary>
/// Orchestration mode for Slot 7A (<see cref="NeuralPipelineOrchestrator"/>).
/// </summary>
public enum NeuralOrchestrationMode {
    /// <summary>Automatically selects, ensembles, and chains installed ONNX checkpoints per task based on quality &amp; priority.</summary>
    Auto = 0,

    /// <summary>Strictly uses only the explicit user-configured model IDs stored in <see cref="NeuralAudioConfig.TaskModelSlots"/>.</summary>
    Manual = 1
}

/// <summary>
/// High-level pipeline goal resolved by Slot 7A (<see cref="NeuralPipelineOrchestrator"/>).
/// </summary>
public enum NeuralPipelineGoal {
    /// <summary>Full music stem separation (4/6-stem or vocal separation + optional Debleed/Denoise).</summary>
    StemSeparation = 0,

    /// <summary>Zero-phase Spatial Karaoke preparation (Lead vs. Backing split or Vocal separation + optional Dereverb).</summary>
    SpatialKaraoke = 1,

    /// <summary>Vocal Mix-Match DNA extraction (Isolated Vocal + Dereverb dry/wet RIR/DRR analysis).</summary>
    VocalMixDna = 2,

    /// <summary>Lossy codec artifact restoration &amp; bandwidth extension (Apollo + optional AP-BWE/FlashSR).</summary>
    LosslessResidualRestore = 3
}

/// <summary>
/// Spatial playback placement mode for Slot 2B neural stems.
/// </summary>
public enum NeuralSpatialUpmixMode {
    /// <summary>Neural spatial stem upmixing bypassed.</summary>
    Off = 0,

    /// <summary>Places separated stems directly as discrete 3D objects (<c>SourceLayer.NeuralStem</c>) in <see cref="SpatialSceneHub"/>.</summary>
    Stems3DObjects = 1,

    /// <summary>Hybrid Neural+DirAC: anchors Vocals/Bass/Drums on front stage while expanding harmonic stems into 7.1.4 + Heights via DirAC/COMPASS.</summary>
    HybridNeuralDirAc = 2
}

/// <summary>
/// Forward-compatible Slot 1 contract implemented by W09 (<c>Universal Lossless Twin Service</c>).
/// Allows Slot 2B Spatial Karaoke, Neural Stems, and Slot 6B Spatial Dialog to auto-accelerate and purify masks
/// whenever a verified, sample-aligned lossless FLAC/WAV twin is paired with an Atmos/lossy track.
/// </summary>
public interface ILosslessReferenceProvider {
    /// <summary>True when a verified, sample-synchronized lossless FLAC/WAV twin is actively paired.</summary>
    bool HasSyncedLosslessTwin { get; }

    /// <summary>Local filesystem path of the paired lossless twin file.</summary>
    string TwinFilePath { get; }

    /// <summary>Native sample rate of the aligned lossless reference buffer (typically 48000 Hz).</summary>
    int SampleRate { get; }

    /// <summary>Estimated sub-sample alignment confidence in [0; 1] (GCC-PHAT peak ratio).</summary>
    float SyncConfidence { get; }

    /// <summary>
    /// Reads <paramref name="frameCount"/> sample-aligned lossless stereo frames matching <paramref name="trackSamplePosition"/>.
    /// Must avoid heap allocations during real-time playback.
    /// </summary>
    bool TryReadAlignedStereoBlock(long trackSamplePosition, float[] outLeft, float[] outRight, int frameCount);
}

/// <summary>
/// Metadata entry describing an open-source or user-imported ONNX audio checkpoint registered in Slot 7A.
/// </summary>
public sealed class NeuralModelCatalogEntry {
    /// <summary>Stable machine identifier (e.g., "scnet_4stem_mobile", "mbr_karaoke_becruily", "custom_my_roformer").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Upstream open-source architecture &amp; repository credit.</summary>
    public string SourceRepository { get; init; } = string.Empty;

    /// <summary>Open-source license identifier (MIT, Apache-2.0, Custom).</summary>
    public string License { get; init; } = "MIT";

    /// <summary>Task classification for this checkpoint.</summary>
    public NeuralModelTask Task { get; init; }

    /// <summary>Expected ONNX file name under <c>FilesDir/neural_models/</c>.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Direct download URL for pre-exported ONNX weights (HuggingFace / GitHub release), or empty for manual import.</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>Optional ordered mirror/fallback ONNX download URLs tried if <see cref="DownloadUrl"/> returns 404/network error.</summary>
    public string[] FallbackDownloadUrls { get; init; } = Array.Empty<string>();

    /// <summary>True when no verified pre-exported community ONNX mirror is guaranteed and local <c>.onnx</c> import is the primary path.</summary>
    public bool RequiresManualOnnxImport { get; init; }

    /// <summary>Approximate download size in megabytes for user display before lazy download.</summary>
    public float ApproxSizeMb { get; init; }

    /// <summary>Expected model sample rate in Hz (44100 or 48000).</summary>
    public int ExpectedSampleRate { get; init; } = 44100;

    /// <summary>Recommended chunk length in samples per channel for MVSEP overlap-add.</summary>
    public int RecommendedChunkSamples { get; init; } = 131072;

    /// <summary>Ordered stem names produced by this model checkpoint.</summary>
    public string[] OutputStemNames { get; init; } = Array.Empty<string>();

    /// <summary>True if this checkpoint is optimized for fast ARM64 mobile inference.</summary>
    public bool IsMobileFast { get; init; }

    /// <summary>Priority ranking used by <see cref="NeuralPipelineOrchestrator"/> in <c>Auto</c> mode (higher = preferred).</summary>
    public int PriorityRank { get; init; } = 100;

    /// <summary>True when this descriptor represents a user-imported standalone custom <c>.onnx</c> model.</summary>
    public bool IsCustomImported { get; init; }
}

/// <summary>
/// Curated multi-repository registry of open-source ONNX audio separation, karaoke, dereverb, debleed, and restoration models.
/// Zero weights are bundled in the APK — every checkpoint is lazily downloaded or imported by the user into <c>FilesDir/neural_models/</c>.
/// </summary>
public static class NeuralModelCatalog {
    /// <summary>Default fast mobile 4-stem model identifier (<c>SCNet</c>).</summary>
    public const string DefaultStemModelId = "scnet_4stem_mobile";

    /// <summary>Default Karaoke Lead-vs-Backing model identifier (<c>Mel-Band RoFormer Karaoke</c>).</summary>
    public const string DefaultKaraokeModelId = "mbr_karaoke_lead_backing";

    /// <summary>Default Dereverb model identifier (<c>Mel-Band RoFormer Dereverb</c>).</summary>
    public const string DefaultDereverbModelId = "mbr_dereverb_echo";

    /// <summary>Default Debleed/Denoise model identifier (<c>Mel-Band RoFormer Debleed</c>).</summary>
    public const string DefaultDebleedModelId = "mbr_debleed_denoise";

    /// <summary>Default Codec Restorer model identifier (<c>Apollo Band-Split</c>).</summary>
    public const string DefaultRestorerModelId = "apollo_codec_restore";

    /// <summary>Default Bandwidth Extension model identifier (<c>AP-BWE / FlashSR 48 kHz</c>).</summary>
    public const string DefaultBweModelId = "ap_bwe_flashsr_48k";

    /// <summary>All curated open-source ONNX checkpoints registered in Slot 7A.</summary>
    public static IReadOnlyList<NeuralModelCatalogEntry> Entries { get; } = [
        new NeuralModelCatalogEntry {
            Id = DefaultStemModelId,
            DisplayName = "SCNet 4-Stem (Fast Mobile)",
            SourceRepository = "starrytong/SCNet + ZFTurbo/MSST (ONNX)",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation4,
            FileName = "scnet_4stem_fp16.onnx",
            DownloadUrl = "https://huggingface.co/onnx-community/scnet-4stem-onnx/resolve/main/model_fp16.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/ZFTurbo/Music-Source-Separation-Training/resolve/main/scnet_4stem_fp16.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 42f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 131072,
            OutputStemNames = ["drums", "bass", "other", "vocals"],
            IsMobileFast = true,
            PriorityRank = 110
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_vocals_studio",
            DisplayName = "Mel-Band RoFormer Vocals (Studio)",
            SourceRepository = "ZFTurbo/MSST + santiquiroz/port-bs-roformer-onnx (KimberleyJSN)",
            License = "MIT",
            Task = NeuralModelTask.VocalSeparation,
            FileName = "mel_band_roformer_vocals_kim.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/mel_band_roformer_vocals_kim.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/onnx-community/mel-band-roformer-vocals-onnx/resolve/main/model.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 185f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["vocals"],
            IsMobileFast = false,
            PriorityRank = 140
        },
        new NeuralModelCatalogEntry {
            Id = "bs_roformer_6stem_studio",
            DisplayName = "BS-RoFormer 6-Stem (Studio)",
            SourceRepository = "ZFTurbo/MSST + santiquiroz/port-bs-roformer-onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation6,
            FileName = "bs_roformer_6stem.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/bs_roformer_6stem.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/onnx-community/bs-roformer-6stem-onnx/resolve/main/model.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 215f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["vocals", "drums", "bass", "guitar", "piano", "other"],
            IsMobileFast = false,
            PriorityRank = 160
        },
        new NeuralModelCatalogEntry {
            Id = DefaultKaraokeModelId,
            DisplayName = "Mel-Band RoFormer Karaoke (Lead vs Backing)",
            SourceRepository = "becruily/mel-band-roformer-karaoke + Acapellas ONNX",
            License = "MIT",
            Task = NeuralModelTask.KaraokeLeadBacking,
            FileName = "mel_band_roformer_karaoke_lead_backing.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/mel-band-roformer-karaoke-webgpu/resolve/main/model.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/mel_band_roformer_karaoke.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 178f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["lead_vocal", "backing_vocals"],
            IsMobileFast = false,
            PriorityRank = 150
        },
        new NeuralModelCatalogEntry {
            Id = DefaultDereverbModelId,
            DisplayName = "Mel-Band RoFormer Dereverb (Dry / Wet RIR)",
            SourceRepository = "Sucial/Dereverb-Echo_Mel_Band_Roformer + ZFTurbo/MSST",
            License = "MIT",
            Task = NeuralModelTask.Dereverb,
            FileName = "mel_band_roformer_dereverb_echo.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/mel_band_roformer_dereverb_echo.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/onnx-community/mel-band-roformer-dereverb-onnx/resolve/main/model.onnx"
            ],
            RequiresManualOnnxImport = true,
            ApproxSizeMb = 172f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["dry_vocal"],
            IsMobileFast = false,
            PriorityRank = 130
        },
        new NeuralModelCatalogEntry {
            Id = DefaultDebleedModelId,
            DisplayName = "Mel-Band RoFormer Debleed / Denoise",
            SourceRepository = "ZFTurbo/Music-Source-Separation-Training",
            License = "MIT",
            Task = NeuralModelTask.DebleedDenoise,
            FileName = "mel_band_roformer_debleed.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/mel_band_roformer_debleed.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/onnx-community/mel-band-roformer-debleed-onnx/resolve/main/model.onnx"
            ],
            RequiresManualOnnxImport = true,
            ApproxSizeMb = 168f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["clean"],
            IsMobileFast = false,
            PriorityRank = 120
        },
        new NeuralModelCatalogEntry {
            Id = DefaultRestorerModelId,
            DisplayName = "Apollo Band-Split Codec Restorer",
            SourceRepository = "JusperLee/Apollo + ZFTurbo/MSST",
            License = "MIT",
            Task = NeuralModelTask.CodecRestoreApollo,
            FileName = "apollo_band_split_44k.onnx",
            DownloadUrl = "https://huggingface.co/onnx-community/apollo-audio-restore-onnx/resolve/main/model.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/JusperLee/Apollo/resolve/main/apollo_band_split_44k.onnx"
            ],
            RequiresManualOnnxImport = true,
            ApproxSizeMb = 76f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 262144,
            OutputStemNames = ["restored"],
            IsMobileFast = true,
            PriorityRank = 135
        },
        new NeuralModelCatalogEntry {
            Id = DefaultBweModelId,
            DisplayName = "AP-BWE / FlashSR 48 kHz Bandwidth Extension",
            SourceRepository = "yxlu-0102/AP-BWE + ysharma3501/FlashSR",
            License = "Apache-2.0",
            Task = NeuralModelTask.BandwidthExtension,
            FileName = "ap_bwe_48k.onnx",
            DownloadUrl = "https://huggingface.co/onnx-community/flashsr-bwe-48k-onnx/resolve/main/model.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/ysharma3501/FlashSR/resolve/main/ap_bwe_48k.onnx"
            ],
            RequiresManualOnnxImport = true,
            ApproxSizeMb = 28f,
            ExpectedSampleRate = 48000,
            RecommendedChunkSamples = 192000,
            OutputStemNames = ["extended_48k"],
            IsMobileFast = true,
            PriorityRank = 115
        }
    ];

    /// <summary>Finds a registered model entry by its stable identifier (case-insensitive).</summary>
    public static NeuralModelCatalogEntry FindById(string id) {
        if (string.IsNullOrWhiteSpace(id)) {
            return null;
        }
        for (int i = 0; i < Entries.Count; i++) {
            if (string.Equals(Entries[i].Id, id, StringComparison.OrdinalIgnoreCase)) {
                return Entries[i];
            }
        }
        return null;
    }

    /// <summary>Returns all curated catalog entries matching <paramref name="task"/> ordered by descending <see cref="NeuralModelCatalogEntry.PriorityRank"/>.</summary>
    public static IReadOnlyList<NeuralModelCatalogEntry> FindByTask(NeuralModelTask task) {
        List<NeuralModelCatalogEntry> matches = [];
        for (int i = 0; i < Entries.Count; i++) {
            if (Entries[i].Task == task) {
                matches.Add(Entries[i]);
            }
        }
        matches.Sort((a, b) => b.PriorityRank.CompareTo(a.PriorityRank));
        return matches;
    }

    /// <summary>Returns canonical default output stem names for a <see cref="NeuralModelTask"/> when importing a custom <c>.onnx</c> model.</summary>
    public static string[] GetDefaultOutputStemNames(NeuralModelTask task) => task switch {
        NeuralModelTask.StemSeparation4 => ["drums", "bass", "other", "vocals"],
        NeuralModelTask.StemSeparation6 => ["vocals", "drums", "bass", "guitar", "piano", "other"],
        NeuralModelTask.VocalSeparation => ["vocals"],
        NeuralModelTask.KaraokeLeadBacking => ["lead_vocal", "backing_vocals"],
        NeuralModelTask.Dereverb => ["dry_vocal"],
        NeuralModelTask.DebleedDenoise => ["clean"],
        NeuralModelTask.CodecRestoreApollo => ["restored"],
        NeuralModelTask.BandwidthExtension => ["extended_48k"],
        _ => ["stem_0"]
    };

    /// <summary>Builds a first-class <see cref="NeuralModelCatalogEntry"/> descriptor for a user-imported custom <c>.onnx</c> checkpoint.</summary>
    public static NeuralModelCatalogEntry CreateCustomEntry(
        string id,
        string displayName,
        NeuralModelTask task,
        string fileName,
        int expectedSampleRate = 44100,
        int recommendedChunkSamples = 131072,
        string[] outputStemNames = null,
        float approxSizeMb = 0f) => new() {
            Id = string.IsNullOrWhiteSpace(id) ? $"custom_{Guid.NewGuid():N}" : id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(fileName ?? "Custom ONNX") : displayName.Trim(),
            SourceRepository = "Local Custom ONNX",
            License = "Custom",
            Task = task,
            FileName = string.IsNullOrWhiteSpace(fileName) ? $"{id}.onnx" : fileName.Trim(),
            DownloadUrl = string.Empty,
            FallbackDownloadUrls = Array.Empty<string>(),
            RequiresManualOnnxImport = true,
            ApproxSizeMb = Math.Max(0f, approxSizeMb),
            ExpectedSampleRate = expectedSampleRate >= 8000 ? expectedSampleRate : (task == NeuralModelTask.BandwidthExtension ? 48000 : 44100),
            RecommendedChunkSamples = Math.Max(4096, recommendedChunkSamples),
            OutputStemNames = outputStemNames != null && outputStemNames.Length > 0 ? outputStemNames : GetDefaultOutputStemNames(task),
            IsMobileFast = approxSizeMb > 0f && approxSizeMb <= 90f,
            PriorityRank = 125,
            IsCustomImported = true
        };

    /// <summary>Builds a human-readable attribution notice covering ONNX Runtime and all curated Slot 7A/7B model repositories.</summary>
    public static string BuildAttributionNotice() =>
        "Slot 7 On-Device Neural Audio & MVSEP Pipeline Licenses:\n\n" +
        "• Microsoft ONNX Runtime (ARM64 XNNPACK / QNN EP) — MIT License (github.com/microsoft/onnxruntime)\n" +
        "• ZFTurbo Music-Source-Separation-Training & MVSEP-MDX23 (Mel-Band/BS-RoFormer, TTA, avg_wave/min_fft) — MIT License\n" +
        "• santiquiroz/port-bs-roformer-onnx — MIT License\n" +
        "• starrytong/SCNet — MIT License\n" +
        "• becruily / aufr33 Mel-Band RoFormer Karaoke — MIT License\n" +
        "• Sucial Dereverb-Echo Mel-Band RoFormer — MIT License\n" +
        "• JusperLee/Apollo Band-Split Restoration — MIT License\n" +
        "• yxlu-0102/AP-BWE & ysharma3501/FlashSR — MIT / Apache-2.0 License";
}

/// <summary>
/// Immutable configuration snapshot for Slot 2B / Slot 7A (Dynamic Neural Task-Slot Registry, Orchestrator, MVSEP Anti-Artifact Engine, Spatial Karaoke &amp; Hybrid Neural+DirAC).
/// </summary>
public sealed class NeuralAudioConfig : IEquatable<NeuralAudioConfig> {
    /// <summary>Neutral default configuration: bit-transparent (Karaoke OFF, 100% vocal level, Auto orchestration, MVSEP 4x overlap + PhaseFix + Conservation armed).</summary>
    public static NeuralAudioConfig Default { get; } = new NeuralAudioConfig().Sanitize();

    /// <summary>Preferred ONNX Runtime Execution Provider (Auto: QNN HTP -> XNNPACK -> CPU).</summary>
    public NeuralExecutionProvider PreferredProvider { get; init; } = NeuralExecutionProvider.Auto;

    /// <summary>Slot 7A orchestration mode (<c>Auto</c> chains best installed checkpoints; <c>Manual</c> uses explicit <see cref="TaskModelSlots"/>).</summary>
    public NeuralOrchestrationMode OrchestrationMode { get; init; } = NeuralOrchestrationMode.Auto;

    /// <summary>MVSEP chunk overlap factor in [2..8] (default 4x = 75% overlap).</summary>
    public int OverlapFactor { get; init; } = MvsepPipelineOptions.DefaultOverlap;

    /// <summary>Enables MVSEP Test-Time Augmentation (L/R swap + polarity inversion averaging).</summary>
    public bool EnableTta { get; init; }

    /// <summary>MVSEP multi-model ensembling mode (<c>avg_wave</c>, <c>min_fft</c>, <c>avg_fft</c>).</summary>
    public MvsepEnsembleMethod EnsembleMode { get; init; } = MvsepEnsembleMethod.AverageWave;

    /// <summary>Enables STFT Wiener original-phase anchoring (<c>phase_fix</c>).</summary>
    public bool EnablePhaseFix { get; init; } = true;

    /// <summary>Enables exact residual energy conservation (<c>residual = mix - sum(stems)</c>).</summary>
    public bool EnforceEnergyConservation { get; init; } = true;

    /// <summary>Enables zero-phase multichannel Spatial Karaoke masking across all Atmos beds &amp; objects.</summary>
    public bool SpatialKaraokeEnabled { get; init; }

    /// <summary>Spatial Karaoke lead vocal level in [0.0; 1.0] (0.0 = instrumental, 1.0 = bit-exact bypass).</summary>
    public float KaraokeVocalLevel { get; init; } = 1f;

    /// <summary>Protects backing vocals &amp; harmonies when attenuating lead vocals.</summary>
    public bool KaraokePreserveBackingVocals { get; init; } = true;

    /// <summary>Automatically uses a synced W09 lossless FLAC twin (when available) to purify &amp; accelerate Spatial Karaoke at 1x cost.</summary>
    public bool UseLosslessTwinForKaraoke { get; init; } = true;

    /// <summary>Spatial 3D placement / Hybrid Neural+DirAC upmix mode for separated stems.</summary>
    public NeuralSpatialUpmixMode SpatialUpmixMode { get; init; } = NeuralSpatialUpmixMode.Off;

    /// <summary>Maximum disk budget in megabytes for Slot 7B <c>CacheDir/neural_tracks/</c> LRU cache.</summary>
    public int TrackCacheMaxSizeMb { get; init; } = 512;

    /// <summary>Extensible task-keyed model registry supporting 1 or more (ensemble) model IDs per <see cref="NeuralModelTask"/>.</summary>
    public IReadOnlyDictionary<NeuralModelTask, IReadOnlyList<string>> TaskModelSlots { get; init; }

    /// <summary>Primary stem separator model ID (synchronized with stem separation entries in <see cref="TaskModelSlots"/>).</summary>
    public string ActiveStemModelId { get; init; } = NeuralModelCatalog.DefaultStemModelId;

    /// <summary>Primary karaoke Lead-vs-Backing model ID (synchronized with <see cref="NeuralModelTask.KaraokeLeadBacking"/>).</summary>
    public string ActiveKaraokeModelId { get; init; } = NeuralModelCatalog.DefaultKaraokeModelId;

    /// <summary>Primary dereverb model ID (synchronized with <see cref="NeuralModelTask.Dereverb"/>).</summary>
    public string ActiveDereverbModelId { get; init; } = NeuralModelCatalog.DefaultDereverbModelId;

    /// <summary>Primary debleed/denoise model ID (synchronized with <see cref="NeuralModelTask.DebleedDenoise"/>).</summary>
    public string ActiveDebleedModelId { get; init; } = NeuralModelCatalog.DefaultDebleedModelId;

    /// <summary>Primary codec restorer / BWE model ID (synchronized with <see cref="NeuralModelTask.CodecRestoreApollo"/> / <see cref="NeuralModelTask.BandwidthExtension"/>).</summary>
    public string ActiveRestorerModelId { get; init; } = NeuralModelCatalog.DefaultRestorerModelId;

    /// <summary>Returns the configured ordered list of model IDs assigned to <paramref name="task"/>.</summary>
    public IReadOnlyList<string> GetModelsForTask(NeuralModelTask task) {
        if (TaskModelSlots != null && TaskModelSlots.TryGetValue(task, out IReadOnlyList<string> configured) && configured != null && configured.Count > 0) {
            return configured;
        }
        string fallback = task switch {
            NeuralModelTask.StemSeparation4 => ActiveStemModelId,
            NeuralModelTask.StemSeparation6 => ActiveStemModelId,
            NeuralModelTask.VocalSeparation => ActiveStemModelId,
            NeuralModelTask.KaraokeLeadBacking => ActiveKaraokeModelId,
            NeuralModelTask.Dereverb => ActiveDereverbModelId,
            NeuralModelTask.DebleedDenoise => ActiveDebleedModelId,
            NeuralModelTask.CodecRestoreApollo => ActiveRestorerModelId,
            NeuralModelTask.BandwidthExtension => ActiveRestorerModelId,
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(fallback) ? Array.Empty<string>() : [fallback.Trim()];
    }

    /// <summary>Returns a copy of this configuration with only <see cref="KaraokeVocalLevel"/> updated, preserving all task slots.</summary>
    public NeuralAudioConfig WithKaraokeVocalLevel(float level01) => new NeuralAudioConfig {
        PreferredProvider = PreferredProvider,
        OrchestrationMode = OrchestrationMode,
        OverlapFactor = OverlapFactor,
        EnableTta = EnableTta,
        EnsembleMode = EnsembleMode,
        EnablePhaseFix = EnablePhaseFix,
        EnforceEnergyConservation = EnforceEnergyConservation,
        SpatialKaraokeEnabled = SpatialKaraokeEnabled,
        KaraokeVocalLevel = Math.Clamp(level01, 0f, 1f),
        KaraokePreserveBackingVocals = KaraokePreserveBackingVocals,
        UseLosslessTwinForKaraoke = UseLosslessTwinForKaraoke,
        SpatialUpmixMode = SpatialUpmixMode,
        TrackCacheMaxSizeMb = TrackCacheMaxSizeMb,
        TaskModelSlots = TaskModelSlots,
        ActiveStemModelId = ActiveStemModelId,
        ActiveKaraokeModelId = ActiveKaraokeModelId,
        ActiveDereverbModelId = ActiveDereverbModelId,
        ActiveDebleedModelId = ActiveDebleedModelId,
        ActiveRestorerModelId = ActiveRestorerModelId
    }.Sanitize();

    /// <summary>Returns a clamped, validated copy of this configuration with synchronized <see cref="TaskModelSlots"/>.</summary>
    public NeuralAudioConfig Sanitize() {
        string stemId = string.IsNullOrWhiteSpace(ActiveStemModelId) ? NeuralModelCatalog.DefaultStemModelId : ActiveStemModelId.Trim();
        string karaokeId = string.IsNullOrWhiteSpace(ActiveKaraokeModelId) ? NeuralModelCatalog.DefaultKaraokeModelId : ActiveKaraokeModelId.Trim();
        string dereverbId = string.IsNullOrWhiteSpace(ActiveDereverbModelId) ? NeuralModelCatalog.DefaultDereverbModelId : ActiveDereverbModelId.Trim();
        string debleedId = string.IsNullOrWhiteSpace(ActiveDebleedModelId) ? NeuralModelCatalog.DefaultDebleedModelId : ActiveDebleedModelId.Trim();
        string restorerId = string.IsNullOrWhiteSpace(ActiveRestorerModelId) ? NeuralModelCatalog.DefaultRestorerModelId : ActiveRestorerModelId.Trim();

        Dictionary<NeuralModelTask, IReadOnlyList<string>> normalizedSlots = [];
        if (TaskModelSlots != null) {
            foreach (KeyValuePair<NeuralModelTask, IReadOnlyList<string>> kv in TaskModelSlots) {
                if (kv.Value == null) {
                    continue;
                }
                List<string> clean = [];
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < kv.Value.Count; i++) {
                    string id = kv.Value[i]?.Trim();
                    if (!string.IsNullOrEmpty(id) && seen.Add(id)) {
                        clean.Add(id);
                    }
                }
                if (clean.Count > 0) {
                    normalizedSlots[kv.Key] = clean;
                }
            }
        }

        // Ensure canonical single-ID properties and dynamic task slots remain synchronized in both directions
        EnsurePrimarySlot(normalizedSlots, ResolveStemTask(stemId), stemId);
        EnsurePrimarySlot(normalizedSlots, NeuralModelTask.KaraokeLeadBacking, karaokeId);
        EnsurePrimarySlot(normalizedSlots, NeuralModelTask.Dereverb, dereverbId);
        EnsurePrimarySlot(normalizedSlots, NeuralModelTask.DebleedDenoise, debleedId);
        EnsurePrimarySlot(normalizedSlots, ResolveRestorerTask(restorerId), restorerId);

        return new NeuralAudioConfig {
            PreferredProvider = Enum.IsDefined(PreferredProvider) ? PreferredProvider : NeuralExecutionProvider.Auto,
            OrchestrationMode = Enum.IsDefined(OrchestrationMode) ? OrchestrationMode : NeuralOrchestrationMode.Auto,
            OverlapFactor = Math.Clamp(OverlapFactor, MvsepPipelineOptions.MinOverlap, MvsepPipelineOptions.MaxOverlap),
            EnableTta = EnableTta,
            EnsembleMode = Enum.IsDefined(EnsembleMode) ? EnsembleMode : MvsepEnsembleMethod.AverageWave,
            EnablePhaseFix = EnablePhaseFix,
            EnforceEnergyConservation = EnforceEnergyConservation,
            SpatialKaraokeEnabled = SpatialKaraokeEnabled,
            KaraokeVocalLevel = Math.Clamp(KaraokeVocalLevel, 0f, 1f),
            KaraokePreserveBackingVocals = KaraokePreserveBackingVocals,
            UseLosslessTwinForKaraoke = UseLosslessTwinForKaraoke,
            SpatialUpmixMode = Enum.IsDefined(SpatialUpmixMode) ? SpatialUpmixMode : NeuralSpatialUpmixMode.Off,
            TrackCacheMaxSizeMb = Math.Clamp(TrackCacheMaxSizeMb, 128, 4096),
            TaskModelSlots = normalizedSlots,
            ActiveStemModelId = stemId,
            ActiveKaraokeModelId = karaokeId,
            ActiveDereverbModelId = dereverbId,
            ActiveDebleedModelId = debleedId,
            ActiveRestorerModelId = restorerId
        };
    }

    static NeuralModelTask ResolveStemTask(string modelId) {
        NeuralModelCatalogEntry e = NeuralModelCatalog.FindById(modelId);
        if (e != null && (e.Task == NeuralModelTask.StemSeparation4 || e.Task == NeuralModelTask.StemSeparation6 || e.Task == NeuralModelTask.VocalSeparation)) {
            return e.Task;
        }
        return NeuralModelTask.StemSeparation4;
    }

    static NeuralModelTask ResolveRestorerTask(string modelId) {
        NeuralModelCatalogEntry e = NeuralModelCatalog.FindById(modelId);
        if (e != null && (e.Task == NeuralModelTask.CodecRestoreApollo || e.Task == NeuralModelTask.BandwidthExtension)) {
            return e.Task;
        }
        return NeuralModelTask.CodecRestoreApollo;
    }

    static void EnsurePrimarySlot(Dictionary<NeuralModelTask, IReadOnlyList<string>> slots, NeuralModelTask task, string primaryId) {
        if (string.IsNullOrWhiteSpace(primaryId)) {
            return;
        }
        if (!slots.TryGetValue(task, out IReadOnlyList<string> existing) || existing == null || existing.Count == 0) {
            slots[task] = [primaryId];
        }
    }

    /// <summary>Builds <see cref="MvsepPipelineOptions"/> from this configuration and a target chunk size.</summary>
    public MvsepPipelineOptions ToMvsepOptions(int chunkSamples = 131072) => new() {
        ChunkSamples = Math.Max(4096, chunkSamples),
        OverlapFactor = Math.Clamp(OverlapFactor, MvsepPipelineOptions.MinOverlap, MvsepPipelineOptions.MaxOverlap),
        EnableTta = EnableTta,
        EnableShiftTta = false,
        EnablePhaseFix = EnablePhaseFix,
        EnforceEnergyConservation = EnforceEnergyConservation
    };

    /// <inheritdoc/>
    public bool Equals(NeuralAudioConfig other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }
        if (other is null) {
            return false;
        }
        if (PreferredProvider != other.PreferredProvider
            || OrchestrationMode != other.OrchestrationMode
            || OverlapFactor != other.OverlapFactor
            || EnableTta != other.EnableTta
            || EnsembleMode != other.EnsembleMode
            || EnablePhaseFix != other.EnablePhaseFix
            || EnforceEnergyConservation != other.EnforceEnergyConservation
            || SpatialKaraokeEnabled != other.SpatialKaraokeEnabled
            || MathF.Abs(KaraokeVocalLevel - other.KaraokeVocalLevel) >= 0.002f
            || KaraokePreserveBackingVocals != other.KaraokePreserveBackingVocals
            || UseLosslessTwinForKaraoke != other.UseLosslessTwinForKaraoke
            || SpatialUpmixMode != other.SpatialUpmixMode
            || TrackCacheMaxSizeMb != other.TrackCacheMaxSizeMb
            || !string.Equals(ActiveStemModelId, other.ActiveStemModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ActiveKaraokeModelId, other.ActiveKaraokeModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ActiveDereverbModelId, other.ActiveDereverbModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ActiveDebleedModelId, other.ActiveDebleedModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ActiveRestorerModelId, other.ActiveRestorerModelId, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        return TaskSlotsEqual(TaskModelSlots, other.TaskModelSlots);
    }

    static bool TaskSlotsEqual(
        IReadOnlyDictionary<NeuralModelTask, IReadOnlyList<string>> a,
        IReadOnlyDictionary<NeuralModelTask, IReadOnlyList<string>> b) {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB) {
            return false;
        }
        if (countA == 0) {
            return true;
        }
        foreach (KeyValuePair<NeuralModelTask, IReadOnlyList<string>> kv in a) {
            if (!b.TryGetValue(kv.Key, out IReadOnlyList<string> listB)) {
                return false;
            }
            IReadOnlyList<string> listA = kv.Value;
            int lenA = listA?.Count ?? 0;
            int lenB = listB?.Count ?? 0;
            if (lenA != lenB) {
                return false;
            }
            for (int i = 0; i < lenA; i++) {
                if (!string.Equals(listA[i], listB[i], StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
            }
        }
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is NeuralAudioConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() {
        HashCode hc = new();
        hc.Add(PreferredProvider);
        hc.Add(OrchestrationMode);
        hc.Add(OverlapFactor);
        hc.Add(EnableTta);
        hc.Add(EnsembleMode);
        hc.Add(EnablePhaseFix);
        hc.Add(EnforceEnergyConservation);
        hc.Add(SpatialKaraokeEnabled);
        hc.Add(MathF.Round(KaraokeVocalLevel, 3));
        hc.Add(KaraokePreserveBackingVocals);
        hc.Add(UseLosslessTwinForKaraoke);
        hc.Add(SpatialUpmixMode);
        hc.Add(TrackCacheMaxSizeMb);
        hc.Add(ActiveStemModelId, StringComparer.OrdinalIgnoreCase);
        hc.Add(ActiveKaraokeModelId, StringComparer.OrdinalIgnoreCase);
        hc.Add(ActiveDereverbModelId, StringComparer.OrdinalIgnoreCase);
        hc.Add(ActiveDebleedModelId, StringComparer.OrdinalIgnoreCase);
        hc.Add(ActiveRestorerModelId, StringComparer.OrdinalIgnoreCase);
        if (TaskModelSlots != null) {
            foreach (NeuralModelTask task in Enum.GetValues<NeuralModelTask>()) {
                if (TaskModelSlots.TryGetValue(task, out IReadOnlyList<string> list) && list != null) {
                    hc.Add(task);
                    for (int i = 0; i < list.Count; i++) {
                        hc.Add(list[i], StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
        }
        return hc.ToHashCode();
    }
}

/// <summary>
/// Live telemetry snapshot for Slot 2B / Slot 7 Neural Audio Runtime, Orchestrator, Track Cache, MVSEP Pipeline, and Spatial Karaoke.
/// Published into <see cref="SpatialSceneHub.NeuralDiagnostics"/>.
/// </summary>
public struct NeuralAudioDiagnostics {
    /// <summary>True when managed ARM64 <c>libonnxruntime.so</c> is installed and verified in <c>FilesDir/bin/onnxruntime/</c>.</summary>
    public bool IsRuntimeInstalled { get; set; }

    /// <summary>Installed ONNX Runtime version string (e.g., "1.22.0").</summary>
    public string RuntimeVersion { get; set; }

    /// <summary>Active Execution Provider label ("QNN HTP NPU", "XNNPACK ARM64", "CPU", or "Not Installed").</summary>
    public string ActiveProviderName { get; set; }

    /// <summary>True when Qualcomm QNN HTP libraries are available alongside ONNX Runtime.</summary>
    public bool IsQnnAvailable { get; set; }

    /// <summary>Number of ONNX audio checkpoints installed locally in <c>FilesDir/neural_models/</c> (curated + custom).</summary>
    public int InstalledModelCount { get; set; }

    /// <summary>Total disk space used by downloaded models &amp; runtime in bytes.</summary>
    public long CacheSizeBytes { get; set; }

    /// <summary>Number of pre-separated tracks stored in Slot 7B disk cache (<c>CacheDir/neural_tracks/</c>).</summary>
    public int TrackCacheEntryCount { get; set; }

    /// <summary>Total disk space used by Slot 7B separated track cache in bytes.</summary>
    public long TrackCacheSizeBytes { get; set; }

    /// <summary>Human-readable summary of the active Slot 7A orchestrator mode &amp; model chain.</summary>
    public string OrchestrationSummary { get; set; }

    /// <summary>True when Spatial Karaoke multichannel masking is actively attenuating vocals.</summary>
    public bool IsKaraokeActive { get; set; }

    /// <summary>Current Karaoke lead vocal level in [0; 1].</summary>
    public float KaraokeVocalLevel { get; set; }

    /// <summary>True when Spatial Karaoke is using a pre-separated neural vocal stem (1x cost across all Atmos channels).</summary>
    public bool HasNeuralVocalReference { get; set; }

    /// <summary>True when a synced W09 lossless FLAC twin is feeding Slot 2B.</summary>
    public bool IsLosslessTwinAccelerated { get; set; }

    /// <summary>Number of separated neural stems currently registered as 3D objects in <see cref="SpatialSceneHub"/>.</summary>
    public int ActiveNeuralStemCount { get; set; }

    /// <summary>Live vocal activity confidence in [0; 1].</summary>
    public float VocalActivity { get; set; }

    /// <summary>Latest measured Dereverb Direct-to-Reverberant Ratio in dB (NaN when unmeasured; feeds W11 Vocal Mix-Match).</summary>
    public float LastDereverbDrrDb { get; set; }

    /// <summary>Latest measured ONNX inference Real-Time Factor (processing_sec / audio_sec; &lt; 1.0 = faster than real time).</summary>
    public float LastInferenceRtf { get; set; }

    /// <summary>Returns a clean default diagnostic snapshot.</summary>
    public static NeuralAudioDiagnostics Default => new() {
        IsRuntimeInstalled = false,
        RuntimeVersion = string.Empty,
        ActiveProviderName = "Not Installed",
        IsQnnAvailable = false,
        InstalledModelCount = 0,
        CacheSizeBytes = 0L,
        TrackCacheEntryCount = 0,
        TrackCacheSizeBytes = 0L,
        OrchestrationSummary = string.Empty,
        IsKaraokeActive = false,
        KaraokeVocalLevel = 1f,
        HasNeuralVocalReference = false,
        IsLosslessTwinAccelerated = false,
        ActiveNeuralStemCount = 0,
        VocalActivity = 0f,
        LastDereverbDrrDb = float.NaN,
        LastInferenceRtf = 0f
    };
}

/// <summary>
/// Holds a track's pre-separated neural stems (and optional mono Lead/Backing karaoke references) for
/// real-time Spatial Karaoke projection, 3D object placement inside <see cref="SpatialSceneHub"/>, and Slot 7B disk caching.
/// </summary>
public sealed class NeuralSeparatedTrack {
    /// <summary>Track path or identifier this stem set belongs to.</summary>
    public string SourceTrackKey { get; init; } = string.Empty;

    /// <summary>Sample rate of the separated buffers in Hz.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Separated stereo stems (including conserved residual when enabled).</summary>
    public IReadOnlyList<StereoStemBuffer> Stems { get; init; } = Array.Empty<StereoStemBuffer>();

    /// <summary>Optional mono Lead Vocal reference (downmixed from neural Lead Vocal stem) for zero-phase multichannel Karaoke.</summary>
    public float[] MonoLeadVocalRef { get; init; }

    /// <summary>Optional mono Backing Vocal reference for Lead-only Karaoke protection.</summary>
    public float[] MonoBackingVocalRef { get; init; }

    /// <summary>Optional Dereverb dry/wet analysis result (reusable by W11 Vocal Mix-Match).</summary>
    public DereverbDecompositionResult DereverbAnalysis { get; init; }

    /// <summary>True when generated from a synced W09 lossless FLAC twin.</summary>
    public bool DerivedFromLosslessTwin { get; init; }

    /// <summary>Slot 7A orchestrator plan signature used to generate this stem bundle (for cache validation).</summary>
    public string PlanSignature { get; init; } = string.Empty;
}
