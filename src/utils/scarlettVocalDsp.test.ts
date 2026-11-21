import test from 'node:test';
import assert from 'node:assert/strict';
import {
  adaaTanhSample,
  applyScarlettChannelPatch,
  computeAutoGainTargetDb,
  computeDualStageCompressorGrDb,
  computeFormantSafeMatchEq,
  computeVocalPocketDuckDb,
  createDefaultScarlettState,
  evaluateClipSafeGain,
  getTrackVocalDna,
  meterRawToDbfs,
} from './scarlettVocalDsp';

test('Scarlett Stereo Link mirrors Gain, Air, Clip Safe, and 48V across Ch 1 and Ch 2', () => {
  const base = createDefaultScarlettState();
  const linked = { ...base, inputsLinked: true };
  const updated = applyScarlettChannelPatch(linked, 0, {
    gainDb: 48,
    airMode: 'drive',
    clipSafe: false,
    phantom48v: true,
  });
  assert.equal(updated.channels[0].gainDb, 48);
  assert.equal(updated.channels[1].gainDb, 48);
  assert.equal(updated.channels[0].airMode, 'drive');
  assert.equal(updated.channels[1].airMode, 'drive');
  assert.equal(updated.channels[0].clipSafe, false);
  assert.equal(updated.channels[1].clipSafe, false);
  assert.equal(updated.channels[1].phantom48v, true);
});

test('Scarlett Gen 3 shares 48V phantom rail and downgrades Air Drive to Presence', () => {
  const gen3 = { ...createDefaultScarlettState(), modelId: '2i2-gen3' as const, inputsLinked: false };
  const updated = applyScarlettChannelPatch(gen3, 0, {
    phantom48v: false,
    airMode: 'drive',
  });
  assert.equal(updated.channels[0].phantom48v, false);
  assert.equal(updated.channels[1].phantom48v, false);
  assert.equal(updated.channels[0].airMode, 'presence');
});

test('Gen 4 Auto Gain computes optimal -12 dBFS target gain clamped to [0, 69] dB', () => {
  assert.equal(computeAutoGainTargetDb(-26, 24), 38);
  assert.equal(computeAutoGainTargetDb(-2, 40), 30);
  assert.equal(computeAutoGainTargetDb(-60, 45), 69);
  assert.equal(computeAutoGainTargetDb(-100, 30), 30);
});

test('Gen 4 Clip Safe pulls preamp gain down on near-clipping peaks (>= -1 dBFS)', () => {
  const safe = evaluateClipSafeGain(42, -0.2, true);
  assert.equal(safe.triggered, true);
  assert.ok(safe.nextGainDb < 42);
  assert.equal(safe.reductionDb, 42 - safe.nextGainDb);

  const nominal = evaluateClipSafeGain(42, -14, true);
  assert.equal(nominal.triggered, false);
  assert.equal(nominal.nextGainDb, 42);

  const disabled = evaluateClipSafeGain(42, 0, false);
  assert.equal(disabled.triggered, false);
});

test('Scarlett2 meter raw uint16 maps cleanly to dBFS', () => {
  assert.equal(meterRawToDbfs(0), -120);
  assert.equal(meterRawToDbfs(65535), 0);
  const half = meterRawToDbfs(32768);
  assert.ok(Math.abs(half - -6.02) < 0.05);
});

test('W11 Formant-Safe Match EQ stays bounded within +/- 6 dB and preserves neutral loudness offset', () => {
  const song = [-26, -18, -14, -12, -11, -10, -14, -18];
  const mic = [-10, -12, -15, -18, -22, -26, -28, -32];
  const deltas = computeFormantSafeMatchEq(song, mic, 6);
  assert.equal(deltas.length, 8);
  for (const d of deltas) {
    assert.ok(d >= -6 && d <= 6);
  }
  assert.ok(deltas[5] > 0, 'Presence band should boost dull mic');
  assert.ok(deltas[0] < 0, 'Sub band should tame boomy proximity mic');
});

test('W11 ADAA tanh saturation matches smooth analytical derivative without NaN or overshoot', () => {
  const zero = adaaTanhSample(0, 0);
  assert.equal(zero, 0);
  const mid = adaaTanhSample(0.5, 0.5);
  assert.ok(Math.abs(mid - Math.tanh(0.5)) < 1e-6);
  const step = adaaTanhSample(2.0, -0.5);
  assert.ok(Number.isFinite(step) && step > 0 && step < 1);
});

test('W11 Dual-Stage FET + Opto compressor and Vocal-Pocket sidechain glue scale predictably', () => {
  const gr = computeDualStageCompressorGrDb(-8, 100, 100);
  assert.ok(gr.fetGrDb < 0);
  assert.ok(gr.optoGrDb < 0);
  assert.equal(gr.totalGrDb, Number((gr.fetGrDb + gr.optoGrDb).toFixed(2)));

  const duckActive = computeVocalPocketDuckDb(-12, 100, true);
  assert.ok(duckActive < 0 && duckActive >= -3.5);
  assert.equal(computeVocalPocketDuckDb(-12, 100, false), 0);

  const dna = getTrackVocalDna('track-atmos-01', true);
  assert.equal(dna.bandHz.length, 8);
  assert.ok(dna.reverbRt60Sec >= 1.5);
});
