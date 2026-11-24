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
using CavernizeAndroid.Theme;

using Java.Security;

using Javax.Crypto;
using Javax.Crypto.Spec;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace CavernizeAndroid.Services;


public static partial class LiveDiagnosticBridgeService {
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
                case "get_design_tokens": {
                    OneUiTheme.Initialize(context);
                    return (true, OneUiTheme.ExportDesignTokensJson("cavernize-ipc"));
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
                case "logcat_tail": {
                    int lines = GetArgInt(argsEl, "lines", 150, 10, 800);
                    string minLevel = GetArgString(argsEl, "minLevel", "W");
                    string buffer = GetArgString(argsEl, "buffer", "main");
                    return await LiveChaosBugHunter.ReadOwnLogcatAsync(lines, minLevel, buffer, ct).ConfigureAwait(false);
                }
                case "anomalies": {
                    int limit = GetArgInt(argsEl, "limit", 80, 1, 160);
                    bool clear = GetArgBool(argsEl, "clear", false);
                    return (true, LiveChaosBugHunter.GetAnomaliesJson(limit, clear));
                }
                case "chaos_hunt": {
                    int rounds = GetArgInt(argsEl, "rounds", 6, 1, 20);
                    return await LiveChaosBugHunter.RunChaosHuntAsync(context, store, rounds, ct).ConfigureAwait(false);
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
                case "runtime_telemetry":
                    return host != null
                        ? (true, await host.GetRuntimeTelemetryJsonAsync().ConfigureAwait(false))
                        : (true, AndroidRuntimeTelemetry.BuildRuntimeTelemetryJson(context, null));
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
                    bool musicOnly = GetArgBool(argsEl, "musicOnly", false);
                    if (host != null) {
                        return (true, await host.QueryLibraryJsonAsync(query, limit, musicOnly).ConfigureAwait(false));
                    }
                    MediaLibrarySnapshot snap = MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(context);
                    IEnumerable<LibraryTrackItem> source = snap?.Tracks ?? [];
                    if (musicOnly) {
                        source = source.Where(t => !t.HasNoAudioTrack && !t.IsVideo && !t.IsLikelyNonMusic);
                    }
                    var matched = source.Where(t =>
                        string.IsNullOrWhiteSpace(query) ||
                        (t.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (t.FilePath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
                    if (string.IsNullOrWhiteSpace(query)) {
                        matched = matched.OrderByDescending(t => !string.IsNullOrWhiteSpace(t.Album) && !t.IsVideo && !t.IsLikelyNonMusic)
                            .ThenByDescending(t => t.DurationMs > 0);
                    }
                    var tracks = matched
                        .Take(limit)
                        .Select(t => new { t.Title, t.Artist, t.Album, t.DurationMs, t.FilePath })
                        .ToList();
                    return (true, JsonSerializer.Serialize(new { totalCached = snap?.Tracks?.Count ?? 0, returned = tracks.Count, musicOnly, tracks }, new JsonSerializerOptions { WriteIndented = true }));
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
        int thermalCode = AndroidRuntimeTelemetry.GetThermalStatusCode(context);
        return JsonSerializer.Serialize(new {
            availRamMb = availMb,
            totalRamMb = totalMb,
            thresholdMb = threshMb,
            lowMemory = lowMem,
            managedGcMb = gcMb,
            thermalStatus = AndroidRuntimeTelemetry.DescribeThermalStatus(thermalCode),
            frameMetrics = AndroidRuntimeTelemetry.BuildFrameMetricsObject(),
            exitReasons = AndroidRuntimeTelemetry.ReadHistoricalExitReasons(context, 3),
            utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

}
