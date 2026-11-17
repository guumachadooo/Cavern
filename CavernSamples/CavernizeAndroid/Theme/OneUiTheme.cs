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
/// dynamically adapts to the device's One UI Wallpaper Color Palette (Monet API 31+) with genuine
/// Samsung Blue fallback, blurred atmospheric backdrop, frosted glass specular rims,
/// Samsung Recoil press physics, vector icon badges, and 44dp fat capsule sliders with double-tap reset.
/// </summary>
public static class OneUiTheme {
    // Dynamic Atmospheric Dark + Frosted Glass Palette (refreshed in Initialize)
    public static Color BgScreen { get; private set; } = Color.ParseColor("#080A10");
    public static Color SurfaceCard { get; private set; } = Color.ParseColor("#C4171820");
    public static Color SurfaceElevated { get; private set; } = Color.ParseColor("#D622232D");
    public static Color SurfaceFloating { get; private set; } = Color.ParseColor("#3DFFFFFF");
    public static Color SurfaceSubTile { get; private set; } = Color.ParseColor("#29FFFFFF");
    public static Color SurfaceBlurTint { get; private set; } = Color.ParseColor("#3A3A44");
    public static Color ControlStroke { get; private set; } = Color.ParseColor("#5A5B66");

    // Specular Glass Borders
    public static Color GlassRimTop { get; private set; } = Color.ParseColor("#48FFFFFF");
    public static Color GlassRimMid { get; private set; } = Color.ParseColor("#14FFFFFF");
    public static Color GlassRimBottom { get; private set; } = Color.ParseColor("#2BFFFFFF");
    public static Color FloatingBorder { get; private set; } = Color.ParseColor("#26FFFFFF");

    // Dynamic Brand & Wallpaper Accent Tokens (default: Samsung One UI Blue #387AFF)
    public static Color Primary { get; private set; } = Color.ParseColor("#387AFF");
    public static Color PrimaryHigh { get; private set; } = Color.ParseColor("#598FFF");
    public static Color PrimaryPressed { get; private set; } = Color.ParseColor("#2E65D4");
    public static Color Secondary { get; private set; } = Color.ParseColor("#7C6CF6");
    public static Color CoralStart { get; private set; } = Color.ParseColor("#598FFF");
    public static Color CoralEnd { get; private set; } = Color.ParseColor("#2E65D4");
    public static Color PeachText { get; private set; } = Color.ParseColor("#C2D8FF");

    public static Color TextPrimary { get; private set; } = Color.ParseColor("#FCFCFF");
    public static Color TextSecondary { get; private set; } = Color.ParseColor("#E9E9EC");
    public static Color TextSub { get; private set; } = Color.ParseColor("#B7B7BB");
    public static Color TextMuted { get; private set; } = Color.ParseColor("#99999E");
    public static Color Divider { get; private set; } = Color.ParseColor("#26FFFFFF");

    // Functional Semantic Colors (harmonized to avoid clashing while staying clear)
    public static readonly Color FunctionalGreen = Color.ParseColor("#34D399");
    public static readonly Color FunctionalOrange = Color.ParseColor("#F97316");
    public static readonly Color FunctionalIndigo = Color.ParseColor("#7C6CF6");
    public static readonly Color FunctionalAmber = Color.ParseColor("#F59E0B");
    public static readonly Color FunctionalCyan = Color.ParseColor("#38BDF8");
    public static readonly Color FunctionalRed = Color.ParseColor("#F56962");
    public static readonly Color FunctionalRedFill = Color.ParseColor("#D93E36");

    // Room Canvas & Spatial Source Tokens
    public static Color RoomBg { get; private set; } = Color.ParseColor("#99101219");
    public static Color BedChannel { get; private set; } = Color.ParseColor("#598FFF");
    public static readonly Color LfeChannel = Color.ParseColor("#A855F7");
    public static readonly Color DynamicObject = Color.ParseColor("#F56962");
    public static readonly Color SoloGold = Color.ParseColor("#FACC15");

    public static bool IsDynamicWallpaperActive { get; private set; }

