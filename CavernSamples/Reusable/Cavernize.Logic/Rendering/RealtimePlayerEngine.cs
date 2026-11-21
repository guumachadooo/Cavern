using System.Diagnostics;
using System.Numerics;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Renderers;
using Cavern.Listeners;
using Cavern.Neural;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Cavern.Waveforms;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Real-time 3D spatial audio playback engine for <see cref="CavernizeTrack"/> with interactive room position,
/// listener head rotation, HRTF headphone virtualization, distance simulation, per-source solo/mute,
/// and the unified 6-slot spatial pipeline backbone (<see cref="SceneHub"/>).
/// </summary>
public sealed class RealtimePlayerEngine : IDisposable {
    const int updateRate = 256;
    const int framesPerChunk = 4; // 1024 samples per render block (~21.3 ms at 48 kHz)
    const int crossfadeFrames = 1536; // ~32 ms at 48 kHz for clickless equal-power transitions

    readonly object renderLock = new();

    // Unified 6-slot backbone & stage processors
    readonly SpatialSceneHub sceneHub = new();
    readonly ManualHeadTrackerProvider manualHeadTracker = new();
    readonly ParametricUpmixerStageProcessor parametricUpmixerStage = new(new UpmixingSettings(true));
    readonly NeuralStemStageProcessor neuralStemStage = new();
    readonly LgsrStageProcessor lgsrStage = new();
    readonly VocalMixMatchStageProcessor vocalMixMatchStage = new();
    readonly CavernVirtualizerRendererAdapter virtualizerRenderer = new();
    readonly AutoEqPostProcessingStage autoEqPostStage = new();
    readonly HptfNotchPostProcessingStage hptfNotchPostStage = new();
    readonly Iso226LoudnessPostProcessingStage iso226PostStage = new();
    readonly Bs1770LoudnessPostProcessingStage bs1770PostStage = new();
    readonly SpatialDialogPostProcessingStage spatialDialogPostStage = new();
    readonly NormalizerPostProcessingAdapter limiterPostStage = new(true);
    readonly TruePeakLimiterPostProcessingStage truePeakLimiterPostStage = new();
    readonly LiveMicrophoneBridge liveMicBridge = new();
    readonly SpatialSceneFrame activeSceneFrame = new();

    // Zero-GC sub-block staging buffer enabling exact 64-frame / 128-frame Path B/A pro-audio bursts
    // over Cavern's 256-sample spatial update rate without stalls or allocations.
    readonly float[] subBlockStaging = new float[updateRate * 2];
    int subBlockReadOffset;
    int subBlockAvailableFrames;

    CavernizeTrack currentTrack;
    UpmixingSettings upmixingSettings = new(true);
    RenderingSettings renderingSettings = new();
    Listener listener;
    bool virtualizerInitialized;
    int appliedProfileVersion = -1;

    // Clickless crossfade state (prevents pops/clicks on profile switch, toggle, seek, filter change, or variant hot-swap)
    readonly float[] lastOutputRing = new float[crossfadeFrames * 2];
    int lastOutputRingWrite;
    bool hasRenderedAudio;
    readonly float[] fadeOutSnapshot = new float[crossfadeFrames * 2];
    int crossfadeRemaining;
    int crossfadeTotal;
    long lastSourceMaskHash;
    bool lastDynamicCeilingState;
    Source[] cachedActiveSources = Array.Empty<Source>();
    float[] cachedBaseSourceVolumes = Array.Empty<float>();
    Source[] liveNeuralSources = Array.Empty<Source>();
    bool isRenderingLiveNeuralSources;
    NeuralSeparatedTrack cachedHybridTrack;
    float[][] cachedHybrid714Buses;
    readonly CompositePreHrtfBusProcessor compositePreHrtfBus = new();
    int activeNativeSampleRateRequest;
    PhysicalExternalizationConfig externalizationConfig = PhysicalExternalizationConfig.Default;
    HrtfPersonalizationConfig personalizationConfig = HrtfPersonalizationConfig.Default;
    PsychoacousticLoudnessConfig loudnessConfig = PsychoacousticLoudnessConfig.Default;
    ProAudioDuplexConfig proAudioConfig = ProAudioDuplexConfig.Default;
    bool lastExternalizationBrirState;
    Vector3 lastExternalizationEnvSize;

    sealed class CompositePreHrtfBusProcessor : IPreHrtfBusProcessor {
        public IPreHrtfBusProcessor First;
        public IPreHrtfBusProcessor Second;

        public void ProcessPreHrtfBus(
            float[][] virtualChannels,
            VirtualChannel[] channelLayout,
            int channelCount,
            int blockSize,
            int sampleRate) {
            First?.ProcessPreHrtfBus(virtualChannels, channelLayout, channelCount, blockSize, sampleRate);
            Second?.ProcessPreHrtfBus(virtualChannels, channelLayout, channelCount, blockSize, sampleRate);
        }
    }

    /// <summary>
    /// Creates a real-time spatial audio engine wired with Slot 1–6 processors.
    /// </summary>
    public RealtimePlayerEngine() {
        sceneHub.BindHeadTracker(manualHeadTracker);
        sceneHub.BindRenderer(virtualizerRenderer);
        sceneHub.BindLiveMicrophone(liveMicBridge);
        sceneHub.RegisterStageProcessor(parametricUpmixerStage);
        sceneHub.RegisterStageProcessor(neuralStemStage);
        sceneHub.RegisterStageProcessor(lgsrStage);
        sceneHub.RegisterStageProcessor(vocalMixMatchStage);
        sceneHub.RegisterPostProcessingStage(autoEqPostStage);
        sceneHub.RegisterPostProcessingStage(hptfNotchPostStage);
        sceneHub.RegisterPostProcessingStage(iso226PostStage);
        sceneHub.RegisterPostProcessingStage(bs1770PostStage);
        sceneHub.RegisterPostProcessingStage(spatialDialogPostStage);
        sceneHub.RegisterPostProcessingStage(limiterPostStage);
        sceneHub.RegisterPostProcessingStage(truePeakLimiterPostStage);
        AcousticPropagationDelayLine.GlobalEnabled = true;
        sceneHub.PropagationDelay6DoFEnabled = true;
    }

    /// <summary>
    /// Slot 3 unified scene hub exposing active sources, 6DoF listener pose, programme metadata, telemetry,
    /// and extensible Slot 2 / Slot 4 / Slot 5 / Slot 6 pipeline registrations.
    /// </summary>
    public SpatialSceneHub SceneHub => sceneHub;

    /// <summary>
    /// Slot 1 zero-GC live microphone bridge (W10 Path B <c>libusb</c> UAC2 / Path A <c>AAudio/Oboe</c> Exclusive).
    /// </summary>
    public LiveMicrophoneBridge LiveMicBridge => liveMicBridge;

    /// <summary>
    /// Active Slot 1 / Slot 4 Pro-Audio full-duplex USB configuration.
    /// </summary>
    public ProAudioDuplexConfig ProAudioConfig => proAudioConfig;

    /// <summary>
    /// Live Slot 1 / Slot 4 Pro-Audio full-duplex USB &amp; Scarlett 2i2 telemetry snapshot.
    /// </summary>
    public ProAudioDuplexDiagnostics ProAudioDiagnostics => sceneHub.ProAudioDiagnostics;

    /// <summary>
    /// True when live microphone monitoring or MLS loopback RTT calibration requires the audio engine to remain active even while track playback is paused.
    /// </summary>
    public bool IsLiveMonitoringOrCalibrating =>
        (proAudioConfig.Enabled && proAudioConfig.LiveMicEnabled && proAudioConfig.MicRouting != LiveMicRoutingMode.Off) ||
        liveMicBridge.IsCalibratingLatency;

    /// <summary>
    /// Applies updated W10 Pro-Audio full-duplex &amp; live microphone parameters cleanly in real time.
    /// </summary>
    public void ApplyProAudioConfig(ProAudioDuplexConfig config) {
        ProAudioDuplexConfig safe = (config ?? ProAudioDuplexConfig.Default).Sanitize();
        lock (renderLock) {
            proAudioConfig = safe;
            liveMicBridge.ApplyConfig(safe, OutputSampleRate, EnvironmentSize);
            if (safe.Enabled && safe.LiveMicEnabled && safe.MicRouting != LiveMicRoutingMode.Off) {
                sceneHub.SetLiveMicrophoneDescriptor(liveMicBridge.BuildSceneDescriptor(EnvironmentSize, sceneHub.Sources.Count));
            } else {
                sceneHub.SetLiveMicrophoneDescriptor(null);
            }
        }
    }

    /// <summary>
    /// Arms a pure-physics MLS loopback round-trip latency calibration measurement on the active full-duplex stream.
    /// </summary>
    public void StartRoundTripLatencyCalibration(Action<Cavern.ProAudio.RoundTripLatencyResult> onCompleted) {
        liveMicBridge.StartRoundTripCalibration(OutputSampleRate, onCompleted);
    }

    /// <summary>
    /// Cancels any active MLS loopback round-trip latency calibration measurement.
    /// </summary>
    public void CancelRoundTripLatencyCalibration() {
        liveMicBridge.CancelRoundTripCalibration();
    }

    /// <summary>
    /// Slot 2B Neural Audio, MVSEP anti-artifact &amp; Spatial Karaoke stage processor (Priority 300).
    /// </summary>
    public NeuralStemStageProcessor NeuralStage => neuralStemStage;

    /// <summary>
    /// Active Slot 2B neural audio &amp; Spatial Karaoke configuration.
    /// </summary>
    public NeuralAudioConfig NeuralConfig => neuralStemStage.Config;

    /// <summary>
    /// Live Slot 2B neural audio, runtime EP, model cache, and Spatial Karaoke telemetry snapshot.
    /// </summary>
    public NeuralAudioDiagnostics NeuralDiagnostics => sceneHub.NeuralDiagnostics;

    /// <summary>
    /// Fired whenever <see cref="SourceItems"/> are rebuilt (for example when binding live 3D neural stems
    /// or switching <see cref="NeuralSpatialUpmixMode"/>).
    /// </summary>
    public event Action SourcesRebuilt;

    /// <summary>
    /// True when the active <see cref="Listener"/> is rendering live separated neural PCM stems as 3D objects
    /// or Hybrid Neural+DirAC 7.1.4 virtual feeds.
    /// </summary>
    public bool IsLiveNeuralStemPlaybackActive {
        get {
            lock (renderLock) {
                return isRenderingLiveNeuralSources;
            }
        }
    }

    /// <summary>
    /// Applies updated Slot 2A upmixing settings (<see cref="UpmixingSettings.ParametricStft"/>,
    /// <see cref="UpmixingSettings.AtmosAware"/>, Matrix, Cavernize) cleanly in real time with clickless crossfade.
    /// </summary>
    public void ApplyUpmixingSettings(UpmixingSettings settings) {
        UpmixingSettings safe = settings ?? new UpmixingSettings(true);
        bool rebuilt = false;
        lock (renderLock) {
            upmixingSettings = safe;
            parametricUpmixerStage.UpdateSettings(safe);
            if (currentTrack != null && !isRenderingLiveNeuralSources) {
                if (IsPlaying && hasRenderedAudio) {
                    ArmCrossfadeLocked();
                }
                currentTrack.Seek(currentTrackSample);
                ConfigureListener(resetToStart: false);
                sourceItems = TrackSourceItem.BuildFromTrack(currentTrack);
                BindTrackToSceneHubLocked(currentTrack);
                rebuilt = true;
            }
        }
        if (rebuilt) {
            SourcesRebuilt?.Invoke();
        }
    }

