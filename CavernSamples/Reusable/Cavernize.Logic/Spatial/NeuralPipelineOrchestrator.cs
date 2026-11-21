using System.Security.Cryptography;
using System.Text;

using Cavern.Neural;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Telemetry snapshot describing the Slot 7B disk-backed separated track cache (<c>CacheDir/neural_tracks/</c>).
/// </summary>
public readonly struct NeuralTrackCacheStats {
    /// <summary>Number of cached <see cref="NeuralSeparatedTrack"/> bundles currently stored on disk.</summary>
    public int EntryCount { get; init; }

    /// <summary>Total disk bytes occupied by cached separated tracks.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Configured maximum LRU cache cap in bytes.</summary>
    public long MaxBytes { get; init; }
}

/// <summary>
/// Slot 7B disk-backed LRU cache contract for pre-separated <see cref="NeuralSeparatedTrack"/> stem bundles,
/// shared by Slot 2B (live 3D stems &amp; Spatial Karaoke), Slot 2D (Song Vocal Mix DNA), and Slot 8 (Offline Studio Mastering).
/// </summary>
public interface INeuralTrackCache {
    /// <summary>Returns current entry count, occupied bytes, and configured LRU cap.</summary>
    NeuralTrackCacheStats GetStats();

    /// <summary>Attempts to load a cached <see cref="NeuralSeparatedTrack"/> matching <paramref name="cacheKey"/>.</summary>
    bool TryLoad(string cacheKey, out NeuralSeparatedTrack track, CancellationToken token = default);

    /// <summary>Atomically writes <paramref name="track"/> under <paramref name="cacheKey"/> and enforces the LRU disk budget.</summary>
    void Save(string cacheKey, NeuralSeparatedTrack track, CancellationToken token = default);

    /// <summary>Enforces the specified maximum cache size in bytes by evicting least-recently-used entries.</summary>
    void EvictToLimit(long maxBytes);

    /// <summary>Deletes all cached separated track bundles on disk.</summary>
    void Clear();
}

/// <summary>
/// Slot 7A model discovery registry uniting built-in <see cref="NeuralModelCatalog"/> checkpoints and
/// user-imported custom <c>.onnx</c> files on disk.
/// </summary>
public interface INeuralModelRegistry {
    /// <summary>Returns all registered models (curated catalog + custom imported <c>.onnx</c> models).</summary>
    IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels();

    /// <summary>Returns all currently installed models on disk (curated + custom).</summary>
    IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels();

    /// <summary>Resolves the absolute disk path for <paramref name="modelId"/>, or null/empty if not installed.</summary>
    string ResolveInstalledModelPath(string modelId);
}

/// <summary>
/// Single resolved stage inside a <see cref="NeuralExecutionPlan"/> (either a single model or a semantic multi-model ensemble).
/// </summary>
public sealed class NeuralExecutionStep {
    /// <summary>Functional neural task performed by this step.</summary>
    public NeuralModelTask Task { get; init; }

    /// <summary>Ordered model descriptors executing in this step (2+ entries trigger MVSEP semantic ensembling).</summary>
    public IReadOnlyList<NeuralModelCatalogEntry> Models { get; init; } = Array.Empty<NeuralModelCatalogEntry>();

    /// <summary>Resolved absolute filesystem paths matching <see cref="Models"/>.</summary>
    public IReadOnlyList<string> ModelPaths { get; init; } = Array.Empty<string>();

    /// <summary>Relative ensemble weights matching <see cref="Models"/>.</summary>
    public float[] Weights { get; init; } = Array.Empty<float>();

    /// <summary>True when this step ensembles 2 or more installed models.</summary>
    public bool IsEnsemble => Models.Count > 1;
}

/// <summary>
/// Immutable execution plan resolved by Slot 7A <see cref="NeuralPipelineOrchestrator"/> for a given goal and installed checkpoints.
/// </summary>
public sealed class NeuralExecutionPlan {
    /// <summary>High-level pipeline goal this plan serves.</summary>
    public NeuralPipelineGoal Goal { get; init; }

    /// <summary>Orchestration mode used to construct this plan (<c>Auto</c> or <c>Manual</c>).</summary>
    public NeuralOrchestrationMode Mode { get; init; }

    /// <summary>Ordered chain of executable neural steps (e.g. Separation -> Debleed -> Karaoke/Dereverb -> Restore).</summary>
    public IReadOnlyList<NeuralExecutionStep> Steps { get; init; } = Array.Empty<NeuralExecutionStep>();

