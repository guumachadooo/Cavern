using System;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Pure-physics Duda &amp; Martens (1998) spherical-head diffraction Distance Variation Function (DVF)
    /// for near-field sound sources (&lt; 1.0 m). Computes exact Legendre-Hankel head scattering relative to
    /// the active HRIR's measurement distance (<see cref="VirtualizerFilter.ReferenceDistance"/>) so near-field
    /// TH Köln HRIRs are never double-compensated, with guaranteed 0.00 dB unity bypass at &gt;= 1.0 m.
    /// </summary>
    public sealed class NearFieldHeadFilter {
        /// <summary>
        /// Distance threshold (meters) below which spherical-head near-field DVF engages.
        /// Strictly unity (0.00 dB) at or above this distance.
        /// </summary>
        public const float NearFieldThresholdMeters = 1.0f;

        /// <summary>
        /// Upper start of the C1 smooth Hermite taper into unity at <see cref="NearFieldThresholdMeters"/>.
        /// </summary>
        const float taperStartMeters = 0.85f;

        /// <summary>
        /// Standard adult head radius in meters (Algazi / Duda &amp; Martens 1998: 8.75 cm).
        /// </summary>
        public const double DefaultHeadRadiusMeters = 0.0875;

        /// <summary>
        /// Minimum clamped acoustic range (meters) to keep the source outside the head surface (<c>r &gt; 1.8 * a</c>).
        /// </summary>
        public const double MinSourceDistanceMeters = 0.16;

        const double shelfCornerHz = 900.0;
        const double shelfSlope = 0.65;

        // Per-ear biquad (low-shelf + HF broadband gain) in Direct Form II Transposed (zero GC)
        double lB0 = 1.0, lB1, lB2, lA1, lA2, lZ1, lZ2;
        double rB0 = 1.0, rB1, rB2, rA1, rA2, rZ1, rZ2;

        float configuredDistance = float.NaN;
        float configuredRefDistance = float.NaN;
        float configuredCosRight = float.NaN;
        int configuredSampleRate;
        bool active;

        /// <summary>
        /// True when near-field DVF filtering is actively modifying the stereo ear pair.
        /// </summary>
        public bool IsActive => active;

        /// <summary>
        /// Computes the exact Duda &amp; Martens (1998) spherical-head acoustic transfer function magnitude
        /// <c>|psi(rho, theta, mu)|</c> via overflow-free spherical Hankel ratio recurrence.
        /// </summary>
        /// <param name="rho">Normalized distance <c>r / a</c> (&gt; 1.0)</param>
        /// <param name="cosTheta">Cosine of angle between source direction and ear normal (<c>-1..+1</c>, +1 = ipsilateral)</param>
        /// <param name="mu">Normalized frequency <c>2 * pi * f * a / c</c> (&gt; 0)</param>
        /// <param name="maxOrder">Maximum Legendre-Hankel harmonic order (default 40)</param>
        /// <returns>Dimensionless pressure magnitude relative to free-field pressure at the head center</returns>
        public static double ComputeSphereTransferMagnitude(double rho, double cosTheta, double mu, int maxOrder = 40) {
            if (rho <= 1.01) {
                rho = 1.01;
            }
            if (mu <= 1e-4) {
                mu = 1e-4;
            }
            cosTheta = Math.Clamp(cosTheta, -1.0, 1.0);

            double z1 = mu;
            double z2 = mu * rho;

            // Complex arithmetic without System.Numerics.Complex allocations:
            // Q_m(z) = h_m^(2)(z) / h_{m-1}^(2)(z), Q_1(z) = 1/z + i
            double q1Re = 1.0 / z1;
            double q1Im = 1.0;
            double q2Re = 1.0 / z2;
            double q2Im = 1.0;

            // ratio_m = h_m(z2) / h_m(z1) (omitting common m-independent phase factor exp(-i*mu*(rho-1)))
            // At m=0: ratio_0 = 1 / rho
            double ratioRe = 1.0 / rho;
            double ratioIm = 0.0;

            // m = 0 term: (2*0 + 1) * P_0(x) * ratio_0 / (-Q_1(z1))
            ComplexDiv(ratioRe, ratioIm, -q1Re, -q1Im, out double termRe, out double termIm);
            double sumRe = termRe;
            double sumIm = termIm;

            double pPrev2 = 1.0;
            double pPrev1 = cosTheta;
            double prevAddMagSq = double.PositiveInfinity;
            int minConvergenceOrder = Math.Max(8, (int)Math.Ceiling(mu * 1.5) + 6);

            for (int m = 1; m <= maxOrder; m++) {
                if (m > 1) {
                    // Q_m(z) = (2m - 1)/z - 1 / Q_{m-1}(z)
                    ComplexInv(q1Re, q1Im, out double inv1Re, out double inv1Im);
                    q1Re = (2.0 * m - 1.0) / z1 - inv1Re;
                    q1Im = -inv1Im;

                    ComplexInv(q2Re, q2Im, out double inv2Re, out double inv2Im);
                    q2Re = (2.0 * m - 1.0) / z2 - inv2Re;
                    q2Im = -inv2Im;
                }

                // ratio_m = ratio_{m-1} * (Q_m(z2) / Q_m(z1))
                ComplexDiv(q2Re, q2Im, q1Re, q1Im, out double stepRe, out double stepIm);
                double nextRatioRe = ratioRe * stepRe - ratioIm * stepIm;
                double nextRatioIm = ratioRe * stepIm + ratioIm * stepRe;
                ratioRe = nextRatioRe;
                ratioIm = nextRatioIm;

                double pm;
                if (m == 1) {
                    pm = pPrev1;
                } else {
                    pm = ((2.0 * m - 1.0) * cosTheta * pPrev1 - (m - 1.0) * pPrev2) / m;
                    pPrev2 = pPrev1;
                    pPrev1 = pm;
                }

                // denom = h_m'(z1) / h_m(z1) = 1 / Q_m(z1) - (m + 1) / z1
                ComplexInv(q1Re, q1Im, out double invQ1Re, out double invQ1Im);
                double denRe = invQ1Re - (m + 1.0) / z1;
                double denIm = invQ1Im;

                ComplexDiv(ratioRe, ratioIm, denRe, denIm, out double fracRe, out double fracIm);
                double scale = (2.0 * m + 1.0) * pm;
                double addRe = scale * fracRe;
                double addIm = scale * fracIm;

                sumRe += addRe;
                sumIm += addIm;

                double addMagSq = addRe * addRe + addIm * addIm;
                double sumMagSq = sumRe * sumRe + sumIm * sumIm;
                if (m >= minConvergenceOrder && Math.Max(addMagSq, prevAddMagSq) < 1e-16 * Math.Max(1.0, sumMagSq)) {
                    break;
                }
                prevAddMagSq = addMagSq;
            }

            // |psi(rho, theta, mu)| = (rho / mu) * |sum|? Notice ratio_0 already divided by rho, so |psi| = (rho / mu) * |sum| = (1 / mu) * rho * |sum|
            // Let's check m=0 limit as mu->0: ratio_0 = 1/rho, |Q_1| = 1/mu => |sum| -> mu/rho => (rho/mu)*|sum| -> 1.0!
            double mag = (rho / mu) * Math.Sqrt(sumRe * sumRe + sumIm * sumIm);
            return mag > 1e-6 ? mag : 1e-6;
        }

        /// <summary>
        /// Computes the exact linear Distance Variation Function (DVF) gain for one ear at <paramref name="distanceMeters"/>
        /// relative to <paramref name="refDistanceMeters"/>, with C1 smooth blend to exact unity (1.0) at &gt;= 1.0 m
        /// and automatic bypass whenever <paramref name="distanceMeters"/> &gt;= <paramref name="refDistanceMeters"/>.
        /// </summary>
        public static double ComputeDvfGainLinear(
            double distanceMeters,
            double refDistanceMeters,
            double cosTheta,
            double frequencyHz,
            double headRadiusMeters = DefaultHeadRadiusMeters,
            double speedOfSound = 343.21) {
            if (distanceMeters >= NearFieldThresholdMeters || refDistanceMeters <= 0.0 || distanceMeters >= refDistanceMeters) {
                return 1.0;
            }
            double r = Math.Max(MinSourceDistanceMeters, distanceMeters);
            double rRef = Math.Max(r + 0.01, refDistanceMeters);
            double a = headRadiusMeters > 0.04 ? headRadiusMeters : DefaultHeadRadiusMeters;
            double c = speedOfSound > 200.0 ? speedOfSound : 343.21;
            double mu = 2.0 * Math.PI * Math.Max(20.0, frequencyHz) * a / c;

            double nearMag = ComputeSphereTransferMagnitude(r / a, cosTheta, mu);
            double refMag = ComputeSphereTransferMagnitude(rRef / a, cosTheta, mu);
            double rawDb = 20.0 * Math.Log10(Math.Max(1e-6, nearMag / refMag));

            // Smooth C1 Hermite taper to exact 0.00 dB between 0.85 m and 1.00 m
            double weight = 1.0;
            if (r > taperStartMeters) {
                double t = (NearFieldThresholdMeters - r) / (NearFieldThresholdMeters - taperStartMeters);
                t = Math.Clamp(t, 0.0, 1.0);
                weight = t * t * (3.0 - 2.0 * t);
            }

            double finalDb = Math.Clamp(rawDb * weight, -16.0, 14.0);
            return Math.Pow(10.0, finalDb * 0.05);
        }

        /// <summary>
        /// Processes a stereo ear pair (already HRIR-convolved for a virtual channel) with exact near-field DVF.
        /// Zero GC allocations; exact unity bypass when <paramref name="distanceMeters"/> &gt;= 1.0 m or &gt;= <paramref name="refDistanceMeters"/>.
        /// </summary>
        /// <param name="leftEar">Left-ear channel samples</param>
        /// <param name="rightEar">Right-ear channel samples</param>
        /// <param name="count">Sample count to process</param>
        /// <param name="distanceMeters">Physical source distance in meters</param>
        /// <param name="refDistanceMeters">Active HRIR measurement distance in meters</param>
        /// <param name="cosThetaRight">Unit X component of head-relative source direction (+1 = right ear, -1 = left ear)</param>
        /// <param name="sampleRate">Audio sample rate in Hz</param>
        /// <param name="speedOfSound">Physical speed of sound in m/s</param>
        public void ProcessStereo(
            float[] leftEar,
            float[] rightEar,
            int count,
            float distanceMeters,
            float refDistanceMeters,
            float cosThetaRight,
            int sampleRate,
            double speedOfSound) {
            if (leftEar == null || rightEar == null || count <= 0) {
                return;
            }
            if (distanceMeters >= NearFieldThresholdMeters || refDistanceMeters <= 0f || distanceMeters >= refDistanceMeters || sampleRate < 8000) {
                if (active) {
                    active = false;
                    configuredDistance = float.NaN;
                    Reset();
                }
                return;
            }

            UpdateCoefficientsIfNeeded(distanceMeters, refDistanceMeters, cosThetaRight, sampleRate, speedOfSound);
            if (!active) {
                return;
            }

            int n = Math.Min(count, Math.Min(leftEar.Length, rightEar.Length));
            double lz1 = lZ1, lz2 = lZ2;
            double rz1 = rZ1, rz2 = rZ2;
            double lb0 = lB0, lb1 = lB1, lb2 = lB2, la1 = lA1, la2 = lA2;
            double rb0 = rB0, rb1 = rB1, rb2 = rB2, ra1 = rA1, ra2 = rA2;

            for (int i = 0; i < n; i++) {
                double xl = leftEar[i];
                double yl = lb0 * xl + lz1;
                lz1 = lb1 * xl - la1 * yl + lz2;
                lz2 = lb2 * xl - la2 * yl;
                leftEar[i] = (float)yl;

                double xr = rightEar[i];
                double yr = rb0 * xr + rz1;
                rz1 = rb1 * xr - ra1 * yr + rz2;
                rz2 = rb2 * xr - ra2 * yr;
                rightEar[i] = (float)yr;
            }

            lZ1 = Math.Abs(lz1) > 1e-24 ? lz1 : 0.0;
            lZ2 = Math.Abs(lz2) > 1e-24 ? lz2 : 0.0;
            rZ1 = Math.Abs(rz1) > 1e-24 ? rz1 : 0.0;
            rZ2 = Math.Abs(rz2) > 1e-24 ? rz2 : 0.0;
        }

        /// <summary>
        /// Clears internal per-ear filter registers.
        /// </summary>
        public void Reset() {
            lZ1 = 0.0;
            lZ2 = 0.0;
            rZ1 = 0.0;
            rZ2 = 0.0;
        }

        void UpdateCoefficientsIfNeeded(
            float distanceMeters,
            float refDistanceMeters,
            float cosThetaRight,
            int sampleRate,
            double speedOfSound) {
            if (active &&
                configuredSampleRate == sampleRate &&
                MathF.Abs(distanceMeters - configuredDistance) < 0.015f &&
                MathF.Abs(refDistanceMeters - configuredRefDistance) < 0.01f &&
                MathF.Abs(cosThetaRight - configuredCosRight) < 0.02f) {
                return;
            }

            configuredDistance = distanceMeters;
            configuredRefDistance = refDistanceMeters;
            configuredCosRight = cosThetaRight;
            configuredSampleRate = sampleRate;

            double cosR = Math.Clamp(cosThetaRight, -1.0, 1.0);
            double cosL = -cosR;

            // Evaluate exact Duda & Martens DVF at low frequency (200 Hz) and high frequency (4000 Hz)
            double lfL = ComputeDvfGainLinear(distanceMeters, refDistanceMeters, cosL, 200.0, DefaultHeadRadiusMeters, speedOfSound);
            double hfL = ComputeDvfGainLinear(distanceMeters, refDistanceMeters, cosL, 4000.0, DefaultHeadRadiusMeters, speedOfSound);
            double lfR = ComputeDvfGainLinear(distanceMeters, refDistanceMeters, cosR, 200.0, DefaultHeadRadiusMeters, speedOfSound);
            double hfR = ComputeDvfGainLinear(distanceMeters, refDistanceMeters, cosR, 4000.0, DefaultHeadRadiusMeters, speedOfSound);

            double lfDbL = 20.0 * Math.Log10(Math.Max(1e-6, lfL));
            double hfDbL = 20.0 * Math.Log10(Math.Max(1e-6, hfL));
            double lfDbR = 20.0 * Math.Log10(Math.Max(1e-6, lfR));
            double hfDbR = 20.0 * Math.Log10(Math.Max(1e-6, hfR));

            if (Math.Abs(lfDbL) < 0.02 && Math.Abs(hfDbL) < 0.02 && Math.Abs(lfDbR) < 0.02 && Math.Abs(hfDbR) < 0.02) {
                active = false;
                Reset();
                return;
            }

            // Realize exact ear transfer as broadband HF gain + low-shelf delta (lfDb - hfDb) at 900 Hz
            BuildLowShelfWithBroadband(shelfCornerHz, lfDbL - hfDbL, hfL, sampleRate, out lB0, out lB1, out lB2, out lA1, out lA2);
            BuildLowShelfWithBroadband(shelfCornerHz, lfDbR - hfDbR, hfR, sampleRate, out rB0, out rB1, out rB2, out rA1, out rA2);
            active = true;
        }

        static void BuildLowShelfWithBroadband(
            double fc,
            double shelfGainDb,
            double broadbandLinear,
            int sampleRate,
            out double b0,
            out double b1,
            out double b2,
            out double a1,
            out double a2) {
            if (Math.Abs(shelfGainDb) < 1e-4) {
                b0 = broadbandLinear;
                b1 = 0.0;
                b2 = 0.0;
                a1 = 0.0;
                a2 = 0.0;
                return;
            }
            double a = Math.Pow(10.0, shelfGainDb / 40.0);
            double w0 = 2.0 * Math.PI * Math.Min(fc, sampleRate * 0.4) / sampleRate;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW * 0.5 * Math.Sqrt((a + 1.0 / a) * (1.0 / shelfSlope - 1.0) + 2.0);
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1.0) + (a - 1.0) * cosW + twoSqrtAAlpha;
            double invA0 = 1.0 / a0;
            b0 = broadbandLinear * (a * ((a + 1.0) - (a - 1.0) * cosW + twoSqrtAAlpha)) * invA0;
            b1 = broadbandLinear * (2.0 * a * ((a - 1.0) - (a + 1.0) * cosW)) * invA0;
            b2 = broadbandLinear * (a * ((a + 1.0) - (a - 1.0) * cosW - twoSqrtAAlpha)) * invA0;
            a1 = (-2.0 * ((a - 1.0) + (a + 1.0) * cosW)) * invA0;
            a2 = ((a + 1.0) + (a - 1.0) * cosW - twoSqrtAAlpha) * invA0;
        }

        static void ComplexInv(double re, double im, out double outRe, out double outIm) {
            double denom = re * re + im * im;
            if (denom < 1e-30) {
                outRe = 0.0;
                outIm = 0.0;
                return;
            }
            outRe = re / denom;
            outIm = -im / denom;
        }

        static void ComplexDiv(double aRe, double aIm, double bRe, double bIm, out double outRe, out double outIm) {
            double denom = bRe * bRe + bIm * bIm;
            if (denom < 1e-30) {
                outRe = 0.0;
                outIm = 0.0;
                return;
            }
            outRe = (aRe * bRe + aIm * bIm) / denom;
            outIm = (aIm * bRe - aRe * bIm) / denom;
        }
    }
}
