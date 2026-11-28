# Central Plans & Blueprints Hub (`PLANS_HUB.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols/rules).
> PURPOSE|single dynamic source of truth for Cavern/CavernizeAndroid active/pending work, physical S25U validation queue, resolved plan conflicts, anti-regression invariants, & machine-synced blueprint/monolith inventory.

## 1. Active Governance Policies (Permanent)
- **SOTA Blueprint-First Default (`blueprint.require_by_default=true`, `v2.19.0` Tiered Grammar):** Run `plan_tool.py blueprint match --prompt "<goal>"` FIRST (amend active BP before creating new); otherwise author right-sized format (`mini` `<=35L/4KB` `0` subagents for `T1/2-way` | `compact` `<=65L/10KB` `<=1 flash` for `T2` | `full` `<=350L/24KB` for `T3/1-way`) passing `>=85/100 --strict` (`Anti-Gaming Linter v2`). Transition lifecycle via `blueprint transition` & keep repo file budget `O(1)` via `blueprint compact --apply`.
  - **Explicit Opt-Out & T0 Exemption:** Skipped ONLY when user explicitly opts out to save tokens (`"sem blueprint"`, `"não faz blueprint"`, `"economizar token"`, `"skip blueprint"`, `"no blueprint"`) OR `Tier 0` Q&A / 1-line typo/UI copy/label tiny-fix.
- **Mandatory Pre-Resumption Plan Hygiene Gate (`blueprint.require_pre_resume_hygiene=true`):** Before resuming ANY paused/older roadmap, wave, or blueprint after intervening work:
  1. Run `python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py blueprint hygiene --strict`.
  2. Read `PLANS_HUB.md` (`Sec 2` pending queue + `Sec 3` conflict/anti-regression registry) & target roadmap/blueprint.
  3. Reconcile stale statuses, superseded directives, duplicated specs, & hardware/RAM invariants against live codebase; record resolutions (`CONFLICT_RESOLVED|` / `HYGIENE_NOTE|`), then run `plan_tool.py blueprint hub --sync`.
- **Anti-Regression & Live S25U Discipline:** Never regress verified `v2.9.80–v2.9.82` fixes (`EnableFullScreenRenderEffectBlur=false`, Scarlett dual-watermark RT `<10 ms` duplex & bit-exact bypass, low-RAM/`:worker` ONNX isolation, `W51.1–W51.6` vocal/cache/loudness/volume guards). Verify C# probes in RAM via `omni_agent.py hot-exec` (`<3s`) before 6-min APK builds.

## 2. Prioritized Pending Work Queue (Structural Track A FIRST -> HW Track B -> App Reform Track C LAST)

