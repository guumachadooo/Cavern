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
/// Metadata entry describing an open-source ONNX audio checkpoint curated for on-device Slot 2B inference.
/// </summary>
public sealed class NeuralModelCatalogEntry {
    /// <summary>Stable machine identifier (e.g., "scnet_4stem_mobile", "mbr_karaoke_becruily").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Upstream open-source architecture &amp; repository credit.</summary>
    public string SourceRepository { get; init; } = string.Empty;

    /// <summary>Open-source license identifier (MIT, Apache-2.0).</summary>
    public string License { get; init; } = "MIT";

    /// <summary>Task classification for this checkpoint.</summary>
    public NeuralModelTask Task { get; init; }

    /// <summary>Expected ONNX file name under <c>FilesDir/neural_models/</c>.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Direct download URL for pre-exported ONNX weights (HuggingFace / GitHub release).</summary>
    public string DownloadUrl { get; init; } = string.Empty;

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

    /// <summary>Default Codec Restorer model identifier (<c>Apollo Band-Split</c>).</summary>
    public const string DefaultRestorerModelId = "apollo_codec_restore";

    /// <summary>All curated open-source ONNX checkpoints registered in Slot 2B.</summary>
    public static IReadOnlyList<NeuralModelCatalogEntry> Entries { get; } = [
        new NeuralModelCatalogEntry {
            Id = DefaultStemModelId,
            DisplayName = "SCNet 4-Stem (Fast Mobile)",
            SourceRepository = "starrytong/SCNet + ZFTurbo/MSST",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation4,
            FileName = "scnet_4stem_fp16.onnx",
            DownloadUrl = "https://huggingface.co/ZFTurbo/Music-Source-Separation-Training/resolve/main/scnet_4stem_fp16.onnx",
            ApproxSizeMb = 42f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 131072,
            OutputStemNames = ["drums", "bass", "other", "vocals"],
            IsMobileFast = true
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_vocals_studio",
            DisplayName = "Mel-Band RoFormer Vocals (Studio)",
            SourceRepository = "ZFTurbo/MSST + santiquiroz/port-bs-roformer-onnx (KimberleyJSN)",
            License = "MIT",
            Task = NeuralModelTask.VocalSeparation,
            FileName = "mel_band_roformer_vocals_kim.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/mel_band_roformer_vocals_kim.onnx",
            ApproxSizeMb = 185f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["vocals"],
            IsMobileFast = false
        },
        new NeuralModelCatalogEntry {
            Id = "bs_roformer_6stem_studio",
            DisplayName = "BS-RoFormer 6-Stem (Studio)",
            SourceRepository = "ZFTurbo/MSST + santiquiroz/port-bs-roformer-onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation6,
            FileName = "bs_roformer_6stem.onnx",
            DownloadUrl = "https://huggingface.co/santiquiroz/port-bs-roformer-onnx/resolve/main/bs_roformer_6stem.onnx",
            ApproxSizeMb = 215f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["vocals", "drums", "bass", "guitar", "piano", "other"],
            IsMobileFast = false
        },
        new NeuralModelCatalogEntry {
            Id = DefaultKaraokeModelId,
            DisplayName = "Mel-Band RoFormer Karaoke (Lead vs Backing)",
            SourceRepository = "becruily/mel-band-roformer-karaoke + aufr33/viperx (ONNX)",
            License = "MIT",
            Task = NeuralModelTask.KaraokeLeadBacking,
            FileName = "mel_band_roformer_karaoke_lead_backing.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/mel-band-roformer-karaoke-webgpu/resolve/main/model.onnx",
            ApproxSizeMb = 178f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["lead_vocal", "backing_vocals"],
            IsMobileFast = false
        },
        new NeuralModelCatalogEntry {
            Id = DefaultDereverbModelId,
            DisplayName = "Mel-Band RoFormer Dereverb (Dry / Wet RIR)",
            SourceRepository = "Sucial/Dereverb-Echo_Mel_Band_Roformer + ZFTurbo/MSST",
            License = "MIT",
            Task = NeuralModelTask.Dereverb,
            FileName = "mel_band_roformer_dereverb_echo.onnx",
            DownloadUrl = "https://huggingface.co/ZFTurbo/Music-Source-Separation-Training/resolve/main/mel_band_roformer_dereverb_echo.onnx",
            ApproxSizeMb = 172f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["dry_vocal"],
            IsMobileFast = false
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_debleed_denoise",
            DisplayName = "Mel-Band RoFormer Debleed / Denoise",
            SourceRepository = "ZFTurbo/Music-Source-Separation-Training",
            License = "MIT",
            Task = NeuralModelTask.DebleedDenoise,
            FileName = "mel_band_roformer_debleed.onnx",
            DownloadUrl = "https://huggingface.co/ZFTurbo/Music-Source-Separation-Training/resolve/main/mel_band_roformer_debleed.onnx",
            ApproxSizeMb = 168f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["clean"],
            IsMobileFast = false
        },
        new NeuralModelCatalogEntry {
            Id = DefaultRestorerModelId,
            DisplayName = "Apollo Band-Split Codec Restorer",
            SourceRepository = "JusperLee/Apollo + ZFTurbo/MSST",
            License = "MIT",
            Task = NeuralModelTask.CodecRestoreApollo,
            FileName = "apollo_band_split_44k.onnx",
            DownloadUrl = "https://huggingface.co/JusperLee/Apollo/resolve/main/apollo_band_split_44k.onnx",
            ApproxSizeMb = 76f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 262144,
            OutputStemNames = ["restored"],
            IsMobileFast = true
        },
        new NeuralModelCatalogEntry {
            Id = "ap_bwe_flashsr_48k",
            DisplayName = "AP-BWE / FlashSR 48 kHz Bandwidth Extension",
            SourceRepository = "yxlu-0102/AP-BWE + ysharma3501/FlashSR",
            License = "Apache-2.0",
            Task = NeuralModelTask.BandwidthExtension,
            FileName = "ap_bwe_48k.onnx",
            DownloadUrl = "https://huggingface.co/ysharma3501/FlashSR/resolve/main/ap_bwe_48k.onnx",
            ApproxSizeMb = 28f,
            ExpectedSampleRate = 48000,
            RecommendedChunkSamples = 192000,
            OutputStemNames = ["extended_48k"],
            IsMobileFast = true
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

    /// <summary>Builds a human-readable attribution notice covering ONNX Runtime and all curated Slot 2B model repositories.</summary>
    public static string BuildAttributionNotice() =>
        "Slot 2B On-Device Neural Audio & MVSEP Pipeline Licenses:\n\n" +
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
/// Immutable configuration snapshot for Slot 2B (Neural Audio Runtime, MVSEP Anti-Artifact Engine, Spatial Karaoke &amp; Hybrid Neural+DirAC).
/// </summary>
public sealed class NeuralAudioConfig : IEquatable<NeuralAudioConfig> {
    /// <summary>Neutral default configuration: bit-transparent (Karaoke OFF, 100% vocal level, MVSEP 4x overlap + PhaseFix + Conservation armed).</summary>
    public static NeuralAudioConfig Default { get; } = new();

    /// <summary>Preferred ONNX Runtime Execution Provider (Auto: QNN HTP -> XNNPACK -> CPU).</summary>
    public NeuralExecutionProvider PreferredProvider { get; init; } = NeuralExecutionProvider.Auto;

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

    /// <summary>Selected stem separator model ID from <see cref="NeuralModelCatalog"/>.</summary>
    public string ActiveStemModelId { get; init; } = NeuralModelCatalog.DefaultStemModelId;

    /// <summary>Selected karaoke Lead-vs-Backing model ID from <see cref="NeuralModelCatalog"/>.</summary>
    public string ActiveKaraokeModelId { get; init; } = NeuralModelCatalog.DefaultKaraokeModelId;

    /// <summary>Selected dereverb model ID from <see cref="NeuralModelCatalog"/>.</summary>
    public string ActiveDereverbModelId { get; init; } = NeuralModelCatalog.DefaultDereverbModelId;

    /// <summary>Selected codec restorer / BWE model ID from <see cref="NeuralModelCatalog"/>.</summary>
    public string ActiveRestorerModelId { get; init; } = NeuralModelCatalog.DefaultRestorerModelId;

    /// <summary>Returns a clamped, validated copy of this configuration.</summary>
    public NeuralAudioConfig Sanitize() => new() {
        PreferredProvider = Enum.IsDefined(PreferredProvider) ? PreferredProvider : NeuralExecutionProvider.Auto,
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
        ActiveStemModelId = string.IsNullOrWhiteSpace(ActiveStemModelId) ? NeuralModelCatalog.DefaultStemModelId : ActiveStemModelId.Trim(),
        ActiveKaraokeModelId = string.IsNullOrWhiteSpace(ActiveKaraokeModelId) ? NeuralModelCatalog.DefaultKaraokeModelId : ActiveKaraokeModelId.Trim(),
        ActiveDereverbModelId = string.IsNullOrWhiteSpace(ActiveDereverbModelId) ? NeuralModelCatalog.DefaultDereverbModelId : ActiveDereverbModelId.Trim(),
        ActiveRestorerModelId = string.IsNullOrWhiteSpace(ActiveRestorerModelId) ? NeuralModelCatalog.DefaultRestorerModelId : ActiveRestorerModelId.Trim()
    };

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
        return PreferredProvider == other.PreferredProvider
            && OverlapFactor == other.OverlapFactor
            && EnableTta == other.EnableTta
            && EnsembleMode == other.EnsembleMode
            && EnablePhaseFix == other.EnablePhaseFix
            && EnforceEnergyConservation == other.EnforceEnergyConservation
            && SpatialKaraokeEnabled == other.SpatialKaraokeEnabled
            && MathF.Abs(KaraokeVocalLevel - other.KaraokeVocalLevel) < 0.002f
            && KaraokePreserveBackingVocals == other.KaraokePreserveBackingVocals
            && UseLosslessTwinForKaraoke == other.UseLosslessTwinForKaraoke
            && SpatialUpmixMode == other.SpatialUpmixMode
            && string.Equals(ActiveStemModelId, other.ActiveStemModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveKaraokeModelId, other.ActiveKaraokeModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveDereverbModelId, other.ActiveDereverbModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ActiveRestorerModelId, other.ActiveRestorerModelId, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is NeuralAudioConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() {
        HashCode hc = new();
        hc.Add(PreferredProvider);
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
        hc.Add(ActiveStemModelId);
        hc.Add(ActiveKaraokeModelId);
        hc.Add(ActiveDereverbModelId);
        hc.Add(ActiveRestorerModelId);
        return hc.ToHashCode();
    }
}

/// <summary>
/// Live telemetry snapshot for Slot 2B Neural Audio Runtime, MVSEP Pipeline, and Spatial Karaoke.
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

    /// <summary>Number of ONNX audio checkpoints installed locally in <c>FilesDir/neural_models/</c>.</summary>
    public int InstalledModelCount { get; set; }

    /// <summary>Total disk space used by downloaded models &amp; temporary inference cache in bytes.</summary>
    public long CacheSizeBytes { get; set; }

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
/// real-time Spatial Karaoke projection and 3D object placement inside <see cref="SpatialSceneHub"/>.
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
}
