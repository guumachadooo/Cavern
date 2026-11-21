import assert from 'node:assert/strict';
import test from 'node:test';
import { DEMO_TRACKS } from '../data/catalog';
import {
  assessLosslessTwin,
  buildAudioSpecSummary,
  summarizeSingDock,
} from './w14AudioSpec';

test('assessLosslessTwin activates ATMOS+FLAC badge for lossy Atmos track with FLAC sibling', () => {
  const track = DEMO_TRACKS[0];
  const atmosVariant = track.variants.find((v) => v.id === 'horizon-eac3');
  assert.ok(atmosVariant);
  assert.ok(track.variants.some((v) => v.container === 'FLAC'));

  const active = assessLosslessTwin(
    track,
    atmosVariant,
    { lgsrEnabled: true, lgsrFusionPct: 80 },
    false,
  );
  assert.equal(active.isLossyAtmos, true);
  assert.ok(active.twinVariant !== null);
  assert.equal(active.twinVariant.container, 'FLAC');
  assert.equal(active.isRestoredActive, true);
  assert.equal(active.isBypassedForAb, false);
  assert.equal(active.badgeMode, 'atmos-flac');
  assert.ok(active.coherenceScore > 0.9);
  assert.equal(active.couplingCutoffHz, 10400);
});

test('assessLosslessTwin switches cleanly to atmos-ab-bypass when A/B compare is toggled', () => {
  const track = DEMO_TRACKS[0];
  const atmosVariant = track.variants.find((v) => v.id === 'horizon-eac3');
  assert.ok(atmosVariant);

  const bypassed = assessLosslessTwin(
    track,
    atmosVariant,
    { lgsrEnabled: true, lgsrFusionPct: 80 },
    true,
  );
  assert.equal(bypassed.isRestoredActive, false);
  assert.equal(bypassed.isBypassedForAb, true);
  assert.equal(bypassed.badgeMode, 'atmos-ab-bypass');
  assert.equal(bypassed.coherenceScore, 0.42);
});

test('assessLosslessTwin stays native for lossless TrueHD Atmos or stereo FLAC', () => {
  const truehdTrack = DEMO_TRACKS.find((tr) =>
    tr.variants.some((v) => v.codec.toLowerCase().includes('truehd')),
  );
  assert.ok(truehdTrack);
  const truehdVariant = truehdTrack.variants[0];

  const assessment = assessLosslessTwin(
    truehdTrack,
    truehdVariant,
    { lgsrEnabled: true, lgsrFusionPct: 85 },
    false,
  );
  assert.equal(assessment.isLossyAtmos, false);
  assert.equal(assessment.twinVariant, null);
  assert.equal(assessment.badgeMode, 'native');

  const summary = buildAudioSpecSummary(
    truehdTrack,
    truehdVariant,
    { lgsrEnabled: true, lgsrFusionPct: 85 },
    false,
  );
  assert.equal(summary.qualityTier, 'Studio Spatial Master');
  assert.equal(summary.nyquistKhz, 24);
});

test('buildAudioSpecSummary reports Lossless Twin Restored vs Lossy Spatial on A/B toggle', () => {
  const track = DEMO_TRACKS[0];
  const atmosVariant = track.variants.find((v) => v.id === 'horizon-eac3');
  assert.ok(atmosVariant);

  const restored = buildAudioSpecSummary(
    track,
    atmosVariant,
    { lgsrEnabled: true, lgsrFusionPct: 75 },
    false,
  );
  assert.equal(restored.qualityTier, 'Lossless Twin Restored');
  assert.equal(restored.effectiveDynamicRangeDb, 14.2);

  const original = buildAudioSpecSummary(
    track,
    atmosVariant,
    { lgsrEnabled: true, lgsrFusionPct: 75 },
    true,
  );
  assert.equal(original.qualityTier, 'Lossy Spatial');
  assert.equal(original.effectiveDynamicRangeDb, 12.6);
});

test('summarizeSingDock clamps vocal level and detects active karaoke attenuation', () => {
  const full = summarizeSingDock(
    { karaokeBackingVocalPct: 100, mixMatchPct: 75, reverbPct: 40, bypass: false },
    false,
  );
  assert.equal(full.vocalLevelPct, 100);
  assert.equal(full.isKaraokeAttenuating, false);
  assert.equal(full.isMicLive, false);

  const sing = summarizeSingDock(
    { karaokeBackingVocalPct: 18, mixMatchPct: 82, reverbPct: 55, bypass: true },
    true,
  );
  assert.equal(sing.vocalLevelPct, 18);
  assert.equal(sing.isKaraokeAttenuating, true);
  assert.equal(sing.isMicLive, true);
  assert.equal(sing.isVocalChainBypassed, true);
});
