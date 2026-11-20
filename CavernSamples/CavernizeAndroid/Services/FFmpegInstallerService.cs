using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Android.Content;
using Android.OS;
using Android.Systems;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class FFmpegStatusResult {
    public bool IsInstalled { get; init; }
    public bool IsWorking { get; init; }
    public string LocalPath { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
    public string DownloadUrl { get; init; } = "";
    public long AssetSizeBytes { get; init; }
}

/// <summary>
/// Automatically checks GitHub for native Android NDK (Bionic) FFmpeg builds (arm64-v8a / x86_64 / armeabi-v7a),
/// downloads and extracts the binary and its companion shared libraries into private app storage, configures
/// LD_LIBRARY_PATH, and provides safe pipe-drained process execution without SIGSYS or SafePipeHandle leaks.
/// </summary>
public static class FFmpegInstallerService {
    const string LatestReleaseApiUrl = "https://api.github.com/repos/husen-hn/ffmpeg-android-binary/releases/latest";
    const string FallbackZipBaseUrl = "https://github.com/husen-hn/ffmpeg-android-binary/releases/download/v2.0.0/";

    static readonly HttpClient http = CreateHttpClient();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.8"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static string GetManagedFFmpegPath(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "bin");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ffmpeg");
    }

    public static string GetManagedFFmpegLibDir(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "bin", "ffmpeg_lib");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolveExistingFFmpegPath(Context context, AppSettingsStore store) {
        if (!string.IsNullOrEmpty(store.FFmpegLocation) && File.Exists(store.FFmpegLocation)) {
            return store.FFmpegLocation;
        }
        string managed = GetManagedFFmpegPath(context);
        if (File.Exists(managed)) {
            store.FFmpegLocation = managed;
            return managed;
        }
        return "";
    }

    public static void ConfigureProcessStartInfo(ProcessStartInfo psi, string executablePath) {
        if (psi == null || string.IsNullOrEmpty(executablePath)) {
            return;
        }
        try {
            string binDir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(binDir)) {
                return;
            }
            List<string> libDirs = [];
            string managedLib = Path.Combine(binDir, "ffmpeg_lib");
            if (Directory.Exists(managedLib)) {
                libDirs.Add(managedLib);
            }
            string siblingLib = Path.Combine(Directory.GetParent(binDir)?.FullName ?? binDir, "lib");
            if (Directory.Exists(siblingLib) && !libDirs.Contains(siblingLib, StringComparer.Ordinal)) {
                libDirs.Add(siblingLib);
            }
            if (Directory.Exists(binDir) && !libDirs.Contains(binDir, StringComparer.Ordinal)) {
                libDirs.Add(binDir);
            }

            if (libDirs.Count > 0) {
                string existing = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out string cur) ? cur : "";
                string joined = string.Join(":", libDirs);
                psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? joined : $"{joined}:{existing}";
            }
        } catch {
            // Best-effort environment setup
        }
    }

    public static (int exitCode, string stdout, string stderr) RunProcessSafe(ProcessStartInfo psi, int timeoutMs = 15000) {
        if (psi == null) {
            return (-1, "", "Null ProcessStartInfo");
        }
        ConfigureProcessStartInfo(psi, psi.FileName);
        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) {
            return (-1, "", "Failed to start process.");
        }

        StringBuilder outSb = new();
        StringBuilder errSb = new();
        using ManualResetEventSlim outDone = new(false);
        using ManualResetEventSlim errDone = new(false);

        proc.OutputDataReceived += (_, e) => {
            if (e.Data == null) {
                outDone.Set();
            } else {
                outSb.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) => {
            if (e.Data == null) {
                errDone.Set();
            } else {
                errSb.AppendLine(e.Data);
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited) {
            try {
                proc.Kill(entireProcessTree: true);
            } catch {
                // Best-effort kill
            }
            try {
                proc.WaitForExit(1500);
            } catch {
                // Ignore
            }
        } else {
            // Wait parameterless after WaitForExit(timeout) so async stdout/stderr pipe tasks complete before Dispose()
            proc.WaitForExit();
        }

        outDone.Wait(1500);
        errDone.Wait(1500);
        try {
            proc.CancelOutputRead();
        } catch {
            // Ignore
        }
        try {
            proc.CancelErrorRead();
        } catch {
            // Ignore
        }

        int code = -1;
        try {
            code = proc.HasExited ? proc.ExitCode : -1;
        } catch {
            // Ignore
        }
        return (code, outSb.ToString(), errSb.ToString());
    }

    static string GetTargetZipAsset() {
        IList<string> abis = Build.SupportedAbis;
        if (abis != null && abis.Count > 0) {
            if (abis.Any(a => a.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                              a.Contains("aarch64", StringComparison.OrdinalIgnoreCase))) {
                return "arm64-v8a.zip";
            }
            if (abis.Any(a => a.Contains("x86_64", StringComparison.OrdinalIgnoreCase))) {
                return "x86_64.zip";
            }
            if (abis.Any(a => a.Contains("armeabi", StringComparison.OrdinalIgnoreCase))) {
                return "armeabi-v7a.zip";
            }
        }
        return "arm64-v8a.zip";
    }

    public static async Task<FFmpegStatusResult> CheckStatusAsync(
        Context context,
        AppSettingsStore store,
        CancellationToken token = default) {
        string localPath = ResolveExistingFFmpegPath(context, store);
        bool isInstalled = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        string liveProbeVer = isInstalled ? ProbeFFmpegVersion(localPath) : "";
        bool isWorking = isInstalled && !string.IsNullOrEmpty(liveProbeVer);
        string installedVer = isWorking
            ? (!string.IsNullOrWhiteSpace(store.InstalledFFmpegVersion) ? store.InstalledFFmpegVersion : liveProbeVer)
            : (isInstalled ? "compat-error (SIGSYS)" : "");

        string targetAsset = GetTargetZipAsset();
        string remoteTag = "v2.0.0";
        string dlUrl = FallbackZipBaseUrl + targetAsset;
        long size = 0;

        try {
            using HttpRequestMessage req = new(HttpMethod.Get, LatestReleaseApiUrl);
            using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) {
                string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string parsedTag = root.TryGetProperty("tag_name", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(parsedTag)) {
                    remoteTag = parsedTag;
                }

                if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement asset in assetsEl.EnumerateArray()) {
                        string name = asset.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "") : "";
                        if (string.Equals(name, targetAsset, StringComparison.OrdinalIgnoreCase)) {
                            string u = asset.TryGetProperty("browser_download_url", out JsonElement uEl) ? (uEl.GetString() ?? "") : "";
                            if (!string.IsNullOrWhiteSpace(u)) {
                                dlUrl = u;
                            }
                            if (asset.TryGetProperty("size", out JsonElement sEl)) {
                                size = sEl.GetInt64();
                            }
                            break;
                        }
                    }
                }
            }
        } catch {
            // Keep deterministic fallback URL if offline/rate-limited
        }

        bool isManaged = string.Equals(localPath, GetManagedFFmpegPath(context), StringComparison.Ordinal);
        string remoteMarker = $"ndk-{remoteTag}";
        bool updateAvailable = isInstalled && (
            !isWorking ||
            (!string.IsNullOrEmpty(remoteTag) && !string.IsNullOrEmpty(dlUrl) && isManaged &&
             !string.Equals(installedVer.Trim(), remoteMarker, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(installedVer.Trim(), remoteTag.Trim(), StringComparison.OrdinalIgnoreCase)));

        return new FFmpegStatusResult {
            IsInstalled = isInstalled,
            IsWorking = isWorking,
            LocalPath = localPath,
            InstalledVersion = installedVer,
            RemoteVersion = remoteMarker,
            IsUpdateAvailable = updateAvailable,
            DownloadUrl = dlUrl,
            AssetSizeBytes = size
        };
    }

    public static async Task<string> DownloadAndInstallAsync(
        Context context,
        AppSettingsStore store,
        FFmpegStatusResult status,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (status == null || string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            status = await CheckStatusAsync(context, store, token).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            throw new InvalidOperationException("No compatible Android NDK FFmpeg binary URL found.");
        }

        string destPath = GetManagedFFmpegPath(context);
        string libDir = GetManagedFFmpegLibDir(context);
        string zipTemp = destPath + ".ndk.zip.part";
        string extractDir = destPath + "_ndk_extract";
        if (File.Exists(zipTemp)) File.Delete(zipTemp);
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

        try {
            using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(10) };
            dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.8"));

            using HttpResponseMessage resp = await dlClient.GetAsync(status.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? status.AssetSizeBytes;
            await using (Stream netStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (FileStream zipOut = File.Create(zipTemp)) {
                byte[] buf = new byte[65536];
                long received = 0;
                long nextUi = 0;
                int read;
                while ((read = await netStream.ReadAsync(buf, token).ConfigureAwait(false)) > 0) {
                    await zipOut.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received >= nextUi || (totalBytes > 0 && received >= totalBytes)) {
                        double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes * 0.85, 0, 0.85) : 0.25;
                        double rxMb = received / (1024.0 * 1024.0);
                        double totMb = totalBytes / (1024.0 * 1024.0);
                        string msg = totalBytes > 0
                            ? $"FFmpeg NDK: {rxMb:0.0} MB / {totMb:0.0} MB ({frac / 0.85:0%})"
                            : $"FFmpeg NDK: {rxMb:0.0} MB";
                        onProgress?.Invoke(frac, msg);
                        nextUi = received + 180_000;
                    }
                }
            }

            onProgress?.Invoke(0.90, "FFmpeg NDK: Extraindo binário e bibliotecas nativas...");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipTemp, extractDir, overwriteFiles: true);

            string extractedBin = Directory.GetFiles(extractDir, "ffmpeg", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(extractedBin) || !File.Exists(extractedBin)) {
                throw new FileNotFoundException("Extracted Android FFmpeg archive did not contain 'bin/ffmpeg'.");
            }

            if (Directory.Exists(libDir)) {
                Directory.Delete(libDir, true);
            }
            Directory.CreateDirectory(libDir);
            foreach (string soFile in Directory.GetFiles(extractDir, "*.so*", SearchOption.AllDirectories)) {
                string targetSo = Path.Combine(libDir, Path.GetFileName(soFile));
                File.Copy(soFile, targetSo, overwrite: true);
                EnsureExecutable(targetSo);
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(extractedBin, destPath);
            EnsureExecutable(destPath);
        } finally {
            try { if (File.Exists(zipTemp)) File.Delete(zipTemp); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
        }

        string detectedBanner = ProbeFFmpegVersion(destPath);
        store.FFmpegLocation = destPath;
        store.InstalledFFmpegVersion = !string.IsNullOrEmpty(status.RemoteVersion) ? status.RemoteVersion : detectedBanner;
        onProgress?.Invoke(1.0, string.IsNullOrEmpty(detectedBanner) ? "FFmpeg NDK Ready" : $"FFmpeg NDK {detectedBanner} Ready");
        return destPath;
    }

    public static void EnsureExecutable(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return;
        }
        try {
            new Java.IO.File(path).SetExecutable(true, false);
        } catch {
            // Fall through to POSIX chmod
        }
        try {
            Os.Chmod(path, Convert.ToInt32("755", 8));
        } catch {
            // Best-effort permission update
        }

        try {
            string binDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(binDir)) {
                string libDir = Path.Combine(binDir, "ffmpeg_lib");
                if (Directory.Exists(libDir)) {
                    foreach (string so in Directory.GetFiles(libDir, "*.so*")) {
                        Os.Chmod(so, Convert.ToInt32("755", 8));
                    }
                }
            }
        } catch {
            // Best-effort permission update on shared libs
        }
    }

    public static string ProbeFFmpegVersion(string path) {
        try {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                return "";
            }
            EnsureExecutable(path);
            ProcessStartInfo psi = new() {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var (exitCode, stdout, _) = RunProcessSafe(psi, 3500);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) {
                return "";
            }
            string firstLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return firstLine.Replace("ffmpeg version ", "", StringComparison.OrdinalIgnoreCase).Split(' ')[0].Trim();
        } catch {
            return "";
        }
    }
}