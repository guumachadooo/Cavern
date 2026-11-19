# Project Structural Guidelines (Cavern / CavernizeAndroid)

This repository is the personal fork (`guumachadooo/Cavern`) focused on the Android app **Cavernize** (`CavernSamples/CavernizeAndroid`, package `hu.sbence.cavern.cavernize`), running the full **Cavern** spatial audio engine on mobile.

## 1. Language & Communication
- **Always reply to the USER in the same language they wrote in**, concisely and objectively.
- **Keep all internal agent files** (`AGENTS.md`, `.agents/docs/*`, `.agents/skills/*`, `.agents/hooks/*`) in **English** so LLMs parse technical instructions cleanly (without translating UI strings in `AndroidStrings.cs` or literal device paths).
- Use clickable Markdown links with the `file://` scheme whenever referencing any file or line range.
- Report only file paths, symbols, implemented behavior, and command/build results directly observed in tool output or read files during the current session.

## 2. Instruction & Memory Loading Order (How New Chats Boot)
When a new session starts, context is loaded in strict layers so rules never duplicate or go stale:

| Layer | File / Mechanism | When Loaded | Purpose |
| :--- | :--- | :--- | :--- |
| **1. Permanent Root Rules** | `AGENTS.md` (this file) | **Always** at startup (`<user_rules>`) | Immutable project invariants, critical rules, language policy, and memory structure |
| **2. Live Runtime Briefing** | Global plugin `SessionStart` + `.agents/planner_profile.json` | **Auto-executed** at startup | **Single source of truth for live state:** git branch, dirty files, `csproj` & CI versions, recent commits, Termux `.NET` flags, Downloads APK size, device log path, and `EXTRA_REQUESTS.md` pending items |
| **3. Path-Scoped Rules** | `CavernSamples/CavernizeAndroid/AGENTS.md` | **Auto-loaded** when working in `CavernizeAndroid/` | Android UI partial map, custom views/services, One UI 9 layout & anti-clipping guards |
| **4. Declarative Guardrails** | Global `dynamic-fractal-planner` hooks reading `.agents/planner_profile.json` | **Auto-executed** on `PreInvocation`, `PreToolUse`, `PostToolUse`, `Stop` | Enforces dynamic planning, `targetSdkVersion="28"`, Termux `.NET` flags/scope, `EXTRA_REQUESTS.md` gate, `PROJECT_HISTORY.md` reminders, and pre-finish verification |
| **5. Task Skills & Global Plugin** | `.agents/skills/*/SKILL.md` + global `dynamic-fractal-planner` plugin | Descriptions in `<skills>`; body loaded on demand | Cavern runbooks (`cavernize-build-and-deliver`, `cavernize-ui-oneui9`, `cavernize-device-debug`) + global `dynamic-planner` & `project-memory` |
| **6. On-Demand Reference Docs** | `.agents/docs/PROJECT_HISTORY.md`, `ONEUI9_REFERENCE.md`, `SAVED_IDEAS.md`, `EXTRA_REQUESTS.md` | **Read on demand** (or before shipping for `EXTRA_REQUESTS.md`) | Reverse-chronological changelog & pitfalls, One UI/APK specs, shelved ideas/code, parallel `/btw` queue |

## 3. Original Feature Preservation
- **Preserve 100% of original Cavernize capabilities** (rendering to multiple speaker targets/layouts, Matrix and Cavernize 3D upmixing, HRIR calibration and QuickEQ filters, separate channel/object/stem extraction and `.json` reports, batch render queue, codec metadata, post-render reports, delay/quality/CLI options) alongside the mobile spatial headphone engine and Android media player.
- **Pre-Android reference history:** Early commits prior to the Android port still contain original desktop Cavern/PC files; search `git log` if an original desktop reference is needed.
- **External binary decoders:** Codecs relying on external binaries on Android (`FFmpeg` NDK build + shared libs in `FilesDir/bin/` and official `truehdd` `aarch64-unknown-linux-musl` in `FilesDir/bin/truehdd` for Dolby TrueHD / Atmos TrueHD) must use the managed download/execution services inside `FilesDir`, preserving temp cache cleanup and safe fallbacks.

