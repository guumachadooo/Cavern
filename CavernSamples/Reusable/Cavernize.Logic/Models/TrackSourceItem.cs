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
    /// True when this element is driven by per-frame bitstream object trajectory metadata (e.g. Dolby Atmos / ADM objects)
    /// rather than a fixed bed, virtual upmix slot, neural stem, or live microphone position.
    /// </summary>
    public bool HasDynamicTrajectory { get; }

    /// <summary>
    /// Stable source identity key (<c>TrackId:SourceIndex:ChannelOrLabel</c>) preserved across listener rebuilds.
    /// </summary>
    public string StableKey { get; }

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
        SourceLayer? layer = null,
        bool? hasDynamicTrajectory = null,
        string stableKey = null) {
        Index = index;
        Name = name;
        IsBed = isBed;
        IsLFE = isLfe;
        Channel = channel;
        NormalizedPosition = normalizedPosition;
        DefaultNormalizedPosition = normalizedPosition;
        SourceLayer resolvedLayer = layer ?? (isBed ? SourceLayer.Bed : SourceLayer.NativeObject);
        Layer = resolvedLayer;
        HasDynamicTrajectory = hasDynamicTrajectory ?? (!isBed && resolvedLayer == SourceLayer.NativeObject);
        StableKey = string.IsNullOrEmpty(stableKey)
            ? CreateStableKey(0, index, resolvedLayer, channel, name)
            : stableKey;
    }

    /// <summary>
    /// Clears any custom position pin: static beds, virtual upmixers, neural stems, and microphones restore to
    /// <see cref="DefaultNormalizedPosition"/>, while dynamic objects (<see cref="HasDynamicTrajectory"/>) unlock
    /// without overwriting their live trajectory coordinate.
    /// </summary>
    public void ResetPosition() {
        HasCustomPosition = false;
        CustomNormalizedPosition = Vector3.Zero;
        if (!HasDynamicTrajectory) {
            NormalizedPosition = DefaultNormalizedPosition;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="channel"/> represents an overhead/ceiling height speaker position.
    /// </summary>
    public static bool IsHeightChannel(ReferenceChannel? channel) {
        if (!channel.HasValue) {
            return false;
        }
        return channel.Value switch {
            ReferenceChannel.TopFrontLeft or
            ReferenceChannel.TopFrontRight or
            ReferenceChannel.TopSideLeft or
            ReferenceChannel.TopSideRight or
            ReferenceChannel.TopFrontCenter or
            ReferenceChannel.GodsVoice or
            ReferenceChannel.TopRearLeft or
            ReferenceChannel.TopRearRight or
            ReferenceChannel.TopRearCenter => true,
            _ => false
        };
    }

    /// <summary>
    /// Builds a deterministic <c>TrackId:SourceIndex:ChannelName</c> key for preserving user overrides across rebuilds.
    /// </summary>
    public static string CreateStableKey(int trackId, int index, SourceLayer layer, ReferenceChannel? channel, string fallbackName = null) {
        string channelToken = channel.HasValue && channel.Value != ReferenceChannel.Unknown
            ? channel.Value.ToString()
            : (!string.IsNullOrWhiteSpace(fallbackName) ? fallbackName.Trim() : layer.ToString());
        return $"{trackId}:{index}:{channelToken}";
    }

    /// <summary>
    /// Copies Solo, Mute, and custom 3D pin overrides from <paramref name="previousItems"/> onto matching items
    /// in <paramref name="nextItems"/> (matched by <see cref="StableKey"/>, then fallback <see cref="Name"/>).
    /// </summary>
    public static void PreserveOverrides(IReadOnlyList<TrackSourceItem> nextItems, IReadOnlyList<TrackSourceItem> previousItems) {
        if (nextItems == null || nextItems.Count == 0 || previousItems == null || previousItems.Count == 0) {
            return;
        }
        Dictionary<string, TrackSourceItem> byKey = new(previousItems.Count, StringComparer.Ordinal);
        Dictionary<string, TrackSourceItem> byName = new(previousItems.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < previousItems.Count; i++) {
            TrackSourceItem prev = previousItems[i];
            if (!string.IsNullOrEmpty(prev.StableKey) && !byKey.ContainsKey(prev.StableKey)) {
                byKey[prev.StableKey] = prev;
            }
            if (!string.IsNullOrEmpty(prev.Name) && !byName.ContainsKey(prev.Name)) {
                byName[prev.Name] = prev;
            }
        }
        for (int i = 0; i < nextItems.Count; i++) {
            TrackSourceItem next = nextItems[i];
            if (!byKey.TryGetValue(next.StableKey ?? string.Empty, out TrackSourceItem matched) &&
                (string.IsNullOrEmpty(next.Name) || !byName.TryGetValue(next.Name, out matched))) {
                continue;
            }
            next.IsMuted = matched.IsMuted;
            next.IsSolo = matched.IsSolo;
            next.HasCustomPosition = matched.HasCustomPosition;
            next.CustomNormalizedPosition = matched.CustomNormalizedPosition;
            if (next.HasCustomPosition) {
                next.NormalizedPosition = next.CustomNormalizedPosition;
            }
        }
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

        int trackId = track?.Index ?? 0;
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
            string stableToken;
            if (isBed && channel.HasValue && channel.Value != ReferenceChannel.Unknown) {
                if (normPos == Vector3.Zero) {
                    normPos = ChannelPrototype.AlternativePositions[(int)channel.Value];
                }
                string fullName = ChannelPrototype.Mapping[(int)channel.Value].Name;
                string shortName = channel.Value.GetShortName();
                name = $"#{i + 1} {fullName} ({shortName})";
                stableToken = channel.Value.ToString();
            } else if (isBed) {
                name = $"#{i + 1} Bed {i + 1}";
                stableToken = $"Bed{i + 1}";
            } else {
                int ord = dynamicOrdinal++;
                name = $"#{i + 1} Object {ord} (3D)";
                stableToken = $"Object{ord}";
            }

            SourceLayer layer = isBed ? SourceLayer.Bed : SourceLayer.NativeObject;
            items.Add(new TrackSourceItem(
                i,
                name,
                isBed,
                isLfe,
                channel,
                normPos,
                layer,
                hasDynamicTrajectory: !isBed,
                stableKey: CreateStableKey(trackId, i, layer, channel, stableToken)));
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

        int trackId = track?.Index ?? 0;
        SourceLayer[] layers = track?.ActiveSourceLayers;
        bool hasDirectBaseMatch = track?.Renderer?.Objects != null &&
            attachedSources.Count >= track.Renderer.Objects.Count;
        // AtmosAwareUpmixer wraps native beds + dynamic Atmos 3D objects in IntermediateSources at indices [0..baseCount-1]
        // with ActiveSourceLayers marked Bed/NativeObject, then appends virtual height/surround channels after them.
        // Preserve 100% of native dynamic Atmos object identities instead of mislabeling them as Virtual Upmix!
        bool atmosAwarePrefixMatch = hasDirectBaseMatch && baseItems.Count > 0 &&
            layers != null && layers.Length >= baseItems.Count &&
            (layers[0] == SourceLayer.Bed || layers[0] == SourceLayer.NativeObject);
        // When CavernizeUpmixer or MatrixUpmixing replaces the source list completely (e.g. 2.0 -> 8/12 virtual sources),
        // enumerate every attached source directly so 3D Room graph and Solo/Mute stay 1:1 with Listener.ActiveSources.
        bool baseIndicesMatch = hasDirectBaseMatch && baseItems.Count > 0 &&
            (ReferenceEquals(attachedSources[0], track.Renderer.Objects[0]) || atmosAwarePrefixMatch);

        List<TrackSourceItem> items = new(attachedSources.Count);
        for (int i = 0; i < attachedSources.Count; i++) {
            if (baseIndicesMatch && i < baseItems.Count) {
                items.Add(baseItems[i]);
                continue;
            }
            Source src = attachedSources[i];
            Vector3 normPos = src.Position / envSize;
            ReferenceChannel ch = Renderer.ChannelFromPosition(normPos);
            if (ch == ReferenceChannel.Unknown && normPos.Y > 0.10f) {
                ch = Renderer.ChannelFromPosition(new Vector3(normPos.X, 1f, normPos.Z));
            }
            bool isLfe = src.LFE || ch == ReferenceChannel.ScreenLFE;
            SourceLayer layer = layers != null && i < layers.Length
                ? layers[i]
                : (normPos.Y > 0.10f ? SourceLayer.VirtualHeight : SourceLayer.VirtualMatrix);
            bool isHeightChannel = IsHeightChannel(ch);
            if (layer == SourceLayer.VirtualMatrix && (normPos.Y > 0.10f || isHeightChannel)) {
                layer = SourceLayer.VirtualHeight;
            }
            bool isBed = layer == SourceLayer.Bed || (layer == SourceLayer.VirtualMatrix && normPos.Y <= 0.10f && !isHeightChannel);
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
            ReferenceChannel? resolvedCh = ch != ReferenceChannel.Unknown ? ch : null;
            items.Add(new TrackSourceItem(
                i,
                label,
                isBed,
                isLfe,
                resolvedCh,
                normPos,
                layer,
                hasDynamicTrajectory: !isBed && layer == SourceLayer.NativeObject,
                stableKey: CreateStableKey(trackId, i, layer, resolvedCh, label)));
        }
        return items;
    }
}
