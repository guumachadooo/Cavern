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

## 2. Prioritized Pending Work Queue
- **P0 | Physical S25U Live Validation (`v2.9.83+` & OmniAgent `v2.2.0` APKs):**
  1. `W53.0-Verify` (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md`): Install updated OmniAgent `v2.2.0` + Cavernize APKs to lock in 24/7 `remoteMessaging` FGS + direct `channel="cavernize"` + WebGPU/RoFormer crash recovery + smooth 120 Hz volume + decoupled 3D Room bar + MVSEP cancel-safe resume.
  2. `W52` (`BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md`): Live S25U check of vocal stability, neural stem cache TTL/hit-rate, 120 Hz zero-jank (`EnableFullScreenRenderEffectBlur=false`), EBU R128 loudness match, & smooth volume ramp.
  3. `W2-LiveReverify` (`BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA.md`): Live Scarlett 2i2 Gen 4 USB-C duplex check (`<10 ms` RT mic duplex, 0 xruns, bit-exact headphone bypass, hot-plug recovery).
  4. `CavernBugHunter v2` (`omni_agent.py hunt`): Clean 0-finding confirmation pass on live device.
- **P1 | V3 AI Separation, Spatial Motion, Modular One UI 9, Mobile ONNX & Transaural Roadmap (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md`, `W53.1–W53.6`):**
  1. `W53.1` (Wave A): MVSEP low-band vocal-rumble fix + FLAC/Atmos GCC-PHAT phase-coherent separate-reference karaoke + MVSEP custom API Single-Stems -> Lead/Backing-on-vocals-only cascade.
  2. `W53.2` (Wave B): Unified Play/Sing context-aware AI extraction sheet + incremental vocal-stem reuse + multi-variant non-destructive cache + lossless storage optimization.
  3. `W53.3` (Wave C): Stereo-pan azimuth preservation for fixed 3D objects + expressive lyrics/energy-aware 3D choreography.
  4. `W53.4` (Wave D): One UI 9 modular editable quick-panel control blocks.
  5. `W53.5` (Wave E): Mobile ONNX QNN HTP Hexagon NPU + FP16/W8A16 quantization + SoC-keyed context binary caching.
  6. `W53.6` (Wave F): Phone built-in & external speaker transaural XTC spatializer w/ bit-exact headphone auto-bypass.
- **P1 | Phase 3 Vocal Cortex (`PHASE3_VOCAL_CORTEX_ROADMAP.md`, `W31–W42`):**
  1. `W31` (Next DSP wave): Wire ALREADY-VERIFIED C# 8.0 `Cavern/VocalChain/AcousticFeedbackShieldFoundation.cs` (`GT-13` `34.31 dB` ERLE, `+16.06 dB` GBF, `0 B` GC, `0.356 ms/blk`) into live Android mic duplex chain — strictly preserving `v2.9.80–v2.9.81` Scarlett dual-watermark RT invariants (`!re-port from Python`).
  2. `W32` (Vocal AI / Neural enhancement): Enforce `:worker` process isolation + `v2.9.82` low-RAM discipline (`NativeOnnxRuntimeBridge` memory guard, zero LMKD OOM on S25U).
  3. `W33–W42`: Subsequent Phase 3 waves — each gated by Pre-Resumption Hygiene + SOTA Blueprint check before execution.
- **P2 | Codebase Modularization (`BP_FULL_APP_SOTA_AUDIT.md`, `W4`):**
  - Split `5` monoliths `>2,000L` (tracked live in `Sec 4` below: `StudioBatchExporter.cs`, `MainActivity.PlayerTab.cs`, `AndroidAudioOutput.cs`, `RoomSimulatorView.cs`, `MediaLibraryService.cs`) into cohesive partial classes / helper services w/o altering DSP math or public APIs.
- **P3 | Deferred / Optional Horizon (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` & `SAVED_IDEAS.md`):**
  - `Phase B W4–W5` (local INT4 LLM spatial director & conversational object choreography): **DEFERRED** until strict `:worker` isolation + S25U PSS ceiling (`<1.2 GB` delta) verified w/ zero LMKD kill risk.
  - Optional H2+ enhancement stubs in `.agents/docs/SAVED_IDEAS.md` (pull on demand via SOTA Blueprint).

## 3. Resolved Conflicts & Anti-Regression Registry
- **H1 | Superseded Full-Screen Blur Directive (`RESOLVED`):** `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md` per-frame full-screen `RenderEffect` blur **superseded** by `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md` (`W51.4` `EnableFullScreenRenderEffectBlur=false`, eliminated `14.78%` 120 Hz jank). **INVARIANT:** NEVER re-enable full-screen per-frame `RenderEffect` blur.
- **H2 | Local LLM / Neural RAM vs S25U LMKD OOM (`RESOLVED`):** `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` `W4` & `PHASE3_VOCAL_CORTEX_ROADMAP.md` `W32` risked `2.0–4.4 GB` PSS LMKD kills. **INVARIANT:** All heavy neural/LLM workloads MUST run under `:worker` process isolation + `v2.9.82` `NativeOnnxRuntimeBridge` low-RAM guard (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` `Phase B` remains `DEFERRED`).
- **H3 | Duplicate `W31` FDKF Port Spec & Scarlett RT Risk (`RESOLVED`):** `PHASE3_VOCAL_CORTEX_ROADMAP.md` `W31` previously specified porting FDKF from Python despite verified C# 8.0 `Cavern/VocalChain/AcousticFeedbackShieldFoundation.cs` already shipped & proven (`GT-13`). **INVARIANT:** Wire existing `AcousticFeedbackShieldFoundation.cs` directly; preserve `v2.9.80–v2.9.81` Scarlett dual-watermark `<10 ms` RT duplex & bit-exact headphone bypass.
- **H4 | Stale Blueprint Statuses & Undetected 5th Monolith (`RESOLVED`):** Completed blueprints previously lingered as `ACTIVE`/`DRAFT` & `AndroidAudioOutput.cs` (`>2,000L`) was missing from `W4` monolith tracking. **INVARIANT:** Machine-sync all blueprint statuses & `>2,000L` monoliths into `Sec 4` via `plan_tool.py blueprint hub --sync`, enforced by `verify_universal.py --all` & `blueprint hygiene --strict`.

