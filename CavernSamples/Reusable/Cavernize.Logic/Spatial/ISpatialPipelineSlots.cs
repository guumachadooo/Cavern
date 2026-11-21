using System.Numerics;

using Cavern;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Slot 1 contract: Decodes any channel-based, object-based (Atmos/ADM), IAMF, or HOA bitstream into
/// <see cref="SpatialSceneFrame"/> descriptors and PCM audio without coupling downstream DSP to codec details.
/// </summary>
public interface ISpatialDecoder : IDisposable {
    /// <summary>
    /// Human-readable decoder identifier (e.g., "Cavern E-AC-3 JOC", "Cavern DAMF", "IAMF").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Programme spatial classification (DirectSpeakers, Objects, Hoa, HybridBedAndObjects).
    /// </summary>
    SpatialContentKind ContentKind { get; }

    /// <summary>
    /// Native decoded stream sample rate in Hz.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Total stream duration in samples per channel, or 0 if unknown/live.
    /// </summary>
    long LengthSamples { get; }

    /// <summary>
    /// Current stream read position in samples per channel.
    /// </summary>
    long PositionSamples { get; }

    /// <summary>
    /// True when random-access <see cref="Seek(long)"/> is supported.
    /// </summary>
    bool SupportsSeek { get; }

    /// <summary>
    /// Programme-level ITU-R BS.2127 / BS.1770 metadata.
    /// </summary>
    SpatialProgramMetadata Metadata { get; }

    /// <summary>
    /// Returns the ordered source descriptors exposed by this decoder.
    /// </summary>
    IReadOnlyList<SpatialSourceDescriptor> DescribeSources();

    /// <summary>
    /// Seeks the stream to <paramref name="targetSample"/> and returns the actual sample position reached.
    /// </summary>
    long Seek(long targetSample);

    /// <summary>
    /// Populates <paramref name="frame"/> metadata and (when operating in discrete-buffer mode) decodes up to
    /// <paramref name="maxFrames"/> samples per source. Returns false when end-of-stream is reached.
    /// </summary>
    bool FillFrame(SpatialSceneFrame frame, int maxFrames);
}

/// <summary>
/// Slot 2 contract: Ordered spatial stage processor operating on the scene before binaural rendering
/// (e.g., Slot 2A Parametric STFT DirAC/COMPASS upmixer, Atmos-aware height enhancer, Slot 2B Neural Stem spatializer).
/// </summary>
public interface ISpatialStageProcessor : IDisposable {
    /// <summary>
    /// Stable machine identifier (e.g., "legacy-upmix", "stft-dirac-matrix", "neural-scnet-stems").
    /// </summary>
    string StageId { get; }

    /// <summary>
    /// Human-readable stage name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Execution order priority (lower values execute earlier: 100 = Matrix/DirAC, 200 = Height, 300 = Neural Stems).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Enables or bypasses this stage cleanly.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// True when this processor safely preserves native Atmos beds/objects instead of collapsing them.
    /// </summary>
    bool SupportsAtmosAware { get; }

    /// <summary>
    /// Configures internal FFT/filter state for the active sample rate and programme metadata.
    /// </summary>
    void Configure(int sampleRate, SpatialProgramMetadata metadata);

    /// <summary>
    /// Processes the active <see cref="SpatialSceneFrame"/> in place (updating sources, buffers, and/or telemetry).
    /// </summary>
    void Process(SpatialSceneFrame frame);

    /// <summary>
    /// Clears internal delay lines / overlap-add state (called on seek, track change, or stop).
    /// </summary>
    void Reset();
}

/// <summary>
/// Slot 4 contract: Supplies low-latency 3DoF/6DoF listener head pose (Manual UI, Galaxy Buds RFCOMM,
/// Android GameRotationVector IMU, or BLE-OSC) with optional short-horizon angular latency prediction.
/// </summary>
public interface IHeadTrackerProvider : IDisposable {
    /// <summary>
    /// Stable provider identifier (e.g., "manual", "galaxy-buds-rfcomm", "android-imu", "ble-osc").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-readable provider display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// True when the provider is actively delivering live sensor updates.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// True when the provider reports 6DoF translational coordinates in addition to 3DoF orientation.
    /// </summary>
    bool Supports6DoF { get; }

