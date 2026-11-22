using System;
using System.Numerics;

using Cavern.Spatial;
using Cavern.Utilities;
using Complex = Cavern.Utilities.Complex;

namespace Cavern.Remapping {
    /// <summary>
    /// Pure-physics STFT parametric soundfield analyzer and unity-energy-conserving upmix engine.
    /// Curated from <c>Spatial_Audio_Framework</c> (SAF DirAC/COMPASS), <c>SPARTA</c>, and <c>qutschwalze/upmixer</c>.
    /// Uses 512-point 50%-overlap WOLA with exact COLA <c>sqrt(Hann)</c> windows (<c>LatencySamples == 512</c> for any block size),
    /// 2x2 Hermitian cross-spectral covariance eigenvalue diffuseness, and orthogonal all-pass decorrelation.
    /// </summary>
    public sealed class ParametricStftAnalyzer : IDisposable {
        /// <summary>
        /// STFT window length in samples (10.67 ms at 48 kHz).
        /// </summary>
        public const int FftSize = 512;

        /// <summary>
        /// STFT hop size in samples (50% overlap, 5.33 ms at 48 kHz, matching Cavern's 256-sample real-time update rate).
        /// </summary>
        public const int HopSize = 256;

        /// <summary>
        /// Exact deterministic causal latency of the streaming WOLA engine in samples across all block sizes.
        /// </summary>
        public const int LatencySamples = FftSize;

        const int SpectrumBins = (FftSize >> 1) + 1;
        const float Epsilon = 1e-12f;

        /// <summary>
        /// Maximum number of output buses synthesized by a single STFT hop
        /// (0=FL, 1=FR, 2=C, 3=SL_dir, 4=SR_dir, 5=SL_diff, 6=SR_diff, 7=RL_diff, 8=RR_diff).
        /// </summary>
        const int MaxOutputBuses = 9;

        readonly int sampleRate;
        readonly ThreadSafeFFTCache fftCache;
        readonly float[] window = new float[FftSize];
        readonly float[] smoothAlpha = new float[SpectrumBins];
        readonly float[] binFrequencies = new float[SpectrumBins];
        readonly int[] erbHalfWidth = new int[SpectrumBins];

        // Streaming input overlap buffers
        readonly float[] prevInL = new float[HopSize];
        readonly float[] prevInR = new float[HopSize];
        readonly float[] currInL = new float[HopSize];
        readonly float[] currInR = new float[HopSize];
        int inFill;

        // Per-bin smoothed 2x2 Hermitian covariance matrix C = [Pll, Plr; Plr*, Prr] + ERB-pooled view
        readonly float[] covLL = new float[SpectrumBins];
        readonly float[] covRR = new float[SpectrumBins];
        readonly float[] covLRRe = new float[SpectrumBins];
        readonly float[] covLRIm = new float[SpectrumBins];
        readonly float[] poolLL = new float[SpectrumBins];
        readonly float[] poolRR = new float[SpectrumBins];
        readonly float[] poolLRRe = new float[SpectrumBins];
        readonly float[] poolLRIm = new float[SpectrumBins];

        // Reusable zero-GC FFT work buffers
        readonly Complex[] specL = new Complex[FftSize];
        readonly Complex[] specR = new Complex[FftSize];
        readonly Complex[][] outSpec;
        readonly float[][] olaTail;
        readonly float[][] readyOut;

        // Orthogonal all-pass decorrelator bank for diffuse streams
        readonly OrthogonalDecorrelator decorrelator;

        // Smooth height-elevation tracker for Cavernize 3D mode
        float smoothedHeightRatio;

        /// <summary>
        /// Latest energy-weighted DirAC/COMPASS soundfield telemetry (<see cref="SoundfieldTelemetry.Coherence"/>,
        /// <see cref="SoundfieldTelemetry.Diffuseness"/>, <see cref="SoundfieldTelemetry.DominantDoa"/>).
        /// </summary>
        public SoundfieldTelemetry LatestTelemetry { get; private set; } = SoundfieldTelemetry.Empty();

