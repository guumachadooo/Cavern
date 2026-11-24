#!/usr/bin/env python3
"""
Cavernize Live Adversarial Bug-Hunter & Chaos Fuzzer (`cavern_bug_hunter.py`).

Actively drives the running Cavernize Android app on the user's device via OmniAgent
local signature-verified IPC (`cavernize_cmd`), continuously diffing BOTH `AppLogger`
and Android `logcat` (`*:W`, crash buffer, Choreographer/GC/AAudio/MediaCodec/ANR)
after every adversarial step to uncover real-world bugs, race conditions, crashes,
warnings, UI defects, acoustic anomalies, decoder failures, and memory leaks.

Zero external Python dependencies (stdlib only). Guaranteed settings & state restore.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

AGENT_DIR = Path(__file__).resolve().parent
REPO_ROOT = AGENT_DIR.parents[1]
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

import omni_agent_client as oac  # noqa: E402

# ---------------------------------------------------------------------------
# Pure Parsers, Classifiers & Normalizers (100% unit-testable offline)
# ---------------------------------------------------------------------------

APPLOG_LINE_RE = re.compile(
    r"^\[(?P<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]\s+"
    r"\[(?P<level>INFO|WARN|ERROR|FATAL|DEBUG)\]\s+"
    r"(?:\[T#(?P<thread>[^\]]+)\]\s+)?"
    r"\[(?P<tag>[^\]]+)\]\s*(?P<msg>.*)$"
)

LOGCAT_BRIEF_RE = re.compile(
    r"^(?P<level>[VDIWEFA])/(?P<tag>[^(\s]+)\s*\(\s*(?P<pid>\d+)\):\s*(?P<msg>.*)$"
)

LOGCAT_THREADTIME_RE = re.compile(
    r"^(?P<date>\d{2}-\d{2})\s+(?P<time>\d{2}:\d{2}:\d{2}\.\d+)\s+"
    r"(?P<pid>\d+)\s+(?P<tid>\d+)\s+(?P<level>[VDIWEFA])\s+"
    r"(?P<tag>[^:]+):\s*(?P<msg>.*)$"
)

CHOREOGRAPHER_SKIP_RE = re.compile(r"Skipped\s+(\d+)\s+frames", re.IGNORECASE)
UNFORMATTED_PLACEHOLDER_RE = re.compile(r"\{\d+(?::[^}]+)?\}")
CAMEL_KEY_RE = re.compile(r"^[A-Z][a-z]+[A-Z][A-Za-z0-9]{4,}$")

# Benign Android / .NET child-process fork noise to exclude from findings
BENIGN_LOGCAT_PATTERNS = (
    re.compile(r"libsigchain.*Setting SIGSEGV to SIG_DFL", re.IGNORECASE),
    re.compile(r"libsigchain.*#\d{2}\s+pc\s+", re.IGNORECASE),
    re.compile(r"ImeTracker.*onCancelled at PHASE_CLIENT_ALREADY_HIDDEN", re.IGNORECASE),
    re.compile(r"ImeFocusController.*skipped hasWindowFocus=false", re.IGNORECASE),
    re.compile(r"ViewPostIme pointer", re.IGNORECASE),
    re.compile(r"Input channel (constructed|destroyed)", re.IGNORECASE),
)

HIGH_SIGNAL_LOGCAT_PATTERNS: tuple[tuple[str, str, re.Pattern[str]], ...] = (
    ("CRITICAL", "crash", re.compile(r"FATAL EXCEPTION|SIGSEGV|SIGABRT|SIGBUS|JNI DETECTED ERROR|Fatal signal|ANR in", re.IGNORECASE)),
    ("CRITICAL", "oom", re.compile(r"OutOfMemoryError|lowmemorykiller|Throwing OutOfMemoryError", re.IGNORECASE)),
    ("HIGH", "exception", re.compile(r"UnhandledException|NullReferenceException|ObjectDisposedException|InvalidOperationException|DeadObjectException|IllegalStateException|RuntimeException", re.IGNORECASE)),
    ("HIGH", "audio_native", re.compile(r"AAudio.*(error|disconnect|invalid|xrun|underrun|timeout|failed)|AudioTrack.*(error|dead|underrun|failed)|OpenSL.*error", re.IGNORECASE)),
    ("HIGH", "codec", re.compile(r"MediaCodec.*(error|failed|codecException)|MediaExtractor.*error|FFmpeg.*(error|failed)|truehdd.*(error|failed)", re.IGNORECASE)),
    ("MEDIUM", "ui_jank", re.compile(r"Choreographer.*Skipped\s+(\d+)\s+frames", re.IGNORECASE)),
    ("MEDIUM", "strict_mode", re.compile(r"StrictMode.*(violation|DiskRead|DiskWrite|Network)", re.IGNORECASE)),
    ("MEDIUM", "graphics", re.compile(r"OpenGLRenderer.*(error|failed|Surface|timeout|lost)|BufferQueue.*abandon|EGL.*error", re.IGNORECASE)),
    ("LOW", "gc_stall", re.compile(r"GC freed.*paused\s+[^,]+,(\d+(?:\.\d+)?)ms", re.IGNORECASE)),
)

SEVERITY_RANK = {"CRITICAL": 0, "HIGH": 1, "MEDIUM": 2, "LOW": 3, "INFO": 4}


def normalize_signature(source: str, category: str, text: str) -> str:
    """Create a stable deduplication signature by stripping volatile numbers/timestamps/addrs."""
    s = (text or "").strip()
    s = re.sub(r"0x[0-9a-fA-F]+", "0xADDR", s)
    s = re.sub(r"\b[0-9a-f]{8,}\b", "HEXID", s)
    s = re.sub(r"\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?Z?", "TS", s)
    s = re.sub(r"\b\d+(?:\.\d+)?\s*(?:ms|us|s|KB|MB|GB|Hz|dB|%|px|f)\b", "NUM", s)
    s = re.sub(r"T#\d+", "T#N", s)
    s = re.sub(r"\(\d+\)", "(N)", s)
    s = re.sub(r"\b\d{2,}\b", "N", s)
    s = re.sub(r"\s+", " ", s)[:180]
    raw = f"{source}|{category}|{s}".lower()
    digest = hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]
    return f"{source}:{category}:{digest}"


def parse_app_logger_lines(text: str) -> list[dict[str, str]]:
    """Parse structured AppLogger lines into dicts."""
    entries: list[dict[str, str]] = []
    for raw_line in (text or "").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        m = APPLOG_LINE_RE.match(line)
        if m:
            entries.append({
                "ts": m.group("ts"),
                "level": m.group("level"),
                "thread": m.group("thread") or "",
                "tag": m.group("tag"),
                "msg": m.group("msg").strip(),
                "raw": line,
            })
        elif any(tok in line for tok in ("[ERROR]", "[WARN]", "[FATAL]", "Exception:", "FAIL [")):
            lvl = "FATAL" if "[FATAL]" in line else ("ERROR" if "[ERROR]" in line or "Exception" in line else "WARN")
            entries.append({
                "ts": "",
                "level": lvl,
                "thread": "",
                "tag": "RAW",
                "msg": line,
                "raw": line,
            })
    return entries


def is_benign_logcat_line(line: str) -> bool:
    for pat in BENIGN_LOGCAT_PATTERNS:
        if pat.search(line):
            return True
    return False


def parse_logcat_anomalies(text: str) -> list[dict[str, Any]]:
    """Extract genuine warnings, errors, fatals, jank & crashes from Android logcat output."""
    anomalies: list[dict[str, Any]] = []
    for raw_line in (text or "").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("--------- beginning of"):
            continue
        if is_benign_logcat_line(line):
            continue

        level = ""
        tag = ""
        msg = line
        m = LOGCAT_THREADTIME_RE.match(line)
        if m:
            level = m.group("level")
            tag = m.group("tag").strip()
            msg = m.group("msg").strip()
        else:
            m2 = LOGCAT_BRIEF_RE.match(line)
            if m2:
                level = m2.group("level")
                tag = m2.group("tag").strip()
                msg = m2.group("msg").strip()

        matched = False
        for sev, cat, pat in HIGH_SIGNAL_LOGCAT_PATTERNS:
            hm = pat.search(line)
            if not hm:
                continue
            if cat == "ui_jank":
                try:
                    skipped = int(hm.group(1))
                except (ValueError, IndexError):
                    skipped = 0
                if skipped < 25:
                    matched = True
                    break
                sev = "HIGH" if skipped >= 60 else "MEDIUM"
            elif cat == "gc_stall":
                try:
                    pause_ms = float(hm.group(1))
                except (ValueError, IndexError):
                    pause_ms = 0.0
                if pause_ms < 25.0:
                    matched = True
                    break
            anomalies.append({
                "severity": sev,
                "category": cat,
                "level": level or "E",
                "tag": tag or "logcat",
                "message": f"{tag}: {msg}" if tag else msg,
                "raw": line,
            })
            matched = True
            break

        if not matched and level in ("E", "F", "A"):
            anomalies.append({
                "severity": "CRITICAL" if level in ("F", "A") else "HIGH",
                "category": "logcat_error",
                "level": level,
                "tag": tag or "logcat",
                "message": f"{tag}: {msg}" if tag else msg,
                "raw": line,
            })
        elif not matched and level == "W":
            # Filter noisy Android framework warnings unless Cavern/audio/media/UI relevant
            relevant_tags = (
                "cavern", "mono", "dotnet", "audio", "aaudio", "media", "codec",
                "exo", "ffmpeg", "onnx", "opengl", "choreographer", "window",
                "view", "input", "strictmode", "activity", "system.err",
            )
            low_line = line.lower()
            if any(rt in low_line for rt in relevant_tags):
                anomalies.append({
                    "severity": "LOW",
                    "category": "logcat_warn",
                    "level": level,
                    "tag": tag or "logcat",
                    "message": f"{tag}: {msg}" if tag else msg,
                    "raw": line,
                })
    return anomalies


def analyze_ui_tree_anomalies(tree_obj: Any, scope: str) -> list[dict[str, str]]:
    """Inspect a `ui_tree` JSON object for real user-visible UI defects."""
    defects: list[dict[str, str]] = []
    if not isinstance(tree_obj, dict):
        return defects

    active_dialogs = int(tree_obj.get("activeDialogs") or 0)
    if active_dialogs > 1:
        defects.append({
            "severity": "MEDIUM",
            "category": "ui_stacked_dialogs",
            "message": f"{scope}: {active_dialogs} stacked modal dialogs simultaneously open",
        })

    def walk(node: Any, depth: int) -> None:
        if not isinstance(node, dict):
            return
        if depth > 22:
            defects.append({
                "severity": "LOW",
                "category": "ui_deep_hierarchy",
                "message": f"{scope}: View hierarchy exceeds depth 22 at {node.get('type')}",
            })
            return
        txt = str(node.get("text") or "")
        ntype = str(node.get("type") or "View")
        if txt:
            if UNFORMATTED_PLACEHOLDER_RE.search(txt):
                defects.append({
                    "severity": "HIGH",
                    "category": "ui_placeholder_leak",
                    "message": f"{scope}: Unformatted placeholder in {ntype}: '{txt[:80]}'",
                })
            if re.search(r"\b(NaN|Infinity|-Infinity)\b", txt):
                defects.append({
                    "severity": "HIGH",
                    "category": "ui_nan_leak",
                    "message": f"{scope}: NaN/Infinity text visible in {ntype}: '{txt[:80]}'",
                })
            if CAMEL_KEY_RE.match(txt.strip()) and any(
                suffix in txt for suffix in ("Btn", "Label", "Title", "Desc", "Hint", "Error", "Toast", "Modal", "Tab")
            ):
                defects.append({
                    "severity": "MEDIUM",
                    "category": "ui_raw_string_key",
                    "message": f"{scope}: Possible untranslated string key in {ntype}: '{txt[:60]}'",
                })
        w = int(node.get("w") or 0)
        h = int(node.get("h") or 0)
        clickable = bool(node.get("clickable"))
        if clickable and ntype in ("Button", "OneUiButton") and (w <= 0 or h <= 0):
            defects.append({
                "severity": "LOW",
                "category": "ui_zero_size_button",
                "message": f"{scope}: Visible clickable {ntype} '{txt[:40]}' has zero size ({w}x{h})",
            })
        for ch in node.get("children") or []:
            walk(ch, depth + 1)

    if tree_obj.get("activity"):
        walk(tree_obj["activity"], 0)
    for idx, dlg in enumerate(tree_obj.get("dialogs") or []):
        walk(dlg, 0)
    return defects


def analyze_acoustic_probe_result(probe_obj: Any, label: str) -> list[dict[str, str]]:
    """Detect psychoacoustic/DSP anomalies in an `acoustic_probe` JSON payload."""
    issues: list[dict[str, str]] = []
    if not isinstance(probe_obj, dict):
        return issues

    raw_str = json.dumps(probe_obj)
    if re.search(r"\b(NaN|Infinity|-Infinity)\b", raw_str):
        issues.append({
            "severity": "CRITICAL",
            "category": "dsp_nan_inf",
            "message": f"Acoustic probe '{label}' emitted NaN/Infinity",
        })

    # Inspect nested fingerprint / binaural metrics regardless of exact casing
    def find_num(obj: Any, *keys: str) -> float | None:
        if not isinstance(obj, dict):
            return None
        lower_map = {k.lower(): v for k, v in obj.items()}
        for k in keys:
            val = lower_map.get(k.lower())
            if isinstance(val, (int, float)):
                return float(val)
        for v in obj.values():
            if isinstance(v, dict):
                res = find_num(v, *keys)
                if res is not None:
                    return res
        return None

    non_finite = find_num(probe_obj, "nonFiniteSamples", "nonFinite", "nanCount")
    if non_finite is not None and non_finite > 0:
        issues.append({
            "severity": "CRITICAL",
            "category": "dsp_non_finite",
            "message": f"Acoustic probe '{label}' reported {int(non_finite)} non-finite samples",
        })

    gain_db = find_num(probe_obj, "gainDb", "outputGainDb", "rmsDb")
    if gain_db is not None and gain_db < -85.0:
        issues.append({
            "severity": "HIGH",
            "category": "dsp_silent_output",
            "message": f"Acoustic probe '{label}' collapsed to near-silence (Gain={gain_db:.1f} dB)",
        })

    peak_db = find_num(probe_obj, "peakDbfs", "peakDb")
    if peak_db is not None and peak_db > 1.5:
        issues.append({
            "severity": "MEDIUM",
            "category": "dsp_clipping",
            "message": f"Acoustic probe '{label}' exceeded digital full scale (Peak={peak_db:+.2f} dBFS)",
        })

    status = str(probe_obj.get("status") or probe_obj.get("verdict") or "").upper()
    if status == "FAIL":
        issues.append({
            "severity": "HIGH",
            "category": "dsp_probe_fail",
            "message": f"Acoustic probe '{label}' returned FAIL: {probe_obj.get('verdictNote') or probe_obj.get('error') or raw_str[:160]}",
        })
    elif status == "WARN":
        issues.append({
            "severity": "MEDIUM",
            "category": "dsp_probe_warn",
            "message": f"Acoustic probe '{label}' returned WARN: {probe_obj.get('verdictNote') or raw_str[:160]}",
        })
    return issues


def classify_applog_severity(entry: dict[str, str]) -> tuple[str, str]:
    """Map an AppLogger entry to (severity, category)."""
    lvl = entry.get("level", "INFO").upper()
    tag = entry.get("tag", "SYS").upper()
    msg = entry.get("msg", "")
    low = msg.lower()

    if lvl == "FATAL" or "unhandledexception" in low or "crash marker" in low:
        return "CRITICAL", f"applog_{tag.lower()}_fatal"
    if lvl == "ERROR":
        return "HIGH", f"applog_{tag.lower()}_error"
    if "selfcheck fail" in low:
        return "HIGH", "selfcheck_fail"
    if "disconnect" in low or "underrun" in low or "xrun" in low:
        return "MEDIUM", "audio_dropout"
    if "slow:" in low or "stall" in low or "jank" in low or "slower-than-budget" in low:
        return "MEDIUM", "perf_jank"
    if lvl == "WARN":
        return "LOW", f"applog_{tag.lower()}_warn"
    return "INFO", f"applog_{tag.lower()}"


@dataclass
class Finding:
    signature: str
    severity: str
    vector: str
    source: str
    category: str
    title: str
    repro_step: str
    evidence: str
    count: int = 1
    first_seen_utc: str = ""
    last_seen_utc: str = ""
    extra_examples: list[str] = field(default_factory=list)


class FindingLedger:
    """Deduplicating bug ledger ranked by severity and frequency."""

    def __init__(self) -> None:
        self._items: dict[str, Finding] = {}

    def record(
        self,
        *,
        severity: str,
        vector: str,
        source: str,
        category: str,
        title: str,
        repro_step: str,
        evidence: str,
    ) -> tuple[bool, Finding]:
        sev = severity.upper() if severity.upper() in SEVERITY_RANK else "MEDIUM"
        sig = normalize_signature(source, category, title)
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        existing = self._items.get(sig)
        if existing is not None:
            existing.count += 1
            existing.last_seen_utc = now
            if SEVERITY_RANK[sev] < SEVERITY_RANK[existing.severity]:
                existing.severity = sev
            ev_short = (evidence or "").strip()[:300]
            if ev_short and ev_short != existing.evidence and len(existing.extra_examples) < 3:
                if ev_short not in existing.extra_examples:
                    existing.extra_examples.append(ev_short)
            return False, existing

        f = Finding(
            signature=sig,
            severity=sev,
            vector=vector,
            source=source,
            category=category,
            title=(title or "").strip()[:240],
            repro_step=(repro_step or "").strip()[:240],
            evidence=(evidence or "").strip()[:400],
            count=1,
            first_seen_utc=now,
            last_seen_utc=now,
        )
        self._items[sig] = f
        return True, f

    def ranked(self) -> list[Finding]:
        return sorted(
            self._items.values(),
            key=lambda x: (SEVERITY_RANK.get(x.severity, 9), -x.count, x.vector, x.title),
        )

    def counts_by_severity(self) -> dict[str, int]:
        counts = {"CRITICAL": 0, "HIGH": 0, "MEDIUM": 0, "LOW": 0}
        for f in self._items.values():
            if f.severity in counts:
                counts[f.severity] += 1
        return counts

    def to_dicts(self) -> list[dict[str, Any]]:
        return [
            {
                "signature": f.signature,
                "severity": f.severity,
                "vector": f.vector,
                "source": f.source,
                "category": f.category,
                "title": f.title,
                "reproStep": f.repro_step,
                "evidence": f.evidence,
                "count": f.count,
                "firstSeenUtc": f.first_seen_utc,
                "lastSeenUtc": f.last_seen_utc,
                "extraExamples": f.extra_examples,
            }
            for f in self.ranked()
        ]


def generate_report_markdown(report: dict[str, Any]) -> str:
    """Render a concise, actionable Markdown bug dossier."""
    lines: list[str] = []
    meta = report.get("meta") or {}
    counts = report.get("severityCounts") or {}
    lines.append("# Cavernize Live Adversarial Bug-Hunter Report")
    lines.append("")
    lines.append(
        f"- **App:** `v{meta.get('appVersion', '?')}` (build `{meta.get('appBuild', '?')}`) | "
        f"**Device:** `{meta.get('device', '?')}` | **Android:** `{meta.get('android', '?')}`"
    )
    lines.append(
        f"- **Mode:** `{meta.get('mode', '?')}` | **Steps Executed:** `{meta.get('stepsExecuted', 0)}` | "
        f"**Elapsed:** `{meta.get('elapsedSec', 0):.1f}s`"
    )
    lines.append(
        f"- **Unique Findings:** `{report.get('totalUniqueFindings', 0)}` "
        f"(`CRITICAL={counts.get('CRITICAL', 0)}` · `HIGH={counts.get('HIGH', 0)}` · "
        f"`MEDIUM={counts.get('MEDIUM', 0)}` · `LOW={counts.get('LOW', 0)}`)"
    )
    if meta.get("restoredState") is not None:
        lines.append(f"- **State Restored Cleanly:** `{meta.get('restoredState')}`")
    lines.append("")

    vectors = report.get("vectors") or []
    if vectors:
        lines.append("## Vector Execution Summary")
        lines.append("")
        lines.append("| Vector | Status | New Findings | Elapsed | Notes |")
        lines.append("|---|---|---:|---:|---|")
        for v in vectors:
            notes = str(v.get("notes") or "").replace("|", "\\|").replace("\n", " ")[:120]
            lines.append(
                f"| `{v.get('name')}` | **{v.get('status')}** | {v.get('newFindings', 0)} | "
                f"{v.get('elapsedSec', 0):.1f}s | {notes} |"
            )
        lines.append("")

    findings = report.get("findings") or []
    if not findings:
        lines.append("## Findings")
        lines.append("")
        lines.append("No anomalies or errors triggered during this hunt.")
        return "\n".join(lines) + "\n"

    lines.append("## Ranked Bugs & Anomalies (Deduplicated)")
    lines.append("")
    for idx, f in enumerate(findings, 1):
        lines.append(
            f"### {idx}. [{f['severity']}] `{f['category']}` ({f['count']}x) — {f['title']}"
        )
        lines.append(f"- **Vector:** `{f['vector']}` | **Source:** `{f['source']}` | **Sig:** `{f['signature']}`")
        lines.append(f"- **Trigger / Repro:** `{f['reproStep']}`")
        lines.append(f"- **Evidence:** `{f['evidence']}`")
        for ex in f.get("extraExamples") or []:
            lines.append(f"  - *Also seen:* `{ex}`")
        lines.append("")
    return "\n".join(lines)


def pick_edge_case_library_tracks(tracks: list[dict[str, Any]], max_picks: int = 6) -> list[tuple[str, dict[str, Any]]]:
    """Select diverse & edge-case real tracks from the user's media library."""
    picks: list[tuple[str, dict[str, Any]]] = []
    seen_paths: set[str] = set()

    def add(reason: str, t: dict[str, Any]) -> None:
        p = str(t.get("FilePath") or "")
        if not p or p in seen_paths or len(picks) >= max_picks:
            return
        seen_paths.add(p)
        picks.append((reason, t))

    for t in tracks:
        if int(t.get("DurationMs") or 0) <= 0:
            add("zero_duration_metadata", t)
            break
    for t in tracks:
        if bool(t.get("IsSpatial")) or int(t.get("ChannelCount") or 0) > 2:
            add("spatial_or_multichannel", t)
            break
    for t in tracks:
        if int(t.get("ChannelCount") or 0) == 1:
            add("mono_channel", t)
            break
    for t in tracks:
        p = str(t.get("FilePath") or "")
        if any(ord(ch) > 127 or ch in "()'&®" for ch in p):
            add("unicode_or_special_chars", t)
            break
    seen_badges: set[str] = set()
    for t in tracks:
        badge = str(t.get("FormatBadge") or "").upper()
        if badge and badge not in seen_badges:
            seen_badges.add(badge)
            add(f"format_{badge.lower()}", t)
    return picks


