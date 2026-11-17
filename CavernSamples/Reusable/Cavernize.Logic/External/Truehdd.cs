using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Exceptions;
using Cavern.Format.Operations;
using Cavern.Format.Renderers;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Exceptions;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using VoidX.WPF;

namespace Cavernize.Logic.External;

/// <summary>
/// Perform MLP / Dolby TrueHD conversions with truehdd by first extracting the TrueHD track (when inside a container),
/// then decoding it with truehdd to DAMF or CAF, and loading the decoded stream into Cavern for rendering.
/// </summary>
public class Truehdd : ExternalConverter {
    /// <summary>
    /// Optional platform override pointing directly to an already-installed truehdd binary (e.g. Android FilesDir/bin/truehdd).
    /// </summary>
    public static string CustomExecutablePath { get; set; }

    /// <summary>
    /// Optional platform override for temporary extraction/decoding files (e.g. Android CacheDir/truehdd_work).
    /// </summary>
    public static string TempWorkingDirectory { get; set; }

    /// <summary>
    /// Optional callback invoked after truehdd exits, receiving (executable, arguments, exitCode, stdout, stderr).
    /// </summary>
    public static Action<string, string, int, string, string> OnCommandCompleted { get; set; }

    /// <summary>
    /// Optional fallback bitstream extractor when container extraction fails or <see cref="CavernizeTrack.Track"/> is null.
    /// Parameters: (sourceTrack, destinationThdPath, progressCallback, cancellationToken) -> success.
    /// </summary>
    public static Func<CavernizeTrack, string, Action<double>, CancellationToken, bool> FallbackTrackExtractor { get; set; }

    /// <summary>
    /// Temporary files created during the current conversion for cleanup.
    /// </summary>
    readonly List<string> generatedFiles = [];

    /// <summary>
    /// Translations for user feedback.
    /// </summary>
    readonly ExternalConverterStrings language = new();

    /// <summary>
    /// The DAMF or CAF track after truehdd's run.
    /// </summary>
    CavernizeTrack track;

    /// <summary>
    /// Isolated working folder created for this conversion.
    /// </summary>
    string workDir;

    /// <summary>
    /// Path to the extracted MLP/TrueHD bitstream or output prefix.
    /// </summary>
    string tempTrack;

    /// <summary>
    /// Where to save the downloaded truehdd archive.
    /// </summary>
    string cacheArchive;

    /// <summary>
    /// If this folder exists, truehdd was properly downloaded and unpacked.
    /// </summary>
    string unpackDir;

    /// <summary>
    /// Save the downloaded truehdd version to this file so it can be checked later.
    /// </summary>
    string versionFile;

    /// <inheritdoc/>
    public override void PrepareOnUI() {
        if (!string.IsNullOrEmpty(CustomExecutablePath) && File.Exists(CustomExecutablePath)) {
            return;
        }

        versionFile = Path.Combine(cavernizeData, "truehdd.version");
        unpackDir = Path.Combine(cavernizeData, "truehdd");
        if (File.Exists(versionFile) && File.Exists(ResolveExecutablePath())) {
            return;
        }

        string platformAsset = GetPlatformArchiveSuffix();
        cacheArchive = Path.Combine(cavernizeData, platformAsset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "truehdd.tar.gz" : "truehdd.zip");

        UpdateStatusMessage(language["LicFe"]);
        string licence = HTTP.GET(licenceUrl) ?? DefaultApacheNotice;
        UpdateStatusMessage(language["LisWa"]);
        LicenceDisplay.SetDescription(string.Format(language["LicNe"], "truehdd", "Meridian Lossless Packing / Dolby TrueHD"));
        LicenceDisplay.SetLicenceText(licence);
        if (!LicenceDisplay.Prompt()) {
            throw new OperationCanceledException(language["LicCa"]);
        }
    }

    /// <summary>
    /// Convert the <paramref name="source"/> track with truehdd to DAMF or CAF and load it for rendering.
    /// </summary>
    public override CavernizeTrack Convert(CavernizeTrack source) => Convert(source, CancellationToken.None);

