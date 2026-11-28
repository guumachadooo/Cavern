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

- IDEA|`Full-App SOTA Subsystem Review Blueprint` (`BP_FULL_APP_SOTA_AUDIT`, queued in `MASTER_OVERHAUL_PLAN.md` Theme T6) -> systematic whole-app audit after T1–T5 overhaul -> partition entire CavernizeAndroid codebase into explicit functional domains (`Audio I/O & ProAudio`, `Codecs & Containers`, `Spatial DSP & Virtualizer`, `Neural AI & Cache`, `Player & Library`, `Studio & Export`, `Converter & Extractor`, `One UI 9 UX & Motion`, `Live Bridge & QA`), score each subsystem against current SOTA (latency, stalls/freezes, acoustic accuracy, architecture cleanliness, defect density), and issue explicit `KEEP | REFACTOR | REPLACE` verdicts + prioritized defect remediation backlog.

- IDEA|`Omniscient Cortex Phase-B Interactive Training & Preference Calibration` (`BP_OMNISCIENT_SPATIAL_OBJECT_CORTEX.md` `SOTA-v2` W4B / `PHASE3_VOCAL_CORTEX_ROADMAP.md`) -> deferred until Autonomous Phase-A infra (`ComponentBehaviorProfile` registry + deterministic/golden-bench foundation) is 100% shipped with side-by-side legacy fallback -> user-in-the-loop preference calibration, optional DAW `.cvfx` capture, & local INT4 NPU director activation without ever requiring upfront user effort or breaking existing working DSP paths.

- IDEA|50pct-headroom-and-stage1-bug-backlog -> strict bug-first separation (Stage 1 bugs/regressions BEFORE Stage 2/3 features; `RAW_SEEDS_ONLY -> MANDATORY FULL SOTA BLUEPRINT` w/ STORM + blind `pro [L3]` Zwicky/TRIZ + Red-Team Pre-Mortem + Wave-0 spikes; `!blind-impl`) | 50% Headroom Gate (`<=50%` S25U CPU RT `<=40-50%` blk/0 xruns, UI `P95<=4-6ms`/0 StrictMode I/O, RAM/PSS 6GB-class ceiling via streaming; offline neural scales wall-time only) + Scarlett Headphone-Out->In1/2 unbalanced Y-cable loopback (Direct Monitor/48V/INST/AIR OFF; dry bypass baseline auto-calibrating L/R swap, gain offset, 60Hz hum, crosstalk; evaluate delta only) + `v2.9.104` bug seed list (crashes in `cavernize_log.txt`, FLAC/car & AI 3D audio stutters + ~1s AI 3D cache-load freeze on Atmos, `IsPlayTabAiActive()` false-active pref bug `MainActivity.NeuralWorkflows.cs:275-283`, Library slow open/Export-picker workaround & Albums/Tracks/Artists/Folders jank, `NowPlayingSheet` dark `alpha=248` leaking unblurred Library vs opaque `OneUiBackdropView` + `OneUiFrostedCapsuleView` frozen `ScheduleDeferredBlur` snapshots vs hw `Window.SetBackgroundBlurRadius`, tiny pull-down dismiss hit zone, separate sheet vs Mini-Player<->NowPlaying morphing, dry Cover/Lyrics/3D Stage transitions `animate:false`, Export capsule-in-capsule, redundant Lyrics top tab, Lyrics missing `OverScroller`/`VelocityTracker` fling, 3D Stage graph/height/head scroll conflict & horizontal-first lock + hide inactive `DynamicMotion`, L/R same-side collapse in 3D motion, audit skipped overhaul/blueprint items).

- IDEA|stage2-ux-and-hardware-unification -> deferred post-Stage-1 (`RAW_SEEDS_ONLY -> MANDATORY FULL SOTA BLUEPRINT` w/ STORM + blind `pro [L3]` Zwicky/TRIZ + Red-Team Pre-Mortem + Wave-0 spikes; `!blind-impl`) | unify MagLS Ambisonics (`MainActivity.Settings.Spatial.cs:178`) vs Minimum-Phase HRIR (`MainActivity.Settings.Headphones.cs:491`) vs BRIR/Studio (`Slot 5A`) w/ real mutual exclusion && ensure `HeadphoneVirtualizer` invariant under Speaker XTC (`SpatialPipeline.cs:672`, `Coordinator.cs:111`); unify Local+Cloud AI Model selector in Play & Settings/Upmix (`!Open AI Settings` split); strategic contextual "right-click" lightweight popover (e.g. `AI 3D` Local+Cloud + `"Always use"` + long-press reset, strategic spots only); smart BT/USB device detection beyond renameable BT name (`AudioDeviceInfo.Type`, BT COD/Class, OUI/MAC, per-device override) + XTC distance slider in cm (`>15-40 cm`).

