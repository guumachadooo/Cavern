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
        /// <summary>Codec Config OBU defining decoder parameters for an elementary audio stream.</summary>
        CodecConfig = 0,
        /// <summary>Audio Element OBU describing channel-based or scene-based spatial audio elements.</summary>
        AudioElement = 1,
        /// <summary>Mix Presentation OBU defining loudness, rendering, and sub-mix layout rules.</summary>
        MixPresentation = 2,
        /// <summary>Parameter Block OBU carrying time-varying gain, recon gain, or demixing metadata.</summary>
        ParameterBlock = 3,
        /// <summary>Temporal Delimiter OBU marking boundaries between temporal units.</summary>
        TemporalDelimiter = 4,
        /// <summary>Audio Frame OBU carrying an explicit substream ID and encoded frame payload.</summary>
        AudioFrame = 5,
        /// <summary>Audio Frame OBU with implicit substream ID 0.</summary>
        AudioFrameId0 = 6,
        /// <summary>Audio Frame OBU with implicit substream ID 17 (upper bound of implicit IDs 0..17).</summary>
        AudioFrameId17 = 23,
        /// <summary>IA Sequence Header OBU identifying an IAMF bitstream and primary/additional profiles.</summary>
        SequenceHeader = 31
    }

    /// <summary>
    /// IAMF Audio Element type (Section 3.6 of AOMedia IAMF v1.0.0).
    /// </summary>
    public enum IamfAudioElementType : byte {
        /// <summary>Channel-based audio element (scalable loudspeaker layouts).</summary>
        ChannelBased = 0,
        /// <summary>Scene-based audio element (Ambisonics / HOA).</summary>
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

        /// <summary>Codec lookahead roll distance (e.g., negative frame count for Opus).</summary>
        public short AudioRollDistance { get; set; }

        /// <summary>Opus pre-skip sample count when <see cref="CodecFourCc"/> is <c>"Opus"</c>.</summary>
        public int PreSkip { get; set; }

        /// <summary>Total sample count declared in FLAC <c>STREAMINFO</c> when <see cref="CodecFourCc"/> is <c>"fLaC"</c>.</summary>
        public ulong TotalSamplesInStream { get; set; }

        /// <summary>Default output mix gain (Q7.8 dB) from <c>MIX_PRESENTATION</c>.</summary>
        public short DefaultMixGainQ78 { get; set; }
    }

    /// <summary>
    /// Pure C# 8.0 AOMedia IAMF v1.0.0 (Eclipsa Audio) Open Bitstream Unit (OBU) decoder.
    /// Parses <c>IA_SEQUENCE_HEADER</c> (<c>"iamf"</c>), <c>CODEC_CONFIG</c> (<c>ipcm</c>, <c>fLaC</c>, <c>Opus</c>),
    /// <c>AUDIO_ELEMENT</c> (Channel-Based scalable layouts &amp; Scene-Based HOA Ambisonics up to 3rd order),
    /// <c>MIX_PRESENTATION</c> (gain definitions &amp; ITU-R BS.1770 Q7.8 loudness), and <c>AUDIO_FRAME</c> substreams.
    /// </summary>
    public class IamfDecoder : Decoder, IMetadataSupplier {
        /// <summary>IAMF sequence header FourCC magic <c>"iamf"</c> (big-endian <c>0x69616D66</c>).</summary>
        public const uint IaCodeMagic = 0x69616D66u;

        /// <summary>LPCM codec FourCC <c>"ipcm"</c> (<c>0x6970636D</c>).</summary>
        public const uint CodecIpcm = 0x6970636Du;

        /// <summary>FLAC codec FourCC <c>"fLaC"</c> (<c>0x664C6143</c>).</summary>
        public const uint CodecFlac = 0x664C6143u;

        /// <summary>Opus codec FourCC <c>"Opus"</c> (<c>0x4F707573</c>).</summary>
        public const uint CodecOpus = 0x4F707573u;

        /// <summary>Parsed IAMF metadata descriptor.</summary>
        public IamfStreamInfo Info { get; } = new IamfStreamInfo();

        readonly Stream inputStream;
        readonly List<long> frameOffsets = new List<long>();
        readonly List<int> frameByteLengths = new List<int>();
        readonly List<int> substreamChannelCounts = new List<int>();
        int currentFrameIndex;
        float[] frameCache = Array.Empty<float>();
        byte[] rawFrameScratch = Array.Empty<byte>();
        int frameCacheSampleOffset;
        int frameCacheSampleCount;

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
                Position += copy / Math.Max(1, ChannelCount);
            }
        }

        /// <inheritdoc/>
        public override void Seek(long sample) {
            long clamped = Math.Max(0, Math.Min(Length, sample));
            int samplesPerFrame = Math.Max(1, Info.SamplesPerFrame);
            int targetFrame = (int)(clamped / samplesPerFrame);
            int remainder = (int)(clamped % samplesPerFrame);
            int subs = Math.Max(1, substreamChannelCounts.Count);
            long entryIndex = (long)targetFrame * subs;
            currentFrameIndex = entryIndex >= frameOffsets.Count ? frameOffsets.Count : (int)entryIndex;
            Position = clamped;
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
                    new ReadableMetadataField("primary_profile", "Primary profile", Info.PrimaryProfile),
                    new ReadableMetadataField("codec_id", "Codec FourCC", Info.CodecFourCc),
                    new ReadableMetadataField("audio_element_type", "Element type", Info.ElementType == IamfAudioElementType.SceneBased
                        ? $"Scene-Based HOA (Order {Info.AmbisonicsOrder}, ACN/SN3D)"
                        : $"Channel-Based (Layout {Info.LoudspeakerLayoutCode})"),
                    new ReadableMetadataField("channels", "Channel count", ChannelCount),
                    new ReadableMetadataField("sample_rate", "Sample rate", $"{SampleRate} Hz"),
                    new ReadableMetadataField("sample_size", "Bit depth", $"{Info.BitDepthBits}-bit Int PCM"),
                    new ReadableMetadataField("num_samples_per_frame", "Frame size", $"{Info.SamplesPerFrame} samples"),
                    new ReadableMetadataField("mix_label", "Mix label", Info.MixLabel),
                    new ReadableMetadataField("integrated_loudness", "Integrated loudness", $"{Info.IntegratedLoudnessLkfs:0.00} LKFS"),
                    new ReadableMetadataField("true_peak", "True peak", $"{Info.TruePeakDbtp:0.00} dBTP"),
                    new ReadableMetadataField("audio_frames", "Audio frames", Info.FrameCount)
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
                long remaining = Math.Max(0L, inputStream.Length - payloadStart);
                long clampedObuSize = obuSize > (ulong)remaining ? remaining : (long)obuSize;
                long payloadEnd = payloadStart + clampedObuSize;

                if (hasTrimming && inputStream.Position < payloadEnd) {
                    TryReadUleb128Bounded(inputStream, payloadEnd, out _); // num_samples_to_trim_at_end
                    if (inputStream.Position < payloadEnd) {
                        TryReadUleb128Bounded(inputStream, payloadEnd, out _); // num_samples_to_trim_at_start
                    }
                }
                if (hasExtension && inputStream.Position < payloadEnd) {
                    if (TryReadUleb128Bounded(inputStream, payloadEnd, out ulong extSize)) {
                        long maxSkip = payloadEnd - inputStream.Position;
                        long skip = extSize > (ulong)maxSkip ? maxSkip : (long)extSize;
                        inputStream.Position += skip;
                    }
                }

                long bodyStart = inputStream.Position;
                int bodyLength = (int)Math.Min(int.MaxValue, Math.Max(0L, payloadEnd - bodyStart));

                if (obuType == (byte)IamfObuType.SequenceHeader) {
                    byte[] payload = Slice(ReadExact(inputStream, bodyLength), 0, bodyLength);
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
                    byte[] raw = ReadExact(inputStream, bodyLength);
                    ParseCodecConfig(Slice(raw, 0, raw.Length));
                } else if (obuType == (byte)IamfObuType.AudioElement) {
                    byte[] raw = ReadExact(inputStream, bodyLength);
                    totalSubstreams = ParseAudioElement(Slice(raw, 0, raw.Length));
                } else if (obuType == (byte)IamfObuType.MixPresentation) {
                    byte[] raw = ReadExact(inputStream, bodyLength);
                    ParseMixPresentation(Slice(raw, 0, raw.Length));
                } else if (obuType >= (byte)IamfObuType.AudioFrame && obuType <= (byte)IamfObuType.AudioFrameId17) {
                    if (obuType == (byte)IamfObuType.AudioFrame && inputStream.Position < payloadEnd) {
                        TryReadUleb128Bounded(inputStream, payloadEnd, out _); // explicit audio_substream_id
                    }
                    long pcmOffset = Math.Min(payloadEnd, inputStream.Position);
                    int pcmLen = (int)Math.Min(int.MaxValue, Math.Max(0L, payloadEnd - pcmOffset));
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
            // AOMedia IAMF ipcm carries signed integer PCM (16, 24, or 32-bit int — never IEEE Float32).
            // Because Cavern.Format.Common.BitDepth reserves 32 exclusively for IEEE Float32, map 32-bit
            // integer ipcm to BitDepth.Int24 for downstream integer-PCM contracts while DecodePcmSample
            // continues decoding full 32-bit signed integer precision using Info.BitDepthBits.
            Bits = Info.BitDepthBits switch {
                16 => BitDepth.Int16,
                24 => BitDepth.Int24,
                32 => BitDepth.Int24,
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
            Info.AudioRollDistance = ReadInt16BE(data, pos);
            pos += 2; // audio_roll_distance (int16)

            if (codecId == CodecIpcm && pos + 6 <= data.Length) {
                byte flags = data[pos++];
                byte sampleSize = data[pos++];
                uint sampleRate = ReadUInt32BE(data, pos);
                Info.IsLittleEndian = (flags & 0x01) != 0;
                Info.BitDepthBits = sampleSize;
                Info.SampleRate = (int)sampleRate;
            } else if (codecId == CodecFlac) {
                while (pos + 4 <= data.Length) {
                    byte blockHdr = data[pos];
                    bool isLast = (blockHdr & 0x80) != 0;
                    byte blockType = (byte)(blockHdr & 0x7F);
                    int blockLen = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                    pos += 4;
                    int nextBlock = Math.Min(data.Length, pos + Math.Max(0, blockLen));
                    if (blockType == 0 && pos + 34 <= nextBlock) {
                        ushort minBlock = ReadUInt16BE(data, pos);
                        ushort maxBlock = ReadUInt16BE(data, pos + 2);
                        if (minBlock >= 16 && minBlock == maxBlock && Info.SamplesPerFrame <= 0) {
                            Info.SamplesPerFrame = minBlock;
                        }
                        ulong packed = ReadUInt64BE(data, pos + 10);
                        int sr = (int)((packed >> 44) & 0xFFFFFu);
                        int bps = (int)(((packed >> 36) & 0x1Fu) + 1);
                        Info.TotalSamplesInStream = packed & 0xFFFFFFFFFu;
                        if (sr >= 8000) {
                            Info.SampleRate = sr;
                        }
                        if (bps >= 8 && bps <= 32) {
                            Info.BitDepthBits = bps;
                        }
                    }
                    pos = nextBlock;
                    if (isLast) {
                        break;
                    }
                }
            } else if (codecId == CodecOpus && pos + 11 <= data.Length) {
                pos += 2; // version, output_channel_count
                Info.PreSkip = ReadUInt16BE(data, pos);
                pos += 2;
                uint inputSampleRate = ReadUInt32BE(data, pos);
                Info.SampleRate = inputSampleRate >= 8000 ? (int)inputSampleRate : 48000;
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
                if (!TryReadUleb128(data, data.Length, ref pos, out _)) { // audio_substream_id
                    break;
                }
            }
            if (!TryReadUleb128(data, data.Length, ref pos, out ulong numParams)) {
                return (int)Math.Max(1, numSubstreams);
            }
            for (ulong i = 0; i < numParams && pos < data.Length; i++) {
                if (!TrySkipParamDefinition(data, ref pos, out _)) {
                    break;
                }
            }

            substreamChannelCounts.Clear();
            if (Info.ElementType == IamfAudioElementType.SceneBased) {
                int ambisonicsStart = pos;
                if (TryReadUleb128(data, data.Length, ref pos, out ulong ambisonicsMode)) {
                    // Fallback for legacy buggy writers emitting output_channel_count (>=4) before ambisonics_mode (0 at byte +2)
                    if (ambisonicsMode >= 4 && ambisonicsStart + 3 <= data.Length && data[ambisonicsStart + 2] == 0) {
                        byte legacyOutCh = data[ambisonicsStart];
                        byte legacySubCount = data[ambisonicsStart + 1];
                        Info.ChannelCount = Math.Max(4, (int)legacyOutCh);
                        Info.AmbisonicsOrder = Math.Max(1, (int)Math.Round(Math.Sqrt(Info.ChannelCount)) - 1);
                        int legacySubs = Math.Max(1, (int)legacySubCount);
                        for (int s = 0; s < legacySubs; s++) {
                            substreamChannelCounts.Add(1);
                        }
                        return legacySubs;
                    }

                    if (ambisonicsMode == 0 && pos + 2 <= data.Length) {
                        byte outputChannelCount = data[pos++];
                        byte substreamCount = data[pos++];
                        // Tolerate optional zero coupled_substream_count byte if present before channel_mapping
                        if (data.Length - pos == outputChannelCount + 1 && data[pos] == 0) {
                            pos++;
                        }
                        pos = Math.Min(data.Length, pos + outputChannelCount);
                        Info.ChannelCount = Math.Max(1, (int)outputChannelCount);
                        Info.AmbisonicsOrder = Math.Max(1, (int)Math.Round(Math.Sqrt(Info.ChannelCount)) - 1);
                        int subs = Math.Max(1, (int)substreamCount);
                        for (int s = 0; s < subs; s++) {
                            substreamChannelCounts.Add(1);
                        }
                        return subs;
                    }

                    if (ambisonicsMode == 1 && pos + 3 <= data.Length) {
                        byte outputChannelCount = data[pos++];
                        byte substreamCount = data[pos++];
                        byte coupledCount = data[pos++];
                        int matrixBytes = 2 * ((int)substreamCount + coupledCount) * outputChannelCount;
                        pos = Math.Min(data.Length, pos + Math.Max(0, matrixBytes));
                        Info.ChannelCount = Math.Max(1, (int)outputChannelCount);
                        Info.AmbisonicsOrder = Math.Max(1, (int)Math.Round(Math.Sqrt(Info.ChannelCount)) - 1);
                        for (int c = 0; c < coupledCount; c++) {
                            substreamChannelCounts.Add(2);
                        }
                        for (int u = coupledCount; u < substreamCount; u++) {
                            substreamChannelCounts.Add(1);
                        }
                        if (substreamChannelCounts.Count > 0) {
                            return substreamChannelCounts.Count;
                        }
                    }
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

            bool parsedLoudness = false;
            if (TryReadUleb128(data, data.Length, ref pos, out ulong numSubMixes)) {
                for (ulong sm = 0; sm < numSubMixes && pos < data.Length; sm++) {
                    if (!TryReadUleb128(data, data.Length, ref pos, out ulong numAudioElements)) {
                        break;
                    }
                    for (ulong ae = 0; ae < numAudioElements && pos < data.Length; ae++) {
                        if (!TryReadUleb128(data, data.Length, ref pos, out _)) { // audio_element_id
                            pos = data.Length;
                            break;
                        }
                        for (ulong l = 0; l < countLabel && pos < data.Length; l++) {
                            SkipNullTerminatedString(data, ref pos); // localized_element_annotations
                        }
                        if (pos >= data.Length) {
                            break;
                        }
                        pos++; // headphones_rendering_mode + reserved
                        if (TryReadUleb128(data, data.Length, ref pos, out ulong extSize)) {
                            int skip = (int)Math.Min((ulong)(data.Length - pos), extSize);
                            pos += skip;
                        } else {
                            break;
                        }
                        // element_mix_gain (MixGainParamDefinition: param_definition_type = 0)
                        if (!TrySkipParamDefinitionBody(data, ref pos, 0, out _)) {
                            pos = data.Length;
                            break;
                        }
                    }

                    // output_mix_gain (MixGainParamDefinition: param_definition_type = 0)
                    if (!TrySkipParamDefinitionBody(data, ref pos, 0, out short outMixGain)) {
                        break;
                    }
                    if (sm == 0) {
                        Info.DefaultMixGainQ78 = outMixGain;
                    }

                    if (!TryReadUleb128(data, data.Length, ref pos, out ulong numLayouts)) {
                        break;
                    }
                    for (ulong ly = 0; ly < numLayouts && pos < data.Length; ly++) {
                        pos++; // loudness_layout (layout_type + sound_system/reserved)
                        if (pos + 5 > data.Length) {
                            pos = data.Length;
                            break;
                        }
                        byte infoType = data[pos++];
                        short integratedQ78 = ReadInt16BE(data, pos);
                        short digitalPeakQ78 = ReadInt16BE(data, pos + 2);
                        pos += 4;

                        short truePeakQ78 = digitalPeakQ78;
                        bool hasTruePeak = false;
                        if ((infoType & 0x01) != 0 && pos + 2 <= data.Length) {
                            truePeakQ78 = ReadInt16BE(data, pos);
                            pos += 2;
                            hasTruePeak = true;
                        }
                        if ((infoType & 0x02) != 0 && pos < data.Length) {
                            // Legacy Cavern writers emitted 0x02 with a 2-byte true peak at the tail
                            if (!hasTruePeak && pos + 2 == data.Length) {
                                truePeakQ78 = ReadInt16BE(data, pos);
                                pos += 2;
                                hasTruePeak = true;
                            } else {
                                byte numAnchored = data[pos++];
                                int anchoredBytes = numAnchored * 3;
                                pos = Math.Min(data.Length, pos + anchoredBytes);
                            }
                        }
                        if ((infoType & 0xFC) != 0 && TryReadUleb128(data, data.Length, ref pos, out ulong infoExtSize)) {
                            pos = Math.Min(data.Length, pos + (int)Math.Min((ulong)(data.Length - pos), infoExtSize));
                        }

                        if (!parsedLoudness) {
                            Info.IntegratedLoudnessLkfs = integratedQ78 / 256f;
                            Info.DigitalPeakDbfs = digitalPeakQ78 / 256f;
                            if (hasTruePeak) {
                                Info.TruePeakDbtp = truePeakQ78 / 256f;
                            }
                            parsedLoudness = true;
                        }
                    }
                }
            }

            // Legacy fallback if older stream omitted element/output mix gain param definitions
            if (!parsedLoudness && data.Length >= 7) {
                int tail = data.Length - 7;
                byte infoType = data[tail];
                short integratedQ78 = ReadInt16BE(data, tail + 1);
                short digitalPeakQ78 = ReadInt16BE(data, tail + 3);
                short truePeakQ78 = ReadInt16BE(data, tail + 5);
                if ((infoType & 0x03) != 0) {
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
                int rawLen = ReadExactInto(inputStream, byteLen, ref rawFrameScratch);
                int framesInSub = Math.Min(spf, rawLen / Math.Max(1, subCh * bytesPerSample));

                for (int f = 0; f < framesInSub; f++) {
                    for (int c = 0; c < subCh; c++) {
                        int dstCh = channelCursor + c;
                        if (dstCh >= ChannelCount) {
                            continue;
                        }
                        int byteIdx = (f * subCh + c) * bytesPerSample;
                        frameCache[f * ChannelCount + dstCh] = DecodePcmSample(rawFrameScratch, rawLen, byteIdx, bytesPerSample, Info.IsLittleEndian);
                    }
                }
                channelCursor += subCh;
            }

            currentFrameIndex += subs;
            frameCacheSampleOffset = 0;
            frameCacheSampleCount = totalSamples;
            return true;
        }

        static float DecodePcmSample(byte[] raw, int rawLen, int idx, int bytesPerSample, bool littleEndian) {
            if (idx + bytesPerSample > rawLen) {
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

        static int ReadExactInto(Stream stream, int count, ref byte[] scratch) {
            if (count <= 0) {
                return 0;
            }
            if (scratch == null || scratch.Length < count) {
                scratch = new byte[count];
            }
            int total = 0;
            while (total < count) {
                int n = stream.Read(scratch, total, count - total);
                if (n <= 0) {
                    break;
                }
                total += n;
            }
            return total;
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

        static bool TryReadUleb128Bounded(Stream stream, long maxPosition, out ulong result) {
            result = 0;
            for (int i = 0; i < 8; i++) {
                if (stream.Position >= maxPosition) {
                    return false;
                }
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

        static bool TrySkipParamDefinition(byte[] data, ref int pos, out short defaultMixGain) {
            defaultMixGain = 0;
            if (!TryReadUleb128(data, data.Length, ref pos, out ulong paramType)) {
                return false;
            }
            return TrySkipParamDefinitionBody(data, ref pos, paramType, out defaultMixGain);
        }

        static bool TrySkipParamDefinitionBody(byte[] data, ref int pos, ulong paramType, out short defaultMixGain) {
            defaultMixGain = 0;
            if (paramType >= 3) {
                if (!TryReadUleb128(data, data.Length, ref pos, out ulong extSize)) {
                    return false;
                }
                pos = Math.Min(data.Length, pos + (int)Math.Min((ulong)(data.Length - pos), extSize));
                return true;
            }

            // Common ParamDefinition fields: parameter_id, parameter_rate, param_definition_mode
            if (!TryReadUleb128(data, data.Length, ref pos, out _) ||
                !TryReadUleb128(data, data.Length, ref pos, out _) ||
                pos >= data.Length) {
                return false;
            }
            bool mode1 = (data[pos++] & 0x80) != 0;
            if (!mode1) {
                if (!TryReadUleb128(data, data.Length, ref pos, out _) || // duration
                    !TryReadUleb128(data, data.Length, ref pos, out ulong constSubblockDuration)) {
                    return false;
                }
                if (constSubblockDuration == 0) {
                    if (!TryReadUleb128(data, data.Length, ref pos, out ulong numSubblocks)) {
                        return false;
                    }
                    for (ulong s = 0; s < numSubblocks; s++) {
                        if (!TryReadUleb128(data, data.Length, ref pos, out _)) {
                            return false;
                        }
                    }
                }
            }

            if (paramType == 0) { // MixGainParamDefinition
                if (pos + 2 > data.Length) {
                    return false;
                }
                defaultMixGain = ReadInt16BE(data, pos);
                pos += 2;
            } else if (paramType == 1) { // DemixingParamDefinition (DefaultDemixingInfoParameterData = 2 bytes)
                if (pos + 2 > data.Length) {
                    return false;
                }
                pos += 2;
            }
            return true;
        }

        static byte[] Slice(byte[] source, int offset, int length) {
            if (source == null || length <= 0 || offset >= source.Length) {
                return Array.Empty<byte>();
            }
            int clamped = Math.Min(length, source.Length - offset);
            if (offset == 0 && clamped == source.Length) {
                return source;
            }
            byte[] copy = new byte[clamped];
            Array.Copy(source, offset, copy, 0, clamped);
            return copy;
        }

        static ushort ReadUInt16BE(byte[] buf, int offset) =>
            (ushort)((buf[offset] << 8) | buf[offset + 1]);

        static uint ReadUInt32BE(byte[] buf, int offset) =>
            ((uint)buf[offset] << 24) |
            ((uint)buf[offset + 1] << 16) |
            ((uint)buf[offset + 2] << 8) |
            buf[offset + 3];

        static ulong ReadUInt64BE(byte[] buf, int offset) =>
            ((ulong)buf[offset] << 56) |
            ((ulong)buf[offset + 1] << 48) |
            ((ulong)buf[offset + 2] << 40) |
            ((ulong)buf[offset + 3] << 32) |
            ((ulong)buf[offset + 4] << 24) |
            ((ulong)buf[offset + 5] << 16) |
            ((ulong)buf[offset + 6] << 8) |
            buf[offset + 7];

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
