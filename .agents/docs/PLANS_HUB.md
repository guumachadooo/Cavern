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

### TRACK A | P0 — Structural, Zero-Static Co-Evolution, Coherence & Meta-Governance (ALL H1 DONE)
1. **`W56.1–W56.3` (`[DONE | DFP v2.21.1–v2.22.5]` — `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE.md`):** `PLANNER_REEXEC=1`, Triad `(semver,tree_sha256)` parity, `shlex` cmd guard, `<1,500L` engine split, machine-generated `COHERENCE_HUB.md` + `domains.json` + `@verify_check`, & Reflexive Meta-Governance (`T0/T1/T2` + `T0` canary + Living `CODE_ANCHORS` + Plane-2 `pro [L3]` `<=4/wk`).
2. **`W56.4` (`[DONE]` — `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE.md`):** App Zero-Static Foundation (`co_evolve.py` `~40ms`, `0 B` RT GC): `BridgeCmd` `54/54` explicit opcodes + `Packaging` SSOT (`federation.json`), pure `MemoryBudgetGovernor` (`<=2600 MB` `REG-001`), generated `AppSettingsStore.Schema.cs` (`213` descriptors, `206` zero-reflection resets), & `RPE<->SBE` DSP parity ledger.
3. **`W55.1b` & `W56.5` (`[DONE | DFP v2.23.0–v2.24.0]`):** Zero-Accumulation Monolith Ratchet (`+1L` on `>2,000L` = `FAIL`), 5-domain Compounding Learner (`BL|UI|ARCH|DSP|AI`), & Token-Tax Auto-Governor (`token_governor.py`, `SessionStart` diet, `PreInv` session dedup, doc token ratchet, `token-audit`, 1-shot `closeout`). *(H2+ stubs `W56.6–W56.8` integrate w/ `W55.3–W55.6`.)*

