# WAVE_HANDOFF (Compact <=20L — Auto-Updated Every Wave)
ROADMAP: `.agents/docs/SPATIAL_ROADMAP.md`
NEXT_WAVE: `W03` — AutoEq Neutral Engine + Auto-Profile per Device (Slot 6A)
PREV_WAVE: `W02` (`[x]` Parametric STFT Core + Atmos-Aware Upmix (Slot 2A) verified)
TRIGGER: User says `continue` / `continuar` -> execute `W03` immediately.
W03_PROTOCOL:
1. `MOD-AUDIT-EXISTING`: Inspect existing QuickEQ / `Equalizer` / biquad filters in `Cavern/` & `Cavern.QuickEQ/`, EQ settings/storage in `Cavernize.Logic/` & `AppSettingsStore`, and any `AudioManager`/`AudioDeviceCallback` usage in `CavernizeAndroid/`.
2. `MOD-CURATE`: Cherry-pick neutral Diffuse-Field / B&K 5128 JM-1 headphone compensation target math & catalog structure from `jaakkopasanen/AutoEq`, RBJ/oratory1990/Wavelet biquad parametric EQ (SOS cascade, preamp headroom, Nyquist/Cramped-Q handling), and Cavern QuickEQ integration.
3. `MOD-CONTRACT` + `MOD-IMPL`: Build zero-GC strict C# 8.0 Biquad/SOS Parametric EQ engine + AutoEq model database/lookup (strict neutral DF/5128 targets, automatic zero double-EQ guard when using DFC HRIRs vs raw HRIRs), implement Slot 6A `IPostProcessingStage` in `Cavernize.Logic`, and wire Android `AudioDeviceCallback` / `AudioDeviceInfo` detector to auto-switch AutoEq + Head Tracking + HRIR profile per connected BT/USB/3.5mm device.
4. `MOD-VERIFY`: Run Python strict C#8 + Android API/wiring + DSP frequency-response/preamp/double-EQ/manifest (`targetSdkVersion="28"`) verification suite.
5. `MOD-HANDOFF`: Update `SPATIAL_ROADMAP.md` (`W03` `[x]` + curation log), overwrite this file for `W04`, prepend concise entry to `PROJECT_HISTORY.md`.
HARD_RULES: Pure C#/.NET Android (strict C#8 in `Cavern`/`Cavern.Format`) | `targetSdkVersion="28"` | Zero fake DSP | Native One UI 9 only | 100% EN/PT-BR `AndroidStrings.cs` | Split dynamically (`W03.a`/`W03.b`) if scope grows.
