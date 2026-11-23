#!/usr/bin/env python3
"""Cavern Live Phone Bridge v3+ / OmniConnect v5 zero-paste client.

Automatically resolves Relay URL, active ephemeral Session Token, or Pair Secret from:
  CLI flags -> Environment Variables (AI Studio Secrets) -> Local Gitignored Vault
  -> Public `.agents/bridge_profile.json` -> Zero-Secret P-256 ECDH Smart Knock (`--auto`).

Supports:
  - Zero-flag auto-connect (`--auto --cmd ping` or simply `--cmd ping` when vaulted!)
  - Direct ephemeral session code (`--code CAV-...` — auto-caches in local vault on use)
  - Background Standby Biometric Wake (`--wake`)
  - Arbitrary flexible tiered commands (`--cmd`, `--args`, `--batch`, `--pipeline`, `--save-screenshot`)
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

_AGENTS_DIR = Path(__file__).resolve().parents[1]
_AGENT_DIR = _AGENTS_DIR / "agent"
_PLUGIN_SCRIPTS = _AGENTS_DIR / "plugins" / "dynamic-fractal-planner" / "scripts"
for _p in (str(_AGENT_DIR), str(_PLUGIN_SCRIPTS)):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import bridge_auth as _bauth  # noqa: E402
import live_bridge_client as _plugin_lb  # noqa: E402
from live_bridge_client import *  # noqa: E402, F401, F403


def _has_flag(argv: list[str], *flags: str) -> bool:
    for a in argv:
        for f in flags:
            if a == f or a.startswith(f + "="):
                return True
    return False


def _get_flag_val(argv: list[str], flag: str) -> str:
    for i, a in enumerate(argv):
        if a == flag and i + 1 < len(argv):
            return argv[i + 1]
        if a.startswith(flag + "="):
            return a.split("=", 1)[1]
    return ""


def main(argv: list[str] | None = None) -> int:
    raw_argv = list(sys.argv[1:] if argv is None else argv)
    auto_mode = "--auto" in raw_argv
    forwarded = [a for a in raw_argv if a != "--auto"]

    if any(h in forwarded for h in ("-h", "--help")):
        return _plugin_lb.main(
            forwarded,
            env_url_vars=("CAVERN_RELAY_URL", "LIVE_BRIDGE_URL"),
            env_secret_vars=("CAVERN_BRIDGE_PAIR_SECRET", "LIVE_BRIDGE_PAIR_SECRET"),
        )

    cli_url = _get_flag_val(forwarded, "--url")
    cli_code = _get_flag_val(forwarded, "--code")
    cli_pair = _get_flag_val(forwarded, "--pair-secret")
    wants_wake = _has_flag(forwarded, "--wake")

    conn = _bauth.resolve_connection(
        cli_url=cli_url,
        cli_pair_secret=cli_pair,
        cli_code=cli_code,
    )

    # Auto-cache explicitly provided session token or URL into local gitignored vault
    if cli_code and conn["relayUrl"]:
        try:
            _bauth.save_vault(relay_url=conn["relayUrl"], session_token=cli_code, session_ttl_seconds=1800)
        except Exception:
            pass

    if not _has_flag(forwarded, "--url") and conn["relayUrl"]:
        forwarded = ["--url", conn["relayUrl"]] + forwarded

    if not wants_wake and not _has_flag(forwarded, "--code"):
        if conn["sessionToken"]:
            forwarded = ["--code", conn["sessionToken"]] + forwarded
        elif conn["pairSecret"]:
            forwarded = ["--wake", "--pair-secret", conn["pairSecret"]] + forwarded
        elif auto_mode and conn["relayUrl"] and conn["knockId"]:
            kr = _bauth.request_smart_knock(
                conn["relayUrl"],
                conn["knockId"],
                requested_scope="session",
                tier=2,
                duration_seconds=3600,
                reason="Live Bridge auto-connect",
                persist_vault=True,
            )
            grant = kr.get("grant") or {}
            tok = str(grant.get("sessionCode") or "").strip()
            if tok:
                forwarded = ["--code", tok] + forwarded
            elif grant.get("pairSecret"):
                forwarded = ["--wake", "--pair-secret", str(grant["pairSecret"])] + forwarded
    elif wants_wake and not _has_flag(forwarded, "--pair-secret") and conn["pairSecret"]:
        forwarded = ["--pair-secret", conn["pairSecret"]] + forwarded

    def _save_acquired_session(relay_url: str, session_code: str, ttl_seconds: int, _meta: dict) -> None:
        try:
            _bauth.save_vault(
                relay_url=relay_url,
                session_token=session_code,
                session_ttl_seconds=max(60, int(ttl_seconds or 1800)),
            )
        except Exception:
            pass

    rc = _plugin_lb.main(
        forwarded,
        env_url_vars=("CAVERN_RELAY_URL", "LIVE_BRIDGE_URL"),
        env_secret_vars=("CAVERN_BRIDGE_PAIR_SECRET", "LIVE_BRIDGE_PAIR_SECRET"),
        on_session_acquired=_save_acquired_session,
    )
    # Only clear vaulted session token when relay explicitly reports HTTP 404 (expired/closed),
    # NEVER on transient network/command timeouts so a 30-min biometric approval is never wasted.
    expired_rc = getattr(_plugin_lb, "EXIT_SESSION_EXPIRED", 4)
    if rc == expired_rc and not cli_code and conn["sessionSource"] == "vault" and conn["sessionToken"]:
        try:
            vault = _bauth.load_vault()
            if vault.get("sessionToken") == conn["sessionToken"]:
                vault.pop("sessionToken", None)
                vault.pop("sessionExpiresUnix", None)
                _bauth.LOCAL_VAULT_PATH.write_text(json.dumps(vault, indent=2) + "\n", encoding="utf-8")
        except Exception:
            pass
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
