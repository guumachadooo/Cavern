using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Native C# recreation of Samsung One UI 9's lock-screen / quick-panel WaveSeekbar
/// (com.android.systemui.media.colorfulwave.WaveSeekbarProgressDrawable + MultiWaveAreaTrackRenderer):
/// 5 overlapping translucent gradient wave ribbons emerging smoothly when playing,
/// flat rounded pill line when paused, 8dp-gapped remaining track, and glowing ring thumb.
/// </summary>
public sealed class OneUiWaveMediaSeekBar : View {
    readonly record struct WaveLayerSpec(
        Color StartColor,
        Color EndColor,
        byte Alpha,
        float HeightScale,
        float Frequency,
        float PhaseOffset,
        float Speed);

    readonly int touchSlop;
    readonly Paint baseTrackPaint = new(PaintFlags.AntiAlias);
    readonly Paint remainPaint = new(PaintFlags.AntiAlias);
    readonly Paint wavePaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbGlowPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint timeLeftPaint = new(PaintFlags.AntiAlias);
    readonly Paint timeRightPaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path wavePath = new();

    readonly List<WaveLayerSpec> layers = new(5);
    readonly float[] upperY = new float[256];
    readonly float[] lowerY = new float[256];

    Color thumbCenterColor;
    Color thumbGlowColor;
    Color remainTrackColor;

    int max = 1000;
    int progress;
    float renderedRatio;
    bool hasInitializedRatio;
    bool isPlaying;
    bool isDragging;
    bool isCancelledToScroll;
    float motionActivity; // 0 = paused flat pill line, 1 = animated flowing wave ribbons
    float dragExpand;     // 0 = sleek flush rest state, 1 = interactive expanded thumb ring & halo
    long lastFrameMs;
    float accumPhaseSec;

    float downX;
    float downY;
    int initialProgress;

    // Cached GPU shaders + reusable Matrix (0 per-frame JNI shader alloc/dispose during 120 Hz drag/playback)
    LinearGradient cachedLineGrad;
    RadialGradient cachedThumbGlowGrad;
    LinearGradient[] cachedWaveGrads;
    readonly Matrix shaderMatrix = new();
    int cachedShaderColorSig;
    bool shadersDirty = true;

    public string CurrentTimeText { get; set; } = "00:00";
    public string TotalTimeText { get; set; } = "00:00";

    public int Max {
        get => max;
        set {
            max = Math.Max(1, value);
            progress = Math.Clamp(progress, 0, max);
            renderedRatio = max > 0 ? Math.Clamp(progress / (float)max, 0f, 1f) : 0f;
            Invalidate();
        }
    }

    public int Progress {
        get => progress;
        set {
            int clamped = Math.Clamp(value, 0, max);
            if (progress != clamped) {
                progress = clamped;
                float targetRatio = max > 0 ? Math.Clamp(progress / (float)max, 0f, 1f) : 0f;
                if (!hasInitializedRatio || isDragging || MathF.Abs(targetRatio - renderedRatio) > 0.04f) {
                    renderedRatio = targetRatio;
                    hasInitializedRatio = true;
                }
                if (IsAttachedToWindow && IsShown) {
                    PostInvalidateOnAnimation();
                }
            }
        }
    }

    public bool IsWavePlaying {
        get => isPlaying;
        set {
            if (isPlaying != value) {
                isPlaying = value;
                lastFrameMs = SystemClock.UptimeMillis();
                if (IsAttachedToWindow && IsShown) {
                    PostInvalidateOnAnimation();
                }
            }
        }
    }

    public event Action StartTrackingTouch;
    public event Action<int, bool> ProgressChanged;
    public event Action StopTrackingTouch;

