using System.Numerics;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Common.Metadata;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;
using Cavern.Remapping;
using Cavern.Remapping.Exceptions;
using Cavern.Spatial;
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
    public bool Supported { get; protected set; }

    /// <summary>
    /// Track audio coding type.
    /// </summary>
    public Codec Codec { get; protected set; }

    /// <summary>
    /// The track's container's file format.
    /// </summary>
    public virtual Container Container => reader is AudioTrackReader trackReader ? trackReader.Source.Type : Container.NotContainer;

    /// <summary>
    /// Original codec when this track was pre-decoded externally (for example <see cref="Codec.TrueHD"/> -> <see cref="Codec.DAMF"/>).
    /// </summary>
    public virtual Codec SourceCodec => Codec;

    /// <summary>
    /// True when the original source stream is lossy-compressed (including fallback-decoded MP3/AAC/M4A/OGG/Opus).
    /// </summary>
    public virtual bool IsLossySource =>
        SourceCodec == Codec.EnhancedAC3 ||
        SourceCodec == Codec.AC3 ||
        SourceCodec == Codec.DTS ||
        SourceCodec == Codec.Opus;

    /// <summary>
    /// Original media file path prior to external fallback PCM decoding (defaults to <see cref="Path"/>).
    /// </summary>
    public virtual string OriginalSourcePath => Path;

    /// <summary>
    /// Optional raw bitstream path on disk when already extracted from a container.
    /// </summary>
    public virtual string RawBitstreamPath => null;

    /// <summary>
    /// The renderer starting at the first sample of the track after construction.
    /// </summary>
    public Renderer Renderer { get; private set; }

    /// <summary>
    /// Number of samples to render by the <see cref="Listener"/> to reach the end of the stream.
    /// </summary>
    public virtual long Length => reader.Length;

    /// <summary>
    /// Sampling rate of the track.
    /// </summary>
    public virtual int SampleRate => reader.SampleRate;

    /// <summary>
    /// Order in the tracks of the input container.
    /// </summary>
    public int Index { get; protected set; }

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
    public virtual string Path => reader?.Path;

    /// <summary>
    /// Raw access to the track's stream.
    /// </summary>
    public virtual Track Track => reader is AudioTrackReader trackReader ? trackReader.track : null;

    /// <summary>
    /// Raw access to the underlying <see cref="AudioReader"/>.
    /// </summary>
    public AudioReader Reader => reader;

    /// <summary>
    /// Current sample position in the stream per channel, or -1 if unsupported.
    /// </summary>
    public long Position {
        get => reader?.Position ?? -1;
        set {
            if (reader != null) {
                reader.Position = value;
            }
        }
    }

    /// <summary>
    /// Optional per-source mute mask indexed by <see cref="Renderer.Objects"/> order.
    /// </summary>
    public bool[] CustomMuteMask { get; set; }

    /// <summary>
    /// Live DirAC/COMPASS soundfield telemetry provider exposed by the active upmixer (null when bypassed).
    /// </summary>
    public ISoundfieldTelemetryProvider ActiveTelemetryProvider { get; private set; }

    /// <summary>
    /// Layer classification for attached sources when <see cref="AtmosAwareUpmixer"/> is active (null otherwise).
    /// </summary>
    public Cavern.Spatial.SourceLayer[] ActiveSourceLayers { get; private set; }

    /// <summary>
    /// Source of audio data.
    /// </summary>
    AudioReader reader;

    /// <summary>
    /// Upmixer instances owned by the last <see cref="AttachFromCurrentPosition"/> call for deterministic FFT cache cleanup.
    /// </summary>
    readonly List<IDisposable> ownedUpmixers = [];

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
        DisposeOwnedUpmixers();
        ActiveTelemetryProvider = null;
        ActiveSourceLayers = null;

        Renderer = reader.GetRenderer();
        listener.SampleRate = SampleRate;
        upmixingSettings ??= new UpmixingSettings(true);

        Source[] attachables;
        if (Renderer.HasObjects && upmixingSettings.AtmosAware && (upmixingSettings.MatrixUpmixing || upmixingSettings.Cavernize)) {
            ReferenceChannel[] staticBeds = Renderer is IMixedBedObjectRenderer mixed
                ? mixed.GetStaticChannels()
                : Renderer.GetChannels();
            AtmosAwareUpmixer atmosUpmixer = new AtmosAwareUpmixer(
                Renderer.Objects,
                staticBeds,
                SampleRate,
                upmixingSettings.MatrixUpmixing,
                upmixingSettings.Cavernize) {
                Effect = upmixingSettings.Effect,
                Smoothness = upmixingSettings.Smoothness
            };
            ownedUpmixers.Add(atmosUpmixer);
            ActiveTelemetryProvider = atmosUpmixer;
            ActiveSourceLayers = atmosUpmixer.OutputLayers;
            listener.LFESeparation = true;
            attachables = atmosUpmixer.IntermediateSources;
        } else {
            attachables = SetupUpmixer(listener, upmixingSettings);

            if (upmixingSettings.Cavernize && !Renderer.HasObjects && Renderer.Objects.All(x => x.LFE || x.Position.Y == 0)) {
                CavernizeUpmixer cavernizer = new CavernizeUpmixer(attachables, SampleRate) {
                    Effect = upmixingSettings.Effect,
                    Smoothness = upmixingSettings.Smoothness,
                    UseParametricStft = upmixingSettings.ParametricStft
                };
                ownedUpmixers.Add(cavernizer);
                ActiveTelemetryProvider ??= cavernizer;
                attachables = cavernizer.IntermediateSources;
            }

            if (attachables.Length != Renderer.Objects.Count ||
                (attachables.Length > 0 && Renderer.Objects.Count > 0 && !ReferenceEquals(attachables[0], Renderer.Objects[0]))) {
                Vector3 envSize = Listener.EnvironmentSize;
                float invY = envSize.Y > 0.1f ? 1f / envSize.Y : 1f / 7f;
                SourceLayer[] layers = new SourceLayer[attachables.Length];
                for (int i = 0; i < attachables.Length; i++) {
                    float ny = attachables[i].Position.Y * invY;
                    layers[i] = ny > 0.15f ? SourceLayer.VirtualHeight : SourceLayer.VirtualMatrix;
                }
                ActiveSourceLayers = layers;
            }
        }

        if (CustomMuteMask != null && attachables.Length >= Renderer.Objects.Count) {
            for (int i = 0; i < CustomMuteMask.Length && i < Renderer.Objects.Count; i++) {
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
    public virtual Track[] GetVideoTracks() => reader is AudioTrackReader trackReader ? [.. trackReader.Source.Tracks.Where(x => x.Format.IsVideo())] : null;

    /// <summary>
    /// Gets the metadata for this codec in a human-readable format.
    /// </summary>
    public virtual ReadableMetadata GetMetadata() => reader is IMetadataSupplier meta ? meta.GetMetadata() : null;

    /// <summary>
    /// Free up resources.
    /// </summary>
    public virtual void Dispose() {
        DisposeOwnedUpmixers();
        ActiveTelemetryProvider = null;
        ActiveSourceLayers = null;
        if (reader is AudioTrackReader track) {
            track.Source.Dispose();
        }
        reader?.Dispose();
        GC.SuppressFinalize(this);
    }

    void DisposeOwnedUpmixers() {
        for (int i = 0; i < ownedUpmixers.Count; i++) {
            ownedUpmixers[i].Dispose();
        }
        ownedUpmixers.Clear();
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
                upmixer = new SurroundUpmixer(channels, SampleRate, false, true) {
                    UseParametricStft = upmixingSettings.ParametricStft
                };
            } catch (LegacyInputExpectedException) {
                listener.LFESeparation = true;
                return [.. Renderer.Objects];
            }

            RunningChannelSeparator separator = new RunningChannelSeparator(channels.Length) {
                GetSamples = input => reader.ReadBlock(input, 0, input.Length)
            };
            upmixer.OnSamplesNeeded = separator.Update;
            ownedUpmixers.Add(upmixer);
            ActiveTelemetryProvider = upmixer;

            // Headphones use full-band HRIRs without a separate physical subwoofer; only engage LFE bass-management
            // crossover on physical multi-speaker layouts lacking a native .1 LFE track.
            listener.LFESeparation = Listener.HeadphoneVirtualizer || channels.Contains(ReferenceChannel.ScreenLFE);
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
        [Codec.TrueHD] = "Dolby TrueHD",
        [Codec.DTS] = "DTS Coherent Acoustics",
        [Codec.DTS_HD] = "DTS-HD",
        [Codec.AC3] = "AC-3",
        [Codec.EnhancedAC3] = "Enhanced AC-3",
    };
}

