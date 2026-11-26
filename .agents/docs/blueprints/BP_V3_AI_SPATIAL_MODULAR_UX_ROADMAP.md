# V3 Bridge Reliability, AI Separation UX, Spatial Motion, Modular One UI 9, Mobile ONNX & Transaural Speakers (`BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_V3_AI_SPATIAL_MODULAR_UX_ROADMAP|domain_vec=ux-product:0.90,dsp-hw:0.85,ai-ml:0.80,infra-ops:0.70,software:0.50|door=1-way|tier=T3|status=PARTIAL|h1_wave=W53
PARENT_ADR|BP_VOCAL_STABILITY_CACHE_UX_LOUDNESS|BP_FULL_APP_SOTA_AUDIT|BP_INSTANT_ZERO_WASTE_RESUME
HYGIENE_NOTE|W53.0 immediate fixes shipped (Hub v6.1 + OmniAgent v2.2 remoteMessaging FGS + Cavernize direct channel + WebGPU/RoFormer crash fix + smooth 120Hz volume + 3D Room transport bar + MVSEP cancel-safe resume + silent stem pruning); Waves A-F (W53.1-W53.6) ready for sequential execution.
- `ux-product`: Progressive disclosure, zero-jargon single-CTA AI extraction sheet, modular One UI 9 quick-panel blocks, 120 Hz zero-jank animations, 4-lang parity.
- `dsp-hw`: Strict Offline / RT 0-alloc `<1 ms/block` / Async separation; GCC-PHAT sub-sample alignment; stereo-azimuth-anchored 3D motion; physical transaural XTC for speakers.
- `ai-ml`: Universal invariant neural separation only; MVSEP custom API cascade without redundant passes; mobile ONNX QNN HTP / FP16 / W8A16 quantization w/ strict PSS ceiling.
- `infra-ops`: 24/7 Android 15-17 remote agent reachability via unlimited `remoteMessaging` FGS + Hub v6.1 7d presence history + Cavernize direct fallback channel.

## 1. Value & Non-Goals
VALUE|Guarantee 24/7 remote phone reachability & zero local AI crash kills, protect paid MVSEP credits on cancel, eliminate FLAC+Atmos karaoke phase cancellation & low-end vocal rumble, deliver stereo-anchored expressive 3D motion, unify Play/Sing AI extraction w/ non-destructive multi-variant cache, introduce modular editable One UI 9 blocks, accelerate mobile ONNX via QNN HTP/FP16, && add physical transaural spatialization for phone & external speakers.
WARDLEY|commodity=Android `remoteMessaging` FGS & `ConnectivityManager.NetworkCallback`, Cloudflare Durable Objects SQLite, MVSEP REST API v1, ORT QNN/WebGPU/CPU EPs|product=GCC-PHAT cross-correlation, CNTC v3 ZLib/FLAC lossless stem storage, One UI 9 spring animations|custom=cancel-safe MVSEP job-hash resume + custom Single-Stems->Lead/Backing cascade, FLAC<->Atmos separate-reference phase-coherent vocal subtraction, stereo-pan-anchored 3D object choreography, incremental vocal-reuse multi-variant cache, phone/external transaural crosstalk cancellation (XTC).
NON_GOAL|!sum FLAC + Dolby Atmos into one waveform for MVSEP subtraction (mathematically invalid because neural separators are nonlinear: `f(A+B) != f(A)+f(B)` and 12ch Atmos bed+objects cannot be linearly inverted from a stereo sum).
NON_GOAL|!re-enable per-frame full-screen `RenderEffect` blur (`H1` invariant: keep `EnableFullScreenRenderEffectBlur=false` for 120 Hz zero-jank).
NON_GOAL|!bypass S25U per-process RAM ceiling (`2600 MB` PSS cap / `:worker` isolation) or run heavy FP32 RoFormer models in main UI process without memory guards.

