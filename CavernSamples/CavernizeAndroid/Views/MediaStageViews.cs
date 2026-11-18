using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;

using AndroidMediaPlayer = Android.Media.MediaPlayer;

namespace CavernizeAndroid.Views;

/// <summary>
/// Outlines a view with rounded corners for clean hardware clipping (API 21+).
/// </summary>
public sealed class RoundedClipOutlineProvider(float radiusPx) : ViewOutlineProvider {
    public float RadiusPx { get; set; } = radiusPx;

    public override void GetOutline(View view, Outline outline) {
        if (view == null || outline == null) return;
        outline.SetRoundRect(0, 0, view.Width, view.Height, RadiusPx);
    }
}

public enum VideoAspectMode {
    Fit,
    Crop,
    Stretch
}

/// <summary>
/// Full Samsung Video Player + YouTube style video stage synchronized to Cavernize's real-time spatial audio clock:
/// - Top HUD bar: Video title, CC/Subtitles selector, Audio Track selector, Aspect Ratio cycle, DSP options
/// - Center HUD: Rewind -10s, Play/Pause, Forward +10s + YouTube double-tap ±10s radial bloom
/// - Live subtitle overlay (.srt/.vtt/.ass external or MKV/MP4 embedded extracted via FFmpeg)
/// - Bottom HUD bar: Fullscreen timeline scrubber (in fullscreen), Touch Lock toggle, Loop, Spatial 3D pill, Fullscreen toggle
/// </summary>
public sealed class SyncedVideoStageView : FrameLayout, TextureView.ISurfaceTextureListener {
    sealed class DoubleTapBloomView : View {
        readonly Paint bloomPaint = new(PaintFlags.AntiAlias);
        readonly Paint arrowPaint = new(PaintFlags.AntiAlias);
        readonly Paint textPaint = new(PaintFlags.AntiAlias);
        bool isForward;
        string label = "";
        long animStartMs;
        bool active;
        const float DurationMs = 560f;

        public DoubleTapBloomView(Context ctx) : base(ctx) {
            float dp = ctx.Resources.DisplayMetrics.Density;
            bloomPaint.SetStyle(Paint.Style.Fill);
            arrowPaint.SetStyle(Paint.Style.Stroke);
            arrowPaint.StrokeWidth = 2.4f * dp;
            arrowPaint.StrokeCap = Paint.Cap.Round;
            arrowPaint.StrokeJoin = Paint.Join.Round;
            textPaint.TextAlign = Paint.Align.Center;
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
            textPaint.TextSize = 13f * dp;
        }

        public void Trigger(bool forward, string text) {
            isForward = forward;
            label = text ?? "";
            animStartMs = SystemClock.UptimeMillis();
            active = true;
            Visibility = ViewStates.Visible;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas) {
            if (!active) return;
            float elapsed = SystemClock.UptimeMillis() - animStartMs;
            float t = Math.Clamp(elapsed / DurationMs, 0f, 1f);
            float dp = Resources.DisplayMetrics.Density;
            float w = Width;
            float h = Height;
            if (w <= 0 || h <= 0) return;

            float alphaCurve = t < 0.2f ? (t / 0.2f) : Math.Clamp(1f - (t - 0.2f) / 0.8f, 0f, 1f);
            float cx = isForward ? w * 0.78f : w * 0.22f;
            float cy = h * 0.5f;
            float maxR = Math.Max(w, h) * 0.65f;
            float curR = maxR * (0.28f + 0.72f * (1f - MathF.Pow(1f - t, 2.2f)));

            bloomPaint.Color = Color.Argb((int)(62 * alphaCurve), 255, 255, 255);
            canvas.DrawCircle(cx, cy, curR, bloomPaint);

            int dir = isForward ? 1 : -1;
            float chevxCenter = isForward ? w * 0.75f : w * 0.25f;
            float sz = 7f * dp;
            for (int k = 0; k < 3; k++) {
                float phase = Math.Clamp(t * 2.2f - k * 0.22f, 0f, 1f);
                int cAlpha = (int)(240 * alphaCurve * (phase > 0f ? 1f : 0.25f));
                arrowPaint.Color = Color.Argb(cAlpha, 255, 255, 255);
                float ox = chevxCenter + dir * (k - 1) * 10f * dp;
                using Android.Graphics.Path p = new();
                p.MoveTo(ox - dir * sz * 0.6f, cy - 12f * dp - sz);
                p.LineTo(ox + dir * sz * 0.5f, cy - 12f * dp);
                p.LineTo(ox - dir * sz * 0.6f, cy - 12f * dp + sz);
                canvas.DrawPath(p, arrowPaint);
            }

            textPaint.Color = Color.Argb((int)(245 * alphaCurve), 255, 255, 255);
            canvas.DrawText(label, chevxCenter, cy + 16f * dp, textPaint);

            if (t < 1f) {
                PostInvalidateOnAnimation();
            } else {
                active = false;
                Visibility = ViewStates.Gone;
            }
        }
    }

    sealed class StageGestureListener(SyncedVideoStageView owner) : GestureDetector.SimpleOnGestureListener {
        public override bool OnDown(MotionEvent e) => true;

        public override bool OnSingleTapConfirmed(MotionEvent e) {
            owner.HandleStageSingleTap();
            return true;
        }

        public override bool OnDoubleTap(MotionEvent e) {
            if (owner.IsControlsLocked) {
                owner.FlashUnlockHint();
                return true;
            }
            float x = e.GetX();
            float w = Math.Max(1f, owner.Width);
            if (x < w * 0.36f) {
                owner.HandleDoubleTapSeek(-10);
            } else if (x > w * 0.64f) {
                owner.HandleDoubleTapSeek(10);
            } else {
                owner.PlayPauseRequested?.Invoke();
            }
            return true;
        }
    }

    readonly RoundedClipOutlineProvider outlineProvider;
    readonly TextureView textureView;
    readonly TextView subtitleOverlayView;
    readonly DoubleTapBloomView bloomOverlay;
    readonly FrameLayout hudOverlay;
    readonly LinearLayout topBar;
    readonly TextView hudTitleText;
    readonly FrameLayout ccBtn;
    readonly OneUiIconView ccIcon;
    readonly LinearLayout trackPillBtn;
    readonly TextView trackPillLabel;
    readonly FrameLayout aspectBtn;
    readonly FrameLayout hudMoreBtn;

    readonly LinearLayout centerRow;
    readonly FrameLayout centerRewBtn;
    readonly FrameLayout centerPlayBtn;
    readonly OneUiIconView centerPlayIcon;
    readonly FrameLayout centerFwdBtn;

    readonly LinearLayout bottomBar;
    readonly LinearLayout fullscreenSeekRow;
    readonly OneUiSeekBar fullscreenSeekBar;
    readonly TextView fsCurTimeText;
    readonly TextView fsTotTimeText;

