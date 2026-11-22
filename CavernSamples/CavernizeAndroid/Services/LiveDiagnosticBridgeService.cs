using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;

using CavernizeAndroid.Audio;

namespace CavernizeAndroid.Services;

/// <summary>
/// Ephemeral, opt-in 15-minute Live Diagnostic Bridge connecting Cavernize Android to the user's
/// own free Cloudflare Worker relay (<c>cloudflare_live_relay_worker.js</c>).
///
/// Security invariants:
/// - Always OFF by default; requires explicit user tap to start and auto-expires after 15 minutes.
/// - Generates a fresh 120-bit CSPRNG session code per session (sent only via X-Session-Token header).
/// - Accepts ONLY a strict whitelist of 7 read-only diagnostic commands (zero arbitrary execution).
/// - Enforces HTTPS-only public relay URLs and sanitizes all outbound diagnostic text via AppLogger.
/// </summary>
public static class LiveDiagnosticBridgeService {
    public const int SessionDurationSeconds = 900; // 15 minutes
    const int PollIntervalMs = 2500;
    const int MaxResponseChars = 140_000;

    static readonly char[] CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray(); // 32 unambiguous chars (5 bits/char)
    static readonly HttpClient http = BuildClient();
    static readonly object stateLock = new();

    static CancellationTokenSource sessionCts;
    static string activeSessionCode = "";
    static string activeRelayUrl = "";
    static DateTime sessionExpiresUtc = DateTime.MinValue;
    static int commandsHandled;
    static string lastEventText = "";

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

