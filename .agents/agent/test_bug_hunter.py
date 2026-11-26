#!/usr/bin/env python3
"""Unit tests for `.agents/agent/cavern_bug_hunter.py` pure parsers, classifiers, ledger & orchestrator."""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

AGENT_DIR = Path(__file__).resolve().parent
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

import cavern_bug_hunter as bh  # noqa: E402


class FakeBridge:
    """Deterministic fake Cavernize bridge for offline orchestrator testing."""

    def __init__(self) -> None:
        self.calls = 0
        self.history: list[tuple[str, dict]] = []
        self.settings = {
            "Iso226Enabled": True,
            "LgsrEnabled": False,
            "LoudnessNormModeIndex": 1,
            "BinauralProfileId": "sadie_d2",
        }
        self._log_lines = [
            "[2026-11-24 14:00:00.000] [WARN] [T#1] [AUDIO] Baseline warning",
        ]

    def cmd(self, name: str, args: dict | None = None, tier: int = 2, timeout_sec: int | None = None) -> dict:
        self.calls += 1
        a = dict(args or {})
        self.history.append((name, a))
        if name == "ping":
            return {"ok": True, "elapsedMs": 10, "body": "{}", "json": {"appVersion": "2.9.69", "appBuild": 69, "device": "S25U", "android": "17", "uiHostAttached": True, "counts": {"warn": 1, "error": 0, "fatal": 0}}, "error": ""}
        if name == "capabilities":
            return {"ok": True, "elapsedMs": 5, "body": "{}", "json": {"commands": [{"cmd": "ping"}, {"cmd": "chaos_hunt"}]}, "error": ""}
        if name == "app_state":
            return {"ok": True, "elapsedMs": 8, "body": "{}", "json": {"activeTab": 2, "loadedFile": "/sdcard/a.flac", "isPlaying": True, "isOperationRunning": False, "libraryTrackCount": 10}, "error": ""}
        if name == "settings_get":
            return {"ok": True, "elapsedMs": 5, "body": json.dumps(self.settings), "json": dict(self.settings), "error": ""}
        if name == "settings_set":
            if "key" in a:
                self.settings[a["key"]] = a["value"]
                if a["key"] == "LgsrEnabled" and a["value"] is True:
                    self._log_lines.append("[2026-11-24 14:01:00.000] [ERROR] [T#9] [DSP] Synthetic LGSR toggle error")
            elif "values" in a:
                self.settings.update(a["values"])
            return {"ok": True, "elapsedMs": 5, "body": "{}", "json": {"applied": ["ok"]}, "error": ""}
        if name == "memory":
            return {"ok": True, "elapsedMs": 5, "body": "{}", "json": {"availRamMb": 4000, "managedGcMb": 30, "lowMemory": False}, "error": ""}
        if name == "log_tail":
            return {"ok": True, "elapsedMs": 5, "body": "\n".join(self._log_lines), "json": None, "error": ""}
        if name == "file_read":
            return {"ok": False, "elapsedMs": 5, "body": "rejected", "json": None, "error": "rejected"}
        if name == "definitely_nonexistent_cmd_xyz":
            return {"ok": False, "elapsedMs": 5, "body": "Unknown command", "json": None, "error": ""}
        if name == "chaos_hunt":
            return {"ok": True, "elapsedMs": 40, "body": "{}", "json": {"anomalies": [{"severity": "HIGH", "category": "dsp_race", "scenario": "concurrent_binaural", "message": "Detected static state race"}]}, "error": ""}
        return {"ok": True, "elapsedMs": 5, "body": "{}", "json": {}, "error": ""}

    def pipeline(self, steps: list[dict], stop_on_error: bool = False, tier: int = 2, timeout_sec: int = 45) -> list[dict]:
        out = []
        for st in steps:
            r = self.cmd(str(st.get("cmd") or ""), st.get("args") or {}, tier=tier, timeout_sec=timeout_sec)
            out.append({"ok": r["ok"], "subCmd": str(st.get("cmd") or ""), "body": r["body"], "json": r["json"], "error": r["error"]})
            if stop_on_error and not r["ok"]:
                break
        return out

    def fetch_logcat(self, lines: int = 120, min_level: str = "W", buffer: str = "main") -> str:
        return "E/libsigchain(123): Setting SIGSEGV to SIG_DFL\nI/Choreographer(100): Skipped 48 frames! The application may be doing too much work on its main thread.\n"


