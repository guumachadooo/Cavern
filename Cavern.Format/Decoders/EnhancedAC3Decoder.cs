using System;
using System.Collections.Generic;
using System.Linq;

using Cavern.Channels;
using Cavern.Format.Common;
using Cavern.Format.Decoders.EnhancedAC3;
using Cavern.Format.Transcoders;
using Cavern.Format.Utilities;
using Cavern.Utilities;

namespace Cavern.Format.Decoders {
    /// <summary>
    /// Converts an Enhanced AC-3 bitstream to raw samples.
    /// </summary>
    public class EnhancedAC3Decoder : FrameBasedDecoder, IMetadataSupplier {
        /// <summary>
        /// The stream is coded in the Enhanced version of AC-3.
        /// </summary>
        public bool Enhanced => header.Decoder >= Transcoders.EnhancedAC3.Decoders.LG_EAC3;

        /// <summary>
        /// True if the stream has reached its end.
        /// </summary>
        public bool Finished { get; private set; }

        /// <summary>
        /// Samples in each decoded frame.
        /// </summary>
        public int FrameSize => header.Blocks * samplesPerBlock;

        /// <summary>
        /// Decoded Dolby Dialogue Normalization level in LKFS/LUFS (-31..-1 LUFS per ATSC A/52 &amp; ETSI TS 102 366).
        /// </summary>
        public float DialnormLufs => header.DialnormLufs;

        /// <summary>
        /// True when E-AC-3 channel coupling was active in the most recently decoded audio frame.
        /// </summary>
        public bool CouplingActive { get; private set; }

        /// <summary>
        /// True when E-AC-3 Spectral Extension (SPX) was active in the most recently decoded audio frame.
        /// </summary>
        public bool SpxActive { get; private set; }

        /// <summary>
        /// Live channel coupling start frequency in Hz across active substreams (0 when inactive).
        /// </summary>
        public float CouplingStartHz { get; private set; }

        /// <summary>
        /// Live Spectral Extension (SPX) start frequency in Hz across active substreams (0 when inactive).
        /// </summary>
        public float SpxStartHz { get; private set; }

        /// <summary>
        /// Header data container and reader.
        /// </summary>
        readonly EnhancedAC3Header header = new EnhancedAC3Header();

        /// <summary>
        /// Independently decoded substreams.
        /// </summary>
        readonly Dictionary<int, EnhancedAC3Body> bodies = new Dictionary<int, EnhancedAC3Body>();

        /// <summary>
        /// Rendered samples for each channel.
        /// </summary>
        readonly Dictionary<ReferenceChannel, float[]> outputs = new Dictionary<ReferenceChannel, float[]>();

        /// <summary>
        /// Bytes per audio frame.
        /// </summary>
        long frameSize;

        /// <summary>
        /// Auxillary metadata parsed for the last decoded frame.
        /// </summary>
        internal ExtensibleMetadataDecoder Extensions { get; private set; } = new ExtensibleMetadataDecoder();

        /// <summary>
        /// Reusable output sample array.
        /// </summary>
        float[] outCache = Array.Empty<float>();

        /// <summary>
        /// Converts an Enhanced AC-3 bitstream to raw samples.
        /// </summary>
        public EnhancedAC3Decoder(BlockBuffer<byte> reader) : base(reader) {
            Length = -1;
            Bits = BitDepth.Int24;
        }

        /// <summary>
        /// Converts an Enhanced AC-3 bitstream to raw samples. When the file size is known, the length can be calculated
        /// from the bitrate assuming AC-3 is constant bitrate.
        /// </summary>
        /// <param name="reader">Accesses the linear E-AC-3 bitstream</param>
        /// <param name="fileSize">Length of the E-AC-3 bitstream</param>
        public EnhancedAC3Decoder(BlockBuffer<byte> reader, long fileSize) : this(reader) =>
            Length = fileSize / frameSize * FrameSize;

        /// <summary>
        /// Get the bed channels.
        /// </summary>
        public ReferenceChannel[] GetChannels() {
            ReferenceChannel[] result = new ReferenceChannel[outputs.Count];
            outputs.Keys.CopyTo(result, 0);
            return result;
        }

