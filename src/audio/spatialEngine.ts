import { HRIR_PROFILES } from '../data/catalog';
import type {
  CavernSettings,
  PostRenderReport,
  SpatialSourceNode,
  SpeakerLayout,
  TrackFormatVariant,
  Vec3,
} from '../types/cavern';
import {
  clamp,
  computeListenerOrientation,
  computeRolloffGain,
  distance3D,
  encodeWavPcm16,
  evaluateSourcePosition,
  isSourceActiveForVariant,
  transformToListenerFrame,
} from '../utils/spatialMath';

interface ActiveVoice {
  osc: OscillatorNode;
  subOsc: OscillatorNode;
  gain: GainNode;
  delay: DelayNode;
  panner: PannerNode;
}

/**
 * Real Web Audio API spatial DSP engine.
 * Synthesizes multi-object spatial audio for demo compositions or decodes & spatializes
 * user-loaded local audio files using HRTF PannerNodes, LFE boost, and QuickEQ filters.
 */
export class CavernSpatialAudioEngine {
  private ctx: AudioContext | null = null;
  private mixBus: GainNode | null = null;
  private masterGain: GainNode | null = null;
  private profileToneFilter: BiquadFilterNode | null = null;
  private limiter: DynamicsCompressorNode | null = null;
  private voices = new Map<string, ActiveVoice>();
  private fileSource: AudioBufferSourceNode | null = null;
  private filePanner: PannerNode | null = null;
  private fileGain: GainNode | null = null;
  private eqNodes: BiquadFilterNode[] = [];
  private isPlaying = false;
  private fileStartOffset = 0;

  private ensureContext(): AudioContext | null {
    if (typeof window === 'undefined') return null;
    const AudioCtx = window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtx) return null;

