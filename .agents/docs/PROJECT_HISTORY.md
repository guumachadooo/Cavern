# Project History & Engineering Pitfalls (`PROJECT_HISTORY.md`)

> **Chronological Archive Only:** Live workspace state, branch status, `csproj`/`CI` versions, and pending extra requests are injected dynamically at startup by `SessionStart` (`.agents/hooks/session_start.py`). **Never write `"Current State"` or `"Current Version"` headers in this static file.**
>
> **How to Update:** Use this log to understand *why* code is written the way it is and avoid regressions — do **not** treat older layout preferences as permanent rules when the user asks to evolve the UI. Add new milestones by **prepending** at the top of Section 3 (newest first), and record reusable bug fixes under Section 2.

---

## 1. Environment & Build Pipeline
- **Primary Test Device:** Samsung Galaxy S25 Ultra running Android 17 / One UI 9 (device `SystemUI.apk` and Samsung APKs were inspected read-only to verify native slider physics and layout dimensions).
- **Agent Workspace:** Ubuntu PRoot container inside Termux on the user's device (`/root/.gemini/antigravity/scratch/Cavern`), with direct APK delivery to `/sdcard/Download/Cavernize-Android.apk` after CI builds.
- **Compilation Split:** Local Termux ARM64 PRoot validates `Cavernize.Logic`, `Cavern.Format`, and `Cavern` with `/root/.dotnet/dotnet` (`.NET 8`) + GC heap caps, and type-checks all `CavernSamples/CavernizeAndroid/*.cs` files locally in seconds via `python3 .agents/scripts/check_android_cs.py` (cached `Microsoft.Android.Ref.34` + Roslyn `4.12.0` shadow build in `/tmp`). Signed `.apk` packaging runs on `master` push via `.github/workflows/BuildAndroid.yml`.

## 2. Technical Lessons & Known Pitfalls
- **`MatroskaTree.GetChild(reader, tag, index)`:** Always advance `nextTag = reader.Position` inside the child read loop and guard `TryCreate` against `reader.Position >= endPosition`; otherwise trailing MKV tags after `Cluster`s (such as `Cues`/`Tags`) trigger an infinite loop at `100%` during extraction.
- **`.NET Android` (`net11.0-android`) Binding Gotchas:**
  - `View` exposes `ScaleX`/`ScaleY` as C# properties (not `SetScaleX`/`SetScaleY` methods).
  - Inside `TextView`/`Switch`/`Button` subclasses, **`TextView.Paint` shadows `Android.Graphics.Paint`** (qualify `Android.Graphics.Paint.Style.*`).
  - `OnMeasure` overrides must be `public override void OnMeasure`.
  - `MediaMetadataRetriever` may require casting numeric keys such as `(MetadataKey)38` (`METADATA_KEY_SAMPLERATE`) depending on binding level.
  - `AppLogger.Error` accepts `(string context, Exception ex)` or `(string message)` (2 or 1 arguments).
  - `ReadableMetadataHeader` requires `IReadOnlyList<ReadableMetadataField>`.
  - Files importing `using Android.OS;` must qualify `System.OperationCanceledException`.
- **`Cavern.Format` Target (`netstandard2.1`):** Targets C# 8.0 syntax — do **not** use C# 9+ features such as `is not`.
- **`truehdd` Release Assets & Extraction:** Linux musl release assets include both `.tar.gz` and `.tar.gz.sha256`; filter strictly for the `.tar.gz` suffix (excluding `.sha256`) and unpack via `GZipStream` + `TarFile`. Extracting tracks via `ExtractTrackFromContainer` requires `source.Track.Source.Seek(0)` before reading.
- **OTA Version Sync:** `BuildAndroid.yml` overrides `-p:ApplicationDisplayVersion=X.Y.${{ github.run_number }}` — always keep the major.minor prefix synchronized with `<ApplicationDisplayVersion>` in `CavernizeAndroid.csproj` so `AppUpdateService.IsRemoteNewer` remains strictly monotonic.
- **Device Debugging & SELinux:** Read runtime logs from `/sdcard/Download/Cavernize/Logs/cavernize_log.txt` and inspect system APKs strictly read-only; never attempt silent APK installs or permission/SELinux bypasses.

## 3. Version History (Newest First)
<!-- MAINTENANCE RULE: Always PREPEND new version entries immediately below this comment (newest first). Consolidate older entries near the bottom if needed. Never label any phase as "Current State". -->

