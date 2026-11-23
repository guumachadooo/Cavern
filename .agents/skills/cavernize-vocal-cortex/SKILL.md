---
name: cavernize-vocal-cortex
description: Principal Vocal Producer, Live FOH Acoustics Engineer & DSP Architect playbook (MOD-AUDIO-ENG-DIRECTOR + MOD-SOTA-PREFLIGHT). Guides live acoustic feedback/bleed shielding (Slot 2D-SHIELD: Multi-Ref FDKF + Two-Path Shadow + Online WPE + residual neural shield), AcousticConstraintGovernor + Joint Multi-Objective Solver, 3-plane S25U execution, Scarlett Gen 4 pre-ADC hardware coupling, artifact-aware isolated vocal DNA forensics, Direct Acoustic Manifold Cortex, and optional mobile/desktop DAW black-box prior harvesting.
---

# Cavernize Omniscient Vocal Cortex & Live Acoustic Shield Skill (`[SEED-v3]`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim `path:line`, `symbol()`, equations/invariants).

## 1. Autonomous SOTA Pre-Flight & Chief Audio Engineer Mandates (`MOD-SOTA-PREFLIGHT` + `MOD-AUDIO-ENG-DIRECTOR`)
- **Zero-Dogma Pre-Flight (`MOD-SOTA-PREFLIGHT` + `MOD-SEED-FALSIFY`):** Treat ALL pre-drafted operators, AI placements, & waves in `.agents/docs/PHASE3_VOCAL_CORTEX_ROADMAP.md` strictly as **`[SEED-v3: Mutable Floor Hypothesis]`**. Before coding ANY vocal/pro-audio wave, execute the 4-step internal SOTA Pre-Flight WITHOUT waiting for user brainstorming:
  1. **Falsify `[SEED-v3]`** via `MOD-TABU-RECON` (`.agents/docs/KNOWN_MECHANISMS_LEDGER.md`, `0%` overlap across orthogonal acoustic/DSP/FOH/speech passes).
  2. **Physics-vs-AI Tournament:** Enforce the **Universal Only-Where-Invariant AI Rule** (neural nets strictly restricted to room/gear/song-invariant residual/perception tasks; linear acoustics & control laws stay analytical/physical so zero retraining is ever needed).
  3. **3-Plane S25U Hardware Split Verification:**
     - **Plane 1 (Offline Track-Prep, WebGPU/CPU):** stem separation, dereverb, `VocalMixDnaProfile v2`, section timeline.
     - **Plane 2 (Hard RT Audio, CPU/ARM NEON, `<1 ms/block`, `<10 ms` acoustic RT, `0 B/frame` GC, `0` locks):** multi-ref FDKF + two-path shadow filter, `AcousticConstraintGovernor`, direct operators $\mathcal{O}_1..\mathcal{O}_5$.
     - **Plane 3 (Parallel Control/NPU, `15–30 ms` async hop, lock-free snapshot bus):** vocal reflex sensor, ephemeral Dual-Kalman room tracker, `Joint Solver`, optional `4–6 ms` singing-trained residual neural shield.
  4. **Offline Golden-Bench Proof Gate:** Prove ERLE, GBF, SNR, phase, & stability in offline Python / deterministic ground-truth vectors (`AUDIO_GROUND_TRUTH_PROTOCOL.md` `GT-08+`) BEFORE porting to production C#/C++.
- **Zero User Burden:** Never ask user to pick DSP topologies or audio engineering safeguards; exercise **Studio Eureka Authority** autonomously and mutate `PHASE3_VOCAL_CORTEX_ROADMAP.md` in-place when stronger methods win.

## 2. Live Acoustic Feedback & Bleed Shield (`Slot 2D-SHIELD` — Priority #1)
- **Multi-Reference Look-Ahead Input:** Feed digital `Music_L`, `Music_R` (with look-ahead from `RealtimePlayerEngine`), AND `PostFx_Voice_Out` (closed-loop reference) into the shield before DAC output.
- **4-Stage Hybrid Shield (Auto-Bypassed on Headphones):**
  1. **Multi-Ref Partitioned FDKF + Two-Path Shadow Filter:** Background filter adapts aggressively; foreground commits only on strict error improvement -> immune to vocal double-talk divergence.
  2. **Psychoacoustically Masked Room Probe (Optional):** Sub-masking excitation stabilizing RIR identification during sparse backing passages.
  3. **Causal Online WPE:** Removes diffuse late room tail before nonlinear vocal compression/saturation.
  4. **Causal `4–6 ms` Singing-Trained Residual Neural Shield:** Post-FDKF nonlinear speaker/bleed cleanup preserving singing vibrato, falsetto, & belting transients.
