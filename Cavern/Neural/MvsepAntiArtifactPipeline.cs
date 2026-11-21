using System;
using System.Threading;

using Cavern.Utilities;

namespace Cavern.Neural {
    /// <summary>
    /// Ensembling strategies curated from <c>ZFTurbo/Music-Source-Separation-Training</c> (<c>ensemble.py</c>)
    /// for combining outputs from multiple neural models or passes without introducing metallic bleed.
    /// </summary>
    public enum MvsepEnsembleMethod {
        /// <summary>Weighted time-domain waveform average (highest overall SDR baseline).</summary>
        AverageWave = 0,

        /// <summary>Conservative STFT complex-magnitude minimum (suppresses aggressive over-prediction bleed).</summary>
        MinFft = 1,

        /// <summary>Weighted complex STFT average for phase-aligned multi-model fusion.</summary>
        AverageFft = 2
    }

    /// <summary>
    /// Configuration parameters for the MVSEP-grade anti-artifact neural audio pipeline.
    /// </summary>
    public sealed class MvsepPipelineOptions {
        /// <summary>Minimum overlap factor (2x = 50% overlap).</summary>
        public const int MinOverlap = 2;

        /// <summary>Maximum overlap factor (8x = 87.5% overlap, studio reference).</summary>
        public const int MaxOverlap = 8;

        /// <summary>Default balanced overlap factor (4x = 75% overlap, MVSEP standard).</summary>
        public const int DefaultOverlap = 4;

        /// <summary>
        /// Model chunk length in samples per channel (e.g. 352800 = 8.0 s @ 44.1 kHz, or 131072 / 262144 for mobile models).
        /// </summary>
        public int ChunkSamples { get; set; } = 131072;

        /// <summary>
        /// Overlap factor in [2..8] (step = ChunkSamples / OverlapFactor). Higher values eliminate chunk-edge transients.
        /// </summary>
        public int OverlapFactor { get; set; } = DefaultOverlap;

        /// <summary>
        /// Fraction of the chunk length on each edge tapered by a raised-cosine Hann window (0.1..0.5; default 0.5 = full Hann).
        /// </summary>
        public float FadeFraction { get; set; } = 0.5f;

        /// <summary>
        /// Enables Test-Time Augmentation (L/R channel swap + polarity inversion averaging).
        /// </summary>
        public bool EnableTta { get; set; }

        /// <summary>
        /// Enables an additional half-hop time-shifted pass during TTA for ultra-clean studio extraction.
        /// </summary>
        public bool EnableShiftTta { get; set; }

        /// <summary>
        /// Enables STFT Wiener original-phase anchoring (<c>phase_fix</c>) to lock separated stems to the original recording's phase.
        /// </summary>
        public bool EnablePhaseFix { get; set; } = true;

        /// <summary>
        /// Wiener mask exponent for <see cref="EnablePhaseFix"/> (1.0 = amplitude ratio, 2.0 = classical Wiener power ratio).
        /// </summary>
        public float WienerExponent { get; set; } = 2.0f;

        /// <summary>
        /// Enforces exact sample-level energy conservation (<c>residual = mix - sum(stems)</c>) when enabled.
        /// </summary>
        public bool EnforceEnergyConservation { get; set; } = true;

        /// <summary>
        /// STFT window size used by <c>min_fft</c>, <c>avg_fft</c>, and <c>phase_fix</c> (default 2048).
        /// </summary>
        public int StftSize { get; set; } = 2048;

        /// <summary>
        /// Creates a sanitized copy with bounded numeric fields.
        /// </summary>
        public MvsepPipelineOptions CloneSanitized() {
            int overlap = OverlapFactor;
            if (overlap < MinOverlap) {
                overlap = MinOverlap;
            } else if (overlap > MaxOverlap) {
                overlap = MaxOverlap;
            }
            int chunk = ChunkSamples < 1024 ? 1024 : ChunkSamples;
            float fade = FadeFraction;
            if (fade < 0.05f) {
                fade = 0.05f;
            } else if (fade > 0.5f) {
                fade = 0.5f;
            }
            int fft = NormalizePowerOfTwo(StftSize, 512, 8192);
            float wiener = WienerExponent;
            if (wiener < 0.5f) {
                wiener = 0.5f;
            } else if (wiener > 4.0f) {
                wiener = 4.0f;
            }
            return new MvsepPipelineOptions {
                ChunkSamples = chunk,
                OverlapFactor = overlap,
                FadeFraction = fade,
                EnableTta = EnableTta,
                EnableShiftTta = EnableShiftTta,
                EnablePhaseFix = EnablePhaseFix,
                WienerExponent = wiener,
                EnforceEnergyConservation = EnforceEnergyConservation,
                StftSize = fft
            };
        }

