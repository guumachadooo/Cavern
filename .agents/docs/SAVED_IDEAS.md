# Saved Ideas & Archived Effects (`SAVED_IDEAS.md`)

> This file stores promising visual ideas, formulas, animations, and prototypes created during CavernizeAndroid development that were shelved temporarily to prioritize other goals or stay faithful to a native OS specification (such as stock One UI 9). Check here whenever the user asks to revisit archived effects.

---

## 1. Elastic / Concave Slider Wall Deformation (`EvalWallRightX`)
- **Origin:** `v2.5.16` in `CavernSamples/CavernizeAndroid/Theme/OneUiTheme.cs` (`OneUiSeekBar`).
- **Behavior:** When a capsule slider's filled track approaches the left wall (`0 < fillRight < h`), its right edge transitions smoothly through a progressive superellipse (`p = 2.0 -> 3.4`), bends into a subtle inward concave dent in the middle while preserving rounded top/bottom shoulders, and finally flattens vertically at zero.
- **Reason archived:** Stock One UI 9 (`SecRoundedCornerSeekBarDrawable` / `SecBrightnessSliderView`) simply clips a rounded rectangle inside the outer capsule `clipPath` without concave wall bending. The user requested removing it from the main slider for exact One UI 9 parity, while preserving the snippet for potential future reuse (noting that a softer dent such as `dent = -0.08f` instead of `-0.18f` may look even cleaner).

### Reference C# Implementation
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
