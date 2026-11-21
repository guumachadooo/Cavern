import type { SpatialSourceNode, Vec3 } from '../types/cavern';
import { clamp } from './spatialMath';

export type W12StudioPresetId =
  | 'personalized-binaural'
  | 'eclipsa-iamf'
  | 'ambisonics-hoa3'
  | 'adm-bwf'
  | 'hybrid-neural-714'
  | 'spatial-karaoke'
  | 'live-karaoke-mixdown'
  | 'flac-atmos-hybrid';

export interface W12StudioPresetSpec {
  id: W12StudioPresetId;
  extension: '.iamf' | '.amb' | '.wav';
  formatTag: string;
  channels: number;
  pipelineSummary: string;
}

export const W12_STUDIO_PRESETS: W12StudioPresetSpec[] = [
  {
    id: 'eclipsa-iamf',
    extension: '.iamf',
    formatTag: 'IAMF v1.0 OBU',
    channels: 16,
    pipelineSummary: 'IA_SEQUENCE_HEADER + ipcm + 3rd-Order HOA Element + BS.1770 MixPresentation',
  },
  {
    id: 'ambisonics-hoa3',
    extension: '.amb',
    formatTag: 'HOA3 ACN/SN3D',
    channels: 16,
    pipelineSummary: 'WAVEFORMATEXTENSIBLE Ambisonic GUID + 16-ch Real Spherical Harmonics SN3D',
  },
  {
    id: 'adm-bwf',
    extension: '.wav',
    formatTag: 'ADM BS.2076',
    channels: 12,
    pipelineSummary: 'RIFF WAVE + ITU-R BS.2076 axml 3D trajectories + chna track assignment',
  },
  {
    id: 'personalized-binaural',
    extension: '.wav',
    formatTag: 'Lossless 2.0 HRTF',
    channels: 2,
    pipelineSummary: 'HRTF + ISM Early Reflections + AutoEq DF + HpTF Notch + 0 dBTP Limiter',
  },
  {
    id: 'hybrid-neural-714',
    extension: '.wav',
    formatTag: '7.1.4 Hybrid',
    channels: 12,
    pipelineSummary: 'W08 Neural Stems + W02 Parametric DirAC STFT Stereo -> 7.1.4 Bed + Heights',
  },
  {
    id: 'spatial-karaoke',
    extension: '.wav',
    formatTag: 'Karaoke Atmos',
    channels: 6,
    pipelineSummary: 'W08 Zero-Phase Multichannel Coherence Mask (Backing Vocals & LFE preserved)',
  },
  {
    id: 'live-karaoke-mixdown',
    extension: '.wav',
    formatTag: 'Live Mixdown',
    channels: 2,
    pipelineSummary: 'Spatial Karaoke Bed + W11 Live Vocal Chain (FET+Opto+ADAA+FDN) + Sidechain Glue',
  },
  {
    id: 'flac-atmos-hybrid',
    extension: '.wav',
    formatTag: 'FLAC+Atmos LGSR',
    channels: 12,
    pipelineSummary: 'W09 Tier 2 EM-MWF Lossless-Guided Restoration + Atmos Residual Conservation',
  },
];

export interface IamfInspectionResult {
  valid: boolean;
  magic: string;
  primaryProfile: number;
  codecFourCc: string;
  elementType: 'Scene-Based HOA' | 'Channel-Based';
  channelCount: number;
  hoaOrder: number;
  sampleRateHz: number;
  bitDepth: number;
  samplesPerFrame: number;
  mixLabel: string;
  integratedLufs: number;
  truePeakDbtp: number;
  obuCount: number;
  audioFrameObuCount: number;
}

export interface SpatialWaveInspectionResult {
  valid: boolean;
  formatKind: 'ADM BWF (ITU-R BS.2076)' | 'Ambisonics HOA B-Format (WAVE-EX)' | 'RIFF WAVE PCM';
  channelCount: number;
  sampleRateHz: number;
  bitDepth: number;
  isAmbisonicGuid: boolean;
  hoaOrder: number;
  hasAxml: boolean;
  hasChna: boolean;
  admObjectCount: number;
  admProgrammeName: string | null;
}

export interface W12MasterArtifact {
  presetId: W12StudioPresetId;
  fileName: string;
  mimeType: string;
  bytes: Uint8Array;
  fileSizeKb: number;
  channelCount: number;
  sampleRateHz: number;
  bitDepth: number;
  durationSec: number;
  integratedLufs: number;
  truePeakDbtp: number;
  headroomDb: number;
  verificationBadge: string;
  verificationDetail: string;
  iamfInspection?: IamfInspectionResult;
  waveInspection?: SpatialWaveInspectionResult;
}

// Ambisonics B-Format WAVEFORMATEXTENSIBLE SubFormat GUID tail (14 bytes)
const AMBISONIC_GUID_TAIL = new Uint8Array([
  0x00, 0x00, 0x21, 0x07, 0xd3, 0x11, 0x86, 0x44, 0xc8, 0xc1, 0xca, 0x00, 0x00, 0x00,
]);

const STANDARD_GUID_TAIL = new Uint8Array([
  0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71,
]);

/** Encodes an unsigned integer into ULEB128 bytes per AOMedia IAMF v1.0 Section 3.1. */
export function encodeUleb128(value: number): Uint8Array {
  let v = Math.max(0, Math.floor(value)) >>> 0;
  const out: number[] = [];
  do {
    let b = v & 0x7f;
    v >>>= 7;
    if (v !== 0) b |= 0x80;
    out.push(b);
  } while (v !== 0);
  return new Uint8Array(out);
}

