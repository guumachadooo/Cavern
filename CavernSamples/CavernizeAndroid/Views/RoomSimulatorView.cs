using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavern.Channels;
using Cavernize.Logic.Models;
using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive top-down 3D room canvas styled for Samsung One UI 9 showing audio bed speakers,
/// live moving 3D objects with elevation/RMS energy, and a scroll-safe draggable listener avatar.
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
    readonly Paint listenerRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerConePaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);
    readonly Paint hudChipPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero;
    DateTime lastTapTime = DateTime.MinValue;

    bool isDraggingListener;
    float downX;
    float downY;
    float maxMoveDist;

    public bool IsPositionLocked { get; set; }

    public event Action<Vector3> ListenerPositionChanged;

    public RoomSimulatorView(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        roomBgPaint.Color = OneUiTheme.RoomBg;
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = OneUiTheme.Divider;
        roomBorderPaint.StrokeWidth = 1.5f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        gridPaint.Color = Color.ParseColor("#1D1D22");
        gridPaint.StrokeWidth = 1f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        screenPaint.Color = OneUiTheme.Primary;
        screenPaint.StrokeWidth = 4f * dp;
        screenPaint.StrokeCap = Paint.Cap.Round;
        screenPaint.SetStyle(Paint.Style.Stroke);

        bedPaint.Color = OneUiTheme.BedChannel;
        bedPaint.SetStyle(Paint.Style.Fill);

        lfePaint.Color = OneUiTheme.LfeChannel;
        lfePaint.SetStyle(Paint.Style.Fill);

        objPaint.Color = OneUiTheme.DynamicObject;
        objPaint.SetStyle(Paint.Style.Fill);

        objGlowPaint.Color = Color.Argb(70, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B);
        objGlowPaint.SetStyle(Paint.Style.Fill);

        heightRingPaint.Color = OneUiTheme.FunctionalGreen;
        heightRingPaint.StrokeWidth = 2f * dp;
        heightRingPaint.SetStyle(Paint.Style.Stroke);

        mutedPaint.Color = OneUiTheme.SurfaceBlurTint;
        mutedPaint.SetStyle(Paint.Style.Fill);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.5f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerPaint.Color = OneUiTheme.Primary;
        listenerPaint.SetStyle(Paint.Style.Fill);

        listenerRingPaint.Color = OneUiTheme.PrimaryHigh;
        listenerRingPaint.StrokeWidth = 2.5f * dp;
        listenerRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.Color = Color.Argb(75, OneUiTheme.Primary.R, OneUiTheme.Primary.G, OneUiTheme.Primary.B);
        listenerConePaint.SetStyle(Paint.Style.Fill);

        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.TextSize = 10f * dp;
        textPaint.TextAlign = Paint.Align.Center;

        subTextPaint.Color = OneUiTheme.TextMuted;
        subTextPaint.TextSize = 10f * dp;
        subTextPaint.TextAlign = Paint.Align.Left;

        hudChipPaint.Color = Color.Argb(180, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
        hudChipPaint.SetStyle(Paint.Style.Fill);
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
        int desiredH = (int)(w * 0.74f);
        if (desiredH < 340) {
            desiredH = 340;
        }
        SetMeasuredDimension(w, ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float dp = Resources.DisplayMetrics.Density;
        float pad = 14f * dp;
        float w = Width - pad * 2;
        float h = Height - pad * 2;
        if (w <= 0 || h <= 0) {
            return;
        }

        RectF rect = new(pad, pad, pad + w, pad + h);
        float corner = 22f * dp;
        canvas.DrawRoundRect(rect, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(rect, corner, corner, roomBorderPaint);

        // Subtle acoustic room grid
        for (int i = 1; i <= 3; i++) {
            float gx = pad + w * (i / 4f);
            float gy = pad + h * (i / 4f);
            canvas.DrawLine(gx, pad, gx, pad + h, gridPaint);
            canvas.DrawLine(pad, gy, pad + w, gy, gridPaint);
        }

        // Front screen bar (+Z top)
        float screenHalf = w * 0.30f;
        float cx = pad + w * 0.5f;
        canvas.DrawLine(cx - screenHalf, pad + 5f * dp, cx + screenHalf, pad + 5f * dp, screenPaint);
        canvas.DrawText("FRENTE / TELA (+Z)", cx, pad + 18f * dp, subTextPaint.CenterCopy());

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
            float px = pad + Math.Clamp((np.X + 1f) * 0.5f, 0.04f, 0.96f) * w;
            float py = pad + Math.Clamp(1f - (np.Z + 1f) * 0.5f, 0.07f, 0.93f) * h;

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(item.RmsLevel * 18f, 0f, 12f) * dp;

            if (item.IsBed) {
                float sz = 7f * dp;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 1f) {
                    canvas.DrawCircle(px, py, sz + rmsBoost, objGlowPaint.WithColor(Color.Argb(65, p.Color.R, p.Color.G, p.Color.B)));
                }
                canvas.DrawRoundRect(px - sz, py - sz, px + sz, py + sz, 4f * dp, 4f * dp, p);
                if (np.Y > 0.25f) {
                    canvas.DrawCircle(px, py, sz + 3.5f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(px, py, sz + 5f * dp, soloRingPaint);
                }
                string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                canvas.DrawText(shortLabel, px, py + sz + 11f * dp, textPaint);
            } else {
                float r = 8.5f * dp;
                if (!effectivelyMuted && rmsBoost > 0.5f) {
                    canvas.DrawCircle(px, py, r + rmsBoost, objGlowPaint.WithColor(Color.Argb(75, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B)));
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
        (float lx, float ly) = GetListenerCanvasCoords(pad, w, h);

        float yawRad = listenerRotation.Y * (MathF.PI / 180f);
        float coneLen = 46f * dp;
        float spread = 0.44f;

        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lx, ly);
            cone.LineTo(lx + MathF.Sin(yawRad - spread) * coneLen, ly - MathF.Cos(yawRad - spread) * coneLen);
            cone.LineTo(lx + MathF.Sin(yawRad + spread) * coneLen, ly - MathF.Cos(yawRad + spread) * coneLen);
            cone.Close();
            canvas.DrawPath(cone, listenerConePaint);
        }

        float lr = 13.5f * dp;
        canvas.DrawCircle(lx, ly, lr + 3.5f * dp, listenerRingPaint);
        canvas.DrawCircle(lx, ly, lr, listenerPaint);

        // Nose / heading direction indicator
        float nxDir = lx + MathF.Sin(yawRad) * (lr + 5f * dp);
        float nyDir = ly - MathF.Cos(yawRad) * (lr + 5f * dp);
        canvas.DrawLine(lx, ly, nxDir, nyDir, roomBorderPaint.WithColor(OneUiTheme.TextPrimary));
        canvas.DrawText("VOCÊ", lx, ly + 3.8f * dp, textPaint);

        // Bottom HUD telemetry pill
        string lockState = IsPositionLocked ? "🔒" : "↔";
        string hud = $"{lockState} ({listenerPosition.X:0.0}, {listenerPosition.Y:0.0}, {listenerPosition.Z:0.0})  Yaw {listenerRotation.Y:0}°";
        float hudWidth = Math.Min(w - 16f * dp, subTextPaint.MeasureText(hud) + 18f * dp);
        RectF chip = new(pad + 8f * dp, pad + h - 24f * dp, pad + 8f * dp + hudWidth, pad + h - 6f * dp);
        canvas.DrawRoundRect(chip, 10f * dp, 10f * dp, hudChipPaint);
        subTextPaint.TextAlign = Paint.Align.Left;
        canvas.DrawText(hud, pad + 16f * dp, pad + h - 11.5f * dp, subTextPaint);
    }

    (float lx, float ly) GetListenerCanvasCoords(float pad, float w, float h) {
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        float lx = pad + (lnx + 1f) * 0.5f * w;
        float ly = pad + (1f - (lnz + 1f) * 0.5f) * h;
        return (lx, ly);
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (IsPositionLocked) {
            return false;
        }

        float dp = Resources.DisplayMetrics.Density;
        float pad = 14f * dp;
        float w = Width - pad * 2;
        float h = Height - pad * 2;
        if (w <= 0 || h <= 0) {
            return false;
        }

        float tx = e.GetX();
        float ty = e.GetY();

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = tx;
                downY = ty;
                maxMoveDist = 0f;
                (float lx, float ly) = GetListenerCanvasCoords(pad, w, h);
                float hitDist = MathF.Sqrt((tx - lx) * (tx - lx) + (ty - ly) * (ty - ly));

                // Only hijack ScrollView immediately when touching directly on the listener avatar.
                if (hitDist <= 40f * dp) {
                    isDraggingListener = true;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromTouch(tx, ty, pad, w, h);
                } else {
                    isDraggingListener = false;
                }
                return true;

            case MotionEventActions.Move:
                float dx = tx - downX;
                float dy = ty - downY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > maxMoveDist) {
                    maxMoveDist = dist;
                }

                if (isDraggingListener) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromTouch(tx, ty, pad, w, h);
                    return true;
                }

                // If finger moves vertically outside the avatar, yield cleanly to ScrollView!
                if (Math.Abs(dy) > touchSlop && Math.Abs(dy) > Math.Abs(dx)) {
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    return false;
                }

                // Deliberate horizontal drag inside room
                if (Math.Abs(dx) > touchSlop * 1.5f && Math.Abs(dx) > Math.Abs(dy) * 1.5f) {
                    isDraggingListener = true;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromTouch(tx, ty, pad, w, h);
                    return true;
                }
                return true;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                if (maxMoveDist < touchSlop) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTapTime).TotalMilliseconds < 280) {
                        listenerPosition = new Vector3(0, listenerPosition.Y, 0);
                        ListenerPositionChanged?.Invoke(listenerPosition);
                        Invalidate();
                        lastTapTime = DateTime.MinValue;
                    } else {
                        lastTapTime = now;
                        if (!isDraggingListener) {
                            UpdateListenerFromTouch(tx, ty, pad, w, h);
                        }
                    }
                }
                isDraggingListener = false;
                return true;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                isDraggingListener = false;
                return false;
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
}
