using System;
using System.ComponentModel;
using System.Runtime.Serialization;

using Cavern.Filters.Interfaces;

namespace Cavern.Filters {
    /// <summary>
    /// Simple variable-order crossover.
    /// </summary>
    public class Crossover : Filter, IResettableFilter, ISampleRateDependentFilter {
        /// <inheritdoc/>
        [IgnoreDataMember]
        public int SampleRate {
            get => sampleRate;
            set {
                sampleRate = value;
                if (lowpasses != null) {
                    for (int i = 0; i < lowpasses.Length; i++) {
                        lowpasses[i].SampleRate = value;
                        highpasses[i].SampleRate = value;
                    }
                }
            }
        }
        int sampleRate;

        /// <summary>
        /// Crossover frequency.
        /// </summary>
        [DisplayName("Frequency (Hz)")]
        public double Frequency {
            get => lowpasses[0].CenterFreq;
            set {
                for (int i = 0; i < lowpasses.Length; i++) {
                    lowpasses[i].CenterFreq = highpasses[i].CenterFreq = value;
                }
            }
        }

        /// <summary>
        /// Number of filters per pass.
        /// </summary>
        /// <remarks>A value of 2 configures a 4th-order Linkwitz-Riley (LR4) crossover with in-phase flat summation;
        /// all orders configure exact Linkwitz-Riley Q factors and polarity alignment.</remarks>
        [DisplayName("Order")]
        public int Order {
            get => lowpasses.Length;
            set => RecreateFilters(lowpasses[0].CenterFreq, value);
        }

        /// <summary>
        /// Low frequency data.
        /// </summary>
        public float[] LowOutput { get; private set; } = Array.Empty<float>();

        /// <summary>
        /// High frequency data.
        /// </summary>
        public float[] HighOutput { get; private set; } = Array.Empty<float>();

        /// <summary>
        /// Lowpass filters for each pass.
        /// </summary>
        Lowpass[] lowpasses;

        /// <summary>
        /// Highpass filters for each pass.
        /// </summary>
        Highpass[] highpasses;

        /// <summary>
        /// True for odd half-orders (e.g. LR2, LR6) where highpass polarity inversion aligns phases for flat sum.
        /// </summary>
        bool invertHighOutput;

        /// <summary>
        /// Create Linkwitz-Riley cascaded filters with exact Butterworth Q placement and phase alignment.
        /// </summary>
        void RecreateFilters(double frequency, int order) {
            if (order < 1) {
                throw new ArgumentOutOfRangeException(nameof(order), "Crossover order must be at least 1.");
            }

            lowpasses = new Lowpass[order];
            highpasses = new Highpass[order];
            invertHighOutput = (order & 1) != 0;

            int idx = 0;
            if (invertHighOutput) {
                lowpasses[idx] = new Lowpass(SampleRate, frequency, 0.5);
                highpasses[idx] = new Highpass(SampleRate, frequency, 0.5);
                idx++;
            }

            int pairCount = order >> 1;
            for (int k = 0; k < pairCount; k++) {
                double angle = invertHighOutput
                    ? (k + 1) * Math.PI / order
                    : (2 * k + 1) * Math.PI / (2.0 * order);
                double sectionQ = 1.0 / (2.0 * Math.Cos(angle));
                for (int copy = 0; copy < 2; copy++) {
                    lowpasses[idx] = new Lowpass(SampleRate, frequency, sectionQ);
                    highpasses[idx] = new Highpass(SampleRate, frequency, sectionQ);
                    idx++;
                }
            }
        }

        /// <summary>
        /// Simple 2nd-order crossover.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="frequency">Crossover frequency</param>
        public Crossover(int sampleRate, double frequency) : this(sampleRate, frequency, 2) { }

        /// <summary>
        /// Simple variable-order crossover.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="frequency">Crossover frequency</param>
        /// <param name="order">Number of filters per pass, 2 is recommended for mixing notch prevention</param>
        public Crossover(int sampleRate, double frequency, int order) {
            SampleRate = sampleRate;
            RecreateFilters(frequency, order);
        }

        /// <summary>
        /// Create empty outputs for a given <paramref name="updateRate"/>> in case they are used before processing. This optimizes zero checks.
        /// </summary>
        public void PresetOutput(int updateRate) {
            LowOutput = new float[updateRate];
            HighOutput = new float[updateRate];
        }

        /// <inheritdoc/>
        public void Reset() {
            for (int i = 0; i < lowpasses.Length; i++) {
                lowpasses[i].Reset();
                highpasses[i].Reset();
            }
        }

        /// <inheritdoc/>
        public override void Process(float[] samples, int channel, int channels) {
            int sampleCount = samples.Length;
            if (sampleCount != LowOutput.Length) {
                LowOutput = new float[sampleCount];
                HighOutput = new float[sampleCount];
            }
            Array.Copy(samples, LowOutput, sampleCount);
            Array.Copy(samples, HighOutput, sampleCount);
            for (int i = 0; i < lowpasses.Length; i++) {
                lowpasses[i].Process(LowOutput, channel, channels);
                highpasses[i].Process(HighOutput, channel, channels);
            }
            if (invertHighOutput) {
                for (int sample = channel; sample < sampleCount; sample += channels) {
                    HighOutput[sample] = -HighOutput[sample];
                }
            }
        }

        /// <summary>
        /// Apply crossover on an array of samples. One filter should be applied to only one continuous stream of samples.
        /// </summary>
        /// <param name="samples">Input samples</param>
        /// <param name="lows">Low frequency data</param>
        /// <param name="highs">High frequency data</param>
        public void Process(float[] samples, out float[] lows, out float[] highs) {
            Process(samples);
            lows = LowOutput;
            highs = HighOutput;
        }

        /// <summary>
        /// Apply crossover on an array of samples. One filter should be applied to only one continuous stream of samples.
        /// </summary>
        /// <param name="samples">Input samples</param>
        /// <param name="channel">Channel to filter</param>
        /// <param name="channels">Total channels</param>
        /// <param name="lows">Low frequency data</param>
        /// <param name="highs">High frequency data</param>
        public void Process(float[] samples, int channel, int channels, out float[] lows, out float[] highs) {
            Process(samples, channel, channels);
            lows = LowOutput;
            highs = HighOutput;
        }

        /// <inheritdoc/>
        public override object Clone() => new Crossover(SampleRate, Frequency, Order);
    }
}
