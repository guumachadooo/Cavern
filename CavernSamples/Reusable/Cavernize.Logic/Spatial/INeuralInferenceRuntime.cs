using System.Diagnostics;

using Cavern.Neural;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Runtime status metadata returned by <see cref="INeuralInferenceRuntime"/>.
/// </summary>
public sealed class NeuralRuntimeInfo {
    /// <summary>True when the native ONNX Runtime library loaded and initialized successfully.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Loaded native library path (<c>FilesDir/bin/onnxruntime/libonnxruntime.so</c>).</summary>
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

    /// <summary>Ordered model output tensor names.</summary>
    IReadOnlyList<string> OutputNames { get; }

    /// <summary>Executes synchronous inference on the primary input tensor and returns all output tensors.</summary>
    NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor);
}

/// <summary>
/// Platform-implemented managed ONNX Runtime host (implemented on Android via native <c>libonnxruntime.so</c>
/// in <c>FilesDir/bin/onnxruntime/</c> with QNN HTP / XNNPACK / CPU execution provider cascade).
/// </summary>
public interface INeuralInferenceRuntime : IDisposable {
    /// <summary>Probes and returns current native runtime availability, version, and active EP capabilities.</summary>
    NeuralRuntimeInfo GetRuntimeInfo();

    /// <summary>Creates a new ONNX inference session for <paramref name="modelPath"/> using <paramref name="preferredProvider"/>.</summary>
    INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 4);

    /// <summary>Resolves the local file path of an installed catalog model, or null if not yet downloaded.</summary>
    string ResolveInstalledModelPath(string modelId);
}

