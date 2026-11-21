using Cavern;
using Cavern.Filters;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6B post-processing stage (Priority 400): ITU-R BS.1770-5 / EBU R128 loudness meter &amp; normalization stage
/// with Dolby Dialnorm bitstream respect. Always meters Momentary (400 ms), Short-Term (3 s), Integrated (gated),
/// LRA, Crest Factor, and 4x True Peak with zero heap allocations; remains bit-transparent when mode is <see cref="LoudnessNormalizationMode.Off"/>.
/// </summary>
public sealed class Bs1770LoudnessPostProcessingStage : IPostProcessingStage {
    const float MaxBoostDb = 12f;
    const float MaxCutDb = -18f;
    const float SilenceFreezeLufs = -55f;

    readonly LoudnessMeter meter = new(Listener.DefaultSampleRate, 2);

    LoudnessNormalizationMode mode = LoudnessNormalizationMode.Off;
    bool respectDolbyDialnorm = true;
    int sampleRate = Listener.DefaultSampleRate;
    float smoothedGainLinear = 1f;
    float appliedGainDb;
    float activeDialnormLufs = float.NaN;

    /// <inheritdoc/>
    public string StageId => "bs1770-loudness-meter";

    /// <inheritdoc/>
    public string DisplayName => "ITU-R BS.1770-5 / EBU R128 Loudness";

    /// <inheritdoc/>
    public int Priority => 400;

    /// <inheritdoc/>
    /// <remarks>Always enabled so live BS.1770-5 metering stays active even when normalization gain is Off (bit-transparent).</remarks>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Active loudness normalization target mode.</summary>
    public LoudnessNormalizationMode NormalizationMode => mode;

    /// <summary>When true, uses bitstream Dolby Dialnorm as static reference anchor to preserve macro-dynamics.</summary>
    public bool RespectDolbyDialnorm => respectDolbyDialnorm;

    /// <summary>Latest BS.1770-5 analysis snapshot (M/S/I LUFS, LRA, 4x True Peak, RMS, Crest Factor).</summary>
    public LoudnessAnalysisSnapshot Snapshot => meter.Snapshot;

    /// <summary>Current smooth normalization gain applied in dB (0.0 dB when mode is Off).</summary>
    public float AppliedGainDb => appliedGainDb;

    /// <summary>Latest active bitstream Dolby Dialnorm in LUFS, or NaN if absent.</summary>
    public float ActiveDialnormLufs => activeDialnormLufs;

