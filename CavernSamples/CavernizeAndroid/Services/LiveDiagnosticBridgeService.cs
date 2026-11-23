using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Security.Keystore;

using CavernizeAndroid.Audio;

using Java.Security;

using Javax.Crypto;
using Javax.Crypto.Spec;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Live UI/Engine host interface implemented by <see cref="MainActivity"/> so Live Bridge v2 can
/// inspect app state, control playback/tabs/UI, query library, and capture screenshots safely
/// while gracefully supporting headless background mode when the Activity is closed.
/// </summary>
public interface ILiveBridgeAppHost {
    Task<string> GetAppStateJsonAsync();
    Task<string> GetDspTelemetryJsonAsync();
    Task<string> QueryLibraryJsonAsync(string query, int limit);
    Task<string> DumpUiTreeJsonAsync(int maxDepth);
    Task<string> CaptureUiScreenshotJsonAsync(int maxWidth, int quality);
    Task<(bool ok, string message)> SwitchTabAsync(int tabIndex, string tabName);
    Task<(bool ok, string message)> PlaybackControlAsync(string action, double? value);
    Task<(bool ok, string message)> OpenFileAsync(string path, bool autoPlay, int? switchToTab);
    Task<(bool ok, string message)> ExecuteUiActionAsync(string action, string message);
    Task<(bool ok, string message)> ClickUiElementAsync(string textQuery, int index);
    Task<(bool ok, string json)> UiInteractAsync(JsonElement argsEl);
    Task<(bool ok, string output)> ExecuteOnUiThreadReflectAsync(Func<(bool ok, string output)> fn);
    object GetReflectionRoot(string rootName);
    Task<(List<SelfCheckItem> Checks, List<UiPerfSample> PerfSamples)> RunLiveUiSelfAuditAsync(Action<string> onProgress, CancellationToken ct);
    Task ApplySettingsChangedAsync();
}

/// <summary>
/// Authenticated wake request pushed by a paired AI client via Cloudflare Worker.
/// </summary>
public sealed class LiveBridgeWakeRequest {
    public string WakeId { get; init; } = "";
    public long TimestampUnix { get; init; }
    public int RequestedTier { get; init; } = 1;
    public int DurationSeconds { get; init; } = 1800;
    public string Reason { get; init; } = "";
    public string Requester { get; init; } = "AI Assistant";
    public string Signature { get; init; } = "";
}

/// <summary>
/// Zero-secret ephemeral P-256 ECDH Smart Knock request received via Cloudflare Hub v5.
/// Requires on-device SAS visual verification + native BiometricPrompt approval.
/// </summary>
public sealed class LiveBridgeKnockRequest {
    public string KnockReqId { get; init; } = "";
    public string ClientPubKeyB64 { get; init; } = "";
    public string SasCode { get; init; } = "";
    public string RequestedScope { get; init; } = "session";
    public int RequestedTier { get; init; } = 2;
    public int DurationSeconds { get; init; } = 3600;
    public string Reason { get; init; } = "";
    public string Requester { get; init; } = "AI Studio";
}

/// <summary>
/// Live Bridge v2 connecting Cavernize Android to the user's free Cloudflare Worker relay
/// (<c>cloudflare_live_relay_worker.js</c>).
///
/// Security architecture:
/// - 256-bit Device Pairing Secret stored strictly encrypted in Android Keystore (AES-256-GCM).
/// - Background Standby Wake verifies HMAC-SHA256 signature + timestamp skew (&lt;=180s) + nonce dedup,
///   then requires native Android BiometricPrompt (fingerprint/face/device credential) before opening
///   a session and returning the ephemeral token end-to-end encrypted (Encrypt-then-MAC: SHA256-CTR + HMAC-SHA256).
/// - Flexible extensible Tier 0 / Tier 1 / Tier 2 capability registry (~30 commands + batch + dynamic
///   reflection over non-secret AppSettingsStore properties), with strict secret blocklisting,
///   canonical path confinement, W^X NativeLibraryDir-only binary probing, and AppLogger scrubbing.
/// </summary>
public static class LiveDiagnosticBridgeService {
    public const int DefaultSessionDurationSeconds = 1800; // 30 minutes (configurable 15..60m)
    const int PollIntervalMs = 2000;
    const int MaxResponseChars = 280_000;
    const int MaxBatchCommands = 15;
    const string PairKeyAlias = "cavernize_live_bridge_pair_v1";
    const string KeystorePrefix = "ks2:";

    static readonly char[] CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();
    static readonly HttpClient http = BuildClient();
    static readonly object stateLock = new();
    static readonly object nonceLock = new();
    static readonly Queue<string> recentWakeNoncesQueue = new();
    static readonly HashSet<string> recentWakeNonces = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, LiveBridgeWakeRequest> pendingWakeRequests = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, LiveBridgeKnockRequest> pendingKnockRequests = new(StringComparer.Ordinal);

    static readonly string[] SecretSettingTokens = [
        "Encrypted", "Token", "Secret", "Password", "ApiKey", "Credential"
    ];

    static WeakReference<ILiveBridgeAppHost> hostRef;
    static CancellationTokenSource sessionCts;
    static string activeSessionCode = "";
    static string activeRelayUrl = "";
    static int activeMaxTier = 1;
    static bool activeBiometricVerified;
    static DateTime sessionExpiresUtc = DateTime.MinValue;
    static int commandsHandled;
    static string lastEventText = "";

    sealed record CommandDescriptor(string Name, int Tier, string Summary, string ArgsSchema);

    static readonly CommandDescriptor[] CapabilitiesCatalog = [
        // Tier 0: Read-only diagnostics & deep introspection
        new("ping", 0, "Online heartbeat, version, device, tier, log counts & host status", "{}"),
        new("capabilities", 0, "List all supported commands, required tiers & argument schemas", "{}"),
        new("help", 0, "Alias for capabilities", "{}"),
        new("quick_check", 0, "Run L1-L4 fast on-device self-diagnostic checkup", "{}"),
        new("full_check", 0, "Run L1-L5 full on-device self-diagnostic checkup incl. codecs", "{}"),
        new("summary", 0, "Export structured diagnostic session summary JSON", "{}"),
        new("log_tail", 0, "Read recent Cavernize log lines", "{lines?:20..1000, errorsOnly?:bool}"),
        new("log_search", 0, "Search recent log lines by case-insensitive keyword", "{query:string, maxMatches?:1..200}"),
        new("audio_routes", 0, "Inspect Android AudioManager outputs/inputs & Pro-Audio NDK bridge", "{}"),
        new("memory", 0, "Inspect Android RAM, lowMemory pressure & managed GC heap", "{}"),
        new("app_state", 0, "Inspect live MainActivity tab, loaded track, playback state & engines", "{}"),
        new("dsp_telemetry", 0, "Inspect active Cavern DSP/spatial/head-tracking/neural status", "{}"),
        new("settings_get", 0, "Read one or all non-secret AppSettingsStore properties via reflection", "{key?:string, filter?:string}"),
        new("library_query", 0, "Query cached local music library tracks", "{query?:string, limit?:1..100}"),
        new("files_list", 0, "List files inside confined app/log directories", "{scope?:'logs'|'files'|'cache'|'downloads'|'native', subdir?:string}"),
        new("file_read", 0, "Read bounded sanitized text snippet from confined app/log file", "{path:string, maxChars?:100..60000, tail?:bool}"),
        new("native_status", 0, "Verify bundled W^X ARM64 NativeLibraryDir binaries & sizes", "{}"),
        new("ui_tree", 0, "Dump live Android View hierarchy of active MainActivity window", "{maxDepth?:1..10}"),
        new("acoustic_audit", 0, "Run L11 Intelligent Acoustic Differential Oracle (HRTF/Upmix/LGSR/ISM/UserConfig fingerprints)", "{quick?:bool, format?:'json'|'markdown'}"),
        new("batch", 0, "Execute up to 15 sequential commands in one round-trip (enforces each command's tier)", "{commands:[{cmd:string, args?:object}]}"),

        // Tier 1: Interactive App Control, QA & Total-App Live Audit
        new("ultra_check", 1, "Run 12-layer Total + Acoustic Differential + Live UI/Modal/Controls/Perf-Jank self-audit", "{}"),
        new("ui_audit", 1, "Run L8-L10+L12 Live UI Tabs, 27-Modal Smoke, Controls & Choreographer Perf/Jank audit", "{}"),
        new("ui_perf_audit", 1, "Run L12 Choreographer UI open-latency, main-thread block & dropped-frame jank profile ranked slowest-first", "{}"),
        new("acoustic_probe", 1, "Run custom on-demand differential A/B psychoacoustic probe with arbitrary HRTF/ISM/MagLS/ITD/Yaw/Upmix/LGSR flags", "{profileId?:string, minPhase?:bool, magLsOrder?:0..3, bioItd?:bool, headWidthCm?:number, manualItdUs?:number, ismRoom?:bool, ismPreset?:0..4, ismLevel?:number, nearField?:bool, nearFieldDistM?:number, yawDeg?:number, pitchDeg?:number, sourceAzDeg?:number, sourceElDeg?:number, sampleRate?:number, upmix?:bool, lgsr?:bool, compareDirect?:bool, signal?:'white'|'pink'|'sine1k'|'impulse'}"),
        new("settings_set", 1, "Update non-secret AppSettingsStore property via reflection & refresh UI", "{key:string, value:any} or {values:{k:v}}"),
        new("tab_switch", 1, "Switch MainActivity bottom navigation tab", "{index?:0..4, name?:'player'|'room'|'studio'|'stems'|'settings'}"),
        new("playback_control", 1, "Control real-time player playback", "{action:'play'|'pause'|'toggle'|'stop'|'next'|'prev'|'seek'|'seek_rel'|'loop', value?:number}"),
        new("open_file", 1, "Open a local audio/video file in Cavernize", "{path:string, autoPlay?:bool, tab?:0..4}"),
        new("ui_action", 1, "Invoke high-level app actions (refresh_ui, rescan_library, sync_logs_cloud, check_updates, reset_head_pose, dismiss_dialogs, bring_to_front, toast)", "{action:string, message?:string}"),
        new("ui_click", 1, "Find and click a visible clickable View matching text/contentDescription (MainActivity + dialogs)", "{text:string, index?:number}"),
        new("cache_clear", 1, "Clear temporary app cache files cleanly", "{scope?:'cache'|'logs'}"),
        new("audio_probe", 1, "Run fast AudioQualityAnalyzer metadata check on a local file", "{path:string}"),

        // Tier 2: Unrestricted Dynamic Programmable Control, Visual & Shell (biometric-gated)
        new("screenshot_ui", 2, "Capture downscaled JPEG base64 screenshot of active dialog or MainActivity window", "{maxWidth?:360..1080, quality?:40..90}"),
        new("native_tool_exec", 2, "Run bundled NativeLibraryDir libffmpeg.so or libtruehdd.so probe w/ ArgumentList (zero shell)", "{tool:'ffmpeg'|'truehdd', args:[string]}"),
        new("file_delete", 2, "Delete a single confined file inside CacheDir or Logs directory", "{path:string}"),
        new("settings_reset", 2, "Reset one specific non-secret setting key or clear temporary bridge cache", "{key:string}"),
        new("reflect_types", 2, "Search & list any loaded .NET types across all loaded assemblies", "{filter?:string, assembly?:string, limit?:1..200}"),
        new("reflect_members", 2, "Inspect properties, fields & method signatures of any .NET type or live root", "{type?:string, root?:string, path?:string, filter?:string, includeNonPublic?:bool}"),
        new("reflect_get", 2, "Dynamically read any static or instance property/field path (secret-redacted)", "{type?:string, root?:'activity'|'store'|'player'|'audiodevice'|'headtracking'|'proaudio'|'dialogs', path:string, uiThread?:bool}"),
        new("reflect_set", 2, "Dynamically set any non-secret static or instance property/field path", "{type?:string, root?:string, path:string, value:any, uiThread?:bool}"),
        new("reflect_invoke", 2, "Dynamically invoke any .NET method (static/instance, sync/async Task, $context/$activity/$store/$null tokens)", "{type?:string, root?:string, path?:string, method:string, args?:[any], uiThread?:bool}"),
        new("ui_interact", 2, "Universal live UI automation across MainActivity & open dialogs (find/click/long_click/set_text/set_checked/set_progress/select_item/scroll_by/tap_xy/swipe_xy/back/dismiss_dialogs/dump_dialogs)", "{action:string, query?:string, type?:string, index?:number, value?:any, dy?:number, x?:number, y?:number, x1?:number, y1?:number, x2?:number, y2?:number}"),
        new("shell_exec", 2, "Execute sandboxed /system/bin/sh diagnostic command inside app sandbox (20s timeout, W^X guarded)", "{command:string, cwd?:string, timeoutSec?:1..20}"),
        new("hot_probe_status", 0, "Inspect W^X-compliant in-memory AssemblyLoadContext hot_exec readiness & HMAC key status", "{}"),
        new("hot_exec", 2, "Execute HMAC-SHA256 signed C# IL probe purely in RAM via collectible AssemblyLoadContext (zero APK rebuild)", "{requestId:string, timestampUnix:number, signature:string, dllBase64:string, entryType?:string, argsJson?:string, timeoutSec?:2..45}"),
        new("pipeline", 2, "Execute up to 30 chained arbitrary commands/reflections/UI steps with $prev substitution", "{steps:[{cmd:string, args?:object}], stopOnError?:bool}")
    ];

