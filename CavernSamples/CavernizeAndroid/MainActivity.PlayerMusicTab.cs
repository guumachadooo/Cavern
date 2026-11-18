using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;

using Cavern.Format.Common;

using Cavernize.Logic.Models;
using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;
using CavernizeAndroid.Views;

using Path = System.IO.Path;

namespace CavernizeAndroid;

public partial class MainActivity {
    sealed class RoundedArtworkView : View {
        readonly Paint bgPaint = new(PaintFlags.AntiAlias);
        readonly Paint bmpPaint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
        readonly Paint rimPaint = new(PaintFlags.AntiAlias);
        readonly Android.Graphics.Path clipPath = new();
        readonly Matrix drawMatrix = new();

        Bitmap bitmap;
        Color accentTint = OneUiTheme.Primary;
        bool isPlayingState;

        public int PreferredSizeDp { get; set; } = 286;
        public float CornerDp { get; set; } = 16f;

        public RoundedArtworkView(Context ctx) : base(ctx) {
            bgPaint.SetStyle(Paint.Style.Fill);
            rimPaint.SetStyle(Paint.Style.Stroke);
            rimPaint.StrokeWidth = Math.Max(1.5f, OneUiTheme.Dp(ctx, 1.0f));
            rimPaint.Color = Color.Argb(48, 255, 255, 255);
            Elevation = OneUiTheme.Dp(ctx, 10f);
        }

        public void SetArtwork(Bitmap bmp, Color accent) {
            bitmap = bmp;
            accentTint = accent;
            Invalidate();
        }

