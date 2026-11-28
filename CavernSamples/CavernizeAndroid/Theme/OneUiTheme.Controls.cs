using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace CavernizeAndroid.Theme;

/// <summary>
/// One UI 9 drawable/control factory helpers and custom controls (<see cref="OneUiBackdropView"/>,
/// <see cref="OneUiGlassDrawable"/>, <see cref="OneUiSwitch"/>).
/// </summary>
public static partial class OneUiTheme {
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
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) {
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

    /// <summary>
    /// Creates an ellipsized single-line One UI 9 status badge chip.
    /// </summary>
    public static TextView CreateStatusChip(Context ctx, string text, Color accentColor) {
        TextView chip = new(ctx) {
            Text = text ?? "",
            TextSize = 11f,
            Gravity = GravityFlags.Center
        };
        chip.SetSingleLine(true);
        chip.Ellipsize = TextUtils.TruncateAt.End;
        chip.SetIncludeFontPadding(false);
        chip.Typeface = GetAppleFontBold(ctx);
        UpdateStatusChip(ctx, chip, text, accentColor);
        return chip;
    }

    /// <summary>
    /// Updates an existing One UI 9 status chip's text and accent tint.
    /// </summary>
    public static void UpdateStatusChip(Context ctx, TextView chip, string text, Color accentColor) {
        if (chip == null) {
            return;
        }
        chip.Text = text ?? "";
        chip.SetTextColor(BlendColor(accentColor, Color.White, 0.35f));
        chip.SetPadding(Dp(ctx, 10f), Dp(ctx, 4f), Dp(ctx, 10f), Dp(ctx, 4f));
        chip.Background = CreateRoundedDrawable(
            ctx,
            WithAlpha(accentColor, 48),
            PillCornerDp,
            WithAlpha(accentColor, 145),
            1f);
    }

    /// <summary>
    /// Creates a clean One UI 9 prerequisite / empty state banner card with an optional CTA pill button.
    /// </summary>
    public static LinearLayout CreateEmptyPrereqCard(
        Context ctx,
        string title,
        string subtitle,
        string ctaText = null,
        Action onCta = null) {
        LinearLayout card = new(ctx) {
            Orientation = Android.Widget.Orientation.Vertical
        };
        card.Background = CreateGlassCardDrawable(
            ctx,
            SurfaceSubTile,
            18f,
            WithAlpha(FunctionalAmber, 110));
        card.SetPadding(Dp(ctx, 14f), Dp(ctx, 12f), Dp(ctx, 14f), Dp(ctx, 12f));

        if (!string.IsNullOrEmpty(title)) {
            TextView titleTv = new(ctx) {
                Text = title,
                TextSize = 13f
            };
            titleTv.SetTextColor(TextPrimary);
            titleTv.Typeface = GetAppleFontBold(ctx);
            titleTv.SetIncludeFontPadding(false);
            card.AddView(titleTv);
        }

        if (!string.IsNullOrEmpty(subtitle)) {
            TextView subTv = new(ctx) {
                Text = subtitle,
                TextSize = 11.8f
            };
            subTv.SetTextColor(TextSub);
            subTv.Typeface = GetAppleFontRegular(ctx);
            subTv.SetLineSpacing(Dp(ctx, 1.5f), 1.06f);
            subTv.SetPadding(0, Dp(ctx, string.IsNullOrEmpty(title) ? 0f : 4f), 0, 0);
            card.AddView(subTv);
        }

        if (!string.IsNullOrEmpty(ctaText)) {
            Button ctaBtn = new(ctx) {
                Text = ctaText,
                TextSize = 12.2f
            };
            ctaBtn.SetAllCaps(false);
            ctaBtn.SetSingleLine(true);
            ctaBtn.Ellipsize = TextUtils.TruncateAt.End;
            ctaBtn.SetIncludeFontPadding(false);
            ctaBtn.SetTextColor(TextPrimary);
            ctaBtn.Typeface = GetAppleFontBold(ctx);
            ctaBtn.SetMinimumHeight(Dp(ctx, 38f));
            ctaBtn.SetPadding(Dp(ctx, 16f), Dp(ctx, 7f), Dp(ctx, 16f), Dp(ctx, 7f));
            ctaBtn.Background = CreateRippleRoundedDrawable(ctx, Primary, PillCornerDp);
            AttachRecoil(ctaBtn, 0.96f);
            ctaBtn.Click += (_, _) => onCta?.Invoke();
            card.AddView(ctaBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) {
                TopMargin = Dp(ctx, 10f)
            });
        }

        return card;
    }

    /// <summary>
    /// Creates a One UI 9 6-Category Settings Hub tile with squircle vector icon badge, bold title,
    /// live ellipsized status subtitle, and drill-down chevron.
    /// </summary>
    public static LinearLayout CreateCategoryHubTile(
        Context ctx,
        Views.OneUiIconType icon,
        Color iconColor,
        string title,
        string liveSubtitle,
        Action onClick,
        out TextView subtitleView) {
        LinearLayout tile = new(ctx) {
            Orientation = Android.Widget.Orientation.Horizontal,
            BaselineAligned = false
        };
        tile.SetGravity(GravityFlags.CenterVertical);
        tile.SetMinimumHeight(Dp(ctx, 68f));
        tile.SetPadding(Dp(ctx, 14f), Dp(ctx, 12f), Dp(ctx, 14f), Dp(ctx, 12f));
        tile.Background = CreateGlassCardDrawable(ctx, SurfaceCard, TileCornerDp);
        AttachRecoil(tile, 0.97f);

        Views.OneUiIconBadge badge = CreateVectorIconBadge(ctx, icon, iconColor, 38);
        tile.AddView(badge, new LinearLayout.LayoutParams(Dp(ctx, 38f), Dp(ctx, 38f)) {
            RightMargin = Dp(ctx, 12f)
        });

        LinearLayout textCol = new(ctx) {
            Orientation = Android.Widget.Orientation.Vertical
        };
        TextView titleTv = new(ctx) {
            Text = title ?? "",
            TextSize = 14.5f
        };
        titleTv.SetTextColor(TextPrimary);
        titleTv.Typeface = GetAppleFontBold(ctx);
        titleTv.SetIncludeFontPadding(false);
        titleTv.SetSingleLine(true);
        titleTv.Ellipsize = TextUtils.TruncateAt.End;

        subtitleView = new TextView(ctx) {
            Text = liveSubtitle ?? "",
            TextSize = 11.8f
        };
        subtitleView.SetTextColor(TextSub);
        subtitleView.Typeface = GetAppleFontMedium(ctx);
        subtitleView.SetIncludeFontPadding(false);
        subtitleView.SetSingleLine(true);
        subtitleView.Ellipsize = TextUtils.TruncateAt.End;
        subtitleView.SetPadding(0, Dp(ctx, 2.5f), 0, 0);

        textCol.AddView(titleTv);
        textCol.AddView(subtitleView);
        tile.AddView(textCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) {
            RightMargin = Dp(ctx, 8f)
        });

        Views.OneUiIconView chevron = new(ctx, Views.OneUiIconType.ChevronRight, TextMuted);
        tile.AddView(chevron, new LinearLayout.LayoutParams(Dp(ctx, 16f), Dp(ctx, 16f)));

        tile.Click += (_, _) => onClick?.Invoke();
        return tile;
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
    int cachedW = -1;
    int cachedH = -1;
    int cachedC1;
    int cachedC2;
    int cachedC3;
    RadialGradient cachedGrad1;
    RadialGradient cachedGrad2;
    RadialGradient cachedGrad3;

    public OneUiBackdropView(Context context) : base(context) {
        basePaint.Color = OneUiTheme.BgScreen;
        basePaint.SetStyle(Paint.Style.Fill);
    }

    public void SetMediaAccent(Color? primary, Color? secondary) {
        if (mediaPrimary == primary && mediaSecondary == secondary) {
            return;
        }
        mediaPrimary = primary;
        mediaSecondary = secondary;
        cachedW = -1;
        PostInvalidateOnAnimation();
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        int iw = Width;
        int ih = Height;
        if (iw <= 0 || ih <= 0) {
            return;
        }
        float w = iw;
        float h = ih;

        basePaint.Color = OneUiTheme.BgScreen;
        canvas.DrawRect(0, 0, w, h, basePaint);

        Color p1 = mediaPrimary ?? OneUiTheme.Primary;
        Color p2 = mediaSecondary ?? OneUiTheme.Tertiary;
        Color p3 = mediaPrimary ?? OneUiTheme.Secondary;
        int alpha1 = mediaPrimary.HasValue ? 64 : 24;
        int alpha2 = mediaSecondary.HasValue ? 46 : 20;
        int alpha3 = mediaPrimary.HasValue ? 36 : 18;
        int c1 = Color.Argb(alpha1, p1.R, p1.G, p1.B).ToArgb();
        int c2 = Color.Argb(alpha2, p2.R, p2.G, p2.B).ToArgb();
        int c3 = Color.Argb(alpha3, p3.R, p3.G, p3.B).ToArgb();

        float maxDim = Math.Max(w, h);
        float r1 = maxDim * 0.58f;
        float r2 = maxDim * 0.50f;
        float r3 = maxDim * 0.48f;

        if (cachedW != iw || cachedH != ih || cachedC1 != c1 || cachedC2 != c2 || cachedC3 != c3 ||
            cachedGrad1 == null || cachedGrad2 == null || cachedGrad3 == null) {
            cachedGrad1?.Dispose();
            cachedGrad2?.Dispose();
            cachedGrad3?.Dispose();
            int transp = Color.Transparent.ToArgb();
            cachedGrad1 = new RadialGradient(w * 0.30f, h * 0.12f, Math.Max(1f, r1), [c1, transp], [0f, 1f], Shader.TileMode.Clamp!);
            cachedGrad2 = new RadialGradient(w * 0.86f, h * 0.44f, Math.Max(1f, r2), [c2, transp], [0f, 1f], Shader.TileMode.Clamp!);
            cachedGrad3 = new RadialGradient(w * 0.22f, h * 0.86f, Math.Max(1f, r3), [c3, transp], [0f, 1f], Shader.TileMode.Clamp!);
            cachedW = iw;
            cachedH = ih;
            cachedC1 = c1;
            cachedC2 = c2;
            cachedC3 = c3;
        }

        // Smooth analytical radial bokeh orbs (zero full-screen RenderEffect GPU pass, zero per-draw shader allocs)
        orbPaint.SetShader(cachedGrad1);
        canvas.DrawCircle(w * 0.30f, h * 0.12f, r1, orbPaint);
        orbPaint.SetShader(cachedGrad2);
        canvas.DrawCircle(w * 0.86f, h * 0.44f, r2, orbPaint);
        orbPaint.SetShader(cachedGrad3);
        canvas.DrawCircle(w * 0.22f, h * 0.86f, r3, orbPaint);
        orbPaint.SetShader(null);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            cachedGrad1?.Dispose();
            cachedGrad2?.Dispose();
            cachedGrad3?.Dispose();
            cachedGrad1 = null;
            cachedGrad2 = null;
            cachedGrad3 = null;
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
/// Faithful Samsung One UI 9 / Gallery SESL9 Switch (extracted from SamsungGallery2018.apk sesl_switch_*):
/// - Track capsule: 32dp x 20dp (corner radius 10dp), track off #636368, track on Dynamic Primary
/// - Thumb: 20dp outer ring (2dp stroke in #8F8F8F off / Primary on) + 16dp #FCFCFF inner circle
/// - Tactile press animation: thumb core scales cleanly from 1.0 to 0.80 while pressed, and glides 180ms on toggle.
/// </summary>
public sealed class OneUiSwitch : Switch {
    readonly Paint trackPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbCorePaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbRingPaint = new(PaintFlags.AntiAlias);
    readonly int touchSlop;

    float thumbPos;
    float pressFrac;
    bool hasDrawnOnce;
    bool isPressedDown;
    bool didSwipe;
    float downX;
    float downY;
    long lastFrameMs;

    public OneUiSwitch(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 20;
        SetBackgroundColor(Color.Transparent);
#pragma warning disable CS0618
        ThumbDrawable = new ColorDrawable(Color.Transparent);
        TrackDrawable = new ColorDrawable(Color.Transparent);
#pragma warning restore CS0618
        SplitTrack = false;
        ShowText = false;
        SetPadding(0, 0, 0, 0);
        SetMinWidth(0);
        SetMinHeight(0);
        Clickable = true;
        Focusable = true;

        trackPaint.SetStyle(Android.Graphics.Paint.Style.Fill);
        thumbCorePaint.SetStyle(Android.Graphics.Paint.Style.Fill);
        thumbRingPaint.SetStyle(Android.Graphics.Paint.Style.Stroke);

        CheckedChange += (_, _) => {
            if (hasDrawnOnce) {
                lastFrameMs = SystemClock.UptimeMillis();
                PostInvalidateOnAnimation();
            } else {
                thumbPos = Checked ? 1f : 0f;
            }
        };
    }

    public override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int w = OneUiTheme.Dp(Context, 40f);
        int h = OneUiTheme.Dp(Context, 26f);
        SetMeasuredDimension(ResolveSize(w, widthMeasureSpec), ResolveSize(h, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        if (!hasDrawnOnce) {
            hasDrawnOnce = true;
            thumbPos = Checked ? 1f : 0f;
            pressFrac = 0f;
            lastFrameMs = SystemClock.UptimeMillis();
        }

        long nowMs = SystemClock.UptimeMillis();
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0.001f, 0.05f);
        lastFrameMs = nowMs;

        float targetPos = Checked ? 1f : 0f;
        thumbPos += (targetPos - thumbPos) * Math.Min(1f, dt * 16f);
        if (MathF.Abs(targetPos - thumbPos) < 0.004f) {
            thumbPos = targetPos;
        }

        float targetPress = isPressedDown ? 1f : 0f;
        pressFrac += (targetPress - pressFrac) * Math.Min(1f, dt * 22f);
        if (MathF.Abs(targetPress - pressFrac) < 0.004f) {
            pressFrac = targetPress;
        }

        float dp = Resources.DisplayMetrics.Density;
        float trackW = 32f * dp;
        float trackH = 20f * dp;
        float trackLeft = (w - trackW) * 0.5f;
        float trackTop = (h - trackH) * 0.5f;
        float corner = trackH * 0.5f;

        Color trackOff = Color.Rgb(99, 99, 104); // #636368 Samsung Gallery dark track off
        Color trackOn = OneUiTheme.Primary;
        trackPaint.Color = OneUiTheme.BlendColor(trackOff, trackOn, thumbPos);
        canvas.DrawRoundRect(
            new RectF(trackLeft, trackTop, trackLeft + trackW, trackTop + trackH),
            corner, corner, trackPaint);

        // Thumb travels cleanly inside the 32x20 capsule (outer diameter 20dp, inner white circle 16dp)
        float outerR = 10f * dp;
        float cy = h * 0.5f;
        float startCx = trackLeft + outerR;
        float endCx = trackLeft + trackW - outerR;
        float cx = startCx + (endCx - startCx) * thumbPos;

        float strokeW = 2f * dp;
        float scale = 1f - 0.16f * pressFrac;
        float coreR = (8.2f * dp) * scale;
        float ringR = (9.0f * dp) * (1f - 0.10f * pressFrac);

        Color ringOff = Color.Rgb(143, 143, 148); // #8F8F8F sesl_switch_thumb_off stroke
        thumbRingPaint.StrokeWidth = strokeW;
        thumbRingPaint.Color = OneUiTheme.BlendColor(ringOff, trackOn, thumbPos);
        thumbCorePaint.Color = Color.Rgb(252, 252, 255); // #FCFCFF sesl_switch_thumb fill

        canvas.DrawCircle(cx, cy, coreR, thumbCorePaint);
        canvas.DrawCircle(cx, cy, ringR, thumbRingPaint);

        if (MathF.Abs(targetPos - thumbPos) > 0.004f || MathF.Abs(targetPress - pressFrac) > 0.004f) {
            PostInvalidateOnAnimation();
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!Enabled) {
            return false;
        }
        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = e.GetX();
                downY = e.GetY();
                didSwipe = false;
                isPressedDown = true;
                lastFrameMs = SystemClock.UptimeMillis();
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Move:
                float dx = e.GetX() - downX;
                float dy = e.GetY() - downY;
                if (!didSwipe && MathF.Abs(dx) > touchSlop * 0.45f && MathF.Abs(dx) >= MathF.Abs(dy)) {
                    didSwipe = true;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                }
                if (didSwipe) {
                    bool desired = dx > 0f;
                    if (Checked != desired) {
                        Checked = desired;
                    }
                    PostInvalidateOnAnimation();
                    return true;
                }
                return true;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isPressedDown = false;
                if (!didSwipe && MathF.Abs(e.GetY() - downY) <= touchSlop) {
                    PerformClick();
                }
                lastFrameMs = SystemClock.UptimeMillis();
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isPressedDown = false;
                didSwipe = false;
                lastFrameMs = SystemClock.UptimeMillis();
                PostInvalidateOnAnimation();
                return true;
        }
        return base.OnTouchEvent(e);
    }
}
