using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

public enum OneUiIconType {
    Room3D,
    Speaker,
    Bolt,
    Scissors,
    Folder,
    Sliders,
    Gear,
    Queue,
    Sparkles,
    Headphones,
    Distance,
    HeightUp,
    Wrench,
    Info,
    Logs,
    Lock,
    Unlock,
    Play,
    Pause,
    Stop,
    Share,
    ChevronDown,
    ChevronUp,
    Check,
    MusicFile
}

/// <summary>
/// Renders crisp vector-stroked One UI 9 icons using Android Canvas &amp; Path (zero emojis).
/// </summary>
public static class OneUiVectorIcons {
    public static void DrawIcon(Canvas canvas, OneUiIconType type, float cx, float cy, float size, Color color) {
        using Paint stroke = new(PaintFlags.AntiAlias) {
            Color = color,
            StrokeWidth = Math.Max(1.6f, size * 0.105f),
            StrokeCap = Paint.Cap.Round,
            StrokeJoin = Paint.Join.Round
        };
        stroke.SetStyle(Paint.Style.Stroke);

        using Paint fill = new(PaintFlags.AntiAlias) {
            Color = color
        };
        fill.SetStyle(Paint.Style.Fill);

        float r = size * 0.40f;
        using Android.Graphics.Path p = new();

        switch (type) {
            case OneUiIconType.Room3D:
                // Isometric spatial cube + center dot
                p.MoveTo(cx, cy - r);
                p.LineTo(cx + r * 0.88f, cy - r * 0.48f);
                p.LineTo(cx + r * 0.88f, cy + r * 0.48f);
                p.LineTo(cx, cy + r);
                p.LineTo(cx - r * 0.88f, cy + r * 0.48f);
                p.LineTo(cx - r * 0.88f, cy - r * 0.48f);
                p.Close();
                canvas.DrawPath(p, stroke);
                canvas.DrawLine(cx, cy, cx + r * 0.88f, cy - r * 0.48f, stroke);
                canvas.DrawLine(cx, cy, cx - r * 0.88f, cy - r * 0.48f, stroke);
                canvas.DrawLine(cx, cy, cx, cy + r, stroke);
                canvas.DrawCircle(cx, cy, size * 0.08f, fill);
                break;

            case OneUiIconType.Speaker:
                p.MoveTo(cx - r * 0.85f, cy - r * 0.30f);
                p.LineTo(cx - r * 0.42f, cy - r * 0.30f);
                p.LineTo(cx + r * 0.10f, cy - r * 0.72f);
                p.LineTo(cx + r * 0.10f, cy + r * 0.72f);
                p.LineTo(cx - r * 0.42f, cy + r * 0.30f);
                p.LineTo(cx - r * 0.85f, cy + r * 0.30f);
                p.Close();
                canvas.DrawPath(p, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.10f, cy - r * 0.45f, cx + r * 0.55f, cy + r * 0.45f), -50, 100, false, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.05f, cy - r * 0.78f, cx + r * 0.92f, cy + r * 0.78f), -52, 104, false, stroke);
                break;

            case OneUiIconType.Bolt:
                p.MoveTo(cx + r * 0.12f, cy - r * 0.95f);
                p.LineTo(cx - r * 0.65f, cy + r * 0.08f);
                p.LineTo(cx + r * 0.02f, cy + r * 0.08f);
                p.LineTo(cx - r * 0.14f, cy + r * 0.95f);
                p.LineTo(cx + r * 0.65f, cy - r * 0.08f);
                p.LineTo(cx - r * 0.02f, cy - r * 0.08f);
                p.Close();
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.Scissors:
                canvas.DrawCircle(cx - r * 0.52f, cy - r * 0.48f, r * 0.28f, stroke);
                canvas.DrawCircle(cx - r * 0.52f, cy + r * 0.48f, r * 0.28f, stroke);
                canvas.DrawLine(cx - r * 0.28f, cy - r * 0.30f, cx + r * 0.82f, cy + r * 0.55f, stroke);
                canvas.DrawLine(cx - r * 0.28f, cy + r * 0.30f, cx + r * 0.82f, cy - r * 0.55f, stroke);
                break;

