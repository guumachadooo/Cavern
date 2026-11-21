using System.Numerics;
using System.Text.Json;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Neural;
using Cavern.Utilities;
using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Spatial;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Summary returned by <see cref="StemExtractor"/> operations.
/// </summary>
public sealed class StemExtractionResult {
    /// <summary>
    /// Paths to all exported audio files.
    /// </summary>
    public IReadOnlyList<string> CreatedFiles { get; init; } = [];

    /// <summary>
    /// Path to the optional 3D trajectory JSON file, or null if not generated.
    /// </summary>
    public string TrajectoryJsonPath { get; init; }

    /// <summary>
    /// Total samples per channel written.
    /// </summary>
    public long SamplesWritten { get; init; }

    /// <summary>
    /// Output sample rate in Hz.
    /// </summary>
    public int SampleRate { get; init; }
}

/// <summary>
/// Extracts individual bed channels, dynamic 3D objects, spatial stems, or raw bitstreams from a <see cref="CavernizeTrack"/>.
/// </summary>
public static class StemExtractor {
    const int updateRate = 256;
    const int writeBatchFrames = 16; // 4096 samples per batch

    /// <summary>
    /// Extracts each selected bed channel and dynamic 3D object from <paramref name="track"/> into separate mono WAV files
    /// in a single decoding pass, plus an optional JSON file containing 3D object trajectories over time.
    /// </summary>
    public static StemExtractionResult ExtractRawStems(
        CavernizeTrack track,
        string outputDir,
        BitDepth bits,
        IReadOnlyList<int> selectedIndices,
        bool exportTrajectoryJson,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("The selected track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP raw stem extraction requires converting to DAMF/CAF via an external decoder first.");
        }

        Directory.CreateDirectory(outputDir);
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        int[] targets = ResolveTargetIndices(allItems.Count, selectedIndices);
        if (targets.Length == 0) {
            throw new ArgumentException("At least one channel or object index must be selected.", nameof(selectedIndices));
        }

        if (Listener.EnvironmentSize.X == 0 || Listener.EnvironmentSize.Y == 0 || Listener.EnvironmentSize.Z == 0) {
            Listener.EnvironmentSize = new Vector3(10, 7, 10);
        }