- IDEA|stage3-holistic-flac-to-dolby-atmos-ai-cortex -> deferred post-Stage-2 (`RAW_SEEDS_ONLY -> MANDATORY FULL T3 SOTA BLUEPRINT` w/ STORM + blind `pro [L3]` Zwicky/TRIZ + Red-Team Pre-Mortem + Wave-0 spikes; `!blind-impl`; `!limited` to basic mixing/mastering/motion — MUST cover ALL spatial, acoustic, perceptual, dynamic, spectral, timbral, mixing, mastering, structural, & artistic dimensions differentiating stereo FLAC from authentic Dolby Atmos, discovering "real deal" SOTA mechanisms beyond initial brainstorm) | seeds to challenge/deepen: unified Hybrid 7.1.4 Bed/Dynamic-Anchors + Full-360° Dynamic Objects (fix `NeuralStemSpatializer.cs:776-795` `7/8 Z>0` front bias; real rear `Z<0`, ceiling `Y->1.0`, anchor<->floating transitions, dedicated ceiling/bed reverb); faithful 2D->3D stereo stage when AI motion OFF (preserve stereo width e.g. piano bass-L/treble-R via stereo pairs/sub-bands + spread, front externalization `!in-head`); non-exponential `Set-Transformer/Cross-Stem Attention` (`1..20+` variable stems, shared encoder, on-the-fly Random Stem Merging/Dropout + Bleed Augmentation for 2/4/6/12-stem parity; empirically sized ~20-60+ MB within 50% RT headroom, `!artificial 8 MB cap`); rich stereo spectrogram input + lossless-preserving full-dimension DSP controls (`X,Y,Z,spread`, bed/reverb, `Δgain`, `ΔEQ`, dynamic expansion/transient restoration + deeper SOTA differentiable DSP); offline forensic ground-truth analyzer on `500-2,000` paired `FLAC<->Atmos E-AC-3 JOC` (`GCC-PHAT` sub-ms sync + remix filter, LUFS normalization, unity energy/phase); `other` unpacking via cloud 4 macro-families (`Wind A54`, `Bowed Strings A52`, `Synth A88`, `SFX/Percussion DnR/BandIt A56/A45` + `Karaoke A49` + `Reverb A22` keeping `IN-noreverb` for bed/ceiling + optional `DrumSep A37`) + zero-ML STFT `HPSS+Direct/Diffuse+Pan-Sector` partition-of-unity splitter on residual `other`.

- IDEA|stage3-serverless-mvsep-graph-qa-chaos-and-workflow -> deferred (`RAW_SEEDS_ONLY -> MANDATORY FULL SOTA BLUEPRINT` w/ STORM + blind `pro [L3]` Zwicky/TRIZ + Red-Team Pre-Mortem + Wave-0 spikes; `!blind-impl`) | personal scale-to-zero Serverless GPU (Modal/RunPod, pay-per-sec, R$0 idle) running configurable MVSEP v1 Preset Graph (`INV`, `PC[mag,phase]`, `MIX`, `ENS(avg_wave,w)` w/ verified open weights `A40_142 pcunwa/BS-Roformer-HyperACE`, `A48_11 becruily/mel-band-roformer-deux`, `A48`, `A46 SCNet XL`, `A49`, `A44`, `A37`, `A22`, `A47`, `A63 6-stem`, `A56/A45`, `A54`, `A52`; `A40_171` 124-band flagship substituted w/ top open checkpoint) + custom `Preset #12 Cavernize Atmos Studio`; Bug Hunter v3 Chaos/Inexperienced-User stateful simulator (fixed demo `1 FLAC + 1 Atmos`, random/adversarial toggle stress, 50% headroom gate, HARD BLOCK on MVSEP cloud API / 0 credits); zero-token horizontal subagent orchestration (`run_workflow` fine fan-out over fat 2-layer subagents).

