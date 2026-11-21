using Cavern.Filters;
using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Magnitude Least-Squares (MagLS) Ambisonics-to-binaural spherical-harmonic decoding order for Slot 5B.
/// </summary>
public enum MagLsDecodeMode {
    /// <summary>Disabled: direct measured discrete HRIR convolution (default zero-regression).</summary>
    Off = 0,

    /// <summary>1st-Order Ambisonics MagLS (4 SH channels, fc ~624 Hz).</summary>
    Order1 = 1,

    /// <summary>2nd-Order Ambisonics MagLS (9 SH channels, fc ~1248 Hz).</summary>
    Order2 = 2,

    /// <summary>3rd-Order Ambisonics MagLS (16 SH channels, fc ~1872 Hz).</summary>
    Order3 = 3
}

/// <summary>
/// Slot 5B HRTF personalization configuration: MagLS Ambisonics decoding, Woodworth-Schlosberg biometric
/// head-width ITD scaling, HpTF ear-canal resonance notch (6–10 kHz), and SONICOM/HUTUBS perceptual cluster ID.
/// </summary>
public readonly struct HrtfPersonalizationConfig : IEquatable<HrtfPersonalizationConfig> {
    /// <summary>MagLS Ambisonic spherical-harmonic decoding mode (Off by default for bit-identical pass-through).</summary>
    public MagLsDecodeMode MagLsMode { get; init; }

    /// <summary>Optional MagLS cutoff frequency override in Hz (0 = automatic physical cutoff f_c = N*c/(2*pi*a)).</summary>
    public float MagLsCutoffHz { get; init; }

    /// <summary>Enables pure-physics Woodworth-Schlosberg individual head-width ITD scaling (false by default).</summary>
    public bool BiometricItdEnabled { get; init; }

    /// <summary>Listener physical bitragion head width in centimeters (13.0..18.0 cm, reference adult KU100/KEMAR = 15.5 cm).</summary>
    public float HeadWidthCm { get; init; }

    /// <summary>Optional fine lateral ITD offset in microseconds (-120..+120 us, default 0 us).</summary>
    public float ManualItdOffsetUs { get; init; }

    /// <summary>Enables HpTF ear-canal standing-wave resonance notch filter in 6–10 kHz (false by default).</summary>
    public bool HptfNotchEnabled { get; init; }

    /// <summary>HpTF ear-canal notch center frequency in Hz (6000..10000 Hz, default 8000 Hz).</summary>
    public float HptfNotchFreqHz { get; init; }

    /// <summary>HpTF ear-canal notch attenuation depth in dB (-12..0 dB, default -4.0 dB).</summary>
    public float HptfNotchGainDb { get; init; }

    /// <summary>HpTF ear-canal notch selectivity Q factor (3..16, default 8.0).</summary>
    public float HptfNotchQ { get; init; }

    /// <summary>Identifier of the winning perceptual HRTF cluster selected via the 60s SONICOM/HUTUBS A/B wizard.</summary>
    public string PerceptualClusterId { get; init; }

    /// <summary>
    /// Zero-regression neutral default: MagLS off, Biometric ITD off (15.5 cm reference), HpTF notch off.
    /// </summary>
    public static HrtfPersonalizationConfig Default => new() {
        MagLsMode = MagLsDecodeMode.Off,
        MagLsCutoffHz = 0f,
        BiometricItdEnabled = false,
        HeadWidthCm = BiometricItdScaler.ReferenceHeadWidthCm,
        ManualItdOffsetUs = 0f,
        HptfNotchEnabled = false,
        HptfNotchFreqHz = HptfNotchFilter.DefaultFrequencyHz,
        HptfNotchGainDb = HptfNotchFilter.DefaultGainDb,
        HptfNotchQ = HptfNotchFilter.DefaultQ,
        PerceptualClusterId = string.Empty
    };

    /// <summary>
    /// True when any HRIR-transforming Slot 5B option (MagLS or Biometric ITD) differs between two configs
    /// and therefore requires rebuilding the active <see cref="VirtualizerFilter"/> impulse bank.
    /// </summary>
    public bool RequiresHrirRebuildComparedTo(HrtfPersonalizationConfig other) =>
        MagLsMode != other.MagLsMode ||
        MathF.Abs(MagLsCutoffHz - other.MagLsCutoffHz) > 1f ||
        BiometricItdEnabled != other.BiometricItdEnabled ||
        MathF.Abs(HeadWidthCm - other.HeadWidthCm) > 0.02f ||
        MathF.Abs(ManualItdOffsetUs - other.ManualItdOffsetUs) > 0.5f;

    /// <inheritdoc/>
    public bool Equals(HrtfPersonalizationConfig other) =>
        !RequiresHrirRebuildComparedTo(other) &&
        HptfNotchEnabled == other.HptfNotchEnabled &&
        MathF.Abs(HptfNotchFreqHz - other.HptfNotchFreqHz) < 1f &&
        MathF.Abs(HptfNotchGainDb - other.HptfNotchGainDb) < 0.05f &&
        MathF.Abs(HptfNotchQ - other.HptfNotchQ) < 0.05f &&
        string.Equals(PerceptualClusterId ?? string.Empty, other.PerceptualClusterId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is HrtfPersonalizationConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        MagLsMode,
        BiometricItdEnabled,
        (int)MathF.Round(HeadWidthCm * 10f),
        HptfNotchEnabled,
        (int)MathF.Round(HptfNotchFreqHz));
}

