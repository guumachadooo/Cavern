# External AI Sync Ledger (`EXTERNAL_AI_SYNC.md`)

> PROTOCOL|External AI prepends 1 entry per `git push` to `master` (newest first). Primary AI runs `git fetch origin && git pull --rebase` & reads top entries on session resume.
> FORMAT|`- <YYYY-MM-DD> | <commit> | <scope> | <files> | <change_or_root_cause> | verified:<cmds> | ci_apk:<ran|skip> | next:<pending>`

## Sync Entries (Newest First)
- 2026-11-22 | base=c438fe8d (code=e6ad0a26) | handoff | .agents/device_logs/cavernize_log.txt, .agents/plugins/dynamic-fractal-planner/, .agents/docs/EXTERNAL_AI_ONBOARDING.md | bundled 181KB S25U log + fractal plugin v2.5.1 + onboarding/sync protocol after Matrix 7.1 FLAC silence & FFmpeg/probe log fixes | verified:verify_universal.py | ci_apk:skip | next:analyze cavernize_log.txt & fix reported issues
