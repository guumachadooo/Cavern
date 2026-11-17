using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavernize.Logic.Models;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive top-down 3D room canvas showing audio bed speakers, live moving 3D objects with elevation/RMS energy,
/// and a draggable listener with directional head orientation cone.
/// </summary>
public sealed class RoomSimulatorView : View {
    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint roomBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint screenPaint = new(PaintFlags.AntiAlias);
    readonly Paint bedPaint = new(PaintFlags.AntiAlias);
    readonly Paint lfePaint = new(PaintFlags.AntiAlias);
    readonly Paint objPaint = new(PaintFlags.AntiAlias);
    readonly Paint objGlowPaint = new(PaintFlags.AntiAlias);
    readonly Paint heightRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint mutedPaint = new(PaintFlags.AntiAlias);
    readonly Paint soloRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerConePaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero;
    DateTime lastTapTime = DateTime.MinValue;

    public event Action<Vector3> ListenerPositionChanged;

    public RoomSimulatorView(Context context) : base(context) {
        float dp = context.Resources.DisplayMetrics.Density;

        roomBgPaint.Color = Color.ParseColor("#0E131D");
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = Color.ParseColor("#334155");
        roomBorderPaint.StrokeWidth = 2f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        gridPaint.Color = Color.ParseColor("#1E293B");
        gridPaint.StrokeWidth = 1f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        screenPaint.Color = Color.ParseColor("#38BDF8");
        screenPaint.StrokeWidth = 4f * dp;
        screenPaint.SetStyle(Paint.Style.Stroke);

        bedPaint.Color = Color.ParseColor("#0EA5E9");
        bedPaint.SetStyle(Paint.Style.Fill);

        lfePaint.Color = Color.ParseColor("#A855F7");
        lfePaint.SetStyle(Paint.Style.Fill);

        objPaint.Color = Color.ParseColor("#F43F5E");
        objPaint.SetStyle(Paint.Style.Fill);

        objGlowPaint.Color = Color.Argb(75, 244, 63, 94);
        objGlowPaint.SetStyle(Paint.Style.Fill);

        heightRingPaint.Color = Color.ParseColor("#22D3EE");
        heightRingPaint.StrokeWidth = 2f * dp;
        heightRingPaint.SetStyle(Paint.Style.Stroke);

        mutedPaint.Color = Color.ParseColor("#475569");
        mutedPaint.SetStyle(Paint.Style.Fill);

        soloRingPaint.Color = Color.ParseColor("#FACC15");
        soloRingPaint.StrokeWidth = 2.5f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerPaint.Color = Color.ParseColor("#10B981");
        listenerPaint.SetStyle(Paint.Style.Fill);

        listenerConePaint.Color = Color.Argb(70, 16, 185, 129);
        listenerConePaint.SetStyle(Paint.Style.Fill);

        textPaint.Color = Color.White;
        textPaint.TextSize = 10.5f * dp;
        textPaint.TextAlign = Paint.Align.Center;

        subTextPaint.Color = Color.ParseColor("#94A3B8");
        subTextPaint.TextSize = 10f * dp;
        subTextPaint.TextAlign = Paint.Align.Left;
    }