/// <summary>
/// Wraps a decoded <see cref="AudioReader"/> (such as DAMF or CAF produced by truehdd) while preserving
/// original container metadata, video tracks, and raw bitstream paths for remuxing and --extract.
/// </summary>
public sealed class ExternalDecodedTrack : CavernizeTrack {
    readonly CavernizeTrack originalTrack;
    readonly Container originalContainer;
    readonly Track[] originalVideoTracks;
    readonly string rawBitstreamPath;
    readonly Codec sourceCodec;
    readonly ReadableMetadata extraMetadata;
    readonly bool? explicitLossySource;
    readonly string originalSourcePath;

    /// <inheritdoc/>
    public override Container Container => originalTrack?.Container ?? originalContainer;

    /// <inheritdoc/>
    public override Codec SourceCodec => sourceCodec;

    /// <inheritdoc/>
    public override bool IsLossySource => explicitLossySource ?? base.IsLossySource;

    /// <inheritdoc/>
    public override string OriginalSourcePath => string.IsNullOrEmpty(originalSourcePath)
        ? (originalTrack?.OriginalSourcePath ?? base.OriginalSourcePath)
        : originalSourcePath;

    /// <inheritdoc/>
    public override string RawBitstreamPath => rawBitstreamPath;

    /// <inheritdoc/>
    public override Track Track => originalTrack?.Track;

