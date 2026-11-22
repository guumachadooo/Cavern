#!/usr/bin/env python3
"""Fetch live uncached CI diagnostics, device self-check reports, and device logs from GitHub.

Uses the GitHub Contents REST API (Accept: application/vnd.github.raw) instead of
raw.githubusercontent.com so freshly uploaded device reports appear immediately
without CDN cache lag. Zero third-party dependencies (Python stdlib only).
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from typing import Any

DEFAULT_REPO = "guumachadooo/Cavern"
DEFAULT_BRANCH = "diagnostics"
USER_AGENT = "Cavern-LiveDiagnosticsFetcher/1.0"


def _http_get(url: str, accept: str = "application/vnd.github+json", timeout: int = 12) -> tuple[int, str]:
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": USER_AGENT,
            "Accept": accept,
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace") if exc.fp else str(exc)
        return exc.code, body
    except Exception as exc:
        return 0, f"{type(exc).__name__}: {exc}"


def fetch_raw_file(repo: str, branch: str, path: str) -> tuple[int, str]:
    url = f"https://api.github.com/repos/{repo}/contents/{path}?ref={branch}"
    return _http_get(url, accept="application/vnd.github.raw")


def fetch_branch_head(repo: str, branch: str) -> dict[str, Any] | None:
    code, body = _http_get(f"https://api.github.com/repos/{repo}/commits/{branch}")
    if code != 200:
        return None
    try:
        data = json.loads(body)
        commit = data.get("commit", {})
        return {
            "sha": (data.get("sha") or "")[:8],
            "date": (commit.get("committer") or {}).get("date", ""),
            "message": (commit.get("message") or "").splitlines()[0] if commit.get("message") else "",
        }
    except Exception:
        return None


def fetch_latest_workflow_run(repo: str) -> dict[str, Any] | None:
    code, body = _http_get(f"https://api.github.com/repos/{repo}/actions/runs?per_page=3")
    if code != 200:
        return None
    try:
        runs = json.loads(body).get("workflow_runs", [])
        if not runs:
            return None
        r = runs[0]
        return {
            "name": r.get("name", ""),
            "status": r.get("status", ""),
            "conclusion": r.get("conclusion", ""),
            "created_at": r.get("created_at", ""),
            "sha": (r.get("head_sha") or "")[:8],
            "url": r.get("html_url", ""),
        }
    except Exception:
        return None


def print_section(title: str) -> None:
    print(f"\n{'=' * 72}\n{title}\n{'=' * 72}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Fetch live diagnostics from GitHub.")
    parser.add_argument("--repo", default=DEFAULT_REPO, help="owner/repo (default: guumachadooo/Cavern)")
    parser.add_argument("--branch", default=DEFAULT_BRANCH, help="branch (default: diagnostics)")
    parser.add_argument("--all", action="store_true", help="Fetch CI diagnostics, learned lessons, self-check, summary, and log tail")
    parser.add_argument("--ci", action="store_true", help="Fetch CI build diagnostics report")
    parser.add_argument("--lessons", action="store_true", help="Fetch CI dynamic error-learner candidates (ci/latest-learned-lessons.md)")
    parser.add_argument("--selfcheck", action="store_true", help="Fetch device self-check report")
    parser.add_argument("--summary", action="store_true", help="Fetch device diagnostic JSON summary")
    parser.add_argument("--log", action="store_true", help="Fetch device log tail")
    parser.add_argument("--tail", type=int, default=120, help="Lines to display from device log (default: 120)")
    parser.add_argument("--errors-only", action="store_true", help="Filter device log lines to WARN/ERROR/FATAL")
    args = parser.parse_args()

    if not (args.all or args.ci or args.lessons or args.selfcheck or args.summary or args.log):
        args.all = True

    head = fetch_branch_head(args.repo, args.branch)
    run = fetch_latest_workflow_run(args.repo)
    print_section(f"LIVE GITHUB SNAPSHOT ({args.repo} @ {args.branch})")
    if head:
        print(f"Branch '{args.branch}' HEAD: {head['sha']} ({head['date']}) - {head['message']}")
    else:
        print(f"Branch '{args.branch}': not created yet (will be bootstrapped on next CI build or device upload).")
    if run:
        print(
            f"Latest Actions run: {run['name']} | status={run['status']} "
            f"conclusion={run['conclusion']} | sha={run['sha']} | {run['created_at']}"
        )

    if args.all or args.ci:
        print_section("CI BUILD DIAGNOSTICS (ci/latest-build-diagnostics.md)")
        code, body = fetch_raw_file(args.repo, args.branch, "ci/latest-build-diagnostics.md")
        if code == 200:
            lines = body.splitlines()
            print("\n".join(lines[:160]))
            if len(lines) > 160:
                print(f"... (+{len(lines) - 160} more lines)")
        else:
            print(f"[HTTP {code}] ci/latest-build-diagnostics.md unavailable yet.")

    if args.all or args.lessons:
        print_section("DYNAMIC BUILD LESSONS CANDIDATES (ci/latest-learned-lessons.md)")
        code, body = fetch_raw_file(args.repo, args.branch, "ci/latest-learned-lessons.md")
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] ci/latest-learned-lessons.md unavailable yet (generated on next CI build).")

    if args.all or args.selfcheck:
        print_section("DEVICE SELF-CHECK REPORT (device-logs/latest_selfcheck.md)")
        code, body = fetch_raw_file(args.repo, args.branch, "device-logs/latest_selfcheck.md")
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] device-logs/latest_selfcheck.md unavailable yet.")

    if args.all or args.summary:
        print_section("DEVICE SESSION SUMMARY (device-logs/latest_summary.json)")
        code, body = fetch_raw_file(args.repo, args.branch, "device-logs/latest_summary.json")
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] device-logs/latest_summary.json unavailable yet.")

    if args.all or args.log:
        print_section(f"DEVICE LOG TAIL (device-logs/cavernize_log.txt, last {args.tail} lines)")
        code, body = fetch_raw_file(args.repo, args.branch, "device-logs/cavernize_log.txt")
        if code != 200:
            # Fallback to master .agents/device_logs/cavernize_log.txt
            code, body = fetch_raw_file(args.repo, "master", ".agents/device_logs/cavernize_log.txt")
            if code == 200:
                print("(Fallback from master/.agents/device_logs/cavernize_log.txt)")
        if code == 200:
            lines = body.splitlines()
            if args.errors_only:
                lines = [
                    ln for ln in lines
                    if any(tok in ln for tok in ("[WARN]", "[ERROR]", "[FATAL]", "=== Session started", "Exception"))
                ]
            tail_lines = lines[-max(1, args.tail):]
            print("\n".join(tail_lines))
        else:
            print(f"[HTTP {code}] Device log unavailable yet.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
