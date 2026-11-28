using Cavern.Format;
using Cavern.Format.Decoders;
using Cavern.Format.Utilities;
using Cavern.Restoration;

namespace Cavernize.Logic.CommandLine.HiddenCommands;

/// <summary>
/// Converts an Enhanced AC-3 bitstream to raw samples, replaces the PCM data with an external lossless bed,
/// optionally phase-aligned to sub-sample precision via <see cref="SubSampleSyncEngine"/>.
/// </summary>
/// <param name="reader">Accesses the linear E-AC-3 bitstream</param>
/// <param name="fileSize">Length of the E-AC-3 bitstream</param>
/// <param name="overrider">Stream to override the PCM data with - only applies to the source PCM data,
/// not the JOC-decoded objects</param>
/// <param name="subSampleOffsetFrames">Optional fractional frame offset (overriderFrame = eac3Frame + offset)
/// computed via <see cref="SubSampleSyncEngine.Align"/>.</param>
public sealed class OverrideBedDecoder(
    BlockBuffer<byte> reader,
    long fileSize,
    AudioReader overrider,
    double subSampleOffsetFrames = 0.0) : EnhancedAC3Decoder(reader, fileSize) {

    bool offsetApplied;
    float[] prevFrameCarry;
    readonly double fracOffset = subSampleOffsetFrames - Math.Floor(subSampleOffsetFrames);
    readonly long intOffsetFrames = (long)Math.Floor(subSampleOffsetFrames);

    /// <summary>
    /// Computes sub-sample alignment between an E-AC-3 reference mono probe and an external lossless bed mono probe
    /// using <see cref="SubSampleSyncEngine"/> (3-stage GCC-PHAT + parabolic peak interpolation).
    /// </summary>
    public static SubSampleSyncResult AlignOverrideBed(
        float[] eac3BedMonoProbe,
        float[] losslessBedMonoProbe,
        int sampleRate,
        float maxSearchSeconds = SubSampleSyncEngine.DefaultMaxMacroSearchSeconds) {
        if (eac3BedMonoProbe == null || losslessBedMonoProbe == null ||
            eac3BedMonoProbe.Length < SubSampleSyncEngine.GccFftSize ||
            losslessBedMonoProbe.Length < SubSampleSyncEngine.GccFftSize) {
            return default;
        }
        return SubSampleSyncEngine.Align(eac3BedMonoProbe, losslessBedMonoProbe, sampleRate, maxSearchSeconds);
    }

    /// <inheritdoc/>
    protected override float[] DecodeFrame() {
        float[] result = base.DecodeFrame();
        if (overrider == null || result == null || result.Length == 0) {
            return result;
        }
        int channels = Math.Max(1, ChannelCount);
        if (!offsetApplied) {
            offsetApplied = true;
            if (intOffsetFrames > 0) {
                long samplesToSkip = intOffsetFrames * channels;
                float[] scratch = new float[Math.Min(samplesToSkip, 4096 * channels)];
                while (samplesToSkip > 0) {
                    int take = (int)Math.Min(samplesToSkip, scratch.Length);
                    overrider.ReadBlock(scratch, 0, take);
                    samplesToSkip -= take;
                }
            }
        }
        overrider.ReadBlock(result, 0, result.Length);
        if (Math.Abs(fracOffset) > 1e-4) {
            ApplyFractionalShiftInPlace(result, channels, (float)fracOffset);
        }
        return result;
    }

    void ApplyFractionalShiftInPlace(float[] interleaved, int channels, float frac) {
        int frames = interleaved.Length / channels;
        if (frames < 2) {
            return;
        }
        prevFrameCarry ??= new float[channels];
        float oneMinus = 1f - frac;
        for (int ch = 0; ch < channels; ch++) {
            float prev = prevFrameCarry[ch];
            for (int f = 0; f < frames; f++) {
                int idx = f * channels + ch;
                float cur = interleaved[idx];
                interleaved[idx] = prev * frac + cur * oneMinus;
                prev = cur;
            }
            prevFrameCarry[ch] = prev;
        }
    }
}
