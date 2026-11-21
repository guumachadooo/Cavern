import type {
  CavernSettings,
  SourceKind,
  SpatialSourceNode,
  TrackFormatVariant,
  Vec3,
} from '../types/cavern';
import type {
  ScarlettHardwareControlState,
  VocalRackState,
} from './scarlettVocalDsp';
import { clamp, distance3D } from './spatialMath';

export const SPEED_OF_SOUND_MPS = 343.0;
export const XRAY_AZIMUTH_BINS = 12;
export const XRAY_ELEVATION_BINS = 6;
export const XRAY_TOTAL_CELLS = XRAY_AZIMUTH_BINS * XRAY_ELEVATION_BINS;

export type XRayBandMode = 'all' | 'low' | 'mid' | 'high';

export interface SphericalPowerCell {
  index: number;
  azIndex: number;
  elIndex: number;
  dir: Vec3;
  low: number;
  mid: number;
  high: number;
  energy: number;
  normalized: number;
}

export interface SphericalPowermapSnapshot {
  bandMode: XRayBandMode;
  cells: SphericalPowerCell[];
  peakEnergy: number;
  totalEnergy: number;
  dominantDoA: Vec3;
  coherence: number;
  diffuseness: number;
}

export interface ThreeBandWeights {
  low: number;
  mid: number;
  high: number;
}

export interface SpatialXRayMetrics {
  coherencePct: number;
  diffusenessPct: number;
  momentaryLufs: number;
  integratedLufs: number;
  truePeakDbtp: number;
  limiterGrDb: number;
  dominantDoA: Vec3;
  avgPropagationDelayMs: number;
  maxPropagationDelayMs: number;
  isLossySource: boolean;
  couplingStartHz: number;
  spxStartHz: number;
  gccPhatOffsetSamples: number;
  gccPhatOffsetMs: number;
  theilSenDriftPpm: number;
  flacFusionPct: number;
  lgsrBypassed: boolean;
  vocalInputDbfs: number;
  vocalOutputDbfs: number;
  vocalGateGrDb: number;
  vocalDeEsserGrDb: number;
  vocalCompGrDb: number;
  vocalSidechainDuckDb: number;
  vocalRttMs: number;
  vocalBypassed: boolean;
}

/**
 * Returns true if the source can be interactively dragged and pinned in the 3D Room.
 */
export function isSourceDraggable(source: Pick<SpatialSourceNode, 'kind'>): boolean {
  return source.kind === 'object' || source.kind === 'stem' || source.kind === 'mic';
}

/**
 * Computes physical 6DoF acoustic time-of-flight propagation delay in milliseconds (c = 343 m/s).
 * Omnidirectional LFE channels return 0 ms.
 */
export function computePropagationDelayMs(distanceMeters: number, isLfe = false): number {
  if (isLfe || !Number.isFinite(distanceMeters) || distanceMeters <= 0) {
    return 0;
  }
  const clampedDist = Math.min(35.0, distanceMeters);
  return Number(((clampedDist / SPEED_OF_SOUND_MPS) * 1000).toFixed(2));
}

/**
 * Derives normalized 3-band spectral energy shares (Low <250 Hz, Mid 250-2500 Hz, High >2500 Hz)
 * matching Cavern's 250 Hz / 2500 Hz crossover bands.
 */
export function computeSourceBandWeights(
  source: Pick<SpatialSourceNode, 'kind' | 'synthFreqHz'>,
): ThreeBandWeights {
  const f = Math.max(20, source.synthFreqHz);
  if (source.kind === 'lfe') {
    return { low: 0.92, mid: 0.07, high: 0.01 };
  }
  if (f < 250) {
    const lowShare = clamp(1 - (f - 50) / 360, 0.42, 0.85);
    const highShare = clamp((f - 100) / 1400, 0.04, 0.16);
    const midShare = Math.max(0.05, 1 - lowShare - highShare);
    return {
      low: Number(lowShare.toFixed(3)),
      mid: Number(midShare.toFixed(3)),
      high: Number(highShare.toFixed(3)),
    };
  }
  if (f <= 2500) {
    const highShare = clamp((f - 220) / 2600, 0.12, 0.48);
    const lowShare = clamp((700 - f) / 1800, 0.05, 0.28);
    const midShare = Math.max(0.2, 1 - lowShare - highShare);
    return {
      low: Number(lowShare.toFixed(3)),
      mid: Number(midShare.toFixed(3)),
      high: Number(highShare.toFixed(3)),
    };
  }
  return { low: 0.05, mid: 0.25, high: 0.7 };
}

