using System.Collections.Generic;
using System.IO;

using Cavern.Channels;
using Cavern.Format.Common;
using Cavern.Format.Common.Metadata;
using Cavern.Format.Consts;
using Cavern.Format.Exceptions;
using Cavern.Format.Transcoders;
using Cavern.Format.Transcoders.AudioDefinitionModelElements;
using Cavern.Format.Utilities;

namespace Cavern.Format.Decoders {
    /// <summary>
    /// Converts a RIFF WAVE bitstream to raw samples.
    /// </summary>
    public class RIFFWaveDecoder : UncompressedDecoder, IMetadataSupplier {
        /// <summary>
        /// Object metadata for Broadcast Wave Files.
        /// </summary>
        public AudioDefinitionModel ADM { get; private set; }

        /// <summary>
        /// True when the WAV header carries an Ambisonics B-Format <c>WAVEFORMATEXTENSIBLE</c> SubFormat GUID
        /// (<c>SUBTYPE_AMBISONIC_B_FORMAT_PCM</c> or <c>SUBTYPE_AMBISONIC_B_FORMAT_IEEE_FLOAT</c>).
        /// </summary>
        public bool IsAmbisonicsHoa { get; private set; }

        /// <summary>
        /// Detected Ambisonics spherical harmonic order N (1..5) when <see cref="IsAmbisonicsHoa"/> is true, otherwise 0.
        /// </summary>
        public int AmbisonicsOrder { get; private set; }

        /// <summary>
        /// WAVEFORMATEXTENSIBLE channel mask if available.
        /// </summary>
        int channelMask = -1;

        /// <summary>
        /// Converts a RIFF WAVE bitstream to raw samples.
        /// </summary>
        public RIFFWaveDecoder(BlockBuffer<byte> reader, int channelCount, long length, int sampleRate, BitDepth bits) :
            base(reader) {
            ChannelCount = channelCount;
            Length = length;
            SampleRate = sampleRate;
            Bits = bits;
        }

        /// <summary>
        /// Converts a RIFF WAVE bitstream with header to raw samples.
        /// </summary>
        /// <param name="reader">File reader object</param>
        public RIFFWaveDecoder(Stream reader) : this(reader, false) { }

        /// <summary>
        /// Converts a RIFF WAVE bitstream with header to raw samples.
        /// </summary>
        /// <param name="reader">File reader object</param>
        /// <param name="skipSyncWord">The sync word from which the format is detected was already read from the stream -
        /// allows for format detection in streams that don't support <see cref="Stream.Position"/></param>
        public RIFFWaveDecoder(Stream reader, bool skipSyncWord) {
            // RIFF header
            if (!skipSyncWord) {
                int sync = reader.ReadInt32();
                if (sync != RIFFWaveConsts.syncWord1 && sync != RIFFWaveConsts.syncWord1_64) {
                    throw new SyncException();
                }
            }
            stream = reader;
            reader.Position += 4; // File length
            if (reader.ReadInt32() != RIFFWaveConsts.syncWord2) {
                throw new SyncException();
            }

            // Subchunks
            Dictionary<int, long> sizeOverrides = null;
            ChannelAssignment chna = null;
            while (reader.Position < reader.Length) {
                int headerID = reader.ReadInt32();
                if (((headerID & 0xFF) == 0) && (reader.Position & 1) == 1) {
                    reader.Position -= 3;
                    continue;
                }
                if (headerID == 0) {
                    continue;
                }
                long headerSize = reader.ReadUInt32();
                if (sizeOverrides != null && sizeOverrides.ContainsKey(headerID)) {
                    headerSize = sizeOverrides[headerID];
                }

                switch (headerID) {
                    case RIFFWaveConsts.formatSync:
                        long headerEnd = reader.Position + headerSize;
                        ParseFormatHeader(reader);
                        reader.Position = headerEnd;
                        break;
                    case RIFFWaveConsts.ds64Sync:
                        sizeOverrides = new Dictionary<int, long> {
                            [RIFFWaveConsts.syncWord1_64] = reader.ReadInt64(),
                            [RIFFWaveConsts.dataSync] = reader.ReadInt64()
                        };
                        reader.Position += 8; // Sample count, redundant
                        int additionalSizes = reader.ReadInt32();
                        for (int i = 0; i < additionalSizes; i++) {
                            headerID = reader.ReadInt32();
                            sizeOverrides[headerID] = reader.ReadInt64();
                        }
                        break;
                    case RIFFWaveConsts.axmlSync:
                        ADM = new AudioDefinitionModel(reader, headerSize, true);
                        break;
                    case RIFFWaveConsts.chnaSync:
                        chna = new ChannelAssignment(reader);
                        break;
                    case RIFFWaveConsts.dataSync:
                        dataStart = reader.Position;
                        bool undefinedLength = headerSize == uint.MaxValue;
                        long fileLength = !undefinedLength || !reader.CanSeek ? headerSize : reader.Length - dataStart;
                        Length = fileLength / ((long)Bits >> 3) / ChannelCount;
                        if (!undefinedLength && dataStart + headerSize < reader.Length) { // Read after PCM samples if there are more tags
                            reader.Position = dataStart + headerSize;
                        } else {
                            Finalize(reader);
                            return;
                        }
                        break;
                    default: // Skip unknown headers
                        reader.Position += headerSize;
                        break;
                }
            }

            if (ADM != null && chna != null) {
                ADM.Assign(chna);
            }
            Finalize(reader);
        }