        public void SetPlaying(bool playing) {
            if (isPlayingState == playing) {
                return;
            }
            isPlayingState = playing;
            float targetScale = playing ? 1.0f : 0.88f;
            float targetElev = OneUiTheme.Dp(Context, playing ? 18f : 7f);
            Animate()
                .ScaleX(targetScale)
                .ScaleY(targetScale)
                .SetDuration(360)
                .SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.7f))
                .Start();
            Elevation = targetElev;
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
            int maxW = MeasureSpec.GetSize(widthMeasureSpec);
            int prefPx = OneUiTheme.Dp(Context, PreferredSizeDp);
            int target = maxW > 0 ? Math.Min(maxW, prefPx) : prefPx;
            SetMeasuredDimension(target, target);
        }

        protected override void OnDraw(Canvas canvas) {
            base.OnDraw(canvas);
            float w = Width;
            float h = Height;
            if (w <= 0 || h <= 0) {
                return;
            }

            float rad = OneUiTheme.Dp(Context, CornerDp);
            RectF rect = new(0f, 0f, w, h);

            clipPath.Reset();
            clipPath.AddRoundRect(rect, rad, rad, Android.Graphics.Path.Direction.Cw);
            int save = canvas.Save();
            canvas.ClipPath(clipPath);

            bgPaint.Color = Color.Argb(255, 16, 18, 26);
            canvas.DrawRect(rect, bgPaint);

            bool drewBitmap = false;
            try {
                if (bitmap != null && bitmap.Handle != IntPtr.Zero && !bitmap.IsRecycled && bitmap.Width > 0 && bitmap.Height > 0) {
                    float bw = bitmap.Width;
                    float bh = bitmap.Height;
                    float scale = Math.Max(w / bw, h / bh);
                    float dx = (w - bw * scale) * 0.5f;
                    float dy = (h - bh * scale) * 0.5f;
                    drawMatrix.Reset();
                    drawMatrix.PostScale(scale, scale);
                    drawMatrix.PostTranslate(dx, dy);
                    canvas.DrawBitmap(bitmap, drawMatrix, bmpPaint);
                    drewBitmap = true;
                }
            } catch {
                drewBitmap = false;
            }
            if (!drewBitmap) {
                using LinearGradient grad = new(0f, 0f, w, h,
                    [Color.Argb(215, accentTint.R, accentTint.G, accentTint.B).ToArgb(), Color.Rgb(16, 18, 25).ToArgb()],
                    [0f, 1f], Shader.TileMode.Clamp!);
                bgPaint.SetShader(grad);
                canvas.DrawRect(rect, bgPaint);
                bgPaint.SetShader(null);
            }
            canvas.RestoreToCount(save);

            canvas.DrawRoundRect(
                new RectF(0.75f, 0.75f, w - 0.75f, h - 0.75f),
                rad, rad, rimPaint);
        }
    }

    enum PlayerStageMode { Cover, Video, Lyrics }
    enum LibraryCategory { Albums, Tracks, Artists, Folders }

    PlayerStageMode musicStageMode = PlayerStageMode.Cover;
    string lastStageBoundPath;
    bool isLibrarySectionOpen;
    LibraryCategory libraryCategory = LibraryCategory.Albums;
    LibraryAlbumGroup selectedLibraryAlbum;
    string selectedLibraryArtist;
    LibraryFolderGroup selectedLibraryFolder;
    string librarySearchQuery = "";
    int libraryRenderLimit = 72;
    MediaLibrarySnapshot librarySnapshot;
    bool isLibraryScanning;
    int onlineEnrichGeneration;

    readonly List<LibraryTrackItem> activeLibraryQueue = [];
    int activeLibraryQueueIndex = -1;

    // Section switcher inside Play tab (Now Playing vs In-App Library)
    LinearLayout musicNowPlayingSection;
    LinearLayout musicLibrarySection;
    TextView segNowPlayingBtn, segLibraryBtn;

    // Library controls
    EditText librarySearchInput;
    Button[] libCatButtons;
    Button[] libFilterButtons;
    LinearLayout libraryListHost;
    TextView libraryStatusText;
    bool isIncrementalSyncRunning;

    FrameLayout mediaStageFrame;
    FrameLayout musicArtworkSlot;
    RoundedArtworkView musicArtworkView;

    FrameLayout musicVideoSlot;
    SyncedVideoStageView musicVideoStage;
    Android.App.Dialog fullscreenVideoDlg;

    // Borderless Apple Music Lyrics Sheet
    LinearLayout musicLyricsContainer;
    RoundedArtworkView musicLyricsMiniArt;
    TextView musicLyricsHeaderTitle, musicLyricsHeaderSub;
    AppleMusicLyricsView musicLyricsView;

    // Apple Music Title + Overflow Row
    LinearLayout musicTitleRow;
    TextView musicTitleText, musicArtistText;
    LinearLayout musicTrackBtn;
    TextView musicTrackBtnText;
    FrameLayout musicMoreBtn;

    // Seekbar + Centered Spatial Badge + Borderless Transport + Slim Volume
    OneUiWaveMediaSeekBar musicWaveSeekBar;
    LinearLayout musicSpatialBadgePill;
    OneUiIconView musicSpatialBadgeIcon;
    TextView musicSpatialBadgeText;

    FrameLayout musicSeekBackBtn, musicRewBtn, musicPlayCircleBtn, musicFwdBtn, musicSeekFwdBtn;
    OneUiIconView musicPlayIcon;
    OneUiSeekBar musicVolSeekBar;

    // Flanking Spatial Toggles ([Cavernize 3D] | [HRTF 3D] | [Matrix 7.1]) + Apple Music 3-Icon Bottom Bar
    LinearLayout musicCavernizeToggleBtn, musicBottomSpatialBtn, musicMatrixToggleBtn;
    OneUiIconView musicCavernizeIcon, musicBottomSpatialIcon, musicMatrixIcon;
    TextView musicCavernizeText, musicBottomSpatialText, musicMatrixText;

    FrameLayout musicBottomLeftBtn, musicBottomOnlineBtn, musicBottomLoopBtn;
    OneUiIconView musicBottomLeftIcon, musicBottomOnlineIcon, musicBottomLoopIcon;

    LinearLayout BuildMusicPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(20), Dp(4), Dp(20), Dp(116));

        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);
        Typeface sfReg = OneUiTheme.GetAppleFontRegular(this);

        // 0. Top Sub-Section Capsule Switcher inside Play Tab: [ Tocando Agora | Biblioteca ]
        LinearLayout segWrap = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        segWrap.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
        segWrap.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(46, 255, 255, 255), 999f, Color.Argb(38, 255, 255, 255), 1f);

        segNowPlayingBtn = new TextView(this) {
            Text = S["PlayerNowPlaying"],
            TextSize = 12f,
            Gravity = GravityFlags.Center
        };
        segNowPlayingBtn.SetTypeface(sfSemi, TypefaceStyle.Bold);
        segNowPlayingBtn.SetSingleLine(true);
        segNowPlayingBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        OneUiTheme.AttachRecoil(segNowPlayingBtn, 0.94f);
        segNowPlayingBtn.Click += (_, _) => SetMusicPlayerSection(openLibrary: false);

        segLibraryBtn = new TextView(this) {
            Text = S["PlayerLibrary"],
            TextSize = 12f,
            Gravity = GravityFlags.Center
        };
        segLibraryBtn.SetTypeface(sfSemi, TypefaceStyle.Bold);
        segLibraryBtn.SetSingleLine(true);
        segLibraryBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        OneUiTheme.AttachRecoil(segLibraryBtn, 0.94f);
        segLibraryBtn.Click += (_, _) => SetMusicPlayerSection(openLibrary: true);

        segWrap.AddView(segNowPlayingBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        segWrap.AddView(segLibraryBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(segWrap, new LinearLayout.LayoutParams(Dp(236), ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // ============================================================
        // SECTION A: NOW PLAYING (Exact Apple Music + Samsung Video)
        // ============================================================
        musicNowPlayingSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        musicNowPlayingSection.SetGravity(GravityFlags.CenterHorizontal);

        // 1. Unified Stage Container: Artwork / Video Stage / Live Lyrics
        mediaStageFrame = new FrameLayout(this);

        musicArtworkSlot = new FrameLayout(this);
        musicArtworkView = new RoundedArtworkView(this) {
            PreferredSizeDp = 286,
            CornerDp = 16f,
            ScaleX = 0.88f,
            ScaleY = 0.88f
        };
        musicArtworkView.Click += (_, _) => {
            if (LoadedFile == null) {
                SetMusicPlayerSection(openLibrary: true);
            } else {
                SetPlayerStageMode(PlayerStageMode.Lyrics);
            }
        };
        musicArtworkSlot.AddView(musicArtworkView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Center,
            TopMargin = Dp(6),
            BottomMargin = Dp(6)
        });
        mediaStageFrame.AddView(musicArtworkSlot, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        // Video Stage (Samsung Video Player + YouTube HUD + CC Subtitles + Portrait/Landscape Fullscreen)
        musicVideoSlot = new FrameLayout(this) { Visibility = ViewStates.Gone };
        musicVideoStage = new SyncedVideoStageView(this, S["VideoSyncBadge"]);
        musicVideoStage.PlayPauseRequested += TogglePlayPause;
        musicVideoStage.SeekDeltaRequested += SeekRelative;
        musicVideoStage.SeekAbsoluteRequested += SeekAbsoluteSeconds;
        musicVideoStage.SubtitlesRequested += ShowSubtitlesDialog;
        musicVideoStage.AudioTrackRequested += ShowTrackSelectorDialog;
        musicVideoStage.LoopToggleRequested += ToggleLoopMode;
        musicVideoStage.SpatialOptionsRequested += ShowPlayerMoreSheet;
        musicVideoStage.MoreOptionsRequested += ShowPlayerMoreSheet;
        musicVideoStage.FullscreenToggleRequested += ToggleVideoFullscreen;
        musicVideoSlot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        mediaStageFrame.AddView(musicVideoSlot, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(240)) {
            TopMargin = Dp(8),
            BottomMargin = Dp(6)
        });

        // Borderless Apple Music Live Lyrics Sheet (with SF Pro Bold, Depth-of-Field Blur & Wave Scroll)
        musicLyricsContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };

        LinearLayout lyricsHeader = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        lyricsHeader.SetGravity(GravityFlags.CenterVertical);
        lyricsHeader.SetPadding(0, Dp(2), 0, Dp(8));

        musicLyricsMiniArt = new RoundedArtworkView(this) {
            PreferredSizeDp = 50,
            CornerDp = 10f
        };
        musicLyricsMiniArt.Click += (_, _) => SetPlayerStageMode(PlayerStageMode.Cover);
        lyricsHeader.AddView(musicLyricsMiniArt, new LinearLayout.LayoutParams(Dp(50), Dp(50)) { RightMargin = Dp(12) });

        LinearLayout lyricsMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicLyricsHeaderTitle = Label(S["PlayerEmptyTitle"], 16.5f, ColText);
        musicLyricsHeaderTitle.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicLyricsHeaderTitle.SetSingleLine(true);
        musicLyricsHeaderTitle.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsHeaderSub = Label(S["LyricsTapHint"], 13f, Color.Argb(182, 252, 252, 255));
        musicLyricsHeaderSub.SetTypeface(sfReg, TypefaceStyle.Normal);
        musicLyricsHeaderSub.SetSingleLine(true);
        musicLyricsHeaderSub.Ellipsize = TextUtils.TruncateAt.End;
        lyricsMetaCol.AddView(musicLyricsHeaderTitle);
        lyricsMetaCol.AddView(musicLyricsHeaderSub);
        lyricsHeader.AddView(lyricsMetaCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        FrameLayout lyricsOnlineBtn = MakeAppleCircleIconBtn(OneUiIconType.Globe, ShowOnlineMetadataDialog);
        lyricsHeader.AddView(lyricsOnlineBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)) { RightMargin = Dp(6) });

        FrameLayout lyricsMoreBtn = MakeAppleCircleOverflowBtn(ShowPlayerMoreSheet);
        lyricsHeader.AddView(lyricsMoreBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)));
        musicLyricsContainer.AddView(lyricsHeader);

        musicLyricsView = new AppleMusicLyricsView(this);
        musicLyricsView.LineSeekRequested += sec => SeekAbsoluteSeconds(sec);
        musicLyricsContainer.AddView(musicLyricsView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        mediaStageFrame.AddView(musicLyricsContainer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(400)));

        musicNowPlayingSection.AddView(mediaStageFrame, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(12)
        });

        // 2. Apple Music Title + Artist + Overflow Row
        musicTitleRow = HRow();
        musicTitleRow.SetGravity(GravityFlags.CenterVertical);

        LinearLayout titleMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicTitleText = new TextView(this) {
            Text = S["PlayerEmptyTitle"],
            TextSize = 20.5f
        };
        musicTitleText.SetTextColor(ColText);
        musicTitleText.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicTitleText.SetSingleLine(true);
        musicTitleText.Ellipsize = TextUtils.TruncateAt.End;
        titleMetaCol.AddView(musicTitleText);

        musicArtistText = new TextView(this) {
            Text = S["PlayerEmptySub"],
            TextSize = 15f
        };
        musicArtistText.SetTextColor(Color.Argb(182, 252, 252, 255));
        musicArtistText.SetTypeface(sfReg, TypefaceStyle.Normal);
        musicArtistText.SetSingleLine(true);
        musicArtistText.Ellipsize = TextUtils.TruncateAt.End;
        titleMetaCol.AddView(musicArtistText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2)
        });
        musicTitleRow.AddView(titleMetaCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(10) });

        musicTrackBtn = HRow();
        musicTrackBtn.Visibility = ViewStates.Gone;
        musicTrackBtn.SetPadding(Dp(9), Dp(5), Dp(9), Dp(5));
        musicTrackBtn.Background = OneUiTheme.CreateGlassCardDrawable(this, Color.Argb(48, 255, 255, 255), OneUiTheme.PillCornerDp);
        OneUiTheme.AttachRecoil(musicTrackBtn, 0.93f);
        musicTrackBtnText = Label("1/2", 11.5f, ColText);
        musicTrackBtnText.SetIncludeFontPadding(false);
        musicTrackBtnText.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicTrackBtnText.SetSingleLine(true);
        musicTrackBtn.AddView(musicTrackBtnText);
        musicTrackBtn.Click += (_, _) => ShowTrackSelectorDialog();
        musicTitleRow.AddView(musicTrackBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(8) });

        musicMoreBtn = MakeAppleCircleOverflowBtn(ShowPlayerMoreSheet);
        musicTitleRow.AddView(musicMoreBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)));

        musicNowPlayingSection.AddView(musicTitleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(14)
        });

        // 3. One UI 9 Wave Media Seekbar + Apple Music centered Audio Badge
        musicWaveSeekBar = new OneUiWaveMediaSeekBar(this) {
            Max = 1000,
            CurrentTimeText = "00:00",
            TotalTimeText = "00:00"
        };
        musicWaveSeekBar.SetThemeByColor(OneUiTheme.Primary);
        musicWaveSeekBar.StartTrackingTouch += () => isUserSeeking = true;
        musicWaveSeekBar.ProgressChanged += (prog, fromUser) => {
            if (fromUser && playerEngine.TotalSeconds > 0) {
                double preview = prog / 1000.0 * playerEngine.TotalSeconds;
                string curStr = $"{TimeSpan.FromSeconds(preview):mm\\:ss}";
                string totStr = $"{TimeSpan.FromSeconds(playerEngine.TotalSeconds):mm\\:ss}";
                musicWaveSeekBar.CurrentTimeText = curStr;
                musicWaveSeekBar.TotalTimeText = totStr;
                musicWaveSeekBar.Invalidate();
                if (playbackSeekBar != null) {
                    playbackSeekBar.Progress = prog;
                    playbackSeekBar.CurrentTimeText = curStr;
                    playbackSeekBar.TotalTimeText = totStr;
                    playbackSeekBar.Invalidate();
                }
            }
        };
        musicWaveSeekBar.StopTrackingTouch += () => {
            isUserSeeking = false;
            if (playerEngine.TotalSeconds > 0) {
                SeekAbsoluteSeconds(musicWaveSeekBar.Progress / 1000.0 * playerEngine.TotalSeconds);
            }
        };
        musicNowPlayingSection.AddView(musicWaveSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        musicSpatialBadgePill = HRow();
        musicSpatialBadgePill.SetGravity(GravityFlags.Center);
        musicSpatialBadgePill.SetPadding(Dp(10), Dp(3), Dp(10), Dp(3));
        musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 8f);
        OneUiTheme.AttachRecoil(musicSpatialBadgePill, 0.94f);
        musicSpatialBadgeIcon = new OneUiIconView(this, OneUiIconType.Sparkles, Color.Argb(220, 252, 252, 255));
        musicSpatialBadgeText = Label(S["PlayerBadgeSpatial"], 11f, Color.Argb(228, 252, 252, 255));
        musicSpatialBadgeText.SetIncludeFontPadding(false);
        musicSpatialBadgeText.SetSingleLine(true);
        musicSpatialBadgeText.SetTypeface(sfSemi, TypefaceStyle.Bold);
        musicSpatialBadgePill.AddView(musicSpatialBadgeIcon, new LinearLayout.LayoutParams(Dp(11), Dp(11)) { RightMargin = Dp(5) });
        musicSpatialBadgePill.AddView(musicSpatialBadgeText);
        musicSpatialBadgePill.Click += (_, _) => ShowPlayerMoreSheet();
        musicNowPlayingSection.AddView(musicSpatialBadgePill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(18)
        });

        // 4. Unified Borderless Transport Row (-10s | Skip Prev | Play/Pause | Skip Next | +10s)
        LinearLayout transportRow = HRow();
        transportRow.SetGravity(GravityFlags.Center);

        musicSeekBackBtn = MakeBorderlessAppleIconBtn(OneUiIconType.Rewind10, 22, () => SeekRelative(-10), out OneUiIconView seekBackIco);
        seekBackIco.IconColor = Color.Argb(175, 252, 252, 255);
        musicRewBtn = MakeBorderlessAppleIconBtn(OneUiIconType.SkipPrevious, 30, HandleMusicSkipPrevious, out _);
        musicPlayCircleBtn = MakeBorderlessAppleIconBtn(OneUiIconType.Play, 44, TogglePlayPause, out musicPlayIcon);
        musicFwdBtn = MakeBorderlessAppleIconBtn(OneUiIconType.SkipNext, 30, HandleMusicSkipNext, out _);
        musicSeekFwdBtn = MakeBorderlessAppleIconBtn(OneUiIconType.Forward10, 22, () => SeekRelative(10), out OneUiIconView seekFwdIco);
        seekFwdIco.IconColor = Color.Argb(175, 252, 252, 255);

        transportRow.AddView(musicSeekBackBtn, new LinearLayout.LayoutParams(Dp(44), Dp(48)) { RightMargin = Dp(8) });
        transportRow.AddView(musicRewBtn, new LinearLayout.LayoutParams(Dp(54), Dp(56)) { RightMargin = Dp(14) });
        transportRow.AddView(musicPlayCircleBtn, new LinearLayout.LayoutParams(Dp(68), Dp(62)) { RightMargin = Dp(14) });
        transportRow.AddView(musicFwdBtn, new LinearLayout.LayoutParams(Dp(54), Dp(56)) { RightMargin = Dp(8) });
        transportRow.AddView(musicSeekFwdBtn, new LinearLayout.LayoutParams(Dp(44), Dp(48)));
        musicNowPlayingSection.AddView(transportRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(18)
        });

        // 5. Apple Music Volume Row (SpeakerLow + slim Samsung relative-drag slider + SpeakerHigh)
        LinearLayout volRow = HRow();
        volRow.SetGravity(GravityFlags.CenterVertical);

        OneUiIconView lowSpk = new(this, OneUiIconType.SpeakerLow, Color.Argb(165, 252, 252, 255));
        lowSpk.Click += (_, _) => SetMasterVolumePercent(Math.Max(0, (int)Math.Round(store.PlayerVolume * 100) - 15));
        volRow.AddView(lowSpk, new LinearLayout.LayoutParams(Dp(18), Dp(18)) { RightMargin = Dp(10) });

        musicVolSeekBar = new OneUiSeekBar(this) {
            Max = 200,
            Progress = Math.Clamp((int)Math.Round(store.PlayerVolume * 100), 0, 200),
            DefaultProgress = 100,
            HasDefaultReset = true,
            SlimVolumeMode = true
        };
        musicVolSeekBar.UserProgressChanged += v => SetMasterVolumePercent(v);
        musicVolSeekBar.ResetRequested += v => SetMasterVolumePercent(v);
        volRow.AddView(musicVolSeekBar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        OneUiIconView highSpk = new(this, OneUiIconType.SpeakerHigh, Color.Argb(165, 252, 252, 255));
        highSpk.Click += (_, _) => SetMasterVolumePercent(Math.Min(200, (int)Math.Round(store.PlayerVolume * 100) + 15));
        volRow.AddView(highSpk, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { LeftMargin = Dp(10) });

        musicNowPlayingSection.AddView(volRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(14)
        });

        // 6. Flanking Spatial Toggles: [ Cavernize 3D ]  [ HRTF 3D / Direto ]  [ Matrix 7.1 ]
        LinearLayout spatialToggleRow = HRow();
        spatialToggleRow.SetGravity(GravityFlags.CenterVertical);

        (musicCavernizeToggleBtn, musicCavernizeIcon, musicCavernizeText) = MakeCompactSpatialPill(
            OneUiIconType.HeightUp, "Cavernize 3D", sfSemi, () => {
                bool next = !store.CavernizeUpmix;
                store.CavernizeUpmix = next;
                if (cavernizeUpmixSwitch != null) cavernizeUpmixSwitch.Checked = next;
                ReattachPlayerTrack();
                RefreshMusicPlayerControlsUi();
            });

        (musicBottomSpatialBtn, musicBottomSpatialIcon, musicBottomSpatialText) = MakeCompactSpatialPill(
            OneUiIconType.Headphones, S["BentoHrtf"], sfSemi, () => {
                store.HeadphoneVirtualizer = !store.HeadphoneVirtualizer;
                playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
                refreshBentoTilesUi?.Invoke();
                RefreshMusicPlayerControlsUi();
            });

        (musicMatrixToggleBtn, musicMatrixIcon, musicMatrixText) = MakeCompactSpatialPill(
            OneUiIconType.Sparkles, "Matrix 7.1", sfSemi, () => {
                bool next = !store.MatrixUpmixing;
                store.MatrixUpmixing = next;
                if (matrixUpmixSwitch != null) matrixUpmixSwitch.Checked = next;
                ReattachPlayerTrack();
                RefreshMusicPlayerControlsUi();
            });

        spatialToggleRow.AddView(musicCavernizeToggleBtn, new LinearLayout.LayoutParams(0, Dp(34), 1f) { RightMargin = Dp(6) });
        spatialToggleRow.AddView(musicBottomSpatialBtn, new LinearLayout.LayoutParams(0, Dp(34), 1f) { RightMargin = Dp(6) });
        spatialToggleRow.AddView(musicMatrixToggleBtn, new LinearLayout.LayoutParams(0, Dp(34), 1f));
        musicNowPlayingSection.AddView(spatialToggleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(12)
        });

        // 7. Apple Music 3-Icon Bottom Utility Bar ([ Letra / CC ] | [ Buscar Capa/Letra ] | [ Repetir ])
        LinearLayout bottomActionRow = HRow();
        bottomActionRow.SetGravity(GravityFlags.CenterVertical);

        musicBottomLeftBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomLeftBtn, 0.88f);
        musicBottomLeftIcon = new OneUiIconView(this, OneUiIconType.LyricsQuote, Color.Argb(195, 252, 252, 255));
        musicBottomLeftBtn.AddView(musicBottomLeftIcon, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
        musicBottomLeftBtn.Click += (_, _) => {
            if (currentMediaMeta?.HasVideo == true) {
                ShowSubtitlesDialog();
            } else {
                SetPlayerStageMode(musicStageMode == PlayerStageMode.Lyrics ? PlayerStageMode.Cover : PlayerStageMode.Lyrics);
            }
        };

        musicBottomOnlineBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomOnlineBtn, 0.88f);
        musicBottomOnlineIcon = new OneUiIconView(this, OneUiIconType.Globe, Color.Argb(195, 252, 252, 255));
        musicBottomOnlineBtn.AddView(musicBottomOnlineIcon, new FrameLayout.LayoutParams(Dp(21), Dp(21)) { Gravity = GravityFlags.Center });
        musicBottomOnlineBtn.Click += (_, _) => {
            if (currentMediaMeta?.HasVideo == true) {
                ShowSubtitlesDialog();
            } else {
                ShowOnlineMetadataDialog();
            }
        };

        musicBottomLoopBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomLoopBtn, 0.88f);
        musicBottomLoopIcon = new OneUiIconView(this, OneUiIconType.RepeatLoop, Color.Argb(195, 252, 252, 255));
        musicBottomLoopBtn.AddView(musicBottomLoopIcon, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
        musicBottomLoopBtn.Click += (_, _) => ToggleLoopMode();

        LinearLayout leftWrap = HRow(); leftWrap.SetGravity(GravityFlags.Center); leftWrap.AddView(musicBottomLeftBtn, new LinearLayout.LayoutParams(Dp(48), Dp(34)));
        LinearLayout midWrap = HRow(); midWrap.SetGravity(GravityFlags.Center); midWrap.AddView(musicBottomOnlineBtn, new LinearLayout.LayoutParams(Dp(48), Dp(34)));
        LinearLayout rightWrap = HRow(); rightWrap.SetGravity(GravityFlags.Center); rightWrap.AddView(musicBottomLoopBtn, new LinearLayout.LayoutParams(Dp(48), Dp(34)));

        bottomActionRow.AddView(leftWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(midWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(rightWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        musicNowPlayingSection.AddView(bottomActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        page.AddView(musicNowPlayingSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // ============================================================
        // SECTION B: IN-APP LIBRARY (Samsung Music + Poweramp Style)
        // ============================================================
        musicLibrarySection = BuildInAppLibrarySection(sfBold, sfSemi, sfReg);
        musicLibrarySection.Visibility = ViewStates.Gone;
        page.AddView(musicLibrarySection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        UpdateSectionSwitcherStyle();
        RefreshMusicPlayerUi();
        return page;
    }

    (LinearLayout pill, OneUiIconView ico, TextView txt) MakeCompactSpatialPill(
        OneUiIconType iconType, string text, Typeface tf, Action onClick) {
        LinearLayout pill = HRow();
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(8), Dp(5), Dp(8), Dp(5));
        OneUiTheme.AttachRecoil(pill, 0.93f);

        OneUiIconView ico = new(this, iconType, ColText);
        TextView tv = Label(text, 10.8f, ColText);
        tv.SetTypeface(tf, TypefaceStyle.Bold);
        tv.SetIncludeFontPadding(false);
        tv.SetSingleLine(true);
        tv.Ellipsize = TextUtils.TruncateAt.End;

        pill.AddView(ico, new LinearLayout.LayoutParams(Dp(13), Dp(13)) { RightMargin = Dp(4) });
        pill.AddView(tv);
        pill.Click += (_, _) => onClick?.Invoke();
        return (pill, ico, tv);
    }

    FrameLayout MakeAppleCircleOverflowBtn(Action onClick) =>
        MakeAppleCircleIconBtn(OneUiIconType.MoreHorizontal, onClick);

    FrameLayout MakeAppleCircleIconBtn(OneUiIconType iconType, Action onClick) {
        FrameLayout btn = new(this);
        btn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(44, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(btn, 0.88f);
        OneUiIconView ico = new(this, iconType, ColText);
        btn.AddView(ico, new FrameLayout.LayoutParams(Dp(17), Dp(17)) { Gravity = GravityFlags.Center });
        btn.Click += (_, _) => onClick?.Invoke();
        return btn;
    }

    FrameLayout MakeBorderlessAppleIconBtn(OneUiIconType iconType, int iconDp, Action onClick, out OneUiIconView iconOut) {
        FrameLayout btn = new(this);
        OneUiTheme.AttachRecoil(btn, 0.84f);
        iconOut = new OneUiIconView(this, iconType, ColText);
        btn.AddView(iconOut, new FrameLayout.LayoutParams(Dp(iconDp), Dp(iconDp)) { Gravity = GravityFlags.Center });
        btn.Click += (_, _) => onClick?.Invoke();
        return btn;
    }

    LinearLayout MakeCircleIconBtn(OneUiIconType icon, int sizeDp, bool primary, Action onClick, out OneUiIconView iconOut) {
        LinearLayout btn = new(this) { Orientation = Orientation.Horizontal };
        btn.SetGravity(GravityFlags.Center);
        btn.Background = primary
            ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, 99f)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 99f);
        OneUiTheme.AttachRecoil(btn, 0.92f);
        int icoPx = Dp(Math.Max(16, (int)(sizeDp * 0.44f)));
        iconOut = new OneUiIconView(this, icon, ColText);
        btn.AddView(iconOut, new LinearLayout.LayoutParams(icoPx, icoPx));
        btn.Click += (_, _) => onClick?.Invoke();
        return btn;
    }

    void HandleMusicSkipPrevious() {
        double cur = playerEngine?.CurrentSeconds ?? 0;
        if (activeLibraryQueue.Count <= 1) {
            EnsureActiveQueueForCurrentFile(LoadedFile?.Path ?? currentMediaMeta?.SourceMediaPath ?? "");
        }
        if (cur > 3.5 || activeLibraryQueue.Count <= 1) {
            if (cur > 0.8) {
                SeekAbsoluteSeconds(0);
            } else {
                Toast.MakeText(this, S["PlayerNoNextTrack"], ToastLength.Short).Show();
            }
            return;
        }
        PlayLibraryQueueStep(-1);
    }

    void HandleMusicSkipNext() {
        if (activeLibraryQueue.Count <= 1) {
            EnsureActiveQueueForCurrentFile(LoadedFile?.Path ?? currentMediaMeta?.SourceMediaPath ?? "");
        }
        if (activeLibraryQueue.Count <= 1) {
            Toast.MakeText(this, S["PlayerNoNextTrack"], ToastLength.Short).Show();
            return;
        }
        PlayLibraryQueueStep(+1);
    }

    void PlayLibraryQueueStep(int delta) {
        if (activeLibraryQueue.Count == 0) return;
        int nextIdx = activeLibraryQueueIndex + delta;
        if (nextIdx < 0) nextIdx = activeLibraryQueue.Count - 1;
        if (nextIdx >= activeLibraryQueue.Count) nextIdx = 0;
        activeLibraryQueueIndex = nextIdx;
        LibraryTrackItem item = activeLibraryQueue[nextIdx];
        OpenLibraryTrack(item, activeLibraryQueue, autoPlay: true);
    }

    void SetMusicPlayerSection(bool openLibrary) {
        isLibrarySectionOpen = openLibrary;
        UpdateSectionSwitcherStyle();

        if (musicNowPlayingSection != null && musicLibrarySection != null) {
            View entering = openLibrary ? musicLibrarySection : musicNowPlayingSection;
            View leaving = openLibrary ? musicNowPlayingSection : musicLibrarySection;

            leaving.Animate()?.Cancel();
            leaving.Visibility = ViewStates.Gone;

            entering.Visibility = ViewStates.Visible;
            entering.Alpha = 0f;
            entering.TranslationX = openLibrary ? Dp(16) : -Dp(16);
            entering.Animate()
                .Alpha(1f)
                .TranslationX(0f)
                .SetDuration(200)
                .SetInterpolator(new Android.Views.Animations.DecelerateInterpolator(1.6f))
                .Start();
        }

        if (openLibrary) {
            musicVideoStage?.PauseVideoDecoderOnly();
            if (librarySnapshot == null) {
                MediaLibrarySnapshot cached = MediaLibraryService.TryLoadCachedSnapshot(this);
                if (cached != null && cached.Tracks.Count > 0) {
                    librarySnapshot = cached;
                    RenderLibraryView();
                }
            } else {
                RenderLibraryView();
            }
            if (!isLibraryScanning) {
                RefreshLibraryScanAsync(force: false);
            }
        } else if (musicStageMode == PlayerStageMode.Video && activeTab == 0) {
            musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
        }
    }

    void UpdateSectionSwitcherStyle() {
        if (segNowPlayingBtn == null || segLibraryBtn == null) return;
        void Style(TextView tv, bool selected) {
            tv.Background = selected
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(228, 252, 252, 255), 999f)
                : null;
            tv.SetTextColor(selected ? Color.Rgb(18, 20, 26) : Color.Argb(210, 240, 242, 248));
        }
        Style(segNowPlayingBtn, !isLibrarySectionOpen);
        Style(segLibraryBtn, isLibrarySectionOpen);
    }

    void SetPlayerStageMode(PlayerStageMode mode) {
        if (currentMediaMeta?.HasVideo == true) {
            mode = PlayerStageMode.Video;
        } else if (mode == PlayerStageMode.Video) {
            mode = PlayerStageMode.Cover;
        }
        musicStageMode = mode;

        if (musicArtworkSlot != null) {
            musicArtworkSlot.Visibility = mode == PlayerStageMode.Cover ? ViewStates.Visible : ViewStates.Gone;
        }
        if (musicVideoSlot != null) {
            musicVideoSlot.Visibility = mode == PlayerStageMode.Video ? ViewStates.Visible : ViewStates.Gone;
            if (mode == PlayerStageMode.Video && activeTab == 0 && !isLibrarySectionOpen) {
                musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
            } else {
                musicVideoStage?.PauseVideoDecoderOnly();
            }
        }
        if (musicLyricsContainer != null) {
            musicLyricsContainer.Visibility = mode == PlayerStageMode.Lyrics ? ViewStates.Visible : ViewStates.Gone;
            if (mode == PlayerStageMode.Lyrics) {
                musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
            }
        }
        if (musicTitleRow != null) {
            musicTitleRow.Visibility = mode == PlayerStageMode.Lyrics ? ViewStates.Gone : ViewStates.Visible;
        }

        RefreshBottomActionBarVisuals();
    }

    void RefreshBottomActionBarVisuals() {
        if (musicBottomLeftBtn == null) return;

        bool hasVideo = currentMediaMeta?.HasVideo == true;
        if (hasVideo) {
            musicBottomLeftIcon.IconType = OneUiIconType.SubtitlesCc;
            bool subActive = activeSubtitleCues.Count > 0;
            musicBottomLeftBtn.Background = subActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(225, 252, 252, 255), 14f)
                : null;
            musicBottomLeftIcon.IconColor = subActive ? Color.Rgb(20, 22, 28) : Color.Argb(195, 252, 252, 255);
        } else {
            musicBottomLeftIcon.IconType = OneUiIconType.LyricsQuote;
            bool lyricsActive = musicStageMode == PlayerStageMode.Lyrics;
            musicBottomLeftBtn.Background = lyricsActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(225, 252, 252, 255), 14f)
                : null;
            musicBottomLeftIcon.IconColor = lyricsActive ? Color.Rgb(20, 22, 28) : Color.Argb(195, 252, 252, 255);
        }

        // Flanking spatial toggles: [Cavernize 3D] | [HRTF 3D / Direto] | [Matrix 7.1]
        void StyleSpatialPill(LinearLayout btn, OneUiIconView ico, TextView tv, bool on, Color accent, string labelText = null) {
            if (btn == null) return;
            if (labelText != null) tv.Text = labelText;
            btn.Background = on
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(accent, 96), 99f, OneUiTheme.WithAlpha(accent, 175), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(24, 255, 255, 255), 99f, Color.Argb(26, 255, 255, 255), 0.8f);
            Color fg = on ? OneUiTheme.TextPrimary : Color.Argb(150, 240, 242, 250);
            ico.IconColor = on ? Color.White : fg;
            tv.SetTextColor(fg);
        }

        StyleSpatialPill(musicCavernizeToggleBtn, musicCavernizeIcon, musicCavernizeText, store.CavernizeUpmix, OneUiTheme.IconAtmosUpmix);
        StyleSpatialPill(musicBottomSpatialBtn, musicBottomSpatialIcon, musicBottomSpatialText, store.HeadphoneVirtualizer, OneUiTheme.Primary,
            store.HeadphoneVirtualizer ? S["BentoHrtf"] : "Direto");
        StyleSpatialPill(musicMatrixToggleBtn, musicMatrixIcon, musicMatrixText, store.MatrixUpmixing, OneUiTheme.IconStudioExport);

        bool loop = playerEngine?.Loop ?? false;
        musicBottomLoopBtn.Background = loop
            ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(52, 255, 255, 255), 14f)
            : null;
        musicBottomLoopIcon.IconColor = loop ? OneUiTheme.TextPrimary : Color.Argb(180, 252, 252, 255);
    }

    static bool IsBitmapUsable(Bitmap bmp) {
        if (bmp == null) return false;
        try {
            return bmp.Handle != IntPtr.Zero && !bmp.IsRecycled && bmp.Width > 0 && bmp.Height > 0;
        } catch {
            return false;
        }
    }

    void RefreshMusicPlayerUi() {
        if (musicArtworkView == null) {
            return;
        }
        Color accent = currentMediaMeta?.PrimaryAccent ?? OneUiTheme.Primary;
        Color accent2 = currentMediaMeta?.SecondaryAccent ?? OneUiTheme.Secondary;
        backdropView?.SetMediaAccent(
            LoadedFile != null ? accent : null,
            LoadedFile != null ? accent2 : null);

        Bitmap activeArt = (LoadedFile != null && IsBitmapUsable(currentMediaMeta?.ArtworkBitmap))
            ? currentMediaMeta.ArtworkBitmap
            : (defaultFallbackCover ??= MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 512));
        musicArtworkView.SetArtwork(activeArt, LoadedFile != null ? accent : OneUiTheme.Primary);
        musicLyricsMiniArt?.SetArtwork(activeArt, LoadedFile != null ? accent : OneUiTheme.Primary);

        bool hasVideo = LoadedFile != null && currentMediaMeta?.HasVideo == true && !string.IsNullOrEmpty(currentMediaMeta.SourceMediaPath);
        if (hasVideo) {
            musicVideoStage?.BindVideoSource(currentMediaMeta.SourceMediaPath, currentMediaMeta.VideoWidth, currentMediaMeta.VideoHeight);
        } else {
            ExitVideoFullscreenIfNeeded();
            musicVideoStage?.ReleasePlayer();
        }

        musicLyricsView?.SetLyrics(currentMediaMeta?.Lyrics, S["NoLyricsFound"], accent);

        CavernizeTrack active = GetActiveTrack();
        string badgeStr;
        if (LoadedFile == null || active == null) {
            musicTitleText.Text = S["PlayerEmptyTitle"];
            musicArtistText.Text = S["PlayerEmptySub"];
            if (musicLyricsHeaderTitle != null) musicLyricsHeaderTitle.Text = S["PlayerEmptyTitle"];
            if (musicLyricsHeaderSub != null) musicLyricsHeaderSub.Text = S["ModeLyrics"];
            badgeStr = S["PlayerBadgeSpatial"];
            musicSpatialBadgeText.Text = badgeStr;
            musicTrackBtn.Visibility = ViewStates.Gone;
            musicWaveSeekBar?.SetThemeByColor(OneUiTheme.Tertiary);
        } else {
            string title = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                ? currentMediaMeta.Title
                : Path.GetFileNameWithoutExtension(LoadedFile.Path);
            musicTitleText.Text = title;

            string sub = string.Join(" — ", new[] { currentMediaMeta?.Artist, currentMediaMeta?.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
            musicArtistText.Text = !string.IsNullOrWhiteSpace(sub)
                ? sub
                : active.FormatHeader;

            if (musicLyricsHeaderTitle != null) {
                musicLyricsHeaderTitle.Text = title;
            }
            if (musicLyricsHeaderSub != null) {
                musicLyricsHeaderSub.Text = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
                    ? currentMediaMeta.Artist
                    : active.FormatHeader;
            }

            int objs = trackSourceItems.Count(x => x.IsDynamicObject);
            if (active.SourceCodec == Codec.TrueHD) {
                badgeStr = objs > 0
                    ? $"Dolby TrueHD Atmos • {objs} Obj"
                    : "Dolby TrueHD Lossless";
            } else {
                badgeStr = objs > 0
                    ? $"Dolby / Spatial 3D • {objs} Obj"
                    : active.Codec.ToString().ToUpperInvariant();
            }
            if (activeLibraryQueue.Count > 1 && activeLibraryQueueIndex >= 0) {
                badgeStr = $"{badgeStr} • {activeLibraryQueueIndex + 1}/{activeLibraryQueue.Count}";
            }
            musicSpatialBadgeText.Text = badgeStr;

            if (LoadedFile.Tracks.Count > 1) {
                int idx = Math.Max(0, LoadedFile.Tracks.ToList().IndexOf(selectedTrack));
                musicTrackBtnText.Text = $"{idx + 1}/{LoadedFile.Tracks.Count}";
                musicTrackBtn.Visibility = ViewStates.Visible;
            } else {
                musicTrackBtn.Visibility = ViewStates.Gone;
            }

            bool hasArt = currentMediaMeta?.HasEmbeddedArtwork == true;
            Color sliderTone = hasArt ? accent : OneUiTheme.Tertiary;
            musicWaveSeekBar?.SetThemeByColor(sliderTone);
        }

        if (hasVideo) {
            string trkStr = LoadedFile.Tracks.Count > 1
                ? $"{Math.Max(1, LoadedFile.Tracks.ToList().IndexOf(selectedTrack) + 1)}/{LoadedFile.Tracks.Count}"
                : "";
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                badgeStr,
                trkStr,
                LoadedFile.Tracks.Count > 1,
                activeSubtitleCues.Count > 0,
                playerEngine?.Loop ?? false,
                S["VideoUnlockHint"]);
        }

        string boundKey = LoadedFile != null ? (currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "") : "";
        if (!string.Equals(lastStageBoundPath, boundKey, StringComparison.Ordinal)) {
            lastStageBoundPath = boundKey;
            SetPlayerStageMode(hasVideo ? PlayerStageMode.Video : PlayerStageMode.Cover);
        } else {
            SetPlayerStageMode(musicStageMode);
        }

        RefreshMusicPlayerControlsUi();
    }

    void RefreshMusicPlayerControlsUi() {
        if (musicWaveSeekBar == null) {
            return;
        }
        bool isPlaying = playerEngine?.IsPlaying ?? false;
        double curSec = playerEngine?.CurrentSeconds ?? 0;
        double totSec = playerEngine?.TotalSeconds ?? 0;

        musicArtworkView?.SetPlaying(isPlaying);
        if (musicStageMode == PlayerStageMode.Video && ((activeTab == 0 && !isLibrarySectionOpen) || fullscreenVideoDlg != null)) {
            musicVideoStage?.SyncPlaybackState(isPlaying, curSec, totSec);
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                musicSpatialBadgeText.Text,
                musicTrackBtnText?.Text ?? "",
                LoadedFile?.Tracks.Count > 1,
                activeSubtitleCues.Count > 0,
                playerEngine?.Loop ?? false,
                S["VideoUnlockHint"]);
        } else if (musicStageMode == PlayerStageMode.Lyrics && activeTab == 0 && !isLibrarySectionOpen) {
            musicLyricsView?.UpdatePlayback(curSec, isPlaying);
        }
        if (musicPlayIcon != null) {
            musicPlayIcon.IconType = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
        }

        RefreshBottomActionBarVisuals();
    }

    void StartBackgroundOnlineEnrichment(MediaArtworkMetadata meta) {
        if (meta == null) return;
        int myGen = ++onlineEnrichGeneration;
        Task.Run(async () => {
            try {
                OnlineEnrichResult res = await OnlineMetadataService.AutoEnrichIfMissingAsync(this, meta).ConfigureAwait(false);
                if (res == null) return;

                RunOnUiThread(() => {
                    if (uiDestroyed || myGen != onlineEnrichGeneration || currentMediaMeta != meta) {
                        res.ArtworkBitmap?.Dispose();
                        return;
                    }
                    bool changed = false;
                    if (res.ArtworkBitmap != null) {
                        meta.ArtworkBitmap = res.ArtworkBitmap;
                        meta.HasEmbeddedArtwork = true;
                        MediaMetadataHelper.ExtractPalette(res.ArtworkBitmap, out Color p, out Color s);
                        meta.PrimaryAccent = p;
                        meta.SecondaryAccent = s;
                        changed = true;
                    }
                    if (res.Lyrics != null && res.Lyrics.HasLyrics) {
                        meta.Lyrics = res.Lyrics;
                        changed = true;
                    }
                    if (changed) {
                        RefreshMusicPlayerUi();
                        mediaNotification?.UpdateMetadata(
                            meta.Title,
                            string.IsNullOrWhiteSpace(meta.Artist) ? "Cavernize 3D" : meta.Artist,
                            meta.Album ?? "",
                            playerEngine?.TotalSeconds ?? 0,
                            meta.ArtworkBitmap);
                    }
                });
            } catch (Exception ex) {
                AppLogger.Warn($"Online auto-enrich: {ex.Message}");
            }
        });
    }

    // ============================================================
    // IN-APP LIBRARY UI (Samsung Music Albums/Artists + Poweramp Folders/Badges)
    // ============================================================
    LinearLayout BuildInAppLibrarySection(Typeface sfBold, Typeface sfSemi, Typeface sfReg) {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };

        // Top Search Bar + Refresh + System Picker shortcut
        LinearLayout searchRow = HRow();
        searchRow.SetPadding(0, Dp(2), 0, Dp(10));

        LinearLayout searchPill = HRow();
        searchPill.SetPadding(Dp(12), Dp(6), Dp(10), Dp(6));
        searchPill.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 999f);
        OneUiIconView srchIco = new(this, OneUiIconType.Search, OneUiTheme.TextMuted);
        librarySearchInput = new EditText(this) {
            Hint = S["LibSearchHint"],
            TextSize = 12.5f
        };
        librarySearchInput.SetTypeface(sfReg, TypefaceStyle.Normal);
        librarySearchInput.SetTextColor(ColText);
        librarySearchInput.SetHintTextColor(ColMuted);
        librarySearchInput.SetBackgroundColor(Color.Transparent);
        librarySearchInput.SetSingleLine(true);
        librarySearchInput.SetPadding(0, Dp(2), 0, Dp(2));
        librarySearchInput.TextChanged += (_, _) => {
            librarySearchQuery = librarySearchInput.Text ?? "";
            libraryRenderLimit = 72;
            RenderLibraryView();
        };
        searchPill.AddView(srchIco, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(8) });
        searchPill.AddView(librarySearchInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        searchRow.AddView(searchPill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        FrameLayout refreshBtn = MakeAppleCircleIconBtn(OneUiIconType.Refresh, () => RefreshLibraryScanAsync(force: true));
        searchRow.AddView(refreshBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(6) });

        FrameLayout sysPickBtn = MakeAppleCircleIconBtn(OneUiIconType.Folder, () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false));
        searchRow.AddView(sysPickBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        box.AddView(searchRow);

        // Category pills: Álbuns | Faixas | Artistas | Pastas
        HorizontalScrollView catScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout catRow = HRow();
        catRow.SetPadding(0, 0, 0, Dp(10));
        (LibraryCategory cat, string title)[] cats = [
            (LibraryCategory.Albums, S["LibAlbums"]),
            (LibraryCategory.Tracks, S["LibTracks"]),
            (LibraryCategory.Artists, S["LibArtists"]),
            (LibraryCategory.Folders, S["LibFolders"])
        ];
        libCatButtons = new Button[cats.Length];
        for (int i = 0; i < cats.Length; i++) {
            int idx = i;
            LibraryCategory c = cats[i].cat;
            Button b = PillButton(cats[i].title, () => {
                libraryCategory = c;
                selectedLibraryAlbum = null;
                selectedLibraryArtist = null;
                selectedLibraryFolder = null;
                libraryRenderLimit = 72;
                UpdateLibraryCatStyles();
                RenderLibraryView();
            });
            b.TextSize = 11.8f;
            b.SetTypeface(sfSemi, TypefaceStyle.Bold);
            b.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
            b.SetMinimumHeight(Dp(34));
            libCatButtons[idx] = b;
            catRow.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        }
        catScroll.AddView(catRow);
        box.AddView(catScroll);

        // Spatial / Surround Filter Chips: [ Todas ] [ Espacial / Surround ] [ Só Espacial / Atmos ]
        HorizontalScrollView filtScroll = new(this) { HorizontalScrollBarEnabled = false };
        LinearLayout filtRow = HRow();
        filtRow.SetPadding(0, 0, 0, Dp(8));
        (int mode, string label)[] filters = [
            (0, S["LibFilterAll"]),
            (1, S["LibFilterSurround"]),
            (2, S["LibFilterSpatialOnly"])
        ];
        libFilterButtons = new Button[filters.Length];
        for (int i = 0; i < filters.Length; i++) {
            int fMode = filters[i].mode;
            int idx = i;
            Button fb = PillButton(filters[i].label, () => {
                store.LibraryFilterMode = fMode;
                libraryRenderLimit = 72;
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            });
            fb.TextSize = 11f;
            fb.SetTypeface(sfSemi, TypefaceStyle.Bold);
            fb.SetPadding(Dp(12), Dp(4), Dp(12), Dp(4));
            fb.SetMinimumHeight(Dp(30));
            libFilterButtons[idx] = fb;
            filtRow.AddView(fb, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        }
        filtScroll.AddView(filtRow);
        box.AddView(filtScroll);

        libraryStatusText = Label("", 11.5f, OneUiTheme.TextMuted);
        libraryStatusText.SetPadding(Dp(4), 0, Dp(4), Dp(8));
        box.AddView(libraryStatusText);

        libraryListHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.AddView(libraryListHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        UpdateLibraryCatStyles();
        UpdateLibraryFilterStyles();
        return box;
    }

    void UpdateLibraryFilterStyles() {
        if (libFilterButtons == null) return;
        for (int i = 0; i < libFilterButtons.Length; i++) {
            bool sel = store.LibraryFilterMode == i;
            libFilterButtons[i].Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 145), OneUiTheme.PillCornerDp, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            libFilterButtons[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSub);
        }
    }

    void UpdateLibraryCatStyles() {
        if (libCatButtons == null) return;
        LibraryCategory[] cats = [LibraryCategory.Albums, LibraryCategory.Tracks, LibraryCategory.Artists, LibraryCategory.Folders];
        for (int i = 0; i < libCatButtons.Length && i < cats.Length; i++) {
            bool sel = libraryCategory == cats[i];
            libCatButtons[i].Background = sel
                ? OneUiTheme.CreateRippleRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        }
    }

    void RefreshLibraryScanAsync(bool force) {
        if (isLibraryScanning) return;
        isLibraryScanning = true;
        isIncrementalSyncRunning = true;
        if (librarySnapshot == null && libraryStatusText != null) {
            libraryStatusText.Text = S["LibScanning"];
        }
        Task.Run(async () => {
            try {
                var (snap, added, updated, removed) = await MediaLibraryService.ScanIncrementalAsync(this, force).ConfigureAwait(false);
                RunOnUiThread(() => {
                    isLibraryScanning = false;
                    isIncrementalSyncRunning = false;
                    if (uiDestroyed) return;
                    bool firstLoad = librarySnapshot == null;
                    bool changed = firstLoad || force || added > 0 || updated > 0 || removed > 0;
                    librarySnapshot = snap;
                    if (changed || isLibrarySectionOpen) {
                        RenderLibraryView();
                        if ((added > 0 || updated > 0 || removed > 0) && libraryStatusText != null) {
                            int spatialCount = snap.Tracks.Count(t => t.IsSurround);
                            libraryStatusText.Text = string.Format(S["LibIncrementalUpdate"], snap.Tracks.Count, spatialCount, added + updated, removed);
                        }
                    }
                });
            } catch (Exception ex) {
                AppLogger.Error("RefreshLibraryScanAsync", ex);
                RunOnUiThread(() => {
                    isLibraryScanning = false;
                    isIncrementalSyncRunning = false;
                });
            }
        });
    }

    void RenderLibraryView() {
        if (libraryListHost == null) return;
        libraryListHost.RemoveAllViews();

        if (librarySnapshot == null) {
            libraryStatusText.Text = isLibraryScanning ? S["LibScanning"] : S["LibEmpty"];
            return;
        }

        string q = (librarySearchQuery ?? "").Trim();
        bool hasQuery = !string.IsNullOrEmpty(q);
        int filterMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        int totalSpatial = librarySnapshot.Tracks.Count(t => t.IsSurround);
        string cacheTag = isIncrementalSyncRunning ? "Sincronizando..." : string.Format(S["LibCacheSynced"], librarySnapshot.Tracks.Count, totalSpatial);

        // Detail Drill-Down inside Album / Artist / Folder
        if (selectedLibraryAlbum != null) {
            RenderAlbumDetailView(selectedLibraryAlbum);
            return;
        }
        if (selectedLibraryFolder != null) {
            List<LibraryTrackItem> fldTracks = selectedLibraryFolder.Tracks
                .Where(t => MediaLibraryService.PassesFilter(t, filterMode))
                .ToList();
            RenderGroupTracksDrilldown(
                selectedLibraryFolder.FolderName,
                $"{fldTracks.Count} faixa(s) • {selectedLibraryFolder.FolderPath}",
                fldTracks,
                () => { selectedLibraryFolder = null; RenderLibraryView(); });
            return;
        }
        if (!string.IsNullOrEmpty(selectedLibraryArtist)) {
            LibraryArtistGroup art = librarySnapshot.Artists.FirstOrDefault(a => string.Equals(a.Artist, selectedLibraryArtist, StringComparison.OrdinalIgnoreCase));
            if (art != null) {
                List<LibraryTrackItem> artTracks = art.Tracks
                    .Where(t => MediaLibraryService.PassesFilter(t, filterMode))
                    .ToList();
                RenderGroupTracksDrilldown(
                    art.Artist,
                    string.Format(S["LibSongsCount"], artTracks.Count),
                    artTracks,
                    () => { selectedLibraryArtist = null; RenderLibraryView(); });
                return;
            }
        }

        if (libraryCategory == LibraryCategory.Albums) {
            List<LibraryAlbumGroup> filtered = librarySnapshot.Albums
                .Where(a => a.Tracks.Any(t => MediaLibraryService.PassesFilter(t, filterMode)))
                .Where(a => !hasQuery ||
                    a.AlbumTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    a.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            libraryStatusText.Text = $"{filtered.Count} {S["LibAlbums"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(filterMode > 0 && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i += 2) {
                LinearLayout gridRow = new(this) {
                    Orientation = Orientation.Horizontal,
                    BaselineAligned = false
                };
                gridRow.AddView(BuildAlbumTileCard(filtered[i]), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (i + 1 < showCount) {
                    gridRow.AddView(BuildAlbumTileCard(filtered[i + 1]), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                } else {
                    gridRow.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1f));
                }
                libraryListHost.AddView(gridRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else if (libraryCategory == LibraryCategory.Tracks) {
            List<LibraryTrackItem> filtered = librarySnapshot.Tracks
                .Where(t => MediaLibraryService.PassesFilter(t, filterMode))
                .Where(t => !hasQuery ||
                    t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    t.Album.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            libraryStatusText.Text = $"{string.Format(S["LibSongsCount"], filtered.Count)} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(filterMode > 0 && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                libraryListHost.AddView(BuildLibraryTrackRow(filtered[i], filtered, i), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else if (libraryCategory == LibraryCategory.Artists) {
            List<LibraryArtistGroup> filtered = librarySnapshot.Artists
                .Where(a => a.Tracks.Any(t => MediaLibraryService.PassesFilter(t, filterMode)))
                .Where(a => !hasQuery || a.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            libraryStatusText.Text = $"{filtered.Count} {S["LibArtists"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(filterMode > 0 && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                LibraryArtistGroup art = filtered[i];
                List<LibraryTrackItem> visTracks = art.Tracks.Where(t => MediaLibraryService.PassesFilter(t, filterMode)).ToList();
                LinearLayout row = BuildGroupRow(
                    art.Artist,
                    $"{art.AlbumCount} álbum(ns) • {visTracks.Count} faixa(s)",
                    visTracks.FirstOrDefault() ?? art.Tracks.FirstOrDefault(),
                    () => { selectedLibraryArtist = art.Artist; RenderLibraryView(); });
                libraryListHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else {
            List<LibraryFolderGroup> filtered = librarySnapshot.Folders
                .Where(f => f.Tracks.Any(t => MediaLibraryService.PassesFilter(t, filterMode)))
                .Where(f => !hasQuery ||
                    f.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            libraryStatusText.Text = $"{filtered.Count} {S["LibFolders"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(filterMode > 0 && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                LibraryFolderGroup fld = filtered[i];
                List<LibraryTrackItem> visTracks = fld.Tracks.Where(t => MediaLibraryService.PassesFilter(t, filterMode)).ToList();
                LinearLayout row = BuildGroupRow(
                    fld.FolderName,
                    $"{visTracks.Count} faixa(s) • {fld.FolderPath}",
                    visTracks.FirstOrDefault() ?? fld.Tracks.FirstOrDefault(),
                    () => { selectedLibraryFolder = fld; RenderLibraryView(); },
                    isFolder: true);
                libraryListHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        }
    }

    void AddLoadMoreIfNeeded(int totalCount, int shown) {
        if (shown >= totalCount) return;
        Button more = PillButton($"Mostrar mais ({totalCount - shown} restantes)", () => {
            libraryRenderLimit += 72;
            RenderLibraryView();
        });
        libraryListHost.AddView(more, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6)
        });
    }

    void RenderLibraryEmptyCard(bool isFilteredEmpty = false) {
        LinearLayout card = Card();
        TextView tv = Label(isFilteredEmpty ? S["LibEmptyFiltered"] : S["LibEmpty"], 12.5f, OneUiTheme.TextSub);
        tv.SetPadding(0, 0, 0, Dp(10));
        card.AddView(tv);
        if (isFilteredEmpty) {
            Button showAllBtn = PrimaryButton(S["LibFilterAll"], () => {
                store.LibraryFilterMode = 0;
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            });
            card.AddView(showAllBtn);
        } else {
            Button openPicker = PrimaryButton(S["LibSystemPicker"], () => LaunchFilePicker(ReqOpenMedia, allowMultiple: false));
            card.AddView(openPicker);
        }
        libraryListHost.AddView(card);
    }

    LinearLayout BuildAlbumTileCard(LibraryAlbumGroup alb) {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfReg = OneUiTheme.GetAppleFontRegular(this);

        LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
        tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 18f);
        tile.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        OneUiTheme.AttachRecoil(tile, 0.96f);

        RoundedArtworkView art = new(this) {
            PreferredSizeDp = 148,
            CornerDp = 12f
        };
        BindThumbAsync(art, alb.Tracks.FirstOrDefault(), alb.AlbumTitle);
        tile.AddView(art, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView titleTv = Label(alb.AlbumTitle, 13f, ColText);
        titleTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(titleTv);

        TextView subTv = Label($"{alb.Artist} • {alb.Tracks.Count}", 11f, OneUiTheme.TextMuted);
        subTv.SetTypeface(sfReg, TypefaceStyle.Normal);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(subTv);

        tile.Click += (_, _) => {
            selectedLibraryAlbum = alb;
            RenderLibraryView();
        };
        return tile;
    }

    void RenderAlbumDetailView(LibraryAlbumGroup alb) {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        List<LibraryTrackItem> tracks = alb.Tracks
            .Where(t => MediaLibraryService.PassesFilter(t, store.LibraryFilterMode))
            .ToList();
        if (tracks.Count == 0) tracks = alb.Tracks;
        libraryStatusText.Text = $"{alb.AlbumTitle} — {tracks.Count} faixa(s)";

        LinearLayout hdrCard = Card();
        LinearLayout backRow = HRow();
        backRow.SetPadding(0, 0, 0, Dp(10));
        LinearLayout backPill = OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["LibAlbums"], () => {
            selectedLibraryAlbum = null;
            RenderLibraryView();
        });
        backRow.AddView(backPill);
        hdrCard.AddView(backRow);

        LinearLayout hero = HRow();
        RoundedArtworkView cover = new(this) { PreferredSizeDp = 88, CornerDp = 14f };
        BindThumbAsync(cover, tracks.FirstOrDefault(), alb.AlbumTitle);
        hero.AddView(cover, new LinearLayout.LayoutParams(Dp(88), Dp(88)) { RightMargin = Dp(12) });

        LinearLayout meta = new(this) { Orientation = Orientation.Vertical };
        TextView tTv = Label(alb.AlbumTitle, 16f, ColText);
        tTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        TextView aTv = Label(alb.Artist, 13f, OneUiTheme.PeachText);
        meta.AddView(tTv);
        meta.AddView(aTv);

        Button playAllBtn = PrimaryButton(S["Play"], () => {
            if (tracks.Count > 0) {
                OpenLibraryTrack(tracks[0], tracks, autoPlay: true);
            }
        });
        playAllBtn.SetMinimumHeight(Dp(34));
        playAllBtn.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        meta.AddView(playAllBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        hero.AddView(meta, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        hdrCard.AddView(hero);
        libraryListHost.AddView(hdrCard);

        for (int i = 0; i < tracks.Count; i++) {
            libraryListHost.AddView(BuildLibraryTrackRow(tracks[i], tracks, i, showThumb: false),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }
    }

    void RenderGroupTracksDrilldown(string title, string sub, List<LibraryTrackItem> tracks, Action onBack) {
        libraryStatusText.Text = $"{title} • {sub}";
        LinearLayout topRow = HRow();
        topRow.SetPadding(0, 0, 0, Dp(10));
        topRow.AddView(OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["PlayerLibrary"], onBack));
        libraryListHost.AddView(topRow);

        for (int i = 0; i < Math.Min(tracks.Count, libraryRenderLimit); i++) {
            libraryListHost.AddView(BuildLibraryTrackRow(tracks[i], tracks, i),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }
    }

    LinearLayout BuildGroupRow(string title, string sub, LibraryTrackItem sample, Action onClick, bool isFolder = false) {
        LinearLayout row = HRow();
        row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 18f);
        row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        OneUiTheme.AttachRecoil(row, 0.97f);

        if (isFolder) {
            row.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Folder, OneUiTheme.IconStudioExport, 40),
                new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(12) });
        } else {
            RoundedArtworkView thumb = new(this) { PreferredSizeDp = 42, CornerDp = 10f };
            BindThumbAsync(thumb, sample, title);
            row.AddView(thumb, new LinearLayout.LayoutParams(Dp(42), Dp(42)) { RightMargin = Dp(12) });
        }

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView tv = Label(title, 13.5f, ColText);
        tv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        tv.SetSingleLine(true);
        tv.Ellipsize = TextUtils.TruncateAt.End;
        TextView stv = Label(sub, 11f, OneUiTheme.TextMuted);
        stv.SetSingleLine(true);
        stv.Ellipsize = TextUtils.TruncateAt.End;
        col.AddView(tv);
        col.AddView(stv);

        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.Click += (_, _) => onClick?.Invoke();
        return row;
    }

    LinearLayout BuildLibraryTrackRow(LibraryTrackItem item, IReadOnlyList<LibraryTrackItem> queue, int indexInQueue, bool showThumb = true) {
        bool isPlayingThis = LoadedFile != null &&
            string.Equals(LoadedFile.Path, item.FilePath, StringComparison.OrdinalIgnoreCase);

        LinearLayout row = HRow();
        row.Background = isPlayingThis
            ? OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.WithAlpha(ColAccent, 64), 16f, ColAccent)
            : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
        row.SetPadding(Dp(10), Dp(9), Dp(10), Dp(9));
        OneUiTheme.AttachRecoil(row, 0.97f);

        if (showThumb) {
            RoundedArtworkView thumb = new(this) { PreferredSizeDp = 44, CornerDp = 10f };
            BindThumbAsync(thumb, item, item.Title);
            row.AddView(thumb, new LinearLayout.LayoutParams(Dp(44), Dp(44)) { RightMargin = Dp(10) });
        } else {
            string numStr = item.TrackNumber > 0 ? $"{item.TrackNumber}" : $"{indexInQueue + 1}";
            TextView numTv = Label(numStr, 12.5f, isPlayingThis ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
            numTv.Gravity = GravityFlags.Center;
            numTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
            row.AddView(numTv, new LinearLayout.LayoutParams(Dp(26), ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
        }

        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
        TextView titleTv = Label(item.Title, 13.5f, isPlayingThis ? OneUiTheme.PrimaryHigh : ColText);
        titleTv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;

        string subLine = string.Join(" — ", new[] { item.Artist, item.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(subLine)) subLine = item.FolderName;
        TextView subTv = Label(subLine, 11f, OneUiTheme.TextMuted);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.End;
        col.AddView(titleTv);
        col.AddView(subTv);
        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        // Spatial / Container format badge & duration column
        LinearLayout rightCol = new(this) { Orientation = Orientation.Vertical };
        rightCol.SetGravity(GravityFlags.End);
        string badgeLabel = !string.IsNullOrEmpty(item.AudioBadge) && item.AudioBadge != item.FormatBadge
            ? $"{item.FormatBadge} • {item.AudioBadge}"
            : item.FormatBadge;
        Color badgeTextCol = item.IsSpatial
            ? OneUiTheme.PrimaryHigh
            : (item.IsSurround ? OneUiTheme.PeachText : OneUiTheme.TextMuted);
        Color badgeBgCol = item.IsSpatial
            ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 52)
            : Color.Argb(40, 255, 255, 255);
        TextView fmtTv = Label(badgeLabel, 9.5f, badgeTextCol);
        fmtTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        fmtTv.SetSingleLine(true);
        fmtTv.SetPadding(Dp(6), Dp(2), Dp(6), Dp(2));
        fmtTv.Background = OneUiTheme.CreateRoundedDrawable(this, badgeBgCol, 6f);
        rightCol.AddView(fmtTv);

        if (item.DurationMs > 0) {
            TimeSpan ts = TimeSpan.FromMilliseconds(item.DurationMs);
            TextView durTv = Label(ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss"), 10f, OneUiTheme.TextMuted);
            durTv.SetPadding(0, Dp(2), 0, 0);
            rightCol.AddView(durTv);
        }
        row.AddView(rightCol);

        row.Click += (_, _) => OpenLibraryTrack(item, queue, autoPlay: true);
        return row;
    }

    async void BindThumbAsync(RoundedArtworkView target, LibraryTrackItem item, string seedTitle) {
        if (target == null) return;
        Bitmap cached = item != null ? MediaLibraryService.TryGetCachedThumbnail(item.CacheKey) : null;
        if (IsBitmapUsable(cached)) {
            target.SetArtwork(cached, OneUiTheme.Primary);
            return;
        }
        target.SetArtwork(defaultFallbackCover ??= MediaMetadataHelper.CreateSpatialFallbackArtwork("Cavernize", OneUiTheme.PrimaryHigh, OneUiTheme.Secondary, 256), OneUiTheme.Primary);
        if (item == null) return;

        string expectedKey = item.CacheKey;
        target.Tag = expectedKey;
        try {
            Bitmap loaded = await MediaLibraryService.LoadThumbnailAsync(this, item, 220).ConfigureAwait(false);
            if (!IsBitmapUsable(loaded) || uiDestroyed) return;
            RunOnUiThread(() => {
                if (uiDestroyed || !IsBitmapUsable(loaded)) return;
                if (string.Equals(target.Tag?.ToString(), expectedKey, StringComparison.Ordinal)) {
                    target.SetArtwork(loaded, OneUiTheme.Primary);
                }
            });
        } catch {
            // Keep fallback cover safely
        }
    }

    void OpenLibraryTrack(LibraryTrackItem item, IReadOnlyList<LibraryTrackItem> queue, bool autoPlay) {
        if (item == null) return;
        activeLibraryQueue.Clear();
        if (queue != null) {
            activeLibraryQueue.AddRange(queue);
            activeLibraryQueueIndex = activeLibraryQueue.IndexOf(item);
        } else {
            activeLibraryQueue.Add(item);
            activeLibraryQueueIndex = 0;
        }
        currentMediaUri = item.ContentUri;
        SetMusicPlayerSection(openLibrary: false);
        OpenLocalPathWithFallbackAsync(item.FilePath, switchToTab: 0, autoPlay: autoPlay);
    }

    // ============================================================
    // ONLINE METADATA, COVER ART, SYNCED LYRICS & SUBTITLE SEARCH
    // ============================================================
    void ShowOnlineMetadataDialog() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        string defaultQuery = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist)
            ? $"{currentMediaMeta.Artist} {currentMediaMeta.Title}"
            : (currentMediaMeta?.Title ?? (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : ""));

        EditText queryInput = new(this) {
            Text = OnlineMetadataService.CleanSearchTerm(defaultQuery),
            Hint = S["OnlineSearchQueryHint"],
            TextSize = 13.5f
        };
        queryInput.SetTextColor(ColText);
        queryInput.SetHintTextColor(ColMuted);
        queryInput.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        queryInput.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
        box.AddView(queryInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(box, S["OnlineAutoBestSwitch"], store.OnlineAutoSelectBestMatch, v => store.OnlineAutoSelectBestMatch = v);

        LinearLayout resultsHost = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void ApplyCoverCandidate(OnlineCoverCandidate c, string fallbackTitle) {
            dlg?.Dismiss();
            string tName = currentMediaMeta?.Title ?? fallbackTitle;
            string arName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist) ? currentMediaMeta.Artist : c.Artist;
            string alName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Album) ? currentMediaMeta.Album : c.Album;
            string pathOvr = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path;
            bool autoPicked = store.OnlineAutoSelectBestMatch && c.IsExactMatch;
            Task.Run(async () => {
                var (bmp, _) = await OnlineMetadataService.DownloadAndCacheCoverAsync(
                    this, c.ImageUrl, tName, arName, alName, filePathOverride: pathOvr).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed || !IsBitmapUsable(bmp)) return;
                    if (currentMediaMeta != null) {
                        currentMediaMeta.ArtworkBitmap = bmp;
                        currentMediaMeta.HasEmbeddedArtwork = true;
                        MediaMetadataHelper.ExtractPalette(bmp, out Color p, out Color s);
                        currentMediaMeta.PrimaryAccent = p;
                        currentMediaMeta.SecondaryAccent = s;
                    }
                    RefreshMusicPlayerUi();
                    mediaNotification?.UpdateMetadata(
                        currentMediaMeta?.Title ?? tName,
                        string.IsNullOrWhiteSpace(arName) ? "Cavernize 3D" : arName,
                        alName ?? "",
                        playerEngine?.TotalSeconds ?? 0,
                        bmp);
                    string msg = autoPicked
                        ? string.Format(S["OnlineAutoBestAppliedCover"], c.Source, c.Artist, c.Title)
                        : S["OnlineAppliedCover"];
                    Toast.MakeText(this, msg, ToastLength.Short).Show();
                });
            });
        }

        void ApplyLyricsCandidate(OnlineLyricsCandidate c, bool autoPicked) {
            dlg?.Dismiss();
            string tName = currentMediaMeta?.Title ?? c.TrackName;
            string arName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist) ? currentMediaMeta.Artist : c.ArtistName;
            OnlineMetadataService.SaveLyricsToCache(this, tName, arName, c.BestContent);
            LyricsDocument doc = LyricsService.ParseLyricsText(c.BestContent, c.Source);
            if (currentMediaMeta != null) {
                currentMediaMeta.Lyrics = doc;
            }
            RefreshMusicPlayerUi();
            SetPlayerStageMode(PlayerStageMode.Lyrics);
            string msg = autoPicked
                ? string.Format(S["OnlineAutoBestAppliedLyrics"], c.Source, c.ArtistName, c.TrackName)
                : S["OnlineAppliedLyrics"];
            Toast.MakeText(this, msg, ToastLength.Short).Show();
        }

        void RunCoverSearch() {
            string q = queryInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            string searchTitle = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : q;
            string searchArtist = currentMediaMeta?.Artist ?? "";
            string searchAlbum = currentMediaMeta?.Album ?? "";
            bool isVid = currentMediaMeta?.HasVideo == true;

            Task.Run(async () => {
                List<OnlineCoverCandidate> covers = await OnlineMetadataService.SearchCoverCandidatesAsync(
                    q, searchArtist, searchAlbum, isVid).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (covers.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineCoverCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? covers.FirstOrDefault(c => c.IsExactMatch)
                        : null;
                    if (exactBest != null) {
                        ApplyCoverCandidate(exactBest, searchTitle);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineCoverCandidate c in covers) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = c.IsExactMatch ? " • [Exata]" : "";
                        TextView t1 = Label($"{c.Title}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{c.Artist} • {c.Album} ({c.Source})", 11f, c.IsExactMatch ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplyCoverCandidate(c, searchTitle);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        void RunLyricsSearch() {
            string q = queryInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            string titleArg = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : q;
            string artistArg = currentMediaMeta?.Artist ?? "";
            string albumArg = currentMediaMeta?.Album ?? "";
            double durArg = playerEngine?.TotalSeconds ?? 0;

            Task.Run(async () => {
                List<OnlineLyricsCandidate> cands = await OnlineMetadataService.SearchLyricsAsync(
                    titleArg, artistArg, albumArg, expectedDurationSec: durArg).ConfigureAwait(false);
                if (cands.Count == 0 && !string.Equals(titleArg, q, StringComparison.OrdinalIgnoreCase)) {
                    cands = await OnlineMetadataService.SearchLyricsAsync(q, "", "", expectedDurationSec: durArg).ConfigureAwait(false);
                }
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (cands.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineLyricsCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? cands.FirstOrDefault(c => c.IsExactMatch)
                        : null;
                    if (exactBest != null) {
                        ApplyLyricsCandidate(exactBest, autoPicked: true);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineLyricsCandidate c in cands) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string syncBadge = c.IsSynced ? $"[Sincronizada • {c.Source}]" : $"[Texto • {c.Source}]";
                        string exactTag = c.IsExactMatch ? " • [Exata]" : "";
                        TextView t1 = Label($"{c.TrackName} — {c.ArtistName}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{syncBadge} • {c.AlbumName}", 11f, c.IsSynced ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplyLyricsCandidate(c, autoPicked: false);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        LinearLayout actionBtns = HRow();
        actionBtns.SetPadding(0, 0, 0, Dp(10));
        Button findCoverBtn = PrimaryButton(S["OnlineCoverBtn"], RunCoverSearch);
        Button findLyricsBtn = PillButton(S["OnlineLyricsBtn"], RunLyricsSearch);
        actionBtns.AddView(findCoverBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        actionBtns.AddView(findLyricsBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        box.AddView(actionBtns);
        box.AddView(resultsHost);

        dlg = OneUiModalDialog.Show(
            this,
            S["OnlineSearchTitle"],
            null,
            icon: OneUiIconType.Globe,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: box);
    }

    void ShowPlayerMoreSheet() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        CavernizeTrack active = GetActiveTrack();
        string tel = active == null
            ? $"{RenderTarget?.Name ?? "Binaural"} • {Math.Round(store.PlayerVolume * 100)}%"
            : $"{active.FormatHeader} • {trackSourceItems.Count(x => x.IsBed)} ch + {trackSourceItems.Count(x => x.IsDynamicObject)} obj • {Math.Round(store.PlayerVolume * 100)}%";
        TextView telTv = Label(tel, 12f, OneUiTheme.PeachText);
        telTv.SetPadding(Dp(4), 0, Dp(4), Dp(12));
        box.AddView(telTv);

        AddSwitchRow(box, S["HeadphoneVirt"], store.HeadphoneVirtualizer, v => {
            store.HeadphoneVirtualizer = v;
            playerEngine.HeadphoneVirtualizer = v;
            refreshBentoTilesUi?.Invoke();
            RefreshMusicPlayerControlsUi();
        });
        AddSwitchRow(box, "Cavernize 3D", store.CavernizeUpmix, v => {
            store.CavernizeUpmix = v;
            if (cavernizeUpmixSwitch != null) cavernizeUpmixSwitch.Checked = v;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        });
        AddSwitchRow(box, S["PlayerQuickMatrix"], store.MatrixUpmixing, v => {
            store.MatrixUpmixing = v;
            if (matrixUpmixSwitch != null) matrixUpmixSwitch.Checked = v;
            ReattachPlayerTrack();
            RefreshMusicPlayerControlsUi();
        });

        AddDivider(box);

        Button onlineBtn = PillButton(S["OnlineSearchTitle"], () => {
            dlg?.Dismiss();
            ShowOnlineMetadataDialog();
        });
        box.AddView(onlineBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

        if (currentMediaMeta?.HasVideo == true) {
            Button subBtn = PillButton(S["VideoSubtitlesTitle"], () => {
                dlg?.Dismiss();
                ShowSubtitlesDialog();
            });
            box.AddView(subBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });
        }

        if (LoadedFile != null && LoadedFile.Tracks.Count > 1) {
            Button trkBtn = PillButton(S["SelectTrackHeader"], () => {
                dlg?.Dismiss();
                ShowTrackSelectorDialog();
            });
            box.AddView(trkBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });
        }

        Button roomBtn = PrimaryButton(S["PlayerOpenRoom"], () => {
            dlg?.Dismiss();
            ExitVideoFullscreenIfNeeded();
            SwitchTab(1);
        });
        box.AddView(roomBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        dlg = OneUiModalDialog.Show(
            this,
            S["PlayerMoreTitle"],
            null,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: "OK",
            customContent: box);
    }

    void ShowSubtitlesDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void AddOptionRow(string labelText, bool selected, Action onClick) {
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = selected
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 85), 16f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
            OneUiTheme.AttachRecoil(row, 0.97f);
            TextView tv = Label(labelText, 13f, selected ? ColText : OneUiTheme.TextSecondary);
            tv.SetTypeface(null, selected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                dlg?.Dismiss();
                onClick?.Invoke();
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        AddOptionRow(S["SubtitlesOff"], string.IsNullOrEmpty(activeSubtitleTrackId), () => {
            activeSubtitleTrackId = null;
            activeSubtitleCues = [];
            musicVideoStage?.SetSubtitles(activeSubtitleCues);
            RefreshBottomActionBarVisuals();
        });

        for (int i = 0; i < discoveredSubtitles.Count; i++) {
            SubtitleTrackInfo tr = discoveredSubtitles[i];
            bool isSel = string.Equals(activeSubtitleTrackId, tr.Id, StringComparison.Ordinal);
            AddOptionRow(tr.Title, isSel, () => SelectSubtitleTrack(tr));
        }

        if (discoveredSubtitles.Count == 0) {
            TextView emptyTv = Label(S["SubtitlesNoneFound"], 12f, OneUiTheme.TextMuted);
            emptyTv.SetPadding(Dp(4), Dp(2), Dp(4), Dp(8));
            list.AddView(emptyTv);
        }

        Button onlineSubBtn = PrimaryButton(S["OnlineSubsBtn"], () => {
            dlg?.Dismiss();
            ShowOnlineSubtitlesSearchDialog();
        });
        list.AddView(onlineSubBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        Button importBtn = PillButton(S["SubtitlesImportBtn"], () => {
            dlg?.Dismiss();
            LaunchFilePicker(ReqLoadSubtitles, allowMultiple: false);
        });
        list.AddView(importBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        dlg = OneUiModalDialog.Show(
            this,
            S["VideoSubtitlesTitle"],
            null,
            icon: OneUiIconType.SubtitlesCc,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
    }

    void ShowOnlineSubtitlesSearchDialog() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        string defaultQ = OnlineMetadataService.CleanSearchTerm(
            currentMediaMeta?.Title ?? (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : ""));

        EditText qInput = new(this) {
            Text = defaultQ,
            Hint = S["OnlineSearchQueryHint"],
            TextSize = 13.5f
        };
        qInput.SetTextColor(ColText);
        qInput.SetHintTextColor(ColMuted);
        qInput.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        qInput.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
        box.AddView(qInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(box, S["OnlineAutoBestSwitch"], store.OnlineAutoSelectBestMatch, v => store.OnlineAutoSelectBestMatch = v);

        LinearLayout resultsHost = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void ApplySubtitleCandidate(OnlineSubtitleCandidate cand, bool autoPicked) {
            dlg?.Dismiss();
            Task.Run(async () => {
                string path = await OnlineMetadataService.DownloadAndExtractSubtitleAsync(this, cand).ConfigureAwait(false);
                if (string.IsNullOrEmpty(path)) return;
                List<SubtitleCue> cues = SubtitleService.LoadFromFilePath(path);
                RunOnUiThread(() => {
                    if (uiDestroyed || cues.Count == 0) return;
                    SubtitleTrackInfo tr = new() {
                        Id = "ext:" + path,
                        Title = $"{cand.Title} ({cand.Language})",
                        Language = cand.Language,
                        ExternalFilePath = path
                    };
                    discoveredSubtitles.RemoveAll(s => string.Equals(s.Id, tr.Id, StringComparison.OrdinalIgnoreCase));
                    discoveredSubtitles.Insert(0, tr);
                    activeSubtitleTrackId = tr.Id;
                    activeSubtitleCues = cues;
                    musicVideoStage?.SetSubtitles(activeSubtitleCues);
                    RefreshBottomActionBarVisuals();
                    string msg = autoPicked
                        ? string.Format(S["OnlineAutoBestAppliedSub"], cand.Source, cand.Title)
                        : S["OnlineAppliedSub"];
                    Toast.MakeText(this, msg, ToastLength.Short).Show();
                });
            });
        }

        void ExecuteSubSearch() {
            string q = qInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            Task.Run(async () => {
                List<OnlineSubtitleCandidate> list = await OnlineMetadataService.SearchSubtitlesAsync(q, "pb").ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed) return;
                    resultsHost.RemoveAllViews();
                    if (list.Count == 0) {
                        resultsHost.AddView(Label(S["OnlineNoResults"], 12f, ColMuted));
                        return;
                    }

                    OnlineSubtitleCandidate exactBest = store.OnlineAutoSelectBestMatch
                        ? list.FirstOrDefault(s => s.IsExactMatch)
                        : null;
                    if (exactBest != null) {
                        ApplySubtitleCandidate(exactBest, autoPicked: true);
                        return;
                    }

                    if (store.OnlineAutoSelectBestMatch) {
                        TextView hint = Label(S["OnlineNoExactFallback"], 11.5f, OneUiTheme.PeachText);
                        hint.SetPadding(Dp(2), 0, Dp(2), Dp(6));
                        resultsHost.AddView(hint);
                    }

                    foreach (OnlineSubtitleCandidate cand in list) {
                        LinearLayout row = HRow();
                        row.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                        row.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 14f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = cand.IsExactMatch ? " • [Exata]" : "";
                        TextView t1 = Label($"{cand.Title}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{cand.Language} • {cand.Source}", 11f, OneUiTheme.PrimaryHigh);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplySubtitleCandidate(cand, autoPicked: false);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        Button searchBtn = PrimaryButton(S["OnlineSearchAction"], ExecuteSubSearch);
        box.AddView(searchBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(10)
        });
        box.AddView(resultsHost);

        dlg = OneUiModalDialog.Show(
            this,
            S["OnlineSubsBtn"],
            null,
            icon: OneUiIconType.SubtitlesCc,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: box);

        if (!string.IsNullOrWhiteSpace(defaultQ)) {
            ExecuteSubSearch();
        }
    }

    void SelectSubtitleTrack(SubtitleTrackInfo track) {
        if (track == null) return;
        string mediaPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path;
        Task.Run(() => {
            List<SubtitleCue> cues = SubtitleService.LoadCues(this, track, mediaPath, ffmpeg);
            RunOnUiThread(() => {
                if (uiDestroyed) return;
                activeSubtitleTrackId = track.Id;
                activeSubtitleCues = cues;
                musicVideoStage?.SetSubtitles(activeSubtitleCues);
                RefreshBottomActionBarVisuals();
                if (cues.Count == 0) {
                    ShowError(S["SubtitlesNoneFound"]);
                }
            });
        });
    }

    void ToggleVideoFullscreen() {
        if (fullscreenVideoDlg != null) {
            ExitVideoFullscreenIfNeeded();
            return;
        }
        if (musicVideoStage == null) {
            return;
        }

        double curSec = playerEngine?.CurrentSeconds ?? 0;
        musicVideoStage.PrepareSurfaceTransferSeek(curSec);
        (musicVideoStage.Parent as ViewGroup)?.RemoveView(musicVideoStage);

        FrameLayout fsRoot = new(this);
        fsRoot.SetBackgroundColor(Color.Black);
        fsRoot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        Android.App.Dialog dlg = new(this);
        dlg.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dlg.SetContentView(fsRoot);
        dlg.SetCancelable(true);
        if (dlg.Window != null) {
            dlg.Window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Black));
            dlg.Window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
#pragma warning disable CS0618, CA1422
            dlg.Window.SetFlags(WindowManagerFlags.Fullscreen, WindowManagerFlags.Fullscreen);
            dlg.Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                SystemUiFlags.ImmersiveSticky |
                SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.LayoutHideNavigation);
#pragma warning restore CS0618, CA1422
        }
        dlg.DismissEvent += (_, _) => ExitVideoFullscreenIfNeeded();

        fullscreenVideoDlg = dlg;
        RequestedOrientation = ScreenOrientation.SensorLandscape;
        dlg.Show();
        dlg.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        musicVideoStage.SetFullscreenMode(true);
    }

    void ExitVideoFullscreenIfNeeded() {
        Android.App.Dialog dlg = fullscreenVideoDlg;
        if (dlg == null) {
            return;
        }
        fullscreenVideoDlg = null;

        try {
            double curSec = playerEngine?.CurrentSeconds ?? 0;
            musicVideoStage?.PrepareSurfaceTransferSeek(curSec);
            if (musicVideoStage?.Parent is ViewGroup parent) {
                parent.RemoveView(musicVideoStage);
            }
            if (musicVideoSlot != null && musicVideoStage != null) {
                musicVideoSlot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                musicVideoStage.SetFullscreenMode(false);
            }
        } catch {
            // Ignore view hierarchy cleanup races
        }

        try {
            if (dlg.IsShowing) {
                dlg.Dismiss();
            }
            dlg.Dispose();
        } catch {
            // Ignore
        }

        // Automatically rotate back to Portrait on fullscreen exit, then release sensor lock cleanly
        RequestedOrientation = ScreenOrientation.Portrait;
        uiHandler?.PostDelayed(() => {
            if (!uiDestroyed && fullscreenVideoDlg == null) {
                RequestedOrientation = ScreenOrientation.Unspecified;
            }
        }, 1400);
    }

    void ShowTrackSelectorDialog() {
        if (LoadedFile == null || LoadedFile.Tracks.Count <= 1) {
            return;
        }
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
            CavernizeTrack tr = LoadedFile.Tracks[i];
            bool isSel = tr == selectedTrack;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = isSel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 80), 16f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, 16f);
            OneUiTheme.AttachRecoil(row, 0.97f);

            TextView tv = Label($"#{i + 1}: {tr}", 13f, isSel ? ColText : OneUiTheme.TextSecondary);
            tv.SetTypeface(null, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (SelectedTrack != tr) {
                    SelectedTrack = tr;
                }
            };
            list.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        dlg = OneUiModalDialog.Show(
            this,
            S["SelectTrackHeader"],
            null,
            icon: OneUiIconType.Queue,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
    }

    void SetMasterVolumePercent(int percent) {
        percent = Math.Clamp(percent, 0, 200);
        float vol = percent / 100f;
        store.PlayerVolume = vol;
        playerEngine.Volume = vol;

        if (seekBarVolume != null && seekBarVolume.Progress != percent) {
            seekBarVolume.Progress = percent;
            seekBarVolume.ValueText = $"{percent}%";
            seekBarVolume.Invalidate();
        }
        if (musicVolSeekBar != null && musicVolSeekBar.Progress != percent) {
            musicVolSeekBar.Progress = percent;
            musicVolSeekBar.Invalidate();
        }
        RefreshMusicPlayerControlsUi();
    }
}
