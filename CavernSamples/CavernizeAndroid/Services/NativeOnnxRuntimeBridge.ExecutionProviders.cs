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


public sealed partial class NativeOnnxRuntimeBridge {
    List<NeuralExecutionProvider> BuildSessionProviderCascade(NeuralExecutionProvider preferred, string modelPath) {
        List<NeuralExecutionProvider> list = [];
        bool qnnReady = supportsQnnHtp &&
                        OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                        OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        bool gpuOptimal = IsWebGpuOptimalModel(modelPath);

        if (preferred == NeuralExecutionProvider.WebGpu) {
            if (supportsWebGpu) {
                list.Add(NeuralExecutionProvider.WebGpu);
            }
            list.Add(NeuralExecutionProvider.Cpu);
            return list;
        }
        if (preferred == NeuralExecutionProvider.QnnHtp) {
            if (qnnReady) {
                list.Add(NeuralExecutionProvider.QnnHtp);
            }
            if (supportsWebGpu && gpuOptimal) {
                list.Add(NeuralExecutionProvider.WebGpu);
            }
            list.Add(NeuralExecutionProvider.Cpu);
            return list;
        }
        if (preferred == NeuralExecutionProvider.Xnnpack) {
            if (supportsXnnpack) {
                list.Add(NeuralExecutionProvider.Xnnpack);
            }
            list.Add(NeuralExecutionProvider.Cpu);
            return list;
        }
        if (preferred == NeuralExecutionProvider.Cpu) {
            list.Add(NeuralExecutionProvider.Cpu);
            return list;
        }

        // Auto mode: empirically verified fastest per-architecture cascade on mobile ARM64:
        // - Static CNN / U-Net STFT models (UVR MDX-Net Karaoke/Vocal/Reverb/Crowd, Apollo): WebGPU Vulkan GPU (5x faster!) -> CPU KleidiAI
        // - Dynamic subband/RNN models (SCNet, HTDemucs, RoFormer): CPU ARM64 KleidiAI 4-thread (3x faster than XNNPACK, zero GPU sync stalls!)
        if (supportsWebGpu && gpuOptimal) {
            list.Add(NeuralExecutionProvider.WebGpu);
        }
        if (qnnReady) {
            list.Add(NeuralExecutionProvider.QnnHtp);
        }
        list.Add(NeuralExecutionProvider.Cpu);
        return list;
    }

    string ConfigureSingleExecutionProvider(IntPtr sessOpts, NeuralExecutionProvider provider, int threads) {
        if (fnAppendEp == null || provider == NeuralExecutionProvider.Cpu) {
            return "CPU (ARM64 KleidiAI)";
        }
        if (provider == NeuralExecutionProvider.WebGpu) {
            if (TryAppendEp(sessOpts, "WebGPU", Array.Empty<string>(), Array.Empty<string>())) {
                return "WebGPU (Vulkan GPU)";
            }
            return "CPU (ARM64 KleidiAI)";
        }
        if (provider == NeuralExecutionProvider.QnnHtp &&
            OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
            OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice()) {
            EnsureQnnAdspLibraryPath();
            string qnnPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(appContext);
            string qnnMode = AndroidRuntimeTelemetry.GetQnnPerformanceMode();
            if (TryAppendEp(sessOpts, "QNN",
                ["backend_path", "htp_performance_mode", "enable_htp_fp16_precision", "htp_graph_finalization_optimization_mode"],
                [qnnPath, qnnMode, "1", "3"])) {
                return $"QNN HTP (NPU · {qnnMode})";
            }
            return "CPU (ARM64 KleidiAI)";
        }
        if (provider == NeuralExecutionProvider.Xnnpack) {
            if (TryAppendEp(sessOpts, "XNNPACK", ["intra_op_num_threads"], [threads.ToString()])) {
                return "XNNPACK (ARM64 SIMD)";
            }
            return "CPU (ARM64 KleidiAI)";
        }
        return "CPU (ARM64 KleidiAI)";
    }

    void EnsureQnnAdspLibraryPath() {
        try {
            string runtimeDir = OnnxRuntimeInstallerService.GetRuntimeDir(appContext);
            if (string.IsNullOrEmpty(runtimeDir)) {
                return;
            }
            string adsp = $"{runtimeDir};/vendor/dsp/cdsp;/vendor/lib/rfsa/adsp;/system/lib/rfsa/adsp;/dsp";
            NativeSetEnv("ADSP_LIBRARY_PATH", adsp, 1);
        } catch { }
    }

