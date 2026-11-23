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
    int libraryRenderGeneration;
    MediaLibrarySnapshot librarySnapshot;
    bool isLibraryScanning;
    int onlineEnrichGeneration;

    readonly List<LibraryTrackItem> activeLibraryQueue = [];
    int activeLibraryQueueIndex = -1;

    // Section switcher inside Play tab (Now Playing vs In-App Library)
    LinearLayout musicTabRootPage;
    ScrollView musicNowPlayingScrollView;
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
    int libraryIndexRequestSeq;
    volatile bool isLibraryIndexBuilding;
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
    LinearLayout musicLyricsSingToggleBtn;
    OneUiIconView musicLyricsSingToggleIcon;
    TextView musicLyricsSingToggleText;
    bool isLyricsSingDockExpanded;
    OneUiIconView musicLyricsSingMicIcon;
    TextView musicLyricsSingPctText;
    OneUiSeekBar musicLyricsVocalSeekBar;
    Button musicLyricsKaraokeBtn, musicLyricsLiveMicBtn, musicLyricsAiPrepBtn, musicLyricsMixMatchBtn, musicLyricsVocalAbBtn, musicLyricsScarlettBtn;
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
    int lastMusicViewportPadPx = -1;
    bool lastMusicViewportLandscape;
    PlayerStageMode? lastMusicViewportStageMode;
    LinearLayout musicTransportRow, musicVolRow, musicSpatialToggleRow, musicBottomActionRow;

    FrameLayout musicSeekBackBtn, musicRewBtn, musicPlayCircleBtn, musicFwdBtn, musicSeekFwdBtn;
    OneUiIconView musicPlayIcon;
    OneUiSeekBar musicVolSeekBar;

    // Flanking Spatial Toggles ([Cavernize 3D] | [HRTF 3D] | [Matrix 7.1] | [AI 3D]) + Integrated Bottom Utility Row
    LinearLayout musicCavernizeToggleBtn, musicBottomSpatialBtn, musicMatrixToggleBtn, musicAiToggleBtn;
    OneUiIconView musicCavernizeIcon, musicBottomSpatialIcon, musicMatrixIcon, musicAiToggleIcon;
    TextView musicCavernizeText, musicBottomSpatialText, musicMatrixText, musicAiToggleText;

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
        segNowPlayingBtn.Ellipsize = TextUtils.TruncateAt.End;
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
        segLibraryBtn.Ellipsize = TextUtils.TruncateAt.End;
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

        musicLyricsSingToggleBtn = new LinearLayout(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false,
            Clickable = true,
            Focusable = true,
            ContentDescription = S["LyricsSingToggleDesc"]
        };
        musicLyricsSingToggleBtn.SetGravity(GravityFlags.CenterVertical);
        musicLyricsSingToggleBtn.SetMinimumHeight(Dp(34));
        musicLyricsSingToggleBtn.SetPadding(Dp(11), Dp(6), Dp(12), Dp(6));
        musicLyricsSingToggleBtn.Background = OneUiTheme.CreateRoundedDrawable(
            this, Color.Argb(38, 255, 255, 255), 99f, Color.Argb(58, 255, 255, 255), 0.9f);
        musicLyricsSingToggleIcon = new OneUiIconView(this, OneUiIconType.Microphone, Color.Argb(230, 252, 252, 255));
        musicLyricsSingToggleBtn.AddView(musicLyricsSingToggleIcon, new LinearLayout.LayoutParams(Dp(15), Dp(15)) { RightMargin = Dp(5) });
        musicLyricsSingToggleText = Label(S["LyricsSingTogglePill"], 11.5f, Color.Argb(230, 252, 252, 255));
        musicLyricsSingToggleText.SetTypeface(sfSemi, TypefaceStyle.Bold);
        musicLyricsSingToggleText.SetIncludeFontPadding(false);
        musicLyricsSingToggleText.SetSingleLine(true);
        musicLyricsSingToggleText.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsSingToggleBtn.AddView(musicLyricsSingToggleText);
        OneUiTheme.AttachRecoil(musicLyricsSingToggleBtn, 0.94f);
        musicLyricsSingToggleBtn.Click += (_, _) => {
            isLyricsSingDockExpanded = !isLyricsSingDockExpanded;
            if (musicLyricsSingDock != null) {
                musicLyricsSingDock.Visibility = isLyricsSingDockExpanded ? ViewStates.Visible : ViewStates.Gone;
            }
            RefreshLyricsSingDockUi();
        };
        lyricsHeader.AddView(musicLyricsSingToggleBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            LeftMargin = Dp(8)
        });
        musicLyricsContainer.AddView(lyricsHeader);

        musicLyricsView = new AppleMusicLyricsView(this);
        musicLyricsView.LineSeekRequested += sec => SeekAbsoluteSeconds(sec);
        musicLyricsContainer.AddView(musicLyricsView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        // W14/W27: 2-Tier Apple Music Sing Dock (collapsed by default; expanded on demand via lyricsHeader Sing pill)
        musicLyricsSingDock = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        musicLyricsSingDock.Background = OneUiTheme.CreateRoundedDrawable(
            this, Color.Argb(44, 20, 22, 32), 18f, Color.Argb(48, 255, 255, 255), 0.9f);
        musicLyricsSingDock.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));

        // Tier 1 Header: Mic icon + Vocals label + un-crushed % readout + right-aligned Vocal DNA / Voiceprint status pill
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
        musicLyricsSingPctText.SetIncludeFontPadding(false);
        musicLyricsSingPctText.SetSingleLine(true);
        musicLyricsSingPctText.SetMinimumWidth(Dp(40));
        singTopRow.AddView(musicLyricsSingPctText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });

        LinearLayout singDnaPillHost = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        singDnaPillHost.SetGravity(GravityFlags.End | GravityFlags.CenterVertical);

        musicLyricsAiStatusText = Label(S["SingDockDnaIdlePill"], 10.2f, OneUiTheme.TextSecondary);
        musicLyricsAiStatusText.SetTypeface(sfSemi, TypefaceStyle.Bold);
        musicLyricsAiStatusText.SetIncludeFontPadding(false);
        musicLyricsAiStatusText.SetSingleLine(true);
        musicLyricsAiStatusText.Ellipsize = TextUtils.TruncateAt.End;
        musicLyricsAiStatusText.Gravity = GravityFlags.Center;
        musicLyricsAiStatusText.SetPadding(Dp(9), Dp(3), Dp(9), Dp(3));
        musicLyricsAiStatusText.Background = OneUiTheme.CreateRoundedDrawable(
            this, Color.Argb(34, 255, 255, 255), 99f, Color.Argb(48, 255, 255, 255), 0.8f);
        OneUiTheme.AttachRecoil(musicLyricsAiStatusText, 0.94f);
        musicLyricsAiStatusText.Click += (_, _) => ShowAutoVocalMixMatchModal();

        singDnaPillHost.AddView(musicLyricsAiStatusText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        singTopRow.AddView(singDnaPillHost, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

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
        musicLyricsSingDock.AddView(musicLyricsVocalSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(6)
        });

        // Tier 2: Horizontal scrollable row of compact plain-label chips (minH 30dp overriding PillButton 40dp)
        Button MakeSingDockChip(string text, Action onClick, bool active = false) {
            Button b = PillButton(text, onClick, active);
            b.TextSize = 10.8f;
            b.SetSingleLine(true);
            b.Ellipsize = TextUtils.TruncateAt.End;
            b.SetMinimumWidth(0);
            b.SetMinimumHeight(Dp(30));
            b.SetMinHeight(Dp(30));
            b.SetPadding(Dp(11), Dp(4), Dp(11), Dp(4));
            return b;
        }

        LinearLayout singChipsRow = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        singChipsRow.SetGravity(GravityFlags.CenterVertical);

        musicLyricsKaraokeBtn = MakeSingDockChip(S["SingDockKaraokeChip"], () =>
            ApplyLyricsSingVocalLevel(store.SpatialKaraokeEnabled ? 100 : 20));
        singChipsRow.AddView(musicLyricsKaraokeBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsLiveMicBtn = MakeSingDockChip(S["SingDockLiveMicChip"], () => {
            bool next = !store.LiveMicEnabled;
            if (next) {
                RequestRecordAudioPermissionIfNeeded();
            }
            store.LiveMicEnabled = next;
            if (proAudioLiveMicSwitch != null && proAudioLiveMicSwitch.Checked != next) {
                proAudioLiveMicSwitch.Checked = next;
            }
            ApplyAndRefreshVocalChain();
            RefreshLyricsSingDockUi();
        });
        singChipsRow.AddView(musicLyricsLiveMicBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsMixMatchBtn = MakeSingDockChip(S["SingDockMixMatchChip"], ShowAutoVocalMixMatchModal);
        singChipsRow.AddView(musicLyricsMixMatchBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsVocalAbBtn = MakeSingDockChip(S["SingDockVocalAbWetChip"], () => {
            if (playerEngine == null) {
                return;
            }
            playerEngine.VocalChainBypassed = !playerEngine.VocalChainBypassed;
            ApplyAndRefreshVocalChain();
        });
        musicLyricsVocalAbBtn.Visibility = store.LiveMicEnabled ? ViewStates.Visible : ViewStates.Gone;
        singChipsRow.AddView(musicLyricsVocalAbBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsScarlettBtn = MakeSingDockChip(S["SingScarlettQuickBtn"], ShowScarlett2i2ControlsModal);
        singChipsRow.AddView(musicLyricsScarlettBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });

        musicLyricsAiPrepBtn = MakeSingDockChip(S["NeuralPrepBtn"], () =>
            PrepareActiveTrackNeuralStemsAsync(Cavernize.Logic.Spatial.NeuralPipelineGoal.SpatialKaraoke));
        singChipsRow.AddView(musicLyricsAiPrepBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        HorizontalScrollView singChipsScroll = new(this) {
            HorizontalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.Never
        };
        singChipsScroll.AddView(singChipsRow, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent));
        musicLyricsSingDock.AddView(singChipsScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

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

        // 6. Flanking Spatial Toggles: [ Cavernize 3D ]  [ HRTF 3D ]  [ Matrix 7.1 ]  [ AI 3D ]
        LinearLayout spatialToggleRow = HRow();
        musicSpatialToggleRow = spatialToggleRow;
        spatialToggleRow.SetGravity(GravityFlags.CenterVertical);

        (musicCavernizeToggleBtn, musicCavernizeIcon, musicCavernizeText) = MakeCompactSpatialPill(
            OneUiIconType.HeightUp, "Cavernize 3D", sfSemi, () => {
                bool next = !store.CavernizeUpmix;
                store.CavernizeUpmix = next;
                SyncUpmixModeUi();
                RefreshSettingsCategorySubtitles();
                playerEngine?.ApplyUpmixingSettings(upmixingSettings);
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
                SyncUpmixModeUi();
                RefreshSettingsCategorySubtitles();
                playerEngine?.ApplyUpmixingSettings(upmixingSettings);
                RefreshMusicPlayerControlsUi();
            });

        (musicAiToggleBtn, musicAiToggleIcon, musicAiToggleText) = MakeCompactSpatialPill(
            OneUiIconType.Sparkles, S["PlayTabAiOffShort"], sfSemi, TogglePlayTabAiProcessing);
        musicAiToggleBtn.LongClick += (_, e) => {
            ShowPlayTabAiModeDialog();
            e.Handled = true;
        };

        spatialToggleRow.AddView(musicCavernizeToggleBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(5) });
        spatialToggleRow.AddView(musicBottomSpatialBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(5) });
        spatialToggleRow.AddView(musicMatrixToggleBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f) { RightMargin = Dp(5) });
        spatialToggleRow.AddView(musicAiToggleBtn, new LinearLayout.LayoutParams(0, Dp(32), 1f));
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

        musicNowPlayingScrollView = new ScrollView(this) {
            FillViewport = true,
            VerticalScrollBarEnabled = false,
            OverScrollMode = OverScrollMode.IfContentScrolls
        };
        musicNowPlayingScrollView.SetClipToPadding(false);
        musicNowPlayingScrollView.AddView(musicNowPlayingSection, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        page.AddView(musicNowPlayingScrollView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

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

    void UpdatePlayTabSpacingForViewport(bool force = false) {
        if (musicTabRootPage == null || musicNowPlayingSection == null || mediaStageFrame == null) {
            return;
        }
        int vpH = musicTabRootPage.Height;
        if (vpH <= 0) {
            return;
        }
        int padV = musicTabRootPage.PaddingTop + musicTabRootPage.PaddingBottom;
        bool isLandscape = Resources?.Configuration?.Orientation == Android.Content.Res.Orientation.Landscape;
        if (!force &&
            Math.Abs(vpH - lastMusicViewportHeightPx) < Dp(8) &&
            padV == lastMusicViewportPadPx &&
            isLandscape == lastMusicViewportLandscape &&
            lastMusicViewportStageMode == musicStageMode) {
            return;
        }
        lastMusicViewportHeightPx = vpH;
        lastMusicViewportPadPx = padV;
        lastMusicViewportLandscape = isLandscape;
        lastMusicViewportStageMode = musicStageMode;

        float density = Math.Max(1f, Resources?.DisplayMetrics?.Density ?? 3f);
        int usableHPx = Math.Max(0, vpH - padV);
        int availHDp = (int)(usableHPx / density);
        bool isLyricsOrVideo = musicStageMode == PlayerStageMode.Lyrics || musicStageMode == PlayerStageMode.Video;
        bool isCompact = availHDp < 600 || isLandscape;

        // Adaptive breathing room scaled cleanly to usable viewport height while allowing inner scroll when crushed
        int gapDp = isCompact ? 3 : (availHDp < 690 ? 6 : (availHDp < 780 ? 8 : 11));
        int stageGapDp = isCompact ? 4 : (availHDp < 690 ? 7 : 10);

        if (musicArtworkView != null) {
            int targetArtDp = isCompact ? Math.Clamp(availHDp - 310, 150, 220) : 340;
            if (musicArtworkView.PreferredSizeDp != targetArtDp) {
                musicArtworkView.PreferredSizeDp = targetArtDp;
                musicArtworkView.RequestLayout();
            }
        }

        void SetBottomMarginDp(View v, int dpVal) {
            if (v?.LayoutParameters is LinearLayout.LayoutParams lp && lp.BottomMargin != Dp(dpVal)) {
                lp.BottomMargin = Dp(dpVal);
                v.LayoutParameters = lp;
            }
        }

        SetBottomMarginDp(mediaStageFrame, stageGapDp);
        SetBottomMarginDp(musicTitleRow, Math.Max(3, gapDp - 1));
        SetBottomMarginDp(badgeFormatRow, gapDp);
        SetBottomMarginDp(musicTransportRow, gapDp);
        SetBottomMarginDp(musicVolRow, Math.Max(3, gapDp - 1));
        SetBottomMarginDp(musicSpatialToggleRow, Math.Max(3, gapDp - 1));

        // Guard stage height against compact/landscape vertical crush so Lyrics/Video/Cover never collapse <= 40dp
        int minStageDp = isLyricsOrVideo ? (isLandscape ? 230 : 250) : 170;
        int minStagePx = Dp(minStageDp);
        int scrollH = musicNowPlayingScrollView?.Height ?? 0;
        int otherControlsPx = 0;
        if (scrollH > 0) {
            for (int i = 0; i < musicNowPlayingSection.ChildCount; i++) {
                View child = musicNowPlayingSection.GetChildAt(i);
                if (child == null || child == mediaStageFrame || child.Visibility == ViewStates.Gone) {
                    continue;
                }
                int childH = child.MeasuredHeight > 0 ? child.MeasuredHeight : child.Height;
                if (child.LayoutParameters is ViewGroup.MarginLayoutParams mlp) {
                    childH += mlp.TopMargin + mlp.BottomMargin;
                }
                otherControlsPx += childH;
            }
        }
        bool needsScrollFloor = scrollH > 0 && otherControlsPx > 0
            ? (scrollH - otherControlsPx - Dp(stageGapDp)) < minStagePx
            : (availHDp < 600 || (isLandscape && isLyricsOrVideo));

        if (mediaStageFrame.LayoutParameters is LinearLayout.LayoutParams stageLp) {
            int targetH = needsScrollFloor ? minStagePx : 0;
            float targetW = needsScrollFloor ? 0f : 1f;
            if (stageLp.Height != targetH || Math.Abs(stageLp.Weight - targetW) > 0.01f) {
                stageLp.Height = targetH;
                stageLp.Weight = targetW;
                mediaStageFrame.LayoutParameters = stageLp;
            }
        }
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
        LinearLayout btn = new(this) { Orientation = Orientation.Horizontal, BaselineAligned = false };
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

}
