import React, { useEffect, useRef, useState } from 'react';
import { Lock, RotateCcw, Unlock, ZoomIn, ZoomOut } from 'lucide-react';
import type { StringKey } from '../i18n/strings';
import type {
  CameraPreset,
  LabelVisibilityMode,
  SpatialSourceNode,
  Vec3,
} from '../types/cavern';
import { clamp, clampToRoom, projectRoomPoint } from '../utils/spatialMath';

interface RoomSimulatorCanvasProps {
  t: Record<StringKey, string>;
  sources: SpatialSourceNode[];
  roomSize: Vec3;
  listenerPosition: Vec3;
  listenerYawDeg: number;
  listenerPitchDeg: number;
  isPlaying: boolean;
  isLocked: boolean;
  onToggleLock: () => void;
  onListenerPositionChange: (pos: Vec3) => void;
  onToggleSolo: (sourceId: string) => void;
}

interface HitTarget {
  id: string;
  x: number;
  y: number;
  radius: number;
}

export const RoomSimulatorCanvas: React.FC<RoomSimulatorCanvasProps> = ({
  t,
  sources,
  roomSize,
  listenerPosition,
  listenerYawDeg,
  listenerPitchDeg,
  isPlaying,
  isLocked,
  onToggleLock,
  onListenerPositionChange,
  onToggleSolo,
}) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const trailsRef = useRef<Map<string, Vec3[]>>(new Map());
  const hitTargetsRef = useRef<HitTarget[]>([]);
  const listenerScreenRef = useRef<{ x: number; y: number }>({ x: 0, y: 0 });
  const dragStateRef = useRef<{
    mode: 'none' | 'orbit' | 'listener';
    startX: number;
    startY: number;
    lastX: number;
    lastY: number;
    moved: boolean;
  }>({ mode: 'none', startX: 0, startY: 0, lastX: 0, lastY: 0, moved: false });

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

    // Update trails for dynamic 3D objects
    for (const src of sources) {
      if (src.kind === 'object') {
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
    const listenerProj = proj(listenerPosition);
    listenerScreenRef.current = { x: listenerProj.x, y: listenerProj.y };

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
        // Floor drop line for listener
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

        // Nose indicator
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
      const baseColor =
        source.kind === 'object'
          ? '#F56962'
          : source.kind === 'lfe'
            ? '#A855F7'
            : source.kind === 'height'
              ? '#38BDF8'
              : '#598FFF';

      // Motion trail for dynamic 3D objects
      if (source.kind === 'object') {
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

      // Vertical floor drop-line for elevated objects & height speakers
      if (source.position.y > -hy + 0.5 && (source.kind === 'object' || source.kind === 'height')) {
        const dropP = proj({ x: source.position.x, y: -hy, z: source.position.z });
        ctx.strokeStyle =
          source.kind === 'object' ? 'rgba(245, 105, 98, 0.28)' : 'rgba(56, 189, 248, 0.2)';
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
      if (isPlaying && isAudible && (source.kind === 'object' || source.solo)) {
        ctx.strokeStyle = source.solo
          ? 'rgba(250, 204, 21, 0.5)'
          : 'rgba(245, 105, 98, 0.25)';
        ctx.lineWidth = source.solo ? 1.8 : 1.1;
        ctx.beginPath();
        ctx.moveTo(sp.x, sp.y);
        ctx.lineTo(listenerProj.x, listenerProj.y);
        ctx.stroke();
      }

      const radius = Math.max(
        8,
        (source.kind === 'object' ? 12 : 9.5) * sp.scale,
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

      // Node label: render object indices cleanly inside their coral spheres; draw subtle backing for speaker labels
      const showLabel =
        labelMode === 'all' ||
        (labelMode === 'active' &&
          (source.kind === 'object' || source.solo || source.kind === 'bed' || source.kind === 'lfe')) ||
        (labelMode === 'minimal' && (source.kind === 'object' || source.solo));

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
          ctx.fillStyle = 'rgba(9, 11, 18, 0.78)';
          ctx.beginPath();
          ctx.roundRect(sp.x - boxW / 2, labelY - 10, boxW, boxH, 4);
          ctx.fill();
          ctx.fillStyle = isAudible ? '#FCFCFF' : '#99999E';
          ctx.textAlign = 'center';
          ctx.fillText(source.shortLabel, sp.x, labelY);
        }
      }

      newHits.push({ id: source.id, x: sp.x, y: sp.y, radius: radius + 6 });
    }

    hitTargetsRef.current = newHits;
  }, [sources, roomSize, listenerPosition, listenerYawDeg, listenerPitchDeg, isPlaying, isLocked, camYaw, camPitch, zoom, labelMode, resizeTick]);

  const handlePointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;

    const distToListener = Math.hypot(
      x - listenerScreenRef.current.x,
      y - listenerScreenRef.current.y,
    );

    const mode = !isLocked && distToListener < 26 ? 'listener' : 'orbit';
    dragStateRef.current = { mode, startX: x, startY: y, lastX: x, lastY: y, moved: false };
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
  };

  return (
    <div className="relative rounded-2xl overflow-hidden border border-white/10 bg-[#090B12]">
      {/* Compact camera toolbar */}
      <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5 bg-[#12141D] border-b border-white/8">
        <div className="flex items-center gap-1.5">
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
        onWheel={(e) => {
          e.preventDefault();
          setZoom((z) => clamp(Number((z - Math.sign(e.deltaY) * 0.08).toFixed(2)), 0.6, 1.8));
        }}
        className="w-full h-[380px] sm:h-[420px] block cursor-grab active:cursor-grabbing touch-none"
      />

      {/* Compact legend strip */}
      <div className="flex flex-wrap items-center justify-between gap-3 px-4 py-2 bg-[#10121A] border-t border-white/8 text-[11px] text-[#99999E]">
        <div className="flex flex-wrap items-center gap-4">
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
