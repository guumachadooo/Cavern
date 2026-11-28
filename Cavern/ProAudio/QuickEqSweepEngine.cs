using System;
using System.Collections.Generic;

using Cavern.Filters;

namespace Cavern.ProAudio {
    /// <summary>
    /// Supported acoustic room/speaker target curves for QuickEQ multi-point calibration.
    /// </summary>
    public enum QuickEqTargetCurve {
        /// <summary>Harman in-room target (+3.5 dB bass shelf below 120 Hz, smooth -0.8 dB/oct tilt).</summary>
        Harman = 0,
        /// <summary>Brüel &amp; Kjær 1974 studio room target (flat low/mid, smooth -1.0 dB/oct roll-off above 1 kHz).</summary>
        BruelKjaer = 1,
        /// <summary>Continuous -1.2 dB/octave warm room tilt anchored at 1 kHz.</summary>
        WarmTilt = 2,
        /// <summary>Strictly flat 0.0 dB anechoic / near-field studio target.</summary>
        Flat = 3
    }

    /// <summary>Parametric EQ band produced by <see cref="QuickEqSweepEngine"/> for live Cytomic SVF filtering and export.</summary>
    public sealed class QuickEqBand {
        /// <summary>Whether the EQ band is active.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Cytomic state-variable filter topology.</summary>
        public CytomicSvfType Type { get; set; } = CytomicSvfType.Peak;
        /// <summary>Center or corner frequency in Hz.</summary>
        public double FrequencyHz { get; set; } = 1000.0;
        /// <summary>Boost or cut gain in dB.</summary>
        public double GainDb { get; set; }
        /// <summary>Resonance / quality factor Q.</summary>
        public double Q { get; set; } = 1.4142135623730951;
        /// <summary>True to apply Vicanek Nyquist-matched magnitude correction.</summary>
        public bool NyquistMatched { get; set; } = true;
        /// <summary>Creates a default peaking EQ band at 1 kHz.</summary>
        public QuickEqBand() { }
        /// <summary>Creates a configured parametric EQ band.</summary>
        public QuickEqBand(CytomicSvfType type, double frequencyHz, double gainDb, double q, bool enabled = true, bool nyquistMatched = true) {
            Type = type; FrequencyHz = frequencyHz; GainDb = gainDb; Q = q; Enabled = enabled; NyquistMatched = nyquistMatched;
        }
    }

    /// <summary>Deconvolved impulse response and harmonic/SNR diagnostics from a Farina Exponential Sine Sweep (ESS).</summary>
    public sealed class QuickEqDeconvResult {
        /// <summary>Linear H1 impulse response extracted around the direct peak.</summary>
        public double[] LinearImpulseResponse { get; set; } = Array.Empty<double>();
        /// <summary>Separated 2nd harmonic distortion energy relative to H1 (dB).</summary>
        public double Harmonic2EnergyDb { get; set; } = -120.0;
        /// <summary>Separated 3rd harmonic distortion energy relative to H1 (dB).</summary>
        public double Harmonic3EnergyDb { get; set; } = -120.0;
        /// <summary>Estimated sweep deconvolution signal-to-noise ratio in dB.</summary>
        public double EstimatedSnrDb { get; set; }
        /// <summary>Sample rate in Hz.</summary>
        public int SampleRate { get; set; } = 48000;
        /// <summary>Direct sound peak sample index inside the deconvolved impulse response.</summary>
        public int PeakIndex { get; set; }
        /// <summary>True when SNR and peak energy meet calibration validity thresholds.</summary>
        public bool IsValid { get; set; }
    }

    /// <summary>Complete multi-point QuickEQ calibration solution, safety-clamped bands, and before/after curves.</summary>
    public sealed class QuickEqCalibrationResult {
        /// <summary>Solved parametric Cytomic SVF correction bands.</summary>
        public QuickEqBand[] Bands { get; set; } = Array.Empty<QuickEqBand>();
        /// <summary>Headroom preamp gain in dB preventing digital clipping.</summary>
        public double PreampDb { get; set; }
        /// <summary>Acoustic room target curve used during optimization.</summary>
        public QuickEqTargetCurve TargetCurve { get; set; } = QuickEqTargetCurve.Harman;
        /// <summary>Number of spatial microphone measurement points averaged.</summary>
        public int PointCount { get; set; }
        /// <summary>Mean deconvolution SNR across averaged takes in dB.</summary>
        public double SnrDb { get; set; }
        /// <summary>Maximum positive boost applied at or above 500 Hz in dB (&lt;= +6.0 dB).</summary>
        public double MaxHighFreqBoostDb { get; set; }
        /// <summary>RMS residual target deviation across the smoothed spectrum in dB.</summary>
        public double SmoothedErrorDb { get; set; }
        /// <summary>Logarithmic evaluation frequencies in Hz.</summary>
        public double[] TargetFrequenciesHz { get; set; } = Array.Empty<double>();
        /// <summary>Spatially averaged, FDW+ERB smoothed measured response in dB.</summary>
        public double[] MeasuredResponseDb { get; set; } = Array.Empty<double>();
        /// <summary>Predicted equalized response in dB after applying solved bands.</summary>
        public double[] CorrectedResponseDb { get; set; } = Array.Empty<double>();
    }

    /// <summary>
    /// 2026-SOTA QuickEQ engine: Farina ESS excitation, double-precision FFT deconvolution (H2/H3 + SNR),
    /// Frequency-Dependent Windowing (FDW), variable-Q ERB smoothing, MMM spatial averaging, and Cytomic SVF solver.
    /// </summary>
    public static class QuickEqSweepEngine {
        /// <summary>Hard safety cap on positive EQ boost in dB (KILL|R4).</summary>
        public const double HardMaxBoostDb = 6.0;
        /// <summary>Default number of logarithmic points returned by the spectrum pipeline.</summary>
        public const int DefaultSpectrumPoints = 96;
        const double InvLn2 = 1.4426950408889634;
        const double MinSnrValidDb = 22.0;