### TRACK A | P0 — Structural, Coherence & Bug-Hunting Foundations (W56.1-W56.5 & W58-W60 ALL DONE -> Track B NEXT)
1. **`W56.1–W56.3` (`[DONE | DFP v2.21.1–v2.22.5]` — `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE.md`):** `PLANNER_REEXEC=1`, Triad `(semver,tree_sha256)` parity, `shlex` cmd guard, `<1,500L` engine split, machine-generated `COHERENCE_HUB.md` + `domains.json` + `@verify_check`, & Reflexive Meta-Governance (`T0/T1/T2` + `T0` canary + Living `CODE_ANCHORS` + Plane-2 `pro [L3]` `<=4/wk`).
2. **`W56.4` (`[DONE]` — `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE.md`):** App Zero-Static Foundation (`co_evolve.py` `~40ms`, `0 B` RT GC): `BridgeCmd` `54/54` explicit opcodes + `Packaging` SSOT (`federation.json`), pure `MemoryBudgetGovernor` (`<=2600 MB` `REG-001`), generated `AppSettingsStore.Schema.cs` (`213` descriptors, `206` zero-reflection resets), & `RPE<->SBE` DSP parity ledger.
3. **`W55.1b` & `W56.5` (`[DONE | DFP v2.23.0–v2.24.0]`):** Zero-Accumulation Monolith Ratchet (`+1L` on `>2,000L` = `FAIL`), 5-domain Compounding Learner (`BL|UI|ARCH|DSP|AI`), & Token-Tax Auto-Governor (`token_governor.py`, `SessionStart` diet, `PreInv` session dedup, doc token ratchet, `token-audit`, 1-shot `closeout`). *(H2+ stubs `W56.6–W56.8` integrate w/ `W55.3–W55.6`.)*
4. **`W58` (`[DONE | DFP v2.25.0]` — `BP_HORIZONTAL_FINE_GRAIN_ORCHESTRATION.md`):** Horizontal Fine-Grain Subagent Orchestration — flat zero-coord `run_workflow` cubicles (`wave {schedule|fanout|stage1-3}`), `_WorkflowAstAuditor` (`model=`/`shell=False`/Termux `<=2`/multi-hop heavy), `is_termux_proot`, dead-PID reaper, HMAC `probes_sig` + sibling rollback, `domains.json` fix, & `8/8` `T0` canary (`116/116 --fast`).
5. **`W59` (`[DONE]` | `BP_QA_V3_CHAOS_HEADROOM_AND_SCARLETT_DELTA.md` `DONE [100/100]`):** Bug Hunter v3 Chaos Simulator (`chaos_user_simulator.py`) — deterministic `--seed`, fixed demo tracks (`1 FLAC + 1 Atmos`), atomic `mutex_snapshot` (`55`), awaitable `OpenFileAsync`, && crash-safe `QaFailClosedHttpHandler` + `QaMvsepBlockUntilUtcMs` lease (`0` MVSEP credits).
6. **`W60` (`[DONE]` | `BP_QA_V3_CHAOS_HEADROOM_AND_SCARLETT_DELTA.md` `DONE [100/100]`):** 50% Headroom Gate (`headroom_gate.py`, 4-site RT CPU + C++ ABI `101` callback timer/`hold_capture_no_discard`, `ui.headroom_50pct` `totalFrames>=15`/`<=4.16ms`, `<=550/1280MB` PSS) && Scarlett Gen4 delta (`scarlett_loopback_delta.py` `--compare-cables`, `0.005 dB` error).

