using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace CavernizeAndroid.Theme;

/// <summary>
/// Official Samsung One UI 9 (SESL9) dark mode design tokens, drawable factories, and scroll-safe controls.
/// Tokens derived from Samsung's SESL9 values (basic_token_* and sesl_*).
/// </summary>
public static class OneUiTheme {
    // SESL9 Basic Dark Mode Color Tokens
    public static readonly Color BgScreen = Color.ParseColor("#010102");            // basic_token_gray_100 / sesl_round_and_dialog_background_dark
    public static readonly Color SurfaceCard = Color.ParseColor("#17171A");         // basic_token_gray_95 / sesl_background_color_dark
    public static readonly Color SurfaceElevated = Color.ParseColor("#252528");     // basic_token_gray_90 / sesl_surface_bright_dark
    public static readonly Color SurfaceFloating = Color.ParseColor("#2D2D30");     // sesl_gray_D4
    public static readonly Color SurfaceBlurTint = Color.ParseColor("#3A3A3D");     // basic_token_gray_80 / sesl_list_divider_color_dark
    public static readonly Color ControlStroke = Color.ParseColor("#4D4D52");       // basic_token_gray_70
    public static readonly Color FloatingBorder = Color.ParseColor("#1FFFFFFF");    // frosted glass hairline edge

    public static readonly Color Primary = Color.ParseColor("#387AFF");             // basic_token_primary_50 / sesl_blue_color_light
    public static readonly Color PrimaryHigh = Color.ParseColor("#598FFF");         // basic_token_primary_40
    public static readonly Color PrimaryPressed = Color.ParseColor("#2E65D4");      // basic_token_primary_60
    public static readonly Color PrimarySoft = Color.ParseColor("#29387AFF");       // active tint pill

    public static readonly Color TextPrimary = Color.ParseColor("#FCFCFF");         // basic_token_gray_01 / sesl_text1_dark
    public static readonly Color TextSecondary = Color.ParseColor("#E9E9EC");       // basic_token_gray_10 / sesl_text2_dark
    public static readonly Color TextSub = Color.ParseColor("#B7B7BB");             // sesl_text4_dark
    public static readonly Color TextMuted = Color.ParseColor("#99999E");           // sesl_text6_dark
    public static readonly Color Divider = Color.ParseColor("#2A2A2E");             // sesl_list_divider_color_dark

    public static readonly Color FunctionalGreen = Color.ParseColor("#58DB9C");     // sesl_functional_green_text_dark
    public static readonly Color FunctionalOrange = Color.ParseColor("#E65B17");    // sesl_badge_background_color
    public static readonly Color FunctionalRed = Color.ParseColor("#F56962");       // basic_token_red_30 / sesl_functional_red_text_dark
    public static readonly Color FunctionalRedFill = Color.ParseColor("#D93E36");   // basic_token_red_50

    // Room Canvas & Spatial Source Tokens
    public static readonly Color RoomBg = Color.ParseColor("#101013");              // sesl_gray_D2
    public static readonly Color BedChannel = Color.ParseColor("#598FFF");
    public static readonly Color LfeChannel = Color.ParseColor("#A855F7");
    public static readonly Color DynamicObject = Color.ParseColor("#F56962");
    public static readonly Color SoloGold = Color.ParseColor("#FACC15");

    // SESL9 Dimensions (dp)
    public const float CardCornerDp = 26f;      // sesl_rounded_corner_radius / sesl_dialog_background_corner_radius
    public const float ButtonCornerDp = 18f;    // sesl_button_corner_radius
    public const float SmallCornerDp = 16f;     // sesl_button_small_corner_radius
    public const float PillCornerDp = 999f;     // sesl_figma_floating_component_background_radius

