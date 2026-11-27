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
- **Cloudflare Unified Diagnostics Hub v3 (Default for Runtime Telemetry)**: `python3 -B .agents/scripts/fetch_live_diagnostics.py --cloudflare --relay-url <worker> --pair-secret <64hex> --all` (`--history` for rolling 14-day snapshots; see `.agents/docs/DIAGNOSTICS_HUB.md`). Zero Git commit spam, zero GitHub PAT — uses the same Keystore 256-bit Pair Key + Cloudflare Worker `RELAY_KV`.
- **GitHub CI & Legacy Fallback**: `python3 -B .agents/scripts/fetch_live_diagnostics.py --all` (reads `origin/diagnostics:ci/latest-build-diagnostics.md` + `ci/latest-learned-lessons.md` + optional legacy `device-logs/*` directly via GitHub Contents API w/ zero CDN lag).
- **10-Layer Ultra-Deep Self-Check (`SelfDiagnosticEngine.cs` + `MainActivity.SelfAudit.cs`)**: `Quick` / `Full DSP` / **`Total + UI` (`Ultra`)** — L1 W^X `NativeLibs`, L2 RAM/I/O + 100% `AndroidStrings.AuditAllDictionaries()` + `AppSettingsStore` integrity + routes, L3 12 deep DSP/service probes (HRIR/BRIR, real AutoEq `.aeqz` catalog, Double-EQ Guard, ISO 9613-1, 4x True-Peak, Vocal Chain, Kaiser Sinc, Farrow Sync, Decorrelator, Parametric STFT, Near-Field/ITD, AHRS, RingBuffer/Library), L4 8-vector Strength-2 Pairwise Matrix, L5 WAV/FLAC + `AudioQualityAnalyzer`, L6 Cloud Hub readiness, L7 `AppLogger` runtime Fatal/Error/Crash scan, L8 live 5-tab + 6-Settings-category UI geometry/text/alignment walk, L9 live 27-modal open-audit-dismiss smoke test, L10 interactive controls & refresh test w/ guaranteed state restoration.
- **Live Phone Bridge v3+ (`LiveDiagnosticBridgeService.cs` + `LiveBridgeWakeService.cs` + `LiveBridgeAuthActivity.cs` + `MainActivity.LiveBridge.cs` + `.agents/docs/cloudflare_live_relay_worker.js` v6 + `.agents/scripts/omni_agent.py`)**:
  - **Direct / IPC session**: `python3 -B .agents/scripts/omni_agent.py cavernize-cmd <cmd> [--args '{...}']` (or `omni_agent.py connect` for zero-secret Smart Knock / biometric wake).
  - **Predefined + Unrestricted Dynamic Tier 2 Capabilities**: Tier 0 (`ping`, `capabilities`, `quick_check`, `full_check`, `summary`, `log_tail`, `log_search`, `audio_routes`, `memory`, `app_state`, `dsp_telemetry`, `settings_get`, `settings_schema`, `library_query`, `files_list`, `file_read_text`, `file_info`, `native_probe`, `models_list`, `ui_tree` modal-aware, `screenshot_ui` modal-aware), Tier 1 (`ultra_check`, `ui_audit`, `settings_set` w/ secret blocklist, `tab_switch`, `playback_control`, `open_file`, `ui_action`, `ui_click`, `cloud_sync_now`), Tier 2 (`reflect_types`, `reflect_members`, `reflect_get`, `reflect_set`, `reflect_invoke` sync+async+UI-thread, `ui_interact` full View/Modal/MotionEvent automation, `shell_exec` sandboxed `/system/bin/sh -c` w/ W^X guard, `pipeline` up to 30 steps w/ `$prev`, `log_clear`, `cache_clear`, `native_exec_probe`, `hot_exec`, `chaos_hunt`, `logcat_tail`, `anomalies`). Never persist session codes or pairing secrets in repo files.
- **In-RAM Live Hot-Execution (`hot-exec` — Test Fixes in RAM Before Compiling APK!):** Run `python3 -B .agents/scripts/omni_agent.py hot-exec --name <Probe> --code '...'` (compiles managed IL via Roslyn locally or via Termux PRoot `/root/.dotnet/dotnet`, signs HMAC-SHA256, executes inside live S25U Cavernize RAM via collectible `AssemblyLoadContext.LoadFromStream`, unloads cleanly in `<3s`, 100% W^X). Use this BEFORE waiting on 6-minute APK builds to verify hypotheses, DSP behavior, or UI fixes!
- **Auto-Wake Local Linux / PRoot (`wake-linux`):** Whenever `termux`/`debian` is offline while `android` is online, run `python3 -B .agents/scripts/omni_agent.py wake-linux` (`launch_termux_daemon` via `android` channel) instead of giving up on local Linux/Antigravity access.
- **Live Adversarial Bug-Hunter SOTA v2 (`omni_agent.py hunt`):** Run `python3 -B .agents/scripts/omni_agent.py hunt --mode standard --out-md .agents/docs/LIVE_BUG_HUNT_REPORT.md` to execute V1–V10 (active vs historical log stratification, concurrent playback + acoustic oracle, settings chaos w/ exact post-restore verification, library edge-case & search oracle, corrupt file fuzzer, UI monkey + latency freeze watchdog, spatial choreography trajectory oracle, & in-RAM `hot_exec` closed-loop probe).
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