    void ProbeAvailableExecutionProvidersLocked() {
        supportsWebGpu = false;
        supportsXnnpack = false;
        supportsQnnHtp = false;
        if (fnCreateSessionOptions == null || fnReleaseSessionOptions == null || fnAppendEp == null) {
            lastActiveProvider = "CPU (ARM64 KleidiAI)";
            return;
        }

        // Probe each EP on an isolated OrtSessionOptions handle so we report exact hardware capabilities
        bool ProbeSingle(string epName, string[] keys, string[] values) {
            IntPtr st = fnCreateSessionOptions(out IntPtr opts);
            if (st != IntPtr.Zero || opts == IntPtr.Zero) {
                ReleaseStatusQuietly(st);
                return false;
            }
            try {
                return TryAppendEp(opts, epName, keys, values, quietOnUnsupported: true);
            } finally {
                fnReleaseSessionOptions(opts);
            }
        }

        supportsWebGpu = ProbeSingle("WebGPU", Array.Empty<string>(), Array.Empty<string>());
        supportsXnnpack = ProbeSingle("XNNPACK", ["intra_op_num_threads"], ["2"]);

        if (OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
            OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice()) {
            EnsureQnnAdspLibraryPath();
            string qnnPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(appContext);
            supportsQnnHtp = ProbeSingle("QNN",
                ["backend_path", "htp_performance_mode", "enable_htp_fp16_precision"],
                [qnnPath, AndroidRuntimeTelemetry.GetQnnPerformanceMode(), "1"]);
        }

        if (supportsWebGpu) {
            lastActiveProvider = "WebGPU (Vulkan GPU) + CPU KleidiAI";
        } else if (supportsQnnHtp) {
            lastActiveProvider = "QNN HTP (NPU)";
        } else if (supportsXnnpack) {
            lastActiveProvider = "CPU (ARM64 KleidiAI)";
        } else {
            lastActiveProvider = "CPU";
        }
    }

