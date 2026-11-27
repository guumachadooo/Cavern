using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.Content;
using Android.OS;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Hardware capability probe result for Qualcomm Hexagon HTP / QNN NPU on the running Android device.
/// </summary>
public sealed class QnnHardwareCapabilitySnapshot {
    /// <summary>True when a Qualcomm Snapdragon SoC is detected.</summary>
    public bool IsQualcommSoc { get; init; }

    /// <summary>True when W^X-safe <c>libQnnHtp.so</c> is bundled in <c>NativeLibraryDir</c> and ready for ORT QNN EP.</summary>
    public bool BundledQnnHtpReady { get; init; }

    /// <summary>True when read-only vendor Hexagon CDSP/HTP libraries (e.g. <c>libcdsprpc.so</c>, <c>libSnpeHtpV79Stub.so</c>) are present.</summary>
    public bool VendorHexagonCdspDetected { get; init; }

    /// <summary>Resolved SoC label (e.g. <c>QTI SM8750</c> on Snapdragon 8 Elite).</summary>
    public string SocLabel { get; init; } = "";

    /// <summary>Android <see cref="Build.Fingerprint"/> used to invalidate stale QNN context binaries after OS OTAs.</summary>
    public string BuildFingerprint { get; init; } = "";

    /// <summary>Hexagon HTP stub revision detected in <c>/vendor/lib64</c> (e.g. <c>V79</c>), or empty.</summary>
    public string VendorHtpVersionTag { get; init; } = "";

    /// <summary>Compact human-readable status summary.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>
/// Wave E (W53.5 / B12) SoC-keyed QNN HTP context binary cache manager &amp; mobile quantized model cache guard.
/// Keys compiled Hexagon HTP context binaries strictly on <c>SHA-256(Build.Fingerprint | SocModel | OrtVersion | QnnBackendSig | ModelSha256 | QuantMode)</c>,
/// automatically purging stale context binaries after Android/One UI OTA updates or ORT upgrades.
/// </summary>
public static class OnnxQnnContextCacheManager {
    const string QnnCacheFolderName = "qnn_soc_ctx_cache";
    const string QuantCacheFolderName = "onnx_mobile_quant_cache";
    const long MaxQnnCacheBytes = 512L * 1024L * 1024L;
    const long MaxQuantCacheBytes = 1024L * 1024L * 1024L;
    const int MinValidContextBytes = 256;

    static readonly object CacheLock = new();
    static volatile QnnHardwareCapabilitySnapshot cachedCapability;

    sealed class ContextCacheMeta {
        public string BuildFingerprint { get; set; } = "";
        public string SocLabel { get; set; } = "";
        public string OrtVersion { get; set; } = "";
        public string ModelSha256 { get; set; } = "";
        public string QuantMode { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }

    /// <summary>
    /// Computes the deterministic 24-hex-char SoC/OS/ORT/model cache key so context binaries are NEVER reused
    /// across Android OTA fingerprint changes, ORT upgrades, QNN backend changes, or model weight updates.
    /// </summary>
    public static string ComputeSocContextKey(
        string buildFingerprint,
        string socManufacturer,
        string socModel,
        string ortVersion,
        string qnnBackendSignature,
        string modelSha256,
        NeuralQuantizationMode quantMode,
        int graphOptLevel = 3) {
        string canonical = string.Join("|",
            (buildFingerprint ?? "").Trim(),
            (socManufacturer ?? "").Trim(),
            (socModel ?? "").Trim(),
            (ortVersion ?? "").Trim(),
            (qnnBackendSignature ?? "").Trim(),
            (modelSha256 ?? "").Trim().ToLowerInvariant(),
            quantMode.ToString(),
            graphOptLevel.ToString());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).Substring(0, 24).ToLowerInvariant();
    }