        Listener.HeadphoneVirtualizer = false;
        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = Math.Max(256, allItems.Count)
        };
        track.Attach(listener, new UpmixingSettings(true));

        IReadOnlyList<Source> sources = track.Renderer.Objects;
        long totalLength = track.Length > 0 ? track.Length : track.SampleRate * 3600L;
        string baseName = SanitizeFilePart(Path.GetFileNameWithoutExtension(track.Path) ?? "track");

        Dictionary<int, RIFFWaveWriter> writers = new(targets.Length);
        Dictionary<int, float[]> batchBuffers = new(targets.Length);
        List<string> createdFiles = new(targets.Length);

        string trajectoryPath = exportTrajectoryJson ? Path.Combine(outputDir, $"{baseName}_trajectory.json") : null;
        FileStream jsonStream = null;
        Utf8JsonWriter jsonWriter = null;
        Vector3[] lastPositions = new Vector3[sources.Count];
        float[] lastGains = new float[sources.Count];
        for (int i = 0; i < sources.Count; i++) {
            lastPositions[i] = new Vector3(float.NaN);
            lastGains[i] = float.NaN;
        }

        try {
            foreach (int idx in targets) {
                string fileName = $"{baseName}_{allItems[idx].GetSafeFileBaseName()}.wav";
                string filePath = Path.Combine(outputDir, fileName);
                RIFFWaveWriter writer = new(filePath, 1, totalLength, track.SampleRate, bits);
                writer.WriteHeader();
                writers[idx] = writer;
                batchBuffers[idx] = new float[updateRate * writeBatchFrames];
                createdFiles.Add(filePath);
            }

            if (exportTrajectoryJson) {
                jsonStream = File.Create(trajectoryPath);
                jsonWriter = new Utf8JsonWriter(jsonStream, new JsonWriterOptions { Indented = true });
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("track", Path.GetFileName(track.Path) ?? baseName);
                jsonWriter.WriteNumber("sampleRate", track.SampleRate);
                jsonWriter.WriteNumber("totalSamples", totalLength);
                jsonWriter.WriteStartArray("sources");
                for (int i = 0; i < allItems.Count; i++) {
                    jsonWriter.WriteStartObject();
                    jsonWriter.WriteNumber("index", allItems[i].Index);
                    jsonWriter.WriteString("name", allItems[i].Name);
                    jsonWriter.WriteBoolean("isBed", allItems[i].IsBed);
                    jsonWriter.WriteBoolean("isLfe", allItems[i].IsLFE);
                    jsonWriter.WriteEndObject();
                }
                jsonWriter.WriteEndArray();
                jsonWriter.WriteStartArray("keyframes");
            }

            long rendered = 0;
            int batchPos = 0;
            int progressCounter = 0;
            Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;

            while (rendered < totalLength) {
                token.ThrowIfCancellationRequested();
                listener.Ping();

                int samplesThisFrame = (int)Math.Min(updateRate, totalLength - rendered);
                foreach (int idx in targets) {
                    float[] srcSamples = idx < sources.Count ? sources[idx].Rendered?[0] : null;
                    float[] batch = batchBuffers[idx];
                    if (srcSamples != null) {
                        for (int s = 0; s < samplesThisFrame; s++) {
                            batch[batchPos + s] = Math.Clamp(srcSamples[s], -1f, 1f);
                        }
                    } else {
                        Array.Clear(batch, batchPos, samplesThisFrame);
                    }
                }

                batchPos += samplesThisFrame;
                if (batchPos >= updateRate * writeBatchFrames || rendered + samplesThisFrame >= totalLength) {
                    foreach (int idx in targets) {
                        writers[idx].WriteBlock(batchBuffers[idx], 0, batchPos);
                    }
                    batchPos = 0;
                }

                if (jsonWriter != null && (rendered == 0 || HasTrajectoryChanged(sources, invEnv, lastPositions, lastGains))) {
                    jsonWriter.WriteStartObject();
                    jsonWriter.WriteNumber("sample", rendered);
                    jsonWriter.WriteNumber("timeSec", Math.Round(rendered / (double)track.SampleRate, 4));
                    jsonWriter.WriteStartArray("objects");
                    for (int i = 0; i < sources.Count; i++) {
                        Vector3 norm = sources[i].Position * invEnv;
                        lastPositions[i] = norm;
                        lastGains[i] = sources[i].Volume;
                        jsonWriter.WriteStartObject();
                        jsonWriter.WriteNumber("i", i);
                        jsonWriter.WriteNumber("x", Math.Round(norm.X, 4));
                        jsonWriter.WriteNumber("y", Math.Round(norm.Y, 4));
                        jsonWriter.WriteNumber("z", Math.Round(norm.Z, 4));
                        jsonWriter.WriteNumber("g", Math.Round(sources[i].Volume, 4));
                        jsonWriter.WriteEndObject();
                    }
                    jsonWriter.WriteEndArray();
                    jsonWriter.WriteEndObject();
                }

                rendered += samplesThisFrame;
                if ((++progressCounter & 31) == 0 || rendered >= totalLength) {
                    double progress = Math.Clamp(rendered / (double)totalLength, 0, 1);
                    onProgress?.Invoke(progress, $"Extraindo {targets.Length} faixas ({progress:0.0%})...");
                }
            }

            if (jsonWriter != null) {
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
                jsonWriter.Flush();
            }

            return new StemExtractionResult {
                CreatedFiles = createdFiles,
                TrajectoryJsonPath = trajectoryPath,
                SamplesWritten = rendered,
                SampleRate = track.SampleRate
            };
        } finally {
            foreach (RIFFWaveWriter writer in writers.Values) {
                writer.Dispose();
            }
            jsonWriter?.Dispose();
            jsonStream?.Dispose();
        }
    }

    /// <summary>
    /// Renders selected channels/objects through Cavern's spatial engine (HRTF Virtualizer or <paramref name="app"/>'s <see cref="RenderTarget"/>)
    /// using custom listener placement, producing either one combined file (e.g. 3D objects only) or one spatial file per selected stem.
    /// </summary>
    public static StemExtractionResult ExtractSpatialStems(
        ICavernizeApp app,
        CavernizeTrack track,
        string outputDir,
        BitDepth bits,
        IReadOnlyList<int> selectedIndices,
        bool combineSelectedIntoSingleFile,
        Vector3 listenerPos,
        Vector3 listenerRot,
        bool distanceSim,
        Rolloffs rolloff,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("The selected track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP spatial stem extraction requires converting to DAMF/CAF via an external decoder first.");
        }

        Directory.CreateDirectory(outputDir);
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        int[] targets = ResolveTargetIndices(allItems.Count, selectedIndices);
        if (targets.Length == 0) {
            throw new ArgumentException("At least one channel or object index must be selected.", nameof(selectedIndices));
        }

        string baseName = SanitizeFilePart(Path.GetFileNameWithoutExtension(track.Path) ?? "track");
        List<string> createdFiles = [];
        int totalPasses = combineSelectedIntoSingleFile ? 1 : targets.Length;
        long lastSamplesWritten = 0;
        int outSampleRate = track.SampleRate;

        for (int pass = 0; pass < totalPasses; pass++) {
            token.ThrowIfCancellationRequested();
            HashSet<int> activeSet = combineSelectedIntoSingleFile ? [.. targets] : [targets[pass]];
            string outName = combineSelectedIntoSingleFile
                ? $"{baseName}_3D_combined.wav"
                : $"{baseName}_{allItems[targets[pass]].GetSafeFileBaseName()}_3D.wav";
            string outPath = Path.Combine(outputDir, outName);

            ConversionEnvironment env = new(app);
            env.AttachToListener(track);
            Listener listener = env.Listener;
            listener.Position = listenerPos;
            listener.Rotation = listenerRot;
            listener.UpdateRate = updateRate;
            outSampleRate = listener.SampleRate;

            bool isHeadphoneTarget = app.RenderTarget is VirtualizerRenderTarget;
            bool isSpeakerVirt = !isHeadphoneTarget && app.RenderingSettings.SpeakerVirtualizer;
            int outChannels = app.RenderTarget.OutputChannels;
            VirtualizerFilter speakerVirtFilter = null;
            Normalizer virtNormalizer = null;
            if (isHeadphoneTarget) {
                Listener.HeadphoneVirtualizer = true;
                new VirtualizerFilter().SetLayout();
                listener.Normalizer = 0f;
                virtNormalizer = new Normalizer(true) {
                    SampleRate = listener.SampleRate,
                    Channels = outChannels,
                    decayFactor = 10f * listener.UpdateRate / listener.SampleRate
                };
            } else if (isSpeakerVirt) {
                Listener.HeadphoneVirtualizer = false;
                speakerVirtFilter = new VirtualizerFilter();
                speakerVirtFilter.SetLayout();
                listener.Normalizer = 0f;
                virtNormalizer = new Normalizer(true) {
                    SampleRate = listener.SampleRate,
                    Channels = Listener.Channels.Length,
                    decayFactor = 10f * listener.UpdateRate / listener.SampleRate
                };
            }

            IReadOnlyList<Source> trackObjects = track.Renderer.Objects;
            foreach (Source src in trackObjects) {
                src.DistanceSimulation = distanceSim && isHeadphoneTarget;
                src.VolumeRolloff = rolloff;
            }

            long totalLength = (long)Math.Ceiling(track.Length * (listener.SampleRate / (double)track.SampleRate));
            using RIFFWaveWriter writer = !app.RenderingSettings.WAVChannelSkip
                ? new RIFFWaveWriter(outPath, app.RenderTarget.Channels[..outChannels], totalLength, listener.SampleRate, bits)
                : new RIFFWaveWriter(outPath, outChannels, totalLength, listener.SampleRate, bits);
            writer.WriteHeader();

            int batchFrames = writeBatchFrames;
            int cacheStride = Listener.Channels.Length;
            long rendered = 0;
            int passIndex = pass;

            while (rendered < totalLength) {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < trackObjects.Count; i++) {
                    bool keep = activeSet.Contains(i);
                    if (keep && (app.RenderingSettings.MuteBed || app.RenderingSettings.MuteGround)) {
                        Vector3 rawPos = trackObjects[i].Position / Listener.EnvironmentSize;
                        if (app.RenderingSettings.MuteBed && MathF.Abs(rawPos.X) % 1 < .01f &&
                            MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f) {
                            keep = false;
                        }
                        if (app.RenderingSettings.MuteGround && MathF.Abs(rawPos.Y) < .05f) {
                            keep = false;
                        }
                    }
                    trackObjects[i].Mute = !keep;
                }

                int framesThisBatch = (int)Math.Min(batchFrames, (totalLength - rendered + updateRate - 1) / updateRate);
                float[] renderedBatch = listener.Render(framesThisBatch);
                int samplesAvailable = Math.Min(framesThisBatch * updateRate, (int)(totalLength - rendered));
                int copyFloats = samplesAvailable * cacheStride;

                if (isHeadphoneTarget) {
                    float[] stereo = new float[samplesAvailable * outChannels];
                    for (int s = 0; s < samplesAvailable; s++) {
                        for (int ch = 0; ch < outChannels; ch++) {
                            stereo[s * outChannels + ch] = renderedBatch[s * cacheStride + ch];
                        }
                    }
                    virtNormalizer.Process(stereo);
                    writer.WriteBlock(stereo, 0, stereo.Length);
                } else if (speakerVirtFilter != null) {
                    float[] slice = new float[copyFloats];
                    Array.Copy(renderedBatch, slice, copyFloats);
                    speakerVirtFilter.Process(slice, listener.SampleRate);
                    virtNormalizer.Process(slice);
                    writer.WriteChannelLimitedBlock(slice, outChannels, cacheStride, 0, copyFloats);
                } else if (app.RenderTarget is DownmixedRenderTarget downmix) {
                    float[] slice = new float[copyFloats];
                    Array.Copy(renderedBatch, slice, copyFloats);
                    downmix.PerformMerge(slice);
                    writer.WriteChannelLimitedBlock(slice, outChannels, cacheStride, 0, copyFloats);
                } else {
                    writer.WriteBlock(renderedBatch, 0, copyFloats);
                }

                rendered += samplesAvailable;
                double passProg = rendered / (double)totalLength;
                double totalProg = (passIndex + passProg) / totalPasses;
                onProgress?.Invoke(totalProg, $"Render 3D ({passIndex + 1}/{totalPasses}): {passProg:0.0%}");
            }

            if (isHeadphoneTarget) {
                Listener.HeadphoneVirtualizer = true;
            }
            createdFiles.Add(outPath);
            lastSamplesWritten = rendered;
        }

        return new StemExtractionResult {
            CreatedFiles = createdFiles,
            SamplesWritten = lastSamplesWritten,
            SampleRate = outSampleRate
        };
    }

    /// <summary>
    /// Extracts the raw encoded bitstream bytes of a container track to <paramref name="outputPath"/> (equivalent to --extract).
    /// </summary>
    public static long ExtractRawBitstream(
        CavernizeTrack track,
        string outputPath,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(track);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        if (!string.IsNullOrEmpty(track.RawBitstreamPath) && File.Exists(track.RawBitstreamPath)) {
            long totalLen = new FileInfo(track.RawBitstreamPath).Length;
            long copied = 0;
            byte[] buf = new byte[262144];
            using (FileStream src = File.OpenRead(track.RawBitstreamPath))
            using (FileStream dst = File.Create(outputPath)) {
                int read;
                while ((read = src.Read(buf, 0, buf.Length)) > 0) {
                    token.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, read);
                    copied += read;
                    if (totalLen > 0) {
                        double prog = Math.Clamp(copied / (double)totalLen, 0, 1);
                        onProgress?.Invoke(prog, $"Extraindo bitstream bruto ({prog:0.0%})...");
                    }
                }
            }
            onProgress?.Invoke(1, "Bitstream bruto extraído!");
            return copied;
        }

        Track rawTrack = track.Track ?? throw new InvalidOperationException("Only container tracks (MKV/MP4/MXF) support raw bitstream extraction.");

        rawTrack.Source.Seek(0);
        double duration = rawTrack.Source.Duration;
        long totalBytes = 0;
        int blockCount = 0;

        using FileStream output = File.Create(outputPath);
        while (rawTrack.IsNextBlockAvailable()) {
            token.ThrowIfCancellationRequested();
            byte[] block = rawTrack.ReadNextBlock();
            if (block != null && block.Length > 0) {
                output.Write(block, 0, block.Length);
                totalBytes += block.Length;
            }
            if ((++blockCount & 31) == 0 && duration > 0) {
                double offset = rawTrack.GetNextBlockOffset();
                if (offset >= 0) {
                    double progress = Math.Clamp(offset / duration, 0, 1);
                    onProgress?.Invoke(progress, $"Extraindo bitstream bruto ({progress:0.0%})...");
                }
            }
        }
        onProgress?.Invoke(1, "Bitstream bruto extraído!");
        return totalBytes;
    }

    /// <summary>
    /// Performs on-device neural stem separation using the Slot 2B MVSEP anti-artifact pipeline
    /// (raised-cosine 2x–8x overlap-add, TTA, <c>avg_wave</c>/<c>min_fft</c>/<c>avg_fft</c> ensembling,
    /// Wiener <c>phase_fix</c>, exact energy conservation <c>residual = mix - sum(stems)</c>), with optional
    /// zero-phase multichannel Spatial Karaoke Atmos export and Hybrid Neural+DirAC 7.1.4 export.
    /// </summary>
    public static StemExtractionResult ExtractNeuralStems(
        CavernizeTrack track,
        INeuralInferenceRuntime runtime,
        string modelFilePath,
        NeuralModelCatalogEntry modelEntry,
        string outputDir,
        BitDepth bits,
        NeuralAudioConfig config,
        bool exportSpatialKaraokeAtmos,
        bool exportHybrid714,
        Action<double, string> onProgress,
        CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("The selected track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP neural stem extraction requires decoding via truehdd first.");
        }

        NeuralAudioConfig safeConfig = (config ?? NeuralAudioConfig.Default).Sanitize();
        Directory.CreateDirectory(outputDir);
        string baseName = SanitizeFilePart(Path.GetFileNameWithoutExtension(track.Path) ?? "track");
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;

        // 1. Decode track audio (all discrete channels/objects if Spatial Karaoke Atmos is requested, else clean stereo downmix)
        onProgress?.Invoke(0.05, "Decodificando áudio de origem...");
        float[] mixLeft;
        float[] mixRight;
        float[][] multiChannels = null;
        bool[] isLfe = null;
        bool[] isFrontCenter = null;

        if (exportSpatialKaraokeAtmos && allItems.Count > 2) {
            DecodeMultichannelAndStereoDownmix(
                track,
                allItems,
                out multiChannels,
                out isLfe,
                out isFrontCenter,
                out mixLeft,
                out mixRight,
                p => onProgress?.Invoke(0.05 + p * 0.15, $"Decodificando {allItems.Count} canais/objetos ({p:0.0%})..."),
                token);
        } else {
            DecodeStereoDownmix(
                track,
                out mixLeft,
                out mixRight,
                p => onProgress?.Invoke(0.05 + p * 0.15, $"Decodificando estéreo ({p:0.0%})..."),
                token);
        }

        int totalSamples = mixLeft.Length;
        List<string> createdFiles = [];

        // 2. Optional zero-phase multichannel Spatial Karaoke export across all 1..32 Atmos beds & dynamic objects
        if (exportSpatialKaraokeAtmos && multiChannels != null && multiChannels.Length > 0) {
            float[] neuralLeadRef = null;
            float[] neuralBackingRef = null;

            if (runtime != null && runtime.IsAvailable && !string.IsNullOrEmpty(modelFilePath) && File.Exists(modelFilePath)) {
                onProgress?.Invoke(0.22, "Separando guia vocal neural (MVSEP)...");
                using INeuralSession session = runtime.LoadSession(modelFilePath, safeConfig.PreferredProvider);
                NeuralModelCatalogEntry entry = modelEntry ?? NeuralModelCatalog.FindById(safeConfig.ActiveKaraokeModelId);
                StereoStemBuffer[] vocalStems = NeuralAudioOfflinePipeline.RunSeparation(
                    session,
                    entry,
                    mixLeft,
                    mixRight,
                    safeConfig.ToMvsepOptions(),
                    p => onProgress?.Invoke(0.22 + p * 0.48, $"Inferência vocal MVSEP ({p:0.0%})..."),
                    token);
                NeuralSeparatedTrack separated = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
                    track.Path ?? baseName,
                    sampleRate,
                    vocalStems,
                    entry?.Id ?? "custom_onnx");
                neuralLeadRef = separated.MonoLeadVocalRef;
                neuralBackingRef = safeConfig.KaraokePreserveBackingVocals ? separated.MonoBackingVocalRef : null;
            }

            onProgress?.Invoke(0.72, "Aplicando Spatial Karaoke zero-phase multicanal...");
            SpatialKaraokeEngine.ProcessOfflineMultichannel(
                multiChannels,
                isLfe,
                isFrontCenter,
                sampleRate,
                safeConfig.KaraokeVocalLevel,
                safeConfig.KaraokePreserveBackingVocals,
                neuralLeadRef,
                neuralBackingRef);

            for (int c = 0; c < multiChannels.Length; c++) {
                token.ThrowIfCancellationRequested();
                string chName = c < allItems.Count ? allItems[c].GetSafeFileBaseName() : $"ch_{c:00}";
                string wavPath = Path.Combine(outputDir, $"{baseName}_karaoke_{chName}.wav");
                WriteMonoWav(wavPath, multiChannels[c], sampleRate, bits);
                createdFiles.Add(wavPath);
                onProgress?.Invoke(0.75 + 0.25 * ((c + 1) / (double)multiChannels.Length), $"Gravando karaoke {c + 1}/{multiChannels.Length}...");
            }

            onProgress?.Invoke(1.0, "Spatial Karaoke Atmos exportado!");
            return new StemExtractionResult {
                CreatedFiles = createdFiles,
                SamplesWritten = totalSamples,
                SampleRate = sampleRate
            };
        }

        // 3. Standard Neural AI Stem Separation via ONNX Runtime + MVSEP anti-artifact pipeline
        if (runtime == null || !runtime.IsAvailable) {
            throw new InvalidOperationException("ONNX Runtime is not installed. Install it in Settings -> Neural Audio first.");
        }
        if (string.IsNullOrEmpty(modelFilePath) || !File.Exists(modelFilePath)) {
            throw new FileNotFoundException("Selected ONNX neural model is not installed.", modelFilePath);
        }

        onProgress?.Invoke(0.22, "Carregando sessão ONNX Runtime...");
        using INeuralSession stemSession = runtime.LoadSession(modelFilePath, safeConfig.PreferredProvider);
        NeuralModelCatalogEntry catalogEntry = modelEntry ?? NeuralModelCatalog.FindById(safeConfig.ActiveStemModelId);
        MvsepPipelineOptions mvsepOptions = safeConfig.ToMvsepOptions();

        StereoStemBuffer[] stems = NeuralAudioOfflinePipeline.RunSeparation(
            stemSession,
            catalogEntry,
            mixLeft,
            mixRight,
            mvsepOptions,
            p => onProgress?.Invoke(0.25 + p * 0.55, $"Separando stems MVSEP ({p:0.0%})..."),
            token);

        for (int i = 0; i < stems.Length; i++) {
            token.ThrowIfCancellationRequested();
            string stemSafe = SanitizeFilePart(stems[i].Name);
            string wavPath = Path.Combine(outputDir, $"{baseName}_neural_{stemSafe}.wav");
            WriteStereoWav(wavPath, stems[i].Left, stems[i].Right, sampleRate, bits);
            createdFiles.Add(wavPath);
            onProgress?.Invoke(0.80 + 0.12 * ((i + 1) / (double)Math.Max(1, stems.Length)), $"Gravando stem {stems[i].Name}...");
        }

        // 4. Optional Hybrid Neural + DirAC 7.1.4 spatial render
        if (exportHybrid714 || safeConfig.SpatialUpmixMode == NeuralSpatialUpmixMode.HybridNeuralDirAc) {
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke(0.93, "Renderizando Hybrid Neural + DirAC 7.1.4...");
            float[][] bus714 = NeuralStemSpatializer.ProcessHybridNeuralDirAc714(stems, sampleRate, 0.35f);
            ReferenceChannel[] layout714 = [
                ReferenceChannel.FrontLeft,
                ReferenceChannel.FrontRight,
                ReferenceChannel.FrontCenter,
                ReferenceChannel.ScreenLFE,
                ReferenceChannel.RearLeft,
                ReferenceChannel.RearRight,
                ReferenceChannel.SideLeft,
                ReferenceChannel.SideRight,
                ReferenceChannel.TopFrontLeft,
                ReferenceChannel.TopFrontRight,
                ReferenceChannel.TopRearLeft,
                ReferenceChannel.TopRearRight
            ];
            float[][] full12 = new float[12][];
            full12[0] = bus714[0]; // FL
            full12[1] = bus714[1]; // FR
            full12[2] = bus714[2]; // FC
            full12[3] = new float[totalSamples]; // LFE (clean zeroed bass-managed slot)
            full12[4] = bus714[3]; // RL
            full12[5] = bus714[4]; // RR
            full12[6] = bus714[5]; // SL
            full12[7] = bus714[6]; // SR
            full12[8] = bus714[7]; // TFL
            full12[9] = bus714[8]; // TFR
            full12[10] = bus714[9]; // TRL
            full12[11] = bus714[10]; // TRR

            string path714 = Path.Combine(outputDir, $"{baseName}_hybrid_neural_7.1.4.wav");
            WriteMultichannelWav(path714, layout714, full12, sampleRate, bits);
            createdFiles.Add(path714);
        }

        onProgress?.Invoke(1.0, "Stems neurais extraídos!");
        return new StemExtractionResult {
            CreatedFiles = createdFiles,
            SamplesWritten = totalSamples,
            SampleRate = sampleRate
        };
    }

    static void DecodeStereoDownmix(
        CavernizeTrack track,
        out float[] left,
        out float[] right,
        Action<double> onProgress,
        CancellationToken token) {
        if (Listener.EnvironmentSize.X == 0 || Listener.EnvironmentSize.Y == 0 || Listener.EnvironmentSize.Z == 0) {
            Listener.EnvironmentSize = new Vector3(10, 7, 10);
        }
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref200));
        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = 256,
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(false));

        long totalLength = track.Length > 0 ? track.Length : track.SampleRate * 600L;
        int len = (int)Math.Min(totalLength, int.MaxValue / 4);
        left = new float[len];
        right = new float[len];

        int stride = Math.Max(2, Listener.Channels.Length);
        int written = 0;
        int iter = 0;
        while (written < len) {
            token.ThrowIfCancellationRequested();
            int frames = Math.Min(writeBatchFrames, (len - written + updateRate - 1) / updateRate);
            float[] rendered = listener.Render(frames);
            int avail = Math.Min(frames * updateRate, len - written);
            for (int s = 0; s < avail; s++) {
                left[written + s] = Math.Clamp(rendered[s * stride], -1f, 1f);
                right[written + s] = Math.Clamp(rendered[s * stride + 1], -1f, 1f);
            }
            written += avail;
            if ((++iter & 15) == 0 || written >= len) {
                onProgress?.Invoke(Math.Clamp(written / (double)Math.Max(1, len), 0.0, 1.0));
            }
        }
    }

    static void DecodeMultichannelAndStereoDownmix(
        CavernizeTrack track,
        List<TrackSourceItem> items,
        out float[][] channels,
        out bool[] isLfe,
        out bool[] isFrontCenter,
        out float[] mixLeft,
        out float[] mixRight,
        Action<double> onProgress,
        CancellationToken token) {
        if (Listener.EnvironmentSize.X == 0 || Listener.EnvironmentSize.Y == 0 || Listener.EnvironmentSize.Z == 0) {
            Listener.EnvironmentSize = new Vector3(10, 7, 10);
        }
        Listener.HeadphoneVirtualizer = false;
        Listener listener = new(false) {
            SampleRate = track.SampleRate,
            UpdateRate = updateRate,
            AudioQuality = QualityModes.Perfect,
            MaximumSources = Math.Max(256, items.Count),
            Normalizer = 0f
        };
        track.Attach(listener, new UpmixingSettings(true));

        IReadOnlyList<Source> sources = track.Renderer.Objects;
        int chCount = Math.Min(sources.Count, SpatialKaraokeEngine.MaxSpatialChannels);
        long totalLength = track.Length > 0 ? track.Length : track.SampleRate * 600L;
        int len = (int)Math.Min(totalLength, int.MaxValue / Math.Max(4, chCount));

        channels = new float[chCount][];
        isLfe = new bool[chCount];
        isFrontCenter = new bool[chCount];
        for (int c = 0; c < chCount; c++) {
            channels[c] = new float[len];
            isLfe[c] = c < items.Count && items[c].IsLFE;
            isFrontCenter[c] = c < items.Count &&
                (items[c].Channel == ReferenceChannel.FrontCenter ||
                 items[c].Channel == ReferenceChannel.FrontLeft ||
                 items[c].Channel == ReferenceChannel.FrontRight ||
                 items[c].NormalizedPosition.Z >= 0.35f);
        }
        mixLeft = new float[len];
        mixRight = new float[len];

        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        int written = 0;
        int iter = 0;
        float normScale = 1f / MathF.Sqrt(Math.Max(1, chCount));
        while (written < len) {
            token.ThrowIfCancellationRequested();
            listener.Ping();
            int avail = Math.Min(updateRate, len - written);
            for (int c = 0; c < chCount; c++) {
                float[] rendered = sources[c].Rendered?[0];
                if (rendered == null) {
                    continue;
                }
                float panX = Math.Clamp((sources[c].Position * invEnv).X, -1f, 1f);
                float gL = MathF.Sqrt(0.5f * (1f - panX)) * normScale;
                float gR = MathF.Sqrt(0.5f * (1f + panX)) * normScale;
                float[] dst = channels[c];
                for (int s = 0; s < avail; s++) {
                    float v = Math.Clamp(rendered[s], -1f, 1f);
                    dst[written + s] = v;
                    if (!isLfe[c]) {
                        mixLeft[written + s] += v * gL;
                        mixRight[written + s] += v * gR;
                    }
                }
            }
            written += avail;
            if ((++iter & 31) == 0 || written >= len) {
                onProgress?.Invoke(Math.Clamp(written / (double)Math.Max(1, len), 0.0, 1.0));
            }
        }
    }

    static void WriteMonoWav(string path, float[] samples, int sampleRate, BitDepth bits) {
        using RIFFWaveWriter writer = new(path, 1, samples.Length, sampleRate, bits);
        writer.WriteHeader();
        const int block = 8192;
        float[] tmp = new float[block];
        for (int pos = 0; pos < samples.Length; pos += block) {
            int count = Math.Min(block, samples.Length - pos);
            for (int i = 0; i < count; i++) {
                tmp[i] = Math.Clamp(samples[pos + i], -1f, 1f);
            }
            writer.WriteBlock(tmp, 0, count);
        }
    }

    static void WriteStereoWav(string path, float[] left, float[] right, int sampleRate, BitDepth bits) {
        int len = Math.Min(left.Length, right.Length);
        using RIFFWaveWriter writer = new(path, 2, len, sampleRate, bits);
        writer.WriteHeader();
        const int block = 4096;
        float[] interleaved = new float[block * 2];
        for (int pos = 0; pos < len; pos += block) {
            int count = Math.Min(block, len - pos);
            for (int i = 0; i < count; i++) {
                interleaved[i * 2] = Math.Clamp(left[pos + i], -1f, 1f);
                interleaved[i * 2 + 1] = Math.Clamp(right[pos + i], -1f, 1f);
            }
            writer.WriteBlock(interleaved, 0, count * 2);
        }
    }

    static void WriteMultichannelWav(string path, ReferenceChannel[] layout, float[][] channels, int sampleRate, BitDepth bits) {
        int chCount = Math.Min(layout.Length, channels.Length);
        int len = chCount > 0 ? channels[0].Length : 0;
        using RIFFWaveWriter writer = new(path, layout[..chCount], len, sampleRate, bits);
        writer.WriteHeader();
        const int block = 2048;
        float[] interleaved = new float[block * chCount];
        for (int pos = 0; pos < len; pos += block) {
            int count = Math.Min(block, len - pos);
            for (int i = 0; i < count; i++) {
                for (int c = 0; c < chCount; c++) {
                    interleaved[i * chCount + c] = Math.Clamp(channels[c][pos + i], -1f, 1f);
                }
            }
            writer.WriteBlock(interleaved, 0, count * chCount);
        }
    }

    static int[] ResolveTargetIndices(int totalCount, IReadOnlyList<int> selectedIndices) {
        if (selectedIndices == null || selectedIndices.Count == 0) {
            return Enumerable.Range(0, totalCount).ToArray();
        }
        return selectedIndices.Where(i => i >= 0 && i < totalCount).Distinct().OrderBy(i => i).ToArray();
    }

    static bool HasTrajectoryChanged(IReadOnlyList<Source> sources, Vector3 invEnv, Vector3[] lastPositions, float[] lastGains) {
        for (int i = 0; i < sources.Count; i++) {
            Vector3 norm = sources[i].Position * invEnv;
            if (Vector3.DistanceSquared(norm, lastPositions[i]) > 0.0001f ||
                MathF.Abs(sources[i].Volume - lastGains[i]) > 0.01f) {
                return true;
            }
        }
        return false;
    }

    static string SanitizeFilePart(string input) {
        foreach (char invalid in Path.GetInvalidFileNameChars()) {
            input = input.Replace(invalid, '_');
        }
        return input.Replace(' ', '_');
    }
}
