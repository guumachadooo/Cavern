using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavern.Channels;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive top-down 3D room canvas for One UI 9 featuring:
/// - 3D-projected listener head sphere with live Yaw + Pitch tilt &amp; headphones
/// - Corner 3D Head Gimbal widget to rotate Yaw and tilt Pitch directly by touch
/// - Direct tap-to-Solo on any 3D object or speaker on the room map
/// - Scroll-safe touch handling &amp; crisp vector HUD chip (zero emojis)
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
    readonly Paint sphereWirePaint = new(PaintFlags.AntiAlias);
    readonly Paint sphereWireBackPaint = new(PaintFlags.AntiAlias);
    readonly Paint gimbalBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint gimbalBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint nosePaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);
    readonly Paint hudChipPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y)> hitTargets = [];

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero; // X=Pitch (-90..90), Y=Yaw (-180..180)
    DateTime lastRoomTapTime = DateTime.MinValue;
    DateTime lastGimbalTapTime = DateTime.MinValue;

    enum TouchDragMode {
        None,
        ListenerPos,
        HeadGimbal
    }

    TouchDragMode dragMode = TouchDragMode.None;
    float downX;
    float downY;
    float lastX;
    float lastY;
    float maxMoveDist;

    public bool IsPositionLocked { get; set; }

    public event Action<Vector3> ListenerPositionChanged;
    public event Action<Vector3> ListenerRotationChanged;
    public event Action<TrackSourceItem> SourceSoloToggled;

    public RoomSimulatorView(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        roomBgPaint.Color = OneUiTheme.RoomBg;
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = OneUiTheme.Divider;
        roomBorderPaint.StrokeWidth = 1.4f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        gridPaint.Color = Color.Argb(38, 255, 255, 255);
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

        objGlowPaint.SetStyle(Paint.Style.Fill);

        heightRingPaint.Color = OneUiTheme.FunctionalGreen;
        heightRingPaint.StrokeWidth = 2f * dp;
        heightRingPaint.SetStyle(Paint.Style.Stroke);

        mutedPaint.Color = OneUiTheme.SurfaceBlurTint;
        mutedPaint.SetStyle(Paint.Style.Fill);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.6f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerPaint.SetStyle(Paint.Style.Fill);

        listenerRingPaint.Color = OneUiTheme.PrimaryHigh;
        listenerRingPaint.StrokeWidth = 2f * dp;
        listenerRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.SetStyle(Paint.Style.Fill);

        sphereWirePaint.Color = Color.Argb(205, 245, 248, 255);
        sphereWirePaint.StrokeWidth = 1.3f * dp;
        sphereWirePaint.SetStyle(Paint.Style.Stroke);

        sphereWireBackPaint.Color = Color.Argb(68, 220, 230, 255);
        sphereWireBackPaint.StrokeWidth = 1f * dp;
        sphereWireBackPaint.SetStyle(Paint.Style.Stroke);

        gimbalBgPaint.Color = Color.Argb(200, 18, 20, 30);
        gimbalBgPaint.SetStyle(Paint.Style.Fill);

        gimbalBorderPaint.Color = Color.Argb(130, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        gimbalBorderPaint.StrokeWidth = 1.5f * dp;
        gimbalBorderPaint.SetStyle(Paint.Style.Stroke);

        nosePaint.Color = Color.White;
        nosePaint.StrokeWidth = 2.4f * dp;
        nosePaint.StrokeCap = Paint.Cap.Round;
        nosePaint.SetStyle(Paint.Style.Stroke);

        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.TextSize = 10f * dp;
        textPaint.TextAlign = Paint.Align.Center;

        subTextPaint.Color = OneUiTheme.TextMuted;
        subTextPaint.TextSize = 9.5f * dp;
        subTextPaint.TextAlign = Paint.Align.Left;

        hudChipPaint.Color = Color.Argb(205, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
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
        int desiredH = (int)(w * 0.78f);
        if (desiredH < 360) {
            desiredH = 360;
        }
        SetMeasuredDimension(w, ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float dp = Resources.DisplayMetrics.Density;
        float pad = 12f * dp;
        float w = Width - pad * 2f;
        float h = Height - pad * 2f;
        if (w <= 0 || h <= 0) {
            return;
        }

        RectF rect = new(pad, pad, pad + w, pad + h);
        float corner = 24f * dp;
        canvas.DrawRoundRect(rect, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(rect, corner, corner, roomBorderPaint);

        // Acoustic perspective room grid
        for (int i = 1; i <= 3; i++) {
            float gx = pad + w * (i / 4f);
            float gy = pad + h * (i / 4f);
            canvas.DrawLine(gx, pad, gx, pad + h, gridPaint);
            canvas.DrawLine(pad, gy, pad + w, gy, gridPaint);
        }

        // Front screen bar (+Z top)
        float screenHalf = w * 0.26f;
        float cx = pad + w * 0.5f;
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(cx - screenHalf, pad + 5f * dp, cx + screenHalf, pad + 5f * dp, screenPaint);
        canvas.DrawText("FRENTE / TELA (+Z)", cx, pad + 18f * dp, subTextPaint.CenterCopy());

        // Draw audio beds & 3D objects, caching hit coordinates for Tap-to-Solo
        hitTargets.Clear();
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
            float px = pad + Math.Clamp((np.X + 1f) * 0.5f, 0.05f, 0.95f) * w;
            float py = pad + Math.Clamp(1f - (np.Z + 1f) * 0.5f, 0.09f, 0.91f) * h;
            hitTargets.Add((item, px, py));

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(item.RmsLevel * 18f, 0f, 12f) * dp;

            if (item.IsBed) {
                float sz = 7.5f * dp;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 1f) {
                    canvas.DrawCircle(px, py, sz + rmsBoost, objGlowPaint.WithColor(Color.Argb(65, p.Color.R, p.Color.G, p.Color.B)));
                }
                canvas.DrawRoundRect(px - sz, py - sz, px + sz, py + sz, 4.5f * dp, 4.5f * dp, p);
                if (np.Y > 0.25f) {
                    canvas.DrawCircle(px, py, sz + 3.5f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(px, py, sz + 5.5f * dp, soloRingPaint);
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
                    canvas.DrawCircle(px, py, r + 5.5f * dp, soloRingPaint);
                }
                string label = $"O{i + 1}";
                if (np.Y > 0.25f) {
                    label += " ^";
                } else if (np.Y < -0.25f) {
                    label += " v";
                }
                canvas.DrawText(label, px, py - r - 4f * dp, textPaint);
            }
        }

        // Draw 3D Head Sphere Listener Avatar + acoustic FOV cone
        (float lx, float ly) = GetListenerCanvasCoords(pad, w, h);
        float yawRad = listenerRotation.Y * (MathF.PI / 180f);
        float pitchRad = listenerRotation.X * (MathF.PI / 180f);
        float pitchForeshorten = Math.Clamp(MathF.Cos(pitchRad), 0.35f, 1f);

        float coneLen = 48f * dp * (0.55f + 0.45f * pitchForeshorten);
        float spread = 0.44f;
        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lx, ly);
            cone.LineTo(lx + MathF.Sin(yawRad - spread) * coneLen, ly - MathF.Cos(yawRad - spread) * coneLen);
            cone.LineTo(lx + MathF.Sin(yawRad + spread) * coneLen, ly - MathF.Cos(yawRad + spread) * coneLen);
            cone.Close();
            using LinearGradient coneGrad = new(
                lx, ly,
                lx + MathF.Sin(yawRad) * coneLen, ly - MathF.Cos(yawRad) * coneLen,
                [Color.Argb(110, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f],
                Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        float headR = 16.5f * dp;
        Draw3DHeadSphere(canvas, lx, ly, headR, yawRad, pitchRad, topDownAvatar: true, dp);

        // Top-Right Interactive 3D Head Gimbal Widget (direct graphical Yaw & Pitch control)
        (float gCx, float gCy, float gR) = GetGimbalGeometry(pad, w, h, dp);
        DrawHeadGimbalWidget(canvas, gCx, gCy, gR, yawRad, pitchRad, dp);

        // Bottom-Left HUD Telemetry Pill (vector lock icon + coordinates, zero emojis)
        float chipH = 22f * dp;
        float chipY = pad + h - chipH - 7f * dp;
        float chipX = pad + 8f * dp;
        string hud = $"({listenerPosition.X:0.0}, {listenerPosition.Y:0.0}, {listenerPosition.Z:0.0})  Y:{listenerRotation.Y:0}° P:{listenerRotation.X:0}°";
        float hudWidth = Math.Min(w - 16f * dp, subTextPaint.MeasureText(hud) + 34f * dp);
        RectF chip = new(chipX, chipY, chipX + hudWidth, chipY + chipH);
        canvas.DrawRoundRect(chip, chipH * 0.5f, chipH * 0.5f, hudChipPaint);

        OneUiVectorIcons.DrawIcon(
            canvas,
            IsPositionLocked ? OneUiIconType.Lock : OneUiIconType.Unlock,
            chipX + 12f * dp,
            chipY + chipH * 0.5f,
            12f * dp,
            IsPositionLocked ? OneUiTheme.PrimaryHigh : OneUiTheme.TextPrimary);

        subTextPaint.TextAlign = Paint.Align.Left;
        canvas.DrawText(hud, chipX + 22f * dp, chipY + chipH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f, subTextPaint);
    }

    void DrawHeadGimbalWidget(Canvas canvas, float cx, float cy, float r, float yawRad, float pitchRad, float dp) {
        RectF lensRect = new(cx - r, cy - r, cx + r, cy + r);
        canvas.DrawRoundRect(lensRect, 18f * dp, 18f * dp, gimbalBgPaint);
        canvas.DrawRoundRect(lensRect, 18f * dp, 18f * dp, gimbalBorderPaint);

        Draw3DHeadSphere(canvas, cx, cy - 4f * dp, r * 0.62f, yawRad, pitchRad, topDownAvatar: false, dp);

        textPaint.TextSize = 8.5f * dp;
        textPaint.Color = OneUiTheme.PeachText;
        canvas.DrawText($"3D Y:{listenerRotation.Y:0}° P:{listenerRotation.X:0}°", cx, cy + r - 5f * dp, textPaint);
        textPaint.TextSize = 10f * dp;
        textPaint.Color = OneUiTheme.TextPrimary;
    }

    void Draw3DHeadSphere(Canvas canvas, float cx, float cy, float r, float yawRad, float pitchRad, bool topDownAvatar, float dp) {
        Color p = OneUiTheme.Primary;
        Color hi = OneUiTheme.PrimaryHigh;
        Color dk = OneUiTheme.PrimaryPressed;

        // 1. 3D shaded sphere body
        using (RadialGradient sphereGrad = new(
            cx - r * 0.28f, cy - r * 0.28f, r * 1.15f,
            [Color.Argb(245, hi.R, hi.G, hi.B).ToArgb(), Color.Argb(235, p.R, p.G, p.B).ToArgb(), Color.Argb(240, dk.R, dk.G, dk.B).ToArgb()],
            [0f, 0.55f, 1f],
            Shader.TileMode.Clamp)) {
            listenerPaint.SetShader(sphereGrad);
            canvas.DrawCircle(cx, cy, r, listenerPaint);
            listenerPaint.SetShader(null);
        }
        canvas.DrawCircle(cx, cy, r, listenerRingPaint);

        // 3D rotation basis: For topDownAvatar we view mostly top-down with visible pitch tilt;
        // for the corner 3D Gimbal we view in front-3/4 perspective so Pitch tilts noticeably up/down!
        float viewTilt = topDownAvatar ? 0.55f : 0.18f;

        Vector3 ProjectPoint(float x, float y, float z) {
            // Pitch around head's X axis, then Yaw around world Y axis
            float cosP = MathF.Cos(pitchRad);
            float sinP = MathF.Sin(pitchRad);
            float y1 = y * cosP - z * sinP;
            float z1 = y * sinP + z * cosP;

            float cosY = MathF.Cos(yawRad);
            float sinY = MathF.Sin(yawRad);
            float x2 = x * cosY + z1 * sinY;
            float z2 = -x * sinY + z1 * cosY;

            // Camera pitch looking slightly down onto the room
            float cosV = MathF.Cos(viewTilt);
            float sinV = MathF.Sin(viewTilt);
            float sy = y1 * cosV - z2 * sinV;
            float depth = y1 * sinV + z2 * cosV;
            return new Vector3(cx + x2 * r, cy - sy * r, depth);
        }

        // 2. Draw 3D wireframe latitude & longitude rings
        void Draw3DRing(Func<float, Vector3> ringFn) {
            const int segs = 24;
            Vector3 prev = ProjectPoint(ringFn(0).X, ringFn(0).Y, ringFn(0).Z);
            for (int i = 1; i <= segs; i++) {
                float a = i / (float)segs * MathF.PI * 2f;
                Vector3 pt = ringFn(a);
                Vector3 cur = ProjectPoint(pt.X, pt.Y, pt.Z);
                Paint wp = (prev.Z + cur.Z) >= 0 ? sphereWirePaint : sphereWireBackPaint;
                canvas.DrawLine(prev.X, prev.Y, cur.X, cur.Y, wp);
                prev = cur;
            }
        }

        // Equator ring (horizon of head)
        Draw3DRing(a => new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
        // Median sagittal ring (nose plane)
        Draw3DRing(a => new Vector3(0f, MathF.Sin(a), MathF.Cos(a)));

        // 3. Headphone earcups on (-1,0,0) and (+1,0,0)
        Vector3 leftEar = ProjectPoint(-1.04f, 0f, 0f);
        Vector3 rightEar = ProjectPoint(1.04f, 0f, 0f);
        Paint earPaint = objGlowPaint.WithColor(Color.Argb(240, 252, 252, 255));
        canvas.DrawCircle(leftEar.X, leftEar.Y, r * 0.24f, earPaint);
        canvas.DrawCircle(rightEar.X, rightEar.Y, r * 0.24f, earPaint);

        // 4. Nose / Gaze vector pointing out +Zhead (shows exact 3D Yaw + Pitch facing)
        Vector3 noseBase = ProjectPoint(0f, 0f, 0.72f);
        Vector3 noseTip = ProjectPoint(0f, 0f, 1.42f);
        canvas.DrawLine(noseBase.X, noseBase.Y, noseTip.X, noseTip.Y, nosePaint);
        canvas.DrawCircle(noseTip.X, noseTip.Y, r * 0.20f, objGlowPaint.WithColor(Color.White));
    }

    (float cx, float cy, float r) GetGimbalGeometry(float pad, float w, float h, float dp) {
        float gR = 34f * dp;
        float gCx = pad + w - gR - 8f * dp;
        float gCy = pad + gR + 8f * dp;
        return (gCx, gCy, gR);
    }

    (float lx, float ly) GetListenerCanvasCoords(float pad, float w, float h) {
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        float lx = pad + (lnx + 1f) * 0.5f * w;
        float ly = pad + (1f - (lnz + 1f) * 0.5f) * h;
        return (lx, ly);
    }

    public override bool OnTouchEvent(MotionEvent e) {
        float dp = Resources.DisplayMetrics.Density;
        float pad = 12f * dp;
        float w = Width - pad * 2f;
        float h = Height - pad * 2f;
        if (w <= 0 || h <= 0) {
            return false;
        }

        float tx = e.GetX();
        float ty = e.GetY();
        (float gCx, float gCy, float gR) = GetGimbalGeometry(pad, w, h, dp);
        bool inGimbal = tx >= gCx - gR - 8f * dp && tx <= gCx + gR + 8f * dp &&
                        ty >= gCy - gR - 8f * dp && ty <= gCy + gR + 8f * dp;

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = tx;
                downY = ty;
                lastX = tx;
                lastY = ty;
                maxMoveDist = 0f;

                if (inGimbal) {
                    dragMode = TouchDragMode.HeadGimbal;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;
                }

                (float lx, float ly) = GetListenerCanvasCoords(pad, w, h);
                float hitDist = MathF.Sqrt((tx - lx) * (tx - lx) + (ty - ly) * (ty - ly));
                if (!IsPositionLocked && hitDist <= 42f * dp) {
                    dragMode = TouchDragMode.ListenerPos;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromTouch(tx, ty, pad, w, h);
                    return true;
                }

                dragMode = TouchDragMode.None;
                return true;

            case MotionEventActions.Move: {
                float dx = tx - downX;
                float dy = ty - downY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > maxMoveDist) {
                    maxMoveDist = dist;
                }

                if (dragMode == TouchDragMode.HeadGimbal) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    float newYaw = NormalizeYaw(listenerRotation.Y + stepX * 1.4f / dp);
                    float newPitch = Math.Clamp(listenerRotation.X - stepY * 1.2f / dp, -90f, 90f);
                    listenerRotation = new Vector3(newPitch, newYaw, listenerRotation.Z);
                    ListenerRotationChanged?.Invoke(listenerRotation);
                    Invalidate();
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.ListenerPos) {
                    if (!IsPositionLocked) {
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        UpdateListenerFromTouch(tx, ty, pad, w, h);
                    }
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                // Vertical scroll protection when starting outside gimbal/avatar
                if (Math.Abs(dy) > touchSlop && Math.Abs(dy) > Math.Abs(dx)) {
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    return false;
                }

                if (!IsPositionLocked && Math.Abs(dx) > touchSlop * 1.5f && Math.Abs(dx) > Math.Abs(dy) * 1.5f) {
                    dragMode = TouchDragMode.ListenerPos;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromTouch(tx, ty, pad, w, h);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }
                lastX = tx;
                lastY = ty;
                return true;
            }

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                bool wasTap = maxMoveDist < touchSlop;
                TouchDragMode endedMode = dragMode;
                dragMode = TouchDragMode.None;

                if (!wasTap) {
                    return true;
                }

                // Double-tap 3D Head Gimbal resets Yaw & Pitch to 0
                if (endedMode == TouchDragMode.HeadGimbal || inGimbal) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastGimbalTapTime).TotalMilliseconds < 320) {
                        lastGimbalTapTime = DateTime.MinValue;
                        listenerRotation = Vector3.Zero;
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                    } else {
                        lastGimbalTapTime = now;
                    }
                    return true;
                }

                // Check Tap-to-Solo on any 3D object or bed speaker dot (works even when room pos is locked!)
                TrackSourceItem hitSource = FindNearestSource(tx, ty, 24f * dp);
                if (hitSource != null) {
                    hitSource.IsSolo = !hitSource.IsSolo;
                    SourceSoloToggled?.Invoke(hitSource);
                    Invalidate();
                    return true;
                }

                if (!IsPositionLocked) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastRoomTapTime).TotalMilliseconds < 280) {
                        lastRoomTapTime = DateTime.MinValue;
                        listenerPosition = new Vector3(0, listenerPosition.Y, 0);
                        ListenerPositionChanged?.Invoke(listenerPosition);
                        Invalidate();
                    } else {
                        lastRoomTapTime = now;
                        UpdateListenerFromTouch(tx, ty, pad, w, h);
                    }
                }
                return true;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                dragMode = TouchDragMode.None;
                return false;
        }

        return base.OnTouchEvent(e);
    }

    TrackSourceItem FindNearestSource(float tx, float ty, float maxRadiusPx) {
        TrackSourceItem best = null;
        float bestDistSq = maxRadiusPx * maxRadiusPx;
        for (int i = 0; i < hitTargets.Count; i++) {
            var (item, sx, sy) = hitTargets[i];
            float d2 = (tx - sx) * (tx - sx) + (ty - sy) * (ty - sy);
            if (d2 <= bestDistSq) {
                bestDistSq = d2;
                best = item;
            }
        }
        return best;
    }

    static float NormalizeYaw(float deg) {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
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