    readonly FrameLayout lockBtn;
    readonly FrameLayout loopBtn;
    readonly OneUiIconView loopIcon;
    readonly LinearLayout spatialPillBtn;
    readonly TextView spatialPillText;
    readonly TextView inlineTimeText;
    readonly FrameLayout fullscreenBtn;
    readonly OneUiIconView fullscreenIcon;

    readonly LinearLayout unlockFloatingPill;
    readonly TextView unlockPillLabel;
    readonly TextView hudAspectToast;
    readonly TextView hudErrorText;

    readonly GestureDetector gestureDetector;
    readonly Handler hideHandler = new(Looper.MainLooper!);

    AndroidMediaPlayer mediaPlayer;
    Surface activeSurface;
    string currentVideoPath;
    int videoWidth;
    int videoHeight;
    bool isPrepared;
    bool desiredPlaying;
    double lastAudioSeconds;
    double lastTotalSeconds;
    long pendingSeekMs = -1;
    long lastDriftCorrectionMs;
    bool hudVisible = true;
    bool isFullscreen;
    bool isSubtitlesActive;
    bool isUserSeekingFs;
    VideoAspectMode aspectMode = VideoAspectMode.Fit;
    IReadOnlyList<SubtitleCue> subtitleCues = [];
    string lastSubRendered;

    Java.Lang.Runnable pendingAutoHide;
    Java.Lang.Runnable pendingUnlockHide;
    Java.Lang.Runnable pendingToastHide;

    public bool IsControlsLocked { get; private set; }
    public bool IsFullscreen => isFullscreen;

    public event Action PlayPauseRequested;
    public event Action<double> SeekDeltaRequested;
    public event Action<double> SeekAbsoluteRequested;
    public event Action SubtitlesRequested;
    public event Action AudioTrackRequested;
    public event Action LoopToggleRequested;
    public event Action SpatialOptionsRequested;
    public event Action MoreOptionsRequested;
    public event Action FullscreenToggleRequested;

    public SyncedVideoStageView(Context ctx, string titlePlaceholder) : base(ctx) {
        SetBackgroundColor(Color.Rgb(6, 7, 11));
        outlineProvider = new RoundedClipOutlineProvider(OneUiTheme.Dp(ctx, 20f));
        OutlineProvider = outlineProvider;
        ClipToOutline = true;
        Elevation = OneUiTheme.Dp(ctx, 12f);

        textureView = new TextureView(ctx) {
            SurfaceTextureListener = this
        };
        AddView(textureView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Live Subtitle Overlay anchored at bottom-center (like Samsung Video Player / YouTube CC)
        subtitleOverlayView = new TextView(ctx) {
            TextSize = 14f,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone
        };
        subtitleOverlayView.SetTextColor(Color.White);
        subtitleOverlayView.SetTypeface(null, TypefaceStyle.Bold);
        subtitleOverlayView.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        subtitleOverlayView.SetShadowLayer(Dp(4), 0, Dp(1), Color.Black);
        subtitleOverlayView.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(182, 8, 9, 13), 10f);
        AddView(subtitleOverlayView, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            LeftMargin = Dp(18),
            RightMargin = Dp(18),
            BottomMargin = Dp(68)
        });

