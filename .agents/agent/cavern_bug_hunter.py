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
import base64
import hashlib
import json
import math
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
ORIGIN_RANK = {"live_triggered": 0, "current_session": 1, "historical_archive": 2}
SESSION_HEADER_RE = re.compile(r"===\s*Session started\s*\[sid=(?P<sid>[A-Za-z0-9_-]+)\]", re.IGNORECASE)


def split_applog_by_session(text: str, current_session_id: str) -> tuple[list[dict[str, str]], list[dict[str, str]]]:
    """Partition AppLogger entries into (current_session_entries, historical_archive_entries)."""
    cur_sid = (current_session_id or "").strip().lower()
    if not cur_sid:
        return [], parse_app_logger_lines(text)
    active_lines: list[str] = []
    hist_lines: list[str] = []
    in_current = False
    saw_any_header = False
    for raw_line in (text or "").splitlines():
        m = SESSION_HEADER_RE.search(raw_line)
        if m:
            saw_any_header = True
            in_current = m.group("sid").strip().lower() == cur_sid
            continue
        if in_current:
            active_lines.append(raw_line)
        else:
            hist_lines.append(raw_line)
    if not saw_any_header:
        return [], parse_app_logger_lines(text)
    return parse_app_logger_lines("\n".join(active_lines)), parse_app_logger_lines("\n".join(hist_lines))


def classify_ui_latency_ms(elapsed_ms: float, action: str) -> dict[str, str] | None:
    """Flag UI main-thread stalls (>400ms) and freezes (>1500ms) per BP_LIVE_QA_BUG_HUNTER_SOTA."""
    if elapsed_ms >= 1500.0:
        return {
            "severity": "HIGH",
            "category": "ui_freeze",
            "message": f"UI freeze detected on '{action}' ({elapsed_ms:.0f} ms >= 1500 ms budget)",
        }
    if elapsed_ms >= 400.0:
        return {
            "severity": "MEDIUM",
            "category": "ui_stall",
            "message": f"UI stall detected on '{action}' ({elapsed_ms:.0f} ms >= 400 ms budget)",
        }
    return None


def analyze_spatial_trajectory(samples: list[dict[str, Any]], mode_name: str, stem_name: str = "stem") -> list[dict[str, str]]:
    """Detect radial center-pull collapse & alternating-sign azimuth ping-pong oscillation in 3D trajectories."""
    import math as _math

    issues: list[dict[str, str]] = []
    if not samples or len(samples) < 4 or str(mode_name).lower() == "off":
        return issues

    radii: list[float] = []
    azimuths: list[float] = []
    for s in samples:
        x = float(s.get("x", 0.0))
        y = float(s.get("y", 0.0))
        z = float(s.get("z", 1.0))
        if not (_math.isfinite(x) and _math.isfinite(y) and _math.isfinite(z)):
            issues.append({
                "severity": "CRITICAL",
                "category": "spatial_trajectory_nan",
                "message": f"SpatialChoreo({mode_name}/{stem_name}) emitted NaN/Infinity coordinates",
            })
            return issues
        r = _math.sqrt(x * x + y * y + z * z)
        az = _math.degrees(_math.atan2(x, z))
        radii.append(r)
        azimuths.append(az)

    r_min, r_max = min(radii), max(radii)
    if r_min < 0.50 or (r_max - r_min) > 0.25:
        issues.append({
            "severity": "HIGH",
            "category": "spatial_radial_collapse",
            "message": (
                f"SpatialChoreo({mode_name}/{stem_name}) exhibits radial collapse/oscillation toward center "
                f"(r_min={r_min:.2f}, r_max={r_max:.2f}, span={r_max - r_min:.2f})"
            ),
        })

    if str(mode_name).lower() == "orbit":
        # Orbit sweeps smoothly around 360deg, naturally crossing both 0deg and +/-180deg.
        # Only flag ping-pong if unwrapped angular velocity actually reverses direction repeatedly!
        vel_reversals = 0
        prev_vel_sign = 0
        for i in range(1, len(azimuths)):
            d_az = azimuths[i] - azimuths[i - 1]
            while d_az > 180.0:
                d_az -= 360.0
            while d_az < -180.0:
                d_az += 360.0
            if abs(d_az) < 5.0:
                continue
            vel_sign = 1 if d_az > 0 else -1
            if prev_vel_sign != 0 and vel_sign != prev_vel_sign:
                vel_reversals += 1
            prev_vel_sign = vel_sign
        if vel_reversals >= 3:
            issues.append({
                "severity": "HIGH",
                "category": "spatial_pingpong_oscillation",
                "message": (
                    f"SpatialChoreo({mode_name}/{stem_name}) reversed orbital direction repeatedly "
                    f"({vel_reversals} angular-velocity reversals)"
                ),
            })
    else:
        # Detect back-and-forth ping-pong swings across center (azimuth sign reversals with significant amplitude)
        sign_reversals = 0
        prev_sign = 0
        prev_az = azimuths[0]
        for az in azimuths[1:]:
            cur_sign = 1 if az > 8.0 else (-1 if az < -8.0 else 0)
            if cur_sign != 0:
                if prev_sign != 0 and cur_sign != prev_sign:
                    sign_reversals += 1
                prev_sign = cur_sign
            prev_az = az

        if sign_reversals > 2 and (max(azimuths) - min(azimuths)) >= 30.0:
            issues.append({
                "severity": "HIGH",
                "category": "spatial_pingpong_oscillation",
                "message": (
                    f"SpatialChoreo({mode_name}/{stem_name}) oscillates back-and-forth across center "
                    f"({sign_reversals} azimuth sign reversals, az=[{min(azimuths):+.1f}..{max(azimuths):+.1f}]deg)"
                ),
            })

    # Detect radial direction reversals (in-out-in-out pumping toward center)
    rad_reversals = 0
    prev_dr_sign = 0
    for i in range(1, len(radii)):
        dr = radii[i] - radii[i - 1]
        if abs(dr) < 0.015:
            continue
        dr_sign = 1 if dr > 0 else -1
        if prev_dr_sign != 0 and dr_sign != prev_dr_sign:
            rad_reversals += 1
        prev_dr_sign = dr_sign
    if rad_reversals >= 3 and (r_max - r_min) >= 0.12:
        issues.append({
            "severity": "MEDIUM",
            "category": "spatial_radial_pingpong",
            "message": (
                f"SpatialChoreo({mode_name}/{stem_name}) pumps toward/away from center "
                f"({rad_reversals} radial reversals, r=[{r_min:.2f}..{r_max:.2f}])"
            ),
        })
    return issues


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


ONEUI9_STANDARD_RADII_DP: tuple[float, ...] = (
    4.0, 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 22.0, 24.0, 26.0, 28.0, 32.0, 36.0
)


def _argb_to_rgba(argb: int) -> tuple[int, int, int, int]:
    u = int(argb) & 0xFFFFFFFF
    return ((u >> 16) & 0xFF, (u >> 8) & 0xFF, u & 0xFF, (u >> 24) & 0xFF)


def _composite_over(fg_rgba: tuple[int, int, int, int], bg_rgb: tuple[float, float, float]) -> tuple[float, float, float]:
    r, g, b, a = fg_rgba
    alpha = max(0.0, min(1.0, a / 255.0))
    return (
        r * alpha + bg_rgb[0] * (1.0 - alpha),
        g * alpha + bg_rgb[1] * (1.0 - alpha),
        b * alpha + bg_rgb[2] * (1.0 - alpha),
    )


def _srgb_channel_linear(c_255: float) -> float:
    s = max(0.0, min(1.0, c_255 / 255.0))
    return s / 12.92 if s <= 0.03928 else ((s + 0.055) / 1.055) ** 2.4


def _relative_luminance(rgb: tuple[float, float, float]) -> float:
    return (
        0.2126 * _srgb_channel_linear(rgb[0])
        + 0.7152 * _srgb_channel_linear(rgb[1])
        + 0.0722 * _srgb_channel_linear(rgb[2])
    )