## 4. Live Machine-Synced Inventory
<!-- HUB:AUTO_START -->
HUB_STATS|blueprints=6 (done=1, partial=4, active=0, deferred=1, superseded=0)|archived_done=10|min_score=100/100|open_conflicts=0|monoliths_gt2000=5

| Blueprint ID | Status | Score | Tier | H2+ Stubs | Hygiene / Lineage Note |
| :--- | :---: | :---: | :---: | :---: | :--- |
| `BP_BLUEPRINT_ARCHITECTURE_V2` | `DONE` | `100/100` | `T3` | `1` | W1-W3 shipped in dynamic-fractal-planner v2.19.0 + 10 prior DONE Cavern blueprints compacted into blueprints_d |
| `BP_FULL_APP_SOTA_AUDIT` | `PARTIAL` | `100/100` | `T3` | `2` | W1-W3 DONE (T6); W4 monolith backlog updated to 5 files >2000L (added AndroidAudioOutput.cs 2157L); W5 speaker |
| `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX` | `DEFERRED` | `100/100` | `T3` | `2` | resolved=Phase-A W1-W3 shipped in T6; Phase-B W4 local INT4 LLM deferred & MUST execute in isolated :worker p |
| `BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA` | `PARTIAL` | `100/100` | `T2` | `1` | W1 native/C# code DONE (v2.9.80); W2-LiveReverify physical Scarlett 2i2 Gen4 S25U test pending. |
| `BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP` | `PARTIAL` | `100/100` | `T3` | `1` | W53.0 immediate fixes shipped (Hub v6.1 + OmniAgent v2.2 remoteMessaging FGS + Cavernize direct channel + WebG |
| `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS` | `PARTIAL` | `100/100` | `T3` | `1` | W51.1-W51.7 code DONE (v2.9.81) + ONNX low-RAM fixes (v2.9.82); W52 live S25U verification pending. |

ARCHIVED_DONE|count=10|ledger=`.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md`|tarball=`.agents/docs/archive/blueprints_done.tar.gz`|ids=`BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP`, `BP_CORE_RUNTIME_STABILITY_AND_SCHEDULING`, `BP_DEVICE_LOG_REMEDIATION`, `BP_INSTANT_ZERO_WASTE_RESUME`, `BP_LIVE_QA_BUG_HUNTER_SOTA`, `BP_ONEUI9_UX_MOTION_DISCOVERY_AND_LYRICS`, `BP_PLAN_GOVERNANCE_AND_CENTRAL_HUB`, `BP_STUDIO_EXPORT_METADATA_AND_DYNAMIC_ATMOS`, `BP_STUDIO_SPATIAL_EXPORT_COMPRESSION`, `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY`

### Oversized Code Files (`>2,000L` Live Scan)
| File | Lines | Cap |
| :--- | ---: | ---: |
| `CavernSamples/Reusable/Cavernize.Logic/Rendering/StudioBatchExporter.cs` | `3578` | `2000` |
| `CavernSamples/CavernizeAndroid/MainActivity.PlayerTab.cs` | `2333` | `2000` |
| `CavernSamples/CavernizeAndroid/Audio/AndroidAudioOutput.cs` | `2161` | `2000` |
| `CavernSamples/CavernizeAndroid/Views/RoomSimulatorView.cs` | `2096` | `2000` |
| `CavernSamples/CavernizeAndroid/Services/MediaLibraryService.cs` | `2046` | `2000` |
<!-- HUB:AUTO_END -->

## 5. Completed Roadmaps & Archive Index
- **Completed Roadmaps (`STATUS|DONE`):**
  - `.agents/docs/MASTER_AUDIT_PLAN_W46_W51.md` (W46–W51 full audit, DSP/ProAudio/UI/stability fixes)
  - `.agents/docs/MASTER_OVERHAUL_PLAN.md` (Core overhaul stages)
  - `.agents/docs/PLUGIN_OPTIMIZATION_PLAN.md` (`dynamic-fractal-planner` optimization waves)
- **Active Roadmaps:**
  - `.agents/docs/PHASE3_VOCAL_CORTEX_ROADMAP.md` (`W31–W42`, hygienized w/ H2+H3 guards)
- **Historical Archives (`<=120L` bounded slices only):**
  - `.agents/docs/archive/PHASE2_ROADMAP.md`
  - `.agents/docs/archive/SPATIAL_ROADMAP.md`
  - `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`
  - `.agents/docs/archive/EXTERNAL_AI_SYNC_ARCHIVE.md`
  - `.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md` (index for verbatim `DONE` archive `.agents/docs/archive/blueprints_done.tar.gz`)