- **Scarlett Gen 4 Pre-ADC Coupling (`HardwarePreAmpStateSnapshot`):** Consume lock-free `Air`/`Drive`/`AutoGain`/`ClipSafe`/preamp-gain telemetry from `Scarlett2Protocol` / `UsbProAudioDeviceManager`; trigger instant FDKF covariance reset on preamp step changes & subtract hardware Air/Drive from digital operators.

## 3. `AcousticConstraintGovernor` & Omniscient Joint Multi-Objective Solver (Priority #2)
- **Anti-Suicidal Loop Law:** Never allow independent Auto-EQ or compressor makeup gain to push closed-loop $|H_{\text{room}}(\omega)\cdot G_{\text{total}}(\omega)| \to 1$ in speaker mode.
- **Constrained Joint Optimization (Plane 3 -> Plane 2):** Jointly optimize song Mix-Match DNA + live Auto-Mix vocal pocket + parameter smoothness subject to `AcousticConstraintGovernor` frequency-dependent gain ceilings (auto-relaxed to full studio headroom when `HeadphoneMode == true`) && Moore-Glasberg cochlear smoothness bounds ($\|\dot{g}\|, \|\ddot{g}\| \le \kappa_{\text{cochlea}}$).

## 4. Artifact-Aware Vocal DNA (`Slot 2D-DNA`) & Direct Manifold Cortex (`Slot 2D-CORTEX`)
- **Cross-Stem Anti-Bleed Gate:** Sample separated vocal frames ONLY where $\text{SIR}(t,f) = 10\log_{10}(|V(t,f)|^2 / |I(t,f)|^2) > +12\text{ dB}$ && `VoicedProb > 0.85`; sample reverb decay ONLY on percussive-free tails.
- **5 Unified Continuous Operators on Lock-Free `OmniscientVocalStateBus`:**
  1. **$\mathcal{O}_{F_0,\text{Tract}}(t,f)$:** `~4.5 ms` causal WSOLA pitch & true-envelope formant sculpting w/ `15–25 ms` consonant bypass, vibrato preservation, & sub-sample backing sync.
  2. **$\mathcal{O}_{\text{Spec}}(t,f)$:** Single zero-latency phase-coherent surface $G(t,f)$ uniting $F_0$-normalized cross-gender timbre match, pitch-shielded narrow-$Q$ suppression (protecting $k F_0$), sibilance, Lombard/proximity tracking, & `Governor` loop-gain ceiling.
  3. **$\mathcal{O}_{\text{Dyn}}(t)$:** Cochlear-constrained envelope sculptor matching syllable attack/body & crest under Moore-Glasberg smoothness & `Governor` makeup-gain caps.
  4. **$\mathcal{O}_{\text{Harm}}(t, k F_0)$:** `Cheby-ADAA2` harmonic-locked saturation + `10–18 kHz` air + `15 Hz` DC block, silenced on sibilants/breath, coupled to $\Delta\text{GR}_{\text{dB}}$, & subtracting Scarlett hardware Air/Drive.
  5. **$\mathcal{O}_{\text{Space}}(t,f,\mathbf{x})$:** Section-morphed MicroShift, syllable-ducked `DrumStem`-BPM Throw Delay, & 16-line 6-octave Velvet-FDN subtracting live ephemeral Dual-Kalman `DRR/RT60(f)` (zero cross-day room carryover) + **Mid-Only `1–3.5 kHz` Dynamic Instrumental Vocal Pocket**.

## 5. Optional Post-Cortex Extension: Black-Box DAW Prior Harvester (`Slot 2D-CAPTURE` + `Slot 2D-FORGE`)
- Deferred to `W43–W44` after live Shield + Cortex ship: single-pass `UnifiedSuperStimulus.wav` (`0.001`-sample Barker/MLS sync + pass-to-pass determinism gate + multi-level Farina sweeps + prime multitone IMD + stepped bursts + high-$Q$ sweeps + $F_0$ glides + M/S probe) harvesting `.cvfx` psychoacoustic sweet-spot priors ($\mathcal{P}_{\text{psycho}}$) gated by 7-dimension `MOD-RESIDUAL-GATE` (`Pre-Emphasis ESR <= -30 dB`, `0.000°` parallel phase, `<= 0.15 LUFS`, `44.1..192 kHz` invariance) w/ `0s` retraining.
