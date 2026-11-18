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
/// Clean, high-contrast Dolby Atmos Renderer-inspired 3D Spatial Room Visualizer:
/// - Full viewport dedicated to the 3D acoustic wireframe room (floor grid, ear-level horizon ring,
///   ceiling cage, and luminous FRONT/TELA bar) with free 3D orbit, pinch/button zoom, and instant angle presets
/// - Structured Bed speaker monitor nodes with Dolby Atmos dB activity colors (Slate -> Green -> Amber -> Red),
///   Mute 'X' markers, and Solo gold rings
/// - Clean translucent 3D Object spheres with thin floor projection drop-lines &amp; rings (zero trail clutter)
/// - Label verbosity toggle ("Ativos" / "Todos") and sleek non-intrusive listener orientation indicator
/// </summary>
public sealed class RoomSimulatorView : View {
    sealed class SmoothedSourceState {
        public Vector3 SmoothedPos;
        public float SmoothedRms;
        public bool Initialized;
    }

    enum LabelVisibilityMode {
        ActiveOnly,
        All,
        Minimal
    }

    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint roomBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint pillActivePaint = new(PaintFlags.AntiAlias);
    readonly Paint pillInactivePaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint horizonRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint floorQuadPaint = new(PaintFlags.AntiAlias);
    readonly Paint ceilingWirePaint = new(PaintFlags.AntiAlias);
    readonly Paint dropLinePaint = new(PaintFlags.AntiAlias);
    readonly Paint dropRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint screenPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint strokePaint = new(PaintFlags.AntiAlias);
    readonly Paint soloRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerConePaint = new(PaintFlags.AntiAlias);
    readonly Paint textPaint = new(PaintFlags.AntiAlias);
    readonly Paint subTextPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y)> hitTargets = [];
    readonly Dictionary<TrackSourceItem, SmoothedSourceState> smoothedStates = [];
    readonly RectF[] camPresetRects = [new(), new(), new()];
    RectF camLabelsBtnRect = new();
    RectF camZoomOutRect = new();
    RectF camZoomInRect = new();
    RectF camResetLabelRect = new();
    RectF roomCanvasRect = new();

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero;
    bool isAudioPlaying;

    LabelVisibilityMode labelMode = LabelVisibilityMode.ActiveOnly;

    const float DefaultRoomZoom = 0.88f;
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

    enum TouchDragMode {
        None,
        OrbitCamera,
        PinchZoom,
        ListenerMove,
        PresetClick,
        LabelsClick,
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
#pragma warning disable CS0067
    public event Action<Vector3> ListenerRotationChanged;
#pragma warning restore CS0067
    public event Action<TrackSourceItem> SourceSoloToggled;

    public RoomSimulatorView(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        roomBgPaint.Color = Color.Rgb(8, 10, 16);
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = OneUiTheme.Divider;
        roomBorderPaint.StrokeWidth = 1.1f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        pillActivePaint.Color = OneUiTheme.Primary;
        pillActivePaint.SetStyle(Paint.Style.Fill);

        pillInactivePaint.Color = Color.Argb(34, 255, 255, 255);
        pillInactivePaint.SetStyle(Paint.Style.Fill);

        gridPaint.Color = Color.Argb(30, 225, 232, 248);
        gridPaint.StrokeWidth = 0.9f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        horizonRingPaint.Color = Color.Argb(36, 130, 175, 255);
        horizonRingPaint.StrokeWidth = 0.9f * dp;
        horizonRingPaint.SetStyle(Paint.Style.Stroke);

        floorQuadPaint.Color = Color.Argb(16, 255, 255, 255);
        floorQuadPaint.SetStyle(Paint.Style.Fill);

        ceilingWirePaint.Color = Color.Argb(48, 150, 185, 255);
        ceilingWirePaint.StrokeWidth = 0.95f * dp;
        ceilingWirePaint.SetStyle(Paint.Style.Stroke);

        dropLinePaint.StrokeWidth = 1.15f * dp;
        dropLinePaint.SetStyle(Paint.Style.Stroke);

        dropRingPaint.StrokeWidth = 1.1f * dp;
        dropRingPaint.SetStyle(Paint.Style.Stroke);

        screenPaint.Color = OneUiTheme.Primary;
        screenPaint.StrokeWidth = 3.6f * dp;
        screenPaint.StrokeCap = Paint.Cap.Round;
        screenPaint.SetStyle(Paint.Style.Stroke);

        fillPaint.SetStyle(Paint.Style.Fill);
        strokePaint.SetStyle(Paint.Style.Stroke);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.2f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.SetStyle(Paint.Style.Fill);

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
        int desiredH = Math.Max((int)(w * 0.80f), (int)(312f * dp));
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

        // Top toolbar: Angle text | Labels pill | - / + zoom | [3D] [Topo] [Frente]
        float topBarH = 26f * dp;
        float topY = outerPad;
        float presetW = 42f * dp;
        float presetGap = 4f * dp;
        float zoomBtnSz = 24f * dp;
        float presetsTotalW = presetW * 3f + presetGap * 2f;
        float presetsStartX = outerPad + w - presetsTotalW;

        float zInX = presetsStartX - 6f * dp - zoomBtnSz;
        float zOutX = zInX - 4f * dp - zoomBtnSz;
        float zTop = topY + (topBarH - zoomBtnSz) * 0.5f;
        camZoomOutRect = new RectF(zOutX, zTop, zOutX + zoomBtnSz, zTop + zoomBtnSz);
        camZoomInRect = new RectF(zInX, zTop, zInX + zoomBtnSz, zTop + zoomBtnSz);

        float lblBtnW = 52f * dp;
        float lblBtnX = zOutX - 6f * dp - lblBtnW;
        camLabelsBtnRect = new RectF(lblBtnX, topY, lblBtnX + lblBtnW, topY + topBarH);
        camResetLabelRect = new RectF(outerPad, topY, camLabelsBtnRect.Left - 4f * dp, topY + topBarH);

        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 10f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        canvas.DrawText(
            $"{camPitchDeg:0}° • {Math.Round(roomZoom * 100)}%",
            outerPad + 4f * dp,
            topY + topBarH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f,
            subTextPaint);

        // Labels toggle pill
        bool lblHighlight = labelMode != LabelVisibilityMode.Minimal;
        canvas.DrawRoundRect(camLabelsBtnRect, topBarH * 0.5f, topBarH * 0.5f,
            lblHighlight ? pillInactivePaint : pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.5f * dp;
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
            textPaint.TextSize = 10f * dp;
            textPaint.Color = isClose ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, isClose ? TypefaceStyle.Bold : TypefaceStyle.Normal));
            canvas.DrawText(presets[i].label, camPresetRects[i].CenterX(),
                camPresetRects[i].CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
        }
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));

        // 100% of remaining viewport dedicated to the clean 3D Acoustic Room
        float roomTop = topY + topBarH + 8f * dp;
        float roomBottom = outerPad + h;
        roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w, Math.Max(roomTop + 160f * dp, roomBottom));

        hitTargets.Clear();
        bool anySolo = SourcesAnySolo();
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);
        float headYawRad = listenerRotation.Y * (MathF.PI / 180f);

        bool stillAnimating = DrawDolbyStyle3DRoom(
            canvas, roomCanvasRect, anySolo, dt, camYawRad, camPitchRad, headYawRad, dp);

        if ((isAudioPlaying || cameraAnimating || stillAnimating)
            && Visibility == ViewStates.Visible
            && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    (float cx, float cy, float radiusPx) GetCameraFraming(RectF r) {
        return (r.CenterX(), r.CenterY(), Math.Min(r.Width(), r.Height()) * 0.37f * roomZoom);
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
        const float camDist = 3.85f;
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
        const float camDist = 3.85f;
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

    bool DrawDolbyStyle3DRoom(
        Canvas canvas, RectF r, bool anySolo, float dt,
        float camYawRad, float camPitchRad, float headYawRad, float dp) {

        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        canvas.Save();
        Android.Graphics.Path clipRoom = new();
        clipRoom.AddRoundRect(r, corner, corner, Android.Graphics.Path.Direction.Cw);
        canvas.ClipPath(clipRoom);

        // 1. Clean Floor (-1 Y) Quad & Architectural Grid
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

        // 2. Ear-level Reference Plane (Y = 0) + Ceiling (+1 Y) Cage
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

        // 3. Front Cinema Screen Bar (+Z wall)
        var scrL = ProjectOrbit3D(r, -0.56f, 0.14f, 1f, camYawRad, camPitchRad);
        var scrR = ProjectOrbit3D(r, 0.56f, 0.14f, 1f, camYawRad, camPitchRad);
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(scrL.sx, scrL.sy, scrR.sx, scrR.sy, screenPaint);
        var scrMid = ProjectOrbit3D(r, 0f, 0.30f, 1.02f, camYawRad, camPitchRad);
        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 8.5f * dp;
        subTextPaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawText("TELA / FRONT", scrMid.sx, scrMid.sy - 2f * dp, subTextPaint);

        // 4. Smooth Source State Interpolation & Depth Sort (back-to-front)
        bool anyInterpolating = false;
        List<(bool isListener, TrackSourceItem item, int objNumber, Vector3 np, float rms, float depth)> renderQueue = new(sources.Count + 1);

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
                st.SmoothedRms = targetRms;
                st.Initialized = true;
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
            }

            var pt = ProjectOrbit3D(r, st.SmoothedPos.X, st.SmoothedPos.Y, st.SmoothedPos.Z, camYawRad, camPitchRad);
            renderQueue.Add((false, item, item.IsBed ? 0 : objSeq, st.SmoothedPos, st.SmoothedRms, pt.depth));
        }

        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        var lCenter = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);
        renderQueue.Add((true, null, 0, new Vector3(lnx, lny, lnz), 0f, lCenter.depth));

        renderQueue.Sort((a, b) => b.depth.CompareTo(a.depth));

        foreach (var entry in renderQueue) {
            if (entry.isListener) {
                DrawListenerInRoom(canvas, r, lnx, lny, lnz, camYawRad, camPitchRad, headYawRad, dp);
                continue;
            }

            TrackSourceItem item = entry.item;
            Vector3 np = entry.np;
            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            var posPt = ProjectOrbit3D(r, np.X, np.Y, np.Z, camYawRad, camPitchRad);
            hitTargets.Add((item, posPt.sx, posPt.sy));

            Color meterCol = ResolveDolbyMeterColor(entry.rms, effectivelyMuted, item.IsBed, item.IsLFE);
            float scale = Math.Clamp(posPt.persp * 1.04f, 0.72f, 1.24f);

            if (item.IsBed) {
                // Dolby Atmos Renderer Room Monitor / Bed Node
                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = Color.Argb(36, 200, 212, 235);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                float hw = 8.4f * dp * scale;
                float hh = 6.6f * dp * scale;
                RectF box = new(posPt.sx - hw, posPt.sy - hh, posPt.sx + hw, posPt.sy + hh);

                // Dark monitor body + active meter rim (Dolby Green -> Yellow -> Red)
                fillPaint.SetShader(null);
                fillPaint.Color = Color.Rgb(24, 28, 38);
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, fillPaint);

                strokePaint.StrokeWidth = (entry.rms > 0.02f ? 2.1f : 1.3f) * dp;
                strokePaint.Color = meterCol;
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, strokePaint);

                // Inner speaker driver glow dot
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
                    textPaint.TextSize = 8.5f * dp;
                    textPaint.Color = effectivelyMuted ? OneUiTheme.TextMuted : Color.Argb(225, 240, 244, 255);
                    canvas.DrawText(shortLabel, posPt.sx, posPt.sy + hh + 9.5f * dp, textPaint);
                }
            } else {
                // Dolby Atmos Renderer Dynamic 3D Object Sphere + crisp vertical floor drop-line
                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = effectivelyMuted
                    ? Color.Argb(35, 160, 165, 180)
                    : Color.Argb(115, meterCol.R, meterCol.G, meterCol.B);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                dropRingPaint.Color = dropLinePaint.Color;
                canvas.DrawCircle(floorPt.sx, floorPt.sy, 3.8f * dp * floorPt.persp, dropRingPaint);

                float rad = (7.2f + Math.Clamp(entry.rms * 3.8f, 0f, 3.2f)) * dp * scale;
                using (RadialGradient objGrad = new(
                    posPt.sx - rad * 0.25f, posPt.sy - rad * 0.25f, Math.Max(1f, rad * 1.15f),
                    [
                        Color.Argb(effectivelyMuted ? 95 : 235, Math.Min(255, meterCol.R + 40), Math.Min(255, meterCol.G + 40), Math.Min(255, meterCol.B + 40)).ToArgb(),
                        Color.Argb(effectivelyMuted ? 65 : 195, meterCol.R, meterCol.G, meterCol.B).ToArgb()
                    ],
                    [0f, 1f], Shader.TileMode.Clamp)) {
                    fillPaint.SetShader(objGrad);
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad, fillPaint);
                    fillPaint.SetShader(null);
                }

                strokePaint.StrokeWidth = 1.25f * dp;
                strokePaint.Color = effectivelyMuted ? OneUiTheme.TextMuted : Color.Argb(200, 255, 255, 255);
                canvas.DrawCircle(posPt.sx, posPt.sy, rad, strokePaint);

                if (effectivelyMuted) {
                    DrawMuteCross(canvas, posPt.sx, posPt.sy, rad * 0.55f, dp);
                } else {
                    // Object index cleanly inside the sphere (Dolby Atmos Renderer signature!)
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = Math.Clamp(8.2f * scale, 7f, 10f) * dp;
                    textPaint.Color = Color.Rgb(12, 14, 20);
                    textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
                    canvas.DrawText(entry.objNumber.ToString(), posPt.sx,
                        posPt.sy - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
                    textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));
                }

                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 3.2f * dp, soloRingPaint);
                }

                bool showTag = labelMode == LabelVisibilityMode.All ||
                    (labelMode == LabelVisibilityMode.ActiveOnly && (item.IsSolo || entry.rms > 0.08f));
                if (showTag && labelMode == LabelVisibilityMode.All) {
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = 8.2f * dp;
                    textPaint.Color = OneUiTheme.TextSecondary;
                    canvas.DrawText($"O{entry.objNumber}", posPt.sx, posPt.sy - rad - 3.5f * dp, textPaint);
                }
            }
        }

        canvas.Restore();
        return anyInterpolating;
    }

    static Color ResolveDolbyMeterColor(float rms, bool muted, bool isBed, bool isLfe) {
        if (muted) {
            return Color.Rgb(72, 76, 88);
        }
        if (rms >= 0.55f) {
            return Color.Rgb(255, 84, 84); // Peak Red
        }
        if (rms >= 0.24f) {
            return Color.Rgb(248, 196, 62); // Active Amber
        }
        if (rms >= 0.015f) {
            return isLfe ? OneUiTheme.LfeChannel : Color.Rgb(58, 214, 132); // Signal Green
        }
        // Idle color: calm distinct tone for LFE, Beds, and Dynamic Objects
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
        float camYawRad, float camPitchRad, float headYawRad, float dp) {

        var lFloor = ProjectOrbit3D(r, lnx, -1f, lnz, camYawRad, camPitchRad);
        var lPos = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);

        dropLinePaint.Color = Color.Argb(135, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        canvas.DrawLine(lFloor.sx, lFloor.sy, lPos.sx, lPos.sy, dropLinePaint);
        dropRingPaint.Color = dropLinePaint.Color;
        canvas.DrawCircle(lFloor.sx, lFloor.sy, 4.5f * dp * lFloor.persp, dropRingPaint);

        // Clean directional listening FOV wedge
        const float spread = 0.44f;
        const float reach = 0.36f;
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
                [Color.Argb(96, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        // Sleek minimal studio listener badge (with left/right headphone axis + nose direction dot)
        float rad = 9.5f * dp * Math.Clamp(lPos.persp, 0.8f, 1.15f);
        fillPaint.SetShader(null);
        fillPaint.Color = OneUiTheme.Primary;
        canvas.DrawCircle(lPos.sx, lPos.sy, rad, fillPaint);

        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawCircle(lPos.sx, lPos.sy, rad, strokePaint);

        var nosePt = ProjectOrbit3D(r,
            Math.Clamp(lnx + MathF.Sin(headYawRad) * 0.14f, -1f, 1f),
            lny,
            Math.Clamp(lnz + MathF.Cos(headYawRad) * 0.14f, -1f, 1f),
            camYawRad, camPitchRad);
        canvas.DrawLine(lPos.sx, lPos.sy, nosePt.sx, nosePt.sy, strokePaint);
        fillPaint.Color = Color.White;
        canvas.DrawCircle(nosePt.sx, nosePt.sy, 2.4f * dp, fillPaint);
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
                if (dist > maxMoveDist) maxMoveDist = dist;

                if (dragMode is TouchDragMode.PresetClick or TouchDragMode.LabelsClick or TouchDragMode.ZoomOutClick or TouchDragMode.ZoomInClick or TouchDragMode.ResetZoomClick) {
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
