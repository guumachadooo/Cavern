using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6 post-processing stage (Priority 110, directly after AutoEq at 100 and before safety limiter at 900):
/// Applies individual Headphone-to-Ear-Canal Transfer Function (HpTF) half-wave standing-wave resonance notch
/// attenuation (6–10 kHz) using zero-GC double-precision Orfanidis Nyquist-decramped biquads.
/// Automatically bit-identical pass-through when disabled, at 0 dB depth, or when Direct 2.0 is active.
/// </summary>
public sealed class HptfNotchPostProcessingStage : IPostProcessingStage {
    readonly HptfNotchFilter filter = new(Listener.DefaultSampleRate, 2);
    bool userEnabled;

    /// <inheritdoc/>
    public string StageId => "hptf-ear-canal-notch";

    /// <inheritdoc/>
    public string DisplayName => "HpTF Ear-Canal Resonance Notch";

    /// <inheritdoc/>
    public int Priority => 110;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => userEnabled && filter.IsActive;
        set {
            userEnabled = value;
            filter.SetParameters(value, filter.FrequencyHz, filter.GainDb, filter.Q);
        }
    }

    /// <summary>Configured notch center frequency in Hz (6000..10000 Hz).</summary>
    public float FrequencyHz => filter.FrequencyHz;

    /// <summary>Configured notch attenuation depth in dB (-12..0 dB).</summary>
    public float GainDb => filter.GainDb;

    /// <summary>Configured notch selectivity Q factor (3..16).</summary>
    public float Q => filter.Q;

    /// <summary>
    /// Atomically updates all HpTF ear-canal notch parameters from Slot 5B personalization config.
    /// </summary>
    public void ApplyParameters(bool enabled, float frequencyHz, float gainDb, float qFactor) {
        userEnabled = enabled;
        filter.SetParameters(enabled, frequencyHz, gainDb, qFactor);
    }

    /// <summary>
    /// Evaluates the exact notch magnitude response in dB at <paramref name="freqHz"/>.
    /// </summary>
    public double EvaluateMagnitudeDb(double freqHz) => filter.EvaluateMagnitudeDb(freqHz);

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        filter.Configure(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled || (context != null && !context.IsBinauralActive)) {
            return;
        }
        if (context != null && context.SampleRate >= 8000 && context.SampleRate != filter.SampleRate) {
            filter.Configure(context.SampleRate, channels);
        }
        filter.ProcessInterleaved(buffer, offset, frameCount, channels);
    }

    /// <inheritdoc/>
    public void Reset() {
        filter.Reset();
    }

    /// <inheritdoc/>
    public IPostProcessingStage CloneForOfflineExport() {
        HptfNotchPostProcessingStage clone = new();
        clone.ApplyParameters(userEnabled, FrequencyHz, GainDb, Q);
        return clone;
    }

    /// <inheritdoc/>
    public void Dispose() {
        filter.SetParameters(false, HptfNotchFilter.DefaultFrequencyHz, 0f, HptfNotchFilter.DefaultQ);
    }
}
