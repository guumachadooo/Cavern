import test from 'node:test';
import assert from 'node:assert/strict';
import { createDefaultSpatialSources } from '../data/catalog';
import {
  computeMaxReWeights,
  decodeUleb128,
  encodeAdmBwfWav,
  encodeAmbisonicsHoaWav,
  encodeIamfObuFile,
  encodeUleb128,
  evaluateRealSphericalHarmonicsSn3d,
  inspectIamfObuBytes,
  inspectSpatialWaveBytes,
  renderW12MasterArtifact,
  W12_STUDIO_PRESETS,
} from './w12Formats';

test('ULEB128 encodes and decodes single-byte and multi-byte values accurately', () => {
  for (const v of [0, 1, 127, 128, 1024, 16384, 65535, 1_048_576]) {
    const enc = encodeUleb128(v);
    const dec = decodeUleb128(enc, 0);
    assert.ok(dec !== null);
    assert.equal(dec.value, v);
    assert.equal(dec.nextOffset, enc.length);
  }
});

test('SN3D 3rd-order spherical harmonics satisfy analytical identities and Max-rE bounds', () => {
  // Front (+Z): az = 0, el = 0 -> W=1, Y=0, Z=0, X=1
  const front = evaluateRealSphericalHarmonicsSn3d(0, 0, 3);
  assert.equal(front.length, 16);
  assert.equal(front[0], 1.0);
  assert.ok(Math.abs(front[1]) < 1e-12);
  assert.ok(Math.abs(front[2]) < 1e-12);
  assert.ok(Math.abs(front[3] - 1.0) < 1e-12);

  // Zenith (+Y): el = +pi/2 -> Y_0^0=1, Y_1^0=1, Y_2^0=1, Y_3^0=1
  const zenith = evaluateRealSphericalHarmonicsSn3d(0, Math.PI / 2, 3);
  assert.ok(Math.abs(zenith[0] - 1.0) < 1e-12);
  assert.ok(Math.abs(zenith[2] - 1.0) < 1e-12);
  assert.ok(Math.abs(zenith[6] - 1.0) < 1e-12);
  assert.ok(Math.abs(zenith[12] - 1.0) < 1e-12);

  const weights = computeMaxReWeights(3);
  assert.equal(weights.length, 4);
  assert.equal(weights[0], 1.0);
  assert.ok(weights[1] > weights[2] && weights[2] > weights[3] && weights[3] > 0);
});

test('Eclipsa Audio IAMF v1.0 OBU binary encode and inspect round-trip succeeds', () => {
  const chs = Array.from({ length: 16 }, () => new Float32Array(2048));
  chs[0].fill(0.25);
  const bytes = encodeIamfObuFile({
    channels: chs,
    sampleRateHz: 48000,
    bitDepth: 16,
    hoaOrder: 3,
    mixLabel: 'Cavernize Eclipsa Test',
    integratedLufs: -18.5,
    truePeakDbtp: -1.0,
    samplesPerFrame: 1024,
  });

  const info = inspectIamfObuBytes(bytes);
  assert.equal(info.valid, true);
  assert.equal(info.magic, 'iamf');
  assert.equal(info.codecFourCc, 'ipcm');
  assert.equal(info.elementType, 'Scene-Based HOA');
  assert.equal(info.channelCount, 16);
  assert.equal(info.hoaOrder, 3);
  assert.equal(info.sampleRateHz, 48000);
  assert.equal(info.mixLabel, 'Cavernize Eclipsa Test');
  assert.ok(Math.abs(info.integratedLufs - -18.5) <= 0.1);
  assert.ok(Math.abs(info.truePeakDbtp - -1.0) <= 0.1);
  assert.equal(info.audioFrameObuCount, 32); // 2 frames * 16 substreams
});

test('Ambisonics HOA 3rd-order WAVEFORMATEXTENSIBLE Ambisonic GUID round-trip succeeds', () => {
  const chs = Array.from({ length: 16 }, () => new Float32Array(512));
  const bytes = encodeAmbisonicsHoaWav(chs, 48000);
  const info = inspectSpatialWaveBytes(bytes);

  assert.equal(info.valid, true);
  assert.equal(info.isAmbisonicGuid, true);
  assert.equal(info.hoaOrder, 3);
  assert.equal(info.channelCount, 16);
  assert.equal(info.sampleRateHz, 48000);
  assert.equal(info.formatKind, 'Ambisonics HOA B-Format (WAVE-EX)');
});

test('ADM BWF ITU-R BS.2076 axml + chna chunks encode and inspect cleanly', () => {
  const sources = createDefaultSpatialSources();
  const chs = Array.from({ length: 12 }, () => new Float32Array(512));
  const bytes = encodeAdmBwfWav({
    programmeName: 'Acoustic Horizon ADM',
    sources,
    channels: chs,
    sampleRateHz: 48000,
    durationSec: 1.0,
  });
  const info = inspectSpatialWaveBytes(bytes);

  assert.equal(info.valid, true);
  assert.equal(info.hasAxml, true);
  assert.equal(info.hasChna, true);
  assert.equal(info.admObjectCount, 12);
  assert.equal(info.admProgrammeName, 'Acoustic Horizon ADM');
  assert.equal(info.formatKind, 'ADM BWF (ITU-R BS.2076)');
});

test('All 8 W12 Studio Master Presets generate valid verified artifacts', () => {
  const sources = createDefaultSpatialSources();
  assert.equal(W12_STUDIO_PRESETS.length, 8);

  for (const preset of W12_STUDIO_PRESETS) {
    const artifact = renderW12MasterArtifact({
      presetId: preset.id,
      trackTitle: 'Acoustic Horizon',
      sources,
      listenerPosition: { x: 0, y: 0, z: 0 },
      hoaOrder: 3,
      karaokeVocalPct: 5,
      truePeakCeilingDbtp: -1.0,
      durationSec: 0.25,
    });
    assert.ok(artifact.bytes.length > 1000, `${preset.id} produced too few bytes`);
    assert.ok(artifact.fileName.endsWith(preset.extension), `${preset.id} wrong extension`);
    assert.ok(artifact.truePeakDbtp <= -0.9, `${preset.id} exceeded -1.0 dBTP ceiling: ${artifact.truePeakDbtp}`);
    assert.ok(artifact.verificationBadge.includes('Verified'), `${preset.id} failed verification`);
  }
});
