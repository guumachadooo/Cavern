---
name: cavernize-ui-oneui9
description: >-
  Guides UI additions, layout fixes, localization updates, and custom Canvas components in
  CavernizeAndroid using Samsung One UI 9 visual conventions and Android layout safety guards.
  Use when editing MainActivity tabs, OneUiTheme, RoomSimulatorView, or AndroidStrings.
---

# One UI 9 & Layout Checklist for CavernizeAndroid

Use this checklist when changing any visual element or screen in `CavernSamples/CavernizeAndroid/`.

## 1. Review Context First
- Read [ONEUI9_REFERENCE.md](../../docs/ONEUI9_REFERENCE.md) when touching slider physics, wave seekbars, dark-mode surface neutrality, or dynamic wallpaper palette behavior.
- Read [PROJECT_HISTORY.md](../../docs/PROJECT_HISTORY.md) to respect recent structural choices (header simplicity, `Queue` placement in `ConverterTab`, contextual `?` help toggles, Settings card ordering) unless the user specifically asks to reorganize them.

## 2. Preserve All Features
- Never strip or hide original Cavernize capabilities during a UI cleanup; expose advanced controls cleanly (e.g., collapsible sections or contextual help `?`) instead of deleting them.

## 3. Anti-Clipping & Scroll Safety Rules
- **Horizontal button rows:** Set `BaselineAligned = false` and `SetGravity(GravityFlags.CenterVertical)` on every `LinearLayout` (`Orientation.Horizontal`) that holds buttons or multi-element pills.
- **Pill buttons:** Configure `SetSingleLine(true)` and `Ellipsize = TextUtils.TruncateAt.End` on compact action buttons (`PrimaryButton`, `SecondaryButton`, `PillButton`) or place overflow button sets inside a `HorizontalScrollView`.
- **Custom touch views inside `ScrollView`:** Maintain `touchSlop` vertical-vs-horizontal disambiguation and `Parent?.RequestDisallowInterceptTouchEvent(...)` in `RoomSimulatorView`, `OneUiSeekBar`, and `OneUiWaveMediaSeekBar`.
- **Edge-to-Edge & Predictive Back (API 35+):** Handle `WindowInsets.Type.SystemBars() | DisplayCutout() | Ime()` via `SetOnApplyWindowInsetsListener` (`navFrostedHost`, `bottomScrimView`, tabs, and fullscreen video cutouts; never use deprecated `SetStatusBarColor`/`SetNavigationBarColor`/`SystemUiVisibility`/`SetFitsSystemWindows(true)`), and dynamically register API 33+ `IOnBackInvokedCallback` only while an internal back-step exists.

## 4. Visual Consistency & Localization
- Replace stock Android dialogs/spinners with `OneUiModalDialog` and `OneUiInlineSelector`.
- Use crisp vector icons (`OneUiVectorIcons` / `OneUiIconBadge`) instead of emojis.
- Add or update strings in **both** `CreateEnglish()` and `ApplyPortuguese()` (and keep `ApplyHungarian` / `ApplyChinese` intact) inside [AndroidStrings.cs](../../../CavernSamples/CavernizeAndroid/Language/AndroidStrings.cs). Do not leave untranslated English labels visible in `pt-BR`.
- Include clear, beginner-friendly `?` card help text for any new card or control group.