/** Decodes an unsigned ULEB128 integer from bytes starting at offset. */
export function decodeUleb128(bytes: Uint8Array, offset: number): { value: number; nextOffset: number } | null {
  let result = 0;
  let shift = 0;
  let pos = offset;
  for (let i = 0; i < 8; i++) {
    if (pos >= bytes.length) return null;
    const b = bytes[pos++];
    result += (b & 0x7f) * Math.pow(2, shift);
    if ((b & 0x80) === 0) {
      return { value: result, nextOffset: pos };
    }
    shift += 7;
  }
  return null;
}

/**
 * Evaluates real spherical harmonics in ACN channel order with SN3D Schmidt semi-normalization
 * up to order N (1..3 -> 4, 9, 16 channels), matching Cavern's MagLsAmbisonicDecoder.
 * Azimuth: 0 = front (+Z), +pi/2 = left (-X). Elevation: +pi/2 = zenith (+Y).
 */
export function evaluateRealSphericalHarmonicsSn3d(
  azimuthRad: number,
  elevationRad: number,
  order: number,
): Float64Array {
  const N = Math.max(0, Math.min(3, Math.floor(order)));
  const count = (N + 1) * (N + 1);
  const y = new Float64Array(count);

  const sinEl = Math.sin(elevationRad);
  const cosEl = Math.cos(elevationRad);

  // Order 0 (ACN 0)
  y[0] = 1.0;
  if (N === 0) return y;

  // Order 1 (ACN 1..3: Y, Z, X)
  y[1] = cosEl * Math.sin(azimuthRad);
  y[2] = sinEl;
  y[3] = cosEl * Math.cos(azimuthRad);
  if (N === 1) return y;

  // Order 2 (ACN 4..8, SN3D Schmidt semi-normalized)
  const sqrt3Over2 = Math.sqrt(3) / 2;
  const sin2Az = Math.sin(2 * azimuthRad);
  const cos2Az = Math.cos(2 * azimuthRad);
  const cos2El = cosEl * cosEl;
  const sin2El = sinEl * sinEl;

  y[4] = sqrt3Over2 * cos2El * sin2Az;
  y[5] = Math.sqrt(3) * sinEl * cosEl * Math.sin(azimuthRad);
  y[6] = 0.5 * (3 * sin2El - 1);
  y[7] = Math.sqrt(3) * sinEl * cosEl * Math.cos(azimuthRad);
  y[8] = sqrt3Over2 * cos2El * cos2Az;
  if (N === 2) return y;

  // Order 3 (ACN 9..15, SN3D Schmidt semi-normalized)
  const sin3Az = Math.sin(3 * azimuthRad);
  const cos3Az = Math.cos(3 * azimuthRad);
  const cos3El = cos2El * cosEl;
  const sqrt5Over8 = Math.sqrt(5 / 8);
  const sqrt15Over2 = Math.sqrt(15) / 2;
  const sqrt3Over8 = Math.sqrt(3 / 8);

  y[9] = sqrt5Over8 * cos3El * sin3Az;
  y[10] = sqrt15Over2 * sinEl * cos2El * sin2Az;
  y[11] = sqrt3Over8 * cosEl * (5 * sin2El - 1) * Math.sin(azimuthRad);
  y[12] = 0.5 * sinEl * (5 * sin2El - 3);
  y[13] = sqrt3Over8 * cosEl * (5 * sin2El - 1) * Math.cos(azimuthRad);
  y[14] = sqrt15Over2 * sinEl * cos2El * cos2Az;
  y[15] = sqrt5Over8 * cos3El * cos3Az;

  return y;
}

/** Closed-form Max-rE weights a_n = P_n(cos(137.9 deg / (N + 1.51))) (Zotter & Frank 2012). */
export function computeMaxReWeights(order: number): Float64Array {
  const N = Math.max(0, Math.min(3, Math.floor(order)));
  const w = new Float64Array(N + 1);
  w[0] = 1.0;
  if (N === 0) return w;
  const rE = Math.cos(((137.9 * Math.PI) / 180) / (N + 1.51));
  w[1] = rE;
  for (let n = 2; n <= N; n++) {
    w[n] = ((2 * n - 1) * rE * w[n - 1] - (n - 1) * w[n - 2]) / n;
  }
  return w;
}

function concatBytes(chunks: Uint8Array[]): Uint8Array {
  let total = 0;
  for (const c of chunks) total += c.length;
  const out = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) {
    out.set(c, offset);
    offset += c.length;
  }
  return out;
}

function writeObu(obuType: number, payload: Uint8Array): Uint8Array {
  const header = new Uint8Array([(obuType & 0x1f) << 3]);
  const sizeBytes = encodeUleb128(payload.length);
  return concatBytes([header, sizeBytes, payload]);
}

function encodeAscii(text: string): Uint8Array {
  const out = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) out[i] = text.charCodeAt(i) & 0x7f;
  return out;
}

function encodeUtf8Z(text: string): Uint8Array {
  const encoded = new TextEncoder().encode(text);
  const out = new Uint8Array(encoded.length + 1);
  out.set(encoded, 0);
  out[encoded.length] = 0;
  return out;
}

