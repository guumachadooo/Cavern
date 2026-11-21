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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
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
    List<TrackSourceItem> trackSourceItems = [];
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
    CancellationTokenSource neuralPrepCts;
    int neuralPrepGeneration;
    volatile bool isNeuralPrepRunning;
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
    View bottomScrimView;
    OneUiBackdropView backdropView;
    OneUiFrostedCapsuleView navFrostedHost;
    FrameLayout navLayer;
    View settingsUpdateDotCollapsed;
    bool isBottomNavVisible = true;
    int navVisAnimGen;
    int tabSwitchAnimGen;
    int insetLeftPx;
    int insetTopPx;
    int insetRightPx;
    int insetBarsBottomPx;
    int insetImeBottomPx;
    Android.Window.IOnBackInvokedCallback predictiveBackCallback;
    bool isPredictiveBackRegistered;
    View tabIndicatorPill;
    ValueAnimator tabPillAnimator;
    LinearLayout[] tabPages;
    View[] tabContainers;
    LinearLayout[] tabCells;
    OneUiTabIconView[] tabIcons;
    TextView[] tabLabels;
    View settingsUpdateDot;
    int activeTab;
    Handler uiHandler;
    bool uiDestroyed;

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
        AudioQualityAnalyzer.EnsureCacheLoaded(this);

        ConfigureEdgeToEdgeWindow(Window);

        store = new AppSettingsStore(this);
        store.ApplyGlobals();
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
        TrueHDDecoderService.CleanupOrphanedCache(this);

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
            Volume = store.PlayerVolume,
            LFEVolume = store.LfeVolume,
            NormalizerEnabled = store.NormalizerEnabled,
            LimiterOnly = store.LimiterOnly,
            EnvironmentType = (Environments)Math.Clamp(store.EnvironmentTypeIndex, 0, 3),
            EnvironmentSize = new System.Numerics.Vector3(store.RoomSizeX, store.RoomSizeY, store.RoomSizeZ),
            AudioQuality = (QualityModes)Math.Clamp(store.AudioQualityIndex, 0, 3),
            UsbDacNativeSampleRateEnabled = store.UsbDacNativeSampleRate,
            HeadTrackingPredictionMs = store.HeadTrackingPredictionMs,
            PropagationDelay6DoFEnabled = store.PropagationDelay6DoFEnabled
        };
        playerEngine.PlaybackCompleted += () => RunOnUiThread(OnPlayerCompleted);
        playerEngine.SourcesRebuilt += () => RunOnUiThread(() => {
            if (uiDestroyed) {
                return;
            }
            trackSourceItems = [.. playerEngine.SourceItems];
            RefreshPlayerSourcesList();
            RefreshExtractorChecklist();
            UpdateRoomCanvas();
            RefreshNeuralPrepUi();
            RefreshNeuralAudioUi();
        });
        audioOutput = new AndroidAudioOutput(playerEngine) {
            LowLatencyMode = store.LowLatencyAudio,
            UsbDacNativeSampleRate = store.UsbDacNativeSampleRate
        };
        audioOutput.DiagnosticsUpdated += _ => RunOnUiThread(RefreshHeadTrackingUi);
        audioOutput.ProAudioDiagnosticsUpdated += _ => RunOnUiThread(RefreshProAudioUi);

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

        BinauralProfileService.ApplySavedProfile(this, store);
        BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
        playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
        playerEngine.ApplyExternalizationConfig(store.BuildExternalizationConfig());
        playerEngine.ApplyPersonalizationConfig(store.BuildPersonalizationConfig());
        playerEngine.ApplyLoudnessConfig(store.BuildLoudnessConfig());
        StemExtractor.TextResolver = key => AndroidStrings.Active[key];
        neuralRuntime = new NativeOnnxRuntimeBridge(this);
        neuralTrackCache = new NeuralTrackCacheService(this, (long)store.NeuralTrackCacheMaxMb * 1024L * 1024L);
        neuralTrackCache.EvictToLimit((long)store.NeuralTrackCacheMaxMb * 1024L * 1024L);
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

        AutoEqService.EnsureLoaded(this);
        AutoEqService.ApplySavedToEngine(this, store, playerEngine);
        audioDeviceProfileManager = new AudioDeviceProfileManager(this, store, playerEngine);
        audioDeviceProfileManager.ActiveDeviceChanged += OnAudioDeviceRouteChanged;

        headTrackingManager = new HeadTrackingManager(this, store, playerEngine, audioOutput);
        headTrackingManager.StatusChanged += () => RunOnUiThread(RefreshHeadTrackingUi);

        uiHandler = new Handler(Looper.MainLooper);
        BuildRootUi();
        audioDeviceProfileManager.Start();
        usbProAudioManager.Start();
        audioOutput.UpdateOutputRoute(audioDeviceProfileManager.CurrentDevice);
        headTrackingManager.ApplySettings();
        RefreshHeadTrackingUi();
        RefreshProAudioUi();
        RefreshExternalizationUi();
        RefreshPersonalizationUi();
        RefreshLoudnessUi();
        RefreshNeuralAudioUi();
        RefreshLgsrUi();
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
    }

    protected override void OnResume() {
        base.OnResume();
        if (!string.IsNullOrEmpty(pendingInstallApkPath) &&
            File.Exists(pendingInstallApkPath) &&
            AppUpdateService.CanInstallPackages(this)) {
            string path = pendingInstallApkPath;
            pendingInstallApkPath = null;
            AppUpdateService.LaunchInstaller(this, path);
        }
    }

    protected override void OnNewIntent(Intent intent) {
        base.OnNewIntent(intent);
        HandleIncomingIntent(intent);
    }

    protected override void OnDestroy() {
        uiDestroyed = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && isPredictiveBackRegistered && predictiveBackCallback != null) {
            try {
                OnBackInvokedDispatcher?.UnregisterOnBackInvokedCallback(predictiveBackCallback);
            } catch {
                // Ignore
            }
            isPredictiveBackRegistered = false;
        }
        StopCalibrationTonePlayback();
        activeCts?.Cancel();
        activeCts?.Dispose();
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        losslessTwinCts?.Cancel();
        losslessTwinCts?.Dispose();
        neuralPrepCts?.Cancel();
        neuralPrepCts?.Dispose();
        activeLosslessTwin?.Dispose();
        activeLosslessTwin = null;
        tabPillAnimator?.Cancel();
        tabPillAnimator?.Dispose();
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

        // Tab content frame with horizontal swipe between non-Play tabs (can swipe into Play, never away from Play)
        TabSwipeHostLayout contentFrame = new(
            this,
            () => activeTab,
            () => tabContainers?.Length ?? 0,
            () => (tabContainers != null && activeTab >= 0 && activeTab < tabContainers.Length) ? tabContainers[activeTab] : null,
            SwitchTab);
        LinearLayout musicTab = BuildMusicPlayerTab();
        LinearLayout playerTab = BuildPlayerTab();
        LinearLayout convertTab = BuildConverterTab();
        LinearLayout extractTab = BuildExtractorTab();
        LinearLayout settingsTab = BuildSettingsTab();
        tabPages = [musicTab, playerTab, convertTab, extractTab, settingsTab];
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
                sv.ScrollChange += (_, _) => navFrostedHost?.RefreshBlur();
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
        string[] tabTitles = [S["TabMusicPlayer"], S["TabPlayer"], S["TabConvert"], S["TabExtract"], S["TabSettings"]];
        OneUiTabKind[] tabKinds = [
            OneUiTabKind.Player,
            OneUiTabKind.Room3D,
            OneUiTabKind.Studio,
            OneUiTabKind.Stems,
            OneUiTabKind.Settings
        ];
        tabCells = new LinearLayout[5];
        tabIcons = new OneUiTabIconView[5];
        tabLabels = new TextView[5];
        for (int i = 0; i < 5; i++) {
            int idx = i;
            LinearLayout cell = new(this) { Orientation = Orientation.Vertical };
            cell.SetGravity(GravityFlags.Center);
            cell.SetMinimumHeight(Dp(48));
            cell.SetPadding(Dp(2), Dp(4), Dp(2), Dp(4));
            cell.Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp);
            OneUiTheme.AttachRecoil(cell, 0.94f);

            OneUiTabIconView iconView = new(this, tabKinds[i]);
            cell.AddView(iconView, new LinearLayout.LayoutParams(Dp(22), Dp(22)));

            TextView labelTv = new(this) {
                Text = tabTitles[i],
                TextSize = 10.5f,
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

            if (i == 4) {
                FrameLayout tabCell = new(this);
                tabCell.AddView(cell, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(48)));

                settingsUpdateDot = new View(this);
                settingsUpdateDot.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.IconAtmosUpmix, 99f);
                settingsUpdateDot.Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
                FrameLayout.LayoutParams dotLp = new(Dp(7), Dp(7)) {
                    Gravity = GravityFlags.Top | GravityFlags.End,
                    TopMargin = Dp(4),
                    RightMargin = Dp(12)
                };
                tabCell.AddView(settingsUpdateDot, dotLp);
                navRow.AddView(tabCell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            } else {
                navRow.AddView(cell, new LinearLayout.LayoutParams(0, Dp(48), 1f));
            }
        }
        navLayer.AddView(navRow, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        navFrostedHost.AddView(navLayer, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        FrameLayout.LayoutParams floatingNavLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(14),
            RightMargin = Dp(14),
            BottomMargin = Dp(12)
        };
        rootFrame.AddView(navFrostedHost, floatingNavLp);

        SetContentView(rootFrame);
        ConfigureEdgeToEdgeWindow(Window);
        rootFrame.SetOnApplyWindowInsetsListener(new RootInsetsListener(ApplyWindowInsets));
        ApplyCurrentWindowInsetsPadding();
        rootFrame.RequestApplyInsets();
        UpdateTabButtonStyles(animate: false);
        SetBottomNavBarVisible(activeTab != 0 || isLibrarySectionOpen, animate: false);
        SyncPredictiveBackRegistration();
        navFrostedHost.Post(() => navFrostedHost?.RefreshBlur(force: true));
        RefreshAllUiState();
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
            win.SetDecorFitsSystemWindows(false);
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
        if (musicTabRootPage != null) {
            musicTabRootPage.SetPadding(
                Dp(20),
                Dp(6),
                Dp(20),
                isLibrarySectionOpen ? 0 : Dp(10) + insetBarsBottomPx);
        }
        if (musicLibraryScrollView != null) {
            musicLibraryScrollView.SetPadding(0, 0, 0, Dp(106) + scrollBottom);
        }
        if (tabPages != null) {
            for (int i = 1; i < tabPages.Length; i++) {
                LinearLayout page = tabPages[i];
                if (page == null) {
                    continue;
                }
                int topDp = i == 4 ? 2 : 6;
                page.SetPadding(Dp(14), Dp(topDp), Dp(14), Dp(116) + scrollBottom);
            }
        }
        if (bottomScrimView?.LayoutParameters is FrameLayout.LayoutParams scrimLp) {
            int targetH = Dp(98) + insetBarsBottomPx;
            if (scrimLp.Height != targetH) {
                scrimLp.Height = targetH;
                bottomScrimView.LayoutParameters = scrimLp;
            }
        }
        if (navFrostedHost?.LayoutParameters is FrameLayout.LayoutParams navLp) {
            int targetLeft = Dp(14) + insetLeftPx;
            int targetRight = Dp(14) + insetRightPx;
            int targetBottom = Dp(12) + insetBarsBottomPx;
            if (navLp.LeftMargin != targetLeft || navLp.RightMargin != targetRight || navLp.BottomMargin != targetBottom) {
                navLp.LeftMargin = targetLeft;
                navLp.RightMargin = targetRight;
                navLp.BottomMargin = targetBottom;
                navFrostedHost.LayoutParameters = navLp;
                navFrostedHost.Post(() => navFrostedHost?.RefreshBlur(force: true));
            }
        }
    }

    bool HasInternalBackStep() =>
        fullscreenVideoDlg != null ||
        (activeTab == 0 && !isLibrarySectionOpen && musicStageMode == PlayerStageMode.Lyrics) ||
        (activeTab == 0 && isLibrarySectionOpen) ||
        activeTab != 0;

    bool HandleInternalBackStep() {
        if (uiDestroyed) {
            return false;
        }
        if (fullscreenVideoDlg != null) {
            ExitVideoFullscreenIfNeeded();
            return true;
        }
        if (activeTab == 0 && !isLibrarySectionOpen && musicStageMode == PlayerStageMode.Lyrics) {
            SetPlayerStageMode(PlayerStageMode.Cover);
            return true;
        }
        if (activeTab == 0 && isLibrarySectionOpen) {
            if (!string.IsNullOrEmpty(librarySearchQuery)) {
                librarySearchQuery = "";
                libraryRenderLimit = 72;
                if (librarySearchInput != null) {
                    librarySearchInput.Text = "";
                } else {
                    RenderLibraryView();
                }
                return true;
            }
            if (selectedLibraryAlbum != null || selectedLibraryFolder != null || !string.IsNullOrEmpty(selectedLibraryArtist)) {
                selectedLibraryAlbum = null;
                selectedLibraryFolder = null;
                selectedLibraryArtist = null;
                RenderLibraryView();
                return true;
            }
            SetMusicPlayerSection(openLibrary: false);
            return true;
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

    void RebuildInlineTrackList() {
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.InlineTrackList == null || ws.TrackBadge == null) {
                continue;
            }
            ws.InlineTrackList.RemoveAllViews();
            if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
                ws.TrackBadge.Visibility = ViewStates.Gone;
                ws.InlineTrackList.Visibility = ViewStates.Gone;
                continue;
            }

            int selIdx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
            ws.TrackBadge.Visibility = ViewStates.Visible;
            ws.TrackBadgeLabel.Text = $"{selIdx + 1}/{LoadedFile.Tracks.Count}";

            TextView hdr = Label(S["SelectTrackHeader"], 11.5f, ColMuted);
            hdr.SetPadding(Dp(2), 0, 0, Dp(6));
            ws.InlineTrackList.AddView(hdr);

            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;

                LinearLayout row = HRow();
                row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 72), 14f)
                    : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
                OneUiTheme.AttachRecoil(row, 0.98f);

                View dot = new(this) {
                    Background = isSel
                        ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, 99f, ColText, 2.5f)
                        : OneUiTheme.CreateRoundedDrawable(this, Color.Transparent, 99f, ColMuted, 1.5f)
                };

                TextView nameTv = Label($"#{i + 1}: {tr}", 12.5f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.SetIncludeFontPadding(false);
                nameTv.SetTypeface(null, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);

                row.AddView(dot, new LinearLayout.LayoutParams(Dp(14), Dp(14)) { RightMargin = Dp(8) });
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    fileDetailsExpanded = false;
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    } else {
                        UpdateInlineTrackListVisibility();
                    }
                };
                ws.InlineTrackList.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(4)
                });
            }
        }
        UpdateInlineTrackListVisibility();
    }

    void UpdateInlineTrackListVisibility() {
        bool hasFile = LoadedFile != null;
        bool showDrawer = fileDetailsExpanded && hasFile;
        foreach (WorkspaceSourceBar ws in workspaceSourceBars) {
            if (ws?.DetailsDrawer == null || ws.Chevron == null) {
                continue;
            }
            ws.Chevron.Visibility = hasFile ? ViewStates.Visible : ViewStates.Gone;
            SetExpandedWithAnimation(ws.DetailsDrawer, showDrawer);
            if (ws.InlineTrackList != null) {
                ws.InlineTrackList.Visibility = (showDrawer && LoadedFile.Tracks.Count > 1) ? ViewStates.Visible : ViewStates.Gone;
            }
            ws.Chevron.IconType = showDrawer ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
        }
    }

    void OpenMediaWithExplorer(Action<string> onSelected = null, int? switchToTab = null, bool autoPlay = false) {
        string startDir = !string.IsNullOrEmpty(LoadedFile?.Path)
            ? Path.GetDirectoryName(LoadedFile.Path)
            : store.LastExplorerDirectory;
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = S["ExplorerTitle"],
            Mode = ExplorerPickMode.SingleFile,
            InitialDirectory = startDir,
            OnConfirm = paths => {
                if (paths == null || paths.Count == 0) {
                    return;
                }
                string path = paths[0];
                store.LastExplorerDirectory = Path.GetDirectoryName(path) ?? "";
                if (onSelected != null) {
                    onSelected(path);
                } else {
                    OpenLocalPathWithFallbackAsync(path, switchToTab, autoPlay);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false)
        });
    }

    void OpenMultiFilesWithExplorer(Action<IReadOnlyList<string>> onSelected) {
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = S["ExplorerTitleMultiFiles"],
            Mode = ExplorerPickMode.MultiFile,
            InitialDirectory = store.LastExplorerDirectory,
            OnConfirm = paths => {
                if (paths != null && paths.Count > 0) {
                    store.LastExplorerDirectory = Path.GetDirectoryName(paths[0]) ?? "";
                    onSelected?.Invoke(paths);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(ReqBatchMedia, allowMultiple: true)
        });
    }

    void OpenFileWithExplorerForExtensions(string title, IEnumerable<string> extensions, Action<string> onSelected, int fallbackRequestCode, bool allowAllFiles = false) {
        HashSet<string> allowed = extensions != null
            ? new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)
            : null;
        OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
            Title = string.IsNullOrWhiteSpace(title) ? S["ExplorerTitle"] : title,
            Mode = ExplorerPickMode.SingleFile,
            InitialDirectory = store.LastExplorerDirectory,
            AllowedExtensions = allowed,
            AllowAllFiles = allowAllFiles,
            OnConfirm = paths => {
                if (paths != null && paths.Count > 0) {
                    store.LastExplorerDirectory = Path.GetDirectoryName(paths[0]) ?? "";
                    onSelected?.Invoke(paths[0]);
                }
            },
            OnFallbackSystemPicker = () => LaunchFilePicker(fallbackRequestCode, allowMultiple: false)
        });
    }

    void ShowToast(string msg) {
        if (!string.IsNullOrEmpty(msg) && !uiDestroyed) {
            Toast.MakeText(this, msg, ToastLength.Short).Show();
        }
    }

    void ShowTechnicalStreamDialog() {
        if (LoadedFile == null || selectedTrack == null) {
            ShowToast(S["ActiveSourceEmpty"]);
            return;
        }
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        TextView pathTv = Label(LoadedFile.Path ?? "", 11f, OneUiTheme.TextMuted);
        pathTv.SetPadding(0, 0, 0, Dp(8));
        content.AddView(pathTv);

        string details = string.Join("\n", selectedTrack.Details.Select(d => $"• {d.property}: {d.value}"));
        TextView techTv = Label($"{selectedTrack.FormatHeader}\n\n{details}", 12f, OneUiTheme.TextSecondary);
        techTv.SetPadding(0, 0, 0, Dp(12));
        content.AddView(techTv);

        if (LoadedFile.Tracks.Count > 1) {
            TextView hdr = Label(S["SelectTrackHeader"], 12.5f, OneUiTheme.TextPrimary);
            hdr.Typeface = OneUiTheme.GetAppleFontBold(this);
            hdr.SetPadding(0, Dp(4), 0, Dp(8));
            content.AddView(hdr);

            Android.App.Dialog activeDlg = null;
            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;
                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 14f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                OneUiTheme.AttachRecoil(row, 0.98f);
                TextView nameTv = Label($"#{i + 1}: {tr}", 12.5f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.Typeface = isSel ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    activeDlg?.Dismiss();
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    }
                };
                content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            activeDlg = OneUiModalDialog.Show(
                this,
                S["PlayerTechInfo"],
                customContent: content,
                icon: OneUiIconType.Info,
                iconBadgeColor: OneUiTheme.Tertiary,
                positiveText: S["CloseBtn"]);
            return;
        }

        OneUiModalDialog.Show(
            this,
            S["PlayerTechInfo"],
            customContent: content,
            icon: OneUiIconType.Info,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: S["CloseBtn"]);
    }

    static Android.Views.Animations.PathInterpolator CreateOneUiEase() =>
        new(0.22f, 1f, 0.36f, 1f);

    void SetExpandedWithAnimation(View panel, bool expanded) {
        if (panel == null) {
            return;
        }
        bool isCurrentlyVisible = panel.Visibility == ViewStates.Visible;
        if (isCurrentlyVisible == expanded) {
            return;
        }
        panel.Animate()?.Cancel();
        if (panel.Parent is ViewGroup vg) {
            Android.Transitions.TransitionSet ts = new();
            ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
            ts.AddTransition(new Android.Transitions.ChangeBounds());
            ts.AddTransition(new Android.Transitions.Fade());
            ts.SetDuration(240);
            ts.SetInterpolator(CreateOneUiEase());
            Android.Transitions.TransitionManager.BeginDelayedTransition(vg, ts);
        }
        panel.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
        panel.Alpha = 1f;
        panel.TranslationY = 0f;
    }

    bool ShouldShowSettingsDot() =>
        latestUpdateInfo?.IsUpdateAvailable == true;

    void SwitchTab(int idx) {
        if (tabContainers == null || idx < 0 || idx >= tabContainers.Length) {
            return;
        }
        int prevTab = activeTab;
        activeTab = idx;
        if (idx != 0) {
            musicVideoStage?.PauseVideoDecoderOnly();
        } else if (musicStageMode == PlayerStageMode.Video && !isLibrarySectionOpen) {
            musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0);
        } else if (musicStageMode == PlayerStageMode.Lyrics && !isLibrarySectionOpen) {
            musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
        }

        int myGen = ++tabSwitchAnimGen;
        if (prevTab != idx) {
            int dir = idx > prevTab ? 1 : -1;
            for (int i = 0; i < tabContainers.Length; i++) {
                View container = tabContainers[i];
                if (container == null) {
                    continue;
                }
                container.Animate()?.Cancel();
                if (i == idx) {
                    container.Visibility = ViewStates.Visible;
                    container.Alpha = 0f;
                    container.ScaleX = 0.985f;
                    container.ScaleY = 0.985f;
                    container.TranslationX = dir * Dp(26);
                    container.Animate()
                        .Alpha(1f)
                        .ScaleX(1f)
                        .ScaleY(1f)
                        .TranslationX(0f)
                        .SetDuration(290)
                        .SetInterpolator(CreateOneUiEase())
                        .WithEndAction(new Java.Lang.Runnable(() => {
                            if (uiDestroyed || myGen != tabSwitchAnimGen) return;
                            navFrostedHost?.RefreshBlur(force: true);
                        }))
                        .Start();
                } else if (i == prevTab) {
                    View outgoing = container;
                    outgoing.Visibility = ViewStates.Visible;
                    outgoing.Animate()
                        .Alpha(0f)
                        .TranslationX(-dir * Dp(18))
                        .SetDuration(175)
                        .SetInterpolator(new Android.Views.Animations.AccelerateInterpolator(1.2f))
                        .WithEndAction(new Java.Lang.Runnable(() => {
                            if (uiDestroyed || myGen != tabSwitchAnimGen) return;
                            outgoing.Visibility = ViewStates.Gone;
                            outgoing.Alpha = 1f;
                            outgoing.ScaleX = 1f;
                            outgoing.ScaleY = 1f;
                            outgoing.TranslationX = 0f;
                        }))
                        .Start();
                } else {
                    container.Visibility = ViewStates.Gone;
                    container.Alpha = 1f;
                    container.ScaleX = 1f;
                    container.ScaleY = 1f;
                    container.TranslationX = 0f;
                }
            }
        } else if (tabContainers[idx] is View svCur) {
            svCur.Animate()?.Cancel();
            svCur.Visibility = ViewStates.Visible;
            svCur.Alpha = 1f;
            svCur.ScaleX = 1f;
            svCur.ScaleY = 1f;
            svCur.TranslationX = 0f;
        }
        UpdateTabButtonStyles(animate: true);
        bool wantsNav = idx != 0 || isLibrarySectionOpen;
        SetBottomNavBarVisible(wantsNav, animate: prevTab != idx || isBottomNavVisible != wantsNav);
        if (idx == 1) {
            UpdateRoomCanvas();
        } else if (idx == 0) {
            RefreshMusicPlayerUi();
        }
        navFrostedHost?.RefreshBlur(force: true);
        SyncPredictiveBackRegistration();
    }

    void SetBottomNavBarVisible(bool visible, bool animate) {
        if (navFrostedHost == null) {
            return;
        }
        isBottomNavVisible = visible;
        int myGen = ++navVisAnimGen;
        navFrostedHost.Animate()?.Cancel();
        bottomScrimView?.Animate()?.Cancel();

        if (!animate) {
            navFrostedHost.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
            navFrostedHost.Alpha = visible ? 1f : 0f;
            navFrostedHost.TranslationY = visible ? 0f : Dp(34);
            navFrostedHost.ScaleX = visible ? 1f : 0.94f;
            navFrostedHost.ScaleY = visible ? 1f : 0.94f;
            if (bottomScrimView != null) {
                bottomScrimView.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
                bottomScrimView.Alpha = visible ? 1f : 0f;
            }
            if (visible) {
                navFrostedHost.Post(() => {
                    PositionTabIndicator(animate: false);
                    navFrostedHost?.RefreshBlur(force: true);
                });
            }
            return;
        }

        if (visible) {
            if (navFrostedHost.Visibility != ViewStates.Visible) {
                navFrostedHost.Visibility = ViewStates.Visible;
                navFrostedHost.Alpha = 0f;
                navFrostedHost.TranslationY = Dp(32);
                navFrostedHost.ScaleX = 0.94f;
                navFrostedHost.ScaleY = 0.94f;
            }
            navFrostedHost.Animate()
                .Alpha(1f)
                .TranslationY(0f)
                .ScaleX(1f)
                .ScaleY(1f)
                .SetDuration(290)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != navVisAnimGen) return;
                    PositionTabIndicator(animate: false);
                    navFrostedHost?.RefreshBlur(force: true);
                }))
                .Start();
            if (bottomScrimView != null) {
                bottomScrimView.Visibility = ViewStates.Visible;
                bottomScrimView.Animate()
                    .Alpha(1f)
                    .SetDuration(250)
                    .SetInterpolator(CreateOneUiEase())
                    .Start();
            }
        } else {
            navFrostedHost.Animate()
                .Alpha(0f)
                .TranslationY(Dp(34))
                .ScaleX(0.94f)
                .ScaleY(0.94f)
                .SetDuration(220)
                .SetInterpolator(new Android.Views.Animations.AccelerateInterpolator(1.3f))
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myGen != navVisAnimGen) return;
                    navFrostedHost.Visibility = ViewStates.Gone;
                }))
                .Start();
            if (bottomScrimView != null) {
                bottomScrimView.Animate()
                    .Alpha(0f)
                    .SetDuration(200)
                    .WithEndAction(new Java.Lang.Runnable(() => {
                        if (uiDestroyed || myGen != navVisAnimGen) return;
                        bottomScrimView.Visibility = ViewStates.Gone;
                    }))
                    .Start();
            }
        }
    }

    void UpdateTabButtonStyles(bool animate = true) {
        if (tabCells == null || tabCells.Length == 0) {
            return;
        }
        for (int i = 0; i < tabCells.Length; i++) {
            bool active = i == activeTab;
            if (tabIcons != null && i < tabIcons.Length && tabIcons[i] != null) {
                tabIcons[i].IsTabSelected = active;
            }
            if (tabLabels != null && i < tabLabels.Length && tabLabels[i] != null) {
                tabLabels[i].SetTextColor(active ? ColText : Color.Argb(184, 210, 212, 220));
                tabLabels[i].Typeface = active ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontRegular(this);
            }
        }
        PositionTabIndicator(animate);
    }

    void PositionTabIndicator(bool animate) {
        if (tabIndicatorPill == null || tabCells == null || activeTab < 0 || activeTab >= tabCells.Length) {
            return;
        }
        View targetHost = activeTab == 4 && tabCells[4].Parent is View parentCell
            ? parentCell
            : tabCells[activeTab];
        int targetW = targetHost.Width;
        int targetH = targetHost.Height;
        if (targetW <= 0 || targetH <= 0) {
            targetHost.Post(() => PositionTabIndicator(animate: false));
            return;
        }

        float startX = tabIndicatorPill.TranslationX;
        float endX = targetHost.Left;
        int startW = tabIndicatorPill.Width > 0 ? tabIndicatorPill.Width : targetW;

        var lp = tabIndicatorPill.LayoutParameters;
        if (lp != null && lp.Height != targetH) {
            lp.Height = targetH;
            tabIndicatorPill.LayoutParameters = lp;
        }

        tabPillAnimator?.Cancel();
        if (!animate) {
            tabIndicatorPill.TranslationX = endX;
            if (lp != null) {
                lp.Width = targetW;
                tabIndicatorPill.LayoutParameters = lp;
            }
            return;
        }

        tabPillAnimator = ValueAnimator.OfFloat(0f, 1f);
        tabPillAnimator.SetDuration(270);
        tabPillAnimator.SetInterpolator(CreateOneUiEase());
        tabPillAnimator.Update += (_, e) => {
            float f = (float)(e.Animation?.AnimatedValue ?? 1f);
            tabIndicatorPill.TranslationX = startX + (endX - startX) * f;
            var curLp = tabIndicatorPill.LayoutParameters;
            if (curLp != null) {
                curLp.Width = (int)Math.Round(startW + (targetW - startW) * f);
                tabIndicatorPill.LayoutParameters = curLp;
            }
        };
        tabPillAnimator.Start();
    }

    void RequestMediaPermissionsIfNeeded() {
        List<string> needed = [];
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
            if (CheckSelfPermission(Manifest.Permission.ReadMediaAudio) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadMediaAudio);
            }
            bool hasVisualAccess = CheckSelfPermission(Manifest.Permission.ReadMediaVideo) == Permission.Granted ||
                (OperatingSystem.IsAndroidVersionAtLeast(34) &&
                 CheckSelfPermission(Manifest.Permission.ReadMediaVisualUserSelected) == Permission.Granted);
            if (!hasVisualAccess) {
                needed.Add(Manifest.Permission.ReadMediaVideo);
            }
            if (CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) {
                needed.Add(Manifest.Permission.PostNotifications);
            }
        } else {
            if (CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadExternalStorage);
            }
            if (!OperatingSystem.IsAndroidVersionAtLeast(30) &&
                CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.WriteExternalStorage);
            }
        }
        if (needed.Count > 0) {
            RequestPermissions(needed.ToArray(), ReqPermissions);
        }
    }

    void RequestBluetoothPermissionsIfNeeded(Cavernize.Logic.Spatial.HeadTrackerSourceKind source) {
        List<string> needed = [];
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            if (CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted) {
                needed.Add(Manifest.Permission.BluetoothConnect);
            }
            if (source == Cavernize.Logic.Spatial.HeadTrackerSourceKind.BleOsc &&
                CheckSelfPermission(Manifest.Permission.BluetoothScan) != Permission.Granted) {
                needed.Add(Manifest.Permission.BluetoothScan);
            }
        } else if (source == Cavernize.Logic.Spatial.HeadTrackerSourceKind.BleOsc &&
                   CheckSelfPermission(Manifest.Permission.AccessFineLocation) != Permission.Granted) {
            needed.Add(Manifest.Permission.AccessFineLocation);
        }
        if (needed.Count > 0) {
            RequestPermissions(needed.ToArray(), ReqBluetoothPermissions);
        }
    }

    bool RequestRecordAudioPermissionIfNeeded() {
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted) {
            return true;
        }
        RequestPermissions([Manifest.Permission.RecordAudio], ReqRecordAudioPermission);
        return false;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == ReqBluetoothPermissions) {
            bool allGranted = grantResults != null && grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted);
            if (!allGranted) {
                ShowToast(S["HeadTrackingPermNeededToast"]);
            }
            headTrackingManager?.ApplySettings();
            RefreshHeadTrackingUi();
        } else if (requestCode == ReqRecordAudioPermission) {
            bool granted = grantResults != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            if (!granted) {
                ShowToast(S["ProAudioMicPermNeededToast"]);
            }
            audioOutput?.ApplyProAudioConfig(store.BuildProAudioConfig());
            RefreshProAudioUi();
        }
    }

    void HandleIncomingIntent(Intent intent) {
        Android.Net.Uri uri = intent.Data;
        if (uri == null && intent.Extras != null && Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) {
            uri = intent.GetParcelableExtra(Intent.ExtraStream) as Android.Net.Uri;
        } else if (uri == null && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) {
            uri = intent.GetParcelableExtra(Intent.ExtraStream, Java.Lang.Class.FromType(typeof(Android.Net.Uri))) as Android.Net.Uri;
        }
        if (uri != null) {
            LoadUriAsync(uri);
        }
    }

    void LaunchFilePicker(int requestCode, bool allowMultiple) {
        Intent intent = new(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, allowMultiple);
        StartActivityForResult(intent, requestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data) {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data == null) {
            return;
        }

        if (requestCode == ReqOpenMedia && data.Data != null) {
            LoadUriAsync(data.Data);
        } else if (requestCode == ReqBatchMedia) {
            List<Android.Net.Uri> uris = [];
            if (data.ClipData != null) {
                for (int i = 0; i < data.ClipData.ItemCount; i++) {
                    uris.Add(data.ClipData.GetItemAt(i).Uri);
                }
            } else if (data.Data != null) {
                uris.Add(data.Data);
            }
            if (uris.Count > 0) {
                AddUrisToQueueAsync(uris);
            }
        } else if (requestCode == ReqLoadHrir && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomBinauralFile(path));
            });
        } else if (requestCode == ReqLoadFilters && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => TryLoadRoomFilters(path));
            });
        } else if (requestCode == ReqLoadSubtitles && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => LoadExternalSubtitleFromPath(path));
            });
        } else if (requestCode == ReqLoadAutoEq && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomAutoEqFile(path));
            });
        } else if (requestCode == ReqLoadOnnxModel && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => ImportCustomOnnxModelFromPath(path));
            });
        }
    }

    void LoadExternalSubtitleFromPath(string path) {
        if (string.IsNullOrEmpty(path)) return;
        Task.Run(() => {
            List<SubtitleCue> cues = SubtitleService.LoadFromFilePath(path);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                if (cues.Count == 0) {
                    ShowError(S["SubtitlesNoneFound"]);
                    return;
                }
                SubtitleTrackInfo imported = new() {
                    Id = "ext:" + path,
                    Title = Path.GetFileName(path),
                    ExternalFilePath = path
                };
                discoveredSubtitles.RemoveAll(s => string.Equals(s.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
                discoveredSubtitles.Insert(0, imported);
                activeSubtitleTrackId = imported.Id;
                activeSubtitleCues = cues;
                musicVideoStage?.SetSubtitles(activeSubtitleCues);
                RefreshBottomActionBarVisuals();
            });
        });
    }

    void LoadUriAsync(Android.Net.Uri uri) {
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        AppLogger.Info($"Loading URI: {uri}");
        currentMediaUri = uri;
        SetConvertStatus(S["CopyingMedia"], -1);
        SetWorkspaceHeaderText(S["CopyingMedia"]);
        Task.Run(() => {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(this, uri,
                    (p, name) => RunOnUiThread(() => {
                        string msg = $"{S["CopyingMedia"]} ({name}: {p:0%})";
                        SetWorkspaceHeaderText(msg);
                        SetConvertStatus(msg, p);
                    }));
                AppLogger.Info($"Resolved file path: '{resolved}'");

                MediaArtworkMetadata extractedMeta = MediaMetadataHelper.Extract(
                    this, uri, resolved, Path.GetFileName(resolved));

                AudioFile file = OpenAudioFileResilient(resolved, out string newFallbackSessionDir);

                RunOnUiThread(() => {
                    string queueSeed = !string.IsNullOrEmpty(extractedMeta?.SourceMediaPath) && File.Exists(extractedMeta.SourceMediaPath)
                        ? extractedMeta.SourceMediaPath
                        : resolved;
                    EnsureActiveQueueForCurrentFile(queueSeed);
                    string oldFallback = activeFallbackSessionDir;
                    activeFallbackSessionDir = newFallbackSessionDir;
                    OpenContent(file, extractedMeta);
                    if (!string.IsNullOrEmpty(oldFallback) &&
                        !string.Equals(oldFallback, newFallbackSessionDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                    TrueHDDecoderService.CleanupOrphanedCache(this, CollectActiveProtectedFiles(resolved));
                    SetConvertStatus(S["Ready"], 0);
                });
            } catch (Exception ex) {
                AppLogger.Error("LoadUriAsync", ex);
                RunOnUiThread(() => {
                    SetWorkspaceHeaderText(LoadedFile != null ? Path.GetFileName(LoadedFile.Path) : S["SelectFilePill"]);
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
                });
            }
        });
    }

    static bool HasVideoOnlyTrackViaMediaExtractor(string resolved) {
        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved)) {
            return false;
        }
        Android.Media.MediaExtractor extractor = null;
        try {
            extractor = new Android.Media.MediaExtractor();
            extractor.SetDataSource(resolved);
            bool hasVideo = false;
            bool hasAudio = false;
            for (int i = 0; i < extractor.TrackCount; i++) {
                string mime = extractor.GetTrackFormat(i)?.GetString(Android.Media.MediaFormat.KeyMime) ?? "";
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) {
                    hasAudio = true;
                } else if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) {
                    hasVideo = true;
                }
            }
            return hasVideo && !hasAudio;
        } catch {
            return false;
        } finally {
            try { extractor?.Release(); } catch { }
            try { extractor?.Dispose(); } catch { }
        }
    }

    AudioFile OpenAudioFileResilient(string resolved, out string fallbackSessionDir) {
        fallbackSessionDir = null;
        string ext = Path.GetExtension(resolved);
        if (ext.Equals(".atmos", StringComparison.OrdinalIgnoreCase)) {
            if (!File.Exists(resolved + ".audio")) {
                throw new InvalidDataException(S["DamfMissingAudioError"]);
            }
            // DAMF (.atmos + .atmos.audio) must never enter FFmpeg/MediaCodec video/audio fallbacks
            return new AudioFile(resolved);
        }

        try {
            AudioFile file = new(resolved);
            if (file.Tracks.Count > 0 && file.Tracks.Any(t => t.Supported)) {
                return file;
            }
            file.Dispose();
            throw new NotSupportedException("Fallback required for container/codec");
        } catch (Exception nativeEx) {
            AppLogger.Info($"Native Cavern reader fallback ({nativeEx.GetType().Name}): inspecting container/codec...");
            return TryOpenWithSmartFallbacks(resolved, out fallbackSessionDir);
        }
    }

    AudioFile TryOpenWithSmartFallbacks(string resolved, out string fallbackSessionDir) {
        fallbackSessionDir = null;
        string containerExt = Path.GetExtension(resolved).ToLowerInvariant();
        Container guessedContainer = containerExt switch {
            ".mkv" or ".mka" or ".webm" or ".weba" => Container.Matroska,
            ".mp4" or ".m4a" or ".m4v" or ".mov" or ".qt" => Container.MP4,
            ".mxf" => Container.MXF,
            _ => Container.NotContainer
        };

        ContainerProbeResult probe = null;
        if (ffmpeg.Found) {
            RunOnUiThread(() => {
                SetWorkspaceHeaderText(S["ProbingContainer"]);
                SetConvertStatus(S["ProbingContainer"], -1);
            });
            probe = TrueHDDecoderService.ProbeContainerWithFFmpeg(ffmpeg.Location, resolved);
            ProbedAudioStream spatialStream = probe.AudioStreams.FirstOrDefault(s => s.IsTrueHD)
                ?? probe.AudioStreams.FirstOrDefault(s => s.IsEnhancedAC3)
                ?? probe.AudioStreams.FirstOrDefault(s => s.IsAC3);

            if (spatialStream != null) {
                string workDir = Path.Combine(TrueHDDecoderService.GetWorkRoot(this), $"probe_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                fallbackSessionDir = workDir;

                if (spatialStream.IsTrueHD) {
                    RunOnUiThread(() => {
                        SetWorkspaceHeaderText(S["ExtractingTrueHD"]);
                        SetConvertStatus(S["ExtractingTrueHD"], -1);
                    });
                    string rawThd = Path.Combine(workDir, "extracted.thd");
                    bool ok = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                        ffmpeg.Location, resolved, spatialStream.StreamIndex, rawThd, "truehd",
                        probe.DurationSeconds,
                        (p, msg) => RunOnUiThread(() => {
                            SetWorkspaceHeaderText(msg);
                            SetConvertStatus(msg, p);
                        }));
                    if (ok) {
                        ExternalPendingTrack pending = new(
                            resolved, rawThd, Codec.TrueHD, guessedContainer,
                            spatialStream.AudioOrdinal, spatialStream.Language,
                            spatialStream.SampleRate, probe.DurationSeconds, spatialStream.ChannelSummary);
                        return AudioFile.FromCustomTracks(resolved, [pending]);
                    }
                } else {
                    string fmt = spatialStream.IsEnhancedAC3 ? "eac3" : "ac3";
                    string ext = "." + fmt;
                    string rawPath = Path.Combine(workDir, "extracted" + ext);
                    bool ok = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                        ffmpeg.Location, resolved, spatialStream.StreamIndex, rawPath, fmt,
                        probe.DurationSeconds,
                        (p, msg) => RunOnUiThread(() => {
                            SetWorkspaceHeaderText(msg);
                            SetConvertStatus(msg, p);
                        }));
                    if (ok) {
                        AudioReader extractedReader = AudioReader.Open(rawPath);
                        Codec c = spatialStream.IsEnhancedAC3 ? Codec.EnhancedAC3 : Codec.AC3;
                        ExternalDecodedTrack decoded = new(
                            extractedReader, c, c, null, guessedContainer, null, rawPath, spatialStream.Language);
                        return AudioFile.FromCustomTracks(resolved, [decoded]);
                    }
                }
            }
        }

        if ((probe != null && probe.AudioStreams.Count == 0 && guessedContainer != Container.NotContainer) ||
            HasVideoOnlyTrackViaMediaExtractor(resolved)) {
            throw new InvalidOperationException(S["VideoNoAudioTrack"]);
        }

        // Standard Android MediaCodec fallback (AAC, MP3, FLAC, OGG, Opus, etc.)
        RunOnUiThread(() => {
            SetWorkspaceHeaderText(S["DecodingFallback"]);
            SetConvertStatus(S["DecodingFallback"], -1);
        });
        string wavCacheDir = Path.Combine(CacheDir.AbsolutePath, "decoded_fallback");
        try {
            string decodedWav = AndroidMediaFallbackDecoder.DecodeToWav(resolved, wavCacheDir,
                p => RunOnUiThread(() => {
                    string msg = $"{S["DecodingFallback"]} ({p:0%})";
                    SetWorkspaceHeaderText(msg);
                    SetConvertStatus(msg, p);
                }));
            AudioReader fallbackReader = AudioReader.Open(decodedWav);
            Codec pcmCodec = fallbackReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE;
            string probedCodecName = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0].CodecName : null;
            string probedLang = probe != null && probe.AudioStreams.Count > 0 ? probe.AudioStreams[0].Language : null;
            (Codec srcCodec, bool isLossy) = InferFallbackCodecAndLossy(resolved, probedCodecName);
            ExternalDecodedTrack fallbackTrack = new(
                fallbackReader, pcmCodec, srcCodec, null, guessedContainer, null, null, probedLang,
                isLossySource: isLossy, originalSourcePath: resolved);
            return AudioFile.FromCustomTracks(resolved, [fallbackTrack]);
        } catch (Exception mediaEx) {
            AppLogger.Warn($"Android MediaCodec fallback failed ({mediaEx.GetType().Name}): {mediaEx.Message}");

            // Final universal fallback: decode audio stream via FFmpeg to WAV (handles DTS, DTS-HD, WMA, etc.)
            if (ffmpeg.Found && probe != null && probe.AudioStreams.Count > 0) {
                ProbedAudioStream firstAudio = probe.AudioStreams[0];
                string workDir = Path.Combine(TrueHDDecoderService.GetWorkRoot(this), $"ffwav_{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                fallbackSessionDir = workDir;
                string outWav = Path.Combine(workDir, "decoded.wav");
                RunOnUiThread(() => {
                    SetWorkspaceHeaderText(S["DecodingFallback"]);
                    SetConvertStatus(S["DecodingFallback"], -1);
                });
                bool wavOk = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                    ffmpeg.Location, resolved, firstAudio.StreamIndex, outWav, "wav",
                    probe.DurationSeconds,
                    (p, msg) => RunOnUiThread(() => {
                        SetWorkspaceHeaderText(msg);
                        SetConvertStatus(msg, p);
                    }));
                if (wavOk) {
                    AudioReader wavReader = AudioReader.Open(outWav);
                    Codec pcmCodec = wavReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE;
                    (Codec srcCodec, bool isLossy) = InferFallbackCodecAndLossy(resolved, firstAudio.CodecName);
                    ExternalDecodedTrack wavTrack = new(
                        wavReader, pcmCodec, srcCodec, null, guessedContainer, null, null, firstAudio.Language,
                        isLossySource: isLossy, originalSourcePath: resolved);
                    return AudioFile.FromCustomTracks(resolved, [wavTrack]);
                }
            }

            if (!ffmpeg.Found && containerExt is ".mkv" or ".mka" or ".m2ts" or ".ts" or ".mp4" or ".thd" or ".mlp" or ".truehd") {
                throw new NotSupportedException(S["TrueHDContainerNeedTools"], mediaEx);
            }
            throw;
        }
    }

    static (Codec SourceCodec, bool IsLossy) InferFallbackCodecAndLossy(string sourcePath, string ffCodecName) {
        string token = (ffCodecName ?? "").Trim().ToLowerInvariant();
        if (token.Contains("flac")) return (Codec.FLAC, false);
        if (token.Contains("truehd") || token.Contains("mlp")) return (Codec.TrueHD, false);
        if (token.Contains("alac") || token.StartsWith("pcm")) return (Codec.PCM_LE, false);
        if (token.Contains("eac3")) return (Codec.EnhancedAC3, true);
        if (token.Contains("ac3")) return (Codec.AC3, true);
        if (token.Contains("dts")) return (token.Contains("hd") || token.Contains("ma") ? Codec.DTS_HD : Codec.DTS, !(token.Contains("hd") || token.Contains("ma")));
        if (token.Contains("aac") || token.Contains("mp3") || token.Contains("opus") || token.Contains("vorbis") || token.Contains("wma")) {
            return (Codec.Unknown, true);
        }

        string ext = Path.GetExtension(sourcePath ?? "").ToLowerInvariant();
        return ext switch {
            ".flac" => (Codec.FLAC, false),
            ".wav" or ".aif" or ".aiff" or ".caf" => (Codec.PCM_LE, false),
            ".laf" => (Codec.LimitlessAudio, false),
            ".mp3" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" => (Codec.Unknown, true),
            ".ac3" => (Codec.AC3, true),
            ".ec3" or ".eac3" => (Codec.EnhancedAC3, true),
            ".dts" => (Codec.DTS, true),
            _ => (Codec.Unknown, true)
        };
    }

    void ShowLoadFailureDialog(Exception ex) {
        if (uiDestroyed) {
            return;
        }
        bool suggestSettings = !ffmpeg.Found || string.IsNullOrEmpty(TruehddInstallerService.ResolveExistingTruehddPath(this, store));
        if (suggestSettings) {
            OneUiModalDialog.Show(
                this,
                S["ErrorTitle"],
                ex.Message,
                icon: OneUiIconType.Info,
                iconBadgeColor: ColDanger,
                positiveText: S["OpenSettingsBtn"],
                onPositive: () => SwitchTab(4),
                negativeText: S["DismissBtn"]);
        } else {
            ShowError(ex.Message);
        }
    }

    // ICavernizeApp implementation
    public void OpenContent(string path) {
        Reset();
        ffmpeg.CheckFFmpeg();
        MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, path, Path.GetFileName(path));
        AudioFile file = OpenAudioFileResilient(path, out string fallbackDir);
        if (!string.IsNullOrEmpty(fallbackDir)) {
            if (suppressAutoTrueHDPrepare) {
                protectedQueuePaths.Add(Path.GetFullPath(fallbackDir));
            } else {
                activeFallbackSessionDir = fallbackDir;
            }
        }
        OpenContent(file, meta);
    }

    void OpenLocalPathWithFallbackAsync(string path, int? switchToTab = null, bool autoPlay = false, bool bypassBusyGuard = false) {
        if (!bypassBusyGuard && (isOperationRunning || isPreparingTrueHD)) {
            ShowError(S["OpRunning"]);
            return;
        }
        currentMediaUri = null;
        SetConvertStatus(S["CopyingMedia"], -1);
        SetWorkspaceHeaderText(Path.GetFileName(path));
        Task.Run(() => {
            try {
                MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, path, Path.GetFileName(path));
                AudioFile file = OpenAudioFileResilient(path, out string newFallbackSessionDir);
                RunOnUiThread(() => {
                    EnsureActiveQueueForCurrentFile(path);
                    string oldFallback = activeFallbackSessionDir;
                    activeFallbackSessionDir = newFallbackSessionDir;
                    pendingAutoPlayAfterLoad = autoPlay;
                    OpenContent(file, meta);
                    pendingAutoPlayAfterLoad = false;
                    if (!string.IsNullOrEmpty(oldFallback) &&
                        !string.Equals(oldFallback, newFallbackSessionDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                    SetConvertStatus(S["Ready"], 0);
                    if (switchToTab.HasValue) {
                        SwitchTab(switchToTab.Value);
                    }
                });
            } catch (Exception ex) {
                pendingSeekAfterLoadSeconds = -1;
                AppLogger.Error("OpenLocalPathWithFallbackAsync", ex);
                RunOnUiThread(() => {
                    pendingAutoPlayAfterLoad = false;
                    SetWorkspaceHeaderText(LoadedFile != null ? Path.GetFileName(LoadedFile.Path) : S["SelectFilePill"]);
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
                });
            }
        });
    }

    void SwitchActiveTrackVariant(LibraryTrackItem targetVariant, IReadOnlyList<LibraryTrackItem> allVariants) {
        if (targetVariant == null || string.IsNullOrEmpty(targetVariant.FilePath)) {
            return;
        }
        if (LoadedFile != null && string.Equals(LoadedFile.Path, targetVariant.FilePath, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        int myGen = ++variantSwitchGeneration;
        bool wasPlaying = playerEngine?.IsPlaying ?? false;
        double baseSec = (playerEngine != null && playerEngine.TotalSeconds > 0) ? playerEngine.CurrentSeconds : 0;
        long startElapsedMs = SystemClock.ElapsedRealtime();
        preferredAudioCategory = AudioQualityAnalyzer.ClassifyCategory(targetVariant);
        preferredFormatVariantKey = MediaLibraryService.GetDistinctVariantLabel(targetVariant, allVariants);

        if (activeLibraryQueueIndex >= 0 && activeLibraryQueueIndex < activeLibraryQueue.Count) {
            activeLibraryQueue[activeLibraryQueueIndex] = targetVariant;
        }

        string targetPath = targetVariant.FilePath;
        string prevTitle = currentMediaMeta?.Title;
        MediaArtworkMetadata prevMetaSnap = currentMediaMeta;

        // Keep current track playing seamlessly in the background while loading & decoding the target variant
        Task.Run(() => {
            AudioFile newFile = null;
            string newFallbackDir = null;
            ExternalDecodedTrack newDecodedTrueHD = null;
            string newTrueHDSession = null;
            try {
                MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, targetPath, Path.GetFileName(targetPath));
                newFile = OpenAudioFileResilient(targetPath, out newFallbackDir);
                if (newFile == null || newFile.Tracks.Count == 0) {
                    throw new InvalidOperationException("No playable audio tracks in selected variant.");
                }
                CavernizeTrack bestTrack = newFile.Tracks
                    .Where(x => x.Codec != Codec.Unknown && x.Supported)
                    .OrderBy(x => x.Codec)
                    .FirstOrDefault() ?? newFile.Tracks[0];

                CavernizeTrack readyTrack = bestTrack;
                if (bestTrack.Codec == Codec.TrueHD) {
                    string truehddPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
                    if (string.IsNullOrEmpty(truehddPath) || !File.Exists(truehddPath) || !store.TruehddLicenceAccepted) {
                        // Needs interactive truehdd download/licence modal -> fall back cleanly with elapsed-time compensation
                        newFile.Dispose();
                        if (!string.IsNullOrEmpty(newFallbackDir)) {
                            TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                        }
                        RunOnUiThread(() => {
                            if (uiDestroyed || myGen != variantSwitchGeneration) return;
                            double liveSec = (wasPlaying && playerEngine.IsPlaying)
                                ? playerEngine.CurrentSeconds
                                : (wasPlaying ? baseSec + (SystemClock.ElapsedRealtime() - startElapsedMs) / 1000.0 : baseSec);
                            pendingSeekAfterLoadSeconds = liveSec > 0.25 ? liveSec : -1;
                            ShowToast(string.Format(S["FormatSwitchedToast"], preferredFormatVariantKey));
                            OpenLocalPathWithFallbackAsync(targetPath, switchToTab: null, autoPlay: wasPlaying, bypassBusyGuard: true);
                        });
                        return;
                    }

                    int? forcedPres = store.MlpPresentation >= 0 ? store.MlpPresentation : null;
                    TrueHDDecodeResult decRes = TrueHDDecoderService.DecodeTrack(
                        this,
                        bestTrack,
                        newFile.Path,
                        ffmpeg.Found ? ffmpeg.Location : null,
                        truehddPath,
                        forcedPres,
                        null,
                        CancellationToken.None);
                    newDecodedTrueHD = decRes.DecodedTrack;
                    newTrueHDSession = decRes.SessionDirectory;
                    readyTrack = newDecodedTrueHD;
                }

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != variantSwitchGeneration || readyTrack == null) {
                        newDecodedTrueHD?.Dispose();
                        newFile?.Dispose();
                        if (!string.IsNullOrEmpty(newTrueHDSession)) TrueHDDecoderService.CleanupWorkDirectory(newTrueHDSession);
                        if (!string.IsNullOrEmpty(newFallbackDir)) TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                        return;
                    }

                    // Preserve high-res artwork & synced lyrics from current song to prevent visual flash
                    bool sameSong = prevMetaSnap != null &&
                        (string.Equals(prevTitle, meta?.Title, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(meta?.Title));
                    if (meta != null && sameSong) {
                        if (!meta.HasEmbeddedArtwork && IsBitmapUsable(prevMetaSnap.ArtworkBitmap)) {
                            meta.ArtworkBitmap = prevMetaSnap.ArtworkBitmap;
                            meta.HasEmbeddedArtwork = prevMetaSnap.HasEmbeddedArtwork;
                            meta.PrimaryAccent = prevMetaSnap.PrimaryAccent;
                            meta.SecondaryAccent = prevMetaSnap.SecondaryAccent;
                        }
                        if ((meta.Lyrics == null || !meta.Lyrics.HasLyrics) && prevMetaSnap.Lyrics?.HasLyrics == true) {
                            meta.Lyrics = prevMetaSnap.Lyrics;
                        }
                        if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = prevMetaSnap.Title;
                        if (string.IsNullOrWhiteSpace(meta.Artist)) meta.Artist = prevMetaSnap.Artist;
                        if (string.IsNullOrWhiteSpace(meta.Album)) meta.Album = prevMetaSnap.Album;
                    }

                    // Exact real-time elapsed timestamp so playback NEVER jumps backward
                    double liveSec = (wasPlaying && playerEngine.IsPlaying)
                        ? playerEngine.CurrentSeconds
                        : (wasPlaying ? baseSec + (SystemClock.ElapsedRealtime() - startElapsedMs) / 1000.0 : baseSec);

                    AudioFile oldFile = LoadedFile;
                    ExternalDecodedTrack oldDecoded = decodedTrueHDTrack;
                    string oldTrueHDSession = activeTrueHDSessionDir;
                    string oldFallback = activeFallbackSessionDir;

                    bool swapped = false;
                    if (readyTrack.Supported && readyTrack.Codec != Codec.TrueHD) {
                        try {
                            playerEngine.HotSwapTrackSeamless(readyTrack, liveSec, upmixingSettings, renderingSettings);
                            swapped = true;
                        } catch (Exception swapEx) {
                            AppLogger.Warn($"HotSwapTrackSeamless fallback: {swapEx.Message}");
                            swapped = false;
                        }
                    }

                    if (!swapped) {
                        // Safe fallback if seamless swap couldn't initialize
                        pendingSeekAfterLoadSeconds = liveSec > 0.25 ? liveSec : -1;
                        pendingAutoPlayAfterLoad = wasPlaying;
                        activeFallbackSessionDir = newFallbackDir;
                        OpenContent(newFile, meta);
                        pendingAutoPlayAfterLoad = false;
                        return;
                    }

                    // Update active references without stopping AudioTrack or tearing down playback
                    LoadedFile = newFile;
                    selectedTrack = bestTrack;
                    decodedTrueHDTrack = newDecodedTrueHD;
                    activeTrueHDSessionDir = newTrueHDSession;
                    activeFallbackSessionDir = newFallbackDir;
                    currentMediaMeta = meta;
                    currentMediaUri = targetVariant.ContentUri;
                    trackSourceItems = [.. playerEngine.SourceItems];

                    // Keep current stage mode (Cover or Lyrics) intact without flashing
                    if (sameSong && meta?.HasVideo != true && musicStageMode != PlayerStageMode.Video) {
                        lastStageBoundPath = meta?.SourceMediaPath ?? newFile.Path ?? "";
                    }

                    if (wasPlaying && !playerEngine.IsPlaying) {
                        playerEngine.IsPlaying = true;
                        audioOutput.Start();
                    }
                    if (meta?.HasVideo == true) {
                        musicVideoStage?.SeekTo(liveSec);
                    }

                    EnsureActiveQueueForCurrentFile(targetPath);
                    SetWorkspaceHeaderText(!string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : Path.GetFileName(newFile.Path));
                    string details = string.Join(" | ", readyTrack.Details.Select(d => $"{d.property}: {d.value}"));
                    SetWorkspaceSummaryText($"{readyTrack.FormatHeader}\n{details}");

                    mediaNotification?.UpdateMetadata(
                        !string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : Path.GetFileNameWithoutExtension(newFile.Path),
                        !string.IsNullOrWhiteSpace(meta?.Artist) ? meta.Artist : $"Cavernize 3D • {readyTrack.FormatHeader}",
                        !string.IsNullOrWhiteSpace(meta?.Album) ? meta.Album : readyTrack.FormatHeader,
                        playerEngine.TotalSeconds,
                        meta?.ArtworkBitmap);

                    RebuildInlineTrackList();
                    RefreshPlayerSourcesList();
                    RefreshMusicPlayerUi();
                    RefreshExtractorChecklist();
                    RefreshConverterTrackInfo();
                    RefreshContainerOptions();
                    SyncPlaybackUi(playerEngine.IsPlaying, playerEngine.CurrentSeconds, playerEngine.TotalSeconds);
                    UpdateRoomCanvas();
                    ShowToast(string.Format(S["FormatSwitchedToast"], preferredFormatVariantKey));

                    // Clean up previous file/session resources AFTER engine has atomically switched
                    if (oldDecoded != null && oldDecoded != newDecodedTrueHD && !IsSessionProtectedByQueue(oldTrueHDSession)) {
                        try { oldDecoded.Dispose(); } catch { }
                    }
                    if (oldFile != null && oldFile != newFile && renderQueue.Jobs.FirstOrDefault(x => x.IsUsingFile(oldFile)) == null) {
                        try { oldFile.Dispose(); } catch { }
                    }
                    if (!string.IsNullOrEmpty(oldTrueHDSession) && !string.Equals(oldTrueHDSession, newTrueHDSession, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldTrueHDSession)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldTrueHDSession);
                    }
                    if (!string.IsNullOrEmpty(oldFallback) && !string.Equals(oldFallback, newFallbackDir, StringComparison.Ordinal) &&
                        !IsSessionProtectedByQueue(oldFallback)) {
                        TrueHDDecoderService.CleanupWorkDirectory(oldFallback);
                    }
                });
            } catch (Exception ex) {
                newDecodedTrueHD?.Dispose();
                newFile?.Dispose();
                if (!string.IsNullOrEmpty(newTrueHDSession)) TrueHDDecoderService.CleanupWorkDirectory(newTrueHDSession);
                if (!string.IsNullOrEmpty(newFallbackDir)) TrueHDDecoderService.CleanupWorkDirectory(newFallbackDir);
                AppLogger.Error("SwitchActiveTrackVariant", ex);
                RunOnUiThread(() => {
                    if (!uiDestroyed && myGen == variantSwitchGeneration) {
                        ShowLoadFailureDialog(ex);
                    }
                });
            }
        });
    }

    public void OpenContent(AudioFile file) => OpenContent(file, null);

    void OpenContent(AudioFile file, MediaArtworkMetadata meta) {
        StopPlayback();
        MediaArtworkMetadata prevMeta = currentMediaMeta;
        currentMediaMeta = meta ?? (!string.IsNullOrEmpty(file?.Path)
            ? MediaMetadataHelper.Extract(this, null, file.Path, Path.GetFileName(file.Path))
            : null);

        LoadedFile = file;
        SetWorkspaceHeaderText(!string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileName(file.Path));
        AppLogger.Info($"Opened file: '{file.Path}', tracks={file.Tracks.Count}, embeddedArt={currentMediaMeta?.HasEmbeddedArtwork}");

        if (file.Tracks.Count > 0) {
            CavernizeTrack best = file.Tracks
                .Where(x => x.Codec != Codec.Unknown && x.Supported)
                .OrderBy(x => x.Codec)
                .FirstOrDefault() ?? file.Tracks[0];
            SelectedTrack = best;
        } else {
            SelectedTrack = null;
        }

        if (prevMeta != null && prevMeta != currentMediaMeta &&
            prevMeta.ArtworkBitmap != null && prevMeta.ArtworkBitmap != currentMediaMeta?.ArtworkBitmap) {
            prevMeta.ArtworkBitmap.Dispose();
            prevMeta.ArtworkBitmap = null;
        }
        discoveredSubtitles = [];
        activeSubtitleTrackId = null;
        activeSubtitleCues = [];
        musicVideoStage?.SetSubtitles(activeSubtitleCues);
        if (currentMediaMeta?.HasVideo == true) {
            StartBackgroundSubtitleDiscovery(file?.Path);
        }
        StartBackgroundOnlineEnrichment(currentMediaMeta);
        RefreshMusicPlayerUi();
    }

    void StartBackgroundSubtitleDiscovery(string fallbackPath) {
        string mediaPath = currentMediaMeta?.SourceMediaPath;
        if (string.IsNullOrEmpty(mediaPath)) mediaPath = fallbackPath;
        string title = currentMediaMeta?.Title ?? "";
        Android.Net.Uri uriSnap = currentMediaUri;
        Task.Run(() => {
            List<SubtitleTrackInfo> tracks = SubtitleService.DiscoverTracks(this, uriSnap, mediaPath, title, ffmpeg);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                discoveredSubtitles = tracks;
                // Auto-enable first external subtitle if found alongside video
                SubtitleTrackInfo autoExt = tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ExternalFilePath));
                if (autoExt != null && string.IsNullOrEmpty(activeSubtitleTrackId)) {
                    SelectSubtitleTrack(autoExt);
                }
            });
        });
    }

    CavernizeTrack GetActiveTrack() => decodedTrueHDTrack ?? selectedTrack;

    bool IsSessionProtectedByQueue(string sessionDir) {
        if (string.IsNullOrEmpty(sessionDir)) {
            return false;
        }
        string full = Path.GetFullPath(sessionDir);
        return protectedQueuePaths.Any(p => p.StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }

    IEnumerable<string> CollectActiveProtectedFiles(string alsoKeep = null) {
        List<string> list = [];
        if (!string.IsNullOrEmpty(alsoKeep)) {
            list.Add(alsoKeep);
        }
        if (LoadedFile != null && !string.IsNullOrEmpty(LoadedFile.Path)) {
            list.Add(LoadedFile.Path);
        }
        if (!string.IsNullOrEmpty(activeTrueHDSessionDir)) {
            list.Add(activeTrueHDSessionDir);
        }
        if (!string.IsNullOrEmpty(activeFallbackSessionDir)) {
            list.Add(activeFallbackSessionDir);
        }
        foreach (string p in protectedQueuePaths) {
            list.Add(p);
        }
        return list;
    }

    void ClearDecodedTrueHDState() {
        truehdPrepareGeneration++;
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        truehdPrepareCts = null;
        isPreparingTrueHD = false;

        ExternalDecodedTrack oldDecoded = decodedTrueHDTrack;
        decodedTrueHDTrack = null;
        string oldSession = activeTrueHDSessionDir;
        activeTrueHDSessionDir = null;

        if (oldDecoded != null && !protectedQueuePaths.Contains(oldDecoded.RawBitstreamPath ?? "") &&
            !IsSessionProtectedByQueue(oldSession)) {
            try {
                oldDecoded.Dispose();
            } catch {
                // Ignore
            }
        }
        if (!string.IsNullOrEmpty(oldSession) && !IsSessionProtectedByQueue(oldSession)) {
            TrueHDDecoderService.CleanupWorkDirectory(oldSession);
        }
    }

    public void Reset() {
        StopPlayback();
        ClearDecodedTrueHDState();
        environment.Reset();
        if (LoadedFile != null && renderQueue.Jobs.FirstOrDefault(x => x.IsUsingFile(LoadedFile)) == null) {
            LoadedFile.Dispose();
            LoadedFile = null;
        }
        if (!string.IsNullOrEmpty(activeFallbackSessionDir) && !IsSessionProtectedByQueue(activeFallbackSessionDir)) {
            TrueHDDecoderService.CleanupWorkDirectory(activeFallbackSessionDir);
            activeFallbackSessionDir = null;
        }
        MediaArtworkMetadata oldMeta = currentMediaMeta;
        currentMediaMeta = null;
        currentMediaUri = null;
        discoveredSubtitles = [];
        activeSubtitleTrackId = null;
        activeSubtitleCues = [];
        selectedTrack = null;
        trackSourceItems = [];
        fileDetailsExpanded = false;
        SetWorkspaceHeaderText(S["SelectFilePill"]);
        SetWorkspaceSummaryText("");
        mediaNotification?.CancelNotification();
        RefreshMusicPlayerUi();
        _ = oldMeta;
        RebuildInlineTrackList();
        report = new PostRenderReport(environment.Listener);
    }

    void EnsureActiveQueueForCurrentFile(string targetPath) {
        if (string.IsNullOrWhiteSpace(targetPath)) {
            return;
        }
        int existingIdx = activeLibraryQueue.FindIndex(x => string.Equals(x.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0) {
            activeLibraryQueueIndex = existingIdx;
            return;
        }

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot ?? MediaLibraryService.TryLoadCachedSnapshot(this);
        if (snap != null) {
            LibraryTrackItem match = snap.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) {
                // Check if current activeLibraryQueue already contains another format variant of this exact song
                if (activeLibraryQueue.Count > 0) {
                    List<LibraryTrackItem> matchVariants = MediaLibraryService.FindVariantsForTrack(
                        snap, targetPath, match.Title, match.EffectiveAlbumArtist, match.Album);
                    for (int i = 0; i < activeLibraryQueue.Count; i++) {
                        if (matchVariants.Any(v => string.Equals(v.FilePath, activeLibraryQueue[i].FilePath, StringComparison.OrdinalIgnoreCase))) {
                            activeLibraryQueue[i] = match;
                            activeLibraryQueueIndex = i;
                            return;
                        }
                    }
                }

                activeLibraryQueue.Clear();
                activeLibraryQueueIndex = -1;

                LibraryAlbumGroup alb = snap.Albums.FirstOrDefault(a => a.Tracks.Contains(match));
                List<LibraryTrackItem> group = alb != null && alb.Tracks.Count > 1
                    ? alb.Tracks
                    : snap.Tracks.Where(t => string.Equals(t.FolderPath, match.FolderPath, StringComparison.OrdinalIgnoreCase)).ToList();

                List<string> selectedFolders = store.GetLibraryFilterFolders();
                List<LibraryTrackItem> filteredGroup = group
                    .Where(t => MediaLibraryService.PassesAllFilters(
                        t, store.LibraryFilterMode, store.LibraryHideNonMusic, store.LibraryFolderFilterMode, selectedFolders))
                    .ToList();
                List<LibraryTrackItem> useList = filteredGroup.Contains(match) ? filteredGroup : group;
                List<LibraryMergedTrack> merged = MediaLibraryService.MergeAlbumTrackVariants(useList);
                foreach (LibraryMergedTrack m in merged) {
                    LibraryTrackItem pick = m.Variants.Contains(match)
                        ? match
                        : (preferredAudioCategory.HasValue
                            ? AudioQualityAnalyzer.PickBestVariantForNextTrack(m.Variants, preferredAudioCategory.Value, this)
                            : m.Primary);
                    activeLibraryQueue.Add(pick ?? m.Primary);
                }
                activeLibraryQueueIndex = activeLibraryQueue.IndexOf(match);
                if (activeLibraryQueue.Count > 0 && activeLibraryQueueIndex >= 0) {
                    return;
                }
                activeLibraryQueue.Clear();
            }
        }

        activeLibraryQueue.Clear();
        activeLibraryQueueIndex = -1;

        // Fallback: enumerate sibling media files in the same folder outside app cache
        try {
            string dir = Path.GetDirectoryName(targetPath);
            string cacheRoot = CacheDir?.AbsolutePath ?? "";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                (string.IsNullOrEmpty(cacheRoot) || !dir.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))) {
                List<string> siblings = Directory.EnumerateFiles(dir)
                    .Where(f => MediaLibraryService.IsSupportedMediaExtension(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (string s in siblings) {
                    activeLibraryQueue.Add(new LibraryTrackItem {
                        FilePath = s,
                        Title = Path.GetFileNameWithoutExtension(s),
                        FolderPath = dir,
                        FolderName = Path.GetFileName(dir) ?? "",
                        FormatBadge = Path.GetExtension(s).TrimStart('.').ToUpperInvariant()
                    });
                }
                activeLibraryQueueIndex = activeLibraryQueue.FindIndex(x => string.Equals(x.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
            }
        } catch {
            // Keep queue empty on scoped folder error
        }
    }

    void OnSelectedTrackChanged() {
        StopPlayback();
        CancelNeuralTrackPrep(silent: true);
        ClearDecodedTrueHDState();
        if (selectedTrack == null) {
            fileDetailsExpanded = false;
            SetWorkspaceSummaryText("");
            trackSourceItems = [];
            playerEngine?.BindSeparatedNeuralTrack(null);
            playerEngine?.VocalMixMatchStage?.ApplySongDna(Cavern.VocalChain.VocalMixDnaProfile.CreateStudioDefault());
            mediaNotification?.CancelNotification();
            RefreshPlayerSourcesList();
            RefreshMusicPlayerUi();
            RefreshExtractorChecklist();
            RefreshConverterTrackInfo();
            RebuildInlineTrackList();
            RefreshNeuralPrepUi();
            return;
        }

        bool shouldAutoPlay = pendingAutoPlayAfterLoad;
        pendingAutoPlayAfterLoad = false;

        if (selectedTrack.Codec == Codec.TrueHD) {
            AppLogger.Info($"Selected TrueHD track: {selectedTrack.FormatHeader}");
            RebuildInlineTrackList();
            RefreshConverterTrackInfo();
            RefreshContainerOptions();
            RefreshMusicPlayerUi();
            if (!suppressAutoTrueHDPrepare) {
                PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: shouldAutoPlay);
            }
            return;
        }

        BindActiveTrackToUiAndPlayer(selectedTrack);
        if (shouldAutoPlay && selectedTrack.Supported && !(playerEngine?.IsPlaying ?? false)) {
            TogglePlayPause();
        }
    }

    void BindActiveTrackToUiAndPlayer(CavernizeTrack activeTrack) {
        if (activeTrack == null) {
            return;
        }
        AppLogger.Info($"Active track ready: {activeTrack.FormatHeader}");
        string details = string.Join(" | ", activeTrack.Details.Select(d => $"{d.property}: {d.value}"));
        SetWorkspaceSummaryText($"{activeTrack.FormatHeader}\n{details}");
        RebuildInlineTrackList();

        if (activeTrack.Supported && activeTrack.Codec != Codec.TrueHD) {
            try {
                playerEngine.Load(activeTrack, upmixingSettings, renderingSettings);
                trackSourceItems = [.. playerEngine.SourceItems];
            } catch (Exception ex) {
                AppLogger.Error("PlayerEngine.Load", ex);
                trackSourceItems = TrackSourceItem.BuildFromTrack(activeTrack);
                ShowError(ex.Message);
            }
        } else {
            trackSourceItems = TrackSourceItem.BuildFromTrack(activeTrack);
        }

        double initialSeekSec = 0;
        if (pendingSeekAfterLoadSeconds >= 0 && activeTrack.Supported && activeTrack.Codec != Codec.TrueHD && playerEngine.TotalSeconds > 0) {
            initialSeekSec = Math.Clamp(pendingSeekAfterLoadSeconds, 0, Math.Max(0, playerEngine.TotalSeconds - 0.1));
            pendingSeekAfterLoadSeconds = -1;
            playerEngine.Seek(initialSeekSec);
            musicVideoStage?.SeekTo(initialSeekSec);
        } else {
            pendingSeekAfterLoadSeconds = -1;
        }

        string metaTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : "Cavernize 3D");
        string metaArtist = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
            ? currentMediaMeta.Artist
            : $"Cavernize 3D • {activeTrack.FormatHeader}";
        string metaAlbum = !string.IsNullOrWhiteSpace(currentMediaMeta?.Album)
            ? currentMediaMeta.Album
            : activeTrack.FormatHeader;

        mediaNotification?.UpdateMetadata(
            metaTitle,
            metaArtist,
            metaAlbum,
            playerEngine.TotalSeconds,
            currentMediaMeta?.ArtworkBitmap);

        RefreshPlayerSourcesList();
        RefreshMusicPlayerUi();
        RefreshExtractorChecklist();
        RefreshConverterTrackInfo();
        RefreshContainerOptions();
        SyncPlaybackUi(false, initialSeekSec, playerEngine.TotalSeconds);
        UpdateRoomCanvas();
        playerEngine?.VocalMixMatchStage?.ApplySongDna(Cavern.VocalChain.VocalMixDnaProfile.CreateStudioDefault());
        StartLosslessTwinDiscoveryAsync(activeTrack);
        TryLoadCachedSongVocalDnaAsync(activeTrack);
        TryAutoBindCachedNeuralStemsAsync(activeTrack);
        RefreshNeuralPrepUi();
    }

    void StartLosslessTwinDiscoveryAsync(CavernizeTrack activeTrack) {
        if (uiDestroyed || activeTrack == null || playerEngine == null) {
            return;
        }
        losslessTwinCts?.Cancel();
        losslessTwinCts?.Dispose();
        losslessTwinCts = new CancellationTokenSource();
        CancellationToken ct = losslessTwinCts.Token;
        int gen = ++losslessTwinGeneration;

        string activePath = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        bool alreadyLossless = LosslessTwinDiscoveryService.IsTrackAlreadyLossless(activeTrack, activePath);
        playerEngine.LgsrStage.SetActiveTrackLosslessStatus(alreadyLossless);

        // Detach stale lossless twin immediately while discovering the twin for the new track
        Cavernize.Logic.Spatial.UniversalLosslessTwinProvider previous = activeLosslessTwin;
        activeLosslessTwin = null;
        playerEngine.BindLosslessReference(null);
        previous?.Dispose();

        Cavernize.Logic.Spatial.LgsrConfig lgsrCfg = store.BuildLgsrConfig();
        playerEngine.ApplyLgsrConfig(lgsrCfg);
        RefreshLgsrUi();

        if (!lgsrCfg.Enabled || !lgsrCfg.AutoDiscoverTwin || (lgsrCfg.BypassOnAlreadyLossless && alreadyLossless)) {
            return;
        }

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        string title = currentMediaMeta?.Title ?? "";
        string artist = currentMediaMeta?.Artist ?? "";
        string album = currentMediaMeta?.Album ?? "";
        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;

        _ = Task.Run(async () => {
            try {
                Cavernize.Logic.Spatial.UniversalLosslessTwinProvider twin =
                    await LosslessTwinDiscoveryService.DiscoverAndSyncAsync(
                        this,
                        snap,
                        activeTrack,
                        activePath,
                        title,
                        artist,
                        album,
                        lgsrCfg,
                        ffLoc,
                        ct).ConfigureAwait(false);

                RunOnUiThread(() => {
                    if (uiDestroyed || gen != losslessTwinGeneration) {
                        twin.Dispose();
                        return;
                    }
                    activeLosslessTwin = twin;
                    playerEngine.BindLosslessReference(twin);
                    RefreshLgsrUi();
                });
            } catch (System.OperationCanceledException) {
                // Superseded by newer track selection
            } catch (Exception ex) {
                AppLogger.Warn($"W09 StartLosslessTwinDiscoveryAsync: {ex.Message}");
            }
        }, ct);
    }

    void CancelNeuralTrackPrep(bool silent = false) {
        neuralPrepGeneration++;
        try {
            neuralPrepCts?.Cancel();
            neuralPrepCts?.Dispose();
        } catch { }
        neuralPrepCts = null;
        isNeuralPrepRunning = false;
        neuralPrepProgress = 0;
        neuralPrepStatusText = "";
        RefreshNeuralPrepUi();
        if (!silent) {
            Toast.MakeText(this, S["CancelledStatus"], ToastLength.Short).Show();
        }
    }

    sealed class CachedVocalDnaRecord {
        public float[] BandEnergyDb { get; set; }
        public float SpectralTiltDbPerOct { get; set; }
        public float SibilanceIndexDb { get; set; }
        public float TargetVocalLufs { get; set; }
        public float CrestFactorDb { get; set; }
        public float HarmonicWarmth { get; set; }
        public float ReverbRt60Sec { get; set; }
        public float ReverbPreDelayMs { get; set; }
        public float ReverbWetMix { get; set; }
        public float ReverbDampingHz { get; set; }
        public float DelayTimeMs { get; set; }
        public float DelayWetMix { get; set; }
        public float StereoWidth { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public bool HasMeasuredDereverb { get; set; }
        public float? MeasuredDrrDb { get; set; }
        public float ActiveSpeechRmsDbfs { get; set; }
        public int ActiveWindowCount { get; set; }
        public string SourceTrackSummary { get; set; }
    }

    string ResolveVocalDnaCacheFilePath(CavernizeTrack track) {
        string sourceKey = track?.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? track?.Path ?? "";
        if (string.IsNullOrEmpty(sourceKey) || !File.Exists(sourceKey)) {
            return null;
        }
        try {
            FileInfo fi = new(sourceKey);
            string raw = $"{sourceKey}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|dna_v2";
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            string hex = Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
            string dir = Path.Combine(CacheDir?.AbsolutePath ?? FilesDir.AbsolutePath, "vocal_dna");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{hex}.json");
        } catch {
            return null;
        }
    }

    void SaveCachedSongVocalDna(CavernizeTrack track, Cavern.VocalChain.VocalMixDnaProfile dna) {
        if (track == null || dna == null || !dna.IsExtractedFromSong) {
            return;
        }
        string cachePath = ResolveVocalDnaCacheFilePath(track);
        if (string.IsNullOrEmpty(cachePath)) {
            return;
        }
        try {
            CachedVocalDnaRecord rec = new() {
                BandEnergyDb = dna.BandEnergyDb,
                SpectralTiltDbPerOct = dna.SpectralTiltDbPerOct,
                SibilanceIndexDb = dna.SibilanceIndexDb,
                TargetVocalLufs = dna.TargetVocalLufs,
                CrestFactorDb = dna.CrestFactorDb,
                HarmonicWarmth = dna.HarmonicWarmth,
                ReverbRt60Sec = dna.ReverbRt60Sec,
                ReverbPreDelayMs = dna.ReverbPreDelayMs,
                ReverbWetMix = dna.ReverbWetMix,
                ReverbDampingHz = dna.ReverbDampingHz,
                DelayTimeMs = dna.DelayTimeMs,
                DelayWetMix = dna.DelayWetMix,
                StereoWidth = dna.StereoWidth,
                PosX = dna.VocalAtmosPosition.X,
                PosY = dna.VocalAtmosPosition.Y,
                PosZ = dna.VocalAtmosPosition.Z,
                HasMeasuredDereverb = dna.HasMeasuredDereverb,
                MeasuredDrrDb = float.IsFinite(dna.MeasuredDrrDb) ? dna.MeasuredDrrDb : null,
                ActiveSpeechRmsDbfs = dna.ActiveSpeechRmsDbfs,
                ActiveWindowCount = dna.ActiveWindowCount,
                SourceTrackSummary = dna.SourceTrackSummary ?? ""
            };
            string json = System.Text.Json.JsonSerializer.Serialize(rec);
            File.WriteAllText(cachePath, json);
        } catch (Exception ex) {
            AppLogger.Warn($"W19.b SaveCachedSongVocalDna: {ex.Message}");
        }
    }

    void TryLoadCachedSongVocalDnaAsync(CavernizeTrack activeTrack) {
        if (uiDestroyed || activeTrack == null || playerEngine == null) {
            return;
        }
        int gen = ++vocalDnaLoadGeneration;
        string cachePath = ResolveVocalDnaCacheFilePath(activeTrack);
        if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) {
            return;
        }
        _ = Task.Run(() => {
            try {
                string json = File.ReadAllText(cachePath);
                CachedVocalDnaRecord rec = System.Text.Json.JsonSerializer.Deserialize<CachedVocalDnaRecord>(json);
                if (rec == null || rec.BandEnergyDb == null || rec.BandEnergyDb.Length != Cavern.VocalChain.VocalMixDnaProfile.AnalysisBandHz.Length) {
                    return;
                }
                Cavern.VocalChain.VocalMixDnaProfile dna = new() {
                    BandEnergyDb = rec.BandEnergyDb,
                    SpectralTiltDbPerOct = rec.SpectralTiltDbPerOct,
                    SibilanceIndexDb = rec.SibilanceIndexDb,
                    TargetVocalLufs = rec.TargetVocalLufs,
                    CrestFactorDb = rec.CrestFactorDb,
                    HarmonicWarmth = rec.HarmonicWarmth,
                    ReverbRt60Sec = rec.ReverbRt60Sec,
                    ReverbPreDelayMs = rec.ReverbPreDelayMs,
                    ReverbWetMix = rec.ReverbWetMix,
                    ReverbDampingHz = rec.ReverbDampingHz,
                    DelayTimeMs = rec.DelayTimeMs,
                    DelayWetMix = rec.DelayWetMix,
                    StereoWidth = rec.StereoWidth,
                    VocalAtmosPosition = new System.Numerics.Vector3(rec.PosX, rec.PosY, rec.PosZ),
                    IsExtractedFromSong = true,
                    HasMeasuredDereverb = rec.HasMeasuredDereverb,
                    MeasuredDrrDb = rec.MeasuredDrrDb ?? float.NaN,
                    ActiveSpeechRmsDbfs = rec.ActiveSpeechRmsDbfs,
                    ActiveWindowCount = rec.ActiveWindowCount,
                    SourceTrackSummary = rec.SourceTrackSummary ?? ""
                };
                RunOnUiThread(() => {
                    if (uiDestroyed || gen != vocalDnaLoadGeneration || GetActiveTrack() != activeTrack) {
                        return;
                    }
                    ApplyExtractedSongDnaOnUiThread(dna);
                });
            } catch (Exception ex) {
                AppLogger.Warn($"W19.b TryLoadCachedSongVocalDnaAsync: {ex.Message}");
            }
        });
    }

    void ApplyExtractedSongDnaOnUiThread(Cavern.VocalChain.VocalMixDnaProfile dna) {
        if (dna == null || !dna.IsExtractedFromSong || playerEngine?.VocalMixMatchStage == null) {
            return;
        }
        Cavern.VocalChain.VocalMixDnaProfile current = playerEngine.VocalMixMatchStage.ActiveDna;
        if (current.IsExtractedFromSong && current.HasMeasuredDereverb && !dna.HasMeasuredDereverb) {
            return;
        }
        playerEngine.VocalMixMatchStage.ApplySongDna(dna);
        RefreshLyricsSingDockUi();
        RefreshSpatialXRayHud();
    }

    void PrepareSongVocalDnaAsync() {
        if (uiDestroyed) {
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || !activeTrack.Supported || activeTrack.Codec == Codec.TrueHD) {
            ShowToast(S["NeuralPrepNoTrackToast"]);
            return;
        }
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(
            Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna, cfg);
        bool runtimeReady = neuralRuntime != null && neuralRuntime.IsAvailable;
        if (!runtimeReady || plan == null || !plan.IsRunnable) {
            ShowToast(S["VocalDnaNeedModelsToast"]);
            return;
        }
        PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna);
    }

    void TryAutoBindCachedNeuralStemsAsync(CavernizeTrack activeTrack) {
        if (uiDestroyed || activeTrack == null || neuralOrchestrator == null || neuralTrackCache == null || playerEngine == null) {
            return;
        }
        string sourceKey = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        if (string.IsNullOrEmpty(sourceKey) || !File.Exists(sourceKey)) {
            return;
        }
        int gen = neuralPrepGeneration;
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        int sr = activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;

        _ = Task.Run(() => {
            try {
                FileInfo fi = new(sourceKey);
                Cavernize.Logic.Spatial.NeuralSeparatedTrack playbackTrack = null;
                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaTrack = null;

                Cavernize.Logic.Spatial.NeuralPipelineGoal[] playbackGoals = [
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.StemSeparation,
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke
                ];
                for (int g = 0; g < playbackGoals.Length; g++) {
                    Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator.BuildPlan(playbackGoals[g], cfg);
                    if (!plan.IsRunnable) {
                        continue;
                    }
                    string cacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, plan.PlanSignature);
                    if (neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cached)) {
                        playbackTrack = cached;
                        break;
                    }
                }

                // Also check dedicated Slot 7A VocalMixDna cache (includes Dereverb dry/wet analysis when available)
                Cavernize.Logic.Spatial.NeuralExecutionPlan dnaPlan = neuralOrchestrator.BuildPlan(
                    Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna, cfg);
                if (dnaPlan.IsRunnable) {
                    string dnaCacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                        sourceKey, fi.Length, fi.LastWriteTimeUtc.Ticks, sr, dnaPlan.PlanSignature);
                    if (neuralTrackCache.TryLoad(dnaCacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedDnaTrack)) {
                        dnaTrack = cachedDnaTrack;
                        if (playbackTrack == null) {
                            playbackTrack = cachedDnaTrack;
                        }
                    }
                }

                Cavernize.Logic.Spatial.NeuralSeparatedTrack dnaSource = dnaTrack ?? playbackTrack;
                string summaryTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                    ? currentMediaMeta.Title
                    : Path.GetFileNameWithoutExtension(sourceKey);
                Cavern.VocalChain.VocalMixDnaProfile extractedDna = dnaSource != null
                    ? Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(dnaSource, summaryTitle)
                    : null;
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                if (playbackTrack != null || extractedDna != null) {
                    RunOnUiThread(() => {
                        if (uiDestroyed || gen != neuralPrepGeneration || GetActiveTrack() != activeTrack) {
                            return;
                        }
                        if (playbackTrack != null) {
                            playerEngine.BindSeparatedNeuralTrack(playbackTrack);
                        }
                        ApplyExtractedSongDnaOnUiThread(extractedDna);
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
                    });
                }
            } catch (Exception ex) {
                AppLogger.Warn($"W18/W19.b TryAutoBindCachedNeuralStemsAsync: {ex.Message}");
            }
        });
    }

    void PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal goal, bool silentIfNotReady = false) {
        if (uiDestroyed) {
            return;
        }
        if (isNeuralPrepRunning) {
            CancelNeuralTrackPrep(silent: false);
            return;
        }
        CavernizeTrack activeTrack = GetActiveTrack();
        if (activeTrack == null || !activeTrack.Supported || activeTrack.Codec == Codec.TrueHD) {
            if (!silentIfNotReady) {
                Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
            }
            return;
        }
        Cavernize.Logic.Spatial.NeuralAudioConfig cfg = store.BuildNeuralAudioConfig();
        Cavernize.Logic.Spatial.NeuralExecutionPlan plan = neuralOrchestrator?.BuildPlan(goal, cfg);
        bool runtimeReady = neuralRuntime != null && neuralRuntime.IsAvailable;
        if (!runtimeReady || plan == null || !plan.IsRunnable) {
            if (!silentIfNotReady) {
                string msgKey = goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna
                    ? "VocalDnaNeedModelsToast"
                    : "NeuralPrepNeedModelsToast";
                Toast.MakeText(this, S[msgKey], ToastLength.Long).Show();
            }
            return;
        }

        string activePath = activeTrack.OriginalSourcePath ?? currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path ?? activeTrack.Path ?? "";
        string decodePath = activePath;
        bool derivedFromTwin = false;
        if (cfg.UseLosslessTwinForKaraoke &&
            activeLosslessTwin != null &&
            activeLosslessTwin.HasSyncedLosslessTwin &&
            !string.IsNullOrEmpty(activeLosslessTwin.Metadata?.FilePath) &&
            File.Exists(activeLosslessTwin.Metadata.FilePath)) {
            decodePath = activeLosslessTwin.Metadata.FilePath;
            derivedFromTwin = true;
        }
        if (string.IsNullOrEmpty(decodePath) || !File.Exists(decodePath)) {
            if (!silentIfNotReady) {
                Toast.MakeText(this, S["NeuralPrepNoTrackToast"], ToastLength.Short).Show();
            }
            return;
        }

        neuralPrepCts?.Cancel();
        neuralPrepCts?.Dispose();
        neuralPrepCts = new CancellationTokenSource();
        CancellationToken ct = neuralPrepCts.Token;
        int myGen = ++neuralPrepGeneration;
        isNeuralPrepRunning = true;
        neuralPrepProgress = 0.02;
        neuralPrepStatusText = S["NeuralPrepDecodingStatus"];
        RefreshNeuralPrepUi();

        string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
        int prefSr = activeTrack.SampleRate >= 8000 ? activeTrack.SampleRate : 48000;
        string summaryTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileNameWithoutExtension(activePath);

        _ = Task.Run(() => {
            try {
                FileInfo keyInfo = new(activePath);
                string cacheKey = Cavernize.Logic.Spatial.NeuralPipelineOrchestrator.ComputeTrackCacheKey(
                    activePath, keyInfo.Length, keyInfo.LastWriteTimeUtc.Ticks, prefSr, plan.PlanSignature);

                // Instant Slot 7B cache hit before decoding PCM
                if (neuralTrackCache != null && neuralTrackCache.TryLoad(cacheKey, out Cavernize.Logic.Spatial.NeuralSeparatedTrack cachedTrack, ct)) {
                    Cavern.VocalChain.VocalMixDnaProfile cachedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                        cachedTrack, summaryTitle);
                    if (cachedDna != null && cachedDna.IsExtractedFromSong) {
                        SaveCachedSongVocalDna(activeTrack, cachedDna);
                    }
                    RunOnUiThread(() => {
                        if (uiDestroyed || myGen != neuralPrepGeneration) {
                            return;
                        }
                        isNeuralPrepRunning = false;
                        neuralPrepProgress = 1.0;
                        neuralPrepStatusText = "";
                        if (goal != Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna ||
                            !(playerEngine.NeuralStage?.HasPlayableSeparatedStems ?? false)) {
                            playerEngine.BindSeparatedNeuralTrack(cachedTrack);
                        }
                        ApplyExtractedSongDnaOnUiThread(cachedDna);
                        RefreshNeuralAudioUi();
                        RefreshNeuralPrepUi();
                        string msg = (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna && cachedDna != null && cachedDna.IsExtractedFromSong)
                            ? string.Format(S["VocalDnaReadyToast"], cachedDna.TargetVocalLufs, cachedDna.ReverbRt60Sec, cachedDna.ActiveWindowCount)
                            : string.Format(S["NeuralPrepCacheHitToast"], cachedTrack.Stems.Count);
                        Toast.MakeText(this, msg, ToastLength.Short).Show();
                    });
                    return;
                }

                ct.ThrowIfCancellationRequested();
                if (!LosslessTwinDiscoveryService.TryDecodeTrackToStereoFloat(
                    decodePath, prefSr, ffLoc, ct, out float[] mixL, out float[] mixR, out int decodedSr)) {
                    throw new InvalidOperationException("Unable to decode track audio for neural separation.");
                }

                ct.ThrowIfCancellationRequested();
                (Cavernize.Logic.Spatial.NeuralSeparatedTrack separated, float rtf, bool fromCache) = neuralOrchestrator.ExecuteTrackPipeline(
                    sourceTrackKey: activePath,
                    fileSizeBytes: keyInfo.Length,
                    lastModifiedTicks: keyInfo.LastWriteTimeUtc.Ticks,
                    mixLeft: mixL,
                    mixRight: mixR,
                    sampleRate: decodedSr,
                    goal: goal,
                    config: cfg,
                    derivedFromLosslessTwin: derivedFromTwin,
                    onProgress: (p, msg) => RunOnUiThread(() => {
                        if (uiDestroyed || myGen != neuralPrepGeneration) {
                            return;
                        }
                        neuralPrepProgress = Math.Clamp(p, 0.0, 1.0);
                        neuralPrepStatusText = msg ?? "";
                        RefreshNeuralPrepUi();
                    }),
                    token: ct);

                Cavern.VocalChain.VocalMixDnaProfile extractedDna = Cavernize.Logic.Spatial.VocalMixMatchStageProcessor.ExtractSongVocalDna(
                    separated, summaryTitle);
                if (extractedDna != null && extractedDna.IsExtractedFromSong) {
                    SaveCachedSongVocalDna(activeTrack, extractedDna);
                }

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    neuralPrepProgress = 1.0;
                    neuralPrepStatusText = "";
                    if (goal != Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna ||
                        !(playerEngine.NeuralStage?.HasPlayableSeparatedStems ?? false)) {
                        playerEngine.BindSeparatedNeuralTrack(separated);
                    }
                    ApplyExtractedSongDnaOnUiThread(extractedDna);
                    RefreshNeuralAudioUi();
                    RefreshNeuralPrepUi();
                    string toastMsg;
                    if (goal == Cavernize.Logic.Spatial.NeuralPipelineGoal.VocalMixDna && extractedDna != null && extractedDna.IsExtractedFromSong) {
                        toastMsg = string.Format(S["VocalDnaReadyToast"], extractedDna.TargetVocalLufs, extractedDna.ReverbRt60Sec, extractedDna.ActiveWindowCount);
                    } else {
                        toastMsg = fromCache
                            ? string.Format(S["NeuralPrepCacheHitToast"], separated.Stems.Count)
                            : string.Format(S["NeuralPrepDoneToast"], separated.Stems.Count, rtf);
                    }
                    Toast.MakeText(this, toastMsg, ToastLength.Short).Show();
                });
            } catch (System.OperationCanceledException) {
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                });
            } catch (Exception ex) {
                AppLogger.Warn($"W18/W19.b PrepareActiveTrackNeuralStemsAsync: {ex.Message}");
                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != neuralPrepGeneration) {
                        return;
                    }
                    isNeuralPrepRunning = false;
                    neuralPrepProgress = 0;
                    neuralPrepStatusText = "";
                    RefreshNeuralPrepUi();
                    Toast.MakeText(this, string.Format(S["NeuralPrepFailedFmt"], ex.Message), ToastLength.Long).Show();
                });
            }
        }, ct);
    }

    void PrepareSelectedTrueHDTrackAsync(CavernizeTrack sourceTrack, bool autoPlayAfter) {
        if (sourceTrack == null || isPreparingTrueHD || uiDestroyed) {
            return;
        }
        string existingBin = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        if (string.IsNullOrEmpty(existingBin) || !File.Exists(existingBin)) {
            ShowError(S["TruehddNotInstalled"]);
            BindActiveTrackToUiAndPlayer(sourceTrack);
            return;
        }

        if (!store.TruehddLicenceAccepted) {
            _ = Task.Run(async () => {
                string licText = await TruehddInstallerService.FetchLicenceTextAsync(this).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed || selectedTrack != sourceTrack) {
                        return;
                    }
                    OneUiModalDialog.Show(
                        this,
                        S["TrueHDLicenceTitle"],
                        $"{S["TrueHDLicenceDesc"]}\n\n{licText}",
                        icon: OneUiIconType.Sparkles,
                        iconBadgeColor: OneUiTheme.Primary,
                        positiveText: S["TrueHDDownloadDecode"],
                        onPositive: () => {
                            store.TruehddLicenceAccepted = true;
                            StartTrueHDPrepareWorker(sourceTrack, autoPlayAfter);
                        },
                        negativeText: S["CancelBtn"],
                        onNegative: () => {
                            SetConvertStatus(S["TrueHDCancelled"], 0);
                            BindActiveTrackToUiAndPlayer(sourceTrack);
                        },
                        cancelable: false);
                });
            });
            return;
        }

        StartTrueHDPrepareWorker(sourceTrack, autoPlayAfter);
    }

    void StartTrueHDPrepareWorker(CavernizeTrack sourceTrack, bool autoPlayAfter) {
        if (uiDestroyed || selectedTrack != sourceTrack) {
            return;
        }
        int myGen = ++truehdPrepareGeneration;
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        truehdPrepareCts = new CancellationTokenSource();
        CancellationToken token = truehdPrepareCts.Token;
        isPreparingTrueHD = true;

        LinearLayout dlgContent = new(this) { Orientation = Orientation.Vertical };
        TextView dlgStatus = Label(S["TrueHDDecoding"], 12.5f, OneUiTheme.TextSecondary);
        dlgStatus.SetPadding(0, 0, 0, Dp(10));
        ProgressBar dlgProgress = new(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) {
            Max = 1000,
            Indeterminate = true
        };
        OneUiTheme.StyleProgressBar(this, dlgProgress);
        dlgContent.AddView(dlgStatus);
        dlgContent.AddView(dlgProgress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(14)));

        Android.App.Dialog progressDlg = OneUiModalDialog.Show(
            this,
            S["TrueHDTitle"],
            customContent: dlgContent,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: null,
            negativeText: S["CancelBtn"],
            onNegative: () => {
                if (myGen == truehdPrepareGeneration) {
                    truehdPrepareGeneration++;
                    isPreparingTrueHD = false;
                }
                truehdPrepareCts?.Cancel();
                SetConvertStatus(S["TrueHDCancelled"], 0);
                BindActiveTrackToUiAndPlayer(sourceTrack);
            },
            cancelable: false);

        void UpdatePrepUi(double p, string msg) {
            RunOnUiThread(() => {
                if (uiDestroyed || myGen != truehdPrepareGeneration) return;
                dlgStatus.Text = msg ?? "";
                if (p < 0) {
                    dlgProgress.Indeterminate = true;
                } else {
                    dlgProgress.Indeterminate = false;
                    dlgProgress.Progress = Math.Clamp((int)Math.Round(p * 1000.0), 0, 1000);
                }
                SetConvertStatus(msg, p);
            });
        }

        Task.Run(async () => {
            try {
                string truehddPath = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
                if (string.IsNullOrEmpty(truehddPath) || !File.Exists(truehddPath)) {
                    throw new FileNotFoundException(S["TruehddNotInstalled"]);
                }
                Truehdd.CustomExecutablePath = truehddPath;

                token.ThrowIfCancellationRequested();
                int? forcedPres = store.MlpPresentation >= 0 ? store.MlpPresentation : null;
                TrueHDDecodeResult res = TrueHDDecoderService.DecodeTrack(
                    this,
                    sourceTrack,
                    LoadedFile?.Path,
                    ffmpeg.Found ? ffmpeg.Location : null,
                    truehddPath,
                    forcedPres,
                    UpdatePrepUi,
                    token);

                RunOnUiThread(() => {
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                    }
                    try { progressDlg?.Dismiss(); } catch { }
                    if (uiDestroyed || selectedTrack != sourceTrack || myGen != truehdPrepareGeneration) {
                        TrueHDDecoderService.CleanupWorkDirectory(res.SessionDirectory);
                        res.DecodedTrack?.Dispose();
                        return;
                    }
                    decodedTrueHDTrack = res.DecodedTrack;
                    activeTrueHDSessionDir = res.SessionDirectory;
                    SetConvertStatus(S["Ready"], 0);
                    BindActiveTrackToUiAndPlayer(decodedTrueHDTrack);
                    if (autoPlayAfter && decodedTrueHDTrack != null) {
                        TogglePlayPause();
                    }
                });
            } catch (System.OperationCanceledException) {
                AppLogger.Info("TrueHD decode cancelled by user.");
                RunOnUiThread(() => {
                    try { progressDlg?.Dismiss(); } catch { }
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                        SetConvertStatus(S["TrueHDCancelled"], 0);
                        BindActiveTrackToUiAndPlayer(sourceTrack);
                    }
                });
            } catch (Exception ex) {
                AppLogger.Error("PrepareSelectedTrueHDTrackAsync", ex);
                RunOnUiThread(() => {
                    try { progressDlg?.Dismiss(); } catch { }
                    if (myGen == truehdPrepareGeneration) {
                        isPreparingTrueHD = false;
                        SetConvertStatus(ex.Message, 0);
                        BindActiveTrackToUiAndPlayer(sourceTrack);
                        ShowError(ex.Message);
                    }
                });
            }
        });
    }

    void RefreshAllUiState() {
        RefreshConverterTabState();
        RefreshPlayerSourcesList();
        RefreshMusicPlayerUi();
        RefreshExtractorChecklist();
        RefreshSettingsTabButtons();
        RefreshSettingsDot();
        RefreshFFmpegCardUi();
        RefreshTruehddCardUi();
        RefreshSettingsUpdateUi();
        UpdateRoomCanvas();
    }

    void RefreshSettingsDot() {
        ViewStates vis = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
        if (settingsUpdateDot != null) {
            settingsUpdateDot.Visibility = vis;
        }
        if (settingsUpdateDotCollapsed != null) {
            settingsUpdateDotCollapsed.Visibility = vis;
        }
    }

    // Shared One UI 9 Frosted UI Helpers (zero emojis, non-clipping pills, contextual '?' help bubbles)
    int Dp(int dp) => (int)Math.Round(dp * Resources.DisplayMetrics.Density);

    LinearLayout HRow() {
        LinearLayout row = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
    }

    LinearLayout Card(string title = null, OneUiIconType? icon = null, Color? badgeColor = null) {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.Background = OneUiTheme.CreateGlassCardDrawable(this, null, OneUiTheme.CardCornerDp);
        card.Elevation = Dp(6);
        card.SetPadding(Dp(16), Dp(15), Dp(16), Dp(15));
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(14)
        };
        card.LayoutParameters = lp;
        if (!string.IsNullOrEmpty(title)) {
            LinearLayout hdr = HRow();
            hdr.SetPadding(0, 0, 0, Dp(10));
            if (icon.HasValue && badgeColor.HasValue) {
                hdr.AddView(OneUiTheme.CreateVectorIconBadge(this, icon.Value, badgeColor.Value));
            }
            TextView tv = SectionTitle(title);
            tv.SetPadding(icon.HasValue ? Dp(10) : 0, 0, Dp(8), 0);
            hdr.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            card.AddView(hdr);
        }
        return card;
    }

    View CreateInlineHelpBtn(string optionTitle, string causeEffectHelp) {
        FrameLayout badge = new(this);
        badge.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceSubTile, 99f);
        OneUiTheme.AttachRecoil(badge, 0.88f);
        OneUiIconView ico = new(this, OneUiIconType.HelpQuestion, OneUiTheme.TextSub);
        badge.AddView(ico, new FrameLayout.LayoutParams(Dp(14), Dp(14)) { Gravity = GravityFlags.Center });
        badge.Click += (_, _) => OneUiModalDialog.Show(
            this,
            optionTitle,
            causeEffectHelp,
            icon: OneUiIconType.Info,
            iconBadgeColor: ColAccent,
            positiveText: "OK");
        return badge;
    }

    LinearLayout WrapControlWithHelp(View control, string helpTitle, string helpBody, int bottomMarginDp = 6) {
        LinearLayout row = HRow();
        row.AddView(control, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (!string.IsNullOrEmpty(helpBody)) {
            row.AddView(CreateInlineHelpBtn(helpTitle, helpBody), new LinearLayout.LayoutParams(Dp(26), Dp(26)) {
                LeftMargin = Dp(8)
            });
        }
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(bottomMarginDp)
        };
        return row;
    }

    TextView SectionTitle(string text) {
        TextView tv = new(this) {
            Text = text,
            TextSize = 15.5f
        };
        tv.SetTextColor(ColText);
        tv.Typeface = OneUiTheme.GetAppleFontBold(this);
        tv.SetPadding(0, 0, 0, Dp(8));
        return tv;
    }

    TextView Label(string text, float sizeSp = 13f, Color? color = null) {
        TextView tv = new(this) {
            Text = text,
            TextSize = sizeSp
        };
        tv.Typeface = OneUiTheme.GetAppleFontRegular(this);
        tv.SetTextColor(color ?? ColText);
        return tv;
    }

    Button PrimaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 13f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(42));
        b.Background = OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp);
        b.SetTextColor(ColText);
        b.Typeface = OneUiTheme.GetAppleFontBold(this);
        b.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button SecondaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 12.5f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(40));
        b.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.ButtonCornerDp);
        b.SetTextColor(ColText);
        b.Typeface = OneUiTheme.GetAppleFontMedium(this);
        b.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button PillButton(string text, Action onClick, bool active = false) {
        Button b = new(this) { Text = text, TextSize = 12f };
        b.SetAllCaps(false);
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(40));
        b.Background = active
            ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        b.SetTextColor(ColText);
        b.Typeface = active ? OneUiTheme.GetAppleFontBold(this) : OneUiTheme.GetAppleFontMedium(this);
        b.SetPadding(Dp(11), Dp(8), Dp(11), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    void AddDivider(LinearLayout parent) {
        parent.AddView(OneUiTheme.CreateDivider(this));
    }

    async Task CheckFFmpegUpdateAsync(bool silent) {
        try {
            RunOnUiThread(() => SetFFmpegStatus(S["CheckingUpdate"], -1));
            FFmpegStatusResult status = await FFmpegInstallerService.CheckStatusAsync(this, store);
            latestFFmpegStatus = status;
            AppLogger.Info($"FFmpeg status: installed={status.IsInstalled}, working={status.IsWorking}, local='{status.LocalPath}', ver='{status.InstalledVersion}'");

            RunOnUiThread(() => {
                if (!string.IsNullOrEmpty(status.LocalPath)) {
                    ffmpeg.Location = status.LocalPath;
                }
                RefreshSettingsDot();
                RefreshSettingsTabButtons();
                RefreshFFmpegCardUi();
                RefreshContainerOptions();
                if (status.IsInstalled && !status.IsWorking) {
                    AppLogger.Warn($"Bundled FFmpeg probe failed at '{status.LocalPath}' (ver={status.InstalledVersion}).");
                } else if (!silent && status.IsInstalled && status.IsWorking) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? "OK" : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["FFmpegUpToDate"], ver), ToastLength.Short).Show();
                } else if (!silent && !status.IsInstalled) {
                    ShowError(S["FFmpegNotInstalled"]);
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckFFmpegUpdateAsync", ex);
            RunOnUiThread(() => {
                RefreshFFmpegCardUi();
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    async Task CheckTruehddUpdateAsync(bool silent) {
        try {
            RunOnUiThread(() => SetTruehddStatus(S["CheckingUpdate"], -1));
            TruehddStatusResult status = await TruehddInstallerService.CheckStatusAsync(this, store);
            latestTruehddStatus = status;
            AppLogger.Info($"truehdd status: installed={status.IsInstalled}, local='{status.LocalPath}', ver='{status.InstalledVersion}'");

            RunOnUiThread(() => {
                if (!string.IsNullOrEmpty(status.LocalPath)) {
                    Truehdd.CustomExecutablePath = status.LocalPath;
                }
                RefreshSettingsDot();
                RefreshSettingsTabButtons();
                RefreshTruehddCardUi();
                if (!silent && status.IsInstalled) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? "OK" : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["TruehddUpToDate"], ver), ToastLength.Short).Show();
                } else if (!silent && !status.IsInstalled) {
                    ShowError(S["TruehddNotInstalled"]);
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckTruehddUpdateAsync", ex);
            RunOnUiThread(() => {
                RefreshTruehddCardUi();
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    async Task CheckForUpdatesAsync(bool silent) {
        try {
            RunOnUiThread(() => SetSettingsUpdateStatus(S["CheckingUpdate"], -1));
            UpdateCheckResult result = await AppUpdateService.CheckForUpdateAsync(this);
            latestUpdateInfo = result;

            RunOnUiThread(() => {
                RefreshSettingsDot();
                RefreshSettingsUpdateUi();
                if (!result.IsUpdateAvailable && !silent) {
                    Toast.MakeText(this, S["UpToDateMsg"], ToastLength.Short).Show();
                }
            });
        } catch (Exception ex) {
            AppLogger.Error("CheckForUpdatesAsync", ex);
            RunOnUiThread(() => {
                SetSettingsUpdateStatus(ex.Message, 0);
                if (!silent) {
                    ShowError(ex.Message);
                }
            });
        }
    }

    void StartUpdateDownloadAndInstall() {
        if (isUpdateDownloading) {
            return;
        }
        if (!string.IsNullOrEmpty(downloadedUpdateApkPath) && File.Exists(downloadedUpdateApkPath)) {
            InstallDownloadedApk(downloadedUpdateApkPath);
            return;
        }
        if (latestUpdateInfo == null || string.IsNullOrEmpty(latestUpdateInfo.DownloadUrl)) {
            _ = CheckForUpdatesAsync(silent: false);
            return;
        }

        isUpdateDownloading = true;
        RunOnUiThread(() => SetSettingsUpdateStatus(S["DownloadingUpdate"], 0));

        string url = latestUpdateInfo.DownloadUrl;
        long expected = latestUpdateInfo.AssetSizeBytes;
        _ = Task.Run(async () => {
            try {
                string apkPath = await AppUpdateService.DownloadUpdateAsync(this, url, expected, (frac, msg) => {
                    RunOnUiThread(() => SetSettingsUpdateStatus(msg, frac));
                });
                downloadedUpdateApkPath = apkPath;
                RunOnUiThread(() => {
                    isUpdateDownloading = false;
                    RefreshSettingsUpdateUi();
                    InstallDownloadedApk(apkPath);
                });
            } catch (Exception ex) {
                AppLogger.Error("StartUpdateDownloadAndInstall", ex);
                RunOnUiThread(() => {
                    isUpdateDownloading = false;
                    SetSettingsUpdateStatus(ex.Message, 0);
                    ShowError(ex.Message);
                });
            }
        });
    }

    void InstallDownloadedApk(string apkPath) {
        if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath)) {
            return;
        }
        if (!AppUpdateService.CanInstallPackages(this)) {
            pendingInstallApkPath = apkPath;
            OneUiModalDialog.Show(
                this,
                S["UpdateCardTitle"],
                S["AllowUnknownSourcesMsg"],
                icon: OneUiIconType.Info,
                positiveText: "OK",
                onPositive: () => AppUpdateService.OpenUnknownSourcesSettings(this),
                negativeText: S["DismissBtn"]);
            return;
        }
        AppUpdateService.LaunchInstaller(this, apkPath);
    }

    void ShowError(string msg) {
        AppLogger.Error($"UI Error shown: {msg}");
        if (uiDestroyed) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            S["ErrorTitle"],
            msg,
            icon: OneUiIconType.Info,
            iconBadgeColor: ColDanger);
    }

    void ShowMessageDialog(string title, string msg, OneUiIconType icon = OneUiIconType.Info, bool monospace = false) {
        if (uiDestroyed) {
            return;
        }
        OneUiModalDialog.Show(
            this,
            title,
            msg,
            icon: icon,
            monospaceBody: monospace);
    }

    /// <summary>
    /// Tab container supporting horizontal swipe navigation between tabs when outside the Play tab
    /// (allows swiping from 3D Room into Play, while disabling swipe away from Play), with strict touch
    /// disambiguation so 3D Room orbit, sliders, and scrollable chips are never intercepted.
    /// </summary>
    sealed class TabSwipeHostLayout : FrameLayout {
        readonly Func<int> getActiveTab;
        readonly Func<int> getTabCount;
        readonly Func<View> getActiveContainer;
        readonly Action<int> switchTab;
        readonly float touchSlop;
        readonly float commitDistPx;
        readonly float flingVelPxPerSec;

        float downRawX;
        float downRawY;
        long downTimeMs;
        bool ignoreGesture;
        bool isSwiping;

        public TabSwipeHostLayout(
            Context ctx,
            Func<int> getActiveTab,
            Func<int> getTabCount,
            Func<View> getActiveContainer,
            Action<int> switchTab) : base(ctx) {
            this.getActiveTab = getActiveTab;
            this.getTabCount = getTabCount;
            this.getActiveContainer = getActiveContainer;
            this.switchTab = switchTab;
            float density = ctx.Resources?.DisplayMetrics?.Density ?? 3f;
            touchSlop = ViewConfiguration.Get(ctx)?.ScaledTouchSlop ?? (int)(8f * density);
            commitDistPx = 56f * density;
            flingVelPxPerSec = 380f * density;
        }

        public override bool OnInterceptTouchEvent(MotionEvent ev) {
            if (ev == null) {
                return false;
            }
            int curTab = getActiveTab();
            // Never allow horizontal swipe away from Play (tab 0)
            if (curTab <= 0) {
                isSwiping = false;
                ignoreGesture = true;
                return false;
            }

            switch (ev.ActionMasked) {
                case MotionEventActions.Down:
                    downRawX = ev.RawX;
                    downRawY = ev.RawY;
                    downTimeMs = SystemClock.UptimeMillis();
                    isSwiping = false;
                    ignoreGesture = IsTouchInsideHorizontalControl(getActiveContainer(), downRawX, downRawY);
                    return false;

                case MotionEventActions.Move:
                    if (ignoreGesture) {
                        return false;
                    }
                    float dx = ev.RawX - downRawX;
                    float dy = ev.RawY - downRawY;
                    if (MathF.Abs(dy) > touchSlop * 1.25f && MathF.Abs(dy) > MathF.Abs(dx)) {
                        ignoreGesture = true;
                        return false;
                    }
                    if (MathF.Abs(dx) > touchSlop * 1.45f && MathF.Abs(dx) > MathF.Abs(dy) * 1.65f) {
                        int count = getTabCount();
                        bool canGoLeft = dx > 0 && curTab > 0;
                        bool canGoRight = dx < 0 && curTab < count - 1;
                        if (canGoLeft || canGoRight) {
                            isSwiping = true;
                            return true;
                        }
                    }
                    return false;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    isSwiping = false;
                    ignoreGesture = false;
                    return false;
            }
            return false;
        }

        public override bool OnTouchEvent(MotionEvent ev) {
            if (ev == null || !isSwiping) {
                return base.OnTouchEvent(ev);
            }
            int curTab = getActiveTab();
            int count = getTabCount();
            View activeView = getActiveContainer();
            float dx = ev.RawX - downRawX;

            switch (ev.ActionMasked) {
                case MotionEventActions.Move:
                    if (activeView != null) {
                        bool allowed = (dx > 0 && curTab > 0) || (dx < 0 && curTab < count - 1);
                        activeView.TranslationX = allowed ? dx * 0.24f : dx * 0.06f;
                    }
                    return true;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    isSwiping = false;
                    long dt = Math.Max(16, SystemClock.UptimeMillis() - downTimeMs);
                    float vel = MathF.Abs(dx) * 1000f / dt;
                    bool commit = ev.ActionMasked == MotionEventActions.Up &&
                                  (MathF.Abs(dx) >= commitDistPx || (MathF.Abs(dx) >= touchSlop * 2f && vel >= flingVelPxPerSec));
                    int target = curTab;
                    if (commit) {
                        if (dx > 0 && curTab > 0) {
                            target = curTab - 1;
                        } else if (dx < 0 && curTab < count - 1) {
                            target = curTab + 1;
                        }
                    }
                    if (target != curTab) {
                        switchTab(target);
                    } else if (activeView != null) {
                        activeView.Animate()
                            .TranslationX(0f)
                            .SetDuration(200)
                            .SetInterpolator(CreateOneUiEase())
                            .Start();
                    }
                    return true;
            }
            return true;
        }

        static bool IsTouchInsideHorizontalControl(View root, float rawX, float rawY) {
            if (root == null || root.Visibility != ViewStates.Visible) {
                return false;
            }
            int[] loc = new int[2];
            root.GetLocationOnScreen(loc);
            if (rawX < loc[0] || rawX > loc[0] + root.Width || rawY < loc[1] || rawY > loc[1] + root.Height) {
                return false;
            }
            if (root is RoomSimulatorView or OneUiSeekBar or OneUiWaveMediaSeekBar or EditText) {
                return true;
            }
            if (root is HorizontalScrollView hsv && (hsv.CanScrollHorizontally(-1) || hsv.CanScrollHorizontally(1))) {
                return true;
            }
            if (root is ViewGroup vg) {
                for (int i = vg.ChildCount - 1; i >= 0; i--) {
                    if (IsTouchInsideHorizontalControl(vg.GetChildAt(i), rawX, rawY)) {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