            case OneUiIconType.Folder:
                p.MoveTo(cx - r * 0.88f, cy - r * 0.62f);
                p.LineTo(cx - r * 0.20f, cy - r * 0.62f);
                p.LineTo(cx + r * 0.02f, cy - r * 0.35f);
                p.LineTo(cx + r * 0.88f, cy - r * 0.35f);
                p.LineTo(cx + r * 0.88f, cy + r * 0.68f);
                p.LineTo(cx - r * 0.88f, cy + r * 0.68f);
                p.Close();
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.Sliders:
                float y1 = cy - r * 0.52f;
                float y2 = cy;
                float y3 = cy + r * 0.52f;
                canvas.DrawLine(cx - r * 0.85f, y1, cx + r * 0.85f, y1, stroke);
                canvas.DrawLine(cx - r * 0.85f, y2, cx + r * 0.85f, y2, stroke);
                canvas.DrawLine(cx - r * 0.85f, y3, cx + r * 0.85f, y3, stroke);
                canvas.DrawCircle(cx - r * 0.28f, y1, r * 0.18f, fill);
                canvas.DrawCircle(cx + r * 0.34f, y2, r * 0.18f, fill);
                canvas.DrawCircle(cx - r * 0.12f, y3, r * 0.18f, fill);
                break;

            case OneUiIconType.Gear:
                canvas.DrawCircle(cx, cy, r * 0.34f, stroke);
                for (int i = 0; i < 6; i++) {
                    float a = i * MathF.PI / 3f;
                    canvas.DrawLine(
                        cx + MathF.Cos(a) * r * 0.60f, cy + MathF.Sin(a) * r * 0.60f,
                        cx + MathF.Cos(a) * r * 0.90f, cy + MathF.Sin(a) * r * 0.90f,
                        stroke);
                }
                canvas.DrawCircle(cx, cy, r * 0.62f, stroke);
                break;

            case OneUiIconType.Queue:
                canvas.DrawLine(cx - r * 0.78f, cy - r * 0.50f, cx + r * 0.78f, cy - r * 0.50f, stroke);
                canvas.DrawLine(cx - r * 0.78f, cy, cx + r * 0.78f, cy, stroke);
                canvas.DrawLine(cx - r * 0.78f, cy + r * 0.50f, cx + r * 0.35f, cy + r * 0.50f, stroke);
                break;

            case OneUiIconType.Sparkles:
                p.MoveTo(cx, cy - r * 0.90f);
                p.QuadTo(cx, cy, cx + r * 0.90f, cy);
                p.QuadTo(cx, cy, cx, cy + r * 0.90f);
                p.QuadTo(cx, cy, cx - r * 0.90f, cy);
                p.QuadTo(cx, cy, cx, cy - r * 0.90f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;

            case OneUiIconType.Headphones:
                canvas.DrawArc(new RectF(cx - r * 0.80f, cy - r * 0.72f, cx + r * 0.80f, cy + r * 0.85f), 185, 170, false, stroke);
                canvas.DrawRoundRect(new RectF(cx - r * 0.90f, cy + r * 0.05f, cx - r * 0.45f, cy + r * 0.75f), r * 0.22f, r * 0.22f, fill);
                canvas.DrawRoundRect(new RectF(cx + r * 0.45f, cy + r * 0.05f, cx + r * 0.90f, cy + r * 0.75f), r * 0.22f, r * 0.22f, fill);
                break;

            case OneUiIconType.Distance:
                canvas.DrawLine(cx - r * 0.82f, cy, cx + r * 0.82f, cy, stroke);
                canvas.DrawLine(cx - r * 0.50f, cy - r * 0.32f, cx - r * 0.82f, cy, stroke);
                canvas.DrawLine(cx - r * 0.50f, cy + r * 0.32f, cx - r * 0.82f, cy, stroke);
                canvas.DrawLine(cx + r * 0.50f, cy - r * 0.32f, cx + r * 0.82f, cy, stroke);
                canvas.DrawLine(cx + r * 0.50f, cy + r * 0.32f, cx + r * 0.82f, cy, stroke);
                break;

            case OneUiIconType.HeightUp:
                canvas.DrawLine(cx, cy + r * 0.78f, cx, cy - r * 0.70f, stroke);
                canvas.DrawLine(cx - r * 0.46f, cy - r * 0.24f, cx, cy - r * 0.72f, stroke);
                canvas.DrawLine(cx + r * 0.46f, cy - r * 0.24f, cx, cy - r * 0.72f, stroke);
                canvas.DrawLine(cx - r * 0.65f, cy - r * 0.82f, cx + r * 0.65f, cy - r * 0.82f, stroke);
                break;

            case OneUiIconType.Wrench:
                canvas.DrawLine(cx - r * 0.62f, cy + r * 0.62f, cx + r * 0.32f, cy - r * 0.32f, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.02f, cy - r * 0.92f, cx + r * 0.92f, cy + r * 0.02f), 25, 250, false, stroke);
                break;