    /// <summary>
    /// Creates an externally decoded track wrapping <paramref name="decodedReader"/>.
    /// </summary>
    public ExternalDecodedTrack(
        AudioReader decodedReader,
        Codec decodedCodec,
        Codec sourceCodec,
        CavernizeTrack originalTrack,
        Container originalContainer,
        Track[] originalVideoTracks,
        string rawBitstreamPath,
        string language = null,
        ReadableMetadata extraMetadata = null,
        bool? isLossySource = null,
        string originalSourcePath = null,
        string customFormatHeader = null)
        : base(decodedReader, decodedCodec, originalTrack?.Index ?? 0, language ?? originalTrack?.Language) {
        this.sourceCodec = sourceCodec;
        this.originalTrack = originalTrack;
        this.originalContainer = originalContainer;
        this.originalVideoTracks = originalVideoTracks;
        this.rawBitstreamPath = rawBitstreamPath;
        this.extraMetadata = extraMetadata;
        explicitLossySource = isLossySource;
        this.originalSourcePath = originalSourcePath;

        if (!string.IsNullOrWhiteSpace(customFormatHeader)) {
            FormatHeader = customFormatHeader;
        } else if (sourceCodec == Codec.TrueHD) {
            string durText = $"{TimeSpan.FromSeconds(Length / (double)Math.Max(1, SampleRate)):h\\:mm\\:ss}";
            FormatHeader = Renderer != null && Renderer.HasObjects
                ? $"Dolby TrueHD Atmos ({durText})"
                : $"Dolby TrueHD ({durText})";
        }
    }

    /// <inheritdoc/>
    public override Track[] GetVideoTracks() => originalTrack?.GetVideoTracks() ?? originalVideoTracks;