    if (!this.ctx) {
      this.ctx = new AudioCtx();
      this.mixBus = this.ctx.createGain();
      this.mixBus.gain.value = 1;

      this.profileToneFilter = this.ctx.createBiquadFilter();
      this.profileToneFilter.type = 'lowpass';
      this.profileToneFilter.frequency.value = 11500;

      this.limiter = this.ctx.createDynamicsCompressor();
      this.limiter.threshold.value = -2;
      this.limiter.knee.value = 6;
      this.limiter.ratio.value = 12;
      this.limiter.attack.value = 0.003;
      this.limiter.release.value = 0.12;

      this.masterGain = this.ctx.createGain();
      this.masterGain.gain.value = 0.45;

      this.mixBus.connect(this.profileToneFilter);
      this.profileToneFilter.connect(this.limiter);
      this.limiter.connect(this.masterGain);
      this.masterGain.connect(this.ctx.destination);
    }
    return this.ctx;
  }

  public async decodeAudioFile(file: File): Promise<AudioBuffer | null> {
    const ctx = this.ensureContext();
    if (!ctx) return null;
    const arrayBuffer = await file.arrayBuffer();
    return ctx.decodeAudioData(arrayBuffer);
  }

  public start(
    sources: SpatialSourceNode[],
    listenerPos: Vec3,
    listenerYawDeg: number,
    listenerPitchDeg: number,
    masterVol: number,
    lfeBoostDb: number,
    settings: CavernSettings,
    activeVariant: Pick<TrackFormatVariant, 'isSpatial' | 'objectCount'>,
    isLooping: boolean,
    localBuffer?: AudioBuffer,
    offsetSec = 0,
  ): void {
    const ctx = this.ensureContext();
    if (!ctx || !this.profileToneFilter || !this.mixBus) return;
    if (ctx.state === 'suspended') {
      void ctx.resume();
    }

    this.stop();
    this.isPlaying = true;
    this.rebuildEqChain(settings);

    const destinationNode = this.mixBus;

    if (localBuffer) {
      const srcNode = ctx.createBufferSource();
      srcNode.buffer = localBuffer;
      srcNode.loop = isLooping;

      const panner = ctx.createPanner();
      panner.panningModel = settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0' ? 'HRTF' : 'equalpower';
      panner.distanceModel = 'inverse';
      panner.refDistance = 1.5;
      panner.positionX.value = 0;
      panner.positionY.value = settings.cavernize3D ? 1.1 : 0;
      panner.positionZ.value = 3.2;

      const gain = ctx.createGain();
      gain.gain.value = 0.85;

      srcNode.connect(panner);
      panner.connect(gain);
      gain.connect(destinationNode);

      const safeOffset = offsetSec % Math.max(0.1, localBuffer.duration);
      this.fileStartOffset = safeOffset;
      srcNode.start(0, safeOffset);

      this.fileSource = srcNode;
      this.filePanner = panner;
      this.fileGain = gain;
    } else {
      for (const src of sources) {
        const osc = ctx.createOscillator();
        const subOsc = ctx.createOscillator();
        const gain = ctx.createGain();
        const delay = ctx.createDelay(0.12);
        const panner = ctx.createPanner();

        osc.type = src.kind === 'lfe' ? 'sine' : src.kind === 'object' || src.kind === 'mic' ? 'triangle' : 'sine';
        osc.frequency.value = src.synthFreqHz;
        subOsc.type = 'sine';
        subOsc.frequency.value = src.synthFreqHz * (src.kind === 'lfe' ? 0.5 : 1.5);

        panner.panningModel = settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0' ? 'HRTF' : 'equalpower';
        panner.distanceModel = 'inverse';
        panner.refDistance = 1.5;
        panner.maxDistance = 16;

        gain.gain.value = 0.0001;
        delay.delayTime.value = 0;

        osc.connect(gain);
        subOsc.connect(gain);
        gain.connect(delay);
        delay.connect(panner);
        panner.connect(destinationNode);

        osc.start();
        subOsc.start();

        this.voices.set(src.id, { osc, subOsc, gain, delay, panner });
      }
    }

    this.updateFrame(
      sources,
      listenerPos,
      listenerYawDeg,
      listenerPitchDeg,
      masterVol,
      lfeBoostDb,
      settings,
      activeVariant,
      offsetSec,
    );
  }

  public updateEq(settings: CavernSettings): void {
    this.rebuildEqChain(settings);
  }

  private rebuildEqChain(settings: CavernSettings): void {
    if (!this.ctx || !this.profileToneFilter || !this.mixBus) return;
    this.mixBus.disconnect();
    for (const node of this.eqNodes) {
      node.disconnect();
    }
    this.eqNodes = [];

    if (settings.quickEqFilters.length === 0) {
      this.mixBus.connect(this.profileToneFilter);
      return;
    }

    let prev: AudioNode = this.mixBus;
    for (const f of settings.quickEqFilters.slice(0, 8)) {
      const biquad = this.ctx.createBiquadFilter();
      biquad.type =
        f.type === 'LSC'
          ? 'lowshelf'
          : f.type === 'HSC'
            ? 'highshelf'
            : f.type === 'LP'
              ? 'lowpass'
              : f.type === 'HP'
                ? 'highpass'
                : 'peaking';
      biquad.frequency.value = clamp(f.freqHz, 20, 20000);
      biquad.gain.value = clamp(f.gainDb, -18, 18);
      biquad.Q.value = clamp(f.q, 0.2, 12);
      prev.connect(biquad);
      prev = biquad;
      this.eqNodes.push(biquad);
    }

    prev.connect(this.profileToneFilter);
  }

  public updateFrame(
    sources: SpatialSourceNode[],
    listenerPos: Vec3,
    listenerYawDeg: number,
    listenerPitchDeg: number,
    masterVol: number,
    lfeBoostDb: number,
    settings: CavernSettings,
    activeVariant: Pick<TrackFormatVariant, 'isSpatial' | 'objectCount'>,
    playbackTimeSec: number,
  ): void {
    if (!this.ctx || !this.masterGain || !this.profileToneFilter || !this.isPlaying) {
      return;
    }

    const now = this.ctx.currentTime;
    const profile = HRIR_PROFILES.find((p) => p.id === settings.hrirProfileId) ?? HRIR_PROFILES[0];
    this.profileToneFilter.frequency.setTargetAtTime(profile.filterToneHz, now, 0.04);

    const renderGainLinear = Math.pow(10, settings.renderGainDb / 20);
    const masterLevel = clamp(masterVol * 0.52 * renderGainLinear, 0, 1.2);
    this.masterGain.gain.setTargetAtTime(masterLevel, now, 0.03);

    const listener = this.ctx.listener;
    const { forward, up } = computeListenerOrientation(listenerYawDeg, listenerPitchDeg);
    if (listener.positionX) {
      listener.positionX.setTargetAtTime(listenerPos.x, now, 0.02);
      listener.positionY.setTargetAtTime(listenerPos.y, now, 0.02);
      listener.positionZ.setTargetAtTime(-listenerPos.z, now, 0.02);
      listener.forwardX.setTargetAtTime(forward.x, now, 0.02);
      listener.forwardY.setTargetAtTime(forward.y, now, 0.02);
      listener.forwardZ.setTargetAtTime(-forward.z, now, 0.02);
      listener.upX.setTargetAtTime(up.x, now, 0.02);
      listener.upY.setTargetAtTime(up.y, now, 0.02);
      listener.upZ.setTargetAtTime(-up.z, now, 0.02);
    }

    const anySolo = sources.some((s) => s.solo);
    const lfeLinear = Math.pow(10, lfeBoostDb / 20);
    const useHrtf = settings.headphoneVirtualizer && settings.hrirProfileId !== 'direct_2_0';

    if (this.filePanner && this.fileGain) {
      this.filePanner.panningModel = useHrtf ? 'HRTF' : 'equalpower';
      const dist = distance3D(listenerPos, { x: 0, y: settings.cavernize3D ? 1.1 : 0, z: 3.2 });
      const roll = settings.distanceSimulation ? computeRolloffGain(dist, settings.rolloffMode) : 1;
      this.fileGain.gain.setTargetAtTime(0.85 * roll, now, 0.04);
      return;
    }

    for (const src of sources) {
      const voice = this.voices.get(src.id);
      if (!voice) continue;

      voice.panner.panningModel = useHrtf ? 'HRTF' : 'equalpower';
      voice.panner.positionX.setTargetAtTime(src.position.x, now, 0.03);
      voice.panner.positionY.setTargetAtTime(
        src.position.y * (1 - settings.heightSqueezePct / 150),
        now,
        0.03,
      );
      voice.panner.positionZ.setTargetAtTime(-src.position.z, now, 0.03);

      const activeByFormat = isSourceActiveForVariant(src, activeVariant, settings);
      const audible = activeByFormat && !src.mute && (!anySolo || src.solo);
      const dist = distance3D(listenerPos, src.position);
      const rolloff = settings.distanceSimulation ? computeRolloffGain(dist, settings.rolloffMode) : 1;

      // Physical 6DoF acoustic time-of-flight propagation delay at 343 m/s (skipped for omnidirectional LFE)
      const propagationDelaySec =
        settings.propagationDelay6DoF && src.kind !== 'lfe'
          ? clamp(dist / 343.0, 0, 0.1)
          : 0;
      voice.delay.delayTime.setTargetAtTime(propagationDelaySec, now, 0.025);

      // Gentle rhythmic harmonic pulse so the room feels alive and musical
      const pulse = 0.55 + 0.45 * Math.sin(playbackTimeSec * (src.kind === 'object' ? 1.8 : 0.7) + src.synthFreqHz * 0.01);
      let baseGain =
        src.kind === 'object'
          ? 0.042
          : src.kind === 'lfe'
            ? 0.055 * lfeLinear
            : src.kind === 'stem' || src.kind === 'mic'
              ? 0.018
              : 0.024;
      if (src.kind === 'height' && !settings.cavernize3D) {
        baseGain *= 0.65;
      }

      const targetGain = audible ? baseGain * rolloff * pulse : 0.00001;
      voice.gain.gain.setTargetAtTime(targetGain, now, 0.04);
    }
  }

  public stop(): void {
    this.isPlaying = false;
    for (const voice of this.voices.values()) {
      try {
        voice.osc.stop();
        voice.subOsc.stop();
        voice.osc.disconnect();
        voice.subOsc.disconnect();
        voice.gain.disconnect();
        voice.delay.disconnect();
        voice.panner.disconnect();
      } catch {
        // Ignore already-stopped nodes
      }
    }
    this.voices.clear();

    if (this.fileSource) {
      try {
        this.fileSource.stop();
        this.fileSource.disconnect();
      } catch {
        // Ignore
      }
      this.fileSource = null;
    }
    if (this.filePanner) {
      this.filePanner.disconnect();
      this.filePanner = null;
    }
    if (this.fileGain) {
      this.fileGain.disconnect();
      this.fileGain = null;
    }
  }

  public getLastFileOffset(): number {
    return this.fileStartOffset;
  }
}

