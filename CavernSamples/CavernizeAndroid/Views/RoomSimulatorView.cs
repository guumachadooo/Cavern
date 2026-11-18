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
/// Next-generation Interactive Dolby Atmos 3D Spatial Room Visualizer:
/// - Volumetric 3D room with shaded translucent walls, floor sweet-spot rings, ear-level horizon frame,
///   ceiling grid, luminous front cinema screen, and subtle RMS acoustic energy rays toward the listener
/// - Articulated 3D Listener Head (L/R earcups, rotating 3D Yaw/Pitch wireframe, tilting 3D FOV cone,
///   and interactive Aim Orb handle) allowing direct drag of Head Yaw (-180°..+180°), Head Pitch (-90°..+90°),
///   and 3D Listener Position (X, Y, Z) directly on the chart with zero extra buttons needed
/// - Integrated translucent Left (Height Y) &amp; Right (Head Tilt Pitch) edge rails + [Sala]/[Cabeça] mode pill
/// - Structured Bed speaker monitor nodes (dB color rings) &amp; clean translucent 3D Object spheres with drop-lines
/// </summary>
public sealed class RoomSimulatorView : View {
    sealed class SmoothedSourceState {
        public Vector3 SmoothedPos;
        public Vector3 LastRawPos;
        public float SmoothedRms;
        public float VisibilityAlpha = 1f;
        public long MovingUntilMs;
        public long SoundUntilMs;
        public readonly (Vector3 Pos, long TimeMs)[] Trail = new (Vector3, long)[8];
        public int TrailHead;
        public int TrailCount;
        public bool Initialized;
    }

    enum LabelVisibilityMode {
        ActiveOnly,
        All,
        Minimal
    }