        /// <summary>
        /// Frequency below which sub-bass stays 100% phase-coherent on the front/ground layer (Hz).
        /// </summary>
        public float BassAnchorHz { get; set; } = 120f;

        /// <summary>
        /// Creates a streaming parametric STFT analyzer for <paramref name="sampleRate"/>.
        /// </summary>
        public ParametricStftAnalyzer(int sampleRate) {
            this.sampleRate = sampleRate > 0 ? sampleRate : 48000;
            fftCache = new ThreadSafeFFTCache(FftSize);
            decorrelator = new OrthogonalDecorrelator(6, this.sampleRate);

            outSpec = new Complex[MaxOutputBuses][];
            olaTail = new float[MaxOutputBuses][];
            readyOut = new float[MaxOutputBuses][];
            for (int b = 0; b < MaxOutputBuses; b++) {
                outSpec[b] = new Complex[FftSize];
                olaTail[b] = new float[HopSize];
                readyOut[b] = new float[HopSize];
            }

            // Exact COLA sqrt(Hann) sine window: w[n]^2 + w[n + N/2]^2 == sin^2(x) + cos^2(x) == 1.0 identically
            for (int n = 0; n < FftSize; n++) {
                window[n] = MathF.Sin(MathF.PI * (n + 0.5f) / FftSize);
            }

            // ERB-aligned covariance pooling widths (SAF/DirAC practice) & temporal smoothing (65 ms low -> 35 ms high)
            float hopSeconds = HopSize / (float)this.sampleRate;
            float binHz = this.sampleRate / (float)FftSize;
            for (int k = 0; k < SpectrumBins; k++) {
                float freq = k * binHz;
                binFrequencies[k] = freq;
                // Glasberg & Moore ERB(f) = 24.7 * (4.37 * f/1000 + 1); pool +-halfWidth bins to eliminate finite-sample bias
                float erbHz = 24.7f * (0.00437f * freq + 1f);
                int halfW = (int)(0.5f * erbHz / binHz);
                if (halfW > 4) {
                    halfW = 4;
                }
                erbHalfWidth[k] = halfW;

                float normF = MathF.Log10(1f + freq / 250f) / MathF.Log10(1f + 20000f / 250f);
                if (normF < 0f) {
                    normF = 0f;
                } else if (normF > 1f) {
                    normF = 1f;
                }
                float tauSec = 0.065f - 0.030f * normF;
                smoothAlpha[k] = 1f - MathF.Exp(-hopSeconds / tauSec);
            }
        }

        /// <summary>
        /// Clears all overlap-add buffers, covariance accumulators, and decorrelator delay lines.
        /// </summary>
        public void Reset() {
            Array.Clear(prevInL, 0, prevInL.Length);
            Array.Clear(prevInR, 0, prevInR.Length);
            Array.Clear(currInL, 0, currInL.Length);
            Array.Clear(currInR, 0, currInR.Length);
            Array.Clear(covLL, 0, covLL.Length);
            Array.Clear(covRR, 0, covRR.Length);
            Array.Clear(covLRRe, 0, covLRRe.Length);
            Array.Clear(covLRIm, 0, covLRIm.Length);
            Array.Clear(poolLL, 0, poolLL.Length);
            Array.Clear(poolRR, 0, poolRR.Length);
            Array.Clear(poolLRRe, 0, poolLRRe.Length);
            Array.Clear(poolLRIm, 0, poolLRIm.Length);
            for (int b = 0; b < MaxOutputBuses; b++) {
                Array.Clear(olaTail[b], 0, HopSize);
                Array.Clear(readyOut[b], 0, HopSize);
            }
            decorrelator.Reset();
            inFill = 0;
            smoothedHeightRatio = 0f;
            LatestTelemetry = SoundfieldTelemetry.Empty();
        }

