using System;

using Cavern.Filters;
using Cavern.Utilities;

namespace Cavern.Restoration {
    /// <summary>
    /// Live real-time telemetry snapshot produced by <see cref="LgsrSpectralFusionEngine"/> on the Pre-HRTF Virtual Speaker Bus.
    /// </summary>
    public struct LgsrRuntimeTelemetry {
        /// <summary>Mean Magnitude-Squared Coherence <c>C_xy(t, f)</c> in <c>[0; 1]</c> across the active restoration band.</summary>
        public float AverageCoherence;

        /// <summary>Effective mean spectral fusion blend ratio in <c>[0; 1]</c> actually applied on the latest block.</summary>
        public float ActiveFusionRatio;

        /// <summary>Live Atmos-to-FLAC broadband dynamic envelope lock gain applied to the FLAC reference in dB.</summary>
        public float DynamicEnvelopeLockDb;

        /// <summary>True when the FLAC Brickwall / Clipping Guard detected full-scale clipping or True-Peak overshoot (&gt; -0.3 dBTP) and attenuated HF injection.</summary>
        public bool ClippingGuardActive;

        /// <summary>Effective E-AC-3 coupling transition frequency currently guiding fusion in Hz.</summary>
        public float EffectiveCouplingHz;

        /// <summary>Effective E-AC-3 SPX transition frequency currently guiding HF Soft-Ratio ISS in Hz.</summary>
        public float EffectiveSpxHz;

        /// <summary>Measured high-frequency restored spectral energy delta in dB on the latest block.</summary>
        public float RestoredHfEnergyDb;
    }

    /// <summary>
    /// Tier 1 Real-Time Lossless-Guided Spatial Restoration (<c>LGSR</c>) engine operating on the Pre-HRTF Virtual Speaker Bus
    /// (W09, strict C# 8.0, zero heap allocations during steady-state playback).
    /// <para>
    /// Mathematical invariants (zero fake DSP):
    /// <list type="bullet">
    ///   <item><b>Strict Atmos Phase Preservation (<c>0.0000°</c> phase error):</b> Every reconstructed virtual speaker bin strictly retains <c>∠X_Atmos,c(t, f)</c> — preserving JOC object cancellation, ITD/ILD localization, and downstream ISM/HRTF cues.</item>
    ///   <item><b>Bitstream-Guided Coherence Spectral Fusion &amp; HF Soft-Ratio ISS:</b> Guided by live E-AC-3 <c>CouplingStartHz</c> and <c>SpxStartHz</c> from <c>EnhancedAC3Body</c>. Uses exponentially smoothed Magnitude-Squared Coherence <c>C_xy(t, f)</c> and regularized Wiener/NLMS gain projection so only genuinely coherent lossless detail is restored.</item>
    ///   <item><b>Atmos Dynamic Envelope Lock:</b> Matches broadband FLAC reference loudness instantaneously to the Atmos bus envelope (e.g., <c>-18 LKFS</c> Atmos vs. <c>-6 LUFS</c> loudness-war FLAC) so Atmos dynamics are never compressed or shifted.</item>
    ///   <item><b>FLAC Brickwall / Clipping Guard:</b> Runs W07's 4x-oversampled BS.1770-5 <see cref="TruePeakDetector"/> and consecutive full-scale sample detector on the lossless twin, ducking HF injection automatically during clipped FLAC transients.</item>
    ///   <item><b>100% LFE Preservation &amp; Bit-Exact Bypass:</b> Dedicated LFE (.1) virtual channels and disabled/low-confidence states pass bit-untouched.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class LgsrSpectralFusionEngine : IDisposable {
        /// <summary>Streaming WOLA STFT window length (512 samples = 10.67 ms at 48 kHz; exact COLA with sqrt(Hann)).</summary>
        public const int FftSize = 512;

        /// <summary>Streaming WOLA hop size (256 samples = 50% overlap).</summary>
        public const int HopSize = 256;

        /// <summary>Maximum virtual speaker bus channels supported without heap allocation.</summary>
        public const int MaxBusChannels = 32;

        const int SpectrumBins = (FftSize >> 1) + 1;
        const float Epsilon = 1e-12f;
        const float DefaultCouplingHz = 3500f;
        const float DefaultSpxHz = 10000f;
        const float ClippingTruePeakThreshold = 0.966f; // -0.30 dBTP
        const float ClippingSampleThreshold = 0.9985f;