    // SESL9 Dimensions (dp)
    public const float CardCornerDp = 26f;
    public const float ButtonCornerDp = 18f;
    public const float TileCornerDp = 22f;
    public const float PillCornerDp = 999f;

    /// <summary>
    /// Reads Samsung One UI / Android Material You dynamic wallpaper colors from <paramref name="ctx"/>
    /// (API 31+) with clean Samsung One UI 9 Blue as fallback.
    /// </summary>
    public static void Initialize(Context ctx) {
        Color defaultBlue = Color.ParseColor("#387AFF");
        Color defaultBlueHigh = Color.ParseColor("#598FFF");
        Color defaultBluePressed = Color.ParseColor("#2E65D4");
        Color defaultHeaderTint = Color.ParseColor("#D6E4FF");
        // Native One UI 9 Dark Mode neutral surfaces (clean dark glass like Settings & Voice Recorder)
        Color defaultBaseBg = Color.Rgb(8, 8, 11);
        Color defaultCard = Color.Argb(215, 22, 22, 27);
        Color defaultElevated = Color.Argb(232, 32, 33, 40);
        Color defaultRoomBg = Color.Argb(185, 13, 14, 19);

        Primary = defaultBlue;
        PrimaryHigh = defaultBlueHigh;
        PrimaryPressed = defaultBluePressed;
        Secondary = Color.ParseColor("#7C6CF6");
        CoralStart = defaultBlueHigh;
        CoralEnd = defaultBluePressed;
        PeachText = defaultHeaderTint;
        BedChannel = defaultBlueHigh;
        BgScreen = defaultBaseBg;
        SurfaceCard = defaultCard;
        SurfaceElevated = defaultElevated;
        RoomBg = defaultRoomBg;
        IsDynamicWallpaperActive = false;

        if (ctx == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return;
        }

        try {
            Color? sysA1_400 = TryGetFrameworkColor(ctx, "system_accent1_400");
            Color? sysA1_300 = TryGetFrameworkColor(ctx, "system_accent1_300");
            Color? sysA1_500 = TryGetFrameworkColor(ctx, "system_accent1_500");
            Color? sysA1_200 = TryGetFrameworkColor(ctx, "system_accent1_200");

            if (sysA1_400.HasValue && IsChromatic(sysA1_400.Value)) {
                IsDynamicWallpaperActive = true;
                Color baseAccent = RefineAccent(sysA1_400.Value);
                Color highAccent = sysA1_300.HasValue ? RefineAccent(sysA1_300.Value) : AdjustBrightness(baseAccent, 1.18f);
                Color lowAccent = sysA1_500 ?? AdjustBrightness(baseAccent, 0.78f);

                Primary = baseAccent;
                PrimaryHigh = highAccent;
                PrimaryPressed = lowAccent;
                Color? sysA2_400 = TryGetFrameworkColor(ctx, "system_accent2_400");
                Secondary = sysA2_400.HasValue && IsChromatic(sysA2_400.Value)
                    ? RefineAccent(sysA2_400.Value)
                    : Blend(highAccent, Color.Rgb(142, 130, 248), 0.45f);
                CoralStart = highAccent;
                CoralEnd = lowAccent;
                PeachText = Blend(Color.Rgb(242, 243, 249), sysA1_200 ?? highAccent, 0.36f);
                BedChannel = Blend(highAccent, Color.Rgb(108, 178, 255), 0.32f);

                // Subtle 3% accent whisper on elevated cards only; backgrounds stay clean neutral dark
                SurfaceCard = WithAlpha(Blend(Color.Rgb(22, 22, 27), baseAccent, 0.03f), 218);
                SurfaceElevated = WithAlpha(Blend(Color.Rgb(32, 33, 40), baseAccent, 0.04f), 235);
            }
        } catch {
            // Fall back cleanly to Samsung One UI 9 Blue palette
        }
    }

