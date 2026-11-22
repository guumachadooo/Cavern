#!/usr/bin/env python3
"""Universal uncached GitHub Live Diagnostics Fetcher (Dynamic Fractal Planner v2.6.0).

Uses the GitHub Contents REST API (Accept: application/vnd.github.raw) instead of
raw.githubusercontent.com so freshly uploaded CI / device / runtime reports appear
immediately without CDN cache lag. Auto-detects owner/repo from `.agents/planner_profile.json`
or `git remote get-url origin`. Zero third-party dependencies (Python stdlib only).
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

DEFAULT_BRANCH = "diagnostics"
USER_AGENT = "DynamicFractalPlanner-LiveDiagnostics/2.6"

DEFAULT_PATHS = {
    "ci_md": "ci/latest-build-diagnostics.md",
    "lessons_md": "ci/latest-learned-lessons.md",
    "selfcheck_md": "device-logs/latest_selfcheck.md",
    "summary_json": "device-logs/latest_summary.json",
    "log_txt": "device-logs/app_log.txt",
    "fallback_log_branch": "master",
    "fallback_log_path": "",
}


def find_repo_root(start_dir: Path | None = None) -> Path:
    base = (start_dir or Path.cwd()).expanduser().resolve()
    try:
        res = subprocess.run(
            ["git", "-C", str(base), "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        if res.returncode == 0 and res.stdout.strip():
            return Path(res.stdout.strip()).resolve()
    except Exception:
        pass
    return base


def load_diagnostics_config(repo_root: Path) -> dict[str, Any]:
    cfg: dict[str, Any] = {
        "repo": "",
        "branch": DEFAULT_BRANCH,
        **DEFAULT_PATHS,
    }
    prof_file = repo_root / ".agents" / "planner_profile.json"
    if prof_file.is_file():
        try:
            prof = json.loads(prof_file.read_text(encoding="utf-8", errors="ignore"))
            diag = prof.get("diagnostics")
            if isinstance(diag, dict):
                for k, v in diag.items():
                    if isinstance(v, str) and v.strip():
                        cfg[k] = v.strip()
        except Exception:
            pass
    if not cfg["repo"]:
        try:
            res = subprocess.run(
                ["git", "-C", str(repo_root), "remote", "get-url", "origin"],
                capture_output=True,
                text=True,
                timeout=3,
                check=False,
            )
            if res.returncode == 0:
                m = re.search(r"github\.com[:/]([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?$", res.stdout.strip())
                if m:
                    cfg["repo"] = m.group(1)
        except Exception:
            pass
    return cfg


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
    url = f"https://api.github.com/repos/{repo}/contents/{path.lstrip('/')}?ref={branch}"
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


def main(
    argv: list[str] | None = None,
    *,
    default_repo: str = "",
    default_branch: str = "",
    overrides: dict[str, str] | None = None,
) -> int:
    parser = argparse.ArgumentParser(description="Fetch live uncached CI & runtime diagnostics from GitHub.")
    parser.add_argument("--workspace", default="", help="Workspace root override")
    parser.add_argument("--repo", default="", help="GitHub owner/repo (auto-detected from profile or git remote)")
    parser.add_argument("--branch", default="", help="Diagnostics branch (default: profile or 'diagnostics')")
    parser.add_argument("--all", action="store_true", help="Fetch CI diagnostics, learned lessons, self-check, summary, and log tail")
    parser.add_argument("--ci", action="store_true", help="Fetch CI build diagnostics report")
    parser.add_argument("--lessons", action="store_true", help="Fetch CI dynamic error-learner candidates")
    parser.add_argument("--selfcheck", action="store_true", help="Fetch device/runtime self-check report")
    parser.add_argument("--summary", action="store_true", help="Fetch device/runtime diagnostic JSON summary")
    parser.add_argument("--log", action="store_true", help="Fetch runtime/device log tail")
    parser.add_argument("--file", default="", help="Fetch any arbitrary file path from the diagnostics branch")
    parser.add_argument("--tail", type=int, default=120, help="Lines to display from log (default: 120)")
    parser.add_argument("--errors-only", action="store_true", help="Filter log lines to WARN/ERROR/FATAL/Exception")
    args = parser.parse_args(argv)

    repo_root = find_repo_root(Path(args.workspace) if args.workspace else None)
    cfg = load_diagnostics_config(repo_root)
    if overrides:
        for k, v in overrides.items():
            if v:
                cfg[k] = v

    repo = args.repo or default_repo or cfg["repo"]
    branch = args.branch or default_branch or cfg["branch"] or DEFAULT_BRANCH
    if not repo:
        print("ERROR: Could not detect GitHub owner/repo. Pass --repo owner/name or configure diagnostics.repo in planner_profile.json.")
        return 2

    if args.file:
        print_section(f"FILE ({repo} @ {branch} : {args.file})")
        code, body = fetch_raw_file(repo, branch, args.file)
        if code == 200:
            print(body.rstrip())
            return 0
        print(f"[HTTP {code}] {args.file} unavailable.")
        return 1

    if not (args.all or args.ci or args.lessons or args.selfcheck or args.summary or args.log):
        args.all = True

    head = fetch_branch_head(repo, branch)
    run = fetch_latest_workflow_run(repo)
    print_section(f"LIVE GITHUB SNAPSHOT ({repo} @ {branch})")
    if head:
        print(f"Branch '{branch}' HEAD: {head['sha']} ({head['date']}) - {head['message']}")
    else:
        print(f"Branch '{branch}': not created yet.")
    if run:
        print(
            f"Latest Actions run: {run['name']} | status={run['status']} "
            f"conclusion={run['conclusion']} | sha={run['sha']} | {run['created_at']}"
        )

    if args.all or args.ci:
        ci_p = cfg["ci_md"]
        print_section(f"CI BUILD DIAGNOSTICS ({ci_p})")
        code, body = fetch_raw_file(repo, branch, ci_p)
        if code == 200:
            lines = body.splitlines()
            print("\n".join(lines[:160]))
            if len(lines) > 160:
                print(f"... (+{len(lines) - 160} more lines)")
        else:
            print(f"[HTTP {code}] {ci_p} unavailable yet.")

    if args.all or args.lessons:
        les_p = cfg["lessons_md"]
        print_section(f"DYNAMIC BUILD LESSONS CANDIDATES ({les_p})")
        code, body = fetch_raw_file(repo, branch, les_p)
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] {les_p} unavailable yet.")

    if args.all or args.selfcheck:
        sc_p = cfg["selfcheck_md"]
        print_section(f"SELF-CHECK REPORT ({sc_p})")
        code, body = fetch_raw_file(repo, branch, sc_p)
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] {sc_p} unavailable yet.")

    if args.all or args.summary:
        sum_p = cfg["summary_json"]
        print_section(f"SESSION SUMMARY ({sum_p})")
        code, body = fetch_raw_file(repo, branch, sum_p)
        if code == 200:
            print(body.strip())
        else:
            print(f"[HTTP {code}] {sum_p} unavailable yet.")

    if args.all or args.log:
        log_p = cfg["log_txt"]
        print_section(f"LOG TAIL ({log_p}, last {args.tail} lines)")
        code, body = fetch_raw_file(repo, branch, log_p)
        if code != 200 and cfg.get("fallback_log_path"):
            fb_branch = cfg.get("fallback_log_branch") or "master"
            fb_path = cfg["fallback_log_path"]
            code, body = fetch_raw_file(repo, fb_branch, fb_path)
            if code == 200:
                print(f"(Fallback from {fb_branch}/{fb_path})")
        if code == 200:
            lines = body.splitlines()
            if args.errors_only:
                lines = [
                    ln for ln in lines
                    if any(tok in ln for tok in ("[WARN]", "[ERROR]", "[FATAL]", "=== Session started", "Exception"))
                ]
            print("\n".join(lines[-max(1, args.tail):]))
        else:
            print(f"[HTTP {code}] Log unavailable yet.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
