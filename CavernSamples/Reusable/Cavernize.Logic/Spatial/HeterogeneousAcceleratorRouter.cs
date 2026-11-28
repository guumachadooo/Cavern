using System.Globalization;

namespace Cavernize.Logic.Spatial;

/// <summary>Compute backend executing a pipeline stage (STFT/iSTFT frontend, neural backbone, or post-mask synthesis).</summary>
public enum StageComputeBackend {
    /// <summary>ARM64 C++ NEON vectorized FFT/STFT &amp; overlap-add synthesis.</summary>
    CpuNeon = 0,
    /// <summary>ARM64 CPU with KleidiAI SME/i8mm GEMM acceleration.</summary>
    CpuKleidiAi = 1,
    /// <summary>Adreno / Vulkan Dawn WebGPU compute shaders.</summary>
    WebGpu = 2,
    /// <summary>Qualcomm Hexagon V79 NPU via QNN HTP.</summary>
    QnnHtp = 3
}

/// <summary>Hardware accelerator capability snapshot probed from the active mobile device.</summary>
public sealed class AcceleratorHardwareCaps {
    /// <summary>True when Qualcomm QNN HTP NPU libraries are installed and verified on a Snapdragon SoC.</summary>
    public bool SupportsQnnHtp { get; init; }
    /// <summary>True when Vulkan WebGPU Dawn execution provider initialized without device loss.</summary>
    public bool SupportsWebGpu { get; init; }
    /// <summary>True when ARM64 XNNPACK execution provider is available.</summary>
    public bool SupportsXnnpack { get; init; }
    /// <summary>True when ARM64 KleidiAI CPU execution is available (baseline always-on).</summary>
    public bool SupportsCpuKleidiAi { get; init; } = true;
    /// <summary>True on Qualcomm Snapdragon SoCs.</summary>
    public bool IsQualcommSnapdragon { get; init; }
    /// <summary>Hardware SoC description (e.g., <c>QTI SM8750</c>).</summary>
    public string SocLabel { get; init; } = string.Empty;
    /// <summary>Detected Hexagon HTP architecture tag (e.g., <c>V79</c>), or empty.</summary>
    public string QnnHtpVersionTag { get; init; } = string.Empty;
    /// <summary>Prime + Performance ARM64 CPU thread budget.</summary>
    public int PerformanceCoreCount { get; init; } = 4;
    /// <summary>Total logical CPU cores on device.</summary>
    public int TotalCoreCount { get; init; } = 8;
}

/// <summary>Architectural graph traits of an ONNX audio model used to pick the optimal heterogeneous accelerator split.</summary>
public sealed class ModelGraphTrait {
    /// <summary>Catalog ID or sanitized model label.</summary>
    public string ModelId { get; init; } = string.Empty;
    /// <summary>Local ONNX model path on disk.</summary>
    public string ModelPath { get; init; } = string.Empty;
    /// <summary>Combined model + external data byte size on disk.</summary>
    public long FileSizeBytes { get; init; }
    /// <summary>True when temporal/frequency tensor dimensions vary dynamically per call.</summary>
    public bool HasDynamicShapes { get; init; }
    /// <summary>True when graph contains dual-path RNN, GRU/LSTM, or axial-attention Transformer layers.</summary>
    public bool HasRnnOrTransformerAttention { get; init; }
    /// <summary>True when graph is a staticizable CNN / U-Net STFT or conv upsampler friendly to NPU/WebGPU.</summary>
    public bool IsStaticCnnOrUnetStft { get; init; }
    /// <summary>Estimated peak activation tensor footprint in bytes (0 lets <see cref="MemoryBudgetGovernor"/> estimate).</summary>
    public long EstimatedActivationBytes { get; init; }
}

