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
    static StudioBatchExportResult ExportEnvironmental(
        CavernizeTrack track,
        StudioBatchExportJob job,
        Codec codec,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token,
        UpmixingSettings upmixOverride = null,
        Source extraVocalSource = null) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, upmixOverride ?? new UpmixingSettings(false));
        if (extraVocalSource != null) {
            listener.AttachSource(extraVocalSource);
        }

        StudioBaseOutputFormat baseFmt = codec switch {
            Codec.IAMF => StudioBaseOutputFormat.EclipsaIamf,
            Codec.AmbisonicsHOA => StudioBaseOutputFormat.Ambisonics360,
            _ => StudioBaseOutputFormat.DolbyAtmosAdm
        };
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, baseFmt);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);
        long totalSamples = Math.Max(updateRate, track.Length);
        SpatialChoreoTrack activeChoreo = ResolveOrBuildChoreoTrack(job, null, sampleRate, totalSamples / (double)Math.Max(1, sampleRate));
        ChoreoSourceMapper choreoMapper = new(listener.ActiveSources, activeChoreo);

        int hoaOrder = Math.Clamp(job.HoaOrder, 1, 3);
        List<Source> activeSources = new(listener.ActiveSources);
        int srcCount = activeSources.Count;
        int outCh = codec == Codec.AmbisonicsHOA || codec == Codec.IAMF
            ? (hoaOrder + 1) * (hoaOrder + 1)
            : Math.Max(1, srcCount);

        bool streamHoaPipe = codec == Codec.AmbisonicsHOA && effectiveTier != StudioCompressionTier.MasterPcm;
        bool tryIamfFfmpeg = codec == Codec.IAMF &&
                             effectiveTier != StudioCompressionTier.MasterPcm &&
                             !string.IsNullOrWhiteSpace(ffmpegPath) &&
                             File.Exists(ffmpegPath);

        if (streamHoaPipe || tryIamfFfmpeg) {
            try {
                FfmpegPipeCodec pipeCodec = codec == Codec.IAMF
                    ? (effectiveTier == StudioCompressionTier.PortableLossy ? FfmpegPipeCodec.IamfOpus : FfmpegPipeCodec.IamfFlac)
                    : (effectiveTier == StudioCompressionTier.PortableLossy ? FfmpegPipeCodec.Opus : FfmpegPipeCodec.WavPack);
                return ExportHoaStreamingPipe(
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
                    pipeCodec,
                    outPath,
                    formatSummary,
                    onProgress,
                    token);
            } catch when (codec == Codec.IAMF) {
                // Older FFmpeg builds without -f iamf muxer fall back cleanly to Cavern's native OBU writer
                TryDeletePartialOutput(outPath);
                listener.DetachAllSources();
                track.Attach(listener, upmixOverride ?? new UpmixingSettings(false));
                if (extraVocalSource != null) {
                    listener.AttachSource(extraVocalSource);
                }
                activeSources = new List<Source>(listener.ActiveSources);
                choreoMapper = new ChoreoSourceMapper(activeSources, activeChoreo);
            }
        }

        // Real ITU-R BS.1770-5 loudness & 4x True-Peak metering across rendered environmental sources
        bool directPerSourceMeter = srcCount > 0 && srcCount <= 16;
        int meterCh = directPerSourceMeter ? srcCount : 2;
        LoudnessMeter meter = new(sampleRate, meterCh);
        if (directPerSourceMeter) {
            double[] weights = new double[meterCh];
            for (int c = 0; c < meterCh; c++) {
                weights[c] = activeSources[c].LFE ? 0.0 : 1.0;
            }
            meter.Configure(sampleRate, meterCh, weights);
        }
        TruePeakLimiter tpGuard = new(sampleRate, meterCh, job.TruePeakCeilingDbtp);
        float[] meterInterleaved = new float[updateRate * meterCh];
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        float stereoFoldNorm = 1f / MathF.Sqrt(Math.Max(1, srcCount));

        IamfCodecMode iamfCodecMode = effectiveTier switch {
            StudioCompressionTier.LosslessCompressed => IamfCodecMode.Flac,
            StudioCompressionTier.PortableLossy => IamfCodecMode.Opus,
            _ => IamfCodecMode.Ipcm
        };

        long written = 0;
        using (EnvironmentWriter writer = codec switch {
            Codec.IAMF => new IamfEnvironmentWriter(outPath, listener, totalSamples, job.BitDepth, useSceneBasedHoa: true, hoaOrder: hoaOrder, mixLabel: job.TrackName, codecMode: iamfCodecMode),
            Codec.AmbisonicsHOA => new AmbisonicsHoaWriter(outPath, listener, totalSamples, job.BitDepth, hoaOrder),
            _ => EnvironmentWriter.Create(outPath, codec, listener, totalSamples, job.BitDepth, track.Renderer)
        }) {
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)sampleRate);
                writer.WriteNextFrame();

                int frames = (int)Math.Min(updateRate, totalSamples - written);
                if (frames > 0 && srcCount > 0) {
                    Array.Clear(meterInterleaved, 0, frames * meterCh);
                    if (directPerSourceMeter) {
                        for (int c = 0; c < meterCh; c++) {
                            float[] srcSamples = activeSources[c].Rendered?[0];
                            if (srcSamples == null) {
                                continue;
                            }
                            int limit = Math.Min(frames, srcSamples.Length);
                            for (int s = 0; s < limit; s++) {
                                meterInterleaved[s * meterCh + c] = srcSamples[s];
                            }
                        }
                    } else {
                        for (int c = 0; c < srcCount; c++) {
                            if (activeSources[c].LFE) {
                                continue;
                            }
                            float[] srcSamples = activeSources[c].Rendered?[0];
                            if (srcSamples == null) {
                                continue;
                            }
                            float panX = Math.Clamp((activeSources[c].Position * invEnv).X, -1f, 1f);
                            float gL = MathF.Sqrt(0.5f * (1f - panX)) * stereoFoldNorm;
                            float gR = MathF.Sqrt(0.5f * (1f + panX)) * stereoFoldNorm;
                            int limit = Math.Min(frames, srcSamples.Length);
                            for (int s = 0; s < limit; s++) {
                                float v = srcSamples[s];
                                meterInterleaved[s * 2] += v * gL;
                                meterInterleaved[s * 2 + 1] += v * gR;
                            }
                        }
                    }
                    meter.AnalyzeInterleaved(meterInterleaved, 0, frames, meterCh);
                    tpGuard.ProcessInterleaved(meterInterleaved, 0, frames, meterCh);
                }

                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = outCh,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportHoaStreamingPipe(
        Listener listener,
        List<Source> activeSources,
        ChoreoSourceMapper choreoMapper,
        long totalSamples,
        int sampleRate,
        int hoaOrder,
        int harmonicCount,
        StudioBatchExportJob job,
        StudioCompressionTier effectiveTier,
        string ffmpegPath,
        Action onSigpipe,
        FfmpegPipeCodec pipeCodec,
        string outPath,
        string formatSummary,
        Action<double> onProgress,
        CancellationToken token) {
        double[] shBuffer = new double[harmonicCount];
        float[] hoaInterleaved = new float[updateRate * harmonicCount];
        LoudnessMeter meter = new(sampleRate, harmonicCount);
        TruePeakLimiter limiter = new(sampleRate, harmonicCount, job.TruePeakCeilingDbtp);
        Vector3 invEnv = new(
            Listener.EnvironmentSize.X > 1e-4f ? 1f / Listener.EnvironmentSize.X : 1f,
            Listener.EnvironmentSize.Y > 1e-4f ? 1f / Listener.EnvironmentSize.Y : 1f,
            Listener.EnvironmentSize.Z > 1e-4f ? 1f / Listener.EnvironmentSize.Z : 1f);
        float headroomNorm = 1f / MathF.Sqrt(Math.Max(1, activeSources.Count));

        long written = 0;
        using (FfmpegStdinPipeSink sink = new(
            ffmpegPath,
            outPath,
            harmonicCount,
            sampleRate,
            pipeCodec,
            onSigpipe,
            isAmbisonics: true,
            metadata: ResolveMetadata(job))) {
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)sampleRate);
                listener.Ping();

                int frames = (int)Math.Min(updateRate, totalSamples - written);
                Array.Clear(hoaInterleaved, 0, frames * harmonicCount);
                for (int c = 0; c < activeSources.Count; c++) {
                    Source src = activeSources[c];
                    float[] rendered = src.Rendered != null && src.Rendered.Length > 0 ? src.Rendered[0] : null;
                    if (rendered == null) {
                        continue;
                    }
                    int count = Math.Min(frames, rendered.Length);
                    if (src.LFE) {
                        for (int s = 0; s < count; s++) {
                            hoaInterleaved[s * harmonicCount] += rendered[s] * headroomNorm;
                        }
                        continue;
                    }
                    Vector3 rel = (src.Position - listener.Position) * invEnv;
                    Cavern.Format.Renderers.AmbisonicsHoaSpatialDecoder.EncodeDirectionSn3d(rel, hoaOrder, shBuffer);
                    for (int s = 0; s < count; s++) {
                        float sample = rendered[s] * headroomNorm;
                        int baseIdx = s * harmonicCount;
                        for (int q = 0; q < harmonicCount; q++) {
                            hoaInterleaved[baseIdx + q] += sample * (float)shBuffer[q];
                        }
                    }
                }

                limiter.ProcessInterleaved(hoaInterleaved, 0, frames, harmonicCount);
                meter.AnalyzeInterleaved(hoaInterleaved, 0, frames, harmonicCount);
                sink.WriteInterleavedBlock(hoaInterleaved, frames);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummary,
            ChannelCount = harmonicCount,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportPersonalizedBinaural(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
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
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(true));
        List<Source> activeSources = new(listener.ActiveSources);

        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Headphone3D);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);

        int stride = Math.Max(2, Listener.Channels.Length);
        double rateRatio = outSampleRate / (double)Math.Max(1, track.SampleRate);
        long totalSamples = Math.Max(updateRate, (long)Math.Round(Math.Max(updateRate, track.Length) * rateRatio));
        SpatialChoreoTrack activeChoreo = ResolveOrBuildChoreoTrack(job, null, outSampleRate, totalSamples / (double)Math.Max(1, outSampleRate));
        ChoreoSourceMapper choreoMapper = new(activeSources, activeChoreo);

        // Collect cloned Slot 6 post-processing stages (AutoEq, HpTF, ISO 226, BS.1770, Dialog)
        // so live playback stages never mutate or reset mid-export; skip limiters because this export
        // runs its own ceiling-configured TruePeakLimiter.
        List<IPostProcessingStage> activePostStages = [];
        if (job.PostProcessingStages != null) {
            for (int i = 0; i < job.PostProcessingStages.Count; i++) {
                IPostProcessingStage stage = job.PostProcessingStages[i];
                if (stage == null || !stage.IsEnabled) {
                    continue;
                }
                if (string.Equals(stage.StageId, "true-peak-limiter-4x", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "true-peak-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "cavern-soft-knee-limiter", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage.StageId, "limiter", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                activePostStages.Add(stage.CloneForOfflineExport());
            }
            activePostStages.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        for (int i = 0; i < activePostStages.Count; i++) {
            activePostStages[i].Configure(outSampleRate, 2);
            activePostStages[i].Reset();
        }

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
            for (int i = 0; i < activePostStages.Count; i++) {
                try {
                    activePostStages[i].Dispose();
                } catch {
                    // Best-effort cleanup of cloned offline post-processing stages
                }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = activePostStages.Count > 0
                ? $"Personalized Lossless Binaural (HRTF + {activePostStages.Count} Slot 6 Stages + BS.1770 Limiter)"
                : "Personalized Lossless Binaural (HRTF + BS.1770 Limiter)",
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

    static bool IsNativeMultichannelOrAtmos(CavernizeTrack track) =>
        track != null &&
        ((track.Renderer != null && (track.Renderer.HasObjects || track.Renderer.Objects.Count > 2)) ||
         (track.Reader != null && track.Reader.ChannelCount > 2));

    static StudioBatchExportResult ExportHybrid714(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token,
        string formatSummaryOverride = null) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        StudioCompressionTier effectiveTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Cinema714);
        string ffmpegPath = ResolveFfmpegPath(job);
        Action onSigpipe = ResolveSigpipeShield(job);
        SpatialChoreoTrack choreoTrack = ResolveChoreoTrack(job);

        // W48 Dynamic Atmos Preservation Invariant: NEVER downmix native Dolby Atmos / multichannel tracks to stereo
        // for AI upmix. Native Atmos moving objects are preserved 100% via AtmosAwareUpmixer below.
        bool isNativeMulti = IsNativeMultichannelOrAtmos(track);

        // Prefer Slot 7A Neural Orchestrator -> NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe (canonical Layout714 + 120 Hz LR4 LFE)
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        if (!isNativeMulti &&
            job.NeuralOrchestrator != null &&
            job.NeuralRuntime != null &&
            job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
            stemPlan != null &&
            stemPlan.IsRunnable) {
            DecodeStereoInput(track, job, out float[] inL, out float[] inR, out bool fromTwin, p => onProgress?.Invoke(p * 0.20), token);
            int totalSamples = Math.Min(inL.Length, inR.Length);
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
                (p, _) => onProgress?.Invoke(0.20 + p * 0.55),
                token);
            inL = null;
            inR = null;
            job.OnSeparatedTrackReady?.Invoke(separated);

            StereoStemBuffer[] stems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
            separated = null;
            if (stems.Length > 0 && totalSamples > 0) {
                if (choreoTrack != null && choreoTrack.StemCount > 0 && choreoTrack.Mode != SpatialChoreoMode.Off) {
                    LoudnessAnalysisSnapshot choreoSnap = RenderNeuralStemsWithChoreoTo714Stream(
                        stems,
                        choreoTrack,
                        sampleRate,
                        outPath,
                        job.BitDepth,
                        job.TruePeakCeilingDbtp,
                        effectiveTier,
                        ffmpegPath,
                        onSigpipe,
                        p => onProgress?.Invoke(0.75 + p * 0.25),
                        token);
                    stems = null;
                    return new StudioBatchExportResult {
                        Preset = job.Preset,
                        OutputPath = outPath,
                        FormatSummary = formatSummaryOverride ?? $"Cinema 7.1.4 AI Spatial Master ({stemPlan.Summary})",
                        ChannelCount = Layout714.Length,
                        SampleRate = sampleRate,
                        DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                        IntegratedLufs = ResolveMeasuredLufs(choreoSnap),
                        LoudnessRangeLu = choreoSnap.LoudnessRangeLu,
                        MaxTruePeakDbtp = choreoSnap.MaxTruePeakDbtp,
                        CompressionTier = effectiveTier,
                        OutputBytes = SafeFileLength(outPath),
                        Succeeded = true
                    };
                }

                float[][] full12 = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sampleRate, 0.35f, 120f);
                stems = null;
                LoudnessAnalysisSnapshot neuralSnap = WriteMeteredLimitedMultichannelStream(
                    outPath,
                    Layout714,
                    full12,
                    sampleRate,
                    job.BitDepth,
                    job.TruePeakCeilingDbtp,
                    effectiveTier,
                    ffmpegPath,
                    onSigpipe,
                    p => onProgress?.Invoke(0.75 + p * 0.25),
                    token,
                    releaseChannelsAfterStream: true);

                return new StudioBatchExportResult {
                    Preset = job.Preset,
                    OutputPath = outPath,
                    FormatSummary = formatSummaryOverride ?? $"Cinema 7.1.4 AI Spatial Master ({stemPlan.Summary})",
                    ChannelCount = Layout714.Length,
                    SampleRate = sampleRate,
                    DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                    IntegratedLufs = ResolveMeasuredLufs(neuralSnap),
                    LoudnessRangeLu = neuralSnap.LoudnessRangeLu,
                    MaxTruePeakDbtp = neuralSnap.MaxTruePeakDbtp,
                    CompressionTier = effectiveTier,
                    OutputBytes = SafeFileLength(outPath),
                    Succeeded = true
                };
            }
        }

        // Transparent pure-DSP fallback: Parametric STFT + Atmos-Aware 7.1.4 spatial upmixer (zero full-track arrays)
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(Layout714));

        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        UpmixingSettings upmix = new(true) {
            Cavernize = true,
            MatrixUpmixing = true,
            ParametricStft = true,
            AtmosAware = true
        };
        track.Attach(listener, upmix);
        List<Source> activeSources = new(listener.ActiveSources);
        ChoreoSourceMapper choreoMapper = new(activeSources, choreoTrack);

        long totalDspSamples = Math.Max(updateRate, track.Length);
        LoudnessMeter meter = new(sampleRate, Layout714.Length);
        TruePeakLimiter limiter = new(sampleRate, Layout714.Length, job.TruePeakCeilingDbtp);

        long written = 0;
        using (IStreamingExportSink sink = CreateStreamingSink(
            outPath,
            Layout714,
            Layout714.Length,
            sampleRate,
            job.BitDepth,
            totalDspSamples,
            effectiveTier,
            ffmpegPath,
            onSigpipe,
            isAmbisonics: false)) {
            long totalFrames = (totalDspSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                choreoMapper.ApplyFrame(activeSources, written / (double)sampleRate);
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalDspSamples - written);
                limiter.ProcessInterleaved(rendered, 0, frames, Layout714.Length);
                meter.AnalyzeInterleaved(rendered, 0, frames, Layout714.Length);
                sink.WriteInterleavedBlock(rendered, frames);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        }

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = formatSummaryOverride ?? "Hybrid DirAC Parametric 7.1.4 Spatial Master (DSP Fallback)",
            ChannelCount = Layout714.Length,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = effectiveTier,
            OutputBytes = SafeFileLength(outPath),
            Succeeded = true
        };
    }

    static LoudnessAnalysisSnapshot RenderNeuralStemsWithChoreoTo714Stream(
        StereoStemBuffer[] stems,
        SpatialChoreoTrack choreoTrack,
        int sampleRate,
        string outPath,
        BitDepth bits,
        float ceilingDbtp,
        StudioCompressionTier effectiveTier,
        string ffmpegPath,
        Action onSigpipe,
        Action<double> onProgress,
        CancellationToken token) {
        EnsureEnvironmentSize();
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(Layout714));
        Listener listener = new(false) {
            SampleRate = sampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = Math.Max(64, stems.Length * 2),
            LFESeparation = true,
            Normalizer = 0f
        };

        int maxLen = 0;
        List<(Source Src, int StemIdx, NeuralStemRole Role, float SideSign)> bound = new(stems.Length * 2);
        for (int i = 0; i < stems.Length; i++) {
            StereoStemBuffer st = stems[i];
            if (st == null || st.Length <= 0 || st.Left == null || st.Right == null) {
                continue;
            }
            if (st.Length > maxLen) {
                maxLen = st.Length;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
            int stemIdx = choreoTrack.FindStemIndex(role, st.Name);
            if (stemIdx < 0) {
                stemIdx = i % choreoTrack.StemCount;
            }
            bool centerMono = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass;
            if (centerMono) {
                float[] mono = new float[st.Length];
                for (int n = 0; n < st.Length; n++) {
                    mono[n] = 0.70710678f * (st.Left[n] + st.Right[n]);
                }
                Source src = new() {
                    Clip = new Clip(new Cavern.Waveforms.MultichannelWaveform(mono), sampleRate),
                    Position = Vector3.Zero,
                    LFE = false,
                    Volume = 1f
                };
                listener.AttachSource(src);
                bound.Add((src, stemIdx, role, 0f));
            } else {
                Source srcL = new() {
                    Clip = new Clip(new Cavern.Waveforms.MultichannelWaveform(st.Left), sampleRate),
                    Position = Vector3.Zero,
                    LFE = false,
                    Volume = 1f
                };
                Source srcR = new() {
                    Clip = new Clip(new Cavern.Waveforms.MultichannelWaveform(st.Right), sampleRate),
                    Position = Vector3.Zero,
                    LFE = false,
                    Volume = 1f
                };
                listener.AttachSource(srcL);
                listener.AttachSource(srcR);
                bound.Add((srcL, stemIdx, role, -1f));
                bound.Add((srcR, stemIdx, role, 1f));
            }
        }

        LoudnessMeter meter = new(sampleRate, Layout714.Length);
        TruePeakLimiter limiter = new(sampleRate, Layout714.Length, ceilingDbtp);
        long totalSamples = Math.Max(updateRate, maxLen);
        long written = 0;
        using (IStreamingExportSink sink = CreateStreamingSink(
            outPath,
            Layout714,
            Layout714.Length,
            sampleRate,
            bits,
            totalSamples,
            effectiveTier,
            ffmpegPath,
            onSigpipe,
            isAmbisonics: false)) {
            long totalFrames = (totalSamples + updateRate - 1) / updateRate;
            for (long f = 0; f < totalFrames; f++) {
                token.ThrowIfCancellationRequested();
                double timeSec = written / (double)sampleRate;
                for (int b = 0; b < bound.Count; b++) {
                    (Source src, int stemIdx, NeuralStemRole role, float sideSign) = bound[b];
                    if (NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(
                        choreoTrack,
                        choreoTrack.Mode,
                        stemIdx,
                        role,
                        sideSign,
                        timeSec,
                        out Vector3 normPos)) {
                        src.Position = normPos * Listener.EnvironmentSize;
                    }
                }
                float[] rendered = listener.Render(1);
                int frames = (int)Math.Min(updateRate, totalSamples - written);
                limiter.ProcessInterleaved(rendered, 0, frames, Layout714.Length);
                meter.AnalyzeInterleaved(rendered, 0, frames, Layout714.Length);
                sink.WriteInterleavedBlock(rendered, frames);
                written += frames;
                if ((f & 15) == 0 || f == totalFrames - 1) {
                    onProgress?.Invoke(Math.Clamp((f + 1.0) / totalFrames, 0.0, 1.0));
                }
            }
        }
        return meter.Snapshot;
    }

    static StudioBatchExportResult ExportSpatialKaraoke(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        bool includeLiveVocalMixdown,
        Action<double> onProgress,
        CancellationToken token) {
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        bool multichannelAtmos = !includeLiveVocalMixdown && allItems.Count > 2;

        // Resolve Slot 7A Neural Lead/Backing vocal reference stems when available (transparent DSP fallback if absent)
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        NeuralExecutionPlan karaokePlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, safeNeural);
        bool hasNeuralKaraoke = job.NeuralOrchestrator != null &&
                                job.NeuralRuntime != null &&
                                job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                                karaokePlan != null &&
                                karaokePlan.IsRunnable;

        if (multichannelAtmos) {
            // Preserve full discrete Atmos / surround layout (e.g. 5.1, 7.1, 7.1.4) using zero-phase multichannel karaoke
            ReferenceChannel[] targetLayout = ResolveExportChannelLayout(track, allItems.Count);
            DecodeMultichannelLayout(track, targetLayout, out float[][] multiChannels, out bool[] isLfe, out float[] mixL, out float[] mixR,
                p => onProgress?.Invoke(p * 0.25), token);
            int totalSamples = multiChannels.Length > 0 ? multiChannels[0].Length : 0;

            float[] leadRef = null;
            float[] leadRefRight = null;
            float[] backingRef = null;
            if (hasNeuralKaraoke && totalSamples > 0) {
                (float[] nL, float[] nR, bool fromTwin) = ResolveNeuralStereoInput(job, mixL, mixR, totalSamples);
                (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
                (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                    track.Path ?? job.TrackName,
                    fileSize,
                    mtimeTicks,
                    nL,
                    nR,
                    sampleRate,
                    NeuralPipelineGoal.SpatialKaraoke,
                    safeNeural,
                    fromTwin,
                    (p, _) => onProgress?.Invoke(0.25 + p * 0.45),
                    token);
                job.OnSeparatedTrackReady?.Invoke(separated);
                ResolveStereoLeadVocalRefs(separated, out leadRef, out leadRefRight);
                backingRef = job.PreserveBackingVocals ? separated?.MonoBackingVocalRef : null;
            }

            bool[] isStrictCenter = new bool[targetLayout.Length];
            for (int c = 0; c < targetLayout.Length; c++) {
                isStrictCenter[c] = targetLayout[c] == ReferenceChannel.FrontCenter;
            }

            onProgress?.Invoke(0.72);
            float[][] karaokeMulti = SpatialKaraokeEngine.ProcessOfflineMultichannel(
                multiChannels,
                isLfe,
                leadRef,
                backingRef,
                sampleRate,
                Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
                job.PreserveBackingVocals,
                neuralLeadVocalRefRight: leadRefRight,
                isStrictCenter: isStrictCenter);
            multiChannels = null;
            mixL = null;
            mixR = null;
            leadRef = null;
            leadRefRight = null;
            backingRef = null;

            StudioCompressionTier multiTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Cinema714);
            LoudnessAnalysisSnapshot multiSnap = WriteMeteredLimitedMultichannelStream(
                outPath,
                targetLayout,
                karaokeMulti,
                sampleRate,
                job.BitDepth,
                job.TruePeakCeilingDbtp,
                multiTier,
                ResolveFfmpegPath(job),
                ResolveSigpipeShield(job),
                p => onProgress?.Invoke(0.75 + p * 0.25),
                token,
                releaseChannelsAfterStream: true);

            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = leadRef != null
                    ? $"Spatial Karaoke Multichannel Atmos Master ({targetLayout.Length} ch · {karaokePlan.Summary})"
                    : $"Spatial Karaoke Multichannel Atmos Master ({targetLayout.Length} ch · Zero-Phase Coherence)",
                ChannelCount = targetLayout.Length,
                SampleRate = sampleRate,
                DurationSeconds = totalSamples / (double)Math.Max(1, sampleRate),
                IntegratedLufs = ResolveMeasuredLufs(multiSnap),
                LoudnessRangeLu = multiSnap.LoudnessRangeLu,
                MaxTruePeakDbtp = multiSnap.MaxTruePeakDbtp,
                CompressionTier = multiTier,
                OutputBytes = SafeFileLength(outPath),
                Succeeded = true
            };
        }

        // Stereo Spatial Karaoke or Live Karaoke Performance Spatial Mixdown (W08 Karaoke + W11 Live Vocal Chain)
        DecodeStereoInput(track, job, out float[] stereoL, out float[] stereoR, out bool twinUsed, p => onProgress?.Invoke(p * 0.20), token);
        int len = Math.Min(stereoL.Length, stereoR.Length);
        float[] neuralLeadRef = null;
        float[] neuralLeadRefRight = null;
        float[] neuralBackingRef = null;

        if (hasNeuralKaraoke && len > 0) {
            (long fileSize, long mtimeTicks) = GetSourceFileStamp(track.Path);
            (NeuralSeparatedTrack separated, _, _) = job.NeuralOrchestrator.ExecuteTrackPipeline(
                track.Path ?? job.TrackName,
                fileSize,
                mtimeTicks,
                stereoL,
                stereoR,
                sampleRate,
                NeuralPipelineGoal.SpatialKaraoke,
                safeNeural,
                twinUsed,
                (p, _) => onProgress?.Invoke(0.20 + p * 0.45),
                token);
            job.OnSeparatedTrackReady?.Invoke(separated);
            ResolveStereoLeadVocalRefs(separated, out neuralLeadRef, out neuralLeadRefRight);
            neuralBackingRef = job.PreserveBackingVocals ? separated?.MonoBackingVocalRef : null;
        }

        bool hasNeuralLead = neuralLeadRef != null;
        float[][] stereoPair = [stereoL, stereoR];
        bool[] stereoLfe = [false, false];
        float[][] karaokeStereo = SpatialKaraokeEngine.ProcessOfflineMultichannel(
            stereoPair,
            stereoLfe,
            neuralLeadRef,
            neuralBackingRef,
            sampleRate,
            Math.Clamp(job.KaraokeVocalLevel, 0f, 1f),
            job.PreserveBackingVocals,
            neuralLeadVocalRefRight: neuralLeadRefRight);
        stereoL = null;
        stereoR = null;
        stereoPair = null;
        neuralLeadRef = null;
        neuralLeadRefRight = null;
        neuralBackingRef = null;

        LiveVocalChainDsp vocalDsp = includeLiveVocalMixdown ? new LiveVocalChainDsp(sampleRate) : null;
        if (vocalDsp != null) {
            vocalDsp.Configure(
                sampleRate,
                job.VocalDnaProfile,
                new VocalMacroSettings { Enabled = true });
        }

        LoudnessMeter meter = new(sampleRate, 2);
        TruePeakLimiter limiter = new(sampleRate, 2, job.TruePeakCeilingDbtp);
        ReferenceChannel[] stereoLayout = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
        float[] micInterleaved = new float[updateRate * 2];
        float[] outInterleaved = new float[updateRate * 2];
        StudioCompressionTier stereoTier = ResolveEffectiveTier(job, StudioBaseOutputFormat.Headphone3D);

        int written = 0;
        using (IStreamingExportSink sink = CreateStreamingSink(
            outPath,
            stereoLayout,
            2,
            sampleRate,
            job.BitDepth,
            len,
            stereoTier,
            ResolveFfmpegPath(job),
            ResolveSigpipeShield(job),
            isAmbisonics: false)) {
            int totalBlocks = (len + updateRate - 1) / updateRate;
            for (int b = 0; b < totalBlocks; b++) {
                token.ThrowIfCancellationRequested();
                int frames = Math.Min(updateRate, len - written);
                for (int s = 0; s < frames; s++) {
                    outInterleaved[s * 2] = karaokeStereo[0][written + s];
                    outInterleaved[s * 2 + 1] = karaokeStereo[1][written + s];
                }

                if (vocalDsp != null && job.LiveMicMonoCapture != null && written < job.LiveMicMonoCapture.Length) {
                    int micAvail = Math.Min(frames, job.LiveMicMonoCapture.Length - written);
                    Array.Clear(micInterleaved, 0, frames * 2);
                    for (int s = 0; s < micAvail; s++) {
                        float m = job.LiveMicMonoCapture[written + s];
                        micInterleaved[s * 2] = m;
                        micInterleaved[s * 2 + 1] = m;
                    }
                    vocalDsp.ProcessStereoBlockZeroGc(micInterleaved, 0, frames, outInterleaved, 0);
                    for (int i = 0; i < frames * 2; i++) {
                        outInterleaved[i] += micInterleaved[i];
                    }
                }

                limiter.ProcessInterleaved(outInterleaved, 0, frames, 2);
                meter.AnalyzeInterleaved(outInterleaved, 0, frames, 2);
                sink.WriteInterleavedBlock(outInterleaved, frames);
                written += frames;
                if ((b & 15) == 0 || b == totalBlocks - 1) {
                    onProgress?.Invoke(Math.Clamp(0.68 + 0.32 * ((b + 1.0) / Math.Max(1, totalBlocks)), 0.0, 1.0));
                }
            }
        }
        karaokeStereo = null;

        LoudnessAnalysisSnapshot snap = meter.Snapshot;
        return new StudioBatchExportResult {
            Preset = job.Preset,
            OutputPath = outPath,
            FormatSummary = includeLiveVocalMixdown
                ? (hasNeuralLead
                    ? $"Live Karaoke Performance Mixdown ({karaokePlan.Summary} + W11 Vocal Chain)"
                    : "Live Karaoke Performance Mixdown (Coherence Karaoke + W11 Vocal Chain)")
                : (hasNeuralLead
                    ? $"Spatial Karaoke Stereo Master ({karaokePlan.Summary})"
                    : "Spatial Karaoke Stereo Master (Zero-Phase Coherence Mask)"),
            ChannelCount = 2,
            SampleRate = sampleRate,
            DurationSeconds = written / (double)Math.Max(1, sampleRate),
            IntegratedLufs = ResolveMeasuredLufs(snap),
            LoudnessRangeLu = snap.LoudnessRangeLu,
            MaxTruePeakDbtp = snap.MaxTruePeakDbtp,
            CompressionTier = stereoTier,
            OutputBytes = SafeFileLength(outPath),
            Succeeded = true
        };
    }

    static StudioBatchExportResult ExportFlacAtmosHybridMaster(
        CavernizeTrack track,
        StudioBatchExportJob job,
        string outPath,
        Action<double> onProgress,
        CancellationToken token) {
        NeuralAudioConfig safeNeural = (job.NeuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        LgsrConfig safeLgsr = (job.LgsrConfig ?? LgsrConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = job.NeuralOrchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        bool canRunEmMwf = job.LosslessTwinProvider is UniversalLosslessTwinProvider universalTwin &&
                           universalTwin.HasSyncedLosslessTwin &&
                           job.NeuralRuntime != null &&
                           job.NeuralRuntime.GetRuntimeInfo().IsAvailable &&
                           job.NeuralOrchestrator != null &&
                           stemPlan != null &&
                           stemPlan.IsRunnable;

        if (canRunEmMwf) {
            UniversalLosslessTwinProvider syncedTwin = (UniversalLosslessTwinProvider)job.LosslessTwinProvider;
            int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;
            LoudnessAnalysisSnapshot measured = LoudnessAnalysisSnapshot.Empty();
            int masteredChannels = 0;

            StemExtractionResult extRes = StemExtractor.ExtractLosslessHybridSpatialMaster(
                track,
                syncedTwin,
                job.NeuralRuntime,
                stemModelPath: null,
                stemModelEntry: null,
                outputDir: Path.GetDirectoryName(outPath) ?? ".",
                bits: job.BitDepth,
                neuralConfig: safeNeural,
                lgsrConfig: safeLgsr,
                tier3RestorerModelPath: null,
                tier3RestorerEntry: null,
                exportResidualTrack: false,
                onProgress: (p, _) => onProgress?.Invoke(p),
                token: token,
                orchestrator: job.NeuralOrchestrator,
                onSeparatedTrackReady: job.OnSeparatedTrackReady,
                combinedMasterOutputPath: outPath,
                onMasterChannelsReady: (channels, layout) => {
                    masteredChannels = channels.Length;
                    measured = ApplyTruePeakLimiterAndMeasureInPlace(
                        channels,
                        layout,
                        sampleRate,
                        job.TruePeakCeilingDbtp,
                        token);
                });

            NeuralExecutionPlan tier3Plan = safeLgsr.EnableTier3ResidualCleanup
                ? job.NeuralOrchestrator.BuildPlan(NeuralPipelineGoal.LosslessResidualRestore, safeNeural)
                : null;
            string tier3Tag = tier3Plan != null && tier3Plan.IsRunnable ? $" + Tier 3 ({tier3Plan.Summary})" : string.Empty;

            return new StudioBatchExportResult {
                Preset = job.Preset,
                OutputPath = outPath,
                FormatSummary = $"FLAC + Atmos EM-MWF Hybrid Master ({masteredChannels} ch · {stemPlan.Summary}{tier3Tag})",
                ChannelCount = Math.Max(1, masteredChannels),
                SampleRate = extRes.SampleRate,
                DurationSeconds = extRes.SamplesWritten / (double)Math.Max(1, extRes.SampleRate),
                IntegratedLufs = ResolveMeasuredLufs(measured),
                LoudnessRangeLu = measured.LoudnessRangeLu,
                MaxTruePeakDbtp = measured.MaxTruePeakDbtp,
                CompressionTier = StudioCompressionTier.MasterPcm,
                OutputBytes = SafeFileLength(outPath),
                Succeeded = true
            };
        }

        // Honest transparent fallback when unpaired or ONNX Runtime / stem checkpoint unavailable
        return ExportHybrid714(
            track,
            job,
            outPath,
            onProgress,
            token,
            formatSummaryOverride: "7.1.4 Spatial Master (Fallback — Pair Lossless Twin + ONNX for EM-MWF)");
    }
}
