using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
        new("batch", 0, "Execute up to 15 sequential commands in one round-trip (enforces each command's tier)", "{commands:[{cmd:string, args?:object}]}"),

        // Tier 1: Interactive App Control & QA
        new("settings_set", 1, "Update non-secret AppSettingsStore property via reflection & refresh UI", "{key:string, value:any} or {values:{k:v}}"),
        new("tab_switch", 1, "Switch MainActivity bottom navigation tab", "{index?:0..4, name?:'player'|'room'|'studio'|'stems'|'settings'}"),
        new("playback_control", 1, "Control real-time player playback", "{action:'play'|'pause'|'toggle'|'stop'|'next'|'prev'|'seek'|'seek_rel'|'loop', value?:number}"),
        new("open_file", 1, "Open a local audio/video file in Cavernize", "{path:string, autoPlay?:bool, tab?:0..4}"),
        new("ui_action", 1, "Invoke high-level app actions (refresh_ui, rescan_library, sync_logs_cloud, check_updates, reset_head_pose, bring_to_front, toast)", "{action:string, message?:string}"),
        new("ui_click", 1, "Find and click a visible clickable View matching text/contentDescription", "{text:string, index?:number}"),
        new("cache_clear", 1, "Clear temporary app cache files cleanly", "{scope?:'cache'|'logs'}"),
        new("audio_probe", 1, "Run fast AudioQualityAnalyzer metadata check on a local file", "{path:string}"),

        // Tier 2: Elevated Visual & Bundled Native Tool Probing (biometric-gated)
        new("screenshot_ui", 2, "Capture downscaled JPEG base64 screenshot of MainActivity window", "{maxWidth?:360..1080, quality?:40..90}"),
        new("native_tool_exec", 2, "Run bundled NativeLibraryDir libffmpeg.so or libtruehdd.so probe w/ ArgumentList (zero shell)", "{tool:'ffmpeg'|'truehdd', args:[string]}"),
        new("file_delete", 2, "Delete a single confined file inside CacheDir or Logs directory", "{path:string}"),
        new("settings_reset", 2, "Reset one specific non-secret setting key or clear temporary bridge cache", "{key:string}")
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
            if (!ok) {
                return (false, "Relay responded without ok=true. Verify RELAY_KV binding.");
            }
            return supportsWake
                ? (true, $"Relay v{ver} online (Biometric Wake + Flexible Cmds ready)")
                : (true, $"Relay v{ver} online (update Worker JS to v2 for Biometric Wake & flexible cmds)");
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

    internal static string ComputeWakeChannelHash(string pairSecretHex) {
        byte[] input = System.Text.Encoding.UTF8.GetBytes($"wake-channel-v2:{pairSecretHex.ToLowerInvariant()}");
        byte[] digest = SHA256.HashData(input);
        return Convert.ToHexString(digest).ToLowerInvariant();
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
                        bridgeVersion = 2,
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
                        bridgeVersion = 2,
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
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, fullSuite: false, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    return (true, rep.ToMarkdown());
                }
                case "full_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, fullSuite: true, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    return (true, rep.ToMarkdown());
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
                        results.Add(new { cmd = subCmd, ok = subOk, body = Truncate(subOut, 24_000) });
                    }
                    return (true, JsonSerializer.Serialize(new { count = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
                }
                default:
                    return (false, $"Command '{cmd}' is not implemented.");
            }
        } catch (Exception ex) {
            AppLogger.Error("CLOUD", $"Live Bridge v2 command '{cmd}' error", ex);
            return (false, $"Command '{cmd}' failed: {ex.GetType().Name}: {ex.Message}");
        }
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
        string baseDir = scope switch {
            "files" => context.FilesDir?.AbsolutePath,
            "cache" => context.CacheDir?.AbsolutePath,
            "native" => context.ApplicationInfo?.NativeLibraryDir,
            "downloads" => "/sdcard/Download/Cavernize",
            _ => Path.Combine(context.FilesDir?.AbsolutePath ?? "", "Logs")
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

    internal static void NotifyStateChanged() {
        try {
            StateChanged?.Invoke();
        } catch {
            // Never disrupt polling loop
        }
    }
}
