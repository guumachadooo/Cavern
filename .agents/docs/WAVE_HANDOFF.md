# Spatial Roadmap Wave Handoff (Compact)
- **Last Completed:** `W06` (`[x]`) — MagLS Ambisonics (`O1..O5`), Woodworth-Schlosberg Biometric Head-Width ITD (`13–18 cm`), HpTF `6–10 kHz` Ear-Canal Notch (`Priority=110`), Impulcifer/HeSuVi/SOFA pipeline & 60s SONICOM/HUTUBS Perceptual HRTF Wizard.
- **Next Wave:** `W07` — **Psychoacoustics & Studio Loudness Standards (Slot 6B)**:
  - Real-time ISO 226:2023 Dynamic Equal-Loudness physiological compensation (`0.00 dB` unity at ref SPL, zero-GC IIR shelving/peaking cascade).
  - ITU-R BS.1770-5 / EBU R128 loudness meter (K-weighting + Momentary/Short-Term/Integrated LUFS + LRA) & Dolby Dialnorm respect.
  - Spatial Dialog Enhancement (isolating Center / speech-band objects without EQ smearing, ready for `W09` lossless-twin assistance).
  - Transparent 4x-oversampled `0 dBTP` True-Peak Limiter; expose reusable LUFS, crest-factor & True-Peak primitives for `W09` LGSR & `W11` Vocal Mix-Match.
- **Audit First (before coding):** `Cavern/Filters/{Normalizer,Limiter,ParametricEqualizer}.cs`, `Cavernize.Logic/Spatial/Adapters/{NormalizerPostProcessingAdapter,AutoEqPostProcessingStage,HptfNotchPostProcessingStage}.cs`, `Cavernize.Logic/Rendering/RealtimePlayerEngine.cs`, `CavernizeAndroid/Services/AppSettingsStore.cs`, `MainActivity.SettingsTab.cs`, `AndroidStrings.cs`.
- **Invariants:** `Cavern/` & `Cavern.Format/` strict C# 8.0; `targetSdkVersion="28"` immutable; zero fake DSP (exact ISO 226:2023 + ITU-R BS.1770-5 / `ebur128` tables); native One UI 9 only; 100% EN/PT-BR parity; `EXTRA_REQUESTS.md` pending queue empty.
- **User Command:** When user says `continue`/`continuar`, execute `W07` immediately per `.agents/docs/SPATIAL_ROADMAP.md`.
