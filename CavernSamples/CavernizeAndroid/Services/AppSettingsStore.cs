using System.Numerics;

using Android.Content;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.External;
using Cavernize.Logic.Models.RenderTargets;

using VoidX.WPF.FFmpeg;
using VoidX.WPF.Language;

namespace CavernizeAndroid.Services;

/// <summary>
/// Persists all Cavernize settings to Android SharedPreferences and applies them to <see cref="Listener"/> and <see cref="RenderingSettings"/>.
/// </summary>
public sealed class AppSettingsStore {
    const string PrefsName = "cavernize_prefs";
    readonly ISharedPreferences prefs;

    public int RenderTargetIndex {
        get => prefs.GetInt("renderTarget", RenderTarget.Targets.Length - 1); // Default: Headphone Virtualizer
        set => Edit().PutInt("renderTarget", value).Apply();
    }

    public int ExportFormatIndex {
        get => prefs.GetInt("exportFormat", 4); // Default: PCM_LE
        set => Edit().PutInt("exportFormat", value).Apply();
    }

    public string LanguageCode {
        get => prefs.GetString("language", "");
        set {
            Edit().PutString("language", value ?? "").Apply();
            LanguageSettings.CultureOverride = value;
        }
    }

    // Upmixing
    public bool MatrixUpmixing {
        get => prefs.GetBoolean("matrixUpmix", false);
        set => Edit().PutBoolean("matrixUpmix", value).Apply();
    }

    public bool CavernizeUpmix {
        get => prefs.GetBoolean("cavernizeUpmix", false);
        set => Edit().PutBoolean("cavernizeUpmix", value).Apply();
    }

    public float UpmixEffect {
        get => prefs.GetFloat("upmixEffect", 0.75f);
        set => Edit().PutFloat("upmixEffect", value).Apply();
    }

    public float UpmixSmoothness {
        get => prefs.GetFloat("upmixSmooth", 0.80f);
        set => Edit().PutFloat("upmixSmooth", value).Apply();
    }

    public bool ParametricStftUpmix {
        get => prefs.GetBoolean("parametricStftUpmix", true);
        set => Edit().PutBoolean("parametricStftUpmix", value).Apply();
    }

    public bool AtmosAwareUpmix {
        get => prefs.GetBoolean("atmosAwareUpmix", true);
        set => Edit().PutBoolean("atmosAwareUpmix", value).Apply();
    }

    // Player & Virtualization
    public bool HeadphoneVirtualizer {
        get => prefs.GetBoolean("headphoneVirt", true);
        set => Edit().PutBoolean("headphoneVirt", value).Apply();
    }

    /// <summary>
    /// Legacy synthetic moving-average Distancer filter is permanently disabled; real binaural distance is
    /// controlled via laboratory-measured near/mid/far-field HRIR profiles.
    /// </summary>
    public bool DistanceSimulation {
        get => false;
        set => Edit().PutBoolean("distanceSim", false).Apply();
    }

    /// <summary>
    /// Converts all anechoic HRIR profiles to mathematically exact Minimum-Phase + measured ITD (zero EQ alteration).
    /// Automatically bypassed on studio BRIR profiles to preserve room reflections.
    /// </summary>
    public bool MinimumPhaseHrir {
        get => prefs.GetBoolean("minPhaseHrir", false);
        set => Edit().PutBoolean("minPhaseHrir", value).Apply();
    }

    public int VolumeRolloffIndex {
        get => prefs.GetInt("volumeRolloff", (int)Rolloffs.Disabled);
        set => Edit().PutInt("volumeRolloff", value).Apply();
    }

    public float PlayerVolume {
        get => prefs.GetFloat("playerVol", 1.0f);
        set => Edit().PutFloat("playerVol", value).Apply();
    }

    public float LfeVolume {
        get => prefs.GetFloat("lfeVol", 1.0f);
        set => Edit().PutFloat("lfeVol", value).Apply();
    }

    public bool NormalizerEnabled {
        get => prefs.GetBoolean("normalizer", true);
        set => Edit().PutBoolean("normalizer", value).Apply();
    }

    public bool LimiterOnly {
        get => prefs.GetBoolean("limiterOnly", true);
        set => Edit().PutBoolean("limiterOnly", value).Apply();
    }

    // Rendering Settings
    public bool SpeakerVirtualizer {
        get => prefs.GetBoolean("speakerVirt", false);
        set => Edit().PutBoolean("speakerVirt", value).Apply();
    }

    public bool MuteBed {
        get => prefs.GetBoolean("muteBed", false);
        set => Edit().PutBoolean("muteBed", value).Apply();
    }

    public bool MuteGround {
        get => prefs.GetBoolean("muteGround", false);
        set => Edit().PutBoolean("muteGround", value).Apply();
    }

    public bool Force24Bit {
        get => prefs.GetBoolean("force24Bit", false);
        set => Edit().PutBoolean("force24Bit", value).Apply();
    }

    public bool SurroundSwap {
        get => prefs.GetBoolean("surroundSwap", false);
        set => Edit().PutBoolean("surroundSwap", value).Apply();
    }

    public bool WavChannelSkip {
        get => prefs.GetBoolean("wavSkip", false);
        set => Edit().PutBoolean("wavSkip", value).Apply();
    }

    public bool ReportOnlyMode {
        get => prefs.GetBoolean("reportOnly", false);
        set => Edit().PutBoolean("reportOnly", value).Apply();
    }

    public bool QualityGrading {
        get => prefs.GetBoolean("qualityGrading", true);
        set => Edit().PutBoolean("qualityGrading", value).Apply();
    }

    // Advanced / CLI
    public float RenderGainDb {
        get => prefs.GetFloat("renderGainDb", 0f);
        set => Edit().PutFloat("renderGainDb", value).Apply();
    }

    public float HeightSqueezePercent {
        get => prefs.GetFloat("heightSqueeze", 0f);
        set => Edit().PutFloat("heightSqueeze", value).Apply();
    }

    public int MlpPresentation {
        get => prefs.GetInt("mlpPresentation", -1);
        set => Edit().PutInt("mlpPresentation", value).Apply();
    }