    /// <summary>
    /// Probes W^X <c>NativeLibraryDir</c> and read-only <c>/vendor/lib64</c> Hexagon CDSP capabilities once per process.
    /// </summary>
    public static QnnHardwareCapabilitySnapshot ProbeDeviceQnnCapabilities(Context context) {
        QnnHardwareCapabilitySnapshot snap = cachedCapability;
        if (snap != null) {
            return snap;
        }
        bool isQualcomm = OnnxRuntimeInstallerService.IsQualcommSnapdragonDevice();
        bool bundledQnn = context != null && OnnxRuntimeInstallerService.IsQnnAvailableOnDisk(context);
        string fp = Build.Fingerprint ?? "unknown";
        string socMfg = OperatingSystem.IsAndroidVersionAtLeast(31) ? (Build.SocManufacturer ?? "") : "";
        string socMod = OperatingSystem.IsAndroidVersionAtLeast(31) ? (Build.SocModel ?? "") : "";
        string socLabel = string.IsNullOrWhiteSpace(socMod)
            ? (Build.Hardware ?? "ARM64")
            : $"{socMfg} {socMod}".Trim();

        bool vendorCdsp = false;
        string htpVer = "";
        try {
            if (Directory.Exists("/vendor/lib64")) {
                if (File.Exists("/vendor/lib64/libcdsprpc.so") || File.Exists("/vendor/lib64/libsnap_qnn.so")) {
                    vendorCdsp = true;
                }
                string[] knownStubs = ["V81", "V79", "V75", "V73", "V69"];
                for (int i = 0; i < knownStubs.Length; i++) {
                    string tag = knownStubs[i];
                    if (File.Exists($"/vendor/lib64/libSnpeHtp{tag}Stub.so") ||
                        File.Exists($"/vendor/lib64/libQnnHtp{tag}Stub.so")) {
                        vendorCdsp = true;
                        htpVer = tag;
                        break;
                    }
                }
            }
        } catch {
            // SELinux may restrict listing /vendor/lib64 on some OEMs; never fail startup
        }

        string summary = bundledQnn
            ? $"QNN HTP Ready ({socLabel}{(string.IsNullOrEmpty(htpVer) ? "" : $" · HTP {htpVer}")})"
            : vendorCdsp
                ? $"Hexagon CDSP {(string.IsNullOrEmpty(htpVer) ? "" : htpVer + " ")}({socLabel}) · WebGPU/KleidiAI"
                : $"{socLabel} · WebGPU/KleidiAI";

        snap = new QnnHardwareCapabilitySnapshot {
            IsQualcommSoc = isQualcomm,
            BundledQnnHtpReady = bundledQnn,
            VendorHexagonCdspDetected = vendorCdsp,
            SocLabel = socLabel,
            BuildFingerprint = fp,
            VendorHtpVersionTag = htpVer,
            Summary = summary
        };
        cachedCapability = snap;
        return snap;
    }

    /// <summary>
    /// Resolves the SoC-keyed QNN context binary path for <paramref name="modelPath"/> and checks whether
    /// a valid, fingerprint-matched compiled context binary already exists on disk.
    /// </summary>
    public static (string contextPath, bool hasValidCache, string socKey) ResolveContextBinaryPath(
        Context context,
        string modelPath,
        string ortVersion,
        NeuralQuantizationMode quantMode) {
        if (context == null || string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) {
            return ("", false, "");
        }
        try {
            string cacheDir = GetQnnCacheDir(context);
            Directory.CreateDirectory(cacheDir);

            QnnHardwareCapabilitySnapshot cap = ProbeDeviceQnnCapabilities(context);
            string qnnLibPath = OnnxRuntimeInstallerService.GetManagedQnnHtpPath(context);
            string qnnSig = File.Exists(qnnLibPath) ? $"{new FileInfo(qnnLibPath).Length}:{cap.VendorHtpVersionTag}" : cap.VendorHtpVersionTag;
            string modelSha = ComputeFastModelFingerprint(modelPath);
            string socMfg = OperatingSystem.IsAndroidVersionAtLeast(31) ? (Build.SocManufacturer ?? "") : "";
            string socMod = OperatingSystem.IsAndroidVersionAtLeast(31) ? (Build.SocModel ?? "") : "";

            string socKey = ComputeSocContextKey(
                cap.BuildFingerprint,
                socMfg,
                socMod,
                ortVersion,
                qnnSig,
                modelSha,
                quantMode);

            string baseName = SanitizeFileToken(Path.GetFileNameWithoutExtension(modelPath));
            string ctxPath = Path.Combine(cacheDir, $"{baseName}_{socKey}.ctx.onnx");
            string metaPath = ctxPath + ".meta.json";

            lock (CacheLock) {
                PurgeStaleFingerprintEntriesLocked(cacheDir, cap.BuildFingerprint, ortVersion);

                if (File.Exists(ctxPath)) {
                    FileInfo fi = new(ctxPath);
                    bool metaValid = IsMetaMatching(metaPath, cap.BuildFingerprint, ortVersion, modelSha);
                    if (fi.Length >= MinValidContextBytes && metaValid) {
                        try { File.SetLastWriteTimeUtc(ctxPath, DateTime.UtcNow); } catch { }
                        return (ctxPath, true, socKey);
                    }
                    TryDeleteQuietly(ctxPath);
                    TryDeleteQuietly(metaPath);
                }
            }

            return (ctxPath, false, socKey);
        } catch (Exception ex) {
            AppLogger.Warn($"OnnxQnnContextCacheManager.ResolveContextBinaryPath failed: {ex.Message}");
            return ("", false, "");
        }
    }

