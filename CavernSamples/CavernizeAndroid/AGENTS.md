# Development Guidelines — CavernizeAndroid

Supplements root `AGENTS.md` inside `CavernSamples/CavernizeAndroid/`.

## 1. Codebase Map (`CavernSamples/CavernizeAndroid/`)
- **UI Partials (5 Tabs):**
  - `MainActivity.cs` (lifecycle, floating bottom nav, backdrop, video/orientation helpers, shared modal/UI helpers, async neural/twin/DNA lifecycle)
  - `MainActivity.PlayerMusicTab.cs` (`Play` tab: Apple Music Now Playing + Lyrics Sing dock + Samsung Video HUD + integrated Library/Albums/Smart Explorer)
  - `MainActivity.PlayerTab.cs` (`3D Room` tab: workspace source bar, spatial/loudness/X-Ray inspector, 3D room, channel/stem/mic mixer)
  - `MainActivity.ConverterTab.cs` (`Studio` tab: target layouts, W12 Studio Master presets & batch export, batch render queue, container/codec export)
  - `MainActivity.ExtractorTab.cs` (`Stems` tab: Modes 1-6 incl. Neural AI Restorer & LGSR EM-MWF Hybrid Stems, `.json` metadata)
  - `MainActivity.SettingsTab.cs` (`Settings` tab: audio/upmix/binaural/AutoEq/head-tracking/pro-audio/neural/vocal modals, installers, diagnostics, OTA)
- **Theme & Custom Views (`Canvas`):**
  - `Theme/OneUiTheme.cs` (`OneUiBackdropView`, `OneUiSeekBar`, `OneUiSwitch`, Monet/neutral dark tokens)
  - `Views/OneUiIconView.cs`, `Views/OneUiComponents.cs` (`OneUiInlineSelector`, `OneUiModalDialog`, `AttachRecoil`), `Views/OneUiFileExplorerDialog.cs`, `Views/OneUiWaveMediaSeekBar.cs`, `Views/MediaStageViews.cs`, `Views/RoomSimulatorView.cs`, `Views/LayoutDiagramView.cs`
- **Audio & Native Pro-Audio:**
  - `Audio/AndroidAudioOutput.cs` (Path B libusb -> Path A AAudio -> AudioTrack), `Audio/AndroidMediaFallbackDecoder.cs`, `Audio/NativeProAudioBridge.cs`
  - `Native/cavernize_proaudio/` (`CMakeLists.txt`, `cavernize_uac2_core.[ch]`, `cavernize_ringbuffer.h`, `cavernize_proaudio_api.h`, `cavernize_path_a_aaudio.cpp`, `cavernize_path_b_libusb.cpp`, `cavernize_proaudio_jni.cpp`, `test_proaudio_native.c`)
- **Services:**
  - *Core & Binary Managers:* `AppSettingsStore.cs`, `AppLogger.cs`, `AppUpdateService.cs`, `AndroidFileHelper.cs`, `ApkFileProvider.cs`, `FFmpegInstallerService.cs`, `TruehddInstallerService.cs`, `TrueHDDecoderService.cs`, `OnnxRuntimeInstallerService.cs`, `NativeOnnxRuntimeBridge.cs`, `NeuralTrackCacheService.cs`
  - *Binaural, Acoustics & Calibration:* `BinauralProfileService.cs`, `SofaHrirReader.cs`, `AutoEqService.cs`, `AudioDeviceProfileManager.cs`, `LosslessTwinDiscoveryService.cs`
  - *Head-Tracking & USB Pro-Audio:* `HeadTrackingManager.cs`, `AndroidImuHeadTrackerProvider.cs`, `GalaxyBudsHeadTrackerProvider.cs`, `BleOscHeadTrackerProvider.cs`, `UsbProAudioDeviceManager.cs`
  - *Media Library & Metadata:* `MediaLibraryService.cs`, `AudioTagReader.cs`, `AudioQualityAnalyzer.cs`, `MediaMetadataHelper.cs`, `OnlineMetadataService.cs`, `LyricsService.cs`, `SubtitleService.cs`, `MediaPlaybackNotificationManager.cs`
