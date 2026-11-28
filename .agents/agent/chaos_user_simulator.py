#!/usr/bin/env python3
"""
CavernizeAndroid W59.2 Deterministic Chaos User Simulator (`chaos_user_simulator.py`).

Zero +1L growth on `cavern_bug_hunter.py` (`4982L` ratchet preserved).
Drives deterministic `--seed` hostile FSM transitions across the fixed S25U demo pair
(`01. I Knew It, I Knew You.flac` + `E-AC3-JOC.m4a`) using strictly Tier-0/Tier-1 bridge commands
(`settings_get`, `settings_set`, `open_file`, `tab_switch`, `playback_control`, `mutex_snapshot`,
`runtime_telemetry`, `soak_snapshot` — zero Tier-2 `reflect_*` dependency).

Hard Safety Invariants:
- `W59-CREDIT-01`: Arms crash-safe `QaMvsepBlockUntilUtcMs` lease BEFORE any Chaos step,
  verifies `qaMvsepBlockActive == True && canUseMvsepCloud == False`, and enforces `mvsepHttpRequests == 0`.
- `W59-REBOOT-02`: Reverts `NeuralExecutionBackendIndex` to `0` (Offline) BEFORE disarming lease in `finally`.
- `W59-REPRO-03`: Same `--seed` produces bit-identical action trace (`seed_replay_diffs == 0`) with
  awaited `open_file` settlement barrier (`!isOperationRunning && loadedFile` matches target).
"""
from __future__ import annotations

import argparse
import json
import random
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol

SCRIPTS_DIR = Path(__file__).resolve().parent.parent / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import headroom_gate as hg  # noqa: E402

DEFAULT_FLAC_DEMO = "/sdcard/Music/01. I Knew It, I Knew You.flac"
DEFAULT_ATMOS_DEMO = "/sdcard/Music/E-AC3-JOC.m4a"
DEFAULT_LEASE_SECONDS = 1800  # 30-minute crash-safe SharedPreferences TTL lease

# Strictly Tier-0/Tier-1 commands allowed by Chaos FSM (no Tier-2 reflect_invoke/reflect_get)
ALLOWED_CHAOS_COMMANDS = frozenset({
    "ping",
    "app_state",
    "settings_get",
    "settings_set",
    "open_file",
    "tab_switch",
    "playback_control",
    "mutex_snapshot",
    "runtime_telemetry",
    "soak_snapshot",
})


class ChaosBridgeProtocol(Protocol):
    def cmd(self, name: str, args: dict[str, Any] | None = None, tier: int = 1, timeout_sec: int = 30) -> dict[str, Any]:
        ...


@dataclass
class ChaosStepRecord:
    step_index: int
    action: str
    cmd: str
    args: dict[str, Any]
    ok: bool
    mutex_violations: list[str] = field(default_factory=list)
    mvsep_http_requests: int = 0
    note: str = ""

    def signature(self) -> dict[str, Any]:
        return {
            "step": self.step_index,
            "action": self.action,
            "cmd": self.cmd,
            "args": self.args,
        }


