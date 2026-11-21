import React, { useMemo, useRef, useState } from 'react';
import {
  Activity,
  Compass,
  Mic,
  PinOff,
  Radar,
  RotateCcw,
  Sliders,
  Waves,
} from 'lucide-react';
import type { StringKey } from '../i18n/strings';
import type {
  CavernSettings,
  RolloffMode,
  SpatialSourceNode,
  TrackFormatVariant,
  Vec3,
} from '../types/cavern';
import type {
  ScarlettHardwareControlState,
  VocalRackState,
} from '../utils/scarlettVocalDsp';
import { clamp, clampToRoom, distance3D } from '../utils/spatialMath';
import {
  computePropagationDelayMs,
  computeSpatialXRayMetrics,
  type XRayBandMode,
} from '../utils/spatialXRay';
import { RoomSimulatorCanvas } from './RoomSimulatorCanvas';

interface RoomTabProps {
  t: Record<StringKey, string>;
  sources: SpatialSourceNode[];
  activeVariant: TrackFormatVariant;
  vocalRack: VocalRackState;
  scarlett: ScarlettHardwareControlState;
  lgsrBypassed: boolean;
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
  onMoveSource: (sourceId: string, pos: Vec3) => void;
  onResetSourcePin: (sourceId: string) => void;
  onResetAllPins: () => void;
  onToggleLgsrBypass: () => void;
  onToggleVocalBypass: () => void;
}

const POSITION_PRESETS: Array<{ id: string; labelKey: StringKey; pos: Vec3 }> = [
  { id: 'center', labelKey: 'presetCenter', pos: { x: 0, y: 0, z: 0 } },
  { id: 'front', labelKey: 'presetFront', pos: { x: 0, y: 0, z: 2.8 } },
  { id: 'back', labelKey: 'presetBack', pos: { x: 0, y: 0, z: -2.8 } },
  { id: 'left', labelKey: 'presetLeft', pos: { x: -2.8, y: 0, z: 0 } },
  { id: 'right', labelKey: 'presetRight', pos: { x: 2.8, y: 0, z: 0 } },
  { id: 'high', labelKey: 'presetHigh', pos: { x: 0, y: 2.2, z: 0 } },
];

