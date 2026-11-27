using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Cavern.Restoration;
using Cavern.Utilities.Enums;

namespace Cavern.Utilities {
    /// <summary>
    /// Zero-allocation stateful polyphase Kaiser-windowed sinc resampler for real-time audio streams.
    /// Uses 64 taps per branch across 512 interpolated sub-phases (<c>&gt;= 90 dB</c> stopband rejection).
    /// </summary>
    public sealed class PolyphaseSincResampler {
        /// <summary>Kaiser window shape parameter achieving ~90.4 dB stopband attenuation within 64 taps.</summary>
        public const double KaiserBeta = 9.0;

        /// <summary>FIR taps per polyphase branch.</summary>
        public const int TapsPerPhase = KaiserSincResampler.TapsPerPhase;

        /// <summary>Number of discrete fractional sub-phases in [0; 1) with linear inter-phase interpolation.</summary>
        public const int NumPhases = KaiserSincResampler.NumPhases;

        const int HalfTaps = TapsPerPhase / 2;
        const int PreFill = HalfTaps * 2 + 2;

        static readonly ConcurrentDictionary<int, float[]> PolyphaseTables = new ConcurrentDictionary<int, float[]>();

        float[] buffer;
        float[] activeTable;
        double readPos;
        double lastStepRatio;
        int writeCount;

        /// <summary>
        /// Create a stateful polyphase Kaiser-windowed sinc resampler with pre-allocated sliding history.
        /// </summary>
        /// <param name="initialCapacity">Initial history buffer capacity in samples</param>
        public PolyphaseSincResampler(int initialCapacity = 4096) {
            buffer = new float[Math.Max(PreFill + 256, initialCapacity)];
            Reset();
        }

        /// <summary>
        /// Clear sliding history and reset the fractional sample read cursor.
        /// </summary>
        public void Reset() {
            Array.Clear(buffer, 0, Math.Min(buffer.Length, PreFill));
            readPos = HalfTaps - 1;
            writeCount = PreFill;
        }

        /// <summary>
        /// Resample <paramref name="inputLength"/> samples from <paramref name="input"/> into
        /// <paramref name="outputLength"/> samples of <paramref name="output"/> with zero steady-state heap allocations.
        /// </summary>
        public void Process(float[] input, int inputLength, float[] output, int outputLength, double stepRatio) {
            if (input == null || output == null || inputLength <= 0 || outputLength <= 0) {
                return;
            }
            if (stepRatio <= 0.0 || double.IsNaN(stepRatio) || double.IsInfinity(stepRatio)) {
                stepRatio = (double)inputLength / outputLength;
            }

            if (activeTable == null || Math.Abs(stepRatio - lastStepRatio) > 1e-6) {
                activeTable = GetPolyphaseTable(stepRatio);
                lastStepRatio = stepRatio;
            }

            int required = writeCount + inputLength;
            if (required > buffer.Length) {
                int newCap = Math.Max(buffer.Length * 2, required + PreFill);
                float[] grown = new float[newCap];
                Array.Copy(buffer, 0, grown, 0, writeCount);
                buffer = grown;
            }

            Array.Copy(input, 0, buffer, writeCount, inputLength);
            writeCount += inputLength;

            float[] table = activeTable;
            double pos = readPos;
            for (int o = 0; o < outputLength; o++) {
                output[o] = (float)EvaluateTapSum(buffer, writeCount, pos, table);
                pos += stepRatio;
            }

            int discard = (int)Math.Floor(pos) - (HalfTaps - 1);
            if (discard > 0) {
                if (discard > writeCount) {
                    discard = writeCount;
                }
                int keep = writeCount - discard;
                if (keep > 0) {
                    Array.Copy(buffer, discard, buffer, 0, keep);
                }
                writeCount = keep;
                pos -= discard;
            }
            readPos = pos;
        }

