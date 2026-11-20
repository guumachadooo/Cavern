using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoidX.WPF.FFmpeg;

/// <summary>
/// FFmpeg runner and locator.
/// </summary>
public abstract class FFmpeg {
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
    /// Full command line arguments of the most recent <see cref="Launch"/> call.
    /// </summary>
    public string LastCommand { get; protected set; } = string.Empty;

    /// <summary>
    /// Exit code of the most recent <see cref="Launch"/> call.
    /// </summary>
    public int LastExitCode { get; protected set; }

    /// <summary>
    /// Captured standard output of the most recent <see cref="Launch"/> call.
    /// </summary>
    public string LastOutput { get; protected set; } = string.Empty;

    /// <summary>
    /// Captured standard error or exception details of the most recent <see cref="Launch"/> call.
    /// </summary>
    public string LastError { get; protected set; } = string.Empty;

    /// <summary>
    /// If the program exists in PATH, its full path is returned.
    /// </summary>
    static string GetPathOfProgram(string key) {
        try {
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "where",
                    Arguments = key,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadLine();
            process.WaitForExit();
            return string.IsNullOrWhiteSpace(output) ? string.Empty : output;
        } catch {
            return string.Empty;
        }
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
            using System.Threading.ManualResetEventSlim outDone = new(false);
            using System.Threading.ManualResetEventSlim errDone = new(false);
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
    /// Arguments that limit the text FFmpeg writes to the console not to flood it in console mode.
    /// </summary>
    const string lesserOutput = " -v error -stats";
}