    bool TryAppendEp(IntPtr sessOpts, string epName, string[] keys, string[] values, bool quietOnUnsupported = false) {
        int count = keys?.Length ?? 0;
        IntPtr[] keyPtrs = count > 0 ? new IntPtr[count] : null;
        IntPtr[] valPtrs = count > 0 ? new IntPtr[count] : null;
        try {
            for (int i = 0; i < count; i++) {
                keyPtrs[i] = Marshal.StringToCoTaskMemUTF8(keys[i]);
                valPtrs[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            }
            IntPtr st = fnAppendEp(sessOpts, epName, keyPtrs, valPtrs, (UIntPtr)count);
            if (st == IntPtr.Zero) {
                return true;
            }
            string msg = ReadAndReleaseStatus(st);
            if (!quietOnUnsupported) {
                AppLogger.Warn($"ONNX Runtime EP '{epName}' unavailable, falling back: {msg}");
            }
            return false;
        } catch (Exception ex) {
            if (!quietOnUnsupported) {
                AppLogger.Warn($"ONNX Runtime EP '{epName}' threw: {ex.Message}");
            }
            return false;
        } finally {
            if (keyPtrs != null) {
                for (int i = 0; i < keyPtrs.Length; i++) {
                    if (keyPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(keyPtrs[i]);
                }
            }
            if (valPtrs != null) {
                for (int i = 0; i < valPtrs.Length; i++) {
                    if (valPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(valPtrs[i]);
                }
            }
        }
    }

    void PreloadCompanionLibrariesLocked(string runtimeDir) {
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir)) {
            return;
        }
        HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < PreferredCompanionLoadOrder.Length; i++) {
            string p = Path.Combine(runtimeDir, PreferredCompanionLoadOrder[i]);
            if (File.Exists(p) && loaded.Add(p)) {
                if (NativeLibrary.TryLoad(p, out IntPtr handle)) {
                    companionLibHandles.Add(handle);
                }
            }
        }
        try {
            foreach (string so in Directory.GetFiles(runtimeDir, "libQnn*.so")) {
                string fileName = Path.GetFileName(so);
                // Never dlopen 32-bit Hexagon DSP EM_QDSP6 Skel binaries or 80 MB HtpPrepare on ARM64 startup
                if (fileName.EndsWith("Skel.so", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains("Prepare", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                if (loaded.Add(so) && NativeLibrary.TryLoad(so, out IntPtr handle)) {
                    companionLibHandles.Add(handle);
                }
            }
        } catch { }
    }

    bool TryInitializeLocked() {
        if (initialized) {
            return true;
        }
        try {
            lastInitError = "";
            string runtimeDir = OnnxRuntimeInstallerService.GetRuntimeDir(appContext);
            string libPath = OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext);

            uint headerApiVersion = 17u;
            string parseDiag = "header asset is empty or missing";
            string headerText = OnnxRuntimeInstallerService.ReadHeaderText(appContext);
            if (!string.IsNullOrWhiteSpace(headerText)) {
                (headerApiVersion, apiSlots, parseDiag) = OnnxRuntimeHeaderParser.ParseOrtApiHeaderWithDiagnostics(headerText);
            }
            if (apiSlots.Count < 20) {
                lastInitError = $"ONNX Runtime C API header validation failed: {parseDiag}";
                AppLogger.Warn($"ONNX Runtime C API header missing from APK assets or failed anchor validation ({parseDiag}); cannot safely bind vtable.");
                return false;
            }

            PreloadCompanionLibrariesLocked(runtimeDir);
            if (!string.IsNullOrEmpty(libPath) && File.Exists(libPath)) {
                libHandle = NativeLibrary.Load(libPath);
            } else if (!NativeLibrary.TryLoad("onnxruntime", typeof(NativeOnnxRuntimeBridge).Assembly, null, out libHandle)) {
                lastInitError = "libonnxruntime.so could not be loaded from NativeLibraryDir";
                AppLogger.Warn($"ONNX Runtime native library failed to load: '{libPath}'");
                CleanupPartialInitLocked();
                return false;
            }
            IntPtr getBaseSym = NativeLibrary.GetExport(libHandle, "OrtGetApiBase");
            OrtGetApiBaseDelegate getBase = Marshal.GetDelegateForFunctionPointer<OrtGetApiBaseDelegate>(getBaseSym);
            IntPtr apiBasePtr = getBase();
            if (apiBasePtr == IntPtr.Zero) {
                lastInitError = "OrtGetApiBase returned null";
                CleanupPartialInitLocked();
                return false;
            }

            IntPtr getApiFnPtr = Marshal.ReadIntPtr(apiBasePtr, 0);
            IntPtr getVerFnPtr = Marshal.ReadIntPtr(apiBasePtr, IntPtr.Size);
            GetApiDelegate getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiFnPtr);
            GetVersionStringDelegate getVer = Marshal.GetDelegateForFunctionPointer<GetVersionStringDelegate>(getVerFnPtr);

            IntPtr verStrPtr = getVer();
            loadedVersion = verStrPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(verStrPtr) ?? "") : "";

            uint resolvedApiVersion = headerApiVersion;
            ortApiPtr = getApi(headerApiVersion);
            uint fallbackStart = headerApiVersion > 14u ? headerApiVersion - 1u : 31u;
            for (uint v = fallbackStart; ortApiPtr == IntPtr.Zero && v >= 14u; v--) {
                ortApiPtr = getApi(v);
                if (ortApiPtr != IntPtr.Zero) {
                    resolvedApiVersion = v;
                }
            }
            if (ortApiPtr == IntPtr.Zero) {
                lastInitError = $"OrtGetApiBase()->GetApi({headerApiVersion}) returned null";
                AppLogger.Warn(lastInitError);
                CleanupPartialInitLocked();
                return false;
            }
            if (resolvedApiVersion < headerApiVersion) {
                AppLogger.Warn($"ONNX Runtime native GetApi fell back from header apiVer={headerApiVersion} to {resolvedApiVersion} (lib v{loadedVersion}).");
            }

            BindApiDelegates();

            CheckStatus(fnCreateEnv(OrtLoggingLevelWarning, "CavernizeNeural", out ortEnv));
            CheckStatus(fnCreateCpuMemInfo(OrtArenaAllocator, OrtMemTypeDefault, out cpuMemoryInfo));
            CheckStatus(fnGetAllocator(out defaultAllocator));

            // Probe all real EP capabilities dynamically so GetRuntimeInfo() & Settings UI never misreport the backend
            ProbeAvailableExecutionProvidersLocked();

            initialized = true;
            lastInitError = "";
            AppLogger.Info($"Initialized ONNX Runtime native bridge v{loadedVersion} ({apiSlots.Count} OrtApi symbols, apiVer={resolvedApiVersion}, WebGPU={supportsWebGpu}, XNNPACK={supportsXnnpack}, QNN={supportsQnnHtp}, EP={lastActiveProvider})");
            return true;
        } catch (Exception ex) {
            lastInitError = ex.Message;
            AppLogger.Warn($"Failed to initialize ONNX Runtime native bridge: {ex.Message}");
            CleanupPartialInitLocked();
            return false;
        }
    }

