using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoidX.WPF.FFmpeg;

/// <summary>
/// FFmpeg runner and locator.
/// </summary>
public abstract class FFmpeg {
    static readonly Regex stderrTimeRegex = new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// In console mode, output less and launch in the same console.
    /// </summary>
    public static bool ConsoleMode { get; set; }

    /// <summary>
    /// Displayed status message when FFmpeg was found.
    /// </summary>
    public static string ReadyText { get; set; } = "Ready.";

    /// <summary>
    /// Displayed status message when FFmpeg was not found.
    /// </summary>
    public static string NotReadyText { get; set; } = "FFmpeg isn't found, codec limitations are applied.";

    /// <summary>
    /// FFmpeg is located and ready to use.
    /// </summary>
    public bool Found { get; private set; }

    /// <summary>
    /// Last user-selected location of FFmpeg.
    /// </summary>
    public string Location {
        get => location;
        set {
            location = value;
            CheckFFmpeg();
        }
    }
    string location;

    /// <summary>
    /// Full command line arguments of the most recent <see cref="Launch"/> or <see cref="LaunchAsync"/> call.
    /// </summary>
    public string LastCommand { get; protected set; } = string.Empty;

    /// <summary>
    /// Exit code of the most recent <see cref="Launch"/> or <see cref="LaunchAsync"/> call.
    /// </summary>
    public int LastExitCode { get; protected set; }

    /// <summary>
    /// Captured standard output of the most recent <see cref="Launch"/> or <see cref="LaunchAsync"/> call.
    /// </summary>
    public string LastOutput { get; protected set; } = string.Empty;

    /// <summary>
    /// Captured standard error or exception details of the most recent <see cref="Launch"/> or <see cref="LaunchAsync"/> call.
    /// </summary>
    public string LastError { get; protected set; } = string.Empty;

    /// <summary>
    /// Resolves an executable path via direct path probing, Windows <c>where</c> / POSIX <c>which</c>,
    /// and <c>PATH</c> directory scanning.
    /// </summary>
    public static string FindExecutable(string key) => GetPathOfProgram(key);

