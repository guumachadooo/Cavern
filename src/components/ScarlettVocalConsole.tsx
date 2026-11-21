import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Activity,
  Link2,
  Mic,
  MicOff,
  Save,
  ShieldCheck,
  Sparkles,
  Unlink2,
  Volume2,
  VolumeX,
  Waves,
  Zap,
} from 'lucide-react';
import { LiveScarlettVocalAudioEngine } from '../audio/vocalChainAudio';
import type { StringKey } from '../i18n/strings';
import type { SpatialTrack } from '../types/cavern';
import {
  applyScarlettChannelPatch,
  computeAutoGainTargetDb,
  computeDualStageCompressorGrDb,
  computeVocalPocketDuckDb,
  evaluateClipSafeGain,
  getScarlettModelSpec,
  getTrackVocalDna,
  SCARLETT_MODELS,
  type ScarlettAirMode,
  type ScarlettDirectMonitorMode,
  type ScarlettHardwareControlState,
  type ScarlettModelId,
  type VocalRackState,
} from '../utils/scarlettVocalDsp';

interface ScarlettVocalConsoleProps {
  t: Record<StringKey, string>;
  activeTrack: SpatialTrack;
  scarlett: ScarlettHardwareControlState;
  vocalRack: VocalRackState;
  onUpdateScarlett: (next: ScarlettHardwareControlState) => void;
  onUpdateVocalRack: (patch: Partial<VocalRackState>) => void;
  onNotify: (msg: string) => void;
}

function getHaloColor(peakDbfs: number): string {
  if (peakDbfs >= -1) return '#EF4444'; // Crimson clip
  if (peakDbfs >= -6) return '#F59E0B'; // Amber hot
  if (peakDbfs >= -45) return '#22C55E'; // Focusrite green signal
  return '#3F4454';
}

function dbfsToPct(dbfs: number): number {
  const clamped = Math.max(-60, Math.min(0, dbfs));
  return Math.round(((clamped + 60) / 60) * 100);
}

