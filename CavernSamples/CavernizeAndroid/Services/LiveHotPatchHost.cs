using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

using Android.Content;

namespace CavernizeAndroid.Services;

/// <summary>
/// Execution context injected into hot-loaded C# probes executed purely in RAM via
/// <c>AssemblyLoadContext.LoadFromStream</c> (100% W^X compliant, zero disk binary execution).
/// </summary>
public sealed class HotProbeContext {
    readonly List<string> logs = [];
    readonly object logLock = new();

    public HotProbeContext(
        Context appContext,
        AppSettingsStore settings,
        ILiveBridgeAppHost host,
        string argsJson,
        CancellationToken cancellationToken) {
        AppContext = appContext;
        Settings = settings;
        Host = host;
        ArgsJson = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;
        CancellationToken = cancellationToken;
    }

    /// <summary>Android application context.</summary>
    public Context AppContext { get; }

    /// <summary>Active Cavernize settings store.</summary>
    public AppSettingsStore Settings { get; }

    /// <summary>Live MainActivity UI/engine host (may be null in headless background mode).</summary>
    public ILiveBridgeAppHost Host { get; }

    /// <summary>Optional JSON arguments string passed by caller.</summary>
    public string ArgsJson { get; }

    /// <summary>Bounded execution cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Record a diagnostic log line returned in the probe result payload.</summary>
    public void Log(string line) {
        if (string.IsNullOrEmpty(line)) {
            return;
        }
        lock (logLock) {
            if (logs.Count < 200) {
                logs.Add(line.Length > 2000 ? line[..2000] : line);
            }
        }
    }

    /// <summary>Marshal a UI inspection or rendering action onto the MainActivity UI thread.</summary>
    public async Task<(bool ok, string output)> RunOnUiAsync(Func<(bool ok, string output)> action) {
        if (Host == null) {
            return (false, "MainActivity host is not attached.");
        }
        return await Host.ExecuteOnUiThreadReflectAsync(action).ConfigureAwait(false);
    }

    internal List<string> SnapshotLogs() {
        lock (logLock) {
            return [.. logs];
        }
    }
}

/// <summary>
/// Optional interface for hot-loaded C# probes executed by <see cref="LiveHotPatchHost"/>.
/// Probes may also simply define a public static <c>RunAsync(HotProbeContext)</c> or <c>Run(HotProbeContext)</c> method.
/// </summary>
public interface IHotProbe {
    /// <summary>Execute the hot-loaded probe inside the live Cavernize process.</summary>
    Task<string> ExecuteAsync(HotProbeContext context);
}

/// <summary>
/// Cryptographically authenticated, collectible in-memory C# hot-execution host for Live Bridge v3+.
/// Verifies HMAC-SHA256 against the Android Keystore pairing secret before loading any managed IL bytes
/// into an isolated collectible <see cref="AssemblyLoadContext"/> and unloads immediately after execution.
/// </summary>
public static class LiveHotPatchHost {
    const int MaxDllBytes = 512 * 1024;
    const long MaxClockSkewSeconds = 180;

    sealed class CollectibleHotLoadContext : AssemblyLoadContext {
        public CollectibleHotLoadContext(string name) : base(name, isCollectible: true) { }

