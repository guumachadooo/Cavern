using Cavern.Neural;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Hardware execution provider preference for the managed ARM64 ONNX Runtime in <c>NativeLibraryDir</c>.
/// </summary>
public enum NeuralExecutionProvider {
    /// <summary>Automatic smart cascade: WebGPU Vulkan GPU (for GPU-optimal CNN/MDX models) -> QNN HTP NPU (if bundled) -> CPU ARM64 KleidiAI SIMD.</summary>
    Auto = 0,

    /// <summary>Qualcomm Snapdragon QNN Hexagon Tensor Processor (HTP NPU) with WebGPU/CPU fallback.</summary>
    QnnHtp = 1,

    /// <summary>ARM64 NEON/FP16 XNNPACK Execution Provider with CPU fallback.</summary>
    Xnnpack = 2,

    /// <summary>Standard ONNX Runtime CPU Execution Provider (ARM64 KleidiAI SIMD).</summary>
    Cpu = 3,

    /// <summary>WebGPU (Vulkan Dawn GPU — Adreno / Mali / Xclipse) Execution Provider with automatic CPU fallback.</summary>
    WebGpu = 4
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
/// Lossless/Studio-lossless audio compression tier for the Slot 7B persistent AI stem cache (<c>CNTC v3</c>).
/// </summary>
public enum NeuralTrackCacheCompressionMode {
    /// <summary>Compact 16-bit Mid/Side + 2nd-order LPC delta + ZLib lossless-within-16bit (&gt;96 dB SNR, ~5–7× smaller than float32).</summary>
    Compact16Bit = 0,

    /// <summary>Studio 24-bit Mid/Side + 2nd-order LPC delta + ZLib mastering-grade (&gt;140 dB SNR, ~3–4× smaller than float32).</summary>
    Studio24Bit = 1,

    /// <summary>Uncompressed IEEE-754 float32 PCM (legacy maximum speed, largest disk footprint).</summary>
    RawFloat32 = 2
}

/// <summary>
/// Offline multi-pass Studio Ensemble &amp; Bundle presets (MVSEP methodology for maximum SDR on non-real-time renders).
/// </summary>
public enum NeuralEnsembleBundlePreset {
    /// <summary>Fast single-model pass optimized for interactive playback and minimal battery usage.</summary>
    FastSingle = 0,

    /// <summary>Studio Vocal Ensemble: combines all installed vocal models via MVSEP <c>min_fft</c>/<c>avg_wave</c> + Wiener <c>phase_fix</c>.</summary>
    StudioVocalEnsemble = 1,

    /// <summary>Studio Pure Instrumental: full-vocal removal (lead + backing) with optional Debleed &amp; conserved instrumental.</summary>
    StudioPureInstrumental = 2,

    /// <summary>Studio Full-Band 6-Stem Bundle: fuses dedicated high-SDR Vocal model with 4/6-stem separator via Vocal-Anchor conservation.</summary>
    StudioFullBand6Stem = 3,

    /// <summary>MVSEP 2-Stage Studio Karaoke Cascade: Mix -&gt; [AllVocals, PureInstrumental], then AllVocals -&gt; [LeadVocal, BackingVocals] (+1.1 dB SDR).</summary>
    StudioKaraokeLeadBacking = 4
}

/// <summary>
/// Execution backend for AI stem separation: on-device ONNX Runtime, Official MVSEP REST Cloud Offload, or Auto Hybrid.
/// </summary>
public enum NeuralExecutionBackend {
    /// <summary>Local on-device ONNX Runtime (WebGPU Vulkan GPU / QNN HTP NPU / ARM64 KleidiAI CPU).</summary>
    OnDevice = 0,

    /// <summary>Official MVSEP REST API Cloud Offload (zero phone heat/GPU load; requires user-supplied MVSEP API token).</summary>
    MvsepCloud = 1,

