using System.Numerics;

using Android.Content;
using Android.Graphics;
using Android.Views;

using Cavern.Channels;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

/// <summary>
/// Interactive One UI 9 Spatial Room Visualizer featuring:
/// - Three selectable room perspectives: 3D Perspective Box (with vertical floor-to-ceiling height stalks),
///   Top-Down + Dedicated Vertical Height (Y) Elevation Tower, and Front (X/Y) Elevation Cross-Section
/// - Dedicated Bottom Control Dock housing the 3D Head Gimbal (Yaw/Pitch) + Listener Height (Y) control,
///   completely separated from the speaker area so speakers are never obscured
/// - Direct tap-to-Solo on any speaker or 3D object across all three views
/// </summary>
public sealed class RoomSimulatorView : View {
    public enum RoomViewMode {
        Perspective3D,
        TopWithHeight,
        FrontElevation
    }

    readonly Paint roomBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint roomBorderPaint = new(PaintFlags.AntiAlias);
    readonly Paint dockBgPaint = new(PaintFlags.AntiAlias);
    readonly Paint tabActivePaint = new(PaintFlags.AntiAlias);
    readonly Paint tabInactivePaint = new(PaintFlags.AntiAlias);
    readonly Paint gridPaint = new(PaintFlags.AntiAlias);
    readonly Paint floorQuadPaint = new(PaintFlags.AntiAlias);
    readonly Paint ceilingWirePaint = new(PaintFlags.AntiAlias);
    readonly Paint stalkPaint = new(PaintFlags.AntiAlias);
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
    readonly Paint hudChipPaint = new(PaintFlags.AntiAlias);

    readonly int touchSlop;
    readonly List<(TrackSourceItem item, float x, float y, float nx, float ny, float nz)> hitTargets = [];
    readonly RectF[] modeTabRects = [new(), new(), new()];
    RectF roomCanvasRect = new();
    RectF sideHeightRect = new();
    RectF dockGimbalRect = new();
    RectF dockGimbalResetRect = new();
    RectF dockHeightRect = new();
    RectF dockHeightResetRect = new();

    RoomViewMode viewMode = RoomViewMode.Perspective3D;
    IReadOnlyList<TrackSourceItem> sources = [];
    Vector3 environmentSize = new(10, 7, 10);
    Vector3 listenerPosition = Vector3.Zero;
    Vector3 listenerRotation = Vector3.Zero; // X=Pitch (-90..90), Y=Yaw (-180..180)
    DateTime lastRoomTapTime = DateTime.MinValue;
    DateTime lastGimbalTapTime = DateTime.MinValue;

    enum TouchDragMode {
        None,
        ListenerMap,
        HeightBar,
        HeadGimbal,
        DockHeight,
        TabClick
    }

    TouchDragMode dragMode = TouchDragMode.None;
    int pressedTabIdx = -1;
    float downX;
    float downY;
    float lastX;
    float lastY;
    float maxMoveDist;

    public bool IsPositionLocked { get; set; }

    public RoomViewMode ActiveViewMode {
        get => viewMode;
        set {
            if (viewMode != value) {
                viewMode = value;
                Invalidate();
            }
        }
    }

    public event Action<Vector3> ListenerPositionChanged;
    public event Action<Vector3> ListenerRotationChanged;
    public event Action<TrackSourceItem> SourceSoloToggled;