export const ScarlettVocalConsole: React.FC<ScarlettVocalConsoleProps> = ({
  t,
  activeTrack,
  scarlett,
  vocalRack,
  onUpdateScarlett,
  onUpdateVocalRack,
  onNotify,
}) => {
  const audioEngineRef = useRef<LiveScarlettVocalAudioEngine | null>(null);
  if (!audioEngineRef.current) {
    audioEngineRef.current = new LiveScarlettVocalAudioEngine();
  }

  const [audioSourceMode, setAudioSourceMode] = useState<'off' | 'mic' | 'demo'>('off');
  const [calibratingVoiceprint, setCalibratingVoiceprint] = useState(false);
  const [meterTick, setMeterTick] = useState(0);

  const modelSpec = useMemo(() => getScarlettModelSpec(scarlett.modelId), [scarlett.modelId]);
  const dna = useMemo(
    () => getTrackVocalDna(activeTrack.id, activeTrack.isSpatial),
    [activeTrack.id, activeTrack.isSpatial],
  );

  // Sync Web Audio DSP whenever hardware or vocal rack parameters change
  useEffect(() => {
    audioEngineRef.current?.updateParameters(scarlett, vocalRack, dna);
  }, [scarlett, vocalRack, dna]);

  // Cleanup live Web Audio on unmount
  useEffect(() => {
    const engine = audioEngineRef.current;
    return () => {
      engine?.stop();
    };
  }, []);

  // Gentle live meter animation when audio audition is active or Auto Gain is running
  const anyAutoRunning =
    scarlett.channels[0].autoGainStatus === 'running' ||
    scarlett.channels[1].autoGainStatus === 'running';

  useEffect(() => {
    if (audioSourceMode === 'off' && !anyAutoRunning) return;
    const id = window.setInterval(() => {
      setMeterTick((n) => n + 1);
    }, 180);
    return () => window.clearInterval(id);
  }, [audioSourceMode, anyAutoRunning]);

  // Progress Gen 4 Auto Gain calibration countdown & apply computed target dB
  const scarlettRef = useRef(scarlett);
  scarlettRef.current = scarlett;

  useEffect(() => {
    if (!anyAutoRunning) return;
    const timer = window.setInterval(() => {
      const cur = scarlettRef.current;
      let changed = false;
      const nextChannels: [typeof cur.channels[0], typeof cur.channels[1]] = [
        { ...cur.channels[0] },
        { ...cur.channels[1] },
      ];

      ([0, 1] as const).forEach((idx) => {
        const ch = nextChannels[idx];
        if (ch.autoGainStatus !== 'running') return;
        changed = true;
        const nextProgress = Math.min(100, ch.autoGainProgressPct + 20);
        if (nextProgress >= 100) {
          const measuredPeak =
            idx === 0
              ? audioEngineRef.current?.readInputPeakDbfs() ?? -25 + (ch.gainDb - 34) * 0.45
              : -28 + (ch.gainDb - 22) * 0.45;
          const targetGain = computeAutoGainTargetDb(measuredPeak, ch.gainDb, -12);
          nextChannels[idx] = {
            ...ch,
            gainDb: targetGain,
            autoGainStatus: 'succeeded',
            autoGainProgressPct: 100,
            meterPeakDbfs: -12.0,
          };
          onNotify(
            t.scarlettAutoGainDoneToast
              .replace('{ch}', String(idx + 1))
              .replace('{gain}', String(targetGain)),
          );
        } else {
          const simPeak = Number((-19 + Math.sin(nextProgress * 0.12) * 4).toFixed(1));
          nextChannels[idx] = {
            ...ch,
            autoGainProgressPct: nextProgress,
            meterPeakDbfs: simPeak,
          };
        }
      });

      if (changed) {
        if (cur.inputsLinked && nextChannels[0].autoGainStatus === 'succeeded') {
          nextChannels[1] = {
            ...nextChannels[1],
            gainDb: nextChannels[0].gainDb,
            autoGainStatus: 'succeeded',
            autoGainProgressPct: 100,
          };
        }
        onUpdateScarlett({ ...cur, channels: nextChannels });
      }
    }, 320);

    return () => window.clearInterval(timer);
  }, [anyAutoRunning, onUpdateScarlett, onNotify, t.scarlettAutoGainDoneToast]);

  const handleModelChange = (modelId: ScarlettModelId) => {
    const spec = getScarlettModelSpec(modelId);
    const nextCh0 = {
      ...scarlett.channels[0],
      airMode:
        scarlett.channels[0].airMode === 'drive' && !spec.supportsAirDrive
          ? ('presence' as ScarlettAirMode)
          : scarlett.channels[0].airMode,
      autoGainStatus: 'idle' as const,
      autoGainProgressPct: 0,
    };
    const nextCh1 = {
      ...scarlett.channels[1],
      airMode:
        scarlett.channels[1].airMode === 'drive' && !spec.supportsAirDrive
          ? ('presence' as ScarlettAirMode)
          : scarlett.channels[1].airMode,
      phantom48v: spec.sharedPhantom48v ? nextCh0.phantom48v : scarlett.channels[1].phantom48v,
      autoGainStatus: 'idle' as const,
      autoGainProgressPct: 0,
    };
    onUpdateScarlett({
      ...scarlett,
      modelId,
      inputsLinked: spec.supportsStereoLink ? scarlett.inputsLinked : false,
      channels: [nextCh0, nextCh1],
    });
  };

  const handleToggleLink = () => {
    if (!modelSpec.supportsStereoLink) return;
    const nextLinked = !scarlett.inputsLinked;
    if (nextLinked) {
      const ch0 = scarlett.channels[0];
      onUpdateScarlett({
        ...scarlett,
        inputsLinked: true,
        channels: [
          ch0,
          {
            ...scarlett.channels[1],
            gainDb: ch0.gainDb,
            phantom48v: ch0.phantom48v,
            airMode: ch0.airMode,
            instrumentHiZ: ch0.instrumentHiZ,
            clipSafe: ch0.clipSafe,
          },
        ],
      });
    } else {
      onUpdateScarlett({ ...scarlett, inputsLinked: false });
    }
  };

  const handleStartAutoGain = (chIdx: 0 | 1) => {
    if (!modelSpec.supportsAutoGain) return;
    const updated = applyScarlettChannelPatch(scarlett, chIdx, {
      autoGainStatus: 'running',
      autoGainProgressPct: 12,
    });
    onUpdateScarlett(updated);
    onNotify(t.scarlettAutoGainStartedToast.replace('{ch}', scarlett.inputsLinked ? '1+2' : String(chIdx + 1)));
  };

  const handleCancelAutoGain = () => {
    onUpdateScarlett({
      ...scarlett,
      channels: [
        { ...scarlett.channels[0], autoGainStatus: 'idle', autoGainProgressPct: 0 },
        { ...scarlett.channels[1], autoGainStatus: 'idle', autoGainProgressPct: 0 },
      ],
    });
  };

  const handleTestClipSafeOverload = (chIdx: 0 | 1) => {
    const ch = scarlett.channels[chIdx];
    const peakHit = -0.2;
    const result = evaluateClipSafeGain(ch.gainDb, peakHit, ch.clipSafe && modelSpec.supportsClipSafe);
    if (result.triggered) {
      const updated = applyScarlettChannelPatch(scarlett, chIdx, {
        gainDb: result.nextGainDb,
        meterPeakDbfs: -6.0,
        clipSafeTriggeredCount: ch.clipSafeTriggeredCount + 1,
      });
      onUpdateScarlett(updated);
      onNotify(
        t.scarlettClipSafeTriggeredToast
          .replace('{ch}', String(chIdx + 1))
          .replace('{db}', String(result.reductionDb)),
      );
    } else {
      const updated = applyScarlettChannelPatch(scarlett, chIdx, {
        meterPeakDbfs: -0.2,
      });
      onUpdateScarlett(updated);
      onNotify(t.scarlettClipOverloadToast.replace('{ch}', String(chIdx + 1)));
    }
  };

  const handleToggleAudioSource = async (target: 'mic' | 'demo') => {
    if (audioSourceMode === target) {
      audioEngineRef.current?.stop();
      setAudioSourceMode('off');
      return;
    }
    if (target === 'mic') {
      const ok = await audioEngineRef.current?.startMic(scarlett, vocalRack, dna);
      if (ok) {
        setAudioSourceMode('mic');
        onNotify(t.scarlettMicLiveToast);
      } else {
        audioEngineRef.current?.startDemoVoice(scarlett, vocalRack, dna);
        setAudioSourceMode('demo');
        onNotify(t.scarlettMicFallbackToast);
      }
    } else {
      audioEngineRef.current?.startDemoVoice(scarlett, vocalRack, dna);
      setAudioSourceMode('demo');
      onNotify(t.scarlettDemoVoiceToast);
    }
  };

  const handleCalibrateVoiceprint = () => {
    setCalibratingVoiceprint(true);
    window.setTimeout(() => {
      setCalibratingVoiceprint(false);
      onUpdateVocalRack({ voiceprintCalibrated: true, enabled: true, bypass: false });
      onNotify(t.vocalVoiceprintDoneToast);
    }, 850);
  };

  // Compute live dBFS peaks & W11 rack metering
  const liveCh1Peak = useMemo(() => {
    const live = audioEngineRef.current?.readInputPeakDbfs();
    if (live !== null && live !== undefined) return live;
    if (audioSourceMode !== 'off') {
      return Number(
        Math.max(-55, Math.min(-0.4, -34 + scarlett.channels[0].gainDb * 0.52 + Math.sin(meterTick * 0.9) * 3.5)).toFixed(1),
      );
    }
    return scarlett.channels[0].meterPeakDbfs;
  }, [audioSourceMode, meterTick, scarlett.channels]);

  const liveCh2Peak = useMemo(() => {
    if (audioSourceMode !== 'off') {
      return Number(
        Math.max(-58, Math.min(-2, -40 + scarlett.channels[1].gainDb * 0.48 + Math.cos(meterTick * 0.7) * 2.8)).toFixed(1),
      );
    }
    return scarlett.channels[1].meterPeakDbfs;
  }, [audioSourceMode, meterTick, scarlett.channels]);

  const compMeters = useMemo(
    () =>
      vocalRack.enabled && !vocalRack.bypass
        ? computeDualStageCompressorGrDb(liveCh1Peak, vocalRack.dynamicsPct, vocalRack.mixMatchPct)
        : { fetGrDb: 0, optoGrDb: 0, totalGrDb: 0 },
    [liveCh1Peak, vocalRack.enabled, vocalRack.bypass, vocalRack.dynamicsPct, vocalRack.mixMatchPct],
  );

  const pocketDuckDb = useMemo(
    () =>
      vocalRack.enabled && !vocalRack.bypass
        ? computeVocalPocketDuckDb(liveCh1Peak, vocalRack.mixMatchPct, vocalRack.sidechainPocketGlue)
        : 0,
    [liveCh1Peak, vocalRack.enabled, vocalRack.bypass, vocalRack.mixMatchPct, vocalRack.sidechainPocketGlue],
  );

  const visibleChannelIndices: Array<0 | 1> = modelSpec.inputCount === 1 ? [0] : [0, 1];

  return (
    <div className="space-y-6">
      {/* SECTION 1: Focusrite Scarlett Control 2 Hardware Console */}
      <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 sm:p-6 space-y-5">
        {/* Hardware Header Bar */}
        <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-4 pb-4 border-b border-white/8">
          <div className="space-y-1">
            <div className="flex flex-wrap items-center gap-2.5">
              <h3 className="text-base sm:text-lg font-bold text-white">
                {t.scarlettConsoleTitle}
              </h3>
              <span className="text-xs font-mono tabular-nums text-[#99999E]">
                VID 0x{modelSpec.vendorId.toString(16).toUpperCase()} · PID 0x
                {modelSpec.productId.toString(16).toUpperCase()}
              </span>
            </div>
            <p className="text-xs text-[#99999E]">{t.scarlettConsoleSubtitle}</p>
          </div>

          <div className="flex flex-wrap items-center gap-2.5">
            <label htmlFor="scarlett-model-select" className="sr-only">
              {t.scarlettModelLabel}
            </label>
            <select
              id="scarlett-model-select"
              value={scarlett.modelId}
              onChange={(e) => handleModelChange(e.target.value as ScarlettModelId)}
              className="px-3 py-2 text-xs font-medium rounded-lg bg-[#0C0E14] border border-white/12 text-white focus:outline-none focus:border-[#DC2626]"
            >
              {SCARLETT_MODELS.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                </option>
              ))}
            </select>

            {modelSpec.supportsStereoLink && (
              <button
                type="button"
                aria-pressed={scarlett.inputsLinked}
                onClick={handleToggleLink}
                className={`inline-flex items-center gap-1.5 px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                  scarlett.inputsLinked
                    ? 'bg-[#DC2626]/20 border-[#DC2626] text-[#FCA5A5]'
                    : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
                }`}
              >
                {scarlett.inputsLinked ? (
                  <Link2 className="w-3.5 h-3.5" />
                ) : (
                  <Unlink2 className="w-3.5 h-3.5" />
                )}
                <span>{t.scarlettLinkBtn}</span>
              </button>
            )}

            <button
              type="button"
              onClick={() => void handleToggleAudioSource('mic')}
              className={`inline-flex items-center gap-1.5 px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                audioSourceMode === 'mic'
                  ? 'bg-[#22C55E]/20 border-[#22C55E]/60 text-[#86EFAC]'
                  : 'bg-[#1C1E27] border-white/10 text-[#E9E9EC] hover:bg-[#272A38]'
              }`}
            >
              {audioSourceMode === 'mic' ? (
                <Mic className="w-3.5 h-3.5" />
              ) : (
                <MicOff className="w-3.5 h-3.5 text-[#99999E]" />
              )}
              <span>{audioSourceMode === 'mic' ? t.scarlettStopMic : t.scarlettLiveMicBtn}</span>
            </button>

            <button
              type="button"
              onClick={() => void handleToggleAudioSource('demo')}
              className={`inline-flex items-center gap-1.5 px-3 py-2 text-xs font-medium rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                audioSourceMode === 'demo'
                  ? 'bg-[#387AFF]/20 border-[#598FFF] text-white'
                  : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
              }`}
            >
              <Waves className="w-3.5 h-3.5" />
              <span>{audioSourceMode === 'demo' ? t.scarlettStopDemo : t.scarlettDemoVoiceBtn}</span>
            </button>
          </div>
        </div>

        {/* Gen 2 Front-Panel Notice */}
        {!modelSpec.supportsSoftwareControls && (
          <div className="px-4 py-3 rounded-xl bg-[#1C1E27] border border-white/10 text-xs text-[#E9E9EC]">
            {t.scarlettGen2WebNotice}
          </div>
        )}

        {/* Input Channel Strips (Ch 1 & Ch 2) */}
        <div
          className={`grid grid-cols-1 ${
            visibleChannelIndices.length === 2 ? 'lg:grid-cols-2' : ''
          } gap-5`}
        >
          {visibleChannelIndices.map((chIdx) => {
            const ch = scarlett.channels[chIdx];
            const peakDbfs = chIdx === 0 ? liveCh1Peak : liveCh2Peak;
            const haloColor = getHaloColor(peakDbfs);
            const isRunningAuto = ch.autoGainStatus === 'running';
            const airModes: Array<{ id: ScarlettAirMode; label: string }> = modelSpec.supportsAirDrive
              ? [
                  { id: 'off', label: t.scarlettAirOff },
                  { id: 'presence', label: t.scarlettAirPresence },
                  { id: 'drive', label: t.scarlettAirDrive },
                ]
              : [
                  { id: 'off', label: t.scarlettAirOff },
                  { id: 'presence', label: t.scarlettAirPresence },
                ];

            return (
              <div
                key={chIdx}
                className="bg-[#111319] border border-white/10 rounded-xl p-4 sm:p-5 space-y-4"
              >
                {/* Strip Top: Channel Title + Gain Halo Meter */}
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <h4 className="text-sm font-bold text-white">
                      {t.scarlettInputLabel.replace('{n}', String(chIdx + 1))}
                      {scarlett.inputsLinked && modelSpec.supportsStereoLink
                        ? ` · ${t.scarlettLinkedTag}`
                        : ''}
                    </h4>
                    <p className="text-[11px] text-[#99999E] mt-0.5 font-mono tabular-nums">
                      {ch.instrumentHiZ ? 'Inst (Hi-Z 1.5 MΩ)' : 'Line / XLR Mic'} ·{' '}
                      {ch.phantom48v ? '48V ON' : '48V OFF'}
                    </p>
                  </div>

                  {/* Gain Halo Ring + Peak dBFS Readout */}
                  <div className="flex items-center gap-2.5 shrink-0">
                    <span
                      aria-hidden="true"
                      className="w-3.5 h-3.5 rounded-full border-2 transition-colors"
                      style={{
                        borderColor: haloColor,
                        backgroundColor: peakDbfs >= -45 ? `${haloColor}33` : 'transparent',
                      }}
                    />
                    <span className="text-xs font-mono tabular-nums text-white w-20 text-right">
                      {peakDbfs <= -95 ? '-∞ dBFS' : `${peakDbfs.toFixed(1)} dBFS`}
                    </span>
                  </div>
                </div>

                {/* Live Peak Meter Bar */}
                <div className="h-2 rounded-full bg-white/8 overflow-hidden">
                  <div
                    className="h-full transition-all duration-150 rounded-full"
                    style={{
                      width: `${dbfsToPct(peakDbfs)}%`,
                      backgroundColor: haloColor,
                    }}
                  />
                </div>

                {/* Digital Preamp Gain Slider (Gen 4) */}
                <div className="space-y-1.5">
                  <div className="flex items-center justify-between text-xs">
                    <span className="text-[#B7B7BB] font-medium">{t.scarlettPreampGain}</span>
                    <span className="font-mono tabular-nums text-white font-semibold">
                      {modelSpec.supportsDigitalGain ? `${ch.gainDb} dB` : t.scarlettHwKnobOnly}
                    </span>
                  </div>
                  <input
                    type="range"
                    aria-label={`${t.scarlettPreampGain} ${chIdx + 1}`}
                    min={0}
                    max={69}
                    step={1}
                    disabled={!modelSpec.supportsDigitalGain}
                    value={ch.gainDb}
                    onChange={(e) =>
                      onUpdateScarlett(
                        applyScarlettChannelPatch(scarlett, chIdx, {
                          gainDb: Number(e.target.value),
                        }),
                      )
                    }
                    className="cavern-slider w-full disabled:opacity-40"
                  />
                </div>

                {/* Air Preamp Segmented Selector (Off / Presence / Presence + Drive) */}
                <div className="space-y-1.5">
                  <div className="flex items-center justify-between text-xs">
                    <span className="text-[#B7B7BB] font-medium">{t.scarlettAirLabel}</span>
                    <span className="text-[11px] text-[#99999E]">
                      {ch.airMode === 'drive'
                        ? t.scarlettAirDriveDesc
                        : ch.airMode === 'presence'
                          ? t.scarlettAirPresenceDesc
                          : t.scarlettAirCleanDesc}
                    </span>
                  </div>
                  <div
                    role="group"
                    aria-label={`${t.scarlettAirLabel} ${chIdx + 1}`}
                    className={`grid ${
                      airModes.length === 3
                        ? 'grid-cols-[0.85fr_0.95fr_1.45fr]'
                        : 'grid-cols-2'
                    } gap-1.5 p-1 rounded-lg bg-[#080A10] border border-white/8`}
                  >
                    {airModes.map((mode) => {
                      const active = ch.airMode === mode.id;
                      return (
                        <button
                          key={mode.id}
                          type="button"
                          disabled={!modelSpec.supportsSoftwareControls}
                          aria-pressed={active}
                          onClick={() =>
                            onUpdateScarlett(
                              applyScarlettChannelPatch(scarlett, chIdx, { airMode: mode.id }),
                            )
                          }
                          className={`px-1.5 sm:px-2 py-1.5 text-[11px] sm:text-xs font-semibold rounded-md transition-colors whitespace-nowrap disabled:opacity-40 ${
                            active
                              ? mode.id === 'off'
                                ? 'bg-white/14 text-white'
                                : 'bg-[#F59E0B] text-[#080A10]'
                              : 'text-[#99999E] hover:text-white'
                          }`}
                        >
                          {mode.label}
                        </button>
                      );
                    })}
                  </div>
                </div>

                {/* Primary Hardware Control Row: 48V | Inst | Auto Gain | Clip Safe */}
                <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 pt-1">
                  <button
                    type="button"
                    disabled={!modelSpec.supportsSoftwareControls}
                    aria-pressed={ch.phantom48v}
                    onClick={() =>
                      onUpdateScarlett(
                        applyScarlettChannelPatch(scarlett, chIdx, {
                          phantom48v: !ch.phantom48v,
                        }),
                      )
                    }
                    className={`px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap disabled:opacity-40 ${
                      ch.phantom48v
                        ? 'bg-[#DC2626] border-[#EF4444] text-white'
                        : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
                    }`}
                  >
                    48V
                  </button>

                  <button
                    type="button"
                    disabled={!modelSpec.supportsSoftwareControls}
                    aria-pressed={ch.instrumentHiZ}
                    onClick={() =>
                      onUpdateScarlett(
                        applyScarlettChannelPatch(scarlett, chIdx, {
                          instrumentHiZ: !ch.instrumentHiZ,
                        }),
                      )
                    }
                    className={`px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap disabled:opacity-40 ${
                      ch.instrumentHiZ
                        ? 'bg-[#387AFF] border-[#598FFF] text-white'
                        : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
                    }`}
                  >
                    {ch.instrumentHiZ ? 'Inst' : 'Line'}
                  </button>

                  <button
                    type="button"
                    disabled={!modelSpec.supportsAutoGain}
                    aria-pressed={isRunningAuto}
                    onClick={() =>
                      isRunningAuto ? handleCancelAutoGain() : handleStartAutoGain(chIdx)
                    }
                    className={`inline-flex items-center justify-center gap-1 px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap disabled:opacity-40 ${
                      isRunningAuto
                        ? 'bg-[#22C55E] border-[#4ADE80] text-[#080A10]'
                        : 'bg-[#1C1E27] border-white/10 text-[#E9E9EC] hover:border-[#22C55E]/60'
                    }`}
                  >
                    <Zap className="w-3.5 h-3.5" />
                    <span>
                      {isRunningAuto ? `${ch.autoGainProgressPct}%` : t.scarlettAutoBtn}
                    </span>
                  </button>

                  <button
                    type="button"
                    disabled={!modelSpec.supportsClipSafe}
                    aria-pressed={ch.clipSafe}
                    onClick={() =>
                      onUpdateScarlett(
                        applyScarlettChannelPatch(scarlett, chIdx, {
                          clipSafe: !ch.clipSafe,
                        }),
                      )
                    }
                    className={`inline-flex items-center justify-center gap-1 px-3 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap disabled:opacity-40 ${
                      ch.clipSafe && modelSpec.supportsClipSafe
                        ? 'bg-[#10B981]/20 border-[#10B981] text-[#6EE7B7]'
                        : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
                    }`}
                  >
                    <ShieldCheck className="w-3.5 h-3.5" />
                    <span>{t.scarlettSafeBtn}</span>
                  </button>
                </div>

                {/* Auto Gain Status & Clip Safe Test Row */}
                {modelSpec.supportsAutoGain && (
                  <div className="flex flex-wrap items-center justify-between gap-2 pt-2 border-t border-white/8 text-[11px] text-[#99999E]">
                    <span className="font-mono tabular-nums">
                      {isRunningAuto
                        ? t.scarlettAutoListening
                        : ch.autoGainStatus === 'succeeded'
                          ? t.scarlettAutoOptimal.replace('{gain}', String(ch.gainDb))
                          : ch.clipSafeTriggeredCount > 0
                            ? t.scarlettClipSafeCount.replace(
                                '{count}',
                                String(ch.clipSafeTriggeredCount),
                              )
                            : t.scarlettAutoReady}
                    </span>
                    <button
                      type="button"
                      onClick={() => handleTestClipSafeOverload(chIdx)}
                      className="text-[#89B0FF] hover:text-white underline underline-offset-2 whitespace-nowrap"
                    >
                      {t.scarlettTestPeakBtn}
                    </button>
                  </div>
                )}
              </div>
            );
          })}
        </div>

        {/* Direct Monitor, Output Controls, Loopback & Hardware NVRAM Bar */}
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-4 pt-3 border-t border-white/8 items-center">
          {/* Direct Monitor Mode */}
          <div className="lg:col-span-4 space-y-1.5">
            <span className="text-xs font-medium text-[#B7B7BB] block">
              {t.scarlettDirectMonLabel}
            </span>
            <div className="grid grid-cols-3 gap-1 p-1 rounded-lg bg-[#0C0E14] border border-white/8">
              {(
                [
                  ['off', t.scarlettDmOff],
                  ['mono', t.scarlettDmMono],
                  ['stereo', t.scarlettDmStereo],
                ] as Array<[ScarlettDirectMonitorMode, string]>
              ).map(([modeId, label]) => {
                const active = scarlett.directMonitor === modeId;
                return (
                  <button
                    key={modeId}
                    type="button"
                    disabled={!modelSpec.supportsSoftwareControls}
                    aria-pressed={active}
                    onClick={() => onUpdateScarlett({ ...scarlett, directMonitor: modeId })}
                    className={`px-2 py-1.5 text-xs font-medium rounded-md transition-colors whitespace-nowrap disabled:opacity-40 ${
                      active ? 'bg-[#387AFF] text-white' : 'text-[#99999E] hover:text-white'
                    }`}
                  >
                    {label}
                  </button>
                );
              })}
            </div>
          </div>

          {/* Direct Monitor Input/Playback Blend & Output Level */}
          <div className="lg:col-span-4 grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1">
              <div className="flex justify-between text-[11px] text-[#99999E]">
                <span>{t.scarlettMonitorMixLabel}</span>
                <span className="font-mono tabular-nums text-white">
                  {scarlett.directMonitorMixPct}%
                </span>
              </div>
              <input
                type="range"
                aria-label={t.scarlettMonitorMixLabel}
                min={0}
                max={100}
                disabled={!modelSpec.supportsMonitorMix}
                value={scarlett.directMonitorMixPct}
                onChange={(e) =>
                  onUpdateScarlett({ ...scarlett, directMonitorMixPct: Number(e.target.value) })
                }
                className="cavern-slider w-full disabled:opacity-40"
              />
            </div>

            <div className="space-y-1">
              <div className="flex justify-between text-[11px] text-[#99999E]">
                <span>{t.scarlettOutputLevelLabel}</span>
                <span className="font-mono tabular-nums text-white">
                  {scarlett.outputMute ? 'MUTE' : `${scarlett.outputLevelPct}%`}
                </span>
              </div>
              <input
                type="range"
                aria-label={t.scarlettOutputLevelLabel}
                min={0}
                max={100}
                value={scarlett.outputLevelPct}
                onChange={(e) =>
                  onUpdateScarlett({ ...scarlett, outputLevelPct: Number(e.target.value) })
                }
                className="cavern-slider w-full"
              />
            </div>
          </div>

          {/* Mute / Dim / Loopback / Save to HW */}
          <div className="lg:col-span-4 flex flex-wrap items-center lg:justify-end gap-2">
            <button
              type="button"
              aria-pressed={scarlett.outputMute}
              onClick={() => onUpdateScarlett({ ...scarlett, outputMute: !scarlett.outputMute })}
              className={`inline-flex items-center gap-1 px-2.5 py-2 text-xs font-semibold rounded-lg border whitespace-nowrap ${
                scarlett.outputMute
                  ? 'bg-[#DC2626] border-[#EF4444] text-white'
                  : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
              }`}
            >
              {scarlett.outputMute ? (
                <VolumeX className="w-3.5 h-3.5" />
              ) : (
                <Volume2 className="w-3.5 h-3.5" />
              )}
              <span>{t.scarlettMuteBtn}</span>
            </button>

            <button
              type="button"
              aria-pressed={scarlett.outputDim}
              onClick={() => onUpdateScarlett({ ...scarlett, outputDim: !scarlett.outputDim })}
              className={`px-2.5 py-2 text-xs font-semibold rounded-lg border whitespace-nowrap ${
                scarlett.outputDim
                  ? 'bg-[#F59E0B]/20 border-[#F59E0B] text-[#FCD34D]'
                  : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
              }`}
            >
              Dim -20
            </button>

            {modelSpec.supportsLoopback && (
              <button
                type="button"
                aria-pressed={scarlett.loopbackEnabled}
                onClick={() =>
                  onUpdateScarlett({ ...scarlett, loopbackEnabled: !scarlett.loopbackEnabled })
                }
                className={`px-2.5 py-2 text-xs font-semibold rounded-lg border whitespace-nowrap ${
                  scarlett.loopbackEnabled
                    ? 'bg-[#387AFF]/20 border-[#598FFF] text-[#89B0FF]'
                    : 'bg-[#1C1E27] border-white/10 text-[#B7B7BB] hover:text-white'
                }`}
              >
                Loopback
              </button>
            )}

            <button
              type="button"
              disabled={!modelSpec.supportsSoftwareControls}
              onClick={() => {
                onUpdateScarlett({
                  ...scarlett,
                  msdDisabled: true,
                  savedToNvramCount: scarlett.savedToNvramCount + 1,
                });
                onNotify(t.scarlettSavedNvramToast);
              }}
              className="inline-flex items-center gap-1.5 px-3 py-2 text-xs font-semibold rounded-lg bg-[#1E1F27] hover:bg-[#2A2C39] text-white border border-white/12 whitespace-nowrap disabled:opacity-40"
            >
              <Save className="w-3.5 h-3.5 text-[#89B0FF]" />
              <span>{t.scarlettSaveHwBtn}</span>
            </button>
          </div>
        </div>
      </div>

      {/* SECTION 2: W11 Auto-Vocal Mix-Match & Live Mastering Vocal Chain (Slot 2D) */}
      <div className="bg-[#15161C] border border-white/10 rounded-2xl p-5 sm:p-6 space-y-5">
        <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-4 pb-4 border-b border-white/8">
          <div className="space-y-1">
            <div className="flex items-center gap-2">
              <Sparkles className="w-4 h-4 text-[#89B0FF]" />
              <h3 className="text-base sm:text-lg font-bold text-white">{t.vocalRackTitle}</h3>
            </div>
            <p className="text-xs text-[#99999E]">{t.vocalRackSubtitle}</p>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={handleCalibrateVoiceprint}
              disabled={calibratingVoiceprint}
              className="inline-flex items-center gap-1.5 px-3.5 py-2 text-xs font-semibold rounded-lg bg-[#387AFF] hover:bg-[#598FFF] text-white transition-colors whitespace-nowrap shrink-0 disabled:opacity-60"
            >
              <Activity className="w-3.5 h-3.5" />
              <span>
                {calibratingVoiceprint
                  ? t.vocalCalibratingBtn
                  : vocalRack.voiceprintCalibrated
                    ? t.vocalRecalibrateBtn
                    : t.vocalCalibrateBtn}
              </span>
            </button>

            <button
              type="button"
              aria-pressed={vocalRack.bypass}
              onClick={() => onUpdateVocalRack({ bypass: !vocalRack.bypass })}
              className={`px-3.5 py-2 text-xs font-semibold rounded-lg border transition-colors whitespace-nowrap shrink-0 ${
                vocalRack.bypass
                  ? 'bg-[#F59E0B]/20 border-[#F59E0B] text-[#FCD34D]'
                  : 'bg-[#1C1E27] border-white/10 text-[#E9E9EC] hover:text-white'
              }`}
            >
              {vocalRack.bypass ? t.vocalBypassActiveBtn : t.vocalBypassBtn}
            </button>
          </div>
        </div>

        {/* Song Vocal Mix DNA Summary Strip (Unboxed Tabular Metrics) */}
        <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-6 gap-4 text-xs pb-4 border-b border-white/8">
          <div>
            <p className="text-[#99999E]">{t.vocalDnaTrack}</p>
            <p className="font-semibold text-white truncate mt-1">{activeTrack.title}</p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.vocalDnaTargetLufs}</p>
            <p className="font-mono tabular-nums text-white mt-1">{dna.targetVocalLufs} LUFS</p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.vocalDnaCrest}</p>
            <p className="font-mono tabular-nums text-white mt-1">{dna.crestFactorDb} dB</p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.vocalDnaReverb}</p>
            <p className="font-mono tabular-nums text-white mt-1">
              {dna.reverbRt60Sec} s · {dna.reverbPreDelayMs} ms
            </p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.vocalDnaDelay}</p>
            <p className="font-mono tabular-nums text-white mt-1">
              {dna.delayTimeMs} ms ({dna.delayWetPct}%)
            </p>
          </div>
          <div>
            <p className="text-[#99999E]">{t.vocalDnaAtmosPos}</p>
            <p className="font-mono tabular-nums text-[#89B0FF] mt-1">
              ({dna.atmosPosition.x.toFixed(1)}, {dna.atmosPosition.y.toFixed(1)},{' '}
              {dna.atmosPosition.z.toFixed(1)})
            </p>
          </div>
        </div>

        {/* Main 2-Column Layout: Macros + Formant-Safe Match EQ & Live DSP Rack */}
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
          {/* Left 5 cols: Studio Macros */}
          <div className="lg:col-span-5 space-y-4">
            <div className="space-y-1.5">
              <div className="flex justify-between text-xs">
                <span className="text-[#E9E9EC] font-medium">{t.vocalMacroMixMatch}</span>
                <span className="font-mono tabular-nums text-white">{vocalRack.mixMatchPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.vocalMacroMixMatch}
                min={0}
                max={100}
                value={vocalRack.mixMatchPct}
                onChange={(e) => onUpdateVocalRack({ mixMatchPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div className="space-y-1.5">
              <div className="flex justify-between text-xs">
                <span className="text-[#E9E9EC] font-medium">{t.vocalMacroDynamics}</span>
                <span className="font-mono tabular-nums text-white">{vocalRack.dynamicsPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.vocalMacroDynamics}
                min={0}
                max={150}
                value={vocalRack.dynamicsPct}
                onChange={(e) => onUpdateVocalRack({ dynamicsPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div className="space-y-1.5">
              <div className="flex justify-between text-xs">
                <span className="text-[#E9E9EC] font-medium">{t.vocalMacroWarmth}</span>
                <span className="font-mono tabular-nums text-white">{vocalRack.warmthPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.vocalMacroWarmth}
                min={0}
                max={100}
                value={vocalRack.warmthPct}
                onChange={(e) => onUpdateVocalRack({ warmthPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div className="space-y-1.5">
              <div className="flex justify-between text-xs">
                <span className="text-[#E9E9EC] font-medium">{t.vocalMacroReverb}</span>
                <span className="font-mono tabular-nums text-white">{vocalRack.reverbPct}%</span>
              </div>
              <input
                type="range"
                aria-label={t.vocalMacroReverb}
                min={0}
                max={150}
                value={vocalRack.reverbPct}
                onChange={(e) => onUpdateVocalRack({ reverbPct: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            <div className="space-y-1.5">
              <div className="flex justify-between text-xs">
                <span className="text-[#E9E9EC] font-medium">{t.vocalMacroLevel}</span>
                <span className="font-mono tabular-nums text-white">
                  {vocalRack.vocalLevelDb >= 0
                    ? `+${vocalRack.vocalLevelDb}`
                    : vocalRack.vocalLevelDb}{' '}
                  dB
                </span>
              </div>
              <input
                type="range"
                aria-label={t.vocalMacroLevel}
                min={-12}
                max={12}
                step={0.5}
                value={vocalRack.vocalLevelDb}
                onChange={(e) => onUpdateVocalRack({ vocalLevelDb: Number(e.target.value) })}
                className="cavern-slider w-full"
              />
            </div>

            {/* Stage Switches */}
            <div className="flex flex-wrap gap-4 pt-2 text-xs text-[#E9E9EC]">
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={vocalRack.noiseGateEnabled}
                  onChange={(e) => onUpdateVocalRack({ noiseGateEnabled: e.target.checked })}
                  className="rounded accent-[#387AFF]"
                />
                <span>{t.vocalStageGate}</span>
              </label>
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={vocalRack.deEsserEnabled}
                  onChange={(e) => onUpdateVocalRack({ deEsserEnabled: e.target.checked })}
                  className="rounded accent-[#387AFF]"
                />
                <span>{t.vocalStageDeEsser}</span>
              </label>
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={vocalRack.sidechainPocketGlue}
                  onChange={(e) => onUpdateVocalRack({ sidechainPocketGlue: e.target.checked })}
                  className="rounded accent-[#387AFF]"
                />
                <span>{t.vocalStageSidechain}</span>
              </label>
            </div>
          </div>

          {/* Right 7 cols: Formant-Safe 8-Band ERB Match EQ Curve + Live Rack GR Telemetry */}
          <div className="lg:col-span-7 space-y-4">
            <div className="bg-[#111319] border border-white/8 rounded-xl p-4 space-y-3">
              <div className="flex items-center justify-between gap-2 text-xs">
                <span className="font-semibold text-white">{t.vocalMatchEqTitle}</span>
                <span className="font-mono tabular-nums text-[#89B0FF]">
                  {vocalRack.voiceprintCalibrated ? t.vocalVoiceprintLocked : t.vocalVoiceprintDefault}
                </span>
              </div>

              <div className="grid grid-cols-8 gap-2 pt-2 items-end h-28">
                {dna.bandHz.map((hz, i) => {
                  const scaled = Number(
                    (
                      dna.matchEqDeltasDb[i] *
                      (vocalRack.bypass ? 0 : vocalRack.mixMatchPct / 100)
                    ).toFixed(1),
                  );
                  const barHeight = Math.min(100, Math.max(8, Math.round((Math.abs(scaled) / 6) * 85)));
                  const positive = scaled >= 0;
                  return (
                    <div key={hz} className="flex flex-col items-center gap-1.5 h-full justify-end">
                      <span className="text-[10px] font-mono tabular-nums text-[#B7B7BB]">
                        {scaled > 0 ? `+${scaled.toFixed(1)}` : scaled.toFixed(1)}
                      </span>
                      <div className="w-full max-w-[26px] h-16 bg-white/5 rounded-md flex items-end overflow-hidden">
                        <div
                          className="w-full rounded-md transition-all duration-150"
                          style={{
                            height: `${barHeight}%`,
                            backgroundColor: positive ? '#387AFF' : '#F59E0B',
                          }}
                        />
                      </div>
                      <span className="text-[10px] font-mono tabular-nums text-[#99999E]">
                        {hz >= 1000 ? `${(hz / 1000).toFixed(hz >= 10000 ? 0 : 1)}k` : hz}
                      </span>
                    </div>
                  );
                })}
              </div>
            </div>

            {/* Live Rack Stage Meters */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-xs">
              <div className="bg-[#111319] border border-white/8 rounded-xl p-3">
                <p className="text-[#99999E]">FET 1176 GR</p>
                <p className="font-mono tabular-nums text-white font-semibold mt-1">
                  {compMeters.fetGrDb.toFixed(1)} dB
                </p>
              </div>
              <div className="bg-[#111319] border border-white/8 rounded-xl p-3">
                <p className="text-[#99999E]">Opto LA-2A GR</p>
                <p className="font-mono tabular-nums text-white font-semibold mt-1">
                  {compMeters.optoGrDb.toFixed(1)} dB
                </p>
              </div>
              <div className="bg-[#111319] border border-white/8 rounded-xl p-3">
                <p className="text-[#99999E]">ADAA Drive</p>
                <p className="font-mono tabular-nums text-white font-semibold mt-1">
                  {vocalRack.bypass
                    ? 'Bypass'
                    : `${Math.round(vocalRack.warmthPct * (vocalRack.mixMatchPct / 100))}%`}
                </p>
              </div>
              <div className="bg-[#111319] border border-white/8 rounded-xl p-3">
                <p className="text-[#99999E]">Pocket Glue</p>
                <p className="font-mono tabular-nums text-[#34D399] font-semibold mt-1">
                  {pocketDuckDb.toFixed(1)} dB
                </p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};