    /// <summary>Auto Hybrid: uses MVSEP Cloud when token is configured and online, falling back seamlessly to On-Device ONNX.</summary>
    AutoHybrid = 2
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
/// Tensor I/O contract implemented by an ONNX audio checkpoint.
/// </summary>
public enum NeuralOnnxContractKind {
    /// <summary>Automatically inferred from runtime input tensor names and shapes.</summary>
    Auto = 0,

    /// <summary>End-to-end planar stereo waveform <c>[1, 2, samples]</c> (e.g., Apollo, HTDemucs).</summary>
    WaveformStereo = 1,

    /// <summary>SCNet 4-channel complex STFT <c>[1, 4, 2049, frames]</c> -> <c>[1, 4, 4, 2049, frames]</c>.</summary>
    ScnetStft4Ch = 2,

    /// <summary>UVR MDX-Net 4-channel complex STFT <c>[1, 4, dimF, dimT]</c> -> <c>[1, 4, dimF, dimT]</c>.</summary>
    MdxNetStft4Ch = 3,

    /// <summary>Mel-Band RoFormer host-STFT mask network (<c>stft_features [1, frames, 4100]</c> -> <c>mask [1, 1, 2050, frames, 2]</c>).</summary>
    MelBandRoFormerHostStft = 4,

    /// <summary>FlashSR 16 kHz -> 48 kHz bandwidth-extension upsampler (<c>audio_values [B, n16k]</c> -> <c>reconstruction [B, 3*n16k]</c>).</summary>
    FlashSr16kTo48k = 5,

    /// <summary>BS-RoFormer dual-tensor host-STFT (<c>spec_real</c>, <c>spec_imag</c> <c>[1, 2, 1025, T]</c> -> <c>out_spec_real</c>, <c>out_spec_imag</c> <c>[1, S, 2, 1025, T]</c>).</summary>
    BsRoFormerHostStft = 6
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

    /// <summary>ONNX tensor I/O contract kind used by the neural inference pipeline.</summary>
    public NeuralOnnxContractKind ContractKind { get; init; } = NeuralOnnxContractKind.Auto;

    /// <summary>Host STFT FFT window size in samples (0 when waveform end-to-end).</summary>
    public int StftFftSize { get; init; }

    /// <summary>Host STFT hop size in samples (0 when waveform end-to-end).</summary>
    public int StftHopSize { get; init; }

    /// <summary>Model frequency bin count <c>dim_f</c> (0 when waveform end-to-end).</summary>
    public int StftDimF { get; init; }

    /// <summary>Fixed temporal frame count <c>dim_t</c> for MDX-Net models (0 when dynamic).</summary>
    public int StftDimT { get; init; }

    /// <summary>UVR MDX-Net scalar stem compensation multiplier.</summary>
    public float MdxCompensate { get; init; } = 1f;