        /// <summary>
        /// Performs analysis-only STFT covariance estimation to update <see cref="LatestTelemetry"/> without modifying audio.
        /// </summary>
        public void AnalyzeOnly(float[] inL, float[] inR, int count) {
            if (inL == null || inR == null || count <= 0) {
                return;
            }
            for (int i = 0; i < count; i++) {
                currInL[inFill] = inL[i];
                currInR[inFill] = inR[i];
                inFill++;
                if (inFill == HopSize) {
                    ComputeForwardFftAndCovariance();
                    FinishInputHop();
                }
            }
        }

        /// <summary>
        /// Upmixes stereo <paramref name="inL"/>/<paramref name="inR"/> to 5.1 or 7.1 bed outputs using DirAC/COMPASS
        /// 2x2 Hermitian covariance decomposition and orthogonal all-pass decorrelation with exact unity energy conservation:
        /// <c>|FL|^2 + |FR|^2 + |C|^2 + |SL|^2 + |SR|^2 + |RL|^2 + |RR|^2 == |inL|^2 + |inR|^2</c>.
        /// </summary>
        public void ProcessStereoToSurround(
            float[] inL,
            float[] inR,
            float[] outFL,
            float[] outFR,
            float[] outC,
            float[] outSL,
            float[] outSR,
            float[] outRL,
            float[] outRR,
            int count,
            bool extractCenter,
            bool mode51) {
            if (inL == null || inR == null || count <= 0) {
                return;
            }
            for (int i = 0; i < count; i++) {
                float sampleL = inL[i];
                float sampleR = inR[i];
                outFL[i] = readyOut[0][inFill];
                outFR[i] = readyOut[1][inFill];
                if (extractCenter && outC != null) {
                    outC[i] = readyOut[2][inFill];
                }
                if (outSL != null) {
                    outSL[i] = readyOut[3][inFill] + readyOut[5][inFill];
                }
                if (outSR != null) {
                    outSR[i] = readyOut[4][inFill] + readyOut[6][inFill];
                }
                if (!mode51) {
                    if (outRL != null) {
                        outRL[i] = readyOut[7][inFill];
                    }
                    if (outRR != null) {
                        outRR[i] = readyOut[8][inFill];
                    }
                }

                currInL[inFill] = sampleL;
                currInR[inFill] = sampleR;
                inFill++;
                if (inFill == HopSize) {
                    ExecuteStereoToSurroundHop(extractCenter, mode51);
                    FinishInputHop();
                }
            }
        }

        /// <summary>
        /// Expands a native 5.1 surround pair (<paramref name="inSL"/>, <paramref name="inSR"/>) into 7.1
        /// side + rear channels while keeping coherent directional cues on sides and splitting diffuse ambience
        /// orthogonally across sides and rears with exact unity energy conservation.
        /// </summary>
        public void ProcessSurroundSplitToRear(
            float[] inSL,
            float[] inSR,
            float[] outSL,
            float[] outSR,
            float[] outRL,
            float[] outRR,
            int count) {
            if (inSL == null || inSR == null || count <= 0) {
                return;
            }
            for (int i = 0; i < count; i++) {
                float sampleSL = inSL[i];
                float sampleSR = inSR[i];
                outSL[i] = readyOut[0][inFill] + readyOut[5][inFill];
                outSR[i] = readyOut[1][inFill] + readyOut[6][inFill];
                outRL[i] = readyOut[7][inFill];
                outRR[i] = readyOut[8][inFill];

                currInL[inFill] = sampleSL;
                currInR[inFill] = sampleSR;
                inFill++;
                if (inFill == HopSize) {
                    ExecuteSurroundSplitHop();
                    FinishInputHop();
                }
            }
        }