### Agent Infrastructure — Plugin v3 (`lean` Mode, Token Diet) & Local `CavernizeAndroid` Roslyn Type-Check
- **Local `CavernizeAndroid` C# Type-Check (`.agents/scripts/check_android_cs.py`):** Caches `Microsoft.Android.Ref.34` (`34.0.154`) and `Microsoft.Net.Compilers.Toolset 4.12.0` (C# 13 Roslyn on `.NET 8`) under `~/.cache/cavern-android-ref/` and compiles all 32 `CavernSamples/CavernizeAndroid/**/*.cs` files via `/tmp/cavern-android-typecheck/` (`0 Error(s)`), wired into `.agents/planner_profile.json` `build_rules` while keeping GitHub Actions for final APK signing/packaging.
- **Global Plugin v3 & Language Policy:** Upgraded `.agents/planner_profile.json` to `schema_version: 3` (`planner_mode: "lean"`, `read_budget`, `secret_guard`, `blast_radius`) and updated `AGENTS.md` Rule 1 to reply dynamically in the user's language (preserving `AndroidStrings.cs` localization coverage).

### Agent Infrastructure — Universal Global Plugin (v2 Schema, Auto-Bootstrap & Regression Gate)
- **Full Universalization (`~/.gemini/config/plugins/dynamic-fractal-planner/`):** Consolidated all generic tools into the global Antigravity plugin: 4-tier adaptive planning (`complexity_triage.py`, `plan_tool.py`), zero-AI chained schema migration (`schema_migrations.py`, `v1 -> v2 -> ... -> vN`), automatic `SessionStart` project memory/profile bootstrap (`PROJECT_HISTORY.md`, `SAVED_IDEAS.md`, `EXTRA_REQUESTS.md`, `planner_profile.json`), `project-memory` skill + anti-rot reminders, `/btw` `EXTRA_REQUESTS.md` gating, declarative `file_guards`/`command_guards`/`diff_rules`/`key_parity_rules`/`build_rules`, `verify_universal.py`, and mandatory plugin self-protection regression suite (`tests/run_plugin_tests.py`).
- **Zero-Script Cavern Extension (`.agents/planner_profile.json` `schema_version: 2`):** Migrated all Cavern-specific rules (`targetSdkVersion="28"`, Termux `.NET`, C# 8 checks, `AndroidStrings.cs` parity, `OneUi` anti-clipping, version sync, APK/log watches) into declarative JSON in `.agents/planner_profile.json` and removed local `.agents/hooks/` and `.agents/scripts/`.

### v2.8.28 — One-Screen Play UX, Unified Visual Identity, Smart Explorer, FLAC<->Atmos Merge, External 3D Head & DAMF RIFF-WAVE Fix
- **Play Tab & Redundancy Cleanup:** Responsively compacted `Now Playing` to fit cleanly on a single screen without vertical scrolling; fixed transport order to `[|<] [-10s] [Play/Pause] [+10s] [>|]`; modernized `OneUiIconView` with rounded caps and `"10"` inside skip arcs; unified frosted surfaces between `Play` and `3D Room`; removed the redundant global top bar (scoped `WorkspaceSourceBar` only to `3D Room`, `Studio`, and `Stems`); eliminated duplicate controls (`lyricsOnlineBtn`, video `CC` now invokes `ShowOnlineSubtitlesSearchDialog`, Bento `3D/Ceiling`, separate `Clear Solo/Mute`, `OnlineAutoBestSwitch` in `Settings`).
- **Modals, External 3D Head & DAMF:** `OneUiModalDialog` upgraded with readable semi-transparent `SurfaceModal`/`SurfaceModalRow` backdrop, `0.68f` dim, and API 31+ `DecorView` blur; `RoomSimulatorView` uses a dedicated external dock (`3D Head` free trackball on the left + One UI 9 vertical `Height Y` capsule on the right); fixed `DolbyAtmosMasterFormatReader`/`MetadataFile` for streaming heavy `.metadata` files and decoding **RIFF WAVE** `.atmos.audio`, plus explicit errors for silent videos and unpaired `.atmos` files.
- **Smart Library & Tags:** Added `OneUiFileExplorerDialog` (prunes folders with zero media, recursive counts, include/exclude directory filters, SAF fallback); added `AudioTagReader` (MP4 `trkn`/`disk`/`©alb`/`©ART` for `.m4a`/`.mp4`, FLAC Vorbis, ID3v2) sorting albums by disc/track number; **Smart Cleanup** rule-based spatial filter (preserving spatial audio); filter-accurate album track counts; unified album variant merging (`ATMOS + FLAC`) with seamless live variant switching in `Play` preserving exact timestamp; and online cover/lyrics search preserving `(Deluxe)`/`Editions` suffixes.

### v2.8.26 — Android 17 NDK FFmpeg Stability, Smooth Animations, Idle 3D Objects & Library/Lyrics Polish
- Replaced the desktop Linux FFmpeg binary (which crashed with `exit=159` / `SIGSYS` under seccomp) with native Android NDK builds (`husen-hn/ffmpeg-android-binary` v2.0.0 using `LD_LIBRARY_PATH` in `ffmpeg_lib/` and startup self-repair); added `SafePipeHandle` EOF barriers in `FFmpeg.cs`/`TrueHDDecoderService.cs` and eliminated concurrent `.Recycle()`/`.Dispose()` races causing `ObjectDisposedException` in `RoundedArtworkView` and LRU thumbnails.
- Restored fluid animations (collision-free unified scroll and crisp text in `AppleMusicLyricsView`, `renderedRatio` interpolation in `OneUiWaveMediaSeekBar`, `40dp` opacity-jump-free `OneUiSeekBar`, smooth motion trails, and automatic hiding of idle silent 3D objects in `RoomSimulatorView`).
- Added disk-cached incremental library sync (`library_cache_v2.json`), `Spatial / Surround` stereo filter, per-file cover isolation (avoiding `Download`/`Music` collisions), configurable multi-source online auto-ranking (`OpenSubtitles` REST + `Netease` + `Lyrics.ovh` + `LRCLIB` + `iTunes HD`), explicit speech-end `• • •` interludes (`ELRC`/`TTML`/`SRT`/blank LRC lines), and Next/Previous track controls in the player and system notification.

### v2.8.x — Apple SF Pro Typography, Integrated Media Library & Online Metadata Scrapers
- Bundled `Assets/fonts/sf_pro_android_ui.ttf` extracted from Apple Music 7.0.0 (`'wght' 700`), staggered line transitions with pre-scaled `0.5x` `BlurMaskFilter` depth blur (`cubic-bezier(0.16, 1, 0.3, 1)`, scale `0.945 -> 1.01`), unified SF Pro typography, and subtle One UI glass translucency.
- Added `Now Playing | Library` switcher (`MediaLibraryService` scanning `MediaStore` + local directories with continuous queue auto-advance on `OnPlayerCompleted`), artwork tap to toggle lyrics, inline spatial mode pills (`Cavernize 3D | HRTF 3D | Matrix 7.1`), `OnlineMetadataService` (iTunes HD, TheAudioDB, MusicBrainz/CoverArtArchive, TMDb, LRCLIB `.lrc`, Podnapisi `.srt`), automatic portrait video fullscreen returning to original orientation, and `[Room]/[Head]` interactive orbit + Aim Orb rails + `33dp` slim sliders in `RoomSimulatorView`.

### v2.7.23 — Exact One UI 9 Sliders/Switches, Apple Music & Samsung Video Parity, Atmos-Style 3D Room
- Implemented relative drag physics in `OneUiSeekBar` matching `SystemUI.apk` `ToggleSeekBar` (no thumb jump on initial touch) and `SamsungGallery2018.apk` `SeslSwitch` (`32dp × 20dp` capsule with `20dp` thumb ring and `#FCFCFF` core).
- Aligned `Play` tab to Apple Music's `music_player.xml` + borderless lyrics with real `• • •` interlude list rows, and full Samsung Video Player HUD (`.srt`/`.vtt`/`.ass` internal/external subtitles, audio track switch, `Fit/Fill/Stretch` aspect, touch lock, `±10s`, landscape fullscreen).
- Upgraded `RoomSimulatorView` to a cleaner Dolby Atmos Renderer aesthetic: removed cluttered internal dock, drew studio monitor speakers with dB level rings (Green/Amber/Red), numbered spheres with vertical floor stems, and toggleable object labels.

### v2.6.x — Verified SystemUI Colors, Gallery Floating Bottom Bar & Preserved GitHub Releases
- Verified exact token mapping in `framework-res.apk` + `SystemUI.apk`: `quickpanel_progress` (`UID="4-8-5"`) uses `system_accent2_300` (not `system_accent3_300`); `OneUiSeekBar` stays 100% solid without thumb when idle (`!isTouchDown`), revealing the `0.50f` gradient fade + thumb during drag, and concave wall deformation was archived to `SAVED_IDEAS.md`.
- Adopted floating `56dp` capsule bottom navigation (`#3A3A3D` 0.90 alpha + `0x1AFFFFFF` pill + line/solid icons from `SamsungGallery2018.apk`), wallpaper-harmonized badges, pinch zoom/buttons in `3D Room`, and symmetric transport controls; restored all historical GitHub Releases and prevented workflow overwrites of older versioned tags.

### v2.5.x — Full Dolby TrueHD / Atmos TrueHD Support (`truehdd`)
- Added official static `truehdd` (`aarch64-unknown-linux-musl` / `x86_64`, Apache-2.0) via `TruehddInstallerService.cs`, `TrueHDDecoderService.cs`, and shared `Truehdd.cs` running inside `FilesDir/bin/truehdd` (under the same `untrusted_app_27` SELinux domain as FFmpeg).
- Integrated TrueHD/Atmos decoding across all 5 tabs (live playback, batch conversion/Queue, Stem/Object/`.json` extraction, MLP presentation switching) with instant cancellation and auto-cleanup (`KeepTempFiles` option).
- Fixed `ChannelPrototype.FromStandardName`, `DolbyAtmosMasterFormatRootFile`/`MetadataFile`, `MatroskaTree.GetChild` `100%` infinite loop (`v2.5.16`), and `MatroskaReader.Seek(-1)`.

### v2.4.x — Dedicated Casual Music Player Tab, Embedded Art & Orbitable 3D Room
- Introduced casual `Player` tab (`MainActivity.PlayerMusicTab.cs` + `MediaMetadataHelper.cs`) extracting embedded/folder/procedural album art and `MediaMetadataRetriever` tags alongside the technical `3D Room` tab (completing the 5-tab navigation architecture).
- Upgraded `RoomSimulatorView.cs` into an interactive 60fps orbitable 3D spatial visualizer.

### v2.3.x — Early S25 Ultra UX Polish & Ergonomics
1. **Neutral Dark Surfaces vs. Wallpaper Accents:** Prevented muddy brown/orange tints on warm wallpapers by keeping cards/backgrounds in One UI's neutral dark slate while focusing wallpaper colors on accents (switches, active pills, sliders).
2. **Compact Expandable File Header:** Replaced the tall 4-line always-on info card with a slim top bar expanding technical details and multi-track selector on demand (later further scoped per tab in `v2.8.27`).
3. **Anti-Clipping Fix on Horizontal Button Rows:** Fixed clipped bottom halves of two-line pills (like batch add) by setting `BaselineAligned = false` and `SetSingleLine(true)`.
4. **Decoupled 3D Gimbal & Vertical Elevation:** Moved the 3D head gimbal out of the corner so it stopped occluding `FR`/`TFR` speakers and added vertical `Y` height visualization/control.
5. **System Media Notification:** Integrated `MediaSession` + `Notification.MediaStyle` (`POST_NOTIFICATIONS` + `FOREGROUND_SERVICE`) for lock-screen and shade transport controls (`Play/Pause`, `-10s`, `+10s`, `Stop`, seekbar).
6. **Smooth Touch-Thumb `OneUiSeekBar`:** Switched discrete small-range snapping to pixel-continuous `float` dragging with `SystemUI`-inspired thumb reveal.
7. **Contextual `?` Help & Tab Organization:** Added inline `?` help modals, moved batch `Queue` into `Studio`, and ordered `Settings` from frequent audio options down to one-time binary setup/logs/updates.
8. **Localization Hygiene:** Enforced clean `pt-BR` coverage in `AndroidStrings.cs` so English fragments do not leak into Portuguese UI.

### v2.2.x — One UI 9 Redesign, Dynamic Palette & Diagnostic Logging
- Built `OneUiTheme` (API 31+ Monet dynamic colors), `OneUiBackdropView`, `OneUiIconView`, `OneUiInlineSelector`, `OneUiModalDialog`, `OneUiWaveMediaSeekBar`, `AppUpdateService` (`android-latest` OTA), and shared `AppLogger` (`Downloads/Cavernize/Logs/`).
- Fixed CI version comparison mismatch in `BuildAndroid.yml` and added complete `stdout`/`stderr` capture in `FFmpeg.cs`.

### v2.0 -> v2.1 — Initial Android Port & Shared Logic Split
- Created `CavernizeAndroid` and factored shared logic into `CavernSamples/Reusable/Cavernize.Logic/`.
- Implemented `RealtimePlayerEngine` + `AndroidAudioOutput`, interactive HRTF 3D room, `StemExtractor`, `AndroidMediaFallbackDecoder`, pure C# lossless `FlacAudioConverter`, `MergeToContainer` extension fixes, and 1-click static ARM64 `FFmpegInstallerService` inside `FilesDir/bin/ffmpeg` (`targetSdkVersion="28"`).
