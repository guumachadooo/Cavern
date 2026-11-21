using System.IO.Compression;
using System.Net.Http.Headers;
using System.Xml.Linq;

using Android.Content;
using Android.OS;
using Android.Systems;

using Cavernize.Logic.Spatial;

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
/// Manages on-demand download, extraction, update checking, and storage hygiene for the native ARM64/x64
/// ONNX Runtime shared libraries (<c>FilesDir/bin/onnxruntime/libonnxruntime.so</c> + optional Snapdragon QNN HTP),
/// C API header (<c>onnxruntime_c_api.h</c>), lazy ONNX audio models (<c>FilesDir/neural_models/</c>),
/// and temporary inference cache (<c>CacheDir/neural_work/</c>).
/// </summary>
public static class OnnxRuntimeInstallerService {
    const string MavenBaseUrl = "https://repo1.maven.org/maven2/com/microsoft/onnxruntime";
    const string StandardArtifactId = "onnxruntime-android";
    const string QnnArtifactId = "onnxruntime-android-qnn";

    static readonly HttpClient http = CreateHttpClient();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.6"));
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
            using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(6) };
            dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.6"));

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

            onProgress?.Invoke(0.90, "ONNX Runtime: Extraindo bibliotecas nativas...");
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

    /// <summary>Resolves the local path of a curated or custom ONNX model if installed on disk.</summary>
    public static string ResolveModelPath(Context context, string modelId) {
        if (context == null || string.IsNullOrWhiteSpace(modelId)) {
            return "";
        }
        NeuralModelCatalogEntry entry = NeuralModelCatalog.FindById(modelId);
        string fileName = entry != null ? entry.FileName : $"{SanitizeId(modelId)}.onnx";
        string path = Path.Combine(GetModelsDir(context), fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 1024) {
            return path;
        }
        return "";
    }

    public static bool IsModelInstalled(Context context, string modelId) =>
        !string.IsNullOrEmpty(ResolveModelPath(context, modelId));

    public static List<string> GetInstalledModelIds(Context context) {
        List<string> ids = [];
        if (context == null) {
            return ids;
        }
        foreach (NeuralModelCatalogEntry entry in NeuralModelCatalog.Entries) {
            if (IsModelInstalled(context, entry.Id)) {
                ids.Add(entry.Id);
            }
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
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl)) {
            throw new InvalidOperationException($"Model '{entry.DisplayName}' does not have an automatic download URL; import a local .onnx file instead.");
        }

        string destPath = Path.Combine(GetModelsDir(context), entry.FileName);
        string partPath = destPath + ".part";
        if (File.Exists(partPath)) {
            File.Delete(partPath);
        }

        try {
            using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(15) };
            dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.6"));
            using HttpResponseMessage resp = await dlClient.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? (long)entry.EstimatedSizeMb * 1024L * 1024L;
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
            AppLogger.Info($"Downloaded neural model '{entry.Id}' to {destPath}");
            onProgress?.Invoke(1.0, $"{entry.DisplayName} Ready");
            return destPath;
        } finally {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    /// <summary>Imports a user-supplied local <c>.onnx</c> checkpoint into managed storage for the chosen catalog slot.</summary>
    public static string ImportCustomOnnxModel(Context context, string sourceFilePath, string targetModelId) {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(sourceFilePath) || !File.Exists(sourceFilePath)) {
            throw new FileNotFoundException("ONNX model file not found.", sourceFilePath);
        }
        NeuralModelCatalogEntry entry = NeuralModelCatalog.FindById(targetModelId);
        string destName = entry != null ? entry.FileName : $"{SanitizeId(targetModelId)}.onnx";
        string destPath = Path.Combine(GetModelsDir(context), destName);
        File.Copy(sourceFilePath, destPath, overwrite: true);
        AppLogger.Info($"Imported local ONNX model '{Path.GetFileName(sourceFilePath)}' as '{targetModelId}'");
        return destPath;
    }

    public static bool DeleteModel(Context context, string modelId) {
        string path = ResolveModelPath(context, modelId);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return false;
        }
        File.Delete(path);
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
        foreach (char c in Path.GetInvalidFileNameChars()) {
            id = id.Replace(c, '_');
        }
        return id.Trim();
    }
}
