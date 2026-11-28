using System.Numerics;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Environment;
using Cavern.Neural;
using Cavern.Remapping;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Cavern.Waveforms;

using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Wave 12 Studio offline master export presets.
/// </summary>
public enum StudioExportPreset {
    /// <summary>Personalized lossless binaural export (HRTF + ISM Room + AutoEq + HpTF + ISO 226 + True-Peak Limiter).</summary>
    PersonalizedLosslessBinaural = 0,
    /// <summary>Eclipsa Audio — AOMedia IAMF v1.0 OBU spatial container (<c>.iamf</c>).</summary>
    EclipsaAudioIamf = 1,
    /// <summary>Ambisonics HOA B-Format 3rd-Order ACN/SN3D 16-channel WAVE-EX (<c>.amb</c>).</summary>
    AmbisonicsHoa3rdOrder = 2,
    /// <summary>ADM BWF (<c>ITU-R BS.2076</c>) with <c>axml</c>, <c>chna</c>, and RF64 <c>ds64</c> (<c>.wav</c>).</summary>
    AdmBwfItuBs2076 = 3,
    /// <summary>Hybrid Neural + DirAC Parametric Stereo -> 7.1.4 Atmos spatial upmix (<c>.wav</c>).</summary>
    HybridNeuralDirAc714 = 4,
    /// <summary>Spatial Karaoke / Instrumental Atmos export preserving backing vocals &amp; LFE (<c>.wav</c>).</summary>
    SpatialKaraokeInstrumental = 5,
    /// <summary>Live Karaoke Performance spatial mixdown (Instrumental + W11 Live Vocal Chain + Sidechain Glue).</summary>
    LiveKaraokePerformanceMixdown = 6,
    /// <summary>FLAC + Atmos Hybrid Lossless Spatial Master (W09 Tier 2 EM-MWF fused 7.1.4 / ADM BWF).</summary>
    FlacAtmosHybridSpatialMaster = 7
}

/// <summary>
/// Commercial base output container/format for the W48 dynamic composable Studio export builder.
/// </summary>
public enum StudioBaseOutputFormat {
    /// <summary>3D Headphone Spatial Audio (.wav — personalized HRTF + room virtualizer).</summary>
    Headphone3D = 0,
    /// <summary>Cinema Surround 7.1.4 (.wav — 12 discrete channels including LFE .1 and 4 overhead heights).</summary>
    Cinema714 = 1,
    /// <summary>Dolby Atmos Master — ADM BWF ITU-R BS.2076 (.wav — preserves dynamic moving 3D objects).</summary>
    DolbyAtmosAdm = 2,
    /// <summary>Eclipsa Audio 3D — AOMedia IAMF (.iamf — modern open 3D spatial streaming container).</summary>
    EclipsaIamf = 3,
    /// <summary>360° Ambisonics Scene — 3rd-Order HOA (.amb — 16-ch spherical soundfield).</summary>
    Ambisonics360 = 4
}

/// <summary>
/// Modular composable Studio export configuration allowing independent combination of
/// Base Output Format + AI 3D Upmix/Atmos Enhancement + Karaoke + Recorded Performance + Lossless Restoration.
/// </summary>
public sealed class StudioComposableExportOptions {
    /// <summary>Selected base output format.</summary>
    public StudioBaseOutputFormat BaseFormat { get; set; } = StudioBaseOutputFormat.Headphone3D;

    /// <summary>Apply AI 3D Spatial Upmix on stereo tracks or Atmos-safe enhancement preserving moving objects on Atmos tracks.</summary>
    public bool EnableAiSpatialUpmix { get; set; }

    /// <summary>Apply Spatial Karaoke vocal attenuation while keeping backing vocals &amp; LFE intact.</summary>
    public bool EnableKaraokeInstrumental { get; set; }

    /// <summary>Mix the Studio recorded vocal performance take through the W11 Live Vocal Chain with sidechain pocket.</summary>
    public bool IncludeRecordedPerformance { get; set; }

    /// <summary>Apply W09 Lossless Twin Restoration (EM-MWF on Atmos or lossless twin stereo feed for neural stems).</summary>
    public bool EnableLosslessTwinRestoration { get; set; }

    /// <summary>Apply active personalized headphone post-processing stages (AutoEq, HpTF, ISO 226) on Headphone 3D output.</summary>
    public bool ApplyPersonalizedHeadphonePost { get; set; } = true;

    /// <summary>Track sample offset where the recorded performance take started (0 = beginning of song).</summary>
    public long RecordedPerformanceStartTrackSample { get; set; }

    /// <summary>Sample rate of the recorded performance take (0 = same as source track).</summary>
    public int RecordedPerformanceSampleRate { get; set; }

    /// <summary>Commercial output compression tier (ADM BWF always locks to <see cref="StudioCompressionTier.MasterPcm"/>).</summary>
    public StudioCompressionTier CompressionTier { get; set; } = StudioCompressionTier.MasterPcm;

    /// <summary>Optional path to FFmpeg (<c>libffmpeg.so</c> / <c>ffmpeg</c>) for WavPack, Opus, AAC, or IAMF pipe encoding.</summary>
    public string FfmpegExecutablePath { get; set; } = string.Empty;

    /// <summary>Optional precomputed 3D spatial object choreography track evaluated per frame during offline rendering.</summary>
    public SpatialChoreoTrack ChoreoTrack { get; set; }

    /// <summary>Explicit Studio dynamic Atmos object motion mode (Off = static anchors; Subtle/Musical/Cinematic/Orbit = moving 3D objects).</summary>
    public SpatialChoreoMode DynamicChoreoMode { get; set; } = SpatialChoreoMode.Off;

    /// <summary>Optional privacy-safe distilled lyric mood sections guiding dynamic spatial choreography.</summary>
    public IReadOnlyList<LyricMoodDigest> LyricMoodDigests { get; set; }

    /// <summary>Optional metadata, cover art, and synchronized lyrics payload preserved into exported audio files.</summary>
    public ExportMetadataPackage Metadata { get; set; }

    /// <summary>Optional callback invoked before piping to FFmpeg stdin to install a <c>SIGPIPE</c> shield.</summary>
    public Action OnSigpipeShield { get; set; }
}

/// <summary>
/// Configuration for a single Studio batch export job.
/// </summary>
public sealed class StudioBatchExportJob {
    /// <summary>Display label of the source track.</summary>
    public string TrackName { get; set; } = "Cavernize_Master";