    /// <summary>
    /// Applies updated Slot 2B neural audio &amp; Spatial Karaoke parameters cleanly in real time.
    /// Switching <see cref="NeuralSpatialUpmixMode"/> when separated stems are bound seamlessly hot-swaps
    /// live 3D neural stem sources into <see cref="Listener"/> with a clickless crossfade.
    /// </summary>
    public void ApplyNeuralConfig(NeuralAudioConfig config) {
        bool rebuilt = false;
        lock (renderLock) {
            bool wasActive = neuralStemStage.IsKaraokeActivelyAttenuating;
            NeuralSpatialUpmixMode prevMode = neuralStemStage.Config.SpatialUpmixMode;
            neuralStemStage.ApplyConfig(config);
            bool nowActive = neuralStemStage.IsKaraokeActivelyAttenuating;
            NeuralSpatialUpmixMode nextMode = neuralStemStage.Config.SpatialUpmixMode;

            if (prevMode != nextMode && currentTrack != null && neuralStemStage.HasPlayableSeparatedStems) {
                if (IsPlaying && hasRenderedAudio) {
                    ArmCrossfadeLocked();
                }
                ConfigureListener(resetToStart: false);
                rebuilt = true;
            } else if (wasActive != nowActive && IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
        }
        if (rebuilt) {
            SourcesRebuilt?.Invoke();
        }
    }

    /// <summary>
    /// Smoothly updates live Spatial Karaoke vocal level in [0; 1] (0% = instrumental, 100% = bit-exact unity bypass).
    /// </summary>
    public void SetKaraokeVocalLevel(float level01) {
        lock (renderLock) {
            neuralStemStage.SetKaraokeVocalLevel(level01);
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
        }
    }

    /// <summary>
    /// Binds a pre-separated neural stem bundle (including Lead/Backing vocal references and PCM stem buffers)
    /// to the active playback scene. When <see cref="NeuralAudioConfig.SpatialUpmixMode"/> is
    /// <see cref="NeuralSpatialUpmixMode.Stems3DObjects"/> or <see cref="NeuralSpatialUpmixMode.HybridNeuralDirAc"/>,
    /// attaches live 3D PCM <see cref="Source"/>s directly to <see cref="Listener"/> synced to the current playhead.
    /// </summary>
    public void BindSeparatedNeuralTrack(NeuralSeparatedTrack separatedTrack) {
        bool rebuilt = false;
        lock (renderLock) {
            neuralStemStage.BindSeparatedTrack(separatedTrack);
            if (!ReferenceEquals(cachedHybridTrack, separatedTrack)) {
                cachedHybridTrack = null;
                cachedHybrid714Buses = null;
            }

            if (currentTrack != null && ShouldRenderLiveNeuralSourcesLocked()) {
                if (IsPlaying && hasRenderedAudio) {
                    ArmCrossfadeLocked();
                }
                ConfigureListener(resetToStart: false);
                rebuilt = true;
            } else if (isRenderingLiveNeuralSources && currentTrack != null) {
                if (IsPlaying && hasRenderedAudio) {
                    ArmCrossfadeLocked();
                }
                ConfigureListener(resetToStart: false);
                rebuilt = true;
            } else {
                SpatialSourceDescriptor[] descriptors = separatedTrack != null
                    ? neuralStemStage.BuildActiveStemDescriptors(EnvironmentSize, sceneHub.Sources.Count)
                    : Array.Empty<SpatialSourceDescriptor>();
                sceneHub.RegisterNeuralStemDescriptors(descriptors);
            }
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
        }
        if (rebuilt) {
            SourcesRebuilt?.Invoke();
        }
    }

    /// <summary>
    /// Binds an optional W09 lossless reference twin provider for Slot 2C LGSR Pre-HRTF restoration,
    /// sample-aligned Spatial Karaoke acceleration, Clean Stems, and telemetry.
    /// </summary>
    public void BindLosslessReference(ILosslessReferenceProvider provider) {
        lock (renderLock) {
            bool wasActive = lgsrStage.IsActivelyRestoring;
            neuralStemStage.BindLosslessReference(provider);
            lgsrStage.BindLosslessReference(provider);
            sceneHub.BindLosslessReference(provider);
            bool nowActive = lgsrStage.IsActivelyRestoring;
            if (wasActive != nowActive && IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
        }
    }

    /// <summary>
    /// Slot 2C Lossless-Guided Atmos Restoration (<c>LGSR</c>) Pre-HRTF stage processor (Priority 400).
    /// </summary>
    public LgsrStageProcessor LgsrStage => lgsrStage;

    /// <summary>
    /// Active Slot 2C LGSR configuration.
    /// </summary>
    public LgsrConfig LgsrConfig => lgsrStage.Config;

    /// <summary>
    /// Live Slot 2C LGSR telemetry snapshot (twin state, sub-sample offset, ppm drift, coherence, coupling/SPX Hz).
    /// </summary>
    public LgsrDiagnostics LgsrDiagnostics => sceneHub.LgsrDiagnostics;

    /// <summary>
    /// Slot 2D Auto-Vocal Mix-Match &amp; Live Mastering Chain stage processor (Priority 450).
    /// </summary>
    public VocalMixMatchStageProcessor VocalMixMatchStage => vocalMixMatchStage;

    /// <summary>
    /// Enables or disables pure-physics 6DoF acoustic time-of-flight propagation delay (343 m/s) across all non-LFE sources.
    /// </summary>
    public bool PropagationDelay6DoFEnabled {
        get => AcousticPropagationDelayLine.GlobalEnabled;
        set {
            lock (renderLock) {
                if (AcousticPropagationDelayLine.GlobalEnabled != value) {
                    AcousticPropagationDelayLine.GlobalEnabled = value;
                    sceneHub.PropagationDelay6DoFEnabled = value;
                    if (IsPlaying && hasRenderedAudio) {
                        ArmCrossfadeLocked();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Instant A/B bypass for Slot 2C LGSR Lossless-Guided Atmos Restoration inside the W13 Spatial X-Ray HUD.
    /// </summary>
    public bool LgsrBypassed {
        get => lgsrBypassed;
        set {
            lock (renderLock) {
                if (lgsrBypassed != value) {
                    lgsrBypassed = value;
                    sceneHub.LgsrBypassed = value;
                    if (IsPlaying && hasRenderedAudio) {
                        ArmCrossfadeLocked();
                    }
                }
            }
        }
    }
    bool lgsrBypassed;

    /// <summary>
    /// Instant A/B bypass for Slot 2D Live Vocal Mastering Chain inside the W13 Spatial X-Ray HUD &amp; Sing dock.
    /// </summary>
    public bool VocalChainBypassed {
        get => vocalMixMatchStage.IsBypassed;
        set {
            lock (renderLock) {
                if (vocalMixMatchStage.IsBypassed != value) {
                    vocalMixMatchStage.IsBypassed = value;
                    sceneHub.VocalChainBypassed = value;
                    sceneHub.VocalChainDiagnostics = vocalMixMatchStage.Telemetry;
                }
            }
        }
    }

    /// <summary>
    /// Starts a guided 3-5 second live microphone voiceprint capture (lock-free on capture thread,
    /// analyzed off-thread via <see cref="VocalMixProfileExtractor.Analyze"/>).
    /// </summary>
    public void StartMicVoiceprintCalibration(float durationSeconds, Action<VoiceprintCaptureResult> onCompleted) {
        int sr = liveMicBridge.SampleRate >= 44100 ? liveMicBridge.SampleRate : OutputSampleRate;
        liveMicBridge.StartVoiceprintCapture(sr, durationSeconds, onCompleted);
    }

    /// <summary>Cancels any active guided microphone voiceprint capture.</summary>
    public void CancelMicVoiceprintCalibration() {
        liveMicBridge.CancelVoiceprintCapture();
    }

    /// <summary>
    /// Pins a draggable 3D object, neural stem, or live microphone to a custom normalized room position [-1; 1].
    /// </summary>
    public void SetSourceCustomPosition(TrackSourceItem item, Vector3 normalizedPosition) {
        if (item == null) {
            return;
        }
        Vector3 clamped = new(
            Math.Clamp(normalizedPosition.X, -1f, 1f),
            Math.Clamp(normalizedPosition.Y, -1f, 1f),
            Math.Clamp(normalizedPosition.Z, -1f, 1f));
        lock (renderLock) {
            item.HasCustomPosition = true;
            item.CustomNormalizedPosition = clamped;
            item.NormalizedPosition = clamped;
            if (item.Layer == SourceLayer.LiveMicrophone) {
                liveMicBridge.SetNormalizedPosition(clamped);
            }
            sceneHub.SetSourceCustomPosition(item.Index, clamped);
        }
    }

    /// <summary>
    /// Releases a single source's custom 3D pin so it returns to its native Atmos trajectory or stem default.
    /// </summary>
    public void ClearSourceCustomPosition(TrackSourceItem item) {
        if (item == null) {
            return;
        }
        lock (renderLock) {
            item.HasCustomPosition = false;
            item.CustomNormalizedPosition = Vector3.Zero;
            sceneHub.ClearSourceCustomPosition(item.Index);
        }
    }

    /// <summary>
    /// Resets all manually dragged 3D object / stem / live mic pins back to their native trajectories.
    /// </summary>
    public void ResetAllCustomPositions() {
        lock (renderLock) {
            for (int i = 0; i < sourceItems.Count; i++) {
                sourceItems[i].HasCustomPosition = false;
                sourceItems[i].CustomNormalizedPosition = Vector3.Zero;
            }
            liveMicBridge.SetNormalizedPosition(proAudioConfig.MicNormalizedPosition);
            sceneHub.ResetAllCustomPositions();
        }
    }

    /// <summary>
    /// Applies updated Slot 2C LGSR parameters cleanly in real time with clickless crossfade on activation toggle.
    /// </summary>
    public void ApplyLgsrConfig(LgsrConfig config) {
        lock (renderLock) {
            bool wasActive = lgsrStage.IsActivelyRestoring && !lgsrBypassed;
            lgsrStage.ApplyConfig(config);
            bool nowActive = lgsrStage.IsActivelyRestoring && !lgsrBypassed;
            if (wasActive != nowActive && IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
        }
    }

    /// <summary>
    /// Slot 6A neutral AutoEq parametric headphone compensation stage (Priority 100, before safety limiter).
    /// </summary>
    public AutoEqPostProcessingStage AutoEqStage => autoEqPostStage;

    /// <summary>
    /// Slot 6B/5B individual HpTF ear-canal resonance notch stage (Priority 110, 6–10 kHz Orfanidis notch).
    /// </summary>
    public HptfNotchPostProcessingStage HptfNotchStage => hptfNotchPostStage;

    /// <summary>
    /// Active Slot 5A physical externalization configuration (ISM Room + Near-Field DVF + ISO 9613-1).
    /// </summary>
    public PhysicalExternalizationConfig ExternalizationConfig => externalizationConfig;

    /// <summary>
    /// Live Slot 5A physical externalization telemetry snapshot (RT60, BRIR guard, near-field active state).
    /// </summary>
    public PhysicalExternalizationDiagnostics ExternalizationDiagnostics => sceneHub.ExternalizationDiagnostics;

    /// <summary>
    /// Active Slot 5B HRTF personalization configuration (MagLS Ambisonics, Biometric ITD, HpTF notch).
    /// </summary>
    public HrtfPersonalizationConfig PersonalizationConfig => personalizationConfig;

    /// <summary>
    /// Live Slot 5B HRTF personalization telemetry snapshot.
    /// </summary>
    public HrtfPersonalizationDiagnostics PersonalizationDiagnostics => sceneHub.PersonalizationDiagnostics;

    /// <summary>
    /// Applies updated Slot 5A physical externalization parameters with clickless crossfade on structural toggles.
    /// </summary>
    public void ApplyExternalizationConfig(PhysicalExternalizationConfig config) {
        lock (renderLock) {
            bool structuralChange =
                externalizationConfig.IsmRoomEnabled != config.IsmRoomEnabled ||
                externalizationConfig.RoomPreset != config.RoomPreset ||
                externalizationConfig.WallMaterial != config.WallMaterial ||
                externalizationConfig.ReflectionOrder != config.ReflectionOrder;
            externalizationConfig = config;
            if (structuralChange && IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            SyncExternalizationLocked();
        }
    }

    /// <summary>
    /// Slot 6B ISO 226:2023 Dynamic Equal-Loudness physiological compensation stage (Priority 300).
    /// </summary>
    public Iso226LoudnessPostProcessingStage Iso226Stage => iso226PostStage;

    /// <summary>
    /// Slot 6B ITU-R BS.1770-5 / EBU R128 loudness meter &amp; Dolby Dialnorm normalization stage (Priority 400).
    /// </summary>
    public Bs1770LoudnessPostProcessingStage LoudnessMeterStage => bs1770PostStage;

    /// <summary>
    /// Slot 6B Spatial Dialog Enhancement stage (Priority 500; Path A pre-virtualizer + Path B LR4 Mid/Side fallback).
    /// </summary>
    public SpatialDialogPostProcessingStage SpatialDialogStage => spatialDialogPostStage;

    /// <summary>
    /// Slot 6B transparent 4x-oversampled True-Peak Lookahead Brickwall Limiter (Priority 900).
    /// </summary>
    public TruePeakLimiterPostProcessingStage TruePeakLimiterStage => truePeakLimiterPostStage;

    /// <summary>
    /// Active Slot 6B psychoacoustics &amp; studio loudness standards configuration.
    /// </summary>
    public PsychoacousticLoudnessConfig LoudnessConfig => loudnessConfig;

    /// <summary>
    /// Live Slot 6B loudness, ISO 226:2023, Spatial Dialog, and True-Peak limiter telemetry snapshot.
    /// </summary>
    public LoudnessStandardsDiagnostics LoudnessDiagnostics => sceneHub.LoudnessDiagnostics;

    /// <summary>
    /// Applies updated Slot 6B psychoacoustics &amp; studio loudness standards parameters cleanly in real time.
    /// </summary>
    public void ApplyLoudnessConfig(PsychoacousticLoudnessConfig config) {
        PsychoacousticLoudnessConfig safe = (config ?? PsychoacousticLoudnessConfig.Default).Sanitize();
        lock (renderLock) {
            loudnessConfig = safe;
            iso226PostStage.ApplyParameters(
                safe.Iso226Enabled,
                safe.Iso226ReferenceSplPhon,
                safe.Iso226ListeningSplPhon,
                safe.Iso226TrackVolume);
            iso226PostStage.UpdatePlaybackVolume(Volume);
            bs1770PostStage.ApplyParameters(safe.NormalizationMode, safe.RespectDolbyDialnorm);
            spatialDialogPostStage.ApplyParameters(safe.DialogEnhancementEnabled, safe.DialogEnhancementDb);
            truePeakLimiterPostStage.ApplyParameters(
                safe.TruePeakLimiterEnabled || (NormalizerEnabled && LimiterOnly),
                safe.TruePeakCeilingDbtp);
            PublishLoudnessDiagnosticsLocked();
        }
    }

    /// <summary>
    /// Resets the ITU-R BS.1770-5 Integrated LUFS / LRA / Max True-Peak accumulators without interrupting playback.
    /// </summary>
    public void ResetLoudnessMeter() {
        lock (renderLock) {
            bs1770PostStage.Reset();
            PublishLoudnessDiagnosticsLocked();
        }
    }

    /// <summary>
    /// Applies Slot 5B HpTF ear-canal notch parameters and synchronizes personalization state into <see cref="SceneHub"/>.
    /// </summary>
    public void ApplyPersonalizationConfig(HrtfPersonalizationConfig config) {
        lock (renderLock) {
            personalizationConfig = config;
            hptfNotchPostStage.ApplyParameters(
                config.HptfNotchEnabled,
                config.HptfNotchFreqHz,
                config.HptfNotchGainDb,
                config.HptfNotchQ);

            HrtfPersonalizationDiagnostics prev = sceneHub.PersonalizationDiagnostics;
            sceneHub.PersonalizationDiagnostics = new HrtfPersonalizationDiagnostics {
                IsMagLsActive = prev.IsMagLsActive,
                MagLsOrder = prev.MagLsOrder,
                MagLsCutoffHz = prev.MagLsCutoffHz,
                MagLsHfErrorDb = prev.MagLsHfErrorDb,
                IsBiometricItdActive = prev.IsBiometricItdActive,
                IsBrirBypassed = prev.IsBrirBypassed,
                HeadWidthCm = config.HeadWidthCm,
                MaxLateralItdUs = (float)BiometricItdScaler.ComputeWoodworthItdUs(90.0, 0.0, config.HeadWidthCm),
                ItdDeltaAt90DegUs = (float)BiometricItdScaler.ComputeItdDeltaUs(90.0, 0.0, config.HeadWidthCm, config.ManualItdOffsetUs),
                IsHptfNotchActive = hptfNotchPostStage.IsEnabled,
                HptfNotchFreqHz = hptfNotchPostStage.FrequencyHz,
                HptfNotchGainDb = hptfNotchPostStage.GainDb,
                HptfNotchQ = hptfNotchPostStage.Q,
                ActiveHrirFormat = prev.ActiveHrirFormat,
                ActiveClusterId = config.PerceptualClusterId ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Active Slot 4 head-tracking provider. Defaults to <see cref="ManualHeadTrackerProvider"/> synced with
    /// <see cref="ListenerPosition"/> and <see cref="ListenerRotation"/>.
    /// </summary>
    public IHeadTrackerProvider HeadTracker {
        get => sceneHub.ActiveHeadTracker ?? manualHeadTracker;
        set => sceneHub.BindHeadTracker(value ?? manualHeadTracker);
    }

    /// <summary>
    /// Fired when non-looping playback reaches the end of the track.
    /// </summary>
    public event Action PlaybackCompleted;

    /// <summary>
    /// Active list of channels (beds) and dynamic 3D objects in the loaded track.
    /// </summary>
    public IReadOnlyList<TrackSourceItem> SourceItems => sourceItems;
    List<TrackSourceItem> sourceItems = [];

    /// <summary>
    /// Active output sample rate in Hz (48000 Hz when headphone virtualization is enabled).
    /// </summary>
    public int OutputSampleRate { get; private set; } = Listener.DefaultSampleRate;

    /// <summary>
    /// Total length of the loaded track in seconds.
    /// </summary>
    public double TotalSeconds => currentTrack != null && currentTrack.SampleRate > 0
        ? currentTrack.Length / (double)currentTrack.SampleRate
        : 0;

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    public double CurrentSeconds => currentTrack != null && currentTrack.SampleRate > 0
        ? currentTrackSample / (double)currentTrack.SampleRate
        : 0;

    long currentTrackSample;

    /// <summary>
    /// True when playback is currently active.
    /// </summary>
    public bool IsPlaying { get; set; }

    /// <summary>
    /// Restart playback automatically when reaching the end of the track.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// Enable Cavern's HRTF headphone spatial virtualizer for stereo output.
    /// </summary>
    public bool HeadphoneVirtualizer { get; set; } = true;

    /// <summary>
    /// Listener position inside the simulated room (in Cavern room coordinates, relative to <see cref="EnvironmentSize"/>).
    /// </summary>
    public Vector3 ListenerPosition { get; set; } = Vector3.Zero;

    /// <summary>
    /// Listener head rotation in Euler angles (degrees: X=Pitch, Y=Yaw, Z=Roll).
    /// </summary>
    public Vector3 ListenerRotation { get; set; } = Vector3.Zero;

    /// <summary>
    /// Physical half-dimensions of the simulated listening room.
    /// </summary>
    public Vector3 EnvironmentSize { get; set; } = new Vector3(10, 7, 10);

    /// <summary>
    /// Room shape/rendering mode.
    /// </summary>
    public Environments EnvironmentType { get; set; } = Environments.Home;

    /// <summary>
    /// Quality mode for spatial resampling and rendering.
    /// </summary>
    public QualityModes AudioQuality { get; set; } = QualityModes.High;

    /// <summary>
    /// Apply Cavern's official HRTF ear-canal distance simulation (<see cref="Distancer"/>). Disabled by default for neutral playback.
    /// </summary>
    public bool DistanceSimulation { get; set; }

    /// <summary>
    /// Distance attenuation curve as listener moves relative to speakers/objects.
    /// </summary>
    public Rolloffs VolumeRolloff { get; set; } = Rolloffs.Disabled;

    /// <summary>
    /// Mute all fixed bed channels (render only dynamic 3D objects).
    /// </summary>
    public bool MuteBed { get; set; }

    /// <summary>
    /// Mute all ground-level sources (render only elevated height sources).
    /// </summary>
    public bool MuteGround { get; set; }

    /// <summary>
    /// Real-time dynamic ceiling filter: continuously follows each object's live Y elevation (&gt; 0.15) frame by frame.
    /// </summary>
    public bool DynamicCeilingOnly { get; set; }

    /// <summary>
    /// Mute all dynamic 3D objects (render only fixed bed channels).
    /// </summary>
    public bool OnlyBeds { get; set; }

    /// <summary>
    /// Master volume multiplier.
    /// </summary>
    public float Volume { get; set; } = 1f;

    /// <summary>
    /// LFE channel volume multiplier.
    /// </summary>
    public float LFEVolume { get; set; } = 1f;

    /// <summary>
    /// Enable Cavern's dynamic normalizer/limiter.
    /// </summary>
    public bool NormalizerEnabled { get; set; } = true;

    /// <summary>
    /// When normalizing, never boost volume above 100%.
    /// </summary>
    public bool LimiterOnly { get; set; } = true;

    /// <summary>
    /// Look-ahead prediction horizon (milliseconds) passed to the active Slot 4 <see cref="IHeadTrackerProvider"/>
    /// to compensate Bluetooth/USB/HAL audio buffer latency during head movement.
    /// </summary>
    public float HeadTrackingPredictionMs { get; set; } = 25f;

    /// <summary>
    /// Enables native high-resolution sample-rate pass-through (44.1..192 kHz) when outputting Direct 2.0 to a USB DAC.
    /// When headphone HRTF virtualization is active, output remains locked to <see cref="VirtualizerFilter.FilterSampleRate"/> (48 kHz)
    /// to preserve laboratory HRIR/BRIR measurement accuracy without resampling coloration.
    /// </summary>
    public bool UsbDacNativeSampleRateEnabled { get; set; } = true;

    /// <summary>
    /// Optional preferred hardware sample rate reported by an attached USB DAC (0 = automatic from track).
    /// </summary>
    public int PreferredHardwareSampleRate { get; set; }

    /// <summary>
    /// Updates the cached <see cref="PreferredHardwareSampleRate"/> negotiated with the hardware DAC without stopping playback.
    /// </summary>
    public void SetPreferredHardwareSampleRate(int sampleRate) {
        int clamped = sampleRate >= 44100 && sampleRate <= 192000 ? sampleRate : 0;
        PreferredHardwareSampleRate = clamped;
    }

    /// <summary>
    /// Requests a smooth clickless crossfade on the next rendered audio block.
    /// </summary>
    public void RequestSmoothTransition() {
        lock (renderLock) {
            ArmCrossfadeLocked();
        }
    }

    /// <summary>
    /// Loads a <see cref="CavernizeTrack"/> into the real-time spatial engine.
    /// </summary>
    public void Load(CavernizeTrack track, UpmixingSettings upmixing = null, RenderingSettings rendering = null) {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("Track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP real-time playback requires an external decoder (truehdd).");
        }

        lock (renderLock) {
            IsPlaying = false;
            currentTrack = track;
            upmixingSettings = upmixing ?? new UpmixingSettings(true);
            renderingSettings = rendering ?? new RenderingSettings();
            currentTrackSample = 0;
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            hasRenderedAudio = false;
            crossfadeRemaining = 0;
            lastSourceMaskHash = 0;

            if (neuralStemStage.ActiveSeparatedTrack != null &&
                !MatchesSeparatedTrack(neuralStemStage.ActiveSeparatedTrack, track)) {
                neuralStemStage.BindSeparatedTrack(null);
                cachedHybridTrack = null;
                cachedHybrid714Buses = null;
            }
            lgsrStage.ClearExplicitLosslessStatus();

            ConfigureListener(resetToStart: true);
            if (!isRenderingLiveNeuralSources) {
                sourceItems = TrackSourceItem.BuildFromTrack(track);
                BindTrackToSceneHubLocked(track);
            }
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
        }
    }

    /// <summary>
    /// Atomically swaps the playing track (e.g., switching Atmos ↔ FLAC ↔ Opus variant) at <paramref name="startSeconds"/>
    /// without stopping playback hardware, blending smoothly via cosine crossfade from the previous audio tail.
    /// </summary>
    public void HotSwapTrackSeamless(
        CavernizeTrack track,
        double startSeconds,
        UpmixingSettings upmixing = null,
        RenderingSettings rendering = null) {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("Track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP real-time playback requires an external decoder (truehdd).");
        }

        lock (renderLock) {
            bool wasPlaying = IsPlaying;
            if (wasPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }

            currentTrack = track;
            upmixingSettings = upmixing ?? new UpmixingSettings(true);
            renderingSettings = rendering ?? new RenderingSettings();

            double maxSec = track.SampleRate > 0 ? Math.Max(0, track.Length / (double)track.SampleRate - 0.1) : 0;
            double clampedSec = Math.Clamp(startSeconds, 0, maxSec);
            long targetSample = (long)Math.Round(clampedSec * track.SampleRate);
            currentTrackSample = track.Seek(targetSample);
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            lastSourceMaskHash = 0;

            if (neuralStemStage.ActiveSeparatedTrack != null &&
                !MatchesSeparatedTrack(neuralStemStage.ActiveSeparatedTrack, track)) {
                neuralStemStage.BindSeparatedTrack(null);
                cachedHybridTrack = null;
                cachedHybrid714Buses = null;
            }
            lgsrStage.ClearExplicitLosslessStatus();

            ConfigureListener(resetToStart: false);
            if (!isRenderingLiveNeuralSources) {
                sourceItems = TrackSourceItem.BuildFromTrack(track);
                BindTrackToSceneHubLocked(track);
            }
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
            IsPlaying = wasPlaying;
        }
    }

    /// <summary>
    /// Updates source items if Solo/Mute changed externally or refreshes active settings.
    /// </summary>
    public void ClearSoloMute() {
        lock (renderLock) {
            if (hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            DynamicCeilingOnly = false;
            MuteGround = false;
            MuteBed = false;
            OnlyBeds = false;
            foreach (TrackSourceItem item in sourceItems) {
                item.IsMuted = false;
                item.IsSolo = false;
                item.IsDynamicallyFilteredOut = false;
            }
            sceneHub.ClearSourceOverrides();
        }
    }

    /// <summary>
    /// Seeks playback to <paramref name="seconds"/> with a clickless crossfade transition.
    /// </summary>
    public void Seek(double seconds) {
        lock (renderLock) {
            if (currentTrack == null) {
                return;
            }
            if (IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            long targetSample = (long)Math.Round(seconds * currentTrack.SampleRate);
            currentTrackSample = currentTrack.Seek(targetSample);
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            ConfigureListener(resetToStart: false);
            sceneHub.ResetStatefulStages();
        }
    }

    /// <summary>
    /// Renders the next block of stereo interleaved float samples into <paramref name="stereoOutput"/>.
    /// Supports arbitrary pro-audio burst sizes (64, 128, 256, 1024 frames) via zero-GC sub-block staging,
    /// sample-locked W10 live microphone mixing, and MLS loopback latency calibration.
    /// Returns the number of stereo sample frames written (0 if stopped or finished).
    /// </summary>
    public int RenderNextInterleavedBlock(float[] stereoOutput, int maxStereoFrames) {
        if (stereoOutput == null || maxStereoFrames <= 0) {
            return 0;
        }

        bool reachedEnd = false;
        int totalFramesWritten = 0;

        lock (renderLock) {
            // Standalone live-microphone monitoring (with Slot 2D live vocal chain) or MLS RTT calibration when paused
            if (!IsPlaying || currentTrack == null || listener == null) {
                int safeFrames = Math.Min(maxStereoFrames, stereoOutput.Length / 2);
                Array.Clear(stereoOutput, 0, safeFrames * 2);
                if (liveMicBridge.IsCalibratingLatency || liveMicBridge.IsActive) {
                    vocalMixMatchStage.EnsureSampleRate(liveMicBridge.SampleRate);
                    liveMicBridge.MixIntoStereoOutputZeroGc(
                        stereoOutput,
                        0,
                        safeFrames,
                        sceneHub.ListenerPose,
                        EnvironmentSize,
                        vocalProcessor: liveMicBridge.IsCalibratingLatency ? null : vocalMixMatchStage,
                        applyBackingSidechain: false);
                    VocalChainTelemetry pausedVocalTel = vocalMixMatchStage.Telemetry;
                    SoundfieldTelemetry micTel = sceneHub.Telemetry;
                    micTel.LiveMicRmsDbfs = liveMicBridge.RmsDbfs;
                    micTel.LiveMicPeakDbfs = liveMicBridge.PeakDbfs;
                    micTel.FullDuplexRoundTripMs = liveMicBridge.RoundTripLatencyMs;
                    micTel.IsLiveMicActive = liveMicBridge.IsActive;
                    micTel.VocalChainInputDbfs = pausedVocalTel.InputDbfs;
                    micTel.VocalChainOutputDbfs = pausedVocalTel.OutputDbfs;
                    micTel.VocalChainGateReductionDb = pausedVocalTel.GateReductionDb;
                    micTel.VocalChainDeEsserReductionDb = pausedVocalTel.DeEsserReductionDb;
                    micTel.VocalChainCompressorReductionDb = pausedVocalTel.CompressorReductionDb;
                    micTel.VocalChainSidechainDuckDb = pausedVocalTel.SidechainPocketDuckDb;
                    micTel.VocalChainBypassed = vocalMixMatchStage.IsBypassed;
                    sceneHub.Telemetry = micTel;
                    sceneHub.VocalChainDiagnostics = pausedVocalTel;
                    return safeFrames;
                }
                vocalMixMatchStage.ClearTelemetryIfIdle();
                sceneHub.VocalChainDiagnostics = vocalMixMatchStage.Telemetry;
                return 0;
            }

            ApplyLiveParameters();

            // Drain any previously staged sub-block frames first (used when Path B/A requests 64f or 128f bursts)
            if (subBlockAvailableFrames > 0) {
                int take = Math.Min(subBlockAvailableFrames, maxStereoFrames);
                Array.Copy(subBlockStaging, subBlockReadOffset * 2, stereoOutput, 0, take * 2);
                subBlockReadOffset += take;
                subBlockAvailableFrames -= take;
                totalFramesWritten += take;
                if (totalFramesWritten >= maxStereoFrames) {
                    return totalFramesWritten;
                }
            }

            // If remaining request is smaller than updateRate (e.g., 64f/128f Path B/A burst),
            // render one 256-frame block into subBlockStaging and deliver exact requested frames.
            if (maxStereoFrames - totalFramesWritten < updateRate) {
                int staged = RenderAlignedBlocksLocked(subBlockStaging, 0, updateRate, ref reachedEnd);
                if (staged > 0) {
                    int take = Math.Min(staged, maxStereoFrames - totalFramesWritten);
                    Array.Copy(subBlockStaging, 0, stereoOutput, totalFramesWritten * 2, take * 2);
                    subBlockReadOffset = take;
                    subBlockAvailableFrames = staged - take;
                    totalFramesWritten += take;
                }
            } else {
                int alignedFrames = ((maxStereoFrames - totalFramesWritten) / updateRate) * updateRate;
                totalFramesWritten += RenderAlignedBlocksLocked(stereoOutput, totalFramesWritten * 2, alignedFrames, ref reachedEnd);
            }
        }

        if (reachedEnd) {
            PlaybackCompleted?.Invoke();
        }
        return totalFramesWritten;
    }

    int RenderAlignedBlocksLocked(float[] stereoOutput, int baseSampleOffset, int maxAlignedFrames, ref bool reachedEnd) {
        int totalFramesWritten = 0;

            while (totalFramesWritten + updateRate <= maxAlignedFrames) {
                if (currentTrack.Length > 0 && currentTrackSample >= currentTrack.Length) {
                    if (Loop) {
                        currentTrackSample = currentTrack.Seek(0);
                        ConfigureListener(resetToStart: false);
                        ApplyLiveParameters();
                    } else {
                        IsPlaying = false;
                        reachedEnd = true;
                        break;
                    }
                }

                int remainingFrames = maxAlignedFrames - totalFramesWritten;
                int chunks = Math.Min(framesPerChunk, remainingFrames / updateRate);
                if (chunks <= 0) {
                    chunks = 1;
                }

                ApplySourceMutesAndProperties();

                // Refresh live per-block E-AC-3 Coupling / SPX bitstream cutoffs prior to Slot 2 & Pre-HRTF LGSR
                if (currentTrack.Renderer is EnhancedAC3Renderer liveEac3) {
                    SpatialProgramMetadata liveMeta = sceneHub.ProgramMetadata;
                    liveMeta.CouplingStartHz = liveEac3.CouplingStartHz;
                    liveMeta.SpxStartHz = liveEac3.SpxStartHz;
                    sceneHub.ProgramMetadata = liveMeta;
                }

                int requestedFrames = chunks * updateRate;
                int outOffset = baseSampleOffset + totalFramesWritten * 2;
                activeSceneFrame.BeginBlock(
                    listener.SampleRate,
                    requestedFrames,
                    currentTrackSample,
                    Stopwatch.GetTimestamp(),
                    Listener.EnvironmentSize,
                    Listener.EnvironmentType,
                    HeadphoneVirtualizer,
                    sceneHub.ListenerPose,
                    sceneHub.ProgramMetadata,
                    sceneHub.Sources,
                    stereoOutput,
                    outOffset,
                    2);

                // Slot 2: Ordered spatial stage processors (W02 Parametric, W08 Neural, W09 LGSR)
                IReadOnlyList<ISpatialStageProcessor> stageProcessors = sceneHub.StageProcessors;
                for (int p = 0; p < stageProcessors.Count; p++) {
                    if (stageProcessors[p].IsEnabled) {
                        stageProcessors[p].Process(activeSceneFrame);
                    }
                }

                // Slot 5: Binaural / Stereo renderer (delegates to Cavern Listener + VirtualizerFilter)
                // Attach Slot 2C LGSR & Slot 2B Multichannel Spatial Karaoke PRE-HRTF on the virtual speaker bus
                // in Headphone Virtualizer mode (preserving LFE, 0.000° spatial phase, and 3D HRIR cues).
                bool lgsrActive = lgsrStage.IsActivelyRestoring && !lgsrBypassed;
                bool karaokeActive = neuralStemStage.IsKaraokeActiveOrRamping;
                if (HeadphoneVirtualizer && (lgsrActive || karaokeActive)) {
                    compositePreHrtfBus.First = lgsrActive ? lgsrStage : null;
                    compositePreHrtfBus.Second = karaokeActive ? neuralStemStage : null;
                    VirtualizerFilter.PreHrtfBusProcessor = compositePreHrtfBus;
                } else {
                    VirtualizerFilter.PreHrtfBusProcessor = null;
                }
                IBinauralRenderer renderer = sceneHub.ActiveRenderer ?? virtualizerRenderer;
                int blockFrames = renderer.RenderFrame(activeSceneFrame, stereoOutput, outOffset, requestedFrames);
                if (blockFrames <= 0) {
                    break;
                }

                // Direct 2.0 fallback for Slot 2C LGSR and Slot 2B Spatial Karaoke when Headphone Virtualizer is bypassed
                if (!HeadphoneVirtualizer && lgsrActive) {
                    lgsrStage.ProcessDirectStereoFallback(stereoOutput, outOffset, blockFrames, currentTrackSample);
                }
                if (!HeadphoneVirtualizer && karaokeActive) {
                    neuralStemStage.ProcessStereoInterleavedKaraoke(stereoOutput, outOffset, blockFrames, currentTrackSample);
                }

                if (neuralStemStage.IsKaraokeActivelyAttenuating) {
                    SoundfieldTelemetry kTel = activeSceneFrame.Telemetry;
                    kTel.VocalActivity = neuralStemStage.VocalActivity;
                    kTel.KaraokeVocalLevel = neuralStemStage.Config.KaraokeVocalLevel;
                    activeSceneFrame.Telemetry = kTel;
                    spatialDialogPostStage.ExternalSpeechActivity = neuralStemStage.VocalActivity;
                } else {
                    spatialDialogPostStage.ExternalSpeechActivity = float.NaN;
                }

                // Slot 1 / W10 Sample-Locked Live Microphone + Slot 2D Live Auto-Vocal Mix-Match Chain
                // Mixed right before Slot 6 4x True-Peak Limiter so live vocal + Karaoke + 1-4 kHz sidechain pocket never clip.
                if (liveMicBridge.IsActive || liveMicBridge.IsCalibratingLatency) {
                    liveMicBridge.MixIntoStereoOutputZeroGc(
                        stereoOutput,
                        outOffset,
                        blockFrames,
                        sceneHub.ListenerPose,
                        Listener.EnvironmentSize,
                        vocalProcessor: liveMicBridge.IsCalibratingLatency ? null : vocalMixMatchStage,
                        applyBackingSidechain: true);
                } else {
                    vocalMixMatchStage.ClearTelemetryIfIdle();
                }

                // Slot 6: Ordered transparent post-processing stages
                // (100 AutoEq, 110 HpTF, 300 ISO 226:2023, 400 BS.1770/Dialnorm, 500 Dialog Path B, 890 Legacy AGC, 900 4x True-Peak Limiter)
                iso226PostStage.UpdatePlaybackVolume(Volume);
                bool hasBs1770Norm = bs1770PostStage.NormalizationMode != LoudnessNormalizationMode.Off;
                limiterPostStage.LimiterOnly = LimiterOnly || hasBs1770Norm;
                limiterPostStage.IsEnabled = NormalizerEnabled && !LimiterOnly && !hasBs1770Norm;
                truePeakLimiterPostStage.IsEnabled = loudnessConfig.TruePeakLimiterEnabled || (NormalizerEnabled && LimiterOnly);
                truePeakLimiterPostStage.CeilingDbtp = loudnessConfig.TruePeakCeilingDbtp;
                IReadOnlyList<IPostProcessingStage> postStages = sceneHub.PostProcessingStages;
                for (int p = 0; p < postStages.Count; p++) {
                    if (postStages[p].IsEnabled) {
                        postStages[p].ProcessInterleaved(stereoOutput, outOffset, blockFrames, 2, activeSceneFrame);
                    }
                }

                if (crossfadeRemaining > 0 && crossfadeTotal > 0) {
                    int blendCount = Math.Min(blockFrames, crossfadeRemaining);
                    int doneSoFar = crossfadeTotal - crossfadeRemaining;
                    for (int s = 0; s < blendCount; s++) {
                        int step = doneSoFar + s;
                        float t = (step + 1f) / crossfadeTotal;
                        float gainIn = 0.5f * (1f - MathF.Cos(MathF.PI * t));
                        float gainOut = 1f - gainIn;
                        int snapIdx = step * 2;
                        int dstIdx = outOffset + s * 2;
                        stereoOutput[dstIdx] = stereoOutput[dstIdx] * gainIn + fadeOutSnapshot[snapIdx] * gainOut;
                        stereoOutput[dstIdx + 1] = stereoOutput[dstIdx + 1] * gainIn + fadeOutSnapshot[snapIdx + 1] * gainOut;
                    }
                    crossfadeRemaining -= blendCount;
                }

                float blockPeak = 0f;
                for (int i = 0; i < blockFrames * 2; i++) {
                    float clamped = Math.Clamp(stereoOutput[outOffset + i], -1f, 1f);
                    stereoOutput[outOffset + i] = clamped;
                    float abs = MathF.Abs(clamped);
                    if (abs > blockPeak) {
                        blockPeak = abs;
                    }
                }

                // Preserve 4x-oversampled dBTP measured by BS.1770 stage; only fall back to sample peak if meter is disabled
                SoundfieldTelemetry blockTelemetry = activeSceneFrame.Telemetry;
                if (!bs1770PostStage.IsEnabled) {
                    blockTelemetry.TruePeakDbfs = blockPeak > 1e-6f ? 20f * MathF.Log10(blockPeak) : -120f;
                }
                blockTelemetry.LiveMicRmsDbfs = liveMicBridge.RmsDbfs;
                blockTelemetry.LiveMicPeakDbfs = liveMicBridge.PeakDbfs;
                blockTelemetry.FullDuplexRoundTripMs = liveMicBridge.RoundTripLatencyMs;
                blockTelemetry.IsLiveMicActive = liveMicBridge.IsActive;
                VocalChainTelemetry vocalTel = vocalMixMatchStage.Telemetry;
                blockTelemetry.VocalChainInputDbfs = vocalTel.InputDbfs;
                blockTelemetry.VocalChainOutputDbfs = vocalTel.OutputDbfs;
                blockTelemetry.VocalChainGateReductionDb = vocalTel.GateReductionDb;
                blockTelemetry.VocalChainDeEsserReductionDb = vocalTel.DeEsserReductionDb;
                blockTelemetry.VocalChainCompressorReductionDb = vocalTel.CompressorReductionDb;
                blockTelemetry.VocalChainSidechainDuckDb = vocalTel.SidechainPocketDuckDb;
                blockTelemetry.VocalChainBypassed = vocalMixMatchStage.IsBypassed;
                activeSceneFrame.Telemetry = blockTelemetry;

                RecordTailLocked(stereoOutput, outOffset, blockFrames);

                double sampleAdvance = blockFrames * (currentTrack.SampleRate / (double)listener.SampleRate);
                currentTrackSample += (long)Math.Round(sampleAdvance);
                totalFramesWritten += blockFrames;

                UpdateSourceTelemetry();
                if (liveMicBridge.IsActive) {
                    sceneHub.SetLiveMicrophoneDescriptor(liveMicBridge.BuildSceneDescriptor(Listener.EnvironmentSize, sceneHub.Sources.Count));
                }
                parametricUpmixerStage.SyncTelemetry(activeSceneFrame);
                sceneHub.Telemetry = activeSceneFrame.Telemetry;
                sceneHub.VocalChainDiagnostics = vocalTel;
                sceneHub.ExternalizationDiagnostics = virtualizerRenderer.Diagnostics;
                PublishLoudnessDiagnosticsLocked();
                sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
                sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
            }
        return totalFramesWritten;
    }

    void RecordTailLocked(float[] stereoOutput, int outOffset, int blockFrames) {
        for (int s = 0; s < blockFrames; s++) {
            int dst = lastOutputRingWrite * 2;
            lastOutputRing[dst] = stereoOutput[outOffset + s * 2];
            lastOutputRing[dst + 1] = stereoOutput[outOffset + s * 2 + 1];
            lastOutputRingWrite++;
            if (lastOutputRingWrite >= crossfadeFrames) {
                lastOutputRingWrite = 0;
            }
        }
        hasRenderedAudio = true;
    }

    void ArmCrossfadeLocked() {
        if (!hasRenderedAudio) {
            return;
        }
        for (int s = 0; s < crossfadeFrames; s++) {
            int ringIdx = lastOutputRingWrite - crossfadeFrames + s;
            if (ringIdx < 0) {
                ringIdx += crossfadeFrames;
            }
            float decay = 1f - (float)s / crossfadeFrames;
            fadeOutSnapshot[s * 2] = lastOutputRing[ringIdx * 2] * decay;
            fadeOutSnapshot[s * 2 + 1] = lastOutputRing[ringIdx * 2 + 1] * decay;
        }
        crossfadeTotal = crossfadeFrames;
        crossfadeRemaining = crossfadeFrames;
    }

    static bool MatchesSeparatedTrack(NeuralSeparatedTrack sep, CavernizeTrack track) {
        if (sep == null || track == null || string.IsNullOrEmpty(sep.SourceTrackKey)) {
            return false;
        }
        return string.Equals(sep.SourceTrackKey, track.Path, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sep.SourceTrackKey, track.OriginalSourcePath, StringComparison.OrdinalIgnoreCase);
    }

    bool ShouldRenderLiveNeuralSourcesLocked() {
        NeuralSpatialUpmixMode mode = neuralStemStage.Config.SpatialUpmixMode;
        if (mode != NeuralSpatialUpmixMode.Stems3DObjects && mode != NeuralSpatialUpmixMode.HybridNeuralDirAc) {
            return false;
        }
        NeuralSeparatedTrack sep = neuralStemStage.ActiveSeparatedTrack;
        return sep != null && sep.Stems != null && sep.Stems.Count > 0;
    }

    void ConfigureListener(bool resetToStart) {
        Listener.EnvironmentType = EnvironmentType;
        Listener.EnvironmentSize = EnvironmentSize;
        Listener.HeadphoneVirtualizer = false;

        if (renderingSettings.RoomCorrectionUsable && !HeadphoneVirtualizer) {
            listener = new ConvolvedListener(false) {
                ConvolutionClip = renderingSettings.RoomCorrection,
                UpdateRate = updateRate,
                AudioQuality = AudioQuality,
                MaximumSources = 256
            };
        } else {
            listener = new Listener(false) {
                UpdateRate = updateRate,
                AudioQuality = AudioQuality,
                MaximumSources = 256
            };
        }

        if (ShouldRenderLiveNeuralSourcesLocked()) {
            AttachLiveNeuralSourcesLocked(resetToStart);
            parametricUpmixerStage.BindTelemetryProvider(null);
        } else {
            bool wasRenderingLiveNeural = isRenderingLiveNeuralSources;
            isRenderingLiveNeuralSources = false;
            liveNeuralSources = Array.Empty<Source>();
            if (resetToStart) {
                currentTrack.Attach(listener, upmixingSettings);
            } else {
                if (wasRenderingLiveNeural) {
                    currentTrack.Seek(currentTrackSample);
                }
                currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
            }
            if (wasRenderingLiveNeural) {
                sourceItems = TrackSourceItem.BuildFromTrack(currentTrack);
                BindTrackToSceneHubLocked(currentTrack);
                SpatialSourceDescriptor[] stemDescs = neuralStemStage.ActiveSeparatedTrack != null
                    ? neuralStemStage.BuildActiveStemDescriptors(EnvironmentSize, sceneHub.Sources.Count)
                    : Array.Empty<SpatialSourceDescriptor>();
                sceneHub.RegisterNeuralStemDescriptors(stemDescs);
            }
            parametricUpmixerStage.BindTelemetryProvider(currentTrack.ActiveTelemetryProvider);
        }

        RebuildCachedActiveSourcesLocked();
        SetupOutputLayout();
    }

    void AttachLiveNeuralSourcesLocked(bool resetToStart) {
        NeuralSeparatedTrack sep = neuralStemStage.ActiveSeparatedTrack;
        NeuralSpatialUpmixMode mode = neuralStemStage.Config.SpatialUpmixMode;
        Vector3 envSize = EnvironmentSize.X > 0.1f && EnvironmentSize.Y > 0.1f && EnvironmentSize.Z > 0.1f
            ? EnvironmentSize
            : new Vector3(10f, 7f, 10f);

        // Preserve any existing user Solo/Mute/CustomPin overrides matched by source name across re-attaches
        Dictionary<string, TrackSourceItem> prevByName = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceItems.Count; i++) {
            prevByName[sourceItems[i].Name] = sourceItems[i];
        }

        int trackSr = currentTrack != null && currentTrack.SampleRate > 0 ? currentTrack.SampleRate : Listener.DefaultSampleRate;
        int stemSr = sep.SampleRate > 0 ? sep.SampleRate : trackSr;
        long startSample = resetToStart
            ? 0L
            : (long)Math.Round(currentTrackSample * (stemSr / (double)trackSr));

        List<Source> builtSources = [];
        List<TrackSourceItem> builtItems = [];
        List<SpatialSourceDescriptor> builtDescriptors = [];

        if (mode == NeuralSpatialUpmixMode.HybridNeuralDirAc) {
            if (!ReferenceEquals(cachedHybridTrack, sep) || cachedHybrid714Buses == null || cachedHybrid714Buses.Length != 11) {
                StereoStemBuffer[] stemArr = new StereoStemBuffer[sep.Stems.Count];
                for (int i = 0; i < sep.Stems.Count; i++) {
                    stemArr[i] = sep.Stems[i];
                }
                float heightExp = Math.Clamp(upmixingSettings.Effect * 0.5f, 0.10f, 0.65f);
                cachedHybrid714Buses = NeuralStemSpatializer.ProcessHybridNeuralDirAc714(stemArr, stemSr, heightExp);
                cachedHybridTrack = sep;
            }

            ReferenceChannel[] layout714 = [
                ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                ReferenceChannel.SideLeft, ReferenceChannel.SideRight, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight, ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
            ];
            for (int b = 0; b < cachedHybrid714Buses.Length && b < layout714.Length; b++) {
                float[] pcm = cachedHybrid714Buses[b];
                if (pcm == null || pcm.Length == 0) {
                    continue;
                }
                ReferenceChannel ch = layout714[b];
                Vector3 normPos = ChannelPrototype.AlternativePositions[(int)ch];
                bool isHeight = b >= 7;
                SourceLayer layer = isHeight ? SourceLayer.VirtualHeight : SourceLayer.NeuralStem;
                string label = $"AI Hybrid {ch.GetShortName()}";
                int idx = builtSources.Count;

                Source src = new() {
                    Clip = new Clip(new MultichannelWaveform(pcm), stemSr),
                    Position = normPos * envSize,
                    TimeSamples = (int)Math.Clamp(startSample, 0, Math.Max(0, pcm.Length - 1)),
                    Loop = Loop,
                    Volume = 1f
                };
                TrackSourceItem item = new(idx, label, isBed: false, isLfe: false, ch, normPos, layer);
                if (prevByName.TryGetValue(label, out TrackSourceItem old)) {
                    item.IsMuted = old.IsMuted;
                    item.IsSolo = old.IsSolo;
                    item.HasCustomPosition = old.HasCustomPosition;
                    item.CustomNormalizedPosition = old.CustomNormalizedPosition;
                    if (item.HasCustomPosition) {
                        src.Position = item.CustomNormalizedPosition * envSize;
                        item.NormalizedPosition = item.CustomNormalizedPosition;
                    }
                }
                builtSources.Add(src);
                builtItems.Add(item);
                builtDescriptors.Add(new SpatialSourceDescriptor {
                    Index = idx,
                    Name = label,
                    Layer = layer,
                    BedChannel = ch,
                    IsLfe = false,
                    IsDialogue = ch == ReferenceChannel.FrontCenter,
                    NormalizedPosition = item.NormalizedPosition,
                    WorldPositionMeters = item.NormalizedPosition * envSize,
                    HasCustomPosition = item.HasCustomPosition,
                    CustomNormalizedPosition = item.CustomNormalizedPosition,
                    Gain = 1f,
                    Diffuse = isHeight ? 0.45f : 0.15f,
                    Importance = ch == ReferenceChannel.FrontCenter ? 1f : 0.8f
                });
            }
        } else {
            // Stems3DObjects: place each separated neural stem directly as live 3D object Sources in Listener
            SpatialSourceDescriptor[] stemDescs = neuralStemStage.BuildActiveStemDescriptors(envSize, 0);
            int descCursor = 0;
            const float invSqrt2 = 0.70710678f;

            for (int s = 0; s < sep.Stems.Count; s++) {
                StereoStemBuffer stem = sep.Stems[s];
                if (stem == null || stem.Length <= 0) {
                    continue;
                }
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stem.Name);
                SourceLayer layer = role == NeuralStemRole.Residual ? SourceLayer.RestoredResidual : SourceLayer.NeuralStem;

                if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass) {
                    SpatialSourceDescriptor d = descCursor < stemDescs.Length
                        ? stemDescs[descCursor++]
                        : new SpatialSourceDescriptor {
                            Name = $"AI {stem.Name}",
                            Layer = layer,
                            IsDialogue = role != NeuralStemRole.Bass,
                            NormalizedPosition = new Vector3(0f, role == NeuralStemRole.Bass ? -0.15f : 0.08f, 1f)
                        };

                    float[] mono = new float[stem.Length];
                    for (int n = 0; n < stem.Length; n++) {
                        mono[n] = (stem.Left[n] + stem.Right[n]) * invSqrt2;
                    }

                    int idx = builtSources.Count;
                    Source src = new() {
                        Clip = new Clip(new MultichannelWaveform(mono), stemSr),
                        Position = d.NormalizedPosition * envSize,
                        TimeSamples = (int)Math.Clamp(startSample, 0, Math.Max(0, mono.Length - 1)),
                        Loop = Loop,
                        Volume = 1f
                    };
                    TrackSourceItem item = new(idx, d.Name, isBed: false, isLfe: false, null, d.NormalizedPosition, layer);
                    if (prevByName.TryGetValue(d.Name, out TrackSourceItem old)) {
                        item.IsMuted = old.IsMuted;
                        item.IsSolo = old.IsSolo;
                        item.HasCustomPosition = old.HasCustomPosition;
                        item.CustomNormalizedPosition = old.CustomNormalizedPosition;
                        if (item.HasCustomPosition) {
                            src.Position = item.CustomNormalizedPosition * envSize;
                            item.NormalizedPosition = item.CustomNormalizedPosition;
                        }
                    }
                    d.Index = idx;
                    d.HasCustomPosition = item.HasCustomPosition;
                    d.CustomNormalizedPosition = item.CustomNormalizedPosition;
                    d.NormalizedPosition = item.NormalizedPosition;
                    d.WorldPositionMeters = item.NormalizedPosition * envSize;
                    builtSources.Add(src);
                    builtItems.Add(item);
                    builtDescriptors.Add(d);
                } else {
                    for (int side = 0; side < 2; side++) {
                        SpatialSourceDescriptor d = descCursor < stemDescs.Length
                            ? stemDescs[descCursor++]
                            : new SpatialSourceDescriptor {
                                Name = $"AI {stem.Name} {(side == 0 ? "L" : "R")}",
                                Layer = layer,
                                NormalizedPosition = new Vector3(side == 0 ? -0.75f : 0.75f, 0.2f, 0.4f)
                            };
                        float[] pcm = side == 0 ? stem.Left : stem.Right;
                        int idx = builtSources.Count;
                        Source src = new() {
                            Clip = new Clip(new MultichannelWaveform(pcm), stemSr),
                            Position = d.NormalizedPosition * envSize,
                            TimeSamples = (int)Math.Clamp(startSample, 0, Math.Max(0, pcm.Length - 1)),
                            Loop = Loop,
                            Volume = 1f
                        };
                        TrackSourceItem item = new(idx, d.Name, isBed: false, isLfe: false, null, d.NormalizedPosition, layer);
                        if (prevByName.TryGetValue(d.Name, out TrackSourceItem old)) {
                            item.IsMuted = old.IsMuted;
                            item.IsSolo = old.IsSolo;
                            item.HasCustomPosition = old.HasCustomPosition;
                            item.CustomNormalizedPosition = old.CustomNormalizedPosition;
                            if (item.HasCustomPosition) {
                                src.Position = item.CustomNormalizedPosition * envSize;
                                item.NormalizedPosition = item.CustomNormalizedPosition;
                            }
                        }
                        d.Index = idx;
                        d.HasCustomPosition = item.HasCustomPosition;
                        d.CustomNormalizedPosition = item.CustomNormalizedPosition;
                        d.NormalizedPosition = item.NormalizedPosition;
                        d.WorldPositionMeters = item.NormalizedPosition * envSize;
                        builtSources.Add(src);
                        builtItems.Add(item);
                        builtDescriptors.Add(d);
                    }
                }
            }
        }

        if (builtSources.Count == 0) {
            isRenderingLiveNeuralSources = false;
            liveNeuralSources = Array.Empty<Source>();
            if (resetToStart) {
                currentTrack.Attach(listener, upmixingSettings);
            } else {
                currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
            }
            return;
        }

        liveNeuralSources = [.. builtSources];
        isRenderingLiveNeuralSources = true;
        listener.SampleRate = stemSr;
        listener.LFESeparation = true;
        listener.AttachSources(liveNeuralSources);

        sourceItems = builtItems;
        CavernTrackDecoderAdapter decoderAdapter = new(currentTrack, sourceItems);
        sceneHub.BindDecoder(decoderAdapter, builtDescriptors);
        sceneHub.ConfigureStageProcessors(OutputSampleRate, decoderAdapter.Metadata);
        sceneHub.ConfigurePostProcessingStages(OutputSampleRate, 2);
    }

    void RebuildCachedActiveSourcesLocked() {
        if (listener == null || listener.ActiveSources.Count == 0) {
            cachedActiveSources = Array.Empty<Source>();
            cachedBaseSourceVolumes = Array.Empty<float>();
            return;
        }
        Source[] arr = new Source[listener.ActiveSources.Count];
        float[] baseVols = new float[listener.ActiveSources.Count];
        int i = 0;
        foreach (Source s in listener.ActiveSources) {
            arr[i] = s;
            baseVols[i] = s.Volume;
            i++;
        }
        cachedActiveSources = arr;
        cachedBaseSourceVolumes = baseVols;
    }

    void PublishLoudnessDiagnosticsLocked() {
        LoudnessAnalysisSnapshot snap = bs1770PostStage.Snapshot;
        float postTp = snap.TruePeakDbtp + bs1770PostStage.AppliedGainDb;
        if (truePeakLimiterPostStage.IsEnabled && postTp > truePeakLimiterPostStage.CeilingDbtp) {
            postTp = truePeakLimiterPostStage.CeilingDbtp;
        }
        sceneHub.LoudnessDiagnostics = new LoudnessStandardsDiagnostics {
            MomentaryLufs = snap.MomentaryLufs,
            ShortTermLufs = snap.ShortTermLufs,
            IntegratedLufs = snap.IntegratedLufs,
            LoudnessRangeLu = snap.LoudnessRangeLu,
            TruePeakDbtp = postTp,
            MaxTruePeakDbtp = truePeakLimiterPostStage.IsEnabled && truePeakLimiterPostStage.MaxOutputTruePeakDbtp > -120f
                ? truePeakLimiterPostStage.MaxOutputTruePeakDbtp
                : (truePeakLimiterPostStage.IsEnabled
                    ? MathF.Min(snap.MaxTruePeakDbtp + bs1770PostStage.AppliedGainDb, truePeakLimiterPostStage.CeilingDbtp)
                    : snap.MaxTruePeakDbtp),
            CrestFactorDb = snap.CrestFactorDb,
            DialnormLufs = bs1770PostStage.ActiveDialnormLufs,
            AppliedNormGainDb = bs1770PostStage.AppliedGainDb,
            LimiterGainReductionDb = truePeakLimiterPostStage.PeakGainReductionDb,
            Iso226Active = iso226PostStage.IsFilterActive,
            Iso226EffectivePhon = iso226PostStage.EffectiveListeningSplPhon,
            Iso226MaxBoostDb = iso226PostStage.MaxCompensationBoostDb,
            Iso226FitRmsErrDb = iso226PostStage.FitRmsErrorDb,
            DialogActive = loudnessConfig.DialogEnhancementEnabled && loudnessConfig.DialogEnhancementDb > 0.05f,
            DialogIsSpatialMode = spatialDialogPostStage.SpatialPathActive,
            DialogSpeechActivity = spatialDialogPostStage.SpeechActivity,
            TruePeakCeilingDbtp = truePeakLimiterPostStage.CeilingDbtp
        };
    }

    void SetupOutputLayout() {
        if (HeadphoneVirtualizer) {
            if (!Listener.HeadphoneVirtualizer) {
                Listener.HeadphoneVirtualizer = true;
            }
            new VirtualizerFilter().SetLayout();
            listener.SampleRate = VirtualizerFilter.FilterSampleRate;
            listener.LFESeparation = true;
            limiterPostStage.LimiterOnly = LimiterOnly;
            limiterPostStage.Reset();
            virtualizerInitialized = true;
            appliedProfileVersion = VirtualizerFilter.ProfileVersion;
            activeNativeSampleRateRequest = listener.SampleRate;
        } else {
            if (Listener.HeadphoneVirtualizer) {
                Listener.HeadphoneVirtualizer = false;
            }
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref200));
            listener.LFESeparation = true;
            int desiredRate = ResolveDirectOutputSampleRate();
            listener.SampleRate = desiredRate;
            virtualizerInitialized = false;
            appliedProfileVersion = -1;
            activeNativeSampleRateRequest = desiredRate;
        }
        OutputSampleRate = listener.SampleRate;
        virtualizerRenderer.Bind(listener, HeadphoneVirtualizer, sceneHub.ActiveBinauralProfileName);
        SyncExternalizationLocked();
        sceneHub.ConfigurePostProcessingStages(listener.SampleRate, 2);
    }

    void SyncExternalizationLocked() {
        bool brirActive = sceneHub.IsMeasuredBrirActive || VirtualizerFilter.IsMeasuredBrirActive;
        lastExternalizationBrirState = brirActive;
        lastExternalizationEnvSize = EnvironmentSize;
        sceneHub.ExternalizationDiagnostics = virtualizerRenderer.ApplyExternalization(
            externalizationConfig,
            brirActive,
            EnvironmentSize);
    }

    int ResolveDirectOutputSampleRate() {
        if (renderingSettings.RoomCorrectionUsable) {
            return renderingSettings.RoomCorrection.SampleRate;
        }
        int trackRate = currentTrack != null ? Math.Clamp(currentTrack.SampleRate, 44100, 192000) : Listener.DefaultSampleRate;
        bool allowHardwareOverride = UsbDacNativeSampleRateEnabled ||
            (proAudioConfig.Enabled && proAudioConfig.LockedSampleRateHz > 0);
        if (allowHardwareOverride && PreferredHardwareSampleRate >= 44100 && PreferredHardwareSampleRate <= 192000) {
            return PreferredHardwareSampleRate;
        }
        return trackRate;
    }

    void BindTrackToSceneHubLocked(CavernizeTrack track) {
        CavernTrackDecoderAdapter decoderAdapter = new(track, sourceItems);
        parametricUpmixerStage.UpdateSettings(upmixingSettings);
        parametricUpmixerStage.BindTelemetryProvider(track.ActiveTelemetryProvider);
        sceneHub.BindDecoder(decoderAdapter, decoderAdapter.DescribeSources());
        sceneHub.ConfigureStageProcessors(OutputSampleRate, decoderAdapter.Metadata);
        sceneHub.ConfigurePostProcessingStages(OutputSampleRate, 2);
    }

    void ApplyLiveParameters() {
        if (Listener.EnvironmentType != EnvironmentType) {
            Listener.EnvironmentType = EnvironmentType;
        }
        if (EnvironmentSize.X > 0.1f && EnvironmentSize.Y > 0.1f && EnvironmentSize.Z > 0.1f &&
            Listener.EnvironmentSize != EnvironmentSize) {
            Listener.EnvironmentSize = EnvironmentSize;
        }

        int desiredDirectRate = !HeadphoneVirtualizer ? ResolveDirectOutputSampleRate() : VirtualizerFilter.FilterSampleRate;
        if (HeadphoneVirtualizer != virtualizerInitialized ||
            (HeadphoneVirtualizer && appliedProfileVersion != VirtualizerFilter.ProfileVersion) ||
            (!HeadphoneVirtualizer && desiredDirectRate != activeNativeSampleRateRequest)) {
            if (hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            SetupOutputLayout();
        } else if (lastExternalizationBrirState != (sceneHub.IsMeasuredBrirActive || VirtualizerFilter.IsMeasuredBrirActive) ||
            Vector3.DistanceSquared(lastExternalizationEnvSize, EnvironmentSize) > 1e-4f) {
            SyncExternalizationLocked();
        }

        if (DynamicCeilingOnly != lastDynamicCeilingState) {
            if (hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            lastDynamicCeilingState = DynamicCeilingOnly;
        }

        manualHeadTracker.SetPose(ListenerPosition, ListenerRotation);
        IHeadTrackerProvider activeTracker = sceneHub.ActiveHeadTracker ?? manualHeadTracker;
        bool externalLive = !ReferenceEquals(activeTracker, manualHeadTracker) && activeTracker.IsConnected;
        float horizonMs = Math.Clamp(HeadTrackingPredictionMs, 0f, 120f);
        ListenerPose6DoF pose = externalLive
            ? activeTracker.GetCurrentPose(horizonMs)
            : manualHeadTracker.GetCurrentPose(0f);

        if (externalLive) {
            // Preserve user's 3D Room translational offset if the hardware tracker is 3DoF-only
            Vector3 effectivePos = activeTracker.Supports6DoF && pose.Position.LengthSquared() > 1e-6f
                ? ListenerPosition + pose.Position
                : ListenerPosition;
            if (effectivePos != pose.Position) {
                pose = new ListenerPose6DoF(
                    effectivePos,
                    pose.Orientation,
                    pose.EulerDegrees,
                    pose.AngularVelocityRadPerSec,
                    pose.TimestampTicks,
                    pose.PredictionHorizonMs,
                    isTrackingActive: true);
            }
            // Mirror live tracked Euler orientation so 3D Room canvas & telemetry follow smoothly
            ListenerRotation = pose.EulerDegrees;
        }

        sceneHub.IsHeadTrackingLive = externalLive;
        sceneHub.ActiveHeadTrackerName = activeTracker.DisplayName;
        sceneHub.ListenerPose = pose;

        listener.Position = pose.Position;
        listener.Rotation = pose.EulerDegrees;
        listener.AudioQuality = AudioQuality;
        listener.Range = Math.Max(50f, EnvironmentSize.Length() * 5f);

        // Avoid double -3 dB attenuation on E-AC-3 JOC (EnhancedAC3Renderer already scales objects by 0.707f)
        // and avoid unnecessary -3 dB height attenuation when headphone virtualization provides 8 overhead speakers.
        float safeGain = currentTrack.Renderer.HasObjects && !HeadphoneVirtualizer && currentTrack.Renderer is not EnhancedAC3Renderer
            ? 0.707f
            : 1f;
        listener.Volume = Volume * (renderingSettings?.Gain ?? 1f) * safeGain;
        listener.LFEVolume = LFEVolume;
        // Avoid double limiting: Slot 6 runs the single transparent 4x True-Peak Limiter (Priority 900)
        // and optional upward AGC normalizer (Priority 890) across both Binaural and Direct 2.0 modes.
        listener.Normalizer = 0f;
        listener.LimiterOnly = LimiterOnly;
    }

    void ApplySourceMutesAndProperties() {
        if (Listener.HeadphoneVirtualizer != HeadphoneVirtualizer) {
            Listener.HeadphoneVirtualizer = HeadphoneVirtualizer;
        }

        // Keep cached flat source array synchronized without per-block enumerator boxing
        if (cachedActiveSources.Length != listener.ActiveSources.Count) {
            RebuildCachedActiveSourcesLocked();
        }

        IReadOnlyList<Source> activeObjects = isRenderingLiveNeuralSources
            ? liveNeuralSources
            : currentTrack.Renderer.Objects;
        bool anySolo = false;
        for (int i = 0; i < sourceItems.Count; i++) {
            if (sourceItems[i].IsSolo) {
                anySolo = true;
                break;
            }
        }

        long maskHash = (MuteBed ? 1L : 0L) | (OnlyBeds ? 2L : 0L) | (MuteGround ? 4L : 0L) | (DynamicCeilingOnly ? 8L : 0L);
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        SourceLayer[] activeLayers = isRenderingLiveNeuralSources ? null : currentTrack.ActiveSourceLayers;
        Source[] flatSources = cachedActiveSources;

        if (activeLayers != null && flatSources.Length == activeLayers.Length && sourceItems.Count == activeObjects.Count) {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                if (idx < sourceItems.Count && sourceItems[idx].HasCustomPosition) {
                    src.LockCustomPosition(sourceItems[idx].CustomNormalizedPosition * Listener.EnvironmentSize);
                } else {
                    src.UnlockCustomPosition();
                }
                Vector3 rawPos = src.Position * invEnv;
                bool dynFiltered = DynamicCeilingOnly && rawPos.Y <= 0.15f;
                bool mute;
                if (idx < sourceItems.Count) {
                    TrackSourceItem item = sourceItems[idx];
                    item.IsDynamicallyFilteredOut = dynFiltered;
                    mute = item.IsMuted || (anySolo && !item.IsSolo) || dynFiltered;
                    if (MuteBed && item.IsBed) {
                        mute = true;
                    }
                    if (OnlyBeds && !item.IsBed) {
                        mute = true;
                    }
                    if (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) {
                        mute = true;
                    }
                } else {
                    bool isVirtualBed = activeLayers[idx] == SourceLayer.VirtualMatrix;
                    mute = anySolo || dynFiltered || (MuteBed && isVirtualBed) || (OnlyBeds && !isVirtualBed) || (MuteGround && MathF.Abs(rawPos.Y) < 0.05f);
                }
                if (mute) {
                    maskHash ^= 1L << ((idx % 56) + 6);
                }
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        } else if (flatSources.Length == activeObjects.Count && sourceItems.Count == activeObjects.Count) {
            for (int i = 0; i < activeObjects.Count; i++) {
                Source src = activeObjects[i];
                TrackSourceItem item = sourceItems[i];
                if (item.HasCustomPosition) {
                    src.LockCustomPosition(item.CustomNormalizedPosition * Listener.EnvironmentSize);
                } else {
                    src.UnlockCustomPosition();
                }
                Vector3 rawPos = src.Position * invEnv;

                bool dynFiltered = DynamicCeilingOnly && rawPos.Y <= 0.15f;
                item.IsDynamicallyFilteredOut = dynFiltered;

                bool mute = item.IsMuted || (anySolo && !item.IsSolo) || dynFiltered;
                if (MuteBed && item.IsBed) {
                    mute = true;
                }
                if (OnlyBeds && !item.IsBed) {
                    mute = true;
                }
                if (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) {
                    mute = true;
                }

                if (mute) {
                    maskHash ^= 1L << ((i % 56) + 6);
                }

                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        } else {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                Vector3 rawPos = src.Position * invEnv;
                bool isBedPos = MathF.Abs(rawPos.X) % 1 < .01f && MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f;
                bool dynFiltered = DynamicCeilingOnly && rawPos.Y <= 0.15f;
                bool mute = (MuteBed && isBedPos) || (OnlyBeds && !isBedPos) || (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) || dynFiltered;
                if (mute) {
                    maskHash ^= 1L << ((idx % 56) + 6);
                }
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        }

        // Path A Spatial Dialog Enhancement (zero EQ smearing): isolate discrete FrontCenter bed and
        // front-center dialogue-cone 3D objects directly in the pre-virtualizer domain without drifting base volumes.
        bool dialogRequested = loudnessConfig.DialogEnhancementEnabled && loudnessConfig.DialogEnhancementDb > 0.05f;
        bool hasSpatialDialogue = false;
        if (dialogRequested && flatSources.Length == cachedBaseSourceVolumes.Length) {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                if (src.Mute) {
                    continue;
                }
                bool isCenterBed = idx < sourceItems.Count && sourceItems[idx].Channel == ReferenceChannel.FrontCenter;
                bool isLfeSrc = idx < sourceItems.Count && sourceItems[idx].IsLFE;
                Vector3 normPos = src.Position * invEnv;
                if (SpatialDialogEnhancer.IsDialogueCandidate(isCenterBed, isLfeSrc, normPos)) {
                    hasSpatialDialogue = true;
                    break;
                }
            }
        }

        spatialDialogPostStage.SpatialPathActive = dialogRequested && hasSpatialDialogue;
        SpatialDialogEnhancer.ComputeSpatialGains(
            dialogRequested,
            loudnessConfig.DialogEnhancementDb,
            hasSpatialDialogue,
            out float dialogGain,
            out float ambientGain);

        if (flatSources.Length == cachedBaseSourceVolumes.Length) {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                float baseVol = cachedBaseSourceVolumes[idx];
                if (!hasSpatialDialogue || !dialogRequested) {
                    src.Volume = baseVol;
                } else {
                    bool isCenterBed = idx < sourceItems.Count && sourceItems[idx].Channel == ReferenceChannel.FrontCenter;
                    bool isLfeSrc = idx < sourceItems.Count && sourceItems[idx].IsLFE;
                    Vector3 normPos = src.Position * invEnv;
                    bool isDiag = SpatialDialogEnhancer.IsDialogueCandidate(isCenterBed, isLfeSrc, normPos);
                    src.Volume = baseVol * (isDiag ? dialogGain : (isLfeSrc ? 1f : ambientGain));
                }
            }
        }

        if (hasRenderedAudio && lastSourceMaskHash != 0 && maskHash != lastSourceMaskHash && crossfadeRemaining == 0) {
            ArmCrossfadeLocked();
        }
        lastSourceMaskHash = maskHash;
    }

    void UpdateSourceTelemetry() {
        IReadOnlyList<Source> activeObjects = isRenderingLiveNeuralSources
            ? liveNeuralSources
            : currentTrack?.Renderer?.Objects;
        if (activeObjects == null) {
            return;
        }
        Vector3 envSize = Listener.EnvironmentSize;
        Vector3 invEnv = new(
            1f / MathF.Max(0.25f, envSize.X),
            1f / MathF.Max(0.25f, envSize.Y),
            1f / MathF.Max(0.25f, envSize.Z));
        Vector3 listenerNorm = sceneHub.ListenerPose.Position * invEnv;
        Vector3 physHalf = AirAbsorptionFilter.PhysicalRoomHalfSize;
        int sr = listener != null && listener.SampleRate > 0 ? listener.SampleRate : Listener.DefaultSampleRate;

        int count = Math.Min(sourceItems.Count, activeObjects.Count);
        for (int i = 0; i < count; i++) {
            Source src = activeObjects[i];
            TrackSourceItem item = sourceItems[i];
            Vector3 normPos = src.Position * invEnv;
            item.NormalizedPosition = normPos;
            Vector3 deltaMeters = (normPos - listenerNorm) * physHalf;
            item.PropagationDelayMs = item.IsLFE
                ? 0f
                : AcousticPropagationDelayLine.ComputePropagationDelayMs(deltaMeters.Length());

            float[] rendered = src.Rendered?[0];
            if (rendered != null && rendered.Length > 0 && !src.Mute) {
                SpatialXRayPowermap.EstimateThreeBandRms(
                    rendered,
                    sr,
                    out float lowRms,
                    out float midRms,
                    out float highRms);
                item.LowBandEnergy = lowRms;
                item.MidBandEnergy = midRms;
                item.HighBandEnergy = highRms;
                item.RmsLevel = MathF.Sqrt(lowRms * lowRms + midRms * midRms + highRms * highRms);
            } else {
                item.RmsLevel = 0f;
                item.LowBandEnergy = 0f;
                item.MidBandEnergy = 0f;
                item.HighBandEnergy = 0f;
            }
        }
        sceneHub.SyncTelemetryFromTrackItems(sourceItems, envSize);
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (renderLock) {
            IsPlaying = false;
            if (ReferenceEquals(VirtualizerFilter.PreHrtfBusProcessor, compositePreHrtfBus) ||
                ReferenceEquals(VirtualizerFilter.PreHrtfBusProcessor, lgsrStage) ||
                ReferenceEquals(VirtualizerFilter.PreHrtfBusProcessor, neuralStemStage)) {
                VirtualizerFilter.PreHrtfBusProcessor = null;
            }
            liveNeuralSources = Array.Empty<Source>();
            isRenderingLiveNeuralSources = false;
            cachedHybridTrack = null;
            cachedHybrid714Buses = null;
            listener?.DetachAllSources();
            listener = null;
            currentTrack = null;
            sceneHub.BindDecoder(null);
            virtualizerRenderer.Dispose();
            autoEqPostStage.Dispose();
            hptfNotchPostStage.Dispose();
            iso226PostStage.Dispose();
            bs1770PostStage.Dispose();
            spatialDialogPostStage.Dispose();
            limiterPostStage.Dispose();
            truePeakLimiterPostStage.Dispose();
            parametricUpmixerStage.Dispose();
            neuralStemStage.Dispose();
            lgsrStage.Dispose();
            vocalMixMatchStage.Dispose();
            manualHeadTracker.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
