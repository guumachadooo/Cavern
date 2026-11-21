using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Cavern.Channels;
using Cavern.Format.Common;
using Cavern.Format.Exceptions;

namespace Cavern.Format.Decoders {
    /// <summary>
    /// AOMedia IAMF v1.0.0 Open Bitstream Unit (OBU) type codes (curated from <c>AOMediaCodec/iamf-tools</c> &amp; <c>libiamf</c>).
    /// </summary>
    public enum IamfObuType : byte {
        CodecConfig = 0,
        AudioElement = 1,
        MixPresentation = 2,
        ParameterBlock = 3,
        TemporalDelimiter = 4,
        AudioFrame = 5,
        AudioFrameId0 = 6,
        AudioFrameId17 = 23,
        SequenceHeader = 31
    }

    /// <summary>
    /// IAMF Audio Element type (Section 3.6 of AOMedia IAMF v1.0.0).
    /// </summary>
    public enum IamfAudioElementType : byte {
        ChannelBased = 0,
        SceneBased = 1
    }

    /// <summary>
    /// Parsed AOMedia IAMF (Eclipsa Audio) stream metadata summary.
    /// </summary>
    public sealed class IamfStreamInfo {
        /// <summary>IAMF primary profile (0 = Simple, 1 = Base, 2 = Base-Enhanced).</summary>
        public byte PrimaryProfile { get; set; }

        /// <summary>IAMF additional profile.</summary>
        public byte AdditionalProfile { get; set; }

        /// <summary>FourCC codec identifier (<c>ipcm</c>, <c>Opus</c>, <c>fLaC</c>).</summary>
        public string CodecFourCc { get; set; } = "ipcm";

        /// <summary>Samples per channel in each IAMF Audio Frame OBU.</summary>
        public int SamplesPerFrame { get; set; } = 1024;

        /// <summary>LPCM bit depth (16, 24, or 32).</summary>
        public int BitDepthBits { get; set; } = 24;

        /// <summary>Whether LPCM payload is little-endian (<c>sample_format_flags &amp; 1 != 0</c>).</summary>
        public bool IsLittleEndian { get; set; } = true;

        /// <summary>Stream sample rate in Hz.</summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>Audio element representation (Channel-Based scalable bed or Scene-Based Ambisonics HOA).</summary>
        public IamfAudioElementType ElementType { get; set; } = IamfAudioElementType.ChannelBased;

        /// <summary>Channel count across all substreams.</summary>
        public int ChannelCount { get; set; } = 2;

        /// <summary>Ambisonics order if <see cref="ElementType"/> is <see cref="IamfAudioElementType.SceneBased"/>.</summary>
        public int AmbisonicsOrder { get; set; }

        /// <summary>Loudspeaker layout code for Channel-Based IAMF elements.</summary>
        public byte LoudspeakerLayoutCode { get; set; } = 1;

        /// <summary>Mix presentation label (UTF-8).</summary>
        public string MixLabel { get; set; } = "Main Mix";

        /// <summary>ITU-R BS.1770 integrated loudness (dB / LKFS) decoded from IAMF Q7.8 fixed-point.</summary>
        public float IntegratedLoudnessLkfs { get; set; } = -18f;

        /// <summary>Digital peak (dBFS) decoded from IAMF Q7.8 fixed-point.</summary>
        public float DigitalPeakDbfs { get; set; } = -1f;

        /// <summary>True peak (dBTP) decoded from IAMF Q7.8 fixed-point.</summary>
        public float TruePeakDbtp { get; set; } = -1f;

        /// <summary>Total decoded audio frame count.</summary>
        public int FrameCount { get; set; }
    }

    /// <summary>
    /// Pure C# 8.0 AOMedia IAMF v1.0.0 (Eclipsa Audio) Open Bitstream Unit (OBU) decoder.
    /// Parses <c>IA_SEQUENCE_HEADER</c> (<c>"iamf"</c>), <c>CODEC_CONFIG</c> (<c>ipcm</c>),
    /// <c>AUDIO_ELEMENT</c> (Channel-Based scalable layouts &amp; Scene-Based HOA Ambisonics up to 3rd order),
    /// <c>MIX_PRESENTATION</c> (ITU-R BS.1770 Q7.8 loudness), and <c>AUDIO_FRAME</c> LPCM substreams.
    /// </summary>
    public class IamfDecoder : Decoder, IMetadataSupplier {
        /// <summary>IAMF sequence header FourCC magic <c>"iamf"</c> (big-endian <c>0x69616D66</c>).</summary>
        public const uint IaCodeMagic = 0x69616D66u;

