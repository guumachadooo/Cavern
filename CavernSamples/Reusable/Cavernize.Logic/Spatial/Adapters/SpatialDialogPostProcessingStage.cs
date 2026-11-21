using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6B post-processing stage (Priority 500): Zero-smearing Spatial Dialog Enhancement.
/// Yields transparently (0.00 dB pass-through) when Path A pre-virtualizer spatial isolation (discrete Center bed
/// or front-center dialogue-cone 3D objects) is already active in <c>RealtimePlayerEngine</c>, and engages
/// Path B (phase-coherent 3-band LR4 Mid/Side speech isolation) on stereo 2.0 material without Center channels.
/// </summary>
public sealed class SpatialDialogPostProcessingStage : IPostProcessingStage {
    readonly SpatialDialogEnhancer enhancer = new(Listener.DefaultSampleRate);
    bool userEnabled;
    bool spatialPathActive;

    /// <inheritdoc/>
    public string StageId => "spatial-dialog-enhancer";

    /// <inheritdoc/>
    public string DisplayName => "Spatial Dialog Enhancement";

    /// <inheritdoc/>
    public int Priority => 500;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => userEnabled && enhancer.IsActive;
        set {
            userEnabled = value;
            enhancer.SetParameters(value, enhancer.BoostDb);
        }
    }

    /// <summary>
    /// Set by <c>RealtimePlayerEngine</c> when discrete Center bed / dialogue-cone 3D objects are isolated
    /// pre-virtualizer (Path A), preventing double-boosting in the post-virtualizer stereo stage (Path B).
    /// </summary>
    public bool SpatialPathActive {
        get => spatialPathActive;
        set => spatialPathActive = value;
    }

    /// <summary>Configured dialogue intelligibility boost in dB (0..6 dB).</summary>
    public float BoostDb => enhancer.BoostDb;

    /// <summary>Latest speech activity confidence in [0; 1] (1.0 when Path A spatial isolation is engaged).</summary>
    public float SpeechActivity => IsEnabled ? (spatialPathActive ? 1f : enhancer.SpeechActivity) : 0f;

    /// <summary>
    /// Optional external speech-activity probability [0; 1] from W08 Neural Vocal Mask or W09 Lossless Twin.
    /// Set to <see cref="float.NaN"/> (default) for built-in Mid/Side coherence detection.
    /// </summary>
    public float ExternalSpeechActivity {
        get => enhancer.ExternalSpeechActivity;
        set => enhancer.ExternalSpeechActivity = value;
    }

    /// <summary>Atomically updates enable flag and dialogue boost depth (0..6 dB).</summary>
    public void ApplyParameters(bool enabled, float boostDb) {
        userEnabled = enabled;
        enhancer.SetParameters(enabled, boostDb);
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        enhancer.Configure(sampleRate);
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        // Yield cleanly when disabled, non-stereo, or Path A pre-virtualizer spatial isolation is already active
        if (!IsEnabled || spatialPathActive || channels != 2) {
            return;
        }
        if (context != null && context.SampleRate >= 8000 && context.SampleRate != enhancer.SampleRate) {
            enhancer.Configure(context.SampleRate);
        }
        enhancer.ProcessStereoInterleaved(buffer, offset, frameCount);
    }

    /// <inheritdoc/>
    public void Reset() {
        enhancer.Reset();
    }

    /// <inheritdoc/>
    public IPostProcessingStage CloneForOfflineExport() {
        SpatialDialogPostProcessingStage clone = new() {
            SpatialPathActive = spatialPathActive,
            ExternalSpeechActivity = ExternalSpeechActivity
        };
        clone.ApplyParameters(userEnabled, BoostDb);
        return clone;
    }

    /// <inheritdoc/>
    public void Dispose() {
        userEnabled = false;
        enhancer.SetParameters(false, 0f);
    }
}
