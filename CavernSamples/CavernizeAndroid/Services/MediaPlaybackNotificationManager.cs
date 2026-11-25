using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;

using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Bitmap = Android.Graphics.Bitmap;
using DrawableIcon = Android.Graphics.Drawables.Icon;

namespace CavernizeAndroid.Services;

/// <summary>
/// Manages Android's system <see cref="MediaSession"/> and <see cref="Notification.MediaStyle"/>
/// playback controls so Cavernize appears in the One UI 9 Notification Panel, Quick Panel Media Card,
/// and Lock Screen Now Bar just like Spotify / Samsung Music.
/// </summary>
public sealed class MediaPlaybackNotificationManager : IDisposable {
    public const int NotificationId = 9021;
    const string ChannelId = "cavernize_media_playback";
    const string ActionPlayPause = "hu.sbence.cavern.cavernize.ACTION_PLAY_PAUSE";
    const string ActionSkipPrev = "hu.sbence.cavern.cavernize.ACTION_SKIP_PREV";
    const string ActionSkipNext = "hu.sbence.cavern.cavernize.ACTION_SKIP_NEXT";
    const string ActionRewind = "hu.sbence.cavern.cavernize.ACTION_REWIND_10";
    const string ActionForward = "hu.sbence.cavern.cavernize.ACTION_FWD_10";
    const string ActionStop = "hu.sbence.cavern.cavernize.ACTION_STOP";

    internal static Notification activeServiceNotification;
    internal static volatile bool isPlaybackActive;
    internal static Action onTaskRemovedCallback;

    readonly Context appContext;
    readonly NotificationManager notificationManager;
    readonly MediaSession mediaSession;
    readonly Handler mainHandler;
    readonly MediaActionReceiver actionReceiver;
    readonly Bitmap defaultArtworkBitmap;
    Bitmap customArtworkBitmap;
    readonly Bitmap iconPrev;
    readonly Bitmap iconNext;
    readonly Bitmap iconRewind;
    readonly Bitmap iconPlay;
    readonly Bitmap iconPause;
    readonly Bitmap iconForward;
    readonly Bitmap iconStop;

    bool disposed;
    bool receiverRegistered;
    bool isNotificationPosted;
    string currentTitle = "Cavernize 3D";
    string currentArtist = "Audio Spatializer";
    string currentAlbum = "Cavern Engine";
    long currentDurationMs;
    bool currentIsPlaying;

    public event Action PlayRequested;
    public event Action PauseRequested;
    public event Action PlayPauseRequested;
    public event Action StopRequested;
    public event Action SkipPreviousRequested;
    public event Action SkipNextRequested;
    public event Action Rewind10Requested;
    public event Action Forward10Requested;
    public event Action<long> SeekRequestedMs;

    public MediaPlaybackNotificationManager(Context context) {
        appContext = context.ApplicationContext ?? context;
        onTaskRemovedCallback = () => mainHandler.Post(() => StopRequested?.Invoke());
        mainHandler = new Handler(Looper.MainLooper);
        notificationManager = (NotificationManager)appContext.GetSystemService(Context.NotificationService);

        EnsureNotificationChannel();

        mediaSession = new MediaSession(appContext, "CavernizeSpatialSession");
#pragma warning disable CS0618, CA1422
        mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
#pragma warning restore CS0618, CA1422
        mediaSession.SetCallback(new SessionCallback(this), mainHandler);

        defaultArtworkBitmap = MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 256);
        iconPrev = CreateActionBitmap("prev");
        iconNext = CreateActionBitmap("next");
        iconRewind = CreateActionBitmap("rew");
        iconPlay = CreateActionBitmap("play");
        iconPause = CreateActionBitmap("pause");
        iconForward = CreateActionBitmap("fwd");
        iconStop = CreateActionBitmap("stop");

