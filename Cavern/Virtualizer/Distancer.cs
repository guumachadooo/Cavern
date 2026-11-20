using System;
using System.Numerics;

using Cavern.Filters;
using Cavern.Utilities;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Handles distancing calculations for a single source's two ears.
    /// </summary>
    public sealed partial class Distancer {
        /// <summary>
        /// The left ear's gain that corresponds to the <see cref="source"/>'s distance.
        /// </summary>
        public float LeftGain { get; private set; }

        /// <summary>
        /// The left ear's gain that corresponds to the <see cref="source"/>'s distance.
        /// </summary>
        public float RightGain { get; private set; }

        /// <summary>
        /// Apply physically correct gain correction. Disable this feature to be able to use a custom gain by distance.
        /// </summary>
        public bool trueGain = true;

        /// <summary>
        /// Decreases real distances by this factor to shrink the environment's scale.
        /// </summary>
        public float distanceFactor;

        /// <summary>
        /// The filtered source.
        /// </summary>
        readonly Source source;

        /// <summary>
        /// The filter processing the <see cref="source"/>.
        /// </summary>
        readonly SpikeConvolver filter;

        /// <summary>
        /// The maximum length of any of the <see cref="impulses"/>, because if the <see cref="FastConvolver"/> is used,
        /// the arrays won't be reassigned and the filter won't cut out, and if the <see cref="SpikeConvolver"/> is used,
        /// the overhead is basically zero.
        /// </summary>
        readonly int filterSize;

        /// <summary>
        /// Thread-safety lock protecting one-time expansion of static <see cref="impulses"/> with <see cref="impulseDelays"/>.
        /// </summary>
        static readonly object initLock = new object();

        /// <summary>
        /// Create a distance simulation for a <see cref="Source"/> using Cavern's official <see cref="impulses"/> tables.
        /// </summary>
        public Distancer(Source source) {
            this.source = source;

            // Add the delays to the official impulses that were removed for storage optimization (thread-safe one-time init)
            if (impulseDelays[0][0] != 0) {
                lock (initLock) {
                    if (impulseDelays[0][0] != 0) {
                        for (int i = 0; i < impulses.Length; ++i) {
                            for (int j = 0; j < impulses[i].Length; ++j) {
                                int convLength = impulses[i][j].Length;
                                short delay = impulseDelays[i][j];
                                if (delay > 0) {
                                    Array.Resize(ref impulses[i][j], convLength + delay);
                                    Array.Copy(impulses[i][j], 0, impulses[i][j], delay, convLength);
                                    Array.Clear(impulses[i][j], 0, delay);
                                }
                            }
                        }
                        impulseDelays[0][0] = 0;
                    }
                }
            }

            source.VolumeRolloff = Rolloffs.Disabled;
            for (int i = 0; i < impulses.Length; ++i) {
                for (int j = 0; j < impulses[i].Length; ++j) {
                    if (filterSize < impulses[i][j].Length) {
                        filterSize = impulses[i][j].Length;
                    }
                }
            }
            distanceFactor = Math.Max(Math.Max(Listener.EnvironmentSize.X, Listener.EnvironmentSize.Y), Listener.EnvironmentSize.Z);
            filter = new SpikeConvolver(new float[filterSize], 0);
        }

        /// <summary>
        /// Generate the left/right ear filters.
        /// </summary>
        /// <param name="right">The object is to the right of the <see cref="Listener"/>'s forward vector</param>
        /// <param name="samples">Single-channel downmixed samples to process</param>
        public void Generate(bool right, float[] samples) {
            float dirMul = -90;
            if (right) {
                dirMul = 90;
            }
            Vector3 sourceForward = new Vector3(0, dirMul, 0).RotateInverse(source.listener.Rotation).PlaceInSphere(),
                dir = source.Position - source.listener.Position;
            float distance = dir.Length(),
                rawAngle = distance < 1e-5f ? 0 :
                    MathF.Acos(Math.Clamp(Vector3.Dot(sourceForward, dir) / distance, -1f, 1f)),
                angle = rawAngle * VectorExtensions.Rad2Deg;
            distance /= distanceFactor;

            // Find bounding angles with discrete impulses
            int smallerAngle = 0;
            while (smallerAngle < angles.Length && angles[smallerAngle] < angle) {
                ++smallerAngle;
            }
            if (smallerAngle != 0) {
                --smallerAngle;
            }
            int largerAngle = smallerAngle + 1;
            if (largerAngle == angles.Length) {
                largerAngle = angles.Length - 1;
            }
            float angleRatio = Math.Min(QMath.LerpInverse(angles[smallerAngle], angles[largerAngle], angle), 1);

            // Find bounding distances with discrete impulses
            int smallerDistance = 0;
            while (smallerDistance < distances.Length && distances[smallerDistance] < distance) {
                ++smallerDistance;
            }
            if (smallerDistance != 0) {
                --smallerDistance;
            }
            int largerDistance = smallerDistance + 1;
            if (largerDistance == distances.Length) {
                largerDistance = distances.Length - 1;
            }
            float distanceRatio =
                Math.Clamp(QMath.LerpInverse(distances[smallerDistance], distances[largerDistance], distance), 0, 1);

            // Find impulse candidates and their weight from Cavern's official tables
            float[][] candidates = new float[][] {
                impulses[smallerAngle][smallerDistance],
                impulses[smallerAngle][largerDistance],
                impulses[largerAngle][smallerDistance],
                impulses[largerAngle][largerDistance]
            };
            float[] gains = {
                (float)Math.Sqrt((1 - angleRatio) * (1 - distanceRatio)),
                (float)Math.Sqrt((1 - angleRatio) * distanceRatio),
                (float)Math.Sqrt(angleRatio * (1 - distanceRatio)),
                (float)Math.Sqrt(angleRatio * distanceRatio)
            };

            // Align candidates to the dominant candidate's peak to prevent comb-filtering across different onset delays
            int dominant = 0;
            for (int c = 1; c < gains.Length; ++c) {
                if (gains[c] > gains[dominant]) {
                    dominant = c;
                }
            }
            int refPeak = FindPeakIndex(candidates[dominant]);

            Array.Clear(filter.Impulse, 0, filterSize);
            for (int candidate = 0; candidate < candidates.Length; ++candidate) {
                float g = gains[candidate];
                if (g <= 1e-5f) {
                    continue;
                }
                float[] src = candidates[candidate];
                int shift = refPeak - FindPeakIndex(src);
                for (int i = 0; i < src.Length; ++i) {
                    int dst = i + shift;
                    if (dst >= 0 && dst < filterSize) {
                        filter.Impulse[dst] += src[i] * g;
                    }
                }
            }

            // Normalize interpolated impulse to unity L2 energy so distance filtering does not boost gain by +8 to +14 dB
            double energySum = 0;
            for (int i = 0; i < filterSize; ++i) {
                double v = filter.Impulse[i];
                energySum += v * v;
            }
            if (energySum > 1e-12) {
                float invNorm = 1f / (float)Math.Sqrt(energySum);
                for (int i = 0; i < filterSize; ++i) {
                    filter.Impulse[i] *= invNorm;
                }
            }

            filter.Process(samples);

            // Apply physically derived ear gains
            LeftGain = 1;
            RightGain = 1;
            float angleDiff = MathF.Sin(rawAngle) * .097f;
            float safeDistance = MathF.Max(distance, MathF.Abs(angleDiff) + .01f);
            float ratioDiff = (safeDistance + angleDiff) * (VirtualizerFilter.ReferenceDistance - angleDiff) /
                             ((safeDistance - angleDiff) * (VirtualizerFilter.ReferenceDistance + angleDiff));
            ratioDiff *= ratioDiff;
            if (right) {
                if (ratioDiff < 1) {
                    RightGain = ratioDiff;
                } else {
                    LeftGain = 1 / ratioDiff;
                }
            } else {
                if (ratioDiff < 1) {
                    LeftGain = ratioDiff;
                } else {
                    RightGain = 1 / ratioDiff;
                }
            }

            if (!trueGain) {
                float powerOffset = 1 / MathF.Sqrt(LeftGain * LeftGain + RightGain * RightGain);
                LeftGain *= powerOffset;
                RightGain *= powerOffset;
            } else {
                float earRmsGain = MathF.Sqrt(0.5f * (LeftGain * LeftGain + RightGain * RightGain));
                WaveformUtils.Gain(samples, earRmsGain);
            }
        }

        /// <summary>
        /// Finds the index of the maximum absolute sample in an impulse response.
        /// </summary>
        static int FindPeakIndex(float[] impulse) {
            int best = 0;
            float max = 0f;
            for (int i = 0; i < impulse.Length; ++i) {
                float abs = MathF.Abs(impulse[i]);
                if (abs > max) {
                    max = abs;
                    best = i;
                }
            }
            return best;
        }
    }
}