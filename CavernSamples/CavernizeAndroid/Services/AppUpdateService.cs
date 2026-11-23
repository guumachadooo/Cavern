using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class UpdateCheckResult {
    public bool IsUpdateAvailable { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long AssetSizeBytes { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public string PublishedAt { get; init; } = "";
}

/// <summary>
/// Checks GitHub Releases (guumachadooo/Cavern tag android-latest), downloads APK updates with progress,
/// and invokes the Android package installer.
/// </summary>
public static class AppUpdateService {
    const string ReleaseApiUrl = "https://api.github.com/repos/guumachadooo/Cavern/releases/tags/android-latest";
    const string LatestWebReleaseUrl = "https://github.com/guumachadooo/Cavern/releases/latest";
    const string TargetAssetName = "Cavernize-Android.apk";

    static readonly HttpClient http = BuildGitHubHttpClient();

    static HttpClient BuildGitHubHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static (string versionName, long versionCode) GetInstalledVersion(Context context) {
        try {
            PackageInfo info = context.PackageManager.GetPackageInfo(context.PackageName, 0);
            string name = info?.VersionName ?? "2.1.0";
#pragma warning disable CS0618, CA1422
            long code = OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
#pragma warning restore CS0618, CA1422
            return (name, code);
        } catch {
            return ("2.1.0", 1);
        }
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(Context context, CancellationToken token = default) {
        (string currentVer, _) = GetInstalledVersion(context);

        using HttpRequestMessage req = new(HttpMethod.Get, ReleaseApiUrl);
        using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429) {
            // GitHub unauthenticated REST API rate-limited (60/hr per IP): fall back to zero-rate-limit web redirect.
            UpdateCheckResult webFallback = await CheckForUpdateViaWebRedirectAsync(currentVer, token).ConfigureAwait(false);
            if (webFallback != null) {
                return webFallback;
            }
        }
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string releaseName = root.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "") : "";
        string releaseBody = root.TryGetProperty("body", out JsonElement bEl) ? (bEl.GetString() ?? "") : "";
        string publishedAt = root.TryGetProperty("published_at", out JsonElement pEl) ? (pEl.GetString() ?? "") : "";

        string downloadUrl = "";
        long assetSize = 0;
        if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array) {
            // Pass 1: exact match for Cavernize-Android.apk (release also bundles OmniAgent-Android.apk)
            foreach (JsonElement asset in assetsEl.EnumerateArray()) {
                string assetName = asset.TryGetProperty("name", out JsonElement an) ? (an.GetString() ?? "") : "";
                if (string.Equals(assetName, TargetAssetName, StringComparison.OrdinalIgnoreCase)) {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement du) ? (du.GetString() ?? "") : "";
                    if (asset.TryGetProperty("size", out JsonElement sz)) {
                        assetSize = sz.GetInt64();
                    }
                    break;
                }
            }
            // Pass 2: fallback to any non-OmniAgent APK if Cavernize APK name ever changes
            if (string.IsNullOrEmpty(downloadUrl)) {
                foreach (JsonElement asset in assetsEl.EnumerateArray()) {
                    string assetName = asset.TryGetProperty("name", out JsonElement an) ? (an.GetString() ?? "") : "";
                    if (assetName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
                        !assetName.StartsWith("OmniAgent", StringComparison.OrdinalIgnoreCase)) {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement du) ? (du.GetString() ?? "") : "";
                        if (asset.TryGetProperty("size", out JsonElement sz)) {
                            assetSize = sz.GetInt64();
                        }
                        break;
                    }
                }
            }
        }

        string remoteVer = ExtractVersionString(releaseName) ?? ExtractVersionString(releaseBody) ?? "";
        bool updateAvailable = !string.IsNullOrEmpty(downloadUrl) && IsRemoteNewer(currentVer, remoteVer);

        return new UpdateCheckResult {
            IsUpdateAvailable = updateAvailable,
            CurrentVersion = currentVer,
            RemoteVersion = string.IsNullOrEmpty(remoteVer) ? currentVer : remoteVer,
            DownloadUrl = downloadUrl,
            AssetSizeBytes = assetSize,
            ReleaseNotes = releaseBody,
            PublishedAt = publishedAt
        };
    }

    static async Task<UpdateCheckResult> CheckForUpdateViaWebRedirectAsync(string currentVer, CancellationToken token) {
        try {
            using HttpRequestMessage headReq = new(HttpMethod.Get, LatestWebReleaseUrl);
            headReq.Headers.Accept.Clear();
            headReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using HttpResponseMessage resp = await http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            Match tagMatch = Regex.Match(finalUrl, @"/releases/tag/([^/?#]+)", RegexOptions.IgnoreCase);
            if (!tagMatch.Success) {
                return null;
            }
            string tag = Uri.UnescapeDataString(tagMatch.Groups[1].Value);
            string remoteVer = ExtractVersionString(tag) ?? "";
            if (string.IsNullOrEmpty(remoteVer)) {
                return null;
            }
            string dlUrl = $"https://github.com/guumachadooo/Cavern/releases/download/{Uri.EscapeDataString(tag)}/{TargetAssetName}";
            return new UpdateCheckResult {
                IsUpdateAvailable = IsRemoteNewer(currentVer, remoteVer),
                CurrentVersion = currentVer,
                RemoteVersion = remoteVer,
                DownloadUrl = dlUrl,
                AssetSizeBytes = 0,
                ReleaseNotes = $"Release {tag} (resolved via GitHub web fallback)",
                PublishedAt = ""
            };
        } catch {
            return null;
        }
    }

    public static async Task<string> DownloadUpdateAsync(
        Context context,
        string downloadUrl,
        long expectedBytes,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(downloadUrl)) {
            throw new InvalidOperationException("Download URL not found in GitHub release.");
        }

        string updatesDir = ApkFileProvider.GetUpdatesDirectory(context);
        string finalApk = Path.Combine(updatesDir, TargetAssetName);
        string tempApk = finalApk + ".part";
        if (File.Exists(tempApk)) {
            File.Delete(tempApk);
        }

        using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(10) };
        dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.1"));

        using HttpResponseMessage resp = await dlClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? expectedBytes;
        await using (Stream src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (FileStream dst = File.Create(tempApk)) {
            byte[] buffer = new byte[65536];
            long received = 0;
            long nextUiBytes = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (received >= nextUiBytes || (totalBytes > 0 && received >= totalBytes)) {
                    double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes, 0, 1) : 0;
                    double rxMb = received / (1024.0 * 1024.0);
                    double totMb = totalBytes / (1024.0 * 1024.0);
                    string text = totalBytes > 0
                        ? $"{rxMb:0.0} MB / {totMb:0.0} MB ({frac:0%})"
                        : $"{rxMb:0.0} MB";
                    onProgress?.Invoke(frac, text);
                    nextUiBytes = received + 200_000;
                }
            }
        }

        if (File.Exists(finalApk)) {
            File.Delete(finalApk);
        }
        File.Move(tempApk, finalApk);

        // Best-effort mirror to public Downloads so user also keeps an accessible APK copy
        try {
            AndroidFileHelper.PublishToDownloads(context, finalApk);
        } catch {
            // Internal CacheDir/updates copy is enough for ApkFileProvider installation
        }

        return finalApk;
    }

    public static bool CanInstallPackages(Context context) {
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
            return context.PackageManager.CanRequestPackageInstalls();
        }
        return true;
    }

    public static void OpenUnknownSourcesSettings(Activity activity) {
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
            Intent intent = new(
                Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
        }
    }

    public static void LaunchInstaller(Activity activity, string apkPath) {
        if (activity == null || string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath)) {
            return;
        }
        Android.Net.Uri apkUri = ApkFileProvider.GetUriForFile(activity, apkPath)
            ?? ApkFileProvider.GetApkUri(activity, Path.GetFileName(apkPath));
        Intent install = new(Intent.ActionView);
        install.SetDataAndType(apkUri, "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(install);
    }

    static string ExtractVersionString(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }
        Match m = Regex.Match(text, @"\bv?(\d+\.\d+\.\d+(?:\.\d+)?)\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    static bool IsRemoteNewer(string localVer, string remoteVer) {
        string cleanLocal = ExtractVersionString(localVer);
        string cleanRemote = ExtractVersionString(remoteVer);
        if (string.IsNullOrWhiteSpace(cleanRemote) || string.IsNullOrWhiteSpace(cleanLocal)) {
            return false;
        }
        if (Version.TryParse(cleanLocal, out Version lv) && Version.TryParse(cleanRemote, out Version rv)) {
            return rv > lv;
        }
        return false;
    }
}