    public OneUiWaveMediaSeekBar(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        baseTrackPaint.SetStyle(Paint.Style.Stroke);
        baseTrackPaint.StrokeWidth = 5f * dp;
        baseTrackPaint.StrokeCap = Paint.Cap.Round;

        remainPaint.SetStyle(Paint.Style.Stroke);
        remainPaint.StrokeWidth = 5f * dp;
        remainPaint.StrokeCap = Paint.Cap.Round;

        wavePaint.SetStyle(Paint.Style.Fill);

        thumbGlowPaint.SetStyle(Paint.Style.Fill);
        thumbFillPaint.SetStyle(Paint.Style.Fill);
        thumbRingPaint.SetStyle(Paint.Style.Stroke);
        thumbRingPaint.StrokeWidth = 2f * dp;
        thumbRingPaint.Color = Color.White;

        timeLeftPaint.Color = OneUiTheme.TextSub;
        timeLeftPaint.TextSize = 11.5f * dp;
        timeLeftPaint.TextAlign = Paint.Align.Left;

        timeRightPaint.Color = OneUiTheme.TextSub;
        timeRightPaint.TextSize = 11.5f * dp;
        timeRightPaint.TextAlign = Paint.Align.Right;

        SetThemeByColor(OneUiTheme.Primary);
    }

    /// <summary>
    /// Builds the 5-layer Samsung One UI 9 WaveTheme gradient palette harmonized around <paramref name="accent"/>.
    /// </summary>
    public void SetThemeByColor(Color accent) {
        float[] hsv = new float[3];
        Color.ColorToHSV(accent, hsv);
        float hue = hsv[0];

        Color Shifted(float hDelta, float satMul, float val) {
            float[] arr = [(hue + hDelta + 360f) % 360f, Math.Clamp(Math.Max(0.45f, hsv[1]) * satMul, 0f, 1f), Math.Clamp(val, 0f, 1f)];
            return new Color(Color.HSVToColor(255, arr));
        }

        Color cMain = hsv[1] < 0.08f ? Color.ParseColor("#2FA7FD") : accent;
        Color cBright = Shifted(8f, 0.72f, 1.0f);
        Color cWarmShift = Shifted(-18f, 0.92f, 0.96f);
        Color cCoolShift = Shifted(24f, 0.88f, 0.94f);
        Color cDeep = Shifted(12f, 1.0f, 0.78f);

        layers.Clear();
        layers.Add(new WaveLayerSpec(cMain, cBright, 220, 1.00f, 0.85f, 0.00f, 0.50f));
        layers.Add(new WaveLayerSpec(cCoolShift, Color.White, 195, 0.85f, 1.05f, 1.15f, 0.48f));
        layers.Add(new WaveLayerSpec(cWarmShift, cBright, 175, 0.70f, 1.45f, 2.35f, 0.46f));
        layers.Add(new WaveLayerSpec(cDeep, cCoolShift, 155, 0.55f, 1.85f, 3.60f, 0.42f));
        layers.Add(new WaveLayerSpec(cBright, Color.White, 135, 0.40f, 2.20f, 4.85f, 0.40f));

        thumbCenterColor = cMain;
        thumbGlowColor = cBright;
        remainTrackColor = Color.ParseColor("#40FCFCFC");

        baseTrackPaint.Color = cMain;
        remainPaint.Color = remainTrackColor;
        Invalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int desiredH = OneUiTheme.Dp(Context, 54f);
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(ResolveSize(w, widthMeasureSpec), ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) {
            lastFrameMs = nowMs;
        }
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0f, 0.05f);
        lastFrameMs = nowMs;

        float targetActivity = isPlaying ? 1f : 0f;
        if (MathF.Abs(motionActivity - targetActivity) > 0.004f) {
            float rate = isPlaying ? 5.5f : 7.0f;
            motionActivity += (targetActivity - motionActivity) * Math.Min(1f, dt * rate);
        } else {
            motionActivity = targetActivity;
        }

        float targetDrag = isDragging ? 1f : 0f;
        if (MathF.Abs(dragExpand - targetDrag) > 0.004f) {
            float dRate = isDragging ? 18f : 12f;
            dragExpand += (targetDrag - dragExpand) * Math.Min(1f, dt * dRate);
        } else {
            dragExpand = targetDrag;
        }

        if (isPlaying || motionActivity > 0.004f) {
            accumPhaseSec += dt;
        }

        float dp = Resources.DisplayMetrics.Density;
        float sidePad = 10f * dp;
        float trackLeft = sidePad;
        float trackRight = w - sidePad;
        float trackWidth = Math.Max(1f, trackRight - trackLeft);
        float centerY = 18f * dp;

