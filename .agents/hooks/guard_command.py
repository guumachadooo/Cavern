#!/usr/bin/env python3
import glob
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path


def allow() -> None:
    print(json.dumps({"decision": "allow"}))


def deny(reason: str) -> None:
    print(json.dumps({"decision": "deny", "reason": reason}, ensure_ascii=False))


def split_shell_segments(cmd: str) -> list[str]:
    """Split a shell command on unquoted separators (; && || | newline)."""
    segments: list[str] = []
    buf: list[str] = []
    in_sq = False
    in_dq = False
    escape = False
    i = 0
    n = len(cmd)

    while i < n:
        ch = cmd[i]
        if escape:
            buf.append(ch)
            escape = False
            i += 1
            continue
        if ch == "\\" and not in_sq:
            buf.append(ch)
            escape = True
            i += 1
            continue
        if ch == "'" and not in_dq:
            in_sq = not in_sq
            buf.append(ch)
            i += 1
            continue
        if ch == '"' and not in_sq:
            in_dq = not in_dq
            buf.append(ch)
            i += 1
            continue
        if not in_sq and not in_dq:
            if ch in ";\n":
                seg = "".join(buf).strip()
                if seg:
                    segments.append(seg)
                buf.clear()
                i += 1
                continue
            if cmd[i : i + 2] in ("&&", "||"):
                seg = "".join(buf).strip()
                if seg:
                    segments.append(seg)
                buf.clear()
                i += 2
                continue
            if ch == "|":
                seg = "".join(buf).strip()
                if seg:
                    segments.append(seg)
                buf.clear()
                i += 1
                continue
        buf.append(ch)
        i += 1

    tail = "".join(buf).strip()
    if tail:
        segments.append(tail)
    return segments


def strip_env_prefix(segment: str) -> tuple[dict[str, str], str]:
    """Return leading KEY=VAL env assignments and the remaining command invocation."""
    envs: dict[str, str] = {}
    rest = segment.lstrip()
    env_pat = re.compile(r"""^([A-Za-z_][A-Za-z0-9_]*)=('[^']*'|"[^"]*"|\S+)\s*""")
    while True:
        m = env_pat.match(rest)
        if not m:
            break
        envs[m.group(1)] = m.group(2).strip("'\"")
        rest = rest[m.end() :].lstrip()
    return envs, rest


def resolve_repo_root(payload: dict) -> Path:
    repo = Path(__file__).resolve().parents[2]
    ws_paths = payload.get("workspacePaths") or []
    if ws_paths and (Path(ws_paths[0]) / ".git").exists():
        repo = Path(ws_paths[0])
    return repo


def get_pending_extra_requests(repo: Path) -> list[str]:
    extra_file = repo / ".agents/docs/EXTRA_REQUESTS.md"
    if not extra_file.exists():
        return []
    try:
        text = extra_file.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return []
    m = re.search(
        r"^##\s+(?:Pending Extra Requests|Pedidos Extras Pendentes)\s*\n(.*?)(?=^##\s+|\Z)",
        text,
        flags=re.MULTILINE | re.DOTALL,
    )
    if not m:
        return []
    pending: list[str] = []
    for ln in m.group(1).splitlines():
        s = ln.strip()
        sl = s.lower()
        if (
            not s
            or s.startswith("<!--")
            or sl.startswith("(no pending")
            or sl.startswith("(none")
            or sl.startswith("(nenhum pedido")
        ):
            continue
        pending.append(s)
    return pending


def check_extra_requests_before_ship(segments: list[str], payload: dict) -> str | None:
    """Block git commit / git push whenever .agents/docs/EXTRA_REQUESTS.md has pending items."""
    is_ship_cmd = False
    for seg in segments:
        _, rest = strip_env_prefix(seg)
        if re.match(r"^git(?:\s+-C\s+\S+)?\s+(commit|push)\b", rest):
            is_ship_cmd = True
            break
    if not is_ship_cmd:
        return None

    repo = resolve_repo_root(payload)
    pending = get_pending_extra_requests(repo)
    if not pending:
        return None

    preview = "\n".join(f"  - {line[:120]}" for line in pending[:5])
    if len(pending) > 5:
        preview += f"\n  (+{len(pending) - 5} line(s))"
    return (
        "BLOCKED BEFORE COMMIT/BUILD: there are pending extra requests in "
        "`.agents/docs/EXTRA_REQUESTS.md` queued from parallel `/btw` conversations:\n"
        f"{preview}\n\n"
        "Read `.agents/docs/EXTRA_REQUESTS.md`, write a new staged plan (as if starting a fresh prompt), "
        "implement and verify every item, move them to `## Completed Requests` "
        "(restoring `(No pending requests.)` under `## Pending Extra Requests`), and only then commit/build the APK."
    )