# ---------------------------------------------------------------------------
# Live Device Transport + Adversarial Orchestrator
# ---------------------------------------------------------------------------

class LiveCavernBridge:
    """Thin wrapper over OmniAgent `cavernize_cmd` with structured JSON decoding."""

    def __init__(self, relay: str, secret: str, admin: str, timeout_sec: int = 45) -> None:
        self.relay = relay
        self.secret = secret
        self.admin = admin
        self.timeout_sec = timeout_sec
        self.calls = 0

    def cmd(self, name: str, args: dict[str, Any] | None = None, tier: int = 2, timeout_sec: int | None = None) -> dict[str, Any]:
        self.calls += 1
        t_out = timeout_sec or self.timeout_sec
        payload = oac.build_cavernize_cmd_payload(name, json.dumps(args or {}), tier, t_out)
        t0 = time.monotonic()
        try:
            res = oac.send_agent_command(
                self.relay,
                self.secret,
                "cavernize_cmd",
                payload,
                channel="android",
                admin_key=self.admin,
                timeout_sec=max(60, t_out + 15),
            )
        except Exception as exc:
            return {
                "ok": False,
                "subCmd": name,
                "elapsedMs": int((time.monotonic() - t0) * 1000),
                "body": "",
                "json": None,
                "error": f"{type(exc).__name__}: {exc}",
            }

        body = str(res.get("body") or "")
        parsed = None
        if body.strip().startswith(("{", "[")):
            try:
                parsed = json.loads(body)
            except Exception:
                parsed = None
        return {
            "ok": bool(res.get("ok")),
            "subCmd": name,
            "elapsedMs": int(res.get("elapsedMs") or ((time.monotonic() - t0) * 1000)),
            "body": body,
            "json": parsed,
            "error": str(res.get("error") or ""),
        }

    def pipeline(self, steps: list[dict[str, Any]], stop_on_error: bool = False, tier: int = 2, timeout_sec: int = 45) -> list[dict[str, Any]]:
        """Execute up to 30 commands in ONE round-trip (millisecond-burst stress + 10x lower latency)."""
        if not steps:
            return []
        res = self.cmd("pipeline", {"steps": steps[:30], "stopOnError": stop_on_error}, tier=tier, timeout_sec=timeout_sec)
        out: list[dict[str, Any]] = []
        if isinstance(res.get("json"), dict) and isinstance(res["json"].get("results"), list):
            for item in res["json"]["results"]:
                body = str(item.get("body") or "")
                parsed = None
                if body.strip().startswith(("{", "[")):
                    try:
                        parsed = json.loads(body)
                    except Exception:
                        parsed = None
                out.append({
                    "ok": bool(item.get("ok")),
                    "subCmd": str(item.get("cmd") or ""),
                    "body": body,
                    "json": parsed,
                    "error": "" if item.get("ok") else body[:240],
                })
            return out
        # Fallback if pipeline itself failed or wasn't supported
        return [{
            "ok": False,
            "subCmd": str(st.get("cmd") or ""),
            "body": res.get("body", ""),
            "json": None,
            "error": res.get("error") or res.get("body", "")[:240],
        } for st in steps[:30]]

    def fetch_logcat(self, lines: int = 120, min_level: str = "W", buffer: str = "main") -> str:
        """Try native `logcat_tail` first; fall back seamlessly to sandboxed `shell_exec logcat`."""
        r = self.cmd("logcat_tail", {"lines": lines, "minLevel": min_level, "buffer": buffer}, tier=0, timeout_sec=15)
        if r["ok"] and r["body"] and "Unknown command" not in r["body"]:
            if isinstance(r["json"], dict):
                return str(r["json"].get("output") or "")
            return r["body"]

        # Seamless fallback for APKs prior to native `logcat_tail`
        buf_flag = f"-b {buffer} " if buffer != "main" else ""
        lvl_spec = f"*:{min_level}" if min_level else "*:V"
        sh = self.cmd(
            "shell_exec",
            {"command": f"logcat -d {buf_flag}-t {int(lines)} -v brief {lvl_spec}", "timeoutSec": 10},
            tier=2,
            timeout_sec=18,
        )
        if sh["ok"] and isinstance(sh["json"], dict):
            return str(sh["json"].get("stdout") or "")
        return ""


