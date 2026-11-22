# External AI Onboarding & Sync Protocol (`Cavern` / `CavernizeAndroid`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 0. Platform Constraints & Auto-Loaded Living Context (CRITICAL)
- **Auto-Loaded Boot Context:** Root `AGENTS.md` (`## 0`) is auto-injected by AI Studio on every new chat -> user never needs to re-paste this context. Whenever architecture, workflows, tooling, preview shim, or persistent invariants evolve, AI MUST update `AGENTS.md` (`## 0`/`3`/`5`) && this file (`EXTERNAL_AI_ONBOARDING.md`) automatically in the same turn (keep volatile session/wave state ONLY in `HANDOFF.md` & `EXTERNAL_AI_SYNC.md`).
- **Real Stack vs. Preview Shim:** Product stack is **C# / .NET (`netstandard2.1` + `net11.0-android`) + C/C++**. Root `package.json` + `.agents/preview/{server,dashboard,dashboard.test}.mjs` is strictly a **zero-dependency pure Node.js preview shim** (`0.0.0.0:3000`, zero `node_modules/`, zero React/Vite/TSX) solely to let AI Studio's dev server boot cleanly (preventing automatic `"The dev server didn't start, please fix it"` prompts) & show live repo context. NEVER migrate/delete C# code, add npm deps, or scaffold `src/*.tsx|jsx` web boilerplate.
- External AI has NO autonomous `git push` & NO local `.NET`/APK build: the **user** clicks Push in the UI manually & triggers/watches `.github/workflows/BuildAndroid.yml` (`Build Android APK`) in GitHub Actions.
- If shell/Python CLI is unavailable in your environment -> skip CLI scripts cleanly & perform equivalent manual static checks against code + rules before finishing.

## 1. Read-First Map (Mandatory Boot Order)
1. `AGENTS.md` (repo root) -> permanent invariants, SELinux/W^X, C# 8.0 strictness, coding style, OTA versioning, Termux/CI build rules.
2. `CavernSamples/CavernizeAndroid/AGENTS.md` -> Android UI, One UI 9, & audio rules.
3. `.agents/plugins/dynamic-fractal-planner/rules/AGENTS.md` + `.agents/plugins/dynamic-fractal-planner/skills/*/SKILL.md` + `.agents/planner_profile.json` -> fractal planner rules, token diet, & verification gates.
4. All 4 repo skills in `.agents/skills/*/SKILL.md`:
   - `cavernize-device-debug` | `cavernize-build-and-deliver` | `cavernize-ui-oneui9` | `cavernize-vocal-cortex`
5. `.agents/docs/` -> `BUILD_LESSONS.md` (living timeless build/CI error guardrails), `EXTERNAL_AI_SYNC.md`, `HANDOFF.md`, `PROJECT_HISTORY.md` (`L1-60` slice only), `PHASE2_ROADMAP.md`, `PHASE3_VOCAL_CORTEX_ROADMAP.md`, `ONEUI9_REFERENCE.md`, `SAVED_IDEAS.md`, `EXTRA_REQUESTS.md`.

## 2. Device Logs (S25 Ultra Snapshot) & CI Diagnostics
- Path -> `.agents/device_logs/cavernize_log.txt` (snapshot of `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`, optionally synced directly by in-app `CloudLogSyncService` when target=`master` w/ `[skip ci]`).
- Cloud `diagnostics` branch -> `origin/diagnostics:device-logs/cavernize_log.txt` + `device-logs/latest_summary.json` (in-app `CloudLogSyncService` default) && `origin/diagnostics:ci/latest-build-diagnostics.{md,json}` + `ci/latest-learned-lessons.md` (always-on CI warnings+errors report + auto-learned candidate summary + `Cavernize-Build-Diagnostics` Actions artifact).
- Token diet -> if shell/Python available, run `python3 .agents/plugins/dynamic-fractal-planner/scripts/log_digest.py .agents/device_logs/cavernize_log.txt` (`--errors-only` if large) BEFORE reading raw slices; otherwise search/read targeted slices (`ERROR`, `Exception`, `FATAL`, `FFmpeg`, `AAudio`, `AudioTrack`, `Matrix`, `FLAC`) & tail per `.agents/skills/cavernize-device-debug/SKILL.md`.

