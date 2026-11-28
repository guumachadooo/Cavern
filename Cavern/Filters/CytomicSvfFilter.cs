using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

using Cavern.Filters.Interfaces;
using Cavern.Filters.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Supported topologies for <see cref="CytomicSvfFilter"/>.
    /// </summary>
    public enum CytomicSvfType {
        /// <summary>
        /// 12 dB/octave zero-delay-feedback lowpass filter.
        /// </summary>
        Lowpass,
        /// <summary>
        /// 12 dB/octave zero-delay-feedback highpass filter.
        /// </summary>
        Highpass,
        /// <summary>
        /// Constant 0 dB peak gain bandpass filter.
        /// </summary>
        Bandpass,
        /// <summary>
        /// Band-reject notch filter.
        /// </summary>
        Notch,
        /// <summary>
        /// Bell / parametric peaking equalizer filter.
        /// </summary>
        Peak,
        /// <summary>
        /// Second-order low-shelf filter.
        /// </summary>
        LowShelf,
        /// <summary>
        /// Second-order high-shelf filter.
        /// </summary>
        HighShelf,
        /// <summary>
        /// Unity-magnitude second-order allpass phase filter.
        /// </summary>
        Allpass
    }

    /// <summary>
    /// Andy Simper Cytomic Zero-Delay-Feedback State Variable Filter (ZDF-SVF) in 64-bit floating-point precision
    /// with click-free critically damped coefficient/parameter smoothing during live modulation sweeps.
    /// </summary>
    public class CytomicSvfFilter : Filter, IResettableFilter, ISampleRateDependentFilter {
        /// <inheritdoc/>
        public override bool LinearTimeInvariant => !isSmoothing;

        /// <inheritdoc/>
        [IgnoreDataMember]
        public int SampleRate {
            get => sampleRate;
            set {
                sampleRate = value > 0 ? value : Listener.DefaultSampleRate;
                UpdateSmoothingAlpha();
                UpdateTargets(!hasProcessed);
            }
        }

        /// <summary>
        /// Active SVF filter topology.
        /// </summary>
        [DisplayName("Filter type")]
        public CytomicSvfType FilterType {
            get => filterType;
            set {
                if (filterType != value) {
                    filterType = value;
                    UpdateTargets(!hasProcessed);
                }
            }
        }

        /// <summary>
        /// Cutoff or center frequency in Hz.
        /// </summary>
        [DisplayName("Center frequency (Hz)")]
        public double CenterFreq {
            get => centerFreq;
            set {
                centerFreq = value;
                UpdateTargets(!hasProcessed);
            }
        }

        /// <summary>
        /// Quality factor Q of the filter.
        /// </summary>
        [DisplayName("Q-factor")]
        public double Q {
            get => q;
            set {
                q = value;
                UpdateTargets(!hasProcessed);
            }
        }

        /// <summary>
        /// Gain in decibels for Peak, LowShelf, and HighShelf modes.
        /// </summary>
        [DisplayName("Gain (dB)")]
        public double Gain {
            get => gain;
            set {
                gain = value;
                UpdateTargets(!hasProcessed);
            }
        }

        /// <summary>
        /// Parameter smoothing time constant in milliseconds (0 disables smoothing).
        /// </summary>
        [DisplayName("Smoothing time (ms)")]
        public double SmoothingTimeMs {
            get => smoothingTimeMs;
            set {
                smoothingTimeMs = value >= 0.0 ? value : 0.0;
                UpdateSmoothingAlpha();
                if (smoothingTimeMs <= 0.0) {
                    SnapToTargets();
                }
            }
        }

        /// <summary>
        /// When true, applies Vicanek/Orfanidis Nyquist-decramped matching for Peak, LowShelf, and HighShelf
        /// topologies so high-frequency magnitude stays within &lt;= 0.25 dB of the analog prototype up to 18 kHz @ 48 kHz.
        /// </summary>
        [DisplayName("Nyquist decramping")]
        public bool NyquistMatched {
            get => nyquistMatched;
            set {
                if (nyquistMatched != value) {
                    nyquistMatched = value;
                    UpdateTargets(!hasProcessed);
                }
            }
        }

        int sampleRate;
        CytomicSvfType filterType;
        double centerFreq;
        double q;
        double gain;
        double smoothingTimeMs;
        bool nyquistMatched;
        bool useMatchedBiquad;

        // ZDF-SVF integrator capacitor states
        double ic1eq, ic2eq;

        // TDF-II states for Nyquist-decramped matched biquad mode
        double bqZ1, bqZ2;

        // Target SVF parameters
        double targetG, targetK, targetM0, targetM1, targetM2;

        // Target normalized biquad parameters (for NyquistMatched mode)
        double targetB0 = 1.0, targetB1, targetB2, targetA1, targetA2;
        double s1B0 = 1.0, s1B1, s1B2, s1A1, s1A2;
        double curB0 = 1.0, curB1, curB2, curA1, curA2;

        // 2-stage critically damped smoother states
        double s1G, s1K, s1M0, s1M1, s1M2;
        double curG, curK, curM0, curM1, curM2;
        double a1, a2, a3;

        double smoothAlpha;
        bool isSmoothing;
        bool hasProcessed;

        /// <summary>
        /// Create a lowpass Cytomic ZDF-SVF filter with default Butterworth Q and 10 ms click-free smoothing.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="centerFreq">Cutoff frequency in Hz</param>
        public CytomicSvfFilter(int sampleRate, double centerFreq) :
            this(sampleRate, CytomicSvfType.Lowpass, centerFreq, QFactor.reference, 0.0, 10.0) { }

        /// <summary>
        /// Create a Cytomic ZDF-SVF filter with the specified mode and parameters.
        /// </summary>
        /// <param name="sampleRate">Audio sample rate</param>
        /// <param name="filterType">SVF topology mode</param>
        /// <param name="centerFreq">Cutoff or center frequency in Hz</param>
        /// <param name="q">Quality factor Q</param>
        /// <param name="gainDb">Gain in decibels for Peak/Shelf modes</param>
        /// <param name="smoothingTimeMs">Critically damped parameter smoothing duration in milliseconds</param>
        public CytomicSvfFilter(
            int sampleRate,
            CytomicSvfType filterType,
            double centerFreq,
            double q = QFactor.reference,
            double gainDb = 0.0,
            double smoothingTimeMs = 10.0) {
            this.sampleRate = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
            this.filterType = filterType;
            this.centerFreq = centerFreq;
            this.q = q;
            gain = gainDb;
            this.smoothingTimeMs = smoothingTimeMs >= 0.0 ? smoothingTimeMs : 0.0;
            UpdateSmoothingAlpha();
            UpdateTargets(true);
        }

        /// <summary>
        /// Atomically update all filter parameters, optionally snapping coefficients immediately without smoothing.
        /// </summary>
        public void SetParameters(CytomicSvfType filterType, double centerFreq, double q, double gainDb, bool immediate = false) {
            this.filterType = filterType;
            this.centerFreq = centerFreq;
            this.q = q;
            gain = gainDb;
            UpdateTargets(immediate || !hasProcessed);
        }

        /// <summary>
        /// Evaluate the linear magnitude response across <paramref name="bins"/> frequency bins using the target coefficients.
        /// </summary>
        public float[] GetFrequencyResponse(int bins) {
            if (bins <= 0) {
                throw new ArgumentOutOfRangeException(nameof(bins), "Must be positive.");
            }
            float[] response = new float[bins];
            double nyquistRatio = 0.5 / bins;
            for (int i = 0; i < bins; i++) {
                double freqHz = i * nyquistRatio * sampleRate;
                response[i] = (float)EvaluateMagnitude(freqHz);
            }
            return response;
        }

        /// <summary>
        /// Evaluate the exact complex transfer function H(e^{jw}) at <paramref name="freqHz"/> using target coefficients.
        /// </summary>
        public void EvaluateComplex(double freqHz, out double real, out double imag) {
            double safeSr = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
            double f = Math.Clamp(freqHz, 0.0, safeSr * 0.499);
            GetBiquadCoefficients(out double b0, out double b1, out double b2, out double da1, out double da2);
            double w = 2.0 * Math.PI * f / safeSr;
            double cos1 = Math.Cos(w);
            double sin1 = Math.Sin(w);
            double cos2 = cos1 * cos1 - sin1 * sin1;
            double sin2 = 2.0 * sin1 * cos1;

            double numR = b0 + b1 * cos1 + b2 * cos2;
            double numI = -(b1 * sin1 + b2 * sin2);
            double denR = 1.0 + da1 * cos1 + da2 * cos2;
            double denI = -(da1 * sin1 + da2 * sin2);
            double denMagSq = denR * denR + denI * denI;
            if (denMagSq <= 1e-24) {
                real = 1.0;
                imag = 0.0;
                return;
            }
            real = (numR * denR + numI * denI) / denMagSq;
            imag = (numI * denR - numR * denI) / denMagSq;
        }

        /// <summary>
        /// Evaluate the exact linear magnitude response at <paramref name="freqHz"/>.
        /// </summary>
        public double EvaluateMagnitude(double freqHz) {
            EvaluateComplex(freqHz, out double re, out double im);
            return Math.Sqrt(re * re + im * im);
        }

        /// <summary>
        /// Evaluate the wrapped phase response in radians (-pi..pi) at <paramref name="freqHz"/>.
        /// </summary>
        public double EvaluatePhaseRad(double freqHz) {
            EvaluateComplex(freqHz, out double re, out double im);
            return Math.Atan2(im, re);
        }

        /// <summary>
        /// Compute the equivalent normalized digital biquad coefficients (a0 = 1) for export or response analysis.
        /// </summary>
        public void GetBiquadCoefficients(out double b0, out double b1, out double b2, out double da1, out double da2) {
            if (useMatchedBiquad) {
                b0 = targetB0;
                b1 = targetB1;
                b2 = targetB2;
                da1 = targetA1;
                da2 = targetA2;
                return;
            }
            double g = targetG;
            double k = targetK;
            double g2 = g * g;
            double c0 = targetM0;
            double c1 = (targetM0 * k + targetM1) * g;
            double c2 = (targetM0 + targetM2) * g2;

            double a0 = 1.0 + k * g + g2;
            double invA0 = a0 > 1e-24 ? 1.0 / a0 : 1.0;
            b0 = (c0 + c1 + c2) * invA0;
            b1 = 2.0 * (c2 - c0) * invA0;
            b2 = (c0 - c1 + c2) * invA0;
            da1 = 2.0 * (g2 - 1.0) * invA0;
            da2 = (1.0 - k * g + g2) * invA0;
        }

        /// <inheritdoc/>
        public void Reset() {
            ic1eq = 0.0;
            ic2eq = 0.0;
            bqZ1 = 0.0;
            bqZ2 = 0.0;
            hasProcessed = false;
            SnapToTargets();
        }

        /// <inheritdoc/>
        public override void Process(float[] samples) => ProcessSlice(samples, 0, samples?.Length ?? 0, 1);

        /// <inheritdoc/>
        public override void Process(float[] samples, int channel, int channels) =>
            ProcessSlice(samples, channel, samples?.Length ?? 0, channels);

        /// <summary>
        /// Process a bounded interleaved slice [<paramref name="start"/>, <paramref name="endExclusive"/>) at stride
        /// <paramref name="step"/> with zero heap allocations.
        /// </summary>
        public void ProcessSlice(float[] samples, int start, int endExclusive, int step) {
            if (samples == null || samples.Length == 0 || start < 0 || step <= 0 || start >= samples.Length) {
                return;
            }
            int limit = Math.Min(endExclusive, samples.Length);
            if (limit <= start) {
                return;
            }

            hasProcessed = true;
            if (useMatchedBiquad) {
                ProcessMatchedBiquadRange(samples, start, limit, step);
                return;
            }

            double c1 = ic1eq;
            double c2 = ic2eq;

            if (!isSmoothing) {
                double ca1 = a1, ca2 = a2, ca3 = a3;
                double cm0 = curM0, cm1 = curM1, cm2 = curM2;
                for (int i = start; i < limit; i += step) {
                    samples[i] = (float)StepSvf(samples[i], ca1, ca2, ca3, cm0, cm1, cm2, ref c1, ref c2);
                }
            } else {
                double alpha = smoothAlpha;
                double tG = targetG, tK = targetK, tM0 = targetM0, tM1 = targetM1, tM2 = targetM2;
                double ls1G = s1G, ls1K = s1K, ls1M0 = s1M0, ls1M1 = s1M1, ls1M2 = s1M2;
                double lG = curG, lK = curK, lM0 = curM0, lM1 = curM1, lM2 = curM2;

                for (int i = start; i < limit; i += step) {
                    ls1G += alpha * (tG - ls1G);
                    lG += alpha * (ls1G - lG);

                    ls1K += alpha * (tK - ls1K);
                    lK += alpha * (ls1K - lK);

                    ls1M0 += alpha * (tM0 - ls1M0);
                    lM0 += alpha * (ls1M0 - lM0);

                    ls1M1 += alpha * (tM1 - ls1M1);
                    lM1 += alpha * (ls1M1 - lM1);

                    ls1M2 += alpha * (tM2 - ls1M2);
                    lM2 += alpha * (ls1M2 - lM2);

                    double ca1 = 1.0 / (1.0 + lG * (lG + lK));
                    double ca2 = lG * ca1;
                    double ca3 = lG * ca2;
                    samples[i] = (float)StepSvf(samples[i], ca1, ca2, ca3, lM0, lM1, lM2, ref c1, ref c2);
                }

                s1G = ls1G;
                s1K = ls1K;
                s1M0 = ls1M0;
                s1M1 = ls1M1;
                s1M2 = ls1M2;
                curG = lG;
                curK = lK;
                curM0 = lM0;
                curM1 = lM1;
                curM2 = lM2;

                if (Math.Abs(tG - lG) < 1e-11 && Math.Abs(tG - ls1G) < 1e-11 &&
                    Math.Abs(tK - lK) < 1e-11 && Math.Abs(tK - ls1K) < 1e-11 &&
                    Math.Abs(tM0 - lM0) < 1e-11 && Math.Abs(tM0 - ls1M0) < 1e-11 &&
                    Math.Abs(tM1 - lM1) < 1e-11 && Math.Abs(tM1 - ls1M1) < 1e-11 &&
                    Math.Abs(tM2 - lM2) < 1e-11 && Math.Abs(tM2 - ls1M2) < 1e-11) {
                    SnapToTargets();
                } else {
                    UpdateStaticCoefficients();
                }
            }

            ic1eq = Math.Abs(c1) < 1e-150 ? 0.0 : c1;
            ic2eq = Math.Abs(c2) < 1e-150 ? 0.0 : c2;
        }

        /// <inheritdoc/>
        public override object Clone() {
            CytomicSvfFilter copy = new CytomicSvfFilter(sampleRate, filterType, centerFreq, q, gain, smoothingTimeMs);
            copy.NyquistMatched = nyquistMatched;
            return copy;
        }

        /// <summary>
        /// Create a copy of this filter targeting a different <paramref name="sampleRate"/>.
        /// </summary>
        public object Clone(int sampleRate) {
            CytomicSvfFilter copy = new CytomicSvfFilter(sampleRate, filterType, centerFreq, q, gain, smoothingTimeMs);
            copy.NyquistMatched = nyquistMatched;
            return copy;
        }

        void ProcessMatchedBiquadRange(float[] samples, int start, int limit, int step) {
            double z1 = bqZ1;
            double z2 = bqZ2;
            if (!isSmoothing) {
                double b0 = curB0, b1 = curB1, b2 = curB2, da1 = curA1, da2 = curA2;
                for (int i = start; i < limit; i += step) {
                    double x = samples[i];
                    double y = b0 * x + z1;
                    z1 = b1 * x - da1 * y + z2 + DenormalGuard;
                    z2 = b2 * x - da2 * y;
                    samples[i] = (float)y;
                }
            } else {
                double alpha = smoothAlpha;
                double tb0 = targetB0, tb1 = targetB1, tb2 = targetB2, ta1 = targetA1, ta2 = targetA2;
                double ls1b0 = s1B0, ls1b1 = s1B1, ls1b2 = s1B2, ls1a1 = s1A1, ls1a2 = s1A2;
                double lb0 = curB0, lb1 = curB1, lb2 = curB2, la1 = curA1, la2 = curA2;
                for (int i = start; i < limit; i += step) {
                    ls1b0 += alpha * (tb0 - ls1b0); lb0 += alpha * (ls1b0 - lb0);
                    ls1b1 += alpha * (tb1 - ls1b1); lb1 += alpha * (ls1b1 - lb1);
                    ls1b2 += alpha * (tb2 - ls1b2); lb2 += alpha * (ls1b2 - lb2);
                    ls1a1 += alpha * (ta1 - ls1a1); la1 += alpha * (ls1a1 - la1);
                    ls1a2 += alpha * (ta2 - ls1a2); la2 += alpha * (ls1a2 - la2);
                    double x = samples[i];
                    double y = lb0 * x + z1;
                    z1 = lb1 * x - la1 * y + z2 + DenormalGuard;
                    z2 = lb2 * x - la2 * y;
                    samples[i] = (float)y;
                }
                s1B0 = ls1b0; s1B1 = ls1b1; s1B2 = ls1b2; s1A1 = ls1a1; s1A2 = ls1a2;
                curB0 = lb0; curB1 = lb1; curB2 = lb2; curA1 = la1; curA2 = la2;
                if (Math.Abs(tb0 - lb0) < 1e-11 && Math.Abs(tb0 - ls1b0) < 1e-11 &&
                    Math.Abs(tb1 - lb1) < 1e-11 && Math.Abs(tb1 - ls1b1) < 1e-11 &&
                    Math.Abs(tb2 - lb2) < 1e-11 && Math.Abs(tb2 - ls1b2) < 1e-11 &&
                    Math.Abs(ta1 - la1) < 1e-11 && Math.Abs(ta1 - ls1a1) < 1e-11 &&
                    Math.Abs(ta2 - la2) < 1e-11 && Math.Abs(ta2 - ls1a2) < 1e-11) {
                    SnapToTargets();
                }
            }
            bqZ1 = Math.Abs(z1) < 1e-150 ? 0.0 : z1;
            bqZ2 = Math.Abs(z2) < 1e-150 ? 0.0 : z2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double StepSvf(
            double v0,
            double ca1,
            double ca2,
            double ca3,
            double cm0,
            double cm1,
            double cm2,
            ref double ic1,
            ref double ic2) {
            double v3 = v0 - ic2;
            double v1 = ca1 * ic1 + ca2 * v3;
            double v2 = ic2 + ca2 * ic1 + ca3 * v3;
            ic1 = 2.0 * v1 - ic1 + DenormalGuard;
            ic2 = 2.0 * v2 - ic2;
            return cm0 * v0 + cm1 * v1 + cm2 * v2;
        }

        void UpdateSmoothingAlpha() {
            if (smoothingTimeMs <= 0.0 || sampleRate <= 0) {
                smoothAlpha = 1.0;
                return;
            }
            double stageSamples = (smoothingTimeMs * 0.5 * 0.001) * sampleRate;
            smoothAlpha = stageSamples > 1.0 ? 1.0 - Math.Exp(-1.0 / stageSamples) : 1.0;
        }

        void UpdateTargets(bool snapImmediate) {
            double safeSr = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
            double safeFreq = Math.Clamp(centerFreq, 1.0, safeSr * 0.495);
            double safeQ = Math.Clamp(q, 0.025, 100.0);
            double safeGainDb = Math.Clamp(gain, -60.0, 60.0);

            double baseG = Math.Tan(Math.PI * safeFreq / safeSr);
            double baseK = 1.0 / safeQ;
            double a = Math.Pow(10.0, safeGainDb / 40.0);

            switch (filterType) {
                case CytomicSvfType.Lowpass:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 0.0;
                    targetM1 = 0.0;
                    targetM2 = 1.0;
                    break;
                case CytomicSvfType.Highpass:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 1.0;
                    targetM1 = -baseK;
                    targetM2 = -1.0;
                    break;
                case CytomicSvfType.Bandpass:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 0.0;
                    targetM1 = baseK;
                    targetM2 = 0.0;
                    break;
                case CytomicSvfType.Notch:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 1.0;
                    targetM1 = -baseK;
                    targetM2 = 0.0;
                    break;
                case CytomicSvfType.Peak:
                    targetG = baseG;
                    targetK = baseK / a;
                    targetM0 = 1.0;
                    targetM1 = targetK * (a * a - 1.0);
                    targetM2 = 0.0;
                    break;
                case CytomicSvfType.LowShelf:
                    targetG = baseG / Math.Sqrt(a);
                    targetK = baseK;
                    targetM0 = 1.0;
                    targetM1 = baseK * (a - 1.0);
                    targetM2 = a * a - 1.0;
                    break;
                case CytomicSvfType.HighShelf:
                    targetG = baseG * Math.Sqrt(a);
                    targetK = baseK;
                    targetM0 = a * a;
                    targetM1 = baseK * (1.0 - a) * a;
                    targetM2 = 1.0 - a * a;
                    break;
                case CytomicSvfType.Allpass:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 1.0;
                    targetM1 = -2.0 * baseK;
                    targetM2 = 0.0;
                    break;
                default:
                    targetG = baseG;
                    targetK = baseK;
                    targetM0 = 0.0;
                    targetM1 = 0.0;
                    targetM2 = 1.0;
                    break;
            }

            useMatchedBiquad = false;
            if (nyquistMatched &&
                (filterType == CytomicSvfType.Peak || filterType == CytomicSvfType.LowShelf || filterType == CytomicSvfType.HighShelf)) {
                useMatchedBiquad = TryComputeVicanekMatchedBiquad(
                    filterType, safeSr, safeFreq, safeQ, safeGainDb,
                    out targetB0, out targetB1, out targetB2, out targetA1, out targetA2);
            }

            if (snapImmediate || smoothingTimeMs <= 0.0 || smoothAlpha >= 1.0) {
                SnapToTargets();
            } else {
                isSmoothing = true;
            }
        }

        static bool TryComputeVicanekMatchedBiquad(
            CytomicSvfType type,
            double sr,
            double f0,
            double qVal,
            double gainDb,
            out double b0,
            out double b1,
            out double b2,
            out double da1,
            out double da2) {
            b0 = 1.0; b1 = 0.0; b2 = 0.0; da1 = 0.0; da2 = 0.0;
            if (Math.Abs(gainDb) < 1e-6) {
                return true;
            }
            double w0 = 2.0 * Math.PI * f0 / sr;
            if (w0 <= 1e-5 || w0 >= Math.PI * 0.985) {
                return false;
            }
            double aSqrt = Math.Pow(10.0, gainDb / 40.0); // sqrt(G)
            double gLin = aSqrt * aSqrt;                   // G = 10^(gainDb/20)
            double r = Math.PI / w0;
            double r2 = r * r;

            double poleOmega;
            double poleAlpha;
            double dcMagSq;
            double w0MagSq;
            double nyqMagSq;

            if (type == CytomicSvfType.Peak) {
                poleOmega = w0;
                poleAlpha = w0 / (2.0 * qVal * aSqrt);
                dcMagSq = 1.0;
                w0MagSq = gLin * gLin;
                double t1 = (1.0 - r2) * (1.0 - r2);
                double numN = t1 + (aSqrt * r / qVal) * (aSqrt * r / qVal);
                double denN = t1 + (r / (aSqrt * qVal)) * (r / (aSqrt * qVal));
                nyqMagSq = denN > 1e-24 ? numN / denN : 1.0;
            } else if (type == CytomicSvfType.HighShelf) {
                poleOmega = Math.Min(w0 * Math.Sqrt(aSqrt), Math.PI * 0.96);
                poleAlpha = poleOmega / (2.0 * qVal);
                dcMagSq = 1.0;
                w0MagSq = gLin;
                double numN = (1.0 - aSqrt * r2) * (1.0 - aSqrt * r2) + (aSqrt * r2 / (qVal * qVal));
                double denN = (aSqrt - r2) * (aSqrt - r2) + (aSqrt * r2 / (qVal * qVal));
                nyqMagSq = denN > 1e-24 ? gLin * numN / denN : gLin * gLin;
            } else {
                // LowShelf
                poleOmega = Math.Min(w0 / Math.Sqrt(aSqrt), Math.PI * 0.96);
                poleAlpha = poleOmega / (2.0 * qVal);
                dcMagSq = gLin * gLin;
                w0MagSq = gLin;
                double numN = (aSqrt - r2) * (aSqrt - r2) + (aSqrt * r2 / (qVal * qVal));
                double denN = (1.0 - aSqrt * r2) * (1.0 - aSqrt * r2) + (aSqrt * r2 / (qVal * qVal));
                nyqMagSq = denN > 1e-24 ? gLin * numN / denN : 1.0;
            }

            // Impulse-invariant pole placement
            double expNeg = Math.Exp(-poleAlpha);
            da2 = expNeg * expNeg;
            if (poleAlpha <= poleOmega) {
                da1 = -2.0 * expNeg * Math.Cos(Math.Sqrt(Math.Max(0.0, poleOmega * poleOmega - poleAlpha * poleAlpha)));
            } else {
                da1 = -2.0 * expNeg * Math.Cosh(Math.Sqrt(Math.Max(0.0, poleAlpha * poleAlpha - poleOmega * poleOmega)));
            }

            double sinHalf = Math.Sin(0.5 * w0);
            double phi1 = sinHalf * sinHalf;
            double phi0 = 1.0 - phi1;
            double phi2 = 4.0 * phi0 * phi1;
            if (phi2 <= 1e-12) {
                return false;
            }

            double A0 = (1.0 + da1 + da2) * (1.0 + da1 + da2);
            double A1 = (1.0 - da1 + da2) * (1.0 - da1 + da2);
            double A2 = -4.0 * da2;
            double denW0 = A0 * phi0 + A1 * phi1 + A2 * phi2;

            double B0 = A0 * dcMagSq;
            double B1 = A1 * nyqMagSq;
            double B2 = (w0MagSq * denW0 - B0 * phi0 - B1 * phi1) / phi2;

            double s0 = Math.Sqrt(Math.Max(0.0, B0));
            double s1 = Math.Sqrt(Math.Max(0.0, B1));
            double wSum = 0.5 * (s0 + s1);
            double disc = wSum * wSum + B2;
            if (disc < 0.0) {
                return false;
            }
            b0 = 0.5 * (wSum + Math.Sqrt(disc));
            b1 = 0.5 * (s0 - s1);
            b2 = wSum - b0;
            return !double.IsNaN(b0) && !double.IsNaN(b1) && !double.IsNaN(b2) &&
                   !double.IsInfinity(b0) && !double.IsInfinity(b1) && !double.IsInfinity(b2);
        }

        void SnapToTargets() {
            s1G = curG = targetG;
            s1K = curK = targetK;
            s1M0 = curM0 = targetM0;
            s1M1 = curM1 = targetM1;
            s1M2 = curM2 = targetM2;
            s1B0 = curB0 = targetB0;
            s1B1 = curB1 = targetB1;
            s1B2 = curB2 = targetB2;
            s1A1 = curA1 = targetA1;
            s1A2 = curA2 = targetA2;
            isSmoothing = false;
            UpdateStaticCoefficients();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void UpdateStaticCoefficients() {
            a1 = 1.0 / (1.0 + curG * (curG + curK));
            a2 = curG * a1;
            a3 = curG * a2;
        }

        const double DenormalGuard = 1e-200;
    }
}
