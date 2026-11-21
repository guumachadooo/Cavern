export type ScarlettGeneration = 2 | 3 | 4;

export type ScarlettModelId =
  | '2i2-gen4'
  | '4i4-gen4'
  | 'solo-gen4'
  | '2i2-gen3'
  | '2i2-gen2';

export type ScarlettAirMode = 'off' | 'presence' | 'drive';

export type ScarlettDirectMonitorMode = 'off' | 'mono' | 'stereo';

export type ScarlettAutoGainStatus = 'idle' | 'running' | 'succeeded' | 'failed';

export interface ScarlettModelSpec {
  id: ScarlettModelId;
  name: string;
  generation: ScarlettGeneration;
  vendorId: number;
  productId: number;
  inputCount: 1 | 2;
  supportsSoftwareControls: boolean;
  supportsDigitalGain: boolean;
  supportsAirDrive: boolean;
  supportsAutoGain: boolean;
  supportsClipSafe: boolean;
  supportsStereoLink: boolean;
  supportsLoopback: boolean;
  supportsMonitorMix: boolean;
  sharedPhantom48v: boolean;
}

export const SCARLETT_MODELS: ScarlettModelSpec[] = [
  {
    id: '2i2-gen4',
    name: 'Focusrite Scarlett 2i2 (4th Gen)',
    generation: 4,
    vendorId: 0x1235,
    productId: 0x8219,
    inputCount: 2,
    supportsSoftwareControls: true,
    supportsDigitalGain: true,
    supportsAirDrive: true,
    supportsAutoGain: true,
    supportsClipSafe: true,
    supportsStereoLink: true,
    supportsLoopback: true,
    supportsMonitorMix: true,
    sharedPhantom48v: false,
  },
  {
    id: '4i4-gen4',
    name: 'Focusrite Scarlett 4i4 (4th Gen)',
    generation: 4,
    vendorId: 0x1235,
    productId: 0x821a,
    inputCount: 2,
    supportsSoftwareControls: true,
    supportsDigitalGain: true,
    supportsAirDrive: true,
    supportsAutoGain: true,
    supportsClipSafe: true,
    supportsStereoLink: true,
    supportsLoopback: true,
    supportsMonitorMix: true,
    sharedPhantom48v: false,
  },
  {
    id: 'solo-gen4',
    name: 'Focusrite Scarlett Solo (4th Gen)',
    generation: 4,
    vendorId: 0x1235,
    productId: 0x8218,
    inputCount: 1,
    supportsSoftwareControls: true,
    supportsDigitalGain: true,
    supportsAirDrive: true,
    supportsAutoGain: true,
    supportsClipSafe: true,
    supportsStereoLink: false,
    supportsLoopback: true,
    supportsMonitorMix: true,
    sharedPhantom48v: false,
  },
  {
    id: '2i2-gen3',
    name: 'Focusrite Scarlett 2i2 (3rd Gen)',
    generation: 3,
    vendorId: 0x1235,
    productId: 0x8210,
    inputCount: 2,
    supportsSoftwareControls: true,
    supportsDigitalGain: false,
    supportsAirDrive: false,
    supportsAutoGain: false,
    supportsClipSafe: false,
    supportsStereoLink: false,
    supportsLoopback: false,
    supportsMonitorMix: false,
    sharedPhantom48v: true,
  },
  {
    id: '2i2-gen2',
    name: 'Focusrite Scarlett 2i2 (2nd Gen)',
    generation: 2,
    vendorId: 0x1235,
    productId: 0x8202,
    inputCount: 2,
    supportsSoftwareControls: false,
    supportsDigitalGain: false,
    supportsAirDrive: false,
    supportsAutoGain: false,
    supportsClipSafe: false,
    supportsStereoLink: false,
    supportsLoopback: false,
    supportsMonitorMix: false,
    sharedPhantom48v: true,
  },
];

export interface ScarlettChannelState {
  gainDb: number;
  phantom48v: boolean;
  airMode: ScarlettAirMode;
  instrumentHiZ: boolean;
  clipSafe: boolean;
  autoGainStatus: ScarlettAutoGainStatus;
  autoGainProgressPct: number;
  meterPeakDbfs: number;
  clipSafeTriggeredCount: number;
}

