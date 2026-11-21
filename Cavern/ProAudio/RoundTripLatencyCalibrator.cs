using System;

namespace Cavern.ProAudio {
    /// <summary>
    /// Result of a physical loopback round-trip latency (RTT) measurement using a bipolar Maximum-Length Sequence (MLS).
    /// </summary>
    public readonly struct RoundTripLatencyResult {
        /// <summary>True when a high-confidence correlation peak was detected above the noise floor.</summary>
        public bool IsValid { get; }

        /// <summary>Sub-sample refined round-trip latency in samples.</summary>
        public double LatencySamples { get; }

        /// <summary>Sub-sample refined round-trip latency in milliseconds.</summary>
        public double LatencyMs { get; }

        /// <summary>Peak-to-noise-RMS correlation confidence ratio (&gt;= 6.0 indicates clean loopback lock).</summary>
        public double ConfidenceSnr { get; }

        /// <summary>Normalized peak correlation magnitude in [0; 1].</summary>
        public double NormalizedPeak { get; }

        /// <summary>Sample rate at which the measurement was performed.</summary>
        public int SampleRate { get; }

        /// <summary>Creates an immutable RTT measurement result.</summary>
        public RoundTripLatencyResult(
            bool isValid,
            double latencySamples,
            double latencyMs,
            double confidenceSnr,
            double normalizedPeak,
            int sampleRate) {
            IsValid = isValid;
            LatencySamples = latencySamples;
            LatencyMs = latencyMs;
            ConfidenceSnr = confidenceSnr;
            NormalizedPeak = normalizedPeak;
            SampleRate = sampleRate;
        }

        /// <summary>Invalid / failed calibration result.</summary>
        public static RoundTripLatencyResult Invalid(int sampleRate) {
            return new RoundTripLatencyResult(false, 0.0, 0.0, 0.0, 0.0, sampleRate);
        }
    }

    /// <summary>
    /// Pure-physics Maximum-Length Sequence (9-bit LFSR MLS, length 511, polynomial x^9 + x^5 + 1)
    /// loopback round-trip latency calibrator inspired by <c>google/oboe</c> OboeTester.
    /// Produces flat broadband autocorrelation with a single sharp delta peak and quadratic sub-sample
    /// peak refinement (&lt; 0.1 sample accuracy) without relying on heuristic latency estimates.
    /// </summary>
    public static class RoundTripLatencyCalibrator {
        /// <summary>Default 9-bit MLS burst length (2^9 - 1 = 511 samples, ~10.6 ms at 48 kHz).</summary>
        public const int DefaultMlsLength = 511;

        /// <summary>Default safe probe amplitude (-12 dBFS = 0.2512) to protect headphones and mic preamps.</summary>
        public const float DefaultProbeAmplitude = 0.25118864f;

        /// <summary>Minimum peak-to-noise-RMS SNR required to accept a loopback correlation lock.</summary>
        public const double MinConfidenceSnr = 6.0;

        /// <summary>
        /// Generates a deterministic zero-mean-balanced bipolar 9-bit MLS probe sequence
        /// (primitive polynomial x^9 + x^5 + 1, period 511) scaled by <paramref name="amplitude"/>.
        /// </summary>
        public static float[] GenerateMlsProbe(float amplitude = DefaultProbeAmplitude) {
            if (amplitude <= 0f || amplitude > 0.9f) {
                amplitude = DefaultProbeAmplitude;
            }
            float[] probe = new float[DefaultMlsLength];
            int lfsr = 0x1FF;
            for (int i = 0; i < DefaultMlsLength; i++) {
                int bit = lfsr & 1;
                probe[i] = bit != 0 ? amplitude : -amplitude;
                // Primitive taps for degree 9: bits 0 and 4 (x^9 + x^5 + 1)
                int feedback = ((lfsr >> 0) ^ (lfsr >> 4)) & 1;
                lfsr = (lfsr >> 1) | (feedback << 8);
            }
            return probe;
        }