/// <summary>Heterogeneous per-stage execution assignment produced by <see cref="HeterogeneousAcceleratorRouter.RouteModel"/>.</summary>
public sealed class AcceleratorStageAssignment {
    /// <summary>Backend executing frontend windowing &amp; STFT (<c>MixedRadixFft</c> / C++ NEON).</summary>
    public StageComputeBackend FrontendStftBackend { get; init; }
    /// <summary>Primary ONNX Runtime Execution Provider running the neural backbone.</summary>
    public NeuralExecutionProvider BackboneEp { get; init; }
    /// <summary>Secondary fallback or dual-stage Execution Provider when concurrent stages are active.</summary>
    public NeuralExecutionProvider SecondaryBackboneEp { get; init; }
    /// <summary>Backend executing post-mask synthesis, Wiener phase fix, and overlap-add iSTFT.</summary>
    public StageComputeBackend PostMaskBackend { get; init; }
    /// <summary>Weight precision chosen within <see cref="MemoryBudgetGovernor"/> safety bounds.</summary>
    public NeuralQuantizationMode PreferredPrecision { get; init; }
    /// <summary>True when ping-pong double buffering is safe to overlap chunk N inference with N+1 STFT.</summary>
    public bool UseDoubleBuffering { get; init; }
    /// <summary>Maximum concurrent in-flight chunks (2 when double-buffered, 1 under tight memory/thermal budgets).</summary>
    public int MaxConcurrentChunks { get; init; }
    /// <summary>Estimated peak native process PSS load in bytes (strictly bounded by <see cref="MemoryBudgetGovernor"/>).</summary>
    public long EstimatedPssBytes { get; init; }
    /// <summary>Active clamped per-process PSS ceiling in bytes (&lt;= 2600 MB).</summary>
    public long EffectivePssCapBytes { get; init; }
    /// <summary>Human-readable explanation of hardware routing and memory governance decisions.</summary>
    public string Reasoning { get; init; } = string.Empty;
}

/// <summary>Exponential moving average (EMA) throughput telemetry sample for an (EP, Model) pair.</summary>
public sealed class StageThroughputSnapshot {
    /// <summary>Execution Provider recorded.</summary>
    public NeuralExecutionProvider Provider { get; init; }
    /// <summary>Model identifier.</summary>
    public string ModelId { get; init; } = string.Empty;
    /// <summary>EMA chunk execution latency in milliseconds.</summary>
    public double EmaChunkElapsedMs { get; init; }
    /// <summary>EMA audio processing throughput in samples per second.</summary>
    public double EmaSamplesPerSecond { get; init; }
    /// <summary>Total recorded chunk observations.</summary>
    public long ObservationCount { get; init; }
    /// <summary>UTC timestamp of the most recent telemetry sample.</summary>
    public DateTime LastUpdatedUtc { get; init; }
}

/// <summary>
/// Snapdragon 8 Elite Heterogeneous Symbiosis Planner coordinating Hexagon V79 NPU (QNN HTP),
/// Adreno 830 Vulkan WebGPU, and ARM64 KleidiAI CPU under strict <see cref="MemoryBudgetGovernor"/> admission control.
/// </summary>
public sealed class HeterogeneousAcceleratorRouter {
    /// <summary>Maximum permitted quantization/heterogeneous SDR loss vs FP32 reference in dB (0.3 dB gate).</summary>
    public const double MaxAllowedSdrLossDb = 0.3;

    const double EmaAlpha = 0.30;
    const double BaselineSeparatorNoiseRatio = 0.06309573444801933; // 10^(-12 dB / 10)

    readonly object telemetryLock = new();
    readonly Dictionary<string, ThroughputAccumulator> throughputStats = new(StringComparer.OrdinalIgnoreCase);

    sealed class ThroughputAccumulator {
        public NeuralExecutionProvider Provider;
        public string ModelId = string.Empty;
        public double EmaMs;
        public double EmaSamplesPerSec;
        public long Count;
        public DateTime LastUtc;
    }

