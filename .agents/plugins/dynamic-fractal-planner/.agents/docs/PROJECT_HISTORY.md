# Project History & Pitfalls (`PROJECT_HISTORY.md`)

> RULE|chronological archive ONLY (`SessionStart` injects live git/versions/queue) -> `!static("Current State"|"Current Version")`.
> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim `path:line`, `symbol()`, invariants).

---

## 1. Env & Stack
- Python 3 stdlib only (zero pip deps); tests: `python3 -B tests/run_plugin_tests.py --fast` (`--full` pre-release); verify: `python3 -B scripts/verify_universal.py --git-stat`.

## 2. Pitfalls & Invariants
- PITFALL|`planner_state.py:is_real_project_root` -> embedded `.agents/plugins/*` outside `~/.gemini` scanned as project -> always exclude `resolve_plugin_root().resolve()`.
- PITFALL|third-party crypto/HTTP imports -> break Termux/CI/sandbox hosts -> keep 100% stdlib (`hashlib`/`hmac`/`urllib`).

## 3. Version History (Newest First)
<!-- RULE: PREPEND 1-3L entries directly below (newest first). !static("Current State"). -->
<!-- FORMAT: - YYYY-MM-DD|<tag>|<scope/file:sym> <change> -> <effect> | verified:<cmd> -->
- 2026-11-22|v2.6.0|`scripts/{error_learner,live_diagnostics,live_bridge_client,self_evolve}.py` + `plan_tool.py` + `verify_universal.py` + `planner_session_start.py` + `.agents/docs/{EXTERNAL_AI_SYNC,HANDOFF,BUILD_LESSONS}.md` -> universalized timeless error learning, live GitHub diagnostics, biometric wake bridge client v2, autonomous dual-scope self-evolution & 1:1 standalone plugin sync ledgers | verified:`tests/run_plugin_tests.py --fast`
