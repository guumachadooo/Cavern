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
- **P0 | V4 Master Reform (`BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`, `W55.1–W55.5`):**
  1. `W55.1` (`[DONE]` Wave V4.1 P0 — shipped `08b44bcb`): stem deduplication (`FilterRenderableStems`), discrete-stem Karaoke backing preservation, ITU-R BS.2127 Atmos 3D gain staging, symmetric Backing L/R, silent-object 3D fade, 2-step `ConfirmDestructiveAction` (10 paths, 4 langs), quantization-aware RAM gate (`<=2600 MB`, 120 MB W8A16) + Manual/Cloud isolation, `FilesDir/neural_tracks` persistence + tab-cancel overlay fix, & live S25U `SystemUI.apk` token extraction. **Next:** install signed V4.1 CI APK on S25U & verify live.
  2. `W55.2` (`[PLANNED]` Wave V4.2): expressive 3D motion & active-only strategic Orbit, hide `SpatialChoreoMode` when 3D OFF, real lyrics-triggered spatial FX, spectral/transient micro-object "ear-candy" 3D layer, & off-UI-thread 3D/ONNX loading (zero UI freeze).
  3. `W55.3` (`[PLANNED]` Wave V4.3): output-aware Binaural/Speaker XTC toggle + auto-bypass virtual ambience on speakers, single anti-conflict Spatialization hub (MagLS vs Min-Phase HRIR+ITD vs BRIR/ISM), hide 5.1/7.1 upmix on native Atmos/AI 3D, & unified 1-tap Local/Cloud AI model picker w/ clear output maps.
  4. `W55.4` (`[PLANNED]` Wave V4.4): extract `QsEditMode` spring curves via `jadx`, authentic `SystemUI.apk` blurred-glass popups (`#CC2E2E2E`, `26dp`, `Window.SetBackgroundBlurRadius`), 120 Hz glass cards (`#3D000000`+`#0DFFFFFF`), true long-press-border drag reorder, smooth top-bar collapse, & scroll-friendly 3D room touch disambiguation.
  5. `W55.5` (`[PLANNED]` Wave V4.5): Listening/Workspace Presets, JSON Export/Import, Drive/SAF Remote Cache, audited MVSEP recipes, & **real Snapdragon 8 Elite Hexagon V79 NPU (QNN HTP) + Adreno 830 WebGPU + ARM64 KleidiAI CPU Heterogeneous Symbiosis** (W^X-compliant dual-EP runtime, external C/C++ STFT/iSTFT + static FP16/W8A16 NPU backbone, double-buffered inter-chunk/inter-stage pipelining, & live S25U fastest-EP auto-router).
