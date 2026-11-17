# Cavernize Android

Spatial audio player, multi-format converter, and 3D object stem extractor for Android built on top of the [Cavern](https://github.com/VoidXH/Cavern) acoustic engine and ported via `.NET 11` (`net11.0-android`).

[![Build Android APK](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml/badge.svg)](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml)
[![Latest Release](https://img.shields.io/github/v/release/guumachadooo/Cavern)](https://github.com/guumachadooo/Cavern/releases/tag/android-latest)

---

## Key Capabilities

### 1. Spatial Music Player (`Player`)
- Reads embedded tags (`Title`, `Artist`, `Album`, sample rate, bitrate) and album cover art directly via Android `MediaMetadataRetriever` (generating clean spatial artwork when absent) and harmonizes the player palette and wave progress bar to the track cover.
- Publishes `MediaSession` metadata, album art, and transport actions (`-10s`, `Play/Pause`, `+10s`, `Stop`, timeline scrubbing) to the Android system notification shade and lock screen.
- Includes live codec/channel telemetry (`kHz`, bed channels, 3D objects) and instant DSP switches (`HRTF 3D`, `Cavernize 3D`, `Matrix 7.1`).

### 2. Interactive 3D Acoustic Room (`Sala 3D` / `3D Room`)
- **Orbitable 3D Camera:** Drag anywhere in the room canvas to orbit azimuth and elevation between `3D`, `Topo`, and `Frente` angles, with animated camera presets and 60 FPS smoothed dynamic object motion trails.
- **Listener Placement & Head Tracking:** Drag the listener across the floor (or vertical Y plane in low-angle front view), rotate head yaw/pitch via the dedicated bottom dock gimbal, or fine-tune every axis numerically with capsule sliders.
- **Live Source Mixer & Binaural Rendering:** Tap any speaker/object sphere inside the room to Solo, enable binaural HRTF virtualizer + distance cues, configure attenuation rolloff (`Disabled`, `Logarithmic`, `Linear`, `Real 1/d`), and boost LFE.

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
- **Built-in FFmpeg Downloader:** One-tap automatic installation and updates for static ARM64 FFmpeg (used for `E-AC-3`, `AC-3`, `Opus`, `FLAC`, and `MKV` muxing).
- **APK Updater:** Checks GitHub Releases (`android-latest`) directly inside Settings with in-app APK download and install.

---

## Supported Media & Codecs
- **Containers:** `.eac3`, `.ec3`, `.ac3`, `.mkv`, `.mka`, `.mp4`, `.m4a`, `.m4v`, `.mov`, `.webm`, `.weba`, `.wav`, `.caf`, `.laf`, `.atmos` (plus Android `MediaCodec` decoding fallback for common `.mp3`, `.flac`, `.aac`, `.ogg`, etc.).
- **Note on Dolby TrueHD (`.thd` / MLP):** Real-time/direct TrueHD MLP decoding requires `truehdd`; `E-AC-3 JOC`, `ADM BWF`, `DAMF`, `LAF`, and standard PCM/MediaCodec containers decode natively on-device.

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
