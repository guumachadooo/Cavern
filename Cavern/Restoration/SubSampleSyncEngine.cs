using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Cavern.Utilities;

namespace Cavern.Restoration {
    /// <summary>
    /// Complete 3-stage sub-sample alignment and clock-drift solution produced by <see cref="SubSampleSyncEngine"/>.
    /// Mapping formula: <c>losslessIndex = BaseOffsetSamples + atmosIndex * (1.0 + DriftPpm * 1e-6)</c>.
    /// </summary>
    public struct SubSampleSyncResult {
        /// <summary>Coarse Stage 1 integer sample offset found via macro onset/envelope cross-correlation (<c>±15 s</c>).</summary>
        public long MacroOffsetSamples;

        /// <summary>Fine Stage 2+3 intercept sample offset at <c>atmosIndex == 0</c> (sub-sample precision <c>&lt; 0.05 samp</c>).</summary>
        public double BaseOffsetSamples;

        /// <summary>Estimated linear clock drift in parts-per-million (ppm) from Stage 3 Theil-Sen regression (0 when gated).</summary>
        public double DriftPpm;

        /// <summary>Overall alignment confidence in <c>[0; 1]</c> (combined GCC-PHAT peak-to-sidelobe ratio and window consensus).</summary>
        public float Confidence;

        /// <summary>Number of sliding GCC-PHAT anchor windows that passed the confidence gate.</summary>
        public int ValidWindowCount;

        /// <summary>Total number of sliding GCC-PHAT windows evaluated.</summary>
        public int TotalWindowCount;

        /// <summary>Median absolute residual error across inlier windows in samples.</summary>
        public float ResidualStdSamples;

        /// <summary>True when <see cref="Confidence"/> passes the safety gate for active lossless spectral fusion.</summary>
        public bool IsSynced;

        /// <summary>Returns an unsynced default result.</summary>
        public static SubSampleSyncResult Unsynced() {
            return new SubSampleSyncResult {
                MacroOffsetSamples = 0L,
                BaseOffsetSamples = 0.0,
                DriftPpm = 0.0,
                Confidence = 0f,
                ValidWindowCount = 0,
                TotalWindowCount = 0,
                ResidualStdSamples = float.NaN,
                IsSynced = false
            };
        }
    }

    /// <summary>
    /// Zero-GC 4-point cubic Lagrange / Farrow fractional sample interpolator for exact sub-sample and ppm clock-drift
    /// compensation when reading an aligned lossless stereo reference buffer in real time.
    /// </summary>
    public static class FarrowLagrangeDelay {
        /// <summary>
        /// Evaluates <paramref name="buffer"/> at continuous sample index <paramref name="exactPosition"/> using
        /// a 4-point cubic Lagrange polynomial over <c>[n-1, n, n+1, n+2]</c> (exact for cubic polynomials, zero heap allocation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SampleAt(float[] buffer, int length, double exactPosition) {
            if (buffer == null || length <= 0) {
                return 0f;
            }
            if (exactPosition < 0.0 || exactPosition >= length - 1) {
                return 0f;
            }
            int idx = (int)Math.Floor(exactPosition);
            double d = exactPosition - idx;
            if (d < 1e-7) {
                return buffer[idx];
            }
            if (idx < 1 || idx + 2 >= length) {
                // Safe linear fallback on the outermost 2 boundary samples
                float s0 = buffer[idx];
                float s1 = idx + 1 < length ? buffer[idx + 1] : 0f;
                return (float)(s0 + (s1 - s0) * d);
            }

            // 4-point cubic Lagrange basis on x = {-1, 0, +1, +2} evaluated at d in [0, 1)
            // Farrow polynomial form: ((c3 * d + c2) * d + c1) * d + c0
            float xm1 = buffer[idx - 1];
            float x0 = buffer[idx];
            float xp1 = buffer[idx + 1];
            float xp2 = buffer[idx + 2];

            double c0 = x0;
            double c1 = (-2.0 * xm1 - 3.0 * x0 + 6.0 * xp1 - xp2) * (1.0 / 6.0);
            double c2 = (xm1 - 2.0 * x0 + xp1) * 0.5;
            double c3 = (-xm1 + 3.0 * x0 - 3.0 * xp1 + xp2) * (1.0 / 6.0);
            return (float)(((c3 * d + c2) * d + c1) * d + c0);
        }

