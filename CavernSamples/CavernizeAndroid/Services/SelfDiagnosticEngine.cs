using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;

using Cavern;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavern.VocalChain;

using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

public enum SelfCheckStatus {
    Pass,
    Warn,
    Fail,
    Skip
}

public sealed class SelfCheckItem {
    public string Id { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Name { get; init; } = "";
    public SelfCheckStatus Status { get; init; }
    public long ElapsedMs { get; init; }
    public string Details { get; init; } = "";
}

public sealed class SelfCheckReport {
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "quick";
    public string SessionId { get; init; } = "";
    public string ExecutedAtUtc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public string AppVersion { get; init; } = "";
    public long AppBuild { get; init; }
    public string DeviceModel { get; init; } = "";
    public string AndroidRelease { get; init; } = "";
    public int SdkInt { get; init; }
    public long TotalElapsedMs { get; init; }
    public int PassCount { get; init; }
    public int WarnCount { get; init; }
    public int FailCount { get; init; }
    public int SkipCount { get; init; }
    public List<SelfCheckItem> Checks { get; init; } = [];

    public string OverallVerdict =>
        FailCount > 0 ? "FAIL" : (WarnCount > 0 ? "WARN" : "PASS");

    public string CompactSummary =>
        $"{OverallVerdict} ({PassCount} OK · {WarnCount} WARN · {FailCount} FAIL · {SkipCount} SKIP · {TotalElapsedMs}ms)";

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown() {
        StringBuilder sb = new();
        sb.AppendLine($"# Cavernize Android — Self-Check Report ({OverallVerdict})");
        sb.AppendLine();
        sb.AppendLine($"- **Mode:** `{Mode}` | **Session:** `{SessionId}` | **UTC:** `{ExecutedAtUtc}`");
        sb.AppendLine($"- **App:** `v{AppVersion}` (build `{AppBuild}`) | **Device:** `{DeviceModel}` (Android {AndroidRelease}, SDK {SdkInt})");
        sb.AppendLine($"- **Totals:** `{PassCount} PASS` · `{WarnCount} WARN` · `{FailCount} FAIL` · `{SkipCount} SKIP` · `{TotalElapsedMs} ms`");
        sb.AppendLine();
        sb.AppendLine("| Layer | Check | Status | Time | Details |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (SelfCheckItem item in Checks) {
            string badge = item.Status switch {
                SelfCheckStatus.Pass => "PASS",
                SelfCheckStatus.Warn => "WARN",
                SelfCheckStatus.Fail => "FAIL",
                _ => "SKIP"
            };
            string safeDetails = AppLogger.SanitizeSensitiveText(item.Details ?? "")
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", " ");
            sb.AppendLine($"| {item.Layer} | `{item.Id}` | **{badge}** | {item.ElapsedMs} ms | {safeDetails} |");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Intelligent, non-exponential on-device health &amp; DSP verification suite.
/// Combines bottom-up subsystem isolation probes (NativeLibs/W^X, FFmpeg, truehdd, ONNX Runtime,
/// Pro-Audio NDK bridge, audio routes, memory/storage, binaural assets, WAV/FLAC codecs) with a
/// deterministic Strength-2 Pairwise Orthogonal Covering Array (8 vectors covering 100% of active
/// DSP stage pairs) to detect NaN/Inf blow-ups, silent-output bugs, ceiling violations, and real-time
/// CPU budget overruns without mutating user settings or user media files.
/// </summary>
public static class SelfDiagnosticEngine {
    readonly record struct PairwiseDspVector(
        string Label,
        bool AutoEq,
        bool HptfNotch,
        bool Iso226,
        LoudnessNormalizationMode LoudNorm,
        bool DialogEnhancer,
        float LimiterCeilingDbtp);

    // Mathematically proven 100%-covering Strength-2 Orthogonal Covering Array OA(8; 4^1 x 2^5; 2):
    // covers all 80 pairwise interactions across the 6 DSP dimensions (128 full states) in exactly 8 vectors.
    static readonly PairwiseDspVector[] PairwiseMatrix = [
        new("V1_Off_Base",       false, false, false, LoudnessNormalizationMode.Off,               false, -1.0f),
        new("V2_Off_Full",       true,  true,  true,  LoudnessNormalizationMode.Off,               true,  -0.1f),
        new("V3_Atmos_IsoDlg",   false, false, true,  LoudnessNormalizationMode.AtmosMusicMinus18, true,  -0.1f),
        new("V4_Atmos_EqHptf",   true,  true,  false, LoudnessNormalizationMode.AtmosMusicMinus18, false, -1.0f),
        new("V5_Stream_HptfDlg", false, true,  false, LoudnessNormalizationMode.StreamingMinus14,  true,  -0.1f),
        new("V6_Stream_EqIso",   true,  false, true,  LoudnessNormalizationMode.StreamingMinus14,  false, -1.0f),
        new("V7_Ebu_HptfIso",    false, true,  true,  LoudnessNormalizationMode.EbuR128Minus23,    false, -0.1f),
        new("V8_Ebu_EqDlg",      true,  false, false, LoudnessNormalizationMode.EbuR128Minus23,    true,  -1.0f)
    ];

    public static Task<SelfCheckReport> RunAsync(
        Context context,
        AppSettingsStore store,
        bool fullSuite,
        Action<string> onProgress = null,
        CancellationToken ct = default) {
        return Task.Run(() => RunInternal(context, store, fullSuite, onProgress, ct), ct);
    }

    static SelfCheckReport RunInternal(
        Context context,
        AppSettingsStore store,
        bool fullSuite,
        Action<string> onProgress,
        CancellationToken ct) {
        Stopwatch totalSw = Stopwatch.StartNew();
        List<SelfCheckItem> checks = [];
        string mode = fullSuite ? "full" : "quick";
        AppLogger.Info("SYS", $"SelfDiagnosticEngine started (mode={mode})");

        void AddCheck(string id, string layer, string name, Func<(SelfCheckStatus status, string details)> probe) {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(name);
            Stopwatch sw = Stopwatch.StartNew();
            try {
                (SelfCheckStatus status, string details) = probe();
                sw.Stop();
                checks.Add(new SelfCheckItem {
                    Id = id,
                    Layer = layer,
                    Name = name,
                    Status = status,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Details = details ?? ""
                });
            } catch (System.OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                sw.Stop();
                AppLogger.Error("SYS", $"SelfCheck '{id}' threw exception", ex);
                checks.Add(new SelfCheckItem {
                    Id = id,
                    Layer = layer,
                    Name = name,
                    Status = SelfCheckStatus.Fail,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Details = $"{ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        // Layer 1: NativeLibs & W^X Binaries
        AddCheck("native.wx_libs", "L1-Native", "NativeLibs W^X Bundle", () => CheckNativeLibs(context));
        AddCheck("native.ffmpeg_exec", "L1-Native", "FFmpeg Execution Probe", () => CheckFFmpegExec(context, store));
        AddCheck("native.truehdd_exec", "L1-Native", "truehdd Execution Probe", () => CheckTruehddExec(context, store));
        AddCheck("native.proaudio_abi", "L1-Native", "Pro-Audio NDK Bridge", CheckProAudioBridge);
        AddCheck("native.onnx_abi", "L1-Native", "ONNX Runtime & Models", () => CheckOnnxRuntime(context));

        // Layer 2: Environment, Storage, Localization & Audio Hardware
        AddCheck("sys.memory_storage", "L2-System", "RAM & Storage I/O", () => CheckMemoryAndStorage(context));
        AddCheck("sys.localization", "L2-System", "EN/PT-BR Strings Integrity", CheckLocalizationIntegrity);
        AddCheck("sys.audio_routes", "L2-System", "Audio Routes & Low-Latency", () => CheckAudioRoutes(context));

        // Layer 3: Isolated Core DSP Physics & Assets
        AddCheck("dsp.binaural_assets", "L3-DSP", "Binaural HRIR/BRIR Assets", () => CheckBinauralAssets(context));
        AddCheck("dsp.autoeq_guard", "L3-DSP", "AutoEq & Double-EQ Guard", CheckAutoEqAndGuard);
        AddCheck("dsp.air_absorption", "L3-DSP", "ISO 9613-1 Air Absorption", CheckAirAbsorptionPhysics);
        AddCheck("dsp.truepeak_brickwall", "L3-DSP", "4x True-Peak Brickwall", CheckTruePeakBrickwall);
        AddCheck("dsp.vocal_chain", "L3-DSP", "Live Vocal Chain Zero-GC", CheckVocalChainDsp);

        // Layer 4: Strength-2 Pairwise Orthogonal DSP Matrix (non-exponential interaction coverage)
        if (fullSuite) {
            AddCheck("dsp.pairwise_matrix", "L4-Pairwise", "Pairwise DSP Matrix (8x OA)", CheckPairwiseDspMatrix);
            AddCheck("codec.wav_roundtrip", "L5-Codecs", "Cavern WAV Write/Read", () => CheckWavRoundTrip(context));
            AddCheck("codec.ffmpeg_flac", "L5-Codecs", "FFmpeg WAV->FLAC Pipeline", () => CheckFFmpegFlacRoundTrip(context, store));
        } else {
            // Quick mode runs the 2 highest-coverage orthogonal vectors (Direct + Full Atmos Stack)
            AddCheck("dsp.pairwise_smoke", "L4-Pairwise", "DSP Stack Smoke (2x OA)", CheckQuickPairwiseSmoke);
        }

        // Layer 6: Cloud Sync Readiness
        AddCheck("cloud.github_sync", "L6-Cloud", "GitHub Cloud Log Sync", () => CheckCloudSyncReadiness(store));

        totalSw.Stop();
        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);

        SelfCheckReport report = new() {
            Mode = mode,
            SessionId = AppLogger.SessionId,
            ExecutedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            AppVersion = ver,
            AppBuild = build,
            DeviceModel = $"{Build.Manufacturer} {Build.Model}".Trim(),
            AndroidRelease = Build.VERSION.Release ?? "",
            SdkInt = (int)Build.VERSION.SdkInt,
            TotalElapsedMs = totalSw.ElapsedMilliseconds,
            PassCount = checks.Count(c => c.Status == SelfCheckStatus.Pass),
            WarnCount = checks.Count(c => c.Status == SelfCheckStatus.Warn),
            FailCount = checks.Count(c => c.Status == SelfCheckStatus.Fail),
            SkipCount = checks.Count(c => c.Status == SelfCheckStatus.Skip),
            Checks = checks
        };

        if (report.FailCount > 0) {
            AppLogger.Error("SYS", $"SelfCheck finished: {report.CompactSummary}");
        } else if (report.WarnCount > 0) {
            AppLogger.Warn("SYS", $"SelfCheck finished: {report.CompactSummary}");
        } else {
            AppLogger.Info("SYS", $"SelfCheck finished: {report.CompactSummary}");
        }
        foreach (SelfCheckItem item in checks) {
            if (item.Status == SelfCheckStatus.Fail) {
                AppLogger.Error("SYS", $"SelfCheck FAIL [{item.Id}]: {item.Details}");
            } else if (item.Status == SelfCheckStatus.Warn) {
                AppLogger.Warn("SYS", $"SelfCheck WARN [{item.Id}]: {item.Details}");
            }
        }

        return report;
    }

    static (SelfCheckStatus, string) CheckNativeLibs(Context context) {
        string natDir = context?.ApplicationInfo?.NativeLibraryDir ?? "";
        if (string.IsNullOrEmpty(natDir) || !Directory.Exists(natDir)) {
            return (SelfCheckStatus.Fail, $"NativeLibraryDir missing or inaccessible ('{natDir}')");
        }
        string[] required = ["libffmpeg.so", "libtruehdd.so", "libonnxruntime.so", "libcavernize_proaudio.so"];
        List<string> missing = [];
        List<string> present = [];
        foreach (string lib in required) {
            string p = Path.Combine(natDir, lib);
            if (File.Exists(p) && new FileInfo(p).Length > 0) {
                present.Add($"{lib}({new FileInfo(p).Length / 1024}KB)");
            } else {
                missing.Add(lib);
            }
        }
        string legacyBin = Path.Combine(context.FilesDir?.AbsolutePath ?? "", "bin");
        bool hasLegacy = !string.IsNullOrEmpty(legacyBin) && Directory.Exists(legacyBin) && Directory.GetFiles(legacyBin).Length > 0;
        if (missing.Count > 0) {
            return (SelfCheckStatus.Fail, $"Missing bundled libs: {string.Join(", ", missing)} | Found: {string.Join(", ", present)}");
        }
        if (hasLegacy) {
            return (SelfCheckStatus.Warn, $"All 4 libs present ({string.Join(", ", present)}), but legacy FilesDir/bin still contains files");
        }
        return (SelfCheckStatus.Pass, $"All 4 W^X libs intact: {string.Join(", ", present)}");
    }

    static (SelfCheckStatus, string) CheckFFmpegExec(Context context, AppSettingsStore store) {
        string bin = FFmpegInstallerService.ResolveExistingFFmpegPath(context, store);
        if (string.IsNullOrEmpty(bin) || !File.Exists(bin)) {
            return (SelfCheckStatus.Fail, "Bundled libffmpeg.so not resolved");
        }
        ProcessStartInfo psi = new(bin, "-version") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        (int exit, string stdout, string stderr) = FFmpegInstallerService.RunProcessSafe(psi, timeoutMs: 8000);
        if (exit != 0) {
            string err = (stderr ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
            return (SelfCheckStatus.Fail, $"libffmpeg.so -version exit={exit} ({err})");
        }
        string firstLine = (stdout ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "ok";
        return (SelfCheckStatus.Pass, firstLine);
    }

    static (SelfCheckStatus, string) CheckTruehddExec(Context context, AppSettingsStore store) {
        string bin = TruehddInstallerService.ResolveExistingTruehddPath(context, store);
        if (string.IsNullOrEmpty(bin) || !File.Exists(bin)) {
            return (SelfCheckStatus.Fail, "Bundled libtruehdd.so not resolved");
        }
        ProcessStartInfo psi = new(bin, "--version") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        (int exit, string stdout, string stderr) = FFmpegInstallerService.RunProcessSafe(psi, timeoutMs: 8000);
        if (exit != 0) {
            string err = (stderr ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
            return (SelfCheckStatus.Fail, $"libtruehdd.so --version exit={exit} ({err})");
        }
        string firstLine = (stdout ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "ok";
        return (SelfCheckStatus.Pass, firstLine);
    }

    static (SelfCheckStatus, string) CheckProAudioBridge() {
        bool avail = NativeProAudioBridge.IsAvailable;
        if (!avail) {
            return (SelfCheckStatus.Warn, "libcavernize_proaudio.so unavailable; managed AudioTrack fallback active");
        }
        return (SelfCheckStatus.Pass,
            $"ABI={NativeProAudioBridge.AbiVersion}, PathA(AAudio)={NativeProAudioBridge.SupportsPathAAudio}, PathB(libusb)={NativeProAudioBridge.SupportsPathBLibUsb}");
    }

    static (SelfCheckStatus, string) CheckOnnxRuntime(Context context) {
        bool installed = OnnxRuntimeInstallerService.IsRuntimeInstalled(context);
        if (!installed) {
            return (SelfCheckStatus.Fail, "libonnxruntime.so missing or truncated in NativeLibraryDir");
        }
        string ver = OnnxRuntimeInstallerService.ReadBundledVersionText(context);
        string header = OnnxRuntimeInstallerService.ReadHeaderText(context);
        (uint apiVer, Dictionary<string, int> slots, string diag) = OnnxRuntimeHeaderParser.ParseOrtApiHeaderWithDiagnostics(header);
        if (slots.Count == 0) {
            return (SelfCheckStatus.Fail, $"OrtApi header parse failed ({diag})");
        }

        string modelsDir = OnnxRuntimeInstallerService.GetModelsDir(context);
        int validModels = 0;
        int corruptModels = 0;
        if (Directory.Exists(modelsDir)) {
            foreach (string f in Directory.GetFiles(modelsDir, "*.onnx")) {
                if (new FileInfo(f).Length >= 1024) {
                    validModels++;
                } else {
                    corruptModels++;
                }
            }
        }
        bool qnn = OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(context);
        string info = $"ORT v{ver} (API v{apiVer}, {slots.Count} vtable slots, QNN={qnn}), models={validModels} valid";
        if (corruptModels > 0) {
            return (SelfCheckStatus.Warn, $"{info}, {corruptModels} corrupt/empty .onnx file(s) detected");
        }
        return (SelfCheckStatus.Pass, info);
    }

    static (SelfCheckStatus, string) CheckMemoryAndStorage(Context context) {
        long availMb = -1;
        long totalMb = -1;
        bool lowMem = false;
        if (context?.GetSystemService(Context.ActivityService) is ActivityManager am) {
            ActivityManager.MemoryInfo mi = new();
            am.GetMemoryInfo(mi);
            availMb = mi.AvailMem / (1024 * 1024);
            totalMb = mi.TotalMem / (1024 * 1024);
            lowMem = mi.LowMemory;
        }
        long gcMb = GC.GetTotalMemory(false) / (1024 * 1024);

        // Verify writable cache & internal storage round-trip
        string probeFile = Path.Combine(context.CacheDir.AbsolutePath, "selfcheck_io_probe.tmp");
        try {
            File.WriteAllText(probeFile, "cavernize-ok", System.Text.Encoding.UTF8);
            string readBack = File.ReadAllText(probeFile, System.Text.Encoding.UTF8);
            File.Delete(probeFile);
            if (!string.Equals(readBack, "cavernize-ok", StringComparison.Ordinal)) {
                return (SelfCheckStatus.Fail, "CacheDir I/O verification mismatch");
            }
        } catch (Exception ex) {
            return (SelfCheckStatus.Fail, $"CacheDir I/O failed: {ex.Message}");
        }

        long freeDiskMb = -1;
        try {
            StatFs stat = new(context.FilesDir.AbsolutePath);
            freeDiskMb = stat.AvailableBytes / (1024 * 1024);
        } catch {
            // Best effort
        }

        string summary = $"RAM {availMb}/{totalMb} MB (GC={gcMb} MB, lowMem={lowMem}), FreeDisk={freeDiskMb} MB";
        if (lowMem || (freeDiskMb >= 0 && freeDiskMb < 150)) {
            return (SelfCheckStatus.Warn, summary);
        }
        return (SelfCheckStatus.Pass, summary);
    }

    static (SelfCheckStatus, string) CheckLocalizationIntegrity() {
        AndroidStrings s = AndroidStrings.Active;
        string[] criticalKeys = [
            "AppTitle", "DiagnosticsTitle", "CloudLogSectionTitle",
            "SelfCheckSectionTitle", "SelfCheckQuickBtn", "SelfCheckFullBtn",
            "ProAudioCardTitle", "Slot5BCardTitle", "Slot2CCardTitle"
        ];
        List<string> missing = [];
        foreach (string k in criticalKeys) {
            string val = s[k];
            if (string.IsNullOrWhiteSpace(val) || string.Equals(val, k, StringComparison.Ordinal)) {
                missing.Add(k);
            }
        }
        if (missing.Count > 0) {
            return (SelfCheckStatus.Fail, $"Missing localized string keys: {string.Join(", ", missing)}");
        }
        return (SelfCheckStatus.Pass, $"Critical UI strings verified ({criticalKeys.Length}/{criticalKeys.Length})");
    }

    static (SelfCheckStatus, string) CheckAudioRoutes(Context context) {
        if (context?.GetSystemService(Context.AudioService) is not AudioManager am) {
            return (SelfCheckStatus.Warn, "AudioManager service unavailable");
        }
        AudioDeviceInfo[] outputs = am.GetDevices(GetDevicesTargets.Outputs) ?? [];
        List<string> routes = [];
        foreach (AudioDeviceInfo dev in outputs) {
            string sr = dev.GetSampleRates()?.Length > 0
                ? string.Join("/", dev.GetSampleRates().Take(3))
                : "any";
            routes.Add($"{dev.Type}({sr}Hz)");
        }
        PackageManager pm = context.PackageManager;
        bool lowLat = pm != null && pm.HasSystemFeature(PackageManager.FeatureAudioLowLatency);
        bool proAudio = pm != null && pm.HasSystemFeature(PackageManager.FeatureAudioPro);
        string nativeSr = am.GetProperty(AudioManager.PropertyOutputSampleRate) ?? "?";
        string nativeBuf = am.GetProperty(AudioManager.PropertyOutputFramesPerBuffer) ?? "?";
        string routeText = routes.Count > 0 ? string.Join(", ", routes.Take(5)) : "none";
        return (SelfCheckStatus.Pass,
            $"Native={nativeSr}Hz/{nativeBuf}f, LowLat={lowLat}, ProAudio={proAudio}, Outputs[{outputs.Length}]: {routeText}");
    }

    static (SelfCheckStatus, string) CheckBinauralAssets(Context context) {
        int checkedAssets = 0;
        List<string> broken = [];
        foreach (BinauralProfileInfo prof in BinauralProfileService.Profiles) {
            if (string.IsNullOrEmpty(prof.AssetFile)) {
                continue;
            }
            checkedAssets++;
            try {
                using System.IO.Stream stream = context.Assets.Open("hrir/" + prof.AssetFile);
                byte[] head = new byte[16];
                int n = stream.Read(head, 0, head.Length);
                if (n < 6) {
                    broken.Add($"{prof.AssetFile}(truncated)");
                }
            } catch (Exception ex) {
                broken.Add($"{prof.AssetFile}({ex.GetType().Name})");
            }
        }
        if (broken.Count > 0) {
            return (SelfCheckStatus.Fail, $"Broken HRIR/BRIR assets: {string.Join(", ", broken)}");
        }
        return (SelfCheckStatus.Pass, $"All {checkedAssets} bundled laboratory HRIR/BRIR assets readable");
    }

    static (SelfCheckStatus, string) CheckAutoEqAndGuard() {
        AutoEqHeadphoneProfile synth = new() {
            Id = "selfcheck_ref",
            Name = "Synthetic Reference",
            SourceTarget = AutoEqSourceTarget.HarmanOverEar2018,
            Bands = [
                new ParametricBand(ParametricFilterType.LowShelf, 105.0, -2.5, 0.71),
                new ParametricBand(ParametricFilterType.Peaking, 1000.0, 1.8, 1.4),
                new ParametricBand(ParametricFilterType.Peaking, 4200.0, -3.2, 2.2),
                new ParametricBand(ParametricFilterType.HighShelf, 8500.0, -1.5, 0.71)
            ]
        };

        using AutoEqPostProcessingStage stage = new() { UserEnabled = true };
        stage.Configure(48000, 2);
        stage.ApplyConfiguration(synth, AutoEqNeutralTarget.DiffuseFieldNeutral, BinauralCompensationMode.DiffuseFieldCompensatedHrir, enableDoubleEqGuard: true);
        bool guardOnDfc = stage.DoubleEqGuardActive;
        int dfcSections = stage.ActiveSectionCount;

        stage.UpdateBinauralMode(BinauralCompensationMode.RawUncompensatedHrir);
        bool guardOnRaw = stage.DoubleEqGuardActive;
        int rawSections = stage.ActiveSectionCount;

        float[] buf = GenerateSyntheticStereo(48000, 2048, 0.25f);
        SpatialSceneFrame frame = CreateSyntheticFrame(48000, 2048);
        stage.ProcessInterleaved(buf, 0, 2048, 2, frame);
        (int nonFinite, float peak, float rms) = MeasureInterleavedStats(buf);

        if (nonFinite > 0 || rms < 1e-4f || !guardOnRaw || guardOnDfc || rawSections <= 0) {
            return (SelfCheckStatus.Fail,
                $"AutoEq invariant violation: nonFinite={nonFinite}, rms={rms:0.0000}, guardDfc={guardOnDfc}, guardRaw={guardOnRaw}, sec={dfcSections}->{rawSections}");
        }
        return (SelfCheckStatus.Pass,
            $"DFC={dfcSections} biquads (guard=off) -> Raw={rawSections} biquads (Double-EQ Guard=ON), peak={peak:0.000}, rms={rms:0.000}");
    }

    static (SelfCheckStatus, string) CheckAirAbsorptionPhysics() {
        AirAbsorptionFilter nearFilter = new();
        AirAbsorptionFilter farFilter = new();
        nearFilter.ConfigureDirectPath(1.0f, 48000, 20f, 50f);
        farFilter.ConfigureDirectPath(15.0f, 48000, 20f, 50f);

        float[] hiNear = GenerateSineMono(48000, 2048, 12000f, 0.5f);
        float[] hiFar = (float[])hiNear.Clone();
        nearFilter.Process(hiNear, 0, hiNear.Length);
        farFilter.Process(hiFar, 0, hiFar.Length);

        (int nf1, _, float rmsNear) = MeasureMonoStats(hiNear);
        (int nf2, _, float rmsFar) = MeasureMonoStats(hiFar);
        if (nf1 > 0 || nf2 > 0 || rmsNear <= 0f || rmsFar <= 0f) {
            return (SelfCheckStatus.Fail, $"Non-finite or silent output (nf={nf1 + nf2}, near={rmsNear}, far={rmsFar})");
        }
        float attenDb = 20f * MathF.Log10(rmsFar / rmsNear);
        if (attenDb >= -0.05f) {
            return (SelfCheckStatus.Warn, $"Expected HF air attenuation at 15m/12kHz, measured {attenDb:0.00} dB");
        }
        return (SelfCheckStatus.Pass, $"ISO 9613-1 12kHz attenuation (1m->15m @ 20C/50%RH) = {attenDb:0.00} dB, 0 NaN");
    }

    static (SelfCheckStatus, string) CheckTruePeakBrickwall() {
        using TruePeakLimiterPostProcessingStage limiter = new();
        limiter.Configure(48000, 2);
        limiter.ApplyParameters(enabled: true, ceilingDbtp: -1.0f);

        // Deliberately hot +6 dBFS input burst
        float[] hot = GenerateSyntheticStereo(48000, 4096, 2.0f);
        SpatialSceneFrame frame = CreateSyntheticFrame(48000, 4096);
        limiter.ProcessInterleaved(hot, 0, 4096, 2, frame);

        // Inspect post-lookahead steady-state portion (skip first 128 frames of lookahead fill)
        (int nonFinite, float peak, float rms) = MeasureInterleavedStats(hot, startSample: 256);
        float maxAllowed = MathF.Pow(10f, -0.7f / 20f); // Allow tiny tolerance above -1.0 dBTP
        if (nonFinite > 0 || rms < 0.05f || peak > maxAllowed) {
            return (SelfCheckStatus.Fail,
                $"Brickwall violated: nonFinite={nonFinite}, peak={peak:0.000} (limit {maxAllowed:0.000}), GR={limiter.PeakGainReductionDb:0.0} dB");
        }
        return (SelfCheckStatus.Pass,
            $"+6dBFS burst clamped to peak={peak:0.000} ({20f * MathF.Log10(MathF.Max(peak, 1e-6f)):0.00} dBFS), GR={limiter.PeakGainReductionDb:0.0} dB");
    }

    static (SelfCheckStatus, string) CheckVocalChainDsp() {
        LiveVocalChainDsp dsp = new(48000);
        VocalMacroSettings macros = new() {
            Enabled = true,
            MixMatchPct = 80f,
            ReverbPct = 100f,
            DynamicsPct = 100f,
            WarmthPct = 45f,
            VocalLevelDb = 0f,
            EnableSidechainPocketGlue = true,
            EnableNoiseGate = true,
            EnableDeEsser = true
        };
        dsp.Configure(48000, VocalMixDnaProfile.CreateStudioDefault(), macros);

        float[] vocal = GenerateSyntheticStereo(48000, 2048, 0.35f);
        float[] backing = GenerateSyntheticStereo(48000, 2048, 0.25f);
        Stopwatch sw = Stopwatch.StartNew();
        dsp.ProcessStereoBlockZeroGc(vocal, 0, 2048, backing, 0);
        sw.Stop();

        (int nfV, float peakV, float rmsV) = MeasureInterleavedStats(vocal);
        (int nfB, _, float rmsB) = MeasureInterleavedStats(backing);
        double budgetMs = 2048.0 / 48.0; // 42.67 ms audio
        double rtRatio = sw.Elapsed.TotalMilliseconds / budgetMs;

        if (nfV > 0 || nfB > 0 || rmsV < 1e-4f || rmsB < 1e-4f || peakV > 4.0f) {
            return (SelfCheckStatus.Fail, $"VocalChain invalid output: nf={nfV + nfB}, peakV={peakV:0.00}, rmsV={rmsV:0.000}");
        }
        if (rtRatio > 0.45) {
            return (SelfCheckStatus.Warn, $"VocalChain slow: {sw.Elapsed.TotalMilliseconds:0.0}ms for {budgetMs:0.0}ms block ({rtRatio * 100:0}% RT budget)");
        }
        return (SelfCheckStatus.Pass,
            $"Output peak={peakV:0.000}, rms={rmsV:0.000}, CPU={rtRatio * 100:0.0}% of RT budget ({sw.Elapsed.TotalMilliseconds:0.0}ms)");
    }

    static (SelfCheckStatus, string) CheckQuickPairwiseSmoke() =>
        RunPairwiseVectors([PairwiseMatrix[0], PairwiseMatrix[1]]);

    static (SelfCheckStatus, string) CheckPairwiseDspMatrix() =>
        RunPairwiseVectors(PairwiseMatrix);

    static (SelfCheckStatus, string) RunPairwiseVectors(PairwiseDspVector[] vectors) {
        const int sr = 48000;
        const int blockFrames = 1024;
        const int blocksPerVector = 6; // 128 ms synthetic audio per vector
        double audioDurationMs = (blockFrames * blocksPerVector) * 1000.0 / sr;

        AutoEqHeadphoneProfile synthEq = new() {
            Id = "oa_eq",
            Name = "OA Test EQ",
            SourceTarget = AutoEqSourceTarget.HarmanOverEar2018,
            Bands = [
                new ParametricBand(ParametricFilterType.LowShelf, 100.0, -2.0, 0.71),
                new ParametricBand(ParametricFilterType.Peaking, 3200.0, 2.5, 1.8)
            ]
        };

        List<string> failures = [];
        List<string> warnings = [];
        double worstRtRatio = 0.0;
        string worstLabel = "";

        foreach (PairwiseDspVector v in vectors) {
            using AutoEqPostProcessingStage autoEq = new() { UserEnabled = v.AutoEq };
            using HptfNotchPostProcessingStage hptf = new();
            using Iso226LoudnessPostProcessingStage iso226 = new();
            using Bs1770LoudnessPostProcessingStage bs1770 = new();
            using SpatialDialogPostProcessingStage dialog = new();
            using TruePeakLimiterPostProcessingStage limiter = new();

            autoEq.Configure(sr, 2);
            if (v.AutoEq) {
                autoEq.ApplyConfiguration(synthEq, AutoEqNeutralTarget.DiffuseFieldNeutral, BinauralCompensationMode.DiffuseFieldCompensatedHrir);
            }
            hptf.Configure(sr, 2);
            hptf.ApplyParameters(v.HptfNotch, 8000f, -4.5f, 6.0f);
            iso226.Configure(sr, 2);
            iso226.ApplyParameters(v.Iso226, 80f, 65f, trackPlaybackVolume: false);
            bs1770.Configure(sr, 2);
            bs1770.ApplyParameters(v.LoudNorm, respectDialnorm: false);
            dialog.Configure(sr, 2);
            dialog.ApplyParameters(v.DialogEnhancer, 3.5f);
            limiter.Configure(sr, 2);
            limiter.ApplyParameters(enabled: true, ceilingDbtp: v.LimiterCeilingDbtp);

            IPostProcessingStage[] chain = [autoEq, hptf, iso226, bs1770, dialog, limiter];
            SpatialSceneFrame frame = CreateSyntheticFrame(sr, blockFrames);

            float[] block = GenerateSyntheticStereo(sr, blockFrames, 0.30f);
            int totalNonFinite = 0;
            float maxPeak = 0f;
            float lastRms = 0f;

            Stopwatch sw = Stopwatch.StartNew();
            for (int b = 0; b < blocksPerVector; b++) {
                float[] work = (float[])block.Clone();
                for (int s = 0; s < chain.Length; s++) {
                    chain[s].ProcessInterleaved(work, 0, blockFrames, 2, frame);
                }
                (int nf, float p, float r) = MeasureInterleavedStats(work);
                totalNonFinite += nf;
                if (p > maxPeak) maxPeak = p;
                lastRms = r;
            }
            sw.Stop();

            double rtRatio = sw.Elapsed.TotalMilliseconds / audioDurationMs;
            if (rtRatio > worstRtRatio) {
                worstRtRatio = rtRatio;
                worstLabel = v.Label;
            }

            if (totalNonFinite > 0) {
                failures.Add($"{v.Label}(NaN/Inf={totalNonFinite})");
            } else if (lastRms < 1e-4f) {
                failures.Add($"{v.Label}(silent rms={lastRms:0.00000})");
            } else if (maxPeak > 1.25f) {
                failures.Add($"{v.Label}(peak blowup={maxPeak:0.00})");
            } else if (rtRatio > 0.35) {
                warnings.Add($"{v.Label}(CPU={rtRatio * 100:0}%)");
            }
        }

        if (failures.Count > 0) {
            return (SelfCheckStatus.Fail, $"{failures.Count}/{vectors.Length} pairwise vectors failed: {string.Join(", ", failures)}");
        }
        if (warnings.Count > 0) {
            return (SelfCheckStatus.Warn, $"{vectors.Length}/{vectors.Length} clean audio, high CPU on: {string.Join(", ", warnings)}");
        }
        return (SelfCheckStatus.Pass,
            $"{vectors.Length}/{vectors.Length} orthogonal DSP vectors clean (0 NaN, valid RMS/peak, worst CPU={worstRtRatio * 100:0.0}% on {worstLabel})");
    }

    static (SelfCheckStatus, string) CheckWavRoundTrip(Context context) {
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "selfcheck_tmp");
        Directory.CreateDirectory(dir);
        string wavPath = Path.Combine(dir, "roundtrip.wav");
        try {
            const int sr = 48000;
            const int frames = 4800; // 100 ms
            float[] src = GenerateSyntheticStereo(sr, frames, 0.45f);

            using (RIFFWaveWriter writer = new(wavPath, 2, frames, sr, BitDepth.Int24)) {
                writer.WriteHeader();
                writer.WriteBlock(src, 0, src.Length);
            }

            using RIFFWaveReader reader = new(wavPath);
            reader.ReadHeader();
            if (reader.ChannelCount != 2 || reader.SampleRate != sr || reader.Length != frames) {
                return (SelfCheckStatus.Fail,
                    $"WAV header mismatch: ch={reader.ChannelCount}, sr={reader.SampleRate}, len={reader.Length}");
            }
            float[] dst = new float[src.Length];
            reader.ReadBlock(dst, 0, dst.Length);
            float maxErr = 0f;
            for (int i = 0; i < src.Length; i++) {
                float e = MathF.Abs(src[i] - dst[i]);
                if (e > maxErr) maxErr = e;
            }
            if (maxErr > 0.002f) {
                return (SelfCheckStatus.Fail, $"WAV reconstruction error too high: maxErr={maxErr:0.00000}");
            }
            return (SelfCheckStatus.Pass, $"24-bit 48kHz stereo WAV round-trip OK (maxErr={maxErr:0.000000})");
        } finally {
            TryDeleteFile(wavPath);
        }
    }

    static (SelfCheckStatus, string) CheckFFmpegFlacRoundTrip(Context context, AppSettingsStore store) {
        string bin = FFmpegInstallerService.ResolveExistingFFmpegPath(context, store);
        if (string.IsNullOrEmpty(bin) || !File.Exists(bin)) {
            return (SelfCheckStatus.Skip, "FFmpeg unavailable");
        }
        string dir = Path.Combine(context.CacheDir.AbsolutePath, "selfcheck_tmp");
        Directory.CreateDirectory(dir);
        string wavPath = Path.Combine(dir, "ff_in.wav");
        string flacPath = Path.Combine(dir, "ff_out.flac");
        try {
            const int sr = 48000;
            const int frames = 4800;
            float[] src = GenerateSyntheticStereo(sr, frames, 0.40f);
            using (RIFFWaveWriter writer = new(wavPath, 2, frames, sr, BitDepth.Int24)) {
                writer.WriteHeader();
                writer.WriteBlock(src, 0, src.Length);
            }

            ProcessStartInfo psi = new(bin, $"-y -v error -i \"{wavPath}\" -c:a flac \"{flacPath}\"") {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            (int exit, _, string stderr) = FFmpegInstallerService.RunProcessSafe(psi, timeoutMs: 12000);
            if (exit != 0 || !File.Exists(flacPath)) {
                return (SelfCheckStatus.Fail, $"FFmpeg WAV->FLAC failed (exit={exit}): {(stderr ?? "").Trim()}");
            }
            byte[] hdr = new byte[4];
            using (FileStream fs = File.OpenRead(flacPath)) {
                _ = fs.Read(hdr, 0, 4);
            }
            string magic = System.Text.Encoding.ASCII.GetString(hdr);
            long flacBytes = new FileInfo(flacPath).Length;
            if (!string.Equals(magic, "fLaC", StringComparison.Ordinal) || flacBytes < 100) {
                return (SelfCheckStatus.Fail, $"Invalid FLAC output (magic='{magic}', size={flacBytes}B)");
            }
            return (SelfCheckStatus.Pass, $"WAV->FLAC valid ('fLaC', {flacBytes} bytes)");
        } finally {
            TryDeleteFile(wavPath);
            TryDeleteFile(flacPath);
        }
    }

    static (SelfCheckStatus, string) CheckCloudSyncReadiness(AppSettingsStore store) {
        if (store == null || !CloudLogSyncService.HasStoredToken(store)) {
            return (SelfCheckStatus.Skip, "GitHub Cloud Log Sync token not configured yet");
        }
        if (!CloudLogSyncService.IsConfigured(store)) {
            return (SelfCheckStatus.Warn, $"Cloud Sync disabled or invalid repo ('{store.CloudLogRepo}')");
        }
        (string branch, string logPath, _) = CloudLogSyncService.ResolveTarget(store);
        return (SelfCheckStatus.Pass, $"Configured -> {store.CloudLogRepo}@{branch} ({logPath})");
    }

    static SpatialSceneFrame CreateSyntheticFrame(int sampleRate, int frameCount) => new() {
        SampleRate = sampleRate,
        FrameCount = frameCount,
        OutputFrameCount = frameCount,
        OutputChannels = 2,
        IsBinauralActive = true,
        Metadata = SpatialProgramMetadata.CreateDefault(),
        Telemetry = SoundfieldTelemetry.Empty()
    };

    static float[] GenerateSyntheticStereo(int sampleRate, int frames, float amplitude) {
        float[] buf = new float[frames * 2];
        float invSr = 1f / sampleRate;
        for (int i = 0; i < frames; i++) {
            float t = i * invSr;
            float l = 0.45f * MathF.Sin(2f * MathF.PI * 440f * t)
                    + 0.30f * MathF.Sin(2f * MathF.PI * 1000f * t)
                    + 0.15f * MathF.Sin(2f * MathF.PI * 3500f * t)
                    + 0.10f * MathF.Sin(2f * MathF.PI * 9000f * t);
            float r = 0.45f * MathF.Sin(2f * MathF.PI * 440f * t + 0.35f)
                    + 0.30f * MathF.Sin(2f * MathF.PI * 1000f * t + 0.15f)
                    + 0.15f * MathF.Sin(2f * MathF.PI * 3500f * t + 0.65f)
                    + 0.10f * MathF.Sin(2f * MathF.PI * 9000f * t + 0.90f);
            buf[i * 2] = l * amplitude;
            buf[i * 2 + 1] = r * amplitude;
        }
        return buf;
    }

    static float[] GenerateSineMono(int sampleRate, int frames, float freqHz, float amplitude) {
        float[] buf = new float[frames];
        float w = 2f * MathF.PI * freqHz / sampleRate;
        for (int i = 0; i < frames; i++) {
            buf[i] = MathF.Sin(w * i) * amplitude;
        }
        return buf;
    }

    static (int nonFinite, float peak, float rms) MeasureInterleavedStats(float[] buf, int startSample = 0) {
        int nf = 0;
        float peak = 0f;
        double sumSq = 0.0;
        int count = 0;
        for (int i = Math.Max(0, startSample); i < buf.Length; i++) {
            float v = buf[i];
            if (!float.IsFinite(v)) {
                nf++;
                continue;
            }
            float a = MathF.Abs(v);
            if (a > peak) peak = a;
            sumSq += (double)v * v;
            count++;
        }
        float rms = count > 0 ? MathF.Sqrt((float)(sumSq / count)) : 0f;
        return (nf, peak, rms);
    }

    static (int nonFinite, float peak, float rms) MeasureMonoStats(float[] buf) =>
        MeasureInterleavedStats(buf, 0);

    static void TryDeleteFile(string path) {
        try {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Best effort cleanup
        }
    }
}