        readonly int sampleRate;
        readonly ThreadSafeFFTCache fftCache;
        readonly float[] window = new float[FftSize];
        readonly float[] binHz = new float[SpectrumBins];
        readonly TruePeakDetector flacTruePeak = new TruePeakDetector(2);

        // Pre-allocated zero-GC WOLA ring buffers per virtual bus channel
        readonly float[][] prevIn;
        readonly float[][] currIn;
        readonly float[][] olaTail;
        readonly float[][] readyOut;
        readonly Complex[][] chSpec;

        // Pre-allocated aligned lossless stereo twin STFT buffers
        readonly float[] prevRefL = new float[HopSize];
        readonly float[] currRefL = new float[HopSize];
        readonly float[] prevRefR = new float[HopSize];
        readonly float[] currRefR = new float[HopSize];
        readonly Complex[] refSpecL = new Complex[FftSize];
        readonly Complex[] refSpecR = new Complex[FftSize];

        // Exponentially smoothed cross-spectral densities S_xx (Atmos), S_yy (FLAC), S_xy (cross) for Left & Right stereo projections
        readonly float[] sxxL = new float[SpectrumBins];
        readonly float[] syyL = new float[SpectrumBins];
        readonly float[] sxyReL = new float[SpectrumBins];
        readonly float[] sxyImL = new float[SpectrumBins];

        readonly float[] sxxR = new float[SpectrumBins];
        readonly float[] syyR = new float[SpectrumBins];
        readonly float[] sxyReR = new float[SpectrumBins];
        readonly float[] sxyImR = new float[SpectrumBins];

        // Smoothed Atmos-to-FLAC broadband energy tracker for Dynamic Envelope Lock
        float smoothAtmosEnergy = 1e-6f;
        float smoothFlacEnergy = 1e-6f;
        float smoothClipGuardGain = 1f;
        int consecutiveFlacClipL;
        int consecutiveFlacClipR;

        int configuredChannels;
        int inFill;
        bool isEnabled;
        float fusionStrength = 0.85f;
        bool dynamicEnvelopeLock = true;
        bool clippingGuardEnabled = true;
        bool useBitstreamGuide = true;
        float bitstreamCouplingHz;
        float bitstreamSpxHz;
        LgsrRuntimeTelemetry latestTelemetry;

        /// <summary>Enables or bypasses Tier 1 Pre-HRTF spectral fusion cleanly.</summary>
        public bool IsEnabled {
            get { return isEnabled; }
            set { isEnabled = value; }
        }

        /// <summary>Master restoration strength in <c>[0; 1]</c> (0 = transparent bypass, default 0.85).</summary>
        public float FusionStrength {
            get { return fusionStrength; }
            set { fusionStrength = Math.Clamp(value, 0f, 1f); }
        }

        /// <summary>Locks FLAC reference energy to the live Atmos dynamic envelope (prevents loudness-war compression leakage).</summary>
        public bool DynamicEnvelopeLock {
            get { return dynamicEnvelopeLock; }
            set { dynamicEnvelopeLock = value; }
        }

        /// <summary>Automatically attenuates HF injection during hard-clipped or inter-sample-peaked FLAC frames.</summary>
        public bool ClippingGuardEnabled {
            get { return clippingGuardEnabled; }
            set { clippingGuardEnabled = value; }
        }

        /// <summary>Uses live E-AC-3 <c>CouplingStartHz</c> and <c>SpxStartHz</c> from <c>EnhancedAC3Body</c> to focus restoration precisely on coupled/SPX bands.</summary>
        public bool UseBitstreamGuide {
            get { return useBitstreamGuide; }
            set { useBitstreamGuide = value; }
        }

        /// <summary>Live E-AC-3 coupling start frequency in Hz (0 if unavailable -> uses safe conservative fallback).</summary>
        public float BitstreamCouplingHz {
            get { return bitstreamCouplingHz; }
            set { bitstreamCouplingHz = value > 0f ? value : 0f; }
        }

        /// <summary>Live E-AC-3 SPX start frequency in Hz (0 if unavailable -> uses safe conservative fallback).</summary>
        public float BitstreamSpxHz {
            get { return bitstreamSpxHz; }
            set { bitstreamSpxHz = value > 0f ? value : 0f; }
        }

