using System;

using Cavern.Channels;
using Cavern.Format.Decoders;

namespace Cavern.Format.Renderers {
    /// <summary>
    /// Renders an AOMedia IAMF (Eclipsa Audio) stream into Cavern 3D virtual sources.
    /// Supports both Channel-Based scalable speaker beds (Stereo..7.1.4) and Scene-Based HOA Ambisonics (orders 1..3 ACN/SN3D).
    /// </summary>
    public class IamfRenderer : PCMToObjectsRenderer {
        readonly IamfDecoder iamfDecoder;
        readonly AmbisonicsHoaSpatialDecoder hoaDecoder;
        float[] rawInterleaved = Array.Empty<float>();

        /// <summary>
        /// Renders a decoded AOMedia IAMF stream.
        /// </summary>
        public IamfRenderer(IamfDecoder stream) : base(stream) {
            iamfDecoder = stream ?? throw new ArgumentNullException(nameof(stream));
            if (iamfDecoder.Info.ElementType == IamfAudioElementType.SceneBased) {
                int order = Math.Max(1, Math.Min(3, iamfDecoder.Info.AmbisonicsOrder));
                hoaDecoder = new AmbisonicsHoaSpatialDecoder(order, useMaxReWeights: true);
                SetupObjects(hoaDecoder.VirtualSpeakerCount);
                for (int s = 0; s < hoaDecoder.VirtualSpeakerCount; s++) {
                    objects[s].Position = hoaDecoder.VirtualSpeakerPositions[s] * Listener.EnvironmentSize;
                }
            } else {
                SetupChannels();
            }
            if (objectSamples.Length > 0) {
                objectSamples[0] = Array.Empty<float>();
            }
        }

        /// <inheritdoc/>
        public override ReferenceChannel[] GetChannels() {
            if (hoaDecoder != null) {
                return hoaDecoder.VirtualChannels;
            }
            return IamfDecoder.GetChannelsForLayoutCode(iamfDecoder.Info.LoudspeakerLayoutCode, iamfDecoder.ChannelCount);
        }

        /// <inheritdoc/>
        public override void Update(int samples) {
            if (hoaDecoder == null) {
                base.Update(samples);
                return;
            }

            if (objectSamples[0].Length != samples) {
                for (int i = 0; i < objectSamples.Length; i++) {
                    objectSamples[i] = new float[samples];
                }
            }

            int interleavedLen = samples * stream.ChannelCount;
            if (rawInterleaved.Length < interleavedLen) {
                rawInterleaved = new float[interleavedLen];
            }
            stream.DecodeBlock(rawInterleaved, 0, interleavedLen);
            hoaDecoder.DecodeInterleaved(rawInterleaved, samples, stream.ChannelCount, objectSamples);
            for (int s = 0; s < hoaDecoder.VirtualSpeakerCount; s++) {
                objects[s].Position = hoaDecoder.VirtualSpeakerPositions[s] * Listener.EnvironmentSize;
            }
        }
    }
}
