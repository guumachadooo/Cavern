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
public sealed partial class RealtimePlayerEngine : IDisposable {
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
    ProgressiveNeuralStemUpdater progressiveNeuralUpdater;
    readonly CompositePreHrtfBusProcessor compositePreHrtfBus = new();
    int activeNativeSampleRateRequest;
    PhysicalExternalizationConfig externalizationConfig = PhysicalExternalizationConfig.Default;
    HrtfPersonalizationConfig personalizationConfig = HrtfPersonalizationConfig.Default;
    PsychoacousticLoudnessConfig loudnessConfig = PsychoacousticLoudnessConfig.Default;
    ProAudioDuplexConfig proAudioConfig = ProAudioDuplexConfig.Default;
    bool lastExternalizationBrirState;
    Vector3 lastExternalizationEnvSize;

    sealed class StatefulLr4Crossover {
        readonly double a1, a2, lpB0, lpB1, lpB2, hpB0, hpB1, hpB2;
        double lp1x1, lp1x2, lp1y1, lp1y2;
        double lp2x1, lp2x2, lp2y1, lp2y2;
        double hp1x1, hp1x2, hp1y1, hp1y2;
        double hp2x1, hp2x2, hp2y1, hp2y2;

        public StatefulLr4Crossover(int sampleRate, float cutoffHz) {
            double safeSr = Math.Max(8000, sampleRate);
            double fc = Math.Clamp(cutoffHz, 40.0, Math.Min(250.0, safeSr * 0.25));
            double w0 = 2.0 * Math.PI * fc / safeSr;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW / (2.0 * 0.7071067811865476);
            double a0 = 1.0 + alpha;
            a1 = (-2.0 * cosW) / a0;
            a2 = (1.0 - alpha) / a0;
            lpB0 = ((1.0 - cosW) * 0.5) / a0;
            lpB1 = (1.0 - cosW) / a0;
            lpB2 = lpB0;
            hpB0 = ((1.0 + cosW) * 0.5) / a0;
            hpB1 = -(1.0 + cosW) / a0;
            hpB2 = hpB0;
        }

        public void ProcessRange(float[] src, int start, int end, float[] highPassDst, float[] lowPassDst) {
            for (int i = start; i < end; i++) {
                double x = src[i];
                if (!double.IsFinite(x)) x = 0.0;

                double l1 = lpB0 * x + lpB1 * lp1x1 + lpB2 * lp1x2 - a1 * lp1y1 - a2 * lp1y2;
                lp1x2 = lp1x1; lp1x1 = x; lp1y2 = lp1y1; lp1y1 = l1;
                double l2 = lpB0 * l1 + lpB1 * lp2x1 + lpB2 * lp2x2 - a1 * lp2y1 - a2 * lp2y2;
                lp2x2 = lp2x1; lp2x1 = l1; lp2y2 = lp2y1; lp2y1 = l2;
                lowPassDst[i] = (float)l2;

                double h1 = hpB0 * x + hpB1 * hp1x1 + hpB2 * hp1x2 - a1 * hp1y1 - a2 * hp1y2;
                hp1x2 = hp1x1; hp1x1 = x; hp1y2 = hp1y1; hp1y1 = h1;
                double h2 = hpB0 * h1 + hpB1 * hp2x1 + hpB2 * hp2x2 - a1 * hp2y1 - a2 * hp2y2;
                hp2x2 = hp2x1; hp2x1 = h1; hp2y2 = hp2y1; hp2y1 = h2;
                highPassDst[i] = (float)h2;
            }
        }
    }

    sealed class ProgressiveNeuralStemUpdater {
        const float InvSqrt2 = 0.70710678f;

        public NeuralSeparatedTrack Track { get; }
        public int WrittenSamples { get; private set; }
        readonly List<(StereoStemBuffer Stem, float[] MonoDst, float[] TempMono, StatefulLr4Crossover BassLr4, float[] LfeDst)> monoJobs = [];
        readonly List<(StereoStemBuffer Stem, float[] HpL, float[] HpR, float[] LpL, float[] LpR, float[] LfeDst, StatefulLr4Crossover Lr4L, StatefulLr4Crossover Lr4R)> stereoLfeJobs = [];

        public ProgressiveNeuralStemUpdater(NeuralSeparatedTrack track) {
            Track = track;
        }

