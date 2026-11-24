# Cross-App Device Log Forensic Remediation (`BP_DEVICE_LOG_REMEDIATION`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_DEVICE_LOG_REMEDIATION|domain_vec=dsp-hw:1.00,infra-ops:0.65,software:0.55|door=1-way|tier=T3|status=SOTA-v1|h1_wave=W0
PARENT_ADR|none
- `dsp-hw`: Split Offline / RT 0-alloc 0-lock hot-path (<1ms/block) / Async control; fuse owned signals/hw; prove math in offline golden bench first.
- `infra-ops`: Design for unattended recovery, idempotent deploy/rollback, OOM/quota ceilings, zero-blind-retry backoff & structured observability.
- `software`: Enforce explicit contracts, idempotency, bounded latency (p95/p99), backward-compatible schema migration & failure isolation.

## 1. Value & Non-Goals
VALUE|Eliminate 100% of Nov 23-24 runtime crash loops (`OmniAgent` Android 17 `dataSync` FGS ban), `GC=3749MB` Studio export LMK OOM kills, `.iamf` roundtrip decode failures (`obu type 1`), `PullAllTriad` rebase breaks, & Pro-Audio route/dropout stalls across Cavernize `v2.9.66`, OmniAgent `v2.1.7`, & Termux `v4.1.1`.
WARDLEY|Commodity=Android `JobScheduler` `UserInitiated` + `mediaProcessing` FGS + `SO_KEEPALIVE` + GitHub ETag/web-redirect; Differentiator=Private fixed `<=262KB` streaming export sink, `AudioRouteFsm` w/ `routeEpoch` CAS, AOMedia IAMF v1.0.0 bounded OBU roundtrip, & SPSC ONNX worker decoupling.
NON_GOAL|Zero blind `dataSync`->`remoteMessaging`/`connectedDevice` Play-policy hacks w/o live peer or unattended `git rebase` over dirty 22-file repos.
NON_GOAL|Zero breaking changes to W46-W51 DSP invariants, keystore signing, or `targetSdk >= 35` SELinux W^X compliance.

