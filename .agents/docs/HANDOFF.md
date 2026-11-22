# Session Handoff
ts=1795372800|branch=master|last_code=pending-push|dirty=1|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W37.c
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md (partitioned: Cavern Project + Plugin) && .agents/plugins/dynamic-fractal-planner/.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md (auto-booted via root `AGENTS.md` `## 0`; AI self-updates on workflow/invariant changes)
BUILD_LESSONS|.agents/docs/BUILD_LESSONS.md (universalized via plugin `v2.6.0` `error_learner.py`: `python3 -B .agents/scripts/error_learner.py --fetch-live-ci --apply` & `--audit-lessons`)
DEVICE_LOG|.agents/device_logs/cavernize_log.txt (live GitHub via `fetch_live_diagnostics.py --all`; Live Bridge v2 via `live_phone_bridge.py --url <worker> --wake --pair-secret <64hex> --tier <0|1|2> --cmd <cmd>` OR `--code <CAV-...>`)
SUMMARY|W37.c: embedded auto-loaded self-updating External AI / AI Studio boot context in root `AGENTS.md` (`## 0`) + synced `EXTERNAL_AI_ONBOARDING.md`; added zero-dependency Node.js preview shim (`package.json` + `.agents/preview/{server,dashboard,dashboard.test}.mjs`, port `3000`, zero `node_modules/`) displaying live workspace status & context docs to stop automatic `"The dev server didn't start, please fix it"` prompts while keeping C#/.NET/Android untouched
NEXT|user pushes W37.c (`[skip ci]` if only docs/preview; omit `[skip ci]` if bundled with pending W37.b code); new AI Studio chats now auto-load context from `AGENTS.md` and boot port 3000 cleanly; primary AI runs `git fetch && git pull --rebase` on return