    public static bool IsActive {
        get {
            lock (stateLock) {
                return sessionCts != null && !sessionCts.IsCancellationRequested && DateTime.UtcNow < sessionExpiresUtc;
            }
        }
    }

    public static string ActiveSessionCode {
        get {
            lock (stateLock) {
                return IsActive ? activeSessionCode : "";
            }
        }
    }

    public static int ActiveMaxTier {
        get {
            lock (stateLock) {
                return IsActive ? activeMaxTier : 0;
            }
        }
    }

    public static bool ActiveBiometricVerified {
        get {
            lock (stateLock) {
                return IsActive && activeBiometricVerified;
            }
        }
    }

    public static int RemainingSeconds {
        get {
            lock (stateLock) {
                if (sessionCts == null || sessionCts.IsCancellationRequested) {
                    return 0;
                }
                return Math.Max(0, (int)(sessionExpiresUtc - DateTime.UtcNow).TotalSeconds);
            }
        }
    }

    public static int CommandsHandled {
        get {
            lock (stateLock) {
                return commandsHandled;
            }
        }
    }

    public static string LastEventText {
        get {
            lock (stateLock) {
                return lastEventText;
            }
        }
    }

    public static event Action StateChanged;

    public static void RegisterAppHost(ILiveBridgeAppHost host) {
        lock (stateLock) {
            hostRef = host != null ? new WeakReference<ILiveBridgeAppHost>(host) : null;
        }
    }

    public static void UnregisterAppHost(ILiveBridgeAppHost host) {
        lock (stateLock) {
            if (hostRef != null && hostRef.TryGetTarget(out ILiveBridgeAppHost current) && ReferenceEquals(current, host)) {
                hostRef = null;
            }
        }
    }

    static ILiveBridgeAppHost TryGetHost() {
        lock (stateLock) {
            return hostRef != null && hostRef.TryGetTarget(out ILiveBridgeAppHost h) ? h : null;
        }
    }

    internal static ILiveBridgeAppHost TryGetHostInternal() => TryGetHost();

