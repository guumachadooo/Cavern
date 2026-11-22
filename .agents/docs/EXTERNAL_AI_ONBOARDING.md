# External AI Onboarding & Sync Protocol (`Cavern` / `CavernizeAndroid`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 0. Platform Constraints (CRITICAL)
- Repo stack is **C# / .NET (`netstandard2.1` + `net11.0-android`) + C/C++**. IGNORE any host IDE **React/JS** preset -> NEVER create `package.json`, `node_modules/`, `src/*.tsx|jsx`, or web boilerplate.
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
- Repo-local fractal planner scripts live at `.agents/plugins/dynamic-fractal-planner/scripts/` (substitute for `~/.gemini/config/plugins/...` paths in plugin docs).
- NEVER create or commit `.metrics.jsonl`, `.model_modes.json`, `.diagnostics*`, `.hook_state.json`, or `__pycache__/` (gitignored).
- Core (`Cavern/`, `Cavern.Format/`) -> `netstandard2.1` strict C# 8.0 (`!is not`, `!new()`, `!file-scoped namespace`); zero fake DSP (ISO/ITU/EBU physics & measured datasets only); document all new `public` types/members/enums w/ `/// <summary>` (`!CS1591`) & initialize spec fields (`!CS0649`).
- Android (`CavernSamples/CavernizeAndroid/`) -> current Android TFM in `.csproj`; keep `targetSdkVersion >= 35` + `extractNativeLibs="true"` + W^X (`NativeLibs/arm64-v8a/`); maintain `AndroidStrings.cs` 1:1 EN/PT-BR key parity; respect `.NET Android` binding pitfalls (`.agents/docs/BUILD_LESSONS.md` `BL-003`..`BL-005` & `PROJECT_HISTORY.md` Sec 2).
- Dynamic Zero-Warning & Error-Learning Loop -> BEFORE touching OS-versioned Android APIs or `.github/workflows/*.yml`, read `.agents/docs/BUILD_LESSONS.md`, fetch live CI diagnostics (`python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`), & verify current official Android/.NET/Actions docs (never rely on static/dated version memory). AFTER any new recurring build/CI failure is diagnosed, run `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` (or `--from-log`/`--record`) + `--audit-lessons` so future sessions inherit a **version-agnostic** lesson w/ dynamic lookup guidance.
- If shell/Python available: run `python3 .agents/scripts/check_android_cs.py` (Android C#), `python3 -B .agents/scripts/error_learner.py --audit-lessons` (when editing `BUILD_LESSONS.md`), & `python3 .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --git-stat` (`--build --git-stat` on code); if no shell: manually verify balanced braces, C# 8.0 rules, EN/PT-BR `AndroidStrings.cs` key parity, & zero syntax/binding regressions.
- Check `.agents/docs/EXTRA_REQUESTS.md` before finalizing edits.
- Keep `ApplicationDisplayVersion` major.minor prefix synced (`CavernizeAndroid.csproj` & `.github/workflows/BuildAndroid.yml`); NEVER delete or overwrite prior versioned GitHub Releases.
- Reply to user in **PT-BR**; write all agent/memory/sync `.md` files in lossless symbolic telegraphic EN.

## 4. Mandatory Sync-Back & User Push Handoff (Before Ending Any Edit Turn)
1. Prepend entry at top of `.agents/docs/EXTERNAL_AI_SYNC.md` (use `pending-push` for commit id since user pushes manually):
   `- <YYYY-MM-DD> | <pending-push|commit> | <scope> | <files> | <change_or_root_cause> | checks:<manual|cmds> | ci_apk:<needed|skip> | next:<pending>`
2. Prepend 1-4L milestone at top of Section 3 in `.agents/docs/PROJECT_HISTORY.md` (keep file `<= 150L`; archive older entries -> `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`).
3. Update `.agents/docs/HANDOFF.md` (`SUMMARY`, `NEXT`, edited files).
4. End every edit reply to user with:
   - Exact list of modified files.
   - Ready-to-copy conventional commit message (`docs(...)` / `fix(...)` / `feat(...)`); **MUST append `[skip ci]` for docs/chore-only changes**, and **omit `[skip ci]` when code changed so `Build Android APK` runs**.
   - Explicit instruction telling the user whether to click Push and trigger/watch `Build Android APK` in GitHub Actions.
