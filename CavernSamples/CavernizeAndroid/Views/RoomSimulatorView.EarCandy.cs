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
}