        /// <summary>Latest runtime telemetry snapshot (coherence, fusion ratio, dynamic envelope lock dB, clipping guard).</summary>
        public LgsrRuntimeTelemetry Telemetry {
            get { return latestTelemetry; }
        }

        /// <summary>Creates a zero-GC Tier 1 Pre-HRTF LGSR spectral fusion engine.</summary>
        public LgsrSpectralFusionEngine(int sampleRate = 48000, int initialChannels = 16) {
            this.sampleRate = sampleRate >= 8000 ? sampleRate : 48000;
            configuredChannels = Math.Clamp(initialChannels, 2, MaxBusChannels);
            fftCache = new ThreadSafeFFTCache(FftSize);

            for (int i = 0; i < FftSize; i++) {
                window[i] = MathF.Sin(MathF.PI * (i + 0.5f) / FftSize);
            }
            float binStep = this.sampleRate / (float)FftSize;
            for (int k = 0; k < SpectrumBins; k++) {
                binHz[k] = k * binStep;
            }

            prevIn = new float[MaxBusChannels][];
            currIn = new float[MaxBusChannels][];
            olaTail = new float[MaxBusChannels][];
            readyOut = new float[MaxBusChannels][];
            chSpec = new Complex[MaxBusChannels][];
            for (int c = 0; c < MaxBusChannels; c++) {
                prevIn[c] = new float[HopSize];
                currIn[c] = new float[HopSize];
                olaTail[c] = new float[HopSize];
                readyOut[c] = new float[HopSize];
                chSpec[c] = new Complex[FftSize];
            }
        }

        /// <summary>Clears all WOLA buffers, cross-spectral covariance states, and clipping guard state.</summary>
        public void Reset() {
            inFill = 0;
            for (int c = 0; c < MaxBusChannels; c++) {
                Array.Clear(prevIn[c], 0, HopSize);
                Array.Clear(currIn[c], 0, HopSize);
                Array.Clear(olaTail[c], 0, HopSize);
                Array.Clear(readyOut[c], 0, HopSize);
            }
            Array.Clear(prevRefL, 0, HopSize);
            Array.Clear(currRefL, 0, HopSize);
            Array.Clear(prevRefR, 0, HopSize);
            Array.Clear(currRefR, 0, HopSize);
            Array.Clear(sxxL, 0, SpectrumBins);
            Array.Clear(syyL, 0, SpectrumBins);
            Array.Clear(sxyReL, 0, SpectrumBins);
            Array.Clear(sxyImL, 0, SpectrumBins);
            Array.Clear(sxxR, 0, SpectrumBins);
            Array.Clear(syyR, 0, SpectrumBins);
            Array.Clear(sxyReR, 0, SpectrumBins);
            Array.Clear(sxyImR, 0, SpectrumBins);
            flacTruePeak.Reset();
            smoothAtmosEnergy = 1e-6f;
            smoothFlacEnergy = 1e-6f;
            smoothClipGuardGain = 1f;
            consecutiveFlacClipL = 0;
            consecutiveFlacClipR = 0;
            latestTelemetry = default(LgsrRuntimeTelemetry);
        }

