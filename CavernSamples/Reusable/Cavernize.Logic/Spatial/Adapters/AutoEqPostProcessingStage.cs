using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6A post-processing stage: Neutral AutoEq headphone compensation engine running at Priority 100
/// (strictly before loudness / true-peak limiter at Priority 900).
/// Wraps Cavern's zero-GC double-precision <see cref="ParametricEqualizer"/> with laboratory-fitted
/// Diffuse-Field / B&amp;K 5128 / JM-1 target transforms and automatic Zero Double-EQ Guard for Raw HRIRs.
/// </summary>
public sealed class AutoEqPostProcessingStage : IPostProcessingStage {
    readonly object stateLock = new();
    readonly ParametricEqualizer equalizer = new(Listener.DefaultSampleRate, 2);

    AutoEqHeadphoneProfile activeProfile;
    AutoEqNeutralTarget activeTarget = AutoEqNeutralTarget.DiffuseFieldNeutral;
    BinauralCompensationMode activeBinauralMode = BinauralCompensationMode.DiffuseFieldCompensatedHrir;
    bool doubleEqGuardEnabled = true;
    bool doubleEqGuardActive;
    bool isEnabled;

    /// <inheritdoc/>
    public string StageId => "autoeq-neutral-peq";

    /// <inheritdoc/>
    public string DisplayName => "AutoEq Neutral Headphone Compensation";

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => isEnabled && activeProfile != null && equalizer.ActiveSectionCount > 0;
        set => isEnabled = value;
    }

    /// <summary>Raw user enable flag (even when no profile is loaded yet).</summary>
    public bool UserEnabled {
        get => isEnabled;
        set => isEnabled = value;
    }

    /// <summary>Currently active headphone compensation profile, or null when flat/unassigned.</summary>
    public AutoEqHeadphoneProfile ActiveProfile => activeProfile;

    /// <summary>Active scientific neutral target curve.</summary>
    public AutoEqNeutralTarget ActiveTarget => activeTarget;

    /// <summary>Active binaural HRIR compensation mode reported by Slot 3 / Slot 5.</summary>
    public BinauralCompensationMode ActiveBinauralMode => activeBinauralMode;

    /// <summary>When true (default), automatically prevents double ear-canal/pinna EQ on Raw HRIRs.</summary>
    public bool DoubleEqGuardEnabled => doubleEqGuardEnabled;

    /// <summary>True when the Zero Double-EQ Guard inverse Diffuse-Field filter is currently engaged.</summary>
    public bool DoubleEqGuardActive => doubleEqGuardActive;

    /// <summary>Active peak-safe preamp attenuation in dB applied ahead of the SOS cascade.</summary>
    public double EffectivePreampDb => equalizer.EffectivePreampDb;

    /// <summary>Worst-case positive boost (dB) across 20 Hz..20 kHz before preamp headroom.</summary>
    public double PeakBoostDb => equalizer.PeakBoostDb;

    /// <summary>Total number of active double-precision biquad sections (headphone + target delta + guard).</summary>
    public int ActiveSectionCount => equalizer.ActiveSectionCount;

    /// <summary>
    /// Atomically applies a headphone profile, neutral target, and binaural HRIR compensation mode.
    /// Safe to invoke from the UI thread while audio is streaming.
    /// </summary>
    public void ApplyConfiguration(
        AutoEqHeadphoneProfile profile,
        AutoEqNeutralTarget target,
        BinauralCompensationMode binauralMode,
        bool enableDoubleEqGuard = true) {
        lock (stateLock) {
            activeProfile = profile;
            activeTarget = target;
            activeBinauralMode = binauralMode;
            doubleEqGuardEnabled = enableDoubleEqGuard;
            RebuildEqualizerLocked();
        }
    }

    /// <summary>
    /// Updates the active binaural HRIR compensation mode (DFC vs Raw vs Direct 2.0) and instantaneously
    /// engages or disengages the Zero Double-EQ Guard without changing the selected headphone profile.
    /// </summary>
    public void UpdateBinauralMode(BinauralCompensationMode binauralMode) {
        lock (stateLock) {
            if (activeBinauralMode == binauralMode) {
                return;
            }
            activeBinauralMode = binauralMode;
            RebuildEqualizerLocked();
        }
    }

    /// <summary>
    /// Clears the loaded headphone profile and resets the stage to bit-transparent unity pass-through.
    /// </summary>
    public void ClearProfile() {
        lock (stateLock) {
            activeProfile = null;
            doubleEqGuardActive = false;
            equalizer.Clear();
        }
    }

    /// <summary>
    /// Evaluates the exact composite magnitude response (in dB) of the active AutoEq + target + guard cascade.
    /// </summary>
    public double EvaluateMagnitudeDb(double freqHz, bool includePreamp = true) =>
        equalizer.EvaluateMagnitudeDb(freqHz, includePreamp);

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        equalizer.Configure(sampleRate, channels);
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled) {
            return;
        }
        if (context != null && context.SampleRate >= 8000 && context.SampleRate != equalizer.SampleRate) {
            equalizer.Configure(context.SampleRate, channels);
        }
        equalizer.ProcessInterleaved(buffer, offset, frameCount, channels);
    }

    /// <inheritdoc/>
    public void Reset() {
        equalizer.ResetState();
    }

    /// <inheritdoc/>
    public void Dispose() {
        ClearProfile();
    }

    void RebuildEqualizerLocked() {
        if (activeProfile == null || activeProfile.Bands == null || activeProfile.Bands.Length == 0) {
            doubleEqGuardActive = false;
            equalizer.Clear();
            return;
        }

        ParametricBand[] composite = AutoEqTargetCurves.BuildCompositeBands(
            activeProfile.Bands,
            activeProfile.SourceTarget,
            activeTarget,
            activeBinauralMode,
            doubleEqGuardEnabled,
            out bool guardEngaged);
        doubleEqGuardActive = guardEngaged;

        // Only trust the source file's explicit preamp verbatim when no target/guard delta was added;
        // otherwise let ParametricEqualizer's exact 256-point peak sweep compute exact clipping-free headroom.
        bool unaltered = composite.Length == activeProfile.Bands.Length && !guardEngaged;
        equalizer.AutoPreampHeadroom = true;
        equalizer.UseNyquistDeCramping = true;
        equalizer.SetBands(
            composite,
            unaltered && activeProfile.HasExplicitPreamp ? activeProfile.ExplicitPreampDb : 0.0,
            unaltered && activeProfile.HasExplicitPreamp);
    }
}