def check_commit_memory_feedback(segments: list[str], payload: dict) -> str | None:
    """Remind once per code delivery if PROJECT_HISTORY.md was not updated recently."""
    commit_seg = None
    for seg in segments:
        _, rest = strip_env_prefix(seg)
        if re.match(r"^(?:git(?:\s+-C\s+\S+)?\s+commit)\b", rest):
            commit_seg = seg
            break

    if not commit_seg:
        return None
    if "--amend" in commit_seg or "[skip-history]" in commit_seg:
        return None

    repo = resolve_repo_root(payload)

    try:
        res = subprocess.run(
            ["git", "-C", str(repo), "status", "--porcelain"],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        changed = [l[3:].strip() for l in res.stdout.splitlines() if len(l) > 3]
    except Exception:
        return None

    code_exts = (".cs", ".csproj", ".xml", ".yml", ".yaml")
    changed_code = [f for f in changed if f.endswith(code_exts)]
    if len(changed_code) <= 1:
        return None

    now = time.time()
    memory_files = [
        repo / ".agents/docs/PROJECT_HISTORY.md",
        repo / ".agents/docs/ONEUI9_REFERENCE.md",
        repo / "AGENTS.md",
        repo / "CavernSamples/CavernizeAndroid/AGENTS.md",
    ]
    for mf in memory_files:
        if mf.exists() and (now - mf.stat().st_mtime) < 3600:
            return None

    conv_id = re.sub(r"[^a-zA-Z0-9_-]", "", str(payload.get("conversationId") or "default"))
    marker = Path(f"/tmp/.cavern_hist_reminded_{conv_id}")
    if marker.exists() and (now - marker.stat().st_mtime) < 900:
        return None

    try:
        marker.touch()
    except Exception:
        pass

    return (
        "Memory Feedback Reminder: multiple code files are modified in this commit "
        "without a recent update to `.agents/docs/PROJECT_HISTORY.md`. "
        "If this cycle fixed a non-obvious bug, changed UX/architecture, or shipped a new version, "
        "prepend a concise 1–4 line summary under `## 3. Version History (Newest First)` in `PROJECT_HISTORY.md` "
        "(and update `ONEUI9_REFERENCE.md` / `AGENTS.md` if applicable) before committing — or retry `git commit` if no new entry is needed."
    )


def main() -> None:
    payload = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    tool = payload.get("toolCall") or {}
    args = tool.get("args") or {}
    cmd = (args.get("CommandLine") or "").strip()

    if not cmd:
        allow()
        return

    segments = split_shell_segments(cmd)

    extra_msg = check_extra_requests_before_ship(segments, payload)
    if extra_msg:
        deny(extra_msg)
        return

    feedback_msg = check_commit_memory_feedback(segments, payload)
    if feedback_msg:
        deny(feedback_msg)
        return

    # Termux ARM64 PRoot guards only activate when running inside /root/.dotnet environment
    if not os.path.exists("/root/.dotnet/dotnet"):
        allow()
        return

    has_android_pack = bool(glob.glob("/root/.dotnet/packs/Microsoft.Android*"))

    # Track exported env vars across earlier segments in the same command chain
    session_envs: set[str] = set()
    for seg in segments:
        stripped = seg.strip()
        if re.match(r"^export\s+", stripped):
            for em in re.finditer(r"(?:^|\s)([A-Za-z_][A-Za-z0-9_]*)=", stripped):
                session_envs.add(em.group(1))

        inline_envs, rest = strip_env_prefix(stripped)
        if not re.match(r"^(?:/root/\.dotnet/)?dotnet\s+(build|publish|test|pack|restore|run)\b", rest):
            continue

        targets_android = (
            "CavernizeAndroid.csproj" in rest
            or "net11.0-android" in rest
            or "InstallAndroidDependencies" in rest
        )
        if targets_android and not has_android_pack:
            deny(
                "Local Termux ARM64 PRoot lacks the Android SDK / `net11.0-android` workload. "
                "Validate shared logic locally by building `CavernSamples/Reusable/Cavernize.Logic/Cavernize.Logic.csproj` "
                "(or `Cavern`/`Cavern.Format`), and build the signed APK via GitHub Actions (`.github/workflows/BuildAndroid.yml`)."
            )
            return

        active_envs = session_envs | set(inline_envs.keys())
        missing = []
        if "DOTNET_GCHeapHardLimit" not in active_envs:
            missing.append("DOTNET_GCHeapHardLimit=20000000")
        if "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" not in active_envs:
            missing.append("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1")
        if missing:
            deny(
                "CoreCLR fails in Termux ARM64 PRoot without required env vars ("
                + ", ".join(missing)
                + "). Run with:\n"
                "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 "
                "/root/.dotnet/dotnet <subcommand> -m:1 /p:UseSharedCompilation=false"
            )
            return

    allow()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print('{"decision": "allow"}')
