using System.Runtime.Versioning;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;

using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using CavernizeAndroid.Audio;

using IOPath = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Android 14/15 <c>mediaProcessing</c> Foreground Service owning offline spatial export execution so long renders
/// survive <see cref="MainActivity.OnDestroy"/>, screen-off, and task swipe, with <c>StatFs</c> free-space preflight,
/// atomic <see cref="MediaStore"/> <c>IS_PENDING</c> commit/cleanup, and 5.5-hour timeout checkpointing.
/// </summary>
[Service(
    Name = "hu.sbence.cavern.cavernize.ExportMediaProcessingService",
    Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaProcessing)]
public sealed class ExportMediaProcessingService : Service {
    const int ExportNotificationId = 9062;
    const string ChannelId = "cavernize_media_export";
    const string ActionStartOrUpdate = "hu.sbence.cavern.cavernize.ACTION_EXPORT_MEDIA_PROCESSING";
    const string ActionCancel = "hu.sbence.cavern.cavernize.ACTION_CANCEL_EXPORT_MEDIA_PROCESSING";
    const long PreflightOverheadBytes = 512L * 1024L * 1024L;
    static readonly TimeSpan CheckpointTimeout = TimeSpan.FromHours(5.5);

    static readonly SemaphoreSlim ExportQueueGate = new(1, 1);
    static readonly object StateLock = new();
    static CancellationTokenSource activeExportCts;
    static volatile ExportMediaProcessingService activeServiceInstance;
    static volatile bool isExportActive;
    static string activeTitle = "Exporting Spatial Audio";
    static double activeProgress;
    static long lastNotifTicks;

    /// <summary>True while an offline spatial export job or batch is actively running.</summary>
    public static bool IsExportActive => isExportActive;

    /// <summary>Raised on export progress changes (<c>(progress 0..1, statusLabel)</c>).</summary>
    public static event Action<double, string> ExportProgress;

    /// <summary>
    /// Enqueues a single <see cref="StudioBatchExportJob"/> on the service-owned background worker
    /// so it survives Activity lifecycle teardown and screen-off.
    /// </summary>
    public static Task<StudioBatchExportResult> EnqueueSingleExportAsync(
        Context context,
        CavernizeTrack track,
        StudioBatchExportJob job,
        Action<double> onProgress = null,
        string title = null,
        CancellationToken callerToken = default) {
        if (job == null) {
            throw new ArgumentNullException(nameof(job));
        }
        Context appCtx = context?.ApplicationContext ?? context ?? Application.Context;
        string displayTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : (!string.IsNullOrWhiteSpace(job.TrackName) ? $"Exporting {job.TrackName}" : "Exporting Spatial Audio");

        return Task.Run(async () => {
            await ExportQueueGate.WaitAsync().ConfigureAwait(false);
            CancellationTokenSource linkedCts = null;
            try {
                CancellationToken serviceToken = BeginExportSession(appCtx, displayTitle);
                linkedCts = callerToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(serviceToken, callerToken)
                    : null;
                CancellationToken effectiveToken = linkedCts?.Token ?? serviceToken;

                PrepareJobEnvironment(appCtx, job);
                UpdateProgress(appCtx, 0.0, displayTitle, forceNotif: true, onProgress);

                StudioBatchExportResult result = StudioBatchExporter.ExportSingle(
                    track,
                    job,
                    p => UpdateProgress(appCtx, p, displayTitle, forceNotif: false, onProgress),
                    effectiveToken);

                UpdateProgress(appCtx, result.Succeeded ? 1.0 : activeProgress, displayTitle, forceNotif: true, onProgress);
                return result;
            } catch (System.OperationCanceledException) {
                return new StudioBatchExportResult {
                    Preset = job.Preset,
                    OutputPath = job.OutputPath,
                    CompressionTier = job.CompressionTier,
                    IsCancelled = true,
                    Succeeded = false,
                    ErrorMessage = "Export cancelled."
                };
            } finally {
                linkedCts?.Dispose();
                EndExportSession(appCtx);
                ExportQueueGate.Release();
            }
        });
    }

