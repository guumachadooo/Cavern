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

    /// <summary>Configured maximum cache size budget in bytes.</summary>
    public long MaxBytes { get; init; }

    /// <summary>Total estimated raw uncompressed bytes across all cached entries (for savings reporting).</summary>
    public long TotalUncompressedBytes { get; init; }
}

/// <summary>
/// Slot 7B disk-backed LRU cache contract for pre-separated <see cref="NeuralSeparatedTrack"/> stem bundles,
/// shared by Slot 2B (live 3D stems &amp; Spatial Karaoke), Slot 2D (Song Vocal Mix DNA), and Slot 8 (Offline Studio Mastering).
/// </summary>
public interface INeuralTrackCache {
    /// <summary>True when persistent caching is enabled.</summary>
    bool IsEnabled { get => true; set { } }

    /// <summary>Active compression tier for newly persisted <c>CNTC v3</c> cache entries.</summary>
    NeuralTrackCacheCompressionMode CompressionMode { get => NeuralTrackCacheCompressionMode.Compact16Bit; set { } }

    /// <summary>Returns current entry count, occupied bytes, and configured LRU cap.</summary>
    NeuralTrackCacheStats GetStats();

    /// <summary>Lists O(1)-peeked metadata for all cached track bundles ordered newest-first for the In-App Cache Folder Browser.</summary>
    IReadOnlyList<NeuralTrackCacheEntryInfo> ListEntries() => Array.Empty<NeuralTrackCacheEntryInfo>();

    /// <summary>Deletes a single cached track entry by its <paramref name="cacheKey"/>.</summary>
    bool DeleteEntry(string cacheKey) => false;

    /// <summary>Attempts to load a cached <see cref="NeuralSeparatedTrack"/> matching <paramref name="cacheKey"/>.</summary>
    bool TryLoad(string cacheKey, out NeuralSeparatedTrack track, CancellationToken token = default);

    /// <summary>
    /// Cache-first source-track fallback: scans O(1) headers to reuse an existing cached stem bundle for the same
    /// source track across backend/preset changes unless the user explicitly requested re-extraction.
    /// </summary>
    bool TryLoadBestForSourceTrack(
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        NeuralPipelineGoal goal,
        out NeuralSeparatedTrack track,
        CancellationToken token = default) {
        track = null;
        return false;
    }

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

        bool isManual = safeCfg.OrchestrationMode == NeuralOrchestrationMode.Manual;
        NeuralEnsembleBundlePreset bundle = safeCfg.StudioBundlePreset;
        List<NeuralExecutionStep> steps = [];

        if (bundle != NeuralEnsembleBundlePreset.FastSingle &&
            (goal == NeuralPipelineGoal.StemSeparation || goal == NeuralPipelineGoal.SpatialKaraoke)) {
            BuildStudioBundleSteps(bundle, safeCfg, installed, installedById, steps);
        }

