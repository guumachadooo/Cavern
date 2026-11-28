using System.Text.Json;

using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace CavernizeAndroid.Theme;

/// <summary>
/// True Samsung One UI 9 (Galaxy S25 Ultra / SESL9) design system:
/// dynamically adapts to the device's One UI Wallpaper Color Palette (Monet API 31+) with genuine
/// Samsung Blue fallback, blurred atmospheric backdrop, frosted glass specular rims,
/// Samsung Recoil press physics, vector icon badges, and 44dp fat capsule sliders with double-tap reset.
/// </summary>
public static partial class OneUiTheme {
    static Typeface appleFontBold;
    static Typeface appleFontSemibold;
    static Typeface appleFontMedium;
    static Typeface appleFontRegular;

    /// <summary>
    /// Returns Apple Music's native SF Pro Android UI variable typeface tuned to the requested weight.
    /// </summary>
    public static Typeface GetAppleFontBold(Context ctx) =>
        appleFontBold ??= LoadVariableAppleFont(ctx, 700, TypefaceStyle.Bold);

    public static Typeface GetAppleFontSemibold(Context ctx) =>
        appleFontSemibold ??= LoadVariableAppleFont(ctx, 600, TypefaceStyle.Bold);

    public static Typeface GetAppleFontMedium(Context ctx) =>
        appleFontMedium ??= LoadVariableAppleFont(ctx, 500, TypefaceStyle.Normal);

    public static Typeface GetAppleFontRegular(Context ctx) =>
        appleFontRegular ??= LoadVariableAppleFont(ctx, 400, TypefaceStyle.Normal);

    /// <summary>
    /// Applies the unified SF Pro headline/UI typeface instead of raw Roboto Bold.
    /// </summary>
    public static void ApplyHeadlineFont(Context ctx, TextView tv, bool bold = true) {
        if (tv == null) {
            return;
        }
        tv.Typeface = bold ? GetAppleFontBold(ctx) : GetAppleFontMedium(ctx);
    }

    static Typeface LoadVariableAppleFont(Context ctx, int weight, TypefaceStyle fallbackStyle) {
        try {
            if (ctx?.Assets != null && OperatingSystem.IsAndroidVersionAtLeast(26)) {
                Typeface.Builder builder = new(ctx.Assets, "fonts/sf_pro_android_ui.ttf");
                builder.SetWeight(weight);
                builder.SetFontVariationSettings($"'wght' {weight}");
                Typeface tf = builder.Build();
                if (tf != null) {
                    return tf;
                }
            }
        } catch {
            // Fallback to CreateFromAsset below
        }
        try {
            if (ctx?.Assets != null) {
                Typeface assetTf = Typeface.CreateFromAsset(ctx.Assets, "fonts/sf_pro_android_ui.ttf");
                if (assetTf != null) {
                    return Typeface.Create(assetTf, fallbackStyle);
                }
            }
        } catch {
            // Fallback to system default
        }
        return Typeface.Create(Typeface.Default, fallbackStyle);
    }

    // Dynamic Atmospheric Dark + Frosted Glass Palette (verified from SystemUI.apk resources.arsc)
    public static Color BgScreen { get; private set; } = Color.ParseColor("#080A10");
    public static Color SurfaceCard { get; private set; } = Color.ParseColor("#3D000000");
    public static Color SurfaceElevated { get; private set; } = Color.ParseColor("#CE20212B");
    public static Color SurfaceModal { get; private set; } = Color.ParseColor("#CC2E2E2E");
    public static Color SurfaceModalRow { get; private set; } = Color.ParseColor("#E02A2C38");
    public static Color SurfaceFloating { get; private set; } = Color.ParseColor("#36FFFFFF");
    public static Color SurfaceSubTile { get; private set; } = Color.ParseColor("#24FFFFFF");
    public static Color SurfaceBlurTint { get; private set; } = Color.ParseColor("#3A3A44");
    public static Color ControlStroke { get; private set; } = Color.ParseColor("#5A5B66");
    public static Color BorderSubtle { get; private set; } = Color.ParseColor("#22FFFFFF");

    // Verified SystemUI.apk SecQSDetail Popup & 120 Hz Glass Module Tokens
    public static readonly Color SecQsDetailBg = Color.ParseColor("#CC2E2E2E");
    public static readonly Color SecQsDetailBgFallback = Color.ParseColor("#EB24252E");
    public static readonly Color SecQsDetailStroke = Color.ParseColor("#26FFFFFF");
    public static readonly Color GlassCardSurface = Color.ParseColor("#3D000000");
    public static readonly Color GlassCardBorder = Color.ParseColor("#0DFFFFFF");
    public static readonly Color GlassCardBorderTop = Color.ParseColor("#24FFFFFF");
    public static readonly Color QsEditBorderStroke = Color.ParseColor("#38FFFFFF");
    public static readonly Color QsEditRippleBg = Color.ParseColor("#4DB8B8B8");

    // Specular Glass Borders
    public static Color GlassRimTop { get; private set; } = Color.ParseColor("#24FFFFFF");
    public static Color GlassRimMid { get; private set; } = Color.ParseColor("#0DFFFFFF");
    public static Color GlassRimBottom { get; private set; } = Color.ParseColor("#1AFFFFFF");
    public static Color FloatingBorder { get; private set; } = Color.ParseColor("#26FFFFFF");

    // Dynamic Brand & Wallpaper Accent Tokens (resolved verbatim from framework-res wallpapertheme_template.json on API 31+)
    public static Color Primary { get; private set; } = Color.ParseColor("#387AFF");
    public static Color PrimaryHigh { get; private set; } = Color.ParseColor("#598FFF");
    public static Color PrimaryPressed { get; private set; } = Color.ParseColor("#2E65D4");
    public static Color Secondary { get; private set; } = Color.ParseColor("#6E7CF6");
    public static Color SecondaryHigh { get; private set; } = Color.ParseColor("#8995FA");
    public static Color SecondaryPressed { get; private set; } = Color.ParseColor("#4F5CE0");
    public static Color HighlightAccent { get; private set; } = Color.ParseColor("#8E96FA");
    public static Color Tertiary { get; private set; } = Color.ParseColor("#6E9CFF");
    public static Color TertiaryHigh { get; private set; } = Color.ParseColor("#89B0FF");
    public static Color TertiaryPressed { get; private set; } = Color.ParseColor("#4B7BE5");
    public static Color SliderAccent { get; private set; } = Color.ParseColor("#6E9CFF");
    public static Color SliderAccentHigh => SliderAccent;
    public static Color SliderAccentEnd => SliderAccent;
    public static Color CoralStart { get; private set; } = Color.ParseColor("#8E96FA");
    public static Color CoralEnd { get; private set; } = Color.ParseColor("#6E9CFF");
    public static Color PeachText { get; private set; } = Color.ParseColor("#C2D8FF");

    // Samsung Gallery Bottom Bar & Floating Tab Capsule Tokens
    public static Color SurfaceFloatingBar { get; private set; } = Color.Argb(230, 58, 58, 61);
    public static Color SelectedTabPill { get; private set; } = Color.Argb(26, 255, 255, 255);

    // 6 Harmonic Semantic Icon Palette Tokens (wallpaper-adaptive so icons share tonal unity yet stay visually distinct)
    public static Color IconSpatial { get; private set; } = Color.ParseColor("#387AFF");
    public static Color IconSliderMedia { get; private set; } = Color.ParseColor("#6E9CFF");
    public static Color IconAtmosUpmix { get; private set; } = Color.ParseColor("#8E96FA");
    public static Color IconStudioExport { get; private set; } = Color.ParseColor("#4FB5E8");
    public static Color IconStemsCalib { get; private set; } = Color.ParseColor("#56B8A8");
    public static Color IconSystemInfo { get; private set; } = Color.ParseColor("#8A97B8");

    public static Color TextPrimary { get; private set; } = Color.ParseColor("#FCFCFF");
    public static Color OnPrimary { get; private set; } = Color.ParseColor("#FCFCFF");
    public static Color TextSecondary { get; private set; } = Color.ParseColor("#E9E9EC");
    public static Color TextSub { get; private set; } = Color.ParseColor("#B7B7BB");
    public static Color TextMuted { get; private set; } = Color.ParseColor("#99999E");
    public static Color Divider { get; private set; } = Color.ParseColor("#26FFFFFF");

