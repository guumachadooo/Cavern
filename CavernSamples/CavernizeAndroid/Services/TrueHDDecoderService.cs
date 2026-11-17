using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Android.Content;

using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Operations;
using Cavern.Format.Renderers;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public sealed class ProbedAudioStream {
    public int StreamIndex { get; init; }
    public int AudioOrdinal { get; init; }
    public string CodecName { get; init; } = "";
    public string Language { get; init; } = "";
    public string ChannelSummary { get; init; } = "";
    public int SampleRate { get; init; } = 48000;
    public string RawDescription { get; init; } = "";

    public bool IsTrueHD => CodecName.Equals("truehd", StringComparison.OrdinalIgnoreCase) ||
                            CodecName.Equals("mlp", StringComparison.OrdinalIgnoreCase);

    public bool IsEnhancedAC3 => CodecName.Equals("eac3", StringComparison.OrdinalIgnoreCase);

    public bool IsAC3 => CodecName.Equals("ac3", StringComparison.OrdinalIgnoreCase);
}

public sealed class ContainerProbeResult {
    public double DurationSeconds { get; set; }
    public List<ProbedAudioStream> AudioStreams { get; } = [];
}

public sealed class TrueHDDecodeResult {
    public ExternalDecodedTrack DecodedTrack { get; init; }
    public string SessionDirectory { get; init; } = "";
    public string DecodedEntryPath { get; init; } = "";
    public string RawBitstreamPath { get; init; } = "";
    public int? PresentationUsed { get; init; }
    public bool HasAtmos { get; init; }
}

/// <summary>
/// Manages Android-safe TrueHD/Atmos extraction, truehdd decoding, FFmpeg container probing/demuxing fallbacks,
/// metadata generation, and temporary cache hygiene.
/// </summary>
public static class TrueHDDecoderService {
    public static string GetWorkRoot(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "truehdd_work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void CleanupOrphanedCache(Context context, IEnumerable<string> keepFilePaths = null) {
        if (context == null || RenderingSettings.KeepTempFiles) {
            return;
        }
        HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
        if (keepFilePaths != null) {
            foreach (string p in keepFilePaths) {
                if (!string.IsNullOrEmpty(p)) {
                    keep.Add(Path.GetFullPath(p));
                }
            }
        }

        try {
            string thdRoot = Path.Combine(context.CacheDir.AbsolutePath, "truehdd_work");
            if (Directory.Exists(thdRoot)) {
                foreach (string sub in Directory.GetDirectories(thdRoot)) {
                    bool keepSub = keep.Any(k => k.StartsWith(Path.GetFullPath(sub), StringComparison.OrdinalIgnoreCase));
                    if (!keepSub) {
                        TryDeleteDirectory(sub);
                    }
                }
            }
        } catch {
            // Best-effort cleanup
        }

        CleanFolderFiles(Path.Combine(context.CacheDir.AbsolutePath, "decoded_fallback"), keep);
        CleanFolderFiles(Path.Combine(context.CacheDir.AbsolutePath, "opened"), keep);
    }

    public static void CleanupWorkDirectory(string sessionDir) {
        if (string.IsNullOrEmpty(sessionDir) || RenderingSettings.KeepTempFiles) {
            return;
        }
        TryDeleteDirectory(sessionDir);
    }