    /// <inheritdoc/>
    public override ReadableMetadata GetMetadata() => extraMetadata ?? originalTrack?.GetMetadata() ?? base.GetMetadata();

    /// <inheritdoc/>
    public override void Dispose() {
        base.Dispose();
    }
}

/// <summary>
/// Represents a TrueHD (or externally decodable) track inside a container or pre-extracted raw bitstream
/// when native header inspection threw or the container used FFmpeg fallback extraction.
/// </summary>
public sealed class ExternalPendingTrack : CavernizeTrack {
    readonly Track containerTrack;
    readonly Container container;
    readonly string filePath;
    readonly string rawBitstreamPath;
    readonly int sampleRate;
    readonly long length;

    /// <inheritdoc/>
    public override Container Container => containerTrack?.Source?.Type ?? container;

    /// <inheritdoc/>
    public override long Length => length;

    /// <inheritdoc/>
    public override int SampleRate => sampleRate;

    /// <inheritdoc/>
    public override string Path => filePath;

    /// <inheritdoc/>
    public override string RawBitstreamPath => rawBitstreamPath;

    /// <inheritdoc/>
    public override Track Track => containerTrack;

    /// <summary>
    /// Creates a pending external track directly from a parsed container <see cref="Track"/>.
    /// </summary>
    public ExternalPendingTrack(Track containerTrack, TrackExtraAudio audioExtra, int index, string filePath) {
        this.containerTrack = containerTrack;
        container = containerTrack?.Source?.Type ?? Container.NotContainer;
        this.filePath = filePath;
        Index = index;
        Codec = containerTrack?.Format ?? Codec.TrueHD;
        Language = containerTrack?.Language;
        Supported = true;
        sampleRate = audioExtra != null && audioExtra.SampleRate > 0 ? (int)audioExtra.SampleRate : 48000;
        double dur = containerTrack?.Source?.Duration ?? 0;
        length = dur > 0 ? (long)(dur * sampleRate) : -1;
        string durText = dur > 0 ? $"{TimeSpan.FromSeconds(dur):h\\:mm\\:ss}" : "--:--";
        FormatHeader = $"Dolby TrueHD ({durText})";
        int ch = audioExtra?.ChannelCount ?? 0;
        Details = ch > 0
            ? [(TrackStrings.Active["Chans"], $"{ch} (truehdd)")]
            : [(TrackStrings.Active["Chans"], "truehdd")];
    }

    /// <summary>
    /// Creates a pending external track from a pre-extracted raw bitstream (e.g. FFmpeg fallback from MKV/MP4/M2TS).
    /// </summary>
    public ExternalPendingTrack(string containerPath, string rawBitstreamPath, Codec codec, Container container,
        int index, string language, int sampleRate, double durationSeconds, string channelSummary) {
        filePath = containerPath;
        this.rawBitstreamPath = rawBitstreamPath;
        this.container = container;
        Index = index;
        Codec = codec;
        Language = language;
        Supported = true;
        this.sampleRate = sampleRate > 0 ? sampleRate : 48000;
        length = durationSeconds > 0 ? (long)(durationSeconds * this.sampleRate) : -1;
        string durText = durationSeconds > 0 ? $"{TimeSpan.FromSeconds(durationSeconds):h\\:mm\\:ss}" : "--:--";
        FormatHeader = $"Dolby TrueHD ({durText})";
        Details = !string.IsNullOrWhiteSpace(channelSummary)
            ? [(TrackStrings.Active["Chans"], channelSummary)]
            : [(TrackStrings.Active["Chans"], "truehdd")];
    }

    /// <inheritdoc/>
    public override Track[] GetVideoTracks() => containerTrack != null
        ? [.. containerTrack.Source.Tracks.Where(x => x.Format.IsVideo())]
        : null;

    /// <inheritdoc/>
    public override void Dispose() {
        containerTrack?.Source?.Dispose();
        GC.SuppressFinalize(this);
    }
}