        /// <summary>
        /// Extracts elevated virtual height ambience from a symmetric ground channel pair (<paramref name="inL"/>, <paramref name="inR"/>)
        /// using STFT diffuseness above <paramref name="crossoverHz"/>. Coherent direct sources and frequencies below
        /// <paramref name="crossoverHz"/> remain anchored on the ground layer; diffuse upper-band energy splits with exact
        /// constant power (<c>g_ground^2 + g_height^2 == 1.0</c>) and orthogonal all-pass decorrelation.
        /// </summary>
        public void ProcessGroundToHeightPair(
            float[] inL,
            float[] inR,
            float[] outGroundL,
            float[] outGroundR,
            float[] outHeightL,
            float[] outHeightR,
            int count,
            float effect,
            float smoothness,
            float crossoverHz,
            out float elevationRatio) {
            if (inL == null || inR == null || count <= 0) {
                elevationRatio = smoothedHeightRatio;
                return;
            }
            float clampedEffect = effect < 0f ? 0f : (effect > 1f ? 1f : effect);
            float clampedSmooth = smoothness < 0f ? 0f : (smoothness > 0.999f ? 0.999f : smoothness);
            float xover = crossoverHz < 120f ? 120f : crossoverHz;

            for (int i = 0; i < count; i++) {
                float sampleL = inL[i];
                float sampleR = inR[i];
                outGroundL[i] = readyOut[0][inFill];
                outGroundR[i] = readyOut[1][inFill];
                outHeightL[i] = readyOut[5][inFill];
                outHeightR[i] = readyOut[6][inFill];

                currInL[inFill] = sampleL;
                currInR[inFill] = sampleR;
                inFill++;
                if (inFill == HopSize) {
                    ExecuteHeightExtractionHop(clampedEffect, clampedSmooth, xover);
                    FinishInputHop();
                }
            }
            elevationRatio = smoothedHeightRatio;
        }