    /// <summary>When true, primary stem is the spectral complement <c>mix - output * compensate</c> (used by Karaoke/Reverb/Crowd models that predict backing/reverb/crowd).</summary>
    public bool InvertPrimaryStem { get; init; }
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
            SourceRepository = "starrytong/SCNet + bgkb/scnet_onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation4,
            FileName = "scnet_4stem_fp16.onnx",
            DownloadUrl = "https://huggingface.co/bgkb/scnet_onnx/resolve/main/scnet_fp16.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/bgkb/scnet_onnx/resolve/main/scnet.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 22.4f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 484352,
            OutputStemNames = ["drums", "bass", "other", "vocals"],
            ContractKind = NeuralOnnxContractKind.ScnetStft4Ch,
            StftFftSize = 4096,
            StftHopSize = 1024,
            StftDimF = 2049,
            StftDimT = 474,
            IsMobileFast = true,
            PriorityRank = 110
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_vocals_studio",
            DisplayName = "Kim Vocal 2 MDX-Net (Studio)",
            SourceRepository = "Anjok07/UVR + Acapellas/Kim_Vocal_2.onnx",
            License = "MIT",
            Task = NeuralModelTask.VocalSeparation,
            FileName = "kim_vocal_2.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/Kim_Vocal_2.onnx/resolve/main/Kim_Vocal_2.onnx",
            FallbackDownloadUrls = [
                "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/Kim_Vocal_2.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 66.8f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 261120,
            OutputStemNames = ["vocals"],
            ContractKind = NeuralOnnxContractKind.MdxNetStft4Ch,
            StftFftSize = 7680,
            StftHopSize = 1024,
            StftDimF = 3072,
            StftDimT = 256,
            MdxCompensate = 1.009f,
            InvertPrimaryStem = false,
            IsMobileFast = true,
            PriorityRank = 140
        },
        new NeuralModelCatalogEntry {
            Id = "bs_roformer_6stem_studio",
            DisplayName = "HTDemucs 6-Stem (Studio)",
            SourceRepository = "facebookresearch/demucs + StemSplitio/htdemucs-6s-onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation6,
            FileName = "htdemucs_6s_fp16.onnx",
            DownloadUrl = "https://huggingface.co/StemSplitio/htdemucs-6s-onnx/resolve/main/htdemucs_6s_fp16weights.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/Acapellas/htdemucs-6s-onnx/resolve/main/htdemucs_6s_fp16weights.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 136.4f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 343980,
            OutputStemNames = ["drums", "bass", "other", "vocals", "guitar", "piano"],
            ContractKind = NeuralOnnxContractKind.WaveformStereo,
            IsMobileFast = false,
            PriorityRank = 160
        },
        new NeuralModelCatalogEntry {
            Id = "bs_roformer_sw_6stem_fp16",
            DisplayName = "BS-RoFormer SW 6-Stem FP16 (WebGPU Studio)",
            SourceRepository = "jarredou/BS-ROFO-SW + elicwhite/bs-roformer-sw-6stem-onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation6,
            FileName = "bs_roformer_sw_6stem_fp16.onnx",
            DownloadUrl = "https://huggingface.co/elicwhite/bs-roformer-sw-6stem-onnx/resolve/main/bs_roformer_sw_6stem_fp16.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/Acapellas/bs-roformer-sw-6stem-FP16-FP32-onnx/resolve/main/bs_roformer_sw_6stem_fp16.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 336.0f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 176400,
            OutputStemNames = ["bass", "drums", "other", "vocals", "guitar", "piano"],
            ContractKind = NeuralOnnxContractKind.BsRoFormerHostStft,
            StftFftSize = 2048,
            StftHopSize = 512,
            StftDimF = 1025,
            StftDimT = 345,
            IsMobileFast = false,
            PriorityRank = 190
        },
        new NeuralModelCatalogEntry {
            Id = "bs_roformer_4stem_rhythm_fp16",
            DisplayName = "BS-RoFormer 4-Stem Rhythm FP16 (Studio)",
            SourceRepository = "ZFTurbo/MSST + silverdaw/bs-roformer-rhythm-onnx",
            License = "MIT",
            Task = NeuralModelTask.StemSeparation4,
            FileName = "bs_roformer_4stem_rhythm_fp16.onnx",
            DownloadUrl = "https://huggingface.co/silverdaw/bs-roformer-rhythm-onnx/resolve/main/bs_roformer_4stem_rhythm_fp16.onnx",
            FallbackDownloadUrls = Array.Empty<string>(),
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 257.0f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["drums", "bass", "other", "vocals"],
            ContractKind = NeuralOnnxContractKind.BsRoFormerHostStft,
            StftFftSize = 2048,
            StftHopSize = 441,
            StftDimF = 1025,
            StftDimT = 801,
            IsMobileFast = false,
            PriorityRank = 170
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_vocals_roformer_sota",
            DisplayName = "Mel-Band RoFormer Vocals (Studio SDR 11.0)",
            SourceRepository = "KimberleyJSN/melbandroformer + smank/mel-band-roformer-vocals-onnx",
            License = "MIT",
            Task = NeuralModelTask.VocalSeparation,
            FileName = "melband_roformer_vocals.onnx",
            DownloadUrl = "https://huggingface.co/smank/mel-band-roformer-vocals-onnx/resolve/main/melband_roformer_vocals.onnx",
            FallbackDownloadUrls = Array.Empty<string>(),
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 953.3f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["vocals", "instrumental"],
            ContractKind = NeuralOnnxContractKind.WaveformStereo,
            IsMobileFast = false,
            PriorityRank = 185
        },
        new NeuralModelCatalogEntry {
            Id = DefaultKaraokeModelId,
            DisplayName = "UVR MDX-Net Karaoke 2 (Lead vs Backing)",
            SourceRepository = "Anjok07/UVR + TRvlvr/model_repo",
            License = "MIT",
            Task = NeuralModelTask.KaraokeLeadBacking,
            FileName = "uvr_mdxnet_kara_2.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/uvr5-models/resolve/main/UVR_MDXNET_KARA_2.onnx",
            FallbackDownloadUrls = [
                "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR_MDXNET_KARA_2.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 52.8f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 261120,
            OutputStemNames = ["lead_vocal", "backing_vocals"],
            ContractKind = NeuralOnnxContractKind.MdxNetStft4Ch,
            StftFftSize = 5120,
            StftHopSize = 1024,
            StftDimF = 2048,
            StftDimT = 256,
            MdxCompensate = 1.065f,
            InvertPrimaryStem = true,
            IsMobileFast = true,
            PriorityRank = 150
        },
        new NeuralModelCatalogEntry {
            Id = "mbr_karaoke_roformer_webgpu",
            DisplayName = "Mel-Band RoFormer Karaoke FP16 (WebGPU SOTA)",
            SourceRepository = "aufr33/viperx + Acapellas/mel-band-roformer-karaoke-webgpu",
            License = "MIT",
            Task = NeuralModelTask.KaraokeLeadBacking,
            FileName = "mel_band_roformer_karaoke_webgpu_fp16.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/mel-band-roformer-karaoke-webgpu/resolve/main/model_fp16.onnx",
            FallbackDownloadUrls = [
                "https://huggingface.co/Acapellas/mel-band-roformer-karaoke-webgpu/resolve/main/model.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 460.4f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 352800,
            OutputStemNames = ["lead_vocal", "backing_vocals"],
            ContractKind = NeuralOnnxContractKind.MelBandRoFormerHostStft,
            StftFftSize = 2048,
            StftHopSize = 441,
            StftDimF = 1025,
            IsMobileFast = false,
            PriorityRank = 180
        },
        new NeuralModelCatalogEntry {
            Id = DefaultDereverbModelId,
            DisplayName = "Reverb HQ MDX-Net (Dry / Wet RIR)",
            SourceRepository = "FoxJoy/UVR + Acapellas/AudioSeparatorONNX",
            License = "MIT",
            Task = NeuralModelTask.Dereverb,
            FileName = "reverb_hq_foxjoy.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/AudioSeparatorONNX/resolve/main/Reverb_HQ_By_FoxJoy.onnx",
            FallbackDownloadUrls = [
                "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/Reverb_HQ_By_FoxJoy.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 66.8f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 523264,
            OutputStemNames = ["dry_vocal"],
            ContractKind = NeuralOnnxContractKind.MdxNetStft4Ch,
            StftFftSize = 6144,
            StftHopSize = 1024,
            StftDimF = 3072,
            StftDimT = 512,
            MdxCompensate = 1.035f,
            InvertPrimaryStem = true,
            IsMobileFast = true,
            PriorityRank = 130
        },
        new NeuralModelCatalogEntry {
            Id = DefaultDebleedModelId,
            DisplayName = "UVR Crowd & Bleed HQ (Debleed / Denoise)",
            SourceRepository = "aufr33/UVR + Acapellas/AudioSeparatorONNX",
            License = "MIT",
            Task = NeuralModelTask.DebleedDenoise,
            FileName = "uvr_mdxnet_crowd_hq_1.onnx",
            DownloadUrl = "https://huggingface.co/Acapellas/AudioSeparatorONNX/resolve/main/UVR-MDX-NET_Crowd_HQ_1.onnx",
            FallbackDownloadUrls = [
                "https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/UVR-MDX-NET_Crowd_HQ_1.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 59.1f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 261120,
            OutputStemNames = ["clean"],
            ContractKind = NeuralOnnxContractKind.MdxNetStft4Ch,
            StftFftSize = 8192,
            StftHopSize = 1024,
            StftDimF = 4096,
            StftDimT = 256,
            MdxCompensate = 1.021f,
            InvertPrimaryStem = true,
            IsMobileFast = true,
            PriorityRank = 120
        },
        new NeuralModelCatalogEntry {
            Id = DefaultRestorerModelId,
            DisplayName = "Apollo Band-Split Codec Restorer",
            SourceRepository = "JusperLee/Apollo (Cavernize ONNX v1)",
            License = "CC BY-SA 4.0",
            Task = NeuralModelTask.CodecRestoreApollo,
            FileName = "apollo_band_split_44k.onnx",
            DownloadUrl = "https://github.com/guumachadooo/Cavern/releases/download/neural-models-v1/apollo_band_split_44k.onnx",
            FallbackDownloadUrls = Array.Empty<string>(),
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 70.5f,
            ExpectedSampleRate = 44100,
            RecommendedChunkSamples = 131072,
            OutputStemNames = ["restored"],
            ContractKind = NeuralOnnxContractKind.WaveformStereo,
            IsMobileFast = true,
            PriorityRank = 135
        },
        new NeuralModelCatalogEntry {
            Id = DefaultBweModelId,
            DisplayName = "FlashSR 48 kHz Bandwidth Extension",
            SourceRepository = "YatharthS/FlashSR (HierSpeech++ Upsampler)",
            License = "Apache-2.0",
            Task = NeuralModelTask.BandwidthExtension,
            FileName = "flashsr_48k.onnx",
            DownloadUrl = "https://huggingface.co/YatharthS/FlashSR/resolve/main/onnx/model.onnx",
            FallbackDownloadUrls = [
                "https://github.com/guumachadooo/Cavern/releases/download/neural-models-v1/flashsr_48k.onnx"
            ],
            RequiresManualOnnxImport = false,
            ApproxSizeMb = 0.5f,
            ExpectedSampleRate = 48000,
            RecommendedChunkSamples = 192000,
            OutputStemNames = ["extended_48k"],
            ContractKind = NeuralOnnxContractKind.FlashSr16kTo48k,
            IsMobileFast = true,
            PriorityRank = 115
        }
    ];

    /// <summary>Finds a registered model entry by its local ONNX filename (case-insensitive).</summary>
    public static NeuralModelCatalogEntry FindByFileName(string fileName) {
        if (string.IsNullOrWhiteSpace(fileName)) {
            return null;
        }
        for (int i = 0; i < Entries.Count; i++) {
            if (string.Equals(Entries[i].FileName, fileName, StringComparison.OrdinalIgnoreCase)) {
                return Entries[i];
            }
        }
        return null;
    }

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
        "• ZFTurbo / MVSEP Anti-Artifact Pipeline & Official MVSEP REST API (TTA, avg_wave/min_fft/avg_fft, Wiener phase_fix) — MIT License\n" +
        "• BS-RoFormer & Mel-Band RoFormer (ByteDance/lucidrains, KimberleyJSN, jarredou, aufr33/viperx, elicwhite, silverdaw, smank) — MIT License\n" +
        "• starrytong/SCNet & bgkb/scnet_onnx (4-Stem Mobile) — MIT License\n" +
        "• Meta Platforms Demucs v4 / StemSplitio htdemucs_6s (6-Stem Studio) — MIT License\n" +
        "• Anjok07 / TRvlvr UVR MDX-Net (Kim Vocal 2, Karaoke 2, FoxJoy Reverb HQ, aufr33 Crowd HQ) — MIT License\n" +
        "• JusperLee/Apollo Band-Split Music Restoration — CC BY-SA 4.0 License\n" +
        "• YatharthS/FlashSR (HierSpeech++ 48 kHz Bandwidth Extension) — Apache-2.0 License";
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

    /// <summary>Enables automatic persistent storage of separated AI stems in <c>CNTC v3</c> compressed cache.</summary>
    public bool TrackCacheEnabled { get; init; } = true;

    /// <summary>Lossless compression tier for <c>CNTC v3</c> persistent stem cache.</summary>
    public NeuralTrackCacheCompressionMode TrackCacheCompressionMode { get; init; } = NeuralTrackCacheCompressionMode.Compact16Bit;

    /// <summary>Offline multi-pass Studio Ensemble/Bundle preset (FastSingle for instant playback; Studio* for multi-pass mastering).</summary>
    public NeuralEnsembleBundlePreset StudioBundlePreset { get; init; } = NeuralEnsembleBundlePreset.FastSingle;

    /// <summary>Selected AI separation execution backend (On-Device ONNX, MVSEP Cloud REST API, or Auto Hybrid).</summary>
    public NeuralExecutionBackend ExecutionBackend { get; init; } = NeuralExecutionBackend.OnDevice;

    /// <summary>
    /// Inter-chunk GPU breathing yield in milliseconds (0..100 ms; default 16 ms) so WebGPU/Vulkan inference
    /// leaves Android SurfaceFlinger/HWUI compositor headroom and never freezes phone UI.
    /// </summary>
    public int GpuUiBreathingYieldMs { get; init; } = 16;

    /// <summary>
    /// Enables Auto-RTF progressive live stem streaming: when initial chunks prove faster than real time
    /// (<c>RTF &lt; 0.85</c>) during sequential playback, separated stems engage progressively without waiting for 100%.
    /// </summary>
    public bool EnableProgressiveStreaming { get; init; } = true;

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
        if (TaskModelSlots != null && TaskModelSlots.TryGetValue(task, out IReadOnlyList<string> configured)) {
            if (configured != null && configured.Count > 0) {
                return configured;
            }
            if (OrchestrationMode == NeuralOrchestrationMode.Manual) {
                return Array.Empty<string>();
            }
        }
        if (OrchestrationMode == NeuralOrchestrationMode.Manual && HasAnyNonEmptyTaskSlot(TaskModelSlots)) {
            return Array.Empty<string>();
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

    static bool HasAnyNonEmptyTaskSlot(IReadOnlyDictionary<NeuralModelTask, IReadOnlyList<string>> slots) {
        if (slots == null || slots.Count == 0) {
            return false;
        }
        foreach (IReadOnlyList<string> list in slots.Values) {
            if (list != null && list.Count > 0) {
                return true;
            }
        }
        return false;
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
        TrackCacheEnabled = TrackCacheEnabled,
        TrackCacheCompressionMode = TrackCacheCompressionMode,
        StudioBundlePreset = StudioBundlePreset,
        ExecutionBackend = ExecutionBackend,
        GpuUiBreathingYieldMs = GpuUiBreathingYieldMs,
        EnableProgressiveStreaming = EnableProgressiveStreaming,
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

        // In Manual mode with explicit TaskModelSlots, preserve user-chosen grouped slots strictly so removing
        // a model from one category never resurrects stale legacy single-ID fields (zero chain-lock).
        bool hasExplicitManualSlots = OrchestrationMode == NeuralOrchestrationMode.Manual && normalizedSlots.Count > 0;
        if (!hasExplicitManualSlots) {
            // Ensure canonical single-ID properties and dynamic task slots remain synchronized in both directions
            EnsurePrimarySlot(normalizedSlots, ResolveStemTask(stemId), stemId);
            EnsurePrimarySlot(normalizedSlots, NeuralModelTask.KaraokeLeadBacking, karaokeId);
            EnsurePrimarySlot(normalizedSlots, NeuralModelTask.Dereverb, dereverbId);
            EnsurePrimarySlot(normalizedSlots, NeuralModelTask.DebleedDenoise, debleedId);
            EnsurePrimarySlot(normalizedSlots, ResolveRestorerTask(restorerId), restorerId);
        }

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
            TrackCacheEnabled = TrackCacheEnabled,
            TrackCacheCompressionMode = Enum.IsDefined(TrackCacheCompressionMode) ? TrackCacheCompressionMode : NeuralTrackCacheCompressionMode.Compact16Bit,
            StudioBundlePreset = Enum.IsDefined(StudioBundlePreset) ? StudioBundlePreset : NeuralEnsembleBundlePreset.FastSingle,
            ExecutionBackend = Enum.IsDefined(ExecutionBackend) ? ExecutionBackend : NeuralExecutionBackend.OnDevice,
            GpuUiBreathingYieldMs = Math.Clamp(GpuUiBreathingYieldMs, 0, 100),
            EnableProgressiveStreaming = EnableProgressiveStreaming,
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
        EnforceEnergyConservation = EnforceEnergyConservation,
        EnableAsyncPipelining = true,
        GpuUiBreathingYieldMs = Math.Clamp(GpuUiBreathingYieldMs, 0, 100)
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
            || TrackCacheEnabled != other.TrackCacheEnabled
            || TrackCacheCompressionMode != other.TrackCacheCompressionMode
            || StudioBundlePreset != other.StudioBundlePreset
            || ExecutionBackend != other.ExecutionBackend
            || GpuUiBreathingYieldMs != other.GpuUiBreathingYieldMs
            || EnableProgressiveStreaming != other.EnableProgressiveStreaming
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
        hc.Add(TrackCacheEnabled);
        hc.Add(TrackCacheCompressionMode);
        hc.Add(StudioBundlePreset);
        hc.Add(ExecutionBackend);
        hc.Add(GpuUiBreathingYieldMs);
        hc.Add(EnableProgressiveStreaming);
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
    /// <summary>True when bundled ARM64 <c>libonnxruntime.so</c> is available and verified in <c>NativeLibraryDir</c>.</summary>
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
    int readySampleCount = -1;

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

    /// <summary>Human-readable track title persisted into <c>CNTC v3</c> header for instant Cache Folder Browser display.</summary>
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>Optional artist name persisted into <c>CNTC v3</c> header.</summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>Processing origin label persisted into <c>CNTC v3</c> header (e.g. "Local AI", "MVSEP Cloud", "Studio Bundle").</summary>
    public string OriginTag { get; set; } = string.Empty;

    /// <summary>Model or bundle summary persisted into <c>CNTC v3</c> header.</summary>
    public string ModelSummary { get; set; } = string.Empty;

    /// <summary>Total stem length in samples per channel.</summary>
    public int TotalSampleCount => Stems != null && Stems.Count > 0 && Stems[0] != null ? Stems[0].Length : (MonoLeadVocalRef?.Length ?? 0);

    /// <summary>
    /// True while chunks are still progressively streaming into this track's buffers via Auto-RTF live mode.
    /// </summary>
    public bool IsProgressiveStreaming { get; set; }

    /// <summary>Latest measured real-time factor (<c>wallSec / audioSec</c>) during progressive or offline inference.</summary>
    public float MeasuredRealTimeFactor { get; set; }

    /// <summary>
    /// Lock-free monotonically advancing sample count that is permanently finalized and safe for real-time audio reads.
    /// Defaults to <see cref="TotalSampleCount"/> for fully materialized tracks.
    /// </summary>
    public int ReadySampleCount {
        get {
            int val = Volatile.Read(ref readySampleCount);
            return val < 0 ? TotalSampleCount : val;
        }
        set => Volatile.Write(ref readySampleCount, Math.Max(0, value));
    }
}
