#!/usr/bin/env python3
"""Cavern thin wrapper delegating to universal plugin `error_learner.py` (v2.6.0).

Re-exports all public symbols so `.github/workflows/BuildAndroid.yml` and
`.agents/scripts/test_error_learner.py` stay 100% backward compatible while the
core timeless-learning engine lives once in `dynamic-fractal-planner`.
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

_PLUGIN_PATH = (
    Path(__file__).resolve().parents[1]
    / "plugins"
    / "dynamic-fractal-planner"
    / "scripts"
    / "error_learner.py"
)
_SPEC = importlib.util.spec_from_file_location("_dfp_universal_error_learner", _PLUGIN_PATH)
assert _SPEC is not None and _SPEC.loader is not None
_plugin_el = importlib.util.module_from_spec(_SPEC)
sys.modules["_dfp_universal_error_learner"] = _plugin_el
_SPEC.loader.exec_module(_plugin_el)

for _name in dir(_plugin_el):
    if not _name.startswith("__"):
        globals()[_name] = getattr(_plugin_el, _name)

DEFAULT_REPO = "guumachadooo/Cavern"
DEFAULT_BRANCH = "diagnostics"
DEFAULT_LESSONS_REL = ".agents/docs/BUILD_LESSONS.md"


def main(argv: list[str] | None = None) -> int:
    return _plugin_el.main(
        argv,
        default_repo=DEFAULT_REPO,
        default_branch=DEFAULT_BRANCH,
        default_lessons_rel=DEFAULT_LESSONS_REL,
    )


if __name__ == "__main__":
    raise SystemExit(main())