            case OneUiIconType.Info:
                canvas.DrawCircle(cx, cy, r * 0.82f, stroke);
                canvas.DrawLine(cx, cy - r * 0.05f, cx, cy + r * 0.42f, stroke);
                canvas.DrawCircle(cx, cy - r * 0.40f, size * 0.06f, fill);
                break;

            case OneUiIconType.Logs:
                canvas.DrawRoundRect(new RectF(cx - r * 0.76f, cy - r * 0.84f, cx + r * 0.76f, cy + r * 0.84f), r * 0.25f, r * 0.25f, stroke);
                canvas.DrawLine(cx - r * 0.40f, cy - r * 0.35f, cx + r * 0.40f, cy - r * 0.35f, stroke);
                canvas.DrawLine(cx - r * 0.40f, cy, cx + r * 0.40f, cy, stroke);
                canvas.DrawLine(cx - r * 0.40f, cy + r * 0.35f, cx + r * 0.16f, cy + r * 0.35f, stroke);
                break;

            case OneUiIconType.Lock:
                canvas.DrawRoundRect(new RectF(cx - r * 0.66f, cy - r * 0.10f, cx + r * 0.66f, cy + r * 0.78f), r * 0.22f, r * 0.22f, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.42f, cy - r * 0.82f, cx + r * 0.42f, cy + r * 0.02f), 180, 180, false, stroke);
                break;

            case OneUiIconType.Unlock:
                canvas.DrawRoundRect(new RectF(cx - r * 0.66f, cy - r * 0.08f, cx + r * 0.66f, cy + r * 0.78f), r * 0.22f, r * 0.22f, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.42f, cy - r * 0.82f, cx + r * 0.42f, cy + r * 0.02f), 190, 130, false, stroke);
                break;

            case OneUiIconType.Play:
                p.MoveTo(cx - r * 0.46f, cy - r * 0.70f);
                p.LineTo(cx + r * 0.74f, cy);
                p.LineTo(cx - r * 0.46f, cy + r * 0.70f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;

            case OneUiIconType.Pause:
                canvas.DrawRoundRect(new RectF(cx - r * 0.56f, cy - r * 0.68f, cx - r * 0.16f, cy + r * 0.68f), r * 0.15f, r * 0.15f, fill);
                canvas.DrawRoundRect(new RectF(cx + r * 0.16f, cy - r * 0.68f, cx + r * 0.56f, cy + r * 0.68f), r * 0.15f, r * 0.15f, fill);
                break;

            case OneUiIconType.Stop:
                canvas.DrawRoundRect(new RectF(cx - r * 0.58f, cy - r * 0.58f, cx + r * 0.58f, cy + r * 0.58f), r * 0.20f, r * 0.20f, fill);
                break;

            case OneUiIconType.Share:
                canvas.DrawCircle(cx + r * 0.56f, cy - r * 0.56f, r * 0.22f, stroke);
                canvas.DrawCircle(cx - r * 0.58f, cy, r * 0.22f, stroke);
                canvas.DrawCircle(cx + r * 0.56f, cy + r * 0.56f, r * 0.22f, stroke);
                canvas.DrawLine(cx - r * 0.38f, cy - r * 0.12f, cx + r * 0.36f, cy - r * 0.46f, stroke);
                canvas.DrawLine(cx - r * 0.38f, cy + r * 0.12f, cx + r * 0.36f, cy + r * 0.46f, stroke);
                break;

            case OneUiIconType.ChevronDown:
                p.MoveTo(cx - r * 0.64f, cy - r * 0.26f);
                p.LineTo(cx, cy + r * 0.36f);
                p.LineTo(cx + r * 0.64f, cy - r * 0.26f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.ChevronUp:
                p.MoveTo(cx - r * 0.64f, cy + r * 0.26f);
                p.LineTo(cx, cy - r * 0.36f);
                p.LineTo(cx + r * 0.64f, cy + r * 0.26f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.Check:
                p.MoveTo(cx - r * 0.68f, cy + r * 0.04f);
                p.LineTo(cx - r * 0.16f, cy + r * 0.56f);
                p.LineTo(cx + r * 0.72f, cy - r * 0.48f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.MusicFile:
                canvas.DrawCircle(cx - r * 0.30f, cy + r * 0.46f, r * 0.30f, stroke);
                canvas.DrawLine(cx, cy + r * 0.46f, cx, cy - r * 0.72f, stroke);
                canvas.DrawLine(cx, cy - r * 0.72f, cx + r * 0.62f, cy - r * 0.45f, stroke);
                break;
        }
    }
}

/// <summary>
/// Standalone vector icon View without background badge.
/// </summary>
public sealed class OneUiIconView : View {
    public OneUiIconType IconType {
        get => iconType;
        set {
            iconType = value;
            Invalidate();
        }
    }
    OneUiIconType iconType;

    public Color IconColor {
        get => iconColor;
        set {
            iconColor = value;
            Invalidate();
        }
    }
    Color iconColor = OneUiTheme.TextPrimary;

    public OneUiIconView(Context ctx, OneUiIconType type, Color? color = null) : base(ctx) {
        iconType = type;
        iconColor = color ?? OneUiTheme.TextPrimary;
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float sz = Math.Min(Width, Height);
        if (sz <= 0) {
            return;
        }
        OneUiVectorIcons.DrawIcon(canvas, iconType, Width * 0.5f, Height * 0.5f, sz * 0.86f, iconColor);
    }
}

/// <summary>
/// Samsung One UI 9 squircle/circle colored icon badge containing a crisp anti-aliased vector glyph.
/// </summary>
public sealed class OneUiIconBadge : View {
    readonly Paint bgPaint = new(PaintFlags.AntiAlias);
    OneUiIconType iconType;
    Color badgeColor;
    Color glyphColor = OneUiTheme.TextPrimary;

    public OneUiIconBadge(Context ctx, OneUiIconType type, Color badgeColor) : base(ctx) {
        iconType = type;
        this.badgeColor = badgeColor;
        bgPaint.Color = badgeColor;
        bgPaint.SetStyle(Paint.Style.Fill);
    }

    public void UpdateColors(Color newBadgeColor, Color? newGlyphColor = null) {
        badgeColor = newBadgeColor;
        bgPaint.Color = newBadgeColor;
        if (newGlyphColor.HasValue) {
            glyphColor = newGlyphColor.Value;
        }
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        float rad = Math.Min(w, h) * 0.36f; // One UI squircle curvature
        canvas.DrawRoundRect(new RectF(0, 0, w, h), rad, rad, bgPaint);
        OneUiVectorIcons.DrawIcon(canvas, iconType, w * 0.5f, h * 0.5f, Math.Min(w, h) * 0.62f, glyphColor);
    }
}

/// <summary>
/// Pill button containing a crisp vector icon + optional label, with adaptive height so text never clips.
/// </summary>
public static class OneUiPillFactory {
    public static LinearLayout CreateIconActionPill(
        Context ctx,
        OneUiIconType icon,
        string text,
        Action onClick,
        bool primary = false) {
        LinearLayout pill = new(ctx) { Orientation = Orientation.Horizontal };
        pill.SetGravity(GravityFlags.Center);
        int dp1 = OneUiTheme.Dp(ctx, 1);
        pill.SetPadding(dp1 * 12, dp1 * 8, dp1 * 14, dp1 * 8);
        pill.SetMinimumHeight(dp1 * 38);
        pill.Background = primary
            ? OneUiTheme.CreateRippleRoundedDrawable(ctx, OneUiTheme.Primary, OneUiTheme.PillCornerDp)
            : OneUiTheme.CreateGlassCardDrawable(ctx, OneUiTheme.SurfaceSubTile, OneUiTheme.PillCornerDp);
        OneUiTheme.AttachRecoil(pill, 0.95f);

        OneUiIconView iv = new(ctx, icon, OneUiTheme.TextPrimary);
        pill.AddView(iv, new LinearLayout.LayoutParams(dp1 * 16, dp1 * 16) { RightMargin = string.IsNullOrEmpty(text) ? 0 : dp1 * 7 });

        if (!string.IsNullOrEmpty(text)) {
            TextView tv = new(ctx) {
                Text = text,
                TextSize = 12.5f
            };
            tv.SetIncludeFontPadding(false);
            tv.SetTextColor(OneUiTheme.TextPrimary);
            tv.SetTypeface(null, primary ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            pill.AddView(tv);
        }

        pill.Click += (_, _) => onClick?.Invoke();
        return pill;
    }
}