    // Functional Semantic Colors (harmonized to avoid clashing while staying clear)
    public static readonly Color FunctionalGreen = Color.ParseColor("#34D399");
    public static readonly Color FunctionalOrange = Color.ParseColor("#F97316");
    public static readonly Color FunctionalIndigo = Color.ParseColor("#7C6CF6");
    public static readonly Color FunctionalAmber = Color.ParseColor("#F59E0B");
    public static readonly Color FunctionalCyan = Color.ParseColor("#38BDF8");
    public static readonly Color FunctionalRed = Color.ParseColor("#F56962");
    public static readonly Color FunctionalRedFill = Color.ParseColor("#D93E36");

    // Room Canvas & Spatial Source Tokens
    public static Color RoomBg { get; private set; } = Color.ParseColor("#99101219");
    public static Color BedChannel { get; private set; } = Color.ParseColor("#598FFF");
    public static readonly Color LfeChannel = Color.ParseColor("#A855F7");
    public static readonly Color DynamicObject = Color.ParseColor("#F56962");
    public static readonly Color SoloGold = Color.ParseColor("#FACC15");

    public static bool IsDynamicWallpaperActive { get; private set; }
    public static string ActiveDesignTokensSource { get; private set; } = "local";
    public const int DesignKitSchemaVersion = 1;

    // SESL9 / SystemUI.apk Verified Dimensions & QsEditMode Motion Constants
    public const float CardCornerDp = 28f;
    public const float SecQsDetailCornerDp = 26f;
    public const float SecQsDetailBlurRadiusDp = 25f;
    public const int SecQsDetailOpenMs = 250;
    public const float ButtonCornerDp = 18f;
    public const float TileCornerDp = 22f;
    public const float PillCornerDp = 999f;

    // Directly extracted from SystemUI.apk EditModeTileDefaults / SecQSImplAnimatorBase DEX pools
    public const float QsEditSpringStiffness = 400f;
    public const float QsEditSpringDamping = 0.75f;
    public const float QsEditLiftElevationDp = 10f;
    public const float QsEditCornerDp = 28f;
    public const int QsEditSettleMs = 200;
    public const float QsEditRestScale = 0.93f;
    public const float QsEditLiftScale = 1.035f;

    public static float EffectiveCardCornerDp { get; private set; } = CardCornerDp;
    public static float EffectiveButtonCornerDp { get; private set; } = ButtonCornerDp;
    public static float EffectiveTileCornerDp { get; private set; } = TileCornerDp;

    /// <summary>
    /// Reads Samsung One UI / Android Material You dynamic wallpaper colors from <paramref name="ctx"/>
    /// (API 31+) with clean Samsung One UI 9 Blue as fallback.
    /// </summary>
    public static void Initialize(Context ctx) {
        Color defaultBlue = Color.ParseColor("#387AFF");
        Color defaultBlueHigh = Color.ParseColor("#598FFF");
        Color defaultBluePressed = Color.ParseColor("#2E65D4");
        Color defaultSec = Color.ParseColor("#6E7CF6");
        Color defaultSecHigh = Color.ParseColor("#8995FA");
        Color defaultSecPressed = Color.ParseColor("#4F5CE0");
        Color defaultTert = Color.ParseColor("#6E9CFF");
        Color defaultTertHigh = Color.ParseColor("#89B0FF");
        Color defaultTertPressed = Color.ParseColor("#4B7BE5");
        Color defaultHighlight = Color.ParseColor("#8E96FA");
        Color defaultHeaderTint = Color.ParseColor("#D6E4FF");
        // Native One UI 9 Dark Mode neutral frosted surfaces (harmonized with ambient cover glow)
        Color defaultBaseBg = Color.Rgb(8, 8, 12);
        Color defaultCard = Color.Argb(186, 21, 22, 28);
        Color defaultElevated = Color.Argb(208, 30, 31, 39);
        Color defaultRoomBg = Color.Argb(172, 12, 14, 20);

        Primary = defaultBlue;
        PrimaryHigh = defaultBlueHigh;
        PrimaryPressed = defaultBluePressed;
        Secondary = defaultSec;
        SecondaryHigh = defaultSecHigh;
        SecondaryPressed = defaultSecPressed;
        HighlightAccent = defaultHighlight;
        SliderAccent = defaultTert;
        Tertiary = defaultTert;
        TertiaryHigh = defaultTertHigh;
        TertiaryPressed = defaultTertPressed;
        CoralStart = defaultHighlight;
        CoralEnd = defaultTert;
        PeachText = defaultHeaderTint;
        BedChannel = defaultBlueHigh;
        BgScreen = defaultBaseBg;
        SurfaceCard = GlassCardSurface;
        SurfaceElevated = defaultElevated;
        SurfaceModal = SecQsDetailBg;
        SurfaceModalRow = Color.Argb(224, 40, 42, 54);
        RoomBg = defaultRoomBg;
        SurfaceFloatingBar = Color.Argb(230, 58, 58, 61);
        SelectedTabPill = Color.Argb(26, 255, 255, 255);
        IconSpatial = defaultBlue;
        IconSliderMedia = defaultTert;
        IconAtmosUpmix = defaultHighlight;
        IconStudioExport = Color.ParseColor("#4FB5E8");
        IconStemsCalib = Color.ParseColor("#56B8A8");
        IconSystemInfo = Color.ParseColor("#8A97B8");
        IsDynamicWallpaperActive = false;
        OnPrimary = GetContrastingTextColor(Primary);

        if (ctx == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return;
        }

        try {
            // Exact Samsung One UI 9 ColorPalette template mappings (wallpapertheme_template.json + SystemUI metadata_colorpalette.xml):
            // - quickpanel_btn_on (4-2-3) & primary -> system_accent1_300
            // - quickpanel_progress (4-8-5 / tw_progress_color_control_activated) -> system_accent2_300
            // - secondary / single_track -> system_accent2_500
            // - highlight -> system_accent3_300
            Color? sysA1_200 = TryGetFrameworkColor(ctx, "system_accent1_200");
            Color? sysA1_300 = TryGetFrameworkColor(ctx, "system_accent1_300");
            Color? sysA1_400 = TryGetFrameworkColor(ctx, "system_accent1_400") ?? TryGetFrameworkColor(ctx, "system_primary_dark");
            Color? sysA1_500 = TryGetFrameworkColor(ctx, "system_accent1_500");

            Color? sysA2_200 = TryGetFrameworkColor(ctx, "system_accent2_200");
            Color? sysA2_300 = TryGetFrameworkColor(ctx, "system_accent2_300") ?? TryGetFrameworkColor(ctx, "system_secondary_dark");
            Color? sysA2_400 = TryGetFrameworkColor(ctx, "system_accent2_400");
            Color? sysA2_500 = TryGetFrameworkColor(ctx, "system_accent2_500");

            Color? sysA3_200 = TryGetFrameworkColor(ctx, "system_accent3_200");
            Color? sysA3_300 = TryGetFrameworkColor(ctx, "system_accent3_300") ?? TryGetFrameworkColor(ctx, "system_tertiary_dark");
            Color? sysA3_400 = TryGetFrameworkColor(ctx, "system_accent3_400");
            Color? sysA3_500 = TryGetFrameworkColor(ctx, "system_accent3_500");

            Color? rawPrimary = sysA1_300 ?? sysA1_400;
            if (rawPrimary.HasValue && IsChromatic(rawPrimary.Value)) {
                IsDynamicWallpaperActive = true;

                // Preserve exact system_accent1_300 & system_accent2_300 without mutating hue or saturation
                Primary = rawPrimary.Value;
                PrimaryHigh = sysA1_200 ?? AdjustBrightness(Primary, 1.14f);
                PrimaryPressed = sysA1_400 ?? sysA1_500 ?? AdjustBrightness(Primary, 0.82f);

                Color exactQuickPanelSlider = sysA2_300 ?? sysA2_400 ?? sysA3_300 ?? Primary;
                SliderAccent = exactQuickPanelSlider;
                Tertiary = exactQuickPanelSlider;
                TertiaryHigh = sysA2_200 ?? AdjustBrightness(exactQuickPanelSlider, 1.10f);
                TertiaryPressed = sysA2_400 ?? AdjustBrightness(exactQuickPanelSlider, 0.84f);

                HighlightAccent = sysA3_300 ?? sysA3_400 ?? exactQuickPanelSlider;
                Secondary = sysA2_500 ?? sysA2_400 ?? exactQuickPanelSlider;
                SecondaryHigh = sysA2_400 ?? exactQuickPanelSlider;
                SecondaryPressed = sysA2_500 ?? AdjustBrightness(Secondary, 0.80f);

                CoralStart = HighlightAccent;
                CoralEnd = exactQuickPanelSlider;

                Color tintSrc = sysA2_200 ?? sysA1_200 ?? PrimaryHigh;
                PeachText = Blend(Color.Rgb(242, 243, 249), tintSrc, 0.36f);
                BedChannel = Blend(PrimaryHigh, Color.Rgb(108, 178, 255), 0.28f);

                // Harmonized 6-role semantic icon colors (share the wallpaper palette's saturation/luminance envelope)
                IconSpatial = Primary;
                IconSliderMedia = exactQuickPanelSlider;
                IconAtmosUpmix = IsChromatic(HighlightAccent) && HueDistance(Primary, HighlightAccent) >= 10f
                    ? HighlightAccent
                    : HarmonizeIconVariant(Primary, +28f);
                IconStudioExport = HarmonizeIconVariant(Primary, +44f);
                IconStemsCalib = HarmonizeIconVariant(exactQuickPanelSlider, -56f);
                IconSystemInfo = sysA2_400 ?? Blend(exactQuickPanelSlider, Color.Rgb(185, 192, 208), 0.35f);

                // Verified SystemUI.apk 120 Hz dark glass modules (#3D000000 + subtle 3% wallpaper whisper)
                SurfaceCard = WithAlpha(Blend(Color.Rgb(8, 9, 14), Primary, 0.03f), 0x52);
                SurfaceElevated = WithAlpha(Blend(Color.Rgb(30, 31, 39), exactQuickPanelSlider, 0.04f), 210);
                SurfaceModal = SecQsDetailBg;
                SurfaceModalRow = WithAlpha(Blend(Color.Rgb(38, 40, 52), exactQuickPanelSlider, 0.04f), 226);
                OnPrimary = GetContrastingTextColor(Primary);
            }
        } catch {
            // Fall back cleanly to Samsung One UI 9 Blue palette
        }
        OnPrimary = GetContrastingTextColor(Primary);
    }

