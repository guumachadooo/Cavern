#!/usr/bin/env python3
"""Cavern Live Phone Bridge v2 client delegating to universal plugin `live_bridge_client.py`.

Supports:
  - Direct ephemeral session code (`--code CAV-...`)
  - Background Standby Biometric Wake (`--wake --pair-secret <64-hex>` or env `CAVERN_BRIDGE_PAIR_SECRET`)
  - Arbitrary flexible tiered commands (`--cmd <name> --args '<json>'`, `--batch '<json>'`, `--capabilities`, `--save-screenshot out.jpg`)
"""

from __future__ import annotations

import sys
from pathlib import Path

_PLUGIN_SCRIPTS = Path(__file__).resolve().parents[1] / "plugins" / "dynamic-fractal-planner" / "scripts"
if str(_PLUGIN_SCRIPTS) not in sys.path:
    sys.path.insert(0, str(_PLUGIN_SCRIPTS))

import live_bridge_client as _plugin_lb  # noqa: E402
from live_bridge_client import *  # noqa: E402, F401, F403


def main(argv: list[str] | None = None) -> int:
    return _plugin_lb.main(
        argv,
        env_url_vars=("CAVERN_RELAY_URL", "LIVE_BRIDGE_URL"),
        env_secret_vars=("CAVERN_BRIDGE_PAIR_SECRET", "LIVE_BRIDGE_PAIR_SECRET"),
    )


if __name__ == "__main__":
    raise SystemExit(main())