    /// <summary>
    /// Derives architectural <see cref="ModelGraphTrait"/> from <see cref="NeuralModelCatalog"/> contract metadata
    /// with safe fallback for user-imported custom ONNX models.
    /// </summary>
    public static ModelGraphTrait BuildModelTrait(
        string modelPath,
        long fileSizeBytes,
        NeuralModelCatalogEntry catalogEntry = null) {
        NeuralModelCatalogEntry entry = catalogEntry ?? NeuralModelCatalog.FindByFileNameOrId(modelPath);
        string id = entry?.Id ?? Path.GetFileNameWithoutExtension(modelPath ?? string.Empty);

        bool isStaticCnn = false;
        bool hasRnnOrAttn = false;
        bool hasDynamic = false;

        if (entry != null) {
            switch (entry.ContractKind) {
                case NeuralOnnxContractKind.MdxNetStft4Ch:
                    isStaticCnn = true;
                    hasDynamic = entry.StftDimT <= 0;
                    break;
                case NeuralOnnxContractKind.FlashSr16kTo48k:
                    isStaticCnn = true;
                    break;
                case NeuralOnnxContractKind.ScnetStft4Ch:
                    hasRnnOrAttn = true;
                    hasDynamic = entry.StftDimT <= 0;
                    break;
                case NeuralOnnxContractKind.MelBandRoFormerHostStft:
                case NeuralOnnxContractKind.BsRoFormerHostStft:
                    hasRnnOrAttn = true;
                    hasDynamic = entry.StftDimT <= 0;
                    break;
                case NeuralOnnxContractKind.WaveformStereo:
                    if (entry.Task == NeuralModelTask.CodecRestoreApollo && entry.IsMobileFast) {
                        isStaticCnn = true;
                    } else {
                        hasRnnOrAttn = true;
                        hasDynamic = true;
                    }
                    break;
                default:
                    isStaticCnn = entry.IsMobileFast;
                    hasDynamic = !entry.IsMobileFast;
                    break;
            }
        } else {
            string lower = Path.GetFileName(modelPath ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("scnet") || lower.Contains("demucs") || lower.Contains("roformer")) {
                hasRnnOrAttn = true;
                hasDynamic = !lower.Contains("sw_6stem") && !lower.Contains("4stem");
            } else {
                isStaticCnn = true;
            }
        }

        return new ModelGraphTrait {
            ModelId = id ?? string.Empty,
            ModelPath = modelPath ?? string.Empty,
            FileSizeBytes = Math.Max(0L, fileSizeBytes),
            HasDynamicShapes = hasDynamic,
            HasRnnOrTransformerAttention = hasRnnOrAttn,
            IsStaticCnnOrUnetStft = isStaticCnn
        };
    }

