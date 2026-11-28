using CavernizeAndroid.Language;
using System.Numerics;

using Android.Graphics;
using Android.Views;

using Cavern.Spatial;

using Cavernize.Logic.Models;

using CavernizeAndroid.Theme;

namespace CavernizeAndroid.Views;

public sealed partial class RoomSimulatorView {
    struct EarCandyParticle {
        public float Nx;
        public float Ny;
        public float Nz;
        public float Vx;
        public float Vy;
        public float Vz;
        public float Life01;
        public byte Band;
        public bool Active;
    }

    const int MaxEarCandyParticles = 36;

    readonly EarCandyMicroNode[] earCandyNodes = new EarCandyMicroNode[EarCandySpectralDetector.MaxMicroObjects];
    readonly EarCandyParticle[] earCandyParticles = new EarCandyParticle[MaxEarCandyParticles];
    readonly Paint earCandyGlowPaint = new(PaintFlags.AntiAlias);
    readonly Paint earCandyArcPaint = new(PaintFlags.AntiAlias);
    readonly RectF earCandyArcRect = new();

    int earCandyNodeCount;
    float earCandyOverallSparkle01;
    int earCandyEmitCursor;
    long lastEarCandyEmitMs;
    bool earCandyPaintsInitialized;

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

    /// <summary>
    /// Synchronizes V4.2 zero-GC DSP ear-candy micro-object telemetry for smooth 120 Hz canvas rendering.
    /// </summary>
    public void UpdateEarCandyState(EarCandyMicroNode[] nodes, int activeCount, float sparkle01, bool triggerInvalidate = false) {
        int clampedCount = nodes == null ? 0 : Math.Clamp(activeCount, 0, earCandyNodes.Length);
        earCandyNodeCount = clampedCount;
        earCandyOverallSparkle01 = Math.Clamp(sparkle01, 0f, 1f);
        for (int i = 0; i < clampedCount; i++) {
            earCandyNodes[i] = nodes[i];
        }
        for (int i = clampedCount; i < earCandyNodes.Length; i++) {
            earCandyNodes[i].Active = false;
        }
        if (triggerInvalidate && (clampedCount > 0 || earCandyOverallSparkle01 > 0.03f) &&
            Visibility == ViewStates.Visible && WindowVisibility == ViewStates.Visible) {
            PostInvalidateOnAnimation();
        }
    }

    void EnsureEarCandyPaints(float dp) {
        if (earCandyPaintsInitialized) {
            return;
        }
        earCandyGlowPaint.SetStyle(Paint.Style.Fill);
        earCandyArcPaint.SetStyle(Paint.Style.Stroke);
        earCandyArcPaint.StrokeCap = Paint.Cap.Round;
        earCandyArcPaint.StrokeWidth = 1.45f * dp;
        earCandyPaintsInitialized = true;
    }