        /// <summary>Generates a Farina Exponential Sine Sweep with smooth raised-cosine fade tapers (&lt;= 25 ms).</summary>
        public static float[] GenerateExponentialSweep(double fStartHz = 20.0, double fEndHz = 20000.0,
            double durationSec = 3.0, int sampleRate = 48000, float amplitude = 0.25f) {
            ResolveSweepParams(fStartHz, fEndHz, durationSec, sampleRate, amplitude,
                out int sr, out double f1, out int n, out double l, out float amp);
            double phaseScale = 2.0 * Math.PI * f1 * l;
            int fadeSamples = Math.Min((int)Math.Round(sr * 0.015), n / 8);
            float[] sweep = new float[n];
            for (int i = 0; i < n; i++) {
                double s = Math.Sin(phaseScale * (Math.Exp(((double)i / sr) / l) - 1.0));
                double w = 1.0;
                if (fadeSamples > 0) {
                    if (i < fadeSamples) w = 0.5 * (1.0 - Math.Cos(Math.PI * i / fadeSamples));
                    else if (i >= n - fadeSamples) w = 0.5 * (1.0 - Math.Cos(Math.PI * (n - 1 - i) / fadeSamples));
                }
                sweep[i] = (float)(amp * w * s);
            }
            return sweep;
        }

        /// <summary>Generates the matched -6 dB/octave Farina inverse filter normalized to 0 dB unity sweep peak.</summary>
        public static float[] GenerateInverseFilter(double fStartHz = 20.0, double fEndHz = 20000.0,
            double durationSec = 3.0, int sampleRate = 48000, float amplitude = 0.25f) {
            ResolveSweepParams(fStartHz, fEndHz, durationSec, sampleRate, amplitude,
                out int sr, out double _, out int n, out double l, out float _);
            float[] sweep = GenerateExponentialSweep(fStartHz, fEndHz, durationSec, sampleRate, amplitude);
            double[] sweepD = new double[n], rawInv = new double[n];
            for (int i = 0; i < n; i++) {
                sweepD[i] = sweep[i];
                rawInv[i] = sweep[n - 1 - i] * Math.Exp(-((double)i / sr) / l);
            }
            double[] conv = ConvolveLinearDouble(sweepD, rawInv);
            double peak = 0.0;
            for (int i = 0; i < conv.Length; i++) {
                double abs = Math.Abs(conv[i]);
                if (abs > peak) peak = abs;
            }
            double invPeak = peak > 1e-18 ? 1.0 / peak : 1.0;
            float[] inverseFilter = new float[n];
            for (int i = 0; i < n; i++) inverseFilter[i] = (float)(rawInv[i] * invPeak);
            return inverseFilter;
        }

        /// <summary>Deconvolves a recorded sweep response via double-precision FFT and isolates H2/H3 precursors and SNR.</summary>
        public static QuickEqDeconvResult DeconvolveImpulseResponse(
            float[] sweep, float[] inverseFilter, float[] recorded, int sampleRate) =>
            DeconvolveImpulseResponse(sweep, inverseFilter, recorded, sampleRate, 0.0, 0.0);

        /// <summary>Deconvolves a recorded sweep response with optional explicit start/end sweep frequencies.</summary>
        public static QuickEqDeconvResult DeconvolveImpulseResponse(
            float[] sweep, float[] inverseFilter, float[] recorded, int sampleRate, double fStartHz, double fEndHz) {
            int sr = Math.Clamp(sampleRate > 0 ? sampleRate : 48000, 8000, 384000);
            if (sweep == null || inverseFilter == null || recorded == null ||
                sweep.Length < 64 || inverseFilter.Length < 64 || recorded.Length < 64) {
                return new QuickEqDeconvResult { SampleRate = sr, IsValid = false };
            }
            double recMean = 0.0;
            int validRecCount = 0;
            for (int i = 0; i < recorded.Length; i++) {
                float v = recorded[i];
                if (!float.IsNaN(v) && !float.IsInfinity(v)) { recMean += v; validRecCount++; }
            }
            if (validRecCount == 0) return new QuickEqDeconvResult { SampleRate = sr, IsValid = false };
            recMean /= validRecCount;

            double[] recD = new double[recorded.Length], invD = new double[inverseFilter.Length];
            for (int i = 0; i < recorded.Length; i++) {
                float v = recorded[i];
                recD[i] = (!float.IsNaN(v) && !float.IsInfinity(v)) ? (v - recMean) : 0.0;
            }
            for (int i = 0; i < inverseFilter.Length; i++) {
                float v = inverseFilter[i];
                invD[i] = (!float.IsNaN(v) && !float.IsInfinity(v)) ? v : 0.0;
            }
            double[] conv = ConvolveLinearDouble(recD, invD);
            int rawPeakIdx = 0;
            double rawPeakAbs = 0.0;
            for (int i = 0; i < conv.Length; i++) {
                double abs = Math.Abs(conv[i]);
                if (abs > rawPeakAbs) { rawPeakAbs = abs; rawPeakIdx = i; }
            }
            if (rawPeakAbs <= 1e-10) return new QuickEqDeconvResult { SampleRate = sr, IsValid = false };

            double lSec = EstimateRateConstantL(sweep, inverseFilter, sr, fStartHz, fEndHz);
            int lagH2 = (int)Math.Round(lSec * Math.Log(2.0) * sr), lagH3 = (int)Math.Round(lSec * Math.Log(3.0) * sr);
            int halfWin = Math.Clamp(sr / 250, 16, 512), preGuard = Math.Max(32, halfWin);
            double h1Energy = SumWindowEnergy(conv, rawPeakIdx - halfWin, Math.Min(conv.Length - 1, rawPeakIdx + halfWin * 2));
            if (h1Energy <= 1e-24) h1Energy = rawPeakAbs * rawPeakAbs;

            double h2Db = MeasureHarmonicPrecursorDb(conv, rawPeakIdx, lagH2, halfWin, preGuard, h1Energy);
            double h3Db = MeasureHarmonicPrecursorDb(conv, rawPeakIdx, lagH3, halfWin, preGuard, h1Energy);
            double snrDb = EstimateDeconvSnrDb(conv, rawPeakIdx, rawPeakAbs, lSec, sr, halfWin, preGuard);

            int preSamples = Math.Min(rawPeakIdx, Math.Min(lagH2 > 64 ? lagH2 / 3 : 64, Math.Max(64, sr / 250)));
            int startIdx = rawPeakIdx - preSamples;
            int irLen = Math.Min(conv.Length - startIdx, Math.Clamp((int)Math.Round(sr * 0.68), 8192, 65536));
            double[] ir = new double[irLen];
            Array.Copy(conv, startIdx, ir, 0, irLen);
            int peakInIr = 0;
            double irPeakAbs = 0.0;
            for (int i = 0; i < irLen; i++) {
                double abs = Math.Abs(ir[i]);
                if (abs > irPeakAbs) { irPeakAbs = abs; peakInIr = i; }
            }
            return new QuickEqDeconvResult {
                LinearImpulseResponse = ir, Harmonic2EnergyDb = h2Db, Harmonic3EnergyDb = h3Db,
                EstimatedSnrDb = snrDb, SampleRate = sr, PeakIndex = peakInIr,
                IsValid = snrDb >= MinSnrValidDb && irPeakAbs > 1e-8
            };
        }

