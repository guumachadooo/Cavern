using Android.Content;
using Android.Content.PM;
using Android.Graphics;
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

        public int PreferredSizeDp { get; set; } = 296;
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
            float targetScale = playing ? 1.0f : 0.86f;
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

            if (bitmap != null && !bitmap.IsRecycled && bitmap.Width > 0 && bitmap.Height > 0) {
                float bw = bitmap.Width;
                float bh = bitmap.Height;
                float scale = Math.Max(w / bw, h / bh);
                float dx = (w - bw * scale) * 0.5f;
                float dy = (h - bh * scale) * 0.5f;
                drawMatrix.Reset();
                drawMatrix.PostScale(scale, scale);
                drawMatrix.PostTranslate(dx, dy);
                canvas.DrawBitmap(bitmap, drawMatrix, bmpPaint);
            } else {
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

    PlayerStageMode musicStageMode = PlayerStageMode.Cover;
    string lastStageBoundPath;

    FrameLayout mediaStageFrame;
    FrameLayout musicArtworkSlot;
    RoundedArtworkView musicArtworkView;

    FrameLayout musicVideoSlot;
    SyncedVideoStageView musicVideoStage;
    Android.App.Dialog fullscreenVideoDlg;

    // Borderless Apple Music Lyrics Sheet (fragment_player_lyrics_sheet.xml + lyrics_header.xml)
    LinearLayout musicLyricsContainer;
    RoundedArtworkView musicLyricsMiniArt;
    TextView musicLyricsHeaderTitle, musicLyricsHeaderSub;
    AppleMusicLyricsView musicLyricsView;

    // Apple Music Title + Overflow Row (music_player.xml)
    LinearLayout musicTitleRow;
    TextView musicTitleText, musicArtistText;
    LinearLayout musicTrackBtn;
    TextView musicTrackBtnText;
    FrameLayout musicMoreBtn;

    // Apple Music Lower Media Controls (constraint_player_media_controls.xml)
    OneUiWaveMediaSeekBar musicWaveSeekBar;
    LinearLayout musicSpatialBadgePill;
    OneUiIconView musicSpatialBadgeIcon;
    TextView musicSpatialBadgeText;

    FrameLayout musicRewBtn, musicPlayCircleBtn, musicFwdBtn;
    OneUiIconView musicPlayIcon;
    OneUiSeekBar musicVolSeekBar;

    // Apple Music 3-Icon Bottom Bar (id/player_lyrics, id/shareplay_badge, id/player_queue)
    FrameLayout musicBottomLeftBtn;
    OneUiIconView musicBottomLeftIcon;
    LinearLayout musicBottomSpatialBtn;
    OneUiIconView musicBottomSpatialIcon;
    TextView musicBottomSpatialText;
    FrameLayout musicBottomLoopBtn;
    OneUiIconView musicBottomLoopIcon;

    LinearLayout BuildMusicPlayerTab() {
        LinearLayout page = new(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(22), Dp(6), Dp(22), Dp(116));

        // 1. Unified Stage Container: Apple Music Artwork / Samsung Video Stage / Borderless Live Lyrics
        mediaStageFrame = new FrameLayout(this);

        musicArtworkSlot = new FrameLayout(this);
        musicArtworkView = new RoundedArtworkView(this) {
            PreferredSizeDp = 296,
            CornerDp = 16f,
            ScaleX = 0.86f,
            ScaleY = 0.86f
        };
        musicArtworkView.Click += (_, _) => {
            if (LoadedFile == null) {
                LaunchFilePicker(ReqOpenMedia, allowMultiple: false);
            } else {
                TogglePlayPause();
            }
        };
        musicArtworkSlot.AddView(musicArtworkView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Center,
            TopMargin = Dp(8),
            BottomMargin = Dp(8)
        });
        mediaStageFrame.AddView(musicArtworkSlot, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        // Video Stage (Samsung Video Player + YouTube HUD + Subtitles + Fullscreen)
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
            Dp(246)) {
            TopMargin = Dp(10),
            BottomMargin = Dp(8)
        });

        // Borderless Apple Music Live Lyrics Sheet (fragment_player_lyrics_sheet.xml)
        musicLyricsContainer = new LinearLayout(this) {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };

        LinearLayout lyricsHeader = new(this) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        lyricsHeader.SetGravity(GravityFlags.CenterVertical);
        lyricsHeader.SetPadding(0, Dp(2), 0, Dp(10));

        musicLyricsMiniArt = new RoundedArtworkView(this) {
            PreferredSizeDp = 52,
            CornerDp = 10f
        };
        musicLyricsMiniArt.Click += (_, _) => SetPlayerStageMode(PlayerStageMode.Cover);
        lyricsHeader.AddView(musicLyricsMiniArt, new LinearLayout.LayoutParams(Dp(52), Dp(52)) { RightMargin = Dp(12) });

        LinearLayout lyricsMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicLyricsHeaderTitle = Label(S["PlayerEmptyTitle"], 16.5f, ColText);
        musicLyricsHeaderTitle.SetTypeface(null, TypefaceStyle.Bold);
        musicLyricsHeaderTitle.SetSingleLine(true);
        musicLyricsHeaderTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        musicLyricsHeaderSub = Label(S["LyricsTapHint"], 13.5f, Color.Argb(175, 252, 252, 255));
        musicLyricsHeaderSub.SetSingleLine(true);
        musicLyricsHeaderSub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        lyricsMetaCol.AddView(musicLyricsHeaderTitle);
        lyricsMetaCol.AddView(musicLyricsHeaderSub);
        lyricsHeader.AddView(lyricsMetaCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(8) });

        FrameLayout lyricsMoreBtn = MakeAppleCircleOverflowBtn(ShowPlayerMoreSheet);
        lyricsHeader.AddView(lyricsMoreBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)));
        musicLyricsContainer.AddView(lyricsHeader);

        musicLyricsView = new AppleMusicLyricsView(this);
        musicLyricsView.LineSeekRequested += sec => SeekAbsoluteSeconds(sec);
        musicLyricsContainer.AddView(musicLyricsView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        mediaStageFrame.AddView(musicLyricsContainer, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(408)));

        page.AddView(mediaStageFrame, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(14)
        });

        // 2. Apple Music Title + Artist + Overflow Row (music_player.xml)
        musicTitleRow = HRow();
        musicTitleRow.SetGravity(GravityFlags.CenterVertical);

        LinearLayout titleMetaCol = new(this) { Orientation = Orientation.Vertical };
        musicTitleText = new TextView(this) {
            Text = S["PlayerEmptyTitle"],
            TextSize = 20.5f
        };
        musicTitleText.SetTextColor(ColText);
        musicTitleText.SetTypeface(null, TypefaceStyle.Bold);
        musicTitleText.SetSingleLine(true);
        musicTitleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleMetaCol.AddView(musicTitleText);

        musicArtistText = new TextView(this) {
            Text = S["PlayerEmptySub"],
            TextSize = 15.5f
        };
        musicArtistText.SetTextColor(Color.Argb(178, 252, 252, 255));
        musicArtistText.SetSingleLine(true);
        musicArtistText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
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
        musicTrackBtnText.SetTypeface(null, TypefaceStyle.Bold);
        musicTrackBtnText.SetSingleLine(true);
        musicTrackBtn.AddView(musicTrackBtnText);
        musicTrackBtn.Click += (_, _) => ShowTrackSelectorDialog();
        musicTitleRow.AddView(musicTrackBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(8) });

        musicMoreBtn = MakeAppleCircleOverflowBtn(ShowPlayerMoreSheet);
        musicTitleRow.AddView(musicMoreBtn, new LinearLayout.LayoutParams(Dp(32), Dp(32)));

        page.AddView(musicTitleRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(16)
        });

        // 3. One UI 9 Wave Media Seekbar (preserved!) + Apple Music centered Audio Badge
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
        page.AddView(musicWaveSeekBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Subtle Apple Music product_header_audio_badges pill centered cleanly under seekbar
        musicSpatialBadgePill = HRow();
        musicSpatialBadgePill.SetGravity(GravityFlags.Center);
        musicSpatialBadgePill.SetPadding(Dp(10), Dp(3), Dp(10), Dp(3));
        musicSpatialBadgePill.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(34, 255, 255, 255), 8f);
        OneUiTheme.AttachRecoil(musicSpatialBadgePill, 0.94f);
        musicSpatialBadgeIcon = new OneUiIconView(this, OneUiIconType.Sparkles, Color.Argb(220, 252, 252, 255));
        musicSpatialBadgeText = Label(S["PlayerBadgeSpatial"], 11f, Color.Argb(228, 252, 252, 255));
        musicSpatialBadgeText.SetIncludeFontPadding(false);
        musicSpatialBadgeText.SetSingleLine(true);
        musicSpatialBadgeText.SetTypeface(null, TypefaceStyle.Bold);
        musicSpatialBadgePill.AddView(musicSpatialBadgeIcon, new LinearLayout.LayoutParams(Dp(11), Dp(11)) { RightMargin = Dp(5) });
        musicSpatialBadgePill.AddView(musicSpatialBadgeText);
        musicSpatialBadgePill.Click += (_, _) => ShowPlayerMoreSheet();
        page.AddView(musicSpatialBadgePill, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(2),
            BottomMargin = Dp(22)
        });

        // 4. Apple Music Borderless Transport Row (<< | Play/Pause | >>)
        LinearLayout transportRow = HRow();
        transportRow.SetGravity(GravityFlags.Center);

        musicRewBtn = MakeBorderlessAppleIconBtn(OneUiIconType.AppleRewind, 32, () => SeekRelative(-10), out _);
        musicPlayCircleBtn = MakeBorderlessAppleIconBtn(OneUiIconType.Play, 44, TogglePlayPause, out musicPlayIcon);
        musicFwdBtn = MakeBorderlessAppleIconBtn(OneUiIconType.AppleForward, 32, () => SeekRelative(10), out _);

        transportRow.AddView(musicRewBtn, new LinearLayout.LayoutParams(Dp(64), Dp(58)) { RightMargin = Dp(28) });
        transportRow.AddView(musicPlayCircleBtn, new LinearLayout.LayoutParams(Dp(72), Dp(68)) { RightMargin = Dp(28) });
        transportRow.AddView(musicFwdBtn, new LinearLayout.LayoutParams(Dp(64), Dp(58)));
        page.AddView(transportRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(22)
        });

        // 5. Apple Music Volume Row (SpeakerLow + slim Samsung relative-drag slider + SpeakerHigh)
        LinearLayout volRow = HRow();
        volRow.SetGravity(GravityFlags.CenterVertical);

        OneUiIconView lowSpk = new(this, OneUiIconType.SpeakerLow, Color.Argb(165, 252, 252, 255));
        lowSpk.Click += (_, _) => SetMasterVolumePercent(Math.Max(0, (int)Math.Round(store.PlayerVolume * 100) - 15));
        volRow.AddView(lowSpk, new LinearLayout.LayoutParams(Dp(18), Dp(18)) { RightMargin = Dp(12) });

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
        volRow.AddView(highSpk, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { LeftMargin = Dp(12) });

        page.AddView(volRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            BottomMargin = Dp(18)
        });

        // 6. Apple Music 3-Icon Bottom Action Row ([ Lyrics / CC ] | [ HRTF 3D / Spatial ] | [ Loop ])
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

        LinearLayout leftWrap = HRow();
        leftWrap.SetGravity(GravityFlags.Center);
        leftWrap.AddView(musicBottomLeftBtn, new LinearLayout.LayoutParams(Dp(46), Dp(34)));

        musicBottomSpatialBtn = HRow();
        musicBottomSpatialBtn.SetGravity(GravityFlags.Center);
        musicBottomSpatialBtn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        OneUiTheme.AttachRecoil(musicBottomSpatialBtn, 0.92f);
        musicBottomSpatialIcon = new OneUiIconView(this, OneUiIconType.Headphones, ColText);
        musicBottomSpatialText = Label(S["BentoHrtf"], 11.5f, ColText);
        musicBottomSpatialText.SetIncludeFontPadding(false);
        musicBottomSpatialText.SetTypeface(null, TypefaceStyle.Bold);
        musicBottomSpatialText.SetSingleLine(true);
        musicBottomSpatialBtn.AddView(musicBottomSpatialIcon, new LinearLayout.LayoutParams(Dp(15), Dp(15)) { RightMargin = Dp(6) });
        musicBottomSpatialBtn.AddView(musicBottomSpatialText);
        musicBottomSpatialBtn.Click += (_, _) => {
            store.HeadphoneVirtualizer = !store.HeadphoneVirtualizer;
            playerEngine.HeadphoneVirtualizer = store.HeadphoneVirtualizer;
            refreshBentoTilesUi?.Invoke();
            RefreshMusicPlayerControlsUi();
        };

        LinearLayout centerWrap = HRow();
        centerWrap.SetGravity(GravityFlags.Center);
        centerWrap.AddView(musicBottomSpatialBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)));

        musicBottomLoopBtn = new FrameLayout(this);
        OneUiTheme.AttachRecoil(musicBottomLoopBtn, 0.88f);
        musicBottomLoopIcon = new OneUiIconView(this, OneUiIconType.RepeatLoop, Color.Argb(195, 252, 252, 255));
        musicBottomLoopBtn.AddView(musicBottomLoopIcon, new FrameLayout.LayoutParams(Dp(22), Dp(22)) { Gravity = GravityFlags.Center });
        musicBottomLoopBtn.Click += (_, _) => ToggleLoopMode();

        LinearLayout rightWrap = HRow();
        rightWrap.SetGravity(GravityFlags.Center);
        rightWrap.AddView(musicBottomLoopBtn, new LinearLayout.LayoutParams(Dp(46), Dp(34)));

        bottomActionRow.AddView(leftWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        bottomActionRow.AddView(centerWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.3f));
        bottomActionRow.AddView(rightWrap, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(bottomActionRow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        RefreshMusicPlayerUi();
        return page;
    }

    FrameLayout MakeAppleCircleOverflowBtn(Action onClick) {
        FrameLayout btn = new(this);
        btn.Background = OneUiTheme.CreateRoundedDrawable(this, Color.Argb(44, 255, 255, 255), 99f);
        OneUiTheme.AttachRecoil(btn, 0.88f);
        OneUiIconView ico = new(this, OneUiIconType.MoreHorizontal, ColText);
        btn.AddView(ico, new FrameLayout.LayoutParams(Dp(18), Dp(18)) { Gravity = GravityFlags.Center });
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
            if (mode == PlayerStageMode.Video && activeTab == 0) {
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

        bool hrtfOn = store.HeadphoneVirtualizer;
        musicBottomSpatialBtn.Background = hrtfOn
            ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(52, 255, 255, 255), 99f)
            : null;
        musicBottomSpatialIcon.IconColor = hrtfOn ? OneUiTheme.TextPrimary : Color.Argb(145, 252, 252, 255);
        musicBottomSpatialText.SetTextColor(hrtfOn ? OneUiTheme.TextPrimary : Color.Argb(145, 252, 252, 255));
        musicBottomSpatialText.Text = hrtfOn ? S["BentoHrtf"] : "Direto";

        bool loop = playerEngine?.Loop ?? false;
        musicBottomLoopBtn.Background = loop
            ? OneUiTheme.CreateRoundedDrawable(this, Color.Argb(52, 255, 255, 255), 14f)
            : null;
        musicBottomLoopIcon.IconColor = loop ? OneUiTheme.TextPrimary : Color.Argb(180, 252, 252, 255);
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

        Bitmap activeArt = (LoadedFile != null && currentMediaMeta?.ArtworkBitmap != null && !currentMediaMeta.ArtworkBitmap.IsRecycled)
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

            int beds = trackSourceItems.Count(x => x.IsBed);
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
        if (musicStageMode == PlayerStageMode.Video && (activeTab == 0 || fullscreenVideoDlg != null)) {
            musicVideoStage?.SyncPlaybackState(isPlaying, curSec, totSec);
            musicVideoStage?.ConfigureMetadata(
                musicTitleText.Text,
                musicSpatialBadgeText.Text,
                musicTrackBtnText?.Text ?? "",
                LoadedFile?.Tracks.Count > 1,
                activeSubtitleCues.Count > 0,
                playerEngine?.Loop ?? false,
                S["VideoUnlockHint"]);
        } else if (musicStageMode == PlayerStageMode.Lyrics && activeTab == 0) {
            musicLyricsView?.UpdatePlayback(curSec, isPlaying);
        }
        if (musicPlayIcon != null) {
            musicPlayIcon.IconType = isPlaying ? OneUiIconType.Pause : OneUiIconType.Play;
        }

        RefreshBottomActionBarVisuals();
    }

    void ShowPlayerMoreSheet() {
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        Android.App.Dialog dlg = null;

        // Technical Codec & Telemetry summary pill
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

        Button importBtn = PillButton(S["SubtitlesImportBtn"], () => {
            dlg?.Dismiss();
            LaunchFilePicker(ReqLoadSubtitles, allowMultiple: false);
        });
        list.AddView(importBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            TopMargin = Dp(4)
        });

        dlg = OneUiModalDialog.Show(
            this,
            S["VideoSubtitlesTitle"],
            null,
            icon: OneUiIconType.SubtitlesCc,
            iconBadgeColor: OneUiTheme.Primary,
            positiveText: S["DismissBtn"],
            customContent: list);
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
        RequestedOrientation = ScreenOrientation.Unspecified;
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