    bool DrawEarCandyOverlay(
        Canvas canvas,
        RectF r,
        float dt,
        long nowMs,
        float camYawRad,
        float camPitchRad,
        float dp) {
        EnsureEarCandyPaints(dp);

        // Emit subtle sparkle particles at active HF/transient nodes without heap allocation
        if (isAudioPlaying && earCandyNodeCount > 0 && nowMs - lastEarCandyEmitMs >= 32L) {
            lastEarCandyEmitMs = nowMs;
            for (int n = 0; n < earCandyNodeCount; n++) {
                EarCandyMicroNode node = earCandyNodes[n];
                if (!node.Active || (node.Sparkle01 < 0.08f && node.TransientFlux01 < 0.14f)) {
                    continue;
                }
                Vector3 dir = PsychoacousticGovernor.SphericalToCartesianUnit(node.AzimuthDeg, node.ElevationDeg) * node.Radius;
                int slot = earCandyEmitCursor % MaxEarCandyParticles;
                earCandyEmitCursor++;
                float jitterAz = ((slot & 3) - 1.5f) * 0.06f;
                earCandyParticles[slot] = new EarCandyParticle {
                    Nx = Math.Clamp(dir.X + jitterAz, -0.98f, 0.98f),
                    Ny = Math.Clamp(dir.Y, -0.95f, 0.95f),
                    Nz = Math.Clamp(dir.Z, -0.98f, 0.98f),
                    Vx = jitterAz * 0.45f,
                    Vy = 0.12f + 0.16f * node.Sparkle01,
                    Vz = ((slot & 1) == 0 ? 1f : -1f) * 0.04f,
                    Life01 = 1f,
                    Band = node.BandHint,
                    Active = true
                };
            }
        }

        float safeDt = Math.Clamp(dt, 0.004f, 0.05f);
        bool anyAnimating = false;

        // Render harmonic arcs + glowing transient micro-object beads directly at active HF nodes
        for (int n = 0; n < earCandyNodeCount; n++) {
            EarCandyMicroNode node = earCandyNodes[n];
            if (!node.Active || node.Sparkle01 < 0.03f) {
                continue;
            }
            anyAnimating = true;
            Vector3 unit = PsychoacousticGovernor.SphericalToCartesianUnit(node.AzimuthDeg, node.ElevationDeg) * node.Radius;
            var pt = ProjectOrbit3D(r, unit.X, unit.Y, unit.Z, camYawRad, camPitchRad);
            float intensity = Math.Clamp(node.Sparkle01 * 0.75f + node.TransientFlux01 * 0.45f, 0f, 1f);
            float arcRad = (6.5f + 7.5f * intensity) * dp * pt.persp;

            GetEarCandyBandColorRgb(node.BandHint, out int cr, out int cg, out int cb);
            int arcAlpha = (int)Math.Round(150f * intensity);
            earCandyArcPaint.Color = Color.Argb(arcAlpha, cr, cg, cb);
            earCandyArcPaint.StrokeWidth = (1.15f + 0.85f * intensity) * dp * pt.persp;
            earCandyArcRect.Set(pt.sx - arcRad, pt.sy - arcRad, pt.sx + arcRad, pt.sy + arcRad);
            float sweepAngle = 68f + 135f * intensity;
            float baseAngle = -125f + (n * 53f) + (nowMs * 0.045f % 360f);
            canvas.DrawArc(earCandyArcRect, baseAngle, sweepAngle, false, earCandyArcPaint);

            earCandyGlowPaint.Color = Color.Argb((int)Math.Round(55f * intensity), cr, cg, cb);
            canvas.DrawCircle(pt.sx, pt.sy, arcRad * 0.72f, earCandyGlowPaint);
            earCandyGlowPaint.Color = Color.Argb((int)Math.Round(210f * intensity), cr, cg, cb);
            canvas.DrawCircle(pt.sx, pt.sy, (1.9f + 1.4f * intensity) * dp * pt.persp, earCandyGlowPaint);
        }

        // Advance and draw floating air-dust sparkle particles at 120 Hz
        for (int p = 0; p < MaxEarCandyParticles; p++) {
            if (!earCandyParticles[p].Active) {
                continue;
            }
            earCandyParticles[p].Life01 -= safeDt * 1.35f;
            if (earCandyParticles[p].Life01 <= 0.01f) {
                earCandyParticles[p].Active = false;
                continue;
            }
            anyAnimating = true;
            earCandyParticles[p].Nx = Math.Clamp(earCandyParticles[p].Nx + earCandyParticles[p].Vx * safeDt, -1f, 1f);
            earCandyParticles[p].Ny = Math.Clamp(earCandyParticles[p].Ny + earCandyParticles[p].Vy * safeDt, -1f, 1f);
            earCandyParticles[p].Nz = Math.Clamp(earCandyParticles[p].Nz + earCandyParticles[p].Vz * safeDt, -1f, 1f);

            var pt = ProjectOrbit3D(
                r,
                earCandyParticles[p].Nx,
                earCandyParticles[p].Ny,
                earCandyParticles[p].Nz,
                camYawRad,
                camPitchRad);
            float life = earCandyParticles[p].Life01;
            GetEarCandyBandColorRgb(earCandyParticles[p].Band, out int cr, out int cg, out int cb);
            float dotRad = (1.1f + 1.8f * life) * dp * pt.persp;
            earCandyGlowPaint.Color = Color.Argb((int)Math.Round(45f * life), cr, cg, cb);
            canvas.DrawCircle(pt.sx, pt.sy, dotRad * 2.1f, earCandyGlowPaint);
            earCandyGlowPaint.Color = Color.Argb((int)Math.Round(185f * life), cr, cg, cb);
            canvas.DrawCircle(pt.sx, pt.sy, dotRad, earCandyGlowPaint);
        }

        return anyAnimating;
    }