        static int NormalizePowerOfTwo(int value, int min, int max) {
            int v = value;
            if (v < min) {
                return min;
            }
            if (v > max) {
                return max;
            }
            int p = min;
            while (p < v && p < max) {
                p <<= 1;
            }
            return p;
        }
    }

    /// <summary>
    /// Stereo planar audio container (<c>Left</c>, <c>Right</c>) used throughout the Slot 2B neural audio pipeline.
    /// </summary>
    public sealed class StereoStemBuffer {
        /// <summary>Stem identifier (e.g., "vocals", "lead_vocal", "backing_vocals", "drums", "bass", "other", "residual", "dry", "wet").</summary>
        public string Name { get; }

        /// <summary>Left channel PCM samples in [-1; +1].</summary>
        public float[] Left { get; }

        /// <summary>Right channel PCM samples in [-1; +1].</summary>
        public float[] Right { get; }

        /// <summary>Number of sample frames per channel.</summary>
        public int Length {
            get { return Left.Length; }
        }

        /// <summary>Creates a new stereo stem buffer with zero-initialized arrays of <paramref name="length"/> samples.</summary>
        public StereoStemBuffer(string name, int length) {
            if (length < 0) {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            Name = name ?? string.Empty;
            Left = new float[length];
            Right = new float[length];
        }

        /// <summary>Wraps existing planar stereo buffers.</summary>
        public StereoStemBuffer(string name, float[] left, float[] right) {
            if (left == null) {
                throw new ArgumentNullException(nameof(left));
            }
            if (right == null) {
                throw new ArgumentNullException(nameof(right));
            }
            if (left.Length != right.Length) {
                throw new ArgumentException("Left and Right buffers must have identical length.");
            }
            Name = name ?? string.Empty;
            Left = left;
            Right = right;
        }
    }

    /// <summary>
    /// Result of dry/wet dereverb decomposition (<see cref="MvsepAntiArtifactPipeline.ComputeDryWetDecomposition"/>),
    /// exposing exact wet reverberation residual and physical Direct-to-Reverberant Ratio (DRR) for W11 Vocal Mix-Match.
    /// </summary>
    public sealed class DereverbDecompositionResult {
        /// <summary>Dry (anechoic) vocal stem returned by the dereverb model.</summary>
        public StereoStemBuffer DryVocal { get; }

        /// <summary>Exact reverberant wet residual (<c>originalVocal - dryVocal</c>).</summary>
        public StereoStemBuffer WetReverb { get; }

        /// <summary>Measured physical Direct-to-Reverberant Ratio in dB (<c>10 * log10(E_dry / E_wet)</c>).</summary>
        public float DirectToReverberantRatioDb { get; }

        /// <summary>Creates a dereverb decomposition result.</summary>
        public DereverbDecompositionResult(StereoStemBuffer dryVocal, StereoStemBuffer wetReverb, float drrDb) {
            DryVocal = dryVocal;
            WetReverb = wetReverb;
            DirectToReverberantRatioDb = drrDb;
        }
    }

    /// <summary>
    /// Delegate executing a single neural chunk pass on planar stereo buffers <c>(inL, inR)</c> of length <c>chunkSamples</c>,
    /// returning <c>stemCount</c> separated stereo stems of identical length.
    /// </summary>
    public delegate StereoStemBuffer[] NeuralChunkEvaluator(float[] chunkLeft, float[] chunkRight, int chunkSamples);

    /// <summary>
    /// Mandatory MVSEP-grade anti-artifact signal processing pipeline for ALL Slot 2B neural audio models
    /// (SCNet, Mel-Band/BS-RoFormer, Karaoke, Dereverb, Debleed, Apollo, AP-BWE/FlashSR).
    /// Curated from <c>ZFTurbo/Music-Source-Separation-Training</c> and <c>MVSEP-MDX23</c>:
    /// <list type="bullet">
    ///   <item>Reflect-padded raised-cosine / Hann windowed overlap-add (<c>2x–8x</c>) with exact per-sample weight normalization (1.000000x identity).</item>
    ///   <item>Test-Time Augmentation (L/R swap + polarity inversion + optional half-hop shift averaging).</item>
    ///   <item>Multi-model ensembling (<c>avg_wave</c>, conservative <c>min_fft</c>, and <c>avg_fft</c>).</item>
    ///   <item>STFT Wiener original-phase anchoring (<c>phase_fix</c>) eliminating vocoder/metallic phase smear.</item>
    ///   <item>Exact time-domain energy conservation (<c>residual = mix - sum(stems)</c>).</item>
    /// </list>
    /// </summary>
    public static class MvsepAntiArtifactPipeline {
        const float Epsilon = 1e-12f;

