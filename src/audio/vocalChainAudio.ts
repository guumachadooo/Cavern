import type {
  ScarlettHardwareControlState,
  VocalMixDna,
  VocalRackState,
} from '../utils/scarlettVocalDsp';

/**
 * Real-time Web Audio API processor for Focusrite Scarlett preamp audition (Gain, Air Presence,
 * Air Harmonic Drive, Output Mute/Dim) and W11 Live Vocal Chain (HPF, Match EQ, De-Esser,
 * Dynamics Compressor, ADAA-inspired Soft Saturation, and Stereo Slapback Delay).
 */
export class LiveScarlettVocalAudioEngine {
  private ctx: AudioContext | null = null;
  private micStream: MediaStream | null = null;
  private sourceNode: AudioNode | null = null;
  private demoOscs: OscillatorNode[] = [];
  private preampGain: GainNode | null = null;
  private airShelf: BiquadFilterNode | null = null;
  private driveShaper: WaveShaperNode | null = null;
  private hpf: BiquadFilterNode | null = null;
  private matchMid: BiquadFilterNode | null = null;
  private deEsser: BiquadFilterNode | null = null;
  private compressor: DynamicsCompressorNode | null = null;
  private delayNode: DelayNode | null = null;
  private delayWet: GainNode | null = null;
  private dryGain: GainNode | null = null;
  private outputGain: GainNode | null = null;
  private analyser: AnalyserNode | null = null;
  private analyserData: Float32Array<ArrayBuffer> = new Float32Array(new ArrayBuffer(2048));
  private mode: 'off' | 'mic' | 'demo' = 'off';