    public int EnvironmentTypeIndex {
        get => prefs.GetInt("envType", (int)Environments.Home);
        set => Edit().PutInt("envType", value).Apply();
    }

    public float RoomSizeX {
        get => prefs.GetFloat("roomX", 10f);
        set => Edit().PutFloat("roomX", value).Apply();
    }

    public float RoomSizeY {
        get => prefs.GetFloat("roomY", 7f);
        set => Edit().PutFloat("roomY", value).Apply();
    }

    public float RoomSizeZ {
        get => prefs.GetFloat("roomZ", 10f);
        set => Edit().PutFloat("roomZ", value).Apply();
    }

    public int AudioQualityIndex {
        get => prefs.GetInt("audioQuality", (int)QualityModes.High);
        set => Edit().PutInt("audioQuality", value).Apply();
    }

    public bool Pedantic {
        get => prefs.GetBoolean("pedantic", false);
        set => Edit().PutBoolean("pedantic", value).Apply();
    }

    public bool Unsafe {
        get => prefs.GetBoolean("unsafe", false);
        set => Edit().PutBoolean("unsafe", value).Apply();
    }

    public bool KeepTempFiles {
        get => prefs.GetBoolean("keepTemp", false);
        set => Edit().PutBoolean("keepTemp", value).Apply();
    }

    public string BinauralProfileId {
        get => prefs.GetString("binauralProfileId", "");
        set => Edit().PutString("binauralProfileId", value ?? "").Apply();
    }

    public string HrirPath {
        get => prefs.GetString("hrirPath", "");
        set => Edit().PutString("hrirPath", value ?? "").Apply();
    }

    public string RoomCorrectionPath {
        get => prefs.GetString("roomCorrPath", "");
        set => Edit().PutString("roomCorrPath", value ?? "").Apply();
    }

    // Slot 6A: AutoEq Neutral Headphone Engine & Per-Device Auto-Profile
    public bool AutoEqEnabled {
        get => prefs.GetBoolean("autoEqEnabled", false);
        set => Edit().PutBoolean("autoEqEnabled", value).Apply();
    }

    public string AutoEqProfileId {
        get => prefs.GetString("autoEqProfileId", "");
        set => Edit().PutString("autoEqProfileId", value ?? "").Apply();
    }

    public string AutoEqProfileName {
        get => prefs.GetString("autoEqProfileName", "");
        set => Edit().PutString("autoEqProfileName", value ?? "").Apply();
    }

    public string AutoEqCustomPath {
        get => prefs.GetString("autoEqCustomPath", "");
        set => Edit().PutString("autoEqCustomPath", value ?? "").Apply();
    }

    public int AutoEqTargetIndex {
        get => prefs.GetInt("autoEqTargetIndex", (int)Cavern.Filters.AutoEqNeutralTarget.DiffuseFieldNeutral);
        set => Edit().PutInt("autoEqTargetIndex", value).Apply();
    }

    public bool AutoEqDoubleEqGuard {
        get => prefs.GetBoolean("autoEqDoubleEqGuard", true);
        set => Edit().PutBoolean("autoEqDoubleEqGuard", value).Apply();
    }

    public bool AutoEqAutoSwitchByDevice {
        get => prefs.GetBoolean("autoEqAutoSwitchByDevice", true);
        set => Edit().PutBoolean("autoEqAutoSwitchByDevice", value).Apply();
    }

    // Slot 4: Low-Latency Audio, USB DAC & 3DoF/6DoF Head Tracking
    public bool LowLatencyAudio {
        get => prefs.GetBoolean("lowLatencyAudio", true);
        set => Edit().PutBoolean("lowLatencyAudio", value).Apply();
    }

    public bool UsbDacNativeSampleRate {
        get => prefs.GetBoolean("usbDacNativeSampleRate", true);
        set => Edit().PutBoolean("usbDacNativeSampleRate", value).Apply();
    }

    public bool HeadTrackingEnabled {
        get => prefs.GetBoolean("headTrackingEnabled", false);
        set => Edit().PutBoolean("headTrackingEnabled", value).Apply();
    }

    public int HeadTrackerSourceIndex {
        get => prefs.GetInt("headTrackerSourceIndex", (int)Cavernize.Logic.Spatial.HeadTrackerSourceKind.GalaxyBudsRfcomm);
        set => Edit().PutInt("headTrackerSourceIndex", value).Apply();
    }

    public bool HeadTrackingAutoRecenter {
        get => prefs.GetBoolean("headTrackingAutoRecenter", true);
        set => Edit().PutBoolean("headTrackingAutoRecenter", value).Apply();
    }

    public bool HeadTrackingLockRoll {
        get => prefs.GetBoolean("headTrackingLockRoll", false);
        set => Edit().PutBoolean("headTrackingLockRoll", value).Apply();
    }

    public float HeadTrackingPredictionMs {
        get => prefs.GetFloat("headTrackingPredictionMs", 25f);
        set => Edit().PutFloat("headTrackingPredictionMs", Math.Clamp(value, 0f, 120f)).Apply();
    }

    public int HeadTrackingOscPort {
        get => prefs.GetInt("headTrackingOscPort", 9000);
        set => Edit().PutInt("headTrackingOscPort", Math.Clamp(value, 1024, 65535)).Apply();
    }

    // Slot 5A: Physical Externalization (Allen & Berkley ISM Room, Duda & Martens Near-Field DVF, ISO 9613-1 Air Absorption)
    public bool IsmRoomEnabled {
        get => prefs.GetBoolean("ismRoomEnabled", false);
        set => Edit().PutBoolean("ismRoomEnabled", value).Apply();
    }

    public int IsmRoomPresetIndex {
        get => prefs.GetInt("ismRoomPreset", (int)Cavern.Virtualizer.IsmRoomPreset.MasteringStudio);
        set => Edit().PutInt("ismRoomPreset", Math.Clamp(value, 0, 4)).Apply();
    }

    public int IsmWallMaterialIndex {
        get => prefs.GetInt("ismWallMaterial", (int)Cavern.Virtualizer.IsmWallMaterial.StudioBroadband);
        set => Edit().PutInt("ismWallMaterial", Math.Clamp(value, 0, 4)).Apply();
    }

