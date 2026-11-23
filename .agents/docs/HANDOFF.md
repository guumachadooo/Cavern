# Session Handoff
ts=1795400000|branch=master|last_code=pending-push|dirty=1|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W45
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md (partitioned: Cavern Project + Plugin) && .agents/plugins/dynamic-fractal-planner/.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md (auto-booted via root `AGENTS.md` `## 0`; AI self-updates on workflow/invariant changes)
BUILD_LESSONS|.agents/docs/BUILD_LESSONS.md (`BL-001`..`BL-017`; universalized via plugin `v2.7.0` `error_learner.py` + `verify_universal.py --all` + 7-layer `check_android_cs.py --ci-parity --strict-warnings`)
DEVICE_LOG|Cloudflare Unified Hub v6 Hybrid SQLite Durable Objects (`HUB_DO` + `RELAY_KV` fallback) + OmniAgent v2.0.0 (`cf_deploy_worker`, instant overlay, same-signature Cavernize IPC) + GitHub `diagnostics` CI-only
SUMMARY|W45 diagnosed slow S25U AI stems/karaoke via native ARM64 real-hardware benchmark (`uvr_mdxnet_kara_2.onnx` & `scnet_4stem_fp16.onnx`): switched `BuildAndroid.yml` from stripped `onnxruntime-android-qnn` (`v1.29.0`, zero `libQnn*.so`, no XNNPACK/WebGPU/FP16 Gelu) to standard `onnxruntime-android` (`1.32.0+`, 16 KB ELF), added `NeuralExecutionProvider.WebGpu = 4` (`1,213 ms/chunk` on Adreno 830 = `5.2x` faster than CPU), smart per-model `Auto` routing (`WebGPU` for static CNN/U-Net STFT models, 4-thread `CPU KleidiAI` `6,488 ms/chunk` for `SCNet`/`Demucs`/`RoFormer` avoiding WebGPU `Slice/Mod/Gather` sync stalls & 32% faster than 6 threads), real non-blocking EP status in Settings (`DescribeEffectiveProvider()` + `ProbeCapabilitiesAsync()`), & `(TTA · 3x slower)` L10n clarity.
NEXT|user pushes W45 (NO `[skip ci]`) -> watches `Build Android APK` CI (0err/0warn) -> installs updated `Cavernize-Android.apk` -> optionally disables `TTA (3x mais lento)` in Settings -> verifies fast WebGPU Vulkan Karaoke & 4-thread KleidiAI SCNet 4-Stem on S25U