        void ExecuteStereoToSurroundHop(bool extractCenter, bool mode51) {
            ComputeForwardFftAndCovariance();

            const float sideDiffWeight71 = 0.7416198487f; // sqrt(0.55)
            const float rearDiffWeight71 = 0.6708203932f; // sqrt(0.45)  -> 0.55 + 0.45 == 1.0

            int activeBuses = mode51 ? 7 : 9;
            for (int b = 0; b < activeBuses; b++) {
                Array.Clear(outSpec[b], 0, FftSize);
            }

            float anchor = BassAnchorHz;
            for (int k = 0; k < SpectrumBins; k++) {
                Complex l = specL[k];
                Complex r = specR[k];

                float pll = poolLL[k];
                float prr = poolRR[k];
                float re = poolLRRe[k];
                float im = poolLRIm[k];
                float totalP = pll + prr;

                float psi = 0f;
                float gammaRe = 0f;
                float panAbs = 0f;
                if (totalP > Epsilon) {
                    float diffP = pll - prr;
                    float eigDelta = MathF.Sqrt(diffP * diffP + 4f * (re * re + im * im));
                    psi = 1f - eigDelta / totalP;
                    if (psi < 0f) {
                        psi = 0f;
                    } else if (psi > 1f) {
                        psi = 1f;
                    }
                    float denom = MathF.Sqrt(pll * prr) + Epsilon;
                    gammaRe = re / denom;
                    if (gammaRe < -1f) {
                        gammaRe = -1f;
                    } else if (gammaRe > 1f) {
                        gammaRe = 1f;
                    }
                    panAbs = MathF.Abs(prr - pll) / totalP;
                }

                // Frequency-dependent bass anchor: keep sub-bass below BassAnchorHz 100% coherent on fronts
                float freq = binFrequencies[k];
                float spatialEnvelope = ComputeBassAnchorEnvelope(freq, anchor);
                float psiEff = psi * spatialEnvelope;

                float gDir = MathF.Sqrt(1f - psiEff);
                float gDiff = MathF.Sqrt(psiEff);

                // In-phase center extraction weight (only extracts strongly centered, in-phase coherent energy)
                float wCenter = 0f;
                if (extractCenter && gammaRe > 0f) {
                    float centerCue = gammaRe * (1f - panAbs);
                    wCenter = centerCue * centerCue;
                    if (wCenter > 1f) {
                        wCenter = 1f;
                    }
                }

                // Anti-phase surround cue (matrix-encoded out-of-phase L = -R content)
                float wAnti = 0f;
                if (gammaRe < 0f) {
                    wAnti = -gammaRe * spatialEnvelope;
                    if (wAnti > 1f) {
                        wAnti = 1f;
                    }
                }

                // Exact parallelogram power-conserving Mid/Side direct split:
                // |L|^2 + |R|^2 == 2|M|^2 + 2|S|^2
                Complex mid = (l + r) * 0.5f;
                Complex side = (l - r) * 0.5f;

                float gFrontMid = gDir * MathF.Sqrt(1f - wCenter);
                float gCenterMid = gDir * MathF.Sqrt(2f * wCenter);
                float gFrontSide = gDir * MathF.Sqrt(1f - wAnti);
                float gAntiSide = gDir * MathF.Sqrt(wAnti);

                Complex flDir = mid * gFrontMid + side * gFrontSide;
                Complex frDir = mid * gFrontMid - side * gFrontSide;
                Complex cDir = mid * gCenterMid;
                Complex slAnti = side * gAntiSide;
                Complex srAnti = side * (-gAntiSide);

                // Diffuse ambient streams (exact total energy = psiEff * (|L|^2 + |R|^2))
                Complex diffL = l * gDiff;
                Complex diffR = r * gDiff;

                AssignHermitian(outSpec[0], k, flDir);
                AssignHermitian(outSpec[1], k, frDir);
                if (extractCenter) {
                    AssignHermitian(outSpec[2], k, cDir);
                }
                AssignHermitian(outSpec[3], k, slAnti);
                AssignHermitian(outSpec[4], k, srAnti);

                if (mode51) {
                    AssignHermitian(outSpec[5], k, diffL);
                    AssignHermitian(outSpec[6], k, diffR);
                } else {
                    AssignHermitian(outSpec[5], k, diffL * sideDiffWeight71);
                    AssignHermitian(outSpec[6], k, diffR * sideDiffWeight71);
                    AssignHermitian(outSpec[7], k, diffL * rearDiffWeight71);
                    AssignHermitian(outSpec[8], k, diffR * rearDiffWeight71);
                }
            }

            // Synthesize time-domain hops via exact COLA WOLA
            SynthesizeBus(0);
            SynthesizeBus(1);
            if (extractCenter) {
                SynthesizeBus(2);
            } else {
                Array.Clear(readyOut[2], 0, HopSize);
            }
            SynthesizeBus(3);
            SynthesizeBus(4);
            SynthesizeBus(5);
            SynthesizeBus(6);
            decorrelator.ProcessInPlace(0, readyOut[5], HopSize);
            decorrelator.ProcessInPlace(1, readyOut[6], HopSize);

            if (!mode51) {
                SynthesizeBus(7);
                SynthesizeBus(8);
                decorrelator.ProcessInPlace(2, readyOut[7], HopSize);
                decorrelator.ProcessInPlace(3, readyOut[8], HopSize);
            } else {
                Array.Clear(readyOut[7], 0, HopSize);
                Array.Clear(readyOut[8], 0, HopSize);
            }
        }

