using Android.Animation;
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
            float targetScale = playing ? 1.0f : 0.89f;
            float targetElev = OneUiTheme.Dp(Context, playing ? 18f : 7f);
            Animate()
                .ScaleX(targetScale)
                .ScaleY(targetScale)
                .SetDuration(380)
                .SetInterpolator(new Android.Views.Animations.PathInterpolator(0.22f, 1f, 0.36f, 1f))
                .Start();
            Elevation = targetElev;
        }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
            MeasureSpecMode wMode = MeasureSpec.GetMode(widthMeasureSpec);
            MeasureSpecMode hMode = MeasureSpec.GetMode(heightMeasureSpec);
            int maxW = MeasureSpec.GetSize(widthMeasureSpec);
            int maxH = MeasureSpec.GetSize(heightMeasureSpec);
            int prefPx = OneUiTheme.Dp(Context, PreferredSizeDp);
            int target = prefPx;
            if (wMode != MeasureSpecMode.Unspecified && maxW > 0) {
                target = Math.Min(target, maxW);
            }
            if (hMode != MeasureSpecMode.Unspecified && maxH > 0) {
                target = Math.Min(target, maxH);
            }
            target = Math.Max(OneUiTheme.Dp(Context, 40), target);
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

    sealed class SearchClearIconView : View {
        readonly Paint strokePaint = new(PaintFlags.AntiAlias) {
            StrokeCap = Paint.Cap.Round
        };

        public SearchClearIconView(Context ctx, Color color) : base(ctx) {
            strokePaint.Color = color;
        }

        protected override void OnDraw(Canvas canvas) {
            base.OnDraw(canvas);
            float w = Width;
            float h = Height;
            if (w <= 0f || h <= 0f) return;
            float pad = Math.Min(w, h) * 0.28f;
            strokePaint.StrokeWidth = Math.Max(1.6f, Math.Min(w, h) * 0.14f);
            canvas.DrawLine(pad, pad, w - pad, h - pad, strokePaint);
            canvas.DrawLine(w - pad, pad, pad, h - pad, strokePaint);
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
    LinearLayout musicTabRootPage;
    LinearLayout musicNowPlayingSection;
    ScrollView musicLibraryScrollView;
    LinearLayout musicLibrarySection;
    TextView segNowPlayingBtn, segLibraryBtn;

    // Library controls & cached filtered/sorted index
    EditText librarySearchInput;
    FrameLayout librarySearchClearBtn;
    Java.Lang.Runnable librarySearchDebounceRunnable;
    TextView[] libCatTabs;
    TextView[] libMediaTypeTabs;
    LinearLayout libQualityFilterBtn, libSortBtn, libNoiseToggleBtn, libFolderScopeChip;
    OneUiIconView libQualityFilterIcon, libSortIcon, libNoiseToggleIcon, libFolderScopeIcon;
    TextView libQualityFilterText, libSortText, libNoiseToggleText, libFolderScopeText;
    LinearLayout libraryListHost;
    TextView libraryStatusText;
    bool isIncrementalSyncRunning;
    int libraryFilterRevision = 1;
    MediaLibrarySnapshot cachedLibIndexSnap;
    int cachedLibIndexRevision = -1;
    int cachedLibSpatialMode = -1, cachedLibMediaMode = -1, cachedLibSortMode = -1, cachedLibFolderMode = -1;
    bool cachedLibHideNoise;
    string cachedLibFoldersJson = "";
    List<LibraryTrackItem> cachedVisibleTracks = [];
    List<LibraryMergedTrack> cachedMergedTracks = [];
    List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> cachedAlbumEntries = [];
    List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> cachedArtistEntries = [];
    List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> cachedFolderEntries = [];
    List<SmartCleanInspectionEntry> cachedSmartCleanEntries = [];
    int cachedHiddenNoiseCount;
    MediaLibrarySnapshot cachedQuerySnap;
    int cachedQueryRevision = -1;
    LibraryCategory cachedQueryCat;
    string cachedQueryStr = "";
    List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged)> cachedQueryAlbums = [];
    List<LibraryMergedTrack> cachedQueryMergedTracks = [];
    List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged)> cachedQueryArtists = [];
    List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged)> cachedQueryFolders = [];

    FrameLayout mediaStageFrame;
    FrameLayout musicArtworkSlot;
    RoundedArtworkView musicArtworkView;

    FrameLayout musicVideoSlot;
    SyncedVideoStageView musicVideoStage;
    Android.App.Dialog fullscreenVideoDlg;

    // Borderless Apple Music Lyrics Sheet + W14 Apple Music Sing Vocal & Live Mic Control Dock
    LinearLayout musicLyricsContainer;
    RoundedArtworkView musicLyricsMiniArt;
    TextView musicLyricsHeaderTitle, musicLyricsHeaderSub;
    AppleMusicLyricsView musicLyricsView;
    LinearLayout musicLyricsSingDock;
    OneUiIconView musicLyricsSingMicIcon;
    TextView musicLyricsSingPctText;
    OneUiSeekBar musicLyricsVocalSeekBar;
    Button musicLyricsAiPrepBtn, musicLyricsMixMatchBtn, musicLyricsVocalAbBtn, musicLyricsScarlettBtn;
    TextView musicLyricsAiStatusText;

    // Apple Music Title Row
    LinearLayout musicTitleRow;
    TextView musicTitleText, musicArtistText;

    // Seekbar + Centered Spatial Badge + Expandable Format Switcher + Unified Transport + Slim Volume
    OneUiWaveMediaSeekBar musicWaveSeekBar;
    LinearLayout badgeFormatRow;
    LinearLayout musicSpatialBadgePill;
    OneUiIconView musicSpatialBadgeIcon;
    TextView musicSpatialBadgeText;
    FrameLayout musicFormatPillContainer;
    HorizontalScrollView musicFormatScrollView;
    LinearLayout musicFormatSwitchRow;
    bool isFormatPillExpanded;
    bool isFormatPillShowingExpandedChips;
    ValueAnimator formatPillAnimator;
    float formatExpandFraction;
    int spatialBadgeFullTextWidthPx;
    int formatCollapsedWidthPx;
    int formatExpandedWidthPx;
    List<LibraryTrackItem> currentTrackVariants = [];
    int stageAnimGen;
    int lastMusicViewportHeightPx;
    LinearLayout musicTransportRow, musicVolRow, musicSpatialToggleRow, musicBottomActionRow;

    FrameLayout musicSeekBackBtn, musicRewBtn, musicPlayCircleBtn, musicFwdBtn, musicSeekFwdBtn;
    OneUiIconView musicPlayIcon;
    OneUiSeekBar musicVolSeekBar;

    // Flanking Spatial Toggles ([Cavernize 3D] | [HRTF 3D] | [Matrix 7.1]) + Integrated Bottom Utility Row
    LinearLayout musicCavernizeToggleBtn, musicBottomSpatialBtn, musicMatrixToggleBtn;
    OneUiIconView musicCavernizeIcon, musicBottomSpatialIcon, musicMatrixIcon;
    TextView musicCavernizeText, musicBottomSpatialText, musicMatrixText;

    FrameLayout musicBottomLeftBtn, musicBottomOnlineBtn, musicBottomLoopBtn, musicBottomMoreBtn;
    OneUiIconView musicBottomLeftIcon, musicBottomOnlineIcon, musicBottomLoopIcon, musicBottomMoreIcon;

    LinearLayout BuildMusicPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        musicTabRootPage = page;
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(20), Dp(6), Dp(20), Dp(10));

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
        segNowPlayingBtn.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        OneUiTheme.AttachRecoil(segNowPlayingBtn, 0.94f);
        segNowPlayingBtn.Click += (_, _) => SetMusicPlayerSection(openLibrary: false);

        segLibraryBtn = new TextView(this) {
            Text = S["PlayerLibrary"],
            TextSize = 12f,
            Gravity = GravityFlags.Center
        };
        segLibraryBtn.SetTypeface(sfSemi, TypefaceStyle.Bold);
        segLibraryBtn.SetSingleLine(true);
        segLibraryBtn.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        OneUiTheme.AttachRecoil(segLibraryBtn, 0.94f);
        segLibraryBtn.Click += (_, _) => SetMusicPlayerSection(openLibrary: true);

        segWrap.AddView(segNowPlayingBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        segWrap.AddView(segLibraryBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(segWrap, new LinearLayout.LayoutParams(Dp(236), ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        // ============================================================
        // SECTION A: NOW PLAYING (Zero-Scroll Exact Viewport Fit)
        // ============================================================
        musicNowPlayingSection = new LinearLayout(this) { Orientation = Orientation.Vertical };
        musicNowPlayingSection.SetGravity(GravityFlags.CenterHorizontal);

        // 1. Unified Flexible Stage Container (weight = 1f: fills exact remaining height with 0px scroll)
        mediaStageFrame = new FrameLayout(this);

        musicArtworkSlot = new FrameLayout(this);
        musicArtworkView = new RoundedArtworkView(this) {
            PreferredSizeDp = 340,
            CornerDp = 16f,
            ScaleX = 0.89f,
            ScaleY = 0.89f
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
            TopMargin = Dp(4),
            BottomMargin = Dp(4)
        });
        mediaStageFrame.AddView(musicArtworkSlot, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        // Video Stage (Samsung Video Player + YouTube HUD + CC Subtitles + Portrait/Landscape Fullscreen)
        musicVideoSlot = new FrameLayout(this) { Visibility = ViewStates.Gone };
        musicVideoStage = new SyncedVideoStageView(this, S["VideoSyncBadge"]);
        musicVideoStage.PlayPauseRequested += TogglePlayPause;
        musicVideoStage.SeekDeltaRequested += SeekRelative;
        musicVideoStage.SeekAbsoluteRequested += SeekAbsoluteSeconds;
        musicVideoStage.SubtitlesRequested += ShowSubtitlesDialog;
        musicVideoStage.AudioTrackRequested += ShowTrackSelectorDialog;
        musicVideoStage.LoopToggleRequested += ToggleLoopMode;
        musicVideoStage.SpatialOptionsRequested += ShowAudioSpecDialog;
        musicVideoStage.FullscreenToggleRequested += ToggleVideoFullscreen;
        musicVideoSlot.AddView(musicVideoStage, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent) {
            Gravity = GravityFlags.Center
        });
        mediaStageFrame.AddView(musicVideoSlot, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(4)
        });

        // Borderless Apple Music Live Lyrics Sheet (expands to fill freed height in Simplified Lyrics Mode)
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
            PreferredSizeDp = 46,
            CornerDp = 10f
        };
        musicLyricsMiniArt.Click += (_, _) => SetPlayerStageMode(PlayerStageMode.Cover);
        lyricsHeader.AddView(musicLyricsMiniArt, new LinearLayout.LayoutParams(Dp(46), Dp(46)) { RightMargin = Dp(10) });

        LinearLayout lyricsMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicLyricsHeaderTitle = Label(S["PlayerEmptyTitle"], 16f, ColText);
        musicLyricsHeaderTitle.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicLyricsHeaderTitle.SetSingleLine(true);
        musicLyricsHeaderTitle.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsHeaderSub = Label(S["LyricsTapHint"], 12.5f, Color.Argb(182, 252, 252, 255));
        musicLyricsHeaderSub.SetTypeface(sfReg, TypefaceStyle.Normal);
        musicLyricsHeaderSub.SetSingleLine(true);
        musicLyricsHeaderSub.Ellipsize = TextUtils.TruncateAt.End;
        lyricsMetaCol.AddView(musicLyricsHeaderTitle);
        lyricsMetaCol.AddView(musicLyricsHeaderSub);
        lyricsMetaCol.Click += (_, _) => SetPlayerStageMode(PlayerStageMode.Cover);
        lyricsHeader.AddView(lyricsMetaCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        musicLyricsContainer.AddView(lyricsHeader);

        musicLyricsView = new AppleMusicLyricsView(this);
        musicLyricsView.LineSeekRequested += sec => SeekAbsoluteSeconds(sec);
        musicLyricsContainer.AddView(musicLyricsView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // W14: Apple Music Sing-style Vocal Level Slider (0%-100%) + Live Mic / Auto-Vocal Mix-Match & Scarlett 2i2 Controls
        musicLyricsSingDock = new LinearLayout(this) { Orientation = Orientation.Vertical };
        musicLyricsSingDock.Background = OneUiTheme.CreateRoundedDrawable(
            this, Color.Argb(44, 20, 22, 32), 18f, Color.Argb(48, 255, 255, 255), 0.9f);
        musicLyricsSingDock.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));

        LinearLayout singTopRow = HRow();
        singTopRow.SetGravity(GravityFlags.CenterVertical);
        musicLyricsSingMicIcon = new OneUiIconView(this, OneUiIconType.Microphone, OneUiTheme.PrimaryHigh);
        singTopRow.AddView(musicLyricsSingMicIcon, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(6) });

        TextView singLabelTv = Label(S["SingVocalLevelTitle"], 11.8f, ColText);
        singLabelTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
        singLabelTv.SetSingleLine(true);
        singLabelTv.Ellipsize = TextUtils.TruncateAt.End;
        singTopRow.AddView(singLabelTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsSingPctText = Label($"{Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100)}%", 11.5f, OneUiTheme.PrimaryHigh);
        musicLyricsSingPctText.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicLyricsSingPctText.SetSingleLine(true);
        singTopRow.AddView(musicLyricsSingPctText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        musicLyricsAiPrepBtn = PillButton(S["NeuralPrepBtn"], () =>
            PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke));
        musicLyricsAiPrepBtn.TextSize = 10.4f;
        musicLyricsAiPrepBtn.SetSingleLine(true);
        musicLyricsAiPrepBtn.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsAiPrepBtn.SetPadding(Dp(9), Dp(5), Dp(9), Dp(5));
        singTopRow.AddView(musicLyricsAiPrepBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });

        musicLyricsMixMatchBtn = PillButton(S["SingMicMixMatchBtn"], ShowAutoVocalMixMatchModal);
        musicLyricsMixMatchBtn.TextSize = 10.6f;
        musicLyricsMixMatchBtn.SetSingleLine(true);
        musicLyricsMixMatchBtn.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsMixMatchBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        singTopRow.AddView(musicLyricsMixMatchBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });

        musicLyricsVocalAbBtn = PillButton(S["SingVocalAbWet"], () => {
            if (playerEngine == null) {
                return;
            }
            playerEngine.VocalChainBypassed = !playerEngine.VocalChainBypassed;
            ApplyAndRefreshVocalChain();
        });
        musicLyricsVocalAbBtn.TextSize = 10.2f;
        musicLyricsVocalAbBtn.SetSingleLine(true);
        musicLyricsVocalAbBtn.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsVocalAbBtn.SetPadding(Dp(8), Dp(5), Dp(8), Dp(5));
        musicLyricsVocalAbBtn.Visibility = store.LiveMicEnabled ? ViewStates.Visible : ViewStates.Gone;
        singTopRow.AddView(musicLyricsVocalAbBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(5) });

        musicLyricsScarlettBtn = PillButton(S["SingScarlettQuickBtn"], ShowScarlett2i2ControlsModal);
        musicLyricsScarlettBtn.TextSize = 10.6f;
        musicLyricsScarlettBtn.SetSingleLine(true);
        musicLyricsScarlettBtn.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsScarlettBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        singTopRow.AddView(musicLyricsScarlettBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        musicLyricsSingDock.AddView(singTopRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(4)
        });

        musicLyricsVocalSeekBar = new OneUiSeekBar(this) {
            Max = 100,
            Progress = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100),
            DefaultProgress = 100,
            HasDefaultReset = true,
            SlimVolumeMode = true
        };
        musicLyricsVocalSeekBar.UserProgressChanged += v => ApplyLyricsSingVocalLevel(v);
        musicLyricsVocalSeekBar.ResetRequested += v => ApplyLyricsSingVocalLevel(v);
        musicLyricsSingDock.AddView(musicLyricsVocalSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        musicLyricsAiStatusText = Label("", 10.5f, OneUiTheme.TextSub);
        musicLyricsAiStatusText.SetSingleLine(true);
        musicLyricsAiStatusText.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsAiStatusText.Visibility = ViewStates.Gone;
        musicLyricsSingDock.AddView(musicLyricsAiStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2)
        });

        musicLyricsContainer.AddView(musicLyricsSingDock, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6)
        });

        mediaStageFrame.AddView(musicLyricsContainer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        musicNowPlayingSection.AddView(mediaStageFrame, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f) {
            BottomMargin = Dp(8)
        });

        // 2. Apple Music Title + Artist Row (clean, zero duplicate buttons)
        musicTitleRow = HRow();
        musicTitleRow.SetGravity(GravityFlags.CenterVertical);

        LinearLayout titleMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicTitleText = new TextView(this) {
            Text = S["PlayerEmptyTitle"],
            TextSize = 19.5f
        };
        musicTitleText.SetTextColor(ColText);
        musicTitleText.SetTypeface(sfBold, TypefaceStyle.Bold);
        musicTitleText.SetSingleLine(true);
        musicTitleText.Ellipsize = TextUtils.TruncateAt.End;
        titleMetaCol.AddView(musicTitleText);

        musicArtistText = new TextView(this) {
            Text = S["PlayerEmptySub"],
            TextSize = 14f
        };
        musicArtistText.SetTextColor(Color.Argb(182, 252, 252, 255));
        musicArtistText.SetTypeface(sfReg, TypefaceStyle.Normal);
        musicArtistText.SetSingleLine(true);
        musicArtistText.Ellipsize = TextUtils.TruncateAt.End;
        titleMetaCol.AddView(musicArtistText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(1)
        });
        musicTitleRow.AddView(titleMetaCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        musicNowPlayingSection.AddView(musicTitleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        // 3. One UI 9 Wave Media Seekbar + Compact Centered Audio Spec Badge & Format Variant Pill (26dp)
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
        musicSpatialBadgePill.SetMinimumWidth(Dp(26));
        musicSpatialBadgePill.SetMinimumHeight(Dp(26));
        musicSpatialBadgePill.SetPadding(Dp(9), 0, Dp(9), 0);
        musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(38, 255, 255, 255), 0.8f);
        musicSpatialBadgePill.ClipToOutline = true;
        musicSpatialBadgePill.OutlineProvider = new RoundedClipOutlineProvider(Dp(13));
        OneUiTheme.AttachRecoil(musicSpatialBadgePill, 0.94f);
        musicSpatialBadgeIcon = new OneUiIconView(this, OneUiIconType.Sparkles, Color.Argb(228, 252, 252, 255));
        musicSpatialBadgeText = Label(S["PlayerBadgeSpatial"], 10.5f, Color.Argb(232, 252, 252, 255));
        musicSpatialBadgeText.Gravity = GravityFlags.Center;
        musicSpatialBadgeText.SetIncludeFontPadding(false);
        musicSpatialBadgeText.SetSingleLine(true);
        musicSpatialBadgeText.Ellipsize = TextUtils.TruncateAt.End;
        musicSpatialBadgeText.SetTypeface(sfSemi, TypefaceStyle.Bold);
        musicSpatialBadgePill.AddView(musicSpatialBadgeIcon, new LinearLayout.LayoutParams(Dp(12), Dp(12)) { RightMargin = Dp(5) });
        musicSpatialBadgePill.AddView(musicSpatialBadgeText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
        musicSpatialBadgePill.Click += (_, _) => ShowAudioSpecDialog();

        musicFormatSwitchRow = new LinearLayout(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        musicFormatSwitchRow.SetGravity(GravityFlags.Center);
        musicFormatSwitchRow.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));

        musicFormatScrollView = new HorizontalScrollView(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never,
            FillViewport = true
        };
        musicFormatScrollView.AddView(musicFormatSwitchRow, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.MatchParent) {
            Gravity = GravityFlags.Center
        });

        musicFormatPillContainer = new FrameLayout(this) {
            Visibility = ViewStates.Gone,
            ClipToOutline = true,
            OutlineProvider = new RoundedClipOutlineProvider(Dp(13))
        };
        musicFormatPillContainer.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f, Color.Argb(44, 255, 255, 255), 0.8f);
        musicFormatPillContainer.AddView(musicFormatScrollView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent) {
            Gravity = GravityFlags.Center
        });

        badgeFormatRow = HRow();
        badgeFormatRow.SetGravity(GravityFlags.Center);
        badgeFormatRow.AddView(musicSpatialBadgePill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(26)));
        badgeFormatRow.AddView(musicFormatPillContainer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(26)) {
            LeftMargin = Dp(7)
        });
        musicNowPlayingSection.AddView(badgeFormatRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(8)
        });

        // 4. Unified One UI 9 + Apple Music Transport Row:
        // Track Skip buttons OUTER [SkipPrevious] | Seek seconds INNER [-10s] | [Play/Pause] | [+10s] | [SkipNext]
        LinearLayout transportRow = HRow();
        musicTransportRow = transportRow;
        transportRow.SetGravity(GravityFlags.Center);

        musicRewBtn = MakeUnifiedGlassTransportBtn(OneUiIconType.SkipPrevious, 48, 26, frostedCircle: false, Color.Argb(245, 252, 252, 255), HandleMusicSkipPrevious, out _);
        musicSeekBackBtn = MakeUnifiedGlassTransportBtn(OneUiIconType.Rewind10, 44, 21, frostedCircle: false, Color.Argb(186, 252, 252, 255), () => SeekRelative(-10), out _);
        musicPlayCircleBtn = MakeUnifiedGlassTransportBtn(OneUiIconType.Play, 62, 31, frostedCircle: true, Color.White, TogglePlayPause, out musicPlayIcon);
        musicSeekFwdBtn = MakeUnifiedGlassTransportBtn(OneUiIconType.Forward10, 44, 21, frostedCircle: false, Color.Argb(186, 252, 252, 255), () => SeekRelative(10), out _);
        musicFwdBtn = MakeUnifiedGlassTransportBtn(OneUiIconType.SkipNext, 48, 26, frostedCircle: false, Color.Argb(245, 252, 252, 255), HandleMusicSkipNext, out _);

        transportRow.AddView(musicRewBtn, new LinearLayout.LayoutParams(Dp(48), Dp(50)) { RightMargin = Dp(8) });
        transportRow.AddView(musicSeekBackBtn, new LinearLayout.LayoutParams(Dp(44), Dp(46)) { RightMargin = Dp(12) });
        transportRow.AddView(musicPlayCircleBtn, new LinearLayout.LayoutParams(Dp(62), Dp(62)) { RightMargin = Dp(12) });
        transportRow.AddView(musicSeekFwdBtn, new LinearLayout.LayoutParams(Dp(44), Dp(46)) { RightMargin = Dp(8) });
        transportRow.AddView(musicFwdBtn, new LinearLayout.LayoutParams(Dp(48), Dp(50)));
        musicNowPlayingSection.AddView(transportRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 5. Apple Music Volume Row (SpeakerLow + slim Samsung relative-drag slider + SpeakerHigh)
        LinearLayout volRow = HRow();
        musicVolRow = volRow;
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
            BottomMargin = Dp(8)
        });

        // 6. Flanking Spatial Toggles: [ Cavernize 3D ]  [ HRTF 3D / Direto ]  [ Matrix 7.1 ]
        LinearLayout spatialToggleRow = HRow();
        musicSpatialToggleRow = spatialToggleRow;
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
                Services.BinauralProfileService.SyncToSceneHub(playerEngine.SceneHub, store, S);
                Services.AutoEqService.SyncBinauralModeToEngine(store, playerEngine);
                RefreshBinauralProfileUi();
                RefreshPersonalizationUi();
                RefreshAutoEqUi();
                refreshBentoTilesUi?.Invoke();
                RefreshMusicPlayerControlsUi();
            });
        musicBottomSpatialBtn.LongClick += (_, e) => {
            ShowBinauralProfileSelectorDialog();
            e.Handled = true;
        };

        (musicMatrixToggleBtn, musicMatrixIcon, musicMatrixText) = MakeCompactSpatialPill(
            OneUiIconType.Sparkles, "Matrix 7.1", sfSemi, () => {
                bool next = !store.MatrixUpmixing;
                store.MatrixUpmixing = next;
                if (matrixUpmixSwitch != null) matrixUpmixSwitch.Checked = next;
                ReattachPlayerTrack();
                RefreshMusicPlayerControlsUi();
            });

        spatialToggleRow.AddView(musicCavernizeToggleBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(6) });
        spatialToggleRow.AddView(musicBottomSpatialBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(6) });
        spatialToggleRow.AddView(musicMatrixToggleBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f));
        musicNowPlayingSection.AddView(spatialToggleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 7. Integrated 4-Slot Bottom Utility Row: [ Letra / CC ] | [ Buscar Online ] | [ Repetir ] | [ + (Sala 3D & Nav Bar) ]
        LinearLayout bottomActionRow = HRow();
        musicBottomActionRow = bottomActionRow;
        bottomActionRow.SetGravity(GravityFlags.CenterVertical);

        musicBottomLeftBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomLeftBtn, 0.88f);
        musicBottomLeftIcon = new OneUiIconView(this, OneUiIconType.LyricsQuote, Color.Argb(195, 252, 252, 255));
        musicBottomLeftBtn.AddView(musicBottomLeftIcon, new FrameLayout.LayoutParams(Dp(21), Dp(21)) { Gravity = GravityFlags.Center });
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
                ShowOnlineSubtitlesSearchDialog();
            } else {
                ShowOnlineMetadataDialog();
            }
        };

        musicBottomLoopBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomLoopBtn, 0.88f);
        musicBottomLoopIcon = new OneUiIconView(this, OneUiIconType.RepeatLoop, Color.Argb(195, 252, 252, 255));
        musicBottomLoopBtn.AddView(musicBottomLoopIcon, new FrameLayout.LayoutParams(Dp(21), Dp(21)) { Gravity = GravityFlags.Center });
        musicBottomLoopBtn.Click += (_, _) => ToggleLoopMode();

        // Compact frosted '+' pill integrated directly on the right side of the utility row (1-click jump to Sala 3D + full nav bar)
        musicBottomMoreBtn = new FrameLayout(this);
        musicBottomMoreBtn.Background = OneUiTheme.CreateRoundedDrawable(
            this, Color.Argb(54, 255, 255, 255), 999f, Color.Argb(84, 255, 255, 255), 1f);
        OneUiTheme.AttachRecoil(musicBottomMoreBtn, 0.88f);
        musicBottomMoreIcon = new OneUiIconView(this, OneUiIconType.Plus, Color.Argb(248, 252, 252, 255));
        musicBottomMoreBtn.AddView(musicBottomMoreIcon, new FrameLayout.LayoutParams(Dp(18), Dp(18)) { Gravity = GravityFlags.Center });

        settingsUpdateDotCollapsed = new View(this) { Visibility = ViewStates.Gone };
        settingsUpdateDotCollapsed.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.FunctionalGreen, 99f);
        musicBottomMoreBtn.AddView(settingsUpdateDotCollapsed, new FrameLayout.LayoutParams(Dp(6), Dp(6)) {
            Gravity = GravityFlags.Top | GravityFlags.End,
            TopMargin = Dp(4),
            RightMargin = Dp(7)
        });
        musicBottomMoreBtn.Click += (_, _) => SwitchTab(1);

        LinearLayout slot1 = HRow(); slot1.SetGravity(GravityFlags.Center); slot1.AddView(musicBottomLeftBtn, new LinearLayout.LayoutParams(Dp(46), Dp(34)));
        LinearLayout slot2 = HRow(); slot2.SetGravity(GravityFlags.Center); slot2.AddView(musicBottomOnlineBtn, new LinearLayout.LayoutParams(Dp(46), Dp(34)));
        LinearLayout slot3 = HRow(); slot3.SetGravity(GravityFlags.Center); slot3.AddView(musicBottomLoopBtn, new LinearLayout.LayoutParams(Dp(46), Dp(34)));
        LinearLayout slot4 = HRow(); slot4.SetGravity(GravityFlags.Center); slot4.AddView(musicBottomMoreBtn, new LinearLayout.LayoutParams(Dp(48), Dp(34)));

        bottomActionRow.AddView(slot1, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(slot2, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(slot3, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(slot4, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        musicNowPlayingSection.AddView(bottomActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        page.AddView(musicNowPlayingSection, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // ============================================================
        // SECTION B: IN-APP LIBRARY (Internally Scrollable, Full Nav Bar Visible)
        // ============================================================
        musicLibrarySection = BuildInAppLibrarySection(sfBold, sfSemi, sfReg);
        musicLibraryScrollView = new ScrollView(this) {
            FillViewport = true,
            VerticalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never,
            Visibility = ViewStates.Gone
        };
        musicLibraryScrollView.SetClipToPadding(false);
        musicLibraryScrollView.SetPadding(0, 0, 0, Dp(106));
        musicLibraryScrollView.ScrollChange += (_, _) => navFrostedHost?.RefreshBlur();
        musicLibraryScrollView.AddView(musicLibrarySection, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        page.AddView(musicLibraryScrollView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        page.LayoutChange += (_, _) => UpdatePlayTabSpacingForViewport();

        UpdateSectionSwitcherStyle();
        RefreshMusicPlayerUi();
        return page;
    }

    void UpdatePlayTabSpacingForViewport() {
        if (musicTabRootPage == null || musicNowPlayingSection == null) {
            return;
        }
        int vpH = musicTabRootPage.Height;
        if (vpH <= 0 || Math.Abs(vpH - lastMusicViewportHeightPx) < Dp(8)) {
            return;
        }
        lastMusicViewportHeightPx = vpH;

        float density = Math.Max(1f, Resources?.DisplayMetrics?.Density ?? 3f);
        int availHDp = (int)(vpH / density);

        // Adaptive breathing room scaled cleanly to viewport height while mediaStageFrame (weight=1f) guarantees 0px scroll
        int gapDp = availHDp < 610 ? 4 : (availHDp < 690 ? 6 : (availHDp < 780 ? 8 : 11));
        int stageGapDp = availHDp < 610 ? 5 : (availHDp < 690 ? 7 : 10);

        void SetBottomMarginDp(View v, int dpVal) {
            if (v?.LayoutParameters is LinearLayout.LayoutParams lp && lp.BottomMargin != Dp(dpVal)) {
                lp.BottomMargin = Dp(dpVal);
                v.LayoutParameters = lp;
            }
        }

        SetBottomMarginDp(mediaStageFrame, stageGapDp);
        SetBottomMarginDp(musicTitleRow, Math.Max(4, gapDp - 1));
        SetBottomMarginDp(badgeFormatRow, gapDp);
        SetBottomMarginDp(musicTransportRow, gapDp);
        SetBottomMarginDp(musicVolRow, Math.Max(4, gapDp - 1));
        SetBottomMarginDp(musicSpatialToggleRow, Math.Max(4, gapDp - 1));
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

    FrameLayout MakeAppleCircleIconBtn(OneUiIconType iconType, Action onClick) {
        FrameLayout btn = new(this);
        btn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(44, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(btn, 0.88f);
        OneUiIconView ico = new(this, iconType, ColText);
        btn.AddView(ico, new FrameLayout.LayoutParams(Dp(17), Dp(17)) { Gravity = GravityFlags.Center });
        btn.Click += (_, _) => onClick?.Invoke();
        return btn;
    }

    FrameLayout MakeUnifiedGlassTransportBtn(
        OneUiIconType iconType, int sizeDp, int iconDp, bool frostedCircle,
        Color iconColor, Action onClick, out OneUiIconView iconOut) {
        FrameLayout btn = new(this);
        if (frostedCircle) {
            btn.Background = OneUiTheme.CreateGlassCardDrawable(
                this, Color.Argb(58, 255, 255, 255), 999f, Color.Argb(96, 255, 255, 255));
        } else {
            btn.Background = OneUiTheme.CreateRippleRoundedDrawable(this, Color.Argb(10, 255, 255, 255), 999f);
        }
        OneUiTheme.AttachRecoil(btn, 0.86f);
        iconOut = new OneUiIconView(this, iconType, iconColor);
        btn.AddView(iconOut, new FrameLayout.LayoutParams(Dp(iconDp), Dp(iconDp)) { Gravity = GravityFlags.Center });
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
        LibraryTrackItem prevItem = activeLibraryQueueIndex >= 0 && activeLibraryQueueIndex < activeLibraryQueue.Count
            ? activeLibraryQueue[activeLibraryQueueIndex]
            : null;
        AudioTierCategory wantedCategory = preferredAudioCategory
            ?? (prevItem != null ? AudioQualityAnalyzer.ClassifyCategory(prevItem) : AudioTierCategory.Atmos);

        int nextIdx = activeLibraryQueueIndex + delta;
        if (nextIdx < 0) nextIdx = activeLibraryQueue.Count - 1;
        if (nextIdx >= activeLibraryQueue.Count) nextIdx = 0;
        activeLibraryQueueIndex = nextIdx;
        LibraryTrackItem item = activeLibraryQueue[nextIdx];

        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        if (snap != null) {
            List<LibraryTrackItem> variants = MediaLibraryService.FindVariantsForTrack(
                snap, item.FilePath, item.Title, item.EffectiveAlbumArtist, item.Album);
            if (variants.Count > 1) {
                // Smart Album Continuity: sticky category with scientific quality ranking & intelligent fallback
                // (Atmos -> Surround -> Stereo; Surround -> Atmos -> Stereo; Stereo -> Atmos -> Surround)
                LibraryTrackItem smartPick = AudioQualityAnalyzer.PickBestVariantForNextTrack(variants, wantedCategory, this);
                if (smartPick != null) {
                    item = smartPick;
                    activeLibraryQueue[nextIdx] = smartPick;
                }
            }
        }
        OpenLibraryTrack(item, activeLibraryQueue, autoPlay: true, preserveCategoryPreference: true);
    }

    void SetMusicPlayerSection(bool openLibrary) {
        isLibrarySectionOpen = openLibrary;
        if (openLibrary && isFormatPillExpanded) {
            SetFormatPillExpanded(false, animate: false);
        }
        ApplyCurrentWindowInsetsPadding();
        UpdateSectionSwitcherStyle();
        SyncPredictiveBackRegistration();

        // Show full bottom nav bar when browsing Library; hide it on Now Playing (integrated '+' pill handles navigation)
        if (activeTab == 0) {
            SetBottomNavBarVisible(openLibrary, animate: true);
        }

        if (musicNowPlayingSection != null && musicLibraryScrollView != null) {
            View entering = openLibrary ? (View)musicLibraryScrollView : musicNowPlayingSection;
            View leaving = openLibrary ? (View)musicNowPlayingSection : musicLibraryScrollView;

            leaving.Animate()?.Cancel();
            leaving.Visibility = ViewStates.Gone;
            leaving.Alpha = 1f;
            leaving.TranslationX = 0f;

            entering.Visibility = ViewStates.Visible;
            entering.Alpha = 0f;
            entering.TranslationX = openLibrary ? Dp(18) : -Dp(18);
            entering.Animate()
                .Alpha(1f)
                .TranslationX(0f)
                .SetDuration(260)
                .SetInterpolator(CreateOneUiEase())
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

    void SetPlayerStageMode(PlayerStageMode mode, bool animate = true) {
        if (currentMediaMeta?.HasVideo == true) {
            mode = PlayerStageMode.Video;
        } else if (mode == PlayerStageMode.Video) {
            mode = PlayerStageMode.Cover;
        }
        PlayerStageMode prevMode = musicStageMode;
        musicStageMode = mode;
        SyncPredictiveBackRegistration();

        if (mode == PlayerStageMode.Lyrics && isFormatPillExpanded) {
            SetFormatPillExpanded(false, animate: false);
        }

        bool canAnimateCoverLyrics = animate &&
            prevMode != mode &&
            musicArtworkSlot != null &&
            musicLyricsContainer != null &&
            ((prevMode == PlayerStageMode.Cover && mode == PlayerStageMode.Lyrics) ||
             (prevMode == PlayerStageMode.Lyrics && mode == PlayerStageMode.Cover));

        int myAnimGen = ++stageAnimGen;
        musicArtworkSlot?.Animate()?.Cancel();
        musicLyricsContainer?.Animate()?.Cancel();

        if (musicVideoSlot != null) {
            musicVideoSlot.Visibility = mode == PlayerStageMode.Video ? ViewStates.Visible : ViewStates.Gone;
            if (mode == PlayerStageMode.Video && activeTab == 0 && !isLibrarySectionOpen) {
                musicVideoStage?.SyncPlaybackState(playerEngine?.IsPlaying == true, playerEngine?.CurrentSeconds ?? 0, playerEngine?.TotalSeconds ?? 0);
            } else {
                musicVideoStage?.PauseVideoDecoderOnly();
            }
        }

        // Simplified Lyrics Mode: hide non-essential rows so Lyrics expands smoothly across the entire viewport
        ViewStates chromeVis = mode == PlayerStageMode.Lyrics ? ViewStates.Gone : ViewStates.Visible;
        if (canAnimateCoverLyrics && musicNowPlayingSection != null) {
            try {
                Android.Transitions.TransitionSet ts = new();
                ts.SetOrdering(Android.Transitions.TransitionOrdering.Together);
                Android.Transitions.ChangeBounds cb = new();
                cb.SetDuration(340);
                Android.Transitions.Fade fd = new();
                fd.SetDuration(220);
                ts.AddTransition(cb);
                ts.AddTransition(fd);
                ts.SetInterpolator(CreateOneUiEase());
                Android.Transitions.TransitionManager.BeginDelayedTransition(musicNowPlayingSection, ts);
            } catch { }
        }

        if (musicTitleRow != null) {
            musicTitleRow.Visibility = chromeVis;
            musicTitleRow.Alpha = 1f;
        }
        if (badgeFormatRow != null) {
            badgeFormatRow.Visibility = chromeVis;
            badgeFormatRow.Alpha = 1f;
        }
        if (musicVolRow != null) {
            musicVolRow.Visibility = chromeVis;
            musicVolRow.Alpha = 1f;
        }
        if (musicSpatialToggleRow != null) {
            musicSpatialToggleRow.Visibility = chromeVis;
            musicSpatialToggleRow.Alpha = 1f;
        }

        if (canAnimateCoverLyrics) {
            bool toLyrics = mode == PlayerStageMode.Lyrics;
            View outgoing = toLyrics ? (View)musicArtworkSlot : musicLyricsContainer;
            View incoming = toLyrics ? (View)musicLyricsContainer : musicArtworkSlot;

            if (toLyrics) {
                musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
            }

            outgoing.Visibility = ViewStates.Visible;
            outgoing.Animate()
                .Alpha(0f)
                .ScaleX(0.95f)
                .ScaleY(0.95f)
                .TranslationY(toLyrics ? -Dp(12) : Dp(12))
                .SetDuration(190)
                .SetInterpolator(CreateOneUiEase())
                .WithEndAction(new Java.Lang.Runnable(() => {
                    if (uiDestroyed || myAnimGen != stageAnimGen) return;
                    outgoing.Visibility = ViewStates.Gone;
                    outgoing.Alpha = 1f;
                    outgoing.ScaleX = 1f;
                    outgoing.ScaleY = 1f;
                    outgoing.TranslationY = 0f;
                }))
                .Start();

            incoming.Visibility = ViewStates.Visible;
            incoming.Alpha = 0f;
            incoming.ScaleX = 0.96f;
            incoming.ScaleY = 0.96f;
            incoming.TranslationY = toLyrics ? Dp(16) : -Dp(16);
            incoming.Animate()
                .Alpha(1f)
                .ScaleX(1f)
                .ScaleY(1f)
                .TranslationY(0f)
                .SetStartDelay(45)
                .SetDuration(320)
                .SetInterpolator(new Android.Views.Animations.PathInterpolator(0.16f, 1f, 0.3f, 1f))
                .Start();
        } else {
            if (musicArtworkSlot != null) {
                musicArtworkSlot.Visibility = mode == PlayerStageMode.Cover ? ViewStates.Visible : ViewStates.Gone;
                musicArtworkSlot.Alpha = 1f;
                musicArtworkSlot.ScaleX = 1f;
                musicArtworkSlot.ScaleY = 1f;
                musicArtworkSlot.TranslationY = 0f;
            }
            if (musicLyricsContainer != null) {
                musicLyricsContainer.Visibility = mode == PlayerStageMode.Lyrics ? ViewStates.Visible : ViewStates.Gone;
                musicLyricsContainer.Alpha = 1f;
                musicLyricsContainer.ScaleX = 1f;
                musicLyricsContainer.ScaleY = 1f;
                musicLyricsContainer.TranslationY = 0f;
                if (mode == PlayerStageMode.Lyrics) {
                    musicLyricsView?.UpdatePlayback(playerEngine?.CurrentSeconds ?? 0, playerEngine?.IsPlaying == true);
                }
            }
        }

        RefreshLyricsSingDockUi();
        RefreshBottomActionBarVisuals();
    }

    void ApplyLyricsSingVocalLevel(int pct) {
        int clamped = Math.Clamp(pct, 0, 100);
        float level01 = clamped * 0.01f;
        store.KaraokeVocalLevel = level01;
        bool shouldEnableKaraoke = clamped < 99;
        if (store.SpatialKaraokeEnabled != shouldEnableKaraoke) {
            store.SpatialKaraokeEnabled = shouldEnableKaraoke;
            playerEngine?.ApplyNeuralConfig(store.BuildNeuralAudioConfig());
        } else {
            playerEngine?.SetKaraokeVocalLevel(level01);
        }
        if (karaokeVocalLevelBar != null) {
            karaokeVocalLevelBar.Progress = clamped;
            karaokeVocalLevelBar.Invalidate();
        }
        if (spatialKaraokeSwitch != null && spatialKaraokeSwitch.Checked != store.SpatialKaraokeEnabled) {
            spatialKaraokeSwitch.Checked = store.SpatialKaraokeEnabled;
        }
        RefreshLyricsSingDockUi();
        RefreshStudioW12ContextUi();
    }

    void RefreshLyricsSingDockUi() {
        if (musicLyricsSingDock == null || store == null) {
            return;
        }
        int pct = Math.Clamp((int)Math.Round(store.KaraokeVocalLevel * 100f), 0, 100);
        if (musicLyricsSingPctText != null) {
            musicLyricsSingPctText.Text = $"{pct}%";
            musicLyricsSingPctText.SetTextColor(pct < 99 ? OneUiTheme.IconSystemInfo : OneUiTheme.PrimaryHigh);
        }
        if (musicLyricsVocalSeekBar != null && musicLyricsVocalSeekBar.Progress != pct) {
            musicLyricsVocalSeekBar.Progress = pct;
            musicLyricsVocalSeekBar.Invalidate();
        }
        if (musicLyricsSingMicIcon != null) {
            musicLyricsSingMicIcon.IconColor = store.LiveMicEnabled
                ? OneUiTheme.IconSystemInfo
                : (pct < 99 ? OneUiTheme.PrimaryHigh : Color.Argb(215, 252, 252, 255));
        }
        if (musicLyricsMixMatchBtn != null) {
            bool micLive = store.LiveMicEnabled && !(playerEngine?.VocalChainBypassed ?? false);
            musicLyricsMixMatchBtn.Text = micLive ? $"{S["SingMicMixMatchBtn"]} · {S["SingMicLiveBadge"]}" : S["SingMicMixMatchBtn"];
            musicLyricsMixMatchBtn.Background = micLive
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 170), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(38, 255, 255, 255), 99f, Color.Argb(54, 255, 255, 255), 0.9f);
        }
        if (musicLyricsVocalAbBtn != null) {
            bool micEnabled = store.LiveMicEnabled;
            musicLyricsVocalAbBtn.Visibility = micEnabled ? ViewStates.Visible : ViewStates.Gone;
            if (micEnabled) {
                bool dryBypass = playerEngine?.VocalChainBypassed == true;
                musicLyricsVocalAbBtn.Text = dryBypass ? S["SingVocalAbDry"] : S["SingVocalAbWet"];
                musicLyricsVocalAbBtn.Background = dryBypass
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 82), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalOrange, 185), 1f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.FunctionalGreen, 175), 1f);
            }
        }
        if (musicLyricsScarlettBtn != null) {
            bool hasUsb = usbProAudioManager?.CurrentSnapshot?.IsAttached == true;
            musicLyricsScarlettBtn.Background = hasUsb
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 76), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSpatial, 175), 1f)
                : OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(48, 255, 255, 255), 0.9f);
        }
        RefreshNeuralPrepUi();
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
            BinauralProfileService.GetActiveShortBadge(store, S));
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

            badgeStr = BuildSpatialBadgeSummary(active);
            UpdateSpatialBadgePillVisuals(badgeStr);

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
            SetFormatPillExpanded(false, animate: false);
            SetPlayerStageMode(hasVideo ? PlayerStageMode.Video : PlayerStageMode.Cover, animate: false);
        } else {
            SetPlayerStageMode(musicStageMode, animate: false);
        }

        RefreshActiveTrackFormatSwitcher(triggerDeepAnalysis: true);
        RefreshLyricsSingDockUi();
        RefreshMusicPlayerControlsUi();
    }

    string BuildSpatialBadgeSummary(CavernizeTrack active) {
        if (LoadedFile == null || active == null) {
            return S["PlayerBadgeSpatial"];
        }
        int objs = trackSourceItems.Count(x => x.IsDynamicObject);
        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        bool lgsrBypassed = playerEngine?.LgsrBypassed ?? false;

        string badgeStr;
        if (twinSynced) {
            string twinTag = lgsrBypassed ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"];
            badgeStr = objs > 0 ? string.Format(S["PlayerBadgeObjFmt"], twinTag, objs) : twinTag;
        } else if (active.SourceCodec == Codec.TrueHD) {
            badgeStr = objs > 0
                ? string.Format(S["PlayerBadgeTrueHdAtmosObjs"], objs)
                : S["PlayerBadgeTrueHdLossless"];
        } else {
            badgeStr = objs > 0
                ? string.Format(S["PlayerBadgeSpatialObjs"], objs)
                : active.Codec.ToString().ToUpperInvariant();
        }
        if (activeLibraryQueue.Count > 1 && activeLibraryQueueIndex >= 0) {
            badgeStr = $"{badgeStr} • {activeLibraryQueueIndex + 1}/{activeLibraryQueue.Count}";
        }
        return badgeStr;
    }

    void UpdateSpatialBadgePillVisuals(string badgeOverride = null) {
        if (musicSpatialBadgeText == null || musicSpatialBadgePill == null) {
            return;
        }
        CavernizeTrack active = GetActiveTrack();
        string badgeStr = badgeOverride ?? BuildSpatialBadgeSummary(active);
        musicSpatialBadgeText.Text = badgeStr;

        Cavernize.Logic.Spatial.LgsrDiagnostics lgsr = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        bool twinSynced = LoadedFile != null && lgsr.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced;
        bool lgsrBypassed = playerEngine?.LgsrBypassed ?? false;

        if (twinSynced && !lgsrBypassed) {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 56), 99f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 165), 1f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = OneUiTheme.IconSystemInfo;
            }
        } else if (twinSynced && lgsrBypassed) {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 48), 99f, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 145), 1f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = OneUiTheme.PeachText;
            }
        } else {
            musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(
                this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(38, 255, 255, 255), 0.8f);
            if (musicSpatialBadgeIcon != null) {
                musicSpatialBadgeIcon.IconColor = Color.Argb(228, 252, 252, 255);
            }
        }
        ApplyFormatPillExpandFraction(isFormatPillExpanded ? 1f : 0f);
    }

    void RefreshActiveTrackFormatSwitcher(bool triggerDeepAnalysis = true) {
        if (musicFormatSwitchRow == null || musicFormatPillContainer == null) return;
        musicFormatSwitchRow.RemoveAllViews();
        currentTrackVariants.Clear();

        if (LoadedFile == null) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        if (snap == null) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }
        string curPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        List<LibraryTrackItem> variants = MediaLibraryService.FindVariantsForTrack(
            snap, curPath, currentMediaMeta?.Title ?? "", currentMediaMeta?.Artist ?? "", currentMediaMeta?.Album ?? "");
        if (variants.Count <= 1) {
            musicFormatPillContainer.Visibility = ViewStates.Gone;
            SetFormatPillExpanded(false, animate: false);
            return;
        }

        // Strict tier ordering (Atmos -> Surround -> Stereo) + scientific quality ranking inside each tier
        AudioQualityAnalyzer.SortVariantsIntelligently(variants, this);
        currentTrackVariants.AddRange(variants);
        musicFormatPillContainer.Visibility = ViewStates.Visible;

        RebuildFormatPillChips();
        ApplyFormatPillExpandFraction(isFormatPillExpanded ? 1f : 0f);

        if (triggerDeepAnalysis) {
            string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
            AudioQualityAnalyzer.AnalyzeVariantsInBackground(this, variants, ffLoc, () => {
                RunOnUiThread(() => {
                    if (!uiDestroyed) {
                        RefreshActiveTrackFormatSwitcher(triggerDeepAnalysis: false);
                    }
                });
            });
        }
    }

    void RebuildFormatPillChips(bool? forceExpandedChips = null) {
        if (musicFormatSwitchRow == null || currentTrackVariants.Count <= 1 || LoadedFile == null) {
            return;
        }
        bool showExpanded = forceExpandedChips ?? isFormatPillExpanded;
        isFormatPillShowingExpandedChips = showExpanded;
        musicFormatSwitchRow.RemoveAllViews();
        string curPath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);

        int activeIdx = 0;
        for (int i = 0; i < currentTrackVariants.Count; i++) {
            LibraryTrackItem v = currentTrackVariants[i];
            if (string.Equals(v.FilePath, curPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.FilePath, LoadedFile.Path, StringComparison.OrdinalIgnoreCase)) {
                activeIdx = i;
                break;
            }
        }

        if (!showExpanded) {
            // Compact collapsed pill (26dp total height; chips fill exact 22dp inner height for mathematical centering)
            LibraryTrackItem activeVar = currentTrackVariants[activeIdx];
            string activeLabel = MediaLibraryService.GetDistinctVariantLabel(activeVar, currentTrackVariants);

            TextView activeChip = new(this) {
                Text = activeLabel,
                TextSize = 10f,
                Gravity = GravityFlags.Center
            };
            activeChip.SetTypeface(sfBold, TypefaceStyle.Bold);
            activeChip.SetIncludeFontPadding(false);
            activeChip.SetSingleLine(true);
            activeChip.SetPadding(Dp(8), 0, Dp(8), 0);
            activeChip.SetTextColor(Color.Rgb(18, 20, 26));
            activeChip.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(240, 252, 252, 255), 99f);

            TextView moreCountTv = new(this) {
                Text = $"+{currentTrackVariants.Count - 1}",
                TextSize = 9.8f,
                Gravity = GravityFlags.Center
            };
            moreCountTv.SetTypeface(sfBold, TypefaceStyle.Bold);
            moreCountTv.SetIncludeFontPadding(false);
            moreCountTv.SetSingleLine(true);
            moreCountTv.SetPadding(Dp(5), 0, Dp(6), 0);
            moreCountTv.SetTextColor(Color.Argb(232, 245, 248, 255));

            musicFormatSwitchRow.AddView(activeChip, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            musicFormatSwitchRow.AddView(moreCountTv, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));

            void ExpandHandler(object s, EventArgs e) => SetFormatPillExpanded(true, animate: true);
            activeChip.Click += ExpandHandler;
            moreCountTv.Click += ExpandHandler;

            musicFormatSwitchRow.Measure(
                View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
                View.MeasureSpec.MakeMeasureSpec(Dp(26), MeasureSpecMode.Exactly));
            formatCollapsedWidthPx = Math.Max(Dp(54), musicFormatSwitchRow.MeasuredWidth);
            return;
        }

        // Expanded mode: show all variants preserving active variant's relative index + 3 bespoke category vector logos
        bool hasMultipleCategories = currentTrackVariants
            .Select(AudioQualityAnalyzer.ClassifyCategory)
            .Distinct()
            .Count() > 1;

        AudioTierCategory? lastCategory = null;
        View activeChipView = null;

        for (int i = 0; i < currentTrackVariants.Count; i++) {
            LibraryTrackItem v = currentTrackVariants[i];
            AudioTierCategory cat = AudioQualityAnalyzer.ClassifyCategory(v);

            if (hasMultipleCategories && lastCategory != cat) {
                if (lastCategory.HasValue) {
                    View divider = new(this) {
                        Background = new Android.Graphics.Drawables.ColorDrawable(Color.Argb(54, 255, 255, 255)),
                        Tag = "extra"
                    };
                    musicFormatSwitchRow.AddView(divider, new LinearLayout.LayoutParams(Dp(1), Dp(12)) {
                        Gravity = GravityFlags.CenterVertical,
                        LeftMargin = Dp(4),
                        RightMargin = Dp(3)
                    });
                }

                OneUiIconType logoIcon = cat switch {
                    AudioTierCategory.Atmos => OneUiIconType.AudioLogoAtmos,
                    AudioTierCategory.Surround => OneUiIconType.AudioLogoSurround,
                    _ => OneUiIconType.AudioLogoStereo
                };
                Color logoColor = cat switch {
                    AudioTierCategory.Atmos => OneUiTheme.PrimaryHigh,
                    AudioTierCategory.Surround => OneUiTheme.PeachText,
                    _ => Color.Argb(204, 232, 236, 245)
                };
                OneUiIconView catLogo = new(this, logoIcon, logoColor) {
                    Tag = "extra"
                };
                musicFormatSwitchRow.AddView(catLogo, new LinearLayout.LayoutParams(Dp(14), Dp(14)) {
                    Gravity = GravityFlags.CenterVertical,
                    LeftMargin = Dp(4),
                    RightMargin = Dp(3)
                });
                lastCategory = cat;
            }

            bool isActive = i == activeIdx;
            string lbl = MediaLibraryService.GetDistinctVariantLabel(v, currentTrackVariants);

            TextView chip = new(this) {
                Text = lbl,
                TextSize = 10f,
                Gravity = GravityFlags.Center,
                Tag = isActive ? "active" : "extra"
            };
            chip.SetTypeface(sfBold, TypefaceStyle.Bold);
            chip.SetIncludeFontPadding(false);
            chip.SetSingleLine(true);
            chip.SetPadding(Dp(8), 0, Dp(8), 0);
            Color inactiveFg = cat switch {
                AudioTierCategory.Atmos => OneUiTheme.PrimaryHigh,
                AudioTierCategory.Surround => OneUiTheme.PeachText,
                _ => Color.Argb(224, 240, 242, 248)
            };
            chip.SetTextColor(isActive ? Color.Rgb(18, 20, 26) : inactiveFg);
            chip.Background = isActive
                ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(240, 252, 252, 255), 99f)
                : null;
            OneUiTheme.AttachRecoil(chip, 0.92f);

            LibraryTrackItem capturedVar = v;
            bool capturedActive = isActive;
            chip.Click += (_, _) => {
                SetFormatPillExpanded(false, animate: true);
                if (!capturedActive) {
                    SwitchActiveTrackVariant(capturedVar, currentTrackVariants);
                }
            };
            musicFormatSwitchRow.AddView(chip, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.MatchParent) {
                RightMargin = i < currentTrackVariants.Count - 1 ? Dp(2) : 0
            });
            if (isActive) {
                activeChipView = chip;
            }
        }

        musicFormatSwitchRow.Measure(
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified),
            View.MeasureSpec.MakeMeasureSpec(Dp(26), MeasureSpecMode.Exactly));
        formatExpandedWidthPx = Math.Max(formatCollapsedWidthPx, musicFormatSwitchRow.MeasuredWidth);

        if (activeChipView != null) {
            musicFormatScrollView?.Post(() => {
                if (uiDestroyed || musicFormatScrollView == null) return;
                int scrollX = Math.Max(0, activeChipView.Left - (musicFormatScrollView.Width - activeChipView.Width) / 2);
                musicFormatScrollView.SmoothScrollTo(scrollX, 0);
            });
        }
    }

    void SetFormatPillExpanded(bool expanded, bool animate) {
        if (musicSpatialBadgePill == null || musicFormatPillContainer == null) {
            isFormatPillExpanded = false;
            return;
        }
        bool wasExpanded = isFormatPillExpanded;
        isFormatPillExpanded = expanded;
        formatPillAnimator?.Cancel();

        float target = expanded ? 1f : 0f;
        if (!animate || currentTrackVariants.Count <= 1) {
            RebuildFormatPillChips(forceExpandedChips: expanded);
            formatExpandFraction = target;
            ApplyFormatPillExpandFraction(target);
            return;
        }

        if (expanded) {
            // Capture exact collapsed width before swapping to expanded chips so the pill morphs from collapsedW -> expandedW with zero jump
            if (!isFormatPillShowingExpandedChips && musicFormatPillContainer.Width > Dp(40)) {
                formatCollapsedWidthPx = musicFormatPillContainer.Width;
            }
            RebuildFormatPillChips(forceExpandedChips: true);
        } else if (!wasExpanded && !isFormatPillShowingExpandedChips) {
            RebuildFormatPillChips(forceExpandedChips: false);
            formatExpandFraction = 0f;
            ApplyFormatPillExpandFraction(0f);
            return;
        }

        float startF = formatExpandFraction;
        bool targetExpanded = expanded;
        formatPillAnimator = ValueAnimator.OfFloat(startF, target);
        formatPillAnimator.SetDuration(expanded ? 320 : 280);
        formatPillAnimator.SetInterpolator(CreateOneUiEase());
        formatPillAnimator.Update += (_, e) => {
            float f = (float)(e.Animation?.AnimatedValue ?? target);
            formatExpandFraction = f;
            ApplyFormatPillExpandFraction(f);
        };
        formatPillAnimator.AnimationEnd += (_, _) => {
            if (uiDestroyed) return;
            if (!targetExpanded && !isFormatPillExpanded) {
                RebuildFormatPillChips(forceExpandedChips: false);
                formatExpandFraction = 0f;
                ApplyFormatPillExpandFraction(0f);
            }
        };
        formatPillAnimator.Start();
    }

    void ApplyFormatPillExpandFraction(float f) {
        if (musicSpatialBadgePill == null || musicSpatialBadgeText == null || musicSpatialBadgeIcon == null) {
            return;
        }
        f = Math.Clamp(f, 0f, 1f);

        // Measure natural full width of spatial badge text
        string txt = musicSpatialBadgeText.Text ?? "";
        int measuredTextW = Math.Max(Dp(20), (int)Math.Ceiling(musicSpatialBadgeText.Paint.MeasureText(txt)) + Dp(4));
        spatialBadgeFullTextWidthPx = measuredTextW;

        int rowW = badgeFormatRow?.Width > 0
            ? badgeFormatRow.Width
            : Math.Max(Dp(280), (Resources?.DisplayMetrics?.WidthPixels ?? 1080) - Dp(40));

        int floorSpatialW = Dp(26); // Guaranteed 26dp x 26dp circular icon floor
        int fullSpatialW = Dp(18) + Dp(12) + Dp(5) + spatialBadgeFullTextWidthPx;
        int gapPx = musicFormatPillContainer?.Visibility == ViewStates.Visible ? Dp(7) : 0;

        if ((f <= 0.001f && !isFormatPillShowingExpandedChips) || musicFormatPillContainer?.Visibility != ViewStates.Visible) {
            if (musicSpatialBadgePill.LayoutParameters is LinearLayout.LayoutParams spLp0) {
                spLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicSpatialBadgePill.LayoutParameters = spLp0;
            }
            musicSpatialBadgePill.SetPadding(Dp(9), 0, Dp(9), 0);
            if (musicSpatialBadgeIcon.LayoutParameters is LinearLayout.LayoutParams icoLp0) {
                icoLp0.RightMargin = Dp(5);
                musicSpatialBadgeIcon.LayoutParameters = icoLp0;
            }
            if (musicSpatialBadgeText.LayoutParameters is LinearLayout.LayoutParams txtLp0) {
                txtLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicSpatialBadgeText.LayoutParameters = txtLp0;
            }
            musicSpatialBadgeText.Alpha = 1f;
            musicSpatialBadgeText.Visibility = ViewStates.Visible;

            if (musicFormatPillContainer?.LayoutParameters is LinearLayout.LayoutParams fmtLp0) {
                fmtLp0.Width = ViewGroup.LayoutParams.WrapContent;
                musicFormatPillContainer.LayoutParameters = fmtLp0;
            }
            return;
        }

        // Smooth clipped width interpolation for the format pill (collapsedW -> targetExpandedFormatW)
        int maxExpandedFormatW = Math.Max(Dp(72), rowW - gapPx - floorSpatialW);
        int targetExpandedFormatW = Math.Min(Math.Max(formatExpandedWidthPx, Dp(72)), maxExpandedFormatW);
        int startCollapsedFormatW = Math.Clamp(formatCollapsedWidthPx > 0 ? formatCollapsedWidthPx : Dp(68), Dp(52), targetExpandedFormatW);
        int curFormatW = (int)Math.Round(startCollapsedFormatW + (targetExpandedFormatW - startCollapsedFormatW) * f);

        // Spatial badge contracts only as much as needed to make room, smoothly down to the 26dp circular icon floor
        int availableSpatialW = Math.Max(floorSpatialW, rowW - gapPx - curFormatW);
        int curSpatialW = Math.Clamp(availableSpatialW, floorSpatialW, fullSpatialW);
        float shrinkProgress = fullSpatialW > floorSpatialW
            ? Math.Clamp((fullSpatialW - curSpatialW) / (float)(fullSpatialW - floorSpatialW), 0f, 1f)
            : 0f;

        int padH = (int)Math.Round(Dp(9) * (1f - shrinkProgress));
        int icoRight = (int)Math.Round(Dp(5) * (1f - shrinkProgress));
        int textAreaW = Math.Max(0, curSpatialW - (padH * 2) - Dp(12) - icoRight);

        if (musicSpatialBadgePill.LayoutParameters is LinearLayout.LayoutParams spLp) {
            spLp.Width = Math.Max(floorSpatialW, curSpatialW);
            musicSpatialBadgePill.LayoutParameters = spLp;
        }
        musicSpatialBadgePill.SetPadding(padH, 0, padH, 0);
        if (musicSpatialBadgeIcon.LayoutParameters is LinearLayout.LayoutParams icoLp) {
            icoLp.RightMargin = icoRight;
            musicSpatialBadgeIcon.LayoutParameters = icoLp;
        }
        if (textAreaW <= Dp(4)) {
            musicSpatialBadgeText.Visibility = ViewStates.Gone;
            musicSpatialBadgeText.Alpha = 0f;
        } else {
            musicSpatialBadgeText.Visibility = ViewStates.Visible;
            musicSpatialBadgeText.Alpha = Math.Clamp(1f - shrinkProgress * 1.35f, 0f, 1f);
            if (musicSpatialBadgeText.LayoutParameters is LinearLayout.LayoutParams txtLp) {
                txtLp.Width = textAreaW;
                musicSpatialBadgeText.LayoutParameters = txtLp;
            }
        }

        if (musicFormatPillContainer?.LayoutParameters is LinearLayout.LayoutParams fmtLp) {
            fmtLp.Width = curFormatW;
            musicFormatPillContainer.LayoutParameters = fmtLp;
        }

        // Cross-fade secondary variants & category logos smoothly while keeping the active variant anchored at full opacity
        if (musicFormatSwitchRow != null && isFormatPillShowingExpandedChips) {
            float extraAlpha = Math.Clamp((f - 0.10f) / 0.75f, 0f, 1f);
            for (int i = 0; i < musicFormatSwitchRow.ChildCount; i++) {
                View child = musicFormatSwitchRow.GetChildAt(i);
                if (child != null && string.Equals(child.Tag?.ToString(), "extra", StringComparison.Ordinal)) {
                    child.Alpha = extraAlpha;
                }
            }
        }
    }

    void ShowAudioSpecDialog() {
        CavernizeTrack active = GetActiveTrack();
        if (LoadedFile == null || active == null) {
            ShowToast(S["ActiveSourceEmpty"]);
            return;
        }

        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);

        // 1. Primary Format & Spatial Engine Summary Card (read-only binaural engine line, no redundant profile button)
        LinearLayout specCard = new(this) { Orientation = Orientation.Vertical };
        specCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        specCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        TextView fmtHdr = Label(active.FormatHeader, 14f, ColText);
        fmtHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
        specCard.AddView(fmtHdr);

        int beds = trackSourceItems.Count(x => x.IsBed);
        int objs = trackSourceItems.Count(x => x.IsDynamicObject);
        int srHz = playerEngine?.OutputSampleRate ?? 48000;
        string spatialLine = objs > 0
            ? string.Format(S["AudioSpecSpatialChObjsFmt"], beds, objs, srHz)
            : string.Format(S["AudioSpecChannelsFmt"], beds, srHz);
        TextView spatialTv = Label(spatialLine, 12f, OneUiTheme.PrimaryHigh);
        spatialTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
        spatialTv.SetPadding(0, Dp(2), 0, Dp(4));
        specCard.AddView(spatialTv);

        TextView binLineTv = Label($"• {S["BinauralProfileActive"]}: {BinauralProfileService.GetActiveDisplayTitle(store, S)}", 11.5f, OneUiTheme.TextSecondary);
        specCard.AddView(binLineTv);

        foreach (var d in active.Details.Take(6)) {
            TextView dTv = Label($"• {d.property}: {d.value}", 11.5f, OneUiTheme.TextSecondary);
            specCard.AddView(dTv);
        }
        box.AddView(specCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 2. Scientific Audio Quality Analysis Card (True/Fake 24-bit, FFT cutoff, Dynamic Range, Clipping)
        string activePath = currentMediaMeta?.SourceMediaPath ?? LoadedFile.Path ?? "";
        MediaLibrarySnapshot snap = librarySnapshot ?? MediaLibraryService.CachedSnapshot;
        LibraryTrackItem libItem = snap?.Tracks?.FirstOrDefault(t => string.Equals(t.FilePath, activePath, StringComparison.OrdinalIgnoreCase))
            ?? new LibraryTrackItem {
                FilePath = activePath,
                FormatBadge = Path.GetExtension(activePath).TrimStart('.').ToUpperInvariant(),
                AudioBadge = active.FormatHeader,
                ChannelCount = beds + objs,
                IsSpatial = objs > 0,
                FileSizeBytes = File.Exists(activePath) ? new FileInfo(activePath).Length : 0,
                LastModifiedTicks = File.Exists(activePath) ? File.GetLastWriteTimeUtc(activePath).Ticks : 0
            };

        AudioQualityReport qReport = AudioQualityAnalyzer.EvaluateFast(libItem, this);
        LinearLayout qualCard = new(this) { Orientation = Orientation.Vertical };
        qualCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
        qualCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

        string tierName = qReport.Category switch {
            AudioTierCategory.Atmos => S["TierAtmos"],
            AudioTierCategory.Surround => S["TierSurround"],
            _ => S["TierStereo"]
        };
        TextView qualHdr = Label($"{tierName} • {string.Format(S["QualityScoreLabel"], (int)Math.Round(qReport.Score))}", 12.5f, OneUiTheme.PeachText);
        qualHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
        qualHdr.SetPadding(0, 0, 0, Dp(4));
        qualCard.AddView(qualHdr);

        if (qReport.DeepAnalyzed) {
            if (qReport.EffectiveBitDepth > 0) {
                string bitStr = qReport.IsFake24Bit
                    ? S["QualityFake24BitWarn"]
                    : (qReport.EffectiveBitDepth >= 20
                        ? string.Format(S["QualityTrue24BitOk"], qReport.EffectiveBitDepth)
                        : string.Format(S["QualityEffectiveBits"], qReport.EffectiveBitDepth));
                Color bitCol = qReport.IsFake24Bit ? OneUiTheme.FunctionalRedFill : OneUiTheme.TextSecondary;
                qualCard.AddView(Label($"• {bitStr}", 11.5f, bitCol));
            }
            if (qReport.SpectralCutoffHz > 0) {
                string cutStr = string.Format(S["QualitySpectralCutoff"], (qReport.SpectralCutoffHz / 1000.0).ToString("0.0"));
                qualCard.AddView(Label($"• {cutStr}", 11.5f, OneUiTheme.TextSecondary));
            }
            if (qReport.IsFakeLossless) {
                qualCard.AddView(Label($"• {S["QualityFakeLosslessWarn"]}", 11.5f, OneUiTheme.FunctionalRedFill));
            } else if (qReport.IsUpsampledHiRes) {
                qualCard.AddView(Label($"• {S["QualityUpsampledHiResWarn"]}", 11.5f, OneUiTheme.PeachText));
            }
            if (qReport.DynamicRangeDb > 0.5) {
                qualCard.AddView(Label($"• {string.Format(S["QualityDynamicRange"], qReport.DynamicRangeDb.ToString("0.1"))}", 11.5f, OneUiTheme.TextSecondary));
            }
            if (qReport.ClippedPercent >= 0.05) {
                qualCard.AddView(Label($"• {string.Format(S["QualityClippingWarn"], qReport.ClippedPercent.ToString("0.2"))}", 11.5f, OneUiTheme.FunctionalRedFill));
            }
        } else {
            TextView pendingTv = Label(S["QualityAnalyzing"], 11.5f, OneUiTheme.TextMuted);
            qualCard.AddView(pendingTv);
            string ffLoc = ffmpeg?.Found == true ? ffmpeg.Location : null;
            AudioQualityAnalyzer.AnalyzeVariantsInBackground(this, [libItem], ffLoc, null);
        }

        box.AddView(qualCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 2B. W09/W14 Universal Lossless Twin & LGSR Status Card + ATMOS+FLAC Instant A/B Compare
        Cavernize.Logic.Spatial.LgsrDiagnostics lgsrDiag = playerEngine?.LgsrDiagnostics ?? Cavernize.Logic.Spatial.LgsrDiagnostics.Default;
        if (lgsrDiag.State != Cavernize.Logic.Spatial.LosslessTwinVerificationState.None) {
            LinearLayout lgsrCard = new(this) { Orientation = Orientation.Vertical };
            lgsrCard.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
            lgsrCard.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));

            LinearLayout lgsrHdrRow = HRow();
            lgsrHdrRow.SetPadding(0, 0, 0, Dp(4));
            TextView lgsrHdr = Label(S["AudioSpecLgsrHeader"], 12.5f, OneUiTheme.IconSystemInfo);
            lgsrHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            lgsrHdr.SetSingleLine(true);
            lgsrHdr.Ellipsize = TextUtils.TruncateAt.End;
            lgsrHdrRow.AddView(lgsrHdr, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            if (lgsrDiag.State == Cavernize.Logic.Spatial.LosslessTwinVerificationState.Synced) {
                TextView twinPill = new(this) {
                    Text = playerEngine.LgsrBypassed ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"],
                    TextSize = 10f,
                    Gravity = GravityFlags.Center
                };
                twinPill.SetTypeface(sfBold, TypefaceStyle.Bold);
                twinPill.SetSingleLine(true);
                twinPill.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
                twinPill.SetTextColor(Color.Rgb(14, 18, 26));
                twinPill.Background = OneUiTheme.CreateRoundedDrawable(
                    this,
                    playerEngine.LgsrBypassed ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo,
                    99f);
                lgsrHdrRow.AddView(twinPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                    LeftMargin = Dp(6)
                });
                lgsrCard.AddView(lgsrHdrRow);

                string twinLabel = string.IsNullOrEmpty(lgsrDiag.TwinFormatSummary)
                    ? lgsrDiag.TwinFileName
                    : $"{lgsrDiag.TwinFormatSummary} • {lgsrDiag.TwinFileName}";
                lgsrCard.AddView(Label($"• {string.Format(S["AudioSpecLgsrActive"], twinLabel)}", 11.5f, ColText));
                lgsrCard.AddView(Label($"• {string.Format(S["AudioSpecLgsrSync"], lgsrDiag.SyncOffsetMs, lgsrDiag.DriftPpm, lgsrDiag.SyncConfidence * 100f)}", 11.5f, OneUiTheme.TextSecondary));
                lgsrCard.AddView(Label($"• {string.Format(S["AudioSpecLgsrBands"], lgsrDiag.CouplingStartHz / 1000f, lgsrDiag.SpxStartHz / 1000f, lgsrDiag.AverageCoherence * 100f)}", 11.5f, OneUiTheme.TextSecondary));
                lgsrCard.AddView(Label($"• {string.Format(S["AudioSpecLgsrGuards"], lgsrDiag.DynamicEnvelopeLockDb, lgsrDiag.ClippingGuardActive ? S["StatusOnTag"] : "OK")}", 11.5f, OneUiTheme.TextSecondary));

                TextView abStateTv = Label(
                    playerEngine.LgsrBypassed ? S["AudioSpecLgsrAbOriginalHint"] : S["AudioSpecLgsrAbRestoredHint"],
                    11.2f,
                    playerEngine.LgsrBypassed ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo);
                abStateTv.SetPadding(0, Dp(6), 0, Dp(6));
                lgsrCard.AddView(abStateTv);

                LinearLayout abRow = HRow();
                Button abToggleBtn = null;
                abToggleBtn = PillButton(
                    playerEngine.LgsrBypassed ? S["AudioSpecLgsrAbToRestored"] : S["AudioSpecLgsrAbToOriginal"],
                    () => {
                        playerEngine.LgsrBypassed = !playerEngine.LgsrBypassed;
                        bool byp = playerEngine.LgsrBypassed;
                        twinPill.Text = byp ? S["AudioSpecAtmosAbBadge"] : S["AudioSpecAtmosFlacBadge"];
                        twinPill.Background = OneUiTheme.CreateRoundedDrawable(
                            this, byp ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo, 99f);
                        abStateTv.Text = byp ? S["AudioSpecLgsrAbOriginalHint"] : S["AudioSpecLgsrAbRestoredHint"];
                        abStateTv.SetTextColor(byp ? OneUiTheme.PeachText : OneUiTheme.IconSystemInfo);
                        abToggleBtn.Text = byp ? S["AudioSpecLgsrAbToRestored"] : S["AudioSpecLgsrAbToOriginal"];
                        abToggleBtn.Background = byp
                            ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 76), 14f, OneUiTheme.WithAlpha(OneUiTheme.PeachText, 175), 1f)
                            : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 76), 14f, OneUiTheme.WithAlpha(OneUiTheme.IconSystemInfo, 175), 1f);
                        UpdateSpatialBadgePillVisuals();
                        RefreshLgsrUi();
                        RefreshSpatialXRayHud();
                    },
                    active: !playerEngine.LgsrBypassed);
                abRow.AddView(abToggleBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                lgsrCard.AddView(abRow);
            } else {
                lgsrCard.AddView(lgsrHdrRow);
                string stateMsg = lgsrDiag.State switch {
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.Searching => S["LgsrStatusSearching"],
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless => S["LgsrStatusBypassedLossless"],
                    Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedFakeLossless => S["LgsrStatusBypassedFake"],
                    _ => S["LgsrStatusBypassedLowConf"]
                };
                lgsrCard.AddView(Label($"• {stateMsg}", 11.5f, OneUiTheme.TextSecondary));

                if (lgsrDiag.State != Cavernize.Logic.Spatial.LosslessTwinVerificationState.BypassedAlreadyLossless) {
                    LinearLayout rescanRow = HRow();
                    rescanRow.SetPadding(0, Dp(6), 0, 0);
                    Button rescanBtn = PillButton(S["LgsrRescanTwinBtn"], () => {
                        StartLosslessTwinDiscoveryAsync(active);
                        dlg?.Dismiss();
                    });
                    rescanRow.AddView(rescanBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                    lgsrCard.AddView(rescanRow);
                }
            }

            box.AddView(lgsrCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = LoadedFile.Tracks.Count > 1 ? Dp(10) : 0
            });
        }

        // 3. Audio Streams Selector (only if container holds multiple audio tracks)
        if (LoadedFile.Tracks.Count > 1) {
            TextView stHdr = Label(S["AudioStreamsSection"], 12.5f, ColText);
            stHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            stHdr.SetPadding(Dp(2), 0, Dp(2), Dp(6));
            box.AddView(stHdr);

            for (int i = 0; i < LoadedFile.Tracks.Count; i++) {
                CavernizeTrack tr = LoadedFile.Tracks[i];
                bool isSel = tr == selectedTrack;
                LinearLayout row = HRow();
                row.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
                row.Background = isSel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 82), 14f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                OneUiTheme.AttachRecoil(row, 0.98f);
                TextView nameTv = Label($"#{i + 1}: {tr}", 12f, isSel ? ColText : OneUiTheme.TextSecondary);
                nameTv.SetTypeface(isSel ? sfBold : sfSemi, isSel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
                row.AddView(nameTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                row.Click += (_, _) => {
                    dlg?.Dismiss();
                    if (SelectedTrack != tr) {
                        SelectedTrack = tr;
                    }
                };
                box.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(5)
                });
            }
        }

        dlg = OneUiModalDialog.Show(
            this,
            S["AudioSpecTitle"],
            null,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"],
            customContent: box);
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
            string trkStr = LoadedFile != null && LoadedFile.Tracks.Count > 1
                ? $"{Math.Max(1, LoadedFile.Tracks.ToList().IndexOf(selectedTrack) + 1)}/{LoadedFile.Tracks.Count}"
                : "";
            musicVideoStage?.SyncPlaybackState(isPlaying, curSec, totSec);
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                musicSpatialBadgeText.Text,
                trkStr,
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
                        if (meta.ArtworkBitmap != null && meta.ArtworkBitmap != res.ArtworkBitmap) {
                            meta.ArtworkBitmap.Dispose();
                        }
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
    // IN-APP LIBRARY UI (Unified IA, Smart Clean v2 Inspector, Variant Merge & Cached Index)
    // ============================================================
    LinearLayout BuildInAppLibrarySection(Typeface sfBold, Typeface sfSemi, Typeface sfReg) {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };

        // 1. Search Pill (180ms debounce + Clear 'X') + Refresh Button
        LinearLayout searchRow = HRow();
        searchRow.SetPadding(0, Dp(2), 0, Dp(8));

        LinearLayout searchPill = HRow();
        searchPill.SetPadding(Dp(12), Dp(5), Dp(8), Dp(5));
        searchPill.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceElevated, 999f);
        OneUiIconView srchIco = new(this, OneUiIconType.Search, OneUiTheme.TextMuted);

        librarySearchInput = new EditText(this) {
            Hint = S["LibSearchHint"],
            TextSize = 12.5f,
            ImeOptions = Android.Views.InputMethods.ImeAction.Search
        };
        librarySearchInput.SetTypeface(sfReg, TypefaceStyle.Normal);
        librarySearchInput.SetTextColor(ColText);
        librarySearchInput.SetHintTextColor(ColMuted);
        librarySearchInput.SetBackgroundColor(Color.Transparent);
        librarySearchInput.SetSingleLine(true);
        librarySearchInput.SetPadding(0, Dp(2), 0, Dp(2));

        librarySearchClearBtn = new FrameLayout(this) {
            Visibility = ViewStates.Gone,
            ContentDescription = S["LibClearSearchDesc"]
        };
        librarySearchClearBtn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(36, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(librarySearchClearBtn, 0.88f);
        librarySearchClearBtn.AddView(new SearchClearIconView(this, OneUiTheme.TextMuted),
            new FrameLayout.LayoutParams(Dp(12), Dp(12)) { Gravity = GravityFlags.Center });
        librarySearchClearBtn.Click += (_, _) => {
            CancelLibrarySearchDebounce();
            librarySearchQuery = "";
            libraryRenderLimit = 72;
            if (librarySearchInput != null && !string.IsNullOrEmpty(librarySearchInput.Text)) {
                librarySearchInput.Text = "";
            } else {
                RenderLibraryView();
            }
        };

        librarySearchInput.TextChanged += (_, _) => {
            string raw = librarySearchInput.Text ?? "";
            if (librarySearchClearBtn != null) {
                librarySearchClearBtn.Visibility = string.IsNullOrEmpty(raw) ? ViewStates.Gone : ViewStates.Visible;
            }
            CancelLibrarySearchDebounce();
            if (string.IsNullOrEmpty(raw)) {
                bool changed = !string.IsNullOrEmpty(librarySearchQuery);
                librarySearchQuery = "";
                libraryRenderLimit = 72;
                if (changed || isLibrarySectionOpen) {
                    RenderLibraryView();
                }
                return;
            }
            if (string.Equals(raw, librarySearchQuery, StringComparison.Ordinal)) {
                return;
            }
            uiHandler ??= new Android.OS.Handler(Android.OS.Looper.MainLooper);
            librarySearchDebounceRunnable = new Java.Lang.Runnable(() => {
                if (uiDestroyed) return;
                librarySearchQuery = librarySearchInput?.Text ?? "";
                libraryRenderLimit = 72;
                RenderLibraryView();
            });
            uiHandler.PostDelayed(librarySearchDebounceRunnable, 180);
        };

        searchPill.AddView(srchIco, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { RightMargin = Dp(8) });
        searchPill.AddView(librarySearchInput, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(4) });
        searchPill.AddView(librarySearchClearBtn, new LinearLayout.LayoutParams(Dp(26), Dp(26)));
        searchRow.AddView(searchPill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        FrameLayout refreshBtn = MakeAppleCircleIconBtn(OneUiIconType.Refresh, () => RefreshLibraryScanAsync(force: true));
        searchRow.AddView(refreshBtn, new LinearLayout.LayoutParams(Dp(38), Dp(38)));
        box.AddView(searchRow);

        // 2. Non-Scrolling Segmented Category Capsule: [ Albums | Tracks | Artists | Folders ]
        LinearLayout catCapsule = HRow();
        catCapsule.SetPadding(Dp(3), Dp(3), Dp(3), Dp(3));
        catCapsule.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(46, 255, 255, 255), 999f, Color.Argb(38, 255, 255, 255), 1f);
        (LibraryCategory cat, string title)[] cats = [
            (LibraryCategory.Albums, S["LibAlbums"]),
            (LibraryCategory.Tracks, S["LibTracks"]),
            (LibraryCategory.Artists, S["LibArtists"]),
            (LibraryCategory.Folders, S["LibFolders"])
        ];
        libCatTabs = new TextView[cats.Length];
        for (int i = 0; i < cats.Length; i++) {
            int idx = i;
            LibraryCategory c = cats[i].cat;
            TextView tabTv = new(this) {
                Text = cats[i].title,
                TextSize = 11.5f,
                Gravity = GravityFlags.Center
            };
            tabTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
            tabTv.SetIncludeFontPadding(false);
            tabTv.SetSingleLine(true);
            tabTv.Ellipsize = TextUtils.TruncateAt.End;
            tabTv.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
            OneUiTheme.AttachRecoil(tabTv, 0.94f);
            tabTv.Click += (_, _) => {
                libraryCategory = c;
                selectedLibraryAlbum = null;
                selectedLibraryArtist = null;
                selectedLibraryFolder = null;
                libraryRenderLimit = 72;
                UpdateLibraryCatStyles();
                RenderLibraryView();
            };
            libCatTabs[idx] = tabTv;
            catCapsule.AddView(tabTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        box.AddView(catCapsule, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        // 3. Single Unified Chip & Action Bar: [All|Music|Videos] | [Audio Filter] | [Sort] | [Smart Clean (N)]
        HorizontalScrollView filtScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        LinearLayout filtRow = HRow();
        filtRow.SetPadding(0, 0, 0, Dp(8));

        LinearLayout mediaTypeGroup = HRow();
        mediaTypeGroup.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
        mediaTypeGroup.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 999f, Color.Argb(34, 255, 255, 255), 0.8f);
        (int mediaMode, string mediaLabel)[] mediaChoices = [
            (0, S["LibMediaAll"]),
            (1, S["LibMediaMusic"]),
            (2, S["LibMediaVideos"])
        ];
        libMediaTypeTabs = new TextView[mediaChoices.Length];
        for (int i = 0; i < mediaChoices.Length; i++) {
            int mMode = mediaChoices[i].mediaMode;
            TextView mTv = new(this) {
                Text = mediaChoices[i].mediaLabel,
                TextSize = 10.8f,
                Gravity = GravityFlags.Center
            };
            mTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
            mTv.SetIncludeFontPadding(false);
            mTv.SetSingleLine(true);
            mTv.Ellipsize = TextUtils.TruncateAt.End;
            mTv.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            mTv.SetMinimumHeight(Dp(26));
            OneUiTheme.AttachRecoil(mTv, 0.94f);
            mTv.Click += (_, _) => {
                if (store.LibraryMediaTypeFilter == mMode) return;
                store.LibraryMediaTypeFilter = mMode;
                libraryRenderLimit = 72;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            };
            libMediaTypeTabs[i] = mTv;
            mediaTypeGroup.AddView(mTv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(26)));
        }
        filtRow.AddView(mediaTypeGroup, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libQualityFilterBtn, libQualityFilterIcon, libQualityFilterText) = MakeCompactSpatialPill(
            OneUiIconType.Sparkles, S["LibQualityAll"], sfSemi, ShowLibraryQualityFilterDialog);
        libQualityFilterBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libQualityFilterBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libSortBtn, libSortIcon, libSortText) = MakeCompactSpatialPill(
            OneUiIconType.Sliders, S["LibSortPrefix"], sfSemi, ShowLibrarySortDialog);
        libSortBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libSortBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libNoiseToggleBtn, libNoiseToggleIcon, libNoiseToggleText) = MakeCompactSpatialPill(
            OneUiIconType.CleanFilter, S["LibHideNoise"], sfSemi, ShowSmartCleanInspectorModal);
        libNoiseToggleBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        filtRow.AddView(libNoiseToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (libFolderScopeChip, libFolderScopeIcon, libFolderScopeText) = MakeCompactSpatialPill(
            OneUiIconType.FolderFilter, S["LibFoldersChipLabel"], sfSemi, ShowLibraryFolderFilterDialog);
        libFolderScopeChip.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        libFolderScopeChip.Visibility = ViewStates.Gone;
        filtRow.AddView(libFolderScopeChip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)));

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

    void CancelLibrarySearchDebounce() {
        if (librarySearchDebounceRunnable != null) {
            uiHandler?.RemoveCallbacks(librarySearchDebounceRunnable);
            librarySearchDebounceRunnable = null;
        }
    }

    void InvalidateLibraryIndexCache() {
        libraryFilterRevision++;
        cachedLibIndexSnap = null;
        cachedQuerySnap = null;
        cachedQueryRevision = -1;
    }

    string GetLibrarySortShortLabel(int sortMode) => sortMode switch {
        1 => S["LibSortArtistAsc"],
        2 => S["LibSortRecentDesc"],
        3 => S["LibSortQualityDesc"],
        _ => S["LibSortTitleAsc"]
    };

    void UpdateLibraryFilterStyles() {
        if (libMediaTypeTabs != null) {
            int mediaMode = store.LibraryMediaTypeFilter;
            for (int i = 0; i < libMediaTypeTabs.Length; i++) {
                bool sel = mediaMode == i;
                libMediaTypeTabs[i].Background = sel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 170), OneUiTheme.PillCornerDp, OneUiTheme.PrimaryHigh, 0.8f)
                    : null;
                libMediaTypeTabs[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSub);
            }
        }
        if (libQualityFilterBtn != null) {
            int qMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
            libQualityFilterText.Text = qMode switch {
                1 => S["LibFilterSurround"],
                2 => S["LibFilterSpatialOnly"],
                _ => S["LibQualityAll"]
            };
            bool active = qMode > 0;
            libQualityFilterBtn.Background = active
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 145), OneUiTheme.PillCornerDp, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            Color fg = active ? Color.White : OneUiTheme.TextSub;
            libQualityFilterIcon.IconColor = fg;
            libQualityFilterText.SetTextColor(fg);
        }
        if (libSortBtn != null) {
            int sMode = store.LibrarySortMode;
            libSortText.Text = $"{S["LibSortPrefix"]}: {GetLibrarySortShortLabel(sMode)}";
            bool active = sMode > 0;
            libSortBtn.Background = active
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Tertiary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Tertiary, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
            Color fg = active ? Color.White : OneUiTheme.TextSub;
            libSortIcon.IconColor = fg;
            libSortText.SetTextColor(fg);
        }
        if (libNoiseToggleBtn != null) {
            if (librarySnapshot != null) {
                EnsureLibraryBaseIndex();
            }
            bool on = store.LibraryHideNonMusic;
            int hiddenCount = librarySnapshot != null ? cachedHiddenNoiseCount : 0;
            libNoiseToggleText.Text = hiddenCount > 0
                ? string.Format(S["SmartCleanChipBadge"], hiddenCount)
                : S["LibHideNoise"];
            libNoiseToggleBtn.Background = on
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Secondary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Secondary, 1f)
                : (hiddenCount > 0
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalAmber, 70), OneUiTheme.PillCornerDp, OneUiTheme.FunctionalAmber, 1f)
                    : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp));
            Color fg = on || hiddenCount > 0 ? Color.White : OneUiTheme.TextSub;
            libNoiseToggleIcon.IconColor = fg;
            libNoiseToggleText.SetTextColor(fg);
        }
        if (libFolderScopeChip != null) {
            int fMode = store.LibraryFolderFilterMode;
            List<string> dirs = store.GetLibraryFilterFolders();
            bool active = fMode > 0 && dirs.Count > 0;
            libFolderScopeChip.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
            if (active) {
                libFolderScopeText.Text = string.Format(S["LibFoldersChipActive"], dirs.Count);
                libFolderScopeChip.Background = OneUiTheme.CreateRoundedDrawable(
                    this, OneUiTheme.WithAlpha(OneUiTheme.Tertiary, 120), OneUiTheme.PillCornerDp, OneUiTheme.Tertiary, 1f);
                libFolderScopeIcon.IconColor = Color.White;
                libFolderScopeText.SetTextColor(Color.White);
            }
        }
    }

    List<LibraryTrackItem> FilterLibraryTracks(IEnumerable<LibraryTrackItem> tracks) =>
        MediaLibraryService.FilterTracks(tracks, store);

    void ShowLibraryQualityFilterDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        int curMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        (int mode, string label)[] opts = [
            (0, S["LibFilterAll"]),
            (1, S["LibFilterSurround"]),
            (2, S["LibFilterSpatialOnly"])
        ];
        for (int i = 0; i < opts.Length; i++) {
            int m = opts[i].mode;
            bool sel = curMode == m;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(opts[i].label, 13f, sel ? Color.White : OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (sel) {
                row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh),
                    new LinearLayout.LayoutParams(Dp(16), Dp(16)) { LeftMargin = Dp(8) });
            }
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (store.LibraryFilterMode != m) {
                    store.LibraryFilterMode = m;
                    libraryRenderLimit = 72;
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
                    RenderLibraryView();
                }
            };
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = i < opts.Length - 1 ? Dp(6) : 0
            });
        }
        dlg = OneUiModalDialog.Show(
            this,
            S["LibQualityDialogTitle"],
            customContent: content,
            icon: OneUiIconType.Sparkles,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["CloseBtn"]);
    }

    void ShowLibrarySortDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        int curSort = store.LibrarySortMode;
        (int mode, string label)[] opts = [
            (0, S["LibSortTitleAsc"]),
            (1, S["LibSortArtistAsc"]),
            (2, S["LibSortRecentDesc"]),
            (3, S["LibSortQualityDesc"])
        ];
        for (int i = 0; i < opts.Length; i++) {
            int sMode = opts[i].mode;
            bool sel = curSort == sMode;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(opts[i].label, 13f, sel ? Color.White : OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), sel ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (sel) {
                row.AddView(new OneUiIconView(this, OneUiIconType.Check, OneUiTheme.PrimaryHigh),
                    new LinearLayout.LayoutParams(Dp(16), Dp(16)) { LeftMargin = Dp(8) });
            }
            row.Click += (_, _) => {
                dlg?.Dismiss();
                if (store.LibrarySortMode != sMode) {
                    store.LibrarySortMode = sMode;
                    libraryRenderLimit = 72;
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
                    RenderLibraryView();
                }
            };
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = i < opts.Length - 1 ? Dp(6) : 0
            });
        }
        dlg = OneUiModalDialog.Show(
            this,
            S["LibSortDialogTitle"],
            customContent: content,
            icon: OneUiIconType.Sliders,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: S["CloseBtn"]);
    }

    void ShowSmartCleanInspectorModal() {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfSemi = OneUiTheme.GetAppleFontSemibold(this);
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;
        Java.Lang.Runnable sliderDebounce = null;
        bool suppressSwitchEvents = false;
        int inspectLimit = 40;

        string FormatMinDur(int sec) => sec <= 0 ? S["SmartCleanMinDurationOff"] : $"{sec}s";

        void CancelInspectorDebounce() {
            if (sliderDebounce != null) {
                uiHandler?.RemoveCallbacks(sliderDebounce);
                sliderDebounce = null;
            }
        }

        LinearLayout dynamicSectionsHost = new(this) { Orientation = Orientation.Vertical };

        void NotifySmartCleanChanged(bool rebuildInspectorList) {
            InvalidateLibraryIndexCache();
            libraryRenderLimit = 72;
            UpdateLibraryFilterStyles();
            RenderLibraryView();
            if (rebuildInspectorList) {
                RebuildDynamicSections();
            }
        }

        Switch masterSw = null, sysSfxSw = null, msgSw = null, camSw = null, stemSw = null;
        OneUiSeekBar minDurBar = null;

        masterSw = AddSwitchRow(box, S["SmartCleanMasterToggle"], store.LibraryHideNonMusic, on => {
            if (suppressSwitchEvents) return;
            store.LibraryHideNonMusic = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });

        TextView rulesHdr = Label(S["SmartCleanRulesHeader"], 12.2f, OneUiTheme.PeachText);
        rulesHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
        rulesHdr.SetPadding(Dp(2), Dp(6), Dp(2), Dp(4));
        box.AddView(rulesHdr);

        sysSfxSw = AddSwitchRow(box, S["SmartCleanCatSystemSfx"], store.SmartCleanHideSystemSfx, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideSystemSfx = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        msgSw = AddSwitchRow(box, S["SmartCleanCatMessengers"], store.SmartCleanHideMessengers, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideMessengers = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        camSw = AddSwitchRow(box, S["SmartCleanCatCameraSocial"], store.SmartCleanHideCameraSocialVideo, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideCameraSocialVideo = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        stemSw = AddSwitchRow(box, S["SmartCleanCatStemsShort"], store.SmartCleanHideStemsAndShort, on => {
            if (suppressSwitchEvents) return;
            store.SmartCleanHideStemsAndShort = on;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });

        minDurBar = new OneUiSeekBar(this) {
            Max = 180,
            Progress = Math.Clamp(store.SmartCleanMinDurationSec, 0, 180),
            DefaultProgress = 25,
            HasDefaultReset = true,
            LabelText = S["SmartCleanMinDurationLabel"],
            ValueText = FormatMinDur(store.SmartCleanMinDurationSec)
        };
        minDurBar.UserProgressChanged += val => {
            int clamped = Math.Clamp(val, 0, 180);
            minDurBar.ValueText = FormatMinDur(clamped);
            minDurBar.Invalidate();
            store.SmartCleanMinDurationSec = clamped;
            CancelInspectorDebounce();
            uiHandler ??= new Android.OS.Handler(Android.OS.Looper.MainLooper);
            sliderDebounce = new Java.Lang.Runnable(() => {
                if (!uiDestroyed) {
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                }
            });
            uiHandler.PostDelayed(sliderDebounce, 150);
        };
        minDurBar.ResetRequested += val => {
            int clamped = Math.Clamp(val, 0, 180);
            CancelInspectorDebounce();
            minDurBar.ValueText = FormatMinDur(clamped);
            minDurBar.Invalidate();
            store.SmartCleanMinDurationSec = clamped;
            NotifySmartCleanChanged(rebuildInspectorList: true);
        };
        box.AddView(minDurBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4),
            BottomMargin = Dp(8)
        });

        List<string> scopeDirs = store.GetLibraryFilterFolders();
        string scopeSummary = store.LibraryFolderFilterMode > 0 && scopeDirs.Count > 0
            ? string.Format(S["LibFolderScopeSummaryFiltered"], scopeDirs.Count)
            : S["LibFolderScopeSummaryAll"];

        LinearLayout toolsRow = HRow();
        toolsRow.SetPadding(0, 0, 0, Dp(8));
        Button folderScopeBtn = PillButton(string.Format(S["SmartCleanFolderScopeBtn"], scopeSummary), () => {
            dlg?.Dismiss();
            ShowLibraryFolderFilterDialog();
        });
        folderScopeBtn.TextSize = 11.5f;
        Button resetRulesBtn = PillButton(S["SmartCleanResetDefaultsBtn"], () => {
            CancelInspectorDebounce();
            store.ResetSmartCleanToDefaults(clearAllowlistAndHiddenFolders: false);
            suppressSwitchEvents = true;
            masterSw.Checked = store.LibraryHideNonMusic;
            sysSfxSw.Checked = store.SmartCleanHideSystemSfx;
            msgSw.Checked = store.SmartCleanHideMessengers;
            camSw.Checked = store.SmartCleanHideCameraSocialVideo;
            stemSw.Checked = store.SmartCleanHideStemsAndShort;
            suppressSwitchEvents = false;
            minDurBar.Progress = store.SmartCleanMinDurationSec;
            minDurBar.ValueText = FormatMinDur(store.SmartCleanMinDurationSec);
            minDurBar.Invalidate();
            NotifySmartCleanChanged(rebuildInspectorList: true);
        });
        resetRulesBtn.TextSize = 11.5f;
        toolsRow.AddView(folderScopeBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });
        toolsRow.AddView(resetRulesBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        box.AddView(toolsRow);

        box.AddView(dynamicSectionsHost);

        void RebuildDynamicSections() {
            if (uiDestroyed) return;
            dynamicSectionsHost.RemoveAllViews();

            HashSet<string> allowlist = store.SmartCleanAllowlistPaths;
            if (allowlist.Count > 0) {
                LinearLayout allowRow = HRow();
                allowRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                allowRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                TextView allowTv = Label(string.Format(S["SmartCleanAllowlistInfo"], allowlist.Count), 11.8f, OneUiTheme.TextSecondary);
                allowTv.SetSingleLine(true);
                allowTv.Ellipsize = TextUtils.TruncateAt.End;
                Button clearAllowBtn = PillButton(string.Format(S["SmartCleanClearAllowlistBtn"], allowlist.Count), () => {
                    store.ClearSmartCleanAllowlist();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                clearAllowBtn.TextSize = 11f;
                clearAllowBtn.SetMinimumHeight(Dp(30));
                clearAllowBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                allowRow.AddView(allowTv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                allowRow.AddView(clearAllowBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                dynamicSectionsHost.AddView(allowRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }

            List<string> hiddenDirs = store.SmartCleanHiddenFolders
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            LinearLayout hiddenHdrRow = HRow();
            hiddenHdrRow.SetPadding(Dp(2), Dp(2), Dp(2), Dp(4));
            TextView hiddenHdr = Label(string.Format(S["SmartCleanHiddenFoldersHeader"], hiddenDirs.Count), 12.2f, OneUiTheme.PeachText);
            hiddenHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            hiddenHdrRow.AddView(hiddenHdr, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (hiddenDirs.Count > 1) {
                Button clearDirsBtn = PillButton(S["SmartCleanClearHiddenFoldersBtn"], () => {
                    store.ClearHiddenLibraryFolders();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                clearDirsBtn.TextSize = 10.8f;
                clearDirsBtn.SetMinimumHeight(Dp(28));
                clearDirsBtn.SetPadding(Dp(9), Dp(3), Dp(9), Dp(3));
                hiddenHdrRow.AddView(clearDirsBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            }
            dynamicSectionsHost.AddView(hiddenHdrRow);

            if (hiddenDirs.Count == 0) {
                TextView noDirsTv = Label(S["SmartCleanHiddenFoldersEmpty"], 11.5f, OneUiTheme.TextMuted);
                noDirsTv.SetPadding(Dp(2), 0, Dp(2), Dp(8));
                dynamicSectionsHost.AddView(noDirsTv);
            } else {
                for (int i = 0; i < hiddenDirs.Count; i++) {
                    string dirPath = hiddenDirs[i];
                    string dirLeaf = Path.GetFileName(dirPath.TrimEnd('/'));
                    if (string.IsNullOrEmpty(dirLeaf)) dirLeaf = dirPath;
                    LinearLayout dirRow = HRow();
                    dirRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                    dirRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                    LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                    TextView nameTv = Label(dirLeaf, 12.2f, ColText);
                    nameTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
                    nameTv.SetSingleLine(true);
                    nameTv.Ellipsize = TextUtils.TruncateAt.End;
                    TextView pathTv = Label(dirPath, 10.5f, OneUiTheme.TextMuted);
                    pathTv.SetSingleLine(true);
                    pathTv.Ellipsize = TextUtils.TruncateAt.Middle;
                    col.AddView(nameTv);
                    col.AddView(pathTv);
                    Button unhideBtn = PillButton(S["SmartCleanUnhideFolderBtn"], () => {
                        store.UnhideLibraryFolder(dirPath);
                        NotifySmartCleanChanged(rebuildInspectorList: true);
                    });
                    unhideBtn.TextSize = 11f;
                    unhideBtn.SetMinimumHeight(Dp(30));
                    unhideBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
                    dirRow.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                    dirRow.AddView(unhideBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                    dynamicSectionsHost.AddView(dirRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        BottomMargin = Dp(6)
                    });
                }
            }

            List<SmartCleanInspectionEntry> entries = MediaLibraryService.GetSmartCleanInspectionList(librarySnapshot?.Tracks, store);
            TextView inspHdr = Label(string.Format(S["SmartCleanInspectedHeader"], entries.Count), 12.2f, OneUiTheme.PeachText);
            inspHdr.SetTypeface(sfBold, TypefaceStyle.Bold);
            inspHdr.SetPadding(Dp(2), Dp(6), Dp(2), Dp(4));
            dynamicSectionsHost.AddView(inspHdr);

            if (entries.Count == 0) {
                TextView emptyTv = Label(S["SmartCleanInspectedEmpty"], 11.5f, OneUiTheme.TextMuted);
                emptyTv.SetPadding(Dp(2), 0, Dp(2), Dp(4));
                dynamicSectionsHost.AddView(emptyTv);
                return;
            }

            int showCount = Math.Min(entries.Count, inspectLimit);
            for (int i = 0; i < showCount; i++) {
                SmartCleanInspectionEntry entry = entries[i];
                LibraryTrackItem t = entry.Track;
                string fileName = !string.IsNullOrEmpty(t.FilePath) && !t.FilePath.StartsWith("content://", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(t.FilePath)
                    : t.Title;
                if (string.IsNullOrWhiteSpace(fileName)) fileName = t.Title;
                string durStr = "";
                if (t.DurationMs > 0) {
                    TimeSpan ts = TimeSpan.FromMilliseconds(t.DurationMs);
                    durStr = ts.TotalHours >= 1 ? $" • {ts:h\\:mm\\:ss}" : $" • {ts:mm\\:ss}";
                }
                string folderLine = $"{t.FolderName}{durStr}";
                string reasonText = !string.IsNullOrEmpty(entry.ReasonKey) ? S[entry.ReasonKey] : entry.ReasonLabel;
                if (string.Equals(reasonText, entry.ReasonKey, StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.ReasonLabel)) {
                    reasonText = entry.ReasonLabel;
                }

                LinearLayout itemRow = HRow();
                itemRow.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
                itemRow.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);

                LinearLayout infoCol = new(this) { Orientation = Orientation.Vertical };
                TextView fileTv = Label(fileName, 12.2f, ColText);
                fileTv.SetTypeface(sfSemi, TypefaceStyle.Bold);
                fileTv.SetSingleLine(true);
                fileTv.Ellipsize = TextUtils.TruncateAt.End;
                TextView fldTv = Label(folderLine, 10.5f, OneUiTheme.TextMuted);
                fldTv.SetSingleLine(true);
                fldTv.Ellipsize = TextUtils.TruncateAt.End;
                TextView reasonTv = Label(reasonText, 10.8f, OneUiTheme.PeachText);
                reasonTv.SetSingleLine(true);
                reasonTv.Ellipsize = TextUtils.TruncateAt.End;
                infoCol.AddView(fileTv);
                infoCol.AddView(fldTv);
                infoCol.AddView(reasonTv);

                Button restoreBtn = PillButton(S["SmartCleanRestoreBtn"], () => {
                    string restoreKey = !string.IsNullOrEmpty(t.FilePath) ? t.FilePath : t.CacheKey;
                    store.AllowSmartCleanPath(restoreKey);
                    Toast.MakeText(this, string.Format(S["SmartCleanRestoredToast"], fileName), ToastLength.Short).Show();
                    NotifySmartCleanChanged(rebuildInspectorList: true);
                });
                restoreBtn.TextSize = 11f;
                restoreBtn.SetMinimumHeight(Dp(30));
                restoreBtn.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));

                itemRow.AddView(infoCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                itemRow.AddView(restoreBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                dynamicSectionsHost.AddView(itemRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }

            if (entries.Count > showCount) {
                Button showAllBtn = PillButton(string.Format(S["SmartCleanInspectedMore"], entries.Count), () => {
                    inspectLimit = entries.Count;
                    RebuildDynamicSections();
                });
                showAllBtn.TextSize = 11.5f;
                dynamicSectionsHost.AddView(showAllBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    TopMargin = Dp(2)
                });
            }
        }

        RebuildDynamicSections();

        dlg = OneUiModalDialog.Show(
            this,
            S["SmartCleanModalTitle"],
            customContent: box,
            icon: OneUiIconType.CleanFilter,
            iconBadgeColor: OneUiTheme.Secondary,
            positiveText: S["CloseBtn"]);
        dlg.DismissEvent += (_, _) => CancelInspectorDebounce();
    }

    void ShowLibraryFolderFilterDialog() {
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        int chosenMode = store.LibraryFolderFilterMode;
        List<string> chosenFolders = store.GetLibraryFilterFolders();
        Android.App.Dialog dlg = null;

        LinearLayout[] modeRows = new LinearLayout[3];
        TextView[] modeLabels = new TextView[3];
        string[] labels = [S["LibFoldersAll"], S["LibFoldersExcept"], S["LibFoldersOnly"]];
        TextView summaryTv = Label("", 11.5f, OneUiTheme.TextSub);

        void RefreshFolderFilterModalUi() {
            for (int i = 0; i < 3; i++) {
                bool sel = chosenMode == i;
                modeRows[i].Background = sel
                    ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Primary, 95), 14f, OneUiTheme.PrimaryHigh, 1.2f)
                    : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                modeLabels[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSecondary);
            }
            string listPreview = chosenFolders.Count == 0
                ? "—"
                : string.Join("\n", chosenFolders.Take(6).Select(p => $"• {p}")) +
                  (chosenFolders.Count > 6 ? $"\n(+{chosenFolders.Count - 6})" : "");
            summaryTv.Text = $"{string.Format(S["LibFoldersCountBadge"], chosenFolders.Count, labels[Math.Clamp(chosenMode, 0, 2)])}\n{listPreview}";
        }

        for (int i = 0; i < 3; i++) {
            int m = i;
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            OneUiTheme.AttachRecoil(row, 0.98f);
            TextView tv = Label(labels[i], 13f, OneUiTheme.TextSecondary);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
            row.AddView(tv, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.Click += (_, _) => {
                chosenMode = m;
                RefreshFolderFilterModalUi();
            };
            modeRows[i] = row;
            modeLabels[i] = tv;
            content.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                BottomMargin = Dp(6)
            });
        }

        summaryTv.SetPadding(Dp(4), Dp(6), Dp(4), Dp(10));
        content.AddView(summaryTv);

        Button pickFoldersBtn = PrimaryButton(S["LibFoldersPickBtn"], () => {
            OneUiFileExplorerDialog.Show(this, new FileExplorerOptions {
                Title = S["ExplorerTitleFolders"],
                Mode = ExplorerPickMode.MultiFolder,
                InitialDirectory = store.LastExplorerDirectory,
                InitiallySelectedPaths = chosenFolders,
                ConfirmLabel = S["ExplorerApplyFolders"],
                OnConfirm = picked => {
                    chosenFolders = picked?.ToList() ?? [];
                    if (chosenFolders.Count > 0 && chosenMode == 0) {
                        chosenMode = 2; // Auto-activate "Only Selected" when user explicitly selects folders
                    }
                    RefreshFolderFilterModalUi();
                }
            });
        });
        content.AddView(pickFoldersBtn);
        RefreshFolderFilterModalUi();

        dlg = OneUiModalDialog.Show(
            this,
            S["LibFoldersFilter"],
            customContent: content,
            icon: OneUiIconType.FolderFilter,
            iconBadgeColor: OneUiTheme.Tertiary,
            positiveText: "OK",
            onPositive: () => {
                store.LibraryFolderFilterMode = chosenFolders.Count == 0 ? 0 : chosenMode;
                store.SetLibraryFilterFolders(chosenFolders);
                selectedLibraryAlbum = null;
                selectedLibraryArtist = null;
                selectedLibraryFolder = null;
                libraryRenderLimit = 72;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            },
            negativeText: S["CancelBtn"]);
    }

    void UpdateLibraryCatStyles() {
        if (libCatTabs == null) return;
        LibraryCategory[] cats = [LibraryCategory.Albums, LibraryCategory.Tracks, LibraryCategory.Artists, LibraryCategory.Folders];
        for (int i = 0; i < libCatTabs.Length && i < cats.Length; i++) {
            bool sel = libraryCategory == cats[i];
            libCatTabs[i].Background = sel
                ? OneUiTheme.CreateRoundedDrawable(this, ColAccent, OneUiTheme.PillCornerDp)
                : null;
            libCatTabs[i].SetTextColor(sel ? Color.White : OneUiTheme.TextSub);
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
                    InvalidateLibraryIndexCache();
                    UpdateLibraryFilterStyles();
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

    void ComputeGroupSortMetrics(
        List<LibraryMergedTrack> merged,
        int sortMode,
        out long maxMod,
        out double maxScore,
        out int bestTier) {
        maxMod = 0;
        maxScore = 0;
        bestTier = 2;
        for (int i = 0; i < merged.Count; i++) {
            LibraryTrackItem p = merged[i].Primary;
            if (p == null) continue;
            if (p.LastModifiedTicks > maxMod) maxMod = p.LastModifiedTicks;
            int tier = p.IsSpatial ? 0 : (p.IsSurround ? 1 : 2);
            if (tier < bestTier) bestTier = tier;
            if (sortMode == 3) {
                double sc = AudioQualityAnalyzer.EvaluateFast(p, this).Score;
                if (sc > maxScore) maxScore = sc;
            }
        }
    }

    void EnsureLibraryBaseIndex() {
        if (librarySnapshot == null) return;
        int spatialMode = Math.Clamp(store.LibraryFilterMode, 0, 2);
        int mediaMode = store.LibraryMediaTypeFilter;
        bool hideNoise = store.LibraryHideNonMusic;
        int sortMode = store.LibrarySortMode;
        int folderMode = store.LibraryFolderFilterMode;
        string foldersJson = store.LibraryFilterFoldersJson ?? "[]";

        if (cachedLibIndexSnap == librarySnapshot &&
            cachedLibIndexRevision == libraryFilterRevision &&
            cachedLibSpatialMode == spatialMode &&
            cachedLibMediaMode == mediaMode &&
            cachedLibHideNoise == hideNoise &&
            cachedLibSortMode == sortMode &&
            cachedLibFolderMode == folderMode &&
            string.Equals(cachedLibFoldersJson, foldersJson, StringComparison.Ordinal)) {
            return;
        }

        List<string> selectedFolders = store.GetLibraryFilterFolders();
        cachedVisibleTracks = MediaLibraryService.FilterTracks(librarySnapshot.Tracks, store);

        // Accurate Smart Clean count honoring ALL categories (including surround SFX & hidden folders) within media/spatial/folder filters
        List<LibraryTrackItem> scopeTracks = new(librarySnapshot.Tracks.Count);
        for (int i = 0; i < librarySnapshot.Tracks.Count; i++) {
            LibraryTrackItem t = librarySnapshot.Tracks[i];
            if (t != null &&
                MediaLibraryService.PassesFilter(t, spatialMode) &&
                MediaLibraryService.PassesMediaTypeFilter(t, mediaMode) &&
                MediaLibraryService.PassesFolderFilter(t.FolderPath, folderMode, selectedFolders)) {
                scopeTracks.Add(t);
            }
        }
        cachedSmartCleanEntries = MediaLibraryService.GetSmartCleanInspectionList(scopeTracks, store);
        cachedHiddenNoiseCount = hideNoise
            ? cachedSmartCleanEntries.Count
            : cachedSmartCleanEntries.Count(e => e.Category == SmartCleanCategory.UserHiddenFolder);

        HashSet<LibraryTrackItem> visSet = new(cachedVisibleTracks);

        // Tracks view: merge ATMOS+FLAC variants & sort
        List<LibraryMergedTrack> mergedTracksRaw = MediaLibraryService.MergeTrackVariants(cachedVisibleTracks, this);
        cachedMergedTracks = MediaLibraryService.SortMergedTracks(mergedTracksRaw, sortMode, this);

        // Albums view: merge variants & sort groups
        List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> albEntries = [];
        for (int i = 0; i < librarySnapshot.Albums.Count; i++) {
            LibraryAlbumGroup alb = librarySnapshot.Albums[i];
            List<LibraryTrackItem> vt = alb.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.MergeAlbumTrackVariants(vt, this);
            if (merged.Count == 0) continue;
            ComputeGroupSortMetrics(merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            albEntries.Add((alb, merged, maxMod, maxScore, bestTier));
        }
        albEntries.Sort((a, b) => sortMode switch {
            1 => string.Compare(a.Alb.Artist, b.Alb.Artist, StringComparison.OrdinalIgnoreCase) is int ac && ac != 0
                ? ac
                : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase),
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Alb.AlbumTitle, b.Alb.AlbumTitle, StringComparison.OrdinalIgnoreCase)
        });
        cachedAlbumEntries = albEntries;

        // Artists view: merge variants & sort groups
        List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> artEntries = [];
        for (int i = 0; i < librarySnapshot.Artists.Count; i++) {
            LibraryArtistGroup art = librarySnapshot.Artists[i];
            List<LibraryTrackItem> vt = art.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.SortMergedTracks(
                MediaLibraryService.MergeTrackVariants(vt, this), sortMode, this);
            if (merged.Count == 0) continue;
            ComputeGroupSortMetrics(merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            artEntries.Add((art, merged, maxMod, maxScore, bestTier));
        }
        artEntries.Sort((a, b) => sortMode switch {
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Art.Artist, b.Art.Artist, StringComparison.OrdinalIgnoreCase)
        });
        cachedArtistEntries = artEntries;

        // Folders view: merge variants & sort groups
        List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged, long MaxModTicks, double MaxScore, int BestTier)> fldEntries = [];
        for (int i = 0; i < librarySnapshot.Folders.Count; i++) {
            LibraryFolderGroup fld = librarySnapshot.Folders[i];
            List<LibraryTrackItem> vt = fld.Tracks.Where(visSet.Contains).ToList();
            if (vt.Count == 0) continue;
            List<LibraryMergedTrack> merged = MediaLibraryService.SortMergedTracks(
                MediaLibraryService.MergeTrackVariants(vt, this), sortMode, this);
            if (merged.Count == 0) continue;
            ComputeGroupSortMetrics(merged, sortMode, out long maxMod, out double maxScore, out int bestTier);
            fldEntries.Add((fld, merged, maxMod, maxScore, bestTier));
        }
        fldEntries.Sort((a, b) => sortMode switch {
            2 => b.MaxModTicks.CompareTo(a.MaxModTicks) is int tc && tc != 0
                ? tc
                : string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase),
            3 => a.BestTier.CompareTo(b.BestTier) is int bc && bc != 0
                ? bc
                : (b.MaxScore.CompareTo(a.MaxScore) is int sc && sc != 0
                    ? sc
                    : string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase)),
            _ => string.Compare(a.Fld.FolderName, b.Fld.FolderName, StringComparison.OrdinalIgnoreCase)
        });
        cachedFolderEntries = fldEntries;

        cachedLibIndexSnap = librarySnapshot;
        cachedLibIndexRevision = libraryFilterRevision;
        cachedLibSpatialMode = spatialMode;
        cachedLibMediaMode = mediaMode;
        cachedLibHideNoise = hideNoise;
        cachedLibSortMode = sortMode;
        cachedLibFolderMode = folderMode;
        cachedLibFoldersJson = foldersJson;
        cachedQueryRevision = -1;
    }

    void EnsureLibraryQueryCache(string q, LibraryCategory cat) {
        if (cachedQuerySnap == librarySnapshot &&
            cachedQueryRevision == libraryFilterRevision &&
            cachedQueryCat == cat &&
            string.Equals(cachedQueryStr, q, StringComparison.Ordinal)) {
            return;
        }
        bool hasQuery = !string.IsNullOrEmpty(q);
        if (cat == LibraryCategory.Albums) {
            cachedQueryAlbums = cachedAlbumEntries
                .Where(x => !hasQuery ||
                    x.Alb.AlbumTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Alb.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Alb, x.Merged))
                .ToList();
        } else if (cat == LibraryCategory.Tracks) {
            cachedQueryMergedTracks = hasQuery
                ? cachedMergedTracks.Where(m =>
                    m.Primary.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Primary.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Primary.Album.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Primary.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList()
                : cachedMergedTracks;
        } else if (cat == LibraryCategory.Artists) {
            cachedQueryArtists = cachedArtistEntries
                .Where(x => !hasQuery || x.Art.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Art, x.Merged))
                .ToList();
        } else {
            cachedQueryFolders = cachedFolderEntries
                .Where(x => !hasQuery ||
                    x.Fld.FolderName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Fld.FolderPath.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Fld, x.Merged))
                .ToList();
        }
        cachedQuerySnap = librarySnapshot;
        cachedQueryRevision = libraryFilterRevision;
        cachedQueryCat = cat;
        cachedQueryStr = q;
    }

    void SyncSmartCleanChipBadge() {
        if (libNoiseToggleBtn == null || libNoiseToggleText == null || libNoiseToggleIcon == null) return;
        bool on = store.LibraryHideNonMusic;
        int hiddenCount = librarySnapshot != null ? cachedHiddenNoiseCount : 0;
        libNoiseToggleText.Text = hiddenCount > 0
            ? string.Format(S["SmartCleanChipBadge"], hiddenCount)
            : S["LibHideNoise"];
        libNoiseToggleBtn.Background = on
            ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.Secondary, 115), OneUiTheme.PillCornerDp, OneUiTheme.Secondary, 1f)
            : (hiddenCount > 0
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(OneUiTheme.FunctionalAmber, 70), OneUiTheme.PillCornerDp, OneUiTheme.FunctionalAmber, 1f)
                : OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp));
        Color fg = on || hiddenCount > 0 ? Color.White : OneUiTheme.TextSub;
        libNoiseToggleIcon.IconColor = fg;
        libNoiseToggleText.SetTextColor(fg);
    }

    void PlayLibraryQueueShuffled(IReadOnlyList<LibraryTrackItem> queue) {
        if (queue == null || queue.Count == 0) return;
        List<LibraryTrackItem> shuffled = queue.Where(t => t != null).ToList();
        if (shuffled.Count == 0) return;
        for (int i = shuffled.Count - 1; i > 0; i--) {
            int j = Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        OpenLibraryTrack(shuffled[0], shuffled, autoPlay: true);
    }

    void HideFolderFromLibrary(LibraryFolderGroup fld) {
        if (fld == null || string.IsNullOrWhiteSpace(fld.FolderPath)) return;
        store.HideLibraryFolder(fld.FolderPath);
        if (selectedLibraryFolder == fld) {
            selectedLibraryFolder = null;
        }
        libraryRenderLimit = 72;
        InvalidateLibraryIndexCache();
        UpdateLibraryFilterStyles();
        RenderLibraryView();
        Toast.MakeText(this, string.Format(S["LibFolderHiddenToast"], fld.FolderName), ToastLength.Short).Show();
    }

    LinearLayout BuildGroupActionPill(
        OneUiIconType? icon,
        string label,
        Action onClick,
        bool primary = false,
        bool subtleDanger = false) {
        LinearLayout pill = HRow();
        pill.SetGravity(GravityFlags.Center);
        pill.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        pill.SetMinimumHeight(Dp(30));
        Color bg = primary
            ? ColAccent
            : (subtleDanger ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalRedFill, 42) : OneUiTheme.SurfaceSubTile);
        Color stroke = primary
            ? OneUiTheme.PrimaryHigh
            : (subtleDanger ? OneUiTheme.WithAlpha(OneUiTheme.FunctionalRed, 130) : Color.Argb(40, 255, 255, 255));
        pill.Background = primary
            ? OneUiTheme.CreateRippleRoundedDrawable(this, bg, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateRoundedDrawable(this, bg, OneUiTheme.PillCornerDp, stroke, 0.9f);
        OneUiTheme.AttachRecoil(pill, 0.94f);

        Color fg = subtleDanger ? OneUiTheme.FunctionalRed : ColText;
        if (icon.HasValue) {
            OneUiIconView ico = new(this, icon.Value, fg);
            pill.AddView(ico, new LinearLayout.LayoutParams(Dp(13), Dp(13)) {
                RightMargin = string.IsNullOrEmpty(label) ? 0 : Dp(5)
            });
        }
        if (!string.IsNullOrEmpty(label)) {
            TextView tv = Label(label, 11f, fg);
            tv.SetTypeface(OneUiTheme.GetAppleFontSemibold(this), TypefaceStyle.Bold);
            tv.SetIncludeFontPadding(false);
            tv.SetSingleLine(true);
            tv.Ellipsize = TextUtils.TruncateAt.End;
            pill.AddView(tv);
        }
        pill.Click += (_, _) => onClick?.Invoke();
        return pill;
    }

    LinearLayout BuildFoldersToolbar() {
        LinearLayout bar = HRow();
        bar.SetPadding(0, 0, 0, Dp(8));
        List<string> dirs = store.GetLibraryFilterFolders();
        bool scopeActive = store.LibraryFolderFilterMode > 0 && dirs.Count > 0;
        string scopeTxt = scopeActive ? string.Format(S["LibFoldersChipActive"], dirs.Count) : S["LibFoldersFilter"];
        LinearLayout scopePill = BuildGroupActionPill(OneUiIconType.FolderFilter, scopeTxt, ShowLibraryFolderFilterDialog, primary: scopeActive);
        LinearLayout explorerPill = BuildGroupActionPill(OneUiIconType.Folder, S["LibOpenExplorer"], () => OpenMediaWithExplorer(switchToTab: 0));
        bar.AddView(scopePill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
        bar.AddView(explorerPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return bar;
    }

    void RenderLibraryView() {
        if (libraryListHost == null) return;
        libraryListHost.RemoveAllViews();

        if (librarySnapshot == null) {
            libraryStatusText.Text = isLibraryScanning ? S["LibScanning"] : S["LibEmpty"];
            return;
        }

        EnsureLibraryBaseIndex();
        SyncSmartCleanChipBadge();

        string q = (librarySearchQuery ?? "").Trim();
        bool hasQuery = !string.IsNullOrEmpty(q);
        int visibleSpatial = cachedVisibleTracks.Count(t => t.IsSurround);
        string cacheTag = isIncrementalSyncRunning
            ? S["LibSyncing"]
            : string.Format(S["LibCacheSynced"], cachedVisibleTracks.Count, visibleSpatial);
        if (cachedHiddenNoiseCount > 0 && !isIncrementalSyncRunning) {
            cacheTag = $"{cacheTag} • {string.Format(S["LibHiddenNoiseCount"], cachedHiddenNoiseCount)}";
        }

        bool hasAnyActiveFilter = store.LibraryFilterMode > 0
            || store.LibraryMediaTypeFilter > 0
            || store.LibraryFolderFilterMode > 0
            || store.SmartCleanHiddenFolders.Count > 0
            || store.LibraryHideNonMusic;

        // Detail Drill-Down inside Album / Artist / Folder (all merged via MergeTrackVariants + paginated)
        if (selectedLibraryAlbum != null) {
            RenderAlbumDetailView(selectedLibraryAlbum, q);
            return;
        }
        if (selectedLibraryFolder != null) {
            var fldMatch = cachedFolderEntries.FirstOrDefault(f => f.Fld == selectedLibraryFolder ||
                string.Equals(f.Fld.FolderPath, selectedLibraryFolder.FolderPath, StringComparison.OrdinalIgnoreCase));
            List<LibraryMergedTrack> fldMerged = fldMatch.Fld != null
                ? fldMatch.Merged
                : MediaLibraryService.SortMergedTracks(
                    MediaLibraryService.MergeTrackVariants(FilterLibraryTracks(selectedLibraryFolder.Tracks), this),
                    store.LibrarySortMode, this);
            if (hasQuery) {
                fldMerged = fldMerged.Where(m =>
                    m.Primary.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Primary.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Primary.Album.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            LibraryFolderGroup capturedFld = selectedLibraryFolder;
            RenderGroupTracksDrilldown(
                selectedLibraryFolder.FolderName,
                $"{string.Format(S["LibSongsCount"], fldMerged.Count)} • {selectedLibraryFolder.FolderPath}",
                fldMerged,
                () => { selectedLibraryFolder = null; RenderLibraryView(); },
                onHideFolder: () => HideFolderFromLibrary(capturedFld));
            return;
        }
        if (!string.IsNullOrEmpty(selectedLibraryArtist)) {
            var artMatch = cachedArtistEntries.FirstOrDefault(a =>
                string.Equals(a.Art.Artist, selectedLibraryArtist, StringComparison.OrdinalIgnoreCase));
            LibraryArtistGroup art = artMatch.Art ?? librarySnapshot.Artists.FirstOrDefault(a =>
                string.Equals(a.Artist, selectedLibraryArtist, StringComparison.OrdinalIgnoreCase));
            if (art != null) {
                List<LibraryMergedTrack> artMerged = artMatch.Art != null
                    ? artMatch.Merged
                    : MediaLibraryService.SortMergedTracks(
                        MediaLibraryService.MergeTrackVariants(FilterLibraryTracks(art.Tracks), this),
                        store.LibrarySortMode, this);
                if (hasQuery) {
                    artMerged = artMerged.Where(m =>
                        m.Primary.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.Primary.Album.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                RenderGroupTracksDrilldown(
                    art.Artist,
                    string.Format(S["LibSongsCount"], artMerged.Count),
                    artMerged,
                    () => { selectedLibraryArtist = null; RenderLibraryView(); });
                return;
            }
        }

        EnsureLibraryQueryCache(q, libraryCategory);

        if (libraryCategory == LibraryCategory.Albums) {
            List<(LibraryAlbumGroup Alb, List<LibraryMergedTrack> Merged)> filtered = cachedQueryAlbums;
            libraryStatusText.Text = $"{filtered.Count} {S["LibAlbums"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i += 2) {
                LinearLayout gridRow = new(this) {
                    Orientation = Orientation.Horizontal,
                    BaselineAligned = false
                };
                gridRow.AddView(BuildAlbumTileCard(filtered[i].Alb, filtered[i].Merged), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });
                if (i + 1 < showCount) {
                    gridRow.AddView(BuildAlbumTileCard(filtered[i + 1].Alb, filtered[i + 1].Merged), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                } else {
                    gridRow.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 1f));
                }
                libraryListHost.AddView(gridRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(10)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else if (libraryCategory == LibraryCategory.Tracks) {
            List<LibraryMergedTrack> filtered = cachedQueryMergedTracks;
            libraryStatusText.Text = $"{string.Format(S["LibSongsCount"], filtered.Count)} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0);
                return;
            }

            List<LibraryTrackItem> queue = filtered.Select(PickPreferredVariant).Where(t => t != null).ToList();
            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                LibraryMergedTrack m = filtered[i];
                LibraryTrackItem chosen = queue[i];
                libraryListHost.AddView(
                    BuildLibraryTrackRow(
                        chosen,
                        queue,
                        i,
                        showThumb: true,
                        badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                        allVariantsForHighlight: m.Variants),
                    new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                        BottomMargin = Dp(6)
                    });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else if (libraryCategory == LibraryCategory.Artists) {
            List<(LibraryArtistGroup Art, List<LibraryMergedTrack> Merged)> filtered = cachedQueryArtists;
            libraryStatusText.Text = $"{filtered.Count} {S["LibArtists"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                LibraryArtistGroup art = filtered[i].Art;
                List<LibraryMergedTrack> merged = filtered[i].Merged;
                List<LibraryTrackItem> artQueue = merged.Select(PickPreferredVariant).Where(t => t != null).ToList();
                int albumCnt = merged.Select(m => string.IsNullOrWhiteSpace(m.Primary.Album) ? m.Primary.FolderPath : m.Primary.Album)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                LinearLayout row = BuildGroupRow(
                    art.Artist,
                    $"{string.Format(S["LibAlbumsCount"], Math.Max(1, albumCnt))} • {string.Format(S["LibSongsCount"], merged.Count)}",
                    artQueue.FirstOrDefault(),
                    artQueue,
                    () => {
                        selectedLibraryArtist = art.Artist;
                        libraryRenderLimit = 72;
                        RenderLibraryView();
                    });
                libraryListHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        } else {
            libraryListHost.AddView(BuildFoldersToolbar());
            List<(LibraryFolderGroup Fld, List<LibraryMergedTrack> Merged)> filtered = cachedQueryFolders;
            libraryStatusText.Text = $"{filtered.Count} {S["LibFolders"].ToLowerInvariant()} • {cacheTag}";
            if (filtered.Count == 0) {
                RenderLibraryEmptyCard(hasAnyActiveFilter && librarySnapshot.Tracks.Count > 0);
                return;
            }

            int showCount = Math.Min(filtered.Count, libraryRenderLimit);
            for (int i = 0; i < showCount; i++) {
                LibraryFolderGroup fld = filtered[i].Fld;
                List<LibraryMergedTrack> merged = filtered[i].Merged;
                List<LibraryTrackItem> fldQueue = merged.Select(PickPreferredVariant).Where(t => t != null).ToList();
                LibraryFolderGroup capturedFld = fld;
                LinearLayout row = BuildGroupRow(
                    fld.FolderName,
                    $"{string.Format(S["LibSongsCount"], merged.Count)} • {fld.FolderPath}",
                    fldQueue.FirstOrDefault(),
                    fldQueue,
                    () => {
                        selectedLibraryFolder = capturedFld;
                        libraryRenderLimit = 72;
                        RenderLibraryView();
                    },
                    isFolder: true,
                    onHideFolder: () => HideFolderFromLibrary(capturedFld));
                libraryListHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                    BottomMargin = Dp(6)
                });
            }
            AddLoadMoreIfNeeded(filtered.Count, showCount);
        }
    }

    void AddLoadMoreIfNeeded(int totalCount, int shown) {
        if (shown >= totalCount) return;
        Button more = PillButton(string.Format(S["LibShowMore"], totalCount - shown), () => {
            libraryRenderLimit += 72;
            RenderLibraryView();
        });
        libraryListHost.AddView(more, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(6)
        });
    }

    void RenderLibraryEmptyCard(bool isFilteredEmpty = false) {
        LinearLayout card = Card();
        TextView tv = Label(isFilteredEmpty ? S["LibEmptyFilteredMedia"] : S["LibEmpty"], 12.5f, OneUiTheme.TextSub);
        tv.SetPadding(0, 0, 0, Dp(10));
        card.AddView(tv);
        if (isFilteredEmpty) {
            LinearLayout btns = HRow();
            Button showAllBtn = PrimaryButton(S["LibResetFiltersBtn"], () => {
                store.LibraryFilterMode = 0;
                store.LibraryMediaTypeFilter = 0;
                store.LibraryFolderFilterMode = 0;
                InvalidateLibraryIndexCache();
                UpdateLibraryFilterStyles();
                RenderLibraryView();
            });
            btns.AddView(showAllBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (cachedHiddenNoiseCount > 0) {
                Button inspectBtn = PillButton(string.Format(S["LibInspectSmartCleanBtn"], cachedHiddenNoiseCount), ShowSmartCleanInspectorModal);
                btns.AddView(inspectBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(8) });
            }
            card.AddView(btns);
        } else {
            Button openPicker = PrimaryButton(S["LibOpenExplorer"], () => OpenMediaWithExplorer(switchToTab: 0));
            card.AddView(openPicker);
        }
        libraryListHost.AddView(card);
    }

    LinearLayout BuildAlbumTileCard(LibraryAlbumGroup alb, List<LibraryMergedTrack> visibleMerged) {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        Typeface sfReg = OneUiTheme.GetAppleFontRegular(this);

        LinearLayout tile = new(this) { Orientation = Orientation.Vertical };
        tile.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 18f);
        tile.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        OneUiTheme.AttachRecoil(tile, 0.96f);

        LibraryTrackItem thumbSample = visibleMerged?.FirstOrDefault()?.Primary ?? alb.Tracks.FirstOrDefault();
        int trackCount = visibleMerged?.Count ?? alb.Tracks.Count;

        RoundedArtworkView art = new(this) {
            PreferredSizeDp = 148,
            CornerDp = 12f
        };
        BindThumbAsync(art, thumbSample, alb.AlbumTitle);
        tile.AddView(art, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView titleTv = Label(alb.AlbumTitle, 13f, ColText);
        titleTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(titleTv);

        TextView subTv = Label($"{alb.Artist} • {trackCount}", 11f, OneUiTheme.TextMuted);
        subTv.SetTypeface(sfReg, TypefaceStyle.Normal);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(subTv);

        tile.Click += (_, _) => {
            selectedLibraryAlbum = alb;
            libraryRenderLimit = 72;
            RenderLibraryView();
        };
        return tile;
    }

    LibraryTrackItem PickPreferredVariant(LibraryMergedTrack m) {
        if (m == null) return null;
        if (m.Variants.Count > 1) {
            if (preferredAudioCategory.HasValue) {
                LibraryTrackItem smartPick = AudioQualityAnalyzer.PickBestVariantForNextTrack(m.Variants, preferredAudioCategory.Value, this);
                if (smartPick != null) return smartPick;
            }
            if (!string.IsNullOrEmpty(preferredFormatVariantKey)) {
                LibraryTrackItem pref = m.Variants.FirstOrDefault(v =>
                    string.Equals(MediaLibraryService.GetDistinctVariantLabel(v, m.Variants), preferredFormatVariantKey, StringComparison.OrdinalIgnoreCase));
                if (pref != null) return pref;
            }
        }
        return m.Primary;
    }

    void RenderAlbumDetailView(LibraryAlbumGroup alb, string q = "") {
        Typeface sfBold = OneUiTheme.GetAppleFontBold(this);
        List<LibraryTrackItem> filteredRaw = FilterLibraryTracks(alb.Tracks);
        if (filteredRaw.Count == 0 &&
            store.LibraryFilterMode == 0 &&
            store.LibraryMediaTypeFilter == 0 &&
            store.LibraryFolderFilterMode == 0 &&
            store.SmartCleanHiddenFolders.Count == 0 &&
            !store.LibraryHideNonMusic) {
            filteredRaw = alb.Tracks;
        }
        List<LibraryMergedTrack> merged = MediaLibraryService.MergeAlbumTrackVariants(filteredRaw, this);
        if (!string.IsNullOrEmpty(q)) {
            merged = merged.Where(m =>
                m.Primary.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Primary.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        List<LibraryTrackItem> albumQueue = merged.Select(PickPreferredVariant).Where(t => t != null).ToList();

        libraryStatusText.Text = $"{alb.AlbumTitle} — {string.Format(S["LibSongsCount"], merged.Count)}";

        LinearLayout hdrCard = Card();
        LinearLayout backRow = HRow();
        backRow.SetPadding(0, 0, 0, Dp(10));
        LinearLayout backPill = OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["LibAlbums"], () => {
            selectedLibraryAlbum = null;
            libraryRenderLimit = 72;
            RenderLibraryView();
        });
        backRow.AddView(backPill);
        hdrCard.AddView(backRow);

        LinearLayout hero = HRow();
        RoundedArtworkView cover = new(this) { PreferredSizeDp = 88, CornerDp = 14f };
        BindThumbAsync(cover, albumQueue.FirstOrDefault(), alb.AlbumTitle);
        hero.AddView(cover, new LinearLayout.LayoutParams(Dp(88), Dp(88)) { RightMargin = Dp(12) });

        LinearLayout meta = new(this) { Orientation = Orientation.Vertical };
        TextView tTv = Label(alb.AlbumTitle, 16f, ColText);
        tTv.SetTypeface(sfBold, TypefaceStyle.Bold);
        TextView aTv = Label(alb.Artist, 13f, OneUiTheme.PeachText);
        meta.AddView(tTv);
        meta.AddView(aTv);

        LinearLayout albActions = HRow();
        albActions.SetPadding(0, Dp(8), 0, 0);
        LinearLayout playPill = BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
            if (albumQueue.Count > 0) {
                OpenLibraryTrack(albumQueue[0], albumQueue, autoPlay: true);
            }
        }, primary: true);
        LinearLayout shufPill = BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(albumQueue));
        albActions.AddView(playPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        albActions.AddView(shufPill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        meta.AddView(albActions);

        hero.AddView(meta, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        hdrCard.AddView(hero);
        libraryListHost.AddView(hdrCard);

        if (merged.Count == 0) {
            RenderLibraryEmptyCard(true);
            return;
        }

        bool multiDisc = merged.Select(m => m.Primary.DiscNumber).Where(d => d > 0).Distinct().Count() > 1;
        int lastDisc = -1;
        int showCount = Math.Min(merged.Count, libraryRenderLimit);
        for (int i = 0; i < showCount; i++) {
            LibraryMergedTrack m = merged[i];
            if (multiDisc && m.Primary.DiscNumber > 0 && m.Primary.DiscNumber != lastDisc) {
                lastDisc = m.Primary.DiscNumber;
                TextView discTv = Label(string.Format(S["LibDiscHeader"], lastDisc), 11.5f, OneUiTheme.PeachText);
                discTv.SetTypeface(sfBold, TypefaceStyle.Bold);
                discTv.SetPadding(Dp(6), Dp(8), Dp(6), Dp(4));
                libraryListHost.AddView(discTv);
            }
            LibraryTrackItem chosen = albumQueue[i];
            libraryListHost.AddView(
                BuildLibraryTrackRow(
                    chosen,
                    albumQueue,
                    i,
                    showThumb: false,
                    badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                    allVariantsForHighlight: m.Variants),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }
        AddLoadMoreIfNeeded(merged.Count, showCount);
    }

    void RenderGroupTracksDrilldown(
        string title,
        string sub,
        List<LibraryMergedTrack> mergedTracks,
        Action onBack,
        Action onHideFolder = null) {
        libraryStatusText.Text = $"{title} • {sub}";
        List<LibraryTrackItem> queue = mergedTracks.Select(PickPreferredVariant).Where(t => t != null).ToList();

        LinearLayout hdrCard = Card();
        LinearLayout topRow = HRow();
        topRow.SetPadding(0, 0, 0, Dp(8));
        topRow.AddView(OneUiPillFactory.CreateIconActionPill(this, OneUiIconType.ChevronLeft, S["PlayerLibrary"], onBack));
        hdrCard.AddView(topRow);

        TextView titleTv = Label(title, 15.5f, ColText);
        titleTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;
        hdrCard.AddView(titleTv);

        TextView subTv = Label(sub, 11.5f, OneUiTheme.TextMuted);
        subTv.SetSingleLine(true);
        subTv.Ellipsize = TextUtils.TruncateAt.Middle;
        subTv.SetPadding(0, Dp(2), 0, Dp(8));
        hdrCard.AddView(subTv);

        HorizontalScrollView actScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        LinearLayout actRow = HRow();
        actRow.AddView(
            BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
                if (queue.Count > 0) OpenLibraryTrack(queue[0], queue, autoPlay: true);
            }, primary: true),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        actRow.AddView(
            BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(queue)),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                RightMargin = onHideFolder != null ? Dp(6) : 0
            });
        if (onHideFolder != null) {
            actRow.AddView(
                BuildGroupActionPill(OneUiIconType.CleanFilter, S["LibHideFolderBtn"], onHideFolder, subtleDanger: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
        actScroll.AddView(actRow);
        hdrCard.AddView(actScroll);
        libraryListHost.AddView(hdrCard);

        if (mergedTracks.Count == 0) {
            RenderLibraryEmptyCard(true);
            return;
        }

        int showCount = Math.Min(mergedTracks.Count, libraryRenderLimit);
        for (int i = 0; i < showCount; i++) {
            LibraryMergedTrack m = mergedTracks[i];
            LibraryTrackItem chosen = queue[i];
            libraryListHost.AddView(
                BuildLibraryTrackRow(
                    chosen,
                    queue,
                    i,
                    showThumb: true,
                    badgeOverride: m.Variants.Count > 1 ? m.CombinedBadgeLabel : null,
                    allVariantsForHighlight: m.Variants),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });
        }
        AddLoadMoreIfNeeded(mergedTracks.Count, showCount);
    }

    LinearLayout BuildGroupRow(
        string title,
        string sub,
        LibraryTrackItem sample,
        List<LibraryTrackItem> queue,
        Action onOpen,
        bool isFolder = false,
        Action onHideFolder = null) {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.Background = OneUiTheme.CreateGlassCardDrawable(this, OneUiTheme.SurfaceCard, 18f);
        card.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        OneUiTheme.AttachRecoil(card, 0.97f);

        LinearLayout topRow = HRow();
        if (isFolder) {
            topRow.AddView(OneUiTheme.CreateVectorIconBadge(this, OneUiIconType.Folder, OneUiTheme.IconStudioExport, 40),
                new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(12) });
        } else {
            RoundedArtworkView thumb = new(this) { PreferredSizeDp = 42, CornerDp = 10f };
            BindThumbAsync(thumb, sample, title);
            topRow.AddView(thumb, new LinearLayout.LayoutParams(Dp(42), Dp(42)) { RightMargin = Dp(12) });
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
        topRow.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(topRow);

        HorizontalScrollView actScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        LinearLayout actRow = HRow();
        actRow.AddView(
            BuildGroupActionPill(OneUiIconType.Play, S["Play"], () => {
                if (queue != null && queue.Count > 0) OpenLibraryTrack(queue[0], queue, autoPlay: true);
            }, primary: true),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
        actRow.AddView(
            BuildGroupActionPill(null, S["LibShuffleBtn"], () => PlayLibraryQueueShuffled(queue)),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                RightMargin = isFolder && onHideFolder != null ? Dp(6) : 0
            });
        if (isFolder && onHideFolder != null) {
            actRow.AddView(
                BuildGroupActionPill(OneUiIconType.CleanFilter, S["LibHideFolderBtn"], onHideFolder, subtleDanger: true),
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        }
        actScroll.AddView(actRow);
        card.AddView(actScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(8)
        });

        topRow.Click += (_, _) => onOpen?.Invoke();
        card.Click += (_, _) => onOpen?.Invoke();
        return card;
    }

    LinearLayout BuildLibraryTrackRow(
        LibraryTrackItem item,
        IReadOnlyList<LibraryTrackItem> queue,
        int indexInQueue,
        bool showThumb = true,
        string badgeOverride = null,
        IReadOnlyList<LibraryTrackItem> allVariantsForHighlight = null) {
        bool isPlayingThis = LoadedFile != null && (
            string.Equals(LoadedFile.Path, item.FilePath, StringComparison.OrdinalIgnoreCase) ||
            (allVariantsForHighlight != null && allVariantsForHighlight.Any(v => string.Equals(LoadedFile.Path, v.FilePath, StringComparison.OrdinalIgnoreCase))));

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

        // Spatial / Container format badge (or merged ATMOS + FLAC badge) & duration column
        LinearLayout rightCol = new(this) { Orientation = Orientation.Vertical };
        rightCol.SetGravity(GravityFlags.End);
        string badgeLabel = !string.IsNullOrEmpty(badgeOverride)
            ? badgeOverride
            : (!string.IsNullOrEmpty(item.AudioBadge) && item.AudioBadge != item.FormatBadge
                ? $"{item.FormatBadge} • {item.AudioBadge}"
                : item.FormatBadge);
        if (string.IsNullOrWhiteSpace(badgeLabel)) {
            badgeLabel = item.IsVideo ? S["LibVideoBadge"] : S["AudioBadgeDefault"];
        }
        bool anySpatial = item.IsSpatial || (allVariantsForHighlight?.Any(v => v.IsSpatial) ?? false);
        bool anySurround = item.IsSurround || (allVariantsForHighlight?.Any(v => v.IsSurround) ?? false);
        Color badgeTextCol = anySpatial
            ? OneUiTheme.PrimaryHigh
            : (anySurround ? OneUiTheme.PeachText : OneUiTheme.TextMuted);
        Color badgeBgCol = anySpatial
            ? OneUiTheme.WithAlpha(OneUiTheme.Primary, 52)
            : Color.Argb(40, 255, 255, 255);
        TextView fmtTv = Label(badgeLabel, 9.5f, badgeTextCol);
        fmtTv.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
        fmtTv.SetSingleLine(true);
        fmtTv.Ellipsize = TextUtils.TruncateAt.End;
        fmtTv.SetMaxWidth(Dp(126));
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

    void OpenLibraryTrack(LibraryTrackItem item, IReadOnlyList<LibraryTrackItem> queue, bool autoPlay, bool preserveCategoryPreference = false) {
        if (item == null) return;
        if (!preserveCategoryPreference) {
            preferredAudioCategory = AudioQualityAnalyzer.ClassifyCategory(item);
        }
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

        EditText MakeModalInput(string initialText, string hint) {
            EditText et = new(this) {
                Text = initialText ?? "",
                Hint = hint ?? "",
                TextSize = 13f
            };
            et.SetTextColor(ColText);
            et.SetHintTextColor(ColMuted);
            et.SetSingleLine(true);
            et.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            et.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
            return et;
        }

        string defTrack = OnlineMetadataService.CleanSearchTerm(
            !string.IsNullOrWhiteSpace(currentMediaMeta?.Title)
                ? currentMediaMeta.Title
                : (LoadedFile != null ? Path.GetFileNameWithoutExtension(LoadedFile.Path) : ""));
        string defArtist = currentMediaMeta?.Artist ?? "";
        string defAlbum = currentMediaMeta?.Album ?? "";

        TextView trackLbl = Label(S["OnlineTrackLabel"], 11f, OneUiTheme.TextMuted);
        trackLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(trackLbl);
        EditText queryInput = MakeModalInput(defTrack, S["OnlineSearchQueryHint"]);
        box.AddView(queryInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView artistLbl = Label(S["OnlineArtistLabel"], 11f, OneUiTheme.TextMuted);
        artistLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(artistLbl);
        EditText artistInput = MakeModalInput(defArtist, "");
        box.AddView(artistInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        TextView albumLbl = Label(S["OnlineAlbumLabel"], 11f, OneUiTheme.TextMuted);
        albumLbl.SetPadding(Dp(2), 0, Dp(2), Dp(3));
        box.AddView(albumLbl);
        EditText albumInput = MakeModalInput(defAlbum, "");
        box.AddView(albumInput, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(8)
        });

        AddSwitchRow(box, S["OnlineAutoBestSwitch"], store.OnlineAutoSelectBestMatch, v => store.OnlineAutoSelectBestMatch = v);

        LinearLayout resultsHost = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void ApplyCoverCandidate(OnlineCoverCandidate c, string fallbackTitle) {
            dlg?.Dismiss();
            string tName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Title) ? currentMediaMeta.Title : fallbackTitle;
            string arName = !string.IsNullOrWhiteSpace(currentMediaMeta?.Artist) ? currentMediaMeta.Artist : c.Artist;
            string alName = !string.IsNullOrWhiteSpace(albumInput.Text) ? albumInput.Text.Trim()
                : (!string.IsNullOrWhiteSpace(currentMediaMeta?.Album) ? currentMediaMeta.Album : c.Album);
            string pathOvr = currentMediaMeta?.SourceMediaPath ?? LoadedFile?.Path;
            bool autoPicked = store.OnlineAutoSelectBestMatch && c.IsExactMatch;
            Task.Run(async () => {
                var (bmp, _) = await OnlineMetadataService.DownloadAndCacheCoverAsync(
                    this, c.ImageUrl, tName, arName, alName, filePathOverride: pathOvr).ConfigureAwait(false);
                RunOnUiThread(() => {
                    if (uiDestroyed || !IsBitmapUsable(bmp)) return;
                    if (!string.IsNullOrEmpty(pathOvr)) {
                        MediaLibraryService.InvalidateThumbCache(pathOvr);
                    }
                    if (currentMediaMeta != null) {
                        currentMediaMeta.ArtworkBitmap = bmp;
                        currentMediaMeta.HasEmbeddedArtwork = true;
                        MediaMetadataHelper.ExtractPalette(bmp, out Color p, out Color s);
                        currentMediaMeta.PrimaryAccent = p;
                        currentMediaMeta.SecondaryAccent = s;
                    }
                    RefreshMusicPlayerUi();
                    if (isLibrarySectionOpen) {
                        RenderLibraryView();
                    }
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
            string searchTitle = queryInput.Text?.Trim() ?? "";
            string searchArtist = artistInput.Text?.Trim() ?? "";
            string searchAlbum = albumInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(searchTitle) && string.IsNullOrEmpty(searchAlbum)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            bool isVid = currentMediaMeta?.HasVideo == true;
            string q = string.IsNullOrWhiteSpace(searchTitle) ? searchAlbum : searchTitle;

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
                        ApplyCoverCandidate(exactBest, q);
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
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = c.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
                        TextView t1 = Label($"{c.Title}{exactTag}", 12.5f, ColText);
                        t1.SetTypeface(OneUiTheme.GetAppleFontBold(this), TypefaceStyle.Bold);
                        TextView t2 = Label($"{c.Artist} • {c.Album} ({c.Source})", 11f, c.IsExactMatch ? OneUiTheme.PrimaryHigh : OneUiTheme.TextMuted);
                        col.AddView(t1);
                        col.AddView(t2);
                        row.AddView(col, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                        row.Click += (_, _) => ApplyCoverCandidate(c, q);
                        resultsHost.AddView(row, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
                            BottomMargin = Dp(6)
                        });
                    }
                });
            });
        }

        void RunLyricsSearch() {
            string titleArg = queryInput.Text?.Trim() ?? "";
            string artistArg = artistInput.Text?.Trim() ?? "";
            string albumArg = albumInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(titleArg)) return;
            resultsHost.RemoveAllViews();
            resultsHost.AddView(Label(S["OnlineSearching"], 12f, OneUiTheme.PeachText));

            double durArg = playerEngine?.TotalSeconds ?? 0;

            Task.Run(async () => {
                List<OnlineLyricsCandidate> cands = await OnlineMetadataService.SearchLyricsAsync(
                    titleArg, artistArg, albumArg, expectedDurationSec: durArg).ConfigureAwait(false);
                if (cands.Count == 0 && (!string.IsNullOrEmpty(artistArg) || !string.IsNullOrEmpty(albumArg))) {
                    cands = await OnlineMetadataService.SearchLyricsAsync(titleArg, "", "", expectedDurationSec: durArg).ConfigureAwait(false);
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
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string syncBadge = c.IsSynced
                            ? string.Format(S["OnlineSyncedLyricsBadge"], c.Source)
                            : string.Format(S["OnlinePlainLyricsBadge"], c.Source);
                        string exactTag = c.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
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

    void ShowSubtitlesDialog() {
        LinearLayout list = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        void AddOptionRow(string labelText, bool selected, Action onClick) {
            LinearLayout row = HRow();
            row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            row.Background = selected
                ? OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.WithAlpha(ColAccent, 85), 16f, OneUiTheme.PrimaryHigh, 1f)
                : OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 16f, OneUiTheme.BorderSubtle, 1f);
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
            OpenFileWithExplorerForExtensions(
                S["VideoSubtitlesTitle"],
                [".srt", ".vtt", ".ass", ".ssa", ".sub", ".txt"],
                LoadExternalSubtitleFromPath,
                ReqLoadSubtitles);
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
        qInput.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
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
                List<OnlineSubtitleCandidate> list = await OnlineMetadataService.SearchSubtitlesAsync(q, S["SubtitlesSearchLangCode"]).ConfigureAwait(false);
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
                        row.Background = OneUiTheme.CreateRoundedDrawable(this, OneUiTheme.SurfaceModalRow, 14f, OneUiTheme.BorderSubtle, 1f);
                        OneUiTheme.AttachRecoil(row, 0.97f);

                        LinearLayout col = new(this) { Orientation = Orientation.Vertical };
                        string exactTag = cand.IsExactMatch ? S["OnlineExactMatchBadge"] : "";
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
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
                dlg.Window.SetDecorFitsSystemWindows(false);
                if (dlg.Window.Attributes != null) {
                    dlg.Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
                    dlg.Window.Attributes = dlg.Window.Attributes;
                }
                fsRoot.SetOnApplyWindowInsetsListener(new RootInsetsListener(insets => {
                    if (insets == null) return;
                    Android.Graphics.Insets cutout = insets.GetInsets(WindowInsets.Type.DisplayCutout());
                    fsRoot.SetPadding(cutout.Left, cutout.Top, cutout.Right, cutout.Bottom);
                }));
            } else {
#pragma warning disable CS0618, CA1422
                dlg.Window.AddFlags(WindowManagerFlags.Fullscreen);
#pragma warning restore CS0618, CA1422
            }
        }
        dlg.DismissEvent += (_, _) => ExitVideoFullscreenIfNeeded();

        fullscreenVideoDlg = dlg;
        SyncPredictiveBackRegistration();
        RequestedOrientation = ScreenOrientation.SensorLandscape;
        dlg.Show();
        dlg.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
            IWindowInsetsController ctrl = dlg.Window?.InsetsController;
            if (ctrl != null) {
                ctrl.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                ctrl.Hide(WindowInsets.Type.SystemBars());
            }
            fsRoot.RequestApplyInsets();
        }
        musicVideoStage.SetFullscreenMode(true);
    }

    void ExitVideoFullscreenIfNeeded() {
        Android.App.Dialog dlg = fullscreenVideoDlg;
        if (dlg == null) {
            return;
        }
        fullscreenVideoDlg = null;
        SyncPredictiveBackRegistration();

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