    /// <summary>Selected W12 Studio export preset.</summary>
    public StudioExportPreset Preset { get; set; } = StudioExportPreset.EclipsaAudioIamf;

    /// <summary>Output file path (extension auto-aligned to preset if empty).</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Output bit depth (default 24-bit studio lossless).</summary>
    public BitDepth BitDepth { get; set; } = BitDepth.Int24;

    /// <summary>Target True-Peak ceiling in dBTP (-1.0 dBTP streaming/mastering default).</summary>
    public float TruePeakCeilingDbtp { get; set; } = -1.0f;

    /// <summary>Karaoke lead vocal level (0.0 = instrumental, 1.0 = full vocals) for Karaoke presets.</summary>
    public float KaraokeVocalLevel { get; set; } = 0.05f;

    /// <summary>Preserve backing vocals in Spatial Karaoke export.</summary>
    public bool PreserveBackingVocals { get; set; } = true;

    /// <summary>Optional pre-recorded mono live mic PCM capture for <see cref="StudioExportPreset.LiveKaraokePerformanceMixdown"/>.</summary>
    public float[] LiveMicMonoCapture { get; set; }

    /// <summary>Optional active W11 vocal chain profile for Live Performance Mixdown.</summary>
    public VocalMixDnaProfile VocalDnaProfile { get; set; }

    /// <summary>Optional synced lossless FLAC/WAV reference twin for W09 Hybrid Spatial Master and neural acceleration.</summary>
    public ILosslessReferenceProvider LosslessTwinProvider { get; set; }

    /// <summary>Optional Slot 7B ONNX inference runtime for neural mastering presets.</summary>
    public INeuralInferenceRuntime NeuralRuntime { get; set; }

    /// <summary>Optional Slot 7A neural pipeline orchestrator (ensemble, Debleed, Apollo/AP-BWE, Slot 7B disk cache).</summary>
    public NeuralPipelineOrchestrator NeuralOrchestrator { get; set; }

    /// <summary>Optional Slot 7A neural audio configuration.</summary>
    public NeuralAudioConfig NeuralConfig { get; set; }

    /// <summary>Optional W09 LGSR restoration configuration (including Tier 3 residual cleanup flag).</summary>
    public LgsrConfig LgsrConfig { get; set; }

    /// <summary>Optional Slot 6 post-processing stages (AutoEq, HpTF, ISO 226, BS.1770, Dialog) applied in Personalized Binaural export.</summary>
    public IReadOnlyList<IPostProcessingStage> PostProcessingStages { get; set; }

    /// <summary>Optional callback invoked whenever a neural pipeline produces a cached <see cref="NeuralSeparatedTrack"/>.</summary>
    public Action<NeuralSeparatedTrack> OnSeparatedTrackReady { get; set; }

    /// <summary>Whether to export ADM BWF in Dolby Atmos 10-bed subset mode.</summary>
    public bool UseDolbyAtmosAdmSubset { get; set; } = true;

    /// <summary>Ambisonic order for HOA / Scene-Based IAMF export (1..3, default 3 = 16 ch).</summary>
    public int HoaOrder { get; set; } = 3;

    /// <summary>
    /// Optional W48 modular composable export configuration. When non-null, <see cref="StudioBatchExporter.ExportSingle"/>
    /// composes the selected base output format with all enabled processing modules (AI Upmix, Karaoke, Recorded Performance,
    /// Lossless Twin Restoration) instead of forcing a single mutually-exclusive legacy preset.
    /// </summary>
    public StudioComposableExportOptions ComposableOptions { get; set; }

    /// <summary>Commercial output compression tier (ADM BWF always locks to <see cref="StudioCompressionTier.MasterPcm"/>).</summary>
    public StudioCompressionTier CompressionTier { get; set; } = StudioCompressionTier.MasterPcm;

    /// <summary>Optional path to FFmpeg (<c>libffmpeg.so</c> / <c>ffmpeg</c>) for WavPack, Opus, AAC, or IAMF pipe encoding.</summary>
    public string FfmpegExecutablePath { get; set; } = string.Empty;

    /// <summary>Optional precomputed 3D spatial object choreography track evaluated per frame during offline rendering.</summary>
    public SpatialChoreoTrack ChoreoTrack { get; set; }

    /// <summary>Explicit Studio dynamic Atmos object motion mode (Off = static anchors; Subtle/Musical/Cinematic/Orbit = moving 3D objects).</summary>
    public SpatialChoreoMode DynamicChoreoMode { get; set; } = SpatialChoreoMode.Off;

    /// <summary>Optional privacy-safe distilled lyric mood sections guiding dynamic spatial choreography.</summary>
    public IReadOnlyList<LyricMoodDigest> LyricMoodDigests { get; set; }

    /// <summary>Optional metadata, cover art, and synchronized lyrics payload preserved into exported audio files.</summary>
    public ExportMetadataPackage Metadata { get; set; }

    /// <summary>Optional callback invoked before piping to FFmpeg stdin to install a <c>SIGPIPE</c> shield.</summary>
    public Action OnSigpipeShield { get; set; }
}

/// <summary>
/// Post-render loudness, True-Peak, and container telemetry for a completed W12 Studio export job.
/// </summary>
public sealed class StudioBatchExportResult {
    /// <summary>Executed preset.</summary>
    public StudioExportPreset Preset { get; init; }

    /// <summary>Generated output path.</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>Resolved container/codec description.</summary>
    public string FormatSummary { get; init; } = string.Empty;

    /// <summary>Output channel count.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Rendered duration in seconds.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Measured ITU-R BS.1770-5 Integrated loudness in LUFS.</summary>
    public float IntegratedLufs { get; init; }

    /// <summary>Measured EBU Tech 3342 Loudness Range in LU.</summary>
    public float LoudnessRangeLu { get; init; }

    /// <summary>Measured 4x-oversampled BS.1770-5 True Peak in dBTP.</summary>
    public float MaxTruePeakDbtp { get; init; }

    /// <summary>True when export succeeded without cancellation or I/O error.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Error details when <see cref="Succeeded"/> is false.</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>True when export was aborted via cancellation token.</summary>
    public bool IsCancelled { get; init; }