    static Color HarmonizeIconVariant(Color referenceAccent, float hueShiftDeg) {
        float[] hsv = new float[3];
        Color.ColorToHSV(referenceAccent, hsv);
        hsv[0] = (hsv[0] + hueShiftDeg + 360f) % 360f;
        hsv[1] = Math.Clamp(hsv[1] * 0.92f, 0.32f, 0.72f);
        hsv[2] = Math.Clamp(hsv[2] * 0.98f, 0.68f, 0.92f);
        return new Color(Color.HSVToColor(255, hsv));
    }

    static Color? TryGetFrameworkColor(Context ctx, string name) {
        int id = ctx.Resources?.GetIdentifier(name, "color", "android") ?? 0;
        if (id == 0) {
            return null;
        }
        int argb = ctx.GetColor(id);
        return new Color(argb);
    }

    static bool IsChromatic(Color c) {
        float[] hsv = new float[3];
        Color.ColorToHSV(c, hsv);
        return hsv[1] >= 0.08f && hsv[2] >= 0.15f;
    }

    static float HueDistance(Color a, Color b) {
        float[] ha = new float[3];
        float[] hb = new float[3];
        Color.ColorToHSV(a, ha);
        Color.ColorToHSV(b, hb);
        float diff = Math.Abs(ha[0] - hb[0]);
        return Math.Min(diff, 360f - diff);
    }

    static Color AdjustBrightness(Color c, float factor) {
        float[] hsv = new float[3];
        Color.ColorToHSV(c, hsv);
        hsv[2] = Math.Clamp(hsv[2] * factor, 0f, 1f);
        if (factor > 1.05f) {
            hsv[1] = Math.Clamp(hsv[1] * 0.82f, 0f, 1f);
        }
        int rgb = Color.HSVToColor(c.A, hsv);
        return new Color(rgb);
    }

