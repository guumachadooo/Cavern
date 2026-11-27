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


public sealed partial class RealtimePlayerEngine {
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
            currentTrackSampleExact = 0.0;
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            hasRenderedAudio = false;
            crossfadeRemaining = 0;
            lastSourceMaskHash = 0;
            telemetryFramesSincePublish = int.MaxValue / 2;
            OutputPipelineLatencySeconds = 0.0;

            if (neuralStemStage.ActiveSpatialUpmixTrack != null &&
                !MatchesSeparatedTrack(neuralStemStage.ActiveSpatialUpmixTrack, track)) {
                neuralStemStage.BindSpatialUpmixTrack(null);
                cachedHybridTrack = null;
                cachedHybrid714Buses = null;
            }
            if (neuralStemStage.ActiveKaraokeTrack != null &&
                !MatchesSeparatedTrack(neuralStemStage.ActiveKaraokeTrack, track)) {
                neuralStemStage.BindKaraokeReferenceTrack(null);
            }
            isPreviewingRecordedPerformance = false;
            previewPerformanceTake = null;
            lgsrStage.ClearExplicitLosslessStatus();

            neuralStemStage.SetSourceTrackSampleRate(track.SampleRate);
            sourceItems = [];
            ConfigureListener(resetToStart: true);
            sceneHub.NeuralDiagnostics = neuralStemStage.BuildDiagnosticsSnapshot();
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
        }
        outputFlushCallback?.Invoke();
        SourcesRebuilt?.Invoke();
    }

    /// <summary>
    /// Atomically swaps the playing track (e.g., switching Atmos ↔ FLAC ↔ Opus variant) at <paramref name="startSeconds"/>
    /// without stopping playback hardware, blending smoothly via cosine crossfade from the previous audio tail.
    /// </summary>
    public void HotSwapTrackSeamless(
        CavernizeTrack track,
        double startSeconds,
        UpmixingSettings upmixing = null,
        RenderingSettings rendering = null,
        bool preserveNeuralStems = false,
        double neuralReferenceOffset = double.NaN,
        double neuralReferenceDriftPpm = double.NaN) {
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
            currentTrackSampleExact = currentTrackSample;
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            lastSourceMaskHash = 0;

            if (!preserveNeuralStems) {
                if (neuralStemStage.ActiveSpatialUpmixTrack != null &&
                    !MatchesSeparatedTrack(neuralStemStage.ActiveSpatialUpmixTrack, track)) {
                    neuralStemStage.BindSpatialUpmixTrack(null);
                    cachedHybridTrack = null;
                    cachedHybrid714Buses = null;
                }
                if (neuralStemStage.ActiveKaraokeTrack != null &&
                    !MatchesSeparatedTrack(neuralStemStage.ActiveKaraokeTrack, track)) {
                    neuralStemStage.BindKaraokeReferenceTrack(null);
                }
                neuralStemStage.ReferenceSampleOffset = 0.0;
                neuralStemStage.ReferenceDriftPpm = 0.0;
            } else {
                if (!double.IsNaN(neuralReferenceOffset)) {
                    neuralStemStage.ReferenceSampleOffset = neuralReferenceOffset;
                }
                if (!double.IsNaN(neuralReferenceDriftPpm)) {
                    neuralStemStage.ReferenceDriftPpm = neuralReferenceDriftPpm;
                }
            }
            lgsrStage.ClearExplicitLosslessStatus();

            neuralStemStage.SetSourceTrackSampleRate(track.SampleRate);
            ConfigureListener(resetToStart: false);
            sceneHub.LgsrDiagnostics = lgsrStage.BuildDiagnosticsSnapshot();
            IsPlaying = wasPlaying;
        }
        SourcesRebuilt?.Invoke();
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
    /// Seeks playback to <paramref name="seconds"/> with a clickless crossfade transition and flushes downstream output buffers.
    /// </summary>
    public void Seek(double seconds) {
        bool didSeek = false;
        lock (renderLock) {
            if (currentTrack == null) {
                return;
            }
            if (IsPlaying && hasRenderedAudio) {
                ArmCrossfadeLocked();
            }
            long targetSample = (long)Math.Round(seconds * currentTrack.SampleRate);
            Volatile.Write(ref currentTrackSample, currentTrack.Seek(targetSample));
            currentTrackSampleExact = currentTrackSample;
            subBlockAvailableFrames = 0;
            subBlockReadOffset = 0;
            ConfigureListener(resetToStart: false);
            sceneHub.ResetStatefulStages();
            OutputPipelineLatencySeconds = 0.0;
            didSeek = true;
        }
        if (didSeek) {
            outputFlushCallback?.Invoke();
            SourcesRebuilt?.Invoke();
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
        bool rebuiltSources = false;
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

            // When Path B/A requests sub-256f bursts (e.g. 64f/128f), stage backing track ONLY and bypass
            // subBlockStaging for live microphone monitoring so live vocal path maintains 0 ms staging lag.
            bool isSubBlockPath = subBlockAvailableFrames > 0 || (maxStereoFrames % updateRate) != 0;
            bool hasRecordedPreview = isPreviewingRecordedPerformance && previewPerformanceTake != null && previewPerformanceTake.HasAudio;
            bool liveMicActive = liveMicBridge.IsActive || liveMicBridge.IsCalibratingLatency;
            bool deferLiveMicToDirectOutput = liveMicActive && !hasRecordedPreview && isSubBlockPath;

            // Drain any previously staged sub-block backing frames first
            if (subBlockAvailableFrames > 0) {
                int take = Math.Min(subBlockAvailableFrames, maxStereoFrames);
                Array.Copy(subBlockStaging, subBlockReadOffset * 2, stereoOutput, 0, take * 2);
                subBlockReadOffset += take;
                subBlockAvailableFrames -= take;
                totalFramesWritten += take;
            }

            // If remaining request is smaller than updateRate (e.g., 64f/128f Path B/A burst),
            // render one 256-frame backing block into subBlockStaging and deliver exact requested frames.
            if (totalFramesWritten < maxStereoFrames) {
                if (maxStereoFrames - totalFramesWritten < updateRate) {
                    int staged = RenderAlignedBlocksLocked(
                        subBlockStaging,
                        0,
                        updateRate,
                        mixLiveMic: !deferLiveMicToDirectOutput,
                        ref reachedEnd,
                        ref rebuiltSources);
                    if (staged > 0) {
                        int take = Math.Min(staged, maxStereoFrames - totalFramesWritten);
                        Array.Copy(subBlockStaging, 0, stereoOutput, totalFramesWritten * 2, take * 2);
                        subBlockReadOffset = take;
                        subBlockAvailableFrames = staged - take;
                        totalFramesWritten += take;
                    }
                } else {
                    int alignedFrames = ((maxStereoFrames - totalFramesWritten) / updateRate) * updateRate;
                    totalFramesWritten += RenderAlignedBlocksLocked(
                        stereoOutput,
                        totalFramesWritten * 2,
                        alignedFrames,
                        mixLiveMic: !deferLiveMicToDirectOutput,
                        ref reachedEnd,
                        ref rebuiltSources);
                }
            }

            if (deferLiveMicToDirectOutput && totalFramesWritten > 0) {
                vocalMixMatchStage.EnsureSampleRate(listener.SampleRate);
                liveMicBridge.MixIntoStereoOutputZeroGc(
                    stereoOutput,
                    0,
                    totalFramesWritten,
                    sceneHub.ListenerPose,
                    Listener.EnvironmentSize,
                    vocalProcessor: liveMicBridge.IsCalibratingLatency ? null : vocalMixMatchStage,
                    applyBackingSidechain: true);
                float ceilLin = truePeakLimiterPostStage.IsEnabled
                    ? Math.Clamp(MathF.Pow(10f, truePeakLimiterPostStage.CeilingDbtp / 20f), 0.1f, 1f)
                    : 1f;
                for (int i = 0; i < totalFramesWritten * 2; i++) {
                    stereoOutput[i] = Math.Clamp(stereoOutput[i], -ceilLin, ceilLin);
                }
                if (liveMicBridge.IsActive) {
                    sceneHub.SetLiveMicrophoneDescriptor(liveMicBridge.BuildSceneDescriptor(Listener.EnvironmentSize, sceneHub.Sources.Count));
                }
                VocalChainTelemetry directVocalTel = vocalMixMatchStage.Telemetry;
                SoundfieldTelemetry directMicTel = sceneHub.Telemetry;
                directMicTel.LiveMicRmsDbfs = liveMicBridge.RmsDbfs;
                directMicTel.LiveMicPeakDbfs = liveMicBridge.PeakDbfs;
                directMicTel.FullDuplexRoundTripMs = liveMicBridge.RoundTripLatencyMs;
                directMicTel.IsLiveMicActive = liveMicBridge.IsActive;
                directMicTel.VocalChainInputDbfs = directVocalTel.InputDbfs;
                directMicTel.VocalChainOutputDbfs = directVocalTel.OutputDbfs;
                directMicTel.VocalChainGateReductionDb = directVocalTel.GateReductionDb;
                directMicTel.VocalChainDeEsserReductionDb = directVocalTel.DeEsserReductionDb;
                directMicTel.VocalChainCompressorReductionDb = directVocalTel.CompressorReductionDb;
                directMicTel.VocalChainSidechainDuckDb = directVocalTel.SidechainPocketDuckDb;
                directMicTel.VocalChainBypassed = vocalMixMatchStage.IsBypassed;
                sceneHub.Telemetry = directMicTel;
                sceneHub.VocalChainDiagnostics = directVocalTel;
            }
        }

        if (rebuiltSources) {
            SourcesRebuilt?.Invoke();
        }
        if (reachedEnd) {
            PlaybackCompleted?.Invoke();
        }
        return totalFramesWritten;
    }

    int RenderAlignedBlocksLocked(
        float[] stereoOutput,
        int baseSampleOffset,
        int maxAlignedFrames,
        bool mixLiveMic,
        ref bool reachedEnd,
        ref bool rebuiltSources) {
        int totalFramesWritten = 0;

            while (totalFramesWritten + updateRate <= maxAlignedFrames) {
                if (currentTrack.Length > 0 && currentTrackSample >= currentTrack.Length) {
                    if (Loop) {
                        Volatile.Write(ref currentTrackSample, currentTrack.Seek(0));
                        currentTrackSampleExact = currentTrackSample;
                        subBlockAvailableFrames = 0;
                        subBlockReadOffset = 0;
                        ConfigureListener(resetToStart: false);
                        ApplyLiveParameters();
                        rebuiltSources = true;
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

                // Advance incremental progressive 3D neural buffers and guard playhead against seeking past ReadySampleCount
                NeuralSeparatedTrack activeSep = neuralStemStage.ActiveSpatialUpmixTrack;
                if (isRenderingLiveNeuralSources) {
                    if (progressiveNeuralUpdater != null && ReferenceEquals(progressiveNeuralUpdater.Track, activeSep)) {
                        progressiveNeuralUpdater.AdvanceTo(activeSep.ReadySampleCount);
                    }
                    if (activeSep != null && activeSep.IsProgressiveStreaming && !IsProgressiveWindowAheadOfPlayheadLocked(activeSep)) {
                        if (hasRenderedAudio) {
                            ArmCrossfadeLocked();
                        }
                        ConfigureListener(resetToStart: false);
                        ApplyLiveParameters();
                        rebuiltSources = true;
                    }
                } else if (activeSep != null && activeSep.IsProgressiveStreaming && ShouldRenderLiveNeuralSourcesLocked()) {
                    if (hasRenderedAudio) {
                        ArmCrossfadeLocked();
                    }
                    ConfigureListener(resetToStart: false);
                    ApplyLiveParameters();
                    rebuiltSources = true;
                }

                ApplyLiveChoreographyPositionsLocked(GetCurrentTrackSecondsLocked());
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
                bool directStemKaraokeActive = isRenderingLiveNeuralSources &&
                    hasDiscreteLiveVocalSource &&
                    neuralStemStage.Config.SpatialKaraokeEnabled;
                bool karaokeActive = !directStemKaraokeActive && neuralStemStage.IsKaraokeActiveOrRamping;
                if (HeadphoneVirtualizer && (lgsrActive || karaokeActive)) {
                    compositePreHrtfBus.First = lgsrActive ? lgsrStage : null;
                    compositePreHrtfBus.Second = karaokeActive ? neuralStemStage : null;
                    VirtualizerFilter.PreHrtfBusProcessor = compositePreHrtfBus;
                } else {
                    VirtualizerFilter.PreHrtfBusProcessor = null;
                }
                Source primarySource = isRenderingLiveNeuralSources
                    ? (liveNeuralSources.Length > 0 ? liveNeuralSources[0] : null)
                    : (cachedActiveSources.Length > 0 ? cachedActiveSources[0] : null);
                long prevSourceSamples = primarySource != null ? primarySource.CumulativeSourceSamples : 0L;

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

                // Slot 1 / W10 Sample-Locked Live Microphone OR W48 Studio Recorded Take Preview + Slot 2D Vocal Chain
                // Mixed right before Slot 6 4x True-Peak Limiter so vocal + Karaoke + 1-4 kHz sidechain pocket never clip.
                if (isPreviewingRecordedPerformance && previewPerformanceTake != null && previewPerformanceTake.HasAudio) {
                    MixRecordedPerformancePreviewZeroGc(previewPerformanceTake, stereoOutput, outOffset, blockFrames);
                } else if (mixLiveMic && (liveMicBridge.IsActive || liveMicBridge.IsCalibratingLatency)) {
                    liveMicBridge.MixIntoStereoOutputZeroGc(
                        stereoOutput,
                        outOffset,
                        blockFrames,
                        sceneHub.ListenerPose,
                        Listener.EnvironmentSize,
                        vocalProcessor: liveMicBridge.IsCalibratingLatency ? null : vocalMixMatchStage,
                        applyBackingSidechain: true);
                } else if (!liveMicBridge.IsActive && !liveMicBridge.IsCalibratingLatency) {
                    vocalMixMatchStage.ClearTelemetryIfIdle();
                }

                // Slot 6: Ordered transparent post-processing stages
                // (100 AutoEq, 110 HpTF, 300 ISO 226:2023, 400 BS.1770/Dialnorm, 500 Dialog Path B, 900 4x True-Peak Limiter)
                iso226PostStage.UpdatePlaybackVolume(Volume * systemPlaybackVolume01);
                limiterPostStage.LimiterOnly = true;
                limiterPostStage.IsEnabled = false;
                truePeakLimiterPostStage.IsEnabled = loudnessConfig.TruePeakLimiterEnabled;
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

                long consumedSourceSamples = primarySource != null
                    ? primarySource.CumulativeSourceSamples - prevSourceSamples
                    : 0L;
                if (consumedSourceSamples > 0) {
                    if (isRenderingLiveNeuralSources && primarySource.Clip != null &&
                        primarySource.Clip.SampleRate > 0 && primarySource.Clip.SampleRate != currentTrack.SampleRate) {
                        currentTrackSampleExact += consumedSourceSamples * (currentTrack.SampleRate / (double)primarySource.Clip.SampleRate);
                    } else {
                        currentTrackSampleExact += consumedSourceSamples;
                    }
                } else {
                    double sampleAdvance = blockFrames * (currentTrack.SampleRate / (double)Math.Max(1, listener.SampleRate));
                    currentTrackSampleExact += sampleAdvance;
                }
                Volatile.Write(ref currentTrackSample, (long)Math.Round(currentTrackSampleExact));
                totalFramesWritten += blockFrames;

                // Throttle heavier RMS/HUD telemetry & diagnostics snapshot allocations to ~20 Hz (50 ms) instead of 187-750 Hz
                // so 96/192 kHz RT playback stays zero-GC on the hot audio thread, while keeping NormalizedPosition block-accurate.
                int telemetryIntervalFrames = Math.Max(blockFrames, listener.SampleRate / 20);
                telemetryFramesSincePublish += blockFrames;
                if (telemetryFramesSincePublish >= telemetryIntervalFrames) {
                    telemetryFramesSincePublish = 0;
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
                } else {
                    SyncSourceNormalizedPositionsLocked();
                }
            }
        return totalFramesWritten;
    }

    int telemetryFramesSincePublish = int.MaxValue / 2;

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

    bool IsProgressiveWindowAheadOfPlayheadLocked(NeuralSeparatedTrack sep) {
        if (sep == null || !sep.IsProgressiveStreaming) {
            return true;
        }
        int trackSr = currentTrack != null && currentTrack.SampleRate > 0 ? currentTrack.SampleRate : Listener.DefaultSampleRate;
        int stemSr = sep.SampleRate > 0 ? sep.SampleRate : trackSr;
        long stemPlayhead = (long)Math.Round(currentTrackSample * (stemSr / (double)trackSr));
        int safetyMargin = Math.Max(stemSr / 4, updateRate * framesPerChunk * 2);
        return stemPlayhead >= 0 && (stemPlayhead + safetyMargin) <= sep.ReadySampleCount;
    }

    bool ShouldRenderLiveNeuralSourcesLocked() {
        NeuralSpatialUpmixMode mode = neuralStemStage.Config.SpatialUpmixMode;
        if (mode != NeuralSpatialUpmixMode.Stems3DObjects && mode != NeuralSpatialUpmixMode.HybridNeuralDirAc) {
            return false;
        }
        // W47 Invariant: NEVER destroy native multichannel beds or moving Atmos 3D objects by replacing them with
        // static stereo neural stems. Live neural stem spatialization applies exclusively to mono/stereo sources.
        if (currentTrack == null || currentTrack.Renderer == null ||
            currentTrack.Renderer.HasObjects ||
            currentTrack.Renderer.Objects.Count > 2 ||
            (currentTrack.Reader != null && currentTrack.Reader.ChannelCount > 2)) {
            return false;
        }
        NeuralSeparatedTrack sep = neuralStemStage.ActiveSpatialUpmixTrack;
        if (sep == null || sep.Stems == null || sep.Stems.Count == 0 ||
            !NeuralStemSpatializer.IsFullMixSpatialStemSet(sep.Stems)) {
            return false;
        }
        return IsProgressiveWindowAheadOfPlayheadLocked(sep);
    }

    void MixRecordedPerformancePreviewZeroGc(
        PerformanceRecordingTake take,
        float[] stereoOutput,
        int outOffset,
        int blockFrames) {
        if (take == null || take.MonoPcm == null || take.MonoPcm.Length == 0 || blockFrames <= 0) {
            return;
        }
        int takeSr = take.SampleRate > 0 ? take.SampleRate : listener.SampleRate;
        int trackSr = currentTrack != null && currentTrack.SampleRate > 0 ? currentTrack.SampleRate : listener.SampleRate;
        double startTakePos = (currentTrackSample - take.StartTrackSample) * (takeSr / (double)trackSr);
        double step = takeSr / (double)Math.Max(1, listener.SampleRate);
        int takeLen = take.MonoPcm.Length;
        if (startTakePos + blockFrames * step <= 0.0 || startTakePos >= takeLen) {
            return;
        }

        int count = Math.Min(blockFrames, previewVocalScratch.Length / 2);
        bool anySample = false;
        for (int f = 0; f < count; f++) {
            double pos = startTakePos + f * step;
            float s = 0f;
            if (pos >= 0.0 && pos < takeLen - 1) {
                int idx = (int)pos;
                float frac = (float)(pos - idx);
                s = take.MonoPcm[idx] * (1f - frac) + take.MonoPcm[idx + 1] * frac;
                anySample = true;
            } else if (pos >= takeLen - 1 && pos < takeLen) {
                s = take.MonoPcm[takeLen - 1];
                anySample = true;
            }
            previewVocalScratch[f * 2] = s;
            previewVocalScratch[f * 2 + 1] = s;
        }
        if (!anySample) {
            return;
        }
        vocalMixMatchStage.EnsureSampleRate(listener.SampleRate);
        vocalMixMatchStage.ProcessLiveVocalZeroGc(previewVocalScratch, 0, count, stereoOutput, outOffset);
        for (int f = 0; f < count; f++) {
            int dst = outOffset + f * 2;
            stereoOutput[dst] += previewVocalScratch[f * 2];
            stereoOutput[dst + 1] += previewVocalScratch[f * 2 + 1];
        }
    }

}
