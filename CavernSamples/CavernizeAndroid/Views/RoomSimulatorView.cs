using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;

using Cavern.Channels;
using Cavern.Spatial;

using Cavernize.Logic.Models;

using CavernizeAndroid.Language;
using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive Dolby Atmos 3D Spatial Room Visualizer &amp; Dedicated One UI 9 Control Dock:
/// - Unobstructed volumetric 3D room (shaded acoustic walls, floor sweet-spot rings, ear horizon,
///   ceiling cage, live RMS rays, dB-ring bed monitors, moving object trails &amp; idle fade-out)
/// - Direct touch on the 3D head moves Listener X/Z/Y freely with zero obstructing handles;
///   dragging empty room orbits camera; tapping any speaker/object toggles Solo; pinch zooms
/// - Dedicated frosted Control Dock below the room:
///   Left: Articulated 3D Head Trackball with attitude horizon ring for free Yaw &amp; Pitch steering
///   Right: One UI 9 vertical capsule elevation slider for Listener Height (Y)
/// </summary>
public sealed class RoomSimulatorView : View {
    sealed class SmoothedSourceState {
        public Vector3 SmoothedPos;
        public Vector3 LastRawPos;
        public float SmoothedRms;
        public float VisibilityAlpha = 1f;
        public long MovingUntilMs;
        public long SoundUntilMs;
        public readonly (Vector3 Pos, long TimeMs)[] Trail = new (Vector3, long)[22];
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
        SourceDrag,
        HeadDock,
        HeightDock,
        PresetClick,
        LabelsClick,
        XRayClick,
        XRayBandClick,
        ZoomOutClick,
        ZoomInClick,
        ResetZoomClick
    }

    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint dockBgPaint = new(PaintFlags.AntiAlias);
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
    readonly Paint trailCorePaint = new(PaintFlags.AntiAlias);
    readonly Paint trailGlowPaint = new(PaintFlags.AntiAlias);
    readonly Paint rayPaint = new(PaintFlags.AntiAlias);
    readonly Paint screenPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint strokePaint = new(PaintFlags.AntiAlias);
    readonly Paint soloRingPaint = new(PaintFlags.AntiAlias);
    readonly Paint listenerConePaint = new(PaintFlags.AntiAlias);
    readonly Paint headWireFrontPaint = new(PaintFlags.AntiAlias);
    readonly Paint headWireBackPaint = new(PaintFlags.AntiAlias);
    readonly Paint capsuleTrackPaint = new(PaintFlags.AntiAlias);
    readonly Paint capsuleFillPaint = new(PaintFlags.AntiAlias);
    readonly TextPaint textPaint = new(PaintFlags.AntiAlias);
    readonly TextPaint subTextPaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path clipRoomPath = new();
    readonly Android.Graphics.Path scratchPath = new();
    readonly Android.Graphics.Path ribbonSegPath = new();
    readonly List<(float x, float y, float persp, float fade)> projectedTrailPts = new(24);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y)> hitTargets = [];
    readonly Dictionary<TrackSourceItem, SmoothedSourceState> smoothedStates = [];
    readonly RectF[] camPresetRects = [new(), new(), new()];
    RectF camLabelsBtnRect = new();
    RectF camXRayBtnRect = new();
    RectF camXRayBandBtnRect = new();
    RectF camZoomOutRect = new();
    RectF camZoomInRect = new();
    RectF camResetLabelRect = new();
    RectF roomCanvasRect = new();

    // Dedicated external Control Dock rectangles (safely outside the 3D room)
    RectF dockHeadRect = new();
    RectF dockHeadResetRect = new();
    RectF dockHeightRect = new();
    RectF dockHeightCapsuleRect = new();
    RectF dockHeightResetRect = new();

    float listenerScreenX;
    float listenerScreenY;

    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero; // X=Pitch (-90..90), Y=Yaw (-180..180)
    bool isAudioPlaying;
    bool hasPlayedOnce;

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
    DateTime lastHeightTapTime = DateTime.MinValue;
    DateTime lastHeadTapTime = DateTime.MinValue;
    DateTime lastSourceTapTime = DateTime.MinValue;
    TrackSourceItem lastTappedSource;
    bool lastTappedSourcePrevSolo;

    TouchDragMode dragMode = TouchDragMode.None;
    TrackSourceItem draggedSource;
    int pressedPresetIdx = -1;
    float downX;
    float downY;
    float lastX;
    float lastY;
    float maxMoveDist;

    SpatialXRayPowermap xrayPowermap;
    SoundfieldTelemetry xrayTelemetry = SoundfieldTelemetry.Empty();

    public bool IsPositionLocked { get; set; }
    public bool IsXRayEnabled { get; set; }
    public SpatialXRayBandMode XRayBandMode { get; set; } = SpatialXRayBandMode.AllBands;

    public event Action<Vector3> ListenerPositionChanged;
    public event Action<Vector3> ListenerRotationChanged;
    public event Action<TrackSourceItem> SourceSoloToggled;
    public event Action<TrackSourceItem, Vector3> SourcePositionDragged;
    public event Action<TrackSourceItem> SourcePositionResetRequested;
    public event Action<bool> XRayToggled;
    public event Action<SpatialXRayBandMode> XRayBandModeChanged;

    public RoomSimulatorView(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;
        Typeface sfMed = OneUiTheme.GetAppleFontMedium(context);
        Typeface sfBold = OneUiTheme.GetAppleFontBold(context);

        roomBgPaint.Color = Color.Argb(205, 9, 11, 17);
        roomBgPaint.SetStyle(Paint.Style.Fill);

        dockBgPaint.Color = Color.Argb(212, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
        dockBgPaint.SetStyle(Paint.Style.Fill);

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

        dropLinePaint.StrokeWidth = 0.85f * dp;
        dropLinePaint.SetStyle(Paint.Style.Stroke);
        dropLinePaint.SetPathEffect(new DashPathEffect([3.0f * dp, 4.2f * dp], 0f));

        dropRingPaint.StrokeWidth = 0.85f * dp;
        dropRingPaint.SetStyle(Paint.Style.Stroke);

        trailCorePaint.SetStyle(Paint.Style.Stroke);
        trailCorePaint.StrokeCap = Paint.Cap.Round;
        trailCorePaint.StrokeJoin = Paint.Join.Round;

        trailGlowPaint.SetStyle(Paint.Style.Stroke);
        trailGlowPaint.StrokeCap = Paint.Cap.Round;
        trailGlowPaint.StrokeJoin = Paint.Join.Round;

        rayPaint.StrokeWidth = 1.0f * dp;
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

        headWireFrontPaint.Color = Color.Argb(172, 255, 255, 255);
        headWireFrontPaint.StrokeWidth = 1.05f * dp;
        headWireFrontPaint.SetStyle(Paint.Style.Stroke);

        headWireBackPaint.Color = Color.Argb(48, 255, 255, 255);
        headWireBackPaint.StrokeWidth = 0.85f * dp;
        headWireBackPaint.SetStyle(Paint.Style.Stroke);

        capsuleTrackPaint.Color = Color.Argb(48, 255, 255, 255);
        capsuleTrackPaint.SetStyle(Paint.Style.Fill);

        capsuleFillPaint.SetStyle(Paint.Style.Fill);

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

    /// <summary>
    /// Synchronizes live W13 3D spherical X-Ray powermap and soundfield telemetry for overlay rendering.
    /// </summary>
    public void UpdateXRayState(SpatialXRayPowermap powermap, SoundfieldTelemetry telemetry) {
        xrayPowermap = powermap;
        xrayTelemetry = telemetry;
        if (IsXRayEnabled) {
            PostInvalidateOnAnimation();
        }
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
        int desiredH = Math.Max((int)(w * 1.08f), (int)(414f * dp));
        SetMeasuredDimension(w, ResolveSize(desiredH, heightMeasureSpec));
    }

    static void DrawEllipsizedText(Canvas canvas, string text, float x, float y, float maxWidth, TextPaint paint) {
        if (string.IsNullOrEmpty(text) || maxWidth <= 4f) {
            return;
        }
        string clipped = TextUtils.Ellipsize(text, paint, maxWidth, TextUtils.TruncateAt.End) ?? text;
        canvas.DrawText(clipped, x, y, paint);
    }

    static bool HitTestExpanded44Dp(RectF rect, float tx, float ty, float dp, float maxPadX = 6f, float maxPadY = 9f) {
        if (rect.Width() <= 0f || rect.Height() <= 0f) {
            return false;
        }
        float minPx = 44f * dp;
        float padX = Math.Min(maxPadX * dp, Math.Max(0f, (minPx - rect.Width()) * 0.5f));
        float padY = Math.Min(maxPadY * dp, Math.Max(0f, (minPx - rect.Height()) * 0.5f));
        return tx >= rect.Left - padX && tx <= rect.Right + padX &&
               ty >= rect.Top - padY && ty <= rect.Bottom + padY;
    }

    protected override void OnDraw(Canvas canvas) {
        base.OnDraw(canvas);
        float dp = Resources.DisplayMetrics.Density;
        float outerPad = 4f * dp;
        float w = Width - outerPad * 2f;
        float h = Height - outerPad * 2f;
        if (w <= 0 || h <= 0) return;

        dockBgPaint.Color = Color.Argb(212, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
        roomBorderPaint.Color = OneUiTheme.Divider;
        pillActivePaint.Color = OneUiTheme.Primary;
        screenPaint.Color = OneUiTheme.Primary;

        long nowMs = SystemClock.UptimeMillis();
        if (lastFrameMs == 0) lastFrameMs = nowMs;
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

        AndroidStrings str = AndroidStrings.Active;

        // Zone 1: Two-Row Top Control Bar (prevents 320dp overflow on 360dp phones; all pills fit inside ~292dp card width)
        // Row 1: [Zoom %] [- / +] ... [3D] [Top] [Front]
        float topBarH = 26f * dp;
        float row1Y = outerPad;
        float presetW = 44f * dp;
        float presetGap = 4f * dp;
        float zoomBtnSz = 26f * dp;
        float presetsTotalW = presetW * 3f + presetGap * 2f;
        float presetsStartX = outerPad + w - presetsTotalW;

        float zoomLabelW = 38f * dp;
        camResetLabelRect = new RectF(outerPad, row1Y, outerPad + zoomLabelW, row1Y + topBarH);
        float zOutX = camResetLabelRect.Right + 4f * dp;
        float zInX = zOutX + zoomBtnSz + 4f * dp;
        float zTop = row1Y + (topBarH - zoomBtnSz) * 0.5f;
        camZoomOutRect = new RectF(zOutX, zTop, zOutX + zoomBtnSz, zTop + zoomBtnSz);
        camZoomInRect = new RectF(zInX, zTop, zInX + zoomBtnSz, zTop + zoomBtnSz);

        // Row 2: [X-Ray] [Band (when active)] [Labels] ... [Legend summary]
        float row2Y = row1Y + topBarH + 5f * dp;
        float xrayBtnW = 52f * dp;
        camXRayBtnRect = new RectF(outerPad, row2Y, outerPad + xrayBtnW, row2Y + topBarH);

        float nextChipX = camXRayBtnRect.Right + 5f * dp;
        if (IsXRayEnabled) {
            float bandBtnW = 42f * dp;
            camXRayBandBtnRect = new RectF(nextChipX, row2Y, nextChipX + bandBtnW, row2Y + topBarH);
            nextChipX = camXRayBandBtnRect.Right + 5f * dp;
        } else {
            camXRayBandBtnRect = new RectF(0, 0, 0, 0);
        }

        float lblBtnW = 54f * dp;
        camLabelsBtnRect = new RectF(nextChipX, row2Y, nextChipX + lblBtnW, row2Y + topBarH);

        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 9.8f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        canvas.DrawText(
            $"{Math.Round(roomZoom * 100)}%",
            outerPad + 4f * dp,
            row1Y + topBarH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f,
            subTextPaint);

        canvas.DrawOval(camZoomOutRect, dragMode == TouchDragMode.ZoomOutClick ? pillActivePaint : pillInactivePaint);
        canvas.DrawOval(camZoomInRect, dragMode == TouchDragMode.ZoomInClick ? pillActivePaint : pillInactivePaint);
        float arm = zoomBtnSz * 0.22f;
        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = OneUiTheme.TextPrimary;
        canvas.DrawLine(camZoomOutRect.CenterX() - arm, camZoomOutRect.CenterY(), camZoomOutRect.CenterX() + arm, camZoomOutRect.CenterY(), strokePaint);
        canvas.DrawLine(camZoomInRect.CenterX() - arm, camZoomInRect.CenterY(), camZoomInRect.CenterX() + arm, camZoomInRect.CenterY(), strokePaint);
        canvas.DrawLine(camZoomInRect.CenterX(), camZoomInRect.CenterY() - arm, camZoomInRect.CenterX(), camZoomInRect.CenterY() + arm, strokePaint);

        (string label, float py, float pp)[] presets = [
            (str["RoomCam3D"], -24f, 28f),
            (str["RoomCamTop"], 0f, 84f),
            (str["RoomCamFront"], 0f, 10f)
        ];
        for (int i = 0; i < 3; i++) {
            float px = presetsStartX + i * (presetW + presetGap);
            camPresetRects[i] = new RectF(px, row1Y, px + presetW, row1Y + topBarH);
            bool isClose = MathF.Abs(NormalizeDeg(camYawDeg - presets[i].py)) < 8f
                && MathF.Abs(camPitchDeg - presets[i].pp) < 8f;
            canvas.DrawRoundRect(camPresetRects[i], topBarH * 0.5f, topBarH * 0.5f, isClose ? pillActivePaint : pillInactivePaint);

            textPaint.TextAlign = Paint.Align.Center;
            textPaint.TextSize = 9.4f * dp;
            textPaint.Color = isClose ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
            DrawEllipsizedText(canvas, presets[i].label, camPresetRects[i].CenterX(),
                camPresetRects[i].CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
                presetW - 6f * dp, textPaint);
        }

        // Draw Row 2 chips: X-Ray + Band + Labels + right-aligned legend summary
        canvas.DrawRoundRect(camXRayBtnRect, topBarH * 0.5f, topBarH * 0.5f, IsXRayEnabled ? pillActivePaint : pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 8.9f * dp;
        textPaint.Color = IsXRayEnabled ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
        DrawEllipsizedText(canvas, str["RoomXRayBtn"], camXRayBtnRect.CenterX(),
            camXRayBtnRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
            xrayBtnW - 6f * dp, textPaint);

        if (IsXRayEnabled) {
            canvas.DrawRoundRect(camXRayBandBtnRect, topBarH * 0.5f, topBarH * 0.5f, pillInactivePaint);
            textPaint.TextSize = 8.8f * dp;
            textPaint.Color = XRayBandMode switch {
                SpatialXRayBandMode.LowOnly => Color.Rgb(255, 166, 76),
                SpatialXRayBandMode.MidOnly => Color.Rgb(64, 224, 168),
                SpatialXRayBandMode.HighOnly => Color.Rgb(158, 178, 255),
                _ => OneUiTheme.PrimaryHigh
            };
            string bTag = XRayBandMode switch {
                SpatialXRayBandMode.LowOnly => str["RoomXRayBandLowShort"],
                SpatialXRayBandMode.MidOnly => str["RoomXRayBandMidShort"],
                SpatialXRayBandMode.HighOnly => str["RoomXRayBandHighShort"],
                _ => str["XRayBandAll"]
            };
            DrawEllipsizedText(canvas, bTag, camXRayBandBtnRect.CenterX(),
                camXRayBandBtnRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
                camXRayBandBtnRect.Width() - 6f * dp, textPaint);
        }

        canvas.DrawRoundRect(camLabelsBtnRect, topBarH * 0.5f, topBarH * 0.5f, pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 8.9f * dp;
        textPaint.Color = labelMode == LabelVisibilityMode.All ? OneUiTheme.PrimaryHigh : OneUiTheme.TextSub;
        string lblTag = labelMode switch {
            LabelVisibilityMode.All => str["RoomLabelAll"],
            LabelVisibilityMode.Minimal => str["RoomLabelMin"],
            _ => str["RoomLabelActive"]
        };
        DrawEllipsizedText(canvas, lblTag, camLabelsBtnRect.CenterX(),
            camLabelsBtnRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
            lblBtnW - 6f * dp, textPaint);

        float legendAvailW = outerPad + w - camLabelsBtnRect.Right - 8f * dp;
        if (legendAvailW >= 44f * dp) {
            subTextPaint.TextAlign = Paint.Align.Right;
            subTextPaint.TextSize = 8.2f * dp;
            subTextPaint.Color = OneUiTheme.TextMuted;
            DrawEllipsizedText(canvas, str["RoomLegendSummary"], outerPad + w - 2f * dp,
                row2Y + topBarH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f,
                legendAvailW, subTextPaint);
        }

        // Zone 3: Dedicated Frosted Control Dock OUTSIDE the 3D room (Left: 3D Head Trackball | Right: Vertical Height Y Capsule)
        float dockH = 98f * dp;
        float dockGap = 10f * dp;
        float dockTop = outerPad + h - dockH;
        float headDockW = w * 0.61f;
        dockHeadRect = new RectF(outerPad, dockTop, outerPad + headDockW, dockTop + dockH);
        dockHeightRect = new RectF(outerPad + headDockW + dockGap, dockTop, outerPad + w, dockTop + dockH);

        // Zone 2: Unobstructed 3D Viewport above the dock
        float roomTop = row2Y + topBarH + 7f * dp;
        float roomBottom = dockTop - dockGap;
        roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w, Math.Max(roomTop + 170f * dp, roomBottom));

        hitTargets.Clear();
        bool anySolo = SourcesAnySolo();
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);
        float headYawRad = listenerRotation.Y * (MathF.PI / 180f);
        float headPitchRad = listenerRotation.X * (MathF.PI / 180f);

        bool stillAnimating = DrawDolbyStyle3DRoom(
            canvas, roomCanvasRect, anySolo, dt, nowMs, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);

        DrawExternalHeadTrackballDock(canvas, dockHeadRect, headYawRad, headPitchRad, dp);
        DrawExternalHeightCapsuleDock(canvas, dockHeightRect, dp);

        if ((isAudioPlaying || cameraAnimating || stillAnimating)
            && Visibility == ViewStates.Visible
            && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    (float cx, float cy, float radiusPx) GetCameraFraming(RectF r) {
        return (r.CenterX(), r.CenterY() + r.Height() * 0.03f, Math.Min(r.Width() - 18f * Resources.DisplayMetrics.Density, r.Height()) * 0.40f * roomZoom);
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
        // Matches Cavern.Utilities.VectorExtensions.Rotate((0,0,1), (pitch, yaw, 0)):
        // negative pitch looks UP (+Y), positive pitch looks DOWN (-Y).
        float cosP = MathF.Cos(pitchRad);
        return new Vector3(
            MathF.Sin(yawRad) * cosP,
            -MathF.Sin(pitchRad),
            MathF.Cos(yawRad) * cosP);
    }

    bool DrawDolbyStyle3DRoom(
        Canvas canvas, RectF r, bool anySolo, float dt, long nowMs,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        canvas.Save();
        clipRoomPath.Reset();
        clipRoomPath.AddRoundRect(r, corner, corner, Android.Graphics.Path.Direction.Cw);
        canvas.ClipPath(clipRoomPath);

        var fl_bl = ProjectOrbit3D(r, -1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_br = ProjectOrbit3D(r, 1f, -1f, -1f, camYawRad, camPitchRad);
        var fl_fr = ProjectOrbit3D(r, 1f, -1f, 1f, camYawRad, camPitchRad);
        var fl_fl = ProjectOrbit3D(r, -1f, -1f, 1f, camYawRad, camPitchRad);

        var ce_bl = ProjectOrbit3D(r, -1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_br = ProjectOrbit3D(r, 1f, 1f, -1f, camYawRad, camPitchRad);
        var ce_fr = ProjectOrbit3D(r, 1f, 1f, 1f, camYawRad, camPitchRad);
        var ce_fl = ProjectOrbit3D(r, -1f, 1f, 1f, camYawRad, camPitchRad);

        void DrawWallQuad(
            (float sx, float sy, float persp, float depth) a,
            (float sx, float sy, float persp, float depth) b,
            (float sx, float sy, float persp, float depth) c,
            (float sx, float sy, float persp, float depth) d,
            int alpha) {
            float avgDepth = (a.depth + b.depth + c.depth + d.depth) * 0.25f;
            if (avgDepth < -0.15f) return;
            scratchPath.Reset();
            scratchPath.MoveTo(a.sx, a.sy);
            scratchPath.LineTo(b.sx, b.sy);
            scratchPath.LineTo(c.sx, c.sy);
            scratchPath.LineTo(d.sx, d.sy);
            scratchPath.Close();
            wallShadePaint.Color = Color.Argb(alpha, 150, 178, 235);
            canvas.DrawPath(scratchPath, wallShadePaint);
        }
        DrawWallQuad(fl_bl, fl_br, ce_br, ce_bl, 12);
        DrawWallQuad(fl_fl, fl_bl, ce_bl, ce_fl, 9);
        DrawWallQuad(fl_br, fl_fr, ce_fr, ce_br, 9);
        DrawWallQuad(fl_fl, fl_fr, ce_fr, ce_fl, 14);

        scratchPath.Reset();
        scratchPath.MoveTo(fl_bl.sx, fl_bl.sy);
        scratchPath.LineTo(fl_br.sx, fl_br.sy);
        scratchPath.LineTo(fl_fr.sx, fl_fr.sy);
        scratchPath.LineTo(fl_fl.sx, fl_fl.sy);
        scratchPath.Close();
        canvas.DrawPath(scratchPath, floorQuadPaint);
        canvas.DrawPath(scratchPath, gridPaint);

        for (int g = -1; g <= 1; g++) {
            float t = g * 0.5f;
            var ax = ProjectOrbit3D(r, t, -1f, -1f, camYawRad, camPitchRad);
            var bx = ProjectOrbit3D(r, t, -1f, 1f, camYawRad, camPitchRad);
            canvas.DrawLine(ax.sx, ax.sy, bx.sx, bx.sy, gridPaint);

            var az = ProjectOrbit3D(r, -1f, -1f, t, camYawRad, camPitchRad);
            var bz = ProjectOrbit3D(r, 1f, -1f, t, camYawRad, camPitchRad);
            canvas.DrawLine(az.sx, az.sy, bz.sx, bz.sy, gridPaint);
        }

        DrawProjectedRing(canvas, r, 0f, -1f, 0f, 0.45f, camYawRad, camPitchRad, gridPaint);

        var ear_bl = ProjectOrbit3D(r, -1f, 0f, -1f, camYawRad, camPitchRad);
        var ear_br = ProjectOrbit3D(r, 1f, 0f, -1f, camYawRad, camPitchRad);
        var ear_fr = ProjectOrbit3D(r, 1f, 0f, 1f, camYawRad, camPitchRad);
        var ear_fl = ProjectOrbit3D(r, -1f, 0f, 1f, camYawRad, camPitchRad);
        scratchPath.Reset();
        scratchPath.MoveTo(ear_bl.sx, ear_bl.sy);
        scratchPath.LineTo(ear_br.sx, ear_br.sy);
        scratchPath.LineTo(ear_fr.sx, ear_fr.sy);
        scratchPath.LineTo(ear_fl.sx, ear_fl.sy);
        scratchPath.Close();
        canvas.DrawPath(scratchPath, horizonRingPaint);

        scratchPath.Reset();
        scratchPath.MoveTo(ce_bl.sx, ce_bl.sy);
        scratchPath.LineTo(ce_br.sx, ce_br.sy);
        scratchPath.LineTo(ce_fr.sx, ce_fr.sy);
        scratchPath.LineTo(ce_fl.sx, ce_fl.sy);
        scratchPath.Close();
        canvas.DrawPath(scratchPath, ceilingWirePaint);
        canvas.DrawLine(fl_bl.sx, fl_bl.sy, ce_bl.sx, ce_bl.sy, gridPaint);
        canvas.DrawLine(fl_br.sx, fl_br.sy, ce_br.sx, ce_br.sy, gridPaint);
        canvas.DrawLine(fl_fr.sx, fl_fr.sy, ce_fr.sx, ce_fr.sy, gridPaint);
        canvas.DrawLine(fl_fl.sx, fl_fl.sy, ce_fl.sx, ce_fl.sy, gridPaint);

        var scrL = ProjectOrbit3D(r, -0.58f, 0.16f, 1f, camYawRad, camPitchRad);
        var scrR = ProjectOrbit3D(r, 0.58f, 0.16f, 1f, camYawRad, camPitchRad);
        var scrBotL = ProjectOrbit3D(r, -0.46f, -1f, 0.98f, camYawRad, camPitchRad);
        var scrBotR = ProjectOrbit3D(r, 0.46f, -1f, 0.98f, camYawRad, camPitchRad);
        scratchPath.Reset();
        scratchPath.MoveTo(scrL.sx, scrL.sy);
        scratchPath.LineTo(scrR.sx, scrR.sy);
        scratchPath.LineTo(scrBotR.sx, scrBotR.sy);
        scratchPath.LineTo(scrBotL.sx, scrBotL.sy);
        scratchPath.Close();
        wallShadePaint.Color = Color.Argb(20, OneUiTheme.Primary.R, OneUiTheme.Primary.G, OneUiTheme.Primary.B);
        canvas.DrawPath(scratchPath, wallShadePaint);
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(scrL.sx, scrL.sy, scrR.sx, scrR.sy, screenPaint);
        var scrMid = ProjectOrbit3D(r, 0f, 0.30f, 1.02f, camYawRad, camPitchRad);
        subTextPaint.TextAlign = Paint.Align.Center;
        subTextPaint.TextSize = 8.2f * dp;
        subTextPaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawText(AndroidStrings.Active["RoomScreenFront"], scrMid.sx, scrMid.sy - 2f * dp, subTextPaint);

        bool anyInterpolating = false;
        List<(bool isListener, TrackSourceItem item, int objNumber, Vector3 np, float rms, float visAlpha, SmoothedSourceState state, float depth)> renderQueue = new(sources.Count + 1);

        float posBlend = 1f - MathF.Exp(-16f * dt);
        float rmsRise = 1f - MathF.Exp(-24f * dt);
        float rmsFall = 1f - MathF.Exp(-10f * dt);
        int objSeq = 0;

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            if (!item.IsBed) objSeq++;

            Vector3 targetPos = new(
                Math.Clamp(item.NormalizedPosition.X, -1f, 1f),
                Math.Clamp(item.NormalizedPosition.Y, -1f, 1f),
                Math.Clamp(item.NormalizedPosition.Z, -1f, 1f));
            bool effectivelyMuted = item.IsMuted || item.IsDynamicallyFilteredOut || (anySolo && !item.IsSolo);
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
                    st.MovingUntilMs = nowMs + 520;
                }
                st.LastRawPos = targetPos;

                Vector3 delta = targetPos - st.SmoothedPos;
                if (delta.LengthSquared() > 0.000008f) {
                    st.SmoothedPos = Vector3.Lerp(st.SmoothedPos, targetPos, posBlend);
                    anyInterpolating = true;
                } else {
                    st.SmoothedPos = targetPos;
                }

                float rmsDiff = targetRms - st.SmoothedRms;
                if (MathF.Abs(rmsDiff) > 0.0015f) {
                    st.SmoothedRms += rmsDiff * (rmsDiff > 0 ? rmsRise : rmsFall);
                    anyInterpolating = true;
                } else {
                    st.SmoothedRms = targetRms;
                }
            }

            bool hasVolumeNow = targetRms > 0.0025f || st.SmoothedRms > 0.0035f;
            if (hasVolumeNow) st.SoundUntilMs = nowMs + 380;

            float targetVis;
            bool alwaysVisibleLayer = item.IsBed || !hasPlayedOnce || item.HasCustomPosition ||
                item.Layer == SourceLayer.NeuralStem ||
                item.Layer == SourceLayer.RestoredResidual ||
                item.Layer == SourceLayer.LiveMicrophone;
            if (alwaysVisibleLayer) {
                targetVis = 1f;
            } else if (!isAudioPlaying) {
                targetVis = st.VisibilityAlpha;
            } else {
                bool isActive = item.IsSolo || hasVolumeNow || nowMs < st.SoundUntilMs || nowMs < st.MovingUntilMs;
                targetVis = isActive ? 1f : 0f;
            }

            float visDiff = targetVis - st.VisibilityAlpha;
            if (MathF.Abs(visDiff) > 0.006f) {
                float visK = visDiff > 0 ? 14f : 4.2f;
                st.VisibilityAlpha = Math.Clamp(st.VisibilityAlpha + visDiff * (1f - MathF.Exp(-visK * dt)), 0f, 1f);
                anyInterpolating = true;
            } else {
                st.VisibilityAlpha = targetVis;
            }

            if (isAudioPlaying && !item.IsBed && st.VisibilityAlpha > 0.08f && !effectivelyMuted) {
                int lastIdx = (st.TrailHead + st.Trail.Length - 1) % st.Trail.Length;
                bool emptyTrail = st.TrailCount == 0;
                long elapsedTrail = emptyTrail ? 9999 : nowMs - st.Trail[lastIdx].TimeMs;
                float movedSq = emptyTrail ? 1f : (st.SmoothedPos - st.Trail[lastIdx].Pos).LengthSquared();
                if (elapsedTrail >= 22 && movedSq > 0.00012f) {
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

        // W13 Spatial X-Ray 3D Spherical Energy Heatmap + Dominant DoA Vector (behind sources for crisp legibility)
        if (IsXRayEnabled && xrayPowermap != null) {
            DrawSpatialXRaySphericalOverlay(canvas, r, lnx, lny, lnz, camYawRad, camPitchRad, dp);
        }

        renderQueue.Sort((a, b) => b.depth.CompareTo(a.depth));

        for (int i = 0; i < renderQueue.Count; i++) {
            var e = renderQueue[i];
            if (e.isListener || e.visAlpha <= 0.04f || e.rms < 0.04f) continue;
            bool effMuted = e.item.IsMuted || e.item.IsDynamicallyFilteredOut || (anySolo && !e.item.IsSolo);
            if (effMuted) continue;
            var sPt = ProjectOrbit3D(r, e.np.X, e.np.Y, e.np.Z, camYawRad, camPitchRad);
            Color mc = ResolveDolbyMeterColor(e.rms, false, e.item.IsBed, e.item.IsLFE, e.item.Layer);
            int rayAlpha = Math.Clamp((int)Math.Round(e.rms * 66f * e.visAlpha), 5, 60);
            rayPaint.Color = Color.Argb(rayAlpha, mc.R, mc.G, mc.B);
            canvas.DrawLine(sPt.sx, sPt.sy, lCenter.sx, lCenter.sy, rayPaint);
        }

        foreach (var entry in renderQueue) {
            if (entry.isListener) {
                DrawListenerInRoom(canvas, r, lnx, lny, lnz, camYawRad, camPitchRad, headYawRad, headPitchRad, dp);
                continue;
            }
            if (entry.visAlpha <= 0.02f) continue;

            TrackSourceItem item = entry.item;
            Vector3 np = entry.np;
            float va = entry.visAlpha;
            bool effectivelyMuted = item.IsMuted || item.IsDynamicallyFilteredOut || (anySolo && !item.IsSolo);
            var posPt = ProjectOrbit3D(r, np.X, np.Y, np.Z, camYawRad, camPitchRad);
            hitTargets.Add((item, posPt.sx, posPt.sy));

            Color meterCol = ResolveDolbyMeterColor(entry.rms, effectivelyMuted, item.IsBed, item.IsLFE, item.Layer);
            float scale = Math.Clamp(posPt.persp * 1.04f, 0.72f, 1.25f);

            if (item.IsBed) {
                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = Color.Argb(18, 200, 212, 235);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                float hw = 8.4f * dp * scale;
                float hh = 6.6f * dp * scale;
                RectF box = new(posPt.sx - hw, posPt.sy - hh, posPt.sx + hw, posPt.sy + hh);

                if (!effectivelyMuted && entry.rms > 0.10f) {
                    float pulseR = hw + (3f + Math.Clamp(entry.rms * 7f, 0f, 6f)) * dp;
                    fillPaint.SetShader(null);
                    fillPaint.Color = Color.Argb(Math.Clamp((int)(entry.rms * 60f), 14, 52), meterCol.R, meterCol.G, meterCol.B);
                    canvas.DrawCircle(posPt.sx, posPt.sy, pulseR, fillPaint);
                }

                fillPaint.SetShader(null);
                fillPaint.Color = Color.Argb(228, 22, 26, 36);
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, fillPaint);

                strokePaint.StrokeWidth = (entry.rms > 0.02f ? 2.0f : 1.25f) * dp;
                strokePaint.Color = meterCol;
                canvas.DrawRoundRect(box, 3.2f * dp, 3.2f * dp, strokePaint);

                fillPaint.Color = meterCol;
                canvas.DrawCircle(posPt.sx, posPt.sy, (2.2f + Math.Clamp(entry.rms * 2.4f, 0f, 2.0f)) * dp * scale, fillPaint);

                if (effectivelyMuted) DrawMuteCross(canvas, posPt.sx, posPt.sy, hw * 0.55f, dp);
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
                // Continuous smooth tapered glowing Bézier trajectory ribbon (no dotted balls)
                if (!effectivelyMuted && entry.state != null && entry.state.TrailCount > 0) {
                    SmoothedSourceState tState = entry.state;
                    projectedTrailPts.Clear();
                    int oldestIdx = (tState.TrailHead - tState.TrailCount + tState.Trail.Length) % tState.Trail.Length;
                    for (int k = 0; k < tState.TrailCount; k++) {
                        int idx = (oldestIdx + k) % tState.Trail.Length;
                        var sample = tState.Trail[idx];
                        long ageMs = nowMs - sample.TimeMs;
                        if (ageMs < 0 || ageMs > 680) continue;
                        float ageFade = 1f - ageMs / 680f;
                        var tPt = ProjectOrbit3D(r, sample.Pos.X, sample.Pos.Y, sample.Pos.Z, camYawRad, camPitchRad);
                        projectedTrailPts.Add((tPt.sx, tPt.sy, tPt.persp, ageFade));
                        anyInterpolating = true;
                    }
                    if (projectedTrailPts.Count > 0) {
                        projectedTrailPts.Add((posPt.sx, posPt.sy, posPt.persp, 1f));
                        int nPts = projectedTrailPts.Count;
                        for (int k = 0; k < nPts - 1; k++) {
                            var p0 = projectedTrailPts[k];
                            var p1 = projectedTrailPts[k + 1];
                            float prog = (k + 1f) / (nPts - 1f);
                            float segFade = Math.Clamp(prog * prog * p1.fade * va, 0f, 1f);
                            if (segFade <= 0.015f) continue;

                            float xStart = k == 0 ? p0.x : (projectedTrailPts[k - 1].x + p0.x) * 0.5f;
                            float yStart = k == 0 ? p0.y : (projectedTrailPts[k - 1].y + p0.y) * 0.5f;
                            float xEnd = k == nPts - 2 ? p1.x : (p0.x + p1.x) * 0.5f;
                            float yEnd = k == nPts - 2 ? p1.y : (p0.y + p1.y) * 0.5f;

                            ribbonSegPath.Rewind();
                            ribbonSegPath.MoveTo(xStart, yStart);
                            ribbonSegPath.QuadTo(p0.x, p0.y, xEnd, yEnd);

                            float perspW = Math.Clamp(p1.persp, 0.72f, 1.2f);
                            trailGlowPaint.StrokeWidth = (2.4f + 4.4f * prog) * dp * perspW;
                            trailGlowPaint.Color = Color.Argb(Math.Clamp((int)Math.Round(42f * segFade), 2, 48), meterCol.R, meterCol.G, meterCol.B);
                            canvas.DrawPath(ribbonSegPath, trailGlowPaint);

                            trailCorePaint.StrokeWidth = (0.75f + 2.35f * prog) * dp * perspW;
                            trailCorePaint.Color = Color.Argb(Math.Clamp((int)Math.Round(152f * segFade), 4, 160),
                                Math.Min(255, meterCol.R + 36), Math.Min(255, meterCol.G + 36), Math.Min(255, meterCol.B + 36));
                            canvas.DrawPath(ribbonSegPath, trailCorePaint);
                        }
                    }
                }

                // Discreet dashed projection drop line + subtle floor anchor ring
                var floorPt = ProjectOrbit3D(r, np.X, -1f, np.Z, camYawRad, camPitchRad);
                dropLinePaint.Color = effectivelyMuted
                    ? Color.Argb((int)Math.Round(16 * va), 160, 165, 180)
                    : Color.Argb((int)Math.Round(42 * va), meterCol.R, meterCol.G, meterCol.B);
                canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, dropLinePaint);

                dropRingPaint.Color = effectivelyMuted
                    ? Color.Argb((int)Math.Round(18 * va), 160, 165, 180)
                    : Color.Argb((int)Math.Round(44 * va), meterCol.R, meterCol.G, meterCol.B);
                canvas.DrawCircle(floorPt.sx, floorPt.sy, 2.6f * dp * floorPt.persp, dropRingPaint);

                // Refined layered 3D acoustic object orb
                float appearScale = 0.76f + 0.24f * va;
                float rad = (7.4f + Math.Clamp(entry.rms * 3.6f, 0f, 3.0f)) * dp * scale * appearScale;
                if (!effectivelyMuted && entry.rms > 0.06f) {
                    float wave = (float)(0.5 + 0.5 * Math.Sin(nowMs * 0.007 + entry.objNumber));
                    float haloR = rad + (3.2f + wave * 3.8f * entry.rms) * dp;
                    fillPaint.SetShader(null);
                    fillPaint.Color = Color.Argb(Math.Clamp((int)(entry.rms * 52f * va), 6, 48), meterCol.R, meterCol.G, meterCol.B);
                    canvas.DrawCircle(posPt.sx, posPt.sy, haloR, fillPaint);

                    strokePaint.StrokeWidth = 0.85f * dp;
                    strokePaint.Color = Color.Argb(Math.Clamp((int)(entry.rms * 115f * va), 12, 95), meterCol.R, meterCol.G, meterCol.B);
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 2.1f * dp, strokePaint);
                }

                using (RadialGradient objGrad = new(
                    posPt.sx - rad * 0.28f, posPt.sy - rad * 0.30f, Math.Max(1f, rad * 1.20f),
                    [
                        Color.Argb((int)Math.Round((effectivelyMuted ? 95 : 246) * va), Math.Min(255, meterCol.R + 68), Math.Min(255, meterCol.G + 68), Math.Min(255, meterCol.B + 68)).ToArgb(),
                        Color.Argb((int)Math.Round((effectivelyMuted ? 70 : 215) * va), meterCol.R, meterCol.G, meterCol.B).ToArgb(),
                        Color.Argb((int)Math.Round((effectivelyMuted ? 55 : 198) * va), (int)(meterCol.R * 0.62f), (int)(meterCol.G * 0.62f), (int)(meterCol.B * 0.68f)).ToArgb()
                    ],
                    [0f, 0.58f, 1f], Shader.TileMode.Clamp)) {
                    fillPaint.SetShader(objGrad);
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad, fillPaint);
                    fillPaint.SetShader(null);
                }

                // Crisp top-left glass specular highlight
                if (!effectivelyMuted) {
                    fillPaint.Color = Color.Argb((int)Math.Round(110 * va), 255, 255, 255);
                    canvas.DrawCircle(posPt.sx - rad * 0.30f, posPt.sy - rad * 0.30f, rad * 0.22f, fillPaint);
                }

                strokePaint.StrokeWidth = 1.15f * dp;
                strokePaint.Color = effectivelyMuted
                    ? Color.Argb((int)Math.Round(105 * va), OneUiTheme.TextMuted.R, OneUiTheme.TextMuted.G, OneUiTheme.TextMuted.B)
                    : Color.Argb((int)Math.Round(218 * va), 255, 255, 255);
                canvas.DrawCircle(posPt.sx, posPt.sy, rad, strokePaint);

                // Subtle ceiling elevation chevron badge when object is above ear plane (Y > 0.15)
                if (!effectivelyMuted && np.Y > 0.15f) {
                    float chevY = posPt.sy - rad - 3.2f * dp;
                    float cw = 2.6f * dp * scale;
                    strokePaint.StrokeWidth = 1.15f * dp;
                    strokePaint.Color = Color.Argb((int)Math.Round(185 * va), 255, 255, 255);
                    canvas.DrawLine(posPt.sx - cw, chevY + cw * 0.65f, posPt.sx, chevY, strokePaint);
                    canvas.DrawLine(posPt.sx, chevY, posPt.sx + cw, chevY + cw * 0.65f, strokePaint);
                }

                if (effectivelyMuted) {
                    DrawMuteCross(canvas, posPt.sx, posPt.sy, rad * 0.50f, dp);
                } else {
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = Math.Clamp(8.0f * scale * appearScale, 6f, 9.8f) * dp;
                    textPaint.Color = Color.Argb((int)Math.Round(255 * va), 10, 12, 18);
                    canvas.DrawText(entry.objNumber.ToString(), posPt.sx,
                        posPt.sy - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
                }

                if (item.IsSolo) canvas.DrawCircle(posPt.sx, posPt.sy, rad + 3.4f * dp, soloRingPaint);

                // W13 custom-pinned ring when the user manually dragged this object/stem/mic
                if (item.HasCustomPosition) {
                    strokePaint.StrokeWidth = 1.6f * dp;
                    strokePaint.Color = Color.Rgb(64, 224, 255);
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + (item.IsSolo ? 6.0f : 3.2f) * dp, strokePaint);
                }

                bool showTag = labelMode == LabelVisibilityMode.All || item.HasCustomPosition || (IsXRayEnabled && (entry.rms > 0.05f || item.IsSolo));
                if (showTag) {
                    AndroidStrings str = AndroidStrings.Active;
                    string baseTag = item.Layer switch {
                        SourceLayer.LiveMicrophone => str["RoomTagMic"],
                        SourceLayer.NeuralStem => string.Format(str["RoomTagStemFmt"], entry.objNumber),
                        SourceLayer.RestoredResidual => string.Format(str["RoomTagResidualFmt"], entry.objNumber),
                        _ => string.Format(str["RoomTagObjectFmt"], entry.objNumber)
                    };
                    string delaySuffix = (IsXRayEnabled || item.HasCustomPosition || ReferenceEquals(draggedSource, item)) && item.PropagationDelayMs > 0.05f
                        ? " " + string.Format(str["RoomDelayMsFmt"], item.PropagationDelayMs)
                        : string.Empty;
                    textPaint.TextAlign = Paint.Align.Center;
                    textPaint.TextSize = 8.0f * dp;
                    textPaint.Color = item.HasCustomPosition
                        ? Color.Rgb(118, 232, 255)
                        : Color.Argb((int)Math.Round(215 * va), OneUiTheme.TextSecondary.R, OneUiTheme.TextSecondary.G, OneUiTheme.TextSecondary.B);
                    canvas.DrawText($"{baseTag}{delaySuffix}", posPt.sx, posPt.sy - rad - (np.Y > 0.15f ? 7.5f : 3.5f) * dp, textPaint);
                }
            }
        }

        canvas.Restore();
        return anyInterpolating;
    }

    void DrawListenerInRoom(
        Canvas canvas, RectF r, float lnx, float lny, float lnz,
        float camYawRad, float camPitchRad, float headYawRad, float headPitchRad, float dp) {

        var lFloor = ProjectOrbit3D(r, lnx, -1f, lnz, camYawRad, camPitchRad);
        var lPos = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);

        dropLinePaint.Color = Color.Argb(78, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        canvas.DrawLine(lFloor.sx, lFloor.sy, lPos.sx, lPos.sy, dropLinePaint);
        dropRingPaint.Color = dropLinePaint.Color;
        canvas.DrawCircle(lFloor.sx, lFloor.sy, 3.6f * dp * lFloor.persp, dropRingPaint);

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

        scratchPath.Reset();
        scratchPath.MoveTo(lPos.sx, lPos.sy);
        scratchPath.LineTo(fovL.sx, fovL.sy);
        scratchPath.LineTo(fovU.sx, fovU.sy);
        scratchPath.LineTo(fovR.sx, fovR.sy);
        scratchPath.LineTo(fovD.sx, fovD.sy);
        scratchPath.Close();
        using (LinearGradient coneGrad = new(
            lPos.sx, lPos.sy, fovC.sx, fovC.sy,
            [Color.Argb(105, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
            [0f, 1f], Shader.TileMode.Clamp)) {
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(scratchPath, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        float rad = 14.5f * dp * Math.Clamp(lPos.persp, 0.78f, 1.18f);
        Draw3DHeadSphere(canvas, lPos.sx, lPos.sy, rad, headYawRad, headPitchRad, camYawRad, camPitchRad, dp, showNoseStick: true);
    }

    void Draw3DHeadSphere(
        Canvas canvas, float cx, float cy, float rad,
        float headYawRad, float headPitchRad, float camYawRad, float camPitchRad, float dp, bool showNoseStick = false) {
        Color p = OneUiTheme.Primary;
        Color hi = OneUiTheme.PrimaryHigh;
        Color dk = OneUiTheme.PrimaryPressed;

        using (RadialGradient sphereGrad = new(
            cx - rad * 0.28f, cy - rad * 0.28f, Math.Max(1f, rad * 1.15f),
            [Color.Argb(245, hi.R, hi.G, hi.B).ToArgb(), Color.Argb(232, p.R, p.G, p.B).ToArgb(), Color.Argb(240, dk.R, dk.G, dk.B).ToArgb()],
            [0f, 0.55f, 1f], Shader.TileMode.Clamp)) {
            fillPaint.SetShader(sphereGrad);
            canvas.DrawCircle(cx, cy, rad, fillPaint);
            fillPaint.SetShader(null);
        }
        strokePaint.StrokeWidth = 1.4f * dp;
        strokePaint.Color = hi;
        canvas.DrawCircle(cx, cy, rad, strokePaint);

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
            return new Vector3(cx + cam.X * rad, cy - cam.Y * rad, -cam.Z);
        }

        void Draw3DRing(Func<float, Vector3> ringFn) {
            const int segs = 20;
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
        fillPaint.Color = Color.Argb(242, 252, 252, 255);
        canvas.DrawCircle(leftEar.X, leftEar.Y, rad * 0.21f, fillPaint);
        canvas.DrawCircle(rightEar.X, rightEar.Y, rad * 0.21f, fillPaint);

        Vector3 noseBase = ProjectHeadPoint(0f, 0f, 0.72f);
        Vector3 noseTip = ProjectHeadPoint(0f, 0f, showNoseStick ? 1.32f : 1.44f);
        strokePaint.StrokeWidth = 1.8f * dp;
        strokePaint.Color = Color.White;
        canvas.DrawLine(noseBase.X, noseBase.Y, noseTip.X, noseTip.Y, strokePaint);
        fillPaint.Color = Color.White;
        canvas.DrawCircle(noseTip.X, noseTip.Y, rad * 0.18f, fillPaint);
    }

    /// <summary>
    /// External 3D Head Trackball Dock (outside the 3D chart) with orbital compass &amp; horizon attitude ring
    /// so Yaw and Pitch feel completely natural without cluttering or blocking the 3D room.
    /// </summary>
    void DrawExternalHeadTrackballDock(Canvas canvas, RectF r, float headYawRad, float headPitchRad, float dp) {
        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, dockBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        float sphereR = Math.Min(26f * dp, (r.Height() - 22f * dp) * 0.5f);
        float sphereCx = r.Left + 16f * dp + sphereR;
        float sphereCy = r.CenterY();

        // Outer compass / attitude gimbal ring around the trackball
        float ringR = sphereR + 5.5f * dp;
        strokePaint.StrokeWidth = 1.3f * dp;
        strokePaint.Color = dragMode == TouchDragMode.HeadDock
            ? OneUiTheme.PrimaryHigh
            : Color.Argb(75, 255, 255, 255);
        canvas.DrawCircle(sphereCx, sphereCy, ringR, strokePaint);

        // Azimuth compass dot riding the outer ring
        float azX = sphereCx + MathF.Sin(headYawRad) * ringR;
        float azY = sphereCy - MathF.Cos(headYawRad) * ringR;
        fillPaint.SetShader(null);
        fillPaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawCircle(azX, azY, 3.0f * dp, fillPaint);

        Draw3DHeadSphere(canvas, sphereCx, sphereCy, sphereR, headYawRad, headPitchRad, camYawRad: 0f, camPitchRad: 0.18f, dp);

        AndroidStrings str = AndroidStrings.Active;
        float textLeft = sphereCx + sphereR + 13f * dp;
        float maxTextW = Math.Max(24f * dp, r.Right - textLeft - 8f * dp);

        canvas.Save();
        canvas.ClipRect(r.Left + 4f * dp, r.Top + 4f * dp, r.Right - 4f * dp, r.Bottom - 4f * dp);

        textPaint.TextAlign = Paint.Align.Left;
        textPaint.TextSize = 11.5f * dp;
        textPaint.Color = OneUiTheme.TextPrimary;
        DrawEllipsizedText(canvas, str["RoomHead3DTitle"], textLeft, r.Top + 25f * dp, maxTextW, textPaint);

        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 9.6f * dp;
        subTextPaint.Color = OneUiTheme.PrimaryHigh;
        DrawEllipsizedText(canvas, string.Format(str["RoomHeadAnglesFmt"], listenerRotation.Y, listenerRotation.X), textLeft, r.Top + 44f * dp, maxTextW, subTextPaint);

        subTextPaint.TextSize = 8.4f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        DrawEllipsizedText(canvas, str["RoomHeadDragHint"], textLeft, r.Top + 59f * dp, maxTextW, subTextPaint);

        float rstW = Math.Max(46f * dp, r.Right - textLeft - 12f * dp);
        float rstH = 22f * dp;
        dockHeadResetRect = new RectF(textLeft, r.Bottom - rstH - 10f * dp, textLeft + Math.Min(68f * dp, rstW), r.Bottom - 10f * dp);
        canvas.DrawRoundRect(dockHeadResetRect, rstH * 0.5f, rstH * 0.5f, pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.2f * dp;
        textPaint.Color = OneUiTheme.TextSecondary;
        DrawEllipsizedText(canvas, str["RoomResetBtn"], dockHeadResetRect.CenterX(),
            dockHeadResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
            dockHeadResetRect.Width() - 8f * dp, textPaint);

        canvas.Restore();
    }

    /// <summary>
    /// Dedicated One UI 9 Vertical Elevation Capsule Dock (outside the 3D chart):
    /// true vertical orientation matching physical height (Y) from Floor to Ceiling.
    /// </summary>
    void DrawExternalHeightCapsuleDock(Canvas canvas, RectF r, float dp) {
        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, dockBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float alphaScale = IsPositionLocked ? 0.45f : 1f;

        // Vertical One UI 9 capsule pill on the right edge of this card
        float capW = 20f * dp;
        float capRight = r.Right - 14f * dp;
        float capLeft = capRight - capW;
        float capTop = r.Top + 12f * dp;
        float capBot = r.Bottom - 12f * dp;
        dockHeightCapsuleRect = new RectF(capLeft, capTop, capRight, capBot);

        float capRad = capW * 0.5f;
        canvas.DrawRoundRect(dockHeightCapsuleRect, capRad, capRad, capsuleTrackPaint);

        float midY = dockHeightCapsuleRect.CenterY();
        float halfH = dockHeightCapsuleRect.Height() * 0.5f;
        float fillEdgeY = midY - lny * (halfH - capRad * 0.35f);

        Color accent = OneUiTheme.SliderAccent;
        capsuleFillPaint.Color = Color.Argb((int)(235 * alphaScale), accent.R, accent.G, accent.B);
        float fillTop = Math.Min(midY, fillEdgeY);
        float fillBot = Math.Max(midY, fillEdgeY);
        if (fillBot - fillTop >= 2f * dp) {
            canvas.Save();
            scratchPath.Reset();
            scratchPath.AddRoundRect(dockHeightCapsuleRect, capRad, capRad, Android.Graphics.Path.Direction.Cw);
            canvas.ClipPath(scratchPath);
            canvas.DrawRoundRect(new RectF(capLeft, fillTop, capRight, fillBot), capRad * 0.6f, capRad * 0.6f, capsuleFillPaint);
            canvas.Restore();
        }

        // Center ear-horizon 0m mark
        strokePaint.StrokeWidth = 1.2f * dp;
        strokePaint.Color = Color.Argb(135, 255, 255, 255);
        canvas.DrawLine(capLeft + 4f * dp, midY, capRight - 4f * dp, midY, strokePaint);

        // Subtle glowing thumb dot only while dragging (matches One UI 9 slider physics)
        if (dragMode == TouchDragMode.HeightDock && !IsPositionLocked) {
            fillPaint.SetShader(null);
            fillPaint.Color = Color.White;
            canvas.DrawCircle(dockHeightCapsuleRect.CenterX(), fillEdgeY, capW * 0.44f, fillPaint);
        }

        // Left info column with bounds clipping & ellipsize
        AndroidStrings str = AndroidStrings.Active;
        float infoLeft = r.Left + 12f * dp;
        float maxInfoW = Math.Max(20f * dp, capLeft - infoLeft - 6f * dp);

        canvas.Save();
        canvas.ClipRect(r.Left + 4f * dp, r.Top + 4f * dp, capLeft - 2f * dp, r.Bottom - 4f * dp);

        textPaint.TextAlign = Paint.Align.Left;
        textPaint.TextSize = 11f * dp;
        textPaint.Color = OneUiTheme.TextPrimary;
        DrawEllipsizedText(canvas, str["RoomHeightTitle"], infoLeft, r.Top + 25f * dp, maxInfoW, textPaint);

        float meters = lny * environmentSize.Y;
        subTextPaint.TextAlign = Paint.Align.Left;
        subTextPaint.TextSize = 10.5f * dp;
        subTextPaint.Color = IsPositionLocked ? OneUiTheme.TextMuted : accent;
        string metersStr = MathF.Abs(meters) < 0.05f
            ? str["RoomHeightZeroMeters"]
            : string.Format(str["RoomHeightMetersFmt"], meters);
        DrawEllipsizedText(canvas, metersStr, infoLeft, r.Top + 44f * dp, maxInfoW, subTextPaint);

        subTextPaint.TextSize = 8.2f * dp;
        subTextPaint.Color = OneUiTheme.TextMuted;
        string heightStateStr = IsPositionLocked
            ? str["RoomHeightLocked"]
            : (meters > 0.1f ? str["RoomHeightCeiling"] : (meters < -0.1f ? str["RoomHeightFloor"] : str["RoomHeightEar"]));
        DrawEllipsizedText(canvas, heightStateStr, infoLeft, r.Top + 59f * dp, maxInfoW, subTextPaint);

        float rstW = Math.Max(38f * dp, capLeft - infoLeft - 8f * dp);
        float rstH = 22f * dp;
        dockHeightResetRect = new RectF(infoLeft, r.Bottom - rstH - 10f * dp, infoLeft + Math.Min(50f * dp, rstW), r.Bottom - 10f * dp);
        canvas.DrawRoundRect(dockHeightResetRect, rstH * 0.5f, rstH * 0.5f, pillInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.0f * dp;
        textPaint.Color = OneUiTheme.TextSecondary;
        DrawEllipsizedText(canvas, str["RoomHeightZeroBtn"], dockHeightResetRect.CenterX(),
            dockHeightResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f,
            dockHeightResetRect.Width() - 6f * dp, textPaint);

        canvas.Restore();
    }

    void DrawProjectedRing(
        Canvas canvas, RectF r, float cx, float cy, float cz, float radNorm,
        float camYawRad, float camPitchRad, Paint paint) {
        const int segs = 28;
        var p0 = ProjectOrbit3D(r, cx + radNorm, cy, cz, camYawRad, camPitchRad);
        scratchPath.Reset();
        scratchPath.MoveTo(p0.sx, p0.sy);
        for (int i = 1; i <= segs; i++) {
            float a = i / (float)segs * MathF.PI * 2f;
            var pi = ProjectOrbit3D(r, cx + MathF.Cos(a) * radNorm, cy, cz + MathF.Sin(a) * radNorm, camYawRad, camPitchRad);
            scratchPath.LineTo(pi.sx, pi.sy);
        }
        scratchPath.Close();
        canvas.DrawPath(scratchPath, paint);
    }

    void DrawSpatialXRaySphericalOverlay(
        Canvas canvas, RectF r, float lnx, float lny, float lnz,
        float camYawRad, float camPitchRad, float dp) {
        if (xrayPowermap == null) return;
        float peak = MathF.Max(0.02f, xrayPowermap.PeakCellEnergy);
        const float sphereRadius = 0.72f;

        for (int i = 0; i < SpatialXRayPowermap.TotalCells; i++) {
            float e = xrayPowermap.GetCellEnergy(i, XRayBandMode);
            float norm = Math.Clamp(e / peak, 0f, 1f);
            if (norm < 0.08f) continue;

            Vector3 dir = xrayPowermap.CellDirections[i];
            float sx = Math.Clamp(lnx + dir.X * sphereRadius, -1f, 1f);
            float sy = Math.Clamp(lny + dir.Y * sphereRadius, -1f, 1f);
            float sz = Math.Clamp(lnz + dir.Z * sphereRadius, -1f, 1f);
            var proj = ProjectOrbit3D(r, sx, sy, sz, camYawRad, camPitchRad);

            Color cellCol;
            if (XRayBandMode == SpatialXRayBandMode.LowOnly) {
                cellCol = Color.Rgb(255, 138, 64);
            } else if (XRayBandMode == SpatialXRayBandMode.MidOnly) {
                cellCol = Color.Rgb(48, 224, 158);
            } else if (XRayBandMode == SpatialXRayBandMode.HighOnly) {
                cellCol = Color.Rgb(128, 168, 255);
            } else {
                float lo = xrayPowermap.LowEnergyGrid[i];
                float mi = xrayPowermap.MidEnergyGrid[i];
                float hi = xrayPowermap.HighEnergyGrid[i];
                float sum = lo + mi + hi + 1e-6f;
                int cr = Math.Clamp((int)Math.Round((lo * 255f + mi * 56f + hi * 136f) / sum), 40, 255);
                int cg = Math.Clamp((int)Math.Round((lo * 136f + mi * 228f + hi * 170f) / sum), 40, 255);
                int cb = Math.Clamp((int)Math.Round((lo * 64f + mi * 165f + hi * 255f) / sum), 50, 255);
                cellCol = Color.Rgb(cr, cg, cb);
            }

            float rad = (7.5f + norm * 16f) * dp * Math.Clamp(proj.persp, 0.72f, 1.2f);
            int alpha = Math.Clamp((int)Math.Round(norm * 102f), 14, 108);
            using RadialGradient heatGrad = new(
                proj.sx, proj.sy, Math.Max(1f, rad),
                [Color.Argb(alpha, cellCol.R, cellCol.G, cellCol.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            fillPaint.SetShader(heatGrad);
            canvas.DrawCircle(proj.sx, proj.sy, rad, fillPaint);
            fillPaint.SetShader(null);
        }

        // Dominant Direction-of-Arrival (DoA) acoustic vector from listener
        Vector3 doa = xrayPowermap.DominantDirection;
        if (doa.LengthSquared() > 0.04f) {
            var lPt = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);
            var tip = ProjectOrbit3D(r,
                Math.Clamp(lnx + doa.X * 0.62f, -1f, 1f),
                Math.Clamp(lny + doa.Y * 0.62f, -1f, 1f),
                Math.Clamp(lnz + doa.Z * 0.62f, -1f, 1f),
                camYawRad, camPitchRad);
            strokePaint.StrokeWidth = 2.0f * dp;
            strokePaint.Color = Color.Argb(205, 86, 232, 255);
            canvas.DrawLine(lPt.sx, lPt.sy, tip.sx, tip.sy, strokePaint);
            fillPaint.SetShader(null);
            fillPaint.Color = Color.Rgb(86, 232, 255);
            canvas.DrawCircle(tip.sx, tip.sy, 3.2f * dp, fillPaint);
        }
    }

    static Color ResolveDolbyMeterColor(float rms, bool muted, bool isBed, bool isLfe, SourceLayer layer = SourceLayer.Bed) {
        if (muted) return Color.Rgb(72, 76, 88);
        if (layer == SourceLayer.LiveMicrophone) return rms >= 0.55f ? Color.Rgb(255, 84, 84) : Color.Rgb(52, 224, 162);
        if (layer == SourceLayer.NeuralStem) return rms >= 0.55f ? Color.Rgb(255, 112, 92) : Color.Rgb(64, 212, 245);
        if (layer == SourceLayer.RestoredResidual) return rms >= 0.55f ? Color.Rgb(255, 128, 176) : Color.Rgb(176, 132, 255);
        if (rms >= 0.55f) return Color.Rgb(255, 84, 84);
        if (rms >= 0.24f) return Color.Rgb(248, 196, 62);
        if (rms >= 0.015f) return isLfe ? OneUiTheme.LfeChannel : Color.Rgb(58, 214, 132);
        if (isLfe) return Color.Argb(180, OneUiTheme.LfeChannel.R, OneUiTheme.LfeChannel.G, OneUiTheme.LfeChannel.B);
        return isBed ? Color.Rgb(98, 118, 148) : Color.Rgb(126, 178, 255);
    }

    void DrawMuteCross(Canvas canvas, float cx, float cy, float halfArm, float dp) {
        strokePaint.StrokeWidth = 1.6f * dp;
        strokePaint.Color = OneUiTheme.FunctionalRedFill;
        canvas.DrawLine(cx - halfArm, cy - halfArm, cx + halfArm, cy + halfArm, strokePaint);
        canvas.DrawLine(cx + halfArm, cy - halfArm, cx - halfArm, cy + halfArm, strokePaint);
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

                // Exact hit test first, then 44dp hit-slop expansion for compact top-bar pills
                for (int i = 0; i < 3; i++) {
                    if (camPresetRects[i].Contains(tx, ty)) {
                        pressedPresetIdx = i;
                        dragMode = TouchDragMode.PresetClick;
                        return true;
                    }
                }
                if (camXRayBtnRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.XRayClick;
                    return true;
                }
                if (IsXRayEnabled && camXRayBandBtnRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.XRayBandClick;
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

                // Expanded >=44dp touch hit-slop fallback for top-bar pills when outside roomCanvasRect
                if (!roomCanvasRect.Contains(tx, ty) && !dockHeadRect.Contains(tx, ty) && !dockHeightRect.Contains(tx, ty)) {
                    for (int i = 0; i < 3; i++) {
                        if (HitTestExpanded44Dp(camPresetRects[i], tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                            pressedPresetIdx = i;
                            dragMode = TouchDragMode.PresetClick;
                            return true;
                        }
                    }
                    if (HitTestExpanded44Dp(camZoomOutRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.ZoomOutClick;
                        Invalidate();
                        return true;
                    }
                    if (HitTestExpanded44Dp(camZoomInRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.ZoomInClick;
                        Invalidate();
                        return true;
                    }
                    if (HitTestExpanded44Dp(camResetLabelRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.ResetZoomClick;
                        return true;
                    }
                    if (HitTestExpanded44Dp(camXRayBtnRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.XRayClick;
                        return true;
                    }
                    if (IsXRayEnabled && HitTestExpanded44Dp(camXRayBandBtnRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.XRayBandClick;
                        return true;
                    }
                    if (HitTestExpanded44Dp(camLabelsBtnRect, tx, ty, dp, maxPadX: 2f, maxPadY: 8f)) {
                        dragMode = TouchDragMode.LabelsClick;
                        return true;
                    }
                }

                if (dockHeadRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeadDock;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    Invalidate();
                    return true;
                }

                if (dockHeightRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeightDock;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    if (!IsPositionLocked && !HitTestExpanded44Dp(dockHeightResetRect, tx, ty, dp)) {
                        UpdateHeightFromCapsuleTouch(ty);
                    }
                    Invalidate();
                    return true;
                }

                if (roomCanvasRect.Contains(tx, ty)) {
                    // Direct listener position drag with ZERO obstructing Aim Orb
                    float headDist = MathF.Sqrt((tx - listenerScreenX) * (tx - listenerScreenX) + (ty - listenerScreenY) * (ty - listenerScreenY));
                    if (!IsPositionLocked && headDist <= 34f * dp) {
                        dragMode = TouchDragMode.ListenerMove;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        return true;
                    }

                    // W13 Draggable 3D Objects / Neural Stems / Live Microphone (tap still toggles Solo)
                    TrackSourceItem hitDraggable = FindNearestSource(tx, ty, 24f * dp);
                    if (!IsPositionLocked && hitDraggable != null && hitDraggable.IsCustomDraggable) {
                        draggedSource = hitDraggable;
                        dragMode = TouchDragMode.SourceDrag;
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

                if (dragMode is TouchDragMode.PresetClick or TouchDragMode.LabelsClick or TouchDragMode.XRayClick or TouchDragMode.XRayBandClick or TouchDragMode.ZoomOutClick or TouchDragMode.ZoomInClick or TouchDragMode.ResetZoomClick) {
                    return true;
                }

                if (dragMode == TouchDragMode.HeadDock) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    if (!HitTestExpanded44Dp(dockHeadResetRect, downX, downY, dp)) {
                        float stepX = tx - lastX;
                        float stepY = ty - lastY;
                        float newYaw = NormalizeDeg(listenerRotation.Y + stepX * 1.30f / dp);
                        float newPitch = Math.Clamp(listenerRotation.X + stepY * 1.05f / dp, -90f, 90f);
                        listenerRotation = new Vector3(newPitch, newYaw, listenerRotation.Z);
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                    }
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.HeightDock && !IsPositionLocked) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    if (!HitTestExpanded44Dp(dockHeightResetRect, downX, downY, dp)) {
                        UpdateHeightFromCapsuleTouch(ty);
                    }
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

                if (dragMode == TouchDragMode.SourceDrag && !IsPositionLocked && draggedSource != null) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    if (maxMoveDist >= touchSlop * 0.55f) {
                        UpdateDraggedSourceFromRoomTouch(draggedSource, tx, ty, lastX, lastY, dp);
                    }
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.OrbitCamera) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    camYawDeg = NormalizeDeg(camYawDeg - stepX * 0.55f / dp);
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
                TrackSourceItem endedSource = draggedSource;
                int preset = pressedPresetIdx;
                dragMode = TouchDragMode.None;
                draggedSource = null;
                pressedPresetIdx = -1;

                if (!wasTap) {
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.XRayClick) {
                    IsXRayEnabled = !IsXRayEnabled;
                    XRayToggled?.Invoke(IsXRayEnabled);
                    Invalidate();
                    return true;
                }
                if (endedMode == TouchDragMode.XRayBandClick) {
                    XRayBandMode = (SpatialXRayBandMode)(((int)XRayBandMode + 1) % 4);
                    XRayBandModeChanged?.Invoke(XRayBandMode);
                    Invalidate();
                    return true;
                }

                if (endedMode == TouchDragMode.SourceDrag && endedSource != null) {
                    DateTime now = DateTime.UtcNow;
                    if (endedSource.HasCustomPosition && ReferenceEquals(lastTappedSource, endedSource) &&
                        (now - lastSourceTapTime).TotalMilliseconds < 320) {
                        lastTappedSource = null;
                        lastSourceTapTime = DateTime.MinValue;
                        if (endedSource.IsSolo != lastTappedSourcePrevSolo) {
                            endedSource.IsSolo = lastTappedSourcePrevSolo;
                            SourceSoloToggled?.Invoke(endedSource);
                        }
                        SourcePositionResetRequested?.Invoke(endedSource);
                    } else {
                        lastTappedSource = endedSource;
                        lastTappedSourcePrevSolo = endedSource.IsSolo;
                        lastSourceTapTime = now;
                        endedSource.IsSolo = !endedSource.IsSolo;
                        SourceSoloToggled?.Invoke(endedSource);
                    }
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

                if (endedMode == TouchDragMode.HeadDock) {
                    if (HitTestExpanded44Dp(dockHeadResetRect, tx, ty, dp)) {
                        listenerRotation = Vector3.Zero;
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                        return true;
                    }
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastHeadTapTime).TotalMilliseconds < 320) {
                        lastHeadTapTime = DateTime.MinValue;
                        listenerRotation = Vector3.Zero;
                        ListenerRotationChanged?.Invoke(listenerRotation);
                        Invalidate();
                    } else {
                        lastHeadTapTime = now;
                    }
                    return true;
                }

                if (endedMode == TouchDragMode.HeightDock) {
                    if (IsPositionLocked) return true;
                    if (HitTestExpanded44Dp(dockHeightResetRect, tx, ty, dp)) {
                        listenerPosition = new Vector3(listenerPosition.X, 0f, listenerPosition.Z);
                        ListenerPositionChanged?.Invoke(listenerPosition);
                        Invalidate();
                        return true;
                    }
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastHeightTapTime).TotalMilliseconds < 320) {
                        lastHeightTapTime = DateTime.MinValue;
                        listenerPosition = new Vector3(listenerPosition.X, 0f, listenerPosition.Z);
                        ListenerPositionChanged?.Invoke(listenerPosition);
                        Invalidate();
                    } else {
                        lastHeightTapTime = now;
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
                draggedSource = null;
                pressedPresetIdx = -1;
                Invalidate();
                return false;
        }

        return base.OnTouchEvent(e);
    }

    void UpdateDraggedSourceFromRoomTouch(TrackSourceItem item, float tx, float ty, float prevX, float prevY, float dp) {
        if (item == null || roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) return;
        float camYawRad = camYawDeg * (MathF.PI / 180f);
        float camPitchRad = camPitchDeg * (MathF.PI / 180f);
        Vector3 cur = item.HasCustomPosition ? item.CustomNormalizedPosition : item.NormalizedPosition;
        Vector3 next;
        if (camPitchDeg < 16f) {
            float dyNorm = -(ty - prevY) / Math.Max(40f * dp, roomCanvasRect.Height() * 0.35f);
            float dxNorm = (tx - prevX) / Math.Max(40f * dp, roomCanvasRect.Width() * 0.35f);
            float nx = Math.Clamp(cur.X + dxNorm * MathF.Cos(camYawRad), -1f, 1f);
            float nz = Math.Clamp(cur.Z - dxNorm * MathF.Sin(camYawRad), -1f, 1f);
            float ny = Math.Clamp(cur.Y + dyNorm, -1f, 1f);
            next = new Vector3(nx, ny, nz);
        } else {
            var (nx, nz) = UnprojectHorizontalPlane(roomCanvasRect, tx, ty, cur.Y, camYawRad, camPitchRad);
            next = new Vector3(nx, cur.Y, nz);
        }
        item.HasCustomPosition = true;
        item.CustomNormalizedPosition = next;
        item.NormalizedPosition = next;
        SourcePositionDragged?.Invoke(item, next);
        Invalidate();
    }

    void UpdateHeightFromCapsuleTouch(float ty) {
        float pad = dockHeightCapsuleRect.Width() * 0.35f;
        float top = dockHeightCapsuleRect.Top + pad;
        float bot = dockHeightCapsuleRect.Bottom - pad;
        float h = Math.Max(12f, bot - top);
        float frac = Math.Clamp((ty - top) / h, 0f, 1f);
        float ny = 1f - frac * 2f;
        listenerPosition = new Vector3(listenerPosition.X, ny * environmentSize.Y, listenerPosition.Z);
        ListenerPositionChanged?.Invoke(listenerPosition);
        Invalidate();
    }

    void UpdateListenerFromRoomTouch(float tx, float ty, float prevX, float prevY, float dp) {
        if (roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) return;
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
