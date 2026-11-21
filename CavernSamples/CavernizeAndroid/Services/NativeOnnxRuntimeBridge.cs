using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Android.Content;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Parses the extracted <c>onnxruntime_c_api.h</c> header to build an exact 0-based vtable slot map for
/// <c>struct OrtApi</c> across any ONNX Runtime version (1.17–1.31+) without hardcoded fragile offsets.
/// </summary>
public static class OnnxRuntimeHeaderParser {
    static readonly Regex ApiVersionRegex = new(@"#define\s+ORT_API_VERSION\s+(\d+)", RegexOptions.Compiled);
    static readonly Regex MemberRegex = new(
        @"\b(ORT_API2_STATUS|ORT_API_STATUS|ORT_API|ORT_CLASS_RELEASE)\s*\(([^)]+)\)",
        RegexOptions.Compiled);
    static readonly Regex RawFnPtrRegex = new(
        @"\(\s*(?:[A-Za-z0-9_]+\s*)?\*\s*([A-Za-z0-9_]+)\s*\)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts the advertised <c>ORT_API_VERSION</c> and a map from C API function name (e.g. <c>CreateSession</c>,
    /// <c>ReleaseSession</c>) to its 0-based pointer slot index inside <c>struct OrtApi</c>.
    /// </summary>
    public static (uint ApiVersion, Dictionary<string, int> SlotMap) ParseOrtApiHeader(string headerText) {
        Dictionary<string, int> slots = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(headerText)) {
            return (17u, slots);
        }

        uint apiVersion = 17u;
        Match verMatch = ApiVersionRegex.Match(headerText);
        if (verMatch.Success && uint.TryParse(verMatch.Groups[1].Value, out uint parsed)) {
            apiVersion = parsed;
        }

        int structIdx = headerText.IndexOf("struct OrtApi {", StringComparison.Ordinal);
        if (structIdx < 0) {
            structIdx = headerText.IndexOf("struct OrtApi{", StringComparison.Ordinal);
        }
        if (structIdx < 0) {
            return (apiVersion, slots);
        }

        int bodyStart = headerText.IndexOf('{', structIdx);
        int bodyEnd = headerText.IndexOf("};", bodyStart, StringComparison.Ordinal);
        if (bodyStart < 0 || bodyEnd <= bodyStart) {
            return (apiVersion, slots);
        }

        string body = StripComments(headerText.Substring(bodyStart + 1, bodyEnd - bodyStart - 1));
        int slotIndex = 0;
        foreach (string rawLine in body.Split(';')) {
            string stmt = rawLine.Trim();
            if (string.IsNullOrEmpty(stmt) || stmt.StartsWith('#')) {
                continue;
            }
            Match m = MemberRegex.Match(stmt);
            if (m.Success) {
                string macro = m.Groups[1].Value;
                string args = m.Groups[2].Value;
                if (macro == "ORT_CLASS_RELEASE") {
                    string cls = args.Trim();
                    if (!string.IsNullOrEmpty(cls)) {
                        slots["Release" + cls] = slotIndex++;
                    }
                } else if (macro == "ORT_API2_STATUS" || macro == "ORT_API_STATUS") {
                    // ORT_API2_STATUS(Name, ...) -> Name is argument 0
                    string[] parts = args.Split(',');
                    string fnName = parts.Length >= 1 ? parts[0].Trim() : "";
                    if (!string.IsNullOrEmpty(fnName)) {
                        slots[fnName] = slotIndex++;
                    }
                } else {
                    // ORT_API(ReturnType, Name, ...) -> Name is argument 1
                    string[] parts = args.Split(',');
                    string fnName = parts.Length >= 2 ? parts[1].Trim() : "";
                    if (!string.IsNullOrEmpty(fnName)) {
                        slots[fnName] = slotIndex++;
                    }
                }
            } else {
                Match rawMatch = RawFnPtrRegex.Match(stmt);
                if (rawMatch.Success) {
                    string fnName = rawMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(fnName)) {
                        slots[fnName] = slotIndex++;
                    }
                }
            }
        }