## 2. Live SOTA & Blind Morphogenesis
SOTA_SOURCES|Android 15-17 FGS quotas (`dataSync` & `mediaProcessing` `6h/24h` caps, `OnTimeout` mandatory `StopSelf()`, `UserInitiated` `JobScheduler` + bound AIDL `BIND_AUTO_CREATE` exempting BAL); AOMedia IAMF v1.0.0 bitstream spec (`ambisonics_config` starts w/ `uleb128 ambisonics_mode` before `output_channel_count`/`substream_count`, full `ParamDefinition` skip inside `obu_size` slice); `.agents/docs/SAVED_IDEAS.md:52-55`, `cavernize_log.txt`, `OmniAgent` `get_logs`, `.omni_agent_debian.log`.
TRIZ_CONTRADICTION|Unattended bg connectivity/exports & multi-repo sync vs Android 17 `dataSync`/`mediaProcessing` `6h` caps, `3.7GB` LMK OOM, HAL callbacks, & `cgroup_freezer` split-brain -> Local-Quality + Asymmetry + Pre-Action (`JobScheduler`/`mediaProcessing` w/ `4-5.5h` checkpoints, private `<=262KB` chunk pool, `routeEpoch` CAS async HAL worker, `ff-only` clean-tree pull + `X-Bridge-Proto-Ver`).
CANDIDATE|A|Localized Point-Patches Per Error|Replace `dataSync`->`SpecialUse` w/ `try/catch`; pass `origin <branch>` to `git pull --rebase`; rent `ArrayPool.Shared` + `GC.Collect`; reorder `IamfEnvironmentWriter:172`; clear `InId=705` on `PhoneMic` + `-11` retry.
REJECTED_BECAUSE|A|`ArrayPool.Shared` `>85KB` hits LOH (`>1M` floats unpooled), `OnDestroy` `activeCts` still kills exports, `dataSync`->`SpecialUse` risks Play rejection, unattended `rebase` clobbers dirty trees, & sync HAL teardown inside AAudio callback deadlocks `mediaserver`.
CANDIDATE|B|Big-Bang Cross-Repo Rewrite|Simultaneously rewrite all 3 repos into micro-daemons w/ `remoteMessaging` FGS, unconditional `ArrayPool` `16384f` chunks, `git fetch && rebase`, & shared `/sdcard` rate-limit lockfile.
REJECTED_BECAUSE|B|`remoteMessaging` fails Play IM audit & `connectedDevice` throws `SecurityException` w/o active BT/USB/CDM peer; `/sdcard` lockfile hits Android MediaStore UID `EACCES` across reinstalls; big-bang rewrite maximizes regression blast radius.
WINNER|Wave-Gated Systemic Lifecycle & Spec Remediation (3 bounded waves, W46-W51 regression-guarded): **Wave 1 (P0 Crash & OOM)**: replace OmniAgent `dataSync` FGS w/ `JobScheduler` `UserInitiated` + bound AIDL Service (`connectedDevice` ONLY during active USB/CDM, `FOREGROUND_SERVICE_START_NOT_ALLOWED` guard); hoist Cavernize export into `ExportMediaProcessingService` (`mediaProcessing` FGS, Service-owned `exportCts`, `OnTaskRemoved` drain, `5.5h` checkpoint, suppress false cancel dialog) + private fixed `<=262KB` chunk pool (`<=192MB` total RSS vs `3749MB` OOM); **Wave 2 (Spec, Git & Net)**: enforce AOMedia IAMF v1.0.0 (`uleb128 ambisonics_mode` first + trimming/ext-header `Slice(obu_size)` + full `ParamDefinition` skip + `AudioFile.cs` `.iamf`/`.amb` sniff decoupling B-format `.amb`), separate native FLAC from `LgsrMasterNeedsTwin` via capability matrix (keeping SR/ch assert), ban unattended `rebase` in `omni_agent_daemon.py` (clean-tree + `flock` + `ff-only` else `wip/` quarantine), & centralize `ETag`/`If-None-Match` (`304`) + `releases/latest` redirect cache; **Wave 3 (ProAudio & Cold IPC)**: single-coordinator `AudioRouteFsm` (`routeEpoch` CAS cancelling stale `300ms` timers, clearing `InId` on `onAudioDevicesRemoved` / `PhoneMic`, offloading HAL close off callback, Path A `-11` -> Path B fallback, idempotent `AppStartupCoordinator`), lock-free SPSC ONNX worker w/ ARM64 acquire/release + PDC crossfade (`WritePlayback` never `0`), Scarlett Gen4 FCP `USB_DETACHED` abort FSM, & `OmniBridgeIpcReceiver` `goAsync()` + eager `FilesDir` ring logger + `X-Bridge-Proto-Ver` OTA-drain gate.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|Blind `remoteMessaging`/`connectedDevice` FGS fails Play policy or throws `SecurityException` on boot; `OnDestroy`-immune `mediaProcessing` hits `6h` `OnTimeout` killing zombie exports; `AudioRouteFsm` sync close inside AAudio callback deadlocks `mediaserver`; daemon `git rebase` on dirty `22`-file workspace drops edits; `App Freezer` + `MediaStore` UID lock breaks `/sdcard` `cavernize_log.txt` across reinstalls.
THREAT|HAL USB Yank Race + Cold IPC ANR + Silent OTA Split-Brain: Scarlett yank mid-Path-A(`-11`) or mid-FCP races `300ms` debounce & clobbers `InId`; cold `OmniBridgeIpcReceiver` broadcast hits BAL/10s ANR before `AppStartupCoordinator` -> DEF: monotonic `routeEpoch` CAS + async HAL close worker + `USB_DETACHED` FCP abort, `goAsync()` w/ `4s` `StartupGate` + bound AIDL Service (`BIND_AUTO_CREATE`), `X-Bridge-Proto-Ver` + `FileProvider`/AIDL log pipe + BusyExport/UsbFcp OTA-drain gate.
KILL|R1|FGS_Play_Timeout|`fgs_start_not_allowed_cnt >= 1 OR fgs_on_timeout_crash >= 1 OR play_fgs_policy_warn == 1`|7d S25U Android 17 soak + Play pre-launch|Pin `JobScheduler` `UserInitiated` + foreground-bound AIDL + `4h` checkpointed export segments.
KILL|R2|HAL_USB_DSP_Integrity|`hal_close_p99_ms > 150 OR route_epoch_stale_switch >= 1 OR spsc_xrun_or_nan_cnt >= 1 OR export_rss_mb > 220`|50x Scarlett Gen4 yank/replug + 60m `16ch` export|Bypass async SPSC ONNX & Path B debounce -> pin sync dry-bypass w/ PDC mute + `<=16MB` ring.
KILL|R3|Git_RateLimit_IAMF|`daemon_rebase_clobber >= 1 OR git_index_lock_err >= 1 OR github_403_429_pct > 1.0% OR iamf_roundtrip_fail > 0`|14d 3-repo sync + force-push chaos + IAMF matrix|Lock daemon git to read-only fetch/status (`ff-only` manual `ship`) + single-owner ETag cache + rollback IAMF `ipcm` spec vector.

