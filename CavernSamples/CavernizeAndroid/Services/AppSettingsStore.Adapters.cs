using Cavern;
using Cavern.CavernSettings;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Language;

using VoidX.WPF.FFmpeg;
using VoidX.WPF.Language;

namespace CavernizeAndroid.Services;

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
        get => true;
        set { }
    }

    public override bool AtmosAware {
        get => (store.MatrixUpmixing || store.CavernizeUpmix) && store.AtmosAwareUpmix;
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