    /// <summary>
    /// Enqueues a sequential batch of <see cref="StudioBatchExportJob"/> items on the service-owned worker.
    /// </summary>
    public static Task<List<StudioBatchExportResult>> EnqueueBatchExportAsync(
        Context context,
        CavernizeTrack track,
        IReadOnlyList<StudioBatchExportJob> jobs,
        Action<double> onProgress = null,
        string title = null,
        CancellationToken callerToken = default) {
        if (jobs == null || jobs.Count == 0) {
            return Task.FromResult(new List<StudioBatchExportResult>());
        }
        Context appCtx = context?.ApplicationContext ?? context ?? Application.Context;
        string displayTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : $"Batch Export ({jobs.Count} formats)";

        return Task.Run(async () => {
            await ExportQueueGate.WaitAsync().ConfigureAwait(false);
            CancellationTokenSource linkedCts = null;
            try {
                CancellationToken serviceToken = BeginExportSession(appCtx, displayTitle);
                linkedCts = callerToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(serviceToken, callerToken)
                    : null;
                CancellationToken effectiveToken = linkedCts?.Token ?? serviceToken;

                for (int i = 0; i < jobs.Count; i++) {
                    if (jobs[i] != null) {
                        PrepareJobEnvironment(appCtx, jobs[i]);
                    }
                }
                UpdateProgress(appCtx, 0.0, displayTitle, forceNotif: true, onProgress);

                List<StudioBatchExportResult> results = StudioBatchExporter.ExportBatch(
                    track,
                    jobs,
                    (idx, total, p) => {
                        double overall = total > 0 ? Math.Clamp((idx + p) / total, 0.0, 1.0) : p;
                        string stepTitle = $"{displayTitle} ({idx + 1}/{total})";
                        UpdateProgress(appCtx, overall, stepTitle, forceNotif: false, onProgress);
                    },
                    effectiveToken);

                UpdateProgress(appCtx, 1.0, displayTitle, forceNotif: true, onProgress);
                return results;
            } catch (System.OperationCanceledException) {
                return [];
            } finally {
                linkedCts?.Dispose();
                EndExportSession(appCtx);
                ExportQueueGate.Release();
            }
        });
    }

    /// <summary>Cancels the currently active export job/batch cleanly.</summary>
    public static void CancelActiveExport() {
        lock (StateLock) {
            try {
                activeExportCts?.Cancel();
            } catch {
                // Ignore if already disposed
            }
        }
    }

    /// <summary>
    /// Verifies available storage in <paramref name="outputDir"/> via <see cref="StatFs"/> is at least
    /// <c>2.2 * estimatedBytes + 512 MB</c>.
    /// </summary>
    public static bool CheckFreeSpacePreflight(string outputDir, long estimatedBytes, out string errorMsg) {
        errorMsg = string.Empty;
        try {
            string probe = outputDir;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe)) {
                string parent = IOPath.GetDirectoryName(probe);
                if (string.Equals(parent, probe, StringComparison.Ordinal)) {
                    break;
                }
                probe = parent;
            }
            if (string.IsNullOrEmpty(probe) || !Directory.Exists(probe)) {
                errorMsg = "Export output directory is unavailable.";
                return false;
            }

            long clampedEst = Math.Max(0L, estimatedBytes);
            double scaled = clampedEst * 2.2;
            long requiredBytes = scaled >= long.MaxValue - PreflightOverheadBytes
                ? long.MaxValue
                : (long)Math.Ceiling(scaled) + PreflightOverheadBytes;