    /// <summary>
    /// Computes the optimal per-stage hardware assignment while strictly honoring <see cref="MemoryBudgetGovernor"/>
    /// (&lt;= 2600 MB single-process PSS ceiling, REG-001) and degrading precision/concurrency under RAM or thermal pressure.
    /// </summary>
    public AcceleratorStageAssignment RouteModel(
        ModelGraphTrait trait,
        AcceleratorHardwareCaps caps,
        DeviceMemorySnapshot memSnap,
        NeuralExecutionProvider userPreference) {
        ModelGraphTrait safeTrait = trait ?? new ModelGraphTrait();
        AcceleratorHardwareCaps safeCaps = caps ?? new AcceleratorHardwareCaps();
        NeuralModelCatalogEntry catalogEntry = NeuralModelCatalog.FindByFileNameOrId(
            !string.IsNullOrWhiteSpace(safeTrait.ModelId) ? safeTrait.ModelId : safeTrait.ModelPath);

        long modelBytes = Math.Max(0L, safeTrait.FileSizeBytes);
        if (modelBytes == 0L && catalogEntry != null && catalogEntry.ApproxSizeMb > 0f) {
            modelBytes = (long)Math.Ceiling(catalogEntry.ApproxSizeMb * 1024.0 * 1024.0);
        }

        bool staticEligible = safeTrait.IsStaticCnnOrUnetStft && !safeTrait.HasDynamicShapes && !safeTrait.HasRnnOrTransformerAttention;
        (NeuralExecutionProvider primaryEp, NeuralExecutionProvider secondaryEp, string routeNote) =
            SelectBackboneProviders(safeTrait, safeCaps, userPreference, staticEligible);

        NeuralQuantizationMode precision = OnnxMobileQuantizer.ResolveEffectiveMode(
            NeuralQuantizationMode.Auto,
            modelBytes,
            primaryEp,
            staticEligible);

        (MemoryBudgetDecision decision, long estLoadBytes) = EvaluateWithTrait(memSnap, modelBytes, catalogEntry, precision, safeTrait);
        long hardCeil = MemoryBudgetGovernor.HardSingleProcessPssCeilingBytes;
        long effectiveCap = Math.Min(decision.EffectivePssCapBytes, hardCeil);
        bool tightMemory = memSnap.IsLowMemory || memSnap.ThermalStatusCode >= 3 || !decision.HasSufficientMemory || estLoadBytes > effectiveCap;

        if (tightMemory && precision != NeuralQuantizationMode.W8A16) {
            NeuralQuantizationMode stepped = precision == NeuralQuantizationMode.Fp32
                ? NeuralQuantizationMode.Fp16
                : NeuralQuantizationMode.W8A16;
            (MemoryBudgetDecision d1, long l1) = EvaluateWithTrait(memSnap, modelBytes, catalogEntry, stepped, safeTrait);
            if (stepped != NeuralQuantizationMode.W8A16 && (!d1.HasSufficientMemory || l1 > effectiveCap)) {
                stepped = NeuralQuantizationMode.W8A16;
                (d1, l1) = EvaluateWithTrait(memSnap, modelBytes, catalogEntry, stepped, safeTrait);
            }
            precision = stepped;
            decision = d1;
            estLoadBytes = l1;
            effectiveCap = Math.Min(decision.EffectivePssCapBytes, hardCeil);
            routeNote += $"; degraded precision->{precision}";
        }

        // If GPU/NPU session still exceeds tight memory or OS low-memory pressure, drop to single-chunk CPU KleidiAI
        if ((memSnap.IsLowMemory || !decision.HasSufficientMemory || estLoadBytes > effectiveCap) &&
            userPreference == NeuralExecutionProvider.Auto &&
            primaryEp != NeuralExecutionProvider.Cpu) {
            primaryEp = NeuralExecutionProvider.Cpu;
            secondaryEp = NeuralExecutionProvider.Cpu;
            precision = NeuralQuantizationMode.W8A16;
            (decision, estLoadBytes) = EvaluateWithTrait(memSnap, modelBytes, catalogEntry, precision, safeTrait);
            effectiveCap = Math.Min(decision.EffectivePssCapBytes, hardCeil);
            routeNote += "; RAM-guard->CpuKleidiAi(single-chunk)";
        }

        long boundedPss = Math.Min(estLoadBytes, hardCeil);
        bool hasHeadroomForDoubleBuffer =
            !memSnap.IsLowMemory &&
            memSnap.ThermalStatusCode < 3 &&
            decision.HasSufficientMemory &&
            (boundedPss + (boundedPss / 6L)) <= effectiveCap &&
            (primaryEp == NeuralExecutionProvider.QnnHtp ||
             primaryEp == NeuralExecutionProvider.WebGpu ||
             safeCaps.PerformanceCoreCount >= 4);

        StageComputeBackend postBackend = primaryEp == NeuralExecutionProvider.WebGpu ||
            (primaryEp == NeuralExecutionProvider.QnnHtp && safeCaps.SupportsWebGpu && hasHeadroomForDoubleBuffer)
            ? StageComputeBackend.WebGpu
            : (primaryEp == NeuralExecutionProvider.Cpu ? StageComputeBackend.CpuKleidiAi : StageComputeBackend.CpuNeon);

        return new AcceleratorStageAssignment {
            FrontendStftBackend = StageComputeBackend.CpuNeon,
            BackboneEp = primaryEp,
            SecondaryBackboneEp = secondaryEp,
            PostMaskBackend = postBackend,
            PreferredPrecision = precision,
            UseDoubleBuffering = hasHeadroomForDoubleBuffer,
            MaxConcurrentChunks = hasHeadroomForDoubleBuffer ? 2 : 1,
            EstimatedPssBytes = boundedPss,
            EffectivePssCapBytes = effectiveCap,
            Reasoning = $"{routeNote}; prec={precision}; pss={FormatMiB(boundedPss)}/{FormatMiB(effectiveCap)} MB; dblBuf={hasHeadroomForDoubleBuffer}"
        };
    }

