using Android;
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

    // Unified Smart Media Header Views (replaces redundant File + Spinner bar)
    TextView headerFileText;
    View mediaMetaDivider;
    LinearLayout mediaMetaRow;
    TextView trackSummaryText;
    LinearLayout trackPickerBadge;
    TextView trackPickerLabel;
    OneUiIconView trackPickerChevron;
    LinearLayout inlineTrackList;
    bool trackPickerExpanded;

    UpdateCheckResult latestUpdateInfo;
    FFmpegStatusResult latestFFmpegStatus;
    string downloadedUpdateApkPath;
    string pendingInstallApkPath;
    bool isUpdateDownloading;
    bool isFFmpegDownloading;

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
        audioOutput?.Dispose();
        playerEngine?.Dispose();
        LoadedFile?.Dispose();
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

        // Layer 0: Atmospheric blurred One UI 9 depth backdrop
        OneUiBackdropView backdrop = new(this);
        rootFrame.AddView(backdrop, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Layer 1: Main content column
        LinearLayout root = new(this) {
            Orientation = Orientation.Vertical
        };
        root.SetBackgroundColor(Color.Transparent);
        root.SetFitsSystemWindows(true);

        // Smart Unified Media Header Card (File Picker + Active Track Summary + Inline Multi-Track Drawer)
        LinearLayout mediaHeaderCard = new(this) { Orientation = Orientation.Vertical };
        mediaHeaderCard.Background = OneUiTheme.CreateGlassCardDrawable(this, null, 22f);
        mediaHeaderCard.Elevation = Dp(6);
        mediaHeaderCard.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        LinearLayout.LayoutParams headerCardLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            LeftMargin = Dp(14),
            RightMargin = Dp(14),
            TopMargin = Dp(10),
            BottomMargin = Dp(8)
        };

        LinearLayout topRow = new(this) { Orientation = Orientation.Horizontal };
        topRow.SetGravity(GravityFlags.CenterVertical);

        ImageView logo = new(this);
        logo.SetImageResource(Resource.Drawable.cavern_logo);
        LinearLayout.LayoutParams logoLp = new(Dp(34), Dp(34)) { RightMargin = Dp(10) };
        topRow.AddView(logo, logoLp);

        LinearLayout titleCol = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = new(this) {
            Text = "Cavernize",
            TextSize = 20f
        };
        titleTv.SetIncludeFontPadding(false);
        titleTv.SetTextColor(OneUiTheme.PeachText);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);

        headerFileText = new TextView(this) {
            Text = S["NoFile"],
            TextSize = 11.5f
        };
        headerFileText.SetTextColor(OneUiTheme.TextSub);
        headerFileText.SetSingleLine(true);
        headerFileText.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;

        titleCol.AddView(titleTv);
        titleCol.AddView(headerFileText);
        topRow.AddView(titleCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        LinearLayout openBtn = OneUiPillFactory.CreateIconActionPill(
            this, OneUiIconType.Folder, S["OpenFile"],
            () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false),
            primary: true);
        topRow.AddView(openBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        mediaHeaderCard.AddView(topRow);

        mediaMetaDivider = OneUiTheme.CreateDivider(this);
        mediaMetaDivider.Visibility = ViewStates.Gone;
        mediaHeaderCard.AddView(mediaMetaDivider);

        mediaMetaRow = new LinearLayout(this) {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        mediaMetaRow.SetGravity(GravityFlags.CenterVertical);

        trackSummaryText = Label("", 11.5f, ColCyan);
        trackSummaryText.SetPadding(Dp(2), 0, Dp(6), 0);
        mediaMetaRow.AddView(trackSummaryText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        trackPickerBadge = new LinearLayout(this) {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        trackPickerBadge.SetGravity(GravityFlags.CenterVertical);
        trackPickerBadge.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp, OneUiTheme.WithAlpha(ColAccent, 140));
        trackPickerBadge.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        OneUiTheme.AttachRecoil(trackPickerBadge, 0.95f);

        trackPickerLabel = Label("", 11.5f, ColText);
        trackPickerLabel.SetIncludeFontPadding(false);
        trackPickerLabel.SetTypeface(null, TypefaceStyle.Bold);
        trackPickerChevron = new OneUiIconView(this, OneUiIconType.ChevronDown, OneUiTheme.PeachText);
        trackPickerBadge.AddView(trackPickerLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });
        trackPickerBadge.AddView(trackPickerChevron, new LinearLayout.LayoutParams(Dp(14), Dp(14)));
        trackPickerBadge.Click += (_, _) => {
            trackPickerExpanded = !trackPickerExpanded;
            UpdateInlineTrackListVisibility();
        };
        mediaMetaRow.AddView(trackPickerBadge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        mediaHeaderCard.AddView(mediaMetaRow);

        inlineTrackList = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        inlineTrackList.SetPadding(0, Dp(8), 0, 0);
        mediaHeaderCard.AddView(inlineTrackList);

        root.AddView(mediaHeaderCard, headerCardLp);

        // Tab content frame
        FrameLayout contentFrame = new(this);
        LinearLayout playerTab = BuildPlayerTab();
        LinearLayout convertTab = BuildConverterTab();
        LinearLayout extractTab = BuildExtractorTab();
        LinearLayout settingsTab = BuildSettingsTab();
        tabPages = [playerTab, convertTab, extractTab, settingsTab];

        for (int i = 0; i < tabPages.Length; i++) {
            ScrollView sv = new(this) {
                FillViewport = true,
                VerticalScrollBarEnabled = true
            };
            sv.SetClipToPadding(false);
            sv.SetBackgroundColor(Color.Transparent);
            sv.AddView(tabPages[i]);
            sv.Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone;
            contentFrame.AddView(sv, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }
        root.AddView(contentFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        rootFrame.AddView(root, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Layer 2: Floating Frosted Glass Capsule Bottom Navigation Bar
        LinearLayout navBar = new(this) { Orientation = Orientation.Horizontal };
        navBar.Background = OneUiTheme.CreateFloatingGlassPill(this);
        navBar.Elevation = Dp(12);
        navBar.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
        navBar.SetGravity(GravityFlags.CenterVertical);

        string[] tabTitles = [S["TabPlayer"], S["TabConvert"], S["TabExtract"], S["TabSettings"]];
        tabButtons = new Button[4];
        for (int i = 0; i < 4; i++) {
            int idx = i;
            Button tb = new(this) {
                Text = tabTitles[i],
                TextSize = 12f
            };
            tb.SetAllCaps(false);
            tb.SetIncludeFontPadding(false);
            tb.SetSingleLine(true);
            tb.SetMinimumHeight(Dp(40));
            tb.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
            OneUiTheme.AttachRecoil(tb, 0.94f);
            tb.Click += (_, _) => SwitchTab(idx);
            tabButtons[i] = tb;

            if (i == 3) {
                FrameLayout tabCell = new(this);
                tabCell.AddView(tb, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

                settingsUpdateDot = new View(this);
                settingsUpdateDot.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.FunctionalOrange, 99f);
                settingsUpdateDot.Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
                FrameLayout.LayoutParams dotLp = new(Dp(8), Dp(8)) {
                    Gravity = GravityFlags.Top | GravityFlags.End,
                    TopMargin = Dp(6),
                    RightMargin = Dp(9)
                };
                tabCell.AddView(settingsUpdateDot, dotLp);
                navBar.AddView(tabCell, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(2), RightMargin = Dp(2) });
            } else {
                navBar.AddView(tb, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(2), RightMargin = Dp(2) });
            }
        }

        FrameLayout.LayoutParams floatingNavLp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(16),
            RightMargin = Dp(16),
            BottomMargin = Dp(14)
        };
        rootFrame.AddView(navBar, floatingNavLp);

        SetContentView(rootFrame);
        UpdateTabButtonStyles();
        RefreshAllUiState();
    }

    void RebuildInlineTrackList() {
        if (inlineTrackList == null || trackPickerBadge == null) {
            return;
        }
        inlineTrackList.RemoveAllViews();
        if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
            trackPickerBadge.Visibility = ViewStates.Gone;
            trackPickerExpanded = false;
            inlineTrackList.Visibility = ViewStates.Gone;
            return;
        }

        int selIdx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
        trackPickerBadge.Visibility = ViewStates.Visible;
        trackPickerLabel.Text = string.Format(S["TrackMultiBadge"], selIdx + 1, LoadedFile.Tracks.Count);

        TextView hdr = Label(S["SelectTrackHeader"], 11.5f, ColMuted);
        hdr.SetPadding(Dp(2), 0, 0, Dp(6));
        inlineTrackList.AddView(hdr);

        for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
            CavernizeTrack tr = LoadedFile.Tracks[i];
            bool isSel = tr == selectedTrack;

            LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
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
                trackPickerExpanded = false;
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
        if (inlineTrackList == null || trackPickerChevron == null) {
            return;
        }
        bool show = trackPickerExpanded && LoadedFile != null && LoadedFile.Tracks.Count > 1;
        inlineTrackList.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        trackPickerChevron.IconType = show ? OneUiIconType.ChevronUp : OneUiIconType.ChevronDown;
    }

    bool ShouldShowSettingsDot() =>
        latestUpdateInfo?.IsUpdateAvailable == true ||
        latestFFmpegStatus?.IsUpdateAvailable == true;

    void SwitchTab(int idx) {
        activeTab = idx;
        if (tabPages == null) {
            return;
        }
        for (int i = 0; i < tabPages.Length; i++) {
            if (tabPages[i].Parent is View sv) {
                sv.Visibility = i == idx ? ViewStates.Visible : ViewStates.Gone;
            }
        }
        UpdateTabButtonStyles();
    }

    void UpdateTabButtonStyles() {
        for (int i = 0; i < tabButtons.Length; i++) {
            bool active = i == activeTab;
            tabButtons[i].Background = active
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 225), OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateRippleRoundedDrawable(this, Color.Transparent, OneUiTheme.PillCornerDp);
            tabButtons[i].SetTextColor(active ? ColText : OneUiTheme.TextSecondary);
            tabButtons[i].SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
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
        }
    }

    void LoadUriAsync(Android.Net.Uri uri) {
        if (isOperationRunning) {
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

                AudioFile file;
                try {
                    file = new AudioFile(resolved);
                    if (file.Tracks.Count == 0 || file.Tracks.All(t => !t.Supported)) {
                        throw new NotSupportedException("Fallback required for container/codec");
                    }
                } catch (Exception nativeEx) {
                    AppLogger.Info($"Native Cavern reader fallback ({nativeEx.GetType().Name}), decoding via Android MediaCodec...");
                    RunOnUiThread(() => {
                        headerFileText.Text = S["DecodingFallback"];
                        SetConvertStatus(S["DecodingFallback"], -1);
                    });
                    string wavCacheDir = Path.Combine(CacheDir.AbsolutePath, "decoded_fallback");
                    string decodedWav = AndroidMediaFallbackDecoder.DecodeToWav(resolved, wavCacheDir,
                        p => RunOnUiThread(() => {
                            string msg = $"{S["DecodingFallback"]} ({p:0%})";
                            headerFileText.Text = msg;
                            SetConvertStatus(msg, p);
                        }));
                    file = new AudioFile(decodedWav);
                }

                RunOnUiThread(() => {
                    OpenContent(file);
                    SetConvertStatus(S["Ready"], 0);
                });
            } catch (Exception ex) {
                AppLogger.Error("LoadUriAsync", ex);
                RunOnUiThread(() => {
                    SetConvertStatus(ex.Message, 0);
                    ShowError(ex.Message);
                });
            }
        });
    }

    // ICavernizeApp implementation
    public void OpenContent(string path) {
        Reset();
        ffmpeg.CheckFFmpeg();
        OpenContent(new AudioFile(path));
    }

    public void OpenContent(AudioFile file) {
        StopPlayback();
        LoadedFile = file;
        headerFileText.Text = Path.GetFileName(file.Path);
        AppLogger.Info($"Opened file: '{file.Path}', tracks={file.Tracks.Count}");

        if (file.Tracks.Count > 0) {
            CavernizeTrack best = file.Tracks
                .Where(x => x.Codec != Codec.Unknown && x.Supported)
                .OrderBy(x => x.Codec)
                .FirstOrDefault() ?? file.Tracks[0];
            SelectedTrack = best;
        } else {
            SelectedTrack = null;
        }
    }

    public void Reset() {
        StopPlayback();
        environment.Reset();
        if (LoadedFile != null && renderQueue.Jobs.FirstOrDefault(x => x.IsUsingFile(LoadedFile)) == null) {
            LoadedFile.Dispose();
            LoadedFile = null;
        }
        selectedTrack = null;
        trackSourceItems = [];
        trackPickerExpanded = false;
        if (headerFileText != null) {
            headerFileText.Text = S["NoFile"];
        }
        if (mediaMetaRow != null) {
            mediaMetaRow.Visibility = ViewStates.Gone;
            mediaMetaDivider.Visibility = ViewStates.Gone;
        }
        RebuildInlineTrackList();
        report = new PostRenderReport(environment.Listener);
    }

    void OnSelectedTrackChanged() {
        StopPlayback();
        if (selectedTrack == null) {
            if (mediaMetaRow != null) {
                mediaMetaRow.Visibility = ViewStates.Gone;
                mediaMetaDivider.Visibility = ViewStates.Gone;
            }
            trackSourceItems = [];
            RefreshPlayerSourcesList();
            RefreshExtractorChecklist();
            RefreshConverterTrackInfo();
            RebuildInlineTrackList();
            return;
        }

        AppLogger.Info($"Selected track: {selectedTrack.FormatHeader}");
        string details = string.Join(" | ", selectedTrack.Details.Select(d => $"{d.property}: {d.value}"));
        if (trackSummaryText != null) {
            trackSummaryText.Text = $"{selectedTrack.FormatHeader} — {details}";
            mediaMetaRow.Visibility = ViewStates.Visible;
            mediaMetaDivider.Visibility = ViewStates.Visible;
        }
        RebuildInlineTrackList();

        if (selectedTrack.Supported && selectedTrack.Codec != Codec.TrueHD) {
            try {
                playerEngine.Load(selectedTrack, upmixingSettings, renderingSettings);
                trackSourceItems = [.. playerEngine.SourceItems];
            } catch (Exception ex) {
                AppLogger.Error("PlayerEngine.Load", ex);
                trackSourceItems = TrackSourceItem.BuildFromTrack(selectedTrack);
                ShowError(ex.Message);
            }
        } else {
            trackSourceItems = TrackSourceItem.BuildFromTrack(selectedTrack);
        }

        RefreshPlayerSourcesList();
        RefreshExtractorChecklist();
        RefreshConverterTrackInfo();
        RefreshContainerOptions();
        if (playbackSeekBar != null) {
            playbackSeekBar.IsWavePlaying = false;
            playbackSeekBar.Progress = 0;
            playbackSeekBar.CurrentTimeText = "00:00";
            playbackSeekBar.TotalTimeText = $"{TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
            playbackSeekBar.Invalidate();
        }
        UpdateRoomCanvas();
    }

    void RefreshAllUiState() {
        RefreshConverterTabState();
        RefreshPlayerSourcesList();
        RefreshExtractorChecklist();
        RefreshSettingsTabButtons();
        RefreshSettingsDot();
        RefreshFFmpegCardUi();
        RefreshSettingsUpdateUi();
        UpdateRoomCanvas();
    }

    void RefreshSettingsDot() {
        if (settingsUpdateDot != null) {
            settingsUpdateDot.Visibility = ShouldShowSettingsDot() ? ViewStates.Visible : ViewStates.Gone;
        }
    }

    // Shared One UI 9 Frosted UI Helpers (zero emojis, non-clipping pills)
    int Dp(int dp) => (int)Math.Round(dp * Resources.DisplayMetrics.Density);

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
            if (icon.HasValue && badgeColor.HasValue) {
                LinearLayout hdr = new(this) { Orientation = Orientation.Horizontal };
                hdr.SetGravity(GravityFlags.CenterVertical);
                hdr.SetPadding(0, 0, 0, Dp(10));
                hdr.AddView(OneUiTheme.CreateVectorIconBadge(this, icon.Value, badgeColor.Value));
                TextView tv = SectionTitle(title);
                tv.SetPadding(Dp(10), 0, 0, 0);
                hdr.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                card.AddView(hdr);
            } else {
                card.AddView(SectionTitle(title));
            }
        }
        return card;
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
        Button b = new(this) { Text = text, TextSize = 13.5f };
        b.SetAllCaps(false);
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(42));
        b.Background = OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp);
        b.SetTextColor(ColText);
        b.SetTypeface(null, TypefaceStyle.Bold);
        b.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button SecondaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 12.5f };
        b.SetAllCaps(false);
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(40));
        b.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.ButtonCornerDp);
        b.SetTextColor(ColText);
        b.SetPadding(Dp(14), Dp(8), Dp(14), Dp(8));
        OneUiTheme.AttachRecoil(b);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button PillButton(string text, Action onClick, bool active = false) {
        Button b = new(this) { Text = text, TextSize = 12.5f };
        b.SetAllCaps(false);
        b.SetIncludeFontPadding(false);
        b.Gravity = GravityFlags.Center;
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(Dp(38));
        b.Background = active
            ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        b.SetTextColor(ColText);
        b.SetTypeface(null, active ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        b.SetPadding(Dp(14), Dp(7), Dp(14), Dp(7));
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
