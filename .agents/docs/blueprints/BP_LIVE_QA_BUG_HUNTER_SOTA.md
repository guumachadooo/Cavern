# Live Adversarial QA, Stateful UI/DSP Bug-Hunter & Hot-Exec Verification Loop (`BP_LIVE_QA_BUG_HUNTER_SOTA`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_LIVE_QA_BUG_HUNTER_SOTA|domain_vec=dsp-hw:1.00,infra-ops:0.33,security:0.33,ux-product:0.33|door=1-way|tier=T3|status=SOTA-v1|h1_wave=W1
PARENT_ADR|none
- `dsp-hw`: Split Offline / RT 0-alloc 0-lock hot-path (<1ms/block) / Async control; fuse owned signals/hw; prove math in offline golden bench first.
- `infra-ops`: Design for unattended recovery, idempotent deploy/rollback, OOM/quota ceilings, zero-blind-retry backoff & structured observability.
- `security`: Apply STRIDE across trust boundaries, fail-closed auth/crypto, least privilege, secret redaction & attacker cost-asymmetry.

## 1. Value & Non-Goals
VALUE|Upgrade Cavernize live device QA (`cavern_bug_hunter.py` + `LiveChaosBugHunter.cs` + `LiveHotPatchHost.cs` + `omni_agent_client.py`) into SOTA stateful UI/DSP adversarial fuzzer + FrameMetrics/freeze watchdog + spatial trajectory oracle + zero-APK-rebuild `hot_exec` closed-loop verifier (`>=95%` regression catch rate, `0` historical-noise conflation, `<=2.5s` in-RAM probe cycle, `100%` automatic Linux/PRoot wakeup).
WARDLEY|Commodity=Android `FrameMetricsAggregator`/`StrictMode`/`ApplicationExitInfo`/AAudio `getXRunCount`, Python stdlib `unittest`, HMAC-SHA256 collectible `AssemblyLoadContext`; Differentiator=Cavern-specific stateful UI/Library/Play-AI latency oracle, 3D `SpatialChoreoTrack` ping-pong/radius-collapse trajectory oracle, zero-APK-rebuild `hot_exec` verification loop, & dual-channel Android-wakes-Linux (`launch_termux_daemon`) orchestration.
NON_GOAL|Zero rooted-device or invasive Magisk/Frida instrumentation dependencies (must operate 100% stock non-root S25U Android 17 w/ W^X compliance).
NON_GOAL|Zero destructive user-library/settings mutations left un-restored (every fuzz burst MUST verify post-restore exact baseline equality).
NON_GOAL|Zero external pip/npm cloud farm dependencies (100% zero-dep stdlib Python + existing Cloudflare Hub v6 DO + OmniAgent same-signature IPC).

