using Android;
using Android.Animation;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.External;
using Cavernize.Logic.Language;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

using CavernizeAndroid.Audio;
using CavernizeAndroid.Language;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using VoidX.WPF.FFmpeg;

using Path = System.IO.Path;

namespace CavernizeAndroid;

[Activity(
    Label = "Cavernize",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.FontScale | ConfigChanges.LayoutDirection | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.Locale | ConfigChanges.ColorMode)]
[IntentFilter(
    [Intent.ActionView, Intent.ActionSend],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataMimeTypes = ["audio/*", "video/*", "application/octet-stream", "application/x-matroska"])]
public partial class MainActivity : Activity, ICavernizeApp {
    const int ReqOpenMedia = 1001;
    const int ReqBatchMedia = 1002;
    const int ReqLoadHrir = 1003;
    const int ReqLoadFilters = 1004;
    const int ReqPermissions = 1007;
    const int ReqLoadSubtitles = 1008;
    const int ReqLoadAutoEq = 1009;
    const int ReqBluetoothPermissions = 1010;
    const int ReqLoadOnnxModel = 1011;
    const int ReqRecordAudioPermission = 1012;

    AppSettingsStore store;
    AndroidUpmixingSettings upmixingSettings;
    AndroidRenderingSettings renderingSettings;
    AndroidFFmpegRunner ffmpeg;
    Cavernize.Logic.Models.Queue renderQueue;
    ConversionEnvironment environment;
    PostRenderReport report;
    RealtimePlayerEngine playerEngine;
    AndroidAudioOutput audioOutput;
    AudioDeviceProfileManager audioDeviceProfileManager;
    UsbProAudioDeviceManager usbProAudioManager;
    HeadTrackingManager headTrackingManager;
    NativeOnnxRuntimeBridge neuralRuntime;
    NeuralTrackCacheService neuralTrackCache;
    Cavernize.Logic.Spatial.NeuralPipelineOrchestrator neuralOrchestrator;

    AndroidStrings S => AndroidStrings.Active;

    // ICavernizeApp properties
    public bool Rendering => isOperationRunning;
    public AudioFile LoadedFile { get; private set; }
    public CavernizeTrack SelectedTrack {
        get => selectedTrack;
        set {
            selectedTrack = value;
            OnSelectedTrackChanged();
        }
    }
    public ExportFormat ExportFormat { get; set; }
    public RenderTarget RenderTarget { get; set; }
    public UpmixingSettings UpmixingSettings => upmixingSettings;
    public RenderingSettings RenderingSettings => renderingSettings;

    CavernizeTrack selectedTrack;
    IReadOnlyList<TrackSourceItem> fallbackTrackSourceItems = Array.Empty<TrackSourceItem>();
    bool useLiveEngineSources;
    IReadOnlyList<TrackSourceItem> trackSourceItems =>
        useLiveEngineSources && playerEngine != null ? playerEngine.SourceItems : fallbackTrackSourceItems;

    void SetLiveTrackSourceItems() {
        useLiveEngineSources = true;
        fallbackTrackSourceItems = Array.Empty<TrackSourceItem>();
    }

    void SetFallbackTrackSourceItems(IReadOnlyList<TrackSourceItem> items) {
        useLiveEngineSources = false;
        fallbackTrackSourceItems = items ?? Array.Empty<TrackSourceItem>();
    }
    volatile bool isOperationRunning;
    CancellationTokenSource activeCts;
    string lastRenderedFilePath;

    // Workspace Source Header Bars (shown in Sala 3D, Estúdio, Stems — removed from global top to avoid Play/Library redundancy)
    sealed class WorkspaceSourceBar {
        public LinearLayout Root;
        public LinearLayout FilePillBtn;
        public TextView FileText;
        public LinearLayout TrackBadge;
        public TextView TrackBadgeLabel;
        public OneUiIconView Chevron;
        public LinearLayout DetailsDrawer;
        public TextView SummaryText;
        public LinearLayout InlineTrackList;
    }

    readonly List<WorkspaceSourceBar> workspaceSourceBars = [];
    bool fileDetailsExpanded;
    string currentHeaderStatusText = "";
    string currentTrackSummaryText = "";
    double pendingSeekAfterLoadSeconds = -1;
    string preferredFormatVariantKey = "";
    AudioTierCategory? preferredAudioCategory;
    int variantSwitchGeneration;
    MediaPlaybackNotificationManager mediaNotification;
    MediaArtworkMetadata currentMediaMeta;
    Android.Net.Uri currentMediaUri;
    List<SubtitleTrackInfo> discoveredSubtitles = [];
    string activeSubtitleTrackId;
    IReadOnlyList<SubtitleCue> activeSubtitleCues = [];
    Bitmap defaultFallbackCover;

    UpdateCheckResult latestUpdateInfo;
    FFmpegStatusResult latestFFmpegStatus;
    TruehddStatusResult latestTruehddStatus;
    OnnxRuntimeStatusResult latestOnnxStatus;
    string downloadedUpdateApkPath;
    string pendingInstallApkPath;
    bool isUpdateDownloading;
    string pendingOnnxImportModelId = Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultStemModelId;
    Cavernize.Logic.Spatial.NeuralModelTask pendingCustomOnnxTask = Cavernize.Logic.Spatial.NeuralModelTask.StemSeparation4;
    bool pendingStandaloneCustomOnnxImport;
    Cavernize.Logic.Spatial.UniversalLosslessTwinProvider activeLosslessTwin;
    CancellationTokenSource losslessTwinCts;
    int losslessTwinGeneration;
    string lastLgsrSyncedPath = "";
    int lastLgsrSyncedConfigHash;
    CancellationTokenSource neuralPrepCts;
    int neuralPrepGeneration;
    volatile bool isNeuralPrepRunning;
    Cavernize.Logic.Spatial.NeuralPipelineGoal? activeNeuralPrepGoal;
    double neuralPrepProgress;
    string neuralPrepStatusText = "";
    int vocalDnaLoadGeneration;

    ExternalDecodedTrack decodedTrueHDTrack;
    string activeTrueHDSessionDir;
    string activeFallbackSessionDir;
    readonly HashSet<string> protectedQueuePaths = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource truehdPrepareCts;
    int truehdPrepareGeneration;
    volatile bool isPreparingTrueHD;
    volatile bool suppressAutoTrueHDPrepare;
    volatile bool pendingAutoPlayAfterLoad;

