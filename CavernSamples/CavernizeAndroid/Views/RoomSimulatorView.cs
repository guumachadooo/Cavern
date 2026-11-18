using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;

using Cavern.Channels;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Unified One UI 9 Orbitable 3D Spatial Room Visualizer:
/// - Single cohesive 3D perspective stage: drag room background to freely orbit azimuth &amp; elevation
///   (smoothly transitioning between 3D isometric, Top-Down, and Front Elevation) with 3 instant preset chips
/// - 60 FPS continuous object/RMS interpolation with fading motion trails and vertical height drop-stalks
/// - Dedicated Bottom Dock housing the 3D Head Gimbal (Yaw/Pitch) and Listener Height (Y) scrubber,
///   completely separated below the 3D acoustic room so speakers are never obscured
/// - Direct tap-to-Solo on any speaker or 3D object and drag Listener across the floor (or Y height in front view)
/// </summary>
public sealed class RoomSimulatorView : View {
    sealed class SmoothedSourceState {
        public Vector3 SmoothedPos;
        public float SmoothedRms;
        public readonly Vector3[] Trail = new Vector3[7];
        public int TrailHead;
        public int TrailCount;
        public long LastTrailMs;
        public bool Initialized;
    }

    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint roomBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint dockBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint pillActivePaint = new(PaintFlags.AntiAlias);
    readonly Paint pillInactivePaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint floorQuadPaint = new(PaintFlags.AntiAlias);
    readonly Paint ceilingWirePaint = new(PaintFlags.AntiAlias);
    readonly Paint stalkPaint = new(PaintFlags.AntiAlias);
    readonly Paint trailPaint = new(PaintFlags.AntiAlias);
    readonly Paint shadowDotPaint = new(PaintFlags.AntiAlias);
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
    readonly Paint nosePaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y, float nx, float ny, float nz)> hitTargets = [];
    readonly Dictionary<TrackSourceItem, SmoothedSourceState> smoothedStates = [];
    readonly RectF[] camPresetRects = [new(), new(), new()];
    RectF camZoomOutRect = new();
    RectF camZoomInRect = new();
    RectF camResetLabelRect = new();

    RectF roomCanvasRect = new();
    RectF dockGimbalRect = new();
    RectF dockGimbalResetRect = new();
    RectF dockHeightRect = new();
    RectF dockHeightResetRect = new();

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero; // X=Pitch (-90..90), Y=Yaw (-180..180)
    bool isAudioPlaying;

    // Smoothly orbitable camera (defaults to clean 3/4 oblique 3D perspective & relaxed framing zoom)
    const float DefaultRoomZoom = 0.84f;
    float roomZoom = DefaultRoomZoom;
    float pinchStartDist;
    float pinchStartZoom = DefaultRoomZoom;
    float camYawDeg = -20f;
    float camPitchDeg = 30f;
    float targetCamYawDeg = -20f;
    float targetCamPitchDeg = 30f;
    bool cameraAnimating;
    long lastFrameMs;

    DateTime lastRoomTapTime = DateTime.MinValue;
    DateTime lastGimbalTapTime = DateTime.MinValue;

    enum TouchDragMode {
        None,
        OrbitCamera,
        PinchZoom,
        ListenerMove,
        HeadGimbal,
        DockHeight,
        PresetClick,
        ZoomOutClick,
        ZoomInClick,
        ResetZoomClick
    }

    TouchDragMode dragMode = TouchDragMode.None;
    int pressedPresetIdx = -1;
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
        roomBorderPaint.StrokeWidth = 1.15f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        dockBgPaint.Color = Color.Argb(208, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
        dockBgPaint.SetStyle(Paint.Style.Fill);

        pillActivePaint.Color = OneUiTheme.Primary;
        pillActivePaint.SetStyle(Paint.Style.Fill);

        pillInactivePaint.Color = Color.Argb(34, 255, 255, 255);
        pillInactivePaint.SetStyle(Paint.Style.Fill);

        gridPaint.Color = Color.Argb(34, 255, 255, 255);
        gridPaint.StrokeWidth = 1f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        floorQuadPaint.Color = Color.Argb(20, 255, 255, 255);
        floorQuadPaint.SetStyle(Paint.Style.Fill);

        ceilingWirePaint.Color = Color.Argb(58, OneUiTheme.IconSliderMedia.R, OneUiTheme.IconSliderMedia.G, OneUiTheme.IconSliderMedia.B);
        ceilingWirePaint.StrokeWidth = 1f * dp;
        ceilingWirePaint.SetStyle(Paint.Style.Stroke);

        stalkPaint.StrokeWidth = 1.4f * dp;
        stalkPaint.SetStyle(Paint.Style.Stroke);

        trailPaint.StrokeWidth = 2.2f * dp;
        trailPaint.StrokeCap = Paint.Cap.Round;
        trailPaint.SetStyle(Paint.Style.Stroke);

        shadowDotPaint.Color = Color.Argb(96, 0, 0, 0);
        shadowDotPaint.SetStyle(Paint.Style.Fill);

        screenPaint.Color = OneUiTheme.Primary;
        screenPaint.StrokeWidth = 3.8f * dp;
        screenPaint.StrokeCap = Paint.Cap.Round;
        screenPaint.SetStyle(Paint.Style.Stroke);

        bedPaint.Color = OneUiTheme.BedChannel;
        bedPaint.SetStyle(Paint.Style.Fill);

        lfePaint.Color = OneUiTheme.LfeChannel;
        lfePaint.SetStyle(Paint.Style.Fill);

        objPaint.Color = OneUiTheme.DynamicObject;
        objPaint.SetStyle(Paint.Style.Fill);

        objGlowPaint.SetStyle(Paint.Style.Fill);

        heightRingPaint.Color = OneUiTheme.IconStemsCalib;
        heightRingPaint.StrokeWidth = 1.8f * dp;
        heightRingPaint.SetStyle(Paint.Style.Stroke);

        mutedPaint.Color = OneUiTheme.SurfaceBlurTint;
        mutedPaint.SetStyle(Paint.Style.Fill);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.4f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerPaint.SetStyle(Paint.Style.Fill);

        listenerRingPaint.Color = OneUiTheme.PrimaryHigh;
        listenerRingPaint.StrokeWidth = 1.9f * dp;
        listenerRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.SetStyle(Paint.Style.Fill);

        sphereWirePaint.Color = Color.Argb(215, 246, 249, 255);
        sphereWirePaint.StrokeWidth = 1.25f * dp;
        sphereWirePaint.SetStyle(Paint.Style.Stroke);

        sphereWireBackPaint.Color = Color.Argb(64, 220, 230, 255);
        sphereWireBackPaint.StrokeWidth = 0.95f * dp;
        sphereWireBackPaint.SetStyle(Paint.Style.Stroke);

        nosePaint.Color = Color.White;
        nosePaint.StrokeWidth = 2.2f * dp;
        nosePaint.StrokeCap = Paint.Cap.Round;
        nosePaint.SetStyle(Paint.Style.Stroke);

        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.TextSize = 9.5f * dp;
        textPaint.TextAlign = Paint.Align.Center;

        subTextPaint.Color = OneUiTheme.TextMuted;
        subTextPaint.TextSize = 9.5f * dp;
        subTextPaint.TextAlign = Paint.Align.Center;
    }

    public void UpdateState(IReadOnlyList<TrackSourceItem> sourceItems, Vector3 envSize, Vector3 pos, Vector3 rot, bool isPlaying = false) {
        sources = sourceItems ?? [];
        if (envSize.X > 0.1f && envSize.Y > 0.1f && envSize.Z > 0.1f) {
            environmentSize = envSize;
        }
        listenerPosition = pos;
        listenerRotation = rot;
        isAudioPlaying = isPlaying;

        // Prune removed source tracking state when track list changes
        if (smoothedStates.Count > sources.Count + 8) {
            HashSet<TrackSourceItem> activeSet = [.. sources];
            List<TrackSourceItem> stale = [];
            foreach (TrackSourceItem key in smoothedStates.Keys) {
                if (!activeSet.Contains(key)) {
                    stale.Add(key);
                }
            }
            foreach (TrackSourceItem k in stale) {
                smoothedStates.Remove(k);
            }
        }

        PostInvalidateOnAnimation();
    }

    public void AnimateToCameraPreset(float yawDeg, float pitchDeg) {
        targetCamYawDeg = NormalizeDeg(yawDeg);
        targetCamPitchDeg = Math.Clamp(pitchDeg, 6f, 86f);
        cameraAnimating = true;
        lastFrameMs = SystemClock.UptimeMillis();
        PostInvalidateOnAnimation();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        float dp = Resources.DisplayMetrics.Density;
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int desiredH = Math.Max((int)(w * 0.84f), (int)(350f * dp));
        SetMeasuredDimension(w, ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float dp = Resources.DisplayMetrics.Density;
        float outerPad = 4f * dp;
        float w = Width - outerPad * 2f;
        float h = Height - outerPad * 2f;
        if (w <= 0 || h <= 0) {
            return;
        }

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) {
            lastFrameMs = nowMs;
        }
        float dt = Math.Clamp((nowMs - lastFrameMs) / 1000f, 0.001f, 0.05f);
        lastFrameMs = nowMs;

        // Smooth camera transition toward selected angle preset
        if (cameraAnimating) {
            float yawDiff = NormalizeDeg(targetCamYawDeg - camYawDeg);
            float pitchDiff = targetCamPitchDeg - camPitchDeg;
            float camStep = Math.Min(1f, dt * 13f);
            camYawDeg = NormalizeDeg(camYawDeg + yawDiff * camStep);
            camPitchDeg += pitchDiff * camStep;
            if (MathF.Abs(yawDiff) < 0.25f && MathF.Abs(pitchDiff) < 0.25f) {
                camYawDeg = targetCamYawDeg;
                camPitchDeg = targetCamPitchDeg;
                cameraAnimating = false;
            }
        }

        // Zone 1: Compact top row — subtle camera hint + zoom (-/+) buttons + 3 crisp angle shortcuts ("3D", "Topo", "Frente")
        float topBarH = 26f * dp;
        float topY = outerPad;
        float presetW = 44f * dp;
        float presetGap = 4f * dp;
        float zoomBtnSz = 24f * dp;
        float zoomGap = 4f * dp;
        float presetsTotalW = presetW * 3f + presetGap * 2f;
        float presetsStartX = outerPad + w - presetsTotalW;

        float zInX = presetsStartX - 6f * dp - zoomBtnSz;
        float zOutX = zInX - zoomGap - zoomBtnSz;
        float zTop = topY + (topBarH - zoomBtnSz) * 0.5f;
        camZoomOutRect = new RectF(zOutX, zTop, zOutX + zoomBtnSz, zTop + zoomBtnSz);
        camZoomInRect = new RectF(zInX, zTop, zInX + zoomBtnSz, zTop + zoomBtnSz);
        camResetLabelRect = new RectF(outerPad, topY, camZoomOutRect.Left - 4f * dp, topY + topBarH);

        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 10f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        canvas.DrawText(
            $"Câm {camPitchDeg:0}° • {Math.Round(roomZoom * 100)}%",
            outerPad + 6f * dp,
            topY + topBarH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f,
            subTextPaint);

        // Draw crisp - and + zoom circular buttons
        canvas.DrawOval(camZoomOutRect, dragMode == TouchDragMode.ZoomOutClick ? pillActivePaint : pillInactivePaint);
        canvas.DrawOval(camZoomInRect, dragMode == TouchDragMode.ZoomInClick ? pillActivePaint : pillInactivePaint);
        float arm = zoomBtnSz * 0.22f;
        float prevStroke = sphereWirePaint.StrokeWidth;
        Color prevColor = sphereWirePaint.Color;
        sphereWirePaint.StrokeWidth = 1.7f * dp;
        sphereWirePaint.Color = OneUiTheme.TextPrimary;
        canvas.DrawLine(camZoomOutRect.CenterX() - arm, camZoomOutRect.CenterY(), camZoomOutRect.CenterX() + arm, camZoomOutRect.CenterY(), sphereWirePaint);
        canvas.DrawLine(camZoomInRect.CenterX() - arm, camZoomInRect.CenterY(), camZoomInRect.CenterX() + arm, camZoomInRect.CenterY(), sphereWirePaint);
        canvas.DrawLine(camZoomInRect.CenterX(), camZoomInRect.CenterY() - arm, camZoomInRect.CenterX(), camZoomInRect.CenterY() + arm, sphereWirePaint);
        sphereWirePaint.StrokeWidth = prevStroke;
        sphereWirePaint.Color = prevColor;

        (string label, float py, float pp)[] presets = [
            ("3D", -20f, 30f),
            ("Topo", 0f, 84f),
            ("Frente", 0f, 10f)
        ];
        for (int i = 0; i < 3; i++) {
            float px = presetsStartX + i * (presetW + presetGap);
            camPresetRects[i] = new RectF(px, topY, px + presetW, topY + topBarH);
            bool isClose = MathF.Abs(NormalizeDeg(camYawDeg - presets[i].py)) < 8f
                && MathF.Abs(camPitchDeg - presets[i].pp) < 8f;
            canvas.DrawRoundRect(camPresetRects[i], topBarH * 0.5f, topBarH * 0.5f, isClose ? pillActivePaint : pillInactivePaint);

            textPaint.TextAlign = Paint.Align.Center;
            textPaint.TextSize = 10.5f * dp;
            textPaint.Color = isClose ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, isClose ? TypefaceStyle.Bold : TypefaceStyle.Normal));
            canvas.DrawText(presets[i].label, camPresetRects[i].CenterX(),
                camPresetRects[i].CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
        }
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));

        // Zone 3: Dedicated Bottom Control Dock (74dp, safely separated from room speakers)
        float dockH = 74f * dp;
        float dockGap = 8f * dp;
        float dockY = outerPad + h - dockH;
        float gimbalW = w * 0.58f;
        dockGimbalRect = new RectF(outerPad, dockY, outerPad + gimbalW, dockY + dockH);
        dockHeightRect = new RectF(outerPad + gimbalW + dockGap, dockY, outerPad + w, dockY + dockH);

        // Zone 2: Main 3D Orbitable Room Viewport
        float roomTop = topY + topBarH + 8f * dp;
        float roomBottom = dockY - dockGap;
        roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w, Math.Max(roomTop + 130f * dp, roomBottom));

        hitTargets.Clear();
        bool anySolo = SourcesAnySolo();
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);
        float headYawRad = listenerRotation.Y * (MathF.PI / 180f);
        float headPitchRad = listenerRotation.X * (MathF.PI / 180f);

        bool stillAnimating = DrawOrbitable3DRoom(
            canvas, roomCanvasRect, anySolo, nowMs, dt, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);

        // Draw Bottom Dock Controls
        DrawDockHeadGimbal(canvas, dockGimbalRect, headYawRad, headPitchRad, dp);
        DrawDockHeightControl(canvas, dockHeightRect, dp);

        if ((isAudioPlaying || cameraAnimating || stillAnimating)
            && Visibility == ViewStates.Visible
            && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    (float cx, float cy, float radiusPx) GetCameraFraming(RectF r) {
        return (r.CenterX(), r.CenterY(), Math.Min(r.Width(), r.Height()) * 0.355f * roomZoom);
    }

    Vector3 TransformWorldToCamera(float x, float y, float z, float camYawRad, float camPitchRad) {
        float cosA = MathF.Cos(camYawRad);
        float sinA = MathF.Sin(camYawRad);
        float x1 = x * cosA - z * sinA;
        float z1 = x * sinA + z * cosA;

        float cosB = MathF.Cos(camPitchRad);
        float sinB = MathF.Sin(camPitchRad);
        float y2 = y * cosB + z1 * sinB;
        float z2 = -y * sinB + z1 * cosB;
        return new Vector3(x1, y2, z2);
    }

    (float sx, float sy, float persp, float depth) ProjectOrbit3D(
        RectF r, float nx, float ny, float nz, float camYawRad, float camPitchRad) {
        const float camDist = 3.65f;
        var (cx, cy, rad) = GetCameraFraming(r);
        Vector3 cam = TransformWorldToCamera(nx, ny, nz, camYawRad, camPitchRad);
        float denom = Math.Max(1.35f, camDist + cam.Z);
        float persp = camDist / denom;
        float sx = cx + cam.X * rad * persp;
        float sy = cy - cam.Y * rad * persp;
        return (sx, sy, persp, cam.Z);
    }

    (float nx, float nz) UnprojectHorizontalPlane(
        RectF r, float sx, float sy, float fixedNy, float camYawRad, float camPitchRad) {
        const float camDist = 3.65f;
        var (cx, cy, rad) = GetCameraFraming(r);
        float u = (sx - cx) / Math.Max(1f, rad);
        float v = (cy - sy) / Math.Max(1f, rad);

        float cosB = MathF.Cos(camPitchRad);
        float sinB = MathF.Sin(camPitchRad);
        float denom = camDist * sinB - v * cosB;
        if (MathF.Abs(denom) < 0.08f) {
            denom = denom >= 0 ? 0.08f : -0.08f;
        }
        float z1 = (v * camDist - v * fixedNy * sinB - camDist * fixedNy * cosB) / denom;
        float z2 = -fixedNy * sinB + z1 * cosB;
        float x1 = u * Math.Max(1.35f, camDist + z2) / camDist;

        float cosA = MathF.Cos(camYawRad);
        float sinA = MathF.Sin(camYawRad);
        float nx = Math.Clamp(x1 * cosA + z1 * sinA, -1f, 1f);
        float nz = Math.Clamp(-x1 * sinA + z1 * cosA, -1f, 1f);
        return (nx, nz);
    }

    bool DrawOrbitable3DRoom(
        Canvas canvas, RectF r, bool anySolo, long nowMs, float dt,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        canvas.Save();
        Android.Graphics.Path clipRoom = new();
        clipRoom.AddRoundRect(r, corner, corner, Android.Graphics.Path.Direction.Cw);
        canvas.ClipPath(clipRoom);

        // 1. Floor (-1 Y) Quad & Grid
        var fl_bl = ProjectOrbit3D(r, -1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_br = ProjectOrbit3D(r, 1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_fr = ProjectOrbit3D(r, 1f, -1f, 1f, camYawRad, camPitchRad);
        var fl_fl = ProjectOrbit3D(r, -1f, -1f, 1f, camYawRad, camPitchRad);
        using (Android.Graphics.Path floorPath = new()) {
            floorPath.MoveTo(fl_bl.sx, fl_bl.sy);
            floorPath.LineTo(fl_br.sx, fl_br.sy);
            floorPath.LineTo(fl_fr.sx, fl_fr.sy);
            floorPath.LineTo(fl_fl.sx, fl_fl.sy);
            floorPath.Close();
            canvas.DrawPath(floorPath, floorQuadPaint);
            canvas.DrawPath(floorPath, gridPaint);
        }

        for (int g = -1; g <= 1; g++) {
            float t = g * 0.5f;
            var ax = ProjectOrbit3D(r, t, -1f, -1f, camYawRad, camPitchRad);
            var bx = ProjectOrbit3D(r, t, -1f, 1f, camYawRad, camPitchRad);
            canvas.DrawLine(ax.sx, ax.sy, bx.sx, bx.sy, gridPaint);

            var az = ProjectOrbit3D(r, -1f, -1f, t, camYawRad, camPitchRad);
            var bz = ProjectOrbit3D(r, 1f, -1f, t, camYawRad, camPitchRad);
            canvas.DrawLine(az.sx, az.sy, bz.sx, bz.sy, gridPaint);
        }

        // 2. Ceiling (+1 Y) Wireframe + Vertical Pillars
        var ce_bl = ProjectOrbit3D(r, -1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_br = ProjectOrbit3D(r, 1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_fr = ProjectOrbit3D(r, 1f, 1f, 1f, camYawRad, camPitchRad);
        var ce_fl = ProjectOrbit3D(r, -1f, 1f, 1f, camYawRad, camPitchRad);
        using (Android.Graphics.Path ceilPath = new()) {
            ceilPath.MoveTo(ce_bl.sx, ce_bl.sy);
            ceilPath.LineTo(ce_br.sx, ce_br.sy);
            ceilPath.LineTo(ce_fr.sx, ce_fr.sy);
            ceilPath.LineTo(ce_fl.sx, ce_fl.sy);
            ceilPath.Close();
            canvas.DrawPath(ceilPath, ceilingWirePaint);
        }
        canvas.DrawLine(fl_bl.sx, fl_bl.sy, ce_bl.sx, ce_bl.sy, gridPaint);
        canvas.DrawLine(fl_br.sx, fl_br.sy, ce_br.sx, ce_br.sy, gridPaint);
        canvas.DrawLine(fl_fr.sx, fl_fr.sy, ce_fr.sx, ce_fr.sy, gridPaint);
        canvas.DrawLine(fl_fl.sx, fl_fl.sy, ce_fl.sx, ce_fl.sy, gridPaint);

        // 3. Front Screen Bar (+Z wall) orbiting cleanly with the room
        var scrL = ProjectOrbit3D(r, -0.58f, 0.25f, 1f, camYawRad, camPitchRad);
        var scrR = ProjectOrbit3D(r, 0.58f, 0.25f, 1f, camYawRad, camPitchRad);
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(scrL.sx, scrL.sy, scrR.sx, scrR.sy, screenPaint);
        var scrMid = ProjectOrbit3D(r, 0f, 0.42f, 1.02f, camYawRad, camPitchRad);
        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 8.5f * dp;
        subTextPaint.Color = OneUiTheme.PeachText;
        canvas.DrawText("TELA", scrMid.sx, scrMid.sy - 2f * dp, subTextPaint);

        // 4. Smooth 60 FPS Object Interpolation + Motion Trails + Depth Sort
        bool anyInterpolating = false;
        List<(bool isListener, TrackSourceItem item, int idx, Vector3 np, float rms, SmoothedSourceState st, float depth)> renderQueue = new(sources.Count + 1);

        float posBlend = Math.Min(1f, dt * 18f);
        float rmsRise = Math.Min(1f, dt * 24f);
        float rmsFall = Math.Min(1f, dt * 10f);

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            Vector3 targetPos = new(
                Math.Clamp(item.NormalizedPosition.X, -1f, 1f),
                Math.Clamp(item.NormalizedPosition.Y, -1f, 1f),
                Math.Clamp(item.NormalizedPosition.Z, -1f, 1f));
            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float targetRms = effectivelyMuted ? 0f : item.RmsLevel;

            if (!smoothedStates.TryGetValue(item, out SmoothedSourceState st)) {
                st = new SmoothedSourceState();
                smoothedStates[item] = st;
            }

            if (!st.Initialized) {
                st.SmoothedPos = targetPos;
                st.SmoothedRms = targetRms;
                st.Initialized = true;
                st.LastTrailMs = nowMs;
            } else {
                Vector3 delta = targetPos - st.SmoothedPos;
                if (delta.LengthSquared() > 0.00001f) {
                    st.SmoothedPos = Vector3.Lerp(st.SmoothedPos, targetPos, posBlend);
                    anyInterpolating = true;
                } else {
                    st.SmoothedPos = targetPos;
                }

                float rmsDiff = targetRms - st.SmoothedRms;
                if (MathF.Abs(rmsDiff) > 0.002f) {
                    st.SmoothedRms += rmsDiff * (rmsDiff > 0 ? rmsRise : rmsFall);
                    anyInterpolating = true;
                } else {
                    st.SmoothedRms = targetRms;
                }

                // Append trail dot when a dynamic object glides visibly
                if (!item.IsBed && nowMs - st.LastTrailMs >= 28) {
                    int prevIdx = (st.TrailHead + st.Trail.Length - 1) % st.Trail.Length;
                    if (st.TrailCount == 0 || (st.SmoothedPos - st.Trail[prevIdx]).LengthSquared() > 0.0012f) {
                        st.Trail[st.TrailHead] = st.SmoothedPos;
                        st.TrailHead = (st.TrailHead + 1) % st.Trail.Length;
                        st.TrailCount = Math.Min(st.TrailCount + 1, st.Trail.Length);
                        st.LastTrailMs = nowMs;
                    } else if (st.TrailCount > 0 && nowMs - st.LastTrailMs >= 160) {
                        st.TrailCount = Math.Max(0, st.TrailCount - 1);
                        st.LastTrailMs = nowMs;
                    }
                }
            }

            var pt = ProjectOrbit3D(r, st.SmoothedPos.X, st.SmoothedPos.Y, st.SmoothedPos.Z, camYawRad, camPitchRad);
            renderQueue.Add((false, item, i, st.SmoothedPos, st.SmoothedRms, st, pt.depth));
        }

        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        var lCenter = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);
        renderQueue.Add((true, null, -1, new Vector3(lnx, lny, lnz), 0f, null, lCenter.depth));

        // Sort back-to-front (largest depth first, nearest last)
        renderQueue.Sort((a, b) => b.depth.CompareTo(a.depth));

        foreach (var entry in renderQueue) {
            if (entry.isListener) {
                DrawListenerIn3DRoom(canvas, r, lnx, lny, lnz, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);
                continue;
            }

            TrackSourceItem item = entry.item;
            Vector3 np = entry.np;
            SmoothedSourceState st = entry.st;

            var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
            var posPt = ProjectOrbit3D(r, np.X, np.Y, np.Z, camYawRad, camPitchRad);
            hitTargets.Add((item, posPt.sx, posPt.sy, np.X, np.Y, np.Z));

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(entry.rms * 16f, 0f, 11f) * dp;

            // Smooth fading motion trail behind moving dynamic objects
            if (!item.IsBed && !effectivelyMuted && st.TrailCount >= 2) {
                int start = (st.TrailHead - st.TrailCount + st.Trail.Length) % st.Trail.Length;
                var prevProj = ProjectOrbit3D(r, st.Trail[start].X, st.Trail[start].Y, st.Trail[start].Z, camYawRad, camPitchRad);
                for (int t = 1; t < st.TrailCount; t++) {
                    int tidx = (start + t) % st.Trail.Length;
                    var curProj = ProjectOrbit3D(r, st.Trail[tidx].X, st.Trail[tidx].Y, st.Trail[tidx].Z, camYawRad, camPitchRad);
                    float frac = t / (float)st.TrailCount;
                    trailPaint.Color = Color.Argb((int)(135 * frac), OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B);
                    trailPaint.StrokeWidth = (1.2f + 1.8f * frac) * dp;
                    canvas.DrawLine(prevProj.sx, prevProj.sy, curProj.sx, curProj.sy, trailPaint);
                    prevProj = curProj;
                }
                trailPaint.Color = Color.Argb(150, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B);
                canvas.DrawLine(prevProj.sx, prevProj.sy, posPt.sx, posPt.sy, trailPaint);
            }

            // Floor shadow + vertical elevation drop-stalk
            canvas.DrawCircle(floorPt.sx, floorPt.sy, 3f * dp * floorPt.persp, shadowDotPaint);
            Color baseCol = effectivelyMuted
                ? OneUiTheme.TextMuted
                : (item.IsBed ? (item.IsLFE ? OneUiTheme.LfeChannel : OneUiTheme.BedChannel) : OneUiTheme.DynamicObject);
            stalkPaint.Color = np.Y > 0.16f
                ? Color.Argb(135, OneUiTheme.IconStemsCalib.R, OneUiTheme.IconStemsCalib.G, OneUiTheme.IconStemsCalib.B)
                : Color.Argb(88, baseCol.R, baseCol.G, baseCol.B);
            canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, stalkPaint);

            float scale = Math.Clamp(posPt.persp * 1.05f, 0.72f, 1.28f);
            if (item.IsBed) {
                float sz = 7.2f * dp * scale;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 0.8f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + rmsBoost,
                        objGlowPaint.WithColor(Color.Argb(68, p.Color.R, p.Color.G, p.Color.B)));
                }
                canvas.DrawRoundRect(posPt.sx - sz, posPt.sy - sz, posPt.sx + sz, posPt.sy + sz, 4f * dp, 4f * dp, p);
                if (np.Y > 0.18f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + 3f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + 5f * dp, soloRingPaint);
                }
                string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                textPaint.TextAlign = Paint.Align.Center;
                textPaint.TextSize = 9.5f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(shortLabel, posPt.sx, posPt.sy + sz + 10.5f * dp, textPaint);
            } else {
                float rad = 8f * dp * scale;
                if (!effectivelyMuted && rmsBoost > 0.5f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + rmsBoost,
                        objGlowPaint.WithColor(Color.Argb(78, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B)));
                }
                canvas.DrawCircle(posPt.sx, posPt.sy, rad, effectivelyMuted ? mutedPaint : objPaint);
                if (np.Y > 0.16f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 3f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 5f * dp, soloRingPaint);
                }
                string label = $"O{entry.idx + 1}";
                if (np.Y > 0.25f) label += " ↑";
                else if (np.Y < -0.25f) label += " ↓";
                textPaint.TextAlign = Paint.Align.Center;
                textPaint.TextSize = 9.5f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(label, posPt.sx, posPt.sy - rad - 4f * dp, textPaint);
            }
        }

        canvas.Restore();
        return anyInterpolating;
    }

    void DrawListenerIn3DRoom(
        Canvas canvas, RectF r, float lnx, float lny, float lnz,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        var lFloor = ProjectOrbit3D(r, lnx, -1f, lnz, camYawRad, camPitchRad);
        var lPos = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);

        canvas.DrawCircle(lFloor.sx, lFloor.sy, 5f * dp * lFloor.persp, shadowDotPaint);
        stalkPaint.Color = Color.Argb(180, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        canvas.DrawLine(lFloor.sx, lFloor.sy, lPos.sx, lPos.sy, stalkPaint);

        // Projected 3D FOV Cone aligned with listener yaw
        const float spread = 0.44f;
        const float reach = 0.44f;
        var fovL = ProjectOrbit3D(r, Math.Clamp(lnx + MathF.Sin(headYawRad - spread) * reach, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(headYawRad - spread) * reach, -1f, 1f), camYawRad, camPitchRad);
        var fovR = ProjectOrbit3D(r, Math.Clamp(lnx + MathF.Sin(headYawRad + spread) * reach, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(headYawRad + spread) * reach, -1f, 1f), camYawRad, camPitchRad);
        var fovC = ProjectOrbit3D(r, Math.Clamp(lnx + MathF.Sin(headYawRad) * reach, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(headYawRad) * reach, -1f, 1f), camYawRad, camPitchRad);

        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lPos.sx, lPos.sy);
            cone.LineTo(fovL.sx, fovL.sy);
            cone.LineTo(fovR.sx, fovR.sy);
            cone.Close();
            using LinearGradient coneGrad = new(
                lPos.sx, lPos.sy, fovC.sx, fovC.sy,
                [Color.Argb(118, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        Draw3DHeadSphere(canvas, lPos.sx, lPos.sy, 15.5f * dp * Math.Clamp(lPos.persp, 0.8f, 1.18f),
            headYawRad, headPitchRad, camYawRad, camPitchRad);
    }

    void Draw3DHeadSphere(
        Canvas canvas, float cx, float cy, float r,
        float headYawRad, float headPitchRad, float camYawRad, float camPitchRad) {

        Color p = OneUiTheme.Primary;
        Color hi = OneUiTheme.PrimaryHigh;
        Color dk = OneUiTheme.PrimaryPressed;

        using (RadialGradient sphereGrad = new(
            cx - r * 0.28f, cy - r * 0.28f, Math.Max(1f, r * 1.15f),
            [Color.Argb(245, hi.R, hi.G, hi.B).ToArgb(), Color.Argb(235, p.R, p.G, p.B).ToArgb(), Color.Argb(240, dk.R, dk.G, dk.B).ToArgb()],
            [0f, 0.55f, 1f], Shader.TileMode.Clamp)) {
            listenerPaint.SetShader(sphereGrad);
            canvas.DrawCircle(cx, cy, r, listenerPaint);
            listenerPaint.SetShader(null);
        }
        canvas.DrawCircle(cx, cy, r, listenerRingPaint);

        Vector3 ProjectHeadPoint(float lx, float ly, float lz) {
            // 1. Local Pitch (positive pitch tilts nose +Z up toward +Y)
            float cosP = MathF.Cos(headPitchRad);
            float sinP = MathF.Sin(headPitchRad);
            float y1 = ly * cosP + lz * sinP;
            float z1 = -ly * sinP + lz * cosP;

            // 2. Local Yaw (positive yaw turns nose +Z clockwise toward +X)
            float cosY = MathF.Cos(headYawRad);
            float sinY = MathF.Sin(headYawRad);
            float x2 = lx * cosY + z1 * sinY;
            float z2 = -lx * sinY + z1 * cosY;

            // 3. Apply Active Room Camera Transform
            Vector3 cam = TransformWorldToCamera(x2, y1, z2, camYawRad, camPitchRad);
            return new Vector3(cx + cam.X * r, cy - cam.Y * r, -cam.Z);
        }

        void Draw3DRing(Func<float, Vector3> ringFn) {
            const int segs = 20;
            Vector3 pt0 = ringFn(0f);
            Vector3 prev = ProjectHeadPoint(pt0.X, pt0.Y, pt0.Z);
            for (int i = 1; i <= segs; i++) {
                float a = i / (float)segs * MathF.PI * 2f;
                Vector3 pt = ringFn(a);
                Vector3 cur = ProjectHeadPoint(pt.X, pt.Y, pt.Z);
                Paint wp = (prev.Z + cur.Z) >= 0f ? sphereWirePaint : sphereWireBackPaint;
                canvas.DrawLine(prev.X, prev.Y, cur.X, cur.Y, wp);
                prev = cur;
            }
        }

        Draw3DRing(a => new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
        Draw3DRing(a => new Vector3(0f, MathF.Sin(a), MathF.Cos(a)));

        Vector3 leftEar = ProjectHeadPoint(-1.04f, 0f, 0f);
        Vector3 rightEar = ProjectHeadPoint(1.04f, 0f, 0f);
        Paint earPaint = objGlowPaint.WithColor(Color.Argb(240, 252, 252, 255));
        canvas.DrawCircle(leftEar.X, leftEar.Y, r * 0.23f, earPaint);
        canvas.DrawCircle(rightEar.X, rightEar.Y, r * 0.23f, earPaint);

        Vector3 noseBase = ProjectHeadPoint(0f, 0f, 0.72f);
        Vector3 noseTip = ProjectHeadPoint(0f, 0f, 1.38f);
        canvas.DrawLine(noseBase.X, noseBase.Y, noseTip.X, noseTip.Y, nosePaint);
        canvas.DrawCircle(noseTip.X, noseTip.Y, r * 0.20f, objGlowPaint.WithColor(Color.White));
    }

    void DrawDockHeadGimbal(Canvas canvas, RectF r, float headYawRad, float headPitchRad, float dp) {
        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, dockBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        float sphereR = Math.Min(23f * dp, (r.Height() - 16f * dp) * 0.5f);
        float sphereCx = r.Left + 14f * dp + sphereR;
        float sphereCy = r.CenterY();
        Draw3DHeadSphere(canvas, sphereCx, sphereCy, sphereR, headYawRad, headPitchRad, camYawRad: 0f, camPitchRad: 0.16f);

        float textLeft = sphereCx + sphereR + 10f * dp;
        textPaint.TextAlign = Paint.Align.Left;
        textPaint.TextSize = 11f * dp;
        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        canvas.DrawText("Cabeça 3D", textLeft, r.Top + 21f * dp, textPaint);

        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));
        textPaint.TextSize = 10f * dp;
        textPaint.Color = OneUiTheme.PeachText;
        canvas.DrawText($"G {listenerRotation.Y:0}° • I {listenerRotation.X:0}°", textLeft, r.Top + 37f * dp, textPaint);

        float rstH = 20f * dp;
        float rstW = Math.Max(46f * dp, r.Right - textLeft - 10f * dp);
        dockGimbalResetRect = new RectF(textLeft, r.Bottom - rstH - 8f * dp, textLeft + Math.Min(68f * dp, rstW), r.Bottom - 8f * dp);
        canvas.DrawRoundRect(dockGimbalResetRect, rstH * 0.5f, rstH * 0.5f, pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.5f * dp;
        textPaint.Color = OneUiTheme.TextSub;
        canvas.DrawText("Zerar", dockGimbalResetRect.CenterX(),
            dockGimbalResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
    }

    void DrawDockHeightControl(Canvas canvas, RectF r, float dp) {
        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, dockBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 10.5f * dp;
        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
        canvas.DrawText($"Altura: {listenerPosition.Y:+0.0;-0.0;0.0}m", r.CenterX(), r.Top + 19f * dp, textPaint);
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));

        float barLeft = r.Left + 12f * dp;
        float barRight = r.Right - 12f * dp;
        float barCy = r.Top + 38f * dp;
        float barH = 12f * dp;
        RectF barRect = new(barLeft, barCy - barH * 0.5f, barRight, barCy + barH * 0.5f);
        canvas.DrawRoundRect(barRect, barH * 0.5f, barH * 0.5f, pillInactivePaint);

        float ny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float fillFrac = (ny + 1f) * 0.5f;
        float knobX = barLeft + fillFrac * barRect.Width();

        canvas.DrawLine(barRect.CenterX(), barRect.Top, barRect.CenterX(), barRect.Bottom, gridPaint);
        Color sliderAccent = OneUiTheme.SliderAccent;
        bool isDraggingHeight = dragMode == TouchDragMode.DockHeight;
        pillActivePaint.Color = isDraggingHeight
            ? Color.Argb(135, sliderAccent.R, sliderAccent.G, sliderAccent.B)
            : Color.Argb(255, sliderAccent.R, sliderAccent.G, sliderAccent.B);
        canvas.DrawRoundRect(new RectF(Math.Min(barRect.CenterX(), knobX), barRect.Top, Math.Max(barRect.CenterX(), knobX), barRect.Bottom),
            barH * 0.5f, barH * 0.5f, pillActivePaint);
        pillActivePaint.Color = OneUiTheme.Primary;

        if (isDraggingHeight) {
            canvas.DrawCircle(knobX, barCy, barH * 0.5f, objGlowPaint.WithColor(Color.Argb(255, sliderAccent.R, sliderAccent.G, sliderAccent.B)));
        }

        float zBtnH = 18f * dp;
        dockHeightResetRect = new RectF(r.CenterX() - 34f * dp, r.Bottom - zBtnH - 6f * dp, r.CenterX() + 34f * dp, r.Bottom - 6f * dp);
        textPaint.TextSize = 9.5f * dp;
        textPaint.Color = OneUiTheme.TextSub;
        canvas.DrawText("Tocar • 0m", r.CenterX(),
            dockHeightResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
    }

    bool SourcesAnySolo() {
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].IsSolo) {
                return true;
            }
        }
        return false;
    }

    public override bool OnTouchEvent(MotionEvent e) {
        float dp = Resources.DisplayMetrics.Density;
        float tx = e.GetX();
        float ty = e.GetY();

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                downX = tx;
                downY = ty;
                lastX = tx;
                lastY = ty;
                maxMoveDist = 0f;
                pressedPresetIdx = -1;

                for (int i = 0; i < 3; i++) {
                    if (camPresetRects[i].Contains(tx, ty)) {
                        pressedPresetIdx = i;
                        dragMode = TouchDragMode.PresetClick;
                        return true;
                    }
                }

                if (camZoomOutRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.ZoomOutClick;
                    Invalidate();
                    return true;
                }

                if (camZoomInRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.ZoomInClick;
                    Invalidate();
                    return true;
                }

                if (camResetLabelRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.ResetZoomClick;
                    return true;
                }

                if (dockGimbalRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeadGimbal;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;
                }

                if (dockHeightRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.DockHeight;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    if (!dockHeightResetRect.Contains(tx, ty)) {
                        UpdateHeightFromDockTouch(tx);
                    }
                    return true;
                }

                if (roomCanvasRect.Contains(tx, ty)) {
                    float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
                    float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
                    float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
                    var lPt = ProjectOrbit3D(roomCanvasRect, lnx, lny, lnz,
                        camYawDeg * (MathF.PI / 180f), camPitchDeg * (MathF.PI / 180f));

                    if (!IsPositionLocked && MathF.Sqrt((tx - lPt.sx) * (tx - lPt.sx) + (ty - lPt.sy) * (ty - lPt.sy)) <= 36f * dp) {
                        dragMode = TouchDragMode.ListenerMove;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        return true;
                    }

                    // Dragging anywhere else inside the 3D room orbits the camera freely!
                    dragMode = TouchDragMode.OrbitCamera;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    cameraAnimating = false;
                    return true;
                }

                dragMode = TouchDragMode.None;
                return false;

            case MotionEventActions.PointerDown:
                if (e.PointerCount >= 2 && (roomCanvasRect.Contains(tx, ty) || dragMode == TouchDragMode.OrbitCamera || dragMode == TouchDragMode.ListenerMove)) {
                    pinchStartDist = PointerDist(e);
                    pinchStartZoom = roomZoom;
                    dragMode = TouchDragMode.PinchZoom;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;
                }
                break;

            case MotionEventActions.Move: {
                if (dragMode == TouchDragMode.PinchZoom && e.PointerCount >= 2 && pinchStartDist > 12f * dp) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float curDist = PointerDist(e);
                    roomZoom = Math.Clamp(pinchStartZoom * (curDist / pinchStartDist), 0.45f, 1.85f);
                    maxMoveDist = touchSlop + 1f;
                    Invalidate();
                    return true;
                }

                float dx = tx - downX;
                float dy = ty - downY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > maxMoveDist) {
                    maxMoveDist = dist;
                }

                if (dragMode == TouchDragMode.PresetClick || dragMode == TouchDragMode.ZoomOutClick || dragMode == TouchDragMode.ZoomInClick || dragMode == TouchDragMode.ResetZoomClick) {
                    return true;
                }

                if (dragMode == TouchDragMode.HeadGimbal) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    float newYaw = NormalizeDeg(listenerRotation.Y + stepX * 1.35f / dp);
                    float newPitch = Math.Clamp(listenerRotation.X - stepY * 1.15f / dp, -90f, 90f);
                    listenerRotation = new Vector3(newPitch, newYaw, listenerRotation.Z);
                    ListenerRotationChanged?.Invoke(listenerRotation);
                    Invalidate();
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.DockHeight) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromDockTouch(tx);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.ListenerMove && !IsPositionLocked) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromRoomTouch(tx, ty, lastX, lastY, dp);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.OrbitCamera) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    camYawDeg = NormalizeDeg(camYawDeg + stepX * 0.55f / dp);
                    camPitchDeg = Math.Clamp(camPitchDeg + stepY * 0.45f / dp, 6f, 86f);
                    Invalidate();
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                lastX = tx;
                lastY = ty;
                return true;
            }

            case MotionEventActions.PointerUp:
                if (dragMode == TouchDragMode.PinchZoom && e.PointerCount <= 2) {
                    dragMode = TouchDragMode.None;
                    maxMoveDist = touchSlop + 1f;
                }
                return true;

            case MotionEventActions.Up: {
                Parent?.RequestDisallowInterceptTouchEvent(false);
                bool wasTap = maxMoveDist < touchSlop;
                TouchDragMode endedMode = dragMode;
                int preset = pressedPresetIdx;
                dragMode = TouchDragMode.None;
                pressedPresetIdx = -1;

                if (!wasTap) {
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.ZoomOutClick) {
                    roomZoom = Math.Clamp(roomZoom - 0.14f, 0.45f, 1.85f);
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.ZoomInClick) {
                    roomZoom = Math.Clamp(roomZoom + 0.14f, 0.45f, 1.85f);
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.ResetZoomClick) {
                    roomZoom = DefaultRoomZoom;
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.PresetClick && preset >= 0 && preset < 3) {
                    switch (preset) {
                        case 0: AnimateToCameraPreset(-20f, 30f); break;
                        case 1: AnimateToCameraPreset(0f, 84f); break;
                        case 2: AnimateToCameraPreset(0f, 10f); break;
                    }
                    return true;
                }

                if (dockGimbalResetRect.Contains(tx, ty)) {
                    listenerRotation = Vector3.Zero;
                    ListenerRotationChanged?.Invoke(listenerRotation);
                    Invalidate();
                    return true;
                }

                if (dockHeightResetRect.Contains(tx, ty)) {
                    listenerPosition = new Vector3(listenerPosition.X, 0f, listenerPosition.Z);
                    ListenerPositionChanged?.Invoke(listenerPosition);
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.HeadGimbal) {
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

                if (roomCanvasRect.Contains(tx, ty)) {
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
                            listenerPosition = new Vector3(0f, listenerPosition.Y, 0f);
                            ListenerPositionChanged?.Invoke(listenerPosition);
                            Invalidate();
                        } else {
                            lastRoomTapTime = now;
                            UpdateListenerFromRoomTouch(tx, ty, tx, ty, dp);
                        }
                    }
                }
                return true;
            }

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                dragMode = TouchDragMode.None;
                pressedPresetIdx = -1;
                return false;
        }

        return base.OnTouchEvent(e);
    }

    void UpdateListenerFromRoomTouch(float tx, float ty, float prevX, float prevY, float dp) {
        if (roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) {
            return;
        }
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);

        if (camPitchDeg < 16f) {
            // Low front/horizon angle: horizontal drag moves X/Z projected lateral, vertical drag adjusts Listener Height Y directly
            float dyNorm = -(ty - prevY) / Math.Max(40f * dp, roomCanvasRect.Height() * 0.35f);
            float dxNorm = (tx - prevX) / Math.Max(40f * dp, roomCanvasRect.Width() * 0.35f);
            float lnx = Math.Clamp(listenerPosition.X / environmentSize.X + dxNorm * MathF.Cos(camYawRad), -1f, 1f);
            float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z - dxNorm * MathF.Sin(camYawRad), -1f, 1f);
            float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y + dyNorm, -1f, 1f);
            listenerPosition = new Vector3(lnx * environmentSize.X, lny * environmentSize.Y, lnz * environmentSize.Z);
        } else {
            float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
            var (nx, nz) = UnprojectHorizontalPlane(roomCanvasRect, tx, ty, lny, camYawRad, camPitchRad);
            listenerPosition = new Vector3(nx * environmentSize.X, listenerPosition.Y, nz * environmentSize.Z);
        }

        ListenerPositionChanged?.Invoke(listenerPosition);
        Invalidate();
    }

    void UpdateHeightFromDockTouch(float tx) {
        float dp = Resources.DisplayMetrics.Density;
        float barLeft = dockHeightRect.Left + 12f * dp;
        float barW = Math.Max(1f, dockHeightRect.Right - 12f * dp - barLeft);
        float ny = Math.Clamp((tx - barLeft) / barW * 2f - 1f, -1f, 1f);
        listenerPosition = new Vector3(listenerPosition.X, ny * environmentSize.Y, listenerPosition.Z);
        ListenerPositionChanged?.Invoke(listenerPosition);
        Invalidate();
    }

    TrackSourceItem FindNearestSource(float tx, float ty, float maxRadiusPx) {
        TrackSourceItem best = null;
        float bestDistSq = maxRadiusPx * maxRadiusPx;
        for (int i = 0; i < hitTargets.Count; i++) {
            var (item, sx, sy, _, _, _) = hitTargets[i];
            float d2 = (tx - sx) * (tx - sx) + (ty - sy) * (ty - sy);
            if (d2 <= bestDistSq) {
                bestDistSq = d2;
                best = item;
            }
        }
        return best;
    }

    static float PointerDist(MotionEvent e) {
        if (e == null || e.PointerCount < 2) {
            return 0f;
        }
        float dx = e.GetX(0) - e.GetX(1);
        float dy = e.GetY(0) - e.GetY(1);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    static float NormalizeDeg(float deg) {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }
}

internal static class PaintExtensions {
    public static Paint WithColor(this Paint p, Color c) {
        p.Color = c;
        return p;
    }
}
