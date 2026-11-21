using System;

namespace Cavern.Remapping {
    /// <summary>
    /// Zero-allocation fixed integer sample delay line used to keep pass-through beds, LFE, and native Atmos objects
    /// sample-accurately time-aligned with <see cref="ParametricStftAnalyzer.LatencySamples"/>.
    /// </summary>
    internal sealed class FixedSampleDelayLine {
        readonly float[] buffer;
        int writeIndex;

        /// <summary>
        /// Delay length in samples.
        /// </summary>
        public int DelaySamples => buffer.Length;

        /// <summary>
        /// Creates a fixed delay line of <paramref name="delaySamples"/> samples.
        /// </summary>
        public FixedSampleDelayLine(int delaySamples) {
            buffer = new float[delaySamples > 0 ? delaySamples : 1];
        }

        /// <summary>
        /// Delays <paramref name="input"/> into <paramref name="output"/> for <paramref name="count"/> samples.
        /// Supports in-place operation (<paramref name="input"/> == <paramref name="output"/>).
        /// </summary>
        public void Process(float[] input, float[] output, int count) {
            float[] line = buffer;
            int len = line.Length;
            int idx = writeIndex;
            for (int i = 0; i < count; i++) {
                float x = input[i];
                output[i] = line[idx];
                line[idx] = x;
                idx++;
                if (idx >= len) {
                    idx = 0;
                }
            }
            writeIndex = idx;
        }

        /// <summary>
        /// Clears the delay buffer to silence.
        /// </summary>
        public void Reset() {
            Array.Clear(buffer, 0, buffer.Length);
            writeIndex = 0;
        }
    }
}
