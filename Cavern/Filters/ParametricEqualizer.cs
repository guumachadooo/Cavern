using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Cavern.Filters.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Filter topology for a single second-order section (SOS) inside <see cref="ParametricEqualizer"/>.
    /// </summary>
    public enum ParametricFilterType {
        /// <summary>Peaking / Bell equalizer filter (PK).</summary>
        Peaking = 0,
        /// <summary>Low-shelf filter (LS / LSC).</summary>
        LowShelf = 1,
        /// <summary>High-shelf filter (HS / HSC).</summary>
        HighShelf = 2,
        /// <summary>Second-order Butterworth/Q low-pass filter (LP / LPQ).</summary>
        LowPass = 3,
        /// <summary>Second-order Butterworth/Q high-pass filter (HP / HPQ).</summary>
        HighPass = 4,
        /// <summary>Second-order notch / band-reject filter (NO).</summary>
        Notch = 5,
        /// <summary>Second-order constant-skirt band-pass filter (BP).</summary>
        BandPass = 6,
        /// <summary>Second-order all-pass phase filter (AP).</summary>
        AllPass = 7
    }

    /// <summary>
    /// Immutable parameter descriptor for a single parametric EQ band (RBJ / Equalizer APO / AutoEq standard).
    /// </summary>
    public struct ParametricBand {
        /// <summary>Biquad filter topology.</summary>
        public ParametricFilterType Type;

        /// <summary>Center or corner frequency in Hz.</summary>
        public double FrequencyHz;

        /// <summary>Boost or cut in dB (ignored for pure pass/notch/allpass topologies).</summary>
        public double GainDb;

        /// <summary>Quality factor Q (&gt; 0). For shelves without explicit Q, 0.70710678 (Butterworth).</summary>
        public double Q;

        /// <summary>When false, this band is bypassed cleanly without removing it from the preset.</summary>
        public bool Enabled;

        /// <summary>
        /// Creates a parametric EQ band descriptor.
        /// </summary>
        public ParametricBand(ParametricFilterType type, double frequencyHz, double gainDb, double q, bool enabled = true) {
            Type = type;
            FrequencyHz = frequencyHz;
            GainDb = gainDb;
            Q = q > 0.0 ? q : QFactor.reference;
            Enabled = enabled;
        }
    }

    /// <summary>
    /// Parsed parametric EQ configuration produced from Equalizer APO, AutoEq, oratory1990, or Wavelet text files.
    /// </summary>
    public sealed class ParsedParametricEq {
        /// <summary>Optional explicit global preamp gain in dB (typically &lt;= 0 dB).</summary>
        public double PreampDb { get; set; }

        /// <summary>True when an explicit <c>Preamp:</c> directive was present in the source text.</summary>
        public bool HasExplicitPreamp { get; set; }

        /// <summary>Parsed parametric filter bands in declaration order.</summary>
        public ParametricBand[] Bands { get; set; } = Array.Empty<ParametricBand>();
    }

    /// <summary>
    /// Zero-GC double-precision Transposed Direct Form II (TDF-II) Second-Order Section (SOS) parametric equalizer
    /// supporting RBJ Audio EQ Cookbook coefficients, Nyquist-safe cramping compensation (Orfanidis high-frequency
    /// peaking preservation), exact composite frequency-response evaluation, and automatic clipping-free preamp headroom.
    /// </summary>
    public sealed class ParametricEqualizer {
        const double DenormalGuard = 1e-24;
        const double MinFrequencyHz = 5.0;
        const double MaxNyquistRatio = 0.49;
        const double MinQ = 0.05;
        const double MaxQ = 40.0;
        const double MaxAbsGainDb = 30.0;

        /// <summary>
        /// Double-precision normalized biquad coefficients for a single SOS section.
        /// </summary>
        struct SosCoeffs {
            public double B0;
            public double B1;
            public double B2;
            public double A1;
            public double A2;
            public bool Bypass;
        }

        /// <summary>
        /// Immutable active filter bank swapped atomically for lock-free zero-GC audio processing.
        /// </summary>
        sealed class FilterBankSnapshot {
            public readonly SosCoeffs[] Sections;
            public readonly ParametricBand[] Bands;
            public readonly double PreampLinear;
            public readonly double PreampDb;
            public readonly double PeakBoostDb;
            public readonly int SampleRate;
            public readonly int Channels;
            public readonly bool IsFlat;

            public FilterBankSnapshot(
                SosCoeffs[] sections,
                ParametricBand[] bands,
                double preampDb,
                double peakBoostDb,
                int sampleRate,
                int channels) {
                Sections = sections;
                Bands = bands;
                PreampDb = preampDb;
                PeakBoostDb = peakBoostDb;
                PreampLinear = Math.Pow(10.0, preampDb / 20.0);
                SampleRate = sampleRate;
                Channels = channels;
                IsFlat = sections.Length == 0 && Math.Abs(preampDb) < 1e-4;
            }
        }

        readonly object configLock = new object();
        volatile FilterBankSnapshot activeSnapshot;

        // Pre-allocated TDF-II delay states indexed as [section * maxChannels + channel]
        // Owned exclusively by the audio thread (re-sized only when bank section/channel capacity grows).
        double[] stateZ1 = Array.Empty<double>();
        double[] stateZ2 = Array.Empty<double>();
        int stateCapacitySections;
        int stateCapacityChannels;
        FilterBankSnapshot lastAppliedAudioSnapshot;

        ParametricBand[] configuredBands = Array.Empty<ParametricBand>();
        double explicitPreampDb;
        bool hasExplicitPreamp;
        bool autoPreampHeadroom = true;
        bool useNyquistDeCramping = true;
        int sampleRate = Listener.DefaultSampleRate;
        int channels = 2;

        /// <summary>
        /// Creates a flat (unity gain, 0-band) double-precision <see cref="ParametricEqualizer"/>.
        /// </summary>
        public ParametricEqualizer(int sampleRate = 48000, int channels = 2) {
            this.sampleRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
            this.channels = channels > 0 ? channels : 2;
            activeSnapshot = new FilterBankSnapshot(
                Array.Empty<SosCoeffs>(),
                Array.Empty<ParametricBand>(),
                0.0,
                0.0,
                this.sampleRate,
                this.channels);
        }

        /// <summary>Active sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Active channel count.</summary>
        public int Channels => channels;

        /// <summary>Effective preamp attenuation in dB currently applied before the SOS cascade.</summary>
        public double EffectivePreampDb => activeSnapshot.PreampDb;

        /// <summary>Worst-case positive peak magnitude boost (dB) across 20 Hz..20 kHz before preamp.</summary>
        public double PeakBoostDb => activeSnapshot.PeakBoostDb;

        /// <summary>Number of active (non-bypassed) biquad sections.</summary>
        public int ActiveSectionCount => activeSnapshot.Sections.Length;

        /// <summary>True when all bands are empty/flat and preamp is 0 dB.</summary>
        public bool IsFlat => activeSnapshot.IsFlat;

        /// <summary>
        /// When true (default), automatically computes exact worst-case peak headroom across the full audible band
        /// so the composite EQ response never exceeds 0 dBFS regardless of explicit preamp metadata.
        /// </summary>
        public bool AutoPreampHeadroom {
            get => autoPreampHeadroom;
            set {
                lock (configLock) {
                    if (autoPreampHeadroom != value) {
                        autoPreampHeadroom = value;
                        RebuildSnapshotLocked();
                    }
                }
            }
        }

        /// <summary>
        /// When true (default), applies exact Orfanidis Nyquist-gain de-cramping to high-frequency peaking bands
        /// (&gt;= 0.15 * fs) so 8–16 kHz AutoEq corrections preserve true analog bandwidth at 44.1/48 kHz.
        /// </summary>
        public bool UseNyquistDeCramping {
            get => useNyquistDeCramping;
            set {
                lock (configLock) {
                    if (useNyquistDeCramping != value) {
                        useNyquistDeCramping = value;
                        RebuildSnapshotLocked();
                    }
                }
            }
        }

        /// <summary>
        /// Updates the target sample rate and channel count and recalculates all SOS coefficients cleanly.
        /// </summary>
        public void Configure(int newSampleRate, int newChannels) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            int ch = newChannels > 0 ? newChannels : 2;
            lock (configLock) {
                if (sampleRate == sr && channels == ch) {
                    return;
                }
                sampleRate = sr;
                channels = ch;
                RebuildSnapshotLocked();
            }
        }

        /// <summary>
        /// Replaces all parametric bands and optional preamp gain atomically.
        /// </summary>
        public void SetBands(ParametricBand[] bands, double preampDb = 0.0, bool hasExplicitPreamp = false) {
            lock (configLock) {
                if (bands == null || bands.Length == 0) {
                    configuredBands = Array.Empty<ParametricBand>();
                } else {
                    configuredBands = new ParametricBand[bands.Length];
                    Array.Copy(bands, configuredBands, bands.Length);
                }
                explicitPreampDb = Math.Max(-30.0, Math.Min(12.0, preampDb));
                this.hasExplicitPreamp = hasExplicitPreamp;
                RebuildSnapshotLocked();
            }
        }

        /// <summary>
        /// Clears all bands and restores unity 0 dB pass-through.
        /// </summary>
        public void Clear() => SetBands(Array.Empty<ParametricBand>(), 0.0, false);

        /// <summary>
        /// Clears TDF-II filter delay states on the next audio block (called on seek or track switch).
        /// </summary>
        public void ResetState() {
            lastAppliedAudioSnapshot = null;
        }

        /// <summary>
        /// Evaluates the exact composite magnitude response (in dB, including effective preamp) at <paramref name="freqHz"/>.
        /// </summary>
        public double EvaluateMagnitudeDb(double freqHz, bool includePreamp = true) {
            FilterBankSnapshot snap = activeSnapshot;
            double magDb = EvaluateSectionsMagnitudeDb(snap.Sections, freqHz, snap.SampleRate);
            return includePreamp ? magDb + snap.PreampDb : magDb;
        }

        /// <summary>
        /// Processes <paramref name="frameCount"/> interleaved float audio frames in place with zero heap allocations
        /// in steady-state audio execution, using double-precision Transposed Direct Form II biquads.
        /// </summary>
        public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channelCount) {
            if (buffer == null || frameCount <= 0 || channelCount <= 0) {
                return;
            }
            FilterBankSnapshot snap = activeSnapshot;
            if (snap.IsFlat) {
                return;
            }

            int totalSamples = frameCount * channelCount;
            if (offset < 0 || offset + totalSamples > buffer.Length) {
                return;
            }

            SosCoeffs[] sections = snap.Sections;
            int sectionCount = sections.Length;
            EnsureAudioStateCapacity(sectionCount, channelCount, snap);

            double preamp = snap.PreampLinear;
            int stride = stateCapacityChannels;
            double[] z1Arr = stateZ1;
            double[] z2Arr = stateZ2;

            // Apply preamp and cascade all SOS sections per channel
            for (int ch = 0; ch < channelCount; ch++) {
                // Fast preamp pass combined with first section when present
                if (sectionCount == 0) {
                    int idx = offset + ch;
                    for (int n = 0; n < frameCount; n++) {
                        buffer[idx] = (float)(buffer[idx] * preamp);
                        idx += channelCount;
                    }
                    continue;
                }

                // Section 0 applies preamp on input
                {
                    SosCoeffs s0 = sections[0];
                    int stateIdx = ch;
                    double z1 = z1Arr[stateIdx];
                    double z2 = z2Arr[stateIdx];
                    double b0 = s0.B0;
                    double b1 = s0.B1;
                    double b2 = s0.B2;
                    double a1 = s0.A1;
                    double a2 = s0.A2;
                    int idx = offset + ch;
                    for (int n = 0; n < frameCount; n++) {
                        double x = buffer[idx] * preamp;
                        double y = b0 * x + z1;
                        z1 = b1 * x - a1 * y + z2;
                        z2 = b2 * x - a2 * y;
                        buffer[idx] = (float)y;
                        idx += channelCount;
                    }
                    z1Arr[stateIdx] = Math.Abs(z1) < DenormalGuard ? 0.0 : z1;
                    z2Arr[stateIdx] = Math.Abs(z2) < DenormalGuard ? 0.0 : z2;
                }

                // Remaining sections 1..sectionCount-1
                for (int s = 1; s < sectionCount; s++) {
                    SosCoeffs sec = sections[s];
                    int stateIdx = s * stride + ch;
                    double z1 = z1Arr[stateIdx];
                    double z2 = z2Arr[stateIdx];
                    double b0 = sec.B0;
                    double b1 = sec.B1;
                    double b2 = sec.B2;
                    double a1 = sec.A1;
                    double a2 = sec.A2;
                    int idx = offset + ch;
                    for (int n = 0; n < frameCount; n++) {
                        double x = buffer[idx];
                        double y = b0 * x + z1;
                        z1 = b1 * x - a1 * y + z2;
                        z2 = b2 * x - a2 * y;
                        buffer[idx] = (float)y;
                        idx += channelCount;
                    }
                    z1Arr[stateIdx] = Math.Abs(z1) < DenormalGuard ? 0.0 : z1;
                    z2Arr[stateIdx] = Math.Abs(z2) < DenormalGuard ? 0.0 : z2;
                }
            }
        }

        /// <summary>
        /// Parses Equalizer APO, AutoEq (<c>ParametricEQ.txt</c>), oratory1990, or Wavelet (<c>GraphicEQ:</c>) text
        /// into a <see cref="ParsedParametricEq"/> configuration.
        /// </summary>
        public static ParsedParametricEq ParseEqualizerApo(string text) {
            ParsedParametricEq result = new ParsedParametricEq();
            if (string.IsNullOrWhiteSpace(text)) {
                return result;
            }

            List<ParametricBand> bands = new List<ParametricBand>();
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++) {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') {
                    continue;
                }

                // Preamp: -6.2 dB
                if (line.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase)) {
                    string[] tokens = line.Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2 && TryParseDoubleInvariant(tokens[1], out double preamp)) {
                        result.PreampDb = preamp;
                        result.HasExplicitPreamp = true;
                    }
                    continue;
                }

                // Wavelet GraphicEQ: 20 -1.2; 25 -0.8; ... (downsampled to smooth 10-band octave peaking anchors)
                if (line.StartsWith("GraphicEQ:", StringComparison.OrdinalIgnoreCase)) {
                    ParseWaveletGraphicEqLine(line.Substring("GraphicEQ:".Length), bands);
                    continue;
                }

                // Filter [N]: ON <TYPE> ...
                if (line.StartsWith("Filter", StringComparison.OrdinalIgnoreCase)) {
                    if (TryParseFilterLine(line, out ParametricBand band)) {
                        bands.Add(band);
                    }
                }
            }

            result.Bands = bands.ToArray();
            return result;
        }

        /// <summary>
        /// Computes exact double-precision RBJ (or Nyquist de-cramped Orfanidis Peaking) SOS coefficients for a band.
        /// </summary>
        static SosCoeffs ComputeCoefficients(ParametricBand band, int fs, bool deCrampHighFreqPeaking) {
            if (!band.Enabled) {
                return new SosCoeffs { B0 = 1.0, Bypass = true };
            }

            double nyquist = fs * 0.5;
            double fc = band.FrequencyHz;
            if (double.IsNaN(fc) || double.IsInfinity(fc) || fc < MinFrequencyHz || fc >= nyquist * 0.99) {
                return new SosCoeffs { B0 = 1.0, Bypass = true };
            }
            if (fc > fs * MaxNyquistRatio) {
                fc = fs * MaxNyquistRatio;
            }

            double gainDb = band.GainDb;
            if (double.IsNaN(gainDb) || double.IsInfinity(gainDb)) {
                gainDb = 0.0;
            }
            if (gainDb > MaxAbsGainDb) {
                gainDb = MaxAbsGainDb;
            } else if (gainDb < -MaxAbsGainDb) {
                gainDb = -MaxAbsGainDb;
            }

            // Unity bypass for zero-gain peaking/shelving bands
            if ((band.Type == ParametricFilterType.Peaking ||
                 band.Type == ParametricFilterType.LowShelf ||
                 band.Type == ParametricFilterType.HighShelf) && Math.Abs(gainDb) < 1e-4) {
                return new SosCoeffs { B0 = 1.0, Bypass = true };
            }

            double q = band.Q;
            if (double.IsNaN(q) || double.IsInfinity(q) || q <= 0.0) {
                q = QFactor.reference;
            }
            if (q < MinQ) {
                q = MinQ;
            } else if (q > MaxQ) {
                q = MaxQ;
            }

            // High-frequency Orfanidis-style Nyquist bandwidth de-cramping for Peaking bands >= 0.15 * fs
            if (deCrampHighFreqPeaking && band.Type == ParametricFilterType.Peaking && fc >= fs * 0.15) {
                return ComputeOrfanidisPeaking(fc, gainDb, q, fs);
            }

            double w0 = 2.0 * Math.PI * fc / fs;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * q);
            double a = Math.Pow(10.0, gainDb / 40.0);

            double b0, b1, b2, a0, a1, a2;
            switch (band.Type) {
                case ParametricFilterType.Peaking: {
                    double alphaMulA = alpha * a;
                    double alphaDivA = alpha / a;
                    b0 = 1.0 + alphaMulA;
                    b1 = -2.0 * cosW0;
                    b2 = 1.0 - alphaMulA;
                    a0 = 1.0 + alphaDivA;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alphaDivA;
                    break;
                }
                case ParametricFilterType.LowShelf: {
                    double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                    double aPlus1 = a + 1.0;
                    double aMinus1 = a - 1.0;
                    b0 = a * (aPlus1 - aMinus1 * cosW0 + twoSqrtAAlpha);
                    b1 = 2.0 * a * (aMinus1 - aPlus1 * cosW0);
                    b2 = a * (aPlus1 - aMinus1 * cosW0 - twoSqrtAAlpha);
                    a0 = aPlus1 + aMinus1 * cosW0 + twoSqrtAAlpha;
                    a1 = -2.0 * (aMinus1 + aPlus1 * cosW0);
                    a2 = aPlus1 + aMinus1 * cosW0 - twoSqrtAAlpha;
                    break;
                }
                case ParametricFilterType.HighShelf: {
                    double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                    double aPlus1 = a + 1.0;
                    double aMinus1 = a - 1.0;
                    b0 = a * (aPlus1 + aMinus1 * cosW0 + twoSqrtAAlpha);
                    b1 = -2.0 * a * (aMinus1 + aPlus1 * cosW0);
                    b2 = a * (aPlus1 + aMinus1 * cosW0 - twoSqrtAAlpha);
                    a0 = aPlus1 - aMinus1 * cosW0 + twoSqrtAAlpha;
                    a1 = 2.0 * (aMinus1 - aPlus1 * cosW0);
                    a2 = aPlus1 - aMinus1 * cosW0 - twoSqrtAAlpha;
                    break;
                }
                case ParametricFilterType.LowPass: {
                    b0 = (1.0 - cosW0) * 0.5;
                    b1 = 1.0 - cosW0;
                    b2 = b0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
                case ParametricFilterType.HighPass: {
                    b0 = (1.0 + cosW0) * 0.5;
                    b1 = -(1.0 + cosW0);
                    b2 = b0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
                case ParametricFilterType.Notch: {
                    b0 = 1.0;
                    b1 = -2.0 * cosW0;
                    b2 = 1.0;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
                case ParametricFilterType.BandPass: {
                    b0 = alpha;
                    b1 = 0.0;
                    b2 = -alpha;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
                case ParametricFilterType.AllPass: {
                    b0 = 1.0 - alpha;
                    b1 = -2.0 * cosW0;
                    b2 = 1.0 + alpha;
                    a0 = 1.0 + alpha;
                    a1 = -2.0 * cosW0;
                    a2 = 1.0 - alpha;
                    break;
                }
                default:
                    return new SosCoeffs { B0 = 1.0, Bypass = true };
            }

            if (Math.Abs(a0) < 1e-18) {
                return new SosCoeffs { B0 = 1.0, Bypass = true };
            }

            double invA0 = 1.0 / a0;
            return new SosCoeffs {
                B0 = b0 * invA0,
                B1 = b1 * invA0,
                B2 = b2 * invA0,
                A1 = a1 * invA0,
                A2 = a2 * invA0,
                Bypass = false
            };
        }

        /// <summary>
        /// Exact Orfanidis (1997) digital parametric peaking equalizer matching the analog prototype's Nyquist gain
        /// and exact 3 dB (midpoint) bandwidth without bilinear cramping near fs/2.
        /// </summary>
        static SosCoeffs ComputeOrfanidisPeaking(double fc, double gainDb, double q, int fs) {
            double g0 = 1.0;
            double gPeak = Math.Pow(10.0, gainDb / 20.0);
            // Midpoint gain G_B = sqrt(G_0 * G) (standard RBJ/Audio EQ midpoint definition)
            double gB = Math.Sqrt(g0 * gPeak);

            // Analog prototype gain at Nyquist frequency (f = fs / 2):
            // |H_a(j * pi * fs)|^2 = (G0^2 * (1 - r^2)^2 + G^2 * (r / Q)^2) / ((1 - r^2)^2 + (r / Q)^2), r = (fs/2) / fc
            double r = (fs * 0.5) / fc;
            double oneMinusR2 = 1.0 - r * r;
            double rOverQ = r / q;
            double g1Sq = (g0 * g0 * oneMinusR2 * oneMinusR2 + gPeak * gPeak * rOverQ * rOverQ) /
                          (oneMinusR2 * oneMinusR2 + rOverQ * rOverQ);
            double g1 = Math.Sqrt(Math.Max(1e-12, g1Sq));

            // Guard against numerical degeneracy when G1 is too close to GB or G
            double gSq = gPeak * gPeak;
            double g0Sq = g0 * g0;
            double gBSq = gB * gB;
            double g1SqVal = g1 * g1;
            double denomF = gBSq - g1SqVal;
            double numF = gSq - gBSq;
            if (Math.Abs(denomF) < 1e-12 || numF / denomF <= 0.0) {
                // Fall back cleanly to standard RBJ peaking
                return ComputeCoefficients(new ParametricBand(ParametricFilterType.Peaking, fc, gainDb, q, true), fs, false);
            }

            double w0 = 2.0 * Math.PI * fc / fs;
            double dw = w0 / q;
            double fVal = Math.Abs(gSq - gBSq);
            double g00 = Math.Abs(gSq - g0Sq);
            double f00 = Math.Abs(gBSq - g0Sq);
            double g01 = Math.Abs(gSq - g0 * g1);
            double g11 = Math.Abs(gSq - g1SqVal);
            double f01 = Math.Abs(gBSq - g0 * g1);
            double f11 = Math.Abs(gBSq - g1SqVal);

            double w2 = Math.Sqrt(g11 / g00) * Math.Tan(w0 * 0.5) * Math.Tan(w0 * 0.5);
            double dW = (1.0 + Math.Sqrt(f00 / f11) * w2) * Math.Tan(dw * 0.5);

            double c = f11 * dW * dW - 2.0 * w2 * (f01 - Math.Sqrt(f00 * f11));
            double d = 2.0 * w2 * (g01 - Math.Sqrt(g00 * g11));
            double rad = (c + d) / Math.Max(1e-18, fVal);
            if (rad <= 0.0 || double.IsNaN(rad) || double.IsInfinity(rad)) {
                return ComputeCoefficients(new ParametricBand(ParametricFilterType.Peaking, fc, gainDb, q, true), fs, false);
            }
            double a = Math.Sqrt(rad);
            double b = Math.Sqrt((gSq * c + gBSq * d) / Math.Max(1e-18, fVal));
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) {
                return ComputeCoefficients(new ParametricBand(ParametricFilterType.Peaking, fc, gainDb, q, true), fs, false);
            }

            double a0 = 1.0 + w2 + a;
            if (Math.Abs(a0) < 1e-18) {
                return new SosCoeffs { B0 = 1.0, Bypass = true };
            }
            double invA0 = 1.0 / a0;
            return new SosCoeffs {
                B0 = (g1 + g0 * w2 + b) * invA0,
                B1 = -2.0 * (g1 - g0 * w2) * invA0,
                B2 = (g1 - b + g0 * w2) * invA0,
                A1 = -2.0 * (1.0 - w2) * invA0,
                A2 = (1.0 + w2 - a) * invA0,
                Bypass = false
            };
        }

        /// <summary>
        /// Evaluates the exact cascade magnitude response in dB at <paramref name="freqHz"/> using H(e^{jw}).
        /// </summary>
        static double EvaluateSectionsMagnitudeDb(SosCoeffs[] sections, double freqHz, int fs) {
            if (sections == null || sections.Length == 0 || freqHz <= 0.0 || freqHz >= fs * 0.5) {
                return 0.0;
            }
            double w = 2.0 * Math.PI * freqHz / fs;
            double cos1 = Math.Cos(w);
            double sin1 = -Math.Sin(w);
            double cos2 = Math.Cos(2.0 * w);
            double sin2 = -Math.Sin(2.0 * w);

            double magSq = 1.0;
            for (int i = 0; i < sections.Length; i++) {
                SosCoeffs s = sections[i];
                double nr = s.B0 + s.B1 * cos1 + s.B2 * cos2;
                double ni = s.B1 * sin1 + s.B2 * sin2;
                double dr = 1.0 + s.A1 * cos1 + s.A2 * cos2;
                double di = s.A1 * sin1 + s.A2 * sin2;
                double den = dr * dr + di * di;
                if (den > 1e-24) {
                    magSq *= (nr * nr + ni * ni) / den;
                }
            }
            return 10.0 * Math.Log10(Math.Max(1e-24, magSq));
        }

        void RebuildSnapshotLocked() {
            List<SosCoeffs> active = new List<SosCoeffs>(configuredBands.Length);
            for (int i = 0; i < configuredBands.Length; i++) {
                SosCoeffs c = ComputeCoefficients(configuredBands[i], sampleRate, useNyquistDeCramping);
                if (!c.Bypass) {
                    active.Add(c);
                }
            }
            SosCoeffs[] sections = active.ToArray();

            // Sweep 256 log-spaced frequencies across 20 Hz .. min(20 kHz, 0.48 * fs) to compute exact worst-case peak
            double peakDb = 0.0;
            if (sections.Length > 0) {
                double fMin = 20.0;
                double fMax = Math.Min(20000.0, sampleRate * 0.48);
                double ratio = fMax / fMin;
                const int sweepPoints = 256;
                for (int i = 0; i < sweepPoints; i++) {
                    double f = fMin * Math.Pow(ratio, i / (double)(sweepPoints - 1));
                    double magDb = EvaluateSectionsMagnitudeDb(sections, f, sampleRate);
                    if (magDb > peakDb) {
                        peakDb = magDb;
                    }
                }
            }

            double effectivePreamp = hasExplicitPreamp ? explicitPreampDb : 0.0;
            if (autoPreampHeadroom) {
                double neededHeadroom = peakDb > 0.05 ? -(peakDb + 0.2) : 0.0;
                effectivePreamp = hasExplicitPreamp
                    ? Math.Min(explicitPreampDb, neededHeadroom)
                    : neededHeadroom;
            }

            activeSnapshot = new FilterBankSnapshot(
                sections,
                configuredBands,
                effectivePreamp,
                peakDb,
                sampleRate,
                channels);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureAudioStateCapacity(int sectionCount, int channelCount, FilterBankSnapshot snap) {
            bool resized = false;
            if (sectionCount > stateCapacitySections || channelCount > stateCapacityChannels) {
                stateCapacitySections = Math.Max(sectionCount, stateCapacitySections);
                stateCapacityChannels = Math.Max(channelCount, stateCapacityChannels);
                int needed = Math.Max(1, stateCapacitySections * stateCapacityChannels);
                stateZ1 = new double[needed];
                stateZ2 = new double[needed];
                resized = true;
            }
            if (!resized && !ReferenceEquals(lastAppliedAudioSnapshot, snap)) {
                // Only clear states if section topology changed or explicit ResetState() was requested
                if (lastAppliedAudioSnapshot == null || lastAppliedAudioSnapshot.Sections.Length != sectionCount) {
                    Array.Clear(stateZ1, 0, stateZ1.Length);
                    Array.Clear(stateZ2, 0, stateZ2.Length);
                }
            }
            lastAppliedAudioSnapshot = snap;
        }

        static bool TryParseFilterLine(string line, out ParametricBand band) {
            band = default;
            string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 6) {
                return false;
            }

            // Locate ON / OFF token
            int onIdx = -1;
            bool enabled = true;
            for (int i = 1; i < Math.Min(4, tokens.Length); i++) {
                if (tokens[i].Equals("ON", StringComparison.OrdinalIgnoreCase)) {
                    onIdx = i;
                    enabled = true;
                    break;
                }
                if (tokens[i].Equals("OFF", StringComparison.OrdinalIgnoreCase)) {
                    onIdx = i;
                    enabled = false;
                    break;
                }
            }
            if (onIdx < 0 || onIdx + 1 >= tokens.Length) {
                return false;
            }

            string typeToken = tokens[onIdx + 1].ToUpperInvariant();
            ParametricFilterType type;
            switch (typeToken) {
                case "PK":
                case "PEQ":
                    type = ParametricFilterType.Peaking;
                    break;
                case "LS":
                case "LSC":
                case "LSQ":
                    type = ParametricFilterType.LowShelf;
                    break;
                case "HS":
                case "HSC":
                case "HSQ":
                    type = ParametricFilterType.HighShelf;
                    break;
                case "LP":
                case "LPQ":
                    type = ParametricFilterType.LowPass;
                    break;
                case "HP":
                case "HPQ":
                    type = ParametricFilterType.HighPass;
                    break;
                case "NO":
                case "NOTCH":
                    type = ParametricFilterType.Notch;
                    break;
                case "BP":
                    type = ParametricFilterType.BandPass;
                    break;
                case "AP":
                    type = ParametricFilterType.AllPass;
                    break;
                default:
                    return false;
            }

            double fc = 0.0;
            double gain = 0.0;
            double q = QFactor.reference;
            bool hasFc = false;
            bool hasQ = false;
            double slopeDb = double.NaN;

            // Optional Equalizer APO slope directly after LSC/HSC: "LSC 12 dB Fc ..."
            if ((typeToken == "LSC" || typeToken == "HSC") && onIdx + 3 < tokens.Length &&
                TryParseDoubleInvariant(tokens[onIdx + 2], out double maybeSlope) &&
                tokens[onIdx + 3].Equals("dB", StringComparison.OrdinalIgnoreCase)) {
                slopeDb = maybeSlope;
            }

            for (int i = onIdx + 2; i < tokens.Length - 1; i++) {
                string key = tokens[i];
                if (key.Equals("Fc", StringComparison.OrdinalIgnoreCase) && TryParseDoubleInvariant(tokens[i + 1], out double parsedFc)) {
                    fc = parsedFc;
                    hasFc = true;
                } else if (key.Equals("Gain", StringComparison.OrdinalIgnoreCase) && TryParseDoubleInvariant(tokens[i + 1], out double parsedGain)) {
                    gain = parsedGain;
                } else if (key.Equals("Q", StringComparison.OrdinalIgnoreCase) && TryParseDoubleInvariant(tokens[i + 1], out double parsedQ) && parsedQ > 0.0) {
                    q = parsedQ;
                    hasQ = true;
                } else if (key.Equals("BW", StringComparison.OrdinalIgnoreCase) && i + 2 < tokens.Length &&
                           tokens[i + 1].Equals("Oct", StringComparison.OrdinalIgnoreCase) &&
                           TryParseDoubleInvariant(tokens[i + 2], out double bwOct) && bwOct > 0.0) {
                    q = QFactor.FromBandwidth(bwOct);
                    hasQ = true;
                }
            }

            if (!hasFc || fc <= 0.0) {
                return false;
            }

            if (!hasQ && !double.IsNaN(slopeDb) && Math.Abs(gain) > 0.05) {
                double computedQ = QFactor.FromSlopeDecibels(slopeDb, gain);
                if (!double.IsNaN(computedQ) && !double.IsInfinity(computedQ) && computedQ > 0.0) {
                    q = computedQ;
                }
            }

            band = new ParametricBand(type, fc, gain, q, enabled);
            return true;
        }

        /// <summary>
        /// Parses Wavelet / Equalizer APO <c>GraphicEQ: f1 g1; f2 g2; ...</c> points into ISO-spaced peaking sections.
        /// </summary>
        static void ParseWaveletGraphicEqLine(string payload, List<ParametricBand> target) {
            string[] pairs = payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<double> freqs = new List<double>(pairs.Length);
            List<double> gains = new List<double>(pairs.Length);
            for (int i = 0; i < pairs.Length; i++) {
                string[] kv = pairs[i].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (kv.Length >= 2 &&
                    TryParseDoubleInvariant(kv[0], out double f) && f >= 10.0 && f <= 22000.0 &&
                    TryParseDoubleInvariant(kv[1], out double g)) {
                    freqs.Add(f);
                    gains.Add(g);
                }
            }
            if (freqs.Count == 0) {
                return;
            }

            // Sample at standard ISO 1/2-octave anchor frequencies (16 bands) with constant-Q = 2.87 for smooth GraphicEQ reconstruction
            double[] isoAnchors = new double[] {
                31.5, 45.0, 63.0, 90.0, 125.0, 180.0, 250.0, 355.0,
                500.0, 710.0, 1000.0, 1400.0, 2000.0, 2800.0, 4000.0,
                5600.0, 8000.0, 11200.0, 16000.0
            };
            const double halfOctaveQ = 2.87;
            for (int a = 0; a < isoAnchors.Length; a++) {
                double anchorF = isoAnchors[a];
                double g = InterpolateLogFreq(freqs, gains, anchorF);
                if (Math.Abs(g) >= 0.15) {
                    target.Add(new ParametricBand(ParametricFilterType.Peaking, anchorF, g, halfOctaveQ, true));
                }
            }
        }

        static double InterpolateLogFreq(List<double> freqs, List<double> gains, double targetFreq) {
            if (targetFreq <= freqs[0]) {
                return gains[0];
            }
            int last = freqs.Count - 1;
            if (targetFreq >= freqs[last]) {
                return gains[last];
            }
            int lo = 0;
            int hi = last;
            while (hi - lo > 1) {
                int mid = (lo + hi) >> 1;
                if (freqs[mid] <= targetFreq) {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            double f0 = freqs[lo];
            double f1 = freqs[hi];
            double denom = Math.Log(f1) - Math.Log(f0);
            if (Math.Abs(denom) < 1e-12) {
                return gains[lo];
            }
            double t = (Math.Log(targetFreq) - Math.Log(f0)) / denom;
            return gains[lo] + t * (gains[hi] - gains[lo]);
        }

        static bool TryParseDoubleInvariant(string token, out double value) {
            if (string.IsNullOrEmpty(token)) {
                value = 0.0;
                return false;
            }
            string clean = token.Replace(',', '.');
            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