        public void AddMonoJob(StereoStemBuffer stem, float[] monoDst, StatefulLr4Crossover bassLr4 = null, float[] lfeDst = null) {
            float[] tempMono = bassLr4 != null ? new float[stem.Length] : null;
            monoJobs.Add((stem, monoDst, tempMono, bassLr4, lfeDst));
        }

        public void AddStereoLfeJob(StereoStemBuffer stem, float[] hpL, float[] hpR, float[] lfeDst, int sampleRate) {
            stereoLfeJobs.Add((
                stem,
                hpL,
                hpR,
                new float[stem.Length],
                new float[stem.Length],
                lfeDst,
                new StatefulLr4Crossover(sampleRate, 120f),
                new StatefulLr4Crossover(sampleRate, 120f)));
        }

        public void AdvanceTo(int targetSamples) {
            int limit = Math.Min(targetSamples, Track.TotalSampleCount);
            if (limit <= WrittenSamples) {
                return;
            }
            int start = WrittenSamples;

            for (int j = 0; j < monoJobs.Count; j++) {
                var job = monoJobs[j];
                int end = Math.Min(limit, job.Stem.Length);
                if (end <= start) continue;

                float[] srcL = job.Stem.Left;
                float[] srcR = job.Stem.Right;
                if (job.BassLr4 != null && job.LfeDst != null && job.TempMono != null) {
                    for (int n = start; n < end; n++) {
                        job.TempMono[n] = (srcL[n] + srcR[n]) * InvSqrt2;
                    }
                    job.BassLr4.ProcessRange(job.TempMono, start, end, job.MonoDst, job.LfeDst);
                } else {
                    for (int n = start; n < end; n++) {
                        job.MonoDst[n] = (srcL[n] + srcR[n]) * InvSqrt2;
                    }
                }
            }

            for (int j = 0; j < stereoLfeJobs.Count; j++) {
                var job = stereoLfeJobs[j];
                int end = Math.Min(limit, job.Stem.Length);
                if (end <= start) continue;

                job.Lr4L.ProcessRange(job.Stem.Left, start, end, job.HpL, job.LpL);
                job.Lr4R.ProcessRange(job.Stem.Right, start, end, job.HpR, job.LpR);
                for (int n = start; n < end; n++) {
                    job.LfeDst[n] = (job.LpL[n] + job.LpR[n]) * 0.5f;
                }
            }

            WrittenSamples = limit;
        }
    }

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
        neuralStemStage.UpstreamLatencySamplesProvider = () => lgsrBypassed ? 0 : lgsrStage.LastAppliedLatencySamples;
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
                sourceItems = TrackSourceItem.BuildFromAttachedSources(currentTrack, cachedActiveSources);
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
            item.NormalizedPosition = item.DefaultNormalizedPosition;
            if (item.Layer == SourceLayer.LiveMicrophone) {
                liveMicBridge.SetNormalizedPosition(proAudioConfig.MicNormalizedPosition);
            } else if (item.Index >= 0 && item.Index < cachedActiveSources.Length) {
                cachedActiveSources[item.Index].UnlockCustomPosition(item.DefaultNormalizedPosition * Listener.EnvironmentSize);
            }
            sceneHub.ClearSourceCustomPosition(item.Index);
        }
    }

    /// <summary>
    /// Resets all manually dragged 3D object / stem / live mic pins back to their native trajectories or defaults.
    /// </summary>
    public void ResetAllCustomPositions() {
        lock (renderLock) {
            for (int i = 0; i < sourceItems.Count; i++) {
                TrackSourceItem item = sourceItems[i];
                item.HasCustomPosition = false;
                item.CustomNormalizedPosition = Vector3.Zero;
                item.NormalizedPosition = item.DefaultNormalizedPosition;
                if (i < cachedActiveSources.Length) {
                    cachedActiveSources[i].UnlockCustomPosition(item.DefaultNormalizedPosition * Listener.EnvironmentSize);
                }
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
                safe.TruePeakLimiterEnabled,
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
    double currentTrackSampleExact;

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
    /// Enable Cavern's legacy dynamic normalizer/limiter (superseded by Slot 6B BS.1770-5 + 4x True-Peak Limiter).
    /// </summary>
    public bool NormalizerEnabled { get; set; }

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
            progressiveNeuralUpdater = null;
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
