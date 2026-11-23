# External AI Sync Ledger — `dynamic-fractal-planner` Plugin (`EXTERNAL_AI_SYNC.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim `path:sym`).
> PURPOSE|Dedicated standalone change-tracking ledger for the universal `dynamic-fractal-planner` plugin (1:1 parity w/ host repo `.agents/docs/EXTERNAL_AI_SYNC.md`). Any AI editing plugin scripts, hooks, rules, skills, or tests MUST prepend an entry here AND in host repo `EXTERNAL_AI_SYNC.md` Plugin section.

---

## Plugin Sync Entries (Newest First)

### 2026-11-23 | `pending-push` | `AI Studio` | Plugin v2.8.0 — Universal SOTA First-Principles Pre-Flight (`MOD-SOTA-PREFLIGHT`), Lens Truncation Fix & Byte-Bloat Guard
- `Scope`: `dual-project+plugin` (`plugin.json`, `CHANGELOG.md`, `rules/AGENTS.md`, `skills/dynamic-planner/SKILL.md`, `scripts/complexity_triage.py`, `hooks/planner_pre_invocation.py`, `scripts/verify_universal.py`, `tests/run_plugin_tests.py`)
- `Summary`: Baked SOTA first-principles architectural brainstorming directly into Tier 3 triage (`MOD-SOTA-PREFLIGHT` in `complexity_triage.py` + `SOTA_PREFLIGHT|` injection in `planner_pre_invocation.py`, suppressed under ECO/tight budget) so complex features automatically falsify seeded assumptions, run Physics-vs-AI tournaments, split execution planes, and validate via offline golden benches before coding; raised `_compact_domain_lenses` cap `220 -> 420` chars fixing silent mid-sentence lens truncation; added byte-bloat warnings (`PROJECT_HISTORY.md > 28 KB`, `EXTERNAL_AI_SYNC.md > 48 KB`) to `verify_universal.py:check_memory_weight()`; added regression unit test in `tests/run_plugin_tests.py`.
- `Files`: `plugin.json`, `CHANGELOG.md`, `rules/AGENTS.md`, `skills/dynamic-planner/SKILL.md`, `scripts/complexity_triage.py`, `hooks/planner_pre_invocation.py`, `scripts/verify_universal.py`, `tests/run_plugin_tests.py`, `.agents/docs/{EXTERNAL_AI_SYNC.md,PROJECT_HISTORY.md,HANDOFF.md}`

### 2026-11-22 | `pending-push` | `AI Studio` | Plugin v2.7.0-r1 — Live Bridge Client Session Persistence Callback & Distinct HTTP 404 Expired Exit Code
- `Scope`: `dual-project+plugin` (`scripts/live_bridge_client.py`)
- `Summary`: Added `EXIT_SESSION_EXPIRED = 4` (HTTP 404 `sessionExpired` propagation vs transient timeout) and `on_session_acquired(token, ttl_sec)` callback invoked immediately after `--wake` approves BEFORE executing `--cmd`, preventing wrapper vaults from losing a freshly biometric-approved session or wiping valid sessions on network timeouts.
- `Files`: `scripts/live_bridge_client.py`, `.agents/docs/{EXTERNAL_AI_SYNC.md,PROJECT_HISTORY.md,HANDOFF.md}`

### 2026-11-22 | `pending-push` | `AI Studio` | Plugin v2.7.0 — Gitless Container Auto-Discovery, Deep C# Pre-Push Syntax Check & Live Bridge Acoustic/Perf CLI
- `Scope`: `dual-project+plugin` (`plugin.json`, `scripts/verify_universal.py`, `scripts/live_bridge_client.py`, `tests/run_plugin_tests.py`)
- `Summary`: Added `--all` + automatic gitless-container workspace file discovery (`discover_workspace_files`) in `verify_universal.py` so environments without `.git` never silently audit 0 files; added `check_csharp_syntax_deep` catching statement indexer trailing-comma syntax bugs (`CS1002`/`CS1513`) with zero false positives on collection initializers; documented `acoustic_audit`, `acoustic_probe`, and `ui_perf_audit` in `live_bridge_client.py`; added regression test in `tests/run_plugin_tests.py`.
- `Files`: `plugin.json`, `CHANGELOG.md`, `scripts/verify_universal.py`, `scripts/live_bridge_client.py`, `tests/run_plugin_tests.py`, `.agents/docs/{EXTERNAL_AI_SYNC.md,PROJECT_HISTORY.md,HANDOFF.md}`