    public static int Dp(Context ctx, float dp) =>
        (int)Math.Round(dp * ctx.Resources.DisplayMetrics.Density);

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
            d.SetStroke(Math.Max(1, Dp(ctx, strokeDp)), strokeColor.Value.ToArgb());
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
        Color ripple = Color.ParseColor("#33FFFFFF");
        return new RippleDrawable(ColorStateList.ValueOf(ripple), content, mask);
    }

    /// <summary>
    /// Creates the SESL9 floating frosted-style bottom navigation pill background.
    /// </summary>
    public static Drawable CreateFloatingNavBackground(Context ctx) {
        GradientDrawable d = CreateRoundedDrawable(
            ctx,
            Color.Argb(240, SurfaceElevated.R, SurfaceElevated.G, SurfaceElevated.B),
            PillCornerDp,
            FloatingBorder,
            1f);
        return d;
    }

    /// <summary>
    /// Styles a SeekBar as a thick SESL9 pill track slider (12dp rounded track + circular thumb).
    /// </summary>
    public static void StyleSeekBar(Context ctx, SeekBar bar) {
        int trackH = Dp(ctx, 11f);
        int radius = Dp(ctx, 99f);

        GradientDrawable bgTrack = new();
        bgTrack.SetShape(ShapeType.Rectangle);
        bgTrack.SetColor(SurfaceFloating.ToArgb());
        bgTrack.SetCornerRadius(radius);
        bgTrack.SetSize(-1, trackH);

        GradientDrawable progressFill = new();
        progressFill.SetShape(ShapeType.Rectangle);
        progressFill.SetColor(Primary.ToArgb());
        progressFill.SetCornerRadius(radius);
        progressFill.SetSize(-1, trackH);

        ClipDrawable progressClip = new(progressFill, GravityFlags.Left, ClipDrawableOrientation.Horizontal);
        LayerDrawable layers = new([bgTrack, progressClip]);
        layers.SetId(0, Android.Resource.Id.Background);
        layers.SetId(1, Android.Resource.Id.Progress);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M) {
            layers.SetLayerHeight(0, trackH);
            layers.SetLayerHeight(1, trackH);
            layers.SetLayerGravity(0, GravityFlags.CenterVertical);
            layers.SetLayerGravity(1, GravityFlags.CenterVertical);
        }
        bar.ProgressDrawable = layers;

        int thumbSize = Dp(ctx, 20f);
        GradientDrawable thumb = new();
        thumb.SetShape(ShapeType.Oval);
        thumb.SetColor(TextPrimary.ToArgb());
        thumb.SetStroke(Dp(ctx, 2.5f), Primary.ToArgb());
        thumb.SetSize(thumbSize, thumbSize);
        bar.SetThumb(thumb);
        bar.SplitTrack = false;
        bar.SetPadding(Dp(ctx, 10f), Dp(ctx, 8f), Dp(ctx, 10f), Dp(ctx, 8f));
    }

    /// <summary>
    /// Styles a horizontal ProgressBar as a thick SESL9 rounded progress track.
    /// </summary>
    public static void StyleProgressBar(Context ctx, ProgressBar bar) {
        int trackH = Dp(ctx, 8f);
        int radius = Dp(ctx, 99f);

        GradientDrawable bgTrack = new();
        bgTrack.SetShape(ShapeType.Rectangle);
        bgTrack.SetColor(SurfaceFloating.ToArgb());
        bgTrack.SetCornerRadius(radius);

        GradientDrawable progressFill = new();
        progressFill.SetShape(ShapeType.Rectangle);
        progressFill.SetColor(Primary.ToArgb());
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
    /// Applies SESL9 thumb/track colors to a native Switch.
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
            ControlStroke.ToArgb()
        ]);
        sw.SetTextColor(TextPrimary);
    }

    /// <summary>
    /// Adds a subtle 1px One UI grouped list divider with 14dp horizontal inset.
    /// </summary>
    public static View CreateDivider(Context ctx) {
        View line = new(ctx);
        line.SetBackgroundColor(Divider);
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(ctx, 0.75f))) {
            LeftMargin = Dp(ctx, 4f),
            RightMargin = Dp(ctx, 4f),
            TopMargin = Dp(ctx, 4f),
            BottomMargin = Dp(ctx, 4f)
        };
        line.LayoutParameters = lp;
        return line;
    }
}

/// <summary>
/// Scroll-safe SeekBar styled for One UI 9: ignores vertical scroll gestures so scrolling down a page
/// never accidentally changes a slider value.
/// </summary>
public sealed class OneUiSeekBar : SeekBar {
    readonly int touchSlop;
    float downX;
    float downY;
    int initialProgress;
    bool isHorizontalDrag;
    bool isCancelledToScroll;
    MotionEvent pendingDown;

    public OneUiSeekBar(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        OneUiTheme.StyleSeekBar(context, this);
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
                        return false;
                    }
                    if (dx > touchSlop && dx >= dy * 1.15f) {
                        isHorizontalDrag = true;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        if (pendingDown != null) {
                            base.OnTouchEvent(pendingDown);
                            pendingDown.Recycle();
                            pendingDown = null;
                        }
                        return base.OnTouchEvent(e);
                    }
                    return true;
                }
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return base.OnTouchEvent(e);

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
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
                return base.OnTouchEvent(e);

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
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