    /// <summary>
    /// Stamps sidecar SoC/OS/ORT fingerprint metadata after ORT QNN EP compiles a context binary and enforces cache cap.
    /// </summary>
    public static void RecordCompiledContextMetadata(
        Context context,
        string contextPath,
        string modelPath,
        string ortVersion,
        NeuralQuantizationMode quantMode) {
        if (context == null || string.IsNullOrWhiteSpace(contextPath) || !File.Exists(contextPath)) {
            return;
        }
        try {
            FileInfo fi = new(contextPath);
            if (fi.Length < MinValidContextBytes) {
                TryDeleteQuietly(contextPath);
                return;
            }
            QnnHardwareCapabilitySnapshot cap = ProbeDeviceQnnCapabilities(context);
            ContextCacheMeta meta = new() {
                BuildFingerprint = cap.BuildFingerprint,
                SocLabel = cap.SocLabel,
                OrtVersion = ortVersion ?? "",
                ModelSha256 = ComputeFastModelFingerprint(modelPath),
                QuantMode = quantMode.ToString(),
                CreatedUtc = DateTime.UtcNow.ToString("O")
            };
            string json = JsonSerializer.Serialize(meta);
            string metaPath = contextPath + ".meta.json";
            string tmp = metaPath + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, metaPath, overwrite: true);

            lock (CacheLock) {
                EvictDirectoryLruLocked(GetQnnCacheDir(context), MaxQnnCacheBytes);
            }
        } catch {
            // Best-effort metadata stamp
        }
    }