    /// <summary>
    /// Convert the <paramref name="source"/> track with truehdd to DAMF or CAF and load it for rendering.
    /// </summary>
    public override CavernizeTrack Convert(CavernizeTrack source, CancellationToken cancellationToken) {
        Download();

        if (source.Codec != Codec.TrueHD && source.SourceCodec != Codec.TrueHD) {
            throw new CodecNotFoundException(Codec.TrueHD);
        }

        string baseTempRoot = !string.IsNullOrEmpty(TempWorkingDirectory)
            ? TempWorkingDirectory
            : Path.GetDirectoryName(source.Path);
        if (string.IsNullOrEmpty(baseTempRoot)) {
            baseTempRoot = Path.GetTempPath();
        }
        Directory.CreateDirectory(baseTempRoot);
        workDir = Path.Combine(baseTempRoot, $"thd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        tempTrack = Path.Combine(workDir, tempFile);
        string toDecode;
        bool hasPreExtractedRaw = !string.IsNullOrEmpty(source.RawBitstreamPath) && File.Exists(source.RawBitstreamPath);
        bool needTempTrack = source.Container != Container.NotContainer && !hasPreExtractedRaw;

        if (hasPreExtractedRaw) {
            toDecode = source.RawBitstreamPath;
        } else if (needTempTrack) {
            UpdateStatusMessage(language["PrgRB"]);
            UpdateProgressValue(-1);
            bool extracted = false;
            if (FallbackTrackExtractor != null) {
                try {
                    extracted = FallbackTrackExtractor(source, tempTrack, p => UpdateProgressValue(Math.Clamp(p * 0.25, 0, 0.25)), cancellationToken);
                } catch (OperationCanceledException) {
                    QFile.DeleteIfExists(tempTrack);
                    throw;
                } catch {
                    QFile.DeleteIfExists(tempTrack);
                    extracted = false;
                }
            }
            if (!extracted && source.Track != null) {
                try {
                    source.Track.Source.Seek(0);
                    double duration = source.Track.Source.Duration;
                    long blocks = 0;
                    using (ExtractTrackFromContainer extractor = new(source.Track, File.OpenWrite(tempTrack))) {
                        while (extractor.Process()) {
                            cancellationToken.ThrowIfCancellationRequested();
                            if ((++blocks & 63) == 0 && duration > 0) {
                                double offset = source.Track.GetNextBlockOffset();
                                if (offset >= 0) {
                                    UpdateProgressValue(Math.Clamp(offset / duration * 0.25, 0, 0.25));
                                }
                            }
                        }
                    }
                    extracted = File.Exists(tempTrack) && new FileInfo(tempTrack).Length > 0;
                } catch (OperationCanceledException) {
                    QFile.DeleteIfExists(tempTrack);
                    throw;
                } catch {
                    QFile.DeleteIfExists(tempTrack);
                    extracted = false;
                }
            }
            if (!extracted || !File.Exists(tempTrack) || new FileInfo(tempTrack).Length == 0) {
                throw new InvalidOperationException("Failed to extract TrueHD bitstream from container.");
            }
            toDecode = tempTrack;
        } else {
            toDecode = source.Path;
        }

        UpdateStatusMessage(string.Format(language["PrgCo"], "truehdd"));
        string exePath = ResolveExecutablePath();
        EnsureExecutablePermissions(exePath);

        string outputPrefix = Path.Combine(workDir, "decoded");
        StringBuilder argBuilder = new();
        argBuilder.Append($"decode --progress --json --log-format json \"{toDecode}\" --output-path \"{outputPrefix}\"");
        if (MeridianLosslessPackingRenderer.ForcedPresentation.HasValue) {
            argBuilder.Append($" --presentation {MeridianLosslessPackingRenderer.ForcedPresentation.Value}");
        }
        string args = argBuilder.ToString();

        ProcessStartInfo truehddInfo = new() {
            FileName = exePath,
            WorkingDirectory = workDir,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        StringBuilder stdoutSb = new();
        StringBuilder stderrSb = new();
        long inputBytes = File.Exists(toDecode) ? new FileInfo(toDecode).Length : 0;

        using (Process runner = new() { StartInfo = truehddInfo }) {
            runner.OutputDataReceived += (_, e) => {
                if (e.Data != null) {
                    stdoutSb.AppendLine(e.Data);
                }
            };
            runner.ErrorDataReceived += (_, e) => {
                if (e.Data != null) {
                    stderrSb.AppendLine(e.Data);
                }
            };
            if (!runner.Start()) {
                throw new InvalidOperationException($"Failed to start truehdd at '{exePath}'.");
            }
            runner.BeginOutputReadLine();
            runner.BeginErrorReadLine();

            while (!runner.WaitForExit(180)) {
                if (cancellationToken.IsCancellationRequested) {
                    try {
                        runner.Kill(true);
                    } catch {
                        // Best-effort kill on cancel
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                double estProg = EstimateOutputProgress(outputPrefix, inputBytes);
                if (estProg > 0) {
                    UpdateProgressValue(needTempTrack ? 0.25 + estProg * 0.72 : estProg * 0.96);
                }
            }
            runner.WaitForExit();

            string stdout = stdoutSb.ToString().Trim();
            string stderr = stderrSb.ToString().Trim();
            OnCommandCompleted?.Invoke(exePath, args, runner.ExitCode, stdout, stderr);

            if (runner.ExitCode != 0) {
                throw new InvalidOperationException($"truehdd decode failed (exit {runner.ExitCode}).\n{ExtractErrorSummary(stderr)}");
            }

            CollectProducedFilesFromJson(stdout);
        }

        if (needTempTrack && !RenderingSettings.KeepTempFiles) {
            QFile.DeleteIfExists(tempTrack);
        }

        (string openPath, Codec decodedCodec) = ResolveDecodedEntry(outputPrefix);
        if (string.IsNullOrEmpty(openPath) || !File.Exists(openPath)) {
            throw new FileNotFoundException("truehdd finished without producing a readable .atmos or .caf output.", outputPrefix);
        }

        UpdateProgressValue(1);
        AudioReader reader = AudioReader.Open(openPath);
        track = new ExternalDecodedTrack(
            reader,
            decodedCodec,
            Codec.TrueHD,
            source,
            source.Container,
            source.GetVideoTracks(),
            toDecode,
            source.Language);
        return track;
    }

    /// <inheritdoc/>
    public override void Cleanup() {
        try {
            track?.Dispose();
        } catch {
            // Ignore disposal issues during cleanup
        }
        track = null;

        if (!RenderingSettings.KeepTempFiles) {
            QFile.DeleteIfExists(tempTrack);
            foreach (string path in generatedFiles) {
                QFile.DeleteIfExists(path);
            }
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) {
                try {
                    Directory.Delete(workDir, true);
                } catch {
                    // Best-effort folder removal
                }
            }
        }
        generatedFiles.Clear();
    }

    /// <summary>
    /// Download the latest truehdd release from GitHub and unpack it when no custom binary is configured.
    /// </summary>
    void Download() {
        if (!string.IsNullOrEmpty(CustomExecutablePath) && File.Exists(CustomExecutablePath)) {
            EnsureExecutablePermissions(CustomExecutablePath);
            return;
        }

        versionFile ??= Path.Combine(cavernizeData, "truehdd.version");
        unpackDir ??= Path.Combine(cavernizeData, "truehdd");
        string existingExe = ResolveExecutablePath();
        if (File.Exists(versionFile) && File.Exists(existingExe)) {
            EnsureExecutablePermissions(existingExe);
            return;
        }

        string platformAsset = GetPlatformArchiveSuffix();
        cacheArchive ??= Path.Combine(cavernizeData, platformAsset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "truehdd.tar.gz" : "truehdd.zip");

        Directory.CreateDirectory(cavernizeData);
        QFile.DeleteIfExists(cacheArchive);
        if (Directory.Exists(unpackDir)) {
            Directory.Delete(unpackDir, true);
        }

        string downloading = string.Format(language["PrgDl"], "truehdd");
        UpdateStatusMessage(downloading);
        string archiveUrl;
        try {
            archiveUrl = Downloader.GetLatestGitHubVersion("truehdd/truehdd", platformAsset);
            Task downloader = Downloader.Download(archiveUrl, cacheArchive,
                progress => {
                    UpdateStatusMessage($"{downloading} {progress:0.00%}");
                    UpdateProgressValue(progress);
                });
            downloader.Wait();
        } catch (Exception e) {
            throw new NetworkException($"{language["ErrNe"]}{Environment.NewLine}{e.Message}");
        }

        UpdateStatusMessage(string.Format(language["PrgEx"], "truehdd"));
        try {
            ExtractArchive(cacheArchive, unpackDir);
            QFile.DeleteIfExists(cacheArchive);
            EnsureExecutablePermissions(ResolveExecutablePath());
        } catch (Exception e) {
            throw new NetworkException($"{language["ErrNe"]}{Environment.NewLine}{e.Message}");
        }

        File.WriteAllText(versionFile, archiveUrl);
    }

    string ResolveExecutablePath() {
        if (!string.IsNullOrEmpty(CustomExecutablePath)) {
            return CustomExecutablePath;
        }
        string binaryName = OperatingSystem.IsWindows() ? "truehdd.exe" : "truehdd";
        string direct = Path.Combine(unpackDir, binaryName);
        if (File.Exists(direct) || !Directory.Exists(unpackDir)) {
            return direct;
        }
        string nested = Directory.GetFiles(unpackDir, binaryName, SearchOption.AllDirectories).FirstOrDefault();
        return nested ?? direct;
    }

    void CollectProducedFilesFromJson(string stdout) {
        if (string.IsNullOrWhiteSpace(stdout)) {
            return;
        }
        try {
            int brace = stdout.IndexOf('{');
            if (brace < 0) {
                return;
            }
            using JsonDocument doc = JsonDocument.Parse(stdout[brace..]);
            if (doc.RootElement.TryGetProperty("presentations", out JsonElement presArr) && presArr.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement pres in presArr.EnumerateArray()) {
                    if (pres.TryGetProperty("files", out JsonElement filesArr) && filesArr.ValueKind == JsonValueKind.Array) {
                        foreach (JsonElement fEl in filesArr.EnumerateArray()) {
                            string filePath = fEl.GetString();
                            if (!string.IsNullOrEmpty(filePath)) {
                                string full = Path.IsPathRooted(filePath) ? filePath : Path.Combine(workDir, filePath);
                                generatedFiles.Add(full);
                            }
                        }
                    }
                }
            }
        } catch {
            // Non-fatal: ResolveDecodedEntry still scans workDir
        }
    }

    (string path, Codec codec) ResolveDecodedEntry(string outputPrefix) {
        string atmosPath = outputPrefix + ".atmos";
        if (File.Exists(atmosPath)) {
            TrackGeneratedSet(outputPrefix);
            return (atmosPath, Codec.DAMF);
        }

        string cafPath = outputPrefix + ".caf";
        if (File.Exists(cafPath)) {
            generatedFiles.Add(cafPath);
            return (cafPath, Codec.PCM_LE);
        }

        string firstAtmos = generatedFiles.FirstOrDefault(f => f.EndsWith(".atmos", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            ?? Directory.GetFiles(workDir, "*.atmos").OrderBy(f => f).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstAtmos)) {
            return (firstAtmos, Codec.DAMF);
        }

        string firstCaf = generatedFiles.FirstOrDefault(f => f.EndsWith(".caf", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            ?? Directory.GetFiles(workDir, "*.caf").OrderBy(f => f).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstCaf)) {
            return (firstCaf, Codec.PCM_LE);
        }

        return (null, Codec.Unknown);
    }

    void TrackGeneratedSet(string prefix) {
        generatedFiles.Add(prefix + ".atmos");
        generatedFiles.Add(prefix + ".atmos.audio");
        generatedFiles.Add(prefix + ".atmos.metadata");
        generatedFiles.Add(prefix + ".caf");
    }

    static double EstimateOutputProgress(string outputPrefix, long inputThdBytes) {
        if (inputThdBytes <= 0) {
            return -1;
        }
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
                return -1;
            }
            // Lossless TrueHD typically compresses ~2.5x to 4.5x relative to unpacked 24-bit PCM/DAMF
            double expected = inputThdBytes * 3.8;
            return Math.Clamp(written / expected, 0.02, 0.96);
        } catch {
            return -1;
        }
    }

