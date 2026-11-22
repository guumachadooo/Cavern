# Diagnostics & Telemetry Architecture (`DIAGNOSTICS_HUB.md`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`, verbatim paths/symbols).

## 1. Professional Separation of Concerns (v3)
- **CI Build Diagnostics (GitHub `diagnostics` branch):**
  - Published exclusively by GitHub Actions (`.github/workflows/BuildAndroid.yml`):
    - `diagnostics:ci/latest-build-diagnostics.md` & `.json` (compiler/linker/packaging errors + warnings)
    - `diagnostics:ci/latest-learned-lessons.md` (dynamic timeless error-learner report)
  - Query live: `python3 -B .agents/scripts/fetch_live_diagnostics.py --ci --lessons`
- **Runtime Device Logs, Crash Reports, Ultra-Deep Self-Checkups & Live Control (Cloudflare Unified Hub v3):**
  - Powered by ONE free Cloudflare Worker + KV namespace (`RELAY_KV` -> `CAVERNIZE_RELAY`) deployed from `.agents/docs/cloudflare_live_relay_worker.js`.
  - **Zero GitHub commit spam & zero GitHub PAT required:** Cavernize Android reuses the same Keystore-encrypted 256-bit Pairing Key to authenticate `/v1/telemetry/upload` (14-day auto-expiring KV, double-hashed bucket isolation, rolling 10-snapshot history) alongside `/v1/wake/*` and `/v1/session/*`.
  - Query live device logs + ultra-deep checkup:
    `python3 -B .agents/scripts/fetch_live_diagnostics.py --cloudflare --relay-url <worker-url> --pair-secret <64hex> --all`
  - Connect live (biometric wake + 30+ shortcuts + unrestricted Tier-2 `reflect_*` / `ui_interact` / `shell_exec` / `pipeline`):
    `python3 -B .agents/scripts/live_phone_bridge.py --url <worker-url> --wake --pair-secret <64hex> --tier 2 --cmd <cmd> --args '{...}'`
- **Optional Legacy Fallback:** GitHub Contents API upload (`CloudLogProviderMode = 1`) remains available in `CloudLogSyncService.cs` only if a user prefers GitHub branch storage over Cloudflare.
