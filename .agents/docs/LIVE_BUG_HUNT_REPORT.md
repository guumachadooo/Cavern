# Cavernize Live Adversarial Bug-Hunter Report (SOTA v2)

- **App:** `v2.9.87` (build `87`) | **Session:** `91a56deb` | **Device:** `samsung SM-S938B` | **Android:** `17 (SDK 37)`
- **Mode:** `standard` | **Steps Executed:** `99` | **Elapsed:** `491.7s`
- **Active Findings (Live + Current Session):** `75` (`CRITICAL=0` · `HIGH=39` · `MEDIUM=31` · `LOW=5`)
- **Origin Breakdown:** `live_triggered=75` · `current_session=0` · `historical_archive=6` (Total unique: `81`)
- **State Restored Cleanly:** `True` (settings diff keys: `0`)
- **Q2 Crash/ANR/120Hz Gate:** `FAIL` | watchdog=`ALIVE` | exitNewFatal=`0` | p95=`11.58ms` (cpuP95=`2.04`) | frozen=`0` | strictMode=`8`
- **Q3 Soak & Stress Gate (`V11`):** `PASS` | cycles=`18` | ΔPSS=`-24.3MB` (`-0.32/cyc`) | ΔFD=`-12` | ΔGC=`-3.0MB` | ΔViews=`+0` | ΔThreads=`+1` | stuckOp=`False` | dialogs=`0`
- **Q4 Frozen Regression Contracts (`V12`):** `PASS` | passed=`15/15` | failed=`0` | live=`5/5` | hotExec=`True`
- **Blocking QA Gate Verdict:** `QA_GATE|VERDICT=FAIL|CRIT=0|HIGH=39|GEOM=24|EXIT_CRASH_NEW=0|P95=2.0ms|CONTRACTS=15/15(fail=0)|REASONS=HIGH=39>0,GEOM=24>0`

## Vector Execution Summary

| Vector | Status | New Findings | Elapsed | Notes |
|---|---|---:|---:|---|
| `V1_log_forensics` | **FINDINGS** | 5 | 16.6s | mined 145 AppLogger entries (stratified active vs historical) + logcat W/E/crash/Choreographer |
| `V2_concurrent_playback_oracle` | **FINDINGS** | 1 | 15.0s | checked 8 acoustic scenarios concurrently (fails=0, warns=0) |
| `V3_settings_chaos_fuzzer` | **FINDINGS** | 1 | 22.7s | executed 16 rapid-fire DSP/boundary steps (safe boundary rejects=0) & restored settings |
| `V4_real_library_edge_hunter` | **FINDINGS** | 3 | 29.7s | probed 2 edge-case tracks, opened & seek-tortured 2 |
| `V5_corrupt_file_fuzzer` | **OK** | 0 | 20.6s | fuzzed 4 malformed/truncated media files in 1 burst + cleaned up |
| `V6_ui_monkey_and_audit` | **FINDINGS** | 61 | 156.9s | ran ui_audit + Q1 geom (5 tabs, ssim=0) + Q3 hostile (rage=3, drag=2, reflow=3) |
| `V7_acoustic_extreme_fuzzer` | **OK** | 0 | 6.9s | fuzzed 6 extreme psychoacoustic/sample-rate/head-pose probes in 1 burst |
| `V8_inapp_chaos_and_watchdog` | **OK** | 0 | 8.0s | in-app chaos_hunt anomalies=0, GC 90MB->57MB (Δ-33MB) |
| `V9_ui_latency_and_search_oracle` | **FINDINGS** | 10 | 36.4s | maxTabMs=18, albumsAudited=2, fm=95/p95=11.58ms,xruns=5, cpuP95=2.04ms |
| `V10_spatial_trajectory_and_hot_exec` | **OK** | 0 | 33.5s | hot_exec RAM probe OK (433ms, unloaded=True), audited 4 choreo modes (anomalies=0) |
| `V11_dirty_state_soak` | **OK** | 0 | 82.2s | soak 18 samples: ΔPSS=-24.3MB (slope=-0.32/cyc), ΔFD=-12, ΔGC=-3.0MB, ΔViews=+0, stuckOp=False, dialogs=0 |
| `V12_regression_state_contracts` | **OK** | 0 | 30.4s | contracts 15/15 PASS (offline=13/13, live=5/5, hotExec=True, fail=0) |

