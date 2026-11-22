# External AI Onboarding & Sync Protocol (`Cavern` / `CavernizeAndroid`)

## 1. Read These First (Mandatory)
1. `AGENTS.md` (repo root) — permanent invariants, SELinux/W^X, C# 8.0 strictness, coding style, OTA versioning, Termux/CI build rules.
2. `CavernSamples/CavernizeAndroid/AGENTS.md` — Android UI/audio rules.
3. `.agents/plugins/dynamic-fractal-planner/rules/AGENTS.md` & `.agents/planner_profile.json` — fractal planner rules, token diet, verification gates.
4. `.agents/docs/HANDOFF.md` & `.agents/docs/PROJECT_HISTORY.md` (`L1-60`) — current state & recent milestones.
5. `.agents/device_logs/cavernize_log.txt` — latest Android device runtime logs.

## 2. Workflow & Verification Protocol
- **Mode:** Follow `AGENTS.md` + `dynamic-fractal-planner` (`plan_tool.py` / `verify_universal.py` in `.agents/plugins/dynamic-fractal-planner/scripts/`).
- **C# Core (`Cavern/`, `Cavern.Format/`):** `netstandard2.1`, strict C# 8.0 (no `is not`, no target-typed `new()`, no file-scoped namespaces). Zero fake DSP.
- **Android (`CavernSamples/CavernizeAndroid/`):** `net11.0-android`. Run `python3 .agents/scripts/check_android_cs.py` if editing Android C#. Keep `targetSdkVersion >= 35`, `extractNativeLibs="true"`, and native libs in `NativeLibs/arm64-v8a/`.
- **Builds:** Run sequentially (`-m:1 /p:UseSharedCompilation=false`). Final signed APK is built by `.github/workflows/BuildAndroid.yml` on GitHub Actions.
- **Verify before commit/push:** Run `python3 .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --git-stat` (or `--build --git-stat` for code changes).
- **Extra Requests:** Check `.agents/docs/EXTRA_REQUESTS.md` before committing/pushing.

## 3. Recording Changes for Sync Back (Mandatory)
When you make changes and `git push` to `master`:
1. Prepend a 1-4 line milestone at the top of Section 3 in `.agents/docs/PROJECT_HISTORY.md` (keep file `<= 150L`).
2. Update `.agents/docs/HANDOFF.md` with your latest commit hash, changed files, and summary of what was done + what remains.
3. Keep commits atomic with conventional prefixes (`fix(...)`, `feat(...)`, `chore(...)`) so the primary AI can `git fetch && git pull --rebase` (or `git diff`) cleanly on return.