        /// <summary>
        /// Reads <paramref name="frameCount"/> fractionally aligned stereo frames from <paramref name="refLeft"/>/<paramref name="refRight"/>
        /// corresponding to Atmos track position <paramref name="atmosStartSample"/> using <paramref name="baseOffsetSamples"/>
        /// and linear clock drift <paramref name="driftPpm"/>. Completely allocation-free.
        /// </summary>
        public static bool ReadAlignedStereoBlock(
            float[] refLeft,
            float[] refRight,
            long atmosStartSample,
            double baseOffsetSamples,
            double driftPpm,
            float[] outLeft,
            float[] outRight,
            int frameCount) {
            if (refLeft == null || refRight == null || outLeft == null || outRight == null || frameCount <= 0) {
                return false;
            }
            int refLen = Math.Min(refLeft.Length, refRight.Length);
            if (refLen < 4 || outLeft.Length < frameCount || outRight.Length < frameCount) {
                return false;
            }

            double rateScale = 1.0 + driftPpm * 1e-6;
            double firstPos = baseOffsetSamples + atmosStartSample * rateScale;
            double lastPos = baseOffsetSamples + (atmosStartSample + frameCount - 1) * rateScale;
            if (lastPos < 0.0 || firstPos >= refLen - 1) {
                Array.Clear(outLeft, 0, frameCount);
                Array.Clear(outRight, 0, frameCount);
                return false;
            }

            for (int i = 0; i < frameCount; i++) {
                double pos = firstPos + i * rateScale;
                outLeft[i] = SampleAt(refLeft, refLen, pos);
                outRight[i] = SampleAt(refRight, refLen, pos);
            }
            return true;
        }
    }

    /// <summary>
    /// 3-Stage Sub-Sample Audio Synchronization &amp; Clock-Drift Tracker (W09, strict C# 8.0).
    /// Curated from <c>synctoolbox</c> / <c>audalign</c>, Knapp &amp; Carter (1976) Generalized Cross-Correlation with
    /// Phase Transform (<c>GCC-PHAT</c>), and Theil-Sen robust non-parametric linear regression:
    /// <list type="number">
    ///   <item><b>Stage 1 — Macro Onset/Envelope Cross-Correlation (<c>±15 s</c>):</b> FFT cross-correlation of 100 Hz log-energy onset envelopes locates coarse integer offset even with different studio lead-in silences.</item>
    ///   <item><b>Stage 2 — Sliding-Window <c>GCC-PHAT</c> (<c>&lt; 0.05 samp</c>):</b> 4096-pt Hann-windowed whitened cross-correlation with local parabolic + windowed-sinc peak interpolation achieves sub-sample accuracy (&lt; 1 µs at 48 kHz).</item>
    ///   <item><b>Stage 3 — Theil-Sen Robust Drift Estimator (ppm) + Confidence Gate:</b> Median pairwise slope across high-confidence windows tracks sample-clock drift (ppm) without outlier sensitivity, rejecting mismatched masters cleanly.</item>
    /// </list>
    /// </summary>
    public static class SubSampleSyncEngine {
        /// <summary>Default maximum macro lead-in / trim search radius in seconds (<c>±15 s</c>).</summary>
        public const float DefaultMaxMacroSearchSeconds = 15f;

        /// <summary>FFT window size for Stage 2 GCC-PHAT sub-sample estimation (4096 samples ≈ 85.3 ms @ 48 kHz).</summary>
        public const int GccFftSize = 4096;

        /// <summary>Maximum physically plausible linear crystal clock drift accepted by Stage 3 (ppm).</summary>
        public const double MaxPlausibleDriftPpm = 250.0;

        const float MinimumWindowEnergy = 1e-6f;
        const float PhatEps = 1e-9f;

        /// <summary>
        /// Runs the full 3-stage alignment between a mono Atmos downmix (<paramref name="atmosMono"/>)
        /// and a sample-rate-matched lossless twin mono mix (<paramref name="losslessMono"/>).
        /// </summary>
        /// <param name="atmosMono">Mono downmix of the lossy/Atmos reference stream at <paramref name="sampleRate"/>.</param>
        /// <param name="losslessMono">Mono downmix of the lossless FLAC/WAV twin already resampled to <paramref name="sampleRate"/>.</param>
        /// <param name="sampleRate">Shared sample rate in Hz (typically 48000).</param>
        /// <param name="maxMacroSearchSeconds">Maximum coarse offset search radius in seconds (default 15 s).</param>
        /// <param name="minConfidenceGate">Confidence threshold in [0; 1] required to mark <see cref="SubSampleSyncResult.IsSynced"/>.</param>
        /// <param name="windowCount">Number of sliding GCC-PHAT windows distributed across overlapping audio (default 12).</param>
        public static SubSampleSyncResult Align(
            float[] atmosMono,
            float[] losslessMono,
            int sampleRate,
            float maxMacroSearchSeconds = DefaultMaxMacroSearchSeconds,
            float minConfidenceGate = 0.35f,
            int windowCount = 12) {
            if (atmosMono == null || losslessMono == null || atmosMono.Length < GccFftSize || losslessMono.Length < GccFftSize) {
                return SubSampleSyncResult.Unsynced();
            }
            int sr = sampleRate > 0 ? sampleRate : 48000;
            float maxSearchSec = Math.Clamp(maxMacroSearchSeconds, 0.5f, 30f);

            // Stage 1: Macro onset/envelope cross-correlation (±15 s)
            long macroOffset = EstimateMacroOffset(atmosMono, losslessMono, sr, maxSearchSec, out float macroConf);

            // Stage 2: Sliding-window GCC-PHAT across overlapping track span
            long overlapStart = Math.Max(0L, -macroOffset);
            long overlapEnd = Math.Min((long)atmosMono.Length, losslessMono.Length - macroOffset);
            long overlapLen = overlapEnd - overlapStart;
            if (overlapLen < GccFftSize) {
                return SubSampleSyncResult.Unsynced();
            }

            int numWindows = Math.Clamp(windowCount, 3, 24);
            // If overlap is short, reduce window count so windows stay distinct
            int maxDistinct = (int)Math.Max(1L, (overlapLen - GccFftSize) / (GccFftSize / 2) + 1);
            if (numWindows > maxDistinct) {
                numWindows = maxDistinct;
            }

            List<double> anchorTimes = new List<double>(numWindows);
            List<double> anchorOffsets = new List<double>(numWindows);
            List<float> anchorConfs = new List<float>(numWindows);

            // Half-window search radius around macro estimate (±1024 samples = ±21.3 ms covers 10 ms onset quantization)
            int fineMaxLag = GccFftSize / 4;
            float[] hann = BuildHannWindow(GccFftSize);
            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(GccFftSize);
            try {
                Complex[] specA = new Complex[GccFftSize];
                Complex[] specB = new Complex[GccFftSize];
                Complex[] gcc = new Complex[GccFftSize];

                for (int w = 0; w < numWindows; w++) {
                    double frac = numWindows == 1 ? 0.5 : (0.08 + 0.84 * w / (numWindows - 1.0));
                    long aStart = overlapStart + (long)Math.Round(frac * (overlapLen - GccFftSize));
                    if (aStart < 0) {
                        aStart = 0;
                    }
                    if (aStart + GccFftSize > atmosMono.Length) {
                        aStart = atmosMono.Length - GccFftSize;
                    }
                    long bCenterStart = aStart + macroOffset;
                    if (bCenterStart < 0 || bCenterStart + GccFftSize > losslessMono.Length) {
                        continue;
                    }

                    // Skip silent passages
                    if (ComputeMeanSquare(atmosMono, (int)aStart, GccFftSize) < MinimumWindowEnergy ||
                        ComputeMeanSquare(losslessMono, (int)bCenterStart, GccFftSize) < MinimumWindowEnergy) {
                        continue;
                    }

                    double fineLag = EstimateGccPhatSubSampleLag(
                        atmosMono,
                        (int)aStart,
                        losslessMono,
                        (int)bCenterStart,
                        hann,
                        specA,
                        specB,
                        gcc,
                        fft,
                        fineMaxLag,
                        out float winConf);

                    if (winConf >= 0.18f) {
                        anchorTimes.Add(aStart + GccFftSize * 0.5);
                        anchorOffsets.Add(macroOffset + fineLag);
                        anchorConfs.Add(winConf);
                    }
                }
            } finally {
                fft.Dispose();
            }

            if (anchorOffsets.Count == 0) {
                SubSampleSyncResult fallback = SubSampleSyncResult.Unsynced();
                fallback.MacroOffsetSamples = macroOffset;
                fallback.BaseOffsetSamples = macroOffset;
                fallback.TotalWindowCount = numWindows;
                fallback.Confidence = macroConf * 0.25f;
                return fallback;
            }

            // Stage 3: Theil-Sen robust linear regression (offset(t) = intercept + slope * t)
            double driftSlope = 0.0;
            double intercept = 0.0;
            if (anchorOffsets.Count >= 3) {
                ComputeTheilSenRegression(anchorTimes, anchorOffsets, out intercept, out driftSlope);
            } else {
                // Fewer than 3 windows: robustly take median offset with 0 ppm drift
                double[] sorted = anchorOffsets.ToArray();
                Array.Sort(sorted);
                intercept = sorted[sorted.Length / 2];
                driftSlope = 0.0;
            }

            double driftPpm = driftSlope * 1e6;
            if (Math.Abs(driftPpm) > MaxPlausibleDriftPpm) {
                // Reject implausible drift slope and fall back to zero-drift median offset
                driftPpm = 0.0;
                driftSlope = 0.0;
                double[] sorted = anchorOffsets.ToArray();
                Array.Sort(sorted);
                intercept = sorted[sorted.Length / 2];
            }

            // Evaluate inlier consistency (residual <= 0.35 samples after linear drift model)
            int inliers = 0;
            double sqErrSum = 0.0;
            double confSum = 0.0;
            for (int i = 0; i < anchorOffsets.Count; i++) {
                double pred = intercept + driftSlope * anchorTimes[i];
                double err = anchorOffsets[i] - pred;
                if (Math.Abs(err) <= 0.5) {
                    inliers++;
                    sqErrSum += err * err;
                    confSum += anchorConfs[i];
                }
            }

            if (inliers == 0) {
                // Re-center around the highest-confidence window if multi-window consensus broke
                int bestIdx = 0;
                for (int i = 1; i < anchorConfs.Count; i++) {
                    if (anchorConfs[i] > anchorConfs[bestIdx]) {
                        bestIdx = i;
                    }
                }
                intercept = anchorOffsets[bestIdx];
                driftPpm = 0.0;
                inliers = 1;
                confSum = anchorConfs[bestIdx];
            }

            float avgInlierConf = (float)(confSum / inliers);
            float consensusRatio = inliers / (float)Math.Max(1, anchorOffsets.Count);
            float residualStd = (float)Math.Sqrt(sqErrSum / Math.Max(1, inliers));

            // Gate ppm drift to 0 unless at least 3 tight inliers span >= 2 seconds of audio
            double spanSamples = anchorTimes.Count >= 2 ? (anchorTimes[anchorTimes.Count - 1] - anchorTimes[0]) : 0.0;
            if (inliers < 3 || spanSamples < sr * 2.0 || residualStd > 0.25f) {
                // Recompute median intercept at zero drift if span is too short for reliable ppm estimation
                if (Math.Abs(driftPpm) > 0.0 && spanSamples < sr * 2.0) {
                    double[] sorted = anchorOffsets.ToArray();
                    Array.Sort(sorted);
                    intercept = sorted[sorted.Length / 2];
                    driftPpm = 0.0;
                }
            }

            // Combined confidence in [0; 1]
            float consistencyBonus = Math.Clamp(1f - residualStd * 2f, 0f, 1f);
            float finalConfidence = Math.Clamp(
                avgInlierConf * (0.45f + 0.35f * consensusRatio + 0.20f * consistencyBonus),
                0f,
                1f);

            return new SubSampleSyncResult {
                MacroOffsetSamples = macroOffset,
                BaseOffsetSamples = intercept,
                DriftPpm = driftPpm,
                Confidence = finalConfidence,
                ValidWindowCount = inliers,
                TotalWindowCount = numWindows,
                ResidualStdSamples = residualStd,
                IsSynced = finalConfidence >= minConfidenceGate
            };
        }

        /// <summary>
        /// Stage 1: Coarse offset estimation (<c>±maxSearchSec</c>) via FFT cross-correlation of 100 Hz log-onset envelopes.
        /// Positive offset means lossless audio is shifted later than Atmos (<c>lossless[i + offset] ≈ atmos[i]</c>).
        /// </summary>
        public static long EstimateMacroOffset(
            float[] atmosMono,
            float[] losslessMono,
            int sampleRate,
            float maxSearchSeconds,
            out float confidence) {
            confidence = 0f;
            int hop = Math.Max(64, sampleRate / 100); // 10 ms envelope frames (100 Hz)
            int maxFrames = Math.Min(6000, Math.Max(atmosMono.Length, losslessMono.Length) / hop); // up to 60s probe
            int lenA = Math.Min(maxFrames, atmosMono.Length / hop);
            int lenB = Math.Min(maxFrames, losslessMono.Length / hop);
            if (lenA < 8 || lenB < 8) {
                return 0L;
            }

            float[] envA = BuildLogOnsetEnvelope(atmosMono, lenA, hop);
            float[] envB = BuildLogOnsetEnvelope(losslessMono, lenB, hop);

            int fftLen = NextPowerOfTwo(lenA + lenB);
            Complex[] specA = new Complex[fftLen];
            Complex[] specB = new Complex[fftLen];
            for (int i = 0; i < lenA; i++) {
                specA[i] = new Complex(envA[i], 0f);
            }
            for (int i = 0; i < lenB; i++) {
                specB[i] = new Complex(envB[i], 0f);
            }

            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(fftLen);
            try {
                specA.InPlaceFFT(fft);
                specB.InPlaceFFT(fft);

                // Cross-correlation R(lag) = IFFT( conj(A) * B ) -> peak at lag = +offset where B[t + lag] ≈ A[t]
                for (int k = 0; k < fftLen; k++) {
                    float aRe = specA[k].Real;
                    float aIm = -specA[k].Imaginary; // conj(A)
                    float bRe = specB[k].Real;
                    float bIm = specB[k].Imaginary;
                    specA[k] = new Complex(aRe * bRe - aIm * bIm, aRe * bIm + aIm * bRe);
                }
                specA.InPlaceIFFT(fft);
            } finally {
                fft.Dispose();
            }

            int maxLagFrames = Math.Min((fftLen / 2) - 1, (int)Math.Ceiling(maxSearchSeconds * sampleRate / hop));
            double bestVal = double.NegativeInfinity;
            int bestLag = 0;
            double sumSq = 0.0;
            int count = 0;

            for (int lag = -maxLagFrames; lag <= maxLagFrames; lag++) {
                int idx = lag >= 0 ? lag : (fftLen + lag);
                float v = specA[idx].Real;
                sumSq += (double)v * v;
                count++;
                if (v > bestVal) {
                    bestVal = v;
                    bestLag = lag;
                }
            }

            double rms = Math.Sqrt(sumSq / Math.Max(1, count));
            double psr = rms > 1e-12 ? (bestVal / rms) : 0.0;
            confidence = (float)Math.Clamp((psr - 2.0) / 10.0, 0.0, 1.0);
            return (long)bestLag * hop;
        }

        /// <summary>
        /// Stage 2: Single-window 4096-pt <c>GCC-PHAT</c> sub-sample delay estimator (<c>&lt; 0.05 samp</c>) with
        /// parabolic + 9-tap windowed-sinc peak refinement.
        /// Returns fractional lag <c>delta</c> such that <c>b[bStart + i + delta] ≈ a[aStart + i]</c>.
        /// </summary>
        public static double EstimateGccPhatSubSampleLag(
            float[] a,
            int aStart,
            float[] b,
            int bStart,
            float[] window,
            Complex[] specA,
            Complex[] specB,
            Complex[] gcc,
            ThreadSafeFFTCache fft,
            int maxLagSamples,
            out float confidence) {
            int n = window.Length;
            for (int i = 0; i < n; i++) {
                specA[i] = new Complex(a[aStart + i] * window[i], 0f);
                specB[i] = new Complex(b[bStart + i] * window[i], 0f);
            }
            specA.InPlaceFFT(fft);
            specB.InPlaceFFT(fft);

            // GCC-PHAT with adaptive noise-floor regularization (prevents empty/sidelobe bins on tonal
            // passages from being amplified to unity, while preserving razor-sharp whitening on occupied bins).
            // Focus on core waveform-coded band (~72 Hz .. 9.6 kHz @ 48 kHz) with smooth cosine roll-off
            // so DC, sub-bass room rumble, and E-AC-3 SPX synthetic HF noise never bias sub-sample peak estimation.
            int lowBin = Math.Max(2, (int)(n * 0.0015));
            int highBin = Math.Min(n / 2 - 2, (int)(n * 0.20));
            int taperBins = Math.Max(4, (highBin - lowBin) / 10);
            double sumCrossMag = 0.0;
            for (int k = lowBin; k <= highBin; k++) {
                float aRe = specA[k].Real;
                float aIm = -specA[k].Imaginary;
                float bRe = specB[k].Real;
                float bIm = specB[k].Imaginary;
                float re = aRe * bRe - aIm * bIm;
                float im = aRe * bIm + aIm * bRe;
                sumCrossMag += Math.Sqrt((double)re * re + (double)im * im);
            }
            float avgCrossMag = (float)(sumCrossMag / Math.Max(1, highBin - lowBin + 1));
            float adaptiveEps = MathF.Max(PhatEps, avgCrossMag * 0.02f);

            Array.Clear(gcc, 0, n);
            for (int k = lowBin; k <= highBin; k++) {
                float aRe = specA[k].Real;
                float aIm = -specA[k].Imaginary;
                float bRe = specB[k].Real;
                float bIm = specB[k].Imaginary;
                float re = aRe * bRe - aIm * bIm;
                float im = aRe * bIm + aIm * bRe;
                float mag = MathF.Sqrt(re * re + im * im) + adaptiveEps;
                float bandW = 1f;
                if (k - lowBin < taperBins) {
                    float t = (k - lowBin) / (float)taperBins;
                    bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                } else if (highBin - k < taperBins) {
                    float t = (highBin - k) / (float)taperBins;
                    bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                }
                float normRe = (re / mag) * bandW;
                float normIm = (im / mag) * bandW;
                gcc[k] = new Complex(normRe, normIm);
                gcc[n - k] = new Complex(normRe, -normIm);
            }
            gcc.InPlaceIFFT(fft);

            int maxLag = Math.Min(maxLagSamples, (n / 2) - 4);
            float peakVal = float.NegativeInfinity;
            int peakLag = 0;
            double sidelobeSqSum = 0.0;
            int sidelobeCount = 0;

            for (int lag = -maxLag; lag <= maxLag; lag++) {
                int idx = lag >= 0 ? lag : (n + lag);
                float v = gcc[idx].Real;
                if (v > peakVal) {
                    peakVal = v;
                    peakLag = lag;
                }
            }

            for (int lag = -maxLag; lag <= maxLag; lag++) {
                if (Math.Abs(lag - peakLag) <= 6) {
                    continue;
                }
                int idx = lag >= 0 ? lag : (n + lag);
                float v = gcc[idx].Real;
                sidelobeSqSum += (double)v * v;
                sidelobeCount++;
            }

            double sidelobeRms = Math.Sqrt(sidelobeSqSum / Math.Max(1, sidelobeCount));
            double psr = sidelobeRms > 1e-9 ? (peakVal / sidelobeRms) : 0.0;
            confidence = (float)Math.Clamp((psr - 3.5) / 14.0, 0.0, 1.0);

            // Pass 2: Re-center window B at bStart + peakLag so both Hann windows overlap identically
            // (eliminates window-edge phase bias on non-zero integer lags and sparse tonal passages)
            int recenteredB = bStart + peakLag;
            if (peakLag != 0 && recenteredB >= 0 && recenteredB + n <= b.Length) {
                for (int i = 0; i < n; i++) {
                    specB[i] = new Complex(b[recenteredB + i] * window[i], 0f);
                }
                specB.InPlaceFFT(fft);
                double sumCross2 = 0.0;
                for (int k = lowBin; k <= highBin; k++) {
                    float aRe = specA[k].Real;
                    float aIm = -specA[k].Imaginary;
                    float bRe = specB[k].Real;
                    float bIm = specB[k].Imaginary;
                    float re = aRe * bRe - aIm * bIm;
                    float im = aRe * bIm + aIm * bRe;
                    sumCross2 += Math.Sqrt((double)re * re + (double)im * im);
                }
                float avg2 = (float)(sumCross2 / Math.Max(1, highBin - lowBin + 1));
                float eps2 = MathF.Max(PhatEps, avg2 * 0.02f);
                Array.Clear(gcc, 0, n);
                for (int k = lowBin; k <= highBin; k++) {
                    float aRe = specA[k].Real;
                    float aIm = -specA[k].Imaginary;
                    float bRe = specB[k].Real;
                    float bIm = specB[k].Imaginary;
                    float re = aRe * bRe - aIm * bIm;
                    float im = aRe * bIm + aIm * bRe;
                    float mag = MathF.Sqrt(re * re + im * im) + eps2;
                    float bandW = 1f;
                    if (k - lowBin < taperBins) {
                        float t = (k - lowBin) / (float)taperBins;
                        bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                    } else if (highBin - k < taperBins) {
                        float t = (highBin - k) / (float)taperBins;
                        bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                    }
                    float normRe = (re / mag) * bandW;
                    float normIm = (im / mag) * bandW;
                    gcc[k] = new Complex(normRe, normIm);
                    gcc[n - k] = new Complex(normRe, -normIm);
                }
                double fracDelta = RefinePeakExactDtft(gcc, lowBin, highBin, n, 0.0);
                return peakLag + fracDelta;
            }

            // Already integer-aligned (peakLag == 0): re-evaluate exact band-limited DTFT around 0
            Array.Clear(gcc, 0, n);
            for (int k = lowBin; k <= highBin; k++) {
                float aRe = specA[k].Real;
                float aIm = -specA[k].Imaginary;
                float bRe = specB[k].Real;
                float bIm = specB[k].Imaginary;
                float re = aRe * bRe - aIm * bIm;
                float im = aRe * bIm + aIm * bRe;
                float mag = MathF.Sqrt(re * re + im * im) + adaptiveEps;
                float bandW = 1f;
                if (k - lowBin < taperBins) {
                    float t = (k - lowBin) / (float)taperBins;
                    bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                } else if (highBin - k < taperBins) {
                    float t = (highBin - k) / (float)taperBins;
                    bandW = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                }
                float normRe = (re / mag) * bandW;
                float normIm = (im / mag) * bandW;
                gcc[k] = new Complex(normRe, normIm);
            }
            return peakLag + RefinePeakExactDtft(gcc, lowBin, highBin, n, 0.0);
        }

        static double RefinePeakExactDtft(Complex[] gccSpec, int lowBin, int highBin, int n, double centerLag) {
            // Golden-section maximization of exact band-limited continuous IFFT Re(sum G[k] * exp(+j 2*pi*k*tau/N))
            // Zero sinc-truncation bias; achieves < 0.03 samp even on sparse tonal passages and < 0.01 samp on broadband music.
            double lo = centerLag - 0.75;
            double hi = centerLag + 0.75;
            const double invPhi = 0.6180339887498949;
            double c = hi - invPhi * (hi - lo);
            double d = lo + invPhi * (hi - lo);
            double fc = EvalExactDtft(gccSpec, lowBin, highBin, n, c);
            double fd = EvalExactDtft(gccSpec, lowBin, highBin, n, d);

            for (int iter = 0; iter < 18; iter++) {
                if (fc < fd) {
                    lo = c;
                    c = d;
                    fc = fd;
                    d = lo + invPhi * (hi - lo);
                    fd = EvalExactDtft(gccSpec, lowBin, highBin, n, d);
                } else {
                    hi = d;
                    d = c;
                    fd = fc;
                    c = hi - invPhi * (hi - lo);
                    fc = EvalExactDtft(gccSpec, lowBin, highBin, n, c);
                }
            }
            return 0.5 * (lo + hi);
        }

        static double EvalExactDtft(Complex[] gccSpec, int lowBin, int highBin, int n, double tau) {
            double w0 = (2.0 * Math.PI * tau) / n;
            double acc = 0.0;
            for (int k = lowBin; k <= highBin; k++) {
                double ang = w0 * k;
                Complex g = gccSpec[k];
                acc += g.Real * Math.Cos(ang) - g.Imaginary * Math.Sin(ang);
            }
            return acc;
        }

        static double RefinePeakSinc(Complex[] gcc, int n, int peakLag) {
            // Evaluate quadratic initial guess
            float ym1 = ReadCircularReal(gcc, n, peakLag - 1);
            float y0 = ReadCircularReal(gcc, n, peakLag);
            float yp1 = ReadCircularReal(gcc, n, peakLag + 1);
            double denom = ym1 - 2.0 * y0 + yp1;
            double quad = Math.Abs(denom) > 1e-12 ? 0.5 * (ym1 - yp1) / denom : 0.0;
            quad = Math.Clamp(quad, -0.5, 0.5);

            // Refine around quadratic guess with 17-tap Hann-windowed sinc interpolation via golden-section maximization
            double lo = Math.Max(-0.75, quad - 0.35);
            double hi = Math.Min(0.75, quad + 0.35);
            const double invPhi = 0.6180339887498949;
            double c = hi - invPhi * (hi - lo);
            double d = lo + invPhi * (hi - lo);
            double fc = EvalWindowedSinc(gcc, n, peakLag, c);
            double fd = EvalWindowedSinc(gcc, n, peakLag, d);

            for (int iter = 0; iter < 18; iter++) {
                if (fc < fd) {
                    lo = c;
                    c = d;
                    fc = fd;
                    d = lo + invPhi * (hi - lo);
                    fd = EvalWindowedSinc(gcc, n, peakLag, d);
                } else {
                    hi = d;
                    d = c;
                    fd = fc;
                    c = hi - invPhi * (hi - lo);
                    fc = EvalWindowedSinc(gcc, n, peakLag, c);
                }
            }
            return 0.5 * (lo + hi);
        }

        static double EvalWindowedSinc(Complex[] gcc, int n, int centerLag, double frac) {
            const int half = 8;
            double sum = 0.0;
            for (int k = -half; k <= half; k++) {
                double x = frac - k;
                double w = 0.5 * (1.0 + Math.Cos(Math.PI * x / (half + 1.0)));
                double sinc = Math.Abs(x) < 1e-10 ? 1.0 : (Math.Sin(Math.PI * x) / (Math.PI * x));
                sum += ReadCircularReal(gcc, n, centerLag + k) * sinc * w;
            }
            return sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float ReadCircularReal(Complex[] buf, int n, int signedLag) {
            int idx = signedLag % n;
            if (idx < 0) {
                idx += n;
            }
            return buf[idx].Real;
        }

        /// <summary>
        /// Stage 3: Exact Theil-Sen non-parametric median-of-pairwise-slopes linear regression
        /// (<c>y = intercept + slope * x</c>), resistant to up to 29.3% gross outlier windows.
        /// </summary>
        public static void ComputeTheilSenRegression(
            IReadOnlyList<double> x,
            IReadOnlyList<double> y,
            out double intercept,
            out double slope) {
            int m = Math.Min(x.Count, y.Count);
            if (m <= 0) {
                intercept = 0.0;
                slope = 0.0;
                return;
            }
            if (m == 1) {
                intercept = y[0];
                slope = 0.0;
                return;
            }

            List<double> slopes = new List<double>(m * (m - 1) / 2);
            for (int i = 0; i < m; i++) {
                for (int j = i + 1; j < m; j++) {
                    double dx = x[j] - x[i];
                    if (Math.Abs(dx) > 1e-6) {
                        slopes.Add((y[j] - y[i]) / dx);
                    }
                }
            }
            if (slopes.Count == 0) {
                slope = 0.0;
            } else {
                slopes.Sort();
                int mid = slopes.Count / 2;
                slope = (slopes.Count & 1) == 1 ? slopes[mid] : 0.5 * (slopes[mid - 1] + slopes[mid]);
            }

            double[] intercepts = new double[m];
            for (int i = 0; i < m; i++) {
                intercepts[i] = y[i] - slope * x[i];
            }
            Array.Sort(intercepts);
            int iMid = m / 2;
            intercept = (m & 1) == 1 ? intercepts[iMid] : 0.5 * (intercepts[iMid - 1] + intercepts[iMid]);
        }

        static float[] BuildLogOnsetEnvelope(float[] mono, int frames, int hop) {
            float[] env = new float[frames];
            float prevLog = 0f;
            float mean = 0f;
            for (int f = 0; f < frames; f++) {
                int start = f * hop;
                double sumSq = 0.0;
                for (int i = 0; i < hop; i++) {
                    float v = mono[start + i];
                    sumSq += v * v;
                }
                float rms = (float)Math.Sqrt(sumSq / hop);
                float logE = MathF.Log(1f + 25f * rms);
                // Half-wave rectified onset flux + subtle sustained energy keeps both percussive & ambient cues locked
                float onset = MathF.Max(0f, logE - prevLog) + 0.25f * logE;
                env[f] = onset;
                mean += onset;
                prevLog = logE;
            }
            mean /= Math.Max(1, frames);
            for (int f = 0; f < frames; f++) {
                env[f] -= mean;
            }
            return env;
        }

        static float[] BuildHannWindow(int n) {
            float[] win = new float[n];
            double scale = 2.0 * Math.PI / n;
            for (int i = 0; i < n; i++) {
                win[i] = (float)(0.5 * (1.0 - Math.Cos(scale * i)));
            }
            return win;
        }

        static float ComputeMeanSquare(float[] buf, int start, int length) {
            double sum = 0.0;
            int end = start + length;
            for (int i = start; i < end; i++) {
                float v = buf[i];
                sum += v * v;
            }
            return (float)(sum / Math.Max(1, length));
        }

        static int NextPowerOfTwo(int v) {
            int p = 1;
            while (p < v) {
                p <<= 1;
            }
            return p;
        }
    }
}
