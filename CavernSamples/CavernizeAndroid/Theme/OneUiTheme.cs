using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace CavernizeAndroid.Theme;

/// <summary>
/// True Samsung One UI 9 (Galaxy S25 Ultra / SESL9) design system:
/// deep blurred atmospheric backdrop, frosted glass surfaces with diagonal specular rims,
/// Samsung Recoil press physics, warm coral + One UI blue accents, Bento tiles, and 46dp fat capsule sliders.
/// </summary>
public static class OneUiTheme {
    // One UI 9 Atmospheric Dark + Frosted Glass Palette (verified from device screenshots & SystemUI/SoundAlive ARSC)
    public static readonly Color BgScreen = Color.ParseColor("#080A10");            // Deep ambient canvas base
    public static readonly Color SurfaceCard = Color.ParseColor("#C4171820");         // Frosted card surface (~77% opacity)
    public static readonly Color SurfaceElevated = Color.ParseColor("#D622232D");     // Elevated popup / active group card
    public static readonly Color SurfaceFloating = Color.ParseColor("#42FFFFFF");     // Translucent pill / capsule track (~26% white)
    public static readonly Color SurfaceSubTile = Color.ParseColor("#29FFFFFF");      // Subtle pill fill (~16% white)
    public static readonly Color SurfaceBlurTint = Color.ParseColor("#3A3A44");
    public static readonly Color ControlStroke = Color.ParseColor("#5A5B66");

    // Specular Glass Borders
    public static readonly Color GlassRimTop = Color.ParseColor("#48FFFFFF");
    public static readonly Color GlassRimMid = Color.ParseColor("#14FFFFFF");
    public static readonly Color GlassRimBottom = Color.ParseColor("#2BFFFFFF");
    public static readonly Color FloatingBorder = Color.ParseColor("#26FFFFFF");

    // Brand & Accent Tokens
    public static readonly Color Primary = Color.ParseColor("#387AFF");             // basic_token_primary_50
    public static readonly Color PrimaryHigh = Color.ParseColor("#598FFF");         // basic_token_primary_40
    public static readonly Color PrimaryPressed = Color.ParseColor("#2E65D4");      // basic_token_primary_60
    public static readonly Color CoralStart = Color.ParseColor("#DF8D7E");          // One UI 9 warm coral pill highlight (Screenshots 7/11/13)
    public static readonly Color CoralEnd = Color.ParseColor("#BA6F7D");            // One UI 9 warm rose pill gradient end
    public static readonly Color PeachText = Color.ParseColor("#E6B59C");           // Warm Samsung header accent (Screenshots 1/2/4)

    public static readonly Color TextPrimary = Color.ParseColor("#FCFCFF");         // basic_token_gray_01
    public static readonly Color TextSecondary = Color.ParseColor("#E9E9EC");       // basic_token_gray_10
    public static readonly Color TextSub = Color.ParseColor("#B7B7BB");             // sesl_text4_dark
    public static readonly Color TextMuted = Color.ParseColor("#99999E");           // sesl_text6_dark
    public static readonly Color Divider = Color.ParseColor("#26FFFFFF");           // Subtle translucent glass hairline divider

    // Functional Badge Colors (Samsung Settings / SoundAlive palette)
    public static readonly Color FunctionalGreen = Color.ParseColor("#34D399");
    public static readonly Color FunctionalOrange = Color.ParseColor("#E65B17");
    public static readonly Color FunctionalIndigo = Color.ParseColor("#7C6CF6");
    public static readonly Color FunctionalAmber = Color.ParseColor("#F59E0B");
    public static readonly Color FunctionalCyan = Color.ParseColor("#38BDF8");
    public static readonly Color FunctionalRed = Color.ParseColor("#F56962");
    public static readonly Color FunctionalRedFill = Color.ParseColor("#D93E36");

