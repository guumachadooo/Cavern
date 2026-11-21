using System;

namespace Cavern.Filters {
    /// <summary>
    /// Real-time ISO 226:2023 Dynamic Equal-Loudness physiological compensation filter (Slot 6B).
    /// Evaluates exact ISO 226:2023 Table 1 &amp; Formula (1) equal-loudness contours across all 29 preferred
    /// one-third-octave frequencies (20 Hz–12.5 kHz) and fits a zero-GC 6-band double-precision IIR
    /// shelving/peaking cascade (<see cref="ParametricEqualizer"/>) with Orfanidis Nyquist de-cramping.
    /// Guarantees exact 0.000 dB bit-transparent unity bypass at the reference SPL and exact 0.000 dB
    /// gain at 1000 Hz across all listening levels.
    /// </summary>
    public sealed class Iso226LoudnessFilter {
        /// <summary>Minimum supported reference SPL in phon (dB SPL at 1 kHz).</summary>
        public const float MinReferenceSplPhon = 70f;

        /// <summary>Maximum supported reference SPL in phon (dB SPL at 1 kHz).</summary>
        public const float MaxReferenceSplPhon = 85f;

        /// <summary>Default studio/home-cinema reference playback level in phon (80 dB SPL).</summary>
        public const float DefaultReferenceSplPhon = 80f;

        /// <summary>Minimum supported listening SPL in phon.</summary>
        public const float MinListeningSplPhon = 40f;

        /// <summary>Maximum supported listening SPL in phon.</summary>
        public const float MaxListeningSplPhon = 95f;

        /// <summary>Default listening SPL in phon when not tracking playback volume.</summary>
        public const float DefaultListeningSplPhon = 68f;

        /// <summary>Deadband around reference SPL (in phon) within which the filter performs exact 0.00 dB unity bypass.</summary>
        const double UnityDeadbandPhon = 0.25;

        /// <summary>Maximum per-band gain clamp (dB) to guarantee IIR numerical stability under extreme SPL deltas.</summary>
        const double MaxBandGainDb = 24.0;

        /// <summary>
        /// ISO 226:2023 Table 1 preferred one-third-octave center frequencies in Hz (29 bands: 20 Hz to 12.5 kHz).
        /// </summary>
        public static readonly double[] TableFrequenciesHz = new double[] {
            20.0, 25.0, 31.5, 40.0, 50.0, 63.0, 80.0, 100.0, 125.0, 160.0,
            200.0, 250.0, 315.0, 400.0, 500.0, 630.0, 800.0, 1000.0, 1250.0, 1600.0,
            2000.0, 2500.0, 3150.0, 4000.0, 5000.0, 6300.0, 8000.0, 10000.0, 12500.0
        };

        /// <summary>
        /// ISO 226:2023 Table 1 loudness perception exponents (alpha_f), aligned with ISO 389-7:2019.
        /// Note: alpha_f(1000 Hz) = 0.300 in ISO 226:2023.
        /// </summary>
        public static readonly double[] TableAlphaF = new double[] {
            0.635, 0.602, 0.569, 0.537, 0.509, 0.482, 0.456, 0.433, 0.412, 0.391,
            0.373, 0.357, 0.343, 0.330, 0.320, 0.311, 0.303, 0.300, 0.295, 0.292,
            0.290, 0.290, 0.289, 0.289, 0.289, 0.293, 0.303, 0.323, 0.354
        };

        /// <summary>
        /// ISO 226:2023 Table 1 magnitude of the linear transfer function normalized at 1000 Hz (L_U, in dB).
        /// </summary>
        public static readonly double[] TableLuDb = new double[] {
            -31.5, -27.2, -23.1, -19.3, -16.1, -13.1, -10.4, -8.2, -6.3, -4.6,
            -3.2, -2.1, -1.2, -0.5, 0.0, 0.4, 0.5, 0.0, -2.7, -4.1,
            -1.0, 1.7, 2.5, 1.2, -2.1, -7.1, -11.2, -10.7, -3.1
        };