        /// <summary>
        /// Decode a new frame if the cached samples are already fetched.
        /// </summary>
        protected override float[] DecodeFrame() {
            if (outputs.Count == 0) {
                ReadHeader();
            }

            long frameStart = reader.LastFetchStart;
            bool frameCplActive = false;
            bool frameSpxActive = false;
            float frameMinCplHz = float.MaxValue;
            float frameMinSpxHz = float.MaxValue;
            do {
                if (Finished) {
                    outCache.Clear();
                    return outCache;
                }

                EnhancedAC3Body body = bodies[header.SubstreamID];
                body.Update();
                if (body.CouplingActive && body.CouplingStartHz > 0f) {
                    frameCplActive = true;
                    if (body.CouplingStartHz < frameMinCplHz) {
                        frameMinCplHz = body.CouplingStartHz;
                    }
                }
                if (body.SpxActive && body.SpxStartHz > 0f) {
                    frameSpxActive = true;
                    if (body.SpxStartHz < frameMinSpxHz) {
                        frameMinSpxHz = body.SpxStartHz;
                    }
                }
                for (int i = 0, c = body.Channels.Count; i < c; ++i) {
                    outputs[body.Channels[i]] = body.FrameResult[i];
                }
                if (header.LFE) {
                    outputs[ReferenceChannel.ScreenLFE] = body.LFEResult;
                }

                Extensions.Decode(body.GetAuxData());
                ReadHeader();
            } while (header.SubstreamID != 0);

            CouplingActive = frameCplActive;
            CouplingStartHz = frameCplActive ? frameMinCplHz : 0f;
            SpxActive = frameSpxActive;
            SpxStartHz = frameSpxActive ? frameMinSpxHz : 0f;

            int outLength = outputs.Count * FrameSize;
            if (outCache.Length != outLength) {
                outCache = new float[outputs.Count * FrameSize];
            }

            int channelIndex = 0;
            IOrderedEnumerable<KeyValuePair<ReferenceChannel, float[]>> orderedChannels = outputs.OrderBy(x => x.Key);
            foreach (KeyValuePair<ReferenceChannel, float[]> channel in orderedChannels) {
                WaveformUtils.Insert(channel.Value, outCache, channelIndex++, outputs.Count);
            }

            if (frameStart < reader.LastFetchStart) {
                frameSize = reader.LastFetchStart - frameStart;
            }

            ChannelCount = outputs.Count;
            SampleRate = header.SampleRate;
            return outCache;
        }

        /// <summary>
        /// Reads all metadata for the next frame and prepares audio decoding.
        /// </summary>
        /// <remarks>This decoder has to read the beginning of the next frame to know if it's a beginning.</remarks>
        void ReadHeader() {
            if (reader.Readable) {
                BitExtractor extractor = header.Decode(reader);
                if (extractor == null || !extractor.Readable) {
                    Finished = true;
                    return;
                }
                if (!bodies.ContainsKey(header.SubstreamID)) {
                    bodies[header.SubstreamID] = new EnhancedAC3Body(header);
                }
                bodies[header.SubstreamID].PrepareUpdate(extractor);
            } else {
                Finished = true;
            }
        }

        /// <summary>
        /// Start the following reads from the selected sample.
        /// </summary>
        /// <param name="sample">The selected sample, for a single channel</param>
        /// <remarks>Assuming a constant bitrate, swapping to a frame is possible. Inter-frame seeking is not possible in this
        /// implementation, this function shouldn't be used for alignment. For tracks, use the container's seek.</remarks>
        public override void Seek(long sample) {
            if (Length == -1) {
                throw new NotImplementedException(trackSeekError);
            }

            long targetFrame = sample / samplesPerBlock / header.Blocks;
            reader.Seek(targetFrame * frameSize);
        }

        /// <summary>
        /// Gets the metadata for this codec in a human-readable format.
        /// </summary>
        public ReadableMetadata GetMetadata() {
            IEnumerable<ReadableMetadataHeader> headers = header.GetMetadata().Headers;
            if (Extensions.OAMD != null) {
                headers = headers.Concat(Extensions.OAMD.GetMetadata().Headers);
            }
            if (Extensions.JOC != null) {
                headers = headers.Concat(Extensions.JOC.GetMetadata().Headers);
            }
            return new ReadableMetadata(headers.ToList());
        }

        /// <summary>
        /// Each (E-)AC-3 block is 256 samples.
        /// </summary>
        const int samplesPerBlock = 256;

        /// <summary>
        /// Exception message when trying to seek a track.
        /// </summary>
        const string trackSeekError = "For seeking in an (E-)AC-3 track, use the container's Seek function.";
    }
}