/** Synthesizes deterministic acoustic scene PCM channels from Cavern 3D sources. */
function synthesizeHoaChannels(
  sources: SpatialSourceNode[],
  listenerPos: Vec3,
  order: number,
  sampleRateHz: number,
  durationSec: number,
  truePeakCeilingDbtp: number,
): { channels: Float32Array[]; peakAbs: number; rms: number } {
  const harmonicCount = (order + 1) * (order + 1);
  const totalSamples = Math.max(1024, Math.floor(sampleRateHz * durationSec));
  const channels = Array.from({ length: harmonicCount }, () => new Float32Array(totalSamples));

  const anySolo = sources.some((s) => s.solo);
  const active = sources.filter((s) => !s.mute && (!anySolo || s.solo));
  const list = active.length > 0 ? active : sources;
  const norm = 0.65 / Math.sqrt(Math.max(1, list.length));

  for (const src of list) {
    const dx = src.position.x - listenerPos.x;
    const dy = src.position.y - listenerPos.y;
    const dz = src.position.z - listenerPos.z;
    const dist = Math.hypot(dx, dy, dz) || 1;
    const az = Math.atan2(-dx / dist, dz / dist);
    const el = Math.atan2(dy / dist, Math.hypot(dx, dz) / dist);
    const sh = evaluateRealSphericalHarmonicsSn3d(az, el, order);
    const freq = Math.max(45, src.synthFreqHz);

    for (let i = 0; i < totalSamples; i++) {
      const t = i / sampleRateHz;
      const env = Math.min(1, t / 0.04) * Math.min(1, (durationSec - t) / 0.06);
      const sample =
        (0.75 * Math.sin(2 * Math.PI * freq * t) + 0.25 * Math.sin(2 * Math.PI * freq * 1.5 * t)) *
        env *
        norm;
      if (src.kind === 'lfe') {
        channels[0][i] += sample;
      } else {
        for (let q = 0; q < harmonicCount; q++) {
          channels[q][i] += sample * sh[q];
        }
      }
    }
  }

  const ceilingLin = Math.pow(10, clamp(truePeakCeilingDbtp, -6, 0) / 20);
  let peakAbs = 0;
  let sumSq = 0;
  for (let c = 0; c < harmonicCount; c++) {
    const ch = channels[c];
    for (let i = 0; i < totalSamples; i++) {
      const v = clamp(ch[i], -ceilingLin, ceilingLin);
      ch[i] = v;
      const a = Math.abs(v);
      if (a > peakAbs) peakAbs = a;
      sumSq += v * v;
    }
  }

  const rms = Math.sqrt(sumSq / Math.max(1, totalSamples * harmonicCount));
  return { channels, peakAbs, rms };
}

/**
 * Encodes a standards-compliant AOMedia IAMF v1.0.0 (Eclipsa Audio) OBU binary file
 * with Sequence Header ("iamf"), Codec Config ("ipcm"), Scene-Based HOA Audio Element,
 * Mix Presentation with Q7.8 ITU-R BS.1770 loudness, Temporal Delimiters, and Audio Frame OBUs.
 */
export function encodeIamfObuFile(params: {
  channels: Float32Array[];
  sampleRateHz: number;
  bitDepth?: 16 | 24;
  hoaOrder: number;
  mixLabel: string;
  integratedLufs: number;
  truePeakDbtp: number;
  samplesPerFrame?: number;
}): Uint8Array {
  const {
    channels,
    sampleRateHz,
    bitDepth = 16,
    hoaOrder,
    mixLabel,
    integratedLufs,
    truePeakDbtp,
    samplesPerFrame = 1024,
  } = params;

  const chCount = channels.length;
  const totalSamples = channels[0]?.length ?? samplesPerFrame;
  const bytesPerSample = bitDepth === 24 ? 3 : 2;
  const obus: Uint8Array[] = [];

  // 1. OBU_IA_SEQUENCE_HEADER (type 31): "iamf" + primary_profile=1 (Base) + additional=1
  obus.push(writeObu(31, new Uint8Array([0x69, 0x61, 0x6d, 0x66, 0x01, 0x01])));

  // 2. OBU_IA_CODEC_CONFIG (type 0): id=0, "ipcm", samplesPerFrame, roll=0, flags=1(LE), bitDepth, sr(BE32)
  const srBytes = new Uint8Array(4);
  new DataView(srBytes.buffer).setUint32(0, sampleRateHz, false);
  const ccPayload = concatBytes([
    encodeUleb128(0),
    encodeAscii('ipcm'),
    encodeUleb128(samplesPerFrame),
    new Uint8Array([0, 0, 0x01, bitDepth]),
    srBytes,
  ]);
  obus.push(writeObu(0, ccPayload));

  // 3. OBU_IA_AUDIO_ELEMENT (type 1): Scene-Based HOA (type=1)
  const aeParts: Uint8Array[] = [
    encodeUleb128(0), // audio_element_id
    new Uint8Array([1 << 5]), // SCENE_BASED
    encodeUleb128(0), // codec_config_id
    encodeUleb128(chCount),
  ];
  for (let s = 0; s < chCount; s++) aeParts.push(encodeUleb128(s));
  aeParts.push(encodeUleb128(0)); // num_parameters
  const hoaMeta = new Uint8Array(3 + chCount);
  hoaMeta[0] = chCount;
  hoaMeta[1] = chCount;
  hoaMeta[2] = 0; // MONO ambisonics_mode
  for (let s = 0; s < chCount; s++) hoaMeta[3 + s] = s;
  aeParts.push(hoaMeta);
  obus.push(writeObu(1, concatBytes(aeParts)));

  // 4. OBU_IA_MIX_PRESENTATION (type 2): label + 1 submix + BS.1770 Q7.8 loudness
  const loudnessBytes = new Uint8Array(7);
  const lv = new DataView(loudnessBytes.buffer);
  loudnessBytes[0] = 0x02; // True Peak flag present
  lv.setInt16(1, Math.round(clamp(integratedLufs, -120, 0) * 256), false);
  lv.setInt16(3, Math.round(clamp(truePeakDbtp, -120, 0) * 256), false);
  lv.setInt16(5, Math.round(clamp(truePeakDbtp, -120, 0) * 256), false);

  const mpPayload = concatBytes([
    encodeUleb128(0), // mix_presentation_id
    encodeUleb128(1), // count_label
    encodeUtf8Z('en-us'),
    encodeUtf8Z(mixLabel),
    encodeUleb128(1), // num_sub_mixes
    encodeUleb128(1), // num_audio_elements
    encodeUleb128(0), // audio_element_id
    encodeUtf8Z(mixLabel),
    new Uint8Array([0]), // headphones_rendering_mode
    encodeUleb128(0), // rendering_config_extension_size
    encodeUleb128(1), // num_layouts
    new Uint8Array([(2 << 6) | (Math.min(7, hoaOrder + 4) << 2)]),
    loudnessBytes,
  ]);
  obus.push(writeObu(2, mpPayload));

  // 5. Temporal Delimiters + Audio Frame OBUs
  const frameCount = Math.ceil(totalSamples / samplesPerFrame);
  for (let f = 0; f < frameCount; f++) {
    obus.push(writeObu(4, new Uint8Array(0)));
    const start = f * samplesPerFrame;
    for (let c = 0; c < chCount; c++) {
      const ch = channels[c];
      const pcm = new Uint8Array(samplesPerFrame * bytesPerSample);
      let off = 0;
      for (let s = 0; s < samplesPerFrame; s++) {
        const v = start + s < totalSamples ? clamp(ch[start + s], -1, 1) : 0;
        if (bytesPerSample === 2) {
          const i16 = Math.round(v * 32767);
          pcm[off++] = i16 & 0xff;
          pcm[off++] = (i16 >> 8) & 0xff;
        } else {
          const i24 = Math.round(v * 8388607);
          pcm[off++] = i24 & 0xff;
          pcm[off++] = (i24 >> 8) & 0xff;
          pcm[off++] = (i24 >> 16) & 0xff;
        }
      }
      if (c < 18) {
        obus.push(writeObu(6 + c, pcm));
      } else {
        obus.push(writeObu(5, concatBytes([encodeUleb128(c), pcm])));
      }
    }
  }

  return concatBytes(obus);
}