        actionReceiver = new MediaActionReceiver(this);
        IntentFilter filter = new();
        filter.AddAction(ActionPlayPause);
        filter.AddAction(ActionSkipPrev);
        filter.AddAction(ActionSkipNext);
        filter.AddAction(ActionRewind);
        filter.AddAction(ActionForward);
        filter.AddAction(ActionStop);

        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
                appContext.RegisterReceiver(actionReceiver, filter, ReceiverFlags.NotExported);
            } else {
                appContext.RegisterReceiver(actionReceiver, filter);
            }
            receiverRegistered = true;
        } catch (Exception ex) {
            AppLogger.Error("MediaPlaybackNotificationManager.RegisterReceiver", ex);
        }
    }

    void EnsureNotificationChannel() => EnsureNotificationChannel(notificationManager);

    internal static void EnsureNotificationChannel(NotificationManager manager) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || manager == null) {
            return;
        }
        NotificationChannel ch = new(ChannelId, "Reprodução de Áudio 3D", NotificationImportance.Low) {
            Description = "Controles de mídia do player espacial Cavernize 3D",
            LockscreenVisibility = NotificationVisibility.Public
        };
        ch.SetShowBadge(false);
        ch.EnableVibration(false);
        ch.SetSound(null, null);
        manager.CreateNotificationChannel(ch);
    }

    public void UpdateMetadata(string title, string artist, string album, double durationSeconds, Bitmap artwork = null) {
        if (disposed) {
            return;
        }
        currentTitle = string.IsNullOrWhiteSpace(title) ? "Cavernize 3D" : title;
        currentArtist = string.IsNullOrWhiteSpace(artist) ? "Cavernize Spatial Audio" : artist;
        currentAlbum = string.IsNullOrWhiteSpace(album) ? "3D Room Player" : album;
        currentDurationMs = Math.Max(0L, (long)Math.Round(durationSeconds * 1000.0));

        Bitmap previousCustom = customArtworkBitmap;
        customArtworkBitmap = CloneArtworkForNotification(artwork);

        Bitmap artToUse = IsBitmapUsable(customArtworkBitmap) ? customArtworkBitmap : defaultArtworkBitmap;

        MediaMetadata.Builder meta = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, currentTitle)
            .PutString(MediaMetadata.MetadataKeyArtist, currentArtist)
            .PutString(MediaMetadata.MetadataKeyAlbum, currentAlbum)
            .PutBitmap(MediaMetadata.MetadataKeyAlbumArt, artToUse);
        if (currentDurationMs > 0) {
            meta.PutLong(MediaMetadata.MetadataKeyDuration, currentDurationMs);
        }
        mediaSession.SetMetadata(meta.Build());

        if (isNotificationPosted) {
            PostOrUpdateNotification(currentIsPlaying, forceVisualRefresh: true);
        }
        if (previousCustom != null && !ReferenceEquals(previousCustom, customArtworkBitmap)) {
            try {
                previousCustom.Dispose();
            } catch {
                // Ignore
            }
        }
    }

    bool lastNotifiedIsPlaying;
    string lastNotifiedMetaKey = "";
    long lastNotifiedElapsedMs;

    static bool IsBitmapUsable(Bitmap source) {
        if (source == null) {
            return false;
        }
        try {
            return source.Handle != IntPtr.Zero && !source.IsRecycled;
        } catch {
            return false;
        }
    }

    static Bitmap CloneArtworkForNotification(Bitmap source) {
        if (!IsBitmapUsable(source)) {
            return null;
        }
        try {
            const int maxSize = 384;
            int w = source.Width;
            int h = source.Height;
            if (w <= maxSize && h <= maxSize) {
                return source.Copy(Bitmap.Config.Argb8888!, false);
            }
            float scale = maxSize / (float)Math.Max(w, h);
            return Bitmap.CreateScaledBitmap(source, Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)), true);
        } catch {
            return null;
        }
    }

    public void UpdatePlaybackState(bool isPlaying, double positionSeconds, bool showNotification) {
        if (disposed) {
            return;
        }
        currentIsPlaying = isPlaying;
        long posMs = Math.Max(0L, (long)Math.Round(positionSeconds * 1000.0));

        long actions =
            PlaybackState.ActionPlay |
            PlaybackState.ActionPause |
            PlaybackState.ActionPlayPause |
            PlaybackState.ActionSkipToPrevious |
            PlaybackState.ActionSkipToNext |
            PlaybackState.ActionSeekTo |
            PlaybackState.ActionRewind |
            PlaybackState.ActionFastForward |
            PlaybackState.ActionStop;

        PlaybackStateCode code = showNotification
            ? (isPlaying ? PlaybackStateCode.Playing : PlaybackStateCode.Paused)
            : PlaybackStateCode.Stopped;

        PlaybackState state = new PlaybackState.Builder()
            .SetActions(actions)
            .SetState(code, posMs, isPlaying ? 1.0f : 0f, SystemClock.ElapsedRealtime())
            .Build();

        mediaSession.SetPlaybackState(state);
        mediaSession.Active = showNotification;
        isPlaybackActive = showNotification && isPlaying;

        if (showNotification) {
            PostOrUpdateNotification(isPlaying, forceVisualRefresh: false);
        } else {
            CancelNotification();
        }
    }

    void PostOrUpdateNotification(bool isPlaying, bool forceVisualRefresh = false) {
        try {
            long nowMs = SystemClock.ElapsedRealtime();
            string metaKey = $"{currentTitle}|{currentArtist}|{currentAlbum}|{currentDurationMs}|{(customArtworkBitmap != null ? customArtworkBitmap.Handle.ToInt64() : 0L)}";
            bool stateChanged = !isNotificationPosted ||
                                lastNotifiedIsPlaying != isPlaying ||
                                !string.Equals(lastNotifiedMetaKey, metaKey, StringComparison.Ordinal);

            // MediaSession.SetPlaybackState drives live seekbar/position smoothly on Android without reposting
            // the foreground Notification. Reposting Notify(9021) at >5 Hz triggers Android NotificationManager shedding.
            if (!forceVisualRefresh && !stateChanged) {
                return;
            }
            if (!stateChanged && nowMs - lastNotifiedElapsedMs < 500L) {
                return;
            }

            bool wasPosted = isNotificationPosted;
            bool wasPlaying = lastNotifiedIsPlaying;
            Notification notif = BuildNotification(isPlaying);
            activeServiceNotification = notif;
            isNotificationPosted = true;
            isPlaybackActive = isPlaying;
            lastNotifiedIsPlaying = isPlaying;
            lastNotifiedMetaKey = metaKey;
            lastNotifiedElapsedMs = nowMs;

            try {
                if (isPlaying && (!wasPosted || !wasPlaying)) {
                    Intent svc = new(appContext, typeof(CavernizePlaybackService));
                    if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
                        appContext.StartForegroundService(svc);
                    } else {
                        appContext.StartService(svc);
                    }
                }
            } catch {
                // Fall back to direct NotificationManager notify if background start is restricted
            }

            notificationManager?.Notify(NotificationId, notif);
        } catch (Exception ex) {
            AppLogger.Error("MediaPlaybackNotificationManager.Post", ex);
        }
    }

    public void CancelNotification() {
        isNotificationPosted = false;
        isPlaybackActive = false;
        lastNotifiedIsPlaying = false;
        lastNotifiedMetaKey = "";
        activeServiceNotification = null;
        try {
            appContext.StopService(new Intent(appContext, typeof(CavernizePlaybackService)));
        } catch {
            // Ignore
        }
        try {
            notificationManager?.Cancel(NotificationId);
        } catch {
            // Ignore
        }
    }

    Notification BuildNotification(bool isPlaying) {
        Intent launchIntent = new(appContext, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntentFlags flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
        PendingIntent contentPi = PendingIntent.GetActivity(appContext, 0, launchIntent, flags);

        PendingIntent prevPi = PendingIntent.GetBroadcast(appContext, 5, new Intent(ActionSkipPrev).SetPackage(appContext.PackageName), flags);
        PendingIntent rewPi = PendingIntent.GetBroadcast(appContext, 1, new Intent(ActionRewind).SetPackage(appContext.PackageName), flags);
        PendingIntent playPausePi = PendingIntent.GetBroadcast(appContext, 2, new Intent(ActionPlayPause).SetPackage(appContext.PackageName), flags);
        PendingIntent fwdPi = PendingIntent.GetBroadcast(appContext, 3, new Intent(ActionForward).SetPackage(appContext.PackageName), flags);
        PendingIntent nextPi = PendingIntent.GetBroadcast(appContext, 6, new Intent(ActionSkipNext).SetPackage(appContext.PackageName), flags);
        PendingIntent stopPi = PendingIntent.GetBroadcast(appContext, 4, new Intent(ActionStop).SetPackage(appContext.PackageName), flags);

        Notification.MediaStyle style = new Notification.MediaStyle()
            .SetMediaSession(mediaSession.SessionToken)
            .SetShowActionsInCompactView(0, 2, 4);

        Notification.Builder builder = new(appContext, ChannelId);

        Bitmap artToUse = IsBitmapUsable(customArtworkBitmap) ? customArtworkBitmap : defaultArtworkBitmap;

        builder
            .SetSmallIcon(Resource.Drawable.cavern_logo)
            .SetLargeIcon(artToUse)
            .SetContentTitle(currentTitle)
            .SetContentText(currentArtist)
            .SetSubText("Cavernize 3D")
            .SetVisibility(NotificationVisibility.Public)
            .SetOngoing(isPlaying)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetContentIntent(contentPi)
            .SetDeleteIntent(stopPi)
            .AddAction(new Notification.Action.Builder(DrawableIcon.CreateWithBitmap(iconPrev), "Anterior", prevPi).Build())
            .AddAction(new Notification.Action.Builder(DrawableIcon.CreateWithBitmap(iconRewind), "-10s", rewPi).Build())
            .AddAction(new Notification.Action.Builder(DrawableIcon.CreateWithBitmap(isPlaying ? iconPause : iconPlay), isPlaying ? "Pausar" : "Tocar", playPausePi).Build())
            .AddAction(new Notification.Action.Builder(DrawableIcon.CreateWithBitmap(iconForward), "+10s", fwdPi).Build())
            .AddAction(new Notification.Action.Builder(DrawableIcon.CreateWithBitmap(iconNext), "Próxima", nextPi).Build())
            .SetStyle(style);

        return builder.Build();
    }

    internal static Notification BuildFallbackNotification(Context context) {
        NotificationManager nm = context.GetSystemService(Context.NotificationService) as NotificationManager;
        EnsureNotificationChannel(nm);

        Notification.Builder builder = new(context, ChannelId);

        return builder
            .SetSmallIcon(Resource.Drawable.cavern_logo)
            .SetContentTitle("Cavernize 3D")
            .SetContentText("Audio Spatializer")
            .SetVisibility(NotificationVisibility.Public)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .Build();
    }

    static Bitmap CreateActionBitmap(string kind) {
        const int sz = 64;
        Bitmap bmp = Bitmap.CreateBitmap(sz, sz, Bitmap.Config.Argb8888);
        using Canvas c = new(bmp);
        using Paint stroke = new(PaintFlags.AntiAlias) {
            Color = Color.White,
            StrokeWidth = 5f,
            StrokeCap = Paint.Cap.Round,
            StrokeJoin = Paint.Join.Round
        };
        stroke.SetStyle(Paint.Style.Stroke);

        using Paint fill = new(PaintFlags.AntiAlias) {
            Color = Color.White
        };
        fill.SetStyle(Paint.Style.Fill);

        switch (kind) {
            case "play":
                OneUiVectorIcons.DrawIcon(c, OneUiIconType.Play, sz * 0.5f, sz * 0.5f, sz * 0.72f, Color.White);
                break;
            case "pause":
                OneUiVectorIcons.DrawIcon(c, OneUiIconType.Pause, sz * 0.5f, sz * 0.5f, sz * 0.72f, Color.White);
                break;
            case "stop":
                OneUiVectorIcons.DrawIcon(c, OneUiIconType.Stop, sz * 0.5f, sz * 0.5f, sz * 0.65f, Color.White);
                break;
            case "prev": {
                c.DrawRoundRect(new RectF(13f, 16f, 19.5f, 48f), 2.5f, 2.5f, fill);
                using Android.Graphics.Path p = new();
                p.MoveTo(48f, 16f);
                p.LineTo(24f, 32f);
                p.LineTo(48f, 48f);
                p.Close();
                c.DrawPath(p, fill);
                break;
            }
            case "next": {
                using Android.Graphics.Path p = new();
                p.MoveTo(16f, 16f);
                p.LineTo(40f, 32f);
                p.LineTo(16f, 48f);
                p.Close();
                c.DrawPath(p, fill);
                c.DrawRoundRect(new RectF(44.5f, 16f, 51f, 48f), 2.5f, 2.5f, fill);
                break;
            }
            case "rew": {
                using Android.Graphics.Path p = new();
                p.MoveTo(30f, 16f);
                p.LineTo(12f, 32f);
                p.LineTo(30f, 48f);
                p.Close();
                c.DrawPath(p, fill);
                using Android.Graphics.Path p2 = new();
                p2.MoveTo(50f, 16f);
                p2.LineTo(32f, 32f);
                p2.LineTo(50f, 48f);
                p2.Close();
                c.DrawPath(p2, fill);
                break;
            }
            case "fwd": {
                using Android.Graphics.Path p = new();
                p.MoveTo(14f, 16f);
                p.LineTo(32f, 32f);
                p.LineTo(14f, 48f);
                p.Close();
                c.DrawPath(p, fill);
                using Android.Graphics.Path p2 = new();
                p2.MoveTo(34f, 16f);
                p2.LineTo(52f, 32f);
                p2.LineTo(34f, 48f);
                p2.Close();
                c.DrawPath(p2, fill);
                break;
            }
        }
        return bmp;
    }

    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        CancelNotification();
        if (receiverRegistered) {
            try {
                appContext.UnregisterReceiver(actionReceiver);
            } catch {
                // Ignore
            }
            receiverRegistered = false;
        }
        try {
            mediaSession.Active = false;
            mediaSession.Release();
            mediaSession.Dispose();
        } catch {
            // Ignore
        }
        customArtworkBitmap?.Dispose();
        customArtworkBitmap = null;
        defaultArtworkBitmap?.Dispose();
        iconPrev?.Dispose();
        iconNext?.Dispose();
        iconRewind?.Dispose();
        iconPlay?.Dispose();
        iconPause?.Dispose();
        iconForward?.Dispose();
        iconStop?.Dispose();
        onTaskRemovedCallback = null;
    }

    sealed class SessionCallback(MediaPlaybackNotificationManager mgr) : MediaSession.Callback {
        public override void OnPlay() => mgr.mainHandler.Post(() => mgr.PlayRequested?.Invoke());
        public override void OnPause() => mgr.mainHandler.Post(() => mgr.PauseRequested?.Invoke());
        public override void OnStop() => mgr.mainHandler.Post(() => mgr.StopRequested?.Invoke());
        public override void OnSkipToPrevious() => mgr.mainHandler.Post(() => mgr.SkipPreviousRequested?.Invoke());
        public override void OnSkipToNext() => mgr.mainHandler.Post(() => mgr.SkipNextRequested?.Invoke());
        public override void OnRewind() => mgr.mainHandler.Post(() => mgr.Rewind10Requested?.Invoke());
        public override void OnFastForward() => mgr.mainHandler.Post(() => mgr.Forward10Requested?.Invoke());
        public override void OnSeekTo(long pos) => mgr.mainHandler.Post(() => mgr.SeekRequestedMs?.Invoke(pos));
    }

    sealed class MediaActionReceiver(MediaPlaybackNotificationManager mgr) : BroadcastReceiver {
        public override void OnReceive(Context context, Intent intent) {
            switch (intent?.Action) {
                case ActionPlayPause:
                    mgr.mainHandler.Post(() => mgr.PlayPauseRequested?.Invoke());
                    break;
                case ActionSkipPrev:
                    mgr.mainHandler.Post(() => mgr.SkipPreviousRequested?.Invoke());
                    break;
                case ActionSkipNext:
                    mgr.mainHandler.Post(() => mgr.SkipNextRequested?.Invoke());
                    break;
                case ActionRewind:
                    mgr.mainHandler.Post(() => mgr.Rewind10Requested?.Invoke());
                    break;
                case ActionForward:
                    mgr.mainHandler.Post(() => mgr.Forward10Requested?.Invoke());
                    break;
                case ActionStop:
                    mgr.mainHandler.Post(() => mgr.StopRequested?.Invoke());
                    break;
            }
        }
    }
}

