# Audio AI Stem Separation, MVSEP API/Ensemble & Smart Persistent Cache (`BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP`)

> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || ! w/ w/o`).

## 0. Meta & Lineage
BP_META|id=BP_AUDIO_AI_SEPARATION_ENSEMBLE_AND_MVSEP|domain_vec=software:1.00,ai-ml:0.67,data-bi:0.25,dsp-hw:0.67,ux-product:0.40|door=1-way|tier=T3|status=ACTIVE-v1|h1_wave=W1
PARENT_ADR|none
- `software`: Enforce explicit REST/binary contracts, idempotency, backward-compatible `CNTC v1/v2 -> v3` cache schema migration, strict host allowlist, & failure isolation.
- `ai-ml`: Use verified SOTA transformer ONNX checkpoints (BS-RoFormer / Mel-Band RoFormer / SCNet) w/ zero per-song retraining; prove Pure Instrumental SDR & CNTC v3 lossless SNR mathematically via offline golden-bench & unit oracle.
- `dsp-hw`: Preserve `<1 ms` RT audio callback (0-alloc); isolate heavy neural/network/cache compression strictly on offline/background worker plane.

## 1. Value & Non-Goals
VALUE|Upgrade Cavernize AI stem separation to SOTA 2025/2026 RoFormer/SCNet on-device + official MVSEP REST API cloud offload + MVSEP multi-pass offline Studio Ensemble/Bundle (incl. 2-stage Pure Instrumental removing backing vocals) + 5-7x smaller CNTC v3 lossless stem cache w/ in-app folder browser.
WARDLEY|commodity=HTTP/ZLibStream/WAV-FLAC|product=ONNX-Runtime-WebGPU-CPU-KleidiAI + MVSEP-REST-API + MIT-RoFormer-ONNX|custom=BsRoFormerHostStft-adapter + 2-stage-Karaoke-Ensemble-orchestrator + CNTC-v3-MS-LPC-codec + In-App-Cache-Browser
NON_GOAL|No real-time neural inference inside AAUDIO/Oboe audio callback (<1 ms RT plane stays 0-alloc deterministic DSP consuming cached/precomputed stems).
NON_GOAL|No hardcoded/bundled MVSEP API keys or scraping web HTML; strictly user-supplied official MVSEP REST token stored locally in `AppSettingsStore`.
NON_GOAL|No destructive removal of existing UVR MDX-Net / SCNet / HTDemucs models or breaking `CNTC v1/v2` cached tracks.

## 2. Live SOTA & Blind Morphogenesis
AUDIT_EXISTING|Audited `NeuralAudioModels.cs`, `NeuralPipelineOrchestrator.cs`, `INeuralInferenceRuntime.cs`, `NativeOnnxRuntimeBridge.cs`, `NeuralStemStageProcessor.cs`, `NeuralTrackCacheService.cs`, `StudioBatchExporter.cs`, `MainActivity.{Settings.Neural,NeuralWorkflows,StudioExport}.cs`. Found 4 real gaps: (1) `!KaraokePreserveBackingVocals` still routed to `uvr_mdxnet_kara_2.onnx` (lead-only, backing+instrum fused) instead of full-vocal removal; (2) catalog lacked WebGPU-safe FP16 BS-RoFormer 6-stem/4-stem & Mel-Band RoFormer Karaoke/Vocals; (3) zero MVSEP cloud offload or multi-pass Studio Bundle presets; (4) `CNTC v2` stores raw uncompressed float32 (~368 MB/song -> fills 512 MB cap in 1 track) w/o per-track browser.
SOTA_SOURCES|Live-probed `https://mvsep.com/api/app/algorithms` (HTTP 200, 129 algos: `sep_type=40` BS-RoFormer SDR 11.31, `48` MelBand RoFormer SDR 11.17, `49` MVSep Karaoke lead/back/instrum-only, `63` BS-RoFormer Multi 6-stem, `46` SCNet XL, `26/28/30` Ensembles); live-verified HF MIT ONNX checkpoints `Acapellas/mel-band-roformer-karaoke-webgpu` (`model_fp16.onnx` 460 MB, WebGPU graph surgery), `smank/mel-band-roformer-vocals-onnx` (end-to-end waveform SDR 10.98), `elicwhite/bs-roformer-sw-6stem-onnx` (336 MB WebGPU-safe 6-stem host-STFT), `silverdaw/bs-roformer-rhythm-onnx` (257 MB 4-stem host-STFT); MVSEP 2-stage cascade (`Mix->[Vocals,PureInstrum]` then `Vocals->[Lead,Backing]`, +1.13 dB SDR). Naive raw-float cache & naive `kara_2` inversion fail mobile storage & backing-vocal removal.
TRIZ_CONTRADICTION|Improving stem separation SDR (multi-model RoFormer ensembles) & cache capacity (keep dozens of separated tracks offline) vs worsening S25U battery/thermal/storage bloat -> Inventive Principles: Segmentation (On-Device vs MVSEP Cloud vs Offline Studio Bundle), Prior Action (CNTC v3 compressed cache), Dimensionality/Decorrelation (Mid/Side + 2nd-order LPC delta + byte-plane shuffle + ZLib).
CANDIDATE|A|Pure Cloud-Only MVSEP + Lossy MP3/Opus Cache|Offload all separation to MVSEP REST & store 192 kbps lossy stems to save disk.
REJECTED_BECAUSE|A|Fails offline airplane/mobile usage, requires paid/rate-limited internet for every track, & lossy stem encoding introduces pre-echo + phase errors breaking Cavern Wiener `phase_fix` & energy conservation (`sum(stems) == mix`).
CANDIDATE|B|Heavy On-Device Raw-Float Multi-Model Only|Run 3x FP32 RoFormer models on phone CPU & store raw float32 PCM (`CNTC v2`).
REJECTED_BECAUSE|B|368 MB/song exhausts 512 MB cache after 1 song (4-min 4-stem @ 44.1 kHz); multi-model FP32 exceeds thermal/RAM envelope on mobile without cloud offload option.
WINNER|Hybrid 3-Mode Plane: (1) SOTA WebGPU/CPU-KleidiAI FP16 RoFormer+SCNet on-device w/ `BsRoFormerHostStft` + fixed Pure Instrumental routing, (2) Optional Official MVSEP REST API Cloud Offload (user token, WAV/FLAC lossless stems), (3) Offline Multi-Pass Studio Bundle Orchestrator (2-stage MVSEP Karaoke cascade + 6-stem Vocal-Anchor Fusion), unified by pure-C# `CNTC v3` compressed lossless cache (Compact 16-bit >96 dB SNR ~5-7x | Studio 24-bit >140 dB SNR ~3-4x) + O(1) header metadata + In-App Cache Folder Browser.