class LiveBugHunter:
    """Runs adversarial attack vectors while continuously watching AppLogger, Logcat & AppState."""

    def __init__(
        self,
        bridge: LiveCavernBridge,
        *,
        mode: str = "standard",
        allow_mutate: bool = True,
        verbose: bool = True,
        out_md: str = "",
        out_json: str = "",
    ) -> None:
        self.bridge = bridge
        self.mode = mode
        self.allow_mutate = allow_mutate
        self.verbose = verbose
        self.out_md = out_md
        self.out_json = out_json
        self.ledger = FindingLedger()
        self.vector_summaries: list[dict[str, Any]] = []
        self.meta: dict[str, Any] = {
            "mode": mode,
            "allowMutate": allow_mutate,
            "startedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        }

        self._seen_applog_lines: set[str] = set()
        self._seen_logcat_lines: set[str] = set()
        self._baseline_state: dict[str, Any] = {}
        self._baseline_settings: dict[str, Any] = {}
        self._baseline_memory: dict[str, Any] = {}
        self._baseline_counts: dict[str, int] = {}
        self._created_cache_files: list[str] = []
        self._has_chaos_hunt_cmd = False
        self._has_native_logcat = False

    def _emit(self, msg: str) -> None:
        if self.verbose:
            sys.stderr.write(f"[bug-hunter] {msg}\n")
            sys.stderr.flush()

    def _ingest_applog_delta(self, log_body: str, vector: str, repro_step: str) -> int:
        new_count = 0
        if not log_body or "(No WARN/ERROR/FATAL" in log_body:
            return 0
        fresh = []
        for raw in log_body.splitlines():
            s = raw.strip()
            if s and s not in self._seen_applog_lines:
                self._seen_applog_lines.add(s)
                fresh.append(s)
        for entry in parse_app_logger_lines("\n".join(fresh)):
            if entry["level"] in ("WARN", "ERROR", "FATAL"):
                sev, cat = classify_applog_severity(entry)
                is_new, _ = self.ledger.record(
                    severity=sev,
                    vector=vector,
                    source="applogger_live",
                    category=cat,
                    title=entry["msg"][:200],
                    repro_step=repro_step,
                    evidence=entry["raw"],
                )
                if is_new:
                    new_count += 1
        return new_count

    def _ingest_logcat_delta(self, logcat_text: str, vector: str, repro_step: str) -> int:
        new_count = 0
        fresh_lc = []
        for raw in (logcat_text or "").splitlines():
            s = raw.strip()
            if s and s not in self._seen_logcat_lines:
                self._seen_logcat_lines.add(s)
                fresh_lc.append(s)
        if fresh_lc:
            for an in parse_logcat_anomalies("\n".join(fresh_lc)):
                is_new, _ = self.ledger.record(
                    severity=an["severity"],
                    vector=vector,
                    source="logcat_live",
                    category=an["category"],
                    title=an["message"][:200],
                    repro_step=repro_step,
                    evidence=an["raw"],
                )
                if is_new:
                    new_count += 1
        return new_count

    def _prime_baselines(self) -> bool:
        # 1 batched pipeline round-trip for complete device bootstrap!
        boot = self.bridge.pipeline([
            {"cmd": "ping"},
            {"cmd": "capabilities"},
            {"cmd": "app_state"},
            {"cmd": "settings_get"},
            {"cmd": "memory"},
            {"cmd": "log_tail", "args": {"lines": 250, "errorsOnly": True}},
        ], stop_on_error=False, tier=2, timeout_sec=30)

        ping = boot[0] if len(boot) > 0 else {"ok": False, "json": None, "error": "empty", "body": ""}
        if not ping["ok"] or not isinstance(ping["json"], dict):
            self.ledger.record(
                severity="CRITICAL",
                vector="bootstrap",
                source="bridge",
                category="app_unreachable",
                title="Cavernize IPC bridge did not respond to ping",
                repro_step="cavernize-cmd --name ping",
                evidence=ping["error"] or ping["body"][:250],
            )
            return False

        p = ping["json"]
        self.meta["appVersion"] = p.get("appVersion", "?")
        self.meta["appBuild"] = p.get("appBuild", 0)
        self.meta["device"] = p.get("device", "?")
        self.meta["android"] = p.get("android", "?")
        self.meta["sessionId"] = p.get("sessionId", "")
        self.meta["uiHostAttached"] = bool(p.get("uiHostAttached"))
        self._baseline_counts = dict(p.get("counts") or {})

        if len(boot) > 1 and boot[1]["ok"] and isinstance(boot[1]["json"], dict):
            cmd_names = {c.get("cmd") for c in (boot[1]["json"].get("commands") or []) if isinstance(c, dict)}
            self._has_chaos_hunt_cmd = "chaos_hunt" in cmd_names
            self._has_native_logcat = "logcat_tail" in cmd_names
        if len(boot) > 2 and boot[2]["ok"] and isinstance(boot[2]["json"], dict):
            self._baseline_state = boot[2]["json"]
        if len(boot) > 3 and boot[3]["ok"] and isinstance(boot[3]["json"], dict):
            self._baseline_settings = boot[3]["json"]
        if len(boot) > 4 and boot[4]["ok"] and isinstance(boot[4]["json"], dict):
            self._baseline_memory = boot[4]["json"]
        if len(boot) > 5 and boot[5]["ok"]:
            for ln in boot[5]["body"].splitlines():
                if ln.strip():
                    self._seen_applog_lines.add(ln.strip())

        if not self.meta["uiHostAttached"]:
            self._emit("MainActivity UI host not attached; bringing Cavernize to front...")
            self.bridge.cmd("ui_action", {"action": "bring_to_front"}, tier=1, timeout_sec=15)
            time.sleep(1.2)
            p2 = self.bridge.cmd("ping", tier=0, timeout_sec=15)
            if p2["ok"] and isinstance(p2["json"], dict):
                self.meta["uiHostAttached"] = bool(p2["json"].get("uiHostAttached"))

        lc = self.bridge.fetch_logcat(lines=180, min_level="W")
        for ln in lc.splitlines():
            if ln.strip():
                self._seen_logcat_lines.add(ln.strip())

        self._emit(
            f"Connected: Cavernize v{self.meta['appVersion']} ({self.meta['appBuild']}) on "
            f"{self.meta['device']} | uiHost={self.meta['uiHostAttached']} | "
            f"tab={self._baseline_state.get('activeTab')} | playing={self._baseline_state.get('isPlaying')} | "
            f"tracks={self._baseline_state.get('libraryTrackCount')}"
        )
        return True

    def _watch_step_delta(self, vector: str, repro_step: str, check_logcat: bool = True) -> int:
        """Capture ONLY newly emitted AppLogger + Logcat anomalies since the previous check."""
        new_count = 0
        steps: list[dict[str, Any]] = [{"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}}]
        if check_logcat:
            if self._has_native_logcat:
                steps.append({"cmd": "logcat_tail", "args": {"lines": 100, "minLevel": "W", "buffer": "main"}})
            else:
                steps.append({"cmd": "shell_exec", "args": {"command": "logcat -d -t 100 -v brief *:W", "timeoutSec": 10}})
        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=25)
        if len(res) > 0 and res[0]["ok"]:
            new_count += self._ingest_applog_delta(res[0]["body"], vector, repro_step)
        if check_logcat and len(res) > 1 and res[1]["ok"]:
            lc_text = ""
            if isinstance(res[1]["json"], dict):
                lc_text = str(res[1]["json"].get("output") or res[1]["json"].get("stdout") or "")
            else:
                lc_text = res[1]["body"]
            new_count += self._ingest_logcat_delta(lc_text, vector, repro_step)
        return new_count

    def _flush_checkpoint_report(self, restored: bool | None = None) -> None:
        if not self.out_json and not self.out_md:
            return
        snap = {
            "ok": True,
            "meta": dict(self.meta, stepsExecuted=self.bridge.calls, restoredState=restored),
            "severityCounts": self.ledger.counts_by_severity(),
            "totalUniqueFindings": len(self.ledger.ranked()),
            "vectors": self.vector_summaries,
            "findings": self.ledger.to_dicts(),
        }
        try:
            if self.out_json:
                Path(self.out_json).write_text(json.dumps(snap, indent=2, ensure_ascii=False), encoding="utf-8")
            if self.out_md:
                Path(self.out_md).write_text(generate_report_markdown(snap), encoding="utf-8")
        except Exception:
            pass

    def _run_vector(self, name: str, fn: Callable[[], str]) -> None:
        self._emit(f"--- Vector: {name} ---")
        t0 = time.monotonic()
        before_total = len(self.ledger.ranked())
        status = "OK"
        notes = ""
        try:
            notes = fn() or ""
        except Exception as exc:
            status = "ERROR"
            notes = f"{type(exc).__name__}: {exc}"
            self.ledger.record(
                severity="HIGH",
                vector=name,
                source="harness",
                category="vector_exception",
                title=f"Vector '{name}' raised {type(exc).__name__}: {exc}",
                repro_step=f"vector:{name}",
                evidence=notes,
            )
        elapsed = time.monotonic() - t0
        new_found = len(self.ledger.ranked()) - before_total
        if new_found > 0 and status == "OK":
            status = "FINDINGS"
        self.vector_summaries.append({
            "name": name,
            "status": status,
            "newFindings": new_found,
            "elapsedSec": round(elapsed, 2),
            "notes": notes,
        })
        self._emit(f"<<< {name}: {status} (+{new_found} unique findings, {elapsed:.1f}s) {notes}")
        self._flush_checkpoint_report()

    # ==================== VECTOR 1: HISTORICAL & LIVE LOG FORENSICS ====================
    def _v1_log_forensics(self) -> str:
        mined_lines = 0
        fl = self.bridge.cmd("files_list", {"scope": "logs"}, tier=0, timeout_sec=15)
        log_path = ""
        if fl["ok"] and isinstance(fl["json"], dict):
            for ent in fl["json"].get("entries") or []:
                if str(ent.get("name") or "").endswith(".txt"):
                    log_path = str(ent.get("path") or "")
                    break

        # Batch file_read + logcat W/crash/I in 1 pipeline!
        steps: list[dict[str, Any]] = []
        if log_path:
            steps.append({"cmd": "file_read", "args": {"path": log_path, "maxChars": 50000, "tail": True}})
        if self._has_native_logcat:
            steps.append({"cmd": "logcat_tail", "args": {"lines": 250, "minLevel": "W", "buffer": "main"}})
            steps.append({"cmd": "logcat_tail", "args": {"lines": 80, "minLevel": "V", "buffer": "crash"}})
            steps.append({"cmd": "logcat_tail", "args": {"lines": 220, "minLevel": "I", "buffer": "main"}})
        else:
            steps.append({"cmd": "shell_exec", "args": {"command": "logcat -d -t 250 -v brief *:W", "timeoutSec": 10}})
            steps.append({"cmd": "shell_exec", "args": {"command": "logcat -d -b crash -t 80 -v brief", "timeoutSec": 10}})
            steps.append({"cmd": "shell_exec", "args": {"command": "logcat -d -t 220 -v brief *:I", "timeoutSec": 10}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=35)
        idx = 0
        if log_path and idx < len(res):
            fr = res[idx]
            idx += 1
            if fr["ok"] and fr["body"]:
                entries = parse_app_logger_lines(fr["body"])
                mined_lines = len(entries)
                for e in entries:
                    if e["level"] in ("WARN", "ERROR", "FATAL"):
                        sev, cat = classify_applog_severity(e)
                        self.ledger.record(
                            severity=sev,
                            vector="V1_log_forensics",
                            source="applogger_history",
                            category=cat,
                            title=e["msg"][:200],
                            repro_step=f"historical_log ({e['ts'] or 'recent'})",
                            evidence=e["raw"],
                        )

        def _extract_lc(r_item: dict[str, Any]) -> str:
            if not r_item.get("ok"):
                return ""
            if isinstance(r_item.get("json"), dict):
                return str(r_item["json"].get("output") or r_item["json"].get("stdout") or "")
            return str(r_item.get("body") or "")

        if idx < len(res):
            for an in parse_logcat_anomalies(_extract_lc(res[idx])):
                self.ledger.record(
                    severity=an["severity"],
                    vector="V1_log_forensics",
                    source="logcat_history",
                    category=an["category"],
                    title=an["message"][:200],
                    repro_step="logcat -d *:W",
                    evidence=an["raw"],
                )
            idx += 1
        if idx < len(res):
            for an in parse_logcat_anomalies(_extract_lc(res[idx])):
                self.ledger.record(
                    severity="CRITICAL",
                    vector="V1_log_forensics",
                    source="logcat_crash",
                    category="native_or_java_crash",
                    title=an["message"][:200],
                    repro_step="logcat -d -b crash",
                    evidence=an["raw"],
                )
            idx += 1
        if idx < len(res):
            for an in parse_logcat_anomalies(_extract_lc(res[idx])):
                if an["category"] in ("ui_jank", "audio_native", "codec", "strict_mode", "gc_stall"):
                    self.ledger.record(
                        severity=an["severity"],
                        vector="V1_log_forensics",
                        source="logcat_runtime",
                        category=an["category"],
                        title=an["message"][:200],
                        repro_step="logcat -d *:I (Choreographer/Audio/GC)",
                        evidence=an["raw"],
                    )

        return f"mined {mined_lines} AppLogger entries + logcat W/E/crash/Choreographer"

    # ==================== VECTOR 2: CONCURRENT PLAYBACK x ACOUSTIC ORACLE ====================
    def _v2_concurrent_playback_oracle(self) -> str:
        """Hunt for static/global DSP races by running acoustic_audit WHILE real playback is active."""
        was_playing = bool(self._baseline_state.get("isPlaying"))
        has_file = bool(self._baseline_state.get("loadedFile"))

        steps: list[dict[str, Any]] = []
        if self.allow_mutate and has_file and not was_playing:
            steps.append({"cmd": "playback_control", "args": {"action": "play"}})
        steps.append({"cmd": "acoustic_audit", "args": {"quick": self.mode != "deep", "format": "json"}})
        steps.append({"cmd": "dsp_telemetry"})
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=50)
        aud_idx = 1 if (self.allow_mutate and has_file and not was_playing) else 0
        aud = res[aud_idx] if aud_idx < len(res) else {"ok": False, "json": None, "body": "", "error": "missing"}
        tel = res[aud_idx + 1] if (aud_idx + 1) < len(res) else {"ok": False, "json": None, "body": ""}
        lt = res[aud_idx + 2] if (aud_idx + 2) < len(res) else {"ok": False, "body": ""}

        scenarios_checked = 0
        fails = 0
        warns = 0
        if aud["ok"] or isinstance(aud["json"], dict):
            data = aud["json"] if isinstance(aud["json"], dict) else {}
            scenarios = data.get("Scenarios") or data.get("scenarios") or []
            scenarios_checked = len(scenarios)
            for sc in scenarios:
                st = str(sc.get("Status") or sc.get("status") or "").upper()
                sid = str(sc.get("Id") or sc.get("id") or "?")
                note = str(sc.get("VerdictNote") or sc.get("verdictNote") or "")
                if st == "FAIL":
                    fails += 1
                    self.ledger.record(
                        severity="HIGH",
                        vector="V2_concurrent_playback_oracle",
                        source="acoustic_oracle",
                        category=f"acoustic_fail_{sid.lower()}",
                        title=f"Acoustic scenario {sid} FAILED during live playback: {note}",
                        repro_step="acoustic_audit while playback active",
                        evidence=json.dumps(sc)[:380],
                    )
                elif st == "WARN":
                    warns += 1
                    self.ledger.record(
                        severity="MEDIUM",
                        vector="V2_concurrent_playback_oracle",
                        source="acoustic_oracle",
                        category=f"acoustic_warn_{sid.lower()}",
                        title=f"Acoustic scenario {sid} WARN: {note}",
                        repro_step="acoustic_audit while playback active",
                        evidence=json.dumps(sc)[:380],
                    )
        else:
            self.ledger.record(
                severity="HIGH",
                vector="V2_concurrent_playback_oracle",
                source="acoustic_oracle",
                category="acoustic_audit_error",
                title="acoustic_audit failed to execute",
                repro_step="cavernize-cmd --name acoustic_audit",
                evidence=(aud["error"] or aud["body"])[:300],
            )

        if tel["ok"] and isinstance(tel["json"], dict):
            raw_tel = json.dumps(tel["json"])
            if re.search(r"\b(NaN|Infinity)\b", raw_tel):
                self.ledger.record(
                    severity="CRITICAL",
                    vector="V2_concurrent_playback_oracle",
                    source="dsp_telemetry",
                    category="dsp_nan_telemetry",
                    title="Live dsp_telemetry contains NaN/Infinity",
                    repro_step="cavernize-cmd --name dsp_telemetry",
                    evidence=raw_tel[:320],
                )
            if not tel["json"].get("proAudioBridgeAvailable"):
                self.ledger.record(
                    severity="MEDIUM",
                    vector="V2_concurrent_playback_oracle",
                    source="dsp_telemetry",
                    category="proaudio_unavailable",
                    title="ProAudio native NDK bridge unavailable (running managed AudioTrack fallback)",
                    repro_step="cavernize-cmd --name dsp_telemetry",
                    evidence=raw_tel[:250],
                )

        if lt["ok"]:
            self._ingest_applog_delta(lt["body"], "V2_concurrent_playback_oracle", "concurrent playback + acoustic_audit")
        return f"checked {scenarios_checked} acoustic scenarios concurrently (fails={fails}, warns={warns})"

    # ==================== VECTOR 3: LIVE SETTINGS CHAOS & BOUNDARY FUZZER ====================
    def _v3_settings_chaos_fuzzer(self) -> str:
        if not self.allow_mutate:
            return "skipped (--no-mutate)"

        keys_present = set(self._baseline_settings.keys())
        bool_candidates = [
            "Iso226Enabled", "LgsrEnabled", "HeadTrackingEnabled", "AutoEqEnabled",
            "HptfNotchEnabled", "MinimumPhaseHrir", "NearFieldEnabled", "IsmRoomEnabled",
            "DistanceSimulation", "DialogEnhancerEnabled", "HeadphoneVirtualizer",
            "ParametricStftEnabled", "ProAudioExclusiveMode",
        ]
        active_bools = [k for k in bool_candidates if k in keys_present]

        steps: list[dict[str, Any]] = []
        labels: list[str] = []
        # 3a. Rapid-fire toggle burst (millisecond spacing inside live app!)
        for key in active_bools[:8 if self.mode != "deep" else 12]:
            orig = bool(self._baseline_settings.get(key))
            steps.append({"cmd": "settings_set", "args": {"key": key, "value": not orig}})
            labels.append(f"toggle {key}={not orig}")

        # 3b. Enable full heavy DSP stack simultaneously!
        combo = {k: True for k in active_bools if k in (
            "Iso226Enabled", "LgsrEnabled", "AutoEqEnabled", "HptfNotchEnabled",
            "MinimumPhaseHrir", "NearFieldEnabled", "IsmRoomEnabled", "DialogEnhancerEnabled",
        )}
        if combo:
            steps.append({"cmd": "settings_set", "args": {"values": combo}})
            labels.append("enable full DSP combo")
            steps.append({"cmd": "dsp_telemetry"})
            labels.append("dsp_telemetry after full combo")

        # 3c. Boundary fuzzing on numeric/string settings
        boundary_probes: list[tuple[str, Any]] = [
            ("LoudnessNormModeIndex", 99),
            ("LoudnessNormModeIndex", -5),
            ("MagLsOrder", 9),
            ("MagLsOrder", -1),
            ("BiometricHeadWidthCm", 0.0),
            ("BiometricHeadWidthCm", 999.0),
            ("NearFieldDistanceM", -3.0),
            ("BinauralProfileId", "nonexistent_fuzzed_profile_xyz"),
        ]
        for bkey, bval in boundary_probes:
            if bkey in keys_present and len(steps) < 25:
                steps.append({"cmd": "settings_set", "args": {"key": bkey, "value": bval}})
                labels.append(f"boundary {bkey}={bval}")

        steps.append({"cmd": "settings_get"})
        labels.append("settings_get post-fuzz")
        steps.append({"cmd": "app_state"})
        labels.append("app_state post-fuzz")
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})
        labels.append("log_tail post-fuzz")

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=45)
        for i, item in enumerate(res):
            lbl = labels[i] if i < len(labels) else f"step#{i}"
            if item["subCmd"] == "settings_set" and not item["ok"]:
                self.ledger.record(
                    severity="MEDIUM",
                    vector="V3_settings_chaos_fuzzer",
                    source="settings_set",
                    category="setting_mutation_rejected",
                    title=f"settings_set failed during rapid chaos ({lbl})",
                    repro_step=lbl,
                    evidence=(item["error"] or item["body"])[:280],
                )
            elif item["subCmd"] in ("settings_get", "dsp_telemetry") and item["ok"]:
                if re.search(r"\b(NaN|Infinity)\b", item["body"]):
                    self.ledger.record(
                        severity="HIGH",
                        vector="V3_settings_chaos_fuzzer",
                        source="settings_fuzz",
                        category="unclamped_setting_nan",
                        title=f"Boundary fuzzing produced NaN/Infinity in {item['subCmd']}",
                        repro_step=lbl,
                        evidence=item["body"][:280],
                    )
            elif item["subCmd"] == "log_tail" and item["ok"]:
                self._ingest_applog_delta(item["body"], "V3_settings_chaos_fuzzer", "rapid DSP settings chaos burst")

        self._restore_settings()
        self._watch_step_delta("V3_settings_chaos_fuzzer", "settings chaos burst + restore", check_logcat=True)
        return f"executed {len(steps)} rapid-fire DSP/boundary steps in 1 burst and restored settings"

    # ==================== VECTOR 4: REAL USER LIBRARY EDGE-CASE & DECODER TORTURE ====================
    def _v4_real_library_edge_hunter(self) -> str:
        lq = self.bridge.cmd("library_query", {"query": "", "limit": 80}, tier=0, timeout_sec=20)
        tracks: list[dict[str, Any]] = []
        if lq["ok"] and isinstance(lq["json"], dict):
            tracks = list(lq["json"].get("tracks") or [])
        if not tracks:
            return "library empty or unavailable"

        zero_dur = [t for t in tracks if int(t.get("DurationMs") or 0) <= 0]
        if zero_dur:
            sample_names = ", ".join(str(t.get("Title") or Path(str(t.get("FilePath") or "")).name) for t in zero_dur[:4])
            self.ledger.record(
                severity="MEDIUM",
                vector="V4_real_library_edge_hunter",
                source="library_metadata",
                category="zero_duration_library_tracks",
                title=f"{len(zero_dur)}/{len(tracks)} sampled library tracks have DurationMs=0",
                repro_step="library_query limit=80",
                evidence=f"Examples: {sample_names}",
            )

        picks = pick_edge_case_library_tracks(tracks, max_picks=4 if self.mode != "deep" else 6)
        # 4a. Batch audio_probe across all selected edge-case tracks in 1 pipeline
        probe_steps = [{"cmd": "audio_probe", "args": {"path": str(t.get("FilePath") or "")}} for _, t in picks if t.get("FilePath")]
        probe_res = self.bridge.pipeline(probe_steps, stop_on_error=False, tier=2, timeout_sec=35)
        for (reason, trk), pr in zip(picks, probe_res):
            fpath = str(trk.get("FilePath") or "")
            if not pr["ok"]:
                self.ledger.record(
                    severity="HIGH",
                    vector="V4_real_library_edge_hunter",
                    source="audio_probe",
                    category="real_track_probe_failed",
                    title=f"audio_probe failed on real user track ({reason}): {Path(fpath).name}",
                    repro_step=f"audio_probe path='{fpath}'",
                    evidence=(pr["error"] or pr["body"])[:300],
                )
            elif isinstance(pr["json"], dict):
                pj = pr["json"]
                if int(pj.get("sampleRateHz") or 0) <= 0 or int(pj.get("channels") or 0) <= 0:
                    self.ledger.record(
                        severity="MEDIUM",
                        vector="V4_real_library_edge_hunter",
                        source="audio_probe",
                        category="audio_probe_zero_format",
                        title=f"audio_probe returned 0 Hz / 0 ch on real track ({reason}): {Path(fpath).name}",
                        repro_step=f"audio_probe path='{fpath}'",
                        evidence=pr["body"][:300],
                    )

        # 4b. Open & extreme-seek-torture 2 edge-case tracks in 1 pipeline each!
        opened = 0
        if self.allow_mutate:
            for reason, trk in picks[:2 if self.mode != "deep" else 3]:
                fpath = str(trk.get("FilePath") or "")
                if not fpath:
                    continue
                opened += 1
                torture_steps = [
                    {"cmd": "open_file", "args": {"path": fpath, "autoPlay": False}},
                    {"cmd": "playback_control", "args": {"action": "seek", "value": -5.0}},
                    {"cmd": "playback_control", "args": {"action": "seek", "value": 0.0}},
                    {"cmd": "playback_control", "args": {"action": "seek", "value": 99999.0}},
                    {"cmd": "playback_control", "args": {"action": "seek_rel", "value": -9999.0}},
                    {"cmd": "playback_control", "args": {"action": "seek_rel", "value": 9999.0}},
                    {"cmd": "app_state"},
                    {"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}},
                ]
                tres = self.bridge.pipeline(torture_steps, stop_on_error=False, tier=2, timeout_sec=35)
                if tres and not tres[0]["ok"]:
                    self.ledger.record(
                        severity="HIGH",
                        vector="V4_real_library_edge_hunter",
                        source="open_file",
                        category="real_track_open_failed",
                        title=f"open_file failed on real track ({reason}): {Path(fpath).name}",
                        repro_step=f"open_file path='{fpath}'",
                        evidence=(tres[0]["error"] or tres[0]["body"])[:300],
                    )
                for sk_idx in range(1, min(6, len(tres))):
                    if not tres[sk_idx]["ok"]:
                        self.ledger.record(
                            severity="MEDIUM",
                            vector="V4_real_library_edge_hunter",
                            source="playback_control",
                            category="extreme_seek_error",
                            title=f"Extreme seek failed on {Path(fpath).name}: {tres[sk_idx]['body'][:120]}",
                            repro_step=f"seek torture on '{Path(fpath).name}'",
                            evidence=tres[sk_idx]["body"][:250],
                        )
                if len(tres) > 7 and tres[7]["ok"]:
                    self._ingest_applog_delta(tres[7]["body"], "V4_real_library_edge_hunter", f"open+seek torture ({reason}) {Path(fpath).name}")

        self._watch_step_delta("V4_real_library_edge_hunter", "real library edge torture", check_logcat=True)
        return f"probed {len(probe_steps)} edge-case tracks, opened & seek-tortured {opened}"

    # ==================== VECTOR 5: CORRUPT / MALFORMED SYNTHETIC INPUT FUZZER ====================
    def _v5_corrupt_file_fuzzer(self) -> str:
        if not self.allow_mutate:
            return "skipped (--no-mutate)"

        setup_cmd = (
            "mkdir -p ../cache/bh_fuzz && "
            ": > ../cache/bh_fuzz/empty.flac && "
            "printf 'RIFF\\x24\\x00\\x00\\x00WAVEfmt ' > ../cache/bh_fuzz/trunc.wav && "
            "printf 'fLaC\\x00\\x00\\x00\\x22GARBAGE_CORRUPT_DATA_1234567890' > ../cache/bh_fuzz/corrupt.flac && "
            "printf 'ID3\\x03\\x00\\x00\\x00\\x00\\x00\\x00NOT_AN_MP3' > ../cache/bh_fuzz/corrupt.mp3 && "
            "ls -1 ../cache/bh_fuzz"
        )
        sh = self.bridge.cmd("shell_exec", {"command": setup_cmd, "timeoutSec": 10}, tier=2, timeout_sec=20)
        if not sh["ok"] or not isinstance(sh["json"], dict) or sh["json"].get("exitCode") != 0:
            return "could not stage temp corrupt files in CacheDir"

        cwd = str(sh["json"].get("cwd") or "/data/user/0/hu.sbence.cavern.cavernize/files")
        cache_dir = str(Path(cwd).parent / "cache" / "bh_fuzz")
        files = [
            f"{cache_dir}/empty.flac",
            f"{cache_dir}/trunc.wav",
            f"{cache_dir}/corrupt.flac",
            f"{cache_dir}/corrupt.mp3",
        ]
        self._created_cache_files.extend(files)

        steps: list[dict[str, Any]] = []
        for fpath in files:
            steps.append({"cmd": "audio_probe", "args": {"path": fpath}})
            steps.append({"cmd": "open_file", "args": {"path": fpath, "autoPlay": False}})
            steps.append({"cmd": "ui_interact", "args": {"action": "dismiss_dialogs"}})
        steps.append({"cmd": "app_state"})
        steps.append({"cmd": "shell_exec", "args": {"command": "rm -rf ../cache/bh_fuzz", "timeoutSec": 10}})
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=45)
        self._created_cache_files.clear()

        if len(res) >= 3:
            st = res[-3]
            if st["ok"] and isinstance(st["json"], dict) and st["json"].get("isOperationRunning"):
                self.ledger.record(
                    severity="HIGH",
                    vector="V5_corrupt_file_fuzzer",
                    source="app_state",
                    category="stuck_busy_flag_on_corrupt_file",
                    title="isOperationRunning remained stuck true after malformed media fuzz burst",
                    repro_step="open_file on empty.flac / trunc.wav / corrupt.flac / corrupt.mp3",
                    evidence=st["body"][:280],
                )
            lt = res[-1]
            if lt["ok"]:
                self._ingest_applog_delta(lt["body"], "V5_corrupt_file_fuzzer", "corrupt media file fuzz burst")

        self._watch_step_delta("V5_corrupt_file_fuzzer", "corrupt media fuzz burst", check_logcat=True)
        return f"fuzzed {len(files)} malformed/truncated media files in 1 burst + cleaned up"

    # ==================== VECTOR 6: UI MONKEY, RAPID TAB THRASH & LAYOUT AUDIT ====================
    def _v6_ui_monkey_and_audit(self) -> str:
        if not self.meta.get("uiHostAttached"):
            return "skipped (MainActivity UI host detached)"

        # 6a. Run built-in L8-L10+L12 Live UI & Perf-Jank Audit
        ui_aud = self.bridge.cmd("ui_audit", tier=1, timeout_sec=60)
        if isinstance(ui_aud["json"], dict):
            for chk in ui_aud["json"].get("checks") or []:
                st = str(chk.get("Status") or chk.get("status") or "").upper()
                cid = str(chk.get("Id") or chk.get("id") or "ui")
                det = str(chk.get("Details") or chk.get("details") or "")
                if st in ("2", "FAIL"):
                    self.ledger.record(
                        severity="HIGH",
                        vector="V6_ui_monkey_and_audit",
                        source="ui_audit",
                        category=f"ui_audit_fail_{cid}",
                        title=f"Live UI audit FAIL [{cid}]: {det[:180]}",
                        repro_step="cavernize-cmd --name ui_audit",
                        evidence=det[:360],
                    )
                elif st in ("1", "WARN"):
                    self.ledger.record(
                        severity="MEDIUM",
                        vector="V6_ui_monkey_and_audit",
                        source="ui_audit",
                        category=f"ui_audit_warn_{cid}",
                        title=f"Live UI audit WARN [{cid}]: {det[:180]}",
                        repro_step="cavernize-cmd --name ui_audit",
                        evidence=det[:360],
                    )

        # 6b. Rapid Tab Thrash + ui_tree Anomaly Inspection + Search Fuzz in 1 pipeline!
        steps: list[dict[str, Any]] = []
        tab_tree_indices: list[tuple[int, int]] = []
        for tab_idx in (0, 4, 1, 3, 2):
            steps.append({"cmd": "tab_switch", "args": {"index": tab_idx}})
            tab_tree_indices.append((len(steps), tab_idx))
            steps.append({"cmd": "ui_tree", "args": {"maxDepth": 6}})
        if self.allow_mutate:
            steps.append({"cmd": "tab_switch", "args": {"index": 4}})
            for q in ("binaural", ".*+?[]()", "🎧🔥", "zzzz_no_match_999", "a" * 160, ""):
                steps.append({"cmd": "ui_interact", "args": {"action": "set_text", "type": "EditText", "value": q}})
            steps.append({"cmd": "ui_interact", "args": {"action": "scroll_by", "dy": 1200}})
            steps.append({"cmd": "ui_interact", "args": {"action": "scroll_by", "dy": -1200}})
            steps.append({"cmd": "ui_interact", "args": {"action": "dismiss_dialogs"}})
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=45)
        for step_pos, tab_idx in tab_tree_indices:
            if step_pos < len(res) and res[step_pos]["ok"] and isinstance(res[step_pos]["json"], dict):
                for d in analyze_ui_tree_anomalies(res[step_pos]["json"], f"Tab{tab_idx}"):
                    self.ledger.record(
                        severity=d["severity"],
                        vector="V6_ui_monkey_and_audit",
                        source="ui_tree",
                        category=d["category"],
                        title=d["message"],
                        repro_step=f"tab_switch({tab_idx}) -> ui_tree",
                        evidence=d["message"],
                    )
        if res and res[-1]["subCmd"] == "log_tail" and res[-1]["ok"]:
            self._ingest_applog_delta(res[-1]["body"], "V6_ui_monkey_and_audit", "tab thrash + search fuzz")

        self._watch_step_delta("V6_ui_monkey_and_audit", "ui_audit + tab thrash + search fuzz", check_logcat=True)
        return f"ran ui_audit + 5-tab ui_tree scan + search fuzz burst"

    # ==================== VECTOR 7: ACOUSTIC EXTREME PARAMETER FUZZER ====================
    def _v7_acoustic_extreme_fuzzer(self) -> str:
        cases: list[tuple[str, dict[str, Any]]] = [
            ("sr_8000_minphase", {"sampleRate": 8000, "minPhase": True, "signal": "white"}),
            ("sr_44100_magls3_bioitd", {"sampleRate": 44100, "magLsOrder": 3, "minPhase": True, "bioItd": True, "headWidthCm": 18.0, "sourceAzDeg": -90}),
            ("sr_96000_nearfield_015m", {"sampleRate": 96000, "nearField": True, "nearFieldDistM": 0.15, "sourceAzDeg": 90, "signal": "pink"}),
            ("sr_192000_ism_upmix_lgsr", {"sampleRate": 192000, "ismRoom": True, "ismPreset": 4, "ismLevel": 1.5, "upmix": True, "lgsr": True, "signal": "sine1k"}),
            ("extreme_head_pose_180", {"yawDeg": 180.0, "pitchDeg": 85.0, "sourceAzDeg": -90.0, "sourceElDeg": 45.0, "signal": "impulse"}),
            ("extreme_itd_bounds", {"bioItd": True, "headWidthCm": 10.0, "manualItdUs": -500.0, "sourceAzDeg": 90.0, "signal": "white"}),
        ]
        if self.mode == "deep":
            cases.extend([
                ("sr_22050_upmix_lgsr", {"sampleRate": 22050, "upmix": True, "lgsr": True, "magLsOrder": 2, "signal": "pink"}),
                ("negative_yaw_pitch", {"yawDeg": -135.0, "pitchDeg": -85.0, "ismRoom": True, "nearField": True, "nearFieldDistM": 0.45}),
            ])

        steps = [{"cmd": "acoustic_probe", "args": params} for _, params in cases]
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})
        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=55)

        for (label, params), r in zip(cases, res):
            if not r["ok"]:
                self.ledger.record(
                    severity="HIGH",
                    vector="V7_acoustic_extreme_fuzzer",
                    source="acoustic_probe",
                    category="acoustic_probe_crash",
                    title=f"acoustic_probe '{label}' failed: {(r['error'] or r['body'])[:150]}",
                    repro_step=f"acoustic_probe {json.dumps(params)}",
                    evidence=(r["error"] or r["body"])[:320],
                )
            elif isinstance(r["json"], dict):
                for iss in analyze_acoustic_probe_result(r["json"], label):
                    self.ledger.record(
                        severity=iss["severity"],
                        vector="V7_acoustic_extreme_fuzzer",
                        source="acoustic_probe",
                        category=iss["category"],
                        title=iss["message"],
                        repro_step=f"acoustic_probe {json.dumps(params)}",
                        evidence=r["body"][:350],
                    )
        if len(res) > len(cases) and res[-1]["ok"]:
            self._ingest_applog_delta(res[-1]["body"], "V7_acoustic_extreme_fuzzer", f"fuzzed {len(cases)} extreme acoustic probes")
        return f"fuzzed {len(cases)} extreme psychoacoustic/sample-rate/head-pose probes in 1 burst"

    # ==================== VECTOR 8: IN-APP HIGH-SPEED CHAOS + BRIDGE SECURITY & MEMORY LEAK ====================
    def _v8_inapp_chaos_and_watchdog(self) -> str:
        notes_parts: list[str] = []
        steps: list[dict[str, Any]] = []
        if self._has_chaos_hunt_cmd:
            steps.append({"cmd": "chaos_hunt", "args": {"rounds": 10 if self.mode == "deep" else 5}})
        steps.append({"cmd": "file_read", "args": {"path": "/etc/hosts"}})
        steps.append({"cmd": "definitely_nonexistent_cmd_xyz"})
        steps.append({"cmd": "memory"})
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=50)
        idx = 0
        if self._has_chaos_hunt_cmd and idx < len(res):
            ch = res[idx]
            idx += 1
            if ch["ok"] and isinstance(ch["json"], dict):
                anomalies = ch["json"].get("anomalies") or []
                notes_parts.append(f"in-app chaos_hunt anomalies={len(anomalies)}")
                for an in anomalies:
                    self.ledger.record(
                        severity=str(an.get("severity") or "HIGH"),
                        vector="V8_inapp_chaos_and_watchdog",
                        source="in_app_chaos_hunt",
                        category=str(an.get("category") or "chaos_anomaly"),
                        title=str(an.get("message") or "")[:200],
                        repro_step=str(an.get("scenario") or "chaos_hunt"),
                        evidence=json.dumps(an)[:350],
                    )
            else:
                notes_parts.append("in-app chaos_hunt failed")

        if idx < len(res):
            if res[idx]["ok"]:
                self.ledger.record(
                    severity="CRITICAL",
                    vector="V8_inapp_chaos_and_watchdog",
                    source="security_fuzzer",
                    category="path_traversal_bypass",
                    title="file_read allowed reading outside confined app roots (/etc/hosts)",
                    repro_step="file_read path='/etc/hosts'",
                    evidence=res[idx]["body"][:200],
                )
            idx += 1
        if idx < len(res):
            if res[idx]["ok"]:
                self.ledger.record(
                    severity="MEDIUM",
                    vector="V8_inapp_chaos_and_watchdog",
                    source="bridge_fuzzer",
                    category="unknown_cmd_returned_ok",
                    title="Unknown bridge command unexpectedly returned ok=true",
                    repro_step="cavernize-cmd --name definitely_nonexistent_cmd_xyz",
                    evidence=res[idx]["body"][:200],
                )
            idx += 1
        if idx < len(res):
            mem_now = res[idx]
            idx += 1
            if mem_now["ok"] and isinstance(mem_now["json"], dict) and self._baseline_memory:
                gc_before = int(self._baseline_memory.get("managedGcMb") or 0)
                gc_after = int(mem_now["json"].get("managedGcMb") or 0)
                delta_mb = gc_after - gc_before
                notes_parts.append(f"GC {gc_before}MB->{gc_after}MB (Δ{delta_mb:+d}MB)")
                if mem_now["json"].get("lowMemory"):
                    self.ledger.record(
                        severity="HIGH",
                        vector="V8_inapp_chaos_and_watchdog",
                        source="memory_watchdog",
                        category="android_low_memory",
                        title="Android reported lowMemory=true after stress hunt",
                        repro_step="memory check post-hunt",
                        evidence=mem_now["body"][:250],
                    )
                elif delta_mb >= 45:
                    self.ledger.record(
                        severity="MEDIUM",
                        vector="V8_inapp_chaos_and_watchdog",
                        source="memory_watchdog",
                        category="managed_heap_growth",
                        title=f"Managed GC heap grew by +{delta_mb} MB during hunt ({gc_before}->{gc_after} MB)",
                        repro_step="memory baseline vs post-hunt",
                        evidence=mem_now["body"][:250],
                    )
        if idx < len(res) and res[idx]["ok"]:
            self._ingest_applog_delta(res[idx]["body"], "V8_inapp_chaos_and_watchdog", "security + memory watchdog")

        return ", ".join(notes_parts) or "bridge security & memory OK"

    # ==================== GUARANTEED CLEAN STATE RESTORE ====================
    def _restore_settings(self) -> bool:
        if not self._baseline_settings:
            return True
        restore_keys = [
            "Iso226Enabled", "LgsrEnabled", "HeadTrackingEnabled", "AutoEqEnabled",
            "HptfNotchEnabled", "MinimumPhaseHrir", "NearFieldEnabled", "IsmRoomEnabled",
            "DistanceSimulation", "DialogEnhancerEnabled", "HeadphoneVirtualizer",
            "ParametricStftEnabled", "ProAudioExclusiveMode", "LoudnessNormModeIndex",
            "MagLsOrder", "BiometricHeadWidthCm", "NearFieldDistanceM", "BinauralProfileId",
        ]
        subset = {k: self._baseline_settings[k] for k in restore_keys if k in self._baseline_settings}
        if not subset:
            return True
        r = self.bridge.cmd("settings_set", {"values": subset}, tier=1, timeout_sec=20)
        return bool(r["ok"])

    def _restore_all(self) -> bool:
        ok = True
        if not self.allow_mutate:
            return True
        try:
            steps: list[dict[str, Any]] = []
            if self._created_cache_files:
                steps.append({"cmd": "shell_exec", "args": {"command": "rm -rf ../cache/bh_fuzz", "timeoutSec": 10}})
                self._created_cache_files.clear()
            restore_keys = [
                "Iso226Enabled", "LgsrEnabled", "HeadTrackingEnabled", "AutoEqEnabled",
                "HptfNotchEnabled", "MinimumPhaseHrir", "NearFieldEnabled", "IsmRoomEnabled",
                "DistanceSimulation", "DialogEnhancerEnabled", "HeadphoneVirtualizer",
                "ParametricStftEnabled", "ProAudioExclusiveMode", "LoudnessNormModeIndex",
                "MagLsOrder", "BiometricHeadWidthCm", "NearFieldDistanceM", "BinauralProfileId",
            ]
            subset = {k: self._baseline_settings[k] for k in restore_keys if k in self._baseline_settings}
            if subset:
                steps.append({"cmd": "settings_set", "args": {"values": subset}})
            steps.append({"cmd": "ui_interact", "args": {"action": "dismiss_dialogs"}})
            orig_file = str(self._baseline_state.get("loadedFile") or "")
            orig_tab = self._baseline_state.get("activeTab")
            was_playing = bool(self._baseline_state.get("isPlaying"))
            if orig_file:
                steps.append({"cmd": "open_file", "args": {"path": orig_file, "autoPlay": was_playing}})
            if isinstance(orig_tab, int) and 0 <= orig_tab <= 4:
                steps.append({"cmd": "tab_switch", "args": {"index": orig_tab}})
            if steps:
                res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=30)
                ok = any(r.get("ok") for r in res)
        except Exception:
            ok = False
        return ok

    def run_hunt(self, selected_vectors: list[str] | None = None) -> dict[str, Any]:
        t0 = time.monotonic()
        connected = self._prime_baselines()
        restored = True
        if connected:
            all_vectors: list[tuple[str, Callable[[], str]]] = [
                ("V1_log_forensics", self._v1_log_forensics),
                ("V2_concurrent_playback_oracle", self._v2_concurrent_playback_oracle),
                ("V3_settings_chaos_fuzzer", self._v3_settings_chaos_fuzzer),
                ("V4_real_library_edge_hunter", self._v4_real_library_edge_hunter),
                ("V5_corrupt_file_fuzzer", self._v5_corrupt_file_fuzzer),
                ("V6_ui_monkey_and_audit", self._v6_ui_monkey_and_audit),
                ("V7_acoustic_extreme_fuzzer", self._v7_acoustic_extreme_fuzzer),
                ("V8_inapp_chaos_and_watchdog", self._v8_inapp_chaos_and_watchdog),
            ]
            try:
                for vname, vfn in all_vectors:
                    if selected_vectors and not any(sel.lower() in vname.lower() for sel in selected_vectors):
                        continue
                    self._run_vector(vname, vfn)
            finally:
                restored = self._restore_all()

        elapsed = round(time.monotonic() - t0, 2)
        self.meta["elapsedSec"] = elapsed
        self.meta["stepsExecuted"] = self.bridge.calls
        self.meta["restoredState"] = restored
        self.meta["finishedUtc"] = datetime.now(timezone.utc).isoformat(timespec="seconds")

        report = {
            "ok": True,
            "meta": self.meta,
            "severityCounts": self.ledger.counts_by_severity(),
            "totalUniqueFindings": len(self.ledger.ranked()),
            "vectors": self.vector_summaries,
            "findings": self.ledger.to_dicts(),
        }
        return report

    def run_watch(self, duration_sec: int = 60, interval_sec: float = 3.0) -> dict[str, Any]:
        """Real-time live monitor: stream EVERY new warning/error/crash/jank while the app is used."""
        t0 = time.monotonic()
        if not self._prime_baselines():
            return {"ok": False, "error": "Cavernize unreachable", "findings": self.ledger.to_dicts()}

        self._emit(f"LIVE WATCH active for {duration_sec}s (polling every {interval_sec:.1f}s) — use Cavernize now!")
        watch_start = time.monotonic()
        end_ts = watch_start + max(5, duration_sec)
        polls = 0
        while True:
            polls += 1
            before = len(self.ledger.ranked())
            self._watch_step_delta("LIVE_WATCH", f"live_user_session poll#{polls}", check_logcat=True)
            after_list = self.ledger.ranked()
            if len(after_list) > before:
                for f in after_list:
                    if f.repro_step == f"live_user_session poll#{polls}":
                        self._emit(f"  !! [{f.severity}] ({f.source}/{f.category}) {f.title}")
            remaining = end_ts - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(interval_sec, remaining))

        self.meta["mode"] = "watch"
        self.meta["elapsedSec"] = round(time.monotonic() - t0, 2)
        self.meta["stepsExecuted"] = self.bridge.calls
        self.meta["restoredState"] = True
        return {
            "ok": True,
            "meta": self.meta,
            "severityCounts": self.ledger.counts_by_severity(),
            "totalUniqueFindings": len(self.ledger.ranked()),
            "vectors": [{"name": "LIVE_WATCH", "status": "FINDINGS" if self.ledger.ranked() else "OK", "newFindings": len(self.ledger.ranked()), "elapsedSec": self.meta["elapsedSec"], "notes": f"{polls} live polls"}],
            "findings": self.ledger.to_dicts(),
        }


