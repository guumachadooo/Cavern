using System;
using System.IO;
using System.Numerics;

using Cavern.Format.Renderers;

namespace Cavern.Format.Environment {
    /// <summary>
    /// Exports a Cavern <see cref="Listener"/> 3D audio scene to Higher-Order Ambisonics (HOA) B-Format (ACN/SN3D,
    /// orders 1..3 = 4, 9, or 16 channels) inside a standards-compliant <c>WAVEFORMATEXTENSIBLE</c> Ambisonic WAV/AMB file
    /// (<c>SUBTYPE_AMBISONIC_B_FORMAT_PCM</c> / <c>SUBTYPE_AMBISONIC_B_FORMAT_IEEE_FLOAT</c>).
    /// </summary>
    public class AmbisonicsHoaWriter : EnvironmentWriter {
        /// <summary>Ambisonics spherical harmonic order N (1..3, default 3 = 16 channels).</summary>
        public int Order { get; }

        /// <summary>Total spherical harmonic channel count <c>(N + 1)^2</c>.</summary>
        public int HarmonicCount { get; }

        readonly RIFFWaveWriter waveWriter;
        readonly double[] shBuffer;
        readonly float[] interleavedBuffer;
        readonly int updateRate;

        /// <summary>
        /// Creates a 3rd-order (default) ACN/SN3D Ambisonics B-Format file writer at <paramref name="path"/>.
        /// </summary>
        public AmbisonicsHoaWriter(string path, Listener source, long length, BitDepth bits, int order = 3)
            : this(AudioWriter.Open(path), source, length, bits, order) { }

        /// <summary>
        /// Creates an ACN/SN3D Ambisonics B-Format stream writer.
        /// </summary>
        public AmbisonicsHoaWriter(Stream writer, Listener source, long length, BitDepth bits, int order = 3)
            : base(writer, source, length, bits) {
            Order = Math.Max(1, Math.Min(3, order));
            HarmonicCount = (Order + 1) * (Order + 1);
            updateRate = Math.Max(64, source.UpdateRate);
            shBuffer = new double[HarmonicCount];
            interleavedBuffer = new float[updateRate * HarmonicCount];
            waveWriter = new RIFFWaveWriter(writer, HarmonicCount, length, source.SampleRate, bits) {
                UseAmbisonicSubFormatGuid = true
            };
            waveWriter.WriteHeader();
        }

        /// <inheritdoc/>
        public override void WriteNextFrame() {
            Source.Ping();
            Array.Clear(interleavedBuffer, 0, interleavedBuffer.Length);

            Vector3 invEnv = new Vector3(
                Listener.EnvironmentSize.X > 1e-4f ? 1f / Listener.EnvironmentSize.X : 1f,
                Listener.EnvironmentSize.Y > 1e-4f ? 1f / Listener.EnvironmentSize.Y : 1f,
                Listener.EnvironmentSize.Z > 1e-4f ? 1f / Listener.EnvironmentSize.Z : 1f);

            int activeCount = Math.Max(1, Source.ActiveSources.Count);
            float headroomNorm = 1f / (float)Math.Sqrt(activeCount);

            foreach (Source src in Source.ActiveSources) {
                float[] rendered = src.Rendered != null && src.Rendered.Length > 0 ? src.Rendered[0] : null;
                if (rendered == null) {
                    continue;
                }
                int frames = Math.Min(updateRate, rendered.Length);
                if (src.LFE) {
                    // LFE is omnidirectional W-channel (ACN 0) energy without fake directional bias
                    for (int f = 0; f < frames; f++) {
                        interleavedBuffer[f * HarmonicCount] += rendered[f] * headroomNorm;
                    }
                    continue;
                }

                Vector3 rel = (src.Position - Source.Position) * invEnv;
                AmbisonicsHoaSpatialDecoder.EncodeDirectionSn3d(rel, Order, shBuffer);
                for (int f = 0; f < frames; f++) {
                    float sample = rendered[f] * headroomNorm;
                    int baseIdx = f * HarmonicCount;
                    for (int q = 0; q < HarmonicCount; q++) {
                        interleavedBuffer[baseIdx + q] += sample * (float)shBuffer[q];
                    }
                }
            }

            waveWriter.WriteBlock(interleavedBuffer, 0, interleavedBuffer.Length);
        }

        /// <inheritdoc/>
        public override void Dispose() {
            waveWriter?.Dispose();
            base.Dispose();
        }
    }
}