        float targetRatio = max > 0 ? Math.Clamp(progress / (float)max, 0f, 1f) : 0f;
        if (!hasInitializedRatio) {
            renderedRatio = targetRatio;
            hasInitializedRatio = true;
        } else if (isDragging || MathF.Abs(targetRatio - renderedRatio) > 0.045f) {
            renderedRatio = targetRatio;
        } else {
            renderedRatio += (targetRatio - renderedRatio) * Math.Min(1f, dt * 18f);
            if (MathF.Abs(targetRatio - renderedRatio) < 0.0004f) {
                renderedRatio = targetRatio;
            }
        }
        float thumbX = trackLeft + renderedRatio * trackWidth;

        // 1. Remaining Track (with smooth gap only when thumb expands)
        float remainStart = thumbX + (3f + 5f * dragExpand) * dp;
        if (remainStart < trackRight) {
            canvas.DrawLine(remainStart, centerY, trackRight, centerY, remainPaint);
        }

        // 2. Active Base Line + MultiWaveAreaTrackRenderer (5 layered translucent gradient wave ribbons)
        if (thumbX > trackLeft + 1f) {
            EnsureCachedShaders();
            if (motionActivity > 0.01f && thumbX - trackLeft > 12f * dp) {
                DrawMultiWaveRibbons(canvas, trackLeft, thumbX, centerY, dp);
            }
            if ((dragExpand > 0.05f || motionActivity > 0.05f) && cachedLineGrad != null) {
                shaderMatrix.Reset();
                shaderMatrix.SetScale(Math.Max(1f, thumbX - trackLeft), 1f);
                shaderMatrix.PostTranslate(trackLeft, centerY);
                cachedLineGrad.SetLocalMatrix(shaderMatrix);
                baseTrackPaint.SetShader(cachedLineGrad);
            } else {
                baseTrackPaint.SetShader(null);
                baseTrackPaint.Color = thumbCenterColor;
            }
            canvas.DrawLine(trackLeft, centerY, thumbX, centerY, baseTrackPaint);
            baseTrackPaint.SetShader(null);
        }

        // 3. Signature One UI 9 Thumb: sleek flush cap at rest; radial glow + translucent ring during drag
        if (dragExpand > 0.01f) {
            EnsureCachedShaders();
            float glowRadius = 11f * dp * dragExpand;
            if (cachedThumbGlowGrad != null && glowRadius >= 1f) {
                shaderMatrix.Reset();
                shaderMatrix.SetScale(glowRadius, glowRadius);
                shaderMatrix.PostTranslate(thumbX, centerY);
                cachedThumbGlowGrad.SetLocalMatrix(shaderMatrix);
                thumbGlowPaint.Alpha = Math.Clamp((int)(255 * dragExpand), 0, 255);
                thumbGlowPaint.SetShader(cachedThumbGlowGrad);
                canvas.DrawCircle(thumbX, centerY, glowRadius, thumbGlowPaint);
                thumbGlowPaint.SetShader(null);
            }

            float thumbR = (2.6f + 3.2f * dragExpand) * dp;
            thumbFillPaint.Color = Color.Argb(235, thumbCenterColor.R, thumbCenterColor.G, thumbCenterColor.B);
            canvas.DrawCircle(thumbX, centerY, thumbR, thumbFillPaint);
            thumbRingPaint.Color = Color.Argb((int)(255 * dragExpand), 255, 255, 255);
            canvas.DrawCircle(thumbX, centerY, thumbR, thumbRingPaint);
        }

        // 4. Time Labels cleanly positioned below the wave track
        float timeY = h - 7f * dp;
        canvas.DrawText(CurrentTimeText ?? "00:00", trackLeft, timeY, timeLeftPaint);
        canvas.DrawText(TotalTimeText ?? "00:00", trackRight, timeY, timeRightPaint);

