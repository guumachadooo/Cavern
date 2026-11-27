using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

using Cavern.Filters.Interfaces;
using Cavern.Filters.Utilities;
using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Simple first-order biquad filter.
    /// </summary>
    public abstract partial class BiquadFilter : Filter, IEqualizerAPOFilter, ILocalizableToString, IResettableFilter, ISampleRateDependentFilter {
        /// <inheritdoc/>
        [IgnoreDataMember]
        public int SampleRate {
            get => sampleRate;
            set {
                sampleRate = value;
                Reset(centerFreq, q, gain);
            }
        }
        int sampleRate;

        /// <summary>
        /// Center frequency (-3 dB point) of the filter.
        /// </summary>
        [DisplayName("Center frequency (Hz)")]
        public double CenterFreq {
            get => centerFreq;
            set => Reset(value, q, gain);
        }

        /// <summary>
        /// Q-factor of the filter.
        /// </summary>
        [DisplayName("Q-factor")]
        public double Q {
            get => q;
            set => Reset(centerFreq, value, gain);
        }

        /// <summary>
        /// Gain of the filter in decibels.
        /// </summary>
        [DisplayName("Gain (dB)")]
        public double Gain {
            get => gain;
            set => Reset(centerFreq, q, value);
        }

#pragma warning disable IDE1006 // Naming Styles
        /// <summary>
        /// Transfer function variable.
        /// </summary>
        public float a1 {
            get => a1f;
            protected set {
                a1f = value;
                a1d = value;
            }
        }

        /// <summary>
        /// Transfer function variable.
        /// </summary>
        public float a2 {
            get => a2f;
            protected set {
                a2f = value;
                a2d = value;
            }
        }

        /// <summary>
        /// Transfer function variable.
        /// </summary>
        public float b0 {
            get => b0f;
            protected set {
                b0f = value;
                b0d = value;
            }
        }

        /// <summary>
        /// Transfer function variable.
        /// </summary>
        public float b1 {
            get => b1f;
            protected set {
                b1f = value;
                b1d = value;
            }
        }

        /// <summary>
        /// Transfer function variable.
        /// </summary>
        public float b2 {
            get => b2f;
            protected set {
                b2f = value;
                b2d = value;
            }
        }
#pragma warning restore IDE1006 // Naming Styles

        /// <summary>
        /// The enumerated type of this filter.
        /// </summary>
        public abstract BiquadFilterType FilterType { get; }

        /// <summary>
        /// Center frequency (-3 dB point) of the filter.
        /// </summary>
        protected double centerFreq;

        /// <summary>
        /// Q-factor of the filter.
        /// </summary>
        protected double q;

        /// <summary>
        /// Gain of the filter in decibels.
        /// </summary>
        protected double gain;

        /// <summary>
        /// 64-bit Transposed Direct Form II delay states.
        /// </summary>
        protected double z1, z2;

        /// <summary>
        /// 64-bit internal transfer function coefficients.
        /// </summary>
        protected double b0d = 1.0, b1d, b2d, a1d, a2d;

        /// <summary>
        /// Cached single-precision transfer function coefficients for public property compatibility.
        /// </summary>
        float b0f = 1f, b1f, b2f, a1f, a2f;

        /// <summary>
        /// Denormal prevention bias in double precision (well above double subnormals, rounds to exact 0f in float).
        /// </summary>
        const double DenormalGuard = 1e-200;

        /// <summary>
        /// Native CavernAmp instance for accelerated processing.
        /// </summary>
        protected IntPtr nativeInstance = IntPtr.Zero;

        /// <summary>
        /// Simple first-order biquad filter with maximum flatness and no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        protected BiquadFilter(int sampleRate, double centerFreq) : this(sampleRate, centerFreq, QFactor.reference, 0) { }

        /// <summary>
        /// Simple first-order biquad filter with no additional gain.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        protected BiquadFilter(int sampleRate, double centerFreq, double q) : this(sampleRate, centerFreq, q, 0) { }

        /// <summary>
        /// Simple first-order biquad filter.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        /// <param name="gain">Gain of the filter in decibels</param>
        protected BiquadFilter(int sampleRate, double centerFreq, double q, double gain) {
            this.sampleRate = sampleRate;
            Reset(centerFreq, q, gain);
        }

        /// <inheritdoc/>
        public override void Process(float[] samples) {
            double lz1 = z1, lz2 = z2;
            double lb0 = b0d, lb1 = b1d, lb2 = b2d, la1 = a1d, la2 = a2d;
            for (int sample = 0; sample < samples.Length; sample++) {
                double x = samples[sample];
                double y = lb0 * x + lz1;
                lz1 = lb1 * x - la1 * y + lz2 + DenormalGuard;
                lz2 = lb2 * x - la2 * y;
                samples[sample] = (float)y;
            }
            z1 = Math.Abs(lz1) < 1e-150 ? 0.0 : lz1;
            z2 = Math.Abs(lz2) < 1e-150 ? 0.0 : lz2;
        }

        /// <inheritdoc/>
        public override void Process(float[] samples, int channel, int channels) {
            if (channels == 1 && channel == 0) {
                Process(samples);
                return;
            }
            double lz1 = z1, lz2 = z2;
            double lb0 = b0d, lb1 = b1d, lb2 = b2d, la1 = a1d, la2 = a2d;
            for (int sample = channel; sample < samples.Length; sample += channels) {
                double x = samples[sample];
                double y = lb0 * x + lz1;
                lz1 = lb1 * x - la1 * y + lz2 + DenormalGuard;
                lz2 = lb2 * x - la2 * y;
                samples[sample] = (float)y;
            }
            z1 = Math.Abs(lz1) < 1e-150 ? 0.0 : lz1;
            z2 = Math.Abs(lz2) < 1e-150 ? 0.0 : lz2;
        }

        /// <inheritdoc/>
        public void Reset() {
            z1 = 0.0;
            z2 = 0.0;
        }

        /// <summary>
        /// Regenerate the transfer function with maximum flatness and no additional gain.
        /// </summary>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        public virtual void Reset(double centerFreq) => Reset(centerFreq, QFactor.reference, 0);

        /// <summary>
        /// Regenerate the transfer function with a custom Q-factor, but no additional gain.
        /// </summary>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        public virtual void Reset(double centerFreq, double q) => Reset(centerFreq, q, 0);

        /// <summary>
        /// Regenerate the transfer function.
        /// </summary>
        /// <param name="centerFreq">Center frequency (-3 dB point) of the filter</param>
        /// <param name="q">Q-factor of the filter</param>
        /// <param name="gain">Gain of the filter in decibels</param>
        public void Reset(double centerFreq, double q, double gain) {
            this.centerFreq = centerFreq;
            this.q = q;
            this.gain = gain;
            double w0 = Math.PI * 2.0 * centerFreq / SampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (q + q);
            double divisor = 1.0 / (1.0 + alpha);
            ResetCoefficients(cos, alpha, divisor);
        }

        /// <summary>
        /// Create a copy of this filter with a changed <see cref="SampleRate"/>.
        /// </summary>
        /// <param name="sampleRate">Sample rate of the new filter</param>
        public abstract object Clone(int sampleRate);

        /// <inheritdoc/>
        public abstract void ExportToEqualizerAPO(List<string> wipConfig);

        /// <summary>
        /// Clone the filter with an inverse gain.
        /// </summary>
        public BiquadFilter GetInverse() => Create(FilterType, sampleRate, centerFreq, q, -gain);

        /// <summary>
        /// Calculate the maximum distance of the filter's poles from the origin.
        /// </summary>
        public float GetPoleRadius() {
            double discriminant = a1d * a1d - 4.0 * a2d;
            return discriminant >= 0.0
                ? (float)Math.Max(Math.Abs((-a1d + Math.Sqrt(discriminant)) * 0.5), Math.Abs((-a1d - Math.Sqrt(discriminant)) * 0.5))
                : (float)Math.Sqrt(a2d);
        }

        /// <summary>
        /// Reset the parameters specifically for the derived filter in 64-bit precision.
        /// </summary>
        /// <param name="cosW0">Cosine of omega0</param>
        /// <param name="alpha">Value of the alpha parameter</param>
        /// <param name="divisor">1 / a0, as a0 is the same for all biquad filters</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected abstract void ResetCoefficients(double cosW0, double alpha, double divisor);

        /// <summary>
        /// Store normalized 64-bit coefficients and update public single-precision projections.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void SetCoefficients(double b0, double b1, double b2, double a1, double a2) {
            b0d = b0;
            b1d = b1;
            b2d = b2;
            a1d = a1;
            a2d = a2;
            b0f = (float)b0;
            b1f = (float)b1;
            b2f = (float)b2;
            a1f = (float)a1;
            a2f = (float)a2;
        }

        /// <summary>
        /// Sets up a lowpass/highpass filter in 64-bit precision.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void SetupPass(double cosW0, double alpha, double divisor, double b1Pre) {
            double b1Val = b1Pre * divisor;
            double b2Val = Math.Abs(b1Val) * 0.5;
            double b0Val = (gain == 0.0 ? 1.0 : Math.Pow(10.0, gain * 0.025)) * b2Val;
            SetCoefficients(b0Val, b1Val, b2Val, -2.0 * cosW0 * divisor, (1.0 - alpha) * divisor);
        }
    }
}
