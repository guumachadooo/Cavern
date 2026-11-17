---
name: cavernize-device-debug
description: >-
  Diagnoses runtime issues on the user's Android device using Cavernize shared logs, checks FFmpeg
  and SELinux execution constraints, and inspects native One UI SystemUI behavior safely. Use when
  troubleshooting crashes, FFmpeg failures, playback/notification bugs, or One UI visual parity.
---

# Device Diagnostics & SystemUI Inspection Runbook

## 1. Reading Runtime & FFmpeg Logs from the Device
[AppLogger.cs](../../../CavernSamples/CavernizeAndroid/Services/AppLogger.cs) writes persistent logs to public Downloads so you can inspect crashes and FFmpeg stderr directly from this environment:

- Shared path: `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`
- Also note the running app version/build logged on startup (`=== Cavernize v... started ===`).
- Always check `FFmpeg` exit codes, arguments, and `stderr` logged by `AppLogger.LogFFmpeg`.

## 2. Verifying FFmpeg, truehdd & Permission Constraints
- Static ARM64 FFmpeg installs inside `FilesDir/bin/ffmpeg` (`FFmpegInstallerService`) and static ARM64 musl `truehdd` installs inside `FilesDir/bin/truehdd` (`TruehddInstallerService`).
- Confirm [AndroidManifest.xml](../../../CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml) retains `android:targetSdkVersion="28"` so SELinux keeps the app in `untrusted_app_27` and permits executing downloaded binaries from `FilesDir`.
- For Android 13+ (API 33+) media/notifications, verify runtime permission requests cover `READ_MEDIA_AUDIO`, `READ_MEDIA_VIDEO`, and `POST_NOTIFICATIONS`.

## 3. Native One UI 9 Reference Inspection (Read-Only)
If the user asks to match a native Samsung One UI 9 animation or component accurately:
- You may inspect classes/resources read-only inside system APKs on the device (e.g., `/system/system_ext/priv-app/SystemUI/SystemUI.apk` with standard tools like `dexdump` / `aapt` if present), without bypassing any access control or modifying system state.
- Record useful UI findings in [ONEUI9_REFERENCE.md](../../docs/ONEUI9_REFERENCE.md) instead of bloating `AGENTS.md`.