    public int IsmReflectionOrder {
        get => prefs.GetInt("ismReflectionOrder", 2);
        set => Edit().PutInt("ismReflectionOrder", value <= 1 ? 1 : 2).Apply();
    }

    public float IsmReflectionLevel {
        get => prefs.GetFloat("ismReflectionLevel", 1.0f);
        set => Edit().PutFloat("ismReflectionLevel", Math.Clamp(value, 0f, 1.5f)).Apply();
    }

    public bool NearFieldCompensationEnabled {
        get => prefs.GetBoolean("nearFieldCompEnabled", true);
        set => Edit().PutBoolean("nearFieldCompEnabled", value).Apply();
    }

    public bool AirAbsorptionEnabled {
        get => prefs.GetBoolean("airAbsorptionEnabled", true);
        set => Edit().PutBoolean("airAbsorptionEnabled", value).Apply();
    }

    public float AirTemperatureCelsius {
        get => prefs.GetFloat("airTempC", Cavern.Virtualizer.AirAbsorptionFilter.DefaultTemperatureCelsius);
        set => Edit().PutFloat("airTempC", Math.Clamp(value, 0f, 40f)).Apply();
    }

    public float AirRelativeHumidityPct {
        get => prefs.GetFloat("airRhPct", Cavern.Virtualizer.AirAbsorptionFilter.DefaultRelativeHumidityPct);
        set => Edit().PutFloat("airRhPct", Math.Clamp(value, 15f, 90f)).Apply();
    }

    public Cavernize.Logic.Spatial.PhysicalExternalizationConfig BuildExternalizationConfig() => new() {
        IsmRoomEnabled = IsmRoomEnabled,
        RoomPreset = (Cavern.Virtualizer.IsmRoomPreset)Math.Clamp(IsmRoomPresetIndex, 0, 4),
        WallMaterial = (Cavern.Virtualizer.IsmWallMaterial)Math.Clamp(IsmWallMaterialIndex, 0, 4),
        ReflectionOrder = IsmReflectionOrder <= 1 ? 1 : 2,
        ReflectionLevel = Math.Clamp(IsmReflectionLevel, 0f, 1.5f),
        NearFieldEnabled = NearFieldCompensationEnabled,
        AirAbsorptionEnabled = AirAbsorptionEnabled,
        AirTemperatureCelsius = Math.Clamp(AirTemperatureCelsius, 0f, 40f),
        AirRelativeHumidityPct = Math.Clamp(AirRelativeHumidityPct, 15f, 90f)
    };

    // Slot 5B: MagLS Ambisonics, Woodworth-Schlosberg Biometric ITD, HpTF Ear-Canal Notch & SONICOM/HUTUBS Wizard
    public int MagLsModeIndex {
        get => prefs.GetInt("magLsModeIndex", (int)Cavernize.Logic.Spatial.MagLsDecodeMode.Off);
        set => Edit().PutInt("magLsModeIndex", Math.Clamp(value, 0, 3)).Apply();
    }

    public bool BiometricItdEnabled {
        get => prefs.GetBoolean("biometricItdEnabled", false);
        set => Edit().PutBoolean("biometricItdEnabled", value).Apply();
    }

    public float HeadWidthCm {
        get => prefs.GetFloat("headWidthCm", Cavern.Virtualizer.BiometricItdScaler.ReferenceHeadWidthCm);
        set => Edit().PutFloat("headWidthCm", Math.Clamp(value,
            Cavern.Virtualizer.BiometricItdScaler.MinHeadWidthCm,
            Cavern.Virtualizer.BiometricItdScaler.MaxHeadWidthCm)).Apply();
    }

    public float ManualItdOffsetUs {
        get => prefs.GetFloat("manualItdOffsetUs", 0f);
        set => Edit().PutFloat("manualItdOffsetUs", Math.Clamp(value,
            -Cavern.Virtualizer.BiometricItdScaler.MaxManualOffsetUs,
            Cavern.Virtualizer.BiometricItdScaler.MaxManualOffsetUs)).Apply();
    }

    public bool HptfNotchEnabled {
        get => prefs.GetBoolean("hptfNotchEnabled", false);
        set => Edit().PutBoolean("hptfNotchEnabled", value).Apply();
    }

    public float HptfNotchFreqHz {
        get => prefs.GetFloat("hptfNotchFreqHz", Cavern.Filters.HptfNotchFilter.DefaultFrequencyHz);
        set => Edit().PutFloat("hptfNotchFreqHz", Math.Clamp(value,
            Cavern.Filters.HptfNotchFilter.MinFrequencyHz,
            Cavern.Filters.HptfNotchFilter.MaxFrequencyHz)).Apply();
    }

    public float HptfNotchGainDb {
        get => prefs.GetFloat("hptfNotchGainDb", Cavern.Filters.HptfNotchFilter.DefaultGainDb);
        set => Edit().PutFloat("hptfNotchGainDb", Math.Clamp(value,
            Cavern.Filters.HptfNotchFilter.MaxCutDb,
            0f)).Apply();
    }

    public float HptfNotchQ {
        get => prefs.GetFloat("hptfNotchQ", Cavern.Filters.HptfNotchFilter.DefaultQ);
        set => Edit().PutFloat("hptfNotchQ", Math.Clamp(value,
            Cavern.Filters.HptfNotchFilter.MinQ,
            Cavern.Filters.HptfNotchFilter.MaxQ)).Apply();
    }

    public string PerceptualClusterId {
        get => prefs.GetString("perceptualClusterId", "");
        set => Edit().PutString("perceptualClusterId", value ?? "").Apply();
    }