- **Localization:** `Language/AndroidStrings.cs` (`pt-BR`, `en-US`, `hu-HU`, `zh-CN`; 100% EN/PT-BR key parity & UI ref coverage enforced by `key_parity_rules`).

## 2. Samsung One UI 9 Standards & Safety Guards
- **Faithful One UI 9 UI:** No stock `AlertDialog` or native `Spinner` popup — use `OneUiModalDialog`, `OneUiInlineSelector`, `OneUiIconView` (zero loose emojis).
- **`.NET Android` (`net11.0-android`, `SupportedOSPlatformVersion` 26.0, `targetSdkVersion="35"`):** Native `Android.*` APIs only (no external AndroidX) with OS guards (`OperatingSystem.IsAndroidVersionAtLeast(...)`). Keep `android:targetSdkVersion >= 35`, W^X `NativeLibraryDir` execution (zero `FilesDir/bin/` exec), 16 KB ELF alignment, Edge-to-Edge `SetOnApplyWindowInsetsListener` (`SystemBars|DisplayCutout|Ime`), and API 33+ dynamic `IOnBackInvokedCallback`.
- **Mandatory Button Anti-Clipping:** Horizontal `LinearLayout` (`Orientation.Horizontal`) containing buttons/pills MUST set `BaselineAligned = false` and `SetGravity(GravityFlags.CenterVertical)`. Action buttons in horizontal rows MUST set `SetSingleLine(true)` and `Ellipsize = TextUtils.TruncateAt.End` (or sit inside `HorizontalScrollView`).
- **Touch Disambiguation:** Preserve `Parent?.RequestDisallowInterceptTouchEvent(...)` + `touchSlop` inside `RoomSimulatorView`, `OneUiSeekBar`, `OneUiWaveMediaSeekBar`, and `TabSwipeHostLayout`.
- **Zero-Warning, Timeless Lessons & Live-API Discipline:** Never trust stale API/version memory as Android SDK / `.NET Android` / CI toolchains evolve -> consult `.agents/docs/BUILD_LESSONS.md`, inspect live CI diagnostics (`python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`), & verify current official Android/.NET docs before introducing/updating versioned APIs. Always use `OperatingSystem.IsAndroidVersionAtLeast(N)` in the exact execution scope (including inside lambdas/callbacks; never `Build.VERSION.SdkInt`) or `[SupportedOSPlatform("androidN.0")]` for `CA1416`; avoid dead `< minSdk` branches, skip APIs obsoleted by newer OS defaults, prefer non-obsolete replacements, and scope unavoidable cross-API `#pragma warning disable CS0618, CA1422` narrowly with rationale. After resolving a novel recurring build/CI error class, run `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` (version-agnostic rules only; audited via `--audit-lessons`).
- **Diagnostics:** Log file, FFmpeg, `truehdd`, ONNX, Pro-Audio, and media failures cleanly via `AppLogger` (`/sdcard/Download/Cavernize/Logs/cavernize_log.txt`).

## 3. Contextual References
- `.agents/docs/BUILD_LESSONS.md` & `.agents/scripts/error_learner.py` (living timeless build/CI error guardrail catalog + auto-learner).
- `.agents/docs/PHASE2_ROADMAP.md` & `.agents/docs/HANDOFF.md` (active waves, Living Slot Topology, `MOD-WIRING-GATE`).
- `.agents/docs/PROJECT_HISTORY.md` (reverse-chronological milestones & pitfalls; pre-Phase 2 in `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`).
- `.agents/docs/ONEUI9_REFERENCE.md` (native One UI 9 / Apple Music / Samsung Media specs).
- `.agents/docs/SAVED_IDEAS.md` (shelved effects), `README.md` (user docs), `.agents/docs/EXTRA_REQUESTS.md` (pre-commit/build gate).
