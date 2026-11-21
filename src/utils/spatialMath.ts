import type {
  QuickEqFilter,
  RolloffMode,
  SpatialSourceNode,
  Vec3,
} from '../types/cavern';

export function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

export function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) {
    return '0:00';
  }
  const total = Math.floor(seconds);
  const mins = Math.floor(total / 60);
  const secs = total % 60;
  return `${mins}:${secs.toString().padStart(2, '0')}`;
}

/**
 * Clamps a listener coordinate cleanly inside the acoustic room bounds.
 */
export function clampToRoom(pos: Vec3, roomSize: Vec3): Vec3 {
  const halfX = Math.max(1, roomSize.x * 0.48);
  const halfY = Math.max(1, roomSize.y * 0.48);
  const halfZ = Math.max(1, roomSize.z * 0.48);
  return {
    x: Number(clamp(pos.x, -halfX, halfX).toFixed(2)),
    y: Number(clamp(pos.y, -halfY, halfY).toFixed(2)),
    z: Number(clamp(pos.z, -halfZ, halfZ).toFixed(2)),
  };
}

/**
 * Computes Euclidean distance between two 3D points in meters.
 */
export function distance3D(a: Vec3, b: Vec3): number {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  const dz = a.z - b.z;
  return Math.sqrt(dx * dx + dy * dy + dz * dz);
}

/**
 * Computes Cavern acoustic distance attenuation gain based on selected rolloff curve.
 */
export function computeRolloffGain(
  distanceMeters: number,
  mode: RolloffMode,
  refDistance = 1.5,
  maxDistance = 12,
): number {
  const d = Math.max(0.25, distanceMeters);
  if (mode === 'disabled') {
    return 1;
  }
  if (mode === 'linear') {
    const t = clamp((d - refDistance) / Math.max(0.1, maxDistance - refDistance), 0, 1);
    return Number(clamp(1 - t * 0.85, 0.15, 1).toFixed(4));
  }
  if (mode === 'logarithmic') {
    const gain = refDistance / (refDistance + Math.log(1 + Math.max(0, d - refDistance)));
    return Number(clamp(gain, 0.12, 1).toFixed(4));
  }
  // 'real' inverse distance 1/d
  const gain = refDistance / Math.max(refDistance, d);
  return Number(clamp(gain, 0.08, 1).toFixed(4));
}

/**
 * Computes listener forward and up unit vectors from Yaw (deg) and Pitch (deg)
 * for Web Audio API AudioListener and 3D head gimbal orientation.
 */
export function computeListenerOrientation(yawDeg: number, pitchDeg: number): {
  forward: Vec3;
  up: Vec3;
} {
  const yaw = (yawDeg * Math.PI) / 180;
  const pitch = (clamp(pitchDeg, -85, 85) * Math.PI) / 180;
  const cosPitch = Math.cos(pitch);
  const forward: Vec3 = {
    x: Number((Math.sin(yaw) * cosPitch).toFixed(5)),
    y: Number(Math.sin(pitch).toFixed(5)),
    z: Number((Math.cos(yaw) * cosPitch).toFixed(5)),
  };
  const up: Vec3 = {
    x: Number((-Math.sin(yaw) * Math.sin(pitch)).toFixed(5)),
    y: Number(cosPitch.toFixed(5)),
    z: Number((-Math.cos(yaw) * Math.sin(pitch)).toFixed(5)),
  };
  return { forward, up };
}

/**
 * Evaluates a dynamic 3D object's position at playback time `timeSec`.
 */
export function evaluateSourcePosition(source: SpatialSourceNode, timeSec: number): Vec3 {
  if (!source.orbit) {
    return source.basePosition;
  }
  const { radiusX, radiusZ, centerY, ampY, speedRad, phase } = source.orbit;
  const angle = timeSec * speedRad + phase;
  return {
    x: Number((Math.cos(angle) * radiusX).toFixed(3)),
    y: Number((centerY + Math.sin(angle * 1.4) * ampY).toFixed(3)),
    z: Number((Math.sin(angle) * radiusZ).toFixed(3)),
  };
}

/**
 * Transforms a world-space source coordinate into listener-relative coordinates
 * taking listener position and horizontal head yaw rotation into account.
 */
export function transformToListenerFrame(
  sourcePos: Vec3,
  listenerPos: Vec3,
  yawDeg: number,
): Vec3 {
  const dx = sourcePos.x - listenerPos.x;
  const dy = sourcePos.y - listenerPos.y;
  const dz = sourcePos.z - listenerPos.z;
  const yawRad = (yawDeg * Math.PI) / 180;
  const cosY = Math.cos(yawRad);
  const sinY = Math.sin(yawRad);
  // Rotate world vector inversely by listener yaw so +X is listener's right ear and +Z is ahead
  return {
    x: Number((dx * cosY - dz * sinY).toFixed(4)),
    y: Number(dy.toFixed(4)),
    z: Number((dx * sinY + dz * cosY).toFixed(4)),
  };
}

/**
 * Determines whether a spatial source channel/object should be active for the
 * currently selected track format variant and Matrix 7.1 / Cavernize 3D upmixing settings.
 */
export function isSourceActiveForVariant(
  source: Pick<SpatialSourceNode, 'id' | 'kind'>,
  variant: { isSpatial: boolean; objectCount: number },
  upmix: { matrixUpmix: boolean; cavernize3D: boolean },
): boolean {
  if (variant.isSpatial) {
    if (source.kind === 'object') return variant.objectCount > 0;
    return true;
  }
  // Stereo / non-spatial variant: L and R are always native
  if (source.id === 'L' || source.id === 'R') return true;
  if (source.kind === 'lfe' || source.kind === 'bed') return upmix.matrixUpmix;
  if (source.kind === 'height') return upmix.cavernize3D;
  if (source.kind === 'object') return upmix.cavernize3D;
  return false;
}

