# Cavernize Android

> **Flagship Spatial Audio Player, Dolby Atmos / TrueHD / IAMF / HOA3 Engine, Neural Stem Studio & USB Pro-Audio Suite for Android** — built on the [Cavern](https://github.com/VoidXH/Cavern) acoustic DSP core and `.NET 11` (`net11.0-android`) with a native **One UI 9** presentation shell.

[![Build Android APK](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml/badge.svg)](https://github.com/guumachadooo/Cavern/actions/workflows/BuildAndroid.yml)
[![Fast Quality & File-Budget Guard](https://github.com/guumachadooo/Cavern/actions/workflows/FastQualityGuard.yml/badge.svg)](https://github.com/guumachadooo/Cavern/actions/workflows/FastQualityGuard.yml)
[![Latest Release](https://img.shields.io/github/v/release/guumachadooo/Cavern?label=Latest%20APK&color=0A84FF)](https://github.com/guumachadooo/Cavern/releases/latest)
[![Platform](https://img.shields.io/badge/.NET%2011-Android%2015%2B%20%28API%2035%29-34C759)](#building)
[![Native Engines](https://img.shields.io/badge/W%5EX%2016KB%20ELF-FFmpeg%20%7C%20truehdd%20%7C%20ONNX%20%7C%20AAudio-111827)](#key-capabilities)

---

## Key Capabilities

### 1. Spatial Multimedia Player & In-App Library (`Play`)
- Reads embedded tags (`Title`, `Artist`, `Album`, sample rate, bitrate) and album cover art directly via Android `MediaMetadataRetriever` (or generates procedural spatial art when absent), harmonizes the frosted SF Pro UI backdrop & One UI 9 wave seekbar, and optionally auto-fetches missing HD album art and synced `.lrc` lyrics online.
- **In-App Music & Media Library (`Tocando Agora | Biblioteca`):** Built-in Samsung Music + Poweramp-inspired browser with persistent disk cache (`library_cache_v3.json`), direct pure-C# binary MP4 (`trkn`/`disk`/`©alb`/`©ART`), FLAC, and ID3v2 tag reading for accurate multi-disc & track-number sorting (even on `.m4a.mp4` Atmos files skipped by Android `MediaStore`), **Media Type Split (`All | Music | Videos`)**, persistent **Sort (`A–Z | Artist | Recent | Quality`)**, `180 ms` debounced search with Clear button, **Spatial / Surround** filter pills, **Smart Clean v2** evidence-based filtering (`HasEmbeddedMusicTags` decoupled from filename/folder fallbacks, evaluating SFX/voice/camera/short `<25s` rules before `IsSurround`/`IsSpatial`) with a transparent **Smart Clean Inspector** (rule toggles, hidden-track inspection, 1-tap restore/allowlist, folder hiding), **In-App Smart File Explorer** (`OneUiFileExplorerDialog`), and **Album+Artist-Scoped Variant Merging** powered by `AudioQualityAnalyzer` with an expandable live **Format Switcher (`[ Active ] +N`)**.
- **Single-Viewport Unified Player (`sf_pro_android_ui.ttf` + One UI 9) + `[A | B]` Audio Spec Compare:** Adapts artwork, lyrics stage, and spacing dynamically to the viewport (collapsing smoothly on compact screens and landscape Lyrics) with a retractable bottom navigation pill, smooth Cover ↔ Lyrics transitions, a live **`ATMOS+FLAC`** Lossless-Twin badge pill, and a dedicated **Audio Specification** modal featuring a segmented **`[A: Original | B: ATMOS+FLAC / Spatial 3D]`** A/B toggle, live quality report refresh, collapsible Pro signal telemetry, and **Rescan Twin**, plus flanking spatial toggles (**`Cavernize 3D | Binaural Profile / Direct 2.0 | Matrix 7.1`**), 120fps wave progress bar, unified frosted transport, slim volume bar, and ref-counted blurred modals.
- **2-Tier Apple Music Sing Dock & Live Mic / Scarlett Controls in `Lyrics`:** Inside the synced Lyrics view, a 2-tier frosted Sing dock provides real-time **Vocal Level (`0%–100%`)** slider + percentage + Vocal DNA pill on Tier 1, and a scrollable row of plain-label action chips (**Karaoke**, **Live Mic**, **Mix-Match**, **Vocal A/B**, **Scarlett**, **AI Stems**) on Tier 2.
- **Ranked Online Cover Art, Lyrics & Subtitles Scrapers:** Automatic background enrichment and editable manual search supporting Title, Artist, and Album **Edition / Deluxe** preservation (`(Deluxe)`, `(Taylor's Version)`, etc.), high-res album art (`iTunes HD`, `TheAudioDB`, `CoverArtArchive`, `TMDb`), synced & plain lyrics (`LRCLIB Synced`, `Netease Synced`, `LRCLIB Plain`, `Lyrics.ovh`), and `.srt` video subtitles (`OpenSubtitles` REST GZip + `Podnapisi` fallback).
- **Samsung Video Player + YouTube Video Mode (with Subtitles & Auto-Rotate Fullscreen):** Automatically switches video files into a rich video player with `CC` subtitles (`.srt`, `.vtt`, `.ass`/`.ssa` external files + online `OpenSubtitles`/Podnapisi `.srt` + FFmpeg-extracted MKV/MP4 streams), audio track switcher, aspect ratio cycling (`Fit` / `Fill` / `Stretch`), center `-10s` / `+10s` & double-tap gestures, touch lock, cutout-safe fullscreen HUD insets, and one-tap portrait/landscape fullscreen with automatic orientation restore on exit.
- Publishes `MediaSession` metadata, album art, and lockscreen transport/track-skip actions (`Previous`, `-10s`, `Play/Pause`, `+10s`, `Next`) to the Android system notification shade and lock screen.

### 2. Interactive 3D Acoustic Room & Spatial X-Ray Inspector (`Sala 3D` / `3D Room`)
- **Active Signal Chain Bar & Mini Transport:** Tappable 5-pill live signal chain overview (`Input Decoder`, `Upmix / AI 3D`, `LGSR Twin`, `Room / HRTF`, `AutoEq + Loudness`) plus compact transport & volume strip directly above the stage.
- **Volumetric 3D Room, Interactive Object/Stem/Mic Dragging & Articulated Head Gimbal (`RoomSimulatorView`):** Architectural 3D acoustic room with translucent shaded walls, sweet-spot floor wave rings, live RMS acoustic rays, monitor bed nodes with dB level rings (Green / Amber / Red, Mute `X`, Solo gold ring), and sculpted 3D dynamic object, neural stem, and live microphone orbs with discreet dashed floor drop-lines, smooth tapered glowing Bézier motion ribbons, ceiling elevation micro-chevrons, long-press/drag 3D pinning (with instant pin reset), and automatic fade-out when stationary & silent (`RMS == 0`).
- **6DoF Physical Propagation Delay & Essential/Pro Spatial X-Ray Inspector:** Real-time speed-of-sound (`343 m/s`) fractional time-of-flight delay across bed/object sources (with LFE excluded), an optional **72-cell spherical 3-band Soundfield Powermap** (`All / Low / Mid / High`), Essential summary vs. Pro telemetry (**ITU-R BS.1770-5** loudness & 4× True-Peak limiter, **LGSR Lossy vs Lossless** coupling/SPX/GCC-PHAT diagnostics, **Live Vocal Chain** metering), and one-tap **LGSR A/B** and **Vocal A/B** bypass toggles.
- **Unobstructed Room + Dedicated 3D Head Trackball & Height Dock:** Orbit the 3D room freely or drag the listener XY directly without internal HUD clutter, steer **Yaw & Pitch** freely from the dedicated articulated **3D Head Trackball (`Cabeça 3D`)**, adjust elevation smoothly with the **Vertical One UI 9 Height Capsule (`Altura Y`)** below the stage, or fine-tune with `40dp` Samsung relative-drag double-tap-reset capsule sliders.
- **Live Source Mixer, Real-Time Dynamic Ceiling Filter & Laboratory Distance:** Tap any speaker/object sphere inside the room to Solo, isolate overhead activity dynamically in real time (`Height Only` tracks moving 3D objects entering/leaving the ceiling plane live), switch real laboratory loudspeaker distances (`0.50 m`–`3.25 m`) seamlessly with clickless equal-power crossfades, configure attenuation rolloff (`Disabled`, `Logarithmic`, `Linear`, `Real 1/d`), and adjust LFE.

### 3. Spatial Converter & Next-Gen Master Studio (`Estúdio` / `Studio`)
- **Unified `Master | Classic | Queue` Workflow & Pipeline Readiness Pills:** Switch cleanly between 8 offline mastering presets (`Master`), custom target speaker layouts (`Classic`), and the multi-file batch queue (`Queue`) with live Pipeline Readiness indicators (`FFmpeg`, `TrueHD`, `ONNX`, `Lossless Twin`) and a single contextual Primary Action button.
- **Next-Gen Immersive Formats & 8 Studio Master Workflows:** Native encoding/decoding for **Eclipsa Audio (IAMF v1.0 OBU)**, **3rd-Order Ambisonics HOA (`ACN/SN3D`)**, and **ADM BWF (`ITU-R BS.2076`)**, alongside 8 offline mastering presets (**Eclipsa IAMF**, **HOA3**, **ADM BWF**, **Personalized Binaural**, **Hybrid Neural+DirAC 7.1.4**, **Spatial Karaoke Atmos**, **Live Karaoke Mixdown**, and **FLAC+Atmos Hybrid Master**) with ITU-R BS.1770-5 loudness & 4× True-Peak ceiling control (`-1.0 dBTP` default), live Lossless Twin readiness status, and contextual Karaoke vocal level adjustment.
- Target any speaker setup from Stereo/Binaural (`Virtualizer`) up to `5.1`, `7.1`, `7.1.4`, `9.1.6`, and matrix-wired layouts with interactive wiring diagrams.
- Optionally bake the 3D Room listener position, head rotation, and active Solo/Mute overrides directly into exported files.
- Built-in batch rendering queue supporting multi-file selection directly via the in-app **Smart File Explorer** (with Android SAF fallback).

### 4. Channel, 3D Object & Neural AI Stem Extractor (`Stems`)
- **Goal-Driven Extraction Cards & Live AI Chain Preview:** Organized into 4 clear workflow goals (**Raw Channels & Bitstream**, **3D Spatial Object Renders**, **AI Source Separation (MVSEP)**, and **Harmonic & Hybrid Atmos Restoration**) with live on-device AI chain preview and contextual controls.
- Isolate bed channels and dynamic 3D objects from immersive formats (`E-AC-3 JOC`, `ADM BWF`, `DAMF`, `LAF`, `IAMF`, `WAV`, `CAF`) into individual mono WAV stems (`16-bit`, `24-bit`, `32-bit float`), per-item 3D renders, combined 3D object renders, or raw bitstream dumps.
- **On-Device Neural AI Stem Separation & Cloud MVSEP:** Supports ONNX Runtime (`HTDemucs` / `BS-RoFormer` / `MelBand-RoFormer`) neural separation for `Vocals / Drums / Bass / Other` (plus Karaoke lead vs backing vocal split) with optional cloud MVSEP offloading and frame-by-frame `.json` `(X, Y, Z)` 3D trajectory export.

### 5. One UI 9 6-Category Settings Hub, USB Pro-Audio, Calibration & Built-In Engines (`Ajustes` / `Settings`)
- **6-Category Settings Hub, Cross-Category Search & Guided Modals:** Clean One UI 9 navigation across 6 categories (**Playback & Spatial**, **Headphones & Calibration**, **AI & Restoration**, **Pro-Audio & Microphone**, **Engines & Storage**, **Diagnostics & About**) with live status subtitles, cross-category keyword search, **Essential / Pro** progressive disclosure, prerequisite banners, predictive back navigation, and `?` guided help modals (`HRTF Wizard` with draft cancel-revert, `AutoEq`, `AI Models & Track Cache` with `256–4096 MB` LRU limit, `Auto-Vocal Mix-Match`, and `Focusrite Scarlett`).
- **Parametric Spatial Upmixing (`STFT / DirAC / Atmos-Aware`):** Expand stereo/5.1 tracks to 7.1 (`Matrix Upmixing`) or synthesize overhead 3D objects (`Cavernize 3D`) with customizable intensity, smoothing, Atmos-Aware protection, and hybrid Neural Stem + DirAC spatial placement.
- **Universal Lossless Twin Restoration (LGSR):** Automatically pairs lossy Atmos (`E-AC-3 JOC` / `AC-4`) tracks with a matching local lossless `FLAC`/`WAV` master via sub-sample **GCC-PHAT** time alignment & drift tracking, restoring masked high-frequency transients and coupling/SPX bins with **Instant A/B Compare** (`ATMOS+FLAC` vs `Original Atmos`), Envelope Lock, and Clipping Guard.
- **Full-Duplex USB Pro-Audio (`libusb` UAC2 + `AAudio`), Focusrite Scarlett Control & Auto-Vocal Mix-Match:** Low-latency USB DAC/ADC (`AAudio` exclusive/low-latency Path A + userspace `libusb` UAC2 full-duplex Path B), full **Focusrite Scarlett 2i2 / Solo (Gen 2 / 3 / 4)** hardware control console (**Air Presence & Drive**, **Auto Gain**, **Clip Safe**, **48V Phantom**, **Inst/Line**, **Direct Monitor**, **Stereo Link**, **NVRAM Save**), and a zero-latency **Auto-Vocal Mix-Match** mastering chain (track Vocal DNA extraction, guided 4s Mic Voiceprint calibration, formant-safe ERB Match EQ, dual FET 1176 + Opto LA-2A dynamics, ADAA saturation, slapback delay + FDN reverb, `1–4 kHz` sidechain vocal-pocket glue, Noise Gate, and Split-Band De-Esser).
- **Physical Externalization, AutoEq (`8,850+` Headphones) & Head Tracking:** Image-Source Method (ISM) early reflections, Near-Field ILD/ITD cues, ISO 9613-1 air absorption, real-time **IMU / Galaxy Buds / BLE OSC Spatial Head Tracking**, and an offline **AutoEq** database with automatic Bluetooth/USB headphone device switching and QuickEQ / Equalizer APO `.txt` room calibration import.
- **Official Offline Binaural Profiles (HRIR / BRIR), MagLS & Perceptual HRTF Personalization:** Includes verified 48 kHz laboratory profiles ready offline — **SADIE II D1 (Neumann KU100)**, **TH Köln / Bernschütz (Neumann KU100 5-distance DFC `0.50 m`–`3.25 m` + Far-Field)**, and **SADIE II D2 (KEMAR 45BB)** — alongside **Minimum-Phase + ITD**, **MagLS** Orders 1–3, **Woodworth-Schlosberg** biometric head-width ITD scaling (`13.0–18.0 cm`), **HpTF ear-canal resonance notch**, interactive **Perceptual HRTF Wizard**, and custom `.sofa` (AES69 HDF5) / Impulcifer `.wav` import.
- **Psychoacoustic Loudness, Dialog Enhancement & Broadcast Limiter:** **ISO 226:2023** equal-loudness compensation, **ITU-R BS.1770-5** integrated loudness normalization, **Spatial Dialog Enhancement**, and a transparent **4× oversampled True-Peak Limiter**.
- **Built-In W^X Engines & OTA Updates:** `libffmpeg.so`, `libtruehdd.so`, `libonnxruntime.so`, and `libcavernize_proaudio.so` are bundled directly inside APK `NativeLibs/arm64-v8a/` (`NativeLibraryDir`, 16 KB ELF aligned, zero writable-storage execution), alongside in-app OTA APK updates checking `android-latest` while preserving all versioned `vX.Y.Z` GitHub Releases.

---

## Supported Media & Codecs
- **Containers:** `.eac3`, `.ec3`, `.ac3`, `.thd`, `.mlp`, `.iamf`, `.amb`, `.mkv`, `.mka`, `.mp4`, `.m4a`, `.m4v`, `.mov`, `.webm`, `.weba`, `.wav`, `.caf`, `.laf`, `.atmos` (plus Android `MediaCodec` decoding fallback for `.mp3`, `.flac`, `.aac`, `.ogg`, etc., and smart FFmpeg container probe/demux fallback).
- **Dolby TrueHD / Dolby Atmos TrueHD:** Decoded cleanly on-device via the built-in 16 KB ELF-aligned ARM64 `libtruehdd.so` engine (`NativeLibraryDir`, W^X compliant), enabling real-time 3D Atmos playback, Studio export, and object stem/trajectory extraction (`.json`) alongside automatic temporary cache cleanup.

---

## Building

### Requirements
- `.NET 11` SDK (`net11.0-android` workload) and Android SDK (`minSdk 26`, `targetSdk 35`, Edge-to-Edge & Predictive Back).

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
Pushing to `master` (or manually triggering `.github/workflows/BuildAndroid.yml`) compiles a signed 16 KB ELF-aligned ARM64 APK in GitHub Actions, publishes a permanent versioned release (`v2.9.<run>`) automatically promoted as **`Latest`**, and mirrors the binary to the rolling `android-latest` OTA alias used by the in-app updater. In parallel, `.github/workflows/FastQualityGuard.yml` verifies static guardrails, L10n parity, unit tests, native C11 pro-audio tests, and workspace file budget (`<920/999`).

---

## Architecture Overview
- [`Cavern/`](./Cavern) & [`Cavern.Format/`](./Cavern.Format) — Strict C# 8.0 (`netstandard2.1`) core spatial DSP engine, object renderers, neural pipelines, restoration/vocal chains, and immersive bitstream parsers (IAMF, HOA3, ADM BWF, E-AC-3 JOC, TrueHD, WAV, MKV).
- [`CavernSamples/Reusable/Cavernize.Logic/`](./CavernSamples/Reusable/Cavernize.Logic) — Shared player engine (`RealtimePlayerEngine`), ONNX WebGPU/FP16 conversion & streaming adapters, conversion environment, post-render reports, and queue logic.
- [`CavernSamples/CavernizeAndroid/`](./CavernSamples/CavernizeAndroid) — Native `.NET 11` Android One UI 9 shell, `RoomSimulatorView` 3D acoustic room, `Native/cavernize_proaudio/` (AAudio Exclusive + userspace `libusb` UAC2), Focusrite Scarlett console, ONNX Runtime bridge, and OTA updater.
- [`archive/desktop-legacy-wpf.tar.gz`](./archive/desktop-legacy-wpf.tar.gz) — Lossless SHA-256 verified archive of legacy upstream Windows WPF desktop projects (also preserved in full git history on branch `backup/cavern-pre-H-ab942fd6`), keeping the repository lean and well below AI Studio's 999-file import ceiling.

---

## Companion Ecosystem
- [**OmniAgent**](https://github.com/guumachadooo/OmniAgent) — Dedicated Zero-Trust E2E Android companion app, Termux daemon, and Cloudflare Durable Objects Hub v6 for same-signature local IPC with Cavernize, biometric approval, remote builds, and automatic APK delivery.
- [**Dynamic Fractal Planner**](https://github.com/guumachadooo/dynamic-fractal-planner) — Zero-dependency AI planning, SOTA pre-flight, wiring-gate, timeless error-learning, and file-budget verification plugin (`v2.10.0`).

---

## Credits & Licence
- Built upon the open-source [**Cavern**](https://github.com/VoidXH/Cavern) spatial audio framework by **VoidX (`VoidXH`)**.
- Original Cavern documentation: [cavern.sbence.hu](http://cavern.sbence.hu/cavern/doc.php) | [HRTF repository](https://github.com/VoidXH/HRTF).
- Bundled official binaural datasets: **SADIE II Database** (University of York AudioLab, Apache License 2.0) and **TH Köln / Bernschütz Neumann KU100 Far-Field & Near-Field HRIR Databases** (CC BY 3.0 / 4.0) — see `CavernSamples/CavernizeAndroid/Assets/hrir/LICENSES.txt`.
- Licensed under the terms of [`LICENSE.md`](./LICENSE.md).
