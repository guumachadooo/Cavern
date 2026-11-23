using System.Numerics;
using System.Text.Json;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Neural;
using Cavern.Restoration;
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
    /// Optional localization callback wired by host applications (e.g., <c>AndroidStrings.Active[key]</c>).
    /// When unset or returning the raw key, clean English fallback templates are used.
    /// </summary>
    public static Func<string, string> TextResolver { get; set; }

    static string T(string key, string fallback, params object[] args) {
        string template = null;
        try {
            template = TextResolver?.Invoke(key);
        } catch {
            template = null;
        }
        if (string.IsNullOrEmpty(template) || string.Equals(template, key, StringComparison.Ordinal)) {
            template = fallback;
        }
        return args != null && args.Length > 0 ? string.Format(template, args) : template;
    }

    /// <summary>
    /// Captures and restores global static <see cref="Listener"/> fields so offline exports and extractions
    /// never corrupt active real-time playback layout or headphone virtualizer state.
    /// </summary>
    internal readonly struct ListenerStateScope : IDisposable {
        readonly bool prevVirtualizer;
        readonly Channel[] prevChannels;
        readonly Vector3 prevEnvironmentSize;

        public ListenerStateScope() {
            prevVirtualizer = Listener.HeadphoneVirtualizer;
            prevChannels = Listener.Channels != null ? (Channel[])Listener.Channels.Clone() : null;
            prevEnvironmentSize = Listener.EnvironmentSize;
        }

        public void Dispose() {
            Listener.HeadphoneVirtualizer = prevVirtualizer;
            if (prevEnvironmentSize.X > 0 && prevEnvironmentSize.Y > 0 && prevEnvironmentSize.Z > 0) {
                Listener.EnvironmentSize = prevEnvironmentSize;
            }
            if (prevChannels != null && prevChannels.Length > 0) {
                Listener.ReplaceChannels(prevChannels);
            }
        }
    }

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

        using ListenerStateScope listenerScope = new();
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
                    onProgress?.Invoke(progress, T("StemProgExtractingTracks", "Extracting {0} tracks ({1})...", targets.Length, $"{progress:0.0%}"));
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

        using ListenerStateScope listenerScope = new();

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
                onProgress?.Invoke(totalProg, T("StemProgRender3D", "3D Render ({0}/{1}): {2}", passIndex + 1, totalPasses, $"{passProg:0.0%}"));
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
                        onProgress?.Invoke(prog, T("StemProgExtractingBitstream", "Extracting raw bitstream ({0})...", $"{prog:0.0%}"));
                    }
                }
            }
            onProgress?.Invoke(1, T("StemProgBitstreamDone", "Raw bitstream extracted!"));
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
                    onProgress?.Invoke(progress, T("StemProgExtractingBitstream", "Extracting raw bitstream ({0})...", $"{progress:0.0%}"));
                }
            }
        }
        onProgress?.Invoke(1, T("StemProgBitstreamDone", "Raw bitstream extracted!"));
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
        CancellationToken token = default,
        ILosslessReferenceProvider losslessTwin = null,
        NeuralPipelineOrchestrator orchestrator = null,
        Action<NeuralSeparatedTrack> onSeparatedTrackReady = null,
        bool runStandaloneRestorer = false) {
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

        long sourceFileSize = 0L;
        long sourceLastModifiedTicks = 0L;
        try {
            if (!string.IsNullOrEmpty(track.Path) && File.Exists(track.Path)) {
                FileInfo fi = new(track.Path);
                sourceFileSize = fi.Length;
                sourceLastModifiedTicks = fi.LastWriteTimeUtc.Ticks;
            }
        } catch { }

        // 1. Decode track audio (all discrete channels/objects if Spatial Karaoke Atmos is requested, else clean stereo downmix)
        onProgress?.Invoke(0.05, T("StemProgDecodingSource", "Decoding source audio..."));
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
                p => onProgress?.Invoke(0.05 + p * 0.15, T("StemProgDecodingMultichannel", "Decoding {0} channels/objects ({1})...", allItems.Count, $"{p:0.0%}")),
                token);
        } else {
            DecodeStereoDownmix(
                track,
                out mixLeft,
                out mixRight,
                p => onProgress?.Invoke(0.05 + p * 0.15, T("StemProgDecodingStereo", "Decoding stereo ({0})...", $"{p:0.0%}")),
                token);
        }

        int totalSamples = mixLeft.Length;
        bool derivedFromTwin = false;
        // W09 Universal Lossless Twin acceleration: when a synced FLAC/WAV/ALAC twin is available,
        // run neural separation directly on the sample-aligned lossless stereo buffer for cleaner stems & karaoke mask!
        float[] neuralInputLeft = mixLeft;
        float[] neuralInputRight = mixRight;
        if (losslessTwin != null && losslessTwin.HasSyncedLosslessTwin && totalSamples > 0) {
            if (losslessTwin is UniversalLosslessTwinProvider universalTwin &&
                universalTwin.TryExtractFullAlignedStereo(totalSamples, out float[] alignedL, out float[] alignedR)) {
                neuralInputLeft = alignedL;
                neuralInputRight = alignedR;
                derivedFromTwin = true;
            } else {
                float[] alignedL2 = new float[totalSamples];
                float[] alignedR2 = new float[totalSamples];
                if (losslessTwin.TryReadAlignedStereoBlock(0L, alignedL2, alignedR2, totalSamples)) {
                    neuralInputLeft = alignedL2;
                    neuralInputRight = alignedR2;
                    derivedFromTwin = true;
                }
            }
        }
        List<string> createdFiles = [];

        // 2. Optional zero-phase multichannel Spatial Karaoke export across all 1..32 Atmos beds & dynamic objects
        if (exportSpatialKaraokeAtmos && multiChannels != null && multiChannels.Length > 0) {
            float[] neuralLeadRef = null;
            float[] neuralLeadRefRight = null;
            float[] neuralBackingRef = null;

            NeuralExecutionPlan karaokePlan = orchestrator?.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, safeConfig);
            if (orchestrator != null && karaokePlan != null && karaokePlan.IsRunnable && runtime != null && runtime.GetRuntimeInfo().IsAvailable) {
                onProgress?.Invoke(0.22, $"Orchestrator: {karaokePlan.Summary}");
                (NeuralSeparatedTrack separated, _, _) = orchestrator.ExecuteTrackPipeline(
                    track.Path ?? baseName,
                    sourceFileSize,
                    sourceLastModifiedTicks,
                    neuralInputLeft,
                    neuralInputRight,
                    sampleRate,
                    NeuralPipelineGoal.SpatialKaraoke,
                    safeConfig,
                    derivedFromTwin,
                    (p, msg) => onProgress?.Invoke(0.22 + p * 0.48, msg),
                    token);
                onSeparatedTrackReady?.Invoke(separated);
                ResolveStereoLeadVocalRefs(separated, out neuralLeadRef, out neuralLeadRefRight);
                neuralBackingRef = safeConfig.KaraokePreserveBackingVocals ? separated?.MonoBackingVocalRef : null;
            } else if (runtime != null && runtime.GetRuntimeInfo().IsAvailable && !string.IsNullOrEmpty(modelFilePath) && File.Exists(modelFilePath)) {
                onProgress?.Invoke(0.22, T("StemProgSeparatingKaraoke", "Separating neural vocal guide (MVSEP)..."));
                NeuralModelCatalogEntry entry = modelEntry ?? NeuralModelCatalog.FindById(safeConfig.ActiveKaraokeModelId);
                string[] karaokeStemNames = entry?.OutputStemNames is { Length: > 0 } ? entry.OutputStemNames : ["lead_vocal", "backing_vocals"];
                int karaokeChunk = entry?.RecommendedChunkSamples > 0 ? entry.RecommendedChunkSamples : 352800;
                int karaokeSr = entry?.ExpectedSampleRate > 0 ? entry.ExpectedSampleRate : 44100;
                (StereoStemBuffer[] vocalStems, _) = NeuralAudioOfflinePipeline.RunSeparation(
                    runtime,
                    [modelFilePath],
                    [karaokeStemNames],
                    null,
                    neuralInputLeft,
                    neuralInputRight,
                    sampleRate,
                    safeConfig,
                    karaokeChunk,
                    (p, msg) => onProgress?.Invoke(0.22 + p * 0.48, msg),
                    token,
                    expectedSampleRatesPerModel: [karaokeSr],
                    chunkSamplesPerModel: [karaokeChunk]);
                NeuralSeparatedTrack separated = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
                    track.Path ?? baseName,
                    sampleRate,
                    vocalStems,
                    dereverb: null,
                    derivedFromLosslessTwin: derivedFromTwin);
                onSeparatedTrackReady?.Invoke(separated);
                ResolveStereoLeadVocalRefs(separated, out neuralLeadRef, out neuralLeadRefRight);
                neuralBackingRef = safeConfig.KaraokePreserveBackingVocals ? separated.MonoBackingVocalRef : null;
            }

            bool[] isStrictCenter = new bool[multiChannels.Length];
            for (int c = 0; c < multiChannels.Length && c < allItems.Count; c++) {
                isStrictCenter[c] = !isLfe[c] && allItems[c].Channel == ReferenceChannel.FrontCenter;
            }

            onProgress?.Invoke(0.72, T("StemProgApplyingKaraoke", "Applying zero-phase multichannel Spatial Karaoke..."));
            multiChannels = SpatialKaraokeEngine.ProcessOfflineMultichannel(
                multiChannels,
                isLfe,
                neuralLeadRef,
                neuralBackingRef,
                sampleRate,
                safeConfig.KaraokeVocalLevel,
                safeConfig.KaraokePreserveBackingVocals,
                neuralLeadVocalRefRight: neuralLeadRefRight,
                isStrictCenter: isStrictCenter);

            for (int c = 0; c < multiChannels.Length; c++) {
                token.ThrowIfCancellationRequested();
                string chName = c < allItems.Count ? allItems[c].GetSafeFileBaseName() : $"ch_{c:00}";
                string wavPath = Path.Combine(outputDir, $"{baseName}_karaoke_{chName}.wav");
                WriteMonoWav(wavPath, multiChannels[c], sampleRate, bits);
                createdFiles.Add(wavPath);
                onProgress?.Invoke(0.75 + 0.25 * ((c + 1) / (double)multiChannels.Length),
                    T("StemProgWritingKaraoke", "Writing karaoke {0}/{1}...", c + 1, multiChannels.Length));
            }

            onProgress?.Invoke(1.0, T("StemProgKaraokeDone", "Atmos Spatial Karaoke exported!"));
            return new StemExtractionResult {
                CreatedFiles = createdFiles,
                SamplesWritten = totalSamples,
                SampleRate = sampleRate
            };
        }

        // 2b. Optional standalone Apollo / AP-BWE Neural Restoration via Slot 7A Orchestrator
        if (runStandaloneRestorer) {
            if (runtime == null || !runtime.GetRuntimeInfo().IsAvailable) {
                throw new InvalidOperationException(T("StemErrOnnxNotInstalled", "ONNX Runtime is not installed. Install it in Settings -> Neural Audio first."));
            }
            NeuralExecutionPlan restorePlan = orchestrator?.BuildPlan(NeuralPipelineGoal.LosslessResidualRestore, safeConfig);
            if (orchestrator == null || restorePlan == null || !restorePlan.IsRunnable) {
                throw new FileNotFoundException(T("StemErrModelNotInstalled", "Selected ONNX neural model is not installed."));
            }
            onProgress?.Invoke(0.22, T("StemProgRestoringStandalone", "Restoring audio via Slot 7A ({0})...", restorePlan.Summary));
            (NeuralSeparatedTrack restoredTrack, _, _) = orchestrator.ExecuteTrackPipeline(
                track.Path ?? baseName,
                sourceFileSize,
                sourceLastModifiedTicks,
                neuralInputLeft,
                neuralInputRight,
                sampleRate,
                NeuralPipelineGoal.LosslessResidualRestore,
                safeConfig,
                derivedFromTwin,
                (p, msg) => onProgress?.Invoke(0.25 + p * 0.65, msg),
                token);
            onSeparatedTrackReady?.Invoke(restoredTrack);
            StereoStemBuffer restoredBuf = restoredTrack?.Stems != null && restoredTrack.Stems.Count > 0
                ? restoredTrack.Stems[0]
                : new StereoStemBuffer("restored", neuralInputLeft, neuralInputRight);
            string restoredPath = Path.Combine(outputDir, $"{baseName}_neural_restored.wav");
            WriteStereoWav(restoredPath, restoredBuf.Left, restoredBuf.Right, sampleRate, bits);
            createdFiles.Add(restoredPath);
            onProgress?.Invoke(1.0, T("StemProgRestorerDone", "Neural restoration exported!"));
            return new StemExtractionResult {
                CreatedFiles = createdFiles,
                SamplesWritten = totalSamples,
                SampleRate = sampleRate
            };
        }

        // 3. Standard Neural AI Stem Separation via Slot 7A Orchestrator (+ Slot 7B Disk Cache) or direct ONNX Runtime
        if (runtime == null || !runtime.GetRuntimeInfo().IsAvailable) {
            throw new InvalidOperationException(T("StemErrOnnxNotInstalled", "ONNX Runtime is not installed. Install it in Settings -> Neural Audio first."));
        }

        StereoStemBuffer[] stems;
        NeuralExecutionPlan stemPlan = orchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeConfig);
        if (orchestrator != null && stemPlan != null && stemPlan.IsRunnable) {
            onProgress?.Invoke(0.22, $"Orchestrator: {stemPlan.Summary}");
            (NeuralSeparatedTrack separated, _, _) = orchestrator.ExecuteTrackPipeline(
                track.Path ?? baseName,
                sourceFileSize,
                sourceLastModifiedTicks,
                neuralInputLeft,
                neuralInputRight,
                sampleRate,
                NeuralPipelineGoal.StemSeparation,
                safeConfig,
                derivedFromTwin,
                (p, msg) => onProgress?.Invoke(0.25 + p * 0.55, msg),
                token);
            onSeparatedTrackReady?.Invoke(separated);
            stems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
        } else {
            if (string.IsNullOrEmpty(modelFilePath) || !File.Exists(modelFilePath)) {
                throw new FileNotFoundException(T("StemErrModelNotInstalled", "Selected ONNX neural model is not installed."), modelFilePath);
            }

            onProgress?.Invoke(0.22, T("StemProgLoadingOnnx", "Loading ONNX Runtime session..."));
            NeuralModelCatalogEntry catalogEntry = modelEntry ?? NeuralModelCatalog.FindById(safeConfig.ActiveStemModelId);
            string[] stemNames = catalogEntry?.OutputStemNames is { Length: > 0 } ? catalogEntry.OutputStemNames : ["drums", "bass", "other", "vocals"];
            int stemChunk = catalogEntry?.RecommendedChunkSamples > 0 ? catalogEntry.RecommendedChunkSamples : 131072;
            int stemSr = catalogEntry?.ExpectedSampleRate > 0 ? catalogEntry.ExpectedSampleRate : 44100;

            (stems, _) = NeuralAudioOfflinePipeline.RunSeparation(
                runtime,
                [modelFilePath],
                [stemNames],
                null,
                neuralInputLeft,
                neuralInputRight,
                sampleRate,
                safeConfig,
                stemChunk,
                (p, msg) => onProgress?.Invoke(0.25 + p * 0.55, msg),
                token,
                expectedSampleRatesPerModel: [stemSr],
                chunkSamplesPerModel: [stemChunk]);

            NeuralSeparatedTrack separated = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
                track.Path ?? baseName,
                sampleRate,
                stems,
                dereverb: null,
                derivedFromLosslessTwin: derivedFromTwin);
            onSeparatedTrackReady?.Invoke(separated);
        }

        for (int i = 0; i < stems.Length; i++) {
            token.ThrowIfCancellationRequested();
            string stemSafe = SanitizeFilePart(stems[i].Name);
            string wavPath = Path.Combine(outputDir, $"{baseName}_neural_{stemSafe}.wav");
            WriteStereoWav(wavPath, stems[i].Left, stems[i].Right, sampleRate, bits);
            createdFiles.Add(wavPath);
            onProgress?.Invoke(0.80 + 0.12 * ((i + 1) / (double)Math.Max(1, stems.Length)),
                T("StemProgWritingStem", "Writing stem {0}...", stems[i].Name));
        }

        // 4. Optional Hybrid Neural + DirAC 7.1.4 spatial render (canonical 12-ch Layout714 + complementary 120 Hz LR4 LFE)
        if (exportHybrid714 || safeConfig.SpatialUpmixMode == NeuralSpatialUpmixMode.HybridNeuralDirAc) {
            token.ThrowIfCancellationRequested();
            onProgress?.Invoke(0.93, T("StemProgRenderingHybrid714", "Rendering Hybrid Neural + DirAC 7.1.4..."));
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
            float[][] full12 = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sampleRate, 0.35f, 120f);
            string path714 = Path.Combine(outputDir, $"{baseName}_hybrid_neural_7.1.4.wav");
            WriteMultichannelWav(path714, layout714, full12, sampleRate, bits);
            createdFiles.Add(path714);
        }

        onProgress?.Invoke(1.0, T("StemProgNeuralDone", "Neural stems extracted!"));
        return new StemExtractionResult {
            CreatedFiles = createdFiles,
            SamplesWritten = totalSamples,
            SampleRate = sampleRate
        };
    }

    /// <summary>
    /// W09 Tier 2 Offline Studio Master: separates a synced lossless FLAC/WAV/ALAC twin into clean neural stems
    /// via Slot 7A MVSEP Orchestrator (+ Slot 7B Disk Cache), projects them onto decoded multichannel Atmos beds
    /// &amp; JOC objects via iterative EM-MWF (<c>sigsep/norbert</c>), preserves 100% of Atmos-exclusive objects/ambience
    /// in a conserved residual, and optionally runs Tier 3 <c>Apollo</c>/<c>AP-BWE</c> neural restoration on the residual.
    /// </summary>
    public static StemExtractionResult ExtractLosslessHybridSpatialMaster(
        CavernizeTrack track,
        UniversalLosslessTwinProvider losslessTwin,
        INeuralInferenceRuntime runtime,
        string stemModelPath,
        NeuralModelCatalogEntry stemModelEntry,
        string outputDir,
        BitDepth bits,
        NeuralAudioConfig neuralConfig,
        LgsrConfig lgsrConfig,
        string tier3RestorerModelPath,
        NeuralModelCatalogEntry tier3RestorerEntry,
        bool exportResidualTrack,
        Action<double, string> onProgress,
        CancellationToken token = default,
        NeuralPipelineOrchestrator orchestrator = null,
        Action<NeuralSeparatedTrack> onSeparatedTrackReady = null,
        string combinedMasterOutputPath = null,
        Action<float[][], ReferenceChannel[]> onMasterChannelsReady = null) {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(losslessTwin);
        if (!losslessTwin.HasSyncedLosslessTwin) {
            throw new InvalidOperationException("A synced lossless twin (FLAC/WAV/ALAC) is required for Tier 2 EM-MWF Studio Master.");
        }
        if (runtime == null || !runtime.GetRuntimeInfo().IsAvailable) {
            throw new InvalidOperationException(T("StemErrOnnxNotInstalled", "ONNX Runtime is not installed. Install it in Settings -> Neural Audio first."));
        }

        NeuralAudioConfig safeNeural = (neuralConfig ?? NeuralAudioConfig.Default).Sanitize();
        LgsrConfig safeLgsr = (lgsrConfig ?? LgsrConfig.Default).Sanitize();
        NeuralExecutionPlan stemPlan = orchestrator?.BuildPlan(NeuralPipelineGoal.StemSeparation, safeNeural);
        bool useOrchestratorStems = orchestrator != null && stemPlan != null && stemPlan.IsRunnable;
        if (!useOrchestratorStems && (string.IsNullOrEmpty(stemModelPath) || !File.Exists(stemModelPath))) {
            throw new FileNotFoundException(T("StemErrModelNotInstalled", "Selected ONNX neural model is not installed."), stemModelPath);
        }

        Directory.CreateDirectory(outputDir);
        string baseName = SanitizeFilePart(Path.GetFileNameWithoutExtension(track.Path) ?? "track");
        List<TrackSourceItem> allItems = TrackSourceItem.BuildFromTrack(track);
        int sampleRate = track.SampleRate > 0 ? track.SampleRate : Listener.DefaultSampleRate;

        long sourceFileSize = 0L;
        long sourceLastModifiedTicks = 0L;
        string twinIdentityPath = !string.IsNullOrEmpty(losslessTwin.Metadata?.FilePath)
            ? losslessTwin.Metadata.FilePath
            : (track.Path ?? baseName);
        try {
            if (!string.IsNullOrEmpty(twinIdentityPath) && File.Exists(twinIdentityPath)) {
                FileInfo fi = new(twinIdentityPath);
                sourceFileSize = fi.Length;
                sourceLastModifiedTicks = fi.LastWriteTimeUtc.Ticks;
            }
        } catch { }

        // 1. Decode multichannel Atmos beds & dynamic JOC objects
        onProgress?.Invoke(0.05, T("StemProgDecodingAtmos", "Decoding Atmos beds and objects..."));
        DecodeMultichannelAndStereoDownmix(
            track,
            allItems,
            out float[][] atmosChannels,
            out bool[] isLfe,
            out _,
            out _,
            out _,
            p => onProgress?.Invoke(0.05 + p * 0.15, T("StemProgDecodingAtmosPct", "Decoding Atmos ({0})...", $"{p:0.0%}")),
            token);

        int totalSamples = atmosChannels.Length > 0 ? atmosChannels[0].Length : 0;
        if (totalSamples <= 0 || !losslessTwin.TryExtractFullAlignedStereo(totalSamples, out float[] twinL, out float[] twinR)) {
            throw new InvalidOperationException("Failed to extract sample-aligned lossless twin audio.");
        }

        // 2. Separate clean neural stems directly from the synced lossless FLAC/WAV/ALAC twin
        //    Prefer Slot 7A Orchestrator (multi-model ensemble + Debleed + Slot 7B disk cache).
        StereoStemBuffer[] losslessStems;
        if (useOrchestratorStems) {
            onProgress?.Invoke(0.22, $"Orchestrator: {stemPlan.Summary}");
            (NeuralSeparatedTrack separated, _, _) = orchestrator.ExecuteTrackPipeline(
                twinIdentityPath,
                sourceFileSize,
                sourceLastModifiedTicks,
                twinL,
                twinR,
                sampleRate,
                NeuralPipelineGoal.StemSeparation,
                safeNeural,
                derivedFromLosslessTwin: true,
                onProgress: (p, msg) => onProgress?.Invoke(0.22 + p * 0.40, msg),
                token: token);
            onSeparatedTrackReady?.Invoke(separated);
            losslessStems = separated?.Stems != null ? separated.Stems.ToArray() : Array.Empty<StereoStemBuffer>();
        } else {
            onProgress?.Invoke(0.22, T("StemProgSeparatingLossless", "Separating lossless stems (MVSEP)..."));
            NeuralModelCatalogEntry entry = stemModelEntry ?? NeuralModelCatalog.FindById(safeNeural.ActiveStemModelId);
            string[] twinStemNames = entry?.OutputStemNames is { Length: > 0 } ? entry.OutputStemNames : ["drums", "bass", "other", "vocals"];
            int twinChunk = entry?.RecommendedChunkSamples > 0 ? entry.RecommendedChunkSamples : 131072;
            int twinSr = entry?.ExpectedSampleRate > 0 ? entry.ExpectedSampleRate : 44100;
            (losslessStems, _) = NeuralAudioOfflinePipeline.RunSeparation(
                runtime,
                [stemModelPath],
                [twinStemNames],
                null,
                twinL,
                twinR,
                sampleRate,
                safeNeural,
                twinChunk,
                (p, msg) => onProgress?.Invoke(0.22 + p * 0.40, msg),
                token,
                expectedSampleRatesPerModel: [twinSr],
                chunkSamplesPerModel: [twinChunk]);
            NeuralSeparatedTrack separated = NeuralAudioOfflinePipeline.BuildSeparatedTrack(
                twinIdentityPath,
                sampleRate,
                losslessStems,
                dereverb: null,
                derivedFromLosslessTwin: true);
            onSeparatedTrackReady?.Invoke(separated);
        }

        // 3. Optional Tier 3 Apollo / AP-BWE residual cleanup delegate via Slot 7A Orchestrator or direct model path
        Func<float[], float[], StereoStemBuffer> tier3Delegate = null;
        if (safeLgsr.EnableTier3ResidualCleanup) {
            NeuralExecutionPlan tier3Plan = orchestrator?.BuildPlan(NeuralPipelineGoal.LosslessResidualRestore, safeNeural);
            if (orchestrator != null && tier3Plan != null && tier3Plan.IsRunnable) {
                tier3Delegate = (resL, resR) => orchestrator.RestoreStereoBuffer(resL, resR, sampleRate, safeNeural, null, token);
            } else if (!string.IsNullOrEmpty(tier3RestorerModelPath) && File.Exists(tier3RestorerModelPath)) {
                NeuralModelCatalogEntry restorerEntry = tier3RestorerEntry ?? NeuralModelCatalog.FindById(safeNeural.ActiveRestorerModelId);
                string[] restorerNames = restorerEntry?.OutputStemNames is { Length: > 0 } ? restorerEntry.OutputStemNames : ["restored"];
                int restorerChunk = restorerEntry?.RecommendedChunkSamples > 0 ? restorerEntry.RecommendedChunkSamples : 262144;
                int restorerSr = restorerEntry?.ExpectedSampleRate > 0 ? restorerEntry.ExpectedSampleRate : 44100;
                tier3Delegate = (resL, resR) => {
                    (StereoStemBuffer[] cleaned, _) = NeuralAudioOfflinePipeline.RunSeparation(
                        runtime,
                        [tier3RestorerModelPath],
                        [restorerNames],
                        null,
                        resL,
                        resR,
                        sampleRate,
                        safeNeural,
                        restorerChunk,
                        null,
                        token,
                        expectedSampleRatesPerModel: [restorerSr],
                        chunkSamplesPerModel: [restorerChunk]);
                    return cleaned != null && cleaned.Length > 0
                        ? cleaned[0]
                        : new StereoStemBuffer("residual", resL, resR);
                };
            }
        }

        onProgress?.Invoke(0.64, T("StemProgProjectingEmMwf", "Projecting lossless stems via EM-MWF (norbert)..."));
        EmMwfSpatialMasterResult master = EmMwfSpatialMasterEngine.SynthesizeStudioMaster(
            atmosChannels,
            isLfe,
            losslessStems,
            sampleRate,
            emIterations: 2,
            tier3ResidualRestorer: tier3Delegate,
            onProgress: p => onProgress?.Invoke(0.64 + p * 0.24, T("StemProgEmMwfPct", "EM-MWF Studio Master ({0})...", $"{p:0.0%}")));

        // 4. Write either a single unified multichannel WAV master (StudioBatchExporter) or discrete per-channel stems
        List<string> createdFiles = [];
        ReferenceChannel[] channelLayout = BuildChannelLayoutForItems(allItems, master.RestoredChannels.Length);
        onMasterChannelsReady?.Invoke(master.RestoredChannels, channelLayout);

        if (!string.IsNullOrEmpty(combinedMasterOutputPath)) {
            Directory.CreateDirectory(Path.GetDirectoryName(combinedMasterOutputPath) ?? outputDir);
            WriteMultichannelWav(combinedMasterOutputPath, channelLayout, master.RestoredChannels, sampleRate, bits);
            createdFiles.Add(combinedMasterOutputPath);
        } else {
            for (int c = 0; c < master.RestoredChannels.Length; c++) {
                token.ThrowIfCancellationRequested();
                string chName = c < allItems.Count ? allItems[c].GetSafeFileBaseName() : $"ch_{c:00}";
                string wavPath = Path.Combine(outputDir, $"{baseName}_lgsr_master_{chName}.wav");
                WriteMonoWav(wavPath, master.RestoredChannels[c], sampleRate, bits);
                createdFiles.Add(wavPath);
                if (exportResidualTrack && !isLfe[c]) {
                    string resPath = Path.Combine(outputDir, $"{baseName}_lgsr_residual_{chName}.wav");
                    WriteMonoWav(resPath, master.AtmosExclusiveResidual[c], sampleRate, bits);
                    createdFiles.Add(resPath);
                }
                onProgress?.Invoke(0.88 + 0.12 * ((c + 1) / (double)Math.Max(1, master.RestoredChannels.Length)),
                    T("StemProgWritingMaster", "Writing master {0}/{1}...", c + 1, master.RestoredChannels.Length));
            }
        }

        onProgress?.Invoke(1.0, T("StemProgMasterDone", "LGSR Studio Master (EM-MWF) complete!"));
        return new StemExtractionResult {
            CreatedFiles = createdFiles,
            SamplesWritten = totalSamples,
            SampleRate = sampleRate
        };
    }

    /// <summary>
    /// Derives a pure-physics 4th-order Linkwitz-Riley (24 dB/oct, 120 Hz) band-limited LFE channel for
    /// Hybrid Neural + DirAC 7.1.4 export from the separated Bass + Drums stems (or FL/FR sum if neither exists),
    /// preserving ITU-R BS.775 / Dolby discrete sub-bass headroom.
    /// </summary>
    internal static float[] BuildBassManagedLfe(
        StereoStemBuffer[] stems,
        float[] fallbackLeft,
        float[] fallbackRight,
        int totalSamples,
        int sampleRate) {
        float[] lfe = new float[Math.Max(0, totalSamples)];
        if (totalSamples <= 0) {
            return lfe;
        }
        bool summedAnyStem = false;
        if (stems != null) {
            for (int i = 0; i < stems.Length; i++) {
                StereoStemBuffer st = stems[i];
                if (st == null || st.Left == null || st.Right == null) {
                    continue;
                }
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
                if (role == NeuralStemRole.Bass || role == NeuralStemRole.Drums) {
                    int copyLen = Math.Min(totalSamples, Math.Min(st.Left.Length, st.Right.Length));
                    for (int n = 0; n < copyLen; n++) {
                        lfe[n] += 0.5f * (st.Left[n] + st.Right[n]);
                    }
                    summedAnyStem = true;
                }
            }
        }

        if (!summedAnyStem) {
            if (fallbackLeft == null || fallbackRight == null) {
                return lfe;
            }
            int copyLen = Math.Min(totalSamples, Math.Min(fallbackLeft.Length, fallbackRight.Length));
            for (int i = 0; i < copyLen; i++) {
                lfe[i] = 0.5f * (fallbackLeft[i] + fallbackRight[i]);
            }
        }

        int safeSr = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        NeuralStemSpatializer.SplitLinkwitzRiley4Lfe(lfe, safeSr, 120f, out _, out float[] subBass);
        return subBass;
    }

    static ReferenceChannel[] BuildChannelLayoutForItems(List<TrackSourceItem> items, int count) {
        if (count <= 0) {
            return [];
        }
        ReferenceChannel[] std = ChannelPrototype.GetStandardMatrix(count);
        if (items == null || items.Count < count) {
            return std;
        }
        ReferenceChannel[] layout = new ReferenceChannel[count];
        HashSet<ReferenceChannel> used = [];
        for (int i = 0; i < count; i++) {
            ReferenceChannel candidate = items[i].Channel ?? (i < std.Length ? std[i] : ReferenceChannel.Unknown);
            if (candidate == ReferenceChannel.Unknown || !used.Add(candidate)) {
                candidate = i < std.Length && used.Add(std[i]) ? std[i] : ReferenceChannel.Unknown;
            }
            layout[i] = candidate;
        }
        return layout;
    }

    static void DecodeStereoDownmix(
        CavernizeTrack track,
        out float[] left,
        out float[] right,
        Action<double> onProgress,
        CancellationToken token) {
        using ListenerStateScope listenerScope = new();
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
        using ListenerStateScope listenerScope = new();
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

    static void ResolveStereoLeadVocalRefs(NeuralSeparatedTrack separated, out float[] leadL, out float[] leadR) {
        leadL = separated?.MonoLeadVocalRef;
        leadR = null;
        if (separated?.Stems == null || separated.Stems.Count == 0) {
            return;
        }
        StereoStemBuffer fallback = null;
        for (int i = 0; i < separated.Stems.Count; i++) {
            StereoStemBuffer st = separated.Stems[i];
            if (st == null || st.Length <= 0 || st.Left == null || st.Right == null) {
                continue;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
            if (role == NeuralStemRole.LeadVocal) {
                leadL = st.Left;
                leadR = st.Right;
                return;
            }
            if (role == NeuralStemRole.Vocals && fallback == null) {
                fallback = st;
            }
        }
        if (fallback != null) {
            leadL = fallback.Left;
            leadR = fallback.Right;
        }
    }

    static string SanitizeFilePart(string input) {
        foreach (char invalid in Path.GetInvalidFileNameChars()) {
            input = input.Replace(invalid, '_');
        }
        return input.Replace(' ', '_');
    }
}