class SimulatedChaosBridge:
    """Deterministic offline CavernizeAndroid Tier-1 simulator for unit testing, dual-seed replay & crash-reboot checks."""

    def __init__(
        self,
        *,
        flac_path: str = DEFAULT_FLAC_DEMO,
        atmos_path: str = DEFAULT_ATMOS_DEMO,
        inject_mutex_fault: bool = False,
        inject_mvsep_leak: bool = False,
        inject_open_race: bool = False,
    ) -> None:
        self.flac_path = flac_path
        self.atmos_path = atmos_path
        self.inject_mutex_fault = inject_mutex_fault
        self.inject_mvsep_leak = inject_mvsep_leak
        self.inject_open_race = inject_open_race
        self.now_ms = 1_770_000_000_000
        self.active_tab = 0
        self.loaded_file = flac_path
        self.is_playing = True
        self.is_operation_running = False
        self.mvsep_http_requests = 0
        self.mvsep_blocked_requests = 0
        self.history: list[tuple[str, dict[str, Any]]] = []
        self.settings: dict[str, Any] = {
            "HeadphoneVirtualizer": True,
            "TransauralXtcEnabled": False,
            "IsmRoomEnabled": True,
            "MatrixUpmixing": False,
            "CavernizeUpmix": False,
            "AtmosAwareUpmix": True,
            "NeuralSpatialUpmixModeIndex": 0,
            "NeuralExecutionBackendIndex": 0,
            "QaMvsepBlockUntilUtcMs": 0,
            "QaMvsepDirtyGuard": False,
            "MvsepApiToken": "qa-synthetic-token",
        }

    def simulate_crash_and_relaunch(self) -> dict[str, Any]:
        """Simulates LMKD process death + fresh process boot enforcing AppSettingsStore.EnforceQaMvsepBootLeaseGuard."""
        # RAM-only state resets; SharedPreferences persists self.settings!
        self.is_playing = False
        self.is_operation_running = False
        block_until = int(self.settings.get("QaMvsepBlockUntilUtcMs", 0) or 0)
        dirty_guard = bool(self.settings.get("QaMvsepDirtyGuard", False))
        backend_idx = int(self.settings.get("NeuralExecutionBackendIndex", 0) or 0)
        reverted = False
        if (block_until > self.now_ms or dirty_guard) and backend_idx == 1:
            self.settings["NeuralExecutionBackendIndex"] = 0
            self.settings["QaMvsepDirtyGuard"] = False
            reverted = True
        block_active = block_until > self.now_ms or dirty_guard
        return {
            "revertedBackendToOffline": reverted,
            "qaMvsepBlockActive": block_active,
            "backendIndexAfterBoot": self.settings["NeuralExecutionBackendIndex"],
        }

    def _is_qa_block_active(self) -> bool:
        return int(self.settings.get("QaMvsepBlockUntilUtcMs", 0) or 0) > self.now_ms or bool(
            self.settings.get("QaMvsepDirtyGuard", False)
        )

    def _compute_mutex_snapshot(self) -> dict[str, Any]:
        hp = bool(self.settings.get("HeadphoneVirtualizer", False))
        xtc = bool(self.settings.get("TransauralXtcEnabled", False))
        ism = bool(self.settings.get("IsmRoomEnabled", False))
        upmix = bool(self.settings.get("MatrixUpmixing", False))
        atmos_file = self.loaded_file.lower().endswith(".m4a")
        qa_active = self._is_qa_block_active()
        backend_idx = int(self.settings.get("NeuralExecutionBackendIndex", 0) or 0)
        can_mvsep = (not qa_active) and backend_idx == 1 and bool(self.settings.get("MvsepApiToken"))
        # Mirror Cavernize mutual-exclusion enforcement (Speaker XTC bypasses ambience & BRIR; HP disables XTC)
        effective_xtc = xtc and (not hp)
        effective_ism = ism and (not effective_xtc)
        effective_upmix_hidden = atmos_file

        violations: list[str] = []
        if effective_xtc and effective_ism:
            violations.append("speaker_ambience_not_bypassed")
        if hp and effective_xtc:
            violations.append("xtc_active_on_headphones")
        if qa_active and can_mvsep:
            violations.append("mvsep_cloud_unblocked_under_qa")
        if self.inject_mutex_fault:
            violations.append("speaker_ambience_not_bypassed")

        return {
            "ok": True,
            "activeTab": self.active_tab,
            "loadedFile": self.loaded_file,
            "isOperationRunning": self.is_operation_running,
            "isPlaying": self.is_playing,
            "headphoneVirtualizer": hp,
            "transauralXtcActive": effective_xtc,
            "ismRoomActive": effective_ism,
            "atmosChannelUpmixHidden": effective_upmix_hidden,
            "neuralExecutionBackendIndex": backend_idx,
            "qaMvsepBlockActive": qa_active,
            "qaMvsepBlockUntilUtcMs": int(self.settings.get("QaMvsepBlockUntilUtcMs", 0) or 0),
            "canUseMvsepCloud": can_mvsep,
            "mvsepQaBlockedRequestCount": self.mvsep_blocked_requests,
            "mvsepHttpRequests": self.mvsep_http_requests,
            "invariantViolations": violations,
        }

    def cmd(self, name: str, args: dict[str, Any] | None = None, tier: int = 1, timeout_sec: int = 30) -> dict[str, Any]:
        if name not in ALLOWED_CHAOS_COMMANDS:
            return {"ok": False, "error": f"Disallowed tier/command in Chaos simulator: {name}", "json": None}
        a = dict(args or {})
        self.history.append((name, a))

        if name == "settings_get":
            return {"ok": True, "json": dict(self.settings), "error": ""}
        if name == "settings_set":
            updates = dict(a.get("values") or {})
            if "key" in a:
                updates[str(a["key"])] = a.get("value")
            for k, v in updates.items():
                if k == "QaMvsepBlockUntilUtcMs":
                    new_ts = int(v or 0)
                    # Mirror TryDisarmQaCloudBlock: refuse disarm while backend == 1
                    if new_ts <= 0 and int(self.settings.get("NeuralExecutionBackendIndex", 0) or 0) == 1:
                        return {"ok": False, "error": "Refused QA block disarm while NeuralExecutionBackendIndex==1", "json": {"disarmed": False}}
                    self.settings[k] = new_ts
                    if new_ts <= 0:
                        self.settings["QaMvsepDirtyGuard"] = False
                elif k == "NeuralExecutionBackendIndex":
                    self.settings[k] = int(v or 0)
                    if int(v or 0) == 1 and self._is_qa_block_active():
                        self.settings["QaMvsepDirtyGuard"] = True
                        if self.inject_mvsep_leak:
                            self.mvsep_http_requests += 1
                        else:
                            self.mvsep_blocked_requests += 1
                else:
                    self.settings[k] = v
            return {"ok": True, "json": {"applied": sorted(updates.keys())}, "error": ""}
        if name == "open_file":
            target = str(a.get("path") or "")
            if self.inject_open_race:
                # Simulate stale metadata race where loadedFile didn't update
                return {"ok": True, "json": {"loadedFile": self.loaded_file, "isOperationRunning": True}, "error": ""}
            self.loaded_file = target
            self.is_operation_running = False
            return {"ok": True, "json": {"loadedFile": self.loaded_file, "isOperationRunning": False}, "error": ""}
        if name == "tab_switch":
            self.active_tab = int(a.get("index", 0))
            return {"ok": True, "json": {"activeTab": self.active_tab}, "error": ""}
        if name == "playback_control":
            action = str(a.get("action") or "")
            if action == "play":
                self.is_playing = True
            elif action == "pause":
                self.is_playing = False
            return {"ok": True, "json": {"action": action, "isPlaying": self.is_playing}, "error": ""}
        if name == "mutex_snapshot":
            return {"ok": True, "json": self._compute_mutex_snapshot(), "error": ""}
        if name == "runtime_telemetry":
            return {
                "ok": True,
                "json": {
                    "processHealth": {"nativePssMb": 420.0, "openFdCount": 144},
                    "audioXRuns": {"nativeEngineRunning": True, "totalOutputUnderruns": 0, "nativeCallbackCpuP95Pct": 12.1},
                    "rtAudioHeadroom": {
                        "totalRenderedBlocks": 180,
                        "cpuP95Pct": 26.5,
                        "zeroFrameStarveCount": 0,
                        "rtGcCount": 0,
                        "renderLockContentionMs": 0.35,
                    },
                    "frameMetrics": {"totalFrames": 42, "cpuP95Ms": 3.05, "p95Ms": 6.20, "frozenFrames": 0},
                },
                "error": "",
            }
        if name == "soak_snapshot":
            return {"ok": True, "json": {"nativePssMb": 420.0, "openFdCount": 144, "activeDialogs": 0}, "error": ""}
        if name == "app_state":
            return {
                "ok": True,
                "json": {"activeTab": self.active_tab, "loadedFile": self.loaded_file, "isPlaying": self.is_playing, "isOperationRunning": self.is_operation_running},
                "error": "",
            }
        return {"ok": True, "json": {"ok": True}, "error": ""}