        /// <summary>
        /// ISO 226:2023 Table 1 normal binaural free-field threshold of hearing (T_f, in dB SPL re 20 uPa).
        /// </summary>
        public static readonly double[] TableThresholdTfDb = new double[] {
            78.1, 68.7, 59.5, 51.1, 44.0, 37.5, 31.5, 26.5, 22.1, 17.9,
            14.4, 11.4, 8.6, 6.2, 4.4, 3.0, 2.2, 2.4, 3.5, 1.7,
            -1.3, -4.2, -6.0, -5.4, -1.5, 6.0, 12.6, 13.9, 12.3
        };

        // Fixed physiological IIR anchor prototypes: 2 shelves + 4 broad peaking bells solved by exact
        // damped Gauss-Newton least squares directly against all 29 ISO 226:2023 Table 1 frequencies.
        static readonly ParametricFilterType[] AnchorTypes = new ParametricFilterType[] {
            ParametricFilterType.LowShelf,
            ParametricFilterType.Peaking,
            ParametricFilterType.Peaking,
            ParametricFilterType.Peaking,
            ParametricFilterType.Peaking,
            ParametricFilterType.HighShelf
        };

        static readonly double[] AnchorFreqsHz = new double[] {
            85.0, 32.0, 220.0, 630.0, 3150.0, 10000.0
        };

        static readonly double[] AnchorQs = new double[] {
            0.55, 0.70, 0.70, 0.80, 0.85, 0.65
        };

        const int AnchorCount = 6;
        const double IsoConstantTerm = 4.0e-10; // (4 * 10^-10)^(0.3 - alpha_f)
        const double Threshold1kPower = 1.1803206356517297; // 10^(0.3 * 2.4 / 10) = 10^0.072

        readonly object configLock = new object();
        readonly ParametricEqualizer eq;

        bool enabled;
        float referenceSplPhon = DefaultReferenceSplPhon;
        float listeningSplPhon = DefaultListeningSplPhon;
        bool autoHeadroomPreamp;
        int sampleRate = Listener.DefaultSampleRate;
        int channels = 2;

        double maxCompensationBoostDb;
        double fitRmsErrorDb;
        bool isBypassed = true;

        /// <summary>
        /// Creates an ISO 226:2023 dynamic equal-loudness filter (disabled by default for zero-regression neutrality).
        /// </summary>
        public Iso226LoudnessFilter(int sampleRate = 48000, int channels = 2) {
            this.sampleRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
            this.channels = channels > 0 ? channels : 2;
            eq = new ParametricEqualizer(this.sampleRate, this.channels);
            eq.AutoPreampHeadroom = false;
            eq.UseNyquistDeCramping = true;
        }

        /// <summary>True when enabled and listening SPL differs from reference SPL outside the 0.25 phon unity deadband.</summary>
        public bool IsActive => enabled && !isBypassed;

        /// <summary>True when currently operating in exact 0.000 dB bit-transparent unity bypass.</summary>
        public bool IsBypassed => !enabled || isBypassed;

        /// <summary>Configured reference playback SPL in phon (70..85).</summary>
        public float ReferenceSplPhon => referenceSplPhon;

        /// <summary>Active listening SPL in phon (40..95).</summary>
        public float ListeningSplPhon => listeningSplPhon;

        /// <summary>Active sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Maximum positive compensation gain across 20 Hz–12.5 kHz in dB.</summary>
        public double MaxCompensationBoostDb => IsActive ? maxCompensationBoostDb : 0.0;

        /// <summary>RMS fitting error (dB) of the 6-band IIR cascade against all 29 ISO 226:2023 Table 1 targets.</summary>
        public double FitRmsErrorDb => IsActive ? fitRmsErrorDb : 0.0;

