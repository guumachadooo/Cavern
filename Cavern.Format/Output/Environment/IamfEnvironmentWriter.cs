using System;
using System.IO;
using System.Numerics;
using System.Text;

using Cavern.Filters;
using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;

namespace Cavern.Format.Environment {
    /// <summary>
    /// Audio codec descriptor mode written into the IAMF <c>CODEC_CONFIG</c> OBU.
    /// </summary>
    public enum IamfCodecMode {
        /// <summary>Uncompressed signed integer LPCM (<c>ipcm</c>).</summary>
        Ipcm = 0,
        /// <summary>Free Lossless Audio Codec (<c>fLaC</c>) with 34-byte <c>STREAMINFO</c>.</summary>
        Flac = 1,
        /// <summary>Opus (<c>Opus</c>) at 48 kHz with normative lookahead roll distance.</summary>
        Opus = 2
    }

    /// <summary>
    /// Exports a Cavern <see cref="Listener"/> 3D audio scene to an AOMedia IAMF v1.0.0 (Eclipsa Audio) OBU stream.
    /// Writes compliant <c>IA_SEQUENCE_HEADER</c> (<c>"iamf"</c>), <c>CODEC_CONFIG</c> (<c>ipcm</c> lossless PCM, plus <c>fLaC</c>/<c>Opus</c> descriptor helpers),
    /// <c>AUDIO_ELEMENT</c> (Scene-Based 3rd-Order ACN/SN3D Ambisonics HOA or Channel-Based 7.1.4),
    /// <c>MIX_PRESENTATION</c> with mandatory gain definitions and Q7.8 ITU-R BS.1770 loudness metadata, and <c>AUDIO_FRAME</c> OBUs.
    /// </summary>
    public class IamfEnvironmentWriter : EnvironmentWriter {
        /// <summary>ITU-R BS.1770 / EBU R128 true-peak headroom ceiling in dBTP (-1.0 dBTP).</summary>
        public const float TruePeakCeilingDbtp = -1.0f;

        /// <summary>Linear amplitude corresponding to -1.0 dBTP (<c>10^(-1/20)</c>).</summary>
        const float TruePeakCeilingLinear = 0.891250938f;

        /// <summary>Whether to encode Scene-Based 3rd-Order HOA (16 ch ACN/SN3D) instead of Channel-Based 7.1.4 (12 ch).</summary>
        public bool UseSceneBasedHoa { get; }

        /// <summary>Ambisonic order when <see cref="UseSceneBasedHoa"/> is true (default 3 = 16 channels).</summary>
        public int HoaOrder { get; }

        /// <summary>Mix presentation label written into the IAMF Mix Presentation OBU.</summary>
        public string MixLabel { get; }

        /// <summary>Codec descriptor mode written into the <c>CODEC_CONFIG</c> OBU.</summary>
        public IamfCodecMode CodecMode { get; }

        readonly int outputChannels;
        readonly int samplesPerFrame;
        readonly int bytesPerSample;
        readonly double[] shBuffer;
        readonly float[][] channelAccumulators;
        readonly byte[] pcmFrameBytes;
        readonly TruePeakDetector truePeakDetector;
        bool headerWritten;
        float peakAbs;
        float headroomGain = 1f;

        /// <summary>
        /// Exports a Cavern listener environment to an IAMF file path.
        /// </summary>
        public IamfEnvironmentWriter(
            string path,
            Listener source,
            long length,
            BitDepth bits,
            bool useSceneBasedHoa = true,
            int hoaOrder = 3,
            string mixLabel = "Cavernize Eclipsa Audio Master")
            : this(AudioWriter.Open(path), source, length, bits, useSceneBasedHoa, hoaOrder, mixLabel, IamfCodecMode.Ipcm) { }

        /// <summary>
        /// Exports a Cavern listener environment to an IAMF file path with an explicit <see cref="IamfCodecMode"/>.
        /// </summary>
        public IamfEnvironmentWriter(
            string path,
            Listener source,
            long length,
            BitDepth bits,
            bool useSceneBasedHoa,
            int hoaOrder,
            string mixLabel,
            IamfCodecMode codecMode)
            : this(AudioWriter.Open(path), source, length, bits, useSceneBasedHoa, hoaOrder, mixLabel, codecMode) { }

        /// <summary>
        /// Exports a Cavern listener environment to an IAMF stream.
        /// </summary>
        public IamfEnvironmentWriter(
            Stream writer,
            Listener source,
            long length,
            BitDepth bits,
            bool useSceneBasedHoa = true,
            int hoaOrder = 3,
            string mixLabel = "Cavernize Eclipsa Audio Master")
            : this(writer, source, length, bits, useSceneBasedHoa, hoaOrder, mixLabel, IamfCodecMode.Ipcm) { }

