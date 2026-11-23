#!/usr/bin/env python3
"""Zero-dependency 1-second Cloudflare Worker v4 Deployer (preserves RELAY_KV & secrets).

Eliminates freezing the Cloudflare Dashboard Quick Edit browser tab when pasting 900+ lines.
Uploads `.agents/docs/cloudflare_live_relay_worker.js` directly via Cloudflare Workers REST API
using `keep_bindings: ["kv_namespace", "secret_text", "plain_text", "r2_bucket", "d1"]`.

Usage:
  python3 -B .agents/agent/deploy_worker.py
  # or non-interactive:
  CLOUDFLARE_API_TOKEN="..." CLOUDFLARE_WORKER_NAME="cavernize-diag-relay" \
      python3 -B .agents/agent/deploy_worker.py --verify-url "https://cavernize-diag-relay....workers.dev"
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import secrets
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_WORKER_FILE = REPO_ROOT / ".agents" / "docs" / "cloudflare_live_relay_worker.js"
PRESERVED_BINDING_TYPES = ["kv_namespace", "secret_text", "plain_text", "r2_bucket", "d1"]
CF_API_BASE = "https://api.cloudflare.com/client/v4"


def build_worker_multipart(
    script_source: bytes,
    main_module: str = "worker.js",
    keep_bindings: list[str] | None = None,
    boundary: str | None = None,
) -> tuple[bytes, str]:
    """Build RFC 7578 multipart/form-data payload for Cloudflare Module Worker upload."""
    bnd = boundary or f"----OmniAgentCF{secrets.token_hex(12)}"
    metadata = {
        "main_module": main_module,
        "keep_bindings": list(PRESERVED_BINDING_TYPES if keep_bindings is None else keep_bindings),
    }
    meta_bytes = json.dumps(metadata, separators=(",", ":")).encode("utf-8")

    parts = [
        (
            f"--{bnd}\r\n"
            f'Content-Disposition: form-data; name="metadata"\r\n'
            f"Content-Type: application/json\r\n\r\n"
        ).encode("utf-8")
        + meta_bytes
        + b"\r\n",
        (
            f"--{bnd}\r\n"
            f'Content-Disposition: form-data; name="{main_module}"; filename="{main_module}"\r\n'
            f"Content-Type: application/javascript+module\r\n\r\n"
        ).encode("utf-8")
        + script_source
        + b"\r\n",
        f"--{bnd}--\r\n".encode("utf-8"),
    ]
    return b"".join(parts), f"multipart/form-data; boundary={bnd}"


def _cf_json_request(method: str, url: str, token: str, body: bytes | None = None, content_type: str | None = None) -> dict[str, Any]:
    headers = {"Authorization": f"Bearer {token}"}
    if content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8", errors="replace"))
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace")
        try:
            err_json = json.loads(raw)
        except Exception:
            err_json = {"success": False, "errors": [{"code": exc.code, "message": raw[:400]}]}
        err_json["_httpStatus"] = exc.code
        return err_json


def auto_detect_account_id(token: str) -> str:
    res = _cf_json_request("GET", f"{CF_API_BASE}/accounts?per_page=10", token)
    accounts = res.get("result") if isinstance(res, dict) else None
    if not res.get("success") or not isinstance(accounts, list) or not accounts:
        raise RuntimeError(f"Failed to list Cloudflare accounts: {res.get('errors')}")
    if len(accounts) == 1:
        return str(accounts[0]["id"])
    print("Multiple Cloudflare accounts found:")
    for idx, acc in enumerate(accounts):
        print(f"  [{idx}] {acc.get('name')} ({acc.get('id')})")
    choice = input("Select account index [0]: ").strip() or "0"
    return str(accounts[int(choice)]["id"])


def deploy_worker(
    token: str,
    account_id: str,
    script_name: str,
    worker_file: Path = DEFAULT_WORKER_FILE,
    main_module: str = "worker.js",
) -> dict[str, Any]:
    source = worker_file.read_bytes()
    body, content_type = build_worker_multipart(source, main_module=main_module)
    url = f"{CF_API_BASE}/accounts/{account_id}/workers/scripts/{script_name}"
    return _cf_json_request("PUT", url, token, body=body, content_type=content_type)


def verify_worker_health(worker_url: str) -> dict[str, Any]:
    url = worker_url.strip().rstrip("/") + "/health"
    req = urllib.request.Request(url, headers={"User-Agent": "OmniAgentDeploy/4.0"}, method="GET")
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read().decode("utf-8", errors="replace"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Upload Cavernize/OmniAgent Worker v4 via Cloudflare API (preserves RELAY_KV).")
    parser.add_argument("--file", type=Path, default=DEFAULT_WORKER_FILE, help="Path to worker JS file")
    parser.add_argument("--account-id", default=os.environ.get("CLOUDFLARE_ACCOUNT_ID", ""), help="Cloudflare Account ID (auto-detected if omitted)")
    parser.add_argument("--worker-name", default=os.environ.get("CLOUDFLARE_WORKER_NAME", ""), help="Existing Worker name (e.g. cavernize-diag-relay)")
    parser.add_argument("--token", default=os.environ.get("CLOUDFLARE_API_TOKEN", ""), help="Cloudflare API Token (Edit Cloudflare Workers)")
    parser.add_argument("--main-module", default="worker.js", help="Entry module filename inside Cloudflare Worker (default: worker.js)")
    parser.add_argument("--verify-url", default=os.environ.get("CAVERN_RELAY_URL", ""), help="Optional Worker URL to check /health after deploy")
    args = parser.parse_args(argv)

    if not args.file.is_file():
        print(f"ERROR: Worker file not found: {args.file}", file=sys.stderr)
        return 2

    token = args.token.strip() or getpass.getpass("Cloudflare API Token (Workers Scripts:Edit): ").strip()
    if not token:
        print("ERROR: Missing Cloudflare API Token.", file=sys.stderr)
        return 2

    account_id = args.account_id.strip() or auto_detect_account_id(token)
    worker_name = args.worker_name.strip() or input("Worker name [cavernize-diag-relay]: ").strip() or "cavernize-diag-relay"

    print(f"[Deploy] Uploading {args.file.name} ({args.file.stat().st_size} bytes) -> {worker_name} (keep_bindings={PRESERVED_BINDING_TYPES})...")
    res = deploy_worker(token, account_id, worker_name, args.file, main_module=args.main_module)
    if not res.get("success"):
        print(f"ERROR: Cloudflare API deploy failed: {json.dumps(res, indent=2)}", file=sys.stderr)
        return 1

    print(f"[Deploy] SUCCESS! Worker '{worker_name}' updated on Cloudflare (RELAY_KV & secrets preserved).")
    if args.verify_url.strip():
        try:
            health = verify_worker_health(args.verify_url)
            print(f"[Health] {json.dumps(health)}")
        except Exception as exc:
            print(f"[Health] Warning: could not verify {args.verify_url}/health: {exc}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
