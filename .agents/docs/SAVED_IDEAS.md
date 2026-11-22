# Saved Ideas & Prototypes (`SAVED_IDEAS.md`)

> Deferred ideas/designs/algos/snippets for later revisit.
> STYLE|lossless symbolic telegraphic EN ONLY (`!articles/copulas/filler`, ops `-> | && || !`, verbatim `path:sym`/snippets).
<!-- FORMAT: - IDEA|<name> -> <why deferred> | <key mechanism/snippet> -->

---

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