        bloomOverlay = new DoubleTapBloomView(ctx) { Visibility = ViewStates.Gone };
        AddView(bloomOverlay, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Samsung Video Player / YouTube HUD Overlay
        hudOverlay = new FrameLayout(ctx);

        // 1. Top Gradient Bar (Title, CC, Audio Track, Aspect Ratio, More/DSP)
        topBar = new LinearLayout(ctx) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        topBar.SetGravity(GravityFlags.CenterVertical);
        topBar.SetPadding(Dp(12), Dp(10), Dp(10), Dp(14));
        Android.Graphics.Drawables.GradientDrawable topGrad = new(
            Android.Graphics.Drawables.GradientDrawable.Orientation.TopBottom,
            [Color.Argb(210, 6, 7, 11).ToArgb(), Color.Transparent.ToArgb()]);
        topBar.Background = topGrad;

        hudTitleText = new TextView(ctx) {
            Text = titlePlaceholder ?? "Video",
            TextSize = 13.5f
        };
        hudTitleText.SetTextColor(OneUiTheme.TextPrimary);
        hudTitleText.SetTypeface(null, TypefaceStyle.Bold);
        hudTitleText.SetSingleLine(true);
        hudTitleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        topBar.AddView(hudTitleText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { RightMargin = Dp(6) });

        trackPillBtn = new LinearLayout(ctx) {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        trackPillBtn.SetGravity(GravityFlags.Center);
        trackPillBtn.SetPadding(Dp(9), Dp(5), Dp(9), Dp(5));
        trackPillBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(150, 26, 28, 38), 99f, Color.Argb(70, 255, 255, 255), 1f);
        OneUiTheme.AttachRecoil(trackPillBtn, 0.92f);
        OneUiIconView trkIco = new(ctx, OneUiIconType.Speaker, OneUiTheme.TextPrimary);
        trackPillLabel = new TextView(ctx) { Text = "Faixa", TextSize = 11f };
        trackPillLabel.SetTextColor(OneUiTheme.TextPrimary);
        trackPillLabel.SetTypeface(null, TypefaceStyle.Bold);
        trackPillLabel.SetIncludeFontPadding(false);
        trackPillLabel.SetSingleLine(true);
        trackPillBtn.AddView(trkIco, new LinearLayout.LayoutParams(Dp(13), Dp(13)) { RightMargin = Dp(4) });
        trackPillBtn.AddView(trackPillLabel);
        trackPillBtn.Click += (_, _) => {
            AudioTrackRequested?.Invoke();
            ScheduleHudAutoHide();
        };
        topBar.AddView(trackPillBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)) { RightMargin = Dp(6) });

        (ccBtn, ccIcon) = MakeHudIconCircle(ctx, OneUiIconType.SubtitlesCc, 34, 17, () => {
            SubtitlesRequested?.Invoke();
            ScheduleHudAutoHide();
        });
        topBar.AddView(ccBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(4) });

        (aspectBtn, _) = MakeHudIconCircle(ctx, OneUiIconType.AspectRatioFit, 34, 17, () => {
            CycleAspectMode();
            ScheduleHudAutoHide();
        });
        topBar.AddView(aspectBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(4) });

        (hudMoreBtn, _) = MakeHudIconCircle(ctx, OneUiIconType.MoreHorizontal, 34, 17, () => {
            MoreOptionsRequested?.Invoke();
            ScheduleHudAutoHide();
        });
        topBar.AddView(hudMoreBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)));

        hudOverlay.AddView(topBar, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Top
        });

        // 2. Center Samsung Video Player Transport (-10s | Play/Pause | +10s)
        centerRow = new LinearLayout(ctx) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        centerRow.SetGravity(GravityFlags.Center);

        (centerRewBtn, _) = MakeHudTransportCircle(ctx, OneUiIconType.Rewind10, 46, 22, () => {
            HandleDoubleTapSeek(-10);
            ScheduleHudAutoHide();
        });
        (centerPlayBtn, centerPlayIcon) = MakeHudTransportCircle(ctx, OneUiIconType.Play, 62, 28, () => {
            PlayPauseRequested?.Invoke();
            ScheduleHudAutoHide();
        });
        (centerFwdBtn, _) = MakeHudTransportCircle(ctx, OneUiIconType.Forward10, 46, 22, () => {
            HandleDoubleTapSeek(10);
            ScheduleHudAutoHide();
        });

        centerRow.AddView(centerRewBtn, new LinearLayout.LayoutParams(Dp(46), Dp(46)) { RightMargin = Dp(26) });
        centerRow.AddView(centerPlayBtn, new LinearLayout.LayoutParams(Dp(62), Dp(62)) { RightMargin = Dp(26) });
        centerRow.AddView(centerFwdBtn, new LinearLayout.LayoutParams(Dp(46), Dp(46)));

        hudOverlay.AddView(centerRow, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Center
        });

        // 3. Bottom Gradient Bar (Fullscreen Seekbar + Lock, Spatial 3D, Loop, Time, Fullscreen)
        bottomBar = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        bottomBar.SetPadding(Dp(12), Dp(14), Dp(12), Dp(10));
        Android.Graphics.Drawables.GradientDrawable botGrad = new(
            Android.Graphics.Drawables.GradientDrawable.Orientation.BottomTop,
            [Color.Argb(225, 6, 7, 11).ToArgb(), Color.Transparent.ToArgb()]);
        bottomBar.Background = botGrad;

        // Interactive timeline bar shown cleanly inside Fullscreen mode
        fullscreenSeekRow = new LinearLayout(ctx) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false,
            Visibility = ViewStates.Gone
        };
        fullscreenSeekRow.SetGravity(GravityFlags.CenterVertical);
        fullscreenSeekRow.SetPadding(0, 0, 0, Dp(6));

        fsCurTimeText = new TextView(ctx) { Text = "00:00", TextSize = 11.5f };
        fsCurTimeText.SetTextColor(OneUiTheme.TextPrimary);
        fsCurTimeText.SetTypeface(null, TypefaceStyle.Bold);
        fsTotTimeText = new TextView(ctx) { Text = "00:00", TextSize = 11.5f };
        fsTotTimeText.SetTextColor(OneUiTheme.TextSub);

        fullscreenSeekBar = new OneUiSeekBar(ctx) {
            Max = 1000,
            Progress = 0,
            SlimVolumeMode = true,
            HasDefaultReset = false,
            CustomFillColor = OneUiTheme.Primary
        };
        fullscreenSeekBar.UserProgressChanged += prog => {
            isUserSeekingFs = true;
            if (lastTotalSeconds > 0) {
                double target = prog / 1000.0 * lastTotalSeconds;
                fsCurTimeText.Text = $"{TimeSpan.FromSeconds(target):mm\\:ss}";
                SeekAbsoluteRequested?.Invoke(target);
            }
            isUserSeekingFs = false;
            ScheduleHudAutoHide();
        };

        fullscreenSeekRow.AddView(fsCurTimeText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
        fullscreenSeekRow.AddView(fullscreenSeekBar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        fullscreenSeekRow.AddView(fsTotTimeText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
        bottomBar.AddView(fullscreenSeekRow);

        LinearLayout actionRow = new(ctx) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
        actionRow.SetGravity(GravityFlags.CenterVertical);

        (lockBtn, _) = MakeHudIconCircle(ctx, OneUiIconType.Lock, 34, 16, () => SetControlsLocked(true));
        actionRow.AddView(lockBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(6) });

        (loopBtn, loopIcon) = MakeHudIconCircle(ctx, OneUiIconType.RepeatLoop, 34, 16, () => {
            LoopToggleRequested?.Invoke();
            ScheduleHudAutoHide();
        });
        actionRow.AddView(loopBtn, new LinearLayout.LayoutParams(Dp(34), Dp(34)) { RightMargin = Dp(8) });

        spatialPillBtn = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        spatialPillBtn.SetGravity(GravityFlags.Center);
        spatialPillBtn.SetPadding(Dp(10), Dp(5), Dp(10), Dp(5));
        spatialPillBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(150, 26, 28, 38), 99f, Color.Argb(75, 255, 255, 255), 1f);
        OneUiTheme.AttachRecoil(spatialPillBtn, 0.93f);
        OneUiIconView spIco = new(ctx, OneUiIconType.Sparkles, OneUiTheme.PrimaryHigh);
        spatialPillText = new TextView(ctx) { Text = "Spatial 3D", TextSize = 11f };
        spatialPillText.SetTextColor(OneUiTheme.TextPrimary);
        spatialPillText.SetTypeface(null, TypefaceStyle.Bold);
        spatialPillText.SetIncludeFontPadding(false);
        spatialPillText.SetSingleLine(true);
        spatialPillBtn.AddView(spIco, new LinearLayout.LayoutParams(Dp(12), Dp(12)) { RightMargin = Dp(4) });
        spatialPillBtn.AddView(spatialPillText);
        spatialPillBtn.Click += (_, _) => {
            SpatialOptionsRequested?.Invoke();
            ScheduleHudAutoHide();
        };
        actionRow.AddView(spatialPillBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(28)));

        View spacer = new(ctx);
        actionRow.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));

        inlineTimeText = new TextView(ctx) { Text = "00:00 / 00:00", TextSize = 11.5f };
        inlineTimeText.SetTextColor(Color.Argb(225, 240, 242, 250));
        inlineTimeText.SetTypeface(null, TypefaceStyle.Bold);
        inlineTimeText.SetIncludeFontPadding(false);
        inlineTimeText.SetSingleLine(true);
        actionRow.AddView(inlineTimeText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });

        (fullscreenBtn, fullscreenIcon) = MakeHudIconCircle(ctx, OneUiIconType.FullscreenEnter, 36, 18, () => {
            FullscreenToggleRequested?.Invoke();
            ScheduleHudAutoHide();
        });
        actionRow.AddView(fullscreenBtn, new LinearLayout.LayoutParams(Dp(36), Dp(36)));

        bottomBar.AddView(actionRow);
        hudOverlay.AddView(bottomBar, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom
        });

        hudErrorText = new TextView(ctx) {
            TextSize = 12f,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone
        };
        hudErrorText.SetTextColor(OneUiTheme.TextSub);
        hudErrorText.SetPadding(Dp(18), Dp(6), Dp(18), Dp(6));
        hudErrorText.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(185, 18, 20, 26), 12f);
        hudOverlay.AddView(hudErrorText, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            BottomMargin = Dp(64)
        });

        AddView(hudOverlay, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Touch Unlock floating pill (when Lock Controls is enabled)
        unlockFloatingPill = new LinearLayout(ctx) {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        unlockFloatingPill.SetGravity(GravityFlags.Center);
        unlockFloatingPill.SetPadding(Dp(14), Dp(8), Dp(14), Dp(8));
        unlockFloatingPill.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(215, 22, 24, 34), 99f, Color.Argb(110, 255, 255, 255), 1.2f);
        OneUiTheme.AttachRecoil(unlockFloatingPill, 0.92f);
        OneUiIconView unlockIco = new(ctx, OneUiIconType.Lock, OneUiTheme.PrimaryHigh);
        unlockPillLabel = new TextView(ctx) { Text = "Destravar", TextSize = 12f };
        unlockPillLabel.SetTextColor(OneUiTheme.TextPrimary);
        unlockPillLabel.SetTypeface(null, TypefaceStyle.Bold);
        unlockPillLabel.SetIncludeFontPadding(false);
        unlockFloatingPill.AddView(unlockIco, new LinearLayout.LayoutParams(Dp(15), Dp(15)) { RightMargin = Dp(6) });
        unlockFloatingPill.AddView(unlockPillLabel);
        unlockFloatingPill.Click += (_, _) => SetControlsLocked(false);
        AddView(unlockFloatingPill, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            BottomMargin = Dp(18)
        });

        // Aspect Ratio temporary toast pill
        hudAspectToast = new TextView(ctx) {
            TextSize = 12f,
            Visibility = ViewStates.Gone
        };
        hudAspectToast.SetTextColor(OneUiTheme.TextPrimary);
        hudAspectToast.SetTypeface(null, TypefaceStyle.Bold);
        hudAspectToast.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
        hudAspectToast.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(210, 20, 22, 30), 99f);
        AddView(hudAspectToast, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            TopMargin = Dp(52)
        });

        gestureDetector = new GestureDetector(ctx, new StageGestureListener(this));
    }

    int Dp(float dp) => OneUiTheme.Dp(Context, dp);

    (FrameLayout btn, OneUiIconView ico) MakeHudIconCircle(Context ctx, OneUiIconType iconType, int boxDp, int icoDp, Action onClick) {
        FrameLayout btn = new(ctx);
        btn.Background = OneUiTheme.CreateRippleRoundedDrawable(ctx, Color.Argb(115, 20, 22, 30), 99f);
        OneUiTheme.AttachRecoil(btn, 0.90f);
        OneUiIconView ico = new(ctx, iconType, OneUiTheme.TextPrimary);
        btn.AddView(ico, new LayoutParams(Dp(icoDp), Dp(icoDp)) { Gravity = GravityFlags.Center });
        btn.Click += (_, _) => onClick?.Invoke();
        return (btn, ico);
    }

    (FrameLayout btn, OneUiIconView ico) MakeHudTransportCircle(Context ctx, OneUiIconType iconType, int boxDp, int icoDp, Action onClick) {
        FrameLayout btn = new(ctx);
        btn.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(175, 18, 20, 28), 999f, Color.Argb(85, 255, 255, 255), 1.4f);
        OneUiTheme.AttachRecoil(btn, 0.89f);
        OneUiIconView ico = new(ctx, iconType, OneUiTheme.TextPrimary);
        btn.AddView(ico, new LayoutParams(Dp(icoDp), Dp(icoDp)) { Gravity = GravityFlags.Center });
        btn.Click += (_, _) => onClick?.Invoke();
        return (btn, ico);
    }

    public override bool OnTouchEvent(MotionEvent e) {
        gestureDetector.OnTouchEvent(e);
        return true;
    }

    protected override void OnLayout(bool changed, int left, int top, int right, int bottom) {
        base.OnLayout(changed, left, top, right, bottom);
        if (changed) {
            UpdateVideoTransform();
        }
    }

    public void ConfigureMetadata(
        string title,
        string spatialBadge,
        string trackSummary,
        bool hasMultipleAudioTracks,
        bool hasSubtitles,
        bool isLooping,
        string unlockText) {
        hudTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Video" : title;
        spatialPillText.Text = string.IsNullOrWhiteSpace(spatialBadge) ? "Spatial 3D" : spatialBadge;
        if (hasMultipleAudioTracks) {
            trackPillLabel.Text = trackSummary ?? "Faixa";
            trackPillBtn.Visibility = ViewStates.Visible;
        } else {
            trackPillBtn.Visibility = ViewStates.Gone;
        }
        isSubtitlesActive = hasSubtitles;
        ccIcon.IconColor = hasSubtitles ? OneUiTheme.PrimaryHigh : OneUiTheme.TextPrimary;
        ccBtn.Background = hasSubtitles
            ? OneUiTheme.CreateRippleRoundedDrawable(Context, OneUiTheme.WithAlpha(OneUiTheme.Primary, 140), 99f)
            : OneUiTheme.CreateRippleRoundedDrawable(Context, Color.Argb(115, 20, 22, 30), 99f);
        loopIcon.IconColor = isLooping ? OneUiTheme.PrimaryHigh : OneUiTheme.TextPrimary;
        unlockPillLabel.Text = unlockText ?? "Destravar";
    }

    public void SetFullscreenMode(bool fullscreen) {
        isFullscreen = fullscreen;
        outlineProvider.RadiusPx = fullscreen ? 0f : Dp(20f);
        ClipToOutline = !fullscreen;
        InvalidateOutline();
        fullscreenIcon.IconType = fullscreen ? OneUiIconType.FullscreenExit : OneUiIconType.FullscreenEnter;
        fullscreenSeekRow.Visibility = fullscreen ? ViewStates.Visible : ViewStates.Gone;
        inlineTimeText.Visibility = fullscreen ? ViewStates.Gone : ViewStates.Visible;
        subtitleOverlayView.TextSize = fullscreen ? 17f : 13.5f;
        UpdateSubtitleMargin();
        ShowHudOverlay(autoHide: desiredPlaying);
    }

    public void SetSubtitles(IReadOnlyList<SubtitleCue> cues) {
        subtitleCues = cues ?? [];
        lastSubRendered = null;
        isSubtitlesActive = subtitleCues.Count > 0;
        ccIcon.IconColor = isSubtitlesActive ? OneUiTheme.PrimaryHigh : OneUiTheme.TextPrimary;
        ccBtn.Background = isSubtitlesActive
            ? OneUiTheme.CreateRippleRoundedDrawable(Context, OneUiTheme.WithAlpha(OneUiTheme.Primary, 140), 99f)
            : OneUiTheme.CreateRippleRoundedDrawable(Context, Color.Argb(115, 20, 22, 30), 99f);
        UpdateSubtitleOverlay(lastAudioSeconds);
    }

    public void BindVideoSource(string videoPath, int knownWidth, int knownHeight) {
        if (string.Equals(currentVideoPath, videoPath, StringComparison.Ordinal)) {
            return;
        }
        currentVideoPath = videoPath;
        videoWidth = knownWidth;
        videoHeight = knownHeight;
        pendingSeekMs = -1;
        hudErrorText.Visibility = ViewStates.Gone;
        ReleasePlayer();

        if (!string.IsNullOrEmpty(currentVideoPath) && textureView.IsAvailable) {
            CreateAndPreparePlayer(textureView.SurfaceTexture);
        }
    }

    public void PrepareSurfaceTransferSeek(double currentSeconds) {
        lastAudioSeconds = currentSeconds;
        pendingSeekMs = Math.Max(0, (long)Math.Round(currentSeconds * 1000.0));
    }

    public void SyncPlaybackState(bool playing, double currentSeconds, double totalSeconds = 0) {
        desiredPlaying = playing;
        lastAudioSeconds = currentSeconds;
        if (totalSeconds > 0) {
            lastTotalSeconds = totalSeconds;
        }
        centerPlayIcon.IconType = playing ? OneUiIconType.Pause : OneUiIconType.Play;

        string curStr = $"{TimeSpan.FromSeconds(Math.Max(0, currentSeconds)):mm\\:ss}";
        string totStr = $"{TimeSpan.FromSeconds(Math.Max(0, lastTotalSeconds)):mm\\:ss}";
        inlineTimeText.Text = $"{curStr} / {totStr}";
        if (isFullscreen) {
            fsCurTimeText.Text = curStr;
            fsTotTimeText.Text = totStr;
            if (!isUserSeekingFs && lastTotalSeconds > 0) {
                fullscreenSeekBar.Progress = Math.Clamp((int)Math.Round(currentSeconds / lastTotalSeconds * 1000.0), 0, 1000);
                fullscreenSeekBar.Invalidate();
            }
        }

        UpdateSubtitleOverlay(currentSeconds);

        if (!isPrepared || mediaPlayer == null) {
            pendingSeekMs = Math.Max(0, (long)Math.Round(currentSeconds * 1000.0));
            return;
        }

        long targetMs = Math.Max(0, (long)Math.Round(currentSeconds * 1000.0));
        try {
            if (playing) {
                if (!mediaPlayer.IsPlaying) {
                    SeekInternal(targetMs);
                    mediaPlayer.Start();
                    ScheduleHudAutoHide();
                } else {
                    long drift = Math.Abs(mediaPlayer.CurrentPosition - targetMs);
                    long now = SystemClock.UptimeMillis();
                    if (drift > 450 && now - lastDriftCorrectionMs > 1200) {
                        SeekInternal(targetMs);
                    }
                }
            } else {
                if (mediaPlayer.IsPlaying) {
                    mediaPlayer.Pause();
                }
                if (!IsControlsLocked) {
                    ShowHudOverlay(autoHide: false);
                }
            }
        } catch {
            // Guard against illegal state transitions on hardware codecs
        }
    }

    void UpdateSubtitleOverlay(double currentSeconds) {
        string text = SubtitleService.FindActiveText(subtitleCues, currentSeconds);
        if (string.Equals(text, lastSubRendered, StringComparison.Ordinal)) {
            return;
        }
        lastSubRendered = text;
        if (string.IsNullOrEmpty(text)) {
            subtitleOverlayView.Visibility = ViewStates.Gone;
        } else {
            subtitleOverlayView.Text = text;
            subtitleOverlayView.Visibility = ViewStates.Visible;
        }
    }

    void UpdateSubtitleMargin() {
        if (subtitleOverlayView.LayoutParameters is LayoutParams lp) {
            int desiredBot = IsControlsLocked
                ? Dp(20)
                : (hudVisible ? Dp(isFullscreen ? 94 : 64) : Dp(isFullscreen ? 26 : 14));
            if (lp.BottomMargin != desiredBot) {
                lp.BottomMargin = desiredBot;
                subtitleOverlayView.LayoutParameters = lp;
            }
        }
    }

    public void SeekTo(double seconds) {
        lastAudioSeconds = seconds;
        long ms = Math.Max(0, (long)Math.Round(seconds * 1000.0));
        pendingSeekMs = ms;
        UpdateSubtitleOverlay(seconds);
        if (isPrepared && mediaPlayer != null) {
            try {
                SeekInternal(ms);
            } catch {
                // Ignore if resetting
            }
        }
    }

    public void PauseVideoDecoderOnly() {
        desiredPlaying = false;
        try {
            if (isPrepared && mediaPlayer != null && mediaPlayer.IsPlaying) {
                mediaPlayer.Pause();
            }
        } catch {
            // Ignore
        }
    }

    void SeekInternal(long ms) {
        lastDriftCorrectionMs = SystemClock.UptimeMillis();
        mediaPlayer?.SeekTo((int)Math.Clamp(ms, 0, int.MaxValue));
    }

    void CycleAspectMode() {
        aspectMode = aspectMode switch {
            VideoAspectMode.Fit => VideoAspectMode.Crop,
            VideoAspectMode.Crop => VideoAspectMode.Stretch,
            _ => VideoAspectMode.Fit
        };
        UpdateVideoTransform();
        string msg = aspectMode switch {
            VideoAspectMode.Crop => "Proporção: Preencher",
            VideoAspectMode.Stretch => "Proporção: Esticar",
            _ => "Proporção: Ajustar"
        };
        ShowAspectToast(msg);
    }

    void ShowAspectToast(string msg) {
        if (pendingToastHide != null) {
            hideHandler.RemoveCallbacks(pendingToastHide);
        }
        hudAspectToast.Text = msg;
        hudAspectToast.Visibility = ViewStates.Visible;
        hudAspectToast.Alpha = 1f;
        pendingToastHide = new Java.Lang.Runnable(() => {
            hudAspectToast.Animate().Alpha(0f).SetDuration(220).WithEndAction(
                new Java.Lang.Runnable(() => hudAspectToast.Visibility = ViewStates.Gone)).Start();
        });
        hideHandler.PostDelayed(pendingToastHide, 1300);
    }

    void SetControlsLocked(bool locked) {
        IsControlsLocked = locked;
        if (locked) {
            HideHudOverlay();
            FlashUnlockHint();
        } else {
            if (pendingUnlockHide != null) hideHandler.RemoveCallbacks(pendingUnlockHide);
            unlockFloatingPill.Visibility = ViewStates.Gone;
            ShowHudOverlay(autoHide: desiredPlaying);
        }
        UpdateSubtitleMargin();
    }

    internal void FlashUnlockHint() {
        if (pendingUnlockHide != null) {
            hideHandler.RemoveCallbacks(pendingUnlockHide);
        }
        unlockFloatingPill.Visibility = ViewStates.Visible;
        unlockFloatingPill.Alpha = 1f;
        pendingUnlockHide = new Java.Lang.Runnable(() => {
            if (IsControlsLocked) {
                unlockFloatingPill.Animate().Alpha(0f).SetDuration(220).WithEndAction(
                    new Java.Lang.Runnable(() => unlockFloatingPill.Visibility = ViewStates.Gone)).Start();
            }
        });
        hideHandler.PostDelayed(pendingUnlockHide, 2200);
    }

    void CreateAndPreparePlayer(SurfaceTexture st) {
        if (st == null || string.IsNullOrEmpty(currentVideoPath)) {
            return;
        }
        ReleasePlayer();
        try {
            activeSurface = new Surface(st);
            isPrepared = false;
            mediaPlayer = new AndroidMediaPlayer();
            mediaPlayer.SetSurface(activeSurface);
            mediaPlayer.SetDataSource(currentVideoPath);
            mediaPlayer.SetVolume(0f, 0f);
            mediaPlayer.Looping = false;

            mediaPlayer.Prepared += (_, _) => {
                isPrepared = true;
                try {
                    mediaPlayer?.SetVolume(0f, 0f);
                    if (mediaPlayer != null) {
                        if (mediaPlayer.VideoWidth > 0) videoWidth = mediaPlayer.VideoWidth;
                        if (mediaPlayer.VideoHeight > 0) videoHeight = mediaPlayer.VideoHeight;
                        UpdateVideoTransform();
                        long seekTarget = pendingSeekMs >= 0
                            ? pendingSeekMs
                            : Math.Max(0, (long)Math.Round(lastAudioSeconds * 1000.0));
                        if (seekTarget > 0) {
                            SeekInternal(seekTarget);
                        }
                        if (desiredPlaying && Visibility == ViewStates.Visible) {
                            mediaPlayer.Start();
                            ScheduleHudAutoHide();
                        }
                    }
                } catch {
                    // Ignore codec teardown race
                }
            };

            mediaPlayer.Error += (_, _) => {
                isPrepared = false;
                hudErrorText.Text = "Prévia de vídeo indisponível para este codec (Áudio espacial segue ativo)";
                hudErrorText.Visibility = ViewStates.Visible;
                ShowHudOverlay(autoHide: false);
            };

            mediaPlayer.PrepareAsync();
        } catch {
            hudErrorText.Text = "Prévia de vídeo indisponível para este arquivo";
            hudErrorText.Visibility = ViewStates.Visible;
            ReleasePlayer();
        }
    }

    void UpdateVideoTransform() {
        int viewW = textureView.Width;
        int viewH = textureView.Height;
        if (viewW <= 0 || viewH <= 0 || videoWidth <= 0 || videoHeight <= 0) {
            return;
        }
        float sx;
        float sy;
        if (aspectMode == VideoAspectMode.Stretch) {
            sx = 1f;
            sy = 1f;
        } else {
            float scale = aspectMode == VideoAspectMode.Crop
                ? Math.Max(viewW / (float)videoWidth, viewH / (float)videoHeight)
                : Math.Min(viewW / (float)videoWidth, viewH / (float)videoHeight);
            sx = videoWidth * scale / viewW;
            sy = videoHeight * scale / viewH;
        }

        Matrix m = new();
        m.SetScale(sx, sy, viewW * 0.5f, viewH * 0.5f);
        textureView.SetTransform(m);
    }

    internal void HandleStageSingleTap() {
        if (IsControlsLocked) {
            FlashUnlockHint();
            return;
        }
        ToggleHudOverlay();
    }

    public void ToggleHudOverlay() {
        if (hudVisible) {
            HideHudOverlay();
        } else {
            ShowHudOverlay(autoHide: desiredPlaying);
        }
    }

    void ShowHudOverlay(bool autoHide) {
        if (IsControlsLocked) return;
        CancelAutoHide();
        hudVisible = true;
        hudOverlay.Visibility = ViewStates.Visible;
        hudOverlay.Animate().Cancel();
        hudOverlay.Animate().Alpha(1f).SetDuration(180).Start();
        UpdateSubtitleMargin();
        if (autoHide) {
            ScheduleHudAutoHide();
        }
    }

    void HideHudOverlay() {
        CancelAutoHide();
        hudVisible = false;
        hudOverlay.Animate().Cancel();
        hudOverlay.Animate().Alpha(0f).SetDuration(220).WithEndAction(new Java.Lang.Runnable(() => {
            if (!hudVisible) {
                hudOverlay.Visibility = ViewStates.Gone;
                UpdateSubtitleMargin();
            }
        })).Start();
    }

    void ScheduleHudAutoHide() {
        CancelAutoHide();
        if (!desiredPlaying || IsControlsLocked) return;
        pendingAutoHide = new Java.Lang.Runnable(HideHudOverlay);
        hideHandler.PostDelayed(pendingAutoHide, 2800);
    }

    void CancelAutoHide() {
        if (pendingAutoHide != null) {
            hideHandler.RemoveCallbacks(pendingAutoHide);
            pendingAutoHide = null;
        }
    }

    internal void HandleDoubleTapSeek(double deltaSec) {
        SeekDeltaRequested?.Invoke(deltaSec);
        bloomOverlay.Trigger(deltaSec >= 0, deltaSec >= 0 ? $"+{deltaSec:0}s" : $"{deltaSec:0}s");
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) {
        if (!string.IsNullOrEmpty(currentVideoPath) && mediaPlayer == null) {
            CreateAndPreparePlayer(surface);
        } else if (mediaPlayer != null) {
            activeSurface?.Release();
            activeSurface = new Surface(surface);
            mediaPlayer.SetSurface(activeSurface);
            UpdateVideoTransform();
        }
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) {
        pendingSeekMs = Math.Max(0, (long)Math.Round(lastAudioSeconds * 1000.0));
        ReleasePlayer();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => UpdateVideoTransform();

    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    public void ReleasePlayer() {
        CancelAutoHide();
        isPrepared = false;
        try {
            mediaPlayer?.Stop();
        } catch {
            // Ignore
        }
        try {
            mediaPlayer?.Release();
            mediaPlayer?.Dispose();
        } catch {
            // Ignore
        }
        mediaPlayer = null;
        try {
            activeSurface?.Release();
            activeSurface?.Dispose();
        } catch {
            // Ignore
        }
        activeSurface = null;
    }
}

