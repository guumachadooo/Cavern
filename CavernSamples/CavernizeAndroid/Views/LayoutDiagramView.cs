using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavern.Channels;

using Cavernize.Logic.Models.RenderTargets;

namespace CavernizeAndroid.Views;

/// <summary>
/// Android port of Cavern.WPF's StandardLayoutDisplay showing active/inactive/dynamic speaker placements.
/// </summary>
public sealed class LayoutDiagramView : View {
    readonly Paint boxPaint = new(PaintFlags.AntiAlias);
    readonly Paint linePaint = new(PaintFlags.AntiAlias);
    readonly Paint speakerPaint = new(PaintFlags.AntiAlias);
    readonly Paint labelPaint = new(PaintFlags.AntiAlias);

    readonly Dictionary<ReferenceChannel, Color> channelColors = new();
    bool isDynamicMode;

    static readonly (ReferenceChannel ch, float x, float y, bool isTop)[] speakerLayout = [
        (ReferenceChannel.FrontLeft, 51, 30, false),
        (ReferenceChannel.FrontCenter, 79, 30, false),
        (ReferenceChannel.FrontRight, 107, 30, false),
        (ReferenceChannel.TopFrontLeft, 51, 5, true),
        (ReferenceChannel.TopFrontCenter, 79, 5, true),
        (ReferenceChannel.TopFrontRight, 107, 5, true),
        (ReferenceChannel.WideLeft, 36, 40, false),
        (ReferenceChannel.WideRight, 126, 40, false),
        (ReferenceChannel.SideLeft, 20, 91, false),
        (ReferenceChannel.SideRight, 142, 91, false),
        (ReferenceChannel.TopSideLeft, 29, 53, true),
        (ReferenceChannel.TopSideRight, 129, 53, true),
        (ReferenceChannel.TopRearLeft, 46, 62, true),
        (ReferenceChannel.TopRearCenter, 79, 62, true),
        (ReferenceChannel.TopRearRight, 112, 62, true),
        (ReferenceChannel.RearLeft, 34, 118, false),
        (ReferenceChannel.RearCenter, 79, 118, false),
        (ReferenceChannel.RearRight, 124, 118, false),
    ];

    public LayoutDiagramView(Context context) : base(context) {
        float dp = context.Resources.DisplayMetrics.Density;
        boxPaint.Color = Color.ParseColor("#94A3B8");
        boxPaint.StrokeWidth = 1.5f * dp;
        boxPaint.SetStyle(Paint.Style.Stroke);

        linePaint.Color = Color.ParseColor("#475569");
        linePaint.StrokeWidth = 1.2f * dp;
        linePaint.SetStyle(Paint.Style.Stroke);

        speakerPaint.SetStyle(Paint.Style.Fill);

        labelPaint.Color = Color.ParseColor("#CBD5E1");
        labelPaint.TextSize = 9.5f * dp;
        labelPaint.TextAlign = Paint.Align.Center;
    }

    public void SetRenderTarget(RenderTarget target) {
        channelColors.Clear();
        if (target is DriverRenderTarget || target is VirtualizerRenderTarget) {
            isDynamicMode = true;
            Color dyn = Color.ParseColor("#38BDF8");
            foreach (var s in speakerLayout) {
                channelColors[s.ch] = dyn;
            }
        } else {
            isDynamicMode = false;
            Color inactive = Color.ParseColor("#475569");
            Color active = Color.ParseColor("#10B981");
            foreach (var s in speakerLayout) {
                channelColors[s.ch] = inactive;
            }
            if (target != null) {
                ReferenceChannel[] channels = target.Channels;
                for (int i = 0; i < channels.Length; i++) {
                    if (target.IsExported(i) && channels[i] != ReferenceChannel.ScreenLFE) {
                        channelColors[channels[i]] = active;
                    }
                }
            }
        }
        Invalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = Resources.DisplayMetrics.Density;
        int w = ResolveSize((int)(180 * dp), widthMeasureSpec);
        int h = ResolveSize((int)(168 * dp), heightMeasureSpec);
        SetMeasuredDimension(w, h);
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float scaleX = Width / 170f;
        float scaleY = Height / 164f;
        float scale = Math.Min(scaleX, scaleY);
        float offX = (Width - 170f * scale) * 0.5f;
        float offY = (Height - 160f * scale) * 0.5f;

        float X(float v) => offX + v * scale;
        float Y(float v) => offY + v * scale;

        // Top layer rect & ground layer rect (from StandardLayoutDisplay.xaml)
        canvas.DrawRect(X(46), Y(0), X(46 + 76), Y(50), boxPaint);
        canvas.DrawRect(X(14), Y(73), X(14 + 140), Y(73 + 85), linePaint);

        // Perspective lines
        canvas.DrawLine(X(46), Y(0), X(14), Y(73), linePaint);
        canvas.DrawLine(X(122), Y(0), X(154), Y(73), linePaint);
        canvas.DrawLine(X(46), Y(50), X(14), Y(158), linePaint);
        canvas.DrawLine(X(122), Y(50), X(154), Y(158), linePaint);

        foreach (var sp in speakerLayout) {
            speakerPaint.Color = channelColors.TryGetValue(sp.ch, out Color col) ? col : Color.Gray;
            float r = (sp.isTop ? 4.5f : 5.5f) * scale;
            canvas.DrawCircle(X(sp.x + 5), Y(sp.y + 5), r, speakerPaint);
        }

        if (isDynamicMode) {
            canvas.DrawText("3D / Dynamic", Width * 0.5f, Height - 4f, labelPaint);
        }
    }
}