    /// <summary>
    /// Resolves (and lazily builds once on disk) a mobile quantized model artifact (FP16 or W8A16) for <paramref name="sourceModelPath"/>.
    /// Returns the original <paramref name="sourceModelPath"/> cleanly if optimization is skipped or not applicable.
    /// </summary>
    public static string ResolveOrBuildQuantizedModelPath(
        Context context,
        string sourceModelPath,
        NeuralQuantizationMode effectiveMode,
        out string activePrecisionTag) {
        activePrecisionTag = "FP32";
        if (context == null || string.IsNullOrWhiteSpace(sourceModelPath) || !File.Exists(sourceModelPath)) {
            return sourceModelPath;
        }
        if (effectiveMode == NeuralQuantizationMode.Fp32) {
            return sourceModelPath;
        }

        try {
            string cacheDir = GetQuantCacheDir(context);
            Directory.CreateDirectory(cacheDir);

            string modelFp = ComputeFastModelFingerprint(sourceModelPath);
            string baseName = SanitizeFileToken(Path.GetFileNameWithoutExtension(sourceModelPath));
            string suffix = effectiveMode == NeuralQuantizationMode.W8A16 ? "w8a16" : "fp16";
            string targetPath = Path.Combine(cacheDir, $"{baseName}_{modelFp}_{suffix}.onnx");

            if (File.Exists(targetPath) && new FileInfo(targetPath).Length >= MinValidContextBytes) {
                try { File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow); } catch { }
                activePrecisionTag = effectiveMode == NeuralQuantizationMode.W8A16 ? "W8A16" : "FP16";
                return targetPath;
            }

            if (effectiveMode == NeuralQuantizationMode.W8A16) {
                OnnxW8A16QuantizationResult qRes = OnnxMobileQuantizer.TryConvertFileToW8A16(sourceModelPath, targetPath);
                if (qRes.Success && File.Exists(targetPath)) {
                    AppLogger.Info($"Wave E W8A16 cached '{Path.GetFileName(targetPath)}': {qRes.Message}");
                    lock (CacheLock) {
                        EvictDirectoryLruLocked(cacheDir, MaxQuantCacheBytes);
                    }
                    activePrecisionTag = "W8A16";
                    return targetPath;
                }
                AppLogger.Info($"Wave E W8A16 skipped for '{Path.GetFileName(sourceModelPath)}': {qRes.Message}");
                return sourceModelPath;
            }

            OnnxFp16ConversionResult fpRes = OnnxFp16ModelConverter.TryConvertFileToFp16(sourceModelPath, targetPath);
            if (fpRes.Success && File.Exists(targetPath)) {
                AppLogger.Info($"Wave E FP16 cached '{Path.GetFileName(targetPath)}': {fpRes.InputBytes / (1024.0 * 1024.0):F1} MB -> {fpRes.OutputBytes / (1024.0 * 1024.0):F1} MB");
                lock (CacheLock) {
                    EvictDirectoryLruLocked(cacheDir, MaxQuantCacheBytes);
                }
                activePrecisionTag = "FP16";
                return targetPath;
            }
            if (fpRes.SkippedAlreadyFp16) {
                activePrecisionTag = "FP16";
            }
            return sourceModelPath;
        } catch (Exception ex) {
            AppLogger.Warn($"Wave E mobile quantization skipped for '{Path.GetFileName(sourceModelPath)}': {ex.Message}");
            return sourceModelPath;
        }
    }

    /// <summary>Returns current on-disk QNN context + mobile quantization cache stats (file count and total bytes).</summary>
    public static (int files, long totalBytes) GetCacheStats(Context context) {
        if (context == null) {
            return (0, 0L);
        }
        int count = 0;
        long bytes = 0L;
        string[] dirs = [GetQnnCacheDir(context), GetQuantCacheDir(context)];
        for (int i = 0; i < dirs.Length; i++) {
            try {
                if (!Directory.Exists(dirs[i])) {
                    continue;
                }
                foreach (string f in Directory.GetFiles(dirs[i])) {
                    if (f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) {
                        count++;
                        bytes += new FileInfo(f).Length;
                    }
                }
            } catch { }
        }
        return (count, bytes);
    }

    /// <summary>Clears all cached QNN context binaries and mobile quantized ONNX models safely.</summary>
    public static int ClearAllCaches(Context context) {
        if (context == null) {
            return 0;
        }
        int deleted = 0;
        lock (CacheLock) {
            string[] dirs = [GetQnnCacheDir(context), GetQuantCacheDir(context)];
            for (int i = 0; i < dirs.Length; i++) {
                try {
                    if (!Directory.Exists(dirs[i])) {
                        continue;
                    }
                    foreach (string f in Directory.GetFiles(dirs[i])) {
                        if (TryDeleteQuietly(f)) {
                            deleted++;
                        }
                    }
                } catch { }
            }
        }
        return deleted;
    }

    static string GetQnnCacheDir(Context context) =>
        Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), QnnCacheFolderName);

    static string GetQuantCacheDir(Context context) =>
        Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), QuantCacheFolderName);

    static string ComputeFastModelFingerprint(string modelPath) {
        FileInfo fi = new(modelPath);
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] meta = Encoding.UTF8.GetBytes($"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:");
        sha.AppendData(meta);
        byte[] buf = new byte[65536];
        using FileStream fs = File.OpenRead(modelPath);
        int headRead = fs.Read(buf, 0, buf.Length);
        if (headRead > 0) {
            sha.AppendData(buf, 0, headRead);
        }
        if (fi.Length > buf.Length * 2) {
            fs.Seek(-buf.Length, SeekOrigin.End);
            int tailRead = fs.Read(buf, 0, buf.Length);
            if (tailRead > 0) {
                sha.AppendData(buf, 0, tailRead);
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset()).Substring(0, 16).ToLowerInvariant();
    }

    static bool IsMetaMatching(string metaPath, string expectedFp, string expectedOrt, string expectedModelSha) {
        try {
            if (!File.Exists(metaPath)) {
                return false;
            }
            ContextCacheMeta meta = JsonSerializer.Deserialize<ContextCacheMeta>(File.ReadAllText(metaPath, Encoding.UTF8));
            if (meta == null) {
                return false;
            }
            return string.Equals(meta.BuildFingerprint, expectedFp, StringComparison.Ordinal) &&
                   string.Equals(meta.OrtVersion, expectedOrt ?? "", StringComparison.Ordinal) &&
                   string.Equals(meta.ModelSha256, expectedModelSha, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    static void PurgeStaleFingerprintEntriesLocked(string cacheDir, string activeFp, string activeOrt) {
        try {
            foreach (string metaPath in Directory.GetFiles(cacheDir, "*.meta.json")) {
                try {
                    ContextCacheMeta meta = JsonSerializer.Deserialize<ContextCacheMeta>(File.ReadAllText(metaPath, Encoding.UTF8));
                    bool stale = meta == null ||
                                 !string.Equals(meta.BuildFingerprint, activeFp, StringComparison.Ordinal) ||
                                 !string.Equals(meta.OrtVersion, activeOrt ?? "", StringComparison.Ordinal);
                    if (stale) {
                        string ctxPath = metaPath.Substring(0, metaPath.Length - ".meta.json".Length);
                        TryDeleteQuietly(ctxPath);
                        TryDeleteQuietly(metaPath);
                    }
                } catch {
                    TryDeleteQuietly(metaPath);
                }
            }
        } catch { }
    }

    static void EvictDirectoryLruLocked(string dir, long maxBytes) {
        try {
            if (!Directory.Exists(dir)) {
                return;
            }
            List<FileInfo> files = Directory.GetFiles(dir, "*.onnx").Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            int idx = 0;
            while (total > maxBytes && idx < files.Count) {
                FileInfo victim = files[idx++];
                total -= victim.Length;
                TryDeleteQuietly(victim.FullName);
                TryDeleteQuietly(victim.FullName + ".meta.json");
            }
        } catch { }
    }

    static bool TryDeleteQuietly(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
                return true;
            }
        } catch { }
        return false;
    }

    static string SanitizeFileToken(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return "model";
        }
        StringBuilder sb = new(raw.Length);
        for (int i = 0; i < raw.Length; i++) {
            char c = raw[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }
        return sb.ToString();
    }
}
