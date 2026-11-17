using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Renderers;
using Cavern.Remapping;
using Cavern.Remapping.Exceptions;
using Cavern.Utilities;

using Cavernize.Logic.Language;

namespace Cavernize.Logic.Models;

/// <summary>
/// Represents an audio track of the loaded audio file.
/// </summary>
public class CavernizeTrack : IDisposable, IMetadataSupplier {
    /// <summary>
    /// This track can be rendered.
    /// </summary>
    public bool Supported { get; }

    /// <summary>
    /// Track audio coding type.
    /// </summary>
    public Codec Codec { get; protected set; }

    /// <summary>
    /// The track's container's file format.
    /// </summary>
    public Container Container => reader is AudioTrackReader trackReader ? trackReader.Source.Type : Container.NotContainer;

    /// <summary>
    /// The renderer starting at the first sample of the track after construction.
    /// </summary>
    public Renderer Renderer { get; private set; }

    /// <summary>
    /// Number of samples to render by the <see cref="Listener"/> to reach the end of the stream.
    /// </summary>
    public long Length => reader.Length;

    /// <summary>
    /// Sampling rate of the track.
    /// </summary>
    public int SampleRate => reader.SampleRate;

    /// <summary>
    /// Order in the tracks of the input container.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Language code.
    /// </summary>
    public string Language { get; protected set; }

    /// <summary>
    /// Major format information to display first when this track is selected.
    /// </summary>
    public string FormatHeader { get; protected set; }

    /// <summary>
    /// Main properties of this audio track.
    /// </summary>
    public (string property, string value)[] Details { get; protected set; }

    /// <summary>
    /// The file in which this track is stored, or null if it's a network stream.
    /// </summary>
    public string Path => reader.Path;

    /// <summary>
    /// Raw access to the track's stream.
    /// </summary>
    public Track Track => reader is AudioTrackReader trackReader ? trackReader.track : null;

    /// <summary>
    /// Raw access to the underlying <see cref="AudioReader"/>.
    /// </summary>
    public AudioReader Reader => reader;

    /// <summary>
    /// Current sample position in the stream per channel, or -1 if unsupported.
    /// </summary>
    public long Position {
        get => reader.Position;
        set => reader.Position = value;
    }

    /// <summary>
    /// Optional per-source mute mask indexed by <see cref="Renderer.Objects"/> order.
    /// </summary>
    public bool[] CustomMuteMask { get; set; }

    /// <summary>
    /// Source of audio data.
    /// </summary>
    AudioReader reader;

    /// <summary>
    /// Reads information from a track's renderer.
    /// </summary>
    public CavernizeTrack(AudioReader reader, Codec codec, int index) {
        this.reader = reader;
        Codec = codec;
        Index = index;

        Renderer = reader.GetRenderer();
        Supported = Renderer is not DummyRenderer;
        EnhancedAC3Renderer eac3 = Renderer as EnhancedAC3Renderer;

        TrackStrings strings = TrackStrings.Active;
        if (!Supported) {
            FormatHeader = strings["NoSup"];
        } else if (eac3 != null) {
            if (eac3.HasObjects) {
                FormatHeader = strings["E3JOC"];
            } else {
                FormatHeader = eac3.Enhanced ? "Enhanced AC-3" : "AC-3";
            }
        } else {
            FormatHeader = Renderer.HasObjects ? strings["ObTra"] : strings["ChTra"];
        }
        FormatHeader += $" ({TimeSpan.FromSeconds(reader.Length / (double)reader.SampleRate):h\\:mm\\:ss})";

        ReferenceChannel[] beds = Renderer != null ? Renderer.GetChannels() : [];
        string bedList = string.Join(' ', ChannelPrototype.GetShortNames(beds));
        List<(string, string)> builder = [];
        if (eac3 != null && eac3.HasObjects) {
            builder.Add((strings["SouCh"], $"{beds.Length} - {bedList}"));
            string[] newBeds = ChannelPrototype.GetShortNames(eac3.GetStaticChannels());
            builder.Add((strings["MatBe"], $"{newBeds.Length} - {string.Join(' ', newBeds)}"));
            builder.Add((strings["MatOb"], eac3.DynamicObjects.ToString()));
        } else {
            if (Renderer != null && beds.Length != Renderer.Objects.Count) { // Generic object-based format: bed and object lines
                if (beds.Length > 0) {
                    builder.Add((strings["SouBe"], $"{beds.Length} - {bedList}"));
                }
                builder.Add((strings["SouDy"], (Renderer.Objects.Count - beds.Length).ToString()));
            } else if (beds.Length > 0) { // Generic channel-based format of known channels
                builder.Add((strings["Chans"], $"{beds.Length} - {bedList}"));
            } else { // Generic channel-based format of unknown channels
                builder.Add((strings["Chans"], reader.ChannelCount.ToString()));
            }
        }
        Details = [.. builder];
    }

    /// <summary>
    /// Reads information from a track's renderer.
    /// </summary>
    public CavernizeTrack(AudioReader reader, Codec codec, int index, string language) :
        this(reader, codec, index) => Language = language;

    /// <summary>
    /// Constructor for derived classes.
    /// </summary>
    protected CavernizeTrack() { }

    /// <summary>
    /// Attach this track to a rendering environment and start from the beginning.
    /// </summary>
    /// <param name="listener">Listener to attach the track's <see cref="Source"/>s to</param>
    /// <param name="upmixingSettings">Optionally create new <see cref="Source"/>s to expand the soundstage</param>
    public void Attach(Listener listener, UpmixingSettings upmixingSettings) {
        reader.Reset();
        AttachFromCurrentPosition(listener, upmixingSettings);
    }