        /// <summary>LPCM codec FourCC <c>"ipcm"</c> (<c>0x6970636D</c>).</summary>
        public const uint CodecIpcm = 0x6970636Du;

        /// <summary>Parsed IAMF metadata descriptor.</summary>
        public IamfStreamInfo Info { get; } = new IamfStreamInfo();

        readonly Stream inputStream;
        readonly List<long> frameOffsets = new List<long>();
        readonly List<int> frameByteLengths = new List<int>();
        readonly List<int> substreamChannelCounts = new List<int>();
        long samplePosition;
        int currentFrameIndex;
        float[] frameCache = Array.Empty<float>();
        int frameCacheSampleOffset;
        int frameCacheSampleCount;

        /// <summary>Current playback sample position per channel.</summary>
        public override long Position => samplePosition;

        /// <summary>
        /// Decodes an AOMedia IAMF (Eclipsa Audio) OBU stream.
        /// </summary>
        public IamfDecoder(Stream stream) {
            inputStream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (inputStream.CanSeek) {
                inputStream.Position = 0;
            }
            ParseBitStream();
        }

        /// <summary>
        /// Checks whether the first 8 bytes of a stream represent an AOMedia IAMF OBU sequence header.
        /// </summary>
        public static bool IsIamfHeader(byte[] headerBytes, int length) {
            if (headerBytes == null || length < 6) {
                return false;
            }
            int obuType = (headerBytes[0] >> 3) & 0x1F;
            if (obuType != (int)IamfObuType.SequenceHeader) {
                return false;
            }
            int idx = 1;
            if (!TryReadUleb128(headerBytes, length, ref idx, out _)) {
                return false;
            }
            if (idx + 4 > length) {
                return false;
            }
            uint magic = ((uint)headerBytes[idx] << 24) |
                ((uint)headerBytes[idx + 1] << 16) |
                ((uint)headerBytes[idx + 2] << 8) |
                headerBytes[idx + 3];
            return magic == IaCodeMagic;
        }