        /// <summary>
        /// Exports a Cavern listener environment to an IAMF stream with an explicit <see cref="IamfCodecMode"/>.
        /// </summary>
        public IamfEnvironmentWriter(
            Stream writer,
            Listener source,
            long length,
            BitDepth bits,
            bool useSceneBasedHoa,
            int hoaOrder,
            string mixLabel,
            IamfCodecMode codecMode)
            : base(writer, source, length, bits) {
            UseSceneBasedHoa = useSceneBasedHoa;
            HoaOrder = Math.Max(1, Math.Min(3, hoaOrder));
            MixLabel = string.IsNullOrEmpty(mixLabel) ? "Cavernize Eclipsa Audio Master" : mixLabel;
            CodecMode = codecMode;
            outputChannels = UseSceneBasedHoa ? (HoaOrder + 1) * (HoaOrder + 1) : 12;
            samplesPerFrame = Math.Max(64, source.UpdateRate);
            bytesPerSample = bits == BitDepth.Int16 ? 2 : bits == BitDepth.Float32 ? 4 : 3;
            shBuffer = new double[outputChannels];
            channelAccumulators = new float[outputChannels][];
            for (int c = 0; c < outputChannels; c++) {
                channelAccumulators[c] = new float[samplesPerFrame];
            }
            pcmFrameBytes = new byte[samplesPerFrame * bytesPerSample];
            truePeakDetector = new TruePeakDetector(outputChannels);
        }

        /// <inheritdoc/>
        public override void WriteNextFrame() {
            if (!headerWritten) {
                WriteIamfDescriptors();
                headerWritten = true;
            }

            Source.Ping();
            for (int c = 0; c < outputChannels; c++) {
                Array.Clear(channelAccumulators[c], 0, samplesPerFrame);
            }

            Vector3 invEnv = new Vector3(
                Listener.EnvironmentSize.X > 1e-4f ? 1f / Listener.EnvironmentSize.X : 1f,
                Listener.EnvironmentSize.Y > 1e-4f ? 1f / Listener.EnvironmentSize.Y : 1f,
                Listener.EnvironmentSize.Z > 1e-4f ? 1f / Listener.EnvironmentSize.Z : 1f);

            int srcIndex = 0;
            foreach (Source src in Source.ActiveSources) {
                float[] rendered = src.Rendered != null && src.Rendered.Length > 0 ? src.Rendered[0] : null;
                if (rendered == null) {
                    srcIndex++;
                    continue;
                }
                int count = Math.Min(samplesPerFrame, rendered.Length);
                if (UseSceneBasedHoa) {
                    Vector3 rel = (src.Position - Source.Position) * invEnv;
                    AmbisonicsHoaSpatialDecoder.EncodeDirectionSn3d(rel, HoaOrder, shBuffer);
                    for (int q = 0; q < outputChannels; q++) {
                        float gain = (float)shBuffer[q];
                        if (Math.Abs(gain) < 1e-7f) {
                            continue;
                        }
                        float[] dst = channelAccumulators[q];
                        for (int s = 0; s < count; s++) {
                            dst[s] += rendered[s] * gain;
                        }
                    }
                } else {
                    int targetCh = srcIndex % outputChannels;
                    float[] dst = channelAccumulators[targetCh];
                    for (int s = 0; s < count; s++) {
                        dst[s] += rendered[s];
                    }
                }
                srcIndex++;
            }

            // ITU-R BS.1770 4x-oversampled -1.0 dBTP true-peak headroom scaling before PCM encode so HOA sums never clip
            ApplyTruePeakHeadroom();

            // Write Temporal Delimiter OBU + 1 Audio Frame OBU per mono substream (ACN 0..N-1)
            WriteObu(IamfObuType.TemporalDelimiter, Array.Empty<byte>());
            for (int c = 0; c < outputChannels; c++) {
                float[] chSamples = channelAccumulators[c];
                EncodeMonoPcm(chSamples, pcmFrameBytes);
                if (c < 18) {
                    WriteObu((IamfObuType)((byte)IamfObuType.AudioFrameId0 + c), pcmFrameBytes);
                } else {
                    using (MemoryStream ms = new MemoryStream(pcmFrameBytes.Length + 4)) {
                        IamfDecoder.WriteUleb128(ms, (ulong)c);
                        ms.Write(pcmFrameBytes, 0, pcmFrameBytes.Length);
                        WriteObu(IamfObuType.AudioFrame, ms.ToArray());
                    }
                }
            }
        }