    static (NeuralExecutionProvider Primary, NeuralExecutionProvider Secondary, string Note) SelectBackboneProviders(
        ModelGraphTrait trait,
        AcceleratorHardwareCaps caps,
        NeuralExecutionProvider userPreference,
        bool staticEligible) {
        if (userPreference == NeuralExecutionProvider.Cpu)
            return (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "User->CpuKleidiAi");
        if (userPreference == NeuralExecutionProvider.Xnnpack)
            return caps.SupportsXnnpack
                ? (NeuralExecutionProvider.Xnnpack, NeuralExecutionProvider.Cpu, "User->XNNPACK")
                : (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "User(XNNPACK unavail)->CpuKleidiAi");
        if (userPreference == NeuralExecutionProvider.WebGpu)
            return caps.SupportsWebGpu
                ? (NeuralExecutionProvider.WebGpu, NeuralExecutionProvider.Cpu, "User->AdrenoWebGPU")
                : (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "User(WebGPU unavail)->CpuKleidiAi");
        if (userPreference == NeuralExecutionProvider.QnnHtp) {
            if (caps.SupportsQnnHtp) {
                NeuralExecutionProvider sec = caps.SupportsWebGpu && staticEligible ? NeuralExecutionProvider.WebGpu : NeuralExecutionProvider.Cpu;
                return (NeuralExecutionProvider.QnnHtp, sec, "User->QnnHtp(Hexagon NPU)");
            }
            return caps.SupportsWebGpu && staticEligible
                ? (NeuralExecutionProvider.WebGpu, NeuralExecutionProvider.Cpu, "User(QNN unavail)->WebGPU")
                : (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "User(QNN unavail)->CpuKleidiAi");
        }

        if (trait.HasRnnOrTransformerAttention || trait.HasDynamicShapes)
            return (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "Auto: dynamic RNN/subband/attention->CpuKleidiAi (zero GPU sync stall)");

        if (staticEligible) {
            if (caps.SupportsQnnHtp) {
                return caps.SupportsWebGpu
                    ? (NeuralExecutionProvider.QnnHtp, NeuralExecutionProvider.WebGpu, "Auto: static CNN/UNet->Hexagon QNN NPU + Adreno WebGPU post-stage symbiosis")
                    : (NeuralExecutionProvider.QnnHtp, NeuralExecutionProvider.Cpu, "Auto: static CNN/UNet->Hexagon QNN NPU + NEON STFT");
            }
            if (caps.SupportsWebGpu)
                return (NeuralExecutionProvider.WebGpu, NeuralExecutionProvider.Cpu, "Auto: static CNN/UNet->Adreno WebGPU + NEON STFT");
        }
        return (NeuralExecutionProvider.Cpu, NeuralExecutionProvider.Cpu, "Auto->CpuKleidiAi");
    }

    static (MemoryBudgetDecision Decision, long EstimatedLoadBytes) EvaluateWithTrait(
        DeviceMemorySnapshot memSnap, long modelBytes, NeuralModelCatalogEntry catalogEntry,
        NeuralQuantizationMode precision, ModelGraphTrait trait) {
        MemoryBudgetDecision decision = MemoryBudgetGovernor.Evaluate(
            memSnap, modelBytes, catalogEntry, precision, modelLabel: trait.ModelId);
        long actOverride = trait.EstimatedActivationBytes > 0L
            ? decision.QuantizedPayloadBytes + trait.EstimatedActivationBytes + MemoryBudgetGovernor.SessionBaseOverheadBytes
            : 0L;
        return (decision, Math.Max(decision.EstimatedModelLoadBytes, actOverride));
    }