    static HttpClient BuildClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid-LiveBridge", "2.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Validates that the relay URL is a well-formed public HTTPS endpoint (rejects http:// and local/private hosts).
    /// </summary>
    public static bool TryNormalizeRelayUrl(string raw, out string normalized) {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) {
            return false;
        }
        string candidate = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri)) {
            return false;
        }
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        string host = uri.Host ?? "";
        if (string.IsNullOrWhiteSpace(host) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host, out _)) {
            return false;
        }
        normalized = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
        return true;
    }

    public static async Task<(bool ok, string message)> TestRelayHealthAsync(string rawRelayUrl, CancellationToken ct = default) {
        if (!TryNormalizeRelayUrl(rawRelayUrl, out string baseUrl)) {
            return (false, "Invalid relay URL. Use your https://...workers.dev Worker URL.");
        }
        try {
            using HttpResponseMessage resp = await http.GetAsync($"{baseUrl}/health", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                return (false, $"Relay health check failed: HTTP {(int)resp.StatusCode}");
            }
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            bool ok = doc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.GetBoolean();
            int ver = doc.RootElement.TryGetProperty("version", out JsonElement vEl) && vEl.TryGetInt32(out int parsedV) ? parsedV : 1;
            bool supportsWake = doc.RootElement.TryGetProperty("supportsWake", out JsonElement wEl) && wEl.GetBoolean();
            bool supportsTelemetry = doc.RootElement.TryGetProperty("supportsTelemetry", out JsonElement tEl) && tEl.GetBoolean();
            bool supportsKnock = doc.RootElement.TryGetProperty("supportsSmartKnock", out JsonElement kEl) && kEl.GetBoolean();
            if (!ok) {
                return (false, "Relay responded without ok=true. Verify RELAY_KV binding.");
            }
            if (supportsKnock && supportsTelemetry && supportsWake) {
                return (true, $"Unified Hub v{ver} online (Smart Knock + Biometric Wake + Cloud Telemetry ready)");
            }
            if (supportsTelemetry && supportsWake) {
                return (true, $"Unified Hub v{ver} online (Biometric Wake + Cloud Telemetry ready; update JS to v5 for Smart Knock)");
            }
            return supportsWake
                ? (true, $"Relay v{ver} online (update Worker JS to v5 for Smart Knock & Cloud Telemetry)")
                : (true, $"Relay v{ver} online (update Worker JS to v5 for Smart Knock, Wake & Telemetry)");
        } catch (Exception ex) {
            return (false, $"Relay connection failed: {ex.Message}");
        }
    }

    // ==================== PAIRING SECRET & CRYPTO (ANDROID KEYSTORE + ENCRYPT-THEN-MAC) ====================

    public static bool HasPairedSecret(AppSettingsStore store) =>
        !string.IsNullOrWhiteSpace(store?.LiveBridgeEncryptedPairSecret) &&
        !string.IsNullOrEmpty(LoadPairSecretHex(store));

    public static string GenerateAndStorePairSecret(AppSettingsStore store) {
        if (store == null) {
            return "";
        }
        byte[] raw = new byte[32];
        RandomNumberGenerator.Fill(raw);
        string hex = Convert.ToHexString(raw).ToLowerInvariant();
        string encrypted = EncryptWithKeystore(hex);
        if (string.IsNullOrEmpty(encrypted)) {
            return "";
        }
        store.LiveBridgeEncryptedPairSecret = encrypted;
        AppLogger.Info("CLOUD", "Generated & stored new 256-bit Live Bridge pairing secret in Android Keystore.");
        NotifyStateChanged();
        return $"CAVPAIR-{hex}";
    }

    public static string GetCopyablePairSecret(AppSettingsStore store) {
        string hex = LoadPairSecretHex(store);
        return string.IsNullOrEmpty(hex) ? "" : $"CAVPAIR-{hex}";
    }

    public static void RevokePairSecret(AppSettingsStore store) {
        if (store == null) {
            return;
        }
        store.LiveBridgeEncryptedPairSecret = "";
        store.LiveBridgeStandbyEnabled = false;
        AppLogger.Info("CLOUD", "Revoked Live Bridge pairing secret and disabled background wake.");
        NotifyStateChanged();
    }

    internal static string LoadPairSecretHex(AppSettingsStore store) {
        string stored = store?.LiveBridgeEncryptedPairSecret;
        if (string.IsNullOrWhiteSpace(stored)) {
            return "";
        }
        string hex = DecryptWithKeystore(stored).Trim().ToLowerInvariant();
        return hex.Length == 64 ? hex : "";
    }

    internal static string GetPairSecretHexForInternalAuth(AppSettingsStore store) => LoadPairSecretHex(store);

    internal static string ComputeDeviceHash(string pairSecretHex) => ComputeWakeChannelHash(pairSecretHex);

    internal static string ComputeWakeChannelHash(string pairSecretHex) {
        byte[] input = System.Text.Encoding.UTF8.GetBytes($"wake-channel-v2:{pairSecretHex.ToLowerInvariant()}");
        byte[] digest = SHA256.HashData(input);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the public double-hashed Smart Knock mailbox ID (<c>sha256("knock-channel-v5:" + deviceHash)</c>).
    /// Safe to store in public <c>.agents/bridge_profile.json</c> (cannot derive deviceHash or pairSecret).
    /// </summary>
    public static string ComputeKnockChannelId(string pairSecretHex) {
        if (string.IsNullOrWhiteSpace(pairSecretHex) || pairSecretHex.Length != 64) {
            return "";
        }
        string devHash = ComputeWakeChannelHash(pairSecretHex);
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"knock-channel-v5:{devHash}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string GetPublicKnockId(AppSettingsStore store) {
        string hex = LoadPairSecretHex(store);
        return string.IsNullOrEmpty(hex) ? "" : ComputeKnockChannelId(hex);
    }

    /// <summary>
    /// Computes the 6-character Visual Verification Code (SAS, <c>XXX-XXX</c>) matching Python <c>omni_protocol.py</c>.
    /// </summary>
    public static string ComputeKnockSasCode(string knockId, string knockReqId, string clientPubKeyB64) {
        string canonical = $"knock-sas-v5|{(knockId ?? "").Trim().ToLowerInvariant()}|{(knockReqId ?? "").Trim()}|{(clientPubKeyB64 ?? "").Trim()}";
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        char[] chars = new char[6];
        for (int i = 0; i < 6; i++) {
            chars[i] = CodeAlphabet[digest[i] & 0x1F];
        }
        return $"{new string(chars, 0, 3)}-{new string(chars, 3, 3)}";
    }

    /// <summary>
    /// Performs ephemeral NIST P-256 ECDH with <paramref name="clientPubKeyB64"/> (65-byte SEC1 uncompressed)
    /// and encrypts <paramref name="grantObj"/> using v4 Encrypt-then-MAC (SHA256-CTR + HMAC-SHA256).
    /// </summary>
    internal static (string serverPubKeyB64, object encryptedGrant) CreateEcdhP256GrantEnvelope(
        string clientPubKeyB64,
        string knockReqId,
        object grantObj) {
        byte[] clientRaw = Convert.FromBase64String((clientPubKeyB64 ?? "").Trim());
        if (clientRaw.Length != 65 || clientRaw[0] != 0x04) {
            throw new CryptographicException("Invalid client P-256 SEC1 public key.");
        }
        ECParameters peerParams = new() {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint {
                X = clientRaw[1..33],
                Y = clientRaw[33..65]
            }
        };
        using ECDiffieHellman peerDh = ECDiffieHellman.Create(peerParams);
        using ECDiffieHellman localDh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECParameters localPub = localDh.ExportParameters(includePrivateParameters: false);
        byte[] serverRaw = new byte[65];
        serverRaw[0] = 0x04;
        Buffer.BlockCopy(localPub.Q.X, 0, serverRaw, 1 + (32 - localPub.Q.X.Length), localPub.Q.X.Length);
        Buffer.BlockCopy(localPub.Q.Y, 0, serverRaw, 33 + (32 - localPub.Q.Y.Length), localPub.Q.Y.Length);
        string serverPubB64 = Convert.ToBase64String(serverRaw);

        byte[] sharedX = localDh.DeriveRawSecretAgreement(peerDh.PublicKey);
        byte[] symKeyBytes = SHA256.HashData(ConcatBytes(
            System.Text.Encoding.UTF8.GetBytes($"ecdh-knock-v5|{(knockReqId ?? "").Trim()}|"),
            sharedX));
        string symKeyHex = Convert.ToHexString(symKeyBytes).ToLowerInvariant();
        object env = EncryptV4Envelope(symKeyHex, $"knock:{(knockReqId ?? "").Trim()}", grantObj);
        return (serverPubB64, env);
    }

    static object EncryptV4Envelope(string keyHex64, string contextId, object payloadObj) {
        byte[] key = Convert.FromHexString(keyHex64);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        byte[] encKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-enc-v4:"), iv));
        byte[] macKey = HMACSHA256.HashData(key, ConcatBytes(System.Text.Encoding.UTF8.GetBytes("omni-mac-v4:"), iv));

        byte[] rawPlain = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj));
        byte[] gzPlain;
        using (MemoryStream ms = new()) {
            using (GZipStream gz = new(ms, CompressionLevel.Optimal, leaveOpen: true)) {
                gz.Write(rawPlain, 0, rawPlain.Length);
            }
            gzPlain = ms.ToArray();
        }
        bool useGz = gzPlain.Length < rawPlain.Length;
        byte[] plain = useGz ? gzPlain : rawPlain;

        byte[] ct = new byte[plain.Length];
        uint counter = 0;
        int offset = 0;
        byte[] ctrBuf = new byte[4];
        while (offset < plain.Length) {
            ctrBuf[0] = (byte)((counter >> 24) & 0xFF);
            ctrBuf[1] = (byte)((counter >> 16) & 0xFF);
            ctrBuf[2] = (byte)((counter >> 8) & 0xFF);
            ctrBuf[3] = (byte)(counter & 0xFF);
            byte[] ksBlock = SHA256.HashData(ConcatBytes(encKey, iv, ctrBuf));
            int take = Math.Min(ksBlock.Length, plain.Length - offset);
            for (int i = 0; i < take; i++) {
                ct[offset + i] = (byte)(plain[offset + i] ^ ksBlock[i]);
            }
            offset += take;
            counter++;
        }

        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"omni-v4|{contextId}|{(useGz ? "1" : "0")}|");
        byte[] tag = HMACSHA256.HashData(macKey, ConcatBytes(prefix, iv, ct));
        return new {
            v = 4,
            gz = useGz,
            nonce = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(ct),
            tag = Convert.ToBase64String(tag)
        };
    }

    internal static bool VerifyWakeRequestSignature(string pairSecretHex, LiveBridgeWakeRequest req, out string rejectReason) {
        rejectReason = "";
        if (req == null || string.IsNullOrWhiteSpace(req.WakeId) || string.IsNullOrWhiteSpace(req.Signature)) {
            rejectReason = "Malformed wake payload.";
            return false;
        }
        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - req.TimestampUnix) > 180) {
            rejectReason = "Wake timestamp expired or clock skew > 180s.";
            return false;
        }
        lock (nonceLock) {
            if (recentWakeNonces.Contains(req.WakeId)) {
                rejectReason = "Duplicate wake nonce rejected.";
                return false;
            }
        }
        try {
            byte[] key = Convert.FromHexString(pairSecretHex);
            string canonical = $"wake-v2|{req.WakeId}|{req.TimestampUnix}|{req.RequestedTier}|{req.DurationSeconds}|{req.Reason}";
            byte[] expectedBytes = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes(canonical));
            byte[] providedBytes = Convert.FromHexString(req.Signature.Trim());
            if (expectedBytes.Length != providedBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes)) {
                rejectReason = "Invalid HMAC-SHA256 wake signature.";
                return false;
            }
            lock (nonceLock) {
                recentWakeNonces.Add(req.WakeId);
                recentWakeNoncesQueue.Enqueue(req.WakeId);
                while (recentWakeNoncesQueue.Count > 64) {
                    string oldest = recentWakeNoncesQueue.Dequeue();
                    recentWakeNonces.Remove(oldest);
                }
            }
            return true;
        } catch {
            rejectReason = "Wake signature verification error.";
            return false;
        }
    }

    /// <summary>
    /// Encrypt-then-MAC (SHA256-CTR + HMAC-SHA256) matching Python stdlib live_bridge_client.py bit-for-bit.
    /// </summary>
    internal static object EncryptApprovalEnvelope(string pairSecretHex, string wakeId, object plaintextObj) {
        byte[] key = Convert.FromHexString(pairSecretHex);
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        byte[] encPrefix = System.Text.Encoding.UTF8.GetBytes("enc-v2:");
        byte[] macPrefix = System.Text.Encoding.UTF8.GetBytes("mac-v2:");
        byte[] encKey = HMACSHA256.HashData(key, ConcatBytes(encPrefix, iv));
        byte[] macKey = HMACSHA256.HashData(key, ConcatBytes(macPrefix, iv));

        string json = JsonSerializer.Serialize(plaintextObj);
        byte[] plain = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] ct = new byte[plain.Length];

        uint counter = 0;
        int offset = 0;
        byte[] ctrBuf = new byte[4];
        while (offset < plain.Length) {
            ctrBuf[0] = (byte)((counter >> 24) & 0xFF);
            ctrBuf[1] = (byte)((counter >> 16) & 0xFF);
            ctrBuf[2] = (byte)((counter >> 8) & 0xFF);
            ctrBuf[3] = (byte)(counter & 0xFF);
            byte[] ksBlock = SHA256.HashData(ConcatBytes(encKey, iv, ctrBuf));
            int take = Math.Min(ksBlock.Length, plain.Length - offset);
            for (int i = 0; i < take; i++) {
                ct[offset + i] = (byte)(plain[offset + i] ^ ksBlock[i]);
            }
            offset += take;
            counter++;
        }

        byte[] macHeader = System.Text.Encoding.UTF8.GetBytes($"wake-approve-v2|{wakeId}|");
        byte[] tag = HMACSHA256.HashData(macKey, ConcatBytes(macHeader, iv, ct));

        return new {
            v = 2,
            nonce = Convert.ToBase64String(iv),
            ct = Convert.ToBase64String(ct),
            tag = Convert.ToBase64String(tag)
        };
    }

    static byte[] ConcatBytes(params byte[][] arrays) {
        int total = 0;
        foreach (byte[] a in arrays) {
            total += a.Length;
        }
        byte[] res = new byte[total];
        int pos = 0;
        foreach (byte[] a in arrays) {
            Buffer.BlockCopy(a, 0, res, pos, a.Length);
            pos += a.Length;
        }
        return res;
    }

    internal static void StorePendingWakeRequest(LiveBridgeWakeRequest req) {
        if (req != null && !string.IsNullOrEmpty(req.WakeId)) {
            pendingWakeRequests[req.WakeId] = req;
        }
    }

    internal static LiveBridgeWakeRequest GetPendingWakeRequest(string wakeId) =>
        !string.IsNullOrEmpty(wakeId) && pendingWakeRequests.TryGetValue(wakeId, out LiveBridgeWakeRequest r) ? r : null;

    internal static async Task<(bool ok, string message)> CompleteWakeApprovalAsync(
        Context context,
        AppSettingsStore store,
        string wakeId,
        int approvedTier,
        int durationSeconds,
        bool biometricVerified,
        CancellationToken ct = default) {
        if (context == null || store == null) {
            return (false, "Context unavailable.");
        }
        pendingWakeRequests.TryRemove(wakeId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex)) {
            return (false, "Pairing secret missing.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "Relay URL invalid.");
        }

        int finalTier = Math.Clamp(approvedTier, 0, 2);
        int finalTtl = Math.Clamp(durationSeconds, 300, 3600);
        (bool started, string sessionCode, string startMsg) = await StartSessionAsync(
            context,
            store,
            maxTier: finalTier,
            durationSeconds: finalTtl,
            biometricVerified: biometricVerified,
            ct: ct).ConfigureAwait(false);
        if (!started) {
            return (false, startMsg);
        }

        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        object envelope = EncryptApprovalEnvelope(pairHex, wakeId, new {
            sessionCode,
            tier = finalTier,
            ttlSeconds = finalTtl,
            biometricVerified,
            device = $"{Build.Manufacturer} {Build.Model}".Trim(),
            appVersion = $"v{ver} ({build})",
            approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });

        string devHash = ComputeWakeChannelHash(pairHex);
        try {
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/wake/approve");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new { wakeId, encryptedSession = envelope }),
                System.Text.Encoding.UTF8,
                "application/json");
            using HttpResponseMessage resp = await http.SendAsync(post, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                return (false, $"Relay wake approval failed: HTTP {(int)resp.StatusCode}");
            }
            AppLogger.Info("CLOUD", $"Live Bridge biometric wake approved (wakeId={wakeId}, tier=T{finalTier}, ttl={finalTtl}s).");
            return (true, $"Connected (Tier {finalTier}, {finalTtl / 60} min)");
        } catch (Exception ex) {
            return (false, $"Wake approve network error: {ex.Message}");
        }
    }

    internal static async Task DenyWakeRequestAsync(AppSettingsStore store, string wakeId, string reason) {
        pendingWakeRequests.TryRemove(wakeId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex) || !TryNormalizeRelayUrl(store?.LiveRelayUrl, out string baseUrl)) {
            return;
        }
        try {
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/wake/deny");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new { wakeId, reason = reason ?? "Denied by user" }),
                System.Text.Encoding.UTF8,
                "application/json");
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage _ = await http.SendAsync(post, cts.Token).ConfigureAwait(false);
            AppLogger.Info("CLOUD", $"Live Bridge wake request denied (wakeId={wakeId}): {reason}");
        } catch {
            // Best effort
        }
    }

    internal static void StorePendingKnockRequest(LiveBridgeKnockRequest req) {
        if (req != null && !string.IsNullOrEmpty(req.KnockReqId)) {
            pendingKnockRequests[req.KnockReqId] = req;
        }
    }

    internal static LiveBridgeKnockRequest GetPendingKnockRequest(string knockReqId) =>
        !string.IsNullOrEmpty(knockReqId) && pendingKnockRequests.TryGetValue(knockReqId, out LiveBridgeKnockRequest r) ? r : null;

    internal static async Task<(bool ok, string message)> CompleteKnockApprovalAsync(
        Context context,
        AppSettingsStore store,
        LiveBridgeKnockRequest knockReq,
        bool rememberWorkspace,
        bool biometricVerified,
        CancellationToken ct = default) {
        if (context == null || store == null || knockReq == null) {
            return (false, "Invalid Smart Knock context.");
        }
        pendingKnockRequests.TryRemove(knockReq.KnockReqId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex)) {
            return (false, "Pairing secret missing.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "Relay URL invalid.");
        }

        // Strict least-privilege invariant: workspace persistent pair grant requires genuine Biometric verification
        bool grantPair = rememberWorkspace && biometricVerified;
        int finalTier = Math.Clamp(knockReq.RequestedTier, 0, 2);
        int finalTtl = Math.Clamp(knockReq.DurationSeconds, 300, 3600);
        (bool started, string sessionCode, string startMsg) = await StartSessionAsync(
            context,
            store,
            maxTier: finalTier,
            durationSeconds: finalTtl,
            biometricVerified: biometricVerified,
            ct: ct).ConfigureAwait(false);
        if (!started) {
            return (false, startMsg);
        }

        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        object grantPayload = grantPair
            ? new {
                mode = "workspace",
                relayUrl = baseUrl,
                sessionCode,
                pairSecret = pairHex,
                knockId = ComputeKnockChannelId(pairHex),
                tier = finalTier,
                ttlSeconds = finalTtl,
                biometricVerified,
                device = $"{Build.Manufacturer} {Build.Model}".Trim(),
                appVersion = $"v{ver} ({build})",
                approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }
            : new {
                mode = "session",
                relayUrl = baseUrl,
                sessionCode,
                pairSecret = "",
                knockId = ComputeKnockChannelId(pairHex),
                tier = finalTier,
                ttlSeconds = finalTtl,
                biometricVerified,
                device = $"{Build.Manufacturer} {Build.Model}".Trim(),
                appVersion = $"v{ver} ({build})",
                approvedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

        try {
            (string serverPubB64, object encGrant) = CreateEcdhP256GrantEnvelope(knockReq.ClientPubKeyB64, knockReq.KnockReqId, grantPayload);
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/knock/approve");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new {
                    knockReqId = knockReq.KnockReqId,
                    serverPubKey = serverPubB64,
                    encryptedGrant = encGrant,
                    scope = grantPair ? "workspace" : "session"
                }),
                System.Text.Encoding.UTF8,
                "application/json");
            using HttpResponseMessage resp = await http.SendAsync(post, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                return (false, $"Smart Knock approval failed: HTTP {(int)resp.StatusCode}");
            }
            AppLogger.Info("CLOUD", $"Smart Knock approved (id={knockReq.KnockReqId}, sas={knockReq.SasCode}, scope={(grantPair ? "workspace" : "session")}, tier=T{finalTier}).");
            return (true, grantPair
                ? $"Connected & remembered (Tier {finalTier})"
                : $"Connected for {finalTtl / 60} min (Tier {finalTier})");
        } catch (Exception ex) {
            return (false, $"Smart Knock crypto/network error: {ex.Message}");
        }
    }

    internal static async Task DenyKnockRequestAsync(AppSettingsStore store, string knockReqId, string reason, int cooldownSeconds = 900) {
        pendingKnockRequests.TryRemove(knockReqId ?? "", out _);
        string pairHex = LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex) || !TryNormalizeRelayUrl(store?.LiveRelayUrl, out string baseUrl)) {
            return;
        }
        try {
            string devHash = ComputeWakeChannelHash(pairHex);
            using HttpRequestMessage post = new(HttpMethod.Post, $"{baseUrl}/v1/knock/deny");
            post.Headers.Add("X-Device-Hash", devHash);
            post.Content = new StringContent(
                JsonSerializer.Serialize(new {
                    knockReqId,
                    reason = reason ?? "Denied by user",
                    cooldownSeconds = Math.Clamp(cooldownSeconds, 60, 3600)
                }),
                System.Text.Encoding.UTF8,
                "application/json");
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage _ = await http.SendAsync(post, cts.Token).ConfigureAwait(false);
            AppLogger.Info("CLOUD", $"Smart Knock denied (id={knockReqId}, cooldown={cooldownSeconds}s): {reason}");
        } catch {
            // Best effort
        }
    }

    // ==================== SESSION LIFECYCLE ====================

    public static async Task<(bool ok, string sessionCode, string message)> StartSessionAsync(
        Context context,
        AppSettingsStore store,
        int? maxTier = null,
        int? durationSeconds = null,
        bool biometricVerified = false,
        CancellationToken ct = default) {
        if (context == null || store == null) {
            return (false, "", "Context unavailable.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "", "Configure a valid https:// Cloudflare Worker URL first.");
        }

        await StopSessionAsync().ConfigureAwait(false);

        int tier = Math.Clamp(maxTier ?? store.LiveBridgeDefaultTier, 0, 2);
        int ttl = Math.Clamp(durationSeconds ?? (store.LiveBridgeSessionMinutes * 60), 300, 3600);
        string code = GenerateEphemeralSessionCode();
        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        string payload = JsonSerializer.Serialize(new {
            ttlSeconds = ttl,
            maxTier = tier,
            device = $"{Build.Manufacturer} {Build.Model}".Trim(),
            appVersion = $"v{ver} ({build})"
        });

        try {
            using HttpRequestMessage req = CreateTokenRequest(HttpMethod.Post, $"{baseUrl}/v1/session/open", code);
            req.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) {
                string errText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (false, "", $"Could not open relay session (HTTP {(int)resp.StatusCode}): {Truncate(errText, 120)}");
            }
        } catch (Exception ex) {
            return (false, "", $"Relay error: {ex.Message}");
        }

        CancellationTokenSource cts = new();
        lock (stateLock) {
            sessionCts = cts;
            activeSessionCode = code;
            activeRelayUrl = baseUrl;
            activeMaxTier = tier;
            activeBiometricVerified = biometricVerified;
            sessionExpiresUtc = DateTime.UtcNow.AddSeconds(ttl);
            commandsHandled = 0;
            lastEventText = $"Waiting for command (T{tier})...";
        }

        AppLogger.Info("CLOUD", $"Live Bridge v2 started (T{tier}, bio={biometricVerified}, TTL={ttl}s, host={new Uri(baseUrl).Host})");
        NotifyStateChanged();

        Context appCtx = context.ApplicationContext ?? context;
        _ = Task.Run(() => PollLoopAsync(appCtx, store, baseUrl, code, tier, cts.Token));
        return (true, code, $"Live Bridge active (Tier {tier}, {ttl / 60} min).");
    }

    public static async Task StopSessionAsync() {
        CancellationTokenSource oldCts;
        string oldUrl;
        string oldCode;
        lock (stateLock) {
            oldCts = sessionCts;
            oldUrl = activeRelayUrl;
            oldCode = activeSessionCode;
            sessionCts = null;
            activeSessionCode = "";
            activeRelayUrl = "";
            activeMaxTier = 0;
            activeBiometricVerified = false;
            sessionExpiresUtc = DateTime.MinValue;
            lastEventText = "";
        }

        if (oldCts != null) {
            try {
                oldCts.Cancel();
                oldCts.Dispose();
            } catch {
                // Ignore
            }
            AppLogger.Info("CLOUD", "Live Bridge v2 session stopped.");
            NotifyStateChanged();
        }

        if (!string.IsNullOrEmpty(oldUrl) && !string.IsNullOrEmpty(oldCode)) {
            try {
                using HttpRequestMessage closeReq = CreateTokenRequest(HttpMethod.Delete, $"{oldUrl}/v1/session/close", oldCode);
                using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(4));
                using HttpResponseMessage _ = await http.SendAsync(closeReq, closeCts.Token).ConfigureAwait(false);
            } catch {
                // Best effort; KV TTL guarantees expiration
            }
        }
    }

    static async Task PollLoopAsync(Context context, AppSettingsStore store, string baseUrl, string code, int maxTier, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < sessionExpiresUtc) {
                try {
                    using HttpRequestMessage pollReq = CreateTokenRequest(HttpMethod.Get, $"{baseUrl}/v1/session/poll", code);
                    using HttpResponseMessage pollResp = await http.SendAsync(pollReq, ct).ConfigureAwait(false);
                    if (pollResp.StatusCode == HttpStatusCode.NotFound) {
                        break;
                    }
                    if (pollResp.IsSuccessStatusCode) {
                        string body = await pollResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using JsonDocument doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("hasCommand", out JsonElement hasEl) &&
                            hasEl.GetBoolean() &&
                            doc.RootElement.TryGetProperty("command", out JsonElement cmdEl)) {
                            string reqId = cmdEl.TryGetProperty("requestId", out JsonElement idEl) ? (idEl.GetString() ?? "") : "";
                            string cmd = cmdEl.TryGetProperty("cmd", out JsonElement cEl) ? (cEl.GetString() ?? "") : "";
                            int lines = cmdEl.TryGetProperty("lines", out JsonElement lEl) && lEl.TryGetInt32(out int parsedLines)
                                ? Math.Clamp(parsedLines, 20, 1000)
                                : 150;
                            bool errorsOnly = cmdEl.TryGetProperty("errorsOnly", out JsonElement eEl) && eEl.GetBoolean();
                            JsonElement argsEl = cmdEl.TryGetProperty("args", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Object
                                ? aEl.Clone()
                                : default;

                            if (!string.IsNullOrEmpty(reqId) && !string.IsNullOrEmpty(cmd)) {
                                await ExecuteAndRespondAsync(context, store, baseUrl, code, maxTier, reqId, cmd, argsEl, lines, errorsOnly, ct).ConfigureAwait(false);
                            }
                        }
                    }
                } catch (System.OperationCanceledException) {
                    break;
                } catch {
                    // Transient network hiccup; continue polling
                }

                NotifyStateChanged();
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            }
        } catch (System.OperationCanceledException) {
            // Normal shutdown
        } finally {
            bool needClear;
            lock (stateLock) {
                needClear = string.Equals(activeSessionCode, code, StringComparison.Ordinal);
                if (needClear) {
                    sessionCts?.Dispose();
                    sessionCts = null;
                    activeSessionCode = "";
                    activeRelayUrl = "";
                    activeMaxTier = 0;
                    activeBiometricVerified = false;
                    sessionExpiresUtc = DateTime.MinValue;
                }
            }
            if (needClear) {
                AppLogger.Info("CLOUD", "Live Bridge v2 session ended (expired or closed).");
                NotifyStateChanged();
            }
        }
    }

    static async Task ExecuteAndRespondAsync(
        Context context,
        AppSettingsStore store,
        string baseUrl,
        string code,
        int maxTier,
        string requestId,
        string rawCmd,
        JsonElement argsEl,
        int legacyLines,
        bool legacyErrorsOnly,
        CancellationToken ct) {
        string cmd = rawCmd.Trim().ToLowerInvariant();
        Stopwatch sw = Stopwatch.StartNew();

        lock (stateLock) {
            lastEventText = $"Running '{cmd}'...";
        }
        NotifyStateChanged();

        (bool ok, string output) = await ExecuteSingleCommandAsync(context, store, maxTier, cmd, argsEl, legacyLines, legacyErrorsOnly, ct, depth: 0).ConfigureAwait(false);

        sw.Stop();
        string safeBody = Truncate(AppLogger.SanitizeSensitiveText(output ?? ""), MaxResponseChars);
        string respPayload = JsonSerializer.Serialize(new {
            requestId,
            cmd,
            tier = maxTier,
            ok,
            elapsedMs = sw.ElapsedMilliseconds,
            respondedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            body = safeBody
        });

        try {
            using HttpRequestMessage postReq = CreateTokenRequest(HttpMethod.Post, $"{baseUrl}/v1/session/result", code);
            postReq.Content = new StringContent(respPayload, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage _ = await http.SendAsync(postReq, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Live Bridge result post failed: {ex.Message}");
        }

        lock (stateLock) {
            commandsHandled++;
            lastEventText = $"Handled '{cmd}' ({sw.ElapsedMilliseconds}ms)";
        }
        AppLogger.Info("CLOUD", $"Live Bridge v2 handled '{cmd}' (T{maxTier}, ok={ok}, {sw.ElapsedMilliseconds}ms)");
        NotifyStateChanged();
    }

    static async Task<(bool ok, string output)> ExecuteSingleCommandAsync(
        Context context,
        AppSettingsStore store,
        int maxTier,
        string rawCmd,
        JsonElement argsEl,
        int legacyLines,
        bool legacyErrorsOnly,
        CancellationToken ct,
        int depth) {
        string cmd = (rawCmd ?? "").Trim().ToLowerInvariant();
        CommandDescriptor desc = CapabilitiesCatalog.FirstOrDefault(c => c.Name == cmd);
        if (desc == null) {
            return (false, $"Unknown command '{cmd}'. Call 'capabilities' to discover all supported commands.");
        }
        if (desc.Tier > maxTier) {
            return (false, $"Command '{cmd}' requires Tier {desc.Tier} (active session is Tier {maxTier}). Reconnect or approve a Tier {desc.Tier} wake session.");
        }

        ILiveBridgeAppHost host = TryGetHost();
        try {
            switch (cmd) {
                case "ping": {
                    (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
                    return (true, JsonSerializer.Serialize(new {
                        status = "online",
                        bridgeVersion = 3,
                        supportsDynamicExecution = true,
                        supportsTelemetry = true,
                        activeTier = maxTier,
                        biometricVerified = ActiveBiometricVerified,
                        uiHostAttached = host != null,
                        standbyWakeActive = LiveBridgeWakeService.IsRunning,
                        sessionId = AppLogger.SessionId,
                        appVersion = ver,
                        appBuild = build,
                        device = $"{Build.Manufacturer} {Build.Model}".Trim(),
                        android = $"{Build.VERSION.Release} (SDK {(int)Build.VERSION.SdkInt})",
                        counts = new {
                            info = AppLogger.SessionInfoCount,
                            warn = AppLogger.SessionWarnCount,
                            error = AppLogger.SessionErrorCount,
                            fatal = AppLogger.SessionFatalCount
                        },
                        utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "capabilities":
                case "help": {
                    return (true, JsonSerializer.Serialize(new {
                        bridgeVersion = 3,
                        supportsDynamicExecution = true,
                        activeTier = maxTier,
                        uiHostAttached = host != null,
                        commands = CapabilitiesCatalog.Select(c => new {
                            cmd = c.Name,
                            tier = c.Tier,
                            allowedInSession = c.Tier <= maxTier,
                            summary = c.Summary,
                            args = c.ArgsSchema
                        })
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "quick_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, SelfCheckMode.Quick, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    return (true, rep.ToMarkdown());
                }
                case "full_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, SelfCheckMode.Full, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    return (true, rep.ToMarkdown());
                }
                case "ultra_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, SelfCheckMode.Ultra, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    return (true, rep.ToMarkdown());
                }
                case "acoustic_audit": {
                    bool quick = GetArgBool(argsEl, "quick", false);
                    string fmt = GetArgString(argsEl, "format", "json").Trim().ToLowerInvariant();
                    AcousticAuditReport rep = await Task.Run(() => AcousticAuditEngine.RunSuite(context, store, quick, ct), ct).ConfigureAwait(false);
                    return (rep.FailCount == 0, fmt == "markdown" || fmt == "md" ? rep.ToMarkdownTable() : rep.ToJson());
                }
                case "acoustic_probe": {
                    AcousticCustomProbeRequest req = new() {
                        ProfileId = GetArgString(argsEl, "profileId", BinauralProfileService.DefaultProfileId),
                        MinimumPhase = GetArgBool(argsEl, "minPhase", false),
                        MagLsOrder = GetArgInt(argsEl, "magLsOrder", 0, 0, 3),
                        BiometricItdEnabled = GetArgBool(argsEl, "bioItd", false),
                        HeadWidthCm = (float)(GetArgNullableDouble(argsEl, "headWidthCm") ?? Cavern.Virtualizer.BiometricItdScaler.ReferenceHeadWidthCm),
                        ManualItdOffsetUs = (float)(GetArgNullableDouble(argsEl, "manualItdUs") ?? 0.0),
                        IsmRoomEnabled = GetArgBool(argsEl, "ismRoom", false),
                        IsmPresetIndex = GetArgInt(argsEl, "ismPreset", 0, 0, 4),
                        IsmReflectionLevel = (float)(GetArgNullableDouble(argsEl, "ismLevel") ?? 1.0),
                        NearFieldEnabled = GetArgBool(argsEl, "nearField", false),
                        NearFieldDistanceM = (float)(GetArgNullableDouble(argsEl, "nearFieldDistM") ?? 3.0),
                        ListenerYawDeg = (float)(GetArgNullableDouble(argsEl, "yawDeg") ?? 0.0),
                        ListenerPitchDeg = (float)(GetArgNullableDouble(argsEl, "pitchDeg") ?? 0.0),
                        SourceAzimuthDeg = (float)(GetArgNullableDouble(argsEl, "sourceAzDeg") ?? 0.0),
                        SourceElevationDeg = (float)(GetArgNullableDouble(argsEl, "sourceElDeg") ?? 0.0),
                        SampleRate = GetArgInt(argsEl, "sampleRate", 48000, 8000, 192000),
                        UseParametricUpmix = GetArgBool(argsEl, "upmix", false),
                        UseLgsrRestore = GetArgBool(argsEl, "lgsr", false),
                        CompareAgainstDirect = GetArgBool(argsEl, "compareDirect", true),
                        SignalType = GetArgString(argsEl, "signal", "white")
                    };
                    string json = await Task.Run(() => AcousticAuditEngine.RunCustomProbeJson(context, req), ct).ConfigureAwait(false);
                    return (true, json);
                }
                case "ui_audit": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    (List<SelfCheckItem> items, List<UiPerfSample> perfSamples) = await host.RunLiveUiSelfAuditAsync(null, ct).ConfigureAwait(false);
                    return (items.All(i => i.Status != SelfCheckStatus.Fail),
                        JsonSerializer.Serialize(new {
                            pass = items.Count(i => i.Status == SelfCheckStatus.Pass),
                            warn = items.Count(i => i.Status == SelfCheckStatus.Warn),
                            fail = items.Count(i => i.Status == SelfCheckStatus.Fail),
                            checks = items,
                            uiPerfSlowest = perfSamples.OrderByDescending(p => p.FirstFrameMs).Take(12).ToList()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "ui_perf_audit": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    (List<SelfCheckItem> items, List<UiPerfSample> perfSamples) = await host.RunLiveUiSelfAuditAsync(null, ct).ConfigureAwait(false);
                    SelfCheckItem perfSummary = items.FirstOrDefault(i => i.Id == "ui.perf_latency_jank");
                    List<UiPerfSample> ranked = perfSamples.OrderByDescending(p => p.FirstFrameMs).ToList();
                    return (perfSummary == null || perfSummary.Status != SelfCheckStatus.Fail,
                        JsonSerializer.Serialize(new {
                            verdict = perfSummary?.Status.ToString() ?? "OK",
                            summary = perfSummary?.Details ?? "",
                            stalls = ranked.Count(p => p.Status == "STALL"),
                            slows = ranked.Count(p => p.Status == "SLOW"),
                            totalDroppedFrames = ranked.Sum(p => p.DroppedFrames),
                            targets = ranked
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "summary":
                    return (true, AppLogger.BuildDiagnosticSummaryJson(context, store));
                case "log_tail": {
                    int lines = GetArgInt(argsEl, "lines", legacyLines, 20, 1000);
                    bool errorsOnly = GetArgBool(argsEl, "errorsOnly", legacyErrorsOnly);
                    string rawTail = AppLogger.ReadRecentLines(lines);
                    if (errorsOnly) {
                        string[] filtered = rawTail
                            .Split('\n')
                            .Where(ln =>
                                ln.Contains("[WARN]", StringComparison.Ordinal) ||
                                ln.Contains("[ERROR]", StringComparison.Ordinal) ||
                                ln.Contains("[FATAL]", StringComparison.Ordinal) ||
                                ln.Contains("=== Session started", StringComparison.Ordinal) ||
                                ln.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        return (true, filtered.Length > 0 ? string.Join("\n", filtered) : "(No WARN/ERROR/FATAL lines in recent log tail)");
                    }
                    return (true, rawTail);
                }
                case "log_search": {
                    string query = GetArgString(argsEl, "query", "");
                    if (string.IsNullOrWhiteSpace(query)) {
                        return (false, "log_search requires {query: string}.");
                    }
                    int maxMatches = GetArgInt(argsEl, "maxMatches", 80, 1, 200);
                    string rawTail = AppLogger.ReadRecentLines(2000);
                    string[] hits = rawTail.Split('\n')
                        .Where(ln => ln.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .TakeLast(maxMatches)
                        .ToArray();
                    return (true, hits.Length > 0 ? string.Join("\n", hits) : $"(No log lines matching '{query}')");
                }
                case "audio_routes":
                    return (true, BuildAudioRoutesSnapshot(context));
                case "memory":
                    return (true, BuildMemoryJson(context));
                case "native_status":
                    return (true, BuildNativeLibsStatusJson(context));
                case "app_state":
                    return host != null
                        ? (true, await host.GetAppStateJsonAsync().ConfigureAwait(false))
                        : (true, JsonSerializer.Serialize(new { uiHostAttached = false, mode = "background_standby", standbyRunning = LiveBridgeWakeService.IsRunning }));
                case "dsp_telemetry":
                    return host != null
                        ? (true, await host.GetDspTelemetryJsonAsync().ConfigureAwait(false))
                        : (false, "MainActivity UI/DSP host is currently closed (background standby mode). Call ui_action {action:'bring_to_front'} first.");
                case "library_query": {
                    string query = GetArgString(argsEl, "query", "");
                    int limit = GetArgInt(argsEl, "limit", 40, 1, 100);
                    if (host != null) {
                        return (true, await host.QueryLibraryJsonAsync(query, limit).ConfigureAwait(false));
                    }
                    MediaLibrarySnapshot snap = MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(context);
                    var tracks = (snap?.Tracks ?? []).Where(t =>
                        string.IsNullOrWhiteSpace(query) ||
                        (t.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                        .Take(limit)
                        .Select(t => new { t.Title, t.Artist, t.Album, t.DurationMs, t.FilePath })
                        .ToList();
                    return (true, JsonSerializer.Serialize(new { totalCached = snap?.Tracks?.Count ?? 0, returned = tracks.Count, tracks }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "ui_tree": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    int maxDepth = GetArgInt(argsEl, "maxDepth", 6, 1, 10);
                    return (true, await host.DumpUiTreeJsonAsync(maxDepth).ConfigureAwait(false));
                }
                case "settings_get":
                    return ExecuteSettingsGet(store, argsEl);
                case "settings_set": {
                    (bool setOk, string setMsg) = ExecuteSettingsSet(store, argsEl);
                    if (setOk && host != null) {
                        await host.ApplySettingsChangedAsync().ConfigureAwait(false);
                    }
                    return (setOk, setMsg);
                }
                case "settings_reset": {
                    string key = GetArgString(argsEl, "key", "");
                    if (string.IsNullOrWhiteSpace(key)) {
                        return (false, "settings_reset requires {key: string}.");
                    }
                    if (IsSecretSettingName(key)) {
                        return (false, $"Rejected: '{key}' is a protected secret property.");
                    }
                    PropertyInfo prop = typeof(AppSettingsStore).GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop == null || !prop.CanWrite) {
                        return (false, $"Writable setting '{key}' not found.");
                    }
                    object defVal = prop.PropertyType == typeof(string) ? "" : (prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null);
                    prop.SetValue(store, defVal);
                    if (host != null) {
                        await host.ApplySettingsChangedAsync().ConfigureAwait(false);
                    }
                    return (true, $"Reset '{prop.Name}' to default ({defVal ?? "null"}).");
                }
                case "files_list":
                    return ExecuteFilesList(context, argsEl);
                case "file_read":
                    return ExecuteFileRead(context, argsEl);
                case "file_delete":
                    return ExecuteFileDelete(context, argsEl);
                case "tab_switch": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    int idx = GetArgInt(argsEl, "index", -1, -1, 4);
                    string name = GetArgString(argsEl, "name", "");
                    return await host.SwitchTabAsync(idx, name).ConfigureAwait(false);
                }
                case "playback_control": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    string action = GetArgString(argsEl, "action", "");
                    double? val = GetArgNullableDouble(argsEl, "value");
                    return await host.PlaybackControlAsync(action, val).ConfigureAwait(false);
                }
                case "open_file": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    string path = GetArgString(argsEl, "path", "");
                    bool autoPlay = GetArgBool(argsEl, "autoPlay", false);
                    int tab = GetArgInt(argsEl, "tab", -1, -1, 4);
                    return await host.OpenFileAsync(path, autoPlay, tab >= 0 ? tab : null).ConfigureAwait(false);
                }
                case "ui_action": {
                    string action = GetArgString(argsEl, "action", "").Trim().ToLowerInvariant();
                    string msg = GetArgString(argsEl, "message", "");
                    if (action == "bring_to_front") {
                        Intent launch = new(context, typeof(MainActivity));
                        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront | ActivityFlags.SingleTop);
                        context.StartActivity(launch);
                        return (true, "Launched MainActivity to foreground.");
                    }
                    if (action == "sync_logs_cloud") {
                        CloudLogSyncResult syncRes = await CloudLogSyncService.UploadLogsAsync(context, store, "live_bridge", ct).ConfigureAwait(false);
                        return (syncRes.Success, syncRes.Message);
                    }
                    if (host == null) {
                        return (false, $"UI action '{action}' requires MainActivity open. Call ui_action {{action:'bring_to_front'}} first.");
                    }
                    return await host.ExecuteUiActionAsync(action, msg).ConfigureAwait(false);
                }
                case "ui_click": {
                    if (host == null) {
                        return (false, "MainActivity is not open.");
                    }
                    string text = GetArgString(argsEl, "text", "");
                    int idx = GetArgInt(argsEl, "index", 0, 0, 50);
                    return await host.ClickUiElementAsync(text, idx).ConfigureAwait(false);
                }
                case "cache_clear": {
                    string scope = GetArgString(argsEl, "scope", "cache").ToLowerInvariant();
                    int deleted = 0;
                    if (scope == "logs") {
                        AppLogger.Clear(context);
                        return (true, "Cleared Cavernize log file.");
                    }
                    string cachePath = context.CacheDir?.AbsolutePath;
                    if (!string.IsNullOrEmpty(cachePath) && Directory.Exists(cachePath)) {
                        foreach (string f in Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)) {
                            try {
                                File.Delete(f);
                                deleted++;
                            } catch {
                                // Skip active lock
                            }
                        }
                    }
                    return (true, $"Cleared {deleted} temporary cache file(s).");
                }
                case "audio_probe": {
                    string path = GetArgString(argsEl, "path", "");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) {
                        return (false, "File not found.");
                    }
                    FileInfo fi = new(path);
                    LibraryTrackItem item = new() {
                        Title = Path.GetFileNameWithoutExtension(path),
                        FilePath = path,
                        FileSizeBytes = fi.Length
                    };
                    AudioQualityReport rep = AudioQualityAnalyzer.EvaluateFast(item, context, allowIo: true);
                    return (true, JsonSerializer.Serialize(new {
                        path,
                        sizeBytes = fi.Length,
                        category = rep.Category.ToString(),
                        codecFamily = rep.CodecFamily,
                        isLossless = rep.IsLossless,
                        score = rep.Score,
                        sampleRateHz = rep.SampleRateHz,
                        declaredBitDepth = rep.DeclaredBitDepth,
                        effectiveBitDepth = rep.EffectiveBitDepth,
                        channels = rep.Channels,
                        bitrateKbps = rep.BitrateKbps
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "screenshot_ui": {
                    if (host == null) {
                        return (false, "MainActivity is not open.");
                    }
                    int maxWidth = GetArgInt(argsEl, "maxWidth", 720, 360, 1080);
                    int quality = GetArgInt(argsEl, "quality", 72, 40, 90);
                    return (true, await host.CaptureUiScreenshotJsonAsync(maxWidth, quality).ConfigureAwait(false));
                }
                case "native_tool_exec":
                    return await ExecuteNativeToolProbeAsync(context, argsEl, ct).ConfigureAwait(false);
                case "ui_interact": {
                    if (host == null) {
                        return (false, "MainActivity is not open. Call ui_action {action:'bring_to_front'} first.");
                    }
                    return await host.UiInteractAsync(argsEl).ConfigureAwait(false);
                }
                case "reflect_types":
                    return ExecuteReflectTypes(argsEl);
                case "reflect_members":
                    return await ExecuteReflectMembersAsync(host, store, argsEl).ConfigureAwait(false);
                case "reflect_get":
                    return await ExecuteReflectGetAsync(host, store, argsEl).ConfigureAwait(false);
                case "reflect_set":
                    return await ExecuteReflectSetAsync(host, store, argsEl).ConfigureAwait(false);
                case "reflect_invoke":
                    return await ExecuteReflectInvokeAsync(context, host, store, argsEl).ConfigureAwait(false);
                case "shell_exec":
                    return await ExecuteSandboxedShellAsync(context, argsEl, ct).ConfigureAwait(false);
                case "hot_probe_status":
                    return (true, LiveHotPatchHost.GetStatusJson(store));
                case "hot_exec":
                    return await LiveHotPatchHost.ExecuteSignedProbeAsync(context, store, host, argsEl, ct).ConfigureAwait(false);
                case "pipeline": {
                    if (depth > 0) {
                        return (false, "Nested pipeline commands are not allowed.");
                    }
                    if (argsEl.ValueKind != JsonValueKind.Object ||
                        !argsEl.TryGetProperty("steps", out JsonElement stepsEl) ||
                        stepsEl.ValueKind != JsonValueKind.Array) {
                        return (false, "pipeline requires {steps: [{cmd, args?}], stopOnError?:bool}");
                    }
                    bool stopOnError = GetArgBool(argsEl, "stopOnError", true);
                    List<object> results = [];
                    string prevOut = "";
                    int count = 0;
                    bool allOk = true;
                    foreach (JsonElement step in stepsEl.EnumerateArray()) {
                        if (++count > 30) {
                            break;
                        }
                        string subCmd = step.TryGetProperty("cmd", out JsonElement scEl) ? (scEl.GetString() ?? "") : "";
                        JsonElement subArgs = default;
                        if (step.TryGetProperty("args", out JsonElement saEl) && saEl.ValueKind == JsonValueKind.Object) {
                            string rawJson = saEl.GetRawText();
                            if (rawJson.Contains("$prev", StringComparison.Ordinal)) {
                                string safePrev = JsonEncodedText.Encode(Truncate(prevOut, 4000)).ToString();
                                using JsonDocument patched = JsonDocument.Parse(rawJson.Replace("$prev", safePrev, StringComparison.Ordinal));
                                subArgs = patched.RootElement.Clone();
                            } else {
                                subArgs = saEl.Clone();
                            }
                        }
                        (bool subOk, string subOut) = await ExecuteSingleCommandAsync(
                            context, store, maxTier, subCmd, subArgs, legacyLines, legacyErrorsOnly, ct, depth + 1).ConfigureAwait(false);
                        prevOut = subOut ?? "";
                        results.Add(new { step = count, cmd = subCmd, ok = subOk, body = TruncateForCommand(subCmd, subArgs, prevOut, 24_000) });
                        if (!subOk) {
                            allOk = false;
                            if (stopOnError) {
                                break;
                            }
                        }
                    }
                    return (allOk, JsonSerializer.Serialize(new { count = results.Count, allOk, results }, new JsonSerializerOptions { WriteIndented = true }));
                }
                case "batch": {
                    if (depth > 0) {
                        return (false, "Nested batch commands are not allowed.");
                    }
                    if (argsEl.ValueKind != JsonValueKind.Object ||
                        !argsEl.TryGetProperty("commands", out JsonElement cmdsEl) ||
                        cmdsEl.ValueKind != JsonValueKind.Array) {
                        return (false, "batch requires {commands: [{cmd, args?}]}");
                    }
                    List<object> results = [];
                    int count = 0;
                    foreach (JsonElement step in cmdsEl.EnumerateArray()) {
                        if (++count > MaxBatchCommands) {
                            break;
                        }
                        string subCmd = step.TryGetProperty("cmd", out JsonElement scEl) ? (scEl.GetString() ?? "") : "";
                        JsonElement subArgs = step.TryGetProperty("args", out JsonElement saEl) && saEl.ValueKind == JsonValueKind.Object
                            ? saEl
                            : default;
                        (bool subOk, string subOut) = await ExecuteSingleCommandAsync(
                            context, store, maxTier, subCmd, subArgs, legacyLines, legacyErrorsOnly, ct, depth + 1).ConfigureAwait(false);
                        results.Add(new { cmd = subCmd, ok = subOk, body = TruncateForCommand(subCmd, subArgs, subOut, 24_000) });
                    }
                    return (true, JsonSerializer.Serialize(new { count = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
                }
                default:
                    return (false, $"Command '{cmd}' is not implemented.");
            }
        } catch (Exception ex) {
            AppLogger.Error("CLOUD", $"Live Bridge v3 command '{cmd}' error", ex);
            return (false, $"Command '{cmd}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ==================== TIER-2 UNRESTRICTED DYNAMIC REFLECTION & SANDBOXED SHELL ====================

    const BindingFlags ReflectAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;

    static Type ResolveDynamicType(string typeName) {
        if (string.IsNullOrWhiteSpace(typeName)) {
            return null;
        }
        string clean = typeName.Trim();
        Type direct = Type.GetType(clean, throwOnError: false, ignoreCase: true);
        if (direct != null) {
            return direct;
        }
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                Type exact = asm.GetType(clean, throwOnError: false, ignoreCase: true);
                if (exact != null) {
                    return exact;
                }
            } catch {
                // Skip dynamic/unreadable assembly
            }
        }
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            try {
                Type byShort = asm.GetTypes().FirstOrDefault(t =>
                    string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, clean, StringComparison.OrdinalIgnoreCase) ||
                    (t.FullName?.EndsWith("." + clean, StringComparison.OrdinalIgnoreCase) ?? false));
                if (byShort != null) {
                    return byShort;
                }
            } catch {
                // Skip assembly load errors
            }
        }
        return null;
    }

    static (bool ok, object target, Type targetType, string error) ResolveReflectionTarget(
        ILiveBridgeAppHost host, AppSettingsStore store, string rootName, string typeName, string dotPath) {
        object current = null;
        Type currentType = null;

        if (!string.IsNullOrWhiteSpace(rootName)) {
            if (string.Equals(rootName, "store", StringComparison.OrdinalIgnoreCase)) {
                current = store;
            } else if (host != null) {
                current = host.GetReflectionRoot(rootName);
            }
            if (current == null) {
                return (false, null, null, $"Reflection root '{rootName}' unavailable (ensure MainActivity is open for UI roots).");
            }
            currentType = current.GetType();
        } else if (!string.IsNullOrWhiteSpace(typeName)) {
            currentType = ResolveDynamicType(typeName);
            if (currentType == null) {
                return (false, null, null, $"Type '{typeName}' not found in loaded assemblies.");
            }
        } else {
            return (false, null, null, "Provide either {root} or {type}.");
        }

        if (string.IsNullOrWhiteSpace(dotPath)) {
            return (true, current, currentType, "");
        }

        string[] segments = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments) {
            if (IsSecretSettingName(seg)) {
                return (false, null, null, $"Access denied: '{seg}' is a protected secret member.");
            }
            PropertyInfo pi = currentType.GetProperty(seg, ReflectAny);
            if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) {
                current = pi.GetValue(current);
                currentType = current?.GetType() ?? pi.PropertyType;
                continue;
            }
            FieldInfo fi = currentType.GetField(seg, ReflectAny);
            if (fi != null) {
                current = fi.GetValue(current);
                currentType = current?.GetType() ?? fi.FieldType;
                continue;
            }
            return (false, null, null, $"Member '{seg}' not found on type '{currentType.FullName}'.");
        }
        return (true, current, currentType, "");
    }

    static (bool ok, string output) ExecuteReflectTypes(JsonElement argsEl) {
        string filter = GetArgString(argsEl, "filter", "");
        string asmFilter = GetArgString(argsEl, "assembly", "");
        int limit = GetArgInt(argsEl, "limit", 80, 1, 200);
        List<object> matched = [];
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            string asmName = asm.GetName().Name ?? "";
            if (!string.IsNullOrWhiteSpace(asmFilter) && !asmName.Contains(asmFilter, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (string.IsNullOrWhiteSpace(asmFilter) && string.IsNullOrWhiteSpace(filter) &&
                !asmName.Contains("Cavern", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            Type[] types;
            try {
                types = asm.GetTypes();
            } catch (ReflectionTypeLoadException rtle) {
                types = rtle.Types.Where(t => t != null).ToArray();
            } catch {
                continue;
            }
            foreach (Type t in types) {
                if (t == null || t.Name.StartsWith("<", StringComparison.Ordinal)) {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(filter) &&
                    !(t.FullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)) {
                    continue;
                }
                matched.Add(new { fullName = t.FullName, assembly = asmName, isStatic = t.IsAbstract && t.IsSealed });
                if (matched.Count >= limit) {
                    break;
                }
            }
            if (matched.Count >= limit) {
                break;
            }
        }
        return (true, JsonSerializer.Serialize(new { count = matched.Count, types = matched }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static async Task<(bool ok, string output)> ExecuteReflectMembersAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        string filter = GetArgString(argsEl, "filter", "");
        bool includeNonPublic = GetArgBool(argsEl, "includeNonPublic", true);
        bool uiThread = GetArgBool(argsEl, "uiThread", false);

        (bool ok, string output) Work() {
            (bool resolved, _, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved || t == null) {
                return (false, err);
            }
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | (includeNonPublic ? BindingFlags.NonPublic : BindingFlags.Default);
            var props = t.GetProperties(flags)
                .Where(p => !IsSecretSettingName(p.Name) && (string.IsNullOrEmpty(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(80)
                .Select(p => $"{p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}")
                .ToList();
            var fields = t.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<", StringComparison.Ordinal) && !IsSecretSettingName(f.Name) && (string.IsNullOrEmpty(filter) || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(80)
                .Select(f => $"{(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name}")
                .ToList();
            var methods = t.GetMethods(flags)
                .Where(m => !m.IsSpecialName && !m.Name.StartsWith("<", StringComparison.Ordinal) && !IsSecretSettingName(m.Name) && (string.IsNullOrEmpty(filter) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .Take(100)
                .Select(m => $"{(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .ToList();
            return (true, JsonSerializer.Serialize(new { type = t.FullName, props, fields, methods }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectGetAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));

        (bool ok, string output) Work() {
            (bool resolved, object val, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved) {
                return (false, err);
            }
            return (true, FormatReflectedValue(val, t));
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectSetAsync(ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || argsEl.ValueKind != JsonValueKind.Object || !argsEl.TryGetProperty("value", out JsonElement valEl)) {
            return (false, "reflect_set requires {root|type, path, value}.");
        }
        JsonElement clonedVal = valEl.Clone();

        (bool ok, string output) Work() {
            int lastDot = path.LastIndexOf('.');
            string parentPath = lastDot > 0 ? path[..lastDot] : "";
            string leaf = lastDot > 0 ? path[(lastDot + 1)..] : path;
            if (IsSecretSettingName(leaf)) {
                return (false, $"Rejected: '{leaf}' is a protected secret member.");
            }
            (bool resolved, object target, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, parentPath);
            if (!resolved || t == null) {
                return (false, err);
            }
            PropertyInfo pi = t.GetProperty(leaf, ReflectAny);
            if (pi != null && pi.CanWrite) {
                object converted = ConvertDynamicArg(clonedVal, pi.PropertyType, null, host, store);
                pi.SetValue(target, converted);
                return (true, $"Set {t.Name}.{pi.Name} = {converted ?? "null"}");
            }
            FieldInfo fi = t.GetField(leaf, ReflectAny);
            if (fi != null && !fi.IsInitOnly && !fi.IsLiteral) {
                object converted = ConvertDynamicArg(clonedVal, fi.FieldType, null, host, store);
                fi.SetValue(target, converted);
                return (true, $"Set {t.Name}.{fi.Name} = {converted ?? "null"}");
            }
            return (false, $"Writable property/field '{leaf}' not found on '{t.FullName}'.");
        }
        return uiThread && host != null
            ? await host.ExecuteOnUiThreadReflectAsync(Work).ConfigureAwait(false)
            : Work();
    }

    static async Task<(bool ok, string output)> ExecuteReflectInvokeAsync(Context context, ILiveBridgeAppHost host, AppSettingsStore store, JsonElement argsEl) {
        string rootName = GetArgString(argsEl, "root", "");
        string typeName = GetArgString(argsEl, "type", "");
        string path = GetArgString(argsEl, "path", "");
        string methodName = GetArgString(argsEl, "method", "");
        bool uiThread = GetArgBool(argsEl, "uiThread", string.Equals(rootName, "activity", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(methodName)) {
            return (false, "reflect_invoke requires {root|type, method, args?:[]}.");
        }
        if (IsSecretSettingName(methodName)) {
            return (false, $"Rejected: method '{methodName}' touches protected secret material.");
        }
        List<JsonElement> rawArgs = [];
        if (argsEl.ValueKind == JsonValueKind.Object && argsEl.TryGetProperty("args", out JsonElement aEl) && aEl.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement item in aEl.EnumerateArray()) {
                rawArgs.Add(item.Clone());
            }
        }

        object invokedResult = null;
        string invokeError = null;
        bool invokeOk = false;

        (bool ok, string output) InvokeCore() {
            (bool resolved, object target, Type t, string err) = ResolveReflectionTarget(host, store, rootName, typeName, path);
            if (!resolved || t == null) {
                invokeError = err;
                return (false, err);
            }
            MethodInfo[] candidates = t.GetMethods(ReflectAny)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) && !m.IsGenericMethodDefinition)
                .ToArray();
            if (candidates.Length == 0) {
                invokeError = $"Method '{methodName}' not found on '{t.FullName}'.";
                return (false, invokeError);
            }

            MethodInfo chosen = null;
            object[] finalArgs = null;
            foreach (MethodInfo m in candidates) {
                ParameterInfo[] ps = m.GetParameters();
                int reqCount = ps.Count(p => !p.HasDefaultValue);
                if (rawArgs.Count < reqCount || rawArgs.Count > ps.Length) {
                    continue;
                }
                try {
                    object[] converted = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) {
                        converted[i] = i < rawArgs.Count
                            ? ConvertDynamicArg(rawArgs[i], ps[i].ParameterType, context, host, store)
                            : ps[i].DefaultValue;
                    }
                    chosen = m;
                    finalArgs = converted;
                    break;
                } catch {
                    // Try next overload
                }
            }
            if (chosen == null) {
                invokeError = $"No matching overload for {t.Name}.{methodName} accepting {rawArgs.Count} arg(s).";
                return (false, invokeError);
            }
            invokedResult = chosen.Invoke(chosen.IsStatic ? null : target, finalArgs);
            invokeOk = true;
            return (true, "invoked");
        }

        try {
            if (uiThread && host != null) {
                await host.ExecuteOnUiThreadReflectAsync(InvokeCore).ConfigureAwait(false);
            } else {
                InvokeCore();
            }
            if (!invokeOk) {
                return (false, invokeError ?? "Reflection invocation failed.");
            }
            if (invokedResult is Task task) {
                await task.ConfigureAwait(false);
                Type taskType = task.GetType();
                if (taskType.IsGenericType) {
                    PropertyInfo resProp = taskType.GetProperty("Result");
                    invokedResult = resProp?.GetValue(task);
                } else {
                    invokedResult = "Task completed (void)";
                }
            }
            return (true, FormatReflectedValue(invokedResult, invokedResult?.GetType()));
        } catch (TargetInvocationException tie) {
            Exception inner = tie.InnerException ?? tie;
            return (false, $"Invoked method threw {inner.GetType().Name}: {inner.Message}");
        } catch (Exception ex) {
            return (false, $"reflect_invoke error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static object ConvertDynamicArg(JsonElement el, Type targetType, Context context, ILiveBridgeAppHost host, AppSettingsStore store) {
        if (el.ValueKind == JsonValueKind.Null) {
            return null;
        }
        if (el.ValueKind == JsonValueKind.String) {
            string s = el.GetString() ?? "";
            if (s == "$null") return null;
            if (s == "$context") return context;
            if (s == "$activity") return host;
            if (s == "$store") return store;
        }
        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum) {
            return el.ValueKind == JsonValueKind.String
                ? Enum.Parse(underlying, el.GetString()!, ignoreCase: true)
                : Enum.ToObject(underlying, el.GetInt32());
        }
        if (IsSimpleSettingType(underlying)) {
            return ConvertJsonToType(el, underlying);
        }
        if (underlying == typeof(CancellationToken)) {
            return CancellationToken.None;
        }
        return JsonSerializer.Deserialize(el.GetRawText(), underlying);
    }

    static string FormatReflectedValue(object val, Type t) {
        if (val == null) {
            return "null";
        }
        Type actual = val.GetType();
        if (IsSimpleSettingType(actual) || actual.IsEnum) {
            return JsonSerializer.Serialize(new { type = actual.FullName, value = val }, new JsonSerializerOptions { WriteIndented = true });
        }
        try {
            if (val is System.Collections.IEnumerable enumerable && val is not string) {
                List<string> items = [];
                int n = 0;
                foreach (object item in enumerable) {
                    if (++n > 60) {
                        items.Add("...(truncated)");
                        break;
                    }
                    items.Add(item?.ToString() ?? "null");
                }
                return JsonSerializer.Serialize(new { type = actual.FullName, count = items.Count, items }, new JsonSerializerOptions { WriteIndented = true });
            }
            Dictionary<string, string> snap = new(StringComparer.Ordinal);
            foreach (PropertyInfo p in actual.GetProperties(BindingFlags.Public | BindingFlags.Instance).Take(40)) {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || IsSecretSettingName(p.Name)) {
                    continue;
                }
                try {
                    object pv = p.GetValue(val);
                    snap[p.Name] = pv?.ToString() ?? "null";
                } catch {
                    // Skip throwing getter
                }
            }
            return JsonSerializer.Serialize(new {
                type = actual.FullName,
                toString = val.ToString(),
                properties = snap.Count > 0 ? snap : null
            }, new JsonSerializerOptions { WriteIndented = true });
        } catch {
            return $"{t?.FullName ?? actual.FullName}: {val}";
        }
    }

    static async Task<(bool ok, string output)> ExecuteSandboxedShellAsync(Context context, JsonElement argsEl, CancellationToken ct) {
        string command = GetArgString(argsEl, "command", "").Trim();
        if (string.IsNullOrWhiteSpace(command)) {
            return (false, "shell_exec requires {command: string}.");
        }
        if (command.Contains("files/bin", StringComparison.OrdinalIgnoreCase)) {
            return (false, "Rejected by W^X policy: never execute binaries from FilesDir/bin.");
        }
        int timeoutSec = GetArgInt(argsEl, "timeoutSec", 15, 1, 20);
        string cwd = GetArgString(argsEl, "cwd", context.FilesDir?.AbsolutePath ?? "/");
        if (!Directory.Exists(cwd)) {
            cwd = context.FilesDir?.AbsolutePath ?? "/";
        }

        ProcessStartInfo psi = new() {
            FileName = "/system/bin/sh",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) {
            return (false, "Failed to launch /system/bin/sh.");
        }
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        } catch (System.OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, $"shell_exec timed out after {timeoutSec}s.");
        }
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        return (proc.ExitCode == 0, JsonSerializer.Serialize(new {
            exitCode = proc.ExitCode,
            cwd,
            stdout = Truncate(AppLogger.SanitizeSensitiveText(stdout), 48_000),
            stderr = Truncate(AppLogger.SanitizeSensitiveText(stderr), 16_000)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ==================== DYNAMIC SETTINGS REFLECTION (SECRET-FILTERED) ====================

    static bool IsSecretSettingName(string name) =>
        SecretSettingTokens.Any(tok => name.Contains(tok, StringComparison.OrdinalIgnoreCase));

    static (bool ok, string output) ExecuteSettingsGet(AppSettingsStore store, JsonElement argsEl) {
        string key = GetArgString(argsEl, "key", "");
        string filter = GetArgString(argsEl, "filter", "");
        PropertyInfo[] props = typeof(AppSettingsStore).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Dictionary<string, object> map = new(StringComparer.Ordinal);

        foreach (PropertyInfo p in props) {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || IsSecretSettingName(p.Name)) {
                continue;
            }
            if (!string.IsNullOrEmpty(key) && !p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (!string.IsNullOrEmpty(filter) && !p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (!IsSimpleSettingType(p.PropertyType)) {
                continue;
            }
            try {
                map[p.Name] = p.GetValue(store);
            } catch {
                // Skip
            }
        }
        if (!string.IsNullOrEmpty(key) && map.Count == 0) {
            return (false, $"Setting '{key}' not found or protected.");
        }
        return (true, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    static (bool ok, string output) ExecuteSettingsSet(AppSettingsStore store, JsonElement argsEl) {
        Dictionary<string, JsonElement> updates = new(StringComparer.OrdinalIgnoreCase);
        if (argsEl.ValueKind == JsonValueKind.Object) {
            if (argsEl.TryGetProperty("values", out JsonElement valsEl) && valsEl.ValueKind == JsonValueKind.Object) {
                foreach (JsonProperty jp in valsEl.EnumerateObject()) {
                    updates[jp.Name] = jp.Value;
                }
            } else if (argsEl.TryGetProperty("key", out JsonElement kEl) && argsEl.TryGetProperty("value", out JsonElement vEl)) {
                string k = kEl.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(k)) {
                    updates[k] = vEl;
                }
            }
        }
        if (updates.Count == 0) {
            return (false, "settings_set requires {key, value} or {values: {k: v}}.");
        }

        List<string> applied = [];
        List<string> rejected = [];
        foreach (KeyValuePair<string, JsonElement> kv in updates) {
            if (IsSecretSettingName(kv.Key)) {
                rejected.Add($"{kv.Key}: protected secret");
                continue;
            }
            PropertyInfo prop = typeof(AppSettingsStore).GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite || !IsSimpleSettingType(prop.PropertyType)) {
                rejected.Add($"{kv.Key}: not a writable primitive setting");
                continue;
            }
            try {
                object converted = ConvertJsonToType(kv.Value, prop.PropertyType);
                prop.SetValue(store, converted);
                applied.Add($"{prop.Name}={converted}");
            } catch (Exception ex) {
                rejected.Add($"{kv.Key}: {ex.Message}");
            }
        }

        if (applied.Count > 0) {
            store.ApplyGlobals();
        }
        return (applied.Count > 0 && rejected.Count == 0,
            JsonSerializer.Serialize(new { applied, rejected }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static bool IsSimpleSettingType(Type t) =>
        t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(string);

    static object ConvertJsonToType(JsonElement el, Type target) {
        if (target == typeof(bool)) {
            return el.ValueKind == JsonValueKind.String ? bool.Parse(el.GetString()!) : el.GetBoolean();
        }
        if (target == typeof(int)) {
            return el.ValueKind == JsonValueKind.String ? int.Parse(el.GetString()!, CultureInfo.InvariantCulture) : el.GetInt32();
        }
        if (target == typeof(long)) {
            return el.ValueKind == JsonValueKind.String ? long.Parse(el.GetString()!, CultureInfo.InvariantCulture) : el.GetInt64();
        }
        if (target == typeof(float)) {
            return el.ValueKind == JsonValueKind.String ? float.Parse(el.GetString()!, CultureInfo.InvariantCulture) : el.GetSingle();
        }
        if (target == typeof(double)) {
            return el.ValueKind == JsonValueKind.String ? double.Parse(el.GetString()!, CultureInfo.InvariantCulture) : el.GetDouble();
        }
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString();
    }

    // ==================== CONFINED FILE SYSTEM & BUNDLED NATIVE TOOL PROBING ====================

    static List<string> GetAllowedReadRoots(Context context) {
        List<string> roots = [];
        void AddIfSet(string p) {
            if (!string.IsNullOrWhiteSpace(p)) {
                try {
                    roots.Add(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                } catch {
                    // Skip
                }
            }
        }
        AddIfSet(context.FilesDir?.AbsolutePath);
        AddIfSet(context.CacheDir?.AbsolutePath);
        AddIfSet(context.ApplicationInfo?.NativeLibraryDir);
        AddIfSet("/sdcard/Download/Cavernize");
        if (!string.IsNullOrEmpty(AppLogger.SharedLogPath)) {
            AddIfSet(Path.GetDirectoryName(AppLogger.SharedLogPath));
        }
        return roots;
    }

    static bool IsPathUnderRoots(string candidate, IEnumerable<string> roots, out string fullPath) {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(candidate)) {
            return false;
        }
        try {
            string norm = Path.GetFullPath(candidate);
            string normDir = norm.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string root in roots) {
                if (normDir.StartsWith(root, StringComparison.Ordinal)) {
                    fullPath = norm;
                    return true;
                }
            }
        } catch {
            // Invalid path
        }
        return false;
    }

    static (bool ok, string output) ExecuteFilesList(Context context, JsonElement argsEl) {
        string scope = GetArgString(argsEl, "scope", "logs").ToLowerInvariant();
        string subdir = GetArgString(argsEl, "subdir", "");
        string resolvedLogsDir = !string.IsNullOrEmpty(AppLogger.SharedLogPath)
            ? Path.GetDirectoryName(AppLogger.SharedLogPath)
            : Path.Combine(context.FilesDir?.AbsolutePath ?? "", "logs");
        string baseDir = scope switch {
            "files" => context.FilesDir?.AbsolutePath,
            "cache" => context.CacheDir?.AbsolutePath,
            "native" => context.ApplicationInfo?.NativeLibraryDir,
            "downloads" => "/sdcard/Download/Cavernize",
            _ => resolvedLogsDir
        };
        if (string.IsNullOrWhiteSpace(baseDir)) {
            return (false, "Scope root unavailable.");
        }
        string target = string.IsNullOrWhiteSpace(subdir) ? baseDir : Path.Combine(baseDir, subdir);
        if (!IsPathUnderRoots(target, GetAllowedReadRoots(context), out string safeDir)) {
            return (false, "Path traversal outside allowed app directories rejected.");
        }
        if (!Directory.Exists(safeDir)) {
            return (true, JsonSerializer.Serialize(new { directory = safeDir, exists = false, entries = Array.Empty<object>() }));
        }
        var entries = Directory.EnumerateFileSystemEntries(safeDir)
            .Take(200)
            .Select(p => {
                bool isDir = Directory.Exists(p);
                long size = isDir ? 0 : (File.Exists(p) ? new FileInfo(p).Length : 0);
                return new { name = Path.GetFileName(p), isDir, sizeBytes = size, path = p };
            })
            .ToList();
        return (true, JsonSerializer.Serialize(new { directory = safeDir, count = entries.Count, entries }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static (bool ok, string output) ExecuteFileRead(Context context, JsonElement argsEl) {
        string path = GetArgString(argsEl, "path", "");
        if (!IsPathUnderRoots(path, GetAllowedReadRoots(context), out string safePath)) {
            return (false, "Path outside allowed app/log directories rejected.");
        }
        if (!File.Exists(safePath)) {
            return (false, "File not found.");
        }
        if (Path.GetFileName(safePath).Contains("prefs", StringComparison.OrdinalIgnoreCase) && safePath.Contains("shared_prefs", StringComparison.OrdinalIgnoreCase)) {
            return (false, "Reading raw shared_prefs XML is blocked for secret safety; use settings_get.");
        }
        int maxChars = GetArgInt(argsEl, "maxChars", 24_000, 100, 60_000);
        bool tail = GetArgBool(argsEl, "tail", true);
        string text = File.ReadAllText(safePath, System.Text.Encoding.UTF8);
        if (text.Length > maxChars) {
            text = tail ? "...[head truncated]\n" + text[^maxChars..] : text[..maxChars] + "\n...[tail truncated]";
        }
        return (true, text);
    }

    static (bool ok, string output) ExecuteFileDelete(Context context, JsonElement argsEl) {
        string path = GetArgString(argsEl, "path", "");
        List<string> deletableRoots = [];
        if (!string.IsNullOrEmpty(context.CacheDir?.AbsolutePath)) {
            deletableRoots.Add(Path.GetFullPath(context.CacheDir.AbsolutePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        }
        if (!string.IsNullOrEmpty(context.FilesDir?.AbsolutePath)) {
            deletableRoots.Add(Path.GetFullPath(Path.Combine(context.FilesDir.AbsolutePath, "Logs")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        }
        if (!IsPathUnderRoots(path, deletableRoots, out string safePath)) {
            return (false, "file_delete is strictly confined to CacheDir and Logs directories.");
        }
        if (!File.Exists(safePath)) {
            return (false, "File not found.");
        }
        File.Delete(safePath);
        return (true, $"Deleted '{safePath}'.");
    }

    static async Task<(bool ok, string output)> ExecuteNativeToolProbeAsync(Context context, JsonElement argsEl, CancellationToken ct) {
        string tool = GetArgString(argsEl, "tool", "").Trim().ToLowerInvariant();
        string nativeDir = context.ApplicationInfo?.NativeLibraryDir ?? "";
        string binName = tool switch {
            "ffmpeg" => "libffmpeg.so",
            "truehdd" => "libtruehdd.so",
            _ => ""
        };
        if (string.IsNullOrEmpty(binName) || string.IsNullOrEmpty(nativeDir)) {
            return (false, "Only bundled NativeLibraryDir 'ffmpeg' or 'truehdd' tools are allowed.");
        }
        string exePath = Path.Combine(nativeDir, binName);
        if (!File.Exists(exePath)) {
            return (false, $"Bundled binary not found: {exePath}");
        }

        List<string> argList = [];
        if (argsEl.ValueKind == JsonValueKind.Object && argsEl.TryGetProperty("args", out JsonElement arrEl) && arrEl.ValueKind == JsonValueKind.Array) {
            foreach (JsonElement a in arrEl.EnumerateArray()) {
                string s = a.GetString() ?? "";
                if (s.Length > 512) {
                    return (false, "Argument exceeds 512 chars.");
                }
                argList.Add(s);
            }
        }
        if (argList.Count == 0) {
            argList.Add(tool == "ffmpeg" ? "-version" : "--version");
        }
        if (argList.Count > 24) {
            return (false, "Too many arguments (max 24).");
        }

        ProcessStartInfo psi = new() {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string a in argList) {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) {
            return (false, "Failed to start native binary.");
        }
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        } catch (System.OperationCanceledException) {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, "Native tool timed out after 20s.");
        }
        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);
        return (proc.ExitCode == 0, JsonSerializer.Serialize(new {
            tool,
            exitCode = proc.ExitCode,
            stdout = Truncate(stdout, 32_000),
            stderr = Truncate(stderr, 16_000)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string BuildNativeLibsStatusJson(Context context) {
        string nativeDir = context.ApplicationInfo?.NativeLibraryDir ?? "";
        string[] expected = ["libffmpeg.so", "libtruehdd.so", "libonnxruntime.so", "libcavernize_proaudio.so"];
        var libs = expected.Select(name => {
            string p = string.IsNullOrEmpty(nativeDir) ? "" : Path.Combine(nativeDir, name);
            bool exists = !string.IsNullOrEmpty(p) && File.Exists(p);
            long size = exists ? new FileInfo(p).Length : 0;
            return new { name, exists, sizeBytes = size };
        }).ToList();
        return JsonSerializer.Serialize(new {
            nativeLibraryDir = nativeDir,
            abiList = Build.SupportedAbis ?? [],
            proAudioBridgeAvailable = NativeProAudioBridge.IsAvailable,
            libs
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    static string BuildAudioRoutesSnapshot(Context context) {
        StringBuilder sb = new();
        sb.AppendLine($"ProAudio NDK Bridge: available={NativeProAudioBridge.IsAvailable}, ABI={NativeProAudioBridge.AbiVersion}, PathA={NativeProAudioBridge.SupportsPathAAudio}, PathB={NativeProAudioBridge.SupportsPathBLibUsb}");
        if (context?.GetSystemService(Context.AudioService) is AudioManager am) {
            PackageManager pm = context.PackageManager;
            sb.AppendLine($"OS Audio: sampleRate={am.GetProperty(AudioManager.PropertyOutputSampleRate)} Hz, framesPerBuf={am.GetProperty(AudioManager.PropertyOutputFramesPerBuffer)}, lowLatency={pm?.HasSystemFeature(PackageManager.FeatureAudioLowLatency)}, proAudio={pm?.HasSystemFeature(PackageManager.FeatureAudioPro)}");
            AudioDeviceInfo[] outs = am.GetDevices(GetDevicesTargets.Outputs) ?? [];
            sb.AppendLine($"Outputs ({outs.Length}):");
            foreach (AudioDeviceInfo d in outs) {
                int[] srs = d.GetSampleRates() ?? [];
                sb.AppendLine($"  - [{d.Id}] {d.Type} '{d.ProductName}' sr=[{string.Join(",", srs)}]");
            }
            AudioDeviceInfo[] ins = am.GetDevices(GetDevicesTargets.Inputs) ?? [];
            sb.AppendLine($"Inputs ({ins.Length}):");
            foreach (AudioDeviceInfo d in ins) {
                int[] srs = d.GetSampleRates() ?? [];
                sb.AppendLine($"  - [{d.Id}] {d.Type} '{d.ProductName}' sr=[{string.Join(",", srs)}]");
            }
        }
        return sb.ToString();
    }

    static string BuildMemoryJson(Context context) {
        long gcMb = GC.GetTotalMemory(false) / (1024 * 1024);
        long availMb = -1, totalMb = -1, threshMb = -1;
        bool lowMem = false;
        if (context?.GetSystemService(Context.ActivityService) is ActivityManager am) {
            ActivityManager.MemoryInfo mi = new();
            am.GetMemoryInfo(mi);
            availMb = mi.AvailMem / (1024 * 1024);
            totalMb = mi.TotalMem / (1024 * 1024);
            threshMb = mi.Threshold / (1024 * 1024);
            lowMem = mi.LowMemory;
        }
        return JsonSerializer.Serialize(new {
            availRamMb = availMb,
            totalRamMb = totalMb,
            thresholdMb = threshMb,
            lowMemory = lowMem,
            managedGcMb = gcMb,
            utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ==================== ANDROID KEYSTORE HELPERS ====================

    static IKey GetOrCreatePairKeystoreKey() {
        KeyStore ks = KeyStore.GetInstance("AndroidKeyStore");
        ks.Load(null);
        if (ks.ContainsAlias(PairKeyAlias)) {
            IKey existing = ks.GetKey(PairKeyAlias, null);
            if (existing != null) {
                return existing;
            }
        }
        KeyGenerator kg = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        KeyGenParameterSpec spec = new KeyGenParameterSpec.Builder(
                PairKeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        kg.Init(spec);
        return kg.GenerateKey();
    }

    static string EncryptWithKeystore(string plain) {
        try {
            IKey key = GetOrCreatePairKeystoreKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
            byte[] iv = cipher.GetIV();
            byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plain);
            byte[] cipherBytes = cipher.DoFinal(plainBytes);
            return $"{KeystorePrefix}{Convert.ToBase64String(iv)}:{Convert.ToBase64String(cipherBytes)}";
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Live Bridge Keystore encrypt unavailable ({ex.GetType().Name})");
            return "";
        }
    }

    static string DecryptWithKeystore(string stored) {
        try {
            if (!stored.StartsWith(KeystorePrefix, StringComparison.Ordinal)) {
                return "";
            }
            string rest = stored[KeystorePrefix.Length..];
            int sep = rest.IndexOf(':');
            if (sep <= 0 || sep >= rest.Length - 1) {
                return "";
            }
            byte[] iv = Convert.FromBase64String(rest[..sep]);
            byte[] cipherBytes = Convert.FromBase64String(rest[(sep + 1)..]);
            IKey key = GetOrCreatePairKeystoreKey();
            Cipher cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
            byte[] plainBytes = cipher.DoFinal(cipherBytes);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"Live Bridge Keystore decrypt failed ({ex.GetType().Name}); regenerate pairing key.");
            return "";
        }
    }

    // ==================== JSON ARG & HTTP HELPERS ====================

    static string GetArgString(JsonElement el, string name, string fallback) {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String) {
            return p.GetString() ?? fallback;
        }
        return fallback;
    }

    static int GetArgInt(JsonElement el, string name, int fallback, int min, int max) {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement p) && p.TryGetInt32(out int v)) {
            return Math.Clamp(v, min, max);
        }
        return Math.Clamp(fallback, min, max);
    }

    static bool GetArgBool(JsonElement el, string name, bool fallback) {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement p) &&
            (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)) {
            return p.GetBoolean();
        }
        return fallback;
    }

    static double? GetArgNullableDouble(JsonElement el, string name) {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement p) && p.TryGetDouble(out double d)) {
            return d;
        }
        return null;
    }

    static string GenerateEphemeralSessionCode() {
        byte[] bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        StringBuilder sb = new(32);
        sb.Append("CAV");
        for (int i = 0; i < bytes.Length; i++) {
            if (i % 4 == 0) {
                sb.Append('-');
            }
            sb.Append(CodeAlphabet[bytes[i] & 31]);
        }
        return sb.ToString();
    }

    static HttpRequestMessage CreateTokenRequest(HttpMethod method, string url, string sessionCode) {
        HttpRequestMessage req = new(method, url);
        req.Headers.Add("X-Session-Token", sessionCode);
        return req;
    }

    static string Truncate(string text, int maxLen) =>
        string.IsNullOrEmpty(text) || text.Length <= maxLen ? (text ?? "") : text[..maxLen] + "\n... [truncated]";

    static string TruncateForCommand(string cmd, JsonElement argsEl, string text, int maxLen) {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen) {
            return text ?? "";
        }
        bool keepTail = string.Equals(cmd, "log_tail", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(cmd, "file_read", StringComparison.OrdinalIgnoreCase) && GetArgBool(argsEl, "tail", true));
        return keepTail
            ? "... [earlier lines truncated]\n" + text[^maxLen..]
            : text[..maxLen] + "\n... [truncated]";
    }

    internal static void NotifyStateChanged() {
        try {
            StateChanged?.Invoke();
        } catch {
            // Never disrupt polling loop
        }
    }
}
