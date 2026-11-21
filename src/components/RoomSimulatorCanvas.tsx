import React, { useEffect, useRef, useState } from 'react';
import { Lock, Radar, RotateCcw, Unlock, ZoomIn, ZoomOut } from 'lucide-react';
import type { StringKey } from '../i18n/strings';
import type {
  CameraPreset,
  LabelVisibilityMode,
  SpatialSourceNode,
  Vec3,
} from '../types/cavern';
import { clamp, clampToRoom, distance3D, projectRoomPoint } from '../utils/spatialMath';
import {
  computePropagationDelayMs,
  computeSphericalPowermap,
  getSourceKindHexColor,
  isSourceDraggable,
  type XRayBandMode,
} from '../utils/spatialXRay';

interface RoomSimulatorCanvasProps {
  t: Record<StringKey, string>;
  sources: SpatialSourceNode[];
  roomSize: Vec3;
  listenerPosition: Vec3;
  listenerYawDeg: number;
  listenerPitchDeg: number;
  isPlaying: boolean;
  isLocked: boolean;
  isXRayEnabled: boolean;
  xrayBandMode: XRayBandMode;
  propagationDelay6DoF: boolean;
  onToggleLock: () => void;
  onToggleXRay: () => void;
  onChangeXRayBand: (band: XRayBandMode) => void;
  onListenerPositionChange: (pos: Vec3) => void;
  onMoveSource: (sourceId: string, pos: Vec3) => void;
  onResetSourcePin: (sourceId: string) => void;
  onToggleSolo: (sourceId: string) => void;
}

interface HitTarget {
  id: string;
  x: number;
  y: number;
  radius: number;
  draggable: boolean;
  pinned: boolean;
}

const BAND_PILLS: Array<{ id: XRayBandMode; shortLabel: string }> = [
  { id: 'all', shortLabel: 'All' },
  { id: 'low', shortLabel: 'L' },
  { id: 'mid', shortLabel: 'M' },
  { id: 'high', shortLabel: 'H' },
];