/// <summary>
/// Faithful Apple Music Live-Lyrics sheet view (modeled on fragment_player_lyrics_sheet.xml
/// + lyrics_line_instrumental.xml):
/// - Borderless full-bleed scrolling directly over the dynamic ambient backdrop
/// - Instrumental intro / interlude waiting dots (• • •) inserted as true list rows with identical
///   spacing so they push lyrics naturally and scroll off-screen seamlessly when singing starts
/// - Crisp high-contrast active lyric typography + smooth vertical edge fade + tap-to-seek
/// </summary>
public sealed class AppleMusicLyricsView : View {
    sealed class LineLayoutInfo {
        public LyricCue Cue;
        public StaticLayout Layout;
        public bool IsInstrumental;
        public double StartSec;
        public double EndSec;
        public float TopY;
        public float Height;
    }

    readonly List<LineLayoutInfo> cachedLines = [];
    readonly List<(RectF rect, double seekSec)> hitBoxes = [];
    readonly TextPaint linePaint = new(PaintFlags.AntiAlias);
    readonly TextPaint emptyPaint = new(PaintFlags.AntiAlias);
    readonly Paint dotPaint = new(PaintFlags.AntiAlias);
    readonly int touchSlop;

    LyricsDocument document = new();
    double currentSeconds;
    bool isPlaying;
    string emptyMessage = "";