    /// <summary>Deterministic fingerprint covering model IDs, file sizes, and MVSEP options for Slot 7B cache keying.</summary>
    public string PlanSignature { get; init; } = string.Empty;

    /// <summary>Compact human-readable chain description (e.g. "Auto: BS-RoFormer 6-Stem + Debleed").</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>True when at least one executable step with installed model weights was resolved.</summary>
    public bool IsRunnable => Steps.Count > 0;

    /// <summary>Returns the first step matching any of <paramref name="tasks"/>, or null if absent.</summary>
    public NeuralExecutionStep FindStep(params NeuralModelTask[] tasks) {
        if (tasks == null || tasks.Length == 0) {
            return null;
        }
        for (int i = 0; i < Steps.Count; i++) {
            for (int t = 0; t < tasks.Length; t++) {
                if (Steps[i].Task == tasks[t]) {
                    return Steps[i];
                }
            }
        }
        return null;
    }
}

/// <summary>
/// Slot 7A Dynamic Neural Pipeline Orchestrator:
/// resolves installed checkpoints across curated + custom <c>.onnx</c> models in <c>Auto</c> or <c>Manual</c> mode,
/// supports multi-model semantic ensembling and stage chaining (Separation -> Debleed -> Karaoke/Dereverb -> Restoration),
/// and integrates directly with Slot 7B <see cref="INeuralTrackCache"/> for instant disk-cached reuse across Slots 2B, 2D, and 8.
/// </summary>
public sealed class NeuralPipelineOrchestrator {
    readonly INeuralInferenceRuntime runtime;
    readonly INeuralModelRegistry registry;
    readonly INeuralTrackCache trackCache;

    /// <summary>Creates a Slot 7A orchestrator bound to the platform runtime, model registry, and optional disk track cache.</summary>
    public NeuralPipelineOrchestrator(
        INeuralInferenceRuntime runtime,
        INeuralModelRegistry registry,
        INeuralTrackCache trackCache = null) {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.trackCache = trackCache;
    }

    /// <summary>Optional Slot 7B disk-backed separated track cache.</summary>
    public INeuralTrackCache TrackCache => trackCache;

    /// <summary>
    /// Builds a deterministic <see cref="NeuralExecutionPlan"/> for <paramref name="goal"/> given the user's
    /// <paramref name="config"/> and the models currently installed on disk.
    /// </summary>
    public NeuralExecutionPlan BuildPlan(NeuralPipelineGoal goal, NeuralAudioConfig config) {
        NeuralAudioConfig safeCfg = (config ?? NeuralAudioConfig.Default).Sanitize();
        IReadOnlyList<NeuralModelCatalogEntry> installed = registry.GetInstalledModels() ?? Array.Empty<NeuralModelCatalogEntry>();
        Dictionary<string, NeuralModelCatalogEntry> installedById = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < installed.Count; i++) {
            installedById[installed[i].Id] = installed[i];
        }