        /// <summary>
        /// Builds a strictly positive raised-cosine (Tukey / Hann) chunk weighting window of length <paramref name="chunkSize"/>.
        /// Edges taper smoothly with <c>0.5 * (1 - cos(pi * (n + 0.5) / fadeLen))</c> so there are zero boundary discontinuities
        /// and every sample retains positive weight (<c>&gt; 0</c>) for exact overlap-add normalization.
        /// </summary>
        public static float[] CreateRaisedCosineChunkWindow(int chunkSize, float fadeFraction = 0.5f) {
            if (chunkSize <= 0) {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }
            if (fadeFraction < 0.05f) {
                fadeFraction = 0.05f;
            } else if (fadeFraction > 0.5f) {
                fadeFraction = 0.5f;
            }

            float[] window = new float[chunkSize];
            int fadeLen = (int)Math.Round(chunkSize * fadeFraction);
            if (fadeLen < 1) {
                fadeLen = 1;
            }
            if (fadeLen * 2 > chunkSize) {
                fadeLen = chunkSize / 2;
            }

            for (int i = 0; i < chunkSize; i++) {
                window[i] = 1f;
            }
            for (int i = 0; i < fadeLen; i++) {
                // Use (i + 0.5) / fadeLen so edge samples are strictly > 0 even at overlap=2
                float w = 0.5f * (1f - MathF.Cos(MathF.PI * (i + 0.5f) / fadeLen));
                window[i] = w;
                window[chunkSize - 1 - i] = w;
            }
            return window;
        }

        /// <summary>
        /// Processes a full stereo track through <paramref name="evaluator"/> using reflect-padded raised-cosine overlap-add
        /// (<paramref name="options"/>.<see cref="MvsepPipelineOptions.OverlapFactor"/> in 2x..8x), optional TTA, optional
        /// Wiener <c>phase_fix</c>, and optional exact residual energy conservation.
        /// </summary>
        public static StereoStemBuffer[] ProcessTrack(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkEvaluator evaluator,
            MvsepPipelineOptions options,
            Action<double> onProgress = null,
            CancellationToken token = default) {
            if (mixLeft == null) {
                throw new ArgumentNullException(nameof(mixLeft));
            }
            if (mixRight == null) {
                throw new ArgumentNullException(nameof(mixRight));
            }
            if (mixLeft.Length != mixRight.Length) {
                throw new ArgumentException("Mix channels must have identical lengths.");
            }
            if (evaluator == null) {
                throw new ArgumentNullException(nameof(evaluator));
            }

            MvsepPipelineOptions opt = (options ?? new MvsepPipelineOptions()).CloneSanitized();
            int totalSamples = mixLeft.Length;
            if (totalSamples == 0) {
                return Array.Empty<StereoStemBuffer>();
            }

            StereoStemBuffer[] stems;
            if (!opt.EnableTta) {
                stems = RunSingleChunkedPass(mixLeft, mixRight, evaluator, opt, 0.0, 0.90, onProgress, token);
            } else {
                stems = RunTtaPasses(mixLeft, mixRight, evaluator, opt, onProgress, token);
            }

            if (stems == null || stems.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }

            if (opt.EnablePhaseFix) {
                ApplyPhaseFixWiener(mixLeft, mixRight, stems, opt.StftSize, opt.WienerExponent, token);
            }

            if (opt.EnforceEnergyConservation) {
                stems = AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
            }

            onProgress?.Invoke(1.0);
            return stems;
        }

        /// <summary>
        /// Ensembles multiple candidate stereo waveforms for the same stem using MVSEP <c>avg_wave</c>,
        /// conservative <c>min_fft</c>, or phase-aligned <c>avg_fft</c>.
        /// </summary>
        public static StereoStemBuffer EnsembleStems(
            StereoStemBuffer[] candidates,
            float[] weights,
            MvsepEnsembleMethod method,
            int stftSize = 2048,
            string outputName = null,
            CancellationToken token = default) {
            if (candidates == null || candidates.Length == 0) {
                throw new ArgumentException("At least one candidate stem is required for ensembling.", nameof(candidates));
            }

            string stemName = outputName ?? candidates[0].Name;
            int length = candidates[0].Length;
            for (int c = 1; c < candidates.Length; c++) {
                if (candidates[c] == null || candidates[c].Length != length) {
                    throw new ArgumentException("All ensemble candidates must be non-null and have equal length.", nameof(candidates));
                }
            }

            float[] normWeights = NormalizeWeights(candidates.Length, weights);
            if (candidates.Length == 1) {
                float[] outL = new float[length];
                float[] outR = new float[length];
                Array.Copy(candidates[0].Left, outL, length);
                Array.Copy(candidates[0].Right, outR, length);
                return new StereoStemBuffer(stemName, outL, outR);
            }

            if (method == MvsepEnsembleMethod.AverageWave) {
                return EnsembleWaveAverage(candidates, normWeights, stemName, token);
            }
            return EnsembleFrequencyDomain(candidates, normWeights, method, stftSize, stemName, token);
        }