    /// <summary>Final output file size in bytes (0 when unavailable or cancelled).</summary>
    public long OutputBytes { get; init; }

    /// <summary>Resolved compression tier used for this export.</summary>
    public StudioCompressionTier CompressionTier { get; init; }

    /// <summary>True when text tags were embedded into the exported container.</summary>
    public bool MetadataEmbedded { get; init; }

    /// <summary>True when front cover artwork was embedded into the exported container.</summary>
    public bool CoverArtEmbedded { get; init; }

    /// <summary>True when synchronized or plain lyrics were embedded into the exported container.</summary>
    public bool LyricsEmbedded { get; init; }

    /// <summary>Effective dynamic Atmos object choreography mode applied during export.</summary>
    public SpatialChoreoMode EffectiveChoreoMode { get; init; }
}

/// <summary>
/// Wave 12 unified offline Studio Batch Exporter supporting all 8 spatial mastering workflows:
/// Personalized Lossless Binaural, Eclipsa Audio (IAMF), 3rd-Order Ambisonics HOA, ADM BWF (ITU BS.2076),
/// Hybrid Neural+DirAC Stereo->7.1.4, Spatial Karaoke Atmos, Live Karaoke Performance Mixdown,
/// and FLAC+Atmos Hybrid Lossless Spatial Master.
/// </summary>
public static partial class StudioBatchExporter {
    const int updateRate = 1024;

    static readonly ReferenceChannel[] Layout714 = [
        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
        ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
    ];

    /// <summary>
    /// Returns the recommended native file extension for a W12 <see cref="StudioExportPreset"/>.
    /// </summary>
    public static string GetDefaultExtension(StudioExportPreset preset) => preset switch {
        StudioExportPreset.EclipsaAudioIamf => ".iamf",
        StudioExportPreset.AmbisonicsHoa3rdOrder => ".amb",
        _ => ".wav"
    };

    /// <summary>
    /// Returns the recommended native file extension for a W48 <see cref="StudioBaseOutputFormat"/>.
    /// </summary>
    public static string GetDefaultExtension(StudioBaseOutputFormat format) => format switch {
        StudioBaseOutputFormat.EclipsaIamf => ".iamf",
        StudioBaseOutputFormat.Ambisonics360 => ".amb",
        _ => ".wav"
    };

    /// <summary>
    /// Returns true when <paramref name="baseFormat"/> is <see cref="StudioBaseOutputFormat.DolbyAtmosAdm"/>
    /// and a compressed <paramref name="tier"/> was requested (ITU-R BS.2076 ADM mandates uncompressed PCM).
    /// </summary>
    public static bool IsAdmCompressionLocked(StudioBaseOutputFormat baseFormat, StudioCompressionTier tier) =>
        baseFormat == StudioBaseOutputFormat.DolbyAtmosAdm && tier != StudioCompressionTier.MasterPcm;

    /// <summary>
    /// Resolves the effective output file extension for a <see cref="StudioBaseOutputFormat"/> and <see cref="StudioCompressionTier"/>.
    /// </summary>
    public static string GetEffectiveOutputExtension(
        StudioBaseOutputFormat baseFormat,
        StudioCompressionTier tier,
        bool isDolbyAtmosAdmSubset = true) {
        if (baseFormat == StudioBaseOutputFormat.DolbyAtmosAdm) {
            return ".wav";
        }
        if (baseFormat == StudioBaseOutputFormat.EclipsaIamf) {
            return ".iamf";
        }
        return tier switch {
            StudioCompressionTier.LosslessCompressed => baseFormat == StudioBaseOutputFormat.Headphone3D ? ".flac" : ".wv",
            StudioCompressionTier.PortableLossy => ".opus",
            _ => baseFormat == StudioBaseOutputFormat.Ambisonics360 ? ".amb" : ".wav"
        };
    }

    /// <summary>
    /// Returns a human-readable compression/container specification summary for UI previews and telemetry.
    /// </summary>
    public static string GetCompressionSpecSummary(StudioBaseOutputFormat baseFormat, StudioCompressionTier tier) {
        if (baseFormat == StudioBaseOutputFormat.DolbyAtmosAdm) {
            return "PCM 24-bit WAV (ITU-R BS.2076 ADM mandates uncompressed PCM)";
        }
        return (baseFormat, tier) switch {
            (StudioBaseOutputFormat.Headphone3D, StudioCompressionTier.MasterPcm) => "Lossless PCM WAV (.wav)",
            (StudioBaseOutputFormat.Headphone3D, StudioCompressionTier.LosslessCompressed) => "Lossless FLAC (.flac)",
            (StudioBaseOutputFormat.Headphone3D, StudioCompressionTier.PortableLossy) => "Portable Opus (.opus)",
            (StudioBaseOutputFormat.Cinema714, StudioCompressionTier.MasterPcm) => "Lossless 12ch PCM WAV (.wav)",
            (StudioBaseOutputFormat.Cinema714, StudioCompressionTier.LosslessCompressed) => "Lossless 12ch WavPack (.wv)",
            (StudioBaseOutputFormat.Cinema714, StudioCompressionTier.PortableLossy) => "Portable 12ch Opus (.opus)",
            (StudioBaseOutputFormat.EclipsaIamf, StudioCompressionTier.MasterPcm) => "Eclipsa IAMF LPCM (.iamf)",
            (StudioBaseOutputFormat.EclipsaIamf, StudioCompressionTier.LosslessCompressed) => "Eclipsa IAMF FLAC (.iamf)",
            (StudioBaseOutputFormat.EclipsaIamf, StudioCompressionTier.PortableLossy) => "Eclipsa IAMF Opus (.iamf)",
            (StudioBaseOutputFormat.Ambisonics360, StudioCompressionTier.MasterPcm) => "3rd-Order HOA PCM WAVE-EX (.amb)",
            (StudioBaseOutputFormat.Ambisonics360, StudioCompressionTier.LosslessCompressed) => "Lossless 16ch HOA WavPack (.wv)",
            (StudioBaseOutputFormat.Ambisonics360, StudioCompressionTier.PortableLossy) => "Portable HOA Opus RFC 8486 (.opus)",
            _ => "Lossless PCM WAV (.wav)"
        };
    }