### TRACK B | P0-HW — Stage 1 Bug-Fix, 50% Headroom & S25U Live Validation (`[NEXT #0 — ALL TRACK A DONE]`)
1. `W54-LiveVerify` (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP` + `BP_V4_REFORM_SPATIAL_AI_ONEUI9`): Install signed Epoch 5 CI APK (`W55.6`+`W57.4`+`v2.9.102 B1-B7`) + OmniAgent `v2.2.0` on S25U & run full `V1–V12 --strict-mode --gate` (`36` contracts; NPU/WebGPU/CPU benchmark).
2. `W52` (`BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md`): Live S25U check of vocal stability, neural stem cache TTL/hit-rate, 120 Hz zero-jank (`EnableFullScreenRenderEffectBlur=false`), EBU R128 loudness match, & smooth volume ramp.
3. `W2-LiveReverify` (`BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA.md` + `BP_LEGACY_CAVERN_CORE_AND_SUITE_PARITY` `W57.4`): Live Scarlett 2i2 Gen 4 USB-C duplex check (`<10 ms` RT mic duplex, 0 xruns, bit-exact headphone bypass, `QuickEQ` live sweep, hot-plug recovery).
4. `CavernBugHunter v2` (`omni_agent.py hunt`): Clean 0-finding confirmation pass on live device.
5. **Post-W58–W60 Execution Order (`SAVED_IDEAS.md` seeds -> SOTA Blueprint/stage; strict bug-first):** **Stage 1 (FIRST):** `v2.9.104` crash/stutter/blueprint-gap diag + Bug Hunter v3 Chaos + Scarlett delta -> fix ALL bugs & regressions to 0 findings & 50% Headroom PASS -> **Stage 2:** UX/HW Unification (MagLS/MinPhase/BRIR, Local+Cloud popover, smart BT/USB + XTC cm) -> **Stage 3:** Holistic `FLAC->Dolby Atmos` AI Cortex + Personal Serverless GPU MVSEP Graph.

### TRACK C | P1–P3 — CavernizeAndroid App Reform & Feature Waves (`W55.1–W55.6` & `W57.1–W57.4` ALL H1 DONE)
- **P1 | V4 Master App Reform (`BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`, `[DONE]` — Epoch 1–5):** `W55.1` (`V4.1` P0 fixes) -> `W55.2` (`V4.2` 3D motion/lyrics FX/ear-candy/off-UI) -> `W55.3` (`V4.3` `UiStateCoordinator`/XTC bypass/dedup) -> `W55.4` (`V4.4` 3-tab `Library|Sound|Export` + Mini-Player/NowPlaying + Gear, ALL 8 monoliths `<1,800L`) -> `W55.5` (`V4.5` `SecQSDetail` glass/gestures/drag) -> `W55.6` (`V4.6` Listening/Workspace Presets, JSON portability, SAF Remote Cache, audited MVSEP recipes, Hexagon V79 NPU + Adreno 830 WebGPU + ARM64 KleidiAI CPU Symbiosis).
- **P1 | Legacy Cavern SOTA Re-Engineering & Suite Parity (`BP_LEGACY_CAVERN_CORE_AND_SUITE_PARITY.md`, `[DONE]` — Epoch 1,2,4,5):** `W57.1` (ARM64 NEON SIMD, `double` TDF-II `BiquadFilter`, `CytomicSvfFilter`, LR4 `Crossover`, zero-alloc Kaiser-sinc `Source` + `DirectionalRenderer`, 0-latency Hilbert JOC) -> `W57.2` (`ListenerContext`, async `FFmpeg`, VBR E-AC-3 seek) -> `W57.3` (`FilterStudio` ZDF-SVF/Vicanek + `Export -> Tools`: `ADMValidator` BS.2076-2/`EnhancedAC3Merger`/`WAVChannelReorderer`/`--override-bed`) -> `W57.4` (`QuickEQ` Farina ESS, FDW/ERB, multi-point MMM, `<=+6 dB` boost cap, AVR/MiniDSP/CamillaDSP/EQ APO export).
- **P1-Completed Ref | V3 Roadmap (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md`, H1 DONE):** `W53.Q1–Q4` & `W53.1–W53.6` shipped; S25U live verification queued in Track B.
- **P2 | Phase 3 Vocal Cortex (`PHASE3_VOCAL_CORTEX_ROADMAP.md`, `W31–W42`):**
  1. `W31` (Next DSP wave): Wire ALREADY-VERIFIED C# 8.0 `Cavern/VocalChain/AcousticFeedbackShieldFoundation.cs` (`GT-13` `34.31 dB` ERLE, `+16.06 dB` GBF, `0 B` GC, `0.356 ms/blk`) into live Android mic duplex chain — strictly preserving `v2.9.80–v2.9.81` Scarlett dual-watermark RT invariants (`!re-port from Python`).
  2. `W32` (Vocal AI / Neural enhancement): Enforce `:worker` process isolation + `v2.9.82` low-RAM discipline (`NativeOnnxRuntimeBridge` memory guard, zero LMKD OOM on S25U).
  3. `W33–W42`: Subsequent Phase 3 waves — each gated by Pre-Resumption Hygiene + SOTA Blueprint + Track A Coherence checks.
- **P2-Subsumed | Codebase Modularization (`BP_FULL_APP_SOTA_AUDIT.md`, `W4` -> subsumed into `W55.4.4`):**
  - Split ALL `8` repo monoliths `>2,000L` (tracked live in `Sec 4` below) `<1,800L` and lock the Zero-Accumulation Ratchet to `0` globally without altering DSP math or public APIs.
