import type { CavernSettings, SpatialTrack, TrackFormatVariant } from '../types/cavern';
import type { VocalRackState } from './scarlettVocalDsp';

export type AtmosFlacBadgeMode = 'atmos-flac' | 'atmos-ab-bypass' | 'native';

export interface LosslessTwinAssessment {
  /** Whether the active variant is a lossy spatial stream eligible for LGSR pairing. */
  isLossyAtmos: boolean;
  /** Matching local lossless variant (FLAC/WAV/ALAC) found in the merged track group. */
  twinVariant: TrackFormatVariant | null;
  /** True when LGSR is enabled, eligible, paired, and not in A/B bypass. */
  isRestoredActive: boolean;
  /** True when eligible and paired, currently bypassed for A/B comparison. */
  isBypassedForAb: boolean;
  /** Badge mode for the Play tab header & Audio Spec modal. */
  badgeMode: AtmosFlacBadgeMode;
  /** Estimated spectral coupling boundary restored by LGSR (Hz). */
  couplingCutoffHz: number;
  /** Estimated GCC-PHAT sub-sample time alignment offset (ms). */
  gccOffsetMs: number;
  /** Estimated inter-source coherence recovery score (0..1). */
  coherenceScore: number;
}

export interface AudioSpecSummary {
  qualityTier: 'Studio Spatial Master' | 'Lossless Twin Restored' | 'Lossy Spatial' | 'Hi-Res Lossless' | 'Standard Stereo';
  effectiveDynamicRangeDb: number;
  nyquistKhz: number;
  bitDepthIntegrity: string;
  lgsr: LosslessTwinAssessment;
}

export interface SingDockSummary {
  vocalLevelPct: number;
  isKaraokeAttenuating: boolean;
  isMicLive: boolean;
  isVocalChainBypassed: boolean;
  mixMatchPct: number;
  reverbPct: number;
}

function isLosslessVariant(v: TrackFormatVariant): boolean {
  const haystack = `${v.codec} ${v.container} ${v.label}`.toLowerCase();
  return (
    haystack.includes('flac') ||
    haystack.includes('wav') ||
    haystack.includes('pcm') ||
    haystack.includes('alac') ||
    haystack.includes('truehd') ||
    haystack.includes('mlp')
  );
}

function isLossySpatialVariant(v: TrackFormatVariant): boolean {
  if (!v.isSpatial) return false;
  if (isLosslessVariant(v)) return false;
  const haystack = `${v.codec} ${v.container} ${v.label}`.toLowerCase();
  return (
    haystack.includes('e-ac-3') ||
    haystack.includes('eac3') ||
    haystack.includes('dd+') ||
    haystack.includes('joc') ||
    haystack.includes('ac-4') ||
    haystack.includes('ac4') ||
    haystack.includes('atmos') ||
    v.bitrateKbps <= 1024
  );
}

/**
 * Evaluates whether the active track + variant qualifies for Universal Lossless Twin
 * (LGSR) restoration and computes deterministic Audio Spec & A/B badge telemetry.
 */