        void WriteIamfDescriptors() {
            uint sampleRate = (uint)Math.Max(8000, Source.SampleRate);

            // 1. OBU_IA_SEQUENCE_HEADER ("iamf", Base profile = 1, Base additional = 1)
            byte[] seqPayload = { 0x69, 0x61, 0x6D, 0x66, 0x01, 0x01 };
            WriteObu(IamfObuType.SequenceHeader, seqPayload);

            // 2. OBU_IA_CODEC_CONFIG (id=0, ipcm / fLaC / Opus)
            using (MemoryStream cc = new MemoryStream(64)) {
                if (CodecMode == IamfCodecMode.Flac) {
                    WriteFlacCodecConfig(cc, samplesPerFrame, (int)sampleRate, bytesPerSample * 8);
                } else if (CodecMode == IamfCodecMode.Opus) {
                    WriteOpusCodecConfig(cc, samplesPerFrame, (int)sampleRate);
                } else {
                    WriteIpcmCodecConfig(cc, samplesPerFrame, bytesPerSample, (int)sampleRate);
                }
                WriteObu(IamfObuType.CodecConfig, cc.ToArray());
            }

            // 3. OBU_IA_AUDIO_ELEMENT (id=0, Scene-Based HOA or Channel-Based 7.1.4)
            using (MemoryStream ae = new MemoryStream(64)) {
                IamfDecoder.WriteUleb128(ae, 0); // audio_element_id
                byte typeBits = (byte)((UseSceneBasedHoa ? 1 : 0) << 5);
                ae.WriteByte(typeBits);
                IamfDecoder.WriteUleb128(ae, 0); // codec_config_id
                IamfDecoder.WriteUleb128(ae, (ulong)outputChannels); // num_substreams
                for (int s = 0; s < outputChannels; s++) {
                    IamfDecoder.WriteUleb128(ae, (ulong)s);
                }
                IamfDecoder.WriteUleb128(ae, 0); // num_parameters

                if (UseSceneBasedHoa) {
                    IamfDecoder.WriteUleb128(ae, 0); // ambisonics_mode = MONO (0) per IAMF v1.0.0 Section 3.6.2
                    ae.WriteByte((byte)outputChannels); // output_channel_count
                    ae.WriteByte((byte)outputChannels); // substream_count
                    for (int s = 0; s < outputChannels; s++) {
                        ae.WriteByte((byte)s); // channel_mapping ACN identity
                    }
                } else {
                    ae.WriteByte(1 << 5); // num_layers = 1
                    ae.WriteByte(7 << 4); // loudspeaker_layout = 7 (7.1.4)
                    ae.WriteByte((byte)outputChannels); // substream_count
                    ae.WriteByte(0); // coupled_substream_count
                }
                WriteObu(IamfObuType.AudioElement, ae.ToArray());
            }

            // 4. OBU_IA_MIX_PRESENTATION (id=0, label, 1 submix, element/output mix gain + BS.1770 Q7.8 loudness)
            using (MemoryStream mp = new MemoryStream(128)) {
                IamfDecoder.WriteUleb128(mp, 0); // mix_presentation_id
                IamfDecoder.WriteUleb128(mp, 1); // count_label
                WriteNullTerminatedUtf8(mp, "en-us");
                WriteNullTerminatedUtf8(mp, MixLabel);
                IamfDecoder.WriteUleb128(mp, 1); // num_sub_mixes
                IamfDecoder.WriteUleb128(mp, 1); // num_audio_elements
                IamfDecoder.WriteUleb128(mp, 0); // audio_element_id
                WriteNullTerminatedUtf8(mp, MixLabel);
                mp.WriteByte(0); // headphones_rendering_mode = Stereo/Binaural default
                IamfDecoder.WriteUleb128(mp, 0); // rendering_config_extension_size
                WriteMixGainParamDefinition(mp, 101u, sampleRate, 0); // element_mix_gain
                WriteMixGainParamDefinition(mp, 100u, sampleRate, 0); // output_mix_gain
                IamfDecoder.WriteUleb128(mp, 1); // num_layouts
                mp.WriteByte((2 << 6) | (7 << 2)); // sound_system = 7.1.4
                // LoudnessInfo: info_type = 0x01 (True Peak flag per IAMF v1.0.0 Sec 3.7.4), -18.0 LKFS, -1.0 dBFS, -1.0 dBTP
                mp.WriteByte(0x01);
                WriteInt16BE(mp, (short)(-18 * 256));
                WriteInt16BE(mp, (short)(-1 * 256));
                WriteInt16BE(mp, (short)(-1 * 256));
                WriteObu(IamfObuType.MixPresentation, mp.ToArray());
            }
        }

