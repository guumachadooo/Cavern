# Session Handoff
ts=1795369200|branch=master|last_code=pending-push|dirty=1|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W36
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md
BUILD_LESSONS|.agents/docs/BUILD_LESSONS.md (auto-learner: `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` & `--audit-lessons`)
DEVICE_LOG|.agents/device_logs/cavernize_log.txt (live GitHub via `python3 -B .agents/scripts/fetch_live_diagnostics.py --all`; live phone bridge via `python3 -B .agents/scripts/live_phone_bridge.py --url <worker> --code <CAV-...> --cmd <cmd>`)
SUMMARY|W36: audited all 42 historical CI runs (`Build Android APK` #1–#39 + `Export Neural Audio` #1–#3) & commit fix history -> created timeless version-agnostic catalog `.agents/docs/BUILD_LESSONS.md` (`BL-001`..`BL-016`), stdlib `.agents/scripts/error_learner.py` + `test_error_learner.py` (`6/6` PASS, `--audit-lessons` PASS, live Run #39 `9/9` matched), CI `learned-lessons.md` -> `diagnostics:ci/latest-learned-lessons.md`, `fetch_live_diagnostics.py --lessons`, 4 timeless `planner_profile.json` `diff_rules`, `verify_universal.py` `LEARN_HINT|`, & boot-doc wiring (`AGENTS.md`, `CavernizeAndroid/AGENTS.md`, `EXTERNAL_AI_ONBOARDING.md`, `cavernize-build-and-deliver/SKILL.md`)
NEXT|user pushes W36 & monitors Build Android APK CI to confirm workflow + `diagnostics:ci/latest-learned-lessons.md` publication; optional next wave can clean Run #39's 9 remaining warnings (`4x CS0419`, `4x XA4301`, `1x XA1006`); primary AI runs git fetch && git pull --rebase on return
