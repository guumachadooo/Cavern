using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

using Android.Content;
using Android.OS;
using Android.Systems;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class FFmpegStatusResult {
    public bool IsInstalled { get; init; }
    public string LocalPath { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
    public string DownloadUrl { get; init; } = "";
    public long AssetSizeBytes { get; init; }
}

/// <summary>
/// Automatically checks GitHub for static Android-compatible ARM64/x64 FFmpeg builds,
/// downloads and decompresses them with progress, sets executable permissions, and checks updates.
/// </summary>
public static class FFmpegInstallerService {
    const string LatestReleaseApiUrl = "https://api.github.com/repos/eugeneware/ffmpeg-static/releases/latest";

    static readonly HttpClient http = CreateHttpClient();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static string GetManagedFFmpegPath(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "bin");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ffmpeg");
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

    public static async Task<FFmpegStatusResult> CheckStatusAsync(
        Context context,
        AppSettingsStore store,
        CancellationToken token = default) {
        string localPath = ResolveExistingFFmpegPath(context, store);
        bool isInstalled = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        string installedVer = store.InstalledFFmpegVersion ?? "";

        bool isArm64 = true;
        IList<string> abis = Build.SupportedAbis;
        if (abis != null && abis.Count > 0) {
            isArm64 = abis.Any(a => a.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                                    a.Contains("aarch64", StringComparison.OrdinalIgnoreCase));
        }
        string targetAsset = isArm64 ? "ffmpeg-linux-arm64.gz" : "ffmpeg-linux-x64.gz";

        using HttpRequestMessage req = new(HttpMethod.Get, LatestReleaseApiUrl);
        using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string remoteTag = root.TryGetProperty("tag_name", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
        string dlUrl = "";
        long size = 0;

        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement asset in assetsEl.EnumerateArray()) {
                string name = asset.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "") : "";
                if (string.Equals(name, targetAsset, StringComparison.OrdinalIgnoreCase)) {
                    dlUrl = asset.TryGetProperty("browser_download_url", out JsonElement uEl) ? (uEl.GetString() ?? "") : "";
                    if (asset.TryGetProperty("size", out JsonElement sEl)) {
                        size = sEl.GetInt64();
                    }
                    break;
                }
            }
        }

        bool isManaged = string.Equals(localPath, GetManagedFFmpegPath(context), StringComparison.Ordinal);
        bool updateAvailable = isInstalled &&
            !string.IsNullOrEmpty(remoteTag) &&
            !string.IsNullOrEmpty(dlUrl) &&
            (isManaged || !string.IsNullOrEmpty(installedVer)) &&
            !string.Equals(installedVer.Trim(), remoteTag.Trim(), StringComparison.OrdinalIgnoreCase);

        return new FFmpegStatusResult {
            IsInstalled = isInstalled,
            LocalPath = localPath,
            InstalledVersion = installedVer,
            RemoteVersion = remoteTag,
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
            throw new InvalidOperationException("No compatible Android FFmpeg binary URL found in GitHub release.");
        }

        string destPath = GetManagedFFmpegPath(context);
        string gzTemp = destPath + ".gz.part";
        string binTemp = destPath + ".part";
        if (File.Exists(gzTemp)) File.Delete(gzTemp);
        if (File.Exists(binTemp)) File.Delete(binTemp);

        using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(10) };
        dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.1"));

        using HttpResponseMessage resp = await dlClient.GetAsync(status.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? status.AssetSizeBytes;
        await using (Stream netStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (FileStream gzOut = File.Create(gzTemp)) {
            byte[] buf = new byte[65536];
            long received = 0;
            long nextUi = 0;
            int read;
            while ((read = await netStream.ReadAsync(buf, token).ConfigureAwait(false)) > 0) {
                await gzOut.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (received >= nextUi || (totalBytes > 0 && received >= totalBytes)) {
                    double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes * 0.85, 0, 0.85) : 0.25;
                    double rxMb = received / (1024.0 * 1024.0);
                    double totMb = totalBytes / (1024.0 * 1024.0);
                    string msg = totalBytes > 0
                        ? $"FFmpeg: {rxMb:0.0} MB / {totMb:0.0} MB ({frac / 0.85:0%})"
                        : $"FFmpeg: {rxMb:0.0} MB";
                    onProgress?.Invoke(frac, msg);
                    nextUi = received + 180_000;
                }
            }
        }

        onProgress?.Invoke(0.90, "FFmpeg: Extracting static binary...");
        await using (FileStream gzIn = File.OpenRead(gzTemp))
        await using (GZipStream gz = new(gzIn, CompressionMode.Decompress))
        await using (FileStream binOut = File.Create(binTemp)) {
            await gz.CopyToAsync(binOut, token).ConfigureAwait(false);
        }

        if (File.Exists(gzTemp)) File.Delete(gzTemp);
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(binTemp, destPath);

        EnsureExecutable(destPath);
        string detectedBanner = ProbeFFmpegVersion(destPath);

        store.FFmpegLocation = destPath;
        store.InstalledFFmpegVersion = !string.IsNullOrEmpty(status.RemoteVersion) ? status.RemoteVersion : detectedBanner;
        onProgress?.Invoke(1.0, string.IsNullOrEmpty(detectedBanner) ? "FFmpeg Ready" : $"FFmpeg {detectedBanner} Ready");
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
    }

    public static string ProbeFFmpegVersion(string path) {
        try {
            EnsureExecutable(path);
            using System.Diagnostics.Process proc = new();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo {
                FileName = path,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            string firstLine = proc.StandardOutput.ReadLine() ?? "";
            proc.WaitForExit(3000);
            return firstLine.Replace("ffmpeg version ", "", StringComparison.OrdinalIgnoreCase).Split(' ')[0].Trim();
        } catch {
            return "";
        }
    }
}