    static string ExtractErrorSummary(string stderr) {
        if (string.IsNullOrWhiteSpace(stderr)) {
            return "Unknown truehdd error.";
        }
        string[] lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(Math.Min(6, lines.Length)));
    }

    static void ExtractArchive(string archivePath, string destDir) {
        Directory.CreateDirectory(destDir);
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) {
            using FileStream fs = File.OpenRead(archivePath);
            using GZipStream gz = new(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, destDir, overwriteFiles: true);
        } else {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
        }
    }

    static void EnsureExecutablePermissions(string path) {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || !File.Exists(path)) {
            return;
        }
        try {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        } catch {
            // Platform may enforce chmod externally
        }
    }

    static string GetPlatformArchiveSuffix() {
        if (OperatingSystem.IsWindows()) {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "aarch64-pc-windows-msvc.zip"
                : "x86_64-pc-windows-msvc.zip";
        }
        if (OperatingSystem.IsMacOS()) {
            return "universal-macOS.zip";
        }
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64-unknown-linux-musl.tar.gz"
            : "x86_64-unknown-linux-musl.tar.gz";
    }

    const string tempFile = "_extracted.thd";

    const string licenceUrl = "https://raw.githubusercontent.com/truehdd/truehdd/refs/heads/main/LICENSE";

    const string DefaultApacheNotice =
        "truehdd (Apache License, Version 2.0)\nCopyright 2025-2026 Rainbaby\nhttps://github.com/truehdd/truehdd";
}