def build_deterministic_chaos_plan(
    seed: int,
    steps: int = 24,
    *,
    flac_path: str = DEFAULT_FLAC_DEMO,
    atmos_path: str = DEFAULT_ATMOS_DEMO,
) -> list[tuple[str, str, dict[str, Any]]]:
    """Generates a 100% deterministic hostile FSM action plan for a given seed."""
    rng = random.Random(int(seed))
    tracks = [flac_path, atmos_path]
    toggle_keys = [
        "HeadphoneVirtualizer",
        "TransauralXtcEnabled",
        "IsmRoomEnabled",
        "MatrixUpmixing",
        "CavernizeUpmix",
        "AtmosAwareUpmix",
        "NeuralSpatialUpmixModeIndex",
    ]
    plan: list[tuple[str, str, dict[str, Any]]] = []
    # Step 0: always start by loading FLAC deterministically with autoPlay=True so RT blocks render
    plan.append(("open_demo_flac", "open_file", {"path": flac_path, "autoPlay": True}))
    for idx in range(1, max(2, steps)):
        roll = rng.random()
        if idx == 3 or (idx % 9 == 0):
            # Hostile MVSEP cloud backend toggle probe — must incur 0 HTTP egress!
            plan.append(("mvsep_tripwire_cloud_attempt", "settings_set", {"key": "NeuralExecutionBackendIndex", "value": 1}))
        elif idx == 4 or (idx % 9 == 1):
            plan.append(("mvsep_revert_offline", "settings_set", {"key": "NeuralExecutionBackendIndex", "value": 0}))
        elif roll < 0.22:
            target = tracks[rng.randrange(len(tracks))]
            plan.append(("open_demo_track", "open_file", {"path": target, "autoPlay": True}))
        elif roll < 0.44:
            tab = rng.choice([0, 1, 2])
            plan.append(("hostile_tab_switch", "tab_switch", {"index": tab}))
        elif roll < 0.66:
            action = rng.choice(["play", "pause", "seek"])
            args: dict[str, Any] = {"action": action}
            if action == "seek":
                args["value"] = round(rng.uniform(5.0, 90.0), 1)
            plan.append(("hostile_playback", "playback_control", args))
        else:
            # Hostile mutual-exclusion combo toggle storm (2 keys at once)
            k1, k2 = rng.sample(toggle_keys, 2)
            vals: dict[str, Any] = {}
            for k in (k1, k2):
                vals[k] = rng.choice([0, 1, 2]) if k == "NeuralSpatialUpmixModeIndex" else rng.choice([True, False])
            plan.append(("hostile_toggle_storm", "settings_set", {"values": vals}))
    return plan


