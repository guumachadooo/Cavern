---
name: cavernize-vocal-cortex
description: Principal Vocal Producer, Mixing/Mastering Engineer & Black-Box System-ID DSP Architect playbook (MOD-AUDIO-ENG-DIRECTOR). Guides mobile/desktop DAW black-box capture (UnifiedSuperStimulus), 7-dimension residual falsification (ESR <= -30 dB), artifact-aware isolated vocal DNA forensics, and the Unified Omniscient Vocal Cortex (OmniscientVocalStateBus). Use whenever designing, auditing, or implementing vocal DSP, black-box effect cloning, pitch/formant correction, or live karaoke auto-mixing.
---

# Cavernize Omniscient Vocal Cortex & Black-Box System-ID Skill (`MOD-AUDIO-ENG-DIRECTOR`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim `path:line`, `symbol()`, equations/invariants).

## 1. Autonomous Chief Audio Engineer & Anti-Dogma Mandates (`MOD-AUDIO-ENG-DIRECTOR` + `MOD-SEED-FALSIFY`)
- **Anti-Dogma Evolutionary Gate (`MOD-SEED-FALSIFY` + `MOD-DYNAMIC-REPLAN`):** Treat ALL pre-drafted operators, AI placements, & wave specs in `.agents/docs/PHASE3_VOCAL_CORTEX_ROADMAP.md` strictly as **`[SEED-v2: Floor Hypothesis]`** — FORBIDDEN to implement blindly as final spec. Before EVERY wave, challenge `[SEED-v2]` via `MOD-TABU-RECON` + run an **AI-vs-Physics Justification Tournament** (enforcing **Universal Only-Where-Invariant AI Rule**: neural nets strictly restricted to plugin-agnostic & room-agnostic human-voice sensors so adding priors or switching rooms NEVER triggers retraining) -> dynamically mutate roadmap & Slot Topology in-place whenever a superior path emerges.
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
4. **Layer 4 — Seconds-Scale Psychoacoustic Prior Harvesting (`~5–30s`, `.cvfx` Manifold Priors, NOT Plugin Boxes):**
   - Probe reference plugins/DAWs via closed-form Farina deconvolution + Levenberg-Marquardt/ridge optimization (`~5–30 seconds` on CPU) **NOT to jail runtime inside fixed plugin emulations**, but to harvest fine-tuned **Psychoacoustic Sweet-Spot Priors ($\mathcal{P}_{\text{psycho}}$)** (cochlear release acceleration $\dot{g},\ddot{g}$, pleasant harmonic/IMD slopes, narrow-$Q$ thresholds, modal decay laws) and a Golden Benchmark (`ESR <= -30 dB`).
   - Parameterize all kernels in continuous-time / state-space ($\dot{x} = Ax + Bu$) + `Cheby-ADAA2` for **native sample-rate invariance (`44.1..192 kHz`)** & **`0-sample` latency**, embedding **`All-Pass Phase-Match`** & **`Unit-Loudness Auto-Gain`** laws.
5. **Layer 5 — 7-Dimension Residual Falsification Gate (`MOD-RESIDUAL-GATE`):**
   - Decompose out-of-distribution residual $e(t) = y_{\text{ref}}(t) - \hat{y}(t)$ across: (1) Static Amplitude, (2) Linear Spectrum, (3) Phase/Group Delay, (4) Dynamic Memory/Hysteresis, (5) Stereo/M-S Crosstalk, (6) Pitch/Formant Kinematics, (7) Stochastic/LFO Floor. Require Pre-Emphasized `ESR <= -30 dB` (`< 0.1%`).

## 3. First-Principles Direct Acoustic Manifold Engine (`Slot 2D-DNA` + `Slot 2D-CORTEX`)
- **Cross-Stem Anti-Bleed Gate:** Sample separated vocal frames ONLY where $\text{SIR}(t,f) = 10\log_{10}(|V(t,f)|^2 / |I(t,f)|^2) > +12\text{ dB}$ && `VoicedProb > 0.85`; sample reverb decay ONLY where instrumental has zero coincident percussive transients.
- **Untethered Direct Audio Manipulation (`5 Unified Continuous Operators` on `OmniscientVocalStateBus`):**
  1. **Pre-Conditioning:** `0 VU (-18 dBFS)` input rider + `130 Hz` plosive sidechain HPF + `InharmonicBreathGate` + reference-guided speaker AEC & anti-howling notch.
  2. **$\mathcal{O}_{F_0,\text{Tract}}(t,f)$ — Continuous Kinematic Pitch & Tract Geometry:** `~4.5 ms` causal WSOLA w/ `15–25 ms` consonant bypass, dual-speed vibrato preservation, cepstral formant sculpting, & sub-sample backing delay sync.
  3. **$\mathcal{O}_{\text{Spec}}(t,f)$ — Unified Time-Frequency Spectral Sculptor:** Single zero-latency phase-coherent surface $G(t,f)$ uniting $F_0$-normalized timbre matching, pitch-shielded narrow-$Q$ suppression (protecting $k F_0$), sibilance control, Lombard effort compensation, & ephemeral room/proximity correction.
  4. **$\mathcal{O}_{\text{Dyn}}(t)$ — Cochlear-Constrained Envelope Sculptor:** Synthesizes direct gain trajectory $g(t)$ matching song syllable attack/body & crest under Moore-Glasberg auditory smoothness bounds ($\|\dot{g}\|, \|\ddot{g}\| \le \kappa_{\text{cochlea}}$ from $\mathcal{P}_{\text{psycho}}$), surpassing 1960s hardware circuit compromises.
  5. **$\mathcal{O}_{\text{Harm}}(t, k F_0)$ — Harmonic-Locked & Masking-Guided Nonlinearity:** Shapes $2F_0..NF_0$ & `10–18 kHz` air via `Cheby-ADAA2` + `15 Hz` dynamic DC block, silenced by $P_{\text{sibilant}}/P_{\text{breath}}$ & coupled to $\Delta \text{GR}_{\text{dB}}$ w/ all-pass phase-matched parallel sum.
  6. **$\mathcal{O}_{\text{Space}}(t,f,\mathbf{x})$ — Continuous Spatial Decay Field:** Unifies section-morphed MicroShift Haas detune, syllable-ducked `DrumStem`-BPM Throw Delay, & 16-line 6-octave Velvet-FDN w/ live ephemeral `DRR/RT60(f)` subtraction + **Mid-Only `1–3.5 kHz` Dynamic Vocal Pocket**.
- **Zero-Retraining Manifold Solver, Ephemeral Room Tracker & Universal-Only AI:**
  1. **Zero-Retraining Direct Manifold Trajectory Solver (`OMP + Coordinate Descent`, `< 50 ms`):** Solves $\min_{\theta_{\mathcal{O}} \in \mathcal{P}_{\text{psycho}}} \|\mathbf{D}_{\text{song}} - \mathcal{F}(\mathbf{D}_{\text{live}}; \mathcal{O}_1..\mathcal{O}_5(\theta))\|_2^2 + \lambda \|\alpha\|_1$ (`0s` retraining when harvesting new `.cvfx` priors).
  2. **Ephemeral Real-Time Blind Room & Proximity Dual-Kalman Tracker:** Zero cross-day room carryover; adapts bedroom $\leftrightarrow$ living room in `1.5–3s`.
  3. **Universal Only-Where-Invariant AI Rule:** Neural nets (`.onnx` separation/dereverb & `< 25 KB` zero-GC Micro-GRU/SSM phoneme sensor) stay strictly **plugin-agnostic & room-agnostic**.