def compute_wcag_contrast(fg_argb: int, bg_argb: int | None, base_bg_argb: int = -16250868) -> float:
    """Compute WCAG 2.1 contrast ratio (1.0..21.0) with alpha compositing over the base screen surface."""
    br, bg, bb, _ = _argb_to_rgba(base_bg_argb)
    eff_bg: tuple[float, float, float] = (float(br), float(bg), float(bb))
    if bg_argb is not None:
        eff_bg = _composite_over(_argb_to_rgba(bg_argb), eff_bg)
    eff_fg = _composite_over(_argb_to_rgba(fg_argb), eff_bg)
    l1 = _relative_luminance(eff_fg)
    l2 = _relative_luminance(eff_bg)
    hi, lo = (l1, l2) if l1 >= l2 else (l2, l1)
    return (hi + 0.05) / (lo + 0.05)


def compute_rect_iou(
    r1: tuple[int, int, int, int],
    r2: tuple[int, int, int, int],
) -> tuple[float, int, int]:
    """Return (IoU, overlap_w_px, overlap_h_px) for two (x, y, w, h) rectangles."""
    x1, y1, w1, h1 = r1
    x2, y2, w2, h2 = r2
    if w1 <= 0 or h1 <= 0 or w2 <= 0 or h2 <= 0:
        return 0.0, 0, 0
    ix1 = max(x1, x2)
    iy1 = max(y1, y2)
    ix2 = min(x1 + w1, x2 + w2)
    iy2 = min(y1 + h1, y2 + h2)
    ow = max(0, ix2 - ix1)
    oh = max(0, iy2 - iy1)
    inter = ow * oh
    if inter <= 0:
        return 0.0, 0, 0
    union = w1 * h1 + w2 * h2 - inter
    return (inter / union if union > 0 else 0.0), ow, oh


def _node_has_geometry(node: Any) -> bool:
    if not isinstance(node, dict):
        return False
    if "x" in node and "y" in node:
        return True
    for ch in node.get("children") or []:
        if _node_has_geometry(ch):
            return True
    return False