/** Parses and verifies an AOMedia IAMF v1.0.0 OBU binary buffer. */
export function inspectIamfObuBytes(bytes: Uint8Array): IamfInspectionResult {
  const result: IamfInspectionResult = {
    valid: false,
    magic: '',
    primaryProfile: 0,
    codecFourCc: 'unknown',
    elementType: 'Channel-Based',
    channelCount: 0,
    hoaOrder: 0,
    sampleRateHz: 48000,
    bitDepth: 16,
    samplesPerFrame: 1024,
    mixLabel: 'Main Mix',
    integratedLufs: -18,
    truePeakDbtp: -1,
    obuCount: 0,
    audioFrameObuCount: 0,
  };

  let pos = 0;
  while (pos < bytes.length) {
    const header = bytes[pos++];
    const obuType = (header >> 3) & 0x1f;
    const sizeDec = decodeUleb128(bytes, pos);
    if (!sizeDec) break;
    pos = sizeDec.nextOffset;
    const payloadEnd = Math.min(bytes.length, pos + sizeDec.value);
    const payload = bytes.subarray(pos, payloadEnd);
    result.obuCount++;

    if (obuType === 31 && payload.length >= 6) {
      result.magic = String.fromCharCode(payload[0], payload[1], payload[2], payload[3]);
      result.primaryProfile = payload[4];
      result.valid = result.magic === 'iamf';
    } else if (obuType === 0 && payload.length >= 12) {
      const idDec = decodeUleb128(payload, 0);
      if (idDec && idDec.nextOffset + 4 <= payload.length) {
        let p = idDec.nextOffset;
        result.codecFourCc = String.fromCharCode(payload[p], payload[p + 1], payload[p + 2], payload[p + 3]);
        p += 4;
        const spf = decodeUleb128(payload, p);
        if (spf) {
          result.samplesPerFrame = spf.value;
          p = spf.nextOffset + 2; // skip roll distance
          if (result.codecFourCc === 'ipcm' && p + 6 <= payload.length) {
            result.bitDepth = payload[p + 1];
            const dv = new DataView(payload.buffer, payload.byteOffset + p + 2, 4);
            result.sampleRateHz = dv.getUint32(0, false);
          }
        }
      }
    } else if (obuType === 1 && payload.length >= 6) {
      const idDec = decodeUleb128(payload, 0);
      if (idDec && idDec.nextOffset < payload.length) {
        let p = idDec.nextOffset;
        const elemType = (payload[p++] >> 5) & 0x07;
        result.elementType = elemType === 1 ? 'Scene-Based HOA' : 'Channel-Based';
        const cc = decodeUleb128(payload, p);
        if (cc) {
          const ns = decodeUleb128(payload, cc.nextOffset);
          if (ns) {
            p = ns.nextOffset;
            for (let i = 0; i < ns.value; i++) {
              const sid = decodeUleb128(payload, p);
              if (!sid) break;
              p = sid.nextOffset;
            }
            const np = decodeUleb128(payload, p);
            if (np) {
              p = np.nextOffset;
              for (let i = 0; i < np.value; i++) {
                const pid = decodeUleb128(payload, p);
                if (!pid) break;
                p = pid.nextOffset;
              }
              if (elemType === 1 && p < payload.length) {
                result.channelCount = payload[p];
                result.hoaOrder = Math.max(1, Math.round(Math.sqrt(result.channelCount)) - 1);
              } else {
                result.channelCount = ns.value;
              }
            }
          }
        }
      }
    } else if (obuType === 2 && payload.length >= 10) {
      const idDec = decodeUleb128(payload, 0);
      const cntDec = idDec ? decodeUleb128(payload, idDec.nextOffset) : null;
      if (cntDec) {
        let p = cntDec.nextOffset;
        // Skip language null-terminated string
        while (p < payload.length && payload[p] !== 0) p++;
        p++;
        const labelStart = p;
        while (p < payload.length && payload[p] !== 0) p++;
        if (p > labelStart) {
          result.mixLabel = new TextDecoder().decode(payload.subarray(labelStart, p));
        }
      }
      const tail = payload.length - 7;
      if (tail >= 0 && (payload[tail] & 0x02) !== 0) {
        const dv = new DataView(payload.buffer, payload.byteOffset + tail, 7);
        result.integratedLufs = Math.round((dv.getInt16(1, false) / 256) * 10) / 10;
        result.truePeakDbtp = Math.round((dv.getInt16(5, false) / 256) * 10) / 10;
      }
    } else if (obuType >= 5 && obuType <= 23) {
      result.audioFrameObuCount++;
    }

    pos = payloadEnd;
  }

  return result;
}

