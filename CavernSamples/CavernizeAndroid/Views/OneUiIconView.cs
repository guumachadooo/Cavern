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
    MusicFile,
    HelpQuestion,
    Rewind10,
    Forward10,
    RepeatLoop,
    PlayerDisc,
    RewindStart,
    LyricsQuote,
    VideoCamera,
    PictureArt,
    ZoomIn,
    ZoomOut,
    MoreHorizontal,
    SubtitlesCc,
    FullscreenEnter,
    FullscreenExit,
    AspectRatioFit,
    SpeakerLow,
    SpeakerHigh,
    AppleRewind,
    AppleForward,
    SkipPrevious,
    SkipNext,
    LabelsTag,
    Search,
    LibraryGrid,
    Globe,
    ChevronLeft,
    Refresh
}

public enum OneUiTabKind {
    Player,
    Room3D,
    Studio,
    Stems,
    Settings
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

            case OneUiIconType.HelpQuestion:
                canvas.DrawArc(new RectF(cx - r * 0.36f, cy - r * 0.76f, cx + r * 0.38f, cy - r * 0.04f), 195f, 225f, false, stroke);
                canvas.DrawLine(cx + r * 0.14f, cy - r * 0.08f, cx, cy + r * 0.16f, stroke);
                canvas.DrawCircle(cx, cy + r * 0.56f, size * 0.068f, fill);
                break;