def run_chaos_simulation(
    bridge: ChaosBridgeProtocol,
    *,
    seed: int = 42,
    steps: int = 24,
    flac_path: str = DEFAULT_FLAC_DEMO,
    atmos_path: str = DEFAULT_ATMOS_DEMO,
    lease_seconds: int = DEFAULT_LEASE_SECONDS,
    now_utc_ms: int | None = None,
    headroom_mode: str = "onnx",
) -> dict[str, Any]:
    """Executes the deterministic Chaos FSM with mandatory crash-safe MVSEP lease arming & restoration."""
    start_ms = int(now_utc_ms if now_utc_ms is not None else time.time() * 1000)
    lease_until_ms = start_ms + max(60, int(lease_seconds)) * 1000
    failures: list[dict[str, Any]] = []
    trace: list[ChaosStepRecord] = []
    baseline_settings: dict[str, Any] = {}
    soak_base: dict[str, Any] = {}
    mvsep_http_total = 0
    open_races = 0
    mutex_violation_total = 0

    # 1. Capture baseline settings + soak baseline
    base_res = bridge.cmd("settings_get", {}, tier=1)
    if base_res.get("ok") and isinstance(base_res.get("json"), dict):
        baseline_settings = dict(base_res["json"])
    soak_res = bridge.cmd("soak_snapshot", {"forceGc": False}, tier=0)
    if soak_res.get("ok") and isinstance(soak_res.get("json"), dict):
        soak_base = dict(soak_res["json"])

    # 2. MANDATORY W59-CREDIT-01: Arm crash-safe SharedPrefs QA MVSEP TTL lease BEFORE any Chaos step
    arm_res = bridge.cmd("settings_set", {"key": "QaMvsepBlockUntilUtcMs", "value": lease_until_ms}, tier=1)
    verify_arm = bridge.cmd("mutex_snapshot", {}, tier=0)
    arm_json = verify_arm.get("json") if isinstance(verify_arm.get("json"), dict) else {}
    if not arm_res.get("ok") or not arm_json.get("qaMvsepBlockActive") or arm_json.get("canUseMvsepCloud"):
        return {
            "ok": False,
            "passed": False,
            "seed": seed,
            "abortedAt": "pre_flight_mvsep_lease",
            "failures": [
                {
                    "killId": "W59-CREDIT-01",
                    "message": "Failed to arm crash-safe QA MVSEP block lease before Chaos start.",
                    "detail": arm_json,
                }
            ],
            "mvsepHttpRequests": int(arm_json.get("mvsepHttpRequests", 0) or 0),
            "trace": [],
        }

    plan = build_deterministic_chaos_plan(seed, steps, flac_path=flac_path, atmos_path=atmos_path)
    last_telemetry: dict[str, Any] = {}
    last_mutex: dict[str, Any] = arm_json

    try:
        for idx, (action_name, cmd_name, cmd_args) in enumerate(plan):
            resp = bridge.cmd(cmd_name, cmd_args, tier=1)
            ok = bool(resp.get("ok"))
            resp_json = resp.get("json") if isinstance(resp.get("json"), dict) else {}

            # W59-REPRO-03: verify awaited open_file settlement barrier
            if cmd_name == "open_file":
                expected_path = str(cmd_args.get("path") or "")
                loaded = str(resp_json.get("loadedFile") or "")
                still_busy = bool(resp_json.get("isOperationRunning", False))
                if still_busy or (loaded and expected_path and Path(loaded).name != Path(expected_path).name):
                    open_races += 1
                    failures.append({
                        "killId": "W59-REPRO-03",
                        "step": idx,
                        "message": f"OpenFileAsync race: expected '{Path(expected_path).name}', got '{loaded}' (busy={still_busy})",
                    })

            # Atomic mutual-exclusion + MVSEP 0-credit oracle check after every step
            ms_resp = bridge.cmd("mutex_snapshot", {}, tier=0)
            ms_json = ms_resp.get("json") if isinstance(ms_resp.get("json"), dict) else {}
            last_mutex = ms_json
            m_viols = [str(v) for v in (ms_json.get("invariantViolations") or [])]
            http_reqs = int(ms_json.get("mvsepHttpRequests", 0) or 0)
            mvsep_http_total = max(mvsep_http_total, http_reqs)

            if m_viols:
                mutex_violation_total += len(m_viols)
                failures.append({
                    "killId": "W59-MUTEX-01",
                    "step": idx,
                    "action": action_name,
                    "violations": m_viols,
                })
            if http_reqs > 0 or bool(ms_json.get("canUseMvsepCloud")):
                failures.append({
                    "killId": "W59-CREDIT-01",
                    "step": idx,
                    "action": action_name,
                    "mvsepHttpRequests": http_reqs,
                    "message": "MVSEP HTTP egress or unblocked cloud flag detected during Chaos!",
                })
                # Hard kill-switch: abort immediately on any MVSEP leak
                trace.append(ChaosStepRecord(idx, action_name, cmd_name, cmd_args, False, m_viols, http_reqs, "ABORT_MVSEP_LEAK"))
                break

            trace.append(ChaosStepRecord(idx, action_name, cmd_name, cmd_args, ok, m_viols, http_reqs))

        # Final 50% Headroom + 6GB-PSS telemetry evaluation
        tel_resp = bridge.cmd("runtime_telemetry", {}, tier=0)
        if tel_resp.get("ok") and isinstance(tel_resp.get("json"), dict):
            last_telemetry = dict(tel_resp["json"])
    finally:
        # W59-REBOOT-02: Revert backend to 0 (Offline) FIRST, restore Chaos-mutated baseline keys, THEN disarm QA lease
        chaos_mutable_keys = {
            "HeadphoneVirtualizer",
            "TransauralXtcEnabled",
            "IsmRoomEnabled",
            "MatrixUpmixing",
            "CavernizeUpmix",
            "AtmosAwareUpmix",
            "NeuralSpatialUpmixModeIndex",
        }
        restore_vals = {
            k: v
            for k, v in baseline_settings.items()
            if k in chaos_mutable_keys
        }
        bridge.cmd("settings_set", {"key": "NeuralExecutionBackendIndex", "value": 0}, tier=1)
        if restore_vals:
            bridge.cmd("settings_set", {"values": restore_vals}, tier=1)
        disarm_res = bridge.cmd("settings_set", {"key": "QaMvsepBlockUntilUtcMs", "value": 0}, tier=1)
        if not disarm_res.get("ok"):
            failures.append({
                "killId": "W59-REBOOT-02",
                "message": f"Failed to cleanly disarm QA MVSEP lease: {disarm_res.get('error')}",
            })
        elif baseline_settings.get("NeuralExecutionBackendIndex") not in (None, 0):
            bridge.cmd("settings_set", {"key": "NeuralExecutionBackendIndex", "value": baseline_settings["NeuralExecutionBackendIndex"]}, tier=1)

    combined_tel = dict(last_telemetry)
    combined_tel["soakBaseline"] = soak_base
    combined_tel["mutexSnapshot"] = last_mutex
    headroom = hg.evaluate_headroom(combined_tel, mode=headroom_mode) if last_telemetry else None
    if headroom and not headroom.passed:
        for v in headroom.violations:
            failures.append(v.to_dict())

    passed = len(failures) == 0 and mvsep_http_total == 0 and open_races == 0 and mutex_violation_total == 0
    return {
        "ok": True,
        "passed": passed,
        "seed": seed,
        "stepsExecuted": len(trace),
        "mvsepHttpRequests": mvsep_http_total,
        "openRaces": open_races,
        "mutexViolations": mutex_violation_total,
        "failureCount": len(failures),
        "failures": failures,
        "headroom": headroom.to_dict() if headroom else None,
        "trace": [t.signature() for t in trace],
    }