        return (apiVersion, slots);
    }

    static string StripComments(string text) {
        string noBlock = Regex.Replace(text, @"/\*[\s\S]*?\*/", " ");
        return Regex.Replace(noBlock, @"//[^\r\n]*", " ");
    }
}

/// <summary>
/// Dynamic Android native bridge for <c>libonnxruntime.so</c> stored in <c>FilesDir/bin/onnxruntime/</c>.
/// Implements <see cref="INeuralInferenceRuntime"/> using <see cref="NativeLibrary.Load"/> + <c>OrtGetApiBase()</c>
/// + header-verified <c>OrtApi</c> vtable slots, with automatic QNN HTP -> XNNPACK -> CPU provider cascade.
/// </summary>
public sealed class NativeOnnxRuntimeBridge : INeuralInferenceRuntime {
    // Unmanaged C ABI delegate signatures for ONNX Runtime C API
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
    delegate IntPtr SetGraphOptimizationLevelDelegate(IntPtr options, int graphOptimizationLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr AddSessionConfigEntryDelegate(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

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

    readonly Context appContext;
    readonly object initLock = new();

    IntPtr libHandle;
    IntPtr ortApiPtr;
    IntPtr ortEnv;
    IntPtr cpuMemoryInfo;
    IntPtr defaultAllocator;
    Dictionary<string, int> apiSlots = new(StringComparer.Ordinal);
    string loadedVersion = "";
    bool initialized;

    // Cached C API delegates
    CreateEnvDelegate fnCreateEnv;
    CreateSessionOptionsDelegate fnCreateSessionOptions;
    SetIntraOpNumThreadsDelegate fnSetIntraOpNumThreads;
    SetGraphOptimizationLevelDelegate fnSetGraphOpt;
    AddSessionConfigEntryDelegate fnAddSessionConfig;
    SessionOptionsAppendEpDelegate fnAppendEp;
    CreateSessionDelegate fnCreateSession;
    SessionGetCountDelegate fnGetInputCount;
    SessionGetCountDelegate fnGetOutputCount;
    SessionGetNameDelegate fnGetInputName;
    SessionGetNameDelegate fnGetOutputName;
    GetAllocatorWithDefaultOptionsDelegate fnGetAllocator;
    AllocatorFreeDelegate fnAllocatorFree;
    CreateCpuMemoryInfoDelegate fnCreateCpuMemInfo;
    CreateTensorWithDataAsOrtValueDelegate fnCreateTensor;
    RunDelegate fnRun;
    GetTensorMutableDataDelegate fnGetTensorData;
    GetTensorTypeAndShapeDelegate fnGetTensorTypeAndShape;
    GetDimensionsCountDelegate fnGetDimsCount;
    GetDimensionsDelegate fnGetDims;
    GetErrorMessageDelegate fnGetErrorMessage;
    ReleaseHandleDelegate fnReleaseStatus;
    ReleaseHandleDelegate fnReleaseEnv;
    ReleaseHandleDelegate fnReleaseSession;
    ReleaseHandleDelegate fnReleaseSessionOptions;
    ReleaseHandleDelegate fnReleaseValue;
    ReleaseHandleDelegate fnReleaseMemoryInfo;
    ReleaseHandleDelegate fnReleaseTensorTypeAndShapeInfo;

    public NativeOnnxRuntimeBridge(Context context) {
        appContext = context?.ApplicationContext ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public bool IsAvailable {
        get {
            lock (initLock) {
                return initialized || OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext);
            }
        }
    }

    /// <inheritdoc/>
    public NeuralRuntimeInfo GetRuntimeInfo() {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(appContext);
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                   OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        if (installed && !initialized) {
            TryInitializeLocked();
        }
        bool ready = installed && initialized;
        return new NeuralRuntimeInfo {
            IsAvailable = ready,
            LibraryPath = installed ? OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext) : string.Empty,
            VersionString = !string.IsNullOrEmpty(loadedVersion) ? loadedVersion : (installed ? "installed" : string.Empty),
            ActiveProvider = !ready ? "Not Installed" : (qnn ? "QNN HTP NPU" : "XNNPACK ARM64"),
            IsQnnAvailable = qnn,
            DiagnosticMessage = ready ? string.Empty : (installed ? "Native ONNX Runtime failed to initialize." : "ONNX Runtime is not installed.")
        };
    }

    /// <inheritdoc/>
    public string ResolveInstalledModelPath(string modelId) {
        string path = OnnxRuntimeInstallerService.ResolveModelPath(appContext, modelId);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// <inheritdoc/>
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 4) {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            throw new FileNotFoundException("ONNX model file not found.", modelPath);
        }

        lock (initLock) {
            if (!initialized && !TryInitializeLocked()) {
                throw new InvalidOperationException("ONNX Runtime native library could not be initialized. Reinstall ONNX Runtime in Settings.");
            }
        }

        int threads = intraOpThreads > 0 ? intraOpThreads : Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        CheckStatus(fnCreateSessionOptions(out IntPtr sessOpts));
        try {
            CheckStatus(fnSetIntraOpNumThreads(sessOpts, threads));
            CheckStatus(fnSetGraphOpt(sessOpts, OrtEnableAll));
            if (fnAddSessionConfig != null) {
                ReleaseStatusQuietly(fnAddSessionConfig(sessOpts, "session.intra_op.allow_spinning", "0"));
            }

            string activeEp = ConfigureExecutionProvider(sessOpts, preferredProvider, threads);
            CheckStatus(fnCreateSession(ortEnv, modelPath, sessOpts, out IntPtr sessionHandle));
            return new NativeOnnxSession(this, modelPath, sessionHandle, activeEp);
        } finally {
            fnReleaseSessionOptions?.Invoke(sessOpts);
        }
    }

