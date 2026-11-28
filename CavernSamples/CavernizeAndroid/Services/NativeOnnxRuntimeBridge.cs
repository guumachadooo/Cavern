using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;


/// <summary>
/// Dynamic Android native bridge for <c>libonnxruntime.so</c> bundled in <see cref="Android.Content.PM.ApplicationInfo.NativeLibraryDir"/>.
/// Implements <see cref="INeuralInferenceRuntime"/> and <see cref="INeuralModelRegistry"/> using
/// companion <c>.so</c> pre-loading + <see cref="NativeLibrary.Load(string)"/> + <c>OrtGetApiBase()</c> +
/// asset-bundled <c>onnxruntime_c_api.h</c> vtable slots, with automatic QNN HTP -> XNNPACK -> CPU provider cascade.
/// </summary>
public sealed partial class NativeOnnxRuntimeBridge : INeuralInferenceRuntime, INeuralModelRegistry {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr OrtGetApiBaseDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetApiDelegate(uint version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetVersionStringDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateEnvDelegate(int logSeverityLevel, [MarshalAs(UnmanagedType.LPUTF8Str)] string logId, out IntPtr env);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateSessionOptionsDelegate(out IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetIntraOpNumThreadsDelegate(IntPtr options, int intraOpNumThreads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetInterOpNumThreadsDelegate(IntPtr options, int interOpNumThreads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetGraphOptimizationLevelDelegate(IntPtr options, int graphOptimizationLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SetOptimizedModelFilePathDelegate(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string optimizedModelFilePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr EnableSessionOptionFlagDelegate(IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AddSessionConfigEntryDelegate(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateRunOptionsDelegate(out IntPtr options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AddRunConfigEntryDelegate(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionOptionsAppendEpDelegate(
        IntPtr options,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string providerName,
        IntPtr[] keys,
        IntPtr[] values,
        UIntPtr numKeys);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateSessionDelegate(IntPtr env, [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath, IntPtr options, out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetCountDelegate(IntPtr session, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetNameDelegate(IntPtr session, UIntPtr index, IntPtr allocator, out IntPtr namePtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr SessionGetTypeInfoDelegate(IntPtr session, UIntPtr index, out IntPtr typeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CastTypeInfoToTensorInfoDelegate(IntPtr typeInfo, out IntPtr tensorInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetAllocatorWithDefaultOptionsDelegate(out IntPtr allocator);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AllocatorFreeDelegate(IntPtr allocator, IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateCpuMemoryInfoDelegate(int allocType, int memType, out IntPtr memInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr CreateTensorWithDataAsOrtValueDelegate(
        IntPtr info,
        IntPtr pData,
        UIntPtr pDataByteLength,
        long[] shape,
        UIntPtr shapeLen,
        int tensorElementDataType,
        out IntPtr ortValue);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr RunDelegate(
        IntPtr session,
        IntPtr runOptions,
        IntPtr[] inputNames,
        IntPtr[] inputs,
        UIntPtr inputLen,
        IntPtr[] outputNames,
        UIntPtr outputNamesLen,
        IntPtr[] outputs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorMutableDataDelegate(IntPtr value, out IntPtr pData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorTypeAndShapeDelegate(IntPtr value, out IntPtr typeAndShapeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetTensorElementTypeDelegate(IntPtr info, out int elementType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetDimensionsCountDelegate(IntPtr info, out UIntPtr count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetDimensionsDelegate(IntPtr info, long[] dimValues, UIntPtr dimValuesLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetErrorMessageDelegate(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ReleaseHandleDelegate(IntPtr handle);

    const int OrtLoggingLevelWarning = 2;
    const int OrtEnableAll = 99;
    const int OrtArenaAllocator = 0;
    const int OrtMemTypeDefault = 0;
    const int OnnxTensorElementDataTypeFloat = 1;
    const int OnnxTensorElementDataTypeFloat16 = 10;
    const int OnnxTensorElementDataTypeDouble = 11;
    const long MaxTensorElements = 64L * 1024L * 1024L; // 256 MB float32 ceiling to prevent mobile OOM / 32-bit length overflow
    const float Float16FiniteMax = 65504f;
    const long UltraHeavyModelBytesThreshold = 400L * 1024L * 1024L; // 400 MB+ models also disable mem-pattern & weight prepacking
    const long MinimumSafetyHeadroomBytes = 192L * 1024L * 1024L; // 192 MB OS/audio engine LMK safety headroom (fallback when ActivityManager unavailable)
    const long MaxSingleProcessModelPssBytes = 2600L * 1024L * 1024L; // Android LMKD single-process PSS ceiling on <=12 GB phones (T0 floor ceiling)

    [DllImport("libc", EntryPoint = "setenv", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeSetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int overwrite);

    static readonly string[] PreferredCompanionLoadOrder = [
        "libonnxruntime_providers_shared.so",
        "libQnnSystem.so",
        "libQnnHtp.so"
    ];

    readonly Context appContext;
    readonly object initLock = new();
    readonly List<IntPtr> companionLibHandles = [];

    IntPtr libHandle;
    IntPtr ortApiPtr;
    IntPtr ortEnv;
    IntPtr cpuMemoryInfo;
    IntPtr defaultAllocator;
    Dictionary<string, int> apiSlots = new(StringComparer.Ordinal);
    string loadedVersion = "";
    string lastActiveProvider = "";
    string lastInitError = "";
    bool supportsWebGpu;
    volatile bool webGpuDeviceLostThisProcess;
    bool supportsXnnpack;
    bool supportsQnnHtp;
    int activeSessionCount;
    bool disposeRequested;
    bool initialized;
    long latchedPssCapBytes;

    // Cached C API delegates
    CreateEnvDelegate fnCreateEnv;
    CreateSessionOptionsDelegate fnCreateSessionOptions;
    SetIntraOpNumThreadsDelegate fnSetIntraOpNumThreads;
    SetInterOpNumThreadsDelegate fnSetInterOpNumThreads;
    SetGraphOptimizationLevelDelegate fnSetGraphOpt;
    SetOptimizedModelFilePathDelegate fnSetOptimizedModelPath;
    EnableSessionOptionFlagDelegate fnEnableCpuMemArena;
    EnableSessionOptionFlagDelegate fnDisableCpuMemArena;
    EnableSessionOptionFlagDelegate fnEnableMemPattern;
    EnableSessionOptionFlagDelegate fnDisableMemPattern;
    AddSessionConfigEntryDelegate fnAddSessionConfig;
    CreateRunOptionsDelegate fnCreateRunOptions;
    AddRunConfigEntryDelegate fnAddRunConfig;
    SessionOptionsAppendEpDelegate fnAppendEp;
    CreateSessionDelegate fnCreateSession;
    SessionGetCountDelegate fnGetInputCount;
    SessionGetCountDelegate fnGetOutputCount;
    SessionGetNameDelegate fnGetInputName;
    SessionGetNameDelegate fnGetOutputName;
    SessionGetTypeInfoDelegate fnGetInputTypeInfo;
    CastTypeInfoToTensorInfoDelegate fnCastTypeInfoToTensorInfo;
    GetAllocatorWithDefaultOptionsDelegate fnGetAllocator;
    AllocatorFreeDelegate fnAllocatorFree;
    CreateCpuMemoryInfoDelegate fnCreateCpuMemInfo;
    CreateTensorWithDataAsOrtValueDelegate fnCreateTensor;
    RunDelegate fnRun;
    GetTensorMutableDataDelegate fnGetTensorData;
    GetTensorTypeAndShapeDelegate fnGetTensorTypeAndShape;
    GetTensorElementTypeDelegate fnGetTensorElementType;
    GetDimensionsCountDelegate fnGetDimsCount;
    GetDimensionsDelegate fnGetDims;
    GetErrorMessageDelegate fnGetErrorMessage;
    ReleaseHandleDelegate fnReleaseStatus;
    ReleaseHandleDelegate fnReleaseEnv;
    ReleaseHandleDelegate fnReleaseSession;
    ReleaseHandleDelegate fnReleaseSessionOptions;
    ReleaseHandleDelegate fnReleaseRunOptions;
    ReleaseHandleDelegate fnReleaseValue;
    ReleaseHandleDelegate fnReleaseMemoryInfo;
    ReleaseHandleDelegate fnReleaseTensorTypeAndShapeInfo;
    ReleaseHandleDelegate fnReleaseTypeInfo;

    public NativeOnnxRuntimeBridge(Context context) {
        appContext = context?.ApplicationContext ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public bool IsAvailable {
        get {
            lock (initLock) {
                return !disposeRequested && (initialized || OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext));
            }
        }
    }

    /// <summary>Last execution provider actually initialized or used by a session (or empty if not initialized yet).</summary>
    public string LastActiveProviderName {
        get {
            lock (initLock) {
                return lastActiveProvider;
            }
        }
    }

    /// <summary>True when the bundled ONNX Runtime supports the WebGPU (Vulkan GPU) execution provider.</summary>
    public bool SupportsWebGpu {
        get {
            lock (initLock) {
                return supportsWebGpu;
            }
        }
    }

    /// <summary>True when the bundled ONNX Runtime supports the XNNPACK execution provider.</summary>
    public bool SupportsXnnpack {
        get {
            lock (initLock) {
                return supportsXnnpack;
            }
        }
    }

    /// <summary>True when Qualcomm QNN HTP NPU execution provider is available and supported.</summary>
    public bool SupportsQnnHtp {
        get {
            lock (initLock) {
                return supportsQnnHtp;
            }
        }
    }

    /// <summary>
    /// Initializes native ONNX Runtime and probes WebGPU/XNNPACK/QNN availability on a background thread
    /// without blocking the Android UI thread.
    /// </summary>
    public Task ProbeCapabilitiesAsync() => Task.Run(() => {
        if (!OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext)) {
            return;
        }
        lock (initLock) {
            if (!disposeRequested && !initialized) {
                TryInitializeLocked();
            }
        }
    });

    /// <summary>
    /// Returns a human-readable summary of the real execution provider active for <paramref name="preferred"/>
    /// without blocking the UI thread or fabricating unavailable backends.
    /// </summary>
    public string DescribeEffectiveProvider(NeuralExecutionProvider preferred) {
        if (!OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext)) {
            return "Not Installed";
        }
        lock (initLock) {
            if (!initialized) {
                bool qnnOnDisk = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                                 OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
                return preferred switch {
                    NeuralExecutionProvider.WebGpu => "WebGPU (Vulkan GPU)",
                    NeuralExecutionProvider.QnnHtp => qnnOnDisk ? "QNN HTP (NPU)" : "WebGPU / CPU KleidiAI",
                    NeuralExecutionProvider.Xnnpack => "XNNPACK (ARM64 SIMD)",
                    NeuralExecutionProvider.Cpu => "CPU (ARM64 KleidiAI)",
                    _ => "WebGPU (Vulkan) + CPU KleidiAI"
                };
            }
            return preferred switch {
                NeuralExecutionProvider.WebGpu => supportsWebGpu ? "WebGPU (Vulkan GPU)" : "CPU (ARM64 KleidiAI)",
                NeuralExecutionProvider.QnnHtp => supportsQnnHtp ? "QNN HTP (NPU)" : (supportsWebGpu ? "WebGPU (Vulkan GPU)" : "CPU (ARM64 KleidiAI)"),
                NeuralExecutionProvider.Xnnpack => supportsXnnpack ? "XNNPACK (ARM64 SIMD)" : "CPU (ARM64 KleidiAI)",
                NeuralExecutionProvider.Cpu => "CPU (ARM64 KleidiAI)",
                _ => !string.IsNullOrEmpty(lastActiveProvider)
                    ? lastActiveProvider
                    : (supportsWebGpu ? "WebGPU (Vulkan GPU) + CPU KleidiAI" : (supportsQnnHtp ? "QNN HTP (NPU)" : "CPU (ARM64 KleidiAI)"))
            };
        }
    }

    /// <inheritdoc/>
    public NeuralRuntimeInfo GetRuntimeInfo() {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext);
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                   OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        bool ready;
        string version;
        string activeEp;

        lock (initLock) {
            if (!disposeRequested && installed && !initialized) {
                TryInitializeLocked();
            }
            ready = !disposeRequested && installed && initialized;
            version = !string.IsNullOrEmpty(loadedVersion) ? loadedVersion : (installed ? "installed" : string.Empty);
            activeEp = !ready
                ? "Not Installed"
                : (!string.IsNullOrEmpty(lastActiveProvider)
                    ? lastActiveProvider
                    : (supportsWebGpu ? "WebGPU (Vulkan GPU)" : (supportsQnnHtp ? "QNN HTP (NPU)" : "CPU (ARM64 KleidiAI)")));
        }

        return new NeuralRuntimeInfo {
            IsAvailable = ready,
            LibraryPath = installed ? OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext) : string.Empty,
            VersionString = version,
            ActiveProvider = activeEp,
            IsQnnAvailable = qnn && supportsQnnHtp,
            DiagnosticMessage = ready
                ? string.Empty
                : (installed
                    ? (!string.IsNullOrEmpty(lastInitError) ? lastInitError : "Native ONNX Runtime failed to initialize.")
                    : "ONNX Runtime is not installed.")
        };
    }

    /// <inheritdoc/>
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() =>
        OnnxRuntimeInstallerService.GetAllRegisteredModels(appContext);

    /// <inheritdoc/>
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() =>
        OnnxRuntimeInstallerService.GetInstalledModelEntries(appContext);

    /// <inheritdoc/>
    public string ResolveInstalledModelPath(string modelId) {
        string path = OnnxRuntimeInstallerService.ResolveModelPath(appContext, modelId);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <summary>
    /// Evaluates current Android system RAM against the estimated peak memory required to load
    /// <paramref name="modelPath"/> (including any companion ONNX external-data tensor files) into ONNX Runtime.
    /// </summary>
    public ModelMemoryCheckResult CheckMemoryForModel(string modelPath) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            return new ModelMemoryCheckResult {
                HasSufficientMemory = false,
                DiagnosticMessage = "ONNX model file not found."
            };
        }
        long primaryBytes = new FileInfo(modelPath).Length;
        long totalModelBytes = ResolveTotalModelSizeBytes(modelPath, primaryBytes);
        return CheckMemoryForModelBytes(totalModelBytes, Path.GetFileName(modelPath));
    }

    /// <summary>
    /// Evaluates current Android system RAM, thermal status, and own-UID historical LMK exit telemetry via
    /// <see cref="MemoryBudgetGovernor"/> without touching disk or initializing a session.
    /// Accounts for automatic or user-selected W8A16/FP16 mobile quantization before enforcing the clamped PSS cap (&lt;= 2600 MB).
    /// </summary>
    public ModelMemoryCheckResult CheckMemoryForModelBytes(
        long modelBytes,
        string modelLabel = "",
        NeuralQuantizationMode? effectiveQuantMode = null) {
        long safeModelBytes = Math.Max(0L, modelBytes);
        NeuralModelCatalogEntry catalogEntry = NeuralModelCatalog.FindByFileNameOrId(modelLabel);
        NeuralQuantizationMode quantMode = effectiveQuantMode ?? OnnxMobileQuantizer.ResolveEffectiveMode(
            PreferredQuantizationMode,
            safeModelBytes,
            NeuralExecutionProvider.Cpu,
            IsWebGpuOptimalModel(modelLabel));
        long _ = EstimateQuantizedPayloadBytes(safeModelBytes, quantMode);

        DeviceMemorySnapshot snapshot = CaptureDeviceMemorySnapshot();
        long prevLatched = Interlocked.Read(ref latchedPssCapBytes);
        MemoryBudgetDecision decision = MemoryBudgetGovernor.Evaluate(
            snapshot,
            safeModelBytes,
            catalogEntry,
            quantMode,
            prevLatched > 0L ? prevLatched : null,
            modelLabel);
        long clampedCap = Math.Min(decision.EffectivePssCapBytes, MaxSingleProcessModelPssBytes);
        Interlocked.Exchange(ref latchedPssCapBytes, clampedCap);

        return new ModelMemoryCheckResult {
            HasSufficientMemory = decision.HasSufficientMemory,
            IsLowMemoryState = decision.IsLowMemoryState,
            ModelFileBytes = safeModelBytes,
            EstimatedRequiredBytes = decision.RequiredBudgetBytes,
            AvailableSystemBytes = snapshot.AvailableBytes,
            TotalSystemBytes = snapshot.TotalBytes,
            LowMemoryThresholdBytes = snapshot.LowMemoryThresholdBytes,
            SafetyHeadroomBytes = decision.SafetyHeadroomBytes,
            Source = snapshot.Source ?? "Unavailable",
            DiagnosticMessage = decision.DiagnosticMessage
        };
    }

    DeviceMemorySnapshot CaptureDeviceMemorySnapshot() {
        int thermalStatus = AndroidRuntimeTelemetry.GetThermalStatusCode(appContext);
        AndroidRuntimeTelemetry.TryGetRecentLowMemoryExitInfo(
            appContext,
            out bool hadRecentLowMemoryExit,
            out long lastLowMemoryExitPssBytes);
        if (!TryReadSystemMemory(out long availBytes, out long totalBytes, out long thresholdBytes, out bool isLowMemory, out string source)) {
            return new DeviceMemorySnapshot {
                AvailableBytes = 0L,
                TotalBytes = 0L,
                LowMemoryThresholdBytes = 0L,
                IsLowMemory = false,
                ThermalStatusCode = thermalStatus,
                HadRecentLowMemoryExit = hadRecentLowMemoryExit,
                LastLowMemoryExitPssBytes = lastLowMemoryExitPssBytes,
                Source = "Unavailable"
            };
        }
        return new DeviceMemorySnapshot {
            AvailableBytes = availBytes,
            TotalBytes = totalBytes,
            LowMemoryThresholdBytes = thresholdBytes,
            IsLowMemory = isLowMemory,
            ThermalStatusCode = thermalStatus,
            HadRecentLowMemoryExit = hadRecentLowMemoryExit,
            LastLowMemoryExitPssBytes = lastLowMemoryExitPssBytes,
            Source = source
        };
    }

    /// <summary>
    /// Detects the optimal ARM64 intra-op CPU thread count for audio separation graphs.
    /// Empirically verified on Snapdragon 8 Elite (2× Prime + 6× Performance Oryon): 4 threads
    /// avoids cross-cluster barrier contention and thermal boost drops (32% faster than 6 threads,
    /// 61% faster than 8 threads on SCNet FP16, while leaving 4 cores free for UI and low-latency audio).
    /// </summary>
    static int DetectPerformanceCoreCount() {
        int totalCores = Math.Max(1, Environment.ProcessorCount);
        try {
            long[] freqs = new long[totalCores];
            long maxFreq = 0L;
            int valid = 0;
            for (int i = 0; i < totalCores; i++) {
                string path = $"/sys/devices/system/cpu/cpu{i}/cpufreq/cpuinfo_max_freq";
                if (!File.Exists(path)) {
                    continue;
                }
                string raw = File.ReadAllText(path).Trim();
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long khz) && khz > 0) {
                    freqs[i] = khz;
                    valid++;
                    if (khz > maxFreq) {
                        maxFreq = khz;
                    }
                }
            }
            if (valid >= 4 && maxFreq > 0) {
                long perfThreshold = (long)(maxFreq * 0.72);
                int perfCores = 0;
                for (int i = 0; i < totalCores; i++) {
                    if (freqs[i] >= perfThreshold) {
                        perfCores++;
                    }
                }
                if (perfCores >= 2) {
                    int cap = Cavern.Neural.MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(totalCores);
                    return Math.Clamp(Math.Min(perfCores, cap), 1, cap);
                }
            }
        } catch {
            // Fall through to core-count heuristic
        }
        return Cavern.Neural.MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(totalCores);
    }

    /// <summary>
    /// Returns true when <paramref name="modelPath"/> matches static CNN / U-Net STFT architectures (e.g. UVR MDX-Net,
    /// Kim Vocal, Reverb HQ, Apollo) that achieve 4-5× speedup on WebGPU Vulkan GPUs.
    /// Dynamic subband-slicing / dual-path RNN / axial-attention Transformer graphs (SCNet, HTDemucs, BS/Mel-Band RoFormer)
    /// either suffer CPU-GPU sync stalls or Dawn unified-RAM buffer explosion (4.6+ GB PSS LMKD kills on mobile Adreno);
    /// Auto mode routes them to ARM64 KleidiAI CPU with bounded memory arenas.
    /// </summary>
    static bool IsWebGpuOptimalModel(string modelPath) {
        string name = Path.GetFileName(modelPath ?? string.Empty).ToLowerInvariant();
        if (name.Contains("scnet") || name.Contains("demucs") || name.Contains("roformer")) {
            return false;
        }
        if (name.Contains("webgpu")) {
            return true;
        }
        return true;
    }

    /// <summary>
    /// Returns true for large (&gt;=200 MB) or multi-branch/axial-attention audio models that require
    /// strict ORT memory-arena discipline, resolved deterministically via <see cref="MemoryBudgetGovernor"/>.
    /// </summary>
    internal static bool IsMemoryHeavyModel(string modelPathOrLabel, long modelBytes) =>
        MemoryBudgetGovernor.IsMemoryHeavy(modelBytes, NeuralModelCatalog.FindByFileNameOrId(modelPathOrLabel));

    string TryResolveOptimizedCachePath(string modelPath, long modelBytes, NeuralExecutionProvider ep) {
        if (ep == NeuralExecutionProvider.QnnHtp || ep == NeuralExecutionProvider.WebGpu || IsMemoryHeavyModel(modelPath, modelBytes)) {
            return null;
        }
        // Only cache self-contained ONNX files (avoid splitting external-data checkpoints)
        if (File.Exists(modelPath + ".data") || File.Exists(modelPath + "_data")) {
            return null;
        }
        try {
            string cacheRoot = appContext.CacheDir?.AbsolutePath;
            if (string.IsNullOrEmpty(cacheRoot)) {
                return null;
            }
            string optDir = Path.Combine(cacheRoot, "ort_opt");
            Directory.CreateDirectory(optDir);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            string safeVer = string.IsNullOrEmpty(loadedVersion) ? "ort" : loadedVersion.Replace('.', '_');
            string stamp = $"{baseName}_{modelBytes}_{ep}_{safeVer}.onnx";
            return Path.Combine(optDir, stamp);
        } catch {
            return null;
        }
    }

    /// <summary>Wave E (W53.5 / B12) preferred mobile ONNX weight precision mode (Auto, FP32, FP16, or W8A16).</summary>
    public NeuralQuantizationMode PreferredQuantizationMode { get; set; } = NeuralQuantizationMode.Auto;

    /// <summary>Wave E (W53.5 / B12) enables SoC-keyed QNN HTP Hexagon NPU context binary caching.</summary>
    public bool QnnContextCacheEnabled { get; set; } = true;

    /// <inheritdoc/>
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            throw new FileNotFoundException("ONNX model file not found.", modelPath);
        }
        long modelBytes = new FileInfo(modelPath).Length;
        if (modelBytes < 64) {
            throw new InvalidDataException($"ONNX model file '{Path.GetFileName(modelPath)}' is empty or truncated ({modelBytes} bytes).");
        }

        NeuralQuantizationMode plannedQuant = OnnxMobileQuantizer.ResolveEffectiveMode(
            PreferredQuantizationMode, modelBytes, preferredProvider, IsWebGpuOptimalModel(modelPath));
        EnsureSufficientMemoryForModel(modelPath, modelBytes, plannedQuant);

        lock (initLock) {
            if (disposeRequested) {
                throw new ObjectDisposedException(nameof(NativeOnnxRuntimeBridge));
            }
            if (!initialized && !TryInitializeLocked()) {
                string detail = !string.IsNullOrWhiteSpace(lastInitError)
                    ? lastInitError
                    : "Native library or C API header initialization failed.";
                throw new InvalidOperationException($"ONNX Runtime could not be initialized ({detail}).");
            }
            // Pin native runtime lifetime (ortEnv, cpuMemoryInfo, libHandle) while session creation & inference run
            activeSessionCount++;
        }

        bool handoffComplete = false;
        try {
            int headroomCap = Cavern.Neural.MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(Environment.ProcessorCount);
            int defaultThreads = Math.Min(DetectPerformanceCoreCount(), headroomCap);
            int threads = intraOpThreads > 0 ? Math.Clamp(intraOpThreads, 1, headroomCap) : defaultThreads;
            List<NeuralExecutionProvider> candidates = BuildSessionProviderCascade(preferredProvider, modelPath);
            Exception lastErr = null;
            DateTime srcWriteUtc = File.GetLastWriteTimeUtc(modelPath);

            for (int c = 0; c < candidates.Count; c++) {
                NeuralExecutionProvider candidate = candidates[c];
                string cachedOptPath = TryResolveOptimizedCachePath(modelPath, modelBytes, candidate);
                bool hasValidCachedOpt = !string.IsNullOrEmpty(cachedOptPath) &&
                    File.Exists(cachedOptPath) &&
                    new FileInfo(cachedOptPath).Length >= 1024 &&
                    File.GetLastWriteTimeUtc(cachedOptPath) >= srcWriteUtc;

                bool isWebGpuOptimal = IsWebGpuOptimalModel(modelPath);
                NeuralQuantizationMode effectiveQuantMode = OnnxMobileQuantizer.ResolveEffectiveMode(
                    PreferredQuantizationMode, modelBytes, candidate, isWebGpuOptimal);

                // Wave E (W53.5): Mobile ONNX FP16 / W8A16 weight-only quantization cache (cuts heavy FP32 models <1.4 GB PSS)
                string activePrecisionTag = "FP32";
                string quantizedModelPath = null;
                bool hasValidQuantCache = false;
                if (effectiveQuantMode == NeuralQuantizationMode.W8A16 ||
                    effectiveQuantMode == NeuralQuantizationMode.Fp16) {
                    string resolvedQuant = OnnxQnnContextCacheManager.ResolveOrBuildQuantizedModelPath(
                        appContext, modelPath, effectiveQuantMode, out activePrecisionTag);
                    if (!string.Equals(resolvedQuant, modelPath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(resolvedQuant)) {
                        quantizedModelPath = resolvedQuant;
                        hasValidQuantCache = true;
                    }
                    if (modelBytes >= 120L * 1024L * 1024L) {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    }
                }

                // Wave E (W53.5): SoC-keyed QNN HTP context binary cache (Build.Fingerprint + SocModel + ORT + model SHA-256)
                string qnnSourcePath = hasValidQuantCache ? quantizedModelPath : modelPath;
                string qnnCtxPath = null;
                bool hasValidQnnCtx = false;
                if (candidate == NeuralExecutionProvider.QnnHtp && QnnContextCacheEnabled) {
                    (qnnCtxPath, hasValidQnnCtx, _) = OnnxQnnContextCacheManager.ResolveContextBinaryPath(
                        appContext, qnnSourcePath, loadedVersion, effectiveQuantMode);
                }

                // Attempt cached quantized / QNN context / graph-opt load first; if unavailable or rejected, load original FP32 cleanly
                bool canTryCachedFirst = hasValidQnnCtx || hasValidQuantCache || hasValidCachedOpt;
                for (int attempt = canTryCachedFirst ? 0 : 1; attempt < 2; attempt++) {
                    bool usingQnnCtx = attempt == 0 && hasValidQnnCtx;
                    bool usingQuantCache = attempt == 0 && !usingQnnCtx && hasValidQuantCache;
                    bool usingCachedOpt = attempt == 0 && !usingQnnCtx && !usingQuantCache && hasValidCachedOpt;
                    string loadPath = usingQnnCtx
                        ? qnnCtxPath
                        : (usingQuantCache ? quantizedModelPath : (usingCachedOpt ? cachedOptPath : modelPath));
                    IntPtr sessOpts = IntPtr.Zero;
                    CheckStatus(fnCreateSessionOptions(out sessOpts));
                    try {
                        CheckStatus(fnSetIntraOpNumThreads(sessOpts, threads));
                        if (fnSetInterOpNumThreads != null) {
                            ReleaseStatusQuietly(fnSetInterOpNumThreads(sessOpts, 1));
                        }
                        bool memoryHeavy = IsMemoryHeavyModel(modelPath, modelBytes);
                        bool ultraHeavy = modelBytes >= UltraHeavyModelBytesThreshold && !usingQuantCache;
                        if (memoryHeavy && fnDisableCpuMemArena != null) {
                            ReleaseStatusQuietly(fnDisableCpuMemArena(sessOpts));
                        } else if (fnEnableCpuMemArena != null) {
                            ReleaseStatusQuietly(fnEnableCpuMemArena(sessOpts));
                        }
                        if (ultraHeavy && fnDisableMemPattern != null) {
                            ReleaseStatusQuietly(fnDisableMemPattern(sessOpts));
                        } else if (fnEnableMemPattern != null) {
                            ReleaseStatusQuietly(fnEnableMemPattern(sessOpts));
                        }
                        CheckStatus(fnSetGraphOpt(sessOpts, (usingCachedOpt || usingQnnCtx) ? 1 : OrtEnableAll));
                        if (!usingCachedOpt && !usingQuantCache && !usingQnnCtx && !string.IsNullOrEmpty(cachedOptPath) && fnSetOptimizedModelPath != null) {
                            ReleaseStatusQuietly(fnSetOptimizedModelPath(sessOpts, cachedOptPath));
                        }
                        if (fnAddSessionConfig != null) {
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.intra_op.allow_spinning", "0"));
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.inter_op.allow_spinning", "0"));
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.set_denormal_as_zero", "1"));
                            if (ultraHeavy) {
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.disable_prepacking", "1"));
                            }
                            if (candidate == NeuralExecutionProvider.QnnHtp && QnnContextCacheEnabled && !usingQnnCtx && !string.IsNullOrEmpty(qnnCtxPath)) {
                                try {
                                    if (File.Exists(qnnCtxPath)) File.Delete(qnnCtxPath);
                                } catch { }
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "ep.context_enable", "1"));
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "ep.context_embed_mode", "0"));
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "ep.context_file_path", qnnCtxPath));
                            }
                        }

                        string activeEp = ConfigureSingleExecutionProvider(sessOpts, candidate, threads);
                        if (candidate != NeuralExecutionProvider.Cpu && activeEp.StartsWith("CPU", StringComparison.Ordinal)) {
                            if (c + 1 < candidates.Count) {
                                break;
                            }
                        }
                        if (usingQuantCache) {
                            activeEp = $"{activeEp} [{activePrecisionTag}]";
                        }

                        Stopwatch swCreate = Stopwatch.StartNew();
                        IntPtr st = fnCreateSession(ortEnv, loadPath, sessOpts, out IntPtr sessionHandle);
                        swCreate.Stop();
                        if (st == IntPtr.Zero && sessionHandle != IntPtr.Zero) {
                            if (candidate == NeuralExecutionProvider.QnnHtp && QnnContextCacheEnabled && !usingQnnCtx && !string.IsNullOrEmpty(qnnCtxPath)) {
                                OnnxQnnContextCacheManager.RecordCompiledContextMetadata(
                                    appContext, qnnCtxPath, qnnSourcePath, loadedVersion, effectiveQuantMode);
                            }
                            lock (initLock) {
                                lastActiveProvider = activeEp;
                            }
                            string cacheTag = usingQnnCtx
                                ? " [qnn-soc-ctx-hit]"
                                : (usingQuantCache ? $" [quant-{activePrecisionTag.ToLowerInvariant()}]" : (usingCachedOpt ? " [opt-cache-hit]" : ""));
                            string memTag = ultraHeavy ? " [low-ram:no-arena+no-prepack]" : (memoryHeavy ? " [low-ram:no-arena]" : " [arena-shrink]");
                            AppLogger.Info($"ONNX session ready [{activeEp}]{cacheTag}{memTag} threads={threads} init={swCreate.ElapsedMilliseconds}ms model='{Path.GetFileName(modelPath)}'");
                            NativeOnnxSession session = new(this, modelPath, sessionHandle, activeEp, enableArenaShrinkage: !memoryHeavy);
                            handoffComplete = true;
                            return session;
                        }

                        string errMsg = ReadAndReleaseStatus(st);
                        if (usingQuantCache) {
                            try { File.Delete(quantizedModelPath); } catch { }
                            AppLogger.Warn($"Wave E {activePrecisionTag} session rejected for '{Path.GetFileName(modelPath)}'; falling back cleanly to FP32: {errMsg}");
                            continue;
                        }
                        if (usingCachedOpt) {
                            try { File.Delete(cachedOptPath); } catch { }
                            AppLogger.Warn($"ONNX opt-cache invalidated for '{Path.GetFileName(modelPath)}'; rebuilding cleanly.");
                            continue;
                        }
                        if (usingQnnCtx) {
                            try { File.Delete(qnnCtxPath); } catch { }
                            AppLogger.Warn($"Wave E SoC QNN context cache invalidated for '{Path.GetFileName(modelPath)}'; recompiling cleanly.");
                            continue;
                        }
                        lastErr = new InvalidOperationException($"ONNX Runtime error ({activeEp}): {errMsg}");
                        if (c + 1 < candidates.Count) {
                            AppLogger.Warn($"ONNX CreateSession failed on {activeEp} for '{Path.GetFileName(modelPath)}'; retrying next EP: {errMsg}");
                        }
                    } finally {
                        if (sessOpts != IntPtr.Zero) {
                            fnReleaseSessionOptions?.Invoke(sessOpts);
                        }
                    }
                }
            }

            throw lastErr ?? new InvalidOperationException($"Failed to create ONNX Runtime session for '{Path.GetFileName(modelPath)}'.");
        } finally {
            if (!handoffComplete) {
                ReleaseSessionRef();
            }
        }
    }

    internal static bool IsWebGpuDeviceLostError(string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return false;
        }
        return message.Contains("Device] is lost", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Device is lost", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("MapAsyncStatus", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("webgpu", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("buffer_manager", StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryRebuildSessionOnCpuAfterWebGpuLoss(string modelPath, out IntPtr newHandle, out string newEp, out bool enableArenaShrink) {
        newHandle = IntPtr.Zero;
        newEp = "CPU (ARM64 KleidiAI)";
        enableArenaShrink = false;
        webGpuDeviceLostThisProcess = true;
        lock (initLock) {
            supportsWebGpu = false;
            lastActiveProvider = newEp;
            if (disposeRequested || !initialized || ortEnv == IntPtr.Zero || fnCreateSessionOptions == null || fnCreateSession == null) {
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            return false;
        }
        IntPtr sessOpts = IntPtr.Zero;
        try {
            long modelBytes = new FileInfo(modelPath).Length;
            int headroomCap = Cavern.Neural.MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(Environment.ProcessorCount);
            int threads = Math.Min(DetectPerformanceCoreCount(), headroomCap);
            bool memoryHeavy = IsMemoryHeavyModel(modelPath, modelBytes);
            bool ultraHeavy = modelBytes >= UltraHeavyModelBytesThreshold;
            enableArenaShrink = !memoryHeavy;

            CheckStatus(fnCreateSessionOptions(out sessOpts));
            CheckStatus(fnSetIntraOpNumThreads(sessOpts, threads));
            if (fnSetInterOpNumThreads != null) {
                ReleaseStatusQuietly(fnSetInterOpNumThreads(sessOpts, 1));
            }
            if (memoryHeavy && fnDisableCpuMemArena != null) {
                ReleaseStatusQuietly(fnDisableCpuMemArena(sessOpts));
            } else if (fnEnableCpuMemArena != null) {
                ReleaseStatusQuietly(fnEnableCpuMemArena(sessOpts));
            }
            if (ultraHeavy && fnDisableMemPattern != null) {
                ReleaseStatusQuietly(fnDisableMemPattern(sessOpts));
            } else if (fnEnableMemPattern != null) {
                ReleaseStatusQuietly(fnEnableMemPattern(sessOpts));
            }
            CheckStatus(fnSetGraphOpt(sessOpts, OrtEnableAll));
            if (fnAddSessionConfig != null) {
                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.intra_op.allow_spinning", "0"));
                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.inter_op.allow_spinning", "0"));
                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.set_denormal_as_zero", "1"));
                if (ultraHeavy) {
                    ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.disable_prepacking", "1"));
                }
            }
            IntPtr st = fnCreateSession(ortEnv, modelPath, sessOpts, out IntPtr rebuilt);
            if (st == IntPtr.Zero && rebuilt != IntPtr.Zero) {
                newHandle = rebuilt;
                AppLogger.Info($"ONNX WebGPU recovery succeeded: switched '{Path.GetFileName(modelPath)}' transparently to [{newEp}] threads={threads}.");
                return true;
            }
            string err = ReadAndReleaseStatus(st);
            AppLogger.Warn($"ONNX WebGPU->CPU recovery session creation failed: {err}");
            return false;
        } catch (Exception ex) {
            AppLogger.Warn($"ONNX WebGPU->CPU recovery threw: {ex.Message}");
            return false;
        } finally {
            if (sessOpts != IntPtr.Zero) {
                fnReleaseSessionOptions?.Invoke(sessOpts);
            }
        }
    }

    void EnsureSufficientMemoryForModel(
        string modelPath,
        long primaryModelBytes,
        NeuralQuantizationMode? effectiveQuantMode = null) {
        long totalModelBytes = ResolveTotalModelSizeBytes(modelPath, primaryModelBytes);
        string fileName = Path.GetFileName(modelPath);
        ModelMemoryCheckResult check = CheckMemoryForModelBytes(totalModelBytes, fileName, effectiveQuantMode);
        if (!check.HasSufficientMemory) {
            // Reclaim any lingering managed audio buffers from prior runs once before rejecting load
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            check = CheckMemoryForModelBytes(totalModelBytes, fileName, effectiveQuantMode);
        }

        if (!check.HasSufficientMemory) {
            AppLogger.Warn($"ONNX memory guard blocked loading '{fileName}': {check.DiagnosticMessage}");
            throw new InsufficientMemoryException(
                $"Insufficient system RAM to safely load ONNX model '{fileName}' ({FormatMiB(totalModelBytes)} MB). " +
                $"Estimated peak requirement: {FormatMiB(check.EstimatedRequiredBytes)} MB (incl. headroom), " +
                $"available RAM: {FormatMiB(check.AvailableSystemBytes)} MB.");
        }

        AppLogger.Info($"ONNX memory guard passed: {check.DiagnosticMessage}");
    }

    static long EstimateQuantizedPayloadBytes(long rawModelBytes, NeuralQuantizationMode effectiveQuantMode) =>
        MemoryBudgetGovernor.EstimateQuantizedPayloadBytes(rawModelBytes, effectiveQuantMode);

    static long ResolveTotalModelSizeBytes(string modelPath, long primaryModelBytes) {
        long total = Math.Max(0L, primaryModelBytes);
        try {
            string[] companionCandidates = [
                modelPath + ".data",
                modelPath + "_data"
            ];
            for (int i = 0; i < companionCandidates.Length; i++) {
                string candidate = companionCandidates[i];
                if (File.Exists(candidate)) {
                    long extra = new FileInfo(candidate).Length;
                    if (extra > 0) {
                        total = total > long.MaxValue - extra ? long.MaxValue : total + extra;
                    }
                }
            }
        } catch {
            // Best-effort companion external-data inspection
        }
        return total;
    }

    bool TryReadSystemMemory(
        out long availableBytes,
        out long totalBytes,
        out long thresholdBytes,
        out bool isLowMemory,
        out string source) {
        availableBytes = 0L;
        totalBytes = 0L;
        thresholdBytes = 0L;
        isLowMemory = false;
        source = "Unavailable";

        try {
            if (appContext.GetSystemService(Context.ActivityService) is Android.App.ActivityManager activityManager) {
                using Android.App.ActivityManager.MemoryInfo memInfo = new();
                activityManager.GetMemoryInfo(memInfo);
                if (memInfo.AvailMem > 0) {
                    availableBytes = memInfo.AvailMem;
                    totalBytes = memInfo.TotalMem > 0 ? memInfo.TotalMem : 0L;
                    thresholdBytes = memInfo.Threshold > 0 ? memInfo.Threshold : 0L;
                    isLowMemory = memInfo.LowMemory;
                    source = "ActivityManager";
                    return true;
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"ActivityManager.GetMemoryInfo failed, falling back to /proc/meminfo: {ex.Message}");
        }

        if (TryReadProcMemInfo(out long procAvail, out long procTotal)) {
            availableBytes = procAvail;
            totalBytes = procTotal;
            thresholdBytes = MinimumSafetyHeadroomBytes;
            isLowMemory = procAvail < MinimumSafetyHeadroomBytes;
            source = "/proc/meminfo";
            return true;
        }

        return false;
    }

    static bool TryReadProcMemInfo(out long availableBytes, out long totalBytes) {
        availableBytes = 0L;
        totalBytes = 0L;
        try {
            if (!File.Exists("/proc/meminfo")) {
                return false;
            }
            long memAvailableKib = -1L;
            long memFreeKib = 0L;
            long buffersKib = 0L;
            long cachedKib = 0L;
            long memTotalKib = 0L;

            foreach (string line in File.ReadLines("/proc/meminfo")) {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) {
                    memTotalKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) {
                    memAvailableKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("MemFree:", StringComparison.Ordinal)) {
                    memFreeKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("Buffers:", StringComparison.Ordinal)) {
                    buffersKib = ParseMemInfoKib(line);
                } else if (line.StartsWith("Cached:", StringComparison.Ordinal)) {
                    cachedKib = ParseMemInfoKib(line);
                }
            }

            long effectiveAvailKib = memAvailableKib >= 0L
                ? memAvailableKib
                : (memFreeKib + buffersKib + cachedKib);
            if (effectiveAvailKib <= 0L) {
                return false;
            }

            availableBytes = effectiveAvailKib * 1024L;
            totalBytes = memTotalKib > 0L ? memTotalKib * 1024L : 0L;
            return true;
        } catch {
            return false;
        }
    }

    static long ParseMemInfoKib(string line) {
        int colon = line.IndexOf(':');
        if (colon < 0) {
            return 0L;
        }
        int i = colon + 1;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) {
            i++;
        }
        int start = i;
        while (i < line.Length && char.IsDigit(line[i])) {
            i++;
        }
        if (i <= start) {
            return 0L;
        }
        return long.TryParse(line.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long kib)
            ? kib
            : 0L;
    }

    static string FormatMiB(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    void ReleaseSessionRef() {
        lock (initLock) {
            if (activeSessionCount > 0) {
                activeSessionCount--;
            }
            if (disposeRequested && activeSessionCount == 0) {
                CleanupPartialInitLocked();
            }
        }
    }

}

/// <summary>
/// Result of comparing available Android system RAM against the estimated peak native memory
/// footprint required to load an ONNX model checkpoint safely.
/// </summary>
public readonly struct ModelMemoryCheckResult {
    /// <summary>True when available system RAM meets the estimated model load + safety headroom budget.</summary>
    public bool HasSufficientMemory { get; init; }

    /// <summary>True when Android reports the device is currently in a low-memory state.</summary>
    public bool IsLowMemoryState { get; init; }

    /// <summary>Combined size on disk of the ONNX checkpoint and any external tensor data files, in bytes.</summary>
    public long ModelFileBytes { get; init; }

    /// <summary>Estimated peak native RAM needed to load the model plus OS/audio headroom, in bytes.</summary>
    public long EstimatedRequiredBytes { get; init; }

    /// <summary>Available system RAM reported by Android at check time, in bytes.</summary>
    public long AvailableSystemBytes { get; init; }

    /// <summary>Total physical system RAM accessible to the kernel, in bytes.</summary>
    public long TotalSystemBytes { get; init; }

    /// <summary>Android low-memory threshold reported by ActivityManager, in bytes.</summary>
    public long LowMemoryThresholdBytes { get; init; }

    /// <summary>Effective safety headroom reserved for the OS and real-time audio engine, in bytes.</summary>
    public long SafetyHeadroomBytes { get; init; }

    /// <summary>Telemetry backend used ("ActivityManager", "/proc/meminfo", or "Unavailable").</summary>
    public string Source { get; init; }

    /// <summary>Human-readable summary suitable for device logs and error reporting.</summary>
    public string DiagnosticMessage { get; init; }
}