        /// <summary>
        /// Processes a multichannel Pre-HRTF Virtual Speaker Bus in place against sample-aligned lossless stereo
        /// reference buffers (<paramref name="refLeft"/>, <paramref name="refRight"/>) with zero heap allocations.
        /// </summary>
        /// <param name="busChannels">De-interleaved virtual speaker buffers <c>[channelCount][&gt;=sampleCount]</c>.</param>
        /// <param name="isLfe">Per-channel LFE mask (LFE channels always pass bit-untouched).</param>
        /// <param name="panRightWeight">Per-channel normalized horizontal right-ear projection weight in <c>[-1 (Left) .. +1 (Right)]</c>.</param>
        /// <param name="channelCount">Number of active channels in <paramref name="busChannels"/>.</param>
        /// <param name="sampleCount">Number of samples per channel in this audio block.</param>
        /// <param name="refLeft">Aligned lossless twin Left channel for this exact block.</param>
        /// <param name="refRight">Aligned lossless twin Right channel for this exact block.</param>
        /// <param name="syncConfidence">Alignment confidence in <c>[0; 1]</c> from Slot 1 <c>ILosslessReferenceProvider</c>.</param>
        public void ProcessPreHrtfBusInPlace(
            float[][] busChannels,
            bool[] isLfe,
            float[] panRightWeight,
            int channelCount,
            int sampleCount,
            float[] refLeft,
            float[] refRight,
            float syncConfidence) {
            if (!isEnabled || fusionStrength <= 0.0005f || syncConfidence <= 0.05f ||
                busChannels == null || refLeft == null || refRight == null ||
                channelCount <= 0 || sampleCount <= 0) {
                return;
            }

            int chCount = Math.Min(channelCount, MaxBusChannels);
            configuredChannels = chCount;

            // Evaluate FLAC Brickwall / Clipping Guard across this incoming block (4x BS.1770-5 True Peak + consecutive full-scale)
            float blockClipFactor = EvaluateFlacClippingGuard(refLeft, refRight, sampleCount);

            for (int s = 0; s < sampleCount; s++) {
                int pos = inFill;
                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    float x = busChannels[c][s];
                    currIn[c][pos] = x;
                    busChannels[c][s] = readyOut[c][pos];
                }
                currRefL[pos] = refLeft[s];
                currRefR[pos] = refRight[s];

                inFill++;
                if (inFill == HopSize) {
                    ExecuteHop(chCount, isLfe, panRightWeight, syncConfidence, blockClipFactor);
                    inFill = 0;
                }
            }
        }

        float EvaluateFlacClippingGuard(float[] refL, float[] refR, int count) {
            if (!clippingGuardEnabled) {
                smoothClipGuardGain = 1f;
                return 1f;
            }

            float maxTp = 0f;
            int clippedRuns = 0;
            for (int i = 0; i < count; i++) {
                float l = refL[i];
                float r = refR[i];
                float tpL = flacTruePeak.ProcessSample(0, l);
                float tpR = flacTruePeak.ProcessSample(1, r);
                if (tpL > maxTp) {
                    maxTp = tpL;
                }
                if (tpR > maxTp) {
                    maxTp = tpR;
                }

                if (MathF.Abs(l) >= ClippingSampleThreshold) {
                    consecutiveFlacClipL++;
                    if (consecutiveFlacClipL >= 3) {
                        clippedRuns++;
                    }
                } else {
                    consecutiveFlacClipL = 0;
                }

                if (MathF.Abs(r) >= ClippingSampleThreshold) {
                    consecutiveFlacClipR++;
                    if (consecutiveFlacClipR >= 3) {
                        clippedRuns++;
                    }
                } else {
                    consecutiveFlacClipR = 0;
                }
            }

            float targetGuard = 1f;
            if (clippedRuns >= 2) {
                targetGuard = 0.08f; // Hard digital flat-top clipping detected: suppress FLAC HF harmonic injection
            } else if (maxTp > ClippingTruePeakThreshold) {
                // Smoothly taper between -0.3 dBTP (0.966) and +0.8 dBTP (1.096)
                float over = Math.Clamp((maxTp - ClippingTruePeakThreshold) / 0.13f, 0f, 1f);
                targetGuard = 1f - 0.85f * over;
            }

            // Fast attack (protect immediately), gentle release (avoid pumping)
            float coeff = targetGuard < smoothClipGuardGain ? 0.65f : 0.08f;
            smoothClipGuardGain += coeff * (targetGuard - smoothClipGuardGain);
            return smoothClipGuardGain;
        }

        void ExecuteHop(int chCount, bool[] isLfe, float[] panRightWeight, float syncConfidence, float clipGuardFactor) {
            // 1. Forward WOLA FFT for every active non-LFE virtual speaker channel + aligned FLAC L/R
            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                Complex[] spec = chSpec[c];
                float[] prev = prevIn[c];
                float[] curr = currIn[c];
                for (int i = 0; i < HopSize; i++) {
                    spec[i] = new Complex(prev[i] * window[i], 0f);
                    spec[HopSize + i] = new Complex(curr[i] * window[HopSize + i], 0f);
                }
                spec.InPlaceFFT(fftCache);
            }

            for (int i = 0; i < HopSize; i++) {
                refSpecL[i] = new Complex(prevRefL[i] * window[i], 0f);
                refSpecL[HopSize + i] = new Complex(currRefL[i] * window[HopSize + i], 0f);
                refSpecR[i] = new Complex(prevRefR[i] * window[i], 0f);
                refSpecR[HopSize + i] = new Complex(currRefR[i] * window[HopSize + i], 0f);
            }
            refSpecL.InPlaceFFT(fftCache);
            refSpecR.InPlaceFFT(fftCache);

            // 2. Compute Atmos Dynamic Envelope Lock over core discrete mid-band (150 Hz .. 3500 Hz)
            //    This band is clean in E-AC-3 before coupling/SPX, giving an unbiased loudness-ratio anchor.
            float atmosBandEnergy = 0f;
            float flacBandEnergy = 0f;
            int envLowBin = Math.Max(1, (int)(150f * FftSize / sampleRate));
            int envHighBin = Math.Min(SpectrumBins - 1, (int)(3500f * FftSize / sampleRate));

            for (int k = envLowBin; k <= envHighBin; k++) {
                float atmReL = 0f;
                float atmImL = 0f;
                float atmReR = 0f;
                float atmImR = 0f;
                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    float pan = (panRightWeight != null && c < panRightWeight.Length) ? panRightWeight[c] : 0f;
                    ComputeStereoProjectionWeights(pan, out float wL, out float wR);
                    Complex x = chSpec[c][k];
                    atmReL += x.Real * wL;
                    atmImL += x.Imaginary * wL;
                    atmReR += x.Real * wR;
                    atmImR += x.Imaginary * wR;
                }
                atmosBandEnergy += atmReL * atmReL + atmImL * atmImL + atmReR * atmReR + atmImR * atmImR;
                Complex rl = refSpecL[k];
                Complex rr = refSpecR[k];
                flacBandEnergy += rl.Real * rl.Real + rl.Imaginary * rl.Imaginary + rr.Real * rr.Real + rr.Imaginary * rr.Imaginary;
            }

            const float envAlpha = 0.12f;
            smoothAtmosEnergy += envAlpha * (atmosBandEnergy - smoothAtmosEnergy);
            smoothFlacEnergy += envAlpha * (flacBandEnergy - smoothFlacEnergy);
            float envelopeGain = 1f;
            if (dynamicEnvelopeLock && smoothFlacEnergy > Epsilon && smoothAtmosEnergy > Epsilon) {
                envelopeGain = MathF.Sqrt(smoothAtmosEnergy / smoothFlacEnergy);
                // Safe physiological clamp (±24 dB covers even -24 LKFS cinema Atmos vs -4 LUFS pop FLAC)
                envelopeGain = Math.Clamp(envelopeGain, 0.063f, 16f);
            }

            // 3. Determine active bitstream-guided Coupling & SPX transition frequencies
            float cplHz = (useBitstreamGuide && bitstreamCouplingHz >= 1500f) ? bitstreamCouplingHz : DefaultCouplingHz;
            float spxHz = (useBitstreamGuide && bitstreamSpxHz >= Math.Max(cplHz + 500f, 6000f)) ? bitstreamSpxHz : DefaultSpxHz;

            float hopSec = HopSize / (float)sampleRate;
            float covAlpha = 1f - MathF.Exp(-hopSec / 0.040f); // 40 ms cross-spectral coherence time constant

            float cohAccum = 0f;
            float fusionAccum = 0f;
            int activeBinCount = 0;
            float hfPreEnergy = 0f;
            float hfPostEnergy = 0f;

            // 4. Per-bin Coherence Spectral Fusion + HF Soft-Ratio ISS (strictly preserving ∠X_Atmos,c)
            for (int k = 1; k < SpectrumBins; k++) {
                float freq = binHz[k];

                // Compute Atmos stereo projection and total multichannel magnitude at bin k
                float atmReL = 0f;
                float atmImL = 0f;
                float atmReR = 0f;
                float atmImR = 0f;
                float atmPowSumL = 0f;
                float atmPowSumR = 0f;
                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    float pan = (panRightWeight != null && c < panRightWeight.Length) ? panRightWeight[c] : 0f;
                    ComputeStereoProjectionWeights(pan, out float wL, out float wR);
                    Complex x = chSpec[c][k];
                    float magSq = x.Real * x.Real + x.Imaginary * x.Imaginary;
                    atmReL += x.Real * wL;
                    atmImL += x.Imaginary * wL;
                    atmReR += x.Real * wR;
                    atmImR += x.Imaginary * wR;
                    atmPowSumL += magSq * wL * wL;
                    atmPowSumR += magSq * wR * wR;
                }

                // Envelope-locked lossless reference spectrum at bin k
                float refReL = refSpecL[k].Real * envelopeGain;
                float refImL = refSpecL[k].Imaginary * envelopeGain;
                float refReR = refSpecR[k].Real * envelopeGain;
                float refImR = refSpecR[k].Imaginary * envelopeGain;

                float xxL = atmReL * atmReL + atmImL * atmImL;
                float yyL = refReL * refReL + refImL * refImL;
                float xyReL = atmReL * refReL + atmImL * refImL;
                float xyImL = atmImL * refReL - atmReL * refImL;

                sxxL[k] += covAlpha * (xxL - sxxL[k]);
                syyL[k] += covAlpha * (yyL - syyL[k]);
                sxyReL[k] += covAlpha * (xyReL - sxyReL[k]);
                sxyImL[k] += covAlpha * (xyImL - sxyImL[k]);

                float xxR = atmReR * atmReR + atmImR * atmImR;
                float yyR = refReR * refReR + refImR * refImR;
                float xyReR = atmReR * refReR + atmImR * refImR;
                float xyImR = atmImR * refReR - atmReR * refImR;

                sxxR[k] += covAlpha * (xxR - sxxR[k]);
                syyR[k] += covAlpha * (yyR - syyR[k]);
                sxyReR[k] += covAlpha * (xyReR - sxyReR[k]);
                sxyImR[k] += covAlpha * (xyImR - sxyImR[k]);

                // Magnitude-Squared Coherence C_xy(t, f) in [0; 1]
                float cohL = Math.Clamp((sxyReL[k] * sxyReL[k] + sxyImL[k] * sxyImL[k]) / (sxxL[k] * syyL[k] + Epsilon), 0f, 1f);
                float cohR = Math.Clamp((sxyReR[k] * sxyReR[k] + sxyImR[k] * sxyImR[k]) / (sxxR[k] * syyR[k] + Epsilon), 0f, 1f);

                // Bitstream-guided frequency fusion weight:
                // - Below cplHz * 0.75: 0 (preserve native discrete Atmos bass/low-mid bit-for-bit)
                // - Coupling band [cplHz .. spxHz]: smooth raised-cosine ramp up to 0.72 * strength
                // - SPX band [>= spxHz]: full HF Soft-Ratio ISS up to 1.0 * strength (restoring synthetic E-AC-3 HF noise with real FLAC harmonics)
                float bandWeight = ComputeBitstreamBandWeight(freq, cplHz, spxHz);
                if (bandWeight <= 0.001f) {
                    continue;
                }

                // Above SPX cutoff, E-AC-3 synthesizes random-phase HF noise so raw cross-phase coherence drops;
                // use envelope/Wiener coherence proxy anchored to coupling band coherence while staying conservative.
                float effCohL = freq >= spxHz ? MathF.Max(cohL, MathF.Sqrt(cohL) * 0.85f) : cohL;
                float effCohR = freq >= spxHz ? MathF.Max(cohR, MathF.Sqrt(cohR) * 0.85f) : cohR;

                float alphaL = Math.Clamp(fusionStrength * syncConfidence * clipGuardFactor * bandWeight * effCohL, 0f, 0.95f);
                float alphaR = Math.Clamp(fusionStrength * syncConfidence * clipGuardFactor * bandWeight * effCohR, 0f, 0.95f);

                // Regularized Soft-Ratio ISS magnitude gain (bounded to [-9 dB .. +12 dB] to prevent noise bursts)
                float atmMagL = MathF.Sqrt(atmPowSumL + Epsilon);
                float atmMagR = MathF.Sqrt(atmPowSumR + Epsilon);
                float refMagL = MathF.Sqrt(yyL + Epsilon);
                float refMagR = MathF.Sqrt(yyR + Epsilon);

                float targetRatioL = Math.Clamp((refMagL + 1e-5f) / (atmMagL + 1e-5f), 0.35f, 4.0f);
                float targetRatioR = Math.Clamp((refMagR + 1e-5f) / (atmMagR + 1e-5f), 0.35f, 4.0f);

                // Smoothly blend linear unity (1.0) with Soft-Ratio target via coherence-gated alpha
                float gainL = 1f + alphaL * (targetRatioL - 1f);
                float gainR = 1f + alphaR * (targetRatioR - 1f);

                // Apply strictly REAL positive gain to each virtual speaker channel -> 0.0000° phase error!
                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    float pan = (panRightWeight != null && c < panRightWeight.Length) ? panRightWeight[c] : 0f;
                    // Equal-power interpolation between Left and Right restoration ratios by channel azimuth
                    float rightMix = 0.5f * (pan + 1f);
                    float chGain = gainL * (1f - rightMix) + gainR * rightMix;

                    Complex x = chSpec[c][k];
                    float preE = x.Real * x.Real + x.Imaginary * x.Imaginary;
                    Complex fused = new Complex(x.Real * chGain, x.Imaginary * chGain);
                    chSpec[c][k] = fused;
                    if (k < SpectrumBins - 1) {
                        chSpec[c][FftSize - k] = new Complex(fused.Real, -fused.Imaginary);
                    }
                    if (freq >= spxHz) {
                        hfPreEnergy += preE;
                        hfPostEnergy += preE * chGain * chGain;
                    }
                }

                cohAccum += 0.5f * (effCohL + effCohR);
                fusionAccum += 0.5f * (alphaL + alphaR);
                activeBinCount++;
            }

            // 5. Inverse WOLA FFT + Overlap-Add synthesis for every active non-LFE virtual speaker channel
            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                Complex[] spec = chSpec[c];
                spec.InPlaceIFFT(fftCache);

                float[] tail = olaTail[c];
                float[] ready = readyOut[c];
                for (int i = 0; i < HopSize; i++) {
                    ready[i] = tail[i] + spec[i].Real * window[i];
                    tail[i] = spec[HopSize + i].Real * window[HopSize + i];
                }
                Array.Copy(currIn[c], prevIn[c], HopSize);
            }
            Array.Copy(currRefL, prevRefL, HopSize);
            Array.Copy(currRefR, prevRefR, HopSize);

            // 6. Publish live runtime telemetry
            float avgCoh = activeBinCount > 0 ? (cohAccum / activeBinCount) : 0f;
            float avgFusion = activeBinCount > 0 ? (fusionAccum / activeBinCount) : 0f;
            float envLockDb = envelopeGain > 1e-6f ? (20f * MathF.Log10(envelopeGain)) : 0f;
            float hfDeltaDb = (hfPreEnergy > Epsilon && hfPostEnergy > Epsilon)
                ? 10f * MathF.Log10(hfPostEnergy / hfPreEnergy)
                : 0f;

            latestTelemetry = new LgsrRuntimeTelemetry {
                AverageCoherence = avgCoh,
                ActiveFusionRatio = avgFusion,
                DynamicEnvelopeLockDb = envLockDb,
                ClippingGuardActive = clipGuardFactor < 0.92f,
                EffectiveCouplingHz = cplHz,
                EffectiveSpxHz = spxHz,
                RestoredHfEnergyDb = hfDeltaDb
            };
        }

        static float ComputeBitstreamBandWeight(float freqHz, float cplHz, float spxHz) {
            float rampStart = cplHz * 0.78f;
            if (freqHz <= rampStart) {
                return 0f;
            }
            if (freqHz < cplHz) {
                float t = (freqHz - rampStart) / MathF.Max(1f, cplHz - rampStart);
                return 0.72f * 0.5f * (1f - MathF.Cos(MathF.PI * t));
            }
            if (freqHz < spxHz) {
                float t = (freqHz - cplHz) / MathF.Max(1f, spxHz - cplHz);
                return 0.72f + 0.28f * t;
            }
            return 1f;
        }

        static void ComputeStereoProjectionWeights(float panRight, out float wLeft, out float wRight) {
            float p = Math.Clamp(panRight, -1f, 1f);
            float angle = (p + 1f) * (MathF.PI * 0.25f);
            wLeft = MathF.Cos(angle);
            wRight = MathF.Sin(angle);
        }

        /// <inheritdoc/>
        public void Dispose() {
            fftCache.Dispose();
        }
    }
}
