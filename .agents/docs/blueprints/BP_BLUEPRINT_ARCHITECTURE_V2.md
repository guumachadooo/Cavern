# Blueprint Architecture v2: Tiered Token-Diet, Anti-Gaming Linter & O(1) File-Budget Archival (`BP_BLUEPRINT_ARCHITECTURE_V2`)

> STYLE|symbolic-EN

## 0. Meta & Lineage
BP_META|id=BP_BLUEPRINT_ARCHITECTURE_V2|format=full|domain_vec=software:1.00,biz-org:0.90,infra-ops:0.85|door=1-way|tier=T3|status=DONE|h1_wave=W1
HYGIENE_NOTE|W1-W3 shipped in dynamic-fractal-planner v2.19.0 + 10 prior DONE Cavern blueprints compacted into blueprints_done.tar.gz.
PARENT_ADR|BP_PLAN_GOVERNANCE_AND_CENTRAL_HUB

## 1. Value & Non-Goals
VALUE|Cut everyday T1/T2 blueprint token & subagent cost by `>=60%` (`~3,741 tok -> ~650 tok` on T1, `0` subagents), close `6` Popperian rubric gaming loopholes (where 1-word tautologies scored `100/100`), cut `hygiene_audit()` runtime by `>=50%` via single-pass linting, & cap active blueprint file growth at `O(1)` (`838 -> <=832` repo files, avoiding `850` warn / `999` AI Studio cap).
WARDLEY|Commodity=Python3 stdlib(`re`,`json`,`tarfile`,`pathlib`) | Product=`dynamic-fractal-planner` v2.18.0 (`blueprint_engine.py`, `complexity_triage.py`, `planner_pre_invocation.py`) | Custom=Tiered `mini|compact|full` Blueprint Grammar, Anti-Gaming Popperian Linter v2, Lossless `DONE` Tarball+Ledger Compactor (`O(1)` files), & Reuse-Before-Create Matcher.
NON_GOAL|Zero third-party packages, databases, or LLM-call hooks inside `blueprint_engine.py` (100% deterministic stdlib Python).
NON_GOAL|Zero breaking changes to `schema_version` (`1..5` fixtures & all 77 existing plugin tests stay 100% green) or C#/.NET Cavern DSP code.

