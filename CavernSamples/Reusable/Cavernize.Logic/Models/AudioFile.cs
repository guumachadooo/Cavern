using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Common.Metadata;
using Cavern.Format.Container;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;

using Cavernize.Logic.Exceptions;

namespace Cavernize.Logic.Models;

/// <summary>
/// Audio file parser and tracklist provider.
/// </summary>
public class AudioFile : IDisposable {
    /// <summary>
    /// Path to the file.
    /// </summary>
    public string Path { get; private set; }

    /// <summary>
    /// Track handlers and info providers for all audio tracks, including unsupported.
    /// </summary>
    public IReadOnlyList<CavernizeTrack> Tracks => tracks;

    /// <summary>
    /// All native tracks of the file, including video and subtitle tracks.
    /// </summary>
    public IReadOnlyList<Track> AllTracks => tracks.Select(x => x.Track?.Source?.Tracks).FirstOrDefault(x => x != null) ?? throw new NoSupportedTracksException();

    /// <summary>
    /// List of track handlers.
    /// </summary>
    protected readonly List<CavernizeTrack> tracks = [];

    readonly bool customTracksMode;

    /// <summary>
    /// Loads an audio file.
    /// </summary>
    public AudioFile(string path) {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Reset();
    }

    /// <summary>
    /// Wraps pre-resolved tracks (for example from FFmpeg container fallback) under an original container <paramref name="path"/>.
    /// </summary>
    AudioFile(string path, IEnumerable<CavernizeTrack> customTracks) {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        customTracksMode = true;
        tracks.AddRange(customTracks);
    }

    /// <summary>
    /// Creates an <see cref="AudioFile"/> backed by externally probed/extracted tracks while keeping <paramref name="path"/> as container source.
    /// </summary>
    public static AudioFile FromCustomTracks(string path, IEnumerable<CavernizeTrack> customTracks) => new(path, customTracks);

    /// <summary>
    /// Reloads the tracklist to be able to start reading from the beginning.
    /// </summary>
    public virtual void Reset() {
        if (customTracksMode) {
            return;
        }
        Dispose();
        tracks.Clear();
        int index = Path.LastIndexOf('.') + 1;
        if (index == 0 || index == Path.Length) {
            throw new NotSupportedException();
        }
        switch (Path[index..].ToLowerInvariant()) {
            case "ac3":
            case "eac3":
            case "ec3":
                tracks.Add(new CavernizeTrack(new EnhancedAC3Reader(Path), Codec.EnhancedAC3, 0));
                break;
            case "mkv":
            case "mka":
            case "webm":
            case "weba":
                AddTracksFromContainer(new MatroskaReader(Path));
                break;
            case "m4a":
            case "m4v":
            case "mov":
            case "mp4":
            case "qt":
                AddTracksFromContainer(new MP4Reader(Path));
                break;
            case "mxf":
                AddTracksFromContainer(new MXFReader(Path));
                break;
            case "atmos":
                AddStandaloneTrack(new DolbyAtmosMasterFormatReader(Path), Codec.DAMF);
                break;
            case "caf":
                AddStandaloneTrack(new CoreAudioFormatReader(Path));
                break;
            case "mlp":
            case "thd":
            case "truehd":
                AddStandaloneTrack(new MeridianLosslessPackingReader(Path), Codec.TrueHD);
                break;
            case "wav":
                AddStandaloneTrack(new RIFFWaveReader(Path));
                break;
            case "amb":
                OpenAmbisonicsTrack();
                break;
            case "iamf":
                AddStandaloneTrack(new IamfReader(Path), Codec.IAMF);
                break;
            case "laf":
                AddStandaloneTrack(new LimitlessAudioFormatReader(Path));
                break;
            default:
                throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Sniffs a <c>.amb</c> file header (RIFF/RF64 WAVE-EX vs IAMF) and routes B-Format streams cleanly
    /// without throwing obscure sync exceptions into fallback probes.
    /// </summary>
    void OpenAmbisonicsTrack() {
        byte[] header = new byte[16];
        int read;
        using (FileStream fs = File.OpenRead(Path)) {
            read = fs.Read(header, 0, header.Length);
        }
        if (read >= 4) {
            bool isRiff = header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F';
            bool isRf64 = header[0] == 'R' && header[1] == 'F' && header[2] == '6' && header[3] == '4';
            if (isRiff || isRf64) {
                RIFFWaveReader ambReader = new RIFFWaveReader(Path);
                try {
                    ambReader.ReadHeader();
                    Codec codec = ambReader.GetRenderer() is RIFFWaveRenderer wavRenderer && wavRenderer.IsAmbisonicsHoa
                        ? Codec.AmbisonicsHOA
                        : (ambReader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE);
                    AddStandaloneTrack(ambReader, codec);
                    return;
                } catch {
                    ambReader.Dispose();
                    throw;
                }
            }
        }
        if (IamfDecoder.IsIamfHeader(header, read)) {
            AddStandaloneTrack(new IamfReader(Path), Codec.IAMF);
            return;
        }
        throw new NotSupportedException();
    }

    /// <summary>
    /// Free up resources.
    /// </summary>
    public void Dispose() {
        for (int i = 0; i < tracks.Count; i++) {
            tracks[i].Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Show the file's name.
    /// </summary>
    public override string ToString() => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Add a track from a file that contains the raw bitstream of a single audio track.
    /// </summary>
    void AddStandaloneTrack(AudioReader reader) => AddStandaloneTrack(reader, reader.Bits == BitDepth.Float32 ? Codec.PCM_Float : Codec.PCM_LE);

    /// <summary>
    /// Add a track from a file that contains the raw bitstream of a single audio track.
    /// </summary>
    void AddStandaloneTrack(AudioReader reader, Codec codec) => tracks.Add(new CavernizeTrack(reader, codec, 0));

    /// <summary>
    /// Add the tracks of a container to the track list.
    /// </summary>
    void AddTracksFromContainer(ContainerReader reader) {
        int trackId = 0;
        for (int i = 0; i < reader.Tracks.Length; i++) {
            if (reader.Tracks[i].Extra is TrackExtraAudio audioExtra) {
                try {
                    tracks.Add(new CavernizeTrack(new AudioTrackReader(reader.Tracks[i]), reader.Tracks[i].Format,
                        trackId, reader.Tracks[i].Language));
                } catch (Exception e) {
                    if (reader.Tracks[i].Format == Codec.TrueHD) {
                        tracks.Add(new ExternalPendingTrack(reader.Tracks[i], audioExtra, trackId, Path));
                    } else {
                        tracks.Add(new InvalidTrack(e.Message, reader.Tracks[i].Format, reader.Tracks[i].Language));
                    }
                }
                trackId++;
            }
        }
    }
}