    void BindApiDelegates() {
        fnCreateEnv = GetSlotDelegate<CreateEnvDelegate>("CreateEnv");
        fnCreateSessionOptions = GetSlotDelegate<CreateSessionOptionsDelegate>("CreateSessionOptions");
        fnSetIntraOpNumThreads = GetSlotDelegate<SetIntraOpNumThreadsDelegate>("SetIntraOpNumThreads");
        fnSetInterOpNumThreads = TryGetSlotDelegate<SetInterOpNumThreadsDelegate>("SetInterOpNumThreads");
        fnSetGraphOpt = GetSlotDelegate<SetGraphOptimizationLevelDelegate>("SetSessionGraphOptimizationLevel");
        fnSetOptimizedModelPath = TryGetSlotDelegate<SetOptimizedModelFilePathDelegate>("SetOptimizedModelFilePath");
        fnEnableCpuMemArena = TryGetSlotDelegate<EnableSessionOptionFlagDelegate>("EnableCpuMemArena");
        fnEnableMemPattern = TryGetSlotDelegate<EnableSessionOptionFlagDelegate>("EnableMemPattern");
        fnAddSessionConfig = TryGetSlotDelegate<AddSessionConfigEntryDelegate>("AddSessionConfigEntry");
        fnAppendEp = TryGetSlotDelegate<SessionOptionsAppendEpDelegate>("SessionOptionsAppendExecutionProvider");
        fnCreateSession = GetSlotDelegate<CreateSessionDelegate>("CreateSession");
        fnGetInputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetInputCount");
        fnGetOutputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetOutputCount");
        fnGetInputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetInputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetInputNameAllocated");
        fnGetOutputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputNameAllocated");
        fnGetInputTypeInfo = TryGetSlotDelegate<SessionGetTypeInfoDelegate>("SessionGetInputTypeInfo");
        fnCastTypeInfoToTensorInfo = TryGetSlotDelegate<CastTypeInfoToTensorInfoDelegate>("CastTypeInfoToTensorInfo");
        fnGetAllocator = GetSlotDelegate<GetAllocatorWithDefaultOptionsDelegate>("GetAllocatorWithDefaultOptions");
        fnAllocatorFree = GetSlotDelegate<AllocatorFreeDelegate>("AllocatorFree");
        fnCreateCpuMemInfo = GetSlotDelegate<CreateCpuMemoryInfoDelegate>("CreateCpuMemoryInfo");
        fnCreateTensor = GetSlotDelegate<CreateTensorWithDataAsOrtValueDelegate>("CreateTensorWithDataAsOrtValue");
        fnRun = GetSlotDelegate<RunDelegate>("Run");
        fnGetTensorData = GetSlotDelegate<GetTensorMutableDataDelegate>("GetTensorMutableData");
        fnGetTensorTypeAndShape = GetSlotDelegate<GetTensorTypeAndShapeDelegate>("GetTensorTypeAndShape");
        fnGetTensorElementType = TryGetSlotDelegate<GetTensorElementTypeDelegate>("GetTensorElementType");
        fnGetDimsCount = GetSlotDelegate<GetDimensionsCountDelegate>("GetDimensionsCount");
        fnGetDims = GetSlotDelegate<GetDimensionsDelegate>("GetDimensions");
        fnGetErrorMessage = GetSlotDelegate<GetErrorMessageDelegate>("GetErrorMessage");
        fnReleaseStatus = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseStatus");
        fnReleaseEnv = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseEnv");
        fnReleaseSession = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseSession");
        fnReleaseSessionOptions = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseSessionOptions");
        fnReleaseValue = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseValue");
        fnReleaseMemoryInfo = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseMemoryInfo");
        fnReleaseTensorTypeAndShapeInfo = GetSlotDelegate<ReleaseHandleDelegate>("ReleaseTensorTypeAndShapeInfo");
        fnReleaseTypeInfo = TryGetSlotDelegate<ReleaseHandleDelegate>("ReleaseTypeInfo");
    }