        void ExecuteSurroundSplitHop() {
            ComputeForwardFftAndCovariance();

            const float sideDiffWeight = 0.70710678118f; // sqrt(0.5)
            const float rearDiffWeight = 0.70710678118f; // sqrt(0.5)

            Array.Clear(outSpec[0], 0, FftSize);
            Array.Clear(outSpec[1], 0, FftSize);
            Array.Clear(outSpec[5], 0, FftSize);
            Array.Clear(outSpec[6], 0, FftSize);
            Array.Clear(outSpec[7], 0, FftSize);
            Array.Clear(outSpec[8], 0, FftSize);

            float anchor = BassAnchorHz;
            for (int k = 0; k < SpectrumBins; k++) {
                Complex sl = specL[k];
                Complex sr = specR[k];
                float pll = poolLL[k];
                float prr = poolRR[k];
                float re = poolLRRe[k];
                float im = poolLRIm[k];
                float totalP = pll + prr;

                float psi = 0f;
                if (totalP > Epsilon) {
                    float diffP = pll - prr;
                    float eigDelta = MathF.Sqrt(diffP * diffP + 4f * (re * re + im * im));
                    psi = 1f - eigDelta / totalP;
                    if (psi < 0f) {
                        psi = 0f;
                    } else if (psi > 1f) {
                        psi = 1f;
                    }
                }

                float spatialEnv = ComputeBassAnchorEnvelope(binFrequencies[k], anchor);
                float psiEff = psi * spatialEnv;
                float gDir = MathF.Sqrt(1f - psiEff);
                float gDiff = MathF.Sqrt(psiEff);

                // Direct side events stay locked on SL/SR; diffuse surround wash splits 50/50 orthogonally to SL/SR + RL/RR
                AssignHermitian(outSpec[0], k, sl * gDir);
                AssignHermitian(outSpec[1], k, sr * gDir);
                AssignHermitian(outSpec[5], k, sl * (gDiff * sideDiffWeight));
                AssignHermitian(outSpec[6], k, sr * (gDiff * sideDiffWeight));
                AssignHermitian(outSpec[7], k, sl * (gDiff * rearDiffWeight));
                AssignHermitian(outSpec[8], k, sr * (gDiff * rearDiffWeight));
            }

            SynthesizeBus(0);
            SynthesizeBus(1);
            SynthesizeBus(5);
            SynthesizeBus(6);
            SynthesizeBus(7);
            SynthesizeBus(8);
            decorrelator.ProcessInPlace(0, readyOut[5], HopSize);
            decorrelator.ProcessInPlace(1, readyOut[6], HopSize);
            decorrelator.ProcessInPlace(2, readyOut[7], HopSize);
            decorrelator.ProcessInPlace(3, readyOut[8], HopSize);
        }

        void ExecuteHeightExtractionHop(float effect, float smoothness, float crossoverHz) {
            ComputeForwardFftAndCovariance();

            Array.Clear(outSpec[0], 0, FftSize);
            Array.Clear(outSpec[1], 0, FftSize);
            Array.Clear(outSpec[5], 0, FftSize);
            Array.Clear(outSpec[6], 0, FftSize);

            double upperTotalEnergy = 0.0;
            double upperDiffuseEnergy = 0.0;

            for (int k = 0; k < SpectrumBins; k++) {
                Complex l = specL[k];
                Complex r = specR[k];
                float pll = poolLL[k];
                float prr = poolRR[k];
                float re = poolLRRe[k];
                float im = poolLRIm[k];
                float totalP = pll + prr;

                float psi = 0f;
                if (totalP > Epsilon) {
                    float diffP = pll - prr;
                    float eigDelta = MathF.Sqrt(diffP * diffP + 4f * (re * re + im * im));
                    psi = 1f - eigDelta / totalP;
                    if (psi < 0f) {
                        psi = 0f;
                    } else if (psi > 1f) {
                        psi = 1f;
                    }
                }

                // Smooth raised-cosine crossover transition from crossoverHz to 2 * crossoverHz
                float freq = binFrequencies[k];
                float bandWeight = ComputeBassAnchorEnvelope(freq, crossoverHz);

                // Only diffuse upper-band ambience lifts to height (zero dry point-source lifting, zero vocal smearing)
                float hPower = psi * bandWeight * effect;
                if (hPower > 0.92f) {
                    hPower = 0.92f;
                }
                float gGround = MathF.Sqrt(1f - hPower);
                float gHeight = MathF.Sqrt(hPower);

                if (bandWeight > 0.01f && totalP > Epsilon) {
                    upperTotalEnergy += totalP * bandWeight;
                    upperDiffuseEnergy += totalP * bandWeight * psi;
                }

                AssignHermitian(outSpec[0], k, l * gGround);
                AssignHermitian(outSpec[1], k, r * gGround);
                AssignHermitian(outSpec[5], k, l * gHeight);
                AssignHermitian(outSpec[6], k, r * gHeight);
            }

            SynthesizeBus(0);
            SynthesizeBus(1);
            SynthesizeBus(5);
            SynthesizeBus(6);
            decorrelator.ProcessInPlace(4, readyOut[5], HopSize);
            decorrelator.ProcessInPlace(5, readyOut[6], HopSize);

            float targetElevation = upperTotalEnergy > 1e-10
                ? (float)(upperDiffuseEnergy / upperTotalEnergy) * effect
                : 0f;
            if (targetElevation > 1f) {
                targetElevation = 1f;
            }
            float hopAlpha = 1f - MathF.Pow(smoothness, 0.25f);
            if (hopAlpha < 0.02f) {
                hopAlpha = 0.02f;
            }
            smoothedHeightRatio += (targetElevation - smoothedHeightRatio) * hopAlpha;
        }