- **P3 | Deferred / Optional Horizon (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` & `SAVED_IDEAS.md`):**
  - `Phase B W4–W5` (local INT4 LLM spatial director & conversational object choreography): **DEFERRED** until strict `:worker` isolation + S25U PSS ceiling (`<1.2 GB` delta) verified w/ zero LMKD kill risk.
  - Optional H2+ enhancement stubs in `.agents/docs/SAVED_IDEAS.md` (pull on demand via SOTA Blueprint).

## 3. Resolved Conflicts & Anti-Regression Registry
- **H1 | Superseded Full-Screen Blur Directive (`RESOLVED`):** `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md` per-frame full-screen `RenderEffect` blur **superseded** by `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md` (`W51.4` `EnableFullScreenRenderEffectBlur=false`, eliminated `14.78%` 120 Hz jank). **INVARIANT:** NEVER re-enable full-screen per-frame `RenderEffect` blur.
- **H2 | Local LLM / Neural RAM vs S25U LMKD OOM (`RESOLVED`):** `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` `W4` & `PHASE3_VOCAL_CORTEX_ROADMAP.md` `W32` risked `2.0–4.4 GB` PSS LMKD kills. **INVARIANT:** All heavy neural/LLM workloads MUST run under `:worker` process isolation + `v2.9.82` `NativeOnnxRuntimeBridge` low-RAM guard (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` `Phase B` remains `DEFERRED`).
- **H3 | Duplicate `W31` FDKF Port Spec & Scarlett RT Risk (`RESOLVED`):** `PHASE3_VOCAL_CORTEX_ROADMAP.md` `W31` previously specified porting FDKF from Python despite verified C# 8.0 `Cavern/VocalChain/AcousticFeedbackShieldFoundation.cs` already shipped & proven (`GT-13`). **INVARIANT:** Wire existing `AcousticFeedbackShieldFoundation.cs` directly; preserve `v2.9.80–v2.9.81` Scarlett dual-watermark `<10 ms` RT duplex & bit-exact headphone bypass.
- **H4 | Stale Blueprint Statuses & Undetected 5th Monolith (`RESOLVED`):** Completed blueprints previously lingered as `ACTIVE`/`DRAFT` & `AndroidAudioOutput.cs` (`>2,000L`) was missing from `W4` monolith tracking. **INVARIANT:** Machine-sync all blueprint statuses & `>2,000L` monoliths into `Sec 4` via `plan_tool.py blueprint hub --sync`, enforced by `verify_universal.py --all` & `blueprint hygiene --strict`.
- **H5 | Structural-Before-App Execution Order & Zero-Static Co-Evolution Gate (`RESOLVED`):** `W56.1 -> W56.4` (`BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE.md`: Ecosystem Coherence + Reflexive Self-Evolving Meta-Governance + Thin-Wedge App Zero-Static Foundation) precedes `W55.1b` (`BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`), and ALL Track A structural/governance waves precede CavernizeAndroid App Reform (`W55.2–W55.6`, Track C), eliminating `C1–C7` split-brains, one-way governance rot, & app static-desync traps (`Bridge`/`Packaging`/`MemoryBudget`/`SettingsReset`) before compounding learning or app overhaul. **INVARIANT:** Never execute new Track C app waves before Track A coherence & verification foundations are green.

## 4. Live Machine-Synced Inventory
<!-- HUB:AUTO_START -->
HUB_STATS|blueprints=11 (done=4, partial=6, active=0, deferred=1, superseded=0)|archived_done=12|min_score=100/100|open_conflicts=0|monoliths_gt2000=0