    enum TouchDragMode {
        None,
        OrbitCamera,
        PinchZoom,
        ListenerMove,
        HeadAim,
        HeightRail,
        PitchRail,
        PresetClick,
        ModeToggleClick,
        LabelsClick,
        ZoomOutClick,
        ZoomInClick,
        ResetZoomClick
    }

    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint roomBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint pillActivePaint = new(PaintFlags.AntiAlias);
    readonly Paint pillInactivePaint = new(PaintFlags.AntiAlias);
    readonly Paint wallShadePaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint horizonRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint floorQuadPaint = new(PaintFlags.AntiAlias);
    readonly Paint ceilingWirePaint = new(PaintFlags.AntiAlias);
    readonly Paint dropLinePaint = new(PaintFlags.AntiAlias);
    readonly Paint dropRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint rayPaint = new(PaintFlags.AntiAlias);
    readonly Paint screenPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint strokePaint = new(PaintFlags.AntiAlias);
    readonly Paint soloRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerConePaint = new(PaintFlags.AntiAlias);
    readonly Paint headWireFrontPaint = new(PaintFlags.AntiAlias);
    readonly Paint headWireBackPaint = new(PaintFlags.AntiAlias);
    readonly Paint railTrackPaint = new(PaintFlags.AntiAlias);
    readonly Paint railFillPaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y)> hitTargets = [];
    readonly Dictionary<TrackSourceItem, SmoothedSourceState> smoothedStates = [];
    readonly RectF[] camPresetRects = [new(), new(), new()];
    RectF camModeBtnRect = new();
    RectF camLabelsBtnRect = new();
    RectF camZoomOutRect = new();
    RectF camZoomInRect = new();
    RectF camResetLabelRect = new();
    RectF roomCanvasRect = new();
    RectF heightRailRect = new();
    RectF pitchRailRect = new();

    float aimOrbScreenX;
    float aimOrbScreenY;
    float listenerScreenX;
    float listenerScreenY;

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero; // X=Pitch (-90..90), Y=Yaw (-180..180)
    bool isAudioPlaying;
    bool hasPlayedOnce;

    LabelVisibilityMode labelMode = LabelVisibilityMode.ActiveOnly;
    bool headTouchMode; // false = [Sala] (drag empty orbits, drag AimOrb steers head, drag Head moves X/Z); true = [Cabeça] (drag anywhere steers Yaw+Pitch)

    const float DefaultRoomZoom = 0.86f;
    float roomZoom = DefaultRoomZoom;
    float pinchStartDist;
    float pinchStartZoom = DefaultRoomZoom;
    float camYawDeg = -24f;
    float camPitchDeg = 28f;
    float targetCamYawDeg = -24f;
    float targetCamPitchDeg = 28f;
    bool cameraAnimating;
    long lastFrameMs;

    DateTime lastRoomTapTime = DateTime.MinValue;
    DateTime lastHeightTapTime = DateTime.MinValue;
    DateTime lastPitchTapTime = DateTime.MinValue;
    DateTime lastHeadTapTime = DateTime.MinValue;

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
        Typeface sfMed = OneUiTheme.GetAppleFontMedium(context);
        Typeface sfBold = OneUiTheme.GetAppleFontBold(context);

        roomBgPaint.Color = Color.Argb(205, 9, 11, 17);
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = OneUiTheme.Divider;
        roomBorderPaint.StrokeWidth = 1.1f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        pillActivePaint.Color = OneUiTheme.Primary;
        pillActivePaint.SetStyle(Paint.Style.Fill);

        pillInactivePaint.Color = Color.Argb(34, 255, 255, 255);
        pillInactivePaint.SetStyle(Paint.Style.Fill);

        wallShadePaint.SetStyle(Paint.Style.Fill);

        gridPaint.Color = Color.Argb(28, 225, 232, 248);
        gridPaint.StrokeWidth = 0.85f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        horizonRingPaint.Color = Color.Argb(54, 130, 175, 255);
        horizonRingPaint.StrokeWidth = 1.0f * dp;
        horizonRingPaint.SetStyle(Paint.Style.Stroke);

        floorQuadPaint.Color = Color.Argb(16, 255, 255, 255);
        floorQuadPaint.SetStyle(Paint.Style.Fill);

        ceilingWirePaint.Color = Color.Argb(52, 150, 185, 255);
        ceilingWirePaint.StrokeWidth = 0.95f * dp;
        ceilingWirePaint.SetStyle(Paint.Style.Stroke);

        dropLinePaint.StrokeWidth = 1.15f * dp;
        dropLinePaint.SetStyle(Paint.Style.Stroke);

        dropRingPaint.StrokeWidth = 1.1f * dp;
        dropRingPaint.SetStyle(Paint.Style.Stroke);

        rayPaint.StrokeWidth = 1.1f * dp;
        rayPaint.SetStyle(Paint.Style.Stroke);

        screenPaint.Color = OneUiTheme.Primary;
        screenPaint.StrokeWidth = 3.8f * dp;
        screenPaint.StrokeCap = Paint.Cap.Round;
        screenPaint.SetStyle(Paint.Style.Stroke);

        fillPaint.SetStyle(Paint.Style.Fill);
        strokePaint.SetStyle(Paint.Style.Stroke);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.2f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.SetStyle(Paint.Style.Fill);

        headWireFrontPaint.Color = Color.Argb(165, 255, 255, 255);
        headWireFrontPaint.StrokeWidth = 1.05f * dp;
        headWireFrontPaint.SetStyle(Paint.Style.Stroke);

        headWireBackPaint.Color = Color.Argb(45, 255, 255, 255);
        headWireBackPaint.StrokeWidth = 0.85f * dp;
        headWireBackPaint.SetStyle(Paint.Style.Stroke);

        railTrackPaint.Color = Color.Argb(44, 255, 255, 255);
        railTrackPaint.SetStyle(Paint.Style.Fill);

        railFillPaint.SetStyle(Paint.Style.Fill);

        textPaint.Color = OneUiTheme.TextPrimary;
        textPaint.TextSize = 9.5f * dp;
        textPaint.SetTypeface(sfBold);
        textPaint.TextAlign = Paint.Align.Center;

        subTextPaint.Color = OneUiTheme.TextMuted;
        subTextPaint.TextSize = 9.5f * dp;
        subTextPaint.SetTypeface(sfMed);
        subTextPaint.TextAlign = Paint.Align.Center;
    }

    public void UpdateState(IReadOnlyList<TrackSourceItem> sourceItems, Vector3 envSize, Vector3 pos, Vector3 rot, bool isPlaying = false) {
        IReadOnlyList<TrackSourceItem> nextSources = sourceItems ?? [];
        bool trackReplaced = sources.Count != nextSources.Count
            || (sources.Count > 0 && nextSources.Count > 0 && !ReferenceEquals(sources[0], nextSources[0]));
        if (trackReplaced) {
            hasPlayedOnce = isPlaying;
        } else if (isPlaying) {
            hasPlayedOnce = true;
        }
        sources = nextSources;
        if (envSize.X > 0.1f && envSize.Y > 0.1f && envSize.Z > 0.1f) {
            environmentSize = envSize;
        }
        listenerPosition = pos;
        listenerRotation = rot;
        isAudioPlaying = isPlaying;

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
        int desiredH = Math.Max((int)(w * 0.86f), (int)(336f * dp));
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

        // Top toolbar: [Mode: Cabeça/Sala] | [Labels] | [- / +] | [3D] [Topo] [Frente]
        float topBarH = 26f * dp;
        float topY = outerPad;
        float presetW = 39f * dp;
        float presetGap = 3.5f * dp;
        float zoomBtnSz = 23f * dp;
        float presetsTotalW = presetW * 3f + presetGap * 2f;
        float presetsStartX = outerPad + w - presetsTotalW;

        float zInX = presetsStartX - 5f * dp - zoomBtnSz;
        float zOutX = zInX - 3.5f * dp - zoomBtnSz;
        float zTop = topY + (topBarH - zoomBtnSz) * 0.5f;
        camZoomOutRect = new RectF(zOutX, zTop, zOutX + zoomBtnSz, zTop + zoomBtnSz);
        camZoomInRect = new RectF(zInX, zTop, zInX + zoomBtnSz, zTop + zoomBtnSz);

        float lblBtnW = 44f * dp;
        float lblBtnX = zOutX - 5f * dp - lblBtnW;
        camLabelsBtnRect = new RectF(lblBtnX, topY, lblBtnX + lblBtnW, topY + topBarH);

        float modeBtnW = 58f * dp;
        float modeBtnX = lblBtnX - 5f * dp - modeBtnW;
        camModeBtnRect = new RectF(Math.Max(outerPad + 54f * dp, modeBtnX), topY, lblBtnX - 5f * dp, topY + topBarH);
        camResetLabelRect = new RectF(outerPad, topY, camModeBtnRect.Left - 3f * dp, topY + topBarH);

        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 9.5f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        canvas.DrawText(
            $"{Math.Round(roomZoom * 100)}%",
            outerPad + 3f * dp,
            topY + topBarH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f,
            subTextPaint);

        // Mode toggle pill: [Cabeça] vs [Sala]
        canvas.DrawRoundRect(camModeBtnRect, topBarH * 0.5f, topBarH * 0.5f,
            headTouchMode ? pillActivePaint : pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.2f * dp;
        textPaint.Color = headTouchMode ? OneUiTheme.TextPrimary : OneUiTheme.PrimaryHigh;
        canvas.DrawText(headTouchMode ? "Cabeça" : "Sala", camModeBtnRect.CenterX(),
            camModeBtnRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);

        // Labels toggle pill
        canvas.DrawRoundRect(camLabelsBtnRect, topBarH * 0.5f, topBarH * 0.5f, pillInactivePaint);
        textPaint.TextSize = 9.0f * dp;
        textPaint.Color = labelMode == LabelVisibilityMode.All ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSub;
        string lblTag = labelMode switch {
            LabelVisibilityMode.All => "Todos",
            LabelVisibilityMode.Minimal => "Min",
            _ => "Ativos"
        };
        canvas.DrawText(lblTag, camLabelsBtnRect.CenterX(),
            camLabelsBtnRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);

        // Zoom - / + buttons
        canvas.DrawOval(camZoomOutRect, dragMode == TouchDragMode.ZoomOutClick ? pillActivePaint : pillInactivePaint);
        canvas.DrawOval(camZoomInRect, dragMode == TouchDragMode.ZoomInClick ? pillActivePaint : pillInactivePaint);
        float arm = zoomBtnSz * 0.22f;
        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = OneUiTheme.TextPrimary;
        canvas.DrawLine(camZoomOutRect.CenterX() - arm, camZoomOutRect.CenterY(), camZoomOutRect.CenterX() + arm, camZoomOutRect.CenterY(), strokePaint);
        canvas.DrawLine(camZoomInRect.CenterX() - arm, camZoomInRect.CenterY(), camZoomInRect.CenterX() + arm, camZoomInRect.CenterY(), strokePaint);
        canvas.DrawLine(camZoomInRect.CenterX(), camZoomInRect.CenterY() - arm, camZoomInRect.CenterX(), camZoomInRect.CenterY() + arm, strokePaint);

        (string label, float py, float pp)[] presets = [
            ("3D", -24f, 28f),
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
            textPaint.TextSize = 9.3f * dp;
            textPaint.Color = isClose ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
            canvas.DrawText(presets[i].label, camPresetRects[i].CenterX(),
                camPresetRects[i].CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
        }

        // Full 3D Viewport + Side HUD Rails (Left = Height Y, Right = Head Pitch)
        float roomTop = topY + topBarH + 8f * dp;
        float roomBottom = outerPad + h;
        roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w, Math.Max(roomTop + 170f * dp, roomBottom));

        float railPadTop = roomCanvasRect.Top + 34f * dp;
        float railPadBot = roomCanvasRect.Bottom - 30f * dp;
        heightRailRect = new RectF(roomCanvasRect.Left + 4f * dp, railPadTop, roomCanvasRect.Left + 30f * dp, railPadBot);
        pitchRailRect = new RectF(roomCanvasRect.Right - 30f * dp, railPadTop, roomCanvasRect.Right - 4f * dp, railPadBot);

        hitTargets.Clear();
        bool anySolo = SourcesAnySolo();
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);
        float headYawRad = listenerRotation.Y * (MathF.PI / 180f);
        float headPitchRad = listenerRotation.X * (MathF.PI / 180f);

        bool stillAnimating = DrawDolbyStyle3DRoom(
            canvas, roomCanvasRect, anySolo, dt, nowMs, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);

        if ((isAudioPlaying || cameraAnimating || stillAnimating)
            && Visibility == ViewStates.Visible
            && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    (float cx, float cy, float radiusPx) GetCameraFraming(RectF r) {
        return (r.CenterX(), r.CenterY() + r.Height() * 0.02f, Math.Min(r.Width() - 48f * Resources.DisplayMetrics.Density, r.Height()) * 0.375f * roomZoom);
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
        const float camDist = 3.75f;
        var (cx, cy, rad) = GetCameraFraming(r);
        Vector3 cam = TransformWorldToCamera(nx, ny, nz, camYawRad, camPitchRad);
        float denom = Math.Max(1.30f, camDist + cam.Z);
        float persp = camDist / denom;
        float sx = cx + cam.X * rad * persp;
        float sy = cy - cam.Y * rad * persp;
        return (sx, sy, persp, cam.Z);
    }

    (float nx, float nz) UnprojectHorizontalPlane(
        RectF r, float sx, float sy, float fixedNy, float camYawRad, float camPitchRad) {
        const float camDist = 3.75f;
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
        float x1 = u * Math.Max(1.30f, camDist + z2) / camDist;

        float cosA = MathF.Cos(camYawRad);
        float sinA = MathF.Sin(camYawRad);
        float nx = Math.Clamp(x1 * cosA + z1 * sinA, -1f, 1f);
        float nz = Math.Clamp(-x1 * sinA + z1 * cosA, -1f, 1f);
        return (nx, nz);
    }

    static Vector3 HeadLookVector(float yawRad, float pitchRad) {
        float cosP = MathF.Cos(pitchRad);
        return new Vector3(
            MathF.Sin(yawRad) * cosP,
            MathF.Sin(pitchRad),
            MathF.Cos(yawRad) * cosP);
    }

    bool DrawDolbyStyle3DRoom(
        Canvas canvas, RectF r, bool anySolo, float dt, long nowMs,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        canvas.Save();
        Android.Graphics.Path clipRoom = new();
        clipRoom.AddRoundRect(r, corner, corner, Android.Graphics.Path.Direction.Cw);
        canvas.ClipPath(clipRoom);

        // Corner vertices for floor (-1) and ceiling (+1)
        var fl_bl = ProjectOrbit3D(r, -1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_br = ProjectOrbit3D(r, 1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_fr = ProjectOrbit3D(r, 1f, -1f, 1f, camYawRad, camPitchRad);
        var fl_fl = ProjectOrbit3D(r, -1f, -1f, 1f, camYawRad, camPitchRad);

        var ce_bl = ProjectOrbit3D(r, -1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_br = ProjectOrbit3D(r, 1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_fr = ProjectOrbit3D(r, 1f, 1f, 1f, camYawRad, camPitchRad);
        var ce_fl = ProjectOrbit3D(r, -1f, 1f, 1f, camYawRad, camPitchRad);

        // 1. Subtle Depth-Shaded Acoustic Walls (only far walls behind camera for true 3D room depth)
        void DrawWallQuad(
            (float sx, float sy, float persp, float depth) a,
            (float sx, float sy, float persp, float depth) b,
            (float sx, float sy, float persp, float depth) c,
            (float sx, float sy, float persp, float depth) d,
            int alpha) {
            float avgDepth = (a.depth + b.depth + c.depth + d.depth) * 0.25f;
            if (avgDepth < -0.15f) return; // Skip near wall facing camera so view stays open
            using Android.Graphics.Path wp = new();
            wp.MoveTo(a.sx, a.sy);
            wp.LineTo(b.sx, b.sy);
            wp.LineTo(c.sx, c.sy);
            wp.LineTo(d.sx, d.sy);
            wp.Close();
            wallShadePaint.Color = Color.Argb(alpha, 150, 178, 235);
            canvas.DrawPath(wp, wallShadePaint);
        }
        DrawWallQuad(fl_bl, fl_br, ce_br, ce_bl, 12); // Back wall
        DrawWallQuad(fl_fl, fl_bl, ce_bl, ce_fl, 9);  // Left wall
        DrawWallQuad(fl_br, fl_fr, ce_fr, ce_br, 9);  // Right wall
        DrawWallQuad(fl_fl, fl_fr, ce_fr, ce_fl, 14); // Front wall

        // 2. Floor Quad + Architectural Grid + Concentric Sweet-Spot Rings
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

        // Sweet-spot polar ring on floor
        DrawProjectedRing(canvas, r, 0f, -1f, 0f, 0.45f, camYawRad, camPitchRad, gridPaint);

        // 3. Ear-level Horizon Reference Plane (Y = 0) & Ceiling Cage (+1 Y)
        var ear_bl = ProjectOrbit3D(r, -1f, 0f, -1f, camYawRad, camPitchRad);
        var ear_br = ProjectOrbit3D(r, 1f, 0f, -1f, camYawRad, camPitchRad);
        var ear_fr = ProjectOrbit3D(r, 1f, 0f, 1f, camYawRad, camPitchRad);
        var ear_fl = ProjectOrbit3D(r, -1f, 0f, 1f, camYawRad, camPitchRad);
        using (Android.Graphics.Path earPath = new()) {
            earPath.MoveTo(ear_bl.sx, ear_bl.sy);
            earPath.LineTo(ear_br.sx, ear_br.sy);
            earPath.LineTo(ear_fr.sx, ear_fr.sy);
            earPath.LineTo(ear_fl.sx, ear_fl.sy);
            earPath.Close();
            canvas.DrawPath(earPath, horizonRingPaint);
        }

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

        // 4. Front Cinema Screen Bar (+Z wall) + Ambient Light Wash
        var scrL = ProjectOrbit3D(r, -0.58f, 0.16f, 1f, camYawRad, camPitchRad);
        var scrR = ProjectOrbit3D(r, 0.58f, 0.16f, 1f, camYawRad, camPitchRad);
        var scrBotL = ProjectOrbit3D(r, -0.46f, -1f, 0.98f, camYawRad, camPitchRad);
        var scrBotR = ProjectOrbit3D(r, 0.46f, -1f, 0.98f, camYawRad, camPitchRad);
        using (Android.Graphics.Path washPath = new()) {
            washPath.MoveTo(scrL.sx, scrL.sy);
            washPath.LineTo(scrR.sx, scrR.sy);
            washPath.LineTo(scrBotR.sx, scrBotR.sy);
            washPath.LineTo(scrBotL.sx, scrBotL.sy);
            washPath.Close();
            wallShadePaint.Color = Color.Argb(20, OneUiTheme.Primary.R, OneUiTheme.Primary.G, OneUiTheme.Primary.B);
            canvas.DrawPath(washPath, wallShadePaint);
        }
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(scrL.sx, scrL.sy, scrR.sx, scrR.sy, screenPaint);
        var scrMid = ProjectOrbit3D(r, 0f, 0.30f, 1.02f, camYawRad, camPitchRad);
        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 8.2f * dp;
        subTextPaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawText("TELA / FRONT", scrMid.sx, scrMid.sy - 2f * dp, subTextPaint);

        // 5. Smooth Source State Interpolation, Idle Visibility & Depth Sort
        bool anyInterpolating = false;
        List<(bool isListener, TrackSourceItem item, int objNumber, Vector3 np, float rms, float visAlpha, SmoothedSourceState state, float depth)> renderQueue = new(sources.Count + 1);

        float posBlend = Math.Min(1f, dt * 18f);
        float rmsRise = Math.Min(1f, dt * 26f);
        float rmsFall = Math.Min(1f, dt * 11f);
        int objSeq = 0;

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            if (!item.IsBed) objSeq++;

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
                st.LastRawPos = targetPos;
                st.SmoothedRms = targetRms;
                st.VisibilityAlpha = 1f;
                st.Initialized = true;
            } else {
                Vector3 rawMove = targetPos - st.LastRawPos;
                if (rawMove.LengthSquared() > 0.000004f) {
                    st.MovingUntilMs = nowMs + 480;
                }
                st.LastRawPos = targetPos;

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
            }

            bool hasVolumeNow = targetRms > 0.0025f || st.SmoothedRms > 0.0035f;
            if (hasVolumeNow) {
                st.SoundUntilMs = nowMs + 380;
            }

            float targetVis;
            if (item.IsBed || !hasPlayedOnce) {
                targetVis = 1f;
            } else if (!isAudioPlaying) {
                targetVis = st.VisibilityAlpha;
            } else {
                bool isActive = item.IsSolo || hasVolumeNow || nowMs < st.SoundUntilMs || nowMs < st.MovingUntilMs;
                targetVis = isActive ? 1f : 0f;
            }

            float visDiff = targetVis - st.VisibilityAlpha;
            if (MathF.Abs(visDiff) > 0.008f) {
                float visRate = visDiff > 0 ? 16f : 4.8f;
                st.VisibilityAlpha = Math.Clamp(st.VisibilityAlpha + visDiff * Math.Min(1f, dt * visRate), 0f, 1f);
                anyInterpolating = true;
            } else {
                st.VisibilityAlpha = targetVis;
            }

            if (isAudioPlaying && !item.IsBed && st.VisibilityAlpha > 0.08f) {
                int lastIdx = (st.TrailHead + st.Trail.Length - 1) % st.Trail.Length;
                bool emptyTrail = st.TrailCount == 0;
                long elapsedTrail = emptyTrail ? 9999 : nowMs - st.Trail[lastIdx].TimeMs;
                float movedSq = emptyTrail ? 1f : (st.SmoothedPos - st.Trail[lastIdx].Pos).LengthSquared();
                if (elapsedTrail >= 55 && movedSq > 0.00055f) {
                    st.Trail[st.TrailHead] = (st.SmoothedPos, nowMs);
                    st.TrailHead = (st.TrailHead + 1) % st.Trail.Length;
                    if (st.TrailCount < st.Trail.Length) st.TrailCount++;
                }
            }

            var pt = ProjectOrbit3D(r, st.SmoothedPos.X, st.SmoothedPos.Y, st.SmoothedPos.Z, camYawRad, camPitchRad);
            renderQueue.Add((false, item, item.IsBed ? 0 : objSeq, st.SmoothedPos, st.SmoothedRms, st.VisibilityAlpha, st, pt.depth));
        }

        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        var lCenter = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);
        listenerScreenX = lCenter.sx;
        listenerScreenY = lCenter.sy;
        renderQueue.Add((true, null, 0, new Vector3(lnx, lny, lnz), 0f, 1f, null, lCenter.depth));

        renderQueue.Sort((a, b) => b.depth.CompareTo(a.depth));

        // 6. Subtle Acoustic Energy Rays from active objects/beds to the Listener Head
        for (int i = 0; i < renderQueue.Count; i++) {
            var e = renderQueue[i];
            if (e.isListener || e.visAlpha <= 0.04f || e.rms < 0.04f) continue;
            bool effMuted = e.item.IsMuted || (anySolo && !e.item.IsSolo);
            if (effMuted) continue;
            var sPt = ProjectOrbit3D(r, e.np.X, e.np.Y, e.np.Z, camYawRad, camPitchRad);
            Color mc = ResolveDolbyMeterColor(e.rms, false, e.item.IsBed, e.item.IsLFE);
            int rayAlpha = Math.Clamp((int)Math.Round(e.rms * 78f * e.visAlpha), 6, 72);
            rayPaint.Color = Color.Argb(rayAlpha, mc.R, mc.G, mc.B);
            canvas.DrawLine(sPt.sx, sPt.sy, lCenter.sx, lCenter.sy, rayPaint);
        }

        // 7. Depth-Sorted Sources & Volumetric 3D Listener Head
        foreach (var entry in renderQueue) {
            if (entry.isListener) {
                DrawListenerInRoom(canvas, r, lnx, lny, lnz, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);
                continue;
            }
            if (entry.visAlpha <= 0.02f) {
                continue;
            }

            TrackSourceItem item = entry.item;
            Vector3 np = entry.np;
            float va = entry.visAlpha;
            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            var posPt = ProjectOrbit3D(r, np.X, np.Y, np.Z, camYawRad, camPitchRad);
            hitTargets.Add((item, posPt.sx, posPt.sy));

            Color meterCol = ResolveDolbyMeterColor(entry.rms, effectivelyMuted, item.IsBed, item.IsLFE);
            float scale = Math.Clamp(posPt.persp * 1.04f, 0.72f, 1.25f);

            if (item.IsBed) {
                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = Color.Argb(36, 200, 212, 235);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                float hw = 8.4f * dp * scale;
                float hh = 6.6f * dp * scale;
                RectF box = new(posPt.sx - hw, posPt.sy - hh, posPt.sx + hw, posPt.sy + hh);

                if (!effectivelyMuted && entry.rms > 0.10f) {
                    float pulseR = hw + (3f + Math.Clamp(entry.rms * 7f, 0f, 6f)) * dp;
                    fillPaint.SetShader(null);
                    fillPaint.Color = Color.Argb(Math.Clamp((int)(entry.rms * 65f), 16, 56), meterCol.R, meterCol.G, meterCol.B);
                    canvas.DrawCircle(posPt.sx, posPt.sy, pulseR, fillPaint);
                }

                fillPaint.SetShader(null);
                fillPaint.Color = Color.Argb(228, 22, 26, 36);
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, fillPaint);

                strokePaint.StrokeWidth = (entry.rms > 0.02f ? 2.1f : 1.3f) * dp;
                strokePaint.Color = meterCol;
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, strokePaint);

                fillPaint.Color = meterCol;
                canvas.DrawCircle(posPt.sx, posPt.sy, (2.2f + Math.Clamp(entry.rms * 2.4f, 0f, 2.0f)) * dp * scale, fillPaint);

                if (effectivelyMuted) {
                    DrawMuteCross(canvas, posPt.sx, posPt.sy, hw * 0.55f, dp);
                }
                if (item.IsSolo) {
                    canvas.DrawRoundRect(new RectF(box.Left - 3f * dp, box.Top - 3f * dp, box.Right + 3f * dp, box.Bottom + 3f * dp),
                        4.5f * dp, 4.5f * dp, soloRingPaint);
                }

                if (labelMode != LabelVisibilityMode.Minimal || item.IsSolo) {
                    string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                        ? item.Channel.Value.GetShortName()
                        : $"B{item.Index + 1}";
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = 8.4f * dp;
                    textPaint.Color = effectivelyMuted ? OneUiTheme.TextMuted : Color.Argb(225, 240, 244, 255);
                    canvas.DrawText(shortLabel, posPt.sx, posPt.sy + hh + 9.5f * dp, textPaint);
                }
            } else {
                // Subtle v2.6 motion trails behind moving 3D objects
                if (entry.state != null && entry.state.TrailCount > 0) {
                    SmoothedSourceState tState = entry.state;
                    int oldestIdx = (tState.TrailHead - tState.TrailCount + tState.Trail.Length) % tState.Trail.Length;
                    for (int k = 0; k < tState.TrailCount; k++) {
                        int idx = (oldestIdx + k) % tState.Trail.Length;
                        var sample = tState.Trail[idx];
                        long ageMs = nowMs - sample.TimeMs;
                        if (ageMs < 0 || ageMs > 520) continue;
                        float ageFade = 1f - ageMs / 520f;
                        var tPt = ProjectOrbit3D(r, sample.Pos.X, sample.Pos.Y, sample.Pos.Z, camYawRad, camPitchRad);
                        int tAlpha = Math.Clamp((int)Math.Round(105f * ageFade * va), 4, 120);
                        fillPaint.SetShader(null);
                        fillPaint.Color = Color.Argb(tAlpha, meterCol.R, meterCol.G, meterCol.B);
                        canvas.DrawCircle(tPt.sx, tPt.sy, (1.4f + 2.4f * ageFade) * dp * tPt.persp, fillPaint);
                        anyInterpolating = true;
                    }
                }

                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = effectivelyMuted
                    ? Color.Argb((int)Math.Round(35 * va), 160, 165, 180)
                    : Color.Argb((int)Math.Round(115 * va), meterCol.R, meterCol.G, meterCol.B);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                // Tick mark where drop-line intersects Y=0 ear horizon
                var earTick = ProjectOrbit3D(r, np.X, 0f, np.Z, camYawRad, camPitchRad);
                fillPaint.SetShader(null);
                fillPaint.Color = dropLinePaint.Color;
                canvas.DrawCircle(earTick.sx, earTick.sy, 1.8f * dp, fillPaint);

                dropRingPaint.Color = dropLinePaint.Color;
                canvas.DrawCircle(floorPt.sx, floorPt.sy, 3.8f * dp * floorPt.persp, dropRingPaint);

                float appearScale = 0.72f + 0.28f * va;
                float rad = (7.2f + Math.Clamp(entry.rms * 3.8f, 0f, 3.2f)) * dp * scale * appearScale;
                if (!effectivelyMuted && entry.rms > 0.08f) {
                    float wave = (float)(0.5 + 0.5 * Math.Sin(nowMs * 0.008 + entry.objNumber));
                    float haloR = rad + (3f + wave * 4f * entry.rms) * dp;
                    fillPaint.Color = Color.Argb(Math.Clamp((int)(entry.rms * 58f * va), 6, 52), meterCol.R, meterCol.G, meterCol.B);
                    canvas.DrawCircle(posPt.sx, posPt.sy, haloR, fillPaint);
                }

                using (RadialGradient objGrad = new(
                    posPt.sx - rad * 0.25f, posPt.sy - rad * 0.25f, Math.Max(1f, rad * 1.15f),
                    [
                        Color.Argb((int)Math.Round((effectivelyMuted ? 95 : 235) * va), Math.Min(255, meterCol.R + 40), Math.Min(255, meterCol.G + 40), Math.Min(255, meterCol.B + 40)).ToArgb(),
                        Color.Argb((int)Math.Round((effectivelyMuted ? 65 : 195) * va), meterCol.R, meterCol.G, meterCol.B).ToArgb()
                    ],
                    [0f, 1f], Shader.TileMode.Clamp)) {
                    fillPaint.SetShader(objGrad);
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad, fillPaint);
                    fillPaint.SetShader(null);
                }

                strokePaint.StrokeWidth = 1.25f * dp;
                strokePaint.Color = effectivelyMuted
                    ? Color.Argb((int)Math.Round(130 * va), OneUiTheme.TextMuted.R, OneUiTheme.TextMuted.G, OneUiTheme.TextMuted.B)
                    : Color.Argb((int)Math.Round(200 * va), 255, 255, 255);
                canvas.DrawCircle(posPt.sx, posPt.sy, rad, strokePaint);

                if (effectivelyMuted) {
                    DrawMuteCross(canvas, posPt.sx, posPt.sy, rad * 0.55f, dp);
                } else {
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = Math.Clamp(8.2f * scale * appearScale, 6f, 10f) * dp;
                    textPaint.Color = Color.Argb((int)Math.Round(255 * va), 12, 14, 20);
                    canvas.DrawText(entry.objNumber.ToString(), posPt.sx,
                        posPt.sy - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
                }

                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 3.2f * dp, soloRingPaint);
                }

                if (labelMode == LabelVisibilityMode.All) {
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = 8.2f * dp;
                    textPaint.Color = Color.Argb((int)Math.Round(210 * va), OneUiTheme.TextSecondary.R, OneUiTheme.TextSecondary.G, OneUiTheme.TextSecondary.B);
                    canvas.DrawText($"O{entry.objNumber}", posPt.sx, posPt.sy - rad - 3.5f * dp, textPaint);
                }
            }
        }

        // 8. Integrated Side Translucent HUD Rails (Left = Height Y, Right = Head Pitch) + Live Status Chip
        DrawEdgeRail(canvas, heightRailRect, "ALT Y", lny, $"{lny * environmentSize.Y:+0.0;-0.0;0.0}m", OneUiTheme.IconStemsCalib, dp);
        float pitchNorm = Math.Clamp(listenerRotation.X / 90f, -1f, 1f);
        DrawEdgeRail(canvas, pitchRailRect, "INCL", pitchNorm, $"{listenerRotation.X:+0;-0;0}°", OneUiTheme.PrimaryHigh, dp);

        // Bottom live orientation & position pill
        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 9.0f * dp;
        subTextPaint.Color = Color.Argb(215, 226, 232, 246);
        string statusLine = $"Giro {listenerRotation.Y:+0;-0;0}°  •  Incl {listenerRotation.X:+0;-0;0}°  •  Arraste o alvo na frente da cabeça ou ative [Cabeça]";
        canvas.DrawText(statusLine, r.CenterX(), r.Bottom - 8f * dp, subTextPaint);

        canvas.Restore();
        return anyInterpolating;
    }

    void DrawProjectedRing(
        Canvas canvas, RectF r, float cx, float cy, float cz, float radNorm,
        float camYawRad, float camPitchRad, Paint paint) {
        const int segs = 28;
        var p0 = ProjectOrbit3D(r, cx + radNorm, cy, cz, camYawRad, camPitchRad);
        using Android.Graphics.Path ringPath = new();
        ringPath.MoveTo(p0.sx, p0.sy);
        for (int i = 1; i <= segs; i++) {
            float a = i / (float)segs * MathF.PI * 2f;
            var pi = ProjectOrbit3D(r, cx + MathF.Cos(a) * radNorm, cy, cz + MathF.Sin(a) * radNorm, camYawRad, camPitchRad);
            ringPath.LineTo(pi.sx, pi.sy);
        }
        ringPath.Close();
        canvas.DrawPath(ringPath, paint);
    }

    void DrawEdgeRail(Canvas canvas, RectF rect, string topTitle, float normVal, string valLabel, Color accent, float dp) {
        float trackW = 5f * dp;
        float trackX = rect.CenterX();
        float trackTop = rect.Top + 16f * dp;
        float trackBot = rect.Bottom - 16f * dp;
        float trackH = Math.Max(20f * dp, trackBot - trackTop);
        float midY = trackTop + trackH * 0.5f;

        RectF track = new(trackX - trackW * 0.5f, trackTop, trackX + trackW * 0.5f, trackBot);
        canvas.DrawRoundRect(track, trackW * 0.5f, trackW * 0.5f, railTrackPaint);

        // Center zero notch
        strokePaint.StrokeWidth = 1.2f * dp;
        strokePaint.Color = Color.Argb(95, 255, 255, 255);
        canvas.DrawLine(trackX - 5f * dp, midY, trackX + 5f * dp, midY, strokePaint);

        // Active fill bar from center (0) to normVal (-1..+1, +1 is UP)
        float knobY = midY - Math.Clamp(normVal, -1f, 1f) * (trackH * 0.5f);
        railFillPaint.Color = Color.Argb(185, accent.R, accent.G, accent.B);
        float fillTop = Math.Min(midY, knobY);
        float fillBot = Math.Max(midY, knobY);
        if (fillBot - fillTop > 1f) {
            canvas.DrawRoundRect(new RectF(track.Left, fillTop, track.Right, fillBot), trackW * 0.5f, trackW * 0.5f, railFillPaint);
        }

        // Thumb dot
        fillPaint.SetShader(null);
        fillPaint.Color = Color.White;
        canvas.DrawCircle(trackX, knobY, 5.2f * dp, fillPaint);
        strokePaint.StrokeWidth = 1.8f * dp;
        strokePaint.Color = accent;
        canvas.DrawCircle(trackX, knobY, 5.2f * dp, strokePaint);

        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 7.8f * dp;
        subTextPaint.Color = Color.Argb(180, 220, 226, 240);
        canvas.DrawText(topTitle, trackX, rect.Top + 9f * dp, subTextPaint);
        subTextPaint.Color = accent;
        canvas.DrawText(valLabel, trackX, rect.Bottom - 2f * dp, subTextPaint);
    }

    static Color ResolveDolbyMeterColor(float rms, bool muted, bool isBed, bool isLfe) {
        if (muted) {
            return Color.Rgb(72, 76, 88);
        }
        if (rms >= 0.55f) {
            return Color.Rgb(255, 84, 84);
        }
        if (rms >= 0.24f) {
            return Color.Rgb(248, 196, 62);
        }
        if (rms >= 0.015f) {
            return isLfe ? OneUiTheme.LfeChannel : Color.Rgb(58, 214, 132);
        }
        if (isLfe) return Color.Argb(180, OneUiTheme.LfeChannel.R, OneUiTheme.LfeChannel.G, OneUiTheme.LfeChannel.B);
        return isBed ? Color.Rgb(98, 118, 148) : Color.Rgb(126, 178, 255);
    }

    void DrawMuteCross(Canvas canvas, float cx, float cy, float halfArm, float dp) {
        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = OneUiTheme.FunctionalRedFill;
        canvas.DrawLine(cx - halfArm, cy - halfArm, cx + halfArm, cy + halfArm, strokePaint);
        canvas.DrawLine(cx + halfArm, cy - halfArm, cx - halfArm, cy + halfArm, strokePaint);
    }

    void DrawListenerInRoom(
        Canvas canvas, RectF r, float lnx, float lny, float lnz,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        var lFloor = ProjectOrbit3D(r, lnx, -1f, lnz, camYawRad, camPitchRad);
        var lPos = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);

        dropLinePaint.Color = Color.Argb(145, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        canvas.DrawLine(lFloor.sx, lFloor.sy, lPos.sx, lPos.sy, dropLinePaint);
        dropRingPaint.Color = dropLinePaint.Color;
        canvas.DrawCircle(lFloor.sx, lFloor.sy, 4.5f * dp * lFloor.persp, dropRingPaint);

        // 3D Volumetric Listening Cone tilting with both Head Yaw and Head Pitch
        const float yawSpread = 0.40f;
        const float pitchSpread = 0.22f;
        const float coneReach = 0.44f;
        Vector3 lookC = HeadLookVector(headYawRad, headPitchRad);
        Vector3 lookL = HeadLookVector(headYawRad - yawSpread, headPitchRad);
        Vector3 lookR = HeadLookVector(headYawRad + yawSpread, headPitchRad);
        Vector3 lookU = HeadLookVector(headYawRad, Math.Clamp(headPitchRad + pitchSpread, -1.48f, 1.48f));
        Vector3 lookD = HeadLookVector(headYawRad, Math.Clamp(headPitchRad - pitchSpread, -1.48f, 1.48f));

        (float sx, float sy, float persp, float depth) ProjVec(Vector3 dir, float scale) =>
            ProjectOrbit3D(r,
                Math.Clamp(lnx + dir.X * scale, -1f, 1f),
                Math.Clamp(lny + dir.Y * scale, -1f, 1f),
                Math.Clamp(lnz + dir.Z * scale, -1f, 1f),
                camYawRad, camPitchRad);

        var fovC = ProjVec(lookC, coneReach);
        var fovL = ProjVec(lookL, coneReach);
        var fovR = ProjVec(lookR, coneReach);
        var fovU = ProjVec(lookU, coneReach * 0.92f);
        var fovD = ProjVec(lookD, coneReach * 0.92f);

        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lPos.sx, lPos.sy);
            cone.LineTo(fovL.sx, fovL.sy);
            cone.LineTo(fovU.sx, fovU.sy);
            cone.LineTo(fovR.sx, fovR.sy);
            cone.LineTo(fovD.sx, fovD.sy);
            cone.Close();
            using LinearGradient coneGrad = new(
                lPos.sx, lPos.sy, fovC.sx, fovC.sy,
                [Color.Argb(105, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        // Articulated 3D Shaded Head Sphere + Wireframe + L/R Earcups
        float rad = 13.5f * dp * Math.Clamp(lPos.persp, 0.78f, 1.18f);
        Color p = OneUiTheme.Primary;
        Color hi = OneUiTheme.PrimaryHigh;
        Color dk = OneUiTheme.PrimaryPressed;

        using (RadialGradient sphereGrad = new(
            lPos.sx - rad * 0.28f, lPos.sy - rad * 0.28f, Math.Max(1f, rad * 1.15f),
            [Color.Argb(245, hi.R, hi.G, hi.B).ToArgb(), Color.Argb(232, p.R, p.G, p.B).ToArgb(), Color.Argb(240, dk.R, dk.G, dk.B).ToArgb()],
            [0f, 0.55f, 1f], Shader.TileMode.Clamp)) {
            fillPaint.SetShader(sphereGrad);
            canvas.DrawCircle(lPos.sx, lPos.sy, rad, fillPaint);
            fillPaint.SetShader(null);
        }
        strokePaint.StrokeWidth = 1.5f * dp;
        strokePaint.Color = hi;
        canvas.DrawCircle(lPos.sx, lPos.sy, rad, strokePaint);

        Vector3 ProjectHeadPoint(float lx, float ly, float lz) {
            float cosP = MathF.Cos(headPitchRad);
            float sinP = MathF.Sin(headPitchRad);
            float y1 = ly * cosP + lz * sinP;
            float z1 = -ly * sinP + lz * cosP;

            float cosY = MathF.Cos(headYawRad);
            float sinY = MathF.Sin(headYawRad);
            float x2 = lx * cosY + z1 * sinY;
            float z2 = -lx * sinY + z1 * cosY;

            Vector3 cam = TransformWorldToCamera(x2, y1, z2, camYawRad, camPitchRad);
            return new Vector3(lPos.sx + cam.X * rad, lPos.sy - cam.Y * rad, -cam.Z);
        }

        void Draw3DRing(Func<float, Vector3> ringFn) {
            const int segs = 18;
            Vector3 pt0 = ringFn(0f);
            Vector3 prev = ProjectHeadPoint(pt0.X, pt0.Y, pt0.Z);
            for (int i = 1; i <= segs; i++) {
                float a = i / (float)segs * MathF.PI * 2f;
                Vector3 pt = ringFn(a);
                Vector3 cur = ProjectHeadPoint(pt.X, pt.Y, pt.Z);
                Paint wp = (prev.Z + cur.Z) >= 0f ? headWireFrontPaint : headWireBackPaint;
                canvas.DrawLine(prev.X, prev.Y, cur.X, cur.Y, wp);
                prev = cur;
            }
        }

        Draw3DRing(a => new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
        Draw3DRing(a => new Vector3(0f, MathF.Sin(a), MathF.Cos(a)));

        Vector3 leftEar = ProjectHeadPoint(-1.05f, 0f, 0f);
        Vector3 rightEar = ProjectHeadPoint(1.05f, 0f, 0f);
        fillPaint.SetShader(null);
        fillPaint.Color = Color.Argb(240, 252, 252, 255);
        canvas.DrawCircle(leftEar.X, leftEar.Y, rad * 0.22f, fillPaint);
        canvas.DrawCircle(rightEar.X, rightEar.Y, rad * 0.22f, fillPaint);

        // Interactive Aim Orb Handle extending from the listener's nose vector
        var aimOrbPt = ProjVec(lookC, 0.42f);
        aimOrbScreenX = aimOrbPt.sx;
        aimOrbScreenY = aimOrbPt.sy;

        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = Color.Argb(215, 255, 255, 255);
        canvas.DrawLine(lPos.sx, lPos.sy, aimOrbScreenX, aimOrbScreenY, strokePaint);

        float orbR = (dragMode == TouchDragMode.HeadAim ? 9.5f : 7.5f) * dp;
        fillPaint.Color = dragMode == TouchDragMode.HeadAim ? OneUiTheme.SoloGold : Color.Argb(230, hi.R, hi.G, hi.B);
        canvas.DrawCircle(aimOrbScreenX, aimOrbScreenY, orbR, fillPaint);
        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = Color.White;
        canvas.DrawCircle(aimOrbScreenX, aimOrbScreenY, orbR, strokePaint);
        fillPaint.Color = Color.White;
        canvas.DrawCircle(aimOrbScreenX, aimOrbScreenY, 2.3f * dp, fillPaint);
    }

    bool SourcesAnySolo() {
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].IsSolo) return true;
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

                if (camModeBtnRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.ModeToggleClick;
                    return true;
                }

                if (camLabelsBtnRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.LabelsClick;
                    return true;
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

                if (!IsPositionLocked && heightRailRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeightRail;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromRailTouch(ty);
                    return true;
                }

                if (pitchRailRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.PitchRail;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdatePitchFromRailTouch(ty);
                    return true;
                }

                if (roomCanvasRect.Contains(tx, ty)) {
                    // Priority 1: Aim Orb Handle always steers Head Yaw + Pitch directly
                    float orbDist = MathF.Sqrt((tx - aimOrbScreenX) * (tx - aimOrbScreenX) + (ty - aimOrbScreenY) * (ty - aimOrbScreenY));
                    if (orbDist <= 28f * dp) {
                        dragMode = TouchDragMode.HeadAim;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        Invalidate();
                        return true;
                    }

                    // Priority 2: Dragging Head Sphere moves listener position (even if headTouchMode is off)
                    float headDist = MathF.Sqrt((tx - listenerScreenX) * (tx - listenerScreenX) + (ty - listenerScreenY) * (ty - listenerScreenY));
                    if (!IsPositionLocked && headDist <= 30f * dp) {
                        dragMode = TouchDragMode.ListenerMove;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        return true;
                    }

                    // Priority 3: [Cabeça] mode lets you drag anywhere in room to steer Head Yaw & Pitch!
                    if (headTouchMode) {
                        dragMode = TouchDragMode.HeadAim;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        return true;
                    }

                    dragMode = TouchDragMode.OrbitCamera;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    cameraAnimating = false;
                    return true;
                }

                dragMode = TouchDragMode.None;
                return false;

            case MotionEventActions.PointerDown:
                if (e.PointerCount >= 2 && roomCanvasRect.Contains(tx, ty)) {
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
                if (dist > maxMoveDist) maxMoveDist = dist;

                if (dragMode is TouchDragMode.PresetClick or TouchDragMode.ModeToggleClick or TouchDragMode.LabelsClick or TouchDragMode.ZoomOutClick or TouchDragMode.ZoomInClick or TouchDragMode.ResetZoomClick) {
                    return true;
                }

                if (dragMode == TouchDragMode.HeightRail && !IsPositionLocked) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromRailTouch(ty);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.PitchRail) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdatePitchFromRailTouch(ty);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.HeadAim) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    float newYaw = NormalizeDeg(listenerRotation.Y + stepX * 1.10f / dp);
                    float newPitch = Math.Clamp(listenerRotation.X - stepY * 0.90f / dp, -90f, 90f);
                    listenerRotation = new Vector3(newPitch, newYaw, listenerRotation.Z);
                    ListenerRotationChanged?.Invoke(listenerRotation);
                    Invalidate();
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

                if (endedMode == TouchDragMode.ModeToggleClick) {
                    headTouchMode = !headTouchMode;
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.LabelsClick) {
                    labelMode = labelMode switch {
                        LabelVisibilityMode.ActiveOnly => LabelVisibilityMode.All,
                        LabelVisibilityMode.All => LabelVisibilityMode.Minimal,
                        _ => LabelVisibilityMode.ActiveOnly
                    };
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
                        case 0: AnimateToCameraPreset(-24f, 28f); break;
                        case 1: AnimateToCameraPreset(0f, 84f); break;
                        case 2: AnimateToCameraPreset(0f, 10f); break;
                    }
                    return true;
                }

                if (endedMode == TouchDragMode.HeightRail && !IsPositionLocked) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastHeightTapTime).TotalMilliseconds < 280) {
                        lastHeightTapTime = DateTime.MinValue;
                        listenerPosition = new Vector3(listenerPosition.X, 0f, listenerPosition.Z);
                        ListenerPositionChanged?.Invoke(listenerPosition);
                        Invalidate();
                    } else {
                        lastHeightTapTime = now;
                    }
                    return true;
                }

                if (endedMode == TouchDragMode.PitchRail) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastPitchTapTime).TotalMilliseconds < 280) {
                        lastPitchTapTime = DateTime.MinValue;
                        listenerRotation = new Vector3(0f, listenerRotation.Y, 0f);
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                    } else {
                        lastPitchTapTime = now;
                    }
                    return true;
                }

                if (endedMode == TouchDragMode.HeadAim) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastHeadTapTime).TotalMilliseconds < 280) {
                        lastHeadTapTime = DateTime.MinValue;
                        listenerRotation = Vector3.Zero;
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                    } else {
                        lastHeadTapTime = now;
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

    void UpdateHeightFromRailTouch(float ty) {
        float dp = Resources.DisplayMetrics.Density;
        float trackTop = heightRailRect.Top + 16f * dp;
        float trackBot = heightRailRect.Bottom - 16f * dp;
        float trackH = Math.Max(20f * dp, trackBot - trackTop);
        float frac = Math.Clamp((ty - trackTop) / trackH, 0f, 1f);
        float ny = 1f - frac * 2f; // Top = +1 (ceiling), Bottom = -1 (floor)
        listenerPosition = new Vector3(listenerPosition.X, ny * environmentSize.Y, listenerPosition.Z);
        ListenerPositionChanged?.Invoke(listenerPosition);
        Invalidate();
    }

    void UpdatePitchFromRailTouch(float ty) {
        float dp = Resources.DisplayMetrics.Density;
        float trackTop = pitchRailRect.Top + 16f * dp;
        float trackBot = pitchRailRect.Bottom - 16f * dp;
        float trackH = Math.Max(20f * dp, trackBot - trackTop);
        float frac = Math.Clamp((ty - trackTop) / trackH, 0f, 1f);
        float newPitch = Math.Clamp((1f - frac * 2f) * 90f, -90f, 90f);
        listenerRotation = new Vector3(newPitch, listenerRotation.Y, 0f);
        ListenerRotationChanged?.Invoke(listenerRotation);
        Invalidate();
    }

    void UpdateListenerFromRoomTouch(float tx, float ty, float prevX, float prevY, float dp) {
        if (roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) {
            return;
        }
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);

        if (camPitchDeg < 16f) {
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

    static float PointerDist(MotionEvent e) {
        if (e == null || e.PointerCount < 2) return 0f;
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