    FrameLayout rootFrameHost;
    LinearLayout rootLayoutHost;
    LinearLayout workspaceChromeHost;
    View bottomScrimView;
    OneUiBackdropView backdropView;
    OneUiFrostedCapsuleView navFrostedHost;
    FrameLayout navLayer;
    View settingsUpdateDotCollapsed;
    bool isBottomNavVisible = true;
    int navVisAnimGen;
    int tabSwitchAnimGen;
    int workspaceChromeAnimGen;
    int insetLeftPx;
    int insetTopPx;
    int insetRightPx;
    int insetBarsBottomPx;
    int insetImeBottomPx;
    Android.Window.IOnBackInvokedCallback predictiveBackCallback;
    bool isPredictiveBackRegistered;
    View tabIndicatorPill;
    LinearLayout[] tabPages;
    View[] tabContainers;
    LinearLayout[] tabCells;
    OneUiTabIconView[] tabIcons;
    TextView[] tabLabels;
    View settingsUpdateDot;
    int activeTab;
    Handler uiHandler;
    bool uiDestroyed;
    volatile bool isActivityResumed;
    volatile bool isRunningUiSelfAudit;
    SystemVolumeBroadcastReceiver systemVolumeReceiver;
    SystemVolumeContentObserver systemVolumeObserver;

    // Dynamic One UI 9 Color Tokens (automatically adapt to system Wallpaper Palette on API 31+)
    static Color ColBg => OneUiTheme.BgScreen;
    static Color ColCard => OneUiTheme.SurfaceCard;
    static Color ColCardAlt => OneUiTheme.SurfaceElevated;
    static Color ColAccent => OneUiTheme.Primary;
    static Color ColCyan => OneUiTheme.PrimaryHigh;
    static Color ColCoral => OneUiTheme.CoralStart;
    static Color ColText => OneUiTheme.TextPrimary;
    static Color ColMuted => OneUiTheme.TextMuted;
    static Color ColDanger => OneUiTheme.FunctionalRedFill;

