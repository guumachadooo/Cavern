# Session Handoff — `dynamic-fractal-planner` (`HANDOFF.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`<=20L`).

- `Release`: `v2.21.0` (Multi-Wave DAG Epoch Scheduler `wave_scheduler.py`, backwards-compat `SLICE_DAG|`/`DAG_POLICY|`, dual `seq|parallel` `run_workflow` + D0->D1->D2 `[LEASE:...]` fallback, dirty-safe baseline rollback, `ScriptPath` & `define_subagent` guards)
- `ParityDocs`: `.agents/docs/{EXTERNAL_AI_SYNC.md,HANDOFF.md,BUILD_LESSONS.md,PROJECT_HISTORY.md,SAVED_IDEAS.md,EXTRA_REQUESTS.md}`
- `Next`: run `python3 -B tests/run_plugin_tests.py --fast` && `plan_tool.py evolve sync-all --apply` so global + all registered repos stay synced at `v2.21.0`.