        /// <summary>
        /// Anchors the magnitude spectra of <paramref name="stems"/> onto the exact phase of the original mix
        /// (<paramref name="mixLeft"/>, <paramref name="mixRight"/>) via multichannel ratio/Wiener masking (<c>phase_fix</c>).
        /// Guarantees zero phase distortion relative to the original mix and exact partition of unity across all stems.
        /// </summary>
        public static void ApplyPhaseFixWiener(
            float[] mixLeft,
            float[] mixRight,
            StereoStemBuffer[] stems,
            int stftSize = 2048,
            float exponent = 2.0f,
            CancellationToken token = default) {
            if (mixLeft == null || mixRight == null || stems == null || stems.Length == 0) {
                return;
            }
            int length = mixLeft.Length;
            if (length == 0) {
                return;
            }

            int fftSize = NormalizeFftSize(stftSize);
            int hopSize = fftSize >> 1;
            int bins = (fftSize >> 1) + 1;
            float[] window = CreateSqrtHannWindow(fftSize);
            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(fftSize);

            try {
                // Process Left and Right channels independently using identical COLA sqrt(Hann) WOLA
                for (int ch = 0; ch < 2; ch++) {
                    float[] mix = ch == 0 ? mixLeft : mixRight;
                    float[][] stemCh = new float[stems.Length][];
                    float[][] outCh = new float[stems.Length][];
                    for (int s = 0; s < stems.Length; s++) {
                        stemCh[s] = ch == 0 ? stems[s].Left : stems[s].Right;
                        outCh[s] = new float[length];
                    }

                    Complex[] mixSpec = new Complex[fftSize];
                    Complex[][] stemSpec = new Complex[stems.Length][];
                    for (int s = 0; s < stems.Length; s++) {
                        stemSpec[s] = new Complex[fftSize];
                    }
                    float[] weightSum = new float[length];
                    float[] powerScratch = new float[stems.Length];

                    int numFrames = (length + hopSize - 1) / hopSize + 2;
                    for (int frame = -1; frame < numFrames; frame++) {
                        token.ThrowIfCancellationRequested();
                        int start = frame * hopSize;
                        if (start >= length || start + fftSize <= 0) {
                            continue;
                        }

                        FillWindowedFrame(mix, start, fftSize, window, mixSpec);
                        mixSpec.InPlaceFFT(fft);

                        for (int s = 0; s < stems.Length; s++) {
                            FillWindowedFrame(stemCh[s], start, fftSize, window, stemSpec[s]);
                            stemSpec[s].InPlaceFFT(fft);
                        }

                        for (int k = 0; k < bins; k++) {
                            float sumPow = 0f;
                            for (int s = 0; s < stems.Length; s++) {
                                float mag = stemSpec[s][k].Magnitude;
                                float p = exponent == 2.0f ? (mag * mag) : (exponent == 1.0f ? mag : MathF.Pow(mag, exponent));
                                powerScratch[s] = p;
                                sumPow += p;
                            }

                            Complex mVal = mixSpec[k];
                            float invDenom = sumPow > Epsilon ? (1f / (sumPow + Epsilon)) : (1f / stems.Length);
                            for (int s = 0; s < stems.Length; s++) {
                                float mask = sumPow > Epsilon ? (powerScratch[s] * invDenom) : invDenom;
                                Complex anchored = new Complex(mVal.Real * mask, mVal.Imaginary * mask);
                                stemSpec[s][k] = anchored;
                                if (k > 0 && k < bins - 1) {
                                    stemSpec[s][fftSize - k] = new Complex(anchored.Real, -anchored.Imaginary);
                                }
                            }
                        }

                        for (int s = 0; s < stems.Length; s++) {
                            stemSpec[s].InPlaceIFFT(fft);
                            AccumulateWindowedFrame(stemSpec[s], start, fftSize, window, outCh[s], s == 0 ? weightSum : null);
                        }
                    }

                    for (int n = 0; n < length; n++) {
                        float w = weightSum[n];
                        float invW = w > Epsilon ? (1f / w) : 1f;
                        for (int s = 0; s < stems.Length; s++) {
                            stemCh[s][n] = outCh[s][n] * invW;
                        }
                    }
                }
            } finally {
                fft.Dispose();
            }
        }