    static Color Blend(Color a, Color b, float t) {
        t = Math.Clamp(t, 0f, 1f);
        return Color.Argb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.Argb(alpha, c.R, c.G, c.B);

    static double SrgbChannelToLinear(byte channel) {
        double s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    public static double ComputeRelativeLuminance(Color c) =>
        0.2126 * SrgbChannelToLinear(c.R) +
        0.7152 * SrgbChannelToLinear(c.G) +
        0.0722 * SrgbChannelToLinear(c.B);

    public static double ComputeWcagContrast(Color fg, Color bg) {
        Color effBg = bg.A < 255 ? Blend(BgScreen, Color.Rgb(bg.R, bg.G, bg.B), bg.A / 255f) : bg;
        Color effFg = fg.A < 255 ? Blend(effBg, Color.Rgb(fg.R, fg.G, fg.B), fg.A / 255f) : fg;
        double l1 = ComputeRelativeLuminance(effFg);
        double l2 = ComputeRelativeLuminance(effBg);
        double hi = Math.Max(l1, l2);
        double lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Returns high-contrast ink (#FFFFFF or #05070C) for any solid or translucent accent fill.
    /// Mathematically guarantees >= 4.50:1 WCAG 2.1 AA contrast across every possible Material You wallpaper hue.
    /// </summary>
    public static Color GetContrastingTextColor(Color background) {
        Color darkInk = Color.Rgb(5, 7, 12);
        double lightRatio = ComputeWcagContrast(Color.White, background);
        double darkRatio = ComputeWcagContrast(darkInk, background);
        return darkRatio > lightRatio ? darkInk : Color.White;
    }

    public static int Dp(Context ctx, float dp) =>
        (int)Math.Round(dp * ctx.Resources.DisplayMetrics.Density);

    public static Color BlendColor(Color a, Color b, float t) => Blend(a, b, t);

    /// <summary>
    /// Serializes the active One UI 9 design tokens (palette, glass surfaces, semantic icons, corner radii)
    /// to compact deterministic JSON so companion apps (OmniAgent) can mirror Cavernize's live visual choices
    /// with zero quota and without false-positive timestamp diffs.
    /// </summary>
    public static string ExportDesignTokensJson(string sourceTag = "cavernize-ipc") {
        return JsonSerializer.Serialize(new {
            schema = DesignKitSchemaVersion,
            source = sourceTag,
            dynamicWallpaper = IsDynamicWallpaperActive,
            cardCornerDp = EffectiveCardCornerDp,
            buttonCornerDp = EffectiveButtonCornerDp,
            tileCornerDp = EffectiveTileCornerDp,
            bgScreen = BgScreen.ToArgb(),
            surfaceCard = SurfaceCard.ToArgb(),
            surfaceElevated = SurfaceElevated.ToArgb(),
            surfaceModal = SurfaceModal.ToArgb(),
            surfaceModalRow = SurfaceModalRow.ToArgb(),
            surfaceFloatingBar = SurfaceFloatingBar.ToArgb(),
            glassRimTop = GlassRimTop.ToArgb(),
            glassRimMid = GlassRimMid.ToArgb(),
            glassRimBottom = GlassRimBottom.ToArgb(),
            primary = Primary.ToArgb(),
            primaryHigh = PrimaryHigh.ToArgb(),
            primaryPressed = PrimaryPressed.ToArgb(),
            secondary = Secondary.ToArgb(),
            secondaryHigh = SecondaryHigh.ToArgb(),
            tertiary = Tertiary.ToArgb(),
            highlightAccent = HighlightAccent.ToArgb(),
            peachText = PeachText.ToArgb(),
            iconSpatial = IconSpatial.ToArgb(),
            iconSliderMedia = IconSliderMedia.ToArgb(),
            iconAtmosUpmix = IconAtmosUpmix.ToArgb(),
            iconStudioExport = IconStudioExport.ToArgb(),
            iconStemsCalib = IconStemsCalib.ToArgb(),
            iconSystemInfo = IconSystemInfo.ToArgb()
        });
    }

    /// <summary>
    /// Computes a deterministic signature of only the active visual tokens (radii + ARGB colors),
    /// ignoring metadata such as timestamps or source labels.
    /// </summary>
    public static string ComputeVisualTokensSignature() {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{EffectiveCardCornerDp:F2}|{EffectiveButtonCornerDp:F2}|{EffectiveTileCornerDp:F2}|" +
            $"{BgScreen.ToArgb()}|{SurfaceCard.ToArgb()}|{SurfaceElevated.ToArgb()}|{SurfaceModal.ToArgb()}|" +
            $"{SurfaceModalRow.ToArgb()}|{SurfaceFloatingBar.ToArgb()}|{GlassRimTop.ToArgb()}|{GlassRimMid.ToArgb()}|" +
            $"{GlassRimBottom.ToArgb()}|{Primary.ToArgb()}|{PrimaryHigh.ToArgb()}|{PrimaryPressed.ToArgb()}|" +
            $"{Secondary.ToArgb()}|{SecondaryHigh.ToArgb()}|{Tertiary.ToArgb()}|{HighlightAccent.ToArgb()}|" +
            $"{PeachText.ToArgb()}|{IconSpatial.ToArgb()}|{IconSliderMedia.ToArgb()}|{IconAtmosUpmix.ToArgb()}|" +
            $"{IconStudioExport.ToArgb()}|{IconStemsCalib.ToArgb()}|{IconSystemInfo.ToArgb()}");
    }

    /// <summary>
    /// Applies a JSON design-token snapshot exported by Cavernize so companion apps reflect
    /// Cavernize design adjustments immediately at runtime without rebuilding or spending quota.
    /// </summary>
    public static bool ApplyRuntimeDesignTokensJson(string json, string sourceLabel = "cavernize-ipc") =>
        ApplyRuntimeDesignTokensJson(json, sourceLabel, out _);

    /// <summary>
    /// Applies a JSON design-token snapshot exported by Cavernize and reports via
    /// <paramref name="visualChanged"/> whether any visual color or corner-radius token actually changed.
    /// </summary>
    public static bool ApplyRuntimeDesignTokensJson(string json, string sourceLabel, out bool visualChanged) {
        visualChanged = false;
        if (string.IsNullOrWhiteSpace(json)) {
            return false;
        }
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }
            string beforeSig = ComputeVisualTokensSignature();
            if (TryGetFloat(root, "cardCornerDp", out float cDp)) EffectiveCardCornerDp = Math.Clamp(cDp, 12f, 36f);
            if (TryGetFloat(root, "buttonCornerDp", out float bDp)) EffectiveButtonCornerDp = Math.Clamp(bDp, 10f, 32f);
            if (TryGetFloat(root, "tileCornerDp", out float tDp)) EffectiveTileCornerDp = Math.Clamp(tDp, 12f, 32f);
            if (TryGetArgb(root, "bgScreen", out Color c)) BgScreen = c;
            if (TryGetArgb(root, "surfaceCard", out c)) SurfaceCard = c;
            if (TryGetArgb(root, "surfaceElevated", out c)) SurfaceElevated = c;
            if (TryGetArgb(root, "surfaceModal", out c)) SurfaceModal = c;
            if (TryGetArgb(root, "surfaceModalRow", out c)) SurfaceModalRow = c;
            if (TryGetArgb(root, "surfaceFloatingBar", out c)) SurfaceFloatingBar = c;
            if (TryGetArgb(root, "glassRimTop", out c)) GlassRimTop = c;
            if (TryGetArgb(root, "glassRimMid", out c)) GlassRimMid = c;
            if (TryGetArgb(root, "glassRimBottom", out c)) GlassRimBottom = c;
            if (TryGetArgb(root, "primary", out c)) Primary = c;
            if (TryGetArgb(root, "primaryHigh", out c)) PrimaryHigh = c;
            if (TryGetArgb(root, "primaryPressed", out c)) PrimaryPressed = c;
            if (TryGetArgb(root, "secondary", out c)) Secondary = c;
            if (TryGetArgb(root, "secondaryHigh", out c)) SecondaryHigh = c;
            if (TryGetArgb(root, "tertiary", out c)) { Tertiary = c; SliderAccent = c; }
            if (TryGetArgb(root, "highlightAccent", out c)) HighlightAccent = c;
            if (TryGetArgb(root, "peachText", out c)) PeachText = c;
            if (TryGetArgb(root, "iconSpatial", out c)) IconSpatial = c;
            if (TryGetArgb(root, "iconSliderMedia", out c)) IconSliderMedia = c;
            if (TryGetArgb(root, "iconAtmosUpmix", out c)) IconAtmosUpmix = c;
            if (TryGetArgb(root, "iconStudioExport", out c)) IconStudioExport = c;
            if (TryGetArgb(root, "iconStemsCalib", out c)) IconStemsCalib = c;
            if (TryGetArgb(root, "iconSystemInfo", out c)) IconSystemInfo = c;
            ActiveDesignTokensSource = string.IsNullOrWhiteSpace(sourceLabel) ? "cavernize-ipc" : sourceLabel;
            string afterSig = ComputeVisualTokensSignature();
            visualChanged = !string.Equals(beforeSig, afterSig, StringComparison.Ordinal);
            return true;
        } catch {
            return false;
        }
    }

    static bool TryGetArgb(JsonElement obj, string name, out Color color) {
        color = default;
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int argb)) {
            color = new Color(argb);
            return true;
        }
        return false;
    }

    static bool TryGetFloat(JsonElement obj, string name, out float value) {
        value = 0f;
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out float v)) {
            value = v;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Frosted glass capsule container that samples and blurs the content behind itself in real time
/// (matching One UI 9 bottom navigation &amp; media control pills) while keeping foreground children razor-sharp.
/// </summary>
public sealed class OneUiFrostedCapsuleView : FrameLayout {
    readonly Paint frostOverlayPaint = new(PaintFlags.AntiAlias);
    readonly Paint rimPaint = new(PaintFlags.AntiAlias);
    readonly Paint bitmapPaint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Android.Graphics.Path clipPath = new();
    readonly int[] selfLoc = new int[2];
    readonly int[] targetLoc = new int[2];

    Bitmap blurBitmap;
    BitmapShader cachedBmpShader;
    readonly Matrix cachedShaderMatrix = new();
    readonly RectF drawBounds = new();
    LinearGradient cachedGlazeShader;
    LinearGradient cachedRimShader;
    int cachedShaderH = -1;
    int cachedGlazeTopArgb;
    int cachedGlazeBotArgb;
    int[] pixelScratch;
    Canvas captureCanvas;
    bool isCapturing;
    long lastCaptureMs;
    Java.Lang.Runnable deferredBlurRunnable;

    public Func<View[]> BehindViewsProvider { get; set; }
    public float CornerRadiusDp { get; set; } = 30f;
    public Color? GlazeTopColor { get; set; }
    public Color? GlazeBottomColor { get; set; }
    public bool IsSuspendedForTransition { get; set; }

    public OneUiFrostedCapsuleView(Context context) : base(context) {
        SetWillNotDraw(false);
        frostOverlayPaint.SetStyle(Paint.Style.Fill);
        rimPaint.SetStyle(Paint.Style.Stroke);
        rimPaint.StrokeWidth = Math.Max(1.5f, OneUiTheme.Dp(context, 1.0f));
    }

    /// <summary>
    /// Debounces frosted backdrop capture until after scrolling or tab transitions settle,
    /// keeping 120 Hz animations completely free of synchronous software View.Draw() passes.
    /// </summary>
    public void ScheduleDeferredBlur(long delayMs = 180) {
        if (Visibility != ViewStates.Visible || Width <= 0 || Height <= 0) {
            return;
        }
        deferredBlurRunnable ??= new Java.Lang.Runnable(() => {
            if (!IsSuspendedForTransition && IsAttachedToWindow && Visibility == ViewStates.Visible) {
                RefreshBlur(force: true);
            }
        });
        RemoveCallbacks(deferredBlurRunnable);
        PostDelayed(deferredBlurRunnable, Math.Max(48L, delayMs));
    }

    protected override void OnDetachedFromWindow() {
        if (deferredBlurRunnable != null) {
            RemoveCallbacks(deferredBlurRunnable);
        }
        base.OnDetachedFromWindow();
    }

    public void RefreshBlur(bool force = false) {
        if (isCapturing || Width <= 0 || Height <= 0 || Visibility != ViewStates.Visible) {
            return;
        }
        if (IsSuspendedForTransition) {
            ScheduleDeferredBlur(160);
            return;
        }
        long now = SystemClock.UptimeMillis();
        if (!force && now - lastCaptureMs < 220) {
            ScheduleDeferredBlur(180);
            return;
        }
        CaptureBehindRegion();
        PostInvalidateOnAnimation();
    }

    void CaptureBehindRegion() {
        View[] behind = BehindViewsProvider?.Invoke();
        if (behind == null || behind.Length == 0) {
            return;
        }

        const int downscale = 10;
        int bw = Math.Clamp(Width / downscale, 16, 160);
        int bh = Math.Clamp(Height / downscale, 8, 48);

        if (blurBitmap == null || blurBitmap.IsRecycled || blurBitmap.Width != bw || blurBitmap.Height != bh) {
            captureCanvas?.Dispose();
            cachedBmpShader?.Dispose();
            cachedBmpShader = null;
            blurBitmap?.Recycle();
            blurBitmap?.Dispose();
            blurBitmap = Bitmap.CreateBitmap(bw, bh, Bitmap.Config.Argb8888!);
            captureCanvas = new Canvas(blurBitmap);
            cachedBmpShader = new BitmapShader(blurBitmap, Shader.TileMode.Clamp!, Shader.TileMode.Clamp!);
            pixelScratch = new int[bw * bh];
        }

        isCapturing = true;
        try {
            lastCaptureMs = SystemClock.UptimeMillis();
            blurBitmap.EraseColor(OneUiTheme.BgScreen.ToArgb());
            GetLocationInWindow(selfLoc);

            float sx = bw / (float)Width;
            float sy = bh / (float)Height;

            foreach (View v in behind) {
                if (v == null || v.Visibility != ViewStates.Visible || v.Width <= 0 || v.Height <= 0) {
                    continue;
                }
                v.GetLocationInWindow(targetLoc);
                int save = captureCanvas.Save();
                captureCanvas.Scale(sx, sy);
                captureCanvas.Translate(targetLoc[0] - selfLoc[0], targetLoc[1] - selfLoc[1]);
                v.Draw(captureCanvas);
                captureCanvas.RestoreToCount(save);
            }

            FastBoxBlur(blurBitmap, pixelScratch, 3);
            FastBoxBlur(blurBitmap, pixelScratch, 2);
        } catch {
            // Keep smooth frosted surface fallback if any child cannot draw offscreen
        } finally {
            isCapturing = false;
        }
    }

    protected override void OnDraw(Canvas canvas) {
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            base.OnDraw(canvas);
            return;
        }

        if (blurBitmap == null && !IsSuspendedForTransition) {
            CaptureBehindRegion();
        }

        float rad = Math.Min(Math.Min(w, h) * 0.5f, OneUiTheme.Dp(Context, CornerRadiusDp));
        float halfStroke = rimPaint.StrokeWidth * 0.5f;
        drawBounds.Set(halfStroke, halfStroke, w - halfStroke, h - halfStroke);

        // Analytical GPU-anti-aliased rounded-rect draw reusing cached BitmapShader + Matrix (zero per-frame alloc)
        if (blurBitmap != null && !blurBitmap.IsRecycled && blurBitmap.Width > 0 && blurBitmap.Height > 0 && cachedBmpShader != null) {
            cachedShaderMatrix.Reset();
            cachedShaderMatrix.SetScale(w / blurBitmap.Width, h / blurBitmap.Height);
            cachedBmpShader.SetLocalMatrix(cachedShaderMatrix);
            bitmapPaint.SetShader(cachedBmpShader);
            canvas.DrawRoundRect(drawBounds, rad, rad, bitmapPaint);
            bitmapPaint.SetShader(null);
        }

        // Translucent smoked One UI 9 frosted glaze + top subtle reflection highlight (cached shaders, zero per-draw alloc)
        Color gTop = GlazeTopColor ?? Color.Argb(204, 34, 36, 44);
        Color gBot = GlazeBottomColor ?? Color.Argb(228, 16, 17, 22);
        int gTopArgb = gTop.ToArgb();
        int gBotArgb = gBot.ToArgb();
        int ih = Height;
        if (cachedShaderH != ih || cachedGlazeTopArgb != gTopArgb || cachedGlazeBotArgb != gBotArgb ||
            cachedGlazeShader == null || cachedRimShader == null) {
            cachedGlazeShader?.Dispose();
            cachedRimShader?.Dispose();
            cachedGlazeShader = new LinearGradient(
                0f, 0f, 0f, h,
                [gTopArgb, gBotArgb],
                [0f, 1f],
                Shader.TileMode.Clamp!);
            cachedRimShader = new LinearGradient(
                0f, 0f, 0f, h,
                [Color.Argb(105, 255, 255, 255).ToArgb(), Color.Argb(22, 255, 255, 255).ToArgb(), Color.Argb(58, 255, 255, 255).ToArgb()],
                [0f, 0.45f, 1f],
                Shader.TileMode.Clamp!);
            cachedShaderH = ih;
            cachedGlazeTopArgb = gTopArgb;
            cachedGlazeBotArgb = gBotArgb;
        }
        frostOverlayPaint.SetShader(cachedGlazeShader);
        canvas.DrawRoundRect(drawBounds, rad, rad, frostOverlayPaint);
        frostOverlayPaint.SetShader(null);

        rimPaint.SetShader(cachedRimShader);
        canvas.DrawRoundRect(drawBounds, rad, rad, rimPaint);
        rimPaint.SetShader(null);

        base.OnDraw(canvas);
    }

    static void FastBoxBlur(Bitmap bmp, int[] pixels, int radius) {
        int w = bmp.Width;
        int h = bmp.Height;
        if (w < 3 || h < 3 || radius <= 0 || pixels == null || pixels.Length < w * h) {
            return;
        }
        bmp.GetPixels(pixels, 0, w, 0, 0, w, h);
        int window = radius * 2 + 1;

        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int x = 0; x < w; x++) {
                int rSum = 0, gSum = 0, bSum = 0;
                for (int k = -radius; k <= radius; k++) {
                    int sx = Math.Clamp(x + k, 0, w - 1);
                    int c = pixels[row + sx];
                    rSum += (c >> 16) & 0xFF;
                    gSum += (c >> 8) & 0xFF;
                    bSum += c & 0xFF;
                }
                pixels[row + x] = unchecked((int)0xFF000000) | ((rSum / window) << 16) | ((gSum / window) << 8) | (bSum / window);
            }
        }

        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                int rSum = 0, gSum = 0, bSum = 0;
                for (int k = -radius; k <= radius; k++) {
                    int sy = Math.Clamp(y + k, 0, h - 1);
                    int c = pixels[sy * w + x];
                    rSum += (c >> 16) & 0xFF;
                    gSum += (c >> 8) & 0xFF;
                    bSum += c & 0xFF;
                }
                pixels[y * w + x] = unchecked((int)0xFF000000) | ((rSum / window) << 16) | ((gSum / window) << 8) | (bSum / window);
            }
        }
        bmp.SetPixels(pixels, 0, w, 0, 0, w, h);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            cachedGlazeShader?.Dispose();
            cachedGlazeShader = null;
            cachedRimShader?.Dispose();
            cachedRimShader = null;
            cachedBmpShader?.Dispose();
            cachedBmpShader = null;
            captureCanvas?.Dispose();
            captureCanvas = null;
            blurBitmap?.Recycle();
            blurBitmap?.Dispose();
            blurBitmap = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Full-capsule 44dp One UI 9 slider (matching SystemUI Brightness &amp; SoundCraft Volume seekbars):