# ---------------------------------------------------------------------------
# CLI Entrypoint
# ---------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="cavern_bug_hunter.py",
        description="Cavernize Live Adversarial Bug-Hunter & Chaos Fuzzer via OmniAgent IPC.",
    )
    p.add_argument("--mode", choices=("quick", "standard", "deep", "watch"), default="standard", help="Hunt intensity or live watch mode")
    p.add_argument("--vectors", default="", help="Comma-separated vector substrings to run (e.g. V1,V2,V5)")
    p.add_argument("--no-mutate", action="store_true", help="Read-only mode (no settings/file/UI mutations)")
    p.add_argument("--duration", type=int, default=60, help="Duration in seconds for --mode watch")
    p.add_argument("--interval", type=float, default=3.0, help="Poll interval seconds for --mode watch")
    p.add_argument("--out-md", default="/tmp/cavern_bug_hunt_latest.md", help="Output Markdown report path")
    p.add_argument("--out-json", default="/tmp/cavern_bug_hunt_latest.json", help="Output JSON report path")
    p.add_argument("--quiet", action="store_true", help="Suppress stderr live progress logs")
    p.add_argument("--url", default="", help="Override relay URL")
    p.add_argument("--pair-secret", default="", help="Override 64-hex pair secret")
    p.add_argument("--admin-key", default="", help="Optional admin key")
    p.add_argument("--timeout", type=int, default=45, help="Per-command timeout seconds")
    return p


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    relay, secret, admin = oac._resolve_auth(args, allow_knock=True)
    bridge = LiveCavernBridge(relay, secret, admin, timeout_sec=args.timeout)

    if args.mode == "watch":
        hunter = LiveBugHunter(bridge, mode="watch", allow_mutate=False, verbose=not args.quiet, out_md=args.out_md, out_json=args.out_json)
        report = hunter.run_watch(duration_sec=args.duration, interval_sec=args.interval)
    else:
        allow_mutate = (not args.no_mutate) and (args.mode != "quick")
        hunter = LiveBugHunter(bridge, mode=args.mode, allow_mutate=allow_mutate, verbose=not args.quiet, out_md=args.out_md, out_json=args.out_json)
        sel = [v.strip() for v in args.vectors.split(",") if v.strip()] if args.vectors else None
        report = hunter.run_hunt(selected_vectors=sel)

    md = generate_report_markdown(report)
    if args.out_json:
        Path(args.out_json).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    if args.out_md:
        Path(args.out_md).write_text(md, encoding="utf-8")

    print(md)
    counts = report.get("severityCounts") or {}
    return 0 if report.get("ok") else 1


if __name__ == "__main__":
    raise SystemExit(main())
