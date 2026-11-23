# Diagnostics, Live Bridge & OmniAgent Full-Agent Architecture (`DIAGNOSTICS_HUB.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 1. Unified Zero-Trust Hub v5 (Single Free Cloudflare Worker + `RELAY_KV`)
- **CI Build Diagnostics (GitHub `diagnostics` branch):**
  - Published exclusively by `.github/workflows/BuildAndroid.yml`: `diagnostics:ci/latest-build-diagnostics.{md,json}` + `diagnostics:ci/latest-learned-lessons.md`.
  - Query live: `python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`
- **OmniConnect v5 Zero-Paste Auto-Connect (`bridge_auth.py` + `/v1/knock/*`):**
  - Priority: `CLI > Env Secrets (CAVERN_RELAY_URL / CAVERN_BRIDGE_PAIR_SECRET, .env.example) > Gitignored Local Vault (.agents/.bridge_vault.json, chmod 0600, excluded from OmniAgent sync) > Public Non-Secret Repo Profile (.agents/bridge_profile.json)`.
  - **Zero-Secret Ephemeral NIST P-256 ECDH Smart Knock (`/v1/knock/*`):** public `knockId = sha256("knock-channel-v5:" + sha256("wake-channel-v2:" + pairSecret))` contains zero secret material; client generates ephemeral P-256 keypair, displays 6-char SAS (`XXX-XXX`), phone polls/approves/denies using private `X-Device-Hash` (server derives `ownerKnockId` preventing spoofing), rate-limited `3/15m` + single-slot `409` lock + `15m` deny cooldown + native `BiometricPrompt`, & encrypts grant (`session` or biometric-verified `workspace`) E2E via P-256 ECDH + v4 Encrypt-then-MAC.
  - Zero-flag CLI entrypoints: `python3 -B .agents/scripts/omni_agent.py connect [--status-only|--force-knock|--scope session|workspace]`, `python3 -B .agents/scripts/live_phone_bridge.py --auto --cmd ping`, `python3 -B .agents/scripts/fetch_live_diagnostics.py --cloudflare --all`.
- **Runtime Device Logs, 12-Layer Self-Audit & Live Bridge v3+ (`hot_exec`) (Cloudflare Hub v5):**
  - Worker `.agents/docs/cloudflare_live_relay_worker.js` (v5, 100% backwards-compatible w/ Cavernize v3/v4).
  - Fetch telemetry: `python3 -B .agents/scripts/fetch_live_diagnostics.py --cloudflare --all`
  - Connect Cavernize Live Bridge v3+: `python3 -B .agents/scripts/live_phone_bridge.py --auto --tier 2 --cmd <cmd> --args '{...}'`
  - **Live C# RAM Hot-Execution (`hot_exec`, zero APK rebuild):** `LiveHotPatchHost.cs` verifies HMAC-SHA256 against Android Keystore pairing key & runs locally compiled IL `.dll` (`.agents/agent/hot_probe_compiler.py`) purely in RAM via collectible `AssemblyLoadContext.LoadFromStream` (100% W^X compliant):
    `python3 -B .agents/scripts/omni_agent.py hot-exec --name MyProbe --code '...'`
- **OmniAgent v4/v5 Full-Agent Ecosystem ("Antigravity 2", `/v1/agent/*`, `/v1/blob/*`, `/v1/swarm/*`, `/v1/knock/*`):**
  - **Phase 1 — Termux / Debian PRoot Daemon (`.agents/agent/omni_agent_daemon.py`, `channel=termux`):**
    - Install/start: `bash .agents/agent/install_termux_agent.sh --bg`
    - Provides true **2-way Git sync/push** (`push-workspace`, `pull-workspace`), Termux/Debian shell + parallel workers, Wireless ADB/Shizuku APK install & `logcat`, Antigravity local Debian integration (`ag-discover`, `ag-sync` compact token-diet mirror never touching binary `.pb`), && optional official local `gemini`/`ollama` CLI subagents.
  - **Phase 2 — Standalone Android Companion APK (`CavernSamples/OmniAgentAndroid/`, `hu.sbence.cavern.omniagent`, `channel=android`):**
    - Built via `.github/workflows/BuildOmniAgent.yml` -> `omniagent-latest`.
    - Survives Cavernize updates/crashes; features One UI 9 AMOLED dashboard, `Smart` (zero fingerprint nagging) / `Biometric` / `Trusted` policy, Smart Knock v5 biometric approval overlay (`OmniKnockApprovalActivity`), Android Keystore AES-256-GCM **direct GitHub Git Data API atomic multi-file push** (works even when Termux is closed!), `OmniApkFileProvider` one-tap APK installer, && Termux `RUN_COMMAND` daemon launcher.
  - **Zero-API-Cost Multi-Agent Swarm Bus (`/v1/swarm/*`):**
    - Coordinates tasks between AI Studio ↔ Antigravity (Debian) ↔ secondary chats via `omni_agent.py swarm <enqueue|list|claim|complete|task|clear>`.
