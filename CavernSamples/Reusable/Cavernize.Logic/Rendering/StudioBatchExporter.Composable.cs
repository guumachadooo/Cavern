using System.Numerics;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Environment;
using Cavern.Neural;
using Cavern.Remapping;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Cavern.Waveforms;

using Cavernize.Logic.Models;
using Cavernize.Logic.Spatial;

namespace Cavernize.Logic.Rendering;

public static partial class StudioBatchExporter {
    static StudioBatchExportResult ExportComposable(
        CavernizeTrack track,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        bool isNativeMulti = IsNativeMultichannelOrAtmos(track);
        bool hasRecordedVocal = opts.IncludeRecordedPerformance && job.LiveMicMonoCapture != null && job.LiveMicMonoCapture.Length > 0;
        bool wantsEnvContainer = opts.BaseFormat == StudioBaseOutputFormat.DolbyAtmosAdm ||
                                 opts.BaseFormat == StudioBaseOutputFormat.EclipsaIamf ||
                                 opts.BaseFormat == StudioBaseOutputFormat.Ambisonics360;

        SpatialChoreoMode effectiveChoreoMode = ResolveDynamicChoreoMode(job);
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        bool canRunNeuralStems = job.NeuralOrchestrator != null &&
                                 job.NeuralRuntime != null &&
                                 job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                                 stemPlan != null &&
                                 stemPlan.IsRunnable;
        bool needsNeuralStemUpmix = (opts.EnableAiSpatialUpmix || effectiveChoreoMode != SpatialChoreoMode.Off) &&
                                    !isNativeMulti &&
                                    canRunNeuralStems;
        bool canRunEmMwf = opts.EnableLosslessTwinRestoration &&
                           isNativeMulti &&
                           canRunNeuralStems &&
                           job.LosslessTwinProvider is UniversalLosslessTwinProvider twinForEm &&
                           twinForEm.HasSyncedLosslessTwin;

        // Zero-full-track streaming fast path for ADM BWF / IAMF / Ambisonics 360 whenever Karaoke & EM-MWF & stereo neural stems aren't needed
        if (wantsEnvContainer && !opts.EnableKaraokeInstrumental && !canRunEmMwf && !needsNeuralStemUpmix) {
            Codec envCodec = opts.BaseFormat switch {
                StudioBaseOutputFormat.EclipsaIamf => Codec.IAMF,
                StudioBaseOutputFormat.Ambisonics360 => Codec.AmbisonicsHOA,
                _ => job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF
            };
            UpmixingSettings envUpmix = (opts.EnableAiSpatialUpmix || effectiveChoreoMode != SpatialChoreoMode.Off)
                ? new UpmixingSettings(true) { AtmosAware = true, ParametricStft = true, Cavernize = true, MatrixUpmixing = true }
                : new UpmixingSettings(false);
            Source vocalObj = hasRecordedVocal
                ? BuildProcessedPerformanceSource(job, opts, sampleRate, Math.Max(updateRate, (int)Math.Min(track.Length, int.MaxValue / 4)))
                : null;
            string summary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems: false, usedEmMwf: false, hasRecordedVocal, effectiveChoreoMode);
            return ExportEnvironmental(track, job, envCodec, outPath, summary, onProgress, token, envUpmix, vocalObj);
        }

