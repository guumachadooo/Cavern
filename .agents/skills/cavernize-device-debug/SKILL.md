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
- Live Phone Bridge v2 (`LiveDiagnosticBridgeService.cs` + `LiveBridgeWakeService.cs` + `LiveBridgeAuthActivity.cs` + `MainActivity.LiveBridge.cs` + `.agents/docs/cloudflare_live_relay_worker.js` v2 + `.agents/scripts/live_phone_bridge.py` -> plugin `live_bridge_client.py`):
  - **Direct session**: `python3 -B .agents/scripts/live_phone_bridge.py --url <url> --code <CAV-...> --cmd <cmd> [--args '{...}']` (or `--capabilities`, `--batch '[...]'`, `--save-screenshot out.jpg`).
  - **Background Standby Biometric Wake (even when app is closed)**: `python3 -B .agents/scripts/live_phone_bridge.py --url <url> --wake --pair-secret <64hex> --tier <0|1|2> --duration <300..3600> --reason "..." --cmd <cmd>` -> verifies HMAC-SHA256 + timestamp + nonce on phone -> shows native Android `BiometricPrompt` fingerprint overlay -> opens session & returns E2E encrypted session token (`SHA256-CTR + HMAC-SHA256`) so Cloudflare KV never sees plaintext credentials.
  - **Flexible Tiered Capabilities (~31 commands + `batch`)**: Tier 0 (`ping`, `capabilities`, `quick_check`, `full_check`, `summary`, `log_tail`, `log_search`, `audio_routes`, `memory`, `app_state`, `dsp_telemetry`, `settings_get`, `settings_schema`, `library_query`, `files_list`, `file_read_text`, `file_info`, `native_probe`, `models_list`, `ui_tree`, `screenshot_ui`), Tier 1 (`settings_set` w/ secret blocklist, `tab_switch`, `playback_control`, `open_file`, `ui_action`, `ui_click`, `cloud_sync_now`), Tier 2 (`log_clear`, `cache_clear`, `native_exec_probe` W^X `NativeLibraryDir` only). Never persist session codes or pairing secrets in repo files.
- Also note the running app version/build, `sid=...`, RAM/`NativeLibs`, and `CONFIG` DSP snapshot logged on startup (`=== Session started [sid=...] | Cavernize v... ===`).
- Always check `FFmpeg` exit codes, arguments, and `stderr` logged by `AppLogger.LogFFmpeg`, plus `[MEM]` `OnTrimMemory` and `[CRASH]` markers.

## 2. Verifying W^X Native Binaries, 16 KB ELF & Permission Constraints
- Bundled ARM64 binaries (`libffmpeg.so`, `libtruehdd.so`, `libonnxruntime.so`, `libcavernize_proaudio.so`) ship inside APK `NativeLibs/arm64-v8a/` and execute directly from `ApplicationInfo.NativeLibraryDir` (W^X compliant, 16 KB ELF aligned; zero `FilesDir/bin/` execution). Only `.onnx` model weights reside in `FilesDir/neural_models/`.
- Confirm [AndroidManifest.xml](../../../CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml) sets `android:targetSdkVersion="35"` (`>= 35`), `android:extractNativeLibs="true"`, `android:enableOnBackInvokedCallback="true"`, and omits `requestLegacyExternalStorage`.
- Verify runtime permission requests cover `READ_MEDIA_AUDIO`, `READ_MEDIA_VIDEO` / `READ_MEDIA_VISUAL_USER_SELECTED` (API 34+), `POST_NOTIFICATIONS` (API 33+), `BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` (API 31+), `USE_BIOMETRIC`/`USE_FULL_SCREEN_INTENT` (Live Bridge v2 wake), and `ACCESS_FINE_LOCATION` (API 26–30 BLE OSC), plus matching FGS permissions (`FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `FOREGROUND_SERVICE_MICROPHONE`, `FOREGROUND_SERVICE_MEDIA_PROCESSING`, `FOREGROUND_SERVICE_DATA_SYNC`).

## 3. Native One UI 9 Reference Inspection (Read-Only)
If the user asks to match a native Samsung One UI 9 animation or component accurately:
- You may inspect classes/resources read-only inside system APKs on the device (e.g., `/system/system_ext/priv-app/SystemUI/SystemUI.apk` with standard tools like `dexdump` / `aapt` if present), without bypassing any access control or modifying system state.
- Record useful UI findings in [ONEUI9_REFERENCE.md](../../docs/ONEUI9_REFERENCE.md) instead of bloating `AGENTS.md`.