  private ensureContext(): AudioContext | null {
    if (typeof window === 'undefined') return null;
    const AudioCtx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtx) return null;
    if (!this.ctx) {
      this.ctx = new AudioCtx();
    }
    return this.ctx;
  }

  private buildSoftClipCurve(amount: number): Float32Array<ArrayBuffer> {
    const samples = 256;
    const buf = new ArrayBuffer(samples * 4);
    const curve = new Float32Array(buf);
    const k = Math.max(0.01, amount);
    for (let i = 0; i < samples; i += 1) {
      const x = (i * 2) / (samples - 1) - 1;
      curve[i] = Math.tanh(x * k) / Math.tanh(k);
    }
    return curve;
  }

  private buildGraph(ctx: AudioContext, input: AudioNode): void {
    this.preampGain = ctx.createGain();
    this.airShelf = ctx.createBiquadFilter();
    this.airShelf.type = 'highshelf';
    this.airShelf.frequency.value = 6500;

    this.driveShaper = ctx.createWaveShaper();
    this.driveShaper.oversample = '2x';
    this.driveShaper.curve = this.buildSoftClipCurve(1.0);

    this.hpf = ctx.createBiquadFilter();
    this.hpf.type = 'highpass';
    this.hpf.frequency.value = 92;

    this.matchMid = ctx.createBiquadFilter();
    this.matchMid.type = 'peaking';
    this.matchMid.frequency.value = 2400;
    this.matchMid.Q.value = 1.1;

    this.deEsser = ctx.createBiquadFilter();
    this.deEsser.type = 'peaking';
    this.deEsser.frequency.value = 7200;
    this.deEsser.Q.value = 2.0;

    this.compressor = ctx.createDynamicsCompressor();
    this.compressor.threshold.value = -18;
    this.compressor.knee.value = 8;
    this.compressor.ratio.value = 4;
    this.compressor.attack.value = 0.004;
    this.compressor.release.value = 0.14;

    this.delayNode = ctx.createDelay(0.6);
    this.delayNode.delayTime.value = 0.115;
    this.delayWet = ctx.createGain();
    this.delayWet.gain.value = 0.12;
    this.dryGain = ctx.createGain();
    this.dryGain.gain.value = 1;

    this.outputGain = ctx.createGain();
    this.outputGain.gain.value = 0.25;

    this.analyser = ctx.createAnalyser();
    this.analyser.fftSize = 512;
    this.analyserData = new Float32Array(new ArrayBuffer(this.analyser.fftSize * 4));

    input.connect(this.preampGain);
    this.preampGain.connect(this.analyser);
    this.preampGain.connect(this.airShelf);
    this.airShelf.connect(this.driveShaper);
    this.driveShaper.connect(this.hpf);
    this.hpf.connect(this.matchMid);
    this.matchMid.connect(this.deEsser);
    this.deEsser.connect(this.compressor);

    this.compressor.connect(this.dryGain);
    this.compressor.connect(this.delayNode);
    this.delayNode.connect(this.delayWet);

    this.dryGain.connect(this.outputGain);
    this.delayWet.connect(this.outputGain);
    this.outputGain.connect(ctx.destination);
  }

  public async startMic(
    scarlett: ScarlettHardwareControlState,
    rack: VocalRackState,
    dna: VocalMixDna,
  ): Promise<boolean> {
    this.stop();
    const ctx = this.ensureContext();
    if (!ctx || !navigator.mediaDevices?.getUserMedia) {
      return false;
    }
    try {
      if (ctx.state === 'suspended') {
        await ctx.resume();
      }
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          echoCancellation: false,
          noiseSuppression: false,
          autoGainControl: false,
        },
      });
      this.micStream = stream;
      const src = ctx.createMediaStreamSource(stream);
      this.sourceNode = src;
      this.buildGraph(ctx, src);
      this.mode = 'mic';
      this.updateParameters(scarlett, rack, dna);
      return true;
    } catch {
      this.stop();
      return false;
    }
  }

  public startDemoVoice(
    scarlett: ScarlettHardwareControlState,
    rack: VocalRackState,
    dna: VocalMixDna,
  ): void {
    this.stop();
    const ctx = this.ensureContext();
    if (!ctx) return;
    if (ctx.state === 'suspended') {
      void ctx.resume();
    }
    const merger = ctx.createGain();
    merger.gain.value = 0.08;

    const osc1 = ctx.createOscillator();
    osc1.type = 'sawtooth';
    osc1.frequency.value = 196; // G3 vocal fundamental

    const osc2 = ctx.createOscillator();
    osc2.type = 'triangle';
    osc2.frequency.value = 392;

    const formantFilter = ctx.createBiquadFilter();
    formantFilter.type = 'bandpass';
    formantFilter.frequency.value = 920;
    formantFilter.Q.value = 1.4;

    osc1.connect(formantFilter);
    osc2.connect(formantFilter);
    formantFilter.connect(merger);
    osc1.start();
    osc2.start();

    this.demoOscs = [osc1, osc2];
    this.sourceNode = merger;
    this.buildGraph(ctx, merger);
    this.mode = 'demo';
    this.updateParameters(scarlett, rack, dna);
  }

  public updateParameters(
    scarlett: ScarlettHardwareControlState,
    rack: VocalRackState,
    dna: VocalMixDna,
  ): void {
    if (!this.ctx || this.mode === 'off') return;
    const now = this.ctx.currentTime;
    const ch1 = scarlett.channels[0];

    const gainLinear = Math.pow(10, (ch1.gainDb - 28) / 20);
    this.preampGain?.gain.setTargetAtTime(Math.max(0.05, Math.min(8, gainLinear)), now, 0.03);

    const airBoost = ch1.airMode === 'off' ? 0 : ch1.airMode === 'presence' ? 3.5 : 4.5;
    this.airShelf?.gain.setTargetAtTime(airBoost, now, 0.03);

    const matchScale = rack.enabled && !rack.bypass ? rack.mixMatchPct / 100 : 0;
    const driveAmount =
      1 +
      (ch1.airMode === 'drive' ? 1.3 : 0) +
      matchScale * (rack.warmthPct / 100) * 1.8;
    if (this.driveShaper) {
      this.driveShaper.curve = this.buildSoftClipCurve(driveAmount);
    }

    const midDelta = (dna.matchEqDeltasDb[4] ?? 1.2) * matchScale;
    this.matchMid?.gain.setTargetAtTime(midDelta, now, 0.04);

    const deEssCut = rack.enabled && !rack.bypass && rack.deEsserEnabled ? -3.2 * matchScale : 0;
    this.deEsser?.gain.setTargetAtTime(deEssCut, now, 0.04);

    if (this.compressor) {
      const dynActive = rack.enabled && !rack.bypass && rack.dynamicsPct > 5;
      this.compressor.ratio.setTargetAtTime(dynActive ? 1 + (rack.dynamicsPct / 100) * 3.5 : 1, now, 0.04);
      this.compressor.threshold.setTargetAtTime(dynActive ? -20 : 0, now, 0.04);
    }

    if (this.delayNode && this.delayWet) {
      this.delayNode.delayTime.setTargetAtTime(Math.max(0.03, Math.min(0.45, dna.delayTimeMs / 1000)), now, 0.04);
      const wet =
        rack.enabled && !rack.bypass
          ? Math.min(0.45, ((dna.reverbWetPct + dna.delayWetPct) / 100) * (rack.reverbPct / 100) * matchScale)
          : 0;
      this.delayWet.gain.setTargetAtTime(wet, now, 0.04);
    }

    if (this.outputGain) {
      const level = scarlett.outputMute
        ? 0
        : (scarlett.outputLevelPct / 100) *
          (scarlett.outputDim ? 0.1 : 1) *
          Math.pow(10, rack.vocalLevelDb / 20) *
          (this.mode === 'demo' ? 0.35 : 0.75);
      this.outputGain.gain.setTargetAtTime(Math.max(0, Math.min(1.2, level)), now, 0.03);
    }
  }

  public readInputPeakDbfs(): number | null {
    if (!this.analyser || this.mode === 'off') return null;
    this.analyser.getFloatTimeDomainData(this.analyserData);
    let peak = 1e-6;
    for (let i = 0; i < this.analyserData.length; i += 1) {
      const v = Math.abs(this.analyserData[i]);
      if (v > peak) peak = v;
    }
    return Number(Math.max(-96, Math.min(0, 20 * Math.log10(peak))).toFixed(1));
  }

  public getMode(): 'off' | 'mic' | 'demo' {
    return this.mode;
  }

  public stop(): void {
    this.mode = 'off';
    for (const osc of this.demoOscs) {
      try {
        osc.stop();
        osc.disconnect();
      } catch {
        // Ignore
      }
    }
    this.demoOscs = [];
    if (this.micStream) {
      for (const tr of this.micStream.getTracks()) {
        tr.stop();
      }
      this.micStream = null;
    }
    try {
      this.sourceNode?.disconnect();
      this.outputGain?.disconnect();
    } catch {
      // Ignore
    }
    this.sourceNode = null;
    this.analyser = null;
  }
}