## Active Findings — Current Build & Live Triggered (Actionable)

### 1. [HIGH] `logcat_error` (1x · `live_triggered`) — avern.cavernize: Failed to query component interface for required system resources: 6
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_error:10f54d1a8860`
- **Trigger / Repro:** `real library edge torture`
- **Evidence:** `E/avern.cavernize(28031): Failed to query component interface for required system resources: 6`

### 2. [HIGH] `ui_audit_fail_ui.modals_smoke` (1x · `live_triggered`) — Live UI audit FAIL [ui.modals_smoke]: 53 modal crash/defect(s) (25/27 opened): Modal:BinauralProfileSelector: overlapping horizontal siblings in LinearLayout (overlap=26px); Modal:BinauralProfileSelector: overlapping h
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_audit` | **Sig:** `ui_audit:ui_audit_fail_ui.modals_smoke:9675b9c7a2ef`
- **Trigger / Repro:** `cavernize-cmd --name ui_audit`
- **Evidence:** `53 modal crash/defect(s) (25/27 opened): Modal:BinauralProfileSelector: overlapping horizontal siblings in LinearLayout (overlap=26px); Modal:BinauralProfileSelector: overlapping horizontal siblings in LinearLayout (overlap=61px); Modal:AutoEqProfileSelector: overlapping horizontal siblings in LinearLayout (overlap=55px); Modal:AutoEqProfileSelector: overlap`

