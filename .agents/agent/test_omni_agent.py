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

import bridge_auth as bauth
import deploy_worker
import flight_recorder as fr
import omni_agent_client as oac
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

    def test_p256_ecdh_sas_and_knock_grant_roundtrip(self) -> None:
        priv_a, pub_a_bytes = proto.p256_generate_keypair()
        priv_b, pub_b_bytes = proto.p256_generate_keypair()
        pub_a = proto.p256_encode_public_key(pub_a_bytes)
        pub_b = proto.p256_encode_public_key(pub_b_bytes)

        s_ab = proto.p256_ecdh_shared_secret(priv_a, pub_b)
        s_ba = proto.p256_ecdh_shared_secret(priv_b, pub_a)
        self.assertEqual(len(s_ab), 32)
        self.assertEqual(s_ab, s_ba)

        kid = proto.compute_knock_channel_id(self.KEY)
        self.assertEqual(len(kid), 64)
        self.assertNotEqual(kid, proto.compute_device_hash(self.KEY))

        sas1 = proto.compute_knock_sas(kid, "k_test_1", pub_a)
        sas2 = proto.compute_knock_sas(kid, "k_test_1", pub_a)
        sas_diff = proto.compute_knock_sas(kid, "k_test_1", pub_b)
        self.assertEqual(sas1, sas2)
        self.assertRegex(sas1, r"^[A-Z2-9]{3}-[A-Z2-9]{3}$")
        self.assertNotEqual(sas1, sas_diff)

        grant = {"mode": "workspace", "sessionCode": "CAV-TEST-1234", "pairSecret": self.KEY, "tier": 2}
        enc = proto.encrypt_knock_grant(priv_b, pub_a, "k_test_1", grant)
        dec = proto.decrypt_knock_grant(priv_a, pub_b, "k_test_1", enc)
        self.assertEqual(dec, grant)

        with self.assertRaises(ValueError):
            proto.decrypt_knock_grant(priv_a, pub_b, "k_wrong_id", enc)

    def test_bridge_auth_vault_priority_expiry_and_sync_exclusion(self) -> None:
        self.assertFalse(proto.should_include_workspace_path(".agents/.bridge_vault.json"))
        self.assertFalse(proto.should_include_workspace_path(".env.local"))
        with tempfile.TemporaryDirectory() as tmp:
            t = Path(tmp)
            prof_p = t / "bridge_profile.json"
            vault_p = t / ".bridge_vault.json"
            kid = proto.compute_knock_channel_id(self.KEY)
            bauth.save_public_profile(relay_url="https://demo.workers.dev", knock_id=kid, profile_path=prof_p)
            bauth.save_vault(
                relay_url="https://vault.workers.dev",
                session_token="CAV-LIVE-1111-2222-3333",
                session_ttl_seconds=600,
                vault_path=vault_p,
            )
            r = bauth.resolve_connection(profile_path=prof_p, vault_path=vault_p)
            self.assertEqual(r["relayUrl"], "https://vault.workers.dev")
            self.assertEqual(r["relaySource"], "vault")
            self.assertEqual(r["sessionToken"], "CAV-LIVE-1111-2222-3333")
            self.assertEqual(r["knockId"], kid)

            # Force expired session token -> auto-pruned cleanly
            vdata = json.loads(vault_p.read_text(encoding="utf-8"))
            vdata["sessionExpiresUnix"] = 100
            vault_p.write_text(json.dumps(vdata), encoding="utf-8")
            r2 = bauth.resolve_connection(profile_path=prof_p, vault_path=vault_p)
            self.assertEqual(r2["sessionToken"], "")

    def test_deploy_worker_v6_sqlite_do_multipart_and_worker_name_extract(self) -> None:
        body, ctype = deploy_worker.build_worker_multipart(
            b"export class HubCoordinator {} export default { async fetch() { return new Response('ok'); } };",
            main_module="worker.js",
            boundary="TESTV6DO",
            enable_durable_object=True,
            include_sqlite_migration=True,
        )
        self.assertEqual(ctype, "multipart/form-data; boundary=TESTV6DO")
        text = body.decode("utf-8")
        self.assertIn('"durable_object_namespace"', text)
        self.assertIn('"name":"HUB_DO"', text)
        self.assertIn('"class_name":"HubCoordinator"', text)
        self.assertIn('"new_sqlite_classes":["HubCoordinator"]', text)
        self.assertIn('"kv_namespace"', text)
        self.assertIn('"secret_text"', text)
        self.assertEqual(
            deploy_worker.infer_worker_name_from_url("https://cavernize-relay.example.workers.dev"),
            "cavernize-relay",
        )

    def test_live_bridge_session_expired_constant_and_vault_callback(self) -> None:
        plugin_scripts = AGENT_DIR.parent / "plugins" / "dynamic-fractal-planner" / "scripts"
        if str(plugin_scripts) not in sys.path:
            sys.path.insert(0, str(plugin_scripts))
        import live_bridge_client as lbc

        self.assertEqual(lbc.EXIT_SESSION_EXPIRED, 4)
        self.assertTrue(callable(lbc.main))

    def test_aistudio_attachments_excluded_from_workspace_sync(self) -> None:
        self.assertFalse(proto.should_include_workspace_path("public/assets/aistudio/cavern_1_lossless.txt"))
        self.assertFalse(proto.should_include_workspace_path("public/assets/aistudio/sub/img.png"))
        self.assertTrue(proto.should_include_workspace_path("Cavern/Neural/MvsepAntiArtifactPipeline.cs"))

    def test_cavernize_cmd_payload_uses_args_object_and_tier(self) -> None:
        p = oac.build_cavernize_cmd_payload("shell_exec", '{"command":"ls -la"}', tier=2, timeout_sec=90)
        self.assertEqual(p["cmd"], "shell_exec")
        self.assertEqual(p["args"], {"command": "ls -la"})
        self.assertEqual(p["tier"], 2)
        self.assertEqual(p["timeoutSec"], 80)
        self.assertNotIn("argsJson", p)
        self.assertNotIn("maxTier", p)
        with self.assertRaises(ValueError):
            oac.build_cavernize_cmd_payload("ping", '["not-an-object"]', tier=0)

    def test_flight_recorder_auto_diff_rollback_detection_and_restore(self) -> None:
        with tempfile.TemporaryDirectory() as ws_tmp, tempfile.TemporaryDirectory() as fr_tmp:
            ws = Path(ws_tmp)
            fr_dir = Path(fr_tmp)
            (ws / "src").mkdir()
            (ws / "src" / "core.cs").write_text("// baseline v1\n", encoding="utf-8")
            (ws / "public" / "assets" / "aistudio").mkdir(parents=True)
            (ws / "public" / "assets" / "aistudio" / "upload.txt").write_text("ignored", encoding="utf-8")

            init_r = fr.init_session(
                workspace=ws,
                local_dir=fr_dir,
                session_id="test_w46",
                wave="W46",
                goal="Test anti-rollback",
                requests=["Keep progress on cancel"],
                use_remote=False,
            )
            self.assertTrue(init_r["ok"])
            self.assertEqual(init_r["checkpoint"]["seq"], 1)

            # Edit existing file + create a new file
            (ws / "src" / "core.cs").write_text("// upgraded v2 FP16 pipeline\n", encoding="utf-8")
            (ws / "src" / "new_stage.cs").write_text("public sealed class NewStage {}\n", encoding="utf-8")

            save_r = fr.save_checkpoint(
                workspace=ws,
                local_dir=fr_dir,
                phase="implementation",
                summary="Implemented 3-stage pipeline",
                findings=["batch=2 rejected 0.466x", "FP16 approved 1.396x"],
                completed=["Baseline captured"],
                pending=["Run CI parity"],
                next_step="Run check_android_cs.py",
                auto_diff=True,
                use_remote=False,
            )
            self.assertTrue(save_r["ok"])
            self.assertEqual(save_r["checkpoint"]["seq"], 2)
            self.assertIn("src/core.cs", save_r["checkpoint"]["fileHashes"])
            self.assertIn("src/new_stage.cs", save_r["checkpoint"]["fileHashes"])
            self.assertNotIn("public/assets/aistudio/upload.txt", save_r["checkpoint"]["fileHashes"])

            # Simulate AI Studio turn cancellation & workspace rollback!
            (ws / "src" / "core.cs").write_text("// baseline v1\n", encoding="utf-8")
            (ws / "src" / "new_stage.cs").unlink()

            rec = fr.recover_session(workspace=ws, local_dir=fr_dir, use_remote=False)
            self.assertTrue(rec["found"])
            self.assertTrue(rec["rollback"]["rollbackDetected"])
            self.assertEqual(len(rec["rollback"]["reverted"]), 1)
            self.assertEqual(rec["rollback"]["reverted"][0]["path"], "src/core.cs")
            self.assertEqual(rec["rollback"]["missing"], ["src/new_stage.cs"])
            self.assertIn("restore", rec["restoreCommand"])

            # Dry-run restore
            dry = fr.restore_session(workspace=ws, local_dir=fr_dir, dry_run=True, use_remote=False)
            self.assertTrue(dry["ok"])
            self.assertEqual(sorted(dry["wouldWrite"]), ["src/core.cs", "src/new_stage.cs"])
            self.assertFalse((ws / "src" / "new_stage.cs").exists())

            # Real restore
            rest = fr.restore_session(workspace=ws, local_dir=fr_dir, use_remote=False)
            self.assertTrue(rest["ok"])
            self.assertEqual((ws / "src" / "core.cs").read_text(encoding="utf-8"), "// upgraded v2 FP16 pipeline\n")
            self.assertEqual((ws / "src" / "new_stage.cs").read_text(encoding="utf-8"), "public sealed class NewStage {}\n")

            # After restore, rollbackDetected must be False
            rec2 = fr.recover_session(workspace=ws, local_dir=fr_dir, use_remote=False)
            self.assertFalse(rec2["rollback"]["rollbackDetected"])

            # Close session after commit/push
            cls = fr.close_session(local_dir=fr_dir, commit="deadbeef", note="Committed", use_remote=False)
            self.assertEqual(cls["checkpoint"]["status"], "closed")
            rec3 = fr.recover_session(workspace=ws, local_dir=fr_dir, use_remote=False)
            self.assertFalse(rec3["rollback"]["rollbackDetected"])

    def test_flight_recorder_multitier_recency_and_bundle_codec(self) -> None:
        cands = {
            "local": {"sessionId": "s1", "seq": 2, "timestampUnix": 1000.0},
            "cloud": {"sessionId": "s1", "seq": 4, "timestampUnix": 1020.0},
            "termux": {"sessionId": "s1", "seq": 3, "timestampUnix": 1010.0},
        }
        tier, cp = fr.pick_freshest_checkpoint(cands)
        self.assertEqual(tier, "cloud")
        self.assertEqual(cp["seq"], 4)

        bundle = {"format": "omni-delta-v1", "files": [], "deleted": []}
        _, b64, sha = fr.encode_bundle_gz_b64(bundle)
        decoded = fr.decode_bundle_gz_b64(b64, expected_sha256=sha)
        self.assertEqual(decoded, bundle)
        with self.assertRaises(ValueError):
            fr.decode_bundle_gz_b64(b64, expected_sha256="0" * 64)


if __name__ == "__main__":
    unittest.main(verbosity=2)