## 4. Critical Rules ("Never Break")
1. **NEVER change `android:targetSdkVersion="28"`** in `CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml`. This keeps the app in the `untrusted_app_27` SELinux domain so downloaded native binaries (`ffmpeg` and `truehdd`) can execute from `FilesDir`.
2. **Local builds in Termux / Ubuntu PRoot (ARM64):** No Android APK packaging workload is installed locally (signed `.apk` builds run in GitHub Actions). Whenever compiling `Cavernize.Logic`, `Cavern.Format`, or `Cavern` locally with `/root/.dotnet/dotnet`, you **must** pass these memory/globalization env vars and single-proc flags:
   ```bash
   DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 /root/.dotnet/dotnet build <proj> -m:1 /p:UseSharedCompilation=false
   ```
   For `CavernSamples/CavernizeAndroid/*.cs`, run `python3 .agents/scripts/check_android_cs.py` (cached `Microsoft.Android.Ref.34` + Roslyn 4.12 shadow type-check in `/tmp`, catching C# errors in seconds before CI packaging).
3. **Synced OTA Versioning + Preserve All GitHub Releases:** `CavernizeAndroid` checks updates on GitHub release tag `android-latest` using numeric comparison (`AppUpdateService.IsRemoteNewer`). Keep `ApplicationDisplayVersion` synchronized between `CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj` and `.github/workflows/BuildAndroid.yml` (never regress or reuse a public version). Alongside updating the `android-latest` OTA release, **always create an individual versioned GitHub Release (`vX.Y.Z`) for every public build — never delete or overwrite prior versioned releases**.
4. **APK Delivery:** The final signed APK produced by `.github/workflows/BuildAndroid.yml` must be saved to `/root/.gemini/antigravity/scratch/Cavern/Cavernize-Android.apk` and copied to `/sdcard/Download/Cavernize-Android.apk` when accessible on the user's device.
5. **Coding Style & Core Safeguards:** Follow `CavernSamples/Coding style.txt` (opening braces on the same line, `PascalCase` for methods/properties, `camelCase` for variables, clear XML summaries). Preserve existing fixes such as the pure C# lossless FLAC fallback (`FlacAudioConverter.cs`) and complete logging for FFmpeg and `truehdd`.
6. **Dynamic Fractal Planning & Strict Verification:** Powered globally by `~/.gemini/config/plugins/dynamic-fractal-planner/` configured with Cavern's v3 `.agents/planner_profile.json` (`planner_mode: "lean"` default; override with `PLANNER_MODE=strict`):
   - **Lean vs Strict:** In `lean` mode, `PreInvocation` injects auto-REQs and domain JIT checklists directly without blocking edits (`Blast Radius Guard` asks on 4th code file in `Tier <= 1`). In `strict` mode, lock every `REQ-01..REQ-N` first (`plan_tool.py init`) and close via `plan_tool.py finish --evidence "..."`.
   - **Tiers 0–3:** `Tier 0 (DIRECT)` for trivial fixes; `Tier 1 (STANDARD)` for 1–2 edits; `Tier 2 (META_MODULES)` for `>= 3` requirements or `>= 2` subsystems; `Tier 3 (FRACTAL_DEEP)` for single high-complexity tasks (`MOD-DISCOVERY -> MOD-CONTRACT -> MOD-IMPL-* -> MOD-HARDEN -> MOD-AUDIT`). Keep discovery lean (`read_guard` symbol maps + bounded slices `<= 250L`).
   - **End Verification:** Run `python3 ~/.gemini/config/plugins/dynamic-fractal-planner/scripts/verify_universal.py --build` when compiled C# files changed (Stop auto-runs fast static checks when only `needs_verify` is active).
7. **Mandatory Disclosure Before Guessing on APKs/Packages:** Whenever the user asks to analyze/decompile an APK or extract exact system behavior and **the package/resource cannot be accessed or extracted cleanly**, **NEVER silently guess or improvise** — inform the user first, explain what was blocked/missing, and ask whether they want to supply the APK/capture, try another approach, or authorize an approximation.
8. **User-Facing Documentation in `README.md`:** Whenever adding a new user-facing feature (new screens, playback/library/spatial capabilities), update the matching section in root `README.md`. Pure bug fixes or internal One UI visual refinements do not need new entries.
9. **Archived Good Ideas (`.agents/docs/SAVED_IDEAS.md`):** Whenever an interesting visual idea, shader, or animation is deferred or replaced (e.g., to stay faithful to another UI spec), archive the concept and full reference snippet in `.agents/docs/SAVED_IDEAS.md`.
10. **Parallel Extra Request Queue (`.agents/docs/EXTRA_REQUESTS.md`):** Before finishing any task, running `git commit`/`git push`, or triggering an APK build, **always read** `.agents/docs/EXTRA_REQUESTS.md`. If any items exist under `## Pending Extra Requests` (queued from parallel `/btw` threads while you worked), **stop before committing/building**, formulate a new staged plan as if it were a fresh request, implement and verify everything, move resolved items to `## Completed Requests` (leaving the pending section empty), and only then ship and build the APK.

## 5. Memory Maintenance & Anti-Rot Rules
Keep internal documentation organized so sections never contradict each other as versions evolve:
- **Never hardcode "Current Version" or "Current State" labels in static Markdown docs.** Live version and working tree status come dynamically from the global `SessionStart` hook, `CavernizeAndroid.csproj`, and `BuildAndroid.yml`.
- **`.agents/docs/PROJECT_HISTORY.md` (Newest First):** Whenever solving a non-obvious bug, learning a new environment constraint, or shipping a UX/version milestone, **prepend** a concise 1–4 line summary at the **top** of `## 3. Version History (Newest First)` and place reusable engineering pitfalls under `## 2. Technical Lessons & Known Pitfalls`. Consolidate very old milestones at the bottom if the file gets long.
- **`.agents/docs/ONEUI9_REFERENCE.md` (Component-Organized):** Update the matching native UI/APK component section **in place** when refining One UI / Apple Music / Samsung Media specs instead of appending contradicting version blocks at the bottom.
- **`AGENTS.md` (Root & Android):** Edit only when a **new permanent invariant** ("never break") or structural module is added — never turn temporary layout preferences into permanent rules.
- **`.agents/skills/` & `.agents/planner_profile.json`:** Update immediately whenever build pipelines, tool paths, or SDK constraints change.

## 6. Quick Code Map
- `Cavern/` and `Cavern.Format/`: spatial DSP engine, container/codec readers, and writers.
- `CavernSamples/Reusable/Cavernize.Logic/`: cross-platform shared logic (`RealtimePlayerEngine`, `StemExtractor`, `FlacAudioConverter`, `CavernizeOutput`, `MergeToContainer`, models, and localization strings).
- `CavernSamples/CavernizeAndroid/`: full `.NET` Android app (`net11.0-android`, native Android APIs without external AndroidX; see `CavernSamples/CavernizeAndroid/AGENTS.md` for detailed partial/service map).
- `.github/workflows/BuildAndroid.yml`: official CI build, persistent-keystore signing, and GitHub Releases publishing workflow (`android-latest` + `vX.Y.Z`).
- `.agents/`: Pure declarative project profile (`planner_profile.json`), Cavern skills (`skills/`), and project docs (`docs/`), executed universally by the global `~/.gemini/config/plugins/dynamic-fractal-planner/` plugin.
- Device logs & read-only APK inspection: `/sdcard/Download/Cavernize/Logs/cavernize_log.txt`; follow `.agents/skills/cavernize-device-debug/SKILL.md` strictly within SELinux/sandbox boundaries.