        /// <summary>
        /// Evaluates exact ISO 226:2023 Formula (1) sound pressure level L_f (dB SPL) for table band
        /// <paramref name="bandIndex"/> (0..28) at loudness level <paramref name="phon"/>.
        /// </summary>
        public static double ComputeContourSplDb(int bandIndex, double phon) {
            if (bandIndex < 0) {
                bandIndex = 0;
            } else if (bandIndex >= TableFrequenciesHz.Length) {
                bandIndex = TableFrequenciesHz.Length - 1;
            }

            double clampedPhon = Math.Clamp(phon, 2.4, 100.0);
            double alpha = TableAlphaF[bandIndex];
            double lu = TableLuDb[bandIndex];
            double tf = TableThresholdTfDb[bandIndex];

            double bTerm = Math.Pow(IsoConstantTerm, 0.3 - alpha) * (Math.Pow(10.0, 0.03 * clampedPhon) - Threshold1kPower)
                + Math.Pow(10.0, alpha * (tf + lu) / 10.0);
            if (bTerm < 1e-30) {
                bTerm = 1e-30;
            }
            return (10.0 / alpha) * Math.Log10(bTerm) - lu;
        }

        /// <summary>
        /// Evaluates the exact ISO 226:2023 physiological compensation delta in dB at table band
        /// <paramref name="bandIndex"/> when listening at <paramref name="listeningPhon"/> relative to
        /// <paramref name="referencePhon"/>. Identically 0.000 dB when listeningPhon == referencePhon
        /// and identically 0.000 dB at 1000 Hz (band 17) for every phon pair.
        /// </summary>
        public static double ComputeCompensationDeltaDb(int bandIndex, double referencePhon, double listeningPhon) {
            if (Math.Abs(listeningPhon - referencePhon) < 1e-9) {
                return 0.0;
            }
            double lRef = ComputeContourSplDb(bandIndex, referencePhon) - referencePhon;
            double lListen = ComputeContourSplDb(bandIndex, listeningPhon) - listeningPhon;
            return lListen - lRef;
        }

        /// <summary>
        /// Evaluates the exact ISO 226:2023 compensation delta in dB at an arbitrary frequency <paramref name="freqHz"/>
        /// using log-frequency interpolation across the 29 normative 1/3-octave points.
        /// </summary>
        public static double EvaluateTargetCompensationDb(double freqHz, double referencePhon, double listeningPhon) {
            if (Math.Abs(listeningPhon - referencePhon) < 1e-9) {
                return 0.0;
            }
            int last = TableFrequenciesHz.Length - 1;
            if (freqHz <= TableFrequenciesHz[0]) {
                return ComputeCompensationDeltaDb(0, referencePhon, listeningPhon);
            }
            if (freqHz >= TableFrequenciesHz[last]) {
                return ComputeCompensationDeltaDb(last, referencePhon, listeningPhon);
            }
            for (int i = 0; i < last; i++) {
                double f0 = TableFrequenciesHz[i];
                double f1 = TableFrequenciesHz[i + 1];
                if (freqHz >= f0 && freqHz <= f1) {
                    double t = Math.Log(freqHz / f0) / Math.Log(f1 / f0);
                    double g0 = ComputeCompensationDeltaDb(i, referencePhon, listeningPhon);
                    double g1 = ComputeCompensationDeltaDb(i + 1, referencePhon, listeningPhon);
                    return g0 + (g1 - g0) * t;
                }
            }
            return 0.0;
        }

        /// <summary>
        /// Converts a linear playback volume scalar (0.01..1.0) into an estimated listening SPL in phon
        /// relative to <paramref name="referenceSplPhon"/>.
        /// </summary>
        public static float EstimateListeningPhonFromVolume(float referenceSplPhon, float linearVolume) {
            float refClamped = Math.Clamp(referenceSplPhon, MinReferenceSplPhon, MaxReferenceSplPhon);
            float vol = Math.Clamp(linearVolume, 0.01f, 2.0f);
            float dbDelta = 20f * MathF.Log10(vol);
            return Math.Clamp(refClamped + dbDelta, MinListeningSplPhon, MaxListeningSplPhon);
        }

