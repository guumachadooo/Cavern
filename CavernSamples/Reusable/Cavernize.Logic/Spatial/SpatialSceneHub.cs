using System.Numerics;

using Cavern.Spatial;

using Cavernize.Logic.Models;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Slot 3 backbone: Single source of truth connecting Slot 1 (Decoders), Slot 2 (Stage Processors),
/// Slot 4 (Head Trackers), Slot 5 (Binaural Renderer), Slot 6 (Post-Processing), the 3D Room, and the Channel Mixer.
/// </summary>
public sealed class SpatialSceneHub {
    readonly object syncLock = new();
    readonly List<SpatialSourceDescriptor> sources = [];
    readonly List<ISpatialStageProcessor> stageProcessors = [];
    readonly List<IPostProcessingStage> postStages = [];

    ISpatialStageProcessor[] stageSnapshot = Array.Empty<ISpatialStageProcessor>();
    IPostProcessingStage[] postSnapshot = Array.Empty<IPostProcessingStage>();

    ListenerPose6DoF listenerPose = ListenerPose6DoF.Identity;
    SpatialProgramMetadata programMetadata = SpatialProgramMetadata.CreateDefault();
    SoundfieldTelemetry telemetry = SoundfieldTelemetry.Empty();

    /// <summary>
    /// Raised when the structural scene (active track, decoder, source list, or binaural profile) changes.
    /// </summary>
    public event Action SceneChanged;

    /// <summary>
    /// Active Slot 1 spatial decoder.
    /// </summary>
    public ISpatialDecoder ActiveDecoder { get; private set; }

    /// <summary>
    /// Active Slot 4 head-tracking provider.
    /// </summary>
    public IHeadTrackerProvider ActiveHeadTracker { get; private set; }

    /// <summary>
    /// Active Slot 5 binaural renderer.
    /// </summary>
    public IBinauralRenderer ActiveRenderer { get; private set; }

    /// <summary>
    /// Active binaural profile identifier mirrored from platform profile service (e.g., "sadie_d1_dfc").
    /// </summary>
    public string ActiveBinauralProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Active binaural profile human-readable label.
    /// </summary>
    public string ActiveBinauralProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Acoustic compensation state of the active binaural profile (DFC HRIR, Raw HRIR, or Direct 2.0)
    /// used by Slot 6A (<see cref="Adapters.AutoEqPostProcessingStage"/>) to enforce the Zero Double-EQ Guard.
    /// </summary>
    public Cavern.Filters.BinauralCompensationMode ActiveBinauralCompensationMode { get; set; } =
        Cavern.Filters.BinauralCompensationMode.DiffuseFieldCompensatedHrir;

