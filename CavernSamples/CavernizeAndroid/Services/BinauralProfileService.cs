using System.IO.Compression;
using System.Text;

using Android.Content;

using Cavern.Format;
using Cavern.Virtualizer;
using Cavern.Waveforms;

using Cavernize.Logic.Spatial;

using CavernizeAndroid.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Categorization groups for official laboratory binaural profiles and user imports.
/// </summary>
public enum BinauralProfileGroup {
    Standard,
    SadieD1,
    ThKoln,
    SadieD2,
    Custom
}

/// <summary>
/// Metadata descriptor for a bundled or custom binaural spatialization profile.
/// </summary>
public sealed class BinauralProfileInfo {
    public string Id { get; init; }
    public BinauralProfileGroup Group { get; init; }
    public string TitleKey { get; init; }
    public string SubtitleKey { get; init; }
    public string ShortBadge { get; init; }
    public string AssetFile { get; init; }
    public float ReferenceDistanceMeters { get; init; }
    public bool IsThKolnDistanceSeries { get; init; }
    public bool IsDirectStereo { get; init; }
    public bool IsClassic { get; init; }
    public bool IsCustom { get; init; }
    public bool IsBrir { get; init; }
    /// <summary>
    /// True when the HRIR retains the dummy head's raw ear-canal/diffuse-field transfer function (e.g., TH Köln Far-Field Raw),
    /// signaling Slot 6A AutoEq to engage the Zero Double-EQ Guard automatically.
    /// </summary>
    public bool IsRawUncompensated { get; init; }
    public bool IsGzipped => !string.IsNullOrEmpty(AssetFile) &&
        (AssetFile.EndsWith(".chrirz", StringComparison.OrdinalIgnoreCase) || AssetFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Manages official offline laboratory HRIR/BRIR profiles (SADIE II D1/D2, TH Köln KU100 Far-Field &amp; Near-Field),
/// global mathematical Minimum-Phase + ITD conversion, laboratory distance selection, Cavern Classic, Direct 2.0,
/// and custom .wav / .sofa imports.
/// </summary>
public static class BinauralProfileService {
    public const string DefaultProfileId = "sadie_d1_dfc";
    public const string Direct20Id = "direct_20";
    public const string ClassicId = "cavern_classic";
    public const string CustomId = "custom";

    static readonly byte[] ChrirMagic = Encoding.ASCII.GetBytes("CHRIR1");

    /// <summary>
    /// Live Slot 5B personalization diagnostics snapshot from the most recent HRIR/BRIR load.
    /// </summary>
    public static HrtfPersonalizationDiagnostics LastPersonalizationDiagnostics { get; private set; } = HrtfPersonalizationDiagnostics.Default;

    /// <summary>
    /// Official laboratory distance progression measured on the same TH Köln Neumann KU100 DFC setup.
    /// </summary>
    public static readonly (string ProfileId, float Meters, string ShortLabel)[] ThKolnDistanceSeries = [
        ("thk_ku100_nf050", 0.50f, "0.50 m"),
        ("thk_ku100_nf075", 0.75f, "0.75 m"),
        ("thk_ku100_nf100", 1.00f, "1.00 m"),
        ("thk_ku100_nf150", 1.50f, "1.50 m"),
        ("thk_ku100_ff_dfc", 3.25f, "3.25 m")
    ];

    /// <summary>
    /// Complete catalog of official and built-in binaural profiles (Min-Phase + ITD is now a universal toggle).
    /// </summary>
    public static IReadOnlyList<BinauralProfileInfo> Profiles { get; } = [
        // SADIE II — D1 (Neumann KU100, Apache 2.0)
        new BinauralProfileInfo {
            Id = "sadie_d1_dfc",
            Group = BinauralProfileGroup.SadieD1,
            TitleKey = "ProfSadieD1Dfc",
            SubtitleKey = "ProfSadieD1DfcSub",
            ShortBadge = "KU100 DFC",
            AssetFile = "sadie_d1_dfc.chrir",
            ReferenceDistanceMeters = 1.20f
        },
        new BinauralProfileInfo {
            Id = "sadie_d1_brir",
            Group = BinauralProfileGroup.SadieD1,
            TitleKey = "ProfSadieD1Brir",
            SubtitleKey = "ProfSadieD1BrirSub",
            ShortBadge = "KU100 BRIR",
            AssetFile = "sadie_d1_brir.chrirz",
            ReferenceDistanceMeters = 1.50f,
            IsBrir = true
        },

        // TH Köln / Bernschütz (Neumann KU100, CC BY) — Real Laboratory Distance Series
        new BinauralProfileInfo {
            Id = "thk_ku100_ff_dfc",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkFfDfc",
            SubtitleKey = "ProfThkFfDfcSub",
            ShortBadge = "THK 3.25m",
            AssetFile = "thk_ku100_ff_dfc.chrir",
            ReferenceDistanceMeters = 3.25f,
            IsThKolnDistanceSeries = true
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf150",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf150",
            SubtitleKey = "ProfThkNf150Sub",
            ShortBadge = "THK 1.50m",
            AssetFile = "thk_ku100_nf150.chrir",
            ReferenceDistanceMeters = 1.50f,
            IsThKolnDistanceSeries = true
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf100",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf100",
            SubtitleKey = "ProfThkNf100Sub",
            ShortBadge = "THK 1.00m",
            AssetFile = "thk_ku100_nf100.chrir",
            ReferenceDistanceMeters = 1.00f,
            IsThKolnDistanceSeries = true
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf075",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf075",
            SubtitleKey = "ProfThkNf075Sub",
            ShortBadge = "THK 0.75m",
            AssetFile = "thk_ku100_nf075.chrir",
            ReferenceDistanceMeters = 0.75f,
            IsThKolnDistanceSeries = true
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf050",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf050",
            SubtitleKey = "ProfThkNf050Sub",
            ShortBadge = "THK 0.50m",
            AssetFile = "thk_ku100_nf050.chrir",
            ReferenceDistanceMeters = 0.50f,
            IsThKolnDistanceSeries = true
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_ff_raw",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkFfRaw",
            SubtitleKey = "ProfThkFfRawSub",
            ShortBadge = "THK Raw",
            AssetFile = "thk_ku100_ff_raw.chrir",
            ReferenceDistanceMeters = 3.25f,
            IsRawUncompensated = true
        },

        // SADIE II — D2 (KEMAR 45BB, Apache 2.0)
        new BinauralProfileInfo {
            Id = "sadie_d2_dfc",
            Group = BinauralProfileGroup.SadieD2,
            TitleKey = "ProfSadieD2Dfc",
            SubtitleKey = "ProfSadieD2DfcSub",
            ShortBadge = "KEMAR DFC",
            AssetFile = "sadie_d2_dfc.chrir",
            ReferenceDistanceMeters = 1.20f
        },
        new BinauralProfileInfo {
            Id = "sadie_d2_brir",
            Group = BinauralProfileGroup.SadieD2,
            TitleKey = "ProfSadieD2Brir",
            SubtitleKey = "ProfSadieD2BrirSub",
            ShortBadge = "KEMAR BRIR",
            AssetFile = "sadie_d2_brir.chrirz",
            ReferenceDistanceMeters = 1.50f,
            IsBrir = true
        },

        // Standard modes
        new BinauralProfileInfo {
            Id = ClassicId,
            Group = BinauralProfileGroup.Standard,
            TitleKey = "ProfCavernClassic",
            SubtitleKey = "ProfCavernClassicSub",
            ShortBadge = "Classic",
            ReferenceDistanceMeters = 1.00f,
            IsClassic = true
        },
        new BinauralProfileInfo {
            Id = Direct20Id,
            Group = BinauralProfileGroup.Standard,
            TitleKey = "ProfDirect20",
            SubtitleKey = "ProfDirect20Sub",
            ShortBadge = "Direct 2.0",
            ReferenceDistanceMeters = 0f,
            IsDirectStereo = true
        },

        // Custom user-imported .wav or .sofa
        new BinauralProfileInfo {
            Id = CustomId,
            Group = BinauralProfileGroup.Custom,
            TitleKey = "ProfCustom",
            SubtitleKey = "ProfCustomSub",
            ShortBadge = "Custom",
            ReferenceDistanceMeters = 0f,
            IsCustom = true
        }
    ];

    /// <summary>
    /// Looks up a profile descriptor by its unique identifier.
    /// </summary>
    public static BinauralProfileInfo FindById(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < Profiles.Count; i++) {
            if (string.Equals(Profiles[i].Id, id, StringComparison.OrdinalIgnoreCase)) {
                return Profiles[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves the stored HRTF profile ID, seamlessly migrating legacy Min-Phase IDs to DFC + MinimumPhaseHrir toggle.
    /// </summary>
    public static string ResolveStoredHrtfProfileId(AppSettingsStore store) {
        string stored = store.BinauralProfileId;
        if (!string.IsNullOrEmpty(stored)) {
            if (string.Equals(stored, "sadie_d1_minphase", StringComparison.OrdinalIgnoreCase)) {
                store.BinauralProfileId = "sadie_d1_dfc";
                store.MinimumPhaseHrir = true;
                return "sadie_d1_dfc";
            }
            if (string.Equals(stored, "sadie_d2_minphase", StringComparison.OrdinalIgnoreCase)) {
                store.BinauralProfileId = "sadie_d2_dfc";
                store.MinimumPhaseHrir = true;
                return "sadie_d2_dfc";
            }
            BinauralProfileInfo info = FindById(stored);
            if (info != null && !info.IsDirectStereo) {
                if (info.IsCustom && (string.IsNullOrEmpty(store.HrirPath) || !File.Exists(store.HrirPath))) {
                    return DefaultProfileId;
                }
                return info.Id;
            }
        }
        if (!string.IsNullOrEmpty(store.HrirPath) && File.Exists(store.HrirPath)) {
            return CustomId;
        }
        return DefaultProfileId;
    }

    /// <summary>
    /// Returns the currently active profile ID, including <see cref="Direct20Id"/> when HRTF is disabled.
    /// </summary>
    public static string GetEffectiveProfileId(AppSettingsStore store) =>
        store.HeadphoneVirtualizer ? ResolveStoredHrtfProfileId(store) : Direct20Id;

    /// <summary>
    /// True when the active profile is an anechoic HRIR using mathematical Minimum-Phase + ITD.
    /// </summary>
    public static bool IsMinimumPhaseEffective(AppSettingsStore store) {
        if (!store.HeadphoneVirtualizer || !store.MinimumPhaseHrir) {
            return false;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        return info != null && !info.IsBrir && !info.IsDirectStereo && !VirtualizerFilter.IsMeasuredBrirActive;
    }

    /// <summary>
    /// True when Slot 5B MagLS Ambisonic decoding is actively applied to the current anechoic HRIR.
    /// </summary>
    public static bool IsMagLsEffective(AppSettingsStore store) {
        if (store == null || !store.HeadphoneVirtualizer || store.MagLsModeIndex <= 0) {
            return false;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        return info != null && !info.IsBrir && !info.IsDirectStereo && !info.IsClassic && !VirtualizerFilter.IsMeasuredBrirActive;
    }

    /// <summary>
    /// Gets the localized display title for the currently active profile.
    /// </summary>
    public static string GetActiveDisplayTitle(AppSettingsStore store, AndroidStrings s) {
        string id = GetEffectiveProfileId(store);
        BinauralProfileInfo info = FindById(id) ?? FindById(DefaultProfileId);
        string title = info.IsCustom && !string.IsNullOrEmpty(store.HrirPath)
            ? $"{s[info.TitleKey]} ({Path.GetFileName(store.HrirPath)})"
            : s[info.TitleKey];
        bool magLs = IsMagLsEffective(store);
        bool minPh = IsMinimumPhaseEffective(store);
        if (magLs && minPh) {
            return $"{title} • MagLS O{store.MagLsModeIndex} • MinPh";
        }
        if (magLs) {
            return $"{title} • MagLS O{store.MagLsModeIndex}";
        }
        if (minPh) {
            return $"{title} • MinPh";
        }
        return title;
    }

    /// <summary>
    /// Gets a compact pill badge label for the Player bar.
    /// </summary>
    public static string GetActiveShortBadge(AppSettingsStore store, AndroidStrings s) {
        if (!store.HeadphoneVirtualizer) {
            return s["PlayerDirectShort"];
        }
        string id = ResolveStoredHrtfProfileId(store);
        BinauralProfileInfo info = FindById(id);
        string baseBadge = info != null ? info.ShortBadge : s["BentoHrtf"];
        if (IsMagLsEffective(store)) {
            return $"{baseBadge} M{store.MagLsModeIndex}";
        }
        return IsMinimumPhaseEffective(store) ? $"{baseBadge} MP" : baseBadge;
    }

    /// <summary>
    /// Returns the real laboratory measurement distance (meters) of the active profile, or 0 when Direct 2.0 is active.
    /// </summary>
    public static float GetActiveDistanceMeters(AppSettingsStore store) {
        if (!store.HeadphoneVirtualizer) {
            return 0f;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        if (info == null || info.IsDirectStereo) {
            return 0f;
        }
        if (info.ReferenceDistanceMeters > 0f) {
            return info.ReferenceDistanceMeters;
        }
        return VirtualizerFilter.ReferenceDistance > 0f ? VirtualizerFilter.ReferenceDistance : 1.00f;
    }

    /// <summary>
    /// Gets a short human-readable laboratory distance label for the 3D Room Distance tile.
    /// </summary>
    public static string GetActiveDistanceShortText(AppSettingsStore store, AndroidStrings s) {
        if (!store.HeadphoneVirtualizer) {
            return s["BinauralDistDirect"];
        }
        float dist = GetActiveDistanceMeters(store);
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        if (info != null && info.IsThKolnDistanceSeries) {
            return $"{dist:0.00} m";
        }
        return $"{dist:0.00} m • {s["BinauralDistFixedTag"]}";
    }

    /// <summary>
    /// True when HRTF virtualization is enabled and the active profile supports multiple real laboratory distances.
    /// </summary>
    public static bool HasVariableDistance(AppSettingsStore store) {
        if (store == null || !store.HeadphoneVirtualizer) {
            return false;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        return info != null && info.IsThKolnDistanceSeries;
    }

    /// <summary>
    /// Returns the next laboratory distance profile ID in the TH Köln KU100 DFC progression (wrapping around).
    /// </summary>
    public static string GetNextDistanceProfileId(AppSettingsStore store) {
        if (ThKolnDistanceSeries.Length == 0) {
            return DefaultProfileId;
        }
        string curId = ResolveStoredHrtfProfileId(store);
        for (int i = 0; i < ThKolnDistanceSeries.Length; i++) {
            if (string.Equals(ThKolnDistanceSeries[i].ProfileId, curId, StringComparison.OrdinalIgnoreCase)) {
                return ThKolnDistanceSeries[(i + 1) % ThKolnDistanceSeries.Length].ProfileId;
            }
        }
        return ThKolnDistanceSeries[0].ProfileId;
    }

    /// <summary>
    /// Applies the user's saved binaural profile at app startup, safely falling back to Cavern Classic if an asset fails.
    /// </summary>
    public static void ApplySavedProfile(Context ctx, AppSettingsStore store) {
        string hrtfId = ResolveStoredHrtfProfileId(store);
        bool wantsHrtf = store.HeadphoneVirtualizer;
        if (!ApplyHrtfFilterOnly(ctx, store, hrtfId, out string err)) {
            AppLogger.Warn($"Startup binaural profile '{hrtfId}' failed ({err}); falling back to Cavern Classic.");
            VirtualizerFilter.Reset(store.MinimumPhaseHrir);
            if (hrtfId == CustomId) {
                store.BinauralProfileId = DefaultProfileId;
            }
        }
        store.HeadphoneVirtualizer = wantsHrtf;
    }

    /// <summary>
    /// Returns the acoustic compensation mode of the currently active binaural profile so Slot 6A AutoEq
    /// can automatically engage the Zero Double-EQ Guard whenever a Raw uncompensated HRIR is active.
    /// </summary>
    public static Cavern.Filters.BinauralCompensationMode GetActiveCompensationMode(AppSettingsStore store) {
        if (store == null || !store.HeadphoneVirtualizer) {
            return Cavern.Filters.BinauralCompensationMode.DirectStereo;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        if (info == null || info.IsDirectStereo) {
            return Cavern.Filters.BinauralCompensationMode.DirectStereo;
        }
        return info.IsRawUncompensated
            ? Cavern.Filters.BinauralCompensationMode.RawUncompensatedHrir
            : Cavern.Filters.BinauralCompensationMode.DiffuseFieldCompensatedHrir;
    }

    /// <summary>
    /// True when the active binaural profile is a measured BRIR (flagged BRIR asset or custom .sofa/.wav &gt; 1024 taps),
    /// signaling Slot 5A to automatically bypass synthetic ISM reflections so room acoustics are never doubled.
    /// </summary>
    public static bool IsActiveProfileBrir(AppSettingsStore store) {
        if (store == null || !store.HeadphoneVirtualizer) {
            return false;
        }
        BinauralProfileInfo info = FindById(ResolveStoredHrtfProfileId(store));
        if (info == null || info.IsDirectStereo) {
            return false;
        }
        return info.IsBrir || VirtualizerFilter.IsMeasuredBrirActive;
    }

    /// <summary>
    /// Mirrors the currently active binaural profile ID, display label, compensation mode, BRIR state,
    /// and Slot 5B personalization telemetry into Slot 3 (<see cref="SpatialSceneHub"/>).
    /// </summary>
    public static void SyncToSceneHub(SpatialSceneHub hub, AppSettingsStore store, AndroidStrings s = null) {
        if (hub == null || store == null) {
            return;
        }
        string effectiveId = GetEffectiveProfileId(store);
        hub.ActiveBinauralProfileId = effectiveId;
        hub.ActiveBinauralCompensationMode = GetActiveCompensationMode(store);
        hub.IsMeasuredBrirActive = IsActiveProfileBrir(store);
        if (s != null) {
            hub.ActiveBinauralProfileName = GetActiveDisplayTitle(store, s);
        } else {
            BinauralProfileInfo info = FindById(effectiveId);
            hub.ActiveBinauralProfileName = info != null ? info.ShortBadge : effectiveId;
        }
        hub.PersonalizationDiagnostics = BuildMergedDiagnostics(store);
    }

    /// <summary>
    /// Builds an up-to-date Slot 5B diagnostics snapshot combining the most recent HRIR load
    /// (MagLS order/cutoff/HF error, Biometric ITD state, BRIR bypass, active format) with live HpTF notch settings.
    /// </summary>
    public static HrtfPersonalizationDiagnostics BuildMergedDiagnostics(AppSettingsStore store) {
        HrtfPersonalizationConfig cfg = store != null ? store.BuildPersonalizationConfig() : HrtfPersonalizationConfig.Default;
        HrtfPersonalizationDiagnostics last = LastPersonalizationDiagnostics;
        double maxItdUs = BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, cfg.HeadWidthCm) + cfg.ManualItdOffsetUs;
        double delta90Us = BiometricItdScaler.ComputeItdDeltaUs(90.0, 0.0, cfg.HeadWidthCm, cfg.ManualItdOffsetUs);
        bool hrtfActive = store != null && store.HeadphoneVirtualizer;
        return new HrtfPersonalizationDiagnostics {
            IsMagLsActive = hrtfActive && last.IsMagLsActive,
            MagLsOrder = hrtfActive ? last.MagLsOrder : 0,
            MagLsCutoffHz = hrtfActive ? last.MagLsCutoffHz : 0f,
            MagLsHfErrorDb = hrtfActive ? last.MagLsHfErrorDb : 0f,
            IsBiometricItdActive = hrtfActive && last.IsBiometricItdActive,
            IsBrirBypassed = hrtfActive && last.IsBrirBypassed,
            HeadWidthCm = cfg.HeadWidthCm,
            MaxLateralItdUs = (float)maxItdUs,
            ItdDeltaAt90DegUs = (float)delta90Us,
            IsHptfNotchActive = hrtfActive && cfg.HptfNotchEnabled && cfg.HptfNotchGainDb < -0.05f,
            HptfNotchFreqHz = cfg.HptfNotchFreqHz,
            HptfNotchGainDb = cfg.HptfNotchGainDb,
            HptfNotchQ = cfg.HptfNotchQ,
            ActiveHrirFormat = hrtfActive ? (last.ActiveHrirFormat ?? "CHRIR1") : "Direct 2.0",
            ActiveClusterId = cfg.PerceptualClusterId ?? ""
        };
    }

    /// <summary>
    /// Selects and activates a binaural profile (bundled, Classic, Direct 2.0, or already-imported Custom).
    /// </summary>
    public static bool ApplyProfile(Context ctx, AppSettingsStore store, string profileId, out string errorMessage) {
        errorMessage = null;
        BinauralProfileInfo info = FindById(profileId);
        if (info == null) {
            errorMessage = string.Format(AndroidStrings.Active["BinauralUnknownProfile"], profileId);
            return false;
        }

        if (info.IsDirectStereo) {
            store.HeadphoneVirtualizer = false;
            LastPersonalizationDiagnostics = BuildMergedDiagnostics(store);
            AppLogger.Info("Switched binaural profile to Direct 2.0 (HRTF bypassed).");
            return true;
        }

        if (!ApplyHrtfFilterOnly(ctx, store, info.Id, out errorMessage)) {
            return false;
        }
        store.BinauralProfileId = info.Id;
        store.HeadphoneVirtualizer = true;
        return true;
    }

    /// <summary>
    /// Toggles global mathematical Minimum-Phase + ITD mode and immediately rebuilds the active anechoic HRIR filter.
    /// </summary>
    public static bool SetMinimumPhase(Context ctx, AppSettingsStore store, bool enabled, out string errorMessage) {
        errorMessage = null;
        store.MinimumPhaseHrir = enabled;
        if (!store.HeadphoneVirtualizer) {
            return true;
        }
        string activeId = ResolveStoredHrtfProfileId(store);
        BinauralProfileInfo info = FindById(activeId);
        if (info != null && info.IsBrir) {
            AppLogger.Info($"Minimum-Phase + ITD set to {enabled} (automatically bypassed while BRIR '{info.Id}' is active).");
            return true;
        }
        return ApplyHrtfFilterOnly(ctx, store, activeId, out errorMessage);
    }

    /// <summary>
    /// Rebuilds the active anechoic HRIR filter bank whenever Slot 5B HRIR parameters (MagLS order or Biometric ITD)
    /// are modified by the user. Automatically bypassed for Direct 2.0 or measured BRIR profiles.
    /// </summary>
    public static bool ApplyPersonalization(Context ctx, AppSettingsStore store, out string errorMessage) {
        errorMessage = null;
        if (store == null || !store.HeadphoneVirtualizer) {
            LastPersonalizationDiagnostics = BuildMergedDiagnostics(store);
            return true;
        }
        string activeId = ResolveStoredHrtfProfileId(store);
        return ApplyHrtfFilterOnly(ctx, store, activeId, out errorMessage);
    }

    /// <summary>
    /// Imports a user-selected custom .wav (Impulcifer/HeSuVi/Cavern) or .sofa (AES69) file into private storage
    /// and activates it immediately with the current Slot 5B personalization settings.
    /// </summary>
    public static bool ImportCustomFile(Context ctx, AppSettingsStore store, string sourcePath, out string errorMessage) {
        errorMessage = null;
        try {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) {
                errorMessage = AndroidStrings.Active["HrirFileNotFound"];
                return false;
            }

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".wav" && ext != ".sofa") {
                errorMessage = AndroidStrings.Active["HrirUnsupportedFormat"];
                return false;
            }

            // Copy to app private storage so Android scoped-storage permissions never invalidate it later
            string customDir = Path.Combine(ctx.FilesDir.AbsolutePath, "custom_hrir");
            Directory.CreateDirectory(customDir);
            string fileName = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) {
                fileName = "custom_hrir" + ext;
            }
            string destPath = Path.Combine(customDir, fileName);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase)) {
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            HrtfPersonalizationConfig personalization = store.BuildPersonalizationConfig();
            LoadAndOverrideCustom(destPath, store.MinimumPhaseHrir, personalization);
            store.HrirPath = destPath;
            store.BinauralProfileId = CustomId;
            store.HeadphoneVirtualizer = true;
            AppLogger.Info($"Imported and activated custom binaural profile: '{destPath}' (fmt={LastPersonalizationDiagnostics.ActiveHrirFormat}, minPhase={store.MinimumPhaseHrir}, magLs={personalization.MagLsMode}, bioItd={personalization.BiometricItdEnabled})");
            return true;
        } catch (Exception ex) {
            AppLogger.Error("BinauralProfileService.ImportCustomFile", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads the official attribution and license text bundled in Assets/hrir/LICENSES.txt.
    /// </summary>
    public static string ReadLicenseCredits(Context ctx) {
        try {
            using Stream s = ctx.Assets.Open("hrir/LICENSES.txt");
            using StreamReader sr = new(s, Encoding.UTF8);
            return sr.ReadToEnd();
        } catch {
            return "SADIE II Database — AudioLab, University of York (Apache License 2.0)\n" +
                   "TH Köln / Bernschütz Neumann KU100 HRIR Database (CC BY 3.0 / 4.0)";
        }
    }

    sealed class CachedRawChrir {
        public MeasuredHrirSpeaker[] Speakers;
        public int SampleRate;
        public float RefDist;
        public float CrossoverHz;
        public int TapCount;
        public bool IsGzipped;
    }

    static readonly object chrirCacheLock = new();
    static readonly Dictionary<string, CachedRawChrir> anechoicChrirCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a bundled laboratory HRIR/BRIR profile into fresh <see cref="VirtualChannel"/> instances with the
    /// requested Slot 5B personalization parameters, without mutating <see cref="LastPersonalizationDiagnostics"/>
    /// or user settings. Used by Layer L11 <see cref="AcousticAuditEngine"/> inside an isolated
    /// <see cref="VirtualizerFilter.BeginStaticStateScope"/>.
    /// Callers should release returned probe channels via <see cref="VirtualizerFilter.DisposeChannels"/> when finished.
    /// </summary>
    internal static (VirtualChannel[] channels, int sampleRate, float refDist, int tapCount, bool isBrir, HrtfPersonalizationDiagnostics diag) LoadBundledProfileForProbe(
        Context ctx,
        string profileId,
        bool minimumPhase,
        HrtfPersonalizationConfig personalization) {
        BinauralProfileInfo info = FindById(profileId) ?? FindById(DefaultProfileId);
        if (info == null || info.IsDirectStereo || info.IsClassic || info.IsCustom || string.IsNullOrEmpty(info.AssetFile)) {
            return (null, VirtualizerFilter.FilterSampleRate, 1.0f, 0, false, HrtfPersonalizationDiagnostics.Default);
        }

        CachedRawChrir cached = null;
        if (!info.IsBrir) {
            lock (chrirCacheLock) {
                anechoicChrirCache.TryGetValue(info.AssetFile, out cached);
            }
        }
        if (cached != null) {
            (VirtualChannel[] cachedCh, HrtfPersonalizationDiagnostics cachedDiag) = BuildPersonalizedChannels(
                cached.Speakers,
                cached.SampleRate,
                cached.CrossoverHz,
                minimumPhase && !info.IsBrir,
                personalization,
                forceBrir: false,
                formatLabel: cached.IsGzipped ? "CHRIR1 BRIR" : "CHRIR1 HRIR");
            return (cachedCh, cached.SampleRate, cached.RefDist, cached.TapCount, info.IsBrir, cachedDiag);
        }

        using Stream assetStream = ctx.Assets.Open("hrir/" + info.AssetFile);
        CachedRawChrir parsed = ParseRawChrirStream(assetStream, info.IsGzipped);
        if (!info.IsBrir && parsed.TapCount <= MinimumPhaseHrir.MaxAnechoicTaps) {
            lock (chrirCacheLock) {
                anechoicChrirCache[info.AssetFile] = parsed;
            }
        }
        (VirtualChannel[] channels, HrtfPersonalizationDiagnostics diag) = BuildPersonalizedChannels(
            parsed.Speakers,
            parsed.SampleRate,
            parsed.CrossoverHz,
            minimumPhase && !info.IsBrir,
            personalization,
            forceBrir: info.IsBrir,
            formatLabel: parsed.IsGzipped ? "CHRIR1 BRIR" : "CHRIR1 HRIR");
        return (channels, parsed.SampleRate, parsed.RefDist, parsed.TapCount, info.IsBrir, diag);
    }

    static bool ApplyHrtfFilterOnly(Context ctx, AppSettingsStore store, string profileId, out string errorMessage) {
        errorMessage = null;
        try {
            BinauralProfileInfo info = FindById(profileId);
            HrtfPersonalizationConfig personalization = store.BuildPersonalizationConfig();
            bool useMinPhase = store.MinimumPhaseHrir && !(info?.IsBrir ?? false);
            if (info == null || info.IsClassic) {
                VirtualizerFilter.Reset(useMinPhase);
                LastPersonalizationDiagnostics = new HrtfPersonalizationDiagnostics {
                    IsMagLsActive = false,
                    MagLsOrder = 0,
                    MagLsCutoffHz = 0f,
                    MagLsHfErrorDb = 0f,
                    IsBiometricItdActive = false,
                    IsBrirBypassed = false,
                    HeadWidthCm = personalization.HeadWidthCm,
                    MaxLateralItdUs = (float)(BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, personalization.HeadWidthCm) + personalization.ManualItdOffsetUs),
                    ItdDeltaAt90DegUs = (float)BiometricItdScaler.ComputeItdDeltaUs(90.0, 0.0, personalization.HeadWidthCm, personalization.ManualItdOffsetUs),
                    IsHptfNotchActive = personalization.HptfNotchEnabled && personalization.HptfNotchGainDb < -0.05f,
                    HptfNotchFreqHz = personalization.HptfNotchFreqHz,
                    HptfNotchGainDb = personalization.HptfNotchGainDb,
                    HptfNotchQ = personalization.HptfNotchQ,
                    ActiveHrirFormat = "Cavern Classic",
                    ActiveClusterId = personalization.PerceptualClusterId ?? ""
                };
                AppLogger.Info($"Activated binaural profile: Cavern Classic (minPhase={useMinPhase})");
                return true;
            }

            if (info.IsCustom) {
                if (string.IsNullOrEmpty(store.HrirPath) || !File.Exists(store.HrirPath)) {
                    errorMessage = AndroidStrings.Active["CustomHrirNotFound"];
                    return false;
                }
                LoadAndOverrideCustom(store.HrirPath, useMinPhase, personalization);
                AppLogger.Info($"Activated custom binaural profile: '{store.HrirPath}' (fmt={LastPersonalizationDiagnostics.ActiveHrirFormat}, minPhase={useMinPhase}, magLs={LastPersonalizationDiagnostics.MagLsOrder}, bioItd={LastPersonalizationDiagnostics.IsBiometricItdActive})");
                return true;
            }

            using Stream assetStream = ctx.Assets.Open("hrir/" + info.AssetFile);
            (VirtualChannel[] channels, int sampleRate, float refDist, int taps, HrtfPersonalizationDiagnostics diag) =
                ReadChrirStream(assetStream, info.IsGzipped, useMinPhase, personalization, info.IsBrir);
            VirtualizerFilter.Override(channels, sampleRate, refDist);
            LastPersonalizationDiagnostics = diag;
            AppLogger.Info($"Activated official binaural profile '{info.Id}' ({channels.Length} ch, {taps} taps @ {sampleRate} Hz, d={refDist:0.00} m, minPhase={useMinPhase}, magLsO={diag.MagLsOrder}, bioItd={diag.IsBiometricItdActive}, brirBypass={diag.IsBrirBypassed})");
            return true;
        } catch (Exception ex) {
            AppLogger.Error($"BinauralProfileService.Apply({profileId})", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    static void LoadAndOverrideCustom(string path, bool minimumPhase, HrtfPersonalizationConfig personalization) {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".sofa") {
            SofaHrirReader.SofaLoadResult result = SofaHrirReader.Load(path, minimumPhase, personalization);
            VirtualizerFilter.Override(result.Channels, result.SampleRate, result.ReferenceDistance);
            LastPersonalizationDiagnostics = new HrtfPersonalizationDiagnostics {
                IsMagLsActive = result.MagLsMetrics.HasValue,
                MagLsOrder = result.MagLsMetrics?.Order ?? 0,
                MagLsCutoffHz = result.MagLsMetrics?.CutoffFrequencyHz ?? 0f,
                MagLsHfErrorDb = result.MagLsMetrics?.MeanHfMagnitudeErrorDb ?? 0f,
                IsBiometricItdActive = result.BiometricItdApplied,
                IsBrirBypassed = result.BrirBypassed,
                HeadWidthCm = personalization.HeadWidthCm,
                MaxLateralItdUs = (float)(BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, personalization.HeadWidthCm) + personalization.ManualItdOffsetUs),
                ItdDeltaAt90DegUs = (float)BiometricItdScaler.ComputeItdDeltaUs(90.0, 0.0, personalization.HeadWidthCm, personalization.ManualItdOffsetUs),
                IsHptfNotchActive = personalization.HptfNotchEnabled && personalization.HptfNotchGainDb < -0.05f,
                HptfNotchFreqHz = personalization.HptfNotchFreqHz,
                HptfNotchGainDb = personalization.HptfNotchGainDb,
                HptfNotchQ = personalization.HptfNotchQ,
                ActiveHrirFormat = $"AES69 SOFA ({result.MeasuredDirectionCount} dirs)",
                ActiveClusterId = personalization.PerceptualClusterId ?? ""
            };
        } else {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            ParsedWavHrirSet parsed = ImpulciferHrirParser.Parse(
                new MultichannelWaveform(reader.ReadMultichannelAfterHeader()),
                reader.SampleRate,
                VirtualizerFilter.FilterSampleRate);
            (VirtualChannel[] channels, HrtfPersonalizationDiagnostics diag) = BuildPersonalizedChannels(
                parsed.Speakers,
                parsed.SampleRate,
                crossoverHz: 0f,
                minimumPhase,
                personalization,
                forceBrir: parsed.IsBrir,
                formatLabel: parsed.FormatName);
            VirtualizerFilter.Override(channels, parsed.SampleRate);
            LastPersonalizationDiagnostics = diag;
        }
    }

    /// <summary>
    /// Shared Slot 5B HRIR builder: optionally applies MagLS spherical-harmonic decoding, single-scalar global
    /// unity-energy normalization, Woodworth-Schlosberg biometric ITD scaling, and Minimum-Phase + ITD conversion.
    /// Automatically bypasses all three transforms on measured room BRIRs (&gt; 1024 taps) to protect room tails.
    /// </summary>
    static (VirtualChannel[] channels, HrtfPersonalizationDiagnostics diag) BuildPersonalizedChannels(
        MeasuredHrirSpeaker[] speakers,
        int sampleRate,
        float crossoverHz,
        bool minimumPhase,
        HrtfPersonalizationConfig personalization,
        bool forceBrir,
        string formatLabel) {
        int count = speakers.Length;
        int maxTap = 0;
        for (int i = 0; i < count; i++) {
            if (speakers[i].LeftIr.Length > maxTap) maxTap = speakers[i].LeftIr.Length;
            if (speakers[i].RightIr.Length > maxTap) maxTap = speakers[i].RightIr.Length;
        }

        bool isBrir = forceBrir || maxTap > MinimumPhaseHrir.MaxAnechoicTaps;
        bool wantsMagLs = personalization.MagLsMode != MagLsDecodeMode.Off;
        bool wantsBioItd = personalization.BiometricItdEnabled &&
            !BiometricItdScaler.IsBypass(personalization.HeadWidthCm, personalization.ManualItdOffsetUs);
        bool brirBypassed = isBrir && (wantsMagLs || wantsBioItd || minimumPhase);

        float[][] leftEars = new float[count][];
        float[][] rightEars = new float[count][];
        MagLsDesignMetrics? magLsMetrics = null;

        if (!isBrir && wantsMagLs && count >= 4) {
            double[] measAz = new double[count];
            double[] measEl = new double[count];
            float[] tgtEl = new float[count];
            float[] tgtAz = new float[count];
            float[][] rawL = new float[count][];
            float[][] rawR = new float[count][];
            for (int i = 0; i < count; i++) {
                tgtEl[i] = speakers[i].ElevationDeg;
                tgtAz[i] = speakers[i].AzimuthDeg;
                MagLsAmbisonicDecoder.CavernAnglesToAmbisonicRad(tgtEl[i], tgtAz[i], out measAz[i], out measEl[i]);
                rawL[i] = speakers[i].LeftIr;
                rawR[i] = speakers[i].RightIr;
            }
            MagLsAmbisonicDecoder.SynthesizeVirtualSpeakerHrirs(
                measAz,
                measEl,
                rawL,
                rawR,
                tgtEl,
                tgtAz,
                sampleRate,
                (int)personalization.MagLsMode,
                out leftEars,
                out rightEars,
                out MagLsDesignMetrics metrics,
                personalization.MagLsCutoffHz);
            magLsMetrics = metrics;

            // Preserve single-scalar global unity-energy L2 normalization across front speakers after MagLS synthesis
            double frontEnergy = 0.0;
            int frontCount = 0;
            for (int i = 0; i < count; i++) {
                if (MathF.Abs(speakers[i].ElevationDeg) < 15f && MathF.Abs(speakers[i].AzimuthDeg) <= 50f) {
                    double e = 0.0;
                    float[] l = leftEars[i];
                    float[] r = rightEars[i];
                    int nLen = Math.Min(l.Length, r.Length);
                    for (int n = 0; n < nLen; n++) {
                        e += (double)l[n] * l[n] + (double)r[n] * r[n];
                    }
                    frontEnergy += e;
                    frontCount++;
                }
            }
            if (frontCount > 0 && frontEnergy > 1e-12) {
                float scale = (float)(1.0 / Math.Sqrt(frontEnergy / frontCount));
                for (int i = 0; i < count; i++) {
                    for (int n = 0; n < leftEars[i].Length; n++) leftEars[i][n] *= scale;
                    for (int n = 0; n < rightEars[i].Length; n++) rightEars[i][n] *= scale;
                }
            }
        } else {
            for (int i = 0; i < count; i++) {
                leftEars[i] = speakers[i].LeftIr;
                rightEars[i] = speakers[i].RightIr;
            }
        }

        bool applyBioItd = !isBrir && wantsBioItd;
        bool applyMinPhase = minimumPhase && !isBrir;
        VirtualChannel[] channels = new VirtualChannel[count];
        for (int i = 0; i < count; i++) {
            float[] irL = leftEars[i];
            float[] irR = rightEars[i];
            if (applyBioItd) {
                BiometricItdScaler.ApplyToPair(
                    irL,
                    irR,
                    speakers[i].ElevationDeg,
                    speakers[i].AzimuthDeg,
                    sampleRate,
                    personalization.HeadWidthCm,
                    personalization.ManualItdOffsetUs,
                    out irL,
                    out irR);
            }
            channels[i] = VirtualChannel.FromMeasured(
                speakers[i].ElevationDeg,
                speakers[i].AzimuthDeg,
                irL,
                irR,
                sampleRate,
                crossoverHz,
                applyMinPhase);
        }

        HrtfPersonalizationDiagnostics diag = new() {
            IsMagLsActive = magLsMetrics.HasValue,
            MagLsOrder = magLsMetrics?.Order ?? 0,
            MagLsCutoffHz = magLsMetrics?.CutoffFrequencyHz ?? 0f,
            MagLsHfErrorDb = magLsMetrics?.MeanHfMagnitudeErrorDb ?? 0f,
            IsBiometricItdActive = applyBioItd,
            IsBrirBypassed = brirBypassed,
            HeadWidthCm = personalization.HeadWidthCm,
            MaxLateralItdUs = (float)(BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, personalization.HeadWidthCm) + personalization.ManualItdOffsetUs),
            ItdDeltaAt90DegUs = (float)BiometricItdScaler.ComputeItdDeltaUs(90.0, 0.0, personalization.HeadWidthCm, personalization.ManualItdOffsetUs),
            IsHptfNotchActive = personalization.HptfNotchEnabled && personalization.HptfNotchGainDb < -0.05f,
            HptfNotchFreqHz = personalization.HptfNotchFreqHz,
            HptfNotchGainDb = personalization.HptfNotchGainDb,
            HptfNotchQ = personalization.HptfNotchQ,
            ActiveHrirFormat = formatLabel,
            ActiveClusterId = personalization.PerceptualClusterId ?? ""
        };
        return (channels, diag);
    }

    /// <summary>
    /// Parses a binary CHRIR1 stream (plain .chrir or GZip-compressed .chrirz) into <see cref="VirtualChannel"/> instances
    /// with optional Slot 5B MagLS &amp; Biometric ITD personalization.
    /// </summary>
    static (VirtualChannel[] channels, int sampleRate, float refDist, int tapCount, HrtfPersonalizationDiagnostics diag) ReadChrirStream(
        Stream source,
        bool gzipped,
        bool minimumPhase,
        HrtfPersonalizationConfig personalization,
        bool isBrirAsset) {
        CachedRawChrir parsed = ParseRawChrirStream(source, gzipped);
        (VirtualChannel[] channels, HrtfPersonalizationDiagnostics diag) = BuildPersonalizedChannels(
            parsed.Speakers,
            parsed.SampleRate,
            parsed.CrossoverHz,
            minimumPhase,
            personalization,
            forceBrir: isBrirAsset,
            formatLabel: gzipped ? "CHRIR1 BRIR" : "CHRIR1 HRIR");
        return (channels, parsed.SampleRate, parsed.RefDist, parsed.TapCount, diag);
    }

    static CachedRawChrir ParseRawChrirStream(Stream source, bool gzipped) {
        Stream dataStream = gzipped ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : source;
        try {
            using BinaryReader br = new(dataStream, Encoding.ASCII, leaveOpen: true);
            byte[] magic = br.ReadBytes(6);
            if (magic.Length != 6) {
                throw new InvalidDataException("Truncated CHRIR1 header.");
            }
            for (int i = 0; i < 6; i++) {
                if (magic[i] != ChrirMagic[i]) {
                    throw new InvalidDataException("Invalid CHRIR1 magic header.");
                }
            }

            ushort version = br.ReadUInt16();
            if (version != 1) {
                throw new InvalidDataException($"Unsupported CHRIR version {version}.");
            }

            int sampleRate = br.ReadInt32();
            float refDist = br.ReadSingle();
            float crossoverHz = br.ReadSingle();
            ushort channelCount = br.ReadUInt16();
            ushort sampleFormat = br.ReadUInt16();
            int tapCount = br.ReadInt32();

            if (sampleFormat != 0 || channelCount == 0 || tapCount <= 0 || sampleRate <= 0) {
                throw new InvalidDataException($"Invalid CHRIR1 parameters (fmt={sampleFormat}, ch={channelCount}, taps={tapCount}, sr={sampleRate}).");
            }

            MeasuredHrirSpeaker[] speakers = new MeasuredHrirSpeaker[channelCount];
            for (int c = 0; c < channelCount; c++) {
                float x = br.ReadSingle();
                float y = br.ReadSingle();
                float[] left = new float[tapCount];
                float[] right = new float[tapCount];
                for (int n = 0; n < tapCount; n++) {
                    left[n] = br.ReadSingle();
                }
                for (int n = 0; n < tapCount; n++) {
                    right[n] = br.ReadSingle();
                }
                speakers[c] = new MeasuredHrirSpeaker(x, y, left, right);
            }

            return new CachedRawChrir {
                Speakers = speakers,
                SampleRate = sampleRate,
                RefDist = refDist,
                CrossoverHz = crossoverHz,
                TapCount = tapCount,
                IsGzipped = gzipped
            };
        } finally {
            if (gzipped) {
                dataStream.Dispose();
            }
        }
    }
}