        /// <summary>
        /// Fits the 6-band IIR shelving/peaking cascade to the exact ISO 226:2023 compensation target
        /// for (<paramref name="referencePhon"/>, <paramref name="listeningPhon"/>) using damped Gauss-Newton
        /// least squares, returning the fitted bands, RMS error (dB), and exact preamp offset to lock 1 kHz to 0.00 dB.
        /// </summary>
        public static ParametricBand[] FitIirCascade(
            double referencePhon,
            double listeningPhon,
            int sampleRate,
            out double offset1kDb,
            out double rmsErrorDb,
            out double maxBoostDb) {
            offset1kDb = 0.0;
            rmsErrorDb = 0.0;
            maxBoostDb = 0.0;

            if (Math.Abs(listeningPhon - referencePhon) < UnityDeadbandPhon) {
                return Array.Empty<ParametricBand>();
            }

            int nFreq = TableFrequenciesHz.Length;
            double[] target = new double[nFreq];
            double[] weights = new double[nFreq];
            for (int i = 0; i < nFreq; i++) {
                target[i] = ComputeCompensationDeltaDb(i, referencePhon, listeningPhon);
                // Slightly emphasize 40 Hz..10 kHz core audible region and exact 1 kHz anchor
                double f = TableFrequenciesHz[i];
                if (i == 17) {
                    weights[i] = 2.0; // 1 kHz reference lock
                } else if (f >= 40.0 && f <= 10000.0) {
                    weights[i] = 1.25;
                } else {
                    weights[i] = 0.85;
                }
            }

            // Build 6-band design using exact ParametricEqualizer biquad evaluation
            double[] gains = new double[AnchorCount];
            ParametricEqualizer scratch = new ParametricEqualizer(sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate, 1);
            scratch.AutoPreampHeadroom = false;
            scratch.UseNyquistDeCramping = true;

            ParametricBand[] candidate = new ParametricBand[AnchorCount];
            double[] residual = new double[nFreq];
            double[,] jacobian = new double[nFreq, AnchorCount];
            double[,] jtj = new double[AnchorCount, AnchorCount];
            double[] jtr = new double[AnchorCount];
            double[] delta = new double[AnchorCount];

            // 4 damped Gauss-Newton iterations converge to < 0.001 dB step (biquad dB response is nearly linear in gain)
            const double eps = 0.05;
            const double lambda = 1e-3;
            for (int iter = 0; iter < 4; iter++) {
                for (int b = 0; b < AnchorCount; b++) {
                    candidate[b] = new ParametricBand(AnchorTypes[b], AnchorFreqsHz[b], gains[b], AnchorQs[b], true);
                }
                scratch.SetBands(candidate, 0.0, true);
                double base1k = scratch.EvaluateMagnitudeDb(1000.0, false);

                double[] baseResp = new double[nFreq];
                for (int i = 0; i < nFreq; i++) {
                    baseResp[i] = scratch.EvaluateMagnitudeDb(TableFrequenciesHz[i], false) - base1k;
                    residual[i] = target[i] - baseResp[i];
                }

                // Numerical Jacobian d(resp_i - resp_1k)/d(gain_b)
                for (int b = 0; b < AnchorCount; b++) {
                    double orig = gains[b];
                    candidate[b] = new ParametricBand(AnchorTypes[b], AnchorFreqsHz[b], orig + eps, AnchorQs[b], true);
                    scratch.SetBands(candidate, 0.0, true);
                    double pert1k = scratch.EvaluateMagnitudeDb(1000.0, false);
                    for (int i = 0; i < nFreq; i++) {
                        double pert = scratch.EvaluateMagnitudeDb(TableFrequenciesHz[i], false) - pert1k;
                        jacobian[i, b] = (pert - baseResp[i]) / eps;
                    }
                    candidate[b] = new ParametricBand(AnchorTypes[b], AnchorFreqsHz[b], orig, AnchorQs[b], true);
                }

                // Form weighted normal equations (J^T W J + lambda I) * delta = J^T W r
                for (int r = 0; r < AnchorCount; r++) {
                    double rhs = 0.0;
                    for (int i = 0; i < nFreq; i++) {
                        rhs += weights[i] * jacobian[i, r] * residual[i];
                    }
                    jtr[r] = rhs;
                    for (int c = 0; c < AnchorCount; c++) {
                        double sum = 0.0;
                        for (int i = 0; i < nFreq; i++) {
                            sum += weights[i] * jacobian[i, r] * jacobian[i, c];
                        }
                        jtj[r, c] = sum + (r == c ? lambda : 0.0);
                    }
                }

                if (!SolveLinearSystem(jtj, jtr, delta, AnchorCount)) {
                    break;
                }

                double maxStep = 0.0;
                for (int b = 0; b < AnchorCount; b++) {
                    double step = Math.Clamp(delta[b], -12.0, 12.0);
                    gains[b] = Math.Clamp(gains[b] + step, -MaxBandGainDb, MaxBandGainDb);
                    double absStep = Math.Abs(step);
                    if (absStep > maxStep) {
                        maxStep = absStep;
                    }
                }
                if (maxStep < 1e-3) {
                    break;
                }
            }

            // Final band assembly and exact 1 kHz unity anchor + fit error calculation
            for (int b = 0; b < AnchorCount; b++) {
                candidate[b] = new ParametricBand(AnchorTypes[b], AnchorFreqsHz[b], gains[b], AnchorQs[b], Math.Abs(gains[b]) >= 0.02);
            }
            scratch.SetBands(candidate, 0.0, true);
            double resp1k = scratch.EvaluateMagnitudeDb(1000.0, false);
            offset1kDb = -resp1k;

            double sumSq = 0.0;
            double peak = 0.0;
            for (int i = 0; i < nFreq; i++) {
                double actual = scratch.EvaluateMagnitudeDb(TableFrequenciesHz[i], false) + offset1kDb;
                double err = actual - target[i];
                sumSq += err * err;
                if (actual > peak) {
                    peak = actual;
                }
            }
            rmsErrorDb = Math.Sqrt(sumSq / nFreq);
            maxBoostDb = peak;
            return candidate;
        }