    /// <summary>Updates normalization mode and Dolby Dialnorm respect cleanly without resetting metering history.</summary>
    public void ApplyParameters(LoudnessNormalizationMode normalizationMode, bool respectDialnorm) {
        mode = Enum.IsDefined(normalizationMode) ? normalizationMode : LoudnessNormalizationMode.Off;
        respectDolbyDialnorm = respectDialnorm;
        if (mode == LoudnessNormalizationMode.Off) {
            smoothedGainLinear = 1f;
            appliedGainDb = 0f;
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        this.sampleRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        meter.Configure(this.sampleRate, channels);
        smoothedGainLinear = 1f;
        appliedGainDb = 0f;
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled || buffer == null || frameCount <= 0 || channels <= 0) {
            return;
        }
        int sliceLen = frameCount * channels;
        if (offset < 0 || offset + sliceLen > buffer.Length) {
            return;
        }

        int sr = context != null && context.SampleRate >= 8000 ? context.SampleRate : sampleRate;
        if (sr != meter.SampleRate) {
            sampleRate = sr;
            meter.Configure(sr, channels);
        }

        // 1. Always analyze raw pre-normalization loudness & True Peak via ITU-R BS.1770-5
        meter.AnalyzeInterleaved(buffer, offset, frameCount, channels);
        LoudnessAnalysisSnapshot snap = meter.Snapshot;

        float dialnorm = context != null ? context.Metadata.DialnormLufs : float.NaN;
        bool hasValidDialnorm = !float.IsNaN(dialnorm) && dialnorm >= -31.5f && dialnorm <= -1.0f;
        activeDialnormLufs = hasValidDialnorm ? dialnorm : float.NaN;

        // 2. Compute smooth target normalization gain (exact 0.00 dB bit-transparent pass-through when Off)
        if (mode == LoudnessNormalizationMode.Off) {
            smoothedGainLinear = 1f;
            appliedGainDb = 0f;
        } else {
            float targetGainDb = ComputeTargetGainDb(snap, dialnorm, hasValidDialnorm);
            if (!float.IsNaN(targetGainDb)) {
                float targetLinear = MathF.Pow(10f, targetGainDb * 0.05f);
                // Slow 400 ms per-sample exponential slew eliminates zipper noise and micro-pumping
                float alpha = 1f - MathF.Exp(-1f / (MathF.Max(8000, sr) * 0.40f));
                float g = smoothedGainLinear;

                for (int f = 0; f < frameCount; f++) {
                    g += (targetLinear - g) * alpha;
                    int baseIdx = offset + f * channels;
                    for (int c = 0; c < channels; c++) {
                        buffer[baseIdx + c] *= g;
                    }
                }
                smoothedGainLinear = g;
                appliedGainDb = g > 1e-6f ? 20f * MathF.Log10(g) : -120f;
            } else if (MathF.Abs(smoothedGainLinear - 1f) > 1e-5f) {
                // Hold steady gain during brief silence/pauses so fade-ins never jump
                float g = smoothedGainLinear;
                for (int i = offset, end = offset + sliceLen; i < end; i++) {
                    buffer[i] *= g;
                }
                appliedGainDb = g > 1e-6f ? 20f * MathF.Log10(g) : -120f;
            }
        }

        // 3. Publish live BS.1770-5 telemetry into the active scene frame
        if (context != null) {
            SoundfieldTelemetry tel = context.Telemetry;
            tel.MomentaryLufs = snap.MomentaryLufs;
            tel.ShortTermLufs = snap.ShortTermLufs;
            tel.IntegratedLufs = snap.IntegratedLufs;
            tel.LoudnessRangeLu = snap.LoudnessRangeLu;
            tel.TruePeakDbfs = snap.TruePeakDbtp + appliedGainDb;
            tel.CrestFactorDb = snap.CrestFactorDb;
            tel.AppliedLoudnessGainDb = appliedGainDb;
            tel.ActiveDialnormLufs = activeDialnormLufs;
            context.Telemetry = tel;
        }
    }

    /// <inheritdoc/>
    public void Reset() {
        meter.Reset();
        smoothedGainLinear = 1f;
        appliedGainDb = 0f;
    }

    /// <inheritdoc/>
    public void Dispose() {
        meter.Reset();
    }

    float ComputeTargetGainDb(in LoudnessAnalysisSnapshot snap, float dialnorm, bool hasValidDialnorm) {
        // ATSC A/52 Dolby Dialnorm mode:
        // On Dolby bitstreams, -31 dBFS is the zero-attenuation reference (e.g., dialnorm -27 => -4 dB, -31 => 0 dB).
        if (mode == LoudnessNormalizationMode.DolbyDialnorm && hasValidDialnorm) {
            return Math.Clamp(-31.0f - dialnorm, MaxCutDb, 0f);
        }

        float targetLufs = mode switch {
            LoudnessNormalizationMode.EbuR128Minus23 => -23.0f,
            LoudnessNormalizationMode.AtmosMusicMinus18 => -18.0f,
            LoudnessNormalizationMode.StreamingMinus14 => -14.0f,
            LoudnessNormalizationMode.DolbyDialnorm => -27.0f,
            _ => float.NaN
        };
        if (float.IsNaN(targetLufs)) {
            return 0f;
        }

        // Static bitstream Dialnorm anchor preserves 100% macro-dynamics when requested
        if (respectDolbyDialnorm && hasValidDialnorm) {
            return Math.Clamp(targetLufs - dialnorm, MaxCutDb, MaxBoostDb);
        }

        // Freeze gain adaptation during near-silence to prevent amplifying room noise or fade-ins
        if (float.IsNaN(snap.MomentaryLufs) || snap.MomentaryLufs < SilenceFreezeLufs) {
            return float.NaN;
        }

        // Prefer two-stage gated Integrated LUFS once valid; fall back to Short-Term during initial 400ms..3s ramp
        float measured = !float.IsNaN(snap.IntegratedLufs)
            ? snap.IntegratedLufs
            : (!float.IsNaN(snap.ShortTermLufs) ? snap.ShortTermLufs : snap.MomentaryLufs);

        if (float.IsNaN(measured)) {
            return float.NaN;
        }
        return Math.Clamp(targetLufs - measured, MaxCutDb, MaxBoostDb);
    }
}