    static Color RefineAccent(Color c) {
        float[] hsv = new float[3];
        Color.ColorToHSV(c, hsv);
        hsv[1] = Math.Clamp(hsv[1] * 0.90f, 0.25f, 0.78f);
        hsv[2] = Math.Clamp(hsv[2], 0.62f, 0.95f);
        return new Color(Color.HSVToColor(255, hsv));
    }

    static Color? TryGetFrameworkColor(Context ctx, string name) {
        int id = ctx.Resources?.GetIdentifier(name, "color", "android") ?? 0;
        if (id == 0) {
            return null;
        }
        int argb = ctx.GetColor(id);
        return new Color(argb);
    }

    static bool IsChromatic(Color c) {
        float[] hsv = new float[3];
        Color.ColorToHSV(c, hsv);
        return hsv[1] >= 0.08f && hsv[2] >= 0.15f;
    }

    static Color AdjustBrightness(Color c, float factor) {
        float[] hsv = new float[3];
        Color.ColorToHSV(c, hsv);
        hsv[2] = Math.Clamp(hsv[2] * factor, 0f, 1f);
        if (factor > 1.05f) {
            hsv[1] = Math.Clamp(hsv[1] * 0.82f, 0f, 1f);
        }
        int rgb = Color.HSVToColor(c.A, hsv);
        return new Color(rgb);
    }