/// <summary>
/// High-level Slot 2B offline neural processor combining <see cref="INeuralInferenceRuntime"/> with
/// <see cref="MvsepAntiArtifactPipeline"/> (2x–8x overlap-add, TTA, <c>avg_wave</c>/<c>min_fft</c> ensembling,
/// Wiener <c>phase_fix</c>, and exact <c>residual = mix - sum(stems)</c> energy conservation).
/// </summary>
public static class NeuralAudioOfflinePipeline {
    /// <summary>
    /// Separates a stereo waveform using one or more ONNX checkpoints and ensembles them using
    /// <paramref name="config"/>.<see cref="NeuralAudioConfig.EnsembleMode"/> (<c>avg_wave</c>, <c>min_fft</c>, or <c>avg_fft</c>).
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
        CancellationToken token = default) {
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
        StereoStemBuffer[][] perModelResults = new StereoStemBuffer[modelCount][];

        // When ensembling multiple models, apply phase_fix & conservation ONCE after ensembling so stems stay exact.
        MvsepPipelineOptions baseOpt = safeCfg.ToMvsepOptions(chunkSamples);
        if (modelCount > 1) {
            baseOpt.EnablePhaseFix = false;
            baseOpt.EnforceEnergyConservation = false;
        }

        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        for (int m = 0; m < modelCount; m++) {
            token.ThrowIfCancellationRequested();
            string path = modelPaths[m];
            string[] stemNames = expectedStemNamesPerModel != null && m < expectedStemNamesPerModel.Count
                ? expectedStemNamesPerModel[m]
                : ["vocals"];

            using INeuralSession session = runtime.CreateSession(path, safeCfg.PreferredProvider, threads);
            int modelIdx = m;
            string modelLabel = Path.GetFileNameWithoutExtension(path);

            perModelResults[m] = MvsepAntiArtifactPipeline.ProcessTrack(
                mixLeft,
                mixRight,
                (chunkL, chunkR, len) => EvaluateStereoChunk(session, chunkL, chunkR, len, stemNames),
                baseOpt,
                p => {
                    double totalP = (modelIdx + p) / modelCount;
                    onProgress?.Invoke(totalP * (modelCount > 1 ? 0.90 : 1.0), $"ONNX [{session.ExecutionProvider}] {modelLabel}: {p:0%}");
                },
                token);
        }

        StereoStemBuffer[] finalStems;
        if (modelCount == 1) {
            finalStems = perModelResults[0];
        } else {
            onProgress?.Invoke(0.92, $"MVSEP Ensemble ({safeCfg.EnsembleMode})...");
            int stemCount = perModelResults[0].Length;
            finalStems = new StereoStemBuffer[stemCount];
            for (int s = 0; s < stemCount; s++) {
                StereoStemBuffer[] candidates = new StereoStemBuffer[modelCount];
                for (int m = 0; m < modelCount; m++) {
                    candidates[m] = s < perModelResults[m].Length ? perModelResults[m][s] : perModelResults[0][s];
                }
                finalStems[s] = MvsepAntiArtifactPipeline.EnsembleStems(
                    candidates,
                    ensembleWeights,
                    safeCfg.EnsembleMode,
                    baseOpt.StftSize,
                    candidates[0].Name,
                    token);
            }

            if (safeCfg.EnablePhaseFix) {
                MvsepAntiArtifactPipeline.ApplyPhaseFixWiener(mixLeft, mixRight, finalStems, baseOpt.StftSize, baseOpt.WienerExponent, token);
            }
            if (safeCfg.EnforceEnergyConservation) {
                StereoStemBuffer res = MvsepAntiArtifactPipeline.ComputeConservedResidual(mixLeft, mixRight, finalStems, "residual");
                StereoStemBuffer[] withRes = new StereoStemBuffer[finalStems.Length + 1];
                Array.Copy(finalStems, withRes, finalStems.Length);
                withRes[finalStems.Length] = res;
                finalStems = withRes;
            }
            onProgress?.Invoke(1.0, "MVSEP Separation Complete");
        }

        sw.Stop();
        double audioSec = sampleRate > 0 ? mixLeft.Length / (double)sampleRate : 1.0;
        float rtf = audioSec > 1e-3 ? (float)(sw.Elapsed.TotalSeconds / audioSec) : 0f;
        return (finalStems, rtf);
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
        CancellationToken token = default) {
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
            token);

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
        bool derivedFromLosslessTwin = false) {
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
            DerivedFromLosslessTwin = derivedFromLosslessTwin
        };
    }

    /// <summary>
    /// Evaluates one planar stereo chunk through an ONNX session, normalizing common audio model tensor layouts:
    /// <list type="bullet">
    ///   <item>Input: <c>[1, 2, T]</c> planar float32 (SCNet, Mel-Band RoFormer, BS-RoFormer, Apollo, AP-BWE).</item>
    ///   <item>Output layouts supported: <c>[1, S, 2, T]</c>, <c>[S, 2, T]</c>, <c>[1, 2, T]</c>, <c>[2, T]</c>, or multiple <c>[1, 2, T]</c> outputs.</item>
    /// </list>
    /// </summary>
    internal static StereoStemBuffer[] EvaluateStereoChunk(
        INeuralSession session,
        float[] chunkLeft,
        float[] chunkRight,
        int chunkSamples,
        string[] expectedStemNames) {
        float[] inputData = new float[2 * chunkSamples];
        Array.Copy(chunkLeft, 0, inputData, 0, chunkSamples);
        Array.Copy(chunkRight, 0, inputData, chunkSamples, chunkSamples);

        NeuralTensorFloat inputTensor = new([1L, 2L, chunkSamples], inputData);
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

            if (shape.Length == 4 && shape[0] == 1 && shape[2] == 2) {
                // [1, S, 2, T] (SCNet / BS-RoFormer multi-stem)
                int stemCount = (int)shape[1];
                int tLen = (int)shape[3];
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
            } else if (shape.Length == 3 && shape[1] == 2 && shape[0] >= 1) {
                // [1, 2, T] (single stereo stem: Mel-Band RoFormer Vocals/Dereverb/Apollo) or [S, 2, T]
                int stemCount = (int)shape[0];
                int tLen = (int)shape[2];
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
            } else if (shape.Length == 2 && shape[0] == 2) {
                // [2, T]
                int tLen = (int)shape[1];
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