    static void GetEarCandyBandColorRgb(byte bandHint, out int r, out int g, out int b) {
        switch (bandHint) {
            case 2: // Air Sparkle (>= 8.5 kHz)
                r = 104;
                g = 232;
                b = 255;
                break;
            case 1: // Presence Edge (5.2 - 8.5 kHz)
                r = 186;
                g = 150;
                b = 255;
                break;
            default: // Upper-mid harmonic
                r = OneUiTheme.PrimaryHigh.R;
                g = 212;
                b = 152;
                break;
        }
    }

    void RequestSourcePositionReset(TrackSourceItem item) {
        if (item == null) return;
        item.ResetPosition();
        if (smoothedStates.TryGetValue(GetSourceStateKey(item), out SmoothedSourceState st)) {
            st.SmoothedPos = item.NormalizedPosition;
            st.LastRawPos = item.NormalizedPosition;
            st.TrailCount = 0;
            st.TrailHead = 0;
            st.WasCustomPosition = false;
            if (item.HasDynamicTrajectory) {
                st.Initialized = false;
            }
        }
        SourcePositionResetRequested?.Invoke(item);
        Invalidate();
    }

    void ResetAllPinnedSources() {
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].HasCustomPosition) {
                RequestSourcePositionReset(sources[i]);
            }
        }
    }

    void UpdateDraggedSourceFromRoomTouch(TrackSourceItem item, float tx, float ty, float prevX, float prevY) {
        if (item == null || roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) return;
        Vector3 cur = item.HasCustomPosition ? item.CustomNormalizedPosition : item.NormalizedPosition;
        Vector3 next = ProjectScreenDeltaToRoom(cur, tx - prevX, ty - prevY);
        item.HasCustomPosition = true;
        item.CustomNormalizedPosition = next;
        item.NormalizedPosition = next;
        if (smoothedStates.TryGetValue(GetSourceStateKey(item), out SmoothedSourceState st)) {
            st.SmoothedPos = next;
            st.LastRawPos = next;
            st.WasCustomPosition = true;
        }
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

    void UpdateListenerFromRoomTouch(float tx, float ty, float prevX, float prevY) {
        if (roomCanvasRect.Width() <= 0 || roomCanvasRect.Height() <= 0) return;
        float ex = Math.Max(0.25f, environmentSize.X);
        float ey = Math.Max(0.25f, environmentSize.Y);
        float ez = Math.Max(0.25f, environmentSize.Z);
        Vector3 curNorm = new(
            Math.Clamp(listenerPosition.X / ex, -1f, 1f),
            Math.Clamp(listenerPosition.Y / ey, -1f, 1f),
            Math.Clamp(listenerPosition.Z / ez, -1f, 1f));
        Vector3 nextNorm = ProjectScreenDeltaToRoom(curNorm, tx - prevX, ty - prevY);
        listenerPosition = new Vector3(nextNorm.X * ex, nextNorm.Y * ey, nextNorm.Z * ez);
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

    bool DrawSpatialXRaySphericalOverlay(
        Canvas canvas, RectF r, float lnx, float lny, float lnz,
        float camYawRad, float camPitchRad, float dt, float dp) {
        if (xrayPowermap == null) return false;
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

        // Dominant Direction-of-Arrival (DoA) acoustic vector: smooth angular slerp + slew clamp + coherence fade
        bool slewing = false;
        float targetCoh = Math.Clamp(xrayPowermap.DominantCoherence, 0f, 1f);
        float cohBlend = 1f - MathF.Exp(-8f * dt);
        smoothedDoaCoherence += (targetCoh - smoothedDoaCoherence) * cohBlend;
        if (MathF.Abs(targetCoh - smoothedDoaCoherence) > 0.01f) {
            slewing = true;
        }

        Vector3 targetDoa = xrayPowermap.DominantDirection;
        if (targetDoa.LengthSquared() > 0.04f) {
            Vector3 targetUnit = Vector3.Normalize(targetDoa);
            if (!hasSmoothedDoaDir) {
                smoothedDoaDir = targetUnit;
                hasSmoothedDoaDir = true;
            } else {
                float cosTheta = Math.Clamp(Vector3.Dot(smoothedDoaDir, targetUnit), -1f, 1f);
                float angle = MathF.Acos(cosTheta);
                if (angle > 0.004f) {
                    slewing = true;
                    float maxStep = 3.2f * dt;
                    float expFrac = 1f - MathF.Exp(-7.5f * dt);
                    float stepFrac = MathF.Min(expFrac, maxStep / angle);
                    Vector3 interp;
                    if (angle < 0.02f) {
                        interp = Vector3.Normalize(Vector3.Lerp(smoothedDoaDir, targetUnit, stepFrac));
                    } else if (angle > MathF.PI - 0.05f) {
                        Vector3 fallbackAxis = MathF.Abs(smoothedDoaDir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
                        Vector3 perp = Vector3.Normalize(Vector3.Cross(smoothedDoaDir, fallbackAxis));
                        float theta = stepFrac * angle;
                        interp = Vector3.Normalize(smoothedDoaDir * MathF.Cos(theta) + perp * MathF.Sin(theta));
                    } else {
                        float sinA = MathF.Sin(angle);
                        float w0 = MathF.Sin((1f - stepFrac) * angle) / sinA;
                        float w1 = MathF.Sin(stepFrac * angle) / sinA;
                        interp = Vector3.Normalize(smoothedDoaDir * w0 + targetUnit * w1);
                    }
                    smoothedDoaDir = interp;
                }
            }

            float cohVis = smoothedDoaCoherence <= 0.08f
                ? 0f
                : (smoothedDoaCoherence < 0.20f
                    ? Math.Clamp((smoothedDoaCoherence - 0.08f) / 0.12f * 0.35f, 0.08f, 0.35f)
                    : Math.Clamp(0.35f + (smoothedDoaCoherence - 0.20f) / 0.35f * 0.65f, 0.35f, 1f));
            if (cohVis > 0.04f) {
                var lPt = ProjectOrbit3D(r, lnx, lny, lnz, camYawRad, camPitchRad);
                var tip = ProjectOrbit3D(r,
                    Math.Clamp(lnx + smoothedDoaDir.X * 0.62f, -1f, 1f),
                    Math.Clamp(lny + smoothedDoaDir.Y * 0.62f, -1f, 1f),
                    Math.Clamp(lnz + smoothedDoaDir.Z * 0.62f, -1f, 1f),
                    camYawRad, camPitchRad);
                strokePaint.StrokeWidth = 2.0f * dp;
                strokePaint.Color = Color.Argb(Math.Clamp((int)Math.Round(205f * cohVis), 8, 205), 86, 232, 255);
                canvas.DrawLine(lPt.sx, lPt.sy, tip.sx, tip.sy, strokePaint);
                fillPaint.SetShader(null);
                fillPaint.Color = Color.Argb(Math.Clamp((int)Math.Round(255f * cohVis), 12, 255), 86, 232, 255);
                canvas.DrawCircle(tip.sx, tip.sy, 3.2f * dp, fillPaint);
            }
        }
        return slewing;
    }
}
