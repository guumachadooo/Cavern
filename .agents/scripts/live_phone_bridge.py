#!/usr/bin/env python3
"""Send a whitelisted read-only diagnostic command to Cavernize Android via the user's
free Cloudflare Worker ephemeral session relay and wait for the live response.

Zero third-party dependencies (Python stdlib only). Never persists session codes to disk.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from typing import Any

ALLOWED_COMMANDS = (
    "ping",
    "quick_check",
    "full_check",
    "log_tail",
    "summary",
    "audio_routes",
    "memory",
)


def _request_json(
    method: str,
    url: str,
    token: str | None = None,
    payload: dict[str, Any] | None = None,
    timeout: int = 12,
) -> tuple[int, dict[str, Any]]:
    headers = {
        "User-Agent": "Cavern-LivePhoneBridge/1.0",
        "Accept": "application/json",
    }
    if token:
        headers["X-Session-Token"] = token
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        data = json.dumps(payload).encode("utf-8")

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            return resp.status, json.loads(raw) if raw else {}
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace") if exc.fp else ""
        try:
            return exc.code, json.loads(raw) if raw else {"ok": False, "error": str(exc)}
        except Exception:
            return exc.code, {"ok": False, "error": raw or str(exc)}
    except Exception as exc:
        return 0, {"ok": False, "error": f"{type(exc).__name__}: {exc}"}


def validate_https_url(raw_url: str) -> str:
    clean = raw_url.strip().rstrip("/")
    parsed = urllib.parse.urlparse(clean)
    if parsed.scheme != "https" or not parsed.netloc:
        raise ValueError("Relay URL must use https:// with a valid host.")
    return clean


def main() -> int:
    parser = argparse.ArgumentParser(description="Query Cavernize Android live via ephemeral Cloudflare Worker relay.")
    parser.add_argument("--url", required=True, help="Your Cloudflare Worker HTTPS URL")
    parser.add_argument("--code", required=True, help="Ephemeral 15-min session code shown in Cavernize Android")
    parser.add_argument("--cmd", default="ping", choices=ALLOWED_COMMANDS, help="Whitelisted read-only command")
    parser.add_argument("--lines", type=int, default=150, help="Lines for log_tail (20..500)")
    parser.add_argument("--errors-only", action="store_true", help="Filter log_tail to WARN/ERROR/FATAL")
    parser.add_argument("--timeout", type=int, default=45, help="Max seconds to wait for phone response")
    args = parser.parse_args()

    try:
        base_url = validate_https_url(args.url)
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    token = args.code.strip()
    request_id = uuid.uuid4().hex[:16]

    code, data = _request_json(
        "POST",
        f"{base_url}/v1/session/cmd",
        token=token,
        payload={
            "requestId": request_id,
            "cmd": args.cmd,
            "lines": max(20, min(500, args.lines)),
            "errorsOnly": bool(args.errors_only),
        },
    )
    if code != 200 or not data.get("ok"):
        print(f"ERROR queueing command (HTTP {code}): {data.get('error', data)}", file=sys.stderr)
        return 1

    deadline = time.monotonic() + max(5, args.timeout)
    while time.monotonic() < deadline:
        time.sleep(1.5)
        r_code, r_data = _request_json(
            "GET",
            f"{base_url}/v1/session/result?requestId={urllib.parse.quote(request_id)}",
            token=token,
        )
        if r_code == 404:
            print("ERROR: Session expired or closed on the phone.", file=sys.stderr)
            return 1
        if r_code == 200 and r_data.get("ready"):
            res = r_data.get("result", {})
            print(f"=== LIVE PHONE RESPONSE [{res.get('cmd', args.cmd)}] (ok={res.get('ok')}, {res.get('elapsedMs', 0)} ms) ===")
            body = res.get("body", "")
            if isinstance(body, str):
                print(body)
            else:
                print(json.dumps(body, indent=2, ensure_ascii=False))
            return 0 if res.get("ok", True) else 1

    print(f"TIMEOUT: Phone did not respond within {args.timeout}s. Verify Live Bridge is active in Cavernize.", file=sys.stderr)
    return 3


if __name__ == "__main__":
    raise SystemExit(main())