## 3. Cold-Ctx Red-Team, Pre-Mortem & Kill Criteria
PREMORTEM|T+6mo|User enables "Remove Backing Vocals" w/ `uvr_mdxnet_kara_2.onnx` OR downloads a RoFormer model whose tensor contract mismatches `NativeOnnxRuntimeBridge` -> silent garbage/all-zero stems or instrument erasure; OR MVSEP API token leaks into logs / arbitrary URL triggers SSRF; OR `CNTC v3` corrupts existing `CNTC v2` cache entries causing crash on boot.
THREAT|STRIDE-InfoDisclosure+SSRF: MVSEP `api_token` logged in crash/debug logs OR arbitrary server URL exfiltrates user audio/token. Defense: strict host allowlist (`mvsep.com`, `de.mvsep.com`, `de2.mvsep.com`, `sg.mvsep.com`, HTTPS-only), token redacted in all logs/exceptions/UI (`••••`), multipart upload + cancellation + size cap.
THREAT|DSP-Phase/EnergyCorruption: Subtracting `kara_2` complement (`backing_vocals` = backing+instrum) erases instruments; or `BsRoFormerHostStft` window normalization mismatch causes audible chunk-boundary clicks. Defense: explicit `HasIsolatedBackingVocals` semantics (only sum `lead+backing` when `instrumental` is distinct), route `!KaraokePreserveBackingVocals` to full-vocal separator, and unit-test STFT/iSTFT + OLA conservation & `CNTC v3` roundtrip SNR.
KILL|R1|CNTC v3 compression quality or ratio regression|`compact16_snr_db < 90.0 OR studio24_snr_db < 135.0 OR compression_ratio < 3.0`|Every CI/unit run (`T24`)|Fallback to `RawFloat32` mode & revert codec predictor.
KILL|R2|Pure Instrumental routing leaks backing vocals or destroys instruments|`backing_attenuation_db < 12.0 OR instrum_preservation_db < -1.0 OR energy_residual_db > -40.0`|Every CI/GT run (`T26`, `GT-11`)|Force full-vocal model routing & disable `kara_2` complement subtraction.
KILL|R3|MVSEP REST contract or security breach|`non_allowlisted_host_accepted == true OR token_in_log == true OR poll_hang_ms > 5000`|Every CI/unit run (`T25`)|Block network dispatch & fallback to On-Device runtime (<10 ms).