        /// <summary>
        /// Writes an AOMedia IAMF v1.0.0 <c>ipcm</c> (uncompressed signed integer PCM) <c>CODEC_CONFIG</c> OBU payload.
        /// </summary>
        public static void WriteIpcmCodecConfig(
            Stream output,
            int samplesPerFrame,
            int bytesPerSample,
            int sampleRate,
            bool littleEndian = true,
            uint codecConfigId = 0) {
            if (output == null) {
                throw new ArgumentNullException(nameof(output));
            }
            int clampedSpf = Math.Max(64, samplesPerFrame);
            int clampedBps = bytesPerSample == 2 || bytesPerSample == 4 ? bytesPerSample : 3;
            uint clampedSr = (uint)Math.Max(8000, sampleRate);
            IamfDecoder.WriteUleb128(output, codecConfigId);
            output.WriteByte(0x69); // 'i'
            output.WriteByte(0x70); // 'p'
            output.WriteByte(0x63); // 'c'
            output.WriteByte(0x6D); // 'm'
            IamfDecoder.WriteUleb128(output, (ulong)clampedSpf);
            WriteInt16BE(output, 0); // audio_roll_distance = 0
            output.WriteByte((byte)(littleEndian ? 0x01 : 0x00)); // sample_format_flags
            output.WriteByte((byte)(clampedBps * 8));
            WriteUInt32BE(output, clampedSr);
        }

        /// <summary>
        /// Writes an AOMedia IAMF v1.0.0 <c>fLaC</c> <c>CODEC_CONFIG</c> OBU payload (<c>0x80000022</c> + 34-byte <c>STREAMINFO</c>
        /// with <c>min_block_size == max_block_size == samplesPerFrame</c>).
        /// </summary>
        public static void WriteFlacCodecConfig(
            Stream output,
            int samplesPerFrame,
            int sampleRate,
            int bitsPerSample = 24,
            int substreamChannels = 1,
            ulong totalSamples = 0,
            uint codecConfigId = 0) {
            if (output == null) {
                throw new ArgumentNullException(nameof(output));
            }
            int clampedSpf = Math.Max(16, Math.Min(65535, samplesPerFrame));
            uint clampedSr = (uint)Math.Max(8000, Math.Min(655350, sampleRate));
            int clampedCh = Math.Max(1, Math.Min(8, substreamChannels));
            int clampedBps = Math.Max(4, Math.Min(32, bitsPerSample));
            IamfDecoder.WriteUleb128(output, codecConfigId);
            output.WriteByte(0x66); // 'f'
            output.WriteByte(0x4C); // 'L'
            output.WriteByte(0x61); // 'a'
            output.WriteByte(0x43); // 'C'
            IamfDecoder.WriteUleb128(output, (ulong)clampedSpf);
            WriteInt16BE(output, 0); // audio_roll_distance = 0
            WriteUInt32BE(output, 0x80000022u); // last-metadata-block=1, type=0 (STREAMINFO), len=34
            WriteUInt16BE(output, (ushort)clampedSpf); // min_block_size
            WriteUInt16BE(output, (ushort)clampedSpf); // max_block_size
            WriteUInt24BE(output, 0); // min_frame_size = unknown
            WriteUInt24BE(output, 0); // max_frame_size = unknown
            ulong packed = (((ulong)clampedSr & 0xFFFFFu) << 44) |
                (((ulong)(clampedCh - 1) & 0x7u) << 41) |
                (((ulong)(clampedBps - 1) & 0x1Fu) << 36) |
                (totalSamples & 0xFFFFFFFFFu);
            WriteUInt64BE(output, packed);
            for (int i = 0; i < 16; i++) {
                output.WriteByte(0); // MD5 signature unset
            }
        }

        /// <summary>
        /// Writes an AOMedia IAMF v1.0.0 <c>Opus</c> <c>CODEC_CONFIG</c> OBU payload with
        /// <c>audio_roll_distance = -ceil(3840 / samplesPerFrame)</c> at 48 kHz.
        /// </summary>
        public static void WriteOpusCodecConfig(
            Stream output,
            int samplesPerFrame,
            int sampleRate = 48000,
            int substreamChannels = 1,
            ushort preSkip = 312,
            uint codecConfigId = 0) {
            if (output == null) {
                throw new ArgumentNullException(nameof(output));
            }
            int clampedSpf = Math.Max(64, samplesPerFrame);
            uint clampedSr = (uint)Math.Max(8000, sampleRate);
            byte outCh = (byte)Math.Max(1, Math.Min(2, substreamChannels));
            short rollDistance = (short)(-((3840 + clampedSpf - 1) / clampedSpf));
            IamfDecoder.WriteUleb128(output, codecConfigId);
            output.WriteByte(0x4F); // 'O'
            output.WriteByte(0x70); // 'p'
            output.WriteByte(0x75); // 'u'
            output.WriteByte(0x73); // 's'
            IamfDecoder.WriteUleb128(output, (ulong)clampedSpf);
            WriteInt16BE(output, rollDistance);
            output.WriteByte(1); // version = 1
            output.WriteByte(outCh); // output_channel_count
            WriteUInt16BE(output, preSkip);
            WriteUInt32BE(output, clampedSr); // input_sample_rate
            WriteInt16BE(output, 0); // output_gain
            output.WriteByte(0); // channel_mapping_family = 0
        }