/**
 * Generates a real downloadable multi-channel or binaural WAV export + Post-Render Report.
 */
export function renderSpatialMixWav(
  trackTitle: string,
  layout: SpeakerLayout,
  outputCodec: string,
  container: string,
  sources: SpatialSourceNode[],
  listenerPos: Vec3,
  listenerYawDeg: number,
  lfeBoostDb: number,
  bakeRoomState: boolean,
  rolloffMode: CavernSettings['rolloffMode'],
): PostRenderReport {
  const sampleRateHz = 48000;
  const durationSec = 4;
  const numFrames = sampleRateHz * durationSec;
  const outChannels = Math.min(layout.channelCount, 8);
  const buffers = Array.from({ length: outChannels }, () => new Float32Array(numFrames));

  const anySolo = bakeRoomState && sources.some((s) => s.solo);
  const activeSources = sources.filter((s) => !bakeRoomState || (!s.mute && (!anySolo || s.solo)));
  const lfeLinear = Math.pow(10, lfeBoostDb / 20);

  let peakAbs = 0.0001;
  let sumSquares = 0;

  for (let i = 0; i < numFrames; i += 1) {
    const t = i / sampleRateHz;
    const env = Math.min(1, t * 4) * Math.min(1, (durationSec - t) * 3);

    for (let sIdx = 0; sIdx < activeSources.length; sIdx += 1) {
      const src = activeSources[sIdx];
      const pos = evaluateSourcePosition(src, t);
      const dist = bakeRoomState ? distance3D(listenerPos, pos) : distance3D({ x: 0, y: 0, z: 0 }, pos);
      const atten = bakeRoomState ? computeRolloffGain(dist, rolloffMode) : 1;
      const lfeGain = src.kind === 'lfe' ? lfeLinear : 1;
      const sample = Math.sin(2 * Math.PI * src.synthFreqHz * t) * 0.075 * atten * lfeGain * env;

      if (outChannels === 2) {
        const rel = bakeRoomState
          ? transformToListenerFrame(pos, listenerPos, listenerYawDeg)
          : pos;
        const pan = clamp(rel.x / 5, -1, 1);
        const leftGain = Math.cos(((pan + 1) * Math.PI) / 4);
        const rightGain = Math.sin(((pan + 1) * Math.PI) / 4);
        buffers[0][i] += sample * leftGain;
        buffers[1][i] += sample * rightGain;
      } else {
        const chIdx = sIdx % outChannels;
        buffers[chIdx][i] += sample;
      }
    }

    for (let ch = 0; ch < outChannels; ch += 1) {
      const val = clamp(buffers[ch][i], -0.98, 0.98);
      buffers[ch][i] = val;
      const absVal = Math.abs(val);
      if (absVal > peakAbs) peakAbs = absVal;
      sumSquares += val * val;
    }
  }

  const rms = Math.sqrt(sumSquares / Math.max(1, numFrames * outChannels));
  const peakDbfs = Number((20 * Math.log10(Math.max(1e-5, peakAbs))).toFixed(2));
  const rmsLufs = Number((20 * Math.log10(Math.max(1e-5, rms)) - 0.691).toFixed(2));
  const headroomDb = Number(Math.max(0, -peakDbfs).toFixed(2));

  const wavBytes = encodeWavPcm16(buffers, sampleRateHz);
  const blob = new Blob([wavBytes], { type: 'audio/wav' });
  const downloadUrl = URL.createObjectURL(blob);
  const safeName = trackTitle.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  const downloadFileName = `${safeName || 'cavernize-mix'}-${layout.id}.wav`;

  return {
    trackTitle,
    targetLayout: layout.name,
    outputCodec,
    container,
    channels: layout.channelCount,
    sampleRateHz,
    bitDepth: outputCodec.includes('24') ? 24 : outputCodec.includes('32') ? 32 : 16,
    durationSec,
    peakDbfs,
    rmsLufs,
    headroomDb,
    bakedRoomPosition: bakeRoomState,
    bakedSoloMuteCount: sources.filter((s) => s.solo || s.mute).length,
    fileSizeKb: Math.round(wavBytes.byteLength / 1024),
    downloadUrl,
    downloadFileName,
  };
}