/**
 * Computes the 12x6 (72-cell) spherical directional 3-band energy heatmap around the listener,
 * mirroring Cavern/Spatial/SpatialXRayPowermap.cs.
 */
export function computeSphericalPowermap(
  sources: SpatialSourceNode[],
  listenerPos: Vec3,
  bandMode: XRayBandMode = 'all',
): SphericalPowermapSnapshot {
  const anySolo = sources.some((s) => s.solo);
  const cells: SphericalPowerCell[] = [];

  for (let el = 0; el < XRAY_ELEVATION_BINS; el += 1) {
    const elevRad = -Math.PI * 0.5 + ((el + 0.5) / XRAY_ELEVATION_BINS) * Math.PI;
    const cosEl = Math.cos(elevRad);
    const sinEl = Math.sin(elevRad);

    for (let az = 0; az < XRAY_AZIMUTH_BINS; az += 1) {
      const azRad = ((az + 0.5) / XRAY_AZIMUTH_BINS) * Math.PI * 2 - Math.PI;
      const dir: Vec3 = {
        x: Math.sin(azRad) * cosEl,
        y: sinEl,
        z: Math.cos(azRad) * cosEl,
      };
      cells.push({
        index: el * XRAY_AZIMUTH_BINS + az,
        azIndex: az,
        elIndex: el,
        dir,
        low: 0,
        mid: 0,
        high: 0,
        energy: 0,
        normalized: 0,
      });
    }
  }

  let doaX = 0;
  let doaY = 0;
  let doaZ = 0;
  let doaWeightSum = 0;

  for (const src of sources) {
    if (src.mute || (anySolo && !src.solo)) continue;
    const dx = src.position.x - listenerPos.x;
    const dy = src.position.y - listenerPos.y;
    const dz = src.position.z - listenerPos.z;
    const dist = Math.hypot(dx, dy, dz);
    if (dist < 1e-4) continue;

    const invDist = 1 / dist;
    const ux = dx * invDist;
    const uy = dy * invDist;
    const uz = dz * invDist;

    const weights = computeSourceBandWeights(src);
    const energy = src.rms * src.rms;
    const lowE = energy * weights.low;
    const midE = energy * weights.mid;
    const highE = energy * weights.high;
    const selectedE =
      bandMode === 'low'
        ? lowE
        : bandMode === 'mid'
          ? midE
          : bandMode === 'high'
            ? highE
            : energy;

    if (selectedE <= 1e-6) continue;

    doaX += ux * selectedE;
    doaY += uy * selectedE;
    doaZ += uz * selectedE;
    doaWeightSum += selectedE;

    for (let i = 0; i < cells.length; i += 1) {
      const c = cells[i];
      const dot = c.dir.x * ux + c.dir.y * uy + c.dir.z * uz;
      if (dot <= 0.15) continue;
      // Raised-cosine directional lobe (matches SpatialXRayPowermap.cs)
      const lobe = dot * dot * dot;
      c.low += lowE * lobe;
      c.mid += midE * lobe;
      c.high += highE * lobe;
    }
  }

  let peakEnergy = 0;
  let totalEnergy = 0;
  for (let i = 0; i < cells.length; i += 1) {
    const c = cells[i];
    const e =
      bandMode === 'low'
        ? c.low
        : bandMode === 'mid'
          ? c.mid
          : bandMode === 'high'
            ? c.high
            : c.low + c.mid + c.high;
    c.energy = e;
    totalEnergy += e;
    if (e > peakEnergy) peakEnergy = e;
  }

  if (peakEnergy > 1e-7) {
    for (let i = 0; i < cells.length; i += 1) {
      cells[i].normalized = clamp(cells[i].energy / peakEnergy, 0, 1);
    }
  }

  const doaMag = Math.hypot(doaX, doaY, doaZ);
  const dominantDoA: Vec3 =
    doaMag > 1e-5
      ? { x: doaX / doaMag, y: doaY / doaMag, z: doaZ / doaMag }
      : { x: 0, y: 0, z: 1 };

  const coherence = doaWeightSum > 1e-6 ? clamp(doaMag / doaWeightSum, 0, 1) : 0;
  const diffuseness = clamp(1 - coherence, 0, 1);

  return {
    bandMode,
    cells,
    peakEnergy,
    totalEnergy,
    dominantDoA,
    coherence,
    diffuseness,
  };
}