## 3. Code & Manual Static Audit Gates
- Repo-local fractal planner (`v2.6.0`) scripts live at `.agents/plugins/dynamic-fractal-planner/scripts/` (`error_learner.py`, `live_diagnostics.py`, `live_bridge_client.py`, `self_evolve.py`, `plan_tool.py`, `verify_universal.py`); `.agents/scripts/*` are thin Cavern wrappers.
- NEVER create or commit `.metrics.jsonl`, `.model_modes.json`, `.diagnostics*`, `.hook_state.json`, or `__pycache__/` (gitignored).
- Core (`Cavern/`, `Cavern.Format/`) -> `netstandard2.1` strict C# 8.0 (`!is not`, `!new()`, `!file-scoped namespace`); zero fake DSP (ISO/ITU/EBU physics & measured datasets only); document all new `public` types/members/enums w/ `/// <summary>` (`!CS1591`) & initialize spec fields (`!CS0649`).
- Android (`CavernSamples/CavernizeAndroid/`) -> current Android TFM in `.csproj`; keep `targetSdkVersion >= 35` + `extractNativeLibs="true"` + W^X (`NativeLibs/arm64-v8a/`); maintain `AndroidStrings.cs` 1:1 EN/PT-BR key parity; respect `.NET Android` binding pitfalls (`.agents/docs/BUILD_LESSONS.md` `BL-003`..`BL-005` & `PROJECT_HISTORY.md` Sec 2).
- Dynamic Zero-Warning, Error-Learning & Autonomous Dual-Scope Self-Evolution (`MOD-SELF-EVOLVE`) -> BEFORE touching OS-versioned Android APIs or `.github/workflows/*.yml`, read `.agents/docs/BUILD_LESSONS.md`, fetch live CI diagnostics (`python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`), & verify current official Android/.NET/Actions docs. AFTER diagnosing any recurring failure, run `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` + `--audit-lessons`. Whenever a reusable workflow/tooling/guardrail improvement is spotted, classify via `plan_tool.py evolve` (`project-only` | `plugin-universal` | `dual-project+plugin`) & implement universalized core in `.agents/plugins/dynamic-fractal-planner/` + thin project wiring automatically.
- Live Phone Bridge v2 (`LiveDiagnosticBridgeService` + `LiveBridgeWakeService` + `LiveBridgeAuthActivity` + `cloudflare_live_relay_worker.js` v2): supports background standby wake w/ HMAC-SHA256 signature + native Android `BiometricPrompt` fingerprint approval + E2E encrypted session token (`SHA256-CTR + HMAC-SHA256`) + 31 Tier 0/1/2 commands + `batch` + dynamic settings inspection/mutation (`live_phone_bridge.py --wake --pair-secret ...` or `--code CAV-...`).
- If shell/Python available: run `python3 .agents/scripts/check_android_cs.py` (Android C#), `python3 -B .agents/scripts/error_learner.py --audit-lessons`, `python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast` (when plugin edited), & `python3 .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --git-stat` (`--build --git-stat` on code); if no shell: manually verify balanced braces, C# 8.0 rules, EN/PT-BR `AndroidStrings.cs` key parity, & zero syntax/binding regressions.
- Check `.agents/docs/EXTRA_REQUESTS.md` before finalizing edits.
- Keep `ApplicationDisplayVersion` major.minor prefix synced (`CavernizeAndroid.csproj` & `.github/workflows/BuildAndroid.yml`); NEVER delete or overwrite prior versioned GitHub Releases.
- Reply to user in **PT-BR**; write all agent/memory/sync `.md` files in lossless symbolic telegraphic EN.

## 4. Mandatory Sync-Back, Self-Updating Context & User Push Handoff (Before Ending Any Edit Turn)
1. **Self-Updating Context Check:** If this turn changed persistent workflows, tools, paths, preview shim, diagnostics, or invariants -> update `AGENTS.md` (Sec 0/3/5) and/or `.agents/docs/EXTERNAL_AI_ONBOARDING.md` in-place (zero hardcoded ephemeral versions/commit hashes).
2. Prepend entry at top of matching section(s) in `.agents/docs/EXTERNAL_AI_SYNC.md` (`## Cavern Project Sync Entries (Newest First)` for project edits; `## Plugin (dynamic-fractal-planner) Sync Entries (Newest First)` AND `.agents/plugins/dynamic-fractal-planner/.agents/docs/{EXTERNAL_AI_SYNC,PROJECT_HISTORY,HANDOFF}.md` for plugin edits; use `pending-push` for commit id since user pushes manually):
   `- <YYYY-MM-DD> | <pending-push|commit> | <scope> | <files> | <change_or_root_cause> | checks:<manual|cmds> | ci_apk:<needed|skip> | next:<pending>`
3. Prepend 1-4L milestone at top of Section 3 in `.agents/docs/PROJECT_HISTORY.md` (keep file `<= 150L`; archive older entries -> `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`).
4. Update `.agents/docs/HANDOFF.md` (`SUMMARY`, `NEXT`, edited files).
5. End every edit reply to user with:
   - Exact list of modified files.
   - Ready-to-copy conventional commit message (`docs(...)` / `chore(...)` / `fix(...)` / `feat(...)`); **MUST append `[skip ci]` for docs/chore/preview-only changes**, and **omit `[skip ci]` when C#/C++/csproj/Android/CI code changed so `Build Android APK` runs**.
   - Explicit instruction telling the user whether to click Push and trigger/watch `Build Android APK` in GitHub Actions.
