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
- Live mid-message GitHub fetch (no CDN cache lag): `python3 -B .agents/scripts/fetch_live_diagnostics.py --all` (reads `origin/diagnostics:ci/latest-build-diagnostics.md`, `device-logs/latest_selfcheck.md`, `device-logs/latest_summary.json`, and `device-logs/cavernize_log.txt` directly via GitHub Contents API).
- GitHub Cloud Sync (`CloudLogSyncService.cs`): `origin/diagnostics:device-logs/cavernize_log.txt`, `latest_summary.json`, and `latest_selfcheck.{md,json}` (default) or `master:.agents/device_logs/cavernize_log.txt` (`[skip ci]`); CI warnings+errors at `origin/diagnostics:ci/latest-build-diagnostics.md` (`Cavernize-Build-Diagnostics` artifact).
- On-device Self-Check (`SelfDiagnosticEngine.cs`): Quick/Full 6-layer probe + 8-vector Strength-2 Pairwise DSP Matrix, auto-runs post-update and uploads to `diagnostics` when Cloud Log Sync is configured.
- Live 15-min Phone Bridge (`LiveDiagnosticBridgeService.cs` + `.agents/docs/cloudflare_live_relay_worker.js` + `.agents/scripts/live_phone_bridge.py`): when user enables an ephemeral 15-min session and shares their `https://<worker>.workers.dev` URL + `CAV-...` code, query the phone live mid-message via `python3 -B .agents/scripts/live_phone_bridge.py --url <url> --code <code> --cmd <ping|quick_check|full_check|log_tail|summary|audio_routes|memory>` (strict read-only whitelist; never persist session codes).
- Also note the running app version/build, `sid=...`, RAM/`NativeLibs`, and `CONFIG` DSP snapshot logged on startup (`=== Session started [sid=...] | Cavernize v... ===`).
- Always check `FFmpeg` exit codes, arguments, and `stderr` logged by `AppLogger.LogFFmpeg`, plus `[MEM]` `OnTrimMemory` and `[CRASH]` markers.

## 2. Verifying W^X Native Binaries, 16 KB ELF & Permission Constraints
- Bundled ARM64 binaries (`libffmpeg.so`, `libtruehdd.so`, `libonnxruntime.so`, `libcavernize_proaudio.so`) ship inside APK `NativeLibs/arm64-v8a/` and execute directly from `ApplicationInfo.NativeLibraryDir` (W^X compliant, 16 KB ELF aligned; zero `FilesDir/bin/` execution). Only `.onnx` model weights reside in `FilesDir/neural_models/`.
- Confirm [AndroidManifest.xml](../../../CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml) sets `android:targetSdkVersion="35"` (`>= 35`), `android:extractNativeLibs="true"`, `android:enableOnBackInvokedCallback="true"`, and omits `requestLegacyExternalStorage`.
- Verify runtime permission requests cover `READ_MEDIA_AUDIO`, `READ_MEDIA_VIDEO` / `READ_MEDIA_VISUAL_USER_SELECTED` (API 34+), `POST_NOTIFICATIONS` (API 33+), `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` (API 31+), and `ACCESS_FINE_LOCATION` (API 26–30 BLE OSC), plus matching FGS permissions (`FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `FOREGROUND_SERVICE_MICROPHONE`, `FOREGROUND_SERVICE_MEDIA_PROCESSING`).

## 3. Native One UI 9 Reference Inspection (Read-Only)
If the user asks to match a native Samsung One UI 9 animation or component accurately:
- You may inspect classes/resources read-only inside system APKs on the device (e.g., `/system/system_ext/priv-app/SystemUI/SystemUI.apk` with standard tools like `dexdump` / `aapt` if present), without bypassing any access control or modifying system state.
- Record useful UI findings in [ONEUI9_REFERENCE.md](../../docs/ONEUI9_REFERENCE.md) instead of bloating `AGENTS.md`.
