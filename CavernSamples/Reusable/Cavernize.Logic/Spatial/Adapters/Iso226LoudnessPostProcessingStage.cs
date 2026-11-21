using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6B post-processing stage (Priority 300): Real-time ISO 226:2023 Dynamic Equal-Loudness physiological
/// compensation filter. Automatically bit-transparent (0.000 dB unity bypass) when disabled or when listening SPL
/// equals reference SPL, and locked to exact 0.000 dB at 1 kHz.
/// </summary>
public sealed class Iso226LoudnessPostProcessingStage : IPostProcessingStage {
    readonly Iso226LoudnessFilter filter = new(Listener.DefaultSampleRate, 2);

    bool userEnabled;
    bool trackVolume = true;
    float referenceSplPhon = Iso226LoudnessFilter.DefaultReferenceSplPhon;
    float manualListeningSplPhon = Iso226LoudnessFilter.DefaultListeningSplPhon;
    float lastLinearVolume = 1f;

    /// <inheritdoc/>
    public string StageId => "iso226-equal-loudness";

    /// <inheritdoc/>
    public string DisplayName => "ISO 226:2023 Equal-Loudness";

    /// <inheritdoc/>
    public int Priority => 300;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => userEnabled && filter.IsActive;
        set {
            userEnabled = value;
            SyncFilter();
        }
    }

    /// <summary>True when actively applying physiological bass/treble compensation (not in unity bypass).</summary>
    public bool IsFilterActive => filter.IsActive;

    /// <summary>Configured reference SPL in phon (70..85).</summary>
    public float ReferenceSplPhon => filter.ReferenceSplPhon;

    /// <summary>Effective listening SPL currently driving ISO 226:2023 in phon.</summary>
    public float EffectiveListeningSplPhon => filter.ListeningSplPhon;

    /// <summary>Maximum compensation boost applied across 20 Hz–12.5 kHz in dB.</summary>
    public float MaxCompensationBoostDb => (float)filter.MaxCompensationBoostDb;

    /// <summary>RMS fitting error of the 6-band IIR cascade against ISO 226:2023 Table 1 in dB.</summary>
    public float FitRmsErrorDb => (float)filter.FitRmsErrorDb;

    /// <summary>Atomically updates ISO 226:2023 parameters from <see cref="PsychoacousticLoudnessConfig"/>.</summary>
    public void ApplyParameters(bool enabled, float referencePhon, float listeningPhon, bool trackPlaybackVolume) {
        userEnabled = enabled;
        referenceSplPhon = Math.Clamp(referencePhon, Iso226LoudnessFilter.MinReferenceSplPhon, Iso226LoudnessFilter.MaxReferenceSplPhon);
        manualListeningSplPhon = Math.Clamp(listeningPhon, Iso226LoudnessFilter.MinListeningSplPhon, Iso226LoudnessFilter.MaxListeningSplPhon);
        trackVolume = trackPlaybackVolume;
        SyncFilter();
    }

    /// <summary>
    /// Updates the master linear playback volume (0.01..1.0+) so ISO 226:2023 tracks listening SPL automatically
    /// when <c>trackVolume</c> is enabled. Only re-fits the IIR cascade when the effective SPL shifts &gt; 0.15 phon.
    /// </summary>
    public void UpdatePlaybackVolume(float linearVolume) {
        float clamped = Math.Clamp(linearVolume, 0.01f, 2.0f);
        if (MathF.Abs(clamped - lastLinearVolume) < 0.004f) {
            return;
        }
        lastLinearVolume = clamped;
        if (userEnabled && trackVolume) {
            SyncFilter();
        }
    }

    /// <summary>Evaluates the exact real-time filter magnitude response (dB) at <paramref name="freqHz"/>.</summary>
    public double EvaluateMagnitudeDb(double freqHz) => filter.EvaluateMagnitudeDb(freqHz);

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        filter.Configure(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled) {
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
    public void Dispose() {
        userEnabled = false;
        filter.SetParameters(false, Iso226LoudnessFilter.DefaultReferenceSplPhon, Iso226LoudnessFilter.DefaultReferenceSplPhon);
    }

    void SyncFilter() {
        float effectiveListen = trackVolume
            ? Iso226LoudnessFilter.EstimateListeningPhonFromVolume(referenceSplPhon, lastLinearVolume)
            : manualListeningSplPhon;
        filter.SetParameters(userEnabled, referenceSplPhon, effectiveListen, autoHeadroomPreamp: false);
    }
}