    public Cavernize.Logic.Spatial.HrtfPersonalizationConfig BuildPersonalizationConfig() => new() {
        MagLsMode = (Cavernize.Logic.Spatial.MagLsDecodeMode)Math.Clamp(MagLsModeIndex, 0, 3),
        MagLsCutoffHz = 0f,
        BiometricItdEnabled = BiometricItdEnabled,
        HeadWidthCm = Math.Clamp(HeadWidthCm,
            Cavern.Virtualizer.BiometricItdScaler.MinHeadWidthCm,
            Cavern.Virtualizer.BiometricItdScaler.MaxHeadWidthCm),
        ManualItdOffsetUs = Math.Clamp(ManualItdOffsetUs,
            -Cavern.Virtualizer.BiometricItdScaler.MaxManualOffsetUs,
            Cavern.Virtualizer.BiometricItdScaler.MaxManualOffsetUs),
        HptfNotchEnabled = HptfNotchEnabled,
        HptfNotchFreqHz = Math.Clamp(HptfNotchFreqHz,
            Cavern.Filters.HptfNotchFilter.MinFrequencyHz,
            Cavern.Filters.HptfNotchFilter.MaxFrequencyHz),
        HptfNotchGainDb = Math.Clamp(HptfNotchGainDb, Cavern.Filters.HptfNotchFilter.MaxCutDb, 0f),
        HptfNotchQ = Math.Clamp(HptfNotchQ,
            Cavern.Filters.HptfNotchFilter.MinQ,
            Cavern.Filters.HptfNotchFilter.MaxQ),
        PerceptualClusterId = PerceptualClusterId ?? ""
    };

    // Slot 6B: Psychoacoustics & Studio Loudness Standards (ISO 226:2023, ITU-R BS.1770-5 / EBU R128, Spatial Dialog, 4x True-Peak Limiter)
    public bool Iso226Enabled {
        get => prefs.GetBoolean("iso226Enabled", false);
        set => Edit().PutBoolean("iso226Enabled", value).Apply();
    }

    public float Iso226ReferenceSplPhon {
        get => prefs.GetFloat("iso226RefPhon", Cavern.Filters.Iso226LoudnessFilter.DefaultReferenceSplPhon);
        set => Edit().PutFloat("iso226RefPhon", Math.Clamp(value,
            Cavern.Filters.Iso226LoudnessFilter.MinReferenceSplPhon,
            Cavern.Filters.Iso226LoudnessFilter.MaxReferenceSplPhon)).Apply();
    }

    public float Iso226ListeningSplPhon {
        get => prefs.GetFloat("iso226ListenPhon", Cavern.Filters.Iso226LoudnessFilter.DefaultListeningSplPhon);
        set => Edit().PutFloat("iso226ListenPhon", Math.Clamp(value,
            Cavern.Filters.Iso226LoudnessFilter.MinListeningSplPhon,
            Cavern.Filters.Iso226LoudnessFilter.MaxListeningSplPhon)).Apply();
    }

    public bool Iso226TrackVolume {
        get => prefs.GetBoolean("iso226TrackVol", true);
        set => Edit().PutBoolean("iso226TrackVol", value).Apply();
    }

    public int LoudnessNormModeIndex {
        get => prefs.GetInt("loudnessNormMode", (int)Cavernize.Logic.Spatial.LoudnessNormalizationMode.Off);
        set => Edit().PutInt("loudnessNormMode", Math.Clamp(value, 0, 4)).Apply();
    }

    public bool RespectDolbyDialnorm {
        get => prefs.GetBoolean("respectDialnorm", true);
        set => Edit().PutBoolean("respectDialnorm", value).Apply();
    }

    public bool DialogEnhancementEnabled {
        get => prefs.GetBoolean("dialogEnhanceEnabled", false);
        set => Edit().PutBoolean("dialogEnhanceEnabled", value).Apply();
    }

    public float DialogEnhancementDb {
        get => prefs.GetFloat("dialogEnhanceDb", Cavern.Filters.SpatialDialogEnhancer.DefaultBoostDb);
        set => Edit().PutFloat("dialogEnhanceDb", Math.Clamp(value,
            Cavern.Filters.SpatialDialogEnhancer.MinBoostDb,
            Cavern.Filters.SpatialDialogEnhancer.MaxBoostDb)).Apply();
    }

    public int TruePeakCeilingIndex {
        get => prefs.GetInt("truePeakCeilingIdx", 0); // 0 = -0.1 dBTP, 1 = -1.0 dBTP, 2 = -2.0 dBTP
        set => Edit().PutInt("truePeakCeilingIdx", Math.Clamp(value, 0, 2)).Apply();
    }

    public float ResolveTruePeakCeilingDbtp() => TruePeakCeilingIndex switch {
        1 => -1.0f,
        2 => -2.0f,
        _ => Cavern.Filters.TruePeakLimiter.DefaultCeilingDbtp
    };

    public Cavernize.Logic.Spatial.PsychoacousticLoudnessConfig BuildLoudnessConfig() => new() {
        Iso226Enabled = Iso226Enabled,
        Iso226ReferenceSplPhon = Iso226ReferenceSplPhon,
        Iso226ListeningSplPhon = Iso226ListeningSplPhon,
        Iso226TrackVolume = Iso226TrackVolume,
        NormalizationMode = (Cavernize.Logic.Spatial.LoudnessNormalizationMode)Math.Clamp(LoudnessNormModeIndex, 0, 4),
        RespectDolbyDialnorm = RespectDolbyDialnorm,
        DialogEnhancementEnabled = DialogEnhancementEnabled,
        DialogEnhancementDb = DialogEnhancementDb,
        TruePeakLimiterEnabled = true,
        TruePeakCeilingDbtp = ResolveTruePeakCeilingDbtp()
    };

    // Slot 2B: On-Device Neural Audio Runtime (ONNX), MVSEP Anti-Artifact Pipeline & Spatial Karaoke
    public string InstalledOnnxRuntimeVersion {
        get => prefs.GetString("onnxRuntimeVer", "");
        set => Edit().PutString("onnxRuntimeVer", value ?? "").Apply();
    }

    public bool OnnxRuntimeQnnInstalled {
        get => prefs.GetBoolean("onnxRuntimeQnn", false);
        set => Edit().PutBoolean("onnxRuntimeQnn", value).Apply();
    }

    public int NeuralProviderIndex {
        get => prefs.GetInt("neuralProviderIdx", (int)Cavernize.Logic.Spatial.NeuralExecutionProvider.Auto);
        set => Edit().PutInt("neuralProviderIdx", Math.Clamp(value, 0, 3)).Apply();
    }