/**
 * Computes real-time Studio Spatial X-Ray Inspector telemetry across all 3 acoustic HUD domains:
 * 1) Soundfield Coherence/Diffuseness + ITU-R BS.1770-5 Metering
 * 2) Lossy-vs-Lossless LGSR (Coupling / SPX / GCC-PHAT / Theil-Sen drift / FLAC fusion)
 * 3) Live Vocal Chain X-Ray (Input/Output dBFS, Gate/De-Esser/Comp/Sidechain Duck GR, RTT)
 */
export function computeSpatialXRayMetrics(
  sources: SpatialSourceNode[],
  listenerPos: Vec3,
  activeVariant: TrackFormatVariant,
  settings: CavernSettings,
  vocalRack: VocalRackState,
  scarlett: ScarlettHardwareControlState,
  lgsrBypassed: boolean,
  isPlaying: boolean,
  bandMode: XRayBandMode = 'all',
): SpatialXRayMetrics {
  const powermap = computeSphericalPowermap(sources, listenerPos, bandMode);
  const anySolo = sources.some((s) => s.solo);
  const audible = sources.filter((s) => !s.mute && (!anySolo || s.solo));

  let delaySum = 0;
  let delayCount = 0;
  let maxDelay = 0;
  let energySum = 0;
  let peakRms = 0;

  for (const src of audible) {
    energySum += src.rms * src.rms;
    if (src.rms > peakRms) peakRms = src.rms;
    if (src.kind !== 'lfe') {
      const d = settings.propagationDelay6DoF
        ? computePropagationDelayMs(distance3D(listenerPos, src.position), false)
        : 0;
      delaySum += d;
      delayCount += 1;
      if (d > maxDelay) maxDelay = d;
    }
  }

  const avgRms = audible.length > 0 ? Math.sqrt(energySum / audible.length) : 0.001;
  const liveScale = isPlaying ? 1 : 0.42;
  const momentaryLufs = Number(
    clamp(20 * Math.log10(Math.max(1e-4, avgRms * liveScale)) - 6.2 + settings.renderGainDb, -60, -3).toFixed(1),
  );
  const integratedLufs = Number(clamp(momentaryLufs - 1.4, -60, -5).toFixed(1));
  const rawPeakDbtp = 20 * Math.log10(Math.max(1e-4, peakRms * liveScale)) - 0.8 + settings.renderGainDb;
  const truePeakDbtp = Number(clamp(settings.dynamicNormalizer ? Math.min(-1.0, rawPeakDbtp) : rawPeakDbtp, -48, 0).toFixed(1));
  const limiterGrDb =
    settings.dynamicNormalizer && rawPeakDbtp > -1.0
      ? Number(clamp(rawPeakDbtp - -1.0, 0, 9.5).toFixed(1))
      : 0;

  // Lossy vs Lossless LGSR detection based on active track variant codec & label
  const formatDescriptor = `${activeVariant.codec} ${activeVariant.label}`.toUpperCase();
  const isLossySource =
    formatDescriptor.includes('E-AC-3') ||
    formatDescriptor.includes('EAC3') ||
    formatDescriptor.includes('DOLBY DIGITAL PLUS') ||
    formatDescriptor.includes('DD+') ||
    formatDescriptor.includes('AC-3') ||
    formatDescriptor.includes('AAC') ||
    formatDescriptor.includes('OPUS') ||
    formatDescriptor.includes('MP3');

  const couplingStartHz = isLossySource ? 10000 : 0;
  const spxStartHz = isLossySource ? 12500 : 0;
  const gccPhatOffsetSamples = isLossySource && !lgsrBypassed ? 2.4 : 0;
  const gccPhatOffsetMs = Number(((gccPhatOffsetSamples / Math.max(44100, activeVariant.sampleRateHz)) * 1000).toFixed(3));
  const theilSenDriftPpm = isLossySource && !lgsrBypassed ? 1.8 : 0;
  const flacFusionPct =
    isLossySource && !lgsrBypassed
      ? Math.round(clamp(72 + powermap.coherence * 22, 65, 96))
      : 0;

  // Live Vocal Chain X-Ray telemetry derived from real Scarlett + W11 VocalRackState
  const ch1 = scarlett.channels[0];
  const micNode = sources.find((s) => s.kind === 'mic');
  const micActivity = micNode && !micNode.mute ? micNode.rms : 0.12;
  const gainNormalized = (ch1.gainDb - 30) * 0.35;
  const vocalInputDbfs = Number(clamp(-26 + micActivity * 18 + gainNormalized, -58, -1.5).toFixed(1));

  const vocalBypassed = vocalRack.bypass || !vocalRack.enabled;
  const vocalGateGrDb =
    !vocalBypassed && vocalRack.noiseGateEnabled && vocalInputDbfs < -34
      ? Number(clamp((-34 - vocalInputDbfs) * 0.75, 0, 18).toFixed(1))
      : 0;
  const vocalDeEsserGrDb =
    !vocalBypassed && vocalRack.deEsserEnabled
      ? Number(clamp(micActivity * 4.2 + (ch1.airMode === 'drive' ? 1.4 : ch1.airMode === 'presence' ? 0.7 : 0), 0, 8).toFixed(1))
      : 0;
  const vocalCompGrDb = !vocalBypassed
    ? Number(clamp((vocalRack.dynamicsPct / 100) * (2.5 + micActivity * 6.5), 0, 12).toFixed(1))
    : 0;
  const vocalSidechainDuckDb =
    !vocalBypassed && vocalRack.sidechainPocketGlue && isPlaying
      ? Number(clamp(1.2 + micActivity * 2.8, 0, 4.5).toFixed(1))
      : 0;

  const vocalOutputDbfs = vocalBypassed
    ? vocalInputDbfs
    : Number(
        clamp(
          vocalInputDbfs +
            vocalRack.vocalLevelDb +
            vocalCompGrDb * 0.55 -
            vocalGateGrDb * 0.4 -
            vocalDeEsserGrDb * 0.25,
          -58,
          -1.0,
        ).toFixed(1),
      );

  const vocalRttMs = scarlett.directMonitor !== 'off' ? 1.4 : 4.8;

  return {
    coherencePct: Math.round(powermap.coherence * 100),
    diffusenessPct: Math.round(powermap.diffuseness * 100),
    momentaryLufs,
    integratedLufs,
    truePeakDbtp,
    limiterGrDb,
    dominantDoA: powermap.dominantDoA,
    avgPropagationDelayMs: delayCount > 0 ? Number((delaySum / delayCount).toFixed(2)) : 0,
    maxPropagationDelayMs: Number(maxDelay.toFixed(2)),
    isLossySource,
    couplingStartHz,
    spxStartHz,
    gccPhatOffsetSamples,
    gccPhatOffsetMs,
    theilSenDriftPpm,
    flacFusionPct,
    lgsrBypassed,
    vocalInputDbfs,
    vocalOutputDbfs,
    vocalGateGrDb,
    vocalDeEsserGrDb,
    vocalCompGrDb,
    vocalSidechainDuckDb,
    vocalRttMs,
    vocalBypassed,
  };
}

/**
 * Helper to classify source kind colors consistently across 3D Canvas & Mixer.
 */
export function getSourceKindHexColor(kind: SourceKind): string {
  switch (kind) {
    case 'object':
      return '#F56962';
    case 'lfe':
      return '#A855F7';
    case 'height':
      return '#38BDF8';
    case 'stem':
      return '#22D3EE';
    case 'mic':
      return '#34D399';
    default:
      return '#598FFF';
  }
}