    public static ContainerProbeResult ProbeContainerWithFFmpeg(string ffmpegPath, string inputPath) {
        ContainerProbeResult result = new();
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath) || string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath)) {
            return result;
        }

        string args = $"-hide_banner -i \"{inputPath}\"";
        (int exitCode, _, string stderr) = RunProcessSimple(ffmpegPath, args, timeoutMs: 15000);
        AppLogger.Info($"FFmpeg probe exit={exitCode} for '{Path.GetFileName(inputPath)}'");
        if (string.IsNullOrWhiteSpace(stderr)) {
            return result;
        }

        Match durMatch = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        double durationSec = 0;
        if (durMatch.Success &&
            int.TryParse(durMatch.Groups[1].Value, out int hh) &&
            int.TryParse(durMatch.Groups[2].Value, out int mm) &&
            double.TryParse(durMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ss)) {
            durationSec = hh * 3600.0 + mm * 60.0 + ss;
        }

        int audioOrd = 0;
        foreach (string line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
            Match m = Regex.Match(line, @"Stream\s+#0:(\d+)(?:\(([^)]+)\))?[^:]*:\s*Audio:\s*([^,\r\n]+)(.*)");
            if (!m.Success) {
                continue;
            }
            if (!int.TryParse(m.Groups[1].Value, out int streamIdx)) {
                continue;
            }
            string lang = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            string rawCodec = m.Groups[3].Value.Trim();
            string codecToken = rawCodec.Split([' ', '('], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? rawCodec;
            string rest = m.Groups[4].Value;

            int sr = 48000;
            Match srMatch = Regex.Match(rest, @"(\d+)\s*Hz");
            if (srMatch.Success && int.TryParse(srMatch.Groups[1].Value, out int parsedSr) && parsedSr > 0) {
                sr = parsedSr;
            }

            string[] restParts = rest.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
            string chSummary = restParts.Length >= 3 ? restParts[2] : (restParts.Length >= 2 ? restParts[1] : "");

            result.AudioStreams.Add(new ProbedAudioStream {
                StreamIndex = streamIdx,
                AudioOrdinal = audioOrd++,
                CodecName = codecToken.ToLowerInvariant(),
                Language = lang,
                SampleRate = sr,
                ChannelSummary = chSummary,
                RawDescription = $"{rawCodec}{rest}".Trim()
            });
        }

        result.DurationSeconds = durationSec;
        return result;
    }

    public static bool ExtractAudioStreamWithFFmpeg(
        string ffmpegPath,
        string inputPath,
        int streamIndex,
        string outputPath,
        string formatOrWav,
        double totalDurationSec,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath) || !File.Exists(inputPath)) {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        string codecArgs = formatOrWav switch {
            "truehd" => "-c:a copy -f truehd",
            "eac3" => "-c:a copy -f eac3",
            "ac3" => "-c:a copy -f ac3",
            _ => "-c:a pcm_s24le -rf64 auto -f wav"
        };
        string args = $"-y -hide_banner -i \"{inputPath}\" -map 0:{streamIndex} {codecArgs} \"{outputPath}\"";

        ProcessStartInfo psi = new() {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        StringBuilder outSb = new();
        StringBuilder errSb = new();
        using Process proc = new() { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => {
            if (e.Data != null) {
                outSb.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) => {
            if (e.Data == null) {
                return;
            }
            errSb.AppendLine(e.Data);
            if (totalDurationSec > 0) {
                Match tm = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (tm.Success &&
                    int.TryParse(tm.Groups[1].Value, out int h) &&
                    int.TryParse(tm.Groups[2].Value, out int m) &&
                    double.TryParse(tm.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) {
                    double cur = h * 3600.0 + m * 60.0 + s;
                    double prog = Math.Clamp(cur / totalDurationSec, 0, 0.99);
                    onProgress?.Invoke(prog, $"FFmpeg: {prog:0%}");
                }
            }
        };

        if (!proc.Start()) {
            return false;
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        while (!proc.WaitForExit(150)) {
            if (token.IsCancellationRequested) {
                try {
                    proc.Kill(entireProcessTree: true);
                } catch {
                    // Best-effort kill
                }
                CleanUpPartialFile(outputPath);
                token.ThrowIfCancellationRequested();
            }
        }
        proc.WaitForExit();

        string stdout = outSb.ToString().Trim();
        string stderr = errSb.ToString().Trim();
        AppLogger.LogFFmpeg(ffmpegPath, args, proc.ExitCode, stdout, stderr);
        bool ok = proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        if (!ok) {
            CleanUpPartialFile(outputPath);
        }
        return ok;
    }

    static void CleanUpPartialFile(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Best-effort cleanup
        }
    }

    public static TrueHDDecodeResult DecodeTrack(
        Context context,
        CavernizeTrack sourceTrack,
        string originalContainerPath,
        string ffmpegPath,
        string truehddPath,
        int? forcedPresentation,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        if (sourceTrack == null) {
            throw new ArgumentNullException(nameof(sourceTrack));
        }
        if (string.IsNullOrEmpty(truehddPath) || !File.Exists(truehddPath)) {
            throw new FileNotFoundException("truehdd binary is not installed.", truehddPath);
        }
        TruehddInstallerService.EnsureExecutable(truehddPath);

        string workRoot = GetWorkRoot(context);
        string sessionDir = Path.Combine(workRoot, $"sess_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDir);

        try {
            string thdPath;
            bool deleteRawOnCleanup = false;
            bool hasExistingRaw = !string.IsNullOrEmpty(sourceTrack.RawBitstreamPath) && File.Exists(sourceTrack.RawBitstreamPath);

            if (sourceTrack.Container == Container.NotContainer && !string.IsNullOrEmpty(sourceTrack.Path) && File.Exists(sourceTrack.Path)) {
                thdPath = sourceTrack.Path;
            } else if (hasExistingRaw) {
                thdPath = sourceTrack.RawBitstreamPath;
            } else {
                thdPath = Path.Combine(sessionDir, "source.thd");
                deleteRawOnCleanup = true;
                onProgress?.Invoke(0.02, "Extraindo faixa Dolby TrueHD...");

                bool extracted = false;
                string containerInput = !string.IsNullOrEmpty(originalContainerPath) ? originalContainerPath : sourceTrack.Path;

                // Prefer native FFmpeg stream copy first when installed: avoids slow block-by-block C# reads on large MKVs and cancels immediately.
                if (!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath) &&
                    !string.IsNullOrEmpty(containerInput) && File.Exists(containerInput)) {
                    ContainerProbeResult probe = ProbeContainerWithFFmpeg(ffmpegPath, containerInput);
                    ProbedAudioStream targetStream = probe.AudioStreams
                        .FirstOrDefault(s => s.IsTrueHD && s.AudioOrdinal == sourceTrack.Index)
                        ?? probe.AudioStreams.FirstOrDefault(s => s.IsTrueHD);
                    if (targetStream != null) {
                        extracted = ExtractAudioStreamWithFFmpeg(
                            ffmpegPath,
                            containerInput,
                            targetStream.StreamIndex,
                            thdPath,
                            "truehd",
                            probe.DurationSeconds,
                            (p, _) => {
                                double extRatio = Math.Clamp(p, 0, 0.99);
                                double totalFrac = Math.Clamp(0.02 + extRatio * 0.22, 0.02, 0.24);
                                onProgress?.Invoke(totalFrac, $"Extraindo TrueHD ({extRatio:0%})...");
                            },
                            token);
                    }
                }

                if (!extracted && sourceTrack.Track != null) {
                    try {
                        sourceTrack.Track.Source.Seek(0);
                        double dur = sourceTrack.Track.Source.Duration;
                        long blocks = 0;
                        using (ExtractTrackFromContainer extractor = new(sourceTrack.Track, File.Create(thdPath))) {
                            while (extractor.Process()) {
                                token.ThrowIfCancellationRequested();
                                if ((++blocks & 63) == 0 && dur > 0) {
                                    double offset = sourceTrack.Track.GetNextBlockOffset();
                                    if (offset >= 0) {
                                        double extRatio = Math.Clamp(offset / dur, 0, 0.99);
                                        double frac = Math.Clamp(0.02 + extRatio * 0.22, 0.02, 0.24);
                                        onProgress?.Invoke(frac, $"Extraindo TrueHD ({extRatio:0%})...");
                                    }
                                }
                            }
                        }
                        extracted = File.Exists(thdPath) && new FileInfo(thdPath).Length > 0;
                    } catch (OperationCanceledException) {
                        CleanUpPartialFile(thdPath);
                        throw;
                    } catch (Exception ex) {
                        AppLogger.Warn($"ExtractTrackFromContainer fallback triggered: {ex.Message}");
                        CleanUpPartialFile(thdPath);
                        extracted = false;
                    }
                }

                if (!extracted || !File.Exists(thdPath) || new FileInfo(thdPath).Length == 0) {
                    throw new InvalidOperationException("Não foi possível extrair o fluxo Dolby TrueHD do contêiner.");
                }
            }

            onProgress?.Invoke(0.25, "Decodificando Dolby TrueHD com truehdd...");
            string outputPrefix = Path.Combine(sessionDir, "decoded");
            StringBuilder argBuilder = new();
            argBuilder.Append($"decode --progress --json --log-format json \"{thdPath}\" --output-path \"{outputPrefix}\"");
            if (forcedPresentation.HasValue && forcedPresentation.Value >= 0) {
                argBuilder.Append($" --presentation {forcedPresentation.Value}");
            }
            string args = argBuilder.ToString();

            ProcessStartInfo psi = new() {
                FileName = truehddPath,
                WorkingDirectory = sessionDir,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            StringBuilder stdoutSb = new();
            StringBuilder stderrSb = new();
            long thdBytes = new FileInfo(thdPath).Length;
            double trackDurSec = sourceTrack.Length > 0 && sourceTrack.SampleRate > 0
                ? sourceTrack.Length / (double)sourceTrack.SampleRate
                : 0;
            string decodeJsonStdout;

            using (Process proc = new() { StartInfo = psi }) {
                proc.OutputDataReceived += (_, e) => {
                    if (e.Data != null) {
                        stdoutSb.AppendLine(e.Data);
                    }
                };
                proc.ErrorDataReceived += (_, e) => {
                    if (e.Data != null) {
                        stderrSb.AppendLine(e.Data);
                    }
                };
                if (!proc.Start()) {
                    throw new InvalidOperationException($"Falha ao iniciar truehdd em '{truehddPath}'.");
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (!proc.WaitForExit(180)) {
                    if (token.IsCancellationRequested) {
                        try {
                            proc.Kill(entireProcessTree: true);
                        } catch {
                            // Best-effort kill
                        }
                        token.ThrowIfCancellationRequested();
                    }

                    (double est, long writtenMb) = EstimateDecodeProgress(outputPrefix, thdBytes, trackDurSec);
                    if (est > 0) {
                        double uiProg = 0.25 + est * 0.72;
                        onProgress?.Invoke(uiProg, $"truehdd: {est:0%} ({writtenMb} MB)");
                    }
                }
                proc.WaitForExit();

                decodeJsonStdout = stdoutSb.ToString().Trim();
                string stderr = stderrSb.ToString().Trim();
                AppLogger.LogTruehdd(truehddPath, args, proc.ExitCode, decodeJsonStdout, stderr);

                if (proc.ExitCode != 0) {
                    string summary = ExtractLastLines(stderr, 6);
                    throw new InvalidOperationException($"truehdd falhou (código {proc.ExitCode}).\n{summary}");
                }
            }

            onProgress?.Invoke(0.98, "Abrindo áudio espacial decodificado...");
            (string entryPath, Codec decodedCodec) = ResolveDecodedEntry(sessionDir, outputPrefix);
            if (string.IsNullOrEmpty(entryPath) || !File.Exists(entryPath)) {
                throw new FileNotFoundException("O truehdd terminou sem gerar saída .atmos ou .caf legível.", outputPrefix);
            }

            // If we extracted a standalone temporary .thd inside sessionDir, keep it until session cleanup so --extract works
            _ = deleteRawOnCleanup;

            AudioReader reader = AudioReader.Open(entryPath);
            ReadableMetadata truehdMeta = BuildMetadataFromDecodeJson(decodeJsonStdout, reader, decodedCodec, forcedPresentation);
            ExternalDecodedTrack decodedTrack = new(
                reader,
                decodedCodec,
                Codec.TrueHD,
                sourceTrack,
                sourceTrack.Container,
                sourceTrack.GetVideoTracks(),
                thdPath,
                sourceTrack.Language,
                truehdMeta);

            onProgress?.Invoke(1.0, decodedCodec == Codec.DAMF ? "Dolby Atmos TrueHD pronto!" : "Dolby TrueHD pronto!");
            return new TrueHDDecodeResult {
                DecodedTrack = decodedTrack,
                SessionDirectory = sessionDir,
                DecodedEntryPath = entryPath,
                RawBitstreamPath = thdPath,
                PresentationUsed = forcedPresentation,
                HasAtmos = decodedCodec == Codec.DAMF
            };
        } catch {
            CleanupWorkDirectory(sessionDir);
            throw;
        }
    }

    static ReadableMetadata BuildMetadataFromDecodeJson(string stdout, AudioReader reader, Codec decodedCodec, int? presentationUsed) {
        List<ReadableMetadataField> fields = [
            new("Output", "Decoded master container", decodedCodec == Codec.DAMF ? "Dolby Atmos Master Format (DAMF)" : "Core Audio Format (PCM)"),
            new("SampleRate", "Sampling frequency", $"{reader.SampleRate} Hz"),
            new("Channels", "Decoded channels / objects", reader.ChannelCount),
            new("Samples", "Total samples per channel", reader.Length),
        ];
        if (presentationUsed.HasValue && presentationUsed.Value >= 0) {
            fields.Add(new("Presentation", "Forced MLP presentation", presentationUsed.Value));
        }

        if (!string.IsNullOrWhiteSpace(stdout)) {
            try {
                int brace = stdout.IndexOf('{');
                if (brace >= 0) {
                    using JsonDocument doc = JsonDocument.Parse(stdout[brace..]);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("version", out JsonElement vEl)) {
                        fields.Add(new("Decoder", "truehdd version", vEl.GetString() ?? ""));
                    }
                    if (root.TryGetProperty("frames", out JsonElement fEl)) {
                        fields.Add(new("Frames", "Access units decoded", fEl.ToString()));
                    }
                    if (root.TryGetProperty("skippedFrames", out JsonElement sfEl)) {
                        fields.Add(new("SkippedFrames", "Skipped access units", sfEl.ToString()));
                    }
                    if (root.TryGetProperty("concealedFrames", out JsonElement cfEl)) {
                        fields.Add(new("ConcealedFrames", "Concealed access units", cfEl.ToString()));
                    }
                    if (root.TryGetProperty("presentations", out JsonElement presArr) && presArr.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement p in presArr.EnumerateArray()) {
                            string idx = p.TryGetProperty("index", out JsonElement iEl) ? iEl.ToString() : "?";
                            string fmt = p.TryGetProperty("format", out JsonElement fmtEl) ? (fmtEl.GetString() ?? "?") : "?";
                            string ch = p.TryGetProperty("channels", out JsonElement cEl) ? cEl.ToString() : "?";
                            fields.Add(new($"Presentation_{idx}", $"Substream presentation {idx}", $"{fmt}, {ch} ch"));
                        }
                    }
                }
            } catch {
                // Keep basic reader metadata on parse fallback
            }
        }

        return new ReadableMetadata([new ReadableMetadataHeader("Dolby TrueHD (truehdd)", fields)]);
    }

    static (string entryPath, Codec codec) ResolveDecodedEntry(string sessionDir, string outputPrefix) {
        string atmos = outputPrefix + ".atmos";
        if (File.Exists(atmos)) {
            return (atmos, Codec.DAMF);
        }
        string caf = outputPrefix + ".caf";
        if (File.Exists(caf)) {
            return (caf, Codec.PCM_LE);
        }
        string anyAtmos = Directory.GetFiles(sessionDir, "*.atmos").OrderBy(f => f).FirstOrDefault();
        if (!string.IsNullOrEmpty(anyAtmos)) {
            return (anyAtmos, Codec.DAMF);
        }
        string anyCaf = Directory.GetFiles(sessionDir, "*.caf").OrderBy(f => f).FirstOrDefault();
        if (!string.IsNullOrEmpty(anyCaf)) {
            return (anyCaf, Codec.PCM_LE);
        }
        return (null, Codec.Unknown);
    }

    static (double frac, long writtenMb) EstimateDecodeProgress(string outputPrefix, long inputThdBytes, double durationSec) {
        try {
            long written = 0;
            string atmosAudio = outputPrefix + ".atmos.audio";
            string caf = outputPrefix + ".caf";
            if (File.Exists(atmosAudio)) {
                written = new FileInfo(atmosAudio).Length;
            } else if (File.Exists(caf)) {
                written = new FileInfo(caf).Length;
            }
            if (written <= 0) {
                return (-1, 0);
            }
            long mb = written / (1024 * 1024);
            double expected = durationSec > 0
                ? durationSec * 48000.0 * 3.0 * 12.0
                : Math.Max(1.0, inputThdBytes * 3.8);
            return (Math.Clamp(written / expected, 0.02, 0.97), mb);
        } catch {
            return (-1, 0);
        }
    }

    static (int exitCode, string stdout, string stderr) RunProcessSimple(string executable, string arguments, int timeoutMs) {
        ProcessStartInfo psi = new() {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process proc = Process.Start(psi);
        if (proc == null) {
            return (-1, "", "Failed to start process.");
        }
        StringBuilder outSb = new();
        StringBuilder errSb = new();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs)) {
            try {
                proc.Kill(entireProcessTree: true);
            } catch {
                // Ignore
            }
        }
        return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    static string ExtractLastLines(string text, int maxLines) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "Unknown error.";
        }
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(Math.Min(maxLines, lines.Length)));
    }

    static void CleanFolderFiles(string folder, HashSet<string> keep) {
        try {
            if (!Directory.Exists(folder)) {
                return;
            }
            foreach (string file in Directory.GetFiles(folder)) {
                if (!keep.Contains(Path.GetFullPath(file))) {
                    try {
                        File.Delete(file);
                    } catch {
                        // Ignore locked temp file
                    }
                }
            }
        } catch {
            // Ignore
        }
    }

    static void TryDeleteDirectory(string dir) {
        try {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, recursive: true);
            }
        } catch {
            // Best-effort cleanup
        }
    }
}