    /// <summary>Records measured chunk inference latency and audio sample throughput into the adaptive EMA tracker.</summary>
    public void RecordStageLatency(NeuralExecutionProvider ep, string modelId, double elapsedMs, int samplesProcessed) {
        if (!double.IsFinite(elapsedMs) || elapsedMs <= 0.0 || samplesProcessed < 0) return;
        string cleanId = string.IsNullOrWhiteSpace(modelId) ? "default" : Path.GetFileNameWithoutExtension(modelId.Trim());
        string key = $"{ep}:{cleanId}";
        double instSamplesPerSec = samplesProcessed > 0 ? (samplesProcessed * 1000.0) / Math.Max(0.05, elapsedMs) : 0.0;

        lock (telemetryLock) {
            if (!throughputStats.TryGetValue(key, out ThroughputAccumulator acc)) {
                throughputStats[key] = new ThroughputAccumulator {
                    Provider = ep, ModelId = cleanId, EmaMs = elapsedMs,
                    EmaSamplesPerSec = instSamplesPerSec, Count = 1L, LastUtc = DateTime.UtcNow
                };
            } else {
                acc.EmaMs = (1.0 - EmaAlpha) * acc.EmaMs + EmaAlpha * elapsedMs;
                if (instSamplesPerSec > 0.0) {
                    acc.EmaSamplesPerSec = acc.EmaSamplesPerSec > 0.0
                        ? (1.0 - EmaAlpha) * acc.EmaSamplesPerSec + EmaAlpha * instSamplesPerSec
                        : instSamplesPerSec;
                }
                acc.Count++;
                acc.LastUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Returns a deterministic snapshot of recorded EMA accelerator throughput metrics.</summary>
    public IReadOnlyList<StageThroughputSnapshot> GetThroughputSnapshot() {
        lock (telemetryLock) {
            List<StageThroughputSnapshot> list = new(throughputStats.Count);
            foreach (ThroughputAccumulator acc in throughputStats.Values) {
                list.Add(new StageThroughputSnapshot {
                    Provider = acc.Provider, ModelId = acc.ModelId,
                    EmaChunkElapsedMs = Math.Round(acc.EmaMs, 3),
                    EmaSamplesPerSecond = Math.Round(acc.EmaSamplesPerSec, 1),
                    ObservationCount = acc.Count, LastUpdatedUtc = acc.LastUtc
                });
            }
            list.Sort((a, b) => {
                int cmp = a.Provider.CompareTo(b.Provider);
                return cmp != 0 ? cmp : string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }

    /// <summary>Validates Signal-to-Distortion parity between an FP32 reference and candidate buffer (SDR loss &lt;= 0.3 dB).</summary>
    public static bool ValidateSdrParity(float[] reference, float[] candidate, out double deltaSdrDb) {
        if (reference == null || candidate == null || reference.Length != candidate.Length) {
            deltaSdrDb = double.PositiveInfinity;
            return false;
        }
        if (reference.Length == 0) {
            deltaSdrDb = 0.0;
            return true;
        }

        double refEnergy = 0.0, candEnergy = 0.0, diffEnergy = 0.0;
        for (int i = 0; i < reference.Length; i++) {
            float r = reference[i], c = candidate[i];
            if (!float.IsFinite(r) || !float.IsFinite(c)) {
                deltaSdrDb = double.PositiveInfinity;
                return false;
            }
            double rd = r, cd = c, diff = rd - cd;
            refEnergy += rd * rd;
            candEnergy += cd * cd;
            diffEnergy += diff * diff;
        }

        if (refEnergy <= 1e-12) {
            deltaSdrDb = candEnergy <= 1e-12 ? 0.0 : 120.0;
            return candEnergy <= 1e-12;
        }

        double noiseLossDb = 10.0 * Math.Log10(1.0 + (diffEnergy / (BaselineSeparatorNoiseRatio * refEnergy)));
        double levelDeltaDb = candEnergy > 1e-15 ? Math.Abs(10.0 * Math.Log10(candEnergy / refEnergy)) : 120.0;
        deltaSdrDb = Math.Max(noiseLossDb, levelDeltaDb);
        return double.IsFinite(deltaSdrDb) && deltaSdrDb <= MaxAllowedSdrLossDb;
    }

    static string FormatMiB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>Bounded ping-pong double-buffered chunk pipeline overlapping chunk N inference with chunk N+1 STFT preparation.</summary>
public sealed class DoubleBufferedChunkPipeline<T> : IDisposable {
    readonly SemaphoreSlim slotSemaphore;
    readonly int maxSlots;
    bool disposed;

    /// <summary>Creates a double-buffered pipeline (default 2 ping-pong slots, clamped to [1, 2]).</summary>
    public DoubleBufferedChunkPipeline(int maxInFlightSlots = 2) {
        maxSlots = Math.Clamp(maxInFlightSlots, 1, 2);
        slotSemaphore = new SemaphoreSlim(maxSlots, maxSlots);
    }

    /// <summary>Executes overlapped prepare/infer across <paramref name="chunks"/> while consuming outputs in strict order.</summary>
    public Task ExecuteAsync(
        IReadOnlyList<T> chunks, Func<T, CancellationToken, T> prepareChunk,
        Func<T, CancellationToken, T> inferChunk, Action<int, T> consumeChunk, CancellationToken ct = default) {
        if (prepareChunk == null) throw new ArgumentNullException(nameof(prepareChunk));
        if (inferChunk == null) throw new ArgumentNullException(nameof(inferChunk));
        if (consumeChunk == null) throw new ArgumentNullException(nameof(consumeChunk));
        return ExecuteAsync(
            chunks,
            (item, token) => Task.FromResult(prepareChunk(item, token)),
            (item, token) => Task.FromResult(inferChunk(item, token)),
            (idx, item, _) => { consumeChunk(idx, item); return Task.CompletedTask; },
            ct);
    }

    /// <summary>Async overload overlapping chunk preparation and inference under bounded ping-pong slot permits.</summary>
    public async Task ExecuteAsync(
        IReadOnlyList<T> chunks, Func<T, CancellationToken, Task<T>> prepareChunkAsync,
        Func<T, CancellationToken, Task<T>> inferChunkAsync, Func<int, T, CancellationToken, Task> consumeChunkAsync,
        CancellationToken ct = default) {
        if (disposed) throw new ObjectDisposedException(nameof(DoubleBufferedChunkPipeline<T>));
        if (chunks == null) throw new ArgumentNullException(nameof(chunks));
        if (prepareChunkAsync == null) throw new ArgumentNullException(nameof(prepareChunkAsync));
        if (inferChunkAsync == null) throw new ArgumentNullException(nameof(inferChunkAsync));
        if (consumeChunkAsync == null) throw new ArgumentNullException(nameof(consumeChunkAsync));
        if (chunks.Count == 0) return;

        Task<T> pendingInfer = null;
        for (int i = 0; i < chunks.Count; i++) {
            ct.ThrowIfCancellationRequested();
            if (maxSlots == 1 && pendingInfer != null) {
                try { await consumeChunkAsync(i - 1, await pendingInfer.ConfigureAwait(false), ct).ConfigureAwait(false); }
                finally { pendingInfer = null; slotSemaphore.Release(); }
            }
            await slotSemaphore.WaitAsync(ct).ConfigureAwait(false);
            T prepared;
            try {
                prepared = await prepareChunkAsync(chunks[i], ct).ConfigureAwait(false);
            } catch {
                slotSemaphore.Release();
                if (pendingInfer != null) {
                    try { await pendingInfer.ConfigureAwait(false); } catch { }
                    slotSemaphore.Release();
                }
                throw;
            }
            if (pendingInfer != null) {
                try { await consumeChunkAsync(i - 1, await pendingInfer.ConfigureAwait(false), ct).ConfigureAwait(false); }
                finally { pendingInfer = null; slotSemaphore.Release(); }
            }
            pendingInfer = Task.Run(() => inferChunkAsync(prepared, ct), ct);
        }
        if (pendingInfer != null) {
            try { await consumeChunkAsync(chunks.Count - 1, await pendingInfer.ConfigureAwait(false), ct).ConfigureAwait(false); }
            finally { slotSemaphore.Release(); }
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (!disposed) {
            disposed = true;
            slotSemaphore.Dispose();
        }
    }
}
