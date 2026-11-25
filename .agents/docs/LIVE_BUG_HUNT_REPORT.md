# Cavernize Live Adversarial Bug-Hunter Report (SOTA v2)

- **App:** `v2.9.81` (build `81`) | **Session:** `e11d3bc8` | **Device:** `samsung SM-S938B` | **Android:** `17 (SDK 37)`
- **Mode:** `standard` | **Steps Executed:** `32` | **Elapsed:** `107.2s`
- **Active Findings (Live + Current Session):** `10` (`CRITICAL=0` · `HIGH=4` · `MEDIUM=0` · `LOW=6`)
- **Origin Breakdown:** `live_triggered=10` · `current_session=0` · `historical_archive=4` (Total unique: `14`)
- **State Restored Cleanly:** `True` (settings diff keys: `0`)

## Vector Execution Summary

| Vector | Status | New Findings | Elapsed | Notes |
|---|---|---:|---:|---|
| `V1_log_forensics` | **FINDINGS** | 4 | 7.6s | mined 118 AppLogger entries (stratified active vs historical) + logcat W/E/crash/Choreographer |
| `V2_concurrent_playback_oracle` | **OK** | 0 | 3.1s | checked 8 acoustic scenarios concurrently (fails=0, warns=0) |
| `V3_settings_chaos_fuzzer` | **OK** | 0 | 10.6s | executed 16 rapid-fire DSP/boundary steps (safe boundary rejects=0) & restored settings |
| `V4_real_library_edge_hunter` | **OK** | 0 | 11.8s | probed 2 edge-case tracks, opened & seek-tortured 2 |
| `V5_corrupt_file_fuzzer` | **FINDINGS** | 5 | 7.4s | fuzzed 4 malformed/truncated media files in 1 burst + cleaned up |
| `V6_ui_monkey_and_audit` | **FINDINGS** | 2 | 8.9s | ran ui_audit + 5-tab ui_tree scan + search fuzz burst |
| `V7_acoustic_extreme_fuzzer` | **OK** | 0 | 4.5s | fuzzed 6 extreme psychoacoustic/sample-rate/head-pose probes in 1 burst |
| `V8_inapp_chaos_and_watchdog` | **OK** | 0 | 7.9s | in-app chaos_hunt anomalies=0, GC 64MB->82MB (Δ+18MB) |
| `V9_ui_latency_and_search_oracle` | **FINDINGS** | 1 | 23.8s | maxTabMs=30, albumsAudited=2, fm=0/p95=0ms,xruns=0 |
| `V10_spatial_trajectory_and_hot_exec` | **FINDINGS** | 2 | 9.3s | hot_exec RAM probe OK (371ms, unloaded=True), audited 4 choreo modes (anomalies=2) |

## Active Findings — Current Build & Live Triggered (Actionable)

### 1. [HIGH] `applog_ui_error` (4x · `live_triggered`) — [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V5_corrupt_file_fuzzer` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_ui_error:8cd25d454be6`
- **Trigger / Repro:** `corrupt media file fuzz burst`
- **Evidence:** `[2026-11-25 08:32:37.716] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-25 08:32:37.770] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-25 08:32:37.839] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-25 08:32:37.886] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 2. [HIGH] `spatial_pingpong_oscillation` (1x · `live_triggered`) — SpatialChoreo(Orbit/drums) oscillates back-and-forth across center (3 azimuth sign reversals, az=[-179.0..+178.0]deg)
- **Vector:** `V10_spatial_trajectory_and_hot_exec` | **Source:** `spatial_trajectory_oracle` | **Sig:** `spatial_trajectory_oracle:spatial_pingpong_oscillation:c43a94426f35`
- **Trigger / Repro:** `hot_exec SpatialChoreoTrack.Build(Orbit, drums)`
- **Evidence:** `cues=4 samples=49`

### 3. [HIGH] `spatial_pingpong_oscillation` (1x · `live_triggered`) — SpatialChoreo(Orbit/other) oscillates back-and-forth across center (3 azimuth sign reversals, az=[-175.0..+174.0]deg)
- **Vector:** `V10_spatial_trajectory_and_hot_exec` | **Source:** `spatial_trajectory_oracle` | **Sig:** `spatial_trajectory_oracle:spatial_pingpong_oscillation:e59ad53ae9b1`
- **Trigger / Repro:** `hot_exec SpatialChoreoTrack.Build(Orbit, other)`
- **Evidence:** `cues=4 samples=49`

### 4. [HIGH] `logcat_error` (1x · `live_triggered`) — NuMediaExtractor: initMediaExtractor: failed to create MediaExtractor
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_error:306c383f9998`
- **Trigger / Repro:** `UI latency & library search oracle`
- **Evidence:** `E/NuMediaExtractor(11427): initMediaExtractor: failed to create MediaExtractor`