    // Room Canvas & Spatial Source Tokens
    public static readonly Color RoomBg = Color.ParseColor("#99101219");
    public static readonly Color BedChannel = Color.ParseColor("#598FFF");
    public static readonly Color LfeChannel = Color.ParseColor("#A855F7");
    public static readonly Color DynamicObject = Color.ParseColor("#F56962");
    public static readonly Color SoloGold = Color.ParseColor("#FACC15");

    // SESL9 Dimensions (dp)
    public const float CardCornerDp = 26f;
    public const float ButtonCornerDp = 18f;
    public const float TileCornerDp = 22f;
    public const float PillCornerDp = 999f;

    public static int Dp(Context ctx, float dp) =>
        (int)Math.Round(dp * ctx.Resources.DisplayMetrics.Density);

    public static Drawable CreateGlassCardDrawable(
        Context ctx,
        Color? tintOverride = null,
        float cornerDp = CardCornerDp,
        Color? borderAccent = null) {
        return new OneUiGlassDrawable(
            ctx,
            tintOverride ?? SurfaceCard,
            Dp(ctx, cornerDp),
            borderAccent);
    }

    public static GradientDrawable CreateRoundedDrawable(
        Context ctx,
        Color fillColor,
        float cornerDp = CardCornerDp,
        Color? strokeColor = null,
        float strokeDp = 0f) {
        GradientDrawable d = new();
        d.SetShape(ShapeType.Rectangle);
        d.SetColor(fillColor.ToArgb());
        d.SetCornerRadius(Dp(ctx, cornerDp));
        if (strokeColor.HasValue && strokeDp > 0f) {
            d.SetStroke(Math.Max(1, Dp(ctx, strokeDp)), ColorStateList.ValueOf(strokeColor.Value));
        }
        return d;
    }

    public static Drawable CreateRippleRoundedDrawable(
        Context ctx,
        Color fillColor,
        float cornerDp = ButtonCornerDp,
        Color? strokeColor = null,
        float strokeDp = 0f) {
        GradientDrawable content = CreateRoundedDrawable(ctx, fillColor, cornerDp, strokeColor, strokeDp);
        GradientDrawable mask = CreateRoundedDrawable(ctx, Color.White, cornerDp);
        Color ripple = Color.ParseColor("#2EFFFFFF");
        return new RippleDrawable(ColorStateList.ValueOf(ripple), content, mask);
    }

    /// <summary>
    /// Creates a floating frosted glass capsule with diagonal specular edge sheen (as seen in One UI 9 Media/Nav/Search bars).
    /// </summary>
    public static Drawable CreateFloatingGlassPill(Context ctx) {
        return new OneUiGlassDrawable(
            ctx,
            Color.Argb(228, 34, 35, 44),
            Dp(ctx, PillCornerDp),
            null,
            isFloatingCapsule: true);
    }

    /// <summary>
    /// Applies Samsung SeslRecoil tactile press scale physics (100ms down, 350ms spring release).
    /// </summary>
    public static void AttachRecoil(View v, float pressedScale = 0.96f) {
        v.Touch += (_, e) => {
            e.Handled = false;
            switch (e.Event.ActionMasked) {
                case MotionEventActions.Down:
                    v.Animate().ScaleX(pressedScale).ScaleY(pressedScale).SetDuration(100).Start();
                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    v.Animate().ScaleX(1f).ScaleY(1f).SetDuration(350).Start();
                    break;
            }
        };
    }

    /// <summary>
    /// Creates a colored circular Samsung Settings-style icon badge (e.g., green, blue, coral, indigo).
    /// </summary>
    public static TextView CreateIconBadge(Context ctx, string glyph, Color badgeColor, int sizeDp = 32) {
        int px = Dp(ctx, sizeDp);
        TextView tv = new(ctx) {
            Text = glyph,
            TextSize = sizeDp * 0.45f,
            Gravity = GravityFlags.Center
        };
        tv.SetTextColor(TextPrimary);
        tv.SetTypeface(null, TypefaceStyle.Bold);
        tv.Background = CreateRoundedDrawable(ctx, badgeColor, PillCornerDp);
        tv.LayoutParameters = new LinearLayout.LayoutParams(px, px);
        return tv;
    }