        /// <summary>Power-domain spatial averaging (<c>10*log10(mean(10^(dB/10)))</c>) across 1..N positions.</summary>
        public static double[] AverageMultiPointMeasurements(IReadOnlyList<double[]> magnitudeDbCurves) {
            if (magnitudeDbCurves == null || magnitudeDbCurves.Count == 0) return Array.Empty<double>();
            int commonLen = int.MaxValue, validCount = 0;
            for (int c = 0; c < magnitudeDbCurves.Count; c++) {
                double[] curve = magnitudeDbCurves[c];
                if (curve != null && curve.Length > 0) {
                    if (curve.Length < commonLen) commonLen = curve.Length;
                    validCount++;
                }
            }
            if (validCount == 0 || commonLen <= 0 || commonLen == int.MaxValue) return Array.Empty<double>();

            double[] averaged = new double[commonLen];
            double invCount = 1.0 / validCount;
            for (int i = 0; i < commonLen; i++) {
                double powerSum = 0.0;
                for (int c = 0; c < magnitudeDbCurves.Count; c++) {
                    double[] curve = magnitudeDbCurves[c];
                    if (curve == null || curve.Length == 0) continue;
                    double db = IsFinite(curve[i]) ? Math.Clamp(curve[i], -120.0, 60.0) : -120.0;
                    powerSum += Math.Pow(10.0, db * 0.1);
                }
                averaged[i] = 10.0 * Math.Log10(Math.Max(powerSum * invCount, 1e-12));
            }
            return averaged;
        }

