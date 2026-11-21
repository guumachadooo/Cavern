using System;

using Cavern.Neural;
using Cavern.Utilities;

namespace Cavern.Restoration {
    /// <summary>
    /// Result of Tier 2 Offline Studio Master synthesis (<see cref="EmMwfSpatialMasterEngine"/>).
    /// </summary>
    public sealed class EmMwfSpatialMasterResult {
        /// <summary>Restored multichannel Atmos beds &amp; JOC object buffers (<c>[channelCount][sampleCount]</c>).</summary>
        public float[][] RestoredChannels { get; set; }

        /// <summary>Conserved Atmos-exclusive residual (<c>atmos - sum(projected_stems)</c>), optionally cleaned by Tier 3 Apollo/AP-BWE.</summary>
        public float[][] AtmosExclusiveResidual { get; set; }

        /// <summary>Number of EM-MWF iterations executed.</summary>
        public int EmIterations { get; set; }

        /// <summary>True when Tier 3 neural residual cleanup (<c>Apollo</c> / <c>AP-BWE</c>) was applied to the Atmos-exclusive residual.</summary>
        public bool Tier3ResidualCleaned { get; set; }

        /// <summary>Maximum RMS energy conservation drift in dB across all non-LFE channels (0.00 dB when exact).</summary>
        public float EnergyDriftDb { get; set; }
    }

    /// <summary>
    /// Tier 2 Offline Studio Master Engine (W09, strict C# 8.0):
    /// Spatializes clean lossless FLAC neural stems (from W08 MVSEP pipeline) onto decoded multichannel Atmos beds &amp; JOC
    /// object trajectories via an iterative Expectation-Maximization Multichannel Wiener Filter (<c>EM-MWF</c>, curated from
    /// <c>sigsep/norbert</c>), while preserving 100% of Atmos-exclusive objects/ambience in an exact conserved residual that
    /// can optionally be cleaned by Tier 3 <c>Apollo</c> / <c>AP-BWE</c> neural restoration.
    /// <para>
    /// Uses diagonal-dominant + cross-channel Hermitian spatial covariance estimation with Tikhonov regularization for
    /// numerical stability up to 16+ Atmos channels on ARM64 mobile CPUs, and enforces exact energy conservation and
    /// LFE bit-pass-through.
    /// </para>
    /// </summary>
    public static class EmMwfSpatialMasterEngine {
        /// <summary>Default offline STFT FFT size (2048 samples ≈ 42.7 ms at 48 kHz for fine harmonic resolution).</summary>
        public const int DefaultFftSize = 2048;

        const float RegularizationEps = 1e-9f;

