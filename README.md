# Cavernize Android

Spatial audio player, multi-format converter, and 3D object stem extractor for Android built on top of the [Cavern](https://github.com/VoidXH/Cavern) acoustic engine and ported via `.NET 11` (`net11.0-android`).

[![Build Android APK](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml/badge.svg)](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml)
[![Latest Release](https://img.shields.io/github/v/release/guumachadooo/Cavern)](https://github.com/guumachadooo/Cavern/releases/tag/android-latest)

---

## Key Capabilities

### 1. Spatial Multimedia Player & In-App Library (`Play`)
- Reads embedded tags (`Title`, `Artist`, `Album`, sample rate, bitrate) and album cover art directly via Android `MediaMetadataRetriever` (or generates procedural spatial art when absent), harmonizes the frosted SF Pro UI backdrop & One UI 9 wave seekbar, and optionally auto-fetches missing HD album art and synced `.lrc` lyrics online.
- **In-App Music & Media Library (`Tocando Agora | Biblioteca`):** Built-in Samsung Music + Poweramp-inspired browser scanning Android `MediaStore` plus local `Music`, `Download`, `Movies`, and `Audiobooks` folders across **Albums** (2-column grid + tracklist drilldown), **Tracks** (with format badges), **Artists**, and **Folders** — supporting automatic continuous queue playback alongside quick access to the system document picker.
- **Apple Music Authentic Typography & Smooth Lyrics (`sf_pro_android_ui.ttf`):** Faithfully renders Apple's SF Pro Android UI bold typography (`'wght' 700`), `1:1` spring-scaled artwork (tapping the cover opens Live Lyrics), left-aligned title/artist, flanking inline spatial toggles (**`Cavernize 3D | HRTF 3D | Matrix 7.1`**), wave progress bar, queue/transport controls, slim volume bar, and smooth staggered + depth-of-field blur transitions across vocal and `•••` instrumental lyrics rows.
- **Online Cover Art, Lyrics & Subtitles Scrapers (`Poweramp` + `Kodi` Style):** Automatic background enrichment and interactive online search for high-res album covers (iTunes HD, TheAudioDB, MusicBrainz/CoverArtArchive, TMDb), synced `.lrc` lyrics (LRCLIB), and downloadable `.srt` video subtitles (Podnapisi XML/ZIP).
- **Samsung Video Player + YouTube Video Mode (with Subtitles & Auto-Rotate Fullscreen):** Automatically switches video files into a rich video player with `CC` subtitles (`.srt`, `.vtt`, `.ass`/`.ssa` external files + online Podnapisi `.srt` + FFmpeg-extracted MKV/MP4 streams), audio track switcher, aspect ratio cycling (`Fit` / `Fill` / `Stretch`), center `-10s` / `+10s` & double-tap gestures, touch lock, and one-tap portrait/landscape fullscreen with automatic orientation restore on exit.
- Publishes `MediaSession` metadata, album art, and transport actions to the Android system notification shade and lock screen.

### 2. Interactive 3D Acoustic Room (`Sala 3D` / `3D Room`)
- **Volumetric 3D Room & Articulated Head Gimbal (`RoomSimulatorView`):** Architectural 3D acoustic room with translucent shaded walls, sweet-spot floor wave rings, live RMS acoustic rays, monitor bed nodes with dB level rings (Green / Amber / Red, Mute `X`, Solo gold ring), and numbered object spheres with vertical floor drop-lines.
- **Direct Touch Head Position, Yaw & Tilt (`[Sala]/[Cabeça]`):** Toggle directly on the 3D stage between orbiting the room and manipulating the listener, drag the glowing **Aim Orb** in front of the 3D head to steer **Yaw & Pitch** freely in 3D space, slide the built-in **Left `ALT Y` Rail** and **Right `INCL` Rail** directly on the canvas, or fine-tune with slim `33dp` Samsung relative-drag double-tap-reset capsule sliders.
- **Live Source Mixer & Symmetric Transport:** Tap any speaker/object sphere inside the room to Solo, control playback via the symmetric 5-icon transport bar, toggle binaural HRTF virtualizer + distance cues, configure attenuation rolloff (`Disabled`, `Logarithmic`, `Linear`, `Real 1/d`), and boost LFE.

### 3. Spatial Converter & Render Studio (`Estúdio` / `Studio`)
- Target any speaker setup from Stereo/Binaural (`Virtualizer`) up to `5.1`, `7.1`, `7.1.4`, `9.1.6`, and matrix-wired layouts with interactive wiring diagrams.
- Optionally bake the 3D Room listener position, head rotation, and active Solo/Mute overrides directly into exported files.
- Built-in batch rendering queue supporting multi-file selection via SAF (`Intent.ActionOpenDocument`).

### 4. Channel & 3D Object Stem Extractor (`Stems`)
- Isolate bed channels and dynamic 3D objects from immersive formats (`E-AC-3 JOC`, `ADM BWF`, `DAMF`, `LAF`, `WAV`, `CAF`) into individual mono WAV stems (`16-bit`, `24-bit`, `32-bit float`), per-item 3D renders, combined 3D object renders, or raw bitstream dumps.
- Optional `.json` frame-by-frame `(X, Y, Z)` 3D trajectory export for dynamic objects.

### 5. Calibration, Upmixing & In-App Updates (`Ajustes` / `Settings`)
- **Upmixing:** Expand stereo/5.1 tracks to 7.1 (`Matrix Upmixing`) or synthesize overhead 3D objects (`Cavernize 3D`) with customizable intensity and smoothing.
- **Calibration:** Load custom headphone HRIR impulse `.wav` profiles or QuickEQ / Equalizer APO `.txt` calibration filters.
- **Built-in Binary Updaters & OTA Releases:** Automatic ARM64 static `FFmpeg` (`FilesDir/bin/ffmpeg`) and `truehdd` (`FilesDir/bin/truehdd`) installers/updaters, plus in-app OTA APK updater checking `android-latest` while preserving all versioned `vX.Y.Z` GitHub Releases.

---

## Supported Media & Codecs
- **Containers:** `.eac3`, `.ec3`, `.ac3`, `.thd`, `.mlp`, `.mkv`, `.mka`, `.mp4`, `.m4a`, `.m4v`, `.mov`, `.webm`, `.weba`, `.wav`, `.caf`, `.laf`, `.atmos` (plus Android `MediaCodec` decoding fallback for `.mp3`, `.flac`, `.aac`, `.ogg`, etc., and smart FFmpeg container probe/demux fallback).
- **Dolby TrueHD / Dolby Atmos TrueHD:** Decoded cleanly on-device via the built-in static ARM64 `truehdd` (`aarch64-unknown-linux-musl`) installer/updater stored in `FilesDir/bin/truehdd`, enabling real-time 3D Atmos playback, Studio export, and object stem/trajectory extraction (`.json`) alongside automatic temporary cache cleanup.

---

## Building

### Requirements
- `.NET 11` SDK (`net11.0-android` workload) and Android SDK (`minSdk 26`).

### Local CLI Build
```bash
dotnet publish CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj \
  -c Release -f net11.0-android -p:AndroidPackageFormat=apk
```

Shared portable business logic (`Cavernize.Logic`) can also be built standalone without the Android workload:
```bash
dotnet build CavernSamples/Reusable/Cavernize.Logic/Cavernize.Logic.csproj -c Release
```

### CI / Signed Release Workflow
Pushing to `master` (or manually triggering `.github/workflows/BuildAndroid.yml`) compiles a signed APK in GitHub Actions, publishes it as a build artifact, and updates the `android-latest` release tag.

---

## Architecture Overview
- [`Cavern/`](./Cavern) & [`Cavern.Format/`](./Cavern.Format) — Core Cavern spatial DSP engine, object renderers, and immersive bitstream parsers (upstream [`VoidXH/Cavern`](https://github.com/VoidXH/Cavern)).
- [`CavernSamples/Reusable/Cavernize.Logic/`](./CavernSamples/Reusable/Cavernize.Logic) — Shared player engine (`RealtimePlayerEngine`), conversion environment, post-render reports, and queue logic.
- [`CavernSamples/CavernizeAndroid/`](./CavernSamples/CavernizeAndroid) — Android UI (`OneUiTheme`, `RoomSimulatorView`, `OneUiWaveMediaSeekBar`), `AudioTrack` output, `MediaMetadataRetriever` metadata/artwork extractor, `MediaSession` lock-screen service, FFmpeg installer, and GitHub Releases updater.

---

## Credits & Licence
- Built upon the open-source [**Cavern**](https://github.com/VoidXH/Cavern) spatial audio framework by **VoidX (`VoidXH`)**.
- Original Cavern documentation: [cavern.sbence.hu](http://cavern.sbence.hu/cavern/doc.php) | [HRTF repository](https://github.com/VoidXH/HRTF).
- Licensed under the terms of [`LICENSE.md`](./LICENSE.md).