    protected override void OnCreate(Bundle savedInstanceState) {
        base.OnCreate(savedInstanceState);

        OneUiTheme.Initialize(this);
        AppLogger.Initialize(this);
        AndroidRuntimeTelemetry.InstallGlobalComputeQoSHooks(ApplicationContext ?? this);
        _ = Task.Run(() => AudioQualityAnalyzer.EnsureCacheLoaded(ApplicationContext));

        ConfigureEdgeToEdgeWindow(Window);
        VolumeControlStream = Android.Media.Stream.Music;
        AndroidRuntimeTelemetry.AttachFrameMetrics(this);

        store = new AppSettingsStore(this);
        store.ApplyGlobals();
        LiveDiagnosticBridgeService.PrewarmPairSecretCacheAsync(store);
        OnnxRuntimeInstallerService.PrewarmStatusCacheAsync(this);
        AppLogger.LogAudioConfigSnapshot(store);
        upmixingSettings = new AndroidUpmixingSettings(store);
        renderingSettings = new AndroidRenderingSettings(store);
        ffmpeg = new AndroidFFmpegRunner(_ => { });
        string resolvedFfmpeg = FFmpegInstallerService.ResolveExistingFFmpegPath(this, store);
        if (!string.IsNullOrEmpty(resolvedFfmpeg)) {
            ffmpeg.Location = resolvedFfmpeg;
            AppLogger.Info($"FFmpeg resolved at '{resolvedFfmpeg}' (Found={ffmpeg.Found})");
        }

        string resolvedTruehdd = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        if (!string.IsNullOrEmpty(resolvedTruehdd)) {
            Truehdd.CustomExecutablePath = resolvedTruehdd;
            AppLogger.Info($"truehdd resolved at '{resolvedTruehdd}'");
        }
        Truehdd.TempWorkingDirectory = TrueHDDecoderService.GetWorkRoot(this);
        Truehdd.OnCommandCompleted = AppLogger.LogTruehdd;
        Truehdd.FallbackTrackExtractor = (srcTrack, destThd, prog, ct) => {
            if (!ffmpeg.Found) {
                return false;
            }
            string inputPath = LoadedFile?.Path ?? srcTrack.Path;
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath)) {
                return false;
            }
            ContainerProbeResult probe = TrueHDDecoderService.ProbeContainerWithFFmpeg(ffmpeg.Location, inputPath);
            ProbedAudioStream stream = probe.AudioStreams.FirstOrDefault(s => s.IsTrueHD && s.AudioOrdinal == srcTrack.Index)
                ?? probe.AudioStreams.FirstOrDefault(s => s.IsTrueHD);
            if (stream == null) {
                return false;
            }
            return TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                ffmpeg.Location, inputPath, stream.StreamIndex, destThd, "truehd", probe.DurationSeconds,
                (p, _) => prog?.Invoke(p), ct);
        };
        _ = Task.Run(() => {
            try { TrueHDDecoderService.CleanupOrphanedCache(this); } catch { }
        });

        RenderTarget[] allTargets = RenderTarget.Targets;
        RenderTarget = allTargets[Math.Clamp(store.RenderTargetIndex, 0, allTargets.Length - 1)];
        ExportFormat[] allFormats = ExportFormat.GetFormats();
        ExportFormat = allFormats[Math.Clamp(store.ExportFormatIndex, 0, allFormats.Length - 1)];

        environment = new ConversionEnvironment(this);
        report = new PostRenderReport(environment.Listener);
        renderQueue = new Cavernize.Logic.Models.Queue(this);

        playerEngine = new RealtimePlayerEngine {
            HeadphoneVirtualizer = store.HeadphoneVirtualizer,
            DistanceSimulation = store.DistanceSimulation,
            VolumeRolloff = (Rolloffs)Math.Clamp(store.VolumeRolloffIndex, 0, 3),
            Volume = 1.0f,
            LFEVolume = store.LfeVolume,
            EnvironmentType = (Environments)Math.Clamp(store.EnvironmentTypeIndex, 0, 3),
            EnvironmentSize = new System.Numerics.Vector3(store.RoomSizeX, store.RoomSizeY, store.RoomSizeZ),
            AudioQuality = (QualityModes)Math.Clamp(store.AudioQualityIndex, 0, 3),
            UsbDacNativeSampleRateEnabled = store.UsbDacNativeSampleRate,
            HeadTrackingPredictionMs = store.HeadTrackingPredictionMs,
            PropagationDelay6DoFEnabled = store.PropagationDelay6DoFEnabled
        };
        playerEngine.PlaybackCompleted += () => RunOnUiThread(OnPlayerCompleted);
        playerEngine.SourcesRebuilt += () => RunOnUiThread(() => {
            if (uiDestroyed || selectedTrack == null || (selectedTrack.Codec == Codec.TrueHD && decodedTrueHDTrack == null)) {
                return;
            }
            SetLiveTrackSourceItems();
            NotifyUiDomainChanged(
                UiStateDomain.Room3D | UiStateDomain.StudioExport | UiStateDomain.Neural | UiStateDomain.Playback,
                immediate: true);
        });
        audioOutput = new AndroidAudioOutput(playerEngine) {
            LowLatencyMode = store.LowLatencyAudio,
            UsbDacNativeSampleRate = store.UsbDacNativeSampleRate
        };
        audioOutput.DiagnosticsUpdated += _ => RunOnUiThread(RefreshHeadTrackingUi);
        audioOutput.ProAudioDiagnosticsUpdated += _ => RunOnUiThread(() => {
            RefreshProAudioUi();
            SyncUiFromSystemMediaVolume(updateEngine: true);
        });

        usbProAudioManager = new UsbProAudioDeviceManager(this);
        usbProAudioManager.DeviceStateChanged += _ => RunOnUiThread(RefreshProAudioUi);
        audioOutput.AttachUsbManager(usbProAudioManager);
        audioOutput.ApplyProAudioConfig(store.BuildProAudioConfig());

        mediaNotification?.Dispose();
        mediaNotification = new MediaPlaybackNotificationManager(this);
        mediaNotification.PlayRequested += () => {
            if (!playerEngine.IsPlaying) TogglePlayPause();
        };
        mediaNotification.PauseRequested += () => {
            if (playerEngine.IsPlaying) TogglePlayPause();
        };
        mediaNotification.PlayPauseRequested += TogglePlayPause;
        mediaNotification.StopRequested += StopPlayback;
        mediaNotification.SkipPreviousRequested += HandleMusicSkipPrevious;
        mediaNotification.SkipNextRequested += HandleMusicSkipNext;
        mediaNotification.Rewind10Requested += () => SeekRelative(-10);
        mediaNotification.Forward10Requested += () => SeekRelative(10);
        mediaNotification.SeekRequestedMs += ms => SeekAbsoluteSeconds(ms / 1000.0);

        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
        playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
        playerEngine.ApplyPersonalizationConfig(store.BuildPersonalizationConfig());
        playerEngine.ApplyLoudnessConfig(store.BuildLoudnessConfig());
        playerEngine.ApplyTransauralXtcConfig(store.BuildTransauralXtcConfig());
        ApplyArbitratedExternalizationAndBrirBypass();
        StemExtractor.TextResolver = key => AndroidStrings.Active[key];
        neuralRuntime = new NativeOnnxRuntimeBridge(this) {
            PreferredQuantizationMode = (Cavernize.Logic.Spatial.NeuralQuantizationMode)Math.Clamp(store.NeuralQuantizationModeIndex, 0, 3),
            QnnContextCacheEnabled = store.NeuralQnnContextCacheEnabled
        };
        neuralTrackCache = new NeuralTrackCacheService(this, (long)store.NeuralTrackCacheMaxMb * 1024L * 1024L) {
            IsEnabled = store.NeuralTrackCacheEnabled,
            CompressionMode = (Cavernize.Logic.Spatial.NeuralTrackCacheCompressionMode)store.NeuralTrackCacheCompressionIndex
        };
        long initialCacheLimitBytes = (long)store.NeuralTrackCacheMaxMb * 1024L * 1024L;
        NeuralTrackCacheService cacheRef = neuralTrackCache;
        _ = Task.Run(() => {
            try { cacheRef.EvictToLimit(initialCacheLimitBytes); } catch { }
        });
        neuralOrchestrator = new Cavernize.Logic.Spatial.NeuralPipelineOrchestrator(neuralRuntime, neuralRuntime, neuralTrackCache);
        playerEngine.ApplyNeuralConfig(store.BuildNeuralAudioConfig());
        playerEngine.ApplyLgsrConfig(store.BuildLgsrConfig());
        float[] savedVoiceprint = store.GetMicVoiceprintBands();
        if (savedVoiceprint != null) {
            playerEngine.VocalMixMatchStage.ApplyMicVoiceprint(savedVoiceprint, store.BuildVocalMacroSettings());
        } else {
            playerEngine.VocalMixMatchStage.ApplyVocalProfile(
                playerEngine.VocalMixMatchStage.ActiveDna,
                store.BuildVocalMacroSettings());
        }
        SyncNeuralRuntimeDiagnostics();

        StartOffUiSceneAndOnnxWarmupAsync();
        audioDeviceProfileManager = new AudioDeviceProfileManager(this, store, playerEngine);
        audioDeviceProfileManager.ActiveDeviceChanged += OnAudioDeviceRouteChanged;

        headTrackingManager = new HeadTrackingManager(this, store, playerEngine, audioOutput);
        headTrackingManager.StatusChanged += () => RunOnUiThread(RefreshHeadTrackingUi);

        uiHandler = new Handler(Looper.MainLooper);
        BuildRootUi();
        RegisterSystemVolumeListeners();
        SyncUiFromSystemMediaVolume(updateEngine: true);
        audioDeviceProfileManager.Start();
        usbProAudioManager.Start();
        audioOutput.UpdateOutputRoute(audioDeviceProfileManager.CurrentDevice);
        headTrackingManager.ApplySettings();
        NotifyUiDomainChanged(UiStateDomain.SpatialOutput | UiStateDomain.Neural | UiStateDomain.Settings, immediate: true);
        RequestMediaPermissionsIfNeeded();
        StartTelemetryTimer();

        if (Intent != null) {
            HandleIncomingIntent(Intent);
        }
        _ = CheckFFmpegUpdateAsync(silent: true);
        _ = CheckTruehddUpdateAsync(silent: true);
        _ = CheckOnnxRuntimeUpdateAsync(silent: true);
        if (store.AutoCheckUpdates) {
            _ = CheckForUpdatesAsync(silent: true);
        }
        AppLogger.CriticalEventLogged += OnCriticalLogEvent;
        CloudLogSyncService.TriggerStartupCrashSyncIfNeeded(this, store, _ => {
            if (!uiDestroyed) {
                RunOnUiThread(RefreshLogSummaryLabel);
            }
        });
        SchedulePostUpdateSelfCheckIfNeeded();
        LiveDiagnosticBridgeService.RegisterAppHost(this);
        LiveDiagnosticBridgeService.StateChanged += OnLiveBridgeStateChanged;
        LiveDiagnosticBridgeService.StartDirectAgentBridge(this, store);
        if (OmniBridgeIpcReceiver.IsOmniAgentInstalledWithMatchingSignature(this)) {
            LiveBridgeWakeService.Stop(this);
            OmniBridgeIpcReceiver.NotifyOmniAgentWake(this);
        } else {
            LiveBridgeWakeService.SyncStandbyState(this, store);
        }
    }

    void OnLiveBridgeStateChanged() {
        if (!uiDestroyed) {
            RunOnUiThread(RefreshLogSummaryLabel);
        }
    }

    void SchedulePostUpdateSelfCheckIfNeeded() {
        if (store == null || !store.AutoSelfCheckAfterUpdate) {
            return;
        }
        (_, long installedBuild) = AppUpdateService.GetInstalledVersion(this);
        if (installedBuild <= 0 || store.LastSelfCheckBuildCode == installedBuild) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await Task.Delay(4500).ConfigureAwait(false);
                if (uiDestroyed) {
                    return;
                }
                SelfCheckReport report = await SelfDiagnosticEngine.RunAsync(this, store, fullSuite: false).ConfigureAwait(false);
                store.LastSelfCheckBuildCode = installedBuild;
                store.LastSelfCheckUtc = report.ExecutedAtUtc;
                store.LastSelfCheckSummary = report.CompactSummary;
                if (CloudLogSyncService.IsConfigured(store)) {
                    _ = await CloudLogSyncService.UploadSelfCheckReportAsync(this, store, report, "post_update").ConfigureAwait(false);
                }
                if (!uiDestroyed) {
                    RunOnUiThread(RefreshLogSummaryLabel);
                }
            } catch {
                // Never disrupt startup
            }
        });
    }

    void OnCriticalLogEvent(string _) {
        if (uiDestroyed || store == null) {
            return;
        }
        CloudLogSyncService.ScheduleAutoErrorSyncIfEnabled(this, store, _ => {
            if (!uiDestroyed) {
                RunOnUiThread(RefreshLogSummaryLabel);
            }
        });
    }

    public override void OnTrimMemory(TrimMemory level) {
        base.OnTrimMemory(level);
        if ((int)level >= 10) {
            AppLogger.LogMemorySnapshot(this, $"OnTrimMemory({level})");
        }
    }

    protected override void OnResume() {
        base.OnResume();
        isActivityResumed = true;
        ResumeTelemetryTimer();
        LiveDiagnosticBridgeService.StartDirectAgentBridge(this, store);
        SyncUiFromSystemMediaVolume(updateEngine: true);
        if (!string.IsNullOrEmpty(pendingInstallApkPath) &&
            File.Exists(pendingInstallApkPath) &&
            AppUpdateService.CanInstallPackages(this)) {
            string path = pendingInstallApkPath;
            pendingInstallApkPath = null;
            AppUpdateService.LaunchInstaller(this, path);
        }
    }

    public override bool DispatchKeyEvent(KeyEvent e) {
        bool handled = base.DispatchKeyEvent(e);
        if (e != null && (e.KeyCode == Keycode.VolumeUp || e.KeyCode == Keycode.VolumeDown || e.KeyCode == Keycode.VolumeMute)) {
            uiHandler?.Post(() => SyncUiFromSystemMediaVolume(updateEngine: true));
        }
        return handled;
    }

    void RegisterSystemVolumeListeners() {
        try {
            if (systemVolumeReceiver == null) {
                systemVolumeReceiver = new SystemVolumeBroadcastReceiver(() => {
                    if (!uiDestroyed) {
                        RunOnUiThread(() => SyncUiFromSystemMediaVolume(updateEngine: true));
                    }
                });
                IntentFilter filter = new("android.media.VOLUME_CHANGED_ACTION");
                if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
                    RegisterReceiver(systemVolumeReceiver, filter, ReceiverFlags.Exported);
                } else {
                    RegisterReceiver(systemVolumeReceiver, filter);
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"RegisterSystemVolumeReceiver: {ex.Message}");
        }
        try {
            if (systemVolumeObserver == null && ContentResolver != null) {
                systemVolumeObserver = new SystemVolumeContentObserver(uiHandler ?? new Handler(Looper.MainLooper), () => {
                    if (!uiDestroyed) {
                        SyncUiFromSystemMediaVolume(updateEngine: true);
                    }
                });
                ContentResolver.RegisterContentObserver(Android.Provider.Settings.System.ContentUri!, true, systemVolumeObserver);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"RegisterSystemVolumeObserver: {ex.Message}");
        }
    }

    void UnregisterSystemVolumeListeners() {
        if (systemVolumeReceiver != null) {
            try { UnregisterReceiver(systemVolumeReceiver); } catch { }
            try { systemVolumeReceiver.Dispose(); } catch { }
            systemVolumeReceiver = null;
        }
        if (systemVolumeObserver != null) {
            try { ContentResolver?.UnregisterContentObserver(systemVolumeObserver); } catch { }
            try { systemVolumeObserver.Dispose(); } catch { }
            systemVolumeObserver = null;
        }
    }

    sealed class SystemVolumeBroadcastReceiver(Action onVolumeChanged) : BroadcastReceiver {
        public override void OnReceive(Context context, Intent intent) {
            if (intent == null) return;
            int streamType = intent.GetIntExtra("android.media.EXTRA_VOLUME_STREAM_TYPE", -1);
            if (streamType == -1 || streamType == (int)Android.Media.Stream.Music) {
                onVolumeChanged?.Invoke();
            }
        }
    }

    sealed class SystemVolumeContentObserver(Handler handler, Action onVolumeChanged) : Android.Database.ContentObserver(handler) {
        public override void OnChange(bool selfChange) {
            base.OnChange(selfChange);
            onVolumeChanged?.Invoke();
        }
    }

    protected override void OnPause() {
        isActivityResumed = false;
        PauseTelemetryTimer();
        base.OnPause();
    }

    protected override void OnNewIntent(Intent intent) {
        base.OnNewIntent(intent);
        HandleIncomingIntent(intent);
    }

    protected override void OnDestroy() {
        uiDestroyed = true;
        PauseTelemetryTimer();
        RecordActiveTrackMeasuredLoudnessIfValid();
        UnregisterSystemVolumeListeners();
        try { nextTrackLoudnessCts?.Cancel(); } catch { }
        try { nextTrackLoudnessCts?.Dispose(); } catch { }
        nextTrackLoudnessCts = null;
        AndroidRuntimeTelemetry.DetachFrameMetrics(this);
        AppLogger.CriticalEventLogged -= OnCriticalLogEvent;
        LiveDiagnosticBridgeService.StateChanged -= OnLiveBridgeStateChanged;
        LiveDiagnosticBridgeService.StopDirectAgentBridge();
        LiveDiagnosticBridgeService.UnregisterAppHost(this);
        if (store == null || !store.LiveBridgeStandbyEnabled) {
            _ = LiveDiagnosticBridgeService.StopSessionAsync();
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && isPredictiveBackRegistered && predictiveBackCallback != null) {
            try {
                OnBackInvokedDispatcher?.UnregisterOnBackInvokedCallback(predictiveBackCallback);
            } catch {
                // Ignore
            }
            isPredictiveBackRegistered = false;
        }
        StopCalibrationTonePlayback();
        CancelV42BackgroundTasks();
        activeCts?.Cancel();
        activeCts?.Dispose();
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        try { losslessTwinCts?.Cancel(); } catch { }
        losslessTwinCts = null;
        try { neuralPrepCts?.Cancel(); } catch { }
        neuralPrepCts = null;
        activeLosslessTwin?.Dispose();
        activeLosslessTwin = null;
        tabIndicatorPill?.Animate()?.Cancel();
        formatPillAnimator?.Cancel();
        formatPillAnimator?.Dispose();
        mediaNotification?.Dispose();
        ExitVideoFullscreenIfNeeded();
        musicVideoStage?.ReleasePlayer();
        currentMediaMeta?.ArtworkBitmap?.Dispose();
        defaultFallbackCover?.Dispose();
        headTrackingManager?.Dispose();
        audioDeviceProfileManager?.Dispose();
        audioOutput?.Dispose();
        usbProAudioManager?.Dispose();
        neuralRuntime?.Dispose();
        playerEngine?.CancelMicVoiceprintCalibration();
        playerEngine?.Dispose();
        uiCoordinator?.Dispose();
        decodedTrueHDTrack?.Dispose();
        LoadedFile?.Dispose();
        TrueHDDecoderService.CleanupWorkDirectory(activeTrueHDSessionDir);
        TrueHDDecoderService.CleanupWorkDirectory(activeFallbackSessionDir);
        OnnxRuntimeInstallerService.CleanupTempCache(this);
        base.OnDestroy();
    }

    void RunOnUiThreadSync(Action action) {
        if (Looper.MainLooper == Looper.MyLooper()) {
            action();
            return;
        }
        using ManualResetEventSlim done = new(false);
        Exception caught = null;
        RunOnUiThread(() => {
            try {
                action();
            } catch (Exception ex) {
                caught = ex;
            } finally {
                done.Set();
            }
        });
        done.Wait();
        if (caught != null) {
            throw caught;
        }
    }

    void BuildRootUi() {
        OneUiTheme.Initialize(this);
        InitializeUiCoordinator();

        FrameLayout rootFrame = new(this);
        rootFrameHost = rootFrame;
        rootFrame.SetBackgroundColor(ColBg);

        // Layer 0: Atmospheric blurred One UI 9 depth backdrop (also breathes with loaded album art palette)
        backdropView = new OneUiBackdropView(this);
        rootFrame.AddView(backdropView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Layer 1: Main content column (Edge-to-Edge WindowInsets applied dynamically)
        LinearLayout root = new(this) {
            Orientation = Orientation.Vertical
        };
        root.SetBackgroundColor(Color.Transparent);
        rootLayoutHost = root;

        workspaceSourceBars.Clear();
        currentHeaderStatusText = S["SelectFilePill"];
        currentTrackSummaryText = "";

        // W55.4.1: Removed global workspaceChromeHost (Source File bar now lives locally inside Export tab only).
        workspaceChromeHost = null;

        // Compact One UI 9 Top Shell Header (active tab title + top-right Settings Gear with OTA badge)
        root.AddView(BuildTopShellHeader(), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // 3-Tab content frame with horizontal swipe across Library | Sound | Export
        TabSwipeHostLayout contentFrame = new(
            this,
            () => activeTab,
            () => tabContainers?.Length ?? 0,
            idx => (tabContainers != null && idx >= 0 && idx < tabContainers.Length) ? tabContainers[idx] : null,
            SwitchTabInternal,
            () => !isNowPlayingSheetOpen);
        LinearLayout libraryTab = BuildMusicPlayerTab();
        LinearLayout stage3dTab = BuildPlayerTab();
        LinearLayout soundTab = BuildSoundHubTab();
        LinearLayout exportTab = BuildUnifiedExportTab();
        tabPages = [libraryTab, soundTab, exportTab];
        tabContainers = new View[tabPages.Length];

        for (int i = 0; i < tabPages.Length; i++) {
            if (i == 0) {
                // Play tab manages its own zero-scroll Now Playing viewport and internal scrollable Library
                tabPages[i].Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone;
                tabContainers[i] = tabPages[i];
                contentFrame.AddView(tabPages[i], new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            } else {
                ScrollView sv = new(this) {
                    FillViewport = true,
                    VerticalScrollBarEnabled = true
                };
                sv.SetClipToPadding(false);
                sv.SetBackgroundColor(Color.Transparent);
                sv.ScrollChange += (_, ev) => {
                    navFrostedHost?.ScheduleDeferredBlur(180);
                    UpdateTopShellHeaderScroll(ev.ScrollY);
                };
                sv.AddView(tabPages[i]);
                sv.Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone;
                tabContainers[i] = sv;
                contentFrame.AddView(sv, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            }
        }
        root.AddView(contentFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        rootFrame.AddView(root, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Bottom edge fade scrim so scrolled cards recede smoothly behind the frosted capsule
        View bottomScrim = new(this) {
            Background = new Android.Graphics.Drawables.GradientDrawable(
                Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom,
                [Color.Transparent.ToArgb(), Color.Argb(205, ColBg.R, ColBg.G, ColBg.B).ToArgb()])
        };
        bottomScrimView = bottomScrim;
        rootFrame.AddView(bottomScrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(98)) {
            Gravity = GravityFlags.Bottom
        });

        // Layer 2: Samsung Gallery Floating Capsule Bottom Navigation Bar (hidden on Play Now Playing so integrated '+' pill saves 100% vertical space)
        navFrostedHost = new OneUiFrostedCapsuleView(this) {
            CornerRadiusDp = 999f,
            GlazeTopColor = OneUiTheme.SurfaceFloatingBar,
            GlazeBottomColor = OneUiTheme.SurfaceFloatingBar,
            BehindViewsProvider = () => [backdropView, contentFrame],
            Elevation = Dp(14)
        };
        navFrostedHost.SetPadding(Dp(5), Dp(4), Dp(5), Dp(4));

        navLayer = new FrameLayout(this);
        tabIndicatorPill = new View(this) {
            Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SelectedTabPill, OneUiTheme.PillCornerDp)
        };
        navLayer.AddView(tabIndicatorPill, new FrameLayout.LayoutParams(Dp(58), Dp(48)) {
            Gravity = GravityFlags.CenterVertical | GravityFlags.Start
        });

        LinearLayout navRow = HRow();
        string[] tabTitles = [S["TabLibrary"], S["TabSound"], S["TabExport"]];
        OneUiTabKind[] tabKinds = [
            OneUiTabKind.Player,
            OneUiTabKind.Room3D,
            OneUiTabKind.Studio
        ];
        tabCells = new LinearLayout[3];
        tabIcons = new OneUiTabIconView[3];
        tabLabels = new TextView[3];
        for (int i = 0; i < 3; i++) {
            int idx = i;
            LinearLayout cell = new(this) { Orientation = Orientation.Vertical };
            cell.SetGravity(GravityFlags.Center);
            cell.SetMinimumHeight(Dp(48));
            cell.SetPadding(Dp(6), Dp(4), Dp(6), Dp(4));
            cell.Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp);
            OneUiTheme.AttachRecoil(cell, 0.94f);

            OneUiTabIconView iconView = new(this, tabKinds[i]);
            cell.AddView(iconView, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

            TextView labelTv = new(this) {
                Text = tabTitles[i],
                TextSize = 11f,
                Gravity = GravityFlags.Center
            };
            labelTv.SetIncludeFontPadding(false);
            labelTv.Typeface = OneUiTheme.GetAppleFontRegular(this);
            labelTv.SetSingleLine(true);
            labelTv.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            cell.AddView(labelTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(2)
            });

            cell.Click += (_, _) => SwitchTab(idx);
            tabCells[i] = cell;
            tabIcons[i] = iconView;
            tabLabels[i] = labelTv;
            navRow.AddView(cell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        navLayer.AddView(navRow, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        navFrostedHost.AddView(navLayer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Persistent Frosted Mini-Player docked cleanly above the 3-tab capsule
        FrameLayout.LayoutParams miniPlayerLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(14),
            RightMargin = Dp(14),
            BottomMargin = Dp(76)
        };
        rootFrame.AddView(BuildPersistentMiniPlayerBar(contentFrame), miniPlayerLp);

        FrameLayout.LayoutParams floatingNavLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(14),
            RightMargin = Dp(14),
            BottomMargin = Dp(12)
        };
        rootFrame.AddView(navFrostedHost, floatingNavLp);

        // Full-Screen Now Playing Sheet Overlay (Cover | Lyrics | 3D Stage) above all main chrome
        rootFrame.AddView(BuildNowPlayingSheetOverlay(stage3dTab), new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        SetContentView(rootFrame);
        ConfigureEdgeToEdgeWindow(Window);
        rootFrame.SetOnApplyWindowInsetsListener(new RootInsetsListener(ApplyWindowInsets));
        ApplyCurrentWindowInsetsPadding();
        rootFrame.RequestApplyInsets();
        UpdateTabButtonStyles(animate: false);
        UpdateTopShellHeaderTitle();
        SetBottomNavBarVisible(true, animate: false);
        RefreshMiniPlayerUi();
        SyncPredictiveBackRegistration();
        navFrostedHost.Post(() => navFrostedHost?.ScheduleDeferredBlur(220));
        RefreshAllUiState();
        SyncFilterStudioAndToolsFromStore();
    }

    sealed class RootInsetsListener(Action<WindowInsets> onApply) : Java.Lang.Object, View.IOnApplyWindowInsetsListener {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets) {
            onApply?.Invoke(insets);
            return OperatingSystem.IsAndroidVersionAtLeast(30) ? WindowInsets.Consumed : insets;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    sealed class InternalBackInvokedCallback(Action onBack) : Java.Lang.Object, Android.Window.IOnBackInvokedCallback {
        public void OnBackInvoked() => onBack?.Invoke();
    }

    void ConfigureEdgeToEdgeWindow(Window win) {
        if (win == null) {
            return;
        }
        win.DecorView?.SetBackgroundColor(ColBg);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35)) {
#pragma warning disable CS0618, CA1422
                win.SetDecorFitsSystemWindows(false);
#pragma warning restore CS0618, CA1422
            }
            if (win.Attributes != null) {
                win.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
                win.Attributes = win.Attributes;
            }
            IWindowInsetsController ctrl = win.InsetsController;
            if (ctrl != null) {
                ctrl.SetSystemBarsAppearance(
                    0,
                    (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars));
            }
        }
    }

    void ApplyWindowInsets(WindowInsets insets) {
        if (insets == null || uiDestroyed) {
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
            Android.Graphics.Insets bars = insets.GetInsets(
                WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            Android.Graphics.Insets ime = insets.GetInsets(WindowInsets.Type.Ime());
            insetLeftPx = bars.Left;
            insetTopPx = bars.Top;
            insetRightPx = bars.Right;
            insetBarsBottomPx = bars.Bottom;
            insetImeBottomPx = ime.Bottom;
        } else {
#pragma warning disable CS0618, CA1422
            insetLeftPx = insets.SystemWindowInsetLeft;
            insetTopPx = insets.SystemWindowInsetTop;
            insetRightPx = insets.SystemWindowInsetRight;
            insetBarsBottomPx = insets.SystemWindowInsetBottom;
            insetImeBottomPx = 0;
#pragma warning restore CS0618, CA1422
        }
        ApplyCurrentWindowInsetsPadding();
    }

    void ApplyCurrentWindowInsetsPadding() {
        if (uiDestroyed) {
            return;
        }
        rootLayoutHost?.SetPadding(insetLeftPx, insetTopPx, insetRightPx, 0);

        int scrollBottom = Math.Max(insetBarsBottomPx, insetImeBottomPx);
        bool imeOpen = insetImeBottomPx > insetBarsBottomPx + Dp(48);
        if (musicTabRootPage != null) {
            musicTabRootPage.SetPadding(Dp(16), Dp(2), Dp(16), 0);
        }
        if (nowPlayingSheetHost != null) {
            nowPlayingSheetHost.SetPadding(insetLeftPx, insetTopPx, insetRightPx, insetBarsBottomPx);
            if (isNowPlayingSheetOpen && activeNowPlayingSubView != 2) {
                UpdatePlayTabSpacingForViewport(force: true);
            }
        }
        if (musicLibraryScrollView != null) {
            musicLibraryScrollView.SetPadding(0, 0, 0, Dp(162) + scrollBottom);
        }
        if (workspaceChromeHost != null) {
            int cutoutL = 0;
            int cutoutR = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                var cutout = Window?.DecorView?.RootWindowInsets?.DisplayCutout;
                if (cutout != null) {
                    cutoutL = Math.Max(0, cutout.SafeInsetLeft - insetLeftPx);
                    cutoutR = Math.Max(0, cutout.SafeInsetRight - insetRightPx);
                }
            }
            workspaceChromeHost.SetPadding(Dp(14) + cutoutL, Dp(6), Dp(14) + cutoutR, Dp(4));
        }
        if (tabPages != null) {
            for (int i = 1; i < tabPages.Length; i++) {
                LinearLayout page = tabPages[i];
                if (page == null) {
                    continue;
                }
                int topDp = 4;
                page.SetPadding(Dp(14), Dp(topDp), Dp(14), Dp(166) + scrollBottom);
            }
        }
        if (bottomScrimView?.LayoutParameters is FrameLayout.LayoutParams scrimLp) {
            int targetH = Dp(98) + scrollBottom;
            if (scrimLp.Height != targetH) {
                scrimLp.Height = targetH;
                bottomScrimView.LayoutParameters = scrimLp;
            }
        }
        if (miniPlayerBarHost?.LayoutParameters is FrameLayout.LayoutParams mpLp) {
            int mpLeft = Dp(14) + insetLeftPx;
            int mpRight = Dp(14) + insetRightPx;
            int mpBottom = Dp(76) + scrollBottom;
            if (mpLp.LeftMargin != mpLeft || mpLp.RightMargin != mpRight || mpLp.BottomMargin != mpBottom) {
                mpLp.LeftMargin = mpLeft;
                mpLp.RightMargin = mpRight;
                mpLp.BottomMargin = mpBottom;
                miniPlayerBarHost.LayoutParameters = mpLp;
            }
        }
        if (miniPlayerBarHost != null) {
            bool showMini = isBottomNavVisible && !isNowPlayingSheetOpen && !imeOpen;
            ViewStates mpTarget = showMini ? ViewStates.Visible : ViewStates.Gone;
            if (miniPlayerBarHost.Visibility != mpTarget) {
                miniPlayerBarHost.Visibility = mpTarget;
                miniPlayerBarHost.Alpha = showMini ? 1f : 0f;
            }
        }
        if (navFrostedHost?.LayoutParameters is FrameLayout.LayoutParams navLp) {
            int targetLeft = Dp(14) + insetLeftPx;
            int targetRight = Dp(14) + insetRightPx;
            int targetBottom = Dp(12) + scrollBottom;
            if (navLp.LeftMargin != targetLeft || navLp.RightMargin != targetRight || navLp.BottomMargin != targetBottom) {
                navLp.LeftMargin = targetLeft;
                navLp.RightMargin = targetRight;
                navLp.BottomMargin = targetBottom;
                navFrostedHost.LayoutParameters = navLp;
                if (!imeOpen) {
                    navFrostedHost.Post(() => navFrostedHost?.ScheduleDeferredBlur(180));
                }
            }
        }
        if (navFrostedHost != null) {
            bool shouldShowNav = isBottomNavVisible && !imeOpen;
            ViewStates targetVis = shouldShowNav ? ViewStates.Visible : ViewStates.Gone;
            if (navFrostedHost.Visibility != targetVis) {
                navFrostedHost.Animate()?.Cancel();
                navFrostedHost.Visibility = targetVis;
                navFrostedHost.Alpha = shouldShowNav ? 1f : 0f;
                navFrostedHost.TranslationY = shouldShowNav ? 0f : Dp(34);
                navFrostedHost.ScaleX = shouldShowNav ? 1f : 0.94f;
                navFrostedHost.ScaleY = shouldShowNav ? 1f : 0.94f;
                if (bottomScrimView != null) {
                    bottomScrimView.Animate()?.Cancel();
                    bottomScrimView.Visibility = targetVis;
                    bottomScrimView.Alpha = shouldShowNav ? 1f : 0f;
                }
                if (shouldShowNav) {
                    navFrostedHost.Post(() => {
                        PositionTabIndicator(animate: false);
                        navFrostedHost?.ScheduleDeferredBlur(180);
                    });
                }
            }
        }
    }

    bool HasInternalBackStep() =>
        fullscreenVideoDlg != null ||
        isNowPlayingSheetOpen ||
        (activeTab == 1 && HasSettingsInternalBackStep()) ||
        (activeTab == 0 && (!string.IsNullOrEmpty(librarySearchQuery) || selectedLibraryAlbum != null || selectedLibraryFolder != null || !string.IsNullOrEmpty(selectedLibraryArtist))) ||
        activeTab != 0;

    bool TryHandleInternalBack() => HandleInternalBackStep();

    bool HandleInternalBackStep() {
        if (uiDestroyed) {
            return false;
        }
        if (fullscreenVideoDlg != null) {
            ExitVideoFullscreenIfNeeded();
            return true;
        }
        if (isNowPlayingSheetOpen) {
            if (activeNowPlayingSubView != 0) {
                SelectNowPlayingSubView(0);
            } else {
                CloseNowPlayingSheet();
            }
            return true;
        }
        if (activeTab == 1 && TryHandleSettingsBackStep()) {
            SyncPredictiveBackRegistration();
            return true;
        }
        if (activeTab == 0) {
            if (!string.IsNullOrEmpty(librarySearchQuery)) {
                librarySearchQuery = "";
                libraryRenderLimit = 72;
                if (librarySearchInput != null) {
                    librarySearchInput.Text = "";
                } else {
                    RenderLibraryView();
                }
                SyncPredictiveBackRegistration();
                return true;
            }
            if (selectedLibraryAlbum != null || selectedLibraryFolder != null || !string.IsNullOrEmpty(selectedLibraryArtist)) {
                selectedLibraryAlbum = null;
                selectedLibraryFolder = null;
                selectedLibraryArtist = null;
                RenderLibraryView();
                SyncPredictiveBackRegistration();
                return true;
            }
        }
        if (activeTab != 0) {
            SwitchTab(0);
            return true;
        }
        return false;
    }

    void SyncPredictiveBackRegistration() {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || uiDestroyed) {
            return;
        }
        bool shouldRegister = HasInternalBackStep();
        if (shouldRegister == isPredictiveBackRegistered) {
            return;
        }
        try {
            Android.Window.IOnBackInvokedDispatcher dispatcher = OnBackInvokedDispatcher;
            if (dispatcher == null) {
                return;
            }
            if (shouldRegister) {
                predictiveBackCallback ??= new InternalBackInvokedCallback(() => HandleInternalBackStep());
                dispatcher.RegisterOnBackInvokedCallback(0, predictiveBackCallback);
                isPredictiveBackRegistered = true;
            } else if (predictiveBackCallback != null) {
                dispatcher.UnregisterOnBackInvokedCallback(predictiveBackCallback);
                isPredictiveBackRegistered = false;
            }
        } catch (Exception ex) {
            AppLogger.Warn($"SyncPredictiveBackRegistration: {ex.Message}");
        }
    }

#pragma warning disable CS0618, CA1422
    public override void OnBackPressed() {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) && HandleInternalBackStep()) {
            return;
        }
        base.OnBackPressed();
    }
#pragma warning restore CS0618, CA1422

    LinearLayout CreateWorkspaceSourceBar() {
        LinearLayout barWrap = new(this) { Orientation = Orientation.Vertical };

        LinearLayout row = HRow();
        LinearLayout pillBtn = HRow();
        pillBtn.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
        pillBtn.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        OneUiTheme.AttachRecoil(pillBtn, 0.98f);

        OneUiIconView fileNoteIcon = new(this, OneUiIconType.MusicFile, OneUiTheme.IconSliderMedia);
        TextView fileTv = new(this) {
            Text = string.IsNullOrEmpty(currentHeaderStatusText) ? S["SelectFilePill"] : currentHeaderStatusText,
            TextSize = 12.5f
        };
        fileTv.SetIncludeFontPadding(false);
        fileTv.SetTextColor(OneUiTheme.TextSecondary);
        fileTv.Typeface = OneUiTheme.GetAppleFontMedium(this);
        fileTv.SetSingleLine(true);
        fileTv.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;

        LinearLayout badge = HRow();
        badge.Visibility = ViewStates.Gone;
        badge.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 195), OneUiTheme.PillCornerDp);
        badge.SetPadding(Dp(7), Dp(2), Dp(7), Dp(2));
        TextView badgeTv = Label("", 10.5f, ColText);
        badgeTv.SetIncludeFontPadding(false);
        badgeTv.Typeface = OneUiTheme.GetAppleFontBold(this);
        badge.AddView(badgeTv);

        OneUiIconView chev = new(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub) {
            Visibility = ViewStates.Gone
        };

        pillBtn.AddView(fileNoteIcon, new LinearLayout.LayoutParams(Dp(15), Dp(15)) { RightMargin = Dp(8) });
        pillBtn.AddView(fileTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        pillBtn.AddView(badge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        pillBtn.AddView(chev, new LinearLayout.LayoutParams(Dp(13), Dp(13)));
        pillBtn.Click += (_, _) => {
            if (LoadedFile == null) {
                OpenMediaWithExplorer();
            } else {
                fileDetailsExpanded = !fileDetailsExpanded;
                UpdateInlineTrackListVisibility();
            }
        };
        row.AddView(pillBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        LinearLayout openBtn = OneUiPillFactory.CreateIconActionPill(
            this, OneUiIconType.Folder, S["OpenShort"],
            () => OpenMediaWithExplorer(),
            primary: true);
        openBtn.SetPadding(Dp(12), Dp(9), Dp(14), Dp(9));
        row.AddView(openBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        barWrap.AddView(row);

        LinearLayout drawer = new(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        drawer.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 20f);
        drawer.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));

        TextView sumTv = Label(currentTrackSummaryText, 11.5f, OneUiTheme.TextSub);
        drawer.AddView(sumTv);

        LinearLayout trackList = new(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        trackList.SetPadding(0, Dp(8), 0, 0);
        drawer.AddView(trackList);

        barWrap.AddView(drawer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6) });

        WorkspaceSourceBar ws = new() {
            Root = barWrap,
            FilePillBtn = pillBtn,
            FileText = fileTv,
            TrackBadge = badge,
            TrackBadgeLabel = badgeTv,
            Chevron = chev,
            DetailsDrawer = drawer,
            SummaryText = sumTv,
            InlineTrackList = trackList
        };
        workspaceSourceBars.Add(ws);
        return barWrap;
    }

    void SetWorkspaceHeaderText(string text) {
        currentHeaderStatusText = text ?? "";
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.FileText != null) {
                ws.FileText.Text = currentHeaderStatusText;
            }
        }
    }

    void SetWorkspaceSummaryText(string text) {
        currentTrackSummaryText = text ?? "";
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.SummaryText != null) {
                ws.SummaryText.Text = currentTrackSummaryText;
            }
        }
    }

}