        /// <summary>
        /// Enforces exact sample-level energy conservation: computes <c>residual = mix - sum(stems)</c> so that
        /// <c>sum(stems) + residual == mix</c> to floating-point precision.
        /// </summary>
        public static StereoStemBuffer ComputeConservedResidual(
            float[] mixLeft,
            float[] mixRight,
            StereoStemBuffer[] stems,
            string residualName = "residual") {
            if (mixLeft == null) {
                throw new ArgumentNullException(nameof(mixLeft));
            }
            if (mixRight == null) {
                throw new ArgumentNullException(nameof(mixRight));
            }
            int length = mixLeft.Length;
            float[] resL = new float[length];
            float[] resR = new float[length];

            for (int n = 0; n < length; n++) {
                double sumL = 0.0;
                double sumR = 0.0;
                if (stems != null) {
                    for (int s = 0; s < stems.Length; s++) {
                        sumL += stems[s].Left[n];
                        sumR += stems[s].Right[n];
                    }
                }
                resL[n] = (float)(mixLeft[n] - sumL);
                resR[n] = (float)(mixRight[n] - sumR);
            }
            return new StereoStemBuffer(residualName ?? "residual", resL, resR);
        }

        /// <summary>
        /// Computes exact wet reverberation residual (<c>wet = original - dry</c>) and physical Direct-to-Reverberant Ratio
        /// (<c>DRR_dB = 10 * log10(E_dry / E_wet)</c>) from a Dereverb checkpoint output (used by W11 Vocal Mix-Match).
        /// </summary>
        public static DereverbDecompositionResult ComputeDryWetDecomposition(
            StereoStemBuffer originalVocal,
            StereoStemBuffer dryVocal) {
            if (originalVocal == null) {
                throw new ArgumentNullException(nameof(originalVocal));
            }
            if (dryVocal == null) {
                throw new ArgumentNullException(nameof(dryVocal));
            }
            if (originalVocal.Length != dryVocal.Length) {
                throw new ArgumentException("Original and dry vocal buffers must have identical length.");
            }

            int nLen = originalVocal.Length;
            float[] wetL = new float[nLen];
            float[] wetR = new float[nLen];
            double energyDry = 0.0;
            double energyWet = 0.0;

            for (int i = 0; i < nLen; i++) {
                float dL = dryVocal.Left[i];
                float dR = dryVocal.Right[i];
                float wL = originalVocal.Left[i] - dL;
                float wR = originalVocal.Right[i] - dR;
                wetL[i] = wL;
                wetR[i] = wR;
                energyDry += (double)dL * dL + (double)dR * dR;
                energyWet += (double)wL * wL + (double)wR * wR;
            }

            float drrDb;
            if (energyWet <= 1e-18 && energyDry <= 1e-18) {
                drrDb = 0f;
            } else if (energyWet <= 1e-18) {
                drrDb = 40f;
            } else if (energyDry <= 1e-18) {
                drrDb = -40f;
            } else {
                drrDb = (float)(10.0 * Math.Log10(energyDry / energyWet));
                if (drrDb < -40f) {
                    drrDb = -40f;
                } else if (drrDb > 40f) {
                    drrDb = 40f;
                }
            }

            return new DereverbDecompositionResult(
                dryVocal,
                new StereoStemBuffer("wet_reverb", wetL, wetR),
                drrDb);
        }

