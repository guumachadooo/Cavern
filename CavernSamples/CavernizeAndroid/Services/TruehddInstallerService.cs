using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

using Android.Content;
using Android.OS;
using Android.Systems;

using Cavernize.Logic.External;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class TruehddStatusResult {
    public bool IsInstalled { get; init; }
    public string LocalPath { get; init; } = "";
    public string InstalledVersion { get; init; } = "";
    public string RemoteVersion { get; init; } = "";
    public bool IsUpdateAvailable { get; init; }
    public string DownloadUrl { get; init; } = "";
    public long AssetSizeBytes { get; init; }
}

/// <summary>
/// Automatically checks GitHub for official static musl ARM64/x64 truehdd builds,
/// downloads and extracts them into FilesDir/bin/truehdd with progress, sets executable permissions, and checks updates.
/// </summary>
public static class TruehddInstallerService {
    const string LatestReleaseApiUrl = "https://api.github.com/repos/truehdd/truehdd/releases/latest";
    const string LatestReleaseHtmlUrl = "https://github.com/truehdd/truehdd/releases/latest";
    const string LicenceRawUrl = "https://raw.githubusercontent.com/truehdd/truehdd/main/LICENSE";

    static readonly HttpClient http = CreateHttpClient();

    static HttpClient CreateHttpClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.5"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static string GetManagedBinDir(Context context) {
        string dir = Path.Combine(context.FilesDir.AbsolutePath, "bin");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetManagedTruehddPath(Context context) =>
        Path.Combine(GetManagedBinDir(context), "truehdd");

    public static string ResolveExistingTruehddPath(Context context, AppSettingsStore store) {
        if (!string.IsNullOrEmpty(store.TruehddLocation) && File.Exists(store.TruehddLocation)) {
            return store.TruehddLocation;
        }
        string managed = GetManagedTruehddPath(context);
        if (File.Exists(managed)) {
            store.TruehddLocation = managed;
            return managed;
        }
        return "";
    }

