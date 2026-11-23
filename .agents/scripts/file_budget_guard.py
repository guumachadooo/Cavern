#!/usr/bin/env python3
"""AI Studio File-Budget Guard & Lossless Cold-Archive Packer (Zero External Deps).

Protects repositories imported into platforms with file-count caps (e.g. AI Studio's
999-file import limit) by:
  1. Auditing total tracked/workspace files & top heaviest directories (`--status`, `--check`).
  2. Losslessly packing cold/legacy directories into deterministic `.tar.gz` bundles
     with embedded SHA-256 `MANIFEST.json` (`--pack ... --out archive/<name>.tar.gz`).
  3. Verifying SHA-256 & restoring archived files on demand (`--unpack archive/<name>.tar.gz`).
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import sys
import tarfile
from collections import Counter
from pathlib import Path
from typing import Any

SKIP_DIRS = frozenset({
    ".git", ".hg", ".svn", "node_modules", "bin", "obj", "dist", "build",
    "__pycache__", ".venv", "venv", ".idea", ".vs", ".gradle", ".cache",
    ".playwright-cli", ".aistudio",
})


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def scan_workspace_files(repo: Path) -> tuple[list[str], Counter[str]]:
    files: list[str] = []
    buckets: Counter[str] = Counter()
    for root, dirs, filenames in os.walk(repo):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
        rel_root = Path(root).relative_to(repo).as_posix()
        for fn in sorted(filenames):
            if fn.endswith((".pyc", ".pyo")):
                continue
            rel = fn if rel_root == "." else f"{rel_root}/{fn}"
            files.append(rel)
            parts = rel.split("/")
            bucket = "/".join(parts[:2]) if len(parts) > 2 else (parts[0] if len(parts) > 1 else "(root)")
            buckets[bucket] += 1
    return files, buckets


def pack_paths(
    repo: Path,
    targets: list[str],
    out_archive: Path,
    *,
    remove_sources: bool = False,
) -> dict[str, Any]:
    """Deterministically pack files/dirs into a gzipped tarball with embedded SHA-256 MANIFEST.json."""
    collected: list[str] = []
    for raw_t in targets:
        rel_t = raw_t.replace("\\", "/").strip("/")
        full = repo / rel_t
        if full.is_file():
            collected.append(rel_t)
        elif full.is_dir():
            for root, dirs, filenames in os.walk(full):
                dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
                for fn in sorted(filenames):
                    if fn.endswith((".pyc", ".pyo")):
                        continue
                    fp = Path(root) / fn
                    collected.append(fp.relative_to(repo).as_posix())
        else:
            raise FileNotFoundError(f"Pack target not found: {rel_t}")

    collected = sorted(set(collected))
    if not collected:
        raise ValueError("No files found in pack targets.")

    out_archive.parent.mkdir(parents=True, exist_ok=True)
    manifest_entries: list[dict[str, Any]] = []

    tmp_out = out_archive.with_suffix(out_archive.suffix + ".tmp")
    with tarfile.open(tmp_out, mode="w:gz", compresslevel=9) as tf:
        for rel in collected:
            fp = repo / rel
            raw = fp.read_bytes()
            digest = sha256_bytes(raw)
            manifest_entries.append({"path": rel, "bytes": len(raw), "sha256": digest})
            ti = tarfile.TarInfo(name=rel)
            ti.size = len(raw)
            ti.mtime = 0
            ti.mode = 0o755 if rel.endswith(".sh") else 0o644
            tf.addfile(ti, io.BytesIO(raw))

        manifest_bytes = json.dumps(
            {
                "schema": "cavern-cold-archive-v1",
                "fileCount": len(manifest_entries),
                "totalRawBytes": sum(e["bytes"] for e in manifest_entries),
                "files": manifest_entries,
            },
            indent=2,
        ).encode("utf-8")
        mti = tarfile.TarInfo(name="MANIFEST.json")
        mti.size = len(manifest_bytes)
        mti.mtime = 0
        mti.mode = 0o644
        tf.addfile(mti, io.BytesIO(manifest_bytes))

    # Verify readability & hashes before replacing or removing sources
    verify_archive(tmp_out)
    tmp_out.replace(out_archive)

    removed = 0
    if remove_sources:
        for rel in collected:
            fp = repo / rel
            if fp.is_file():
                fp.unlink()
                removed += 1
        # Prune now-empty parent directories inside packed targets
        for raw_t in targets:
            full = repo / raw_t.replace("\\", "/").strip("/")
            if full.is_dir():
                for root, dirs, files in os.walk(full, topdown=False):
                    rp = Path(root)
                    if not any(rp.iterdir()):
                        rp.rmdir()

    return {
        "ok": True,
        "archive": out_archive.relative_to(repo).as_posix() if out_archive.is_relative_to(repo) else str(out_archive),
        "packedFiles": len(collected),
        "removedSources": removed,
        "archiveBytes": out_archive.stat().st_size,
        "totalRawBytes": sum(e["bytes"] for e in manifest_entries),
    }


def verify_archive(archive_path: Path) -> dict[str, Any]:
    with tarfile.open(archive_path, mode="r:gz") as tf:
        members = {m.name: m for m in tf.getmembers() if m.isfile()}
        if "MANIFEST.json" not in members:
            raise ValueError(f"{archive_path}: missing MANIFEST.json")
        manifest = json.loads(tf.extractfile(members["MANIFEST.json"]).read().decode("utf-8"))
        for entry in manifest["files"]:
            rel = entry["path"]
            if rel not in members:
                raise ValueError(f"{archive_path}: missing entry {rel}")
            raw = tf.extractfile(members[rel]).read()
            if sha256_bytes(raw) != entry["sha256"]:
                raise ValueError(f"{archive_path}: SHA-256 mismatch for {rel}")
        return {"ok": True, "fileCount": len(manifest["files"]), "totalRawBytes": manifest["totalRawBytes"]}


def unpack_archive(archive_path: Path, dest_root: Path) -> dict[str, Any]:
    info = verify_archive(archive_path)
    restored = 0
    with tarfile.open(archive_path, mode="r:gz") as tf:
        for m in tf.getmembers():
            if not m.isfile() or m.name == "MANIFEST.json":
                continue
            norm = m.name.replace("\\", "/").lstrip("/")
            if ".." in norm.split("/"):
                raise ValueError(f"Unsafe path in archive: {m.name}")
            target = dest_root / norm
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(tf.extractfile(m).read())
            restored += 1
    return {"ok": True, "restoredFiles": restored, **info}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="AI Studio <999 File Budget Guard & Lossless Cold-Archive Packer.")
    parser.add_argument("--workspace", default=".")
    parser.add_argument("--status", action="store_true", help="Print file count, headroom to 999 & heaviest directories")
    parser.add_argument("--check", action="store_true", help="Exit non-zero if file count exceeds --fail threshold")
    parser.add_argument("--warn", type=int, default=850)
    parser.add_argument("--fail", type=int, default=920)
    parser.add_argument("--cap", type=int, default=999)
    parser.add_argument("--pack", nargs="+", default=None, help="Files/directories to pack into --out archive")
    parser.add_argument("--out", default="", help="Output .tar.gz path for --pack")
    parser.add_argument("--remove-sources", action="store_true", help="Remove packed loose files after SHA-256 verification")
    parser.add_argument("--verify", default="", help="Verify SHA-256 integrity of a packed archive")
    parser.add_argument("--unpack", default="", help="Restore files from a verified archive into --workspace")
    args = parser.parse_args(argv)

    repo = Path(args.workspace).resolve()

    if args.verify:
        res = verify_archive((repo / args.verify) if not Path(args.verify).is_absolute() else Path(args.verify))
        print(json.dumps(res, indent=2))
        return 0

    if args.unpack:
        arch = (repo / args.unpack) if not Path(args.unpack).is_absolute() else Path(args.unpack)
        res = unpack_archive(arch, repo)
        print(json.dumps(res, indent=2))
        return 0

    if args.pack:
        if not args.out:
            parser.error("--out is required with --pack")
        out_p = (repo / args.out) if not Path(args.out).is_absolute() else Path(args.out)
        res = pack_paths(repo, args.pack, out_p, remove_sources=bool(args.remove_sources))
        print(json.dumps(res, indent=2))
        return 0

    files, buckets = scan_workspace_files(repo)
    total = len(files)
    headroom = args.cap - total
    status = "FAIL" if total > args.fail else ("WARN" if total > args.warn else "OK")
    report = {
        "ok": status != "FAIL",
        "status": status,
        "totalFiles": total,
        "platformCap": args.cap,
        "headroomToCap": headroom,
        "warnThreshold": args.warn,
        "failThreshold": args.fail,
        "topDirectories": [{"dir": k, "files": v} for k, v in buckets.most_common(15)],
    }
    print(json.dumps(report, indent=2))
    if args.check and total > args.fail:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