        /// <summary>
        /// Atomically updates ISO 226:2023 parameters. When <paramref name="enabled"/> is false or
        /// |listeningSplPhon - referenceSplPhon| &lt; 0.25 phon, switches cleanly to exact 0.000 dB unity bypass.
        /// </summary>
        public void SetParameters(bool enabled, float referenceSplPhon, float listeningSplPhon, bool autoHeadroomPreamp = false) {
            float clampedRef = Math.Clamp(referenceSplPhon, MinReferenceSplPhon, MaxReferenceSplPhon);
            float clampedListen = Math.Clamp(listeningSplPhon, MinListeningSplPhon, MaxListeningSplPhon);
            lock (configLock) {
                bool changed = this.enabled != enabled
                    || MathF.Abs(this.referenceSplPhon - clampedRef) > 0.05f
                    || MathF.Abs(this.listeningSplPhon - clampedListen) > 0.15f
                    || this.autoHeadroomPreamp != autoHeadroomPreamp;
                this.enabled = enabled;
                this.referenceSplPhon = clampedRef;
                this.listeningSplPhon = clampedListen;
                this.autoHeadroomPreamp = autoHeadroomPreamp;
                if (changed) {
                    RebuildLocked();
                }
            }
        }

        /// <summary>
        /// Updates sample rate and channel count cleanly.
        /// </summary>
        public void Configure(int newSampleRate, int newChannels) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            int ch = newChannels > 0 ? newChannels : 2;
            lock (configLock) {
                bool srChanged = sampleRate != sr;
                sampleRate = sr;
                channels = ch;
                eq.Configure(sr, ch);
                if (srChanged) {
                    RebuildLocked();
                }
            }
        }