- **P0 | Physical S25U Live Validation (`v2.9.91+` / V4.1 & OmniAgent `v2.2.0` APKs):**
  1. `W54-LiveVerify` (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md` + `BP_V4_REFORM_SPATIAL_AI_ONEUI9.md`): Install signed V4.1 CI APK + OmniAgent `v2.2.0` on S25U & run full `V1–V12 --strict-mode --gate` (`29` frozen contracts covering V3 Waves A–F + V4.1 P0 fixes).
  2. `W52` (`BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS.md`): Live S25U check of vocal stability, neural stem cache TTL/hit-rate, 120 Hz zero-jank (`EnableFullScreenRenderEffectBlur=false`), EBU R128 loudness match, & smooth volume ramp.
  3. `W2-LiveReverify` (`BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA.md`): Live Scarlett 2i2 Gen 4 USB-C duplex check (`<10 ms` RT mic duplex, 0 xruns, bit-exact headphone bypass, hot-plug recovery).
  4. `CavernBugHunter v2` (`omni_agent.py hunt`): Clean 0-finding confirmation pass on live device.
- **P1 | V3 Adversarial QA Gate, AI Separation, Spatial Motion, Modular One UI 9, Mobile ONNX & Transaural Roadmap (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP.md`, `W53.Q1–W53.Q4` + `W53.1–W53.6`):**
  1. `W53.Q1` (`[DONE]` QA Layer Q1 + M1 Canary): Native Android View geometric/overlap/touch-target/ellipsis/alignment/WCAG/token-grid oracle + zero-dep BT.709 Golden SSIM across 5 tabs & modals (proved: Spike S3 `maxWalk=9.18ms`, offline M1 `10/10` `0-FP`, live S25U M1 `7/7=100%`, caught 2 real S25U UI bugs).
  2. `W53.Q2` (`[DONE]` QA Layer Q2 + Live S25U `v2.9.85` & M2 Canary `10/10` `0-FP`): External OmniAgent `cavernize_watchdog` PID/ANR/crash-restart tracker + blocking `ApplicationExitInfo` / `StrictMode` (opt-in w/ `finally` restore) / 120 Hz `FrameMetrics` (`cpuP95Ms <= 12ms`, `frozen == 0`, gesture `<50ms`) + Termux crash forensics fallback — live-proved on S25U `v2.9.85` (PID-restart detected, real `BlockGuardOs.access` + `56ms` `Tab:Play` spike caught, `LiveM2CanaryV2` `75ms` stall caught).
  3. `W53.Q3` (`[DONE]` QA Layer Q3 + Live S25U `v2.9.86` Native Proof & M3 Canary `10/10` `0-FP`): Native `soak_snapshot`/`rage_click`/`drag_sweep`/`reflow_probe` (`1.3x` font-scale & landscape w/ `finally` restore) + expanded `V6` hostile + `V11_dirty_state_soak` (native PSS, GC heap, View/thread & `/proc/self/fd` OLS slope); live-confirmed natively on S25U `v2.9.86` (`V11` PASS `ΔPSS=-10.46MB/ΔFD=+4/ΔGC=-2.46MB/ΔViews=0/stuckOp=False/dialogs=0`, `3` rage targets, `2` drag sweeps, `3` reflow tabs catching real `font_130` ellipsis).
  4. `W53.Q4` (`[DONE]` QA Layer Q4 + Plugin `v2.20.0` + Live S25U `v2.9.86` M4 Canary `10/10` `0-FP`): Frozen 1-bug=1-test contracts (`.agents/qa/regression_contracts.json`, `12` immutable contracts) + `V12_regression_state_contracts` + fail-closed `hot_exec` `CavernQ4StateContractProbe` + `--gate` blocking CLI + `dynamic-fractal-planner v2.20.0` (`qa_gate.py`, `plan_tool.py qa`, `verify_universal.py` auto-gate, `[QA_GATE|COLD_CTX]`); live-proved on S25U `v2.9.86` (`12/12` PASS `hotExecVerified=true`; live M4 `OneUiMotionBlur` mutation blocked `rc=2 FAIL` -> restored `rc=0 PASS`).
  5. `W53.1` (`[DONE]` Wave A — B2/B3/B4): B2 Lower-Mid Anchor MAP estimator + zero-phase 50 Hz vocal HP + GCC-PHAT priming compensation (`GT-14`: `-25.93 dB` low-band residual under `+12 dB` bass, `0.00 dB` bass delta, `0 B` RT GC); B3 `CrossVariantVocalAligner` + `ReferenceDriftPpm` + 40 ms Hann-windowed Stage-1 onset envelope (`0.001 smp` offset err, `24.98/25 ppm` drift, `-58.45 dB` late-track FLAC↔Atmos karaoke); B4 MVSEP 2-Stage Custom Cascade (`SeparateTwoStageCascadeAsync`, `sep_type=49 add_opt2=0` vocals-only + independent cancel-safe Stage 1/2 job hashes); + 3 frozen contracts (`15/15` live S25U `V12 --gate` PASS).
  6. `W53.1b` (`[DONE]` Full-System QA Remediation & Oracle Calibration): fixed all 6 real app defects + calibrated 3 QA oracle FPs + froze `REG-QA-016..020`; **live-verified on S25U `v2.9.88` (`V12 --gate`: `20/20 PASS`, `hotExec=True`, `fail=0`)**.
  7. `W53.2` (`[DONE]` Wave B — B7): Unified context-aware Play/Sing AI extraction sheet (`ShowUnifiedAiExtractionSheet`), multi-variant non-destructive cache switcher (`FindAllActiveSongCacheVariants`, off-UI-thread `ActivateCachedVariantAsync`, Use/Delete/Extract New Variant/Remove All/Optimize Storage), incremental Stage-1 vocal-stem reuse across BOTH MVSEP 2-Stage Cloud & Local ONNX (`preExtractedTrack` + `seededFromReusableVocals` + discrete multi-stem band preservation in `MergeTwoStageKaraokeStems`), CNTC v3 lossless mono-ref dedup + `CompressionLevel.Optimal` + `CompactFileIfBeneficial`/`OptimizeCacheStorage`, 13 4-lang keys, `T45`, `PY_T12`, & `REG-B7-UNIFIED-CACHE-REUSE`.
  8. `W53.3` (`[DONE]` Wave C — B6): Stokes 2×2 covariance PCA constant-power stereo-pan azimuth estimator (diffuse-reverb immune), fixed-object pan preservation (`BuildSceneDescriptors(IReadOnlyList<StereoStemBuffer>)` + `GetPanPreservedAnchorPosition` + `SpatialChoreoMode.Off` ERB skip; `GT-15` `0.00°` centroid error vs `<=5.0°` `QAS|Q4`), & lyrics-mood (`Anthem`/`Intimate`/`Euphoric`/`Melancholic`/`Aggressive`) + energy-aware 3D choreography orbiting relative to mix pan (`58.93°/s` max slew, `14.66°` mood delta, `0 B` RT GC), `T46`, `PY_T12`, & `REG-B6-STEREO-PAN-CHOREO` (`22/22` contracts).
  9. `W53.4` (`[DONE]` Wave D — B10): One UI 9 modular editable quick-panel control blocks (`ModularQuickPanelLayout.cs`, `MainActivity.ModularBlocks.cs`) w/ schema-safe persistence (`FromJsonOrDefault`), locked/pinned-top essential guards, in-place 120 Hz `ReorderLinearLayoutInPlace`, long-press `+` / Settings editor, `T47`, `PY_T13`, & `REG-B10-MODULAR-ONEUI9`.
  10. `W53.5` (`[DONE]` Wave E — B12): Mobile ONNX W8A16 weight-only per-channel INT8 (`DequantizeLinear`) + FP16 quantizer (`OnnxMobileQuantizer.cs`, `OnnxFp16ModelConverter.cs`) + SoC-fingerprint-keyed QNN HTP context binary cache (`OnnxQnnContextCacheManager.cs`, `Build.Fingerprint`-bound auto-purge), `T48`, `PY_T13`, & `REG-B12-QNN-FP16-W8A16-CACHE`.
  11. `W53.6` (`[DONE]` Wave F — B13): Physical transaural crosstalk cancellation (`TransauralXtcFilter.cs`, `TransauralXtcModels.cs`, `TransauralXtcPostProcessingStage.cs`) for built-in phone & external stereo speakers w/ guaranteed bit-exact `0.0f` headphone bypass (`GT-16`: `+13.87 dB` phone / `+16.28 dB` desktop gain, `0.0f` headphone err, `0 B` RT GC, `0.094 ms/blk`), `T49`, `PY_T13`, & `REG-B13-TRANSAURAL-XTC-BYPASS` (`25/25` contracts, signed `v2.9.90` APK `#90` `0/0`).
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
HUB_STATS|blueprints=7 (done=1, partial=5, active=0, deferred=1, superseded=0)|archived_done=10|min_score=100/100|open_conflicts=0|monoliths_gt2000=8