        bool needsNextFrame = isPlaying ||
                              MathF.Abs(motionActivity - targetActivity) > 0.004f ||
                              MathF.Abs(dragExpand - targetDrag) > 0.004f ||
                              MathF.Abs(targetRatio - renderedRatio) > 0.0004f;
        if (needsNextFrame && IsAttachedToWindow && IsShown && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    int ComputeShaderColorSignature() {
        unchecked {
            int sig = thumbGlowColor.ToArgb() * 397 ^ layers.Count;
            for (int i = 0; i < layers.Count; i++) {
                sig = (sig * 397) ^ layers[i].StartColor.ToArgb();
                sig = (sig * 397) ^ layers[i].EndColor.ToArgb();
                sig = (sig * 397) ^ layers[i].Alpha;
            }
            return sig;
        }
    }

    void EnsureCachedShaders() {
        int sig = ComputeShaderColorSignature();
        if (!shadersDirty && sig == cachedShaderColorSig && cachedLineGrad != null && cachedThumbGlowGrad != null &&
            cachedWaveGrads != null && cachedWaveGrads.Length == layers.Count) {
            return;
        }
        DisposeCachedShaders();
        cachedShaderColorSig = sig;
        shadersDirty = false;
        if (layers.Count > 0) {
            cachedLineGrad = new LinearGradient(
                0f, 0f, 1f, 0f,
                [layers[0].StartColor.ToArgb(), layers[0].EndColor.ToArgb()],
                [0f, 1f],
                Shader.TileMode.Clamp);
            cachedWaveGrads = new LinearGradient[layers.Count];
            for (int i = 0; i < layers.Count; i++) {
                WaveLayerSpec spec = layers[i];
                Color startC = Color.Argb(spec.Alpha, spec.StartColor.R, spec.StartColor.G, spec.StartColor.B);
                Color endC = Color.Argb((byte)(spec.Alpha * 0.78f), spec.EndColor.R, spec.EndColor.G, spec.EndColor.B);
                cachedWaveGrads[i] = new LinearGradient(
                    0f, 0f, 1f, 0f,
                    [startC.ToArgb(), endC.ToArgb()],
                    [0f, 1f],
                    Shader.TileMode.Clamp);
            }
        }
        cachedThumbGlowGrad = new RadialGradient(
            0f, 0f, 1f,
            [Color.Argb(135, thumbGlowColor.R, thumbGlowColor.G, thumbGlowColor.B).ToArgb(), Color.Transparent.ToArgb()],
            [0f, 1f],
            Shader.TileMode.Clamp);
    }

    void DisposeCachedShaders() {
        baseTrackPaint.SetShader(null);
        wavePaint.SetShader(null);
        thumbGlowPaint.SetShader(null);
        try { cachedLineGrad?.Dispose(); } catch { }
        cachedLineGrad = null;
        try { cachedThumbGlowGrad?.Dispose(); } catch { }
        cachedThumbGlowGrad = null;
        if (cachedWaveGrads != null) {
            for (int i = 0; i < cachedWaveGrads.Length; i++) {
                try { cachedWaveGrads[i]?.Dispose(); } catch { }
            }
            cachedWaveGrads = null;
        }
        shadersDirty = true;
    }

    protected override void OnDetachedFromWindow() {
        DisposeCachedShaders();
        base.OnDetachedFromWindow();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            DisposeCachedShaders();
            try { shaderMatrix.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    void DrawMultiWaveRibbons(Canvas canvas, float trackLeft, float thumbX, float centerY, float dp) {
        float activeLen = thumbX - trackLeft;
        int samples = Math.Clamp((int)(activeLen / (3f * dp)), 24, upperY.Length - 1);
        float maxAmp = 8.8f * dp * motionActivity;
        float halfBase = 2.4f * dp;
        float fadeEdge = Math.Min(activeLen * 0.35f, 18f * dp);

        for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++) {
            WaveLayerSpec spec = layers[layerIdx];
            float omega = (2f * MathF.PI * 3f * spec.Frequency) / Math.Max(60f * dp, activeLen);
            float phase = accumPhaseSec * -9.0f * spec.Speed + spec.PhaseOffset;

            for (int s = 0; s <= samples; s++) {
                float u = s / (float)samples;
                float x = trackLeft + u * activeLen;
                float dLeft = x - trackLeft;
                float dRight = thumbX - x;

                // Envelope tapers smoothly from left end cap & thumb so ribbons blossom in the middle
                float envLeft = Math.Min(1f, dLeft / Math.Max(1f, fadeEdge));
                float envRight = Math.Min(1f, dRight / Math.Max(1f, fadeEdge));
                float env = MathF.Sin(envLeft * MathF.PI * 0.5f) * MathF.Sin(envRight * MathF.PI * 0.5f);

                float theta = dLeft * omega + phase;
                float wave1 = 0.5f + 0.5f * MathF.Sin(theta);
                float wave2 = 0.5f + 0.5f * MathF.Cos(theta * 0.87f + 0.9f);
                float amp = maxAmp * spec.HeightScale * env;

                upperY[s] = centerY - halfBase - amp * wave1;
                lowerY[s] = centerY + halfBase + amp * wave2;
            }

            wavePath.Reset();
            wavePath.MoveTo(trackLeft, upperY[0]);
            for (int s = 1; s <= samples; s++) {
                float uPrev = (s - 1) / (float)samples;
                float uCur = s / (float)samples;
                float xPrev = trackLeft + uPrev * activeLen;
                float xCur = trackLeft + uCur * activeLen;
                float xMid = (xPrev + xCur) * 0.5f;
                wavePath.QuadTo(xPrev, upperY[s - 1], xMid, (upperY[s - 1] + upperY[s]) * 0.5f);
            }
            wavePath.LineTo(thumbX, upperY[samples]);
            wavePath.LineTo(thumbX, lowerY[samples]);

            for (int s = samples - 1; s >= 0; s--) {
                float uNext = (s + 1) / (float)samples;
                float uCur = s / (float)samples;
                float xNext = trackLeft + uNext * activeLen;
                float xCur = trackLeft + uCur * activeLen;
                float xMid = (xNext + xCur) * 0.5f;
                wavePath.QuadTo(xNext, lowerY[s + 1], xMid, (lowerY[s + 1] + lowerY[s]) * 0.5f);
            }
            wavePath.Close();

            if (cachedWaveGrads != null && layerIdx < cachedWaveGrads.Length && cachedWaveGrads[layerIdx] != null) {
                shaderMatrix.Reset();
                shaderMatrix.SetScale(Math.Max(1f, activeLen), 1f);
                shaderMatrix.PostTranslate(trackLeft, centerY);
                cachedWaveGrads[layerIdx].SetLocalMatrix(shaderMatrix);
                wavePaint.SetShader(cachedWaveGrads[layerIdx]);
                canvas.DrawPath(wavePath, wavePaint);
                wavePaint.SetShader(null);
            }
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!Enabled) {
            return false;
        }

        float dp = Resources.DisplayMetrics.Density;
        float sidePad = 10f * dp;
        float trackW = Math.Max(1f, Width - sidePad * 2f);

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = e.GetX();
                downY = e.GetY();
                initialProgress = progress;
                isDragging = false;
                isCancelledToScroll = false;
                return true;

            case MotionEventActions.Move:
                if (isCancelledToScroll) {
                    return false;
                }
                float dx = Math.Abs(e.GetX() - downX);
                float dy = Math.Abs(e.GetY() - downY);
                if (!isDragging) {
                    if (dy > touchSlop && dy > dx) {
                        isCancelledToScroll = true;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        progress = initialProgress;
                        Invalidate();
                        return false;
                    }
                    if (dx > touchSlop && dx >= dy * 1.15f) {
                        isDragging = true;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        StartTrackingTouch?.Invoke();
                    } else {
                        return true;
                    }
                }
                Parent?.RequestDisallowInterceptTouchEvent(true);
                UpdateProgressFromX(e.GetX(), sidePad, trackW);
                return true;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                if (isCancelledToScroll) {
                    isCancelledToScroll = false;
                    return false;
                }
                if (!isDragging) {
                    StartTrackingTouch?.Invoke();
                }
                isDragging = false;
                UpdateProgressFromX(e.GetX(), sidePad, trackW);
                StopTrackingTouch?.Invoke();
                Invalidate();
                return true;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                if (isDragging) {
                    isDragging = false;
                    StopTrackingTouch?.Invoke();
                }
                isCancelledToScroll = false;
                Invalidate();
                return false;
        }

        return base.OnTouchEvent(e);
    }

    void UpdateProgressFromX(float x, float sidePad, float trackW) {
        float ratio = Math.Clamp((x - sidePad) / trackW, 0f, 1f);
        int next = Math.Clamp((int)Math.Round(ratio * max), 0, max);
        progress = next;
        ProgressChanged?.Invoke(progress, true);
        Invalidate();
    }
}