## 2. Live SOTA & Blind Morphogenesis
SOTA_SOURCES|Android 15+ `FOREGROUND_SERVICE_TYPE_REMOTE_MESSAGING` (exempt from `dataSync` 6h timeout); MVSEP REST v1 custom cascade on extracted vocals stem; GCC-PHAT + STFT phase-coherent Wiener vocal subtraction; ITU-R BS.2076 ILD/ITD stereo pan azimuth staging; Qualcomm QNN HTP NPU EP + FP16/W8A16 context binary caching; BACCH / Bauck-Cooper OSD transaural XTC for close-spaced smartphone & external stereo speakers.
TRIZ_CONTRADICTION|Multi-pass AI separation, non-destructive multi-variant cache, & 3D/transaural DSP vs paid API cost, S25U LMKD RAM cap, & UX clutter -> **Segmentation + Prior Action + Reuse (P1, P10, P25)**: reuse vocals stem in stage-2 Lead/Backing cascade, deduplicate cache variants, & disclose advanced controls via one context-aware Play/Sing sheet.
CANDIDATE|A|Single Monolithic Release + Linear FLAC+Atmos Mix Summing|Ship all 13 backlog items in one unverified batch and sum FLAC+Atmos waveforms before sending to MVSEP to halve API cost.
REJECTED_BECAUSE|A|Fails mathematical linearity (`f(A+B) != f(A)+f(B)` creates severe phase bleed and destroys discrete 12ch Atmos objects) and couples unrelated UI/DSP/NPU/transaural changes into an un-debuggable blast radius.
CANDIDATE|B|Local-Only Fixed Presets Without Cloud Cascade or Cache Variants|Restrict MVSEP to pre-built server ensembles (`All-In` only), overwrite cache on every re-extraction, and keep fixed static UI blocks.
REJECTED_BECAUSE|B|Forces users to pay for redundant full-band All-In passes when only Single-Stems + Lead/Backing is needed, destroys prior stem comparisons in cache, and leaves 3D Room/One UI 9 UX rigid.
WINNER|Phased 7-Wave Architecture (`W53.0` shipped + `W53.1..W53.6` queued): `W53.0` 24/7 bridge + WebGPU/RoFormer crash fix + 120Hz volume + decoupled 3D Room bar + MVSEP resume/prune; `W53.1` MVSEP vocal-rumble + FLAC/Atmos GCC-PHAT karaoke + Single-Stems->Lead/Backing cascade; `W53.2` Unified Play/Sing AI sheet + multi-variant cache; `W53.3` stereo-anchored 3D motion; `W53.4` modular One UI 9 blocks; `W53.5` QNN HTP/FP16 ONNX; `W53.6` speaker transaural XTC.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|P0 defenses: (1) Stage-2 Lead/Backing encodes extracted `vocals` stem directly w/ independent job-hash persistence (never re-uploads full mix); (2) Transaural XTC hard-gates on `BuiltInSpeaker`/external speaker & bit-exact bypasses (`0 dB`) on any headphone; (3) QNN HTP context binary cache keys on `Build.Fingerprint + ORT + model SHA-256` w/ CPU fallback.
THREAT|Credit Loss on Network Drop, LMKD OOM During Local Extraction, & Stale Multi-Variant Cache Bloat|Network drop mid-MVSEP job or unbounded multi-variant cache filling storage -> DEFENSE: 24h persisted `mvsepPendingJobsV1` job hash resume, strict `2600 MB` PSS guard + transparent WebGPU->CPU session rebuild, and LRU byte budget eviction preserving pinned favorites.
KILL|R1|Remote bridge channel unreachable during active phone internet|android_channel_uptime_pct < 95.0% OR cavernize_direct_fallback_fail > 0|24h S25U standby check|Fallback to 15-min WorkManager wake + foreground notification channel audit.
KILL|R2|Local ONNX WebGPU crash or RoFormer LMKD OOM kill|webgpu_unhandled_crash > 0 OR lmkd_kill_count > 0|S25U neural stress run|Force CPU ARM64 KleidiAI provider & route heavy RoFormer strictly to `:worker` process or MVSEP Cloud.
KILL|R3|FLAC+Atmos karaoke residual vocal rumble or transaural headphone leak|karaoke_lowband_residual_db > -24.0 dB OR xtc_headphone_bypass_err > 0.0f|Audio Ground Truth bench|Lock FLAC & Atmos to separate aligned vocal references and force hard bypass on non-speaker outputs.