        static StereoStemBuffer[] RunTtaPasses(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkEvaluator evaluator,
            MvsepPipelineOptions opt,
            Action<double> onProgress,
            CancellationToken token) {
            int totalPasses = opt.EnableShiftTta ? 4 : 3;
            int length = mixLeft.Length;
            double passWeight = 0.90 / totalPasses;

            // Pass 0: Original (L, R)
            StereoStemBuffer[] pass0 = RunSingleChunkedPass(mixLeft, mixRight, evaluator, opt, 0.0, passWeight, onProgress, token);
            if (pass0 == null || pass0.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }

            // Pass 1: Channel Swap (R, L) -> swap outputs back to (L, R)
            StereoStemBuffer[] pass1 = RunSingleChunkedPass(mixRight, mixLeft, evaluator, opt, passWeight, passWeight * 2.0, onProgress, token);

            // Pass 2: Polarity Inversion (-L, -R) -> invert outputs back
            float[] invL = new float[length];
            float[] invR = new float[length];
            for (int i = 0; i < length; i++) {
                invL[i] = -mixLeft[i];
                invR[i] = -mixRight[i];
            }
            StereoStemBuffer[] pass2 = RunSingleChunkedPass(invL, invR, evaluator, opt, passWeight * 2.0, passWeight * 3.0, onProgress, token);

            // Optional Pass 3: Half-hop time shift (reflect-padded) -> unshift outputs back
            StereoStemBuffer[] pass3 = null;
            int shift = Math.Max(1, (opt.ChunkSamples / opt.OverlapFactor) / 2);
            if (opt.EnableShiftTta) {
                float[] shiftL = new float[length + shift];
                float[] shiftR = new float[length + shift];
                for (int i = 0; i < shift; i++) {
                    int src = ReflectIndex(shift - 1 - i, length);
                    shiftL[i] = mixLeft[src];
                    shiftR[i] = mixRight[src];
                }
                Array.Copy(mixLeft, 0, shiftL, shift, length);
                Array.Copy(mixRight, 0, shiftR, shift, length);
                pass3 = RunSingleChunkedPass(shiftL, shiftR, evaluator, opt, passWeight * 3.0, 0.90, onProgress, token);
            }

            int stemCount = pass0.Length;
            float invCount = 1f / totalPasses;
            StereoStemBuffer[] averaged = new StereoStemBuffer[stemCount];
            for (int s = 0; s < stemCount; s++) {
                float[] outL = new float[length];
                float[] outR = new float[length];
                for (int n = 0; n < length; n++) {
                    // pass1 ran on (R, L), so pass1[s].Right is original Left and pass1[s].Left is original Right
                    float sumL = pass0[s].Left[n] + pass1[s].Right[n] - pass2[s].Left[n];
                    float sumR = pass0[s].Right[n] + pass1[s].Left[n] - pass2[s].Right[n];
                    if (pass3 != null) {
                        sumL += pass3[s].Left[n + shift];
                        sumR += pass3[s].Right[n + shift];
                    }
                    outL[n] = sumL * invCount;
                    outR[n] = sumR * invCount;
                }
                averaged[s] = new StereoStemBuffer(pass0[s].Name, outL, outR);
            }
            return averaged;
        }

        static StereoStemBuffer[] RunSingleChunkedPass(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkEvaluator evaluator,
            MvsepPipelineOptions opt,
            double progressStart,
            double progressEnd,
            Action<double> onProgress,
            CancellationToken token) {
            int length = mixLeft.Length;
            int chunkSize = opt.ChunkSamples;
            int step = Math.Max(1, chunkSize / opt.OverlapFactor);
            int pad = step;
            int paddedLen = length + pad * 2;
            if (paddedLen < chunkSize) {
                paddedLen = chunkSize;
            }

            // Build reflect-padded input buffers to eliminate zero-padding edge clicks
            float[] padL = new float[paddedLen];
            float[] padR = new float[paddedLen];
            for (int i = 0; i < paddedLen; i++) {
                int srcIdx = ReflectIndex(i - pad, length);
                padL[i] = mixLeft[srcIdx];
                padR[i] = mixRight[srcIdx];
            }

            float[] chunkWindow = CreateRaisedCosineChunkWindow(chunkSize, opt.FadeFraction);
            float[] chunkInL = new float[chunkSize];
            float[] chunkInR = new float[chunkSize];
            float[] weightSum = new float[paddedLen];

            int totalChunks = ((paddedLen - chunkSize) + step - 1) / step + 1;
            if (totalChunks < 1) {
                totalChunks = 1;
            }

            float[][] accumL = null;
            float[][] accumR = null;
            string[] stemNames = null;

            for (int c = 0; c < totalChunks; c++) {
                token.ThrowIfCancellationRequested();
                int start = c * step;
                if (start + chunkSize > paddedLen) {
                    start = paddedLen - chunkSize;
                }

                Array.Copy(padL, start, chunkInL, 0, chunkSize);
                Array.Copy(padR, start, chunkInR, 0, chunkSize);

                StereoStemBuffer[] chunkOut = evaluator(chunkInL, chunkInR, chunkSize);
                if (chunkOut == null || chunkOut.Length == 0) {
                    return Array.Empty<StereoStemBuffer>();
                }

                if (accumL == null) {
                    int stems = chunkOut.Length;
                    accumL = new float[stems][];
                    accumR = new float[stems][];
                    stemNames = new string[stems];
                    for (int s = 0; s < stems; s++) {
                        accumL[s] = new float[paddedLen];
                        accumR[s] = new float[paddedLen];
                        stemNames[s] = string.IsNullOrEmpty(chunkOut[s].Name) ? ("stem_" + s) : chunkOut[s].Name;
                    }
                }

                for (int i = 0; i < chunkSize; i++) {
                    float w = chunkWindow[i];
                    int dst = start + i;
                    weightSum[dst] += w;
                    for (int s = 0; s < accumL.Length; s++) {
                        accumL[s][dst] += chunkOut[s].Left[i] * w;
                        accumR[s][dst] += chunkOut[s].Right[i] * w;
                    }
                }

                if (onProgress != null) {
                    double frac = (c + 1.0) / totalChunks;
                    onProgress(progressStart + (progressEnd - progressStart) * frac);
                }

                if (start + chunkSize >= paddedLen) {
                    break;
                }
            }

            StereoStemBuffer[] result = new StereoStemBuffer[accumL.Length];
            for (int s = 0; s < accumL.Length; s++) {
                float[] outL = new float[length];
                float[] outR = new float[length];
                for (int n = 0; n < length; n++) {
                    int src = n + pad;
                    float w = weightSum[src];
                    float invW = w > Epsilon ? (1f / w) : 1f;
                    outL[n] = accumL[s][src] * invW;
                    outR[n] = accumR[s][src] * invW;
                }
                result[s] = new StereoStemBuffer(stemNames[s], outL, outR);
            }
            return result;
        }