        /// <summary>Evaluates FDW (full IR below <paramref name="schroederHz"/>, cycle window above) + ERB smoothing.</summary>
        public static void ComputeFdwAndErbSpectrum(
            double[] impulse, int sampleRate, double[] outFreqs, double[] outMagDb, double schroederHz = 300.0) {
            if (outFreqs == null || outMagDb == null) return;
            int nOut = Math.Min(outFreqs.Length, outMagDb.Length);
            if (nOut <= 0) return;

            int sr = Math.Clamp(sampleRate > 0 ? sampleRate : 48000, 8000, 384000);
            double fMaxOut = Math.Min(20000.0, sr * 0.45);
            bool callerGrid = nOut >= 2 && IsFinite(outFreqs[0]) && outFreqs[0] > 0.0 &&
                              IsFinite(outFreqs[nOut - 1]) && outFreqs[nOut - 1] > outFreqs[0];
            if (!callerGrid) {
                double ratio = fMaxOut / 20.0;
                for (int i = 0; i < nOut; i++) {
                    outFreqs[i] = nOut > 1 ? 20.0 * Math.Pow(ratio, (double)i / (nOut - 1)) : 1000.0;
                }
            }

            int peakIdx = 0;
            double peakAbs = 0.0;
            if (impulse != null) {
                for (int i = 0; i < impulse.Length; i++) {
                    double v = IsFinite(impulse[i]) ? Math.Abs(impulse[i]) : 0.0;
                    if (v > peakAbs) { peakAbs = v; peakIdx = i; }
                }
            }
            if (impulse == null || impulse.Length == 0 || peakAbs <= 1e-12) {
                for (int i = 0; i < nOut; i++) outMagDb[i] = -120.0;
                return;
            }

            double schroeder = Math.Clamp(schroederHz, 80.0, 1200.0);
            int fullFftN = NextPowerOfTwo(Math.Max(impulse.Length, 32768));
            double[] fullRe = new double[fullFftN], fullIm = new double[fullFftN];
            int rightCount = impulse.Length - peakIdx, tailFade = Math.Max(16, rightCount / 8);
            int tailFadeStart = impulse.Length - tailFade;
            for (int i = 0; i < impulse.Length; i++) {
                double v = IsFinite(impulse[i]) ? impulse[i] : 0.0, w = 1.0;
                if (i < peakIdx && peakIdx > 0) w = 0.5 * (1.0 - Math.Cos(Math.PI * i / peakIdx));
                else if (i >= tailFadeStart && tailFade > 0) w = 0.5 * (1.0 - Math.Cos(Math.PI * (impulse.Length - 1 - i) / tailFade));
                fullRe[i] = v * w;
            }
            Radix2Fft(fullRe, fullIm, inverse: false);

            const int DenseCount = 240;
            double fLow = 16.0, denseRatio = Math.Min(22000.0, sr * 0.48) / fLow;
            double[] denseFreqs = new double[DenseCount], densePower = new double[DenseCount];
            double lowBlendHz = schroeder * 0.6, highBlendHz = schroeder * 1.6;
            double logSpan = Math.Log(highBlendHz / lowBlendHz);

            for (int j = 0; j < DenseCount; j++) {
                double f = fLow * Math.Pow(denseRatio, (double)j / (DenseCount - 1));
                denseFreqs[j] = f;
                double binExact = f * fullFftN / sr;
                int b0 = Math.Clamp((int)Math.Floor(binExact), 0, (fullFftN / 2) - 1);
                int b1 = Math.Min(b0 + 1, fullFftN / 2);
                double p0 = fullRe[b0] * fullRe[b0] + fullIm[b0] * fullIm[b0];
                double p1 = fullRe[b1] * fullRe[b1] + fullIm[b1] * fullIm[b1];
                double pFull = p0 + (p1 - p0) * Math.Clamp(binExact - b0, 0.0, 1.0);
                if (f <= lowBlendHz) { densePower[j] = pFull; continue; }

                double cycles = Math.Clamp(6.0 + 9.0 * (schroeder / f), 6.0, 15.0);
                int rightWin = Math.Clamp((int)Math.Round(cycles * sr / f), 16, rightCount);
                double pFdw = EvaluateWindowedDftPower(impulse, peakIdx, Math.Min(peakIdx, Math.Max(4, rightWin / 4)), rightWin, f, sr);
                if (f >= highBlendHz) {
                    densePower[j] = pFdw;
                } else {
                    double alpha = 0.5 * (1.0 - Math.Cos(Math.PI * Math.Clamp(Math.Log(f / lowBlendHz) / logSpan, 0.0, 1.0)));
                    densePower[j] = (1.0 - alpha) * pFull + alpha * pFdw;
                }
            }

            // Variable-Q smoothing: 1/6-oct modal fidelity below Schroeder -> ERB psychoacoustic smoothing above
            for (int i = 0; i < nOut; i++) {
                double f = Math.Clamp(outFreqs[i], 10.0, sr * 0.49);
                double erbHz = 24.7 * (0.00437 * f + 1.0);
                double erbSigmaOct = 0.5 * Math.Log((f + 0.5 * erbHz) / Math.Max(1.0, f - 0.5 * erbHz)) * InvLn2;
                double lowSigmaOct = 1.0 / 12.0;
                double blend = Math.Clamp(Math.Log(Math.Max(f, 20.0) / lowBlendHz) / logSpan, 0.0, 1.0);
                double sigmaOct = lowSigmaOct + (Math.Max(erbSigmaOct, 0.07) - lowSigmaOct) * blend;

                double wSum = 0.0, pSum = 0.0;
                for (int j = 0; j < DenseCount; j++) {
                    double u = (Math.Log(denseFreqs[j] / f) * InvLn2) / sigmaOct;
                    if (Math.Abs(u) > 3.5) continue;
                    double w = Math.Exp(-0.5 * u * u);
                    wSum += w;
                    pSum += w * densePower[j];
                }
                outMagDb[i] = 10.0 * Math.Log10(Math.Max(wSum > 1e-18 ? pSum / wSum : 1e-12, 1e-12));
            }
        }

