# Plugin-Compliance, Token-Diet & Monolith Modularization Plan (`W46.d`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).
> SCHEDULE|Executes **IMMEDIATELY NEXT** (in the follow-up chat right after `W46.c`), BEFORE `W47`, so Waves `W47`–`W51` & Phase 3 run on a lightweight, modular, zero-warning, low-token repository.
> ORIGIN|Measured live repo audit (`2026-11-23`): `.agents/` docs total `553 KB` w/ 4 giant docs (`PHASE2_ROADMAP.md` `101 KB`, `EXTERNAL_AI_SYNC.md` `64 KB`, `PROJECT_HISTORY.md` `52.5 KB`, `cloudflare_live_relay_worker.js` `52.7 KB` inside `docs/`) + obsolete duplicate `MASTER_AUDIT_PLAN_W46_W50.md` (`13 KB`); C# codebase has 4 massive UI/L10n monoliths (`6,376L`, `5,132L`, `3,911L`, `3,011L`) + 15 files `1,300–2,473L` breaching plugin `read_budget` (`300L` full / `250L` slice) by `5x–21x`; `11` lingering `verify_universal.py --all` C# static warnings.
> SAFETY_RULE|Split into **2 clean checkpoints**: `W46.d-1` (Docs/Memory/Plugin hygiene, `[skip ci]`, `0` runtime risk) -> `W46.d-2` (Pure mechanical C# `partial class` modularization + `11 -> 0` static warnings, `0` logic/behavioral changes, `7/7` CI-parity `0err/0warn`). Always run `flight_recorder.py init/save/close`, re-check `omni_agent.py capabilities` before push, auto-push via `omni_agent.py push-workspace --via auto`, & sync Termux + Antigravity (`ag-sync`).

---

## 1. Sub-Wave `W46.d-1` — Docs/Memory Token Diet & Plugin `v2.8.0+` Guardrails (`[skip ci]`)
- **Goal:** Cut `.agents/` active context weight by `>= 65%`, eliminate duplicate/obsolete plans, & add automated anti-bloat guardrails into `dynamic-fractal-planner`.
- **Tasks:**
  1. **Archive Completed & Oversized Docs (`.agents/docs/archive/`):**
     - Move completed `.agents/docs/PHASE2_ROADMAP.md` (`101.2 KB`, `222L`) -> `.agents/docs/archive/PHASE2_ROADMAP.md` && register in `.agents/planner_profile.json` `archive_docs` (enforces `<=120L` slice guard).
     - Delete obsolete duplicate `.agents/docs/MASTER_AUDIT_PLAN_W46_W50.md` (`13.0 KB`, 100% superseded by `MASTER_AUDIT_PLAN_W46_W51.md`).
     - Prune `.agents/docs/PROJECT_HISTORY.md` (`52.5 KB -> <= 20 KB`): move pre-`W38` milestones verbatim into `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md` (NEVER rewrite historical facts).
     - Prune `.agents/docs/EXTERNAL_AI_SYNC.md` (`64.2 KB -> <= 26 KB`): move pre-`W41` entries verbatim into `.agents/docs/archive/EXTERNAL_AI_SYNC_ARCHIVE.md` && register in `archive_docs`.
  2. **Deduplicate Root `AGENTS.md` vs `EXTERNAL_AI_ONBOARDING.md`:**
     - Slim root `AGENTS.md` (`16.4 KB -> <= 9.5 KB`) into crisp short bullets (eliminating "lost-in-the-middle" attention dilution & Sec 0 duplication with `EXTERNAL_AI_ONBOARDING.md`); keep deep CLI flags/troubleshooting solely in `EXTERNAL_AI_ONBOARDING.md`.
  3. **Clean Code-Out-of-Docs (Optional Safe Move):**
     - Evaluate moving `.agents/docs/cloudflare_live_relay_worker.js` (`52.7 KB`) -> `.agents/agent/cloudflare_live_relay_worker.js` (updating `deploy_worker.py`, `test_worker_v4.mjs`, `DIAGNOSTICS_HUB.md`, `CloudflareDeployService.cs` — or if `CloudflareDeployService.cs` stays untouched in `W46.d-1`, move it in `W46.d-2` alongside C# edits so `[skip ci]` remains pure).
  4. **Plugin Upgrade (`dynamic-fractal-planner v2.9.0` — `dual-project+plugin`, Retroactive Upgrade + Higher-Version Sync + Anti-Bloat):**
     - **Retroactive Workspace Auto-Upgrade (`v3 -> v4` + `plugin_version`):** Bump `schema_migrations.py` to `CURRENT_SCHEMA_VERSION = 4` (`migrate_v3_to_v4` adding `code_weight_limits` + `memory_weight_limits` losslessly + `profile_v3_cavern.json` compat fixture), track/advance `plugin_version` monotonically in `planner_state.py:auto_upgrade_workspace()` (never downgrading future workspaces), && retroactively migrate Cavern's `.agents/planner_profile.json` (`v3 -> v4`, `plugin_version: "2.9.0"`).
     - **Bidirectional Higher-Version Plugin Propagation (`sync-plugin`):** Add `self_evolve.py:sync_plugin_versions()` + `plan_tool.py evolve sync-plugin [--apply]` + `planner_session_start.py` auto-sync (`embedded > global` auto-propagates w/ `.last_good_backup` & runtime-state preservation; `global > embedded` emits upgrade directive) + `install.sh` downgrade guard (`--force` required). Immediately sync Termux global `~/.gemini/config/plugins/dynamic-fractal-planner` (`v2.5.1 -> v2.9.0`).
     - **Universal Code Bloat Guard, Cross-File Key Parity & Gitless Sync Fix (`verify_universal.py`):** Add `check_code_weight()` (`warn_lines: 1200` edited / `warn_lines_all: 2000` full-repo), add cross-file `target_path` support in `check_key_parity_rules()` (ready for `AndroidStrings.cs` + `AndroidStrings.PtBr.cs` split), && fix `check_plugin_sync_parity` under `--all` (gitless container) so zero false warnings fire when plugin `EXTERNAL_AI_SYNC.md` records the current version.
- **Verification Gate (`W46.d-1`):** `run_plugin_tests.py --fast` PASS + `verify_universal.py --all` (`0` memory-bloat warnings, `0` false plugin-sync warnings) + `error_learner.py --audit-lessons` PASS + `dashboard.test.mjs` PASS + direct push (`[skip ci]`) + Termux global plugin sync (`v2.5.1 -> v2.9.0`) + `ag-sync`.

---

## 2. Sub-Wave `W46.d-2` — Mechanical C# Monolith Splitting & Zero Static Warnings (Full CI Gate)
- **Goal:** Decompose the worst C# monoliths into clean `partial class` files (`<= 800–1,000L` each, matching plugin `read_budget` + `repo_map.py` surgical lookup) && clear ALL `11` remaining `verify_universal.py --all` static warnings with **zero functional regression**.
- **Strict Mechanical Refactor Invariants:**
  - ZERO algorithm/UI/control-flow rewrites during splits; preserve every namespace, class name, method signature, `AndroidStrings` key, and XML summary verbatim.
  - Keep `Cavern/` & `Cavern.Format/` strict C# 8.0 (`!is not`, `!new()`, `!file-scoped namespace`).
  - Run `python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings` after EACH major split group.

### 2.1 Tier 1 Monolith Splits (Highest Token ROI — Mandatory)
| Current File | Size (Lines / KB) | Target `partial class` Decomposition (`~500–950L` each) |
| :--- | :---: | :--- |
| `CavernSamples/CavernizeAndroid/MainActivity.SettingsTab.cs` | `6,376L / 319 KB` | `SettingsTab.Core.cs`, `SettingsTab.Audio.cs`, `SettingsTab.Spatial.cs`, `SettingsTab.Neural.cs`, `SettingsTab.ProAudio.cs`, `SettingsTab.Diagnostics.cs`, `SettingsTab.Appearance.cs` (under `CavernSamples/CavernizeAndroid/Ui/Settings/` or flat `MainActivity.Settings.*.cs`) |
| `CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs` | `5,132L / 261 KB` | `MainActivity.PlayerMusic.Core.cs`, `MainActivity.PlayerMusic.Library.cs`, `MainActivity.PlayerMusic.NowPlaying.cs`, `MainActivity.PlayerMusic.LyricsSingDock.cs`, `MainActivity.PlayerMusic.Modals.cs` |
| `CavernSamples/CavernizeAndroid/MainActivity.cs` | `3,911L / 182 KB` | `MainActivity.cs` (lifecycle/shell), `MainActivity.PlaybackBindings.cs`, `MainActivity.NeuralWorkflows.cs`, `MainActivity.PermissionsStorage.cs` |
| `CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs` | `3,011L / 233 KB` | `partial class AndroidStrings`: `AndroidStrings.cs` (core + `CreateEnglish`), `AndroidStrings.PtBr.cs`, `AndroidStrings.Hu.cs`, `AndroidStrings.Zh.cs` *(update `planner_profile.json` `key_parity_rules` + `verify_universal.py` if cross-file parity needed -> keep `100%` EN/PT-BR parity enforced!)* |

### 2.2 Tier 2 Large Files (`1,500–2,500L` — Split in `W46.d-2` if Clean & Fast, Else Just-in-Time in `W47–W51`)
- `LiveDiagnosticBridgeService.cs` (`2,473L` -> `.Core`, `.Commands`, `.Reflection`, `.RelayCrypto`)
- `RealtimePlayerEngine.cs` (`2,335L` -> `.Core`, `.Streaming`, `.SpatialPipeline`, `.KaraokeMic`)
- `NativeOnnxRuntimeBridge.cs` (`2,298L` -> `.SessionFactory`, `.Inference`, `.EpRouting`)
- `MainActivity.PlayerTab.cs` (`1,980L`), `MediaLibraryService.cs` (`1,977L`), `RoomSimulatorView.cs` (`1,904L`), `MainActivity.ConverterTab.cs` (`1,782L`), `AppSettingsStore.cs` (`1,532L`), `OneUiTheme.cs` (`1,594L`).
- *(DSP core `MvsepAntiArtifactPipeline.cs` `1,695L`, `SpatialKaraokeEngine.cs` `1,407L`, `INeuralInferenceRuntime.cs` `1,301L`: split ONLY cleanly along internal helpers or defer until touched in `W47–W48` to avoid unnecessary merge churn.)*

### 2.3 Clear All 11 Lingering `verify_universal.py --all` Static Warnings (`11 -> 0`)
1. **4x Horizontal `LinearLayout` `BaselineAligned = false`:** `MainActivity.PlayerMusicTab.cs:1074`, `Views/MediaStageViews.cs:280`, `Views/OneUiComponents.cs:80,309`.
2. **2x `Build.VERSION.SdkInt` -> `OperatingSystem.IsAndroidVersionAtLeast(N)`:** `MainActivity.SettingsTab.cs:1547`, `Theme/OneUiTheme.cs:448`.
3. **4x Unqualified `Process.Start(` -> `System.Diagnostics.Process.Start(` (`BL-004`):** `Services/AudioQualityAnalyzer.cs:1014`, `Services/LosslessTwinDiscoveryService.cs:561,828`, `Services/LyricsService.cs:300`.
4. **1x `repo_map.py` Smoke Check:** Run `python3 -B .agents/plugins/dynamic-fractal-planner/scripts/repo_map.py --workspace .` verifying fast symbol lookup across split files.

- **Verification Gate (`W46.d-2`):**
  - `python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings` -> **`7/7 PASS, 0 errors, 0 warnings`**
  - `python3 -B .agents/scripts/run_w46_unit_tests.py` -> **`6/6 PASS`**
  - `python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all` -> **`0 failures, 0 warnings`**
  - `python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast` -> **ALL PASS**
  - Push automatically (NO `[skip ci]` since C# files changed) via `omni_agent.py push-workspace --via auto`, monitor `omni_agent.py ci-status` green, sync `ag-sync`, close Flight Recorder, && advance cleanly to **`W47`**!
