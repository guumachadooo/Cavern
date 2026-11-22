# External AI Onboarding & Sync Protocol (`Cavern` / `CavernizeAndroid`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 1. Read-First Map (Mandatory Boot Order)
1. `AGENTS.md` (repo root) -> permanent invariants, SELinux/W^X, C# 8.0 strictness, coding style, OTA versioning, Termux/CI build rules.
2. `CavernSamples/CavernizeAndroid/AGENTS.md` -> Android UI, One UI 9, & audio rules.
3. `.agents/plugins/dynamic-fractal-planner/rules/AGENTS.md` + `.agents/plugins/dynamic-fractal-planner/skills/*/SKILL.md` + `.agents/planner_profile.json` -> fractal planner rules, token diet, & verification gates.
4. All 4 repo skills in `.agents/skills/*/SKILL.md`:
   - `cavernize-device-debug` | `cavernize-build-and-deliver` | `cavernize-ui-oneui9` | `cavernize-vocal-cortex`
5. `.agents/docs/` -> `EXTERNAL_AI_SYNC.md`, `HANDOFF.md`, `PROJECT_HISTORY.md` (`L1-60` slice only), `PHASE2_ROADMAP.md`, `PHASE3_VOCAL_CORTEX_ROADMAP.md`, `ONEUI9_REFERENCE.md`, `SAVED_IDEAS.md`, `EXTRA_REQUESTS.md`.

## 2. Device Logs (S25 Ultra Snapshot)
- Path -> `.agents/device_logs/cavernize_log.txt` (snapshot of `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`).
- Token diet -> run `python3 .agents/plugins/dynamic-fractal-planner/scripts/log_digest.py .agents/device_logs/cavernize_log.txt` (add `--errors-only` if large) BEFORE reading raw slices.
- Follow `.agents/skills/cavernize-device-debug/SKILL.md` for SELinux/FFmpeg/AAudio/SystemUI diagnosis.

## 3. Plugin & Protocol Standards
- Repo-local fractal planner scripts live at `.agents/plugins/dynamic-fractal-planner/scripts/` (substitute for `~/.gemini/config/plugins/...` paths in plugin docs).
- NEVER commit `.metrics.jsonl`, `.model_modes.json`, `.diagnostics*`, `.hook_state.json`, or `__pycache__/` (gitignored).
- Core (`Cavern/`, `Cavern.Format/`) -> `netstandard2.1` strict C# 8.0 (`!is not`, `!new()`, `!file-scoped namespace`); zero fake DSP (ISO/ITU/EBU physics & measured datasets only).
- Android (`CavernSamples/CavernizeAndroid/`) -> `net11.0-android`; `targetSdkVersion >= 35` + `extractNativeLibs="true"` + W^X (`NativeLibs/arm64-v8a/`); `AndroidStrings.cs` EN/PT-BR 1:1 key parity; check w/ `python3 .agents/scripts/check_android_cs.py`.
- Builds -> sequential ONLY (`-m:1 /p:UseSharedCompilation=false`), never parallel `dotnet build`.
- Pre-push verify -> run `python3 .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --git-stat` (or `--build --git-stat` on code changes) && check `.agents/docs/EXTRA_REQUESTS.md`.
- Reply to user in **PT-BR**; keep agent/memory/plan `.md` files in lossless symbolic telegraphic EN.

## 4. CI & Versioning Rules (`.github/workflows/BuildAndroid.yml`)
- `.github/workflows/BuildAndroid.yml` triggers on EVERY `master` push -> MUST append `[skip ci]` to docs/chore-only commit messages.
- Omit `[skip ci]` ONLY on code pushes requiring a new signed APK build.
- Keep `ApplicationDisplayVersion` major.minor prefix synced (`CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj` & `BuildAndroid.yml`); NEVER delete or overwrite prior versioned GitHub Releases.

## 5. Mandatory Sync-Back Ledger (On Every `git push` to `master`)
1. Prepend entry at top of `.agents/docs/EXTERNAL_AI_SYNC.md`:
   `- <YYYY-MM-DD> | <commit> | <scope> | <files> | <change_or_root_cause> | verified:<cmds> | ci_apk:<ran|skip> | next:<pending>`
2. Prepend 1-4L milestone at top of Section 3 in `.agents/docs/PROJECT_HISTORY.md` (keep file `<= 150L`; archive older entries -> `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`).
3. Update `.agents/docs/HANDOFF.md` w/ latest commit hash, modified files, & remaining work.
4. Keep commits atomic w/ conventional prefixes (`fix(...)`, `feat(...)`, `docs(...)`, `chore(...)`).
