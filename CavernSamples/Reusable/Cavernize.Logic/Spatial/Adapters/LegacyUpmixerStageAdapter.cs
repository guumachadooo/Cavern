using Cavern.CavernSettings;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 2 pass-through adapter exposing Cavern's existing <see cref="UpmixingSettings"/> (Matrix 7.1 <see cref="Cavern.Remapping.SurroundUpmixer"/>
/// and Cavernize 3D <see cref="Cavern.Remapping.CavernizeUpmixer"/>) inside the 6-slot pipeline without double-processing.
/// Actual DSP remains wired inside <see cref="Models.CavernizeTrack.AttachFromCurrentPosition"/> until Wave W02 upgrades Slot 2A.
/// </summary>
public sealed class LegacyUpmixerStageAdapter : ISpatialStageProcessor {
    UpmixingSettings settings;
    SpatialProgramMetadata activeMetadata = SpatialProgramMetadata.CreateDefault();
    int sampleRate = 48000;

    /// <inheritdoc/>
    public string StageId => "legacy-upmix";

    /// <inheritdoc/>
    public string DisplayName => "Cavern Legacy Matrix & 3D Upmix";

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool SupportsAtmosAware => false;

    /// <summary>
    /// Active legacy upmixing settings mirrored from the player engine.
    /// </summary>
    public UpmixingSettings Settings => settings;

    /// <summary>
    /// Active sample rate configured for this stage.
    /// </summary>
    public int SampleRate => sampleRate;

    /// <summary>
    /// Creates a Slot 2 pass-through adapter for <paramref name="settings"/>.
    /// </summary>
    public LegacyUpmixerStageAdapter(UpmixingSettings settings) {
        this.settings = settings ?? new UpmixingSettings(true);
    }

    /// <summary>
    /// Updates the mirrored <see cref="UpmixingSettings"/> reference without allocating DSP state.
    /// </summary>
    public void UpdateSettings(UpmixingSettings newSettings) {
        settings = newSettings ?? new UpmixingSettings(true);
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        this.sampleRate = sampleRate > 0 ? sampleRate : 48000;
        activeMetadata = metadata;
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        // Non-breaking pass-through: existing SurroundUpmixer / CavernizeUpmixer already execute inside Listener.Render().
        // Wave W02 will replace/augment this slot with STFT DirAC/COMPASS & Atmos-Aware upmixing.
        _ = activeMetadata;
        _ = frame;
    }

    /// <inheritdoc/>
    public void Reset() {
        // Stateless pass-through adapter.
    }

    /// <inheritdoc/>
    public void Dispose() {
        // Stateless pass-through adapter.
    }
}