        static StereoStemBuffer[] AppendOrUpdateConservedResidual(
            float[] mixLeft,
            float[] mixRight,
            StereoStemBuffer[] stems) {
            // If the model already produced a "residual" or "other" stem as the last entry when multi-stem,
            // compute explicit residual so sum(all returned stems) == mix identically.
            if (stems.Length == 1) {
                StereoStemBuffer residual = ComputeConservedResidual(mixLeft, mixRight, stems, "instrumental");
                return new StereoStemBuffer[] { stems[0], residual };
            }

            int residualIdx = -1;
            for (int i = 0; i < stems.Length; i++) {
                if (string.Equals(stems[i].Name, "residual", StringComparison.OrdinalIgnoreCase)) {
                    residualIdx = i;
                    break;
                }
            }

            if (residualIdx >= 0) {
                StereoStemBuffer[] nonRes = new StereoStemBuffer[stems.Length - 1];
                int k = 0;
                for (int i = 0; i < stems.Length; i++) {
                    if (i != residualIdx) {
                        nonRes[k++] = stems[i];
                    }
                }
                stems[residualIdx] = ComputeConservedResidual(mixLeft, mixRight, nonRes, stems[residualIdx].Name);
                return stems;
            }

            // Compute exact residual so sum(stems) + residual == mix without altering primary separated stems
            StereoStemBuffer computedResidual = ComputeConservedResidual(mixLeft, mixRight, stems, "residual");
            StereoStemBuffer[] extended = new StereoStemBuffer[stems.Length + 1];
            Array.Copy(stems, extended, stems.Length);
            extended[stems.Length] = computedResidual;
            return extended;
        }

        static StereoStemBuffer EnsembleWaveAverage(
            StereoStemBuffer[] candidates,
            float[] normWeights,
            string stemName,
            CancellationToken token) {
            int length = candidates[0].Length;
            float[] outL = new float[length];
            float[] outR = new float[length];
            for (int n = 0; n < length; n++) {
                if ((n & 8191) == 0) {
                    token.ThrowIfCancellationRequested();
                }
                double sumL = 0.0;
                double sumR = 0.0;
                for (int c = 0; c < candidates.Length; c++) {
                    double w = normWeights[c];
                    sumL += candidates[c].Left[n] * w;
                    sumR += candidates[c].Right[n] * w;
                }
                outL[n] = (float)sumL;
                outR[n] = (float)sumR;
            }
            return new StereoStemBuffer(stemName, outL, outR);
        }

