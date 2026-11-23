using System.Diagnostics;

using Cavern.Neural;

using Cavern.Restoration;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Runtime status metadata returned by <see cref="INeuralInferenceRuntime"/>.
/// </summary>
public sealed class NeuralRuntimeInfo {
    /// <summary>True when the native ONNX Runtime library loaded and initialized successfully.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Loaded native library path (<c>NativeLibraryDir/libonnxruntime.so</c>).</summary>
    public string LibraryPath { get; init; } = string.Empty;

    /// <summary>Reported ONNX Runtime version string from <c>OrtApiBase.GetVersionString()</c>.</summary>
    public string VersionString { get; init; } = string.Empty;

    /// <summary>Active Execution Provider selected ("QNN HTP NPU", "XNNPACK ARM64", or "CPU").</summary>
    public string ActiveProvider { get; init; } = "CPU";

    /// <summary>True when Qualcomm Snapdragon QNN HTP libraries were detected alongside ONNX Runtime.</summary>
    public bool IsQnnAvailable { get; init; }

    /// <summary>Optional diagnostic error message when <see cref="IsAvailable"/> is false.</summary>
    public string DiagnosticMessage { get; init; } = string.Empty;
}

/// <summary>
/// Dense float32 tensor container exchanged across <see cref="INeuralSession"/>.
/// </summary>
public sealed class NeuralTensorFloat {
    /// <summary>Tensor shape dimensions (e.g., <c>[1, 2, 131072]</c> or <c>[1, 4, 2, 131072]</c>).</summary>
    public long[] Shape { get; }

    /// <summary>Contiguous row-major float32 tensor elements.</summary>
    public float[] Data { get; }

    /// <summary>Creates a float32 tensor container.</summary>
    public NeuralTensorFloat(long[] shape, float[] data) {
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }
}

/// <summary>
/// Loaded ONNX inference session bound to a specific model file and Execution Provider.
/// </summary>
public interface INeuralSession : IDisposable {
    /// <summary>Loaded model file path.</summary>
    string ModelPath { get; }

    /// <summary>Execution Provider active on this session ("QNN HTP NPU", "XNNPACK ARM64", or "CPU").</summary>
    string ExecutionProvider { get; }

    /// <summary>Ordered model input tensor names.</summary>
    IReadOnlyList<string> InputNames { get; }

    /// <summary>Ordered model input tensor shapes (-1 for dynamic dimensions).</summary>
    IReadOnlyList<long[]> InputShapes => Array.Empty<long[]>();

    /// <summary>Ordered model output tensor names.</summary>
    IReadOnlyList<string> OutputNames { get; }

    /// <summary>Executes synchronous inference on the primary input tensor and returns all output tensors.</summary>
    NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor);
}

/// <summary>
/// Platform-implemented managed ONNX Runtime host (implemented on Android via native <c>libonnxruntime.so</c>
/// in <c>NativeLibraryDir</c> with QNN HTP / XNNPACK / CPU execution provider cascade).
/// </summary>
public interface INeuralInferenceRuntime : IDisposable {
    /// <summary>Probes and returns current native runtime availability, version, and active EP capabilities.</summary>
    NeuralRuntimeInfo GetRuntimeInfo();

    /// <summary>Creates a new ONNX inference session for <paramref name="modelPath"/> using <paramref name="preferredProvider"/> (<paramref name="intraOpThreads"/> = 0 auto-detects performance cores).</summary>
    INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0);

    /// <summary>Resolves the local file path of an installed catalog or custom model, or null if not yet installed.</summary>
    string ResolveInstalledModelPath(string modelId);
}

/// <summary>
/// High-level Slot 7B offline neural processor combining <see cref="INeuralInferenceRuntime"/> with
/// <see cref="MvsepAntiArtifactPipeline"/> (exact Kaiser polyphase sample-rate matching, 2x–8x overlap-add, TTA,
/// semantic <see cref="NeuralStemRole"/>-matched <c>avg_wave</c>/<c>min_fft</c>/<c>avg_fft</c> ensembling,
/// conservation-before-Wiener <c>phase_fix</c>, and exact <c>residual = mix - sum(stems)</c> energy conservation).
/// </summary>
public static class NeuralAudioOfflinePipeline {
    /// <summary>
    /// Separates a stereo waveform using one or more ONNX checkpoints with automatic <see cref="KaiserSincResampler"/>
    /// rate conversion to/from each model's native sample rate and semantic <see cref="NeuralStemRole"/>-matched ensembling.
    /// </summary>
    public static (StereoStemBuffer[] Stems, float RealTimeFactor) RunSeparation(
        INeuralInferenceRuntime runtime,
        IReadOnlyList<string> modelPaths,
        IReadOnlyList<string[]> expectedStemNamesPerModel,
        float[] ensembleWeights,
        float[] mixLeft,
        float[] mixRight,
        int sampleRate,
        NeuralAudioConfig config,
        int chunkSamples,
        Action<double, string> onProgress = null,
        CancellationToken token = default,
        IReadOnlyList<int> expectedSampleRatesPerModel = null,
        IReadOnlyList<int> chunkSamplesPerModel = null) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(modelPaths);
        ArgumentNullException.ThrowIfNull(mixLeft);
        ArgumentNullException.ThrowIfNull(mixRight);

        if (modelPaths.Count == 0) {
            throw new ArgumentException("At least one ONNX model path is required.", nameof(modelPaths));
        }

        NeuralAudioConfig safeCfg = (config ?? NeuralAudioConfig.Default).Sanitize();
        Stopwatch sw = Stopwatch.StartNew();
        int modelCount = modelPaths.Count;
        int nativeSr = sampleRate >= 8000 ? sampleRate : 48000;
        int nativeLength = mixLeft.Length;
        StereoStemBuffer[][] perModelResults = new StereoStemBuffer[modelCount][];
        Dictionary<int, (float[] Left, float[] Right)> resampledMixCache = [];

