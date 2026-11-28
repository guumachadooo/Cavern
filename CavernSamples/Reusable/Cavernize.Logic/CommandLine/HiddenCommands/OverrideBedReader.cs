using Cavern.Format;
using Cavern.Format.Decoders;
using Cavern.Format.Utilities;

namespace Cavernize.Logic.CommandLine.HiddenCommands;

/// <summary>
/// Enhanced AC-3 file reader that reads the PCM data from another file, optionally phase-aligned via
/// <see cref="Cavern.Restoration.SubSampleSyncEngine"/>.
/// </summary>
/// <param name="path">Path of the original E-AC-3 file</param>
/// <param name="overrider">Stream to override the PCM data with - only applies to the source PCM data,
/// not the JOC-decoded objects</param>
/// <param name="subSampleOffsetFrames">Sub-sample frame offset computed by <see cref="OverrideBedDecoder.AlignOverrideBed"/></param>
public sealed class OverrideBedReader(string path, AudioReader overrider, double subSampleOffsetFrames = 0.0) : EnhancedAC3Reader(path) {
    /// <inheritdoc/>
    public override EnhancedAC3Decoder CreateDecoder(bool skipSyncWord) =>
        new OverrideBedDecoder(BlockBuffer<byte>.Create(reader, 10 * 1024 * 1024), fileSize, overrider, subSampleOffsetFrames);
}
