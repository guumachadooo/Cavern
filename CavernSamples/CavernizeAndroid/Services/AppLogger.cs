using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.App;
using Android.Content;
using Android.OS;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Centralized structured diagnostic logger that writes timestamped, thread- and category-tagged events,
/// startup hardware/RAM/NativeLibs audits, recursive exception chains, FFmpeg/truehdd diagnostics, and
/// crash markers to internal storage and Downloads/Cavernize/Logs/cavernize_log.txt.
/// </summary>
public static class AppLogger {
    const long MaxLogBytes = 2 * 1024 * 1024;
    const int TrimTargetBytes = 1200 * 1024;
    const int CoalesceWindowSeconds = 15;
    const int MaxCoalescedRepeats = 500;
    const int MaxExceptionDepth = 5;
    const int MaxStackFramesPerLevel = 14;
    const int RecentEventsCapacity = 24;

    static readonly object sync = new();
    static readonly HashSet<string> ensuredDirs = new(StringComparer.Ordinal);
    static readonly Queue<string> recentCriticalEvents = new(RecentEventsCapacity + 2);

    static readonly Regex SecretTokenRegex = new(
        @"(?:\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9_]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|(?i)(?:Authorization\s*:\s*(?:Bearer|token)\s+)[^\s""']+|(?i)((?:api[_-]?key|access[_-]?token|pat|token|password|secret)\s*[=:]\s*)[^\s&""';,]+)",
        RegexOptions.Compiled);

    static readonly Regex FfmpegLyricsNoiseRegex = new(
        @"^\s*:\s",
        RegexOptions.Compiled);

    static string internalLogPath;
    static string sharedLogPath;
    static string crashMarkerPath;
    static bool initialized;
    static int mainManagedThreadId = -1;

    static string sessionId = Guid.NewGuid().ToString("N")[..8];
    static DateTime sessionStartUtc = DateTime.UtcNow;
    static int sessionInfoCount;
    static int sessionWarnCount;
    static int sessionErrorCount;
    static int sessionFatalCount;
    static string lastErrorSummary = "";
    static DateTime? lastErrorUtc;

    // Consecutive duplicate suppression state
    static string lastDedupKey;
    static DateTime lastDedupFirstLocal;
    static DateTime lastDedupLastLocal;
    static int lastDedupCount;

    static string cachedAppVersion = "";
    static long cachedAppBuild;

    public static string SessionId => sessionId;
    public static DateTime SessionStartUtc => sessionStartUtc;
    public static int SessionInfoCount => Volatile.Read(ref sessionInfoCount);
    public static int SessionWarnCount => Volatile.Read(ref sessionWarnCount);
    public static int SessionErrorCount => Volatile.Read(ref sessionErrorCount);
    public static int SessionFatalCount => Volatile.Read(ref sessionFatalCount);
    public static string LastErrorSummary {
        get {
            lock (sync) {
                return lastErrorSummary;
            }
        }
    }
    public static DateTime? LastErrorUtc {
        get {
            lock (sync) {
                return lastErrorUtc;
            }
        }
    }