## 2. Live SOTA & Blind Morphogenesis
AUDIT_EXISTING|Empirical audit of `14` Cavern blueprints (`854L`, `179.1 KB`, `~52,400 tok`, avg `13.1 KB`/`~3,741 tok`) + live probes exposed `10` defects (`D1–D10`):
- `D1–D5`: (D1) `T1/2-way` pays full `T3` tax (`4` subagents incl. `3 pro`, `8` sections); (D2) `10/14` (`71.4%`) `DONE` BPs stay as separate files pushing repo to `838/850` warn; (D3) 100% score inflation (all `14/14` + 1-word garbage pass `100/100`, `1,919c` max line, whole-line weasel bypass); (D4) `8.6%` (`15.8 KB`) repeated boilerplate (`EVOLVE_RULE` `14x`, lenses `25x`); (D5) split-brain triage (`"Muda o texto do botão"` forced BP; `StudioBatchExporter.cs` misclassified `ux-product`).
- `D6–D10`: (D6) `hygiene_audit()` re-lints every BP `3x` per `SessionStart`; (D7) no `transition` lifecycle CLI; (D8) `[BLUEPRINT_FIRST|SOTA]` pushes `init` w/o `match` reuse check; (D9) naive `split("|")` mangles `||` & backticked pipes in `KILL`/`QAS`; (D10) `--fast` CLI flag falsely trips `"fast"` weasel ban.
SOTA_SOURCES|Google Design Doc tiering (1-page Mini-RFC vs full Design Doc by blast radius & door reversibility); Nygard ADR archival compaction (immutable cold archive + active decision index); Goodhart-resistant rubric design (structural specificity + clause-local anti-weasel checks); single-pass DAG validation.
TRIZ_CONTRADICTION|Universal Blueprint-First rigor (`tier >= 1`) vs low token cost, zero ceremony fatigue, & `999`-file cap -> Principle #1 Segmentation (`mini|compact|full` by `(tier, door)`) + Principle #2 Extraction (strip global boilerplate & compact `DONE` into 1 tarball + 1 ledger) + Principle #10 Preliminary Action (`blueprint match` + single-pass cache).
CANDIDATE|A|Keep Monolithic 8-Section Full Blueprint for Every `T1..T3` Task & Raise File Cap|Retain v2.18.0 single template, 4-subagent topology, & per-file `DONE` storage while hoping users type `"sem blueprint"` often.
REJECTED_BECAUSE|A|Burns `~3,741 tok` + `3 pro` subagents on simple `T1` tweaks (`>5x` waste), breaches `warn=850` file cap in `12` tasks, & lets 1-word tautologies pass `100/100`.
CANDIDATE|B|Drop Blueprints for `T1/T2` Entirely & Use Unstructured Freeform Notes|Raise `default_min_tier=3` and disable Popperian linting on `T1/T2` changes.
REJECTED_BECAUSE|B|Abandons falsifiable `QAS` oracles & `KILL` guards on `~80%` of real engineering tasks (`T1/T2` bug/feature work), restoring blind regressions & scope creep.
WINNER|Blueprint Architecture v2 (`dynamic-fractal-planner v2.19.0`): tiered `mini|compact|full` grammar (`<=35L/4KB` `0` subagents for `T1/2-way`; `<=65L/10KB` `<=1 flash` for `T2`; `<=350L/24KB` for `T3/1-way` & legacy default) + zero-boilerplate scaffolds + Anti-Gaming Linter v2 + lossless `DONE` archiver (`O(1)` files).
- Engine upgrades: code-aware pipe splitter (`||` & backticks), CLI-flag-exempt clause-local `±90c` weasel check, comparator `KILL`, non-dummy `QAS` oracle, substantive `REJECTED_BECAUSE`, `>600c` density warn, single-pass `hygiene_audit()` (`1x` lint/file), `blueprint match`/`transition`/`compact`, & `T0` tiny-fix + domain-classifier alignment.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|Stricter Anti-Gaming Linter v2 breaks existing valid Cavern blueprints or `v2.13..v2.18` plugin tests, OR `blueprint compact` deletes a `DONE` file without preserving its verbatim bytes in `blueprints_done.tar.gz`, OR `PLANS_HUB.md` loses lineage of archived blueprints causing agents to re-implement solved work.
THREAT|Backward-Compat Breakage & Silent History Loss: tightened rubric rejects legacy `BP_*.md` fixtures; compaction breaks `SUPERSEDED_BY`/`PARENT_ADR` resolution or drops text -> DEF: default blueprints without `format=` to `full`, test all `14` real Cavern blueprints + all `77` existing plugin tests against Linter v2 BEFORE commit, write & verify SHA-256 tarball members BEFORE unlinking any `DONE` `.md` file, & index every archived `BP_ID` in `BLUEPRINTS_DONE_LEDGER.md` + `PLANS_HUB.md` `HUB_STATS`.
KILL|R1|Plugin or Legacy Blueprint Regression|existing_plugin_test_fails > 0 or legacy_bp_lint_fails > 0|`<=30s` test run pre-stop|Relax over-eager heuristic immediately; isolate new strict checks per-rule.
KILL|R2|Compaction Data Loss or File-Budget Increase|archived_tar_verify_fails > 0 or post_compact_files >= 838 files|Immediate post-compact tar readback + file_budget_guard|Abort unlink if tar readback or ledger write fails; keep original `.md` untouched.
KILL|R3|Rubric Gaming Still Passing|fake_1word_bp_score >= 85% or fake_1word_bp_passed == 1|Unit adversarial fixture test in run_plugin_tests.py|Harden ORI/KCC/IDS/WBS validators until adversarial fixture scores `<50%`.

