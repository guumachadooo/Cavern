using System.IO.Compression;
using System.Text;

using Android.Content;

using Cavern.Format;
using Cavern.Virtualizer;
using Cavern.Waveforms;

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
    public bool IsDirectStereo { get; init; }
    public bool IsClassic { get; init; }
    public bool IsCustom { get; init; }
    public bool IsBrir { get; init; }
    public bool IsGzipped => !string.IsNullOrEmpty(AssetFile) &&
        (AssetFile.EndsWith(".chrirz", StringComparison.OrdinalIgnoreCase) || AssetFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Manages official offline laboratory HRIR/BRIR profiles (SADIE II D1/D2, TH Köln KU100 Far-Field &amp; Near-Field),
/// Cavern Classic, Direct 2.0, and custom .wav / .sofa imports.
/// </summary>
public static class BinauralProfileService {
    public const string DefaultProfileId = "sadie_d1_dfc";
    public const string Direct20Id = "direct_20";
    public const string ClassicId = "cavern_classic";
    public const string CustomId = "custom";

    static readonly byte[] ChrirMagic = Encoding.ASCII.GetBytes("CHRIR1");

    /// <summary>
    /// Complete catalog of official and built-in binaural profiles.
    /// </summary>
    public static IReadOnlyList<BinauralProfileInfo> Profiles { get; } = [
        // SADIE II — D1 (Neumann KU100, Apache 2.0)
        new BinauralProfileInfo {
            Id = "sadie_d1_dfc",
            Group = BinauralProfileGroup.SadieD1,
            TitleKey = "ProfSadieD1Dfc",
            SubtitleKey = "ProfSadieD1DfcSub",
            ShortBadge = "KU100 DFC",
            AssetFile = "sadie_d1_dfc.chrir"
        },
        new BinauralProfileInfo {
            Id = "sadie_d1_minphase",
            Group = BinauralProfileGroup.SadieD1,
            TitleKey = "ProfSadieD1Min",
            SubtitleKey = "ProfSadieD1MinSub",
            ShortBadge = "KU100 MinPh",
            AssetFile = "sadie_d1_minphase.chrir"
        },
        new BinauralProfileInfo {
            Id = "sadie_d1_brir",
            Group = BinauralProfileGroup.SadieD1,
            TitleKey = "ProfSadieD1Brir",
            SubtitleKey = "ProfSadieD1BrirSub",
            ShortBadge = "KU100 BRIR",
            AssetFile = "sadie_d1_brir.chrirz",
            IsBrir = true
        },

        // TH Köln / Bernschütz (Neumann KU100, CC BY)
        new BinauralProfileInfo {
            Id = "thk_ku100_ff_dfc",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkFfDfc",
            SubtitleKey = "ProfThkFfDfcSub",
            ShortBadge = "THK FF DFC",
            AssetFile = "thk_ku100_ff_dfc.chrir"
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_ff_raw",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkFfRaw",
            SubtitleKey = "ProfThkFfRawSub",
            ShortBadge = "THK FF Raw",
            AssetFile = "thk_ku100_ff_raw.chrir"
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf050",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf050",
            SubtitleKey = "ProfThkNf050Sub",
            ShortBadge = "THK 0.5m",
            AssetFile = "thk_ku100_nf050.chrir"
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf075",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf075",
            SubtitleKey = "ProfThkNf075Sub",
            ShortBadge = "THK 0.75m",
            AssetFile = "thk_ku100_nf075.chrir"
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf100",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf100",
            SubtitleKey = "ProfThkNf100Sub",
            ShortBadge = "THK 1.0m",
            AssetFile = "thk_ku100_nf100.chrir"
        },
        new BinauralProfileInfo {
            Id = "thk_ku100_nf150",
            Group = BinauralProfileGroup.ThKoln,
            TitleKey = "ProfThkNf150",
            SubtitleKey = "ProfThkNf150Sub",
            ShortBadge = "THK 1.5m",
            AssetFile = "thk_ku100_nf150.chrir"
        },

        // SADIE II — D2 (KEMAR 45BB, Apache 2.0)
        new BinauralProfileInfo {
            Id = "sadie_d2_dfc",
            Group = BinauralProfileGroup.SadieD2,
            TitleKey = "ProfSadieD2Dfc",
            SubtitleKey = "ProfSadieD2DfcSub",
            ShortBadge = "KEMAR DFC",
            AssetFile = "sadie_d2_dfc.chrir"
        },
        new BinauralProfileInfo {
            Id = "sadie_d2_minphase",
            Group = BinauralProfileGroup.SadieD2,
            TitleKey = "ProfSadieD2Min",
            SubtitleKey = "ProfSadieD2MinSub",
            ShortBadge = "KEMAR MinPh",
            AssetFile = "sadie_d2_minphase.chrir"
        },
        new BinauralProfileInfo {
            Id = "sadie_d2_brir",
            Group = BinauralProfileGroup.SadieD2,
            TitleKey = "ProfSadieD2Brir",
            SubtitleKey = "ProfSadieD2BrirSub",
            ShortBadge = "KEMAR BRIR",
            AssetFile = "sadie_d2_brir.chrirz",
            IsBrir = true
        },

        // Standard modes
        new BinauralProfileInfo {
            Id = ClassicId,
            Group = BinauralProfileGroup.Standard,
            TitleKey = "ProfCavernClassic",
            SubtitleKey = "ProfCavernClassicSub",
            ShortBadge = "Classic",
            IsClassic = true
        },
        new BinauralProfileInfo {
            Id = Direct20Id,
            Group = BinauralProfileGroup.Standard,
            TitleKey = "ProfDirect20",
            SubtitleKey = "ProfDirect20Sub",
            ShortBadge = "Direct 2.0",
            IsDirectStereo = true
        },

        // Custom user-imported .wav or .sofa
        new BinauralProfileInfo {
            Id = CustomId,
            Group = BinauralProfileGroup.Custom,
            TitleKey = "ProfCustom",
            SubtitleKey = "ProfCustomSub",
            ShortBadge = "Custom",
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
    /// Resolves the stored HRTF profile ID (preserving legacy <see cref="AppSettingsStore.HrirPath"/> when upgrading).
    /// </summary>
    public static string ResolveStoredHrtfProfileId(AppSettingsStore store) {
        string stored = store.BinauralProfileId;
        if (!string.IsNullOrEmpty(stored)) {
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
    /// Gets the localized display title for the currently active profile.
    /// </summary>
    public static string GetActiveDisplayTitle(AppSettingsStore store, AndroidStrings s) {
        string id = GetEffectiveProfileId(store);
        BinauralProfileInfo info = FindById(id) ?? FindById(DefaultProfileId);
        if (info.IsCustom && !string.IsNullOrEmpty(store.HrirPath)) {
            return $"{s[info.TitleKey]} ({Path.GetFileName(store.HrirPath)})";
        }
        return s[info.TitleKey];
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
        return info != null ? info.ShortBadge : s["BentoHrtf"];
    }

    /// <summary>
    /// Applies the user's saved binaural profile at app startup, safely falling back to Cavern Classic if an asset fails.
    /// </summary>
    public static void ApplySavedProfile(Context ctx, AppSettingsStore store) {
        string hrtfId = ResolveStoredHrtfProfileId(store);
        bool wantsHrtf = store.HeadphoneVirtualizer;
        if (!ApplyHrtfFilterOnly(ctx, store, hrtfId, out string err)) {
            AppLogger.Warn($"Startup binaural profile '{hrtfId}' failed ({err}); falling back to Cavern Classic.");
            VirtualizerFilter.Reset();
            if (hrtfId == CustomId) {
                store.BinauralProfileId = DefaultProfileId;
            }
        }
        store.HeadphoneVirtualizer = wantsHrtf;
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

            LoadAndOverrideCustom(destPath);
            store.HrirPath = destPath;
            store.BinauralProfileId = CustomId;
            store.HeadphoneVirtualizer = true;
            AppLogger.Info($"Imported and activated custom binaural profile: '{destPath}'");
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
            if (info == null || info.IsClassic) {
                VirtualizerFilter.Reset();
                AppLogger.Info("Activated binaural profile: Cavern Classic");
                return true;
            }

            if (info.IsCustom) {
                if (string.IsNullOrEmpty(store.HrirPath) || !File.Exists(store.HrirPath)) {
                    errorMessage = "Custom HRIR/SOFA file not found.";
                    return false;
                }
                LoadAndOverrideCustom(store.HrirPath);
                AppLogger.Info($"Activated custom binaural profile: '{store.HrirPath}'");
                return true;
            }

            using Stream assetStream = ctx.Assets.Open("hrir/" + info.AssetFile);
            (VirtualChannel[] channels, int sampleRate, float refDist, int taps) = ReadChrirStream(assetStream, info.IsGzipped);
            VirtualizerFilter.Override(channels, sampleRate, refDist);
            AppLogger.Info($"Activated official binaural profile '{info.Id}' ({channels.Length} ch, {taps} taps @ {sampleRate} Hz, d={refDist:0.00} m)");
            return true;
        } catch (Exception ex) {
            AppLogger.Error($"BinauralProfileService.Apply({profileId})", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    static void LoadAndOverrideCustom(string path) {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".sofa") {
            SofaHrirReader.SofaLoadResult result = SofaHrirReader.Load(path);
            VirtualizerFilter.Override(result.Channels, result.SampleRate, result.ReferenceDistance);
        } else {
            using RIFFWaveReader reader = new(path);
            reader.ReadHeader();
            VirtualChannel[] channels = VirtualChannel.Parse(
                new MultichannelWaveform(reader.ReadMultichannelAfterHeader()),
                reader.SampleRate);
            VirtualizerFilter.Override(channels, reader.SampleRate);
        }
    }

    /// <summary>
    /// Parses a binary CHRIR1 stream (plain .chrir or GZip-compressed .chrir.gz) into <see cref="VirtualChannel"/> instances.
    /// </summary>
    static (VirtualChannel[] channels, int sampleRate, float refDist, int tapCount) ReadChrirStream(Stream source, bool gzipped) {
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
                channels[c] = VirtualChannel.FromMeasured(x, y, left, right, sampleRate, crossoverHz);
            }

            return (channels, sampleRate, refDist, tapCount);
        } finally {
            if (gzipped) {
                dataStream.Dispose();
            }
        }
    }
}