    /// <summary>
    /// If the program exists directly or in PATH, its full path is returned.
    /// </summary>
    static string GetPathOfProgram(string key) {
        if (string.IsNullOrWhiteSpace(key)) {
            return string.Empty;
        }
        string trimmed = key.Trim();
        try {
            if (File.Exists(trimmed)) {
                return Path.GetFullPath(trimmed);
            }
        } catch {
            // Fall through to PATH lookup
        }

        string locator = OperatingSystem.IsWindows() ? "where" : "which";
        try {
            using Process process = new() {
                StartInfo = new ProcessStartInfo {
                    FileName = locator,
                    Arguments = trimmed,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (process.Start()) {
                string output = process.StandardOutput.ReadLine();
                if (!process.WaitForExit(3000)) {
                    TerminateProcessTree(process);
                    return string.Empty;
                }
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) {
                    string candidate = output.Trim();
                    if (File.Exists(candidate)) {
                        return candidate;
                    }
                }
            }
        } catch {
            // Fall through to direct PATH scan when `where`/`which` is absent (e.g., restricted Android/POSIX sandboxes)
        }

        try {
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv)) {
                string[] dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                bool hasExt = Path.HasExtension(trimmed);
                for (int i = 0; i < dirs.Length; i++) {
                    string dir = dirs[i].Trim();
                    if (string.IsNullOrEmpty(dir)) {
                        continue;
                    }
                    string candidate = Path.Combine(dir, trimmed);
                    if (File.Exists(candidate)) {
                        return candidate;
                    }
                    if (OperatingSystem.IsWindows() && !hasExt) {
                        string exeCandidate = candidate + ".exe";
                        if (File.Exists(exeCandidate)) {
                            return exeCandidate;
                        }
                    }
                }
            }
        } catch {
            // Best-effort PATH scan
        }
        return string.Empty;
    }

    /// <summary>
    /// Display the status of FFmpeg.
    /// </summary>
    public abstract void UpdateStatusText(string text);

    /// <summary>
    /// Optional hook to customize the <see cref="ProcessStartInfo"/> before launching FFmpeg (e.g., LD_LIBRARY_PATH on Android).
    /// </summary>
    protected virtual void ConfigureStartInfo(ProcessStartInfo startInfo) { }

    /// <summary>
    /// Optional hook invoked after each FFmpeg invocation completes or fails.
    /// </summary>
    protected virtual void OnCommandCompleted(string executable, string arguments, int exitCode, string stdout, string stderr) { }

    /// <summary>
    /// Checks if FFmpeg's executable is located at the selected directory and update the UI accordingly.
    /// </summary>
    public void CheckFFmpeg() {
        if (string.IsNullOrEmpty(location)) {
            location = GetPathOfProgram("ffmpeg");
        } else if (!File.Exists(location) &&
            location.IndexOf(Path.DirectorySeparatorChar) < 0 &&
            location.IndexOf(Path.AltDirectorySeparatorChar) < 0) {
            string resolved = GetPathOfProgram(location);
            if (!string.IsNullOrEmpty(resolved)) {
                location = resolved;
            }
        }
        Found = !string.IsNullOrEmpty(location) && File.Exists(location);
        UpdateStatusText(Found ? ReadyText : NotReadyText);
    }

    /// <summary>
    /// Launch the FFmpeg to process a file with the given arguments.
    /// </summary>
    public bool Launch(string arguments) {
        string finalArgs = ConsoleMode ? arguments + lesserOutput : arguments;
        LastCommand = finalArgs;
        LastExitCode = -1;
        LastOutput = string.Empty;
        LastError = string.Empty;
        Console.WriteLine("Launching FFmpeg with the following arguments: " + finalArgs);

        if (string.IsNullOrEmpty(Location) || !File.Exists(Location)) {
            LastError = $"FFmpeg executable not found at '{Location}'.";
            OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
            return false;
        }

        ProcessStartInfo start = new() {
            Arguments = finalArgs,
            FileName = Location,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureStartInfo(start);
        try {
            using Process proc = Process.Start(start);
            if (proc == null) {
                LastError = "Process.Start returned null.";
                OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
                return false;
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
            proc.WaitForExit();
            outDone.Wait(2000);
            errDone.Wait(2000);
            CancelOutputReads(proc);

            LastExitCode = proc.ExitCode;
            LastOutput = outSb.ToString().Trim();
            LastError = errSb.ToString().Trim();
            OnCommandCompleted(Location, finalArgs, LastExitCode, LastOutput, LastError);
            return LastExitCode == 0;
        } catch (Exception ex) {
            LastExitCode = -1;
            LastError = ex.ToString();
            OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
            return false;
        }
    }

    /// <summary>
    /// Asynchronously launches FFmpeg with structured <c>-progress pipe:1</c> parsing (<c>out_time_us</c>,
    /// <c>out_time_ms</c>, <c>out_time</c>, <c>progress=end</c>) and guaranteed process-tree teardown on cancellation.
    /// </summary>
    public async Task<bool> LaunchAsync(
        string arguments,
        CancellationToken cancellationToken = default,
        IProgress<double> progress = null,
        double totalDurationSeconds = 0) {
        cancellationToken.ThrowIfCancellationRequested();

        string safeArgs = arguments ?? string.Empty;
        string finalArgs = safeArgs;
        if (progress != null && safeArgs.IndexOf("-progress", StringComparison.OrdinalIgnoreCase) < 0) {
            finalArgs = string.IsNullOrWhiteSpace(safeArgs)
                ? "-progress pipe:1"
                : "-progress pipe:1 " + safeArgs;
        }
        if (ConsoleMode) {
            finalArgs += lesserOutput;
        }

        LastCommand = finalArgs;
        LastExitCode = -1;
        LastOutput = string.Empty;
        LastError = string.Empty;
        Console.WriteLine("Launching FFmpeg with the following arguments: " + finalArgs);

        if (string.IsNullOrEmpty(Location) || !File.Exists(Location)) {
            LastError = $"FFmpeg executable not found at '{Location}'.";
            OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
            return false;
        }

        ProcessStartInfo start = new() {
            Arguments = finalArgs,
            FileName = Location,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureStartInfo(start);

        Process proc = null;
        try {
            proc = Process.Start(start);
            if (proc == null) {
                LastError = "Process.Start returned null.";
                OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
                return false;
            }

            StringBuilder outSb = new();
            StringBuilder errSb = new();
            TaskCompletionSource<bool> outDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> errDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

            bool sawOutTimeUs = false;
            bool reportedEnd = false;
            double maxProgress = 0.0;
            object progressGate = new();

            void ReportNormalized(double value, bool isEnd = false) {
                if (progress == null) {
                    return;
                }
                lock (progressGate) {
                    if (isEnd) {
                        if (!reportedEnd) {
                            reportedEnd = true;
                            maxProgress = 1.0;
                            progress.Report(1.0);
                        }
                        return;
                    }
                    double clamped = Math.Clamp(value, 0.0, 0.999);
                    if (clamped > maxProgress) {
                        maxProgress = clamped;
                        progress.Report(clamped);
                    }
                }
            }

            proc.OutputDataReceived += (_, e) => {
                if (e.Data == null) {
                    outDone.TrySetResult(true);
                    return;
                }
                lock (outSb) {
                    outSb.AppendLine(e.Data);
                }
                if (progress != null) {
                    ParseProgressLine(e.Data, totalDurationSeconds, ref sawOutTimeUs, ReportNormalized);
                }
            };

            proc.ErrorDataReceived += (_, e) => {
                if (e.Data == null) {
                    errDone.TrySetResult(true);
                    return;
                }
                lock (errSb) {
                    errSb.AppendLine(e.Data);
                }
                if (progress != null && totalDurationSeconds > 0.0 && !Volatile.Read(ref sawOutTimeUs)) {
                    Match m = stderrTimeRegex.Match(e.Data);
                    if (m.Success &&
                        int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
                        int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min) &&
                        double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) {
                        double elapsed = h * 3600.0 + min * 60.0 + sec;
                        ReportNormalized(elapsed / totalDurationSeconds);
                    }
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (CancellationTokenRegistration cancelReg = cancellationToken.Register(() => TerminateProcessTree(proc))) {
                if (cancellationToken.IsCancellationRequested) {
                    TerminateProcessTree(proc);
                }
                await Task.WhenAny(proc.WaitForExitAsync(CancellationToken.None), WaitUntilCancelledAsync(proc, cancellationToken)).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) {
                    TerminateProcessTree(proc);
                    await Task.WhenAny(proc.WaitForExitAsync(CancellationToken.None), Task.Delay(2000)).ConfigureAwait(false);
                } else {
                    await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(2000)).ConfigureAwait(false);
            CancelOutputReads(proc);

            lock (outSb) {
                LastOutput = outSb.ToString().Trim();
            }
            lock (errSb) {
                LastError = errSb.ToString().Trim();
            }

            if (cancellationToken.IsCancellationRequested) {
                LastExitCode = -1;
                if (string.IsNullOrEmpty(LastError)) {
                    LastError = "Operation canceled.";
                }
                OnCommandCompleted(Location, finalArgs, -1, LastOutput, LastError);
                cancellationToken.ThrowIfCancellationRequested();
            }

            LastExitCode = proc.HasExited ? proc.ExitCode : -1;
            if (LastExitCode == 0 && progress != null) {
                ReportNormalized(1.0, isEnd: true);
            }
            OnCommandCompleted(Location, finalArgs, LastExitCode, LastOutput, LastError);
            return LastExitCode == 0;
        } catch (OperationCanceledException) {
            TerminateProcessTree(proc);
            throw;
        } catch (Exception ex) {
            TerminateProcessTree(proc);
            LastExitCode = -1;
            LastError = ex.ToString();
            OnCommandCompleted(Location, finalArgs, -1, string.Empty, LastError);
            return false;
        } finally {
            if (proc != null) {
                if (cancellationToken.IsCancellationRequested) {
                    TerminateProcessTree(proc);
                }
                proc.Dispose();
            }
        }
    }

    static async Task WaitUntilCancelledAsync(Process proc, CancellationToken cancellationToken) {
        if (!cancellationToken.CanBeCanceled) {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration reg = cancellationToken.Register(() => tcs.TrySetResult(true));
        if (cancellationToken.IsCancellationRequested) {
            tcs.TrySetResult(true);
        }
        await tcs.Task.ConfigureAwait(false);
    }

    static void ParseProgressLine(
        string line,
        double totalDurationSeconds,
        ref bool sawOutTimeUs,
        Action<double, bool> report) {
        int eq = line.IndexOf('=');
        if (eq <= 0) {
            return;
        }
        ReadOnlySpan<char> key = line.AsSpan(0, eq).Trim();
        ReadOnlySpan<char> val = line.AsSpan(eq + 1).Trim();

        if (key.Equals("out_time_us", StringComparison.Ordinal)) {
            if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0) {
                Volatile.Write(ref sawOutTimeUs, true);
                if (totalDurationSeconds > 0.0) {
                    report((us / 1_000_000.0) / totalDurationSeconds, false);
                }
            }
        } else if (key.Equals("out_time_ms", StringComparison.Ordinal)) {
            if (!Volatile.Read(ref sawOutTimeUs) &&
                long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long msUs) &&
                msUs >= 0 &&
                totalDurationSeconds > 0.0) {
                report((msUs / 1_000_000.0) / totalDurationSeconds, false);
            }
        } else if (key.Equals("out_time", StringComparison.Ordinal)) {
            if (TimeSpan.TryParse(val, CultureInfo.InvariantCulture, out TimeSpan ts) && ts.TotalSeconds >= 0.0) {
                Volatile.Write(ref sawOutTimeUs, true);
                if (totalDurationSeconds > 0.0) {
                    report(ts.TotalSeconds / totalDurationSeconds, false);
                }
            }
        } else if (key.Equals("progress", StringComparison.Ordinal) &&
            val.Equals("end", StringComparison.OrdinalIgnoreCase)) {
            report(1.0, true);
        }
    }

    static void TerminateProcessTree(Process proc) {
        if (proc == null) {
            return;
        }
        try {
            if (!proc.HasExited) {
                proc.Kill(entireProcessTree: true);
            }
        } catch {
            // Process may have already exited or been disposed
        }
    }

    static void CancelOutputReads(Process proc) {
        try {
            proc.CancelOutputRead();
        } catch {
            // Ignore when already completed
        }
        try {
            proc.CancelErrorRead();
        } catch {
            // Ignore when already completed
        }
    }

    /// <summary>
    /// Arguments that limit the text FFmpeg writes to the console not to flood it in console mode.
    /// </summary>
    const string lesserOutput = " -v error -stats";
}