    T GetSlotDelegate<T>(string name) where T : Delegate {
        if (!apiSlots.TryGetValue(name, out int slot)) {
            throw new EntryPointNotFoundException($"OrtApi slot '{name}' was not found in onnxruntime_c_api.h.");
        }
        IntPtr fnPtr = Marshal.ReadIntPtr(ortApiPtr, slot * IntPtr.Size);
        if (fnPtr == IntPtr.Zero) {
            throw new EntryPointNotFoundException($"OrtApi function pointer for '{name}' (slot {slot}) is null.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    T TryGetSlotDelegate<T>(string name) where T : Delegate {
        if (!apiSlots.TryGetValue(name, out int slot)) {
            return null;
        }
        IntPtr fnPtr = Marshal.ReadIntPtr(ortApiPtr, slot * IntPtr.Size);
        return fnPtr != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(fnPtr) : null;
    }

    void CheckStatus(IntPtr status) {
        if (status == IntPtr.Zero) {
            return;
        }
        string message = ReadAndReleaseStatus(status);
        throw new InvalidOperationException($"ONNX Runtime error: {message}");
    }

    string ReadAndReleaseStatus(IntPtr status) {
        if (status == IntPtr.Zero) {
            return "";
        }
        try {
            IntPtr msgPtr = fnGetErrorMessage != null ? fnGetErrorMessage(status) : IntPtr.Zero;
            return msgPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(msgPtr) ?? "Unknown ORT error") : "Unknown ORT error";
        } finally {
            fnReleaseStatus?.Invoke(status);
        }
    }

    void ReleaseStatusQuietly(IntPtr status) {
        if (status != IntPtr.Zero) {
            fnReleaseStatus?.Invoke(status);
        }
    }

    void CleanupPartialInitLocked() {
        if (cpuMemoryInfo != IntPtr.Zero) {
            fnReleaseMemoryInfo?.Invoke(cpuMemoryInfo);
            cpuMemoryInfo = IntPtr.Zero;
        }
        if (ortEnv != IntPtr.Zero) {
            fnReleaseEnv?.Invoke(ortEnv);
            ortEnv = IntPtr.Zero;
        }
        defaultAllocator = IntPtr.Zero;

        // Clear all native function-pointer delegates before unloading libonnxruntime.so so no dangling pointers remain
        fnCreateEnv = null;
        fnCreateSessionOptions = null;
        fnSetIntraOpNumThreads = null;
        fnSetInterOpNumThreads = null;
        fnSetGraphOpt = null;
        fnSetOptimizedModelPath = null;
        fnEnableCpuMemArena = null;
        fnEnableMemPattern = null;
        fnAddSessionConfig = null;
        fnAppendEp = null;
        fnCreateSession = null;
        fnGetInputCount = null;
        fnGetOutputCount = null;
        fnGetInputName = null;
        fnGetOutputName = null;
        fnGetInputTypeInfo = null;
        fnCastTypeInfoToTensorInfo = null;
        fnGetAllocator = null;
        fnAllocatorFree = null;
        fnCreateCpuMemInfo = null;
        fnCreateTensor = null;
        fnRun = null;
        fnGetTensorData = null;
        fnGetTensorTypeAndShape = null;
        fnGetTensorElementType = null;
        fnGetDimsCount = null;
        fnGetDims = null;
        fnGetErrorMessage = null;
        fnReleaseStatus = null;
        fnReleaseEnv = null;
        fnReleaseSession = null;
        fnReleaseSessionOptions = null;
        fnReleaseValue = null;
        fnReleaseMemoryInfo = null;
        fnReleaseTensorTypeAndShapeInfo = null;
        fnReleaseTypeInfo = null;

        if (libHandle != IntPtr.Zero) {
            try { NativeLibrary.Free(libHandle); } catch { }
            libHandle = IntPtr.Zero;
        }
        for (int i = companionLibHandles.Count - 1; i >= 0; i--) {
            if (companionLibHandles[i] != IntPtr.Zero) {
                try { NativeLibrary.Free(companionLibHandles[i]); } catch { }
            }
        }
        companionLibHandles.Clear();
        apiSlots.Clear();
        ortApiPtr = IntPtr.Zero;
        loadedVersion = "";
        lastActiveProvider = "";
        initialized = false;
    }

    static long ComputeCheckedElementCount(long[] dims, bool allowZero) {
        if (dims == null || dims.Length == 0) {
            return 1L;
        }
        long total = 1L;
        for (int d = 0; d < dims.Length; d++) {
            long dim = dims[d];
            if (dim < 0) {
                throw new InvalidDataException($"ONNX tensor has negative dimension {dim} at axis {d}.");
            }
            if (dim == 0) {
                if (allowZero) {
                    return 0L;
                }
                throw new InvalidDataException($"ONNX input tensor has zero dimension at axis {d}.");
            }
            if (total > MaxTensorElements / dim) {
                throw new InvalidDataException($"ONNX tensor element count exceeds safe mobile limit ({MaxTensorElements} elements).");
            }
            total *= dim;
        }
        return total;
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (initLock) {
            disposeRequested = true;
            if (activeSessionCount == 0) {
                CleanupPartialInitLocked();
            }
        }
    }

}