    /// <summary>
    /// Applies One UI 9 pill progress bar styling (thick capsule track with rounded gradient fill).
    /// </summary>
    public static void StyleProgressBar(Context ctx, ProgressBar bar) {
        int trackH = Dp(ctx, 14f);
        int radius = Dp(ctx, 99f);

        GradientDrawable bgTrack = new();
        bgTrack.SetShape(ShapeType.Rectangle);
        bgTrack.SetColor(Color.Argb(75, 255, 255, 255).ToArgb());
        bgTrack.SetCornerRadius(radius);

        GradientDrawable progressFill = new(
            GradientDrawable.Orientation.LeftRight,
            [CoralStart.ToArgb(), Primary.ToArgb()]);
        progressFill.SetShape(ShapeType.Rectangle);
        progressFill.SetCornerRadius(radius);

        ClipDrawable progressClip = new(progressFill, GravityFlags.Left, ClipDrawableOrientation.Horizontal);
        LayerDrawable layers = new([bgTrack, progressClip]);
        layers.SetId(0, Android.Resource.Id.Background);
        layers.SetId(1, Android.Resource.Id.Progress);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M) {
            layers.SetLayerHeight(0, trackH);
            layers.SetLayerHeight(1, trackH);
        }
        bar.ProgressDrawable = layers;
    }

    /// <summary>
    /// Applies SESL9 switch thumb/track colors with soft luminous off-track.
    /// </summary>
    public static void StyleSwitch(Switch sw) {
        int[][] states = [
            [Android.Resource.Attribute.StateChecked],
            [-Android.Resource.Attribute.StateChecked]
        ];
        sw.ThumbTintList = new ColorStateList(states, [
            TextPrimary.ToArgb(),
            TextPrimary.ToArgb()
        ]);
        sw.TrackTintList = new ColorStateList(states, [
            CoralStart.ToArgb(),
            Color.ParseColor("#52525E").ToArgb()
        ]);
        sw.SetTextColor(TextPrimary);
    }

    /// <summary>
    /// Adds a subtle translucent One UI grouped list divider.
    /// </summary>
    public static View CreateDivider(Context ctx) {
        View line = new(ctx);
        line.SetBackgroundColor(Divider);
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(ctx, 0.75f))) {
            LeftMargin = Dp(ctx, 4f),
            RightMargin = Dp(ctx, 4f),
            TopMargin = Dp(ctx, 6f),
            BottomMargin = Dp(ctx, 6f)
        };
        line.LayoutParameters = lp;
        return line;
    }
}

/// <summary>
/// Multi-layered atmospheric depth & RenderEffect-blurred radial backdrop inspired by One UI 9's Quick Panel and Settings.
/// </summary>
public sealed class OneUiBackdropView : View {
    readonly Paint basePaint = new(PaintFlags.AntiAlias);
    readonly Paint orbPaint = new(PaintFlags.AntiAlias);

    public OneUiBackdropView(Context context) : base(context) {
        basePaint.Color = OneUiTheme.BgScreen;
        basePaint.SetStyle(Paint.Style.Fill);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            SetRenderEffect(RenderEffect.CreateBlurEffect(96f, 96f, Shader.TileMode.Clamp));
        }
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        canvas.DrawRect(0, 0, w, h, basePaint);

        // Top cool indigo-blue ambient depth glow
        DrawGlow(canvas, w * 0.25f, h * 0.08f, Math.Max(w, h) * 0.55f,
            Color.Argb(95, 56, 122, 255));

        // Mid-right warm coral bokeh depth sphere (matching One UI 9 palette)
        DrawGlow(canvas, w * 0.88f, h * 0.42f, Math.Max(w, h) * 0.50f,
            Color.Argb(75, 223, 141, 126));

