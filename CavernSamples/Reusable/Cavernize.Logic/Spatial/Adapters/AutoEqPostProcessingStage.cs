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
    // Dedicated scratch analyzer keeps live audio thread snapshot lock-free and click-free during rebuild sweeps
    readonly ParametricEqualizer analysisEq = new(Listener.DefaultSampleRate, 2) {
        AutoPreampHeadroom = false,
        UseNyquistDeCramping = true
    };

    AutoEqHeadphoneProfile activeProfile;
    AutoEqNeutralTarget activeTarget = AutoEqNeutralTarget.NativeAutoEqProfile;
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
            analysisEq.Clear();
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
    public IPostProcessingStage CloneForOfflineExport() {
        AutoEqHeadphoneProfile profile;
        AutoEqNeutralTarget target;
        BinauralCompensationMode binauralMode;
        bool guardEnabled;
        bool enabled;
        lock (stateLock) {
            profile = activeProfile;
            target = activeTarget;
            binauralMode = activeBinauralMode;
            guardEnabled = doubleEqGuardEnabled;
            enabled = isEnabled;
        }
        AutoEqPostProcessingStage clone = new() {
            UserEnabled = enabled
        };
        clone.ApplyConfiguration(profile, target, binauralMode, guardEnabled);
        return clone;
    }

    /// <inheritdoc/>
    public void Dispose() {
        ClearProfile();
    }

    void RebuildEqualizerLocked() {
        if (activeProfile == null || activeProfile.Bands == null || activeProfile.Bands.Length == 0) {
            doubleEqGuardActive = false;
            equalizer.Clear();
            analysisEq.Clear();
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

        int sr = Math.Max(8000, equalizer.SampleRate);
        analysisEq.Configure(sr, equalizer.Channels);
        analysisEq.AutoPreampHeadroom = false;
        analysisEq.UseNyquistDeCramping = true;
        analysisEq.SetBands(composite, 0.0, false);

        (double subPeakDb, double subPeakFreqHz, double audiblePeakDb, double ultraHighPeakDb) = MeasureBandPeaks(analysisEq, sr);

        // Damp extreme sub-28Hz shelf/bell resonance (e.g. LS(105,+17.6)+PK(48,-16.9)) when it inflates
        // global preamp above the 35Hz..16kHz calibration band by >1.2 dB, without distorting >=40Hz response.
        bool subDamped = false;
        if (subPeakFreqHz < 28.0 && subPeakDb > audiblePeakDb + 1.2) {
            double dampFreq = Math.Clamp(subPeakFreqHz, 18.0, 24.0);
            double dampGain = -Math.Min(subPeakDb - audiblePeakDb, 3.0);
            ParametricBand[] damped = new ParametricBand[composite.Length + 1];
            Array.Copy(composite, damped, composite.Length);
            damped[composite.Length] = new ParametricBand(ParametricFilterType.Peaking, dampFreq, dampGain, 2.0, true);
            composite = damped;
            subDamped = true;

            analysisEq.SetBands(composite, 0.0, false);
            (subPeakDb, _, audiblePeakDb, ultraHighPeakDb) = MeasureBandPeaks(analysisEq, sr);
        }

        // Guarantee 0 dBFS headroom across the full musical band [35Hz..16kHz] while capping sub-35Hz / >16kHz
        // outlier headroom penalty at 1.5 dB (Priority 900 True-Peak Limiter remains active downstream).
        double fullBandPeakDb = Math.Max(subPeakDb, Math.Max(audiblePeakDb, ultraHighPeakDb));
        double effectivePeakDb = Math.Max(audiblePeakDb, Math.Max(subPeakDb - 1.5, ultraHighPeakDb - 1.5));
        double perceptualPreampDb = effectivePeakDb > 0.05 ? -(effectivePeakDb + 0.2) : 0.0;

        bool unaltered = !guardEngaged && !subDamped && composite.Length == activeProfile.Bands.Length;
        if (unaltered && activeProfile.HasExplicitPreamp && activeProfile.ExplicitPreampDb < -(fullBandPeakDb + 0.6)) {
            perceptualPreampDb = Math.Min(perceptualPreampDb, activeProfile.ExplicitPreampDb);
        }

        equalizer.AutoPreampHeadroom = false;
        equalizer.UseNyquistDeCramping = true;
        equalizer.SetBands(composite, perceptualPreampDb, true);
    }

    static (double subPeakDb, double subPeakFreqHz, double audiblePeakDb, double ultraHighPeakDb) MeasureBandPeaks(
        ParametricEqualizer eq, int sampleRate) {
        double subPeakDb = double.NegativeInfinity;
        double subPeakFreqHz = 20.0;
        const int subSteps = 32;
        const double subMin = 20.0;
        const double subMax = 35.0;
        double subRatio = subMax / subMin;
        for (int i = 0; i < subSteps; i++) {
            double f = subMin * Math.Pow(subRatio, i / (double)(subSteps - 1));
            double db = eq.EvaluateMagnitudeDb(f, includePreamp: false);
            if (db > subPeakDb) {
                subPeakDb = db;
                subPeakFreqHz = f;
            }
        }
        if (subPeakDb < 0.0) {
            subPeakDb = 0.0;
        }

        double audiblePeakDb = 0.0;
        double audMax = Math.Min(16000.0, sampleRate * 0.45);
        if (audMax > subMax) {
            const int audSteps = 192;
            double audRatio = audMax / subMax;
            for (int i = 0; i < audSteps; i++) {
                double f = subMax * Math.Pow(audRatio, i / (double)(audSteps - 1));
                double db = eq.EvaluateMagnitudeDb(f, includePreamp: false);
                if (db > audiblePeakDb) {
                    audiblePeakDb = db;
                }
            }
        }

        double ultraHighPeakDb = 0.0;
        double uhMin = 16000.0;
        double uhMax = Math.Min(20000.0, sampleRate * 0.48);
        if (uhMax > uhMin) {
            const int uhSteps = 24;
            double uhRatio = uhMax / uhMin;
            for (int i = 0; i < uhSteps; i++) {
                double f = uhMin * Math.Pow(uhRatio, i / (double)(uhSteps - 1));
                double db = eq.EvaluateMagnitudeDb(f, includePreamp: false);
                if (db > ultraHighPeakDb) {
                    ultraHighPeakDb = db;
                }
            }
        }

        return (subPeakDb, subPeakFreqHz, audiblePeakDb, ultraHighPeakDb);
    }
}