        /// <summary>
        /// Get the custom channel layout or the standard layout corresponding to this file's channel count.
        /// </summary>
        public ReferenceChannel[] GetChannels() {
            if (IsAmbisonicsHoa) {
                return Renderers.AmbisonicsHoaSpatialDecoder.VirtualLayout714;
            }
            if (channelMask == -1 || channelMask == 0) {
                return ChannelPrototype.GetStandardMatrix(ChannelCount);
            } else {
                return RIFFWaveConsts.ParseChannelMask(channelMask);
            }
        }

        /// <summary>
        /// Gets the metadata for this codec in a human-readable format.
        /// </summary>
        public ReadableMetadata GetMetadata() {
            if (ADM != null) {
                return ADMMetadataExporter.GenerateMetadata(ADM);
            }
            if (IsAmbisonicsHoa) {
                return new ReadableMetadata(new List<ReadableMetadataHeader> {
                    new ReadableMetadataHeader("Ambisonics HOA B-Format (WAVE-EX)", new List<ReadableMetadataField> {
                        new ReadableMetadataField("Order", $"Order {AmbisonicsOrder} (ACN/SN3D)"),
                        new ReadableMetadataField("Harmonic Channels", ChannelCount.ToString()),
                        new ReadableMetadataField("Sample Rate", $"{SampleRate} Hz"),
                        new ReadableMetadataField("Bit Depth", Bits.ToString())
                    })
                });
            }
            return ADMMetadataExporter.GenerateMetadata(ADM);
        }

        /// <summary>
        /// Finish header reading, start data reading.
        /// </summary>
        void Finalize(Stream reader) {
            reader.Position = dataStart;
            this.reader = BlockBuffer<byte>.Create(reader, FormatConsts.blockSize);
        }

        /// <summary>
        /// Read the main RIFF WAVE header.
        /// </summary>
        void ParseFormatHeader(Stream reader) {
            short sampleFormat = reader.ReadInt16(); // 1 = int, 3 = float, -2 = WAVEFORMATEXTENSIBLE
            ChannelCount = reader.ReadInt16();
            SampleRate = reader.ReadInt32();
            reader.Position += 4; // Bytes/sec
            reader.Position += 2; // Block size in bytes
            short bitDepth = reader.ReadInt16();
            if (sampleFormat == -2) {
                long endPosition = reader.ReadInt16() + reader.Position;
                bitDepth = reader.ReadInt16();
                channelMask = reader.ReadInt32();
                sampleFormat = reader.ReadInt16();
                if (endPosition - reader.Position >= 14) {
                    byte[] guidTail = new byte[14];
                    int read = reader.Read(guidTail, 0, 14);
                    if (read == 14 && RIFFWaveConsts.IsAmbisonicSubFormatGuidTail(guidTail)) {
                        int detectedOrder = RIFFWaveConsts.GetHoaOrderForChannelCount(ChannelCount);
                        if (detectedOrder > 0) {
                            IsAmbisonicsHoa = true;
                            AmbisonicsOrder = detectedOrder;
                        }
                    }
                }
                reader.Position = endPosition;
            }
            if (sampleFormat == 1) {
                Bits = bitDepth switch {
                    8 => BitDepth.Int8,
                    16 => BitDepth.Int16,
                    24 => BitDepth.Int24,
                    _ => throw new InvalidBitDepthException(Bits)
                };
            } else if (sampleFormat == 3 && bitDepth == 32) {
                Bits = BitDepth.Float32;
            } else {
                throw new IOException($"Unsupported bit depth ({bitDepth}) for sample format {sampleFormat}.");
            }
        }
    }
}
