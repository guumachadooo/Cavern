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
    const long HeavyModelBytesThreshold = 200L * 1024L * 1024L; // 200 MB+ models disable ORT power-of-2 CPU arena & opt-cache duplication
    const long UltraHeavyModelBytesThreshold = 400L * 1024L * 1024L; // 400 MB+ models also disable mem-pattern & weight prepacking
    const double ModelMemoryExpansionFactor = 2.5; // Standard CNN/MDX protobuf parse + graph opt + EP weight packing multiplier
    const double RoFormerMemoryExpansionFactor = 3.2; // Transformer/RoFormer axial-attention peak multiplier (w/ arena disabled)
    const long SessionBaseOverheadBytes = 32L * 1024L * 1024L; // 32 MB base ORT runtime & activation overhead
    const long MinimumSafetyHeadroomBytes = 192L * 1024L * 1024L; // 192 MB OS/audio engine LMK safety headroom
    const long HeavyModelSafetyHeadroomBytes = 384L * 1024L * 1024L; // 384 MB Android LMKD margin for 200+ MB Transformer graphs

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
    bool supportsXnnpack;
    bool supportsQnnHtp;
    int activeSessionCount;
    bool disposeRequested;
    bool initialized;

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
    /// Evaluates current Android system RAM against the estimated peak native memory required for a model
    /// of <paramref name="modelBytes"/> bytes without touching disk or initializing a session.
    /// </summary>
    public ModelMemoryCheckResult CheckMemoryForModelBytes(long modelBytes, string modelLabel = "") {
        long safeModelBytes = Math.Max(0L, modelBytes);
        bool memoryHeavy = IsMemoryHeavyModel(modelLabel, safeModelBytes);
        long estimatedModelBytes = EstimateRequiredModelBytes(safeModelBytes, modelLabel);

        if (!TryReadSystemMemory(out long availBytes, out long totalBytes, out long thresholdBytes, out bool isLowMemory, out string source)) {
            return new ModelMemoryCheckResult {
                HasSufficientMemory = true,
                IsLowMemoryState = false,
                ModelFileBytes = safeModelBytes,
                EstimatedRequiredBytes = estimatedModelBytes,
                AvailableSystemBytes = 0L,
                TotalSystemBytes = 0L,
                LowMemoryThresholdBytes = 0L,
                SafetyHeadroomBytes = memoryHeavy ? HeavyModelSafetyHeadroomBytes : MinimumSafetyHeadroomBytes,
                Source = "Unavailable",
                DiagnosticMessage = $"System RAM telemetry unavailable; proceeding with model '{modelLabel}' ({FormatMiB(safeModelBytes)} MB, est. {FormatMiB(estimatedModelBytes)} MB)."
            };
        }

        long headroomBytes = ComputeEffectiveSafetyHeadroom(totalBytes, thresholdBytes, memoryHeavy);
        long requiredBudgetBytes = estimatedModelBytes > long.MaxValue - headroomBytes
            ? long.MaxValue
            : estimatedModelBytes + headroomBytes;

        bool sufficient = !isLowMemory && availBytes >= requiredBudgetBytes;
        string namePart = string.IsNullOrWhiteSpace(modelLabel) ? "ONNX model" : $"'{modelLabel}'";
        string status = sufficient ? "OK" : (isLowMemory ? "LOW_MEMORY" : "INSUFFICIENT_RAM");
        string diag = $"{status} [{source}] for {namePart}: file={FormatMiB(safeModelBytes)} MB, estLoad={FormatMiB(estimatedModelBytes)} MB, headroom={FormatMiB(headroomBytes)} MB, need={FormatMiB(requiredBudgetBytes)} MB, avail={FormatMiB(availBytes)} MB / total={FormatMiB(totalBytes)} MB (lowMem={isLowMemory})";

        return new ModelMemoryCheckResult {
            HasSufficientMemory = sufficient,
            IsLowMemoryState = isLowMemory,
            ModelFileBytes = safeModelBytes,
            EstimatedRequiredBytes = requiredBudgetBytes,
            AvailableSystemBytes = availBytes,
            TotalSystemBytes = totalBytes,
            LowMemoryThresholdBytes = thresholdBytes,
            SafetyHeadroomBytes = headroomBytes,
            Source = source,
            DiagnosticMessage = diag
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
    /// Returns true for large (&gt;=200 MB) or Transformer/RoFormer/HTDemucs audio models that require
    /// strict ORT memory-arena discipline to avoid Android LMKD kills.
    /// </summary>
    internal static bool IsMemoryHeavyModel(string modelPathOrLabel, long modelBytes) {
        if (modelBytes >= HeavyModelBytesThreshold) {
            return true;
        }
        string name = Path.GetFileName(modelPathOrLabel ?? string.Empty).ToLowerInvariant();
        return name.Contains("roformer") || name.Contains("demucs");
    }

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

    string TryResolveWebGpuFp16CachePath(string modelPath, long modelBytes, DateTime srcWriteUtc) {
        if (!IsWebGpuOptimalModel(modelPath)) {
            return null;
        }
        string fileName = Path.GetFileName(modelPath ?? string.Empty);
        if (fileName.Contains("fp16", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        if (File.Exists(modelPath + ".data") || File.Exists(modelPath + "_data")) {
            return null;
        }
        try {
            string cacheRoot = appContext.CacheDir?.AbsolutePath;
            if (string.IsNullOrEmpty(cacheRoot)) {
                return null;
            }
            string fp16Dir = Path.Combine(cacheRoot, "ort_fp16");
            Directory.CreateDirectory(fp16Dir);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            return Path.Combine(fp16Dir, $"{baseName}_{modelBytes}_{srcWriteUtc.Ticks}_fp16.onnx");
        } catch {
            return null;
        }
    }

    string TryResolveQnnContextModelPath(string modelPath, long modelBytes) {
        try {
            string cacheRoot = appContext.CacheDir?.AbsolutePath;
            if (string.IsNullOrEmpty(cacheRoot)) {
                return null;
            }
            string qnnDir = Path.Combine(cacheRoot, "qnn_cache");
            Directory.CreateDirectory(qnnDir);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            string safeVer = string.IsNullOrEmpty(loadedVersion) ? "ort" : loadedVersion.Replace('.', '_');
            return Path.Combine(qnnDir, $"{baseName}_{modelBytes}_{safeVer}_qnn_ctx.onnx");
        } catch {
            return null;
        }
    }

    /// <inheritdoc/>
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            throw new FileNotFoundException("ONNX model file not found.", modelPath);
        }
        long modelBytes = new FileInfo(modelPath).Length;
        if (modelBytes < 64) {
            throw new InvalidDataException($"ONNX model file '{Path.GetFileName(modelPath)}' is empty or truncated ({modelBytes} bytes).");
        }

        EnsureSufficientMemoryForModel(modelPath, modelBytes);

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

                string qnnCtxPath = candidate == NeuralExecutionProvider.QnnHtp
                    ? TryResolveQnnContextModelPath(modelPath, modelBytes)
                    : null;
                bool hasValidQnnCtx = !string.IsNullOrEmpty(qnnCtxPath) &&
                    File.Exists(qnnCtxPath) &&
                    new FileInfo(qnnCtxPath).Length >= 1024 &&
                    File.GetLastWriteTimeUtc(qnnCtxPath) >= srcWriteUtc;

                // W46 IEEE-754 FP32 -> FP16 WebGPU cache (strictly no INT8 quantization):
                // Achieves ~1.40x real-time on Snapdragon Adreno Vulkan GPU (SNR > 37 dB, 0 NaN/Inf)
                // with automatic fallback to original FP32 if conversion or WebGPU session creation fails.
                string fp16CachePath = candidate == NeuralExecutionProvider.WebGpu
                    ? TryResolveWebGpuFp16CachePath(modelPath, modelBytes, srcWriteUtc)
                    : null;
                bool hasValidFp16Cache = !string.IsNullOrEmpty(fp16CachePath) &&
                    File.Exists(fp16CachePath) &&
                    new FileInfo(fp16CachePath).Length >= 1024 &&
                    File.GetLastWriteTimeUtc(fp16CachePath) >= srcWriteUtc;
                bool convertedFp16Now = false;
                if (!hasValidFp16Cache && !string.IsNullOrEmpty(fp16CachePath)) {
                    OnnxFp16ConversionResult fp16Res = OnnxFp16ModelConverter.TryConvertFileToFp16(modelPath, fp16CachePath);
                    if (fp16Res.Success) {
                        hasValidFp16Cache = true;
                        convertedFp16Now = true;
                        AppLogger.Info($"W46 WebGPU FP16 cache generated for '{Path.GetFileName(modelPath)}': {fp16Res.Message}");
                    } else if (!fp16Res.SkippedAlreadyFp16 && !fp16Res.SkippedExternalData) {
                        AppLogger.Warn($"W46 WebGPU FP16 conversion skipped for '{Path.GetFileName(modelPath)}': {fp16Res.Message}");
                    }
                }

                // Attempt cached FP16 / graph / QNN context load first; if unavailable or rejected, load original FP32
                bool canTryCachedFirst = hasValidFp16Cache || hasValidCachedOpt || hasValidQnnCtx;
                for (int attempt = canTryCachedFirst ? 0 : 1; attempt < 2; attempt++) {
                    bool usingFp16Cache = attempt == 0 && hasValidFp16Cache;
                    bool usingCachedOpt = attempt == 0 && !usingFp16Cache && hasValidCachedOpt;
                    bool usingQnnCtx = attempt == 0 && !usingFp16Cache && hasValidQnnCtx;
                    string loadPath = usingFp16Cache
                        ? fp16CachePath
                        : (usingQnnCtx ? qnnCtxPath : (usingCachedOpt ? cachedOptPath : modelPath));
                    IntPtr sessOpts = IntPtr.Zero;
                    CheckStatus(fnCreateSessionOptions(out sessOpts));
                    try {
                        // Keep full CPU intra-op threads even for XNNPACK (with spinning disabled per ORT guidance)
                        // so non-XNNPACK layers (ConvTranspose/Gelu/RNN) never starve on a single CPU core.
                        CheckStatus(fnSetIntraOpNumThreads(sessOpts, threads));
                        if (fnSetInterOpNumThreads != null) {
                            ReleaseStatusQuietly(fnSetInterOpNumThreads(sessOpts, 1));
                        }
                        // Memory discipline for mobile ARM64 (prevents 4.6 GB PSS Android LMKD kills on RoFormer/Demucs):
                        // - 200+ MB / RoFormer / Demucs: disable ORT's power-of-two CPU memory arena so huge attention
                        //   intermediates are returned immediately to Android instead of doubling and pinning forever.
                        // - 400+ MB (e.g. 909 MB Mel-Band RoFormer FP32): also disable static mem-pattern & weight
                        //   prepacking so ORT does not keep both raw protobuf and repacked weight copies simultaneously.
                        bool memoryHeavy = IsMemoryHeavyModel(modelPath, modelBytes);
                        bool ultraHeavy = modelBytes >= UltraHeavyModelBytesThreshold;
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
                        if (!usingCachedOpt && !usingFp16Cache && !string.IsNullOrEmpty(cachedOptPath) && fnSetOptimizedModelPath != null) {
                            ReleaseStatusQuietly(fnSetOptimizedModelPath(sessOpts, cachedOptPath));
                        }
                        if (fnAddSessionConfig != null) {
                            // Disable busy-spin waiting globally across all EPs so Android UI/SurfaceFlinger & RT audio
                            // threads never suffer big.LITTLE scheduler starvation or thermal burn during background AI.
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.intra_op.allow_spinning", "0"));
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.inter_op.allow_spinning", "0"));
                            ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.set_denormal_as_zero", "1"));
                            if (ultraHeavy) {
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.disable_prepacking", "1"));
                            }
                            if (candidate == NeuralExecutionProvider.QnnHtp && !usingQnnCtx && !string.IsNullOrEmpty(qnnCtxPath)) {
                                // Only pass ep.context_enable=1 when compiling the original model (never when loading existing ctx .onnx)
                                try {
                                    if (File.Exists(qnnCtxPath)) File.Delete(qnnCtxPath);
                                } catch { }
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "ep.context_enable", "1"));
                                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "ep.context_file_path", qnnCtxPath));
                            }
                        }

                        string activeEp = ConfigureSingleExecutionProvider(sessOpts, candidate, threads);
                        if (candidate != NeuralExecutionProvider.Cpu && activeEp.StartsWith("CPU", StringComparison.Ordinal)) {
                            // Requested accelerator EP could not be appended; skip duplicate CPU run if CPU is already queued next
                            if (c + 1 < candidates.Count) {
                                break;
                            }
                        }
                        if (usingFp16Cache && activeEp.Contains("WebGPU", StringComparison.OrdinalIgnoreCase)) {
                            activeEp = "WebGPU FP16 (Vulkan GPU)";
                        }

                        Stopwatch swCreate = Stopwatch.StartNew();
                        IntPtr st = fnCreateSession(ortEnv, loadPath, sessOpts, out IntPtr sessionHandle);
                        swCreate.Stop();
                        if (st == IntPtr.Zero && sessionHandle != IntPtr.Zero) {
                            lock (initLock) {
                                lastActiveProvider = activeEp;
                            }
                            string cacheTag = usingFp16Cache
                                ? (convertedFp16Now ? " [webgpu-fp16-conv]" : " [webgpu-fp16-hit]")
                                : (usingQnnCtx ? " [qnn-ctx-hit]" : (usingCachedOpt ? " [opt-cache-hit]" : ""));
                            string memTag = ultraHeavy ? " [low-ram:no-arena+no-prepack]" : (memoryHeavy ? " [low-ram:no-arena]" : " [arena-shrink]");
                            AppLogger.Info($"ONNX session ready [{activeEp}]{cacheTag}{memTag} threads={threads} init={swCreate.ElapsedMilliseconds}ms model='{Path.GetFileName(modelPath)}'");
                            NativeOnnxSession session = new(this, modelPath, sessionHandle, activeEp, enableArenaShrinkage: !memoryHeavy);
                            handoffComplete = true;
                            return session;
                        }

                        string errMsg = ReadAndReleaseStatus(st);
                        if (usingFp16Cache) {
                            try { File.Delete(fp16CachePath); } catch { }
                            AppLogger.Warn($"W46 WebGPU FP16 session rejected for '{Path.GetFileName(modelPath)}'; falling back cleanly to FP32: {errMsg}");
                            continue;
                        }
                        if (usingCachedOpt) {
                            try { File.Delete(cachedOptPath); } catch { }
                            AppLogger.Warn($"ONNX opt-cache invalidated for '{Path.GetFileName(modelPath)}'; rebuilding cleanly.");
                            continue;
                        }
                        if (usingQnnCtx) {
                            try { File.Delete(qnnCtxPath); } catch { }
                            AppLogger.Warn($"ONNX QNN context cache invalidated for '{Path.GetFileName(modelPath)}'; recompiling cleanly.");
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

    void EnsureSufficientMemoryForModel(string modelPath, long primaryModelBytes) {
        long totalModelBytes = ResolveTotalModelSizeBytes(modelPath, primaryModelBytes);
        string fileName = Path.GetFileName(modelPath);
        ModelMemoryCheckResult check = CheckMemoryForModelBytes(totalModelBytes, fileName);
        if (!check.HasSufficientMemory) {
            // Reclaim any lingering managed audio buffers from prior runs once before rejecting load
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            check = CheckMemoryForModelBytes(totalModelBytes, fileName);
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

    static long EstimateRequiredModelBytes(long modelBytes, string modelLabel = "") {
        if (modelBytes <= 0) {
            return SessionBaseOverheadBytes;
        }
        double factor = IsMemoryHeavyModel(modelLabel, modelBytes)
            ? RoFormerMemoryExpansionFactor
            : ModelMemoryExpansionFactor;
        double expanded = modelBytes * factor;
        if (!double.IsFinite(expanded) || expanded >= long.MaxValue - SessionBaseOverheadBytes) {
            return long.MaxValue;
        }
        return (long)Math.Ceiling(expanded) + SessionBaseOverheadBytes;
    }

    static long ComputeEffectiveSafetyHeadroom(long totalSystemBytes, long lowMemoryThresholdBytes, bool memoryHeavy = false) {
        long cappedThreshold = lowMemoryThresholdBytes > 0 ? lowMemoryThresholdBytes : 0L;
        if (totalSystemBytes > 0) {
            long quarterRam = totalSystemBytes / 4L;
            if (cappedThreshold > quarterRam) {
                cappedThreshold = quarterRam;
            }
        }
        long floorBytes = memoryHeavy ? HeavyModelSafetyHeadroomBytes : MinimumSafetyHeadroomBytes;
        return Math.Max(floorBytes, cappedThreshold);
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
