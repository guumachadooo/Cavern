import React, { useMemo, useRef, useState } from 'react';
import { Compass, RotateCcw, Sliders } from 'lucide-react';
import type { StringKey } from '../i18n/strings';
import type {
  CavernSettings,
  RolloffMode,
  SpatialSourceNode,
  Vec3,
} from '../types/cavern';
import { clamp, clampToRoom } from '../utils/spatialMath';
import { RoomSimulatorCanvas } from './RoomSimulatorCanvas';

interface RoomTabProps {
  t: Record<StringKey, string>;
  sources: SpatialSourceNode[];
  listenerPosition: Vec3;
  listenerYawDeg: number;
  listenerPitchDeg: number;
  lfeBoostDb: number;
  isPlaying: boolean;
  settings: CavernSettings;
  onListenerPositionChange: (pos: Vec3) => void;
  onListenerOrientationChange: (yawDeg: number, pitchDeg: number) => void;
  onResetRoom: () => void;
  onLfeBoostChange: (db: number) => void;
  onUpdateSettings: (patch: Partial<CavernSettings>) => void;
  onToggleSolo: (sourceId: string) => void;
  onToggleMute: (sourceId: string) => void;
  onResetSoloMute: () => void;
}

const POSITION_PRESETS: Array<{ id: string; label: string; pos: Vec3 }> = [
  { id: 'center', label: 'Center', pos: { x: 0, y: 0, z: 0 } },
  { id: 'front', label: 'Front', pos: { x: 0, y: 0, z: 2.8 } },
  { id: 'back', label: 'Back', pos: { x: 0, y: 0, z: -2.8 } },
  { id: 'left', label: 'Left', pos: { x: -2.8, y: 0, z: 0 } },
  { id: 'right', label: 'Right', pos: { x: 2.8, y: 0, z: 0 } },
  { id: 'high', label: 'High', pos: { x: 0, y: 2.2, z: 0 } },
];