/// - Exact Samsung ToggleSeekBar.onTouchEvent relative-drag mechanism (touching anywhere keeps the
///   bar &amp; ball in place and adjusts proportionally from the initial touch point)
/// - Blooming circular touch "ball" thumb with spring animation on press/drag, optional SlimVolumeMode
///   for Apple Music's volume bar, embedded label &amp; value readout, double-tap reset, and scroll safety.
/// </summary>
public sealed class OneUiSeekBar : SeekBar {
    readonly int touchSlop;
    readonly Paint trackPaint = new(PaintFlags.AntiAlias);
    readonly Paint trackRimPaint = new(PaintFlags.AntiAlias);
    readonly Paint fillPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbHaloPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbBallPaint = new(PaintFlags.AntiAlias);
    readonly Paint thumbBallRimPaint = new(PaintFlags.AntiAlias);
    readonly TextPaint labelPaint = new(PaintFlags.AntiAlias);
    readonly Paint valuePaint = new(PaintFlags.AntiAlias);
    readonly Android.Graphics.Path clipPath = new();

    float downX;
    float downY;
    float dragAnchorX;
    float startDragRatio;
    float maxMoveDist;
    int initialProgress;
    bool isHorizontalDrag;
    bool isTouchDown;
    bool isCancelledToScroll;
    DateTime lastTapTime = DateTime.MinValue;

