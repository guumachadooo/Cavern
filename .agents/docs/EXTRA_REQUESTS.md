# Extra Request Queue (`/btw` Threads)

> GATE|check pre-finish, `git commit` & `git push`: implement pending items -> move to `## Completed Requests` -> restore `(No pending requests.)`.
> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/filler`, ops `-> | &&`, verbatim `path:sym`). Format: `- REQ|<scope> -> <goal/constraint>`.

## Pending Extra Requests
(No pending requests.)

## Completed Requests
- REQ|upmix-ui-simplify -> DONE (`AppSettingsStore.cs` forced `ParametricStft=true` && `MainActivity.SettingsTab.cs` unified `OneUiInlineSelector upmixModeSelector` Direct/Surround 7.1/Full 3D + gated `atmosAwareUpmixSection` + fixed SignalChain summary + `PlayerMusicTab.cs` `SyncUpmixModeUi`)
- REQ|lyrics-sing-ondemand -> DONE (`MainActivity.PlayerMusicTab.cs` collapsed `musicLyricsSingDock` by default + added compact `musicLyricsSingToggleBtn` pill in `lyricsHeader` w/ active accent & recoil + auto-state indication)
- REQ|overhaul-quality-gate -> DONE (`1250/1250` EN/PT-BR `AndroidStrings.cs` parity + One UI 9 layout safety + `MOD-WIRING-GATE` + `MOD-UX-CRITIQUE` + `verify_universal.py --build --git-stat` PASS)
- REQ|v2.9.31-stability-dropouts-audit -> DONE (`MainActivity.cs` `ConfigChanges`, `AndroidMediaFallbackDecoder` `.Release()`+FD+FFmpeg fallback & localized errors, `StartLosslessTwinDiscoveryAsync` sync dedup, `AndroidAudioOutput` `nextPreferred` fix/`48k` HAL rate/`1500ms` idle grace/`350ms` diag throttle, removed `ReattachPlayerTrack()` dropouts in `SettingsTab`/`PlayerMusicTab`, `cavernize_path_a_aaudio.cpp` `AAUDIO_USAGE_MEDIA`/`burst*4`/`waitForStateChange`/atomic `io_inflight`)
- REQ|library-permanent-freeze-on-open -> DONE (`AudioQualityAnalyzer.EvaluateFast(allowIo:false)` zero sync I/O + corrupt JSON recovery + `.Release()`, `MediaLibraryService` `SemaphoreSlim(3)`+`inflightThumbs`+API29 `LoadThumbnail`+corrupt JSON recovery+`.Release()`, `MainActivity.PlayerMusicTab.cs` off-UI-thread `ScheduleLibraryLoadAndRenderAsync` w/ `libraryIndexRequestSeq`)
- REQ|phase2-subagent-orchestration -> DONE (executed `MOD-RECON-SWEEP` -> `MOD-IMPL` -> `MOD-WIRING-GATE` -> `MOD-UX-CRITIQUE` -> `MOD-VERIFY` w/ sequential PRoot builds/pushes)
