using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6 pass-through adapter wrapping Cavern's per-sample channel-linked soft-knee <see cref="Normalizer"/>.
/// Runs at priority 890 (optional upward AGC ahead of the final 4x True-Peak brickwall limiter at 900)
/// using a pre-allocated reusable buffer for zero steady-state GC.
/// </summary>
public sealed class NormalizerPostProcessingAdapter : IPostProcessingStage {
    Normalizer normalizer;
    float[] scratchBuffer = Array.Empty<float>();
    int sampleRate = Listener.DefaultSampleRate;
    int channels = 2;
    bool limiterOnly = true;

    /// <inheritdoc/>
    public string StageId => "cavern-soft-knee-limiter";

    /// <inheritdoc/>
    public string DisplayName => "Transparent Soft-Knee Limiter";

    /// <inheritdoc/>
    public int Priority => 890;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When true, acts strictly as a 0 dB transparent clipping protector without boosting quiet passages.
    /// </summary>
    public bool LimiterOnly {
        get => limiterOnly;
        set {
            limiterOnly = value;
            if (normalizer != null) {
                normalizer.limiterOnly = value;
            }
        }
    }

    /// <summary>
    /// Creates a Slot 6 adapter wrapping Cavern's soft-knee <see cref="Normalizer"/>.
    /// </summary>
    public NormalizerPostProcessingAdapter(bool limiterOnly = true) {
        this.limiterOnly = limiterOnly;
        normalizer = new Normalizer(limiterOnly) {
            SampleRate = sampleRate,
            Channels = channels
        };
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        this.sampleRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        this.channels = channels > 0 ? channels : 2;
        normalizer ??= new Normalizer(limiterOnly);
        normalizer.limiterOnly = limiterOnly;
        normalizer.SampleRate = this.sampleRate;
        normalizer.Channels = this.channels;
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (!IsEnabled || buffer == null || frameCount <= 0 || channels <= 0) {
            return;
        }
        int sliceLen = frameCount * channels;
        if (offset < 0 || offset + sliceLen > buffer.Length) {
            return;
        }

        normalizer ??= new Normalizer(limiterOnly);
        normalizer.limiterOnly = limiterOnly;
        normalizer.SampleRate = context != null && context.SampleRate >= 8000 ? context.SampleRate : sampleRate;
        normalizer.Channels = channels;
        normalizer.decayFactor = 10f * frameCount / normalizer.SampleRate;

        if (offset == 0 && sliceLen == buffer.Length) {
            normalizer.Process(buffer);
            return;
        }

        if (scratchBuffer.Length < sliceLen) {
            scratchBuffer = new float[sliceLen];
        }
        Array.Copy(buffer, offset, scratchBuffer, 0, sliceLen);
        normalizer.Process(scratchBuffer, sliceLen);
        Array.Copy(scratchBuffer, 0, buffer, offset, sliceLen);
    }

    /// <inheritdoc/>
    public void Reset() {
        if (normalizer == null) {
            normalizer = new Normalizer(limiterOnly) {
                SampleRate = sampleRate,
                Channels = channels
            };
        } else {
            normalizer.Reset();
        }
    }

    /// <inheritdoc/>
    public IPostProcessingStage CloneForOfflineExport() => new NormalizerPostProcessingAdapter(limiterOnly) {
        IsEnabled = IsEnabled
    };

    /// <inheritdoc/>
    public void Dispose() {
        normalizer = null;
        scratchBuffer = Array.Empty<float>();
    }
}
