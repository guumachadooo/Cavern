using System.Numerics;

using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;
using Cavern.Spatial;

using Cavernize.Logic.Models;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 1 pass-through adapter wrapping an existing <see cref="CavernizeTrack"/> (Atmos/TrueHD/E-AC-3/DAMF/FLAC/PCM)
/// into the unified <see cref="ISpatialDecoder"/> contract without altering Cavern's existing decoding behavior.
/// </summary>
public sealed class CavernTrackDecoderAdapter : ISpatialDecoder {
    readonly CavernizeTrack track;
    readonly List<SpatialSourceDescriptor> descriptors;
    readonly SpatialProgramMetadata metadata;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public SpatialContentKind ContentKind => metadata.ContentKind;

    /// <inheritdoc/>
    public int SampleRate => track.SampleRate;

    /// <inheritdoc/>
    public long LengthSamples => track.Length;

    /// <inheritdoc/>
    public long PositionSamples => track.Position >= 0 ? track.Position : 0;

    /// <inheritdoc/>
    public bool SupportsSeek => track.Supported;

    /// <inheritdoc/>
    public SpatialProgramMetadata Metadata => metadata;

    /// <summary>
    /// Underlying <see cref="CavernizeTrack"/> wrapped by this adapter.
    /// </summary>
    public CavernizeTrack Track => track;

    /// <summary>
    /// Creates a Slot 1 adapter over <paramref name="track"/> and optional pre-built <paramref name="sourceItems"/>.
    /// </summary>
    public CavernTrackDecoderAdapter(CavernizeTrack track, IReadOnlyList<TrackSourceItem> sourceItems = null) {
        ArgumentNullException.ThrowIfNull(track);
        this.track = track;
        Name = $"Cavern ({track.SourceCodec})";

        IReadOnlyList<TrackSourceItem> items = sourceItems ?? TrackSourceItem.BuildFromTrack(track);
        Vector3 envSize = Listener.EnvironmentSize;
        if (envSize.X <= 0f || envSize.Y <= 0f || envSize.Z <= 0f) {
            envSize = new Vector3(10f, 7f, 10f);
        }

        descriptors = new List<SpatialSourceDescriptor>(items.Count);
        int bedCount = 0;
        int dynamicCount = 0;
        bool hasHeights = false;
        bool hasLfe = false;

        for (int i = 0; i < items.Count; i++) {
            TrackSourceItem item = items[i];
            SourceLayer layer = item.IsBed ? SourceLayer.Bed : SourceLayer.NativeObject;
            if (item.IsBed) {
                bedCount++;
            } else {
                dynamicCount++;
            }
            if (item.IsLFE) {
                hasLfe = true;
            }
            if (!item.IsLFE && MathF.Abs(item.NormalizedPosition.Y) > 0.10f) {
                hasHeights = true;
            }

            bool isDialogue = SpatialDialogEnhancer.IsDialogueCandidate(
                item.Channel == ReferenceChannel.FrontCenter,
                item.IsLFE,
                item.NormalizedPosition);
            descriptors.Add(new SpatialSourceDescriptor {
                Index = item.Index,
                Name = item.Name,
                Layer = layer,
                BedChannel = item.Channel,
                IsLfe = item.IsLFE,
                IsDialogue = isDialogue,
                NormalizedPosition = item.NormalizedPosition,
                WorldPositionMeters = item.NormalizedPosition * envSize,
                Gain = 1f,
                Diffuse = 0f,
                Divergence = 0f,
                Importance = isDialogue ? 1f : 0.75f,
                IsMuted = item.IsMuted,
                IsSolo = item.IsSolo,
                IsDynamicallyFilteredOut = item.IsDynamicallyFilteredOut,
                RmsLevel = item.RmsLevel
            });
        }

        Renderer renderer = track.Renderer;
        bool hasObjects = renderer != null && renderer.HasObjects;
        SpatialContentKind contentKind;
        if (hasObjects && bedCount > 0) {
            contentKind = SpatialContentKind.HybridBedAndObjects;
        } else if (hasObjects) {
            contentKind = SpatialContentKind.Objects;
        } else {
            contentKind = SpatialContentKind.DirectSpeakers;
        }

        metadata = SpatialProgramMetadata.CreateDefault();
        metadata.ContentKind = contentKind;
        metadata.BedChannelCount = bedCount;
        metadata.DynamicObjectCount = dynamicCount;
        metadata.HasNativeObjects = hasObjects;
        metadata.HasNativeHeights = hasHeights;
        metadata.HasLfe = hasLfe;
        if (renderer is EnhancedAC3Renderer eac3Renderer) {
            metadata.DialnormLufs = eac3Renderer.DialnormLufs;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SpatialSourceDescriptor> DescribeSources() => descriptors;

    /// <inheritdoc/>
    public long Seek(long targetSample) => track.Seek(targetSample);

    /// <inheritdoc/>
    public bool FillFrame(SpatialSceneFrame frame, int maxFrames) {
        if (frame == null || maxFrames <= 0) {
            return false;
        }
        frame.Metadata = metadata;
        frame.Sources = descriptors;
        return track.Length <= 0 || track.Position < track.Length;
    }

    /// <inheritdoc/>
    public void Dispose() {
        // Non-owning adapter: CavernizeTrack lifecycle remains owned by the caller/player.
    }
}
