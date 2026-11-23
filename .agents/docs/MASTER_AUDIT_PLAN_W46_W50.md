# CavernizeAndroid — Master Engineering Audit & Wave Plan (`W46`–`W50`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`). Persistent master roadmap covering ALL 15+ real-world user-reported bugs, architectural upgrades, && verification protocols so zero items are lost across sessions.

---

## Wave W46 — Neural Engine Async 3-Stage Pipelining, GPU Headroom & Auto-RTF Progressive Streaming
- **Status:** `IN_PROGRESS` (executing now)
- **Objectives:**
  1. **Async 3-Stage CPU↔GPU Producer-Consumer Pipelining (0.0 dB Bit-Exact):**
     - `Cavern/Neural/MvsepAntiArtifactPipeline.cs` (strict C# 8.0) + `CavernSamples/Reusable/Cavernize.Logic/Spatial/INeuralInferenceRuntime.cs`:
     - Add explicit 3-stage split (`Stage 1 CPU Prepare/STFT(c+1)` || `Stage 2 GPU/ONNX Infer(c)` || `Stage 3 CPU Post/iSTFT+WOLA(c-1)`) via `NeuralPipelinedChunkEvaluator` (or `ProcessTrackPipelined<TPrep, TOut>`) while keeping exact IEEE-754 sequential accumulation order into `accumL/accumR/weightSum` (`0.0f` max abs diff proven via `.NET` unit test across Waveform, MDX-Net STFT, SCNet STFT, Mel-Band RoFormer).
  2. **Adreno 830 GPU UI-Headroom Breathing Yield:**
     - Add `GpuUiBreathingYieldMs` (`MvsepPipelineOptions` + `NeuralAudioConfig`, default `18 ms` when WebGPU/NNAPI foreground UI is active, `0 ms` in offline/headless/background mode) so WebGPU dispatches never starve Android SurfaceFlinger/HWUI or freeze the phone UI during AI inference.
  3. **Auto-RTF Progressive Real-Time Neural Application:**
     - Add monotonic finalized-sample progressive publishing (`OnProgressiveFinalized` in `MvsepAntiArtifactPipeline` + `NeuralSeparatedTrack.ReadySampleCount` / `IsProgressiveStreaming` / `MeasuredRealTimeFactor` + `NeuralStemStageProcessor` ready-sample guard).
     - After initial 2 chunks, measure real-time factor (`RTF = wallSec / audioSec`). When `RTF < 0.85` && track playing sequentially from start w/o seeking past ready window, bind progressive `NeuralSeparatedTrack` immediately to `RealtimePlayerEngine` so separated vocals/stems take effect live in seconds instead of waiting 100%! If user seeks beyond `ReadySampleCount`, gracefully hold/bypass neural stems until `ReadySampleCount` catches up (zero clicks/garbage).
  4. **Live S25 Ultra Adreno 830 WebGPU Benchmark (`batch=2` & FP16, STRICTLY NO INT8):**
     - Run live benchmark on user's `SM-S938B` (Adreno 830) via OmniAgent comparing `uvr_mdxnet_kara_2.onnx` FP32 `batch=1` vs FP32 `batch=2` vs FP16 `batch=1` vs FP16 `batch=2` (measuring wall latency, RTF, && exact SDR / dB diff against FP32 baseline). Only ship what proves real speedup w/ zero audible loss.

---

## Wave W47 — Automated Multi-Stem Ground-Truth Protocol + Critical Audio, 3D Room, Library & USB Stability Fixes
- **Status:** `IN_PROGRESS` (executing now)
- **Objectives:**
  1. **Automated Multi-Stem Ground-Truth Combination Verification Protocol:**
     - Permanent protocol doc `.agents/docs/AUDIO_GROUND_TRUTH_PROTOCOL.md` + runnable `.NET` verification suite (`CavernizeGroundTruthHarness`): synthesizes realistic multi-stem reference tracks (harmonic formant lead vocal, wide backing harmonies, pitched bass + sub-80Hz kick, stereo instruments, moving 3D Atmos helicopter/synth object) rendered into 4 canonical environments:
       1. `Stereo 44.1 kHz` (FLAC-like)
       2. `Stereo 48 kHz` (FLAC-like)
       3. `Native 7.1.4 Dolby Atmos (Beds + Dynamic Moving 3D Objects)`
       4. `Dolby Atmos + Synced FLAC Lossless Twin (w/ realistic +187 ms / 42 ppm master offset)`
     - Automatically sweeps combinations of Karaoke (Off / Blind / Neural Lead / Neural Lead+PreserveBacking), Upmix (Off / Matrix / Cavernize / Neural Stems3D / Neural Hybrid 7.1.4), Solo/Mute, Custom Drag + Reset, && X-Ray DoA stability, asserting quantitative pass thresholds (Vocal Cut dB, Instrument Preservation dB, LFE energy conservation, Dynamic Object trajectory preservation, Solo isolation >= 40 dB, Reset accuracy == 0.0).
  2. **Karaoke Unreliability Across FLAC / Atmos / Atmos+FLAC Lossless Twin:**
     - Root cause #1 (`MainActivity.cs:2997–3007`): When Atmos has synced FLAC Lossless Twin, neural separation decoded raw *unaligned* FLAC file ignoring `SyncResult.BaseOffsetSamples` & `DriftPpm` (>15 ms mismatch breaks STFT complex coherence `S_XV -> 0` in `SpatialKaraokeEngine`). Fix: extract sample-locked aligned stereo via `UniversalLosslessTwinProvider.TryExtractFullAlignedStereo(atmosLengthSamples, ...)` so Twin stems are bit-locked to sample 0 of Atmos!
     - Root cause #2 (`SpatialKaraokeEngine.cs` & `NeuralStemStageProcessor.cs`): Verify multichannel coherence projection, center/front stereo vocal projection on 5.1/7.1.4 Atmos beds & objects, && sample-rate scaling so Karaoke achieves clean, consistent vocal removal across ALL formats.
  3. **AI Upmix Preserves Native Moving Atmos Objects + Restores Dedicated `.1` LFE Subwoofer Channel:**
     - Root cause (`RealtimePlayerEngine.cs:1259–1525`): `ShouldRenderLiveNeuralSourcesLocked()` replaced native Dolby Atmos moving objects w/ static stereo neural stems, discarding OAMD trajectories & collapsing channels; `AttachLiveNeuralSourcesLocked()` omitted `ReferenceChannel.ScreenLFE` completely (`HybridNeuralDirAc` = 11 ch, `Stems3DObjects` = `isLfe: false`).
     - Fix: Never replace native multichannel/object tracks (`currentTrack.Renderer.HasObjects || currentTrack.ChannelCount > 2`) with static stereo neural stems; extract a proper 120 Hz LR4 Linkwitz-Riley dedicated `ScreenLFE` (`Source.LFE = true`, `isLfe: true`) channel on stereo AI Upmix (`HybridNeuralDirAc` -> true 12 ch 7.1.4, `Stems3DObjects` -> includes dedicated `.1` LFE + high-passed bass/mains with flat energy conservation).
  4. **3D Spherical Energy Map ("Mapa 3D Quente") Smooth DoA & Peak Energy:**
     - Root cause (`Cavern/Spatial/SpatialXRayPowermap.cs:269–298`): `dominantDirection` & `peakCellEnergy` overwritten raw at 187.5 Hz w/ zero temporal smoothing -> blue DoA indicator spins/jitters violently on stereo transients.
     - Fix: Exponential directional smoothing (`tau ~ 180 ms`, energy-gated deadband) + smooth attack/release envelope on `peakCellEnergy` (strict C# 8.0).
  5. **3D Room Suite Fixes (Solo, Reset Object Position, Movement Axes, Upmix 3D Graph Visibility):**
     - **Solo broken under Upmix/Neural:** Fix `RealtimePlayerEngine.cs:1749–1831` fallback branch (previously ignored `anySolo`, `IsSolo`, `IsMuted`, `HasCustomPosition`) AND expose virtual Matrix/Cavernize upmix channels in `TrackSourceItem` + `ActiveSourceLayers` so Upmix immediately appears in 3D Room graph (never stuck looking 2D!) AND every channel/object responds to Solo/Mute!
     - **Reset Dragged Object Position fails:** Store `DefaultNormalizedPosition` on `TrackSourceItem` & `Source` (`UnlockCustomPosition(restoreDefault)`), restore it on `ClearSourceCustomPosition` & `ResetAllCustomPositions`, AND wire `resetRoomBtn` in `MainActivity.PlayerTab.cs` to call `playerEngine.ResetAllCustomPositions()` too + add explicit per-object reset affordance!
     - **3D Movement Axes Intuitive Alignment:** Audit & fix `RoomSimulatorView.cs` `UpdateListenerFromRoomTouch` / `UpdateDraggedSourceFromRoomTouch` / `UnprojectHorizontalPlane` / `HeadDock` so dragging forward/back/left/right matches screen-space camera perspective naturally without inverted signs.
  6. **Library Tab 1–2s UI Freeze on Switching From Albums:**
     - Root cause (`MainActivity.PlayerMusicTab.cs:3863–3952`): Switching to Tracks/Artists/Folders synchronously ran `PickPreferredVariant` across all tracks + `Distinct().Count()` across artists + inflated 72 complex card hierarchies (700+ Views) + fired 72 simultaneous `BindThumbAsync` calls in ONE UI frame!
     - Fix: Precompute `PreferredVariant` & `AlbumCount` on background thread inside `BuildLibraryBaseIndexSnapshot`, plus incremental chunked UI rendering (first 14 visible rows in `< 12 ms`, remaining rows appended smoothly in 12-item frame slices with generation cancellation token).
  7. **Scarlett USB Disconnect App Crash Fix:**
     - Root cause (`UsbProAudioDeviceManager.cs` + `cavernize_path_b_libusb.cpp` + `AndroidAudioOutput.cs` + `cavernize_path_a_aaudio.cpp`): On `ACTION_USB_DEVICE_DETACHED`, `UsbProAudioDeviceManager` synchronously closed `UsbDeviceConnection` FD while native `libusb` Path B `event_thread` was actively polling that FD -> Android `fdsan` `SIGABRT` / `libusb` `SIGSEGV`! Plus race between `libusb_free_transfer` & in-flight URBs on `ENODEV`.
     - Fix: Synchronous `BeforeUsbConnectionClosed` / `ForceStopPathBImmediate()` callback stopping Path B *before* closing `UsbDeviceConnection`; `dup()`/safe detach flag in `cavernize_path_b_libusb.cpp` skipping blocking `libusb_set_interface_alt_setting` / `libusb_release_interface` when `device_detached` (`ENODEV`), and safe exception-guarded detach recovery in `AndroidAudioOutput.cs` + `cavernize_path_a_aaudio.cpp`.

---

## Wave W48 — AutoEq Natural Bass & Plain Targets + Decoupled AI/Karaoke UX + Mic Source, Low Latency & Scarlett Hardware UI
- **Status:** `QUEUED`
- **Objectives:**
  1. **Headphone Calibration (AutoEq) Bass Fix & Simplified Commercial Options:**
     - Change default target from `DiffuseFieldNeutral` (`-7.86 dB` bass shelf cut!) to `NativeAutoEqProfile` (natural Harman bass preserved, `0 dB` cut), simplify target options with clear commercial labels + bass impact summary (`Natural / Harman Bass (Recommended)`, `Balanced Studio (-2 dB)`, `Flat Studio Reference (-5.5 dB)`, etc.) so calibration enhances clarity w/o stripping bass.
  2. **Decouple AI Separation / Upmix From Karaoke + Play Tab 1-Tap AI Bypass Toggle:**
     - Provide standalone AI Analysis / 3D Upmix trigger independent of Karaoke tab (so users wanting 3D Stereo->Atmos upmix never have to open Karaoke).
     - Add clear 1-tap `AI Active / Bypass` toggle chip directly on the Play tab whenever AI stems/upmix are active.
  3. **Live Mic Source Selector (Phone Mic While Scarlett Connected) + Fix 500–1000ms Mic Latency + Scarlett Hardware Controls in UI & Vocal Chain:**
     - Add explicit Mic Input Source selector (`Auto` | `Phone Mic` | `USB Interface (Scarlett)`), enabling phone mic capture even while Scarlett handles playback (using AAudio independent input device ID or hybrid capture).
     - Diagnose & fix 500–1000 ms monitoring latency (check `LiveMicrophoneBridge` ring prefill/backlog, `AndroidAudioOutput` burst sizing, `VocalChain` lookahead/FFT hop, `AAudio` input drain & `VirtualizerFilter` buffering) down to target `< 25–45 ms` round-trip.
     - Surface real Scarlett 2i2 Gen 3/4 hardware controls (`Air`, `Drive`, `Preamp Gain`, `Clip Safe`, `Inst/Line`, `48V Phantom`) cleanly in Live Mic / Vocal Studio UI and feed active hardware state (`Air` presence, `Drive` saturation, `Gain` trim) into the Vocal Chain calibration so hardware + DSP complement each other cleanly.

---

## Wave W49 — Lyrics Sync Stability, Risk-Scored Matching & Syllable-by-Syllable + Karaoke Recording & Composable Studio Presets
- **Status:** `QUEUED`
- **Objectives:**
  1. **Lyrics Sync Drift Fix + Risk-Scored Matcher + 3-Tier Syllable Hierarchy:**
     - Fix synced lyrics drift/desync requiring repeated rewind (verify timestamp clock source vs `RealtimePlayerEngine.CurrentPosition`, seek reset, binary search vs linear cursor stuck on out-of-order/duplicate timestamps, and LRC offset persistence).
     - Implement risk-scored candidate evaluation (title/artist/duration similarity + mismatch risk score): prefer synced over exact-unsynced when mismatch risk is low; enforce strict hierarchy **(1) Syllable-by-syllable (`ELRC` `<mm:ss.xx>` / `TTML` / `YRC` / `KRC` from LRCLIB / NetEase / QQ / Musixmatch) -> (2) Line-by-line (`LRC`) -> (3) Plain unsynced text**, with smooth word/syllable sweep highlight in Lyrics view.
  2. **Explicit Karaoke Performance Recording + Dynamic Composable Studio Export Presets:**
     - Add clear `Record Performance` / `Stop` button directly in Karaoke / Sing Dock (with live timer & level meter, saving take cleanly for preview/export instead of only showing "Export Karaoke Performance").
     - Refactor Studio Export Presets from mutually-exclusive list into **dynamic composable feature toggles** (e.g., combine `Karaoke Vocal Performance` + `3D Spatial Upmix` + `Lossless FLAC/Atmos Container`) with plain commercial names & concise inline explanations.

---

## Wave W50 — Live S25 Ultra End-to-End Hardware Verification, Final Telemetry & Polish
- **Status:** `QUEUED`
- **Objectives:**
  1. Run live device verification on S25 Ultra via OmniAgent + Cavernize Live Bridge (`SelfDiagnosticEngine` Ultra 12-layer audit, live Scarlett attach/detach test, live mic RTT measurement, live Karaoke FLAC/Atmos/Twin test, UI jank/perf audit).
  2. Incorporate any final device-observed tuning, ship final CI-verified signed APK, and archive completed master plan.