        /// <summary>
        /// Maps an IAMF Channel-Based <c>loudspeaker_layout</c> code to Cavern <see cref="ReferenceChannel"/> array.
        /// </summary>
        public static ReferenceChannel[] GetChannelsForLayoutCode(byte layoutCode, int fallbackCount) {
            switch (layoutCode) {
                case 0: return new[] { ReferenceChannel.FrontCenter };
                case 1: return new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight };
                case 2:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight
                    };
                case 3:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight
                    };
                case 4:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    };
                case 5:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight
                    };
                case 6:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight
                    };
                case 7:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    };
                case 8:
                    return new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight
                    };
                default:
                    return ChannelPrototype.GetStandardMatrix(Math.Max(1, fallbackCount));
            }
        }

        /// <inheritdoc/>
        public override void DecodeBlock(float[] target, long from, long to) {
            long totalNeeded = to - from;
            long written = 0;
            while (written < totalNeeded) {
                if (frameCacheSampleOffset >= frameCacheSampleCount) {
                    if (!DecodeNextFrame()) {
                        Array.Clear(target, (int)(from + written), (int)(totalNeeded - written));
                        return;
                    }
                }
                int available = frameCacheSampleCount - frameCacheSampleOffset;
                int copy = (int)Math.Min(available, totalNeeded - written);
                Array.Copy(frameCache, frameCacheSampleOffset, target, (int)(from + written), copy);
                frameCacheSampleOffset += copy;
                written += copy;
                samplePosition += copy / Math.Max(1, ChannelCount);
            }
        }

        /// <inheritdoc/>
        public override void Seek(long sample) {
            long clamped = Math.Max(0, Math.Min(Length, sample));
            int samplesPerFrame = Math.Max(1, Info.SamplesPerFrame);
            int targetFrame = (int)(clamped / samplesPerFrame);
            int remainder = (int)(clamped % samplesPerFrame);
            currentFrameIndex = Math.Min(targetFrame, frameOffsets.Count);
            samplePosition = clamped;
            frameCacheSampleOffset = 0;
            frameCacheSampleCount = 0;
            if (remainder > 0 && DecodeNextFrame()) {
                frameCacheSampleOffset = Math.Min(frameCacheSampleCount, remainder * ChannelCount);
            }
        }

        /// <summary>
        /// Gets human-readable IAMF metadata headers for inspection dialogs.
        /// </summary>
        public ReadableMetadata GetMetadata() {
            List<ReadableMetadataHeader> headers = new List<ReadableMetadataHeader> {
                new ReadableMetadataHeader("IAMF (Eclipsa Audio) Stream", new List<ReadableMetadataField> {
                    new ReadableMetadataField("Primary Profile", Info.PrimaryProfile.ToString()),
                    new ReadableMetadataField("Codec FourCC", Info.CodecFourCc),
                    new ReadableMetadataField("Element Type", Info.ElementType == IamfAudioElementType.SceneBased
                        ? $"Scene-Based HOA (Order {Info.AmbisonicsOrder}, ACN/SN3D)"
                        : $"Channel-Based (Layout {Info.LoudspeakerLayoutCode})"),
                    new ReadableMetadataField("Channels", ChannelCount.ToString()),
                    new ReadableMetadataField("Sample Rate", $"{SampleRate} Hz"),
                    new ReadableMetadataField("Bit Depth", $"{Info.BitDepthBits}-bit"),
                    new ReadableMetadataField("Frame Size", $"{Info.SamplesPerFrame} samples"),
                    new ReadableMetadataField("Mix Label", Info.MixLabel),
                    new ReadableMetadataField("Integrated Loudness", $"{Info.IntegratedLoudnessLkfs:0.00} LKFS"),
                    new ReadableMetadataField("True Peak", $"{Info.TruePeakDbtp:0.00} dBTP"),
                    new ReadableMetadataField("Audio Frames", Info.FrameCount.ToString())
                })
            };
            return new ReadableMetadata(headers);
        }

        void ParseBitStream() {
            bool sawSequenceHeader = false;
            int totalSubstreams = 1;
            int substreamIndex = 0;
            long audioFrameUnits = 0;

            while (inputStream.Position < inputStream.Length) {
                int headerByte = inputStream.ReadByte();
                if (headerByte < 0) {
                    break;
                }
                byte obuType = (byte)((headerByte >> 3) & 0x1F);
                bool hasTrimming = (headerByte & 0x02) != 0;
                bool hasExtension = (headerByte & 0x01) != 0;

                if (!TryReadUleb128Stream(inputStream, out ulong obuSize)) {
                    break;
                }
                long payloadStart = inputStream.Position;
                long payloadEnd = Math.Min(inputStream.Length, payloadStart + (long)obuSize);

                if (hasTrimming) {
                    TryReadUleb128Stream(inputStream, out _); // num_samples_to_trim_at_end
                    TryReadUleb128Stream(inputStream, out _); // num_samples_to_trim_at_start
                }
                if (hasExtension) {
                    if (TryReadUleb128Stream(inputStream, out ulong extSize)) {
                        inputStream.Position = Math.Min(payloadEnd, inputStream.Position + (long)extSize);
                    }
                }

                long bodyStart = inputStream.Position;
                int bodyLength = (int)Math.Max(0, payloadEnd - bodyStart);

                if (obuType == (byte)IamfObuType.SequenceHeader) {
                    byte[] payload = ReadExact(inputStream, bodyLength);
                    if (payload.Length >= 6) {
                        uint magic = ReadUInt32BE(payload, 0);
                        if (magic != IaCodeMagic) {
                            throw new SyncException();
                        }
                        sawSequenceHeader = true;
                        Info.PrimaryProfile = payload[4];
                        Info.AdditionalProfile = payload[5];
                    }
                } else if (obuType == (byte)IamfObuType.CodecConfig) {
                    byte[] payload = ReadExact(inputStream, bodyLength);
                    ParseCodecConfig(payload);
                } else if (obuType == (byte)IamfObuType.AudioElement) {
                    byte[] payload = ReadExact(inputStream, bodyLength);
                    totalSubstreams = ParseAudioElement(payload);
                } else if (obuType == (byte)IamfObuType.MixPresentation) {
                    byte[] payload = ReadExact(inputStream, bodyLength);
                    ParseMixPresentation(payload);
                } else if (obuType >= (byte)IamfObuType.AudioFrame && obuType <= (byte)IamfObuType.AudioFrameId17) {
                    if (obuType == (byte)IamfObuType.AudioFrame) {
                        TryReadUleb128Stream(inputStream, out _); // explicit audio_substream_id
                    }
                    long pcmOffset = inputStream.Position;
                    int pcmLen = (int)Math.Max(0, payloadEnd - pcmOffset);
                    frameOffsets.Add(pcmOffset);
                    frameByteLengths.Add(pcmLen);
                    substreamIndex++;
                    if (substreamIndex >= Math.Max(1, totalSubstreams)) {
                        audioFrameUnits++;
                        substreamIndex = 0;
                    }
                }

                inputStream.Position = payloadEnd;
            }

            if (!sawSequenceHeader) {
                throw new SyncException();
            }

            ChannelCount = Math.Max(1, Info.ChannelCount);
            SampleRate = Math.Max(8000, Info.SampleRate);
            Info.FrameCount = (int)audioFrameUnits;
            Length = audioFrameUnits * Info.SamplesPerFrame;
            Bits = Info.BitDepthBits switch {
                16 => BitDepth.Int16,
                24 => BitDepth.Int24,
                32 => BitDepth.Float32,
                _ => BitDepth.Int24
            };
        }

        void ParseCodecConfig(byte[] data) {
            int pos = 0;
            if (!TryReadUleb128(data, data.Length, ref pos, out _)) {
                return;
            }
            if (pos + 4 > data.Length) {
                return;
            }
            uint codecId = ReadUInt32BE(data, pos);
            Info.CodecFourCc = Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;

            if (!TryReadUleb128(data, data.Length, ref pos, out ulong samplesPerFrame)) {
                return;
            }
            Info.SamplesPerFrame = (int)Math.Max(64, Math.Min(16384, samplesPerFrame));
            if (pos + 2 > data.Length) {
                return;
            }
            pos += 2; // audio_roll_distance (int16)

            if (codecId == CodecIpcm && pos + 6 <= data.Length) {
                byte flags = data[pos++];
                byte sampleSize = data[pos++];
                uint sampleRate = ReadUInt32BE(data, pos);
                Info.IsLittleEndian = (flags & 0x01) != 0;
                Info.BitDepthBits = sampleSize;
                Info.SampleRate = (int)sampleRate;
            }
        }

        int ParseAudioElement(byte[] data) {
            int pos = 0;
            if (!TryReadUleb128(data, data.Length, ref pos, out _)) { // audio_element_id
                return 1;
            }
            if (pos >= data.Length) {
                return 1;
            }
            byte elementType = (byte)((data[pos++] >> 5) & 0x07);
            Info.ElementType = elementType == 1 ? IamfAudioElementType.SceneBased : IamfAudioElementType.ChannelBased;

            if (!TryReadUleb128(data, data.Length, ref pos, out _)) { // codec_config_id
                return 1;
            }
            if (!TryReadUleb128(data, data.Length, ref pos, out ulong numSubstreams)) {
                return 1;
            }
            for (ulong i = 0; i < numSubstreams; i++) {
                TryReadUleb128(data, data.Length, ref pos, out _); // audio_substream_id
            }
            if (!TryReadUleb128(data, data.Length, ref pos, out ulong numParams)) {
                return (int)Math.Max(1, numSubstreams);
            }
            for (ulong i = 0; i < numParams; i++) {
                TryReadUleb128(data, data.Length, ref pos, out _);
            }

            substreamChannelCounts.Clear();
            if (Info.ElementType == IamfAudioElementType.SceneBased) {
                if (pos + 2 <= data.Length) {
                    byte outputChannelCount = data[pos++];
                    byte substreamCount = data[pos++];
                    Info.ChannelCount = Math.Max(4, (int)outputChannelCount);
                    Info.AmbisonicsOrder = Math.Max(1, (int)Math.Round(Math.Sqrt(Info.ChannelCount)) - 1);
                    int subs = Math.Max(1, (int)substreamCount);
                    for (int s = 0; s < subs; s++) {
                        substreamChannelCounts.Add(1);
                    }
                    return subs;
                }
            } else {
                if (pos < data.Length) {
                    byte numLayers = (byte)((data[pos++] >> 5) & 0x07);
                    int totalCh = 0;
                    for (int l = 0; l < Math.Max(1, (int)numLayers) && pos + 3 <= data.Length; l++) {
                        byte layoutAndFlags = data[pos++];
                        byte layout = (byte)((layoutAndFlags >> 4) & 0x0F);
                        byte substreamCount = data[pos++];
                        byte coupledCount = data[pos++];
                        Info.LoudspeakerLayoutCode = layout;
                        totalCh = substreamCount + coupledCount;
                        substreamChannelCounts.Clear();
                        for (int c = 0; c < coupledCount; c++) {
                            substreamChannelCounts.Add(2);
                        }
                        for (int u = coupledCount; u < substreamCount; u++) {
                            substreamChannelCounts.Add(1);
                        }
                    }
                    if (totalCh > 0) {
                        Info.ChannelCount = totalCh;
                    }
                }
            }

            if (substreamChannelCounts.Count == 0) {
                substreamChannelCounts.Add(Math.Max(1, Info.ChannelCount));
            }
            return Math.Max(1, substreamChannelCounts.Count);
        }

        void ParseMixPresentation(byte[] data) {
            int pos = 0;
            if (!TryReadUleb128(data, data.Length, ref pos, out _)) { // mix_presentation_id
                return;
            }
            if (!TryReadUleb128(data, data.Length, ref pos, out ulong countLabel)) {
                return;
            }
            for (ulong i = 0; i < countLabel && pos < data.Length; i++) {
                SkipNullTerminatedString(data, ref pos); // language
            }
            for (ulong i = 0; i < countLabel && pos < data.Length; i++) {
                string label = ReadNullTerminatedString(data, ref pos);
                if (i == 0 && !string.IsNullOrEmpty(label)) {
                    Info.MixLabel = label;
                }
            }
            // Scan tail for Q7.8 loudness triple (info_type byte + int16 integrated + int16 digital_peak [+ int16 true_peak])
            if (data.Length >= 7) {
                int tail = data.Length - 7;
                byte infoType = data[tail];
                short integratedQ78 = ReadInt16BE(data, tail + 1);
                short digitalPeakQ78 = ReadInt16BE(data, tail + 3);
                short truePeakQ78 = ReadInt16BE(data, tail + 5);
                if ((infoType & 0x02) != 0) {
                    Info.IntegratedLoudnessLkfs = integratedQ78 / 256f;
                    Info.DigitalPeakDbfs = digitalPeakQ78 / 256f;
                    Info.TruePeakDbtp = truePeakQ78 / 256f;
                }
            }
        }

        bool DecodeNextFrame() {
            int subs = Math.Max(1, substreamChannelCounts.Count);
            if (currentFrameIndex + subs > frameOffsets.Count) {
                return false;
            }

            int spf = Math.Max(1, Info.SamplesPerFrame);
            int totalSamples = spf * ChannelCount;
            if (frameCache.Length < totalSamples) {
                frameCache = new float[totalSamples];
            }
            Array.Clear(frameCache, 0, totalSamples);

            int bytesPerSample = Math.Max(2, Info.BitDepthBits / 8);
            int channelCursor = 0;

            for (int s = 0; s < subs; s++) {
                int subCh = substreamChannelCounts[s];
                long offset = frameOffsets[currentFrameIndex + s];
                int byteLen = frameByteLengths[currentFrameIndex + s];
                inputStream.Position = offset;
                byte[] raw = ReadExact(inputStream, byteLen);
                int framesInSub = Math.Min(spf, raw.Length / Math.Max(1, subCh * bytesPerSample));

                for (int f = 0; f < framesInSub; f++) {
                    for (int c = 0; c < subCh; c++) {
                        int dstCh = channelCursor + c;
                        if (dstCh >= ChannelCount) {
                            continue;
                        }
                        int byteIdx = (f * subCh + c) * bytesPerSample;
                        frameCache[f * ChannelCount + dstCh] = DecodePcmSample(raw, byteIdx, bytesPerSample, Info.IsLittleEndian);
                    }
                }
                channelCursor += subCh;
            }

            currentFrameIndex += subs;
            frameCacheSampleOffset = 0;
            frameCacheSampleCount = totalSamples;
            return true;
        }

        static float DecodePcmSample(byte[] raw, int idx, int bytesPerSample, bool littleEndian) {
            if (idx + bytesPerSample > raw.Length) {
                return 0f;
            }
            if (bytesPerSample == 2) {
                int v = littleEndian
                    ? (short)(raw[idx] | (raw[idx + 1] << 8))
                    : (short)((raw[idx] << 8) | raw[idx + 1]);
                return v / 32768f;
            }
            if (bytesPerSample == 3) {
                int v = littleEndian
                    ? (raw[idx] | (raw[idx + 1] << 8) | ((sbyte)raw[idx + 2] << 16))
                    : (((sbyte)raw[idx] << 16) | (raw[idx + 1] << 8) | raw[idx + 2]);
                return v / 8388608f;
            }
            int i32 = littleEndian
                ? (raw[idx] | (raw[idx + 1] << 8) | (raw[idx + 2] << 16) | (raw[idx + 3] << 24))
                : ((raw[idx] << 24) | (raw[idx + 1] << 16) | (raw[idx + 2] << 8) | raw[idx + 3]);
            return i32 / 2147483648f;
        }

        /// <summary>Encodes an unsigned 64-bit integer into ULEB128 bytes per AOMedia IAMF Section 3.1.</summary>
        public static void WriteUleb128(Stream output, ulong value) {
            do {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) {
                    b |= 0x80;
                }
                output.WriteByte(b);
            } while (value != 0);
        }

        /// <summary>Decodes an unsigned ULEB128 integer from a byte array.</summary>
        public static bool TryReadUleb128(byte[] buffer, int length, ref int index, out ulong result) {
            result = 0;
            for (int i = 0; i < 8; i++) {
                if (index >= length) {
                    return false;
                }
                byte b = buffer[index++];
                result |= (ulong)(b & 0x7F) << (i * 7);
                if ((b & 0x80) == 0) {
                    return true;
                }
            }
            return false;
        }

        static bool TryReadUleb128Stream(Stream stream, out ulong result) {
            result = 0;
            for (int i = 0; i < 8; i++) {
                int b = stream.ReadByte();
                if (b < 0) {
                    return false;
                }
                result |= (ulong)(b & 0x7F) << (i * 7);
                if ((b & 0x80) == 0) {
                    return true;
                }
            }
            return false;
        }

        static byte[] ReadExact(Stream stream, int count) {
            if (count <= 0) {
                return Array.Empty<byte>();
            }
            byte[] buf = new byte[count];
            int total = 0;
            while (total < count) {
                int n = stream.Read(buf, total, count - total);
                if (n <= 0) {
                    break;
                }
                total += n;
            }
            if (total == count) {
                return buf;
            }
            byte[] trimmed = new byte[total];
            Array.Copy(buf, trimmed, total);
            return trimmed;
        }

        static uint ReadUInt32BE(byte[] buf, int offset) =>
            ((uint)buf[offset] << 24) |
            ((uint)buf[offset + 1] << 16) |
            ((uint)buf[offset + 2] << 8) |
            buf[offset + 3];

        static short ReadInt16BE(byte[] buf, int offset) =>
            (short)((buf[offset] << 8) | buf[offset + 1]);

        static void SkipNullTerminatedString(byte[] data, ref int pos) {
            while (pos < data.Length && data[pos] != 0) {
                pos++;
            }
            if (pos < data.Length) {
                pos++;
            }
        }

        static string ReadNullTerminatedString(byte[] data, ref int pos) {
            int start = pos;
            while (pos < data.Length && data[pos] != 0) {
                pos++;
            }
            string s = Encoding.UTF8.GetString(data, start, pos - start);
            if (pos < data.Length) {
                pos++;
            }
            return s;
        }
    }
}
