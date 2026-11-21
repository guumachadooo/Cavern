using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cavern.Filters {
    /// <summary>
    /// Zero-smearing Spatial Dialog Enhancer (Slot 6B) supporting two complementary physical modes:
    /// <list type="bullet">
    ///   <item><b>Path A (Spatial Multichannel / Atmos / 5.1 / 7.1):</b> Isolates dedicated Center bed and
    ///   front-center dialogue-cone 3D objects directly in the pre-virtualizer spatial domain using pure gain
    ///   scaling with zero EQ coloration or phase smearing of surround/music beds.</item>
    ///   <item><b>Path B (Stereo 2.0 Fallback):</b> Phase-coherent Mid/Side speech-band (250–3600 Hz) isolation
    ///   using a Linkwitz-Riley 4th-order (LR4) complementary crossover + Mid-dominance coherence gate,
    ///   lifting only center-panned speech energy while leaving stereo instruments (Side) and bass/air 100% untouched.</item>
    /// </list>
    /// Exposes an <see cref="ExternalSpeechActivity"/> hook ready for W08/W09 lossless-twin or neural vocal guidance.
    /// </summary>
    public sealed class SpatialDialogEnhancer {
        /// <summary>Minimum dialogue boost in dB (0.0 dB = exact unity pass-through).</summary>
        public const float MinBoostDb = 0f;

        /// <summary>Maximum dialogue boost in dB (+6.0 dB studio intelligibility ceiling).</summary>
        public const float MaxBoostDb = 6f;

        /// <summary>Default gentle dialogue intelligibility lift in dB.</summary>
        public const float DefaultBoostDb = 3f;

        /// <summary>Lower crossover frequency of the human speech core band (250 Hz).</summary>
        const double SpeechLowHz = 250.0;

        /// <summary>Upper crossover frequency of the human speech intelligibility band (3600 Hz).</summary>
        const double SpeechHighHz = 3600.0;

        const double DenormalGuard = 1e-24;

        // LR4 = two cascaded Butterworth 2nd-order (Q = 1/sqrt(2)) filters; LP + HP sums to all-pass flat magnitude!
        struct BiquadCoeffs {
            public double B0, B1, B2, A1, A2;
        }

        struct BiquadState {
            public double Z1, Z2;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public double Process(double x, ref BiquadCoeffs c) {
                double y = c.B0 * x + Z1;
                Z1 = c.B1 * x - c.A1 * y + Z2 + DenormalGuard;
                Z2 = c.B2 * x - c.A2 * y;
                return y;
            }

            public void Clear() {
                Z1 = 0.0;
                Z2 = 0.0;
            }
        }

        BiquadCoeffs lpLowC, hpLowC, lpHighC, hpHighC;

        // Mid-channel 3-band LR4 crossover states (Low < 250 Hz, Mid Speech 250..3600 Hz, High > 3600 Hz)
        BiquadState mLpLow1, mLpLow2, mHpLow1, mHpLow2;
        BiquadState mLpHigh1, mLpHigh2, mHpHigh1, mHpHigh2;
        // Phase-alignment 2nd-order all-pass matching H_LP_LR4 + H_HP_LR4 phase on the Low band (exact 0.000 dB flat sum!)
        BiquadState mLowAp;
        BiquadCoeffs apHighC;

        // Side-channel speech-band detector states (only for coherence gating; Side audio itself passes bit-untouched!)
        BiquadState sHpLow1, sHpLow2, sLpHigh1, sLpHigh2;

        int sampleRate = Listener.DefaultSampleRate;
        bool enabled;
        float boostDb = DefaultBoostDb;
        float smoothedSpeechActivity;
        double midSpeechEnv;
        double sideSpeechEnv;
        double envAlpha = 0.001;
        double gateAttackAlpha = 0.004;
        double gateReleaseAlpha = 0.0004;

        /// <summary>
        /// Optional external speech-activity probability [0; 1] provided by W09 Lossless Twin or W08 Neural Vocal Mask.
        /// Set to <see cref="float.NaN"/> (default) to use built-in Mid/Side coherence detection.
        /// </summary>
        public float ExternalSpeechActivity { get; set; } = float.NaN;

        /// <summary>Creates a Spatial Dialog Enhancer (disabled by default for zero-regression neutrality).</summary>
        public SpatialDialogEnhancer(int sampleRate = Listener.DefaultSampleRate) {
            Configure(sampleRate);
        }

        /// <summary>True when enabled and boost &gt; 0.05 dB.</summary>
        public bool IsActive => enabled && boostDb > 0.05f;

        /// <summary>Configured dialogue boost in dB (0..6 dB).</summary>
        public float BoostDb => boostDb;

        /// <summary>Configured sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Latest smoothed speech-activity confidence in [0; 1].</summary>
        public float SpeechActivity => smoothedSpeechActivity;

        /// <summary>Atomically updates enable state and boost depth in dB.</summary>
        public void SetParameters(bool enabled, float boostDb) {
            this.enabled = enabled;
            this.boostDb = Math.Clamp(boostDb, MinBoostDb, MaxBoostDb);
        }

        /// <summary>Configures LR4 crossover and envelope time constants for <paramref name="newSampleRate"/>.</summary>
        public void Configure(int newSampleRate) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            sampleRate = sr;

            double qButt = 0.7071067811865476;
            lpLowC = DesignButterworthLowPass(sr, SpeechLowHz, qButt);
            hpLowC = DesignButterworthHighPass(sr, SpeechLowHz, qButt);
            lpHighC = DesignButterworthLowPass(sr, SpeechHighHz, qButt);
            hpHighC = DesignButterworthHighPass(sr, SpeechHighHz, qButt);
            apHighC = DesignSecondOrderAllPass(sr, SpeechHighHz, qButt);

            envAlpha = 1.0 - Math.Exp(-1.0 / (sr * 0.030));       // 30 ms energy RMS window
            gateAttackAlpha = 1.0 - Math.Exp(-1.0 / (sr * 0.015)); // 15 ms speech onset
            gateReleaseAlpha = 1.0 - Math.Exp(-1.0 / (sr * 0.160)); // 160 ms syllable hold
            Reset();
        }

        /// <summary>Resets all crossover filter states and speech activity followers.</summary>
        public void Reset() {
            mLpLow1.Clear();
            mLpLow2.Clear();
            mHpLow1.Clear();
            mHpLow2.Clear();
            mLpHigh1.Clear();
            mLpHigh2.Clear();
            mHpHigh1.Clear();
            mHpHigh2.Clear();
            mLowAp.Clear();
            sHpLow1.Clear();
            sHpLow2.Clear();
            sLpHigh1.Clear();
            sLpHigh2.Clear();
            midSpeechEnv = 0.0;
            sideSpeechEnv = 0.0;
            smoothedSpeechActivity = 0f;
        }

        /// <summary>
        /// Evaluates whether a spatial source at normalized room position <paramref name="normalizedPos"/>
        /// qualifies as a front-center dialogue carrier for Path A pre-virtualizer spatial isolation
        /// (either the dedicated FrontCenter bed or a non-LFE dynamic object inside the frontal dialogue cone).
        /// </summary>
        public static bool IsDialogueCandidate(bool isFrontCenterBed, bool isLfe, Vector3 normalizedPos) {
            if (isLfe) {
                return false;
            }
            if (isFrontCenterBed) {
                return true;
            }
            // Front-center dialogue cone in Cavern normalized coordinates (X=left/right, Y=height, Z=front +1)
            return MathF.Abs(normalizedPos.X) <= 0.25f
                && normalizedPos.Z >= 0.35f
                && MathF.Abs(normalizedPos.Y) <= 0.30f;
        }

        /// <summary>
        /// Computes pure linear gain multipliers for Path A (pre-virtualizer spatial isolation) given
        /// <paramref name="boostDb"/> (0..6 dB). Dialogue sources receive linear lift while non-dialogue
        /// beds receive subtle complementary headroom preservation (-0.35 * boost) so overall loudness never jumps.
        /// </summary>
        public static void ComputeSpatialGains(
            bool enabled,
            float boostDb,
            bool hasDialogueSource,
            out float dialogueLinearGain,
            out float ambientLinearGain) {
            float clamped = Math.Clamp(boostDb, MinBoostDb, MaxBoostDb);
            if (!enabled || !hasDialogueSource || clamped <= 0.05f) {
                dialogueLinearGain = 1f;
                ambientLinearGain = 1f;
                return;
            }
            // Split total contrast across dialogue lift (+0.65 * boost) and gentle surround bed headroom (-0.35 * boost)
            // so dialogue rises by the exact requested boostDb relative to the room without driving the limiter.
            dialogueLinearGain = MathF.Pow(10f, (clamped * 0.65f) * 0.05f);
            ambientLinearGain = MathF.Pow(10f, (-clamped * 0.35f) * 0.05f);
        }

        /// <summary>
        /// Path B (Stereo 2.0 Fallback): Applies phase-coherent Mid-channel speech-band (250–3600 Hz) enhancement
        /// gated by Mid-vs-Side spatial coherence (or <see cref="ExternalSpeechActivity"/> when supplied by W08/W09).
        /// Side channel (stereo panned music/ambience) passes 100% bit-identically untouched; zero heap allocations.
        /// </summary>
        public void ProcessStereoInterleaved(float[] buffer, int offset, int frameCount) {
            if (!IsActive || buffer == null || frameCount <= 0) {
                return;
            }
            int end = offset + frameCount * 2;
            if (offset < 0 || end > buffer.Length) {
                return;
            }

            double maxSpeechGainDelta = Math.Pow(10.0, boostDb * 0.05) - 1.0;
            bool hasExternalVad = !float.IsNaN(ExternalSpeechActivity);
            double extVad = hasExternalVad ? Math.Clamp(ExternalSpeechActivity, 0f, 1f) : 0.0;
            double act = smoothedSpeechActivity;

            for (int f = 0; f < frameCount; f++) {
                int idx = offset + f * 2;
                double l = buffer[idx];
                double r = buffer[idx + 1];

                // Exact Mid/Side decomposition (L = M + S, R = M - S)
                double mid = 0.5 * (l + r);
                double side = 0.5 * (l - r);

                // Phase-coherent 3-band LR4 split of Mid channel:
                // Low = AP_high(LP_low(M)), Speech = LP_high(HP_low(M)), Air = HP_high(HP_low(M))
                // Sum Low + Speech + Air == all-pass flat magnitude (0.000 dB ripple) at unity gain!
                double mLowRaw = mLpLow2.Process(mLpLow1.Process(mid, ref lpLowC), ref lpLowC);
                double mLow = mLowAp.Process(mLowRaw, ref apHighC);

                double mAboveLow = mHpLow2.Process(mHpLow1.Process(mid, ref hpLowC), ref hpLowC);
                double mSpeech = mLpHigh2.Process(mLpHigh1.Process(mAboveLow, ref lpHighC), ref lpHighC);
                double mAir = mHpHigh2.Process(mHpHigh1.Process(mAboveLow, ref hpHighC), ref hpHighC);

                // Measure Side speech-band energy solely for Mid-dominance spatial coherence gate
                double sAboveLow = sHpLow2.Process(sHpLow1.Process(side, ref hpLowC), ref hpLowC);
                double sSpeech = sLpHigh2.Process(sLpHigh1.Process(sAboveLow, ref lpHighC), ref lpHighC);

                midSpeechEnv += (mSpeech * mSpeech - midSpeechEnv) * envAlpha;
                sideSpeechEnv += (sSpeech * sSpeech - sideSpeechEnv) * envAlpha;

                double targetActivity;
                if (hasExternalVad) {
                    targetActivity = extVad;
                } else {
                    double totalEnv = midSpeechEnv + sideSpeechEnv;
                    if (totalEnv < 1e-8) { // Below -80 dBFS noise floor: do not amplify hiss
                        targetActivity = 0.0;
                    } else {
                        // Coherence ratio in [0; 1]: 1.0 = pure center mono voice, 0.5 = uncorrelated stereo noise
                        double midRatio = midSpeechEnv / totalEnv;
                        // Smooth ramp engaging cleanly above 0.58 center dominance up to 0.88 full lock
                        targetActivity = Math.Clamp((midRatio - 0.58) / 0.30, 0.0, 1.0);
                    }
                }

                double alpha = targetActivity > act ? gateAttackAlpha : gateReleaseAlpha;
                act += (targetActivity - act) * alpha;

                double speechGain = 1.0 + maxSpeechGainDelta * act;
                double enhancedMid = mLow + mSpeech * speechGain + mAir;

                // Reconstruct L/R with Side channel 100% bit-identical
                buffer[idx] = (float)(enhancedMid + side);
                buffer[idx + 1] = (float)(enhancedMid - side);
            }

            smoothedSpeechActivity = (float)act;
        }

        static BiquadCoeffs DesignButterworthLowPass(double fs, double fc, double q) {
            double w0 = 2.0 * Math.PI * Math.Clamp(fc, 10.0, fs * 0.45) / fs;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW / (2.0 * q);
            double a0 = 1.0 + alpha;
            double b1 = 1.0 - cosW;
            return new BiquadCoeffs {
                B0 = (0.5 * b1) / a0,
                B1 = b1 / a0,
                B2 = (0.5 * b1) / a0,
                A1 = (-2.0 * cosW) / a0,
                A2 = (1.0 - alpha) / a0
            };
        }

        static BiquadCoeffs DesignButterworthHighPass(double fs, double fc, double q) {
            double w0 = 2.0 * Math.PI * Math.Clamp(fc, 10.0, fs * 0.45) / fs;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW / (2.0 * q);
            double a0 = 1.0 + alpha;
            double b1 = -(1.0 + cosW);
            return new BiquadCoeffs {
                B0 = (-0.5 * b1) / a0,
                B1 = b1 / a0,
                B2 = (-0.5 * b1) / a0,
                A1 = (-2.0 * cosW) / a0,
                A2 = (1.0 - alpha) / a0
            };
        }

        static BiquadCoeffs DesignSecondOrderAllPass(double fs, double fc, double q) {
            double w0 = 2.0 * Math.PI * Math.Clamp(fc, 10.0, fs * 0.45) / fs;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW / (2.0 * q);
            double a0 = 1.0 + alpha;
            return new BiquadCoeffs {
                B0 = (1.0 - alpha) / a0,
                B1 = (-2.0 * cosW) / a0,
                B2 = (1.0 + alpha) / a0,
                A1 = (-2.0 * cosW) / a0,
                A2 = (1.0 - alpha) / a0
            };
        }
    }
}