    public void UpdateState(IReadOnlyList<TrackSourceItem> sourceItems, Vector3 envSize, Vector3 pos, Vector3 rot) {
        sources = sourceItems ?? [];
        if (envSize.X > 0.1f && envSize.Y > 0.1f && envSize.Z > 0.1f) {
            environmentSize = envSize;
        }
        listenerPosition = pos;
        listenerRotation = rot;
        PostInvalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int desiredH = (int)(w * 0.78f);
        if (desiredH < 360) {
            desiredH = 360;
        }
        SetMeasuredDimension(w, ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float dp = Resources.DisplayMetrics.Density;
        float pad = 18f * dp;
        float w = Width - pad * 2;
        float h = Height - pad * 2;
        if (w <= 0 || h <= 0) {
            return;
        }

        RectF rect = new(pad, pad, pad + w, pad + h);
        canvas.DrawRoundRect(rect, 14f * dp, 14f * dp, roomBgPaint);
        canvas.DrawRoundRect(rect, 14f * dp, 14f * dp, roomBorderPaint);

        // Grid lines
        for (int i = 1; i <= 3; i++) {
            float gx = pad + w * (i / 4f);
            float gy = pad + h * (i / 4f);
            canvas.DrawLine(gx, pad, gx, pad + h, gridPaint);
            canvas.DrawLine(pad, gy, pad + w, gy, gridPaint);
        }

        // Front screen bar (+Z top)
        float screenHalf = w * 0.34f;
        float cx = pad + w * 0.5f;
        canvas.DrawLine(cx - screenHalf, pad + 3f * dp, cx + screenHalf, pad + 3f * dp, screenPaint);
        canvas.DrawText("TELA / FRENTE (+Z)", cx, pad + 15f * dp, subTextPaint.CenterCopy());

        // Draw audio beds & 3D objects
        bool anySolo = false;
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].IsSolo) {
                anySolo = true;
                break;
            }
        }

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            Vector3 np = item.NormalizedPosition;
            float px = pad + Math.Clamp((np.X + 1f) * 0.5f, 0.03f, 0.97f) * w;
            float py = pad + Math.Clamp(1f - (np.Z + 1f) * 0.5f, 0.05f, 0.95f) * h;

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(item.RmsLevel * 18f, 0f, 12f) * dp;

            if (item.IsBed) {
                float sz = 7f * dp;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 1f) {
                    canvas.DrawCircle(px, py, sz + rmsBoost, objGlowPaint.WithAlphaColor(Color.Argb(65, 14, 165, 233)));
                }
                canvas.DrawRoundRect(px - sz, py - sz, px + sz, py + sz, 3f * dp, 3f * dp, p);
                if (np.Y > 0.25f) {
                    canvas.DrawCircle(px, py, sz + 4f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(px, py, sz + 5f * dp, soloRingPaint);
                }
                string shortLabel = item.Channel.HasValue && item.Channel.Value != Cavern.Channels.ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                canvas.DrawText(shortLabel, px, py + sz + 11f * dp, textPaint);
            } else {
                float r = 8.5f * dp;
                if (!effectivelyMuted && rmsBoost > 0.5f) {
                    canvas.DrawCircle(px, py, r + rmsBoost, objGlowPaint);
                }
                canvas.DrawCircle(px, py, r, effectivelyMuted ? mutedPaint : objPaint);
                if (np.Y > 0.15f) {
                    canvas.DrawCircle(px, py, r + 3.5f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(px, py, r + 5f * dp, soloRingPaint);
                }
                string label = $"O{i + 1}";
                if (np.Y > 0.25f) {
                    label += "↑";
                } else if (np.Y < -0.25f) {
                    label += "↓";
                }
                canvas.DrawText(label, px, py - r - 4f * dp, textPaint);
            }
        }

        // Draw listener + head FOV cone
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        float lx = pad + (lnx + 1f) * 0.5f * w;
        float ly = pad + (1f - (lnz + 1f) * 0.5f) * h;

        float yawRad = listenerRotation.Y * (MathF.PI / 180f);
        float coneLen = 46f * dp;
        float spread = 0.45f; // ~26 deg half-angle

        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lx, ly);
            cone.LineTo(lx + MathF.Sin(yawRad - spread) * coneLen, ly - MathF.Cos(yawRad - spread) * coneLen);
            cone.LineTo(lx + MathF.Sin(yawRad + spread) * coneLen, ly - MathF.Cos(yawRad + spread) * coneLen);
            cone.Close();
            canvas.DrawPath(cone, listenerConePaint);
        }

        float lr = 12f * dp;
        canvas.DrawCircle(lx, ly, lr + 3f * dp, soloRingPaint.WithColor(Color.ParseColor("#6EE7B7")));
        canvas.DrawCircle(lx, ly, lr, listenerPaint);

        // Nose indicator
        float nxDir = lx + MathF.Sin(yawRad) * (lr + 4f * dp);
        float nyDir = ly - MathF.Cos(yawRad) * (lr + 4f * dp);
        canvas.DrawLine(lx, ly, nxDir, nyDir, roomBorderPaint.WithColor(Color.White));
        canvas.DrawText("VOCÊ", lx, ly + 4f * dp, textPaint);

        // Telemetry overlay
        string hud = $"Pos: ({listenerPosition.X:0.0}, {listenerPosition.Y:0.0}, {listenerPosition.Z:0.0})  Yaw: {listenerRotation.Y:0}°";
        canvas.DrawText(hud, pad + 8f * dp, pad + h - 8f * dp, subTextPaint);
    }

    public override bool OnTouchEvent(MotionEvent e) {
        float dp = Resources.DisplayMetrics.Density;
        float pad = 18f * dp;
        float w = Width - pad * 2;
        float h = Height - pad * 2;
        if (w <= 0 || h <= 0) {
            return base.OnTouchEvent(e);
        }

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                Parent?.RequestDisallowInterceptTouchEvent(true);
                DateTime now = DateTime.UtcNow;
                if ((now - lastTapTime).TotalMilliseconds < 280) {
                    listenerPosition = new Vector3(0, listenerPosition.Y, 0);
                    ListenerPositionChanged?.Invoke(listenerPosition);
                    Invalidate();
                    lastTapTime = DateTime.MinValue;
                    return true;
                }
                lastTapTime = now;
                UpdateListenerFromTouch(e.GetX(), e.GetY(), pad, w, h);
                return true;

            case MotionEventActions.Move:
                UpdateListenerFromTouch(e.GetX(), e.GetY(), pad, w, h);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                return true;
        }
        return base.OnTouchEvent(e);
    }

    void UpdateListenerFromTouch(float tx, float ty, float pad, float w, float h) {
        float nx = Math.Clamp((tx - pad) / w * 2f - 1f, -1f, 1f);
        float nz = Math.Clamp((1f - (ty - pad) / h) * 2f - 1f, -1f, 1f);
        listenerPosition = new Vector3(nx * environmentSize.X, listenerPosition.Y, nz * environmentSize.Z);
        ListenerPositionChanged?.Invoke(listenerPosition);
        Invalidate();
    }
}

internal static class PaintExtensions {
    public static Paint CenterCopy(this Paint p) {
        p.TextAlign = Paint.Align.Center;
        return p;
    }

    public static Paint WithColor(this Paint p, Color c) {
        p.Color = c;
        return p;
    }

    public static Paint WithAlphaColor(this Paint p, Color c) {
        p.Color = c;
        return p;
    }
}
