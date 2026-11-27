#!/usr/bin/env python3
"""Unit tests for `.agents/scripts/error_learner.py` and `.agents/docs/BUILD_LESSONS.md`.

Zero third-party dependencies (`unittest` stdlib only).
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPTS_DIR.parents[1]
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

import error_learner as el  # noqa: E402


class TestErrorLearner(unittest.TestCase):
    def setUp(self) -> None:
        self.lessons_file = REPO_ROOT / ".agents" / "docs" / "BUILD_LESSONS.md"
        self.lessons = el.parse_lessons(self.lessons_file.read_text(encoding="utf-8"))

    def test_real_build_lessons_passes_timelessness_and_integrity_audit(self) -> None:
        ok, errs = el.audit_lessons_file(self.lessons_file)
        self.assertTrue(ok, f"BUILD_LESSONS.md audit failed: {errs}")
        self.assertGreaterEqual(len(self.lessons), 16)

    def test_known_historical_ci_errors_match_existing_lessons_without_duplication(self) -> None:
        sample_ci_json = {
            "errorsByLayer": {
                "04_CavernizeAndroid": [
                    "CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(12,1): error CS0103: The name 'SpatialProgramMetadata' does not exist in the current context",
                    "CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(22,1): error CS0117: 'AndroidStrings' does not contain a definition for 'Current'",
                    "CavernSamples/CavernizeAndroid/Services/SelfDiagnosticEngine.cs(33,1): error CS0104: 'Stream' is an ambiguous reference between 'Android.Media.Stream' and 'System.IO.Stream'",
                ]
            },
            "warningsByLayer": {
                "02_Cavern.Format": [
                    "Cavern.Format/Decoders/IamfDecoder.cs(10,5): warning CS1591: Missing XML comment for publicly visible type or member",
                ],
                "04_CavernizeAndroid": [
                    "CavernSamples/CavernizeAndroid/MainActivity.cs(705,13): warning CA1422: 'Window.SetDecorFitsSystemWindows(bool)' is obsoleted on: 'android' 35.0 and later.",
                    "CavernSamples/CavernizeAndroid/MainActivity.PlayerMusicTab.cs(4919,52): warning CA1416: 'WindowInsets.GetInsets(int)' is only supported on: 'android' 30.0 and later.",
                    "CavernSamples/CavernizeAndroid/Services/MediaServiceUtils.cs(65,121): warning CS0419: Ambiguous reference in cref attribute: 'TimeSpan.TryParse'.",
                    "Microsoft.Android.Sdk.Tooling.targets(72,5): warning XA1006: The TargetFrameworkVersion (Android API level 37) is higher than the targetSdkVersion (35).",
                    "NativeLibs/arm64-v8a/libffmpeg.so : warning XA4301: APK already contains the item lib/arm64-v8a/libffmpeg.so; ignoring.",
                ],
            },
        }
        items = el.load_diagnostics_from_ci_json(sample_ci_json)
        res = el.analyze_diagnostics(items, self.lessons)
        self.assertEqual(len(res.new_candidates), 0, f"Unexpected new candidates: {res.new_candidates}")
        self.assertEqual(len(res.skipped_transient), 0)
        self.assertIn("BL-001", res.known_matches)
        self.assertIn("BL-002", res.known_matches)
        self.assertIn("BL-003", res.known_matches)
        self.assertIn("BL-004", res.known_matches)
        self.assertIn("BL-006", res.known_matches)
        self.assertIn("BL-008", res.known_matches)
        self.assertIn("BL-010", res.known_matches)

    def test_transient_environmental_errors_are_filtered_out(self) -> None:
        raw_log = "\n".join(
            [
                "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.",
                "error MSB3021: Unable to copy file because it is being used by another process.",
                "error: HTTP 503 Service Unavailable timed out",
                "::warning::No space left on device",
            ]
        )
        items = el.load_diagnostics_from_log_text(raw_log)
        res = el.analyze_diagnostics(items, self.lessons)
        self.assertEqual(len(res.new_candidates), 0)
        self.assertGreaterEqual(len(res.skipped_transient), 3)

    def test_timelessness_linter_rejects_dated_versions_and_allows_timeless_rules(self) -> None:
        bad_samples = [
            "Always upgrade to Android 17 API 37 immediately.",
            "Target net11.0-android and .NET 11 in all projects.",
            "Use actions/setup-java@v5 with Java 21 on ubuntu-24.04.",
            "Pin libusb 1.0.27 and NDK r27 as of 2026.",
        ]
        for txt in bad_samples:
            violations = el.lint_timelessness(txt)
            self.assertTrue(violations, f"Expected timelessness violation for: {txt}")

        good_samples = [
            "Check live official Android & .NET Android docs for the current non-obsolete API replacement.",
            "Keep `<TargetFramework>` aligned w/ the active SDK installed in CI; verify `CS0104` / `CA1416`.",
            "Use `OperatingSystem.IsAndroidVersionAtLeast(N)` or `[SupportedOSPlatform(\"androidN.0\")]`.",
            "Zero Fake DSP: follow ISO 226 / ITU-R BS.1770 / 16 KB ELF standards; keep C# 8.0 in `netstandard2.1`.",
        ]
        for txt in good_samples:
            violations = el.lint_timelessness(txt)
            self.assertEqual(violations, [], f"Unexpected violation in timeless rule `{txt}`: {violations}")

    def test_auto_learn_new_structural_error_is_timeless_and_idempotent(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cavern_el_") as tmp:
            tmp_lessons = Path(tmp) / "BUILD_LESSONS.md"
            shutil.copyfile(self.lessons_file, tmp_lessons)

            ci_json = Path(tmp) / "diag.json"
            ci_json.write_text(
                json.dumps(
                    {
                        "errorsByLayer": {
                            "04_CavernizeAndroid": [
                                "CavernSamples/CavernizeAndroid/Services/Foo.cs(42,9): error IL2026: Members annotated with RequiresUnreferencedCode break on net14.0-android and Android 42.0 and later [CavernizeAndroid.csproj]",
                            ]
                        },
                        "warningsByLayer": {
                            "03_Cavernize.Logic": [
                                "CavernSamples/Reusable/Cavernize.Logic/Bar.cs(18,5): warning CA1861: Prefer static readonly arrays for constant arguments on .NET 14 [Cavernize.Logic.csproj]",
                            ]
                        },
                    }
                ),
                encoding="utf-8",
            )

            # First apply should learn IL2026 + CA1861 cleanly with version abstraction
            rc1 = el.main(["--lessons-file", str(tmp_lessons), "--from-ci-json", str(ci_json), "--apply"])
            self.assertEqual(rc1, 0)
            after_1 = tmp_lessons.read_text(encoding="utf-8")
            self.assertIn("IL2026", after_1)
            self.assertIn("CA1861", after_1)
            self.assertNotIn("net14.0-android", after_1)
            self.assertNotIn("Android 42.0", after_1)
            self.assertNotIn(".NET 14", after_1)

            ok, errs = el.audit_lessons_file(tmp_lessons)
            self.assertTrue(ok, f"Auto-learned file failed timelessness audit: {errs}")

            # Second apply MUST be idempotent (zero diff)
            rc2 = el.main(["--lessons-file", str(tmp_lessons), "--from-ci-json", str(ci_json), "--apply"])
            self.assertEqual(rc2, 0)
            self.assertEqual(tmp_lessons.read_text(encoding="utf-8"), after_1)

    def test_manual_record_blocks_dated_rule_and_accepts_timeless_rule(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cavern_el_rec_") as tmp:
            tmp_lessons = Path(tmp) / "BUILD_LESSONS.md"
            shutil.copyfile(self.lessons_file, tmp_lessons)

            # Dated rule MUST be rejected
            rc_bad = el.main(
                [
                    "--lessons-file",
                    str(tmp_lessons),
                    "--record",
                    "--title",
                    "Upgrade to Android 18",
                    "--codes",
                    "XA9999",
                    "--root-cause",
                    "Older API fails on Android 18.",
                    "--rule",
                    "Always target API 38 with actions/setup-java@v6.",
                    "--lookup",
                    "Check docs.",
                    "--apply",
                ]
            )
            self.assertNotEqual(rc_bad, 0)

            # Timeless rule MUST succeed
            rc_good = el.main(
                [
                    "--lessons-file",
                    str(tmp_lessons),
                    "--record",
                    "--title",
                    "Dynamic ABI Manifest Filter Hygiene",
                    "--category",
                    "android-packaging",
                    "--codes",
                    "XA9998",
                    "--root-cause",
                    "Mismatched ABI filters drop native libraries during APK packaging.",
                    "--rule",
                    "Keep native library ABI folders aligned w/ `<RuntimeIdentifiers>` in `.csproj`.",
                    "--lookup",
                    "Check current `.NET Android` ABI packaging docs && CI APK entry verifier.",
                    "--apply",
                ]
            )
            self.assertEqual(rc_good, 0)
            ok, errs = el.audit_lessons_file(tmp_lessons)
            self.assertTrue(ok, f"Recorded lesson failed audit: {errs}")
            self.assertIn("XA9998", tmp_lessons.read_text(encoding="utf-8"))

    def test_multi_domain_compounding_lessons_and_relevance_query(self) -> None:
        domains = {l.domain for l in self.lessons}
        self.assertTrue({"BL", "UI", "ARCH", "DSP", "AI"}.issubset(domains), f"Missing domains: {domains}")
        self.assertGreaterEqual(len(self.lessons), 25)

        ui_hits = el.select_relevant_lessons(self.lessons_file, ["ui"], prompt_text="checkbox blur card", max_items=5)
        self.assertTrue(ui_hits and len(ui_hits) <= 5)
        self.assertTrue(all(l.domain in ("UI", "ARCH") for l in ui_hits))

        with tempfile.TemporaryDirectory(prefix="cavern_el_md_") as tmp:
            tmp_lessons = Path(tmp) / "BUILD_LESSONS.md"
            shutil.copyfile(self.lessons_file, tmp_lessons)
            rc_bad_guard = el.main(
                [
                    "--lessons-file", str(tmp_lessons),
                    "--record", "--domain", "DSP",
                    "--title", "No Blocking Calls in RT Audio",
                    "--root-cause", "Blocking stalls RT audio.",
                    "--rule", "Keep RT audio lock-free.",
                    "--lookup", "Check AUDIO_GROUND_TRUTH_PROTOCOL.md.",
                    "--remediation-hint", "Use lock-free ring buffer.",
                    "--guard", "manual-review",
                    "--apply",
                ]
            )
            self.assertNotEqual(rc_bad_guard, 0)


if __name__ == "__main__":
    unittest.main()
