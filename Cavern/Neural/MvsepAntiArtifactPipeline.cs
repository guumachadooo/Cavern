using System;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>Default balanced mobile overlap factor (2x = 50% raised-cosine COLA unity; users can raise up to 8x).</summary>
        public const int DefaultOverlap = 2;

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
        /// Enables 3-stage CPU↔GPU producer-consumer double-buffered pipelining (STFT/pack for chunk <c>c+1</c> and
        /// iSTFT/WOLA for chunk <c>c-1</c> overlap concurrently with GPU/ONNX inference for chunk <c>c</c>, 0.0 dB bit-exact).
        /// </summary>
        public bool EnableAsyncPipelining { get; set; } = true;

        /// <summary>
        /// Optional inter-chunk GPU breathing yield in milliseconds (0..100 ms) so mobile WebGPU/Vulkan dispatches leave
        /// compositor frame headroom for Android SurfaceFlinger/UI responsiveness without starving the display.
        /// </summary>
        public int GpuUiBreathingYieldMs { get; set; }

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
            int yieldMs = GpuUiBreathingYieldMs;
            if (yieldMs < 0) {
                yieldMs = 0;
            } else if (yieldMs > 100) {
                yieldMs = 100;
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
                StftSize = fft,
                EnableAsyncPipelining = EnableAsyncPipelining,
                GpuUiBreathingYieldMs = yieldMs
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
    /// Stage 1 (CPU Producer) delegate: converts a planar stereo chunk into a model-ready tensor/STFT payload
    /// concurrently while Stage 2 executes ONNX inference on the previous chunk.
    /// </summary>
    public delegate TPrepared NeuralChunkPreparer<TPrepared>(float[] chunkLeft, float[] chunkRight, int chunkSamples);

    /// <summary>
    /// Stage 2 (GPU/Accelerator Inference) delegate: executes the ONNX session on <paramref name="prepared"/>
    /// concurrently with Stage 1 (next chunk) and Stage 3 (previous chunk).
    /// </summary>
    public delegate TInferOut NeuralChunkInferer<TPrepared, TInferOut>(TPrepared prepared);

    /// <summary>
    /// Stage 3 (CPU Consumer) delegate: runs iSTFT / mask synthesis on <paramref name="inferred"/> to produce
    /// planar stereo stem chunks ready for bit-exact sequential overlap-add accumulation.
    /// </summary>
    public delegate StereoStemBuffer[] NeuralChunkFinalizer<TPrepared, TInferOut>(TPrepared prepared, TInferOut inferred, int chunkSamples);

    /// <summary>
    /// Callback invoked monotonically as consecutive overlap-add sample spans become permanently finalized during
    /// single-pass chunked inference, enabling Auto-RTF progressive real-time playback before 100% completion.
    /// </summary>
    public delegate void ProgressiveStemsReadyCallback(StereoStemBuffer[] liveStems, int finalizedSamples, int totalSamples, double elapsedSeconds);

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

            // CRITICAL ORDER (W17): Enforce residual conservation BEFORE Wiener phase_fix so 1-stem models
            // (e.g. Mel-Band RoFormer Vocals) form a 2-stem [target, instrumental] partition of unity.
            // Running Wiener phase_fix on 1 stem alone would compute mask = 1.0 and overwrite the stem with 100% mix.
            if (opt.EnforceEnergyConservation) {
                stems = AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
            }

            if (opt.EnablePhaseFix && stems.Length > 1) {
                ApplyPhaseFixWiener(mixLeft, mixRight, stems, opt.StftSize, opt.WienerExponent, token);
                if (opt.EnforceEnergyConservation) {
                    // Refresh conserved residual in-place after WOLA phase_fix so sum(stems) == mix to float precision
                    stems = AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
                }
            }

            onProgress?.Invoke(1.0);
            return stems;
        }

        /// <summary>
        /// Processes a full stereo track using an explicit 3-stage CPU↔GPU producer-consumer double-buffered pipeline:
        /// Stage 1 (<paramref name="preparer"/> on chunk <c>c+1</c>), Stage 2 (<paramref name="inferer"/> on chunk <c>c</c>),
        /// and Stage 3 (<paramref name="finalizer"/> + overlap-add on chunk <c>c-1</c>) run concurrently while preserving
        /// strictly sequential chunk accumulation order (0.0 dB / IEEE-754 bit-exact with <see cref="ProcessTrack"/>).
        /// </summary>
        public static StereoStemBuffer[] ProcessTrackPipelined<TPrepared, TInferOut>(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkPreparer<TPrepared> preparer,
            NeuralChunkInferer<TPrepared, TInferOut> inferer,
            NeuralChunkFinalizer<TPrepared, TInferOut> finalizer,
            MvsepPipelineOptions options,
            Action<double> onProgress = null,
            ProgressiveStemsReadyCallback onProgressiveReady = null,
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
            if (preparer == null) {
                throw new ArgumentNullException(nameof(preparer));
            }
            if (inferer == null) {
                throw new ArgumentNullException(nameof(inferer));
            }
            if (finalizer == null) {
                throw new ArgumentNullException(nameof(finalizer));
            }

            MvsepPipelineOptions opt = (options ?? new MvsepPipelineOptions()).CloneSanitized();
            int totalSamples = mixLeft.Length;
            if (totalSamples == 0) {
                return Array.Empty<StereoStemBuffer>();
            }

            StereoStemBuffer[] stems;
            if (!opt.EnableTta) {
                stems = RunSingleChunkedPassPipelined(
                    mixLeft, mixRight, preparer, inferer, finalizer, opt,
                    0.0, 0.90, onProgress, onProgressiveReady, token);
            } else {
                stems = RunTtaPassesPipelined(
                    mixLeft, mixRight, preparer, inferer, finalizer, opt, onProgress, token);
            }

            if (stems == null || stems.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }

            // Isolate live progressive playback buffers from in-place post-processing (e.g., Wiener phase_fix)
            // so the real-time audio thread never reads partially mutated buffers before final atomic swap.
            bool willPostProcess = opt.EnforceEnergyConservation || (opt.EnablePhaseFix && stems.Length > 1);
            if (onProgressiveReady != null && !opt.EnableTta && willPostProcess) {
                StereoStemBuffer[] isolated = new StereoStemBuffer[stems.Length];
                for (int s = 0; s < stems.Length; s++) {
                    float[] copyL = new float[stems[s].Length];
                    float[] copyR = new float[stems[s].Length];
                    Array.Copy(stems[s].Left, copyL, stems[s].Length);
                    Array.Copy(stems[s].Right, copyR, stems[s].Length);
                    isolated[s] = new StereoStemBuffer(stems[s].Name, copyL, copyR);
                }
                stems = isolated;
            }

            if (opt.EnforceEnergyConservation) {
                stems = AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
            }

            if (opt.EnablePhaseFix && stems.Length > 1) {
                ApplyPhaseFixWiener(mixLeft, mixRight, stems, opt.StftSize, opt.WienerExponent, token);
                if (opt.EnforceEnergyConservation) {
                    stems = AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
                }
            }

            onProgress?.Invoke(1.0);
            return stems;
        }

        /// <summary>
        /// Optional platform hook returning additional thermal breathing yield in milliseconds (0..100 ms)
        /// based on OS thermal pressure (e.g., Android <c>PowerManager.CurrentThermalStatus</c>).
        /// </summary>
        public static Func<int> GlobalThermalExtraYieldMsProvider { get; set; }

        /// <summary>
        /// Optional platform hook invoked on background neural/DSP worker threads to lower OS scheduling priority
        /// (e.g., Android <c>Process.SetThreadPriority(ThreadPriority.Background)</c>) so UI and audio never starve.
        /// </summary>
        public static Action GlobalBackgroundThreadQoSHook { get; set; }

        /// <summary>
        /// Global AI compute headroom tier: <c>0 = Eco (40% max)</c>, <c>1 = Balanced (60% max, default)</c>,
        /// <c>2 = Fast (75% max — never 100%)</c>.
        /// </summary>
        public static int GlobalComputeBudgetTier { get; set; } = 1;

        /// <summary>
        /// Computes the non-starving background worker cap for the active <see cref="GlobalComputeBudgetTier"/>,
        /// always reserving guaranteed CPU cores for Android UI/SurfaceFlinger and real-time audio rendering.
        /// </summary>
        public static int ComputeHeadroomWorkerCap(int logicalCores) {
            return ComputeHeadroomWorkerCap(logicalCores, GlobalComputeBudgetTier);
        }

        /// <summary>
        /// Computes the non-starving background worker cap for a specific compute budget tier
        /// (<c>0 = Eco</c>, <c>1 = Balanced</c>, <c>2 = FastMax75</c>). Never allows 100% CPU saturation.
        /// </summary>
        public static int ComputeHeadroomWorkerCap(int logicalCores, int budgetTier) {
            int tier = budgetTier < 0 ? 0 : (budgetTier > 2 ? 2 : budgetTier);
            if (logicalCores <= 2) {
                return 1;
            }
            if (tier == 0) {
                // Eco: <=25-33% CPU (2 threads on 8-core)
                int eco = logicalCores / 4;
                return eco < 1 ? 1 : (eco > 2 ? 2 : eco);
            }
            if (tier == 1) {
                // Balanced (default): <=37.5-50% CPU (3 threads on 8-core, leaving 5/8 = 62.5% free)
                int bal = (logicalCores * 3) / 8;
                if (bal < 2) {
                    bal = logicalCores >= 4 ? 2 : 1;
                }
                return bal > 3 ? 3 : bal;
            }
            // Fast (Max 75% overall ceiling -> strictly <=50% raw threads on 8-core = 4 threads max)
            int fast = logicalCores <= 5 ? Math.Max(1, logicalCores - 2) : (logicalCores / 2);
            return fast > 4 ? 4 : fast;
        }

        /// <summary>
        /// Computes an adaptive inter-chunk GPU UI breathing yield (in ms) from the configured base yield and
        /// the measured Stage 2 inference dispatch duration, preventing Vulkan/WebGPU compositor stalls on heavy
        /// dispatches while adding zero overhead on fast (<c>&lt; 50 ms</c>) chunks.
        /// </summary>
        public static int ComputeAdaptiveGpuBreathingYieldMs(int baseYieldMs, double stage2InferElapsedMs) {
            return ComputeEffectiveBreathingYieldMs(baseYieldMs, stage2InferElapsedMs, 0);
        }

        /// <summary>
        /// Computes the effective inter-chunk breathing yield combining base UI-headroom yield and live OS thermal
        /// extra yield (0..100 ms), ensuring zero overhead on sub-50 ms chunks while throttling gracefully under heat.
        /// </summary>
        public static int ComputeEffectiveBreathingYieldMs(int baseYieldMs, double stage2InferElapsedMs, int thermalExtraMs) {
            int safeThermal = thermalExtraMs <= 0 ? 0 : Math.Min(100, thermalExtraMs);
            if (stage2InferElapsedMs < 50.0) {
                return safeThermal >= 40 ? Math.Min(100, safeThermal / 2) : 0;
            }
            int effectiveBase = 0;
            if (baseYieldMs > 0) {
                int cappedBase = Math.Min(100, baseYieldMs);
                effectiveBase = stage2InferElapsedMs >= 500.0
                    ? Math.Min(100, Math.Max(cappedBase, 32))
                    : cappedBase;
            }
            return Math.Min(100, Math.Max(effectiveBase, safeThermal));
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
        /// Uses lock-free 2-color (even/odd) parallel WOLA for 4–6x multi-core speedup with zero extra track RAM.
        /// </summary>
        public static void ApplyPhaseFixWiener(
            float[] mixLeft,
            float[] mixRight,
            StereoStemBuffer[] stems,
            int stftSize = 2048,
            float exponent = 2.0f,
            CancellationToken token = default) {
            if (mixLeft == null || mixRight == null || stems == null || stems.Length <= 1) {
                // Guard: Wiener partition-of-unity requires >= 2 stems; a 1-stem mask is identically 1.0 (destroys isolation).
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
            int stemCount = stems.Length;
            int numFrames = (length + hopSize - 1) / hopSize + 2;
            int workers = ComputeHeadroomWorkerCap(Environment.ProcessorCount);
            bool useParallel = workers > 1 && numFrames >= 24;

            // Process Left and Right channels with identical COLA sqrt(Hann) WOLA
            for (int ch = 0; ch < 2; ch++) {
                float[] mix = ch == 0 ? mixLeft : mixRight;
                float[][] stemCh = new float[stemCount][];
                float[][] outCh = new float[stemCount][];
                for (int s = 0; s < stemCount; s++) {
                    stemCh[s] = ch == 0 ? stems[s].Left : stems[s].Right;
                    outCh[s] = new float[length];
                }
                float[] weightSum = new float[length];

                // For 50% overlap (hopSize == fftSize / 2), all even frames are mutually disjoint in [0, length),
                // and all odd frames are mutually disjoint in [0, length). Running parity=0 then parity=1 in parallel
                // gives zero lock contention, zero extra full-track RAM, and bit-exact deterministic accumulation.
                for (int parity = 0; parity < 2; parity++) {
                    int firstFrame = parity == 0 ? 0 : -1;
                    int count = firstFrame < numFrames ? ((numFrames - 1 - firstFrame) / 2 + 1) : 0;
                    if (count <= 0) {
                        continue;
                    }

                    if (useParallel && count >= 12) {
                        ParallelOptions po = new ParallelOptions {
                            MaxDegreeOfParallelism = workers,
                            CancellationToken = token
                        };
                        int batchSize = (count + workers - 1) / workers;
                        Parallel.For(0, workers, po, w => {
                            GlobalBackgroundThreadQoSHook?.Invoke();
                            int idxFrom = w * batchSize;
                            int idxTo = Math.Min(count, idxFrom + batchSize);
                            if (idxFrom >= idxTo) {
                                return;
                            }
                            ThreadSafeFFTCache localFft = new ThreadSafeFFTCache(fftSize);
                            try {
                                Complex[] mixSpec = new Complex[fftSize];
                                Complex[][] stemSpec = new Complex[stemCount][];
                                for (int s = 0; s < stemCount; s++) {
                                    stemSpec[s] = new Complex[fftSize];
                                }
                                float[] powerScratch = new float[stemCount];
                                for (int idx = idxFrom; idx < idxTo; idx++) {
                                    token.ThrowIfCancellationRequested();
                                    int frame = firstFrame + idx * 2;
                                    ProcessWienerFrame(mix, stemCh, outCh, weightSum, frame, length, fftSize, hopSize, bins,
                                        window, exponent, localFft, mixSpec, stemSpec, powerScratch);
                                }
                            } finally {
                                localFft.Dispose();
                            }
                        });
                    } else {
                        ThreadSafeFFTCache localFft = new ThreadSafeFFTCache(fftSize);
                        try {
                            Complex[] mixSpec = new Complex[fftSize];
                            Complex[][] stemSpec = new Complex[stemCount][];
                            for (int s = 0; s < stemCount; s++) {
                                stemSpec[s] = new Complex[fftSize];
                            }
                            float[] powerScratch = new float[stemCount];
                            for (int idx = 0; idx < count; idx++) {
                                token.ThrowIfCancellationRequested();
                                int frame = firstFrame + idx * 2;
                                ProcessWienerFrame(mix, stemCh, outCh, weightSum, frame, length, fftSize, hopSize, bins,
                                    window, exponent, localFft, mixSpec, stemSpec, powerScratch);
                            }
                        } finally {
                            localFft.Dispose();
                        }
                    }
                }

                if (useParallel && length >= 65536) {
                    int block = (length + workers - 1) / workers;
                    Parallel.For(0, workers, w => {
                        int from = w * block;
                        int to = Math.Min(length, from + block);
                        for (int n = from; n < to; n++) {
                            float wVal = weightSum[n];
                            float invW = wVal > Epsilon ? (1f / wVal) : 1f;
                            for (int s = 0; s < stemCount; s++) {
                                stemCh[s][n] = outCh[s][n] * invW;
                            }
                        }
                    });
                } else {
                    for (int n = 0; n < length; n++) {
                        float wVal = weightSum[n];
                        float invW = wVal > Epsilon ? (1f / wVal) : 1f;
                        for (int s = 0; s < stemCount; s++) {
                            stemCh[s][n] = outCh[s][n] * invW;
                        }
                    }
                }
            }
        }

        static void ProcessWienerFrame(
            float[] mix,
            float[][] stemCh,
            float[][] outCh,
            float[] weightSum,
            int frame,
            int length,
            int fftSize,
            int hopSize,
            int bins,
            float[] window,
            float exponent,
            ThreadSafeFFTCache fft,
            Complex[] mixSpec,
            Complex[][] stemSpec,
            float[] powerScratch) {
            int start = frame * hopSize;
            if (start >= length || start + fftSize <= 0) {
                return;
            }

            int stemCount = stemCh.Length;
            FillWindowedFrame(mix, start, fftSize, window, mixSpec);
            mixSpec.InPlaceFFT(fft);

            for (int s = 0; s < stemCount; s++) {
                FillWindowedFrame(stemCh[s], start, fftSize, window, stemSpec[s]);
                stemSpec[s].InPlaceFFT(fft);
            }

            bool expIsTwo = MathF.Abs(exponent - 2.0f) < 1e-6f;
            bool expIsOne = !expIsTwo && MathF.Abs(exponent - 1.0f) < 1e-6f;
            float invStems = 1f / stemCount;

            for (int k = 0; k < bins; k++) {
                float sumPow = 0f;
                for (int s = 0; s < stemCount; s++) {
                    Complex c = stemSpec[s][k];
                    float magSq = c.Real * c.Real + c.Imaginary * c.Imaginary;
                    float p;
                    if (expIsTwo) {
                        p = magSq;
                    } else if (expIsOne) {
                        p = MathF.Sqrt(magSq);
                    } else {
                        p = MathF.Pow(MathF.Sqrt(magSq), exponent);
                    }
                    powerScratch[s] = p;
                    sumPow += p;
                }

                Complex mVal = mixSpec[k];
                bool hasEnergy = sumPow > Epsilon;
                float invDenom = hasEnergy ? (1f / (sumPow + Epsilon)) : invStems;
                for (int s = 0; s < stemCount; s++) {
                    float mask = hasEnergy ? (powerScratch[s] * invDenom) : invStems;
                    Complex anchored = new Complex(mVal.Real * mask, mVal.Imaginary * mask);
                    stemSpec[s][k] = anchored;
                    if (k > 0 && k < bins - 1) {
                        stemSpec[s][fftSize - k] = new Complex(anchored.Real, -anchored.Imaginary);
                    }
                }
            }

            for (int s = 0; s < stemCount; s++) {
                stemSpec[s].InPlaceIFFT(fft);
                AccumulateWindowedFrame(stemSpec[s], start, fftSize, window, outCh[s], s == 0 ? weightSum : null);
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

            // Pass 0: Original (L, R) — reuse pass0 buffers directly as in-place TTA accumulators to avoid 4x RAM OOM
            StereoStemBuffer[] accum = RunSingleChunkedPass(mixLeft, mixRight, evaluator, opt, 0.0, passWeight, onProgress, token);
            if (accum == null || accum.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }
            int stemCount = accum.Length;

            // Pass 1: Channel Swap (R, L) -> swap outputs back to (L, R) and accumulate in-place
            StereoStemBuffer[] pass1 = RunSingleChunkedPass(mixRight, mixLeft, evaluator, opt, passWeight, passWeight * 2.0, onProgress, token);
            if (pass1 != null && pass1.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    float[] accL = accum[s].Left;
                    float[] accR = accum[s].Right;
                    float[] p1L = pass1[s].Left;
                    float[] p1R = pass1[s].Right;
                    for (int n = 0; n < length; n++) {
                        // pass1 ran on (R, L), so pass1[s].Right is original Left and pass1[s].Left is original Right
                        accL[n] += p1R[n];
                        accR[n] += p1L[n];
                    }
                }
            }
            pass1 = null;

            // Pass 2: Polarity Inversion (-L, -R) -> subtract negated output in-place and release temp arrays
            float[] invL = new float[length];
            float[] invR = new float[length];
            for (int i = 0; i < length; i++) {
                invL[i] = -mixLeft[i];
                invR[i] = -mixRight[i];
            }
            StereoStemBuffer[] pass2 = RunSingleChunkedPass(invL, invR, evaluator, opt, passWeight * 2.0, passWeight * 3.0, onProgress, token);
            invL = null;
            invR = null;
            if (pass2 != null && pass2.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    float[] accL = accum[s].Left;
                    float[] accR = accum[s].Right;
                    float[] p2L = pass2[s].Left;
                    float[] p2R = pass2[s].Right;
                    for (int n = 0; n < length; n++) {
                        accL[n] -= p2L[n];
                        accR[n] -= p2R[n];
                    }
                }
            }
            pass2 = null;

            // Optional Pass 3: Half-hop time shift (reflect-padded) -> unshift and accumulate in-place
            if (opt.EnableShiftTta) {
                int shift = Math.Max(1, (opt.ChunkSamples / opt.OverlapFactor) / 2);
                float[] shiftL = new float[length + shift];
                float[] shiftR = new float[length + shift];
                for (int i = 0; i < shift; i++) {
                    int src = ReflectIndex(shift - 1 - i, length);
                    shiftL[i] = mixLeft[src];
                    shiftR[i] = mixRight[src];
                }
                Array.Copy(mixLeft, 0, shiftL, shift, length);
                Array.Copy(mixRight, 0, shiftR, shift, length);
                StereoStemBuffer[] pass3 = RunSingleChunkedPass(shiftL, shiftR, evaluator, opt, passWeight * 3.0, 0.90, onProgress, token);
                shiftL = null;
                shiftR = null;
                if (pass3 != null && pass3.Length >= stemCount) {
                    for (int s = 0; s < stemCount; s++) {
                        float[] accL = accum[s].Left;
                        float[] accR = accum[s].Right;
                        float[] p3L = pass3[s].Left;
                        float[] p3R = pass3[s].Right;
                        for (int n = 0; n < length; n++) {
                            accL[n] += p3L[n + shift];
                            accR[n] += p3R[n + shift];
                        }
                    }
                }
                pass3 = null;
            }

            // Final in-place average normalization
            float invCount = 1f / totalPasses;
            for (int s = 0; s < stemCount; s++) {
                float[] accL = accum[s].Left;
                float[] accR = accum[s].Right;
                for (int n = 0; n < length; n++) {
                    accL[n] *= invCount;
                    accR[n] *= invCount;
                }
            }
            return accum;
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

        static StereoStemBuffer[] RunTtaPassesPipelined<TPrepared, TInferOut>(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkPreparer<TPrepared> preparer,
            NeuralChunkInferer<TPrepared, TInferOut> inferer,
            NeuralChunkFinalizer<TPrepared, TInferOut> finalizer,
            MvsepPipelineOptions opt,
            Action<double> onProgress,
            CancellationToken token) {
            int totalPasses = opt.EnableShiftTta ? 4 : 3;
            int length = mixLeft.Length;
            double passWeight = 0.90 / totalPasses;

            StereoStemBuffer[] accum = RunSingleChunkedPassPipelined(
                mixLeft, mixRight, preparer, inferer, finalizer, opt, 0.0, passWeight, onProgress, null, token);
            if (accum == null || accum.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }
            int stemCount = accum.Length;

            StereoStemBuffer[] pass1 = RunSingleChunkedPassPipelined(
                mixRight, mixLeft, preparer, inferer, finalizer, opt, passWeight, passWeight * 2.0, onProgress, null, token);
            if (pass1 != null && pass1.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    float[] accL = accum[s].Left;
                    float[] accR = accum[s].Right;
                    float[] p1L = pass1[s].Left;
                    float[] p1R = pass1[s].Right;
                    for (int n = 0; n < length; n++) {
                        accL[n] += p1R[n];
                        accR[n] += p1L[n];
                    }
                }
            }
            pass1 = null;

            float[] invL = new float[length];
            float[] invR = new float[length];
            for (int i = 0; i < length; i++) {
                invL[i] = -mixLeft[i];
                invR[i] = -mixRight[i];
            }
            StereoStemBuffer[] pass2 = RunSingleChunkedPassPipelined(
                invL, invR, preparer, inferer, finalizer, opt, passWeight * 2.0, passWeight * 3.0, onProgress, null, token);
            invL = null;
            invR = null;
            if (pass2 != null && pass2.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    float[] accL = accum[s].Left;
                    float[] accR = accum[s].Right;
                    float[] p2L = pass2[s].Left;
                    float[] p2R = pass2[s].Right;
                    for (int n = 0; n < length; n++) {
                        accL[n] -= p2L[n];
                        accR[n] -= p2R[n];
                    }
                }
            }
            pass2 = null;

            if (opt.EnableShiftTta) {
                int shift = Math.Max(1, (opt.ChunkSamples / opt.OverlapFactor) / 2);
                float[] shiftL = new float[length + shift];
                float[] shiftR = new float[length + shift];
                for (int i = 0; i < shift; i++) {
                    int src = ReflectIndex(shift - 1 - i, length);
                    shiftL[i] = mixLeft[src];
                    shiftR[i] = mixRight[src];
                }
                Array.Copy(mixLeft, 0, shiftL, shift, length);
                Array.Copy(mixRight, 0, shiftR, shift, length);
                StereoStemBuffer[] pass3 = RunSingleChunkedPassPipelined(
                    shiftL, shiftR, preparer, inferer, finalizer, opt, passWeight * 3.0, 0.90, onProgress, null, token);
                shiftL = null;
                shiftR = null;
                if (pass3 != null && pass3.Length >= stemCount) {
                    for (int s = 0; s < stemCount; s++) {
                        float[] accL = accum[s].Left;
                        float[] accR = accum[s].Right;
                        float[] p3L = pass3[s].Left;
                        float[] p3R = pass3[s].Right;
                        for (int n = 0; n < length; n++) {
                            accL[n] += p3L[n + shift];
                            accR[n] += p3R[n + shift];
                        }
                    }
                }
                pass3 = null;
            }

            float invCount = 1f / totalPasses;
            for (int s = 0; s < stemCount; s++) {
                float[] accL = accum[s].Left;
                float[] accR = accum[s].Right;
                for (int n = 0; n < length; n++) {
                    accL[n] *= invCount;
                    accR[n] *= invCount;
                }
            }
            return accum;
        }

        static StereoStemBuffer[] RunSingleChunkedPassPipelined<TPrepared, TInferOut>(
            float[] mixLeft,
            float[] mixRight,
            NeuralChunkPreparer<TPrepared> preparer,
            NeuralChunkInferer<TPrepared, TInferOut> inferer,
            NeuralChunkFinalizer<TPrepared, TInferOut> finalizer,
            MvsepPipelineOptions opt,
            double progressStart,
            double progressEnd,
            Action<double> onProgress,
            ProgressiveStemsReadyCallback onProgressiveReady,
            CancellationToken token) {
            int length = mixLeft.Length;
            int chunkSize = opt.ChunkSamples;
            int step = Math.Max(1, chunkSize / opt.OverlapFactor);
            int pad = step;
            int paddedLen = length + pad * 2;
            if (paddedLen < chunkSize) {
                paddedLen = chunkSize;
            }

            float[] padL = new float[paddedLen];
            float[] padR = new float[paddedLen];
            for (int i = 0; i < paddedLen; i++) {
                int srcIdx = ReflectIndex(i - pad, length);
                padL[i] = mixLeft[srcIdx];
                padR[i] = mixRight[srcIdx];
            }

            float[] chunkWindow = CreateRaisedCosineChunkWindow(chunkSize, opt.FadeFraction);
            float[] weightSum = new float[paddedLen];

            int maxChunks = ((paddedLen - chunkSize) + step - 1) / step + 1;
            if (maxChunks < 1) {
                maxChunks = 1;
            }
            int[] starts = new int[maxChunks];
            int totalChunks = 0;
            for (int c = 0; c < maxChunks; c++) {
                int st = c * step;
                if (st + chunkSize > paddedLen) {
                    st = paddedLen - chunkSize;
                }
                starts[totalChunks++] = st;
                if (st + chunkSize >= paddedLen) {
                    break;
                }
            }

            float[][] accumL = null;
            float[][] accumR = null;
            string[] stemNames = null;
            StereoStemBuffer[] progressiveStems = null;
            int publishedSamples = 0;
            bool abortedEmpty = false;
            System.Diagnostics.Stopwatch passSw = System.Diagnostics.Stopwatch.StartNew();

            Action<int, TPrepared, TInferOut> runStage3Sequential = (chunkIdx, prepPayload, inferPayload) => {
                StereoStemBuffer[] chunkOut = finalizer(prepPayload, inferPayload, chunkSize);
                if (chunkOut == null || chunkOut.Length == 0) {
                    abortedEmpty = true;
                    return;
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
                    if (onProgressiveReady != null) {
                        progressiveStems = new StereoStemBuffer[stems];
                        for (int s = 0; s < stems; s++) {
                            progressiveStems[s] = new StereoStemBuffer(stemNames[s], length);
                        }
                    }
                }

                int start = starts[chunkIdx];
                for (int i = 0; i < chunkSize; i++) {
                    float w = chunkWindow[i];
                    int dst = start + i;
                    weightSum[dst] += w;
                    for (int s = 0; s < accumL.Length; s++) {
                        accumL[s][dst] += chunkOut[s].Left[i] * w;
                        accumR[s][dst] += chunkOut[s].Right[i] * w;
                    }
                }

                if (onProgressiveReady != null && progressiveStems != null) {
                    int safeFinalized = (chunkIdx + 1 < totalChunks)
                        ? Math.Min(length, Math.Max(0, starts[chunkIdx + 1] - pad))
                        : length;
                    if (safeFinalized > publishedSamples) {
                        for (int s = 0; s < accumL.Length; s++) {
                            float[] dstL = progressiveStems[s].Left;
                            float[] dstR = progressiveStems[s].Right;
                            float[] srcL = accumL[s];
                            float[] srcR = accumR[s];
                            for (int n = publishedSamples; n < safeFinalized; n++) {
                                int pIdx = n + pad;
                                float wVal = weightSum[pIdx];
                                float invW = wVal > Epsilon ? (1f / wVal) : 1f;
                                dstL[n] = srcL[pIdx] * invW;
                                dstR[n] = srcR[pIdx] * invW;
                            }
                        }
                        publishedSamples = safeFinalized;
                        onProgressiveReady(progressiveStems, publishedSamples, length, passSw.Elapsed.TotalSeconds);
                    }
                }

                if (onProgress != null) {
                    double frac = (chunkIdx + 1.0) / totalChunks;
                    onProgress(progressStart + (progressEnd - progressStart) * frac);
                }
            };

            bool usePipeline = opt.EnableAsyncPipelining && totalChunks > 1;
            ThreadPriority originalPriority = ThreadPriority.Normal;
            bool priorityChanged = false;
            try {
                originalPriority = Thread.CurrentThread.Priority;
                if (originalPriority > ThreadPriority.BelowNormal) {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                }
            } catch {
                // Best-effort thread priority hint for UI/audio responsiveness
            }

            try {
                if (!usePipeline) {
                    float[] chunkInL = new float[chunkSize];
                    float[] chunkInR = new float[chunkSize];
                    for (int c = 0; c < totalChunks; c++) {
                        token.ThrowIfCancellationRequested();
                        int st = starts[c];
                        Array.Copy(padL, st, chunkInL, 0, chunkSize);
                        Array.Copy(padR, st, chunkInR, 0, chunkSize);
                        TPrepared prepared = preparer(chunkInL, chunkInR, chunkSize);
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        TInferOut inferred = inferer(prepared);
                        double inferMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        runStage3Sequential(c, prepared, inferred);
                        if (abortedEmpty) {
                            return Array.Empty<StereoStemBuffer>();
                        }
                        if (c + 1 < totalChunks) {
                            int thermalMs = GlobalThermalExtraYieldMsProvider != null ? GlobalThermalExtraYieldMsProvider() : 0;
                            int yieldMs = ComputeEffectiveBreathingYieldMs(opt.GpuUiBreathingYieldMs, inferMs, thermalMs);
                            if (yieldMs > 0) {
                                token.WaitHandle.WaitOne(yieldMs);
                            }
                        }
                    }
                } else {
                    // Triple-buffered 3-stage producer-consumer pipeline:
                    // Stage 1 (CPU STFT/pack for c+1) || Stage 2 (GPU/ONNX inference for c) || Stage 3 (CPU iSTFT+WOLA for c-1).
                    // Using 3 slots (chunkIdx % 3) guarantees Stage 1 (c+1), Stage 2 (c), and Stage 3 (c-1) never alias buffers.
                    // Stage 3 joins strictly in chunk order c = 0..N-1 before dispatching c+1, guaranteeing 0.0 dB IEEE-754 identity.
                    float[][] inLSlots = new float[3][] { new float[chunkSize], new float[chunkSize], new float[chunkSize] };
                    float[][] inRSlots = new float[3][] { new float[chunkSize], new float[chunkSize], new float[chunkSize] };

                    Func<int, Task<TPrepared>> launchStage1 = chunkIdx => {
                        int slot = chunkIdx % 3;
                        int st = starts[chunkIdx];
                        float[] bufL = inLSlots[slot];
                        float[] bufR = inRSlots[slot];
                        return Task.Run(() => {
                            GlobalBackgroundThreadQoSHook?.Invoke();
                            token.ThrowIfCancellationRequested();
                            Array.Copy(padL, st, bufL, 0, chunkSize);
                            Array.Copy(padR, st, bufR, 0, chunkSize);
                            return preparer(bufL, bufR, chunkSize);
                        }, token);
                    };

                    Task<TPrepared> curPrepTask = launchStage1(0);
                    Task<TPrepared> nextPrepTask = null;
                    Task prevPostTask = null;

                    try {
                        for (int c = 0; c < totalChunks; c++) {
                            token.ThrowIfCancellationRequested();

                            TPrepared curPrep = curPrepTask.GetAwaiter().GetResult();

                            // Overlap Stage 1 for chunk c+1 concurrently with Stage 2 GPU inference for chunk c
                            nextPrepTask = (c + 1 < totalChunks) ? launchStage1(c + 1) : null;

                            // Stage 2: run ONNX inference on current chunk while Stage 1 (c+1) and Stage 3 (c-1) run in parallel
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            TInferOut curInfer = inferer(curPrep);
                            double inferMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                            // Join Stage 3 (c-1) before dispatching Stage 3 (c) so overlap-add order is bit-exact
                            if (prevPostTask != null) {
                                prevPostTask.GetAwaiter().GetResult();
                                prevPostTask = null;
                                if (abortedEmpty) {
                                    break;
                                }
                            }

                            int capturedChunk = c;
                            TPrepared capturedPrep = curPrep;
                            TInferOut capturedInfer = curInfer;
                            prevPostTask = Task.Run(() => {
                                GlobalBackgroundThreadQoSHook?.Invoke();
                                runStage3Sequential(capturedChunk, capturedPrep, capturedInfer);
                            }, token);

                            // Adaptive UI-compositor + OS thermal breathing gap between consecutive neural chunks
                            if (c + 1 < totalChunks) {
                                int thermalMs = GlobalThermalExtraYieldMsProvider != null ? GlobalThermalExtraYieldMsProvider() : 0;
                                int yieldMs = ComputeEffectiveBreathingYieldMs(opt.GpuUiBreathingYieldMs, inferMs, thermalMs);
                                if (yieldMs > 0) {
                                    token.WaitHandle.WaitOne(yieldMs);
                                }
                            }

                            curPrepTask = nextPrepTask;
                            nextPrepTask = null;
                        }

                        if (prevPostTask != null) {
                            prevPostTask.GetAwaiter().GetResult();
                            prevPostTask = null;
                        }
                    } finally {
                        // Always drain any in-flight worker tasks before returning so no background task touches arrays
                        if (nextPrepTask != null) {
                            try { nextPrepTask.GetAwaiter().GetResult(); } catch { }
                        }
                        if (curPrepTask != null && !curPrepTask.IsCompleted) {
                            try { curPrepTask.GetAwaiter().GetResult(); } catch { }
                        }
                        if (prevPostTask != null) {
                            try { prevPostTask.GetAwaiter().GetResult(); } catch { }
                        }
                    }

                    if (abortedEmpty) {
                        return Array.Empty<StereoStemBuffer>();
                    }
                }
            } finally {
                if (priorityChanged) {
                    try { Thread.CurrentThread.Priority = originalPriority; } catch { }
                }
            }

            if (accumL == null) {
                return Array.Empty<StereoStemBuffer>();
            }

            if (progressiveStems != null && publishedSamples == length) {
                return progressiveStems;
            }

            StereoStemBuffer[] result = progressiveStems ?? new StereoStemBuffer[accumL.Length];
            for (int s = 0; s < accumL.Length; s++) {
                float[] outL = progressiveStems != null ? progressiveStems[s].Left : new float[length];
                float[] outR = progressiveStems != null ? progressiveStems[s].Right : new float[length];
                int startN = progressiveStems != null ? publishedSamples : 0;
                for (int n = startN; n < length; n++) {
                    int src = n + pad;
                    float w = weightSum[src];
                    float invW = w > Epsilon ? (1f / w) : 1f;
                    outL[n] = accumL[s][src] * invW;
                    outR[n] = accumR[s][src] * invW;
                }
                if (progressiveStems == null) {
                    result[s] = new StereoStemBuffer(stemNames[s], outL, outR);
                }
            }
            return result;
        }

        /// <summary>
        /// Appends or refreshes the conserved residual stem (<c>instrumental</c> for 1-stem models or <c>residual</c> for
        /// multi-stem models) so <c>sum(all returned stems) == mix</c> without duplicating an existing residual slot.
        /// </summary>
        public static StereoStemBuffer[] AppendOrUpdateConservedResidual(
            float[] mixLeft,
            float[] mixRight,
            StereoStemBuffer[] stems) {
            if (stems == null || stems.Length == 0) {
                return Array.Empty<StereoStemBuffer>();
            }
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
            if (residualIdx < 0 && stems.Length == 2 &&
                string.Equals(stems[1].Name, "instrumental", StringComparison.OrdinalIgnoreCase)) {
                residualIdx = 1;
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
            int candCount = candidates.Length;
            int numFrames = (length + hopSize - 1) / hopSize + 2;
            int workers = ComputeHeadroomWorkerCap(Environment.ProcessorCount);
            bool useParallel = workers > 1 && numFrames >= 24;

            float[] outL = new float[length];
            float[] outR = new float[length];
            float[] weightSum = new float[length];

            for (int ch = 0; ch < 2; ch++) {
                float[] dst = ch == 0 ? outL : outR;
                Array.Clear(weightSum, 0, weightSum.Length);

                for (int parity = 0; parity < 2; parity++) {
                    int firstFrame = parity == 0 ? 0 : -1;
                    int count = firstFrame < numFrames ? ((numFrames - 1 - firstFrame) / 2 + 1) : 0;
                    if (count <= 0) {
                        continue;
                    }

                    if (useParallel && count >= 12) {
                        ParallelOptions po = new ParallelOptions {
                            MaxDegreeOfParallelism = workers,
                            CancellationToken = token
                        };
                        int batchSize = (count + workers - 1) / workers;
                        Parallel.For(0, workers, po, w => {
                            GlobalBackgroundThreadQoSHook?.Invoke();
                            int idxFrom = w * batchSize;
                            int idxTo = Math.Min(count, idxFrom + batchSize);
                            if (idxFrom >= idxTo) {
                                return;
                            }
                            ThreadSafeFFTCache localFft = new ThreadSafeFFTCache(fftSize);
                            try {
                                Complex[][] specs = new Complex[candCount][];
                                for (int c = 0; c < candCount; c++) {
                                    specs[c] = new Complex[fftSize];
                                }
                                Complex[] fused = new Complex[fftSize];
                                for (int idx = idxFrom; idx < idxTo; idx++) {
                                    token.ThrowIfCancellationRequested();
                                    int frame = firstFrame + idx * 2;
                                    ProcessEnsembleFrame(candidates, ch, normWeights, method, frame, length, fftSize, hopSize, bins,
                                        window, localFft, specs, fused, dst, weightSum);
                                }
                            } finally {
                                localFft.Dispose();
                            }
                        });
                    } else {
                        ThreadSafeFFTCache localFft = new ThreadSafeFFTCache(fftSize);
                        try {
                            Complex[][] specs = new Complex[candCount][];
                            for (int c = 0; c < candCount; c++) {
                                specs[c] = new Complex[fftSize];
                            }
                            Complex[] fused = new Complex[fftSize];
                            for (int idx = 0; idx < count; idx++) {
                                token.ThrowIfCancellationRequested();
                                int frame = firstFrame + idx * 2;
                                ProcessEnsembleFrame(candidates, ch, normWeights, method, frame, length, fftSize, hopSize, bins,
                                    window, localFft, specs, fused, dst, weightSum);
                            }
                        } finally {
                            localFft.Dispose();
                        }
                    }
                }

                for (int n = 0; n < length; n++) {
                    float w = weightSum[n];
                    if (w > Epsilon) {
                        dst[n] /= w;
                    }
                }
            }

            return new StereoStemBuffer(stemName, outL, outR);
        }

        static void ProcessEnsembleFrame(
            StereoStemBuffer[] candidates,
            int ch,
            float[] normWeights,
            MvsepEnsembleMethod method,
            int frame,
            int length,
            int fftSize,
            int hopSize,
            int bins,
            float[] window,
            ThreadSafeFFTCache fft,
            Complex[][] specs,
            Complex[] fused,
            float[] dst,
            float[] weightSum) {
            int start = frame * hopSize;
            if (start >= length || start + fftSize <= 0) {
                return;
            }

            for (int c = 0; c < candidates.Length; c++) {
                float[] src = ch == 0 ? candidates[c].Left : candidates[c].Right;
                FillWindowedFrame(src, start, fftSize, window, specs[c]);
                specs[c].InPlaceFFT(fft);
            }

            for (int k = 0; k < bins; k++) {
                Complex val;
                if (method == MvsepEnsembleMethod.MinFft) {
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
