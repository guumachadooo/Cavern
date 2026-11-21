using System;
using System.Collections.Generic;
using System.Numerics;

using Cavern.Channels;
using Cavern.Format.Decoders;
using Cavern.Format.Transcoders;
using Cavern.Format.Transcoders.AudioDefinitionModelElements;
using Cavern.Utilities;

namespace Cavern.Format.Renderers {
    /// <summary>
    /// Renders a decoded RIFF WAVE stream.
    /// </summary>
    public class RIFFWaveRenderer : PCMToObjectsRenderer {
        /// <summary>
        /// Index of the last passed ADM block.
        /// </summary>
        readonly int[] admBlocks;

        /// <summary>
        /// Optional Ambisonics HOA decoder when the WAV carries an Ambisonics B-Format SubFormat GUID.
        /// </summary>
        readonly AmbisonicsHoaSpatialDecoder hoaDecoder;

        /// <summary>
        /// Reusable interleaved HOA decode scratch buffer (zero-GC).
        /// </summary>
        float[] hoaScratch = Array.Empty<float>();

        /// <summary>
        /// True when the WAV stream is rendered through the Ambisonics HOA B-Format decoder (ACN/SN3D).
        /// </summary>
        public bool IsAmbisonicsHoa => hoaDecoder != null;

        /// <summary>
        /// Active Ambisonics spherical harmonic order when <see cref="IsAmbisonicsHoa"/> is true, otherwise 0.
        /// </summary>
        public int AmbisonicsOrder => hoaDecoder != null ? ((RIFFWaveDecoder)stream).AmbisonicsOrder : 0;

        /// <summary>
        /// Renders a decoded RIFF WAVE stream.
        /// </summary>
        public RIFFWaveRenderer(RIFFWaveDecoder stream) : base(stream) {
            if (stream.IsAmbisonicsHoa && stream.ADM == null) {
                hoaDecoder = new AmbisonicsHoaSpatialDecoder(stream.AmbisonicsOrder, useMaxReWeights: true);
                SetupObjects(hoaDecoder.VirtualSpeakerCount);
                for (int s = 0; s < hoaDecoder.VirtualSpeakerCount; s++) {
                    objects[s].Position = hoaDecoder.VirtualSpeakerPositions[s] * Listener.EnvironmentSize;
                }
            } else if (stream.ADM == null) {
                SetupChannels();
            } else {
                SetupObjects(stream.ChannelCount);
                admBlocks = new int[stream.ChannelCount];
                IReadOnlyList<ADMChannelFormat> movements = stream.ADM.Movements;
                for (int i = 0, c = movements.Count; i < c; i++) {
                    if (movements[i].Blocks.Count == 1 &&
                        ChannelFromPosition(movements[i].Blocks[0].Position) == ReferenceChannel.ScreenLFE) {
                        objects[i].LFE = true;
                    }
                }
            }
            objectSamples[0] = Array.Empty<float>();
        }

        /// <summary>
        /// Get the bed channels.
        /// </summary>
        public override ReferenceChannel[] GetChannels() {
            RIFFWaveDecoder decoder = (RIFFWaveDecoder)stream;
            if (hoaDecoder != null) {
                return hoaDecoder.VirtualChannels;
            }
            AudioDefinitionModel adm = decoder.ADM;
            if (adm == null) {
                return decoder.GetChannels();
            } else {
                List<ReferenceChannel> channels = new List<ReferenceChannel>();
                for (int i = 0, c = adm.Movements.Count; i < c; i++) {
                    if (adm.Movements[i].Blocks.Count == 1 && adm.Movements[i].Blocks[0].Duration.IsZero()) {
                        channels.Add(ChannelFromPosition(adm.Movements[i].Blocks[0].Position));
                    }
                }
                return channels.ToArray();
            }
        }

        /// <summary>
        /// Read the next <paramref name="samples"/> and update the objects.
        /// </summary>
        /// <param name="samples">Samples per channel</param>
        public override void Update(int samples) {
            if (hoaDecoder != null) {
                if (objectSamples[0].Length != samples) {
                    for (int i = 0; i < objectSamples.Length; i++) {
                        objectSamples[i] = new float[samples];
                    }
                }
                int interleavedLen = samples * stream.ChannelCount;
                if (hoaScratch.Length < interleavedLen) {
                    hoaScratch = new float[interleavedLen];
                }
                stream.DecodeBlock(hoaScratch, 0, interleavedLen);
                hoaDecoder.DecodeInterleaved(hoaScratch, samples, stream.ChannelCount, objectSamples);
                for (int s = 0; s < hoaDecoder.VirtualSpeakerCount; s++) {
                    objects[s].Position = hoaDecoder.VirtualSpeakerPositions[s] * Listener.EnvironmentSize;
                }
                return;
            }

            base.Update(samples);
            AudioDefinitionModel adm = ((RIFFWaveDecoder)stream).ADM;
            if (adm != null) {
                for (int i = 0; i < objectSamples.Length; i++) {
                    List<ADMBlockFormat> blocks = adm.Movements[i].Blocks;

                    while (admBlocks[i] > 0 && blocks[admBlocks[i] - 1].Offset.TotalSeconds * stream.SampleRate > stream.Position) {
                        admBlocks[i]--;
                    }
                    while (admBlocks[i] < blocks.Count - 1 && blocks[admBlocks[i] + 1].Offset.TotalSeconds * stream.SampleRate < stream.Position) {
                        admBlocks[i]++;
                    }

                    ADMBlockFormat current = blocks[admBlocks[i]], previous = admBlocks[i] != 0 ? blocks[admBlocks[i] - 1] : current;
                    float fade = 1;
                    if (!current.Offset.IsZero()) {
                        fade = (float)QMath.LerpInverse(current.Offset.TotalSeconds * stream.SampleRate,
                            (current.Offset + current.Interpolation).TotalSeconds * stream.SampleRate, stream.Position);
                    }
                    objects[i].Position = Vector3.Lerp(previous.Position, current.Position, QMath.Clamp01(fade)) * Listener.EnvironmentSize;
                }
            }
        }
    }
}