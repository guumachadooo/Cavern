import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { createDefaultSpatialSources, DEMO_TRACKS } from '../data/catalog';
import type { CavernSettings, SpatialSourceNode } from '../types/cavern';
import { createDefaultScarlettState, createDefaultVocalRackState } from './scarlettVocalDsp';
import { evaluateSourcePosition, isSourceActiveForVariant } from './spatialMath';
import {
  computePropagationDelayMs,
  computeSourceBandWeights,
  computeSpatialXRayMetrics,
  computeSphericalPowermap,
  isSourceDraggable,
  SPEED_OF_SOUND_MPS,
  XRAY_TOTAL_CELLS,
} from './spatialXRay';

const BASE_SETTINGS: CavernSettings = {
  locale: 'en-US',
  hrirProfileId: 'sadie_d1_dfc',
  customHrirName: null,
  headphoneVirtualizer: true,
  distanceSimulation: true,
  propagationDelay6DoF: true,
  rolloffMode: 'logarithmic',
  matrixUpmix: true,
  cavernize3D: true,
  upmixEffectPct: 75,
  upmixSmoothPct: 80,
  speakerHeightVirt: false,
  dynamicNormalizer: true,
  limiterOnly: true,
  renderGainDb: 0,
  heightSqueezePct: 0,
  roomSize: { x: 10, y: 7, z: 10 },
  quickEqFilters: [],
  quickEqFileName: null,
};

describe('W13 6DoF Propagation Delay Physics', () => {
  it('computes exact speed-of-sound time-of-flight delay (343 m/s)', () => {
    assert.equal(SPEED_OF_SOUND_MPS, 343.0);
    assert.equal(computePropagationDelayMs(3.43, false), 10.0);
    assert.equal(computePropagationDelayMs(0, false), 0);
    assert.equal(computePropagationDelayMs(-2, false), 0);
    assert.equal(computePropagationDelayMs(6.86, false), 20.0);
  });

  it('always returns 0 ms for omnidirectional LFE channels', () => {
    assert.equal(computePropagationDelayMs(5.0, true), 0);
    assert.equal(computePropagationDelayMs(12.5, true), 0);
  });
});

describe('W13 Draggable Pinning & Layer Activation', () => {
  it('identifies 3D objects, Neural Stems, and Live Mic as draggable', () => {
    assert.equal(isSourceDraggable({ kind: 'object' }), true);
    assert.equal(isSourceDraggable({ kind: 'stem' }), true);
    assert.equal(isSourceDraggable({ kind: 'mic' }), true);
    assert.equal(isSourceDraggable({ kind: 'bed' }), false);
    assert.equal(isSourceDraggable({ kind: 'height' }), false);
    assert.equal(isSourceDraggable({ kind: 'lfe' }), false);
  });

  it('overrides dynamic object orbit when customPosition pin is set', () => {
    const sources = createDefaultSpatialSources();
    const obj1 = sources.find((s) => s.id === 'Obj1')!;
    const pin = { x: 1.5, y: 2.0, z: -1.0 };
    const pinned: SpatialSourceNode = { ...obj1, customPosition: pin };
    assert.deepEqual(evaluateSourcePosition(pinned, 12.5), pin);
  });

  it('keeps Neural Stems and Live Mic active regardless of stereo or spatial variant', () => {
    assert.equal(
      isSourceActiveForVariant(
        { id: 'StemVoc', kind: 'stem' },
        { isSpatial: false, objectCount: 0 },
        { matrixUpmix: false, cavernize3D: false },
      ),
      true,
    );
    assert.equal(
      isSourceActiveForVariant(
        { id: 'LiveMic', kind: 'mic' },
        { isSpatial: false, objectCount: 0 },
        { matrixUpmix: false, cavernize3D: false },
      ),
      true,
    );
  });
});