    int lastBuiltWidth;
    float totalContentHeight;
    float scrollOffsetPx;
    float targetScrollOffsetPx;
    long manualBrowseUntilMs;
    bool isDragging;
    float downY;
    float lastTouchY;
    float maxMoveDist;
    long lastFrameMs;

    public event Action<double> LineSeekRequested;

    public AppleMusicLyricsView(Context ctx) : base(ctx) {
        touchSlop = ViewConfiguration.Get(ctx)?.ScaledTouchSlop ?? 24;
        float dp = ctx.Resources.DisplayMetrics.Density;
        linePaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        emptyPaint.TextSize = 14.5f * dp;
        emptyPaint.Color = OneUiTheme.TextSub;
        dotPaint.SetStyle(Paint.Style.Fill);
    }

    public void SetLyrics(LyricsDocument doc, string emptyText, Color _) {
        document = doc ?? new LyricsDocument();
        emptyMessage = emptyText ?? "";
        lastBuiltWidth = -1;
        scrollOffsetPx = 0f;
        targetScrollOffsetPx = 0f;
        manualBrowseUntilMs = 0;
        RebuildLayouts();
        Invalidate();
    }

    public void UpdatePlayback(double seconds, bool playing) {
        bool stateChanged = Math.Abs(currentSeconds - seconds) > 0.04 || isPlaying != playing;
        currentSeconds = seconds;
        isPlaying = playing;
        if (stateChanged && Visibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    void RebuildLayouts() {
        cachedLines.Clear();
        totalContentHeight = 0f;
        float dp = Resources.DisplayMetrics.Density;
        int availW = Math.Max(40, Width - (int)(24f * dp));
        if (availW <= 40 || !document.HasLyrics) {
            return;
        }
        lastBuiltWidth = Width;

        linePaint.TextSize = 22.5f * dp;
        float y = 20f * dp;
        float lineSpacing = 18f * dp;
        float instrH = 34f * dp;

        if (document.IsSynced && document.Lines.Count > 0 && document.Lines[0].TimeSeconds >= 3.0) {
            cachedLines.Add(new LineLayoutInfo {
                Cue = new LyricCue(0, ""),
                IsInstrumental = true,
                StartSec = 0,
                EndSec = document.Lines[0].TimeSeconds,
                TopY = y,
                Height = instrH
            });
            y += instrH + lineSpacing;
        }

        for (int i = 0; i < document.Lines.Count; i++) {
            LyricCue cue = document.Lines[i];
            string trimmed = (cue.Text ?? "").Trim();
            bool explicitInstr = string.IsNullOrWhiteSpace(trimmed) || trimmed is "..." or "…" or "• • •" or "•";

            if (explicitInstr && document.IsSynced) {
                double end = i + 1 < document.Lines.Count ? document.Lines[i + 1].TimeSeconds : cue.TimeSeconds + 8.0;
                cachedLines.Add(new LineLayoutInfo {
                    Cue = cue,
                    IsInstrumental = true,
                    StartSec = cue.TimeSeconds,
                    EndSec = Math.Max(cue.TimeSeconds + 1.0, end),
                    TopY = y,
                    Height = instrH
                });
                y += instrH + lineSpacing;
                continue;
            }

            string txt = string.IsNullOrWhiteSpace(trimmed) ? "• • •" : trimmed;
            StaticLayout sl = StaticLayout.Builder
                .Obtain(txt, 0, txt.Length, linePaint, availW)
                .SetAlignment(Android.Text.Layout.Alignment.AlignNormal)
                .SetLineSpacing(2f * dp, 1.05f)
                .SetIncludePad(false)
                .Build();
            float h = sl.Height;
            cachedLines.Add(new LineLayoutInfo {
                Cue = cue,
                Layout = sl,
                IsInstrumental = false,
                StartSec = cue.TimeSeconds,
                EndSec = i + 1 < document.Lines.Count ? document.Lines[i + 1].TimeSeconds : cue.TimeSeconds + 60.0,
                TopY = y,
                Height = h
            });
            y += h + lineSpacing;

            // Insert true instrumental row for long gaps (modeled on Apple Music lyrics_line_instrumental.xml)
            if (document.IsSynced && i + 1 < document.Lines.Count) {
                double gap = document.Lines[i + 1].TimeSeconds - cue.TimeSeconds;
                if (gap >= 9.0) {
                    cachedLines.Add(new LineLayoutInfo {
                        Cue = new LyricCue(cue.TimeSeconds + 4.2, ""),
                        IsInstrumental = true,
                        StartSec = cue.TimeSeconds + 4.2,
                        EndSec = document.Lines[i + 1].TimeSeconds,
                        TopY = y,
                        Height = instrH
                    });
                    y += instrH + lineSpacing;
                }
            }
        }
        totalContentHeight = y + 90f * dp;
    }

    int ResolveActiveIndex() {
        if (!document.IsSynced || cachedLines.Count == 0) {
            return -1;
        }
        int active = -1;
        for (int i = 0; i < cachedLines.Count; i++) {
            double triggerTime = cachedLines[i].IsInstrumental ? cachedLines[i].StartSec : cachedLines[i].Cue.TimeSeconds;
            if (triggerTime <= currentSeconds + 0.14) {
                active = i;
            } else {
                break;
            }
        }
        return active;
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) return;

        float dp = Resources.DisplayMetrics.Density;
        if (!document.HasLyrics || (cachedLines.Count == 0 && lastBuiltWidth == Width)) {
            int msgW = Math.Max(40, (int)(w - 40f * dp));
            string msg = emptyMessage ?? "";
            StaticLayout emptySl = StaticLayout.Builder
                .Obtain(msg, 0, msg.Length, emptyPaint, msgW)
                .SetAlignment(Android.Text.Layout.Alignment.AlignCenter)
                .SetLineSpacing(3f * dp, 1.15f)
                .Build();
            canvas.Save();
            canvas.Translate((w - msgW) * 0.5f, Math.Max(20f * dp, (h - emptySl.Height) * 0.45f));
            emptySl.Draw(canvas);
            canvas.Restore();
            return;
        }

        if (lastBuiltWidth != Width) {
            RebuildLayouts();
        }
        if (cachedLines.Count == 0) {
            return;
        }

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) lastFrameMs = nowMs;
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0.001f, 0.05f);
        lastFrameMs = nowMs;

