using System;

using Cavern.Filters;
using Cavern.Spatial;
using Cavern.Utilities;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Diagnostic metrics produced when designing a Magnitude Least-Squares (MagLS) Ambisonics-to-binaural decoder.
    /// </summary>
    public readonly struct MagLsDesignMetrics {
        /// <summary>Ambisonic spherical harmonic order N (1..5).</summary>
        public readonly int Order;

        /// <summary>Number of spherical harmonic channels <c>(N + 1)^2</c>.</summary>
        public readonly int HarmonicCount;

        /// <summary>Number of measured HRTF directions used in the fit.</summary>
        public readonly int DirectionCount;

        /// <summary>FFT length used to synthesize the MagLS decoding filters.</summary>
        public readonly int FftSize;

        /// <summary>Sample rate in Hz.</summary>
        public readonly int SampleRate;

        /// <summary>MagLS phase-to-magnitude transition cutoff frequency in Hz.</summary>
        public readonly float CutoffFrequencyHz;

        /// <summary>Mean high-frequency diffuse-field magnitude error (dB) above cutoff vs exact measured HRTFs.</summary>
        public readonly float MeanHfMagnitudeErrorDb;

        /// <summary>Maximum high-frequency diffuse-field magnitude error (dB) above cutoff (2..16 kHz) vs exact measured HRTFs.</summary>
        public readonly float MaxHfMagnitudeErrorDb;

        /// <summary>
        /// Creates an immutable MagLS design metrics snapshot.
        /// </summary>
        public MagLsDesignMetrics(
            int order,
            int harmonicCount,
            int directionCount,
            int fftSize,
            int sampleRate,
            float cutoffFrequencyHz,
            float meanHfMagnitudeErrorDb,
            float maxHfMagnitudeErrorDb) {
            Order = order;
            HarmonicCount = harmonicCount;
            DirectionCount = directionCount;
            FftSize = fftSize;
            SampleRate = sampleRate;
            CutoffFrequencyHz = cutoffFrequencyHz;
            MeanHfMagnitudeErrorDb = meanHfMagnitudeErrorDb;
            MaxHfMagnitudeErrorDb = maxHfMagnitudeErrorDb;
        }
    }

    /// <summary>
    /// Synthesized MagLS binaural decoding filter bank in the spherical harmonic domain (ACN/SN3D).
    /// </summary>
    public sealed class MagLsFilterSet {
        /// <summary>Left-ear decoding impulse responses indexed by ACN harmonic index <c>0..(N+1)^2-1</c>.</summary>
        public float[][] LeftFilters { get; }

        /// <summary>Right-ear decoding impulse responses indexed by ACN harmonic index <c>0..(N+1)^2-1</c>.</summary>
        public float[][] RightFilters { get; }

        /// <summary>Design accuracy and configuration diagnostics.</summary>
        public MagLsDesignMetrics Metrics { get; }

        /// <summary>
        /// Creates a synthesized MagLS filter bank.
        /// </summary>
        public MagLsFilterSet(float[][] leftFilters, float[][] rightFilters, MagLsDesignMetrics metrics) {
            LeftFilters = leftFilters;
            RightFilters = rightFilters;
            Metrics = metrics;
        }
    }

    /// <summary>
    /// Magnitude Least-Squares (MagLS) Higher-Order Ambisonics (HOA) to binaural decoder and spherical-harmonic HRIR synthesizer.
    /// Curated from Schörkhuber, Zaunschirm &amp; Höldrich (DAGA 2018), <c>leomccormack/Spatial_Audio_Framework</c> (<c>saf_hoa</c>),
    /// and the <c>IEM Plug-in Suite</c> <c>BinauralDecoder</c>.
    /// Preserves exact interaural phase/ITD below the spatial aliasing cutoff <c>f_c</c> and optimizes spherical-harmonic
    /// magnitude accuracy above <c>f_c</c> with phase continuation and diffuse-field covariance energy equalization.
    /// </summary>
    public sealed class MagLsAmbisonicDecoder {
        /// <summary>Minimum supported Ambisonic order.</summary>
        public const int MinOrder = 1;

        /// <summary>Maximum supported Ambisonic order.</summary>
        public const int MaxOrder = 5;

        /// <summary>Standard effective spherical head radius (m) for spatial aliasing cutoff estimation.</summary>
        public const double DefaultHeadRadiusMeters = 0.0875;

        /// <summary>Reference speed of sound (m/s) at 20 °C.</summary>
        public const double DefaultSpeedOfSound = 343.21;

        const double tikhonovLambda = 1e-4;
        const int magLsIterations = 6;

        readonly int order;
        readonly int harmonicCount;
        readonly int sampleRate;
        readonly PartitionedDualConvolver[] convolvers;
        readonly MagLsDesignMetrics metrics;

        float[] scratchLeft = Array.Empty<float>();
        float[] scratchRight = Array.Empty<float>();

        /// <summary>Ambisonic order N.</summary>
        public int Order => order;

        /// <summary>Number of spherical harmonic channels <c>(N + 1)^2</c>.</summary>
        public int HarmonicCount => harmonicCount;

        /// <summary>Sample rate of the active MagLS decoding filter bank.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Design accuracy metrics for this decoder.</summary>
        public MagLsDesignMetrics Metrics => metrics;

        /// <summary>
        /// Constructs a zero-GC real-time MagLS HOA binaural decoder from a pre-designed <see cref="MagLsFilterSet"/>.
        /// </summary>
        public MagLsAmbisonicDecoder(MagLsFilterSet filterSet) {
            if (filterSet == null) {
                throw new ArgumentNullException(nameof(filterSet));
            }
            metrics = filterSet.Metrics;
            order = metrics.Order;
            harmonicCount = metrics.HarmonicCount;
            sampleRate = metrics.SampleRate;
            convolvers = new PartitionedDualConvolver[harmonicCount];
            for (int q = 0; q < harmonicCount; q++) {
                convolvers[q] = new PartitionedDualConvolver(filterSet.LeftFilters[q], filterSet.RightFilters[q]);
            }
        }

        /// <summary>
        /// Computes the psychoacoustic MagLS phase-to-magnitude transition frequency <c>f_c = N * c / (2 * pi * a)</c>,
        /// bounded to <c>[600, 3000] Hz</c> (Schörkhuber et al. 2018 / SAF <c>saf_hoa</c>).
        /// </summary>
        public static float ComputeDefaultCutoffHz(int order, double headRadiusMeters = DefaultHeadRadiusMeters) {
            int clampedOrder = Math.Max(MinOrder, Math.Min(MaxOrder, order));
            double a = Math.Max(0.065, Math.Min(0.115, headRadiusMeters));
            double fc = (clampedOrder * DefaultSpeedOfSound) / (2.0 * Math.PI * a);
            return (float)Math.Max(600.0, Math.Min(3000.0, fc));
        }

        /// <summary>
        /// Evaluates real spherical harmonic basis functions (ACN channel order, SN3D Schmidt semi-normalization)
        /// for a unit direction specified by counter-clockwise azimuth <paramref name="azimuthRad"/> (0 = front, +pi/2 = left)
        /// and elevation <paramref name="elevationRad"/> (+pi/2 = zenith, -pi/2 = nadir).
        /// </summary>
        public static void EvaluateRealSphericalHarmonicsSn3d(double azimuthRad, double elevationRad, int order, double[] destination) {
            int clampedOrder = Math.Max(0, Math.Min(MaxOrder, order));
            int count = (clampedOrder + 1) * (clampedOrder + 1);
            if (destination == null || destination.Length < count) {
                throw new ArgumentException("Destination buffer is too small for requested Ambisonic order.", nameof(destination));
            }

            double sinEl = Math.Sin(elevationRad);
            double cosEl = Math.Cos(elevationRad);
            double x = Math.Max(-1.0, Math.Min(1.0, sinEl));
            double sqrtOneMinusX2 = Math.Max(0.0, cosEl);

            // Compute un-Condon-Shortley associated Legendre polynomials P_n^m(sin(el)) for 0 <= m <= n <= N
            // Using stable three-term recurrence without Condon-Shortley (-1)^m factor (AmbiX ACN/SN3D convention).
            double[,] p = new double[clampedOrder + 1, clampedOrder + 1];
            p[0, 0] = 1.0;
            for (int m = 1; m <= clampedOrder; m++) {
                p[m, m] = (2.0 * m - 1.0) * sqrtOneMinusX2 * p[m - 1, m - 1];
            }
            for (int m = 0; m < clampedOrder; m++) {
                p[m + 1, m] = (2.0 * m + 1.0) * x * p[m, m];
            }
            for (int m = 0; m <= clampedOrder; m++) {
                for (int n = m + 2; n <= clampedOrder; n++) {
                    p[n, m] = ((2.0 * n - 1.0) * x * p[n - 1, m] - (n + m - 1.0) * p[n - 2, m]) / (n - m);
                }
            }

            double[] cosM = new double[clampedOrder + 1];
            double[] sinM = new double[clampedOrder + 1];
            cosM[0] = 1.0;
            sinM[0] = 0.0;
            for (int m = 1; m <= clampedOrder; m++) {
                cosM[m] = Math.Cos(m * azimuthRad);
                sinM[m] = Math.Sin(m * azimuthRad);
            }

            for (int n = 0; n <= clampedOrder; n++) {
                int centerIdx = n * n + n;
                destination[centerIdx] = p[n, 0];
                for (int m = 1; m <= n; m++) {
                    double norm = Math.Sqrt(2.0 * FactorialRatio(n - m, n + m));
                    double val = norm * p[n, m];
                    destination[centerIdx + m] = val * cosM[m];
                    destination[centerIdx - m] = val * sinM[m];
                }
            }
        }

        /// <summary>
        /// Converts Cavern virtual speaker angles (<paramref name="cavernElevationDeg"/> where -45 = up,
        /// <paramref name="cavernAzimuthDeg"/> where -90 = left, +90 = right) into standard Ambisonic
        /// counter-clockwise azimuth and elevation in radians.
        /// </summary>
        public static void CavernAnglesToAmbisonicRad(
            double cavernElevationDeg,
            double cavernAzimuthDeg,
            out double azimuthRad,
            out double elevationRad) {
            const double degToRad = Math.PI / 180.0;
            azimuthRad = -cavernAzimuthDeg * degToRad;
            elevationRad = -cavernElevationDeg * degToRad;
        }

        /// <summary>
        /// Designs a frequency-domain Magnitude Least-Squares (MagLS) Ambisonic binaural filter bank from a set of
        /// laboratory-measured HRIR pairs at arbitrary spherical directions.
        /// Automatically limits effective spherical-harmonic order when the grid has fewer directions than <c>(N+1)^2</c>.
        /// </summary>
        /// <param name="azimuthsRad">Counter-clockwise azimuth (rad) for each measured direction (0 = front, +pi/2 = left).</param>
        /// <param name="elevationsRad">Elevation (rad) for each measured direction (+pi/2 = zenith).</param>
        /// <param name="leftIrs">Measured left-ear HRIRs [K][taps].</param>
        /// <param name="rightIrs">Measured right-ear HRIRs [K][taps].</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="requestedOrder">Requested Ambisonic order (1..5).</param>
        /// <param name="cutoffHzOverride">Optional MagLS cutoff frequency in Hz (0 = automatic physical cutoff).</param>
        /// <param name="quadratureWeights">Optional positive integration weights per direction (null = elevation-cosine balanced weights).</param>
        public static MagLsFilterSet DesignFromMeasuredHrir(
            double[] azimuthsRad,
            double[] elevationsRad,
            float[][] leftIrs,
            float[][] rightIrs,
            int sampleRate,
            int requestedOrder,
            float cutoffHzOverride = 0f,
            double[] quadratureWeights = null) {
            if (azimuthsRad == null || elevationsRad == null || leftIrs == null || rightIrs == null) {
                throw new ArgumentNullException(nameof(leftIrs));
            }
            int kCount = Math.Min(Math.Min(azimuthsRad.Length, elevationsRad.Length), Math.Min(leftIrs.Length, rightIrs.Length));
            if (kCount < 4) {
                throw new ArgumentException("At least 4 measured HRIR directions are required for MagLS decoding.");
            }

            int order = Math.Max(MinOrder, Math.Min(MaxOrder, requestedOrder));
            while (order > MinOrder && (order + 1) * (order + 1) > kCount) {
                order--;
            }
            int qCount = (order + 1) * (order + 1);

            int maxTap = 0;
            for (int k = 0; k < kCount; k++) {
                if (leftIrs[k] != null && leftIrs[k].Length > maxTap) {
                    maxTap = leftIrs[k].Length;
                }
                if (rightIrs[k] != null && rightIrs[k].Length > maxTap) {
                    maxTap = rightIrs[k].Length;
                }
            }
            int tapLen = Math.Max(64, Math.Min(MinimumPhaseHrir.MaxAnechoicTaps, maxTap));
            int fftSize = 1 << QMath.Log2Ceil(Math.Max(256, tapLen * 2));
            int halfBins = (fftSize >> 1) + 1;

            // Build spherical harmonic design matrix Y [K x Q] and area weights W [K]
            double[,] yMat = new double[kCount, qCount];
            double[] weights = new double[kCount];
            double weightSum = 0.0;
            double[] shRow = new double[qCount];

            for (int k = 0; k < kCount; k++) {
                EvaluateRealSphericalHarmonicsSn3d(azimuthsRad[k], elevationsRad[k], order, shRow);
                for (int q = 0; q < qCount; q++) {
                    yMat[k, q] = shRow[q];
                }
                double w = quadratureWeights != null && k < quadratureWeights.Length && quadratureWeights[k] > 0.0
                    ? quadratureWeights[k]
                    : Math.Max(0.15, Math.Cos(elevationsRad[k]));
                weights[k] = w;
                weightSum += w;
            }
            double invWeightSum = weightSum > 1e-12 ? 1.0 / weightSum : 1.0 / kCount;
            for (int k = 0; k < kCount; k++) {
                weights[k] *= invWeightSum;
            }

            // Compute Tikhonov-regularized weighted pseudoinverse Y_pinv [Q x K] = (Y^T W Y + lambda I)^(-1) Y^T W
            double[,] yPinv = ComputeWeightedPseudoinverse(yMat, weights, kCount, qCount, tikhonovLambda);

            // Transform all measured HRIR pairs to complex frequency domain (exact double-precision FFT via Cavern FFTCache)
            FFTCache fftCache = new ThreadSafeFFTCache(fftSize);
            double[,] hLRe = new double[halfBins, kCount];
            double[,] hLIm = new double[halfBins, kCount];
            double[,] hRRe = new double[halfBins, kCount];
            double[,] hRIm = new double[halfBins, kCount];
            Complex[] specBuf = new Complex[fftSize];

            for (int k = 0; k < kCount; k++) {
                ForwardRealFft(leftIrs[k], fftSize, fftCache, specBuf);
                for (int b = 0; b < halfBins; b++) {
                    hLRe[b, k] = specBuf[b].Real;
                    hLIm[b, k] = specBuf[b].Imaginary;
                }
                ForwardRealFft(rightIrs[k], fftSize, fftCache, specBuf);
                for (int b = 0; b < halfBins; b++) {
                    hRRe[b, k] = specBuf[b].Real;
                    hRIm[b, k] = specBuf[b].Imaginary;
                }
            }

            float cutoffHz = cutoffHzOverride >= 400f && cutoffHzOverride <= 4000f
                ? cutoffHzOverride
                : ComputeDefaultCutoffHz(order);
            double fLow = cutoffHz * 0.75;
            double fHigh = cutoffHz * 1.50;

            double[,] dLRe = new double[qCount, halfBins];
            double[,] dLIm = new double[qCount, halfBins];
            double[,] dRRe = new double[qCount, halfBins];
            double[,] dRIm = new double[qCount, halfBins];

            double[] lsLRe = new double[qCount];
            double[] lsLIm = new double[qCount];
            double[] lsRRe = new double[qCount];
            double[] lsRIm = new double[qCount];

            double[] magLRe = new double[qCount];
            double[] magLIm = new double[qCount];
            double[] magRRe = new double[qCount];
            double[] magRIm = new double[qCount];

            double[] prevLRe = new double[qCount];
            double[] prevLIm = new double[qCount];
            double[] prevRRe = new double[qCount];
            double[] prevRIm = new double[qCount];

            double[] targetLRe = new double[kCount];
            double[] targetLIm = new double[kCount];
            double[] targetRRe = new double[kCount];
            double[] targetRIm = new double[kCount];

            double[] magAbsL = new double[kCount];
            double[] magAbsR = new double[kCount];

            double hfErrSumDb = 0.0;
            double hfErrMaxDb = 0.0;
            int hfErrCount = 0;

            for (int b = 0; b < halfBins; b++) {
                double freqHz = (double)b * sampleRate / fftSize;

                // 1. Standard phase-preserving Least-Squares fit: D_LS = H(f) * Y_pinv^T
                for (int q = 0; q < qCount; q++) {
                    double slRe = 0.0, slIm = 0.0, srRe = 0.0, srIm = 0.0;
                    for (int k = 0; k < kCount; k++) {
                        double p = yPinv[q, k];
                        slRe += hLRe[b, k] * p;
                        slIm += hLIm[b, k] * p;
                        srRe += hRRe[b, k] * p;
                        srIm += hRIm[b, k] * p;
                    }
                    lsLRe[q] = slRe;
                    lsLIm[q] = slIm;
                    lsRRe[q] = srRe;
                    lsRIm[q] = srIm;
                }

                if (b == 0 || b == halfBins - 1 || freqHz <= fLow) {
                    // Pure phase-preserving LS below cutoff (and real-only DC/Nyquist bins)
                    for (int q = 0; q < qCount; q++) {
                        dLRe[q, b] = lsLRe[q];
                        dLIm[q, b] = (b == 0 || b == halfBins - 1) ? 0.0 : lsLIm[q];
                        dRRe[q, b] = lsRRe[q];
                        dRIm[q, b] = (b == 0 || b == halfBins - 1) ? 0.0 : lsRIm[q];
                        prevLRe[q] = dLRe[q, b];
                        prevLIm[q, b] = dLIm[q, b];
                        prevRRe[q] = dRRe[q, b];
                        prevRIm[q, b] = dRIm[q, b];
                    }
                    continue;
                }

                // 2. Above fLow: MagLS with phase continuation from bin (b - 1) + iterative projection
                for (int k = 0; k < kCount; k++) {
                    magAbsL[k] = Math.Sqrt(hLRe[b, k] * hLRe[b, k] + hLIm[b, k] * hLIm[b, k]);
                    magAbsR[k] = Math.Sqrt(hRRe[b, k] * hRRe[b, k] + hRIm[b, k] * hRIm[b, k]);
                }

                // Initialize phase from previous frequency bin rendered onto the spherical grid
                for (int q = 0; q < qCount; q++) {
                    magLRe[q] = prevLRe[q];
                    magLIm[q] = prevLIm[q];
                    magRRe[q] = prevRRe[q];
                    magRIm[q] = prevRIm[q];
                }

                for (int iter = 0; iter < magLsIterations; iter++) {
                    for (int k = 0; k < kCount; k++) {
                        double rlRe = 0.0, rlIm = 0.0, rrRe = 0.0, rrIm = 0.0;
                        for (int q = 0; q < qCount; q++) {
                            double y = yMat[k, q];
                            rlRe += magLRe[q] * y;
                            rlIm += magLIm[q] * y;
                            rrRe += magRRe[q] * y;
                            rrIm += magRIm[q] * y;
                        }
                        double rlNorm = Math.Sqrt(rlRe * rlRe + rlIm * rlIm);
                        double rrNorm = Math.Sqrt(rrRe * rrRe + rrIm * rrIm);

                        if (rlNorm > 1e-12) {
                            double scaleL = magAbsL[k] / rlNorm;
                            targetLRe[k] = rlRe * scaleL;
                            targetLIm[k] = rlIm * scaleL;
                        } else {
                            targetLRe[k] = hLRe[b, k];
                            targetLIm[k] = hLIm[b, k];
                        }

                        if (rrNorm > 1e-12) {
                            double scaleR = magAbsR[k] / rrNorm;
                            targetRRe[k] = rrRe * scaleR;
                            targetRIm[k] = rrIm * scaleR;
                        } else {
                            targetRRe[k] = hRRe[b, k];
                            targetRIm[k] = hRIm[b, k];
                        }
                    }

                    for (int q = 0; q < qCount; q++) {
                        double nlRe = 0.0, nlIm = 0.0, nrRe = 0.0, nrIm = 0.0;
                        for (int k = 0; k < kCount; k++) {
                            double p = yPinv[q, k];
                            nlRe += targetLRe[k] * p;
                            nlIm += targetLIm[k] * p;
                            nrRe += targetRRe[k] * p;
                            nrIm += targetRIm[k] * p;
                        }
                        magLRe[q] = nlRe;
                        magLIm[q] = nlIm;
                        magRRe[q] = nrRe;
                        magRIm[q] = nrIm;
                    }
                }

                // 3. Smooth raised-cosine crossfade between LS and MagLS across [0.75 * fc, 1.50 * fc]
                double blend = freqHz >= fHigh
                    ? 1.0
                    : 0.5 - 0.5 * Math.Cos(Math.PI * (freqHz - fLow) / Math.Max(1.0, fHigh - fLow));
                double invBlend = 1.0 - blend;

                for (int q = 0; q < qCount; q++) {
                    double clRe = invBlend * lsLRe[q] + blend * magLRe[q];
                    double clIm = invBlend * lsLIm[q] + blend * magLIm[q];
                    double crRe = invBlend * lsRRe[q] + blend * magRRe[q];
                    double crIm = invBlend * lsRIm[q] + blend * magRIm[q];
                    dLRe[q, b] = clRe;
                    dLIm[q, b] = clIm;
                    dRRe[q, b] = crRe;
                    dRIm[q, b] = crIm;
                }

                // 4. Diffuse-field covariance energy equalization (SAF / Zaunschirm) above transition
                double refPowerL = 0.0, refPowerR = 0.0;
                double decPowerL = 0.0, decPowerR = 0.0;
                for (int k = 0; k < kCount; k++) {
                    double w = weights[k];
                    refPowerL += w * (magAbsL[k] * magAbsL[k]);
                    refPowerR += w * (magAbsR[k] * magAbsR[k]);

                    double rlRe = 0.0, rlIm = 0.0, rrRe = 0.0, rrIm = 0.0;
                    for (int q = 0; q < qCount; q++) {
                        double y = yMat[k, q];
                        rlRe += dLRe[q, b] * y;
                        rlIm += dLIm[q, b] * y;
                        rrRe += dRRe[q, b] * y;
                        rrIm += dRIm[q, b] * y;
                    }
                    decPowerL += w * (rlRe * rlRe + rlIm * rlIm);
                    decPowerR += w * (rrRe * rrRe + rrIm * rrIm);
                }

                double covGainL = (decPowerL > 1e-18 && refPowerL > 1e-18)
                    ? Math.Sqrt(refPowerL / decPowerL)
                    : 1.0;
                double covGainR = (decPowerR > 1e-18 && refPowerR > 1e-18)
                    ? Math.Sqrt(refPowerR / decPowerR)
                    : 1.0;
                // Blend covariance gain smoothly with MagLS transition and clamp against noise-floor runaway
                covGainL = 1.0 + blend * (Math.Max(0.25, Math.Min(4.0, covGainL)) - 1.0);
                covGainR = 1.0 + blend * (Math.Max(0.25, Math.Min(4.0, covGainR)) - 1.0);

                for (int q = 0; q < qCount; q++) {
                    dLRe[q, b] *= covGainL;
                    dLIm[q, b] *= covGainL;
                    dRRe[q, b] *= covGainR;
                    dRIm[q, b] *= covGainR;
                    prevLRe[q] = dLRe[q, b];
                    prevLIm[q] = dLIm[q, b];
                    prevRRe[q] = dRRe[q, b];
                    prevRIm[q] = dRIm[q, b];
                }

                // Measure post-equalization diffuse-field magnitude error in [max(fHigh, 2000 Hz), min(16000 Hz, 0.42*fs)]
                if (freqHz >= Math.Max(fHigh, 1800.0) && freqHz <= Math.Min(16000.0, sampleRate * 0.42)) {
                    double postPowerL = decPowerL * covGainL * covGainL;
                    double postPowerR = decPowerR * covGainR * covGainR;
                    double errL = Math.Abs(10.0 * Math.Log10(Math.Max(1e-18, postPowerL) / Math.Max(1e-18, refPowerL)));
                    double errR = Math.Abs(10.0 * Math.Log10(Math.Max(1e-18, postPowerR) / Math.Max(1e-18, refPowerR)));
                    double err = 0.5 * (errL + errR);
                    hfErrSumDb += err;
                    if (err > hfErrMaxDb) {
                        hfErrMaxDb = err;
                    }
                    hfErrCount++;
                }
            }

            // Reconstruct causal time-domain decoding filters per harmonic q (L & R)
            int outLen = Math.Min(fftSize, Math.Max(tapLen, 256));
            float[][] leftFilters = new float[qCount][];
            float[][] rightFilters = new float[qCount][];
            for (int q = 0; q < qCount; q++) {
                leftFilters[q] = InverseRealFftToCausalIr(dLRe, dLIm, q, fftSize, outLen, fftCache, specBuf);
                rightFilters[q] = InverseRealFftToCausalIr(dRRe, dRIm, q, fftSize, outLen, fftCache, specBuf);
            }

            float meanErr = hfErrCount > 0 ? (float)(hfErrSumDb / hfErrCount) : 0f;
            MagLsDesignMetrics designMetrics = new MagLsDesignMetrics(
                order,
                qCount,
                kCount,
                fftSize,
                sampleRate,
                cutoffHz,
                meanErr,
                (float)hfErrMaxDb);

            return new MagLsFilterSet(leftFilters, rightFilters, designMetrics);
        }

        /// <summary>
        /// Synthesizes smooth MagLS spherical-harmonic binaural HRIR pairs at Cavern's target virtual speaker angles
        /// from an arbitrary measured spherical grid (e.g. a dense SONICOM/HUTUBS/SADIE SOFA dataset or bundled HRIR set).
        /// </summary>
        public static void SynthesizeVirtualSpeakerHrirs(
            double[] measuredAzimuthsRad,
            double[] measuredElevationsRad,
            float[][] measuredLeftIrs,
            float[][] measuredRightIrs,
            float[] targetCavernElevationsDeg,
            float[] targetCavernAzimuthsDeg,
            int sampleRate,
            int requestedOrder,
            out float[][] synthesizedLeftIrs,
            out float[][] synthesizedRightIrs,
            out MagLsDesignMetrics metrics,
            float cutoffHzOverride = 0f) {
            MagLsFilterSet filterSet = DesignFromMeasuredHrir(
                measuredAzimuthsRad,
                measuredElevationsRad,
                measuredLeftIrs,
                measuredRightIrs,
                sampleRate,
                requestedOrder,
                cutoffHzOverride);
            metrics = filterSet.Metrics;

            int targetCount = Math.Min(targetCavernElevationsDeg.Length, targetCavernAzimuthsDeg.Length);
            int qCount = filterSet.Metrics.HarmonicCount;
            int tapCount = filterSet.LeftFilters[0].Length;
            synthesizedLeftIrs = new float[targetCount][];
            synthesizedRightIrs = new float[targetCount][];
            double[] sh = new double[qCount];

            for (int s = 0; s < targetCount; s++) {
                CavernAnglesToAmbisonicRad(targetCavernElevationsDeg[s], targetCavernAzimuthsDeg[s], out double azRad, out double elRad);
                EvaluateRealSphericalHarmonicsSn3d(azRad, elRad, filterSet.Metrics.Order, sh);

                float[] irL = new float[tapCount];
                float[] irR = new float[tapCount];
                for (int q = 0; q < qCount; q++) {
                    float w = (float)sh[q];
                    if (MathF.Abs(w) < 1e-8f) {
                        continue;
                    }
                    float[] fL = filterSet.LeftFilters[q];
                    float[] fR = filterSet.RightFilters[q];
                    for (int n = 0; n < tapCount; n++) {
                        irL[n] += fL[n] * w;
                        irR[n] += fR[n] * w;
                    }
                }
                synthesizedLeftIrs[s] = irL;
                synthesizedRightIrs[s] = irR;
            }
        }

        /// <summary>
        /// Renders a block of Higher-Order Ambisonics (HOA) channels directly to binaural stereo (L/R)
        /// using zero-GC partitioned dual convolution (1 FFT per SH channel for both ears).
        /// Supports ACN/SN3D, ACN/N3D, and 1st-order FuMa input conventions.
        /// </summary>
        public void ProcessAmbisonicBlock(
            float[][] hoaInput,
            float[] leftOut,
            float[] rightOut,
            int frameCount,
            HoaChannelOrder channelOrder = HoaChannelOrder.Acn,
            HoaNormalization normalization = HoaNormalization.Sn3d) {
            if (hoaInput == null || leftOut == null || rightOut == null || frameCount <= 0) {
                return;
            }
            if (scratchLeft.Length != frameCount) {
                scratchLeft = new float[frameCount];
                scratchRight = new float[frameCount];
            }

            Array.Clear(leftOut, 0, Math.Min(frameCount, leftOut.Length));
            Array.Clear(rightOut, 0, Math.Min(frameCount, rightOut.Length));

            int available = Math.Min(harmonicCount, hoaInput.Length);
            for (int q = 0; q < available; q++) {
                int srcIdx = MapChannelIndex(q, channelOrder, hoaInput.Length);
                if (srcIdx < 0 || hoaInput[srcIdx] == null) {
                    continue;
                }
                float normScale = GetNormalizationScaleToSn3d(q, normalization);
                float[] src = hoaInput[srcIdx];
                int copyLen = Math.Min(frameCount, src.Length);
                for (int i = 0; i < copyLen; i++) {
                    scratchLeft[i] = src[i] * normScale;
                }
                for (int i = copyLen; i < frameCount; i++) {
                    scratchLeft[i] = 0f;
                }

                convolvers[q].Process(scratchLeft, scratchRight);
                for (int i = 0; i < frameCount; i++) {
                    leftOut[i] += scratchLeft[i];
                    rightOut[i] += scratchRight[i];
                }
            }
        }

        /// <summary>
        /// Resets all internal partitioned convolution delay lines cleanly (on seek or stream switch).
        /// </summary>
        public void Reset() {
            for (int q = 0; q < convolvers.Length; q++) {
                convolvers[q].Reset();
            }
        }

        static int MapChannelIndex(int acnIndex, HoaChannelOrder order, int inputCount) {
            if (order == HoaChannelOrder.FuMa && acnIndex < 4 && inputCount >= 4) {
                // FuMa 1st order: [0]=W, [1]=X, [2]=Y, [3]=Z -> ACN: [0]=W, [1]=Y, [2]=Z, [3]=X
                switch (acnIndex) {
                    case 0: return 0; // W
                    case 1: return 2; // Y
                    case 2: return 3; // Z
                    case 3: return 1; // X
                }
            }
            return acnIndex < inputCount ? acnIndex : -1;
        }

        static float GetNormalizationScaleToSn3d(int acnIndex, HoaNormalization normalization) {
            if (normalization == HoaNormalization.N3d) {
                int n = (int)Math.Floor(Math.Sqrt(acnIndex));
                return 1f / MathF.Sqrt(2f * n + 1f);
            }
            if (normalization == HoaNormalization.FuMa) {
                return acnIndex == 0 ? 1.41421356f : 1f;
            }
            return 1f;
        }

        static double FactorialRatio(int num, int den) {
            double result = 1.0;
            for (int i = num + 1; i <= den; i++) {
                result /= i;
            }
            return result;
        }

        /// <summary>
        /// Computes <c>(Y^T W Y + lambda I)^(-1) Y^T W</c> using double-precision partial-pivoting Gauss-Jordan inversion.
        /// </summary>
        static double[,] ComputeWeightedPseudoinverse(double[,] yMat, double[] weights, int kCount, int qCount, double lambda) {
            double[,] gram = new double[qCount, qCount];
            for (int r = 0; r < qCount; r++) {
                for (int c = r; c < qCount; c++) {
                    double sum = 0.0;
                    for (int k = 0; k < kCount; k++) {
                        sum += yMat[k, r] * weights[k] * yMat[k, c];
                    }
                    gram[r, c] = sum;
                    gram[c, r] = sum;
                }
                gram[r, r] += lambda;
            }

            double[,] invGram = InvertSymmetricPositiveDefinite(gram, qCount);
            double[,] pinv = new double[qCount, kCount];
            for (int r = 0; r < qCount; r++) {
                for (int k = 0; k < kCount; k++) {
                    double sum = 0.0;
                    double wk = weights[k];
                    for (int c = 0; c < qCount; c++) {
                        sum += invGram[r, c] * yMat[k, c];
                    }
                    pinv[r, k] = sum * wk;
                }
            }
            return pinv;
        }

        static double[,] InvertSymmetricPositiveDefinite(double[,] mat, int n) {
            double[,] aug = new double[n, 2 * n];
            for (int i = 0; i < n; i++) {
                for (int j = 0; j < n; j++) {
                    aug[i, j] = mat[i, j];
                }
                aug[i, n + i] = 1.0;
            }

            for (int col = 0; col < n; col++) {
                int pivotRow = col;
                double maxAbs = Math.Abs(aug[col, col]);
                for (int row = col + 1; row < n; row++) {
                    double v = Math.Abs(aug[row, col]);
                    if (v > maxAbs) {
                        maxAbs = v;
                        pivotRow = row;
                    }
                }
                if (pivotRow != col) {
                    for (int j = 0; j < 2 * n; j++) {
                        double tmp = aug[col, j];
                        aug[col, j] = aug[pivotRow, j];
                        aug[pivotRow, j] = tmp;
                    }
                }

                double pivot = aug[col, col];
                if (Math.Abs(pivot) < 1e-14) {
                    pivot = pivot >= 0.0 ? 1e-14 : -1e-14;
                }
                double invPivot = 1.0 / pivot;
                for (int j = 0; j < 2 * n; j++) {
                    aug[col, j] *= invPivot;
                }

                for (int row = 0; row < n; row++) {
                    if (row == col) {
                        continue;
                    }
                    double factor = aug[row, col];
                    if (Math.Abs(factor) < 1e-18) {
                        continue;
                    }
                    for (int j = 0; j < 2 * n; j++) {
                        aug[row, j] -= factor * aug[col, j];
                    }
                }
            }

            double[,] inv = new double[n, n];
            for (int i = 0; i < n; i++) {
                for (int j = 0; j < n; j++) {
                    inv[i, j] = aug[i, n + j];
                }
            }
            return inv;
        }

        static void ForwardRealFft(float[] ir, int fftSize, FFTCache cache, Complex[] buf) {
            int copyLen = ir != null ? Math.Min(ir.Length, fftSize) : 0;
            for (int i = 0; i < copyLen; i++) {
                buf[i] = new Complex(ir[i], 0f);
            }
            for (int i = copyLen; i < fftSize; i++) {
                buf[i] = new Complex(0f, 0f);
            }
            buf.InPlaceFFT(cache);
        }

        static float[] InverseRealFftToCausalIr(
            double[,] specRe,
            double[,] specIm,
            int harmonicIndex,
            int fftSize,
            int outLength,
            FFTCache cache,
            Complex[] buf) {
            int half = fftSize >> 1;
            buf[0] = new Complex((float)specRe[harmonicIndex, 0], 0f);
            for (int b = 1; b < half; b++) {
                float re = (float)specRe[harmonicIndex, b];
                float im = (float)specIm[harmonicIndex, b];
                buf[b] = new Complex(re, im);
                buf[fftSize - b] = new Complex(re, -im);
            }
            buf[half] = new Complex((float)specRe[harmonicIndex, half], 0f);
            buf.InPlaceIFFT(cache);

            float[] ir = new float[outLength];
            int fadeSamples = Math.Min(16, outLength / 8);
            int fadeStart = outLength - fadeSamples;
            for (int i = 0; i < outLength; i++) {
                float v = buf[i].Real;
                if (i >= fadeStart && fadeSamples > 0) {
                    float t = (float)(i - fadeStart) / fadeSamples;
                    v *= 0.5f * (1f + MathF.Cos(MathF.PI * t));
                }
                ir[i] = float.IsFinite(v) ? v : 0f;
            }
            return ir;
        }
    }
}