    string ConfigureExecutionProvider(IntPtr sessOpts, NeuralExecutionProvider preferred, int threads) {
        if (fnAppendEp == null) {
            return "CPU";
        }

        bool tryQnn = (preferred == NeuralExecutionProvider.QnnHtp || preferred == NeuralExecutionProvider.Auto) &&
                      OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(appContext) &&
                      OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        if (tryQnn) {
            string qnnPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(appContext);
            if (TryAppendEp(sessOpts, "QNN",
                ["backend_path", "htp_performance_mode", "enable_htp_fp16_precision"],
                [qnnPath, "burst", "1"])) {
                return "QNN HTP (NPU)";
            }
        }

        bool tryXnn = preferred == NeuralExecutionProvider.Xnnpack ||
                      preferred == NeuralExecutionProvider.Auto ||
                      preferred == NeuralExecutionProvider.QnnHtp;
        if (tryXnn) {
            if (TryAppendEp(sessOpts, "XNNPACK", ["intra_op_num_threads"], [threads.ToString()])) {
                return "XNNPACK (ARM64 SIMD)";
            }
        }

        return "CPU";
    }

    bool TryAppendEp(IntPtr sessOpts, string epName, string[] keys, string[] values) {
        IntPtr[] keyPtrs = new IntPtr[keys.Length];
        IntPtr[] valPtrs = new IntPtr[values.Length];
        try {
            for (int i = 0; i < keys.Length; i++) {
                keyPtrs[i] = Marshal.StringToCoTaskMemUTF8(keys[i]);
                valPtrs[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            }
            IntPtr st = fnAppendEp(sessOpts, epName, keyPtrs, valPtrs, (UIntPtr)keys.Length);
            if (st == IntPtr.Zero) {
                return true;
            }
            string msg = ReadAndReleaseStatus(st);
            AppLogger.Warn($"ONNX Runtime EP '{epName}' unavailable, falling back: {msg}");
            return false;
        } catch (Exception ex) {
            AppLogger.Warn($"ONNX Runtime EP '{epName}' threw: {ex.Message}");
            return false;
        } finally {
            for (int i = 0; i < keyPtrs.Length; i++) {
                if (keyPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(keyPtrs[i]);
            }
            for (int i = 0; i < valPtrs.Length; i++) {
                if (valPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(valPtrs[i]);
            }
        }
    }

    bool TryInitializeLocked() {
        if (initialized) {
            return true;
        }
        try {
            string libPath = OnnxRuntimeInstallerService.GetManagedLibraryPath(appContext);
            string headerPath = OnnxRuntimeInstallerService.GetManagedHeaderPath(appContext);
            if (!File.Exists(libPath)) {
                return false;
            }

            uint headerApiVersion = 17u;
            if (File.Exists(headerPath)) {
                (headerApiVersion, apiSlots) = OnnxRuntimeHeaderParser.ParseOrtApiHeader(File.ReadAllText(headerPath));
            }
            if (apiSlots.Count < 20) {
                AppLogger.Warn("ONNX Runtime C API header missing or incomplete; cannot safely bind vtable.");
                return false;
            }

            libHandle = NativeLibrary.Load(libPath);
            IntPtr getBaseSym = NativeLibrary.GetExport(libHandle, "OrtGetApiBase");
            OrtGetApiBaseDelegate getBase = Marshal.GetDelegateForFunctionPointer<OrtGetApiBaseDelegate>(getBaseSym);
            IntPtr apiBasePtr = getBase();
            if (apiBasePtr == IntPtr.Zero) {
                return false;
            }

            // struct OrtApiBase { const OrtApi* (*GetApi)(uint32_t); const char* (*GetVersionString)(void); };
            IntPtr getApiFnPtr = Marshal.ReadIntPtr(apiBasePtr, 0);
            IntPtr getVerFnPtr = Marshal.ReadIntPtr(apiBasePtr, IntPtr.Size);
            GetApiDelegate getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiFnPtr);
            GetVersionStringDelegate getVer = Marshal.GetDelegateForFunctionPointer<GetVersionStringDelegate>(getVerFnPtr);

            IntPtr verStrPtr = getVer();
            loadedVersion = verStrPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(verStrPtr) ?? "") : "";

            // Try header's ORT_API_VERSION first, then step down gracefully if needed
            ortApiPtr = getApi(headerApiVersion);
            for (uint v = Math.Min(headerApiVersion, 24u); ortApiPtr == IntPtr.Zero && v >= 14u; v--) {
                ortApiPtr = getApi(v);
            }
            if (ortApiPtr == IntPtr.Zero) {
                AppLogger.Warn("OrtGetApiBase()->GetApi returned null.");
                return false;
            }

            BindApiDelegates();

            CheckStatus(fnCreateEnv(OrtLoggingLevelWarning, "CavernizeNeural", out ortEnv));
            CheckStatus(fnCreateCpuMemInfo(OrtArenaAllocator, OrtMemTypeDefault, out cpuMemoryInfo));
            CheckStatus(fnGetAllocator(out defaultAllocator));

            initialized = true;
            AppLogger.Info($"Initialized ONNX Runtime native bridge v{loadedVersion} ({apiSlots.Count} OrtApi symbols)");
            return true;
        } catch (Exception ex) {
            AppLogger.Warn($"Failed to initialize ONNX Runtime native bridge: {ex.Message}");
            CleanupPartialInitLocked();
            return false;
        }
    }

    void BindApiDelegates() {
        fnCreateEnv = GetSlotDelegate<CreateEnvDelegate>("CreateEnv");
        fnCreateSessionOptions = GetSlotDelegate<CreateSessionOptionsDelegate>("CreateSessionOptions");
        fnSetIntraOpNumThreads = GetSlotDelegate<SetIntraOpNumThreadsDelegate>("SetIntraOpNumThreads");
        fnSetGraphOpt = GetSlotDelegate<SetGraphOptimizationLevelDelegate>("SetSessionGraphOptimizationLevel");
        fnAddSessionConfig = TryGetSlotDelegate<AddSessionConfigEntryDelegate>("AddSessionConfigEntry");
        fnAppendEp = TryGetSlotDelegate<SessionOptionsAppendEpDelegate>("SessionOptionsAppendExecutionProvider");
        fnCreateSession = GetSlotDelegate<CreateSessionDelegate>("CreateSession");
        fnGetInputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetInputCount");
        fnGetOutputCount = GetSlotDelegate<SessionGetCountDelegate>("SessionGetOutputCount");
        fnGetInputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetInputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetInputNameAllocated");
        fnGetOutputName = TryGetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputName")
            ?? GetSlotDelegate<SessionGetNameDelegate>("SessionGetOutputNameAllocated");
        fnGetAllocator = GetSlotDelegate<GetAllocatorWithDefaultOptionsDelegate>("GetAllocatorWithDefaultOptions");
        fnAllocatorFree = GetSlotDelegate<AllocatorFreeDelegate>("AllocatorFree");
        fnCreateCpuMemInfo = GetSlotDelegate<CreateCpuMemoryInfoDelegate>("CreateCpuMemoryInfo");
        fnCreateTensor = GetSlotDelegate<CreateTensorWithDataAsOrtValueDelegate>("CreateTensorWithDataAsOrtValue");
        fnRun = GetSlotDelegate<RunDelegate>("Run");
        fnGetTensorData = GetSlotDelegate<GetTensorMutableDataDelegate>("GetTensorMutableData");
        fnGetTensorTypeAndShape = GetSlotDelegate<GetTensorTypeAndShapeDelegate>("GetTensorTypeAndShape");
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
        if (libHandle != IntPtr.Zero) {
            try { NativeLibrary.Free(libHandle); } catch { }
            libHandle = IntPtr.Zero;
        }
        ortApiPtr = IntPtr.Zero;
        initialized = false;
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (initLock) {
            CleanupPartialInitLocked();
        }
    }

    sealed class NativeOnnxSession : INeuralSession {
        readonly NativeOnnxRuntimeBridge bridge;
        IntPtr sessionHandle;
        readonly string[] inputNames;
        readonly string[] outputNames;
        bool disposed;

        public NativeOnnxSession(NativeOnnxRuntimeBridge bridge, string modelPath, IntPtr handle, string activeEp) {
            this.bridge = bridge;
            ModelPath = modelPath ?? string.Empty;
            sessionHandle = handle;
            ExecutionProvider = activeEp;
            inputNames = ReadNames(isInput: true);
            outputNames = ReadNames(isInput: false);
        }

        public string ModelPath { get; }
        public string ExecutionProvider { get; }
        public IReadOnlyList<string> InputNames => inputNames;
        public IReadOnlyList<string> OutputNames => outputNames;

        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            ArgumentNullException.ThrowIfNull(inputTensor);
            if (inputNames.Length == 0) {
                throw new InvalidOperationException("ONNX model exposes no input tensors.");
            }
            Dictionary<string, NeuralTensorFloat> inputs = new(1, StringComparer.Ordinal) {
                [inputNames[0]] = inputTensor
            };
            IReadOnlyDictionary<string, NeuralTensorFloat> outputs = Run(inputs);
            NeuralTensorFloat[] ordered = new NeuralTensorFloat[outputNames.Length];
            for (int o = 0; o < outputNames.Length; o++) {
                outputs.TryGetValue(outputNames[o], out ordered[o]);
            }
            return ordered;
        }

        public IReadOnlyDictionary<string, NeuralTensorFloat> Run(IReadOnlyDictionary<string, NeuralTensorFloat> inputs) {
            if (disposed) {
                throw new ObjectDisposedException(nameof(NativeOnnxSession));
            }
            ArgumentNullException.ThrowIfNull(inputs);

            int inCount = inputs.Count;
            IntPtr[] inNamePtrs = new IntPtr[inCount];
            IntPtr[] inValPtrs = new IntPtr[inCount];
            GCHandle[] pinnedInputs = new GCHandle[inCount];

            int outCount = outputNames.Length;
            IntPtr[] outNamePtrs = new IntPtr[outCount];
            IntPtr[] outValPtrs = new IntPtr[outCount];

            try {
                int idx = 0;
                foreach (KeyValuePair<string, NeuralTensorFloat> kv in inputs) {
                    inNamePtrs[idx] = Marshal.StringToCoTaskMemUTF8(kv.Key);
                    NeuralTensorFloat tensor = kv.Value;
                    pinnedInputs[idx] = GCHandle.Alloc(tensor.Data, GCHandleType.Pinned);
                    IntPtr dataPtr = pinnedInputs[idx].AddrOfPinnedObject();
                    UIntPtr byteLen = (UIntPtr)(tensor.Data.Length * sizeof(float));
                    bridge.CheckStatus(bridge.fnCreateTensor(
                        bridge.cpuMemoryInfo,
                        dataPtr,
                        byteLen,
                        tensor.Shape,
                        (UIntPtr)tensor.Shape.Length,
                        OnnxTensorElementDataTypeFloat,
                        out inValPtrs[idx]));
                    idx++;
                }

                for (int o = 0; o < outCount; o++) {
                    outNamePtrs[o] = Marshal.StringToCoTaskMemUTF8(outputNames[o]);
                }

                bridge.CheckStatus(bridge.fnRun(
                    sessionHandle,
                    IntPtr.Zero,
                    inNamePtrs,
                    inValPtrs,
                    (UIntPtr)inCount,
                    outNamePtrs,
                    (UIntPtr)outCount,
                    outValPtrs));

                Dictionary<string, NeuralTensorFloat> results = new(outCount, StringComparer.Ordinal);
                for (int o = 0; o < outCount; o++) {
                    IntPtr outVal = outValPtrs[o];
                    if (outVal == IntPtr.Zero) {
                        continue;
                    }
                    bridge.CheckStatus(bridge.fnGetTensorTypeAndShape(outVal, out IntPtr shapeInfo));
                    long[] dims;
                    try {
                        bridge.CheckStatus(bridge.fnGetDimsCount(shapeInfo, out UIntPtr dimCount));
                        dims = new long[(int)dimCount];
                        if (dims.Length > 0) {
                            bridge.CheckStatus(bridge.fnGetDims(shapeInfo, dims, dimCount));
                        }
                    } finally {
                        bridge.fnReleaseTensorTypeAndShapeInfo?.Invoke(shapeInfo);
                    }

                    long elemCount = 1;
                    for (int d = 0; d < dims.Length; d++) {
                        elemCount *= Math.Max(1L, dims[d]);
                    }
                    float[] managed = new float[elemCount];
                    if (elemCount > 0) {
                        bridge.CheckStatus(bridge.fnGetTensorData(outVal, out IntPtr rawPtr));
                        Marshal.Copy(rawPtr, managed, 0, (int)elemCount);
                    }
                    results[outputNames[o]] = new NeuralTensorFloat(dims, managed);
                }

                return results;
            } finally {
                for (int i = 0; i < inCount; i++) {
                    if (inValPtrs[i] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(inValPtrs[i]);
                    if (pinnedInputs[i].IsAllocated) pinnedInputs[i].Free();
                    if (inNamePtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(inNamePtrs[i]);
                }
                for (int o = 0; o < outCount; o++) {
                    if (outValPtrs[o] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(outValPtrs[o]);
                    if (outNamePtrs[o] != IntPtr.Zero) Marshal.FreeCoTaskMem(outNamePtrs[o]);
                }
            }
        }

        string[] ReadNames(bool isInput) {
            SessionGetCountDelegate countFn = isInput ? bridge.fnGetInputCount : bridge.fnGetOutputCount;
            SessionGetNameDelegate nameFn = isInput ? bridge.fnGetInputName : bridge.fnGetOutputName;
            bridge.CheckStatus(countFn(sessionHandle, out UIntPtr count));
            int n = (int)count;
            string[] names = new string[n];
            for (int i = 0; i < n; i++) {
                bridge.CheckStatus(nameFn(sessionHandle, (UIntPtr)i, bridge.defaultAllocator, out IntPtr namePtr));
                try {
                    names[i] = namePtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(namePtr) ?? $"tensor_{i}") : $"tensor_{i}";
                } finally {
                    if (namePtr != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(bridge.fnAllocatorFree(bridge.defaultAllocator, namePtr));
                    }
                }
            }
            return names;
        }

        public void Dispose() {
            if (!disposed) {
                disposed = true;
                if (sessionHandle != IntPtr.Zero) {
                    bridge.fnReleaseSession?.Invoke(sessionHandle);
                    sessionHandle = IntPtr.Zero;
                }
            }
        }
    }
}
