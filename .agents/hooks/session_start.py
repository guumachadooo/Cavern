#!/usr/bin/env python3
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def run_git(repo: Path, args: list[str]) -> str:
    try:
        res = subprocess.run(
            ["git", "-C", str(repo), *args],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        return res.stdout.strip() if res.returncode == 0 else ""
    except Exception:
        return ""


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="ignore")
    except Exception:
        return ""


def main() -> None:
    try:
        payload = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    except Exception:
        payload = {}

    # Working directory of hooks.json is .agents/; repo root is one level up unless cwd says otherwise.
    repo = Path(__file__).resolve().parents[2]
    ws_paths = payload.get("workspacePaths") or []
    if ws_paths:
        cand = Path(ws_paths[0])
        if (cand / ".git").exists():
            repo = cand

    branch = run_git(repo, ["branch", "--show-current"]) or "?"
    recent_commits = run_git(repo, ["log", "-n", "4", "--oneline"])
    dirty_lines = [l for l in run_git(repo, ["status", "-s"]).splitlines() if l.strip()]

    csproj = read_text(repo / "CavernSamples/CavernizeAndroid/CavernizeAndroid.csproj")
    m_ver = re.search(r"<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>", csproj)
    csproj_ver = m_ver.group(1) if m_ver else "?"

    wf = read_text(repo / ".github/workflows/BuildAndroid.yml")
    m_wf = re.search(r"-p:ApplicationDisplayVersion=([^\n\\]+)", wf)
    wf_ver = m_wf.group(1).strip() if m_wf else "?"

    has_termux_dotnet = os.path.exists("/root/.dotnet/dotnet")
    log_path = Path("/sdcard/Download/Cavernize/Logs/cavernize_log.txt")
    apk_dl = Path("/sdcard/Download/Cavernize-Android.apk")

    lines = [
        "[CavernizeAndroid — Estado Atual do Workspace]",
        f"- Repo: {repo} | Branch: {branch} | Working tree: {'limpa' if not dirty_lines else f'{len(dirty_lines)} arquivo(s) pendente(s)'}",
        f"- Versões configuradas: csproj={csproj_ver} | CI={wf_ver}",
    ]
    if recent_commits:
        lines.append("- Últimos commits:\n  " + "\n  ".join(recent_commits.splitlines()))
    if dirty_lines:
        preview = ", ".join(l.strip() for l in dirty_lines[:6])
        if len(dirty_lines) > 6:
            preview += f" (+{len(dirty_lines) - 6})"
        lines.append(f"- Pendências locais (possível trabalho em andamento): {preview}")
    if has_termux_dotnet:
        lines.append("- Ambiente Termux ARM64: `/root/.dotnet/dotnet` requer `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=20000000 DOTNET_EnableDiagnostics=0 ... -m:1 /p:UseSharedCompilation=false`; APK assinado final é gerado via `.github/workflows/BuildAndroid.yml`.")
    if apk_dl.exists():
        mb = apk_dl.stat().st_size / (1024 * 1024)
        lines.append(f"- Último APK em Downloads: `{apk_dl}` ({mb:.1f} MB)")
    if log_path.exists():
        lines.append(f"- Log compartilhado do aparelho disponível em: `{log_path}`")
    lines.append("- Memória histórica & referência One UI 9 sob demanda: `.agents/docs/PROJECT_HISTORY.md` e `.agents/docs/ONEUI9_REFERENCE.md`.")

    extra_raw = read_text(repo / ".agents/docs/EXTRA_REQUESTS.md")
    pending_extras: list[str] = []
    if extra_raw:
        m_sec = re.search(r"^##\s+Pedidos Extras Pendentes\s*\n(.*?)(?=^##\s+|\Z)", extra_raw, flags=re.MULTILINE | re.DOTALL)
        if m_sec:
            for ln in m_sec.group(1).splitlines():
                s = ln.strip()
                if not s or s.startswith("<!--") or s.lower().startswith("(nenhum pedido"):
                    continue
                pending_extras.append(s)
    if pending_extras:
        lines.append(f"- ATENÇÃO — Pedidos extras pendentes em `.agents/docs/EXTRA_REQUESTS.md` ({len(pending_extras)} linha(s)): leia e planeje-os antes de compilar/commitar!")
    else:
        lines.append("- Pedidos extras (conversas paralelas): leia `.agents/docs/EXTRA_REQUESTS.md` ao final da tarefa antes de commitar ou compilar o APK.")

    out = {
        "injectSteps": [
            {
                "systemMessage": {
                    "systemMessage": "\n".join(lines)
                }
            }
        ]
    }
    print(json.dumps(out, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("{}")