        // Bottom-left deep cyan-slate sphere behind floating nav capsule
        DrawGlow(canvas, w * 0.18f, h * 0.86f, Math.Max(w, h) * 0.48f,
            Color.Argb(80, 40, 95, 160));
    }

    void DrawGlow(Canvas canvas, float cx, float cy, float radius, Color centerColor) {
        using RadialGradient grad = new(
            cx, cy, Math.Max(1f, radius),
            [centerColor.ToArgb(), Color.Transparent.ToArgb()],
            [0f, 1f],
            Shader.TileMode.Clamp);
        orbPaint.SetShader(grad);
        canvas.DrawCircle(cx, cy, radius, orbPaint);
        orbPaint.SetShader(null);
    }
}

/// <summary>
/// Frosted glass container drawable with subtle inner elevation gradient and diagonal specular glass rim stroke.
/// </summary>
public sealed class OneUiGlassDrawable : Drawable {
    readonly float cornerRadiusPx;
    readonly float strokeWidthPx;
    readonly Color fillColor;
    readonly Color? accentBorder;
    readonly bool isFloatingCapsule;
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint borderPaint = new(PaintFlags.AntiAlias);

    int cachedW = -1;
    int cachedH = -1;

    public OneUiGlassDrawable(
        Context ctx,
        Color fillColor,
        float cornerRadiusPx,
        Color? accentBorder = null,
        bool isFloatingCapsule = false) {
        this.fillColor = fillColor;
        this.cornerRadiusPx = cornerRadiusPx;
        this.accentBorder = accentBorder;
        this.isFloatingCapsule = isFloatingCapsule;
        strokeWidthPx = Math.Max(1.5f, OneUiTheme.Dp(ctx, 1.1f));

        fillPaint.SetStyle(Paint.Style.Fill);
        borderPaint.SetStyle(Paint.Style.Stroke);
        borderPaint.StrokeWidth = strokeWidthPx;
    }

    public override void Draw(Canvas canvas) {
        Android.Graphics.Rect b = Bounds;
        int w = b.Width();
        int h = b.Height();
        if (w <= 0 || h <= 0) {
            return;
        }

        if (w != cachedW || h != cachedH) {
            cachedW = w;
            cachedH = h;
            UpdateShaders(b.Left, b.Top, b.Right, b.Bottom);
        }

        float halfStroke = strokeWidthPx * 0.5f;
        RectF r = new(b.Left + halfStroke, b.Top + halfStroke, b.Right - halfStroke, b.Bottom - halfStroke);
        float maxRad = Math.Min(w, h) * 0.5f;
        float rad = Math.Min(cornerRadiusPx, maxRad);

        canvas.DrawRoundRect(r, rad, rad, fillPaint);
        canvas.DrawRoundRect(r, rad, rad, borderPaint);
    }

    void UpdateShaders(float left, float top, float right, float bottom) {
        Color topFill = Color.Argb(
            fillColor.A,
            Math.Min(255, fillColor.R + 14),
            Math.Min(255, fillColor.G + 14),
            Math.Min(255, fillColor.B + 18));
        Color botFill = fillColor;

        fillPaint.SetShader(new LinearGradient(
            left, top, right, bottom,
            [topFill.ToArgb(), botFill.ToArgb()],
            [0f, 1f],
            Shader.TileMode.Clamp));

        if (accentBorder.HasValue) {
            Color c = accentBorder.Value;
            borderPaint.SetShader(new LinearGradient(
                left, top, right, bottom,
                [Color.Argb(200, c.R, c.G, c.B).ToArgb(), Color.Argb(90, c.R, c.G, c.B).ToArgb()],
                [0f, 1f],
                Shader.TileMode.Clamp));
        } else {
            Color topRim = isFloatingCapsule ? Color.Argb(95, 255, 255, 255) : OneUiTheme.GlassRimTop;
            Color midRim = OneUiTheme.GlassRimMid;
            Color botRim = isFloatingCapsule ? Color.Argb(60, 255, 255, 255) : OneUiTheme.GlassRimBottom;
            borderPaint.SetShader(new LinearGradient(
                left, top, right, bottom,
                [topRim.ToArgb(), midRim.ToArgb(), botRim.ToArgb()],
                [0f, 0.5f, 1f],
                Shader.TileMode.Clamp));
        }
    }