    static Color Blend(Color a, Color b, float t) {
        t = Math.Clamp(t, 0f, 1f);
        return Color.Argb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.Argb(alpha, c.R, c.G, c.B);

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
    /// Creates a floating frosted glass capsule with diagonal specular edge sheen (One UI 9 Media/Nav/Search bars).
    /// </summary>
    public static Drawable CreateFloatingGlassPill(Context ctx) {
        Color tint = WithAlpha(SurfaceElevated, 240);
        return new OneUiGlassDrawable(
            ctx,
            tint,
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
    /// Creates a colored circular/squircle Samsung Settings-style crisp vector icon badge.
    /// </summary>
    public static Views.OneUiIconBadge CreateVectorIconBadge(
        Context ctx,
        Views.OneUiIconType iconType,
        Color badgeColor,
        int sizeDp = 32) {
        int px = Dp(ctx, sizeDp);
        Views.OneUiIconBadge badge = new(ctx, iconType, badgeColor) {
            LayoutParameters = new LinearLayout.LayoutParams(px, px)
        };
        return badge;
    }

    /// <summary>
    /// Applies One UI 9 pill progress bar styling (thick capsule track with dynamic gradient fill).
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
            [Primary.ToArgb(), PrimaryHigh.ToArgb()]);
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
    /// Applies SESL9 switch thumb/track colors using the active dynamic accent.
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
            Primary.ToArgb(),
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
/// Multi-layered atmospheric depth &amp; RenderEffect-blurred radial backdrop inspired by One UI 9's Quick Panel and Settings,
/// reacting dynamically to the active wallpaper palette colors.
/// </summary>
public sealed class OneUiBackdropView : View {
    readonly Paint basePaint = new(PaintFlags.AntiAlias);
    readonly Paint orbPaint = new(PaintFlags.AntiAlias);
    Color? mediaPrimary;
    Color? mediaSecondary;

    public OneUiBackdropView(Context context) : base(context) {
        basePaint.Color = OneUiTheme.BgScreen;
        basePaint.SetStyle(Paint.Style.Fill);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            SetRenderEffect(RenderEffect.CreateBlurEffect(96f, 96f, Shader.TileMode.Clamp));
        }
    }

    public void SetMediaAccent(Color? primary, Color? secondary) {
        mediaPrimary = primary;
        mediaSecondary = secondary;
        PostInvalidateOnAnimation();
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        basePaint.Color = OneUiTheme.BgScreen;
        canvas.DrawRect(0, 0, w, h, basePaint);

        Color p1 = mediaPrimary ?? OneUiTheme.Primary;
        Color p2 = mediaSecondary ?? OneUiTheme.CoralStart;
        Color p3 = mediaPrimary ?? OneUiTheme.PrimaryPressed;
        int alpha1 = mediaPrimary.HasValue ? 46 : 22;
        int alpha2 = mediaSecondary.HasValue ? 34 : 16;
        int alpha3 = mediaPrimary.HasValue ? 28 : 18;

        // Top ambient wallpaper / cover art depth glow
        DrawGlow(canvas, w * 0.30f, h * 0.12f, Math.Max(w, h) * 0.58f,
            Color.Argb(alpha1, p1.R, p1.G, p1.B));

        // Mid-right secondary harmonic bokeh sphere
        DrawGlow(canvas, w * 0.86f, h * 0.44f, Math.Max(w, h) * 0.50f,
            Color.Argb(alpha2, p2.R, p2.G, p2.B));

        // Bottom-left deep ambient sphere behind floating nav capsule
        DrawGlow(canvas, w * 0.22f, h * 0.86f, Math.Max(w, h) * 0.48f,
            Color.Argb(alpha3, p3.R, p3.G, p3.B));
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
/// Frosted glass capsule container that samples and blurs the content behind itself in real time
/// (matching One UI 9 bottom navigation &amp; media control pills) while keeping foreground children razor-sharp.
/// </summary>
public sealed class OneUiFrostedCapsuleView : FrameLayout {
    readonly Paint frostOverlayPaint = new(PaintFlags.AntiAlias);
    readonly Paint rimPaint = new(PaintFlags.AntiAlias);
    readonly Paint bitmapPaint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Android.Graphics.Path clipPath = new();
    readonly int[] selfLoc = new int[2];
    readonly int[] targetLoc = new int[2];

    Bitmap blurBitmap;
    int[] pixelScratch;
    Canvas captureCanvas;
    bool isCapturing;
    long lastCaptureMs;

    public Func<View[]> BehindViewsProvider { get; set; }
    public float CornerRadiusDp { get; set; } = 30f;

    public OneUiFrostedCapsuleView(Context context) : base(context) {
        SetWillNotDraw(false);
        frostOverlayPaint.SetStyle(Paint.Style.Fill);
        rimPaint.SetStyle(Paint.Style.Stroke);
        rimPaint.StrokeWidth = Math.Max(1.5f, OneUiTheme.Dp(context, 1.0f));
    }

    public void RefreshBlur(bool force = false) {
        if (isCapturing || Width <= 0 || Height <= 0 || Visibility != ViewStates.Visible) {
            return;
        }
        long now = SystemClock.UptimeMillis();
        if (!force && now - lastCaptureMs < 24) {
            return;
        }
        CaptureBehindRegion();
        PostInvalidateOnAnimation();
    }

    void CaptureBehindRegion() {
        View[] behind = BehindViewsProvider?.Invoke();
        if (behind == null || behind.Length == 0) {
            return;
        }

        const int downscale = 10;
        int bw = Math.Clamp(Width / downscale, 16, 160);
        int bh = Math.Clamp(Height / downscale, 8, 48);

        if (blurBitmap == null || blurBitmap.IsRecycled || blurBitmap.Width != bw || blurBitmap.Height != bh) {
            captureCanvas?.Dispose();
            blurBitmap?.Recycle();
            blurBitmap?.Dispose();
            blurBitmap = Bitmap.CreateBitmap(bw, bh, Bitmap.Config.Argb8888!);
            captureCanvas = new Canvas(blurBitmap);
            pixelScratch = new int[bw * bh];
        }

        isCapturing = true;
        try {
            lastCaptureMs = SystemClock.UptimeMillis();
            blurBitmap.EraseColor(OneUiTheme.BgScreen.ToArgb());
            GetLocationInWindow(selfLoc);

            float sx = bw / (float)Width;
            float sy = bh / (float)Height;

            foreach (View v in behind) {
                if (v == null || v.Visibility != ViewStates.Visible || v.Width <= 0 || v.Height <= 0) {
                    continue;
                }
                v.GetLocationInWindow(targetLoc);
                int save = captureCanvas.Save();
                captureCanvas.Scale(sx, sy);
                captureCanvas.Translate(targetLoc[0] - selfLoc[0], targetLoc[1] - selfLoc[1]);
                v.Draw(captureCanvas);
                captureCanvas.RestoreToCount(save);
            }

            FastBoxBlur(blurBitmap, pixelScratch, 3);
            FastBoxBlur(blurBitmap, pixelScratch, 2);
        } catch {
            // Keep smooth frosted surface fallback if any child cannot draw offscreen
        } finally {
            isCapturing = false;
        }
    }

    protected override void OnDraw(Canvas canvas) {
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            base.OnDraw(canvas);
            return;
        }

        if (blurBitmap == null) {
            CaptureBehindRegion();
        }

        float rad = Math.Min(Math.Min(w, h) * 0.5f, OneUiTheme.Dp(Context, CornerRadiusDp));
        float halfStroke = rimPaint.StrokeWidth * 0.5f;
        RectF bounds = new(halfStroke, halfStroke, w - halfStroke, h - halfStroke);

        clipPath.Reset();
        clipPath.AddRoundRect(bounds, rad, rad, Android.Graphics.Path.Direction.Cw);

        int save = canvas.Save();
        canvas.ClipPath(clipPath);

        if (blurBitmap != null && !blurBitmap.IsRecycled) {
            canvas.DrawBitmap(blurBitmap, null, new RectF(0f, 0f, w, h), bitmapPaint);
        }

        // Translucent smoked One UI 9 frosted glaze + top subtle reflection highlight
        using LinearGradient glaze = new(
            0f, 0f, 0f, h,
            [Color.Argb(204, 34, 36, 44).ToArgb(), Color.Argb(228, 16, 17, 22).ToArgb()],
            [0f, 1f],
            Shader.TileMode.Clamp!);
        frostOverlayPaint.SetShader(glaze);
        canvas.DrawRect(0f, 0f, w, h, frostOverlayPaint);
        frostOverlayPaint.SetShader(null);
        canvas.RestoreToCount(save);

        using LinearGradient rimGrad = new(
            0f, 0f, 0f, h,
            [Color.Argb(105, 255, 255, 255).ToArgb(), Color.Argb(22, 255, 255, 255).ToArgb(), Color.Argb(58, 255, 255, 255).ToArgb()],
            [0f, 0.45f, 1f],
            Shader.TileMode.Clamp!);
        rimPaint.SetShader(rimGrad);
        canvas.DrawRoundRect(bounds, rad, rad, rimPaint);
        rimPaint.SetShader(null);

        base.OnDraw(canvas);
    }

    static void FastBoxBlur(Bitmap bmp, int[] pixels, int radius) {
        int w = bmp.Width;
        int h = bmp.Height;
        if (w < 3 || h < 3 || radius <= 0 || pixels == null || pixels.Length < w * h) {
            return;
        }
        bmp.GetPixels(pixels, 0, w, 0, 0, w, h);
        int window = radius * 2 + 1;

        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int x = 0; x < w; x++) {
                int rSum = 0, gSum = 0, bSum = 0;
                for (int k = -radius; k <= radius; k++) {
                    int sx = Math.Clamp(x + k, 0, w - 1);
                    int c = pixels[row + sx];
                    rSum += (c >> 16) & 0xFF;
                    gSum += (c >> 8) & 0xFF;
                    bSum += c & 0xFF;
                }
                pixels[row + x] = unchecked((int)0xFF000000) | ((rSum / window) << 16) | ((gSum / window) << 8) | (bSum / window);
            }
        }

        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                int rSum = 0, gSum = 0, bSum = 0;
                for (int k = -radius; k <= radius; k++) {
                    int sy = Math.Clamp(y + k, 0, h - 1);
                    int c = pixels[sy * w + x];
                    rSum += (c >> 16) & 0xFF;
                    gSum += (c >> 8) & 0xFF;
                    bSum += c & 0xFF;
                }
                pixels[y * w + x] = unchecked((int)0xFF000000) | ((rSum / window) << 16) | ((gSum / window) << 8) | (bSum / window);
            }
        }
        bmp.SetPixels(pixels, 0, w, 0, 0, w, h);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            captureCanvas?.Dispose();
            captureCanvas = null;
            blurBitmap?.Recycle();
            blurBitmap?.Dispose();
            blurBitmap = null;
        }
        base.Dispose(disposing);
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
/// Full-capsule 44dp One UI 9 slider (matching SystemUI Brightness &amp; SoundCraft Volume seekbars):
/// continuous pixel-smooth float tracking (zero stepping jitter), blooming circular touch "ball" thumb
/// with spring animation on press/drag, embedded label &amp; value readout, double-tap reset, and scroll safety.
/// </summary>
public sealed class OneUiSeekBar : SeekBar {
    readonly int touchSlop;
    readonly Paint trackPaint = new(PaintFlags.AntiAlias);
    readonly Paint trackRimPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbHaloPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbBallPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbBallRimPaint = new(PaintFlags.AntiAlias);
    readonly Paint labelPaint = new(PaintFlags.AntiAlias);
    readonly Paint valuePaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path clipPath = new();

