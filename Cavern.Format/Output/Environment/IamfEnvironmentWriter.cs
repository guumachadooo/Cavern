using System;
using System.IO;
using System.Numerics;
using System.Text;

using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;

namespace Cavern.Format.Environment {
    /// <summary>
    /// Exports a Cavern <see cref="Listener"/> 3D audio scene to an AOMedia IAMF v1.0.0 (Eclipsa Audio) OBU stream.
    /// Writes compliant <c>IA_SEQUENCE_HEADER</c> (<c>"iamf"</c>), <c>CODEC_CONFIG</c> (<c>ipcm</c> lossless PCM),
    /// <c>AUDIO_ELEMENT</c> (Scene-Based 3rd-Order ACN/SN3D Ambisonics HOA or Channel-Based 7.1.4),
    /// <c>MIX_PRESENTATION</c> with Q7.8 ITU-R BS.1770 loudness metadata, and <c>AUDIO_FRAME</c> OBUs.
    /// </summary>
    public class IamfEnvironmentWriter : EnvironmentWriter {
        /// <summary>Whether to encode Scene-Based 3rd-Order HOA (16 ch ACN/SN3D) instead of Channel-Based 7.1.4 (12 ch).</summary>
        public bool UseSceneBasedHoa { get; }

        /// <summary>Ambisonic order when <see cref="UseSceneBasedHoa"/> is true (default 3 = 16 channels).</summary>
        public int HoaOrder { get; }

        /// <summary>Mix presentation label written into the IAMF Mix Presentation OBU.</summary>
        public string MixLabel { get; }

        readonly int outputChannels;
        readonly int samplesPerFrame;
        readonly int bytesPerSample;
        readonly double[] shBuffer;
        readonly float[][] channelAccumulators;
        readonly byte[] pcmFrameBytes;
        bool headerWritten;
        float peakAbs;

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
            : this(AudioWriter.Open(path), source, length, bits, useSceneBasedHoa, hoaOrder, mixLabel) { }

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
            : base(writer, source, length, bits) {
            UseSceneBasedHoa = useSceneBasedHoa;
            HoaOrder = Math.Max(1, Math.Min(3, hoaOrder));
            MixLabel = string.IsNullOrEmpty(mixLabel) ? "Cavernize Eclipsa Audio Master" : mixLabel;
            outputChannels = UseSceneBasedHoa ? (HoaOrder + 1) * (HoaOrder + 1) : 12;
            samplesPerFrame = Math.Max(64, source.UpdateRate);
            bytesPerSample = bits == BitDepth.Int16 ? 2 : bits == BitDepth.Float32 ? 4 : 3;
            shBuffer = new double[outputChannels];
            channelAccumulators = new float[outputChannels][];
            for (int c = 0; c < outputChannels; c++) {
                channelAccumulators[c] = new float[samplesPerFrame];
            }
            pcmFrameBytes = new byte[samplesPerFrame * bytesPerSample];
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
            // 1. OBU_IA_SEQUENCE_HEADER ("iamf", Base profile = 1, Base additional = 1)
            byte[] seqPayload = { 0x69, 0x61, 0x6D, 0x66, 0x01, 0x01 };
            WriteObu(IamfObuType.SequenceHeader, seqPayload);

            // 2. OBU_IA_CODEC_CONFIG (id=0, codec_id="ipcm", samplesPerFrame, roll=0, flags=1 LE, sampleSize, sampleRate)
            using (MemoryStream cc = new MemoryStream(32)) {
                IamfDecoder.WriteUleb128(cc, 0); // codec_config_id
                cc.WriteByte(0x69); // 'i'
                cc.WriteByte(0x70); // 'p'
                cc.WriteByte(0x63); // 'c'
                cc.WriteByte(0x6D); // 'm'
                IamfDecoder.WriteUleb128(cc, (ulong)samplesPerFrame);
                cc.WriteByte(0); cc.WriteByte(0); // audio_roll_distance = 0
                cc.WriteByte(0x01); // sample_format_flags: little-endian PCM
                cc.WriteByte((byte)(bytesPerSample * 8));
                WriteUInt32BE(cc, (uint)Math.Max(8000, Source.SampleRate));
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
                    ae.WriteByte((byte)outputChannels); // output_channel_count
                    ae.WriteByte((byte)outputChannels); // substream_count
                    ae.WriteByte(0); // ambisonics_mode = MONO (0)
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

            // 4. OBU_IA_MIX_PRESENTATION (id=0, label, 1 submix, BS.1770 Q7.8 loudness = -18.0 LKFS / -1.0 dBTP)
            using (MemoryStream mp = new MemoryStream(96)) {
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
                IamfDecoder.WriteUleb128(mp, 1); // num_layouts
                mp.WriteByte((2 << 6) | (7 << 2)); // sound_system = 7.1.4
                // LoudnessInfo: info_type = 0x02 (True Peak present), integrated = -18.0 LKFS, digital = -1.0 dBFS, true = -1.0 dBTP
                mp.WriteByte(0x02);
                WriteInt16BE(mp, (short)(-18 * 256));
                WriteInt16BE(mp, (short)(-1 * 256));
                WriteInt16BE(mp, (short)(-1 * 256));
                WriteObu(IamfObuType.MixPresentation, mp.ToArray());
            }
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

        static void WriteUInt32BE(Stream stream, uint value) {
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