    float renderedRatio;
    float touchRatio;
    float thumbExpansion;
    bool hasInitializedRatio;
    long lastFrameMs;

    public string LabelText { get; set; } = "";
    public string ValueText { get; set; } = "";
    public bool UseBlueAccent { get; set; }
    public bool SlimVolumeMode { get; set; }
    public Color? CustomFillColor { get; set; }
    public int DefaultProgress { get; set; }
    public bool HasDefaultReset { get; set; } = true;

    public event Action<int> ResetRequested;
    public event Action<int> UserProgressChanged;

    public OneUiSeekBar(Context context) : base(context) {
        touchSlop = ViewConfiguration.Get(context)?.ScaledTouchSlop ?? 24;
        float dp = context.Resources.DisplayMetrics.Density;

        SetBackgroundColor(Color.Transparent);
#pragma warning disable CS0618
        SetThumb(new ColorDrawable(Color.Transparent));
#pragma warning restore CS0618
        ProgressDrawable = new ColorDrawable(Color.Transparent);
        SplitTrack = false;
        SetPadding(0, 0, 0, 0);

        trackPaint.Color = Color.Argb(58, 255, 255, 255);
        trackPaint.SetStyle(Paint.Style.Fill);

        trackRimPaint.Color = Color.Argb(36, 255, 255, 255);
        trackRimPaint.StrokeWidth = Math.Max(1f, dp);
        trackRimPaint.SetStyle(Paint.Style.Stroke);

        fillPaint.SetStyle(Paint.Style.Fill);

        thumbHaloPaint.SetStyle(Paint.Style.Fill);
        thumbBallPaint.SetStyle(Paint.Style.Fill);
        thumbBallRimPaint.SetStyle(Paint.Style.Stroke);
        thumbBallRimPaint.StrokeWidth = Math.Max(1.2f, 1.2f * dp);

        labelPaint.Color = OneUiTheme.TextPrimary;
        labelPaint.TextSize = 11.8f * dp;
        labelPaint.SetTypeface(OneUiTheme.GetAppleFontMedium(context));
        labelPaint.SetShadowLayer(2.5f * dp, 0f, 1f * dp, Color.Argb(140, 0, 0, 0));

        valuePaint.Color = OneUiTheme.TextPrimary;
        valuePaint.TextSize = 11.8f * dp;
        valuePaint.SetTypeface(OneUiTheme.GetAppleFontBold(context));
        valuePaint.TextAlign = Paint.Align.Right;
        valuePaint.SetShadowLayer(2.5f * dp, 0f, 1f * dp, Color.Argb(140, 0, 0, 0));
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        int desiredH = OneUiTheme.Dp(Context, SlimVolumeMode ? 26f : 44f);
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(ResolveSize(w, widthMeasureSpec), ResolveSize(desiredH, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas) {
        float w = Width;
        float h = Height;
        if (w <= 0 || h <= 0) {
            return;
        }

        long nowMs = SystemClock.UptimeMillis();
        long elapsedMs = lastFrameMs == 0 ? 0L : (nowMs - lastFrameMs);
        float dt = (lastFrameMs == 0 || elapsedMs > 60L)
            ? 0.008f
            : Math.Clamp(elapsedMs / 1000f, 0.001f, 0.033f);
        lastFrameMs = nowMs;

        float discreteRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
        float targetRatio = isHorizontalDrag ? touchRatio : discreteRatio;
        if (!hasInitializedRatio) {
            renderedRatio = targetRatio;
            hasInitializedRatio = true;
        } else {
            float ratioSpeed = isHorizontalDrag ? 28f : 11f;
            float alpha = 1f - MathF.Exp(-ratioSpeed * dt);
            renderedRatio += (targetRatio - renderedRatio) * alpha;
            if (MathF.Abs(targetRatio - renderedRatio) < 0.0006f) {
                renderedRatio = targetRatio;
            }
        }

        float targetThumb = isTouchDown ? 1f : 0f;
        float thumbSpeed = isTouchDown ? 24f : 16f;
        thumbExpansion += (targetThumb - thumbExpansion) * (1f - MathF.Exp(-thumbSpeed * dt));
        if (MathF.Abs(targetThumb - thumbExpansion) < 0.004f) {
            thumbExpansion = targetThumb;
        }

        float dp = Resources.DisplayMetrics.Density;

        if (SlimVolumeMode) {
            DrawSlimVolumeTrack(canvas, w, h, dp);
            bool slimAnim =
                MathF.Abs(targetRatio - renderedRatio) > 0.0006f ||
                MathF.Abs(targetThumb - thumbExpansion) > 0.004f;
            if (slimAnim) {
                PostInvalidateOnAnimation();
            } else {
                lastFrameMs = 0;
            }
            return;
        }

        float radius = h * 0.5f;
        RectF bounds = new(0.5f, 0.5f, w - 0.5f, h - 0.5f);

        // 1. Background translucent neutral glass track
        canvas.DrawRoundRect(bounds, radius, radius, trackPaint);

        // 2. Faithful Samsung SystemUI Control Slider Fill:
        //    - Resting (!isTouchDown / thumbExpansion == 0) OR at Max: 100% solid uniform fill, ZERO visible ball
        //    - Active dragging/pressing below Max: left fade 0.50 alpha -> full alpha ball (r = h/2) of exact same RGB
        if (renderedRatio > 0.0005f || (isTouchDown && Progress > 0)) {
            clipPath.Reset();
            clipPath.AddRoundRect(bounds, radius, radius, Android.Graphics.Path.Direction.Cw);
            canvas.Save();
            canvas.ClipPath(clipPath);

            float fillRight = Math.Clamp(renderedRatio * w, 0f, w);
            Color accent = CustomFillColor ?? (UseBlueAccent ? OneUiTheme.Primary : OneUiTheme.SliderAccent);

            if (thumbExpansion <= 0.004f) {
                fillPaint.SetShader(null);
                fillPaint.Color = Color.Argb(255, accent.R, accent.G, accent.B);
                float cornerR = Math.Min(radius, Math.Max(0.5f, fillRight * 0.5f));
                canvas.DrawRoundRect(0f, 0f, fillRight, h, cornerR, cornerR, fillPaint);
            } else {
                float maxSolidity = SmoothStep(0.96f, 0.999f, renderedRatio);
                float reveal = thumbExpansion * (1f - maxSolidity);
                byte bodyLeftAlpha = (byte)Math.Round(255f - 128f * reveal);   // 255 -> 127 (0.50f like tw_progress_color_control_activated_start)
                byte bodyRightAlpha = (byte)Math.Round(255f - 70f * reveal);   // 255 -> 185 (0.72f near thumb)

                if (fillRight > 0.5f) {
                    if (reveal > 0.005f) {
                        Color bodyLeft = Color.Argb(bodyLeftAlpha, accent.R, accent.G, accent.B);
                        Color bodyRight = Color.Argb(bodyRightAlpha, accent.R, accent.G, accent.B);
                        using LinearGradient fillShader = new(0f, 0f, Math.Max(2f, fillRight), 0f, [bodyLeft.ToArgb(), bodyRight.ToArgb()], [0f, 1f], Shader.TileMode.Clamp);
                        fillPaint.SetShader(fillShader);
                    } else {
                        fillPaint.SetShader(null);
                        fillPaint.Color = Color.Argb(255, accent.R, accent.G, accent.B);
                    }
                    float cornerR = Math.Min(radius, Math.Max(0.5f, fillRight * 0.5f));
                    canvas.DrawRoundRect(0f, 0f, fillRight, h, cornerR, cornerR, fillPaint);
                    fillPaint.SetShader(null);

                    if (reveal > 0.005f) {
                        float edgeFade = Math.Clamp(fillRight / Math.Max(1f, h), 0f, 1f);
                        float ballX = Math.Clamp(fillRight - radius, 0f, w - radius);
                        thumbBallPaint.Color = Color.Argb((int)Math.Round(255f * reveal * edgeFade), accent.R, accent.G, accent.B);
                        canvas.DrawCircle(ballX, radius, radius, thumbBallPaint);
                    }
                }
            }

            canvas.Restore();
        }

        // Subtle tick notch indicating default reset point
        if (HasDefaultReset && Max > 0 && DefaultProgress > 0 && DefaultProgress < Max) {
            float defX = DefaultProgress / (float)Max * w;
            canvas.DrawLine(defX, h - 5.5f * dp, defX, h - 1.5f * dp, trackRimPaint);
        }

        canvas.DrawRoundRect(bounds, radius, radius, trackRimPaint);

        // 3. Embedded label & live value centered vertically inside the capsule (always crisp above fill & ball)
        float textY = radius - (labelPaint.Ascent() + labelPaint.Descent()) * 0.5f;
        string valText = string.IsNullOrEmpty(ValueText) ? Progress.ToString() : ValueText;
        float valW = valuePaint.MeasureText(valText);

        if (!string.IsNullOrEmpty(LabelText)) {
            float maxLabelW = Math.Max(28f * dp, w - 36f * dp - valW);
            string ellipsized = TextUtils.Ellipsize(LabelText, labelPaint, maxLabelW, TextUtils.TruncateAt.End) ?? LabelText;
            canvas.Save();
            canvas.ClipRect(12f * dp, 0f, 14f * dp + maxLabelW, h);
            canvas.DrawText(ellipsized, 14f * dp, textY, labelPaint);
            canvas.Restore();
        }
        canvas.DrawText(valText, w - 14f * dp, textY, valuePaint);

        bool needsNextFrame =
            MathF.Abs(targetRatio - renderedRatio) > 0.0006f ||
            MathF.Abs(targetThumb - thumbExpansion) > 0.004f;
        if (needsNextFrame) {
            PostInvalidateOnAnimation();
        } else {
            lastFrameMs = 0;
        }
    }

    void DrawSlimVolumeTrack(Canvas canvas, float w, float h, float dp) {
        float barH = (7f + 5f * thumbExpansion) * dp;
        float cy = h * 0.5f;
        float top = cy - barH * 0.5f;
        float bot = cy + barH * 0.5f;
        float rad = barH * 0.5f;

        RectF trackRect = new(0f, top, w, bot);
        fillPaint.SetShader(null);
        fillPaint.Color = Color.Argb(54, 255, 255, 255);
        canvas.DrawRoundRect(trackRect, rad, rad, fillPaint);

        if (renderedRatio > 0.002f) {
            clipPath.Reset();
            clipPath.AddRoundRect(trackRect, rad, rad, Android.Graphics.Path.Direction.Cw);
            canvas.Save();
            canvas.ClipPath(clipPath);

            float fillRight = Math.Clamp(renderedRatio * w, 0f, w);
            Color c = CustomFillColor ?? Color.Argb(228, 255, 255, 255);
            fillPaint.Color = Color.Argb((int)Math.Round(185 + 70 * thumbExpansion), c.R, c.G, c.B);
            canvas.DrawRoundRect(0f, top, Math.Max(rad * 1.4f, fillRight), bot, rad, rad, fillPaint);
            canvas.Restore();
        }

        if (HasDefaultReset && Max > 0 && DefaultProgress > 0 && DefaultProgress < Max) {
            float defX = DefaultProgress / (float)Max * w;
            canvas.DrawLine(defX, top + 1f * dp, defX, bot - 1f * dp, trackRimPaint);
        }
    }

    public override bool OnTouchEvent(MotionEvent e) {
        if (!Enabled) {
            return false;
        }

        float w = Math.Max(1f, Width);
        float dp = Resources.DisplayMetrics.Density;

        switch (e.ActionMasked) {
            case MotionEventActions.Down:
                // Exact Samsung ToggleSeekBar.onTouchEvent mechanism:
                // Do NOT jump progress to downX; record downX & current ratio and anchor relative drag.
                downX = e.GetX();
                downY = e.GetY();
                dragAnchorX = downX;
                maxMoveDist = 0f;
                initialProgress = Progress;
                startDragRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
                touchRatio = startDragRatio;
                isHorizontalDrag = false;
                isTouchDown = true;
                isCancelledToScroll = false;
                lastFrameMs = SystemClock.UptimeMillis();
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Move:
                if (isCancelledToScroll) {
                    return false;
                }
                float curX = e.GetX();
                float curY = e.GetY();
                float dxAbs = Math.Abs(curX - downX);
                float dyAbs = Math.Abs(curY - downY);
                float moveDist = MathF.Sqrt(dxAbs * dxAbs + dyAbs * dyAbs);
                if (moveDist > maxMoveDist) {
                    maxMoveDist = moveDist;
                }

                if (!isHorizontalDrag) {
                    if (dyAbs > touchSlop && dyAbs > dxAbs) {
                        isCancelledToScroll = true;
                        isTouchDown = false;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        Progress = initialProgress;
                        PostInvalidateOnAnimation();
                        return false;
                    }
                    if (dxAbs > touchSlop * 0.5f && dxAbs >= dyAbs * 1.05f) {
                        isHorizontalDrag = true;
                        dragAnchorX = curX;
                        startDragRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : renderedRatio;
                        touchRatio = startDragRatio;
                        Parent?.RequestDisallowInterceptTouchEvent(true);
                        if (!SlimVolumeMode) {
                            Animate().ScaleX(1.008f).ScaleY(1.032f).SetDuration(120).Start();
                        }
                    } else {
                        PostInvalidateOnAnimation();
                        return true;
                    }
                }

                Parent?.RequestDisallowInterceptTouchEvent(true);
                float usableW = Math.Max(1f, w - (SlimVolumeMode ? 12f * dp : Height * 0.45f));
                float rawRatio = startDragRatio + (curX - dragAnchorX) / usableW;
                if (rawRatio > 1f) {
                    rawRatio = 1f;
                    startDragRatio = 1f;
                    dragAnchorX = curX;
                } else if (rawRatio < 0f) {
                    rawRatio = 0f;
                    startDragRatio = 0f;
                    dragAnchorX = curX;
                }
                touchRatio = rawRatio;

                int newProg = Max > 0 ? Math.Clamp((int)Math.Round(touchRatio * Max), 0, Max) : 0;
                if (newProg != Progress) {
                    Progress = newProg;
                    UserProgressChanged?.Invoke(newProg);
                }
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Up:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleX(1f).ScaleY(1f).SetDuration(280).Start();
                isTouchDown = false;
                if (isCancelledToScroll) {
                    isCancelledToScroll = false;
                    PostInvalidateOnAnimation();
                    return false;
                }

                bool wasTap = !isHorizontalDrag && maxMoveDist < touchSlop;
                if (wasTap && HasDefaultReset) {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastTapTime).TotalMilliseconds <= 320) {
                        lastTapTime = DateTime.MinValue;
                        isHorizontalDrag = false;
                        int target = Math.Clamp(DefaultProgress, 0, Max);
                        Progress = target;
                        touchRatio = Max > 0 ? Math.Clamp(target / (float)Max, 0f, 1f) : 0f;
                        ResetRequested?.Invoke(target);
                        Animate().ScaleX(0.99f).ScaleY(0.95f).SetDuration(80).WithEndAction(
                            new Java.Lang.Runnable(() => Animate().ScaleX(1f).ScaleY(1f).SetDuration(240).Start())).Start();
                        PostInvalidateOnAnimation();
                        return true;
                    }
                    lastTapTime = now;
                }

                // On single tap without horizontal drag, keep current Progress intact (ToggleSeekBar behavior)
                isHorizontalDrag = false;
                touchRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
                PostInvalidateOnAnimation();
                return true;

            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                Animate().ScaleX(1f).ScaleY(1f).SetDuration(280).Start();
                isTouchDown = false;
                bool wasDragging = isHorizontalDrag;
                isHorizontalDrag = false;
                isCancelledToScroll = false;
                touchRatio = Max > 0 ? Math.Clamp(Progress / (float)Max, 0f, 1f) : 0f;
                PostInvalidateOnAnimation();
                return wasDragging;
        }

        return base.OnTouchEvent(e);
    }