## 4. Invariants, Budgets & QAS
BUDGET|export_rss<=192MB|hal_close_p99<=150ms|fgs_crash=0|github_403_rate<=1.0%|iamf_roundtrip_err=0|ipc_cold_log_ms<=500ms
PLANES|offline=Wave2_IAMF_roundtrip+daemon_ff_only_sync|rt=Wave3_AudioRouteFsm_SPSC_ONNX_0alloc(<1ms)|async=Wave1_UserInitiated_Job+mediaProcessing_FGS_5.5h_ckpt+ETag_governor
QAS|Q1|60m Studio export on S25U w/ screen off + Activity `OnDestroy`|Cavernize `ExportMediaProcessingService` + private `262KB` pool|Export completes w/ zero `Export cancelled.` popup & `lmkd` survival|<=192MB peak RSS & 0 OOM kills & 0 false popups|python3 -B .agents/scripts/run_w46_unit_tests.py --filter StudioExportLifecycle
QAS|Q2|Export `.iamf` (7.1.4 & 16ch HOA) then immediately re-open in Cavernize & `ffprobe`|`IamfEnvironmentWriter` + `IamfDecoder` + `AudioFile`|`libffmpeg` v8.1.3 & Cavern native reader decode `obu type 1` cleanly w/ 0 fallback errors|0% OBU decode errs & 0ms fallback failure|python3 -B .agents/scripts/run_audio_ground_truth.py --suite iamf-roundtrip
QAS|Q3|OmniAgent 24h S25U soak + 10x `PullAllTriad` + 50x Scarlett Gen4 yank/PhoneMic toggle|OmniAgent `v2.1.7+` + Termux `v4.1.1+` + Cavernize `v2.9.66+`|0 `dataSync` FGS crashes, 0 `Cannot rebase` errors, 0 sticky `InId=705`, `<=1%` GH 403, non-empty cold `log_tail`|0 fatal crashes & 0 `WritePlayback=0` stalls & <=150ms HAL close|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.95*10|Verify OmniAgent `JobScheduler UserInitiated` + bound AIDL Service survival over 12h S25U Android 17 standby vs `dataSync` 6h ban|0 `ForegroundServiceStartNotAllowedException` & 100% IPC ping responsiveness after 12h
SPIKE|S2|VoI=0.90*9|Hex-diff `IamfEnvironmentWriter` output against AOMedia IAMF v1.0.0 & `ffprobe` (`uleb128 ambisonics_mode` first + `ParamDefinition` skip)|0 `Failed to read obu type 1` errors across `7.1.4` & `16ch HOA`

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|OmniAgent `dataSync`->`UserInitiated`/AIDL + Daemon `ff-only` & `ECONNRESET` Backoff + ETag Governor|FGS crash & Triad pull & `Errno 104`->0 FGS exceptions + `ff-only` + `304` ETag cache (<=20s verify)|python3 -B .agents/scripts/omni_agent.py status && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --fast
SLICE|W2|Cavernize `ExportMediaProcessingService` + `262KB` Streaming Pool + Cancel/FLAC Gate + IAMF v1.0.0 Fix|`StudioBatchExporter`/`IamfWriter`/`AudioFile`->`<=192MB` RSS + valid IAMF roundtrip + native FLAC export|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings
SLICE|W3|`AudioRouteFsm` (`routeEpoch` CAS) + SPSC ONNX Worker + Scarlett FCP FSM + Cold `log_tail` Fix|ProAudio callbacks & IPC cold-start->0 sticky `InId=705`, 0 `WritePlayback=0` stalls, non-empty `log_tail`|python3 -B .agents/scripts/run_audio_ground_truth.py --suite proaudio-route-fsm

### H2+ Outcome Stubs
STUB|W4|Cross-app `X-Bridge-Proto-Ver` handshake, `FileProvider` log pipe, & BusyExport/UsbFcp OTA-drain gate|0 MediaStore EACCES & 0 mid-export OTA kills
STUB|W5|7-day S25U multi-session telemetry audit & Play pre-launch FGS compliance sign-off|0 fatal markers across 3 repos

## 7. Fitness Functions & Evolution
FITNESS|`python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings && python3 -B .agents/scripts/run_audio_ground_truth.py --suite iamf-roundtrip`
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