        void ApplyTruePeakHeadroom() {
            float frameTruePeak = 0f;
            for (int s = 0; s < samplesPerFrame; s++) {
                for (int c = 0; c < outputChannels; c++) {
                    float tp = truePeakDetector.ProcessSample(c, channelAccumulators[c][s]);
                    if (tp > frameTruePeak) {
                        frameTruePeak = tp;
                    }
                }
            }
            float targetScale = frameTruePeak > TruePeakCeilingLinear
                ? TruePeakCeilingLinear / frameTruePeak
                : 1f;
            if (targetScale < headroomGain) {
                headroomGain = targetScale;
            } else if (headroomGain < 1f) {
                headroomGain = Math.Min(targetScale, headroomGain + 0.1f * (1f - headroomGain));
            }
            if (headroomGain < 0.999999f) {
                for (int c = 0; c < outputChannels; c++) {
                    float[] ch = channelAccumulators[c];
                    for (int s = 0; s < samplesPerFrame; s++) {
                        ch[s] *= headroomGain;
                    }
                }
            }
        }

        static void WriteMixGainParamDefinition(Stream stream, uint paramId, uint paramRate, short defaultMixGainQ78) {
            IamfDecoder.WriteUleb128(stream, paramId);
            IamfDecoder.WriteUleb128(stream, paramRate);
            stream.WriteByte(0x80); // param_definition_mode = 1, reserved = 0
            WriteInt16BE(stream, defaultMixGainQ78);
        }

        void EncodeMonoPcm(float[] samples, byte[] dst) {
            int offset = 0;
            for (int i = 0; i < samples.Length; i++) {
                float v = samples[i];
                float abs = Math.Abs(v);
                if (abs > peakAbs) {
                    peakAbs = abs;
                }
                if (v > 1f) {
                    v = 1f;
                } else if (v < -1f) {
                    v = -1f;
                }
                if (bytesPerSample == 2) {
                    short s16 = (short)Math.Round(v * 32767f);
                    dst[offset++] = (byte)(s16 & 0xFF);
                    dst[offset++] = (byte)((s16 >> 8) & 0xFF);
                } else if (bytesPerSample == 3) {
                    int s24 = (int)Math.Round(v * 8388607f);
                    dst[offset++] = (byte)(s24 & 0xFF);
                    dst[offset++] = (byte)((s24 >> 8) & 0xFF);
                    dst[offset++] = (byte)((s24 >> 16) & 0xFF);
                } else {
                    int s32 = (int)Math.Round(v * 2147483647.0);
                    dst[offset++] = (byte)(s32 & 0xFF);
                    dst[offset++] = (byte)((s32 >> 8) & 0xFF);
                    dst[offset++] = (byte)((s32 >> 16) & 0xFF);
                    dst[offset++] = (byte)((s32 >> 24) & 0xFF);
                }
            }
        }

        void WriteObu(IamfObuType type, byte[] payload) {
            byte header = (byte)(((byte)type & 0x1F) << 3);
            writer.WriteByte(header);
            IamfDecoder.WriteUleb128(writer, (ulong)payload.Length);
            if (payload.Length > 0) {
                writer.Write(payload, 0, payload.Length);
            }
        }

        static void WriteUInt16BE(Stream stream, ushort value) {
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        static void WriteUInt24BE(Stream stream, uint value) {
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        static void WriteUInt32BE(Stream stream, uint value) {
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        static void WriteUInt64BE(Stream stream, ulong value) {
            stream.WriteByte((byte)((value >> 56) & 0xFF));
            stream.WriteByte((byte)((value >> 48) & 0xFF));
            stream.WriteByte((byte)((value >> 40) & 0xFF));
            stream.WriteByte((byte)((value >> 32) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        static void WriteInt16BE(Stream stream, short value) {
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        static void WriteNullTerminatedUtf8(Stream stream, string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }
    }
}