        int activeIdx = ResolveActiveIndex();
        float maxScroll = Math.Max(0f, totalContentHeight - h * 0.55f);
        bool manualActive = isDragging || nowMs < manualBrowseUntilMs;

        if (!manualActive && document.IsSynced && activeIdx >= 0 && activeIdx < cachedLines.Count) {
            LineLayoutInfo act = cachedLines[activeIdx];
            float focalAnchor = h * 0.28f;
            targetScrollOffsetPx = Math.Clamp(act.TopY + act.Height * 0.5f - focalAnchor, 0f, maxScroll);
        } else if (!manualActive && document.IsSynced && activeIdx < 0) {
            targetScrollOffsetPx = 0f;
        }

        float diff = targetScrollOffsetPx - scrollOffsetPx;
        if (Math.Abs(diff) > 0.5f && !isDragging) {
            scrollOffsetPx += diff * Math.Min(1f, dt * 8.5f);
        } else if (!isDragging) {
            scrollOffsetPx = targetScrollOffsetPx;
        }

        hitBoxes.Clear();
        float padX = 12f * dp;
        bool hasActiveInstrumental = false;

        for (int i = 0; i < cachedLines.Count; i++) {
            LineLayoutInfo info = cachedLines[i];
            float drawY = info.TopY - scrollOffsetPx;
            if (drawY + info.Height < -12f * dp || drawY > h + 12f * dp) {
                continue;
            }

            bool isActive = document.IsSynced && i == activeIdx;
            float centerY = drawY + info.Height * 0.5f;

            // Vertical edge fade (top 28dp and bottom 54dp) matching Apple Music AlphaGradientFrameLayout
            float edgeFactor = 1f;
            float topFadeH = 26f * dp;
            float botFadeH = 54f * dp;
            if (centerY < topFadeH) {
                edgeFactor = Math.Clamp(centerY / topFadeH, 0.04f, 1f);
            } else if (centerY > h - botFadeH) {
                edgeFactor = Math.Clamp((h - centerY) / botFadeH, 0.04f, 1f);
            }

            if (info.IsInstrumental) {
                if (isActive) {
                    hasActiveInstrumental = true;
                    double dur = Math.Max(0.8, info.EndSec - info.StartSec);
                    float prog = Math.Clamp((float)((currentSeconds - info.StartSec) / dur), 0f, 1f);
                    float breathe = (float)(0.5 + 0.5 * Math.Sin(nowMs * 0.0046));
                    for (int d = 0; d < 3; d++) {
                        float stepGate = Math.Clamp((prog - d * 0.25f) / 0.35f, 0.32f, 1f);
                        float dotPulse = (float)(0.5 + 0.5 * Math.Sin(nowMs * 0.0046 - d * 0.78));
                        float r = (4.8f + dotPulse * 1.4f + breathe * 0.4f) * dp;
                        int a = Math.Clamp((int)((135 + 120 * stepGate) * edgeFactor), 20, 255);
                        dotPaint.Color = Color.Argb(a, 252, 252, 255);
                        canvas.DrawCircle(padX + 10f * dp + d * 18f * dp, centerY, r, dotPaint);
                    }
                } else {
                    int baseDotA = i < activeIdx ? 42 : 76;
                    dotPaint.Color = Color.Argb(Math.Clamp((int)(baseDotA * edgeFactor), 8, 255), 230, 232, 240);
                    for (int d = 0; d < 3; d++) {
                        canvas.DrawCircle(padX + 10f * dp + d * 18f * dp, centerY, 4.0f * dp, dotPaint);
                    }
                }
                hitBoxes.Add((new RectF(0f, drawY - 4f * dp, w, drawY + info.Height + 4f * dp), info.StartSec));
                continue;
            }

            int baseAlpha = !document.IsSynced
                ? 235
                : (isActive ? 255 : (i < activeIdx ? 72 : 118));
            int finalAlpha = Math.Clamp((int)(baseAlpha * edgeFactor), 12, 255);
            linePaint.Color = isActive
                ? Color.Argb(finalAlpha, 255, 255, 255)
                : Color.Argb(finalAlpha, 236, 238, 246);

            float scale = isActive ? 1.035f : 0.945f;
            canvas.Save();
            canvas.Translate(padX, drawY);
            canvas.Scale(scale, scale, 0f, info.Height * 0.5f);
            info.Layout.Draw(canvas);
            canvas.Restore();

            hitBoxes.Add((new RectF(0f, drawY - 6f * dp, w, drawY + info.Height + 6f * dp), info.Cue.TimeSeconds));
        }

