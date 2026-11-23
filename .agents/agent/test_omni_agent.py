#!/usr/bin/env python3
"""Unit tests for OmniAgent v4 Protocol, Termux/Debian Daemon, Antigravity Bridge & Hot-Probe Compiler."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

AGENT_DIR = Path(__file__).resolve().parent
if str(AGENT_DIR) not in sys.path:
    sys.path.insert(0, str(AGENT_DIR))

import deploy_worker
import omni_agent_daemon as daemon
import omni_protocol as proto


class OmniAgentProtocolTests(unittest.TestCase):
    KEY = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

    def test_key_normalization_and_headers(self) -> None:
        self.assertEqual(proto.normalize_pair_secret_hex("CAVPAIR-" + self.KEY.upper()), self.KEY)
        h = proto.derive_agent_headers(self.KEY, channel="termux")
        self.assertEqual(len(h["X-Device-Hash"]), 64)
        self.assertEqual(len(h["X-Agent-Key"]), 64)
        self.assertNotEqual(h["X-Device-Hash"], h["X-Agent-Key"])
        self.assertEqual(h["X-Agent-Channel"], "termux")

    def test_e2e_envelope_roundtrip_and_tamper_reject(self) -> None:
        payload = {"cmd": "git_commit_push", "args": {"message": "feat: test", "big": "x" * 2048}}
        env = proto.encrypt_envelope(self.KEY, "cmd:req123", payload)
        self.assertEqual(env["v"], 4)
        self.assertTrue(env["gz"])
        dec = proto.decrypt_envelope(self.KEY, "cmd:req123", env)
        self.assertEqual(dec, payload)

        with self.assertRaises(ValueError):
            proto.decrypt_envelope(self.KEY, "cmd:wrong_ctx", env)

        tampered = dict(env)
        tampered["ct"] = env["ct"][:-4] + ("AAAA" if not env["ct"].endswith("AAAA") else "BBBB")
        with self.assertRaises(ValueError):
            proto.decrypt_envelope(self.KEY, "cmd:req123", tampered)

    def test_hot_exec_hmac_signature(self) -> None:
        dll = b"MZ" + b"\x00" * 512
        sha1, sig1 = proto.sign_hot_exec(self.KEY, "hot_1", 1700000000, dll)
        sha2, sig2 = proto.sign_hot_exec(self.KEY, "hot_1", 1700000000, dll)
        self.assertEqual((sha1, sig1), (sha2, sig2))
        self.assertEqual(len(sha1), 64)
        self.assertEqual(len(sig1), 64)
        _, sig_other = proto.sign_hot_exec(self.KEY, "hot_2", 1700000000, dll)
        self.assertNotEqual(sig1, sig_other)

    def test_delta_bundle_roundtrip_and_path_traversal_guard(self) -> None:
        with tempfile.TemporaryDirectory() as src_tmp, tempfile.TemporaryDirectory() as dst_tmp:
            src, dst = Path(src_tmp), Path(dst_tmp)
            (src / "dir").mkdir()
            (src / "dir" / "hello.cs").write_text("public class A {}", encoding="utf-8")
            (dst / "old.txt").write_text("remove me", encoding="utf-8")

            bundle = proto.pack_delta_bundle(src, ["dir/hello.cs"], deleted_paths=["old.txt"])
            res = proto.apply_delta_bundle(dst, bundle, allow_delete=True)
            self.assertEqual(res["written"], ["dir/hello.cs"])
            self.assertEqual(res["deleted"], ["old.txt"])
            self.assertEqual((dst / "dir" / "hello.cs").read_text(encoding="utf-8"), "public class A {}")
            self.assertFalse((dst / "old.txt").exists())

            bad_bundle = {
                "format": "omni-delta-v1",
                "files": [{"path": "../escape.txt", "sha256": "", "gz_b64": ""}],
                "deleted": [],
            }
            with self.assertRaises(ValueError):
                proto.apply_delta_bundle(dst, bad_bundle)

    def test_antigravity_discover_and_compact_sync(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            ag = root / "antigravity"
            (ag / "brain" / "conv1").mkdir(parents=True)
            (ag / "knowledge").mkdir(parents=True)
            (ag / "conversations").mkdir(parents=True)
            (ag / "conversations" / "test.pb").write_bytes(b"\x08\x01binary")
            repo = root / "Cavern"
            repo.mkdir()

            disc = daemon.discover_antigravity(ag)
            self.assertTrue(disc["found"])
            self.assertIn("brain", disc["entries"])

            sync = daemon.sync_antigravity_session(
                ag,
                repo,
                {
                    "sessionId": "w41_test",
                    "title": "W41 OmniAgent",
                    "summary": "Compact token-diet handoff.",
                    "commit": "abc1234",
                    "files": ["a.cs", "b.py"],
                },
            )
            self.assertTrue(sync["ok"])
            self.assertTrue(Path(sync["sessionFile"]).is_file())
            self.assertTrue(Path(sync["latestFile"]).is_file())
            self.assertTrue(Path(sync["knowledgeMirror"]).is_file())
            # Verify binary .pb file was NEVER modified or touched
            self.assertEqual((ag / "conversations" / "test.pb").read_bytes(), b"\x08\x01binary")

    def test_security_mode_smart_never_nags(self) -> None:
        ok, note = daemon.verify_biometric_if_needed("smart", "git_commit_push")
        self.assertTrue(ok)
        self.assertEqual(note, "smart-hmac")

        ok_t, note_t = daemon.verify_biometric_if_needed("trusted", "shell_exec")
        self.assertTrue(ok_t)
        self.assertEqual(note_t, "trusted")

    def test_daemon_parallel_and_ping(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            d = daemon.OmniAgentDaemon(
                relay_url="https://example.workers.dev",
                pair_secret_hex=self.KEY,
                repo_path=Path(tmp),
                security_mode="smart",
            )
            p = d.handle_command({"cmd": "ping", "args": {}})
            self.assertTrue(p["ok"])
            self.assertEqual(p["agentVersion"], daemon.AGENT_VERSION)

            par = d.handle_command({
                "cmd": "parallel_exec",
                "args": {
                    "tasks": [
                        {"id": "t1", "command": "echo one"},
                        {"id": "t2", "command": "echo two"},
                    ]
                },
            })
            self.assertTrue(par["ok"])
            self.assertEqual(len(par["results"]), 2)

    def test_deploy_worker_multipart_preserves_kv_bindings(self) -> None:
        body, ctype = deploy_worker.build_worker_multipart(
            b"export default { async fetch() { return new Response('ok'); } };",
            main_module="worker.js",
            boundary="TESTBOUNDARY123",
        )
        self.assertEqual(ctype, "multipart/form-data; boundary=TESTBOUNDARY123")
        text = body.decode("utf-8")
        self.assertIn('"main_module":"worker.js"', text)
        self.assertIn('"kv_namespace"', text)
        self.assertIn('"secret_text"', text)
        self.assertIn("Content-Type: application/javascript+module", text)
        self.assertIn("export default", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
