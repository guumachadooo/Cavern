using System;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Pure-physics atmospheric sound absorption filter implementing ISO 9613-1:1993
    /// (oxygen and nitrogen molecular relaxation + classical viscosity/thermal conduction).
    /// Zero-GC cascaded biquad high-shelf realization with exact unity (0.00 dB) pass-through
    /// at or below <see cref="DirectPathThresholdMeters"/> for direct paths.
    /// </summary>
    public sealed class AirAbsorptionFilter {
        /// <summary>
        /// Direct-path distance threshold (meters) below which direct air absorption is strictly bypassed (0.00 dB).
        /// </summary>
        public const float DirectPathThresholdMeters = 5.0f;

        /// <summary>
        /// Standard ambient temperature in degrees Celsius (ISO 9613-1 reference).
        /// </summary>
        public const float DefaultTemperatureCelsius = 20.0f;

        /// <summary>
        /// Standard ambient relative humidity in percent (ISO 9613-1 reference).
        /// </summary>
        public const float DefaultRelativeHumidityPct = 50.0f;

        /// <summary>
        /// Standard atmospheric pressure in kilopascals (ISO 9613-1 reference: 1 atm = 101.325 kPa).
        /// </summary>
        public const float ReferencePressureKpa = 101.325f;

        sealed class ThreadIsolatedState {
            public bool GlobalEnabled;
            public float GlobalTemperatureCelsius;
            public float GlobalRelativeHumidityPct;
            public System.Numerics.Vector3 PhysicalRoomHalfSize;
        }

        [ThreadStatic]
        static ThreadIsolatedState threadState;

        sealed class ThreadIsolationScope : IDisposable {
            readonly ThreadIsolatedState previous;
            bool disposed;

            public ThreadIsolationScope(ThreadIsolatedState prev) => previous = prev;

            public void Dispose() {
                if (disposed) {
                    return;
                }
                disposed = true;
                threadState = previous;
            }
        }

        /// <summary>
        /// Isolates static atmospheric absorption settings on the calling thread until disposed.
        /// </summary>
        internal static IDisposable BeginThreadIsolatedScope() {
            ThreadIsolatedState prev = threadState;
            threadState = new ThreadIsolatedState {
                GlobalEnabled = GlobalEnabled,
                GlobalTemperatureCelsius = GlobalTemperatureCelsius,
                GlobalRelativeHumidityPct = GlobalRelativeHumidityPct,
                PhysicalRoomHalfSize = PhysicalRoomHalfSize
            };
            return new ThreadIsolationScope(prev);
        }

        /// <summary>
        /// Global enable flag for direct-path ISO 9613-1 air absorption beyond <see cref="DirectPathThresholdMeters"/>.
        /// </summary>
        public static bool GlobalEnabled {
            get => threadState != null ? threadState.GlobalEnabled : globalEnabled;
            set {
                if (threadState != null) {
                    threadState.GlobalEnabled = value;
                } else {
                    globalEnabled = value;
                }
            }
        }
        static bool globalEnabled = true;

        /// <summary>
        /// Global ambient air temperature in degrees Celsius (clamped to -10..45 C).
        /// </summary>
        public static float GlobalTemperatureCelsius {
            get => threadState != null ? threadState.GlobalTemperatureCelsius : globalTemperatureCelsius;
            set {
                float clamped = Math.Clamp(value, -10f, 45f);
                if (threadState != null) {
                    threadState.GlobalTemperatureCelsius = clamped;
                } else {
                    globalTemperatureCelsius = clamped;
                }
            }
        }
        static float globalTemperatureCelsius = DefaultTemperatureCelsius;

        /// <summary>
        /// Global ambient relative humidity in percent (clamped to 10..95 %).
        /// </summary>
        public static float GlobalRelativeHumidityPct {
            get => threadState != null ? threadState.GlobalRelativeHumidityPct : globalRelativeHumidityPct;
            set {
                float clamped = Math.Clamp(value, 10f, 95f);
                if (threadState != null) {
                    threadState.GlobalRelativeHumidityPct = clamped;
                } else {
                    globalRelativeHumidityPct = clamped;
                }
            }
        }
        static float globalRelativeHumidityPct = DefaultRelativeHumidityPct;

        /// <summary>
        /// Physical room half-dimensions (meters) used to map Cavern normalized room coordinates into real meters.
        /// </summary>
        public static System.Numerics.Vector3 PhysicalRoomHalfSize {
            get => threadState != null ? threadState.PhysicalRoomHalfSize : physicalRoomHalfSize;
            set {
                if (threadState != null) {
                    threadState.PhysicalRoomHalfSize = value;
                } else {
                    physicalRoomHalfSize = value;
                }
            }
        }
        static System.Numerics.Vector3 physicalRoomHalfSize = new System.Numerics.Vector3(2.6f, 1.55f, 3.2f);

        // Two cascaded RBJ high-shelf biquads fitted via closed-form 2x2 least squares to exact ISO 9613-1 dB targets.
        const double stage1CenterHz = 4200.0;
        const double stage2CenterHz = 11500.0;
        const double shelfSlope = 0.65;

        static readonly double[] fitFrequenciesHz = new double[] { 1000.0, 2000.0, 4000.0, 8000.0, 12000.0, 16000.0 };

        // Stage 1 biquad coefficients & Direct Form II Transposed states
        double b0_1 = 1.0, b1_1, b2_1, a1_1, a2_1;
        double z1_1, z2_1;

        // Stage 2 biquad coefficients & Direct Form II Transposed states
        double b0_2 = 1.0, b1_2, b2_2, a1_2, a2_2;
        double z1_2, z2_2;

        float configuredDeltaMeters = -1f;
        float configuredTempC = float.NaN;
        float configuredRhPct = float.NaN;
        int configuredSampleRate;
        bool active;

        /// <summary>
        /// True when the filter is currently applying non-zero ISO 9613-1 attenuation.
        /// </summary>
        public bool IsActive => active;

        /// <summary>
        /// Computes the physical speed of sound in air (m/s) as a function of ambient temperature (Celsius).
        /// Returns 343.21 m/s at 20.0 C.
        /// </summary>
        public static double ComputeSpeedOfSound(double temperatureCelsius) {
            double clampedC = Math.Clamp(temperatureCelsius, -40.0, 60.0);
            return 331.3 * Math.Sqrt(1.0 + clampedC / 273.15);
        }

        /// <summary>
        /// Computes the exact ISO 9613-1:1993 pure-tone atmospheric attenuation coefficient in dB/m.
        /// </summary>
        /// <param name="frequencyHz">Acoustic frequency in Hz (&gt; 0)</param>
        /// <param name="temperatureCelsius">Ambient air temperature in degrees Celsius</param>
        /// <param name="relativeHumidityPct">Ambient relative humidity in percent (0..100)</param>
        /// <param name="pressureKpa">Ambient atmospheric pressure in kPa (default 101.325 kPa)</param>
        /// <returns>Attenuation in dB/m (&gt;= 0)</returns>
        public static double ComputeAttenuationDbPerMeter(
            double frequencyHz,
            double temperatureCelsius = DefaultTemperatureCelsius,
            double relativeHumidityPct = DefaultRelativeHumidityPct,
            double pressureKpa = ReferencePressureKpa) {
            if (frequencyHz <= 0.0) {
                return 0.0;
            }
            double tKelvin = Math.Clamp(temperatureCelsius, -40.0, 60.0) + 273.15;
            double rh = Math.Clamp(relativeHumidityPct, 0.01, 100.0);
            double pRatio = Math.Clamp(pressureKpa, 50.0, 120.0) / 101.325;

            const double t0 = 293.15; // Reference air temperature (20 C)
            const double t01 = 273.16; // Triple-point isotherm temperature
            double tRel = tKelvin / t0;

            // ISO 9613-1 Annex B saturation vapor pressure ratio (psat / pr)
            double cSat = -6.8346 * Math.Pow(t01 / tKelvin, 1.261) + 4.6151;
            double psatPr = Math.Pow(10.0, cSat);

            // Molar concentration of water vapor (%)
            double h = rh * (psatPr / pRatio);

            // Oxygen relaxation frequency (Hz)
            double frO = pRatio * (24.0 + 4.04e4 * h * ((0.02 + h) / (0.391 + h)));

            // Nitrogen relaxation frequency (Hz)
            double frN = pRatio * Math.Pow(tRel, -0.5) *
                (9.0 + 280.0 * h * Math.Exp(-4.170 * (Math.Pow(tRel, -1.0 / 3.0) - 1.0)));

            double f2 = frequencyHz * frequencyHz;
            double classical = 1.84e-11 * (1.0 / pRatio) * Math.Sqrt(tRel);
            double vibO = 0.01275 * Math.Exp(-2239.1 / tKelvin) / (frO + f2 / frO);
            double vibN = 0.1068 * Math.Exp(-3352.0 / tKelvin) / (frN + f2 / frN);

            double alphaDbPerMeter = 8.686 * f2 * (classical + Math.Pow(tRel, -2.5) * (vibO + vibN));
            return alphaDbPerMeter > 0.0 ? alphaDbPerMeter : 0.0;
        }

        /// <summary>
        /// Computes linear gain for an extra propagation distance <paramref name="distanceMeters"/> at <paramref name="frequencyHz"/>.
        /// </summary>
        public static double ComputeLinearGain(
            double frequencyHz,
            double distanceMeters,
            double temperatureCelsius = DefaultTemperatureCelsius,
            double relativeHumidityPct = DefaultRelativeHumidityPct) {
            if (distanceMeters <= 0.0 || frequencyHz <= 0.0) {
                return 1.0;
            }
            double db = -ComputeAttenuationDbPerMeter(frequencyHz, temperatureCelsius, relativeHumidityPct) * distanceMeters;
            return Math.Pow(10.0, db * 0.05);
        }

        /// <summary>
        /// Configures the filter for a direct source path (strictly bypassed at or below <see cref="DirectPathThresholdMeters"/>).
        /// Zero heap allocations.
        /// </summary>
        public void ConfigureDirectPath(float distanceMeters, int sampleRate, float temperatureCelsius, float relativeHumidityPct) {
            float delta = distanceMeters - DirectPathThresholdMeters;
            ConfigureDeltaPath(delta, sampleRate, temperatureCelsius, relativeHumidityPct);
        }

        /// <summary>
        /// Configures the filter for an excess propagation distance <paramref name="deltaMeters"/> (&gt; 0 engages ISO 9613-1).
        /// Zero heap allocations.
        /// </summary>
        public void ConfigureDeltaPath(float deltaMeters, int sampleRate, float temperatureCelsius, float relativeHumidityPct) {
            if (deltaMeters <= 0.01f || sampleRate < 8000) {
                if (active) {
                    active = false;
                    configuredDeltaMeters = 0f;
                    Reset();
                }
                return;
            }

            float clampedDelta = Math.Clamp(deltaMeters, 0f, 150f);
            float clampedTemp = Math.Clamp(temperatureCelsius, -10f, 45f);
            float clampedRh = Math.Clamp(relativeHumidityPct, 10f, 95f);

            if (active &&
                configuredSampleRate == sampleRate &&
                MathF.Abs(clampedDelta - configuredDeltaMeters) < 0.15f &&
                MathF.Abs(clampedTemp - configuredTempC) < 0.25f &&
                MathF.Abs(clampedRh - configuredRhPct) < 0.5f) {
                return;
            }

            configuredDeltaMeters = clampedDelta;
            configuredTempC = clampedTemp;
            configuredRhPct = clampedRh;
            configuredSampleRate = sampleRate;

            double nyquist = sampleRate * 0.5;
            double fc1 = Math.Min(stage1CenterHz, nyquist * 0.42);
            double fc2 = Math.Min(stage2CenterHz, nyquist * 0.78);

            // Solve 2-stage high-shelf gains (g1, g2 in dB) matching exact ISO 9613-1 attenuation via 3-step Gauss-Newton.
            double g1 = -ComputeAttenuationDbPerMeter(4000.0, clampedTemp, clampedRh) * clampedDelta;
            double g2 = -ComputeAttenuationDbPerMeter(16000.0, clampedTemp, clampedRh) * clampedDelta - g1;
            g1 = Math.Clamp(g1, -24.0, 0.0);
            g2 = Math.Clamp(g2, -36.0, 0.0);

            for (int iter = 0; iter < 3; iter++) {
                ComputeHighShelf(fc1, g1, sampleRate, out double tb0_1, out double tb1_1, out double tb2_1, out double ta1_1, out double ta2_1);
                ComputeHighShelf(fc2, g2, sampleRate, out double tb0_2, out double tb1_2, out double tb2_2, out double ta1_2, out double ta2_2);

                const double eps = 0.05;
                ComputeHighShelf(fc1, g1 + eps, sampleRate, out double pb0_1, out double pb1_1, out double pb2_1, out double pa1_1, out double pa2_1);
                ComputeHighShelf(fc2, g2 + eps, sampleRate, out double pb0_2, out double pb1_2, out double pb2_2, out double pa1_2, out double pa2_2);

                double h11 = 1e-6, h12 = 0.0, h22 = 1e-6;
                double r1 = 0.0, r2 = 0.0;

                for (int i = 0; i < fitFrequenciesHz.Length; i++) {
                    double f = fitFrequenciesHz[i];
                    if (f >= nyquist * 0.92) {
                        continue;
                    }
                    double targetDb = -ComputeAttenuationDbPerMeter(f, clampedTemp, clampedRh) * clampedDelta;
                    double w = 2.0 * Math.PI * f / sampleRate;
                    double m1 = EvalBiquadMagDb(w, tb0_1, tb1_1, tb2_1, ta1_1, ta2_1);
                    double m2 = EvalBiquadMagDb(w, tb0_2, tb1_2, tb2_2, ta1_2, ta2_2);
                    double err = targetDb - (m1 + m2);

                    double dm1 = (EvalBiquadMagDb(w, pb0_1, pb1_1, pb2_1, pa1_1, pa2_1) - m1) / eps;
                    double dm2 = (EvalBiquadMagDb(w, pb0_2, pb1_2, pb2_2, pa1_2, pa2_2) - m2) / eps;

                    h11 += dm1 * dm1;
                    h12 += dm1 * dm2;
                    h22 += dm2 * dm2;
                    r1 += dm1 * err;
                    r2 += dm2 * err;
                }

                double det = h11 * h22 - h12 * h12;
                if (Math.Abs(det) > 1e-12) {
                    double dg1 = (h22 * r1 - h12 * r2) / det;
                    double dg2 = (h11 * r2 - h12 * r1) / det;
                    g1 = Math.Clamp(g1 + dg1, -24.0, 0.0);
                    g2 = Math.Clamp(g2 + dg2, -36.0, 0.0);
                }
            }

            ComputeHighShelf(fc1, g1, sampleRate, out b0_1, out b1_1, out b2_1, out a1_1, out a2_1);
            ComputeHighShelf(fc2, g2, sampleRate, out b0_2, out b1_2, out b2_2, out a1_2, out a2_2);
            active = true;
        }

        /// <summary>
        /// Processes a mono audio buffer in place with zero heap allocation.
        /// Exact no-op when inactive (0.00 dB pass-through).
        /// </summary>
        public void Process(float[] samples, int offset, int count) {
            if (!active || samples == null || count <= 0) {
                return;
            }
            int end = Math.Min(samples.Length, offset + count);
            double s1z1 = z1_1, s1z2 = z2_1;
            double s2z1 = z1_2, s2z2 = z2_2;
            double lb0_1 = b0_1, lb1_1 = b1_1, lb2_1 = b2_1, la1_1 = a1_1, la2_1 = a2_1;
            double lb0_2 = b0_2, lb1_2 = b1_2, lb2_2 = b2_2, la1_2 = a1_2, la2_2 = a2_2;

            for (int i = offset; i < end; i++) {
                double x = samples[i];
                double y1 = lb0_1 * x + s1z1;
                s1z1 = lb1_1 * x - la1_1 * y1 + s1z2;
                s1z2 = lb2_1 * x - la2_1 * y1;

                double y2 = lb0_2 * y1 + s2z1;
                s2z1 = lb1_2 * y1 - la1_2 * y2 + s2z2;
                s2z2 = lb2_2 * y1 - la2_2 * y2;

                samples[i] = (float)y2;
            }

            // Flush denormals
            z1_1 = Math.Abs(s1z1) > 1e-24 ? s1z1 : 0.0;
            z2_1 = Math.Abs(s1z2) > 1e-24 ? s1z2 : 0.0;
            z1_2 = Math.Abs(s2z1) > 1e-24 ? s2z1 : 0.0;
            z2_2 = Math.Abs(s2z2) > 1e-24 ? s2z2 : 0.0;
        }

        /// <summary>
        /// Evaluates the currently configured cascaded IIR filter magnitude response in dB at <paramref name="frequencyHz"/>.
        /// </summary>
        public double EvaluateResponseDb(double frequencyHz) {
            if (!active || configuredSampleRate <= 0 || frequencyHz <= 0.0) {
                return 0.0;
            }
            double w = 2.0 * Math.PI * frequencyHz / configuredSampleRate;
            return EvalBiquadMagDb(w, b0_1, b1_1, b2_1, a1_1, a2_1) +
                   EvalBiquadMagDb(w, b0_2, b1_2, b2_2, a1_2, a2_2);
        }

        /// <summary>
        /// Clears internal biquad delay registers.
        /// </summary>
        public void Reset() {
            z1_1 = 0.0;
            z2_1 = 0.0;
            z1_2 = 0.0;
            z2_2 = 0.0;
        }

        static void ComputeHighShelf(
            double fc,
            double gainDb,
            int sampleRate,
            out double b0,
            out double b1,
            out double b2,
            out double a1,
            out double a2) {
            if (Math.Abs(gainDb) < 1e-5) {
                b0 = 1.0;
                b1 = 0.0;
                b2 = 0.0;
                a1 = 0.0;
                a2 = 0.0;
                return;
            }
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * fc / sampleRate;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW * 0.5 * Math.Sqrt((a + 1.0 / a) * (1.0 / shelfSlope - 1.0) + 2.0);
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double a0 = (a + 1.0) - (a - 1.0) * cosW + twoSqrtAAlpha;
            double invA0 = 1.0 / a0;
            b0 = (a * ((a + 1.0) + (a - 1.0) * cosW + twoSqrtAAlpha)) * invA0;
            b1 = (-2.0 * a * ((a - 1.0) + (a + 1.0) * cosW)) * invA0;
            b2 = (a * ((a + 1.0) + (a - 1.0) * cosW - twoSqrtAAlpha)) * invA0;
            a1 = (2.0 * ((a - 1.0) - (a + 1.0) * cosW)) * invA0;
            a2 = ((a + 1.0) - (a - 1.0) * cosW - twoSqrtAAlpha) * invA0;
        }

        static double EvalBiquadMagDb(double w, double b0, double b1, double b2, double a1, double a2) {
            double c1 = Math.Cos(w);
            double s1 = Math.Sin(w);
            double c2 = Math.Cos(2.0 * w);
            double s2 = Math.Sin(2.0 * w);
            double nr = b0 + b1 * c1 + b2 * c2;
            double ni = -(b1 * s1 + b2 * s2);
            double dr = 1.0 + a1 * c1 + a2 * c2;
            double di = -(a1 * s1 + a2 * s2);
            double magSq = (nr * nr + ni * ni) / Math.Max(1e-24, dr * dr + di * di);
            return 10.0 * Math.Log10(Math.Max(1e-24, magSq));
        }
    }
}
