---
name: cavernize-vocal-cortex
description: Principal Vocal Producer, Mixing/Mastering Engineer & Black-Box System-ID DSP Architect playbook (MOD-AUDIO-ENG-DIRECTOR). Guides mobile/desktop DAW black-box capture (UnifiedSuperStimulus), 7-dimension residual falsification (ESR <= -30 dB), artifact-aware isolated vocal DNA forensics, and the Unified Omniscient Vocal Cortex (OmniscientVocalStateBus). Use whenever designing, auditing, or implementing vocal DSP, black-box effect cloning, pitch/formant correction, or live karaoke auto-mixing.
---

# Cavernize Omniscient Vocal Cortex & Black-Box System-ID Skill (`MOD-AUDIO-ENG-DIRECTOR`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim `path:line`, `symbol()`, equations/invariants).

## 1. Autonomous Chief Audio Engineer Mandate (`MOD-AUDIO-ENG-DIRECTOR`)
- **Role:** Act permanently as **Principal Vocal Producer + Grammys-Class Mixing/Mastering Engineer + Black-Box System-ID DSP Architect**.
- **Zero User Burden:** Never ask user which audio engineering techniques, studio routing topologies, or psychoacoustic safeguards to apply. Proactively exercise **Studio Eureka Authority** in every Recon/Impl wave.
- **Tabu Multi-Pass Research (`MOD-TABU-RECON`):** Before designing or modifying vocal DSP waves, maintain `.agents/docs/KNOWN_MECHANISMS_LEDGER.md` as a negative-constraint accumulator (`FORBIDDEN_TO_REPEAT`). Run sequential orthogonal passes across DAFx/AES/IEEE literature, studio vocal production, live FOH acoustics, speech coding, & adversarial red-team failure analysis until marginal novelty delta `== 0`.

## 2. Five-Layer Black-Box System-ID & In-App Mobile DAW Capture (`Slot 2D-CAPTURE` + `Slot 2D-FORGE`)
1. **Layer 1 — Sub-Sample Sync & Determinism Gate (Pre-Sweep Mandatory):**
   - Align capture I/O to `0.001` sample using `Cavern/Restoration/SubSampleSyncEngine.cs` (`GCC-PHAT` + fractional parabolic/Kaiser-sinc delay compensation) via Barker/MLS chirp headers.
   - Run **Pass-to-Pass Determinism Null Test** (`y_1[n] - y_2[n]` w/ frozen knobs):
     - `max(|y_1 - y_2|) < -130 dBFS` -> deterministic time-invariant path -> phase-locked `Pre-Emphasis ESR + MR-STFT` loss.
     - `max(|y_1 - y_2|) > -120 dBFS` -> stochastic thermal noise/dither or asynchronous free-running LFO -> separate noise/LFO generator model + phase-invariant `MR-STFT + Hilbert Envelope Modulation` loss.
2. **Layer 2 — Multi-Regime Excitation Battery (`UnifiedSuperStimulus.wav` for Single-Pass Mobile/Desktop DAW Bounce):**
   - **Multi-Level Synchronized Swept-Sine (Farina/Novak)** @ `-36, -24, -12, -6, 0 dBFS` -> deconvolves linear $H_1(\omega)$ & harmonic kernels $H_2(\omega)..H_7(\omega)$ + pre/de-emphasis curves (`Fresh Air`/Dolby-A).
   - **Prime/Irrational Sparse Multitone** -> measures full **Intermodulation Distortion (IMD)** carpet ($f_1 \pm f_2, 2f_1 - f_2$) & Nyquist foldback aliasing (deducing internal oversampling `1x/2x/4x/8x` & anti-aliasing filter slope).
   - **Stepped-Level Tone Bursts & Hysteresis Pulses** -> extracts compressor/transient-shaper (`1176`/`LA-2A`/`Heatwave`) attack/release ballistics, T4 optical dual-release memory, knee, lookahead, & Peak vs. RMS detection.
   - **Sliding High-$Q$ Narrow Resonances ($Q \in [8, 40]$) + Low/High Sidechain Pumping Probe** -> extracts dynamic spectral suppression (`Soothe2`-class) selectivity & multiband vs. broadband sidechain coupling.
   - **Kinematic $F_0$ Glissando (`5..120 st/s`) + Vibrato (`4..8 Hz`) + Formant Chirps** -> extracts pitch quantizer retune speed curves, hysteresis deadbands, & formant warper matrices.
   - **Stereo & Mid/Side Cross-Coupling (`L-only`, `R-only`, `Pure Mid`, `Pure Side`)** -> extracts dual-mono vs. stereo-link vs. M/S matrixing.
3. **Layer 3 — Parameter Space Exploration without Combinatorial Explosion:**
   - **1D Knob Taper Discovery** (individual knob sweeps at anchor points mapping normalized `[0,1]` to physical dB/Hz/ms inflection curves) + **Continuous Automation Slew Probe** (extracting internal parameter-smoothing time constant).
   - **Sobol Quasi-Monte Carlo (`128..512` pts) + Bayesian Active Probing** (concentrating samples in high-gradient regions $\|\nabla_\theta \mathcal{L}\|$).
