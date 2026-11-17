using System.Numerics;
using System.Text.Json;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;
using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

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
            VirtualizerFilter speakerVirtFilter = null;
            Normalizer virtNormalizer = null;
            if (isHeadphoneTarget) {
                Listener.HeadphoneVirtualizer = true;
                new VirtualizerFilter().SetLayout();
                virtNormalizer = new Normalizer(true) {
                    decayFactor = 10f * listener.UpdateRate / listener.SampleRate
                };
            } else if (isSpeakerVirt) {
                Listener.HeadphoneVirtualizer = false;
                speakerVirtFilter = new VirtualizerFilter();
                speakerVirtFilter.SetLayout();
                virtNormalizer = new Normalizer(true) {
                    decayFactor = 10f * listener.UpdateRate / listener.SampleRate
                };
            }

            IReadOnlyList<Source> trackObjects = track.Renderer.Objects;
            foreach (Source src in trackObjects) {
                src.DistanceSimulation = distanceSim && isHeadphoneTarget;
                src.VolumeRolloff = rolloff;
            }

            long totalLength = (long)Math.Ceiling(track.Length * (listener.SampleRate / (double)track.SampleRate));
            int outChannels = app.RenderTarget.OutputChannels;
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
        Track rawTrack = track.Track ?? throw new InvalidOperationException("Only container tracks (MKV/MP4/MXF) support raw bitstream extraction.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
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