export const RoomTab: React.FC<RoomTabProps> = ({
  t,
  sources,
  listenerPosition,
  listenerYawDeg,
  listenerPitchDeg,
  lfeBoostDb,
  isPlaying,
  settings,
  onListenerPositionChange,
  onListenerOrientationChange,
  onResetRoom,
  onLfeBoostChange,
  onUpdateSettings,
  onToggleSolo,
  onToggleMute,
  onResetSoloMute,
}) => {
  const [isLocked, setIsLocked] = useState(false);
  const [mixerFilter, setMixerFilter] = useState<'all' | 'object' | 'height' | 'bed'>('all');
  const gimbalDragRef = useRef<{ active: boolean; lastX: number; lastY: number }>({
    active: false,
    lastX: 0,
    lastY: 0,
  });

  const filteredSources = useMemo(() => {
    if (mixerFilter === 'all') return sources;
    if (mixerFilter === 'bed') return sources.filter((s) => s.kind === 'bed' || s.kind === 'lfe');
    return sources.filter((s) => s.kind === mixerFilter);
  }, [sources, mixerFilter]);

  const soloCount = useMemo(() => sources.filter((s) => s.solo).length, [sources]);
  const muteCount = useMemo(() => sources.filter((s) => s.mute).length, [sources]);

  return (
    <section aria-label={t.tabRoom} className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-xl font-bold text-white">{t.roomTitle}</h2>
          <p className="text-sm text-[#99999E] mt-0.5">{t.roomSubtitle}</p>
        </div>

        <button
          type="button"
          onClick={onResetRoom}
          className="inline-flex items-center gap-2 px-3.5 py-2 text-xs font-medium rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap shrink-0"
        >
          <RotateCcw className="w-3.5 h-3.5" />
          <span>{t.resetRoom}</span>
        </button>
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* Left 8 cols: 3D Volumetric Stage + Dedicated Head Gimbal & Height Dock */}
        <div className="xl:col-span-8 space-y-4">
          <RoomSimulatorCanvas
            sources={sources}
            roomSize={settings.roomSize}
            listenerPosition={listenerPosition}
            listenerYawDeg={listenerYawDeg}
            listenerPitchDeg={listenerPitchDeg}
            isPlaying={isPlaying}
            isLocked={isLocked}
            onToggleLock={() => setIsLocked((v) => !v)}
            onListenerPositionChange={onListenerPositionChange}
            onToggleSolo={onToggleSolo}
          />

          {/* Dedicated One UI 9 Head Trackball & Height Elevation Control Dock */}
          <div className="grid grid-cols-1 md:grid-cols-12 gap-4">
            {/* 3D Head Gimbal Trackball */}
            <div className="md:col-span-5 bg-[#15161C] border border-white/10 rounded-2xl p-4 flex flex-col justify-between gap-3">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Compass className="w-4 h-4 text-[#598FFF]" />
                  <h3 className="text-sm font-semibold text-white">{t.headTrackball}</h3>
                </div>
                <button
                  type="button"
                  onClick={() => onListenerOrientationChange(0, 0)}
                  className="text-xs text-[#89B0FF] hover:text-white"
                >
                  Center
                </button>
              </div>

              <div className="flex items-center gap-4">
                <div
                  role="application"
                  aria-label="Drag to rotate listener head yaw and pitch"
                  onPointerDown={(e) => {
                    gimbalDragRef.current = { active: true, lastX: e.clientX, lastY: e.clientY };
                    e.currentTarget.setPointerCapture(e.pointerId);
                  }}
                  onPointerMove={(e) => {
                    if (!gimbalDragRef.current.active) return;
                    const dx = e.clientX - gimbalDragRef.current.lastX;
                    const dy = e.clientY - gimbalDragRef.current.lastY;
                    gimbalDragRef.current.lastX = e.clientX;
                    gimbalDragRef.current.lastY = e.clientY;
                    const nextYaw = ((listenerYawDeg + dx * 1.1 + 540) % 360) - 180;
                    const nextPitch = clamp(listenerPitchDeg - dy * 0.9, -75, 75);
                    onListenerOrientationChange(Math.round(nextYaw), Math.round(nextPitch));
                  }}
                  onPointerUp={() => {
                    gimbalDragRef.current.active = false;
                  }}
                  onDoubleClick={() => onListenerOrientationChange(0, 0)}
                  className="w-24 h-24 rounded-full bg-[#0C0E14] border border-white/15 relative shrink-0 cursor-grab active:cursor-grabbing select-none touch-none"
                >
                  {/* Horizon crosshairs */}
                  <div className="absolute inset-x-3 top-1/2 h-px bg-white/15" />
                  <div className="absolute inset-y-3 left-1/2 w-px bg-white/15" />
                  {/* Gimbal dot reflecting Yaw & Pitch */}
                  <div
                    className="w-4 h-4 rounded-full bg-[#387AFF] border-2 border-white absolute -translate-x-1/2 -translate-y-1/2 pointer-events-none"
                    style={{
                      left: `${50 + clamp(listenerYawDeg / 180, -1, 1) * 34}%`,
                      top: `${50 - clamp(listenerPitchDeg / 75, -1, 1) * 34}%`,
                    }}
                  />
                </div>

                <div className="flex-1 space-y-2.5">
                  <div>
                    <div className="flex justify-between text-xs text-[#99999E] mb-1">
                      <span>Yaw</span>
                      <span className="font-mono tabular-nums text-white">{listenerYawDeg}°</span>
                    </div>
                    <input
                      type="range"
                      aria-label="Head Yaw"
                      min={-180}
                      max={180}
                      value={listenerYawDeg}
                      onChange={(e) => onListenerOrientationChange(Number(e.target.value), listenerPitchDeg)}
                      className="cavern-slider w-full"
                    />
                  </div>
                  <div>
                    <div className="flex justify-between text-xs text-[#99999E] mb-1">
                      <span>Pitch</span>
                      <span className="font-mono tabular-nums text-white">{listenerPitchDeg}°</span>
                    </div>
                    <input
                      type="range"
                      aria-label="Head Pitch"
                      min={-75}
                      max={75}
                      value={listenerPitchDeg}
                      onChange={(e) => onListenerOrientationChange(listenerYawDeg, Number(e.target.value))}
                      className="cavern-slider w-full"
                    />
                  </div>
                </div>
              </div>
            </div>

            {/* Listener Presets + Elevation Y + Acoustic Controls */}
            <div className="md:col-span-7 bg-[#15161C] border border-white/10 rounded-2xl p-4 space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <Sliders className="w-4 h-4 text-[#598FFF]" />
                  <h3 className="text-sm font-semibold text-white">Sweet-Spot & Acoustics</h3>
                </div>
                <div className="flex flex-wrap gap-1.5">
                  {POSITION_PRESETS.map((preset) => (
                    <button
                      key={preset.id}
                      type="button"
                      disabled={isLocked}
                      onClick={() => onListenerPositionChange(clampToRoom(preset.pos, settings.roomSize))}
                      className="px-2.5 py-1 text-xs font-medium rounded-md bg-[#1E1F27] hover:bg-[#387AFF]/25 disabled:opacity-40 text-[#E9E9EC] border border-white/8 transition-colors whitespace-nowrap shrink-0"
                    >
                      {preset.label}
                    </button>
                  ))}
                </div>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div>
                  <div className="flex justify-between text-xs text-[#99999E] mb-1">
                    <span>{t.heightElevation}</span>
                    <span className="font-mono tabular-nums text-white">
                      {listenerPosition.y.toFixed(1)} m
                    </span>
                  </div>
                  <input
                    type="range"
                    aria-label={t.heightElevation}
                    disabled={isLocked}
                    min={-3.0}
                    max={3.0}
                    step={0.1}
                    value={listenerPosition.y}
                    onChange={(e) =>
                      onListenerPositionChange(
                        clampToRoom({ ...listenerPosition, y: Number(e.target.value) }, settings.roomSize),
                      )
                    }
                    className="cavern-slider w-full"
                  />
                </div>

                <div>
                  <div className="flex justify-between text-xs text-[#99999E] mb-1">
                    <span>{t.lfeSubwoofer}</span>
                    <span className="font-mono tabular-nums text-white">
                      {lfeBoostDb >= 0 ? `+${lfeBoostDb}` : lfeBoostDb} dB
                    </span>
                  </div>
                  <input
                    type="range"
                    aria-label={t.lfeSubwoofer}
                    min={-12}
                    max={12}
                    step={1}
                    value={lfeBoostDb}
                    onChange={(e) => onLfeBoostChange(Number(e.target.value))}
                    className="cavern-slider w-full"
                  />
                </div>
              </div>

              <div className="flex flex-wrap items-center justify-between gap-3 pt-1 border-t border-white/8">
                <label className="inline-flex items-center gap-2 text-xs text-[#E9E9EC] cursor-pointer">
                  <input
                    type="checkbox"
                    checked={settings.distanceSimulation}
                    onChange={(e) => onUpdateSettings({ distanceSimulation: e.target.checked })}
                    className="rounded accent-[#387AFF]"
                  />
                  <span>Distance Cues (HRTF)</span>
                </label>

                <div className="flex items-center gap-2">
                  <label htmlFor="rolloff-select" className="text-xs text-[#99999E]">
                    {t.rolloffLabel}:
                  </label>
                  <select
                    id="rolloff-select"
                    value={settings.rolloffMode}
                    onChange={(e) => onUpdateSettings({ rolloffMode: e.target.value as RolloffMode })}
                    className="px-2.5 py-1.5 text-xs rounded-lg bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#598FFF]"
                  >
                    <option value="disabled">Disabled</option>
                    <option value="logarithmic">Logarithmic</option>
                    <option value="linear">Linear</option>
                    <option value="real">Real (1/d)</option>
                  </select>
                </div>
              </div>
            </div>
          </div>
        </div>

        {/* Right 4 cols: Live Channels & 3D Objects Mixer */}
        <div className="xl:col-span-4 bg-[#15161C] border border-white/10 rounded-2xl p-4 space-y-4">
          <div className="flex items-center justify-between gap-2">
            <div>
              <h3 className="text-sm font-semibold text-white">{t.mixerTitle}</h3>
              <p className="text-xs text-[#99999E] font-mono tabular-nums mt-0.5">
                {sources.length} sources · {soloCount} solo · {muteCount} muted
              </p>
            </div>
            {(soloCount > 0 || muteCount > 0) && (
              <button
                type="button"
                onClick={onResetSoloMute}
                className="px-2.5 py-1 text-xs font-medium rounded-md bg-[#FACC15]/15 text-[#FDE047] hover:bg-[#FACC15]/25 border border-[#FACC15]/30 whitespace-nowrap shrink-0"
              >
                {t.resetSoloMute}
              </button>
            )}
          </div>

          <div className="flex flex-wrap gap-1 p-1 rounded-lg bg-[#0C0E14] border border-white/8">
            {(
              [
                ['all', t.filterAll],
                ['object', t.filter3DOnly],
                ['height', t.filterHeightOnly],
                ['bed', t.filterBedsOnly],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                onClick={() => setMixerFilter(key)}
                className={`flex-1 px-2.5 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                  mixerFilter === key
                    ? 'bg-[#387AFF] text-white'
                    : 'text-[#B7B7BB] hover:text-white'
                }`}
              >
                {label}
              </button>
            ))}
          </div>

          <div className="space-y-1.5 max-h-[500px] overflow-y-auto pr-1">
            {filteredSources.map((src) => {
              const barPct = Math.round(clamp(src.rms * 100, 4, 100));
              const badgeColor =
                src.kind === 'object'
                  ? 'text-[#F56962]'
                  : src.kind === 'lfe'
                    ? 'text-[#C084FC]'
                    : src.kind === 'height'
                      ? 'text-[#38BDF8]'
                      : 'text-[#89B0FF]';
              return (
                <div
                  key={src.id}
                  className="flex items-center justify-between gap-3 px-3 py-2 rounded-xl bg-[#1C1E27] border border-white/6"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className={`font-mono text-xs font-semibold ${badgeColor}`}>
                        {src.shortLabel}
                      </span>
                      <span className="text-xs font-medium text-[#E9E9EC] truncate">
                        {src.name}
                      </span>
                    </div>
                    <div className="mt-1.5 h-1.5 w-full rounded-full bg-white/8 overflow-hidden">
                      <div
                        className={`h-full rounded-full transition-all duration-100 ${
                          src.mute
                            ? 'bg-white/15'
                            : barPct > 65
                              ? 'bg-[#F59E0B]'
                              : 'bg-[#34D399]'
                        }`}
                        style={{ width: `${isPlaying && !src.mute ? barPct : 6}%` }}
                      />
                    </div>
                  </div>

                  <div className="flex items-center gap-1.5 shrink-0">
                    <button
                      type="button"
                      aria-label={`Solo ${src.name}`}
                      aria-pressed={src.solo}
                      onClick={() => onToggleSolo(src.id)}
                      className={`w-7 h-7 rounded-md text-xs font-bold border transition-colors ${
                        src.solo
                          ? 'bg-[#FACC15] border-[#FDE047] text-[#080A10]'
                          : 'bg-[#12141D] border-white/12 text-[#B7B7BB] hover:text-white'
                      }`}
                    >
                      S
                    </button>
                    <button
                      type="button"
                      aria-label={`Mute ${src.name}`}
                      aria-pressed={src.mute}
                      onClick={() => onToggleMute(src.id)}
                      className={`w-7 h-7 rounded-md text-xs font-bold border transition-colors ${
                        src.mute
                          ? 'bg-[#F56962] border-[#FCA5A5] text-white'
                          : 'bg-[#12141D] border-white/12 text-[#B7B7BB] hover:text-white'
                      }`}
                    >
                      M
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </section>
  );
};