export interface ScarlettHardwareControlState {
  modelId: ScarlettModelId;
  inputsLinked: boolean;
  channels: [ScarlettChannelState, ScarlettChannelState];
  directMonitor: ScarlettDirectMonitorMode;
  directMonitorMixPct: number;
  outputLevelPct: number;
  outputMute: boolean;
  outputDim: boolean;
  loopbackEnabled: boolean;
  msdDisabled: boolean;
  savedToNvramCount: number;
}

export interface VocalMixDna {
  trackId: string;
  bandHz: number[];
  songBandDb: number[];
  micBandDb: number[];
  matchEqDeltasDb: number[];
  targetVocalLufs: number;
  crestFactorDb: number;
  spectralTiltDb: number;
  sibilanceIndexDb: number;
  harmonicWarmth: number;
  reverbRt60Sec: number;
  reverbPreDelayMs: number;
  reverbWetPct: number;
  delayTimeMs: number;
  delayWetPct: number;
  atmosPosition: { x: number; y: number; z: number };
}

export interface VocalRackState {
  enabled: boolean;
  bypass: boolean;
  mixMatchPct: number;
  reverbPct: number;
  dynamicsPct: number;
  warmthPct: number;
  vocalLevelDb: number;
  karaokeBackingVocalPct: number;
  noiseGateEnabled: boolean;
  deEsserEnabled: boolean;
  sidechainPocketGlue: boolean;
  voiceprintCalibrated: boolean;
}

export const ERB_ANALYSIS_BANDS_HZ = [120, 250, 500, 1000, 2000, 3800, 6800, 11500];

export function getScarlettModelSpec(modelId: ScarlettModelId): ScarlettModelSpec {
  return SCARLETT_MODELS.find((m) => m.id === modelId) ?? SCARLETT_MODELS[0];
}

export function createDefaultScarlettState(): ScarlettHardwareControlState {
  return {
    modelId: '2i2-gen4',
    inputsLinked: false,
    channels: [
      {
        gainDb: 34,
        phantom48v: true,
        airMode: 'presence',
        instrumentHiZ: false,
        clipSafe: true,
        autoGainStatus: 'idle',
        autoGainProgressPct: 0,
        meterPeakDbfs: -24,
        clipSafeTriggeredCount: 0,
      },
      {
        gainDb: 22,
        phantom48v: false,
        airMode: 'off',
        instrumentHiZ: true,
        clipSafe: true,
        autoGainStatus: 'idle',
        autoGainProgressPct: 0,
        meterPeakDbfs: -42,
        clipSafeTriggeredCount: 0,
      },
    ],
    directMonitor: 'off',
    directMonitorMixPct: 50,
    outputLevelPct: 82,
    outputMute: false,
    outputDim: false,
    loopbackEnabled: false,
    msdDisabled: true,
    savedToNvramCount: 0,
  };
}

export function createDefaultVocalRackState(): VocalRackState {
  return {
    enabled: true,
    bypass: false,
    mixMatchPct: 78,
    reverbPct: 100,
    dynamicsPct: 100,
    warmthPct: 38,
    vocalLevelDb: 0,
    karaokeBackingVocalPct: 25,
    noiseGateEnabled: true,
    deEsserEnabled: true,
    sidechainPocketGlue: true,
    voiceprintCalibrated: false,
  };
}

/**
 * Updates one Scarlett input channel while enforcing Focusrite hardware constraints:
 * - Gen 4 Stereo Link synchronizes Gain, 48V, Air, Inst/Line, and Clip Safe across Ch 1 & Ch 2.
 * - Gen 3 2i2 shares 48V Phantom Power across both channels and caps Air to Off / Presence.
 */
