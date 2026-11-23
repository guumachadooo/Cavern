# Session Handoff
ts=1795418000|branch=master|last_code=6be72d8c|dirty=0|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W45
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md (partitioned: Cavern Project + Plugin) && .agents/plugins/dynamic-fractal-planner/.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md (auto-booted via root `AGENTS.md` `## 0`; AI self-updates on workflow/invariant changes)
BUILD_LESSONS|.agents/docs/BUILD_LESSONS.md (`BL-001`..`BL-017`; universalized via plugin `v2.7.0` `error_learner.py` + `verify_universal.py --all` + 7-layer `check_android_cs.py --ci-parity --strict-warnings`)
DEVICE_LOG|Cloudflare Unified Hub v6 Hybrid SQLite Durable Objects (`HUB_DO` + `RELAY_KV` fallback) + OmniAgent v2.0.0 (`termux`+`android` both online, `smart` mode, zero-paste vault active) + GitHub `diagnostics` CI-only
SUMMARY|W45 (`6be72d8c`, pushed via OmniAgent -> CI Build Android APK #57 `v2.9.57`) diagnosed slow S25U AI stems/karaoke via native ARM64 real-hardware benchmark (`uvr_mdxnet_kara_2.onnx` & `scnet_4stem_fp16.onnx`): switched `BuildAndroid.yml` from stripped `onnxruntime-android-qnn` (`v1.29.0`, zero `libQnn*.so`, no XNNPACK/WebGPU/FP16 Gelu) to standard `onnxruntime-android` (`1.32.0+`, 16 KB ELF), added `NeuralExecutionProvider.WebGpu = 4` (`1,213 ms/chunk` on Adreno 830 = `5.2x` faster than CPU), smart per-model `Auto` routing (`WebGPU` for static CNN/U-Net STFT models, 4-thread `CPU KleidiAI` `6,488 ms/chunk` for `SCNet`/`Demucs`/`RoFormer` avoiding WebGPU `Slice/Mod/Gather` sync stalls & 32% faster than 6 threads), real non-blocking EP status in Settings (`DescribeEffectiveProvider()` + `ProbeCapabilitiesAsync()`), & `(TTA · 3x slower)` L10n clarity (user had `NeuralTtaEnabled=true`).
NEXT|1) Verify CI Build #57 (`v2.9.57`) & user's S25U test results (`WebGPU` Karaoke + `CPU KleidiAI 4t` SCNet + TTA toggle); 2) Proposed W46 follow-up (if user approves): lossless **Async 3-Stage CPU<->GPU Producer-Consumer Pipelining** (`INeuralInferenceRuntime.cs` + `MvsepAntiArtifactPipeline.cs`: overlap CPU STFT `c+1` & CPU iSTFT/WOLA `c-1` + pooled buffers concurrently while WebGPU runs chunk `c`, bit-exact 0 dB loss) + optional S25U benchmark of `batch=2` and FP16 `uvr_mdxnet_kara_2` (NEVER INT8 — degrades music SDR/phase).