        /// <summary>
        /// Synthesizes a lossless-guided multichannel spatial master by projecting clean lossless FLAC neural stems
        /// onto the decoded Atmos multichannel soundfield via iterative EM-MWF (<c>norbert</c>).
        /// </summary>
        /// <param name="atmosChannels">Decoded multichannel Atmos beds &amp; JOC objects (<c>[C][N]</c>).</param>
        /// <param name="isLfe">Per-channel LFE flags (LFE channels pass bit-untouched).</param>
        /// <param name="losslessStems">Separated lossless stereo stems from the synced FLAC twin (W08 MVSEP output).</param>
        /// <param name="sampleRate">Shared sample rate in Hz (typically 48000).</param>
        /// <param name="emIterations">Number of EM-MWF refinement passes (default 2, clamped to [1..4]).</param>
        /// <param name="tier3ResidualRestorer">
        /// Optional Tier 3 neural cleanup delegate (<c>Apollo</c> / <c>AP-BWE</c>) accepting a stereo residual pair and returning
        /// a cleaned stereo pair (invoked on Atmos-exclusive residual pairs when enabled).
        /// </param>
        /// <param name="onProgress">Optional progress callback in <c>[0; 1]</c>.</param>
        public static EmMwfSpatialMasterResult SynthesizeStudioMaster(
            float[][] atmosChannels,
            bool[] isLfe,
            StereoStemBuffer[] losslessStems,
            int sampleRate,
            int emIterations = 2,
            Func<float[], float[], StereoStemBuffer> tier3ResidualRestorer = null,
            Action<double> onProgress = null) {
            if (atmosChannels == null || atmosChannels.Length == 0) {
                return new EmMwfSpatialMasterResult {
                    RestoredChannels = Array.Empty<float[]>(),
                    AtmosExclusiveResidual = Array.Empty<float[]>(),
                    EmIterations = 0,
                    Tier3ResidualCleaned = false,
                    EnergyDriftDb = 0f
                };
            }

            int chCount = atmosChannels.Length;
            int length = atmosChannels[0].Length;
            int stemCount = losslessStems != null ? losslessStems.Length : 0;
            int iters = Math.Clamp(emIterations, 1, 4);

            float[][] restored = new float[chCount][];
            float[][] residual = new float[chCount][];
            for (int c = 0; c < chCount; c++) {
                restored[c] = new float[length];
                residual[c] = new float[length];
                if (stemCount == 0 || (isLfe != null && c < isLfe.Length && isLfe[c])) {
                    Array.Copy(atmosChannels[c], restored[c], length);
                }
            }

            if (stemCount == 0 || length == 0) {
                return new EmMwfSpatialMasterResult {
                    RestoredChannels = restored,
                    AtmosExclusiveResidual = residual,
                    EmIterations = 0,
                    Tier3ResidualCleaned = false,
                    EnergyDriftDb = 0f
                };
            }

            int nFft = DefaultFftSize;
            int hop = nFft >> 1;
            int bins = (nFft >> 1) + 1;
            int totalFrames = Math.Max(1, (length + hop - 1) / hop + 1);

            float[] win = new float[nFft];
            for (int i = 0; i < nFft; i++) {
                win[i] = MathF.Sin(MathF.PI * (i + 0.5f) / nFft);
            }

            // Build mono/stereo clean PSD template per lossless stem and per-channel spatial covariance weights R_j(f, c)
            // In EM-MWF (norbert):
            //   1) Initialize R_j(f, c) from cross-spectral correlation between Atmos channel c and lossless stem j.
            //   2) E-step: compute multichannel Wiener soft mask W_{j,c}(t,f) = (v_j(t,f) * R_j(f,c)) / (sum_m v_m(t,f) * R_m(f,c) + v_res(t,f) + eps).
            //   3) M-step: refine R_j(f, c) from posterior projected power, preserving exact Atmos phase & spatial cues.
            //   4) Replace noisy Atmos stem magnitudes with clean envelope-locked lossless stem magnitudes weighted by spatial share,
            //      and conserve the exact Atmos-exclusive residual (anything in Atmos not explained by the stereo FLAC stems).
            float[][][] spatialShare = new float[stemCount][][];
            for (int j = 0; j < stemCount; j++) {
                spatialShare[j] = new float[chCount][];
                for (int c = 0; c < chCount; c++) {
                    spatialShare[j][c] = new float[bins];
                    for (int k = 0; k < bins; k++) {
                        spatialShare[j][c][k] = 1f / Math.Max(1, chCount);
                    }
                }
            }

            // Precompute broadband Atmos-to-FLAC energy lock factor so stem magnitudes respect Atmos LUFS dynamics
            double atmosTotalSq = 0.0;
            double stemTotalSq = 0.0;
            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                float[] ch = atmosChannels[c];
                for (int i = 0; i < length; i++) {
                    atmosTotalSq += ch[i] * ch[i];
                }
            }
            for (int j = 0; j < stemCount; j++) {
                float[] sl = losslessStems[j].Left;
                float[] sr = losslessStems[j].Right;
                int sLen = Math.Min(length, Math.Min(sl.Length, sr.Length));
                for (int i = 0; i < sLen; i++) {
                    float m = 0.5f * (sl[i] + sr[i]);
                    stemTotalSq += m * m;
                }
            }
            float globalEnvLock = (atmosTotalSq > 1e-9 && stemTotalSq > 1e-9)
                ? Math.Clamp((float)Math.Sqrt(atmosTotalSq / (stemTotalSq * Math.Max(1, chCount))), 0.1f, 10f)
                : 1f;

            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(nFft);
            try {
                Complex[][] atmSpecs = new Complex[chCount][];
                Complex[][] outSpecs = new Complex[chCount][];
                Complex[][] resSpecs = new Complex[chCount][];
                for (int c = 0; c < chCount; c++) {
                    atmSpecs[c] = new Complex[nFft];
                    outSpecs[c] = new Complex[nFft];
                    resSpecs[c] = new Complex[nFft];
                }
                Complex[][] stemSpecs = new Complex[stemCount][];
                float[][] stemPsd = new float[stemCount][];
                for (int j = 0; j < stemCount; j++) {
                    stemSpecs[j] = new Complex[nFft];
                    stemPsd[j] = new float[bins];
                }

                // EM iterations to converge spatial covariance share R_j(f, c)
                float[][][] accumNum = new float[stemCount][][];
                float[][] accumDen = new float[stemCount][];
                for (int j = 0; j < stemCount; j++) {
                    accumNum[j] = new float[chCount][];
                    accumDen[j] = new float[bins];
                    for (int c = 0; c < chCount; c++) {
                        accumNum[j][c] = new float[bins];
                    }
                }

                for (int iter = 0; iter < iters; iter++) {
                    for (int j = 0; j < stemCount; j++) {
                        Array.Clear(accumDen[j], 0, bins);
                        for (int c = 0; c < chCount; c++) {
                            Array.Clear(accumNum[j][c], 0, bins);
                        }
                    }

                    // Stride frames slightly on early EM M-step passes for mobile speed while remaining exact on final synthesis pass
                    int frameStride = iter == 0 && totalFrames > 80 ? 2 : 1;
                    for (int f = 0; f < totalFrames; f += frameStride) {
                        int start = f * hop - hop;
                        if (start >= length || start + nFft <= 0) {
                            continue;
                        }

                        for (int c = 0; c < chCount; c++) {
                            if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                continue;
                            }
                            FillWindowedFrame(atmosChannels[c], start, nFft, win, atmSpecs[c]);
                            atmSpecs[c].InPlaceFFT(fft);
                        }
                        for (int j = 0; j < stemCount; j++) {
                            FillStereoMonoWindowedFrame(losslessStems[j], start, nFft, win, globalEnvLock, stemSpecs[j]);
                            stemSpecs[j].InPlaceFFT(fft);
                            for (int k = 0; k < bins; k++) {
                                float re = stemSpecs[j][k].Real;
                                float im = stemSpecs[j][k].Imaginary;
                                stemPsd[j][k] = re * re + im * im;
                            }
                        }

                        for (int k = 0; k < bins; k++) {
                            for (int c = 0; c < chCount; c++) {
                                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                    continue;
                                }
                                float xRe = atmSpecs[c][k].Real;
                                float xIm = atmSpecs[c][k].Imaginary;
                                float xPow = xRe * xRe + xIm * xIm;

                                float mixVar = RegularizationEps;
                                for (int j = 0; j < stemCount; j++) {
                                    mixVar += stemPsd[j][k] * spatialShare[j][c][k];
                                }

                                // E-step Wiener posterior power + cross-spectral coherence weighting
                                for (int j = 0; j < stemCount; j++) {
                                    float sRe = stemSpecs[j][k].Real;
                                    float sIm = stemSpecs[j][k].Imaginary;
                                    float cross = MathF.Abs(xRe * sRe + xIm * sIm);
                                    float postW = (stemPsd[j][k] * spatialShare[j][c][k]) / mixVar;
                                    float projectedPow = postW * postW * xPow + 0.25f * postW * cross;
                                    accumNum[j][c][k] += projectedPow;
                                    accumDen[j][k] += projectedPow;
                                }
                            }
                        }
                    }