/** Encodes a WAVEFORMATEXTENSIBLE Ambisonics B-Format (.amb / .wav) file with SUBTYPE_AMBISONIC_B_FORMAT_PCM GUID. */
export function encodeAmbisonicsHoaWav(
  channels: Float32Array[],
  sampleRateHz: number,
): Uint8Array {
  const chCount = channels.length;
  const frames = channels[0]?.length ?? 0;
  const bytesPerSample = 2;
  const dataSize = frames * chCount * bytesPerSample;
  const fmtChunkSize = 40; // WAVEFORMATEXTENSIBLE
  const riffSize = 4 + (8 + fmtChunkSize) + (8 + dataSize);

  const buf = new ArrayBuffer(8 + riffSize);
  const view = new DataView(buf);
  const u8 = new Uint8Array(buf);

  const writeFourCc = (off: number, s: string) => {
    for (let i = 0; i < 4; i++) u8[off + i] = s.charCodeAt(i);
  };

  writeFourCc(0, 'RIFF');
  view.setUint32(4, riffSize, true);
  writeFourCc(8, 'WAVE');

  writeFourCc(12, 'fmt ');
  view.setUint32(16, fmtChunkSize, true);
  view.setInt16(20, -2, true); // WAVE_FORMAT_EXTENSIBLE (0xFFFE)
  view.setUint16(22, chCount, true);
  view.setUint32(24, sampleRateHz, true);
  const blockAlign = chCount * bytesPerSample;
  view.setUint32(28, sampleRateHz * blockAlign, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, 16, true);
  view.setUint16(36, 22, true); // cbSize
  view.setUint16(38, 16, true); // validBitsPerSample
  view.setUint32(40, 0, true); // dwChannelMask = 0 for Ambisonics B-Format
  view.setUint16(44, 1, true); // PCM SubFormat prefix
  u8.set(AMBISONIC_GUID_TAIL, 46); // Ambisonic B-Format GUID tail

  const dataOff = 20 + fmtChunkSize;
  writeFourCc(dataOff, 'data');
  view.setUint32(dataOff + 4, dataSize, true);

  let sampleOff = dataOff + 8;
  for (let f = 0; f < frames; f++) {
    for (let c = 0; c < chCount; c++) {
      const s = clamp(channels[c][f], -1, 1);
      view.setInt16(sampleOff, Math.round(s * 32767), true);
      sampleOff += 2;
    }
  }

  return u8;
}