        // Always apply residual conservation & Wiener phase_fix ONCE at native sample rate against original mixLeft/mixRight
        // whenever ensembling (>1 model) OR when sample-rate conversion occurred (prevents phase smear across resamplers).
        // Pass 0 so NativeOnnxRuntimeBridge auto-detects Prime/Performance ARM64 cluster threads (6 on Snapdragon 8 Elite, 4 on big.LITTLE).
        const int threads = 0;
        bool anyResampled = false;

        for (int m = 0; m < modelCount; m++) {
            token.ThrowIfCancellationRequested();
            string path = modelPaths[m];
            string[] stemNames = expectedStemNamesPerModel != null && m < expectedStemNamesPerModel.Count
                ? expectedStemNamesPerModel[m]
                : ["vocals"];
            int modelSr = expectedSampleRatesPerModel != null && m < expectedSampleRatesPerModel.Count && expectedSampleRatesPerModel[m] >= 8000
                ? expectedSampleRatesPerModel[m]
                : ResolveModelExpectedSampleRate(path, nativeSr);
            int modelChunk = chunkSamplesPerModel != null && m < chunkSamplesPerModel.Count && chunkSamplesPerModel[m] >= 4096
                ? chunkSamplesPerModel[m]
                : chunkSamples;

            bool needsResample = modelSr != nativeSr;
            if (needsResample) {
                anyResampled = true;
            }

            MvsepPipelineOptions passOpt = safeCfg.ToMvsepOptions(modelChunk);
            if (modelCount > 1 || needsResample) {
                passOpt.EnablePhaseFix = false;
                passOpt.EnforceEnergyConservation = false;
            }

            float[] inL = mixLeft;
            float[] inR = mixRight;
            if (needsResample) {
                if (!resampledMixCache.TryGetValue(modelSr, out (float[] Left, float[] Right) cached)) {
                    KaiserSincResampler.ResampleStereo(mixLeft, mixRight, nativeSr, modelSr, out float[] rL, out float[] rR);
                    cached = (rL, rR);
                    resampledMixCache[modelSr] = cached;
                }
                inL = cached.Left;
                inR = cached.Right;
            }

            using INeuralSession session = runtime.CreateSession(path, safeCfg.PreferredProvider, threads);
            int modelIdx = m;
            string modelLabel = Path.GetFileNameWithoutExtension(path);
            NeuralModelCatalogEntry catalogEntry = NeuralModelCatalog.FindByFileName(Path.GetFileName(path));
            ResolvedModelContract contract = ResolveContract(session, catalogEntry, modelChunk);
            if (contract.FixedChunkSamples > 0) {
                passOpt = safeCfg.ToMvsepOptions(contract.FixedChunkSamples);
                if (modelCount > 1 || needsResample) {
                    passOpt.EnablePhaseFix = false;
                    passOpt.EnforceEnergyConservation = false;
                }
            }

            Stopwatch modelSw = Stopwatch.StartNew();
            StereoStemBuffer[] rawModelStems = MvsepAntiArtifactPipeline.ProcessTrack(
                inL,
                inR,
                (chunkL, chunkR, len) => EvaluateStereoChunk(session, chunkL, chunkR, len, stemNames, contract),
                passOpt,
                p => {
                    double totalP = (modelIdx + p) / modelCount;
                    double scale = (modelCount > 1 || needsResample) ? 0.90 : 1.0;
                    string etaPart = string.Empty;
                    if (p >= 0.03 && p < 0.995) {
                        double elapsed = modelSw.Elapsed.TotalSeconds;
                        double rem = elapsed * (1.0 - p) / p;
                        if (double.IsFinite(rem) && rem >= 1.0 && rem < 7200.0) {
                            etaPart = rem >= 60.0
                                ? $" · ~{(int)(rem / 60)}m{(int)(rem % 60):00}s"
                                : $" · ~{(int)Math.Ceiling(rem)}s";
                        }
                    }
                    onProgress?.Invoke(totalP * scale, $"[{session.ExecutionProvider}] {modelLabel} {p:0%}{etaPart}");
                },
                token);

            if (needsResample && rawModelStems != null && rawModelStems.Length > 0) {
                KaiserSincResampler backResampler = new(modelSr, nativeSr);
                StereoStemBuffer[] nativeStems = new StereoStemBuffer[rawModelStems.Length];
                for (int s = 0; s < rawModelStems.Length; s++) {
                    token.ThrowIfCancellationRequested();
                    float[] outL = new float[nativeLength];
                    float[] outR = new float[nativeLength];
                    backResampler.ResampleMonoInto(rawModelStems[s].Left, rawModelStems[s].Length, outL, nativeLength);
                    backResampler.ResampleMonoInto(rawModelStems[s].Right, rawModelStems[s].Length, outR, nativeLength);
                    nativeStems[s] = new StereoStemBuffer(rawModelStems[s].Name, outL, outR);
                }
                perModelResults[m] = nativeStems;
            } else {
                perModelResults[m] = rawModelStems ?? Array.Empty<StereoStemBuffer>();
            }
        }

        StereoStemBuffer[] finalStems;
        MvsepPipelineOptions postOpt = safeCfg.ToMvsepOptions(chunkSamples);
        if (modelCount == 1) {
            finalStems = perModelResults[0];
            if (anyResampled && finalStems.Length > 0) {
                finalStems = ApplyNativePostConservationAndPhaseFix(mixLeft, mixRight, finalStems, safeCfg, postOpt, token);
                onProgress?.Invoke(1.0, "MVSEP Separation Complete");
            }
        } else {
            onProgress?.Invoke(0.92, $"MVSEP Semantic Ensemble ({safeCfg.EnsembleMode})...");
            finalStems = EnsembleBySemanticRole(perModelResults, ensembleWeights, safeCfg.EnsembleMode, postOpt.StftSize, token);
            if (finalStems.Length > 0) {
                finalStems = ApplyNativePostConservationAndPhaseFix(mixLeft, mixRight, finalStems, safeCfg, postOpt, token);
            }
            onProgress?.Invoke(1.0, "MVSEP Separation Complete");
        }