/**
 * Projects a 3D world point in the Cavern room onto 2D viewport coordinates.
 */
export function projectRoomPoint(
  point: Vec3,
  camYawDeg: number,
  camPitchDeg: number,
  zoom: number,
  width: number,
  height: number,
): { x: number; y: number; depth: number; scale: number } {
  const yaw = (camYawDeg * Math.PI) / 180;
  const pitch = (camPitchDeg * Math.PI) / 180;

  // Rotate around Y (yaw)
  const cosY = Math.cos(yaw);
  const sinY = Math.sin(yaw);
  const rx = point.x * cosY - point.z * sinY;
  const rz = point.x * sinY + point.z * cosY;

  // Rotate around X (pitch)
  const cosP = Math.cos(pitch);
  const sinP = Math.sin(pitch);
  const ry = point.y * cosP - rz * sinP;
  const depth = point.y * sinP + rz * cosP;

  const cameraDist = 20;
  const perspective = cameraDist / Math.max(4, cameraDist - depth);
  const unitScale = Math.min(width, height) * 0.072 * zoom;

  return {
    x: width * 0.5 + rx * unitScale * perspective,
    y: height * 0.54 - ry * unitScale * perspective,
    depth,
    scale: perspective,
  };
}

/**
 * Parses QuickEQ / Equalizer APO filter lines (e.g. "Filter 1: ON PK Fc 63 Hz Gain -4.5 dB Q 2.4").
 */
export function parseQuickEqText(rawText: string): QuickEqFilter[] {
  const filters: QuickEqFilter[] = [];
  const lines = rawText.split(/\r?\n/);
  const regex =
    /Filter\s+\d+:\s+ON\s+(PK|LSC|HSC|LP|HP)\s+Fc\s+([\d.]+)\s*Hz(?:\s+Gain\s+(-?[\d.]+)\s*dB)?(?:\s+Q\s+([\d.]+))?/i;

  for (const line of lines) {
    const match = line.match(regex);
    if (!match) continue;
    const type = match[1].toUpperCase() as QuickEqFilter['type'];
    const freqHz = Number.parseFloat(match[2]);
    const gainDb = match[3] !== undefined ? Number.parseFloat(match[3]) : 0;
    const q = match[4] !== undefined ? Number.parseFloat(match[4]) : 1.0;
    if (Number.isFinite(freqHz) && freqHz > 0) {
      filters.push({ type, freqHz, gainDb, q });
    }
  }
  return filters;
}

/**
 * Generates Cavern-compatible frame-by-frame 3D trajectory JSON for selected sources.
 */
export function buildTrajectoryJson(
  trackTitle: string,
  sources: SpatialSourceNode[],
  durationSec: number,
  fps = 10,
): string {
  const clampedDuration = clamp(durationSec, 1, 12);
  const totalFrames = Math.max(2, Math.floor(clampedDuration * fps));
  const payload = {
    engine: 'Cavernize Web Spatial Engine',
    track: trackTitle,
    coordinateSystem: 'Cavern Right-Handed (X: Left/Right, Y: Height, Z: Rear/Front)',
    frameRateHz: fps,
    frameCount: totalFrames,
    sources: sources.map((src) => ({
      id: src.id,
      name: src.name,
      kind: src.kind,
      frames: Array.from({ length: totalFrames }, (_, idx) => {
        const t = Number((idx / fps).toFixed(2));
        const pos = evaluateSourcePosition(src, t);
        return { t, x: pos.x, y: pos.y, z: pos.z };
      }),
    })),
  };
  return JSON.stringify(payload, null, 2);
}

/**
 * Encodes floating-point interleaved channel buffers into a standard RIFF PCM WAV ArrayBuffer.
 */
export function encodeWavPcm16(channels: Float32Array[], sampleRateHz: number): ArrayBuffer {
  const numChannels = Math.max(1, channels.length);
  const numFrames = channels[0]?.length ?? 0;
  const bytesPerSample = 2;
  const blockAlign = numChannels * bytesPerSample;
  const dataByteLength = numFrames * blockAlign;
  const buffer = new ArrayBuffer(44 + dataByteLength);
  const view = new DataView(buffer);

  const writeStr = (offset: number, str: string) => {
    for (let i = 0; i < str.length; i += 1) {
      view.setUint8(offset + i, str.charCodeAt(i));
    }
  };

  writeStr(0, 'RIFF');
  view.setUint32(4, 36 + dataByteLength, true);
  writeStr(8, 'WAVE');
  writeStr(12, 'fmt ');
  view.setUint32(16, 16, true); // PCM chunk size
  view.setUint16(20, 1, true); // PCM linear format
  view.setUint16(22, numChannels, true);
  view.setUint32(24, sampleRateHz, true);
  view.setUint32(28, sampleRateHz * blockAlign, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, 16, true); // 16-bit
  writeStr(36, 'data');
  view.setUint32(40, dataByteLength, true);

  let offset = 44;
  for (let frame = 0; frame < numFrames; frame += 1) {
    for (let ch = 0; ch < numChannels; ch += 1) {
      const sample = clamp(channels[ch][frame] ?? 0, -1, 1);
      const pcm = sample < 0 ? Math.round(sample * 0x8000) : Math.round(sample * 0x7fff);
      view.setInt16(offset, pcm, true);
      offset += 2;
    }
  }

  return buffer;
}
