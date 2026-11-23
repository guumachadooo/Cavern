using System.Numerics;

using Cavern;
using Cavern.Channels;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;
using Cavern.Spatial;

namespace Cavernize.Logic.Models;

/// <summary>
/// Describes a single channel (bed), dynamic 3D object, neural stem, or live microphone source inside a <see cref="CavernizeTrack"/>.
/// </summary>
public sealed class TrackSourceItem {
    /// <summary>
    /// Zero-based index inside <see cref="Renderer.Objects"/> (or virtual layer index for stems / live mic).
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Display name of the channel, 3D object, stem, or live microphone.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Architectural origin layer (Bed, NativeObject, VirtualMatrix, NeuralStem, LiveMicrophone).
    /// </summary>
    public SourceLayer Layer { get; set; }

    /// <summary>
    /// True when this element is a fixed bed channel rather than a dynamic object.
    /// </summary>
    public bool IsBed { get; }

    /// <summary>
    /// True when this element is a dynamic 3D object.
    /// </summary>
    public bool IsDynamicObject => !IsBed;

    /// <summary>
    /// True when the user can drag and pin this source in the interactive 6DoF 3D Room.
    /// </summary>
    public bool IsCustomDraggable => !IsBed || Layer == SourceLayer.NeuralStem || Layer == SourceLayer.RestoredResidual || Layer == SourceLayer.LiveMicrophone;

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
    /// Original default normalized position in room space [-1; 1], used when resetting custom dragged pins.
    /// </summary>
    public Vector3 DefaultNormalizedPosition { get; }

    /// <summary>
    /// True when the user manually dragged and pinned this source to <see cref="CustomNormalizedPosition"/>.
    /// </summary>
    public bool HasCustomPosition { get; set; }

    /// <summary>
    /// User-pinned normalized room position [-1; 1] overriding native object metadata while <see cref="HasCustomPosition"/> is true.
    /// </summary>
    public Vector3 CustomNormalizedPosition { get; set; }

    /// <summary>
    /// True when temporarily muted by a live dynamic filter (e.g. Dynamic Ceiling-Only mode).
    /// </summary>
    public bool IsDynamicallyFilteredOut { get; set; }

    /// <summary>
    /// Latest RMS amplitude [0; 1+] for live metering.
    /// </summary>
    public float RmsLevel { get; set; }

    /// <summary>
    /// Live low-frequency (&lt;250 Hz) RMS energy for the W13 3D Spatial X-Ray heatmap.
    /// </summary>
    public float LowBandEnergy { get; set; }

    /// <summary>
    /// Live mid-frequency (250–2500 Hz) RMS energy for the W13 3D Spatial X-Ray heatmap.
    /// </summary>
    public float MidBandEnergy { get; set; }

    /// <summary>
    /// Live high-frequency (&gt;2500 Hz) RMS energy for the W13 3D Spatial X-Ray heatmap.
    /// </summary>
    public float HighBandEnergy { get; set; }

    /// <summary>
    /// Live 6DoF acoustic time-of-flight delay (ms at 343 m/s) from this source to the listener.
    /// </summary>
    public float PropagationDelayMs { get; set; }

    /// <summary>
    /// Creates metadata describing a channel, object, neural stem, or live microphone source.
    /// </summary>
    public TrackSourceItem(
        int index,
        string name,
        bool isBed,
        bool isLfe,
        ReferenceChannel? channel,
        Vector3 normalizedPosition,
        SourceLayer? layer = null) {
        Index = index;
        Name = name;
        IsBed = isBed;
        IsLFE = isLfe;
        Channel = channel;
        NormalizedPosition = normalizedPosition;
        DefaultNormalizedPosition = normalizedPosition;
        Layer = layer ?? (isBed ? SourceLayer.Bed : SourceLayer.NativeObject);
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

    /// <summary>
    /// Builds <see cref="TrackSourceItem"/> entries matching the exact attached <see cref="Listener.ActiveSources"/>
    /// (including virtual Matrix / Cavernize 3D upmix channels) so the 3D Room graph displays every active upmixed
    /// speaker/height object and Solo/Mute/CustomPosition map 1:1 with the rendered sources.
    /// </summary>
    public static List<TrackSourceItem> BuildFromAttachedSources(CavernizeTrack track, IReadOnlyList<Source> attachedSources) {
        List<TrackSourceItem> baseItems = BuildFromTrack(track);
        if (attachedSources == null || attachedSources.Count <= baseItems.Count) {
            return baseItems;
        }

        Vector3 envSize = Listener.EnvironmentSize;
        if (envSize.X <= 0.1f || envSize.Y <= 0.1f || envSize.Z <= 0.1f) {
            envSize = new Vector3(10, 7, 10);
        }

        SourceLayer[] layers = track?.ActiveSourceLayers;
        bool hasDirectBaseMatch = track?.Renderer?.Objects != null &&
            attachedSources.Count >= track.Renderer.Objects.Count;
        // When CavernizeUpmixer or MatrixUpmixing replaces the source list completely (e.g. 2.0 -> 8/12 virtual sources),
        // enumerate every attached source directly so 3D Room graph and Solo/Mute stay 1:1 with Listener.ActiveSources.
        bool baseIndicesMatch = hasDirectBaseMatch && baseItems.Count > 0 &&
            ReferenceEquals(attachedSources[0], track.Renderer.Objects[0]);

        List<TrackSourceItem> items = new(attachedSources.Count);
        for (int i = 0; i < attachedSources.Count; i++) {
            if (baseIndicesMatch && i < baseItems.Count) {
                items.Add(baseItems[i]);
                continue;
            }
            Source src = attachedSources[i];
            Vector3 normPos = src.Position / envSize;
            ReferenceChannel ch = Renderer.ChannelFromPosition(normPos);
            bool isLfe = src.LFE || ch == ReferenceChannel.ScreenLFE;
            SourceLayer layer = layers != null && i < layers.Length
                ? layers[i]
                : (normPos.Y > 0.15f ? SourceLayer.VirtualHeight : SourceLayer.VirtualMatrix);
            bool isBed = layer == SourceLayer.Bed || layer == SourceLayer.VirtualMatrix;
            string label;
            if (ch != ReferenceChannel.Unknown) {
                string shortName = ch.GetShortName();
                string prefix = layer == SourceLayer.VirtualHeight ? "Upmix Height" : (layer == SourceLayer.VirtualMatrix ? "Upmix" : ChannelPrototype.Mapping[(int)ch].Name);
                label = $"#{i + 1} {prefix} ({shortName})";
            } else {
                label = layer == SourceLayer.VirtualHeight
                    ? $"#{i + 1} Virtual Height {i + 1}"
                    : $"#{i + 1} Virtual Upmix {i + 1}";
            }
            items.Add(new TrackSourceItem(i, label, isBed, isLfe, ch != ReferenceChannel.Unknown ? ch : null, normPos, layer));
        }
        return items;
    }
}