export function applyScarlettChannelPatch(
  state: ScarlettHardwareControlState,
  channelIndex: 0 | 1,
  patch: Partial<ScarlettChannelState>,
): ScarlettHardwareControlState {
  const spec = getScarlettModelSpec(state.modelId);
  const sanitizedPatch: Partial<ScarlettChannelState> = { ...patch };

  if (sanitizedPatch.gainDb !== undefined) {
    sanitizedPatch.gainDb = Math.max(0, Math.min(69, Math.round(sanitizedPatch.gainDb)));
  }
  if (sanitizedPatch.airMode === 'drive' && !spec.supportsAirDrive) {
    sanitizedPatch.airMode = 'presence';
  }

  const nextCh0 = { ...state.channels[0] };
  const nextCh1 = { ...state.channels[1] };

  const shouldMirror = state.inputsLinked && spec.supportsStereoLink;
  if (channelIndex === 0 || shouldMirror) {
    Object.assign(nextCh0, sanitizedPatch);
  }
  if (channelIndex === 1 || shouldMirror) {
    Object.assign(nextCh1, sanitizedPatch);
  }

  // Gen 3 2i2 has a single shared 48V rail
  if (spec.sharedPhantom48v && sanitizedPatch.phantom48v !== undefined) {
    nextCh0.phantom48v = sanitizedPatch.phantom48v;
    nextCh1.phantom48v = sanitizedPatch.phantom48v;
  }

  return {
    ...state,
    channels: [nextCh0, nextCh1],
  };
}

/**
 * Computes optimal Focusrite Gen 4 Auto Gain setting targeting -12 dBFS vocal headroom.
 */
export function computeAutoGainTargetDb(
  measuredPeakDbfs: number,
  currentGainDb: number,
  targetPeakDbfs = -12,
): number {
  if (!Number.isFinite(measuredPeakDbfs) || measuredPeakDbfs <= -90) {
    return Math.max(0, Math.min(69, Math.round(currentGainDb)));
  }
  const delta = targetPeakDbfs - measuredPeakDbfs;
  return Math.max(0, Math.min(69, Math.round(currentGainDb + delta)));
}

/**
 * Evaluates Focusrite Gen 4 Clip Safe automatic preamp attenuation when peaks threaten clipping (>= -1 dBFS).
 */
export function evaluateClipSafeGain(
  currentGainDb: number,
  peakDbfs: number,
  clipSafeEnabled: boolean,
): { nextGainDb: number; triggered: boolean; reductionDb: number } {
  if (!clipSafeEnabled || peakDbfs < -1.0 || currentGainDb <= 0) {
    return { nextGainDb: currentGainDb, triggered: false, reductionDb: 0 };
  }
  const overshoot = Math.max(2, Math.ceil(peakDbfs + 5.5));
  const nextGainDb = Math.max(0, Math.round(currentGainDb - overshoot));
  const reductionDb = currentGainDb - nextGainDb;
  return { nextGainDb, triggered: reductionDb > 0, reductionDb };
}

/**
 * Converts Focusrite 16-bit unsigned linear peak meter value (0..65535) into dBFS (-120..0).
 */
export function meterRawToDbfs(rawPeak: number): number {
  if (!Number.isFinite(rawPeak) || rawPeak <= 0) {
    return -120;
  }
  const linear = Math.min(65535, rawPeak) / 65535;
  const db = 20 * Math.log10(linear);
  return Number(Math.max(-120, Math.min(0, db)).toFixed(2));
}

/**
 * Builds deterministic song vocal mix DNA for a track (matching W11 VocalMixProfileExtractor fields).
 */
export function getTrackVocalDna(trackId: string, isSpatial: boolean): VocalMixDna {
  let seed = 0;
  for (let i = 0; i < trackId.length; i += 1) {
    seed = (seed * 31 + trackId.charCodeAt(i)) % 997;
  }
  const brightBias = ((seed % 7) - 3) * 0.6;
  const songBandDb = [
    -25.5,
    -17.8,
    -14.6,
    -13.8,
    Number((-13.2 + brightBias * 0.4).toFixed(1)),
    Number((-12.0 + brightBias * 0.8).toFixed(1)),
    Number((-15.4 + brightBias * 0.7).toFixed(1)),
    Number((-19.2 + brightBias * 0.5).toFixed(1)),
  ];
  const micBandDb = [-22.0, -15.2, -14.8, -14.5, -15.0, -15.6, -17.5, -22.8];
  return {
    trackId,
    bandHz: [...ERB_ANALYSIS_BANDS_HZ],
    songBandDb,
    micBandDb,
    matchEqDeltasDb: computeFormantSafeMatchEq(songBandDb, micBandDb, 6),
    targetVocalLufs: Number((-14.2 + (seed % 5) * 0.4).toFixed(1)),
    crestFactorDb: Number((9.4 + (seed % 4) * 0.7).toFixed(1)),
    spectralTiltDb: Number((0.9 + brightBias * 0.35).toFixed(2)),
    sibilanceIndexDb: Number((-6.4 + (seed % 3) * 0.6).toFixed(1)),
    harmonicWarmth: Number((0.26 + (seed % 5) * 0.06).toFixed(2)),
    reverbRt60Sec: Number((isSpatial ? 1.55 + (seed % 4) * 0.25 : 1.05 + (seed % 3) * 0.2).toFixed(2)),
    reverbPreDelayMs: isSpatial ? 28 : 18,
    reverbWetPct: isSpatial ? 24 : 16,
    delayTimeMs: 95 + (seed % 5) * 18,
    delayWetPct: 9 + (seed % 4) * 2,
    atmosPosition: isSpatial ? { x: 0, y: 0.45, z: 3.1 } : { x: 0, y: 0.1, z: 2.8 },
  };
}