    /// <summary>
    /// Attach this track to a rendering environment starting from the reader's current position.
    /// </summary>
    /// <param name="listener">Listener to attach the track's <see cref="Source"/>s to</param>
    /// <param name="upmixingSettings">Optionally create new <see cref="Source"/>s to expand the soundstage</param>
    public void AttachFromCurrentPosition(Listener listener, UpmixingSettings upmixingSettings) {
        Renderer = reader.GetRenderer();
        listener.SampleRate = SampleRate;
        Source[] attachables = SetupUpmixer(listener, upmixingSettings);

        if (upmixingSettings.Cavernize && !Renderer.HasObjects && Renderer.Objects.All(x => x.LFE || x.Position.Y == 0)) {
            CavernizeUpmixer cavernizer = new CavernizeUpmixer(attachables, SampleRate) {
                Effect = upmixingSettings.Effect,
                Smoothness = upmixingSettings.Smoothness
            };
            attachables = cavernizer.IntermediateSources;
        }

        if (CustomMuteMask != null && attachables.Length == Renderer.Objects.Count) {
            for (int i = 0; i < CustomMuteMask.Length && i < attachables.Length; i++) {
                if (CustomMuteMask[i]) {
                    attachables[i].Mute = true;
                }
            }
        }

        listener.AttachSources(attachables);
    }

    /// <summary>
    /// Seek the underlying audio stream to <paramref name="targetSample"/> per channel and return the actual sample position.
    /// </summary>
    public long Seek(long targetSample) {
        long maxSample = Length > 0 ? Length - 1 : 0;
        targetSample = Math.Clamp(targetSample, 0, maxSample);

        if (targetSample <= 0) {
            if (reader is EnhancedAC3Reader && !string.IsNullOrEmpty(Path)) {
                string path = Path;
                reader.Dispose();
                reader = new EnhancedAC3Reader(path);
                reader.ReadHeader();
            } else {
                reader.Reset();
            }
            return 0;
        }

        if (reader is AudioTrackReader trackReader) {
            double actualSeconds = trackReader.Source.Seek(targetSample / (double)SampleRate);
            trackReader.ReadHeader();
            return actualSeconds >= 0 ? (long)(actualSeconds * SampleRate) : targetSample;
        }

        if (reader is EnhancedAC3Reader && !string.IsNullOrEmpty(Path)) {
            string path = Path;
            reader.Dispose();
            reader = new EnhancedAC3Reader(path);
            reader.ReadHeader();
            reader.Position = targetSample;
            return targetSample;
        }

        reader.Position = targetSample;
        return reader.Position >= 0 ? reader.Position : targetSample;
    }

    /// <summary>
    /// Get the video tracks this audio track is accompanying.
    /// </summary>
    public Track[] GetVideoTracks() => reader is AudioTrackReader trackReader ? [.. trackReader.Source.Tracks.Where(x => x.Format.IsVideo())] : null;

    /// <summary>
    /// Gets the metadata for this codec in a human-readable format.
    /// </summary>
    public ReadableMetadata GetMetadata() => reader is IMetadataSupplier meta ? meta.GetMetadata() : null;

    /// <summary>
    /// Free up resources.
    /// </summary>
    public void Dispose() {
        if (reader is AudioTrackReader track) {
            track.Source.Dispose();
        }
        reader?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Very short track information for the dropdown.
    /// </summary>
    public override string ToString() {
        TrackStrings.Active.CodecNames.TryGetValue(Codec, out string codecName);
        codecName ??= formatNames.TryGetValue(Codec, out string value) ? value : Codec.ToString();
        if (Renderer != null && Renderer.HasObjects) {
            codecName = $"{codecName} {TrackStrings.Active["WiObj"]}";
        }
        return string.IsNullOrEmpty(Language) ? codecName : $"{codecName} ({Language})";
    }

    /// <summary>
    /// Based on <paramref name="upmixingSettings"/>, return the <see cref="Source"/>s to be attached to the <paramref name="listener"/>.
    /// </summary>
    Source[] SetupUpmixer(Listener listener, UpmixingSettings upmixingSettings) {
        if (upmixingSettings.MatrixUpmixing && !Renderer.HasObjects) {
            ReferenceChannel[] channels = Renderer.GetChannels();
            SurroundUpmixer upmixer = null;
            try {
                upmixer = new SurroundUpmixer(channels, SampleRate, false, true);
            } catch (LegacyInputExpectedException) {
                listener.LFESeparation = true;
                return [.. Renderer.Objects];
            }

            RunningChannelSeparator separator = new RunningChannelSeparator(channels.Length) {
                GetSamples = input => reader.ReadBlock(input, 0, input.Length)
            };
            upmixer.OnSamplesNeeded = separator.Update;

            listener.LFESeparation = channels.Contains(ReferenceChannel.ScreenLFE); // Apply crossover if LFE is not present
            return upmixer.IntermediateSources;
        } else {
            listener.LFESeparation = true;
            return [.. Renderer.Objects];
        }
    }

    /// <summary>
    /// Expanded names of codecs for which the enum is a shorthand.
    /// </summary>
    static readonly Dictionary<Codec, string> formatNames = new() {
        [Codec.DAMF] = "Dolby Atmos Master Format",
        [Codec.DTS] = "DTS Coherent Acoustics",
        [Codec.DTS_HD] = "DTS-HD",
        [Codec.AC3] = "AC-3",
        [Codec.EnhancedAC3] = "Enhanced AC-3",
    };
}
