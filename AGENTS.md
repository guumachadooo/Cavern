# Project Guidelines — Cavern / CavernizeAndroid (`AGENTS.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols/rules). Note: reply to USER in user's chat language; keep UI strings in `AndroidStrings.cs`.

## 1. Scope & Memory Layers
- Fork `guumachadooo/Cavern` -> Android **Cavernize** (`CavernSamples/CavernizeAndroid`, `hu.sbence.cavern.cavernize`) running full **Cavern** DSP + neural/pro-audio stack on mobile.
- Boot layers: (1) root `AGENTS.md` (permanent invariants) -> (2) global `dynamic-fractal-planner` `SessionStart` + `.agents/planner_profile.json` (`auto` mode; live git/versions/APK/logs/queue) -> (3) `CavernSamples/CavernizeAndroid/AGENTS.md` -> (4) declarative hooks (`PreInvocation`/`PreToolUse`/`PostToolUse`/`Stop`) -> (5) `.agents/skills/*/SKILL.md` + global skills -> (6) `.agents/docs/` (`PHASE2_ROADMAP.md`, `HANDOFF.md`, `PROJECT_HISTORY.md`, `ONEUI9_REFERENCE.md`, `SAVED_IDEAS.md`, `EXTRA_REQUESTS.md`, `archive/*`).

## 2. Feature & Reference Invariants
- Preserve 100% original Cavernize capabilities (multi-target speaker layouts, Matrix & Cavernize 3D upmixing, HRIR/QuickEQ, stem/object extraction + `.json`, batch queue, codec metadata, post-render reports, CLI/quality/delay) + mobile spatial/neural/pro-audio/player features.
- Desktop PC reference exists in pre-Android `git log` (`backup/cavern-pre-H-ab942fd6` / `archive/cavern-pre-H-ab942fd6` preserves pre-H Cavern history).
- External binaries (`FFmpeg`, `truehdd`, `ONNX Runtime`): pre-W23 execute inside `FilesDir/bin/` under `targetSdkVersion="28"`; W23 migrates execution to APK `NativeLibs/arm64-v8a/` (`ApplicationInfo.NativeLibraryDir`, W^X compliant).

## 3. Critical Rules ("Never Break")
1. **SELinux / `targetSdkVersion`:** Keep `android:targetSdkVersion="28"` in `CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml` (`untrusted_app_27` for `FilesDir/bin/`) **UNTIL W23** migrates `libffmpeg.so`/`libtruehdd.so`/`libonnxruntime.so` to `NativeLibraryDir` (in W23, atomically update this rule + `.agents/planner_profile.json` `file_guards` to enforce `targetSdkVersion >= 35` + W^X).
2. **Termux ARM64 PRoot `.NET` & Sequential Builds:** No Android APK workload locally (`BuildAndroid.yml` packages signed APK). Always run sequentially (NEVER parallel across subagents):
   `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 /root/.dotnet/dotnet build <proj> -m:1 /p:UseSharedCompilation=false`
   + `python3 .agents/scripts/check_android_cs.py` for `CavernizeAndroid/**/*.cs` + `gcc` `test_proaudio_native.c` for `Native/`.
3. **Strict C# 8.0 Core & Zero Fake DSP:** `Cavern/` & `Cavern.Format/` target `netstandard2.1` (strict C# 8.0: `!is not`, `!new()`, `!file-scoped namespace`). Zero fake DSP: pure ISO/ITU/EBU physics & measured datasets only.
4. **Synced OTA Versioning & Release Preservation:** Keep `ApplicationDisplayVersion` prefix synced (`CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj` & `.github/workflows/BuildAndroid.yml`); always publish individual `vX.Y.Z` GitHub Releases alongside `android-latest` (`!delete/!overwrite` prior versioned releases).
5. **APK Delivery:** Save signed CI APK -> `/root/.gemini/antigravity/scratch/Cavern/Cavernize-Android.apk` && `/sdcard/Download/Cavernize-Android.apk` (when writable).
6. **Coding Style (`CavernSamples/Coding style.txt`):** Same-line `{`, `PascalCase` methods/props, `camelCase` locals, XML summaries; preserve `FlacAudioConverter` lossless fallback & full FFmpeg/`truehdd`/ONNX logging.
7. **Planner & Verify (`dynamic-fractal-planner v2.5.1`):** `.agents/planner_profile.json` (`schema_version: 3`, `planner_mode: "auto"`, `wiring_gate`, `key_parity_rules`, `archive_docs`). Verify compiled/C changes via `python3 ~/.gemini/config/plugins/dynamic-fractal-planner/scripts/verify_universal.py --build --git-stat`.
8. **APK Disclosure Gate:** If requested APK/system inspection fails or is blocked, NEVER silently guess — disclose block & ask user first.
9. **User Docs (`README.md`) & Archived Ideas (`SAVED_IDEAS.md`):** Update `README.md` for user-facing features; archive deferred visual/shader/algo snippets in `.agents/docs/SAVED_IDEAS.md`.
10. **Extra Request Gate (`.agents/docs/EXTRA_REQUESTS.md`):** Always check before finishing, `git commit`, `git push`, or CI APK build; implement pending items -> move to `## Completed Requests` -> ship.

## 4. Anti-Rot & Memory Rules
- `!static("Current State"|"Current Version")` in Markdown docs (`SessionStart` injects live git/versions).
- `PROJECT_HISTORY.md`: `<=150L`; prepend 1-4L milestones at top of Section 3; move old entries -> `.agents/docs/archive/PROJECT_HISTORY_ARCHIVE.md`; NEVER rewrite past Cavern historical entries.
- `ONEUI9_REFERENCE.md`: update matching component sections in-place.
- `HANDOFF.md`: single compact handoff (`plan_tool.py handoff --roadmap .agents/docs/PHASE2_ROADMAP.md --wave W{N} --archive .agents/docs/archive/...`).
- Archive docs (`.agents/docs/archive/SPATIAL_ROADMAP.md`, `PROJECT_HISTORY_ARCHIVE.md`): bounded slices `<=120L` only.

## 5. Code Map
- `Cavern/` & `Cavern.Format/`: spatial DSP (`Virtualizer/`, `Filters/`, `Remapping/`, `Neural/`, `Restoration/`, `VocalChain/`, `ProAudio/`, `Spatial/`), format codecs (`IAMF`, `AmbisonicsHOA`, `ADM`, `E-AC-3`, `WAV`, `MKV`).
- `CavernSamples/Reusable/Cavernize.Logic/`: `RealtimePlayerEngine`, `StudioBatchExporter`, `StemExtractor`, `FlacAudioConverter`, `CavernizeOutput`, `MergeToContainer`, `Spatial/` slots/adapters/orchestrator/hub, `Models/`, `Language/`.
- `CavernSamples/CavernizeAndroid/`: `net11.0-android` UI (`MainActivity*.cs`), `Theme/`, `Views/`, `Audio/`, `Services/`, `Native/cavernize_proaudio/`, `Language/AndroidStrings.cs`, `Assets/`.
- `.github/workflows/BuildAndroid.yml`: NDK ARM64 build + 4-layer compile/shadow audit + persistent keystore signing + GitHub Releases.
- Device logs: `/sdcard/Download/Cavernize/Logs/cavernize_log.txt` (follow `.agents/skills/cavernize-device-debug/SKILL.md`).
