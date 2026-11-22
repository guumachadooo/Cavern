#!/usr/bin/env python3
"""Autonomous Dual-Scope Self-Evolution Engine (Dynamic Fractal Planner v2.6.0).

Helps any AI agent automatically classify detected workflow, tooling, diagnostic,
guardrail, or UX/engineering improvements into:
  - `plugin-universal`: reusable across any repo -> implement in plugin + plugin sync/changelog.
  - `project-only`: specific to host app domain -> implement in host project + host sync ledger.
  - `dual-project+plugin`: universal engine in plugin + thin adapter/config/UI in host project ->
    implement BOTH in the same turn and update BOTH ledgers.

Zero third-party dependencies (Python stdlib only).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

UNIVERSAL_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("build/CI error learning & timelessness", re.compile(r"\b(?:error.?learn|build.?lessons|timeless|anti-?obsolescence|ci.?diag|diagnostic.?fetch|log.?digest)\b", re.I)),
    ("planner/hooks/triage/guardrails/verification", re.compile(r"\b(?:planner|hook|triage|verify_universal|plan_tool|guardrail|wiring.?gate|parity.?rule|diff.?rule|circuit.?breaker|eco.?mode|recon)\b", re.I)),
    ("multi-AI memory/sync/handoff/self-evolution", re.compile(r"\b(?:external_ai_sync|handoff|project_history|saved_ideas|extra_requests|self.?evolv|dual.?scope|sync.?ledger|auto.?migrat)\b", re.I)),
    ("generic relay/bridge protocol client", re.compile(r"\b(?:relay.?client|bridge.?client|cloudflare.?worker|hmac.?wake|encrypt-?then-?mac|live.?bridge.?protocol)\b", re.I)),
    ("reusable workflow efficiency tooling", re.compile(r"\b(?:universaliz|reusable.?script|workflow.?efficienc|quota.?sav|token.?diet|plugin)\b", re.I)),
)

PROJECT_SPECIFIC_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("audio/DSP/codec domain logic", re.compile(r"\b(?:cavern|hrir|brir|atmos|truehd|truehdd|e-?ac-?3|iamf|ambisonic|lgsr|vocal.?cortex|upmix|quickeq|autoeq|iso.?226|iso.?9613|dsp|ffmpeg|onnx)\b", re.I)),
    ("Android/UI/native app implementation", re.compile(r"\b(?:mainactivity|androidmanifest|androidstrings|oneui|biometricprompt|foregroundservice|keystore|appsettingsstore|\.cs\b|\.csproj\b|ndk|jni|apk)\b", re.I)),
    ("app product UI/business domain", re.compile(r"\b(?:screen|tab|button|modal|player|playlist|checkout|billing|database.?schema|migration|product.?copy)\b", re.I)),
)

PLUGIN_SYNC_HEADING = "## Plugin (`dynamic-fractal-planner`) Sync Entries (Newest First)"
HOST_SYNC_HEADING_RE = re.compile(r"^##\s+(?:Cavern Project|Project|Sync)\s+Entries.*$", re.MULTILINE)


def classify_improvement(
    description: str,
    files: list[str] | None = None,
    plugin_rel_prefix: str = ".agents/plugins/dynamic-fractal-planner",
) -> dict[str, Any]:
    """Classify an improvement into `plugin-universal`, `project-only`, or `dual-project+plugin`."""
    text = (description or "").strip()
    file_list = [f.replace("\\", "/").lstrip("./") for f in (files or []) if f]
    combined = text + "\n" + "\n".join(file_list)

    uni_hits: list[str] = []
    for label, pat in UNIVERSAL_PATTERNS:
        if pat.search(combined):
            uni_hits.append(label)

    proj_hits: list[str] = []
    for label, pat in PROJECT_SPECIFIC_PATTERNS:
        if pat.search(combined):
            proj_hits.append(label)

    touches_plugin = any(f.startswith(plugin_rel_prefix) for f in file_list)
    touches_project = any(not f.startswith(plugin_rel_prefix) for f in file_list)

    if touches_plugin:
        uni_hits.append("touches plugin directory")
    if touches_project:
        proj_hits.append("touches host project files")

    if uni_hits and proj_hits:
        scope = "dual-project+plugin"
        confidence = "high" if (len(uni_hits) + len(proj_hits)) >= 3 else "medium"
        actions = [
            "Implement reusable core in plugin (`scripts/` / `hooks/` / `rules/` / `tests/`)",
            "Add thin project wrapper/config/UI integration in host repo",
            "Log entry in BOTH host `EXTERNAL_AI_SYNC.md` (Project + Plugin sections) && plugin `.agents/docs/EXTERNAL_AI_SYNC.md`",
        ]
        ledgers = [
            ".agents/docs/EXTERNAL_AI_SYNC.md (Project + Plugin sections)",
            f"{plugin_rel_prefix}/.agents/docs/EXTERNAL_AI_SYNC.md",
            f"{plugin_rel_prefix}/CHANGELOG.md",
            ".agents/docs/PROJECT_HISTORY.md",
        ]
    elif uni_hits:
        scope = "plugin-universal"
        confidence = "high" if len(uni_hits) >= 2 else "medium"
        actions = [
            "Implement directly in plugin with zero host-project hardcoding",
            "Add regression test in plugin `tests/run_plugin_tests.py`",
            "Log entry in plugin `.agents/docs/EXTERNAL_AI_SYNC.md` && host `EXTERNAL_AI_SYNC.md` Plugin section",
        ]
        ledgers = [
            f"{plugin_rel_prefix}/.agents/docs/EXTERNAL_AI_SYNC.md",
            f"{plugin_rel_prefix}/CHANGELOG.md",
            ".agents/docs/EXTERNAL_AI_SYNC.md (Plugin section)",
        ]
    else:
        scope = "project-only"
        confidence = "high" if proj_hits else "low"
        actions = [
            "Implement inside host project without polluting universal plugin",
            "Log entry in host `.agents/docs/EXTERNAL_AI_SYNC.md` (Project section) && `PROJECT_HISTORY.md`",
        ]
        ledgers = [
            ".agents/docs/EXTERNAL_AI_SYNC.md (Project section)",
            ".agents/docs/PROJECT_HISTORY.md",
        ]

    return {
        "scope": scope,
        "confidence": confidence,
        "universal_signals": uni_hits,
        "project_signals": proj_hits,
        "recommended_actions": actions,
        "required_ledgers": ledgers,
    }


def _prepend_under_heading(md_path: Path, heading_re: re.Pattern[str], entry_block: str, fallback_heading: str) -> bool:
    if not md_path.is_file():
        md_path.parent.mkdir(parents=True, exist_ok=True)
        content = (
            f"# External AI Sync Ledger (`{md_path.name}`)\n\n"
            f"> STYLE|lossless symbolic telegraphic EN ONLY.\n\n"
            f"{fallback_heading}\n\n"
            f"{entry_block}\n"
        )
        md_path.write_text(content, encoding="utf-8")
        return True

    text = md_path.read_text(encoding="utf-8", errors="ignore")
    m = heading_re.search(text)
    if m:
        insert_at = m.end()
        updated = text[:insert_at] + "\n\n" + entry_block.strip() + "\n" + text[insert_at:].lstrip("\n")
    else:
        updated = text.rstrip() + f"\n\n{fallback_heading}\n\n{entry_block.strip()}\n"
    md_path.write_text(updated, encoding="utf-8")
    return True


def record_sync_entry(
    repo_root: Path,
    *,
    scope: str,
    title: str,
    summary: str,
    files: list[str],
    commit: str = "pending-push",
    agent: str = "AI",
    plugin_rel: str = ".agents/plugins/dynamic-fractal-planner",
) -> list[str]:
    """Prepend a telegraphic EN sync entry into the appropriate host and/or plugin sync ledgers."""
    date_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    files_str = ", ".join(f"`{f}`" for f in files) if files else "`-`"
    block = "\n".join(
        [
            f"### {date_str} | `{commit}` | `{agent}` | {title}",
            f"- `Scope`: `{scope}`",
            f"- `Summary`: {summary}",
            f"- `Files`: {files_str}",
        ]
    )

    touched: list[str] = []
    host_sync = repo_root / ".agents" / "docs" / "EXTERNAL_AI_SYNC.md"
    plugin_sync = repo_root / plugin_rel / ".agents" / "docs" / "EXTERNAL_AI_SYNC.md"

    if scope in ("project-only", "project", "dual-project+plugin", "dual"):
        _prepend_under_heading(
            host_sync,
            HOST_SYNC_HEADING_RE,
            block,
            "## Project Sync Entries (Newest First)",
        )
        touched.append(str(host_sync.relative_to(repo_root)))

    if scope in ("plugin-universal", "plugin", "dual-project+plugin", "dual"):
        _prepend_under_heading(
            host_sync,
            re.compile(r"^##\s+Plugin\b.*Sync Entries.*$", re.MULTILINE),
            block,
            PLUGIN_SYNC_HEADING,
        )
        if str(host_sync.relative_to(repo_root)) not in touched:
            touched.append(str(host_sync.relative_to(repo_root)))

        _prepend_under_heading(
            plugin_sync,
            re.compile(r"^##\s+(?:Plugin\b.*)?Sync Entries.*$", re.MULTILINE),
            block,
            "## Sync Entries (Newest First)",
        )
        touched.append(str(plugin_sync.relative_to(repo_root)))

    return touched


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Autonomous Dual-Scope Self-Evolution Classifier & Dual-Ledger Sync.")
    parser.add_argument("--workspace", default="", help="Workspace root override")
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_cls = sub.add_parser("classify", help="Classify an improvement as project-only, plugin-universal, or dual-project+plugin.")
    p_cls.add_argument("--desc", required=True, help="Improvement description")
    p_cls.add_argument("--files", nargs="*", default=[], help="Optional candidate file paths")
    p_cls.add_argument("--json", action="store_true", help="Output JSON")

    p_sync = sub.add_parser("sync", help="Prepend a telegraphic sync entry to host and/or plugin EXTERNAL_AI_SYNC.md.")
    p_sync.add_argument("--scope", choices=["project", "plugin", "dual"], required=True)
    p_sync.add_argument("--title", required=True)
    p_sync.add_argument("--summary", required=True)
    p_sync.add_argument("--files", nargs="*", default=[])
    p_sync.add_argument("--commit", default="pending-push")
    p_sync.add_argument("--agent", default="AI")

    args = parser.parse_args(argv)
    ws = Path(args.workspace).expanduser().resolve() if args.workspace else Path.cwd().resolve()

    if args.cmd == "classify":
        res = classify_improvement(args.desc, args.files)
        if args.json:
            print(json.dumps(res, indent=2, ensure_ascii=False))
        else:
            print(f"EVOLVE_SCOPE|{res['scope']}|conf={res['confidence']}")
            if res["universal_signals"]:
                print("  UNIVERSAL: " + " ; ".join(res["universal_signals"]))
            if res["project_signals"]:
                print("  PROJECT: " + " ; ".join(res["project_signals"]))
            print("  LEDGERS: " + " | ".join(res["required_ledgers"]))
        return 0

    if args.cmd == "sync":
        touched = record_sync_entry(
            ws,
            scope=args.scope,
            title=args.title,
            summary=args.summary,
            files=args.files,
            commit=args.commit,
            agent=args.agent,
        )
        print("EVOLVE_SYNC_OK|" + ",".join(touched))
        return 0

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