    public int NeuralOverlapFactor {
        get => prefs.GetInt("neuralOverlapFactor", 4);
        set => Edit().PutInt("neuralOverlapFactor", Math.Clamp(value, 2, 8)).Apply();
    }

    public bool NeuralTtaEnabled {
        get => prefs.GetBoolean("neuralTtaEnabled", false);
        set => Edit().PutBoolean("neuralTtaEnabled", value).Apply();
    }

    public int NeuralEnsembleModeIndex {
        get => prefs.GetInt("neuralEnsembleMode", (int)Cavern.Neural.MvsepEnsembleMethod.MinFft);
        set => Edit().PutInt("neuralEnsembleMode", Math.Clamp(value, 0, 2)).Apply();
    }

    public bool NeuralPhaseFixEnabled {
        get => prefs.GetBoolean("neuralPhaseFix", true);
        set => Edit().PutBoolean("neuralPhaseFix", value).Apply();
    }

    public bool NeuralEnergyConservation {
        get => prefs.GetBoolean("neuralEnergyCons", true);
        set => Edit().PutBoolean("neuralEnergyCons", value).Apply();
    }

    public bool SpatialKaraokeEnabled {
        get => prefs.GetBoolean("spatialKaraokeEnabled", false);
        set => Edit().PutBoolean("spatialKaraokeEnabled", value).Apply();
    }

    public float KaraokeVocalLevel {
        get => prefs.GetFloat("karaokeVocalLevel", 1.0f);
        set => Edit().PutFloat("karaokeVocalLevel", Math.Clamp(value, 0f, 1f)).Apply();
    }

    public bool KaraokePreserveBackingVocals {
        get => prefs.GetBoolean("karaokePreserveBacking", true);
        set => Edit().PutBoolean("karaokePreserveBacking", value).Apply();
    }

    public bool UseLosslessTwinForKaraoke {
        get => prefs.GetBoolean("useLosslessTwinKaraoke", true);
        set => Edit().PutBoolean("useLosslessTwinKaraoke", value).Apply();
    }

    public int NeuralSpatialUpmixModeIndex {
        get => prefs.GetInt("neuralSpatialUpmixMode", (int)Cavernize.Logic.Spatial.NeuralSpatialUpmixMode.Off);
        set => Edit().PutInt("neuralSpatialUpmixMode", Math.Clamp(value, 0, 2)).Apply();
    }

    public string ActiveStemModelId {
        get => prefs.GetString("activeStemModelId", Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultStemModelId);
        set => Edit().PutString("activeStemModelId", value ?? Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultStemModelId).Apply();
    }

    public string ActiveKaraokeModelId {
        get => prefs.GetString("activeKaraokeModelId", Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultKaraokeModelId);
        set => Edit().PutString("activeKaraokeModelId", value ?? Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultKaraokeModelId).Apply();
    }

    public string ActiveDereverbModelId {
        get => prefs.GetString("activeDereverbModelId", Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultDereverbModelId);
        set => Edit().PutString("activeDereverbModelId", value ?? Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultDereverbModelId).Apply();
    }

    public string ActiveRestorerModelId {
        get => prefs.GetString("activeRestorerModelId", Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultRestorerModelId);
        set => Edit().PutString("activeRestorerModelId", value ?? Cavernize.Logic.Spatial.NeuralModelCatalog.DefaultRestorerModelId).Apply();
    }

    public Cavernize.Logic.Spatial.NeuralAudioConfig BuildNeuralAudioConfig() => new() {
        PreferredProvider = (Cavernize.Logic.Spatial.NeuralExecutionProvider)Math.Clamp(NeuralProviderIndex, 0, 3),
        OverlapFactor = Math.Clamp(NeuralOverlapFactor, 2, 8),
        EnableTta = NeuralTtaEnabled,
        EnsembleMode = (Cavern.Neural.MvsepEnsembleMethod)Math.Clamp(NeuralEnsembleModeIndex, 0, 2),
        EnablePhaseFix = NeuralPhaseFixEnabled,
        EnforceEnergyConservation = NeuralEnergyConservation,
        SpatialKaraokeEnabled = SpatialKaraokeEnabled,
        KaraokeVocalLevel = Math.Clamp(KaraokeVocalLevel, 0f, 1f),
        KaraokePreserveBackingVocals = KaraokePreserveBackingVocals,
        UseLosslessTwinForKaraoke = UseLosslessTwinForKaraoke,
        SpatialUpmixMode = (Cavernize.Logic.Spatial.NeuralSpatialUpmixMode)Math.Clamp(NeuralSpatialUpmixModeIndex, 0, 2),
        ActiveStemModelId = ActiveStemModelId,
        ActiveKaraokeModelId = ActiveKaraokeModelId,
        ActiveDereverbModelId = ActiveDereverbModelId,
        ActiveRestorerModelId = ActiveRestorerModelId
    };

    // Slot 2C: Universal Lossless Twin & Lossless-Guided Atmos Restoration (LGSR, W09)
    public bool LgsrEnabled {
        get => prefs.GetBoolean("lgsrEnabled", true);
        set => Edit().PutBoolean("lgsrEnabled", value).Apply();
    }

    public bool LgsrAutoDiscoverTwin {
        get => prefs.GetBoolean("lgsrAutoDiscoverTwin", true);
        set => Edit().PutBoolean("lgsrAutoDiscoverTwin", value).Apply();
    }

    public float LgsrFusionStrength {
        get => prefs.GetFloat("lgsrFusionStrength", 0.85f);
        set => Edit().PutFloat("lgsrFusionStrength", Math.Clamp(value, 0f, 1f)).Apply();
    }

    public bool LgsrDynamicEnvelopeLock {
        get => prefs.GetBoolean("lgsrEnvelopeLock", true);
        set => Edit().PutBoolean("lgsrEnvelopeLock", value).Apply();
    }

    public bool LgsrClippingGuard {
        get => prefs.GetBoolean("lgsrClippingGuard", true);
        set => Edit().PutBoolean("lgsrClippingGuard", value).Apply();
    }

    public bool LgsrBitstreamGuide {
        get => prefs.GetBoolean("lgsrBitstreamGuide", true);
        set => Edit().PutBoolean("lgsrBitstreamGuide", value).Apply();
    }

