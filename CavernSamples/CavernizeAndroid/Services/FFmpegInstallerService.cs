using System.Diagnostics;
using System.Text;

using Android.Content;
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
/// Resolves the W^X-compliant native Android NDK FFmpeg binary (<c>libffmpeg.so</c>) bundled inside
/// <see cref="Android.Content.PM.ApplicationInfo.NativeLibraryDir"/>, cleans up legacy <c>FilesDir/bin/</c> copies,
/// configures <c>LD_LIBRARY_PATH</c>, and provides pipe-drained process execution without SIGSYS or SafePipeHandle leaks.
/// </summary>
public static class FFmpegInstallerService {
    public const string BundledLibraryFileName = "libffmpeg.so";
    const string BundledVersionAssetPath = "ffmpeg/version.txt";

    public static string GetNativeLibraryDir(Context context) =>
        context?.ApplicationInfo?.NativeLibraryDir
        ?? Android.App.Application.Context?.ApplicationInfo?.NativeLibraryDir
        ?? "";

    public static string GetManagedFFmpegPath(Context context) {
        string dir = GetNativeLibraryDir(context);
        return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, BundledLibraryFileName);
    }

    public static string GetManagedFFmpegLibDir(Context context) =>
        GetNativeLibraryDir(context);

    public static string ResolveExistingFFmpegPath(Context context, AppSettingsStore store) {
        string bundled = GetManagedFFmpegPath(context);
        if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled)) {
            CleanupLegacyWritableBin(context);
            if (store != null && !string.Equals(store.FFmpegLocation, bundled, StringComparison.Ordinal)) {
                store.FFmpegLocation = bundled;
            }
            return bundled;
        }

        if (store != null && !string.IsNullOrEmpty(store.FFmpegLocation)) {
            if (IsAppWritableDataPath(context, store.FFmpegLocation)) {
                store.FFmpegLocation = "";
                return "";
            }
            if (File.Exists(store.FFmpegLocation)) {
                return store.FFmpegLocation;
            }
        }
        return "";
    }

    public static void ConfigureProcessStartInfo(ProcessStartInfo psi, string executablePath) {
        if (psi == null || string.IsNullOrEmpty(executablePath)) {
            return;
        }
        try {
            string libDir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(libDir) || !Directory.Exists(libDir)) {
                return;
            }
            string existing = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out string cur) ? cur : "";
            psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libDir : $"{libDir}:{existing}";
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

    public static async Task<FFmpegStatusResult> CheckStatusAsync(
        Context context,
        AppSettingsStore store,
        CancellationToken token = default) {
        string localPath = ResolveExistingFFmpegPath(context, store);
        bool isInstalled = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        string liveProbeVer = isInstalled
            ? await Task.Run(() => ProbeFFmpegVersion(localPath), token).ConfigureAwait(false)
            : "";
        bool isWorking = isInstalled && !string.IsNullOrEmpty(liveProbeVer);
        string bundledVer = ReadAssetText(context, BundledVersionAssetPath).Trim();
        string installedVer = isWorking
            ? (!string.IsNullOrWhiteSpace(liveProbeVer) ? liveProbeVer : bundledVer)
            : (isInstalled ? (!string.IsNullOrWhiteSpace(bundledVer) ? bundledVer : "compat-error") : "");
        if (isWorking && store != null && !string.IsNullOrWhiteSpace(liveProbeVer)) {
            store.InstalledFFmpegVersion = liveProbeVer;
        }
        long size = 0;
        try {
            if (isInstalled) {
                size = new FileInfo(localPath).Length;
            }
        } catch { }

        return new FFmpegStatusResult {
            IsInstalled = isInstalled,
            IsWorking = isWorking,
            LocalPath = localPath,
            InstalledVersion = installedVer,
            RemoteVersion = installedVer,
            IsUpdateAvailable = false,
            DownloadUrl = "",
            AssetSizeBytes = size
        };
    }

    public static Task<string> DownloadAndInstallAsync(
        Context context,
        AppSettingsStore store,
        FFmpegStatusResult status,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        string existing = ResolveExistingFFmpegPath(context, store);
        if (!string.IsNullOrEmpty(existing) && File.Exists(existing)) {
            string detectedBanner = ProbeFFmpegVersion(existing);
            string bundledVer = ReadAssetText(context, BundledVersionAssetPath).Trim();
            string ver = !string.IsNullOrEmpty(detectedBanner) ? detectedBanner : bundledVer;
            if (store != null) {
                store.FFmpegLocation = existing;
                if (!string.IsNullOrEmpty(ver)) {
                    store.InstalledFFmpegVersion = ver;
                }
            }
            onProgress?.Invoke(1.0, string.IsNullOrEmpty(ver) ? "FFmpeg NDK Ready" : $"FFmpeg NDK {ver} Ready");
            return Task.FromResult(existing);
        }

        throw new PlatformNotSupportedException(
            "FFmpeg is bundled inside APK NativeLibraryDir (W^X enforced on Android 10+). Runtime downloads to FilesDir/bin are disabled.");
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
            foreach (string legacy in new[] { "ffmpeg", "ffprobe" }) {
                string p = Path.Combine(binDir, legacy);
                if (File.Exists(p)) {
                    File.Delete(p);
                }
            }
            string legacyLibDir = Path.Combine(binDir, "ffmpeg_lib");
            if (Directory.Exists(legacyLibDir)) {
                Directory.Delete(legacyLibDir, recursive: true);
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