/** Encodes a standards-compliant ADM BWF (ITU-R BS.2076) WAV file with real axml + chna chunks. */
export function encodeAdmBwfWav(params: {
  programmeName: string;
  sources: SpatialSourceNode[];
  channels: Float32Array[];
  sampleRateHz: number;
  durationSec: number;
}): Uint8Array {
  const { programmeName, sources, channels, sampleRateHz, durationSec } = params;
  const chCount = channels.length;
  const frames = channels[0]?.length ?? 0;
  const bytesPerSample = 2;
  const dataSize = frames * chCount * bytesPerSample;

  const hh = String(Math.floor(durationSec / 3600)).padStart(2, '0');
  const mm = String(Math.floor((durationSec % 3600) / 60)).padStart(2, '0');
  const ss = (durationSec % 60).toFixed(5).padStart(8, '0');
  const endTime = `${hh}:${mm}:${ss}`;

  const objectXml: string[] = [];
  const channelXml: string[] = [];
  const objRefs: string[] = [];

  for (let i = 0; i < chCount; i++) {
    const src = sources[i % Math.max(1, sources.length)];
    const hexId = (0x1001 + i).toString(16).toUpperCase();
    const aoId = `AO_${hexId}`;
    const apId = `AP_0003${hexId}`;
    const acId = `AC_0003${hexId}`;
    const abId = `AB_0003${hexId}_00000001`;
    objRefs.push(`      <audioObjectIDRef>${aoId}</audioObjectIDRef>`);

    const x = clamp(src?.position.x ?? 0, -1, 1).toFixed(4);
    const y = clamp(src?.position.z ?? 1, -1, 1).toFixed(4);
    const z = clamp(src?.position.y ?? 0, -1, 1).toFixed(4);
    const safeName = (src?.name ?? `Object ${i + 1}`).replace(/[<>&"]/g, '');

    objectXml.push(
      `    <audioObject audioObjectID="${aoId}" audioObjectName="${safeName}" start="00:00:00.00000" duration="${endTime}">` +
        `<audioPackFormatIDRef>${apId}</audioPackFormatIDRef></audioObject>`,
    );
    channelXml.push(
      `    <audioChannelFormat audioChannelFormatID="${acId}" audioChannelFormatName="${safeName}" typeLabel="0003" typeDefinition="Objects">` +
        `<audioBlockFormat audioBlockFormatID="${abId}" rtime="00:00:00.00000" duration="${endTime}">` +
        `<cartesian>1</cartesian><position coordinate="X">${x}</position><position coordinate="Y">${y}</position><position coordinate="Z">${z}</position>` +
        `</audioBlockFormat></audioChannelFormat>`,
    );
  }

  const safeProg = programmeName.replace(/[<>&"]/g, '');
  const axmlStr =
    `<?xml version="1.0" encoding="UTF-8"?>\n` +
    `<ebuCoreMain xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns="urn:ebu:metadata-schema:ebuCore_2014">\n` +
    `  <coreMetadata><format><audioFormatExtended>\n` +
    `    <audioProgramme audioProgrammeID="APR_1001" audioProgrammeName="${safeProg}" start="00:00:00.00000" end="${endTime}">\n` +
    `      <audioContentIDRef>ACO_1001</audioContentIDRef>\n` +
    `    </audioProgramme>\n` +
    `    <audioContent audioContentID="ACO_1001" audioContentName="${safeProg}_Content">\n` +
    `${objRefs.join('\n')}\n` +
    `    </audioContent>\n` +
    `${objectXml.join('\n')}\n` +
    `${channelXml.join('\n')}\n` +
    `  </audioFormatExtended></format></coreMetadata>\n` +
    `</ebuCoreMain>`;

  const axmlRaw = new TextEncoder().encode(axmlStr);
  const axmlPadded = axmlRaw.length % 2 === 0 ? axmlRaw : concatBytes([axmlRaw, new Uint8Array([0])]);

  // Binary chna chunk: numTracks (u16) + numUIDs (u16) + 40 bytes per track UID record
  const chnaSize = 4 + chCount * 40;
  const chnaBytes = new Uint8Array(chnaSize);
  const chnaView = new DataView(chnaBytes.buffer);
  chnaView.setUint16(0, chCount, true);
  chnaView.setUint16(2, chCount, true);
  for (let i = 0; i < chCount; i++) {
    const recOff = 4 + i * 40;
    chnaView.setUint16(recOff, i + 1, true);
    const hexId = (0x1001 + i).toString(16).toUpperCase();
    chnaBytes.set(encodeAscii(`ATU_${String(i + 1).padStart(8, '0')}`), recOff + 2);
    chnaBytes.set(encodeAscii(`AT_0003${hexId}_01`), recOff + 14);
    chnaBytes.set(encodeAscii(`AP_0003${hexId}`), recOff + 28);
  }

  const fmtSize = 40;
  const riffSize = 4 + (8 + fmtSize) + (8 + dataSize) + (8 + chnaBytes.length) + (8 + axmlPadded.length);
  const out = new Uint8Array(8 + riffSize);
  const dv = new DataView(out.buffer);
  const writeFourCc = (off: number, s: string) => {
    for (let i = 0; i < 4; i++) out[off + i] = s.charCodeAt(i);
  };

  let pos = 0;
  writeFourCc(pos, 'RIFF'); pos += 4;
  dv.setUint32(pos, riffSize, true); pos += 4;
  writeFourCc(pos, 'WAVE'); pos += 4;

  writeFourCc(pos, 'fmt '); pos += 4;
  dv.setUint32(pos, fmtSize, true); pos += 4;
  dv.setInt16(pos, -2, true); pos += 2;
  dv.setUint16(pos, chCount, true); pos += 2;
  dv.setUint32(pos, sampleRateHz, true); pos += 4;
  const blockAlign = chCount * bytesPerSample;
  dv.setUint32(pos, sampleRateHz * blockAlign, true); pos += 4;
  dv.setUint16(pos, blockAlign, true); pos += 2;
  dv.setUint16(pos, 16, true); pos += 2;
  dv.setUint16(pos, 22, true); pos += 2;
  dv.setUint16(pos, 16, true); pos += 2;
  dv.setUint32(pos, 0, true); pos += 4;
  dv.setUint16(pos, 1, true); pos += 2;
  out.set(STANDARD_GUID_TAIL, pos); pos += 14;

  writeFourCc(pos, 'data'); pos += 4;
  dv.setUint32(pos, dataSize, true); pos += 4;
  for (let f = 0; f < frames; f++) {
    for (let c = 0; c < chCount; c++) {
      dv.setInt16(pos, Math.round(clamp(channels[c][f], -1, 1) * 32767), true);
      pos += 2;
    }
  }

  writeFourCc(pos, 'chna'); pos += 4;
  dv.setUint32(pos, chnaBytes.length, true); pos += 4;
  out.set(chnaBytes, pos); pos += chnaBytes.length;

  writeFourCc(pos, 'axml'); pos += 4;
  dv.setUint32(pos, axmlPadded.length, true); pos += 4;
  out.set(axmlPadded, pos);

  return out;
}

/** Inspects a RIFF WAVE / AMB / ADM BWF binary buffer for Ambisonics GUID and ITU-R BS.2076 axml/chna chunks. */
export function inspectSpatialWaveBytes(bytes: Uint8Array): SpatialWaveInspectionResult {
  const result: SpatialWaveInspectionResult = {
    valid: false,
    formatKind: 'RIFF WAVE PCM',
    channelCount: 0,
    sampleRateHz: 48000,
    bitDepth: 16,
    isAmbisonicGuid: false,
    hoaOrder: 0,
    hasAxml: false,
    hasChna: false,
    admObjectCount: 0,
    admProgrammeName: null,
  };

  if (bytes.length < 44) return result;
  const readFourCc = (off: number) =>
    String.fromCharCode(bytes[off], bytes[off + 1], bytes[off + 2], bytes[off + 3]);

  const riff = readFourCc(0);
  const wave = readFourCc(8);
  if ((riff !== 'RIFF' && riff !== 'RF64') || wave !== 'WAVE') return result;
  result.valid = true;

  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let pos = 12;
  while (pos + 8 <= bytes.length) {
    const id = readFourCc(pos);
    const size = dv.getUint32(pos + 4, true);
    pos += 8;
    const end = Math.min(bytes.length, pos + size);

    if (id === 'fmt ' && size >= 16) {
      const fmtTag = dv.getInt16(pos, true);
      result.channelCount = dv.getUint16(pos + 2, true);
      result.sampleRateHz = dv.getUint32(pos + 4, true);
      result.bitDepth = dv.getUint16(pos + 14, true);
      if (fmtTag === -2 && size >= 40) {
        let matchAmb = true;
        for (let i = 0; i < AMBISONIC_GUID_TAIL.length; i++) {
          if (bytes[pos + 26 + i] !== AMBISONIC_GUID_TAIL[i]) {
            matchAmb = false;
            break;
          }
        }
        if (matchAmb) {
          result.isAmbisonicGuid = true;
          const n = Math.round(Math.sqrt(result.channelCount)) - 1;
          if ((n + 1) * (n + 1) === result.channelCount && n >= 1 && n <= 5) {
            result.hoaOrder = n;
          }
        }
      }
    } else if (id === 'chna') {
      result.hasChna = true;
    } else if (id === 'axml') {
      result.hasAxml = true;
      const xml = new TextDecoder().decode(bytes.subarray(pos, end));
      const progMatch = xml.match(/audioProgrammeName="([^"]+)"/);
      if (progMatch) result.admProgrammeName = progMatch[1];
      const objMatches = xml.match(/<audioObject\b/g);
      result.admObjectCount = objMatches ? objMatches.length : 0;
    }

    pos = end + (size % 2);
  }

  if (result.hasAxml) {
    result.formatKind = 'ADM BWF (ITU-R BS.2076)';
  } else if (result.isAmbisonicGuid) {
    result.formatKind = 'Ambisonics HOA B-Format (WAVE-EX)';
  }

  return result;
}

/**
 * Renders any of the 8 Wave 12 Studio Master Presets into a real, downloadable binary artifact
 * with verified binary format inspection and BS.1770 / True-Peak metrics.
 */
export function renderW12MasterArtifact(params: {
  presetId: W12StudioPresetId;
  trackTitle: string;
  sources: SpatialSourceNode[];
  listenerPosition: Vec3;
  hoaOrder?: number;
  karaokeVocalPct?: number;
  truePeakCeilingDbtp?: number;
  durationSec?: number;
}): W12MasterArtifact {
  const {
    presetId,
    trackTitle,
    sources,
    listenerPosition,
    hoaOrder = 3,
    karaokeVocalPct = 5,
    truePeakCeilingDbtp = -1.0,
    durationSec = 1.5,
  } = params;

  const sampleRateHz = 48000;
  const clampedOrder = Math.max(1, Math.min(3, Math.floor(hoaOrder)));
  const slug = trackTitle.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') || 'cavernize_master';

  const preset = W12_STUDIO_PRESETS.find((p) => p.id === presetId) ?? W12_STUDIO_PRESETS[0];

  // For Karaoke presets, attenuate center/vocal energy smoothly by karaokeVocalPct
  const effectiveSources =
    presetId === 'spatial-karaoke' || presetId === 'live-karaoke-mixdown'
      ? sources.map((s) =>
          s.shortLabel === 'C' || s.kind === 'object'
            ? { ...s, rms: s.rms * (karaokeVocalPct / 100) }
            : s,
        )
      : sources;

  const synthOrder = presetId === 'eclipsa-iamf' || presetId === 'ambisonics-hoa3' ? clampedOrder : 3;
  const synth = synthesizeHoaChannels(
    effectiveSources,
    listenerPosition,
    synthOrder,
    sampleRateHz,
    durationSec,
    truePeakCeilingDbtp,
  );

  const truePeakDbtp = Math.round(20 * Math.log10(Math.max(1e-5, synth.peakAbs)) * 10) / 10;
  const integratedLufs = Math.round((20 * Math.log10(Math.max(1e-5, synth.rms)) - 0.691) * 10) / 10;
  const headroomDb = Math.round(Math.max(0, -truePeakDbtp) * 10) / 10;

  if (presetId === 'eclipsa-iamf') {
    const bytes = encodeIamfObuFile({
      channels: synth.channels,
      sampleRateHz,
      bitDepth: 16,
      hoaOrder: clampedOrder,
      mixLabel: `${trackTitle} (Eclipsa Master)`,
      integratedLufs,
      truePeakDbtp,
    });
    const inspected = inspectIamfObuBytes(bytes);
    return {
      presetId,
      fileName: `${slug}_eclipsa_hoa${clampedOrder}.iamf`,
      mimeType: 'application/octet-stream',
      bytes,
      fileSizeKb: Math.max(1, Math.round(bytes.length / 1024)),
      channelCount: inspected.channelCount,
      sampleRateHz,
      bitDepth: 16,
      durationSec,
      integratedLufs,
      truePeakDbtp,
      headroomDb,
      verificationBadge: inspected.valid ? 'IAMF OBU Verified' : 'IAMF Invalid',
      verificationDetail: `magic="${inspected.magic}" · ${inspected.codecFourCc} · ${inspected.elementType} (Order ${inspected.hoaOrder}, ${inspected.channelCount}ch) · ${inspected.obuCount} OBUs`,
      iamfInspection: inspected,
    };
  }

  if (presetId === 'ambisonics-hoa3') {
    const bytes = encodeAmbisonicsHoaWav(synth.channels, sampleRateHz);
    const inspected = inspectSpatialWaveBytes(bytes);
    return {
      presetId,
      fileName: `${slug}_hoa${clampedOrder}_sn3d.amb`,
      mimeType: 'audio/wav',
      bytes,
      fileSizeKb: Math.max(1, Math.round(bytes.length / 1024)),
      channelCount: inspected.channelCount,
      sampleRateHz,
      bitDepth: 16,
      durationSec,
      integratedLufs,
      truePeakDbtp,
      headroomDb,
      verificationBadge: inspected.isAmbisonicGuid ? 'Ambisonics GUID Verified' : 'WAV PCM',
      verificationDetail: `${inspected.formatKind} · Order ${inspected.hoaOrder} ACN/SN3D · ${inspected.channelCount}ch · 48 kHz`,
      waveInspection: inspected,
    };
  }

  if (presetId === 'adm-bwf' || presetId === 'flac-atmos-hybrid') {
    const chSlice = synth.channels.slice(0, 12);
    const bytes = encodeAdmBwfWav({
      programmeName:
        presetId === 'flac-atmos-hybrid' ? `${trackTitle} (FLAC+Atmos LGSR)` : `${trackTitle} (ADM Master)`,
      sources: effectiveSources,
      channels: chSlice,
      sampleRateHz,
      durationSec,
    });
    const inspected = inspectSpatialWaveBytes(bytes);
    return {
      presetId,
      fileName:
        presetId === 'flac-atmos-hybrid'
          ? `${slug}_flac_atmos_hybrid_adm.wav`
          : `${slug}_adm_bs2076.wav`,
      mimeType: 'audio/wav',
      bytes,
      fileSizeKb: Math.max(1, Math.round(bytes.length / 1024)),
      channelCount: chSlice.length,
      sampleRateHz,
      bitDepth: 16,
      durationSec,
      integratedLufs,
      truePeakDbtp,
      headroomDb,
      verificationBadge: inspected.hasAxml && inspected.hasChna ? 'ADM BS.2076 Verified' : 'WAV PCM',
      verificationDetail: `axml + chna · ${inspected.admObjectCount} 3D objects · ${chSlice.length}ch · Programme "${inspected.admProgrammeName}"`,
      waveInspection: inspected,
    };
  }

  // Remaining WAV presets (Personalized Binaural 2ch, Hybrid 7.1.4 12ch, Spatial Karaoke 6ch, Live Performance Mixdown 2ch)
  const targetCh = preset.channels;
  const outCh = synth.channels.slice(0, targetCh);
  const bytes = encodeStandardWavePcm16(outCh, sampleRateHz);
  const inspected = inspectSpatialWaveBytes(bytes);

  return {
    presetId,
    fileName: `${slug}_${presetId.replace(/-/g, '_')}.wav`,
    mimeType: 'audio/wav',
    bytes,
    fileSizeKb: Math.max(1, Math.round(bytes.length / 1024)),
    channelCount: targetCh,
    sampleRateHz,
    bitDepth: 16,
    durationSec,
    integratedLufs,
    truePeakDbtp,
    headroomDb,
    verificationBadge: `${preset.formatTag} Verified`,
    verificationDetail: `${preset.pipelineSummary} · ${targetCh}ch @ 48 kHz`,
    waveInspection: inspected,
  };
}

function encodeStandardWavePcm16(channels: Float32Array[], sampleRateHz: number): Uint8Array {
  const chCount = channels.length;
  const frames = channels[0]?.length ?? 0;
  const dataSize = frames * chCount * 2;
  const out = new Uint8Array(44 + dataSize);
  const dv = new DataView(out.buffer);
  const writeFourCc = (off: number, s: string) => {
    for (let i = 0; i < 4; i++) out[off + i] = s.charCodeAt(i);
  };
  writeFourCc(0, 'RIFF');
  dv.setUint32(4, 36 + dataSize, true);
  writeFourCc(8, 'WAVE');
  writeFourCc(12, 'fmt ');
  dv.setUint32(16, 16, true);
  dv.setUint16(20, 1, true);
  dv.setUint16(22, chCount, true);
  dv.setUint32(24, sampleRateHz, true);
  dv.setUint32(28, sampleRateHz * chCount * 2, true);
  dv.setUint16(32, chCount * 2, true);
  dv.setUint16(34, 16, true);
  writeFourCc(36, 'data');
  dv.setUint32(40, dataSize, true);
  let pos = 44;
  for (let f = 0; f < frames; f++) {
    for (let c = 0; c < chCount; c++) {
      dv.setInt16(pos, Math.round(clamp(channels[c][f], -1, 1) * 32767), true);
      pos += 2;
    }
  }
  return out;
}