## 4. Invariants, Budgets & QAS
BUDGET|latency=RT callback 0ms delta (<1ms block), CNTC v3 header peek <2ms/file, encode/decode <150ms/min-audio|mem=peak <1.2x single stem buffer during cache compress|cost=0 USD built-in (user-supplied MVSEP key optional)|degraded_floor=On-Device SCNet/KimVocal + RawFloat32 fallback if cloud offline or cache disabled
PLANES|offline=Studio Ensemble Bundles + MVSEP Cloud Upload/Download + CNTC v3 Compress/Decompress|rt=0-alloc `SpatialKaraokeEngine` & `RealtimePlayerEngine` reading pre-decoded stems|async=Background track prep, MVSEP poll loop (clamped 2s-6s backoff + CancellationToken), Cache Browser UI
QAS|Q1|4-min stereo 4-stem song persisted to `CNTC v3` (`Compact16Bit` & `Studio24Bit`)|Android/CI .NET 11|Bit-accurate header peek + SNR >=90 dB (16-bit) / >=135 dB (24-bit) + >=3.0x compression ratio vs `CNTC v2` + `CNTC v2` backward-read intact|SNR>=90dB(16b), >=135dB(24b), ratio>=3.0x|`python3 -B .agents/scripts/run_w46_unit_tests.py` (T24)
QAS|Q2|MVSEP Cloud separation lifecycle (`algorithms`, `create`, `get` poll `waiting->processing->done`, stem download + role map, SSRF/cancel reject)|Offline mock `HttpMessageHandler` in .NET 11|Canonical `NeuralSeparatedTrack` w/ `lead_vocal`/`backing_vocals`/`instrumental`/`vocals` mapped & non-allowlisted hosts rejected|100% pass, 0 token leaks|`python3 -B .agents/scripts/run_w46_unit_tests.py` (T25)
QAS|Q3|Karaoke Pure Instrumental (`!KaraokePreserveBackingVocals`) + 2-Stage MVSEP Studio Karaoke Bundle + 6-Stem Vocal-Anchor Fusion|Synthetic multi-stem golden mix (`T26` + `GT-11`)|Full-vocal removal eliminates both lead & backing (>=18 dB atten) while conserving instruments (<0.5 dB drift) & `sum(stems)==mix`|atten>=18dB, drift<=0.5dB|`python3 -B .agents/scripts/run_w46_unit_tests.py && python3 -B .agents/scripts/run_audio_ground_truth.py`

## 5. Epistemic Wave-0 Spikes
SPIKE|S1|VoI=0.80|Live MVSEP REST API endpoints (`/api/app/algorithms`, `/api/separation/create`, `/api/separation/get`) & live `render_id` values (`40,48,49,63,46,20,26,28,30`) verified HTTP 200; HF MIT RoFormer ONNX tensor contracts (`MelBandRoFormerHostStft`, `WaveformStereo`, `BsRoFormerHostStft`) verified against official READMEs|PASS (completed pre-H1: all 4 HF models & 9 MVSEP presets verified live)

## 6. Rolling-Wave DSM Graph
### H1 Atomic Slices
SLICE|W1|SOTA Model Catalog, `BsRoFormerHostStft` Contract & Pure Instrumental DSP Fix (`NeuralAudioModels.cs`, `INeuralInferenceRuntime.cs`, `NeuralPipelineOrchestrator.cs`, `NeuralStemStageProcessor.cs`, `NativeOnnxRuntimeBridge.cs`, `NativeOnnxRuntimeBridge.Session.cs`)|HF verified specs + Karaoke/Bundle flags -> 4 new RoFormer models, WebGPU routing, Pure Instrumental full-vocal routing, 2-stage Karaoke & 6-stem Studio Bundles|`check_android_cs.py --ci-parity --strict-warnings` + `T26`
SLICE|W2|Official MVSEP REST Cloud Client (`MvsepCloudClient.cs`)|Audio PCM/WAV + user API token + preset -> verified HTTPS MVSEP offload + canonical `NeuralSeparatedTrack`|`T25` unit contract suite
SLICE|W3|Intelligent Compressed Lossless Cache `CNTC v3` (`NeuralTrackCacheCodec.cs`, `NeuralTrackCacheService.cs`)|`NeuralSeparatedTrack` + metadata -> Mid/Side + 2nd-order LPC + byte-shuffle + ZLib `CNTC v3` + O(1) header peek + `ListEntries`/`DeleteEntry` + `v1/v2` compat|`T24` unit compression & roundtrip suite
SLICE|W4|Android Settings, MVSEP Cloud UI, Studio Bundle UI, Pure Instrumental Toggles, In-App Cache Folder Browser & 4-Lang Parity (`AppSettingsStore.cs`, `MainActivity.{Settings.Neural,NeuralWorkflows,StudioExport,PlayerMusic.NowPlaying}.cs`, `AndroidStrings*.cs`)|W1-W3 services -> One UI 9 controls + masked token modal + Cache Folder Browser modal + EN/PT-BR/HU/ZH key parity|`verify_universal.py --all` + `file_budget_guard.py --check` + `GT-11` + S25U `hot-exec` probe

### H2+ Outcome Stubs
STUB|W5|Stage 4 (T4): Player/Library UX polish, lossless cache hit badge in Now Playing, & Studio/Karaoke one-tap preset polish (deferred strictly to T4 per `MASTER_OVERHAUL_PLAN.md`).

## 7. Fitness Functions & Evolution
FITNESS|`python3 -B .agents/scripts/check_android_cs.py --ci-parity --strict-warnings && python3 -B .agents/scripts/run_w46_unit_tests.py && python3 -B .agents/scripts/run_audio_ground_truth.py && python3 -B .agents/scripts/file_budget_guard.py --check && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/plan_tool.py host-audit && python3 -B .agents/plugins/dynamic-fractal-planner/scripts/verify_universal.py --all`
EVOLVE_RULE|on(kill_trigger || fitness_fail) -> mark SEED/ADR SUPERSEDED && delta-reenter Stage2-3 (!full rewrite)
