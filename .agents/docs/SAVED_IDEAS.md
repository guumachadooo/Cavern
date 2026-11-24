# Saved Ideas & Prototypes (`SAVED_IDEAS.md`)

> Deferred ideas/designs/algos/snippets for later revisit.
> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || !`, verbatim `path:sym`/snippets).
<!-- FORMAT: - IDEA|<name> -> <why deferred> | <key mechanism/snippet> -->

---

- IDEA|termux-auto-reopen-and-resume -> PRoot crash/kill diagnosis & self-healing resume (deferred Android/Termux app automation) | root-cause: Android 12+ Phantom Process Killer (32-child cap hit during 200+ subprocess `--full` plugin tests) + high RAM pressure (`8.7/11.1 GB` + `7.5 GB` swap) + missing wakelock; fixes: (1) S25U Developer Options `Disable child process restrictions` + Termux battery `Unrestricted` + `termux-wake-lock`; (2) Termux PRoot watchdog loop (`while true; do proot-distro login ...; sleep 1; done`) auto-restarting `language_server`/`antigravity`/`omni_agent_daemon.py` on crash; (3) OmniAgent `ForegroundService`/Tasker healthcheck pinging daemon loopback (`127.0.0.1:47921`) & relaunching Termux intent on timeout; (4) auto-resume reads `.agents/docs/HANDOFF.md` + `EXTRA_REQUESTS.md` via `SessionStart` (`HOST|`/`HANDOFF|`) to summarize prior state & resume active wave immediately w/o manual user re-prompting.
- IDEA|`EvalWallRightX` (`v2.5.16` `OneUiTheme.cs:OneUiSeekBar`) -> elastic concave wall deformation when capsule progress `0 < fillRight < h` (`p=2.0->3.4` superellipse + inward dent `-0.18f` / subtler `-0.08f`) -> deferred for exact stock One UI 9 parity (`SecRoundedCornerSeekBarDrawable` uses outer semicircular `clipPath` over `drawRoundRect(..., fillRight * 0.5f)` w/o concave bend).
```csharp
/// <summary>
/// Original concave deformation (dent = -0.18f) and subtler suggested tuning (dent = -0.07f..-0.09f)
/// for organic wall collision when a circular thumb meets the left capsule boundary.
/// t = Math.Clamp(fillRight / h, 0f, 1f); u in [-1, 1]; r = h * 0.5f.
/// </summary>
static float EvalWallRightX(float t, float u, float r) {
    float au = Math.Clamp(MathF.Abs(u), 0f, 1f);
    if (t >= 0.50f) {
        float s = (t - 0.50f) / 0.50f;
        float p = 2.0f + 1.4f * (1.0f - s);
        float shoulder = r * (0.90f + 0.10f * s);
        float sag = r * MathF.Pow(s, 0.85f);
        float prof = MathF.Sqrt(Math.Max(0f, 1.0f - MathF.Pow(au, p)));
        return shoulder + sag * prof;
    }
    if (t >= 0.12f) {
        float s = (t - 0.12f) / 0.38f;
        float bell = MathF.Sin(MathF.PI * s);
        float shoulder = r * (0.32f + 0.58f * s);
        // Subtler alternative: ear = 0.03f and dent = -0.08f instead of -0.18f
        float ear = 0.06f * r * bell * MathF.Pow(au, 2.2f);
        float dent = -0.18f * r * bell * (1.0f - au * au);
        return shoulder + dent + ear;
    }
    return r * 0.32f * (t / 0.12f);
}
```

- IDEA|`W28 Turnkey Level 2 Rebranding` (`brand_config.json`, `.agents/scripts/apply_rebrand.py`, `BrandIdentity.cs`) -> deferred by user (`W28` skipped pre-`W29`/Phase 3) -> centralized identity manifest (`BrandDisplayName`, `BrandNamespacePrefix`, `AndroidApplicationId`, `NativeLibName`, `ExportFolderName`, `ApkArtifactName`, `UpdateTagPrefix`, `FileProviderAuthority`, `NotificationChannelPrefix`) + idempotent Python dry-run/apply/verify script + brand-agnostic `AndroidStrings.cs` tokens + `NOTICE.md` OSS attribution (preserving historical `PROJECT_HISTORY.md`/`SPATIAL_ROADMAP.md` *Cavern* records).

- IDEA|`Omniscient Spatial Object Cortex (3-Layer Atmos/IAMF Choreography Engine)` (`Cavern/Spatial/`, `Cavern.Format/Common/Metadata/ADMMetadataExporter.cs`, `Cavern.Format/Output/Environment/IamfEnvironmentWriter.cs`, `CavernSamples/Reusable/Cavernize.Logic/Spatial/`, `.agents/docs/PHASE3_VOCAL_CORTEX_ROADMAP.md`) -> deferred for next chat via `plan_tool.py blueprint` (`sota-blueprint`) -> evolve static stem spatializer into 3-layer musical+semantic Atmos/IAMF object director sharing Plane-1 `.ntc` offline track-prep w/ Vocal Cortex (zero per-song retraining; avoid naive video-LLM cost or noisy 8D circular sweeps or untrusted online BPM DBs):
  - **Layer 1 — 100% Audio-Derived Forensics (Offline Plane 1):** reuse neural stems (`Vocals`, `Drums`, `Bass`, `Other`), `DrumStem` variable-BPM/beat-grid tracker (handles mid-song tempo shifts missed by online DBs), structural section timeline (`Intro|Verse|PreChorus|Chorus|Bridge|Drop|Outro`), stem ILD/stereo pan tracker, onset/drum-fill/riser/brightness detectors, & lightweight `<25 MB` ONNX genre/mood/energy classifier.
  - **Layer 2 — Semantic Lyric & Context Director:** fuse W49 line/syllable-synced lyrics + optional verified online metadata + Layer-1 section/tempo/genre map -> compact INT4 `1B-3B` local/hybrid LLM outputting strictly schema-constrained JSON **Director Cues** (e.g. `"left, right"` -> `PING_PONG_LR`, `"high above"` -> `ELEVATION_ASCEND`, chorus -> `STAGE_BLOOM_360`), calibrated against real professional Atmos object-motion statistics/priors + lightweight user preference loop (thumbs up/down tuning, 0 heavy training).
  - **Layer 3 — Physics & Psychoacoustic Trajectory Compiler + Governor (RT Plane 2 + Export):** compiles beat grid + stems + Director Cues into zipper-free Catmull-Rom/Hermite `X/Y/Z` splines clamped by Psychoacoustic Governor (Lead Vocal center anchor `<=10-15 deg`, Kick/Sub-Bass locked low, angular slew cap `<=45 deg/s`, `Subtle/Musical/Cinematic/Orbit` presets) -> drives live `SpatialSceneHub`/3D Room X-Ray AND offline `StudioBatchExporter` `ADM BWF` (`audioBlockFormat`) + `Eclipsa IAMF` dynamic objects.

- IDEA|`Studio Spatial Export 3-Tier Compression Selector (PCM | Lossless Compressed | Normal Lossy)` (`CavernSamples/CavernizeAndroid/MainActivity.StudioExport.cs`, `CavernSamples/Reusable/Cavernize.Logic/Rendering/StudioBatchExporter.cs`, `CavernSamples/CavernizeAndroid/Language/AndroidStrings*.cs`) -> deferred user request from 2026-11-24 chat -> add 3-tier codec selector in Studio Export Builder respecting container specs (reducing 361 MB PCM exports cleanly):
  - **Tier 1 — Uncompressed Master (PCM):** `Headphone3D`/`.wav` 24-bit, `Cinema714` `.wav` RF64, `DolbyAtmosAdm` ADM BWF `.wav` RF64, `EclipsaIamf` `.iamf` (`ipcm`), `Ambisonics360` `.amb` WAVE-EX.
  - **Tier 2 — Lossless Compressed (~45-65% smaller, 0 quality loss):** `Headphone3D` -> 24-bit `.flac`; `EclipsaIamf` -> `.iamf` w/ native `fLaC` substreams; `Cinema714` (12 ch) & `Ambisonics360` (16 ch) -> WavPack `.wv` (since FLAC spec caps at 8 ch) or `IAMF fLaC`; `DolbyAtmosAdm` -> clear spec note in UI (ITU-R BS.2076 ADM requires PCM; no OSS TrueHD+Atmos encoder) w/ Eclipsa `fLaC` suggestion.
  - **Tier 3 — Normal Lossy (~85-95% smaller, portable):** `Headphone3D` -> Opus `.opus` or AAC `.m4a`; `EclipsaIamf` -> `.iamf` w/ `Opus` (~12-15 MB/track); `Cinema714`/`Ambisonics360` -> Opus Multichannel / Ambisonics RFC 8486 (`.opus`/`.mka`) + optional E-AC-3/AC-3 (up to 7.1/5.1).

- IDEA|`Pending Device Log Fixes (2026-11-24 cavernize_log.txt Forensic Sweep)` (`/sdcard/Download/Cavernize/Logs/cavernize_log.txt`, `MainActivity.StudioExport.cs`, `StudioBatchExporter.cs`, `AudioFile.cs`, `IamfEnvironmentWriter.cs`, `IamfDecoder.cs`, `AndroidAudioOutput.cs`) -> root causes found in 2026-11-24 log inspection (fixes deferred):
  1. **Studio AI Export `GC=3749 MB` OOM Process Kill (`01:40:48 -> 01:42:03`) + False Cancel Error (`01:38:58`):** multi-channel `float[][]` buffers + cached neural stems stay live until `OnTrimMemory(UiHidden)` triggers Android LMK process death -> dispose/null intermediate channel/stem buffers between pipeline stages + attach export ForegroundService; also fix `StudioBatchExporter.cs:300` + `MainActivity.StudioExport.cs:668` throwing `InvalidOperationException("Export cancelled.")` UI popup on normal `OperationCanceledException` cancel.
  2. **`.iamf` Exported Files Fail to Re-Open (`23:04:04`, FFmpeg `Failed to read obu type 1`):** `AudioFile.cs:71-112` lacks `case "iamf":` / `case "amb":` native reader routing; AND `IamfEnvironmentWriter.cs:172-178` (+ mirror `IamfDecoder.cs:428-439`) writes `ambisonics_config()` fields out of AOMedia IAMF v1.0.0 spec order (writes `output_channel_count` where FFmpeg expects `ambisonics_mode` `uleb128` first, plus missing `coupled_substream_count` u8).
  3. **Scarlett 2i2 Gen 4 Minor Polish (`01:18:28`, `01:21:21`, `01:13:30`):** 1st switch to `MicSource=PhoneMic` once retained USB `InId=705` before locking `InId=22`; `Path B (libusb)` re-attached rapidly on repeated USB route broadcasts; `Gen4FcpVerified=False` (`FW=0`) handshake still pending completion.

