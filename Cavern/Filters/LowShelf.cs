using System;
using System.Collections.Generic;

using Cavern.Filters.Utilities;
using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Simple first-order low shelf filter.
    /// </summary>
    public class LowShelf : BiquadFilter {
        /// <inheritdoc/>
        public override BiquadFilterType FilterType => BiquadFilterType.LowShelf;

        /// <summary>
        /// Simple first-order low shelf filter with maximum flatness and no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        public LowShelf(int sampleRate, double centerFreq) : base(sampleRate, centerFreq) { }

        /// <summary>
        /// Simple first-order low shelf filter with no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        public LowShelf(int sampleRate, double centerFreq, double q) : base(sampleRate, centerFreq, q) { }

        /// <summary>
        /// Simple first-order low shelf filter.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        /// <param name="gain">Gain of the filter in decibels</param>
        public LowShelf(int sampleRate, double centerFreq, double q, double gain) : base(sampleRate, centerFreq, q, gain) { }

        /// <summary>
        /// Parse a Filter line of Equalizer APO or AutoEq which was split at spaces to a Cavern <see cref="LowShelf"/> filter.<br />
        /// Sample with fixed shelf: Filter: ON LS Fc 100 Hz Gain 0 dB<br />
        /// Sample with AutoEq Q shelf: Filter 1: ON LSC Fc 105 Hz Gain 5.0 dB Q 0.70<br />
        /// Sample with custom slope: Filter: ON LSC 12 dB Fc 100 Hz Gain 0 dB
        /// </summary>
        public static new LowShelf FromEqualizerAPO(string[] splitLine, int sampleRate) {
            int typeIndex = splitLine.Length > 2 && splitLine[1].Equals("ON", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            if (splitLine.Length <= typeIndex) {
                throw new FormatException(nameof(splitLine));
            }
            string type = splitLine[typeIndex].ToLowerInvariant();
            if ((type == "ls" || type == "lsc") && splitLine.Length >= typeIndex + 6 &&
                splitLine[typeIndex + 1].Equals("Fc", StringComparison.OrdinalIgnoreCase) &&
                QMath.TryParseDouble(splitLine[typeIndex + 2], out double freq) &&
                QMath.TryParseDouble(splitLine[typeIndex + 5], out double gain)) {
                if (splitLine.Length >= typeIndex + 9 &&
                    splitLine[typeIndex + 7].Equals("Q", StringComparison.OrdinalIgnoreCase) &&
                    QMath.TryParseDouble(splitLine[typeIndex + 8], out double qVal) && qVal > 0) {
                    return new LowShelf(sampleRate, freq, qVal, gain);
                }
                return new LowShelf(sampleRate, freq, QFactor.FromSlope(0.9, gain), gain);
            } else if (type == "lsc" && splitLine.Length >= typeIndex + 8 &&
                QMath.TryParseDouble(splitLine[typeIndex + 1], out double slope) &&
                QMath.TryParseDouble(splitLine[typeIndex + 4], out freq) &&
                QMath.TryParseDouble(splitLine[typeIndex + 7], out gain)) {
                return new LowShelf(sampleRate, freq, QFactor.FromSlopeDecibels(slope, gain), gain);
            }
            throw new FormatException(nameof(splitLine));
        }

        /// <inheritdoc/>
        public override object Clone() => new LowShelf(SampleRate, centerFreq, q, gain);

        /// <inheritdoc/>
        public override object Clone(int sampleRate) => new LowShelf(sampleRate, centerFreq, q, gain);

        /// <inheritdoc/>
        public override void ExportToEqualizerAPO(List<string> wipConfig) {
            if (q == QFactor.reference) {
                wipConfig.Add($"Filter: ON LS Fc {centerFreq} Hz Gain {gain} dB");
            } else {
                wipConfig.Add($"Filter: ON LSC {QFactor.ToSlope(q, gain)} dB Fc {centerFreq} Hz Gain {gain} dB");
            }
        }

        /// <inheritdoc/>
        protected override void Reset(float cosW0, float alpha, float _) {
            float a = (float)Math.Pow(10, gain * .025f),
                slope = 2 * (float)Math.Sqrt(a) * alpha,
                minCos = (a - 1) * cosW0,
                addCos = (a + 1) * cosW0;
            float divisor = 1 / (a + 1 + minCos + slope);
            a1 = -2 * (a - 1 + addCos) * divisor;
            a2 = (a + 1 + minCos - slope) * divisor;
            b0 = a * (a + 1 - minCos + slope) * divisor;
            b1 = 2 * a * (a - 1 - addCos) * divisor;
            b2 = a * (a + 1 - minCos - slope) * divisor;
        }
    }
}