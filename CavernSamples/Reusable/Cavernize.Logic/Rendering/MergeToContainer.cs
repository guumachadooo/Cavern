using Cavern.Format.Common;
using VoidX.WPF.FFmpeg;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// When a WAV result is available, use <see cref="FFmpeg"/> to merge it into a container format.
/// This runs in cases where an unsupported codec is set for export with a .mkv path or native compressed audio container.
/// </summary>
public sealed class MergeToContainer {
    readonly string sourceFile;
    readonly string render;
    readonly string outputCodec;
    readonly List<FFmpegArgument> extraArgs = [];
    string trackName;
    bool allow8PlusChannels;
    bool audioOnlyMode;

    /// <summary>
    /// Contains the setup for performing the merge with <see cref="FFmpeg"/>.
    /// </summary>
    FFmpegConfigurator args;

    /// <summary>
    /// Last error description if a merge or fallback conversion fails.
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// When a WAV result is available, use <see cref="FFmpeg"/> to merge it into a container format.
    /// </summary>
    public MergeToContainer(string sourceFile, string render, string outputCodec) {
        this.sourceFile = sourceFile;
        this.render = render;
        this.outputCodec = outputCodec;
    }

    /// <summary>
    /// Get file browser filters based on what output containers are supported for a given input and output codec.
    /// </summary>
    public static string GetPossibleContainers(CavernizeTrack input, Codec output, FFmpeg ffmpeg) {
        const string matroskaOnly = "Matroska|*.mkv";
        string native = output.GetSaveDialogFilter();
        if (output.IsEnvironmental()) {
            return output.GetSaveDialogFilter() ?? throw new NotImplementedException();
        } else if (output == Codec.PCM_Float || output == Codec.PCM_LE) {
            return ffmpeg.Found || input.Container == Container.Matroska ? $"{matroskaOnly}|{native}" : native;
        } else if (output == Codec.FLAC) {
            return ffmpeg.Found ? $"{native}|{matroskaOnly}" : native;
        } else if (ffmpeg.Found) {
            return native == null ? matroskaOnly : $"{matroskaOnly}|{native}";
        } else {
            return matroskaOnly;
        }
    }

    /// <summary>
    /// Additional to the default arguments, allow the user to add custom arguments to the FFmpeg command line.
    /// </summary>
    public void AddArguments(IEnumerable<FFmpegArgument> arguments) {
        if (arguments != null) {
            extraArgs.AddRange(arguments);
        }
    }

    /// <summary>
    /// Disable FFmpeg's internal mixer and any channel map export for supporting the maximum number of channels for each codec.
    /// </summary>
    public void Allow8PlusChannels() => allow8PlusChannels = true;

    /// <summary>
    /// Remove some conditions under which FFmpeg would refuse to process the file.
    /// </summary>
    public void MakeSafe(string fileName) {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.IsNative() || ext is ".flac" or ".opus" or ".ogg" or ".ac3" or ".eac3" or ".ec3") {
            audioOnlyMode = true;
        }
    }

    /// <summary>
    /// Name of the <see cref="render"/>ed audio track merged to the output.
    /// </summary>
    public void SetTrackName(string name) => trackName = name;

    void BuildConfigurator(string fileName, bool forceAudioOnly) {
        args = new FFmpegConfigurator();
        bool hasVideoContainer = !forceAudioOnly &&
            !string.IsNullOrEmpty(sourceFile) &&
            File.Exists(sourceFile) &&
            IsVideoContainerExtension(Path.GetExtension(sourceFile));

        if (hasVideoContainer) {
            args.AddInputFile(sourceFile);
            args.AddInputFile(render);
            args.AddMappingIfExists(0, FFmpegStream.Video);
            args.AddMapping(1, FFmpegStream.Audio);
            args.AddMappingIfExists(0, FFmpegStream.Subtitle);
            args.SetCodec(FFmpegStream.Video, "copy");
            args.SetCodec(FFmpegStream.Subtitle, "copy");
        } else {
            args.AddInputFile(render);
            args.AddMapping(0, FFmpegStream.Audio);
        }

        args.SetCodec(FFmpegStream.Audio, outputCodec);
        if (allow8PlusChannels && string.Equals(outputCodec, "libopus", StringComparison.OrdinalIgnoreCase)) {
            args.Allow8PlusChannels();
        }
        if (string.Equals(outputCodec, "eac3", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputCodec, "ac3", StringComparison.OrdinalIgnoreCase)) {
            args.AddArgument(new FFmpegArgument("-strict", "-2"));
        }
        if (!string.IsNullOrEmpty(trackName)) {
            args.SetMetadata(FFmpegStream.Audio, 0, "title", trackName);
        }
        foreach (FFmpegArgument arg in extraArgs) {
            args.AddArgument(arg);
        }
        args.SetOverwrite(true);
        args.OutputFile = fileName;
    }

    static bool IsVideoContainerExtension(string ext) {
        if (string.IsNullOrEmpty(ext)) {
            return false;
        }
        return ext.ToLowerInvariant() is ".mkv" or ".mp4" or ".m4v" or ".mov" or ".ts" or ".m2ts" or ".webm" or ".avi";
    }

    /// <summary>
    /// Merge the video/subtitle streams of the source file with the <see cref="render"/>, and if successful, delete the render.
    /// </summary>
    /// <returns>If the merge was successful.</returns>
    public bool Merge(FFmpeg ffmpeg, string fileName) {
        LastError = string.Empty;
        string targetExt = Path.GetExtension(fileName).ToLowerInvariant();
        if (targetExt.IsNative() || targetExt is ".flac" or ".opus" or ".ogg" or ".ac3" or ".eac3" or ".ec3") {
            audioOnlyMode = true;
        }

        bool isFlacCodec = string.Equals(outputCodec, "flac", StringComparison.OrdinalIgnoreCase) || targetExt == ".flac";
        bool launchedOk = false;

        if (ffmpeg != null && ffmpeg.Found) {
            BuildConfigurator(fileName, audioOnlyMode);
            launchedOk = ffmpeg.Launch(args.ToString());
            if (!launchedOk && !audioOnlyMode) {
                // Retry audio-only in case source video/cover stream caused muxer rejection
                BuildConfigurator(fileName, true);
                launchedOk = ffmpeg.Launch(args.ToString());
            }
            if (!launchedOk) {
                LastError = !string.IsNullOrEmpty(ffmpeg.LastError) ? ffmpeg.LastError : $"FFmpeg exit code {ffmpeg.LastExitCode}";
            }
        } else {
            LastError = "FFmpeg binary not available.";
        }

        if ((!launchedOk || !File.Exists(fileName)) && isFlacCodec && targetExt == ".flac" && File.Exists(render)) {
            try {
                FlacAudioConverter.ConvertWavToFlac(render, fileName);
                launchedOk = true;
                LastError = string.Empty;
            } catch (Exception ex) {
                LastError += $"\nNative FLAC fallback error: {ex.Message}";
                launchedOk = false;
            }
        }

        if (launchedOk && File.Exists(fileName)) {
            if (!RenderingSettings.KeepTempFiles && !string.Equals(render, fileName, StringComparison.Ordinal) && File.Exists(render)) {
                try {
                    File.Delete(render);
                } catch {
                    // Best-effort temp cleanup
                }
            }
            return true;
        }
        return false;
    }
}
