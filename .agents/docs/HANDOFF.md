# Session Handoff
ts=1795400000|branch=master|last_code=pending-push|dirty=1|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W44.b
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md (partitioned: Cavern Project + Plugin) && .agents/plugins/dynamic-fractal-planner/.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md (auto-booted via root `AGENTS.md` `## 0`; AI self-updates on workflow/invariant changes)
BUILD_LESSONS|.agents/docs/BUILD_LESSONS.md (`BL-001`..`BL-017`; universalized via plugin `v2.7.0` `error_learner.py` + `verify_universal.py --all` + 7-layer `check_android_cs.py --ci-parity --strict-warnings`)
DEVICE_LOG|Cloudflare Unified Hub v6 Hybrid SQLite Durable Objects (`HUB_DO` + `RELAY_KV` fallback) + OmniAgent v2.0.0 (`cf_deploy_worker`, instant overlay, same-signature Cavernize IPC) + GitHub `diagnostics` CI-only
SUMMARY|W44.b unified CI releases so `BuildAndroid.yml` builds & attaches BOTH `Cavernize-Android.apk` (`v2.9.<run>`) and `OmniAgent-Android.apk` (`v2.0.<run>`, monotonic epoch-minute `versionCode`) side-by-side inside `v2.9.<run>` & rolling `android-latest` Releases (plus `omniagent-latest` mirror), hardened `AppUpdateService.cs` to strictly pick `Cavernize-Android.apk` first & exclude `OmniAgent*.apk` on fallback, && converted `BuildOmniAgent.yml` to manual `workflow_dispatch` fast-build.
NEXT|user pushes W44.b (NO `[skip ci]`) -> single `Build Android APK` run publishes BOTH APKs together in the main `android-latest` Release -> user configures Cloudflare API Token once in OmniAgent -> Deploy CF -> AI runs `omni_agent.py deploy-worker` for v6 SQLite DO