4. **Layer 4 — Continuous-Time Grey-Box Organ Distillation (`.cvfx` Primitives, NOT Isolated Plugins):**
   - Formulate organs in continuous-time / bilinear / matched-$Z$ / state-space ($\dot{x} = Ax + Bu$) + 2nd-order Chebyshev-ADAA waveshaping for **native sample-rate invariance (`44.1 / 48 / 96 / 192 kHz`)** and **`0-sample` added latency**.
   - Embed **`All-Pass Phase-Match Curve`** (`0.000°` parallel dry/wet cancellation) and **`Unit-Loudness Auto-Gain Law`** (`<= 0.15 LUFS` jump) in every `.cvfx` organ.
5. **Layer 5 — 7-Dimension Residual Falsification Gate (`MOD-RESIDUAL-GATE`):**
   - Decompose out-of-distribution residual $e(t) = y_{\text{ref}}(t) - \hat{y}(t)$ across: (1) Static Amplitude, (2) Linear Spectrum, (3) Phase/Group Delay, (4) Dynamic Memory/Hysteresis, (5) Stereo/M-S Crosstalk, (6) Pitch/Formant Kinematics, (7) Stochastic/LFO Floor. Require Pre-Emphasized `ESR <= -30 dB` (`< 0.1%`).

## 3. Artifact-Aware Vocal Forensics & Unified Omniscient Vocal Cortex (`Slot 2D-DNA` + `Slot 2D-CORTEX`)
- **Cross-Stem Anti-Bleed Gate:** Sample separated vocal frames ONLY where $\text{SIR}(t,f) = 10\log_{10}(|V(t,f)|^2 / |I(t,f)|^2) > +12\text{ dB}$ && `VoicedProb > 0.85`; sample reverb decay ONLY where instrumental has zero coincident percussive transients.
- **Omniscient Acoustic Nervous System (`OmniscientVocalStateBus`):** Broadcast per-sample/hop `F0Hz`, `HarmonicCombHz[1..12]`, `ActiveScaleMask12Bit`, `PhonemeClass` (`Vowel|VoicedConsonant|Sibilant|Plosive|InharmonicBreath|Silence`), `SingerEffortLombardIndex`, `Target0VuGainRiderDb`, & `InstantaneousCompressionGrDb` across all organs:
  1. **Target `0 VU (-18 dBFS)` Pre-Rider + Dynamic Proximity Low-Shelf + `130 Hz` Plosive Sidechain HPF + Inharmonic De-Breath Gate + Reference-Guided Speaker Bleed AEC & Anti-Howling Notch.**
  2. **Consonant-Bypassed & Vibrato-Aware Kinematic Pitch/Formant Organ** (`~4.5 ms` causal WSOLA, bypassing pitch shift for `15–25 ms` on plosives/sibilants, dual-speed vibrato preservation, & exact sub-sample backing delay sync in `RealtimePlayerEngine`).
  3. **$F_0$-Normalized & Lombard-Effort-Adaptive Fused State-Space Timbre Organ** (cross-gender baritone/soprano safe).
  4. **Pitch-Shielded Spectral Resonance Tamer** (`Soothe`-organ protecting active $k F_0(t)$ harmonics while notching `2–5.5 kHz` harsh resonances).
  5. **Phoneme-Inhibited Air & Asymmetric Tube/Tape Saturation** (`Fresh Air`/Cheby-ADAA2 organ pre-emptively inhibited on `Sibilant`/`Breath` + `15 Hz` dynamic DC blocker + Compression-$\Delta\text{GR}$-to-Harmonic density coupling).
  6. **Syllable-Ducked & Drum-Groove-Locked Spatial Cortex** (MicroShift Haas Doubler, BPM/DrumStem-locked Throw Delay & 16-line 6-octave Velvet-FDN w/ user room `DRR/RT60` subtraction + **Mid-Only `1–3.5 kHz` Dynamic Vocal Pocket** on instrumental bus).
- **3-Tier Neural Cortex Conductor (AI Control Intelligence over Zero-Latency DSP Organs):**
  1. **Tier 1 (Nearline `Slot 2D-DNA` + `Slot 7B` ONNX Runtime):** `Self-Supervised Style-Inversion Hypernetwork` (`~2–5 MB` `.onnx`) predicts per-section continuous organ weights $\alpha_k \in [0, 1]$ + cross-organ parameter priors from `(LeadVocal, DryVocal, Instrumental)`.
  2. **Tier 2 (Hard Real-Time `Slot 2D-CORTEX` Audio Thread):** `Zero-GC Micro-GRU / State-Space Spinal Reflex Net` (`< 25 KB` weights, SIMD `< 0.05 ms/block`, `0 B` alloc) outputs continuous probabilities $[P_{\text{vowel}}, P_{\text{sibilant}}, P_{\text{plosive}}, P_{\text{breath}}, P_{\text{vibrato}}, P_{\text{belting}}, P_{\text{sustain}}]$ to smoothly morph organ synaptic weights sample-by-sample.
  3. **Tier 3 (On-Device Longitudinal Personalization):** `Singer & Mic Co-Adaptation Memory` refines voiceprint priors, vocal register comfort zone, & recurrent room/mic resonances across sessions.
