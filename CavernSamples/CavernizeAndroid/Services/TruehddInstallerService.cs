using System.Diagnostics;

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
/// Resolves the W^X-compliant native ARM64 <c>libtruehdd.so</c> executable bundled inside
/// <see cref="Android.Content.PM.ApplicationInfo.NativeLibraryDir"/>, reads bundled Apache-2.0 notices from APK assets,
/// and removes legacy <c>FilesDir/bin/truehdd</c> copies.
/// </summary>
public static class TruehddInstallerService {
    public const string BundledLibraryFileName = "libtruehdd.so";
    const string BundledVersionAssetPath = "truehdd/version.txt";
    const string BundledLicenseAssetPath = "truehdd/LICENSE";
    const string BundledNoticeAssetPath = "truehdd/NOTICE";

    public static string GetNativeLibraryDir(Context context) =>
        context?.ApplicationInfo?.NativeLibraryDir
        ?? Android.App.Application.Context?.ApplicationInfo?.NativeLibraryDir
        ?? "";

    public static string GetManagedBinDir(Context context) =>
        GetNativeLibraryDir(context);

    public static string GetManagedTruehddPath(Context context) {
        string dir = GetNativeLibraryDir(context);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, BundledLibraryFileName);
    }

    public static string ResolveExistingTruehddPath(Context context, AppSettingsStore store) {
        string bundled = GetManagedTruehddPath(context);
        if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled)) {
            CleanupLegacyWritableBin(context);
            if (store != null && !string.Equals(store.TruehddLocation, bundled, StringComparison.Ordinal)) {
                store.TruehddLocation = bundled;
            }
            return bundled;
        }

        if (store != null && !string.IsNullOrEmpty(store.TruehddLocation)) {
            if (IsAppWritableDataPath(context, store.TruehddLocation)) {
                store.TruehddLocation = "";
                return "";
            }
            if (File.Exists(store.TruehddLocation)) {
                return store.TruehddLocation;
            }
        }
        return "";
    }

    public static void SyncGlobalPath(Context context, AppSettingsStore store) {
        string path = ResolveExistingTruehddPath(context, store);
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
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
        string probedVer = isInstalled
            ? await Task.Run(() => ProbeTruehddVersion(localPath), token).ConfigureAwait(false)
            : "";
        string bundledVer = ReadAssetText(context, BundledVersionAssetPath).Trim();
        string installedVer = !string.IsNullOrWhiteSpace(probedVer)
            ? probedVer
            : (!string.IsNullOrWhiteSpace(bundledVer) ? bundledVer : (store?.InstalledTruehddVersion ?? ""));
        if (isInstalled && store != null && !string.IsNullOrWhiteSpace(installedVer)) {
            store.InstalledTruehddVersion = installedVer;
        }
        long size = 0;
        try {
            if (isInstalled) {
                size = new FileInfo(localPath).Length;
            }
        } catch { }

        return new TruehddStatusResult {
            IsInstalled = isInstalled,
            LocalPath = localPath,
            InstalledVersion = installedVer,
            RemoteVersion = installedVer,
            IsUpdateAvailable = false,
            DownloadUrl = "",
            AssetSizeBytes = size
        };
    }

    public static async Task<string> FetchLicenceTextAsync(Context context, CancellationToken token = default) {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        string lic = ReadAssetText(context, BundledLicenseAssetPath);
        string notice = ReadAssetText(context, BundledNoticeAssetPath);
        if (!string.IsNullOrWhiteSpace(lic)) {
            return string.IsNullOrWhiteSpace(notice) ? lic : $"{lic.TrimEnd()}\n\n--- NOTICE ---\n{notice.Trim()}";
        }

        return "Apache License, Version 2.0 (January 2004)\n" +
               "truehdd / truehd - Copyright 2025-2026 Rainbaby\n" +
               "https://github.com/truehdd/truehdd\n\n" +
               "Licensed under the Apache License, Version 2.0; you may not use this binary except in compliance with the License. " +
               "Full license text is included inside the bundled truehdd APK assets (LICENSE / NOTICE).";
    }

    public static Task<string> DownloadAndInstallAsync(
        Context context,
        AppSettingsStore store,
        TruehddStatusResult status,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        string existing = ResolveExistingTruehddPath(context, store);
        if (!string.IsNullOrEmpty(existing) && File.Exists(existing)) {
            string detectedVer = ProbeTruehddVersion(existing);
            string bundledVer = ReadAssetText(context, BundledVersionAssetPath).Trim();
            string ver = !string.IsNullOrEmpty(detectedVer) ? detectedVer : bundledVer;
            if (store != null) {
                store.TruehddLocation = existing;
                if (!string.IsNullOrEmpty(ver)) {
                    store.InstalledTruehddVersion = ver;
                }
            }
            Truehdd.CustomExecutablePath = existing;
            onProgress?.Invoke(1.0, string.IsNullOrEmpty(ver) ? "truehdd Ready" : $"truehdd v{ver} Ready");
            return Task.FromResult(existing);
        }

        throw new PlatformNotSupportedException(
            "truehdd is bundled inside APK NativeLibraryDir (W^X enforced on Android 10+). Runtime downloads to FilesDir/bin are disabled.");
    }

    public static void EnsureExecutable(string path) {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || IsReadOnlyNativeDirPath(path)) {
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
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                return "";
            }
            EnsureExecutable(path);
            ProcessStartInfo psi = new() {
                FileName = path,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var (exitCode, stdout, _) = FFmpegInstallerService.RunProcessSafe(psi, 3500);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) {
                return "";
            }
            string firstLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            // Example output: "truehdd 0.6.3 (truehd library 0.7.3) Built: ..."
            string[] tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 && tokens[0].Equals("truehdd", StringComparison.OrdinalIgnoreCase)
                ? tokens[1].Trim()
                : firstLine.Trim();
        } catch {
            return "";
        }
    }

    static bool IsReadOnlyNativeDirPath(string path) {
        string norm = path.Replace('\\', '/');
        return norm.StartsWith("/data/app/", StringComparison.Ordinal) ||
               norm.Contains("/lib/arm64", StringComparison.Ordinal) ||
               Path.GetFileName(norm).StartsWith("lib", StringComparison.Ordinal);
    }

    static bool IsAppWritableDataPath(Context context, string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }
        string full = Path.GetFullPath(path);
        string files = context?.FilesDir?.AbsolutePath;
        string cache = context?.CacheDir?.AbsolutePath;
        if (!string.IsNullOrEmpty(files) && full.StartsWith(Path.GetFullPath(files), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        if (!string.IsNullOrEmpty(cache) && full.StartsWith(Path.GetFullPath(cache), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        return full.Replace('\\', '/').Contains("/files/bin/", StringComparison.OrdinalIgnoreCase);
    }

    static void CleanupLegacyWritableBin(Context context) {
        try {
            string filesDir = context?.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(filesDir)) {
                return;
            }
            string binDir = Path.Combine(filesDir, "bin");
            if (!Directory.Exists(binDir)) {
                return;
            }
            foreach (string legacy in new[] { "truehdd", "truehdd.LICENSE", "truehdd.NOTICE" }) {
                string p = Path.Combine(binDir, legacy);
                if (File.Exists(p)) {
                    File.Delete(p);
                }
            }
        } catch {
            // Best-effort legacy cleanup
        }
    }

    static string ReadAssetText(Context context, string assetPath) {
        try {
            var assets = context?.Assets ?? Android.App.Application.Context?.Assets;
            if (assets == null) {
                return "";
            }
            using Stream s = assets.Open(assetPath);
            using StreamReader sr = new(s);
            return sr.ReadToEnd();
        } catch {
            return "";
        }
    }
}
