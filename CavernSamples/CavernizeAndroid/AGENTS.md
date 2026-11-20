# Development Guidelines — CavernizeAndroid

These rules supplement the root `AGENTS.md` whenever working inside `CavernSamples/CavernizeAndroid/`.

## 1. Codebase Map (`CavernSamples/CavernizeAndroid/`)
- **UI Partials (5 Tabs):**
  - `MainActivity.cs` (lifecycle, bottom navigation, frosted layout shell, video/orientation helpers, shared modal/UI helpers)
  - `MainActivity.PlayerMusicTab.cs` (`Play` tab: Apple Music-inspired Now Playing + Samsung Video Player HUD + integrated Media Library/Albums/Smart Explorer)
  - `MainActivity.PlayerTab.cs` (`3D Room` tab: workspace source bar, spatial controls, 3D room, channel mixer)
  - `MainActivity.ConverterTab.cs` (`Studio` tab: target layouts, batch render queue, container/codec export)
  - `MainActivity.ExtractorTab.cs` (`Stems` tab: channel/object/stem extraction and `.json` metadata)
  - `MainActivity.SettingsTab.cs` (`Settings` tab: audio preferences, FFmpeg/`truehdd` installers, diagnostics, OTA updates)
- **Theme & Custom Views (`Canvas`):**
  - `Theme/OneUiTheme.cs` (dynamic Monet/neutral dark tokens, frosted glass, `OneUiBackdropView`, `OneUiSeekBar`, `OneUiSwitch`)
  - `Views/OneUiIconView.cs` (`OneUiVectorIcons`, `OneUiIconBadge`, crisp vector controls)
  - `Views/OneUiComponents.cs` (`OneUiInlineSelector`, `OneUiModalDialog`, `AttachRecoil`, pill controls)
  - `Views/OneUiFileExplorerDialog.cs` (smart directory/folder explorer with empty-branch pruning and SAF fallback)
  - `Views/OneUiWaveMediaSeekBar.cs`, `Views/MediaStageViews.cs`, `Views/RoomSimulatorView.cs`, `Views/LayoutDiagramView.cs`
- **Audio & Services:**
  - `Audio/AndroidAudioOutput.cs`, `Audio/AndroidMediaFallbackDecoder.cs`
  - `Services/AppSettingsStore.cs`, `Services/AppLogger.cs`, `Services/FFmpegInstallerService.cs`, `Services/TruehddInstallerService.cs`, `Services/TrueHDDecoderService.cs`, `Services/AppUpdateService.cs`, `Services/AndroidFileHelper.cs`, `Services/ApkFileProvider.cs`
  - `Services/MediaLibraryService.cs`, `Services/AudioTagReader.cs`, `Services/MediaMetadataHelper.cs`, `Services/OnlineMetadataService.cs`, `Services/LyricsService.cs`, `Services/SubtitleService.cs`, `Services/MediaPlaybackNotificationManager.cs`
- **Localization:**
  - `Language/AndroidStrings.cs` (`pt-BR`, `en-US`, `hu-HU`, `zh-CN`). Never leave visible hardcoded English strings when `pt-BR` is active; keep every supported locale consistent when modifying UI text.

## 2. Samsung One UI 9 Standards & Safety Guards
- **Faithful One UI 9 UI:** Avoid stock legacy Android dialogs/popups (`AlertDialog`, native `Spinner` popup) — use `OneUiModalDialog`, `OneUiInlineSelector`, pill buttons with `AttachRecoil` physics, custom vector icons (`OneUiIconView` — zero loose emojis in UI), and One UI 9 controls.
- **`.NET Android` Compatibility (`net11.0-android`, `SupportedOSPlatformVersion` 26.0):** No external AndroidX packages are used; rely strictly on native `Android.*` APIs with proper API-level guards (`Build.VERSION.SdkInt` / `OperatingSystem.IsAndroidVersionAtLeast(...)`), and keep `android:targetSdkVersion="28"` untouched in `Properties/AndroidManifest.xml`.
- **Mandatory Button Anti-Clipping:**
  - Every horizontal `LinearLayout` (`Orientation.Horizontal`) containing buttons or multi-element pills **must** set `BaselineAligned = false` and `SetGravity(GravityFlags.CenterVertical)`. Without this, any two-line wrap causes Android to align on line 1's baseline and clip the bottom half.
  - Action buttons inside horizontal rows (`PrimaryButton`, `SecondaryButton`, `PillButton`, etc.) must use `SetSingleLine(true)` and `Ellipsize = TextUtils.TruncateAt.End`, or sit inside a `HorizontalScrollView` when displaying multiple chips.
- **Touch Disambiguation Inside `ScrollView`:** `RoomSimulatorView`, `OneUiSeekBar`, and `OneUiWaveMediaSeekBar` guard against accidental vertical scroll interception (`Parent?.RequestDisallowInterceptTouchEvent(...)` + `touchSlop`). Preserve that logic whenever modifying them.
- **Diagnostics:** Log file, FFmpeg, `truehdd`, DAMF, and media failures cleanly with `AppLogger` (written to `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`) for direct device debugging.

## 3. Contextual References
- Read `.agents/docs/PROJECT_HISTORY.md` for reverse-chronological evolution and technical pitfalls (start from the top for the latest milestones).
- Read `.agents/docs/ONEUI9_REFERENCE.md` before tuning native One UI 9 / Apple Music / Samsung Media specs extracted from device APKs.
- Read `.agents/docs/SAVED_IDEAS.md` for shelved visual effects/code that the user may ask to bring back, and update root `README.md` when shipping new user-facing features.
- Read `.agents/docs/EXTRA_REQUESTS.md` before committing, pushing, or compiling the APK.
- Critical reminder: if decompiling or inspecting a requested APK/system behavior fails or is blocked, **warn the user before guessing or approximating**.