    public bool LgsrRejectFakeLossless {
        get => prefs.GetBoolean("lgsrRejectFakeLossless", true);
        set => Edit().PutBoolean("lgsrRejectFakeLossless", value).Apply();
    }

    public bool LgsrTier3ResidualCleanup {
        get => prefs.GetBoolean("lgsrTier3Cleanup", false);
        set => Edit().PutBoolean("lgsrTier3Cleanup", value).Apply();
    }

    public Cavernize.Logic.Spatial.LgsrConfig BuildLgsrConfig() => new() {
        Enabled = LgsrEnabled,
        AutoDiscoverTwin = LgsrAutoDiscoverTwin,
        BypassOnAlreadyLossless = true,
        RejectFakeLosslessTwins = LgsrRejectFakeLossless,
        FusionStrength = Math.Clamp(LgsrFusionStrength, 0f, 1f),
        DynamicEnvelopeLock = LgsrDynamicEnvelopeLock,
        ClippingGuardEnabled = LgsrClippingGuard,
        UseBitstreamCouplingSpxGuide = LgsrBitstreamGuide,
        EnableTier3ResidualCleanup = LgsrTier3ResidualCleanup,
        MinSyncConfidence = 0.35f
    };

    // Slot 1 / Slot 4: Pro-Audio Full-Duplex USB Engine (Path B libusb UAC2 + Path A AAudio/Oboe Exclusive, W10)
    public bool ProAudioEnabled {
        get => prefs.GetBoolean("proAudioEnabled", false);
        set => Edit().PutBoolean("proAudioEnabled", value).Apply();
    }

    public int ProAudioPreferredPathIndex {
        get => prefs.GetInt("proAudioPathIdx", (int)Cavernize.Logic.Spatial.ProAudioEnginePath.Auto);
        set => Edit().PutInt("proAudioPathIdx", Math.Clamp(value, 0, 3)).Apply();
    }

    public int ProAudioBufferFrames {
        get => prefs.GetInt("proAudioBufferFrames", (int)Cavernize.Logic.Spatial.ProAudioBufferPreset.Frames128);
        set {
            int safe = value <= 64 ? 64 : (value <= 128 ? 128 : 256);
            Edit().PutInt("proAudioBufferFrames", safe).Apply();
        }
    }

    public int ProAudioLockedSampleRate {
        get => prefs.GetInt("proAudioLockedSr", 0);
        set => Edit().PutInt("proAudioLockedSr", value).Apply();
    }

    public bool LiveMicEnabled {
        get => prefs.GetBoolean("liveMicEnabled", false);
        set => Edit().PutBoolean("liveMicEnabled", value).Apply();
    }

    public int LiveMicRoutingIndex {
        get => prefs.GetInt("liveMicRoutingIdx", (int)Cavernize.Logic.Spatial.LiveMicRoutingMode.MonoChannel1);
        set => Edit().PutInt("liveMicRoutingIdx", Math.Clamp(value, 0, 3)).Apply();
    }

    public float LiveMicInputGainDb {
        get => prefs.GetFloat("liveMicInputGainDb", 0f);
        set => Edit().PutFloat("liveMicInputGainDb", Math.Clamp(value, -18f, 24f)).Apply();
    }

    public float LiveMicMonitorLevel {
        get => prefs.GetFloat("liveMicMonitorLevel", 0.85f);
        set => Edit().PutFloat("liveMicMonitorLevel", Math.Clamp(value, 0f, 1.25f)).Apply();
    }

    public float CalibratedRoundTripSamples {
        get => prefs.GetFloat("proAudioCalRttSamples", 0f);
        set => Edit().PutFloat("proAudioCalRttSamples", Math.Clamp(value, 0f, 48000f)).Apply();
    }

    public float CalibratedRoundTripMs {
        get => prefs.GetFloat("proAudioCalRttMs", 0f);
        set => Edit().PutFloat("proAudioCalRttMs", Math.Clamp(value, 0f, 500f)).Apply();
    }

    public Cavernize.Logic.Spatial.ProAudioDuplexConfig BuildProAudioConfig() => new Cavernize.Logic.Spatial.ProAudioDuplexConfig {
        Enabled = ProAudioEnabled,
        PreferredPath = (Cavernize.Logic.Spatial.ProAudioEnginePath)Math.Clamp(ProAudioPreferredPathIndex, 0, 3),
        BufferFrames = ProAudioBufferFrames <= 64
            ? Cavernize.Logic.Spatial.ProAudioBufferPreset.Frames64
            : (ProAudioBufferFrames <= 128
                ? Cavernize.Logic.Spatial.ProAudioBufferPreset.Frames128
                : Cavernize.Logic.Spatial.ProAudioBufferPreset.Frames256),
        LockedSampleRateHz = ProAudioLockedSampleRate,
        LiveMicEnabled = LiveMicEnabled,
        MicRouting = (Cavernize.Logic.Spatial.LiveMicRoutingMode)Math.Clamp(LiveMicRoutingIndex, 0, 3),
        MicInputGainDb = LiveMicInputGainDb,
        MicMonitorLevel = LiveMicMonitorLevel,
        CalibratedRoundTripSamples = CalibratedRoundTripSamples,
        CalibratedRoundTripMs = CalibratedRoundTripMs
    }.Sanitize();

    // Slot 2D: Auto-Vocal Mix-Match & Live Mastering Vocal Chain (W11 / W14)
    public bool VocalMixMatchEnabled {
        get => prefs.GetBoolean("vocalMixMatchEnabled", true);
        set => Edit().PutBoolean("vocalMixMatchEnabled", value).Apply();
    }

    public float VocalMixMatchPct {
        get => prefs.GetFloat("vocalMixMatchPct", 75f);
        set => Edit().PutFloat("vocalMixMatchPct", Math.Clamp(value, 0f, 100f)).Apply();
    }

    public float VocalReverbPct {
        get => prefs.GetFloat("vocalReverbPct", 100f);
        set => Edit().PutFloat("vocalReverbPct", Math.Clamp(value, 0f, 150f)).Apply();
    }