        if ((Math.Abs(targetScrollOffsetPx - scrollOffsetPx) > 0.5f || hasActiveInstrumental || isPlaying)
            && Visibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!document.HasLyrics || cachedLines.Count == 0) {
            return base.OnTouchEvent(e);
        }

        float ty = e.GetY();
        float h = Math.Max(1f, Height);
        float maxScroll = Math.Max(0f, totalContentHeight - h * 0.55f);

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                isDragging = true;
                downY = ty;
                lastTouchY = ty;
                maxMoveDist = 0f;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;

            case MotionEventActions.Move: {
                float dy = ty - lastTouchY;
                float totalMove = MathF.Abs(ty - downY);
                if (totalMove > maxMoveDist) maxMoveDist = totalMove;
                scrollOffsetPx = Math.Clamp(scrollOffsetPx - dy, 0f, maxScroll);
                targetScrollOffsetPx = scrollOffsetPx;
                lastTouchY = ty;
                manualBrowseUntilMs = SystemClock.UptimeMillis() + 3400;
                Invalidate();
                return true;
            }

            case MotionEventActions.Up: {
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isDragging = false;
                bool wasTap = maxMoveDist < touchSlop;
                if (wasTap && document.IsSynced) {
                    foreach (var (rect, seekSec) in hitBoxes) {
                        if (rect.Contains(e.GetX(), ty)) {
                            manualBrowseUntilMs = 0;
                            LineSeekRequested?.Invoke(seekSec);
                            Invalidate();
                            return true;
                        }
                    }
                }
                manualBrowseUntilMs = SystemClock.UptimeMillis() + 3400;
                Invalidate();
                return true;
            }

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isDragging = false;
                return false;
        }
        return base.OnTouchEvent(e);
    }
}
