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
    const int ReqLocateFFmpeg = 1005;
    const int ReqLocateTruehdd = 1006;
    const int ReqPermissions = 1007;

    AppSettingsStore store;
    AndroidUpmixingSettings upmixingSettings;
    AndroidRenderingSettings renderingSettings;
    AndroidFFmpegRunner ffmpeg;
    Cavernize.Logic.Models.Queue renderQueue;
    ConversionEnvironment environment;
    PostRenderReport report;
    RealtimePlayerEngine playerEngine;
    AndroidAudioOutput audioOutput;

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

    // Minimalist Compact Top Bar Views + Collapsible Stream Details Drawer
    LinearLayout filePillBtn;
    TextView headerFileText;
    LinearLayout fileDetailsDrawer;
    TextView trackSummaryText;
    LinearLayout trackPickerBadge;
    TextView trackPickerLabel;
    OneUiIconView fileDetailsChevron;
    LinearLayout inlineTrackList;
    bool fileDetailsExpanded;
    MediaPlaybackNotificationManager mediaNotification;
    MediaArtworkMetadata currentMediaMeta;
    Bitmap defaultFallbackCover;

    UpdateCheckResult latestUpdateInfo;
    FFmpegStatusResult latestFFmpegStatus;
    TruehddStatusResult latestTruehddStatus;
    string downloadedUpdateApkPath;
    string pendingInstallApkPath;
    bool isUpdateDownloading;
    bool isFFmpegDownloading;
    bool isTruehddDownloading;

    ExternalDecodedTrack decodedTrueHDTrack;
    string activeTrueHDSessionDir;
    string activeFallbackSessionDir;
    readonly HashSet<string> protectedQueuePaths = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource truehdPrepareCts;
    volatile bool isPreparingTrueHD;
    volatile bool suppressAutoTrueHDPrepare;

    OneUiBackdropView backdropView;
    OneUiFrostedCapsuleView navFrostedHost;
    View tabIndicatorPill;
    ValueAnimator tabPillAnimator;
    LinearLayout[] tabPages;
    Button[] tabButtons;
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

        if (Window != null) {
            Window.DecorView?.SetBackgroundColor(ColBg);
#pragma warning disable CS0618, CA1422
            Window.SetStatusBarColor(ColBg);
            Window.SetNavigationBarColor(ColBg);
#pragma warning restore CS0618, CA1422
        }

        store = new AppSettingsStore(this);
        store.ApplyGlobals();
        upmixingSettings = new AndroidUpmixingSettings(store);
        renderingSettings = new AndroidRenderingSettings(store);
        ffmpeg = new AndroidFFmpegRunner(_ => { });
        string resolvedFfmpeg = FFmpegInstallerService.ResolveExistingFFmpegPath(this, store);
        if (!string.IsNullOrEmpty(resolvedFfmpeg)) {
            FFmpegInstallerService.EnsureExecutable(resolvedFfmpeg);
            ffmpeg.Location = resolvedFfmpeg;
            AppLogger.Info($"FFmpeg resolved at '{resolvedFfmpeg}' (Found={ffmpeg.Found})");
        }

        string resolvedTruehdd = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        if (!string.IsNullOrEmpty(resolvedTruehdd)) {
            TruehddInstallerService.EnsureExecutable(resolvedTruehdd);
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
            AudioQuality = (QualityModes)Math.Clamp(store.AudioQualityIndex, 0, 3)
        };
        playerEngine.PlaybackCompleted += () => RunOnUiThread(OnPlayerCompleted);
        audioOutput = new AndroidAudioOutput(playerEngine);

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
        mediaNotification.Rewind10Requested += () => SeekRelative(-10);
        mediaNotification.Forward10Requested += () => SeekRelative(10);
        mediaNotification.SeekRequestedMs += ms => SeekAbsoluteSeconds(ms / 1000.0);

        if (!string.IsNullOrEmpty(store.HrirPath) && File.Exists(store.HrirPath)) {
            TryLoadHrir(store.HrirPath, showError: false);
        }

        uiHandler = new Handler(Looper.MainLooper);
        BuildRootUi();
        RequestMediaPermissionsIfNeeded();
        StartTelemetryTimer();

        if (Intent != null) {
            HandleIncomingIntent(Intent);
        }
        _ = CheckFFmpegUpdateAsync(silent: true);
        _ = CheckTruehddUpdateAsync(silent: true);
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
        activeCts?.Cancel();
        activeCts?.Dispose();
        truehdPrepareCts?.Cancel();
        truehdPrepareCts?.Dispose();
        tabPillAnimator?.Cancel();
        tabPillAnimator?.Dispose();
        mediaNotification?.Dispose();
        currentMediaMeta?.ArtworkBitmap?.Dispose();
        defaultFallbackCover?.Dispose();
        audioOutput?.Dispose();
        playerEngine?.Dispose();
        decodedTrueHDTrack?.Dispose();
        LoadedFile?.Dispose();
        TrueHDDecoderService.CleanupWorkDirectory(activeTrueHDSessionDir);
        TrueHDDecoderService.CleanupWorkDirectory(activeFallbackSessionDir);
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
        rootFrame.SetBackgroundColor(ColBg);

        // Layer 0: Atmospheric blurred One UI 9 depth backdrop (also breathes with loaded album art palette)
        backdropView = new OneUiBackdropView(this);
        rootFrame.AddView(backdropView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Layer 1: Main content column
        LinearLayout root = new(this) {
            Orientation = Orientation.Vertical
        };
        root.SetBackgroundColor(Color.Transparent);
        root.SetFitsSystemWindows(true);

        // Minimalist One UI 9 Compact Header Bar (title + frosted file pill + Open action; technical stream details stay hidden until tapped)
        LinearLayout topBarWrap = new(this) { Orientation = Orientation.Vertical };
        topBarWrap.SetPadding(Dp(14), Dp(8), Dp(14), Dp(6));

        LinearLayout topRow = HRow();

        TextView titleTv = new(this) {
            Text = "Cavernize",
            TextSize = 18f
        };
        titleTv.SetIncludeFontPadding(false);
        titleTv.SetTextColor(ColText);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);
        topRow.AddView(titleTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) });

        filePillBtn = HRow();
        filePillBtn.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, OneUiTheme.PillCornerDp);
        filePillBtn.SetPadding(Dp(11), Dp(7), Dp(11), Dp(7));
        OneUiTheme.AttachRecoil(filePillBtn, 0.97f);

        OneUiIconView fileNoteIcon = new(this, OneUiIconType.MusicFile, OneUiTheme.PrimaryHigh);
        headerFileText = new TextView(this) {
            Text = S["SelectFilePill"],
            TextSize = 12f
        };
        headerFileText.SetIncludeFontPadding(false);
        headerFileText.SetTextColor(OneUiTheme.TextSecondary);
        headerFileText.SetSingleLine(true);
        headerFileText.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;

        trackPickerBadge = HRow();
        trackPickerBadge.Visibility = ViewStates.Gone;
        trackPickerBadge.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 195), OneUiTheme.PillCornerDp);
        trackPickerBadge.SetPadding(Dp(7), Dp(2), Dp(7), Dp(2));
        trackPickerLabel = Label("", 10.5f, ColText);
        trackPickerLabel.SetIncludeFontPadding(false);
        trackPickerLabel.SetTypeface(null, TypefaceStyle.Bold);
        trackPickerBadge.AddView(trackPickerLabel);

        fileDetailsChevron = new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.TextSub) {
            Visibility = ViewStates.Gone
        };

        filePillBtn.AddView(fileNoteIcon, new LinearLayout.LayoutParams(Dp(14), Dp(14)) { RightMargin = Dp(6) });
        filePillBtn.AddView(headerFileText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        filePillBtn.AddView(trackPickerBadge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(4) });
        filePillBtn.AddView(fileDetailsChevron, new LinearLayout.LayoutParams(Dp(13), Dp(13)));
        filePillBtn.Click += (_, _) => {
            if (LoadedFile == null) {
                LaunchFilePicker(ReqOpenMedia, allowMultiple: false);
            } else {
                fileDetailsExpanded = !fileDetailsExpanded;
                UpdateInlineTrackListVisibility();
            }
        };
        topRow.AddView(filePillBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        LinearLayout openBtn = OneUiPillFactory.CreateIconActionPill(
            this, OneUiIconType.Folder, S["OpenShort"],
            () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false),
            primary: true);
        openBtn.SetPadding(Dp(11), Dp(7), Dp(13), Dp(7));
        topRow.AddView(openBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        topBarWrap.AddView(topRow);

        fileDetailsDrawer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        fileDetailsDrawer.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 20f);
        fileDetailsDrawer.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));

        trackSummaryText = Label("", 11.5f, OneUiTheme.TextSub);
        fileDetailsDrawer.AddView(trackSummaryText);

        inlineTrackList = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        inlineTrackList.SetPadding(0, Dp(8), 0, 0);
        fileDetailsDrawer.AddView(inlineTrackList);

        topBarWrap.AddView(fileDetailsDrawer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6) });
        root.AddView(topBarWrap, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Tab content frame
        FrameLayout contentFrame = new(this);
        LinearLayout musicTab = BuildMusicPlayerTab();
        LinearLayout playerTab = BuildPlayerTab();
        LinearLayout convertTab = BuildConverterTab();
        LinearLayout extractTab = BuildExtractorTab();
        LinearLayout settingsTab = BuildSettingsTab();
        tabPages = [musicTab, playerTab, convertTab, extractTab, settingsTab];

        for (int i = 0; i < tabPages.Length; i++) {
            ScrollView sv = new(this) {
                FillViewport = true,
                VerticalScrollBarEnabled = true
            };
            sv.SetClipToPadding(false);
            sv.SetBackgroundColor(Color.Transparent);
            sv.ScrollChange += (_, _) => navFrostedHost?.RefreshBlur();
            sv.AddView(tabPages[i]);
            sv.Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone;
            contentFrame.AddView(sv, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }
        root.AddView(contentFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        rootFrame.AddView(root, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Bottom edge fade scrim so scrolled cards recede smoothly behind the frosted capsule
        View bottomScrim = new(this) {
            Background = new Android.Graphics.Drawables.GradientDrawable(
                Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom,
                [Color.Transparent.ToArgb(), Color.Argb(205, ColBg.R, ColBg.G, ColBg.B).ToArgb()])
        };
        rootFrame.AddView(bottomScrim, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(98)) {
            Gravity = GravityFlags.Bottom
        });

        // Layer 2: Live Frosted Glass Capsule Bottom Navigation Bar + Sliding Pill Indicator
        navFrostedHost = new OneUiFrostedCapsuleView(this) {
            CornerRadiusDp = 30f,
            BehindViewsProvider = () => [backdropView, contentFrame],
            Elevation = Dp(14)
        };
        navFrostedHost.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));

        FrameLayout navLayer = new(this);
        tabIndicatorPill = new View(this) {
            Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 232), OneUiTheme.PillCornerDp)
        };
        navLayer.AddView(tabIndicatorPill, new FrameLayout.LayoutParams(Dp(56), Dp(40)) {
            Gravity = GravityFlags.CenterVertical | GravityFlags.Start
        });

        LinearLayout navRow = HRow();
        string[] tabTitles = [S["TabMusicPlayer"], S["TabPlayer"], S["TabConvert"], S["TabExtract"], S["TabSettings"]];
        tabButtons = new Button[5];
        for (int i = 0; i < 5; i++) {
            int idx = i;
            Button tb = new(this) {
                Text = tabTitles[i],
                TextSize = 11.5f
            };
            tb.SetAllCaps(false);
            tb.SetIncludeFontPadding(false);
            tb.SetSingleLine(true);
            tb.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            tb.SetMinimumHeight(Dp(40));
            tb.SetMinimumWidth(0);
            tb.SetPadding(Dp(2), Dp(6), Dp(2), Dp(6));
            tb.Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp);
            OneUiTheme.AttachRecoil(tb, 0.94f);
            tb.Click += (_, _) => SwitchTab(idx);
            tabButtons[i] = tb;

            if (i == 4) {
                FrameLayout tabCell = new(this);
                tabCell.AddView(tb, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

                settingsUpdateDot = new View(this);
                settingsUpdateDot.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.FunctionalOrange, 99f);
                settingsUpdateDot.Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
                FrameLayout.LayoutParams dotLp = new(Dp(7), Dp(7)) {
                    Gravity = GravityFlags.Top | GravityFlags.End,
                    TopMargin = Dp(6),
                    RightMargin = Dp(7)
                };
                tabCell.AddView(settingsUpdateDot, dotLp);
                navRow.AddView(tabCell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            } else {
                navRow.AddView(tb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }
        }
        navLayer.AddView(navRow, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        navFrostedHost.AddView(navLayer);

        FrameLayout.LayoutParams floatingNavLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(14),
            RightMargin = Dp(14),
            BottomMargin = Dp(14)
        };
        rootFrame.AddView(navFrostedHost, floatingNavLp);

        SetContentView(rootFrame);
        UpdateTabButtonStyles(animate: false);
        navFrostedHost.Post(() => navFrostedHost?.RefreshBlur(force: true));
        RefreshAllUiState();
    }

    void RebuildInlineTrackList() {
        if (inlineTrackList == null || trackPickerBadge == null) {
            return;
        }
        inlineTrackList.RemoveAllViews();
        if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
            trackPickerBadge.Visibility = ViewStates.Gone;
            inlineTrackList.Visibility = ViewStates.Gone;
            UpdateInlineTrackListVisibility();
            return;
        }

        int selIdx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
        trackPickerBadge.Visibility = ViewStates.Visible;
        trackPickerLabel.Text = $"{selIdx + 1}/{LoadedFile.Tracks.Count}";

        TextView hdr = Label(S["SelectTrackHeader"], 11.5f, ColMuted);
        hdr.SetPadding(Dp(2), 0, 0, Dp(6));
        inlineTrackList.AddView(hdr);

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
            inlineTrackList.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(4)
            });
        }
        UpdateInlineTrackListVisibility();
    }

    void UpdateInlineTrackListVisibility() {
        if (fileDetailsDrawer == null || fileDetailsChevron == null) {
            return;
        }
        bool hasFile = LoadedFile != null;
        fileDetailsChevron.Visibility = hasFile ? ViewStates.Visible : ViewStates.Gone;
        bool showDrawer = fileDetailsExpanded && hasFile;
        SetExpandedWithAnimation(fileDetailsDrawer, showDrawer);
        if (inlineTrackList != null) {
            inlineTrackList.Visibility = (showDrawer && LoadedFile.Tracks.Count > 1) ? ViewStates.Visible : ViewStates.Gone;
        }
        fileDetailsChevron.IconType = showDrawer ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
    }

    void SetExpandedWithAnimation(View panel, bool expanded) {
        if (panel == null) {
            return;
        }
        panel.Animate()?.Cancel();
        if (expanded) {
            if (panel.Visibility != ViewStates.Visible) {
                panel.Visibility = ViewStates.Visible;
                panel.Alpha = 0f;
                panel.TranslationY = -Dp(6);
            }
            panel.Animate()
                .Alpha(1f)
                .TranslationY(0f)
                .SetDuration(190)
                .SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.5f))
                .Start();
        } else {
            panel.Visibility = ViewStates.Gone;
            panel.Alpha = 1f;
            panel.TranslationY = 0f;
        }
    }

    bool ShouldShowSettingsDot() =>
        latestUpdateInfo?.IsUpdateAvailable == true ||
        latestFFmpegStatus?.IsUpdateAvailable == true ||
        latestTruehddStatus?.IsUpdateAvailable == true;

    void SwitchTab(int idx) {
        if (tabPages == null || idx < 0 || idx >= tabPages.Length) {
            return;
        }
        int prevTab = activeTab;
        activeTab = idx;
        if (prevTab != idx) {
            int dir = idx > prevTab ? 1 : -1;
            for (int i = 0; i < tabPages.Length; i++) {
                if (tabPages[i].Parent is not View sv) {
                    continue;
                }
                sv.Animate()?.Cancel();
                if (i == idx) {
                    sv.Visibility = ViewStates.Visible;
                    sv.Alpha = 0f;
                    sv.TranslationX = dir * Dp(20);
                    sv.Animate()
                        .Alpha(1f)
                        .TranslationX(0f)
                        .SetDuration(220)
                        .SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.6f))
                        .WithEndAction(new Java.Lang.Runnable(() => navFrostedHost?.RefreshBlur(force: true)))
                        .Start();
                } else {
                    sv.Visibility = ViewStates.Gone;
                    sv.Alpha = 1f;
                    sv.TranslationX = 0f;
                }
            }
        } else if (tabPages[idx].Parent is View svCur) {
            svCur.Visibility = ViewStates.Visible;
            svCur.Alpha = 1f;
            svCur.TranslationX = 0f;
        }
        UpdateTabButtonStyles(animate: true);
        if (idx == 1) {
            UpdateRoomCanvas();
        } else if (idx == 0) {
            RefreshMusicPlayerUi();
        }
        navFrostedHost?.RefreshBlur(force: true);
    }

    void UpdateTabButtonStyles(bool animate = true) {
        if (tabButtons == null || tabButtons.Length == 0) {
            return;
        }
        for (int i = 0; i < tabButtons.Length; i++) {
            bool active = i == activeTab;
            tabButtons[i].SetTextColor(active ? ColText : OneUiTheme.TextSecondary);
            tabButtons[i].SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        PositionTabIndicator(animate);
    }

    void PositionTabIndicator(bool animate) {
        if (tabIndicatorPill == null || tabButtons == null || activeTab < 0 || activeTab >= tabButtons.Length) {
            return;
        }
        View targetHost = activeTab == 4 && tabButtons[4].Parent is View parentCell
            ? parentCell
            : tabButtons[activeTab];
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
        tabPillAnimator.SetDuration(250);
        tabPillAnimator.SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.7f));
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
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) {
            if (CheckSelfPermission(Manifest.Permission.ReadMediaAudio) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadMediaAudio);
            }
            if (CheckSelfPermission(Manifest.Permission.ReadMediaVideo) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadMediaVideo);
            }
            if (CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) {
                needed.Add(Manifest.Permission.PostNotifications);
            }
        } else {
            if (CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.ReadExternalStorage);
            }
            if (Build.VERSION.SdkInt <= BuildVersionCodes.Q &&
                CheckSelfPermission(Manifest.Permission.WriteExternalStorage) != Permission.Granted) {
                needed.Add(Manifest.Permission.WriteExternalStorage);
            }
        }
        if (needed.Count > 0) {
            RequestPermissions(needed.ToArray(), ReqPermissions);
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
                RunOnUiThread(() => {
                    if (TryLoadHrir(path, showError: true)) {
                        store.HrirPath = path;
                        RefreshSettingsTabButtons();
                    }
                });
            });
        } else if (requestCode == ReqLoadFilters && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                RunOnUiThread(() => TryLoadRoomFilters(path));
            });
        } else if (requestCode == ReqLocateFFmpeg && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                FFmpegInstallerService.EnsureExecutable(path);
                string ver = FFmpegInstallerService.ProbeFFmpegVersion(path);
                AppLogger.Info($"Manual FFmpeg selected: '{path}' (ver={ver})");
                RunOnUiThread(() => {
                    store.FFmpegLocation = path;
                    if (!string.IsNullOrEmpty(ver)) {
                        store.InstalledFFmpegVersion = ver;
                    }
                    ffmpeg.Location = path;
                    RefreshSettingsTabButtons();
                    RefreshFFmpegCardUi();
                });
            });
        } else if (requestCode == ReqLocateTruehdd && data.Data != null) {
            Task.Run(() => {
                string path = AndroidFileHelper.ResolveReadablePath(this, data.Data);
                TruehddInstallerService.EnsureExecutable(path);
                string ver = TruehddInstallerService.ProbeTruehddVersion(path);
                AppLogger.Info($"Manual truehdd selected: '{path}' (ver={ver})");
                RunOnUiThread(() => {
                    store.TruehddLocation = path;
                    if (!string.IsNullOrEmpty(ver)) {
                        store.InstalledTruehddVersion = ver;
                    }
                    Truehdd.CustomExecutablePath = path;
                    RefreshSettingsTabButtons();
                    RefreshTruehddCardUi();
                });
            });
        }
    }

    void LoadUriAsync(Android.Net.Uri uri) {
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        AppLogger.Info($"Loading URI: {uri}");
        SetConvertStatus(S["CopyingMedia"], -1);
        headerFileText.Text = S["CopyingMedia"];
        Task.Run(() => {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(this, uri,
                    (p, name) => RunOnUiThread(() => {
                        string msg = $"{S["CopyingMedia"]} ({name}: {p:0%})";
                        headerFileText.Text = msg;
                        SetConvertStatus(msg, p);
                    }));
                AppLogger.Info($"Resolved file path: '{resolved}'");

                MediaArtworkMetadata extractedMeta = MediaMetadataHelper.Extract(
                    this, uri, resolved, Path.GetFileName(resolved));

                string newFallbackSessionDir = null;
                AudioFile file = null;
                try {
                    file = new AudioFile(resolved);
                    if (file.Tracks.Count == 0 || file.Tracks.All(t => !t.Supported)) {
                        file.Dispose();
                        file = null;
                        throw new NotSupportedException("Fallback required for container/codec");
                    }
                } catch (Exception nativeEx) {
                    AppLogger.Info($"Native Cavern reader fallback ({nativeEx.GetType().Name}): inspecting container/codec...");
                    file = TryOpenWithSmartFallbacks(resolved, out newFallbackSessionDir);
                }

                RunOnUiThread(() => {
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
                    headerFileText.Text = LoadedFile != null ? Path.GetFileName(LoadedFile.Path) : S["SelectFilePill"];
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
                });
            }
        });
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
                headerFileText.Text = S["ProbingContainer"];
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
                        headerFileText.Text = S["ExtractingTrueHD"];
                        SetConvertStatus(S["ExtractingTrueHD"], -1);
                    });
                    string rawThd = Path.Combine(workDir, "extracted.thd");
                    bool ok = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                        ffmpeg.Location, resolved, spatialStream.StreamIndex, rawThd, "truehd",
                        probe.DurationSeconds,
                        (p, msg) => RunOnUiThread(() => {
                            headerFileText.Text = msg;
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
                            headerFileText.Text = msg;
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

        // Standard Android MediaCodec fallback (AAC, MP3, FLAC, OGG, Opus, etc.)
        RunOnUiThread(() => {
            headerFileText.Text = S["DecodingFallback"];
            SetConvertStatus(S["DecodingFallback"], -1);
        });
        string wavCacheDir = Path.Combine(CacheDir.AbsolutePath, "decoded_fallback");
        try {
            string decodedWav = AndroidMediaFallbackDecoder.DecodeToWav(resolved, wavCacheDir,
                p => RunOnUiThread(() => {
                    string msg = $"{S["DecodingFallback"]} ({p:0%})";
                    headerFileText.Text = msg;
                    SetConvertStatus(msg, p);
                }));
            return new AudioFile(decodedWav);
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
                    headerFileText.Text = "FFmpeg: decodificando áudio...";
                    SetConvertStatus("FFmpeg: decodificando áudio...", -1);
                });
                bool wavOk = TrueHDDecoderService.ExtractAudioStreamWithFFmpeg(
                    ffmpeg.Location, resolved, firstAudio.StreamIndex, outWav, "wav",
                    probe.DurationSeconds,
                    (p, msg) => RunOnUiThread(() => {
                        headerFileText.Text = msg;
                        SetConvertStatus(msg, p);
                    }));
                if (wavOk) {
                    AudioReader wavReader = AudioReader.Open(outWav);
                    ExternalDecodedTrack wavTrack = new(
                        wavReader, wavReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE,
                        Codec.PCM_LE, null, guessedContainer, null, null, firstAudio.Language);
                    return AudioFile.FromCustomTracks(resolved, [wavTrack]);
                }
            }

            if (!ffmpeg.Found && containerExt is ".mkv" or ".mka" or ".m2ts" or ".ts" or ".mp4" or ".thd" or ".mlp" or ".truehd") {
                throw new NotSupportedException(S["TrueHDContainerNeedTools"], mediaEx);
            }
            throw;
        }
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
        AudioFile file;
        string fallbackDir = null;
        try {
            file = new AudioFile(path);
            if (file.Tracks.Count == 0 || file.Tracks.All(t => !t.Supported)) {
                file.Dispose();
                throw new NotSupportedException("Fallback required for container/codec");
            }
        } catch {
            file = TryOpenWithSmartFallbacks(path, out fallbackDir);
        }
        if (!string.IsNullOrEmpty(fallbackDir)) {
            if (suppressAutoTrueHDPrepare) {
                protectedQueuePaths.Add(Path.GetFullPath(fallbackDir));
            } else {
                activeFallbackSessionDir = fallbackDir;
            }
        }
        OpenContent(file, meta);
    }

    void OpenLocalPathWithFallbackAsync(string path, int? switchToTab = null) {
        if (isOperationRunning || isPreparingTrueHD) {
            ShowError(S["OpRunning"]);
            return;
        }
        SetConvertStatus(S["CopyingMedia"], -1);
        Task.Run(() => {
            try {
                MediaArtworkMetadata meta = MediaMetadataHelper.Extract(this, null, path, Path.GetFileName(path));
                string newFallbackSessionDir = null;
                AudioFile file;
                try {
                    file = new AudioFile(path);
                    if (file.Tracks.Count == 0 || file.Tracks.All(t => !t.Supported)) {
                        file.Dispose();
                        throw new NotSupportedException("Fallback required for container/codec");
                    }
                } catch {
                    file = TryOpenWithSmartFallbacks(path, out newFallbackSessionDir);
                }
                RunOnUiThread(() => {
                    string oldFallback = activeFallbackSessionDir;
                    activeFallbackSessionDir = newFallbackSessionDir;
                    OpenContent(file, meta);
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
                AppLogger.Error("OpenLocalPathWithFallbackAsync", ex);
                RunOnUiThread(() => {
                    SetConvertStatus(ex.Message, 0);
                    ShowLoadFailureDialog(ex);
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
        headerFileText.Text = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
            ? currentMediaMeta.Title
            : Path.GetFileName(file.Path);
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

        if (!ReferenceEquals(prevMeta, currentMediaMeta)) {
            prevMeta?.ArtworkBitmap?.Dispose();
        }
        RefreshMusicPlayerUi();
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
        selectedTrack = null;
        trackSourceItems = [];
        fileDetailsExpanded = false;
        if (headerFileText != null) {
            headerFileText.Text = S["SelectFilePill"];
        }
        if (trackSummaryText != null) {
            trackSummaryText.Text = "";
        }
        mediaNotification?.CancelNotification();
        RefreshMusicPlayerUi();
        oldMeta?.ArtworkBitmap?.Dispose();
        RebuildInlineTrackList();
        report = new PostRenderReport(environment.Listener);
    }

    void OnSelectedTrackChanged() {
        StopPlayback();
        ClearDecodedTrueHDState();
        if (selectedTrack == null) {
            fileDetailsExpanded = false;
            if (trackSummaryText != null) {
                trackSummaryText.Text = "";
            }
            trackSourceItems = [];
            mediaNotification?.CancelNotification();
            RefreshPlayerSourcesList();
            RefreshMusicPlayerUi();
            RefreshExtractorChecklist();
            RefreshConverterTrackInfo();
            RebuildInlineTrackList();
            return;
        }

        if (selectedTrack.Codec == Codec.TrueHD) {
            AppLogger.Info($"Selected TrueHD track: {selectedTrack.FormatHeader}");
            RebuildInlineTrackList();
            RefreshConverterTrackInfo();
            RefreshContainerOptions();
            RefreshMusicPlayerUi();
            if (!suppressAutoTrueHDPrepare) {
                PrepareSelectedTrueHDTrackAsync(selectedTrack, autoPlayAfter: false);
            }
            return;
        }

        BindActiveTrackToUiAndPlayer(selectedTrack);
    }

    void BindActiveTrackToUiAndPlayer(CavernizeTrack activeTrack) {
        if (activeTrack == null) {
            return;
        }
        AppLogger.Info($"Active track ready: {activeTrack.FormatHeader}");
        string details = string.Join(" | ", activeTrack.Details.Select(d => $"{d.property}: {d.value}"));
        if (trackSummaryText != null) {
            trackSummaryText.Text = $"{activeTrack.FormatHeader}\n{details}";
        }
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
        SyncPlaybackUi(false, 0, playerEngine.TotalSeconds);
        UpdateRoomCanvas();
    }

    void PrepareSelectedTrueHDTrackAsync(CavernizeTrack sourceTrack, bool autoPlayAfter) {
        if (sourceTrack == null || isPreparingTrueHD || uiDestroyed) {
            return;
        }
        string existingBin = TruehddInstallerService.ResolveExistingTruehddPath(this, store);
        bool needsDownload = string.IsNullOrEmpty(existingBin) || !File.Exists(existingBin);

        if (needsDownload || !store.TruehddLicenceAccepted) {
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
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: null,
            negativeText: S["CancelBtn"],
            onNegative: () => truehdPrepareCts?.Cancel(),
            cancelable: false);

        void UpdatePrepUi(double p, string msg) {
            RunOnUiThread(() => {
                if (uiDestroyed) return;
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
                    UpdatePrepUi(-1, S["TruehddDownloading"]);
                    TruehddStatusResult status = await TruehddInstallerService.CheckStatusAsync(this, store, token).ConfigureAwait(false);
                    truehddPath = await TruehddInstallerService.DownloadAndInstallAsync(
                        this, store, status,
                        (frac, msg) => UpdatePrepUi(frac * 0.25, msg),
                        token).ConfigureAwait(false);
                    latestTruehddStatus = await TruehddInstallerService.CheckStatusAsync(this, store, token).ConfigureAwait(false);
                    RunOnUiThread(() => {
                        RefreshSettingsDot();
                        RefreshTruehddCardUi();
                    });
                }

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
                    isPreparingTrueHD = false;
                    try { progressDlg?.Dismiss(); } catch { }
                    if (uiDestroyed || selectedTrack != sourceTrack) {
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
                    isPreparingTrueHD = false;
                    try { progressDlg?.Dismiss(); } catch { }
                    SetConvertStatus(S["TrueHDCancelled"], 0);
                    BindActiveTrackToUiAndPlayer(sourceTrack);
                });
            } catch (Exception ex) {
                AppLogger.Error("PrepareSelectedTrueHDTrackAsync", ex);
                RunOnUiThread(() => {
                    isPreparingTrueHD = false;
                    try { progressDlg?.Dismiss(); } catch { }
                    SetConvertStatus(ex.Message, 0);
                    BindActiveTrackToUiAndPlayer(sourceTrack);
                    ShowError(ex.Message);
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
        if (settingsUpdateDot != null) {
            settingsUpdateDot.Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
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
        tv.SetTypeface(null, TypefaceStyle.Bold);
        tv.SetPadding(0, 0, 0, Dp(8));
        return tv;
    }

    TextView Label(string text, float sizeSp = 13f, Color? color = null) {
        TextView tv = new(this) {
            Text = text,
            TextSize = sizeSp
        };
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
        b.SetTypeface(null, TypefaceStyle.Bold);
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
        b.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
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
            AppLogger.Info($"FFmpeg status: installed={status.IsInstalled}, local='{status.LocalPath}', ver='{status.InstalledVersion}', remote='{status.RemoteVersion}'");

            RunOnUiThread(() => {
                RefreshSettingsDot();
                RefreshFFmpegCardUi();
                if (!silent && !status.IsUpdateAvailable && status.IsInstalled) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? status.RemoteVersion : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["FFmpegUpToDate"], ver), ToastLength.Short).Show();
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

    void StartFFmpegDownloadOrUpdate() {
        if (isFFmpegDownloading) {
            return;
        }
        isFFmpegDownloading = true;
        RunOnUiThread(() => SetFFmpegStatus(S["FFmpegDownloading"], 0));

        _ = Task.Run(async () => {
            try {
                string path = await FFmpegInstallerService.DownloadAndInstallAsync(
                    this, store, latestFFmpegStatus,
                    (frac, msg) => RunOnUiThread(() => SetFFmpegStatus(msg, frac)));

                FFmpegStatusResult refreshed = await FFmpegInstallerService.CheckStatusAsync(this, store);
                latestFFmpegStatus = refreshed;
                AppLogger.Info($"FFmpeg installed at '{path}'");

                RunOnUiThread(() => {
                    isFFmpegDownloading = false;
                    ffmpeg.Location = path;
                    RefreshSettingsDot();
                    RefreshSettingsTabButtons();
                    RefreshFFmpegCardUi();
                    RefreshContainerOptions();
                    Toast.MakeText(this, S["Ready"], ToastLength.Short).Show();
                });
            } catch (Exception ex) {
                AppLogger.Error("StartFFmpegDownloadOrUpdate", ex);
                RunOnUiThread(() => {
                    isFFmpegDownloading = false;
                    SetFFmpegStatus(ex.Message, 0);
                    RefreshFFmpegCardUi();
                    ShowError(ex.Message);
                });
            }
        });
    }

    async Task CheckTruehddUpdateAsync(bool silent) {
        try {
            RunOnUiThread(() => SetTruehddStatus(S["CheckingUpdate"], -1));
            TruehddStatusResult status = await TruehddInstallerService.CheckStatusAsync(this, store);
            latestTruehddStatus = status;
            AppLogger.Info($"truehdd status: installed={status.IsInstalled}, local='{status.LocalPath}', ver='{status.InstalledVersion}', remote='{status.RemoteVersion}'");

            RunOnUiThread(() => {
                RefreshSettingsDot();
                RefreshTruehddCardUi();
                if (!silent && !status.IsUpdateAvailable && status.IsInstalled) {
                    string ver = string.IsNullOrEmpty(status.InstalledVersion) ? status.RemoteVersion : status.InstalledVersion;
                    Toast.MakeText(this, string.Format(S["TruehddUpToDate"], ver), ToastLength.Short).Show();
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

    void StartTruehddDownloadOrUpdate() {
        if (isTruehddDownloading) {
            return;
        }
        if (!store.TruehddLicenceAccepted) {
            _ = Task.Run(async () => {
                string licText = await TruehddInstallerService.FetchLicenceTextAsync(this).ConfigureAwait(false);
                RunOnUiThread(() => {
                    OneUiModalDialog.Show(
                        this,
                        S["TrueHDLicenceTitle"],
                        $"{S["TrueHDLicenceDesc"]}\n\n{licText}",
                        icon: OneUiIconType.Sparkles,
                        iconBadgeColor: OneUiTheme.Primary,
                        positiveText: S["TrueHDDownloadDecode"],
                        onPositive: () => {
                            store.TruehddLicenceAccepted = true;
                            LaunchTruehddDownloadWorker();
                        },
                        negativeText: S["CancelBtn"]);
                });
            });
            return;
        }
        LaunchTruehddDownloadWorker();
    }

    void LaunchTruehddDownloadWorker() {
        if (isTruehddDownloading) {
            return;
        }
        isTruehddDownloading = true;
        RunOnUiThread(() => SetTruehddStatus(S["TruehddDownloading"], 0));

        _ = Task.Run(async () => {
            try {
                string path = await TruehddInstallerService.DownloadAndInstallAsync(
                    this, store, latestTruehddStatus,
                    (frac, msg) => RunOnUiThread(() => SetTruehddStatus(msg, frac)));

                TruehddStatusResult refreshed = await TruehddInstallerService.CheckStatusAsync(this, store);
                latestTruehddStatus = refreshed;
                AppLogger.Info($"truehdd installed at '{path}'");

                RunOnUiThread(() => {
                    isTruehddDownloading = false;
                    Truehdd.CustomExecutablePath = path;
                    RefreshSettingsDot();
                    RefreshSettingsTabButtons();
                    RefreshTruehddCardUi();
                    Toast.MakeText(this, S["Ready"], ToastLength.Short).Show();
                });
            } catch (Exception ex) {
                AppLogger.Error("StartTruehddDownloadOrUpdate", ex);
                RunOnUiThread(() => {
                    isTruehddDownloading = false;
                    SetTruehddStatus(ex.Message, 0);
                    RefreshTruehddCardUi();
                    ShowError(ex.Message);
                });
            }
        });
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
}