    public override void SetAlpha(int alpha) {
        fillPaint.Alpha = alpha;
        borderPaint.Alpha = alpha;
    }

    public override void SetColorFilter(ColorFilter colorFilter) {
        fillPaint.SetColorFilter(colorFilter);
        borderPaint.SetColorFilter(colorFilter);
    }

#pragma warning disable CS0672, CS0618, CA1422
    public override int Opacity => (int)Android.Graphics.Format.Translucent;
#pragma warning restore CS0672, CS0618, CA1422
}

/// <summary>
/// Full-capsule 44dp One UI 9 slider (matching Galaxy S25 Ultra Brightness/Volume &amp; Media capsules)
/// with gradient pill fill, embedded label &amp; value readout, Recoil press feedback, and vertical scroll protection.
/// </summary>
public sealed class OneUiSeekBar : SeekBar {
    readonly int touchSlop;
    readonly Paint trackPaint = new(PaintFlags.AntiAlias);
    readonly Paint trackRimPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint capDotPaint = new(PaintFlags.AntiAlias);
    readonly Paint labelPaint = new(PaintFlags.AntiAlias);
    readonly Paint valuePaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path clipPath = new();

    float downX;
    float downY;
    int initialProgress;
    bool isHorizontalDrag;
    bool isCancelledToScroll;
    MotionEvent pendingDown;

    public string LabelText { get; set; } = "";
    public string ValueText { get; set; } = "";
    public bool UseBlueAccent { get; set; }

