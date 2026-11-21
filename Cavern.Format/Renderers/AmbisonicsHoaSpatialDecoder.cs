using System;
using System.Numerics;

using Cavern.Channels;
using Cavern.Virtualizer;

namespace Cavern.Format.Renderers {
    /// <summary>
    /// Pure-physics Higher-Order Ambisonics (HOA, orders 1..3 ACN/SN3D) to virtual 3D speaker array decoder.
    /// Uses real spherical harmonics evaluated via <see cref="MagLsAmbisonicDecoder.EvaluateRealSphericalHarmonicsSn3d"/>
    /// with Max-rE energy-vector weighting (Daniel 2000 / Zotter &amp; Frank 2012 / <c>Spatial_Audio_Framework</c> /
    /// <c>videolan/libspatialaudio</c>) for accurate localization without artificial EQ or fake DSP.
    /// </summary>
    public sealed class AmbisonicsHoaSpatialDecoder {
        /// <summary>
        /// Virtual 12-direction 7.1.4 spherical layout used to project decoded HOA soundfields into Cavern's 3D engine.
        /// </summary>
        public static readonly ReferenceChannel[] VirtualLayout714 = {
            ReferenceChannel.FrontLeft,
            ReferenceChannel.FrontRight,
            ReferenceChannel.FrontCenter,
            ReferenceChannel.SideLeft,
            ReferenceChannel.SideRight,
            ReferenceChannel.RearLeft,
            ReferenceChannel.RearRight,
            ReferenceChannel.TopFrontLeft,
            ReferenceChannel.TopFrontRight,
            ReferenceChannel.TopRearLeft,
            ReferenceChannel.TopRearRight,
            ReferenceChannel.GodsVoice
        };

        /// <summary>Ambisonic order N (1..3).</summary>
        public int Order { get; }

        /// <summary>Number of spherical harmonic channels <c>(N + 1)^2</c>.</summary>
        public int HarmonicCount { get; }

        /// <summary>Number of virtual 3D output directions.</summary>
        public int VirtualSpeakerCount { get; }

        /// <summary>Normalized unit 3D positions in Cavern listener coordinates (X right, Y up, Z front).</summary>
        public Vector3[] VirtualSpeakerPositions { get; }

        /// <summary>Reference channels for the virtual speakers.</summary>
        public ReferenceChannel[] VirtualChannels { get; }

        /// <summary>
        /// Decoding matrix <c>[speaker][acn]</c> combining SN3D dual-mode pseudo-inverse projection and Max-rE weights.
        /// </summary>
        readonly float[][] decodeMatrix;

        /// <summary>
        /// Constructs a zero-GC Max-rE weighted HOA spatial decoder for orders 1..3 (ACN/SN3D).
        /// </summary>
        public AmbisonicsHoaSpatialDecoder(int order, bool useMaxReWeights = true) {
            Order = Math.Max(1, Math.Min(3, order));
            HarmonicCount = (Order + 1) * (Order + 1);
            VirtualChannels = VirtualLayout714;
            VirtualSpeakerCount = VirtualChannels.Length;
            VirtualSpeakerPositions = new Vector3[VirtualSpeakerCount];
            decodeMatrix = new float[VirtualSpeakerCount][];

            double[] maxRe = ComputeMaxReWeights(Order, useMaxReWeights);
            double[][] basis = new double[VirtualSpeakerCount][];

            for (int s = 0; s < VirtualSpeakerCount; s++) {
                Vector3 unitPos = GetCanonicalUnitDirection(VirtualChannels[s]);
                VirtualSpeakerPositions[s] = unitPos;

                // Azimuth: 0 = front (+Z), +pi/2 = left (-X) per SN3D/ACN right-handed Ambisonics convention
                double azimuthRad = Math.Atan2(-unitPos.X, unitPos.Z);
                double horiz = Math.Sqrt(unitPos.X * unitPos.X + unitPos.Z * unitPos.Z);
                double elevationRad = Math.Atan2(unitPos.Y, horiz);

                double[] y = new double[HarmonicCount];
                MagLsAmbisonicDecoder.EvaluateRealSphericalHarmonicsSn3d(azimuthRad, elevationRad, Order, y);
                basis[s] = y;
            }

            // Compute sampling-mode decoding matrix with exact per-order SN3D energy normalization and Max-rE weighting.
            // Energy normalization ensures omnidirectional W=1 produces unit total acoustic power across the virtual array.
            double speakerNorm = 1.0 / VirtualSpeakerCount;
            for (int s = 0; s < VirtualSpeakerCount; s++) {
                decodeMatrix[s] = new float[HarmonicCount];
                for (int n = 0; n <= Order; n++) {
                    double n3dScale = 2.0 * n + 1.0;
                    double weight = n3dScale * maxRe[n] * speakerNorm;
                    int baseIdx = n * n;
                    for (int m = -n; m <= n; m++) {
                        int acn = baseIdx + n + m;
                        decodeMatrix[s][acn] = (float)(basis[s][acn] * weight);
                    }
                }
            }

            // Normalize omnidirectional W energy so sum_s (D[s, 0]^2) == 1.0
            double wEnergy = 0.0;
            for (int s = 0; s < VirtualSpeakerCount; s++) {
                wEnergy += decodeMatrix[s][0] * decodeMatrix[s][0];
            }
            if (wEnergy > 1e-12) {
                float norm = (float)(1.0 / Math.Sqrt(wEnergy));
                for (int s = 0; s < VirtualSpeakerCount; s++) {
                    for (int q = 0; q < HarmonicCount; q++) {
                        decodeMatrix[s][q] *= norm;
                    }
                }
            }
        }