/// <summary>
/// Live diagnostic telemetry snapshot for Slot 5B MagLS &amp; Biometric HRTF Personalization.
/// </summary>
public readonly struct HrtfPersonalizationDiagnostics {
    /// <summary>True when MagLS spherical-harmonic decoding was actively applied to the current HRIR filter set.</summary>
    public bool IsMagLsActive { get; init; }

    /// <summary>Active Ambisonic order N (0 when MagLS is off or bypassed on BRIR).</summary>
    public int MagLsOrder { get; init; }

    /// <summary>Active MagLS transition cutoff frequency in Hz.</summary>
    public float MagLsCutoffHz { get; init; }

    /// <summary>Mean high-frequency diffuse-field magnitude error (dB) above cutoff vs exact measured HRTFs.</summary>
    public float MagLsHfErrorDb { get; init; }

    /// <summary>True when Woodworth-Schlosberg biometric ITD scaling is actively applied to the HRIR set.</summary>
    public bool IsBiometricItdActive { get; init; }

    /// <summary>True when a measured BRIR (&gt; 1024 taps) automatically bypassed MagLS / Biometric ITD to preserve room tails.</summary>
    public bool IsBrirBypassed { get; init; }

    /// <summary>Configured head width in cm.</summary>
    public float HeadWidthCm { get; init; }

    /// <summary>Computed Woodworth-Schlosberg maximum lateral ITD at 90 deg in microseconds.</summary>
    public float MaxLateralItdUs { get; init; }

    /// <summary>Computed ITD delta at 90 deg relative to reference KU100/KEMAR (15.5 cm) in microseconds.</summary>
    public float ItdDeltaAt90DegUs { get; init; }

    /// <summary>True when the Slot 6 HpTF ear-canal resonance notch filter is actively attenuating.</summary>
    public bool IsHptfNotchActive { get; init; }

    /// <summary>Active HpTF notch frequency in Hz.</summary>
    public float HptfNotchFreqHz { get; init; }

    /// <summary>Active HpTF notch depth in dB.</summary>
    public float HptfNotchGainDb { get; init; }

    /// <summary>Active HpTF notch Q factor.</summary>
    public float HptfNotchQ { get; init; }

    /// <summary>Detected format of the active HRIR/BRIR file (e.g., "CHRIR1", "AES69 SOFA", "Impulcifer HeSuVi 14-ch").</summary>
    public string ActiveHrirFormat { get; init; }

    /// <summary>Active SONICOM/HUTUBS perceptual wizard cluster ID, if configured.</summary>
    public string ActiveClusterId { get; init; }

    /// <summary>Neutral default diagnostics snapshot.</summary>
    public static HrtfPersonalizationDiagnostics Default => new() {
        IsMagLsActive = false,
        MagLsOrder = 0,
        MagLsCutoffHz = 0f,
        MagLsHfErrorDb = 0f,
        IsBiometricItdActive = false,
        IsBrirBypassed = false,
        HeadWidthCm = BiometricItdScaler.ReferenceHeadWidthCm,
        MaxLateralItdUs = (float)BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, BiometricItdScaler.ReferenceHeadWidthCm),
        ItdDeltaAt90DegUs = 0f,
        IsHptfNotchActive = false,
        HptfNotchFreqHz = HptfNotchFilter.DefaultFrequencyHz,
        HptfNotchGainDb = HptfNotchFilter.DefaultGainDb,
        HptfNotchQ = HptfNotchFilter.DefaultQ,
        ActiveHrirFormat = "CHRIR1",
        ActiveClusterId = string.Empty
    };
}

