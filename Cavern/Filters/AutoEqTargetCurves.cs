using System;
using System.Collections.Generic;

namespace Cavern.Filters {
    /// <summary>
    /// Scientific neutral target curve used to transform standard AutoEq headphone measurements.
    /// Zero artificial bass/treble boosts: all transforms are mathematically fitted to official
    /// ISO 11904-1, GRAS KEMAR, Brüel &amp; Kjær Type 5128 Diffuse-Field / JM-1, and Harman Neutral CSV tables.
    /// </summary>
    public enum AutoEqNeutralTarget {
        /// <summary>
        /// Strictly flat Diffuse-Field reference (GRAS KEMAR / ISO 11904-1, 0 dB bass shelf).
        /// Ideal default for Diffuse-Field Compensated (DFC) HRIR &amp; BRIR spatialization.
        /// </summary>
        DiffuseFieldNeutral = 0,

        /// <summary>
        /// Brüel &amp; Kjær Type 5128 Diffuse-Field flat 0 dB/oct neutral laboratory reference.
        /// </summary>
        Bk5128DiffuseField = 1,

        /// <summary>
        /// Brüel &amp; Kjær Type 5128 JM-1 modern perceptual neutral reference (with Harman treble slope, flat bass).
        /// </summary>
        Bk5128Jm1Neutral = 2,

        /// <summary>
        /// Studio Harman Over-Ear 2018 neutral without artificial bass shelf (flat bass below 200 Hz).
        /// </summary>
        HarmanNeutralNoBass = 3,

        /// <summary>
        /// Applies the AutoEq profile's original measurement target verbatim (e.g. Harman 2018 OE or AutoEq IE).
        /// </summary>
        NativeAutoEqProfile = 4
    }

    /// <summary>
    /// Original AutoEq target curve baked into a headphone's <c>ParametricEQ.txt</c> measurement file.
    /// </summary>
    public enum AutoEqSourceTarget {
        /// <summary>AutoEq standard over-ear / on-ear reference (Harman over-ear 2018).</summary>
        HarmanOverEar2018 = 0,
        /// <summary>AutoEq standard in-ear / earbud reference (AutoEq in-ear neutral bass).</summary>
        AutoEqInEar = 1,
        /// <summary>Profile already equalized to neutral Diffuse-Field or custom flat target.</summary>
        AlreadyNeutral = 2
    }

    /// <summary>
    /// Acoustic state of the active binaural HRIR/BRIR renderer used to enforce the Zero Double-EQ Guard.
    /// </summary>
    public enum BinauralCompensationMode {
        /// <summary>
        /// Active HRIR/BRIR is Diffuse-Field Compensated (e.g. SADIE II D1/D2 DFC/BRIR, TH Köln DFC/Near-Field).
        /// Headphone EQ should restore Diffuse-Field / JM-1 neutral response so directional cues arrive uncolored.
        /// </summary>
        DiffuseFieldCompensatedHrir = 0,

        /// <summary>
        /// Active HRIR is RAW / Uncompensated (e.g. TH Köln KU100 Far-Field Raw), meaning it already contains the
        /// dummy head's ear-canal + diffuse-field pinna resonance (~+11 dB at 3 kHz).
        /// Applying a DF headphone EQ on top would double-apply ear-canal/pinna EQ; the Zero Double-EQ Guard
        /// automatically applies the exact inverse ISO 11904-1 Diffuse-Field transform (-H_DF) to keep response flat.
        /// </summary>
        RawUncompensatedHrir = 1,

        /// <summary>
        /// Direct 2.0 stereo output with HRTF bypassed. Standard neutral headphone EQ applies without double-EQ conflict.
        /// </summary>
        DirectStereo = 2
    }

