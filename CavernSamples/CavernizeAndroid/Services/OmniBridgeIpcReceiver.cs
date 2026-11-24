using Android.App;
using Android.Content;
using Android.Content.PM;

using CavernizeAndroid.Audio;

namespace CavernizeAndroid.Services;

/// <summary>
/// Cryptographically verified local Broadcast IPC receiver allowing the same-signed
/// <c>OmniAgent Companion</c> (<c>hu.sbence.cavern.omniagent</c>) to dispatch Live Bridge commands
/// directly into Cavernize without running duplicate background cloud polling services.
///
/// Security invariants:
/// - Protected by Android <c>signature</c>-level permission <c>hu.sbence.cavern.permission.OMNI_BRIDGE</c>.
/// - Explicitly verifies <see cref="PackageManager.CheckSignatures(string, string)"/> == <c>0</c> (<c>SIGNATURE_MATCH</c>)
///   for <c>hu.sbence.cavern.omniagent</c> before executing any command.
/// - Respects user's <see cref="AppSettingsStore.OmniAgentIpcEnabled"/> toggle (on by default).
/// </summary>
[BroadcastReceiver(
    Enabled = true,
    Exported = true,
    Permission = OmniBridgePermission)]
[IntentFilter([ActionCommand])]
public sealed class OmniBridgeIpcReceiver : BroadcastReceiver {
    public const string OmniAgentPackage = "hu.sbence.cavern.omniagent";
    public const string OmniBridgePermission = "hu.sbence.cavern.permission.OMNI_BRIDGE";
    public const string ActionCommand = "hu.sbence.cavern.ipc.OMNI_BRIDGE_CMD";
    public const string ActionReply = "hu.sbence.cavern.ipc.OMNI_BRIDGE_REPLY";

    public const string ExtraReqId = "req_id";
    public const string ExtraCmd = "cmd";
    public const string ExtraArgsJson = "args_json";
    public const string ExtraMaxTier = "max_tier";
    public const string ExtraReplyPackage = "reply_package";
    public const string ExtraOk = "ok";
    public const string ExtraBody = "body";
    public const string ExtraElapsedMs = "elapsed_ms";
    public const string ExtraBridgeProtoVer = "X-Bridge-Proto-Ver";
    public const string BridgeProtoVersion = "2.2";

    const int ColdStartGateTimeoutMs = 4000;
    static readonly object coldStartGateLock = new();
    static Task coldStartGateTask;

    public const string ActionEnsureOmniAlive = "hu.sbence.cavern.ipc.OMNI_ENSURE_ALIVE";