## 4. Invariants, Budgets & QAS
BUDGET|latency=RT audio block <1.0 ms (0 alloc, 0 lock), 120 Hz volume & UI frame p95 <=12.0 ms, MVSEP resume probe <2.0 s|mem=MaxSingleProcessModelPssBytes <=2600 MB, 0 B RT heap alloc in XTC & 3D motion|cost=0 wasted MVSEP re-uploads on cancel, 0 new web deps, repo file budget <850/999|degraded_floor=Transparent CPU ARM64 fallback on WebGPU device loss, direct `channel="cavernize"` bridge if OmniAgent sleeps, bit-exact speaker XTC bypass on headphones.
PLANES|offline=MVSEP 2-stage cascade, CNTC v3/FLAC multi-variant cache, ONNX FP16/QNN context compilation|rt=0-alloc phase-coherent karaoke subtraction, stereo-pan-anchored 3D object motion, transaural XTC FIR/IIR filter|async=Hub v6.1 heartbeats (`remoteMessaging` FGS + Cavernize direct loop), cancel-safe MVSEP polling, One UI 9 modular block layout persistence.
QAS|Q1|Simulate OmniAgent/Cavernize heartbeat & stale-presence query on Hub v6.1 + `omni_agent_client.py` auto-fallback|Unit test suite (`test_worker_v4.mjs`, `test_omni_agent.py`)|Hub preserves 7d `lastSeenUtc` on offline channels, `pick_cavernize_channel` auto-falls back `android -> cavernize`|worker_tests_pass == 9 && omni_tests_pass >= 32|node --test .agents/agent/test_worker_v4.mjs && python3 -B .agents/agent/test_omni_agent.py
QAS|Q2|Simulate WebGPU `Device is lost` mid-run & 909 MB RoFormer load on S25U memory envelope|C# static/unit verification + live S25U log check|Session rebuilds transparently on CPU ARM64 KleidiAI; RoFormer exceeding 2600 MB PSS raises `InsufficientMemoryException` cleanly into lightweight fallback|unhandled_crash == 0 && lmkd_kills == 0|python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings
QAS|Q3|Cancel MVSEP separation after job hash creation and re-invoke on same track+preset, with silent extra stems returned|C# unit test + `MvsepCloudClient`|Reuses saved remote job hash (`0` duplicate `/separation/create` uploads) and prunes silent extra stems (`peak < 1e-4`)|duplicate_uploads == 0 && silent_stems_kept == 0|python3 -B .agents/scripts/run_w46_unit_tests.py
QAS|Q4|Verify FLAC+Atmos karaoke subtraction residual & stereo-pan-anchored 3D azimuth preservation (Waves A & C)|Offline Audio Ground Truth golden bench|Low-frequency vocal residual `<= -24 dB`, fixed stereo object pan error `<= 5 deg`|lowband_residual_db <= -24.0 && pan_err_deg <= 5.0|python3 -B .agents/scripts/run_audio_ground_truth.py

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.95*High|Probe live MVSEP `/api/app/algorithms` schema for exact Single-Stems & Lead/Backing `sep_type`/`add_opt*` parameters before implementing Wave A cascade|valid_render_ids_verified == true
SPIKE|S2|VoI=0.90*High|Benchmark S25U Snapdragon 8 Elite ORT `QnnHtp` SELinux/dlopen + FP16/W8A16 latency & peak PSS before activating Wave E|qnn_selinux_ok == true && peak_pss_mb <= 1400

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W53.0|[DONE] Structural 24/7 remote comms (Hub v6.1 + OmniAgent v2.2 `remoteMessaging` FGS + Cavernize direct `channel="cavernize"` + auto-fallback), WebGPU `Device is lost` CPU recovery & RoFormer 2600 MB PSS guard, 120 Hz exponential volume smoothing, decoupled 3D Room volume/timeline bar + unclipped repeat, MVSEP cancel-safe job resume & silent stem pruning|live Hub v6.1 + OmniAgent & Cavernize APKs + unit/static parity|node `test_worker_v4.mjs`, `test_omni_agent.py`, `check_android_cs.py`, `run_w46_unit_tests.py`
SLICE|W53.1|Wave A (B2/B3/B4): MVSEP low-band vocal-rumble root-cause fix + FLAC/Atmos GCC-PHAT phase-coherent separate-reference karaoke + MVSEP custom API Single-Stems -> Lead/Backing-on-vocals-only cascade|clean vocal removal across FLAC & Atmos + 2-stage MVSEP cascade saving credits|Audio Ground Truth + `MvsepCloudClient` unit tests
SLICE|W53.2|Wave B (B7): Unified context-aware Play/Sing AI extraction sheet + incremental vocal-stem reuse + multi-variant non-destructive cache + lossless storage optimization|1-sheet AI flow + zero redundant vocal re-extraction + non-destructive multi-variant cache|`NeuralTrackCacheService` + UI contract tests
SLICE|W53.3|Wave C (B6): Stereo-pan azimuth estimator for fixed 3D objects + expressive lyrics/energy-aware choreography orbiting relative to original mix pan|fixed stems keep stereo L/R staging & Auto Motion moves expressively|`SpatialObjectCortex` golden bench
SLICE|W53.4|Wave D (B10): One UI 9 modular editable quick-panel inspired control blocks w/ persistent layout order & visibility across Play/3D Room|user-reconfigurable One UI 9 blocks @ 120 Hz zero-jank|UI layout & 4-lang parity verification
SLICE|W53.5|Wave E (B12): Mobile ONNX optimization — QNN HTP Hexagon NPU EP + FP16/W8A16 quantization + SoC-keyed context binary caching|3-6x faster on-device separation w/ `<1.4 GB` PSS|S25U `hot-exec` + ONNX telemetry bench
SLICE|W53.6|Wave F (B13): Physical transaural crosstalk cancellation (XTC) spatializer for S25U built-in stereo speakers & external stereo speakers w/ automatic headphone bypass|wide externalized speaker stage w/ bit-exact headphone bypass|Audio Ground Truth transaural XTC bench

### H2+ Outcome Stubs
STUB|W54|Live S25U validation pass across Waves A-F measuring MVSEP API credit savings, karaoke ERLE across Atmos/FLAC, QNN HTP RTF, 120 Hz FrameMetrics on modular blocks, and built-in speaker XTC blind listening verification.

## 7. Fitness Functions & Evolution
FITNESS|node --test .agents/agent/test_worker_v4.mjs && python3 -B .agents/agent/test_omni_agent.py && python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings && python3 -B .agents/scripts/run_w46_unit_tests.py && python3 -B .agents/scripts/file_budget_guard.py --check && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