export const RoomTab: React.FC<RoomTabProps> = ({
  t,
  sources,
  activeVariant,
  vocalRack,
  scarlett,
  lgsrBypassed,
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
  onMoveSource,
  onResetSourcePin,
  onResetAllPins,
  onToggleLgsrBypass,
  onToggleVocalBypass,
}) => {
  const [isLocked, setIsLocked] = useState(false);
  const [isXRayEnabled, setIsXRayEnabled] = useState(true);
  const [xrayBandMode, setXrayBandMode] = useState<XRayBandMode>('all');
  const [mixerFilter, setMixerFilter] = useState<'all' | 'object' | 'stems' | 'height' | 'bed'>('all');
  const gimbalDragRef = useRef<{ active: boolean; lastX: number; lastY: number }>({
    active: false,
    lastX: 0,
    lastY: 0,
  });

  const filteredSources = useMemo(() => {
    if (mixerFilter === 'all') return sources;
    if (mixerFilter === 'bed') return sources.filter((s) => s.kind === 'bed' || s.kind === 'lfe');
    if (mixerFilter === 'stems') return sources.filter((s) => s.kind === 'stem' || s.kind === 'mic');
    return sources.filter((s) => s.kind === mixerFilter);
  }, [sources, mixerFilter]);

  const soloCount = useMemo(() => sources.filter((s) => s.solo).length, [sources]);
  const muteCount = useMemo(() => sources.filter((s) => s.mute).length, [sources]);
  const pinnedCount = useMemo(() => sources.filter((s) => Boolean(s.customPosition)).length, [sources]);

  const xrayMetrics = useMemo(
    () =>
      computeSpatialXRayMetrics(
        sources,
        listenerPosition,
        activeVariant,
        settings,
        vocalRack,
        scarlett,
        lgsrBypassed,
        isPlaying,
        xrayBandMode,
      ),
    [
      sources,
      listenerPosition,
      activeVariant,
      settings,
      vocalRack,
      scarlett,
      lgsrBypassed,
      isPlaying,
      xrayBandMode,
    ],
  );

  return (
    <section aria-label={t.tabRoom} className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h2 className="text-xl font-bold text-white">{t.roomTitle}</h2>
          <p className="text-sm text-[#99999E] mt-0.5">{t.roomSubtitle}</p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {pinnedCount > 0 && (
            <button
              type="button"
              onClick={onResetAllPins}
              className="inline-flex items-center gap-1.5 px-3 py-2 text-xs font-medium rounded-lg bg-[#22D3EE]/15 hover:bg-[#22D3EE]/25 text-[#67E8F9] border border-[#22D3EE]/35 transition-colors whitespace-nowrap shrink-0"
            >
              <PinOff className="w-3.5 h-3.5" />
              <span>
                {t.resetObjectPins} ({pinnedCount})
              </span>
            </button>
          )}

          <button
            type="button"
            onClick={onResetRoom}
            className="inline-flex items-center gap-2 px-3.5 py-2 text-xs font-medium rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-[#E9E9EC] border border-white/10 transition-colors whitespace-nowrap shrink-0"
          >
            <RotateCcw className="w-3.5 h-3.5" />
            <span>{t.resetRoom}</span>
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6 items-start">
        {/* Left 8 cols: 3D Volumetric Stage + Head Gimbal & Sweet-Spot + Studio Spatial X-Ray Inspector */}
        <div className="xl:col-span-8 space-y-4">
          <RoomSimulatorCanvas
            t={t}
            sources={sources}
            roomSize={settings.roomSize}
            listenerPosition={listenerPosition}
            listenerYawDeg={listenerYawDeg}
            listenerPitchDeg={listenerPitchDeg}
            isPlaying={isPlaying}
            isLocked={isLocked}
            isXRayEnabled={isXRayEnabled}
            xrayBandMode={xrayBandMode}
            propagationDelay6DoF={settings.propagationDelay6DoF}
            onToggleLock={() => setIsLocked((v) => !v)}
            onToggleXRay={() => setIsXRayEnabled((v) => !v)}
            onChangeXRayBand={setXrayBandMode}
            onListenerPositionChange={onListenerPositionChange}
            onMoveSource={onMoveSource}
            onResetSourcePin={onResetSourcePin}
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
                  {t.centerBtn}
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
                  <div className="absolute inset-x-3 top-1/2 h-px bg-white/15" />
                  <div className="absolute inset-y-3 left-1/2 w-px bg-white/15" />
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
                  <h3 className="text-sm font-semibold text-white">{t.sweetSpotTitle}</h3>
                </div>
                <div className="flex flex-wrap gap-1.5">
                  {POSITION_PRESETS.map((preset) => (
                    <button
                      key={preset.id}
                      type="button"
                      data-testid={`preset-${preset.id}`}
                      disabled={isLocked}
                      onClick={() => onListenerPositionChange(clampToRoom(preset.pos, settings.roomSize))}
                      className="px-2.5 py-1 text-xs font-medium rounded-md bg-[#1E1F27] hover:bg-[#387AFF]/25 disabled:opacity-40 text-[#E9E9EC] border border-white/8 transition-colors whitespace-nowrap shrink-0"
                    >
                      {t[preset.labelKey]}
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
                <div className="flex flex-wrap items-center gap-4">
                  <label className="inline-flex items-center gap-2 text-xs text-[#E9E9EC] cursor-pointer">
                    <input
                      type="checkbox"
                      checked={settings.distanceSimulation}
                      onChange={(e) => onUpdateSettings({ distanceSimulation: e.target.checked })}
                      className="rounded accent-[#387AFF]"
                    />
                    <span>{t.distanceCuesHrtf}</span>
                  </label>

                  <label className="inline-flex items-center gap-2 text-xs text-[#67E8F9] cursor-pointer">
                    <input
                      type="checkbox"
                      data-testid="delay-6dof-toggle"
                      checked={settings.propagationDelay6DoF}
                      onChange={(e) => onUpdateSettings({ propagationDelay6DoF: e.target.checked })}
                      className="rounded accent-[#22D3EE]"
                    />
                    <span>{t.propagationDelay6DoF}</span>
                  </label>
                </div>

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
                    <option value="disabled">{t.rolloffDisabled}</option>
                    <option value="logarithmic">{t.rolloffLog}</option>
                    <option value="linear">{t.rolloffLinear}</option>
                    <option value="real">{t.rolloffReal}</option>
                  </select>
                </div>
              </div>
            </div>
          </div>

          {/* W13 Studio Spatial X-Ray Inspector Panel */}
          <div
            data-testid="xray-inspector-panel"
            className="bg-[#15161C] border border-white/10 rounded-2xl p-4 space-y-4"
          >
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <div className="flex items-center gap-2">
                  <Radar className="w-4 h-4 text-[#22D3EE]" />
                  <h3 className="text-sm font-semibold text-white">{t.xrayInspectorTitle}</h3>
                </div>
                <p className="text-xs text-[#99999E] mt-0.5">{t.xrayInspectorSubtitle}</p>
              </div>

              <div className="flex flex-wrap items-center gap-2">
                <div className="inline-flex items-center gap-1 p-0.5 rounded-lg bg-[#0C0E14] border border-white/8">
                  {(
                    [
                      ['all', t.xrayBandAll],
                      ['low', t.xrayBandLow],
                      ['mid', t.xrayBandMid],
                      ['high', t.xrayBandHigh],
                    ] as const
                  ).map(([band, label]) => (
                    <button
                      key={band}
                      type="button"
                      onClick={() => setXrayBandMode(band)}
                      className={`px-2 py-1 text-[11px] font-medium rounded-md transition-colors whitespace-nowrap ${
                        xrayBandMode === band
                          ? 'bg-[#22D3EE] text-[#080A10] font-semibold'
                          : 'text-[#B7B7BB] hover:text-white'
                      }`}
                    >
                      {label}
                    </button>
                  ))}
                </div>

                <button
                  type="button"
                  data-testid="lgsr-ab-btn"
                  aria-pressed={lgsrBypassed}
                  onClick={onToggleLgsrBypass}
                  className={`px-2.5 py-1.5 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap ${
                    lgsrBypassed
                      ? 'bg-[#F59E0B]/18 border-[#F59E0B]/50 text-[#FCD34D]'
                      : 'bg-[#1E1F27] border-white/12 text-[#E9E9EC] hover:bg-[#2A2C39]'
                  }`}
                >
                  {t.xrayLgsrAbBtn}: {lgsrBypassed ? t.xrayBypassedTag : t.xrayActiveTag}
                </button>

                <button
                  type="button"
                  data-testid="vocal-ab-btn"
                  aria-pressed={vocalRack.bypass}
                  onClick={onToggleVocalBypass}
                  className={`px-2.5 py-1.5 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap ${
                    vocalRack.bypass
                      ? 'bg-[#F59E0B]/18 border-[#F59E0B]/50 text-[#FCD34D]'
                      : 'bg-[#1E1F27] border-white/12 text-[#E9E9EC] hover:bg-[#2A2C39]'
                  }`}
                >
                  {t.xrayVocalAbBtn}: {vocalRack.bypass ? t.xrayBypassedTag : t.xrayActiveTag}
                </button>
              </div>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
              {/* Tile 1: Soundfield & ITU-R BS.1770-5 */}
              <div className="rounded-xl bg-[#0C0E14] border border-white/8 p-3.5 space-y-2.5">
                <div className="flex items-center gap-1.5 text-xs font-semibold text-[#89B0FF] whitespace-nowrap">
                  <Activity className="w-3.5 h-3.5 shrink-0" />
                  <span className="truncate">{t.xraySoundfieldTitle}</span>
                </div>
                <div className="space-y-1.5 text-[11px]">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] truncate">{t.xrayCoherenceLabel} / {t.xrayDiffusenessLabel}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.coherencePct}% / {xrayMetrics.diffusenessPct}%
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] truncate">{t.xrayMomentaryLufs}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.momentaryLufs.toFixed(1)} / {xrayMetrics.integratedLufs.toFixed(1)} LUFS
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] truncate">{t.xrayTruePeakLimiter}</span>
                    <span className="font-mono tabular-nums text-[#34D399] whitespace-nowrap shrink-0">
                      {xrayMetrics.truePeakDbtp.toFixed(1)} dBTP · {xrayMetrics.limiterGrDb > 0.05 ? `-${xrayMetrics.limiterGrDb.toFixed(1)}` : '0.0'} dB
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2 pt-1.5 border-t border-white/6">
                    <span className="text-[#99999E] whitespace-nowrap">6DoF Δt</span>
                    <span
                      data-testid="xray-delay-readout"
                      className="font-mono tabular-nums text-[#67E8F9] whitespace-nowrap shrink-0"
                    >
                      {xrayMetrics.avgPropagationDelayMs.toFixed(1)} / {xrayMetrics.maxPropagationDelayMs.toFixed(1)} ms
                    </span>
                  </div>
                </div>
              </div>

              {/* Tile 2: Lossy vs Lossless X-Ray (LGSR) */}
              <div className="rounded-xl bg-[#0C0E14] border border-white/8 p-3.5 space-y-2.5">
                <div className="flex items-center justify-between gap-2">
                  <div className="flex items-center gap-1.5 text-xs font-semibold text-[#22D3EE] min-w-0">
                    <Waves className="w-3.5 h-3.5 shrink-0" />
                    <span className="truncate">{t.xrayLgsrTitle}</span>
                  </div>
                  <span className="text-[10px] font-mono text-[#99999E] whitespace-nowrap shrink-0">
                    {activeVariant.label}
                  </span>
                </div>
                <div className="space-y-1.5 text-[11px]">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayCouplingSpx}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.isLossySource
                        ? `${(xrayMetrics.couplingStartHz / 1000).toFixed(1)}k / ${(xrayMetrics.spxStartHz / 1000).toFixed(1)}k Hz`
                        : t.xrayLosslessNative}
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayGccDrift}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.gccPhatOffsetSamples.toFixed(1)} smp · {xrayMetrics.theilSenDriftPpm.toFixed(1)} ppm
                    </span>
                  </div>
                  <div className="space-y-1.5 pt-1 border-t border-white/6">
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-[#99999E] truncate">{t.xrayFlacFusion}</span>
                      <span
                        data-testid="xray-lgsr-fusion"
                        className="font-mono tabular-nums text-[#67E8F9] whitespace-nowrap shrink-0"
                      >
                        {xrayMetrics.flacFusionPct}%
                      </span>
                    </div>
                    <div className="h-1.5 w-full rounded-full bg-white/8 overflow-hidden">
                      <div
                        className="h-full rounded-full bg-[#22D3EE] transition-all duration-150"
                        style={{ width: `${xrayMetrics.flacFusionPct}%` }}
                      />
                    </div>
                  </div>
                </div>
              </div>

              {/* Tile 3: Live Vocal Chain X-Ray */}
              <div className="rounded-xl bg-[#0C0E14] border border-white/8 p-3.5 space-y-2.5">
                <div className="flex items-center justify-between gap-2">
                  <div className="flex items-center gap-1.5 text-xs font-semibold text-[#34D399] min-w-0">
                    <Mic className="w-3.5 h-3.5 shrink-0" />
                    <span className="truncate">{t.xrayVocalTitle}</span>
                  </div>
                  <span
                    data-testid="xray-vocal-status"
                    className={`text-[10px] font-mono whitespace-nowrap shrink-0 ${
                      xrayMetrics.vocalBypassed ? 'text-[#FCD34D]' : 'text-[#34D399]'
                    }`}
                  >
                    {xrayMetrics.vocalBypassed ? t.xrayBypassedTag : t.xrayActiveTag}
                  </span>
                </div>
                <div className="space-y-1.5 text-[11px]">
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayVocalInOut}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.vocalInputDbfs.toFixed(1)} / {xrayMetrics.vocalOutputDbfs.toFixed(1)} dBFS
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayVocalGateDeEss}</span>
                    <span className="font-mono tabular-nums text-white whitespace-nowrap shrink-0">
                      {xrayMetrics.vocalGateGrDb > 0.05 ? `-${xrayMetrics.vocalGateGrDb.toFixed(1)}` : '0.0'} / {xrayMetrics.vocalDeEsserGrDb > 0.05 ? `-${xrayMetrics.vocalDeEsserGrDb.toFixed(1)}` : '0.0'} dB
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayVocalCompDuck}</span>
                    <span className="font-mono tabular-nums text-[#F59E0B] whitespace-nowrap shrink-0">
                      {xrayMetrics.vocalCompGrDb > 0.05 ? `-${xrayMetrics.vocalCompGrDb.toFixed(1)}` : '0.0'} / {xrayMetrics.vocalSidechainDuckDb > 0.05 ? `-${xrayMetrics.vocalSidechainDuckDb.toFixed(1)}` : '0.0'} dB
                    </span>
                  </div>
                  <div className="flex items-center justify-between gap-2 pt-1.5 border-t border-white/6">
                    <span className="text-[#99999E] whitespace-nowrap">{t.xrayVocalRtt}</span>
                    <span className="font-mono tabular-nums text-[#34D399] whitespace-nowrap shrink-0">
                      {xrayMetrics.vocalRttMs.toFixed(1)} ms
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        {/* Right 4 cols: Live Channels, 3D Objects, Stems & Live Mic Mixer */}
        <div className="xl:col-span-4 bg-[#15161C] border border-white/10 rounded-2xl p-4 space-y-4">
          <div className="flex items-center justify-between gap-2">
            <div>
              <h3 className="text-sm font-semibold text-white">{t.mixerTitle}</h3>
              <p className="text-xs text-[#99999E] font-mono tabular-nums mt-0.5">
                {t.mixerSummary
                  .replace('{total}', String(sources.length))
                  .replace('{solo}', String(soloCount))
                  .replace('{muted}', String(muteCount))}
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
                ['stems', t.filterStemsMicOnly],
                ['height', t.filterHeightOnly],
                ['bed', t.filterBedsOnly],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                data-testid={`mixer-filter-${key}`}
                onClick={() => setMixerFilter(key)}
                className={`flex-1 px-2 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap shrink-0 ${
                  mixerFilter === key
                    ? 'bg-[#387AFF] text-white'
                    : 'text-[#B7B7BB] hover:text-white'
                }`}
              >
                {label}
              </button>
            ))}
          </div>

          <div className="space-y-1.5 max-h-[620px] overflow-y-auto pr-1">
            {filteredSources.map((src) => {
              const barPct = Math.round(clamp(src.rms * 100, 4, 100));
              const badgeColor =
                src.kind === 'object'
                  ? 'text-[#F56962]'
                  : src.kind === 'lfe'
                    ? 'text-[#C084FC]'
                    : src.kind === 'height'
                      ? 'text-[#38BDF8]'
                      : src.kind === 'stem'
                        ? 'text-[#22D3EE]'
                        : src.kind === 'mic'
                          ? 'text-[#34D399]'
                          : 'text-[#89B0FF]';
              const delayMs = settings.propagationDelay6DoF
                ? computePropagationDelayMs(distance3D(listenerPosition, src.position), src.kind === 'lfe')
                : 0;
              const isPinned = Boolean(src.customPosition);

              return (
                <div
                  key={src.id}
                  className="flex items-center justify-between gap-3 px-3 py-2 rounded-xl bg-[#1C1E27] border border-white/6"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center justify-between gap-2">
                      <div className="flex items-center gap-2 min-w-0">
                        <span className={`font-mono text-xs font-semibold shrink-0 ${badgeColor}`}>
                          {src.shortLabel}
                        </span>
                        <span className="text-xs font-medium text-[#E9E9EC] truncate">
                          {src.name}
                        </span>
                      </div>
                      <div className="flex items-center gap-1.5 shrink-0">
                        {isPinned && (
                          <button
                            type="button"
                            onClick={() => onResetSourcePin(src.id)}
                            title={t.resetObjectPins}
                            className="px-1.5 py-0.5 text-[10px] font-mono font-semibold rounded bg-[#22D3EE]/18 text-[#67E8F9] border border-[#22D3EE]/40 hover:bg-[#22D3EE]/30"
                          >
                            {t.pinnedBadge}
                          </button>
                        )}
                        <span className="font-mono text-[10px] tabular-nums text-[#99999E]">
                          Δt {delayMs.toFixed(1)}ms
                        </span>
                      </div>
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