        void ComputeForwardFftAndCovariance() {
            for (int i = 0; i < HopSize; i++) {
                specL[i] = new Complex(prevInL[i] * window[i], 0f);
                specR[i] = new Complex(prevInR[i] * window[i], 0f);
                int hi = HopSize + i;
                specL[hi] = new Complex(currInL[i] * window[hi], 0f);
                specR[hi] = new Complex(currInR[i] * window[hi], 0f);
            }

            specL.InPlaceFFT(fftCache);
            specR.InPlaceFFT(fftCache);

            double energySum = 0.0;
            double weightedPsi = 0.0;
            double doaX = 0.0;
            double doaZ = 0.0;

            for (int k = 0; k < SpectrumBins; k++) {
                Complex l = specL[k];
                Complex r = specR[k];
                float a = smoothAlpha[k];
                float oneMinusA = 1f - a;

                float instLL = l.Real * l.Real + l.Imaginary * l.Imaginary;
                float instRR = r.Real * r.Real + r.Imaginary * r.Imaginary;
                float instLRRe = l.Real * r.Real + l.Imaginary * r.Imaginary;
                float instLRIm = l.Imaginary * r.Real - l.Real * r.Imaginary;

                covLL[k] = oneMinusA * covLL[k] + a * instLL;
                covRR[k] = oneMinusA * covRR[k] + a * instRR;
                covLRRe[k] = oneMinusA * covLRRe[k] + a * instLRRe;
                covLRIm[k] = oneMinusA * covLRIm[k] + a * instLRIm;
            }

            // ERB-aligned local frequency-neighborhood covariance pooling (eliminates single-bin finite-sample bias)
            for (int k = 0; k < SpectrumBins; k++) {
                int hw = erbHalfWidth[k];
                if (hw <= 0) {
                    poolLL[k] = covLL[k];
                    poolRR[k] = covRR[k];
                    poolLRRe[k] = covLRRe[k];
                    poolLRIm[k] = covLRIm[k];
                } else {
                    int lo = k - hw;
                    if (lo < 1) {
                        lo = 1;
                    }
                    int hi = k + hw;
                    if (hi >= SpectrumBins) {
                        hi = SpectrumBins - 1;
                    }
                    float sLL = 0f, sRR = 0f, sRe = 0f, sIm = 0f;
                    int cnt = hi - lo + 1;
                    for (int j = lo; j <= hi; j++) {
                        sLL += covLL[j];
                        sRR += covRR[j];
                        sRe += covLRRe[j];
                        sIm += covLRIm[j];
                    }
                    float invCnt = 1f / cnt;
                    poolLL[k] = sLL * invCnt;
                    poolRR[k] = sRR * invCnt;
                    poolLRRe[k] = sRe * invCnt;
                    poolLRIm[k] = sIm * invCnt;
                }

                float pll = poolLL[k];
                float prr = poolRR[k];
                float re = poolLRRe[k];
                float im = poolLRIm[k];
                float totalP = pll + prr;
                if (k >= 1 && totalP > Epsilon) {
                    float diffP = pll - prr;
                    float eigDelta = MathF.Sqrt(diffP * diffP + 4f * (re * re + im * im));
                    float psi = 1f - eigDelta / totalP;
                    if (psi < 0f) {
                        psi = 0f;
                    } else if (psi > 1f) {
                        psi = 1f;
                    }
                    float pan = (prr - pll) / totalP; // -1 (L) .. +1 (R)
                    float denom = MathF.Sqrt(pll * prr) + Epsilon;
                    float gammaRe = re / denom;
                    float weight = totalP;

                    energySum += weight;
                    weightedPsi += weight * psi;

                    // Map direct energy to room DoA vector (+X right, +Z front, -Z rear for anti-phase cues)
                    float directWeight = weight * (1f - psi);
                    float azimuth = pan * (MathF.PI / 6f); // +-30 deg front stereo arc
                    float zSign = gammaRe < -0.25f ? -1f : 1f;
                    doaX += directWeight * MathF.Sin(azimuth);
                    doaZ += directWeight * zSign * MathF.Cos(azimuth);
                }
            }

            SoundfieldTelemetry telemetry = LatestTelemetry;
            if (energySum > 1e-10) {
                float meanPsi = (float)(weightedPsi / energySum);
                if (meanPsi < 0f) {
                    meanPsi = 0f;
                } else if (meanPsi > 1f) {
                    meanPsi = 1f;
                }
                telemetry.Diffuseness = meanPsi;
                telemetry.Coherence = 1f - meanPsi;
                Vector3 doa = new Vector3((float)doaX, 0f, (float)doaZ);
                float len = doa.Length();
                telemetry.DominantDoa = len > 1e-6f ? doa / len : new Vector3(0f, 0f, 1f);
            } else {
                telemetry.Diffuseness = 0f;
                telemetry.Coherence = 0f;
                telemetry.DominantDoa = Vector3.Zero;
            }
            LatestTelemetry = telemetry;
        }

