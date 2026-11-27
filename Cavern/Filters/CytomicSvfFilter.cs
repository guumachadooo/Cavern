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

        int sampleRate;
        CytomicSvfType filterType;
        double centerFreq;
        double q;
        double gain;
        double smoothingTimeMs;

        // ZDF-SVF integrator capacitor states
        double ic1eq, ic2eq;

        // Target SVF parameters
        double targetG, targetK, targetM0, targetM1, targetM2;

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
        /// Evaluate the exact continuous-time bilinear SVF magnitude response at <paramref name="freqHz"/>.
        /// </summary>
        public double EvaluateMagnitude(double freqHz) {
            if (freqHz <= 0.0 || sampleRate <= 0) {
                freqHz = 0.0;
            }
            double w = Math.Min(freqHz, sampleRate * 0.495);
            double omega = Math.Tan(Math.PI * w / sampleRate) / Math.Max(1e-12, targetG);
            double omegaSq = omega * omega;
            double numReal = targetM0 + targetM2 - targetM0 * omegaSq;
            double numImag = (targetM0 * targetK + targetM1) * omega;
            double denReal = 1.0 - omegaSq;
            double denImag = targetK * omega;
            double denMagSq = denReal * denReal + denImag * denImag;
            return denMagSq > 1e-24
                ? Math.Sqrt((numReal * numReal + numImag * numImag) / denMagSq)
                : 1.0;
        }

        /// <inheritdoc/>
        public void Reset() {
            ic1eq = 0.0;
            ic2eq = 0.0;
            hasProcessed = false;
            SnapToTargets();
        }

        /// <inheritdoc/>
        public override void Process(float[] samples) => ProcessRange(samples, 0, 1);

        /// <inheritdoc/>
        public override void Process(float[] samples, int channel, int channels) => ProcessRange(samples, channel, channels);

        /// <inheritdoc/>
        public override object Clone() => new CytomicSvfFilter(sampleRate, filterType, centerFreq, q, gain, smoothingTimeMs);

        /// <summary>
        /// Create a copy of this filter targeting a different <paramref name="sampleRate"/>.
        /// </summary>
        public object Clone(int sampleRate) => new CytomicSvfFilter(sampleRate, filterType, centerFreq, q, gain, smoothingTimeMs);

        /// <summary>
        /// Process samples at the given starting offset and channel stride with zero heap allocations.
        /// </summary>
        void ProcessRange(float[] samples, int start, int step) {
            if (samples == null || samples.Length == 0 || start < 0 || step <= 0 || start >= samples.Length) {
                return;
            }

            hasProcessed = true;
            double c1 = ic1eq;
            double c2 = ic2eq;

            if (!isSmoothing) {
                double ca1 = a1, ca2 = a2, ca3 = a3;
                double cm0 = curM0, cm1 = curM1, cm2 = curM2;
                for (int i = start; i < samples.Length; i += step) {
                    samples[i] = (float)StepSvf(samples[i], ca1, ca2, ca3, cm0, cm1, cm2, ref c1, ref c2);
                }
            } else {
                double alpha = smoothAlpha;
                double tG = targetG, tK = targetK, tM0 = targetM0, tM1 = targetM1, tM2 = targetM2;
                double ls1G = s1G, ls1K = s1K, ls1M0 = s1M0, ls1M1 = s1M1, ls1M2 = s1M2;
                double lG = curG, lK = curK, lM0 = curM0, lM1 = curM1, lM2 = curM2;

                for (int i = start; i < samples.Length; i += step) {
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

            if (snapImmediate || smoothingTimeMs <= 0.0 || smoothAlpha >= 1.0) {
                SnapToTargets();
            } else {
                isSmoothing = true;
            }
        }

        void SnapToTargets() {
            s1G = curG = targetG;
            s1K = curK = targetK;
            s1M0 = curM0 = targetM0;
            s1M1 = curM1 = targetM1;
            s1M2 = curM2 = targetM2;
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