/**
 * Synthesizes an isolated mono or 3D-spatialized stereo WAV stem for a specific bed channel or 3D object.
 */
export function renderStemWav(
  source: SpatialSourceNode,
  mode: 'mono-stems' | 'spatial-each' = 'mono-stems',
): { url: string; fileName: string; sizeKb: number; channelsLabel: string } {
  const sampleRateHz = 48000;
  const durationSec = 3;
  const numFrames = sampleRateHz * durationSec;

  if (mode === 'spatial-each') {
    const left = new Float32Array(numFrames);
    const right = new Float32Array(numFrames);
    for (let i = 0; i < numFrames; i += 1) {
      const t = i / sampleRateHz;
      const env = Math.min(1, t * 5) * Math.min(1, (durationSec - t) * 4);
      const pos = evaluateSourcePosition(source, t);
      const pan = clamp(pos.x / 5, -1, 1);
      const leftGain = Math.cos(((pan + 1) * Math.PI) / 4);
      const rightGain = Math.sin(((pan + 1) * Math.PI) / 4);
      const sig = Math.sin(2 * Math.PI * source.synthFreqHz * t) * 0.35 * env;
      left[i] = sig * leftGain;
      right[i] = sig * rightGain;
    }
    const wavBytes = encodeWavPcm16([left, right], sampleRateHz);
    const blob = new Blob([wavBytes], { type: 'audio/wav' });
    return {
      url: URL.createObjectURL(blob),
      fileName: `stem-${source.id.toLowerCase()}-3d.wav`,
      sizeKb: Math.round(wavBytes.byteLength / 1024),
      channelsLabel: '2ch HRTF',
    };
  }

  const mono = new Float32Array(numFrames);
  for (let i = 0; i < numFrames; i += 1) {
    const t = i / sampleRateHz;
    const env = Math.min(1, t * 5) * Math.min(1, (durationSec - t) * 4);
    mono[i] = Math.sin(2 * Math.PI * source.synthFreqHz * t) * 0.35 * env;
  }

  const wavBytes = encodeWavPcm16([mono], sampleRateHz);
  const blob = new Blob([wavBytes], { type: 'audio/wav' });
  return {
    url: URL.createObjectURL(blob),
    fileName: `stem-${source.id.toLowerCase()}.wav`,
    sizeKb: Math.round(wavBytes.byteLength / 1024),
    channelsLabel: '1ch Mono',
  };
}

