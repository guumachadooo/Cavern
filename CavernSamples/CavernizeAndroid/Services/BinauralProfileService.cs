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
            ReferenceDistanceMeters = 3.25f
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
        return info != null && !info.IsBrir && !info.IsDirectStereo;
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
        if (IsMinimumPhaseEffective(store)) {
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
    /// Mirrors the currently active binaural profile ID and display label into Slot 3 (<see cref="SpatialSceneHub"/>).
    /// </summary>
    public static void SyncToSceneHub(SpatialSceneHub hub, AppSettingsStore store, AndroidStrings s = null) {
        if (hub == null || store == null) {
            return;
        }
        string effectiveId = GetEffectiveProfileId(store);
        hub.ActiveBinauralProfileId = effectiveId;
        if (s != null) {
            hub.ActiveBinauralProfileName = GetActiveDisplayTitle(store, s);
        } else {
            BinauralProfileInfo info = FindById(effectiveId);
            hub.ActiveBinauralProfileName = info != null ? info.ShortBadge : effectiveId;
        }
    }

    /// <summary>
    /// Selects and activates a binaural profile (bundled, Classic, Direct 2.0, or already-imported Custom).
    /// </summary>
    public static bool ApplyProfile(Context ctx, AppSettingsStore store, string profileId, out string errorMessage) {
        errorMessage = null;
        BinauralProfileInfo info = FindById(profileId);
        if (info == null) {
            errorMessage = $"Unknown binaural profile '{profileId}'.";
            return false;
        }

        if (info.IsDirectStereo) {
            store.HeadphoneVirtualizer = false;
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
    /// Imports a user-selected custom .wav or .sofa file into private storage and activates it immediately.
    /// </summary>
    public static bool ImportCustomFile(Context ctx, AppSettingsStore store, string sourcePath, out string errorMessage) {
        errorMessage = null;
        try {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) {
                errorMessage = "Selected HRIR/SOFA file does not exist.";
                return false;
            }

            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".wav" && ext != ".sofa") {
                errorMessage = "Unsupported file format. Please select a .sofa or .wav HRIR/BRIR file.";
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

            LoadAndOverrideCustom(destPath, store.MinimumPhaseHrir);
            store.HrirPath = destPath;
            store.BinauralProfileId = CustomId;
            store.HeadphoneVirtualizer = true;
            AppLogger.Info($"Imported and activated custom binaural profile: '{destPath}' (minPhase={store.MinimumPhaseHrir})");
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

    static bool ApplyHrtfFilterOnly(Context ctx, AppSettingsStore store, string profileId, out string errorMessage) {
        errorMessage = null;
        try {
            BinauralProfileInfo info = FindById(profileId);
            bool useMinPhase = store.MinimumPhaseHrir && !(info?.IsBrir ?? false);
            if (info == null || info.IsClassic) {
                VirtualizerFilter.Reset(useMinPhase);
                AppLogger.Info($"Activated binaural profile: Cavern Classic (minPhase={useMinPhase})");
                return true;
            }

            if (info.IsCustom) {
                if (string.IsNullOrEmpty(store.HrirPath) || !File.Exists(store.HrirPath)) {
                    errorMessage = "Custom HRIR/SOFA file not found.";
                    return false;
                }
                LoadAndOverrideCustom(store.HrirPath, useMinPhase);
                AppLogger.Info($"Activated custom binaural profile: '{store.HrirPath}' (minPhase={useMinPhase})");
                return true;
            }

            using Stream assetStream = ctx.Assets.Open("hrir/" + info.AssetFile);
            (VirtualChannel[] channels, int sampleRate, float refDist, int taps) = ReadChrirStream(assetStream, info.IsGzipped, useMinPhase);
            VirtualizerFilter.Override(channels, sampleRate, refDist);
            AppLogger.Info($"Activated official binaural profile '{info.Id}' ({channels.Length} ch, {taps} taps @ {sampleRate} Hz, d={refDist:0.00} m, minPhase={useMinPhase})");
            return true;
        } catch (Exception ex) {
            AppLogger.Error($"BinauralProfileService.Apply({profileId})", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    static void LoadAndOverrideCustom(string path, bool minimumPhase) {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".sofa") {
            SofaHrirReader.SofaLoadResult result = SofaHrirReader.Load(path, minimumPhase);
            VirtualizerFilter.Override(result.Channels, result.SampleRate, result.ReferenceDistance);
        } else {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            VirtualChannel[] channels = VirtualChannel.Parse(
                new MultichannelWaveform(reader.ReadMultichannelAfterHeader()),
                reader.SampleRate,
                0f,
                minimumPhase);
            VirtualizerFilter.Override(channels, reader.SampleRate);
        }
    }

    /// <summary>
    /// Parses a binary CHRIR1 stream (plain .chrir or GZip-compressed .chrirz) into <see cref="VirtualChannel"/> instances.
    /// </summary>
    static (VirtualChannel[] channels, int sampleRate, float refDist, int tapCount) ReadChrirStream(Stream source, bool gzipped, bool minimumPhase) {
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

            bool applyMinPhase = minimumPhase && tapCount <= MinimumPhaseHrir.MaxAnechoicTaps;
            VirtualChannel[] channels = new VirtualChannel[channelCount];
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
                channels[c] = VirtualChannel.FromMeasured(x, y, left, right, sampleRate, crossoverHz, applyMinPhase);
            }

            return (channels, sampleRate, refDist, tapCount);
        } finally {
            if (gzipped) {
                dataStream.Dispose();
            }
        }
    }
}