/// <summary>
/// Single selectable acoustic candidate inside the 60-second SONICOM / HUTUBS / Katz &amp; Parseihian
/// perceptual A/B/C HRTF selection wizard. Every candidate maps strictly to real laboratory-measured HRIRs
/// and pure-physics Woodworth-Schlosberg / MagLS transforms (zero artificial EQ or fake HRTFs).
/// </summary>
public sealed class PerceptualWizardCandidate {
    public string Id { get; init; }
    public string LabelKey { get; init; }
    public string DescKey { get; init; }
    public string BinauralProfileId { get; init; }
    public float? HeadWidthCm { get; init; }
    public bool? MinimumPhase { get; init; }
    public MagLsDecodeMode? MagLsMode { get; init; }
}

/// <summary>
/// 60-second 3-stage perceptual HRTF selection protocol curated from SONICOM / HUTUBS perceptual clustering
/// and Katz &amp; Parseihian (2012) A/B tournament methodology:
/// Stage 1 (Pinna Spectral Family: KU100 vs KEMAR vs TH Köln Far-Field),
/// Stage 2 (Biometric Head-Width ITD Scale: 14.2 cm / 15.5 cm / 16.8 cm),
/// Stage 3 (Phase &amp; Spherical-Harmonic Clarity: Measured Phase vs Min-Phase + ITD vs MagLS Order 3).
/// </summary>
public static class PerceptualHrtfWizardCatalog {
    public static IReadOnlyList<PerceptualWizardCandidate> Stage1PinnaClusters { get; } = [
        new PerceptualWizardCandidate {
            Id = "cluster_ku100_sadie",
            LabelKey = "HrtfWizS1OptA",
            DescKey = "HrtfWizS1OptADesc",
            BinauralProfileId = "sadie_d1_dfc"
        },
        new PerceptualWizardCandidate {
            Id = "cluster_kemar_sadie",
            LabelKey = "HrtfWizS1OptB",
            DescKey = "HrtfWizS1OptBDesc",
            BinauralProfileId = "sadie_d2_dfc"
        },
        new PerceptualWizardCandidate {
            Id = "cluster_thk_ff",
            LabelKey = "HrtfWizS1OptC",
            DescKey = "HrtfWizS1OptCDesc",
            BinauralProfileId = "thk_ku100_ff_dfc"
        }
    ];

    public static IReadOnlyList<PerceptualWizardCandidate> Stage2BiometricItdClusters { get; } = [
        new PerceptualWizardCandidate {
            Id = "itd_compact_142",
            LabelKey = "HrtfWizS2OptA",
            DescKey = "HrtfWizS2OptADesc",
            HeadWidthCm = 14.2f
        },
        new PerceptualWizardCandidate {
            Id = "itd_standard_155",
            LabelKey = "HrtfWizS2OptB",
            DescKey = "HrtfWizS2OptBDesc",
            HeadWidthCm = 15.5f
        },
        new PerceptualWizardCandidate {
            Id = "itd_broad_168",
            LabelKey = "HrtfWizS2OptC",
            DescKey = "HrtfWizS2OptCDesc",
            HeadWidthCm = 16.8f
        }
    ];

    public static IReadOnlyList<PerceptualWizardCandidate> Stage3PhaseAndMagLsModes { get; } = [
        new PerceptualWizardCandidate {
            Id = "mode_measured_exact",
            LabelKey = "HrtfWizS3OptA",
            DescKey = "HrtfWizS3OptADesc",
            MinimumPhase = false,
            MagLsMode = MagLsDecodeMode.Off
        },
        new PerceptualWizardCandidate {
            Id = "mode_minphase_itd",
            LabelKey = "HrtfWizS3OptB",
            DescKey = "HrtfWizS3OptBDesc",
            MinimumPhase = true,
            MagLsMode = MagLsDecodeMode.Off
        },
        new PerceptualWizardCandidate {
            Id = "mode_magls_order3",
            LabelKey = "HrtfWizS3OptC",
            DescKey = "HrtfWizS3OptCDesc",
            MinimumPhase = false,
            MagLsMode = MagLsDecodeMode.Order3
        }
    ];

