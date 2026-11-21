using Cavern.Spatial;
using Cavern.VocalChain;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Slot 2D spatial stage processor coupling <see cref="LiveMicrophoneBridge"/> and <see cref="LiveVocalChainDsp"/>
/// to perform zero-latency Auto-Vocal Mix-Match and inject the processed vocal stream into <see cref="SpatialSceneHub"/>.
/// </summary>
public sealed class VocalMixMatchStageProcessor : ISpatialStageProcessor {
    readonly LiveVocalChainDsp dsp = new();
    VocalMixDnaProfile activeDna = VocalMixDnaProfile.CreateStudioDefault();
    VocalMacroSettings activeMacros = new();
    float[] matchEqDeltasDb;
    int configuredSampleRate = 48000;

    /// <inheritdoc/>
    public string StageId => "vocal-mix-match-2d";

    /// <inheritdoc/>
    public string DisplayName => "Auto-Vocal Mix-Match & Live Mastering Chain";

    /// <inheritdoc/>
    public int Priority => 450;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => activeMacros.Enabled;
        set {
            activeMacros.Enabled = value;
            dsp.Configure(configuredSampleRate, activeDna, activeMacros, matchEqDeltasDb);
        }
    }

    /// <inheritdoc/>
    public bool SupportsAtmosAware => true;

    /// <summary>Latest real-time vocal rack telemetry (input/output dBFS, Gate, De-Esser, Opto+FET GR, Sidechain Pocket).</summary>
    public VocalChainTelemetry Telemetry => dsp.Telemetry;

    /// <summary>Currently loaded song vocal mix DNA profile.</summary>
    public VocalMixDnaProfile ActiveDna => activeDna;

    /// <summary>Applies a freshly extracted song vocal DNA profile and optional mic voiceprint Match EQ deltas.</summary>
    public void ApplyVocalProfile(VocalMixDnaProfile dna, VocalMacroSettings macros, float[] matchEqDeltas = null) {
        activeDna = dna ?? VocalMixDnaProfile.CreateStudioDefault();
        activeMacros = macros ?? new VocalMacroSettings();
        matchEqDeltasDb = matchEqDeltas;
        dsp.Configure(configuredSampleRate, activeDna, activeMacros, matchEqDeltasDb);
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        configuredSampleRate = Math.Clamp(sampleRate, 16000, 192000);
        dsp.Configure(configuredSampleRate, activeDna, activeMacros, matchEqDeltasDb);
    }

    /// <summary>Processes a live stereo mic buffer in place and optionally ducks 1-4 kHz in the backing instrumental.</summary>
    public void ProcessLiveVocalZeroGc(
        float[] vocalStereoInterleaved,
        int vocalOffset,
        int frameCount,
        float[] backingStereoInterleaved = null,
        int backingOffset = 0) {
        dsp.ProcessStereoBlockZeroGc(
            vocalStereoInterleaved,
            vocalOffset,
            frameCount,
            backingStereoInterleaved,
            backingOffset);
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        // Scene-frame hook ensures Atmos vocal placement metadata stays synchronized with active DNA
        if (frame == null || !activeMacros.Enabled) {
            return;
        }
        IReadOnlyList<SpatialSourceDescriptor> sources = frame.Sources;
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].Layer == SourceLayer.LiveMicrophone) {
                sources[i].NormalizedPosition = activeDna.VocalAtmosPosition;
                break;
            }
        }
    }

    /// <inheritdoc/>
    public void Reset() {
        dsp.Reset();
    }

    /// <inheritdoc/>
    public void Dispose() {
        dsp.Reset();
    }
}
