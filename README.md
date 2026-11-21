# Cavernize Android

Spatial audio player, multi-format converter, and 3D object stem extractor for Android built on top of the [Cavern](https://github.com/VoidXH/Cavern) acoustic engine and ported via `.NET 11` (`net11.0-android`).

[![Build Android APK](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml/badge.svg)](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml)
[![Latest Release](https://img.shields.io/github/v/release/guumachadooo/Cavern)](https://github.com/guumachadooo/Cavern/releases/tag/android-latest)

---

## Key Capabilities

### 1. Spatial Multimedia Player & In-App Library (`Play`)
- Reads embedded tags (`Title`, `Artist`, `Album`, sample rate, bitrate) and album cover art directly via Android `MediaMetadataRetriever` (or generates procedural spatial art when absent), harmonizes the frosted SF Pro UI backdrop & One UI 9 wave seekbar, and optionally auto-fetches missing HD album art and synced `.lrc` lyrics online.
- **In-App Music & Media Library (`Tocando Agora | Biblioteca`):** Built-in Samsung Music + Poweramp-inspired browser with persistent disk cache (`library_cache_v3.json`), direct pure-C# binary MP4 (`trkn`/`disk`/`©alb`/`©ART`), FLAC, and ID3v2 tag reading for accurate multi-disc & track-number sorting (even on `.m4a.mp4` Atmos files skipped by Android `MediaStore`), **Spatial / Surround** filter pills, **Smart Clean** non-music clutter filter (hiding voice notes, call recordings, SFX, and stems while keeping all Spatial audio), **Directory Inclusion/Exclusion Filters** via an **In-App Smart File Explorer** (`OneUiFileExplorerDialog` with dead-end folder pruning & multi-select), and **Multi-Format Track Merging** powered by a **Scientific Multi-Factor Audio Quality Analyzer** (`AudioQualityAnalyzer`: strict `Atmos -> Surround -> Stereo` ranking, psychoacoustic codec curves, and background PCM/FFT inspection detecting fake 24-bit, fake lossless, upsampled Hi-Res, dynamic range, and hard-clipping) with **Smart Album Continuity** and an expandable live **Format Switcher (`[ Active ] +N`)** that hot-swaps variants with zero gap, clickless crossfade, and elapsed-time compensation.
- **Single-Viewport Unified Player (`sf_pro_android_ui.ttf` + One UI 9) + `ATMOS+FLAC` Audio Spec A/B:** Dynamically adapts artwork, lyrics stage, and margins to the full viewport with a retractable bottom navigation pill on `Play`, smooth animated Cover ↔ Lyrics transitions, a live **`ATMOS+FLAC`** Lossless-Twin badge pill, and a dedicated **Audio Specification** modal on badge tap (format, streams, binaural profile, live scientific quality report, Lossless Twin pairing status with **Rescan Twin**, and an **Instant A/B Compare** toggle switching cleanly between `ATMOS+FLAC` restored playback and `Original Atmos`), flanking spatial toggles (**`Cavernize 3D | Binaural Profile / Direct 2.0 | Matrix 7.1`**), 120fps wave progress bar, unified frosted transport (`|◀ | -10s | Play/Pause | +10s | ▶|`), slim volume bar, and smoked-frosted backdrop-blurred modals.
- **Apple Music Sing Vocal Slider & Live Mic / Scarlett Controls in `Lyrics`:** Inside the synced Lyrics view, a frosted Sing dock provides real-time **Vocal Level (`0%–100%`)** control (auto-engaging Spatial Karaoke lead-vocal attenuation while preserving backing vocals), live microphone monitoring status, and one-tap shortcuts to the **Auto-Vocal Mix-Match** rack and **Focusrite Scarlett 2i2** hardware console.
- **Ranked Online Cover Art, Lyrics & Subtitles Scrapers:** Automatic background enrichment and editable manual search supporting Title, Artist, and Album **Edition / Deluxe** preservation (`(Deluxe)`, `(Taylor's Version)`, etc.), high-res album art (`iTunes HD`, `TheAudioDB`, `CoverArtArchive`, `TMDb`), synced & plain lyrics (`LRCLIB Synced`, `Netease Synced`, `LRCLIB Plain`, `Lyrics.ovh`), and `.srt` video subtitles (`OpenSubtitles` REST GZip + `Podnapisi` fallback).
- **Samsung Video Player + YouTube Video Mode (with Subtitles & Auto-Rotate Fullscreen):** Automatically switches video files into a rich video player with `CC` subtitles (`.srt`, `.vtt`, `.ass`/`.ssa` external files + online `OpenSubtitles`/Podnapisi `.srt` + FFmpeg-extracted MKV/MP4 streams), audio track switcher, aspect ratio cycling (`Fit` / `Fill` / `Stretch`), center `-10s` / `+10s` & double-tap gestures, touch lock, and one-tap portrait/landscape fullscreen with automatic orientation restore on exit.
- Publishes `MediaSession` metadata, album art, and lockscreen transport/track-skip actions (`Previous`, `-10s`, `Play/Pause`, `+10s`, `Next`) to the Android system notification shade and lock screen.

### 2. Interactive 3D Acoustic Room & Spatial X-Ray Inspector (`Sala 3D` / `3D Room`)
- **Volumetric 3D Room, Interactive Object/Stem/Mic Dragging & Articulated Head Gimbal (`RoomSimulatorView`):** Architectural 3D acoustic room with translucent shaded walls, sweet-spot floor wave rings, live RMS acoustic rays, monitor bed nodes with dB level rings (Green / Amber / Red, Mute `X`, Solo gold ring), and sculpted 3D dynamic object, neural stem, and live microphone orbs with discreet dashed floor drop-lines, smooth tapered glowing Bézier motion ribbons, ceiling elevation micro-chevrons, long-press/drag 3D pinning (with instant pin reset), and automatic fade-out when stationary & silent (`RMS == 0`).
- **6DoF Physical Propagation Delay & Studio Spatial X-Ray Inspector:** Real-time speed-of-sound (`343 m/s`) fractional time-of-flight delay across bed/object sources (with LFE excluded), an optional **72-cell spherical 3-band Soundfield Powermap** (`All / Low / Mid / High`), live **ITU-R BS.1770-5** loudness & 4× True-Peak limiter telemetry, **LGSR Lossy vs Lossless** coupling/SPX/GCC-PHAT diagnostics, **Live Vocal Chain** metering, and one-tap **LGSR A/B** and **Vocal A/B** bypass toggles.
- **Unobstructed Room + Dedicated 3D Head Trackball & Height Dock:** Orbit the 3D room freely or drag the listener XY directly without internal HUD clutter, steer **Yaw & Pitch** freely from the dedicated articulated **3D Head Trackball (`Cabeça 3D`)**, adjust elevation smoothly with the **Vertical One UI 9 Height Capsule (`Altura Y`)** below the stage, or fine-tune with `40dp` Samsung relative-drag double-tap-reset capsule sliders.
- **Live Source Mixer, Real-Time Dynamic Ceiling Filter & Laboratory Distance:** Tap any speaker/object sphere inside the room to Solo, isolate overhead activity dynamically in real time (`Height Only` tracks moving 3D objects entering/leaving the ceiling plane live), switch real laboratory loudspeaker distances (`0.50 m`–`3.25 m`) seamlessly with clickless equal-power crossfades, configure attenuation rolloff (`Disabled`, `Logarithmic`, `Linear`, `Real 1/d`), and adjust LFE.

### 3. Spatial Converter & Next-Gen Master Studio (`Estúdio` / `Studio`)
- **Next-Gen Immersive Formats & 8 Studio Master Workflows:** Native encoding/decoding for **Eclipsa Audio (IAMF v1.0 OBU)**, **3rd-Order Ambisonics HOA (`ACN/SN3D`)**, and **ADM BWF (`ITU-R BS.2076`)**, alongside 8 offline mastering presets (**Eclipsa IAMF**, **HOA3**, **ADM BWF**, **Personalized Binaural**, **Hybrid Neural+DirAC 7.1.4**, **Spatial Karaoke Atmos**, **Live Karaoke Mixdown**, and **FLAC+Atmos Hybrid Master**) with ITU-R BS.1770-5 loudness & 4× True-Peak ceiling control (`-1.0 dBTP` default), live Lossless Twin readiness status, and contextual Karaoke vocal level adjustment.
- Target any speaker setup from Stereo/Binaural (`Virtualizer`) up to `5.1`, `7.1`, `7.1.4`, `9.1.6`, and matrix-wired layouts with interactive wiring diagrams.
- Optionally bake the 3D Room listener position, head rotation, and active Solo/Mute overrides directly into exported files.
- Built-in batch rendering queue supporting multi-file selection directly via the in-app **Smart File Explorer** (with Android SAF fallback).

### 4. Channel, 3D Object & Neural AI Stem Extractor (`Stems`)
- Isolate bed channels and dynamic 3D objects from immersive formats (`E-AC-3 JOC`, `ADM BWF`, `DAMF`, `LAF`, `IAMF`, `WAV`, `CAF`) into individual mono WAV stems (`16-bit`, `24-bit`, `32-bit float`), per-item 3D renders, combined 3D object renders, or raw bitstream dumps.
- **On-Device Neural AI Stem Separation & Cloud MVSEP:** Supports ONNX Runtime (`HTDemucs` / `BS-RoFormer` / `MelBand-RoFormer`) neural separation for `Vocals / Drums / Bass / Other` (plus Karaoke lead vs backing vocal split) with optional cloud MVSEP offloading and frame-by-frame `.json` `(X, Y, Z)` 3D trajectory export.

### 5. Modular 6-Slot Spatial DSP Engine, USB Pro-Audio, Calibration & OTA Updates (`Ajustes` / `Settings`)
- **Slot 1 — Parametric Spatial Upmixing (`STFT / DirAC / Atmos-Aware`):** Expand stereo/5.1 tracks to 7.1 (`Matrix Upmixing`) or synthesize overhead 3D objects (`Cavernize 3D`) with customizable intensity, smoothing, Atmos-Aware protection, and hybrid Neural Stem + DirAC spatial placement.
- **Slot 2C — Universal Lossless Twin Restoration (LGSR):** Automatically pairs lossy Atmos (`E-AC-3 JOC` / `AC-4`) tracks with a matching local lossless `FLAC`/`WAV` master via sub-sample **GCC-PHAT** time alignment & drift tracking, restoring masked high-frequency transients and coupling/SPX bins with **Instant A/B Compare** (`ATMOS+FLAC` vs `Original Atmos`), Envelope Lock, and Clipping Guard.
- **Slot 2D & Slot 1/4 — Full-Duplex USB Pro-Audio (`libusb` UAC2), Focusrite Scarlett Control & Auto-Vocal Mix-Match:** Low-latency USB DAC/ADC (`AAudio` exclusive/low-latency + userspace `libusb` UAC2 full-duplex driver), full **Focusrite Scarlett 2i2 / Solo (Gen 2 / 3 / 4)** hardware control console (**Air Presence & Drive**, **Auto Gain**, **Clip Safe**, **48V Phantom**, **Inst/Line**, **Direct Monitor**, **Stereo Link**, **NVRAM Save**), and a zero-latency **Auto-Vocal Mix-Match** mastering chain (track Vocal DNA extraction, formant-safe ERB Match EQ, dual FET 1176 + Opto LA-2A dynamics, ADAA saturation, slapback delay + FDN reverb, `1–4 kHz` sidechain vocal-pocket glue, Noise Gate, and Split-Band De-Esser).
- **Slot 4 — Physical Externalization, AutoEq (`8,850+` Headphones) & Head Tracking:** Image-Source Method (ISM) early reflections, Near-Field ILD/ITD cues, ISO 9613-1 air absorption, real-time **IMU / BLE Spatial Head Tracking** (recenter + auto-drift), and an offline **AutoEq** database with automatic Bluetooth/USB headphone device switching and QuickEQ / Equalizer APO `.txt` room calibration import.
- **Slot 5 — Official Offline Binaural Profiles (HRIR / BRIR), MagLS & Perceptual HRTF Personalization:** Includes verified 48 kHz laboratory profiles ready offline — **SADIE II D1 (Neumann KU100: Diffuse-Field & DFC-compensated 0.30s Studio Control Room BRIR)**, **TH Köln / Bernschütz (Neumann KU100: unified 5-distance DFC series `0.50 m` / `0.75 m` / `1.00 m` / `1.50 m` / `3.25 m` + Far-Field Raw)**, and **SADIE II D2 (KEMAR 45BB: Diffuse-Field & DFC-compensated Studio BRIR)** — alongside a universal mathematical **Minimum-Phase + ITD** real-cepstrum toggle, **MagLS** Orders 1–3 spherical-harmonic binaural decoding, **Woodworth-Schlosberg** biometric head-width ITD scaling (`13.0–18.0 cm`), **HpTF ear-canal resonance notch** (`6–10 kHz` Orfanidis filter + safe `-18 dBFS` tone/sweep test), an interactive **60-second Perceptual HRTF Wizard** (SONICOM/HUTUBS), and custom `.sofa` (AES69 HDF5) / Impulcifer `.wav` import.
- **Slot 6 — Psychoacoustic Loudness, Dialog Enhancement & Broadcast Limiter:** **ISO 226:2023** equal-loudness compensation, **ITU-R BS.1770-5** integrated loudness normalization, **Spatial Dialog Enhancement**, and a transparent **4× oversampled True-Peak Limiter**.
- **Built-in Binary Updaters & OTA Releases:** Automatic ARM64 static `FFmpeg` (`FilesDir/bin/ffmpeg`) and `truehdd` (`FilesDir/bin/truehdd`) installers/updaters, plus in-app OTA APK updater checking `android-latest` while preserving all versioned `vX.Y.Z` GitHub Releases.

---

## Supported Media & Codecs
- **Containers:** `.eac3`, `.ec3`, `.ac3`, `.thd`, `.mlp`, `.iamf`, `.amb`, `.mkv`, `.mka`, `.mp4`, `.m4a`, `.m4v`, `.mov`, `.webm`, `.weba`, `.wav`, `.caf`, `.laf`, `.atmos` (plus Android `MediaCodec` decoding fallback for `.mp3`, `.flac`, `.aac`, `.ogg`, etc., and smart FFmpeg container probe/demux fallback).
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
- Bundled official binaural datasets: **SADIE II Database** (University of York AudioLab, Apache License 2.0) and **TH Köln / Bernschütz Neumann KU100 Far-Field & Near-Field HRIR Databases** (CC BY 3.0 / 4.0) — see `CavernSamples/CavernizeAndroid/Assets/hrir/LICENSES.txt`.
- Licensed under the terms of [`LICENSE.md`](./LICENSE.md).
