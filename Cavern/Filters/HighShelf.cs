using System;
using System.Collections.Generic;

using Cavern.Filters.Utilities;
using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Simple first-order high shelf filter.
    /// </summary>
    public class HighShelf : BiquadFilter {
        /// <inheritdoc/>
        public override BiquadFilterType FilterType => BiquadFilterType.HighShelf;

        /// <summary>
        /// Simple first-order high shelf filter with maximum flatness and no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        public HighShelf(int sampleRate, double centerFreq) : base(sampleRate, centerFreq) { }

        /// <summary>
        /// Simple first-order high shelf filter with no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        public HighShelf(int sampleRate, double centerFreq, double q) : base(sampleRate, centerFreq, q) { }

        /// <summary>
        /// Simple first-order high shelf filter.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        /// <param name="gain">Gain of the filter in decibels</param>
        public HighShelf(int sampleRate, double centerFreq, double q, double gain) : base(sampleRate, centerFreq, q, gain) { }

        /// <summary>
        /// Parse a Filter line of Equalizer APO or AutoEq which was split at spaces to a Cavern <see cref="HighShelf"/> filter.<br />
        /// Sample with fixed shelf: Filter: ON HS Fc 10000 Hz Gain 0 dB<br />
        /// Sample with AutoEq Q shelf: Filter 6: ON HSC Fc 10000 Hz Gain -2.1 dB Q 0.70<br />
        /// Sample with custom slope: Filter: ON HSC 12 dB Fc 10000 Hz Gain 0 dB
        /// </summary>
        public static new HighShelf FromEqualizerAPO(string[] splitLine, int sampleRate) {
            int typeIndex = splitLine.Length > 2 && splitLine[1].Equals("ON", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            if (splitLine.Length <= typeIndex) {
                throw new FormatException(nameof(splitLine));
            }
            string type = splitLine[typeIndex].ToLowerInvariant();
            if ((type == "hs" || type == "hsc") && splitLine.Length >= typeIndex + 6 &&
                splitLine[typeIndex + 1].Equals("Fc", StringComparison.OrdinalIgnoreCase) &&
                QMath.TryParseDouble(splitLine[typeIndex + 2], out double freq) &&
                QMath.TryParseDouble(splitLine[typeIndex + 5], out double gain)) {
                if (splitLine.Length >= typeIndex + 9 &&
                    splitLine[typeIndex + 7].Equals("Q", StringComparison.OrdinalIgnoreCase) &&
                    QMath.TryParseDouble(splitLine[typeIndex + 8], out double qVal) && qVal > 0) {
                    return new HighShelf(sampleRate, freq, qVal, gain);
                }
                return new HighShelf(sampleRate, freq, QFactor.FromSlope(0.9, gain), gain);
            } else if (type == "hsc" && splitLine.Length >= typeIndex + 8 &&
                QMath.TryParseDouble(splitLine[typeIndex + 1], out double slope) &&
                QMath.TryParseDouble(splitLine[typeIndex + 4], out freq) &&
                QMath.TryParseDouble(splitLine[typeIndex + 7], out gain)) {
                return new HighShelf(sampleRate, freq, QFactor.FromSlopeDecibels(slope, gain), gain);
            }
            throw new FormatException(nameof(splitLine));
        }

        /// <inheritdoc/>
        public override object Clone() => new HighShelf(SampleRate, centerFreq, q, gain);

        /// <inheritdoc/>
        public override object Clone(int sampleRate) => new HighShelf(sampleRate, centerFreq, q, gain);

        /// <inheritdoc/>
        public override void ExportToEqualizerAPO(List<string> wipConfig) {
            if (q == QFactor.reference) {
                wipConfig.Add($"Filter: ON HS Fc {centerFreq} Hz Gain {gain} dB");
            } else {
                wipConfig.Add($"Filter: ON HSC {QFactor.ToSlope(q, gain)} dB Fc {centerFreq} Hz Gain {gain} dB");
            }
        }

        /// <inheritdoc/>
        protected override void ResetCoefficients(double cosW0, double alpha, double _) {
            double a = Math.Pow(10.0, gain * 0.025);
            double slope = 2.0 * Math.Sqrt(a) * alpha;
            double minCos = (a - 1.0) * cosW0;
            double addCos = (a + 1.0) * cosW0;
            double divisor = 1.0 / (a + 1.0 - minCos + slope);
            SetCoefficients(
                a * (a + 1.0 + minCos + slope) * divisor,
                -2.0 * a * (a - 1.0 + addCos) * divisor,
                a * (a + 1.0 + minCos - slope) * divisor,
                2.0 * (a - 1.0 - addCos) * divisor,
                (a + 1.0 - minCos - slope) * divisor);
        }
    }
}