/**
 * Synthesizes all selected 3D objects/channels combined into a single spatialized stereo WAV file.
 */
export function renderCombinedStemsWav(
  trackTitle: string,
  sources: SpatialSourceNode[],
): { url: string; fileName: string; sizeKb: number; channelsLabel: string } {
  const sampleRateHz = 48000;
  const durationSec = 4;
  const numFrames = sampleRateHz * durationSec;
  const left = new Float32Array(numFrames);
  const right = new Float32Array(numFrames);
  const scale = 0.45 / Math.max(1, Math.sqrt(sources.length));

  for (let i = 0; i < numFrames; i += 1) {
    const t = i / sampleRateHz;
    const env = Math.min(1, t * 5) * Math.min(1, (durationSec - t) * 4);
    let lSum = 0;
    let rSum = 0;
    for (let s = 0; s < sources.length; s += 1) {
      const src = sources[s];
      const pos = evaluateSourcePosition(src, t);
      const pan = clamp(pos.x / 5, -1, 1);
      const sig = Math.sin(2 * Math.PI * src.synthFreqHz * t) * scale * env;
      lSum += sig * Math.cos(((pan + 1) * Math.PI) / 4);
      rSum += sig * Math.sin(((pan + 1) * Math.PI) / 4);
    }
    left[i] = clamp(lSum, -0.98, 0.98);
    right[i] = clamp(rSum, -0.98, 0.98);
  }

  const wavBytes = encodeWavPcm16([left, right], sampleRateHz);
  const blob = new Blob([wavBytes], { type: 'audio/wav' });
  const safeName = trackTitle.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  return {
    url: URL.createObjectURL(blob),
    fileName: `stem-${safeName || 'cavern'}-3d-combined-${sources.length}obj.wav`,
    sizeKb: Math.round(wavBytes.byteLength / 1024),
    channelsLabel: `2ch Combined (${sources.length})`,
  };
}