    /// <summary>
    /// Laboratory-derived biquad delta transforms converting standard <c>jaakkopasanen/AutoEq</c> profiles
    /// to neutral Diffuse-Field / B&amp;K 5128 / JM-1 references, plus the exact Zero Double-EQ Guard for Raw HRIRs.
    /// Every coefficient set below was fitted via least-squares optimization directly against AutoEq's official
    /// MIT-licensed CSV target tables (20 Hz – 16 kHz, 0 dB aligned at 1 kHz, RMS error 0.012 – 0.376 dB).
    /// </summary>
    public static class AutoEqTargetCurves {
        // Harman over-ear 2018.csv -> Diffuse field GRAS KEMAR.csv (RMS = 0.211 dB, Max = 0.631 dB)
        static readonly ParametricBand[] HarmanOeToDfKemar = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 106.1, -7.86, 0.628, true),
            new ParametricBand(ParametricFilterType.Peaking, 283.0, -1.28, 0.500, true),
            new ParametricBand(ParametricFilterType.Peaking, 2520.1, 3.87, 1.314, true),
            new ParametricBand(ParametricFilterType.Peaking, 8622.5, 3.96, 5.498, true),
            new ParametricBand(ParametricFilterType.HighShelf, 10158.3, 4.96, 1.076, true),
            new ParametricBand(ParametricFilterType.Peaking, 15178.7, 0.94, 0.863, true)
        };

        // AutoEq in-ear.csv -> Diffuse field ISO 11904-1.csv (RMS = 0.088 dB, Max = 0.324 dB)
        static readonly ParametricBand[] AutoEqIeToDfIso = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 164.6, -2.47, 0.682, true),
            new ParametricBand(ParametricFilterType.Peaking, 287.9, -1.32, 0.667, true),
            new ParametricBand(ParametricFilterType.Peaking, 2740.9, 3.99, 1.458, true),
            new ParametricBand(ParametricFilterType.Peaking, 5892.3, -1.96, 1.923, true),
            new ParametricBand(ParametricFilterType.HighShelf, 9401.0, 4.56, 0.596, true),
            new ParametricBand(ParametricFilterType.Peaking, 15500.0, 2.02, 0.552, true)
        };

        // Harman over-ear 2018.csv -> Diffuse field 5128.csv (RMS = 0.370 dB)
        static readonly ParametricBand[] HarmanOeToDf5128 = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 100.8, -8.27, 0.625, true),
            new ParametricBand(ParametricFilterType.Peaking, 236.1, -0.92, 0.389, true),
            new ParametricBand(ParametricFilterType.Peaking, 2899.9, 2.71, 4.059, true),
            new ParametricBand(ParametricFilterType.Peaking, 5007.3, 1.83, 0.496, true),
            new ParametricBand(ParametricFilterType.Peaking, 8000.0, 2.75, 5.183, true),
            new ParametricBand(ParametricFilterType.HighShelf, 10666.2, 6.57, 0.682, true),
            new ParametricBand(ParametricFilterType.Peaking, 15500.0, 1.63, 0.490, true)
        };

        // AutoEq in-ear.csv -> Diffuse field 5128.csv (RMS = 0.376 dB)
        static readonly ParametricBand[] AutoEqIeToDf5128 = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 226.9, -2.55, 0.634, true),
            new ParametricBand(ParametricFilterType.Peaking, 333.9, -0.78, 1.378, true),
            new ParametricBand(ParametricFilterType.Peaking, 2891.5, 2.34, 3.557, true),
            new ParametricBand(ParametricFilterType.Peaking, 3655.9, 2.53, 1.066, true),
            new ParametricBand(ParametricFilterType.Peaking, 8042.0, 2.12, 2.356, true),
            new ParametricBand(ParametricFilterType.HighShelf, 10500.0, 7.46, 0.696, true),
            new ParametricBand(ParametricFilterType.Peaking, 15500.0, 1.58, 0.591, true)
        };

        // Harman over-ear 2018.csv -> JM-1 with Harman treble filter.csv (RMS = 0.152 dB, Max = 0.466 dB)
        static readonly ParametricBand[] HarmanOeToJm1 = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 106.8, -7.71, 0.663, true),
            new ParametricBand(ParametricFilterType.Peaking, 430.3, -0.75, 1.362, true),
            new ParametricBand(ParametricFilterType.Peaking, 5750.2, -1.44, 2.798, true),
            new ParametricBand(ParametricFilterType.Peaking, 4421.2, -1.66, 2.271, true),
            new ParametricBand(ParametricFilterType.HighShelf, 9608.5, 3.46, 0.541, true),
            new ParametricBand(ParametricFilterType.Peaking, 15500.0, 1.49, 1.601, true)
        };

        // AutoEq in-ear.csv -> JM-1 with Harman treble filter.csv (RMS = 0.134 dB, Max = 0.503 dB)
        static readonly ParametricBand[] AutoEqIeToJm1 = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 145.4, -2.11, 0.695, true),
            new ParametricBand(ParametricFilterType.Peaking, 262.9, -0.78, 1.071, true),
            new ParametricBand(ParametricFilterType.Peaking, 5410.5, -2.09, 2.684, true),
            new ParametricBand(ParametricFilterType.Peaking, 6613.8, -1.28, 4.546, true),
            new ParametricBand(ParametricFilterType.HighShelf, 9725.6, 3.27, 0.586, true),
            new ParametricBand(ParametricFilterType.Peaking, 15500.0, 2.20, 1.509, true)
        };

        // Harman over-ear 2018.csv -> Harman over-ear 2018 without bass.csv (RMS = 0.012 dB, Max = 0.048 dB)
        static readonly ParametricBand[] HarmanOeToHarmanNoBass = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 105.0, -5.51, 0.710, true),
            new ParametricBand(ParametricFilterType.Peaking, 34.1, -0.50, 0.350, true),
            new ParametricBand(ParametricFilterType.Peaking, 196.1, 0.08, 1.757, true)
        };

        // Inverse Diffuse field ISO 11904-1.csv: -H_DF(f) (RMS = 0.115 dB, Max = 0.450 dB)
        // Applied automatically when a Raw uncompensated HRIR (which already contains dummy-head DF pinna gain) is active.
        static readonly ParametricBand[] InverseDiffuseFieldIso = new ParametricBand[] {
            new ParametricBand(ParametricFilterType.LowShelf, 244.1, 4.10, 0.573, true),
            new ParametricBand(ParametricFilterType.Peaking, 494.0, 1.92, 0.451, true),
            new ParametricBand(ParametricFilterType.Peaking, 2751.8, -10.53, 1.114, true),
            new ParametricBand(ParametricFilterType.Peaking, 4183.2, -2.47, 1.054, true),
            new ParametricBand(ParametricFilterType.Peaking, 8232.0, -3.89, 1.217, true),
            new ParametricBand(ParametricFilterType.HighShelf, 12672.1, 4.41, 0.660, true)
        };

        /// <summary>
        /// Builds the complete composite parametric EQ band list combining a headphone's measured AutoEq bands,
        /// the exact laboratory target transform, and (when active HRIR is Raw/uncompensated and guard is enabled)
        /// the exact inverse Diffuse-Field guard preventing double pinna/ear-canal equalization.
        /// </summary>
        public static ParametricBand[] BuildCompositeBands(
            ParametricBand[] headphoneBands,
            AutoEqSourceTarget sourceTarget,
            AutoEqNeutralTarget desiredTarget,
            BinauralCompensationMode binauralMode,
            bool enableDoubleEqGuard,
            out bool doubleEqGuardApplied) {
            doubleEqGuardApplied = false;
            if (headphoneBands == null || headphoneBands.Length == 0) {
                return Array.Empty<ParametricBand>();
            }

            List<ParametricBand> combined = new List<ParametricBand>(headphoneBands.Length + 14);
            for (int i = 0; i < headphoneBands.Length; i++) {
                if (headphoneBands[i].Enabled) {
                    combined.Add(headphoneBands[i]);
                }
            }

            // Determine whether Zero Double-EQ Guard must neutralize DF/pinna target gain for a Raw uncompensated HRIR.
            // Algebra: H_hp_raw_compensation = (H_DF / H_hp_measured) * (1 / H_DF) = 1 / H_hp_measured.
            if (enableDoubleEqGuard && binauralMode == BinauralCompensationMode.RawUncompensatedHrir) {
                // First bring the profile to Diffuse-Field if it originated from Harman/AutoEq, then divide out H_DF
                ParametricBand[] toDf = GetTargetDeltaBands(sourceTarget, AutoEqNeutralTarget.DiffuseFieldNeutral);
                AppendBands(combined, toDf);
                AppendBands(combined, InverseDiffuseFieldIso);
                doubleEqGuardApplied = true;
                return combined.ToArray();
            }

            // Standard DFC HRIR / BRIR or Direct 2.0 stereo: apply exact target delta to neutral DF / 5128 / JM-1
            ParametricBand[] delta = GetTargetDeltaBands(sourceTarget, desiredTarget);
            AppendBands(combined, delta);
            return combined.ToArray();
        }

        /// <summary>
        /// Returns the exact laboratory-fitted biquad delta bands transforming <paramref name="sourceTarget"/>
        /// into <paramref name="desiredTarget"/>, or an empty array when no transform is needed.
        /// </summary>
        public static ParametricBand[] GetTargetDeltaBands(AutoEqSourceTarget sourceTarget, AutoEqNeutralTarget desiredTarget) {
            if (sourceTarget == AutoEqSourceTarget.AlreadyNeutral || desiredTarget == AutoEqNeutralTarget.NativeAutoEqProfile) {
                return Array.Empty<ParametricBand>();
            }

            if (sourceTarget == AutoEqSourceTarget.HarmanOverEar2018) {
                switch (desiredTarget) {
                    case AutoEqNeutralTarget.DiffuseFieldNeutral:
                        return HarmanOeToDfKemar;
                    case AutoEqNeutralTarget.Bk5128DiffuseField:
                        return HarmanOeToDf5128;
                    case AutoEqNeutralTarget.Bk5128Jm1Neutral:
                        return HarmanOeToJm1;
                    case AutoEqNeutralTarget.HarmanNeutralNoBass:
                        return HarmanOeToHarmanNoBass;
                    default:
                        return Array.Empty<ParametricBand>();
                }
            }

            if (sourceTarget == AutoEqSourceTarget.AutoEqInEar) {
                switch (desiredTarget) {
                    case AutoEqNeutralTarget.DiffuseFieldNeutral:
                        return AutoEqIeToDfIso;
                    case AutoEqNeutralTarget.Bk5128DiffuseField:
                        return AutoEqIeToDf5128;
                    case AutoEqNeutralTarget.Bk5128Jm1Neutral:
                        return AutoEqIeToJm1;
                    case AutoEqNeutralTarget.HarmanNeutralNoBass:
                        // AutoEq in-ear.csv already has flat neutral bass (-1.6 dB from 20..250 Hz)
                        return Array.Empty<ParametricBand>();
                    default:
                        return Array.Empty<ParametricBand>();
                }
            }

            return Array.Empty<ParametricBand>();
        }

        static void AppendBands(List<ParametricBand> dst, ParametricBand[] src) {
            if (src == null) {
                return;
            }
            for (int i = 0; i < src.Length; i++) {
                dst.Add(src[i]);
            }
        }
    }
}