class BugHunterUnitTests(unittest.TestCase):
    def test_parse_applog_and_classify(self) -> None:
        raw = (
            "[2026-11-24 14:33:10.733] [WARN] [T#19] [AUDIO] Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1)\n"
            "[2026-11-24 14:34:58.904] [ERROR] [T#3] [DSP] NaN detected in filter\n"
            "[2026-11-24 14:35:00.000] [FATAL] [T#1] [SYS] AppDomain.UnhandledException: boom\n"
        )
        entries = bh.parse_app_logger_lines(raw)
        self.assertEqual(len(entries), 3)
        self.assertEqual(bh.classify_applog_severity(entries[0])[0], "MEDIUM")
        self.assertEqual(bh.classify_applog_severity(entries[1])[0], "HIGH")
        self.assertEqual(bh.classify_applog_severity(entries[2])[0], "CRITICAL")

    def test_logcat_filters_benign_and_catches_real_anomalies(self) -> None:
        raw = "\n".join([
            "--------- beginning of main",
            "E/libsigchain(14176): Setting SIGSEGV to SIG_DFL",
            "E/libsigchain(14176):   #00 pc 00006790 /apex/com.android.art/lib64/libsigchain.so",
            "I/Choreographer(10778): Skipped 12 frames!",
            "I/Choreographer(10778): Skipped 72 frames! The application may be doing too much work on its main thread.",
            "E/AAudioStream(10778): AAudio stream disconnected error=-899",
            "E/AndroidRuntime(10778): FATAL EXCEPTION: main",
        ])
        anomalies = bh.parse_logcat_anomalies(raw)
        cats = [a["category"] for a in anomalies]
        self.assertNotIn("logcat_error", cats)  # libsigchain filtered out
        self.assertIn("ui_jank", cats)  # 72 frames kept, 12 ignored
        self.assertIn("audio_native", cats)
        self.assertIn("crash", cats)
        self.assertEqual(len(anomalies), 3)

    def test_dedup_ledger_collapses_volatile_numbers(self) -> None:
        ledger = bh.FindingLedger()
        is_new1, _ = ledger.record(
            severity="MEDIUM",
            vector="V1",
            source="applogger",
            category="perf",
            title="VocalChain slow: 20.8ms for 42.7ms block (49% RT budget)",
            repro_step="step1",
            evidence="ev1",
        )
        is_new2, f2 = ledger.record(
            severity="MEDIUM",
            vector="V1",
            source="applogger",
            category="perf",
            title="VocalChain slow: 24.1ms for 42.7ms block (56% RT budget)",
            repro_step="step2",
            evidence="ev2",
        )
        self.assertTrue(is_new1)
        self.assertFalse(is_new2)
        self.assertEqual(f2.count, 2)
        self.assertEqual(len(ledger.ranked()), 1)

    def test_ui_tree_anomaly_detection(self) -> None:
        tree = {
            "activeDialogs": 2,
            "activity": {
                "type": "LinearLayout",
                "w": 1080,
                "h": 2400,
                "children": [
                    {"type": "TextView", "text": "Loudness: NaN dB", "w": 200, "h": 40},
                    {"type": "TextView", "text": "Track {0} of {1}", "w": 200, "h": 40},
                    {"type": "Button", "text": "OK", "clickable": True, "w": 0, "h": 0},
                ],
            },
        }
        defects = bh.analyze_ui_tree_anomalies(tree, "Tab0")
        cats = {d["category"] for d in defects}
        self.assertIn("ui_stacked_dialogs", cats)
        self.assertIn("ui_nan_leak", cats)
        self.assertIn("ui_placeholder_leak", cats)
        self.assertIn("ui_zero_size_button", cats)

    def test_acoustic_probe_analyzer(self) -> None:
        issues = bh.analyze_acoustic_probe_result({"status": "PASS", "fingerprint": {"gainDb": -166.0, "peakDbfs": 2.4}}, "probe1")
        cats = {i["category"] for i in issues}
        self.assertIn("dsp_silent_output", cats)
        self.assertIn("dsp_clipping", cats)

    def test_pick_edge_case_tracks(self) -> None:
        tracks = [
            {"Title": "Zero", "DurationMs": 0, "ChannelCount": 2, "FormatBadge": "MP3", "FilePath": "/a/zero.mp3"},
            {"Title": "Mono", "DurationMs": 1000, "ChannelCount": 1, "FormatBadge": "MP3", "FilePath": "/a/mono.mp3"},
            {"Title": "Atmos", "DurationMs": 2000, "ChannelCount": 6, "IsSpatial": True, "FormatBadge": "FLAC", "FilePath": "/a/at.flac"},
            {"Title": "Spec", "DurationMs": 2000, "ChannelCount": 2, "FormatBadge": "WAV", "FilePath": "/a/Ready® (1).wav"},
        ]
        picks = bh.pick_edge_case_library_tracks(tracks, max_picks=4)
        reasons = [r for r, _ in picks]
        self.assertIn("zero_duration_metadata", reasons)
        self.assertIn("spatial_or_multichannel", reasons)
        self.assertIn("mono_channel", reasons)
        self.assertIn("unicode_or_special_chars", reasons)

    def test_live_orchestrator_captures_delta_and_restores_settings(self) -> None:
        fb = FakeBridge()
        hunter = bh.LiveBugHunter(fb, mode="standard", allow_mutate=True, verbose=False)
        report = hunter.run_hunt(selected_vectors=["V3", "V8"])
        self.assertTrue(report["ok"])
        self.assertTrue(report["meta"]["restoredState"])
        # Settings must be restored to original baseline after chaos!
        self.assertEqual(fb.settings["Iso226Enabled"], True)
        self.assertEqual(fb.settings["LgsrEnabled"], False)
        self.assertEqual(fb.settings["LoudnessNormModeIndex"], 1)
        # Must have caught BOTH the live synthetic LGSR error AND the in-app chaos_hunt anomaly!
        titles = [f["title"] for f in report["findings"]]
        self.assertTrue(any("LGSR toggle error" in t for t in titles))
        self.assertTrue(any("static state race" in t for t in titles))
        md = bh.generate_report_markdown(report)
        self.assertIn("Cavernize Live Adversarial Bug-Hunter Report", md)
        self.assertIn("Active Findings", md)
        self.assertEqual(report["meta"].get("restoreDiffKeys"), [])

    def test_split_applog_by_session_stratification(self) -> None:
        raw = "\n".join([
            "[2026-11-23 10:00:00.000] [INFO] [T#1] [SYS] === Session started [sid=old001] ===",
            "[2026-11-23 10:01:00.000] [ERROR] [T#2] [DSP] Pre-fix error from yesterday",
            "[2026-11-24 14:00:00.000] [INFO] [T#1] [SYS] === Session started [sid=cur999] ===",
            "[2026-11-24 14:02:00.000] [WARN] [T#3] [AUDIO] Current session warning",
        ])
        cur, hist = bh.split_applog_by_session(raw, "cur999")
        self.assertEqual(len(cur), 1)
        self.assertEqual(len(hist), 1)
        self.assertIn("Current session warning", cur[0]["msg"])
        self.assertIn("Pre-fix error", hist[0]["msg"])

        # Missing session id treats all entries as historical rather than falsely active
        cur_empty, hist_all = bh.split_applog_by_session(raw, "")
        self.assertEqual(len(cur_empty), 0)
        self.assertEqual(len(hist_all), 4)

    def test_classify_ui_latency_ms(self) -> None:
        self.assertIsNone(bh.classify_ui_latency_ms(120.0, "tap_upmix"))
        stall = bh.classify_ui_latency_ms(450.0, "tap_upmix")
        self.assertIsNotNone(stall)
        assert stall is not None
        self.assertEqual(stall["severity"], "MEDIUM")
        self.assertEqual(stall["category"], "ui_stall")
        freeze = bh.classify_ui_latency_ms(2100.0, "tap_cavernize")
        self.assertIsNotNone(freeze)
        assert freeze is not None
        self.assertEqual(freeze["severity"], "HIGH")
        self.assertEqual(freeze["category"], "ui_freeze")

    def test_analyze_spatial_trajectory_clean_vs_collapse_vs_pingpong(self) -> None:
        # Clean smooth arc at constant radius r=1.0
        clean = [
            {"x": 0.0, "y": 0.0, "z": 1.0},
            {"x": 0.2588, "y": 0.0, "z": 0.9659},
            {"x": 0.5, "y": 0.0, "z": 0.866},
            {"x": 0.7071, "y": 0.0, "z": 0.7071},
            {"x": 0.866, "y": 0.0, "z": 0.5},
        ]
        self.assertEqual(bh.analyze_spatial_trajectory(clean, "Musical", "Guitar"), [])

        # Radial collapse toward center + pumping
        collapse = [
            {"x": 0.8, "y": 0.0, "z": 0.6},
            {"x": 0.2, "y": 0.0, "z": 0.2},
            {"x": 0.8, "y": 0.0, "z": 0.6},
            {"x": 0.15, "y": 0.0, "z": 0.15},
            {"x": 0.8, "y": 0.0, "z": 0.6},
            {"x": 0.2, "y": 0.0, "z": 0.2},
        ]
        collapse_cats = {i["category"] for i in bh.analyze_spatial_trajectory(collapse, "Cinematic", "Drums")}
        self.assertIn("spatial_radial_collapse", collapse_cats)
        self.assertIn("spatial_radial_pingpong", collapse_cats)

        # Azimuth ping-pong across center (alternating left/right sign flips)
        pingpong = [
            {"x": 0.7, "y": 0.0, "z": 0.714},
            {"x": -0.7, "y": 0.0, "z": 0.714},
            {"x": 0.7, "y": 0.0, "z": 0.714},
            {"x": -0.7, "y": 0.0, "z": 0.714},
            {"x": 0.7, "y": 0.0, "z": 0.714},
        ]
        pp_cats = {i["category"] for i in bh.analyze_spatial_trajectory(pingpong, "Musical", "Lead")}
        self.assertIn("spatial_pingpong_oscillation", pp_cats)

    def test_orbit_unwrapped_angular_velocity_avoids_wrap_false_positives(self) -> None:
        import math as _math
        # Smooth continuous 2-revolution 360deg orbit crossing 0deg and +/-180deg multiple times
        smooth_orbit = []
        for k in range(49):
            ang = _math.radians(-170.0 + k * 15.0)
            smooth_orbit.append({"x": _math.sin(ang), "y": 0.1, "z": _math.cos(ang)})
        self.assertEqual(bh.analyze_spatial_trajectory(smooth_orbit, "Orbit", "drums"), [])

        # Erratic back-and-forth angular velocity reversals in Orbit mode must still be caught
        erratic_angles = [0.0, 25.0, 5.0, 35.0, 10.0, 40.0, 15.0]
        erratic_orbit = [
            {"x": _math.sin(_math.radians(a)), "y": 0.0, "z": _math.cos(_math.radians(a))}
            for a in erratic_angles
        ]
        cats = {i["category"] for i in bh.analyze_spatial_trajectory(erratic_orbit, "Orbit", "other")}
        self.assertIn("spatial_pingpong_oscillation", cats)

    def test_v5_corrupt_file_expected_rejections_suppressed_while_crashes_caught(self) -> None:
        fb = FakeBridge()
        hunter = bh.LiveBugHunter(fb, mode="standard", allow_mutate=False, verbose=False)
        sample_log = "\n".join([
            "[2026-11-25 08:32:37.713] [WARN] [T#7] [APP] OpenLocalPathWithFallbackAsync rejected 'corrupt.mp3': Could not decode this audio file. It may be corrupted or use an unsupported codec.",
            "[2026-11-25 08:32:37.716] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.",
            "[2026-11-25 08:32:37.800] [ERROR] [T#9] [DSP] NullReferenceException in corrupt header parser",
        ])
        added = hunter._ingest_applog_delta(sample_log, "V5_corrupt_file_fuzzer", "corrupt fuzz burst")
        self.assertEqual(added, 1)
        findings = hunter.ledger.ranked()
        self.assertEqual(len(findings), 1)
        self.assertIn("NullReferenceException", findings[0].title)

    def test_finding_ledger_origin_stratification_and_live_promotion(self) -> None:
        ledger = bh.FindingLedger()
        ledger.record(
            severity="HIGH",
            vector="V1",
            source="applogger",
            category="audio_error",
            title="Native backend disconnect",
            repro_step="historical_log",
            evidence="old",
            origin="historical_archive",
        )
        ledger.record(
            severity="MEDIUM",
            vector="V1",
            source="applogger",
            category="perf",
            title="Current session jank",
            repro_step="current_log",
            evidence="cur",
            origin="current_session",
        )
        self.assertEqual(len(ledger.active_findings()), 1)
        self.assertEqual(len(ledger.historical_findings()), 1)
        self.assertEqual(ledger.counts_by_severity(active_only=True)["HIGH"], 0)
        self.assertEqual(ledger.counts_by_severity(active_only=False)["HIGH"], 1)

        # Reproducing the historical defect live must promote it to live_triggered!
        is_new, promoted = ledger.record(
            severity="HIGH",
            vector="V5",
            source="applogger",
            category="audio_error",
            title="Native backend disconnect",
            repro_step="live_repro_step",
            evidence="new",
            origin="live_triggered",
        )
        self.assertFalse(is_new)
        self.assertEqual(promoted.origin, "live_triggered")
        self.assertEqual(promoted.vector, "V5")
        self.assertEqual(len(ledger.active_findings()), 2)
        self.assertEqual(ledger.counts_by_origin()["historical_archive"], 0)

    def test_v9_music_only_album_oracle_and_runtime_telemetry(self) -> None:
        class V9MockBridge(FakeBridge):
            def __init__(self) -> None:
                super().__init__()
                self.last_lq_args: list[dict] = []

            def cmd(self, name: str, args: dict | None = None, *, tier: int = 0, timeout_sec: int = 30) -> dict:
                args = args or {}
                if name == "capabilities":
                    return {
                        "ok": True,
                        "body": "{}",
                        "json": {
                            "commands": [
                                {"cmd": "ping"},
                                {"cmd": "library_query"},
                                {"cmd": "runtime_telemetry"},
                                {"cmd": "tab_switch"},
                                {"cmd": "log_tail"},
                                {"cmd": "logcat_tail"},
                                {"cmd": "anomalies"},
                            ]
                        },
                        "elapsedMs": 5,
                        "error": "",
                    }
                if name == "library_query":
                    self.last_lq_args.append(dict(args))
                    q = str(args.get("query") or "").lower()
                    all_music = [
                        {"Title": "willow", "Album": "evermore", "Artist": "Taylor Swift", "DurationMs": 214000, "FilePath": "/Music/evermore/01.flac"},
                        {"Title": "evermore", "Album": "evermore", "Artist": "Taylor Swift", "DurationMs": 304000, "FilePath": "/Music/evermore/15.flac"},
                        {"Title": "gold rush", "Album": "evermore", "Artist": "Taylor Swift", "DurationMs": 185000, "FilePath": "/Music/evermore/03.flac"},
                    ]
                    tracks = [t for t in all_music if not q or q in t["Album"].lower() or q in t["Title"].lower()]
                    return {"ok": True, "body": "{}", "json": {"tracks": tracks, "musicOnly": bool(args.get("musicOnly"))}, "elapsedMs": 8, "error": ""}
                if name == "runtime_telemetry":
                    return {
                        "ok": True,
                        "body": "{}",
                        "json": {
                            "frameMetrics": {"sampledFrames": 120, "p95TotalMs": 9.4},
                            "audioXRuns": {"audioTrackUnderrunCount": 0},
                        },
                        "elapsedMs": 4,
                        "error": "",
                    }
                return super().cmd(name, args, tier=tier, timeout_sec=timeout_sec)

        fb = V9MockBridge()
        hunter = bh.LiveBugHunter(fb, mode="standard", allow_mutate=False, verbose=False)
        report = hunter.run_hunt(selected_vectors=["V9"])
        self.assertTrue(report["ok"])
        self.assertTrue(any(a.get("musicOnly") is True for a in fb.last_lq_args))
        v9_notes = report["vectors"][0]["notes"]
        self.assertIn("albumsAudited=1", v9_notes)
        self.assertIn("fm=120/p95=9.4ms,xruns=0", v9_notes)
        self.assertEqual([f for f in report["findings"] if f["severity"] == "HIGH"], [])

    def test_q1_wcag_contrast_and_alpha_compositing(self) -> None:
        # Pure white (#FFFFFFFF = -1) on One UI 9 BgScreen (#FF080A10 = -16250868)
        white_on_dark = bh.compute_wcag_contrast(-1, -16250868)
        self.assertGreater(white_on_dark, 15.0)
        # Near-black (#FF141822) on BgScreen (#FF080A10) fails WCAG (<3.0)
        dark_on_dark = bh.compute_wcag_contrast(0xFF141822, -16250868)
        self.assertLess(dark_on_dark, 3.0)
        # Semi-transparent white (50% alpha) over dark still composited deterministically
        semi_white = bh.compute_wcag_contrast(0x80FFFFFF, -16250868)
        self.assertGreater(semi_white, 4.5)
        self.assertLess(semi_white, white_on_dark)

    def test_q1_rect_iou_geometry(self) -> None:
        # Disjoint rectangles
        iou0, ow0, oh0 = bh.compute_rect_iou((0, 0, 100, 100), (120, 0, 100, 100))
        self.assertEqual((iou0, ow0, oh0), (0.0, 0, 0))
        # Half overlap horizontally (50x100 intersection over 150x100 union = 1/3)
        iou1, ow1, oh1 = bh.compute_rect_iou((0, 0, 100, 100), (50, 0, 100, 100))
        self.assertAlmostEqual(iou1, 1.0 / 3.0, places=4)
        self.assertEqual((ow1, oh1), (50, 100))
        # Identical rectangles -> IoU 1.0
        iou2, ow2, oh2 = bh.compute_rect_iou((10, 20, 80, 40), (10, 20, 80, 40))
        self.assertAlmostEqual(iou2, 1.0, places=6)
        self.assertEqual((ow2, oh2), (80, 40))

    def test_q1_golden_ssim_seed_pass_fail_and_dim_mismatch(self) -> None:
        import base64 as _b64
        import tempfile as _tf

        w, h = 48, 64
        ref = bytes(((x * 5 + y * 3) % 256) for y in range(h) for x in range(w))
        mut = bytearray(ref)
        for y in range(8, 56):
            for x in range(4, 44):
                mut[y * w + x] = 255 if (x + y) % 2 == 0 else 0

        same_res = bh.compute_ssim_luma_grid(w, h, list(ref), list(ref))
        self.assertTrue(same_res["ok"])
        self.assertAlmostEqual(same_res["ssim"], 1.0, places=5)
        self.assertIsNone(same_res["diffBBox"])

        diff_res = bh.compute_ssim_luma_grid(w, h, list(ref), list(mut))
        self.assertTrue(diff_res["ok"])
        self.assertLess(diff_res["ssim"], 0.6)
        self.assertIsNotNone(diff_res["diffBBox"])

        with _tf.TemporaryDirectory() as td:
            ref_payload = {"lumaW": w, "lumaH": h, "lumaB64": _b64.b64encode(ref).decode("ascii")}
            mut_payload = {"lumaW": w, "lumaH": h, "lumaB64": _b64.b64encode(bytes(mut)).decode("ascii")}
            bad_dim_payload = {"lumaW": 32, "lumaH": 32, "lumaB64": _b64.b64encode(bytes(32 * 32)).decode("ascii")}

            seeded = bh.compare_screenshot_against_golden("Tab0", ref_payload, td)
            self.assertEqual(seeded["status"], "SEEDED")
            passed = bh.compare_screenshot_against_golden("Tab0", ref_payload, td)
            self.assertEqual(passed["status"], "PASS")
            self.assertIsNone(passed["finding"])
            failed = bh.compare_screenshot_against_golden("Tab0", mut_payload, td)
            self.assertEqual(failed["status"], "FAIL")
            self.assertEqual(failed["finding"]["category"], "ui_visual_regression_ssim")
            dim_drift = bh.compare_screenshot_against_golden("Tab0", bad_dim_payload, td)
            self.assertEqual(dim_drift["status"], "MISMATCH_DIM")
            self.assertEqual(dim_drift["finding"]["category"], "ui_visual_dimension_drift")

    def test_q1_m1_mutation_canary_offline_100pct_catch_zero_fp(self) -> None:
        import tempfile as _tf

        with _tf.TemporaryDirectory() as td:
            res = bh.run_m1_mutation_canary_offline(td)
            self.assertTrue(res["ok"], f"M1 canary failed: {res}")
            self.assertEqual(res["caught"], res["total"])
            self.assertEqual(res["total"], 10)
            self.assertAlmostEqual(res["detectRate"], 1.0, places=6)
            self.assertEqual(res["cleanFalsePositives"], 0, f"Unexpected FP: {res['cleanDefects']}")

    def test_v6_q1_geometry_and_golden_ssim_integration(self) -> None:
        import base64 as _b64
        import tempfile as _tf

        w, h = 48, 48
        ref_grid = bytes(((x * 4 + y * 4) % 256) for y in range(h) for x in range(w))
        bad_grid = bytes((255 if (x + y) % 2 == 0 else 0) for y in range(h) for x in range(w))

        class V6Q1MockBridge(FakeBridge):
            def __init__(self, fail_tab2_ssim: bool) -> None:
                super().__init__()
                self.active_tab = 0
                self.fail_tab2_ssim = fail_tab2_ssim

            def cmd(self, name: str, args: dict | None = None, tier: int = 2, timeout_sec: int | None = None) -> dict:
                a = dict(args or {})
                self.history.append((name, a))
                if name == "tab_switch":
                    self.active_tab = int(a.get("index") or 0)
                    return {"ok": True, "elapsedMs": 5, "body": "{}", "json": {"tab": self.active_tab}, "error": ""}
                if name == "ui_audit":
                    return {"ok": True, "elapsedMs": 10, "body": "{}", "json": {"checks": []}, "error": ""}
                if name == "ui_tree":
                    # Inject a real Q1 button ellipsis defect on Tab 4
                    children = []
                    if self.active_tab == 4:
                        children.append({
                            "type": "Button", "text": "A/B Compare: ACTIVE", "clickable": True,
                            "x": 40, "y": 100, "w": 220, "h": 128, "ell": 7, "ellMode": "end", "fg": -1, "sp": 13.0,
                        })
                    payload = {
                        "schemaVersion": 2, "activeTab": self.active_tab, "dpDensity": 3.0,
                        "displayRect": {"x": 0, "y": 0, "w": 1080, "h": 2400},
                        "activity": {"type": "LinearLayout", "orient": "v", "x": 0, "y": 0, "w": 1080, "h": 2400, "children": children},
                    }
                    return {"ok": True, "elapsedMs": 8, "body": json.dumps(payload), "json": payload, "error": ""}
                if name == "screenshot_ui":
                    grid = bad_grid if (self.fail_tab2_ssim and self.active_tab == 2) else ref_grid
                    payload = {"lumaW": w, "lumaH": h, "lumaB64": _b64.b64encode(grid).decode("ascii")}
                    return {"ok": True, "elapsedMs": 12, "body": "{}", "json": payload, "error": ""}
                return super().cmd(name, args, tier=tier, timeout_sec=timeout_sec)

        with _tf.TemporaryDirectory() as td:
            # First run seeds goldens cleanly
            fb_seed = V6Q1MockBridge(fail_tab2_ssim=False)
            h_seed = bh.LiveBugHunter(fb_seed, mode="quick", allow_mutate=False, verbose=False, golden_dir=td, update_goldens=True)
            rep_seed = h_seed.run_hunt(selected_vectors=["V6"])
            self.assertTrue(rep_seed["ok"])
            ui_tree_calls = [a for n, a in fb_seed.history if n == "ui_tree"]
            self.assertEqual(len(ui_tree_calls), 5)
            self.assertTrue(all(a.get("geometry") is True and a.get("maxDepth") == 12 for a in ui_tree_calls))

            # Second run detects BOTH Q1 Tab4 button ellipsis AND Tab2 Golden SSIM regression
            fb_chk = V6Q1MockBridge(fail_tab2_ssim=True)
            h_chk = bh.LiveBugHunter(fb_chk, mode="quick", allow_mutate=False, verbose=False, golden_dir=td, update_goldens=False)
            rep_chk = h_chk.run_hunt(selected_vectors=["V6"])
            cats = {(f["source"], f["category"]) for f in rep_chk["findings"]}
            self.assertIn(("ui_tree_q1", "ui_button_text_ellipsized"), cats)
            self.assertIn(("golden_ssim_q1", "ui_visual_regression_ssim"), cats)


if __name__ == "__main__":
    unittest.main()