        sw.Stop();
        double audioSec = nativeSr > 0 ? nativeLength / (double)nativeSr : 1.0;
        float rtf = audioSec > 1e-3 ? (float)(sw.Elapsed.TotalSeconds / audioSec) : 0f;
        return (finalStems, rtf);
    }

    static int ResolveModelExpectedSampleRate(string modelPath, int fallbackSampleRate) {
        if (!string.IsNullOrWhiteSpace(modelPath)) {
            string fileName = Path.GetFileName(modelPath);
            for (int i = 0; i < NeuralModelCatalog.Entries.Count; i++) {
                NeuralModelCatalogEntry e = NeuralModelCatalog.Entries[i];
                if (string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase)) {
                    return e.ExpectedSampleRate;
                }
            }
        }
        return fallbackSampleRate;
    }

    static StereoStemBuffer[] EnsembleBySemanticRole(
        StereoStemBuffer[][] perModelResults,
        float[] ensembleWeights,
        MvsepEnsembleMethod method,
        int stftSize,
        CancellationToken token) {
        List<NeuralStemRole> encounteredRoles = [];
        Dictionary<NeuralStemRole, List<(StereoStemBuffer Stem, float Weight)>> roleBuckets = [];

        for (int m = 0; m < perModelResults.Length; m++) {
            StereoStemBuffer[] modelStems = perModelResults[m];
            if (modelStems == null) {
                continue;
            }
            float modelWeight = (ensembleWeights != null && m < ensembleWeights.Length && ensembleWeights[m] > 0f)
                ? ensembleWeights[m]
                : 1f;

            for (int s = 0; s < modelStems.Length; s++) {
                StereoStemBuffer stem = modelStems[s];
                if (stem == null) {
                    continue;
                }
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stem.Name);
                // Defer synthetic residual stems so final exact conservation computes residual = mix - sum(ensembled)
                if (role == NeuralStemRole.Residual && string.Equals(stem.Name, "residual", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                if (!roleBuckets.TryGetValue(role, out List<(StereoStemBuffer Stem, float Weight)> bucket)) {
                    bucket = [];
                    roleBuckets[role] = bucket;
                    encounteredRoles.Add(role);
                }
                bucket.Add((stem, modelWeight));
            }
        }

        List<StereoStemBuffer> fused = new(encounteredRoles.Count);
        for (int r = 0; r < encounteredRoles.Count; r++) {
            token.ThrowIfCancellationRequested();
            NeuralStemRole role = encounteredRoles[r];
            List<(StereoStemBuffer Stem, float Weight)> bucket = roleBuckets[role];
            if (bucket.Count == 1) {
                fused.Add(bucket[0].Stem);
            } else {
                StereoStemBuffer[] candidates = new StereoStemBuffer[bucket.Count];
                float[] weights = new float[bucket.Count];
                for (int i = 0; i < bucket.Count; i++) {
                    candidates[i] = bucket[i].Stem;
                    weights[i] = bucket[i].Weight;
                }
                fused.Add(MvsepAntiArtifactPipeline.EnsembleStems(
                    candidates,
                    weights,
                    method,
                    stftSize,
                    candidates[0].Name,
                    token));
            }
        }
        return fused.ToArray();
    }

    static StereoStemBuffer[] ApplyNativePostConservationAndPhaseFix(
        float[] mixLeft,
        float[] mixRight,
        StereoStemBuffer[] stems,
        NeuralAudioConfig safeCfg,
        MvsepPipelineOptions opt,
        CancellationToken token) {
        if (stems == null || stems.Length == 0) {
            return Array.Empty<StereoStemBuffer>();
        }
        if (safeCfg.EnforceEnergyConservation) {
            stems = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
        }
        if (safeCfg.EnablePhaseFix && stems.Length > 1) {
            MvsepAntiArtifactPipeline.ApplyPhaseFixWiener(mixLeft, mixRight, stems, opt.StftSize, opt.WienerExponent, token);
            if (safeCfg.EnforceEnergyConservation) {
                stems = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
            }
        }
        return stems;
    }

    /// <summary>
    /// Runs a Dereverb ONNX checkpoint (e.g., <c>Mel-Band RoFormer Dereverb</c>) with MVSEP overlap-add and
    /// computes exact dry/wet decomposition + physical Direct-to-Reverberant Ratio (DRR dB) for W11 Vocal Mix-Match.
    /// </summary>
    public static (DereverbDecompositionResult Result, float RealTimeFactor) RunDereverbDryWet(
        INeuralInferenceRuntime runtime,
        string dereverbModelPath,
        StereoStemBuffer vocalStem,
        int sampleRate,
        NeuralAudioConfig config,
        int chunkSamples = 352800,
        Action<double, string> onProgress = null,
        CancellationToken token = default,
        int expectedModelSampleRate = 44100) {
        ArgumentNullException.ThrowIfNull(vocalStem);
        NeuralAudioConfig dereverbCfg = new() {
            PreferredProvider = config?.PreferredProvider ?? NeuralExecutionProvider.Auto,
            OverlapFactor = config?.OverlapFactor ?? MvsepPipelineOptions.DefaultOverlap,
            EnableTta = config?.EnableTta ?? false,
            EnablePhaseFix = false, // Preserve exact model dry vocal waveform before computing wet = orig - dry
            EnforceEnergyConservation = false
        };

        (StereoStemBuffer[] stems, float rtf) = RunSeparation(
            runtime,
            [dereverbModelPath],
            [["dry_vocal"]],
            null,
            vocalStem.Left,
            vocalStem.Right,
            sampleRate,
            dereverbCfg,
            chunkSamples,
            onProgress,
            token,
            expectedSampleRatesPerModel: [expectedModelSampleRate > 0 ? expectedModelSampleRate : 44100]);

        StereoStemBuffer dry = stems.Length > 0 ? stems[0] : new StereoStemBuffer("dry_vocal", vocalStem.Length);
        DereverbDecompositionResult decomp = MvsepAntiArtifactPipeline.ComputeDryWetDecomposition(vocalStem, dry);
        return (decomp, rtf);
    }

    /// <summary>
    /// Builds a <see cref="NeuralSeparatedTrack"/> ready to bind into <see cref="Adapters.NeuralStemStageProcessor"/>
    /// (extracting mono Lead &amp; Backing references for zero-phase multichannel Spatial Karaoke).
    /// </summary>
    public static NeuralSeparatedTrack BuildSeparatedTrack(
        string trackKey,
        int sampleRate,
        StereoStemBuffer[] stems,
        DereverbDecompositionResult dereverb = null,
        bool derivedFromLosslessTwin = false,
        string planSignature = "") {
        float[] monoLead = null;
        float[] monoBacking = null;

        if (stems != null) {
            StereoStemBuffer leadStem = null;
            StereoStemBuffer vocalStem = null;
            StereoStemBuffer backingStem = null;

            for (int i = 0; i < stems.Length; i++) {
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stems[i].Name);
                if (role == NeuralStemRole.LeadVocal) {
                    leadStem = stems[i];
                } else if (role == NeuralStemRole.Vocals && vocalStem == null) {
                    vocalStem = stems[i];
                } else if (role == NeuralStemRole.BackingVocals) {
                    backingStem = stems[i];
                }
            }

            StereoStemBuffer chosenLead = leadStem ?? vocalStem;
            if (chosenLead != null) {
                monoLead = DownmixMono(chosenLead);
            }
            if (backingStem != null) {
                monoBacking = DownmixMono(backingStem);
            }
        }

        return new NeuralSeparatedTrack {
            SourceTrackKey = trackKey ?? string.Empty,
            SampleRate = sampleRate > 0 ? sampleRate : 48000,
            Stems = stems ?? Array.Empty<StereoStemBuffer>(),
            MonoLeadVocalRef = monoLead,
            MonoBackingVocalRef = monoBacking,
            DereverbAnalysis = dereverb,
            DerivedFromLosslessTwin = derivedFromLosslessTwin,
            PlanSignature = planSignature ?? string.Empty
        };
    }

    internal readonly struct ResolvedModelContract {
        public NeuralOnnxContractKind Kind { get; init; }
        public int FftSize { get; init; }
        public int HopSize { get; init; }
        public int DimF { get; init; }
        public int DimT { get; init; }
        public float Compensate { get; init; }
        public bool InvertPrimaryStem { get; init; }
        public int FixedChunkSamples { get; init; }
    }

    internal static ResolvedModelContract ResolveContract(
        INeuralSession session,
        NeuralModelCatalogEntry catalogEntry,
        int defaultChunkSamples) {
        if (catalogEntry != null && catalogEntry.ContractKind != NeuralOnnxContractKind.Auto) {
            int fixedChunk = catalogEntry.ContractKind switch {
                NeuralOnnxContractKind.MdxNetStft4Ch when catalogEntry.StftDimT > 0 && catalogEntry.StftHopSize > 0 =>
                    (catalogEntry.StftDimT - 1) * catalogEntry.StftHopSize,
                NeuralOnnxContractKind.ScnetStft4Ch when catalogEntry.StftDimT > 0 && catalogEntry.StftHopSize > 0 =>
                    (catalogEntry.StftDimT - 1) * catalogEntry.StftHopSize,
                NeuralOnnxContractKind.WaveformStereo when session?.InputShapes != null && session.InputShapes.Count > 0 =>
                    ResolveFixedWaveformLength(session.InputShapes[0]),
                _ => 0
            };
            return new ResolvedModelContract {
                Kind = catalogEntry.ContractKind,
                FftSize = catalogEntry.StftFftSize,
                HopSize = catalogEntry.StftHopSize,
                DimF = catalogEntry.StftDimF,
                DimT = catalogEntry.StftDimT,
                Compensate = catalogEntry.MdxCompensate > 0f ? catalogEntry.MdxCompensate : 1f,
                InvertPrimaryStem = catalogEntry.InvertPrimaryStem,
                FixedChunkSamples = fixedChunk
            };
        }

        string in0 = session?.InputNames != null && session.InputNames.Count > 0 ? (session.InputNames[0] ?? "") : "";
        long[] s0 = session?.InputShapes != null && session.InputShapes.Count > 0 ? session.InputShapes[0] : null;

        if (string.Equals(in0, "stft_features", StringComparison.OrdinalIgnoreCase) ||
            (s0 != null && s0.Length == 3 && s0[2] == 4100)) {
            return new ResolvedModelContract {
                Kind = NeuralOnnxContractKind.MelBandRoFormerHostStft,
                FftSize = 2048,
                HopSize = 441,
                DimF = 1025,
                Compensate = 1f
            };
        }

        if (string.Equals(in0, "audio_values", StringComparison.OrdinalIgnoreCase) ||
            (s0 != null && s0.Length == 2)) {
            return new ResolvedModelContract {
                Kind = NeuralOnnxContractKind.FlashSr16kTo48k,
                Compensate = 1f
            };
        }

        if (s0 != null && s0.Length == 4 && s0[1] == 4 && s0[2] > 0) {
            int dimF = (int)s0[2];
            int dimT = (int)s0[3];
            if (dimT <= 0 || dimF == 2049) {
                return new ResolvedModelContract {
                    Kind = NeuralOnnxContractKind.ScnetStft4Ch,
                    FftSize = 4096,
                    HopSize = 1024,
                    DimF = dimF,
                    Compensate = 1f
                };
            }
            int inferredFft = dimF == 2048 ? 6144 : (dimF == 3072 ? 7680 : dimF * 2);
            return new ResolvedModelContract {
                Kind = NeuralOnnxContractKind.MdxNetStft4Ch,
                FftSize = inferredFft,
                HopSize = 1024,
                DimF = dimF,
                DimT = dimT,
                Compensate = 1f,
                FixedChunkSamples = (dimT - 1) * 1024
            };
        }

        return new ResolvedModelContract {
            Kind = NeuralOnnxContractKind.WaveformStereo,
            Compensate = 1f,
            FixedChunkSamples = ResolveFixedWaveformLength(s0)
        };
    }

    static int ResolveFixedWaveformLength(long[] shape) =>
        shape != null && shape.Length == 3 && shape[1] == 2 && shape[2] >= 4096 ? (int)shape[2] : 0;

    /// <summary>
    /// Evaluates one planar stereo chunk through an ONNX session using the model's exact tensor I/O contract:
    /// WaveformStereo (<c>[1, 2, T]</c>), SCNet STFT (<c>[1, 4, 2049, frames]</c>),
    /// UVR MDX-Net STFT (<c>[1, 4, dimF, dimT]</c>), Mel-Band RoFormer host-STFT (<c>[1, frames, 4100]</c>),
    /// or FlashSR 16 kHz -> 48 kHz bandwidth extension (<c>[2, n16k]</c>).
    /// </summary>
    internal static StereoStemBuffer[] EvaluateStereoChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames,
        ResolvedModelContract contract = default) {
        if (session == null || chunkLeft == null || chunkRight == null || chunkSamples <= 0) {
            return Array.Empty<StereoStemBuffer>();
        }
        int safeChunk = Math.Min(chunkSamples, Math.Min(chunkLeft.Length, chunkRight.Length));
        if (safeChunk <= 0) {
            return Array.Empty<StereoStemBuffer>();
        }
        if (contract.Kind == NeuralOnnxContractKind.Auto) {
            contract = ResolveContract(session, null, safeChunk);
        }

        return contract.Kind switch {
            NeuralOnnxContractKind.ScnetStft4Ch => EvaluateScnetStftChunk(session, chunkLeft, chunkRight, safeChunk, expectedStemNames, contract),
            NeuralOnnxContractKind.MdxNetStft4Ch => EvaluateMdxNetStftChunk(session, chunkLeft, chunkRight, safeChunk, expectedStemNames, contract),
            NeuralOnnxContractKind.MelBandRoFormerHostStft => EvaluateMelBandRoFormerHostStftChunk(session, chunkLeft, chunkRight, safeChunk, expectedStemNames, contract),
            NeuralOnnxContractKind.FlashSr16kTo48k => EvaluateFlashSrChunk(session, chunkLeft, chunkRight, safeChunk, expectedStemNames),
            _ => EvaluateWaveformStereoChunk(session, chunkLeft, chunkRight, safeChunk, expectedStemNames, contract.FixedChunkSamples)
        };
    }

    static StereoStemBuffer[] EvaluateWaveformStereoChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames,
        int fixedChunkSamples) {
        int evalSamples = fixedChunkSamples > 0 ? fixedChunkSamples : chunkSamples;
        int copyIn = Math.Min(chunkSamples, evalSamples);
        float[] inputData = new float[2 * evalSamples];
        Array.Copy(chunkLeft, 0, inputData, 0, copyIn);
        Array.Copy(chunkRight, 0, inputData, evalSamples, copyIn);

        NeuralTensorFloat inputTensor = new([1L, 2L, evalSamples], inputData);
        NeuralTensorFloat[] outputs = session.RunSingleInput(inputTensor);
        if (outputs == null || outputs.Length == 0) {
            return Array.Empty<StereoStemBuffer>();
        }

        List<StereoStemBuffer> parsed = [];
        int stemCounter = 0;

        for (int o = 0; o < outputs.Length; o++) {
            NeuralTensorFloat t = outputs[o];
            if (t == null || t.Data == null || t.Shape == null) {
                continue;
            }
            long[] shape = t.Shape;
            float[] data = t.Data;

            if (shape.Length == 4 && shape[0] == 1 && shape[1] > 0 && shape[2] == 2 && shape[3] > 0) {
                // [1, S, 2, T] (SCNet / HTDemucs / BS-RoFormer multi-stem)
                int stemCount = (int)shape[1];
                int tLen = (int)shape[3];
                if ((long)stemCount * 2L * tLen > data.Length) {
                    throw new InvalidDataException($"ONNX waveform output [1,{stemCount},2,{tLen}] truncated ({data.Length} floats).");
                }
                int copyLen = Math.Min(chunkSamples, tLen);
                for (int s = 0; s < stemCount; s++) {
                    string name = ResolveStemName(expectedStemNames, stemCounter++);
                    float[] l = new float[chunkSamples];
                    float[] r = new float[chunkSamples];
                    int baseIdx = s * 2 * tLen;
                    Array.Copy(data, baseIdx, l, 0, copyLen);
                    Array.Copy(data, baseIdx + tLen, r, 0, copyLen);
                    parsed.Add(new StereoStemBuffer(name, l, r));
                }
            } else if (shape.Length == 3 && shape[1] == 2 && shape[0] >= 1 && shape[2] > 0) {
                // [1, 2, T] (single stereo stem: Apollo, Mel-Band RoFormer) or [S, 2, T]
                int stemCount = (int)shape[0];
                int tLen = (int)shape[2];
                if ((long)stemCount * 2L * tLen > data.Length) {
                    throw new InvalidDataException($"ONNX waveform output [{stemCount},2,{tLen}] truncated ({data.Length} floats).");
                }
                int copyLen = Math.Min(chunkSamples, tLen);
                for (int s = 0; s < stemCount; s++) {
                    string name = ResolveStemName(expectedStemNames, stemCounter++);
                    float[] l = new float[chunkSamples];
                    float[] r = new float[chunkSamples];
                    int baseIdx = s * 2 * tLen;
                    Array.Copy(data, baseIdx, l, 0, copyLen);
                    Array.Copy(data, baseIdx + tLen, r, 0, copyLen);
                    parsed.Add(new StereoStemBuffer(name, l, r));
                }
            } else if (shape.Length == 2 && shape[0] == 2 && shape[1] > 0) {
                // [2, T]
                int tLen = (int)shape[1];
                if (2L * tLen > data.Length) {
                    throw new InvalidDataException($"ONNX waveform output [2,{tLen}] truncated ({data.Length} floats).");
                }
                int copyLen = Math.Min(chunkSamples, tLen);
                string name = ResolveStemName(expectedStemNames, stemCounter++);
                float[] l = new float[chunkSamples];
                float[] r = new float[chunkSamples];
                Array.Copy(data, 0, l, 0, copyLen);
                Array.Copy(data, tLen, r, 0, copyLen);
                parsed.Add(new StereoStemBuffer(name, l, r));
            } else if (data.Length >= chunkSamples) {
                // Fallback mono [1, T] or [1, 1, T]
                int copyLen = Math.Min(chunkSamples, data.Length);
                string name = ResolveStemName(expectedStemNames, stemCounter++);
                float[] l = new float[chunkSamples];
                float[] r = new float[chunkSamples];
                Array.Copy(data, 0, l, 0, copyLen);
                Array.Copy(data, 0, r, 0, copyLen);
                parsed.Add(new StereoStemBuffer(name, l, r));
            }
        }

        return parsed.ToArray();
    }

    static StereoStemBuffer[] EvaluateScnetStftChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames,
        ResolvedModelContract contract) {
        int nFft = contract.FftSize > 0 ? contract.FftSize : 4096;
        int hop = contract.HopSize > 0 ? contract.HopSize : 1024;
        int dimF = contract.DimF > 0 ? contract.DimF : (nFft / 2 + 1);

        // When the exported SCNet ONNX graph bakes fixed DFT MatMul weights (e.g. bgkb/scnet_onnx [474, 238]),
        // enforce exact target frame count DimT via paddedSamples = (DimT - 1) * hop so STFT yields 1 + (DimT - 1) = DimT frames.
        int paddedSamples;
        if (contract.DimT > 0) {
            paddedSamples = (contract.DimT - 1) * hop;
        } else {
            int pad = hop - (chunkSamples % hop);
            if (((chunkSamples + pad) / hop) % 2 == 0) {
                pad += hop;
            }
            paddedSamples = chunkSamples + pad;
        }
        float[] padL = new float[paddedSamples];
        float[] padR = new float[paddedSamples];
        int copyIn = Math.Min(chunkSamples, paddedSamples);
        Array.Copy(chunkLeft, padL, copyIn);
        Array.Copy(chunkRight, padR, copyIn);

        int frames = MixedRadixFft.ComputeComplexStft(padL, nFft, hop, dimF, out float[] lRe, out float[] lIm);
        MixedRadixFft.ComputeComplexStft(padR, nFft, hop, dimF, out float[] rRe, out float[] rIm);

        float stftNorm = 1f / MathF.Sqrt(nFft);
        int planeSize = dimF * frames;
        float[] inputData = new float[4 * planeSize];
        for (int i = 0; i < planeSize; i++) {
            inputData[i] = lRe[i] * stftNorm;
            inputData[planeSize + i] = lIm[i] * stftNorm;
            inputData[2 * planeSize + i] = rRe[i] * stftNorm;
            inputData[3 * planeSize + i] = rIm[i] * stftNorm;
        }

        NeuralTensorFloat[] outputs = session.RunSingleInput(new NeuralTensorFloat([1L, 4L, dimF, frames], inputData));
        if (outputs == null || outputs.Length == 0 || outputs[0]?.Data == null || outputs[0].Shape == null) {
            return Array.Empty<StereoStemBuffer>();
        }

        NeuralTensorFloat tOut = outputs[0];
        long[] sShape = tOut.Shape;
        float[] data = tOut.Data;
        int stemCount = sShape.Length == 5 ? (int)sShape[1] : 4;
        int outCh = sShape.Length == 5 ? (int)sShape[2] : 4;
        int outBins = sShape.Length == 5 ? (int)sShape[3] : dimF;
        int outFrames = sShape.Length == 5 ? (int)sShape[4] : frames;
        if (stemCount <= 0 || outCh < 4 || outBins <= 0 || outFrames <= 0) {
            throw new InvalidDataException($"Unexpected SCNet STFT output shape [{string.Join(",", sShape)}].");
        }
        long outPlaneLong = (long)outBins * outFrames;
        long totalNeeded = (long)stemCount * outCh * outPlaneLong;
        if (totalNeeded > data.Length) {
            throw new InvalidDataException($"SCNet STFT output buffer underflow: expected {totalNeeded} floats for [{string.Join(",", sShape)}], got {data.Length}.");
        }
        int outPlane = (int)outPlaneLong;
        float istftUnnorm = MathF.Sqrt(nFft);

        float[] stemRe = new float[outPlane];
        float[] stemIm = new float[outPlane];
        StereoStemBuffer[] stems = new StereoStemBuffer[stemCount];

        for (int s = 0; s < stemCount; s++) {
            int stemBase = s * outCh * outPlane;
            for (int i = 0; i < outPlane; i++) {
                stemRe[i] = data[stemBase + i] * istftUnnorm;
                stemIm[i] = data[stemBase + outPlane + i] * istftUnnorm;
            }
            float[] outL = MixedRadixFft.ComputeComplexIstft(stemRe, stemIm, outFrames, nFft, hop, outBins, chunkSamples);

            for (int i = 0; i < outPlane; i++) {
                stemRe[i] = data[stemBase + 2 * outPlane + i] * istftUnnorm;
                stemIm[i] = data[stemBase + 3 * outPlane + i] * istftUnnorm;
            }
            float[] outR = MixedRadixFft.ComputeComplexIstft(stemRe, stemIm, outFrames, nFft, hop, outBins, chunkSamples);

            stems[s] = new StereoStemBuffer(ResolveStemName(expectedStemNames, s), outL, outR);
        }

        return stems;
    }

    static StereoStemBuffer[] EvaluateMdxNetStftChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames,
        ResolvedModelContract contract) {
        int nFft = contract.FftSize > 0 ? contract.FftSize : 6144;
        int hop = contract.HopSize > 0 ? contract.HopSize : 1024;
        int dimF = contract.DimF > 0 ? contract.DimF : 2048;
        int dimT = contract.DimT > 0 ? contract.DimT : 256;
        int mdxChunkSamples = (dimT - 1) * hop;

        float[] srcL = chunkLeft;
        float[] srcR = chunkRight;
        if (chunkSamples != mdxChunkSamples) {
            srcL = new float[mdxChunkSamples];
            srcR = new float[mdxChunkSamples];
            int copy = Math.Min(chunkSamples, mdxChunkSamples);
            Array.Copy(chunkLeft, srcL, copy);
            Array.Copy(chunkRight, srcR, copy);
        }

        int frames = MixedRadixFft.ComputeComplexStft(srcL, nFft, hop, dimF, out float[] lRe, out float[] lIm);
        MixedRadixFft.ComputeComplexStft(srcR, nFft, hop, dimF, out float[] rRe, out float[] rIm);

        int plane = dimF * dimT;
        int copyFrames = Math.Min(frames, dimT);
        float[] inputData = new float[4 * plane];
        for (int b = 0; b < dimF; b++) {
            int srcRow = b * frames;
            int dstRow = b * dimT;
            for (int f = 0; f < copyFrames; f++) {
                inputData[dstRow + f] = lRe[srcRow + f];
                inputData[plane + dstRow + f] = lIm[srcRow + f];
                inputData[2 * plane + dstRow + f] = rRe[srcRow + f];
                inputData[3 * plane + dstRow + f] = rIm[srcRow + f];
            }
        }

        NeuralTensorFloat[] outputs = session.RunSingleInput(new NeuralTensorFloat([1L, 4L, dimF, dimT], inputData));
        if (outputs == null || outputs.Length == 0 || outputs[0]?.Data == null) {
            return Array.Empty<StereoStemBuffer>();
        }

        float[] outData = outputs[0].Data;
        if (outData.Length < 4L * plane) {
            throw new InvalidDataException($"MDX-Net STFT output buffer underflow: expected {4L * plane} floats ([1,4,{dimF},{dimT}]), got {outData.Length}.");
        }
        float[] outLRe = new float[plane];
        float[] outLIm = new float[plane];
        float[] outRRe = new float[plane];
        float[] outRIm = new float[plane];
        Array.Copy(outData, 0, outLRe, 0, plane);
        Array.Copy(outData, plane, outLIm, 0, plane);
        Array.Copy(outData, 2 * plane, outRRe, 0, plane);
        Array.Copy(outData, 3 * plane, outRIm, 0, plane);

        float[] rawL = MixedRadixFft.ComputeComplexIstft(outLRe, outLIm, dimT, nFft, hop, dimF, chunkSamples);
        float[] rawR = MixedRadixFft.ComputeComplexIstft(outRRe, outRIm, dimT, nFft, hop, dimF, chunkSamples);

        float comp = contract.Compensate > 0f ? contract.Compensate : 1f;
        float[] modelL = new float[chunkSamples];
        float[] modelR = new float[chunkSamples];
        for (int i = 0; i < chunkSamples; i++) {
            modelL[i] = rawL[i] * comp;
            modelR[i] = rawR[i] * comp;
        }

        if (!contract.InvertPrimaryStem) {
            return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), modelL, modelR)];
        }

        float[] invL = new float[chunkSamples];
        float[] invR = new float[chunkSamples];
        for (int i = 0; i < chunkSamples; i++) {
            invL[i] = chunkLeft[i] - modelL[i];
            invR[i] = chunkRight[i] - modelR[i];
        }

        if (expectedStemNames != null && expectedStemNames.Length >= 2) {
            return [
                new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), invL, invR),
                new StereoStemBuffer(ResolveStemName(expectedStemNames, 1), modelL, modelR)
            ];
        }
        return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), invL, invR)];
    }

    static StereoStemBuffer[] EvaluateMelBandRoFormerHostStftChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames,
        ResolvedModelContract contract) {
        const int nFft = 2048;
        const int hop = 441;
        const int bins = 1025;
        const int width = 4100; // (2 * bins) * 2 (re, im)

        int frames = MixedRadixFft.ComputeComplexStft(chunkLeft, nFft, hop, bins, out float[] lRe, out float[] lIm);
        MixedRadixFft.ComputeComplexStft(chunkRight, nFft, hop, bins, out float[] rRe, out float[] rIm);

        float[] inputData = new float[frames * width];
        Parallel.For(0, frames, t => {
            int rowBase = t * width;
            for (int f = 0; f < bins; f++) {
                int stftIdx = f * frames + t;
                int baseL = rowBase + (2 * f) * 2;
                int baseR = rowBase + (2 * f + 1) * 2;
                inputData[baseL] = lRe[stftIdx];
                inputData[baseL + 1] = lIm[stftIdx];
                inputData[baseR] = rRe[stftIdx];
                inputData[baseR + 1] = rIm[stftIdx];
            }
        });

        NeuralTensorFloat[] outputs = session.RunSingleInput(new NeuralTensorFloat([1L, frames, width], inputData));
        if (outputs == null || outputs.Length == 0 || outputs[0]?.Data == null) {
            return Array.Empty<StereoStemBuffer>();
        }

        // Output mask [1, 1, 2050, frames, 2] -> packed bin = 2*freq + ch
        float[] mask = outputs[0].Data;
        long requiredMaskElems = (long)bins * 2L * frames * 2L;
        if (mask.Length < requiredMaskElems) {
            throw new InvalidDataException($"Mel-Band RoFormer output mask underflow: expected {requiredMaskElems} floats, got {mask.Length}.");
        }
        float[] leadLRe = new float[bins * frames];
        float[] leadLIm = new float[bins * frames];
        float[] leadRRe = new float[bins * frames];
        float[] leadRIm = new float[bins * frames];

        Parallel.For(0, bins, f => {
            int ch0Row = (2 * f) * frames;
            int ch1Row = (2 * f + 1) * frames;
            for (int t = 0; t < frames; t++) {
                int stftIdx = f * frames + t;
                int m0 = (ch0Row + t) * 2;
                float m0Re = mask[m0];
                float m0Im = mask[m0 + 1];
                leadLRe[stftIdx] = lRe[stftIdx] * m0Re - lIm[stftIdx] * m0Im;
                leadLIm[stftIdx] = lRe[stftIdx] * m0Im + lIm[stftIdx] * m0Re;

                int m1 = (ch1Row + t) * 2;
                float m1Re = mask[m1];
                float m1Im = mask[m1 + 1];
                leadRRe[stftIdx] = rRe[stftIdx] * m1Re - rIm[stftIdx] * m1Im;
                leadRIm[stftIdx] = rRe[stftIdx] * m1Im + rIm[stftIdx] * m1Re;
            }
        });

        float[] leadL = MixedRadixFft.ComputeComplexIstft(leadLRe, leadLIm, frames, nFft, hop, bins, chunkSamples);
        float[] leadR = MixedRadixFft.ComputeComplexIstft(leadRRe, leadRIm, frames, nFft, hop, bins, chunkSamples);

        float[] backL = new float[chunkSamples];
        float[] backR = new float[chunkSamples];
        for (int i = 0; i < chunkSamples; i++) {
            backL[i] = chunkLeft[i] - leadL[i];
            backR[i] = chunkRight[i] - leadR[i];
        }

        if (expectedStemNames != null && expectedStemNames.Length >= 2) {
            return [
                new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), leadL, leadR),
                new StereoStemBuffer(ResolveStemName(expectedStemNames, 1), backL, backR)
            ];
        }
        return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), leadL, leadR)];
    }

    static StereoStemBuffer[] EvaluateFlashSrChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames) {
        KaiserSincResampler.ResampleStereo(chunkLeft, chunkRight, 48000, 16000, out float[] l16, out float[] r16);
        int n16 = l16.Length;
        if (n16 < 128) {
            return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), chunkLeft, chunkRight)];
        }

        float[] inputData = new float[2 * n16];
        Array.Copy(l16, 0, inputData, 0, n16);
        Array.Copy(r16, 0, inputData, n16, n16);

        NeuralTensorFloat[] outputs = session.RunSingleInput(new NeuralTensorFloat([2L, n16], inputData));
        if (outputs == null || outputs.Length == 0 || outputs[0]?.Data == null || outputs[0].Shape == null) {
            return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), chunkLeft, chunkRight)];
        }

        long[] outShape = outputs[0].Shape;
        float[] outData = outputs[0].Data;
        int outLen = outShape.Length >= 2 ? (int)outShape[outShape.Length - 1] : outData.Length / 2;
        if (outLen <= 0 || outData.Length < 2 * outLen) {
            return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), chunkLeft, chunkRight)];
        }

        float[] srL = new float[chunkSamples];
        float[] srR = new float[chunkSamples];
        int copyLen = Math.Min(chunkSamples, outLen);
        Array.Copy(outData, 0, srL, 0, copyLen);
        Array.Copy(outData, outLen, srR, 0, copyLen);

        // Spectral crossover at 7.2 kHz (6.8–7.6 kHz cosine transition): keep native 48 kHz low/mid band intact and restore HF air from FlashSR
        float[] blendL = BlendCrossoverHighBand(chunkLeft, srL, 48000, 7200f, 400f);
        float[] blendR = BlendCrossoverHighBand(chunkRight, srR, 48000, 7200f, 400f);
        return [new StereoStemBuffer(ResolveStemName(expectedStemNames, 0), blendL, blendR)];
    }

    static float[] BlendCrossoverHighBand(float[] lowNative, float[] highRestored, int sampleRate, float crossoverHz, float halfTransitionHz) {
        const int nFft = 2048;
        const int hop = 512;
        const int bins = nFft / 2 + 1;
        int frames = MixedRadixFft.ComputeComplexStft(lowNative, nFft, hop, bins, out float[] loRe, out float[] loIm);
        MixedRadixFft.ComputeComplexStft(highRestored, nFft, hop, bins, out float[] hiRe, out float[] hiIm);

        float[] outRe = new float[bins * frames];
        float[] outIm = new float[bins * frames];
        float binHz = sampleRate / (float)nFft;
        float fLow = crossoverHz - halfTransitionHz;
        float fHigh = crossoverHz + halfTransitionHz;

        for (int b = 0; b < bins; b++) {
            float freq = b * binHz;
            float wHigh = freq <= fLow
                ? 0f
                : (freq >= fHigh ? 1f : 0.5f * (1f - MathF.Cos(MathF.PI * (freq - fLow) / (fHigh - fLow))));
            float wLow = 1f - wHigh;
            int row = b * frames;
            for (int f = 0; f < frames; f++) {
                int idx = row + f;
                outRe[idx] = loRe[idx] * wLow + hiRe[idx] * wHigh;
                outIm[idx] = loIm[idx] * wLow + hiIm[idx] * wHigh;
            }
        }
        return MixedRadixFft.ComputeComplexIstft(outRe, outIm, frames, nFft, hop, bins, lowNative.Length);
    }

    static string ResolveStemName(string[] names, int index) {
        if (names != null && index >= 0 && index < names.Length && !string.IsNullOrWhiteSpace(names[index])) {
            return names[index];
        }
        return $"stem_{index}";
    }

    static float[] DownmixMono(StereoStemBuffer stem) {
        float[] mono = new float[stem.Length];
        for (int i = 0; i < stem.Length; i++) {
            mono[i] = 0.5f * (stem.Left[i] + stem.Right[i]);
        }
        return mono;
    }
}
