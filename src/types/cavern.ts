export type TabId = 'play' | 'room' | 'studio' | 'stems' | 'settings';

export type Locale = 'en-US' | 'pt-BR';

export type SourceKind = 'bed' | 'lfe' | 'height' | 'object';

export type RolloffMode = 'disabled' | 'logarithmic' | 'linear' | 'real';

export type CameraPreset = 'iso' | 'top' | 'front';

export type LabelVisibilityMode = 'active' | 'all' | 'minimal';

export interface Vec3 {
  x: number;
  y: number;
  z: number;
}

export interface LyricLine {
  timeSec: number;
  text: string;
}

export interface TrackFormatVariant {
  id: string;
  label: string;
  codec: string;
  container: string;
  channels: string;
  sampleRateHz: number;
  bitDepth: number;
  bitrateKbps: number;
  isSpatial: boolean;
  objectCount: number;
}

export interface SpatialTrack {
  id: string;
  title: string;
  artist: string;
  album: string;
  discNumber: number;
  trackNumber: number;
  durationSec: number;
  folder: string;
  isSpatial: boolean;
  isNonMusicNoise?: boolean;
  accentHue: number;
  secondaryHue: number;
  waveformSeed: number;
  variants: TrackFormatVariant[];
  lyrics: LyricLine[];
  /** Optional decoded audio buffer for user-uploaded local files */
  localBuffer?: AudioBuffer;
  localFileName?: string;
}

export interface SpatialSourceNode {
  id: string;
  shortLabel: string;
  name: string;
  kind: SourceKind;
  basePosition: Vec3;
  position: Vec3;
  rms: number;
  solo: boolean;
  mute: boolean;
  /** Frequency in Hz used by the built-in multi-object synthesizer */
  synthFreqHz: number;
  /** Orbit recipe for dynamic 3D objects */
  orbit?: {
    radiusX: number;
    radiusZ: number;
    centerY: number;
    ampY: number;
    speedRad: number;
    phase: number;
  };
}

export interface HrirProfile {
  id: string;
  name: string;
  dataset: 'SADIE II D1' | 'TH Köln KU100' | 'SADIE II D2' | 'Cavern' | 'Custom';
  manikin: string;
  distance: string;
  reverbRt60: string;
  fileName: string;
  sampleRateHz: number;
  filterToneHz: number;
  roomWetMix: number;
}

export interface SpeakerChannelSpec {
  id: string;
  label: string;
  name: string;
  kind: 'bed' | 'lfe' | 'height';
  azimuthDeg: number;
  elevationDeg: number;
  outputPort: string;
  matrixNote?: string;
}

export interface SpeakerLayout {
  id: string;
  name: string;
  category: 'pc' | 'matrix' | 'multichannel';
  channelCount: number;
  description: string;
  speakers: SpeakerChannelSpec[];
}

export interface RenderQueueItem {
  id: string;
  trackTitle: string;
  sourceFormat: string;
  targetLayoutId: string;
  targetLayoutName: string;
  outputCodec: string;
  container: string;
  bakeRoomState: boolean;
  status: 'queued' | 'rendering' | 'done';
  progress: number;
}

export interface PostRenderReport {
  trackTitle: string;
  targetLayout: string;
  outputCodec: string;
  container: string;
  channels: number;
  sampleRateHz: number;
  bitDepth: number;
  durationSec: number;
  peakDbfs: number;
  rmsLufs: number;
  headroomDb: number;
  bakedRoomPosition: boolean;
  bakedSoloMuteCount: number;
  fileSizeKb: number;
  downloadUrl: string;
  downloadFileName: string;
}

export interface QuickEqFilter {
  type: 'PK' | 'LSC' | 'HSC' | 'LP' | 'HP';
  freqHz: number;
  gainDb: number;
  q: number;
}

export interface CavernSettings {
  locale: Locale;
  hrirProfileId: string;
  customHrirName: string | null;
  headphoneVirtualizer: boolean;
  distanceSimulation: boolean;
  rolloffMode: RolloffMode;
  matrixUpmix: boolean;
  cavernize3D: boolean;
  upmixEffectPct: number;
  upmixSmoothPct: number;
  speakerHeightVirt: boolean;
  dynamicNormalizer: boolean;
  limiterOnly: boolean;
  renderGainDb: number;
  heightSqueezePct: number;
  roomSize: Vec3;
  quickEqFilters: QuickEqFilter[];
  quickEqFileName: string | null;
}