        /// <summary>
        /// Measures exact round-trip latency (samples and ms) between the emitted <paramref name="probe"/>
        /// (transmitted starting at sample index 0) and the captured mono loopback signal <paramref name="captured"/>
        /// within [<paramref name="minLagSamples"/>; <paramref name="maxLagSamples"/>].
        /// Uses normalized cross-correlation with DC removal, noise-floor SNR gating, and parabolic sub-sample refinement.
        /// </summary>
        public static RoundTripLatencyResult MeasureRoundTripLatency(
            float[] probe,
            float[] captured,
            int sampleRate,
            int minLagSamples = 16,
            int maxLagSamples = 14400) {
            if (probe == null || captured == null || probe.Length < 31 || sampleRate < 8000) {
                return RoundTripLatencyResult.Invalid(sampleRate);
            }
            if (minLagSamples < 0) {
                minLagSamples = 0;
            }
            int maxPossibleLag = captured.Length - probe.Length;
            if (maxPossibleLag < minLagSamples) {
                return RoundTripLatencyResult.Invalid(sampleRate);
            }
            if (maxLagSamples > maxPossibleLag) {
                maxLagSamples = maxPossibleLag;
            }

            int m = probe.Length;
            double probeEnergy = 0.0;
            for (int i = 0; i < m; i++) {
                double p = probe[i];
                probeEnergy += p * p;
            }
            if (probeEnergy <= 1e-12) {
                return RoundTripLatencyResult.Invalid(sampleRate);
            }

            // Compute capture DC offset to prevent bias from asymmetric ADC preamps
            double capMean = 0.0;
            for (int i = 0; i < captured.Length; i++) {
                capMean += captured[i];
            }
            capMean /= captured.Length;

            int lagCount = maxLagSamples - minLagSamples + 1;
            double[] corr = new double[lagCount];

            int bestIdx = 0;
            double bestAbs = 0.0;
            double bestWindowEnergy = 0.0;

            for (int idx = 0; idx < lagCount; idx++) {
                int lag = minLagSamples + idx;
                double sum = 0.0;
                double winEnergy = 0.0;
                for (int j = 0; j < m; j++) {
                    double c = captured[lag + j] - capMean;
                    sum += c * probe[j];
                    winEnergy += c * c;
                }
                corr[idx] = sum;
                double absSum = Math.Abs(sum);
                if (absSum > bestAbs) {
                    bestAbs = absSum;
                    bestIdx = idx;
                    bestWindowEnergy = winEnergy;
                }
            }

            if (bestAbs <= 1e-9 || bestWindowEnergy <= 1e-12) {
                return RoundTripLatencyResult.Invalid(sampleRate);
            }

            // Compute off-peak noise RMS excluding a ±8 sample main-lobe guard band around the peak
            double noiseSqSum = 0.0;
            int noiseCount = 0;
            for (int idx = 0; idx < lagCount; idx++) {
                if (Math.Abs(idx - bestIdx) <= 8) {
                    continue;
                }
                double v = corr[idx];
                noiseSqSum += v * v;
                noiseCount++;
            }

            double noiseRms = noiseCount > 0 ? Math.Sqrt(noiseSqSum / noiseCount) : 1e-12;
            if (noiseRms < 1e-12) {
                noiseRms = 1e-12;
            }
            double snr = bestAbs / noiseRms;
            double normPeak = bestAbs / Math.Sqrt(probeEnergy * bestWindowEnergy);

            // Quadratic parabolic sub-sample interpolation on |corr| around bestIdx
            double delta = 0.0;
            if (bestIdx > 0 && bestIdx < lagCount - 1) {
                double y0 = Math.Abs(corr[bestIdx - 1]);
                double y1 = Math.Abs(corr[bestIdx]);
                double y2 = Math.Abs(corr[bestIdx + 1]);
                double denom = y0 - 2.0 * y1 + y2;
                if (Math.Abs(denom) > 1e-12) {
                    delta = 0.5 * (y0 - y2) / denom;
                    if (delta < -0.5) {
                        delta = -0.5;
                    } else if (delta > 0.5) {
                        delta = 0.5;
                    }
                }
            }

            double exactLagSamples = minLagSamples + bestIdx + delta;
            double latencyMs = (exactLagSamples * 1000.0) / sampleRate;
            bool valid = snr >= MinConfidenceSnr && normPeak >= 0.15;

            return new RoundTripLatencyResult(valid, exactLagSamples, latencyMs, snr, normPeak, sampleRate);
        }
    }
}