| Blueprint ID | Status | Score | Tier | H2+ Stubs | Hygiene / Lineage Note |
| :--- | :---: | :---: | :---: | :---: | :--- |
| `BP_BLUEPRINT_ARCHITECTURE_V2` | `DONE` | `100/100` | `T3` | `1` | W1-W3 shipped in dynamic-fractal-planner v2.19.0 + 10 prior DONE Cavern blueprints compacted into blueprints_d |
| `BP_FULL_APP_SOTA_AUDIT` | `PARTIAL` | `100/100` | `T3` | `2` | W1-W3 DONE (T6); W4 monolith backlog updated to 5 files >2000L (added AndroidAudioOutput.cs 2157L); W5 speaker |
| `BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX` | `DEFERRED` | `100/100` | `T3` | `2` | resolved=Phase-A W1-W3 shipped in T6; Phase-B W4 local INT4 LLM deferred & MUST execute in isolated :worker p |
| `BP_SCARLETT_PROAUDIO_STABILITY_AND_LIVE_QA` | `PARTIAL` | `100/100` | `T2` | `1` | W1 native/C# code DONE (v2.9.80); W2-LiveReverify physical Scarlett 2i2 Gen4 S25U test pending. |
| `BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP` | `PARTIAL` | `100/100` | `T3` | `1` | ALL H1 slices (`W53.0`, `W53.Q1–Q4`, `W53.1`, `W53.1b`, `W53.2`, `W53.3`, `W53.4`, `W53.5`, `W53.6`) DONE & CI |
| `BP_V4_REFORM_SPATIAL_AI_ONEUI9` | `PARTIAL` | `100/100` | `T3` | `1` | Initiated per V4 user audit across live Galaxy S25 Ultra (`v2.9.91`): resolves P0 stem duplication (`vocals`+` |
| `BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS` | `PARTIAL` | `100/100` | `T3` | `1` | W51.1-W51.7 code DONE (v2.9.81) + ONNX low-RAM fixes (v2.9.82); W52 live S25U verification pending. |

ARCHIVED_DONE|count=10|ledger=`.agents/docs/archive/BLUEPRINTS_DONE_LEDGER.md`|tarball=`.agents/docs/archive/blueprints_done.tar.gz`|ids=`BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP`, `BP_CORE_RUNTIME_STABILITY_AND_SCHEDULING`, `BP_DEVICE_LOG_REMEDIATION`, `BP_INSTANT_ZERO_WASTE_RESUME`, `BP_LIVE_QA_BUG_HUNTER_SOTA`, `BP_ONEUI9_UX_MOTION_DISCOVERY_AND_LYRICS`, `BP_PLAN_GOVERNANCE_AND_CENTRAL_HUB`, `BP_STUDIO_EXPORT_METADATA_AND_DYNAMIC_ATMOS`, `BP_STUDIO_SPATIAL_EXPORT_COMPRESSION`, `BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY`

### Oversized Code Files (`>2,000L` Live Scan)
| File | Lines | Cap |
| :--- | ---: | ---: |
| `CavernSamples/Reusable/Cavernize.Logic/Rendering/StudioBatchExporter.cs` | `3578` | `2000` |
| `CavernSamples/CavernizeAndroid/MainActivity.PlayerTab.cs` | `2383` | `2000` |
| `CavernSamples/CavernizeAndroid/Audio/AndroidAudioOutput.cs` | `2161` | `2000` |
| `Cavern/Spatial/SpatialObjectCortex.cs` | `2152` | `2000` |
| `CavernSamples/CavernizeAndroid/Views/RoomSimulatorView.cs` | `2094` | `2000` |
| `CavernSamples/CavernizeAndroid/Services/MediaLibraryService.cs` | `2046` | `2000` |
| `CavernSamples/CavernizeAndroid/MainActivity.NeuralWorkflows.cs` | `2027` | `2000` |
| `CavernSamples/CavernizeAndroid/Theme/OneUiTheme.cs` | `2013` | `2000` |
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