def _create_live_omni_bridge() -> ChaosBridgeProtocol:
    agent_dir = Path(__file__).resolve().parent
    if str(agent_dir) not in sys.path:
        sys.path.insert(0, str(agent_dir))
    import bridge_auth as bauth  # type: ignore
    import omni_agent_client as oac  # type: ignore

    conn = bauth.resolve_connection()
    relay = conn["relayUrl"]
    secret = conn["pairSecret"]
    admin = conn["adminKey"]
    if not relay or not secret:
        raise RuntimeError("Missing relayUrl/pairSecret for live Chaos run. Configure bridge secrets or run `omni_agent.py connect`.")
    cav_chan = oac.pick_cavernize_channel(relay, secret, "auto", admin)

    class _OmniTier1Adapter:
        def cmd(self, name: str, args: dict[str, Any] | None = None, tier: int = 1, timeout_sec: int = 30) -> dict[str, Any]:
            if name not in ALLOWED_CHAOS_COMMANDS:
                return {"ok": False, "error": f"Disallowed command: {name}", "json": None}
            payload = oac.build_cavernize_cmd_payload(name, args or {}, tier=min(1, tier), timeout_sec=timeout_sec)
            t0 = time.monotonic()
            try:
                res = oac.send_agent_command(
                    relay,
                    secret,
                    "cavernize_cmd",
                    payload,
                    channel=cav_chan,
                    admin_key=admin,
                    timeout_sec=max(45, timeout_sec + 15),
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

    return _OmniTier1Adapter()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="CavernizeAndroid W59.2 Deterministic Chaos User Simulator")
    parser.add_argument("--seed", type=int, default=42, help="Deterministic PRNG seed")
    parser.add_argument("--steps", type=int, default=24, help="Number of hostile FSM steps")
    parser.add_argument("--flac", default=DEFAULT_FLAC_DEMO, help="Demo FLAC track path")
    parser.add_argument("--atmos", default=DEFAULT_ATMOS_DEMO, help="Demo Atmos/E-AC3-JOC track path")
    parser.add_argument("--live", action="store_true", help="Run against live S25U Cavernize via OmniAgent Tier-1 bridge")
    parser.add_argument("--dry-run", action="store_true", help="Run against offline SimulatedChaosBridge (default)")
    parser.add_argument("--verify-replay", action="store_true", help="Run seed twice and assert 0 trace diffs")
    args = parser.parse_args(argv)

    bridge: ChaosBridgeProtocol = _create_live_omni_bridge() if args.live else SimulatedChaosBridge(
        flac_path=args.flac, atmos_path=args.atmos
    )
    res = run_chaos_simulation(bridge, seed=args.seed, steps=args.steps, flac_path=args.flac, atmos_path=args.atmos)
    if args.verify_replay and not args.live:
        b2 = SimulatedChaosBridge(flac_path=args.flac, atmos_path=args.atmos)
        res2 = run_chaos_simulation(b2, seed=args.seed, steps=args.steps, flac_path=args.flac, atmos_path=args.atmos)
        diffs = sum(1 for a, b in zip(res["trace"], res2["trace"]) if a != b) + abs(len(res["trace"]) - len(res2["trace"]))
        res["seedReplayDiffs"] = diffs
        if diffs != 0:
            res["passed"] = False

    print(json.dumps(res, indent=2))
    return 0 if res.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
