using System.Net.Http.Headers;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.OS;

using CavernizeAndroid.Language;

namespace CavernizeAndroid.Services;

/// <summary>
/// Lightweight opt-in Foreground Service (<c>dataSync</c>) that stays on standby in the background
/// even when Cavernize Android is closed — listening for cryptographically signed AI wake requests
/// on the paired Cloudflare Worker channel and presenting a native BiometricPrompt approval overlay.
/// </summary>
[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class LiveBridgeWakeService : Service {
    const int StandbyNotificationId = 9055;
    const int WakeAlertNotificationId = 9056;
    const string StandbyChannelId = "cavernize_live_bridge_standby";
    const string WakeAlertChannelId = "cavernize_live_bridge_wake_alert";
    const string ActionStopStandby = "hu.sbence.cavern.cavernize.ACTION_STOP_LIVE_BRIDGE_STANDBY";

    static readonly HttpClient http = BuildClient();
    static volatile bool isRunning;

    CancellationTokenSource loopCts;

    public static bool IsRunning => isRunning;

    static HttpClient BuildClient() {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CavernizeAndroid-WakeService", "2.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static void SyncStandbyState(Context context, AppSettingsStore store) {
        if (context == null || store == null) {
            return;
        }
        Context appCtx = context.ApplicationContext ?? context;
        bool shouldRun = store.LiveBridgeStandbyEnabled &&
                         LiveDiagnosticBridgeService.HasPairedSecret(store) &&
                         LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out _);
        if (shouldRun) {
            Start(appCtx);
        } else {
            Stop(appCtx);
        }
    }

    static void Start(Context context) {
        if (context == null) {
            return;
        }
        Context appCtx = context.ApplicationContext ?? context;
        Intent intent = new(appCtx, typeof(LiveBridgeWakeService));
        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
                appCtx.StartForegroundService(intent);
            } else {
                appCtx.StartService(intent);
            }
        } catch (Exception ex) {
            AppLogger.Warn("CLOUD", $"LiveBridgeWakeService.Start failed: {ex.Message}");
        }
    }

    static void Stop(Context context) {
        if (context == null) {
            return;
        }
        Context appCtx = context.ApplicationContext ?? context;
        try {
            appCtx.StopService(new Intent(appCtx, typeof(LiveBridgeWakeService)));
        } catch {
            // Ignore
        }
    }

    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId) {
        if (string.Equals(intent?.Action, ActionStopStandby, StringComparison.Ordinal)) {
            AppSettingsStore store = new(this);
            store.LiveBridgeStandbyEnabled = false;
            StopForegroundCompat(removeNotification: true);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        EnsureNotificationChannels();
        Notification standbyNotif = BuildStandbyNotification();
        StartForegroundCompat(StandbyNotificationId, standbyNotif);

        if (loopCts == null || loopCts.IsCancellationRequested) {
            loopCts?.Dispose();
            loopCts = new CancellationTokenSource();
            isRunning = true;
            LiveDiagnosticBridgeService.NotifyStateChanged();
            CancellationToken ct = loopCts.Token;
            _ = Task.Run(() => WakePollLoopAsync(ct));
        }
        return StartCommandResult.Sticky;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android34.0")]
    public override void OnTimeout(int startId) {
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) {
            base.OnTimeout(startId);
        }
        HandleServiceTimeout(startId, Android.Content.PM.ForegroundService.TypeShortService);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android35.0")]
    public override void OnTimeout(int startId, Android.Content.PM.ForegroundService fgsType) {
        if (OperatingSystem.IsAndroidVersionAtLeast(35)) {
            base.OnTimeout(startId, fgsType);
        }
        HandleServiceTimeout(startId, fgsType);
    }

    void HandleServiceTimeout(int startId, Android.Content.PM.ForegroundService fgsType) {
        AppLogger.Warn("CLOUD", $"LiveBridgeWakeService.OnTimeout(startId={startId}, fgsType={fgsType}) — stopping standby cleanly.");
        StopForegroundCompat(removeNotification: true);
        try {
            StopSelf(startId);
        } catch {
            StopSelf();
        }
    }

    public override void OnDestroy() {
        isRunning = false;
        try {
            loopCts?.Cancel();
            loopCts?.Dispose();
        } catch {
            // Ignore
        }
        loopCts = null;
        LiveDiagnosticBridgeService.NotifyStateChanged();
        base.OnDestroy();
    }

    async Task WakePollLoopAsync(CancellationToken ct) {
        int consecutiveIdle = 0;
        try {
            while (!ct.IsCancellationRequested) {
                AppSettingsStore store = new(this);
                if (!store.LiveBridgeStandbyEnabled) {
                    break;
                }
                string pairHex = LiveDiagnosticBridgeService.LoadPairSecretHex(store);
                if (string.IsNullOrEmpty(pairHex) || !LiveDiagnosticBridgeService.TryNormalizeRelayUrl(store.LiveRelayUrl, out string baseUrl)) {
                    await Task.Delay(15_000, ct).ConfigureAwait(false);
                    continue;
                }

                try {
                    string devHash = LiveDiagnosticBridgeService.ComputeWakeChannelHash(pairHex);
                    using HttpRequestMessage req = new(HttpMethod.Get, $"{baseUrl}/v1/wake/poll");
                    req.Headers.Add("X-Device-Hash", devHash);
                    using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) {
                        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using JsonDocument doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("hasWake", out JsonElement hasEl) &&
                            hasEl.GetBoolean() &&
                            doc.RootElement.TryGetProperty("wake", out JsonElement wakeEl)) {
                            consecutiveIdle = 0;
                            LiveBridgeWakeRequest wakeReq = new() {
                                WakeId = wakeEl.TryGetProperty("wakeId", out JsonElement idEl) ? (idEl.GetString() ?? "") : "",
                                TimestampUnix = wakeEl.TryGetProperty("timestampUnix", out JsonElement tsEl) && tsEl.TryGetInt64(out long ts) ? ts : 0,
                                RequestedTier = wakeEl.TryGetProperty("requestedTier", out JsonElement tEl) && tEl.TryGetInt32(out int t) ? Math.Clamp(t, 0, 2) : 1,
                                DurationSeconds = wakeEl.TryGetProperty("durationSeconds", out JsonElement dEl) && dEl.TryGetInt32(out int d) ? Math.Clamp(d, 300, 3600) : 1800,
                                Reason = wakeEl.TryGetProperty("reason", out JsonElement rEl) ? (rEl.GetString() ?? "") : "",
                                Requester = wakeEl.TryGetProperty("requester", out JsonElement rqEl) ? (rqEl.GetString() ?? "AI") : "AI",
                                Signature = wakeEl.TryGetProperty("signature", out JsonElement sEl) ? (sEl.GetString() ?? "") : ""
                            };

                            if (LiveDiagnosticBridgeService.VerifyWakeRequestSignature(pairHex, wakeReq, out string rejectReason)) {
                                LiveDiagnosticBridgeService.StorePendingWakeRequest(wakeReq);
                                TriggerBiometricPromptOverlay(wakeReq);
                            } else {
                                AppLogger.Warn("CLOUD", $"LiveBridgeWakeService rejected wake '{wakeReq.WakeId}': {rejectReason}");
                                _ = LiveDiagnosticBridgeService.DenyWakeRequestAsync(store, wakeReq.WakeId, rejectReason);
                            }
                        } else {
                            consecutiveIdle = Math.Min(consecutiveIdle + 1, 10);
                        }
                    }
                } catch (System.OperationCanceledException) {
                    break;
                } catch {
                    consecutiveIdle = Math.Min(consecutiveIdle + 1, 10);
                }

                // Adaptive backoff: 4s when recently active, 10s in quiet standby (minimal battery & KV quota)
                int delayMs = consecutiveIdle >= 4 ? 10_000 : 4_000;
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        } catch (System.OperationCanceledException) {
            // Normal shutdown
        } finally {
            isRunning = false;
            LiveDiagnosticBridgeService.NotifyStateChanged();
            StopForegroundCompat(removeNotification: true);
            StopSelf();
        }
    }

    void TriggerBiometricPromptOverlay(LiveBridgeWakeRequest wakeReq) {
        Intent authIntent = new(this, typeof(LiveBridgeAuthActivity));
        authIntent.PutExtra(LiveBridgeAuthActivity.ExtraWakeId, wakeReq.WakeId);
        authIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);

        PendingIntentFlags piFlags = OperatingSystem.IsAndroidVersionAtLeast(23)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
        PendingIntent fullScreenPi = PendingIntent.GetActivity(this, WakeAlertNotificationId, authIntent, piFlags);

        AndroidStrings S = AndroidStrings.Active;
        string title = S["LiveBridgeWakeNotifTitle"];
        string text = string.Format(S["LiveBridgeWakeNotifBodyFmt"], wakeReq.RequestedTier, wakeReq.DurationSeconds / 60, wakeReq.Reason);

        NotificationManager nm = (NotificationManager)GetSystemService(NotificationService);
        if (nm != null) {
            Notification.Builder b = new(this, WakeAlertChannelId);
            b.SetSmallIcon(Resource.Drawable.cavern_logo)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetCategory(Notification.CategoryCall)
                .SetAutoCancel(true)
                .SetContentIntent(fullScreenPi)
                .SetFullScreenIntent(fullScreenPi, true);
            nm.Notify(WakeAlertNotificationId, b.Build());
        }

        try {
            StartActivity(authIntent);
        } catch (Exception ex) {
            AppLogger.Info("CLOUD", $"Direct wake overlay deferred to high-priority notification ({ex.GetType().Name}).");
        }
    }

    void EnsureNotificationChannels() {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) {
            return;
        }
        NotificationManager nm = (NotificationManager)GetSystemService(NotificationService);
        if (nm == null) {
            return;
        }
        NotificationChannel standby = new(StandbyChannelId, "Live Bridge Standby", NotificationImportance.Min) {
            Description = "Lightweight background listener for paired AI fingerprint connection requests"
        };
        standby.SetShowBadge(false);
        nm.CreateNotificationChannel(standby);

        NotificationChannel alert = new(WakeAlertChannelId, "AI Connection Requests", NotificationImportance.High) {
            Description = "Biometric approval prompts when your paired AI requests a live connection",
            LockscreenVisibility = NotificationVisibility.Public
        };
        alert.EnableVibration(true);
        nm.CreateNotificationChannel(alert);
    }

    Notification BuildStandbyNotification() {
        AndroidStrings S = AndroidStrings.Active;
        Intent openIntent = new(this, typeof(MainActivity));
        openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        PendingIntentFlags piFlags = OperatingSystem.IsAndroidVersionAtLeast(23)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
        PendingIntent openPi = PendingIntent.GetActivity(this, StandbyNotificationId, openIntent, piFlags);

        Intent stopIntent = new(this, typeof(LiveBridgeWakeService));
        stopIntent.SetAction(ActionStopStandby);
        PendingIntent stopPi = PendingIntent.GetService(this, StandbyNotificationId + 1, stopIntent, piFlags);

        Notification.Builder b = new(this, StandbyChannelId);
        b.SetSmallIcon(Resource.Drawable.cavern_logo)
            .SetContentTitle(S["LiveBridgeStandbyNotifTitle"])
            .SetContentText(S["LiveBridgeStandbyNotifBody"])
            .SetOngoing(true)
            .SetContentIntent(openPi);
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) {
            Notification.Action stopAct = new Notification.Action.Builder(
                Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.cavern_logo),
                S["LiveBridgeStopStandbyBtn"],
                stopPi).Build();
            b.AddAction(stopAct);
        }
        return b.Build();
    }

    void StartForegroundCompat(int id, Notification notification) {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
            StartForeground(id, notification, Android.Content.PM.ForegroundService.TypeDataSync);
        } else {
            StartForeground(id, notification);
        }
    }

    void StopForegroundCompat(bool removeNotification) {
        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(24)) {
                StopForeground(removeNotification ? StopForegroundFlags.Remove : StopForegroundFlags.Detach);
            } else {
#pragma warning disable CS0618, CA1422
                StopForeground(removeNotification);
#pragma warning restore CS0618, CA1422
            }
        } catch {
            // Ignore
        }
    }
}