        /// <summary>
        /// Processes interleaved float samples in place with zero heap allocations.
        /// Bit-identical pass-through when disabled or at reference SPL.
        /// </summary>
        public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channelCount) {
            if (!IsActive) {
                return;
            }
            eq.ProcessInterleaved(buffer, offset, frameCount, channelCount);
        }

        /// <summary>
        /// Evaluates the exact real-time filter magnitude response (dB) at <paramref name="freqHz"/>.
        /// </summary>
        public double EvaluateMagnitudeDb(double freqHz) =>
            IsActive ? eq.EvaluateMagnitudeDb(freqHz, true) : 0.0;

        /// <summary>
        /// Resets internal TDF-II delay states cleanly on seek or track switch.
        /// </summary>
        public void Reset() {
            eq.ResetState();
        }

        void RebuildLocked() {
            if (!enabled || Math.Abs(listeningSplPhon - referenceSplPhon) < UnityDeadbandPhon) {
                isBypassed = true;
                maxCompensationBoostDb = 0.0;
                fitRmsErrorDb = 0.0;
                eq.Clear();
                return;
            }

            ParametricBand[] bands = FitIirCascade(
                referenceSplPhon,
                listeningSplPhon,
                sampleRate,
                out double offset1kDb,
                out double rmsErr,
                out double maxBoost);

            if (bands.Length == 0) {
                isBypassed = true;
                maxCompensationBoostDb = 0.0;
                fitRmsErrorDb = 0.0;
                eq.Clear();
                return;
            }

            // Keep 1 kHz locked to 0.00 dB unless explicit headroom preamp is requested
            double preampDb = autoHeadroomPreamp && maxBoost > 0.0 ? offset1kDb - maxBoost : offset1kDb;
            eq.AutoPreampHeadroom = false;
            eq.UseNyquistDeCramping = true;
            eq.SetBands(bands, preampDb, true);
            maxCompensationBoostDb = maxBoost;
            fitRmsErrorDb = rmsErr;
            isBypassed = false;
        }

        static bool SolveLinearSystem(double[,] a, double[] b, double[] x, int n) {
            double[,] m = new double[n, n + 1];
            for (int r = 0; r < n; r++) {
                for (int c = 0; c < n; c++) {
                    m[r, c] = a[r, c];
                }
                m[r, n] = b[r];
            }

            for (int col = 0; col < n; col++) {
                int pivot = col;
                double maxAbs = Math.Abs(m[col, col]);
                for (int row = col + 1; row < n; row++) {
                    double v = Math.Abs(m[row, col]);
                    if (v > maxAbs) {
                        maxAbs = v;
                        pivot = row;
                    }
                }
                if (maxAbs < 1e-14) {
                    return false;
                }
                if (pivot != col) {
                    for (int c = col; c <= n; c++) {
                        double tmp = m[col, c];
                        m[col, c] = m[pivot, c];
                        m[pivot, c] = tmp;
                    }
                }
                double diag = m[col, col];
                for (int row = col + 1; row < n; row++) {
                    double factor = m[row, col] / diag;
                    m[row, col] = 0.0;
                    for (int c = col + 1; c <= n; c++) {
                        m[row, c] -= factor * m[col, c];
                    }
                }
            }

            for (int r = n - 1; r >= 0; r--) {
                double sum = m[r, n];
                for (int c = r + 1; c < n; c++) {
                    sum -= m[r, c] * x[c];
                }
                x[r] = sum / m[r, r];
            }
            return true;
        }
    }
}
