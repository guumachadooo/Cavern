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

    // Player & Virtualization
    public bool HeadphoneVirtualizer {
        get => prefs.GetBoolean("headphoneVirt", true);
        set => Edit().PutBoolean("headphoneVirt", value).Apply();
    }

    public bool DistanceSimulation {
        get => prefs.GetBoolean("distanceSim", true);
        set => Edit().PutBoolean("distanceSim", value).Apply();
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

    public string HrirPath {
        get => prefs.GetString("hrirPath", "");
        set => Edit().PutString("hrirPath", value ?? "").Apply();
    }

    public string RoomCorrectionPath {
        get => prefs.GetString("roomCorrPath", "");
        set => Edit().PutString("roomCorrPath", value ?? "").Apply();
    }

    public string FFmpegLocation {
        get => prefs.GetString("ffmpegPath", "");
        set => Edit().PutString("ffmpegPath", value ?? "").Apply();
    }

    public string FFmpegExtraArgs {
        get => prefs.GetString("ffmpegArgs", "");
        set => Edit().PutString("ffmpegArgs", value ?? "").Apply();
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
}

public sealed class AndroidRenderingSettings(AppSettingsStore store) : RenderingSettings {
    public override float Gain {
        get => QMath.DbToGain(store.RenderGainDb);
        set => store.RenderGainDb = QMath.GainToDb(value);
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
}

public sealed class AndroidLicencePrompt(Func<string, string, bool> promptUser) : ILicence {
    string desc = "";
    string text = "";

    public void SetDescription(string description) => desc = description;
    public void SetLicenceText(string licenceText) => text = licenceText;
    public bool Prompt() => promptUser?.Invoke(desc, text) ?? false;
}