        if (steps.Count == 0) {
            switch (goal) {
                case NeuralPipelineGoal.StemSeparation:
                    AddStepIfValid(steps, ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: false));
                    if (isManual) {
                        AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, safeCfg, installed, installedById, allowEnsemble: false));
                    }
                    break;

                case NeuralPipelineGoal.SpatialKaraoke: {
                    if (!safeCfg.KaraokePreserveBackingVocals) {
                        // Pure Instrumental (Remove Backing Vocals): route to full-vocal removal first so BOTH lead & backing
                        // vocals are eliminated cleanly without erasing instruments (never rely solely on lead-only kara_2!).
                        NeuralExecutionStep fullVocalStep = ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: true);
                        if (fullVocalStep != null) {
                            steps.Add(fullVocalStep);
                        } else {
                            AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.KaraokeLeadBacking, safeCfg, installed, installedById, allowEnsemble: isManual));
                        }
                    } else {
                        NeuralExecutionStep karaokeStep = ResolveSingleTaskStep(NeuralModelTask.KaraokeLeadBacking, safeCfg, installed, installedById, allowEnsemble: isManual);
                        if (karaokeStep != null) {
                            steps.Add(karaokeStep);
                        } else {
                            AddStepIfValid(steps, ResolveSeparationStep(safeCfg, installed, installedById, preferVocalsOnly: true));
                        }
                    }
                    if (isManual) {
                        AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, safeCfg, installed, installedById, allowEnsemble: false));
                    }
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
        }

        string signature = ComputePlanSignature(goal, safeCfg, steps);
        string summary = FormatPlanSummary(safeCfg.OrchestrationMode, bundle, steps);
        return new NeuralExecutionPlan {
            Goal = goal,
            Mode = safeCfg.OrchestrationMode,
            Steps = steps,
            PlanSignature = signature,
            Summary = summary
        };
    }

    void BuildStudioBundleSteps(
        NeuralEnsembleBundlePreset bundle,
        NeuralAudioConfig cfg,
        IReadOnlyList<NeuralModelCatalogEntry> installed,
        Dictionary<string, NeuralModelCatalogEntry> installedById,
        List<NeuralExecutionStep> steps) {
        switch (bundle) {
            case NeuralEnsembleBundlePreset.StudioVocalEnsemble: {
                List<NeuralModelCatalogEntry> vocals = installed
                    .Where(m => m.Task == NeuralModelTask.VocalSeparation)
                    .OrderByDescending(m => m.PriorityRank)
                    .ToList();
                if (vocals.Count > 0) {
                    AddStepIfValid(steps, BuildStepFromEntries(NeuralModelTask.VocalSeparation, vocals));
                } else {
                    AddStepIfValid(steps, ResolveSeparationStep(cfg, installed, installedById, preferVocalsOnly: true));
                }
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, cfg, installed, installedById, allowEnsemble: false));
                break;
            }
            case NeuralEnsembleBundlePreset.StudioPureInstrumental: {
                List<NeuralModelCatalogEntry> vocals = installed
                    .Where(m => m.Task == NeuralModelTask.VocalSeparation)
                    .OrderByDescending(m => m.PriorityRank)
                    .ToList();
                if (vocals.Count > 0) {
                    AddStepIfValid(steps, BuildStepFromEntries(NeuralModelTask.VocalSeparation, vocals));
                } else {
                    AddStepIfValid(steps, ResolveSeparationStep(cfg, installed, installedById, preferVocalsOnly: true));
                }
                AddStepIfValid(steps, ResolveSingleTaskStep(NeuralModelTask.DebleedDenoise, cfg, installed, installedById, allowEnsemble: false));
                break;
            }
            case NeuralEnsembleBundlePreset.StudioFullBand6Stem: {
                // Step 1: Highest-SDR multi-stem separator (prefer BS-RoFormer 6-stem -> HTDemucs 6-stem -> BS-RoFormer 4-stem -> SCNet)
                NeuralModelCatalogEntry bestMulti = installed
                    .Where(m => m.Task == NeuralModelTask.StemSeparation6 || m.Task == NeuralModelTask.StemSeparation4)
                    .OrderByDescending(m => m.Task == NeuralModelTask.StemSeparation6 ? 1 : 0)
                    .ThenByDescending(m => m.PriorityRank)
                    .FirstOrDefault();
                if (bestMulti != null) {
                    AddStepIfValid(steps, BuildStepFromEntries(bestMulti.Task, [bestMulti]));
                }
                // Step 2: Dedicated vocal anchor model (e.g., Mel-Band RoFormer Vocals / Kim Vocal 2) fused over multi-stem vocals
                NeuralModelCatalogEntry bestVocal = installed
                    .Where(m => m.Task == NeuralModelTask.VocalSeparation)
                    .OrderByDescending(m => m.PriorityRank)
                    .FirstOrDefault();
                if (bestVocal != null) {
                    AddStepIfValid(steps, BuildStepFromEntries(NeuralModelTask.VocalSeparation, [bestVocal]));
                }
                break;
            }
            case NeuralEnsembleBundlePreset.StudioKaraokeLeadBacking: {
                // MVSEP 2-Stage Studio Karaoke Cascade (+1.13 dB SDR):
                // Stage 1: Full-vocal extractor -> [vocals, instrumental] (pure instrumental!)
                // Stage 2: Lead-vs-Backing model run strictly on isolated vocals -> [lead_vocal, backing_vocals]
                NeuralModelCatalogEntry bestVocal = installed
                    .Where(m => m.Task == NeuralModelTask.VocalSeparation)
                    .OrderByDescending(m => m.PriorityRank)
                    .FirstOrDefault();
                if (bestVocal != null) {
                    AddStepIfValid(steps, BuildStepFromEntries(NeuralModelTask.VocalSeparation, [bestVocal]));
                } else {
                    AddStepIfValid(steps, ResolveSeparationStep(cfg, installed, installedById, preferVocalsOnly: true));
                }
                NeuralModelCatalogEntry bestKaraoke = installed
                    .Where(m => m.Task == NeuralModelTask.KaraokeLeadBacking)
                    .OrderByDescending(m => m.PriorityRank)
                    .FirstOrDefault();
                if (bestKaraoke != null) {
                    AddStepIfValid(steps, BuildStepFromEntries(NeuralModelTask.KaraokeLeadBacking, [bestKaraoke]));
                }
                break;
            }
        }
    }

    /// <summary>
    /// Selects the highest vocal-band-energy (300–3400 Hz center-weighted) contiguous excerpt up to
    /// <paramref name="maxDurationSeconds"/> so <see cref="NeuralPipelineGoal.VocalMixDna"/> captures the main sung
    /// verse/chorus in ~10–25 seconds instead of running multiple ONNX passes across an entire 5-minute track.
    /// </summary>
    public static (float[] Left, float[] Right, double StartSeconds, double DurationSeconds) SelectRepresentativeVocalExcerpt(
        float[] mixLeft,
        float[] mixRight,
        int sampleRate,
        double maxDurationSeconds = 40.0) {
        ArgumentNullException.ThrowIfNull(mixLeft);
        ArgumentNullException.ThrowIfNull(mixRight);
        int sr = sampleRate >= 8000 ? sampleRate : 48000;
        int totalLen = Math.Min(mixLeft.Length, mixRight.Length);
        if (totalLen <= 0) {
            return (Array.Empty<float>(), Array.Empty<float>(), 0.0, 0.0);
        }

        int targetSamples = (int)Math.Round(Math.Clamp(maxDurationSeconds, 12.0, 120.0) * sr);
        if (totalLen <= targetSamples) {
            return (mixLeft, mixRight, 0.0, totalLen / (double)sr);
        }

        // Compute 2-second vocal-band (300..3400 Hz) center-weighted energy blocks via fast 1-pole HP/LP IIR
        int blockSamples = Math.Max(1024, sr * 2);
        int numBlocks = (totalLen + blockSamples - 1) / blockSamples;
        double[] blockScore = new double[numBlocks];

        float dt = 1f / sr;
        float hpAlpha = 1f / (1f + 2f * MathF.PI * 300f * dt);
        float lpAlpha = (2f * MathF.PI * 3400f * dt) / (1f + 2f * MathF.PI * 3400f * dt);
        float hpPrevIn = 0f;
        float hpPrevOut = 0f;
        float lpPrevOut = 0f;

        for (int b = 0; b < numBlocks; b++) {
            int start = b * blockSamples;
            int end = Math.Min(totalLen, start + blockSamples);
            double bandSum = 0.0;
            for (int i = start; i < end; i++) {
                float mid = 0.5f * (mixLeft[i] + mixRight[i]);
                float hp = hpAlpha * (hpPrevOut + mid - hpPrevIn);
                hpPrevIn = mid;
                hpPrevOut = hp;
                float bp = lpPrevOut + lpAlpha * (hp - lpPrevOut);
                lpPrevOut = bp;
                bandSum += (double)bp * bp;
            }
            blockScore[b] = bandSum / Math.Max(1, end - start);
        }

        int windowBlocks = Math.Max(1, targetSamples / blockSamples);
        if (windowBlocks >= numBlocks) {
            return (mixLeft, mixRight, 0.0, totalLen / (double)sr);
        }

        // Sliding window over blocks to locate peak sustained vocal region (preferring non-intro/outro interior)
        double curSum = 0.0;
        for (int b = 0; b < windowBlocks; b++) {
            curSum += blockScore[b];
        }
        double bestSum = curSum;
        int bestStartBlock = 0;
        for (int b = windowBlocks; b < numBlocks; b++) {
            curSum += blockScore[b] - blockScore[b - windowBlocks];
            if (curSum > bestSum) {
                bestSum = curSum;
                bestStartBlock = b - windowBlocks + 1;
            }
        }

        int startSample = Math.Clamp(bestStartBlock * blockSamples, 0, Math.Max(0, totalLen - targetSamples));
        int sliceLen = Math.Min(targetSamples, totalLen - startSample);
        float[] sliceL = new float[sliceLen];
        float[] sliceR = new float[sliceLen];
        Array.Copy(mixLeft, startSample, sliceL, 0, sliceLen);
        Array.Copy(mixRight, startSample, sliceR, 0, sliceLen);
        return (sliceL, sliceR, startSample / (double)sr, sliceLen / (double)sr);
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
        CancellationToken token = default,
        Action<NeuralSeparatedTrack> onProgressiveTrackReady = null,
        string displayTitle = null,
        string artistName = null,
        bool forceReextract = false) {
        ArgumentNullException.ThrowIfNull(mixLeft);
        ArgumentNullException.ThrowIfNull(mixRight);

        NeuralAudioConfig safeCfg = (config ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan plan = BuildPlan(goal, safeCfg);
        if (!plan.IsRunnable) {
            throw new InvalidOperationException("No installed ONNX model matches the requested neural pipeline goal.");
        }

        bool allowCache = trackCache != null && safeCfg.TrackCacheEnabled && trackCache.IsEnabled;
        string cacheKey = ComputeTrackCacheKey(sourceTrackKey, fileSizeBytes, lastModifiedTicks, sampleRate, plan.PlanSignature);
        if (allowCache && !forceReextract) {
            if (trackCache.TryLoad(cacheKey, out NeuralSeparatedTrack cached, token) ||
                trackCache.TryLoadBestForSourceTrack(sourceTrackKey, displayTitle, artistName, goal, out cached, token)) {
                onProgress?.Invoke(1.0, $"Cache Hit ({plan.Summary})");
                return (cached, 0f, true);
            }
        }

        // For VocalMixDna, analyze a smart ~40s peak vocal-activity excerpt with 2x COLA overlap (fast & accurate DNA extraction
        // instead of processing 4-6 full minutes through multiple models + 6144-pt Reverb HQ).
        float[] workLeft = mixLeft;
        float[] workRight = mixRight;
        NeuralAudioConfig execCfg = safeCfg;
        bool isExcerptDna = false;
        if (goal == NeuralPipelineGoal.VocalMixDna) {
            (float[] exL, float[] exR, _, _) = SelectRepresentativeVocalExcerpt(mixLeft, mixRight, sampleRate, maxDurationSeconds: 40.0);
            if (exL.Length > 0 && exL.Length < mixLeft.Length) {
                workLeft = exL;
                workRight = exR;
                isExcerptDna = true;
            }
            execCfg = new NeuralAudioConfig {
                PreferredProvider = safeCfg.PreferredProvider,
                OrchestrationMode = safeCfg.OrchestrationMode,
                OverlapFactor = MvsepPipelineOptions.MinOverlap,
                EnableTta = false,
                EnsembleMode = safeCfg.EnsembleMode,
                EnablePhaseFix = false,
                EnforceEnergyConservation = false,
                TrackCacheMaxSizeMb = safeCfg.TrackCacheMaxSizeMb,
                TaskModelSlots = safeCfg.TaskModelSlots,
                ActiveStemModelId = safeCfg.ActiveStemModelId,
                ActiveKaraokeModelId = safeCfg.ActiveKaraokeModelId,
                ActiveDereverbModelId = safeCfg.ActiveDereverbModelId,
                ActiveDebleedModelId = safeCfg.ActiveDebleedModelId,
                ActiveRestorerModelId = safeCfg.ActiveRestorerModelId
            };
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

            if (step.Task == NeuralModelTask.KaraokeLeadBacking && stems.Length > 0) {
                // MVSEP 2-Stage Studio Karaoke Cascade: run Lead-vs-Backing strictly on the isolated full-vocal stem
                // from Stage 1, producing 4 clean stems: lead_vocal, backing_vocals, vocals, and pure instrumental!
                int vocalIdx = FindPrimaryVocalStemIndex(stems);
                if (vocalIdx >= 0) {
                    StereoStemBuffer isolatedVocals = stems[vocalIdx];
                    NeuralAudioConfig stage2Cfg = new() {
                        PreferredProvider = execCfg.PreferredProvider,
                        OverlapFactor = execCfg.OverlapFactor,
                        EnableTta = execCfg.EnableTta,
                        EnablePhaseFix = false,
                        EnforceEnergyConservation = false
                    };
                    (StereoStemBuffer[] splitVocals, float stepRtf) = RunStepSeparation(
                        step, isolatedVocals.Left, isolatedVocals.Right, sampleRate, stage2Cfg,
                        (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, $"Stage 2 Lead/Back: {msg}"), token);
                    totalRtf += stepRtf;
                    stems = MergeTwoStageKaraokeStems(workLeft, workRight, stems, isolatedVocals, splitVocals, execCfg);
                }
            } else if (step.Task == NeuralModelTask.VocalSeparation && stems.Length > 0 &&
                       safeCfg.StudioBundlePreset == NeuralEnsembleBundlePreset.StudioFullBand6Stem) {
                // Studio Full-Band 6-Stem Vocal-Anchor Fusion: replace multi-stem vocal with dedicated vocal model
                // and re-conserve energy cleanly across all instruments.
                (StereoStemBuffer[] vocalPass, float stepRtf) = RunStepSeparation(
                    step, workLeft, workRight, sampleRate, execCfg,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, $"Vocal Anchor: {msg}"), token);
                totalRtf += stepRtf;
                stems = FuseVocalAnchorIntoMultiStem(workLeft, workRight, stems, vocalPass, execCfg, token);
            } else if (step.Task == NeuralModelTask.StemSeparation4 ||
                step.Task == NeuralModelTask.StemSeparation6 ||
                step.Task == NeuralModelTask.VocalSeparation ||
                step.Task == NeuralModelTask.KaraokeLeadBacking) {
                Action<NeuralSeparatedTrack> stepProgressive = (!isExcerptDna && totalSteps == 1)
                    ? onProgressiveTrackReady
                    : null;
                (stems, float stepRtf) = RunStepSeparation(step, workLeft, workRight, sampleRate, execCfg,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token,
                    sourceTrackKey: sourceTrackKey,
                    derivedFromLosslessTwin: derivedFromLosslessTwin,
                    modelSignature: plan.PlanSignature,
                    onProgressiveTrackReady: stepProgressive);
                totalRtf += stepRtf;
            } else if (step.Task == NeuralModelTask.DebleedDenoise && stems.Length > 0) {
                // Refine primary vocal/lead stem via installed Debleed/Denoise checkpoint without altering instrumental mix
                int targetIdx = FindPrimaryVocalStemIndex(stems);
                if (targetIdx >= 0) {
                    NeuralAudioConfig cleanCfg = new() {
                        PreferredProvider = execCfg.PreferredProvider,
                        OverlapFactor = execCfg.OverlapFactor,
                        EnableTta = execCfg.EnableTta,
                        EnablePhaseFix = false,
                        EnforceEnergyConservation = false
                    };
                    (StereoStemBuffer[] cleaned, float stepRtf) = RunStepSeparation(
                        step, stems[targetIdx].Left, stems[targetIdx].Right, sampleRate, cleanCfg,
                        (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg), token);
                    totalRtf += stepRtf;
                    if (cleaned.Length > 0) {
                        stems[targetIdx] = new StereoStemBuffer(stems[targetIdx].Name, cleaned[0].Left, cleaned[0].Right);
                        if (execCfg.EnforceEnergyConservation) {
                            stems = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(workLeft, workRight, stems);
                        }
                    }
                }
            } else if (step.Task == NeuralModelTask.Dereverb) {
                int vocalIdx = FindPrimaryVocalStemIndex(stems);
                StereoStemBuffer sourceVocal = vocalIdx >= 0
                    ? stems[vocalIdx]
                    : new StereoStemBuffer("vocals", workLeft, workRight);
                NeuralModelCatalogEntry drvModel = step.Models[0];
                (dereverbResult, float stepRtf) = NeuralAudioOfflinePipeline.RunDereverbDryWet(
                    runtime,
                    step.ModelPaths[0],
                    sourceVocal,
                    sampleRate,
                    execCfg,
                    drvModel.RecommendedChunkSamples,
                    (p, msg) => onProgress?.Invoke(stepStart + p * stepSpan, msg),
                    token,
                    drvModel.ExpectedSampleRate);
                totalRtf += stepRtf;
            } else if (step.Task == NeuralModelTask.CodecRestoreApollo || step.Task == NeuralModelTask.BandwidthExtension) {
                float[] srcL = stems.Length > 0 ? stems[0].Left : workLeft;
                float[] srcR = stems.Length > 0 ? stems[0].Right : workRight;
                NeuralAudioConfig restoreCfg = new() {
                    PreferredProvider = execCfg.PreferredProvider,
                    OverlapFactor = execCfg.OverlapFactor,
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
        track.OriginTag = safeCfg.StudioBundlePreset != NeuralEnsembleBundlePreset.FastSingle
            ? "Studio Bundle"
            : "Local AI";
        track.ModelSummary = plan.Summary;
        if (!string.IsNullOrWhiteSpace(displayTitle)) {
            track.DisplayTitle = displayTitle.Trim();
        }
        if (!string.IsNullOrWhiteSpace(artistName)) {
            track.ArtistName = artistName.Trim();
        }

        // Only cache full-length track bundles in Slot 7B stem cache (avoid overwriting full track cache with a 40s DNA excerpt)
        if (allowCache && stems.Length > 0 && !isExcerptDna) {
            trackCache.CompressionMode = safeCfg.TrackCacheCompressionMode;
            trackCache.Save(cacheKey, track, token);
            trackCache.EvictToLimit((long)safeCfg.TrackCacheMaxSizeMb * 1024L * 1024L);
        }

        onProgress?.Invoke(1.0, $"Neural Ready ({plan.Summary})");
        return (track, totalRtf, false);
    }

    static StereoStemBuffer[] MergeTwoStageKaraokeStems(
        float[] mixLeft,
        float[] mixRight,
        StereoStemBuffer[] stage1Stems,
        StereoStemBuffer isolatedVocals,
        StereoStemBuffer[] stage2Split,
        NeuralAudioConfig cfg) {
        int len = Math.Min(mixLeft.Length, mixRight.Length);
        StereoStemBuffer lead = stage2Split != null && stage2Split.Length > 0 ? stage2Split[0] : isolatedVocals;
        float[] leadL = new float[len];
        float[] leadR = new float[len];
        float[] backL = new float[len];
        float[] backR = new float[len];
        float[] instrL = new float[len];
        float[] instrR = new float[len];

        int copyLead = Math.Min(len, lead.Length);
        int copyVoc = Math.Min(len, isolatedVocals.Length);
        for (int i = 0; i < len; i++) {
            float vl = i < copyVoc ? isolatedVocals.Left[i] : 0f;
            float vr = i < copyVoc ? isolatedVocals.Right[i] : 0f;
            float ll = i < copyLead ? lead.Left[i] : 0f;
            float lr = i < copyLead ? lead.Right[i] : 0f;
            leadL[i] = ll;
            leadR[i] = lr;
            // Exact vocal energy conservation: backing = isolated_vocals - lead_vocal (zero instruments in backing!)
            backL[i] = vl - ll;
            backR[i] = vr - lr;
            // Exact mix energy conservation: instrumental = mix - isolated_vocals
            instrL[i] = mixLeft[i] - vl;
            instrR[i] = mixRight[i] - vr;
        }

        List<StereoStemBuffer> merged = [
            new StereoStemBuffer("lead_vocal", leadL, leadR),
            new StereoStemBuffer("backing_vocals", backL, backR),
            new StereoStemBuffer("instrumental", instrL, instrR),
            new StereoStemBuffer("vocals", isolatedVocals.Left, isolatedVocals.Right)
        ];
        return merged.ToArray();
    }

    static StereoStemBuffer[] FuseVocalAnchorIntoMultiStem(
        float[] mixLeft,
        float[] mixRight,
        StereoStemBuffer[] multiStems,
        StereoStemBuffer[] vocalPass,
        NeuralAudioConfig cfg,
        CancellationToken token) {
        if (multiStems == null || multiStems.Length == 0 || vocalPass == null || vocalPass.Length == 0) {
            return multiStems ?? Array.Empty<StereoStemBuffer>();
        }
        int anchorIdx = FindPrimaryVocalStemIndex(vocalPass);
        StereoStemBuffer anchorVocal = anchorIdx >= 0 ? vocalPass[anchorIdx] : vocalPass[0];
        List<StereoStemBuffer> fused = new(multiStems.Length + 1);
        bool replacedVocal = false;

        for (int i = 0; i < multiStems.Length; i++) {
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(multiStems[i].Name);
            if (role == NeuralStemRole.Residual && string.Equals(multiStems[i].Name, "residual", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if ((role == NeuralStemRole.Vocals || role == NeuralStemRole.LeadVocal) && !replacedVocal) {
                fused.Add(new StereoStemBuffer("vocals", anchorVocal.Left, anchorVocal.Right));
                replacedVocal = true;
            } else {
                fused.Add(multiStems[i]);
            }
        }
        if (!replacedVocal) {
            fused.Add(new StereoStemBuffer("vocals", anchorVocal.Left, anchorVocal.Right));
        }

        StereoStemBuffer[] result = fused.ToArray();
        if (cfg.EnforceEnergyConservation) {
            result = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(mixLeft, mixRight, result);
        }
        if (cfg.EnablePhaseFix && result.Length > 1) {
            MvsepAntiArtifactPipeline.ApplyPhaseFixWiener(mixLeft, mixRight, result, cfg.ToMvsepOptions().StftSize, 1.0f, token);
            if (cfg.EnforceEnergyConservation) {
                result = MvsepAntiArtifactPipeline.AppendOrUpdateConservedResidual(mixLeft, mixRight, result);
            }
        }
        return result;
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
        CancellationToken token,
        string sourceTrackKey = null,
        bool derivedFromLosslessTwin = false,
        string modelSignature = null,
        Action<NeuralSeparatedTrack> onProgressiveTrackReady = null) {
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
            chunkSamplesPerModel: chunks,
            onProgressiveTrackReady: onProgressiveTrackReady,
            progressiveTrackKey: sourceTrackKey,
            progressiveDerivedFromTwin: derivedFromLosslessTwin,
            progressivePlanSignature: modelSignature);
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

            bool hasAnyExplicitSeparationSlot = false;
            foreach (NeuralModelTask t in order) {
                IReadOnlyList<string> ids = cfg.GetModelsForTask(t);
                if (ids.Count > 0) {
                    hasAnyExplicitSeparationSlot = true;
                }
                for (int i = 0; i < ids.Count; i++) {
                    if (installedById.TryGetValue(ids[i], out NeuralModelCatalogEntry entry) && seen.Add(entry.Id)) {
                        chosen.Add(entry);
                    }
                }
            }
            if (!hasAnyExplicitSeparationSlot && chosen.Count == 0 &&
                !string.IsNullOrWhiteSpace(cfg.ActiveStemModelId) &&
                installedById.TryGetValue(cfg.ActiveStemModelId, out NeuralModelCatalogEntry fallback)) {
                chosen.Add(fallback);
            }
            return BuildStepFromEntries(chosen.Count > 0 ? chosen[0].Task : NeuralModelTask.StemSeparation4, chosen);
        }

        // Auto mode: select ONE single best mobile-fast checkpoint per goal (eliminating 2-3x multiplicative model chains
        // that caused 30+ minute mobile stalls; multi-model MVSEP ensembling remains fully available in Manual mode).
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
            // Prefer dedicated mobile-fast vocal model -> any vocal model -> mobile-fast multi-stem -> highest rank
            NeuralModelCatalogEntry best = sepModels
                .OrderByDescending(m => m.Task == NeuralModelTask.VocalSeparation ? 1 : 0)
                .ThenByDescending(m => m.IsMobileFast ? 1 : 0)
                .ThenByDescending(m => m.PriorityRank)
                .First();
            return BuildStepFromEntries(best.Task, [best]);
        }

        // Full stem separation Auto: prioritize mobile-fast multi-stem (e.g. SCNet 4-Stem) over heavy 136 MB HTDemucs
        // when both are installed, falling back to any multi-stem or vocal separator if that's all installed.
        NeuralModelCatalogEntry bestMultiFast = sepModels
            .Where(m => (m.Task == NeuralModelTask.StemSeparation4 || m.Task == NeuralModelTask.StemSeparation6) && m.IsMobileFast)
            .OrderByDescending(m => m.PriorityRank)
            .FirstOrDefault();
        if (bestMultiFast != null) {
            return BuildStepFromEntries(bestMultiFast.Task, [bestMultiFast]);
        }

        NeuralModelCatalogEntry bestMultiAny = sepModels
            .Where(m => m.Task == NeuralModelTask.StemSeparation6 || m.Task == NeuralModelTask.StemSeparation4)
            .OrderByDescending(m => m.PriorityRank)
            .FirstOrDefault();
        if (bestMultiAny != null) {
            return BuildStepFromEntries(bestMultiAny.Task, [bestMultiAny]);
        }

        NeuralModelCatalogEntry bestVocalFallback = sepModels
            .OrderByDescending(m => m.IsMobileFast ? 1 : 0)
            .ThenByDescending(m => m.PriorityRank)
            .First();
        return BuildStepFromEntries(bestVocalFallback.Task, [bestVocalFallback]);
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
                .OrderByDescending(m => m.IsMobileFast ? 1 : 0)
                .ThenByDescending(m => m.PriorityRank)
                .ToList();
            if (matches.Count > 0) {
                // Single best model in Auto mode for fast mobile responsiveness
                chosen.Add(matches[0]);
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
            .Append("|b=").Append((int)cfg.StudioBundlePreset)
            .Append("|pbv=").Append(cfg.KaraokePreserveBackingVocals ? '1' : '0')
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
        string hash16 = Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
        string modelIds = steps.Count > 0
            ? string.Join("+", steps.SelectMany(st => st.Models.Select(m => m.Id)))
            : "none";
        return $"{hash16}|cfg:v1;g={goal};bk={cfg.ExecutionBackend};om={cfg.OrchestrationMode};bd={(int)cfg.StudioBundlePreset};pbv={(cfg.KaraokePreserveBackingVocals ? 1 : 0)};ov={cfg.OverlapFactor};tta={(cfg.EnableTta ? 1 : 0)};ens={cfg.EnsembleMode};pf={(cfg.EnablePhaseFix ? 1 : 0)};ec={(cfg.EnforceEnergyConservation ? 1 : 0)};m={modelIds}";
    }

    static string FormatPlanSummary(NeuralOrchestrationMode mode, NeuralEnsembleBundlePreset bundle, List<NeuralExecutionStep> steps) {
        string prefix = bundle != NeuralEnsembleBundlePreset.FastSingle
            ? $"Bundle({bundle})"
            : (mode == NeuralOrchestrationMode.Auto ? "Auto" : "Manual");
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