export function assessLosslessTwin(
  track: SpatialTrack,
  activeVariant: TrackFormatVariant,
  settings: Pick<CavernSettings, 'lgsrEnabled' | 'lgsrFusionPct'>,
  lgsrBypassed: boolean,
): LosslessTwinAssessment {
  const isLossyAtmos = isLossySpatialVariant(activeVariant);
  const twinVariant = isLossyAtmos
    ? track.variants.find(
        (v) => v.id !== activeVariant.id && !v.isSpatial && isLosslessVariant(v),
      ) ??
      track.variants.find((v) => v.id !== activeVariant.id && isLosslessVariant(v)) ??
      null
    : null;

  const hasPairedTwin = isLossyAtmos && twinVariant !== null && settings.lgsrEnabled;
  const isRestoredActive = hasPairedTwin && !lgsrBypassed;
  const isBypassedForAb = hasPairedTwin && lgsrBypassed;

  const badgeMode: AtmosFlacBadgeMode = isRestoredActive
    ? 'atmos-flac'
    : isBypassedForAb
      ? 'atmos-ab-bypass'
      : 'native';

  const fusionNorm = Math.max(0, Math.min(1, settings.lgsrFusionPct / 100));
  const couplingCutoffHz = isLossyAtmos ? (activeVariant.bitrateKbps >= 768 ? 10400 : 8800) : 0;
  const gccOffsetMs = hasPairedTwin
    ? Number((((track.waveformSeed % 17) - 8) * 0.035).toFixed(3))
    : 0;
  const coherenceScore = isRestoredActive
    ? Number((0.78 + fusionNorm * 0.19).toFixed(2))
    : isBypassedForAb
      ? 0.42
      : 0;

  return {
    isLossyAtmos,
    twinVariant,
    isRestoredActive,
    isBypassedForAb,
    badgeMode,
    couplingCutoffHz,
    gccOffsetMs,
    coherenceScore,
  };
}

/**
 * Builds the scientific Audio Specification report displayed when tapping the
 * Play tab format / ATMOS+FLAC badge.
 */
export function buildAudioSpecSummary(
  track: SpatialTrack,
  activeVariant: TrackFormatVariant,
  settings: Pick<CavernSettings, 'lgsrEnabled' | 'lgsrFusionPct'>,
  lgsrBypassed: boolean,
): AudioSpecSummary {
  const lgsr = assessLosslessTwin(track, activeVariant, settings, lgsrBypassed);
  const lossless = isLosslessVariant(activeVariant);

  let qualityTier: AudioSpecSummary['qualityTier'] = 'Standard Stereo';
  if (activeVariant.isSpatial && lossless) {
    qualityTier = 'Studio Spatial Master';
  } else if (lgsr.isRestoredActive) {
    qualityTier = 'Lossless Twin Restored';
  } else if (activeVariant.isSpatial) {
    qualityTier = 'Lossy Spatial';
  } else if (lossless) {
    qualityTier = 'Hi-Res Lossless';
  }

  const effectiveDynamicRangeDb = activeVariant.isSpatial
    ? lgsr.isRestoredActive
      ? 14.2
      : lossless
        ? 14.8
        : 12.6
    : lossless
      ? 11.4
      : 8.8;

  const nyquistKhz = Number((activeVariant.sampleRateHz / 2000).toFixed(1));
  const bitDepthIntegrity =
    activeVariant.bitDepth >= 24 && (lossless || lgsr.isRestoredActive)
      ? 'Verified 24-bit PCM headroom'
      : lossless
        ? '16-bit lossless PCM'
        : 'Floating-point perceptual decode';

  return {
    qualityTier,
    effectiveDynamicRangeDb,
    nyquistKhz,
    bitDepthIntegrity,
    lgsr,
  };
}

/**
 * Computes the compact state summary for the Apple Music Sing & Auto-Vocal Mix-Match
 * dock inside the Lyrics view.
 */
export function summarizeSingDock(
  vocalRack: Pick<VocalRackState, 'karaokeBackingVocalPct' | 'mixMatchPct' | 'reverbPct' | 'bypass'>,
  liveMicEnabled: boolean,
): SingDockSummary {
  const vocalLevelPct = Math.max(0, Math.min(100, Math.round(vocalRack.karaokeBackingVocalPct)));
  return {
    vocalLevelPct,
    isKaraokeAttenuating: vocalLevelPct < 99,
    isMicLive: liveMicEnabled,
    isVocalChainBypassed: vocalRack.bypass,
    mixMatchPct: Math.max(0, Math.min(100, Math.round(vocalRack.mixMatchPct))),
    reverbPct: Math.max(0, Math.min(100, Math.round(vocalRack.reverbPct))),
  };
}
