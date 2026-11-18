using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Services;
using CavernizeAndroid.Theme;

using AndroidMediaPlayer = Android.Media.MediaPlayer;
using AndroidMediaSeekMode = Android.Media.MediaSeekMode;

namespace CavernizeAndroid.Views;

/// <summary>
/// Outlines a view with rounded corners for clean hardware clipping (API 21+).
/// </summary>
public sealed class RoundedClipOutlineProvider(float radiusPx) : ViewOutlineProvider {
    public override void GetOutline(View view, Outline outline) {
        if (view == null || outline == null) return;
        outline.SetRoundRect(0, 0, view.Width, view.Height, radiusPx);
    }
}

/// <summary>
/// Renders muted hardware-accelerated video inside a <see cref="TextureView"/> synchronized
/// frame-accurately to Cavernize's real-time spatial audio clock, with YouTube / Samsung Video Player
/// single-tap HUD overlay and double-tap ±10s radial bloom animations.
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

            // Triple chevron directional cue (like YouTube / Samsung Video double-tap)
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
            owner.ToggleHudOverlay();
            return true;
        }

        public override bool OnDoubleTap(MotionEvent e) {
            float x = e.GetX();
            float w = Math.Max(1f, owner.Width);
            if (x < w * 0.38f) {
                owner.HandleDoubleTapSeek(-10);
            } else if (x > w * 0.62f) {
                owner.HandleDoubleTapSeek(10);
            } else {
                owner.PlayPauseRequested?.Invoke();
            }
            return true;
        }
    }

    readonly TextureView textureView;
    readonly DoubleTapBloomView bloomOverlay;
    readonly FrameLayout hudOverlay;
    readonly LinearLayout hudPlayBtn;
    readonly OneUiIconView hudPlayIcon;
    readonly TextView hudBadgeText;
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
    long pendingSeekMs = -1;
    long lastDriftCorrectionMs;
    bool hudVisible = true;
    Java.Lang.Runnable pendingAutoHide;

    public event Action PlayPauseRequested;
    public event Action<double> SeekDeltaRequested;

    public SyncedVideoStageView(Context ctx, string badgeLabel) : base(ctx) {
        SetBackgroundColor(Color.Rgb(8, 9, 13));
        OutlineProvider = new RoundedClipOutlineProvider(OneUiTheme.Dp(ctx, 22f));
        ClipToOutline = true;
        Elevation = OneUiTheme.Dp(ctx, 12f);

        textureView = new TextureView(ctx) {
            SurfaceTextureListener = this
        };
        AddView(textureView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        bloomOverlay = new DoubleTapBloomView(ctx) { Visibility = ViewStates.Gone };
        AddView(bloomOverlay, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        hudOverlay = new FrameLayout(ctx);
        hudOverlay.SetBackgroundColor(Color.Argb(96, 0, 0, 0));

        hudBadgeText = new TextView(ctx) {
            Text = badgeLabel ?? "Synced 3D Video",
            TextSize = 11f
        };
        hudBadgeText.SetTextColor(OneUiTheme.TextPrimary);
        hudBadgeText.SetIncludeFontPadding(false);
        hudBadgeText.SetSingleLine(true);
        hudBadgeText.SetTypeface(null, TypefaceStyle.Bold);
        hudBadgeText.SetPadding(OneUiTheme.Dp(ctx, 10f), OneUiTheme.Dp(ctx, 5f), OneUiTheme.Dp(ctx, 10f), OneUiTheme.Dp(ctx, 5f));
        hudBadgeText.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(168, 22, 24, 32), OneUiTheme.PillCornerDp);
        hudOverlay.AddView(hudBadgeText, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            LeftMargin = OneUiTheme.Dp(ctx, 12f),
            TopMargin = OneUiTheme.Dp(ctx, 12f)
        });

        hudPlayBtn = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        hudPlayBtn.SetGravity(GravityFlags.Center);
        hudPlayBtn.Background = OneUiTheme.CreateRoundedDrawable(ctx, Color.Argb(185, 22, 24, 34), 999f, Color.Argb(75, 255, 255, 255), 1.5f);
        OneUiTheme.AttachRecoil(hudPlayBtn, 0.90f);
        hudPlayIcon = new OneUiIconView(ctx, OneUiIconType.Play, OneUiTheme.TextPrimary);
        hudPlayBtn.AddView(hudPlayIcon, new LinearLayout.LayoutParams(OneUiTheme.Dp(ctx, 24f), OneUiTheme.Dp(ctx, 24f)));
        hudPlayBtn.Click += (_, _) => {
            PlayPauseRequested?.Invoke();
            ScheduleHudAutoHide();
        };
        hudOverlay.AddView(hudPlayBtn, new LayoutParams(OneUiTheme.Dp(ctx, 56f), OneUiTheme.Dp(ctx, 56f)) {
            Gravity = GravityFlags.Center
        });

        hudErrorText = new TextView(ctx) {
            TextSize = 12f,
            Gravity = GravityFlags.Center,
            Visibility = ViewStates.Gone
        };
        hudErrorText.SetTextColor(OneUiTheme.TextSub);
        hudErrorText.SetPadding(OneUiTheme.Dp(ctx, 18f), 0, OneUiTheme.Dp(ctx, 18f), 0);
        hudOverlay.AddView(hudErrorText, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
            Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
            BottomMargin = OneUiTheme.Dp(ctx, 14f)
        });

        AddView(hudOverlay, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        gestureDetector = new GestureDetector(ctx, new StageGestureListener(this));
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

    public void SetBadgeText(string text) {
        hudBadgeText.Text = text ?? "";
    }

    public void BindVideoSource(string videoPath, int knownWidth, int knownHeight) {
        if (string.Equals(currentVideoPath, videoPath, StringComparison.Ordinal)) {
            return;
        }
        currentVideoPath = videoPath;
        videoWidth = knownWidth;
        videoHeight = knownHeight;
        hudErrorText.Visibility = ViewStates.Gone;
        ReleasePlayer();

        if (!string.IsNullOrEmpty(currentVideoPath) && textureView.IsAvailable) {
            CreateAndPreparePlayer(textureView.SurfaceTexture);
        }
    }

    public void SyncPlaybackState(bool playing, double currentSeconds) {
        desiredPlaying = playing;
        hudPlayIcon.IconType = playing ? OneUiIconType.Pause : OneUiIconType.Play;

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
                ShowHudOverlay(autoHide: false);
            }
        } catch {
            // Guard against illegal state transitions on hardware codecs
        }
    }

    public void SeekTo(double seconds) {
        long ms = Math.Max(0, (long)Math.Round(seconds * 1000.0));
        pendingSeekMs = ms;
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
        mediaPlayer?.SeekTo(ms, AndroidMediaSeekMode.ClosestSync);
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
                        if (pendingSeekMs > 0) {
                            SeekInternal(pendingSeekMs);
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
                hudErrorText.Text = "Video preview unavailable for this container codec (Spatial audio unaffected)";
                hudErrorText.Visibility = ViewStates.Visible;
                ShowHudOverlay(autoHide: false);
            };

            mediaPlayer.PrepareAsync();
        } catch {
            hudErrorText.Text = "Video preview unavailable for this file";
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
        float fitScale = Math.Min(viewW / (float)videoWidth, viewH / (float)videoHeight);
        float targetW = videoWidth * fitScale;
        float targetH = videoHeight * fitScale;
        float sx = targetW / viewW;
        float sy = targetH / viewH;

        Matrix m = new();
        m.SetScale(sx, sy, viewW * 0.5f, viewH * 0.5f);
        textureView.SetTransform(m);
    }

    public void ToggleHudOverlay() {
        if (hudVisible) {
            HideHudOverlay();
        } else {
            ShowHudOverlay(autoHide: desiredPlaying);
        }
    }

    void ShowHudOverlay(bool autoHide) {
        CancelAutoHide();
        hudVisible = true;
        hudOverlay.Visibility = ViewStates.Visible;
        hudOverlay.Animate().Cancel();
        hudOverlay.Animate().Alpha(1f).SetDuration(180).Start();
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
            }
        })).Start();
    }

    void ScheduleHudAutoHide() {
        CancelAutoHide();
        if (!desiredPlaying) return;
        pendingAutoHide = new Java.Lang.Runnable(HideHudOverlay);
        hideHandler.PostDelayed(pendingAutoHide, 2600);
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
/// Apple Music Live-Lyrics inspired view: highlights active timed lines in crisp bold typography
/// with accent glow, smooth spring scroll, animated waiting dots during instrumental breaks,
/// vertical edge fade, manual browse cooldown, and tap-any-line to seek.
/// </summary>
public sealed class AppleMusicLyricsView : View {
    sealed class LineLayoutInfo {
        public LyricCue Cue;
        public StaticLayout Layout;
        public float TopY;
        public float Height;
    }

    readonly List<LineLayoutInfo> cachedLines = [];
    readonly List<(RectF rect, int index)> hitBoxes = [];
    readonly TextPaint linePaint = new(PaintFlags.AntiAlias);
    readonly TextPaint emptyPaint = new(PaintFlags.AntiAlias);
    readonly Paint glowPaint = new(PaintFlags.AntiAlias);
    readonly Paint dotPaint = new(PaintFlags.AntiAlias);
    readonly Paint fadePaint = new(PaintFlags.AntiAlias);
    readonly int touchSlop;

    LyricsDocument document = new();
    double currentSeconds;
    bool isPlaying;
    string emptyMessage = "";
    Color accentColor = OneUiTheme.Primary;

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
        emptyPaint.TextSize = 13.5f * dp;
        emptyPaint.Color = OneUiTheme.TextSub;
        glowPaint.SetStyle(Paint.Style.Fill);
        dotPaint.SetStyle(Paint.Style.Fill);
        fadePaint.SetStyle(Paint.Style.Fill);
    }

    public void SetLyrics(LyricsDocument doc, string emptyText, Color accent) {
        document = doc ?? new LyricsDocument();
        emptyMessage = emptyText ?? "";
        accentColor = accent;
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
        int availW = Math.Max(40, Width - (int)(32f * dp));
        if (availW <= 40 || !document.HasLyrics) {
            return;
        }
        lastBuiltWidth = Width;

        linePaint.TextSize = 19.5f * dp;
        float y = 26f * dp;
        float lineSpacing = 14f * dp;

        for (int i = 0; i < document.Lines.Count; i++) {
            LyricCue cue = document.Lines[i];
            string txt = string.IsNullOrWhiteSpace(cue.Text) ? "• • •" : cue.Text;
            StaticLayout sl = StaticLayout.Builder
                .Obtain(txt, 0, txt.Length, linePaint, availW)
                .SetAlignment(Android.Text.Layout.Alignment.AlignNormal)
                .SetLineSpacing(2f * dp, 1.06f)
                .SetIncludePad(false)
                .Build();
            float h = sl.Height;
            cachedLines.Add(new LineLayoutInfo {
                Cue = cue,
                Layout = sl,
                TopY = y,
                Height = h
            });
            y += h + lineSpacing;
        }
        totalContentHeight = y + 40f * dp;
    }

    int ResolveActiveIndex() {
        if (!document.IsSynced || cachedLines.Count == 0) {
            return -1;
        }
        int active = -1;
        for (int i = 0; i < cachedLines.Count; i++) {
            if (cachedLines[i].Cue.TimeSeconds <= currentSeconds + 0.14) {
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
        if (!document.HasLyrics || cachedLines.Count == 0) {
            int msgW = Math.Max(40, (int)(w - 48f * dp));
            string msg = emptyMessage ?? "";
            StaticLayout emptySl = StaticLayout.Builder
                .Obtain(msg, 0, msg.Length, emptyPaint, msgW)
                .SetAlignment(Android.Text.Layout.Alignment.AlignCenter)
                .SetLineSpacing(3f * dp, 1.15f)
                .Build();
            canvas.Save();
            canvas.Translate((w - msgW) * 0.5f, Math.Max(18f * dp, (h - emptySl.Height) * 0.5f));
            emptySl.Draw(canvas);
            canvas.Restore();
            return;
        }

        if (lastBuiltWidth != Width) {
            RebuildLayouts();
        }

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) lastFrameMs = nowMs;
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0.001f, 0.05f);
        lastFrameMs = nowMs;

        int activeIdx = ResolveActiveIndex();
        float maxScroll = Math.Max(0f, totalContentHeight - h * 0.65f);
        bool manualActive = isDragging || nowMs < manualBrowseUntilMs;

        if (!manualActive && document.IsSynced && activeIdx >= 0 && activeIdx < cachedLines.Count) {
            LineLayoutInfo act = cachedLines[activeIdx];
            float focalAnchor = h * 0.36f;
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
        float padX = 16f * dp;

        // Animated Apple Music breathing vocal intro/interlude dots (when waiting > 2s for first/next line)
        bool showIntroDots = false;
        if (document.IsSynced && activeIdx < 0 && cachedLines.Count > 0 && currentSeconds + 0.5 < cachedLines[0].Cue.TimeSeconds) {
            showIntroDots = true;
        } else if (document.IsSynced && activeIdx >= 0 && activeIdx + 1 < cachedLines.Count) {
            double gap = cachedLines[activeIdx + 1].Cue.TimeSeconds - cachedLines[activeIdx].Cue.TimeSeconds;
            if (gap > 9.0 && currentSeconds - cachedLines[activeIdx].Cue.TimeSeconds > 4.5 && cachedLines[activeIdx + 1].Cue.TimeSeconds - currentSeconds > 1.5) {
                showIntroDots = true;
            }
        }

        if (showIntroDots) {
            float dotY = (activeIdx < 0 ? 12f * dp : cachedLines[activeIdx].TopY + cachedLines[activeIdx].Height + 10f * dp) - scrollOffsetPx;
            if (dotY > -20f * dp && dotY < h) {
                double phaseBase = nowMs * 0.0042;
                for (int d = 0; d < 3; d++) {
                    float wave = (float)(0.5 + 0.5 * Math.Sin(phaseBase - d * 0.85));
                    float dr = (3.4f + wave * 1.8f) * dp;
                    dotPaint.Color = Color.Argb((int)(110 + wave * 145), 255, 255, 255);
                    canvas.DrawCircle(padX + 10f * dp + d * 14f * dp, dotY, dr, dotPaint);
                }
            }
        }

        for (int i = 0; i < cachedLines.Count; i++) {
            LineLayoutInfo info = cachedLines[i];
            float drawY = info.TopY - scrollOffsetPx;
            if (drawY + info.Height < -10f * dp || drawY > h + 10f * dp) {
                continue;
            }

            bool isActive = document.IsSynced && i == activeIdx;
            float centerY = drawY + info.Height * 0.5f;

            // Smooth vertical edge vignette fade (like Apple Music lyrics_fragment)
            float edgeFactor = 1f;
            float topFadeH = 34f * dp;
            float botFadeH = 44f * dp;
            if (centerY < topFadeH) {
                edgeFactor = Math.Clamp(centerY / topFadeH, 0.08f, 1f);
            } else if (centerY > h - botFadeH) {
                edgeFactor = Math.Clamp((h - centerY) / botFadeH, 0.08f, 1f);
            }

            if (isActive) {
                glowPaint.SetShader(new LinearGradient(
                    0f, drawY - 4f * dp, w, drawY + info.Height + 6f * dp,
                    [Color.Argb(38, accentColor.R, accentColor.G, accentColor.B), Color.Argb(16, 255, 255, 255)],
                    null,
                    Shader.TileMode.Clamp!));
                canvas.DrawRoundRect(
                    new RectF(padX - 8f * dp, drawY - 4f * dp, w - padX + 8f * dp, drawY + info.Height + 4f * dp),
                    14f * dp, 14f * dp, glowPaint);
                glowPaint.SetShader(null);
            }

            int baseAlpha = !document.IsSynced
                ? 220
                : (isActive ? 255 : (i < activeIdx ? 100 : 148));
            int finalAlpha = Math.Clamp((int)(baseAlpha * edgeFactor), 18, 255);
            linePaint.Color = isActive
                ? Color.Argb(finalAlpha, 252, 252, 255)
                : Color.Argb(finalAlpha, 226, 228, 236);

            float scale = isActive ? 1.03f : 0.94f;
            canvas.Save();
            canvas.Translate(padX, drawY);
            canvas.Scale(scale, scale, 0f, info.Height * 0.5f);
            info.Layout.Draw(canvas);
            canvas.Restore();

            hitBoxes.Add((new RectF(0f, drawY - 5f * dp, w, drawY + info.Height + 5f * dp), i));
        }

        if ((Math.Abs(targetScrollOffsetPx - scrollOffsetPx) > 0.5f || showIntroDots || isPlaying)
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
        float maxScroll = Math.Max(0f, totalContentHeight - h * 0.65f);

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
                manualBrowseUntilMs = SystemClock.UptimeMillis() + 3200;
                Invalidate();
                return true;
            }

            case MotionEventActions.Up: {
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isDragging = false;
                bool wasTap = maxMoveDist < touchSlop;
                if (wasTap && document.IsSynced) {
                    foreach (var (rect, idx) in hitBoxes) {
                        if (rect.Contains(e.GetX(), ty)) {
                            manualBrowseUntilMs = 0;
                            LineSeekRequested?.Invoke(cachedLines[idx].Cue.TimeSeconds);
                            Invalidate();
                            return true;
                        }
                    }
                }
                manualBrowseUntilMs = SystemClock.UptimeMillis() + 3200;
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