        static StereoStemBuffer EnsembleFrequencyDomain(
            StereoStemBuffer[] candidates,
            float[] normWeights,
            MvsepEnsembleMethod method,
            int stftSize,
            string stemName,
            CancellationToken token) {
            int length = candidates[0].Length;
            int fftSize = NormalizeFftSize(stftSize);
            int hopSize = fftSize >> 1;
            int bins = (fftSize >> 1) + 1;
            float[] window = CreateSqrtHannWindow(fftSize);
            ThreadSafeFFTCache fft = new ThreadSafeFFTCache(fftSize);

            float[] outL = new float[length];
            float[] outR = new float[length];

            try {
                Complex[][] specs = new Complex[candidates.Length][];
                for (int c = 0; c < candidates.Length; c++) {
                    specs[c] = new Complex[fftSize];
                }
                Complex[] fused = new Complex[fftSize];
                float[] weightSum = new float[length];

                for (int ch = 0; ch < 2; ch++) {
                    float[] dst = ch == 0 ? outL : outR;
                    Array.Clear(weightSum, 0, weightSum.Length);

                    int numFrames = (length + hopSize - 1) / hopSize + 2;
                    for (int frame = -1; frame < numFrames; frame++) {
                        token.ThrowIfCancellationRequested();
                        int start = frame * hopSize;
                        if (start >= length || start + fftSize <= 0) {
                            continue;
                        }

                        for (int c = 0; c < candidates.Length; c++) {
                            float[] src = ch == 0 ? candidates[c].Left : candidates[c].Right;
                            FillWindowedFrame(src, start, fftSize, window, specs[c]);
                            specs[c].InPlaceFFT(fft);
                        }

                        for (int k = 0; k < bins; k++) {
                            Complex val;
                            if (method == MvsepEnsembleMethod.MinFft) {
                                // MVSEP conservative min_fft: select candidate bin with smallest magnitude
                                // (matches ZFTurbo ensemble.py min_fft to suppress over-aggressive bleed)
                                int bestIdx = 0;
                                float bestMagSq = specs[0][k].Real * specs[0][k].Real + specs[0][k].Imaginary * specs[0][k].Imaginary;
                                for (int c = 1; c < candidates.Length; c++) {
                                    float magSq = specs[c][k].Real * specs[c][k].Real + specs[c][k].Imaginary * specs[c][k].Imaginary;
                                    if (magSq < bestMagSq) {
                                        bestMagSq = magSq;
                                        bestIdx = c;
                                    }
                                }
                                val = specs[bestIdx][k];
                            } else {
                                // Weighted complex STFT average (avg_fft)
                                double re = 0.0;
                                double im = 0.0;
                                for (int c = 0; c < candidates.Length; c++) {
                                    double w = normWeights[c];
                                    re += specs[c][k].Real * w;
                                    im += specs[c][k].Imaginary * w;
                                }
                                val = new Complex((float)re, (float)im);
                            }

                            fused[k] = val;
                            if (k > 0 && k < bins - 1) {
                                fused[fftSize - k] = new Complex(val.Real, -val.Imaginary);
                            }
                        }

                        fused.InPlaceIFFT(fft);
                        AccumulateWindowedFrame(fused, start, fftSize, window, dst, weightSum);
                    }

                    for (int n = 0; n < length; n++) {
                        float w = weightSum[n];
                        if (w > Epsilon) {
                            dst[n] /= w;
                        }
                    }
                }
            } finally {
                fft.Dispose();
            }

            return new StereoStemBuffer(stemName, outL, outR);
        }

        static float[] CreateSqrtHannWindow(int fftSize) {
            float[] w = new float[fftSize];
            for (int n = 0; n < fftSize; n++) {
                w[n] = MathF.Sin(MathF.PI * (n + 0.5f) / fftSize);
            }
            return w;
        }

        static void FillWindowedFrame(float[] src, int start, int fftSize, float[] window, Complex[] dst) {
            int len = src.Length;
            for (int n = 0; n < fftSize; n++) {
                int idx = start + n;
                float sample = (idx >= 0 && idx < len) ? src[idx] : 0f;
                dst[n] = new Complex(sample * window[n], 0f);
            }
        }

        static void AccumulateWindowedFrame(
            Complex[] timeDomain,
            int start,
            int fftSize,
            float[] window,
            float[] dst,
            float[] weightSum) {
            int len = dst.Length;
            for (int n = 0; n < fftSize; n++) {
                int idx = start + n;
                if (idx >= 0 && idx < len) {
                    float w = window[n];
                    dst[idx] += timeDomain[n].Real * w;
                    if (weightSum != null) {
                        weightSum[idx] += w * w;
                    }
                }
            }
        }

        static int ReflectIndex(int idx, int length) {
            if (length <= 1) {
                return 0;
            }
            while (idx < 0 || idx >= length) {
                if (idx < 0) {
                    idx = -idx;
                }
                if (idx >= length) {
                    idx = (length - 1) * 2 - idx;
                }
            }
            return idx;
        }

        static int NormalizeFftSize(int size) {
            if (size < 512) {
                return 512;
            }
            if (size > 8192) {
                return 8192;
            }
            int p = 512;
            while (p < size && p < 8192) {
                p <<= 1;
            }
            return p;
        }

        static float[] NormalizeWeights(int count, float[] weights) {
            float[] norm = new float[count];
            double sum = 0.0;
            if (weights != null && weights.Length >= count) {
                for (int i = 0; i < count; i++) {
                    float w = weights[i] > 0f ? weights[i] : 0f;
                    norm[i] = w;
                    sum += w;
                }
            }
            if (sum <= 1e-12) {
                float eq = 1f / count;
                for (int i = 0; i < count; i++) {
                    norm[i] = eq;
                }
            } else {
                float inv = (float)(1.0 / sum);
                for (int i = 0; i < count; i++) {
                    norm[i] *= inv;
                }
            }
            return norm;
        }
    }
}