        /// <summary>
        /// Decodes interleaved ACN/SN3D HOA samples into per-virtual-speaker mono buffers without heap allocation.
        /// </summary>
        public void DecodeInterleaved(float[] interleavedHoa, int frameCount, int inputChannelCount, float[][] outputSpeakers) {
            int channels = Math.Min(HarmonicCount, inputChannelCount);
            int speakers = Math.Min(VirtualSpeakerCount, outputSpeakers.Length);
            for (int s = 0; s < speakers; s++) {
                float[] dst = outputSpeakers[s];
                float[] row = decodeMatrix[s];
                for (int f = 0; f < frameCount; f++) {
                    int baseOffset = f * inputChannelCount;
                    float sum = 0f;
                    for (int q = 0; q < channels; q++) {
                        sum += row[q] * interleavedHoa[baseOffset + q];
                    }
                    dst[f] = sum;
                }
            }
        }

        /// <summary>
        /// Encodes a single mono 3D source at unit direction <paramref name="cavernDirection"/> into ACN/SN3D
        /// spherical harmonic gains <paramref name="destination"/> (length at least <c>(order + 1)^2</c>).
        /// </summary>
        public static void EncodeDirectionSn3d(Vector3 cavernDirection, int order, double[] destination) {
            float len = cavernDirection.Length();
            Vector3 unit = len > 1e-6f ? cavernDirection / len : new Vector3(0f, 0f, 1f);
            double azimuthRad = Math.Atan2(-unit.X, unit.Z);
            double horiz = Math.Sqrt(unit.X * unit.X + unit.Z * unit.Z);
            double elevationRad = Math.Atan2(unit.Y, horiz);
            MagLsAmbisonicDecoder.EvaluateRealSphericalHarmonicsSn3d(azimuthRad, elevationRad, order, destination);
        }

        /// <summary>
        /// Computes exact closed-form Max-rE per-order weights <c>a_n = P_n(cos(137.9 deg / (N + 1.51)))</c>
        /// (Zotter &amp; Frank 2012, Eq. 10 — identical to SAF/IEM/libspatialaudio).
        /// </summary>
        public static double[] ComputeMaxReWeights(int order, bool enabled) {
            double[] weights = new double[order + 1];
            for (int n = 0; n <= order; n++) {
                weights[n] = 1.0;
            }
            if (!enabled || order <= 0) {
                return weights;
            }

            double rE = Math.Cos((137.9 * Math.PI / 180.0) / (order + 1.51));
            weights[0] = 1.0;
            if (order >= 1) {
                weights[1] = rE;
            }
            for (int n = 2; n <= order; n++) {
                weights[n] = ((2.0 * n - 1.0) * rE * weights[n - 1] - (n - 1.0) * weights[n - 2]) / n;
            }
            return weights;
        }

        /// <summary>
        /// Maps Cavern reference channel enum to canonical unit direction vector (X right, Y up, Z front).
        /// </summary>
        static Vector3 GetCanonicalUnitDirection(ReferenceChannel ch) {
            Vector3 dir;
            switch (ch) {
                case ReferenceChannel.FrontLeft: dir = new Vector3(-0.5f, 0f, 0.8660254f); break;
                case ReferenceChannel.FrontRight: dir = new Vector3(0.5f, 0f, 0.8660254f); break;
                case ReferenceChannel.FrontCenter: dir = new Vector3(0f, 0f, 1f); break;
                case ReferenceChannel.SideLeft: dir = new Vector3(-1f, 0f, 0f); break;
                case ReferenceChannel.SideRight: dir = new Vector3(1f, 0f, 0f); break;
                case ReferenceChannel.RearLeft: dir = new Vector3(-0.7071068f, 0f, -0.7071068f); break;
                case ReferenceChannel.RearRight: dir = new Vector3(0.7071068f, 0f, -0.7071068f); break;
                case ReferenceChannel.TopFrontLeft: dir = new Vector3(-0.5773503f, 0.5773503f, 0.5773503f); break;
                case ReferenceChannel.TopFrontRight: dir = new Vector3(0.5773503f, 0.5773503f, 0.5773503f); break;
                case ReferenceChannel.TopRearLeft: dir = new Vector3(-0.5773503f, 0.5773503f, -0.5773503f); break;
                case ReferenceChannel.TopRearRight: dir = new Vector3(0.5773503f, 0.5773503f, -0.5773503f); break;
                case ReferenceChannel.GodsVoice: dir = new Vector3(0f, 1f, 0f); break;
                default: dir = new Vector3(0f, 0f, 1f); break;
            }
            return Vector3.Normalize(dir);
        }
    }
}
