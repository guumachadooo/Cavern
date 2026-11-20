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
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;

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
    readonly CavernVirtualizerRendererAdapter virtualizerRenderer = new();
    readonly AutoEqPostProcessingStage autoEqPostStage = new();
    readonly NormalizerPostProcessingAdapter limiterPostStage = new(true);
    readonly SpatialSceneFrame activeSceneFrame = new();

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

    /// <summary>
    /// Creates a real-time spatial audio engine wired with Slot 1–6 processors.
    /// </summary>
    public RealtimePlayerEngine() {
        sceneHub.BindHeadTracker(manualHeadTracker);
        sceneHub.BindRenderer(virtualizerRenderer);
        sceneHub.RegisterStageProcessor(parametricUpmixerStage);
        sceneHub.RegisterPostProcessingStage(autoEqPostStage);
        sceneHub.RegisterPostProcessingStage(limiterPostStage);
    }

    /// <summary>
    /// Slot 3 unified scene hub exposing active sources, 6DoF listener pose, programme metadata, telemetry,
    /// and extensible Slot 2 / Slot 4 / Slot 5 / Slot 6 pipeline registrations.
    /// </summary>
    public SpatialSceneHub SceneHub => sceneHub;

    /// <summary>
    /// Slot 6A neutral AutoEq parametric headphone compensation stage (Priority 100, before safety limiter).
    /// </summary>
    public AutoEqPostProcessingStage AutoEqStage => autoEqPostStage;

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
            hasRenderedAudio = false;
            crossfadeRemaining = 0;
            lastSourceMaskHash = 0;

            ConfigureListener(resetToStart: true);
            sourceItems = TrackSourceItem.BuildFromTrack(track);
            BindTrackToSceneHubLocked(track);
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
            lastSourceMaskHash = 0;

            ConfigureListener(resetToStart: false);
            sourceItems = TrackSourceItem.BuildFromTrack(track);
            BindTrackToSceneHubLocked(track);
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
            ConfigureListener(resetToStart: false);
            sceneHub.ResetStatefulStages();
        }
    }

    /// <summary>
    /// Renders the next block of stereo interleaved float samples into <paramref name="stereoOutput"/>.
    /// Returns the number of stereo sample frames written (0 if stopped or finished).
    /// </summary>
    public int RenderNextInterleavedBlock(float[] stereoOutput, int maxStereoFrames) {
        if (stereoOutput == null || maxStereoFrames <= 0) {
            return 0;
        }

        bool reachedEnd = false;
        int totalFramesWritten = 0;

        lock (renderLock) {
            if (!IsPlaying || currentTrack == null || listener == null) {
                Array.Clear(stereoOutput, 0, Math.Min(stereoOutput.Length, maxStereoFrames * 2));
                return 0;
            }

            ApplyLiveParameters();

            while (totalFramesWritten + updateRate <= maxStereoFrames) {
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

                int remainingFrames = maxStereoFrames - totalFramesWritten;
                int chunks = Math.Min(framesPerChunk, remainingFrames / updateRate);
                if (chunks <= 0) {
                    chunks = 1;
                }

                ApplySourceMutesAndProperties();

                int requestedFrames = chunks * updateRate;
                int outOffset = totalFramesWritten * 2;
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

                // Slot 2: Ordered spatial stage processors (pass-through until Wave W02/W08)
                IReadOnlyList<ISpatialStageProcessor> stageProcessors = sceneHub.StageProcessors;
                for (int p = 0; p < stageProcessors.Count; p++) {
                    if (stageProcessors[p].IsEnabled) {
                        stageProcessors[p].Process(activeSceneFrame);
                    }
                }

                // Slot 5: Binaural / Stereo renderer (delegates to Cavern Listener + VirtualizerFilter)
                IBinauralRenderer renderer = sceneHub.ActiveRenderer ?? virtualizerRenderer;
                int blockFrames = renderer.RenderFrame(activeSceneFrame, stereoOutput, outOffset, requestedFrames);
                if (blockFrames <= 0) {
                    break;
                }

                // Slot 6: Ordered transparent post-processing stages (AutoEq Priority 100, ISO 226, BS.1770, Soft-Knee Limiter Priority 900)
                limiterPostStage.LimiterOnly = LimiterOnly;
                limiterPostStage.IsEnabled = (HeadphoneVirtualizer || autoEqPostStage.IsEnabled) && NormalizerEnabled;
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

                SoundfieldTelemetry telemetry = activeSceneFrame.Telemetry;
                telemetry.TruePeakDbfs = blockPeak > 1e-6f ? 20f * MathF.Log10(blockPeak) : -120f;
                activeSceneFrame.Telemetry = telemetry;

                RecordTailLocked(stereoOutput, outOffset, blockFrames);

                double sampleAdvance = blockFrames * (currentTrack.SampleRate / (double)listener.SampleRate);
                currentTrackSample += (long)Math.Round(sampleAdvance);
                totalFramesWritten += blockFrames;

                UpdateSourceTelemetry();
                parametricUpmixerStage.SyncTelemetry(activeSceneFrame);
                sceneHub.Telemetry = activeSceneFrame.Telemetry;
            }
        }

        if (reachedEnd) {
            PlaybackCompleted?.Invoke();
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

        if (resetToStart) {
            currentTrack.Attach(listener, upmixingSettings);
        } else {
            currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
        }

        parametricUpmixerStage.BindTelemetryProvider(currentTrack.ActiveTelemetryProvider);
        SetupOutputLayout();
    }

    void SetupOutputLayout() {
        if (HeadphoneVirtualizer) {
            Listener.HeadphoneVirtualizer = true;
            new VirtualizerFilter().SetLayout();
            listener.SampleRate = VirtualizerFilter.FilterSampleRate;
            listener.LFESeparation = true;
            limiterPostStage.LimiterOnly = LimiterOnly;
            limiterPostStage.Reset();
            virtualizerInitialized = true;
            appliedProfileVersion = VirtualizerFilter.ProfileVersion;
        } else {
            Listener.HeadphoneVirtualizer = false;
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref200));
            listener.LFESeparation = true;
            listener.SampleRate = renderingSettings.RoomCorrectionUsable
                ? renderingSettings.RoomCorrection.SampleRate
                : Math.Max(44100, currentTrack.SampleRate);
            virtualizerInitialized = false;
            appliedProfileVersion = -1;
        }
        OutputSampleRate = listener.SampleRate;
        virtualizerRenderer.Bind(listener, HeadphoneVirtualizer, sceneHub.ActiveBinauralProfileName);
        sceneHub.ConfigurePostProcessingStages(listener.SampleRate, 2);
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
        Listener.EnvironmentType = EnvironmentType;
        if (EnvironmentSize.X > 0.1f && EnvironmentSize.Y > 0.1f && EnvironmentSize.Z > 0.1f) {
            Listener.EnvironmentSize = EnvironmentSize;
        }

        if (HeadphoneVirtualizer != virtualizerInitialized ||
            (HeadphoneVirtualizer && appliedProfileVersion != VirtualizerFilter.ProfileVersion)) {
            if (hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            SetupOutputLayout();
        }

        if (DynamicCeilingOnly != lastDynamicCeilingState) {
            if (hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            lastDynamicCeilingState = DynamicCeilingOnly;
        }

        manualHeadTracker.SetPose(ListenerPosition, ListenerRotation);
        IHeadTrackerProvider activeTracker = sceneHub.ActiveHeadTracker ?? manualHeadTracker;
        ListenerPose6DoF pose = activeTracker.IsConnected
            ? activeTracker.GetCurrentPose()
            : manualHeadTracker.GetCurrentPose();
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
        // Avoid double limiting: when HeadphoneVirtualizer is active, only the single post-virtualizer stereo-linked limiter runs.
        listener.Normalizer = !HeadphoneVirtualizer && NormalizerEnabled ? 1f : 0f;
        listener.LimiterOnly = LimiterOnly;
    }

    void ApplySourceMutesAndProperties() {
        Listener.HeadphoneVirtualizer = HeadphoneVirtualizer;

        IReadOnlyList<Source> rendererObjects = currentTrack.Renderer.Objects;
        bool anySolo = false;
        for (int i = 0; i < sourceItems.Count; i++) {
            if (sourceItems[i].IsSolo) {
                anySolo = true;
                break;
            }
        }

        long maskHash = (MuteBed ? 1L : 0L) | (OnlyBeds ? 2L : 0L) | (MuteGround ? 4L : 0L) | (DynamicCeilingOnly ? 8L : 0L);
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        SourceLayer[] activeLayers = currentTrack.ActiveSourceLayers;
        if (activeLayers != null && listener.ActiveSources.Count == activeLayers.Length && sourceItems.Count == rendererObjects.Count) {
            int idx = 0;
            foreach (Source src in listener.ActiveSources) {
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
                idx++;
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        } else if (listener.ActiveSources.Count == rendererObjects.Count && sourceItems.Count == rendererObjects.Count) {
            for (int i = 0; i < rendererObjects.Count; i++) {
                Source src = rendererObjects[i];
                TrackSourceItem item = sourceItems[i];
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
            int idx = 0;
            foreach (Source src in listener.ActiveSources) {
                Vector3 rawPos = src.Position * invEnv;
                bool isBedPos = MathF.Abs(rawPos.X) % 1 < .01f && MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f;
                bool dynFiltered = DynamicCeilingOnly && rawPos.Y <= 0.15f;
                bool mute = (MuteBed && isBedPos) || (OnlyBeds && !isBedPos) || (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) || dynFiltered;
                if (mute) {
                    maskHash ^= 1L << ((idx % 56) + 6);
                }
                idx++;
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        }

        if (hasRenderedAudio && lastSourceMaskHash != 0 && maskHash != lastSourceMaskHash && crossfadeRemaining == 0) {
            ArmCrossfadeLocked();
        }
        lastSourceMaskHash = maskHash;
    }

    void UpdateSourceTelemetry() {
        IReadOnlyList<Source> rendererObjects = currentTrack?.Renderer?.Objects;
        if (rendererObjects == null) {
            return;
        }
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        int count = Math.Min(sourceItems.Count, rendererObjects.Count);
        for (int i = 0; i < count; i++) {
            Source src = rendererObjects[i];
            sourceItems[i].NormalizedPosition = src.Position * invEnv;
            float[] rendered = src.Rendered?[0];
            if (rendered != null && rendered.Length > 0 && !src.Mute) {
                double sumSq = 0;
                int step = Math.Max(1, rendered.Length / 16);
                int samples = 0;
                for (int s = 0; s < rendered.Length; s += step) {
                    float v = rendered[s];
                    sumSq += v * v;
                    samples++;
                }
                sourceItems[i].RmsLevel = MathF.Sqrt((float)(sumSq / Math.Max(1, samples)));
            } else {
                sourceItems[i].RmsLevel = 0f;
            }
        }
        sceneHub.SyncTelemetryFromTrackItems(sourceItems, Listener.EnvironmentSize);
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (renderLock) {
            IsPlaying = false;
            listener?.DetachAllSources();
            listener = null;
            currentTrack = null;
            sceneHub.BindDecoder(null);
            virtualizerRenderer.Dispose();
            limiterPostStage.Dispose();
            parametricUpmixerStage.Dispose();
            manualHeadTracker.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
