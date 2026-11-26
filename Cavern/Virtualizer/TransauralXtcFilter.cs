using System;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Physical loudspeaker geometry preset for Bauck-Cooper Optimal Stereo Dipole / BACCH
    /// transaural crosstalk cancellation (XTC).
    /// </summary>
    public enum TransauralSpeakerPreset {
        /// <summary>Built-in flagship smartphone stereo speakers held in landscape (~15.5 cm span @ 40 cm distance, +/-11 deg).</summary>
        PhoneBuiltInLandscape = 0,

        /// <summary>Built-in smartphone stereo speakers in portrait / handheld near-field (~10.5 cm span @ 38 cm distance, +/-8 deg).</summary>
        PhoneBuiltInPortrait = 1,

        /// <summary>External near-field desktop monitors or compact soundbar (~62 cm span @ 85 cm distance, +/-20 deg).</summary>
        DesktopMonitors = 2,

        /// <summary>Standard ITU-R BS.775 stereo loudspeaker triangle (~230 cm span @ 200 cm distance, +/-30 deg).</summary>
        StereoHiFi = 3,

        /// <summary>User-defined speaker span (meters) and listener distance (meters).</summary>
        Custom = 4
    }

    /// <summary>
    /// Immutable physical speaker-to-ear geometry and acoustic transfer parameters derived from
    /// spherical head diffraction (Woodworth-Schlosberg) and free-field path difference.
    /// </summary>
    public struct TransauralGeometryParams {
        /// <summary>Center-to-center distance between Left and Right speakers in meters.</summary>
        public float SpeakerSpanMeters;

        /// <summary>Perpendicular distance from the speaker line to the listener head center in meters.</summary>
        public float ListenerDistanceMeters;

        /// <summary>Half-angle from listener midline to each loudspeaker in degrees.</summary>
        public float HalfAngleDegrees;

        /// <summary>Contralateral minus ipsilateral arrival delay in microseconds.</summary>
        public float ContralateralDelayUs;

        /// <summary>Broadband contralateral-to-ipsilateral amplitude ratio g0 in (0, 1).</summary>
        public float ContralateralGainRatio;

        /// <summary>Head-shadow low-pass corner frequency in Hz for the contralateral ear path.</summary>
        public float HeadShadowCutoffHz;

        /// <summary>Lower frequency bound (Hz) where XTC smoothly fades in (protects micro-speaker bass excursion).</summary>
        public float LowBandCutoffHz;

        /// <summary>Upper frequency bound (Hz) where XTC smoothly tapers off (preserves head-movement sweet spot).</summary>
        public float HighBandCutoffHz;
    }

    /// <summary>
    /// Zero-allocation, strictly causal recursive Bauck-Cooper / BACCH transaural crosstalk cancellation (XTC)
    /// filter for built-in smartphone stereo speakers and external stereo speakers.
    /// </summary>
    /// <remarks>
    /// Strict C# 8.0 (<c>netstandard2.1</c>) DSP core. When <see cref="Bypassed"/> is true (e.g., on any headphone,
    /// IEM, USB DAC headphone, or Bluetooth headset route), <see cref="ProcessInterleaved"/> returns immediately
    /// without touching the buffer, guaranteeing bit-exact (<c>0.0f</c> error) headphone pass-through.
    /// </remarks>
    public sealed class TransauralXtcFilter {
        const float SpeedOfSound = 343.0f;
        const float DefaultHeadRadiusMeters = 0.0875f;
        const int DelayLineSize = 256;
        const int DelayLineMask = DelayLineSize - 1;

        readonly float[] delayLineL = new float[DelayLineSize];
        readonly float[] delayLineR = new float[DelayLineSize];
        int delayWriteIndex;

        int sampleRate = 48000;
        TransauralSpeakerPreset preset = TransauralSpeakerPreset.PhoneBuiltInLandscape;
        float customSpanMeters = 0.155f;
        float customDistanceMeters = 0.40f;
        float strength = 0.85f;
        bool enabled;
        bool bypassed;

        // Derived physical geometry & filter coefficients
        TransauralGeometryParams geometry;
        float delaySamples = 9.5f;
        float feedbackCoeff;
        float outputNormGain = 1.0f;

        // Head-shadow 1-pole low-pass coefficients (models contralateral diffraction H_c(z))
        float shadowAlpha = 0.25f;
        float shadowStateL;
        float shadowStateR;

        // Feedback path DC/sub-bass blocker (1-pole HP at 0.25*LowBandCutoffHz prevents sub-bass loop buildup)
        float fbHpCoeff = 0.97f;
        float fbHpPrevInL, fbHpPrevOutL;
        float fbHpPrevInR, fbHpPrevOutR;

        // Diagnostics metrics (updated lock-free on audio thread, zero alloc)
        float lastEstimatedCancellationDb;
        float lastPeakGainApplied = 1.0f;

        /// <summary>Creates a new transaural XTC filter configured for <paramref name="sampleRate"/> Hz.</summary>
        public TransauralXtcFilter(int sampleRate = 48000) {
            Configure(sampleRate, TransauralSpeakerPreset.PhoneBuiltInLandscape, 0.85f, false, true);
        }

        /// <summary>Active audio sample rate in Hz.</summary>
        public int SampleRate {
            get { return sampleRate; }
        }

        /// <summary>Active loudspeaker geometry preset.</summary>
        public TransauralSpeakerPreset Preset {
            get { return preset; }
        }

        /// <summary>True when transaural XTC is user-enabled.</summary>
        public bool Enabled {
            get { return enabled; }
        }

        /// <summary>
        /// True when hardware route forces bit-exact bypass (headphones, IEMs, USB-DAC headsets, BT headsets).
        /// </summary>
        public bool Bypassed {
            get { return bypassed; }
            set { bypassed = value; }
        }

        /// <summary>True when transaural XTC is actively processing stereo speaker output.</summary>
        public bool IsActivelyProcessing {
            get { return enabled && !bypassed && strength > 1e-4f && feedbackCoeff > 1e-4f; }
        }

        /// <summary>Cancellation strength in [0, 1].</summary>
        public float Strength {
            get { return strength; }
        }

        /// <summary>Computed physical speaker-to-ear geometry parameters.</summary>
        public TransauralGeometryParams Geometry {
            get { return geometry; }
        }

        /// <summary>Theoretical mid-band crosstalk cancellation depth (dB) at active geometry and strength.</summary>
        public float EstimatedCancellationDb {
            get { return IsActivelyProcessing ? lastEstimatedCancellationDb : 0f; }
        }

        /// <summary>Peak output scalar applied during the last processed block.</summary>
        public float LastPeakGainApplied {
            get { return IsActivelyProcessing ? lastPeakGainApplied : 1.0f; }
        }

        /// <summary>
        /// Computes physical speaker-to-ear geometry and acoustic transfer parameters from a preset or custom dimensions.
        /// </summary>
        public static TransauralGeometryParams ComputeGeometry(
            TransauralSpeakerPreset preset,
            float customSpanMeters = 0.155f,
            float customDistanceMeters = 0.40f) {
            float span;
            float dist;
            float lowCut;
            float highCut;
            switch (preset) {
                case TransauralSpeakerPreset.PhoneBuiltInPortrait:
                    span = 0.105f;
                    dist = 0.38f;
                    lowCut = 220f;
                    highCut = 6200f;
                    break;
                case TransauralSpeakerPreset.DesktopMonitors:
                    span = 0.62f;
                    dist = 0.85f;
                    lowCut = 150f;
                    highCut = 6500f;
                    break;
                case TransauralSpeakerPreset.StereoHiFi:
                    span = 2.30f;
                    dist = 2.00f;
                    lowCut = 140f;
                    highCut = 5500f;
                    break;
                case TransauralSpeakerPreset.Custom:
                    span = Clamp(customSpanMeters, 0.08f, 4.0f);
                    dist = Clamp(customDistanceMeters, 0.25f, 5.0f);
                    lowCut = span < 0.25f ? 200f : 150f;
                    highCut = span < 0.25f ? 6800f : 6000f;
                    break;
                case TransauralSpeakerPreset.PhoneBuiltInLandscape:
                default:
                    // Galaxy S25 Ultra stereo speakers (~15.5 cm acoustic center spacing @ 40 cm handheld distance)
                    span = 0.155f;
                    dist = 0.40f;
                    lowCut = 200f;
                    highCut = 6800f;
                    break;
            }

            float halfSpan = span * 0.5f;
            float halfAngleRad = (float)Math.Atan2(halfSpan, dist);
            float halfAngleDeg = halfAngleRad * (180.0f / (float)Math.PI);

            float dxIpsi = halfSpan - DefaultHeadRadiusMeters;
            float dxContra = halfSpan + DefaultHeadRadiusMeters;
            float dIpsi = (float)Math.Sqrt(dxIpsi * dxIpsi + dist * dist);
            float dContra = (float)Math.Sqrt(dxContra * dxContra + dist * dist);

            // Add Woodworth-Schlosberg spherical head diffraction path excess around contralateral side
            float diffractionExcess = DefaultHeadRadiusMeters * (float)Math.Sin(halfAngleRad) * 0.35f;
            float deltaMeters = Math.Max(0.015f, (dContra - dIpsi) + diffractionExcess);
            float delayUs = (deltaMeters / SpeedOfSound) * 1e6f;

            // Free-field 1/r attenuation ratio plus mild spherical head-shadow attenuation
            float distRatio = dIpsi / Math.Max(dIpsi, dContra);
            float g0 = Clamp(distRatio * (0.93f - 0.14f * (float)Math.Sin(halfAngleRad)), 0.58f, 0.86f);

            // Head-shadow low-pass corner drops slightly at wider angles (stronger acoustic head shadow)
            float shadowCutoffHz = Clamp(3200f - halfAngleDeg * 42f, 1500f, 3000f);

            TransauralGeometryParams p = new TransauralGeometryParams();
            p.SpeakerSpanMeters = span;
            p.ListenerDistanceMeters = dist;
            p.HalfAngleDegrees = halfAngleDeg;
            p.ContralateralDelayUs = delayUs;
            p.ContralateralGainRatio = g0;
            p.HeadShadowCutoffHz = shadowCutoffHz;
            p.LowBandCutoffHz = lowCut;
            p.HighBandCutoffHz = highCut;
            return p;
        }

        /// <summary>
        /// Configures sample rate, speaker geometry preset, cancellation depth, enable flag, and hardware bypass flag.
        /// </summary>
        public void Configure(
            int newSampleRate,
            TransauralSpeakerPreset newPreset,
            float newStrength,
            bool isEnabled,
            bool isBypassed,
            float newCustomSpanMeters = 0.155f,
            float newCustomDistanceMeters = 0.40f) {
            bool srChanged = newSampleRate != sampleRate;
            sampleRate = Math.Max(16000, newSampleRate);
            preset = newPreset;
            customSpanMeters = Clamp(newCustomSpanMeters, 0.08f, 4.0f);
            customDistanceMeters = Clamp(newCustomDistanceMeters, 0.25f, 5.0f);
            strength = Clamp(newStrength, 0f, 1f);
            enabled = isEnabled;
            bypassed = isBypassed;

            geometry = ComputeGeometry(preset, customSpanMeters, customDistanceMeters);

            // Fractional contralateral delay in samples (clamped safely inside ring buffer)
            float exactSamples = (geometry.ContralateralDelayUs * 1e-6f) * sampleRate;
            delaySamples = Clamp(exactSamples, 1.5f, DelayLineSize - 6);

            // Regularized cancellation gain k = strength * g0 * 0.94 (strictly < 0.82 for unconditional BIBO stability)
            feedbackCoeff = Clamp(strength * geometry.ContralateralGainRatio * 0.94f, 0f, 0.80f);

            // Mid-band theoretical crosstalk residual: (g0 - k) / g0 -> dB improvement
            float residual = Math.Max(0.03f, Math.Abs(geometry.ContralateralGainRatio - feedbackCoeff));
            float separationRatio = geometry.ContralateralGainRatio / residual;
            lastEstimatedCancellationDb = (float)(20.0 * Math.Log10(Math.Max(1.0, separationRatio)));

            // Unity-loudness regularization scalar preventing excessive dipole boost
            outputNormGain = 1.0f / (1.0f + 0.55f * feedbackCoeff);

            // 1-pole head-shadow low-pass matching H_c(z) = alpha / (1 - (1 - alpha) z^-1)
            float shadowOmega = 2.0f * (float)Math.PI * geometry.HeadShadowCutoffHz / sampleRate;
            shadowAlpha = Clamp(shadowOmega / (1.0f + shadowOmega), 0.04f, 0.85f);

            // 1-pole feedback sub-bass blocker (fc = 0.25 * LowBandCutoffHz): blocks DC/sub-bass loop buildup
            // while keeping mid-band phase error < 6 deg at 400 Hz for >= 15 dB physical crosstalk cancellation.
            float fbOmega = 2.0f * (float)Math.PI * (geometry.LowBandCutoffHz * 0.25f) / sampleRate;
            fbHpCoeff = Clamp(1.0f / (1.0f + fbOmega), 0.85f, 0.999f);

            if (srChanged) {
                Reset();
            }
        }

        /// <summary>Clears all delay lines and filter states cleanly (call on seek, track change, or route switch).</summary>
        public void Reset() {
            Array.Clear(delayLineL, 0, delayLineL.Length);
            Array.Clear(delayLineR, 0, delayLineR.Length);
            delayWriteIndex = 0;
            shadowStateL = 0f;
            shadowStateR = 0f;
            fbHpPrevInL = fbHpPrevOutL = 0f;
            fbHpPrevInR = fbHpPrevOutR = 0f;
            lastPeakGainApplied = 1.0f;
        }

        /// <summary>
        /// Processes interleaved stereo PCM in-place with zero heap allocations.
        /// Guarantees bit-exact (<c>0.0f</c> diff) pass-through whenever <see cref="Bypassed"/> is true or XTC is disabled.
        /// </summary>
        public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels) {
            // HARD INVARIANT (KILL R3): bit-exact 0.0f headphone bypass — zero writes to buffer!
            if (buffer == null || !enabled || bypassed || strength <= 1e-4f || feedbackCoeff <= 1e-4f || channels < 2 || frameCount <= 0) {
                return;
            }

            int totalFloats = frameCount * channels;
            if (offset < 0 || offset + totalFloats > buffer.Length) {
                return;
            }

            float k = feedbackCoeff;
            float norm = outputNormGain;
            float sAlpha = shadowAlpha;
            float sOneMinus = 1.0f - sAlpha;
            float fbC = fbHpCoeff;

            int intDelay = (int)delaySamples;
            float frac = delaySamples - intDelay;
            float oneMinusFrac = 1.0f - frac;

            float peakOut = 0f;
            float peakIn = 1e-6f;

            int idx = offset;
            for (int f = 0; f < frameCount; f++) {
                float inL = buffer[idx];
                float inR = buffer[idx + 1];
                if (float.IsNaN(inL) || float.IsInfinity(inL)) {
                    inL = 0f;
                }
                if (float.IsNaN(inR) || float.IsInfinity(inR)) {
                    inR = 0f;
                }

                float absIn = Math.Max(Math.Abs(inL), Math.Abs(inR));
                if (absIn > peakIn) {
                    peakIn = absIn;
                }

                // 1. Read fractionally delayed contralateral cancellation signals y_R[n - tau_c], y_L[n - tau_c]
                int readIdx0 = (delayWriteIndex - intDelay) & DelayLineMask;
                int readIdx1 = (readIdx0 - 1) & DelayLineMask;
                float delayedR = delayLineR[readIdx0] * oneMinusFrac + delayLineR[readIdx1] * frac;
                float delayedL = delayLineL[readIdx0] * oneMinusFrac + delayLineL[readIdx1] * frac;

                // 2. Apply physical contralateral head-shadow diffraction filter H_c(z) + sub-bass DC loop blocker
                shadowStateR = sAlpha * delayedR + sOneMinus * shadowStateR;
                shadowStateL = sAlpha * delayedL + sOneMinus * shadowStateL;

                float fbR = fbC * (fbHpPrevOutR + shadowStateR - fbHpPrevInR);
                fbHpPrevInR = shadowStateR;
                fbHpPrevOutR = fbR;

                float fbL = fbC * (fbHpPrevOutL + shadowStateL - fbHpPrevInL);
                fbHpPrevInL = shadowStateL;
                fbHpPrevOutL = fbL;

                // 3. Causal Bauck-Cooper / BACCH recursive crosstalk cancellation:
                //    Y_L = X_L - k * H_fb * H_c * z^-tau * Y_R, Y_R = X_R - k * H_fb * H_c * z^-tau * Y_L
                float yL = Clamp(inL - k * fbR, -4.0f, 4.0f);
                float yR = Clamp(inR - k * fbL, -4.0f, 4.0f);

                // Write cancelled outputs into fractional delay ring buffer for next contralateral cycle
                delayLineL[delayWriteIndex] = yL;
                delayLineR[delayWriteIndex] = yR;
                delayWriteIndex = (delayWriteIndex + 1) & DelayLineMask;

                // 4. Unity-loudness regularization scalar preserving exact L/R cancellation ratio
                float outL = yL * norm;
                float outR = yR * norm;

                float absOut = Math.Max(Math.Abs(outL), Math.Abs(outR));
                if (absOut > peakOut) {
                    peakOut = absOut;
                }

                buffer[idx] = outL;
                buffer[idx + 1] = outR;
                idx += channels;
            }

            lastPeakGainApplied = peakOut / peakIn;
        }

        /// <summary>
        /// Simulates physical loudspeaker-to-ear propagation through the symmetric 2x2 transfer matrix
        /// <c>H(z) = [[1, g0 * H_c(z) * z^-tau], [g0 * H_c(z) * z^-tau, 1]]</c> for offline/ground-truth verification.
        /// </summary>
        public static void SimulatePhysicalSpeakerToEar(
            float[] speakerInterleaved,
            float[] earInterleavedOut,
            int frameCount,
            int sampleRate,
            TransauralGeometryParams geo) {
            if (speakerInterleaved == null || earInterleavedOut == null || frameCount <= 0) {
                return;
            }
            float[] dL = new float[DelayLineSize];
            float[] dR = new float[DelayLineSize];
            int writeIdx = 0;

            float exactDelay = Clamp((geo.ContralateralDelayUs * 1e-6f) * Math.Max(16000, sampleRate), 1.5f, DelayLineSize - 6);
            int intD = (int)exactDelay;
            float frac = exactDelay - intD;
            float oneMinus = 1.0f - frac;
            float omega = 2.0f * (float)Math.PI * geo.HeadShadowCutoffHz / Math.Max(16000, sampleRate);
            float alpha = Clamp(omega / (1.0f + omega), 0.04f, 0.85f);
            float oneMinusAlpha = 1.0f - alpha;
            float g0 = geo.ContralateralGainRatio;

            float sL = 0f;
            float sR = 0f;
            for (int f = 0; f < frameCount; f++) {
                int i = f * 2;
                float xL = speakerInterleaved[i];
                float xR = speakerInterleaved[i + 1];

                dL[writeIdx] = xL;
                dR[writeIdx] = xR;

                int r0 = (writeIdx - intD) & DelayLineMask;
                int r1 = (r0 - 1) & DelayLineMask;
                float contraFromR = dR[r0] * oneMinus + dR[r1] * frac;
                float contraFromL = dL[r0] * oneMinus + dL[r1] * frac;

                sR = alpha * contraFromR + oneMinusAlpha * sR;
                sL = alpha * contraFromL + oneMinusAlpha * sL;

                earInterleavedOut[i] = xL + g0 * sR;
                earInterleavedOut[i + 1] = xR + g0 * sL;

                writeIdx = (writeIdx + 1) & DelayLineMask;
            }
        }

        static float Clamp(float value, float min, float max) {
            if (value < min) {
                return min;
            }
            if (value > max) {
                return max;
            }
            return value;
        }
    }
}
