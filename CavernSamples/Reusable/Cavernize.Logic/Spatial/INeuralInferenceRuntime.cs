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

    /// <summary>Creates a new ONNX inference session for <paramref name="modelPath"/> using <paramref name="preferredProvider"/>.</summary>
    INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 4);

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
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
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

            StereoStemBuffer[] rawModelStems = MvsepAntiArtifactPipeline.ProcessTrack(
                inL,
                inR,
                (chunkL, chunkR, len) => EvaluateStereoChunk(session, chunkL, chunkR, len, stemNames),
                passOpt,
                p => {
                    double totalP = (modelIdx + p) / modelCount;
                    double scale = (modelCount > 1 || needsResample) ? 0.90 : 1.0;
                    onProgress?.Invoke(totalP * scale, $"ONNX [{session.ExecutionProvider}] {modelLabel}: {p:0%}");
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