    public static bool HasPendingCrashReport {
        get {
            lock (sync) {
                try {
                    return !string.IsNullOrEmpty(crashMarkerPath) && File.Exists(crashMarkerPath);
                } catch {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Raised on ERROR or FATAL entries (outside logger lock) so optional cloud sync can schedule a throttled upload.
    /// </summary>
    public static event Action<string> CriticalEventLogged;

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
            if (mainManagedThreadId < 0) {
                mainManagedThreadId = System.Environment.CurrentManagedThreadId;
            }

            try {
                string intDir = Path.Combine(context.FilesDir.AbsolutePath, "logs");
                Directory.CreateDirectory(intDir);
                ensuredDirs.Add(intDir);
                internalLogPath = Path.Combine(intDir, "cavernize_log.txt");
                crashMarkerPath = Path.Combine(intDir, "last_crash_marker.txt");
            } catch {
                // Keep best-effort internal log path
            }

            try {
                string extDir = AndroidFileHelper.GetWorkingOutputDir(context, "Logs");
                Directory.CreateDirectory(extDir);
                ensuredDirs.Add(extDir);
                sharedLogPath = Path.Combine(extDir, "cavernize_log.txt");
            } catch {
                sharedLogPath = internalLogPath;
            }

            if (!initialized) {
                initialized = true;
                sessionId = Guid.NewGuid().ToString("N")[..8];
                sessionStartUtc = DateTime.UtcNow;

                AppDomain.CurrentDomain.UnhandledException += (_, e) => {
                    if (e.ExceptionObject is Exception ex) {
                        WriteFatalException("AppDomain.UnhandledException", ex, context);
                    } else {
                        WriteFatalMessage($"AppDomain.UnhandledException: {e.ExceptionObject}", context);
                    }
                };
                TaskScheduler.UnobservedTaskException += (_, e) => {
                    Error("SYS", "TaskScheduler.UnobservedTaskException", e.Exception);
                };
                Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => {
                    WriteFatalException("AndroidEnvironment.UnhandledException", e.Exception, context);
                };

                (string ver, long code) = AppUpdateService.GetInstalledVersion(context);
                cachedAppVersion = ver;
                cachedAppBuild = code;

                string abis = Build.SupportedAbis != null && Build.SupportedAbis.Count > 0
                    ? string.Join(",", Build.SupportedAbis)
                    : "unknown";
                int cores = System.Environment.ProcessorCount;

                // Matches log_digest.py RUN_START_RE: ^[\d{4}-\d{2}-\d{2}...] === Session started ... ===
                WriteSessionBannerLocked(
                    $"Session started [sid={sessionId}] | Cavernize v{ver} (build {code}) | {Build.Manufacturer} {Build.Model} | Android {Build.VERSION.Release} (SDK {(int)Build.VERSION.SdkInt}) | ABI={abis} | CPU={cores}");

                string memLine = BuildMemorySnapshotText(context);
                string nativeLine = BuildNativeLibsAuditText(context);
                WriteRawLocked("INFO", "SYS", $"Startup Env: {memLine} | {nativeLine}", allowCoalesce: false);

                if (HasPendingCrashReport) {
                    string marker = ReadPendingCrashMarkerLocked();
                    WriteRawLocked("WARN", "SYS", $"Previous session ended with fatal crash marker: {marker}", allowCoalesce: false);
                }
                LiveChaosBugHunter.EnsureInitialized();
            }
        }
    }

    public static void LogAudioConfigSnapshot(AppSettingsStore store) {
        if (store == null) {
            return;
        }
        try {
            string upmix = store.CavernizeUpmix
                ? "Cavernize3D"
                : (store.MatrixUpmixing ? "Matrix7.1" : "Direct");
            string profile = string.IsNullOrWhiteSpace(store.BinauralProfileId) ? "default" : store.BinauralProfileId;
            string cloudState = store.CloudLogSyncEnabled
                ? (store.CloudLogBranchMode == 1 ? "master" : "diagnostics")
                : "off";
            Info("CONFIG",
                $"DSP Snapshot: Virt={store.HeadphoneVirtualizer}, Upmix={upmix} (AtmosAware={store.AtmosAwareUpmix}), " +
                $"Binaural='{profile}' (MinPhase={store.MinimumPhaseHrir}, MagLS={store.MagLsModeIndex}), " +
                $"AutoEq={store.AutoEqEnabled}, ISM={store.IsmRoomEnabled}, " +
                $"ProAudio={store.ProAudioEnabled} (Path={store.ProAudioPreferredPathIndex}, LowLat={store.LowLatencyAudio}, UsbNativeSR={store.UsbDacNativeSampleRate}), " +
                $"LGSR={store.LgsrEnabled}, NeuralProv={store.NeuralProviderIndex}, LoudNorm={store.LoudnessNormModeIndex}, ISO226={store.Iso226Enabled}, CloudLog={cloudState}");
        } catch {
            // Never disrupt startup
        }
    }

    public static void LogMemorySnapshot(Context context, string reason) {
        string text = BuildMemorySnapshotText(context);
        string level = text.Contains("lowMem=True", StringComparison.OrdinalIgnoreCase) ? "WARN" : "INFO";
        WriteRaw(level, "MEM", $"{reason}: {text}", allowCoalesce: false);
    }

    public static void Info(string message) => WriteRaw("INFO", InferCategory(message), message);

    public static void Info(string category, string message) => WriteRaw("INFO", NormalizeCategory(category), message);

    public static void Warn(string message) => WriteRaw("WARN", InferCategory(message), message);

    public static void Warn(string category, string message) => WriteRaw("WARN", NormalizeCategory(category), message);

    public static void Error(string context, Exception ex) => Error(InferCategory(context), context, ex);

    public static void Error(string category, string context, Exception ex) {
        string cat = NormalizeCategory(category);
        if (ex == null) {
            WriteRaw("ERROR", cat, context);
            return;
        }
        WriteRaw("ERROR", cat, $"{context}: {FormatExceptionChain(ex)}", allowCoalesce: false);
    }

    public static void Error(string message) => WriteRaw("ERROR", InferCategory(message), message);

    public static void Error(string category, string message) => WriteRaw("ERROR", NormalizeCategory(category), message);

    public static void Metric(string category, string operation, long elapsedMs, string details = null) {
        string tail = string.IsNullOrWhiteSpace(details) ? "" : $" | {details}";
        WriteRaw("METRIC", NormalizeCategory(category), $"{operation} took {elapsedMs} ms{tail}", allowCoalesce: false);
    }

    public static void LogFFmpeg(string executable, string arguments, int exitCode, string stdout, string stderr) {
        StringBuilder sb = new();
        sb.Append($"FFmpeg exit={exitCode} bin=\"{Path.GetFileName(executable ?? "")}\" args={arguments}");
        int cap = exitCode == 0 ? 1800 : 6000;
        string cleanOut = CleanProcessOutput(stdout, exitCode == 0, cap);
        string cleanErr = CleanProcessOutput(stderr, exitCode == 0, cap);
        if (!string.IsNullOrWhiteSpace(cleanOut)) {
            sb.Append($"\n  stdout: {cleanOut}");
        }
        if (!string.IsNullOrWhiteSpace(cleanErr)) {
            sb.Append($"\n  stderr: {cleanErr}");
        }
        WriteRaw(exitCode == 0 ? "FFMPEG" : "ERROR", "FFMPEG", sb.ToString(), allowCoalesce: false);
    }

    public static void LogTruehdd(string executable, string arguments, int exitCode, string stdout, string stderr) {
        StringBuilder sb = new();
        sb.Append($"truehdd exit={exitCode} bin=\"{Path.GetFileName(executable ?? "")}\" args={arguments}");
        int cap = exitCode == 0 ? 1800 : 6000;
        string cleanOut = CleanProcessOutput(stdout, exitCode == 0, cap);
        string cleanErr = CleanProcessOutput(stderr, exitCode == 0, cap);
        if (!string.IsNullOrWhiteSpace(cleanOut)) {
            sb.Append($"\n  stdout: {cleanOut}");
        }
        if (!string.IsNullOrWhiteSpace(cleanErr)) {
            sb.Append($"\n  stderr: {cleanErr}");
        }
        WriteRaw(exitCode == 0 ? "TRUEHDD" : "ERROR", "TRUEHDD", sb.ToString(), allowCoalesce: false);
    }

    public static string SanitizeSensitiveText(string text) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }
        return SecretTokenRegex.Replace(text, match => {
            if (match.Groups[1].Success && !string.IsNullOrEmpty(match.Groups[1].Value)) {
                return match.Groups[1].Value + "[REDACTED]";
            }
            string v = match.Value;
            if (v.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase)) {
                return "Authorization: [REDACTED]";
            }
            return "[REDACTED_TOKEN]";
        });
    }

    public static string ReadPendingCrashMarker() {
        lock (sync) {
            return ReadPendingCrashMarkerLocked();
        }
    }

    public static void ClearPendingCrashMarker() {
        lock (sync) {
            try {
                if (!string.IsNullOrEmpty(crashMarkerPath) && File.Exists(crashMarkerPath)) {
                    File.Delete(crashMarkerPath);
                }
            } catch {
                // Ignore
            }
        }
    }

    public static string ReadAll() {
        lock (sync) {
            FlushCoalescedLocked();
            foreach (string path in new[] { sharedLogPath, internalLogPath }) {
                try {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                        return File.ReadAllText(path, System.Text.Encoding.UTF8);
                    }
                } catch {
                    // Try fallback
                }
            }
            return "(Log vazio / ainda sem registros)";
        }
    }

    /// <summary>
    /// Reads the newest lines from the active log for snappy UI preview without blocking on multi-MB logs.
    /// </summary>
    public static string ReadRecentLines(int maxLines = 450) {
        string tail = ReadTailUtf8(192 * 1024);
        if (string.IsNullOrEmpty(tail)) {
            return "(Log vazio / ainda sem registros)";
        }
        string[] lines = tail.Split('\n');
        if (lines.Length <= maxLines) {
            return tail;
        }
        int start = lines.Length - maxLines;
        StringBuilder sb = new();
        sb.AppendLine($"... (showing last {maxLines} lines; use Share or Cloud Upload for full log) ...");
        for (int i = start; i < lines.Length; i++) {
            sb.AppendLine(lines[i].TrimEnd('\r'));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from the tail of the log cleanly aligned on UTF-8 line boundaries,
    /// with sensitive tokens redacted for safe external sharing or cloud sync.
    /// </summary>
    public static string ReadTailUtf8(int maxBytes = 768 * 1024) {
        lock (sync) {
            FlushCoalescedLocked();
            foreach (string path in new[] { sharedLogPath, internalLogPath }) {
                try {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
                        continue;
                    }
                    FileInfo fi = new(path);
                    if (fi.Length <= maxBytes) {
                        return SanitizeSensitiveText(File.ReadAllText(path, System.Text.Encoding.UTF8));
                    }
                    byte[] buf = new byte[maxBytes];
                    using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(-maxBytes, SeekOrigin.End);
                    int read = fs.Read(buf, 0, maxBytes);
                    string raw = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                    int nl = raw.IndexOf('\n');
                    string aligned = nl >= 0 && nl + 1 < raw.Length ? raw[(nl + 1)..] : raw;
                    string header = $"[TRUNCATED: uploaded newest {maxBytes / 1024} KB of {fi.Length / 1024} KB | sid={sessionId} | v{cachedAppVersion} (build {cachedAppBuild})]{System.Environment.NewLine}";
                    return header + SanitizeSensitiveText(aligned);
                } catch {
                    // Try fallback
                }
            }
            return "";
        }
    }

    /// <summary>
    /// Builds a structured JSON summary snapshot of the current session health, device specs, RAM, and recent errors.
    /// </summary>
    public static string BuildDiagnosticSummaryJson(Context context, AppSettingsStore store = null) {
        string[] recentErrors;
        string lastErr;
        DateTime? lastErrTime;
        lock (sync) {
            FlushCoalescedLocked();
            recentErrors = [.. recentCriticalEvents];
            lastErr = lastErrorSummary;
            lastErrTime = lastErrorUtc;
        }

        (string ver, long buildCode) = context != null
            ? AppUpdateService.GetInstalledVersion(context)
            : (cachedAppVersion, cachedAppBuild);

        var payload = new {
            schemaVersion = 1,
            exportedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            sessionId,
            sessionStartUtc = sessionStartUtc.ToString("O", CultureInfo.InvariantCulture),
            app = new {
                version = ver,
                build = buildCode
            },
            device = new {
                manufacturer = Build.Manufacturer ?? "",
                model = Build.Model ?? "",
                androidRelease = Build.VERSION.Release ?? "",
                sdkInt = (int)Build.VERSION.SdkInt,
                abis = Build.SupportedAbis != null ? string.Join(",", Build.SupportedAbis) : "",
                cpuCores = System.Environment.ProcessorCount
            },
            memory = BuildMemorySnapshotText(context),
            nativeLibs = BuildNativeLibsAuditText(context),
            logSizeBytes = GetLogSizeBytes(),
            counts = new {
                info = SessionInfoCount,
                warn = SessionWarnCount,
                error = SessionErrorCount,
                fatal = SessionFatalCount
            },
            hasPendingCrash = HasPendingCrashReport,
            lastErrorUtc = lastErrTime?.ToString("O", CultureInfo.InvariantCulture) ?? "",
            lastErrorSummary = SanitizeSensitiveText(lastErr),
            dspConfig = store != null ? new {
                virtualizer = store.HeadphoneVirtualizer,
                matrixUpmix = store.MatrixUpmixing,
                cavernizeUpmix = store.CavernizeUpmix,
                binauralProfile = store.BinauralProfileId ?? "",
                autoEq = store.AutoEqEnabled,
                proAudio = store.ProAudioEnabled,
                proAudioPath = store.ProAudioPreferredPathIndex,
                lgsr = store.LgsrEnabled,
                neuralProvider = store.NeuralProviderIndex
            } : null,
            recentCriticalEvents = recentErrors
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Clear(Context context) {
        lock (sync) {
            lastDedupKey = null;
            lastDedupCount = 0;
            Interlocked.Exchange(ref sessionInfoCount, 0);
            Interlocked.Exchange(ref sessionWarnCount, 0);
            Interlocked.Exchange(ref sessionErrorCount, 0);
            Interlocked.Exchange(ref sessionFatalCount, 0);
            lastErrorSummary = "";
            lastErrorUtc = null;
            recentCriticalEvents.Clear();

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
            WriteSessionBannerLocked(
                $"Session started [sid={sessionId}] (Log reset) | Cavernize v{ver} (build {code}) | {Build.Manufacturer} {Build.Model}");
        }
    }

    public static Android.Net.Uri GetShareableLogUri(Context context) {
        lock (sync) {
            FlushCoalescedLocked();
        }
        string path = SharedLogPath;
        return !string.IsNullOrEmpty(path) ? AndroidFileHelper.GetShareableUri(context, path) : null;
    }

    public static Android.Net.Uri PublishLogToDownloads(Context context) {
        lock (sync) {
            FlushCoalescedLocked();
        }
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

    static void WriteFatalException(string source, Exception ex, Context context) {
        string summary = ex != null ? $"{source}: {FormatExceptionChain(ex)}" : source;
        WriteFatalMessage(summary, context);
    }

    static void WriteFatalMessage(string summary, Context context) {
        string mem = BuildMemorySnapshotText(context);
        lock (sync) {
            FlushCoalescedLocked();
            WriteRawLocked("FATAL", "CRASH", $"{summary}\n  {mem}", allowCoalesce: false);
            WriteCrashMarkerLocked(summary);
        }
        NotifyCritical("FATAL", summary);
    }

    static void WriteCrashMarkerLocked(string summary) {
        if (string.IsNullOrEmpty(crashMarkerPath)) {
            return;
        }
        try {
            string oneLine = (summary ?? "Unknown fatal crash").Replace('\r', ' ').Replace('\n', ' ');
            if (oneLine.Length > 260) {
                oneLine = oneLine[..260] + "...";
            }
            string payload = $"{DateTime.UtcNow:O}|sid={sessionId}|v{cachedAppVersion}|{SanitizeSensitiveText(oneLine)}";
            File.WriteAllText(crashMarkerPath, payload, System.Text.Encoding.UTF8);
        } catch {
            // Best effort
        }
    }

    static string ReadPendingCrashMarkerLocked() {
        try {
            if (!string.IsNullOrEmpty(crashMarkerPath) && File.Exists(crashMarkerPath)) {
                return File.ReadAllText(crashMarkerPath, System.Text.Encoding.UTF8).Trim();
            }
        } catch {
            // Ignore
        }
        return "";
    }

    static void WriteRaw(string level, string category, string message, bool allowCoalesce = true) {
        string safeMsg = SanitizeSensitiveText(message ?? "");
        bool isCritical = level == "ERROR" || level == "FATAL";
        lock (sync) {
            WriteRawLocked(level, category, safeMsg, allowCoalesce);
        }
        if (isCritical) {
            NotifyCritical(level, $"[{category}] {safeMsg}");
        }
    }

    static void NotifyCritical(string level, string summary) {
        try {
            CriticalEventLogged?.Invoke($"{level}: {summary}");
        } catch {
            // Never let subscriber failures affect logging
        }
    }

    static void WriteRawLocked(string level, string category, string safeMsg, bool allowCoalesce) {
        switch (level) {
            case "WARN":
                Interlocked.Increment(ref sessionWarnCount);
                break;
            case "ERROR":
                Interlocked.Increment(ref sessionErrorCount);
                RecordCriticalEventLocked(level, category, safeMsg);
                break;
            case "FATAL":
                Interlocked.Increment(ref sessionFatalCount);
                RecordCriticalEventLocked(level, category, safeMsg);
                break;
            default:
                Interlocked.Increment(ref sessionInfoCount);
                break;
        }

        DateTime now = DateTime.Now;
        string dedupKey = allowCoalesce && level != "FATAL" ? $"{level}|{category}|{safeMsg}" : null;

        if (dedupKey != null &&
            string.Equals(dedupKey, lastDedupKey, StringComparison.Ordinal) &&
            (now - lastDedupLastLocal).TotalSeconds <= CoalesceWindowSeconds &&
            lastDedupCount < MaxCoalescedRepeats) {
            lastDedupCount++;
            lastDedupLastLocal = now;
            return;
        }

        FlushCoalescedLocked();

        if (dedupKey != null) {
            lastDedupKey = dedupKey;
            lastDedupFirstLocal = now;
            lastDedupLastLocal = now;
            lastDedupCount = 1;
        } else {
            lastDedupKey = null;
            lastDedupCount = 0;
        }

        string threadTag = GetThreadTag();
        string line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{threadTag}] [{category}] {safeMsg}{System.Environment.NewLine}";
        WriteLineToFilesLocked(line);
    }

    static void WriteSessionBannerLocked(string bannerText) {
        FlushCoalescedLocked();
        Interlocked.Increment(ref sessionInfoCount);
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === {bannerText} ==={System.Environment.NewLine}";
        WriteLineToFilesLocked(line);
    }

    static void FlushCoalescedLocked() {
        if (lastDedupCount > 1) {
            double spanSec = Math.Max(0.0, (lastDedupLastLocal - lastDedupFirstLocal).TotalSeconds);
            string line = $"[{lastDedupLastLocal:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [SYS] (previous message repeated {lastDedupCount}x in {spanSec:0.0}s){System.Environment.NewLine}";
            WriteLineToFilesLocked(line);
        }
        lastDedupKey = null;
        lastDedupCount = 0;
    }

    static void RecordCriticalEventLocked(string level, string category, string safeMsg) {
        string firstLine = safeMsg;
        int nl = firstLine.IndexOf('\n');
        if (nl >= 0) {
            firstLine = firstLine[..nl].Trim();
        }
        if (firstLine.Length > 200) {
            firstLine = firstLine[..200] + "...";
        }
        lastErrorSummary = $"[{category}] {firstLine}";
        lastErrorUtc = DateTime.UtcNow;

        string item = $"[{DateTime.UtcNow:HH:mm:ss}Z] [{level}] [{category}] {firstLine}";
        recentCriticalEvents.Enqueue(item);
        while (recentCriticalEvents.Count > RecentEventsCapacity) {
            recentCriticalEvents.Dequeue();
        }
    }

    static void WriteLineToFilesLocked(string line) {
        AppendSafe(internalLogPath, line);
        if (!string.Equals(sharedLogPath, internalLogPath, StringComparison.Ordinal)) {
            AppendSafe(sharedLogPath, line);
        }
    }

    static void AppendSafe(string path, string line) {
        if (string.IsNullOrEmpty(path)) {
            return;
        }
        try {
            string dir = Path.GetDirectoryName(path) ?? ".";
            if (!ensuredDirs.Contains(dir)) {
                Directory.CreateDirectory(dir);
                ensuredDirs.Add(dir);
            }

            // Atomic trim when log exceeds 2 MB: preserve newest ~1.2 MB aligned to line boundary
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes) {
                byte[] tailBuf = new byte[TrimTargetBytes];
                int read;
                using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    fs.Seek(-TrimTargetBytes, SeekOrigin.End);
                    read = fs.Read(tailBuf, 0, TrimTargetBytes);
                }
                string text = System.Text.Encoding.UTF8.GetString(tailBuf, 0, read);
                int nl = text.IndexOf('\n');
                string trimmed = nl >= 0 && nl + 1 < text.Length ? text[(nl + 1)..] : text;
                string tmpPath = path + ".trim.tmp";
                File.WriteAllText(tmpPath, trimmed, System.Text.Encoding.UTF8);
                File.Move(tmpPath, path, overwrite: true);
            }
            File.AppendAllText(path, line, System.Text.Encoding.UTF8);
        } catch {
            // Avoid secondary failures during logging
        }
    }

    static string GetThreadTag() {
        int tid = System.Environment.CurrentManagedThreadId;
        return tid == mainManagedThreadId ? "UI" : $"T#{tid}";
    }

    static string NormalizeCategory(string category) {
        if (string.IsNullOrWhiteSpace(category)) {
            return "APP";
        }
        string clean = category.Trim().ToUpperInvariant();
        return clean.Length > 12 ? clean[..12] : clean;
    }

    static string InferCategory(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "APP";
        }
        if (text.Contains("ONNX", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("neural", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OrtApi", StringComparison.OrdinalIgnoreCase)) {
            return "ONNX";
        }
        if (text.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase)) {
            return "FFMPEG";
        }
        if (text.Contains("truehdd", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("TrueHD", StringComparison.OrdinalIgnoreCase)) {
            return "TRUEHDD";
        }
        if (text.Contains("AAudio", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AudioTrack", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Pro-Audio", StringComparison.OrdinalIgnoreCase)) {
            return "AUDIO";
        }
        if (text.Contains("UAC2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Scarlett", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("USB", StringComparison.OrdinalIgnoreCase)) {
            return "USB";
        }
        if (text.Contains("LGSR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LosslessTwin", StringComparison.OrdinalIgnoreCase)) {
            return "LGSR";
        }
        if (text.Contains("binaural", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("HRIR", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SOFA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("AutoEq", StringComparison.OrdinalIgnoreCase)) {
            return "BINAURAL";
        }
        if (text.Contains("CloudLog", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GitHub", StringComparison.OrdinalIgnoreCase)) {
            return "CLOUD";
        }
        if (text.Contains("MediaCodec", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Library", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Opened file", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Active track", StringComparison.OrdinalIgnoreCase)) {
            return "MEDIA";
        }
        if (text.StartsWith("UI Error", StringComparison.OrdinalIgnoreCase)) {
            return "UI";
        }
        return "APP";
    }

    static string FormatExceptionChain(Exception root) {
        StringBuilder sb = new();
        FormatExceptionRecursive(sb, root, 0);
        return sb.ToString();
    }

    static void FormatExceptionRecursive(StringBuilder sb, Exception ex, int depth) {
        if (ex == null || depth > MaxExceptionDepth) {
            return;
        }
        if (depth > 0) {
            sb.Append($"\n  ---> [Inner #{depth}] ");
        }
        sb.Append($"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace)) {
            string[] frames = ex.StackTrace.Split('\n');
            int take = Math.Min(frames.Length, MaxStackFramesPerLevel);
            for (int i = 0; i < take; i++) {
                string f = frames[i].TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(f)) {
                    sb.Append('\n').Append(f);
                }
            }
            if (frames.Length > take) {
                sb.Append($"\n  ... (+{frames.Length - take} more frames)");
            }
        }
        if (ex is AggregateException agg) {
            var flat = agg.Flatten().InnerExceptions;
            for (int i = 0; i < Math.Min(flat.Count, 3); i++) {
                FormatExceptionRecursive(sb, flat[i], depth + 1);
            }
        } else if (ex.InnerException != null) {
            FormatExceptionRecursive(sb, ex.InnerException, depth + 1);
        }
    }

    static string CleanProcessOutput(string raw, bool stripMetadataNoise, int maxChars) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return "";
        }
        string cleaned = raw;
        if (stripMetadataNoise) {
            string[] lines = raw.Split('\n');
            StringBuilder filtered = new();
            int skippedLyrics = 0;
            foreach (string ln in lines) {
                string trimmedEnd = ln.TrimEnd('\r');
                if (FfmpegLyricsNoiseRegex.IsMatch(trimmedEnd)) {
                    skippedLyrics++;
                    continue;
                }
                if (skippedLyrics > 0) {
                    filtered.AppendLine($"    ... ({skippedLyrics} metadata/lyrics lines omitted)");
                    skippedLyrics = 0;
                }
                filtered.AppendLine(trimmedEnd);
            }
            if (skippedLyrics > 0) {
                filtered.AppendLine($"    ... ({skippedLyrics} metadata/lyrics lines omitted)");
            }
            cleaned = filtered.ToString().Trim();
        }
        if (cleaned.Length <= maxChars) {
            return cleaned;
        }
        int headLen = maxChars / 2;
        int tailLen = maxChars - headLen;
        return $"{cleaned[..headLen]}\n    ... ({cleaned.Length - maxChars} chars omitted) ...\n{cleaned[^tailLen..]}";
    }

    static string BuildMemorySnapshotText(Context context) {
        try {
            long gcMb = GC.GetTotalMemory(false) / (1024 * 1024);
            if (context?.GetSystemService(Context.ActivityService) is ActivityManager am) {
                ActivityManager.MemoryInfo mi = new();
                am.GetMemoryInfo(mi);
                long availMb = mi.AvailMem / (1024 * 1024);
                long totalMb = mi.TotalMem / (1024 * 1024);
                long threshMb = mi.Threshold / (1024 * 1024);
                return $"RAM avail={availMb}/{totalMb} MB (thresh={threshMb} MB, lowMem={mi.LowMemory}), GC={gcMb} MB";
            }
            return $"GC={gcMb} MB";
        } catch {
            return "RAM=unavailable";
        }
    }

    static string BuildNativeLibsAuditText(Context context) {
        try {
            string natDir = context?.ApplicationInfo?.NativeLibraryDir;
            if (string.IsNullOrEmpty(natDir)) {
                return "NativeLibs=unknown";
            }
            string[] expected = ["libffmpeg.so", "libtruehdd.so", "libonnxruntime.so", "libcavernize_proaudio.so"];
            List<string> items = new(expected.Length);
            foreach (string name in expected) {
                string full = Path.Combine(natDir, name);
                if (File.Exists(full)) {
                    long kb = new FileInfo(full).Length / 1024;
                    items.Add($"{name}:{kb}KB");
                } else {
                    items.Add($"{name}:MISSING");
                }
            }
            return $"NativeLibs[{string.Join(", ", items)}]";
        } catch {
            return "NativeLibs=err";
        }
    }
}