    float downX;
    float downY;
    float maxMoveDist;
    int initialProgress;
    bool isHorizontalDrag;
    bool isTouchDown;
    bool isCancelledToScroll;
    MotionEvent pendingDown;
    DateTime lastTapTime = DateTime.MinValue;

    float renderedRatio;
    float touchRatio;
    float thumbExpansion;
    bool hasInitializedRatio;
    long lastFrameMs;

    public string LabelText { get; set; } = "";
    public string ValueText { get; set; } = "";
    public bool UseBlueAccent { get; set; }
    public int DefaultProgress { get; set; }
    public bool HasDefaultReset { get; set; } = true;

    public event Action<int> ResetRequested;

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

        trackPaint.Color = Color.Argb(58, 255, 255, 255);
        trackPaint.SetStyle(Paint.Style.Fill);

        trackRimPaint.Color = Color.Argb(36, 255, 255, 255);
        trackRimPaint.StrokeWidth = Math.Max(1f, dp);
        trackRimPaint.SetStyle(Paint.Style.Stroke);

        fillPaint.SetStyle(Paint.Style.Fill);

        thumbHaloPaint.SetStyle(Paint.Style.Fill);
        thumbBallPaint.SetStyle(Paint.Style.Fill);
        thumbBallRimPaint.SetStyle(Paint.Style.Stroke);
        thumbBallRimPaint.StrokeWidth = Math.Max(1.2f, 1.2f * dp);

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

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) {
            lastFrameMs = nowMs;
        }
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0.001f, 0.05f);
        lastFrameMs = nowMs;

        float discreteRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
        float targetRatio = isHorizontalDrag ? touchRatio : discreteRatio;
        if (!hasInitializedRatio) {
            renderedRatio = targetRatio;
            hasInitializedRatio = true;
        } else {
            float ratioSpeed = isHorizontalDrag ? 30f : 16f;
            renderedRatio += (targetRatio - renderedRatio) * Math.Min(1f, dt * ratioSpeed);
            if (MathF.Abs(targetRatio - renderedRatio) < 0.0006f) {
                renderedRatio = targetRatio;
            }
        }

        float targetThumb = isTouchDown ? 1f : 0f;
        float thumbSpeed = isTouchDown ? 18f : 12f;
        thumbExpansion += (targetThumb - thumbExpansion) * Math.Min(1f, dt * thumbSpeed);
        if (MathF.Abs(targetThumb - thumbExpansion) < 0.004f) {
            thumbExpansion = targetThumb;
        }

        float dp = Resources.DisplayMetrics.Density;
        float radius = h * 0.5f;
        RectF bounds = new(0.5f, 0.5f, w - 0.5f, h - 0.5f);

        // 1. Background translucent neutral glass track
        canvas.DrawRoundRect(bounds, radius, radius, trackPaint);

        // 2. Active capsule fill + SoundCraft One UI 9 internal pearl thumb + shock tip highlight
        if (renderedRatio > 0.001f || thumbExpansion > 0.01f) {
            clipPath.Reset();
            clipPath.AddRoundRect(bounds, radius, radius, Android.Graphics.Path.Direction.Cw);
            canvas.Save();
            canvas.ClipPath(clipPath);

            float minVisibleRight = renderedRatio > 0.002f ? h * 0.55f : 0f;
            float fillRight = Math.Clamp(renderedRatio * w, minVisibleRight, w);
            Color c1 = UseBlueAccent ? OneUiTheme.PrimaryHigh : OneUiTheme.CoralStart;
            Color c2 = UseBlueAccent ? OneUiTheme.PrimaryPressed : OneUiTheme.CoralEnd;
            // Brighten tip slightly during touch (One UI 9 shock value transition)
            Color tipColor = Color.Argb(
                255,
                Math.Min(255, (int)(c1.R + (255 - c1.R) * 0.24f * thumbExpansion)),
                Math.Min(255, (int)(c1.G + (255 - c1.G) * 0.24f * thumbExpansion)),
                Math.Min(255, (int)(c1.B + (255 - c1.B) * 0.24f * thumbExpansion)));

            if (fillRight > 1f) {
                using LinearGradient fillShader = new(0, 0, Math.Max(h, fillRight), 0, [c2.ToArgb(), tipColor.ToArgb()], [0f, 1f], Shader.TileMode.Clamp);
                fillPaint.SetShader(fillShader);
                canvas.DrawRoundRect(0f, 0f, fillRight, h, radius, radius, fillPaint);
                fillPaint.SetShader(null);
            }

            // Signature One UI 9 touch sphere fades gently inside the pill tip ONLY on touch/drag (zero idle dot clutter)
            if (thumbExpansion > 0.01f && fillRight > h * 0.35f) {
                float ballX = Math.Clamp(fillRight - radius * 0.54f, radius * 0.54f, w - radius * 0.54f);
                float ballY = radius;
                float ballR = radius * (0.44f + 0.14f * thumbExpansion);
                float haloR = ballR * 1.45f;

                using RadialGradient haloGrad = new(
                    ballX, ballY, haloR,
                    [Color.Argb((int)(54 * thumbExpansion), 255, 255, 255).ToArgb(), Color.Transparent.ToArgb()],
                    [0f, 1f],
                    Shader.TileMode.Clamp);
                thumbHaloPaint.SetShader(haloGrad);
                canvas.DrawCircle(ballX, ballY, haloR, thumbHaloPaint);
                thumbHaloPaint.SetShader(null);

                thumbBallPaint.Color = Color.Argb((int)(142 * thumbExpansion), 255, 255, 255);
                canvas.DrawCircle(ballX, ballY, ballR, thumbBallPaint);
            }
            canvas.Restore();
        }

        // Subtle tick notch indicating default reset point
        if (HasDefaultReset && Max > 0 && DefaultProgress > 0 && DefaultProgress < Max) {
            float defX = DefaultProgress / (float)Max * w;
            canvas.DrawLine(defX, h - 5.5f * dp, defX, h - 1.5f * dp, trackRimPaint);
        }

        canvas.DrawRoundRect(bounds, radius, radius, trackRimPaint);

        // 3. Embedded label & live value centered vertically inside the capsule (always crisp above fill & ball)
        float textY = radius - (labelPaint.Ascent() + labelPaint.Descent()) * 0.5f;
        string valText = string.IsNullOrEmpty(ValueText) ? Progress.ToString() : ValueText;
        float valW = valuePaint.MeasureText(valText);

        if (!string.IsNullOrEmpty(LabelText)) {
            float maxLabelW = Math.Max(40f * dp, w - 38f * dp - valW);
            canvas.Save();
            canvas.ClipRect(14f * dp, 0f, 14f * dp + maxLabelW, h);
            canvas.DrawText(LabelText, 16f * dp, textY, labelPaint);
            canvas.Restore();
        }
        canvas.DrawText(valText, w - 16f * dp, textY, valuePaint);

        bool needsNextFrame =
            MathF.Abs(targetRatio - renderedRatio) > 0.0006f ||
            MathF.Abs(targetThumb - thumbExpansion) > 0.004f;
        if (needsNextFrame) {
            PostInvalidateOnAnimation();
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!Enabled) {
            return false;
        }

        float w = Math.Max(1f, Width);

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = e.GetX();
                downY = e.GetY();
                maxMoveDist = 0f;
                initialProgress = Progress;
                isHorizontalDrag = false;
                isTouchDown = true;
                isCancelledToScroll = false;
                touchRatio = Math.Clamp(downX / w, 0f, 1f);
                lastFrameMs = SystemClock.UptimeMillis();
                pendingDown?.Recycle();
                pendingDown = MotionEvent.Obtain(e);
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Move:
                if (isCancelledToScroll) {
                    return false;
                }
                float curX = e.GetX();
                float curY = e.GetY();
                float dx = Math.Abs(curX - downX);
                float dy = Math.Abs(curY - downY);
                float moveDist = MathF.Sqrt(dx * dx + dy * dy);
                if (moveDist > maxMoveDist) {
                    maxMoveDist = moveDist;
                }
                touchRatio = Math.Clamp(curX / w, 0f, 1f);

                if (!isHorizontalDrag) {
                    if (dy > touchSlop && dy > dx) {
                        isCancelledToScroll = true;
                        isTouchDown = false;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        Progress = initialProgress;
                        pendingDown?.Recycle();
                        pendingDown = null;
                        PostInvalidateOnAnimation();
                        return false;
                    }
                    if (dx > touchSlop * 0.55f && dx >= dy * 1.1f) {
                        isHorizontalDrag = true;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        Animate().ScaleX(1.008f).ScaleY(1.032f).SetDuration(120).Start();
                        if (pendingDown != null) {
                            base.OnTouchEvent(pendingDown);
                            pendingDown.Recycle();
                            pendingDown = null;
                        }
                        bool res = base.OnTouchEvent(e);
                        PostInvalidateOnAnimation();
                        return res;
                    }
                    PostInvalidateOnAnimation();
                    return true;
                }
                Parent?.RequestDisallowInterceptTouchEvent(true);
                bool handled = base.OnTouchEvent(e);
                PostInvalidateOnAnimation();
                return handled;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleX(1f).ScaleY(1f).SetDuration(280).Start();
                isTouchDown = false;
                if (isCancelledToScroll) {
                    isCancelledToScroll = false;
                    PostInvalidateOnAnimation();
                    return false;
                }

                bool wasTap = !isHorizontalDrag && maxMoveDist < touchSlop;
                if (wasTap && HasDefaultReset) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTapTime).TotalMilliseconds <= 320) {
                        lastTapTime = DateTime.MinValue;
                        pendingDown?.Recycle();
                        pendingDown = null;
                        isHorizontalDrag = false;
                        int target = Math.Clamp(DefaultProgress, 0, Max);
                        Progress = target;
                        ResetRequested?.Invoke(target);
                        Animate().ScaleX(0.99f).ScaleY(0.95f).SetDuration(80).WithEndAction(
                            new Java.Lang.Runnable(() => Animate().ScaleX(1f).ScaleY(1f).SetDuration(240).Start())).Start();
                        PostInvalidateOnAnimation();
                        return true;
                    }
                    lastTapTime = now;
                }

                if (pendingDown != null) {
                    base.OnTouchEvent(pendingDown);
                    pendingDown.Recycle();
                    pendingDown = null;
                }
                isHorizontalDrag = false;
                bool upHandled = base.OnTouchEvent(e);
                touchRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
                PostInvalidateOnAnimation();
                return upHandled;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleX(1f).ScaleY(1f).SetDuration(280).Start();
                isTouchDown = false;
                pendingDown?.Recycle();
                pendingDown = null;
                if (!isHorizontalDrag) {
                    isCancelledToScroll = false;
                    PostInvalidateOnAnimation();
                    return false;
                }
                isHorizontalDrag = false;
                isCancelledToScroll = false;
                PostInvalidateOnAnimation();
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