        protected override Assembly Load(AssemblyName assemblyName) {
            // Resolve Cavern, Cavern.Format, Cavernize.Logic, CavernizeAndroid & Android BCL from host ALC
            if (assemblyName?.Name != null) {
                string targetName = assemblyName.Name;
                if (string.Equals(targetName, "CavernizeAndroid.TypeCheck", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetName, "CavernizeAndroidShadow", StringComparison.OrdinalIgnoreCase)) {
                    targetName = "CavernizeAndroid";
                }
                foreach (Assembly loaded in Default.Assemblies) {
                    if (string.Equals(loaded.GetName().Name, targetName, StringComparison.OrdinalIgnoreCase)) {
                        return loaded;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>Return runtime readiness diagnostics for in-memory hot-probe execution.</summary>
    public static string GetStatusJson(AppSettingsStore store) {
        bool hasKey = LiveDiagnosticBridgeService.HasPairedSecret(store);
        return JsonSerializer.Serialize(new {
            supported = true,
            dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            dynamicCodeCompiled = RuntimeFeature.IsDynamicCodeCompiled,
            wxCompliant = true,
            mechanism = "AssemblyLoadContext(isCollectible:true).LoadFromStream(MemoryStream)",
            hmacAuthRequired = true,
            pairKeyConfigured = hasKey,
            maxDllBytes = MaxDllBytes,
            maxClockSkewSeconds = MaxClockSkewSeconds
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Verify HMAC-SHA256 signature, load compiled C# IL bytes purely in RAM, run probe with timeout,
    /// and unload the collectible <see cref="AssemblyLoadContext"/> cleanly.
    /// </summary>
    public static async Task<(bool ok, string json)> ExecuteSignedProbeAsync(
        Context context,
        AppSettingsStore store,
        ILiveBridgeAppHost host,
        JsonElement argsEl,
        CancellationToken ct) {
        if (argsEl.ValueKind != JsonValueKind.Object) {
            return (false, "hot_exec requires JSON args: {requestId, timestampUnix, dllSha256, signature, dllBase64, entryType?, argsJson?, timeoutSec?}");
        }

        string pairHex = LiveDiagnosticBridgeService.LoadPairSecretHex(store);
        if (string.IsNullOrEmpty(pairHex)) {
            return (false, "hot_exec rejected: 256-bit Pairing Key is not configured in Android Keystore.");
        }

        string reqId = ReadString(argsEl, "requestId");
        long tsUnix = ReadInt64(argsEl, "timestampUnix");
        string sigHex = ReadString(argsEl, "signature").Trim().ToLowerInvariant();
        string dllB64 = ReadString(argsEl, "dllBase64");
        string entryTypeName = ReadString(argsEl, "entryType");
        string probeArgsJson = ReadString(argsEl, "argsJson");
        int timeoutSec = Math.Clamp(ReadInt32(argsEl, "timeoutSec", 15), 2, 45);

        if (string.IsNullOrEmpty(reqId) || string.IsNullOrEmpty(sigHex) || string.IsNullOrEmpty(dllB64)) {
            return (false, "hot_exec rejected: missing requestId, signature, or dllBase64.");
        }

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - tsUnix) > MaxClockSkewSeconds) {
            return (false, $"hot_exec rejected: timestamp skew {Math.Abs(nowUnix - tsUnix)}s exceeds {MaxClockSkewSeconds}s.");
        }

        byte[] dllBytes;
        try {
            dllBytes = Convert.FromBase64String(dllB64);
        } catch {
            return (false, "hot_exec rejected: invalid dllBase64 payload.");
        }
        if (dllBytes.Length < 128 || dllBytes.Length > MaxDllBytes) {
            return (false, $"hot_exec rejected: DLL size {dllBytes.Length} bytes outside allowed range (128..{MaxDllBytes}).");
        }

        // Verify constant-time HMAC-SHA256 before loading a single IL byte
        string actualSha = Convert.ToHexString(SHA256.HashData(dllBytes)).ToLowerInvariant();
        byte[] keyBytes = Convert.FromHexString(pairHex);
        string canonical = $"hot-exec-v1|{reqId}|{tsUnix}|{actualSha}";
        byte[] expectedMac = HMACSHA256.HashData(keyBytes, System.Text.Encoding.UTF8.GetBytes(canonical));
        byte[] providedMac;
        try {
            providedMac = Convert.FromHexString(sigHex);
        } catch {
            return (false, "hot_exec rejected: malformed hex signature.");
        }
        if (providedMac.Length != expectedMac.Length || !CryptographicOperations.FixedTimeEquals(providedMac, expectedMac)) {
            AppLogger.Warn("CLOUD", $"LiveHotPatchHost rejected invalid HMAC signature for reqId={reqId}");
            return (false, "hot_exec rejected: HMAC-SHA256 signature verification failed.");
        }

        Stopwatch sw = Stopwatch.StartNew();
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        HotProbeContext probeCtx = new(context, store, host, probeArgsJson, timeoutCts.Token);
        WeakReference alcWeakRef = null;
        string outputText = "";
        string executedEntry = "";
        bool ok = false;
        string errorText = "";

        try {
            (ok, executedEntry, outputText, errorText, alcWeakRef) = await RunIsolatedAsync(
                dllBytes, entryTypeName, probeCtx).ConfigureAwait(false);
        } catch (System.OperationCanceledException) {
            ok = false;
            errorText = $"Hot probe timed out or canceled after {timeoutSec}s.";
        } catch (Exception ex) {
            ok = false;
            errorText = $"{ex.GetType().Name}: {ex.Message}";
        } finally {
            sw.Stop();
            // Encourage collectible AssemblyLoadContext reclaim cleanly
            if (alcWeakRef != null) {
                for (int i = 0; i < 3 && alcWeakRef.IsAlive; i++) {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        AppLogger.Info("CLOUD", $"LiveHotPatchHost executed {executedEntry} (ok={ok}, {sw.ElapsedMilliseconds}ms, bytes={dllBytes.Length})");
        string resultJson = JsonSerializer.Serialize(new {
            ok,
            requestId = reqId,
            dllSha256 = actualSha,
            dllBytes = dllBytes.Length,
            entry = executedEntry,
            elapsedMs = sw.ElapsedMilliseconds,
            unloaded = alcWeakRef == null || !alcWeakRef.IsAlive,
            output = outputText,
            error = errorText,
            logs = probeCtx.SnapshotLogs()
        }, new JsonSerializerOptions { WriteIndented = true });
        return (ok, resultJson);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<(bool ok, string entry, string output, string error, WeakReference alcRef)> RunIsolatedAsync(
        byte[] dllBytes,
        string entryTypeName,
        HotProbeContext probeCtx) {
        CollectibleHotLoadContext alc = new($"HotProbe_{Guid.NewGuid():N}");
        WeakReference alcRef = new(alc, trackResurrection: true);
        try {
            using MemoryStream ms = new(dllBytes, writable: false);
            Assembly asm = alc.LoadFromStream(ms);
            Type[] types = asm.GetExportedTypes();

            Type targetType = null;
            if (!string.IsNullOrWhiteSpace(entryTypeName)) {
                targetType = types.FirstOrDefault(t =>
                    string.Equals(t.FullName, entryTypeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Name, entryTypeName, StringComparison.OrdinalIgnoreCase));
            }
            targetType ??= types.FirstOrDefault(t => typeof(IHotProbe).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            targetType ??= types.FirstOrDefault(t => FindStaticEntryMethod(t) != null);

            if (targetType == null) {
                return (false, "", "", "No entry type found (implement IHotProbe or define public static RunAsync/Run).", alcRef);
            }

            if (typeof(IHotProbe).IsAssignableFrom(targetType) && !targetType.IsAbstract) {
                IHotProbe instance = (IHotProbe)Activator.CreateInstance(targetType);
                string res = await instance.ExecuteAsync(probeCtx).ConfigureAwait(false);
                return (true, $"{targetType.FullName}.ExecuteAsync", res ?? "", "", alcRef);
            }

            MethodInfo method = FindStaticEntryMethod(targetType);
            if (method == null) {
                return (false, targetType.FullName ?? "", "", "Target type lacks public static RunAsync/Run method.", alcRef);
            }

            ParameterInfo[] pars = method.GetParameters();
            object[] callArgs = pars.Length == 1 && pars[0].ParameterType == typeof(HotProbeContext)
                ? [probeCtx]
                : [];
            object ret = method.Invoke(null, callArgs);
            string text = await UnwrapReturnAsync(ret).ConfigureAwait(false);
            return (true, $"{targetType.FullName}.{method.Name}", text, "", alcRef);
        } catch (TargetInvocationException tie) {
            Exception inner = tie.InnerException ?? tie;
            return (false, entryTypeName, "", $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}", alcRef);
        } finally {
            alc.Unload();
        }
    }

    static MethodInfo FindStaticEntryMethod(Type t) {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (string name in new[] { "RunAsync", "Run", "ExecuteAsync", "Execute" }) {
            MethodInfo mWithCtx = t.GetMethod(name, flags, null, [typeof(HotProbeContext)], null);
            if (mWithCtx != null) {
                return mWithCtx;
            }
            MethodInfo mNoArg = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
            if (mNoArg != null) {
                return mNoArg;
            }
        }
        return null;
    }

    static async Task<string> UnwrapReturnAsync(object ret) {
        if (ret == null) {
            return "";
        }
        if (ret is Task<string> ts) {
            return (await ts.ConfigureAwait(false)) ?? "";
        }
        if (ret is Task task) {
            await task.ConfigureAwait(false);
            PropertyInfo resultProp = task.GetType().GetProperty("Result");
            object val = resultProp?.GetValue(task);
            return FormatOutput(val);
        }
        return FormatOutput(ret);
    }

    static string FormatOutput(object val) {
        if (val == null) {
            return "";
        }
        if (val is string s) {
            return s;
        }
        try {
            return JsonSerializer.Serialize(val, new JsonSerializerOptions { WriteIndented = true });
        } catch {
            return val.ToString() ?? "";
        }
    }

    static string ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";

    static long ReadInt64(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement p) && p.TryGetInt64(out long v) ? v : 0L;

    static int ReadInt32(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out JsonElement p) && p.TryGetInt32(out int v) ? v : fallback;
}
