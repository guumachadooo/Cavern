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

    // Shell views
    TextView headerFileText;
    Spinner trackSpinner;
    TextView trackSummaryText;
    LinearLayout[] tabPages;
    Button[] tabButtons;
    int activeTab;
    Handler uiHandler;
    bool uiDestroyed;

    // Color palette
    static readonly Color ColBg = Color.ParseColor("#0B0F17");
    static readonly Color ColCard = Color.ParseColor("#151C28");
    static readonly Color ColCardAlt = Color.ParseColor("#1E293B");
    static readonly Color ColAccent = Color.ParseColor("#10B981");
    static readonly Color ColCyan = Color.ParseColor("#38BDF8");
    static readonly Color ColText = Color.ParseColor("#F1F5F9");
    static readonly Color ColMuted = Color.ParseColor("#94A3B8");
    static readonly Color ColDanger = Color.ParseColor("#EF4444");

    protected override void OnCreate(Bundle savedInstanceState) {
        base.OnCreate(savedInstanceState);

        store = new AppSettingsStore(this);
        store.ApplyGlobals();
        upmixingSettings = new AndroidUpmixingSettings(store);
        renderingSettings = new AndroidRenderingSettings(store);
        ffmpeg = new AndroidFFmpegRunner(_ => { });
        if (!string.IsNullOrEmpty(store.FFmpegLocation)) {
            ffmpeg.Location = store.FFmpegLocation;
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
        LinearLayout root = new(this) {
            Orientation = Orientation.Vertical
        };
        root.SetBackgroundColor(ColBg);
        root.SetFitsSystemWindows(true);

        // Top Header Bar
        LinearLayout topBar = new(this) {
            Orientation = Orientation.Horizontal
        };
        topBar.SetBackgroundColor(ColCard);
        topBar.SetPadding(Dp(12), Dp(10), Dp(12), Dp(8));
        topBar.SetGravity(GravityFlags.CenterVertical);

        ImageView logo = new(this);
        logo.SetImageResource(Resource.Drawable.cavern_logo);
        LinearLayout.LayoutParams logoLp = new(Dp(34), Dp(34)) { RightMargin = Dp(10) };
        topBar.AddView(logo, logoLp);

        LinearLayout titleCol = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = new(this) {
            Text = "Cavernize Spatial Studio",
            TextSize = 17f
        };
        titleTv.SetTextColor(ColText);
        titleTv.SetTypeface(null, TypefaceStyle.Bold);

        headerFileText = new TextView(this) {
            Text = S["NoFile"],
            TextSize = 11.5f
        };
        headerFileText.SetTextColor(ColMuted);
        headerFileText.SetSingleLine(true);
        headerFileText.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;

        titleCol.AddView(titleTv);
        titleCol.AddView(headerFileText);
        topBar.AddView(titleCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        Button openBtn = PrimaryButton(S["OpenFile"], () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false));
        openBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        topBar.AddView(openBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(38)) { LeftMargin = Dp(8) });
        root.AddView(topBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Track selector bar
        LinearLayout trackBar = new(this) { Orientation = Orientation.Vertical };
        trackBar.SetBackgroundColor(Color.ParseColor("#111827"));
        trackBar.SetPadding(Dp(12), Dp(4), Dp(12), Dp(6));

        LinearLayout trackRow = new(this) { Orientation = Orientation.Horizontal };
        trackRow.SetGravity(GravityFlags.CenterVertical);
        TextView trLbl = Label(S["TrackLabel"], 12.5f, ColMuted);
        trackSpinner = new Spinner(this);
        trackSpinner.ItemSelected += (_, e) => {
            if (LoadedFile != null && e.Position >= 0 && e.Position < LoadedFile.Tracks.Count) {
                if (SelectedTrack != LoadedFile.Tracks[e.Position]) {
                    SelectedTrack = LoadedFile.Tracks[e.Position];
                }
            }
        };
        trackRow.AddView(trLbl, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
        trackRow.AddView(trackSpinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        trackBar.AddView(trackRow);

        trackSummaryText = Label("", 11.5f, ColCyan);
        trackSummaryText.Visibility = ViewStates.Gone;
        trackBar.AddView(trackSummaryText);
        root.AddView(trackBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Tab content frame
        FrameLayout contentFrame = new(this);
        LinearLayout playerTab = BuildPlayerTab();
        LinearLayout convertTab = BuildConverterTab();
        LinearLayout extractTab = BuildExtractorTab();
        LinearLayout settingsTab = BuildSettingsTab();
        tabPages = [playerTab, convertTab, extractTab, settingsTab];

        for (int i = 0; i < tabPages.Length; i++) {
            ScrollView sv = new(this) { FillViewport = true };
            sv.AddView(tabPages[i]);
            sv.Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone;
            contentFrame.AddView(sv, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }
        root.AddView(contentFrame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // Bottom 4-Tab Bar
        LinearLayout navBar = new(this) { Orientation = Orientation.Horizontal };
        navBar.SetBackgroundColor(ColCard);
        navBar.SetPadding(Dp(4), Dp(6), Dp(4), Dp(6));
        string[] tabTitles = [S["TabPlayer"], S["TabConvert"], S["TabExtract"], S["TabSettings"]];
        tabButtons = new Button[4];
        for (int i = 0; i < 4; i++) {
            int idx = i;
            Button tb = new(this) {
                Text = tabTitles[i],
                TextSize = 11.5f
            };
            tb.SetAllCaps(false);
            tb.Click += (_, _) => SwitchTab(idx);
            tabButtons[i] = tb;
            navBar.AddView(tb, new LinearLayout.LayoutParams(0, Dp(44), 1f) { LeftMargin = Dp(3), RightMargin = Dp(3) });
        }
        root.AddView(navBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SetContentView(root);
        UpdateTabButtonStyles();
        RefreshAllUiState();
    }

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
            tabButtons[i].SetBackgroundColor(active ? ColAccent : ColCardAlt);
            tabButtons[i].SetTextColor(active ? Color.Black : ColText);
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
                RunOnUiThread(() => {
                    store.FFmpegLocation = path;
                    ffmpeg.Location = path;
                    RefreshSettingsTabButtons();
                });
            });
        }
    }

    void LoadUriAsync(Android.Net.Uri uri) {
        if (isOperationRunning) {
            ShowError(S["OpRunning"]);
            return;
        }
        SetConvertStatus(S["CopyingMedia"], -1);
        Task.Run(() => {
            try {
                string resolved = AndroidFileHelper.ResolveReadablePath(this, uri,
                    (p, name) => RunOnUiThread(() => SetConvertStatus($"{S["CopyingMedia"]} ({name}: {p:0%})", p)));

                AudioFile file;
                try {
                    file = new AudioFile(resolved);
                    if (file.Tracks.Count == 0 || file.Tracks.All(t => !t.Supported)) {
                        throw new NotSupportedException();
                    }
                } catch {
                    // Fallback to Android MediaExtractor/MediaCodec for standard music formats (MP3, AAC, FLAC, OGG, etc.)
                    RunOnUiThread(() => SetConvertStatus(S["DecodingFallback"], -1));
                    string wavCacheDir = Path.Combine(CacheDir.AbsolutePath, "decoded_fallback");
                    string decodedWav = AndroidMediaFallbackDecoder.DecodeToWav(resolved, wavCacheDir,
                        p => RunOnUiThread(() => SetConvertStatus($"{S["DecodingFallback"]} ({p:0%})", p)));
                    file = new AudioFile(decodedWav);
                }

                RunOnUiThread(() => {
                    OpenContent(file);
                    SetConvertStatus(S["Ready"], 0);
                });
            } catch (Exception ex) {
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

        if (file.Tracks.Count > 0) {
            string[] trackNames = file.Tracks.Select((t, idx) => $"#{idx + 1}: {t}").ToArray();
            ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleSpinnerItem, trackNames);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            trackSpinner.Adapter = adapter;

            CavernizeTrack best = file.Tracks
                .Where(x => x.Codec != Codec.Unknown && x.Supported)
                .OrderBy(x => x.Codec)
                .FirstOrDefault() ?? file.Tracks[0];
            int bestIdx = file.Tracks.ToList().IndexOf(best);
            trackSpinner.SetSelection(Math.Max(0, bestIdx));
            SelectedTrack = best;
        } else {
            trackSpinner.Adapter = null;
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
        if (headerFileText != null) {
            headerFileText.Text = S["NoFile"];
        }
        if (trackSpinner != null) {
            trackSpinner.Adapter = null;
        }
        if (trackSummaryText != null) {
            trackSummaryText.Visibility = ViewStates.Gone;
        }
        report = new PostRenderReport(environment.Listener);
    }

    void OnSelectedTrackChanged() {
        StopPlayback();
        if (selectedTrack == null) {
            trackSummaryText.Visibility = ViewStates.Gone;
            trackSourceItems = [];
            RefreshPlayerSourcesList();
            RefreshExtractorChecklist();
            RefreshConverterTrackInfo();
            return;
        }

        string details = string.Join(" | ", selectedTrack.Details.Select(d => $"{d.property}: {d.value}"));
        trackSummaryText.Text = $"{selectedTrack.FormatHeader} — {details}";
        trackSummaryText.Visibility = ViewStates.Visible;

        if (selectedTrack.Supported && selectedTrack.Codec != Codec.TrueHD) {
            try {
                playerEngine.Load(selectedTrack, upmixingSettings, renderingSettings);
                trackSourceItems = [.. playerEngine.SourceItems];
            } catch (Exception ex) {
                trackSourceItems = TrackSourceItem.BuildFromTrack(selectedTrack);
                ShowError(ex.Message);
            }
        } else {
            trackSourceItems = TrackSourceItem.BuildFromTrack(selectedTrack);
        }

        RefreshPlayerSourcesList();
        RefreshExtractorChecklist();
        RefreshConverterTrackInfo();
        UpdateRoomCanvas();
    }

    void RefreshAllUiState() {
        RefreshConverterTabState();
        RefreshPlayerSourcesList();
        RefreshExtractorChecklist();
        RefreshSettingsTabButtons();
        UpdateRoomCanvas();
    }

    // Shared UI Helpers
    int Dp(int dp) => (int)Math.Round(dp * Resources.DisplayMetrics.Density);

    LinearLayout Card(string title = null) {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.SetBackgroundColor(ColCard);
        card.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        };
        card.LayoutParameters = lp;
        if (!string.IsNullOrEmpty(title)) {
            card.AddView(SectionTitle(title));
        }
        return card;
    }

    TextView SectionTitle(string text) {
        TextView tv = new(this) {
            Text = text,
            TextSize = 14.5f
        };
        tv.SetTextColor(ColAccent);
        tv.SetTypeface(null, TypefaceStyle.Bold);
        tv.SetPadding(0, 0, 0, Dp(6));
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
        b.SetBackgroundColor(ColAccent);
        b.SetTextColor(Color.Black);
        b.SetTypeface(null, TypefaceStyle.Bold);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    Button SecondaryButton(string text, Action onClick) {
        Button b = new(this) { Text = text, TextSize = 12.5f };
        b.SetAllCaps(false);
        b.SetBackgroundColor(ColCardAlt);
        b.SetTextColor(ColText);
        b.Click += (_, _) => onClick?.Invoke();
        return b;
    }

    void ShowError(string msg) {
        ShowMessageDialog(S["ErrorTitle"], msg);
    }

    void ShowMessageDialog(string title, string msg) {
        if (uiDestroyed) {
            return;
        }
        new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetMessage(msg)
            .SetPositiveButton("OK", (_, _) => { })
            .Show();
    }
}