        // Zero-full-track streaming fast path for Headphone 3D when Karaoke / EM-MWF / neural stem upmix aren't needed
        if (opts.BaseFormat == StudioBaseOutputFormat.Headphone3D &&
            !opts.EnableAiSpatialUpmix &&
            !opts.EnableKaraokeInstrumental &&
            !hasRecordedVocal &&
            !canRunEmMwf &&
            !needsNeuralStemUpmix) {
            StudioBatchExportJob cleanJob = opts.ApplyPersonalizedHeadphonePost
                ? job
                : new StudioBatchExportJob {
                    TrackName = job.TrackName,
                    Preset = StudioExportPreset.PersonalizedLosslessBinaural,
                    OutputPath = outPath,
                    BitDepth = job.BitDepth,
                    TruePeakCeilingDbtp = job.TruePeakCeilingDbtp,
                    CompressionTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Headphone3D),
                    FfmpegExecutablePath = ResolveFfmpegPath(job),
                    ChoreoTrack = ResolveChoreoTrack(job),
                    DynamicChoreoMode = effectiveChoreoMode,
                    LyricMoodDigests = ResolveLyricMoodDigests(job),
                    Metadata = ResolveMetadata(job),
                    OnSigpipeShield = ResolveSigpipeShield(job),
                    PostProcessingStages = null
                };
            return ExportPersonalizedBinaural(track, cleanJob, outPath, onProgress, token);
        }

        // Zero-full-track streaming fast path for Cinema 7.1.4 (and Headphone 3D w/ DSP upmix or recorded vocal) without Karaoke/EM-MWF/NeuralStems
        if ((opts.BaseFormat == StudioBaseOutputFormat.Cinema714 || opts.BaseFormat == StudioBaseOutputFormat.Headphone3D) &&
            !opts.EnableKaraokeInstrumental &&
            !canRunEmMwf &&
            !needsNeuralStemUpmix) {
            return ExportComposableStreamingDirect(track, job, opts, isNativeMulti, hasRecordedVocal, outPath, onProgress, token);
        }

        // Unified Composable Multichannel Pipeline (only used when Karaoke, EM-MWF, or Neural Stems require offline buffer transforms)
        ReferenceChannel[] activeLayout;
        float[][] channels;
        float[] refStereoL;
        float[] refStereoR;
        bool usedNeuralStems = false;
        bool usedEmMwf = false;

        if (canRunEmMwf) {
            UniversalLosslessTwinProvider syncedTwin = (UniversalLosslessTwinProvider)job.LosslessTwinProvider;
            float[][] mastered = null;
            ReferenceChannel[] masteredLayout = null;
            string tempDir = Path.GetDirectoryName(outPath) ?? ".";
            string tempUnused = Path.Combine(tempDir, $".cavern_emmwf_tmp_{Guid.NewGuid():N}.wav");
            try {
                StemExtractor.ExtractLosslessHybridSpatialMaster(
                    track,
                    syncedTwin,
                    job.NeuralRuntime,
                    stemModelPath: null,
                    stemModelEntry: null,
                    outputDir: tempDir,
                    bits: job.BitDepth,
                    neuralConfig: safeNeural,
                    lgsrConfig: (job.LgsrConfig ?? LgsrConfig.Default).Sanitize(),
                    tier3RestorerModelPath: null,
                    tier3RestorerEntry: null,
                    exportResidualTrack: false,
                    onProgress: (p, _) => onProgress?.Invoke(p * 0.50),
                    token: token,
                    orchestrator: job.NeuralOrchestrator,
                    onSeparatedTrackReady: job.OnSeparatedTrackReady,
                    combinedMasterOutputPath: tempUnused,
                    onMasterChannelsReady: (chs, lay) => {
                        mastered = chs;
                        masteredLayout = lay;
                    });
            } finally {
                try { if (File.Exists(tempUnused)) File.Delete(tempUnused); } catch { }
            }
            activeLayout = masteredLayout ?? Layout714;
            channels = mastered ?? Array.Empty<float[]>();
            BuildStereoFolddown(channels, activeLayout, out refStereoL, out refStereoR);
            usedEmMwf = true;
            usedNeuralStems = true;
        } else if (needsNeuralStemUpmix) {
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out bool fromTwin, p => onProgress?.Invoke(p * 0.15), token);
            (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
            (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                track.Path ?? job.TrackName,
                fileSize,
                mtimeTicks,
                inL,
                inR,
                sampleRate,
                NeuralPipelineGoal.StemSeparation,
                safeNeural,
                fromTwin,
                (p, _) => onProgress?.Invoke(0.15 + p * 0.35),
                token);
            job.OnSeparatedTrackReady?.Invoke(separated);
            StereoStemBuffer[] stems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
            separated = null;
            if (stems.Length > 0) {
                // T5 True Dynamic Moving Atmos Object Choreography: preserve semantic neural stems as moving 3D objects
                // paired with a stationary discrete 120 Hz LR4 .1 LFE bed instead of flattening to static 12ch speaker beds!
                if (effectiveChoreoMode != SpatialChoreoMode.Off) {
                    inL = null;
                    inR = null;
                    return ExportSemanticMovingObjects(
                        stems,
                        sampleRate,
                        job,
                        opts,
                        effectiveChoreoMode,
                        isNativeMulti,
                        hasRecordedVocal,
                        outPath,
                        p => onProgress?.Invoke(0.50 + p * 0.50),
                        token);
                }

                activeLayout = Layout714;
                channels = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sampleRate, 0.35f, 120f);
                stems = null;
                refStereoL = opts.EnableKaraokeInstrumental ? inL : null;
                refStereoR = opts.EnableKaraokeInstrumental ? inR : null;
                usedNeuralStems = true;
            } else {
                inL = null;
                inR = null;
                activeLayout = Layout714;
                DecodeMultichannelLayout(track, Layout714, out channels, out _, out refStereoL, out refStereoR, p => onProgress?.Invoke(0.15 + p * 0.35), token,
                    new UpmixingSettings(true) { Cavernize = true, MatrixUpmixing = true, ParametricStft = true, AtmosAware = true });
            }
            inL = null;
            inR = null;
        } else if (opts.EnableAiSpatialUpmix || opts.BaseFormat == StudioBaseOutputFormat.Cinema714 || isNativeMulti) {
            activeLayout = opts.BaseFormat == StudioBaseOutputFormat.Cinema714 || opts.EnableAiSpatialUpmix
                ? Layout714
                : ResolveExportChannelLayout(track, TrackSourceItem.BuildFromTrack(track).Count);
            UpmixingSettings upmix = opts.EnableAiSpatialUpmix
                ? new UpmixingSettings(true) { Cavernize = true, MatrixUpmixing = true, ParametricStft = true, AtmosAware = true }
                : new UpmixingSettings(false);
            DecodeMultichannelLayout(track, activeLayout, out channels, out _, out refStereoL, out refStereoR, p => onProgress?.Invoke(p * 0.45), token, upmix);
        } else {
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out _, p => onProgress?.Invoke(p * 0.45), token);
            activeLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
            channels = [inL, inR];
            refStereoL = inL;
            refStereoR = inR;
        }

        int totalSamples = channels.Length > 0 && channels[0] != null ? channels[0].Length : 0;
        if (totalSamples <= 0) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                CompressionTier = ResolveEffectiveTier(job, opts.BaseFormat),
                Succeeded = false,
                ErrorMessage = "No audio frames decoded for Studio export."
            };
        }

        // Step 2: Optional Spatial Karaoke vocal attenuation (preserves backing vocals & discrete LFE)
        if (opts.EnableKaraokeInstrumental) {
            bool[] isLfe = new bool[activeLayout.Length];
            bool[] isStrictCenter = new bool[activeLayout.Length];
            for (int c = 0; c < activeLayout.Length; c++) {
                isLfe[c] = activeLayout[c] == ReferenceChannel.ScreenLFE;
                isStrictCenter[c] = activeLayout[c] == ReferenceChannel.FrontCenter;
            }
            float[] leadRefL = null;
            float[] leadRefR = null;
            float[] backingRef = null;
            NeuralExecutionPlan karaokePlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, safeNeural);
            if (job.NeuralOrchestrator != null &&
                job.NeuralRuntime != null &&
                job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                karaokePlan != null &&
                karaokePlan.IsRunnable) {
                (float[] nL, float[] nR, bool fromTwin) = ResolveNeuralStereoInput(job, refStereoL, refStereoR, totalSamples);
                refStereoL = null;
                refStereoR = null;
                (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
                (NeuralSeparatedTrack kSep, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                    track.Path ?? job.TrackName,
                    fileSize,
                    mtimeTicks,
                    nL,
                    nR,
                    sampleRate,
                    NeuralPipelineGoal.SpatialKaraoke,
                    safeNeural,
                    fromTwin,
                    (p, _) => onProgress?.Invoke(0.50 + p * 0.15),
                    token);
                nL = null;
                nR = null;
                job.OnSeparatedTrackReady?.Invoke(kSep);
                ResolveStereoLeadVocalRefs(kSep, out leadRefL, out leadRefR);
                backingRef = job.PreserveBackingVocals ? kSep?.MonoBackingVocalRef : null;
                kSep = null;
            }
            channels = SpatialKaraokeEngine.ProcessOfflineMultichannel(
                channels,
                isLfe,
                leadRefL,
                backingRef,
                sampleRate,
                Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
                job.PreserveBackingVocals,
                neuralLeadVocalRefRight: leadRefR,
                isStrictCenter: isStrictCenter);
            leadRefL = null;
            leadRefR = null;
            backingRef = null;
        } else {
            refStereoL = null;
            refStereoR = null;
        }

        // Step 3: Optional Recorded Vocal Performance mixdown through W11 Live Vocal Chain + sidechain pocket
        if (hasRecordedVocal) {
            MixRecordedPerformanceIntoMultichannelInPlace(channels, activeLayout, job, opts, sampleRate, token);
        }

        string formatSummary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems, usedEmMwf, hasRecordedVocal, effectiveChoreoMode);

        // Step 4: Render into the chosen commercial Base Output Format
        if (opts.BaseFormat == StudioBaseOutputFormat.Cinema714) {
            StudioCompressionTier tier714 = ResolveEffectiveTier(job, StudioBaseOutputFormat.Cinema714);
            LoudnessAnalysisSnapshot snap714 = WriteMeteredLimitedMultichannelStream(
                outPath,
                activeLayout,
                channels,
                sampleRate,
                job.BitDepth,
                job.TruePeakCeilingDbtp,
                tier714,
                ResolveFfmpegPath(job),
                ResolveSigpipeShield(job),
                p => onProgress?.Invoke(0.70 + p * 0.30),
                token,
                releaseChannelsAfterStream: true,
                metadata: ResolveMetadata(job));
            channels = null;
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = formatSummary,
                ChannelCount = activeLayout.Length,
                SampleRate = sampleRate,
                DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                IntegratedLufs = ResolveMeasuredLufs(snap714),
                LoudnessRangeLu = snap714.LoudnessRangeLu,
                MaxTruePeakDbtp = snap714.MaxTruePeakDbtp,
                CompressionTier = tier714,
                OutputBytes = SafeFileLength(outPath),
                Succeeded = true
            };
        }

        if (opts.BaseFormat == StudioBaseOutputFormat.Headphone3D) {
            return RenderMultichannelToBinauralStream(
                channels,
                activeLayout,
                sampleRate,
                job,
                opts.ApplyPersonalizedHeadphonePost,
                outPath,
                formatSummary,
                p => onProgress?.Invoke(0.70 + p * 0.30),
                token);
        }

        // ADM BWF / IAMF / Ambisonics 360 from composed spatial buses (each channel placed at its ITU 3D speaker anchor)
        Codec outCodec = opts.BaseFormat switch {
            StudioBaseOutputFormat.EclipsaIamf => Codec.IAMF,
            StudioBaseOutputFormat.Ambisonics360 => Codec.AmbisonicsHOA,
            _ => job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF
        };
        return RenderMultichannelToEnvironmentalContainer(
            channels,
            activeLayout,
            sampleRate,
            job,
            outCodec,
            outPath,
            formatSummary,
            p => onProgress?.Invoke(0.70 + p * 0.30),
            token);
    }

    static StudioBatchExportResult ExportComposableStreamingDirect(
        CavernizeTrack track,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        bool isNativeMulti,
        bool hasRecordedVocal,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        bool isHeadphone = opts.BaseFormat == StudioBaseOutputFormat.Headphone3D;
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, opts.BaseFormat);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);
        SpatialChoreoMode effectiveChoreoMode = ResolveDynamicChoreoMode(job);

        using IDisposable virtScope = isHeadphone ? VirtualizerFilter.BeginStaticStateScope() : null;
        EnsureEnvironmentSize();
        ReferenceChannel[] targetLayout;
        int outSampleRate;
        if (isHeadphone) {
            new VirtualizerFilter().SetLayout();
            Listener.HeadphoneVirtualizer = true;
            outSampleRate = VirtualizerFilter.FilterSampleRate;
            targetLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
        } else {
            Listener.HeadphoneVirtualizer = false;
            targetLayout = Layout714;
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(targetLayout));
            outSampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        }

        Listener listener = new(false) {
            SampleRate = outSampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };
        UpmixingSettings upmix = (isHeadphone || opts.EnableAiSpatialUpmix || effectiveChoreoMode != SpatialChoreoMode.Off)
            ? new UpmixingSettings(true) { Cavernize = true, MatrixUpmixing = true, ParametricStft = true, AtmosAware = true }
            : new UpmixingSettings(false);
        track.Attach(listener, upmix);
        List<Source> activeSources = new(listener.ActiveSources);

        int outCh = targetLayout.Length;
        int stride = Math.Max(outCh, Listener.Channels.Length);
        double rateRatio = isHeadphone ? (outSampleRate / (double)Math.Max(1, track.SampleRate)) : 1.0;
        long totalSamples = Math.Max(updateRate, (long)Math.Round(Math.Max(updateRate, track.Length) * rateRatio));
        SpatialChoreoTrack choreoTrack = ResolveOrBuildChoreoTrack(job, null, outSampleRate, totalSamples / (double)Math.Max(1, outSampleRate));
        ChoreoSourceMapper choreoMapper = new(activeSources, choreoTrack);

        List<IPostProcessingStage> activePostStages = isHeadphone && opts.ApplyPersonalizedHeadphonePost
            ? CloneOfflinePostStages(job.PostProcessingStages, outSampleRate, outCh)
            : [];

        LoudnessMeter meter = new(outSampleRate, outCh);
        if (!isHeadphone) {
            double[] weights = new double[outCh];
            for (int c = 0; c < outCh; c++) {
                weights[c] = targetLayout[c] == ReferenceChannel.ScreenLFE ? 0.0 : 1.0;
            }
            meter.Configure(outSampleRate, outCh, weights);
        }
        TruePeakLimiter limiter = new(outSampleRate, outCh, job.TruePeakCeilingDbtp);
        float[] blockOut = new float[updateRate * outCh];
        SpatialSceneFrame stageContext = new() {
            SampleRate = outSampleRate,
            FrameCount = updateRate,
            Metadata = SpatialProgramMetadata.CreateDefault()
        };

        LiveVocalChainDsp vocalDsp = hasRecordedVocal ? new LiveVocalChainDsp(outSampleRate) : null;
        vocalDsp?.Configure(outSampleRate, job.VocalDnaProfile, new VocalMacroSettings { Enabled = true });
        float[] vocalStereo = hasRecordedVocal ? new float[updateRate * 2] : null;
        float[] backingStereo = hasRecordedVocal ? new float[updateRate * 2] : null;
        int idxFl = Array.IndexOf(targetLayout, ReferenceChannel.FrontLeft);
        int idxFr = Array.IndexOf(targetLayout, ReferenceChannel.FrontRight);
        int idxFc = Array.IndexOf(targetLayout, ReferenceChannel.FrontCenter);
        if (idxFl < 0) idxFl = 0;
        if (idxFr < 0) idxFr = Math.Min(1, outCh - 1);

        long written = 0;
        try {
            using IStreamingExportSink sink = CreateStreamingSink(
                outPath,
                targetLayout,
                outCh,
                outSampleRate,
                job.BitDepth,
                totalSamples,
                effectiveTier,
                ffmpegPath,
                onSigpipe,
                isAmbisonics: false,
                metadata: ResolveMetadata(job));

            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)outSampleRate);
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalSamples - written);
                for (int s = 0; s < frames; s++) {
                    int srcBase = s * stride;
                    int dstBase = s * outCh;
                    for (int c = 0; c < outCh; c++) {
                        blockOut[dstBase + c] = rendered[srcBase + c];
                    }
                }

                if (vocalDsp != null) {
                    FillResampledMicStereoChunk(
                        job.LiveMicMonoCapture,
                        opts.RecordedPerformanceSampleRate,
                        outSampleRate,
                        opts.RecordedPerformanceStartTrackSample,
                        written,
                        frames,
                        vocalStereo);
                    for (int s = 0; s < frames; s++) {
                        int b = s * outCh;
                        backingStereo[s * 2] = blockOut[b + idxFl];
                        backingStereo[s * 2 + 1] = blockOut[b + idxFr];
                    }
                    vocalDsp.ProcessStereoBlockZeroGc(vocalStereo, 0, frames, backingStereo, 0);
                    for (int s = 0; s < frames; s++) {
                        int b = s * outCh;
                        blockOut[b + idxFl] = backingStereo[s * 2];
                        blockOut[b + idxFr] = backingStereo[s * 2 + 1];
                        float vL = vocalStereo[s * 2];
                        float vR = vocalStereo[s * 2 + 1];
                        if (idxFc >= 0 && idxFc < outCh) {
                            float mid = 0.70710678f * (vL + vR);
                            float side = 0.5f * (vL - vR);
                            blockOut[b + idxFc] += mid;
                            blockOut[b + idxFl] += side;
                            blockOut[b + idxFr] -= side;
                        } else {
                            blockOut[b + idxFl] += vL;
                            blockOut[b + idxFr] += vR;
                        }
                    }
                }

                if (activePostStages.Count > 0) {
                    stageContext.FrameCount = frames;
                    for (int i = 0; i < activePostStages.Count; i++) {
                        activePostStages[i].ProcessInterleaved(blockOut, 0, frames, outCh, stageContext);
                    }
                }

                limiter.ProcessInterleaved(blockOut, 0, frames, outCh);
                meter.AnalyzeInterleaved(blockOut, 0, frames, outCh);
                sink.WriteInterleavedBlock(blockOut, frames);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        } finally {
            DisposePostStages(activePostStages);
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems: false, usedEmMwf: false, hasRecordedVocal, effectiveChoreoMode),
            ChannelCount = outCh,
            SampleRate = outSampleRate,
            DurationSeconds = written / (double)Math.Max(1, outSampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            EffectiveChoreoMode = effectiveChoreoMode,
            Succeeded = true
        };
    }

    static List<IPostProcessingStage> CloneOfflinePostStages(IReadOnlyList<IPostProcessingStage> stages, int sampleRate, int channels) {
        List<IPostProcessingStage> active = [];
        if (stages == null) {
            return active;
        }
        for (int i = 0; i < stages.Count; i++) {
            IPostProcessingStage stage = stages[i];
            if (stage == null || !stage.IsEnabled) {
                continue;
            }
            if (string.Equals(stage.StageId, "true-peak-limiter-4x", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stage.StageId, "true-peak-limiter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stage.StageId, "cavern-soft-knee-limiter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stage.StageId, "limiter", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            active.Add(stage.CloneForOfflineExport());
        }
        active.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        for (int i = 0; i < active.Count; i++) {
            active[i].Configure(sampleRate, channels);
            active[i].Reset();
        }
        return active;
    }

    static void DisposePostStages(List<IPostProcessingStage> stages) {
        for (int i = 0; i < stages.Count; i++) {
            try {
                stages[i].Dispose();
            } catch {
                // Best-effort cleanup
            }
        }
    }

    static void FillResampledMicStereoChunk(
        float[] rawMono,
        int takeSampleRate,
        int targetSampleRate,
        long startTrackSample,
        long dstOffset,
        int frames,
        float[] vocalStereo) {
        Array.Clear(vocalStereo, 0, frames * 2);
        if (rawMono == null || rawMono.Length == 0 || frames <= 0) {
            return;
        }
        int srcSr = takeSampleRate >= 8000 ? takeSampleRate : targetSampleRate;
        double step = srcSr / (double)Math.Max(1, targetSampleRate);
        int maxSrc = rawMono.Length;
        for (int s = 0; s < frames; s++) {
            long dst = dstOffset + s;
            if (dst < startTrackSample) {
                continue;
            }
            double srcPos = (dst - startTrackSample) * step;
            if (srcPos >= maxSrc) {
                break;
            }
            int idx = (int)srcPos;
            float frac = (float)(srcPos - idx);
            float s0 = rawMono[idx];
            float s1 = idx + 1 < maxSrc ? rawMono[idx + 1] : s0;
            float v = s0 + (s1 - s0) * frac;
            vocalStereo[s * 2] = v;
            vocalStereo[s * 2 + 1] = v;
        }
    }

    static string BuildComposableSummary(
        StudioComposableExportOptions opts,
        bool isNativeMulti,
        bool usedNeuralStems,
        bool usedEmMwf,
        bool hasRecordedVocal,
        SpatialChoreoMode choreoMode = SpatialChoreoMode.Off) {
        string baseName = opts.BaseFormat switch {
            StudioBaseOutputFormat.Headphone3D => "3D Headphones",
            StudioBaseOutputFormat.Cinema714 => "Cinema 7.1.4",
            StudioBaseOutputFormat.DolbyAtmosAdm => "Dolby Atmos Master",
            StudioBaseOutputFormat.EclipsaIamf => "Eclipsa Audio 3D",
            StudioBaseOutputFormat.Ambisonics360 => "360° Ambisonics",
            _ => "Spatial Master"
        };
        List<string> mods = [];
        if (opts.EnableAiSpatialUpmix || usedNeuralStems) {
            mods.Add(isNativeMulti ? "Atmos-Safe AI" : (usedNeuralStems ? "AI 3D Upmix" : "3D Spatial Upmix"));
        }
        if (choreoMode != SpatialChoreoMode.Off) {
            mods.Add($"Motion {choreoMode}");
        }
        if (opts.EnableLosslessTwinRestoration) {
            mods.Add(usedEmMwf ? "Lossless EM-MWF" : "Lossless Twin");
        }
        if (opts.EnableKaraokeInstrumental) {
            mods.Add("Karaoke");
        }
        if (hasRecordedVocal) {
            mods.Add("Vocal Take");
        }
        return mods.Count > 0 ? $"{baseName} + {string.Join(" + ", mods)}" : baseName;
    }

    static Source BuildProcessedPerformanceSource(
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        int targetSampleRate,
        int totalSamples) {
        float[] aligned = AlignAndResamplePerformanceMono(job.LiveMicMonoCapture, opts.RecordedPerformanceSampleRate, targetSampleRate, opts.RecordedPerformanceStartTrackSample, totalSamples);
        LiveVocalChainDsp vocalDsp = new(targetSampleRate);
        vocalDsp.Configure(targetSampleRate, job.VocalDnaProfile, new VocalMacroSettings { Enabled = true });
        float[] stereoBuf = new float[updateRate * 2];
        for (int pos = 0; pos < totalSamples; pos += updateRate) {
            int frames = Math.Min(updateRate, totalSamples - pos);
            for (int s = 0; s < frames; s++) {
                float v = aligned[pos + s];
                stereoBuf[s * 2] = v;
                stereoBuf[s * 2 + 1] = v;
            }
            vocalDsp.ProcessStereoBlockZeroGc(stereoBuf, 0, frames, null, 0);
            for (int s = 0; s < frames; s++) {
                aligned[pos + s] = 0.5f * (stereoBuf[s * 2] + stereoBuf[s * 2 + 1]);
            }
        }
        EnsureEnvironmentSize();
        return new Source {
            Clip = new Clip(new MultichannelWaveform(aligned), targetSampleRate),
            Position = new Vector3(0f, 0.08f, 1.0f) * Listener.EnvironmentSize,
            Volume = 1f
        };
    }

    static float[] AlignAndResamplePerformanceMono(
        float[] rawMono,
        int takeSampleRate,
        int targetSampleRate,
        long startTrackSample,
        int totalSamples) {
        float[] aligned = new float[Math.Max(0, totalSamples)];
        if (rawMono == null || rawMono.Length == 0 || totalSamples <= 0) {
            return aligned;
        }
        int srcSr = takeSampleRate >= 8000 ? takeSampleRate : targetSampleRate;
        double step = srcSr / (double)Math.Max(1, targetSampleRate);
        int startDst = (int)Math.Clamp(startTrackSample, 0L, totalSamples);
        int maxSrc = rawMono.Length;
        for (int dst = startDst; dst < totalSamples; dst++) {
            double srcPos = (dst - startDst) * step;
            if (srcPos >= maxSrc) {
                break;
            }
            int idx = (int)srcPos;
            float frac = (float)(srcPos - idx);
            float s0 = rawMono[idx];
            float s1 = idx + 1 < maxSrc ? rawMono[idx + 1] : s0;
            aligned[dst] = s0 + (s1 - s0) * frac;
        }
        return aligned;
    }

    static void MixRecordedPerformanceIntoMultichannelInPlace(
        float[][] channels,
        ReferenceChannel[] layout,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        int sampleRate,
        CancellationToken token) {
        if (channels == null || channels.Length == 0 || channels[0] == null) {
            return;
        }
        int totalSamples = channels[0].Length;
        int idxFl = Array.IndexOf(layout, ReferenceChannel.FrontLeft);
        int idxFr = Array.IndexOf(layout, ReferenceChannel.FrontRight);
        int idxFc = Array.IndexOf(layout, ReferenceChannel.FrontCenter);
        if (idxFl < 0) idxFl = 0;
        if (idxFr < 0) idxFr = Math.Min(1, channels.Length - 1);

        LiveVocalChainDsp vocalDsp = new(sampleRate);
        vocalDsp.Configure(sampleRate, job.VocalDnaProfile, new VocalMacroSettings { Enabled = true });
        float[] vocalStereo = new float[updateRate * 2];
        float[] backingStereo = new float[updateRate * 2];

        for (int pos = 0; pos < totalSamples; pos += updateRate) {
            token.ThrowIfCancellationRequested();
            int frames = Math.Min(updateRate, totalSamples - pos);
            FillResampledMicStereoChunk(
                job.LiveMicMonoCapture,
                opts.RecordedPerformanceSampleRate,
                sampleRate,
                opts.RecordedPerformanceStartTrackSample,
                pos,
                frames,
                vocalStereo);
            for (int s = 0; s < frames; s++) {
                backingStereo[s * 2] = channels[idxFl][pos + s];
                backingStereo[s * 2 + 1] = channels[idxFr][pos + s];
            }
            vocalDsp.ProcessStereoBlockZeroGc(vocalStereo, 0, frames, backingStereo, 0);
            for (int s = 0; s < frames; s++) {
                channels[idxFl][pos + s] = backingStereo[s * 2];
                channels[idxFr][pos + s] = backingStereo[s * 2 + 1];
                float vL = vocalStereo[s * 2];
                float vR = vocalStereo[s * 2 + 1];
                if (idxFc >= 0 && idxFc < channels.Length) {
                    float mid = 0.70710678f * (vL + vR);
                    float side = 0.5f * (vL - vR);
                    channels[idxFc][pos + s] += mid;
                    channels[idxFl][pos + s] += side;
                    channels[idxFr][pos + s] -= side;
                } else {
                    channels[idxFl][pos + s] += vL;
                    channels[idxFr][pos + s] += vR;
                }
            }
        }
    }

    static void BuildStereoFolddown(float[][] channels, ReferenceChannel[] layout, out float[] mixL, out float[] mixR) {
        int len = channels != null && channels.Length > 0 && channels[0] != null ? channels[0].Length : 0;
        mixL = new float[len];
        mixR = new float[len];
        if (len == 0) {
            return;
        }
        int chCount = Math.Min(channels.Length, layout.Length);
        for (int s = 0; s < len; s++) {
            float l = 0f, r = 0f;
            for (int c = 0; c < chCount; c++) {
                if (layout[c] == ReferenceChannel.ScreenLFE) continue;
                float v = channels[c][s];
                ReferenceChannel rc = layout[c];
                if (rc == ReferenceChannel.FrontCenter || rc == ReferenceChannel.RearCenter) {
                    l += v * 0.70710678f;
                    r += v * 0.70710678f;
                } else if (rc == ReferenceChannel.FrontLeft || rc == ReferenceChannel.SideLeft ||
                           rc == ReferenceChannel.RearLeft || rc == ReferenceChannel.TopFrontLeft ||
                           rc == ReferenceChannel.TopRearLeft || rc == ReferenceChannel.TopSideLeft) {
                    l += v;
                } else if (rc == ReferenceChannel.FrontRight || rc == ReferenceChannel.SideRight ||
                           rc == ReferenceChannel.RearRight || rc == ReferenceChannel.TopFrontRight ||
                           rc == ReferenceChannel.TopRearRight || rc == ReferenceChannel.TopSideRight) {
                    r += v;
                } else {
                    l += v * 0.5f;
                    r += v * 0.5f;
                }
            }
            mixL[s] = Math.Clamp(l, -1f, 1f);
            mixR[s] = Math.Clamp(r, -1f, 1f);
        }
    }

    static StudioBatchExportResult RenderMultichannelToBinauralStream(
        float[][] channels,
        ReferenceChannel[] layout,
        int sourceSampleRate,
        StudioBatchExportJob job,
        bool applyPostStages,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token) {
        using IDisposable virtScope = VirtualizerFilter.BeginStaticStateScope();
        EnsureEnvironmentSize();
        new VirtualizerFilter().SetLayout();
        Listener.HeadphoneVirtualizer = true;
        int outSampleRate = VirtualizerFilter.FilterSampleRate;
        Listener listener = new(false) {
            SampleRate = outSampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };

        int chCount = Math.Min(channels.Length, layout.Length);
        int srcLen = chCount > 0 && channels[0] != null ? channels[0].Length : 0;
        for (int c = 0; c < chCount; c++) {
            ReferenceChannel ch = layout[c];
            Vector3 normPos = ChannelPrototype.AlternativePositions[(int)ch];
            listener.AttachSource(new Source {
                Clip = new Clip(new MultichannelWaveform(channels[c]), sourceSampleRate),
                Position = normPos * Listener.EnvironmentSize,
                LFE = ch == ReferenceChannel.ScreenLFE,
                Volume = 1f
            });
        }

        List<Source> activeSources = new(listener.ActiveSources);
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Headphone3D);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);

        int stride = Math.Max(2, Listener.Channels.Length);
        double rateRatio = outSampleRate / (double)Math.Max(1, sourceSampleRate);
        long totalSamples = Math.Max(updateRate, (long)Math.Round(Math.Max(updateRate, srcLen) * rateRatio));
        ChoreoSourceMapper choreoMapper = new(activeSources, ResolveOrBuildChoreoTrack(job, null, outSampleRate, totalSamples / (double)Math.Max(1, outSampleRate)));

        List<IPostProcessingStage> activePostStages = applyPostStages
            ? CloneOfflinePostStages(job.PostProcessingStages, outSampleRate, 2)
            : [];

        LoudnessMeter meter = new(outSampleRate, 2);
        TruePeakLimiter limiter = new(outSampleRate, 2, job.TruePeakCeilingDbtp);
        float[] binauralStereo = new float[updateRate * 2];
        SpatialSceneFrame stageContext = new() {
            SampleRate = outSampleRate,
            FrameCount = updateRate,
            Metadata = SpatialProgramMetadata.CreateDefault()
        };

        long written = 0;
        ReferenceChannel[] stereoLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
        try {
            using IStreamingExportSink sink = CreateStreamingSink(
                outPath,
                stereoLayout,
                2,
                outSampleRate,
                job.BitDepth,
                totalSamples,
                effectiveTier,
                ffmpegPath,
                onSigpipe,
                isAmbisonics: false,
                metadata: ResolveMetadata(job));
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)outSampleRate);
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalSamples - written);
                for (int s = 0; s < frames; s++) {
                    binauralStereo[s * 2] = rendered[s * stride];
                    binauralStereo[s * 2 + 1] = rendered[s * stride + 1];
                }
                stageContext.FrameCount = frames;
                for (int i = 0; i < activePostStages.Count; i++) {
                    activePostStages[i].ProcessInterleaved(binauralStereo, 0, frames, 2, stageContext);
                }
                limiter.ProcessInterleaved(binauralStereo, 0, frames, 2);
                meter.AnalyzeInterleaved(binauralStereo, 0, frames, 2);
                sink.WriteInterleavedBlock(binauralStereo, frames);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        } finally {
            DisposePostStages(activePostStages);
            for (int c = 0; c < chCount; c++) {
                channels[c] = null;
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = 2,
            SampleRate = outSampleRate,
            DurationSeconds = written / (double)Math.Max(1, outSampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            Succeeded = true
        };
    }

    static StudioBatchExportResult RenderMultichannelToEnvironmentalContainer(
        float[][] channels,
        ReferenceChannel[] layout,
        int sampleRate,
        StudioBatchExportJob job,
        Codec codec,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };
        int chCount = Math.Min(channels.Length, layout.Length);
        int totalSamples = chCount > 0 && channels[0] != null ? channels[0].Length : updateRate;
        for (int c = 0; c < chCount; c++) {
            ReferenceChannel ch = layout[c];
            listener.AttachSource(new Source {
                Clip = new Clip(new MultichannelWaveform(channels[c]), sampleRate),
                Position = ChannelPrototype.AlternativePositions[(int)ch] * Listener.EnvironmentSize,
                LFE = ch == ReferenceChannel.ScreenLFE,
                Volume = 1f
            });
        }

        List<Source> activeSources = new(listener.ActiveSources);
        ChoreoSourceMapper choreoMapper = new(activeSources, ResolveOrBuildChoreoTrack(job, null, sampleRate, totalSamples / (double)Math.Max(1, sampleRate)));
        StudioBaseOutputFormat baseFmt = codec switch {
            Codec.IAMF => StudioBaseOutputFormat.EclipsaIamf,
            Codec.AmbisonicsHOA => StudioBaseOutputFormat.Ambisonics360,
            _ => StudioBaseOutputFormat.DolbyAtmosAdm
        };
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, baseFmt);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);

        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        int outCh = codec == Codec.AmbisonicsHOA || codec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, chCount);

        LoudnessAnalysisSnapshot measured = ApplyTruePeakLimiterAndMeasureInPlace(channels, layout, sampleRate, job.TruePeakCeilingDbtp, token);

        if (codec == Codec.AmbisonicsHOA && effectiveTier != StudioCompressionTier.MasterPcm) {
            FfmpegPipeCodec hoaPipe = effectiveTier == StudioCompressionTier.PortableLossy ? FfmpegPipeCodec.Opus : FfmpegPipeCodec.WavPack;
            StudioBatchExportResult pipeRes = ExportHoaStreamingPipe(
                listener,
                activeSources,
                choreoMapper,
                totalSamples,
                sampleRate,
                hoaOrder,
                outCh,
                job,
                effectiveTier,
                ffmpegPath,
                onSigpipe,
                hoaPipe,
                outPath,
                formatSummary,
                onProgress,
                token);
            for (int c = 0; c < chCount; c++) {
                channels[c] = null;
            }
            return pipeRes;
        }

        IamfCodecMode iamfCodecMode = effectiveTier switch {
            StudioCompressionTier.LosslessCompressed => IamfCodecMode.Flac,
            StudioCompressionTier.PortableLossy => IamfCodecMode.Opus,
            _ => IamfCodecMode.Ipcm
        };

        try {
            using EnvironmentWriter writer = codec switch {
                Codec.IAMF => new IamfEnvironmentWriter(outPath, listener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName, codecMode: iamfCodecMode),
                Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, listener, totalSamples, job.BitDepth, hoaOrder),
                _ => EnvironmentWriter.Create(outPath, codec, listener, totalSamples, job.BitDepth, renderer: null)
            };

            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            long written = 0;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)sampleRate);
                writer.WriteNextFrame();
                written += Math.Min(updateRate, totalSamples - written);
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        } finally {
            for (int c = 0; c < chCount; c++) {
                channels[c] = null;
            }
        }

        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = outCh,
            SampleRate = sampleRate,
            DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(measured),
            LoudnessRangeLu = measured.LoudnessRangeLu,
            MaxTruePeakDbtp = measured.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            EffectiveChoreoMode = ResolveDynamicChoreoMode(job),
            Succeeded = true
        };
    }

    /// <summary>
    /// T5 True Semantic Moving Atmos Object Renderer:
    /// Keeps neural stems as individual moving 3D objects mapped 1:1 to <see cref="SpatialChoreoTrack"/>
    /// (producing real time-varying ITU-R BS.2076 <c>ADMBlockFormat</c> keyframes in ADM BWF, dynamic HOA in IAMF/AMB,
    /// and moving 3D stage audio in Headphone 3D / Cinema 7.1.4) while anchoring sub-120 Hz bass/kick into a
    /// stationary discrete 4th-order Linkwitz-Riley <c>.1</c> LFE bed (0.0 ms drift).
    /// </summary>
    static StudioBatchExportResult ExportSemanticMovingObjects(
        StereoStemBuffer[] stems,
        int sampleRate,
        StudioBatchExportJob job,
        StudioComposableExportOptions opts,
        SpatialChoreoMode choreoMode,
        bool isNativeMulti,
        bool hasRecordedVocal,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        int totalSamples = 0;
        for (int i = 0; i < stems.Length; i++) {
            if (stems[i] != null && stems[i].Length > totalSamples) {
                totalSamples = stems[i].Length;
            }
        }
        if (totalSamples <= 0) {
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                CompressionTier = ResolveEffectiveTier(job, opts.BaseFormat),
                Succeeded = false,
                ErrorMessage = "No neural stem samples available for dynamic Atmos export."
            };
        }

        double durationSec = totalSamples / (double)Math.Max(1, sampleRate);
        SpatialChoreoTrack choreoTrack = ResolveOrBuildChoreoTrack(job, stems, sampleRate, durationSec);
        string formatSummary = BuildComposableSummary(opts, isNativeMulti, usedNeuralStems: true, usedEmMwf: false, hasRecordedVocal, choreoMode);

        // Extract stationary 120 Hz LR4 discrete LFE bed from Bass + Drums while high-passing their mains cleanly
        float[] lfeBed = new float[totalSamples];
        List<Source> objectSources = [];
        List<int> stemIndices = [];
        List<float> sideSigns = [];

        EnsureEnvironmentSize();
        Vector3 env = Listener.EnvironmentSize;
        float karaokeLeadGain = opts.EnableKaraokeInstrumental ? Math.Clamp(job.KaraokeVocalLevel, 0f, 1f) : 1f;
        float karaokeBackGain = opts.EnableKaraokeInstrumental && !job.PreserveBackingVocals ? karaokeLeadGain : 1f;

        for (int i = 0; i < stems.Length; i++) {
            StereoStemBuffer st = stems[i];
            if (st == null || st.Length <= 0 || st.Left == null || st.Right == null) {
                continue;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
            float stemGain = role is NeuralStemRole.LeadVocal or NeuralStemRole.Vocals
                ? karaokeLeadGain
                : (role == NeuralStemRole.BackingVocals ? karaokeBackGain : 1f);
            if (stemGain <= 0.0001f) {
                continue;
            }

            int len = Math.Min(totalSamples, st.Length);
            bool monoCenter = role is NeuralStemRole.LeadVocal or NeuralStemRole.Vocals or NeuralStemRole.Bass;
            if (monoCenter) {
                float[] mono = new float[totalSamples];
                for (int n = 0; n < len; n++) {
                    mono[n] = Math.Clamp(0.5f * (st.Left[n] + st.Right[n]) * stemGain, -1f, 1f);
                }
                if (role == NeuralStemRole.Bass) {
                    NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(mono, sampleRate, 120f, out float[] hpBass, out float[] lpBass);
                    for (int n = 0; n < totalSamples; n++) {
                        lfeBed[n] += lpBass[n] * 0.75f;
                    }
                    mono = hpBass;
                }
                NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(null, SpatialChoreoMode.Off, i, role, 0f, 0.0, out Vector3 anchor);
                objectSources.Add(new Source {
                    Clip = new Clip(new MultichannelWaveform(mono), sampleRate),
                    Position = anchor * env,
                    LFE = false,
                    Volume = 1f
                });
                stemIndices.Add(i);
                sideSigns.Add(0f);
            } else {
                float[] leftBus = new float[totalSamples];
                float[] rightBus = new float[totalSamples];
                for (int n = 0; n < len; n++) {
                    leftBus[n] = Math.Clamp(st.Left[n] * stemGain, -1f, 1f);
                    rightBus[n] = Math.Clamp(st.Right[n] * stemGain, -1f, 1f);
                }
                if (role == NeuralStemRole.Drums) {
                    NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(leftBus, sampleRate, 120f, out float[] hpL, out float[] lpL);
                    NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(rightBus, sampleRate, 120f, out float[] hpR, out float[] lpR);
                    for (int n = 0; n < totalSamples; n++) {
                        lfeBed[n] += 0.35f * (lpL[n] + lpR[n]);
                    }
                    leftBus = hpL;
                    rightBus = hpR;
                }

                NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(null, SpatialChoreoMode.Off, i, role, -1f, 0.0, out Vector3 anchorL);
                objectSources.Add(new Source {
                    Clip = new Clip(new MultichannelWaveform(leftBus), sampleRate),
                    Position = anchorL * env,
                    LFE = false,
                    Volume = 1f
                });
                stemIndices.Add(i);
                sideSigns.Add(-1f);

                NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(null, SpatialChoreoMode.Off, i, role, 1f, 0.0, out Vector3 anchorR);
                objectSources.Add(new Source {
                    Clip = new Clip(new MultichannelWaveform(rightBus), sampleRate),
                    Position = anchorR * env,
                    LFE = false,
                    Volume = 1f
                });
                stemIndices.Add(i);
                sideSigns.Add(1f);
            }
        }

        // Always attach the stationary discrete 120 Hz LR4 .1 LFE bed locked in place
        for (int n = 0; n < totalSamples; n++) {
            lfeBed[n] = Math.Clamp(lfeBed[n], -1f, 1f);
        }
        objectSources.Insert(0, new Source {
            Clip = new Clip(new MultichannelWaveform(lfeBed), sampleRate),
            Position = ChannelPrototype.AlternativePositions[(int)ReferenceChannel.ScreenLFE] * env,
            LFE = true,
            Volume = 1f
        });
        stemIndices.Insert(0, -1);
        sideSigns.Insert(0, 0f);

        if (hasRecordedVocal) {
            Source vocalObj = BuildProcessedPerformanceSource(job, opts, sampleRate, totalSamples);
            if (vocalObj != null) {
                objectSources.Add(vocalObj);
                stemIndices.Add(-1);
                sideSigns.Add(0f);
            }
        }

        int[] explicitMap = stemIndices.ToArray();
        float[] explicitSigns = sideSigns.ToArray();
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, opts.BaseFormat);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);

        // Route 1: Headphone 3D (Binaural) or Cinema 7.1.4 (12ch) rendered directly from moving semantic objects
        if (opts.BaseFormat == StudioBaseOutputFormat.Headphone3D || opts.BaseFormat == StudioBaseOutputFormat.Cinema714) {
            bool isHeadphone = opts.BaseFormat == StudioBaseOutputFormat.Headphone3D;
            using IDisposable virtScope = isHeadphone ? VirtualizerFilter.BeginStaticStateScope() : null;
            EnsureEnvironmentSize();
            ReferenceChannel[] targetLayout;
            int outSampleRate;
            if (isHeadphone) {
                new VirtualizerFilter().SetLayout();
                Listener.HeadphoneVirtualizer = true;
                outSampleRate = VirtualizerFilter.FilterSampleRate;
                targetLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
            } else {
                Listener.HeadphoneVirtualizer = false;
                targetLayout = Layout714;
                Listener.ReplaceChannels(ChannelPrototype.ToLayout(targetLayout));
                outSampleRate = sampleRate;
            }

            Listener listener = new(false) {
                SampleRate = outSampleRate,
                UpdateRate = updateRate,
                AudioQuality = QualityModes.Perfect,
                MaximumSources = 256,
                LFESeparation = true,
                Normalizer = 0f
            };
            for (int i = 0; i < objectSources.Count; i++) {
                listener.AttachSource(objectSources[i]);
            }
            List<Source> activeSources = new(listener.ActiveSources);
            ChoreoSourceMapper choreoMapper = new(activeSources, choreoTrack, explicitMap, explicitSigns);

            int outCh = targetLayout.Length;
            int stride = Math.Max(outCh, Listener.Channels.Length);
            double rateRatio = outSampleRate / (double)Math.Max(1, sampleRate);
            long outTotalSamples = Math.Max(updateRate, (long)Math.Round(totalSamples * rateRatio));

            List<IPostProcessingStage> activePostStages = isHeadphone && opts.ApplyPersonalizedHeadphonePost
                ? CloneOfflinePostStages(job.PostProcessingStages, outSampleRate, outCh)
                : [];
            LoudnessMeter meter = new(outSampleRate, outCh);
            if (!isHeadphone) {
                double[] weights = new double[outCh];
                for (int c = 0; c < outCh; c++) {
                    weights[c] = targetLayout[c] == ReferenceChannel.ScreenLFE ? 0.0 : 1.0;
                }
                meter.Configure(outSampleRate, outCh, weights);
            }
            TruePeakLimiter limiter = new(outSampleRate, outCh, job.TruePeakCeilingDbtp);
            float[] blockOut = new float[updateRate * outCh];
            SpatialSceneFrame stageContext = new() {
                SampleRate = outSampleRate,
                FrameCount = updateRate,
                Metadata = SpatialProgramMetadata.CreateDefault()
            };

            long written = 0;
            try {
                using IStreamingExportSink sink = CreateStreamingSink(
                    outPath,
                    targetLayout,
                    outCh,
                    outSampleRate,
                    job.BitDepth,
                    outTotalSamples,
                    effectiveTier,
                    ffmpegPath,
                    onSigpipe,
                    isAmbisonics: false,
                    metadata: ResolveMetadata(job));
                long totalFrames = (outTotalSamples + updateRate - 1) / updateRate;
                for (long f = 0; f < totalFrames; f++) {
                    token.ThrowIfCancellationRequested();
                    choreoMapper.ApplyFrame(activeSources, written / (double)outSampleRate);
                    float[] rendered = listener.Render(1);
                    int frames = (int)Math.Min(updateRate, outTotalSamples - written);
                    for (int s = 0; s < frames; s++) {
                        int srcBase = s * stride;
                        int dstBase = s * outCh;
                        for (int c = 0; c < outCh; c++) {
                            blockOut[dstBase + c] = rendered[srcBase + c];
                        }
                    }
                    if (activePostStages.Count > 0) {
                        stageContext.FrameCount = frames;
                        for (int i = 0; i < activePostStages.Count; i++) {
                            activePostStages[i].ProcessInterleaved(blockOut, 0, frames, outCh, stageContext);
                        }
                    }
                    limiter.ProcessInterleaved(blockOut, 0, frames, outCh);
                    meter.AnalyzeInterleaved(blockOut, 0, frames, outCh);
                    sink.WriteInterleavedBlock(blockOut, frames);
                    written += frames;
                    if ((f & 15) == 0 || f == totalFrames - 1) {
                        onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                    }
                }
            } finally {
                DisposePostStages(activePostStages);
            }

            LoudnessAnalysisSnapshot snap = meter.Snapshot;
            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = formatSummary,
                ChannelCount = outCh,
                SampleRate = outSampleRate,
                DurationSeconds = written / (double)Math.Max(1, outSampleRate),
                IntegratedLufs = ResolveMeasuredLufs(snap),
                LoudnessRangeLu = snap.LoudnessRangeLu,
                MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
                CompressionTier = effectiveTier,
                OutputBytes = SafeFileLength(outPath),
                EffectiveChoreoMode = choreoMode,
                Succeeded = true
            };
        }

        // Route 2: Environmental Spatial Container (ADM BWF w/ true moving ITU-R BS.2076 objects, Eclipsa IAMF, 360° HOA)
        Codec envCodec = opts.BaseFormat switch {
            StudioBaseOutputFormat.EclipsaIamf => Codec.IAMF,
            StudioBaseOutputFormat.Ambisonics360 => Codec.AmbisonicsHOA,
            _ => job.UseDolbyAtmosAdmSubset ? Codec.ADM_BWF_Atmos : Codec.ADM_BWF
        };
        Listener.HeadphoneVirtualizer = false;
        Listener envListener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            LFESeparation = true,
            Normalizer = 0f
        };
        for (int i = 0; i < objectSources.Count; i++) {
            envListener.AttachSource(objectSources[i]);
        }
        List<Source> envSources = new(envListener.ActiveSources);
        ChoreoSourceMapper envChoreoMapper = new(envSources, choreoTrack, explicitMap, explicitSigns);

        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        int envOutCh = envCodec == Codec.AmbisonicsHOA || envCodec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, envSources.Count);

        if (envCodec == Codec.AmbisonicsHOA && effectiveTier != StudioCompressionTier.MasterPcm) {
            FfmpegPipeCodec hoaPipe = effectiveTier == StudioCompressionTier.PortableLossy ? FfmpegPipeCodec.Opus : FfmpegPipeCodec.WavPack;
            return ExportHoaStreamingPipe(
                envListener,
                envSources,
                envChoreoMapper,
                totalSamples,
                sampleRate,
                hoaOrder,
                envOutCh,
                job,
                effectiveTier,
                ffmpegPath,
                onSigpipe,
                hoaPipe,
                outPath,
                formatSummary,
                onProgress,
                token);
        }

        IamfCodecMode envIamfCodec = effectiveTier switch {
            StudioCompressionTier.LosslessCompressed => IamfCodecMode.Flac,
            StudioCompressionTier.PortableLossy => IamfCodecMode.Opus,
            _ => IamfCodecMode.Ipcm
        };

        int meterCh = envSources.Count > 0 && envSources.Count <= 16 ? envSources.Count : 2;
        LoudnessMeter envMeter = new(sampleRate, meterCh);
        if (meterCh == envSources.Count) {
            double[] weights = new double[meterCh];
            for (int c = 0; c < meterCh; c++) {
                weights[c] = envSources[c].LFE ? 0.0 : 1.0;
            }
            envMeter.Configure(sampleRate, meterCh, weights);
        }
        float[] envMeterBuf = new float[updateRate * meterCh];
        long envWritten = 0;

        using (EnvironmentWriter writer = envCodec switch {
            Codec.IAMF => new IamfEnvironmentWriter(outPath, envListener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName, codecMode: envIamfCodec),
            Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, envListener, totalSamples, job.BitDepth, hoaOrder),
            _ => EnvironmentWriter.Create(outPath, envCodec, envListener, totalSamples, job.BitDepth, renderer: null)
        }) {
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                envChoreoMapper.ApplyFrame(envSources, envWritten / (double)sampleRate);
                writer.WriteNextFrame();

                int frames = (int)Math.Min(updateRate, totalSamples - envWritten);
                if (frames > 0 && envSources.Count > 0) {
                    Array.Clear(envMeterBuf, 0, frames * meterCh);
                    if (meterCh == envSources.Count) {
                        for (int c = 0; c < meterCh; c++) {
                            float[] srcSamples = envSources[c].Rendered?[0];
                            if (srcSamples == null) continue;
                            int lim = Math.Min(frames, srcSamples.Length);
                            for (int s = 0; s < lim; s++) {
                                envMeterBuf[s * meterCh + c] = srcSamples[s];
                            }
                        }
                    } else {
                        float foldNorm = 1f / MathF.Sqrt(Math.Max(1, envSources.Count));
                        for (int c = 0; c < envSources.Count; c++) {
                            if (envSources[c].LFE) continue;
                            float[] srcSamples = envSources[c].Rendered?[0];
                            if (srcSamples == null) continue;
                            int lim = Math.Min(frames, srcSamples.Length);
                            for (int s = 0; s < lim; s++) {
                                float v = srcSamples[s] * foldNorm;
                                envMeterBuf[s * 2] += v;
                                envMeterBuf[s * 2 + 1] += v;
                            }
                        }
                    }
                    envMeter.AnalyzeInterleaved(envMeterBuf, 0, frames, meterCh);
                }

                envWritten += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        }

        LoudnessAnalysisSnapshot envSnap = envMeter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = envOutCh,
            SampleRate = sampleRate,
            DurationSeconds = envWritten / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(envSnap),
            LoudnessRangeLu = envSnap.LoudnessRangeLu,
            MaxTruePeakDbtp = envSnap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            EffectiveChoreoMode = choreoMode,
            Succeeded = true
        };
    }
}
