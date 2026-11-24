# Multi-Stem Ground-Truth Audio Verification Protocol (`AUDIO_GROUND_TRUTH_PROTOCOL.md`)

> STYLE|lossless symbolic telegraphic EN ONLY. Permanent on-demand verification & regression protocol for Cavern / CavernizeAndroid DSP, Spatial Karaoke, LGSR Lossless Twin, Spatial Upmix, Atmos Moving Objects, & 3D Room engine.

## 1. Purpose & Invocation (Permanent On-Demand)
- **Trigger:** Mandatory whenever touching `Cavern/Neural/*`, `Cavern/Restoration/*`, `Cavern/Spatial/*`, `Cavern/Remapping/*`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/*`, `CavernSamples/Reusable/Cavernize.Logic/Rendering/*`, `CavernSamples/Reusable/Cavernize.Logic/Models/{TrackSourceItem,CavernizeTrack}.cs`, or Karaoke/LGSR/Upmix/3D-Room wiring in `CavernizeAndroid`.
- **Command:** `python3 -B .agents/scripts/run_audio_ground_truth.py` (optional: `--verbose`, `--suite GT-04`, `--json /tmp/gt_report.json`).
- **Zero-Flake Physics Rule:** Uses deterministic multi-stem synthetic ground truth (known exact isolated stems: `LeadVocalMono`, `LeadVocalStereoWide`, `BackingVocal`, `SubBass 55 Hz`, `StereoInstruments`, `DiscreteAtmosFC`, `MovingAtmosObject`) + real compiled `Cavern.dll` & `Cavernize.Logic.dll` so every run measures exact physical dB, sample lag, phase coherence, LFE bit-exactness, and NaN/clipping with zero copyrighted external files. Complements live S25 Ultra checks (`omni_agent.py hot-exec` / `acoustic_probe`).

## 2. Ground-Truth Signal Bank (Deterministic Multi-Stem Fixture)
- **Sample Rates:** `44100 Hz` (CD/FLAC) && `48000 Hz` (Dolby Atmos E-AC-3 JOC / TrueHD / HRIR Virtualizer bus).
- **Isolated Stems (Exact Ground Truth):**
  - `LeadVocalMono`: centered harmonic formant (`440 Hz + 880 Hz + 1320 Hz`, syllable envelope @ `2.5 Hz`).
  - `LeadVocalStereoWide`: stereo double-tracked / widened vocal (`L = LeadMono + 0.45*sin(620 Hz)`, `R = LeadMono - 0.45*sin(620 Hz)`, exercising orthogonalized 2-reference subspace `(V_M, V_So)`).
  - `BackingVocal`: wide stereo harmony (`330 Hz` L, `495 Hz` R).
  - `SubBassLfe`: `55 Hz` pure sub-bass (below `120 Hz` LR4 crossover) routed to `.1` LFE in Atmos or bass stem in stereo.
  - `StereoInstruments`: uncorrelated wide stereo instruments (`185 Hz` + `1180 Hz` L, `245 Hz` + `1640 Hz` R; 0 coherence with lead vocal).
  - `MovingAtmosObject`: dynamic 3D `Source` orbiting smoothly across azimuth `[-60°, +60°]` & elevation `[+15°, +65°]`.
- **Synthetic Misaligned FLAC Lossless Twin (Real-World Mastering Delta):**
  - Same multi-stem truth shifted by **`+187.35 ms` (`+8992.8 samples` @ 48 kHz)** lead-in offset + **`+25 ppm` clock drift** + **`-1.2 dB` loudness offset**, while Atmos stream has E-AC-3-style HF roll-off above `10 kHz` (`SpxStartHz = 10000`).

## 3. Pairwise / Orthogonal Verification Matrix & Quantitative Pass Gates

| Suite ID | Pairwise Environment / Signal Chain | Measured Metrics & Hard Pass Gates |
|---|---|---|
| **GT-01** | **LGSR WOLA Impulse Latency & 0.000° Phase Lock** (`LgsrSpectralFusionEngine` 12ch Pre-HRTF bus) | Measured impulse delay `== LgsrSpectralFusionEngine.LatencySamples (512)` exact; LFE channel `maxAbsDiff == 0.0f`; 0 NaN/Inf/Clip |
| **GT-02** | **Pure Stereo FLAC (44.1 & 48 kHz) × Direct 2.0 & Virtualizer 7.1.4 × Instant Blind & AI Stereo Stems** | Direct 2.0 & Virtualizer 7.1.4 Blind: Vocal atten `>= 18.0 dB`, Side Instrument delta `< 1.0 dB` (parity `< 1.5 dB` between Direct & Virtualizer); AI Stereo Stems (wide double-tracked vocal) on BOTH Direct 2.0 & 12ch Virtualizer: Vocal atten `>= 22.0 dB`, Instrument delta `< 1.2 dB`, 0 NaN/Clip |
| **GT-03** | **60-Second 44.1 kHz → 48 kHz Fractional Playhead Drift Check** (`RealtimePlayerEngine` accumulator math + `NeuralStemStageProcessor` rate lock) | Cumulative playhead drift over 60 s (`2812` blocks @ 1024 frames) `<= 0.5 samples` (previously `+562.5 samples`!); 44.1 kHz stem Karaoke at `t = 55 s` still achieves `>= 22.0 dB` vocal atten |
| **GT-04** | **Pure Dolby Atmos (7.1.4 Discrete `FC` + Beds + Objects) × Instant Blind & AI Stereo Stems & Offline Export** | Blind discrete `FC`: `FC` vocal atten `>= 18.0 dB` AND `FL`/`FR` uncorrelated instrument delta `< 1.2 dB` (zero `FL`/`FR` guitar pollution!); AI Stereo Stems (live & `ProcessOfflineMultichannel`): Vocal atten `>= 22.0 dB` across all beds, Instrument delta `< 1.2 dB`, LFE `maxAbsDiff == 0.0f` |
| **GT-05** | **Dolby Atmos + Misaligned FLAC Lossless Twin (`+187.35 ms`, `+25 ppm`, `-1.2 dB`) + ACTIVE LGSR Pre-HRTF -> Spatial Karaoke** | `SubSampleSyncEngine` residual lag `<= 0.5 samples`; `TryExtractFullAlignedStereo` aligned lag `<= 1.0 sample`; **LGSR (512-sample WOLA) running BEFORE `NeuralStemStageProcessor`** via `UpstreamLatencySamplesProvider` -> Vocal atten `>= 20.0 dB` (previously collapsed to `~0–3 dB` without latency lock!), Instrument delta `< 1.5 dB`, LFE `maxAbsDiff == 0.0f`, 0 NaN/Clip |
| **GT-06** | **W46 3-Stage Async CPU↔GPU Parity + Progressive 44.1→48 kHz Streaming** | `ProcessTrackPipelined` vs `ProcessTrack` `maxAbsDiff == 0.0f`; `ReadySampleCount` monotonic; progressive chunk-2 live Karaoke `>= 15.0 dB` vocal atten; seek-ahead guard 0 NaN/crash |
| **GT-07** | **Atmos Moving Objects, LR4 Dedicated `.1` LFE & SpatialXRayPowermap Smoothing** | LR4 `120 Hz` crossover `55 Hz` LFE isolation `> 12 dB` & flat sum `±0.35 dB`; `SpatialXRayPowermap` step-change first-frame jump `< 45%` & converges smoothly over `>= 3` frames |
| **GT-08** | **Zero Double-EQ Guard & Dual-Path Headroom** (`AutoEqPostProcessingStage` × DFC/Raw HRIR + BS.1770) | Active DFC HRIR prevents duplicate treble ear-gain stacking; `0` NaN/Clip; BS.1770 True-Peak `<= -1.0 dBTP` |
| **GT-09** | **Measured BRIR Guard & ISM Externalization** (`IsmEarlyReflectionProcessor` + Sabine/Eyring) | Active measured BRIR bypasses synthetic ISM reflections (`0` double-room coloration); synthetic room matches Sabine/Eyring RT60 bounds |
| **GT-10** | **Spatial Object Cortex Anti-Oscillation Choreography** (`SpatialObjectCortex` SLERP LUT + Motion Budget) | Sub-band onset/flux locks stationary `.1` LFE & centered vocals; smooth angular velocity clamp (`0` jitter); `0 B` RT GC |
| **GT-11** | **Karaoke Pure Instrumental vs Keep Backing Vocals** (`NeuralStemStageProcessor` 6-stem lead+backing suppression) | KeepBacking attenuates lead `>= 20 dB` & preserves backing `<= 1.5 dB` + instruments `<= 1.2 dB`; PureInstrumental attenuates BOTH lead & backing `>= 20 dB` & preserves instruments `<= 1.2 dB` |
| **GT-12** | **Omniscient Cortex `ComponentBehaviorProfile` & `ComponentConflictGovernor`** (`--suite spatial-cortex-rt`) | Auto-profiled Cavern DSP blocks (`TruePeakLimiter`, `Iso226LoudnessFilter`, `SpatialDialogEnhancer`, `Normalizer`, `LiveVocalChainDsp`, `HighShelf`) predict unseen RMS levels within `<= 1.0 dB`; governor detects hardware Scarlett Air+Drive, measured BRIR, stacked makeup & enforces `<= -1.0 dBTP` |
| **GT-13** | **Phase 3 `Slot 2D-SHIELD` (`MultiRefFdkfShield` + Two-Path Shadow Filter + `AcousticConstraintGovernor`)** (`--suite spatial-cortex-rt`) | Stereo music-bleed `ERLE >= 25.0 dB`, singing double-talk vocal delta within `±1.5 dB`, post-Scarlett `GainStepEpoch` reconvergence `ERLE >= 22.0 dB`, closed-loop `GBF improvement >= +8.0 dB` (`margin >= 5.5 dB`), bit-exact headphone bypass, `0 B` RT GC, `< 1.0 ms/block` |

## 4. Failure Triage Protocol
1. Run `python3 -B .agents/scripts/run_audio_ground_truth.py --verbose` (or `--suite spatial-cortex-rt` for Cortex fast loop).
2. Locate the exact failing `GT-XX` cell & metric (`vocalAttenDb`, `instDeltaDb`, `lfeMaxDiff`, `lagSamples`, `driftSamples`, `maxUnseenRmsErrDb`, `musicBleedErleDb`, `gbfImprovementDb`, `rtAllocBytes`, `nanCount`, `clipCount`).
3. Fix the physical/DSP root cause in `Cavern/` or `Cavernize.Logic/` — NEVER relax quantitative gates unless physics-justified & recorded in `BUILD_LESSONS.md`.
4. Re-run `python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings` && `python3 -B .agents/scripts/run_audio_ground_truth.py` before pushing.