    /// <summary>
    /// Maps a W12 <see cref="StudioExportPreset"/> to its canonical <see cref="Codec"/> identifier.
    /// </summary>
    public static Codec GetPrimaryCodec(StudioExportPreset preset, bool atmosSubset = true) => preset switch {
        StudioExportPreset.EclipsaAudioIamf => Codec.IAMF,
        StudioExportPreset.AmbisonicsHoa3rdOrder => Codec.AmbisonicsHOA,
        StudioExportPreset.AdmBwfItuBs2076 => atmosSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF,
        _ => Codec.PCM_LE
    };

    static StudioBaseOutputFormat ResolveBaseFormat(StudioBatchExportJob job) {
        if (job.ComposableOptions != null) {
            return job.ComposableOptions.BaseFormat;
        }
        return job.Preset switch {
            StudioExportPreset.EclipsaAudioIamf => StudioBaseOutputFormat.EclipsaIamf,
            StudioExportPreset.AmbisonicsHoa3rdOrder => StudioBaseOutputFormat.Ambisonics360,
            StudioExportPreset.AdmBwfItuBs2076 => StudioBaseOutputFormat.DolbyAtmosAdm,
            StudioExportPreset.HybridNeuralDirAc714 => StudioBaseOutputFormat.Cinema714,
            StudioExportPreset.FlacAtmosHybridSpatialMaster => StudioBaseOutputFormat.Cinema714,
            _ => StudioBaseOutputFormat.Headphone3D
        };
    }

    static StudioCompressionTier ResolveEffectiveTier(StudioBatchExportJob job, StudioBaseOutputFormat? baseFormatOverride = null) {
        StudioBaseOutputFormat fmt = baseFormatOverride ?? ResolveBaseFormat(job);
        if (fmt == StudioBaseOutputFormat.DolbyAtmosAdm) {
            return StudioCompressionTier.MasterPcm;
        }
        if (job.ComposableOptions != null && job.ComposableOptions.CompressionTier != StudioCompressionTier.MasterPcm) {
            return job.ComposableOptions.CompressionTier;
        }
        return job.CompressionTier;
    }

    static string ResolveFfmpegPath(StudioBatchExportJob job) =>
        !string.IsNullOrWhiteSpace(job.ComposableOptions?.FfmpegExecutablePath)
            ? job.ComposableOptions.FfmpegExecutablePath
            : job.FfmpegExecutablePath;

    static SpatialChoreoTrack ResolveChoreoTrack(StudioBatchExportJob job) =>
        job.ComposableOptions?.ChoreoTrack ?? job.ChoreoTrack;

    static SpatialChoreoMode ResolveDynamicChoreoMode(StudioBatchExportJob job) {
        if (job.ComposableOptions != null && job.ComposableOptions.DynamicChoreoMode != SpatialChoreoMode.Off) {
            return job.ComposableOptions.DynamicChoreoMode;
        }
        if (job.DynamicChoreoMode != SpatialChoreoMode.Off) {
            return job.DynamicChoreoMode;
        }
        return ResolveChoreoTrack(job)?.Mode ?? SpatialChoreoMode.Off;
    }

    static IReadOnlyList<LyricMoodDigest> ResolveLyricMoodDigests(StudioBatchExportJob job) =>
        job.ComposableOptions?.LyricMoodDigests ?? job.LyricMoodDigests;

    static ExportMetadataPackage ResolveMetadata(StudioBatchExportJob job) =>
        job.ComposableOptions?.Metadata ?? job.Metadata;

    static SpatialChoreoTrack ResolveOrBuildChoreoTrack(
        StudioBatchExportJob job,
        IReadOnlyList<StereoStemBuffer> stems,
        int sampleRate,
        double durationSec) {
        SpatialChoreoMode mode = ResolveDynamicChoreoMode(job);
        if (mode == SpatialChoreoMode.Off) {
            return null;
        }
        SpatialChoreoTrack existing = ResolveChoreoTrack(job);
        if (existing != null &&
            existing.Mode == mode &&
            existing.StemCount > 0 &&
            (stems == null || stems.Count == 0 || existing.StemCount == stems.Count)) {
            return existing;
        }
        if (stems != null && stems.Count > 0) {
            return NeuralStemSpatializer.BuildChoreoTrack(stems, sampleRate, mode, ResolveLyricMoodDigests(job));
        }
        return BuildDeterministicFallbackChoreoTrack(mode, durationSec);
    }

    static SpatialChoreoTrack BuildDeterministicFallbackChoreoTrack(SpatialChoreoMode mode, double durationSec) {
        if (mode == SpatialChoreoMode.Off) {
            return null;
        }
        double safeDuration = Math.Max(1.0, durationSec);
        string[] roles = ["Vocals", "Drums", "Other", "BackingVocals"];
        NeuralStemRole[] roleEnums = [NeuralStemRole.Vocals, NeuralStemRole.Drums, NeuralStemRole.Other, NeuralStemRole.BackingVocals];
        int steps = Math.Clamp((int)Math.Ceiling(safeDuration / 1.5), 4, 24);
        List<DirectorCue> cues = new(steps * roles.Length);
        float azSpan = mode switch {
            SpatialChoreoMode.Subtle => 28f,
            SpatialChoreoMode.Musical => 62f,
            SpatialChoreoMode.Cinematic => 125f,
            SpatialChoreoMode.Orbit => 180f,
            _ => 0f
        };
        float elSpan = mode switch {
            SpatialChoreoMode.Subtle => 14f,
            SpatialChoreoMode.Musical => 28f,
            SpatialChoreoMode.Cinematic => 48f,
            SpatialChoreoMode.Orbit => 40f,
            _ => 0f
        };

        for (int k = 0; k < steps; k++) {
            double t = (safeDuration * k) / Math.Max(1, steps - 1);
            float phase = (float)(k * (2.0 * Math.PI / Math.Max(4, steps)));
            for (int r = 0; r < roles.Length; r++) {
                float roleOffset = r * 1.15f;
                float az = mode == SpatialChoreoMode.Orbit
                    ? (r == 0 ? 18f * MathF.Sin(phase) : ((k * 360f / Math.Max(4, steps) + r * 90f) % 360f) - 180f)
                    : (r == 0 ? 12f * MathF.Sin(phase) : azSpan * MathF.Sin(phase + roleOffset));
                float el = r == 0
                    ? 6f + 6f * MathF.Sin(phase * 0.5f)
                    : MathF.Max(0f, 10f + elSpan * (0.5f + 0.5f * MathF.Cos(phase + roleOffset)));
                float radius = r == 0 ? 0.92f : 0.85f;
                cues.Add(new DirectorCue(t, roleEnums[r], az, el, radius, 0.72f));
            }
        }
        return SpatialChoreoTrack.Build(roles, cues.ToArray(), mode, safeDuration);
    }

