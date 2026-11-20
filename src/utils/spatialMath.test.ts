import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildTrajectoryJson,
  clampToRoom,
  computeListenerOrientation,
  computeRolloffGain,
  encodeWavPcm16,
  evaluateSourcePosition,
  formatTime,
  parseQuickEqText,
  projectRoomPoint,
} from './spatialMath';
import { createDefaultSpatialSources } from '../data/catalog';

test('formatTime formats mm:ss cleanly and guards invalid input', () => {
  assert.equal(formatTime(0), '0:00');
  assert.equal(formatTime(65), '1:05');
  assert.equal(formatTime(214.8), '3:34');
  assert.equal(formatTime(-4), '0:00');
});

test('clampToRoom enforces room boundary walls', () => {
  const clamped = clampToRoom({ x: 14, y: -9, z: 2 }, { x: 10, y: 7, z: 10 });
  assert.equal(clamped.x, 4.8);
  assert.equal(clamped.y, -3.36);
  assert.equal(clamped.z, 2);
});

test('computeRolloffGain respects Disabled, Linear, Logarithmic, and Real 1/d curves', () => {
  assert.equal(computeRolloffGain(6, 'disabled'), 1);
  const linearClose = computeRolloffGain(1.5, 'linear');
  const linearFar = computeRolloffGain(8, 'linear');
  assert.ok(linearClose > linearFar);
  const realClose = computeRolloffGain(1.5, 'real');
  const realFar = computeRolloffGain(6, 'real');
  assert.equal(realClose, 1);
  assert.equal(realFar, 0.25);
  assert.ok(computeRolloffGain(5, 'logarithmic') < 1);
});

test('computeListenerOrientation maps 0 yaw/pitch to forward +Z and up +Y', () => {
  const { forward, up } = computeListenerOrientation(0, 0);
  assert.equal(forward.x, 0);
  assert.equal(forward.y, 0);
  assert.equal(forward.z, 1);
  assert.equal(up.y, 1);
});

test('evaluateSourcePosition moves dynamic 3D objects over time while keeping beds fixed', () => {
  const sources = createDefaultSpatialSources();
  const bedL = sources.find((s) => s.id === 'L')!;
  const obj1 = sources.find((s) => s.id === 'Obj1')!;
  assert.deepEqual(evaluateSourcePosition(bedL, 12), bedL.basePosition);
  const p0 = evaluateSourcePosition(obj1, 0);
  const p2 = evaluateSourcePosition(obj1, 2.5);
  assert.notDeepEqual(p0, p2);
});

test('projectRoomPoint centers origin on viewport', () => {
  const projected = projectRoomPoint({ x: 0, y: 0, z: 0 }, 0, 0, 1, 800, 500);
  assert.equal(projected.x, 400);
  assert.equal(projected.y, 270);
  assert.equal(projected.scale, 1);
});

test('parseQuickEqText parses Equalizer APO / QuickEQ filter directives', () => {
  const sample = [
    'Preamp: -4.2 dB',
    'Filter 1: ON PK Fc 63 Hz Gain -4.5 dB Q 2.4',
    'Filter 2: ON LSC Fc 120 Hz Gain 2.0 dB Q 0.71',
    'Filter 3: OFF PK Fc 500 Hz Gain 3.0 dB Q 1.0',
  ].join('\n');
  const parsed = parseQuickEqText(sample);
  assert.equal(parsed.length, 2);
  assert.equal(parsed[0].type, 'PK');
  assert.equal(parsed[0].freqHz, 63);
  assert.equal(parsed[0].gainDb, -4.5);
  assert.equal(parsed[1].type, 'LSC');
});

test('buildTrajectoryJson outputs valid Cavern 3D frame trajectory JSON', () => {
  const sources = createDefaultSpatialSources().filter((s) => s.kind === 'object');
  const json = buildTrajectoryJson('Test Master', sources, 2, 5);
  const parsed = JSON.parse(json) as { frameCount: number; sources: Array<{ id: string; frames: unknown[] }> };
  assert.equal(parsed.frameCount, 10);
  assert.equal(parsed.sources.length, 4);
  assert.equal(parsed.sources[0].frames.length, 10);
});

test('encodeWavPcm16 produces a valid RIFF WAVE header and payload size', () => {
  const left = new Float32Array([0, 0.5, -0.5, 1]);
  const right = new Float32Array([0, -0.5, 0.5, -1]);
  const wav = encodeWavPcm16([left, right], 48000);
  assert.equal(wav.byteLength, 44 + 4 * 2 * 2);
  const view = new DataView(wav);
  const riff = String.fromCharCode(view.getUint8(0), view.getUint8(1), view.getUint8(2), view.getUint8(3));
  const wave = String.fromCharCode(view.getUint8(8), view.getUint8(9), view.getUint8(10), view.getUint8(11));
  assert.equal(riff, 'RIFF');
  assert.equal(wave, 'WAVE');
  assert.equal(view.getUint16(22, true), 2);
  assert.equal(view.getUint32(24, true), 48000);
});
