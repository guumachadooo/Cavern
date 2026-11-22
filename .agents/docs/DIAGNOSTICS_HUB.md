# Diagnostics, Live Bridge & OmniAgent Full-Agent Architecture (`DIAGNOSTICS_HUB.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 1. Unified Zero-Trust Hub v4 (Single Free Cloudflare Worker + `RELAY_KV`)
- **CI Build Diagnostics (GitHub `diagnostics` branch):**
  - Published exclusively by `.github/workflows/BuildAndroid.yml`: `diagnostics:ci/latest-build-diagnostics.{md,json}` + `diagnostics:ci/latest-learned-lessons.md`.
  - Query live: `python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`
- **Runtime Device Logs, 12-Layer Self-Audit & Live Bridge v3+ (`hot_exec`) (Cloudflare Hub v4):**
  - Worker `.agents/docs/cloudflare_live_relay_worker.js` (v4, 100% backwards-compatible w/ Cavernize v3).
  - Fetch telemetry: `python3 -B .agents/scripts/fetch_live_diagnostics.py --cloudflare --relay-url <worker> --pair-secret <64hex> --all`
  - Connect Cavernize Live Bridge v3+: `python3 -B .agents/scripts/live_phone_bridge.py --url <worker> --wake --pair-secret <64hex> --tier 2 --cmd <cmd> --args '{...}'`
  - **Live C# RAM Hot-Execution (`hot_exec`, zero APK rebuild):** `LiveHotPatchHost.cs` verifies HMAC-SHA256 against Android Keystore pairing key & runs locally compiled IL `.dll` (`.agents/agent/hot_probe_compiler.py`) purely in RAM via collectible `AssemblyLoadContext.LoadFromStream` (100% W^X compliant):
    `python3 -B .agents/scripts/omni_agent.py hot-exec --name MyProbe --code '...'`
- **OmniAgent v4 Full-Agent Ecosystem ("Antigravity 2", `/v1/agent/*`, `/v1/blob/*`, `/v1/swarm/*`):**
  - **Phase 1 — Termux / Debian PRoot Daemon (`.agents/agent/omni_agent_daemon.py`, `channel=termux`):**
    - Install/start: `bash .agents/agent/install_termux_agent.sh --bg`
    - Provides true **2-way Git sync/push** (`push-workspace`, `pull-workspace`), Termux/Debian shell + parallel workers, Wireless ADB/Shizuku APK install & `logcat`, Antigravity local Debian integration (`ag-discover`, `ag-sync` compact token-diet mirror never touching binary `.pb`), && optional official local `gemini`/`ollama` CLI subagents.
  - **Phase 2 — Standalone Android Companion APK (`CavernSamples/OmniAgentAndroid/`, `hu.sbence.cavern.omniagent`, `channel=android`):**
    - Built via `.github/workflows/BuildOmniAgent.yml` -> `omniagent-latest`.
    - Survives Cavernize updates/crashes; features One UI 9 AMOLED dashboard, `Smart` (zero fingerprint nagging) / `Biometric` / `Trusted` policy, Android Keystore AES-256-GCM **direct GitHub Git Data API atomic multi-file push** (works even when Termux is closed!), `OmniApkFileProvider` one-tap APK installer, && Termux `RUN_COMMAND` daemon launcher.
  - **Zero-API-Cost Multi-Agent Swarm Bus (`/v1/swarm/*`):**
    - Coordinates tasks between AI Studio ↔ Antigravity (Debian) ↔ secondary chats via `omni_agent.py swarm <enqueue|list|claim|complete|task|clear>`.
