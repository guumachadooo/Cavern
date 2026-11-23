#!/usr/bin/env python3
"""Cavern thin wrapper delegating to universal plugin `live_diagnostics.py` (v2.6.0)
with OmniConnect v5 zero-paste Cloudflare relay & pair-secret auto-resolution."""

from __future__ import annotations

import sys
from pathlib import Path

_AGENTS_DIR = Path(__file__).resolve().parents[1]
_AGENT_DIR = _AGENTS_DIR / "agent"
_PLUGIN_SCRIPTS = _AGENTS_DIR / "plugins" / "dynamic-fractal-planner" / "scripts"
for _p in (str(_AGENT_DIR), str(_PLUGIN_SCRIPTS)):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import bridge_auth as _bauth  # noqa: E402
import live_diagnostics as _plugin_ld  # noqa: E402
from live_diagnostics import *  # noqa: E402, F401, F403

DEFAULT_REPO = "guumachadooo/Cavern"
DEFAULT_BRANCH = "diagnostics"


def main(argv: list[str] | None = None) -> int:
    forwarded = list(sys.argv[1:] if argv is None else argv)
    if "--cloudflare" in forwarded:
        conn = _bauth.resolve_connection()
        has_url = any(a == "--relay-url" or a.startswith("--relay-url=") for a in forwarded)
        has_sec = any(a == "--pair-secret" or a.startswith("--pair-secret=") for a in forwarded)
        if not has_url and conn["relayUrl"]:
            forwarded.extend(["--relay-url", conn["relayUrl"]])
        if not has_sec and conn["pairSecret"]:
            forwarded.extend(["--pair-secret", conn["pairSecret"]])
    return _plugin_ld.main(
        forwarded,
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
