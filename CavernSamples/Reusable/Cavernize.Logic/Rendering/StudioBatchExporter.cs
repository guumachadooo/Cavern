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
}

/// <summary>
/// Wave 12 unified offline Studio Batch Exporter supporting all 8 spatial mastering workflows:
/// Personalized Lossless Binaural, Eclipsa Audio (IAMF), 3rd-Order Ambisonics HOA, ADM BWF (ITU BS.2076),
/// Hybrid Neural+DirAC Stereo->7.1.4, Spatial Karaoke Atmos, Live Karaoke Performance Mixdown,
/// and FLAC+Atmos Hybrid Lossless Spatial Master.
/// </summary>
public static class StudioBatchExporter {
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
    /// Maps a W12 <see cref="StudioExportPreset"/> to its canonical <see cref="Codec"/> identifier.
    /// </summary>
    public static Codec GetPrimaryCodec(StudioExportPreset preset, bool atmosSubset = true) => preset switch {
        StudioExportPreset.EclipsaAudioIamf => Codec.IAMF,
        StudioExportPreset.AmbisonicsHoa3rdOrder => Codec.AmbisonicsHOA,
        StudioExportPreset.AdmBwfItuBs2076 => atmosSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF,
        _ => Codec.PCM_LE
    };

    /// <summary>
    /// Executes a single W12/W48 Studio export job against an attached <see cref="CavernizeTrack"/> and <see cref="Listener"/>
    /// while isolating and restoring global static <see cref="Listener"/> state in <c>finally</c>.
    /// </summary>
    public static StudioBatchExportResult ExportSingle(
        CavernizeTrack track,
        StudioBatchExportJob job,
        Action<double> onProgress = null,
        CancellationToken token = default) {
        if (track == null || !track.Supported) {
            return new StudioBatchExportResult {
                Preset = job?.Preset ?? StudioExportPreset.EclipsaAudioIamf,
                Succeeded = false,
                ErrorMessage = "No supported active track for Studio batch export."
            };
        }
        if (job == null) {
            throw new ArgumentNullException(nameof(job));
        }

        string outPath = job.OutputPath;
        if (string.IsNullOrWhiteSpace(outPath)) {
            string ext = job.ComposableOptions != null
                ? GetDefaultExtension(job.ComposableOptions.BaseFormat)
                : GetDefaultExtension(job.Preset);
            string tag = job.ComposableOptions != null
                ? job.ComposableOptions.BaseFormat.ToString()
                : job.Preset.ToString();
            outPath = $"{job.TrackName}_{tag}{ext}";
        }
        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        using StemExtractor.ListenerStateScope listenerScope = new();
        try {
            if (job.ComposableOptions != null) {
                return ExportComposable(track, job, job.ComposableOptions, outPath, onProgress, token);
            }
            switch (job.Preset) {
                case StudioExportPreset.EclipsaAudioIamf:
                    return ExportEnvironmental(track, job, Codec.IAMF, outPath, "Eclipsa Audio IAMF v1.0 OBU (3rd-Order HOA / LPCM)", onProgress, token);
                case StudioExportPreset.AmbisonicsHoa3rdOrder:
                    return ExportEnvironmental(track, job, Codec.AmbisonicsHOA, outPath, $"Ambisonics HOA Order {Math.Clamp(job.HoaOrder, 1, 3)} (ACN/SN3D WAVE-EX)", onProgress, token);
                case StudioExportPreset.AdmBwfItuBs2076:
                    Codec admCodec = job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF;
                    return ExportEnvironmental(track, job, admCodec, outPath, job.UseDolbyAtmosAdmSubset ? "ADM BWF Dolby Atmos (ITU-R BS.2076)" : "ADM BWF Compact (ITU-R BS.2076)", onProgress, token);
                case StudioExportPreset.PersonalizedLosslessBinaural:
                    return ExportPersonalizedBinaural(track, job, outPath, onProgress, token);
                case StudioExportPreset.HybridNeuralDirAc714:
                    return ExportHybrid714(track, job, outPath, onProgress, token);
                case StudioExportPreset.SpatialKaraokeInstrumental:
                    return ExportSpatialKaraoke(track, job, outPath, includeLiveVocalMixdown: false, onProgress, token);
                case StudioExportPreset.LiveKaraokePerformanceMixdown:
                    return ExportSpatialKaraoke(track, job, outPath, includeLiveVocalMixdown: true, onProgress, token);
                case StudioExportPreset.FlacAtmosHybridSpatialMaster:
                    return ExportFlacAtmosHybridMaster(track, job, outPath, onProgress, token);
                default:
                    throw new NotSupportedException($"Unsupported Studio export preset: {job.Preset}");
            }
        } catch (OperationCanceledException) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                Succeeded = false,
                ErrorMessage = "Export cancelled."
            };
        } catch (Exception ex) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Executes a batch list of W12 Studio export jobs sequentially with aggregate progress reporting.
    /// </summary>
    public static List<StudioBatchExportResult> ExportBatch(
        CavernizeTrack track,
        IReadOnlyList<StudioBatchExportJob> jobs,
        Action<int, int, double> onProgress = null,
        CancellationToken token = default) {
        List<StudioBatchExportResult> results = new(jobs.Count);
        for (int i = 0; i < jobs.Count; i++) {
            token.ThrowIfCancellationRequested();
            int index = i;
            StudioBatchExportResult result = ExportSingle(
                track,
                jobs[i],
                p => onProgress?.Invoke(index, jobs.Count, p),
                token);
            results.Add(result);
        }
        return results;
    }

    static StudioBatchExportResult ExportEnvironmental(
        CavernizeTrack track,
        StudioBatchExportJob job,
        Codec codec,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token,
        UpmixingSettings upmixOverride = null,
        Source extraVocalSource = null) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, upmixOverride ?? new UpmixingSettings(false));
        if (extraVocalSource != null) {
            listener.AttachSource(extraVocalSource);
        }

        long totalSamples = Math.Max(updateRate, track.Length);
        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        List<Source> activeSources = new(listener.ActiveSources);
        int srcCount = activeSources.Count;
        int outCh = codec == Codec.AmbisonicsHOA || codec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, srcCount);

        // Real ITU-R BS.1770-5 loudness & 4x True-Peak metering across rendered environmental sources
        bool directPerSourceMeter = srcCount > 0 && srcCount <= 16;
        int meterCh = directPerSourceMeter ? srcCount : 2;
        LoudnessMeter meter = new(sampleRate, meterCh);
        if (directPerSourceMeter) {
            double[] weights = new double[meterCh];
            for (int c = 0; c < meterCh; c++) {
                weights[c] = activeSources[c].LFE ? 0.0 : 1.0;
            }
            meter.Configure(sampleRate, meterCh, weights);
        }
        TruePeakLimiter tpGuard = new(sampleRate, meterCh, job.TruePeakCeilingDbtp);
        float[] meterInterleaved = new float[updateRate * meterCh];
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        float stereoFoldNorm = 1f / MathF.Sqrt(Math.Max(1, srcCount));

        using EnvironmentWriter writer = codec switch {
            Codec.IAMF => new IamfEnvironmentWriter(outPath, listener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName),
            Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, listener, totalSamples, job.BitDepth, hoaOrder),
            _ => EnvironmentWriter.Create(outPath, codec, listener, totalSamples, job.BitDepth, track.Renderer)
        };

        long written = 0;
        long totalFrames = (totalSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            writer.WriteNextFrame();

            int frames = (int)Math.Min(updateRate, totalSamples - written);
            if (frames > 0 && srcCount > 0) {
                Array.Clear(meterInterleaved, 0, frames * meterCh);
                if (directPerSourceMeter) {
                    for (int c = 0; c < meterCh; c++) {
                        float[] srcSamples = activeSources[c].Rendered?[0];
                        if (srcSamples == null) {
                            continue;
                        }
                        int limit = Math.Min(frames, srcSamples.Length);
                        for (int s = 0; s < limit; s++) {
                            meterInterleaved[s * meterCh + c] = srcSamples[s];
                        }
                    }
                } else {
                    for (int c = 0; c < srcCount; c++) {
                        if (activeSources[c].LFE) {
                            continue;
                        }
                        float[] srcSamples = activeSources[c].Rendered?[0];
                        if (srcSamples == null) {
                            continue;
                        }
                        float panX = Math.Clamp((activeSources[c].Position * invEnv).X, -1f, 1f);
                        float gL = MathF.Sqrt(0.5f * (1f - panX)) * stereoFoldNorm;
                        float gR = MathF.Sqrt(0.5f * (1f + panX)) * stereoFoldNorm;
                        int limit = Math.Min(frames, srcSamples.Length);
                        for (int s = 0; s < limit; s++) {
                            float v = srcSamples[s];
                            meterInterleaved[s * 2] += v * gL;
                            meterInterleaved[s * 2 + 1] += v * gR;
                        }
                    }
                }
                meter.AnalyzeInterleaved(meterInterleaved, 0, frames, meterCh);
                tpGuard.ProcessInterleaved(meterInterleaved, 0, frames, meterCh);
            }

            written += frames;
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = outCh,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportPersonalizedBinaural(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        using IDisposable virtScope = VirtualizerFilter.BeginStaticStateScope();
        EnsureEnvironmentSize();
        new VirtualizerFilter().SetLayout();
        Listener.HeadphoneVirtualizer = true;
        int outSampleRate = VirtualizerFilter.FilterSampleRate;
        Listener listener = new(false) {
            SampleRate = outSampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(true));

        int stride = Math.Max(2, Listener.Channels.Length);
        double rateRatio = outSampleRate / (double)Math.Max(1, track.SampleRate);
        long totalSamples = Math.Max(updateRate, (long)Math.Round(Math.Max(updateRate, track.Length) * rateRatio));

        // Collect cloned Slot 6 post-processing stages (AutoEq, HpTF, ISO 226, BS.1770, Dialog)
        // so live playback stages never mutate or reset mid-export; skip limiters because this export
        // runs its own ceiling-configured TruePeakLimiter.
        List<IPostProcessingStage> activePostStages = [];
        if (job.PostProcessingStages != null) {
            for (int i = 0; i < job.PostProcessingStages.Count; i++) {
                IPostProcessingStage stage = job.PostProcessingStages[i];
                if (stage == null || !stage.IsEnabled) {
                    continue;
                }
                if (string.Equals(stage.StageId, "true-peak-limiter-4x", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "true-peak-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "cavern-soft-knee-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "limiter", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                activePostStages.Add(stage.CloneForOfflineExport());
            }
            activePostStages.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        for (int i = 0; i < activePostStages.Count; i++) {
            activePostStages[i].Configure(outSampleRate, 2);
            activePostStages[i].Reset();
        }

        LoudnessMeter meter = new(outSampleRate, 2);
        TruePeakLimiter limiter = new(outSampleRate, 2, job.TruePeakCeilingDbtp);
        float[] binauralStereo = new float[updateRate * 2];
        SpatialSceneFrame stageContext = new() {
            SampleRate = outSampleRate,
            FrameCount = updateRate,
            Metadata = SpatialProgramMetadata.CreateDefault()
        };

        long written = 0;
        try {
            using RIFFWaveWriter writer = new(outPath, new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight }, totalSamples, outSampleRate, job.BitDepth);
            writer.WriteHeader();

            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalSamples - written);
                for (int s = 0; s < frames; s++) {
                    binauralStereo[s * 2] = rendered[s * stride];
                    binauralStereo[s * 2 + 1] = rendered[s * stride + 1];
                }

                stageContext.FrameCount = frames;
                for (int i = 0; i < activePostStages.Count; i++) {
                    activePostStages[i].ProcessInterleaved(binauralStereo, 0, frames, 2, stageContext);
                }

                limiter.ProcessInterleaved(binauralStereo, 0, frames, 2);
                meter.AnalyzeInterleaved(binauralStereo, 0, frames, 2);
                writer.WriteBlock(binauralStereo, 0, frames * 2);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        } finally {
            for (int i = 0; i < activePostStages.Count; i++) {
                try {
                    activePostStages[i].Dispose();
                } catch {
                    // Best-effort cleanup of cloned offline post-processing stages
                }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = activePostStages.Count > 0
                ? $"Personalized Lossless Binaural (HRTF + {activePostStages.Count} Slot 6 Stages + BS.1770 Limiter)"
                : "Personalized Lossless Binaural (HRTF + BS.1770 Limiter)",
            ChannelCount = 2,
            SampleRate = outSampleRate,
            DurationSeconds = written / (double)Math.Max(1, outSampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static bool IsNativeMultichannelOrAtmos(CavernizeTrack track) =>
        track != null &&
        ((track.Renderer != null && (track.Renderer.HasObjects || track.Renderer.Objects.Count > 2)) ||
         (track.Reader != null && track.Reader.ChannelCount > 2));

    static StudioBatchExportResult ExportHybrid714(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token,
        string formatSummaryOverride = null) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;

        // W48 Dynamic Atmos Preservation Invariant: NEVER downmix native Dolby Atmos / multichannel tracks to stereo
        // for AI upmix. Native Atmos moving objects are preserved 100% via AtmosAwareUpmixer below.
        bool isNativeMulti = IsNativeMultichannelOrAtmos(track);

        // Prefer Slot 7A Neural Orchestrator -> NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe (canonical Layout714 + 120 Hz LR4 LFE)
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        if (!isNativeMulti &&
            job.NeuralOrchestrator != null &&
            job.NeuralRuntime != null &&
            job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
            stemPlan != null &&
            stemPlan.IsRunnable) {
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out bool fromTwin, p => onProgress?.Invoke(p * 0.20), token);
            int totalSamples = Math.Min(inL.Length, inR.Length);
            (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
            (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                track.Path ?? job.TrackName,
                fileSize,
                mtimeTicks,
                inL,
                inR,
                sampleRate,
                NeuralPipelineGoal.StemSeparation,
                safeNeural,
                fromTwin,
                (p, _) => onProgress?.Invoke(0.20 + p * 0.55),
                token);
            job.OnSeparatedTrackReady?.Invoke(separated);

            StereoStemBuffer[] stems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
            if (stems.Length > 0 && totalSamples > 0) {
                float[][] full12 = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sampleRate, 0.35f, 120f);
                LoudnessAnalysisSnapshot neuralSnap = WriteMeteredLimitedMultichannelWav(
                    outPath,
                    Layout714,
                    full12,
                    sampleRate,
                    job.BitDepth,
                    job.TruePeakCeilingDbtp,
                    p => onProgress?.Invoke(0.75 + p * 0.25),
                    token);

                return new StudioBatchExportResult {
                    Preset = job.Preset,
                    OutputPath = outPath,
                    FormatSummary = formatSummaryOverride ?? $"Cinema 7.1.4 AI Spatial Master ({stemPlan.Summary})",
                    ChannelCount = Layout714.Length,
                    SampleRate = sampleRate,
                    DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                    IntegratedLufs = ResolveMeasuredLufs(neuralSnap),
                    LoudnessRangeLu = neuralSnap.LoudnessRangeLu,
                    MaxTruePeakDbtp = neuralSnap.MaxTruePeakDbtp,
                    Succeeded = true
                };
            }
        }

        // Transparent pure-DSP fallback: Parametric STFT + Atmos-Aware 7.1.4 spatial upmixer
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(Layout714));

        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        UpmixingSettings upmix = new(true) {
            Cavernize = true,
            MatrixUpmixing = true,
            ParametricStft = true,
            AtmosAware = true
        };
        track.Attach(listener, upmix);

        long totalDspSamples = Math.Max(updateRate, track.Length);
        LoudnessMeter meter = new(sampleRate, Layout714.Length);
        TruePeakLimiter limiter = new(sampleRate, Layout714.Length, job.TruePeakCeilingDbtp);

        using RIFFWaveWriter writer = new(outPath, Layout714, totalDspSamples, sampleRate, job.BitDepth);
        writer.WriteHeader();

        long written = 0;
        long totalFrames = (totalDspSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int frames = (int)Math.Min(updateRate, totalDspSamples - written);
            limiter.ProcessInterleaved(rendered, 0, frames, Layout714.Length);
            meter.AnalyzeInterleaved(rendered, 0, frames, Layout714.Length);
            writer.WriteBlock(rendered, 0, frames * Layout714.Length);
            written += frames;
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummaryOverride ?? "Hybrid DirAC Parametric 7.1.4 Spatial Master (DSP Fallback)",
            ChannelCount = Layout714.Length,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportSpatialKaraoke(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        bool includeLiveVocalMixdown,
        Action<double> onProgress,
        CancellationToken token) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        bool multichannelAtmos = !includeLiveVocalMixdown && allItems.Count > 2;

        // Resolve Slot 7A Neural Lead/Backing vocal reference stems when available (transparent DSP fallback if absent)
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan karaokePlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, safeNeural);
        bool hasNeuralKaraoke = job.NeuralOrchestrator != null &&
                                job.NeuralRuntime != null &&
                                job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                                karaokePlan != null &&
                                karaokePlan.IsRunnable;

        if (multichannelAtmos) {
            // Preserve full discrete Atmos / surround layout (e.g. 5.1, 7.1, 7.1.4) using zero-phase multichannel karaoke
            ReferenceChannel[] targetLayout = ResolveExportChannelLayout(track, allItems.Count);
            DecodeMultichannelLayout(track, targetLayout, out float[][] multiChannels, out bool[] isLfe, out float[] mixL, out float[] mixR,
                p => onProgress?.Invoke(p * 0.25), token);
            int totalSamples = multiChannels.Length > 0 ? multiChannels[0].Length : 0;

            float[] leadRef = null;
            float[] leadRefRight = null;
            float[] backingRef = null;
            if (hasNeuralKaraoke && totalSamples > 0) {
                (float[] nL, float[] nR, bool fromTwin) = ResolveNeuralStereoInput(job, mixL, mixR, totalSamples);
                (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
                (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                    track.Path ?? job.TrackName,
                    fileSize,
                    mtimeTicks,
                    nL,
                    nR,
                    sampleRate,
                    NeuralPipelineGoal.SpatialKaraoke,
                    safeNeural,
                    fromTwin,
                    (p, _) => onProgress?.Invoke(0.25 + p * 0.45),
                    token);
                job.OnSeparatedTrackReady?.Invoke(separated);
                ResolveStereoLeadVocalRefs(separated, out leadRef, out leadRefRight);
                backingRef = job.PreserveBackingVocals ? separated?.MonoBackingVocalRef : null;
            }

            bool[] isStrictCenter = new bool[targetLayout.Length];
            for (int c = 0; c < targetLayout.Length; c++) {
                isStrictCenter[c] = targetLayout[c] == ReferenceChannel.FrontCenter;
            }

            onProgress?.Invoke(0.72);
            float[][] karaokeMulti = SpatialKaraokeEngine.ProcessOfflineMultichannel(
                multiChannels,
                isLfe,
                leadRef,
                backingRef,
                sampleRate,
                Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
                job.PreserveBackingVocals,
                neuralLeadVocalRefRight: leadRefRight,
                isStrictCenter: isStrictCenter);

            LoudnessAnalysisSnapshot multiSnap = WriteMeteredLimitedMultichannelWav(
                outPath,
                targetLayout,
                karaokeMulti,
                sampleRate,
                job.BitDepth,
                job.TruePeakCeilingDbtp,
                p => onProgress?.Invoke(0.75 + p * 0.25),
                token);

            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = leadRef != null
                    ? $"Spatial Karaoke Multichannel Atmos Master ({targetLayout.Length} ch · {karaokePlan.Summary})"
                    : $"Spatial Karaoke Multichannel Atmos Master ({targetLayout.Length} ch · Zero-Phase Coherence)",
                ChannelCount = targetLayout.Length,
                SampleRate = sampleRate,
                DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                IntegratedLufs = ResolveMeasuredLufs(multiSnap),
                LoudnessRangeLu = multiSnap.LoudnessRangeLu,
                MaxTruePeakDbtp = multiSnap.MaxTruePeakDbtp,
                Succeeded = true
            };
        }

        // Stereo Spatial Karaoke or Live Karaoke Performance Spatial Mixdown (W08 Karaoke + W11 Live Vocal Chain)
        DecodeStereoInput(track, job, out float[] stereoL, out float[] stereoR, out bool twinUsed, p => onProgress?.Invoke(p * 0.20), token);
        int len = Math.Min(stereoL.Length, stereoR.Length);
        float[] neuralLeadRef = null;
        float[] neuralLeadRefRight = null;
        float[] neuralBackingRef = null;

        if (hasNeuralKaraoke && len > 0) {
            (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
            (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                track.Path ?? job.TrackName,
                fileSize,
                mtimeTicks,
                stereoL,
                stereoR,
                sampleRate,
                NeuralPipelineGoal.SpatialKaraoke,
                safeNeural,
                twinUsed,
                (p, _) => onProgress?.Invoke(0.20 + p * 0.45),
                token);
            job.OnSeparatedTrackReady?.Invoke(separated);
            ResolveStereoLeadVocalRefs(separated, out neuralLeadRef, out neuralLeadRefRight);
            neuralBackingRef = job.PreserveBackingVocals ? separated?.MonoBackingVocalRef : null;
        }

        float[][] stereoPair = [stereoL, stereoR];
        bool[] stereoLfe = [false, false];
        float[][] karaokeStereo = SpatialKaraokeEngine.ProcessOfflineMultichannel(
            stereoPair,
            stereoLfe,
            neuralLeadRef,
            neuralBackingRef,
            sampleRate,
            Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
            job.PreserveBackingVocals,
            neuralLeadVocalRefRight: neuralLeadRefRight);

        LiveVocalChainDsp vocalDsp = includeLiveVocalMixdown ? new LiveVocalChainDsp(sampleRate) : null;
        if (vocalDsp != null) {
            vocalDsp.Configure(
                sampleRate,
                job.VocalDnaProfile,
                new VocalMacroSettings { Enabled = true });
        }

        LoudnessMeter meter = new(sampleRate, 2);
        TruePeakLimiter limiter = new(sampleRate, 2, job.TruePeakCeilingDbtp);
        ReferenceChannel[] stereoLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
        float[] micInterleaved = new float[updateRate * 2];
        float[] outInterleaved = new float[updateRate * 2];

        using RIFFWaveWriter writer = new(outPath, stereoLayout, len, sampleRate, job.BitDepth);
        writer.WriteHeader();

        int written = 0;
        int totalBlocks = (len + updateRate - 1) / updateRate;
        for (int b = 0; b < totalBlocks; b++) {
            token.ThrowIfCancellationRequested();
            int frames = Math.Min(updateRate, len - written);
            for (int s = 0; s < frames; s++) {
                outInterleaved[s * 2] = karaokeStereo[0][written + s];
                outInterleaved[s * 2 + 1] = karaokeStereo[1][written + s];
            }

            if (vocalDsp != null && job.LiveMicMonoCapture != null && written < job.LiveMicMonoCapture.Length) {
                int micAvail = Math.Min(frames, job.LiveMicMonoCapture.Length - written);
                Array.Clear(micInterleaved, 0, frames * 2);
                for (int s = 0; s < micAvail; s++) {
                    float m = job.LiveMicMonoCapture[written + s];
                    micInterleaved[s * 2] = m;
                    micInterleaved[s * 2 + 1] = m;
                }
                vocalDsp.ProcessStereoBlockZeroGc(micInterleaved, 0, frames, outInterleaved, 0);
                for (int i = 0; i < frames * 2; i++) {
                    outInterleaved[i] += micInterleaved[i];
                }
            }

            limiter.ProcessInterleaved(outInterleaved, 0, frames, 2);
            meter.AnalyzeInterleaved(outInterleaved, 0, frames, 2);
            writer.WriteBlock(outInterleaved, 0, frames * 2);
            written += frames;
            if ((b & 15) == 0 || b == totalBlocks - 1) {
                onProgress?.Invoke(Math.Clamp(0.68 + 0.32 * ((b + 1.0) / Math.Max(1, totalBlocks)), 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = includeLiveVocalMixdown
                ? (neuralLeadRef != null
                    ? $"Live Karaoke Performance Mixdown ({karaokePlan.Summary} + W11 Vocal Chain)"
                    : "Live Karaoke Performance Mixdown (Coherence Karaoke + W11 Vocal Chain)")
                : (neuralLeadRef != null
                    ? $"Spatial Karaoke Stereo Master ({karaokePlan.Summary})"
                    : "Spatial Karaoke Stereo Master (Zero-Phase Coherence Mask)"),
            ChannelCount = 2,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportFlacAtmosHybridMaster(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        LgsrConfig safeLgsr = (job.LgsrConfig ?? LgsrConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        bool canRunEmMwf = job.LosslessTwinProvider is UniversalLosslessTwinProvider universalTwin &&
                           universalTwin.HasSyncedLosslessTwin &&
                           job.NeuralRuntime != null &&
                           job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                           job.NeuralOrchestrator != null &&
                           stemPlan != null &&
                           stemPlan.IsRunnable;

        if (canRunEmMwf) {
            UniversalLosslessTwinProvider syncedTwin = (UniversalLosslessTwinProvider)job.LosslessTwinProvider;
            int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
            LoudnessAnalysisSnapshot measured = LoudnessAnalysisSnapshot.Empty();
            int masteredChannels = 0;

            StemExtractionResult extRes = StemExtractor.ExtractLosslessHybridSpatialMaster(
                track,
                syncedTwin,
                job.NeuralRuntime,
                stemModelPath: null,
                stemModelEntry: null,
                outputDir: Path.GetDirectoryName(outPath) ?? ".",
                bits: job.BitDepth,
                neuralConfig: safeNeural,
                lgsrConfig: safeLgsr,
                tier3RestorerModelPath: null,
                tier3RestorerEntry: null,
                exportResidualTrack: false,
                onProgress: (p, _) => onProgress?.Invoke(p),
                token: token,
                orchestrator: job.NeuralOrchestrator,
                onSeparatedTrackReady: job.OnSeparatedTrackReady,
                combinedMasterOutputPath: outPath,
                onMasterChannelsReady: (channels, layout) => {
                    masteredChannels = channels.Length;
                    measured = ApplyTruePeakLimiterAndMeasureInPlace(
                        channels,
                        layout,
                        sampleRate,
                        job.TruePeakCeilingDbtp,
                        token);
                });

            NeuralExecutionPlan tier3Plan = safeLgsr.EnableTier3ResidualCleanup
                ? job.NeuralOrchestrator.BuildPlan(NeuralPipelineGoal.LosslessResidualRestore, safeNeural)
                : null;
            string tier3Tag = tier3Plan != null && tier3Plan.IsRunnable ? $" + Tier 3 ({tier3Plan.Summary})" : string.Empty;

            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = $"FLAC + Atmos EM-MWF Hybrid Master ({masteredChannels} ch · {stemPlan.Summary}{tier3Tag})",
                ChannelCount = Math.Max(1, masteredChannels),
                SampleRate = extRes.SampleRate,
                DurationSeconds = extRes.SamplesWritten / (double)Math.Max(1, extRes.SampleRate),
                IntegratedLufs = ResolveMeasuredLufs(measured),
                LoudnessRangeLu = measured.LoudnessRangeLu,
                MaxTruePeakDbtp = measured.MaxTruePeakDbtp,
                Succeeded = true
            };
        }

        // Honest transparent fallback when unpaired or ONNX Runtime / stem checkpoint unavailable
        return ExportHybrid714(
            track,
            job,
            outPath,
            onProgress,
            token,
            formatSummaryOverride: "7.1.4 Spatial Master (Fallback — Pair Lossless Twin + ONNX for EM-MWF)");
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

    static LoudnessAnalysisSnapshot WriteMeteredLimitedMultichannelWav(
        string outPath,
        ReferenceChannel[] layout,
        float[][] channels,
        int sampleRate,
        BitDepth bits,
        float ceilingDbtp,
        Action<double> onProgress,
        CancellationToken token) {
        int chCount = Math.Min(layout.Length, channels.Length);
        int len = chCount > 0 && channels[0] != null ? channels[0].Length : 0;
        LoudnessMeter meter = new(sampleRate, Math.Max(1, chCount));
        double[] weights = new double[Math.Max(1, chCount)];
        for (int c = 0; c < chCount; c++) {
            weights[c] = layout[c] == ReferenceChannel.ScreenLFE ? 0.0 : 1.0;
        }
        meter.Configure(sampleRate, Math.Max(1, chCount), weights);
        TruePeakLimiter limiter = new(sampleRate, Math.Max(1, chCount), ceilingDbtp);

        using RIFFWaveWriter writer = new(outPath, layout[..chCount], len, sampleRate, bits);
        writer.WriteHeader();

        float[] interleaved = new float[updateRate * chCount];
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
            writer.WriteBlock(interleaved, 0, frames * chCount);
            blockIdx++;
            if ((blockIdx & 15) == 0 || blockIdx >= totalBlocks) {
                onProgress?.Invoke(Math.Clamp(blockIdx / (double)totalBlocks, 0.0, 1.0));
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

    static StudioBatchExportResult ExportComposable(
        CavernizeTrack track,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        bool isNativeMulti = IsNativeMultichannelOrAtmos(track);
        bool hasRecordedVocal = opts.IncludeRecordedPerformance && job.LiveMicMonoCapture != null && job.LiveMicMonoCapture.Length > 0;
        bool wantsEnvContainer = opts.BaseFormat == StudioBaseOutputFormat.DolbyAtmosAdm ||
                                 opts.BaseFormat == StudioBaseOutputFormat.EclipsaIamf ||
                                 opts.BaseFormat == StudioBaseOutputFormat.Ambisonics360;

        // Fast path preserving 100% of dynamic moving Dolby Atmos objects directly into ADM BWF / IAMF / Ambisonics 360
        // (supports combining with Atmos-safe AI Enhancement AND Recorded Vocal Performance!).
        if (wantsEnvContainer && isNativeMulti && !opts.EnableKaraokeInstrumental && !opts.EnableLosslessTwinRestoration) {
            Codec envCodec = opts.BaseFormat switch {
                StudioBaseOutputFormat.EclipsaIamf => Codec.IAMF,
                StudioBaseOutputFormat.Ambisonics360 => Codec.AmbisonicsHOA,
                _ => job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF
            };
            UpmixingSettings envUpmix = opts.EnableAiSpatialUpmix
                ? new UpmixingSettings(true) { AtmosAware = true, ParametricStft = true, Cavernize = true, MatrixUpmixing = true }
                : new UpmixingSettings(false);
            Source vocalObj = hasRecordedVocal
                ? BuildProcessedPerformanceSource(job, opts, sampleRate, Math.Max(updateRate, (int)Math.Min(track.Length, int.MaxValue / 4)))
                : null;
            string summary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems: false, usedEmMwf: false, hasRecordedVocal);
            return ExportEnvironmental(track, job, envCodec, outPath, summary, onProgress, token, envUpmix, vocalObj);
        }

        // Pure binaural fast path when no Karaoke, Recorded Performance, AI Neural Upmix, or Lossless Twin override is active
        if (opts.BaseFormat == StudioBaseOutputFormat.Headphone3D &&
            !opts.EnableAiSpatialUpmix &&
            !opts.EnableKaraokeInstrumental &&
            !hasRecordedVocal &&
            !opts.EnableLosslessTwinRestoration) {
            StudioBatchExportJob cleanJob = opts.ApplyPersonalizedHeadphonePost
                ? job
                : new StudioBatchExportJob {
                    TrackName = job.TrackName,
                    Preset = StudioExportPreset.PersonalizedLosslessBinaural,
                    OutputPath = outPath,
                    BitDepth = job.BitDepth,
                    TruePeakCeilingDbtp = job.TruePeakCeilingDbtp,
                    PostProcessingStages = null
                };
            return ExportPersonalizedBinaural(track, cleanJob, outPath, onProgress, token);
        }

        // Unified Composable Multichannel Pipeline:
        // Step 1: Produce canonical spatial channel buses (7.1.4 or native layout) with 100% Atmos object preservation or AI 7.1.4 + discrete LFE
        ReferenceChannel[] activeLayout;
        float[][] channels;
        float[] refStereoL;
        float[] refStereoR;
        bool usedNeuralStems = false;
        bool usedEmMwf = false;

        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        bool canRunNeuralStems = job.NeuralOrchestrator != null &&
                                 job.NeuralRuntime != null &&
                                 job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                                 stemPlan != null &&
                                 stemPlan.IsRunnable;

        bool canRunEmMwf = opts.EnableLosslessTwinRestoration &&
                           isNativeMulti &&
                           canRunNeuralStems &&
                           job.LosslessTwinProvider is UniversalLosslessTwinProvider twinForEm &&
                           twinForEm.HasSyncedLosslessTwin;

        if (canRunEmMwf) {
            UniversalLosslessTwinProvider syncedTwin = (UniversalLosslessTwinProvider)job.LosslessTwinProvider;
            float[][] mastered = null;
            ReferenceChannel[] masteredLayout = null;
            string tempDir = Path.GetDirectoryName(outPath) ?? ".";
            string tempUnused = Path.Combine(tempDir, $".cavern_emmwf_tmp_{Guid.NewGuid():N}.wav");
            try {
                StemExtractor.ExtractLosslessHybridSpatialMaster(
                    track,
                    syncedTwin,
                    job.NeuralRuntime,
                    stemModelPath: null,
                    stemModelEntry: null,
                    outputDir: tempDir,
                    bits: job.BitDepth,
                    neuralConfig: safeNeural,
                    lgsrConfig: (job.LgsrConfig ?? LgsrConfig.Default).Sanitize(),
                    tier3RestorerModelPath: null,
                    tier3RestorerEntry: null,
                    exportResidualTrack: false,
                    onProgress: (p, _) => onProgress?.Invoke(p * 0.50),
                    token: token,
                    orchestrator: job.NeuralOrchestrator,
                    onSeparatedTrackReady: job.OnSeparatedTrackReady,
                    combinedMasterOutputPath: tempUnused,
                    onMasterChannelsReady: (chs, lay) => {
                        mastered = chs;
                        masteredLayout = lay;
                    });
            } finally {
                try { if (File.Exists(tempUnused)) File.Delete(tempUnused); } catch { }
            }
            activeLayout = masteredLayout ?? Layout714;
            channels = mastered ?? Array.Empty<float[]>();
            BuildStereoFolddown(channels, activeLayout, out refStereoL, out refStereoR);
            usedEmMwf = true;
            usedNeuralStems = true;
        } else if (opts.EnableAiSpatialUpmix && !isNativeMulti && canRunNeuralStems) {
            // Stereo + AI Spatial Upmix -> canonical 12-channel 7.1.4 with complementary 120 Hz LR4 discrete LFE
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out bool fromTwin, p => onProgress?.Invoke(p * 0.15), token);
            (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
            (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                track.Path ?? job.TrackName,
                fileSize,
                mtimeTicks,
                inL,
                inR,
                sampleRate,
                NeuralPipelineGoal.StemSeparation,
                safeNeural,
                fromTwin,
                (p, _) => onProgress?.Invoke(0.15 + p * 0.35),
                token);
            job.OnSeparatedTrackReady?.Invoke(separated);
            StereoStemBuffer[] stems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
            if (stems.Length > 0) {
                activeLayout = Layout714;
                channels = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sampleRate, 0.35f, 120f);
                refStereoL = inL;
                refStereoR = inR;
                usedNeuralStems = true;
            } else {
                activeLayout = Layout714;
                DecodeMultichannelLayout(track, Layout714, out channels, out _, out refStereoL, out refStereoR, p => onProgress?.Invoke(0.15 + p * 0.35), token,
                    new UpmixingSettings(true) { Cavernize = true, MatrixUpmixing = true, ParametricStft = true, AtmosAware = true });
            }
        } else if (opts.EnableAiSpatialUpmix || opts.BaseFormat == StudioBaseOutputFormat.Cinema714 || isNativeMulti) {
            // Native Atmos / multichannel OR DSP 7.1.4 spatial upmix (preserves dynamic moving Atmos objects!)
            activeLayout = opts.BaseFormat == StudioBaseOutputFormat.Cinema714 || opts.EnableAiSpatialUpmix
                ? Layout714
                : ResolveExportChannelLayout(track, TrackSourceItem.BuildFromTrack(track).Count);
            UpmixingSettings upmix = opts.EnableAiSpatialUpmix
                ? new UpmixingSettings(true) { Cavernize = true, MatrixUpmixing = true, ParametricStft = true, AtmosAware = true }
                : new UpmixingSettings(false);
            DecodeMultichannelLayout(track, activeLayout, out channels, out _, out refStereoL, out refStereoR, p => onProgress?.Invoke(p * 0.45), token, upmix);
        } else {
            // Stereo base (e.g. Stereo + Karaoke + Recorded Performance -> Headphone 3D)
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out _, p => onProgress?.Invoke(p * 0.45), token);
            activeLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
            channels = [inL, inR];
            refStereoL = inL;
            refStereoR = inR;
        }

        int totalSamples = channels.Length > 0 && channels[0] != null ? channels[0].Length : 0;
        if (totalSamples <= 0) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                Succeeded = false,
                ErrorMessage = "No audio frames decoded for Studio export."
            };
        }

        // Step 2: Optional Spatial Karaoke vocal attenuation (preserves backing vocals & discrete LFE)
        if (opts.EnableKaraokeInstrumental) {
            bool[] isLfe = new bool[activeLayout.Length];
            bool[] isStrictCenter = new bool[activeLayout.Length];
            for (int c = 0; c < activeLayout.Length; c++) {
                isLfe[c] = activeLayout[c] == ReferenceChannel.ScreenLFE;
                isStrictCenter[c] = activeLayout[c] == ReferenceChannel.FrontCenter;
            }
            float[] leadRefL = null;
            float[] leadRefR = null;
            float[] backingRef = null;
            NeuralExecutionPlan karaokePlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, safeNeural);
            if (job.NeuralOrchestrator != null &&
                job.NeuralRuntime != null &&
                job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                karaokePlan != null &&
                karaokePlan.IsRunnable) {
                (float[] nL, float[] nR, bool fromTwin) = ResolveNeuralStereoInput(job, refStereoL, refStereoR, totalSamples);
                (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
                (NeuralSeparatedTrack kSep, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                    track.Path ?? job.TrackName,
                    fileSize,
                    mtimeTicks,
                    nL,
                    nR,
                    sampleRate,
                    NeuralPipelineGoal.SpatialKaraoke,
                    safeNeural,
                    fromTwin,
                    (p, _) => onProgress?.Invoke(0.50 + p * 0.15),
                    token);
                job.OnSeparatedTrackReady?.Invoke(kSep);
                ResolveStereoLeadVocalRefs(kSep, out leadRefL, out leadRefR);
                backingRef = job.PreserveBackingVocals ? kSep?.MonoBackingVocalRef : null;
            }
            channels = SpatialKaraokeEngine.ProcessOfflineMultichannel(
                channels,
                isLfe,
                leadRefL,
                backingRef,
                sampleRate,
                Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
                job.PreserveBackingVocals,
                neuralLeadVocalRefRight: leadRefR,
                isStrictCenter: isStrictCenter);
        }

        // Step 3: Optional Recorded Vocal Performance mixdown through W11 Live Vocal Chain + sidechain pocket
        if (hasRecordedVocal) {
            MixRecordedPerformanceIntoMultichannelInPlace(channels, activeLayout, job, opts, sampleRate, token);
        }

        string formatSummary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems, usedEmMwf, hasRecordedVocal);

        // Step 4: Render into the chosen commercial Base Output Format
        if (opts.BaseFormat == StudioBaseOutputFormat.Cinema714) {
            LoudnessAnalysisSnapshot snap714 = WriteMeteredLimitedMultichannelWav(
                outPath,
                activeLayout,
                channels,
                sampleRate,
                job.BitDepth,
                job.TruePeakCeilingDbtp,
                p => onProgress?.Invoke(0.70 + p * 0.30),
                token);
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = formatSummary,
                ChannelCount = activeLayout.Length,
                SampleRate = sampleRate,
                DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                IntegratedLufs = ResolveMeasuredLufs(snap714),
                LoudnessRangeLu = snap714.LoudnessRangeLu,
                MaxTruePeakDbtp = snap714.MaxTruePeakDbtp,
                Succeeded = true
            };
        }

        if (opts.BaseFormat == StudioBaseOutputFormat.Headphone3D) {
            return RenderMultichannelToBinauralWav(
                channels,
                activeLayout,
                sampleRate,
                job,
                opts.ApplyPersonalizedHeadphonePost,
                outPath,
                formatSummary,
                p => onProgress?.Invoke(0.70 + p * 0.30),
                token);
        }

        // ADM BWF / IAMF / Ambisonics 360 from composed spatial buses (each channel placed at its ITU 3D speaker anchor)
        Codec outCodec = opts.BaseFormat switch {
            StudioBaseOutputFormat.EclipsaIamf => Codec.IAMF,
            StudioBaseOutputFormat.Ambisonics360 => Codec.AmbisonicsHOA,
            _ => job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF
        };
        return RenderMultichannelToEnvironmentalContainer(
            channels,
            activeLayout,
            sampleRate,
            job,
            outCodec,
            outPath,
            formatSummary,
            p => onProgress?.Invoke(0.70 + p * 0.30),
            token);
    }

    static string BuildComposableSummary(
        StudioComposableExportOptions opts,
        bool isNativeMulti,
        bool usedNeuralStems,
        bool usedEmMwf,
        bool hasRecordedVocal) {
        string baseName = opts.BaseFormat switch {
            StudioBaseOutputFormat.Headphone3D => "3D Headphones",
            StudioBaseOutputFormat.Cinema714 => "Cinema 7.1.4",
            StudioBaseOutputFormat.DolbyAtmosAdm => "Dolby Atmos Master",
            StudioBaseOutputFormat.EclipsaIamf => "Eclipsa Audio 3D",
            StudioBaseOutputFormat.Ambisonics360 => "360° Ambisonics",
            _ => "Spatial Master"
        };
        List<string> mods = [];
        if (opts.EnableAiSpatialUpmix) {
            mods.Add(isNativeMulti ? "Atmos-Safe AI" : (usedNeuralStems ? "AI 3D Upmix" : "3D Spatial Upmix"));
        }
        if (opts.EnableLosslessTwinRestoration) {
            mods.Add(usedEmMwf ? "Lossless EM-MWF" : "Lossless Twin");
        }
        if (opts.EnableKaraokeInstrumental) {
            mods.Add("Karaoke");
        }
        if (hasRecordedVocal) {
            mods.Add("Vocal Take");
        }
        return mods.Count > 0 ? $"{baseName} + {string.Join(" + ", mods)}" : baseName;
    }

    static Source BuildProcessedPerformanceSource(
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        int targetSampleRate,
        int totalSamples) {
        float[] aligned = AlignAndResamplePerformanceMono(job.LiveMicMonoCapture, opts.RecordedPerformanceSampleRate, targetSampleRate, opts.RecordedPerformanceStartTrackSample, totalSamples);
        LiveVocalChainDsp vocalDsp = new(targetSampleRate);
        vocalDsp.Configure(targetSampleRate, job.VocalDnaProfile, new VocalMacroSettings { Enabled = true });
        float[] stereoBuf = new float[updateRate * 2];
        for (int pos = 0; pos < totalSamples; pos += updateRate) {
            int frames = Math.Min(updateRate, totalSamples - pos);
            for (int s = 0; s < frames; s++) {
                float v = aligned[pos + s];
                stereoBuf[s * 2] = v;
                stereoBuf[s * 2 + 1] = v;
            }
            vocalDsp.ProcessStereoBlockZeroGc(stereoBuf, 0, frames, null, 0);
            for (int s = 0; s < frames; s++) {
                aligned[pos + s] = 0.5f * (stereoBuf[s * 2] + stereoBuf[s * 2 + 1]);
            }
        }
        EnsureEnvironmentSize();
        return new Source {
            Clip = new Clip(new MultichannelWaveform(aligned), targetSampleRate),
            Position = new Vector3(0f, 0.08f, 1.0f) * Listener.EnvironmentSize,
            Volume = 1f
        };
    }

    static float[] AlignAndResamplePerformanceMono(
        float[] rawMono,
        int takeSampleRate,
        int targetSampleRate,
        long startTrackSample,
        int totalSamples) {
        float[] aligned = new float[Math.Max(0, totalSamples)];
        if (rawMono == null || rawMono.Length == 0 || totalSamples <= 0) {
            return aligned;
        }
        int srcSr = takeSampleRate >= 8000 ? takeSampleRate : targetSampleRate;
        double step = srcSr / (double)Math.Max(1, targetSampleRate);
        int startDst = (int)Math.Clamp(startTrackSample, 0L, totalSamples);
        int maxSrc = rawMono.Length;
        for (int dst = startDst; dst < totalSamples; dst++) {
            double srcPos = (dst - startDst) * step;
            if (srcPos >= maxSrc) {
                break;
            }
            int idx = (int)srcPos;
            float frac = (float)(srcPos - idx);
            float s0 = rawMono[idx];
            float s1 = idx + 1 < maxSrc ? rawMono[idx + 1] : s0;
            aligned[dst] = s0 + (s1 - s0) * frac;
        }
        return aligned;
    }

    static void MixRecordedPerformanceIntoMultichannelInPlace(
        float[][] channels,
        ReferenceChannel[] layout,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        int sampleRate,
        CancellationToken token) {
        if (channels == null || channels.Length == 0 || channels[0] == null) {
            return;
        }
        int totalSamples = channels[0].Length;
        float[] aligned = AlignAndResamplePerformanceMono(
            job.LiveMicMonoCapture,
            opts.RecordedPerformanceSampleRate,
            sampleRate,
            opts.RecordedPerformanceStartTrackSample,
            totalSamples);

        int idxFl = Array.IndexOf(layout, ReferenceChannel.FrontLeft);
        int idxFr = Array.IndexOf(layout, ReferenceChannel.FrontRight);
        int idxFc = Array.IndexOf(layout, ReferenceChannel.FrontCenter);
        if (idxFl < 0) idxFl = 0;
        if (idxFr < 0) idxFr = Math.Min(1, channels.Length - 1);

        LiveVocalChainDsp vocalDsp = new(sampleRate);
        vocalDsp.Configure(sampleRate, job.VocalDnaProfile, new VocalMacroSettings { Enabled = true });
        float[] vocalStereo = new float[updateRate * 2];
        float[] backingStereo = new float[updateRate * 2];

        for (int pos = 0; pos < totalSamples; pos += updateRate) {
            token.ThrowIfCancellationRequested();
            int frames = Math.Min(updateRate, totalSamples - pos);
            for (int s = 0; s < frames; s++) {
                float v = aligned[pos + s];
                vocalStereo[s * 2] = v;
                vocalStereo[s * 2 + 1] = v;
                backingStereo[s * 2] = channels[idxFl][pos + s];
                backingStereo[s * 2 + 1] = channels[idxFr][pos + s];
            }
            vocalDsp.ProcessStereoBlockZeroGc(vocalStereo, 0, frames, backingStereo, 0);
            for (int s = 0; s < frames; s++) {
                channels[idxFl][pos + s] = backingStereo[s * 2];
                channels[idxFr][pos + s] = backingStereo[s * 2 + 1];
                float vL = vocalStereo[s * 2];
                float vR = vocalStereo[s * 2 + 1];
                if (idxFc >= 0 && idxFc < channels.Length) {
                    // Anchor vocal cleanly in Front Center (Mid) + subtle stereo air in FL/FR (Side)
                    float mid = 0.70710678f * (vL + vR);
                    float side = 0.5f * (vL - vR);
                    channels[idxFc][pos + s] += mid;
                    channels[idxFl][pos + s] += side;
                    channels[idxFr][pos + s] -= side;
                } else {
                    channels[idxFl][pos + s] += vL;
                    channels[idxFr][pos + s] += vR;
                }
            }
        }
    }

    static void BuildStereoFolddown(float[][] channels, ReferenceChannel[] layout, out float[] mixL, out float[] mixR) {
        int len = channels != null && channels.Length > 0 && channels[0] != null ? channels[0].Length : 0;
        mixL = new float[len];
        mixR = new float[len];
        if (len == 0) {
            return;
        }
        int chCount = Math.Min(channels.Length, layout.Length);
        for (int s = 0; s < len; s++) {
            float l = 0f, r = 0f;
            for (int c = 0; c < chCount; c++) {
                if (layout[c] == ReferenceChannel.ScreenLFE) continue;
                float v = channels[c][s];
                ReferenceChannel rc = layout[c];
                if (rc == ReferenceChannel.FrontCenter || rc == ReferenceChannel.RearCenter) {
                    l += v * 0.70710678f;
                    r += v * 0.70710678f;
                } else if (rc == ReferenceChannel.FrontLeft || rc == ReferenceChannel.SideLeft ||
                           rc == ReferenceChannel.RearLeft || rc == ReferenceChannel.TopFrontLeft ||
                           rc == ReferenceChannel.TopRearLeft || rc == ReferenceChannel.TopSideLeft) {
                    l += v;
                } else if (rc == ReferenceChannel.FrontRight || rc == ReferenceChannel.SideRight ||
                           rc == ReferenceChannel.RearRight || rc == ReferenceChannel.TopFrontRight ||
                           rc == ReferenceChannel.TopRearRight || rc == ReferenceChannel.TopSideRight) {
                    r += v;
                } else {
                    l += v * 0.5f;
                    r += v * 0.5f;
                }
            }
            mixL[s] = Math.Clamp(l, -1f, 1f);
            mixR[s] = Math.Clamp(r, -1f, 1f);
        }
    }

    static StudioBatchExportResult RenderMultichannelToBinauralWav(
        float[][] channels,
        ReferenceChannel[] layout,
        int sourceSampleRate,
        StudioBatchExportJob job,
        bool applyPostStages,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token) {
        using IDisposable virtScope = VirtualizerFilter.BeginStaticStateScope();
        EnsureEnvironmentSize();
        new VirtualizerFilter().SetLayout();
        Listener.HeadphoneVirtualizer = true;
        int outSampleRate = VirtualizerFilter.FilterSampleRate;
        Listener listener = new(false) {
            SampleRate = outSampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };

        int chCount = Math.Min(channels.Length, layout.Length);
        int srcLen = chCount > 0 && channels[0] != null ? channels[0].Length : 0;
        for (int c = 0; c < chCount; c++) {
            ReferenceChannel ch = layout[c];
            Vector3 normPos = ChannelPrototype.AlternativePositions[(int)ch];
            listener.AttachSource(new Source {
                Clip = new Clip(new MultichannelWaveform(channels[c]), sourceSampleRate),
                Position = normPos * Listener.EnvironmentSize,
                LFE = ch == ReferenceChannel.ScreenLFE,
                Volume = 1f
            });
        }

        int stride = Math.Max(2, Listener.Channels.Length);
        double rateRatio = outSampleRate / (double)Math.Max(1, sourceSampleRate);
        long totalSamples = Math.Max(updateRate, (long)Math.Round(Math.Max(updateRate, srcLen) * rateRatio));

        List<IPostProcessingStage> activePostStages = [];
        if (applyPostStages && job.PostProcessingStages != null) {
            for (int i = 0; i < job.PostProcessingStages.Count; i++) {
                IPostProcessingStage stage = job.PostProcessingStages[i];
                if (stage == null || !stage.IsEnabled) continue;
                if (string.Equals(stage.StageId, "true-peak-limiter-4x", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "true-peak-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "cavern-soft-knee-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "limiter", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                activePostStages.Add(stage.CloneForOfflineExport());
            }
            activePostStages.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            for (int i = 0; i < activePostStages.Count; i++) {
                activePostStages[i].Configure(outSampleRate, 2);
                activePostStages[i].Reset();
            }
        }

        LoudnessMeter meter = new(outSampleRate, 2);
        TruePeakLimiter limiter = new(outSampleRate, 2, job.TruePeakCeilingDbtp);
        float[] binauralStereo = new float[updateRate * 2];
        SpatialSceneFrame stageContext = new() {
            SampleRate = outSampleRate,
            FrameCount = updateRate,
            Metadata = SpatialProgramMetadata.CreateDefault()
        };

        long written = 0;
        try {
            using RIFFWaveWriter writer = new(outPath, new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight }, totalSamples, outSampleRate, job.BitDepth);
            writer.WriteHeader();
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalSamples - written);
                for (int s = 0; s < frames; s++) {
                    binauralStereo[s * 2] = rendered[s * stride];
                    binauralStereo[s * 2 + 1] = rendered[s * stride + 1];
                }
                stageContext.FrameCount = frames;
                for (int i = 0; i < activePostStages.Count; i++) {
                    activePostStages[i].ProcessInterleaved(binauralStereo, 0, frames, 2, stageContext);
                }
                limiter.ProcessInterleaved(binauralStereo, 0, frames, 2);
                meter.AnalyzeInterleaved(binauralStereo, 0, frames, 2);
                writer.WriteBlock(binauralStereo, 0, frames * 2);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        } finally {
            for (int i = 0; i < activePostStages.Count; i++) {
                try { activePostStages[i].Dispose(); } catch { }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = 2,
            SampleRate = outSampleRate,
            DurationSeconds = written / (double)Math.Max(1, outSampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult RenderMultichannelToEnvironmentalContainer(
        float[][] channels,
        ReferenceChannel[] layout,
        int sampleRate,
        StudioBatchExportJob job,
        Codec codec,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };
        int chCount = Math.Min(channels.Length, layout.Length);
        int totalSamples = chCount > 0 && channels[0] != null ? channels[0].Length : updateRate;
        for (int c = 0; c < chCount; c++) {
            ReferenceChannel ch = layout[c];
            listener.AttachSource(new Source {
                Clip = new Clip(new MultichannelWaveform(channels[c]), sampleRate),
                Position = ChannelPrototype.AlternativePositions[(int)ch] * Listener.EnvironmentSize,
                LFE = ch == ReferenceChannel.ScreenLFE,
                Volume = 1f
            });
        }

        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        int outCh = codec == Codec.AmbisonicsHOA || codec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, chCount);

        LoudnessAnalysisSnapshot measured = ApplyTruePeakLimiterAndMeasureInPlace(channels, layout, sampleRate, job.TruePeakCeilingDbtp, token);

        using EnvironmentWriter writer = codec switch {
            Codec.IAMF => new IamfEnvironmentWriter(outPath, listener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName),
            Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, listener, totalSamples, job.BitDepth, hoaOrder),
            _ => EnvironmentWriter.Create(outPath, codec, listener, totalSamples, job.BitDepth, renderer: null)
        };

        long totalFrames = (totalSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            writer.WriteNextFrame();
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = outCh,
            SampleRate = sampleRate,
            DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(measured),
            LoudnessRangeLu = measured.LoudnessRangeLu,
            MaxTruePeakDbtp = measured.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static void EnsureEnvironmentSize() {
        if (Listener.EnvironmentSize.X == 0 || Listener.EnvironmentSize.Y == 0 || Listener.EnvironmentSize.Z == 0) {
            Listener.EnvironmentSize = new Vector3(10, 7, 10);
        }
    }
}