## 4. Invariants, Budgets & QAS
BUDGET|latency=hygiene<=100ms && hub_sync<=100ms|mem=<=24MB RSS stdlib Python|cost=T1_mini<=800 tok && 0 subagents && repo_files<=835 files|degraded_floor=Missing `format=` defaults to `full`; absent archive ledger reports `archived_done=0` w/ `0` errors.
PLANES|offline=One-time `blueprint compact --apply` archiving 10 prior `DONE` Cavern blueprints + ledger sync|rt=PreInvocation `<=15ms` format-aware `[BLUEPRINT_FIRST|SOTA]` + `T0` tiny-fix exemption|async=Single-pass `hygiene_audit()` in `SessionStart` & `verify_universal.py`.
QAS|Q1|Lint adversarial 1-word garbage blueprint (1-word reject, non-numeric kill, dummy oracle, distant-number weasel bypass)|`blueprint_engine.lint_blueprint(..., strict=True)`|Linter rejects all 4 gaming tricks|`<=50%` score && `0` pass|python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast
QAS|Q2|Scaffold & lint a `T1/2-way` `mini` blueprint vs `T3/1-way` `full` blueprint|`plan_tool.py blueprint classify, init, lint`|`T1` selects `format=mini` w/ `0` subagents & passes `>=95%` in `<=30L` / `<=3 KB`|`<=30 lines` && `<=3.0 KB` && `0 items` subagents|python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast
QAS|Q3|Compact `10` prior `DONE` Cavern blueprints into cold archive + ledger & verify lossless roundtrip|Cavern workspace (`838` files pre-compact)|All `10` full `.md` files readable verbatim from `blueprints_done.tar.gz`, `BLUEPRINTS_DONE_LEDGER.md` indexed, `PLANS_HUB.md` synced, & repo files reduced|`>=10 files` archived && `<=832 files` total|python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py blueprint hygiene --strict && python3 -B .agents/scripts/file_budget_guard.py --check
QAS|Q4|Single-pass `hygiene_audit()` & full plugin+repo verification gate|Cavern workspace + embedded `dynamic-fractal-planner v2.19.0`|`0` verify failures, `0` host-audit errors, `0` hub drift, & `hygiene` latency within budget|`<=120ms` hygiene && `0%` fails|python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py host-audit

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.95*10|Empirically measure all `14` Cavern blueprints (tokens, boilerplate, score inflation, line lengths, file budget) & run adversarial 1-word blueprint through v2.18.0 linter|DONE: Measured `179.1 KB` (`~52,400 tok`), `10/14` unarchived `DONE` (`838/850` files), `14/14` + 1-word garbage all scoring `100/100`, `1,919c` max line length, `8.6%` boilerplate, & `3x` redundant linting in `hygiene_audit()`.

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|Upgrade `dynamic-fractal-planner` (`v2.18.0 -> v2.19.0`): `blueprint_engine.py`, `schema_migrations.py`, `complexity_triage.py`, `planner_pre_invocation.py`, `planner_session_start.py`, `plan_tool.py`, `sota-blueprint/SKILL.md`, `rules/AGENTS.md`, `plugin.json`, `CHANGELOG.md`, & `TestBlueprintArchitectureV2`|Engine & policy upgrades -> `>=82` passing plugin tests (`<=15s`)|python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast
SLICE|W2|Compact `10` prior `DONE` Cavern blueprints via `plan_tool.py blueprint compact --apply`, register `BLUEPRINTS_DONE_LEDGER.md` in `.agents/planner_profile.json` (`plugin_version: "2.19.0"`), sync `.agents/docs/PLANS_HUB.md`, & update `AGENTS.md`, `PROJECT_HISTORY.md`, `HANDOFF.md`, & dual-scope `EXTERNAL_AI_SYNC.md` ledgers|14 files (`838` total) -> active-only dir + lossless tarball+ledger (`<=832` total files, `0` drift)|python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py blueprint hygiene --strict && python3 -B .agents/scripts/file_budget_guard.py --check
SLICE|W3|Full verification gate (`run_plugin_tests.py --fast`, `blueprint lint --strict`, `blueprint hygiene --strict`, `blueprint hub --check`, `verify_universal.py --all`, `file_budget_guard.py --check`, `host-audit`, `compile_applet`, `lint_applet`)|All static, unit, & build gates -> `0` errors|python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all

### H2+ Outcome Stubs
STUB|W4|Propagate `dynamic-fractal-planner v2.19.0` across fleet (`global` + `OmniAgent` + `dynamic-fractal-planner` canonical) via `evolve sync-all --apply --push` (`0%` semver drift across triad).

## 7. Fitness Functions & Evolution
FITNESS|python3 -B .agents/plugins/dynamic-fractal-planner/tests/run_plugin_tests.py --fast && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py blueprint hygiene --strict && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all && python3 -B .agents/scripts/file_budget_guard.py --check