    /// <summary>
    /// Starts sensor/transport polling.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops sensor/transport polling without destroying configuration.
    /// </summary>
    void Stop();

    /// <summary>
    /// Captures the current physical head orientation as the forward reference (0° yaw/pitch/roll).
    /// </summary>
    void Recenter();

    /// <summary>
    /// Samples the latest listener pose, optionally extrapolated forward by <paramref name="predictionHorizonMs"/>.
    /// Must be lock-free or constant-time safe to invoke from the real-time audio thread.
    /// </summary>
    ListenerPose6DoF GetCurrentPose(float predictionHorizonMs = 0f);
}

/// <summary>
/// Slot 5 contract: Spatializes a <see cref="SpatialSceneFrame"/> into stereo headphone output
/// (e.g., Cavern VirtualizerFilter, PartitionedDualConvolver, MagLS Ambisonics, ISM Room + Near-Field + ISO 9613-1).
/// </summary>
public interface IBinauralRenderer : IDisposable {
    /// <summary>
    /// Stable renderer identifier (e.g., "cavern-virtualizer", "magls-ism-binaural").
    /// </summary>
    string RendererId { get; }

    /// <summary>
    /// Active HRIR/BRIR profile display name.
    /// </summary>
    string ProfileName { get; }

    /// <summary>
    /// Output sample rate in Hz produced by this renderer.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Monotonically increasing version counter incremented whenever HRIR/BRIR filters or acoustics change.
    /// </summary>
    int ProfileVersion { get; }

    /// <summary>
    /// True when binaural HRTF/BRIR processing is enabled; false when operating in Direct 2.0 stereo fold-down.
    /// </summary>
    bool IsBinauralActive { get; }

    /// <summary>
    /// Configures room dimensions, environment mode, and target sample rate.
    /// </summary>
    void Configure(int sampleRate, Vector3 environmentSize, Environments environmentType);

    /// <summary>
    /// Renders up to <paramref name="maxFrames"/> stereo frames into <paramref name="interleavedStereoOut"/>.
    /// </summary>
    int RenderFrame(SpatialSceneFrame frame, float[] interleavedStereoOut, int outputOffset, int maxFrames);

    /// <summary>
    /// Clears convolution overlap-add tails and acoustic delay lines.
    /// </summary>
    void Reset();
}

/// <summary>
/// Slot 6 contract: Strictly transparent, standards-based stereo/multichannel post-processing stage
/// (AutoEq neutral DF/5128 PEQ, HpTF notch, ISO 226:2023 equal-loudness, ITU-R BS.1770-5 loudness, True-Peak Limiter).
/// </summary>
public interface IPostProcessingStage : IDisposable {
    /// <summary>
    /// Stable stage identifier (e.g., "autoeq-peq", "iso226-loudness", "bs1770-meter", "true-peak-limiter").
    /// </summary>
    string StageId { get; }

    /// <summary>
    /// Human-readable stage name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Execution order priority (100=AutoEq, 200=HpTF, 300=ISO226, 400=BS1770/Dialnorm, 500=Dialog, 900=Limiter).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Enables or bypasses this stage transparently.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Configures filter coefficients for <paramref name="sampleRate"/> and <paramref name="channels"/>.
    /// </summary>
    void Configure(int sampleRate, int channels);

    /// <summary>
    /// Processes <paramref name="frameCount"/> interleaved frames starting at float index <paramref name="offset"/> in place.
    /// Must avoid heap allocations during steady-state audio execution.
    /// </summary>
    void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context);

    /// <summary>
    /// Resets biquad states, loudness sliding windows, and limiter envelope followers.
    /// </summary>
    void Reset();

    /// <summary>
    /// Creates an isolated clone with matching configuration and independent filter state for offline batch export.
    /// </summary>
    IPostProcessingStage CloneForOfflineExport();
}
