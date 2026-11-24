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
    Task<string> QueryLibraryJsonAsync(string query, int limit, bool musicOnly = false);
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
    Task<string> GetRuntimeTelemetryJsonAsync();
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
public static partial class LiveDiagnosticBridgeService {
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
        new("get_design_tokens", 0, "Export active Cavernize One UI 9 runtime design tokens JSON for zero-quota companion UI sync", "{}"),
        new("logcat_tail", 0, "Read Cavernize own-UID Android logcat cleanly (zero shell, filters fork noise)", "{lines?:10..800, minLevel?:'V'|'D'|'I'|'W'|'E'|'F', buffer?:'main'|'system'|'crash'|'events'}"),
        new("anomalies", 0, "Read live in-memory RuntimeAnomalyRing captured from critical logs & chaos hunts", "{limit?:1..160, clear?:bool}"),
        new("runtime_telemetry", 0, "Inspect live FrameMetrics (P50/P95/P99/jank/frozen), ApplicationExitInfo, AAudio/AudioTrack XRuns & thermal QoS", "{}"),
        new("batch", 0, "Execute up to 15 sequential commands in one round-trip (enforces each command's tier)", "{commands:[{cmd:string, args?:object}]}"),

        // Tier 1: Interactive App Control, QA & Total-App Live Audit
        new("ultra_check", 1, "Run 12-layer Total + Acoustic Differential + Live UI/Modal/Controls/Perf-Jank self-audit", "{}"),
        new("chaos_hunt", 1, "Run high-speed in-process adversarial chaos & concurrent DSP/RingBuffer/malformed-file bug hunter", "{rounds?:1..20}"),
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

    /// <summary>
    /// Executes a single Live Bridge command dispatched locally over signature-protected Android IPC
    /// from the same-signed OmniAgent Companion app (zero duplicate cloud polling or extra biometric prompts).
    /// </summary>
    public static async Task<(bool ok, string body, long elapsedMs)> ExecuteLocalIpcCommandAsync(
        Context context,
        AppSettingsStore store,
        int maxTier,
        string rawCmd,
        string argsJson,
        CancellationToken ct) {
        const int MaxIpcBinderChars = 220_000;
        string cmd = (rawCmd ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(cmd)) {
            return (false, "Empty IPC command.", 0);
        }
        Stopwatch sw = Stopwatch.StartNew();
        JsonDocument argsDoc = null;
        try {
            JsonElement argsEl = default;
            if (!string.IsNullOrWhiteSpace(argsJson)) {
                try {
                    argsDoc = JsonDocument.Parse(argsJson);
                    if (argsDoc.RootElement.ValueKind == JsonValueKind.Object) {
                        argsEl = argsDoc.RootElement;
                    }
                } catch {
                    argsEl = default;
                }
            }
            int clampedTier = Math.Clamp(maxTier, 0, 2);
            (bool ok, string output) = await ExecuteSingleCommandAsync(
                context,
                store,
                clampedTier,
                cmd,
                argsEl,
                legacyLines: 200,
                legacyErrorsOnly: false,
                ct,
                depth: 0).ConfigureAwait(false);
            sw.Stop();
            string safeBody = Truncate(AppLogger.SanitizeSensitiveText(output ?? ""), MaxIpcBinderChars);
            lock (stateLock) {
                commandsHandled++;
                lastEventText = $"IPC '{cmd}' ({sw.ElapsedMilliseconds}ms)";
            }
            AppLogger.Info("IPC", $"OmniAgent local IPC handled '{cmd}' (T{clampedTier}, ok={ok}, {sw.ElapsedMilliseconds}ms)");
            NotifyStateChanged();
            return (ok, safeBody, sw.ElapsedMilliseconds);
        } catch (Exception ex) {
            sw.Stop();
            AppLogger.Warn("IPC", $"OmniAgent local IPC '{cmd}' failed: {ex.Message}");
            return (false, $"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        } finally {
            argsDoc?.Dispose();
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