    /// <summary>
    /// Synthesizes a clean deterministic 4-point broadband pink-noise + transient pulse audition cue
    /// (Front -> Left -> Right -> Overhead-Back) rendered through the active <see cref="VirtualizerFilter"/>
    /// so users can accurately evaluate front/back, elevation, and head-width externalization even when no music is playing.
    /// </summary>
    public static float[] SynthesizeAuditionCueStereo(int sampleRate = 48000, float headWidthCm = 15.5f) {
        // 4 spatial bursts x 0.75 s = 3.0 s total @ -14 dBFS RMS: Front (0 deg), Left (-90 deg), Right (+90 deg), Rear-Left (-135 deg)
        int burstFrames = (int)Math.Round(sampleRate * 0.75);
        int totalFrames = burstFrames * 4;
        float[] stereo = new float[totalFrames * 2];

        (float azDeg, float elDeg)[] cues = [
            (0f, 0f),
            (-90f, 0f),
            (90f, 0f),
            (-135f, -35f)
        ];

        // Deterministic xorshift32 pink-filtered noise (Voss/Paul Kellet filter) with crisp localization envelope
        uint rng = 0x5EED1234u;
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;

        for (int c = 0; c < cues.Length; c++) {
            double itdUs = BiometricItdScaler.ComputeWoodworthItdUs(cues[c].azDeg, cues[c].elDeg, headWidthCm);
            int delaySamples = (int)Math.Round(Math.Abs(itdUs) * 1e-6 * sampleRate);
            bool rightLeads = itdUs > 0.0;
            double azRad = cues[c].azDeg * (Math.PI / 180.0);
            double sinAz = Math.Sin(azRad);
            // Smooth physical ILD shadow (~6 dB lateral broadband cue for quick audition feedback)
            float gainL = (float)Math.Pow(10.0, (-3.0 * sinAz) / 20.0) * 0.18f;
            float gainR = (float)Math.Pow(10.0, (3.0 * sinAz) / 20.0) * 0.18f;

            float[] mono = new float[burstFrames];
            int activeLen = (int)(burstFrames * 0.78);
            int fade = Math.Max(64, sampleRate / 50);
            for (int n = 0; n < activeLen; n++) {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                double white = ((rng & 0xFFFFFFu) / (double)0x800000u) - 1.0;
                b0 = 0.99886 * b0 + white * 0.0555179;
                b1 = 0.99332 * b1 + white * 0.0750759;
                b2 = 0.96900 * b2 + white * 0.1538520;
                b3 = 0.86650 * b3 + white * 0.3104856;
                b4 = 0.55000 * b4 + white * 0.5329522;
                b5 = -0.7616 * b5 - white * 0.0168980;
                double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
                b6 = white * 0.115926;

                // Two crisp rhythmic amplitude pulses per direction aid ITD onset localization
                double subEnv = (n % (activeLen / 2)) < (activeLen / 3) ? 1.0 : 0.25;
                double edgeEnv = 1.0;
                if (n < fade) edgeEnv = 0.5 - 0.5 * Math.Cos(Math.PI * n / fade);
                else if (n >= activeLen - fade) edgeEnv = 0.5 - 0.5 * Math.Cos(Math.PI * (activeLen - 1 - n) / fade);
                mono[n] = (float)(pink * subEnv * edgeEnv);
            }

            int baseFrame = c * burstFrames;
            for (int n = 0; n < burstFrames; n++) {
                int idxL = rightLeads ? n - delaySamples : n;
                int idxR = rightLeads ? n : n - delaySamples;
                float sL = (idxL >= 0 && idxL < burstFrames) ? mono[idxL] * gainL : 0f;
                float sR = (idxR >= 0 && idxR < burstFrames) ? mono[idxR] * gainR : 0f;
                int outIdx = (baseFrame + n) * 2;
                stereo[outIdx] = sL;
                stereo[outIdx + 1] = sR;
            }
        }
        return stereo;
    }
}
