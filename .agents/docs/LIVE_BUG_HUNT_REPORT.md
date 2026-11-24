# Cavernize Live Adversarial Bug-Hunter Report (SOTA v2)

- **App:** `v2.9.70` (build `70`) | **Session:** `5ce171ac` | **Device:** `samsung SM-S938B` | **Android:** `17 (SDK 37)`
- **Mode:** `standard` | **Steps Executed:** `29` | **Elapsed:** `199.5s`
- **Active Findings (Live + Current Session):** `14` (`CRITICAL=0` · `HIGH=7` · `MEDIUM=3` · `LOW=4`)
- **Origin Breakdown:** `live_triggered=11` · `current_session=3` · `historical_archive=8` (Total unique: `22`)
- **State Restored Cleanly:** `True` (settings diff keys: `0`)

## Vector Execution Summary

| Vector | Status | New Findings | Elapsed | Notes |
|---|---|---:|---:|---|
| `V1_log_forensics` | **FINDINGS** | 11 | 4.9s | mined 121 AppLogger entries (stratified active vs historical) + logcat W/E/crash/Choreographer |
| `V2_concurrent_playback_oracle` | **OK** | 0 | 4.8s | checked 8 acoustic scenarios concurrently (fails=0, warns=0) |
| `V3_settings_chaos_fuzzer` | **OK** | 0 | 11.7s | executed 16 rapid-fire DSP/boundary steps (safe boundary rejects=0) & restored settings |
| `V4_real_library_edge_hunter` | **FINDINGS** | 7 | 18.1s | probed 4 edge-case tracks, opened & seek-tortured 2 |
| `V5_corrupt_file_fuzzer` | **OK** | 0 | 13.6s | fuzzed 4 malformed/truncated media files in 1 burst + cleaned up |
| `V6_ui_monkey_and_audit` | **FINDINGS** | 1 | 10.6s | ran ui_audit + 5-tab ui_tree scan + search fuzz burst |
| `V7_acoustic_extreme_fuzzer` | **OK** | 0 | 4.8s | fuzzed 6 extreme psychoacoustic/sample-rate/head-pose probes in 1 burst |
| `V8_inapp_chaos_and_watchdog` | **FINDINGS** | 1 | 5.1s | in-app chaos_hunt anomalies=0, GC 67MB->163MB (Δ+96MB) |
| `V9_ui_latency_and_search_oracle` | **FINDINGS** | 2 | 24.1s | maxTabMs=70, albumsAudited=0 |
| `V10_spatial_trajectory_and_hot_exec` | **OK** | 0 | 87.2s | hot_exec RAM probe OK (567ms, unloaded=True), audited 4 choreo modes (anomalies=0) |

## Active Findings — Current Build & Live Triggered (Actionable)

