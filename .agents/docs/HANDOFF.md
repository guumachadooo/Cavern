# Session Handoff
ts=1795367200|branch=master|last_code=pending-push|dirty=1|fmt=1
ROADMAP|.agents/docs/PHASE2_ROADMAP.md
WAVE|W32.c
ARCHIVE|.agents/docs/archive/SPATIAL_ROADMAP.md (!read_full; slice only on demand)
SYNC_LEDGER|.agents/docs/EXTERNAL_AI_SYNC.md
ONBOARDING|.agents/docs/EXTERNAL_AI_ONBOARDING.md
DEVICE_LOG|.agents/device_logs/cavernize_log.txt
SUMMARY|W32+W32.b+W32.c S25U log fixes + AI inference memory/init hardening + pre-load RAM monitor: added ActivityManager.MemoryInfo+/proc/meminfo RAM guard (`CheckMemoryForModel`/`EnsureSufficientMemoryForModel`, `2.5x+32MB` load + `>=192MB` LMK headroom + 1x GC retry + `InsufficientMemoryException`) on top of ONNX OrtApi/FP16/EP cascade, ref-counted UAF guard, checked 64M tensor bounds, USB debounce, AAudio backoff+latch, ALAC fast FFmpeg, UAC2 2-phase GET_RANGE, & 0 :0.1/:0.2 format bugs
NEXT|user pushes commit & monitors Build Android APK CI -> installs APK on S25U -> verifies ONNX memory guard logs & AI model loading (SCNet FP16/MDX/custom .onnx), Scarlett 2i2 Gen4 hot-plug, ALAC playback, & telemetry; primary AI runs git fetch && git pull --rebase on return