    public static void SyncGlobalPath(Context context, AppSettingsStore store) {
        string path = ResolveExistingTruehddPath(context, store);
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
            EnsureExecutable(path);
            Truehdd.CustomExecutablePath = path;
        }
    }

    public static string GetTargetAssetSuffix() {
        bool isArm64 = true;
        IList<string> abis = Build.SupportedAbis;
        if (abis != null && abis.Count > 0) {
            isArm64 = abis.Any(a => a.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                                    a.Contains("aarch64", StringComparison.OrdinalIgnoreCase));
        }
        return isArm64 ? "aarch64-unknown-linux-musl.tar.gz" : "x86_64-unknown-linux-musl.tar.gz";
    }

    public static async Task<TruehddStatusResult> CheckStatusAsync(
        Context context,
        AppSettingsStore store,
        CancellationToken token = default) {
        string localPath = ResolveExistingTruehddPath(context, store);
        bool isInstalled = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        string installedVer = store.InstalledTruehddVersion ?? "";
        if (isInstalled && string.IsNullOrWhiteSpace(installedVer)) {
            installedVer = ProbeTruehddVersion(localPath);
            if (!string.IsNullOrWhiteSpace(installedVer)) {
                store.InstalledTruehddVersion = installedVer;
            }
        }

        string targetSuffix = GetTargetAssetSuffix();
        string remoteTag = "";
        string dlUrl = "";
        long size = 0;

        try {
            using HttpRequestMessage req = new(HttpMethod.Get, LatestReleaseApiUrl);
            using HttpResponseMessage resp = await http.SendAsync(req, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            remoteTag = root.TryGetProperty("tag_name", out JsonElement tEl) ? (tEl.GetString() ?? "") : "";
            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement asset in assetsEl.EnumerateArray()) {
                    string name = asset.TryGetProperty("name", out JsonElement nEl) ? (nEl.GetString() ?? "") : "";
                    if (name.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) {
                        dlUrl = asset.TryGetProperty("browser_download_url", out JsonElement uEl) ? (uEl.GetString() ?? "") : "";
                        if (asset.TryGetProperty("size", out JsonElement sEl)) {
                            size = sEl.GetInt64();
                        }
                        break;
                    }
                }
            }
        } catch {
            // Fallback to GitHub HTML release redirect if API rate-limited
            try {
                (remoteTag, dlUrl) = await FallbackResolveFromHtmlAsync(targetSuffix, token).ConfigureAwait(false);
            } catch {
                // Propagate empty remote if offline when checking silently
                if (!isInstalled) {
                    throw;
                }
            }
        }

        bool isManaged = string.Equals(localPath, GetManagedTruehddPath(context), StringComparison.Ordinal);
        bool updateAvailable = isInstalled &&
            !string.IsNullOrEmpty(remoteTag) &&
            !string.IsNullOrEmpty(dlUrl) &&
            (isManaged || !string.IsNullOrEmpty(installedVer)) &&
            !string.Equals(NormalizeVersion(installedVer), NormalizeVersion(remoteTag), StringComparison.OrdinalIgnoreCase);

        return new TruehddStatusResult {
            IsInstalled = isInstalled,
            LocalPath = localPath,
            InstalledVersion = installedVer,
            RemoteVersion = remoteTag,
            IsUpdateAvailable = updateAvailable,
            DownloadUrl = dlUrl,
            AssetSizeBytes = size
        };
    }

    public static async Task<string> FetchLicenceTextAsync(Context context, CancellationToken token = default) {
        string localLic = Path.Combine(GetManagedBinDir(context), "truehdd.LICENSE");
        if (File.Exists(localLic)) {
            try {
                string txt = await File.ReadAllTextAsync(localLic, token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(txt)) {
                    return txt;
                }
            } catch {
                // Fall through to remote fetch
            }
        }

        try {
            using HttpClient licClient = new() { Timeout = TimeSpan.FromSeconds(8) };
            licClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.5"));
            string remote = await licClient.GetStringAsync(LicenceRawUrl, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(remote)) {
                return remote;
            }
        } catch {
            // Fallback to built-in Apache 2.0 notice
        }

        return "Apache License, Version 2.0 (January 2004)\n" +
               "truehdd / truehd - Copyright 2025-2026 Rainbaby\n" +
               "https://github.com/truehdd/truehdd\n\n" +
               "Licensed under the Apache License, Version 2.0; you may not use this binary except in compliance with the License. " +
               "Full license text is included inside the official truehdd release package (LICENSE / NOTICE).";
    }

    public static async Task<string> DownloadAndInstallAsync(
        Context context,
        AppSettingsStore store,
        TruehddStatusResult status,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (status == null || string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            status = await CheckStatusAsync(context, store, token).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(status.DownloadUrl)) {
            throw new InvalidOperationException("No compatible Android truehdd musl binary URL found in GitHub release.");
        }

        string binDir = GetManagedBinDir(context);
        string destPath = GetManagedTruehddPath(context);
        string archiveTemp = destPath + ".tar.gz.part";
        string extractDir = destPath + "_extract";
        if (File.Exists(archiveTemp)) File.Delete(archiveTemp);
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

        try {
            using HttpClient dlClient = new() { Timeout = TimeSpan.FromMinutes(5) };
            dlClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.5"));

            using HttpResponseMessage resp = await dlClient.GetAsync(status.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? status.AssetSizeBytes;
            await using (Stream netStream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (FileStream tarOut = File.Create(archiveTemp)) {
                byte[] buf = new byte[65536];
                long received = 0;
                long nextUi = 0;
                int read;
                while ((read = await netStream.ReadAsync(buf, token).ConfigureAwait(false)) > 0) {
                    await tarOut.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received >= nextUi || (totalBytes > 0 && received >= totalBytes)) {
                        double frac = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes * 0.85, 0, 0.85) : 0.35;
                        double rxMb = received / (1024.0 * 1024.0);
                        double totMb = totalBytes / (1024.0 * 1024.0);
                        string msg = totalBytes > 0
                            ? $"truehdd: {rxMb:0.0} MB / {totMb:0.0} MB ({frac / 0.85:0%})"
                            : $"truehdd: {rxMb:0.0} MB";
                        onProgress?.Invoke(frac, msg);
                        nextUi = received + 96_000;
                    }
                }
            }

            onProgress?.Invoke(0.90, "truehdd: Extraindo binário musl...");
            Directory.CreateDirectory(extractDir);
            await using (FileStream tarIn = File.OpenRead(archiveTemp))
            await using (GZipStream gz = new(tarIn, CompressionMode.Decompress)) {
                await TarFile.ExtractToDirectoryAsync(gz, extractDir, overwriteFiles: true, token).ConfigureAwait(false);
            }

            string extractedBin = Directory.GetFiles(extractDir, "truehdd", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(extractedBin) || !File.Exists(extractedBin)) {
                throw new FileNotFoundException("Extracted truehdd tarball did not contain the 'truehdd' executable.");
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(extractedBin, destPath);

            // Preserve Apache-2.0 LICENSE and NOTICE files alongside binary
            string extractedLic = Directory.GetFiles(extractDir, "LICENSE", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(extractedLic) && File.Exists(extractedLic)) {
                File.Copy(extractedLic, Path.Combine(binDir, "truehdd.LICENSE"), overwrite: true);
            }
            string extractedNotice = Directory.GetFiles(extractDir, "NOTICE", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(extractedNotice) && File.Exists(extractedNotice)) {
                File.Copy(extractedNotice, Path.Combine(binDir, "truehdd.NOTICE"), overwrite: true);
            }
        } finally {
            try { if (File.Exists(archiveTemp)) File.Delete(archiveTemp); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
        }

        EnsureExecutable(destPath);
        string detectedVer = ProbeTruehddVersion(destPath);

        store.TruehddLocation = destPath;
        store.InstalledTruehddVersion = !string.IsNullOrEmpty(status.RemoteVersion) ? status.RemoteVersion : detectedVer;
        Truehdd.CustomExecutablePath = destPath;
        onProgress?.Invoke(1.0, string.IsNullOrEmpty(detectedVer) ? "truehdd Ready" : $"truehdd v{detectedVer} Ready");
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

    public static string ProbeTruehddVersion(string path) {
        try {
            EnsureExecutable(path);
            using System.Diagnostics.Process proc = new();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo {
                FileName = path,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            string firstLine = proc.StandardOutput.ReadLine() ?? "";
            proc.WaitForExit(3000);
            // Example output: "truehdd 0.6.3 (truehd library 0.7.3) Built: ..."
            string[] tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 && tokens[0].Equals("truehdd", StringComparison.OrdinalIgnoreCase)
                ? tokens[1].Trim()
                : firstLine.Trim();
        } catch {
            return "";
        }
    }

    static async Task<(string tag, string url)> FallbackResolveFromHtmlAsync(string targetSuffix, CancellationToken token) {
        using HttpClient web = new() { Timeout = TimeSpan.FromSeconds(15) };
        web.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid", "2.5"));
        string html = await web.GetStringAsync(LatestReleaseHtmlUrl, token).ConfigureAwait(false);
        const string marker = "truehdd/truehdd/releases/tag/";
        int idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) {
            throw new InvalidOperationException("Unable to locate latest truehdd release tag.");
        }
        int start = idx + marker.Length;
        int end = html.IndexOf('"', start);
        string tag = html[start..end].Trim();
        string expanded = await web.GetStringAsync($"https://github.com/truehdd/truehdd/releases/expanded_assets/{tag}", token).ConfigureAwait(false);
        string pattern = targetSuffix + "\"";
        int assetIdx = expanded.IndexOf(pattern, StringComparison.Ordinal);
        if (assetIdx < 0) {
            throw new InvalidOperationException($"Asset '{targetSuffix}' not found in truehdd release '{tag}'.");
        }
        int linkStart = expanded.LastIndexOf('"', assetIdx) + 1;
        int linkEnd = assetIdx + targetSuffix.Length;
        string relPath = expanded[linkStart..linkEnd];
        if (!relPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)) {
            relPath = "https://github.com" + relPath;
        }
        return (tag, relPath);
    }

    static string NormalizeVersion(string ver) =>
        (ver ?? "").Trim().TrimStart('v', 'V');
}