def analyze_ui_geometry_oracle(
    tree_obj: Any,
    scope: str,
    design_tokens: dict[str, Any] | None = None,
) -> list[dict[str, str]]:
    """Q1 Deterministic Native Android Visual & Geometric Oracle.

    Evaluates screen/parent overflow (scroll-axis-aware), sibling overlap IoU,
    laid-out zero-size interactive/text views, touch target ergonomics, ellipsis &
    hard text clipping, horizontal row alignment, WCAG 2.1 contrast, & One UI 9 token grid.
    """
    defects: list[dict[str, str]] = []
    if not isinstance(tree_obj, dict):
        return defects

    density = float(tree_obj.get("dpDensity") or 3.0)
    if density <= 0.1:
        density = 3.0
    tokens = design_tokens if isinstance(design_tokens, dict) else (tree_obj.get("designTokens") or {})
    base_bg_argb = int(tokens.get("bgScreen")) if isinstance(tokens.get("bgScreen"), int) else -16250868
    allowed_radii = set(ONEUI9_STANDARD_RADII_DP)
    for tk in ("cardCornerDp", "buttonCornerDp", "tileCornerDp"):
        val = tokens.get(tk)
        if isinstance(val, (int, float)) and val > 0:
            allowed_radii.add(float(val))

    disp = tree_obj.get("displayRect") if isinstance(tree_obj.get("displayRect"), dict) else {}
    disp_x = int(disp.get("x") or 0)
    disp_y = int(disp.get("y") or 0)
    disp_w = int(disp.get("w") or disp.get("screenW") or 0)
    disp_h = int(disp.get("h") or disp.get("screenH") or 0)

    tol_px = max(2, int(round(2.0 * density)))
    overlap_min_px = max(2, int(round(2.0 * density)))
    align_tol_px = max(3, int(round(3.5 * density)))
    hard_clip_tol_px = max(3, int(round(4.0 * density)))

    def _is_interactive_leaf(ntype: str) -> bool:
        return ntype in (
            "Button", "OneUiButton", "ImageButton", "EditText",
            "SeekBar", "OneUiWaveMediaSeekBar", "OneUiInlineSelector",
            "Switch", "OneUiSwitch", "CheckBox",
        )

    def _is_compact_allowed_clickable(ntype: str) -> bool:
        return ntype in (
            "Switch", "OneUiSwitch", "CheckBox", "RadioButton",
            "EditText", "OneUiIconView", "ImageView", "TextView",
        )

    def _inspect_root(root_node: Any, root_scope: str, is_dialog: bool) -> None:
        if not isinstance(root_node, dict):
            return
        r_x = int(root_node.get("x") or disp_x)
        r_y = int(root_node.get("y") or disp_y)
        r_w = int(root_node.get("w") or disp_w)
        r_h = int(root_node.get("h") or disp_h)
        bounds_w = r_w if (is_dialog and r_w > 0) else (disp_w or r_w)
        bounds_h = r_h if (is_dialog and r_h > 0) else (disp_h or r_h)
        bounds_left = r_x if is_dialog else disp_x
        bounds_top = r_y if is_dialog else disp_y
        laid_out = bounds_w > 0 and bounds_h > 0

        def walk(
            node: Any,
            depth: int,
            in_h_scroll: bool,
            in_v_scroll: bool,
            parent_rect: tuple[int, int, int, int] | None,
            parent_clips: bool,
            parent_clickable_ok: bool,
            eff_bg_argb: int,
        ) -> None:
            if not isinstance(node, dict) or depth > 22:
                return
            ntype = str(node.get("type") or "View")
            txt = str(node.get("text") or "")
            label = txt[:36] if txt else str(node.get("desc") or ntype)[:36]
            clickable = bool(node.get("clickable"))
            enabled = node.get("enabled") is not False
            has_xy = ("x" in node and "y" in node)
            nx = int(node.get("x") or 0)
            ny = int(node.get("y") or 0)
            nw = int(node.get("w") or 0)
            nh = int(node.get("h") or 0)

            node_bg = node.get("bg")
            cur_bg = int(node_bg) if isinstance(node_bg, int) else eff_bg_argb

            # G3: Zero-size visible interactive or text view on laid-out screen
            if laid_out and (nw <= 0 or nh <= 0):
                if clickable or (txt and ntype in ("TextView", "Button", "OneUiButton", "EditText")) or _is_interactive_leaf(ntype):
                    cat = "ui_zero_size_button" if ntype in ("Button", "OneUiButton") else "ui_zero_size_interactive"
                    defects.append({
                        "severity": "HIGH",
                        "category": cat,
                        "message": f"{root_scope}: Laid-out visible {ntype} '{label}' has zero size ({nw}x{nh})",
                    })

            # G1: Screen & parent bounds overflow (scroll-axis-aware)
            if has_xy and nw > 0 and nh > 0 and bounds_w > 0:
                if not in_h_scroll:
                    right = nx + nw
                    if right > bounds_left + bounds_w + tol_px or nx < bounds_left - tol_px:
                        defects.append({
                            "severity": "HIGH",
                            "category": "ui_horizontal_overflow",
                            "message": (
                                f"{root_scope}: {ntype} '{label}' overflows screen horizontally "
                                f"(x={nx}, right={right}, screen=[{bounds_left}..{bounds_left + bounds_w}])"
                            ),
                        })
                    elif parent_rect is not None and parent_clips:
                        px, _py, pw, _ph = parent_rect
                        if pw > 0 and (right > px + pw + tol_px or nx < px - tol_px):
                            defects.append({
                                "severity": "HIGH",
                                "category": "ui_parent_horizontal_clip",
                                "message": (
                                    f"{root_scope}: {ntype} '{label}' clips parent bounds horizontally "
                                    f"(x={nx}..{right} outside parent {px}..{px + pw})"
                                ),
                            })
                if not in_v_scroll and bounds_h > 0:
                    bottom = ny + nh
                    if bottom > bounds_top + bounds_h + tol_px or ny < bounds_top - tol_px:
                        defects.append({
                            "severity": "HIGH",
                            "category": "ui_vertical_overflow",
                            "message": (
                                f"{root_scope}: {ntype} '{label}' overflows screen vertically "
                                f"(y={ny}, bottom={bottom}, screen=[{bounds_top}..{bounds_top + bounds_h}])"
                            ),
                        })

            # G4: Touch target ergonomics (respect clickable parent wrappers & compact controls)
            if clickable and enabled and nw > 0 and nh > 0 and not parent_clickable_ok:
                w_dp = nw / density
                h_dp = nh / density
                min_dp = min(w_dp, h_dp)
                if not _is_compact_allowed_clickable(ntype):
                    if min_dp < 24.0:
                        defects.append({
                            "severity": "HIGH",
                            "category": "ui_touch_target_crushed",
                            "message": (
                                f"{root_scope}: Crushed touch target on {ntype} '{label}' "
                                f"({w_dp:.1f}x{h_dp:.1f}dp < 24dp)"
                            ),
                        })
                    elif ntype in ("Button", "OneUiButton", "ImageButton") and min_dp < 28.0:
                        defects.append({
                            "severity": "MEDIUM",
                            "category": "ui_touch_target_small",
                            "message": (
                                f"{root_scope}: Undersized button touch target on {ntype} '{label}' "
                                f"({w_dp:.1f}x{h_dp:.1f}dp < 28dp)"
                            ),
                        })

            # Dead clickable control
            if clickable and enabled and node.get("hasClick") is False and ntype in ("Button", "OneUiButton", "ImageButton"):
                defects.append({
                    "severity": "MEDIUM",
                    "category": "ui_dead_clickable",
                    "message": f"{root_scope}: Clickable {ntype} '{label}' has no OnClickListener attached",
                })

            # G5: Ellipsis & hard text clipping
            ell = int(node.get("ell") or 0)
            ell_mode = str(node.get("ellMode") or "")
            meas_w = int(node.get("measW") or 0)
            if ell > 0:
                if ntype in ("Button", "OneUiButton"):
                    defects.append({
                        "severity": "HIGH",
                        "category": "ui_button_text_ellipsized",
                        "message": f"{root_scope}: Button '{label}' text is ellipsized ({ell} chars truncated)",
                    })
                elif len(txt.strip()) <= 24:
                    defects.append({
                        "severity": "MEDIUM",
                        "category": "ui_text_ellipsized",
                        "message": f"{root_scope}: Short {ntype} '{label}' text is ellipsized ({ell} chars truncated)",
                    })
            if meas_w > 0 and nw > 0 and not ell_mode and meas_w > nw + hard_clip_tol_px:
                defects.append({
                    "severity": "HIGH",
                    "category": "ui_text_hard_clipped",
                    "message": (
                        f"{root_scope}: {ntype} '{label}' text hard-clipped without ellipsis "
                        f"(measured={meas_w}px > width={nw}px)"
                    ),
                })

            # G7: WCAG 2.1 contrast & G8: minimum readable font size
            sp = node.get("sp")
            if isinstance(sp, (int, float)) and txt and sp > 0 and sp < 9.0:
                defects.append({
                    "severity": "MEDIUM",
                    "category": "ui_text_too_small",
                    "message": f"{root_scope}: {ntype} '{label}' font size {sp:.1f}sp is below 9sp readability floor",
                })
            fg = node.get("fg")
            has_custom_bg = bool(node.get("customBg"))
            if isinstance(fg, int) and txt and enabled and not has_custom_bg:
                fg_a = (fg >> 24) & 0xFF
                if fg_a >= 96:
                    ratio = compute_wcag_contrast(fg, cur_bg, base_bg_argb)
                    if ratio < 2.2:
                        defects.append({
                            "severity": "HIGH",
                            "category": "ui_wcag_contrast_fail",
                            "message": (
                                f"{root_scope}: Unreadable WCAG contrast ({ratio:.2f}:1 < 2.2) on {ntype} '{label}'"
                            ),
                        })
                    elif ratio < 3.0:
                        defects.append({
                            "severity": "MEDIUM",
                            "category": "ui_wcag_contrast_warn",
                            "message": (
                                f"{root_scope}: Low WCAG contrast ({ratio:.2f}:1 < 3.0) on {ntype} '{label}'"
                            ),
                        })

            # G8: One UI 9 corner radius token grid
            rad_dp = node.get("radDp")
            if isinstance(rad_dp, (int, float)) and 0.5 < rad_dp < 200.0 and nw > 0 and nh > 0:
                min_half_dp = min(nw, nh) / (2.0 * density)
                is_capsule = abs(rad_dp - min_half_dp) <= 2.5 or rad_dp >= min_half_dp - 1.5
                if not is_capsule:
                    nearest = min(allowed_radii, key=lambda t: abs(t - rad_dp))
                    if abs(nearest - rad_dp) > 2.0:
                        defects.append({
                            "severity": "LOW",
                            "category": "ui_token_radius_drift",
                            "message": (
                                f"{root_scope}: Non-token corner radius {rad_dp:.1f}dp on {ntype} '{label}' "
                                f"(nearest One UI 9 token={nearest:.0f}dp)"
                            ),
                        })

            # G2 & G6: Sibling overlap IoU & horizontal row alignment
            children = node.get("children") or []
            if len(children) >= 2:
                is_linear = ntype == "LinearLayout"
                orient = str(node.get("orient") or "")
                vis_children = [
                    ch for ch in children
                    if isinstance(ch, dict) and "x" in ch and "y" in ch
                    and int(ch.get("w") or 0) > 0 and int(ch.get("h") or 0) > 0
                ]
                for i in range(len(vis_children)):
                    c1 = vis_children[i]
                    r1 = (int(c1["x"]), int(c1["y"]), int(c1["w"]), int(c1["h"]))
                    c1_click = bool(c1.get("clickable")) and c1.get("enabled") is not False
                    for j in range(i + 1, len(vis_children)):
                        c2 = vis_children[j]
                        r2 = (int(c2["x"]), int(c2["y"]), int(c2["w"]), int(c2["h"]))
                        iou, ow, oh = compute_rect_iou(r1, r2)
                        if is_linear and iou > 0.05 and ow > overlap_min_px and oh > overlap_min_px:
                            l1 = str(c1.get("text") or c1.get("type") or "View")[:24]
                            l2 = str(c2.get("text") or c2.get("type") or "View")[:24]
                            defects.append({
                                "severity": "HIGH",
                                "category": "ui_sibling_overlap",
                                "message": (
                                    f"{root_scope}: Overlapping LinearLayout siblings '{l1}' & '{l2}' "
                                    f"(IoU={iou:.2f}, overlap={ow}x{oh}px)"
                                ),
                            })
                        elif (not is_linear) and c1_click and bool(c2.get("clickable")) and c2.get("enabled") is not False:
                            if iou > 0.15 and ow > overlap_min_px and oh > overlap_min_px:
                                l1 = str(c1.get("text") or c1.get("type") or "View")[:24]
                                l2 = str(c2.get("text") or c2.get("type") or "View")[:24]
                                defects.append({
                                    "severity": "HIGH",
                                    "category": "ui_clickable_overlap",
                                    "message": (
                                        f"{root_scope}: Overlapping clickable siblings '{l1}' & '{l2}' "
                                        f"(IoU={iou:.2f}, overlap={ow}x{oh}px)"
                                    ),
                                })

                if is_linear and orient == "h":
                    btns = [
                        ch for ch in vis_children
                        if str(ch.get("type") or "") in ("Button", "OneUiButton")
                    ]
                    if len(btns) >= 2:
                        if node.get("baselineAligned") is True:
                            defects.append({
                                "severity": "MEDIUM",
                                "category": "ui_row_misalignment",
                                "message": f"{root_scope}: Horizontal button row ({len(btns)} btns) missing BaselineAligned=false",
                            })
                        cys = [int(b["y"]) + int(b["h"]) / 2.0 for b in btns]
                        drift = max(cys) - min(cys)
                        if drift > align_tol_px:
                            defects.append({
                                "severity": "MEDIUM",
                                "category": "ui_row_misalignment",
                                "message": (
                                    f"{root_scope}: Horizontal button row vertical center drift "
                                    f"({drift / density:.1f}dp > 3.5dp across {len(btns)} buttons)"
                                ),
                            })

            # Recurse into children
            scroll_ax = str(node.get("scrollAxis") or "")
            next_h_scroll = in_h_scroll or scroll_ax == "h" or ntype == "HorizontalScrollView"
            next_v_scroll = in_v_scroll or scroll_ax == "v" or ntype in ("ScrollView", "NestedScrollView", "ListView")
            next_rect = (nx, ny, nw, nh) if (has_xy and nw > 0 and nh > 0) else parent_rect
            next_clips = node.get("clipCh") is not False
            this_clickable_ok = parent_clickable_ok or (
                clickable and enabled and nw > 0 and nh > 0 and min(nw / density, nh / density) >= 40.0
            )
            for ch in children:
                walk(
                    ch,
                    depth + 1,
                    next_h_scroll,
                    next_v_scroll,
                    next_rect,
                    next_clips,
                    this_clickable_ok,
                    cur_bg,
                )

        walk(
            root_node,
            0,
            in_h_scroll=False,
            in_v_scroll=False,
            parent_rect=(bounds_left, bounds_top, bounds_w, bounds_h) if laid_out else None,
            parent_clips=True,
            parent_clickable_ok=False,
            eff_bg_argb=base_bg_argb,
        )

    if tree_obj.get("activity"):
        _inspect_root(tree_obj["activity"], scope, is_dialog=False)
    for idx, dlg in enumerate(tree_obj.get("dialogs") or []):
        _inspect_root(dlg, f"{scope}:Dialog{idx}", is_dialog=True)
    return defects