        void SynthesizeBus(int busIndex) {
            Complex[] spec = outSpec[busIndex];
            spec.InPlaceIFFT(fftCache);
            float[] tail = olaTail[busIndex];
            float[] ready = readyOut[busIndex];
            for (int i = 0; i < HopSize; i++) {
                ready[i] = tail[i] + spec[i].Real * window[i];
                tail[i] = spec[HopSize + i].Real * window[HopSize + i];
            }
        }

        void FinishInputHop() {
            Array.Copy(currInL, 0, prevInL, 0, HopSize);
            Array.Copy(currInR, 0, prevInR, 0, HopSize);
            inFill = 0;
        }

        static void AssignHermitian(Complex[] spectrum, int bin, Complex value) {
            if (bin == 0 || bin == (FftSize >> 1)) {
                spectrum[bin] = new Complex(value.Real, 0f);
            } else {
                spectrum[bin] = value;
                spectrum[FftSize - bin] = new Complex(value.Real, -value.Imaginary);
            }
        }

        /// <summary>
        /// Smooth raised-cosine high-pass envelope: 0 below <paramref name="anchorHz"/>, ramping smoothly to 1 by <c>2 * anchorHz</c>.
        /// </summary>
        static float ComputeBassAnchorEnvelope(float freqHz, float anchorHz) {
            if (anchorHz <= 1f || freqHz >= anchorHz * 2f) {
                return 1f;
            }
            if (freqHz <= anchorHz) {
                return 0f;
            }
            float t = (freqHz - anchorHz) / anchorHz;
            return 0.5f * (1f - MathF.Cos(MathF.PI * t));
        }

        /// <inheritdoc/>
        public void Dispose() {
            fftCache.Dispose();
        }
    }
}