        /// <summary>Evaluates the target curve level in dB at <paramref name="freqHz"/> (anchored near 0 dB at 1 kHz).</summary>
        public static double EvaluateTargetCurveDb(QuickEqTargetCurve target, double freqHz) {
            double f = Math.Clamp(freqHz, 10.0, 24000.0);
            switch (target) {
                case QuickEqTargetCurve.Harman:
                    return (3.5 / (1.0 + Math.Pow(f / 120.0, 2.0))) +
                           (-0.4 * Math.Log(1.0 + Math.Pow(f / 120.0, 2.0)) * InvLn2) + 2.45;
                case QuickEqTargetCurve.BruelKjaer:
                    return -0.5 * Math.Log(1.0 + Math.Pow(f / 1000.0, 2.0)) * InvLn2;
                case QuickEqTargetCurve.WarmTilt:
                    return -1.2 * Math.Log(f / 1000.0) * InvLn2;
                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// Greedy residual peak/dip parametric EQ solver producing up to <paramref name="maxBands"/>
        /// Nyquist-matched Cytomic SVF bands, enforcing the hard safety clamp &lt;= +6.0 dB boost (KILL|R4).
        /// </summary>
        public static QuickEqCalibrationResult SolveCorrectionBands(double[] freqsHz, double[] measuredDb,
            QuickEqTargetCurve target, int maxBands = 8, double maxBoostDb = 6.0, double maxCutDb = -12.0) {
            const int refSr = 48000;
            int safeMaxBands = Math.Clamp(maxBands, 1, 16);
            double safeMaxBoost = Math.Clamp(maxBoostDb, 0.0, HardMaxBoostDb);
            double safeMaxCut = Math.Clamp(maxCutDb, -24.0, 0.0);
            if (freqsHz == null || measuredDb == null || freqsHz.Length == 0 || measuredDb.Length == 0) {
                return new QuickEqCalibrationResult { TargetCurve = target };
            }

            int n = Math.Min(freqsHz.Length, measuredDb.Length);
            List<KeyValuePair<double, double>> validPts = new List<KeyValuePair<double, double>>(n);
            for (int i = 0; i < n; i++) {
                if (IsFinite(freqsHz[i]) && freqsHz[i] >= 16.0 && freqsHz[i] <= 22000.0 && IsFinite(measuredDb[i])) {
                    validPts.Add(new KeyValuePair<double, double>(freqsHz[i], Math.Clamp(measuredDb[i], -120.0, 60.0)));
                }
            }
            if (validPts.Count == 0) return new QuickEqCalibrationResult { TargetCurve = target };
            validPts.Sort((a, b) => a.Key.CompareTo(b.Key));

            int mCount = validPts.Count;
            double[] fGrid = new double[mCount], measGrid = new double[mCount], targetGrid = new double[mCount];
            for (int i = 0; i < mCount; i++) {
                fGrid[i] = validPts[i].Key;
                measGrid[i] = validPts[i].Value;
                targetGrid[i] = EvaluateTargetCurveDb(target, fGrid[i]);
            }

            double levelOffsetDb = ComputeMedianLevelOffset(fGrid, measGrid, targetGrid);
            double[] alignedTarget = new double[mCount], residual = new double[mCount];
            double currentMse = 0.0;
            for (int i = 0; i < mCount; i++) {
                alignedTarget[i] = targetGrid[i] + levelOffsetDb;
                residual[i] = alignedTarget[i] - measGrid[i];
                currentMse += residual[i] * residual[i];
            }

            List<QuickEqBand> chosen = new List<QuickEqBand>(safeMaxBands);
            double[] scratchBandDb = new double[mCount], bestBandDb = new double[mCount], compositeEqDb = new double[mCount];

            for (int step = 0; step < safeMaxBands; step++) {
                QuickEqBand candidate = FindBestNextBand(fGrid, residual, compositeEqDb, chosen,
                    safeMaxBoost, safeMaxCut, refSr, scratchBandDb, bestBandDb, currentMse, out double nextMse);
                if (candidate == null || (currentMse - nextMse) / mCount < 0.04) break;
                chosen.Add(candidate);
                currentMse = nextMse;
                for (int i = 0; i < mCount; i++) {
                    residual[i] -= bestBandDb[i];
                    compositeEqDb[i] += bestBandDb[i];
                }
            }

            EnforceCompositeHighFreqBoostGuard(chosen, safeMaxBoost, refSr);
            chosen.Sort((a, b) => a.FrequencyHz.CompareTo(b.FrequencyHz));

            QuickEqBand[] bandsArr = chosen.ToArray();
            double preampDb = EqExportFormatter.ComputeAutoPreampDb(ToExportBands(bandsArr), refSr);
            double[] corrected = new double[mCount];
            double errSqSum = 0.0;
            for (int i = 0; i < mCount; i++) {
                corrected[i] = measGrid[i] + EvaluateCascadeDb(bandsArr, fGrid[i], refSr);
                double diff = alignedTarget[i] - corrected[i];
                errSqSum += diff * diff;
            }

            return new QuickEqCalibrationResult {
                Bands = bandsArr, PreampDb = preampDb, TargetCurve = target, PointCount = 1, SnrDb = 0.0,
                MaxHighFreqBoostDb = MeasureCompositeHighFreqBoostDb(bandsArr, refSr),
                SmoothedErrorDb = Math.Sqrt(errSqSum / mCount),
                TargetFrequenciesHz = fGrid, MeasuredResponseDb = measGrid, CorrectedResponseDb = corrected
            };
        }

        /// <summary>Converts calibrated <see cref="QuickEqBand"/> entries into <see cref="FilterStudioBandSpec"/> for export and live SVF playback.</summary>
        public static FilterStudioBandSpec[] ToExportBands(IReadOnlyList<QuickEqBand> bands) {
            if (bands == null || bands.Count == 0) return Array.Empty<FilterStudioBandSpec>();
            List<FilterStudioBandSpec> list = new List<FilterStudioBandSpec>(bands.Count);
            for (int i = 0; i < bands.Count; i++) {
                QuickEqBand b = bands[i];
                if (b == null) continue;
                double clampedGain = Math.Min(HardMaxBoostDb, Math.Clamp(b.GainDb, -24.0, HardMaxBoostDb));
                list.Add(new FilterStudioBandSpec(b.Type, Math.Clamp(b.FrequencyHz, 16.0, 22000.0),
                    clampedGain, Math.Clamp(b.Q, 0.15, 18.0), b.Enabled, b.NyquistMatched));
            }
            return list.ToArray();
        }

        static QuickEqBand FindBestNextBand(double[] fGrid, double[] residual, double[] compositeEqDb,
            List<QuickEqBand> existing, double maxBoost, double maxCut, int sr,
            double[] scratchBandDb, double[] bestBandDb, double currentMse, out double bestMse) {
            int n = fGrid.Length;
            bestMse = currentMse;
            QuickEqBand best = null;
            List<QuickEqBand> proposals = new List<QuickEqBand>(16);

            for (int i = 0; i < n; i++) {
                double f = fGrid[i], r = residual[i];
                if (f < 22.0 || f > 16500.0 || Math.Abs(r) < 0.6) continue;
                bool isPeak = (i == 0 || Math.Abs(r) >= Math.Abs(residual[i - 1])) &&
                              (i == n - 1 || Math.Abs(r) >= Math.Abs(residual[i + 1]));
                if (!isPeak && (i % Math.Max(1, n / 16)) != 0) continue;

                double gain = Math.Clamp(r, maxCut, maxBoost);
                if (f >= 500.0 && gain > 0.0) {
                    gain = Math.Min(gain, Math.Max(0.0, maxBoost - Math.Max(0.0, compositeEqDb[i])));
                }
                if (Math.Abs(gain) < 0.5) continue;
                proposals.Add(new QuickEqBand(CytomicSvfType.Peak, f, gain,
                    EstimateBandQFromResidual(fGrid, residual, i, gain > 0.0), true, true));
            }
            if (existing.Count <= 1) {
                AddShelfProposal(proposals, fGrid, residual, true, maxBoost, maxCut);
                AddShelfProposal(proposals, fGrid, residual, false, maxBoost, maxCut);
            }

            CytomicSvfFilter evalFilter = new CytomicSvfFilter(sr, CytomicSvfType.Peak, 1000.0, 1.0, 0.0, 0.0) {
                NyquistMatched = true
            };
            for (int p = 0; p < proposals.Count; p++) {
                if (IsTooCloseToExistingBand(existing, proposals[p])) continue;
                double mse = EvaluateCandidateMse(evalFilter, proposals[p], fGrid, residual, compositeEqDb, maxBoost, scratchBandDb);
                if (mse < bestMse) { bestMse = mse; best = proposals[p]; Array.Copy(scratchBandDb, bestBandDb, n); }
            }
            if (best == null) return null;

            double[] fMults = new double[] { 0.92, 1.0, 1.08 };
            double[] qMults = new double[] { 0.72, 1.0, 1.38 };
            double[] gOffsets = new double[] { -0.8, 0.0, 0.8 };
            double baseF = best.FrequencyHz, baseQ = best.Q, baseG = best.GainDb;

            for (int fi = 0; fi < fMults.Length; fi++) {
                for (int qi = 0; qi < qMults.Length; qi++) {
                    for (int gi = 0; gi < gOffsets.Length; gi++) {
                        double tg = Math.Clamp(baseG + gOffsets[gi], maxCut, maxBoost);
                        if (Math.Abs(tg) < 0.4) continue;
                        QuickEqBand refined = new QuickEqBand(best.Type,
                            Math.Clamp(baseF * fMults[fi], 22.0, 16500.0), tg,
                            Math.Clamp(baseQ * qMults[qi], 0.5, best.GainDb > 0.0 ? 4.0 : 8.0), true, true);
                        double mse = EvaluateCandidateMse(evalFilter, refined, fGrid, residual, compositeEqDb, maxBoost, scratchBandDb);
                        if (mse < bestMse) { bestMse = mse; best = refined; Array.Copy(scratchBandDb, bestBandDb, n); }
                    }
                }
            }
            return best;
        }

        static void AddShelfProposal(List<QuickEqBand> proposals, double[] fGrid, double[] residual,
            bool lowShelf, double maxBoost, double maxCut) {
            double sum = 0.0;
            int cnt = 0;
            for (int i = 0; i < fGrid.Length; i++) {
                if ((lowShelf && fGrid[i] <= 120.0) || (!lowShelf && fGrid[i] >= 6000.0)) { sum += residual[i]; cnt++; }
            }
            if (cnt == 0) return;
            double mean = Math.Clamp(sum / cnt, maxCut, maxBoost);
            if (Math.Abs(mean) >= 1.5) {
                proposals.Add(new QuickEqBand(lowShelf ? CytomicSvfType.LowShelf : CytomicSvfType.HighShelf,
                    lowShelf ? 95.0 : 8000.0, mean, 0.7071067811865476, true, true));
            }
        }

        static double EvaluateCandidateMse(CytomicSvfFilter filter, QuickEqBand cand, double[] fGrid,
            double[] residual, double[] compositeEqDb, double maxBoost, double[] outBandDb) {
            filter.SetParameters(cand.Type, cand.FrequencyHz, cand.Q, cand.GainDb, immediate: true);
            double mse = 0.0;
            for (int i = 0; i < fGrid.Length; i++) {
                double db = 20.0 * Math.Log10(Math.Max(filter.EvaluateMagnitude(fGrid[i]), 1e-9));
                if (fGrid[i] >= 500.0 && compositeEqDb[i] + db > maxBoost + 0.05) return double.MaxValue;
                outBandDb[i] = db;
                double err = residual[i] - db;
                mse += err * err;
            }
            return mse;
        }

        static bool IsTooCloseToExistingBand(List<QuickEqBand> existing, QuickEqBand cand) {
            for (int i = 0; i < existing.Count; i++) {
                if (existing[i].Type == cand.Type && Math.Abs(Math.Log(existing[i].FrequencyHz / cand.FrequencyHz) * InvLn2) < 0.14) return true;
            }
            return false;
        }

        static double EstimateBandQFromResidual(double[] fGrid, double[] residual, int idx, bool isBoost) {
            double peak = residual[idx], halfTarget = peak * 0.5;
            int left = idx, right = idx;
            while (left > 0 && ((peak > 0 && residual[left] > halfTarget) || (peak < 0 && residual[left] < halfTarget))) left--;
            while (right < fGrid.Length - 1 && ((peak > 0 && residual[right] > halfTarget) || (peak < 0 && residual[right] < halfTarget))) right++;
            double twoBw = Math.Pow(2.0, Math.Max(0.2, Math.Log(fGrid[right] / Math.Max(1.0, fGrid[left])) * InvLn2));
            return Math.Clamp(Math.Sqrt(twoBw) / Math.Max(0.05, twoBw - 1.0), 0.55, isBoost ? 4.0 : 8.0);
        }

        static void EnforceCompositeHighFreqBoostGuard(List<QuickEqBand> bands, double maxBoost, int sr) {
            if (bands.Count == 0) return;
            for (int i = 0; i < bands.Count; i++) {
                if (bands[i].GainDb > maxBoost) bands[i].GainDb = maxBoost;
            }
            double maxHf = MeasureCompositeHighFreqBoostDb(bands, sr);
            if (maxHf > maxBoost) {
                double scale = (maxBoost - 0.02) / Math.Max(maxHf, 1e-6);
                for (int i = 0; i < bands.Count; i++) {
                    QuickEqBand b = bands[i];
                    if (b.GainDb > 0.0 && (b.FrequencyHz >= 350.0 || b.Type == CytomicSvfType.HighShelf)) {
                        b.GainDb = Math.Min(maxBoost, b.GainDb * scale);
                    }
                }
                maxHf = MeasureCompositeHighFreqBoostDb(bands, sr);
                if (maxHf > maxBoost) {
                    double fallbackScale = (maxBoost - 0.02) / Math.Max(maxHf, 1e-6);
                    for (int i = 0; i < bands.Count; i++) {
                        if (bands[i].GainDb > 0.0) bands[i].GainDb = Math.Min(maxBoost, bands[i].GainDb * fallbackScale);
                    }
                }
            }
        }

        static double MeasureCompositeHighFreqBoostDb(IReadOnlyList<QuickEqBand> bands, int sr) {
            if (bands == null || bands.Count == 0) return 0.0;
            const int steps = 128;
            double maxGainDb = 0.0, ratio = Math.Min(20000.0, sr * 0.45) / 500.0;
            for (int i = 0; i < steps; i++) {
                double db = EvaluateCascadeDb(bands, 500.0 * Math.Pow(ratio, (double)i / (steps - 1)), sr);
                if (db > maxGainDb) maxGainDb = db;
            }
            return maxGainDb;
        }

        static double EvaluateCascadeDb(IReadOnlyList<QuickEqBand> bands, double freqHz, int sr) {
            double sumDb = 0.0;
            for (int i = 0; i < bands.Count; i++) {
                QuickEqBand b = bands[i];
                if (b == null || !b.Enabled) continue;
                CytomicSvfFilter f = new CytomicSvfFilter(sr, b.Type, b.FrequencyHz, b.Q, b.GainDb, 0.0) { NyquistMatched = b.NyquistMatched };
                sumDb += 20.0 * Math.Log10(Math.Max(f.EvaluateMagnitude(freqHz), 1e-9));
            }
            return sumDb;
        }

        static double ComputeMedianLevelOffset(double[] freqs, double[] measured, double[] target) {
            List<double> diffs = new List<double>(freqs.Length);
            for (int i = 0; i < freqs.Length; i++) {
                if (freqs[i] >= 150.0 && freqs[i] <= 6000.0) diffs.Add(measured[i] - target[i]);
            }
            if (diffs.Count == 0) {
                for (int i = 0; i < freqs.Length; i++) diffs.Add(measured[i] - target[i]);
            }
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }

        static double EvaluateWindowedDftPower(double[] ir, int peakIdx, int leftWin, int rightWin, double freqHz, int sr) {
            int start = Math.Max(0, peakIdx - leftWin), end = Math.Min(ir.Length, peakIdx + rightWin);
            double omega = 2.0 * Math.PI * freqHz / sr, cosStep = Math.Cos(omega), sinStep = Math.Sin(omega);
            double theta0 = -omega * (start - peakIdx), phRe = Math.Cos(theta0), phIm = Math.Sin(theta0);
            double sumRe = 0.0, sumIm = 0.0;

            for (int n = start; n < end; n++) {
                double w = n < peakIdx
                    ? (leftWin > 0 ? 0.5 * (1.0 + Math.Cos(Math.PI * (peakIdx - n) / leftWin)) : 1.0)
                    : (rightWin > 0 ? 0.5 * (1.0 + Math.Cos(Math.PI * (n - peakIdx) / rightWin)) : 1.0);
                double x = IsFinite(ir[n]) ? ir[n] * w : 0.0;
                sumRe += x * phRe; sumIm += x * phIm;
                double nextRe = phRe * cosStep + phIm * sinStep;
                phIm = phIm * cosStep - phRe * sinStep;
                phRe = nextRe;
            }
            return sumRe * sumRe + sumIm * sumIm;
        }

        static double EstimateRateConstantL(float[] sweep, float[] inverseFilter, int sr, double fStartHz, double fEndHz) {
            double dur = (double)sweep.Length / sr;
            if (fStartHz > 0.0 && fEndHz > fStartHz) return dur / Math.Log(fEndHz / fStartHz);
            if (sweep.Length == inverseFilter.Length && sweep.Length >= 256) {
                double maxSweep = 0.0;
                for (int i = 0; i < sweep.Length; i++) {
                    double a = Math.Abs(sweep[i]);
                    if (a > maxSweep) maxSweep = a;
                }
                int lo = (int)(sweep.Length * 0.08), hi = (int)(sweep.Length * 0.92), count = 0;
                double sumT = 0.0, sumY = 0.0, sumTT = 0.0, sumTY = 0.0, thresh = maxSweep * 0.2;
                for (int j = lo; j < hi; j++) {
                    double sVal = Math.Abs(sweep[sweep.Length - 1 - j]), iVal = Math.Abs(inverseFilter[j]);
                    if (sVal > thresh && iVal > 1e-12) {
                        double t = (double)j / sr, y = Math.Log(iVal / sVal);
                        sumT += t; sumY += y; sumTT += t * t; sumTY += t * y; count++;
                    }
                }
                if (count >= 32) {
                    double denom = count * sumTT - sumT * sumT;
                    if (Math.Abs(denom) > 1e-12) {
                        double slope = (count * sumTY - sumT * sumY) / denom;
                        if (slope < -1e-4) return -1.0 / slope;
                    }
                }
            }
            return dur / Math.Log(Math.Min(20000.0, sr * 0.45) / 20.0);
        }

        static double MeasureHarmonicPrecursorDb(double[] conv, int rawPeakIdx, int lagSamples, int halfWin, int preGuard, double h1Energy) {
            int center = rawPeakIdx - lagSamples;
            if (lagSamples <= preGuard || center < 0 || center >= rawPeakIdx - preGuard) return -120.0;
            int start = Math.Max(0, center - halfWin), end = Math.Min(rawPeakIdx - preGuard - 1, center + halfWin);
            return end < start ? -120.0 : 10.0 * Math.Log10(Math.Max(SumWindowEnergy(conv, start, end) / h1Energy, 1e-12));
        }

        static double EstimateDeconvSnrDb(double[] conv, int rawPeakIdx, double rawPeakAbs, double lSec, int sr, int halfWin, int preGuard) {
            int start = Math.Min(rawPeakIdx / 4, Math.Max(64, sr / 100)), end = rawPeakIdx - preGuard;
            const int blockSize = 128;
            if (end - start < blockSize * 2) return 0.0;

            int[] harmCenters = new int[4];
            for (int h = 0; h < 4; h++) harmCenters[h] = rawPeakIdx - (int)Math.Round(lSec * Math.Log(h + 2.0) * sr);

            List<double> blockRms = new List<double>((end - start) / blockSize + 1);
            for (int b = start; b + blockSize <= end; b += blockSize) {
                int bMid = b + blockSize / 2;
                bool nearHarmonic = false;
                for (int h = 0; h < harmCenters.Length; h++) {
                    if (Math.Abs(bMid - harmCenters[h]) <= halfWin + blockSize) { nearHarmonic = true; break; }
                }
                if (nearHarmonic) continue;
                double sq = 0.0;
                for (int i = b; i < b + blockSize; i++) sq += conv[i] * conv[i];
                blockRms.Add(Math.Sqrt(sq / blockSize));
            }
            if (blockRms.Count == 0) return 0.0;
            blockRms.Sort();
            return Math.Clamp(20.0 * Math.Log10(rawPeakAbs / Math.Max(blockRms[blockRms.Count / 2], 1e-12)), 0.0, 140.0);
        }

        static double SumWindowEnergy(double[] data, int startInclusive, int endInclusive) {
            int s = Math.Max(0, startInclusive), e = Math.Min(data.Length - 1, endInclusive);
            double sum = 0.0;
            for (int i = s; i <= e; i++) sum += data[i] * data[i];
            return sum;
        }

        static double[] ConvolveLinearDouble(double[] a, double[] b) {
            int convLen = a.Length + b.Length - 1, fftN = NextPowerOfTwo(convLen);
            double[] ar = new double[fftN], ai = new double[fftN], br = new double[fftN], bi = new double[fftN];
            Array.Copy(a, ar, a.Length);
            Array.Copy(b, br, b.Length);
            Radix2Fft(ar, ai, inverse: false);
            Radix2Fft(br, bi, inverse: false);
            for (int i = 0; i < fftN; i++) {
                double re = ar[i] * br[i] - ai[i] * bi[i];
                ai[i] = ar[i] * bi[i] + ai[i] * br[i];
                ar[i] = re;
            }
            Radix2Fft(ar, ai, inverse: true);
            double[] result = new double[convLen];
            Array.Copy(ar, result, convLen);
            return result;
        }

        static void Radix2Fft(double[] real, double[] imag, bool inverse) {
            int n = real.Length, j = 0;
            for (int i = 1; i < n; i++) {
                int bit = n >> 1;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i < j) {
                    double tr = real[i]; real[i] = real[j]; real[j] = tr;
                    double ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
            }
            double dir = inverse ? 2.0 * Math.PI : -2.0 * Math.PI;
            for (int len = 2; len <= n; len <<= 1) {
                double angle = dir / len, wLenRe = Math.Cos(angle), wLenIm = Math.Sin(angle);
                int half = len >> 1;
                for (int i = 0; i < n; i += len) {
                    double wRe = 1.0, wIm = 0.0;
                    for (int k = 0; k < half; k++) {
                        int idxEven = i + k, idxOdd = idxEven + half;
                        double uRe = real[idxEven], uIm = imag[idxEven];
                        double vRe = real[idxOdd] * wRe - imag[idxOdd] * wIm;
                        double vIm = real[idxOdd] * wIm + imag[idxOdd] * wRe;
                        real[idxEven] = uRe + vRe; imag[idxEven] = uIm + vIm;
                        real[idxOdd] = uRe - vRe; imag[idxOdd] = uIm - vIm;
                        double nextWRe = wRe * wLenRe - wIm * wLenIm;
                        wIm = wRe * wLenIm + wIm * wLenRe;
                        wRe = nextWRe;
                    }
                }
            }
            if (inverse) {
                double invN = 1.0 / n;
                for (int i = 0; i < n; i++) { real[i] *= invN; imag[i] *= invN; }
            }
        }

        static void ResolveSweepParams(double fStartHz, double fEndHz, double durationSec, int sampleRate,
            float amplitude, out int sr, out double f1, out int n, out double l, out float amp) {
            sr = Math.Clamp(sampleRate > 0 ? sampleRate : 48000, 8000, 384000);
            f1 = Math.Clamp(IsFinite(fStartHz) ? fStartHz : 20.0, 5.0, sr * 0.2);
            double f2 = Math.Clamp(IsFinite(fEndHz) ? fEndHz : 20000.0, f1 * 1.5, sr * 0.495);
            double dur = Math.Clamp(IsFinite(durationSec) ? durationSec : 3.0, 0.2, 30.0);
            amp = float.IsNaN(amplitude) || float.IsInfinity(amplitude) ? 0.25f : Math.Clamp(amplitude, 0.001f, 0.99f);
            n = Math.Max(64, (int)Math.Round(dur * sr));
            l = ((double)n / sr) / Math.Log(f2 / f1);
        }

        static int NextPowerOfTwo(int v) {
            int p = 1;
            while (p < v && p < (1 << 24)) p <<= 1;
            return p;
        }

        static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