| Blueprint ID | Status | Score | Tier | H2+ Stubs | Hygiene / Lineage Note |
| :--- | :---: | :---: | :---: | :---: | :--- |
| `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE` | `PARTIAL` | `100/100` | `T3` | `3` | ALL H1 Track A slices (`W56.1->W56.5`, `DFP v2.24.0`) DONE & verified: Zero-Accumulation Token-Tax & Side-Effe |
| `BP_FULL_APP_SOTA_AUDIT` | `PARTIAL` | `100/100` | `T3` | `2` | W1-W3 DONE (T6); W4 monolith backlog updated to 5 files >2000L (added AndroidAudioOutput.cs 2157L); W5 speaker |
| `BP_HORIZONTAL_FINE_GRAIN_ORCHESTRATION` | `DONE` | `100/100` | `T3` | `2` | W58.1-W58.6 DONE (DFP v2.25.0): flat zero-token horizontal run_workflow cubicles, AST guard, Termux PPK cap, 8 |
| `BP_LEGACY_CAVERN_CORE_AND_SUITE_PARITY` | `DONE` | `100/100` | `T3` | `1` | ALL H1 slices (W57.1-W57.4) DONE & QA PASS (CRIT=0/HIGH=0); Track B S25U Scarlett sweep QA next |
| `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX` | `DEFERRED` | `100/100` | `T3` | `2` | resolved=Phase-A W1-W3 shipped in T6; Phase-B W4 local INT4 LLM deferred & MUST execute in isolated :worker p |
| `BP_QA_V3_CHAOS_HEADROOM_AND_SCARLETT_DELTA` | `DONE` | `100/100` | `T3` | `2` | ALL H1 slices (W59.1-W60.3) DONE & verified: 61/61 C# + 23/23 py + 8/8 C11 + 6/6 CI-parity 0e/0w + 40/40 QA co |
| `BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA` | `PARTIAL` | `100/100` | `T2` | `1` | W1 native/C# code DONE (v2.9.80); W2-LiveReverify physical Scarlett 2i2 Gen4 S25U test pending. |
| `BP_TRACKB_LIVE_QA_REMEDIATION` | `PARTIAL` | `100/100` | `T2` | `0` | Code + CI parity verified for v2.9.104; awaiting live S25U Track B V1-V12 sign-off |
| `BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP` | `PARTIAL` | `100/100` | `T3` | `1` | ALL H1 slices (`W53.0`, `W53.Q1–Q4`, `W53.1`, `W53.1b`, `W53.2`, `W53.3`, `W53.4`, `W53.5`, `W53.6`) DONE & CI |
| `BP_V4_REFORM_SPATIAL_AI_ONEUI9` | `DONE` | `100/100` | `T3` | `1` | ALL H1 slices (W55.1-W55.6) + v2.9.102 B1-B7 DONE & QA PASS (CRIT=0/HIGH=0); Track B S25U HW QA next |
| `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS` | `PARTIAL` | `100/100` | `T3` | `1` | W51.1-W51.7 code DONE (v2.9.81) + ONNX low-RAM fixes (v2.9.82); W52 live S25U verification pending. |

ARCHIVED_DONE|count=12|ledger=`.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md`|tarball=`.agents/docs/archive/blueprints_done.tar.gz`|ids=`BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP`, `BP_BLUEPRINT_ARCHITECTURE_V2`, `BP_CORE_RUNTIME_STABILITY_AND_SCHEDULING`, `BP_DEVICE_LOG_REMEDIATION`, `BP_FRACTAL_PARALLEL_WAVE_ORCHESTRATION`, `BP_INSTANT_ZERO_WASTE_RESUME`, `BP_LIVE_QA_BUG_HUNTER_SOTA`, `BP_ONEUI9_UX_MOTION_DISCOVERY_AND_LYRICS`, `BP_PLAN_GOVERNANCE_AND_CENTRAL_HUB`, `BP_STUDIO_EXPORT_METADATA_AND_DYNAMIC_ATMOS`, `BP_STUDIO_SPATIAL_EXPORT_COMPRESSION`, `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY`
COHERENCE_HUB|.agents/docs/COHERENCE_HUB.md
<!-- HUB:AUTO_END -->

## 5. Completed Roadmaps & Archive Index
- **Completed Roadmaps & Reports (Cold-Archived in `.agents/docs/archive/completed_roadmaps_and_reports.tar.gz`):**
  - `MASTER_AUDIT_PLAN_W46_W51.md` (W46–W51 full audit, DSP/ProAudio/UI/stability fixes)
  - `MASTER_OVERHAUL_PLAN.md` (Core overhaul stages T1–T6)
  - `PLUGIN_OPTIMIZATION_PLAN.md` (`dynamic-fractal-planner` optimization waves)
  - `LIVE_BUG_HUNT_REPORT.md` (S25U `v2.9.70` bug hunt report)
  - `archive/PHASE2_ROADMAP.md` & `archive/SPATIAL_ROADMAP.md` (`W01–W29` Phase 1/2 roadmaps)
- **Active Roadmaps:**
  - `.agents/docs/PHASE3_VOCAL_CORTEX_ROADMAP.md` (`W31–W42`, hygienized w/ H2+H3 guards)
- **Historical Archives (`<=120L` bounded slices only):**
  - `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`
  - `.agents/docs/archive/EXTERNAL_AI_SYNC_ARCHIVE.md`
  - `.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md` (index for verbatim `DONE` archive `.agents/docs/archive/blueprints_done.tar.gz`)