            using StatFs stat = new(probe);
            long availableBytes = stat.AvailableBytes;
            if (availableBytes < requiredBytes) {
                double availMb = availableBytes / (1024.0 * 1024.0);
                double reqMb = requiredBytes / (1024.0 * 1024.0);
                errorMsg = $"Insufficient storage: {availMb:0} MB free, {reqMb:0} MB required (2.2x + 512 MB safety margin).";
                return false;
            }
            return true;
        } catch (Exception ex) {
            AppLogger.Warn($"CheckFreeSpacePreflight fallback: {ex.Message}");
            errorMsg = $"Storage preflight check failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Atomically publishes a pending <see cref="MediaStore"/> item (<c>IS_PENDING = 0</c>) on success,
    /// or deletes both the pending MediaStore row and any staging temp file on cancel/ENOSPC/failure.
    /// </summary>
    public static Task<bool> CommitOrCleanupPendingMediaStoreAsync(
        Context context,
        Android.Net.Uri pendingUri,
        string tempFilePath,
        bool succeeded) => Task.Run(() => {
        ContentResolver resolver = context?.ContentResolver;
        if (!succeeded) {
            if (pendingUri != null && resolver != null) {
                try {
                    resolver.Delete(pendingUri, null, null);
                } catch {
                    // Best-effort MediaStore rollback
                }
            }
            if (!string.IsNullOrEmpty(tempFilePath)) {
                try {
                    if (File.Exists(tempFilePath)) {
                        File.Delete(tempFilePath);
                    }
                } catch {
                    // Best-effort temp cleanup
                }
            }
            return false;
        }

        if (pendingUri != null && resolver != null && OperatingSystem.IsAndroidVersionAtLeast(29)) {
            try {
                ContentValues values = new();
                values.Put(MediaStore.IMediaColumns.IsPending, 0);
                int rows = resolver.Update(pendingUri, values, null, null);
                return rows >= 0;
            } catch (Exception ex) {
                AppLogger.Warn($"CommitOrCleanupPendingMediaStoreAsync commit failed: {ex.Message}");
                return false;
            }
        }
        return string.IsNullOrEmpty(tempFilePath) || File.Exists(tempFilePath);
    });

    static CancellationToken BeginExportSession(Context appCtx, string title) {
        CancellationTokenSource cts;
        lock (StateLock) {
            activeExportCts?.Dispose();
            activeExportCts = new CancellationTokenSource(CheckpointTimeout);
            cts = activeExportCts;
            activeTitle = title;
            activeProgress = 0.0;
            isExportActive = true;
            lastNotifTicks = 0;
        }
        TryStartForegroundServiceSafe(appCtx);
        return cts.Token;
    }

    static void EndExportSession(Context appCtx) {
        lock (StateLock) {
            isExportActive = false;
            activeProgress = 0.0;
            try {
                activeExportCts?.Dispose();
            } catch {
                // Ignore
            }
            activeExportCts = null;
        }
        StopServiceGracefully(appCtx);
    }

    static void PrepareJobEnvironment(Context appCtx, StudioBatchExportJob job) {
        if (string.IsNullOrWhiteSpace(job.FfmpegExecutablePath)) {
            string resolved = FFmpegInstallerService.ResolveExistingFFmpegPath(appCtx, null);
            if (!string.IsNullOrEmpty(resolved)) {
                job.FfmpegExecutablePath = resolved;
            }
        }
        if (job.ComposableOptions != null &&
            string.IsNullOrWhiteSpace(job.ComposableOptions.FfmpegExecutablePath) &&
            !string.IsNullOrWhiteSpace(job.FfmpegExecutablePath)) {
            job.ComposableOptions.FfmpegExecutablePath = job.FfmpegExecutablePath;
        }
        job.OnSigpipeShield ??= () => NativeProAudioBridge.InstallSigpipeShield();
        if (job.ComposableOptions != null && job.ComposableOptions.OnSigpipeShield == null) {
            job.ComposableOptions.OnSigpipeShield = job.OnSigpipeShield;
        }
    }

    static void UpdateProgress(
        Context appCtx,
        double progress,
        string label,
        bool forceNotif,
        Action<double> localCallback) {
        double clamped = Math.Clamp(progress, 0.0, 1.0);
        activeProgress = clamped;
        activeTitle = label ?? activeTitle;
        try {
            localCallback?.Invoke(clamped);
        } catch {
            // Protect worker from caller callback exceptions
        }
        try {
            ExportProgress?.Invoke(clamped, activeTitle);
        } catch {
            // Protect worker from subscriber exceptions
        }

        long now = System.Environment.TickCount64;
        if (!forceNotif && now - Interlocked.Read(ref lastNotifTicks) < 250) {
            return;
        }
        Interlocked.Exchange(ref lastNotifTicks, now);

        ExportMediaProcessingService svc = activeServiceInstance;
        if (svc != null && isExportActive) {
            try {
                svc.PostProgressNotification(appCtx, activeTitle, clamped);
            } catch {
                // Ignore notification update errors
            }
        }
    }

    static void TryStartForegroundServiceSafe(Context appCtx) {
        if (appCtx == null) {
            return;
        }
        Intent intent = new(appCtx, typeof(ExportMediaProcessingService));
        intent.SetAction(ActionStartOrUpdate);
        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
                StartForegroundServiceApi31Safe(appCtx, intent);
            } else if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
                appCtx.StartForegroundService(intent);
            } else {
                appCtx.StartService(intent);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"ExportMediaProcessingService.Start failed (continuing background task): {ex.Message}");
        }
    }

    [SupportedOSPlatform("android31.0")]
    static void StartForegroundServiceApi31Safe(Context appCtx, Intent intent) {
        try {
            appCtx.StartForegroundService(intent);
        } catch (ForegroundServiceStartNotAllowedException ex) {
            AppLogger.Warn($"ExportMediaProcessingService FGS start restricted by OS (continuing in-process): {ex.Message}");
        }
    }

    static void StopServiceGracefully(Context appCtx) {
        ExportMediaProcessingService svc = activeServiceInstance;
        if (svc != null) {
            try {
                svc.StopForegroundCompat(removeNotification: true);
                svc.StopSelf();
                return;
            } catch {
                // Fall through to Context.StopService
            }
        }
        if (appCtx != null) {
            try {
                appCtx.StopService(new Intent(appCtx, typeof(ExportMediaProcessingService)));
            } catch {
                // Ignore
            }
        }
    }

    /// <inheritdoc/>
    public override IBinder OnBind(Intent intent) => null;

    /// <inheritdoc/>
    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId) {
        activeServiceInstance = this;
        if (string.Equals(intent?.Action, ActionCancel, StringComparison.Ordinal)) {
            CancelActiveExport();
            StopForegroundCompat(removeNotification: true);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        EnsureNotificationChannel();
        Notification notif = BuildProgressNotification(activeTitle, activeProgress);
        if (!TryEnterForeground(ExportNotificationId, notif)) {
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (!isExportActive) {
            StopForegroundCompat(removeNotification: true);
            StopSelf(startId);
        }
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc/>
    public override void OnTaskRemoved(Intent rootIntent) {
        if (isExportActive) {
            AppLogger.Info("ExportMediaProcessingService.OnTaskRemoved — draining active spatial export safely.");
            try {
                PostProgressNotification(this, activeTitle, activeProgress);
            } catch {
                // Ignore
            }
        } else {
            StopForegroundCompat(removeNotification: true);
            StopSelf();
        }
        base.OnTaskRemoved(rootIntent);
    }

    /// <inheritdoc/>
    [SupportedOSPlatform("android34.0")]
    public override void OnTimeout(int startId) {
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) {
            base.OnTimeout(startId);
        }
        HandleServiceTimeout(startId, Android.Content.PM.ForegroundService.TypeMediaProcessing);
    }

    /// <inheritdoc/>
    [SupportedOSPlatform("android35.0")]
    public override void OnTimeout(int startId, Android.Content.PM.ForegroundService fgsType) {
        if (OperatingSystem.IsAndroidVersionAtLeast(35)) {
            base.OnTimeout(startId, fgsType);
        }
        HandleServiceTimeout(startId, fgsType);
    }

    void HandleServiceTimeout(int startId, Android.Content.PM.ForegroundService fgsType) {
        AppLogger.Warn($"ExportMediaProcessingService.OnTimeout(startId={startId}, fgsType={fgsType}) — checkpointing & stopping export cleanly.");
        CancelActiveExport();
        StopForegroundCompat(removeNotification: true);
        try {
            StopSelf(startId);
        } catch {
            StopSelf();
        }
    }

    /// <inheritdoc/>
    public override void OnDestroy() {
        if (ReferenceEquals(activeServiceInstance, this)) {
            activeServiceInstance = null;
        }
        base.OnDestroy();
    }

    bool TryEnterForeground(int id, Notification notification) {
        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) {
                StartForeground(id, notification, Android.Content.PM.ForegroundService.TypeMediaProcessing);
            } else {
                StartForeground(id, notification);
            }
            return true;
        } catch (Exception ex) {
            if (OperatingSystem.IsAndroidVersionAtLeast(31) && ex is ForegroundServiceStartNotAllowedException) {
                AppLogger.Warn($"ExportMediaProcessingService.StartForeground blocked: {ex.Message}");
                return false;
            }
            AppLogger.Warn($"ExportMediaProcessingService.StartForeground failed: {ex.Message}");
            return false;
        }
    }

    void EnsureNotificationChannel() {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) {
            return;
        }
        NotificationManager nm = (NotificationManager)GetSystemService(NotificationService);
        if (nm == null) {
            return;
        }
        NotificationChannel ch = new(ChannelId, "Spatial Audio Export", NotificationImportance.Low) {
            Description = "Offline spatial mastering and encoding progress"
        };
        ch.SetShowBadge(false);
        nm.CreateNotificationChannel(ch);
    }

    void PostProgressNotification(Context ctx, string title, double progress) {
        NotificationManager nm = (NotificationManager)(ctx?.GetSystemService(NotificationService) ?? GetSystemService(NotificationService));
        if (nm == null) {
            return;
        }
        Notification notif = BuildProgressNotification(title, progress);
        nm.Notify(ExportNotificationId, notif);
    }

    Notification BuildProgressNotification(string title, double progress) {
        Intent openIntent = new(this, typeof(MainActivity));
        openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        PendingIntentFlags piFlags = OperatingSystem.IsAndroidVersionAtLeast(23)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
            : PendingIntentFlags.UpdateCurrent;
        PendingIntent openPi = PendingIntent.GetActivity(this, ExportNotificationId, openIntent, piFlags);

        Intent cancelIntent = new(this, typeof(ExportMediaProcessingService));
        cancelIntent.SetAction(ActionCancel);
        PendingIntent cancelPi = PendingIntent.GetService(this, ExportNotificationId + 1, cancelIntent, piFlags);

        int pct = (int)Math.Round(Math.Clamp(progress, 0.0, 1.0) * 100.0);
        Notification.Builder b = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        b.SetSmallIcon(Resource.Drawable.cavern_logo)
            .SetContentTitle(string.IsNullOrWhiteSpace(title) ? "Exporting Spatial Audio" : title)
            .SetContentText($"{pct}%")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetProgress(100, pct, pct == 0)
            .SetContentIntent(openPi);

        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            b.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) {
            Notification.Action cancelAct = new Notification.Action.Builder(
                Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.cavern_logo),
                "Cancel",
                cancelPi).Build();
            b.AddAction(cancelAct);
        }
        return b.Build();
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
