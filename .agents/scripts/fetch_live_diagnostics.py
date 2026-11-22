#!/usr/bin/env python3
"""Cavern thin wrapper delegating to universal plugin `live_diagnostics.py` (v2.6.0)."""

from __future__ import annotations

import sys
from pathlib import Path

_PLUGIN_SCRIPTS = Path(__file__).resolve().parents[1] / "plugins" / "dynamic-fractal-planner" / "scripts"
if str(_PLUGIN_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_PLUGIN_SCRIPTS))

import live_diagnostics as _plugin_ld  # noqa: E402
from live_diagnostics import *  # noqa: E402, F401, F403

DEFAULT_REPO = "guumachadooo/Cavern"
DEFAULT_BRANCH = "diagnostics"


def main(argv: list[str] | None = None) -> int:
    return _plugin_ld.main(
        argv,
        default_repo=DEFAULT_REPO,
        default_branch=DEFAULT_BRANCH,
        overrides={
            "log_txt": "device-logs/cavernize_log.txt",
            "fallback_log_branch": "master",
            "fallback_log_path": ".agents/device_logs/cavernize_log.txt",
        },
    )


if __name__ == "__main__":
    raise SystemExit(main())
