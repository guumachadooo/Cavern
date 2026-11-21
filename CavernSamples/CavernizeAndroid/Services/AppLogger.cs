using System.Text;

using Android.Content;
using Android.OS;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Centralized diagnostic logger that writes timestamped events, FFmpeg commands/errors, and exceptions
/// both internally and to Downloads/Cavernize/Logs/cavernize_log.txt so errors can be inspected or shared easily.
/// </summary>
public static class AppLogger {
    static readonly object sync = new();
    static string internalLogPath;
    static string sharedLogPath;
    static bool initialized;

    public static string SharedLogPath {
        get {
            if (!string.IsNullOrEmpty(sharedLogPath) && File.Exists(sharedLogPath)) {
                return sharedLogPath;
            }
            if (!string.IsNullOrEmpty(internalLogPath) && File.Exists(internalLogPath)) {
                return internalLogPath;
            }
            return sharedLogPath ?? internalLogPath ?? "";
        }
    }

    public static void Initialize(Context context) {
        lock (sync) {
            try {
                string intDir = Path.Combine(context.FilesDir.AbsolutePath, "logs");
                Directory.CreateDirectory(intDir);
                internalLogPath = Path.Combine(intDir, "cavernize_log.txt");
            } catch {
                // Keep best-effort internal log path
            }

            try {
                string extDir = AndroidFileHelper.GetWorkingOutputDir(context, "Logs");
                Directory.CreateDirectory(extDir);
                sharedLogPath = Path.Combine(extDir, "cavernize_log.txt");
            } catch {
                sharedLogPath = internalLogPath;
            }

            if (!initialized) {
                initialized = true;
                AppDomain.CurrentDomain.UnhandledException += (_, e) => {
                    if (e.ExceptionObject is Exception ex) {
                        Error("AppDomain.UnhandledException", ex);
                    } else {
                        WriteRaw("FATAL", $"UnhandledException: {e.ExceptionObject}");
                    }
                };
                TaskScheduler.UnobservedTaskException += (_, e) => {
                    Error("TaskScheduler.UnobservedTaskException", e.Exception);
                };
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => {
                    Error("AndroidEnvironment.UnhandledException", e.Exception);
                };
            }

            (string ver, long code) = AppUpdateService.GetInstalledVersion(context);
            WriteRaw("INFO", $"=== Cavernize v{ver} (build {code}) started | {Build.Manufacturer} {Build.Model} | Android {Build.VERSION.Release} (SDK {(int)Build.VERSION.SdkInt}) ===");
        }
    }

    public static void Info(string message) => WriteRaw("INFO", message);

    public static void Warn(string message) => WriteRaw("WARN", message);

    public static void Error(string context, Exception ex) {
        if (ex == null) {
            WriteRaw("ERROR", context);
            return;
        }
        WriteRaw("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    public static void Error(string message) => WriteRaw("ERROR", message);

    public static void LogFFmpeg(string executable, string arguments, int exitCode, string stdout, string stderr) {
        StringBuilder sb = new();
        sb.Append($"FFmpeg exit={exitCode} bin=\"{executable}\" args={arguments}");
        if (!string.IsNullOrWhiteSpace(stdout)) {
            sb.Append($"\n  stdout: {stdout}");
        }
        if (!string.IsNullOrWhiteSpace(stderr)) {
            sb.Append($"\n  stderr: {stderr}");
        }
        WriteRaw(exitCode == 0 ? "FFMPEG" : "ERROR", sb.ToString());
    }

    public static void LogTruehdd(string executable, string arguments, int exitCode, string stdout, string stderr) {
        StringBuilder sb = new();
        sb.Append($"truehdd exit={exitCode} bin=\"{executable}\" args={arguments}");
        if (!string.IsNullOrWhiteSpace(stdout)) {
            sb.Append($"\n  stdout: {stdout}");
        }
        if (!string.IsNullOrWhiteSpace(stderr)) {
            sb.Append($"\n  stderr: {stderr}");
        }
        WriteRaw(exitCode == 0 ? "TRUEHDD" : "ERROR", sb.ToString());
    }

    public static string ReadAll() {
        lock (sync) {
            foreach (string path in new[] { sharedLogPath, internalLogPath }) {
                try {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                        return File.ReadAllText(path);
                    }
                } catch {
                    // Try fallback
                }
            }
            return "(Log vazio / ainda sem registros)";
        }
    }

    public static void Clear(Context context) {
        lock (sync) {
            foreach (string path in new[] { internalLogPath, sharedLogPath }) {
                try {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                        File.Delete(path);
                    }
                } catch {
                    // Ignore
                }
            }
            (string ver, long code) = AppUpdateService.GetInstalledVersion(context);
            WriteRaw("INFO", $"=== Log reset | Cavernize v{ver} (build {code}) | {Build.Manufacturer} {Build.Model} ===");
        }
    }

    public static Android.Net.Uri GetShareableLogUri(Context context) {
        string path = SharedLogPath;
        return !string.IsNullOrEmpty(path) ? AndroidFileHelper.GetShareableUri(context, path) : null;
    }

    public static Android.Net.Uri PublishLogToDownloads(Context context) {
        string path = SharedLogPath;
        return !string.IsNullOrEmpty(path) ? AndroidFileHelper.PublishToDownloads(context, path, "Logs") : null;
    }

    public static long GetLogSizeBytes() {
        lock (sync) {
            string p = SharedLogPath;
            try {
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? new FileInfo(p).Length : 0;
            } catch {
                return 0;
            }
        }
    }

    static void WriteRaw(string level, string message) {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{System.Environment.NewLine}";
        lock (sync) {
            AppendSafe(internalLogPath, line);
            if (!string.Equals(sharedLogPath, internalLogPath, StringComparison.Ordinal)) {
                AppendSafe(sharedLogPath, line);
            }
        }
    }

    static void AppendSafe(string path, string line) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            // Trim overly large logs (> 2 MB) to keep the latest half
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) {
                string tail = File.ReadAllText(path);
                int cut = tail.Length / 2;
                int nl = tail.IndexOf('\n', cut);
                File.WriteAllText(path, nl > 0 ? tail[(nl + 1)..] : tail[cut..]);
            }
            File.AppendAllText(path, line, Encoding.UTF8);
        } catch {
            // Avoid secondary failures during logging
        }
    }
}
