using System.Numerics;

using Cavern;
using Cavern.Channels;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;

namespace Cavernize.Logic.Models;

/// <summary>
/// Describes a single channel (bed) or dynamic 3D object inside a <see cref="CavernizeTrack"/>.
/// </summary>
public sealed class TrackSourceItem {
    /// <summary>
    /// Zero-based index inside <see cref="Renderer.Objects"/>.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Display name of the channel or 3D object.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// True when this element is a fixed bed channel rather than a dynamic object.
    /// </summary>
    public bool IsBed { get; }

    /// <summary>
    /// True when this element is a dynamic 3D object.
    /// </summary>
    public bool IsDynamicObject => !IsBed;

    /// <summary>
    /// True when this element is an LFE channel.
    /// </summary>
    public bool IsLFE { get; }

    /// <summary>
    /// Reference channel type if this is a bed channel.
    /// </summary>
    public ReferenceChannel? Channel { get; }

    /// <summary>
    /// True when muted by the user.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// True when soloed by the user.
    /// </summary>
    public bool IsSolo { get; set; }

    /// <summary>
    /// Current normalized position in room space [-1; 1] on each axis.
    /// </summary>
    public Vector3 NormalizedPosition { get; set; }

    /// <summary>
    /// Latest RMS amplitude [0; 1+] for live metering.
    /// </summary>
    public float RmsLevel { get; set; }

    /// <summary>
    /// Creates metadata describing a channel or object source.
    /// </summary>
    public TrackSourceItem(int index, string name, bool isBed, bool isLfe, ReferenceChannel? channel, Vector3 normalizedPosition) {
        Index = index;
        Name = name;
        IsBed = isBed;
        IsLFE = isLfe;
        Channel = channel;
        NormalizedPosition = normalizedPosition;
    }

    /// <summary>
    /// Generates a filesystem-safe stem file name without extension.
    /// </summary>
    public string GetSafeFileBaseName() {
        string kind = IsBed ? "bed" : "obj3d";
        string label = Channel.HasValue && Channel.Value != ReferenceChannel.Unknown
            ? Channel.Value.GetShortName()
            : (IsBed ? $"bed_{Index + 1}" : $"obj_{Index + 1}");
        string raw = $"{Index + 1:D2}_{label}_{kind}";
        foreach (char invalid in Path.GetInvalidFileNameChars()) {
            raw = raw.Replace(invalid, '_');
        }
        return raw.Replace(' ', '_');
    }

    /// <summary>
    /// Inspects a <see cref="CavernizeTrack"/> and lists all of its bed channels and dynamic 3D objects.
    /// </summary>
    public static List<TrackSourceItem> BuildFromTrack(CavernizeTrack track) {
        List<TrackSourceItem> items = [];
        Renderer renderer = track?.Renderer;
        if (renderer == null || renderer.Objects == null || renderer.Objects.Count == 0) {
            return items;
        }

        IReadOnlyList<Source> objects = renderer.Objects;
        ReferenceChannel[] beds = renderer.HasObjects && renderer is IMixedBedObjectRenderer mixed
            ? mixed.GetStaticChannels()
            : renderer.GetChannels();

        Vector3 envSize = Listener.EnvironmentSize;
        if (envSize.X == 0 || envSize.Y == 0 || envSize.Z == 0) {
            envSize = new Vector3(10, 7, 10);
        }

        int bedCount = beds?.Length ?? 0;
        if (!renderer.HasObjects && bedCount < objects.Count) {
            bedCount = objects.Count;
        }

        int dynamicOrdinal = 1;
        for (int i = 0; i < objects.Count; i++) {
            Source src = objects[i];
            Vector3 normPos = src.Position / envSize;
            bool isBed = i < bedCount;
            ReferenceChannel? channel = null;

            if (isBed && beds != null && i < beds.Length) {
                channel = beds[i];
            } else if (!renderer.HasObjects) {
                channel = Renderer.ChannelFromPosition(normPos);
            }

            bool isLfe = src.LFE || channel == ReferenceChannel.ScreenLFE;
            string name;
            if (isBed && channel.HasValue && channel.Value != ReferenceChannel.Unknown) {
                if (normPos == Vector3.Zero) {
                    normPos = ChannelPrototype.AlternativePositions[(int)channel.Value];
                }
                string fullName = ChannelPrototype.Mapping[(int)channel.Value].Name;
                string shortName = channel.Value.GetShortName();
                name = $"#{i + 1} {fullName} ({shortName})";
            } else if (isBed) {
                name = $"#{i + 1} Bed {i + 1}";
            } else {
                name = $"#{i + 1} Object {dynamicOrdinal++} (3D)";
            }

            items.Add(new TrackSourceItem(i, name, isBed, isLfe, channel, normPos));
        }
        return items;
    }
}