def decode_luma_grid(payload: dict[str, Any]) -> tuple[int, int, list[int]] | None:
    """Decode a deterministic grayscale luma grid from `screenshot_ui` (`lumaB64`) or raw PGM/PPM bytes."""
    if not isinstance(payload, dict):
        return None
    b64 = payload.get("lumaB64")
    lw = int(payload.get("lumaW") or 0)
    lh = int(payload.get("lumaH") or 0)
    if isinstance(b64, str) and b64 and lw > 0 and lh > 0:
        try:
            raw = base64.b64decode(b64)
            if len(raw) == lw * lh:
                return lw, lh, list(raw)
        except Exception:
            return None
    # Pure-Python binary PGM (P5) fallback
    pgm_bytes = payload.get("pgmBytes")
    if isinstance(pgm_bytes, (bytes, bytearray)) and pgm_bytes.startswith(b"P5"):
        parts = pgm_bytes.split(None, 3)
        if len(parts) == 4:
            try:
                pw, ph, _maxv = int(parts[1]), int(parts[2]), int(parts[3].split(None, 1)[0])
                pix = parts[3].split(None, 1)[1]
                if len(pix) >= pw * ph:
                    return pw, ph, list(pix[: pw * ph])
            except Exception:
                return None
    return None


def compute_ssim_luma_grid(
    width: int,
    height: int,
    ref_px: list[int],
    cur_px: list[int],
    block_size: int = 8,
) -> dict[str, Any]:
    """Pure-Python Wang et al. (2004) windowed SSIM over 8-bit grayscale grids (zero external deps)."""
    if width <= 0 or height <= 0 or len(ref_px) != width * height or len(cur_px) != width * height:
        return {"ok": False, "ssim": 0.0, "minBlockSsim": 0.0, "changedBlocks": 0, "totalBlocks": 0, "diffBBox": None}

    c1 = (0.01 * 255.0) ** 2
    c2 = (0.03 * 255.0) ** 2
    bs = max(2, min(block_size, width, height))

    ssim_sum = 0.0
    min_ssim = 1.0
    total_blocks = 0
    changed_blocks = 0
    min_bx, min_by, max_bx, max_by = width, height, 0, 0

    for by in range(0, height - bs + 1, bs):
        for bx in range(0, width - bs + 1, bs):
            n = bs * bs
            sx = 0.0
            sy = 0.0
            sxx = 0.0
            syy = 0.0
            sxy = 0.0
            for dy in range(bs):
                row_off = (by + dy) * width + bx
                for dx in range(bs):
                    x = float(ref_px[row_off + dx])
                    y = float(cur_px[row_off + dx])
                    sx += x
                    sy += y
                    sxx += x * x
                    syy += y * y
                    sxy += x * y
            mx = sx / n
            my = sy / n
            vx = max(0.0, sxx / n - mx * mx)
            vy = max(0.0, syy / n - my * my)
            cov = sxy / n - mx * my
            num = (2.0 * mx * my + c1) * (2.0 * cov + c2)
            den = (mx * mx + my * my + c1) * (vx + vy + c2)
            b_ssim = num / den if den > 1e-12 else 1.0
            b_ssim = max(-1.0, min(1.0, b_ssim))
            ssim_sum += b_ssim
            total_blocks += 1
            if b_ssim < min_ssim:
                min_ssim = b_ssim
            if b_ssim < 0.90:
                changed_blocks += 1
                min_bx = min(min_bx, bx)
                min_by = min(min_by, by)
                max_bx = max(max_bx, bx + bs)
                max_by = max(max_by, by + bs)

    mean_ssim = ssim_sum / total_blocks if total_blocks > 0 else 1.0
    bbox = [min_bx, min_by, max_bx - min_bx, max_by - min_by] if changed_blocks > 0 else None
    return {
        "ok": True,
        "ssim": round(mean_ssim, 5),
        "minBlockSsim": round(min_ssim, 5),
        "changedBlocks": changed_blocks,
        "totalBlocks": total_blocks,
        "diffBBox": bbox,
    }