[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeMediaPlayback)]
public sealed class CavernizePlaybackService : Service {
    public override IBinder OnBind(Intent intent) => null;

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId) {
        Notification active = MediaPlaybackNotificationManager.activeServiceNotification;
        bool hasActivePlayback = intent != null && active != null && MediaPlaybackNotificationManager.isPlaybackActive;
        try {
            Notification notif = active ?? MediaPlaybackNotificationManager.BuildFallbackNotification(this);
            StartForegroundCompat(MediaPlaybackNotificationManager.NotificationId, notif);
            if (hasActivePlayback) {
                return StartCommandResult.Sticky;
            }
            // Sticky restart (intent == null) or stopped playback: satisfy FGS contract first, then tear down cleanly.
            StopForegroundCompat(removeNotification: true);
        } catch (Exception ex) {
            AppLogger.Warn($"CavernizePlaybackService.OnStartCommand: {ex.Message}");
        }
        StopSelf(startId);
        return StartCommandResult.NotSticky;
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
        AppLogger.Warn($"CavernizePlaybackService.OnTimeout(startId={startId}, fgsType={fgsType})");
        StopForegroundCompat(removeNotification: true);
        try {
            StopSelf(startId);
        } catch {
            StopSelf();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    Android.Content.PM.ForegroundService ResolveForegroundServiceType() {
        Android.Content.PM.ForegroundService fgsType = Android.Content.PM.ForegroundService.TypeMediaPlayback;
        if (OperatingSystem.IsAndroidVersionAtLeast(35) &&
            CheckSelfPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PROCESSING") == Android.Content.PM.Permission.Granted) {
            try {
#pragma warning disable CS0618, CA1422
                var info = PackageManager?.GetServiceInfo(
                    new ComponentName(this, Java.Lang.Class.FromType(typeof(CavernizePlaybackService))),
                    Android.Content.PM.PackageInfoFlags.MatchDefaultOnly);
#pragma warning restore CS0618, CA1422
                if (info != null && (info.ForegroundServiceType & Android.Content.PM.ForegroundService.TypeMediaProcessing) != 0) {
                    fgsType |= Android.Content.PM.ForegroundService.TypeMediaProcessing;
                }
            } catch {
                // Keep declared mediaPlayback type
            }
        }
        return fgsType;
    }

    void StartForegroundCompat(int id, Notification notification) {
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) {
            StartForeground(id, notification, ResolveForegroundServiceType());
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

    public override void OnTaskRemoved(Intent rootIntent) {
        MediaPlaybackNotificationManager.onTaskRemovedCallback?.Invoke();
        StopForegroundCompat(removeNotification: true);
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }
}
