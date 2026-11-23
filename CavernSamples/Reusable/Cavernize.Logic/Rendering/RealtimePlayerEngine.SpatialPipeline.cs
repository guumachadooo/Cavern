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
            RebuildCachedActiveSourcesLocked();
        } else {
            bool wasRenderingLiveNeural = isRenderingLiveNeuralSources;
            isRenderingLiveNeuralSources = false;
            progressiveNeuralUpdater = null;
            liveNeuralSources = Array.Empty<Source>();
            if (resetToStart) {
                currentTrack.Attach(listener, upmixingSettings);
            } else {
                if (wasRenderingLiveNeural) {
                    currentTrack.Seek(currentTrackSample);
                }
                currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
            }
            RebuildCachedActiveSourcesLocked();
            if (wasRenderingLiveNeural) {
                sourceItems = TrackSourceItem.BuildFromAttachedSources(currentTrack, cachedActiveSources);
                BindTrackToSceneHubLocked(currentTrack);
                SpatialSourceDescriptor[] stemDescs = neuralStemStage.ActiveSpatialUpmixTrack != null
                    ? neuralStemStage.BuildActiveStemDescriptors(EnvironmentSize, sceneHub.Sources.Count)
                    : Array.Empty<SpatialSourceDescriptor>();
                sceneHub.RegisterNeuralStemDescriptors(stemDescs);
            }
            parametricUpmixerStage.BindTelemetryProvider(currentTrack.ActiveTelemetryProvider);
        }

        SetupOutputLayout();
    }

    void AttachLiveNeuralSourcesLocked(bool resetToStart) {
        NeuralSeparatedTrack sep = neuralStemStage.ActiveSpatialUpmixTrack;
        NeuralSpatialUpmixMode mode = neuralStemStage.Config.SpatialUpmixMode;
        bool isProgressive = sep != null && sep.IsProgressiveStreaming;
        Vector3 envSize = EnvironmentSize.X > 0.1f && EnvironmentSize.Y > 0.1f && EnvironmentSize.Z > 0.1f
            ? EnvironmentSize
            : new Vector3(10f, 7f, 10f);

        // Preserve any existing user Solo/Mute/CustomPin overrides matched by source name across re-attaches
        Dictionary<string, TrackSourceItem> prevByName = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceItems.Count; i++) {
            prevByName[sourceItems[i].Name] = sourceItems[i];
        }

        int trackSr = currentTrack != null && currentTrack.SampleRate > 0 ? currentTrack.SampleRate : Listener.DefaultSampleRate;
        int stemSr = sep != null && sep.SampleRate > 0 ? sep.SampleRate : trackSr;
        long startSample = resetToStart
            ? 0L
            : (long)Math.Round(currentTrackSample * (stemSr / (double)trackSr));

        List<Source> builtSources = [];
        List<TrackSourceItem> builtItems = [];
        List<SpatialSourceDescriptor> builtDescriptors = [];

        if (sep == null || sep.Stems == null || sep.Stems.Count == 0) {
            progressiveNeuralUpdater = null;
            isRenderingLiveNeuralSources = false;
            liveNeuralSources = Array.Empty<Source>();
            if (resetToStart) {
                currentTrack.Attach(listener, upmixingSettings);
            } else {
                currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
            }
            return;
        }

        // While progressive streaming is active, run live incremental Stems3DObjects (never run full-track
        // HybridNeuralDirAc over unfinalized zeros); seamlessly upgrade to 7.1.4 Hybrid DirAC on final bind.
        if (mode == NeuralSpatialUpmixMode.HybridNeuralDirAc && !isProgressive) {
            progressiveNeuralUpdater = null;
            if (!ReferenceEquals(cachedHybridTrack, sep) || cachedHybrid714Buses == null || cachedHybrid714Buses.Length != 12) {
                StereoStemBuffer[] stemArr = new StereoStemBuffer[sep.Stems.Count];
                for (int i = 0; i < sep.Stems.Count; i++) {
                    stemArr[i] = sep.Stems[i];
                }
                float heightExp = Math.Clamp(upmixingSettings.Effect * 0.5f, 0.10f, 0.65f);
                // W48: Shared canonical 12-channel 7.1.4 builder with complementary 120 Hz LR4 ScreenLFE crossover
                cachedHybrid714Buses = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stemArr, stemSr, heightExp, 120f);
                cachedHybridTrack = sep;
            }

            // Canonical ITU/SMPTE/Cavern Layout714 order matching BuildHybrid714WithDiscreteLfe 1:1
            ReferenceChannel[] layout714 = [
                ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                ReferenceChannel.ScreenLFE,
                ReferenceChannel.RearLeft, ReferenceChannel.RearRight, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight, ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
            ];
            for (int b = 0; b < cachedHybrid714Buses.Length && b < layout714.Length; b++) {
                float[] pcm = cachedHybrid714Buses[b];
                if (pcm == null || pcm.Length == 0) {
                    continue;
                }
                ReferenceChannel ch = layout714[b];
                bool isLfe = ch == ReferenceChannel.ScreenLFE;
                Vector3 normPos = ChannelPrototype.AlternativePositions[(int)ch];
                bool isHeight = b >= 8 && !isLfe;
                SourceLayer layer = isHeight ? SourceLayer.VirtualHeight : SourceLayer.NeuralStem;
                string label = isLfe ? "AI Hybrid LFE (.1)" : $"AI Hybrid {ch.GetShortName()}";
                int idx = builtSources.Count;

                Source src = new() {
                    Clip = new Clip(new MultichannelWaveform(pcm), stemSr),
                    Position = normPos * envSize,
                    TimeSamples = (int)Math.Clamp(startSample, 0, Math.Max(0, pcm.Length - 1)),
                    Loop = Loop,
                    LFE = isLfe,
                    Volume = 1f
                };
                TrackSourceItem item = new(idx, label, isBed: isLfe, isLfe: isLfe, ch, normPos, layer);
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
                    IsLfe = isLfe,
                    IsDialogue = ch == ReferenceChannel.FrontCenter,
                    NormalizedPosition = item.NormalizedPosition,
                    WorldPositionMeters = item.NormalizedPosition * envSize,
                    HasCustomPosition = item.HasCustomPosition,
                    CustomNormalizedPosition = item.CustomNormalizedPosition,
                    Gain = 1f,
                    Diffuse = isHeight ? 0.45f : (isLfe ? 0.05f : 0.15f),
                    Importance = ch == ReferenceChannel.FrontCenter ? 1f : (isLfe ? 0.9f : 0.8f)
                });
            }
        } else {
            // Stems3DObjects: place each separated neural stem directly as live 3D object Sources in Listener
            // plus a dedicated 120 Hz LR4 ScreenLFE (.1) crossover channel summing BOTH Bass and Drums sub-bass
            // (with automatic fallback to Instrumental/Other/Residual when neither Bass nor Drums is present).
            SpatialSourceDescriptor[] stemDescs = neuralStemStage.BuildActiveStemDescriptors(envSize, 0);
            int descCursor = 0;
            const float invSqrt2 = 0.70710678f;
            int maxStemLen = 0;
            bool hasBassOrDrums = false;
            for (int s = 0; s < sep.Stems.Count; s++) {
                StereoStemBuffer st = sep.Stems[s];
                if (st == null || st.Length <= 0) {
                    continue;
                }
                if (st.Length > maxStemLen) {
                    maxStemLen = st.Length;
                }
                NeuralStemRole r = NeuralStemSpatializer.ClassifyStemName(st.Name);
                if (r == NeuralStemRole.Bass || r == NeuralStemRole.Drums) {
                    hasBassOrDrums = true;
                }
            }
            float[] dedicatedLfePcm = maxStemLen > 0 ? new float[maxStemLen] : null;
            bool hasPopulatedLfe = false;
            progressiveNeuralUpdater = isProgressive ? new ProgressiveNeuralStemUpdater(sep, dedicatedLfePcm) : null;

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
                    if (isProgressive && progressiveNeuralUpdater != null) {
                        if (role == NeuralStemRole.Bass && dedicatedLfePcm != null) {
                            progressiveNeuralUpdater.AddMonoJob(stem, mono, new StatefulLr4Crossover(stemSr, 120f), lfeWeight: 0.70710678f);
                            hasPopulatedLfe = true;
                        } else {
                            progressiveNeuralUpdater.AddMonoJob(stem, mono);
                        }
                    } else {
                        for (int n = 0; n < stem.Length; n++) {
                            mono[n] = (stem.Left[n] + stem.Right[n]) * invSqrt2;
                        }
                        if (role == NeuralStemRole.Bass && dedicatedLfePcm != null) {
                            NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(mono, stemSr, 120f, out float[] upperBass, out float[] subLfe);
                            mono = upperBass;
                            int m = Math.Min(dedicatedLfePcm.Length, subLfe.Length);
                            for (int n = 0; n < m; n++) {
                                dedicatedLfePcm[n] += subLfe[n] * invSqrt2;
                            }
                            hasPopulatedLfe = true;
                        }
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
                    float[] leftSrc = stem.Left;
                    float[] rightSrc = stem.Right;
                    bool routeSubToLfe = dedicatedLfePcm != null && (
                        role == NeuralStemRole.Drums ||
                        (!hasBassOrDrums && (role == NeuralStemRole.Instrumental || role == NeuralStemRole.Other || role == NeuralStemRole.Residual)));
                    if (routeSubToLfe) {
                        if (isProgressive && progressiveNeuralUpdater != null) {
                            float[] hpL = new float[stem.Length];
                            float[] hpR = new float[stem.Length];
                            progressiveNeuralUpdater.AddStereoLfeJob(stem, hpL, hpR, stemSr, lfeWeight: 0.5f);
                            leftSrc = hpL;
                            rightSrc = hpR;
                            hasPopulatedLfe = true;
                        } else {
                            NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(stem.Left, stemSr, 120f, out float[] hpL, out float[] lpL);
                            NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(stem.Right, stemSr, 120f, out float[] hpR, out float[] lpR);
                            leftSrc = hpL;
                            rightSrc = hpR;
                            int m = Math.Min(dedicatedLfePcm.Length, stem.Length);
                            for (int n = 0; n < m; n++) {
                                dedicatedLfePcm[n] += (lpL[n] + lpR[n]) * 0.5f;
                            }
                            hasPopulatedLfe = true;
                        }
                    }
                    for (int side = 0; side < 2; side++) {
                        SpatialSourceDescriptor d = descCursor < stemDescs.Length
                            ? stemDescs[descCursor++]
                            : new SpatialSourceDescriptor {
                                Name = $"AI {stem.Name} {(side == 0 ? "L" : "R")}",
                                Layer = layer,
                                NormalizedPosition = new Vector3(side == 0 ? -0.75f : 0.75f, 0.2f, 0.4f)
                            };
                        float[] pcm = side == 0 ? leftSrc : rightSrc;
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

            if (hasPopulatedLfe && dedicatedLfePcm != null && dedicatedLfePcm.Length > 0) {
                Vector3 lfeNormPos = ChannelPrototype.AlternativePositions[(int)ReferenceChannel.ScreenLFE];
                int lfeIdx = builtSources.Count;
                const string lfeLabel = "AI LFE (.1)";
                Source lfeSrc = new() {
                    Clip = new Clip(new MultichannelWaveform(dedicatedLfePcm), stemSr),
                    Position = lfeNormPos * envSize,
                    TimeSamples = (int)Math.Clamp(startSample, 0, Math.Max(0, dedicatedLfePcm.Length - 1)),
                    Loop = Loop,
                    LFE = true,
                    Volume = 1f
                };
                TrackSourceItem lfeItem = new(lfeIdx, lfeLabel, isBed: true, isLfe: true, ReferenceChannel.ScreenLFE, lfeNormPos, SourceLayer.NeuralStem);
                if (prevByName.TryGetValue(lfeLabel, out TrackSourceItem oldLfe)) {
                    lfeItem.IsMuted = oldLfe.IsMuted;
                    lfeItem.IsSolo = oldLfe.IsSolo;
                }
                builtSources.Add(lfeSrc);
                builtItems.Add(lfeItem);
                builtDescriptors.Add(new SpatialSourceDescriptor {
                    Index = lfeIdx,
                    Name = lfeLabel,
                    Layer = SourceLayer.NeuralStem,
                    BedChannel = ReferenceChannel.ScreenLFE,
                    IsLfe = true,
                    NormalizedPosition = lfeNormPos,
                    WorldPositionMeters = lfeNormPos * envSize,
                    Gain = 1f,
                    Diffuse = 0.05f,
                    Importance = 0.9f
                });
            }
        }

        if (builtSources.Count == 0) {
            isRenderingLiveNeuralSources = false;
            progressiveNeuralUpdater = null;
            liveNeuralSources = Array.Empty<Source>();
            if (resetToStart) {
                currentTrack.Attach(listener, upmixingSettings);
            } else {
                currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
            }
            return;
        }

        if (progressiveNeuralUpdater != null) {
            progressiveNeuralUpdater.AdvanceTo(sep.ReadySampleCount);
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

    static void SplitLinkwitzRiley4Lfe(float[] input, int sampleRate, float cutoffHz, out float[] highPass, out float[] lowPass) =>
        NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(input, sampleRate, cutoffHz, out highPass, out lowPass);

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
        if (currentTrack != null && currentTrack.SampleRate >= 8000) {
            neuralStemStage.SetSourceTrackSampleRate(currentTrack.SampleRate);
        }
        neuralStemStage.Configure(listener.SampleRate, sceneHub.ProgramMetadata);
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
            (proAudioConfig.Enabled && proAudioConfig.LockedSampleRateHz > 0) ||
            PreferredHardwareSampleRate == Listener.DefaultSampleRate;
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

        if (flatSources.Length == sourceItems.Count && flatSources.Length > 0) {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                TrackSourceItem item = sourceItems[idx];
                if (item.HasCustomPosition) {
                    src.LockCustomPosition(item.CustomNormalizedPosition * Listener.EnvironmentSize);
                } else if (src.PositionLocked) {
                    src.UnlockCustomPosition(item.DefaultNormalizedPosition * Listener.EnvironmentSize);
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
                    maskHash ^= 1L << ((idx % 56) + 6);
                }
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        } else if (activeLayers != null && flatSources.Length == activeLayers.Length && sourceItems.Count == activeObjects.Count) {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                if (idx < sourceItems.Count && sourceItems[idx].HasCustomPosition) {
                    src.LockCustomPosition(sourceItems[idx].CustomNormalizedPosition * Listener.EnvironmentSize);
                } else if (src.PositionLocked) {
                    if (idx < sourceItems.Count) {
                        src.UnlockCustomPosition(sourceItems[idx].DefaultNormalizedPosition * Listener.EnvironmentSize);
                    } else {
                        src.UnlockCustomPosition();
                    }
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
        } else {
            for (int idx = 0; idx < flatSources.Length; idx++) {
                Source src = flatSources[idx];
                bool hasItem = idx < sourceItems.Count;
                if (hasItem && sourceItems[idx].HasCustomPosition) {
                    src.LockCustomPosition(sourceItems[idx].CustomNormalizedPosition * Listener.EnvironmentSize);
                } else if (src.PositionLocked) {
                    if (hasItem) {
                        src.UnlockCustomPosition(sourceItems[idx].DefaultNormalizedPosition * Listener.EnvironmentSize);
                    } else {
                        src.UnlockCustomPosition();
                    }
                }
                Vector3 rawPos = src.Position * invEnv;
                bool isBedPos = hasItem
                    ? sourceItems[idx].IsBed
                    : (MathF.Abs(rawPos.X) % 1 < .01f && MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f);
                bool dynFiltered = DynamicCeilingOnly && rawPos.Y <= 0.15f;
                bool mute = (hasItem && sourceItems[idx].IsMuted) ||
                    (anySolo && (!hasItem || !sourceItems[idx].IsSolo)) ||
                    (MuteBed && isBedPos) ||
                    (OnlyBeds && !isBedPos) ||
                    (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) ||
                    dynFiltered;
                if (hasItem) {
                    sourceItems[idx].IsDynamicallyFilteredOut = dynFiltered;
                }
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

}