    public OneUiSeekBar(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        SetBackgroundColor(Color.Transparent);
#pragma warning disable CS0618
        SetThumb(new ColorDrawable(Color.Transparent));
#pragma warning restore CS0618
        ProgressDrawable = new ColorDrawable(Color.Transparent);
        SplitTrack = false;
        SetPadding(0, 0, 0, 0);

        trackPaint.Color = Color.Argb(82, 255, 255, 255);
        trackPaint.SetStyle(Paint.Style.Fill);

        trackRimPaint.Color = Color.Argb(45, 255, 255, 255);
        trackRimPaint.StrokeWidth = Math.Max(1f, dp);
        trackRimPaint.SetStyle(Paint.Style.Stroke);

        fillPaint.SetStyle(Paint.Style.Fill);

        capDotPaint.Color = Color.Argb(220, 252, 252, 255);
        capDotPaint.SetStyle(Paint.Style.Fill);

        labelPaint.Color = OneUiTheme.TextPrimary;
        labelPaint.TextSize = 13f * dp;
        labelPaint.SetShadowLayer(3f * dp, 0f, 1f * dp, Color.Argb(140, 0, 0, 0));

        valuePaint.Color = OneUiTheme.TextPrimary;
        valuePaint.TextSize = 13f * dp;
        valuePaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        valuePaint.TextAlign = Paint.Align.Right;
        valuePaint.SetShadowLayer(3f * dp, 0f, 1f * dp, Color.Argb(140, 0, 0, 0));
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int desiredH = OneUiTheme.Dp(Context, 44f);
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(ResolveSize(w, widthMeasureSpec), ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        float dp = Resources.DisplayMetrics.Density;
        float radius = h * 0.5f;
        RectF bounds = new(0.5f, 0.5f, w - 0.5f, h - 0.5f);

        // 1. Background translucent glass track
        canvas.DrawRoundRect(bounds, radius, radius, trackPaint);

        // 2. Active capsule fill clipped to pill bounds
        float ratio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
        if (ratio > 0.001f) {
            clipPath.Reset();
            clipPath.AddRoundRect(bounds, radius, radius, Android.Graphics.Path.Direction.Cw);
            canvas.Save();
            canvas.ClipPath(clipPath);

            float fillRight = Math.Max(h * 0.65f, ratio * w);
            Color c1 = UseBlueAccent ? OneUiTheme.PrimaryHigh : OneUiTheme.CoralStart;
            Color c2 = UseBlueAccent ? OneUiTheme.Primary : OneUiTheme.CoralEnd;
            using LinearGradient fillShader = new(0, 0, w, 0, [c2.ToArgb(), c1.ToArgb()], [0f, 1f], Shader.TileMode.Clamp);
            fillPaint.SetShader(fillShader);
            canvas.DrawRoundRect(0f, 0f, fillRight, h, radius, radius, fillPaint);
            fillPaint.SetShader(null);

            // Signature bright pill cap indicator inside slider tip
            if (fillRight > 16f * dp) {
                float dotX = Math.Clamp(fillRight - radius * 0.48f, radius * 0.45f, w - radius * 0.45f);
                canvas.DrawCircle(dotX, radius, 2.8f * dp, capDotPaint);
            }
            canvas.Restore();
        }

        canvas.DrawRoundRect(bounds, radius, radius, trackRimPaint);

        // 3. Embedded label & live value centered vertically inside the capsule
        float textY = radius - (labelPaint.Ascent() + labelPaint.Descent()) * 0.5f;

        if (!string.IsNullOrEmpty(LabelText)) {
            canvas.DrawText(LabelText, 16f * dp, textY, labelPaint);
        }
        string valText = string.IsNullOrEmpty(ValueText) ? Progress.ToString() : ValueText;
        canvas.DrawText(valText, w - 16f * dp, textY, valuePaint);
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!Enabled) {
            return false;
        }

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = e.GetX();
                downY = e.GetY();
                initialProgress = Progress;
                isHorizontalDrag = false;
                isCancelledToScroll = false;
                pendingDown?.Recycle();
                pendingDown = MotionEvent.Obtain(e);
                return true;

            case MotionEventActions.Move:
                if (isCancelledToScroll) {
                    return false;
                }
                float dx = Math.Abs(e.GetX() - downX);
                float dy = Math.Abs(e.GetY() - downY);
                if (!isHorizontalDrag) {
                    if (dy > touchSlop && dy > dx) {
                        isCancelledToScroll = true;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        Progress = initialProgress;
                        pendingDown?.Recycle();
                        pendingDown = null;
                        Invalidate();
                        return false;
                    }
                    if (dx > touchSlop && dx >= dy * 1.15f) {
                        isHorizontalDrag = true;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        Animate().ScaleY(0.95f).SetDuration(100).Start();
                        if (pendingDown != null) {
                            base.OnTouchEvent(pendingDown);
                            pendingDown.Recycle();
                            pendingDown = null;
                        }
                        bool res = base.OnTouchEvent(e);
                        Invalidate();
                        return res;
                    }
                    return true;
                }
                Parent?.RequestDisallowInterceptTouchEvent(true);
                bool handled = base.OnTouchEvent(e);
                Invalidate();
                return handled;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleY(1f).SetDuration(350).Start();
                if (isCancelledToScroll) {
                    isCancelledToScroll = false;
                    return false;
                }
                if (pendingDown != null) {
                    base.OnTouchEvent(pendingDown);
                    pendingDown.Recycle();
                    pendingDown = null;
                }
                isHorizontalDrag = false;
                bool upHandled = base.OnTouchEvent(e);
                Invalidate();
                return upHandled;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleY(1f).SetDuration(350).Start();
                pendingDown?.Recycle();
                pendingDown = null;
                if (!isHorizontalDrag) {
                    isCancelledToScroll = false;
                    return false;
                }
                isHorizontalDrag = false;
                isCancelledToScroll = false;
                return base.OnTouchEvent(e);
        }

        return base.OnTouchEvent(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            pendingDown?.Recycle();
            pendingDown = null;
        }
        base.Dispose(disposing);
    }
}