### 1. [HIGH] `applog_app_error` (5x · `live_triggered`) — OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_error:ab077d5e949a`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `[2026-11-24 17:32:15.894] [ERROR] [T#18] [APP] OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.488] [ERROR] [T#14] [APP] OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.534] [ERROR] [T#22] [APP] OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.579] [ERROR] [T#26] [APP] OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 2. [HIGH] `applog_ui_error` (5x · `live_triggered`) — [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_ui_error:8cd25d454be6`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `[2026-11-24 17:32:15.905] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.505] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.563] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-24 17:32:28.614] [ERROR] [UI] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 3. [HIGH] `applog_ffmpeg_error` (1x · `live_triggered`) — FFmpeg exit=183 bin="libffmpeg.so" args=-y -hide_banner -i "/storage/emulated/0/Download/Devil - August James (3).mp3" -map 0:a:0 -c:a pcm_s24le -rf64 auto -f wav "/data/user/0/hu.sbence.cavern.cavern
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_ffmpeg_error:ac7adef8ffca`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `[2026-11-24 17:32:15.876] [ERROR] [T#18] [FFMPEG] FFmpeg exit=183 bin="libffmpeg.so" args=-y -hide_banner -i "/storage/emulated/0/Download/Devil - August James (3).mp3" -map 0:a:0 -c:a pcm_s24le -rf64 auto -f wav "/data/user/0/hu.sbence.cavern.cavernize/cache/truehdd_work/ffwav_1b622177302c40c0b9680328cef6b740/decoded.wav"`

### 4. [HIGH] `applog_raw_error` (1x · `live_triggered`) — java.io.IOException: Failed to instantiate extractor.
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_raw_error:d2bccc406d5a`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `java.io.IOException: Failed to instantiate extractor.`

### 5. [HIGH] `logcat_error` (1x · `live_triggered`) — NuMediaExtractor: initMediaExtractor: failed to create MediaExtractor
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_error:306c383f9998`
- **Trigger / Repro:** `UI latency & library search oracle`
- **Evidence:** `E/NuMediaExtractor(24819): initMediaExtractor: failed to create MediaExtractor`

### 6. [MEDIUM] `zero_duration_library_tracks` (1x · `live_triggered`) — 54/80 sampled library tracks have DurationMs=0
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `library_metadata` | **Sig:** `library_metadata:zero_duration_library_tracks:e839da6b7610`
- **Trigger / Repro:** `library_query limit=80`
- **Evidence:** `Examples: Devil!, Devil!, Devil!, Devil!`

### 7. [MEDIUM] `ui_audit_warn_ui.perf_latency_jank` (1x · `live_triggered`) — Live UI audit WARN [ui.perf_latency_jank]: 0 stalls, 1 slower-than-budget target(s) (38 profiled, P50=8ms, P95=53ms, drops=9) [background UI-block mode]: Modal:SmartCleanInspector(68ms/1st=68ms>180ms)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_audit` | **Sig:** `ui_audit:ui_audit_warn_ui.perf_latency_jank:b674828668df`
- **Trigger / Repro:** `cavernize-cmd --name ui_audit`
- **Evidence:** `0 stalls, 1 slower-than-budget target(s) (38 profiled, P50=8ms, P95=53ms, drops=9) [background UI-block mode]: Modal:SmartCleanInspector(68ms/1st=68ms>180ms)`

### 8. [MEDIUM] `managed_heap_growth` (1x · `live_triggered`) — Managed GC heap grew by +96 MB during hunt (67->163 MB)
- **Vector:** `V8_inapp_chaos_and_watchdog` | **Source:** `memory_watchdog` | **Sig:** `memory_watchdog:managed_heap_growth:9d6b9739812f`
- **Trigger / Repro:** `memory baseline vs post-hunt`
- **Evidence:** `{
  "availRamMb": 2472,
  "totalRamMb": 11112,
  "thresholdMb": 216,
  "lowMemory": false,
  "managedGcMb": 163,
  "utc": "2026-11-24T20:32:54.5878848Z"
}`

### 9. [LOW] `applog_media_warn` (1x · `live_triggered`) — Android MediaCodec fallback failed (IOException): Failed to instantiate extractor.
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_media_warn:11c5bfa5cea7`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `[2026-11-24 17:32:15.661] [WARN] [T#18] [MEDIA] Android MediaCodec fallback failed (IOException): Failed to instantiate extractor.`

### 10. [LOW] `applog_ffmpeg_warn` (1x · `live_triggered`) — FFmpeg probe exit=183 (0 audio streams) for 'Devil - August James (3).mp3': Error opening input files: Invalid data found when processing input
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_ffmpeg_warn:2d334ca00be1`
- **Trigger / Repro:** `open+seek torture (spatial_or_multichannel) 13 Hours (2008) [1st Attack] {FHD SDR & Dolby Atmos TrueHD}.mkv`
- **Evidence:** `[2026-11-24 17:32:15.642] [WARN] [T#18] [FFMPEG] FFmpeg probe exit=183 (0 audio streams) for 'Devil - August James (3).mp3': Error opening input files: Invalid data found when processing input`

### 11. [LOW] `logcat_warn` (1x · `live_triggered`) — MediaPlayerNative: info/warning (10950, 0)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:d98311f84973`
- **Trigger / Repro:** `UI latency & library search oracle`
- **Evidence:** `W/MediaPlayerNative(24819): info/warning (10950, 0)`

### 12. [HIGH] `applog_app_error` (1x · `current_session`) — OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): This video contains no audio track.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_current` | **Sig:** `applogger_current:applog_app_error:4b1862c612ae`
- **Trigger / Repro:** `current_session (2026-11-24 17:13:42.001)`
- **Evidence:** `[2026-11-24 17:13:42.001] [ERROR] [T#5] [APP] OpenLocalPathWithFallbackAsync: InvalidOperationException (0x80131509): This video contains no audio track.`

### 13. [HIGH] `applog_ui_error` (1x · `current_session`) — [UI] UI Error shown: This video contains no audio track.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_current` | **Sig:** `applogger_current:applog_ui_error:305eab64c70a`
- **Trigger / Repro:** `current_session (2026-11-24 17:13:42.024)`
- **Evidence:** `[2026-11-24 17:13:42.024] [ERROR] [UI] [UI] UI Error shown: This video contains no audio track.`

### 14. [LOW] `applog_ffmpeg_warn` (1x · `current_session`) — FFmpeg probe exit=1 (0 audio streams) for '00327_P1265029657_Anull_video_gr660_sdr_830x1108-.mp4.mp4': At least one output file must be specified
- **Vector:** `V1_log_forensics` | **Source:** `applogger_current` | **Sig:** `applogger_current:applog_ffmpeg_warn:0cee62d9eeb3`
- **Trigger / Repro:** `current_session (2026-11-24 17:13:41.975)`
- **Evidence:** `[2026-11-24 17:13:41.975] [WARN] [T#5] [FFMPEG] FFmpeg probe exit=1 (0 audio streams) for '00327_P1265029657_Anull_video_gr660_sdr_830x1108-.mp4.mp4': At least one output file must be specified`

## Historical Pre-Fix Archive (Prior Sessions in Retained AppLogger — Non-Blocking)

### 1. [HIGH] `applog_ui_error` (2x · `historical_archive`) — [UI] UI Error shown: An operation is already running.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_ui_error:eecfad236b44`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:36:11.955)`
- **Evidence:** `[2026-11-24 16:36:11.955] [ERROR] [UI] [UI] UI Error shown: An operation is already running.`
  - *Also seen:* `[2026-11-24 16:36:22.048] [ERROR] [UI] [UI] UI Error shown: An operation is already running.`

### 2. [MEDIUM] `audio_dropout` (1x · `historical_archive`) — Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:audio_dropout:269dca50193b`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:36:20.762)`
- **Evidence:** `[2026-11-24 16:36:20.762] [WARN] [T#22] [AUDIO] Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.`

### 3. [MEDIUM] `perf_jank` (1x · `historical_archive`) — SelfCheck WARN [ui.perf_latency_jank]: 0 stalls, 1 slower-than-budget target(s) (38 profiled, P50=14ms, P95=43ms, drops=9): Modal:RenderTarget(17ms/1st=18ms>90ms)
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:perf_jank:9bb773ddb9be`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:22:49.860)`
- **Evidence:** `[2026-11-24 16:22:49.860] [WARN] [T#19] [SYS] SelfCheck WARN [ui.perf_latency_jank]: 0 stalls, 1 slower-than-budget target(s) (38 profiled, P50=14ms, P95=43ms, drops=9): Modal:RenderTarget(17ms/1st=18ms>90ms)`

### 4. [LOW] `applog_sys_warn` (3x · `historical_archive`) — SelfCheck finished: WARN (17 OK · 1 WARN · 0 FAIL · 0 SKIP · 2284ms)
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_sys_warn:91f2e554d0a6`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:22:45.465)`
- **Evidence:** `[2026-11-24 16:22:45.465] [WARN] [T#5] [SYS] SelfCheck finished: WARN (17 OK · 1 WARN · 0 FAIL · 0 SKIP · 2284ms)`
  - *Also seen:* `[2026-11-24 16:45:42.533] [WARN] [T#11] [SYS] SelfCheck finished: WARN (30 OK · 1 WARN · 0 FAIL · 0 SKIP · 5254ms)`
  - *Also seen:* `[2026-11-24 16:48:28.686] [WARN] [T#7] [SYS] SelfCheck finished: WARN (17 OK · 1 WARN · 0 FAIL · 0 SKIP · 1914ms)`

### 5. [LOW] `applog_sys_warn` (2x · `historical_archive`) — SelfCheck WARN [logs.runtime_errors]: Session errors=0, recentLogErrors=12 | Latest: [2026-11-24 15:55:50.747] [ERROR] [T#38] [CLOUD] Live Bridge v3 command 'acoustic_probe' error:...
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_sys_warn:8f8f0de11c6e`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:22:45.466)`
- **Evidence:** `[2026-11-24 16:22:45.466] [WARN] [T#5] [SYS] SelfCheck WARN [logs.runtime_errors]: Session errors=0, recentLogErrors=12 | Latest: [2026-11-24 15:55:50.747] [ERROR] [T#38] [CLOUD] Live Bridge v3 command 'acoustic_probe' error:...`
  - *Also seen:* `[2026-11-24 16:22:49.860] [WARN] [T#19] [SYS] SelfCheck WARN [logs.runtime_errors]: Session errors=0, recentLogErrors=12 | Latest: [2026-11-24 15:55:50.747] [ERROR] [T#38] [CLOUD] Live Bridge v3 command 'acoustic_probe' error:...`

### 6. [LOW] `applog_sys_warn` (1x · `historical_archive`) — SelfCheck WARN [logs.runtime_errors]: Session errors=0, recentLogErrors=3 | Latest: [2026-11-24 16:36:22.048] [ERROR] [UI] [UI] UI Error shown: An operation is already running.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_sys_warn:57ca56a3e0ad`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:48:28.688)`
- **Evidence:** `[2026-11-24 16:48:28.688] [WARN] [T#7] [SYS] SelfCheck WARN [logs.runtime_errors]: Session errors=0, recentLogErrors=3 | Latest: [2026-11-24 16:36:22.048] [ERROR] [UI] [UI] UI Error shown: An operation is already running.`

### 7. [LOW] `applog_sys_warn` (1x · `historical_archive`) — SelfCheck WARN [logs.runtime_errors]: Session errors=3, recentLogErrors=10 | Latest: [2026-11-24 16:36:22.048] [ERROR] [UI] [UI] UI Error shown: An operation is already running.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_sys_warn:9ce428175550`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:45:42.534)`
- **Evidence:** `[2026-11-24 16:45:42.534] [WARN] [T#11] [SYS] SelfCheck WARN [logs.runtime_errors]: Session errors=3, recentLogErrors=10 | Latest: [2026-11-24 16:36:22.048] [ERROR] [UI] [UI] UI Error shown: An operation is already running.`

### 8. [LOW] `applog_sys_warn` (1x · `historical_archive`) — SelfCheck finished: WARN (29 OK · 2 WARN · 0 FAIL · 0 SKIP · 4389ms)
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_sys_warn:7d5ffa5ea2ae`
- **Trigger / Repro:** `historical_archive (2026-11-24 16:22:49.857)`
- **Evidence:** `[2026-11-24 16:22:49.857] [WARN] [T#19] [SYS] SelfCheck finished: WARN (29 OK · 2 WARN · 0 FAIL · 0 SKIP · 4389ms)`