describe('W13 72-Cell Spherical 3-Band X-Ray Powermap', () => {
  it('concentrates dominant DoA toward a single front source with high coherence', () => {
    const frontOnly: SpatialSourceNode[] = [
      {
        id: 'C',
        shortLabel: 'C',
        name: 'Front Center',
        kind: 'bed',
        basePosition: { x: 0, y: 0, z: 4.5 },
        position: { x: 0, y: 0, z: 4.5 },
        rms: 0.8,
        solo: false,
        mute: false,
        synthFreqHz: 1000,
      },
    ];
    const map = computeSphericalPowermap(frontOnly, { x: 0, y: 0, z: 0 }, 'all');
    assert.equal(map.cells.length, XRAY_TOTAL_CELLS);
    assert.ok(map.dominantDoA.z > 0.95, `Expected front DoA z > 0.95, got ${map.dominantDoA.z}`);
    assert.ok(map.coherence > 0.95, `Expected high coherence > 0.95, got ${map.coherence}`);
    assert.ok(map.diffuseness < 0.05, `Expected low diffuseness < 0.05, got ${map.diffuseness}`);
  });

  it('yields high diffuseness for symmetric opposing sources', () => {
    const symmetric: SpatialSourceNode[] = [
      {
        id: 'F',
        shortLabel: 'F',
        name: 'Front',
        kind: 'bed',
        basePosition: { x: 0, y: 0, z: 4 },
        position: { x: 0, y: 0, z: 4 },
        rms: 0.6,
        solo: false,
        mute: false,
        synthFreqHz: 500,
      },
      {
        id: 'B',
        shortLabel: 'B',
        name: 'Back',
        kind: 'bed',
        basePosition: { x: 0, y: 0, z: -4 },
        position: { x: 0, y: 0, z: -4 },
        rms: 0.6,
        solo: false,
        mute: false,
        synthFreqHz: 500,
      },
    ];
    const map = computeSphericalPowermap(symmetric, { x: 0, y: 0, z: 0 }, 'all');
    assert.ok(map.coherence < 0.05, `Expected near-zero coherence, got ${map.coherence}`);
    assert.ok(map.diffuseness > 0.95, `Expected high diffuseness, got ${map.diffuseness}`);
  });

  it('isolates Low (<250 Hz) vs High (>2.5 kHz) directional energy cleanly', () => {
    const lowWeight = computeSourceBandWeights({ kind: 'lfe', synthFreqHz: 55 });
    const highWeight = computeSourceBandWeights({ kind: 'object', synthFreqHz: 4200 });
    assert.ok(lowWeight.low > 0.85);
    assert.ok(highWeight.high > 0.6);

    const dual: SpatialSourceNode[] = [
      {
        id: 'Sub',
        shortLabel: 'LFE',
        name: 'Sub',
        kind: 'lfe',
        basePosition: { x: -4, y: 0, z: 0 },
        position: { x: -4, y: 0, z: 0 },
        rms: 0.8,
        solo: false,
        mute: false,
        synthFreqHz: 55,
      },
      {
        id: 'Air',
        shortLabel: 'HI',
        name: 'Air Bell',
        kind: 'object',
        basePosition: { x: 4, y: 0, z: 0 },
        position: { x: 4, y: 0, z: 0 },
        rms: 0.8,
        solo: false,
        mute: false,
        synthFreqHz: 4200,
      },
    ];

    const lowMap = computeSphericalPowermap(dual, { x: 0, y: 0, z: 0 }, 'low');
    const highMap = computeSphericalPowermap(dual, { x: 0, y: 0, z: 0 }, 'high');
    assert.ok(lowMap.dominantDoA.x < -0.7, 'Low band should point left toward LFE');
    assert.ok(highMap.dominantDoA.x > 0.7, 'High band should point right toward High object');
  });
});

describe('W13 Studio Spatial X-Ray Metrics & Instant A/B Bypass', () => {
  it('activates LGSR coupling/SPX/fusion for lossy E-AC-3 JOC and drops fusion to 0% on A/B bypass', () => {
    const sources = createDefaultSpatialSources();
    const eac3Variant = {
      id: 'eac3',
      label: 'E-AC-3 JOC 768k',
      codec: 'Dolby Digital Plus JOC (E-AC-3)',
      container: 'MP4',
      channels: '5.1.2 + Objects',
      sampleRateHz: 48000,
      bitDepth: 24,
      bitrateKbps: 768,
      isSpatial: true,
      objectCount: 16,
    };
    const trueHdVariant = DEMO_TRACKS[0].variants[0];
    const vocal = createDefaultVocalRackState();
    const scarlett = createDefaultScarlettState();

    const activeLgsr = computeSpatialXRayMetrics(
      sources,
      { x: 0, y: 0, z: 0 },
      eac3Variant,
      BASE_SETTINGS,
      vocal,
      scarlett,
      false,
      true,
    );
    assert.equal(activeLgsr.isLossySource, true);
    assert.equal(activeLgsr.couplingStartHz, 10000);
    assert.equal(activeLgsr.spxStartHz, 12500);
    assert.ok(activeLgsr.flacFusionPct >= 65);

    const bypassedLgsr = computeSpatialXRayMetrics(
      sources,
      { x: 0, y: 0, z: 0 },
      eac3Variant,
      BASE_SETTINGS,
      vocal,
      scarlett,
      true,
      true,
    );
    assert.equal(bypassedLgsr.flacFusionPct, 0);
    assert.equal(bypassedLgsr.gccPhatOffsetSamples, 0);

    const losslessMetrics = computeSpatialXRayMetrics(
      sources,
      { x: 0, y: 0, z: 0 },
      trueHdVariant,
      BASE_SETTINGS,
      vocal,
      scarlett,
      false,
      true,
    );
    assert.equal(losslessMetrics.isLossySource, false);
    assert.equal(losslessMetrics.couplingStartHz, 0);
    assert.equal(losslessMetrics.spxStartHz, 0);
  });

  it('zeroes Vocal Chain GR meters when Vocal A/B bypass is active', () => {
    const sources = createDefaultSpatialSources();
    const variant = DEMO_TRACKS[0].variants[0];
    const vocalActive = createDefaultVocalRackState();
    const vocalBypassed = { ...vocalActive, bypass: true };
    const scarlett = createDefaultScarlettState();

    const activeM = computeSpatialXRayMetrics(
      sources,
      { x: 0, y: 0, z: 0 },
      variant,
      BASE_SETTINGS,
      vocalActive,
      scarlett,
      false,
      true,
    );
    const bypassedM = computeSpatialXRayMetrics(
      sources,
      { x: 0, y: 0, z: 0 },
      variant,
      BASE_SETTINGS,
      vocalBypassed,
      scarlett,
      false,
      true,
    );

    assert.ok(activeM.vocalCompGrDb > 0);
    assert.equal(bypassedM.vocalCompGrDb, 0);
    assert.equal(bypassedM.vocalDeEsserGrDb, 0);
    assert.equal(bypassedM.vocalSidechainDuckDb, 0);
    assert.equal(bypassedM.vocalBypassed, true);
  });
});