## 2. Live SOTA & Blind Morphogenesis
SOTA_SOURCES|ByteDance `Fastbot2` (model-based directed-graph stateful GUI fuzzing, 12+ actions/s, Android 12–17), `APE` / `AHA-Fuzz` (view-hierarchy accessibility fuzzing), AndroidX `FrameMetricsAggregator` (`EVERY_DURATION` / `HALTED_INDEX` frozen-frame jank >16.6ms/8.3ms), `StrictMode` + `ApplicationExitInfo` (`REASON_ANR`) + `SIGQUIT` stall watchdogs, AAudio `AAudioStream_getXRunCount` + differential PCM discontinuity/SNR oracle + CTS Audio Workload ramping; audited codebase: `.agents/agent/cavern_bug_hunter.py` (V1–V8), `CavernSamples/CavernizeAndroid/Services/{LiveChaosBugHunter,LiveDiagnosticBridgeService.Commands,LiveHotPatchHost}.cs`, `.agents/agent/omni_agent_client.py`.
TRIZ_CONTRADICTION|Deep adversarial UI+DSP+Spatial coverage on live phone vs zero-false-positive speed (`<=45s` standard hunt) & zero APK rebuild wait (`6m` CI) -> Dimension-separation (split `origin=live_triggered|current_session|historical_archive` eliminating old-APK noise) + Time-separation (compile C# fixes/oracles locally w/ Roslyn -> run purely in Cavernize RAM via `hot_exec` in `<=2.5s` BEFORE committing to 6m APK CI).
CANDIDATE|A|External ADB-Only Monkey + Offline Logcat Scraper|Drive UI via Termux Wireless ADB `monkey`/`uiautomator dump` + scrape `logcat -d` externally without in-process hooks or `hot_exec`.
REJECTED_BECAUSE|A|Blind `monkey` misses deep business-logic states (Play AI 7.1->UpMix stall, Library Albums search truncation, SpatialChoreoMode trajectories), breaks whenever Wireless ADB port rotates or Termux sleeps (`>40%` connection flake), and cannot inspect internal DSP/LUT/managed-heap state (`0%` visibility into `SpatialChoreoTrack` ping-pong).
CANDIDATE|B|Pure In-App Heavy Continuous Telemetry & Always-On StrictMode Death|Embed continuous `FrameMetrics` + `StrictMode.penaltyDeath()` + heavy fuzzing loop permanently inside production `MainActivity` without external orchestrator.
REJECTED_BECAUSE|B|`StrictMode.penaltyDeath()` crashes real user playback on benign OEM binder/disk ticks, adds `>=3%` CPU/GC overhead on 120Hz scrolling, and lacks cross-process recovery or `hot_exec` pre-APK patch verification when a bug freezes the UI thread.
WINNER|Hybrid Dual-Plane SOTA QA Loop (`cavern_bug_hunter.py` v2 + `LiveHotPatchHost` `hot_exec` + `omni_agent_client.py` auto-wake): **Plane A (Python Orchestrator v2)** stratifies findings by `origin` (`live_triggered` vs `current_session` vs `historical_archive`), distinguishes expected boundary-fuzz rejections from bugs, asserts 100% baseline settings restoration diff, enforces per-action UI latency/freeze watchdogs (`>400ms` WARN, `>1500ms` HIGH) on real Play/Library/Search journeys (`V9`), runs `SpatialChoreoTrack` trajectory oscillation/radius-collapse + `hot_exec` RAM probes (`V10`), & auto-wakes sleeping Linux/PRoot via `channel=android` `launch_termux_daemon`; **Plane B (In-App On-Demand Hooks, H2)** adds opt-in `FrameMetricsAggregator` per-journey jank histogram, `ApplicationExitInfo` ANR/LMK forensics, & AAudio `getXRunCount` delta snapshot inside `LiveChaosBugHunter.cs` (`0` overhead when idle).

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|Auto-wake loop (`launch_termux_daemon`) triggers recursive wakeup storms when Termux `RUN_COMMAND` permission is revoked -> drains S25U battery `>8%/hr`, OR `V9` interactive Play/Library fuzzing leaves playback muted / settings dirty after mid-hunt timeout, OR `V10` `hot_exec` probe triggers a UI-thread deadlock inside `RunOnUiAsync` locking out the bridge for 45s.
THREAT|Untrusted Probe Injection & Replay on `hot_exec` / `wake-linux`: rogue client replays captured `hot_exec` DLL or spams `launch_termux_daemon` -> DEF: `LiveHotPatchHost` enforces constant-time HMAC-SHA256 (`hot-exec-v1|reqId|ts|sha256`) + `<=180s` clock skew + `<=512KB` cap + collectible `AssemblyLoadContext.Unload()`; `wake-linux` enforces single-shot bounded retry (`max_attempts=1`, `<=15s` poll budget) + Keystore-authenticated `android` channel; `V9/V10` wrap every mutation in guaranteed `try/finally` restoration + post-restore diff check.
KILL|K1|FalsePositive_Or_NoiseFlood|`historical_conflation_count > 0 OR boundary_reject_fp_count > 0`|10 consecutive hunts across `v2.9.70+` on S25U|Revert V1/V3 parser regex & force strict `sessionId` log partitioning.
KILL|K2|StateLeak_Or_WakeStorm|`restore_diff_keys > 0 OR wake_retries_per_cmd > 1 OR hunt_elapsed_sec > 75`|5 consecutive `--mode standard` hunts w/ simulated mid-hunt timeout|Force `--no-mutate` safe mode & disable auto-wake retry pending manual `wake-linux`.
KILL|K3|HotExec_RamLeak_Or_Deadlock|`hot_exec_unloaded == false OR hot_exec_elapsed_ms > 15000 OR managed_gc_delta_mb > 35`|20 sequential `hot_exec` probes on live S25U|Disable V10 `hot_exec` sub-probe & fall back to built-in `reflect_invoke` read-only telemetry.

## 4. Invariants, Budgets & QAS
BUDGET|hunt_standard<=45s|hot_exec_cycle<=2500ms|ui_stall_warn=400ms|ui_freeze_high=1500ms|wake_linux_wait<=15s|restore_diff_keys=0|unit_test_ms<=1500ms
PLANES|offline=Roslyn_hot_probe_compiler+test_bug_hunter_unit_suite|rt=Cavernize_LiveHotPatchHost(collectible_ALC)+AAudio_xrun_counters(0_idle_overhead)|async=cavern_bug_hunter_v2(V1..V10)+omni_agent_wake_linux
QAS|Q1|V1 ingests mixed historical + current-session AppLogger lines|Offline unit test + live S25U `v2.9.70`|Ledger tags each finding `origin=live_triggered/current_session/historical_archive` with zero mixing|<=5ms parse time & 0% historical conflation|python3 -B .agents/agent/test_bug_hunter.py
QAS|Q2|Boundary fuzzing sends invalid settings (`MagLsOrder=-1`, `BiometricHeadWidthCm=0`)|V3 settings chaos burst|Valid rejection is recorded as PASS (not false-positive bug); only crashes/NaN/unclamped acceptance trigger findings & post-restore diff equals 0|0% false-positive rate & 0ms state leak|python3 -B .agents/agent/test_bug_hunter.py
QAS|Q3|Sleeping `termux` channel while `android` channel is online|`omni_agent.py wake-linux` or auto-wake on `termux` command|Client dispatches `launch_termux_daemon` over `android` channel once (max 1 retry) and polls `<=15s` until `termux` heartbeats online|<=15s wakeup & <=1 retry per command|python3 -B .agents/agent/test_omni_agent.py
QAS|Q4|Dynamic spatial choreography sampled across `Subtle/Musical/Cinematic/Orbit`|V10 trajectory oracle + live `hot_exec` probe on S25U|Oracle detects sign-reversal ping-pong & radial collapse toward center (`r_min/r_max`) in `<2500ms` without APK rebuild|<=2500ms probe latency & 100% ping-pong detection|python3 -B .agents/agent/test_bug_hunter.py

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.90*9|Execute live `hot_exec` RAM probe on S25U `v2.9.70` inspecting `SpatialObjectCortex` trajectories & `MediaLibraryService` album search|Probe compiles, authenticates HMAC, runs `<2500ms`, returns JSON & `unloaded=true` else fall back to `reflect_invoke`
SPIKE|S2|VoI=0.80*8|Run upgraded `cavern_bug_hunter.py` v2 (`V1–V10`) live on S25U `v2.9.70` & verify active vs historical finding stratification + 0 restore diff|`restoredState=true`, `restoreDiffKeys=0`, & clear active/historical report saved

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|Auto-Wake Linux (`omni_agent_client.py` `wake-linux` + single-shot auto-wake retry on offline `termux`) + permanent `AGENTS.md`/`EXTERNAL_AI_ONBOARDING.md`/`SKILL.md` rules for `wake-linux` & `hot-exec`|`.agents/agent/omni_agent_client.py` + docs -> verified auto-wake CLI & unit tests|python3 -B .agents/agent/test_omni_agent.py
SLICE|W2|Bug-Hunter SOTA v2 (`cavern_bug_hunter.py`): `origin` stratification (`live_triggered`/`current_session`/`historical_archive`), boundary-fuzz FP fix, exact post-restore baseline diff, `V9` UI-Freeze Latency & Library/Play Stateful Oracle, & `V10` Spatial Choreography Trajectory + `hot_exec` Oracle|`.agents/agent/cavern_bug_hunter.py` + `.agents/agent/test_bug_hunter.py` -> full V1–V10 unit + live S25U hunt|python3 -B .agents/agent/test_bug_hunter.py
SLICE|W3|Live S25U `v2.9.70` Wave-0 Spike (`hot_exec` RAM verification + full SOTA v2 Bug-Hunter execution + categorized Active vs Historical Report)|Live S25U `v2.9.70` -> `.agents/docs/LIVE_BUG_HUNT_REPORT.md` feeding Stage 2 remediation|python3 -B .agents/agent/cavern_bug_hunter.py --mode standard --out-md .agents/docs/LIVE_BUG_HUNT_REPORT.md

### H2+ Outcome Stubs
STUB|W4|On-device frame-jank histogram, process-exit ANR/LMK forensics & native audio XRun delta snapshots (zero idle overhead)|<=8.3ms P95 frame telemetry & 0 missed ANR/XRun events
STUB|W5|Directed UI state-graph explorer covering all tabs & modals automatically w/ reward-weighted unseen-widget coverage|>=90% widget-state coverage w/ 0 stuck dialogs

## 7. Fitness Functions & Evolution
FITNESS|`python3 -B .agents/agent/test_bug_hunter.py && python3 -B .agents/agent/test_omni_agent.py && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py blueprint lint .agents/docs/blueprints/BP_LIVE_QA_BUG_HUNTER_SOTA.md --strict`
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