### 3. [HIGH] `ui_audit_fail_ui.tabs_and_settings` (1x · `live_triggered`) — Live UI audit FAIL [ui.tabs_and_settings]: 458 UI defect(s), 2 warn(s) across 1341 views: Tab1:3DRoom: horizontal screen overflow on ScrollView 'ScrollView' (x=1449, right=2889, rootRight=1440); Tab1:3DRoom: horizontal scre
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_audit` | **Sig:** `ui_audit:ui_audit_fail_ui.tabs_and_settings:fd3430fa1f47`
- **Trigger / Repro:** `cavernize-cmd --name ui_audit`
- **Evidence:** `458 UI defect(s), 2 warn(s) across 1341 views: Tab1:3DRoom: horizontal screen overflow on ScrollView 'ScrollView' (x=1449, right=2889, rootRight=1440); Tab1:3DRoom: horizontal screen overflow on LinearLayout 'LinearLayout' (x=1449, right=2889, rootRight=1440); Tab1:3DRoom: horizontal screen overflow on HorizontalScrollView 'HorizontalScrollView' (x=1500, rig`

### 4. [HIGH] `hostile_rage_click_exception` (1x · `live_triggered`) — Rage-click on 'Tab0:PlayButton' threw 5 exception(s): No visible View found matching query='' type='Button'.
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `hostile_rage_q3` | **Sig:** `hostile_rage_q3:hostile_rage_click_exception:a5f578c2ea4f`
- **Trigger / Repro:** `tab_switch(0) -> ui_interact(rage_click)`
- **Evidence:** `{
  "action": "rage_click",
  "ok": false,
  "query": "",
  "type": "",
  "requested": 6,
  "intervalMs": 22,
  "succeeded": 0,
  "failed": 6,
  "maxBlockMs": 1.39,
  "totalMs": 131.2,
  "activeDialogsAfter": 0,
  "isOperationRunningAfter": false,
  "exceptions": [
    "No visible View found matchin`

### 5. [HIGH] `ui_parent_horizontal_clip` (1x · `live_triggered`) — Tab0: RoundedArtworkView 'RoundedArtworkView' clips parent bounds horizontally (x=152..1427 outside parent 75..1365)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_parent_horizontal_clip:912823374e9c`
- **Trigger / Repro:** `tab_switch(0) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab0: RoundedArtworkView 'RoundedArtworkView' clips parent bounds horizontally (x=152..1427 outside parent 75..1365)`

### 6. [HIGH] `ui_vertical_overflow` (1x · `live_triggered`) — Tab1: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_vertical_overflow:e54195a309e1`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])`

### 7. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab1: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView '3D Room'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:3d8580fbd943`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView '3D Room'`

### 8. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:deecbc0d9d72`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'`

### 9. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:6b4e4c434d6a`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'`

### 10. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:af30f98f9ba9`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'`

### 11. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:b38606a00274`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'`

### 12. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab2: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Studio'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:217e93a710b7`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Studio'`

### 13. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:34a5d6cf029b`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'`

### 14. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:030605cd5a72`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'`

### 15. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:eef0a0fd77c4`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'`

### 16. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:c3527ba7633a`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'`

### 17. [HIGH] `ui_vertical_overflow` (1x · `live_triggered`) — Tab3: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_vertical_overflow:03143bb50520`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])`

### 18. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab3: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Stems'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:d4956f03245f`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Stems'`

### 19. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:33666a69676a`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'`

### 20. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:68a0d63a4218`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'`

### 21. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:eb09edf265f5`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Settings'`

### 22. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:a59462d73b04`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'`

### 23. [HIGH] `ui_vertical_overflow` (1x · `live_triggered`) — Tab4: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_vertical_overflow:03159f44d9cd`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: OneUiFrostedCapsuleView 'OneUiFrostedCapsuleView' overflows screen vertically (y=2919, bottom=3129, screen=[0..3120])`

### 24. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab4: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Settings'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:92ba06451aa9`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Unreadable WCAG contrast (1.02:1 < 2.2) on TextView 'Settings'`

### 25. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:784a06e3d0ff`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView '3D Room'`

### 26. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:9426092e81b0`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Player'`

### 27. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:d3679093c625`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Stems'`

### 28. [HIGH] `ui_wcag_contrast_fail` (1x · `live_triggered`) — Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_fail:689dfc413c50`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Unreadable WCAG contrast (1.32:1 < 2.2) on TextView 'Studio'`

### 29. [HIGH] `reflow_layout_defect` (1x · `live_triggered`) — [font_130/ui_button_text_ellipsized] Tab1:font_130: Button 'Fine-tune Position & Head' text is ellipsized (5 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_defect:af596e032757`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_button_text_ellipsized] Tab1:font_130: Button 'Fine-tune Position & Head' text is ellipsized (5 chars truncated)`

### 30. [HIGH] `reflow_layout_defect` (1x · `live_triggered`) — [font_130/ui_button_text_ellipsized] Tab1:font_130: Button 'Reset Position & Head' text is ellipsized (5 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_defect:6e095c9c9a85`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_button_text_ellipsized] Tab1:font_130: Button 'Reset Position & Head' text is ellipsized (5 chars truncated)`

### 31. [HIGH] `reflow_layout_defect` (1x · `live_triggered`) — [font_130/ui_parent_horizontal_clip] Tab0:font_130: RoundedArtworkView 'RoundedArtworkView' clips parent bounds horizontally (x=152..1427 outside parent 75..1365)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_defect:fc1dcc67dd7f`
- **Trigger / Repro:** `tab_switch(0) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_parent_horizontal_clip] Tab0:font_130: RoundedArtworkView 'RoundedArtworkView' clips parent bounds horizontally (x=152..1427 outside parent 75..1365)`

### 32. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: CustomViolation: abort @ android.security.KeyStoreOperation.abort(KeyStoreOperation.java:142)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:7a96fb428c56`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: CustomViolation: abort @ android.security.KeyStoreOperation.abort(KeyStoreOperation.java:142)`

### 33. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: CustomViolation: finish @ android.security.KeyStoreOperation.finish(KeyStoreOperation.java:131)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:d7034caf5e6e`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: CustomViolation: finish @ android.security.KeyStoreOperation.finish(KeyStoreOperation.java:131)`

### 34. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: CustomViolation: update @ android.security.KeyStoreOperation.update(KeyStoreOperation.java:117)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:b2241a2d3c90`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: CustomViolation: update @ android.security.KeyStoreOperation.update(KeyStoreOperation.java:117)`

### 35. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: DiskReadViolation: @ android.security.KeyStore2.getKeyEntry(KeyStore2.java:292)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:de08d1fb8312`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: DiskReadViolation: @ android.security.KeyStore2.getKeyEntry(KeyStore2.java:292)`

### 36. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: DiskReadViolation: @ android.security.keystore2.AndroidKeyStoreCipherSpiBase.ensureKeystoreOperationInitialized(AndroidKeyStoreCipherSpiBase.java:360)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:e1c41482951e`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: DiskReadViolation: @ android.security.keystore2.AndroidKeyStoreCipherSpiBase.ensureKeystoreOperationInitialized(AndroidKeyStoreCipherSpiBase.java:360)`

### 37. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: DiskReadViolation: @ android.security.keystore2.AndroidKeyStoreSpi.getKeyMetadata(AndroidKeyStoreSpi.java:172)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:7688cc7304d1`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: DiskReadViolation: @ android.security.keystore2.AndroidKeyStoreSpi.getKeyMetadata(AndroidKeyStoreSpi.java:172)`

### 38. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: DiskReadViolation: @ libcore.io.BlockGuardOs.access(BlockGuardOs.java:74)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:4c6110b052e4`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: DiskReadViolation: @ libcore.io.BlockGuardOs.access(BlockGuardOs.java:74)`

### 39. [HIGH] `strict_mode_main_thread_violation` (1x · `live_triggered`) — StrictMode (thread) violation: DiskWriteViolation: @ android.security.KeyStoreSecurityLevel.createOperation(KeyStoreSecurityLevel.java:99)
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `strict_mode_q2` | **Sig:** `strict_mode_q2:strict_mode_main_thread_violation:074d4778c6c2`
- **Trigger / Repro:** `runtime_telemetry.strictMode`
- **Evidence:** `StrictMode (thread) violation: DiskWriteViolation: @ android.security.KeyStoreSecurityLevel.createOperation(KeyStoreSecurityLevel.java:99)`

### 40. [MEDIUM] `ui_wcag_contrast_warn` (2x · `live_triggered`) — Tab1: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:755a44d0e277`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'`

### 41. [MEDIUM] `reflow_layout_warn` (2x · `live_triggered`) — [font_130/ui_wcag_contrast_warn] Tab1:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:cfa8fd867898`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_wcag_contrast_warn] Tab1:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'`

### 42. [MEDIUM] `reflow_layout_warn` (2x · `live_triggered`) — [landscape/ui_wcag_contrast_warn] Tab1:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:97039d99940f`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[landscape/ui_wcag_contrast_warn] Tab1:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'All'`

### 43. [MEDIUM] `audio_dropout` (1x · `live_triggered`) — Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.
- **Vector:** `V2_concurrent_playback_oracle` | **Source:** `applogger_live` | **Sig:** `applogger_live:audio_dropout:aaeabb8d5406`
- **Trigger / Repro:** `concurrent playback + acoustic_audit`
- **Evidence:** `[2026-11-26 13:01:34.021] [WARN] [T#26] [AUDIO] Pro-Audio native backend reported disconnect (WritePlayback=-1, streak=1); triggering auto-recovery.`

### 44. [MEDIUM] `drag_sweep_jank_120hz` (1x · `live_triggered`) — Drag sweep 'Tab1:RoomSeekBar' 120 Hz jank: 120 Hz FrameMetrics Total P95=44.53ms elevated despite CPU P95=1.52ms (GPU/SurfaceFlinger pressure across 29 frames)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `drag_sweep_q3` | **Sig:** `drag_sweep_q3:drag_sweep_jank_120hz:87fbbc11fbc1`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(drag_sweep) -> frameMetrics`
- **Evidence:** `{
  "action": "drag_sweep",
  "ok": true,
  "type": "OneUiWaveMediaSeekBar",
  "stepsRequested": 20,
  "stepsDispatched": 22,
  "stepMs": 14,
  "startProgress": 500,
  "endProgress": 500,
  "restoredProgress": true,
  "maxStepUiBlockMs": 39.63,
  "error": ""
}`

### 45. [MEDIUM] `ui_text_ellipsized` (1x · `live_triggered`) — Tab0: Short TextView 'THK 1.00m MP' text is ellipsized (3 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_text_ellipsized:1e13911850cd`
- **Trigger / Repro:** `tab_switch(0) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab0: Short TextView 'THK 1.00m MP' text is ellipsized (3 chars truncated)`

### 46. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab1: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:0e9453e17be8`
- **Trigger / Repro:** `tab_switch(1) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab1: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'`

### 47. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Export Master'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:0d605d6d43bf`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Export Master'`

### 48. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Lossless'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:a3b2e7c908ad`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Lossless'`

### 49. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Master Presets'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:82c409af7ea7`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Master Presets'`

### 50. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Record Performance'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:cdbe12258d8d`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Low WCAG contrast (2.23:1 < 3.0) on Button 'Record Performance'`

### 51. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab2: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:818863764ea7`
- **Trigger / Repro:** `tab_switch(2) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab2: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'`

### 52. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab3: Low WCAG contrast (2.23:1 < 3.0) on Button '24-bit PCM'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:4d761c21f5c3`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Low WCAG contrast (2.23:1 < 3.0) on Button '24-bit PCM'`

### 53. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab3: Low WCAG contrast (2.23:1 < 3.0) on Button 'Extract to Downloads/Cavernize'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:626509459cc5`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Low WCAG contrast (2.23:1 < 3.0) on Button 'Extract to Downloads/Cavernize'`

### 54. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab3: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:431d542c835e`
- **Trigger / Repro:** `tab_switch(3) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab3: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'`

### 55. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Mode: Pro'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:ab043ca1b169`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Mode: Pro'`

### 56. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:8fbcd7b0b932`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'`

### 57. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:6146c0e0e406`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'`

### 58. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:41c751ade624`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'`

### 59. [MEDIUM] `ui_wcag_contrast_warn` (1x · `live_triggered`) — Tab4: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `ui_tree_q1` | **Sig:** `ui_tree_q1:ui_wcag_contrast_warn:636cf1d9b8f3`
- **Trigger / Repro:** `tab_switch(4) -> ui_tree(maxDepth=12,geometry=true)`
- **Evidence:** `Tab4: Low WCAG contrast (2.23:1 < 3.0) on TextView 'Open'`

### 60. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_text_ellipsized] Tab0:font_130: Short TextView 'Cavernize 3D' text is ellipsized (4 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:df7194cda1f4`
- **Trigger / Repro:** `tab_switch(0) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_text_ellipsized] Tab0:font_130: Short TextView 'Cavernize 3D' text is ellipsized (4 chars truncated)`

### 61. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_text_ellipsized] Tab0:font_130: Short TextView 'THK 1.00m MP' text is ellipsized (4 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:309230ee4beb`
- **Trigger / Repro:** `tab_switch(0) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_text_ellipsized] Tab0:font_130: Short TextView 'THK 1.00m MP' text is ellipsized (4 chars truncated)`

### 62. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_text_ellipsized] Tab1:font_130: Short TextView '01:34 / 03:09' text is ellipsized (6 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:c2dfb053489f`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_text_ellipsized] Tab1:font_130: Short TextView '01:34 / 03:09' text is ellipsized (6 chars truncated)`

### 63. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_text_ellipsized] Tab1:font_130: Short TextView 'Ear Distance' text is ellipsized (4 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:4415e5aa72bf`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_text_ellipsized] Tab1:font_130: Short TextView 'Ear Distance' text is ellipsized (4 chars truncated)`

### 64. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_text_ellipsized] Tab1:font_130: Short TextView 'Spatial X-Ray Inspector' text is ellipsized (8 chars truncated)
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:b5f942446593`
- **Trigger / Repro:** `tab_switch(1) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_text_ellipsized] Tab1:font_130: Short TextView 'Spatial X-Ray Inspector' text is ellipsized (8 chars truncated)`

### 65. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:2aceee13379f`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'`

### 66. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:93cd41e20dca`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'`

### 67. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:b6f237cb88c7`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[font_130/ui_wcag_contrast_warn] Tab4:font_130: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'`

### 68. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:f8f0209865cc`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'Open OmniAgent'`

### 69. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:5c852d686b6d`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'Total + UI'`

### 70. [MEDIUM] `reflow_layout_warn` (1x · `live_triggered`) — [landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `reflow_q3` | **Sig:** `reflow_q3:reflow_layout_warn:cac811e3fba8`
- **Trigger / Repro:** `tab_switch(4) -> ui_interact(reflow_probe fontScale=1.3 + landscape)`
- **Evidence:** `[landscape/ui_wcag_contrast_warn] Tab4:landscape: Low WCAG contrast (2.23:1 < 3.0) on Button 'View Log'`

### 71. [LOW] `logcat_warn` (1x · `live_triggered`) — AudioSystem: ioConfigChanged() closing unknown output 1989
- **Vector:** `V3_settings_chaos_fuzzer` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:a4fa8b02deb8`
- **Trigger / Repro:** `settings chaos burst + restore`
- **Evidence:** `W/AudioSystem(28031): ioConfigChanged() closing unknown output 1989`

### 72. [LOW] `logcat_warn` (1x · `live_triggered`) — avern.cavernize: AIBinder_linkToDeath is being called with a non-null cookie and no onUnlink callback set. Use AIBinder_DeathRecipient_setOnUnlinked to manage the lifetime of the cookie. This will bec
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:c401b79d82a1`
- **Trigger / Repro:** `real library edge torture`
- **Evidence:** `W/avern.cavernize(28031): AIBinder_linkToDeath is being called with a non-null cookie and no onUnlink callback set. Use AIBinder_DeathRecipient_setOnUnlinked to manage the lifetime of the cookie. This will become an abort.`

### 73. [LOW] `logcat_warn` (1x · `live_triggered`) — avern.cavernize: Failed to query component store for system resources: 6
- **Vector:** `V4_real_library_edge_hunter` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:c35a5dc75e37`
- **Trigger / Repro:** `real library edge torture`
- **Evidence:** `W/avern.cavernize(28031): Failed to query component store for system resources: 6`

### 74. [LOW] `applog_app_warn` (1x · `live_triggered`) — ComputeQoS: elevated Android thermal status (SEVERE) -> throttling background AI yield.
- **Vector:** `V6_ui_monkey_and_audit` | **Source:** `applogger_live` | **Sig:** `applogger_live:applog_app_warn:61f7db290e8a`
- **Trigger / Repro:** `ui_audit + Q1 geom + Q3 hostile stress/reflow`
- **Evidence:** `[2026-11-26 13:04:49.328] [WARN] [T#22] [APP] ComputeQoS: elevated Android thermal status (SEVERE) -> throttling background AI yield.`

### 75. [LOW] `logcat_warn` (1x · `live_triggered`) — HWUI: Incorrectly called buildLayer on View: OneUiFrostedCapsuleView, destroying layer...
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `logcat_live` | **Sig:** `logcat_live:logcat_warn:9d782b689fa1`
- **Trigger / Repro:** `UI latency & library search oracle`
- **Evidence:** `W/HWUI    (28031): Incorrectly called buildLayer on View: OneUiFrostedCapsuleView, destroying layer...`

## Historical Pre-Fix Archive (Prior Sessions in Retained AppLogger — Non-Blocking)

### 1. [CRITICAL] `exit_info_historical_excessive_resource_usage` (1x · `historical_archive`) — ApplicationExitInfo EXCESSIVE_RESOURCE_USAGE (pid=23629, CACHED, pss=857.0MB, ts=2026-11-26T11:18:29.0340000Z): [EXCESSIVE CPU USAGE] excessive cpu 291860 during 300051 dur=371965 limit=25
- **Vector:** `V9_ui_latency_and_search_oracle` | **Source:** `exit_info_q2` | **Sig:** `exit_info_q2:exit_info_historical_excessive_resource_usage:c2767eaa5c2b`
- **Trigger / Repro:** `runtime_telemetry.exitReasons`
- **Evidence:** `ApplicationExitInfo EXCESSIVE_RESOURCE_USAGE (pid=23629, CACHED, pss=857.0MB, ts=2026-11-26T11:18:29.0340000Z): [EXCESSIVE CPU USAGE] excessive cpu 291860 during 300051 dur=371965 limit=25`

### 2. [HIGH] `applog_ui_error` (2x · `historical_archive`) — UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_ui_error:48615f3823ae`
- **Trigger / Repro:** `historical_archive (2026-11-26 12:46:16.488)`
- **Evidence:** `[2026-11-26 12:46:16.488] [ERROR] [T#1] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`
  - *Also seen:* `[2026-11-26 12:46:16.565] [ERROR] [T#1] [UI] UI Error shown: Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 3. [LOW] `applog_app_warn` (1x · `historical_archive`) — OpenLocalPathWithFallbackAsync rejected 'corrupt.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_app_warn:b82a14cfbe8b`
- **Trigger / Repro:** `historical_archive (2026-11-26 12:46:16.486)`
- **Evidence:** `[2026-11-26 12:46:16.486] [WARN] [T#21] [APP] OpenLocalPathWithFallbackAsync rejected 'corrupt.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 4. [LOW] `applog_app_warn` (1x · `historical_archive`) — OpenLocalPathWithFallbackAsync rejected 'corrupt.mp3': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_app_warn:f922cabe2cbf`
- **Trigger / Repro:** `historical_archive (2026-11-26 12:46:16.550)`
- **Evidence:** `[2026-11-26 12:46:16.550] [WARN] [T#5] [APP] OpenLocalPathWithFallbackAsync rejected 'corrupt.mp3': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 5. [LOW] `applog_app_warn` (1x · `historical_archive`) — OpenLocalPathWithFallbackAsync rejected 'trunc.wav': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_app_warn:d4a320056f4a`
- **Trigger / Repro:** `historical_archive (2026-11-26 12:46:16.492)`
- **Evidence:** `[2026-11-26 12:46:16.492] [WARN] [T#8] [APP] OpenLocalPathWithFallbackAsync rejected 'trunc.wav': Could not decode this audio file. It may be corrupted or use an unsupported codec.`

### 6. [LOW] `applog_ui_warn` (1x · `historical_archive`) — [APP] OpenLocalPathWithFallbackAsync rejected 'empty.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.
- **Vector:** `V1_log_forensics` | **Source:** `applogger_history` | **Sig:** `applogger_history:applog_ui_warn:0eccc2493778`
- **Trigger / Repro:** `historical_archive (2026-11-26 12:46:16.484)`
- **Evidence:** `[2026-11-26 12:46:16.484] [WARN] [UI] [APP] OpenLocalPathWithFallbackAsync rejected 'empty.flac': Could not decode this audio file. It may be corrupted or use an unsupported codec.`
