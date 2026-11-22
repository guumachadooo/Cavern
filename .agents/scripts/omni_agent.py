#!/usr/bin/env python3
"""Thin Cavern workspace entrypoint delegating to `.agents/agent/omni_agent_client.py`."""

from __future__ import annotations

import sys
from pathlib import Path

AGENT_DIR = Path(__file__).resolve().parents[1] / "agent"
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

from omni_agent_client import main

if __name__ == "__main__":
    raise SystemExit(main())
