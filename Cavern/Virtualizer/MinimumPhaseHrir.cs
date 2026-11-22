using System;

using Cavern.Utilities;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Converts laboratory-measured anechoic HRIR pairs into mathematically exact Minimum-Phase + measured ITD
    /// impulse responses via real-cepstrum Hilbert decomposition (zero magnitude alteration, zero artificial EQ).
    /// </summary>
    public static class MinimumPhaseHrir {
        /// <summary>
        /// Maximum impulse length eligible for minimum-phase conversion (longer impulses are BRIRs with room tails
        /// whose non-minimum-phase reflections must remain intact).
        /// </summary>
        public const int MaxAnechoicTaps = 1024;

        /// <summary>
        /// Converts a measured left/right HRIR pair into Minimum-Phase + measured ITD, preserving exact frequency
        /// magnitude and interaural time difference without modifying timbre.
        /// </summary>
        /// <param name="leftIR">Original measured left-ear HRIR</param>
        /// <param name="rightIR">Original measured right-ear HRIR</param>
        /// <param name="minLeft">Output minimum-phase left-ear HRIR (with relative ITD applied if right leads)</param>
        /// <param name="minRight">Output minimum-phase right-ear HRIR (with relative ITD applied if left leads)</param>
        public static void ConvertPair(float[] leftIR, float[] rightIR, out float[] minLeft, out float[] minRight) {
            ConvertPair(leftIR, rightIR, out minLeft, out minRight, 48000);
        }

        /// <summary>
        /// Converts a measured left/right HRIR pair into Minimum-Phase + measured ITD at <paramref name="sampleRate"/> Hz,
        /// preserving exact frequency magnitude and interaural time difference without modifying timbre.
        /// </summary>
        /// <param name="leftIR">Original measured left-ear HRIR</param>
        /// <param name="rightIR">Original measured right-ear HRIR</param>
        /// <param name="minLeft">Output minimum-phase left-ear HRIR (with relative ITD applied if right leads)</param>
        /// <param name="minRight">Output minimum-phase right-ear HRIR (with relative ITD applied if left leads)</param>
        /// <param name="sampleRate">Impulse sample rate in Hz (used to bound ITD lag search to +/- 1.2 ms)</param>
        public static void ConvertPair(float[] leftIR, float[] rightIR, out float[] minLeft, out float[] minRight, int sampleRate) {
            if (leftIR == null || rightIR == null) {
                minLeft = leftIR != null ? (float[])leftIR.Clone() : Array.Empty<float>();
                minRight = rightIR != null ? (float[])rightIR.Clone() : Array.Empty<float>();
                return;
            }
            int len = Math.Max(leftIR.Length, rightIR.Length);
            if (len <= 0 || len > MaxAnechoicTaps) {
                minLeft = (float[])leftIR.Clone();
                minRight = (float[])rightIR.Clone();
                return;
            }

            // Minimum-phase decomposition on padded FFT size (4x zero-padding prevents time-aliasing in cepstrum)
            int fftSize = 1 << QMath.Log2Ceil(Math.Max(256, len * 4));
            float[] rawMinL, rawMinR;
            int itdSamples;
            using (FFTCache cache = new ThreadSafeFFTCache(fftSize)) {
                // Measure true physical ITD via Duplex-Theory low-pass (<=1500 Hz) waveform cross-correlation
                // (positive => right ear arrives later than left ear; immune to MagLS HF phase relaxation & pinna peaks).
                // Subtract the intrinsic minimum-phase group-delay difference already embedded in the contralateral
                // head-shadow magnitude rolloff of (rawMinL, rawMinR) so total ITD is preserved without double-counting.
                int totalItd = EstimateInterauralDelaySamples(leftIR, rightIR, sampleRate, fftSize, cache);
                rawMinL = ComputeMinimumPhase(leftIR, len, fftSize, cache);
                rawMinR = ComputeMinimumPhase(rightIR, len, fftSize, cache);
                int minPhaseIntrinsicItd = EstimateInterauralDelaySamples(rawMinL, rawMinR, sampleRate, fftSize, cache);
                itdSamples = totalItd - minPhaseIntrinsicItd;
            }

            int delayL = itdSamples < 0 ? -itdSamples : 0;
            int delayR = itdSamples > 0 ? itdSamples : 0;
            int outLen = len + Math.Max(delayL, delayR);

            minLeft = new float[outLen];
            minRight = new float[outLen];
            Array.Copy(rawMinL, 0, minLeft, delayL, rawMinL.Length);
            Array.Copy(rawMinR, 0, minRight, delayR, rawMinR.Length);
        }

        /// <summary>
        /// Computes the minimum-phase equivalent of <paramref name="impulse"/> via real cepstrum folding.
        /// </summary>
        static float[] ComputeMinimumPhase(float[] impulse, int outLength, int fftSize, FFTCache cache) {
            Complex[] spec = new Complex[fftSize];
            for (int i = 0; i < impulse.Length; i++) {
                float sample = impulse[i];
                spec[i] = new Complex(!float.IsNaN(sample) && !float.IsInfinity(sample) ? sample : 0f, 0f);
            }
            spec.InPlaceFFT(cache);

            // Real log-magnitude spectrum (epsilon floor -120 dB prevents log(0) singularities)
            const float magFloor = 1e-6f;
            float logFloor = MathF.Log(magFloor);
            for (int k = 0; k < fftSize; k++) {
                float re = spec[k].Real;
                float im = spec[k].Imaginary;
                float mag = MathF.Sqrt(re * re + im * im);
                float safeLog = !float.IsNaN(mag) && !float.IsInfinity(mag) ? MathF.Log(Math.Max(mag, magFloor)) : logFloor;
                spec[k] = new Complex(safeLog, 0f);
            }
            spec.InPlaceIFFT(cache);

            // Causal cepstrum window: keep n=0 and Nyquist n=N/2, double 1..N/2-1, zero anticausal tail
            int half = fftSize >> 1;
            spec[0] = new Complex(!float.IsNaN(spec[0].Real) && !float.IsInfinity(spec[0].Real) ? spec[0].Real : 0f, 0f);
            for (int n = 1; n < half; n++) {
                float re = spec[n].Real;
                spec[n] = new Complex(!float.IsNaN(re) && !float.IsInfinity(re) ? re * 2f : 0f, 0f);
            }
            spec[half] = new Complex(!float.IsNaN(spec[half].Real) && !float.IsInfinity(spec[half].Real) ? spec[half].Real : 0f, 0f);
            for (int n = half + 1; n < fftSize; n++) {
                spec[n] = new Complex(0f, 0f);
            }

            spec.InPlaceFFT(cache);
            for (int k = 0; k < fftSize; k++) {
                float re = spec[k].Real;
                float im = spec[k].Imaginary;
                float expRe = !float.IsNaN(re) && !float.IsInfinity(re) ? MathF.Exp(re) : 0f;
                if (!float.IsNaN(expRe) && !float.IsInfinity(expRe) && !float.IsNaN(im) && !float.IsInfinity(im)) {
                    spec[k] = new Complex(expRe * MathF.Cos(im), expRe * MathF.Sin(im));
                } else {
                    spec[k] = new Complex(0f, 0f);
                }
            }
            spec.InPlaceIFFT(cache);

            float[] result = new float[outLength];
            for (int i = 0; i < outLength; i++) {
                float val = spec[i].Real;
                result[i] = !float.IsNaN(val) && !float.IsInfinity(val) ? val : 0f;
            }
            return result;
        }

        /// <summary>
        /// Estimates integer interaural time difference (ITD = onsetRight - onsetLeft) via Duplex-Theory
        /// low-pass filtered (&lt;= 1500 Hz, smooth Hann rolloff 1200–1800 Hz) zero-phase waveform cross-correlation,
        /// bounded to physically plausible head lags (+/- 1.2 ms).
        /// Low-pass fine-structure correlation preserves exact physical ITD even when high-frequency phase has been
        /// relaxed by spherical-harmonic MagLS decoding or modulated by pinna reflections (Katz &amp; Noisternig 2014).
        /// </summary>
        static int EstimateInterauralDelaySamples(float[] leftIR, float[] rightIR, int sampleRate, int fftSize, FFTCache cache) {
            int len = Math.Min(leftIR.Length, rightIR.Length);
            if (len < 8) {
                return 0;
            }
            if (sampleRate <= 0) {
                sampleRate = 48000;
            }

            Complex[] specL = new Complex[fftSize];
            Complex[] specR = new Complex[fftSize];
            double energyL = 0.0, energyR = 0.0;
            for (int i = 0; i < leftIR.Length; i++) {
                float v = leftIR[i];
                if (!float.IsNaN(v) && !float.IsInfinity(v)) {
                    specL[i] = new Complex(v, 0f);
                    energyL += (double)v * v;
                }
            }
            for (int i = 0; i < rightIR.Length; i++) {
                float v = rightIR[i];
                if (!float.IsNaN(v) && !float.IsInfinity(v)) {
                    specR[i] = new Complex(v, 0f);
                    energyR += (double)v * v;
                }
            }
            if (energyL < 1e-12 || energyR < 1e-12) {
                return 0;
            }

            specL.InPlaceFFT(cache);
            specR.InPlaceFFT(cache);

            // Compute low-pass weighted cross-power spectrum: X[k] = W(f_k) * conj(L[k]) * R[k]
            // Smooth Hann rolloff 1200..1800 Hz isolates Duplex-Theory ITD without Gibbs ringing.
            const double passHz = 1200.0;
            const double stopHz = 1800.0;
            double binHz = (double)sampleRate / fftSize;
            int half = fftSize >> 1;

            Complex[] xcorr = new Complex[fftSize];
            double lowEnergy = 0.0;
            double totalEnergy = 0.0;
            for (int k = 1; k < half; k++) {
                double f = k * binHz;
                float lr = specL[k].Real, li = specL[k].Imaginary;
                float rr = specR[k].Real, ri = specR[k].Imaginary;
                double binE = (double)lr * lr + (double)li * li + (double)rr * rr + (double)ri * ri;
                totalEnergy += binE;

                if (f >= stopHz) {
                    continue;
                }
                float w = f <= passHz
                    ? 1f
                    : (float)(0.5 * (1.0 + Math.Cos(Math.PI * (f - passHz) / (stopHz - passHz))));
                lowEnergy += binE * w;

                // conj(L) * R = (lr - i*li)(rr + i*ri) = (lr*rr + li*ri) + i*(lr*ri - li*rr)
                float re = (lr * rr + li * ri) * w;
                float im = (lr * ri - li * rr) * w;
                xcorr[k] = new Complex(re, im);
                xcorr[fftSize - k] = new Complex(re, -im);
            }

            // Fallback to broadband cross-correlation only for synthetic high-pass/ultrasonic impulses with no LF energy
            if (lowEnergy < totalEnergy * 1e-6) {
                for (int k = 0; k < fftSize; k++) {
                    float lr = specL[k].Real, li = specL[k].Imaginary;
                    float rr = specR[k].Real, ri = specR[k].Imaginary;
                    xcorr[k] = new Complex(lr * rr + li * ri, lr * ri - li * rr);
                }
            }

            xcorr.InPlaceIFFT(cache);

            // Bounded lag search (+/- 1.2 ms at sampleRate, well above max human head ITD ~0.75 ms)
            int maxLag = Math.Max(8, (int)Math.Ceiling(0.0012 * sampleRate));
            maxLag = Math.Min(maxLag, len / 2);
            int bestLag = 0;
            double bestCorr = double.NegativeInfinity;
            for (int lag = -maxLag; lag <= maxLag; lag++) {
                int idx = lag >= 0 ? lag : fftSize + lag;
                double corr = xcorr[idx].Real;
                if (!double.IsNaN(corr) && !double.IsInfinity(corr) && corr > bestCorr) {
                    bestCorr = corr;
                    bestLag = lag;
                }
            }
            return bestLag;
        }
    }
}
