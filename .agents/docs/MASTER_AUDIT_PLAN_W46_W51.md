# Master Audit & Execution Plan (`W46`–`W51`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).
> ORIGIN|100% lossless reconstruction of full user prompt `[U]` + validated Samsung S25 Ultra (`SM-S938B`, Snapdragon 8 Elite, Adreno 830, Android 17 / API 37) `hot_exec` benchmarks recovered after AI Studio turn cancellation/rollback (`v2.9.57`, Build `#57`, commit `6be72d8c` / `d2bbff9c`).
> ANTI_CANCEL_PROTOCOL|At wave start run `python3 -B .agents/scripts/flight_recorder.py init --session <id> --wave <W> --goal "..."`; at EVERY major milestone (diagnosis closed, benchmark run, edit batch complete, before CI/push) run `python3 -B .agents/scripts/flight_recorder.py save --auto-diff --phase <p> --summary "..." --finding "..." --done "..." --pending "..." --next "..."`. After commit/push run `flight_recorder.py close --commit <sha>`.
> CADENCE|User is less quota-constrained -> grouped logical waves (`W46`..`W51`), free to run live S25U `hot_exec` / `cavernize-cmd` / `shell` checks via OmniAgent (`termux` + `android` channels). Always pass baton cleanly w/ continuation prompt when chat gets long.

---

## 0. Validated S25U Hardware Benchmarks & Tooling Invariants (DO NOT Redo From Scratch)
- **Device Baseline:** `samsung SM-S938B` (Galaxy S25U, Android 17 / SDK 37, ORT `1.32.0`, `WebGPU (Vulkan GPU) + CPU KleidiAI`), `Focusrite Scarlett 2i2 (4th Gen)` (`VID=0x1235 PID=0x8219`, `UAC2=True`, `Duplex=True`, `ClockID=41`, `VendorIf=3`, `Scarlett=Gen4`), `Audio-Technica ATH-M40x` (`target=NativeAutoEqProfile`, `hrirMode=DiffuseFieldCompensatedHrir`, `preamp=-5.88 dB`).
- **W46 Live S25U `hot_exec` Benchmark #1 (`uvr_mdxnet_kara_2.onnx`, 52.79 MB, `[-1,4,2048,256]`, WebGPU Vulkan):**
  - `batch=1` FP32: `1223.21 ms/chunk` (`[1225.47, 1219.76, 1222.59, 1224.43]`, warmup `1829.46 ms`).
  - `batch=2` FP32: **`2626.1 ms/chunk`** (`pairTimes=[4273.14, 6231.16]`, **`speedupFactor=0.466`**, >2x slower). **DECISION: REJECT `batch=2`.**
- **W46 Live S25U `hot_exec` Benchmark #2 (Pure-C# Lossless IEEE-754 `FP32 -> FP16` ONNX Protobuf Converter, NO INT8):**
  - Conversion: `52,786,726 B -> 26,404,456 B` (`0.50x`), `220` initializers + `2` IOs converted, `0` clamped weights, `406.9 ms` one-time on-device conversion.
  - WebGPU FP32 baseline: `init=299.5 ms`, `warmup=1770.5 ms`, `avg=1239.58 ms/chunk`.
  - WebGPU FP16 result: `init=126.0 ms` (`2.38x` faster load), `warmup=1220.3 ms`, **`avg=887.97 ms/chunk`** (`[912.92, 893.11, 875.46, 870.38]`, **`speedupFactor=1.396x`**), **`nanInfCount=0`**, **`SNR=37.09 dB`**, **`cosineSimilarity=0.9999069`**. **DECISION: APPROVE pure-C# IEEE-754 FP16 converter + disk cache (STRICTLY NO INT8).**
- **Hot-Probe Reflection Invariant:** `omni_agent.py hot-exec` defaults to `include_android=False` + `--via auto` (routes via online `android` IPC channel). Access `CavernizeAndroid` & `Android.App.Application.Context` via reflection (compiling w/ `include_android=True` causes `FileNotFoundException: CavernizeAndroid.TypeCheck`).

---

## 1. Wave Breakdown (100% Coverage of User Request `[U]`)