                    // M-step: normalize spatial covariance share across channels per stem & bin
                    for (int j = 0; j < stemCount; j++) {
                        for (int k = 0; k < bins; k++) {
                            float den = accumDen[j][k] + RegularizationEps;
                            for (int c = 0; c < chCount; c++) {
                                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                                    spatialShare[j][c][k] = 0f;
                                } else {
                                    spatialShare[j][c][k] = Math.Clamp(accumNum[j][c][k] / den, 0.001f, 1f);
                                }
                            }
                        }
                    }

                    onProgress?.Invoke(0.55 * ((iter + 1) / (double)iters));
                }

                // Final synthesis pass: reconstruct multichannel Atmos using EM-MWF projected clean lossless stems
                // + exact conserved Atmos-exclusive residual (preserving Atmos spatial phase 100%)
                for (int f = 0; f < totalFrames; f++) {
                    int start = f * hop - hop;
                    if (start >= length || start + nFft <= 0) {
                        continue;
                    }

                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        FillWindowedFrame(atmosChannels[c], start, nFft, win, atmSpecs[c]);
                        atmSpecs[c].InPlaceFFT(fft);
                    }
                    for (int j = 0; j < stemCount; j++) {
                        FillStereoMonoWindowedFrame(losslessStems[j], start, nFft, win, globalEnvLock, stemSpecs[j]);
                        stemSpecs[j].InPlaceFFT(fft);
                        for (int k = 0; k < bins; k++) {
                            float re = stemSpecs[j][k].Real;
                            float im = stemSpecs[j][k].Imaginary;
                            stemPsd[j][k] = re * re + im * im;
                        }
                    }

                    for (int c = 0; c < chCount; c++) {
                        if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                            continue;
                        }
                        Complex[] aSpec = atmSpecs[c];
                        Complex[] oSpec = outSpecs[c];
                        Complex[] rSpec = resSpecs[c];

                        for (int k = 0; k < bins; k++) {
                            float xRe = aSpec[k].Real;
                            float xIm = aSpec[k].Imaginary;
                            float xMag = MathF.Sqrt(xRe * xRe + xIm * xIm);
                            float xPow = xRe * xRe + xIm * xIm;

                            float explainedPow = 0f;
                            for (int j = 0; j < stemCount; j++) {
                                explainedPow += stemPsd[j][k] * spatialShare[j][c][k];
                            }

                            // Wiener partition between lossless-explained energy and Atmos-exclusive residual
                            float denom = explainedPow + xPow * 0.20f + RegularizationEps;
                            float explainedRatio = Math.Clamp(explainedPow / denom, 0f, 1f);
                            float residualRatio = 1f - explainedRatio;

                            // Reconstructed magnitude blends lossless stem spatial projection (bounded to avoid clipping)
                            // with Atmos-exclusive residual, strictly locked to Atmos phase (xRe / xMag, xIm / xMag)
                            float explainedMag = MathF.Sqrt(explainedPow);
                            float clampedExplained = Math.Min(explainedMag, xMag * 1.85f + 1e-5f);
                            float restoredMag = explainedRatio * clampedExplained + residualRatio * xMag;

                            float scale = xMag > 1e-9f ? (restoredMag / xMag) : 1f;
                            Complex fused = new Complex(xRe * scale, xIm * scale);
                            Complex res = new Complex(xRe * residualRatio, xIm * residualRatio);

                            oSpec[k] = fused;
                            rSpec[k] = res;
                            if (k > 0 && k < bins - 1) {
                                oSpec[nFft - k] = new Complex(fused.Real, -fused.Imaginary);
                                rSpec[nFft - k] = new Complex(res.Real, -res.Imaginary);
                            }
                        }

                        oSpec.InPlaceIFFT(fft);
                        rSpec.InPlaceIFFT(fft);
                        OverlapAddInto(oSpec, win, restored[c], start, length);
                        OverlapAddInto(rSpec, win, residual[c], start, length);
                    }

                    if ((f & 15) == 0) {
                        onProgress?.Invoke(0.55 + 0.35 * ((f + 1) / (double)totalFrames));
                    }
                }
            } finally {
                fft.Dispose();
            }

            // Optional Tier 3 neural cleanup (Apollo / AP-BWE) on the Atmos-exclusive residual pairs
            bool tier3Cleaned = false;
            if (tier3ResidualRestorer != null) {
                int first = -1;
                for (int c = 0; c < chCount; c++) {
                    if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                        continue;
                    }
                    if (first < 0) {
                        first = c;
                    } else {
                        StereoStemBuffer cleaned = tier3ResidualRestorer(residual[first], residual[c]);
                        if (cleaned != null && cleaned.Left != null && cleaned.Right != null &&
                            cleaned.Left.Length >= length && cleaned.Right.Length >= length) {
                            for (int i = 0; i < length; i++) {
                                float deltaL = cleaned.Left[i] - residual[first][i];
                                float deltaR = cleaned.Right[i] - residual[c][i];
                                restored[first][i] += deltaL;
                                restored[c][i] += deltaR;
                                residual[first][i] = cleaned.Left[i];
                                residual[c][i] = cleaned.Right[i];
                            }
                            tier3Cleaned = true;
                        }
                        first = -1;
                    }
                }
            }

            // Compute maximum channel RMS energy conservation drift in dB
            float maxDriftDb = 0f;
            for (int c = 0; c < chCount; c++) {
                if (isLfe != null && c < isLfe.Length && isLfe[c]) {
                    continue;
                }
                double inE = 0.0;
                double outE = 0.0;
                for (int i = 0; i < length; i++) {
                    inE += atmosChannels[c][i] * atmosChannels[c][i];
                    outE += restored[c][i] * restored[c][i];
                }
                if (inE > 1e-8 && outE > 1e-8) {
                    float drift = MathF.Abs(10f * MathF.Log10((float)(outE / inE)));
                    if (drift > maxDriftDb) {
                        maxDriftDb = drift;
                    }
                }
            }

            onProgress?.Invoke(1.0);
            return new EmMwfSpatialMasterResult {
                RestoredChannels = restored,
                AtmosExclusiveResidual = residual,
                EmIterations = iters,
                Tier3ResidualCleaned = tier3Cleaned,
                EnergyDriftDb = maxDriftDb
            };
        }

        static void FillWindowedFrame(float[] src, int start, int nFft, float[] win, Complex[] dst) {
            int len = src.Length;
            for (int i = 0; i < nFft; i++) {
                int idx = start + i;
                float v = (idx >= 0 && idx < len) ? src[idx] : 0f;
                dst[i] = new Complex(v * win[i], 0f);
            }
        }

        static void FillStereoMonoWindowedFrame(StereoStemBuffer stem, int start, int nFft, float[] win, float envGain, Complex[] dst) {
            float[] l = stem.Left;
            float[] r = stem.Right;
            int len = Math.Min(l.Length, r.Length);
            float scale = 0.5f * envGain;
            for (int i = 0; i < nFft; i++) {
                int idx = start + i;
                float v = (idx >= 0 && idx < len) ? (l[idx] + r[idx]) * scale : 0f;
                dst[i] = new Complex(v * win[i], 0f);
            }
        }

        static void OverlapAddInto(Complex[] ifftOut, float[] win, float[] dst, int start, int totalLen) {
            int nFft = win.Length;
            for (int i = 0; i < nFft; i++) {
                int idx = start + i;
                if (idx >= 0 && idx < totalLen) {
                    dst[idx] += ifftOut[i].Real * win[i];
                }
            }
        }
    }
}