        List<NeuralExecutionStep> steps = [];
        switch (goal) {
            case NeuralPipelineGoal.StemSeparation:
                AddStepIfValid(steps, ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: false));
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, safeCfg, installed, installedById, allowEnsemble: false));
                break;

            case NeuralPipelineGoal.SpatialKaraoke: {
                NeuralExecutionStep karaokeStep = ResolveSingleTaskStep(NeuralModelTask.KaraokeLeadBacking, safeCfg, installed, installedById, allowEnsemble: true);
                if (karaokeStep != null) {
                    steps.Add(karaokeStep);
                } else {
                    AddStepIfValid(steps, ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: true));
                }
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, safeCfg, installed, installedById, allowEnsemble: false));
                break;
            }

            case NeuralPipelineGoal.VocalMixDna:
                AddStepIfValid(steps, ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: true));
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.Dereverb, safeCfg, installed, installedById, allowEnsemble: false));
                break;

            case NeuralPipelineGoal.LosslessResidualRestore:
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.CodecRestoreApollo, safeCfg, installed, installedById, allowEnsemble: false));
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.BandwidthExtension, safeCfg, installed, installedById, allowEnsemble: false));
                break;
        }

        string signature = ComputePlanSignature(goal, safeCfg, steps);
        string summary = FormatPlanSummary(safeCfg.OrchestrationMode, steps);
        return new NeuralExecutionPlan {
            Goal = goal,
            Mode = safeCfg.OrchestrationMode,
            Steps = steps,
            PlanSignature = signature,
            Summary = summary
        };
    }

    /// <summary>Computes a deterministic SHA-256 track cache key uniting audio identity, sample rate, and plan signature.</summary>
    public static string ComputeTrackCacheKey(
        string sourceTrackKey,
        long fileSizeBytes,
        long lastModifiedTicks,
        int sampleRate,
        string planSignature) {
        string raw = $"{sourceTrackKey ?? ""}|{fileSizeBytes}|{lastModifiedTicks}|{sampleRate}|{planSignature ?? ""}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();
    }

    /// <summary>
    /// Executes the full resolved <see cref="NeuralExecutionPlan"/> on a stereo track (using <see cref="INeuralTrackCache"/>
    /// for instant hits and atomic persistence on misses), returning the resulting <see cref="NeuralSeparatedTrack"/> and RTF.
    /// </summary>
    public (NeuralSeparatedTrack Track, float RealTimeFactor, bool FromCache) ExecuteTrackPipeline(
        string sourceTrackKey,
        long fileSizeBytes,
        long lastModifiedTicks,
        float[] mixLeft,
        float[] mixRight,
        int sampleRate,
        NeuralPipelineGoal goal,
        NeuralAudioConfig config,
        bool derivedFromLosslessTwin = false,
        Action<double, string> onProgress = null,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(mixLeft);
        ArgumentNullException.ThrowIfNull(mixRight);

        NeuralAudioConfig safeCfg = (config ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan plan = BuildPlan(goal, safeCfg);
        if (!plan.IsRunnable) {
            throw new InvalidOperationException("No installed ONNX model matches the requested neural pipeline goal.");
        }

        string cacheKey = ComputeTrackCacheKey(sourceTrackKey, fileSizeBytes, lastModifiedTicks, sampleRate, plan.PlanSignature);
        if (trackCache != null && trackCache.TryLoad(cacheKey, out NeuralSeparatedTrack cached, token)) {
            onProgress?.Invoke(1.0, $"Cache Hit ({plan.Summary})");
            return (cached, 0f, true);
        }

        float totalRtf = 0f;
        StereoStemBuffer[] stems = Array.Empty<StereoStemBuffer>();
        DereverbDecompositionResult dereverbResult = null;
        int totalSteps = plan.Steps.Count;

        for (int s = 0; s < totalSteps; s++) {
            token.ThrowIfCancellationRequested();
            NeuralExecutionStep step = plan.Steps[s];
            double stepStart = s / (double)totalSteps;
            double stepSpan = 1.0 / totalSteps;

            if (step.Task == NeuralModelTask.StemSeparation4 ||
                step.Task == NeuralModelTask.StemSeparation6 ||
                step.Task == NeuralModelTask.VocalSeparation ||
                step.Task == NeuralModelTask.KaraokeLeadBacking) {
                (stems, float stepRtf) = RunStepSeparation(step, mixLeft, mixRight, sampleRate, safeCfg,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token);
                totalRtf += stepRtf;
            } else if (step.Task == NeuralModelTask.DebleedDenoise && stems.Length > 0) {
                // Refine primary vocal/lead stem via installed Debleed/Denoise checkpoint without altering instrumental mix
                int targetIdx = FindPrimaryVocalStemIndex(stems);
                if (targetIdx >= 0) {
                    NeuralAudioConfig cleanCfg = new() {
                        PreferredProvider = safeCfg.PreferredProvider,
                        OverlapFactor = safeCfg.OverlapFactor,
                        EnableTta = safeCfg.EnableTta,
                        EnablePhaseFix = false,
                        EnforceEnergyConservation = false
                    };
                    (StereoStemBuffer[] cleaned, float stepRtf) = RunStepSeparation(
                        step, stems[targetIdx].Left, stems[targetIdx].Right, sampleRate, cleanCfg,
                        (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token);
                    totalRtf += stepRtf;
                    if (cleaned.Length > 0) {
                        stems[targetIdx] = new StereoStemBuffer(stems[targetIdx].Name, cleaned[0].Left, cleaned[0].Right);
                        if (safeCfg.EnforceEnergyConservation) {
                            stems = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(mixLeft, mixRight, stems);
                        }
                    }
                }
            } else if (step.Task == NeuralModelTask.Dereverb) {
                int vocalIdx = FindPrimaryVocalStemIndex(stems);
                StereoStemBuffer sourceVocal = vocalIdx >= 0
                    ? stems[vocalIdx]
                    : new StereoStemBuffer("vocals", mixLeft, mixRight);
                NeuralModelCatalogEntry drvModel = step.Models[0];
                (dereverbResult, float stepRtf) = NeuralAudioOfflinePipeline.RunDereverbDryWet(
                    runtime,
                    step.ModelPaths[0],
                    sourceVocal,
                    sampleRate,
                    safeCfg,
                    drvModel.RecommendedChunkSamples,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg),
                    token,
                    drvModel.ExpectedSampleRate);
                totalRtf += stepRtf;
            } else if (step.Task == NeuralModelTask.CodecRestoreApollo || step.Task == NeuralModelTask.BandwidthExtension) {
                float[] srcL = stems.Length > 0 ? stems[0].Left : mixLeft;
                float[] srcR = stems.Length > 0 ? stems[0].Right : mixRight;
                NeuralAudioConfig restoreCfg = new() {
                    PreferredProvider = safeCfg.PreferredProvider,
                    OverlapFactor = safeCfg.OverlapFactor,
                    EnableTta = false,
                    EnablePhaseFix = false,
                    EnforceEnergyConservation = false
                };
                (StereoStemBuffer[] restored, float stepRtf) = RunStepSeparation(
                    step, srcL, srcR, sampleRate, restoreCfg,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token);
                totalRtf += stepRtf;
                if (restored.Length > 0) {
                    stems = restored;
                }
            }
        }

        NeuralSeparatedTrack track = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
            sourceTrackKey,
            sampleRate,
            stems,
            dereverbResult,
            derivedFromLosslessTwin,
            plan.PlanSignature);

        if (trackCache != null && stems.Length > 0) {
            trackCache.Save(cacheKey, track, token);
            trackCache.EvictToLimit((long)safeCfg.TrackCacheMaxSizeMb * 1024L * 1024L);
        }

        onProgress?.Invoke(1.0, $"Neural Ready ({plan.Summary})");
        return (track, totalRtf, false);
    }

    /// <summary>
    /// Runs the Slot 7A <see cref="NeuralPipelineGoal.LosslessResidualRestore"/> chain (<c>Apollo</c> -&gt; <c>AP-BWE</c>)
    /// directly on an arbitrary stereo buffer (such as an Atmos-exclusive EM-MWF residual pair) without writing
    /// intermediate per-channel pairs into the disk track cache.
    /// </summary>
    public StereoStemBuffer RestoreStereoBuffer(
        float[] inLeft,
        float[] inRight,
        int sampleRate,
        NeuralAudioConfig config,
        Action<double, string> onProgress = null,
        CancellationToken token = default) {
        if (inLeft == null || inRight == null || inLeft.Length == 0 || inRight.Length == 0) {
            return new StereoStemBuffer("restored", inLeft ?? Array.Empty<float>(), inRight ?? Array.Empty<float>());
        }
        NeuralAudioConfig safeCfg = (config ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan plan = BuildPlan(NeuralPipelineGoal.LosslessResidualRestore, safeCfg);
        if (!plan.IsRunnable || !runtime.GetRuntimeInfo().IsAvailable) {
            return new StereoStemBuffer("restored", inLeft, inRight);
        }

        float[] curL = inLeft;
        float[] curR = inRight;
        NeuralAudioConfig restoreCfg = new() {
            PreferredProvider = safeCfg.PreferredProvider,
            OverlapFactor = safeCfg.OverlapFactor,
            EnableTta = false,
            EnablePhaseFix = false,
            EnforceEnergyConservation = false
        };
        int stepCount = Math.Max(1, plan.Steps.Count);
        for (int s = 0; s < plan.Steps.Count; s++) {
            token.ThrowIfCancellationRequested();
            NeuralExecutionStep step = plan.Steps[s];
            double stepStart = s / (double)stepCount;
            double stepSpan = 1.0 / stepCount;
            (StereoStemBuffer[] restored, _) = RunStepSeparation(
                step, curL, curR, sampleRate, restoreCfg,
                (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token);
            if (restored.Length > 0 && restored[0].Left != null && restored[0].Right != null) {
                curL = restored[0].Left;
                curR = restored[0].Right;
            }
        }
        return new StereoStemBuffer("restored", curL, curR);
    }

    (StereoStemBuffer[] Stems, float RealTimeFactor) RunStepSeparation(
        NeuralExecutionStep step,
        float[] inLeft,
        float[] inRight,
        int sampleRate,
        NeuralAudioConfig cfg,
        Action<double, string> onProgress,
        CancellationToken token) {
        List<string[]> stemNames = new(step.Models.Count);
        List<int> expectedRates = new(step.Models.Count);
        List<int> chunks = new(step.Models.Count);
        for (int i = 0; i < step.Models.Count; i++) {
            NeuralModelCatalogEntry m = step.Models[i];
            stemNames.Add(m.OutputStemNames != null && m.OutputStemNames.Length > 0
                ? m.OutputStemNames
                : NeuralModelCatalog.GetDefaultOutputStemNames(m.Task));
            expectedRates.Add(m.ExpectedSampleRate);
            chunks.Add(m.RecommendedChunkSamples);
        }

        return NeuralAudioOfflinePipeline.RunSeparation(
            runtime,
            step.ModelPaths,
            stemNames,
            step.Weights,
            inLeft,
            inRight,
            sampleRate,
            cfg,
            chunks[0],
            onProgress,
            token,
            expectedSampleRatesPerModel: expectedRates,
            chunkSamplesPerModel: chunks);
    }

    NeuralExecutionStep ResolveSeparationStep(
        NeuralAudioConfig cfg,
        IReadOnlyList<NeuralModelCatalogEntry> installed,
        Dictionary<string, NeuralModelCatalogEntry> installedById,
        bool preferVocalsOnly) {
        if (cfg.OrchestrationMode == NeuralOrchestrationMode.Manual) {
            List<NeuralModelCatalogEntry> chosen = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            NeuralModelTask[] order = preferVocalsOnly
                ? [NeuralModelTask.VocalSeparation, NeuralModelTask.StemSeparation6, NeuralModelTask.StemSeparation4]
                : [NeuralModelTask.StemSeparation6, NeuralModelTask.StemSeparation4, NeuralModelTask.VocalSeparation];

            foreach (NeuralModelTask t in order) {
                IReadOnlyList<string> ids = cfg.GetModelsForTask(t);
                for (int i = 0; i < ids.Count; i++) {
                    if (installedById.TryGetValue(ids[i], out NeuralModelCatalogEntry entry) && seen.Add(entry.Id)) {
                        chosen.Add(entry);
                    }
                }
            }
            if (chosen.Count == 0 && installedById.TryGetValue(cfg.ActiveStemModelId, out NeuralModelCatalogEntry fallback)) {
                chosen.Add(fallback);
            }
            return BuildStepFromEntries(chosen.Count > 0 ? chosen[0].Task : NeuralModelTask.StemSeparation4, chosen);
        }

        // Auto mode: rank all installed stem/vocal models by PriorityRank and combine complementary models for MVSEP ensemble
        List<NeuralModelCatalogEntry> sepModels = [];
        for (int i = 0; i < installed.Count; i++) {
            NeuralModelTask t = installed[i].Task;
            if (t == NeuralModelTask.StemSeparation6 || t == NeuralModelTask.StemSeparation4 || t == NeuralModelTask.VocalSeparation) {
                sepModels.Add(installed[i]);
            }
        }
        if (sepModels.Count == 0) {
            return null;
        }

        if (preferVocalsOnly) {
            sepModels.Sort((a, b) => {
                int aVoc = a.Task == NeuralModelTask.VocalSeparation ? 1 : 0;
                int bVoc = b.Task == NeuralModelTask.VocalSeparation ? 1 : 0;
                int cmp = bVoc.CompareTo(aVoc);
                return cmp != 0 ? cmp : b.PriorityRank.CompareTo(a.PriorityRank);
            });
            // Top 1–2 vocal-capable models
            List<NeuralModelCatalogEntry> picked = sepModels.Take(2).ToList();
            return BuildStepFromEntries(picked[0].Task, picked);
        }

        // Full stem separation: pick best multi-stem model + optional top dedicated vocal model for semantic vocal ensembling
        NeuralModelCatalogEntry bestMulti = sepModels
            .Where(m => m.Task == NeuralModelTask.StemSeparation6 || m.Task == NeuralModelTask.StemSeparation4)
            .OrderByDescending(m => m.PriorityRank)
            .FirstOrDefault();
        NeuralModelCatalogEntry bestVocal = sepModels
            .Where(m => m.Task == NeuralModelTask.VocalSeparation)
            .OrderByDescending(m => m.PriorityRank)
            .FirstOrDefault();

        List<NeuralModelCatalogEntry> autoChosen = [];
        if (bestMulti != null) {
            autoChosen.Add(bestMulti);
        }
        if (bestVocal != null) {
            autoChosen.Add(bestVocal);
        }
        return BuildStepFromEntries(autoChosen[0].Task, autoChosen);
    }

    NeuralExecutionStep ResolveSingleTaskStep(
        NeuralModelTask task,
        NeuralAudioConfig cfg,
        IReadOnlyList<NeuralModelCatalogEntry> installed,
        Dictionary<string, NeuralModelCatalogEntry> installedById,
        bool allowEnsemble) {
        List<NeuralModelCatalogEntry> chosen = [];
        if (cfg.OrchestrationMode == NeuralOrchestrationMode.Manual) {
            IReadOnlyList<string> ids = cfg.GetModelsForTask(task);
            for (int i = 0; i < ids.Count; i++) {
                if (installedById.TryGetValue(ids[i], out NeuralModelCatalogEntry entry)) {
                    chosen.Add(entry);
                    if (!allowEnsemble) {
                        break;
                    }
                }
            }
        } else {
            List<NeuralModelCatalogEntry> matches = installed
                .Where(m => m.Task == task)
                .OrderByDescending(m => m.PriorityRank)
                .ToList();
            if (matches.Count > 0) {
                if (allowEnsemble) {
                    chosen.AddRange(matches.Take(2));
                } else {
                    chosen.Add(matches[0]);
                }
            }
        }
        return BuildStepFromEntries(task, chosen);
    }

    NeuralExecutionStep BuildStepFromEntries(NeuralModelTask task, List<NeuralModelCatalogEntry> entries) {
        if (entries == null || entries.Count == 0) {
            return null;
        }
        List<NeuralModelCatalogEntry> validEntries = [];
        List<string> paths = [];
        List<float> weights = [];
        for (int i = 0; i < entries.Count; i++) {
            string p = registry.ResolveInstalledModelPath(entries[i].Id);
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) {
                validEntries.Add(entries[i]);
                paths.Add(p);
                weights.Add(Math.Max(10f, entries[i].PriorityRank));
            }
        }
        if (validEntries.Count == 0) {
            return null;
        }
        return new NeuralExecutionStep {
            Task = task,
            Models = validEntries,
            ModelPaths = paths,
            Weights = weights.ToArray()
        };
    }

    static void AddStepIfValid(List<NeuralExecutionStep> steps, NeuralExecutionStep step) {
        if (step != null) {
            steps.Add(step);
        }
    }

    static int FindPrimaryVocalStemIndex(StereoStemBuffer[] stems) {
        if (stems == null || stems.Length == 0) {
            return -1;
        }
        int fallbackVocal = -1;
        for (int i = 0; i < stems.Length; i++) {
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stems[i].Name);
            if (role == NeuralStemRole.LeadVocal) {
                return i;
            }
            if (role == NeuralStemRole.Vocals && fallbackVocal < 0) {
                fallbackVocal = i;
            }
        }
        return fallbackVocal;
    }

    static string ComputePlanSignature(NeuralPipelineGoal goal, NeuralAudioConfig cfg, List<NeuralExecutionStep> steps) {
        StringBuilder sb = new();
        sb.Append(goal).Append('|').Append(cfg.OrchestrationMode)
            .Append("|ov=").Append(cfg.OverlapFactor)
            .Append("|tta=").Append(cfg.EnableTta ? '1' : '0')
            .Append("|ens=").Append(cfg.EnsembleMode)
            .Append("|pf=").Append(cfg.EnablePhaseFix ? '1' : '0')
            .Append("|ec=").Append(cfg.EnforceEnergyConservation ? '1' : '0');
        for (int i = 0; i < steps.Count; i++) {
            sb.Append('|').Append(steps[i].Task).Append(':');
            for (int m = 0; m < steps[i].Models.Count; m++) {
                if (m > 0) sb.Append('+');
                sb.Append(steps[i].Models[m].Id);
                try {
                    FileInfo fi = new(steps[i].ModelPaths[m]);
                    sb.Append('@').Append(fi.Length);
                } catch { }
            }
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    static string FormatPlanSummary(NeuralOrchestrationMode mode, List<NeuralExecutionStep> steps) {
        string prefix = mode == NeuralOrchestrationMode.Auto ? "Auto" : "Manual";
        if (steps == null || steps.Count == 0) {
            return $"{prefix}: —";
        }
        List<string> labels = [];
        for (int i = 0; i < steps.Count; i++) {
            NeuralExecutionStep st = steps[i];
            if (st.Models.Count == 1) {
                labels.Add(st.Models[0].DisplayName);
            } else {
                labels.Add(string.Join(" + ", st.Models.Select(m => m.DisplayName)));
            }
        }
        return $"{prefix}: {string.Join(" -> ", labels)}";
    }
}