### 2026-11-22 | `pending-push` | `AI Studio` | Plugin v2.6.0-r2 — Cloudflare Unified Telemetry Hub v3 & Live Bridge v3 Dynamic Client
- `Scope`: `dual-project+plugin` (`scripts/live_diagnostics.py`, `scripts/live_bridge_client.py`)
- `Summary`: Upgraded universal `scripts/live_diagnostics.py` with zero-dep Cloudflare Unified Telemetry Hub v3 client (`--cloudflare`, `--relay-url`, `--pair-secret`, `--history`, `derive_telemetry_headers` deriving `X-Device-Hash` + `X-Telemetry-Key`, seamless GitHub fallback for CI reports); upgraded `scripts/live_bridge_client.py` to v3 (`USER_AGENT/3.0`, `--pipeline` shorthand for `$prev` multi-step execution, updated Tier 2 unrestricted dynamic command docs).
- `Files`: `scripts/live_diagnostics.py`, `scripts/live_bridge_client.py`, `.agents/docs/{EXTERNAL_AI_SYNC.md,PROJECT_HISTORY.md,HANDOFF.md}`

### 2026-11-22 | `pending-push` | `AI Studio` | Plugin v2.6.0 — Universal Efficiency Engines, Dual Sync Ledger & Autonomous Self-Evolution
- `Scope`: `plugin-universal` (`v2.5.1` -> `v2.6.0`)
- `Summary`: Universalized 4 core project efficiency innovations into zero-dependency plugin engines: (1) `scripts/error_learner.py` (multi-stack Roslyn/TS/Rust/Clang/Actions timeless build-error learner + anti-obsolescence linter); (2) `scripts/live_diagnostics.py` (uncached GitHub Contents API diagnostics fetcher w/ profile + git remote auto-detection); (3) `scripts/live_bridge_client.py` (v2 live device/app bridge client supporting HMAC-SHA256 signed biometric background wake + Encrypt-then-MAC `SHA256-CTR + HMAC-SHA256` session unlock + arbitrary tiered cmds/batch/screenshot capture); (4) `scripts/self_evolve.py` (`MOD-SELF-EVOLVE` auto scope classifier `project-only`/`plugin-universal`/`dual-project+plugin` + dual-ledger sync writer). Wired `plan_tool.py` (`evolve`, `learn`, `live-diag`), `verify_universal.py` (auto `BUILD_LESSONS.md` timeless audit + plugin sync parity warn), `planner_session_start.py` (`EVOLVE|` directive + `BUILD_LESSONS.md`/`EXTERNAL_AI_SYNC.md` auto-discovery), and fixed embedded `is_real_project_root()` guard + M5 tight-budget threshold. Added standalone plugin parity docs (`EXTERNAL_AI_SYNC.md`, `HANDOFF.md`, `BUILD_LESSONS.md`).
- `Files`: `plugin.json`, `CHANGELOG.md`, `rules/AGENTS.md`, `scripts/error_learner.py`, `scripts/live_diagnostics.py`, `scripts/live_bridge_client.py`, `scripts/self_evolve.py`, `scripts/plan_tool.py`, `scripts/verify_universal.py`, `hooks/planner_state.py`, `hooks/planner_session_start.py`, `tests/run_plugin_tests.py`, `.agents/docs/{EXTERNAL_AI_SYNC.md,HANDOFF.md,BUILD_LESSONS.md,PROJECT_HISTORY.md}`