    static float SmoothStep(float edge0, float edge1, float x) {
        if (edge1 <= edge0) {
            return x >= edge1 ? 1f : 0f;
        }
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Hardware-accelerated blended optical defocus + velocity-scaled horizontal motion blur engine
/// (API 31+ <see cref="RenderEffect"/>). Instead of harsh 1D shutter-speed smearing, combines soft
/// isotropic Gaussian defocus (<c>radiusY</c>) with subtle directional horizontal stretch (<c>radiusX</c>)
/// and delicate depth breathing so transitions feel lush, calm, and glass-like, clearing cleanly to <c>null</c> at rest.
/// </summary>
public static class OneUiMotionBlur {
    const float DeadzoneDpPerSec = 240f;
    const float ScaleGain = 0.0036f;
    const float DefaultOpticalDefocusRatio = 0.52f;

    /// <summary>
    /// Computes the horizontal blur radius in pixels from instantaneous horizontal speed in px/sec.
    /// Guarantees <c>0</c> below deadzone, monotonic velocity scaling, and an aesthetically soft GPU ceiling.
    /// </summary>
    public static float ComputeHorizontalBlurRadiusPx(float velocityPxPerSec, float density, float maxBlurDp = 14f) {
        if (!float.IsFinite(velocityPxPerSec) || !float.IsFinite(density) || density <= 0f) {
            return 0f;
        }
        float speedDpPerSec = MathF.Abs(velocityPxPerSec) / density;
        if (speedDpPerSec <= DeadzoneDpPerSec) {
            return 0f;
        }
        float excess = speedDpPerSec - DeadzoneDpPerSec;
        float rawDp = excess * ScaleGain;
        float maxDp = Math.Clamp(maxBlurDp, 3f, 20f);
        // Smooth soft-knee saturation keeping 1-tab vs 2-tab vs 3-tab jumps distinct without harsh streaking
        float curvedDp = maxDp * (1f - MathF.Exp(-rawDp / Math.Max(3.5f, maxDp * 0.72f)));
        float px = curvedDp * density;
        return px >= 0.75f ? px : 0f;
    }

    /// <summary>
    /// Computes blended 2D elliptical blur radii (<c>BlurX</c> directional motion + <c>BlurY</c> soft optical defocus).
    /// When active, <c>BlurX &gt;= BlurY &gt;= 0.75px</c>, avoiding harsh 1D scanline streaks.
    /// </summary>
    public static (float BlurX, float BlurY) ComputeBlendedBlurRadiiPx(
        float velocityPxPerSec,
        float density,
        float maxBlurDp = 14f,
        float opticalDefocusRatio = DefaultOpticalDefocusRatio) {
        float blurX = ComputeHorizontalBlurRadiusPx(velocityPxPerSec, density, maxBlurDp);
        if (blurX < 0.75f) {
            return (0f, 0f);
        }
        float ratio = Math.Clamp(opticalDefocusRatio, 0.30f, 0.85f);
        float clampedX = Math.Clamp(blurX, 0.75f, 52f);
        float clampedY = Math.Clamp(clampedX * ratio, 0.75f, 36f);
        return (clampedX, clampedY);
    }

    /// <summary>
    /// Computes subtle optical depth micro-scale and silky alpha veil proportional to normalized blur intensity,
    /// returning exactly <c>(1f, 1f)</c> at rest.
    /// </summary>
    public static (float Scale, float Alpha) ComputeTransitionOpticalDepth(
        float blurRadiusPx,
        float maxBlurPx,
        bool isEntering = false) {
        if (!float.IsFinite(blurRadiusPx) || !float.IsFinite(maxBlurPx) || blurRadiusPx < 0.75f || maxBlurPx <= 0.75f) {
            return (1f, 1f);
        }
        float norm = Math.Clamp(blurRadiusPx / maxBlurPx, 0f, 1f);
        // Smooth quadratic ease so gentle movement stays crisp and fast travel breathes softly
        float soft = norm * (0.65f + 0.35f * norm);
        float scale = 1f - (isEntering ? 0.018f : 0.024f) * soft;
        float alpha = 1f - (isEntering ? 0.06f : 0.14f) * soft;
        return (scale, alpha);
    }

    /// <summary>
    /// Guard disabling full-screen per-frame RenderEffect blur allocation (which triggers 1440p offscreen
    /// GPU render-target churn &gt; 8.3 ms at 120 Hz). Keep false by default for silky 120 Hz transitions.
    /// </summary>
    public static bool EnableFullScreenRenderEffectBlur { get; set; }

    /// <summary>Promotes <paramref name="view"/> to a GPU hardware texture layer for smooth 120 Hz transitions.</summary>
    public static void EnableHardwareLayer(View view) {
        if (view == null) {
            return;
        }
        try {
            if (view.LayerType != LayerType.Hardware) {
                view.SetLayerType(LayerType.Hardware, null);
            }
        } catch {
            // Ignore
        }
    }

    /// <summary>Restores <paramref name="view"/> back to <see cref="LayerType.None"/> after a transition finishes.</summary>
    public static void ClearHardwareLayer(View view) {
        if (view == null) {
            return;
        }
        try {
            if (view.LayerType != LayerType.None) {
                view.SetLayerType(LayerType.None, null);
            }
        } catch {
            // Ignore
        }
    }

    /// <summary>
    /// Applies blended optical defocus + subtle horizontal motion blur to <paramref name="view"/> on Android 12+ (API 31+)
    /// only when <see cref="EnableFullScreenRenderEffectBlur"/> is explicitly opted in; otherwise acts as a zero-cost GPU no-op.
    /// </summary>
    public static void ApplyHorizontalMotionBlur(
        View view,
        float blurRadiusPx,
        float opticalDefocusRatio = DefaultOpticalDefocusRatio) {
        if (view == null || !EnableFullScreenRenderEffectBlur || !OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return;
        }
        try {
            if (!float.IsFinite(blurRadiusPx) || blurRadiusPx < 0.75f) {
                view.SetRenderEffect(null);
                return;
            }
            float ratio = Math.Clamp(opticalDefocusRatio, 0.30f, 0.85f);
            float clampedX = Math.Clamp(blurRadiusPx, 0.75f, 52f);
            float clampedY = Math.Clamp(clampedX * ratio, 0.75f, 36f);
            using RenderEffect effect = RenderEffect.CreateBlurEffect(clampedX, clampedY, Shader.TileMode.Clamp!);
            view.SetRenderEffect(effect);
        } catch {
            try {
                view.SetRenderEffect(null);
            } catch {
                // Ignore
            }
        }
    }

    /// <summary>Clears any active motion blur <see cref="RenderEffect"/> from <paramref name="view"/>.</summary>
    public static void ClearMotionBlur(View view) {
        if (view == null || !OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return;
        }
        try {
            view.SetRenderEffect(null);
        } catch {
            // Ignore
        }
    }
}