/**
 * Computes formant-safe bounded Match EQ deltas (in dB, capped at +/- maxGainDb)
 * matching C# VocalMixProfileExtractor.ComputeFormantSafeMatchEqDb.
 */
export function computeFormantSafeMatchEq(
  songBandDb: number[],
  micBandDb: number[],
  maxGainDb = 6,
): number[] {
  const count = Math.min(songBandDb.length, micBandDb.length);
  if (count === 0) return [];
  const songAvg = songBandDb.slice(0, count).reduce((a, b) => a + b, 0) / count;
  const micAvg = micBandDb.slice(0, count).reduce((a, b) => a + b, 0) / count;
  const out: number[] = [];
  for (let i = 0; i < count; i += 1) {
    const rawDelta = (songBandDb[i] - songAvg) - (micBandDb[i] - micAvg);
    const weight = i === 0 || i === count - 1 ? 0.55 : 0.78;
    const clamped = Math.max(-maxGainDb, Math.min(maxGainDb, rawDelta * weight));
    out.push(Number(clamped.toFixed(2)));
  }
  return out;
}

/**
 * Overflow-safe ln(cosh(x)) antiderivative of tanh(x) used in 1st-order ADAA anti-aliased saturation.
 */
export function logCosh(x: number): number {
  const ax = Math.abs(x);
  return ax + Math.log(1 + Math.exp(-2 * ax)) - Math.LN2;
}

/**
 * Evaluates 1st-order ADAA anti-aliased tanh waveshaper between previous sample and current sample.
 */
export function adaaTanhSample(x: number, prevX: number): number {
  const diff = x - prevX;
  if (Math.abs(diff) < 1e-5) {
    return Math.tanh(0.5 * (x + prevX));
  }
  return (logCosh(x) - logCosh(prevX)) / diff;
}

/**
 * Computes combined FET 1176 + Optical LA-2A gain reduction (in dB, <= 0) for live rack metering.
 */
export function computeDualStageCompressorGrDb(
  inputDbfs: number,
  dynamicsPct: number,
  mixMatchPct: number,
): { fetGrDb: number; optoGrDb: number; totalGrDb: number } {
  const scale = Math.max(0, Math.min(1.5, (dynamicsPct / 100) * (mixMatchPct / 100)));
  if (scale <= 0.01 || inputDbfs <= -60) {
    return { fetGrDb: 0, optoGrDb: 0, totalGrDb: 0 };
  }
  const fetOver = Math.max(0, inputDbfs - -16);
  const fetGrDb = Number((-fetOver * 0.75 * Math.min(1.2, scale)).toFixed(2));
  const postFetDb = inputDbfs + fetGrDb;
  const optoOver = Math.max(0, postFetDb - -20);
  const optoGrDb = Number((-optoOver * (1 - 1 / 2.8) * Math.min(1.2, scale)).toFixed(2));
  return {
    fetGrDb,
    optoGrDb,
    totalGrDb: Number((fetGrDb + optoGrDb).toFixed(2)),
  };
}

/**
 * Computes dynamic 1-4 kHz vocal-pocket sidechain ducking applied to the backing instrumental (dB, <= 0).
 */
export function computeVocalPocketDuckDb(
  voiceDbfs: number,
  mixMatchPct: number,
  enabled: boolean,
): number {
  if (!enabled || voiceDbfs <= -32) {
    return 0;
  }
  const scale = Math.max(0, Math.min(1.5, mixMatchPct / 100));
  const duck = -Math.min(3.5, (voiceDbfs - -32) * 0.14 * scale);
  return Number(duck.toFixed(2));
}
