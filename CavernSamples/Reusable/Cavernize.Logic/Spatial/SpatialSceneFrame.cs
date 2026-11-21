using System.Numerics;

using Cavern;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Reusable per-block spatial audio scene container passed across Slots 1–6.
/// Pre-allocates internal scratch arrays so steady-state real-time audio loops perform zero heap allocations.
/// </summary>
public sealed class SpatialSceneFrame {
    /// <summary>
    /// Active sample rate in Hz for this block.
    /// </summary>
    public int SampleRate { get; set; } = Listener.DefaultSampleRate;

    /// <summary>
    /// Number of sample frames per channel contained in this block.
    /// </summary>
    public int FrameCount { get; set; }

    /// <summary>
    /// Monotonic sample position within the decoded track at the start of this block.
    /// </summary>
    public long TrackSamplePosition { get; set; }

    /// <summary>
    /// High-resolution monotonic timestamp (Stopwatch ticks) when this block began processing.
    /// </summary>
    public long TimestampTicks { get; set; }

    /// <summary>
    /// Physical half-dimensions of the simulated room (Cavern room coordinates).
    /// </summary>
    public Vector3 EnvironmentSize { get; set; } = new Vector3(10f, 7f, 10f);

    /// <summary>
    /// Room shape/rendering mode.
    /// </summary>
    public Environments EnvironmentType { get; set; } = Environments.Home;

    /// <summary>
    /// True when headphone binaural virtualization is active for this block.
    /// </summary>
    public bool IsBinauralActive { get; set; } = true;

    /// <summary>
    /// Current 6DoF listener pose sampled from Slot 4 (manual or hardware head tracker).
    /// </summary>
    public ListenerPose6DoF ListenerPose { get; set; } = ListenerPose6DoF.Identity;

    /// <summary>
    /// Programme-level ITU-R BS.2127 / BS.1770 metadata supplied by Slot 1.
    /// </summary>
    public SpatialProgramMetadata Metadata { get; set; } = SpatialProgramMetadata.CreateDefault();

    /// <summary>
    /// Live acoustic telemetry (coherence, diffuseness, DoA, LUFS, True Peak) updated by Slots 2 and 6.
    /// </summary>
    public SoundfieldTelemetry Telemetry { get; set; } = SoundfieldTelemetry.Empty();

    /// <summary>
    /// Active source descriptors (beds, native objects, upmixed virtual sources, neural stems, HOA channels).
    /// </summary>
    public IReadOnlyList<SpatialSourceDescriptor> Sources { get; set; } = Array.Empty<SpatialSourceDescriptor>();

    /// <summary>
    /// Optional per-source mono PCM buffers ([sourceIndex][sample]) used by Slot 2/5 stages operating on discrete stems.
    /// Null in pass-through mode when Cavern's internal <see cref="Listener"/> owns per-source buffers directly.
    /// </summary>
    public float[][] SourceBuffers { get; set; }

    /// <summary>
    /// Number of populated entries in <see cref="SourceBuffers"/>.
    /// </summary>
    public int ActiveSourceBufferCount { get; set; }

    /// <summary>
    /// Interleaved output audio buffer populated by Slot 5 and post-processed by Slot 6.
    /// </summary>
    public float[] InterleavedOutput { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Offset in <see cref="InterleavedOutput"/> (in float samples) where the current block starts.
    /// </summary>
    public int OutputOffset { get; set; }

    /// <summary>
    /// Number of interleaved output channels (2 for binaural/stereo headphones).
    /// </summary>
    public int OutputChannels { get; set; } = 2;

    /// <summary>
    /// Number of valid output frames written into <see cref="InterleavedOutput"/>.
    /// </summary>
    public int OutputFrameCount { get; set; }

    /// <summary>
    /// Resets per-block counters and binds caller output storage without reallocating buffers.
    /// </summary>
    public void BeginBlock(
        int sampleRate,
        int frameCount,
        long trackSamplePosition,
        long timestampTicks,
        Vector3 environmentSize,
        Environments environmentType,
        bool isBinauralActive,
        ListenerPose6DoF listenerPose,
        SpatialProgramMetadata metadata,
        IReadOnlyList<SpatialSourceDescriptor> sources,
        float[] interleavedOutput,
        int outputOffset,
        int outputChannels) {
        SampleRate = sampleRate;
        FrameCount = frameCount;
        TrackSamplePosition = trackSamplePosition;
        TimestampTicks = timestampTicks;
        EnvironmentSize = environmentSize;
        EnvironmentType = environmentType;
        IsBinauralActive = isBinauralActive;
        ListenerPose = listenerPose;
        Metadata = metadata;
        Sources = sources ?? Array.Empty<SpatialSourceDescriptor>();
        InterleavedOutput = interleavedOutput ?? Array.Empty<float>();
        OutputOffset = outputOffset;
        OutputChannels = outputChannels;
        OutputFrameCount = 0;
    }

    /// <summary>
    /// Ensures <see cref="SourceBuffers"/> has at least <paramref name="sourceCount"/> channels of <paramref name="frameCount"/> samples.
    /// Reuses existing arrays whenever dimensions already fit to maintain Zero-GC operation.
    /// </summary>
    public void EnsureSourceBuffers(int sourceCount, int frameCount) {
        if (sourceCount <= 0 || frameCount <= 0) {
            ActiveSourceBufferCount = 0;
            return;
        }
        if (SourceBuffers == null || SourceBuffers.Length < sourceCount) {
            float[][] resized = new float[sourceCount][];
            if (SourceBuffers != null) {
                Array.Copy(SourceBuffers, resized, SourceBuffers.Length);
            }
            SourceBuffers = resized;
        }
        for (int i = 0; i < sourceCount; i++) {
            if (SourceBuffers[i] == null || SourceBuffers[i].Length < frameCount) {
                SourceBuffers[i] = new float[frameCount];
            }
        }
        ActiveSourceBufferCount = sourceCount;
    }
}