### 5. [LOW] `applog_app_warn` (1x · `live_triggered`) — OpenLocalPathWithFallbackAsync rejected 'corrupt.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V5_corrupt_file_fuzzer` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_warn:0f847e256aaa`
- **Trigger / Repro:** `corrupt media fuzz burst`
- **Evidence:** `[2026-11-25 08:32:37.884] [WARN] [T#5] [APP] OpenLocalPathWithFallbackAsync rejected 'corrupt.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 6. [LOW] `applog_app_warn` (1x · `live_triggered`) — OpenLocalPathWithFallbackAsync rejected 'corrupt.mp3': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V5_corrupt_file_fuzzer` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_warn:bbefb4cfbaed`
- **Trigger / Repro:** `corrupt media file fuzz burst`
- **Evidence:** `[2026-11-25 08:32:37.713] [WARN] [T#7] [APP] OpenLocalPathWithFallbackAsync rejected 'corrupt.mp3': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 7. [LOW] `applog_app_warn` (1x · `live_triggered`) — OpenLocalPathWithFallbackAsync rejected 'empty.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V5_corrupt_file_fuzzer` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_warn:918211f858d1`
- **Trigger / Repro:** `corrupt media fuzz burst`
- **Evidence:** `[2026-11-25 08:32:37.769] [WARN] [T#8] [APP] OpenLocalPathWithFallbackAsync rejected 'empty.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 8. [LOW] `applog_app_warn` (1x · `live_triggered`) — OpenLocalPathWithFallbackAsync rejected 'trunc.wav': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V5_corrupt_file_fuzzer` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_warn:b8cdd2e7df2e`
- **Trigger / Repro:** `corrupt media fuzz burst`
- **Evidence:** `[2026-11-25 08:32:37.838] [WARN] [T#16] [APP] OpenLocalPathWithFallbackAsync rejected 'trunc.wav': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 9. [LOW] `ui_zero_size_button` (1x · `live_triggered`) — Tab1: Visible clickable Button 'Mode: Pro' has zero size (0x0)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree` | **Sig:** `ui_tree:ui_zero_size_button:ee698bbba8ac`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree`
- **Evidence:** `Tab1: Visible clickable Button 'Mode: Pro' has zero size (0x0)`

### 10. [LOW] `logcat_warn` (1x · `live_triggered`) — avern.cavernize: Cleared Reference was only reachable from finalizer (only reported once)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:f934f8d1b883`
- **Trigger / Repro:** `ui_audit + tab thrash + search fuzz`
- **Evidence:** `W/avern.cavernize(11427): Cleared Reference was only reachable from finalizer (only reported once)`

## Historical Pre-Fix Archive (Prior Sessions in Retained AppLogger — Non-Blocking)

### 1. [HIGH] `logcat_error` (49x · `historical_archive`) — perf_hint: Session ID too large, must fit 32-bit integer
- **Vector:** `V1_log_forensics` | **Source:** `logcat_history` | **Sig:** `logcat_history:logcat_error:164a6b831c18`
- **Trigger / Repro:** `logcat -d *:W`
- **Evidence:** `E/perf_hint(11427): Session ID too large, must fit 32-bit integer`

### 2. [MEDIUM] `audio_dropout` (1x · `historical_archive`) — Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:audio_dropout:269dca50193b`
- **Trigger / Repro:** `historical_archive (2026-11-25 07:46:40.054)`
- **Evidence:** `[2026-11-25 07:46:40.054] [WARN] [T#15] [AUDIO] Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.`

### 3. [LOW] `applog_onnx_warn` (2x · `historical_archive`) — ONNX memory guard blocked loading 'melband_roformer_vocals.onnx': INSUFFICIENT_RAM [ActivityManager] for 'melband_roformer_vocals.onnx': file=909.1 MB, estLoad=2304.8 MB, headroom=216.0 MB, need=2520.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_onnx_warn:306c99fffb58`
- **Trigger / Repro:** `historical_archive (2026-11-25 07:50:20.380)`
- **Evidence:** `[2026-11-25 07:50:20.380] [WARN] [T#19] [ONNX] ONNX memory guard blocked loading 'melband_roformer_vocals.onnx': INSUFFICIENT_RAM [ActivityManager] for 'melband_roformer_vocals.onnx': file=909.1 MB, estLoad=2304.8 MB, headroom=216.0 MB, need=2520.8 MB, avail=2093.8 MB / total=11113.0 MB (lowMem=False)`
  - *Also seen:* `[2026-11-25 07:50:36.219] [WARN] [T#19] [ONNX] ONNX memory guard blocked loading 'melband_roformer_vocals.onnx': INSUFFICIENT_RAM [ActivityManager] for 'melband_roformer_vocals.onnx': file=909.1 MB, estLoad=2304.8 MB, headroom=216.0 MB, need=2520.8 MB, avail=2226.7 MB / total=11113.0 MB (lowMem=Fals`

### 4. [LOW] `applog_onnx_warn` (2x · `historical_archive`) — W18/W19.b/T3 PrepareActiveTrackNeuralStemsAsync: Insufficient system RAM to safely load ONNX model 'melband_roformer_vocals.onnx' (909.1 MB). Estimated peak requirement: 2520.8 MB (incl. headroom), av
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_onnx_warn:e26863a4eedf`
- **Trigger / Repro:** `historical_archive (2026-11-25 07:50:20.382)`
- **Evidence:** `[2026-11-25 07:50:20.382] [WARN] [T#19] [ONNX] W18/W19.b/T3 PrepareActiveTrackNeuralStemsAsync: Insufficient system RAM to safely load ONNX model 'melband_roformer_vocals.onnx' (909.1 MB). Estimated peak requirement: 2520.8 MB (incl. headroom), available RAM: 2093.8 MB.`
  - *Also seen:* `[2026-11-25 07:50:36.221] [WARN] [T#19] [ONNX] W18/W19.b/T3 PrepareActiveTrackNeuralStemsAsync: Insufficient system RAM to safely load ONNX model 'melband_roformer_vocals.onnx' (909.1 MB). Estimated peak requirement: 2520.8 MB (incl. headroom), available RAM: 2226.7 MB.`