    /// <summary>
    /// Returns true if <c>OmniAgent Companion</c> is installed and signed with the exact same
    /// release keystore certificate as Cavernize.
    /// </summary>
    public static bool IsOmniAgentInstalledWithMatchingSignature(Context context) {
        if (context?.PackageManager == null || string.IsNullOrEmpty(context.PackageName)) {
            return false;
        }
        try {
#pragma warning disable CS0618, CA1422
            // Android PackageManager.SIGNATURE_MATCH == 0
            return (int)context.PackageManager.CheckSignatures(context.PackageName, OmniAgentPackage) == 0;
#pragma warning restore CS0618, CA1422
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Silently sends a signature-protected wake ping to OmniAgent Companion so its always-on
    /// background service stays alive even if Android previously stopped the process.
    /// </summary>
    public static void NotifyOmniAgentWake(Context context) {
        if (context == null || !IsOmniAgentInstalledWithMatchingSignature(context)) {
            return;
        }
        try {
            Intent wake = new(ActionEnsureOmniAlive);
            wake.SetPackage(OmniAgentPackage);
            wake.AddFlags(ActivityFlags.IncludeStoppedPackages);
            context.SendBroadcast(wake, OmniBridgePermission);
        } catch {
            // Best effort
        }
    }

    public override void OnReceive(Context context, Intent intent) {
        if (context == null || intent == null || !string.Equals(intent.Action, ActionCommand, StringComparison.Ordinal)) {
            return;
        }

        string reqId = intent.GetStringExtra(ExtraReqId) ?? "";
        string cmd = intent.GetStringExtra(ExtraCmd) ?? "";
        string argsJson = intent.GetStringExtra(ExtraArgsJson) ?? "{}";
        int maxTier = Math.Clamp(intent.GetIntExtra(ExtraMaxTier, 2), 0, 2);
        string replyPkg = intent.GetStringExtra(ExtraReplyPackage) ?? OmniAgentPackage;
        int timeoutSec = Math.Clamp(intent.GetIntExtra("timeout_sec", 45), 5, 120);

        if (string.IsNullOrEmpty(reqId)) {
            return;
        }

        // Strict signature verification: automatically accepts commands whenever same-signed OmniAgent is installed
        if (!string.Equals(replyPkg, OmniAgentPackage, StringComparison.Ordinal) ||
            !IsOmniAgentInstalledWithMatchingSignature(context)) {
            AppLogger.Warn("IPC", $"Rejected OmniBridge IPC '{cmd}' from untrusted caller '{replyPkg}'.");
            return;
        }

        Context appCtx = context.ApplicationContext ?? context;
        AppSettingsStore store = new(appCtx);

        PendingResult pending = GoAsync();
        _ = Task.Run(async () => {
            try {
                // Bounded <=4s cold-start gate: eagerly initialize AppLogger + SIGPIPE shield before IPC commands
                // so cold-start log_tail is never empty and BroadcastReceiver never hits Android 10s ANR.
                Task gate = EnsureColdStartGateAsync(appCtx);
                if (!gate.IsCompleted) {
                    await Task.WhenAny(gate, Task.Delay(ColdStartGateTimeoutMs)).ConfigureAwait(false);
                }

                string normalizedCmd = (cmd ?? "").Trim().ToLowerInvariant();
                if (normalizedCmd == "get_omni_bootstrap") {
                    string relayUrl = (store.LiveRelayUrl ?? "").Trim();
                    string pairHex = LiveDiagnosticBridgeService.GetPairSecretHexForInternalAuth(store) ?? "";
                    string payload = System.Text.Json.JsonSerializer.Serialize(new {
                        ok = !string.IsNullOrEmpty(pairHex) || !string.IsNullOrEmpty(relayUrl),
                        relayUrl,
                        pairSecretHex = pairHex,
                        hasPairSecret = !string.IsNullOrEmpty(pairHex),
                        hasRelayUrl = !string.IsNullOrEmpty(relayUrl),
                        bridgeProtoVer = BridgeProtoVersion
                    });
                    AppLogger.Info("IPC", $"OmniAgent local IPC bootstrap served (hasRelay={!string.IsNullOrEmpty(relayUrl)}, hasPair={!string.IsNullOrEmpty(pairHex)}).");
                    SendReply(appCtx, replyPkg, reqId, ok: true, body: payload, elapsedMs: 1);
                    return;
                }
                if (normalizedCmd == "self_check") {
                    normalizedCmd = "quick_check";
                }
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSec));
                (bool ok, string body, long elapsedMs) = await LiveDiagnosticBridgeService.ExecuteLocalIpcCommandAsync(
                    appCtx, store, maxTier, normalizedCmd, argsJson, cts.Token).ConfigureAwait(false);
                SendReply(appCtx, replyPkg, reqId, ok, body, elapsedMs);
            } catch (OperationCanceledException) {
                SendReply(appCtx, replyPkg, reqId, ok: false, body: $"Cavernize IPC command '{cmd}' timed out after {timeoutSec}s.", elapsedMs: timeoutSec * 1000L);
            } catch (Exception ex) {
                SendReply(appCtx, replyPkg, reqId, ok: false, body: $"{ex.GetType().Name}: {ex.Message}", elapsedMs: 0);
            } finally {
                try {
                    pending?.Finish();
                } catch {
                    // Ignore
                }
            }
        });
    }

    static Task EnsureColdStartGateAsync(Context appCtx) {
        lock (coldStartGateLock) {
            if (coldStartGateTask == null || coldStartGateTask.IsFaulted || coldStartGateTask.IsCanceled) {
                coldStartGateTask = Task.Run(() => {
                    try {
                        AppLogger.Initialize(appCtx);
                    } catch {
                        // Best effort logger initialization
                    }
                    try {
                        NativeProAudioBridge.InstallSigpipeShield();
                    } catch {
                        // Best effort native shield
                    }
                });
            }
            return coldStartGateTask;
        }
    }

    static void SendReply(Context context, string replyPkg, string reqId, bool ok, string body, long elapsedMs) {
        try {
            Intent reply = new(ActionReply);
            reply.SetPackage(replyPkg);
            reply.PutExtra(ExtraReqId, reqId);
            reply.PutExtra(ExtraOk, ok);
            reply.PutExtra(ExtraBody, body ?? "");
            reply.PutExtra(ExtraElapsedMs, elapsedMs);
            reply.PutExtra(ExtraBridgeProtoVer, BridgeProtoVersion);
            context.SendBroadcast(reply, OmniBridgePermission);
        } catch (Exception ex) {
            AppLogger.Warn("IPC", $"Failed to send OmniBridge IPC reply for '{reqId}': {ex.Message}");
        }
    }
}
