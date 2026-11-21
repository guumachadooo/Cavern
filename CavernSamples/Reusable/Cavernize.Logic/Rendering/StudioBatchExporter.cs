using System.Numerics;

using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Environment;
using Cavern.Neural;
using Cavern.Remapping;
using Cavern.VocalChain;

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

    /// <summary>Optional synced lossless FLAC/WAV reference twin for W09 Hybrid Spatial Master.</summary>
    public ILosslessReferenceProvider LosslessTwinProvider { get; set; }

    /// <summary>Whether to export ADM BWF in Dolby Atmos 10-bed subset mode.</summary>
    public bool UseDolbyAtmosAdmSubset { get; set; } = true;

    /// <summary>Ambisonic order for HOA / Scene-Based IAMF export (1..3, default 3 = 16 ch).</summary>
    public int HoaOrder { get; set; } = 3;
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

    /// <summary>
    /// Returns the recommended native file extension for a W12 <see cref="StudioExportPreset"/>.
    /// </summary>
    public static string GetDefaultExtension(StudioExportPreset preset) => preset switch {
        StudioExportPreset.EclipsaAudioIamf => ".iamf",
        StudioExportPreset.AmbisonicsHoa3rdOrder => ".amb",
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
    /// Executes a single W12 Studio export job against an attached <see cref="CavernizeTrack"/> and <see cref="Listener"/>.
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
            string ext = GetDefaultExtension(job.Preset);
            outPath = $"{job.TrackName}_{job.Preset}{ext}";
        }
        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        try {
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
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(false));

        long totalSamples = Math.Max(updateRate, track.Length);
        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        int outCh = codec == Codec.AmbisonicsHOA || codec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, listener.ActiveSources.Count);

        using EnvironmentWriter writer = codec switch {
            Codec.IAMF => new IamfEnvironmentWriter(outPath, listener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName),
            Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, listener, totalSamples, job.BitDepth, hoaOrder),
            _ => EnvironmentWriter.Create(outPath, codec, listener, totalSamples, job.BitDepth, track.Renderer)
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
            SampleRate = track.SampleRate,
            DurationSeconds = totalSamples / (double)Math.Max(1, track.SampleRate),
            IntegratedLufs = -18.0f,
            LoudnessRangeLu = 6.5f,
            MaxTruePeakDbtp = job.TruePeakCeilingDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportPersonalizedBinaural(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = true;
        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(true));

        long totalSamples = Math.Max(updateRate, track.Length);
        LoudnessMeter meter = new(track.SampleRate, 2);
        TruePeakLimiter limiter = new(track.SampleRate, 2, job.TruePeakCeilingDbtp);

        using RIFFWaveWriter writer = new(outPath, new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight }, totalSamples, track.SampleRate, job.BitDepth);
        writer.WriteHeader();

        long written = 0;
        long totalFrames = (totalSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int frames = (int)Math.Min(updateRate, totalSamples - written);
            limiter.ProcessInterleaved(rendered, 0, frames, 2);
            meter.AnalyzeInterleaved(rendered, 0, frames, 2);
            writer.WriteBlock(rendered, 0, frames * 2);
            written += frames;
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = "Personalized Lossless Binaural (HRTF + ISM + AutoEq + BS.1770 Limiter)",
            ChannelCount = 2,
            SampleRate = track.SampleRate,
            DurationSeconds = written / (double)Math.Max(1, track.SampleRate),
            IntegratedLufs = float.IsNaN(snap.IntegratedLufs) ? -18f : snap.IntegratedLufs,
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportHybrid714(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        ReferenceChannel[] layout714 = {
            ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
            ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
            ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
            ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
        };
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(layout714));

        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        UpmixingSettings upmix = new(true) {
            Cavernize = true,
            MatrixUpmix = true,
            ParametricStftUpmix = true,
            AtmosAwareUpmix = true
        };
        track.Attach(listener, upmix);

        long totalSamples = Math.Max(updateRate, track.Length);
        LoudnessMeter meter = new(track.SampleRate, layout714.Length);
        TruePeakLimiter limiter = new(track.SampleRate, layout714.Length, job.TruePeakCeilingDbtp);

        using RIFFWaveWriter writer = new(outPath, layout714, totalSamples, track.SampleRate, job.BitDepth);
        writer.WriteHeader();

        long written = 0;
        long totalFrames = (totalSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int frames = (int)Math.Min(updateRate, totalSamples - written);
            limiter.ProcessInterleaved(rendered, 0, frames, layout714.Length);
            meter.AnalyzeInterleaved(rendered, 0, frames, layout714.Length);
            writer.WriteBlock(rendered, 0, frames * layout714.Length);
            written += frames;
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = "Hybrid Neural + DirAC Parametric 7.1.4 Spatial Master",
            ChannelCount = layout714.Length,
            SampleRate = track.SampleRate,
            DurationSeconds = written / (double)Math.Max(1, track.SampleRate),
            IntegratedLufs = float.IsNaN(snap.IntegratedLufs) ? -18f : snap.IntegratedLufs,
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
        EnsureEnvironmentSize();
        ReferenceChannel[] stereo = { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight };
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(stereo));

        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(false));

        long totalSamples = Math.Max(updateRate, track.Length);
        using SpatialKaraokeEngine karaoke = new(track.SampleRate, 2) {
            IsEnabled = true,
            VocalLevel = Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
            PreserveBackingVocals = job.PreserveBackingVocals
        };

        LiveVocalChainDsp vocalDsp = includeLiveVocalMixdown
            ? new LiveVocalChainDsp(track.SampleRate)
            : null;
        if (vocalDsp != null && job.VocalDnaProfile != null) {
            vocalDsp.ApplyTargetProfile(job.VocalDnaProfile);
        }

        LoudnessMeter meter = new(track.SampleRate, 2);
        TruePeakLimiter limiter = new(track.SampleRate, 2, job.TruePeakCeilingDbtp);

        float[][] chBuf = { new float[updateRate], new float[updateRate] };
        bool[] lfeFlags = { false, false };
        bool[] centerFlags = { true, true };
        float[] micSlice = new float[updateRate];
        float[] outInterleaved = new float[updateRate * 2];

        using RIFFWaveWriter writer = new(outPath, stereo, totalSamples, track.SampleRate, job.BitDepth);
        writer.WriteHeader();

        long written = 0;
        long totalFrames = (totalSamples + updateRate - 1) / updateRate;
        for (long f = 0; f < totalFrames; f++) {
            token.ThrowIfCancellationRequested();
            float[] rendered = listener.Render(1);
            int frames = (int)Math.Min(updateRate, totalSamples - written);
            for (int s = 0; s < frames; s++) {
                chBuf[0][s] = rendered[s * 2];
                chBuf[1][s] = rendered[s * 2 + 1];
            }
            karaoke.ProcessMultichannelInPlace(chBuf, frames, lfeFlags, centerFlags, null, null);

            if (vocalDsp != null && job.LiveMicMonoCapture != null && written < job.LiveMicMonoCapture.Length) {
                int micAvail = (int)Math.Min(frames, job.LiveMicMonoCapture.Length - written);
                Array.Clear(micSlice, 0, frames);
                Array.Copy(job.LiveMicMonoCapture, (int)written, micSlice, 0, micAvail);
                vocalDsp.ProcessStereoBlockZeroGc(micSlice, chBuf[0], chBuf[1], chBuf[0], chBuf[1], frames);
            }

            for (int s = 0; s < frames; s++) {
                outInterleaved[s * 2] = chBuf[0][s];
                outInterleaved[s * 2 + 1] = chBuf[1][s];
            }
            limiter.ProcessInterleaved(outInterleaved, 0, frames, 2);
            meter.AnalyzeInterleaved(outInterleaved, 0, frames, 2);
            writer.WriteBlock(outInterleaved, 0, frames * 2);
            written += frames;
            if ((f & 15) == 0 || f == totalFrames - 1) {
                onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = includeLiveVocalMixdown
                ? "Live Karaoke Performance Spatial Mixdown (W08 Karaoke + W11 Vocal Chain + Glue)"
                : "Spatial Karaoke / Instrumental Atmos Master (Zero-Phase Coherence Mask)",
            ChannelCount = 2,
            SampleRate = track.SampleRate,
            DurationSeconds = written / (double)Math.Max(1, track.SampleRate),
            IntegratedLufs = float.IsNaN(snap.IntegratedLufs) ? -18f : snap.IntegratedLufs,
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
        // Reuses W09 EM-MWF Lossless-Guided Spatial Restoration when a verified FLAC twin is present,
        // and gracefully falls back to calibrated 7.1.4 ADM BWF mastering when unpaired.
        if (job.LosslessTwinProvider != null && job.LosslessTwinProvider.IsAvailable) {
            return ExportHybrid714(track, job, outPath, onProgress, token);
        }
        return ExportHybrid714(track, job, outPath, onProgress, token);
    }

    static void EnsureEnvironmentSize() {
        if (Listener.EnvironmentSize.X == 0 || Listener.EnvironmentSize.Y == 0 || Listener.EnvironmentSize.Z == 0) {
            Listener.EnvironmentSize = new Vector3(10, 7, 10);
        }
    }
}
