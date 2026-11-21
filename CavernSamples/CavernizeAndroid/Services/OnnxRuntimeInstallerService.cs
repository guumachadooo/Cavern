using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using Android.Content;
using Android.OS;
using Android.Systems;

using Cavernize.Logic.Spatial;

using CavernizeAndroid.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Status snapshot returned by <see cref="OnnxRuntimeInstallerService.CheckStatusAsync"/>.
/// </summary>
public sealed class OnnxRuntimeStatusResult {
    public bool IsInstalled { get; init; }
    public string LocalLibPath { get; init; } = "";
    public string HeaderPath { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
    public bool IsQnnPackageInstalled { get; init; }
    public bool IsQualcommDevice { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string ArtifactId { get; init; } = "onnxruntime-android";
}

/// <summary>
/// Manages on-demand download, extraction, update checking, custom <c>.onnx</c> registration, and storage hygiene
/// for the native ARM64/x64 ONNX Runtime shared libraries (<c>FilesDir/bin/onnxruntime/libonnxruntime.so</c> +
/// optional Snapdragon QNN HTP), C API header (<c>onnxruntime_c_api.h</c>), lazy ONNX audio models
/// (<c>FilesDir/neural_models/</c>), and temporary inference cache (<c>CacheDir/neural_work/</c>).
/// </summary>
public static class OnnxRuntimeInstallerService {
    const string MavenBaseUrl = "https://repo1.maven.org/maven2/com/microsoft/onnxruntime";
    const string StandardArtifactId = "onnxruntime-android";
    const string QnnArtifactId = "onnxruntime-android-qnn";
    const string CustomMetaSuffix = ".meta.json";

    sealed class CustomOnnxModelMetadata {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Task { get; set; } = (int)NeuralModelTask.StemSeparation4;
        public int ExpectedSampleRate { get; set; } = 44100;
        public int DefaultChunkSamples { get; set; } = 262144;
        public string[] OutputStemNames { get; set; } = Array.Empty<string>();
    }

    static readonly string appUserAgentVersion = ResolveAppUserAgentVersion();
    static readonly HttpClient http = CreateHttpClient();

    static string ResolveAppUserAgentVersion() {
        try {
            Assembly asm = typeof(OnnxRuntimeInstallerService).Assembly;
            string infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer)) {
                int plus = infoVer.IndexOf('+');
                string clean = (plus > 0 ? infoVer[..plus] : infoVer).Trim();
                if (!string.IsNullOrEmpty(clean)) {
                    return clean;
                }
            }
            Version v = asm.GetName().Version;
            if (v != null && (v.Major > 0 || v.Minor > 0)) {
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        } catch { }
        return "2.8";
    }

    static HttpClient CreateHttpClient(TimeSpan? timeout = null) {
        HttpClient client = new() { Timeout = timeout ?? TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", appUserAgentVersion));
        return client;
    }

    public static string GetRuntimeDir(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "bin", "onnxruntime");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetModelsDir(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "neural_models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetTempWorkDir(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "neural_work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetManagedLibraryPath(Context context) =>
        Path.Combine(GetRuntimeDir(context), "libonnxruntime.so");

    public static string GetManagedHeaderPath(Context context) =>
        Path.Combine(GetRuntimeDir(context), "onnxruntime_c_api.h");

    public static string GetManagedQnnHtpPath(Context context) =>
        Path.Combine(GetRuntimeDir(context), "libQnnHtp.so");

    public static bool IsRuntimeInstalled(Context context) {
        string lib = GetManagedLibraryPath(context);
        return File.Exists(lib) && new FileInfo(lib).Length > 100_000;
    }

    public static bool IsQnnAvailableOnDisk(Context context) {
        string qnn = GetManagedQnnHtpPath(context);
        return File.Exists(qnn) && new FileInfo(qnn).Length > 100_000;
    }

    /// <summary>
    /// Detects Qualcomm Snapdragon SoCs where the QNN HTP NPU execution provider can accelerate inference.
    /// </summary>
    public static bool IsQualcommSnapdragonDevice() {
        try {
            string hw = (Build.Hardware ?? "").ToLowerInvariant();
            string board = (Build.Board ?? "").ToLowerInvariant();
            string soc = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? (Build.SocManufacturer ?? "").ToLowerInvariant() + " " + (Build.SocModel ?? "").ToLowerInvariant()
                : "";
            return hw.Contains("qcom") || hw.Contains("snapdragon") ||
                   board.Contains("kalama") || board.Contains("pineapple") || board.Contains("taro") ||
                   board.Contains("lahaina") || board.Contains("sun") ||
                   soc.Contains("qualcomm") || soc.Contains("qti") || soc.Contains("sm8");
        } catch {
            return false;
        }
    }

    public static string GetPrimaryAndroidAbi() {
        IList<string> abis = Build.SupportedAbis;
        if (abis != null && abis.Count > 0) {
            if (abis.Any(a => a.Equals("arm64-v8a", StringComparison.OrdinalIgnoreCase))) {
                return "arm64-v8a";
            }
            if (abis.Any(a => a.Equals("x86_64", StringComparison.OrdinalIgnoreCase))) {
                return "x86_64";
            }
            if (abis.Any(a => a.Equals("armeabi-v7a", StringComparison.OrdinalIgnoreCase))) {
                return "armeabi-v7a";
            }
        }
        return "arm64-v8a";
    }

    public static async Task<OnnxRuntimeStatusResult> CheckStatusAsync(
        Context context,
        AppSettingsStore store,
        bool preferQnn = false,
        CancellationToken token = default) {
        bool installed = IsRuntimeInstalled(context);
        bool qnnOnDisk = IsQnnAvailableOnDisk(context);
        bool isQcom = IsQualcommSnapdragonDevice();
        string installedVer = store?.InstalledOnnxRuntimeVersion ?? "";
        string artifact = (preferQnn && isQcom) ? QnnArtifactId : StandardArtifactId;
        string remoteVer = "";
        string dlUrl = "";

        try {
            remoteVer = await FetchLatestMavenReleaseAsync(artifact, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(remoteVer) && artifact == QnnArtifactId) {
                artifact = StandardArtifactId;
                remoteVer = await FetchLatestMavenReleaseAsync(artifact, token).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(remoteVer)) {
                dlUrl = $"{MavenBaseUrl}/{artifact}/{remoteVer}/{artifact}-{remoteVer}.aar";
            }
        } catch {
            if (!installed) {
                throw;
            }
        }

        bool updateAvail = installed &&
            !string.IsNullOrEmpty(remoteVer) &&
            !string.IsNullOrEmpty(dlUrl) &&
            !string.Equals(installedVer.Trim(), remoteVer.Trim(), StringComparison.OrdinalIgnoreCase);

        return new OnnxRuntimeStatusResult {
            IsInstalled = installed,
            LocalLibPath = installed ? GetManagedLibraryPath(context) : "",
            HeaderPath = File.Exists(GetManagedHeaderPath(context)) ? GetManagedHeaderPath(context) : "",
            InstalledVersion = installedVer,
            RemoteVersion = remoteVer,
            IsUpdateAvailable = updateAvail,
            IsQnnPackageInstalled = qnnOnDisk,
            IsQualcommDevice = isQcom,
            DownloadUrl = dlUrl,
            ArtifactId = artifact
        };
    }

    public static async Task<string> DownloadAndInstallRuntimeAsync(
        Context context,
        AppSettingsStore store,
        OnnxRuntimeStatusResult status,
        bool preferQnn,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (status == null || string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            status = await CheckStatusAsync(context, store, preferQnn, token).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            throw new InvalidOperationException("No compatible ONNX Runtime Android AAR release found on Maven Central.");
        }

        string runtimeDir = GetRuntimeDir(context);
        string aarTemp = Path.Combine(GetTempWorkDir(context), $"ort_{Guid.NewGuid():N}.aar.part");
        string abi = GetPrimaryAndroidAbi();
        string jniPrefix = $"jni/{abi}/";

        try {
            using HttpClient dlClient = CreateHttpClient(TimeSpan.FromMinutes(6));

            using HttpResponseMessage resp = await dlClient.GetAsync(status.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? 0;
            await using (Stream netStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (FileStream outStream = File.Create(aarTemp)) {
                byte[] buf = new byte[65536];
                long received = 0;
                long nextUi = 0;
                int read;
                while ((read = await netStream.ReadAsync(buf, token).ConfigureAwait(false)) > 0) {
                    await outStream.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received >= nextUi || (totalBytes > 0 && received >= totalBytes)) {
                        double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes * 0.85, 0, 0.85) : 0.35;
                        double rxMb = received / (1024.0 * 1024.0);
                        double totMb = totalBytes / (1024.0 * 1024.0);
                        string msg = totalBytes > 0
                            ? $"ONNX Runtime: {rxMb:0.0} / {totMb:0.0} MB ({frac / 0.85:0%})"
                            : $"ONNX Runtime: {rxMb:0.0} MB";
                        onProgress?.Invoke(frac, msg);
                        nextUi = received + 128_000;
                    }
                }
            }

            onProgress?.Invoke(0.90, AndroidStrings.Active["NeuralExtractingLibs"]);
            bool extractedMainLib = false;
            bool extractedQnn = false;

            using (ZipArchive zip = ZipFile.OpenRead(aarTemp)) {
                foreach (ZipArchiveEntry entry in zip.Entries) {
                    token.ThrowIfCancellationRequested();
                    string fullName = entry.FullName.Replace('\\', '/');
                    if (fullName.StartsWith(jniPrefix, StringComparison.OrdinalIgnoreCase) &&
                        fullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) {
                        string fileName = Path.GetFileName(fullName);
                        if (string.IsNullOrEmpty(fileName)) {
                            continue;
                        }
                        string dest = Path.Combine(runtimeDir, fileName);
                        entry.ExtractToFile(dest, overwrite: true);
                        EnsureExecutable(dest);
                        if (fileName.Equals("libonnxruntime.so", StringComparison.OrdinalIgnoreCase)) {
                            extractedMainLib = true;
                        } else if (fileName.Contains("QnnHtp", StringComparison.OrdinalIgnoreCase)) {
                            extractedQnn = true;
                        }
                    } else if (fullName.EndsWith("/onnxruntime_c_api.h", StringComparison.OrdinalIgnoreCase) ||
                               fullName.Equals("headers/onnxruntime_c_api.h", StringComparison.OrdinalIgnoreCase)) {
                        string destHeader = GetManagedHeaderPath(context);
                        entry.ExtractToFile(destHeader, overwrite: true);
                    } else if (fullName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                               fullName.EndsWith("/LICENSE", StringComparison.OrdinalIgnoreCase) ||
                               fullName.EndsWith("LICENSE.txt", StringComparison.OrdinalIgnoreCase)) {
                        entry.ExtractToFile(Path.Combine(runtimeDir, "LICENSE.txt"), overwrite: true);
                    }
                }
            }

            if (!extractedMainLib) {
                throw new FileNotFoundException($"AAR did not contain {jniPrefix}libonnxruntime.so.");
            }

            if (store != null) {
                store.InstalledOnnxRuntimeVersion = status.RemoteVersion;
                store.OnnxRuntimeQnnInstalled = extractedQnn;
            }
            AppLogger.Info($"Installed ONNX Runtime v{status.RemoteVersion} ({abi}, QNN={extractedQnn}) to {runtimeDir}");
            onProgress?.Invoke(1.0, $"ONNX Runtime v{status.RemoteVersion} Ready");
            return GetManagedLibraryPath(context);
        } finally {
            try { if (File.Exists(aarTemp)) File.Delete(aarTemp); } catch { }
        }
    }

    /// <summary>
    /// Returns all registered ONNX models: the curated <see cref="NeuralModelCatalog"/> plus any user-imported
    /// standalone custom <c>.onnx</c> checkpoints in <c>FilesDir/neural_models/</c>.
    /// </summary>
    public static List<NeuralModelCatalogEntry> GetAllRegisteredModels(Context context) {
        List<NeuralModelCatalogEntry> result = new(NeuralModelCatalog.Entries.Count + 4);
        HashSet<string> curatedFileNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < NeuralModelCatalog.Entries.Count; i++) {
            NeuralModelCatalogEntry entry = NeuralModelCatalog.Entries[i];
            result.Add(entry);
            curatedFileNames.Add(entry.FileName);
            seenIds.Add(entry.Id);
        }

        if (context == null) {
            return result;
        }

        try {
            string modelsDir = GetModelsDir(context);
            foreach (string onnxPath in Directory.GetFiles(modelsDir, "*.onnx")) {
                string fileName = Path.GetFileName(onnxPath);
                if (string.IsNullOrEmpty(fileName) || curatedFileNames.Contains(fileName)) {
                    continue;
                }
                FileInfo fi = new(onnxPath);
                if (fi.Length <= 1024) {
                    continue;
                }
                NeuralModelCatalogEntry custom = LoadCustomEntry(onnxPath, fi.Length);
                if (custom != null && seenIds.Add(custom.Id)) {
                    result.Add(custom);
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"Custom ONNX model scan warning: {ex.Message}");
        }

        return result;
    }

    /// <summary>Returns all currently installed models (curated catalog + standalone custom <c>.onnx</c>).</summary>
    public static List<NeuralModelCatalogEntry> GetInstalledModelEntries(Context context) {
        List<NeuralModelCatalogEntry> installed = [];
        if (context == null) {
            return installed;
        }
        List<NeuralModelCatalogEntry> all = GetAllRegisteredModels(context);
        for (int i = 0; i < all.Count; i++) {
            if (!string.IsNullOrEmpty(ResolveModelPath(context, all[i].Id))) {
                installed.Add(all[i]);
            }
        }
        return installed;
    }

    /// <summary>Resolves the local path of a curated or custom ONNX model if installed on disk.</summary>
    public static string ResolveModelPath(Context context, string modelId) {
        if (context == null || string.IsNullOrWhiteSpace(modelId)) {
            return "";
        }
        string modelsDir = GetModelsDir(context);
        NeuralModelCatalogEntry entry = NeuralModelCatalog.FindById(modelId);
        if (entry != null) {
            string curatedPath = Path.Combine(modelsDir, entry.FileName);
            return File.Exists(curatedPath) && new FileInfo(curatedPath).Length > 1024 ? curatedPath : "";
        }

        string directName = modelId.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? SanitizeId(modelId)
            : $"{SanitizeId(modelId)}.onnx";
        string directPath = Path.Combine(modelsDir, directName);
        if (File.Exists(directPath) && new FileInfo(directPath).Length > 1024) {
            return directPath;
        }

        // Fallback: check custom .meta.json sidecars whose Id matches modelId
        try {
            foreach (string metaPath in Directory.GetFiles(modelsDir, "*" + CustomMetaSuffix)) {
                CustomOnnxModelMetadata meta = ReadCustomMeta(metaPath);
                if (meta != null && string.Equals(meta.Id, modelId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(meta.FileName)) {
                    string p = Path.Combine(modelsDir, meta.FileName);
                    if (File.Exists(p) && new FileInfo(p).Length > 1024) {
                        return p;
                    }
                }
            }
        } catch { }

        return "";
    }

    public static bool IsModelInstalled(Context context, string modelId) =>
        !string.IsNullOrEmpty(ResolveModelPath(context, modelId));

    /// <summary>Returns the IDs of all installed ONNX models on disk, including both curated catalog entries and custom imported checkpoints.</summary>
    public static List<string> GetInstalledModelIds(Context context) {
        List<string> ids = [];
        if (context == null) {
            return ids;
        }
        List<NeuralModelCatalogEntry> installed = GetInstalledModelEntries(context);
        for (int i = 0; i < installed.Count; i++) {
            ids.Add(installed[i].Id);
        }
        return ids;
    }

    public static async Task<string> DownloadModelAsync(
        Context context,
        NeuralModelCatalogEntry entry,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entry);

        List<string> candidateUrls = [];
        if (!string.IsNullOrWhiteSpace(entry.DownloadUrl)) {
            candidateUrls.Add(entry.DownloadUrl.Trim());
        }
        if (entry.FallbackDownloadUrls != null) {
            for (int i = 0; i < entry.FallbackDownloadUrls.Count; i++) {
                string fb = entry.FallbackDownloadUrls[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(fb) && !candidateUrls.Contains(fb, StringComparison.OrdinalIgnoreCase)) {
                    candidateUrls.Add(fb);
                }
            }
        }
        if (candidateUrls.Count == 0) {
            throw new InvalidOperationException(
                string.Format(AndroidStrings.Active["NeuralModelDownloadFallbackErr"], entry.DisplayName));
        }

        string destPath = Path.Combine(GetModelsDir(context), entry.FileName);
        string partPath = destPath + ".part";
        Exception lastError = null;

        using HttpClient dlClient = CreateHttpClient(TimeSpan.FromMinutes(15));
        for (int u = 0; u < candidateUrls.Count; u++) {
            token.ThrowIfCancellationRequested();
            string url = candidateUrls[u];
            try {
                if (File.Exists(partPath)) {
                    File.Delete(partPath);
                }
                using HttpResponseMessage resp = await dlClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                long totalBytes = resp.Content.Headers.ContentLength ?? (long)Math.Round(entry.ApproxSizeMb * 1024.0 * 1024.0);
                await using (Stream netStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (FileStream outStream = File.Create(partPath)) {
                    byte[] buf = new byte[131072];
                    long received = 0;
                    long nextUi = 0;
                    int read;
                    while ((read = await netStream.ReadAsync(buf, token).ConfigureAwait(false)) > 0) {
                        await outStream.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                        received += read;
                        if (received >= nextUi || (totalBytes > 0 && received >= totalBytes)) {
                            double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes, 0, 0.99) : 0.4;
                            double rxMb = received / (1024.0 * 1024.0);
                            double totMb = totalBytes / (1024.0 * 1024.0);
                            string msg = totalBytes > 0
                                ? $"{entry.DisplayName}: {rxMb:0.0} / {totMb:0.0} MB ({frac:0%})"
                                : $"{entry.DisplayName}: {rxMb:0.0} MB";
                            onProgress?.Invoke(frac, msg);
                            nextUi = received + 262_144;
                        }
                    }
                }

                if (File.Exists(destPath)) {
                    File.Delete(destPath);
                }
                File.Move(partPath, destPath);
                AppLogger.Info($"Downloaded neural model '{entry.Id}' from mirror #{u + 1} to {destPath}");
                onProgress?.Invoke(1.0, $"{entry.DisplayName} Ready");
                return destPath;
            } catch (System.OperationCanceledException) {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
                throw;
            } catch (Exception ex) {
                lastError = ex;
                AppLogger.Warn($"Neural model '{entry.Id}' mirror #{u + 1} failed ({url}): {ex.Message}");
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            }
        }

        string friendly = string.Format(AndroidStrings.Active["NeuralModelDownloadFallbackErr"], entry.DisplayName);
        throw new InvalidOperationException(
            lastError != null ? $"{friendly} ({lastError.Message})" : friendly,
            lastError);
    }

    /// <summary>
    /// Imports a user-supplied local <c>.onnx</c> checkpoint into managed storage either into an existing curated
    /// catalog slot (<paramref name="targetModelId"/> matches a catalog entry) or as a first-class standalone
    /// custom model with a persistent <c>.meta.json</c> descriptor so the Slot 7A orchestrator can use it.
    /// </summary>
    public static NeuralModelCatalogEntry ImportCustomOnnxModel(
        Context context,
        string sourceFilePath,
        string targetModelId,
        NeuralModelTask? customTask = null,
        string customDisplayName = null,
        int expectedSampleRate = 44100) {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath)) {
            throw new FileNotFoundException("ONNX model file not found.", sourceFilePath);
        }

        string modelsDir = GetModelsDir(context);
        NeuralModelCatalogEntry catalogEntry = !string.IsNullOrWhiteSpace(targetModelId)
            ? NeuralModelCatalog.FindById(targetModelId)
            : null;
        if (catalogEntry != null) {
            string destPath = Path.Combine(modelsDir, catalogEntry.FileName);
            File.Copy(sourceFilePath, destPath, overwrite: true);
            AppLogger.Info($"Imported local ONNX model '{Path.GetFileName(sourceFilePath)}' into catalog slot '{catalogEntry.Id}'");
            return catalogEntry;
        }

        string rawBaseName = Path.GetFileNameWithoutExtension(sourceFilePath);
        string safeId = !string.IsNullOrWhiteSpace(targetModelId)
            ? SanitizeId(targetModelId)
            : $"custom_{SanitizeId(rawBaseName)}";
        if (string.IsNullOrWhiteSpace(safeId)) {
            safeId = $"custom_{DateTime.UtcNow:yyyyMMddHHmmss}";
        }
        // Avoid colliding with any curated catalog ID or filename
        if (NeuralModelCatalog.FindById(safeId) != null) {
            safeId = "custom_" + safeId;
        }

        string destFileName = safeId.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? safeId : $"{safeId}.onnx";
        string customDestPath = Path.Combine(modelsDir, destFileName);
        File.Copy(sourceFilePath, customDestPath, overwrite: true);

        NeuralModelTask task = customTask ?? InferTaskFromFileName(rawBaseName);
        int sr = expectedSampleRate >= 8000 ? expectedSampleRate : 44100;
        string displayName = !string.IsNullOrWhiteSpace(customDisplayName)
            ? customDisplayName.Trim()
            : rawBaseName.Replace('_', ' ').Trim();
        if (string.IsNullOrEmpty(displayName)) {
            displayName = safeId;
        }

        IReadOnlyList<string> defaultStems = NeuralModelCatalog.GetDefaultOutputStemNames(task);
        CustomOnnxModelMetadata meta = new() {
            Id = safeId,
            DisplayName = displayName,
            FileName = destFileName,
            Task = (int)task,
            ExpectedSampleRate = sr,
            DefaultChunkSamples = task == NeuralModelTask.CodecRestoreApollo || task == NeuralModelTask.BandwidthExtension ? 131072 : 262144,
            OutputStemNames = defaultStems.ToArray()
        };
        string metaPath = customDestPath + CustomMetaSuffix;
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));

