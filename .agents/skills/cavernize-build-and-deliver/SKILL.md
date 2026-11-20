---
name: cavernize-build-and-deliver
description: >-
  Builds shared Cavernize .NET libraries locally in Termux/PRoot, keeps APK version numbers
  synchronized between CavernizeAndroid.csproj and GitHub Actions, triggers and monitors the
  Android APK release build, and delivers the signed APK to the workspace and /sdcard/Download.
  Use when compiling, committing, releasing, or updating Cavernize-Android.apk.
---

# Build and Delivery Workflow for CavernizeAndroid

Follow these steps in order whenever shipping or testing changes to `CavernizeAndroid`.

## 0. Check Extra Parallel Requests First (`.agents/docs/EXTRA_REQUESTS.md`)
Before running final checks, committing, pushing, or building the APK, **always read** [EXTRA_REQUESTS.md](../../docs/EXTRA_REQUESTS.md):
- If `## Pending Extra Requests` has any pending items (added from parallel `/btw` chats while you worked), **stop before committing/building**, create a new staged plan, implement and verify every extra item as if it were a fresh conversation, move them to `## Completed Requests`, and only then proceed to Step 1.

## 1. Local Pre-Check in Termux / PRoot ARM64
This environment has `.NET 8` at `/root/.dotnet/dotnet` without the Android workload (`net11.0-android` builds run on GitHub Actions). Validate shared logic (`Cavernize.Logic`, `Cavern.Format`, `Cavern`) before committing:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 \
  /root/.dotnet/dotnet build CavernSamples/Reusable/Cavernize.Logic/Cavernize.Logic.csproj -c Release -m:1 /p:UseSharedCompilation=false
```

- Also audit C# syntax, `using` directives, Android API names (`Android.*`), and `AndroidManifest.xml` (`android:targetSdkVersion` **must remain `28`**) carefully before pushing so CI succeeds on the first try.

## 2. Sync Version for OTA Auto-Updater
[AppUpdateService.cs](../../../CavernSamples/CavernizeAndroid/Services/AppUpdateService.cs) checks GitHub release tag `android-latest` and compares versions numerically (`IsRemoteNewer`).

1. Check [CavernizeAndroid.csproj](../../../CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj) (`<ApplicationDisplayVersion>`) and [.github/workflows/BuildAndroid.yml](../../../.github/workflows/BuildAndroid.yml) (`ApplicationDisplayVersion` and release title).
2. Ensure the version produced by `BuildAndroid.yml` increases monotonically over previously published releases (keep minor prefix aligned, e.g. `X.Y.${{ github.run_number }}`), and ensure each public build creates its own individual `vX.Y.Z` GitHub Release in addition to updating `android-latest` (never delete older releases).

## 3. Update Project Memory (Feedback Loop) & Clean Commit
- If this release introduces new features, UX decisions, CI changes, or non-obvious bug fixes, **prepend** a concise 1–4 line summary under `## 3. Version History (Newest First)` in [PROJECT_HISTORY.md](../../docs/PROJECT_HISTORY.md) (and update [ONEUI9_REFERENCE.md](../../docs/ONEUI9_REFERENCE.md) in place or `AGENTS.md` if applicable) before committing.
- Check `git status` and review diffs (`git diff --stat`) before staging.
- Never force-push over collaborator/concurrent work or commit temporary logs/APKs unless already tracked (note `*.apk` is in `.gitignore`).
- Keep commits organized on `master` with clear messages and push to `origin/master`.

## 4. Monitor GitHub Actions (`Build Android APK`)
- Wait for the workflow triggered by the push to finish cleanly (`completed` + `success`), inspecting build logs via `gh run list` / `gh run view --log-failed` (or GitHub REST API if `gh` is unavailable).
- Only report the APK build as passing after observing the workflow complete with zero errors.

## 5. Deliver the Signed APK
Once the run succeeds, download the `Cavernize-APK` artifact (or `android-latest` release asset) and place it in both locations if accessible:
1. `/root/.gemini/antigravity/scratch/Cavern/Cavernize-Android.apk`
2. `/sdcard/Download/Cavernize-Android.apk`

Verify both files exist and check `ls -lh` + timestamps before reporting completion to the user.
