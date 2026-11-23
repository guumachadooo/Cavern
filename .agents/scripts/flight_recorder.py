#!/usr/bin/env python3
"""Project entrypoint for Cavern Anti-Cancellation 3-Tier Flight Recorder.

Delegates cleanly to `.agents/agent/flight_recorder.py` (pure Python 3 stdlib).
"""

from __future__ import annotations

import sys
from pathlib import Path

AGENT_DIR = Path(__file__).resolve().parent.parent / "agent"
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

import flight_recorder as fr  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(fr.main())