    /// <summary>
    /// Active AutoEq headphone profile identifier (empty when AutoEq is bypassed/flat).
    /// </summary>
    public string ActiveAutoEqProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Active AutoEq headphone profile display name.
    /// </summary>
    public string ActiveAutoEqProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Normalized hardware key of the active output audio device (e.g., "bt:galaxy_buds2_pro", "usb:dac", "wired:35mm").
    /// </summary>
    public string ActiveAudioDeviceKey { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name of the active output audio device.
    /// </summary>
    public string ActiveAudioDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label of the currently active Slot 4 head tracker (e.g., "Galaxy Buds2 Pro", "Android IMU", "BLE/OSC").
    /// </summary>
    public string ActiveHeadTrackerName { get; set; } = "Manual 3D Control";

    /// <summary>
    /// True when a hardware/sensor head tracker is actively driving <see cref="ListenerPose"/>.
    /// </summary>
    public bool IsHeadTrackingLive { get; set; }

    /// <summary>
    /// True when the active binaural profile is a measured room BRIR (&gt; 1024 taps or flagged BRIR),
    /// triggering Slot 5A's automatic double-room bypass guard for synthetic ISM reflections.
    /// </summary>
    public bool IsMeasuredBrirActive { get; set; }

    /// <summary>
    /// Live Slot 5A physical externalization telemetry (ISM room active/BRIR guard state, Sabine/Eyring RT60, near-field, ISO 9613-1).
    /// </summary>
    public PhysicalExternalizationDiagnostics ExternalizationDiagnostics {
        get {
            lock (syncLock) {
                return externalizationDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                externalizationDiagnostics = value;
            }
        }
    }
    PhysicalExternalizationDiagnostics externalizationDiagnostics = PhysicalExternalizationDiagnostics.Default;

    /// <summary>
    /// Live Slot 5B HRTF personalization telemetry (MagLS Ambisonic order/cutoff/HF error, Woodworth-Schlosberg biometric ITD, HpTF notch).
    /// </summary>
    public HrtfPersonalizationDiagnostics PersonalizationDiagnostics {
        get {
            lock (syncLock) {
                return personalizationDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                personalizationDiagnostics = value;
            }
        }
    }
    HrtfPersonalizationDiagnostics personalizationDiagnostics = HrtfPersonalizationDiagnostics.Default;

    /// <summary>
    /// Live Slot 6B psychoacoustics &amp; studio loudness telemetry (ISO 226:2023, ITU-R BS.1770-5 M/S/I/LRA/dBTP, Dialnorm, Dialog, Limiter GR).
    /// </summary>
    public LoudnessStandardsDiagnostics LoudnessDiagnostics {
        get {
            lock (syncLock) {
                return loudnessDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                loudnessDiagnostics = value;
            }
        }
    }
    LoudnessStandardsDiagnostics loudnessDiagnostics = LoudnessStandardsDiagnostics.Default;

    /// <summary>
    /// Live Slot 4 audio output telemetry (sample rate, Float32, LowLatency, USB DAC preference, estimated latency, underruns).
    /// </summary>
    public AudioOutputDiagnostics AudioDiagnostics {
        get {
            lock (syncLock) {
                return audioDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                audioDiagnostics = value;
            }
        }
    }
    AudioOutputDiagnostics audioDiagnostics = AudioOutputDiagnostics.Default;

    /// <summary>
    /// Current 6DoF listener pose (thread-safe snapshot).
    /// </summary>
    public ListenerPose6DoF ListenerPose {
        get {
            lock (syncLock) {
                return listenerPose;
            }
        }
        set {
            lock (syncLock) {
                listenerPose = value;
            }
        }
    }

    /// <summary>
    /// Programme-level ITU-R BS.2127 / BS.1770 metadata for the active stream.
    /// </summary>
    public SpatialProgramMetadata ProgramMetadata {
        get {
            lock (syncLock) {
                return programMetadata;
            }
        }
        set {
            lock (syncLock) {
                programMetadata = value;
            }
        }
    }

    /// <summary>
    /// Latest acoustic telemetry snapshot (coherence, diffuseness, DoA, LUFS, True Peak).
    /// </summary>
    public SoundfieldTelemetry Telemetry {
        get {
            lock (syncLock) {
                return telemetry;
            }
        }
        set {
            lock (syncLock) {
                telemetry = value;
            }
        }
    }

    /// <summary>
    /// Active scene source descriptors (beds, native objects, upmixed virtual sources, neural stems, HOA).
    /// </summary>
    public IReadOnlyList<SpatialSourceDescriptor> Sources => sources;

    /// <summary>
    /// Ordered Slot 2 stage processor snapshot safe for allocation-free traversal on the audio thread.
    /// </summary>
    public IReadOnlyList<ISpatialStageProcessor> StageProcessors => stageSnapshot;

    /// <summary>
    /// Ordered Slot 6 post-processing stage snapshot safe for allocation-free traversal on the audio thread.
    /// </summary>
    public IReadOnlyList<IPostProcessingStage> PostProcessingStages => postSnapshot;

    /// <summary>
    /// Binds Slot 1 decoder and replaces the active source descriptor table atomically.
    /// </summary>
    public void BindDecoder(ISpatialDecoder decoder, IReadOnlyList<SpatialSourceDescriptor> initialSources = null) {
        lock (syncLock) {
            ActiveDecoder = decoder;
            programMetadata = decoder != null ? decoder.Metadata : SpatialProgramMetadata.CreateDefault();
            sources.Clear();
            IReadOnlyList<SpatialSourceDescriptor> list = initialSources ?? decoder?.DescribeSources();
            if (list != null) {
                for (int i = 0; i < list.Count; i++) {
                    sources.Add(list[i]);
                }
            }
        }
        SceneChanged?.Invoke();
    }

    /// <summary>
    /// Binds Slot 4 head tracker provider.
    /// </summary>
    public void BindHeadTracker(IHeadTrackerProvider provider) {
        lock (syncLock) {
            ActiveHeadTracker = provider;
        }
        SceneChanged?.Invoke();
    }

    /// <summary>
    /// Binds Slot 5 binaural renderer.
    /// </summary>
    public void BindRenderer(IBinauralRenderer renderer) {
        lock (syncLock) {
            ActiveRenderer = renderer;
        }
        SceneChanged?.Invoke();
    }

    /// <summary>
    /// Registers or replaces a Slot 2 stage processor (matched by <see cref="ISpatialStageProcessor.StageId"/>)
    /// and maintains strict ascending priority order.
    /// </summary>
    public void RegisterStageProcessor(ISpatialStageProcessor stage) {
        ArgumentNullException.ThrowIfNull(stage);
        lock (syncLock) {
            for (int i = 0; i < stageProcessors.Count; i++) {
                if (string.Equals(stageProcessors[i].StageId, stage.StageId, StringComparison.OrdinalIgnoreCase)) {
                    stageProcessors[i] = stage;
                    RebuildStageSnapshotLocked();
                    return;
                }
            }
            stageProcessors.Add(stage);
            RebuildStageSnapshotLocked();
        }
    }

    /// <summary>
    /// Removes a Slot 2 stage processor by <paramref name="stageId"/>.
    /// </summary>
    public bool UnregisterStageProcessor(string stageId) {
        if (string.IsNullOrEmpty(stageId)) {
            return false;
        }
        lock (syncLock) {
            for (int i = 0; i < stageProcessors.Count; i++) {
                if (string.Equals(stageProcessors[i].StageId, stageId, StringComparison.OrdinalIgnoreCase)) {
                    stageProcessors.RemoveAt(i);
                    RebuildStageSnapshotLocked();
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Registers or replaces a Slot 6 post-processing stage (matched by <see cref="IPostProcessingStage.StageId"/>)
    /// and maintains strict ascending priority order.
    /// </summary>
    public void RegisterPostProcessingStage(IPostProcessingStage stage) {
        ArgumentNullException.ThrowIfNull(stage);
        lock (syncLock) {
            for (int i = 0; i < postStages.Count; i++) {
                if (string.Equals(postStages[i].StageId, stage.StageId, StringComparison.OrdinalIgnoreCase)) {
                    postStages[i] = stage;
                    RebuildPostSnapshotLocked();
                    return;
                }
            }
            postStages.Add(stage);
            RebuildPostSnapshotLocked();
        }
    }

    /// <summary>
    /// Removes a Slot 6 post-processing stage by <paramref name="stageId"/>.
    /// </summary>
    public bool UnregisterPostProcessingStage(string stageId) {
        if (string.IsNullOrEmpty(stageId)) {
            return false;
        }
        lock (syncLock) {
            for (int i = 0; i < postStages.Count; i++) {
                if (string.Equals(postStages[i].StageId, stageId, StringComparison.OrdinalIgnoreCase)) {
                    postStages.RemoveAt(i);
                    RebuildPostSnapshotLocked();
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Synchronizes live position, mute/solo, dynamic filter, and RMS telemetry from existing <see cref="TrackSourceItem"/>s
    /// into the hub's <see cref="SpatialSourceDescriptor"/> list without heap allocation.
    /// </summary>
    public void SyncTelemetryFromTrackItems(IReadOnlyList<TrackSourceItem> items, Vector3 environmentSize) {
        if (items == null) {
            return;
        }
        int count = Math.Min(items.Count, sources.Count);
        for (int i = 0; i < count; i++) {
            TrackSourceItem item = items[i];
            SpatialSourceDescriptor desc = sources[i];
            desc.IsMuted = item.IsMuted;
            desc.IsSolo = item.IsSolo;
            desc.IsDynamicallyFilteredOut = item.IsDynamicallyFilteredOut;
            desc.NormalizedPosition = item.NormalizedPosition;
            desc.WorldPositionMeters = item.NormalizedPosition * environmentSize;
            desc.RmsLevel = item.RmsLevel;
        }
    }

    /// <summary>
    /// Applies a manual 3D room position override for a source (used by W10 draggable objects/stems).
    /// </summary>
    public bool SetSourceCustomPosition(int sourceIndex, Vector3 normalizedPosition) {
        lock (syncLock) {
            if (sourceIndex < 0 || sourceIndex >= sources.Count) {
                return false;
            }
            sources[sourceIndex].HasCustomPosition = true;
            sources[sourceIndex].CustomNormalizedPosition = normalizedPosition;
            return true;
        }
    }

    /// <summary>
    /// Clears all manual solo, mute, and position overrides across every source in the scene.
    /// </summary>
    public void ClearSourceOverrides() {
        lock (syncLock) {
            for (int i = 0; i < sources.Count; i++) {
                sources[i].IsMuted = false;
                sources[i].IsSolo = false;
                sources[i].IsDynamicallyFilteredOut = false;
                sources[i].HasCustomPosition = false;
                sources[i].CustomNormalizedPosition = Vector3.Zero;
                sources[i].Gain = 1f;
            }
        }
    }

    /// <summary>
    /// Resets stateful stages across Slots 2, 5, and 6 (called on seek, track swap, or stop).
    /// </summary>
    public void ResetStatefulStages() {
        ISpatialStageProcessor[] stages = stageSnapshot;
        for (int i = 0; i < stages.Length; i++) {
            stages[i].Reset();
        }
        ActiveRenderer?.Reset();
        IPostProcessingStage[] post = postSnapshot;
        for (int i = 0; i < post.Length; i++) {
            post[i].Reset();
        }
    }

    /// <summary>
    /// Configures all registered Slot 2 stage processors for the given sample rate and programme metadata.
    /// </summary>
    public void ConfigureStageProcessors(int sampleRate, SpatialProgramMetadata metadata) {
        ISpatialStageProcessor[] stages = stageSnapshot;
        for (int i = 0; i < stages.Length; i++) {
            stages[i].Configure(sampleRate, metadata);
        }
    }

    /// <summary>
    /// Configures all registered Slot 6 post-processing stages for the given sample rate and channel count.
    /// </summary>
    public void ConfigurePostProcessingStages(int sampleRate, int channels) {
        IPostProcessingStage[] post = postSnapshot;
        for (int i = 0; i < post.Length; i++) {
            post[i].Configure(sampleRate, channels);
        }
    }

    void RebuildStageSnapshotLocked() {
        stageProcessors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        stageSnapshot = stageProcessors.ToArray();
    }

    void RebuildPostSnapshotLocked() {
        postStages.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        postSnapshot = postStages.ToArray();
    }
}
