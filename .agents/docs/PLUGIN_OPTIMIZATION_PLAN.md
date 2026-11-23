# Plugin-Compliance, Token-Diet & Monolith Modularization Plan (`W46.d`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).
> SCHEDULE|Executes **IMMEDIATELY NEXT** (in the follow-up chat right after `W46.c`), BEFORE `W47`, so Waves `W47`–`W51` & Phase 3 run on a lightweight, modular, zero-warning, low-token repository.
> ORIGIN|Measured live repo audit (`2026-11-23`): `.agents/` docs total `553 KB` w/ 4 giant docs (`PHASE2_ROADMAP.md` `101 KB`, `EXTERNAL_AI_SYNC.md` `64 KB`, `PROJECT_HISTORY.md` `52.5 KB`, `cloudflare_live_relay_worker.js` `52.7 KB` inside `docs/`) + obsolete duplicate `MASTER_AUDIT_PLAN_W46_W50.md` (`13 KB`); C# codebase has 4 massive UI/L10n monoliths (`6,376L`, `5,132L`, `3,911L`, `3,011L`) + 15 files `1,300–2,473L` breaching plugin `read_budget` (`300L` full / `250L` slice) by `5x–21x`; `11` lingering `verify_universal.py --all` C# static warnings.
> SAFETY_RULE|Split into **2 clean checkpoints**: `W46.d-1` (Docs/Memory/Plugin hygiene, `[skip ci]`, `0` runtime risk) -> `W46.d-2` (Pure mechanical C# `partial class` modularization + `11 -> 0` static warnings, `0` logic/behavioral changes, `7/7` CI-parity `0err/0warn`). Always run `flight_recorder.py init/save/close`, re-check `omni_agent.py capabilities` before push, auto-push via `omni_agent.py push-workspace --via auto`, & sync Termux + Antigravity (`ag-sync`).

---

## 1. Sub-Wave `W46.d-1 [x]` — Docs/Memory Token Diet & Plugin `v2.9.0` Guardrails (`[skip ci]`, `714ec7b8`)
- **Goal:** Cut `.agents/` active context weight by `>= 65%`, eliminate duplicate/obsolete plans, & add automated anti-bloat + retroactive higher-version sync guardrails into `dynamic-fractal-planner`.
- **Delivered:**
  1. Archived `PHASE2_ROADMAP.md` (`101 KB`) -> `archive/`; deleted obsolete `MASTER_AUDIT_PLAN_W46_W50.md` (`13 KB`); pruned `PROJECT_HISTORY.md` (`52.5->20.5 KB`) & `EXTERNAL_AI_SYNC.md` (`64->24.8 KB`) verbatim into `archive/`; slimmed root `AGENTS.md` (`16.7->11.0 KB`).
  2. Shipped `dynamic-fractal-planner v2.9.0` (`schema_version: 4`, monotonic `plugin_version`, bidirectional higher-version `sync-plugin`, `install.sh` downgrade guard, `code_weight_limits`, cross-file `key_parity_rules.target_path`, zero-FP gitless `--all`), upgraded Cavern `.agents/planner_profile.json` (`v3->v4`, `plugin_version: "2.9.0"`), && synced Termux global plugin `v2.5.1 -> v2.9.0`.

---

## 2. Sub-Wave `W46.d-2 [x]` — Mechanical C# Monolith Splitting & Zero Static Warnings (Full CI Gate)
- **Goal:** Decompose all 7 C# monoliths (`>2,000L`) into clean `partial class` files (`0` behavioral changes) && clear ALL `11` remaining `verify_universal.py --all` static warnings (`21 -> 0` total warnings).
- **Delivered Partial-Class Splits (`0` logic/behavioral changes):**
  - `MainActivity.SettingsTab.cs` (`6,376L` removed) -> `MainActivity.Settings.{Core,Audio,Spatial,Headphones,Neural,ProAudio,Diagnostics}.cs` (`491–1,168L` each)
  - `MainActivity.PlayerMusicTab.cs` (`5,132L` removed) -> `MainActivity.PlayerMusic.{Core,NowPlaying,Library,LibraryViews,Modals}.cs` (`825–1,171L` each)
  - `MainActivity.cs` (`3,911L`) -> `MainActivity.{cs,Navigation.cs,MediaLoader.cs,NeuralWorkflows.cs,UiHelpers.cs}` (`388–1,035L` each)
  - `AndroidStrings.cs` (`3,011L`) -> `AndroidStrings.{cs,PtBr.cs,Hu.cs,Zh.cs}` (`1,524L`/`1,432L`/`32L`/`32L`) + cross-file `target_path` parity in `planner_profile.json`, `verify_universal.py`, & `check_android_cs.py` (`05_Packaging_L10n`)
  - `LiveDiagnosticBridgeService.cs` (`2,473L`) -> `LiveDiagnosticBridgeService.{cs,RelayCrypto.cs,Commands.cs,Reflection.cs}` (`474–840L` each)
  - `RealtimePlayerEngine.cs` (`2,335L`) -> `RealtimePlayerEngine.{cs,Streaming.cs,SpatialPipeline.cs}` (`537–972L` each)
  - `NativeOnnxRuntimeBridge.cs` (`2,298L`) -> `OnnxRuntimeHeaderParser.cs` + `NativeOnnxRuntimeBridge.{cs,ExecutionProviders.cs,Session.cs}` (`422–946L` each)
- **Cleared All 11 Lingering Static Warnings (`11 -> 0`):**
  - `5x` horizontal `LinearLayout` `BaselineAligned = false` (`MediaStageViews.cs`, `OneUiComponents.cs`, `MainActivity.PlayerMusic.Core.cs`)
  - `2x` `Build.VERSION.SdkInt` -> `OperatingSystem.IsAndroidVersionAtLeast(23/30)` (`OneUiTheme.cs`, `MainActivity.Settings.Core.cs`)
  - `4x` unqualified `Process.Start(` -> `System.Diagnostics.Process.Start(` (`AudioQualityAnalyzer.cs`, `LosslessTwinDiscoveryService.cs`, `LyricsService.cs`)
- **Verified Gate (`W46.d-2`):** `check_android_cs.py --ci-parity --strict-warnings` (`7/7 PASS, 0 errors, 0 warnings`, `81` Android files) + `run_w46_unit_tests.py` (`6/6 PASS`) + `run_audio_ground_truth.py` (`7/7 PASS`) + `verify_universal.py --all` (`783` files, `0 failures, 0 warnings`) + `run_plugin_tests.py --fast` (`53/53 OK`) + `repo_map.py` (`400` files) + `error_learner.py --audit-lessons` (`17/17 PASS`) + `dashboard.test.mjs` (`8/8 PASS`).