        long fileBytes = new FileInfo(customDestPath).Length;
        AppLogger.Info($"Registered custom ONNX model '{safeId}' (Task={task}, SR={sr} Hz, {fileBytes / (1024.0 * 1024.0):0.1} MB)");
        return NeuralModelCatalog.CreateCustomEntry(safeId, displayName, destFileName, task, fileBytes, sr, defaultStems);
    }

    public static bool DeleteModel(Context context, string modelId) {
        string path = ResolveModelPath(context, modelId);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return false;
        }
        File.Delete(path);
        string metaPath = path + CustomMetaSuffix;
        try { if (File.Exists(metaPath)) File.Delete(metaPath); } catch { }
        return true;
    }

    public static long GetModelsAndRuntimeSizeBytes(Context context) {
        if (context == null) {
            return 0;
        }
        return SumDirBytes(GetRuntimeDir(context)) + SumDirBytes(GetModelsDir(context)) + SumDirBytes(GetTempWorkDir(context));
    }

    public static void CleanupTempCache(Context context) {
        if (context == null) {
            return;
        }
        try {
            string dir = Path.Combine(context.CacheDir.AbsolutePath, "neural_work");
            if (Directory.Exists(dir)) {
                foreach (string f in Directory.GetFiles(dir)) {
                    try { File.Delete(f); } catch { }
                }
                foreach (string sub in Directory.GetDirectories(dir)) {
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
        } catch {
            // Best-effort cleanup
        }
    }

    public static void ClearAllModelsAndCache(Context context) {
        CleanupTempCache(context);
        if (context == null) {
            return;
        }
        try {
            string dir = GetModelsDir(context);
            if (Directory.Exists(dir)) {
                foreach (string f in Directory.GetFiles(dir)) {
                    try { File.Delete(f); } catch { }
                }
            }
        } catch {
            // Best-effort cleanup
        }
    }

    static NeuralModelCatalogEntry LoadCustomEntry(string onnxPath, long fileBytes) {
        string fileName = Path.GetFileName(onnxPath);
        string metaPath = onnxPath + CustomMetaSuffix;
        CustomOnnxModelMetadata meta = ReadCustomMeta(metaPath);
        if (meta != null && !string.IsNullOrWhiteSpace(meta.Id)) {
            NeuralModelTask task = Enum.IsDefined(typeof(NeuralModelTask), meta.Task)
                ? (NeuralModelTask)meta.Task
                : InferTaskFromFileName(fileName);
            int sr = meta.ExpectedSampleRate >= 8000 ? meta.ExpectedSampleRate : 44100;
            IReadOnlyList<string> stems = meta.OutputStemNames != null && meta.OutputStemNames.Length > 0
                ? meta.OutputStemNames
                : NeuralModelCatalog.GetDefaultOutputStemNames(task);
            return NeuralModelCatalog.CreateCustomEntry(
                meta.Id,
                string.IsNullOrWhiteSpace(meta.DisplayName) ? Path.GetFileNameWithoutExtension(fileName) : meta.DisplayName,
                fileName,
                task,
                fileBytes,
                sr,
                stems);
        }

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        NeuralModelTask inferred = InferTaskFromFileName(baseName);
        return NeuralModelCatalog.CreateCustomEntry(
            SanitizeId(baseName),
            baseName.Replace('_', ' '),
            fileName,
            inferred,
            fileBytes,
            expectedSampleRate: 44100,
            outputStemNames: NeuralModelCatalog.GetDefaultOutputStemNames(inferred));
    }

    static CustomOnnxModelMetadata ReadCustomMeta(string metaPath) {
        try {
            if (!File.Exists(metaPath)) {
                return null;
            }
            return JsonSerializer.Deserialize<CustomOnnxModelMetadata>(File.ReadAllText(metaPath));
        } catch {
            return null;
        }
    }

    static NeuralModelTask InferTaskFromFileName(string name) {
        string lower = (name ?? "").ToLowerInvariant();
        if (lower.Contains("karaoke") || lower.Contains("lead_back") || lower.Contains("backing")) {
            return NeuralModelTask.KaraokeLeadBacking;
        }
        if (lower.Contains("dereverb") || lower.Contains("echo") || lower.Contains("reverb")) {
            return NeuralModelTask.Dereverb;
        }
        if (lower.Contains("debleed") || lower.Contains("denoise") || lower.Contains("bleed")) {
            return NeuralModelTask.DebleedDenoise;
        }
        if (lower.Contains("apollo") || lower.Contains("restore")) {
            return NeuralModelTask.CodecRestoreApollo;
        }
        if (lower.Contains("bwe") || lower.Contains("flashsr") || lower.Contains("bandwidth") || lower.Contains("superres")) {
            return NeuralModelTask.BandwidthExtension;
        }
        if (lower.Contains("6stem") || lower.Contains("6s") || lower.Contains("6_stem") || lower.Contains("htdemucs_6")) {
            return NeuralModelTask.StemSeparation6;
        }
        if (lower.Contains("vocal") || lower.Contains("viperx") || lower.Contains("kim_vocal")) {
            return NeuralModelTask.VocalSeparation;
        }
        return NeuralModelTask.StemSeparation4;
    }

    static async Task<string> FetchLatestMavenReleaseAsync(string artifactId, CancellationToken token) {
        string metaUrl = $"{MavenBaseUrl}/{artifactId}/maven-metadata.xml";
        string xml = await http.GetStringAsync(metaUrl, token).ConfigureAwait(false);
        XDocument doc = XDocument.Parse(xml);
        string release = doc.Descendants("release").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrEmpty(release)) {
            return release;
        }
        return doc.Descendants("latest").FirstOrDefault()?.Value?.Trim() ?? "";
    }

    static long SumDirBytes(string dir) {
        try {
            if (!Directory.Exists(dir)) {
                return 0;
            }
            long total = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total;
        } catch {
            return 0;
        }
    }

    static void EnsureExecutable(string path) {
        try { new Java.IO.File(path).SetExecutable(true, false); } catch { }
        try { Os.Chmod(path, Convert.ToInt32("755", 8)); } catch { }
    }

    static string SanitizeId(string id) {
        if (string.IsNullOrWhiteSpace(id)) {
            return "";
        }
        string clean = id;
        foreach (char c in Path.GetInvalidFileNameChars()) {
            clean = clean.Replace(c, '_');
        }
        return clean.Replace(' ', '_').Trim();
    }
}
