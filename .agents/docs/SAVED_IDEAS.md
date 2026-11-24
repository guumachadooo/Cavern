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

