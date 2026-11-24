using System.Collections.Concurrent;
using System.Net;
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
/// and invokes the Android package installer. Persists ETag (<c>If-None-Match</c> / <c>304 Not Modified</c>)
/// and <c>/releases/latest</c> HTML redirect resolutions to keep GitHub 403/429 rates &lt;= 1%.
/// </summary>
public static class AppUpdateService {
    const string ReleaseApiUrl = "https://api.github.com/repos/guumachadooo/Cavern/releases/tags/android-latest";
    const string LatestWebReleaseUrl = "https://github.com/guumachadooo/Cavern/releases/latest";
    const string TargetAssetName = "Cavernize-Android.apk";
    const string CachePrefsName = "cavernize_github_http_cache_v1";
    const long RedirectCacheTtlSeconds = 600;
    const long RateLimitCooldownSeconds = 300;

    sealed record CachedApiEntry(string ETag, string Body, long SavedAtUnixSec);
    sealed record CachedRedirectEntry(string Tag, string DownloadUrl, long SavedAtUnixSec);

    static readonly HttpClient http = BuildGitHubHttpClient();
    static readonly ConcurrentDictionary<string, CachedApiEntry> apiCache = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, CachedRedirectEntry> redirectCache = new(StringComparer.Ordinal);
    static readonly object cacheLock = new();
    static Context cachedAppContext;
    static bool cacheLoaded;
    static long apiCooldownUntilUnixSec;

    static HttpClient BuildGitHubHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    static Context ResolveAppContext(Context context) {
        if (context != null) {
            cachedAppContext = context.ApplicationContext ?? context;
        }
        return cachedAppContext ?? Application.Context;
    }

    static void EnsureCacheLoaded(Context context) {
        Context appCtx = ResolveAppContext(context);
        if (appCtx == null) {
            return;
        }
        lock (cacheLock) {
            if (cacheLoaded) {
                return;
            }
            cacheLoaded = true;
            try {
                ISharedPreferences prefs = appCtx.GetSharedPreferences(CachePrefsName, FileCreationMode.Private);
                if (prefs == null) {
                    return;
                }
                apiCooldownUntilUnixSec = prefs.GetLong("api_cooldown_until", 0L);
                string apiJson = prefs.GetString("api_entries", "") ?? "";
                if (!string.IsNullOrWhiteSpace(apiJson)) {
                    Dictionary<string, CachedApiEntry> loaded = JsonSerializer.Deserialize<Dictionary<string, CachedApiEntry>>(apiJson);
                    if (loaded != null) {
                        foreach (KeyValuePair<string, CachedApiEntry> kv in loaded) {
                            apiCache[kv.Key] = kv.Value;
                        }
                    }
                }
                string redirJson = prefs.GetString("redirect_entries", "") ?? "";
                if (!string.IsNullOrWhiteSpace(redirJson)) {
                    Dictionary<string, CachedRedirectEntry> loaded = JsonSerializer.Deserialize<Dictionary<string, CachedRedirectEntry>>(redirJson);
                    if (loaded != null) {
                        foreach (KeyValuePair<string, CachedRedirectEntry> kv in loaded) {
                            redirectCache[kv.Key] = kv.Value;
                        }
                    }
                }
            } catch {
                // Best effort cache load
            }
        }
    }

    static void PersistCache(Context context) {
        Context appCtx = ResolveAppContext(context);
        if (appCtx == null) {
            return;
        }
        lock (cacheLock) {
            try {
                ISharedPreferences prefs = appCtx.GetSharedPreferences(CachePrefsName, FileCreationMode.Private);
                prefs?.Edit()
                    ?.PutLong("api_cooldown_until", Interlocked.Read(ref apiCooldownUntilUnixSec))
                    ?.PutString("api_entries", JsonSerializer.Serialize(new Dictionary<string, CachedApiEntry>(apiCache)))
                    ?.PutString("redirect_entries", JsonSerializer.Serialize(new Dictionary<string, CachedRedirectEntry>(redirectCache)))
                    ?.Apply();
            } catch {
                // Best effort cache save
            }
        }
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
        EnsureCacheLoaded(context);
        (string currentVer, _) = GetInstalledVersion(context);
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        apiCache.TryGetValue(ReleaseApiUrl, out CachedApiEntry cached);

        if (nowUnix < Interlocked.Read(ref apiCooldownUntilUnixSec)) {
            UpdateCheckResult cooldownFallback = await CheckForUpdateViaWebRedirectAsync(context, currentVer, token).ConfigureAwait(false);
            if (cooldownFallback != null) {
                return cooldownFallback;
            }
            if (cached != null && !string.IsNullOrWhiteSpace(cached.Body)) {
                return ParseReleaseApiJson(currentVer, cached.Body);
            }
        }

        string json = null;
        using HttpRequestMessage req = new(HttpMethod.Get, ReleaseApiUrl);
        if (cached != null && !string.IsNullOrWhiteSpace(cached.ETag)) {
            req.Headers.IfNoneMatch.TryParseAdd(cached.ETag);
        }

        using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified && cached != null && !string.IsNullOrWhiteSpace(cached.Body)) {
            json = cached.Body;
        } else if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429) {
            Interlocked.Exchange(ref apiCooldownUntilUnixSec, nowUnix + RateLimitCooldownSeconds);
            PersistCache(context);
            // GitHub unauthenticated REST API rate-limited (60/hr per IP): fall back to cached web redirect.
            UpdateCheckResult webFallback = await CheckForUpdateViaWebRedirectAsync(context, currentVer, token).ConfigureAwait(false);
            if (webFallback != null) {
                return webFallback;
            }
            if (cached != null && !string.IsNullOrWhiteSpace(cached.Body)) {
                json = cached.Body;
            } else {
                resp.EnsureSuccessStatusCode();
            }
        } else {
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            string etag = resp.Headers.ETag?.ToString() ?? "";
            apiCache[ReleaseApiUrl] = new CachedApiEntry(etag, json, nowUnix);
            PersistCache(context);
        }

        return ParseReleaseApiJson(currentVer, json);
    }

    static UpdateCheckResult ParseReleaseApiJson(string currentVer, string json) {
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

    static async Task<UpdateCheckResult> CheckForUpdateViaWebRedirectAsync(Context context, string currentVer, CancellationToken token) {
        EnsureCacheLoaded(context);
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (redirectCache.TryGetValue(LatestWebReleaseUrl, out CachedRedirectEntry cached) &&
            !string.IsNullOrEmpty(cached.Tag) &&
            !string.IsNullOrEmpty(cached.DownloadUrl) &&
            nowUnix - cached.SavedAtUnixSec < RedirectCacheTtlSeconds) {
            string cachedRemote = ExtractVersionString(cached.Tag) ?? "";
            if (!string.IsNullOrEmpty(cachedRemote)) {
                return new UpdateCheckResult {
                    IsUpdateAvailable = IsRemoteNewer(currentVer, cachedRemote),
                    CurrentVersion = currentVer,
                    RemoteVersion = cachedRemote,
                    DownloadUrl = cached.DownloadUrl,
                    AssetSizeBytes = 0,
                    ReleaseNotes = $"Release {cached.Tag} (cached GitHub web redirect)",
                    PublishedAt = ""
                };
            }
        }

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
            redirectCache[LatestWebReleaseUrl] = new CachedRedirectEntry(tag, dlUrl, nowUnix);
            PersistCache(context);
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