### TRACK B | P0-HW — Physical S25U Live Validation (Shipped V3 / V4.1 / W56.4 / OmniAgent `v2.2.0`)
1. `W54-LiveVerify` (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md` + `BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`): Install signed V4.1/W56.4 CI APK + OmniAgent `v2.2.0` on S25U & run full `V1–V12 --strict-mode --gate` (`29` frozen contracts covering V3 Waves A–F + V4.1 P0 fixes).
2. `W52` (`BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md`): Live S25U check of vocal stability, neural stem cache TTL/hit-rate, 120 Hz zero-jank (`EnableFullScreenRenderEffectBlur=false`), EBU R128 loudness match, & smooth volume ramp.
3. `W2-LiveReverify` (`BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA.md`): Live Scarlett 2i2 Gen 4 USB-C duplex check (`<10 ms` RT mic duplex, 0 xruns, bit-exact headphone bypass, hot-plug recovery).
4. `CavernBugHunter v2` (`omni_agent.py hunt`): Clean 0-finding confirmation pass on live device.

### TRACK C | P1–P3 — CavernizeAndroid App Reform & Feature Waves (NOW UNBLOCKED)
- **P1 | V4 Master App Reform (`BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`, `W55.1`+`W55.1b` DONE -> `W55.2` `[NEXT #1]`):**
  1. `W55.1` (`[DONE]` Wave V4.1 P0 — shipped `08b44bcb`): stem dedup (`FilterRenderableStems`), discrete Karaoke backing preservation, BS.2127 Atmos staging, symmetric Backing L/R, silent-object fade, 2-step `ConfirmDestructiveAction`, quantized RAM gate (`<=2600 MB`) + Manual/Cloud isolation, `FilesDir/neural_tracks` persistence, & live `SystemUI.apk` tokens.
  2. `W55.2` (`[NEXT #1]` Wave V4.2): expressive 3D motion & active-only strategic Orbit, hide `SpatialChoreoMode` when 3D OFF, real lyrics-triggered spatial FX, spectral/transient micro-object "ear-candy" 3D layer, & off-UI-thread 3D/ONNX loading (zero UI freeze).
  3. `W55.3` (`[PLANNED]` Wave V4.3): reactive `UiStateCoordinator`, output-aware Binaural/Speaker XTC toggle + speaker ambience auto-bypass, single Spatialization hub, hide 5.1/7.1 upmix on native Atmos/AI 3D, unified 1-tap Local/Cloud AI picker, & hard cross-tab control deduplication.
  4. `W55.4` (`[PLANNED]` Wave V4.4 — **Whole-App Shell Redesign, UI De-Pollution & All-8-Monolith Sweep**): replace 5 tabs w/ **3-Tab One UI 9 Capsule (`Library|Sound|Export`) + Persistent Mini-Player -> Full-Screen Now Playing (`Cover|Lyrics|3D Stage`) + Top-Right Settings Gear**; move live audio/spatial/AI/Scarlett controls into **`Sound`** (4 Essential cards) & slim `Settings` + opt-in Developer sub-page; merge `Studio`+`Stems` -> **`Export`** (`Master|Convert|Extract` + local `Source File` + `Queue`); `isSettingsProMode=false`, `Collapse-When-Disabled`, `0` legacy `CheckBox`/`"?"`, **split ALL 8 monoliths `<1,800L`** (lock global ratchet `0`), & post-`W55.4` Plane-2 Audit.
  5. `W55.5` (`[PLANNED]` Wave V4.5): extract `QsEditMode` springs via `jadx`, unify 50 modals into `SecQSDetail` blurred-glass popups (`#CC2E2E2E`, `26dp`, `Window.SetBackgroundBlurRadius`), 120 Hz glass cards (`28dp`), swipe Mini-Player <-> Now Playing, true long-press drag reorder, & scroll-friendly 3D touch disambiguation.
  6. `W55.6` (`[PLANNED]` Wave V4.6): Listening Presets (`Natural`/`Cinema 3D`/`Studio Reference`/`Karaoke`/`Custom`) & Workspace Presets, JSON Export/Import, SAF/Drive Remote Cache, audited MVSEP recipes, & **Hexagon V79 NPU (QNN HTP) + Adreno 830 WebGPU + KleidiAI CPU Symbiosis** (W^X dual-EP, C/C++ STFT/iSTFT + static FP16/W8A16 backbone, double-buffered pipelining).
- **P1 | Legacy Cavern SOTA Re-Engineering & Suite Parity (`BP_LEGACY_CAVERN_CORE_AND_SUITE_PARITY.md`, `W57.1–W57.4` — zero blind 2018 ports):**
  1. `W57.1` (`[NEXT #1-A | 0 UI, pairs w/ W55.2]`): unlock ARM64 NEON SIMD (`CavernAmp.IsMono`), `double` TDF-II `BiquadFilter` + `CytomicSvfFilter` + LR4 `Crossover`, zero-alloc Kaiser-sinc `Source` resampler & `DirectionalRenderer`, & 90-deg Hilbert JOC + inline loop.
  2. `W57.2` (`[PLANNED | 0 UI]`): per-instance `ListenerContext` (race-free Play+Export, skip `%APPDATA%`), async cancellable `FFmpeg.cs` (`-progress pipe:1`, POSIX `which`), & VBR E-AC-3 seek index.
  3. `W57.3` (`[PLANNED | post-W55.4]`): 2026-SOTA `FilterStudio` (click-free ZDF-SVF + Nyquist-decramped EQ + EQ APO/CamillaDSP/MiniDSP/PipeWire/FIR export) & V4 `Export -> Tools` (`ADMValidator` BS.2076-2, `EnhancedAC3Merger`, `WAVChannelReorderer`, `--override-bed` w/ `SubSampleSyncEngine`, `--extract`, `--mlp-presentation`).
  4. `W57.4` (`[PLANNED | post-W55.4 + Scarlett]`): 2026-SOTA `QuickEQ` (Farina ESS + harmonic sep, ERB/FDW HF cap `<=+6 dB`, multi-point MMM, Harman/B&K targets over `<10 ms` Scarlett/mic -> in-app or AVR/MiniDSP/CamillaDSP/EQ APO).
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
HUB_STATS|blueprints=8 (done=0, partial=7, active=0, deferred=1, superseded=0)|archived_done=12|min_score=100/100|open_conflicts=0|monoliths_gt2000=6

| Blueprint ID | Status | Score | Tier | H2+ Stubs | Hygiene / Lineage Note |
| :--- | :---: | :---: | :---: | :---: | :--- |
| `BP_ECOSYSTEM_COHERENCE_AND_SELF_GOVERNANCE` | `PARTIAL` | `100/100` | `T3` | `3` | ALL H1 Track A slices (`W56.1->W56.5`, `DFP v2.24.0`) DONE & verified: Zero-Accumulation Token-Tax & Side-Effe |
| `BP_FULL_APP_SOTA_AUDIT` | `PARTIAL` | `100/100` | `T3` | `2` | W1-W3 DONE (T6); W4 monolith backlog updated to 5 files >2000L (added AndroidAudioOutput.cs 2157L); W5 speaker |
| `BP_LEGACY_CAVERN_CORE_AND_SUITE_PARITY` | `PARTIAL` | `100/100` | `T3` | `1` | Exhaustive SOTA audit of ALL ~55k active legacy VoidX C# lines (`Cavern/`, `Cavern.Format/`, `Cavernize.Logic/ |
| `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX` | `DEFERRED` | `100/100` | `T3` | `2` | resolved=Phase-A W1-W3 shipped in T6; Phase-B W4 local INT4 LLM deferred & MUST execute in isolated :worker p |
| `BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA` | `PARTIAL` | `100/100` | `T2` | `1` | W1 native/C# code DONE (v2.9.80); W2-LiveReverify physical Scarlett 2i2 Gen4 S25U test pending. |
| `BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP` | `PARTIAL` | `100/100` | `T3` | `1` | ALL H1 slices (`W53.0`, `W53.Q1–Q4`, `W53.1`, `W53.1b`, `W53.2`, `W53.3`, `W53.4`, `W53.5`, `W53.6`) DONE & CI |
| `BP_V4_REFORM_SPATIAL_AI_ONEUI9` | `PARTIAL` | `100/100` | `T3` | `1` | Expanded w/ (1) 37,240L UI audit (`S2`), (2) whole-app shell redesign (`S4`: 3-tab capsule `Library/Sound/Expo |
| `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS` | `PARTIAL` | `100/100` | `T3` | `1` | W51.1-W51.7 code DONE (v2.9.81) + ONNX low-RAM fixes (v2.9.82); W52 live S25U verification pending. |

ARCHIVED_DONE|count=12|ledger=`.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md`|tarball=`.agents/docs/archive/blueprints_done.tar.gz`|ids=`BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP`, `BP_BLUEPRINT_ARCHITECTURE_V2`, `BP_CORE_RUNTIME_STABILITY_AND_SCHEDULING`, `BP_DEVICE_LOG_REMEDIATION`, `BP_FRACTAL_PARALLEL_WAVE_ORCHESTRATION`, `BP_INSTANT_ZERO_WASTE_RESUME`, `BP_LIVE_QA_BUG_HUNTER_SOTA`, `BP_ONEUI9_UX_MOTION_DISCOVERY_AND_LYRICS`, `BP_PLAN_GOVERNANCE_AND_CENTRAL_HUB`, `BP_STUDIO_EXPORT_METADATA_AND_DYNAMIC_ATMOS`, `BP_STUDIO_SPATIAL_EXPORT_COMPRESSION`, `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY`

### Oversized Code Files (`>2,000L` Live Scan)
| File | Lines | Cap |
| :--- | ---: | ---: |
| `CavernSamples/Reusable/Cavernize.Logic/Rendering/StudioBatchExporter.cs` | `3578` | `2000` |
| `CavernSamples/CavernizeAndroid/MainActivity.PlayerTab.cs` | `2250` | `2000` |
| `CavernSamples/CavernizeAndroid/Audio/AndroidAudioOutput.cs` | `2161` | `2000` |
| `CavernSamples/CavernizeAndroid/Services/MediaLibraryService.cs` | `2046` | `2000` |
| `CavernSamples/CavernizeAndroid/Theme/OneUiTheme.cs` | `2013` | `2000` |
| `CavernSamples/CavernizeAndroid/MainActivity.NeuralWorkflows.cs` | `2004` | `2000` |
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