def compare_screenshot_against_golden(
    screen_key: str,
    shot_payload: dict[str, Any],
    golden_dir: str | Path,
    *,
    update_goldens: bool = False,
    fail_threshold: float = 0.92,
    warn_threshold: float = 0.97,
) -> dict[str, Any]:
    """Compare a captured `screenshot_ui` luma grid against a golden baseline (or seed/update baseline)."""
    decoded = decode_luma_grid(shot_payload)
    if decoded is None:
        return {"status": "SKIP", "reason": "no_luma_grid", "finding": None}
    w, h, cur_px = decoded
    safe_key = re.sub(r"[^A-Za-z0-9_.-]+", "_", screen_key).strip("_") or "screen"
    gdir = Path(golden_dir)
    gfile = gdir / f"{safe_key}.luma.json"

    if update_goldens or not gfile.is_file():
        gdir.mkdir(parents=True, exist_ok=True)
        gfile.write_text(
            json.dumps({
                "screen": screen_key,
                "lumaW": w,
                "lumaH": h,
                "lumaB64": base64.b64encode(bytes(cur_px)).decode("ascii"),
                "updatedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            }, indent=2),
            encoding="utf-8",
        )
        return {"status": "SEEDED", "goldenPath": str(gfile), "ssim": 1.0, "finding": None}

    try:
        ref_obj = json.loads(gfile.read_text(encoding="utf-8"))
        ref_dec = decode_luma_grid(ref_obj)
    except Exception as exc:
        return {"status": "ERROR", "reason": str(exc), "finding": None}
    if ref_dec is None or ref_dec[0] != w or ref_dec[1] != h:
        return {
            "status": "MISMATCH_DIM",
            "reason": f"golden={ref_dec[0] if ref_dec else 0}x{ref_dec[1] if ref_dec else 0} vs cur={w}x{h}",
            "finding": {
                "severity": "MEDIUM",
                "category": "ui_visual_dimension_drift",
                "message": f"{screen_key}: Golden SSIM grid size changed ({ref_dec[0] if ref_dec else 0}x{ref_dec[1] if ref_dec else 0} -> {w}x{h})",
            },
        }

    res = compute_ssim_luma_grid(w, h, ref_dec[2], cur_px)
    ssim_val = float(res["ssim"])
    finding = None
    status = "PASS"
    if ssim_val < fail_threshold:
        status = "FAIL"
        finding = {
            "severity": "HIGH",
            "category": "ui_visual_regression_ssim",
            "message": (
                f"{screen_key}: Visual regression vs golden (SSIM={ssim_val:.4f} < {fail_threshold:.2f}, "
                f"minBlock={res['minBlockSsim']:.3f}, changed={res['changedBlocks']}/{res['totalBlocks']}, bbox={res['diffBBox']})"
            ),
        }
    elif ssim_val < warn_threshold:
        status = "WARN"
        finding = {
            "severity": "MEDIUM",
            "category": "ui_visual_drift_ssim",
            "message": (
                f"{screen_key}: Visual drift vs golden (SSIM={ssim_val:.4f} < {warn_threshold:.2f}, "
                f"changed={res['changedBlocks']}/{res['totalBlocks']})"
            ),
        }
    return {"status": status, **res, "finding": finding}


def run_m1_mutation_canary_offline(tmp_dir: str | Path) -> dict[str, Any]:
    """Prove Q1 Visual & Geometric Oracle is non-decorative: 100% catch rate on injected M1 defects & 0% FP on clean One UI 9 tree."""
    tokens = {
        "cardCornerDp": 26.0,
        "buttonCornerDp": 18.0,
        "tileCornerDp": 22.0,
        "bgScreen": -16250868,  # #FF080A10
    }
    clean_tree = {
        "schemaVersion": 2,
        "dpDensity": 3.0,
        "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400},
        "designTokens": tokens,
        "activity": {
            "type": "FrameLayout",
            "x": 0, "y": 0, "w": 1080, "h": 2400,
            "children": [
                # Intentional FrameLayout overlay pill (non-clickable background pill behind nav buttons)
                {"type": "View", "x": 40, "y": 2160, "w": 200, "h": 150, "bg": 0x1AFFFFFF, "radDp": 25.0},
                {
                    "type": "LinearLayout", "orient": "v", "x": 0, "y": 100, "w": 1080, "h": 2000,
                    "children": [
                        {
                            "type": "HorizontalScrollView", "scrollAxis": "h", "x": 24, "y": 120, "w": 1032, "h": 140,
                            "children": [
                                # Off-screen chip inside HorizontalScrollView -> MUST NOT false-positive!
                                {"type": "Button", "text": "Chip Far Right", "clickable": True, "x": 960, "y": 124, "w": 360, "h": 128, "fg": -1, "bg": -1139140063, "sp": 13.0, "radDp": 18.0},
                            ],
                        },
                        {
                            "type": "LinearLayout", "orient": "h", "x": 24, "y": 300, "w": 1032, "h": 140,
                            "children": [
                                {"type": "Button", "text": "Play", "clickable": True, "x": 24, "y": 306, "w": 490, "h": 128, "fg": -1, "bg": -13083393, "sp": 14.0, "radDp": 18.0},
                                {"type": "Button", "text": "Queue", "clickable": True, "x": 540, "y": 306, "w": 490, "h": 128, "fg": -1, "bg": -1139140063, "sp": 14.0, "radDp": 18.0},
                            ],
                        },
                        # Disabled muted button -> MUST NOT fail WCAG!
                        {"type": "Button", "text": "Cancel", "enabled": False, "x": 24, "y": 480, "w": 300, "h": 128, "fg": 0x66404450, "bg": -16250868, "sp": 13.0, "radDp": 18.0},
                        # Empty hidden-slot LinearLayout (non-clickable, no text) -> MUST NOT fail zero-size!
                        {"type": "LinearLayout", "x": 24, "y": 640, "w": 1032, "h": 0},
                    ],
                },
            ],
        },
    }
    clean_defects = analyze_ui_tree_anomalies(clean_tree, "CleanOneUi9")

    mutations: list[tuple[str, str, dict[str, Any]]] = [
        ("M1_horiz_overflow", "ui_horizontal_overflow", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "h", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "Button", "text": "Repeat", "clickable": True, "x": 980, "y": 20, "w": 180, "h": 128, "fg": -1, "sp": 14.0}
            ]},
        }),
        ("M1_sibling_overlap", "ui_sibling_overlap", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "h", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "Button", "text": "Vol", "clickable": True, "x": 20, "y": 20, "w": 300, "h": 128, "fg": -1, "sp": 14.0},
                {"type": "Button", "text": "Loop", "clickable": True, "x": 200, "y": 20, "w": 300, "h": 128, "fg": -1, "sp": 14.0},
            ]},
        }),
        ("M1_clickable_overlap", "ui_clickable_overlap", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "FrameLayout", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "Button", "text": "A", "clickable": True, "x": 40, "y": 20, "w": 200, "h": 128, "fg": -1, "sp": 14.0},
                {"type": "Button", "text": "B", "clickable": True, "x": 80, "y": 20, "w": 200, "h": 128, "fg": -1, "sp": 14.0},
            ]},
        }),
        ("M1_crushed_target", "ui_touch_target_crushed", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "Button", "text": "X", "clickable": True, "x": 40, "y": 20, "w": 48, "h": 48, "fg": -1, "sp": 12.0},
            ]},
        }),
        ("M1_zero_size_interactive", "ui_zero_size_interactive", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "OneUiInlineSelector", "clickable": True, "x": 40, "y": 20, "w": 0, "h": 0},
            ]},
        }),
        ("M1_button_ellipsis", "ui_button_text_ellipsized", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "Button", "text": "A/B Compare: ACTIVE", "clickable": True, "x": 40, "y": 20, "w": 220, "h": 128, "ell": 7, "ellMode": "end", "fg": -1, "sp": 13.0},
            ]},
        }),
        ("M1_text_hard_clipped", "ui_text_hard_clipped", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "TextView", "text": "Unclipped Overflow Title", "x": 40, "y": 20, "w": 180, "h": 60, "measW": 340, "fg": -1, "sp": 14.0},
            ]},
        }),
        ("M1_row_misaligned", "ui_row_misalignment", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "h", "x": 0, "y": 0, "w": 1080, "h": 220, "children": [
                {"type": "Button", "text": "Left", "clickable": True, "x": 20, "y": 20, "w": 240, "h": 128, "fg": -1, "sp": 14.0},
                {"type": "Button", "text": "Right", "clickable": True, "x": 300, "y": 50, "w": 240, "h": 128, "fg": -1, "sp": 14.0},
            ]},
        }),
        ("M1_wcag_low_contrast", "ui_wcag_contrast_fail", {
            "schemaVersion": 2, "dpDensity": 3.0, "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400}, "designTokens": tokens,
            "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 200, "children": [
                {"type": "TextView", "text": "Invisible Text", "x": 40, "y": 20, "w": 400, "h": 80, "fg": 0xFF151822, "bg": -16250868, "sp": 14.0},
            ]},
        }),
    ]

    caught = 0
    details: dict[str, bool] = {}
    for m_name, expected_cat, m_tree in mutations:
        found = analyze_ui_tree_anomalies(m_tree, m_name)
        hit = any(d["category"] == expected_cat for d in found)
        details[m_name] = hit
        if hit:
            caught += 1

    # Also prove Golden SSIM canary: identical passes (~1.0), mutated banner fails (<0.92)
    w, h = 48, 96
    ref_grid = bytes(((x * 3 + y * 2) % 256) for y in range(h) for x in range(w))
    mut_grid = bytearray(ref_grid)
    for y in range(10, 60):
        for x in range(4, 44):
            mut_grid[y * w + x] = 250 if (x + y) % 2 == 0 else 5
    gdir = Path(tmp_dir) / "m1_goldens"
    seed = compare_screenshot_against_golden("tab0", {"lumaW": w, "lumaH": h, "lumaB64": base64.b64encode(ref_grid).decode("ascii")}, gdir)
    same = compare_screenshot_against_golden("tab0", {"lumaW": w, "lumaH": h, "lumaB64": base64.b64encode(ref_grid).decode("ascii")}, gdir)
    diff = compare_screenshot_against_golden("tab0", {"lumaW": w, "lumaH": h, "lumaB64": base64.b64encode(bytes(mut_grid)).decode("ascii")}, gdir)
    ssim_canary_ok = (
        seed["status"] == "SEEDED"
        and same["status"] == "PASS"
        and diff["status"] == "FAIL"
        and (diff.get("finding") or {}).get("category") == "ui_visual_regression_ssim"
    )
    details["M1_golden_ssim"] = ssim_canary_ok
    if ssim_canary_ok:
        caught += 1

    total = len(mutations) + 1
    return {
        "ok": caught == total and len(clean_defects) == 0,
        "caught": caught,
        "total": total,
        "detectRate": caught / total,
        "cleanFalsePositives": len(clean_defects),
        "cleanDefects": clean_defects,
        "details": details,
    }


def analyze_ui_tree_anomalies(tree_obj: Any, scope: str) -> list[dict[str, str]]:
    """Inspect a `ui_tree` JSON object for real user-visible UI & Q1 geometric/visual defects."""
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

    has_geom = int(tree_obj.get("schemaVersion") or 1) >= 2 or _node_has_geometry(tree_obj.get("activity"))

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
        if not has_geom:
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

    if has_geom:
        defects.extend(analyze_ui_geometry_oracle(tree_obj, scope))
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
    origin: str = "live_triggered"
    count: int = 1
    first_seen_utc: str = ""
    last_seen_utc: str = ""
    extra_examples: list[str] = field(default_factory=list)