export const RoomSimulatorCanvas: React.FC<RoomSimulatorCanvasProps> = ({
  t,
  sources,
  roomSize,
  listenerPosition,
  listenerYawDeg,
  listenerPitchDeg,
  isPlaying,
  isLocked,
  isXRayEnabled,
  xrayBandMode,
  propagationDelay6DoF,
  onToggleLock,
  onToggleXRay,
  onChangeXRayBand,
  onListenerPositionChange,
  onMoveSource,
  onResetSourcePin,
  onToggleSolo,
}) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const trailsRef = useRef<Map<string, Vec3[]>>(new Map());
  const hitTargetsRef = useRef<HitTarget[]>([]);
  const listenerScreenRef = useRef<{ x: number; y: number }>({ x: 0, y: 0 });
  const dragStateRef = useRef<{
    mode: 'none' | 'orbit' | 'listener' | 'source';
    sourceId: string | null;
    startX: number;
    startY: number;
    lastX: number;
    lastY: number;
    moved: boolean;
  }>({
    mode: 'none',
    sourceId: null,
    startX: 0,
    startY: 0,
    lastX: 0,
    lastY: 0,
    moved: false,
  });

  const [camYaw, setCamYaw] = useState(-24);
  const [camPitch, setCamPitch] = useState(28);
  const [zoom, setZoom] = useState(1.0);
  const [labelMode, setLabelMode] = useState<LabelVisibilityMode>('active');
  const [resizeTick, setResizeTick] = useState(0);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => setResizeTick((n) => n + 1));
    observer.observe(canvas);
    return () => observer.disconnect();
  }, []);

  const applyCameraPreset = (preset: CameraPreset) => {
    if (preset === 'iso') {
      setCamYaw(-24);
      setCamPitch(28);
      setZoom(1.0);
    } else if (preset === 'top') {
      setCamYaw(0);
      setCamPitch(84);
      setZoom(1.05);
    } else {
      setCamYaw(0);
      setCamPitch(8);
      setZoom(1.0);
    }
  };

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(320, Math.floor(rect.width));
    const height = Math.max(320, Math.floor(rect.height));

    if (canvas.width !== Math.floor(width * dpr) || canvas.height !== Math.floor(height * dpr)) {
      canvas.width = Math.floor(width * dpr);
      canvas.height = Math.floor(height * dpr);
    }

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);

    // Background acoustic chamber gradient
    const bgGrad = ctx.createRadialGradient(
      width * 0.5,
      height * 0.45,
      20,
      width * 0.5,
      height * 0.5,
      Math.max(width, height) * 0.65,
    );
    bgGrad.addColorStop(0, '#131826');
    bgGrad.addColorStop(1, '#090B12');
    ctx.fillStyle = bgGrad;
    ctx.fillRect(0, 0, width, height);

    const hx = roomSize.x * 0.5;
    const hy = roomSize.y * 0.5;
    const hz = roomSize.z * 0.5;

    const proj = (p: Vec3) => projectRoomPoint(p, camYaw, camPitch, zoom, width, height);

    const drawPoly = (pts: Vec3[], fill: string, stroke?: string) => {
      const screen = pts.map(proj);
      ctx.beginPath();
      screen.forEach((p, idx) => (idx === 0 ? ctx.moveTo(p.x, p.y) : ctx.lineTo(p.x, p.y)));
      ctx.closePath();
      ctx.fillStyle = fill;
      ctx.fill();
      if (stroke) {
        ctx.strokeStyle = stroke;
        ctx.lineWidth = 1;
        ctx.stroke();
      }
    };

    // Floor & translucent acoustic walls
    drawPoly(
      [
        { x: -hx, y: -hy, z: -hz },
        { x: hx, y: -hy, z: -hz },
        { x: hx, y: -hy, z: hz },
        { x: -hx, y: -hy, z: hz },
      ],
      'rgba(28, 36, 54, 0.36)',
      'rgba(255, 255, 255, 0.14)',
    );

    // Front cinema screen wall (+Z)
    drawPoly(
      [
        { x: -hx * 0.72, y: -hy * 0.35, z: hz },
        { x: hx * 0.72, y: -hy * 0.35, z: hz },
        { x: hx * 0.72, y: hy * 0.65, z: hz },
        { x: -hx * 0.72, y: hy * 0.65, z: hz },
      ],
      'rgba(56, 122, 255, 0.12)',
      'rgba(89, 143, 255, 0.45)',
    );

    const screenLabel = proj({ x: 0, y: hy * 0.56, z: hz });
    ctx.fillStyle = 'rgba(137, 176, 255, 0.72)';
    ctx.font = '600 9.5px "JetBrains Mono", monospace';
    ctx.textAlign = 'center';
    ctx.fillText('FRONT SCREEN', screenLabel.x, screenLabel.y);

    // Floor grid lines
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.06)';
    ctx.lineWidth = 1;
    for (let i = -4; i <= 4; i += 2) {
      const a1 = proj({ x: (i / 5) * hx, y: -hy, z: -hz });
      const a2 = proj({ x: (i / 5) * hx, y: -hy, z: hz });
      ctx.beginPath();
      ctx.moveTo(a1.x, a1.y);
      ctx.lineTo(a2.x, a2.y);
      ctx.stroke();

      const b1 = proj({ x: -hx, y: -hy, z: (i / 5) * hz });
      const b2 = proj({ x: hx, y: -hy, z: (i / 5) * hz });
      ctx.beginPath();
      ctx.moveTo(b1.x, b1.y);
      ctx.lineTo(b2.x, b2.y);
      ctx.stroke();
    }

    // Ceiling height cage wireframe
    const ceilingCorners: Vec3[] = [
      { x: -hx, y: hy, z: -hz },
      { x: hx, y: hy, z: -hz },
      { x: hx, y: hy, z: hz },
      { x: -hx, y: hy, z: hz },
    ];
    const floorCorners: Vec3[] = [
      { x: -hx, y: -hy, z: -hz },
      { x: hx, y: -hy, z: -hz },
      { x: hx, y: -hy, z: hz },
      { x: -hx, y: -hy, z: hz },
    ];

    ctx.strokeStyle = 'rgba(110, 156, 255, 0.16)';
    ctx.beginPath();
    ceilingCorners.forEach((c, idx) => {
      const p = proj(c);
      if (idx === 0) ctx.moveTo(p.x, p.y);
      else ctx.lineTo(p.x, p.y);
    });
    ctx.closePath();
    ctx.stroke();

    // Vertical corner pillars
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.1)';
    for (let i = 0; i < 4; i += 1) {
      const pTop = proj(ceilingCorners[i]);
      const pBot = proj(floorCorners[i]);
      ctx.beginPath();
      ctx.moveTo(pTop.x, pTop.y);
      ctx.lineTo(pBot.x, pBot.y);
      ctx.stroke();
    }

    // Sweet-spot ear-level horizon ring
    ctx.strokeStyle = 'rgba(89, 143, 255, 0.22)';
    ctx.setLineDash([4, 5]);
    ctx.beginPath();
    for (let deg = 0; deg <= 360; deg += 10) {
      const rad = (deg * Math.PI) / 180;
      const pt = proj({ x: Math.cos(rad) * 3.2, y: 0, z: Math.sin(rad) * 3.2 });
      if (deg === 0) ctx.moveTo(pt.x, pt.y);
      else ctx.lineTo(pt.x, pt.y);
    }
    ctx.stroke();
    ctx.setLineDash([]);

    const listenerProj = proj(listenerPosition);
    listenerScreenRef.current = { x: listenerProj.x, y: listenerProj.y };

    // W13 Spherical 3-Band X-Ray Powermap Overlay (72 directional lobes + Dominant DoA vector)
    if (isXRayEnabled) {
      const powermap = computeSphericalPowermap(sources, listenerPosition, xrayBandMode);
      const sphereRadius = 2.65;

      // Subtle spherical shell equator & prime meridian
      ctx.strokeStyle = 'rgba(34, 211, 238, 0.16)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (let deg = 0; deg <= 360; deg += 15) {
        const rad = (deg * Math.PI) / 180;
        const pt = proj({
          x: listenerPosition.x + Math.cos(rad) * sphereRadius,
          y: listenerPosition.y,
          z: listenerPosition.z + Math.sin(rad) * sphereRadius,
        });
        if (deg === 0) ctx.moveTo(pt.x, pt.y);
        else ctx.lineTo(pt.x, pt.y);
      }
      ctx.stroke();

      // Render 72 spherical cells back-to-front
      const projectedCells = powermap.cells
        .filter((c) => c.normalized > 0.1)
        .map((cell) => {
          const p = proj({
            x: listenerPosition.x + cell.dir.x * sphereRadius,
            y: listenerPosition.y + cell.dir.y * sphereRadius * 0.78,
            z: listenerPosition.z + cell.dir.z * sphereRadius,
          });
          return { cell, p };
        })
        .sort((a, b) => a.p.depth - b.p.depth);

      for (const { cell, p } of projectedCells) {
        const intensity = cell.normalized;
        let rCol = 56;
        let gCol = 189;
        let bCol = 248;
        if (xrayBandMode === 'low') {
          rCol = 249;
          gCol = 115;
          bCol = 22;
        } else if (xrayBandMode === 'mid') {
          rCol = 52;
          gCol = 211;
          bCol = 153;
        } else if (xrayBandMode === 'high') {
          rCol = 34;
          gCol = 211;
          bCol = 238;
        } else {
          // Spectral blend: Low = warm amber, Mid = emerald, High = cyan
          const sum = Math.max(1e-6, cell.low + cell.mid + cell.high);
          const wl = cell.low / sum;
          const wm = cell.mid / sum;
          const wh = cell.high / sum;
          rCol = Math.round(wl * 249 + wm * 52 + wh * 34);
          gCol = Math.round(wl * 115 + wm * 211 + wh * 211);
          bCol = Math.round(wl * 22 + wm * 153 + wh * 238);
        }

        const blobR = Math.max(8, (10 + intensity * 18) * p.scale);
        const grad = ctx.createRadialGradient(p.x, p.y, 1, p.x, p.y, blobR);
        grad.addColorStop(0, `rgba(${rCol}, ${gCol}, ${bCol}, ${(0.34 * intensity).toFixed(3)})`);
        grad.addColorStop(1, `rgba(${rCol}, ${gCol}, ${bCol}, 0)`);
        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.arc(p.x, p.y, blobR, 0, Math.PI * 2);
        ctx.fill();
      }

      // Dominant Direction-of-Arrival (DoA) vector arrow from listener
      if (powermap.peakEnergy > 1e-5) {
        const doaTip = proj({
          x: listenerPosition.x + powermap.dominantDoA.x * (sphereRadius + 0.55),
          y: listenerPosition.y + powermap.dominantDoA.y * (sphereRadius * 0.78),
          z: listenerPosition.z + powermap.dominantDoA.z * (sphereRadius + 0.55),
        });
        ctx.strokeStyle = 'rgba(34, 211, 238, 0.85)';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(listenerProj.x, listenerProj.y);
        ctx.lineTo(doaTip.x, doaTip.y);
        ctx.stroke();

        ctx.beginPath();
        ctx.arc(doaTip.x, doaTip.y, 4, 0, Math.PI * 2);
        ctx.fillStyle = '#22D3EE';
        ctx.fill();
      }
    }

    // Update trails for dynamic 3D objects
    for (const src of sources) {
      if (src.kind === 'object' && !src.customPosition) {
        const list = trailsRef.current.get(src.id) ?? [];
        const prev = list[list.length - 1];
        if (!prev || Math.hypot(prev.x - src.position.x, prev.y - src.position.y, prev.z - src.position.z) > 0.18) {
          list.push({ ...src.position });
          if (list.length > 10) list.shift();
          trailsRef.current.set(src.id, list);
        }
      }
    }

    // Depth-sorted drawable entities (sources + listener head)
    const anySolo = sources.some((s) => s.solo);

    type DrawItem =
      | { kind: 'listener'; depth: number }
      | { kind: 'source'; source: SpatialSourceNode; proj: ReturnType<typeof proj> };

    const items: DrawItem[] = [
      { kind: 'listener', depth: listenerProj.depth },
      ...sources.map((source) => ({
        kind: 'source' as const,
        source,
        proj: proj(source.position),
      })),
    ];

    items.sort((a, b) => {
      const da = a.kind === 'listener' ? a.depth : a.proj.depth;
      const db = b.kind === 'listener' ? b.depth : b.proj.depth;
      return da - db;
    });

    const newHits: HitTarget[] = [];

    for (const item of items) {
      if (item.kind === 'listener') {
        const floorP = proj({ x: listenerPosition.x, y: -hy, z: listenerPosition.z });
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.25)';
        ctx.setLineDash([3, 3]);
        ctx.beginPath();
        ctx.moveTo(listenerProj.x, listenerProj.y);
        ctx.lineTo(floorP.x, floorP.y);
        ctx.stroke();
        ctx.setLineDash([]);

        // Listener forward cone
        const yawRad = (listenerYawDeg * Math.PI) / 180;
        const pitchRad = (listenerPitchDeg * Math.PI) / 180;
        const tip = proj({
          x: listenerPosition.x + Math.sin(yawRad) * Math.cos(pitchRad) * 1.9,
          y: listenerPosition.y + Math.sin(pitchRad) * 1.9,
          z: listenerPosition.z + Math.cos(yawRad) * Math.cos(pitchRad) * 1.9,
        });
        const leftWing = proj({
          x: listenerPosition.x + Math.sin(yawRad - 0.4) * 1.45,
          y: listenerPosition.y + Math.sin(pitchRad) * 1.1,
          z: listenerPosition.z + Math.cos(yawRad - 0.4) * 1.45,
        });
        const rightWing = proj({
          x: listenerPosition.x + Math.sin(yawRad + 0.4) * 1.45,
          y: listenerPosition.y + Math.sin(pitchRad) * 1.1,
          z: listenerPosition.z + Math.cos(yawRad + 0.4) * 1.45,
        });

        ctx.beginPath();
        ctx.moveTo(listenerProj.x, listenerProj.y);
        ctx.lineTo(leftWing.x, leftWing.y);
        ctx.lineTo(tip.x, tip.y);
        ctx.lineTo(rightWing.x, rightWing.y);
        ctx.closePath();
        ctx.fillStyle = 'rgba(56, 122, 255, 0.22)';
        ctx.fill();
        ctx.strokeStyle = 'rgba(137, 176, 255, 0.65)';
        ctx.lineWidth = 1.2;
        ctx.stroke();

        // Articulated listener head sphere
        const r = Math.max(11, 14 * listenerProj.scale);
        ctx.beginPath();
        ctx.arc(listenerProj.x, listenerProj.y, r, 0, Math.PI * 2);
        ctx.fillStyle = '#1E273D';
        ctx.fill();
        ctx.strokeStyle = isLocked ? '#F59E0B' : '#FCFCFF';
        ctx.lineWidth = 2;
        ctx.stroke();

        const noseX = listenerProj.x + (tip.x - listenerProj.x) * 0.28;
        const noseY = listenerProj.y + (tip.y - listenerProj.y) * 0.28;
        ctx.beginPath();
        ctx.arc(noseX, noseY, 3.2, 0, Math.PI * 2);
        ctx.fillStyle = '#598FFF';
        ctx.fill();

        ctx.fillStyle = '#FCFCFF';
        ctx.font = '600 10px "Plus Jakarta Sans", sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('LISTENER', listenerProj.x, listenerProj.y + r + 13);
        continue;
      }

      const { source, proj: sp } = item;
      const isAudible = !source.mute && (!anySolo || source.solo);
      const baseColor = getSourceKindHexColor(source.kind);
      const isPinned = Boolean(source.customPosition);
      const draggable = isSourceDraggable(source);

      // Motion trail for dynamic 3D objects (hidden when pinned)
      if (source.kind === 'object' && !isPinned) {
        const trail = trailsRef.current.get(source.id) ?? [];
        if (trail.length > 1) {
          ctx.beginPath();
          trail.forEach((pt, idx) => {
            const tp = proj(pt);
            if (idx === 0) ctx.moveTo(tp.x, tp.y);
            else ctx.lineTo(tp.x, tp.y);
          });
          ctx.strokeStyle = isAudible ? 'rgba(245, 105, 98, 0.45)' : 'rgba(245, 105, 98, 0.14)';
          ctx.lineWidth = 2;
          ctx.stroke();
        }
      }

      // Vertical floor drop-line for elevated objects, stems, mic, & height speakers
      if (
        source.position.y > -hy + 0.5 &&
        (source.kind === 'object' || source.kind === 'height' || source.kind === 'stem' || source.kind === 'mic')
      ) {
        const dropP = proj({ x: source.position.x, y: -hy, z: source.position.z });
        ctx.strokeStyle =
          source.kind === 'object'
            ? 'rgba(245, 105, 98, 0.28)'
            : source.kind === 'mic'
              ? 'rgba(52, 211, 153, 0.34)'
              : source.kind === 'stem'
                ? 'rgba(34, 211, 238, 0.28)'
                : 'rgba(56, 189, 248, 0.2)';
        ctx.lineWidth = 1;
        ctx.setLineDash([2, 3]);
        ctx.beginPath();
        ctx.moveTo(sp.x, sp.y);
        ctx.lineTo(dropP.x, dropP.y);
        ctx.stroke();
        ctx.setLineDash([]);

        ctx.beginPath();
        ctx.arc(dropP.x, dropP.y, 2.5, 0, Math.PI * 2);
        ctx.fillStyle = baseColor;
        ctx.globalAlpha = 0.45;
        ctx.fill();
        ctx.globalAlpha = 1;
      }

      // Live acoustic energy ray to listener when playing and active
      if (isPlaying && isAudible && (source.kind === 'object' || source.kind === 'mic' || source.solo)) {
        ctx.strokeStyle = source.solo
          ? 'rgba(250, 204, 21, 0.5)'
          : source.kind === 'mic'
            ? 'rgba(52, 211, 153, 0.35)'
            : 'rgba(245, 105, 98, 0.25)';
        ctx.lineWidth = source.solo ? 1.8 : 1.1;
        ctx.beginPath();
        ctx.moveTo(sp.x, sp.y);
        ctx.lineTo(listenerProj.x, listenerProj.y);
        ctx.stroke();
      }

      const radius = Math.max(
        8,
        (source.kind === 'object' || source.kind === 'mic' ? 11.5 : source.kind === 'stem' ? 10 : 9) * sp.scale,
      );

      // RMS halo ring
      if (isPlaying && isAudible) {
        ctx.beginPath();
        ctx.arc(sp.x, sp.y, radius + 3 + source.rms * 7, 0, Math.PI * 2);
        ctx.strokeStyle = source.rms > 0.55 ? '#F59E0B' : '#34D399';
        ctx.globalAlpha = 0.5;
        ctx.lineWidth = 1.4;
        ctx.stroke();
        ctx.globalAlpha = 1;
      }

      // Custom Pin cyan dashed halo
      if (isPinned) {
        ctx.save();
        ctx.setLineDash([3, 2]);
        ctx.beginPath();
        ctx.arc(sp.x, sp.y, radius + 5.5, 0, Math.PI * 2);
        ctx.strokeStyle = '#22D3EE';
        ctx.lineWidth = 1.8;
        ctx.stroke();
        ctx.restore();
      }

      // Solo Gold halo
      if (source.solo) {
        ctx.beginPath();
        ctx.arc(sp.x, sp.y, radius + 5, 0, Math.PI * 2);
        ctx.strokeStyle = '#FACC15';
        ctx.lineWidth = 2.2;
        ctx.stroke();
      }

      // Node fill
      ctx.beginPath();
      ctx.arc(sp.x, sp.y, radius, 0, Math.PI * 2);
      ctx.fillStyle = isAudible ? baseColor : 'rgba(90, 95, 112, 0.55)';
      ctx.fill();
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.72)';
      ctx.lineWidth = 1.2;
      ctx.stroke();

      // Mute cross
      if (source.mute) {
        ctx.strokeStyle = '#F56962';
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(sp.x - 5, sp.y - 5);
        ctx.lineTo(sp.x + 5, sp.y + 5);
        ctx.moveTo(sp.x + 5, sp.y - 5);
        ctx.lineTo(sp.x - 5, sp.y + 5);
        ctx.stroke();
      }

      // Node label
      const showLabel =
        labelMode === 'all' ||
        (labelMode === 'active' &&
          (source.kind === 'object' ||
            source.kind === 'mic' ||
            source.kind === 'stem' ||
            source.solo ||
            isPinned ||
            source.kind === 'bed' ||
            source.kind === 'lfe')) ||
        (labelMode === 'minimal' && (source.kind === 'object' || source.kind === 'mic' || source.solo || isPinned));

      if (showLabel) {
        if (source.kind === 'object' && !source.mute) {
          ctx.fillStyle = '#080A10';
          ctx.font = '700 10.5px "JetBrains Mono", monospace';
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(source.shortLabel, sp.x, sp.y + 0.5);
          ctx.textBaseline = 'alphabetic';
        } else {
          ctx.font = '600 9.5px "JetBrains Mono", monospace';
          const textW = ctx.measureText(source.shortLabel).width;
          const padX = 4;
          const boxW = textW + padX * 2;
          const boxH = 14;
          const labelY = sp.y - radius - 7;
          ctx.fillStyle = 'rgba(9, 11, 18, 0.8)';
          ctx.beginPath();
          ctx.roundRect(sp.x - boxW / 2, labelY - 10, boxW, boxH, 4);
          ctx.fill();
          ctx.fillStyle = isAudible ? '#FCFCFF' : '#99999E';
          ctx.textAlign = 'center';
          ctx.fillText(source.shortLabel, sp.x, labelY);
        }
      }

      // 6DoF Δt propagation delay badge when X-Ray is active or node is pinned/soloed
      if (
        propagationDelay6DoF &&
        source.kind !== 'lfe' &&
        isAudible &&
        (isPinned || source.solo || (isXRayEnabled && (source.kind === 'object' || source.kind === 'mic')))
      ) {
        const delayMs = computePropagationDelayMs(distance3D(listenerPosition, source.position), false);
        const dtText = `${delayMs.toFixed(1)}ms`;
        ctx.font = '600 8.5px "JetBrains Mono", monospace';
        const tw = ctx.measureText(dtText).width + 6;
        const dyPos = sp.y + radius + 11;
        ctx.fillStyle = 'rgba(9, 11, 18, 0.82)';
        ctx.beginPath();
        ctx.roundRect(sp.x - tw / 2, dyPos - 9, tw, 12, 3);
        ctx.fill();
        ctx.fillStyle = '#67E8F9';
        ctx.textAlign = 'center';
        ctx.fillText(dtText, sp.x, dyPos);
      }

      newHits.push({
        id: source.id,
        x: sp.x,
        y: sp.y,
        radius: radius + 7,
        draggable,
        pinned: isPinned,
      });
    }

    hitTargetsRef.current = newHits;
  }, [
    sources,
    roomSize,
    listenerPosition,
    listenerYawDeg,
    listenerPitchDeg,
    isPlaying,
    isLocked,
    isXRayEnabled,
    xrayBandMode,
    propagationDelay6DoF,
    camYaw,
    camPitch,
    zoom,
    labelMode,
    resizeTick,
  ]);

  const handlePointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;

    const distToListener = Math.hypot(
      x - listenerScreenRef.current.x,
      y - listenerScreenRef.current.y,
    );

    if (!isLocked && distToListener < 26) {
      dragStateRef.current = {
        mode: 'listener',
        sourceId: null,
        startX: x,
        startY: y,
        lastX: x,
        lastY: y,
        moved: false,
      };
      e.currentTarget.setPointerCapture(e.pointerId);
      return;
    }

    // Check draggable 3D sources (Objects, Neural Stems, Live Mic) top-most first
    if (!isLocked) {
      for (let i = hitTargetsRef.current.length - 1; i >= 0; i -= 1) {
        const hit = hitTargetsRef.current[i];
        if (hit.draggable && Math.hypot(x - hit.x, y - hit.y) <= hit.radius) {
          dragStateRef.current = {
            mode: 'source',
            sourceId: hit.id,
            startX: x,
            startY: y,
            lastX: x,
            lastY: y,
            moved: false,
          };
          e.currentTarget.setPointerCapture(e.pointerId);
          return;
        }
      }
    }

    dragStateRef.current = {
      mode: 'orbit',
      sourceId: null,
      startX: x,
      startY: y,
      lastX: x,
      lastY: y,
      moved: false,
    };
    e.currentTarget.setPointerCapture(e.pointerId);
  };

  const handlePointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragStateRef.current;
    if (drag.mode === 'none') return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const dx = x - drag.lastX;
    const dy = y - drag.lastY;
    if (Math.hypot(x - drag.startX, y - drag.startY) > 5) {
      drag.moved = true;
    }
    drag.lastX = x;
    drag.lastY = y;

    if (drag.mode === 'orbit') {
      setCamYaw((prev) => prev + dx * 0.45);
      setCamPitch((prev) => clamp(prev + dy * 0.35, -10, 86));
    } else if (drag.mode === 'listener' && !isLocked) {
      const yawRad = (camYaw * Math.PI) / 180;
      const scale = 0.028 / Math.max(0.5, zoom);
      const worldDx = (dx * Math.cos(yawRad) - dy * Math.sin(yawRad)) * scale;
      const worldDz = (-dx * Math.sin(yawRad) - dy * Math.cos(yawRad)) * scale;
      onListenerPositionChange(
        clampToRoom(
          {
            x: listenerPosition.x + worldDx,
            y: listenerPosition.y,
            z: listenerPosition.z + worldDz,
          },
          roomSize,
        ),
      );
    } else if (drag.mode === 'source' && drag.sourceId && drag.moved && !isLocked) {
      const target = sources.find((s) => s.id === drag.sourceId);
      if (!target) return;
      const yawRad = (camYaw * Math.PI) / 180;
      const scale = 0.028 / Math.max(0.5, zoom);
      const worldDx = (dx * Math.cos(yawRad) - dy * Math.sin(yawRad)) * scale;
      const worldDz = (-dx * Math.sin(yawRad) - dy * Math.cos(yawRad)) * scale;
      onMoveSource(
        drag.sourceId,
        clampToRoom(
          {
            x: target.position.x + worldDx,
            y: target.position.y,
            z: target.position.z + worldDz,
          },
          roomSize,
        ),
      );
    }
  };

  const handlePointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragStateRef.current;
    if (!drag.moved) {
      const rect = e.currentTarget.getBoundingClientRect();
      const x = e.clientX - rect.left;
      const y = e.clientY - rect.top;
      for (let i = hitTargetsRef.current.length - 1; i >= 0; i -= 1) {
        const hit = hitTargetsRef.current[i];
        if (Math.hypot(x - hit.x, y - hit.y) <= hit.radius) {
          onToggleSolo(hit.id);
          break;
        }
      }
    }
    dragStateRef.current.mode = 'none';
    dragStateRef.current.sourceId = null;
  };

  const handleDoubleClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    for (let i = hitTargetsRef.current.length - 1; i >= 0; i -= 1) {
      const hit = hitTargetsRef.current[i];
      if (hit.pinned && Math.hypot(x - hit.x, y - hit.y) <= hit.radius) {
        onResetSourcePin(hit.id);
        break;
      }
    }
  };

  return (
    <div className="relative rounded-2xl overflow-hidden border border-white/10 bg-[#090B12]">
      {/* Compact camera & X-Ray toolbar */}
      <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5 bg-[#12141D] border-b border-white/8">
        <div className="flex flex-wrap items-center gap-1.5">
          {(['iso', 'top', 'front'] as CameraPreset[]).map((preset) => (
            <button
              key={preset}
              type="button"
              onClick={() => applyCameraPreset(preset)}
              className="px-2.5 py-1 text-xs font-medium rounded-md bg-white/6 hover:bg-white/12 text-[#E9E9EC] capitalize transition-colors whitespace-nowrap shrink-0"
            >
              {preset}
            </button>
          ))}
          <button
            type="button"
            onClick={() =>
              setLabelMode((prev) =>
                prev === 'active' ? 'all' : prev === 'all' ? 'minimal' : 'active',
              )
            }
            className="px-2.5 py-1 text-xs font-medium rounded-md bg-white/6 hover:bg-white/12 text-[#B7B7BB] transition-colors whitespace-nowrap shrink-0"
          >
            {t.labelsPrefix}:{' '}
            {labelMode === 'active'
              ? t.labelModeActive
              : labelMode === 'all'
                ? t.labelModeAll
                : t.labelModeMinimal}
          </button>

          <button
            type="button"
            aria-pressed={isXRayEnabled}
            data-testid="xray-toggle-btn"
            onClick={onToggleXRay}
            className={`inline-flex items-center gap-1.5 px-2.5 py-1 text-xs font-semibold rounded-md border transition-colors whitespace-nowrap shrink-0 ${
              isXRayEnabled
                ? 'bg-[#22D3EE]/18 border-[#22D3EE]/50 text-[#67E8F9]'
                : 'bg-white/6 border-white/10 text-[#B7B7BB] hover:text-white'
            }`}
          >
            <Radar className="w-3.5 h-3.5" />
            <span>{t.xrayToggleBtn}</span>
          </button>

          {isXRayEnabled && (
            <div className="inline-flex items-center gap-0.5 p-0.5 rounded-md bg-[#090B12] border border-white/10">
              {BAND_PILLS.map((bp) => (
                <button
                  key={bp.id}
                  type="button"
                  data-testid={`xray-band-${bp.id}`}
                  onClick={() => onChangeXRayBand(bp.id)}
                  className={`px-2 py-0.5 text-[11px] font-mono font-semibold rounded transition-colors ${
                    xrayBandMode === bp.id
                      ? 'bg-[#22D3EE] text-[#080A10]'
                      : 'text-[#99999E] hover:text-white'
                  }`}
                >
                  {bp.shortLabel}
                </button>
              ))}
            </div>
          )}
        </div>

        <div className="flex items-center gap-1.5">
          <button
            type="button"
            aria-label="Zoom out room"
            onClick={() => setZoom((z) => clamp(Number((z - 0.12).toFixed(2)), 0.6, 1.8))}
            className="p-1.5 rounded-md bg-white/6 hover:bg-white/12 text-[#E9E9EC]"
          >
            <ZoomOut className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            aria-label="Zoom in room"
            onClick={() => setZoom((z) => clamp(Number((z + 0.12).toFixed(2)), 0.6, 1.8))}
            className="p-1.5 rounded-md bg-white/6 hover:bg-white/12 text-[#E9E9EC]"
          >
            <ZoomIn className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            aria-label="Reset camera view"
            onClick={() => applyCameraPreset('iso')}
            className="p-1.5 rounded-md bg-white/6 hover:bg-white/12 text-[#E9E9EC]"
          >
            <RotateCcw className="w-3.5 h-3.5" />
          </button>
          <button
            type="button"
            aria-pressed={isLocked}
            onClick={onToggleLock}
            className={`inline-flex items-center gap-1.5 px-2.5 py-1 text-xs font-medium rounded-md border transition-colors whitespace-nowrap shrink-0 ${
              isLocked
                ? 'bg-[#F59E0B]/18 border-[#F59E0B]/45 text-[#FCD34D]'
                : 'bg-white/6 border-white/10 text-[#E9E9EC] hover:bg-white/12'
            }`}
          >
            {isLocked ? <Lock className="w-3.5 h-3.5" /> : <Unlock className="w-3.5 h-3.5" />}
            <span>{isLocked ? t.lockPosition : t.unlockPosition}</span>
          </button>
        </div>
      </div>

      <canvas
        ref={canvasRef}
        data-testid="room-simulator-canvas"
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerCancel={handlePointerUp}
        onDoubleClick={handleDoubleClick}
        onWheel={(e) => {
          e.preventDefault();
          setZoom((z) => clamp(Number((z - Math.sign(e.deltaY) * 0.08).toFixed(2)), 0.6, 1.8));
        }}
        className="w-full h-[380px] sm:h-[420px] block cursor-grab active:cursor-grabbing touch-none"
      />

      {/* Compact legend strip */}
      <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-2 bg-[#10121A] border-t border-white/8 text-[11px] text-[#99999E]">
        <div className="flex flex-wrap items-center gap-3.5">
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#598FFF]" /> {t.legendBeds}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#38BDF8]" /> {t.legendHeight}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#A855F7]" /> {t.legendLfe}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#F56962]" /> {t.legendObjects}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#22D3EE]" /> {t.legendStems}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full bg-[#34D399]" /> {t.legendLiveMic}
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="w-2.5 h-2.5 rounded-full border-2 border-[#FACC15]" /> {t.legendSolo}
          </span>
        </div>
        <span className="font-mono tabular-nums text-[#B7B7BB]">
          {t.listenerLabel} X:{listenerPosition.x.toFixed(1)}m · Y:{listenerPosition.y.toFixed(1)}m · Z:{listenerPosition.z.toFixed(1)}m
        </span>
      </div>
    </div>
  );
};