    static HttpClient BuildClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid-LiveBridge", "1.0"));
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
            return ok
                ? (true, $"Relay online ({baseUrl})")
                : (false, "Relay responded without ok=true. Verify RELAY_KV binding.");
        } catch (Exception ex) {
            return (false, $"Relay connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a fresh 15-minute ephemeral diagnostic session on the user's Cloudflare Worker relay
    /// and starts polling for whitelisted read-only diagnostic commands.
    /// </summary>
    public static async Task<(bool ok, string sessionCode, string message)> StartSessionAsync(
        Context context,
        AppSettingsStore store,
        CancellationToken ct = default) {
        if (context == null || store == null) {
            return (false, "", "Context unavailable.");
        }
        if (!TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
            return (false, "", "Configure a valid https:// Cloudflare Worker URL first.");
        }

        await StopSessionAsync().ConfigureAwait(false);

        string code = GenerateEphemeralSessionCode();
        (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
        string payload = JsonSerializer.Serialize(new {
            ttlSeconds = SessionDurationSeconds,
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
            sessionExpiresUtc = DateTime.UtcNow.AddSeconds(SessionDurationSeconds);
            commandsHandled = 0;
            lastEventText = "Waiting for command...";
        }

        AppLogger.Info("CLOUD", $"Live Diagnostic Bridge started (TTL={SessionDurationSeconds}s, host={new Uri(baseUrl).Host})");
        NotifyStateChanged();

        Context appCtx = context.ApplicationContext ?? context;
        _ = Task.Run(() => PollLoopAsync(appCtx, store, baseUrl, code, cts.Token));
        return (true, code, "Live Bridge active for 15 minutes.");
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
            AppLogger.Info("CLOUD", "Live Diagnostic Bridge stopped.");
            NotifyStateChanged();
        }

        if (!string.IsNullOrEmpty(oldUrl) && !string.IsNullOrEmpty(oldCode)) {
            try {
                using HttpRequestMessage closeReq = CreateTokenRequest(HttpMethod.Delete, $"{oldUrl}/v1/session/close", oldCode);
                using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(4));
                using HttpResponseMessage _ = await http.SendAsync(closeReq, closeCts.Token).ConfigureAwait(false);
            } catch {
                // Best-effort remote close; KV TTL guarantees expiration regardless
            }
        }
    }

    static async Task PollLoopAsync(Context context, AppSettingsStore store, string baseUrl, string code, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < sessionExpiresUtc) {
                try {
                    using HttpRequestMessage pollReq = CreateTokenRequest(HttpMethod.Get, $"{baseUrl}/v1/session/poll", code);
                    using HttpResponseMessage pollResp = await http.SendAsync(pollReq, ct).ConfigureAwait(false);
                    if (pollResp.StatusCode == HttpStatusCode.NotFound) {
                        // Session expired remotely
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
                                ? Math.Clamp(parsedLines, 20, 500)
                                : 150;
                            bool errorsOnly = cmdEl.TryGetProperty("errorsOnly", out JsonElement eEl) && eEl.GetBoolean();

                            if (!string.IsNullOrEmpty(reqId) && !string.IsNullOrEmpty(cmd)) {
                                await ExecuteAndRespondAsync(context, store, baseUrl, code, reqId, cmd, lines, errorsOnly, ct).ConfigureAwait(false);
                            }
                        }
                    }
                } catch (System.OperationCanceledException) {
                    break;
                } catch {
                    // Transient network hiccup; keep polling until TTL expires
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
                    sessionExpiresUtc = DateTime.MinValue;
                }
            }
            if (needClear) {
                AppLogger.Info("CLOUD", "Live Diagnostic Bridge session ended (expired or closed).");
                NotifyStateChanged();
            }
        }
    }

    static async Task ExecuteAndRespondAsync(
        Context context,
        AppSettingsStore store,
        string baseUrl,
        string code,
        string requestId,
        string rawCmd,
        int lines,
        bool errorsOnly,
        CancellationToken ct) {
        string cmd = rawCmd.Trim().ToLowerInvariant();
        Stopwatch sw = Stopwatch.StartNew();
        bool ok = true;
        string output;

        lock (stateLock) {
            lastEventText = $"Running '{cmd}'...";
        }
        NotifyStateChanged();

        try {
            switch (cmd) {
                case "ping": {
                    (string ver, long build) = AppUpdateService.GetInstalledVersion(context);
                    output = JsonSerializer.Serialize(new {
                        status = "online",
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
                    }, new JsonSerializerOptions { WriteIndented = true });
                    break;
                }
                case "quick_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, fullSuite: false, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    output = rep.ToMarkdown();
                    break;
                }
                case "full_check": {
                    SelfCheckReport rep = await SelfDiagnosticEngine.RunAsync(context, store, fullSuite: true, ct: ct).ConfigureAwait(false);
                    store.LastSelfCheckUtc = rep.ExecutedAtUtc;
                    store.LastSelfCheckSummary = rep.CompactSummary;
                    output = rep.ToMarkdown();
                    break;
                }
                case "summary": {
                    output = AppLogger.BuildDiagnosticSummaryJson(context, store);
                    break;
                }
                case "log_tail": {
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
                        output = filtered.Length > 0 ? string.Join("\n", filtered) : "(No WARN/ERROR/FATAL lines in recent log tail)";
                    } else {
                        output = rawTail;
                    }
                    break;
                }
                case "audio_routes": {
                    output = BuildAudioRoutesSnapshot(context);
                    break;
                }
                case "memory": {
                    output = BuildMemoryJson(context);
                    break;
                }
                default:
                    ok = false;
                    output = $"Rejected: command '{cmd}' is not in the read-only diagnostic whitelist.";
                    AppLogger.Warn("CLOUD", $"Live Bridge rejected non-whitelisted command '{cmd}'");
                    break;
            }
        } catch (Exception ex) {
            ok = false;
            output = $"Command '{cmd}' failed: {ex.GetType().Name}: {ex.Message}";
            AppLogger.Error("CLOUD", $"Live Bridge command '{cmd}' error", ex);
        }

        sw.Stop();
        string safeBody = Truncate(AppLogger.SanitizeSensitiveText(output ?? ""), MaxResponseChars);
        string respPayload = JsonSerializer.Serialize(new {
            requestId,
            cmd,
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
        AppLogger.Info("CLOUD", $"Live Bridge handled '{cmd}' (ok={ok}, {sw.ElapsedMilliseconds}ms)");
        NotifyStateChanged();
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

    static string GenerateEphemeralSessionCode() {
        // 24 chars from 32-char alphabet = 120 bits of CSPRNG entropy, grouped for easy readability
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

    static void NotifyStateChanged() {
        try {
            StateChanged?.Invoke();
        } catch {
            // Never disrupt polling loop
        }
    }
}