class FindingLedger:
    """Deduplicating bug ledger ranked by origin (active before historical), severity, and frequency."""

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
        origin: str = "live_triggered",
    ) -> tuple[bool, Finding]:
        sev = severity.upper() if severity.upper() in SEVERITY_RANK else "MEDIUM"
        orig = origin if origin in ORIGIN_RANK else "live_triggered"
        sig = normalize_signature(source, category, title)
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        existing = self._items.get(sig)
        if existing is not None:
            existing.count += 1
            existing.last_seen_utc = now
            if SEVERITY_RANK[sev] < SEVERITY_RANK[existing.severity]:
                existing.severity = sev
            # Promote origin if a historical defect is reproduced live in the active session
            if ORIGIN_RANK[orig] < ORIGIN_RANK.get(existing.origin, 2):
                existing.origin = orig
                existing.vector = vector
                existing.repro_step = (repro_step or "").strip()[:240]
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
            origin=orig,
            count=1,
            first_seen_utc=now,
            last_seen_utc=now,
        )
        self._items[sig] = f
        return True, f

    def ranked(self) -> list[Finding]:
        return sorted(
            self._items.values(),
            key=lambda x: (
                ORIGIN_RANK.get(x.origin, 2),
                SEVERITY_RANK.get(x.severity, 9),
                -x.count,
                x.vector,
                x.title,
            ),
        )

    def active_findings(self) -> list[Finding]:
        return [f for f in self.ranked() if f.origin != "historical_archive"]

    def historical_findings(self) -> list[Finding]:
        return [f for f in self.ranked() if f.origin == "historical_archive"]

    def counts_by_severity(self, active_only: bool = False) -> dict[str, int]:
        counts = {"CRITICAL": 0, "HIGH": 0, "MEDIUM": 0, "LOW": 0}
        pool = self.active_findings() if active_only else list(self._items.values())
        for f in pool:
            if f.severity in counts:
                counts[f.severity] += 1
        return counts

    def counts_by_origin(self) -> dict[str, int]:
        counts = {"live_triggered": 0, "current_session": 0, "historical_archive": 0}
        for f in self._items.values():
            key = f.origin if f.origin in counts else "live_triggered"
            counts[key] += 1
        return counts

    def to_dicts(self) -> list[dict[str, Any]]:
        return [
            {
                "signature": f.signature,
                "severity": f.severity,
                "origin": f.origin,
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
    """Render a concise, stratified Markdown bug dossier (Active Live/Session vs Historical Archive)."""
    lines: list[str] = []
    meta = report.get("meta") or {}
    counts = report.get("severityCounts") or {}
    active_counts = report.get("activeSeverityCounts") or counts
    origins = report.get("originCounts") or {}
    findings = report.get("findings") or []
    active_list = [f for f in findings if f.get("origin", "live_triggered") != "historical_archive"]
    hist_list = [f for f in findings if f.get("origin") == "historical_archive"]

    lines.append("# Cavernize Live Adversarial Bug-Hunter Report (SOTA v2)")
    lines.append("")
    lines.append(
        f"- **App:** `v{meta.get('appVersion', '?')}` (build `{meta.get('appBuild', '?')}`) | "
        f"**Session:** `{meta.get('sessionId', '?')}` | "
        f"**Device:** `{meta.get('device', '?')}` | **Android:** `{meta.get('android', '?')}`"
    )
    lines.append(
        f"- **Mode:** `{meta.get('mode', '?')}` | **Steps Executed:** `{meta.get('stepsExecuted', 0)}` | "
        f"**Elapsed:** `{meta.get('elapsedSec', 0):.1f}s`"
    )
    lines.append(
        f"- **Active Findings (Live + Current Session):** `{len(active_list)}` "
        f"(`CRITICAL={active_counts.get('CRITICAL', 0)}` · `HIGH={active_counts.get('HIGH', 0)}` · "
        f"`MEDIUM={active_counts.get('MEDIUM', 0)}` · `LOW={active_counts.get('LOW', 0)}`)"
    )
    lines.append(
        f"- **Origin Breakdown:** `live_triggered={origins.get('live_triggered', 0)}` · "
        f"`current_session={origins.get('current_session', 0)}` · "
        f"`historical_archive={origins.get('historical_archive', len(hist_list))}` "
        f"(Total unique: `{report.get('totalUniqueFindings', len(findings))}`)"
    )
    if meta.get("restoredState") is not None:
        diff_keys = meta.get("restoreDiffKeys") or []
        lines.append(
            f"- **State Restored Cleanly:** `{meta.get('restoredState')}` "
            f"(settings diff keys: `{len(diff_keys)}`)"
        )
    lines.append("")

    vectors = report.get("vectors") or []
    if vectors:
        lines.append("## Vector Execution Summary")
        lines.append("")
        lines.append("| Vector | Status | New Findings | Elapsed | Notes |")
        lines.append("|---|---|---:|---:|---|")
        for v in vectors:
            notes = str(v.get("notes") or "").replace("|", "\\|").replace("\n", " ")[:140]
            lines.append(
                f"| `{v.get('name')}` | **{v.get('status')}** | {v.get('newFindings', 0)} | "
                f"{v.get('elapsedSec', 0):.1f}s | {notes} |"
            )
        lines.append("")

    def _render_findings(section_title: str, items: list[dict[str, Any]], empty_msg: str) -> None:
        lines.append(f"## {section_title}")
        lines.append("")
        if not items:
            lines.append(empty_msg)
            lines.append("")
            return
        for idx, f in enumerate(items, 1):
            orig = f.get("origin", "live_triggered")
            lines.append(
                f"### {idx}. [{f['severity']}] `{f['category']}` ({f['count']}x · `{orig}`) — {f['title']}"
            )
            lines.append(f"- **Vector:** `{f['vector']}` | **Source:** `{f['source']}` | **Sig:** `{f['signature']}`")
            lines.append(f"- **Trigger / Repro:** `{f['reproStep']}`")
            lines.append(f"- **Evidence:** `{f['evidence']}`")
            for ex in f.get("extraExamples") or []:
                lines.append(f"  - *Also seen:* `{ex}`")
            lines.append("")

    _render_findings(
        "Active Findings — Current Build & Live Triggered (Actionable)",
        active_list,
        "No active anomalies or errors triggered on the current build session.",
    )
    if hist_list:
        _render_findings(
            "Historical Pre-Fix Archive (Prior Sessions in Retained AppLogger — Non-Blocking)",
            hist_list,
            "None.",
        )
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
        golden_dir: str = "",
        update_goldens: bool = False,
    ) -> None:
        self.bridge = bridge
        self.mode = mode
        self.allow_mutate = allow_mutate
        self.verbose = verbose
        self.out_md = out_md
        self.out_json = out_json
        self.golden_dir = golden_dir
        self.update_goldens = update_goldens
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
                if vector == "V5_corrupt_file_fuzzer" and entry["level"] in ("WARN", "ERROR"):
                    low_msg = entry["msg"].lower()
                    if (
                        "could not decode this audio file" in low_msg
                        or "openlocalpathwithfallbackasync rejected" in low_msg
                    ):
                        # Expected graceful rejection of intentionally malformed synthetic fuzz files
                        continue
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
            self._capabilities = {c for c in cmd_names if isinstance(c, str)}
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
            "severityCounts": self.ledger.counts_by_severity(active_only=False),
            "activeSeverityCounts": self.ledger.counts_by_severity(active_only=True),
            "originCounts": self.ledger.counts_by_origin(),
            "totalUniqueFindings": len(self.ledger.ranked()),
            "totalActiveFindings": len(self.ledger.active_findings()),
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
                cur_entries, hist_entries = split_applog_by_session(fr["body"], str(self.meta.get("sessionId") or ""))
                mined_lines = len(cur_entries) + len(hist_entries)
                for origin_tag, entry_list in (("current_session", cur_entries), ("historical_archive", hist_entries)):
                    for e in entry_list:
                        if e["level"] in ("WARN", "ERROR", "FATAL"):
                            sev, cat = classify_applog_severity(e)
                            self.ledger.record(
                                severity=sev,
                                vector="V1_log_forensics",
                                source="applogger_current" if origin_tag == "current_session" else "applogger_history",
                                category=cat,
                                title=e["msg"][:200],
                                repro_step=f"{origin_tag} ({e['ts'] or 'recent'})",
                                evidence=e["raw"],
                                origin=origin_tag,
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
                    origin="historical_archive",
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
                    origin="historical_archive",
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
                        origin="historical_archive",
                    )

        return f"mined {mined_lines} AppLogger entries (stratified active vs historical) + logcat W/E/crash/Choreographer"

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
        boundary_rejected_ok = 0
        for i, item in enumerate(res):
            lbl = labels[i] if i < len(labels) else f"step#{i}"
            is_boundary = lbl.startswith("boundary ")
            if item["subCmd"] == "settings_set" and not item["ok"]:
                if is_boundary:
                    # Rejecting an intentionally out-of-range boundary probe is safe validation, NOT a bug!
                    boundary_rejected_ok += 1
                    continue
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
        return f"executed {len(steps)} rapid-fire DSP/boundary steps (safe boundary rejects={boundary_rejected_ok}) & restored settings"

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

        # 6b. Rapid Tab Thrash + Full Q1 Geometric/Visual ui_tree Inspection + optional Golden SSIM
        geom_tabs_scanned = 0
        ssim_tabs_checked = 0
        for tab_idx in (0, 4, 1, 3, 2):
            self.bridge.cmd("tab_switch", {"index": tab_idx}, tier=1, timeout_sec=15)
            ut = self.bridge.cmd("ui_tree", {"maxDepth": 12, "geometry": True}, tier=0, timeout_sec=20)
            if ut["ok"] and isinstance(ut["json"], dict):
                geom_tabs_scanned += 1
                for d in analyze_ui_tree_anomalies(ut["json"], f"Tab{tab_idx}"):
                    self.ledger.record(
                        severity=d["severity"],
                        vector="V6_ui_monkey_and_audit",
                        source="ui_tree_q1",
                        category=d["category"],
                        title=d["message"],
                        repro_step=f"tab_switch({tab_idx}) -> ui_tree(maxDepth=12,geometry=true)",
                        evidence=d["message"],
                    )
            if self.golden_dir:
                sh = self.bridge.cmd("screenshot_ui", {"maxWidth": 480, "quality": 65}, tier=1, timeout_sec=20)
                if sh["ok"] and isinstance(sh["json"], dict):
                    ssim_res = compare_screenshot_against_golden(
                        f"Tab{tab_idx}",
                        sh["json"],
                        self.golden_dir,
                        update_goldens=self.update_goldens,
                    )
                    if ssim_res.get("status") in ("PASS", "WARN", "FAIL", "SEEDED", "MISMATCH_DIM"):
                        ssim_tabs_checked += 1
                    f_ssim = ssim_res.get("finding")
                    if isinstance(f_ssim, dict):
                        self.ledger.record(
                            severity=f_ssim["severity"],
                            vector="V6_ui_monkey_and_audit",
                            source="golden_ssim_q1",
                            category=f_ssim["category"],
                            title=f_ssim["message"],
                            repro_step=f"tab_switch({tab_idx}) -> screenshot_ui -> SSIM",
                            evidence=f_ssim["message"],
                        )

        steps: list[dict[str, Any]] = []
        if self.allow_mutate:
            steps.append({"cmd": "tab_switch", "args": {"index": 4}})
            for q in ("binaural", ".*+?[]()", "🎧🔥", "zzzz_no_match_999", "a" * 160, ""):
                steps.append({"cmd": "ui_interact", "args": {"action": "set_text", "type": "EditText", "value": q}})
            steps.append({"cmd": "ui_interact", "args": {"action": "scroll_by", "dy": 1200}})
            steps.append({"cmd": "ui_interact", "args": {"action": "scroll_by", "dy": -1200}})
            steps.append({"cmd": "ui_interact", "args": {"action": "dismiss_dialogs"}})
        steps.append({"cmd": "log_tail", "args": {"lines": 80, "errorsOnly": True}})

        res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=45)
        if res and res[-1]["subCmd"] == "log_tail" and res[-1]["ok"]:
            self._ingest_applog_delta(res[-1]["body"], "V6_ui_monkey_and_audit", "tab thrash + search fuzz")

        self._watch_step_delta("V6_ui_monkey_and_audit", "ui_audit + Q1 geometric scan + search fuzz", check_logcat=True)
        return f"ran ui_audit + Q1 geom scan ({geom_tabs_scanned} tabs, ssim={ssim_tabs_checked}) + search fuzz burst"

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

    # ==================== VECTOR 9: INTERACTIVE UI FREEZE WATCHDOG & LIBRARY/PLAY ORACLE ====================
    def _v9_ui_latency_and_search_oracle(self) -> str:
        """Measure real per-action UI latency (stall >400ms / freeze >1500ms) & audit Library album-search consistency."""
        notes: list[str] = []
        max_ms = 0.0

        # 9a. Time individual tab switches to catch real UI stalls/freezes
        if self.meta.get("uiHostAttached") and self.allow_mutate:
            for tab_idx, tab_name in ((2, "Play"), (4, "Settings"), (0, "Converter"), (2, "Play")):
                t0 = time.monotonic()
                r = self.bridge.cmd("tab_switch", {"index": tab_idx}, tier=1, timeout_sec=20)
                dt_ms = (time.monotonic() - t0) * 1000.0
                # Prefer on-device elapsedMs when available so cloud network RTT never creates false UI-freeze alarms
                dev_ms = float(r.get("elapsedMs") or dt_ms)
                max_ms = max(max_ms, dev_ms)
                lat_issue = classify_ui_latency_ms(dev_ms, f"tab_switch({tab_name})")
                if lat_issue:
                    self.ledger.record(
                        severity=lat_issue["severity"],
                        vector="V9_ui_latency_and_search_oracle",
                        source="ui_latency_watchdog",
                        category=lat_issue["category"],
                        title=lat_issue["message"],
                        repro_step=f"tab_switch index={tab_idx} ({tab_name})",
                        evidence=f"deviceElapsedMs={dev_ms:.0f} wallMs={dt_ms:.0f}",
                    )
            notes.append(f"maxTabMs={max_ms:.0f}")

        # 9b. Stateful Library Album-Search Oracle (catches single-track truncation when track shares album name!)
        lq = self.bridge.cmd("library_query", {"query": "", "limit": 100, "musicOnly": True}, tier=0, timeout_sec=20)
        if lq["ok"] and isinstance(lq["json"], dict):
            all_tracks = list(lq["json"].get("tracks") or [])
            album_counts: dict[str, int] = {}
            title_matches_album: list[str] = []
            for tr in all_tracks:
                alb = str(tr.get("Album") or "").strip()
                tit = str(tr.get("Title") or "").strip()
                if alb:
                    album_counts[alb] = album_counts.get(alb, 0) + 1
                    if tit and tit.lower() == alb.lower() and alb not in title_matches_album:
                        title_matches_album.append(alb)
            # Prioritize title-track albums (e.g. "evermore") or any multi-track album
            multi_albums = [a for a in title_matches_album if album_counts.get(a, 0) >= 2]
            if not multi_albums:
                multi_albums = [a for a, cnt in sorted(album_counts.items(), key=lambda kv: -kv[1]) if cnt >= 2][:2]
            checked_albums = 0
            for alb in multi_albums[:2]:
                checked_albums += 1
                expected_cnt = album_counts[alb]
                qr = self.bridge.cmd("library_query", {"query": alb, "limit": 100, "musicOnly": True}, tier=0, timeout_sec=20)
                if qr["ok"] and isinstance(qr["json"], dict):
                    returned = [
                        t for t in (qr["json"].get("tracks") or [])
                        if str(t.get("Album") or "").strip().lower() == alb.lower()
                    ]
                    if len(returned) < expected_cnt:
                        self.ledger.record(
                            severity="HIGH",
                            vector="V9_ui_latency_and_search_oracle",
                            source="library_search_oracle",
                            category="library_album_search_truncated",
                            title=f"Library search for album '{alb}' returned {len(returned)}/{expected_cnt} album tracks",
                            repro_step=f"library_query query='{alb}'",
                            evidence=f"expected>={expected_cnt} tracks in album '{alb}', got {len(returned)}",
                        )
            notes.append(f"albumsAudited={checked_albums}")

        # 9c. H2 Runtime Telemetry Hook (FrameMetrics, ApplicationExitInfo, AudioTrack/AAudio XRuns)
        if "runtime_telemetry" in getattr(self, "_capabilities", ()):
            rt = self.bridge.cmd("runtime_telemetry", {}, tier=0, timeout_sec=15)
            if rt["ok"] and isinstance(rt["json"], dict):
                fm = rt["json"].get("frameMetrics") or {}
                xr = rt["json"].get("audioXRuns") or {}
                notes.append(
                    f"fm={fm.get('sampledFrames', 0)}/p95={fm.get('p95TotalMs', 0)}ms,"
                    f"xruns={xr.get('audioTrackUnderrunCount', 0)}"
                )

        self._watch_step_delta("V9_ui_latency_and_search_oracle", "UI latency & library search oracle", check_logcat=True)
        return ", ".join(notes) or "UI latency & library oracle OK"

    # ==================== VECTOR 10: SPATIAL TRAJECTORY & IN-RAM HOT-EXEC ORACLE ====================
    def _v10_spatial_trajectory_and_hot_exec(self) -> str:
        """Verify In-RAM hot_exec closed-loop capability AND sample SpatialObjectCortex trajectories across all modes."""
        if not hasattr(self.bridge, "relay"):
            return "skipped (offline mock bridge)"

        # Compile & execute a tiny collectible C# probe purely in running Cavernize RAM (zero APK rebuild!)
        probe_cs = r"""
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Cavern.Neural;
using Cavern.Spatial;

public static class SpatialTrajectoryHotProbe {
    public static string Run() {
        int sr = 48000;
        int len = sr * 24;
        var stems = new List<StereoStemBuffer>();
        string[] names = new[] { "vocals", "bass", "drums", "other" };
        for (int s = 0; s < names.Length; s++) {
            float[] l = new float[len];
            float[] r = new float[len];
            float freq = 180f + s * 320f;
            for (int i = 0; i < len; i++) {
                float env = 0.25f + 0.75f * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * (0.25f + 0.1f * s) * i / sr));
                float v = 0.35f * env * MathF.Sin(2f * MathF.PI * freq * i / sr);
                l[i] = v;
                r[i] = v * 0.9f;
            }
            stems.Add(new StereoStemBuffer(names[s], l, r));
        }

        var modes = new[] { SpatialChoreoMode.Subtle, SpatialChoreoMode.Musical, SpatialChoreoMode.Cinematic, SpatialChoreoMode.Orbit };
        var sb = new StringBuilder();
        sb.Append("{\"ok\":true,\"modes\":{");
        for (int m = 0; m < modes.Length; m++) {
            if (m > 0) sb.Append(',');
            var mode = modes[m];
            var cues = Layer1AudioForensics.Analyze(stems, sr, mode, null);
            var track = SpatialChoreoTrack.Build(names, cues, mode, 24.0, 0.05);
            sb.Append('"').Append(mode.ToString()).Append("\":{\"cueCount\":").Append(cues.Length).Append(",\"stems\":{");
            for (int s = 0; s < names.Length; s++) {
                if (s > 0) sb.Append(',');
                sb.Append('"').Append(names[s]).Append("\":[");
                for (int step = 0; step <= 48; step++) {
                    double t = step * 0.5;
                    Vector3 p;
                    track.EvaluatePositionZeroAlloc(s, t, out p, false);
                    if (step > 0) sb.Append(',');
                    sb.Append("{\"t\":").Append(t.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(",\"x\":").Append(p.X.ToString("0.000", CultureInfo.InvariantCulture))
                      .Append(",\"y\":").Append(p.Y.ToString("0.000", CultureInfo.InvariantCulture))
                      .Append(",\"z\":").Append(p.Z.ToString("0.000", CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append("}}");
        }
        sb.Append("}}");
        return sb.ToString();
    }
}
"""
        try:
            import hot_probe_compiler as hpc
            import secrets as _sec

            comp = hpc.compile_hot_probe(probe_cs, probe_name="SpatialTrajectoryHotProbe", include_android=False)
            if not comp.get("ok"):
                return f"hot_exec compile skipped ({str(comp.get('error') or 'no local dotnet')[:80]})"
            req_id = f"hot_v10_{int(time.time())}_{_sec.token_hex(4)}"
            hot_args = hpc.build_signed_hot_exec_args(
                comp["dllBytes"],
                self.bridge.secret,
                req_id,
                entry_type="SpatialTrajectoryHotProbe",
                timeout_sec=20,
            )
            res = self.bridge.cmd("hot_exec", hot_args, tier=2, timeout_sec=35)
            if not res["ok"] or not isinstance(res["json"], dict):
                self.ledger.record(
                    severity="MEDIUM",
                    vector="V10_spatial_trajectory_and_hot_exec",
                    source="hot_exec",
                    category="hot_exec_probe_failed",
                    title=f"In-RAM hot_exec spatial probe failed: {(res['error'] or res['body'])[:140]}",
                    repro_step="hot_exec SpatialTrajectoryHotProbe",
                    evidence=(res["error"] or res["body"])[:300],
                )
                return "hot_exec probe failed"

            hj = res["json"]
            if hj.get("unloaded") is False:
                self.ledger.record(
                    severity="MEDIUM",
                    vector="V10_spatial_trajectory_and_hot_exec",
                    source="hot_exec",
                    category="hot_exec_alc_not_unloaded",
                    title="Collectible AssemblyLoadContext remained alive after hot_exec probe",
                    repro_step="hot_exec SpatialTrajectoryHotProbe",
                    evidence=res["body"][:260],
                )

            payload_str = str(hj.get("output") or "")
            parsed = json.loads(payload_str) if payload_str.strip().startswith("{") else {}
            modes_map = parsed.get("modes") or {}
            modes_checked = 0
            anomalies_found = 0
            for mode_name, mdata in modes_map.items():
                modes_checked += 1
                for stem_name, samples in (mdata.get("stems") or {}).items():
                    for iss in analyze_spatial_trajectory(samples, mode_name, stem_name):
                        anomalies_found += 1
                        self.ledger.record(
                            severity=iss["severity"],
                            vector="V10_spatial_trajectory_and_hot_exec",
                            source="spatial_trajectory_oracle",
                            category=iss["category"],
                            title=iss["message"],
                            repro_step=f"hot_exec SpatialChoreoTrack.Build({mode_name}, {stem_name})",
                            evidence=f"cues={mdata.get('cueCount')} samples={len(samples)}",
                        )
            return f"hot_exec RAM probe OK ({hj.get('elapsedMs', 0)}ms, unloaded={hj.get('unloaded')}), audited {modes_checked} choreo modes (anomalies={anomalies_found})"
        except Exception as exc:
            return f"hot_exec trajectory probe unavailable ({type(exc).__name__}: {str(exc)[:80]})"

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
        self.meta["restoreDiffKeys"] = []
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
            steps.append({"cmd": "settings_get"})
            if steps:
                res = self.bridge.pipeline(steps, stop_on_error=False, tier=2, timeout_sec=30)
                ok = any(r.get("ok") for r in res)
                if res and res[-1].get("subCmd") == "settings_get" and res[-1].get("ok") and isinstance(res[-1].get("json"), dict):
                    after = res[-1]["json"]
                    diff_keys = [k for k, v in subset.items() if after.get(k) != v]
                    self.meta["restoreDiffKeys"] = diff_keys
                    if diff_keys:
                        ok = False
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
                ("V9_ui_latency_and_search_oracle", self._v9_ui_latency_and_search_oracle),
                ("V10_spatial_trajectory_and_hot_exec", self._v10_spatial_trajectory_and_hot_exec),
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
            "severityCounts": self.ledger.counts_by_severity(active_only=False),
            "activeSeverityCounts": self.ledger.counts_by_severity(active_only=True),
            "originCounts": self.ledger.counts_by_origin(),
            "totalUniqueFindings": len(self.ledger.ranked()),
            "totalActiveFindings": len(self.ledger.active_findings()),
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
    p.add_argument("--golden-dir", default="", help="Optional directory for Q1 Golden SSIM visual diff baselines")
    p.add_argument("--update-goldens", action="store_true", help="Refresh/seed Q1 Golden SSIM baselines in --golden-dir")
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
        hunter = LiveBugHunter(
            bridge,
            mode=args.mode,
            allow_mutate=allow_mutate,
            verbose=not args.quiet,
            out_md=args.out_md,
            out_json=args.out_json,
            golden_dir=args.golden_dir,
            update_goldens=args.update_goldens,
        )
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
