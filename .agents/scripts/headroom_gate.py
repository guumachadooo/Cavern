#!/usr/bin/env python3
"""
CavernizeAndroid W60.1 True 50% Headroom & 6GB-Class PSS Gate (`headroom_gate.py`).

Pure stdlib deterministic evaluator enforcing BP_QA_V3_CHAOS_HEADROOM_AND_SCARLETT_DELTA budgets:
- RT Audio (`W60-RT-CPU-04`): 4-site exact per-block CPU P95 <= 50.0%, native C++ callback CPU P95 <= 50.0%,
  underruns == 0, zeroFrameStarveCount == 0, rtGcCount == 0, active playback blocks > 0.
- UI 120Hz (`W60-UI-05`): FrameMetrics totalFrames >= 15, cpuP95Ms <= 4.16 ms, totalP95Ms <= 8.33 ms,
  frozenFrames == 0, Simple transition <= 25 ms, HeavyModal <= 80 ms.
- 6GB-Class Memory & Leaks (`W60-MEM-06`): idle PSS <= 550 MB, active ONNX PSS <= 1280 MB, fdDelta <= 8.
- Mutual-Exclusion Oracle (optional `mutexSnapshot`): invariantViolations == [].
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class HeadroomBudget:
    rt_cpu_p95_limit_pct: float = 50.0
    native_cb_cpu_p95_limit_pct: float = 50.0
    max_render_lock_contention_ms: float = 4.0
    ui_min_frames: int = 15
    ui_cpu_p95_limit_ms: float = 4.16
    ui_total_p95_limit_ms: float = 8.33
    ui_simple_limit_ms: float = 25.0
    ui_heavy_modal_limit_ms: float = 80.0
    pss_idle_limit_mb: float = 550.0
    pss_onnx_limit_mb: float = 1280.0
    fd_delta_limit: int = 8


@dataclass
class HeadroomViolation:
    kill_id: str
    domain: str
    metric: str
    actual: Any
    limit: Any
    message: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "killId": self.kill_id,
            "domain": self.domain,
            "metric": self.metric,
            "actual": self.actual,
            "limit": self.limit,
            "message": self.message,
        }


@dataclass
class HeadroomEvaluation:
    passed: bool
    mode: str
    violations: list[HeadroomViolation] = field(default_factory=list)
    metrics: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "passed": self.passed,
            "mode": self.mode,
            "violationCount": len(self.violations),
            "violations": [v.to_dict() for v in self.violations],
            "metrics": self.metrics,
        }


def _as_float(val: Any, default: float = 0.0) -> float:
    try:
        if val is None:
            return default
        f = float(val)
        return f if f == f else default
    except (TypeError, ValueError):
        return default


def _as_int(val: Any, default: int = 0) -> int:
    try:
        if val is None:
            return default
        return int(val)
    except (TypeError, ValueError):
        return default


def evaluate_headroom(
    snapshot: dict[str, Any],
    *,
    mode: str = "onnx",
    require_rt_active: bool = True,
    require_ui_frames: bool = True,
    budget: HeadroomBudget = HeadroomBudget(),
) -> HeadroomEvaluation:
    """Evaluates a combined runtime_telemetry / soak / mutex snapshot against strict 50% headroom budgets."""
    violations: list[HeadroomViolation] = []
    mode_norm = (mode or "onnx").strip().lower()
    if mode_norm not in ("idle", "onnx", "active_onnx"):
        mode_norm = "onnx"

    rt = snapshot.get("rtAudioHeadroom") or {}
    xruns = snapshot.get("audioXRuns") or {}
    fm = snapshot.get("frameMetrics") or {}
    health = snapshot.get("processHealth") or {}
    ui_lat = snapshot.get("uiTransitions") or {}
    mutex = snapshot.get("mutexSnapshot")
    soak_base = snapshot.get("soakBaseline") or {}

    # 1. RT Audio 50% Headroom (`W60-RT-CPU-04`)
    total_blocks = _as_int(rt.get("totalRenderedBlocks"), 0)
    rt_cpu_p95 = _as_float(rt.get("cpuP95Pct"), 0.0)
    starve_count = _as_int(rt.get("zeroFrameStarveCount"), 0)
    rt_gc_count = _as_int(rt.get("rtGcCount"), 0)
    lock_contention_ms = _as_float(rt.get("renderLockContentionMs"), 0.0)
    total_underruns = _as_int(
        xruns.get("totalOutputUnderruns"),
        _as_int(xruns.get("aaudioOutputXRuns"), 0) + _as_int(xruns.get("audioTrackUnderruns"), 0),
    )
    native_cb_cpu_p95 = _as_float(xruns.get("nativeCallbackCpuP95Pct"), 0.0)
    native_running = bool(xruns.get("nativeEngineRunning", False))

    if require_rt_active and total_blocks <= 0 and starve_count == 0:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "totalRenderedBlocks",
                total_blocks,
                "> 0",
                "No rendered RT audio blocks observed during active RT gate (prevents silent false green).",
            )
        )
    if rt_cpu_p95 > budget.rt_cpu_p95_limit_pct:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "cpuP95Pct",
                rt_cpu_p95,
                budget.rt_cpu_p95_limit_pct,
                f"RT audio block CPU P95 {rt_cpu_p95:.2f}% exceeds {budget.rt_cpu_p95_limit_pct:.1f}% headroom cap.",
            )
        )
    if native_running and native_cb_cpu_p95 > budget.native_cb_cpu_p95_limit_pct:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "nativeCallbackCpuP95Pct",
                native_cb_cpu_p95,
                budget.native_cb_cpu_p95_limit_pct,
                f"Native C++ RT callback CPU P95 {native_cb_cpu_p95:.2f}% exceeds {budget.native_cb_cpu_p95_limit_pct:.1f}% cap.",
            )
        )
    if total_underruns > 0:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "totalOutputUnderruns",
                total_underruns,
                0,
                f"Audio output underruns detected ({total_underruns} > 0).",
            )
        )
    if starve_count > 0:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "zeroFrameStarveCount",
                starve_count,
                0,
                f"Decoder/render 0-frame starvation detected ({starve_count} blocks).",
            )
        )
    if rt_gc_count > 0:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "rtGcCount",
                rt_gc_count,
                0,
                f"GC collections occurred on RT audio render path ({rt_gc_count} > 0).",
            )
        )
    if lock_contention_ms > budget.max_render_lock_contention_ms:
        violations.append(
            HeadroomViolation(
                "W60-RT-CPU-04",
                "rt_audio",
                "renderLockContentionMs",
                lock_contention_ms,
                budget.max_render_lock_contention_ms,
                f"RT render lock contention {lock_contention_ms:.2f} ms exceeds {budget.max_render_lock_contention_ms:.2f} ms.",
            )
        )

    # 2. UI 120Hz 50% Headroom (`W60-UI-05`)
    ui_frames = _as_int(fm.get("totalFrames"), 0)
    ui_cpu_p95 = _as_float(fm.get("cpuP95Ms"), 0.0)
    ui_total_p95 = _as_float(fm.get("p95Ms"), 0.0)
    ui_frozen = _as_int(fm.get("frozenFrames"), 0)
    simple_ms = _as_float(ui_lat.get("simpleMaxMs"), 0.0)
    heavy_modal_ms = _as_float(ui_lat.get("heavyModalMaxMs"), 0.0)

    if require_ui_frames and ui_frames < budget.ui_min_frames:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "totalFrames",
                ui_frames,
                budget.ui_min_frames,
                f"Insufficient FrameMetrics samples ({ui_frames} < {budget.ui_min_frames}); cannot certify 120 Hz headroom.",
            )
        )
    if ui_frames > 0 and ui_cpu_p95 > budget.ui_cpu_p95_limit_ms:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "cpuP95Ms",
                ui_cpu_p95,
                budget.ui_cpu_p95_limit_ms,
                f"UI CPU P95 {ui_cpu_p95:.2f} ms exceeds {budget.ui_cpu_p95_limit_ms:.2f} ms (50% of 8.33 ms 120 Hz budget).",
            )
        )
    if ui_frames > 0 and ui_total_p95 > budget.ui_total_p95_limit_ms:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "p95Ms",
                ui_total_p95,
                budget.ui_total_p95_limit_ms,
                f"UI total frame P95 {ui_total_p95:.2f} ms exceeds {budget.ui_total_p95_limit_ms:.2f} ms 120 Hz frame budget.",
            )
        )
    if ui_frozen > 0:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "frozenFrames",
                ui_frozen,
                0,
                f"Frozen UI frames detected ({ui_frozen} > 0).",
            )
        )
    if simple_ms > budget.ui_simple_limit_ms:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "simpleMaxMs",
                simple_ms,
                budget.ui_simple_limit_ms,
                f"Simple UI transition {simple_ms:.1f} ms exceeds {budget.ui_simple_limit_ms:.1f} ms budget.",
            )
        )
    if heavy_modal_ms > budget.ui_heavy_modal_limit_ms:
        violations.append(
            HeadroomViolation(
                "W60-UI-05",
                "ui_120hz",
                "heavyModalMaxMs",
                heavy_modal_ms,
                budget.ui_heavy_modal_limit_ms,
                f"Heavy modal open {heavy_modal_ms:.1f} ms exceeds {budget.ui_heavy_modal_limit_ms:.1f} ms budget.",
            )
        )

    # 3. 6GB-Class PSS & FD Ceilings (`W60-MEM-06`)
    pss_mb = _as_float(health.get("nativePssMb"), _as_float(snapshot.get("nativePssMb"), -1.0))
    pss_limit = budget.pss_idle_limit_mb if mode_norm == "idle" else budget.pss_onnx_limit_mb
    if pss_mb > pss_limit:
        violations.append(
            HeadroomViolation(
                "W60-MEM-06",
                "memory_6gb",
                "nativePssMb",
                pss_mb,
                pss_limit,
                f"Process Total PSS {pss_mb:.1f} MB exceeds 6GB-class {mode_norm} ceiling ({pss_limit:.1f} MB).",
            )
        )
    fd_now = _as_int(health.get("openFdCount"), _as_int(snapshot.get("openFdCount"), -1))
    fd_base = _as_int(soak_base.get("openFdCount"), -1)
    fd_delta = (fd_now - fd_base) if (fd_now >= 0 and fd_base >= 0) else _as_int(snapshot.get("fdDelta"), 0)
    if fd_delta > budget.fd_delta_limit:
        violations.append(
            HeadroomViolation(
                "W60-MEM-06",
                "memory_6gb",
                "fdDelta",
                fd_delta,
                budget.fd_delta_limit,
                f"Open file descriptor delta +{fd_delta} exceeds leak ceiling (+{budget.fd_delta_limit}).",
            )
        )

    # 4. Atomic Mutual-Exclusion Oracle (when present)
    mutex_violations: list[str] = []
    if isinstance(mutex, dict):
        raw_v = mutex.get("invariantViolations")
        if isinstance(raw_v, list):
            mutex_violations = [str(x) for x in raw_v if str(x).strip()]
        if mutex_violations:
            violations.append(
                HeadroomViolation(
                    "W59-MUTEX-01",
                    "mutex_oracle",
                    "invariantViolations",
                    mutex_violations,
                    [],
                    f"Mutual-exclusion invariant violation(s): {', '.join(mutex_violations)}",
                )
            )

    metrics = {
        "rtCpuP95Pct": rt_cpu_p95,
        "nativeCallbackCpuP95Pct": native_cb_cpu_p95,
        "totalOutputUnderruns": total_underruns,
        "zeroFrameStarveCount": starve_count,
        "rtGcCount": rt_gc_count,
        "uiTotalFrames": ui_frames,
        "uiCpuP95Ms": ui_cpu_p95,
        "uiTotalP95Ms": ui_total_p95,
        "pssMb": pss_mb,
        "pssLimitMb": pss_limit,
        "fdDelta": fd_delta,
        "mutexViolationCount": len(mutex_violations),
        "uiCpuLimitMs": budget.ui_cpu_p95_limit_ms,
    }
    return HeadroomEvaluation(
        passed=len(violations) == 0,
        mode=mode_norm,
        violations=violations,
        metrics=metrics,
    )


def _baseline_passing_snapshot() -> dict[str, Any]:
    return {
        "processHealth": {"nativePssMb": 412.5, "openFdCount": 142},
        "soakBaseline": {"openFdCount": 140},
        "audioXRuns": {
            "nativeEngineRunning": True,
            "totalOutputUnderruns": 0,
            "nativeCallbackCpuP95Pct": 11.4,
        },
        "rtAudioHeadroom": {
            "totalRenderedBlocks": 240,
            "cpuP95Pct": 24.8,
            "zeroFrameStarveCount": 0,
            "rtGcCount": 0,
            "renderLockContentionMs": 0.4,
        },
        "frameMetrics": {
            "totalFrames": 48,
            "cpuP95Ms": 3.12,
            "p95Ms": 6.40,
            "frozenFrames": 0,
        },
        "uiTransitions": {"simpleMaxMs": 18.0, "heavyModalMaxMs": 62.0},
        "mutexSnapshot": {"ok": True, "invariantViolations": []},
    }


def run_self_test() -> int:
    """Executes QAS Q2 fault-injection verification with zero false-green passes."""
    clean = evaluate_headroom(_baseline_passing_snapshot(), mode="idle")
    assert clean.passed, f"Baseline clean snapshot unexpectedly failed: {clean.to_dict()}"

    false_greens = 0
    fault_cases: list[tuple[str, str, dict[str, Any], str]] = []

    # Fault 1a: >50% RT CPU
    s1 = _baseline_passing_snapshot()
    s1["rtAudioHeadroom"]["cpuP95Pct"] = 58.2
    fault_cases.append(("rt_cpu_over_50pct", "W60-RT-CPU-04", s1, "onnx"))

    # Fault 1b: 0-frame decoder starvation (faking low CPU)
    s1b = _baseline_passing_snapshot()
    s1b["rtAudioHeadroom"]["cpuP95Pct"] = 0.0
    s1b["rtAudioHeadroom"]["zeroFrameStarveCount"] = 3
    fault_cases.append(("rt_zero_frame_starve", "W60-RT-CPU-04", s1b, "onnx"))

    # Fault 1c: Native C++ callback CPU >50%
    s1c = _baseline_passing_snapshot()
    s1c["audioXRuns"]["nativeCallbackCpuP95Pct"] = 61.5
    fault_cases.append(("native_cb_over_50pct", "W60-RT-CPU-04", s1c, "onnx"))

    # Fault 2a: <15 UI frames
    s2a = _baseline_passing_snapshot()
    s2a["frameMetrics"]["totalFrames"] = 8
    fault_cases.append(("ui_lt_15_frames", "W60-UI-05", s2a, "onnx"))

    # Fault 2b: UI CPU P95 > 4.16 ms
    s2b = _baseline_passing_snapshot()
    s2b["frameMetrics"]["cpuP95Ms"] = 5.42
    fault_cases.append(("ui_cpu_over_4_16ms", "W60-UI-05", s2b, "onnx"))

    # Fault 3: 1400 MB PSS (> 1280 MB 6GB ONNX ceiling)
    s3 = _baseline_passing_snapshot()
    s3["processHealth"]["nativePssMb"] = 1400.0
    fault_cases.append(("pss_1400mb_over_1280mb", "W60-MEM-06", s3, "onnx"))

    # Fault 4: Mutual-exclusion violation
    s4 = _baseline_passing_snapshot()
    s4["mutexSnapshot"]["invariantViolations"] = ["speaker_ambience_not_bypassed"]
    fault_cases.append(("mutex_violation", "W59-MUTEX-01", s4, "onnx"))

    for label, expected_kill, payload, mode in fault_cases:
        res = evaluate_headroom(payload, mode=mode)
        if res.passed:
            false_greens += 1
            print(f"[FAIL] {label}: FALSE GREEN!", file=sys.stderr)
        else:
            kills = {v.kill_id for v in res.violations}
            assert expected_kill in kills, f"{label}: expected {expected_kill}, got {kills}"

    assert false_greens == 0, f"Detected {false_greens} false green pass(es)"
    print(
        f"[PASS] headroom_gate self-test: baseline PASS + {len(fault_cases)}/{len(fault_cases)} "
        f"faults caught (false_green_passes=0, ui_cpu_limit_ms={HeadroomBudget().ui_cpu_p95_limit_ms})"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="CavernizeAndroid W60.1 50% Headroom & 6GB PSS Gate")
    parser.add_argument("--json", dest="json_path", help="Path to telemetry JSON file (or '-' for stdin)")
    parser.add_argument("--mode", choices=("idle", "onnx"), default="onnx", help="PSS budget tier")
    parser.add_argument("--no-require-rt", action="store_true", help="Allow 0 RT blocks (e.g. paused idle check)")
    parser.add_argument("--no-require-ui", action="store_true", help="Skip totalFrames >= 15 check")
    parser.add_argument("--self-test", action="store_true", help="Run QAS Q2 fault-injection self-test")
    args = parser.parse_args(argv)

    if args.self_test:
        return run_self_test()

    if not args.json_path:
        parser.error("Provide --json <file|-> or --self-test")

    raw = sys.stdin.read() if args.json_path == "-" else Path(args.json_path).read_text(encoding="utf-8")
    payload = json.loads(raw)
    result = evaluate_headroom(
        payload,
        mode=args.mode,
        require_rt_active=not args.no_require_rt,
        require_ui_frames=not args.no_require_ui,
    )
    print(json.dumps(result.to_dict(), indent=2))
    return 0 if result.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