    static Action ResolveSigpipeShield(StudioBatchExportJob job) =>
        job.ComposableOptions?.OnSigpipeShield ?? job.OnSigpipeShield;

    static void TryDeletePartialOutput(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Best-effort cleanup of incomplete export artifact
        }
    }

    static long SafeFileLength(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                return new FileInfo(path).Length;
            }
        } catch {
            // Ignore
        }
        return 0L;
    }

    static IStreamingExportSink CreateStreamingSink(
        string outPath,
        ReferenceChannel[] layout,
        int channelCount,
        int sampleRate,
        BitDepth bits,
        long expectedFrames,
        StudioCompressionTier tier,
        string ffmpegPath,
        Action onSigpipeShield,
        bool isAmbisonics = false,
        ExportMetadataPackage metadata = null) {
        if (tier == StudioCompressionTier.LosslessCompressed) {
            bool wantsWavPack = channelCount > 8 ||
                                outPath.EndsWith(".wv", StringComparison.OrdinalIgnoreCase);
            if (!wantsWavPack) {
                return new DirectFlacStreamingSink(outPath, channelCount, sampleRate, bits);
            }
            return new FfmpegStdinPipeSink(
                ffmpegPath,
                outPath,
                channelCount,
                sampleRate,
                FfmpegPipeCodec.WavPack,
                onSigpipeShield,
                isAmbisonics,
                metadata: metadata);
        }

        if (tier == StudioCompressionTier.PortableLossy) {
            FfmpegPipeCodec lossyCodec = outPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                ? FfmpegPipeCodec.AacM4a
                : FfmpegPipeCodec.Opus;
            return new FfmpegStdinPipeSink(
                ffmpegPath,
                outPath,
                channelCount,
                sampleRate,
                lossyCodec,
                onSigpipeShield,
                isAmbisonics,
                metadata: metadata);
        }

        return new DirectPcmStreamingSink(
            outPath,
            channelCount,
            sampleRate,
            bits,
            layout,
            expectedFrames,
            useAmbisonicSubFormatGuid: isAmbisonics);
    }

    sealed class ChoreoSourceMapper {
        readonly SpatialChoreoTrack track;
        readonly int[] stemMap;
        readonly float[] sideSigns;
        readonly bool applyStereoSpread;

        public ChoreoSourceMapper(
            IReadOnlyCollection<Source> sources,
            SpatialChoreoTrack choreo,
            int[] explicitStemMap = null,
            float[] explicitSideSigns = null) {
            track = choreo;
            if (choreo == null || choreo.StemCount <= 0 || choreo.Mode == SpatialChoreoMode.Off || sources == null || sources.Count == 0) {
                stemMap = Array.Empty<int>();
                sideSigns = Array.Empty<float>();
                return;
            }

            if (explicitStemMap != null && explicitStemMap.Length == sources.Count) {
                stemMap = new int[sources.Count];
                sideSigns = new float[sources.Count];
                int idxEx = 0;
                foreach (Source s in sources) {
                    if (s == null || s.LFE || s.PositionLocked || explicitStemMap[idxEx] < 0) {
                        stemMap[idxEx] = -1;
                        sideSigns[idxEx] = 0f;
                    } else {
                        stemMap[idxEx] = explicitStemMap[idxEx] % choreo.StemCount;
                        sideSigns[idxEx] = explicitSideSigns != null && idxEx < explicitSideSigns.Length ? explicitSideSigns[idxEx] : 0f;
                    }
                    idxEx++;
                }
                applyStereoSpread = true;
                return;
            }

            stemMap = new int[sources.Count];
            sideSigns = new float[sources.Count];
            int nonLfeCount = 0;
            foreach (Source s in sources) {
                if (s != null && !s.LFE && !s.PositionLocked) {
                    nonLfeCount++;
                }
            }
            applyStereoSpread = nonLfeCount > choreo.StemCount;

            int nonLfeOrd = 0;
            int idx = 0;
            foreach (Source s in sources) {
                if (s == null || s.LFE || s.PositionLocked) {
                    stemMap[idx++] = -1;
                    continue;
                }
                stemMap[idx] = nonLfeOrd % choreo.StemCount;
                sideSigns[idx] = s.Position.X < -0.05f ? -1f : (s.Position.X > 0.05f ? 1f : 0f);
                nonLfeOrd++;
                idx++;
            }
        }

        public void ApplyFrame(IReadOnlyList<Source> sources, double timeSec) {
            if (track == null || stemMap.Length == 0 || sources == null) {
                return;
            }
            int count = Math.Min(sources.Count, stemMap.Length);
            Vector3 env = Listener.EnvironmentSize;
            for (int i = 0; i < count; i++) {
                int stemIdx = stemMap[i];
                if (stemIdx < 0) {
                    continue;
                }
                Source src = sources[i];
                if (src == null || src.PositionLocked) {
                    continue;
                }
                if (track.EvaluatePositionZeroAlloc(stemIdx, timeSec, out Vector3 normPos)) {
                    if (applyStereoSpread && MathF.Abs(sideSigns[i]) > 0.01f) {
                        float horizSq = normPos.X * normPos.X + normPos.Z * normPos.Z;
                        if (horizSq > 1e-6f) {
                            float invH = 1f / MathF.Sqrt(horizSq);
                            Vector3 tangent = new(normPos.Z * invH, 0f, -normPos.X * invH);
                            normPos += tangent * (0.20f * sideSigns[i]);
                            float len = normPos.Length();
                            if (len > 1f) {
                                normPos /= len;
                            }
                        }
                    }
                    src.Position = normPos * env;
                }
            }
        }
    }

    /// <summary>
    /// Executes a single W12/W48 Studio export job against an attached <see cref="CavernizeTrack"/> and <see cref="Listener"/>
    /// while isolating and restoring global static <see cref="Listener"/> state in <c>finally</c>.
    /// </summary>
    public static StudioBatchExportResult ExportSingle(
        CavernizeTrack track,
        StudioBatchExportJob job,
        Action<double> onProgress = null,
        CancellationToken token = default) {
        if (job == null) {
            throw new ArgumentNullException(nameof(job));
        }
        StudioBaseOutputFormat baseFmt = ResolveBaseFormat(job);
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, baseFmt);
        if (track == null || !track.Supported) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                CompressionTier = effectiveTier,
                Succeeded = false,
                ErrorMessage = "No supported active track for Studio batch export."
            };
        }

        string outPath = job.OutputPath;
        if (string.IsNullOrWhiteSpace(outPath)) {
            string ext = GetEffectiveOutputExtension(baseFmt, effectiveTier, job.UseDolbyAtmosAdmSubset);
            string tag = job.ComposableOptions != null
                ? job.ComposableOptions.BaseFormat.ToString()
                : job.Preset.ToString();
            outPath = $"{job.TrackName}_{tag}{ext}";
        }
        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        using ListenerContext.Scope listenerScope = BeginIsolatedExportScope();
        try {
            token.ThrowIfCancellationRequested();
            StudioBatchExportResult result;
            if (job.ComposableOptions != null) {
                result = ExportComposable(track, job, job.ComposableOptions, outPath, onProgress, token);
            } else {
                result = job.Preset switch {
                    StudioExportPreset.EclipsaAudioIamf => ExportEnvironmental(track, job, Codec.IAMF, outPath, "Eclipsa Audio IAMF v1.0 OBU (3rd-Order HOA / LPCM)", onProgress, token),
                    StudioExportPreset.AmbisonicsHoa3rdOrder => ExportEnvironmental(track, job, Codec.AmbisonicsHOA, outPath, $"Ambisonics HOA Order {Math.Clamp(job.HoaOrder, 1, 3)} (ACN/SN3D WAVE-EX)", onProgress, token),
                    StudioExportPreset.AdmBwfItuBs2076 => ExportEnvironmental(track, job, job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF, outPath, job.UseDolbyAtmosAdmSubset ? "ADM BWF Dolby Atmos (ITU-R BS.2076)" : "ADM BWF Compact (ITU-R BS.2076)", onProgress, token),
                    StudioExportPreset.PersonalizedLosslessBinaural => ExportPersonalizedBinaural(track, job, outPath, onProgress, token),
                    StudioExportPreset.HybridNeuralDirAc714 => ExportHybrid714(track, job, outPath, onProgress, token),
                    StudioExportPreset.SpatialKaraokeInstrumental => ExportSpatialKaraoke(track, job, outPath, includeLiveVocalMixdown: false, onProgress, token),
                    StudioExportPreset.LiveKaraokePerformanceMixdown => ExportSpatialKaraoke(track, job, outPath, includeLiveVocalMixdown: true, onProgress, token),
                    StudioExportPreset.FlacAtmosHybridSpatialMaster => ExportFlacAtmosHybridMaster(track, job, outPath, onProgress, token),
                    _ => throw new NotSupportedException($"Unsupported Studio export preset: {job.Preset}")
                };
            }
            return FinalizeExportResultWithMetadata(result, job);
        } catch (OperationCanceledException) {
            TryDeletePartialOutput(outPath);
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                CompressionTier = effectiveTier,
                IsCancelled = true,
                OutputBytes = 0,
                Succeeded = false,
                ErrorMessage = "Export cancelled."
            };
        } catch (Exception ex) {
            TryDeletePartialOutput(outPath);
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                CompressionTier = effectiveTier,
                OutputBytes = 0,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    static StudioBatchExportResult FinalizeExportResultWithMetadata(StudioBatchExportResult res, StudioBatchExportJob job) {
        if (res == null || !res.Succeeded || string.IsNullOrWhiteSpace(res.OutputPath) || !File.Exists(res.OutputPath)) {
            return res;
        }
        ExportMetadataPackage meta = ResolveMetadata(job);
        SpatialChoreoMode effectiveMode = res.EffectiveChoreoMode != SpatialChoreoMode.Off
            ? res.EffectiveChoreoMode
            : ResolveDynamicChoreoMode(job);
        bool embeddedTags = res.MetadataEmbedded;
        bool embeddedCover = res.CoverArtEmbedded;
        bool embeddedLyrics = res.LyricsEmbedded;

        if (meta != null && meta.HasAnythingToEmbed) {
            string ext = Path.GetExtension(res.OutputPath).ToLowerInvariant();
            if (ext is ".flac" or ".wav" or ".amb") {
                try {
                    ExportMetadataEmbedResult embedRes = ExportMetadataEmbedder.EmbedInPlace(res.OutputPath, meta);
                    embeddedTags |= embedRes.EmbeddedTags;
                    embeddedCover |= embedRes.EmbeddedCoverArt;
                    embeddedLyrics |= embedRes.EmbeddedLyrics;
                } catch {
                    // Metadata post-embed is strictly non-fatal so rendered audio is never lost
                }
            } else if (ext is ".opus" or ".wv" or ".m4a") {
                embeddedTags |= meta.HasAnyTextTags;
                embeddedCover |= (ext is ".opus" or ".m4a") && meta.HasCoverArt;
                embeddedLyrics |= meta.HasLyrics;
            }
        }

        return new StudioBatchExportResult {
            Preset = res.Preset,
            OutputPath = res.OutputPath,
            FormatSummary = res.FormatSummary,
            ChannelCount = res.ChannelCount,
            SampleRate = res.SampleRate,
            DurationSeconds = res.DurationSeconds,
            IntegratedLufs = res.IntegratedLufs,
            LoudnessRangeLu = res.LoudnessRangeLu,
            MaxTruePeakDbtp = res.MaxTruePeakDbtp,
            Succeeded = res.Succeeded,
            ErrorMessage = res.ErrorMessage,
            IsCancelled = res.IsCancelled,
            OutputBytes = SafeFileLength(res.OutputPath),
            CompressionTier = res.CompressionTier,
            MetadataEmbedded = embeddedTags,
            CoverArtEmbedded = embeddedCover,
            LyricsEmbedded = embeddedLyrics,
            EffectiveChoreoMode = effectiveMode
        };
    }

    /// <summary>
    /// Executes a batch list of W12 Studio export jobs sequentially with aggregate progress reporting.
    /// </summary>
    public static List<StudioBatchExportResult> ExportBatch(
        CavernizeTrack track,
        IReadOnlyList<StudioBatchExportJob> jobs,
        Action<int, int, double> onProgress = null,
        CancellationToken token = default) {
        if (jobs == null || jobs.Count == 0) {
            return [];
        }
        List<StudioBatchExportResult> results = new(jobs.Count);
        for (int i = 0; i < jobs.Count; i++) {
            StudioBatchExportJob job = jobs[i];
            try {
                token.ThrowIfCancellationRequested();
                int index = i;
                StudioBatchExportResult result = ExportSingle(
                    track,
                    job,
                    p => onProgress?.Invoke(index, jobs.Count, p),
                    token);
                results.Add(result);
                if (result.IsCancelled || token.IsCancellationRequested) {
                    break;
                }
            } catch (OperationCanceledException) {
                TryDeletePartialOutput(job?.OutputPath);
                results.Add(new StudioBatchExportResult {
                    Preset = job?.Preset ?? StudioExportPreset.EclipsaAudioIamf,
                    OutputPath = job?.OutputPath ?? string.Empty,
                    CompressionTier = job != null ? ResolveEffectiveTier(job) : StudioCompressionTier.MasterPcm,
                    IsCancelled = true,
                    OutputBytes = 0,
                    Succeeded = false,
                    ErrorMessage = "Export cancelled."
                });
                break;
            }
        }
        return results;
    }

    static LoudnessAnalysisSnapshot ApplyTruePeakLimiterAndMeasureInPlace(
        float[][] channels,
        ReferenceChannel[] layout,
        int sampleRate,
        float ceilingDbtp,
        CancellationToken token) {
        if (channels == null || channels.Length == 0 || channels[0] == null || channels[0].Length == 0) {
            return LoudnessAnalysisSnapshot.Empty();
        }
        int chCount = channels.Length;
        int len = channels[0].Length;
        LoudnessMeter meter = new(sampleRate, chCount);
        double[] weights = new double[chCount];
        for (int c = 0; c < chCount; c++) {
            weights[c] = (layout != null && c < layout.Length && layout[c] == ReferenceChannel.ScreenLFE) ? 0.0 : 1.0;
        }
        meter.Configure(sampleRate, chCount, weights);
        TruePeakLimiter limiter = new(sampleRate, chCount, ceilingDbtp);

        float[] interleaved = new float[updateRate * chCount];
        for (int pos = 0; pos < len; pos += updateRate) {
            token.ThrowIfCancellationRequested();
            int frames = Math.Min(updateRate, len - pos);
            for (int s = 0; s < frames; s++) {
                int baseIdx = s * chCount;
                for (int c = 0; c < chCount; c++) {
                    interleaved[baseIdx + c] = channels[c][pos + s];
                }
            }
            limiter.ProcessInterleaved(interleaved, 0, frames, chCount);
            meter.AnalyzeInterleaved(interleaved, 0, frames, chCount);
            for (int s = 0; s < frames; s++) {
                int baseIdx = s * chCount;
                for (int c = 0; c < chCount; c++) {
                    channels[c][pos + s] = interleaved[baseIdx + c];
                }
            }
        }
        return meter.Snapshot;
    }

    static LoudnessAnalysisSnapshot WriteMeteredLimitedMultichannelStream(
        string outPath,
        ReferenceChannel[] layout,
        float[][] channels,
        int sampleRate,
        BitDepth bits,
        float ceilingDbtp,
        StudioCompressionTier tier,
        string ffmpegPath,
        Action onSigpipe,
        Action<double> onProgress,
        CancellationToken token,
        bool releaseChannelsAfterStream = false,
        ExportMetadataPackage metadata = null) {
        int chCount = Math.Min(layout.Length, channels.Length);
        int len = chCount > 0 && channels[0] != null ? channels[0].Length : 0;
        LoudnessMeter meter = new(sampleRate, Math.Max(1, chCount));
        double[] weights = new double[Math.Max(1, chCount)];
        for (int c = 0; c < chCount; c++) {
            weights[c] = layout[c] == ReferenceChannel.ScreenLFE ? 0.0 : 1.0;
        }
        meter.Configure(sampleRate, Math.Max(1, chCount), weights);
        TruePeakLimiter limiter = new(sampleRate, Math.Max(1, chCount), ceilingDbtp);

        using (IStreamingExportSink sink = CreateStreamingSink(
            outPath,
            layout[..chCount],
            chCount,
            sampleRate,
            bits,
            len,
            tier,
            ffmpegPath,
            onSigpipe,
            isAmbisonics: false,
            metadata: metadata)) {
            float[] interleaved = new float[updateRate * Math.Max(1, chCount)];
            int totalBlocks = Math.Max(1, (len + updateRate - 1) / updateRate);
            int blockIdx = 0;
            for (int pos = 0; pos < len; pos += updateRate) {
                token.ThrowIfCancellationRequested();
                int frames = Math.Min(updateRate, len - pos);
                for (int s = 0; s < frames; s++) {
                    int baseIdx = s * chCount;
                    for (int c = 0; c < chCount; c++) {
                        interleaved[baseIdx + c] = channels[c][pos + s];
                    }
                }
                limiter.ProcessInterleaved(interleaved, 0, frames, chCount);
                meter.AnalyzeInterleaved(interleaved, 0, frames, chCount);
                sink.WriteInterleavedBlock(interleaved, frames);
                blockIdx++;
                if ((blockIdx & 15) == 0 || blockIdx >= totalBlocks) {
                    onProgress?.Invoke(Math.Clamp(blockIdx / (double)totalBlocks, 0.0, 1.0));
                }
            }
        }
        if (releaseChannelsAfterStream && channels != null) {
            for (int c = 0; c < channels.Length; c++) {
                channels[c] = null;
            }
        }
        return meter.Snapshot;
    }

    static void DecodeStereoInput(
        CavernizeTrack track,
        StudioBatchExportJob job,
        out float[] left,
        out float[] right,
        out bool derivedFromTwin,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref200));
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(false));

        long totalLength = track.Length > 0 ? track.Length : sampleRate * 600L;
        int len = (int)Math.Min(totalLength, int.MaxValue / 4);
        left = new float[len];
        right = new float[len];
        int stride = Math.Max(2, Listener.Channels.Length);
        int written = 0;
        int iter = 0;
        while (written < len) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int avail = Math.Min(updateRate, len - written);
            for (int s = 0; s < avail; s++) {
                left[written + s] = Math.Clamp(rendered[s * stride], -1f, 1f);
                right[written + s] = Math.Clamp(rendered[s * stride + 1], -1f, 1f);
            }
            written += avail;
            if ((++iter & 15) == 0 || written >= len) {
                onProgress?.Invoke(Math.Clamp(written / (double)Math.Max(1, len), 0.0, 1.0));
            }
        }

        (left, right, derivedFromTwin) = ResolveNeuralStereoInput(job, left, right, len);
    }

    static void DecodeMultichannelLayout(
        CavernizeTrack track,
        ReferenceChannel[] layout,
        out float[][] channels,
        out bool[] isLfe,
        out float[] mixLeft,
        out float[] mixRight,
        Action<double> onProgress,
        CancellationToken token,
        UpmixingSettings upmixOverride = null) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(layout));
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };
        track.Attach(listener, upmixOverride ?? new UpmixingSettings(false));

        int chCount = layout.Length;
        long totalLength = track.Length > 0 ? track.Length : sampleRate * 600L;
        int len = (int)Math.Min(totalLength, int.MaxValue / Math.Max(4, chCount));
        channels = new float[chCount][];
        isLfe = new bool[chCount];
        for (int c = 0; c < chCount; c++) {
            channels[c] = new float[len];
            isLfe[c] = layout[c] == ReferenceChannel.ScreenLFE;
        }
        mixLeft = new float[len];
        mixRight = new float[len];

        int stride = Math.Max(chCount, Listener.Channels.Length);
        int written = 0;
        int iter = 0;
        while (written < len) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int avail = Math.Min(updateRate, len - written);
            for (int s = 0; s < avail; s++) {
                int baseIdx = s * stride;
                float lSum = 0f;
                float rSum = 0f;
                for (int c = 0; c < chCount; c++) {
                    float v = Math.Clamp(rendered[baseIdx + c], -1f, 1f);
                    channels[c][written + s] = v;
                    if (!isLfe[c]) {
                        ReferenceChannel rc = layout[c];
                        if (rc == ReferenceChannel.FrontCenter || rc == ReferenceChannel.RearCenter) {
                            lSum += v * 0.70710678f;
                            rSum += v * 0.70710678f;
                        } else if (rc == ReferenceChannel.FrontLeft || rc == ReferenceChannel.SideLeft ||
                                   rc == ReferenceChannel.RearLeft || rc == ReferenceChannel.TopFrontLeft ||
                                   rc == ReferenceChannel.TopRearLeft || rc == ReferenceChannel.TopSideLeft) {
                            lSum += v;
                        } else if (rc == ReferenceChannel.FrontRight || rc == ReferenceChannel.SideRight ||
                                   rc == ReferenceChannel.RearRight || rc == ReferenceChannel.TopFrontRight ||
                                   rc == ReferenceChannel.TopRearRight || rc == ReferenceChannel.TopSideRight) {
                            rSum += v;
                        } else {
                            lSum += v * 0.5f;
                            rSum += v * 0.5f;
                        }
                    }
                }
                mixLeft[written + s] = Math.Clamp(lSum, -1f, 1f);
                mixRight[written + s] = Math.Clamp(rSum, -1f, 1f);
            }
            written += avail;
            if ((++iter & 15) == 0 || written >= len) {
                onProgress?.Invoke(Math.Clamp(written / (double)Math.Max(1, len), 0.0, 1.0));
            }
        }
    }

    static (float[] Left, float[] Right, bool FromTwin) ResolveNeuralStereoInput(
        StudioBatchExportJob job,
        float[] fallbackLeft,
        float[] fallbackRight,
        int totalSamples) {
        if (job.LosslessTwinProvider != null && job.LosslessTwinProvider.HasSyncedLosslessTwin && totalSamples > 0) {
            if (job.LosslessTwinProvider is UniversalLosslessTwinProvider universalTwin &&
                universalTwin.TryExtractFullAlignedStereo(totalSamples, out float[] alignedL, out float[] alignedR)) {
                return (alignedL, alignedR, true);
            }
            float[] alignedL2 = new float[totalSamples];
            float[] alignedR2 = new float[totalSamples];
            if (job.LosslessTwinProvider.TryReadAlignedStereoBlock(0L, alignedL2, alignedR2, totalSamples)) {
                return (alignedL2, alignedR2, true);
            }
        }
        return (fallbackLeft, fallbackRight, false);
    }

    static ReferenceChannel[] ResolveExportChannelLayout(CavernizeTrack track, int sourceCount) {
        if (track.Renderer != null && track.Renderer.HasObjects) {
            return Layout714;
        }
        int clamped = Math.Clamp(sourceCount, 2, 12);
        return clamped >= 12 ? Layout714 : ChannelPrototype.GetStandardMatrix(clamped);
    }

    static float ResolveMeasuredLufs(LoudnessAnalysisSnapshot snap) {
        if (!float.IsNaN(snap.IntegratedLufs)) {
            return snap.IntegratedLufs;
        }
        if (!float.IsNaN(snap.ShortTermLufs)) {
            return snap.ShortTermLufs;
        }
        if (!float.IsNaN(snap.MomentaryLufs)) {
            return snap.MomentaryLufs;
        }
        return -120f;
    }

    static (long FileSize, long LastModifiedTicks) GetSourceFileStamp(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                FileInfo fi = new(path);
                return (fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
        } catch { }
        return (0L, 0L);
    }

    static void ResolveStereoLeadVocalRefs(NeuralSeparatedTrack separated, out float[] leadL, out float[] leadR) {
        leadL = separated?.MonoLeadVocalRef;
        leadR = null;
        if (separated?.Stems == null || separated.Stems.Count == 0) {
            return;
        }
        StereoStemBuffer fallback = null;
        for (int i = 0; i < separated.Stems.Count; i++) {
            StereoStemBuffer st = separated.Stems[i];
            if (st == null || st.Length <= 0 || st.Left == null || st.Right == null) {
                continue;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
            if (role == NeuralStemRole.LeadVocal) {
                leadL = st.Left;
                leadR = st.Right;
                return;
            }
            if (role == NeuralStemRole.Vocals && fallback == null) {
                fallback = st;
            }
        }
        if (fallback != null) {
            leadL = fallback.Left;
            leadR = fallback.Right;
        }
    }

}