### `W46` — Lossless Neural Speedup, GPU UI Breathing & Automatic Real-Time Streaming
- **Scope:**
  1. **Async 3-Stage CPU<->GPU Producer-Consumer Pipeline (0 dB Bit-Exact):** Decouple `PrepareChunkInput` (STFT/pack) + `RunSingleInput` (WebGPU/CPU inference) + `FinalizeChunkOutput` (iSTFT/WOLA) in `CavernSamples/Reusable/Cavernize.Logic/Spatial/INeuralInferenceRuntime.cs` && `Cavern/Neural/MvsepAntiArtifactPipeline.cs` (strict C# 8.0 in `Cavern/`). Include `.NET` unit harness proving **`0 dB` bit-exact diff** vs sequential baseline.
  2. **Productionize Validated Pure-C# IEEE-754 `FP32 -> FP16` Converter & Disk Cache (NO INT8):** Cache converted `.fp16.onnx` alongside source mtime/size hash in `CacheDir/ort_fp16/` inside `NativeOnnxRuntimeBridge.cs` (for WebGPU-optimal static FP32 models like `uvr_mdxnet_kara_2.onnx`, strictly preserving `0` NaN/Inf & skipping already-FP16 models like `scnet_4stem_fp16.onnx`). Evaluate & ship any extra zero-loss AI engineering speedups (buffer reuse, reduced tensor copies, pre-allocated WOLA scratch).
  3. **GPU UI Breathing Window:** Prevent WebGPU inference from saturating 100% GPU and freezing Android UI/compositor — add adaptive minimum inter-chunk GPU breathing yield (e.g., short yield/sleep calibrated by chunk wall time when foreground UI is active) so app & OS stay fluid during separation.
  4. **Automatic Real-Time Neural Streaming:** Dynamically measure real per-chunk inference throughput vs audio chunk duration (`realTimeFactor = chunkInferenceSec / effectiveChunkAudioSec`) and, when sequential playback from `0:00 -> end` (no seek) can sustain real-time margin (e.g., with safe lookahead buffer), automatically stream & apply AI output live during playback instead of blocking until 100% completion (applicable to any AI mode where feasible: Karaoke, AI Upmix, Stems).
- **Primary Targets:** `Cavern/Neural/MvsepAntiArtifactPipeline.cs`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/{INeuralInferenceRuntime,NeuralPipelineOrchestrator,NeuralAudioModels}.cs`, `CavernSamples/CavernizeAndroid/Services/NativeOnnxRuntimeBridge.cs`, `CavernSamples/Reusable/Cavernize.Logic/Rendering/RealtimePlayerEngine.cs`.

### `W47` — Karaoke Consistency Fix & Permanent Audio Ground Truth Verification Protocol
- **Scope:**
  1. **Fix Vocal-Removal Inconsistency Across All Track/Environment Combinations:** Audit & fix divergent Karaoke vocal-removal quality across:
     - Pure stereo `FLAC` (Center Instant vs AI Stems)
     - Pure `Dolby Atmos` (E-AC-3 JOC / TrueHD Atmos)
     - `Dolby Atmos` enhanced w/ `FLAC` (`Lossless Twin` — specifically audit vocal mask extraction from FLAC applied onto Atmos channels/objects: sample-rate alignment, latency/offset alignment, gain/phase scaling, multichannel channel-map projection)
  2. **Permanent Audio Ground Truth Verification & Correction Protocol:**
     - Create `.agents/docs/AUDIO_GROUND_TRUTH_PROTOCOL.md` + reusable `.NET` test harness (usable on demand whenever audio DSP/neural code changes, NOT forced on unrelated edits).
     - Use real/dataset-derived (or curated multi-stem reference clips w/ known isolated vocals + drums + bass + other + full mix, short representative segments) + intelligent **pairwise / orthogonal combination matrix** (avoiding exponential brute-force explosion) across formats (`FLAC`, `Atmos`, `Lossless Twin`) x Karaoke modes x Upmix/Spatial flags.
     - Automatically report quantitative metrics (Vocal Attenuation dB, Instrument Preservation Delta dB, LFE Integrity, Phase/Time Alignment Drift, NaN/Clipping) & flag exact failing combinations for rapid root-cause fix.
- **Primary Targets:** `Cavern/Neural/SpatialKaraokeEngine.cs`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/{UniversalLosslessTwinProvider,Adapters/NeuralStemStageProcessor,NeuralPipelineOrchestrator}.cs`, `CavernSamples/Reusable/Cavernize.Logic/Rendering/RealtimePlayerEngine.cs`, `.agents/docs/AUDIO_GROUND_TRUTH_PROTOCOL.md`.

### `W48` — Decoupled AI Architecture, Dynamic Atmos Object Preservation, LFE Preservation & Studio/Export UX
- **Scope:**
  1. **Decouple AI Spatial Upmix / Atmos Enhancement from Karaoke:** Separate AI separation/upmix orchestration from the Karaoke tab so users can trigger stereo->Atmos AI upmix or Atmos AI enhancement independently without entering Karaoke.
  2. **Direct Play-Tab AI Toggle:** Add a clear, intuitive ON/OFF control directly on the **Play** tab to enable/disable active AI processing (3D/upmix/stems) at any time without hunting through sub-tabs.
  3. **Preserve Dynamic Moving Dolby Atmos Objects Under AI Enhancement/Upmix:** Fix bug where running AI enhancement/upmix on a Dolby Atmos track collapses/groups dynamic floating objects into fewer static bed channels. Preserve original dynamic object trajectories & channel/object count! (Note: `FLAC -> Atmos` staying static is expected; dynamic FLAC->Atmos objects remains deferred in `SAVED_IDEAS.md`).
  4. **Fix Missing LFE (Sub-Bass) Channel in AI Upmix:** Diagnose & fix bug where enabling AI upmix removes the discrete LFE channel and mixes sub-bass into other channels; ensure LFE stays discrete and level-accurate.
  5. **Studio Recording & Dynamic Composable Export Presets:**
     - Add an explicit, unmistakable **Record** button/state workflow for Karaoke performance prior to exporting (clear separation: Record -> Preview -> Export).
     - Replace rigid mutually-exclusive export presets (forcing Performance OR Upmix) w/ **dynamic composable export builder** (modular toggles allowing combinations like `Performance + AI Upmix`).
     - Replace overly complex technical preset/option names w/ simple commercial/intuitive names AND add concise explanations under each option (`AndroidStrings.cs` 100% EN/PT-BR parity).
- **Primary Targets:** `CavernSamples/Reusable/Cavernize.Logic/{Rendering/RealtimePlayerEngine,Models/CavernizeTrack,Spatial/{NeuralPipelineOrchestrator,Adapters/NeuralStemStageProcessor},StudioBatchExporter}.cs`, `CavernSamples/CavernizeAndroid/{MainActivity.PlayerTab,MainActivity.PlayerMusicTab,MainActivity.ConverterTab,Language/AndroidStrings}.cs`.

### `W49` — Lyrics Sync Drift Fix, Syllable-by-Syllable Sync & Risk-Scored Hierarchy
- **Scope:**
  1. **Fix Synced Lyrics Drift:** Diagnose why downloaded synced lyrics start synced on initial open, then drift out of sync (requiring manual rewind/scrubbing to resync) — audit playback clock source, seek/buffer latency offset, audio-engine sample-rate/resampler clock vs `LyricsService` / UI timer polling.
  2. **Syllable-by-Syllable Synced Lyrics (Apple Music Style):** Support word/syllable-timed lyric formats (TTML / Enhanced LRC `<mm:ss.xx>` / compatible public syllable-timed lyric providers such as NetEase/KuGou/Musixmatch-compatible/LRCLIB endpoints where publicly accessible) + smooth progressive syllable highlight UI in lyrics view.
  3. **Match-Risk Scoring Analyzer & Strict Priority Hierarchy:** Implement probabilistic match-risk scoring (evaluating title/artist normalization, duration delta, remix/live/acoustic/instrumental mismatch risk) so low-risk non-exact matches can outrank un-timed exact matches according to strict hierarchy:
     1. Syllable-by-syllable synced lyrics
     2. Line-by-line synced lyrics
     3. Unsynced plain lyrics
- **Primary Targets:** `CavernSamples/CavernizeAndroid/Services/LyricsService.cs`, `CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs`, `CavernSamples/CavernizeAndroid/Views/*Lyrics*` / `MediaStageViews.cs`, `CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs`.

### `W50` — Focusrite Scarlett 2i2 Detach Crash, Low-Latency Mic, Mic Source Choice & Hardware Controls UI
- **Scope:**
  1. **Fix Focusrite Scarlett 2i2 Unplug Crash:** Inspect live S25U crash/logcat + `UsbProAudioDeviceManager.cs` & `Native/cavernize_proaudio/cavernize_path_b_libusb.cpp` to eliminate native/managed crash & race condition when unplugging Scarlett 2i2 (4th Gen, `VID=0x1235 PID=0x8219`) mid-session.
  2. **Eliminate ~0.5s–1.0s Microphone Latency (Both Scarlett & Phone Internal Mic):** Audit capture ring-buffer sizing, AAudio/AudioRecord buffering, resample/STFT vocal-chain lookahead, & duplex sync causing 500–1000 ms monitoring delay on BOTH Scarlett mic and internal phone mic; drive down to true low-latency monitoring.
  3. **User-Selectable Mic Source While Scarlett Is Connected:** Do NOT auto-hijack mic input to Scarlett when user plugs Scarlett in (user often uses Scarlett DAC output w/ phone's built-in mic). Add explicit persisted/live Mic Input selector (`Phone Mic` vs `USB Interface Mic`) + smart default/fallback.
  4. **Scarlett Hardware Controls (Air, Drive, etc.) in Vocal Chain & One UI 9 UI:**
     - Integrate active Scarlett Gen4 hardware DSP states (`Air`, `Presence/Drive`, `AutoGain/Safe`, etc.) into Vocal Chain logic so software effects don't double-process what Scarlett hardware already performs.
     - Expose intuitive One UI 9 controls for those already-implemented backend Scarlett vendor controls directly in the app UI (`AndroidStrings.cs` EN/PT-BR parity).
- **Primary Targets:** `CavernSamples/CavernizeAndroid/Services/UsbProAudioDeviceManager.cs`, `CavernSamples/CavernizeAndroid/Native/cavernize_proaudio/cavernize_path_b_libusb.cpp`, `CavernSamples/CavernizeAndroid/Audio/AndroidAudioOutput.cs`, `CavernSamples/CavernizeAndroid/MainActivity.{PlayerTab,SettingsTab,cs}`, `Cavern/VocalChain/*`, `CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs`.

### `W51` — AutoEq Bass Over-Cut & Simplification, 3D Room Simulator / X-Ray (5 Bugs) & Library Tab Freeze
- **Scope:**
  1. **AutoEq Headphone Calibration Over-Cut Bass & UI Simplification:**
     - Diagnose why almost all calibration modes strip excessive bass (feeling like the bass channel was removed — verify against user's real log: `ATH-M40x`, `target=NativeAutoEqProfile`, `hrirMode=DiffuseFieldCompensatedHrir`, `preamp=-5.88 dB`, `AutoEqPostProcessingStage.cs` + `AutoEqTargetCurves.cs` + double-compensation w/ `sadie_d1_dfc` HRIR!).
     - Simplify & clarify the post-headphone calibration mode choices (clear plain-language labels + short descriptions; remove confusing jargon overload).
  2. **3D Room Simulator / Graph / X-Ray — Fix All 5 Distinct Bugs:**
     - **Bug 51.2a (Missing/2D Upmix in 3D Graph):** Fix active Upmix (both AI and non-AI modes) sometimes failing to show in 3D Graph tab or rendering only 2D channels.
     - **Bug 51.2b (Solo Button Stops Working):** Fix Solo toggling becoming completely ineffective in certain neural/FLAC/Atmos/upmix states (click/unclick produces zero audio change).
     - **Bug 51.2c (Inverted/Garbled Movement Axes When Solo Breaks):** Fix coordinate/axis inversion & corruption when dragging objects / moving listener in that broken state.
     - **Bug 51.2d (Reset Custom Object Position Fails):** Fix `Reset` failing to restore manually dragged objects back to their original position (`Source.Interface.cs` / `TrackSourceItem.cs` / `RealtimePlayerEngine.cs` / `RoomSimulatorView.cs`).
     - **Bug 51.2e (3D Spherical Energy Map / X-Ray Wildly Spinning Blue Indicator):** Fix `SpatialXRayPowermap.cs` / `RoomSimulatorView.cs` blue forward-of-head indicator spinning/jittering uncontrollably.
  3. **Library Tab 1–2s UI Freeze:** Profile & eliminate the 1–2 second main-thread UI freeze when switching from the first tab (**Albums**) to any other Library category (move heavy grouping/sorting/adapter/bitmap work off UI thread or pre-index/diff asynchronously).
- **Primary Targets:** `Cavern/Filters/AutoEqTargetCurves.cs`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/Adapters/AutoEqPostProcessingStage.cs`, `Cavern/Source.Interface.cs`, `Cavern/Spatial/SpatialXRayPowermap.cs`, `CavernSamples/Reusable/Cavernize.Logic/{Models/{CavernizeTrack,TrackSourceItem},Rendering/RealtimePlayerEngine}.cs`, `CavernSamples/CavernizeAndroid/{Views/RoomSimulatorView,MainActivity.PlayerTab,MainActivity.PlayerMusicTab,MainActivity.SettingsTab,Language/AndroidStrings}.cs`.