    public float VocalDynamicsPct {
        get => prefs.GetFloat("vocalDynamicsPct", 100f);
        set => Edit().PutFloat("vocalDynamicsPct", Math.Clamp(value, 0f, 150f)).Apply();
    }

    public float VocalWarmthPct {
        get => prefs.GetFloat("vocalWarmthPct", 35f);
        set => Edit().PutFloat("vocalWarmthPct", Math.Clamp(value, 0f, 100f)).Apply();
    }

    public float VocalLevelDb {
        get => prefs.GetFloat("vocalLevelDb", 0f);
        set => Edit().PutFloat("vocalLevelDb", Math.Clamp(value, -18f, 12f)).Apply();
    }

    public bool VocalSidechainPocketGlue {
        get => prefs.GetBoolean("vocalSidechainGlue", true);
        set => Edit().PutBoolean("vocalSidechainGlue", value).Apply();
    }

    public bool VocalNoiseGate {
        get => prefs.GetBoolean("vocalNoiseGate", true);
        set => Edit().PutBoolean("vocalNoiseGate", value).Apply();
    }

    public bool VocalDeEsser {
        get => prefs.GetBoolean("vocalDeEsser", true);
        set => Edit().PutBoolean("vocalDeEsser", value).Apply();
    }

    public Cavern.VocalChain.VocalMacroSettings BuildVocalMacroSettings() => new() {
        Enabled = VocalMixMatchEnabled,
        Bypass = false,
        MixMatchPct = Math.Clamp(VocalMixMatchPct, 0f, 100f),
        ReverbPct = Math.Clamp(VocalReverbPct, 0f, 150f),
        DynamicsPct = Math.Clamp(VocalDynamicsPct, 0f, 150f),
        WarmthPct = Math.Clamp(VocalWarmthPct, 0f, 100f),
        VocalLevelDb = Math.Clamp(VocalLevelDb, -18f, 12f),
        EnableSidechainPocketGlue = VocalSidechainPocketGlue,
        EnableNoiseGate = VocalNoiseGate,
        EnableDeEsser = VocalDeEsser
    };

    public string DeviceProfilesJson {
        get => prefs.GetString("deviceProfilesV1", "[]");
        set => Edit().PutString("deviceProfilesV1", value ?? "[]").Apply();
    }

    public List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding> GetDeviceProfiles() {
        try {
            List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding> list =
                System.Text.Json.JsonSerializer.Deserialize<List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding>>(DeviceProfilesJson);
            return list?.Where(b => b != null && !string.IsNullOrWhiteSpace(b.DeviceKey)).ToList() ?? [];
        } catch {
            return [];
        }
    }

    public Cavernize.Logic.Spatial.AudioDeviceProfileBinding FindDeviceProfile(string deviceKey) {
        if (string.IsNullOrWhiteSpace(deviceKey)) {
            return null;
        }
        List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding> all = GetDeviceProfiles();
        for (int i = 0; i < all.Count; i++) {
            if (string.Equals(all[i].DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)) {
                return all[i];
            }
        }
        return null;
    }

    public void UpsertDeviceProfile(Cavernize.Logic.Spatial.AudioDeviceProfileBinding binding) {
        if (binding == null || string.IsNullOrWhiteSpace(binding.DeviceKey)) {
            return;
        }
        List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding> all = GetDeviceProfiles();
        bool replaced = false;
        for (int i = 0; i < all.Count; i++) {
            if (string.Equals(all[i].DeviceKey, binding.DeviceKey, StringComparison.OrdinalIgnoreCase)) {
                all[i] = binding;
                replaced = true;
                break;
            }
        }
        if (!replaced) {
            all.Add(binding);
        }
        DeviceProfilesJson = System.Text.Json.JsonSerializer.Serialize(all);
    }

