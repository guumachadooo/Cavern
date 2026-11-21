using System.Numerics;

using Cavern.Spatial;
using Cavern.VocalChain;

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
    readonly SpatialXRayPowermap xrayPowermap = new();
    VocalChainTelemetry vocalChainDiagnostics;

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
    /// Live Slot 2B neural audio, MVSEP anti-artifact, and Spatial Karaoke telemetry.
    /// </summary>
    public NeuralAudioDiagnostics NeuralDiagnostics {
        get {
            lock (syncLock) {
                return neuralDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                neuralDiagnostics = value;
            }
        }
    }
    NeuralAudioDiagnostics neuralDiagnostics = NeuralAudioDiagnostics.Default;

    /// <summary>
    /// Optional W09 lossless reference twin provider for sample-aligned Spatial Karaoke, LGSR, Dialog, and X-Ray acceleration.
    /// </summary>
    public ILosslessReferenceProvider ActiveLosslessReference {
        get {
            lock (syncLock) {
                return activeLosslessReference;
            }
        }
    }
    ILosslessReferenceProvider activeLosslessReference;

    /// <summary>
    /// Live Slot 2C Lossless-Guided Atmos Restoration (LGSR) telemetry (twin status, sub-sample offset, ppm drift, coherence, coupling/SPX Hz).
    /// </summary>
    public LgsrDiagnostics LgsrDiagnostics {
        get {
            lock (syncLock) {
                return lgsrDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                lgsrDiagnostics = value;
            }
        }
    }
    LgsrDiagnostics lgsrDiagnostics = LgsrDiagnostics.Default;

    /// <summary>
    /// Active Slot 1 live microphone provider (<see cref="SourceLayer.LiveMicrophone"/>) fed by W10 Path B `libusb` UAC2 or Path A `AAudio/Oboe`.
    /// </summary>
    public ILiveMicrophoneProvider ActiveLiveMicrophone {
        get {
            lock (syncLock) {
                return activeLiveMicrophone;
            }
        }
    }
    ILiveMicrophoneProvider activeLiveMicrophone;

    /// <summary>
    /// Live Slot 1 / Slot 4 Pro-Audio full-duplex USB &amp; Scarlett 2i2 telemetry snapshot.
    /// </summary>
    public ProAudioDuplexDiagnostics ProAudioDiagnostics {
        get {
            lock (syncLock) {
                return proAudioDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                proAudioDiagnostics = value ?? ProAudioDuplexDiagnostics.Default;
            }
        }
    }
    ProAudioDuplexDiagnostics proAudioDiagnostics = ProAudioDuplexDiagnostics.Default;

    /// <summary>
    /// Live Slot 2D Auto-Vocal Mix-Match &amp; Mastering Chain telemetry snapshot for the W13 Spatial X-Ray HUD.
    /// </summary>
    public VocalChainTelemetry VocalChainDiagnostics {
        get {
            lock (syncLock) {
                return vocalChainDiagnostics;
            }
        }
        set {
            lock (syncLock) {
                vocalChainDiagnostics = value;
            }
        }
    }

    /// <summary>
    /// W13 zero-GC 3D spherical 3-band directional energy heatmap updated each audio block.
    /// </summary>
    public SpatialXRayPowermap XRayPowermap => xrayPowermap;

    /// <summary>
    /// Active spectral filter band displayed in the 3D Room Spatial X-Ray overlay.
    /// </summary>
    public SpatialXRayBandMode ActiveXRayBandMode { get; set; } = SpatialXRayBandMode.AllBands;

    /// <summary>
    /// True when pure-physics 6DoF acoustic propagation delay (343 m/s) is active.
    /// </summary>
    public bool PropagationDelay6DoFEnabled { get; set; } = true;

    /// <summary>
    /// True when Slot 2C LGSR is momentarily bypassed for instant Lossy-vs-Lossless A/B comparison.
    /// </summary>
    public bool LgsrBypassed { get; set; }

    /// <summary>
    /// True when Slot 2D Live Vocal Chain is momentarily bypassed for instant A/B comparison.
    /// </summary>
    public bool VocalChainBypassed { get; set; }

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
    /// Binds an optional W09 lossless reference twin provider for lossless-accelerated neural vocal masking.
    /// </summary>
    public void BindLosslessReference(ILosslessReferenceProvider provider) {
        lock (syncLock) {
            activeLosslessReference = provider;
        }
        SceneChanged?.Invoke();
    }

    /// <summary>
    /// Binds Slot 1 live microphone provider (<see cref="SourceLayer.LiveMicrophone"/>).
    /// </summary>
    public void BindLiveMicrophone(ILiveMicrophoneProvider provider) {
        lock (syncLock) {
            activeLiveMicrophone = provider;
        }
        SceneChanged?.Invoke();
    }

    /// <summary>
    /// Inserts, updates, or removes the active <see cref="SourceLayer.LiveMicrophone"/> descriptor in-place
    /// without disturbing beds, native Atmos objects, or neural stems.
    /// </summary>
    public void SetLiveMicrophoneDescriptor(SpatialSourceDescriptor micDescriptor) {
        bool structuralChange = false;
        lock (syncLock) {
            int existingIdx = -1;
            for (int i = 0; i < sources.Count; i++) {
                if (sources[i].Layer == SourceLayer.LiveMicrophone) {
                    existingIdx = i;
                    break;
                }
            }
            if (micDescriptor == null) {
                if (existingIdx >= 0) {
                    sources.RemoveAt(existingIdx);
                    structuralChange = true;
                }
            } else if (existingIdx >= 0) {
                SpatialSourceDescriptor dst = sources[existingIdx];
                dst.Name = micDescriptor.Name;
                if (!dst.HasCustomPosition) {
                    dst.NormalizedPosition = micDescriptor.NormalizedPosition;
                    dst.WorldPositionMeters = micDescriptor.WorldPositionMeters;
                }
                dst.Gain = micDescriptor.Gain;
                dst.RmsLevel = micDescriptor.RmsLevel;
                dst.PeakLevel = micDescriptor.PeakLevel;
                dst.LowBandEnergy = micDescriptor.LowBandEnergy;
                dst.MidBandEnergy = micDescriptor.MidBandEnergy;
                dst.HighBandEnergy = micDescriptor.HighBandEnergy;
                dst.PropagationDelayMs = micDescriptor.PropagationDelayMs;
            } else {
                sources.Add(micDescriptor);
                structuralChange = true;
            }
        }
        if (structuralChange) {
            SceneChanged?.Invoke();
        }
    }

    /// <summary>
    /// Replaces any existing <see cref="SourceLayer.NeuralStem"/> or <see cref="SourceLayer.RestoredResidual"/> descriptors
    /// with the provided Slot 2B separated stems while preserving beds and native Atmos objects intact.
    /// </summary>
    public void RegisterNeuralStemDescriptors(IReadOnlyList<SpatialSourceDescriptor> stemDescriptors) {
        lock (syncLock) {
            for (int i = sources.Count - 1; i >= 0; i--) {
                SourceLayer layer = sources[i].Layer;
                if (layer == SourceLayer.NeuralStem || layer == SourceLayer.RestoredResidual) {
                    sources.RemoveAt(i);
                }
            }
            if (stemDescriptors != null) {
                for (int i = 0; i < stemDescriptors.Count; i++) {
                    sources.Add(stemDescriptors[i]);
                }
            }
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
    /// Synchronizes live position, mute/solo, dynamic filter, 3-band RMS, and 6DoF propagation delay telemetry
    /// from existing <see cref="TrackSourceItem"/>s into the hub's <see cref="SpatialSourceDescriptor"/> list
    /// and updates the zero-GC <see cref="XRayPowermap"/> without heap allocation.
    /// </summary>
    public void SyncTelemetryFromTrackItems(IReadOnlyList<TrackSourceItem> items, Vector3 environmentSize) {
        if (items == null) {
            return;
        }
        Vector3 safeEnv = new(
            MathF.Max(0.25f, environmentSize.X),
            MathF.Max(0.25f, environmentSize.Y),
            MathF.Max(0.25f, environmentSize.Z));
        Vector3 listenerNorm = listenerPose.Position / safeEnv;

        int count = Math.Min(items.Count, sources.Count);
        for (int i = 0; i < count; i++) {
            TrackSourceItem item = items[i];
            SpatialSourceDescriptor desc = sources[i];
            desc.IsMuted = item.IsMuted;
            desc.IsSolo = item.IsSolo;
            desc.IsDynamicallyFilteredOut = item.IsDynamicallyFilteredOut;
            desc.HasCustomPosition = item.HasCustomPosition;
            desc.CustomNormalizedPosition = item.CustomNormalizedPosition;
            desc.NormalizedPosition = item.NormalizedPosition;
            desc.WorldPositionMeters = item.NormalizedPosition * safeEnv;
            desc.RmsLevel = item.RmsLevel;
            desc.LowBandEnergy = item.LowBandEnergy;
            desc.MidBandEnergy = item.MidBandEnergy;
            desc.HighBandEnergy = item.HighBandEnergy;
            desc.PropagationDelayMs = item.PropagationDelayMs;
        }
        xrayPowermap.Update(sources, listenerNorm);
    }

    /// <summary>
    /// Applies a manual 3D room position override for a source (used by W13 draggable objects/stems/live mic).
    /// </summary>
    public bool SetSourceCustomPosition(int sourceIndex, Vector3 normalizedPosition) {
        lock (syncLock) {
            if (sourceIndex < 0 || sourceIndex >= sources.Count) {
                return false;
            }
            Vector3 clamped = new(
                Math.Clamp(normalizedPosition.X, -1f, 1f),
                Math.Clamp(normalizedPosition.Y, -1f, 1f),
                Math.Clamp(normalizedPosition.Z, -1f, 1f));
            sources[sourceIndex].HasCustomPosition = true;
            sources[sourceIndex].CustomNormalizedPosition = clamped;
            sources[sourceIndex].NormalizedPosition = clamped;
            return true;
        }
    }

    /// <summary>
    /// Clears a single source's manual 3D room pin so it resumes its native Atmos trajectory or stem default.
    /// </summary>
    public bool ClearSourceCustomPosition(int sourceIndex) {
        lock (syncLock) {
            if (sourceIndex < 0 || sourceIndex >= sources.Count) {
                return false;
            }
            sources[sourceIndex].HasCustomPosition = false;
            sources[sourceIndex].CustomNormalizedPosition = Vector3.Zero;
            return true;
        }
    }

    /// <summary>
    /// Clears all manual 3D room position pins while preserving active Solo/Mute states.
    /// </summary>
    public void ResetAllCustomPositions() {
        lock (syncLock) {
            for (int i = 0; i < sources.Count; i++) {
                sources[i].HasCustomPosition = false;
                sources[i].CustomNormalizedPosition = Vector3.Zero;
            }
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