    public RoomSimulatorView(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        roomBgPaint.Color = OneUiTheme.RoomBg;
        roomBgPaint.SetStyle(Paint.Style.Fill);

        roomBorderPaint.Color = OneUiTheme.Divider;
        roomBorderPaint.StrokeWidth = 1.2f * dp;
        roomBorderPaint.SetStyle(Paint.Style.Stroke);

        dockBgPaint.Color = Color.Argb(205, OneUiTheme.SurfaceElevated.R, OneUiTheme.SurfaceElevated.G, OneUiTheme.SurfaceElevated.B);
        dockBgPaint.SetStyle(Paint.Style.Fill);

        tabActivePaint.Color = OneUiTheme.Primary;
        tabActivePaint.SetStyle(Paint.Style.Fill);

        tabInactivePaint.Color = Color.Argb(34, 255, 255, 255);
        tabInactivePaint.SetStyle(Paint.Style.Fill);

        gridPaint.Color = Color.Argb(36, 255, 255, 255);
        gridPaint.StrokeWidth = 1f * dp;
        gridPaint.SetStyle(Paint.Style.Stroke);

        floorQuadPaint.Color = Color.Argb(22, 255, 255, 255);
        floorQuadPaint.SetStyle(Paint.Style.Fill);

        ceilingWirePaint.Color = Color.Argb(62, OneUiTheme.FunctionalCyan.R, OneUiTheme.FunctionalCyan.G, OneUiTheme.FunctionalCyan.B);
        ceilingWirePaint.StrokeWidth = 1.1f * dp;
        ceilingWirePaint.SetStyle(Paint.Style.Stroke);

        stalkPaint.StrokeWidth = 1.5f * dp;
        stalkPaint.SetStyle(Paint.Style.Stroke);

        shadowDotPaint.Color = Color.Argb(95, 0, 0, 0);
        shadowDotPaint.SetStyle(Paint.Style.Fill);

        screenPaint.Color = OneUiTheme.Primary;
        screenPaint.StrokeWidth = 3.6f * dp;
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
        heightRingPaint.StrokeWidth = 1.9f * dp;
        heightRingPaint.SetStyle(Paint.Style.Stroke);

        mutedPaint.Color = OneUiTheme.SurfaceBlurTint;
        mutedPaint.SetStyle(Paint.Style.Fill);

        soloRingPaint.Color = OneUiTheme.SoloGold;
        soloRingPaint.StrokeWidth = 2.5f * dp;
        soloRingPaint.SetStyle(Paint.Style.Stroke);

        listenerPaint.SetStyle(Paint.Style.Fill);

        listenerRingPaint.Color = OneUiTheme.PrimaryHigh;
        listenerRingPaint.StrokeWidth = 2f * dp;
        listenerRingPaint.SetStyle(Paint.Style.Stroke);

        listenerConePaint.SetStyle(Paint.Style.Fill);

        sphereWirePaint.Color = Color.Argb(210, 245, 248, 255);
        sphereWirePaint.StrokeWidth = 1.25f * dp;
        sphereWirePaint.SetStyle(Paint.Style.Stroke);

        sphereWireBackPaint.Color = Color.Argb(68, 220, 230, 255);
        sphereWireBackPaint.StrokeWidth = 1f * dp;
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
        subTextPaint.TextAlign = Paint.Align.Left;

        hudChipPaint.Color = Color.Argb(215, 18, 19, 24);
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
        float dp = Resources.DisplayMetrics.Density;
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int desiredH = Math.Max((int)(w * 0.94f), (int)(382f * dp));
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

        // Zone 1: Top View Mode Switcher Bar (32dp)
        float tabH = 30f * dp;
        float tabGap = 6f * dp;
        float tabY = outerPad;
        float singleTabW = (w - tabGap * 2f) / 3f;
        string[] tabNames = ["3D + Altura", "Topo + Altura", "Frente (X/Y)"];
        for (int i = 0; i < 3; i++) {
            float tx = outerPad + i * (singleTabW + tabGap);
            modeTabRects[i] = new RectF(tx, tabY, tx + singleTabW, tabY + tabH);
            bool active = (int)viewMode == i;
            canvas.DrawRoundRect(modeTabRects[i], tabH * 0.5f, tabH * 0.5f, active ? tabActivePaint : tabInactivePaint);

            textPaint.TextSize = 11f * dp;
            textPaint.Color = active ? OneUiTheme.TextPrimary : OneUiTheme.TextSub;
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, active ? TypefaceStyle.Bold : TypefaceStyle.Normal));
            float textCy = modeTabRects[i].CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f;
            canvas.DrawText(tabNames[i], modeTabRects[i].CenterX(), textCy, textPaint);
        }
        textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));

        // Zone 3: Dedicated Bottom Control Dock (76dp, completely clear of all speakers!)
        float dockH = 74f * dp;
        float dockGap = 8f * dp;
        float dockY = outerPad + h - dockH;
        float gimbalW = w * 0.59f;
        dockGimbalRect = new RectF(outerPad, dockY, outerPad + gimbalW, dockY + dockH);
        dockHeightRect = new RectF(outerPad + gimbalW + dockGap, dockY, outerPad + w, dockY + dockH);

        // Zone 2: Main Acoustic Room Canvas (between top tabs and bottom dock)
        float roomTop = tabY + tabH + 8f * dp;
        float roomBottom = dockY - dockGap;
        float roomH = Math.Max(120f * dp, roomBottom - roomTop);

        if (viewMode == RoomViewMode.TopWithHeight) {
            float heightColW = Math.Clamp(w * 0.22f, 54f * dp, 74f * dp);
            float colGap = 6f * dp;
            roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w - heightColW - colGap, roomBottom);
            sideHeightRect = new RectF(roomCanvasRect.Right + colGap, roomTop, outerPad + w, roomBottom);
        } else {
            roomCanvasRect = new RectF(outerPad, roomTop, outerPad + w, roomBottom);
            sideHeightRect = new RectF();
        }

        // Draw Room Map according to active perspective
        hitTargets.Clear();
        bool anySolo = SourcesAnySolo();
        float yawRad = listenerRotation.Y * (MathF.PI / 180f);
        float pitchRad = listenerRotation.X * (MathF.PI / 180f);

        switch (viewMode) {
            case RoomViewMode.Perspective3D:
                DrawPerspective3DRoom(canvas, roomCanvasRect, anySolo, yawRad, pitchRad, dp);
                break;
            case RoomViewMode.TopWithHeight:
                DrawTopDownRoom(canvas, roomCanvasRect, anySolo, yawRad, pitchRad, dp);
                DrawVerticalHeightTower(canvas, sideHeightRect, anySolo, dp);
                break;
            case RoomViewMode.FrontElevation:
                DrawFrontElevationRoom(canvas, roomCanvasRect, anySolo, dp);
                break;
        }

        // Bottom HUD telemetry chip inside bottom-left of main room canvas
        DrawRoomHudTelemetry(canvas, roomCanvasRect, dp);

        // Draw Bottom Dock Controls (Zero overlap with room speakers)
        DrawDockHeadGimbal(canvas, dockGimbalRect, yawRad, pitchRad, dp);
        DrawDockHeightControl(canvas, dockHeightRect, dp);
    }

    bool SourcesAnySolo() {
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].IsSolo) {
                return true;
            }
        }
        return false;
    }

    (float sx, float sy, float persp) Project3D(RectF r, float nx, float ny, float nz) {
        float cx = r.CenterX();
        float cy = r.Top + r.Height() * 0.52f;
        float persp = 2.6f / (3.1f + nz * 0.65f);
        float sx = cx + nx * (r.Width() * 0.39f) * persp;
        float sy = cy + (-nz * 0.27f - ny * 0.16f) * r.Height() * persp;
        return (sx, sy, persp);
    }

    (float nx, float nz) Unproject3DHorizontal(RectF r, float sx, float sy, float fixedNy) {
        float cy = r.Top + r.Height() * 0.52f;
        float k = (sy - cy) / (Math.Max(1f, r.Height()) * 2.6f);
        float nz = Math.Clamp(-(3.1f * k + 0.16f * fixedNy) / Math.Max(0.05f, 0.65f * k + 0.27f), -1f, 1f);
        float persp = 2.6f / (3.1f + nz * 0.65f);
        float nx = Math.Clamp((sx - r.CenterX()) / Math.Max(1f, r.Width() * 0.39f * persp), -1f, 1f);
        return (nx, nz);
    }

    void DrawPerspective3DRoom(Canvas canvas, RectF r, bool anySolo, float yawRad, float pitchRad, float dp) {
        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        // Floor quad (-1 floor, +1/-1 X/Z)
        var fl_bl = Project3D(r, -1f, -1f, -1f);
        var fl_br = Project3D(r, 1f, -1f, -1f);
        var fl_fr = Project3D(r, 1f, -1f, 1f);
        var fl_fl = Project3D(r, -1f, -1f, 1f);
        using (Android.Graphics.Path floorPath = new()) {
            floorPath.MoveTo(fl_bl.sx, fl_bl.sy);
            floorPath.LineTo(fl_br.sx, fl_br.sy);
            floorPath.LineTo(fl_fr.sx, fl_fr.sy);
            floorPath.LineTo(fl_fl.sx, fl_fl.sy);
            floorPath.Close();
            canvas.DrawPath(floorPath, floorQuadPaint);
            canvas.DrawPath(floorPath, gridPaint);
        }

        // Floor Perspective Grid Lines
        for (int g = -1; g <= 1; g += 2) {
            float t = g * 0.5f;
            var a = Project3D(r, t, -1f, -1f);
            var b = Project3D(r, t, -1f, 1f);
            canvas.DrawLine(a.sx, a.sy, b.sx, b.sy, gridPaint);

            var c = Project3D(r, -1f, -1f, t);
            var d = Project3D(r, 1f, -1f, t);
            canvas.DrawLine(c.sx, c.sy, d.sx, d.sy, gridPaint);
        }
        var midX1 = Project3D(r, 0f, -1f, -1f);
        var midX2 = Project3D(r, 0f, -1f, 1f);
        canvas.DrawLine(midX1.sx, midX1.sy, midX2.sx, midX2.sy, gridPaint);
        var midZ1 = Project3D(r, -1f, -1f, 0f);
        var midZ2 = Project3D(r, 1f, -1f, 0f);
        canvas.DrawLine(midZ1.sx, midZ1.sy, midZ2.sx, midZ2.sy, gridPaint);

        // Ceiling Frame (+1 Y) + Corner Pillars
        var ce_bl = Project3D(r, -1f, 1f, -1f);
        var ce_br = Project3D(r, 1f, 1f, -1f);
        var ce_fr = Project3D(r, 1f, 1f, 1f);
        var ce_fl = Project3D(r, -1f, 1f, 1f);
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

        // Front Screen Banner on Front Wall (+Z)
        var scrL = Project3D(r, -0.55f, 0.35f, 1f);
        var scrR = Project3D(r, 0.55f, 0.35f, 1f);
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(scrL.sx, scrL.sy, scrR.sx, scrR.sy, screenPaint);
        subTextPaint.TextAlign = Paint.Align.Center;
        canvas.DrawText("FRENTE / TELA (+Z) • TETO (+Y)", r.CenterX(), r.Top + 17f * dp, subTextPaint);

        // Draw sources back-to-front (largest +Z first) with floor shadow + vertical Y height stalk!
        List<(TrackSourceItem item, int idx, Vector3 np)> ordered = new(sources.Count);
        for (int i = 0; i < sources.Count; i++) {
            Vector3 np = sources[i].NormalizedPosition;
            ordered.Add((sources[i], i, np));
        }
        ordered.Sort((a, b) => b.np.Z.CompareTo(a.np.Z));

        foreach (var (item, idx, np) in ordered) {
            float nx = Math.Clamp(np.X, -1f, 1f);
            float ny = Math.Clamp(np.Y, -1f, 1f);
            float nz = Math.Clamp(np.Z, -1f, 1f);

            var floorPt = Project3D(r, nx, -1f, nz);
            var posPt = Project3D(r, nx, ny, nz);
            hitTargets.Add((item, posPt.sx, posPt.sy, nx, ny, nz));

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(item.RmsLevel * 16f, 0f, 10f) * dp;

            // Floor shadow & vertical height stalk showing exact Floor -> Ceiling Y position!
            canvas.DrawCircle(floorPt.sx, floorPt.sy, 3.2f * dp * floorPt.persp, shadowDotPaint);
            Color baseCol = effectivelyMuted
                ? OneUiTheme.TextMuted
                : (item.IsBed ? (item.IsLFE ? OneUiTheme.LfeChannel : OneUiTheme.BedChannel) : OneUiTheme.DynamicObject);
            stalkPaint.Color = ny > 0.15f
                ? Color.Argb(145, OneUiTheme.FunctionalGreen.R, OneUiTheme.FunctionalGreen.G, OneUiTheme.FunctionalGreen.B)
                : Color.Argb(95, baseCol.R, baseCol.G, baseCol.B);
            canvas.DrawLine(floorPt.sx, floorPt.sy, posPt.sx, posPt.sy, stalkPaint);

            float scale = Math.Clamp(posPt.persp * 1.05f, 0.72f, 1.25f);
            if (item.IsBed) {
                float sz = 7.2f * dp * scale;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 1f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + rmsBoost, objGlowPaint.WithColor(Color.Argb(65, p.Color.R, p.Color.G, p.Color.B)));
                }
                canvas.DrawRoundRect(posPt.sx - sz, posPt.sy - sz, posPt.sx + sz, posPt.sy + sz, 4f * dp, 4f * dp, p);
                if (ny > 0.20f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + 3f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, sz + 5f * dp, soloRingPaint);
                }
                string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                textPaint.TextSize = 9.5f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(shortLabel, posPt.sx, posPt.sy + sz + 10.5f * dp, textPaint);
            } else {
                float rad = 8f * dp * scale;
                if (!effectivelyMuted && rmsBoost > 0.5f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + rmsBoost, objGlowPaint.WithColor(Color.Argb(75, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B)));
                }
                canvas.DrawCircle(posPt.sx, posPt.sy, rad, effectivelyMuted ? mutedPaint : objPaint);
                if (ny > 0.15f) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 3f * dp, heightRingPaint);
                }
                if (item.IsSolo) {
                    canvas.DrawCircle(posPt.sx, posPt.sy, rad + 5f * dp, soloRingPaint);
                }
                string label = $"O{idx + 1}";
                if (ny > 0.25f) label += " ↑";
                else if (ny < -0.25f) label += " ↓";
                textPaint.TextSize = 9.5f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(label, posPt.sx, posPt.sy - rad - 4f * dp, textPaint);
            }
        }

        // Listener Shadow + Height Stalk + 3D Head Sphere at real (X, Y, Z)!
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);

        var lFloor = Project3D(r, lnx, -1f, lnz);
        var lPos = Project3D(r, lnx, lny, lnz);

        canvas.DrawCircle(lFloor.sx, lFloor.sy, 5f * dp, shadowDotPaint);
        stalkPaint.Color = Color.Argb(175, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B);
        canvas.DrawLine(lFloor.sx, lFloor.sy, lPos.sx, lPos.sy, stalkPaint);

        // Projected 3D FOV Cone on horizontal plane at listener height
        float spread = 0.44f;
        var fovL = Project3D(r, Math.Clamp(lnx + MathF.Sin(yawRad - spread) * 0.42f, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(yawRad - spread) * 0.42f, -1f, 1f));
        var fovR = Project3D(r, Math.Clamp(lnx + MathF.Sin(yawRad + spread) * 0.42f, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(yawRad + spread) * 0.42f, -1f, 1f));
        var fovC = Project3D(r, Math.Clamp(lnx + MathF.Sin(yawRad) * 0.42f, -1f, 1f), lny, Math.Clamp(lnz + MathF.Cos(yawRad) * 0.42f, -1f, 1f));
        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lPos.sx, lPos.sy);
            cone.LineTo(fovL.sx, fovL.sy);
            cone.LineTo(fovR.sx, fovR.sy);
            cone.Close();
            using LinearGradient coneGrad = new(
                lPos.sx, lPos.sy, fovC.sx, fovC.sy,
                [Color.Argb(115, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        Draw3DHeadSphere(canvas, lPos.sx, lPos.sy, 15.5f * dp * Math.Clamp(lPos.persp, 0.8f, 1.18f), yawRad, pitchRad, viewTilt: 0.48f, dp);
    }

    void DrawTopDownRoom(Canvas canvas, RectF r, bool anySolo, float yawRad, float pitchRad, float dp) {
        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        for (int i = 1; i <= 3; i++) {
            float gx = r.Left + r.Width() * (i / 4f);
            float gy = r.Top + r.Height() * (i / 4f);
            canvas.DrawLine(gx, r.Top, gx, r.Bottom, gridPaint);
            canvas.DrawLine(r.Left, gy, r.Right, gy, gridPaint);
        }

        float screenHalf = r.Width() * 0.28f;
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(r.CenterX() - screenHalf, r.Top + 5f * dp, r.CenterX() + screenHalf, r.Top + 5f * dp, screenPaint);
        subTextPaint.TextAlign = Paint.Align.Center;
        canvas.DrawText("FRENTE / TELA (+Z)", r.CenterX(), r.Top + 18f * dp, subTextPaint);

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            Vector3 np = item.NormalizedPosition;
            float px = r.Left + Math.Clamp((np.X + 1f) * 0.5f, 0.06f, 0.94f) * r.Width();
            float py = r.Top + Math.Clamp(1f - (np.Z + 1f) * 0.5f, 0.10f, 0.90f) * r.Height();
            hitTargets.Add((item, px, py, np.X, np.Y, np.Z));

            bool effectivelyMuted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = effectivelyMuted ? 0f : Math.Clamp(item.RmsLevel * 18f, 0f, 12f) * dp;

            if (item.IsBed) {
                float sz = 7.5f * dp;
                Paint p = effectivelyMuted ? mutedPaint : (item.IsLFE ? lfePaint : bedPaint);
                if (rmsBoost > 1f) {
                    canvas.DrawCircle(px, py, sz + rmsBoost, objGlowPaint.WithColor(Color.Argb(65, p.Color.R, p.Color.G, p.Color.B)));
                }
                canvas.DrawRoundRect(px - sz, py - sz, px + sz, py + sz, 4.5f * dp, 4.5f * dp, p);
                if (np.Y > 0.25f) canvas.DrawCircle(px, py, sz + 3.5f * dp, heightRingPaint);
                if (item.IsSolo) canvas.DrawCircle(px, py, sz + 5.5f * dp, soloRingPaint);
                string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                textPaint.TextSize = 10f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(shortLabel, px, py + sz + 11f * dp, textPaint);
            } else {
                float rad = 8.5f * dp;
                if (!effectivelyMuted && rmsBoost > 0.5f) {
                    canvas.DrawCircle(px, py, rad + rmsBoost, objGlowPaint.WithColor(Color.Argb(75, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B)));
                }
                canvas.DrawCircle(px, py, rad, effectivelyMuted ? mutedPaint : objPaint);
                if (np.Y > 0.15f) canvas.DrawCircle(px, py, rad + 3.5f * dp, heightRingPaint);
                if (item.IsSolo) canvas.DrawCircle(px, py, rad + 5.5f * dp, soloRingPaint);
                string label = $"O{i + 1}";
                if (np.Y > 0.25f) label += " ↑";
                else if (np.Y < -0.25f) label += " ↓";
                textPaint.TextSize = 10f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(label, px, py - rad - 4f * dp, textPaint);
            }
        }

        (float lx, float ly) = GetListenerTopDownCoords(r);
        float pitchForeshorten = Math.Clamp(MathF.Cos(pitchRad), 0.35f, 1f);
        float coneLen = 44f * dp * (0.55f + 0.45f * pitchForeshorten);
        float spread = 0.44f;
        using (Android.Graphics.Path cone = new()) {
            cone.MoveTo(lx, ly);
            cone.LineTo(lx + MathF.Sin(yawRad - spread) * coneLen, ly - MathF.Cos(yawRad - spread) * coneLen);
            cone.LineTo(lx + MathF.Sin(yawRad + spread) * coneLen, ly - MathF.Cos(yawRad + spread) * coneLen);
            cone.Close();
            using LinearGradient coneGrad = new(
                lx, ly, lx + MathF.Sin(yawRad) * coneLen, ly - MathF.Cos(yawRad) * coneLen,
                [Color.Argb(110, OneUiTheme.PrimaryHigh.R, OneUiTheme.PrimaryHigh.G, OneUiTheme.PrimaryHigh.B).ToArgb(), Color.Transparent.ToArgb()],
                [0f, 1f], Shader.TileMode.Clamp);
            listenerConePaint.SetShader(coneGrad);
            canvas.DrawPath(cone, listenerConePaint);
            listenerConePaint.SetShader(null);
        }

        Draw3DHeadSphere(canvas, lx, ly, 16f * dp, yawRad, pitchRad, viewTilt: 0.62f, dp);
    }

    void DrawVerticalHeightTower(Canvas canvas, RectF r, bool anySolo, float dp) {
        float corner = 18f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        textPaint.TextSize = 8.5f * dp;
        textPaint.Color = OneUiTheme.FunctionalGreen;
        canvas.DrawText("ALTURA", r.CenterX(), r.Top + 13f * dp, textPaint);
        subTextPaint.TextAlign = Paint.Align.Center;
        canvas.DrawText("Teto +Y", r.CenterX(), r.Top + 24f * dp, subTextPaint);
        canvas.DrawText("Chão -Y", r.CenterX(), r.Bottom - 6f * dp, subTextPaint);

        float plotTop = r.Top + 30f * dp;
        float plotBot = r.Bottom - 16f * dp;
        float plotH = Math.Max(40f * dp, plotBot - plotTop);
        float earY = plotTop + plotH * 0.5f;

        // Vertical axis spine & ear midline
        canvas.DrawLine(r.CenterX(), plotTop, r.CenterX(), plotBot, gridPaint);
        canvas.DrawLine(r.Left + 8f * dp, earY, r.Right - 8f * dp, earY, ceilingWirePaint);

        // Plot each source at its exact vertical height Y
        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            Vector3 np = item.NormalizedPosition;
            float ny = Math.Clamp(np.Y, -1f, 1f);
            float sy = plotTop + (1f - (ny + 1f) * 0.5f) * plotH;
            float sx = r.CenterX() + Math.Clamp(np.X, -1f, 1f) * (r.Width() * 0.28f);

            bool muted = item.IsMuted || (anySolo && !item.IsSolo);
            Paint p = muted ? mutedPaint : (item.IsBed ? (item.IsLFE ? lfePaint : bedPaint) : objPaint);
            canvas.DrawCircle(sx, sy, item.IsDynamicObject ? 4.5f * dp : 3.8f * dp, p);
        }

        // Plot Listener vertical height bar
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float ly = plotTop + (1f - (lny + 1f) * 0.5f) * plotH;
        listenerPaint.Color = OneUiTheme.PrimaryHigh;
        canvas.DrawRoundRect(r.Left + 10f * dp, ly - 4f * dp, r.Right - 10f * dp, ly + 4f * dp, 4f * dp, 4f * dp, listenerPaint);
        canvas.DrawRoundRect(r.Left + 10f * dp, ly - 4f * dp, r.Right - 10f * dp, ly + 4f * dp, 4f * dp, 4f * dp, listenerRingPaint);
    }

    void DrawFrontElevationRoom(Canvas canvas, RectF r, bool anySolo, float dp) {
        float corner = 22f * dp;
        canvas.DrawRoundRect(r, corner, corner, roomBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        float plotLeft = r.Left + 18f * dp;
        float plotRight = r.Right - 18f * dp;
        float plotW = plotRight - plotLeft;
        float plotTop = r.Top + 26f * dp;
        float plotBot = r.Bottom - 36f * dp;
        float plotH = Math.Max(60f * dp, plotBot - plotTop);

        float earY = plotTop + plotH * 0.5f;
        screenPaint.Color = OneUiTheme.Primary;
        canvas.DrawLine(r.Left + r.Width() * 0.24f, plotTop + 2f * dp, r.Right - r.Width() * 0.24f, plotTop + 2f * dp, ceilingWirePaint);
        canvas.DrawLine(plotLeft, earY, plotRight, earY, gridPaint);
        canvas.DrawLine(plotLeft, plotBot, plotRight, plotBot, screenPaint);
        canvas.DrawLine(r.CenterX(), plotTop, r.CenterX(), plotBot, gridPaint);

        subTextPaint.TextAlign = Paint.Align.Center;
        canvas.DrawText("VISÃO FRONTAL • ESQ/DIR (X) × CHÃO/TETO (Y)", r.CenterX(), r.Top + 17f * dp, subTextPaint);
        subTextPaint.TextAlign = Paint.Align.Left;
        canvas.DrawText("Teto (+Y)", plotLeft, plotTop + 12f * dp, subTextPaint);
        canvas.DrawText("Ouvido (0)", plotLeft, earY - 4f * dp, subTextPaint);
        canvas.DrawText("Chão (-Y)", plotLeft, plotBot - 5f * dp, subTextPaint);

        for (int i = 0; i < sources.Count; i++) {
            TrackSourceItem item = sources[i];
            Vector3 np = item.NormalizedPosition;
            float px = plotLeft + Math.Clamp((np.X + 1f) * 0.5f, 0.04f, 0.96f) * plotW;
            float py = plotTop + Math.Clamp(1f - (np.Y + 1f) * 0.5f, 0.05f, 0.95f) * plotH;
            hitTargets.Add((item, px, py, np.X, np.Y, np.Z));

            bool muted = item.IsMuted || (anySolo && !item.IsSolo);
            float rmsBoost = muted ? 0f : Math.Clamp(item.RmsLevel * 18f, 0f, 12f) * dp;

            // Drop vertical guide line to ear/floor
            Paint p = muted ? mutedPaint : (item.IsBed ? (item.IsLFE ? lfePaint : bedPaint) : objPaint);
            stalkPaint.Color = Color.Argb(75, p.Color.R, p.Color.G, p.Color.B);
            canvas.DrawLine(px, earY, px, py, stalkPaint);

            if (item.IsBed) {
                float sz = 7.5f * dp;
                if (rmsBoost > 1f) canvas.DrawCircle(px, py, sz + rmsBoost, objGlowPaint.WithColor(Color.Argb(65, p.Color.R, p.Color.G, p.Color.B)));
                canvas.DrawRoundRect(px - sz, py - sz, px + sz, py + sz, 4.5f * dp, 4.5f * dp, p);
                if (item.IsSolo) canvas.DrawCircle(px, py, sz + 5.5f * dp, soloRingPaint);
                string shortLabel = item.Channel.HasValue && item.Channel.Value != ReferenceChannel.Unknown
                    ? item.Channel.Value.GetShortName()
                    : $"B{item.Index + 1}";
                textPaint.TextSize = 10f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText(shortLabel, px, py + sz + 11f * dp, textPaint);
            } else {
                float rad = 8.5f * dp;
                if (!muted && rmsBoost > 0.5f) canvas.DrawCircle(px, py, rad + rmsBoost, objGlowPaint.WithColor(Color.Argb(75, OneUiTheme.DynamicObject.R, OneUiTheme.DynamicObject.G, OneUiTheme.DynamicObject.B)));
                canvas.DrawCircle(px, py, rad, p);
                if (item.IsSolo) canvas.DrawCircle(px, py, rad + 5.5f * dp, soloRingPaint);
                textPaint.TextSize = 10f * dp;
                textPaint.Color = OneUiTheme.TextPrimary;
                canvas.DrawText($"O{i + 1}", px, py - rad - 4f * dp, textPaint);
            }
        }

        // Listener avatar at (X, Y)
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lx = plotLeft + (lnx + 1f) * 0.5f * plotW;
        float ly = plotTop + (1f - (lny + 1f) * 0.5f) * plotH;
        float yawRad = listenerRotation.Y * (MathF.PI / 180f);
        float pitchRad = listenerRotation.X * (MathF.PI / 180f);
        Draw3DHeadSphere(canvas, lx, ly, 15.5f * dp, yawRad, pitchRad, viewTilt: 0.08f, dp);
    }

    void DrawRoomHudTelemetry(Canvas canvas, RectF r, float dp) {
        float chipH = 22f * dp;
        float chipY = r.Bottom - chipH - 7f * dp;
        float chipX = r.Left + 8f * dp;
        string hud = $"({listenerPosition.X:0.0}, {listenerPosition.Y:0.0}, {listenerPosition.Z:0.0})m";
        subTextPaint.TextSize = 9.5f * dp;
        float hudWidth = Math.Min(r.Width() - 16f * dp, subTextPaint.MeasureText(hud) + 32f * dp);
        RectF chip = new(chipX, chipY, chipX + hudWidth, chipY + chipH);
        canvas.DrawRoundRect(chip, chipH * 0.5f, chipH * 0.5f, hudChipPaint);

        OneUiVectorIcons.DrawIcon(
            canvas,
            IsPositionLocked ? OneUiIconType.Lock : OneUiIconType.Unlock,
            chipX + 12f * dp,
            chipY + chipH * 0.5f,
            11.5f * dp,
            IsPositionLocked ? OneUiTheme.PrimaryHigh : OneUiTheme.TextPrimary);

        subTextPaint.TextAlign = Paint.Align.Left;
        canvas.DrawText(hud, chipX + 22f * dp, chipY + chipH * 0.5f - (subTextPaint.Ascent() + subTextPaint.Descent()) * 0.5f, subTextPaint);
    }

    void DrawDockHeadGimbal(Canvas canvas, RectF r, float yawRad, float pitchRad, float dp) {
        float corner = 20f * dp;
        canvas.DrawRoundRect(r, corner, corner, dockBgPaint);
        canvas.DrawRoundRect(r, corner, corner, roomBorderPaint);

        float sphereR = Math.Min(23f * dp, (r.Height() - 16f * dp) * 0.5f);
        float sphereCx = r.Left + 14f * dp + sphereR;
        float sphereCy = r.CenterY();
        Draw3DHeadSphere(canvas, sphereCx, sphereCy, sphereR, yawRad, pitchRad, viewTilt: 0.16f, dp);

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

        // Reset pill at bottom-right inside gimbal dock
        float rstH = 20f * dp;
        float rstW = Math.Max(46f * dp, r.Right - textLeft - 10f * dp);
        dockGimbalResetRect = new RectF(textLeft, r.Bottom - rstH - 8f * dp, textLeft + Math.Min(68f * dp, rstW), r.Bottom - 8f * dp);
        canvas.DrawRoundRect(dockGimbalResetRect, rstH * 0.5f, rstH * 0.5f, tabInactivePaint);
        textPaint.TextAlign = Paint.Align.Center;
        textPaint.TextSize = 9.5f * dp;
        textPaint.Color = OneUiTheme.TextSub;
        canvas.DrawText("Centralizar", dockGimbalResetRect.CenterX(), dockGimbalResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
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

        // Interactive capsule bar inside dock height box
        float barLeft = r.Left + 12f * dp;
        float barRight = r.Right - 12f * dp;
        float barCy = r.Top + 38f * dp;
        float barH = 12f * dp;
        RectF barRect = new(barLeft, barCy - barH * 0.5f, barRight, barCy + barH * 0.5f);
        canvas.DrawRoundRect(barRect, barH * 0.5f, barH * 0.5f, tabInactivePaint);

        float ny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float fillFrac = (ny + 1f) * 0.5f;
        float knobX = barLeft + fillFrac * barRect.Width();

        canvas.DrawLine(barRect.CenterX(), barRect.Top, barRect.CenterX(), barRect.Bottom, gridPaint);
        tabActivePaint.Color = OneUiTheme.FunctionalGreen;
        canvas.DrawRoundRect(new RectF(Math.Min(barRect.CenterX(), knobX), barRect.Top, Math.Max(barRect.CenterX(), knobX), barRect.Bottom),
            barH * 0.5f, barH * 0.5f, tabActivePaint);
        tabActivePaint.Color = OneUiTheme.Primary;

        canvas.DrawCircle(knobX, barCy, 7.5f * dp, objGlowPaint.WithColor(Color.White));

        // Quick 0m center button
        float zBtnH = 18f * dp;
        dockHeightResetRect = new RectF(r.CenterX() - 28f * dp, r.Bottom - zBtnH - 6f * dp, r.CenterX() + 28f * dp, r.Bottom - 6f * dp);
        textPaint.TextSize = 9.5f * dp;
        textPaint.Color = OneUiTheme.TextSub;
        canvas.DrawText("Arraste Y • 0m", r.CenterX(), dockHeightResetRect.CenterY() - (textPaint.Ascent() + textPaint.Descent()) * 0.5f, textPaint);
    }

    void Draw3DHeadSphere(Canvas canvas, float cx, float cy, float r, float yawRad, float pitchRad, float viewTilt, float dp) {
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

        Vector3 ProjectPoint(float x, float y, float z) {
            float cosP = MathF.Cos(pitchRad);
            float sinP = MathF.Sin(pitchRad);
            float y1 = y * cosP - z * sinP;
            float z1 = y * sinP + z * cosP;

            float cosY = MathF.Cos(yawRad);
            float sinY = MathF.Sin(yawRad);
            float x2 = x * cosY + z1 * sinY;
            float z2 = -x * sinY + z1 * cosY;

            float cosV = MathF.Cos(viewTilt);
            float sinV = MathF.Sin(viewTilt);
            float sy = y1 * cosV - z2 * sinV;
            float depth = y1 * sinV + z2 * cosV;
            return new Vector3(cx + x2 * r, cy - sy * r, depth);
        }

        void Draw3DRing(Func<float, Vector3> ringFn) {
            const int segs = 20;
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

        Draw3DRing(a => new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)));
        Draw3DRing(a => new Vector3(0f, MathF.Sin(a), MathF.Cos(a)));

        Vector3 leftEar = ProjectPoint(-1.04f, 0f, 0f);
        Vector3 rightEar = ProjectPoint(1.04f, 0f, 0f);
        Paint earPaint = objGlowPaint.WithColor(Color.Argb(240, 252, 252, 255));
        canvas.DrawCircle(leftEar.X, leftEar.Y, r * 0.23f, earPaint);
        canvas.DrawCircle(rightEar.X, rightEar.Y, r * 0.23f, earPaint);

        Vector3 noseBase = ProjectPoint(0f, 0f, 0.72f);
        Vector3 noseTip = ProjectPoint(0f, 0f, 1.40f);
        canvas.DrawLine(noseBase.X, noseBase.Y, noseTip.X, noseTip.Y, nosePaint);
        canvas.DrawCircle(noseTip.X, noseTip.Y, r * 0.20f, objGlowPaint.WithColor(Color.White));
    }

    (float lx, float ly) GetListenerTopDownCoords(RectF r) {
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        float lx = r.Left + (lnx + 1f) * 0.5f * r.Width();
        float ly = r.Top + (1f - (lnz + 1f) * 0.5f) * r.Height();
        return (lx, ly);
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
                pressedTabIdx = -1;

                for (int i = 0; i < 3; i++) {
                    if (modeTabRects[i].Contains(tx, ty)) {
                        pressedTabIdx = i;
                        dragMode = TouchDragMode.TabClick;
                        return true;
                    }
                }

                if (dockGimbalRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeadGimbal;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;
                }

                if (dockHeightRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.DockHeight;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromDockTouch(tx);
                    return true;
                }

                if (viewMode == RoomViewMode.TopWithHeight && sideHeightRect.Contains(tx, ty)) {
                    dragMode = TouchDragMode.HeightBar;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromTowerTouch(ty);
                    return true;
                }

                if (roomCanvasRect.Contains(tx, ty)) {
                    (float lx, float ly) = GetListenerScreenPos();
                    if (!IsPositionLocked && MathF.Sqrt((tx - lx) * (tx - lx) + (ty - ly) * (ty - ly)) <= 42f * dp) {
                        dragMode = TouchDragMode.ListenerMap;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        UpdateListenerFromMapTouch(tx, ty);
                        return true;
                    }
                }

                dragMode = TouchDragMode.None;
                return true;

            case MotionEventActions.Move: {
                float dx = tx - downX;
                float dy = ty - downY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > maxMoveDist) maxMoveDist = dist;

                if (dragMode == TouchDragMode.TabClick) {
                    return true;
                }

                if (dragMode == TouchDragMode.HeadGimbal) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    float stepX = tx - lastX;
                    float stepY = ty - lastY;
                    float newYaw = NormalizeYaw(listenerRotation.Y + stepX * 1.35f / dp);
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

                if (dragMode == TouchDragMode.HeightBar) {
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateHeightFromTowerTouch(ty);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                if (dragMode == TouchDragMode.ListenerMap) {
                    if (!IsPositionLocked) {
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        UpdateListenerFromMapTouch(tx, ty);
                    }
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                // Vertical scroll protection when starting outside dock controls / avatar
                if (Math.Abs(dy) > touchSlop && Math.Abs(dy) > Math.Abs(dx)) {
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    return false;
                }

                if (!IsPositionLocked && roomCanvasRect.Contains(tx, ty) &&
                    Math.Abs(dx) > touchSlop * 1.2f && Math.Abs(dx) > Math.Abs(dy) * 1.25f) {
                    dragMode = TouchDragMode.ListenerMap;
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateListenerFromMapTouch(tx, ty);
                    lastX = tx;
                    lastY = ty;
                    return true;
                }

                lastX = tx;
                lastY = ty;
                return true;
            }

            case MotionEventActions.Up: {
                Parent?.RequestDisallowInterceptTouchEvent(false);
                bool wasTap = maxMoveDist < touchSlop;
                TouchDragMode endedMode = dragMode;
                int tab = pressedTabIdx;
                dragMode = TouchDragMode.None;
                pressedTabIdx = -1;

                if (!wasTap) {
                    return true;
                }

                if (endedMode == TouchDragMode.TabClick && tab >= 0 && tab < 3) {
                    ActiveViewMode = (RoomViewMode)tab;
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
                            UpdateListenerFromMapTouch(tx, ty);
                        }
                    }
                }
                return true;
            }

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                dragMode = TouchDragMode.None;
                pressedTabIdx = -1;
                return false;
        }

        return base.OnTouchEvent(e);
    }

    (float lx, float ly) GetListenerScreenPos() {
        float lnx = Math.Clamp(listenerPosition.X / environmentSize.X, -1f, 1f);
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);
        float lnz = Math.Clamp(listenerPosition.Z / environmentSize.Z, -1f, 1f);
        float dp = Resources.DisplayMetrics.Density;

        switch (viewMode) {
            case RoomViewMode.Perspective3D:
                var pt = Project3D(roomCanvasRect, lnx, lny, lnz);
                return (pt.sx, pt.sy);
            case RoomViewMode.FrontElevation:
                float plotLeft = roomCanvasRect.Left + 18f * dp;
                float plotW = roomCanvasRect.Right - 18f * dp - plotLeft;
                float plotTop = roomCanvasRect.Top + 26f * dp;
                float plotH = Math.Max(60f * dp, roomCanvasRect.Bottom - 36f * dp - plotTop);
                return (plotLeft + (lnx + 1f) * 0.5f * plotW, plotTop + (1f - (lny + 1f) * 0.5f) * plotH);
            default:
                return GetListenerTopDownCoords(roomCanvasRect);
        }
    }

    void UpdateListenerFromMapTouch(float tx, float ty) {
        if (roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) {
            return;
        }
        float dp = Resources.DisplayMetrics.Density;
        float lny = Math.Clamp(listenerPosition.Y / environmentSize.Y, -1f, 1f);

        switch (viewMode) {
            case RoomViewMode.Perspective3D: {
                var (nx, nz) = Unproject3DHorizontal(roomCanvasRect, tx, ty, lny);
                listenerPosition = new Vector3(nx * environmentSize.X, listenerPosition.Y, nz * environmentSize.Z);
                break;
            }
            case RoomViewMode.FrontElevation: {
                float plotLeft = roomCanvasRect.Left + 18f * dp;
                float plotW = Math.Max(1f, roomCanvasRect.Right - 18f * dp - plotLeft);
                float plotTop = roomCanvasRect.Top + 26f * dp;
                float plotH = Math.Max(1f, roomCanvasRect.Bottom - 36f * dp - plotTop);
                float nx = Math.Clamp((tx - plotLeft) / plotW * 2f - 1f, -1f, 1f);
                float ny = Math.Clamp((1f - (ty - plotTop) / plotH) * 2f - 1f, -1f, 1f);
                listenerPosition = new Vector3(nx * environmentSize.X, ny * environmentSize.Y, listenerPosition.Z);
                break;
            }
            default: {
                float nx = Math.Clamp((tx - roomCanvasRect.Left) / roomCanvasRect.Width() * 2f - 1f, -1f, 1f);
                float nz = Math.Clamp((1f - (ty - roomCanvasRect.Top) / roomCanvasRect.Height()) * 2f - 1f, -1f, 1f);
                listenerPosition = new Vector3(nx * environmentSize.X, listenerPosition.Y, nz * environmentSize.Z);
                break;
            }
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

    void UpdateHeightFromTowerTouch(float ty) {
        float dp = Resources.DisplayMetrics.Density;
        float plotTop = sideHeightRect.Top + 30f * dp;
        float plotH = Math.Max(1f, sideHeightRect.Bottom - 16f * dp - plotTop);
        float ny = Math.Clamp((1f - (ty - plotTop) / plotH) * 2f - 1f, -1f, 1f);
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

    static float NormalizeYaw(float deg) {
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
