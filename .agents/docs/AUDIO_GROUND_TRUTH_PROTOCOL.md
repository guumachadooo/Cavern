# Multi-Stem Ground-Truth Audio Verification Protocol (`AUDIO_GROUND_TRUTH_PROTOCOL.md`)

> STYLE|lossless symbolic telegraphic EN ONLY. Permanent verification & regression protocol for Cavern / CavernizeAndroid DSP, Neural Karaoke, Spatial Upmix, Atmos + FLAC Lossless Twin, & 3D Room engine.

## 1. Purpose & When To Run
- **Trigger:** Mandatory whenever touching `Cavern/Neural/*`, `Cavern/Spatial/*`, `Cavern/Remapping/*`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/*`, `CavernSamples/Reusable/Cavernize.Logic/Rendering/*`, `CavernSamples/Reusable/Cavernize.Logic/Models/{TrackSourceItem,CavernizeTrack}.cs`, or Karaoke/Upmix/3D-Room wiring in `CavernizeAndroid`.
- **Command:** `python3 -B .agents/scripts/run_audio_ground_truth.py`
- **Zero-Flake Rule:** Uses deterministic multi-stem synthetic ground truth (known exact isolated stems: `LeadVocal`, `BackingVocal`, `Bass 55 Hz`, `Instruments`, `MovingAtmosObject`) + deterministic mock/real ONNX sessions so CI and Linux/Android containers validate exact physical dB/sample invariants without downloading external copyrighted datasets (MUSDB18/MoisesDB). Real-world device validation complements this via `hot_exec` + `acoustic_probe` on S25 Ultra.

## 2. Ground-Truth Signal Bank (Deterministic Multi-Stem Fixture)
- **Sample Rates:** `44100 Hz` (CD/FLAC) && `48000 Hz` (Atmos/E-AC-3/TrueHD).
- **Isolated Stems (Exact Ground Truth):**
  - `LeadVocal`: centered harmonic formant (`220 Hz + 660 Hz + 1320 Hz`, amplitude-modulated syllables at `2.5 Hz`).
  - `BackingVocal`: stereo wide harmony (`330 Hz` L, `440 Hz` R).
  - `Bass`: sub-bass `55 Hz` (below `120 Hz` LFE crossover) + `110 Hz` harmonic.
  - `Other/Drums`: stereo wide `880 Hz` / `1760 Hz` rhythmic impulses (above `120 Hz` crossover).
  - `MovingAtmosObject`: dynamic 3D `Source` orbiting smoothly across azimuth `[-60°, +60°]` & elevation `[+15°, +65°]`.
- **Synthetic Misaligned FLAC Lossless Twin:** Derived from the same multi-stem truth with intentional **`+187 ms` lead-in offset** + **`+25 ppm` clock drift** + `-1.2 dB` loudness offset (matching real Apple Music Atmos vs Qobuz/Tidal FLAC mastering differences).

## 3. Verification Matrix & Quantitative Pass Gates

| Suite ID | Environment / Combination | Quantitative Pass Gate |
|---|---|---|
| **GT-01** | **W46 3-Stage Async CPU↔GPU Pipeline** (`Waveform`, `MdxNetStft4Ch`, `ScnetStft4Ch`, `Overlap=2/4`, `TTA=false/true`) | `maxAbsDiff == 0.0f` (`0.00 dB` exact IEEE-754 parity vs sequential `ProcessTrack`) && `ReadySampleCount` strictly monotonic -> `totalSamples` |
| **GT-02** | **Stereo 44.1 kHz / 48 kHz + Neural Karaoke** (Lead Only / Full Removal / Vocal Isolation) | Vocal band suppression `>= 18.0 dB` on Karaoke, instrumental preservation `< 1.5 dB` loss; Vocal Isolation retains vocal within `<= 1.5 dB` while attenuating backing `>= 18.0 dB` |
| **GT-03** | **Atmos + Misaligned FLAC Lossless Twin** (`+187 ms` offset, `25 ppm` drift) | `TryExtractFullAlignedStereo` residual lag `<= 2 samples`; aligned neural extraction matches Atmos timeline; `NeuralStemStageProcessor` does NOT clobber multichannel discrete `FrontCenter` bed via blind stereo sum fallback |
| **GT-04** | **Native Atmos Moving 3D Objects + AI Upmix** (`Stems3DObjects` / `HybridNeuralDirAc`) | `ShouldRenderLiveNeuralSourcesLocked == false` on `HasObjects` / `ChannelCount > 2`; dynamic 3D objects keep moving coordinates (displacement `> 0.5 m` across render window, zero static collapse) |
| **GT-05** | **Stereo AI Upmix Dedicated `.1` LFE** (`HybridNeuralDirAc` 7.1.4 & `Stems3DObjects`) | Dedicated `ReferenceChannel.ScreenLFE` (`Source.LFE == true`) attached; LR4 `120 Hz` crossover isolates `55 Hz` bass into `.1` (`> 12 dB` LFE-vs-HP separation at `55 Hz` vs `500 Hz`) && flat summed magnitude (`±0.25 dB`) |
| **GT-06** | **3D Room Visibility, Solo & Reset** (Stereo + Matrix/Cavernize Upmix) | `TrackSourceItem.BuildFromAttachedSources` matches `Listener.ActiveSources.Count` 1:1; Soloing 1 virtual upmixed source mutes all other `N-1` active sources; dragging `CustomPosition` then calling `ResetAllCustomPositions` restores `DefaultNormalizedPosition` (`err < 1e-4`) |
| **GT-07** | **3D Spherical Energy Map (`SpatialXRayPowermap`)** | Step-change in source direction (`Left -> Right`) smoothly converges over `>= 3` frames (first-frame jump `< 45%` of total delta, eliminating 187 Hz visual thrashing) |

## 4. Failure Triage Protocol
1. Re-run `python3 -B .agents/scripts/run_audio_ground_truth.py --verbose`.
2. Inspect exact failing `GT-XX` metric (lag, dB suppression, maxAbsDiff, LFE crossover, or source count).
3. Fix root cause in DSP/Logic (never relax pass gates unless physics-justified & documented).
4. Re-run `check_android_cs.py --ci-parity --strict-warnings` + `run_audio_ground_truth.py` before pushing.