            case OneUiIconType.Rewind10:
                canvas.DrawArc(new RectF(cx - r * 0.78f, cy - r * 0.78f, cx + r * 0.78f, cy + r * 0.78f), 225f, -265f, false, stroke);
                p.MoveTo(cx - r * 0.62f, cy - r * 0.72f);
                p.LineTo(cx - r * 0.62f, cy - r * 0.24f);
                p.LineTo(cx - r * 0.18f, cy - r * 0.42f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.Forward10:
                canvas.DrawArc(new RectF(cx - r * 0.78f, cy - r * 0.78f, cx + r * 0.78f, cy + r * 0.78f), -45f, 265f, false, stroke);
                p.MoveTo(cx + r * 0.62f, cy - r * 0.72f);
                p.LineTo(cx + r * 0.62f, cy - r * 0.24f);
                p.LineTo(cx + r * 0.18f, cy - r * 0.42f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.RepeatLoop:
                canvas.DrawRoundRect(new RectF(cx - r * 0.78f, cy - r * 0.48f, cx + r * 0.78f, cy + r * 0.48f), r * 0.36f, r * 0.36f, stroke);
                p.MoveTo(cx + r * 0.18f, cy - r * 0.74f);
                p.LineTo(cx + r * 0.48f, cy - r * 0.48f);
                p.LineTo(cx + r * 0.18f, cy - r * 0.22f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.PlayerDisc:
                canvas.DrawCircle(cx, cy, r * 0.86f, stroke);
                canvas.DrawCircle(cx, cy, r * 0.24f, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.52f, cy - r * 0.52f, cx + r * 0.52f, cy + r * 0.52f), -65f, 95f, false, stroke);
                break;

            case OneUiIconType.RewindStart:
                float barX = cx - r * 0.62f;
                canvas.DrawLine(barX, cy - r * 0.58f, barX, cy + r * 0.58f, stroke);
                p.MoveTo(cx + r * 0.62f, cy - r * 0.60f);
                p.LineTo(cx - r * 0.34f, cy);
                p.LineTo(cx + r * 0.62f, cy + r * 0.60f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;

            case OneUiIconType.LyricsQuote:
                // Apple Music ic_nowplaying_lyrics rounded quote bubble with crisp quote commas
                canvas.DrawRoundRect(new RectF(cx - r * 0.84f, cy - r * 0.72f, cx + r * 0.84f, cy + r * 0.50f), r * 0.30f, r * 0.30f, stroke);
                p.MoveTo(cx - r * 0.36f, cy + r * 0.50f);
                p.LineTo(cx - r * 0.42f, cy + r * 0.86f);
                p.LineTo(cx - r * 0.04f, cy + r * 0.50f);
                canvas.DrawPath(p, stroke);
                canvas.DrawCircle(cx - r * 0.28f, cy - r * 0.16f, r * 0.14f, fill);
                canvas.DrawLine(cx - r * 0.22f, cy - r * 0.04f, cx - r * 0.32f, cy + r * 0.14f, stroke);
                canvas.DrawCircle(cx + r * 0.26f, cy - r * 0.16f, r * 0.14f, fill);
                canvas.DrawLine(cx + r * 0.32f, cy - r * 0.04f, cx + r * 0.22f, cy + r * 0.14f, stroke);
                break;

            case OneUiIconType.VideoCamera:
                canvas.DrawRoundRect(new RectF(cx - r * 0.86f, cy - r * 0.54f, cx + r * 0.36f, cy + r * 0.54f), r * 0.24f, r * 0.24f, stroke);
                p.MoveTo(cx + r * 0.38f, cy - r * 0.22f);
                p.LineTo(cx + r * 0.88f, cy - r * 0.48f);
                p.LineTo(cx + r * 0.88f, cy + r * 0.48f);
                p.LineTo(cx + r * 0.38f, cy + r * 0.22f);
                p.Close();
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.PictureArt:
                canvas.DrawRoundRect(new RectF(cx - r * 0.80f, cy - r * 0.80f, cx + r * 0.80f, cy + r * 0.80f), r * 0.28f, r * 0.28f, stroke);
                canvas.DrawCircle(cx - r * 0.28f, cy - r * 0.24f, r * 0.16f, fill);
                p.MoveTo(cx - r * 0.62f, cy + r * 0.56f);
                p.LineTo(cx - r * 0.14f, cy + r * 0.10f);
                p.LineTo(cx + r * 0.16f, cy + r * 0.34f);
                p.LineTo(cx + r * 0.46f, cy + r * 0.02f);
                p.LineTo(cx + r * 0.68f, cy + r * 0.56f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.ZoomIn:
                canvas.DrawCircle(cx - r * 0.14f, cy - r * 0.14f, r * 0.58f, stroke);
                canvas.DrawLine(cx + r * 0.28f, cy + r * 0.28f, cx + r * 0.78f, cy + r * 0.78f, stroke);
                canvas.DrawLine(cx - r * 0.36f, cy - r * 0.14f, cx + r * 0.08f, cy - r * 0.14f, stroke);
                canvas.DrawLine(cx - r * 0.14f, cy - r * 0.36f, cx - r * 0.14f, cy + r * 0.08f, stroke);
                break;

            case OneUiIconType.ZoomOut:
                canvas.DrawCircle(cx - r * 0.14f, cy - r * 0.14f, r * 0.58f, stroke);
                canvas.DrawLine(cx + r * 0.28f, cy + r * 0.28f, cx + r * 0.78f, cy + r * 0.78f, stroke);
                canvas.DrawLine(cx - r * 0.36f, cy - r * 0.14f, cx + r * 0.08f, cy - r * 0.14f, stroke);
                break;

            case OneUiIconType.MoreHorizontal:
                float dotR = Math.Max(1.6f, r * 0.16f);
                canvas.DrawCircle(cx - r * 0.52f, cy, dotR, fill);
                canvas.DrawCircle(cx, cy, dotR, fill);
                canvas.DrawCircle(cx + r * 0.52f, cy, dotR, fill);
                break;

            case OneUiIconType.SubtitlesCc:
                canvas.DrawRoundRect(new RectF(cx - r * 0.88f, cy - r * 0.62f, cx + r * 0.88f, cy + r * 0.62f), r * 0.22f, r * 0.22f, stroke);
                // Two crisp "C" glyphs inside box
                canvas.DrawArc(new RectF(cx - r * 0.58f, cy - r * 0.30f, cx - r * 0.08f, cy + r * 0.30f), 45f, 270f, false, stroke);
                canvas.DrawArc(new RectF(cx + r * 0.08f, cy - r * 0.30f, cx + r * 0.58f, cy + r * 0.30f), 45f, 270f, false, stroke);
                break;

            case OneUiIconType.FullscreenEnter: {
                float arm = r * 0.36f;
                float m = r * 0.78f;
                // Top-left
                canvas.DrawLine(cx - m, cy - m + arm, cx - m, cy - m, stroke);
                canvas.DrawLine(cx - m, cy - m, cx - m + arm, cy - m, stroke);
                // Top-right
                canvas.DrawLine(cx + m - arm, cy - m, cx + m, cy - m, stroke);
                canvas.DrawLine(cx + m, cy - m, cx + m, cy - m + arm, stroke);
                // Bottom-left
                canvas.DrawLine(cx - m, cy + m - arm, cx - m, cy + m, stroke);
                canvas.DrawLine(cx - m, cy + m, cx - m + arm, cy + m, stroke);
                // Bottom-right
                canvas.DrawLine(cx + m - arm, cy + m, cx + m, cy + m, stroke);
                canvas.DrawLine(cx + m, cy + m, cx + m, cy + m - arm, stroke);
                break;
            }

            case OneUiIconType.FullscreenExit: {
                float arm = r * 0.34f;
                float inGap = r * 0.26f;
                float outM = r * 0.82f;
                // Top-left inner corner
                canvas.DrawLine(cx - outM, cy - inGap, cx - inGap, cy - inGap, stroke);
                canvas.DrawLine(cx - inGap, cy - outM, cx - inGap, cy - inGap, stroke);
                // Top-right inner corner
                canvas.DrawLine(cx + inGap, cy - outM, cx + inGap, cy - inGap, stroke);
                canvas.DrawLine(cx + inGap, cy - inGap, cx + outM, cy - inGap, stroke);
                // Bottom-left inner corner
                canvas.DrawLine(cx - outM, cy + inGap, cx - inGap, cy + inGap, stroke);
                canvas.DrawLine(cx - inGap, cy + inGap, cx - inGap, cy + outM, stroke);
                // Bottom-right inner corner
                canvas.DrawLine(cx + inGap, cy + inGap, cx + outM, cy + inGap, stroke);
                canvas.DrawLine(cx + inGap, cy + inGap, cx + inGap, cy + outM, stroke);
                break;
            }

            case OneUiIconType.AspectRatioFit:
                canvas.DrawRoundRect(new RectF(cx - r * 0.88f, cy - r * 0.58f, cx + r * 0.88f, cy + r * 0.58f), r * 0.20f, r * 0.20f, stroke);
                canvas.DrawLine(cx - r * 0.52f, cy - r * 0.22f, cx - r * 0.24f, cy - r * 0.22f, stroke);
                canvas.DrawLine(cx - r * 0.52f, cy - r * 0.22f, cx - r * 0.52f, cy + r * 0.06f, stroke);
                canvas.DrawLine(cx + r * 0.52f, cy + r * 0.22f, cx + r * 0.24f, cy + r * 0.22f, stroke);
                canvas.DrawLine(cx + r * 0.52f, cy + r * 0.22f, cx + r * 0.52f, cy - r * 0.06f, stroke);
                break;

            case OneUiIconType.SpeakerLow:
                p.MoveTo(cx - r * 0.58f, cy - r * 0.26f);
                p.LineTo(cx - r * 0.20f, cy - r * 0.26f);
                p.LineTo(cx + r * 0.24f, cy - r * 0.62f);
                p.LineTo(cx + r * 0.24f, cy + r * 0.62f);
                p.LineTo(cx - r * 0.20f, cy + r * 0.26f);
                p.LineTo(cx - r * 0.58f, cy + r * 0.26f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;

            case OneUiIconType.SpeakerHigh:
                p.MoveTo(cx - r * 0.80f, cy - r * 0.26f);
                p.LineTo(cx - r * 0.44f, cy - r * 0.26f);
                p.LineTo(cx - r * 0.02f, cy - r * 0.62f);
                p.LineTo(cx - r * 0.02f, cy + r * 0.62f);
                p.LineTo(cx - r * 0.44f, cy + r * 0.26f);
                p.LineTo(cx - r * 0.80f, cy + r * 0.26f);
                p.Close();
                canvas.DrawPath(p, fill);
                canvas.DrawArc(new RectF(cx - r * 0.14f, cy - r * 0.42f, cx + r * 0.42f, cy + r * 0.42f), -48, 96, false, stroke);
                canvas.DrawArc(new RectF(cx - r * 0.10f, cy - r * 0.72f, cx + r * 0.76f, cy + r * 0.72f), -50, 100, false, stroke);
                break;

            case OneUiIconType.AppleRewind: {
                // Two solid rounded left-pointing triangles (Apple Music style)
                p.MoveTo(cx - r * 0.04f, cy - r * 0.48f);
                p.LineTo(cx - r * 0.86f, cy);
                p.LineTo(cx - r * 0.04f, cy + r * 0.48f);
                p.Close();
                p.MoveTo(cx + r * 0.78f, cy - r * 0.48f);
                p.LineTo(cx - r * 0.04f, cy);
                p.LineTo(cx + r * 0.78f, cy + r * 0.48f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;
            }

            case OneUiIconType.AppleForward: {
                // Two solid rounded right-pointing triangles (Apple Music style)
                p.MoveTo(cx - r * 0.78f, cy - r * 0.48f);
                p.LineTo(cx + r * 0.04f, cy);
                p.LineTo(cx - r * 0.78f, cy + r * 0.48f);
                p.Close();
                p.MoveTo(cx + r * 0.04f, cy - r * 0.48f);
                p.LineTo(cx + r * 0.86f, cy);
                p.LineTo(cx + r * 0.04f, cy + r * 0.48f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;
            }

            case OneUiIconType.SkipPrevious: {
                canvas.DrawRoundRect(new RectF(cx - r * 0.78f, cy - r * 0.56f, cx - r * 0.54f, cy + r * 0.56f), r * 0.12f, r * 0.12f, fill);
                p.MoveTo(cx + r * 0.74f, cy - r * 0.56f);
                p.LineTo(cx - r * 0.34f, cy);
                p.LineTo(cx + r * 0.74f, cy + r * 0.56f);
                p.Close();
                canvas.DrawPath(p, fill);
                break;
            }

            case OneUiIconType.SkipNext: {
                p.MoveTo(cx - r * 0.74f, cy - r * 0.56f);
                p.LineTo(cx + r * 0.34f, cy);
                p.LineTo(cx - r * 0.74f, cy + r * 0.56f);
                p.Close();
                canvas.DrawPath(p, fill);
                canvas.DrawRoundRect(new RectF(cx + r * 0.54f, cy - r * 0.56f, cx + r * 0.78f, cy + r * 0.56f), r * 0.12f, r * 0.12f, fill);
                break;
            }

            case OneUiIconType.LabelsTag:
                canvas.DrawRoundRect(new RectF(cx - r * 0.82f, cy - r * 0.56f, cx + r * 0.82f, cy + r * 0.56f), r * 0.24f, r * 0.24f, stroke);
                canvas.DrawLine(cx - r * 0.48f, cy - r * 0.14f, cx + r * 0.48f, cy - r * 0.14f, stroke);
                canvas.DrawLine(cx - r * 0.48f, cy + r * 0.18f, cx + r * 0.18f, cy + r * 0.18f, stroke);
                break;

            case OneUiIconType.Search:
                canvas.DrawCircle(cx - r * 0.14f, cy - r * 0.14f, r * 0.56f, stroke);
                canvas.DrawLine(cx + r * 0.26f, cy + r * 0.26f, cx + r * 0.80f, cy + r * 0.80f, stroke);
                break;

            case OneUiIconType.LibraryGrid:
                float gGap = r * 0.14f;
                float gRad = r * 0.18f;
                canvas.DrawRoundRect(new RectF(cx - r * 0.84f, cy - r * 0.84f, cx - gGap, cy - gGap), gRad, gRad, stroke);
                canvas.DrawRoundRect(new RectF(cx + gGap, cy - r * 0.84f, cx + r * 0.84f, cy - gGap), gRad, gRad, stroke);
                canvas.DrawRoundRect(new RectF(cx - r * 0.84f, cy + gGap, cx - gGap, cy + r * 0.84f), gRad, gRad, stroke);
                canvas.DrawRoundRect(new RectF(cx + gGap, cy + gGap, cx + r * 0.84f, cy + r * 0.84f), gRad, gRad, stroke);
                break;

            case OneUiIconType.Globe:
                canvas.DrawCircle(cx, cy, r * 0.82f, stroke);
                canvas.DrawOval(new RectF(cx - r * 0.36f, cy - r * 0.82f, cx + r * 0.36f, cy + r * 0.82f), stroke);
                canvas.DrawLine(cx - r * 0.80f, cy, cx + r * 0.80f, cy, stroke);
                break;

            case OneUiIconType.ChevronLeft:
                p.MoveTo(cx + r * 0.26f, cy - r * 0.64f);
                p.LineTo(cx - r * 0.36f, cy);
                p.LineTo(cx + r * 0.26f, cy + r * 0.64f);
                canvas.DrawPath(p, stroke);
                break;

            case OneUiIconType.Refresh:
                canvas.DrawArc(new RectF(cx - r * 0.72f, cy - r * 0.72f, cx + r * 0.72f, cy + r * 0.72f), -35f, 290f, false, stroke);
                p.MoveTo(cx + r * 0.22f, cy - r * 0.72f);
                p.LineTo(cx + r * 0.66f, cy - r * 0.72f);
                p.LineTo(cx + r * 0.66f, cy - r * 0.28f);
                canvas.DrawPath(p, stroke);
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
/// Samsung One UI 9 squircle icon badge containing a crisp anti-aliased vector glyph.
/// Uses frosted translucent squircle tinting for section headers and solid fill when highlighted (e.g. active Bento tiles).
/// </summary>
public sealed class OneUiIconBadge : View {
    readonly Paint bgPaint = new(PaintFlags.AntiAlias);
    readonly Paint rimPaint = new(PaintFlags.AntiAlias);
    OneUiIconType iconType;
    Color badgeColor;
    Color glyphColor = OneUiTheme.TextPrimary;
    bool isSolid;

    public OneUiIconBadge(Context ctx, OneUiIconType type, Color badgeColor, bool solid = false) : base(ctx) {
        iconType = type;
        this.badgeColor = badgeColor;
        isSolid = solid;
        bgPaint.SetStyle(Paint.Style.Fill);
        rimPaint.SetStyle(Paint.Style.Stroke);
        rimPaint.StrokeWidth = Math.Max(1.2f, OneUiTheme.Dp(ctx, 1.0f));
        ApplyPaintState();
    }

    public void UpdateColors(Color newBadgeColor, Color? newGlyphColor = null, bool? solidOverride = null) {
        badgeColor = newBadgeColor;
        if (solidOverride.HasValue) {
            isSolid = solidOverride.Value;
        }
        if (newGlyphColor.HasValue) {
            glyphColor = newGlyphColor.Value;
        }
        ApplyPaintState();
        Invalidate();
    }

    void ApplyPaintState() {
        if (isSolid) {
            bgPaint.Color = badgeColor;
            rimPaint.Color = Color.Transparent;
        } else {
            bgPaint.Color = Color.Argb(44, badgeColor.R, badgeColor.G, badgeColor.B);
            rimPaint.Color = Color.Argb(115, badgeColor.R, badgeColor.G, badgeColor.B);
            glyphColor = OneUiTheme.BlendColor(badgeColor, Color.White, 0.28f);
        }
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        float rad = Math.Min(w, h) * 0.36f; // One UI squircle curvature
        float inset = rimPaint.StrokeWidth * 0.5f;
        RectF r = new(inset, inset, w - inset, h - inset);
        canvas.DrawRoundRect(r, rad, rad, bgPaint);
        if (!isSolid) {
            canvas.DrawRoundRect(r, rad, rad, rimPaint);
        }
        OneUiVectorIcons.DrawIcon(canvas, iconType, w * 0.5f, h * 0.5f, Math.Min(w, h) * 0.58f, glyphColor);
    }
}

/// <summary>
/// Semi-transparent frosted line / even-odd cutout solid icon modeled directly after Samsung Gallery's bottom tab bar
/// (SamsungGallery2018.apk sesl_bottom_navigation_item: semi-transparent glass line unselected, crisp solid cutout selected).
/// </summary>
public sealed class OneUiTabIconView : View {
    readonly Paint solidPaint = new(PaintFlags.AntiAlias);
    readonly Paint glassFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint linePaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path evenOddPath = new();

    OneUiTabKind kind;
    bool isTabSelected;

    public bool IsTabSelected {
        get => isTabSelected;
        set {
            if (isTabSelected != value) {
                isTabSelected = value;
                Invalidate();
            }
        }
    }

    public OneUiTabIconView(Context ctx, OneUiTabKind kind) : base(ctx) {
        this.kind = kind;
        float dp = ctx.Resources.DisplayMetrics.Density;

        solidPaint.Color = Color.Argb(245, 252, 252, 255); // #FCFCFF
        solidPaint.SetStyle(Paint.Style.Fill);

        glassFillPaint.Color = Color.Argb(38, 255, 255, 255); // translucent frosted interior
        glassFillPaint.SetStyle(Paint.Style.Fill);

        linePaint.Color = Color.Argb(196, 188, 190, 196); // semi-transparent frosted stroke
        linePaint.StrokeWidth = Math.Max(1.4f, 1.55f * dp);
        linePaint.StrokeCap = Paint.Cap.Round;
        linePaint.StrokeJoin = Paint.Join.Round;
        linePaint.SetStyle(Paint.Style.Stroke);
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float r = Math.Min(w, h) * 0.36f;

        if (isTabSelected) {
            evenOddPath.Reset();
            evenOddPath.SetFillType(Android.Graphics.Path.FillType.EvenOdd);
            BuildSelectedCutoutPath(evenOddPath, cx, cy, r);
            canvas.DrawPath(evenOddPath, solidPaint);
        } else {
            DrawUnselectedGlassIcon(canvas, cx, cy, r);
        }
    }

    void DrawUnselectedGlassIcon(Canvas canvas, float cx, float cy, float r) {
        RectF outer = new(cx - r * 0.88f, cy - r * 0.88f, cx + r * 0.88f, cy + r * 0.88f);
        float rr = r * 0.34f;
        using Android.Graphics.Path p = new();

        switch (kind) {
            case OneUiTabKind.Player:
                canvas.DrawRoundRect(outer, rr, rr, glassFillPaint);
                canvas.DrawRoundRect(outer, rr, rr, linePaint);
                p.MoveTo(cx - r * 0.24f, cy - r * 0.36f);
                p.LineTo(cx + r * 0.42f, cy);
                p.LineTo(cx - r * 0.24f, cy + r * 0.36f);
                p.Close();
                canvas.DrawPath(p, linePaint);
                break;

            case OneUiTabKind.Room3D:
                p.MoveTo(cx, cy - r * 0.94f);
                p.LineTo(cx + r * 0.84f, cy - r * 0.47f);
                p.LineTo(cx + r * 0.84f, cy + r * 0.47f);
                p.LineTo(cx, cy + r * 0.94f);
                p.LineTo(cx - r * 0.84f, cy + r * 0.47f);
                p.LineTo(cx - r * 0.84f, cy - r * 0.47f);
                p.Close();
                canvas.DrawPath(p, glassFillPaint);
                canvas.DrawPath(p, linePaint);
                canvas.DrawLine(cx, cy, cx + r * 0.84f, cy - r * 0.47f, linePaint);
                canvas.DrawLine(cx, cy, cx - r * 0.84f, cy - r * 0.47f, linePaint);
                canvas.DrawLine(cx, cy, cx, cy + r * 0.94f, linePaint);
                break;

            case OneUiTabKind.Studio:
                canvas.DrawRoundRect(outer, rr, rr, glassFillPaint);
                canvas.DrawRoundRect(outer, rr, rr, linePaint);
                canvas.DrawLine(cx - r * 0.42f, cy - r * 0.16f, cx - r * 0.42f, cy + r * 0.38f, linePaint);
                canvas.DrawLine(cx, cy - r * 0.44f, cx, cy + r * 0.38f, linePaint);
                canvas.DrawLine(cx + r * 0.42f, cy - r * 0.26f, cx + r * 0.42f, cy + r * 0.38f, linePaint);
                break;

            case OneUiTabKind.Stems:
                canvas.DrawRoundRect(new RectF(cx - r * 0.88f, cy - r * 0.86f, cx - r * 0.14f, cy + r * 0.86f), rr * 0.7f, rr * 0.7f, glassFillPaint);
                canvas.DrawRoundRect(new RectF(cx - r * 0.88f, cy - r * 0.86f, cx - r * 0.14f, cy + r * 0.86f), rr * 0.7f, rr * 0.7f, linePaint);
                canvas.DrawRoundRect(new RectF(cx + r * 0.14f, cy - r * 0.86f, cx + r * 0.88f, cy - r * 0.10f), rr * 0.7f, rr * 0.7f, glassFillPaint);
                canvas.DrawRoundRect(new RectF(cx + r * 0.14f, cy - r * 0.86f, cx + r * 0.88f, cy - r * 0.10f), rr * 0.7f, rr * 0.7f, linePaint);
                canvas.DrawRoundRect(new RectF(cx + r * 0.14f, cy + r * 0.12f, cx + r * 0.88f, cy + r * 0.86f), rr * 0.7f, rr * 0.7f, glassFillPaint);
                canvas.DrawRoundRect(new RectF(cx + r * 0.14f, cy + r * 0.12f, cx + r * 0.88f, cy + r * 0.86f), rr * 0.7f, rr * 0.7f, linePaint);
                break;

            case OneUiTabKind.Settings:
                canvas.DrawCircle(cx, cy, r * 0.72f, glassFillPaint);
                canvas.DrawCircle(cx, cy, r * 0.72f, linePaint);
                canvas.DrawCircle(cx, cy, r * 0.28f, linePaint);
                for (int i = 0; i < 6; i++) {
                    float a = i * MathF.PI / 3f;
                    canvas.DrawLine(
                        cx + MathF.Cos(a) * r * 0.72f, cy + MathF.Sin(a) * r * 0.72f,
                        cx + MathF.Cos(a) * r * 0.96f, cy + MathF.Sin(a) * r * 0.96f,
                        linePaint);
                }
                break;
        }
    }

    void BuildSelectedCutoutPath(Android.Graphics.Path path, float cx, float cy, float r) {
        RectF outer = new(cx - r * 0.92f, cy - r * 0.92f, cx + r * 0.92f, cy + r * 0.92f);
        float rr = r * 0.36f;

        switch (kind) {
            case OneUiTabKind.Player:
                path.AddRoundRect(outer, rr, rr, Android.Graphics.Path.Direction.Cw);
                path.MoveTo(cx - r * 0.24f, cy - r * 0.40f);
                path.LineTo(cx + r * 0.46f, cy);
                path.LineTo(cx - r * 0.24f, cy + r * 0.40f);
                path.Close();
                break;

            case OneUiTabKind.Room3D:
                path.MoveTo(cx, cy - r * 0.96f);
                path.LineTo(cx + r * 0.88f, cy - r * 0.48f);
                path.LineTo(cx + r * 0.88f, cy + r * 0.48f);
                path.LineTo(cx, cy + r * 0.96f);
                path.LineTo(cx - r * 0.88f, cy + r * 0.48f);
                path.LineTo(cx - r * 0.88f, cy - r * 0.48f);
                path.Close();
                // Cutout Y-axes inside solid hexagon cube
                float lw = r * 0.11f;
                path.AddCircle(cx, cy, r * 0.20f, Android.Graphics.Path.Direction.Cw);
                path.AddRoundRect(new RectF(cx - lw, cy + r * 0.16f, cx + lw, cy + r * 0.86f), lw, lw, Android.Graphics.Path.Direction.Cw);
                break;

            case OneUiTabKind.Studio:
                path.AddRoundRect(outer, rr, rr, Android.Graphics.Path.Direction.Cw);
                float bw = r * 0.12f;
                path.AddRoundRect(new RectF(cx - r * 0.46f - bw, cy - r * 0.16f, cx - r * 0.46f + bw, cy + r * 0.44f), bw, bw, Android.Graphics.Path.Direction.Cw);
                path.AddRoundRect(new RectF(cx - bw, cy - r * 0.46f, cx + bw, cy + r * 0.44f), bw, bw, Android.Graphics.Path.Direction.Cw);
                path.AddRoundRect(new RectF(cx + r * 0.46f - bw, cy - r * 0.28f, cx + r * 0.46f + bw, cy + r * 0.44f), bw, bw, Android.Graphics.Path.Direction.Cw);
                break;

            case OneUiTabKind.Stems:
                path.AddRoundRect(new RectF(cx - r * 0.90f, cy - r * 0.88f, cx - r * 0.10f, cy + r * 0.88f), rr * 0.65f, rr * 0.65f, Android.Graphics.Path.Direction.Cw);
                path.AddRoundRect(new RectF(cx + r * 0.12f, cy - r * 0.88f, cx + r * 0.90f, cy - r * 0.10f), rr * 0.65f, rr * 0.65f, Android.Graphics.Path.Direction.Cw);
                path.AddRoundRect(new RectF(cx + r * 0.12f, cy + r * 0.12f, cx + r * 0.90f, cy + r * 0.88f), rr * 0.65f, rr * 0.65f, Android.Graphics.Path.Direction.Cw);
                break;

            case OneUiTabKind.Settings:
                path.AddCircle(cx, cy, r * 0.78f, Android.Graphics.Path.Direction.Cw);
                for (int i = 0; i < 6; i++) {
                    float a = i * MathF.PI / 3f;
                    float tx = cx + MathF.Cos(a) * r * 0.82f;
                    float ty = cy + MathF.Sin(a) * r * 0.82f;
                    path.AddCircle(tx, ty, r * 0.20f, Android.Graphics.Path.Direction.Cw);
                }
                path.AddCircle(cx, cy, r * 0.34f, Android.Graphics.Path.Direction.Ccw);
                break;
        }
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
        LinearLayout pill = new(ctx) {
            Orientation = Orientation.Horizontal,
            BaselineAligned = false
        };
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