        /// <summary>
        /// Stateless single-shot polyphase Kaiser-sinc evaluation into <paramref name="output"/>.
        /// </summary>
        internal static void EvaluateStatic(float[] input, int inputLength, float[] output, int outputLength) {
            if (inputLength <= 0 || outputLength <= 0) {
                return;
            }
            double stepRatio = (double)inputLength / outputLength;
            float[] table = GetPolyphaseTable(stepRatio);
            for (int o = 0; o < outputLength; o++) {
                output[o] = (float)EvaluateTapSum(input, inputLength, o * stepRatio, table);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double EvaluateTapSum(float[] samples, int validLength, double pos, float[] table) {
            int baseIdx = (int)Math.Floor(pos);
            double frac = pos - baseIdx;

            double phaseScaled = frac * NumPhases;
            int p0 = (int)phaseScaled;
            if (p0 >= NumPhases) {
                p0 = NumPhases - 1;
            }
            float alpha = (float)(phaseScaled - p0);
            float oneMinusAlpha = 1f - alpha;
            int row0 = p0 * TapsPerPhase;
            int row1 = row0 + TapsPerPhase;
            int startSample = baseIdx - HalfTaps + 1;
            double acc = 0.0;

            if (startSample >= 0 && startSample + TapsPerPhase <= validLength) {
                for (int t = 0; t < TapsPerPhase; t++) {
                    float coeff = table[row0 + t] * oneMinusAlpha + table[row1 + t] * alpha;
                    acc += samples[startSample + t] * coeff;
                }
            } else {
                for (int t = 0; t < TapsPerPhase; t++) {
                    int idx = startSample + t;
                    if (idx >= 0 && idx < validLength) {
                        float coeff = table[row0 + t] * oneMinusAlpha + table[row1 + t] * alpha;
                        acc += samples[idx] * coeff;
                    }
                }
            }
            return acc;
        }

        static float[] GetPolyphaseTable(double stepRatio) {
            double outOverIn = stepRatio > 0.0 ? 1.0 / stepRatio : 1.0;
            double rawCutoff = outOverIn < 1.0 ? 0.89 * outOverIn : 0.905;
            int key = Math.Clamp((int)Math.Floor(rawCutoff * 200.0), 10, 184);
            return PolyphaseTables.GetOrAdd(key, k => KaiserSincResampler.BuildPolyphaseTable(k / 200.0, KaiserBeta));
        }
    }

    /// <summary>
    /// Audio resampling functions.
    /// </summary>
    public static class Resample {
        /// <summary>
        /// Resamples a single channel with the quality set by the user.
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <param name="quality">Listener audio quality</param>
        /// <returns>Resampled version of the input channel</returns>
        public static float[] Adaptive(float[] samples, int to, QualityModes quality) {
            if (quality < QualityModes.High) {
                return NearestNeighbour(samples, to);
            } else if (quality < QualityModes.Perfect) {
                return Lerp(samples, to);
            } else {
                return CatmullRom(samples, to);
            }
        }

        /// <summary>
        /// Resamples a single channel with a fixed algorithm.
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <param name="algorithm">Resampling algorithm to use</param>
        /// <returns>Resampled version of the input channel</returns>
        public static float[] Fixed(float[] samples, int to, ResampleAlgorithm algorithm) {
            return algorithm switch {
                ResampleAlgorithm.NearestNeighbour => NearestNeighbour(samples, to),
                ResampleAlgorithm.Lerp => Lerp(samples, to),
                ResampleAlgorithm.CatmullRom => CatmullRom(samples, to),
                _ => NearestNeighbour(samples, to)
            };
        }

        /// <summary>
        /// Resamples a multichannel array with the quality set by the user.
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count per channel</param>
        /// <param name="channels">Channel count</param>
        /// <param name="quality">Listener audio quality</param>
        /// <returns>Resampled version of the input</returns>
        public static float[] Adaptive(float[] samples, int to, int channels, QualityModes quality) {
            float[][] channelSplit = new float[channels][];
            int splitSize = samples.Length / channels;
            for (int channel = 0; channel < channels; channel++) {
                channelSplit[channel] = new float[splitSize];
            }
            for (int sample = 0, outSample = 0; sample < splitSize; sample++) {
                for (int channel = 0; channel < channels; channel++, outSample++) {
                    channelSplit[channel][sample] = samples[outSample];
                }
            }
            for (int channel = 0; channel < channels; channel++) {
                channelSplit[channel] = Adaptive(channelSplit[channel], to, quality);
            }
            int newUpdateRate = channelSplit[0].Length;
            samples = new float[channels * newUpdateRate];
            for (int sample = 0, outSample = 0; sample < newUpdateRate; sample++) {
                for (int channel = 0; channel < channels; channel++, outSample++) {
                    samples[outSample] = channelSplit[channel][sample];
                }
            }
            return samples;
        }

        /// <summary>
        /// Resamples a single channel with medium quality (nearest neighbour).
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <returns>Returns a resampled version of the given array</returns>
        public static float[] NearestNeighbour(float[] samples, int to) {
            if (samples.Length == to) {
                return samples;
            }
            float[] output = new float[to];
            float ratio = samples.Length / (float)to;
            for (int i = 0; i < to; i++) {
                output[i] = samples[(int)(i * ratio)];
            }
            return output;
        }

        /// <summary>
        /// Resamples a single channel with medium quality (linear interpolation).
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <returns>Returns a resampled version of the given array</returns>
        public static float[] Lerp(float[] samples, int to) {
            if (samples.Length == to) {
                return samples;
            }
            float[] output = new float[to];
            float ratio = samples.Length / (float)to;
            int lerpUntil = (int)((samples.Length - 1) / ratio); // Halving point where i * ratio would be over the array
            for (int i = 0; i < lerpUntil; i++) {
                int sample = (int)(i * ratio);
                output[i] = QMath.Lerp(samples[sample], samples[sample + 1], i * ratio % 1);
            }
            for (int i = lerpUntil; i < to; i++) {
                output[i] = samples[(int)(i * ratio)];
            }
            return output;
        }

        /// <summary>
        /// Resamples a single channel with high quality (Catmull-Rom spline).
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <returns>Returns a resampled version of the given array</returns>
        public static float[] CatmullRom(float[] samples, int to) {
            if (samples.Length == to) {
                return samples;
            }
            float[] output = new float[to];
            float ratio = samples.Length / (float)to;
            int start = (int)(1 / ratio + 1), end = samples.Length - 3;
            for (int i = 0; i < start; i++) {
                output[i] = samples[i];
            }
            for (int i = start; i < to; i++) {
                int sample = (int)(i * ratio);
                if (sample < end) {
                    float t = i * ratio % 1;
                    float p0 = samples[sample - 1], p1 = samples[sample], p2 = samples[sample + 1], p3 = samples[sample + 2];
                    output[i] = ((p1 * 2) + (p2 - p0) * t + (p0 * 2 - p1 * 5 + p2 * 4 - p3) * t * t +
                        (3 * p1 - p0 - 3 * p2 + p3) * t * t * t) * .5f;
                } else {
                    output[i] = samples[sample];
                }
            }
            return output;
        }

        /// <summary>
        /// Resamples a single channel with Kaiser-windowed 64-tap polyphase sinc interpolation.
        /// </summary>
        /// <param name="samples">Samples of the source channel</param>
        /// <param name="to">New sample count</param>
        /// <returns>Returns a resampled version of the given array</returns>
        public static float[] KaiserSinc(float[] samples, int to) {
            if (samples.Length == to) {
                return samples;
            }
            float[] output = new float[to];
            PolyphaseSincResampler.EvaluateStatic(samples, samples.Length, output, to);
            return output;
        }
    }
}