    public bool RemoveDeviceProfile(string deviceKey) {
        if (string.IsNullOrWhiteSpace(deviceKey)) {
            return false;
        }
        List<Cavernize.Logic.Spatial.AudioDeviceProfileBinding> all = GetDeviceProfiles();
        int removed = all.RemoveAll(b => string.Equals(b.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) {
            DeviceProfilesJson = System.Text.Json.JsonSerializer.Serialize(all);
            return true;
        }
        return false;
    }

    public string FFmpegLocation {
        get => prefs.GetString("ffmpegPath", "");
        set => Edit().PutString("ffmpegPath", value ?? "").Apply();
    }

    public string InstalledFFmpegVersion {
        get => prefs.GetString("ffmpegVer", "");
        set => Edit().PutString("ffmpegVer", value ?? "").Apply();
    }

    public string FFmpegExtraArgs {
        get => prefs.GetString("ffmpegArgs", "");
        set => Edit().PutString("ffmpegArgs", value ?? "").Apply();
    }

    public string TruehddLocation {
        get => prefs.GetString("truehddPath", "");
        set => Edit().PutString("truehddPath", value ?? "").Apply();
    }

    public string InstalledTruehddVersion {
        get => prefs.GetString("truehddVer", "");
        set => Edit().PutString("truehddVer", value ?? "").Apply();
    }

    public bool TruehddLicenceAccepted {
        get => prefs.GetBoolean("truehddLicenceAccepted", false);
        set => Edit().PutBoolean("truehddLicenceAccepted", value).Apply();
    }

    public bool AutoCheckUpdates {
        get => prefs.GetBoolean("autoCheckUpdates", true);
        set => Edit().PutBoolean("autoCheckUpdates", value).Apply();
    }

    public bool RoomPositionLocked {
        get => prefs.GetBoolean("roomPosLocked", false);
        set => Edit().PutBoolean("roomPosLocked", value).Apply();
    }

    // W13: 6DoF Walkable 3D Room & Studio Spatial X-Ray Inspector
    public bool PropagationDelay6DoFEnabled {
        get => prefs.GetBoolean("propDelay6DoF", true);
        set => Edit().PutBoolean("propDelay6DoF", value).Apply();
    }

    public bool RoomXRayOverlayEnabled {
        get => prefs.GetBoolean("roomXRayEnabled", false);
        set => Edit().PutBoolean("roomXRayEnabled", value).Apply();
    }

    public int RoomXRayBandMode {
        get => prefs.GetInt("roomXRayBandMode", 0);
        set => Edit().PutInt("roomXRayBandMode", Math.Clamp(value, 0, 3)).Apply();
    }

    public bool RoomXRayInspectorExpanded {
        get => prefs.GetBoolean("roomXRayInspectorExp", false);
        set => Edit().PutBoolean("roomXRayInspectorExp", value).Apply();
    }

    public bool OnlineAutoSelectBestMatch {
        get => prefs.GetBoolean("onlineAutoBestMatch", true);
        set => Edit().PutBoolean("onlineAutoBestMatch", value).Apply();
    }

    public int LibraryFilterMode {
        get => prefs.GetInt("libraryFilterMode", 0);
        set => Edit().PutInt("libraryFilterMode", value).Apply();
    }

    public bool LibraryHideNonMusic {
        get => prefs.GetBoolean("libraryHideNonMusic", true);
        set => Edit().PutBoolean("libraryHideNonMusic", value).Apply();
    }

    public int LibraryFolderFilterMode {
        get => prefs.GetInt("libraryFolderFilterMode", 0); // 0 = All, 1 = AllExcept, 2 = OnlySelected
        set => Edit().PutInt("libraryFolderFilterMode", value).Apply();
    }

    public string LibraryFilterFoldersJson {
        get => prefs.GetString("libraryFilterFolders", "[]");
        set => Edit().PutString("libraryFilterFolders", value ?? "[]").Apply();
    }

    public List<string> GetLibraryFilterFolders() {
        try {
            List<string> list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(LibraryFilterFoldersJson);
            return list?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        } catch {
            return [];
        }
    }

    public void SetLibraryFilterFolders(IEnumerable<string> folders) {
        List<string> clean = folders?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        LibraryFilterFoldersJson = System.Text.Json.JsonSerializer.Serialize(clean);
    }

    public string LastExplorerDirectory {
        get => prefs.GetString("lastExplorerDir", "");
        set => Edit().PutString("lastExplorerDir", value ?? "").Apply();
    }

    public AppSettingsStore(Context context) {
        prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        if (!string.IsNullOrEmpty(LanguageCode)) {
            LanguageSettings.CultureOverride = LanguageCode;
        }
    }

    ISharedPreferencesEditor Edit() => prefs.Edit();

    public void ApplyGlobals() {
        Listener.EnvironmentType = (Environments)Math.Clamp(EnvironmentTypeIndex, 0, 3);
        Listener.EnvironmentSize = new Vector3(
            Math.Clamp(RoomSizeX, 1f, 50f),
            Math.Clamp(RoomSizeY, 1f, 50f),
            Math.Clamp(RoomSizeZ, 1f, 50f));
        RenderTarget.HeightSqueeze = Math.Clamp(HeightSqueezePercent, 0f, 100f) * 0.01f;
        Cavern.Format.Renderers.MeridianLosslessPackingRenderer.ForcedPresentation = MlpPresentation >= 0 ? MlpPresentation : null;
        Cavern.Format.Common.CavernFormatGlobal.Pedantic = Pedantic;
        Cavern.Format.Common.CavernFormatGlobal.Unsafe = Unsafe;
        RenderingSettings.KeepTempFiles = KeepTempFiles;
    }
}

public sealed class AndroidUpmixingSettings(AppSettingsStore store) : UpmixingSettings(false) {
    public override bool MatrixUpmixing {
        get => store.MatrixUpmixing;
        set => store.MatrixUpmixing = value;
    }

    public override bool Cavernize {
        get => store.CavernizeUpmix;
        set => store.CavernizeUpmix = value;
    }

    public override float Effect {
        get => store.UpmixEffect;
        set => store.UpmixEffect = value;
    }

    public override float Smoothness {
        get => store.UpmixSmoothness;
        set => store.UpmixSmoothness = value;
    }

    public override bool ParametricStft {
        get => store.ParametricStftUpmix;
        set => store.ParametricStftUpmix = value;
    }

    public override bool AtmosAware {
        get => store.AtmosAwareUpmix;
        set => store.AtmosAwareUpmix = value;
    }
}

public sealed class AndroidRenderingSettings(AppSettingsStore store) : RenderingSettings {
    public override float Gain {
        get => QMath.DbToGain(store.RenderGainDb);
        set => store.RenderGainDb = QMath.GainToDb(value);
    }

    public override float LFEVolume {
        get => store.LfeVolume;
        set => store.LfeVolume = value;
    }

    public override bool SpeakerVirtualizer {
        get => store.SpeakerVirtualizer;
        set => store.SpeakerVirtualizer = value;
    }

    public override bool MuteBed {
        get => store.MuteBed;
        set => store.MuteBed = value;
    }

    public override bool MuteGround {
        get => store.MuteGround;
        set => store.MuteGround = value;
    }

    public override bool Force24Bit {
        get => store.Force24Bit;
        set => store.Force24Bit = value;
    }

    public override bool SurroundSwap {
        get => store.SurroundSwap;
        set => store.SurroundSwap = value;
    }

    public override bool WAVChannelSkip {
        get => store.WavChannelSkip;
        set => store.WavChannelSkip = value;
    }
}

public sealed class AndroidFFmpegRunner(Action<string> onStatus) : FFmpeg {
    public override void UpdateStatusText(string text) => onStatus?.Invoke(text);

    protected override void ConfigureStartInfo(System.Diagnostics.ProcessStartInfo startInfo) {
        FFmpegInstallerService.ConfigureProcessStartInfo(startInfo, Location);
    }

    protected override void OnCommandCompleted(string executable, string arguments, int exitCode, string stdout, string stderr) {
        AppLogger.LogFFmpeg(executable, arguments, exitCode, stdout, stderr);
    }
}

public sealed class AndroidLicencePrompt(Func<string, string, bool> promptUser) : ILicence {
    string desc = "";
    string text = "";

    public void SetDescription(string description) => desc = description;
    public void SetLicenceText(string licenceText) => text = licenceText;
    public bool Prompt() => promptUser?.Invoke(desc, text) ?? false;
}
