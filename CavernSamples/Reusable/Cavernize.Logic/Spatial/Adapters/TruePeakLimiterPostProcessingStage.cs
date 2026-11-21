using Cavern;
using Cavern.Filters;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6B final post-processing stage (Priority 900): Transparent 4x-oversampled ITU-R BS.1770-5 True-Peak
/// Lookahead Brickwall Limiter. Passes all sub-ceiling signals at exact 1.000000x unity gain while eliminating
/// inter-sample DAC clipping with 1.5 ms lookahead and channel-linked stereo imaging preservation.
/// </summary>
public sealed class TruePeakLimiterPostProcessingStage : IPostProcessingStage {
    readonly TruePeakLimiter limiter = new(Listener.DefaultSampleRate, 2, TruePeakLimiter.DefaultCeilingDbtp);

    /// <inheritdoc/>
    public string StageId => "true-peak-limiter-4x";

    /// <inheritdoc/>
    public string DisplayName => "4x True-Peak Brickwall Limiter";

    /// <inheritdoc/>
    public int Priority => 900;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Configured brickwall ceiling in dBTP (-6.0..0.0 dBTP).</summary>
    public float CeilingDbtp {
        get => limiter.CeilingDbtp;
        set => limiter.CeilingDbtp = value;
    }

    /// <summary>Configured release time constant in milliseconds (10..500 ms).</summary>
    public float ReleaseMs {
        get => limiter.ReleaseMs;
        set => limiter.ReleaseMs = value;
    }

    /// <summary>Lookahead latency in sample frames.</summary>
    public int LatencySamples => limiter.LatencySamples;

    /// <summary>Instantaneous gain reduction in dB (&lt;= 0.0 dB).</summary>
    public float CurrentGainReductionDb => IsEnabled ? limiter.CurrentGainReductionDb : 0f;

    /// <summary>Peak gain reduction over the latest block in dB (&lt;= 0.0 dB).</summary>
    public float PeakGainReductionDb => IsEnabled ? limiter.PeakGainReductionDb : 0f;

    /// <summary>Running maximum output True Peak in dBTP since last reset.</summary>
    public float MaxOutputTruePeakDbtp => limiter.MaxOutputTruePeakDbtp;

    /// <summary>Atomically updates enable flag and True-Peak ceiling (dBTP).</summary>
    public void ApplyParameters(bool enabled, float ceilingDbtp) {
        IsEnabled = enabled;
        limiter.CeilingDbtp = ceilingDbtp;
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        limiter.Configure(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled || buffer == null || frameCount <= 0 || channels <= 0) {
            if (context != null) {
                SoundfieldTelemetry tel = context.Telemetry;
                tel.LimiterGainReductionDb = 0f;
                context.Telemetry = tel;
            }
            return;
        }
        if (context != null && context.SampleRate >= 8000 && context.SampleRate != limiter.SampleRate) {
            limiter.Configure(context.SampleRate, channels);
        }

        limiter.ProcessInterleaved(buffer, offset, frameCount, channels);

        if (context != null) {
            SoundfieldTelemetry tel = context.Telemetry;
            tel.LimiterGainReductionDb = limiter.PeakGainReductionDb;
            // Clamp reported post-limiter True Peak so telemetry reflects the actual output ceiling
            if (tel.TruePeakDbfs > limiter.CeilingDbtp) {
                tel.TruePeakDbfs = limiter.CeilingDbtp;
            }
            context.Telemetry = tel;
        }
    }

    /// <inheritdoc/>
    public void Reset() {
        limiter.Reset();
    }

    /// <inheritdoc/>
    public IPostProcessingStage CloneForOfflineExport() => new TruePeakLimiterPostProcessingStage {
        IsEnabled = IsEnabled,
        CeilingDbtp = CeilingDbtp,
        ReleaseMs = ReleaseMs
    };

    /// <inheritdoc/>
    public void Dispose() {
        limiter.Reset();
    }
}
