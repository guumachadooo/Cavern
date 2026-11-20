# WAVE_HANDOFF (Compact <=20L — Auto-Updated Every Wave)
ROADMAP: `.agents/docs/SPATIAL_ROADMAP.md`
NEXT_WAVE: `W04` — Zero-GC Low-Latency Audio + USB DAC + 3DoF/6DoF Head Tracking (Slot 4)
PREV_WAVE: `W03` (`[x]` AutoEq Neutral Engine + Auto-Profile per Device (Slot 6A) verified)
TRIGGER: User says `continue` / `continuar` -> execute `W04` immediately.
W04_PROTOCOL:
1. `MOD-AUDIT-EXISTING`: Inspect `RealtimePlayerEngine.cs`, `AndroidAudioOutput.cs`, `ManualHeadTrackerProvider.cs`, `ListenerPose6DoF.cs`, `AudioDeviceProfileManager.cs`, `AppSettingsStore.cs`, and `AndroidManifest.xml` (preserve `targetSdkVersion="28"`).
2. `MOD-CURATE`: Cherry-pick Galaxy Buds RFCOMM spatial quaternion protocol (`timschneeb/GalaxyBudsClient`, `timschneeb/BudsPro-Headtracking`), Madgwick/AHRS fusion & short-horizon angular predictor (`xioTechnologies/Fusion`), Android `GameRotationVector` IMU & `Mr. Headtracker` BLE-OSC, plus `AudioTrackPerformanceMode.LowLatency` + USB DAC `Float32`/native sample-rate routing (`44.1..192 kHz`).
3. `MOD-CONTRACT` + `MOD-IMPL`: Upgrade audio output & `RealtimePlayerEngine` render loop to pre-allocated zero-GC buffers, `LowLatency` `AudioTrack`, USB DAC native SR + `PcmFloat`; add strict C# 8.0 Madgwick/Kalman short-horizon head-pose predictor in `Cavern/Spatial/`; implement Slot 4 `IHeadTrackerProvider` providers (Galaxy Buds RFCOMM, Android IMU `GameRotationVector`, BLE-OSC) + auto-recenter + `SpatialSceneHub` / One UI 9 Settings controls & 100% EN/PT-BR parity.
4. `MOD-VERIFY`: Run Python strict C#8 + Android API/permissions/wiring/L10n/OneUI9/manifest (`targetSdkVersion="28"`) + pure-math quaternion/Madgwick/predictor/zero-GC verification suite.
5. `MOD-HANDOFF`: Update `SPATIAL_ROADMAP.md` (`W04` `[x]` + curation log), overwrite this file for `W05`, prepend concise entry to `PROJECT_HISTORY.md`.
HARD_RULES: Pure C#/.NET Android (strict C#8 in `Cavern`/`Cavern.Format`) | `targetSdkVersion="28"` | Zero fake DSP | Native One UI 9 only | 100% EN/PT-BR `AndroidStrings.cs` | Split dynamically (`W04.a`/`W04.b`) if scope grows.
