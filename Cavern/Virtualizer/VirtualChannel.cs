using System;

using Cavern.Channels;
using Cavern.Filters;
using Cavern.Utilities;
using Cavern.Waveforms;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Represents a virtualizable channel with impulse responses for both ears.
    /// </summary>
    public struct VirtualChannel : IEquatable<VirtualChannel> {
        /// <summary>
        /// Low frequency crossover filter for retaining bass outside the usable impulse response frequency range.
        /// Null when operating in full-band mode (<c>crossoverFrequency &lt;= 0</c>).
        /// </summary>
        public Crossover Crossover { get; }

        /// <summary>
        /// Delay applied to the lowpass crossover path to phase-align bass with the main ear impulse onset.
        /// </summary>
        public Delay LowDelay { get; }

        /// <summary>
        /// Partitioned dual FFT convolution filter for both ears (supports short HRIRs and long BRIRs).
        /// </summary>
        public PartitionedDualConvolver Filter { get; }

        /// <summary>
        /// Virtual speaker angle difference from the subject's gaze on the vertical axis: elevation.
        /// </summary>
        public float x;

        /// <summary>
        /// Virtual speaker angle difference from the subject's gaze on the horizontal axis: azimuth.
        /// </summary>
        public float y;

        /// <summary>
        /// This channel should be virtualized and downmixed to stereo.
        /// </summary>
        public bool active;

        /// <summary>
        /// This virtual channel represents a dedicated Low-Frequency Effects (LFE) subwoofer channel.
        /// </summary>
        public bool isLfe;

        /// <summary>
        /// When <see cref="isLfe"/> is true and <see cref="active"/> is false, equal-power mix into stereo L/R.
        /// </summary>
        public bool mixLfeToStereo;

        /// <summary>
        /// Constructs a virtual channel with disabled virtualization.
        /// </summary>
        public VirtualChannel(float x, float y) : this(x, y, false, false) { }

        /// <summary>
        /// Constructs a pass-through virtual channel (used for physical ground speakers or dedicated LFE).
        /// </summary>
        public VirtualChannel(float x, float y, bool isLfe, bool mixLfeToStereo = false) {
            this.x = x;
            this.y = y;
            active = false;
            this.isLfe = isLfe;
            this.mixLfeToStereo = mixLfeToStereo;
            Crossover = null;
            LowDelay = null;
            Filter = null;
        }

        /// <summary>
        /// Constructs a virtualizable channel with impulse responses for both ears and geometric secondary-ear delay.
        /// </summary>
        public VirtualChannel(float x, float y, float[] leftEarIR, float[] rightEarIR, int sampleRate, float crossoverFrequency)
            : this(x, y, leftEarIR, rightEarIR, sampleRate, crossoverFrequency,
                y % 180 > 0 ? GetDelay(y % 180) : 0, y < 0 ? GetDelay(-y) : 0) { }

        /// <summary>
        /// Internal constructor supporting explicit delays (0, 0 for laboratory-measured HRIR/BRIRs with baked-in ITD).
        /// </summary>
        VirtualChannel(float x, float y, float[] leftEarIR, float[] rightEarIR, int sampleRate,
            float crossoverFrequency, int delay1, int delay2) {
            this.x = x;
            this.y = y;
            active = true;
            isLfe = false;
            mixLfeToStereo = false;
            if (crossoverFrequency > 0) {
                Crossover = new Crossover(sampleRate, crossoverFrequency);
                int peakL = GetPeakIndex(leftEarIR) + delay1;
                int peakR = GetPeakIndex(rightEarIR) + delay2;
                int mainOnset = Math.Min(peakL, peakR);
                LowDelay = mainOnset > 0 ? new Delay((float)mainOnset / sampleRate, sampleRate) : null;
            } else {
                Crossover = null;
                LowDelay = null;
            }
            Filter = new PartitionedDualConvolver(leftEarIR, rightEarIR, delay1, delay2);
        }

        /// <summary>
        /// Creates a dedicated LFE virtual channel without HRIR convolution (clean 120 Hz lowpass path via <see cref="Listener"/>).
        /// </summary>
        public static VirtualChannel CreateLFE(bool mixToStereo = true) => new VirtualChannel(0, 0, true, mixToStereo);

        /// <summary>
        /// Constructs a virtualizable channel from real laboratory-measured HRIR/BRIR impulse responses,
        /// preserving exact measured ITD, ILD, and full-band phase without synthetic delays or artificial EQ.
        /// </summary>
        public static VirtualChannel FromMeasured(float x, float y, float[] leftEarIR, float[] rightEarIR,
            int sampleRate, float crossoverFrequency = 0) =>
            new VirtualChannel(x, y, leftEarIR, rightEarIR, sampleRate, crossoverFrequency, 0, 0);

        /// <summary>
        /// Constructs a virtualizable channel with impulse responses for both ears, recalibrating gains by the position's angles
        /// using true L2 impulse energy normalization so high frequencies match the unity-gain lowpass path.
        /// </summary>
        public static VirtualChannel FromSubparMeasurement(float x, float y, float[] leftEarIR, float[] rightEarIR,
            int sampleRate, float crossoverFrequency) {
            float[] leftCopy = (float[])leftEarIR.Clone();
            float[] rightCopy = (float[])rightEarIR.Clone();
            if (y == 0 || y == 180) { // Middle: equal power -3 dB per ear (L^2 + R^2 = 1.0)
                float norm = GetEnergyNorm(leftCopy);
                if (norm > 1e-6f) {
                    WaveformUtils.Gain(leftCopy, minus3dB / norm);
                }
                Array.Copy(leftCopy, rightCopy, leftCopy.Length);
            } else {
                float ratio = QMath.DbToGain(-20 * MathF.Sin(MathF.Abs(y) * VectorExtensions.Deg2Rad));
                float mainGain = 1f / MathF.Sqrt(1f + ratio * ratio);
                float otherGain = mainGain * ratio;
                float leftNorm = GetEnergyNorm(leftCopy);
                float rightNorm = GetEnergyNorm(rightCopy);
                if (y < 0) { // Left side
                    if (leftNorm > 1e-6f) {
                        WaveformUtils.Gain(leftCopy, mainGain / leftNorm);
                    }
                    if (rightNorm > 1e-6f) {
                        WaveformUtils.Gain(rightCopy, otherGain / rightNorm);
                    }
                } else { // Right side
                    if (leftNorm > 1e-6f) {
                        WaveformUtils.Gain(leftCopy, otherGain / leftNorm);
                    }
                    if (rightNorm > 1e-6f) {
                        WaveformUtils.Gain(rightCopy, mainGain / rightNorm);
                    }
                }
            }
            return new VirtualChannel(x, y, leftCopy, rightCopy, sampleRate, crossoverFrequency);
        }

        /// <summary>
        /// Parse the virtual channels from a multichannel HRIR set in full-band mode (unity global L2 energy).
        /// </summary>
        public static VirtualChannel[] Parse(MultichannelWaveform hrir, int sampleRate) => Parse(hrir, sampleRate, 0);

        /// <summary>
        /// Parse the virtual channels from a multichannel HRIR set, optionally crossing over at a set frequency.
        /// </summary>
        public static VirtualChannel[] Parse(MultichannelWaveform hrir, int sampleRate, float crossoverFrequency) {
            hrir.TrimStart();
            hrir.Normalize();

            // L/R impulses of all channels in line
            if (hrir.Channels == 2) {
                int channelCount = 0,
                    lPeak = -1,
                    rPeak = -1;
                float[] l = hrir[0], r = hrir[1];
                for (int i = 0; i < l.Length; i++) {
                    if (MathF.Abs(l[i]) > .5f || MathF.Abs(r[i]) > .5f) {
                        ++channelCount;
                        if (lPeak == -1) {
                            lPeak = i;
                        } else if (rPeak == -1) {
                            rPeak = i;
                        }
                        i += 64; // Impulse responses can't be shorter than this, and this large peaks never happen after 64 samples
                    }
                }

                // Preprocessing
                int step = rPeak > lPeak ? rPeak - lPeak : l.Length;
                MultichannelWaveform[] splits = hrir.Split(step);
                int lastToKeep = splits.Length;
                while (lastToKeep > 1 && splits[lastToKeep - 1].IsMute()) {
                    lastToKeep--;
                }
                if (splits.Length != lastToKeep) {
                    splits = splits[..lastToKeep];
                }
                for (int i = 0; i < splits.Length; i++) {
                    splits[i].TrimEnd();
                }

                // If there's an odd number of channels (>= 3), duplicate center to keep standard matrix mapping aligned
                if (splits.Length % 2 == 1 && splits.Length >= 3) {
                    MultichannelWaveform[] newSplits = new MultichannelWaveform[splits.Length + 1];
                    Array.Copy(splits, newSplits, 3);
                    newSplits[3] = newSplits[2];
                    Array.Copy(splits, 3, newSplits, 4, splits.Length - 3);
                    splits = newSplits;
                }

                // Global single-scalar L2 energy normalization across non-LFE channels to preserve measured ILD & channel balance
                ReferenceChannel[] channels = ChannelPrototype.GetStandardMatrix(splits.Length);
                double energySum = 0;
                int energyCount = 0;
                for (int i = 0; i < splits.Length; i++) {
                    if (!ChannelPrototype.Mapping[(int)channels[i]].LFE) {
                        float nl = GetEnergyNorm(splits[i][0]);
                        float nr = GetEnergyNorm(splits[i][1]);
                        energySum += nl * nl + nr * nr;
                        energyCount++;
                    }
                }
                float globalScale = energyCount > 0 && energySum > 1e-12 ? 1f / (float)Math.Sqrt(energySum / energyCount) : 1f;

                VirtualChannel[] result = new VirtualChannel[splits.Length];
                for (int i = 0; i < result.Length; i++) {
                    ChannelPrototype prototype = ChannelPrototype.Mapping[(int)channels[i]];
                    if (prototype.LFE) {
                        result[i] = CreateLFE(true);
                    } else {
                        float[] leftIr = (float[])splits[i][0].Clone();
                        float[] rightIr = (float[])splits[i][1].Clone();
                        WaveformUtils.Gain(leftIr, globalScale);
                        WaveformUtils.Gain(rightIr, globalScale);
                        result[i] = FromMeasured(prototype.X, prototype.Y, leftIr, rightIr, sampleRate, crossoverFrequency);
                    }
                }
                return result;
            }

            // Every channel is a single impulse
            else {
                throw new NotSupportedException("Non-stereo HRIR file");
            }
        }

        /// <summary>
        /// Check if the two virtual channels handle the same source channel position and LFE role.
        /// </summary>
        public readonly bool Equals(VirtualChannel other) => x == other.x && y == other.y && isLfe == other.isLfe;

        /// <summary>
        /// Computes the L2 impulse energy norm <c>sqrt(sum(h[n]^2))</c> (true wideband convolution gain, independent of tap length).
        /// </summary>
        static float GetEnergyNorm(float[] impulse) {
            double sum = 0;
            for (int i = 0; i < impulse.Length; i++) {
                double v = impulse[i];
                sum += v * v;
            }
            return (float)Math.Sqrt(sum);
        }

        /// <summary>
        /// Returns the sample index of the maximum absolute peak in an impulse response.
        /// </summary>
        static int GetPeakIndex(float[] impulse) {
            int best = 0;
            float max = 0f;
            for (int i = 0; i < impulse.Length; i++) {
                float abs = MathF.Abs(impulse[i]);
                if (abs > max) {
                    max = abs;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Get the secondary ear's delay by angle of attack.
        /// </summary>
        /// <remarks>This formula is based on measurements and the sine wave's usability was disproven.
        /// See Bence S. (2022). Extending HRTF with distance simulation based on ray-tracing.</remarks>
        static int GetDelay(float angle) => (int)((90 - Math.Abs(angle - 90)) / 2.7f);

        /// <summary>
        /// Equal-power -3 dB voltage gain (<c>1 / sqrt(2)</c>) for centerline and LFE stereo downmix.
        /// </summary>
        internal const float minus3dB = 0.70710678118f;
    }
}
