using System;
using System.Runtime.CompilerServices;

namespace Cavern.Filters {
    /// <summary>
    /// Immutable snapshot of ITU-R BS.1770-5 / EBU R128 loudness, dynamic range, crest factor, and 4x True-Peak metrics.
    /// Designed for direct reuse by Slot 6B playback metering, W09 LGSR dynamics envelope lock, and W11 Vocal Mix-Match.
    /// </summary>
    public struct LoudnessAnalysisSnapshot {
        /// <summary>Momentary loudness (400 ms sliding window) in LUFS/LKFS, or <see cref="float.NaN"/> if below floor.</summary>
        public float MomentaryLufs;

        /// <summary>Short-term loudness (3.0 s sliding window) in LUFS/LKFS, or <see cref="float.NaN"/> if below floor.</summary>
        public float ShortTermLufs;

        /// <summary>Two-stage gated Integrated loudness (ITU-R BS.1770-5: -70 abs, -10 LU rel) in LUFS/LKFS.</summary>
        public float IntegratedLufs;

        /// <summary>Loudness Range (EBU Tech 3342: 95th - 10th percentile of gated 3 s blocks) in LU.</summary>
        public float LoudnessRangeLu;

        /// <summary>Maximum 4x-oversampled True Peak level in dBTP across the active measurement window.</summary>
        public float TruePeakDbtp;

        /// <summary>Running maximum True Peak level in dBTP since last reset.</summary>
        public float MaxTruePeakDbtp;

        /// <summary>Sample peak level in dBFS for the latest block.</summary>
        public float SamplePeakDbfs;

        /// <summary>Unweighted RMS level in dBFS over the 400 ms momentary window.</summary>
        public float RmsDbfs;

        /// <summary>Instantaneous Peak-to-RMS Crest Factor (TruePeakDbtp - RmsDbfs) in dB (reused by W09/W11).</summary>
        public float CrestFactorDb;

        /// <summary>Returns a silent/unmeasured baseline snapshot.</summary>
        public static LoudnessAnalysisSnapshot Empty() {
            return new LoudnessAnalysisSnapshot {
                MomentaryLufs = float.NaN,
                ShortTermLufs = float.NaN,
                IntegratedLufs = float.NaN,
                LoudnessRangeLu = 0f,
                TruePeakDbtp = -120f,
                MaxTruePeakDbtp = -120f,
                SamplePeakDbfs = -120f,
                RmsDbfs = -120f,
                CrestFactorDb = 0f
            };
        }
    }

    /// <summary>
    /// Exact ITU-R BS.1770-5 Annex 2 4x-oversampled 48-tap 4-phase polyphase FIR True-Peak interpolator.
    /// Zero heap allocations during steady-state processing. Shared by <see cref="LoudnessMeter"/> and
    /// <see cref="TruePeakLimiter"/> (and directly reusable by W09 LGSR &amp; W11 Vocal Chain).
    /// </summary>
    public sealed class TruePeakDetector {
        /// <summary>Number of polyphase sub-filters (4x oversampling).</summary>
        public const int OversampleFactor = 4;

        /// <summary>Number of FIR taps per polyphase branch (48 total taps / 4 phases = 12 taps/phase).</summary>
        public const int TapsPerPhase = 12;

        /// <summary>
        /// Exact ITU-R BS.1770-5 Annex 2 Table 4 48-tap FIR interpolating coefficients organized by polyphase branch [4][12].
        /// Normative equiripple prototype (+0.007 dB @ 1 kHz, +3.055 dBTP on fs/4 45° ISP); Phase 0 reverses Phase 3, Phase 1 reverses Phase 2.
        /// </summary>
        public static readonly double[][] PolyphaseTaps = new double[][] {
            new double[] {
                0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000,
                -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750,
                0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500
            },
            new double[] {
                -0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250,
                -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125,
                0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375
            },
            new double[] {
                -0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000,
                -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500,
                0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875
            },
            new double[] {
                -0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750,
                -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875,
                0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750
            }
        };

        // Flattened float taps for tight zero-GC inner loops
        static readonly float[] FlatTaps = BuildFlatTaps();

        float[] delayLines = Array.Empty<float>();
        int[] writeIndex = Array.Empty<int>();
        int channels;

        /// <summary>Creates a zero-GC 4x-oversampled BS.1770-5 True-Peak detector for <paramref name="channels"/> channels.</summary>
        public TruePeakDetector(int channels = 2) {
            Configure(channels);
        }

        /// <summary>Configures channel count and resets FIR delay state.</summary>
        public void Configure(int channelCount) {
            int ch = channelCount > 0 ? channelCount : 2;
            if (ch != channels || delayLines.Length != ch * TapsPerPhase) {
                channels = ch;
                delayLines = new float[ch * TapsPerPhase];
                writeIndex = new int[ch];
            } else {
                Reset();
            }
        }

        /// <summary>Clears all polyphase FIR delay lines.</summary>
        public void Reset() {
            if (delayLines.Length > 0) {
                Array.Clear(delayLines, 0, delayLines.Length);
            }
            if (writeIndex.Length > 0) {
                Array.Clear(writeIndex, 0, writeIndex.Length);
            }
        }

        /// <summary>
        /// Pushes one sample for <paramref name="channel"/> into the 12-tap FIR history and evaluates the
        /// maximum absolute amplitude across all 4 polyphase interpolated sub-samples (and the raw sample).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ProcessSample(int channel, float sample) {
            if (channel < 0 || channel >= channels) {
                return MathF.Abs(sample);
            }
            int baseOff = channel * TapsPerPhase;
            int w = writeIndex[channel];
            delayLines[baseOff + w] = sample;
            int nextW = w + 1;
            if (nextW == TapsPerPhase) {
                nextW = 0;
            }
            writeIndex[channel] = nextW;

            // Evaluate all 4 polyphase branches (k = 0 is most recent sample at index w, going backwards mod 12)
            float maxAbs = MathF.Abs(sample);
            float p0 = 0f;
            float p1 = 0f;
            float p2 = 0f;
            float p3 = 0f;
            int idx = w;
            for (int k = 0; k < TapsPerPhase; k++) {
                float x = delayLines[baseOff + idx];
                p0 += x * FlatTaps[k];
                p1 += x * FlatTaps[12 + k];
                p2 += x * FlatTaps[24 + k];
                p3 += x * FlatTaps[36 + k];
                idx--;
                if (idx < 0) {
                    idx = TapsPerPhase - 1;
                }
            }
            float a0 = MathF.Abs(p0);
            float a1 = MathF.Abs(p1);
            float a2 = MathF.Abs(p2);
            float a3 = MathF.Abs(p3);
            if (a0 > maxAbs) {
                maxAbs = a0;
            }
            if (a1 > maxAbs) {
                maxAbs = a1;
            }
            if (a2 > maxAbs) {
                maxAbs = a2;
            }
            if (a3 > maxAbs) {
                maxAbs = a3;
            }
            return maxAbs;
        }

        /// <summary>
        /// Evaluates the maximum linear True Peak amplitude over an interleaved float slice.
        /// </summary>
        public float ProcessBlockMax(float[] interleaved, int offset, int frameCount, int channelCount) {
            if (interleaved == null || frameCount <= 0 || channelCount <= 0) {
                return 0f;
            }
            if (channelCount != channels) {
                Configure(channelCount);
            }
            float maxPeak = 0f;
            int end = offset + frameCount * channelCount;
            if (offset < 0 || end > interleaved.Length) {
                return 0f;
            }
            for (int f = 0; f < frameCount; f++) {
                int baseIdx = offset + f * channelCount;
                for (int c = 0; c < channelCount; c++) {
                    float tp = ProcessSample(c, interleaved[baseIdx + c]);
                    if (tp > maxPeak) {
                        maxPeak = tp;
                    }
                }
            }
            return maxPeak;
        }

        /// <summary>Converts linear amplitude to dBTP (floored at -120 dBTP).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LinearToDbtp(float linear) =>
            linear > 1e-6f ? 20f * MathF.Log10(linear) : -120f;

        /// <summary>Stateless helper measuring exact 4x True Peak (dBTP) of an entire interleaved buffer (reused by W09/W11).</summary>
        public static float MeasureTruePeakDbtp(float[] interleaved, int offset, int frameCount, int channelCount) {
            TruePeakDetector detector = new TruePeakDetector(channelCount);
            float peak = detector.ProcessBlockMax(interleaved, offset, frameCount, channelCount);
            return LinearToDbtp(peak);
        }

        static float[] BuildFlatTaps() {
            float[] flat = new float[OversampleFactor * TapsPerPhase];
            for (int p = 0; p < OversampleFactor; p++) {
                for (int k = 0; k < TapsPerPhase; k++) {
                    flat[p * TapsPerPhase + k] = (float)PolyphaseTaps[p][k];
                }
            }
            return flat;
        }
    }

    /// <summary>
    /// Standards-compliant ITU-R BS.1770-5 / EBU R128 / EBU Tech 3341 &amp; 3342 loudness meter with multi-rate
    /// exact bilinear K-weighting (44.1..192 kHz), Momentary (400 ms), Short-Term (3.0 s), two-stage gated
    /// Integrated LUFS (-70 abs / -10 LU rel), Loudness Range (LRA, 95th–10th percentile), Crest Factor,
    /// and 4x-oversampled BS.1770-5 Annex 2 True Peak metering. Zero heap allocations during audio processing.
    /// </summary>
    public sealed class LoudnessMeter {
        /// <summary>ITU-R BS.1770-5 absolute gating threshold in LKFS/LUFS.</summary>
        public const double AbsoluteGateLufs = -70.0;

        /// <summary>ITU-R BS.1770-5 relative gating offset for Integrated loudness in LU.</summary>
        public const double IntegratedRelativeOffsetLu = -10.0;

        /// <summary>EBU Tech 3342 relative gating offset for Loudness Range (LRA) in LU.</summary>
        public const double LraRelativeOffsetLu = -20.0;

        /// <summary>ITU-R BS.1770-5 K-weighting loudness offset constant (-0.691 dB).</summary>
        public const double LoudnessOffsetDb = -0.691;

        // Exact jiixyj/libebur128 analog prototype constants reproducing ITU-R BS.1770-5 Table 1 & 2 at 48 kHz to < 1e-12
        const double Stage1F0Hz = 1681.974450955533;
        const double Stage1GainDb = 3.999843853973347;
        const double Stage1Q = 0.7071752369554196;

        const double Stage2F0Hz = 38.13547087602444;
        const double Stage2Q = 0.5003270373238773;

        // Fixed zero-GC 0.1 LU histogram from -70.0 to +20.0 LUFS (900 bins = 7.2 KB)
        const double HistMinLufs = -70.0;
        const double HistBinWidthLu = 0.1;
        const int HistBinCount = 900;

        // 100 ms hop ring: 4 hops = 400 ms (Momentary), 30 hops = 3.0 s (Short-Term)
        const int MomentaryHops = 4;
        const int ShortTermHops = 30;
        const double DenormalGuard = 1e-24;

        // K-weighting biquad coefficients (Stage 1: High-Shelf, Stage 2: RLB High-Pass)
        double s1b0, s1b1, s1b2, s1a1, s1a2;
        double s2b0, s2b1, s2b2, s2a1, s2a2;

        // Per-channel TDF-II delay states
        double[] s1z1 = Array.Empty<double>();
        double[] s1z2 = Array.Empty<double>();
        double[] s2z1 = Array.Empty<double>();
        double[] s2z2 = Array.Empty<double>();
        double[] channelWeights = Array.Empty<double>();

        // 100 ms hop ring buffers (weighted K-energy and unweighted RMS energy)
        readonly double[] hopWeightedEnergy = new double[ShortTermHops];
        readonly double[] hopRawEnergy = new double[ShortTermHops];
        readonly long[] integratedHist = new long[HistBinCount];
        readonly long[] shortTermHist = new long[HistBinCount];

        readonly TruePeakDetector truePeakDetector = new TruePeakDetector(2);

        int sampleRate = Listener.DefaultSampleRate;
        int channels = 2;
        int hopSamples = 4800; // 100 ms @ 48 kHz
        int currentHopSampleCount;
        double currentHopWeightedSum;
        double currentHopRawSum;
        int hopWriteIndex;
        int filledHops;
        long totalIntegratedBlocks;
        long totalShortTermBlocks;

        float latestMomentaryLufs = float.NaN;
        float latestShortTermLufs = float.NaN;
        float latestIntegratedLufs = float.NaN;
        float latestLraLu;
        float latestBlockTruePeakLinear;
        float runningMaxTruePeakLinear;
        float latestBlockSamplePeak;
        float latestRmsDbfs = -120f;
        float latestCrestFactorDb;

        /// <summary>Creates an ITU-R BS.1770-5 / EBU R128 loudness meter.</summary>
        public LoudnessMeter(int sampleRate = 48000, int channels = 2) {
            Configure(sampleRate, channels);
        }

        /// <summary>Configured sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Configured channel count.</summary>
        public int Channels => channels;

        /// <summary>Latest complete telemetry snapshot.</summary>
        public LoudnessAnalysisSnapshot Snapshot => new LoudnessAnalysisSnapshot {
            MomentaryLufs = latestMomentaryLufs,
            ShortTermLufs = latestShortTermLufs,
            IntegratedLufs = latestIntegratedLufs,
            LoudnessRangeLu = latestLraLu,
            TruePeakDbtp = TruePeakDetector.LinearToDbtp(latestBlockTruePeakLinear),
            MaxTruePeakDbtp = TruePeakDetector.LinearToDbtp(runningMaxTruePeakLinear),
            SamplePeakDbfs = TruePeakDetector.LinearToDbtp(latestBlockSamplePeak),
            RmsDbfs = latestRmsDbfs,
            CrestFactorDb = latestCrestFactorDb
        };

        /// <summary>
        /// Computes exact ITU-R BS.1770-5 multi-rate K-weighting Stage 1 (pre-filter shelf) and Stage 2 (RLB high-pass)
        /// biquad coefficients for any sample rate via exact bilinear transform of the normative analog prototype.
        /// </summary>
        public static void ComputeKWeightingCoefficients(
            int sampleRate,
            out double b10, out double b11, out double b12, out double a11, out double a12,
            out double b20, out double b21, out double b22, out double a21, out double a22) {
            double fs = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;

            // Stage 1: Acoustic head diffraction high-shelf
            double vh = Math.Pow(10.0, Stage1GainDb / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double k1 = Math.Tan(Math.PI * Stage1F0Hz / fs);
            double k1Sq = k1 * k1;
            double a10 = 1.0 + k1 / Stage1Q + k1Sq;
            b10 = (vh + vb * k1 / Stage1Q + k1Sq) / a10;
            b11 = 2.0 * (k1Sq - vh) / a10;
            b12 = (vh - vb * k1 / Stage1Q + k1Sq) / a10;
            a11 = 2.0 * (k1Sq - 1.0) / a10;
            a12 = (1.0 - k1 / Stage1Q + k1Sq) / a10;

            // Stage 2: Revised Low-frequency B-weighting (RLB) high-pass
            double k2 = Math.Tan(Math.PI * Stage2F0Hz / fs);
            double k2Sq = k2 * k2;
            double a20 = 1.0 + k2 / Stage2Q + k2Sq;
            b20 = 1.0;
            b21 = -2.0;
            b22 = 1.0;
            a21 = 2.0 * (k2Sq - 1.0) / a20;
            a22 = (1.0 - k2 / Stage2Q + k2Sq) / a20;
        }

        /// <summary>
        /// Configures sample rate, channel count, and optional per-channel BS.1770-5 weights
        /// (defaults to 1.0 for L/R/C/heights, 1.41 for Ls/Rs in 5.1+, 0.0 for LFE).
        /// </summary>
        public void Configure(int newSampleRate, int newChannels, double[] customChannelWeights = null) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            int ch = newChannels > 0 ? newChannels : 2;
            sampleRate = sr;
            channels = ch;
            hopSamples = Math.Max(1, (int)Math.Round(sr * 0.1));

            ComputeKWeightingCoefficients(
                sr,
                out s1b0, out s1b1, out s1b2, out s1a1, out s1a2,
                out s2b0, out s2b1, out s2b2, out s2a1, out s2a2);

            if (s1z1.Length != ch) {
                s1z1 = new double[ch];
                s1z2 = new double[ch];
                s2z1 = new double[ch];
                s2z2 = new double[ch];
                channelWeights = new double[ch];
            }

            if (customChannelWeights != null && customChannelWeights.Length >= ch) {
                for (int c = 0; c < ch; c++) {
                    channelWeights[c] = Math.Max(0.0, customChannelWeights[c]);
                }
            } else {
                // ITU-R BS.1770-5 channel weights: 1.0 for L/R/C, 0.0 for LFE (index 3 in standard 5.1), 1.41 for Ls/Rs
                for (int c = 0; c < ch; c++) {
                    if (ch >= 6 && c == 3) {
                        channelWeights[c] = 0.0; // LFE excluded from BS.1770 loudness sum
                    } else if (ch >= 5 && (c == 4 || c == 5)) {
                        channelWeights[c] = 1.4125375446227544; // +1.5 dB for surround channels
                    } else {
                        channelWeights[c] = 1.0;
                    }
                }
            }

            truePeakDetector.Configure(ch);
            Reset();
        }

        /// <summary>
        /// Resets all K-weighting filter states, sliding windows, Integrated/LRA histograms, and Max True Peak.
        /// </summary>
        public void Reset() {
            Array.Clear(s1z1, 0, s1z1.Length);
            Array.Clear(s1z2, 0, s1z2.Length);
            Array.Clear(s2z1, 0, s2z1.Length);
            Array.Clear(s2z2, 0, s2z2.Length);
            Array.Clear(hopWeightedEnergy, 0, hopWeightedEnergy.Length);
            Array.Clear(hopRawEnergy, 0, hopRawEnergy.Length);
            Array.Clear(integratedHist, 0, integratedHist.Length);
            Array.Clear(shortTermHist, 0, shortTermHist.Length);
            truePeakDetector.Reset();

            currentHopSampleCount = 0;
            currentHopWeightedSum = 0.0;
            currentHopRawSum = 0.0;
            hopWriteIndex = 0;
            filledHops = 0;
            totalIntegratedBlocks = 0;
            totalShortTermBlocks = 0;

            latestMomentaryLufs = float.NaN;
            latestShortTermLufs = float.NaN;
            latestIntegratedLufs = float.NaN;
            latestLraLu = 0f;
            latestBlockTruePeakLinear = 0f;
            runningMaxTruePeakLinear = 0f;
            latestBlockSamplePeak = 0f;
            latestRmsDbfs = -120f;
            latestCrestFactorDb = 0f;
        }

        /// <summary>
        /// Analyzes <paramref name="frameCount"/> interleaved float frames without modifying the audio buffer
        /// and without allocating on the managed heap.
        /// </summary>
        public void AnalyzeInterleaved(float[] buffer, int offset, int frameCount, int channelCount) {
            if (buffer == null || frameCount <= 0 || channelCount <= 0) {
                return;
            }
            if (channelCount != channels) {
                Configure(sampleRate, channelCount);
            }
            int end = offset + frameCount * channelCount;
            if (offset < 0 || end > buffer.Length) {
                return;
            }

            float blockTp = 0f;
            float blockSp = 0f;
            double invCh = 1.0 / channelCount;

            for (int f = 0; f < frameCount; f++) {
                int baseIdx = offset + f * channelCount;
                double frameWeightedSq = 0.0;
                double frameRawSq = 0.0;

                for (int c = 0; c < channelCount; c++) {
                    float xf = buffer[baseIdx + c];
                    float absX = MathF.Abs(xf);
                    if (absX > blockSp) {
                        blockSp = absX;
                    }
                    float tp = truePeakDetector.ProcessSample(c, xf);
                    if (tp > blockTp) {
                        blockTp = tp;
                    }

                    double x = xf;
                    frameRawSq += x * x;

                    // Stage 1: K-weighting High-Shelf (TDF-II)
                    double y1 = s1b0 * x + s1z1[c];
                    s1z1[c] = s1b1 * x - s1a1 * y1 + s1z2[c] + DenormalGuard;
                    s1z2[c] = s1b2 * x - s1a2 * y1;

                    // Stage 2: K-weighting RLB High-Pass (TDF-II)
                    double y2 = s2b0 * y1 + s2z1[c];
                    s2z1[c] = s2b1 * y1 - s2a1 * y2 + s2z2[c] + DenormalGuard;
                    s2z2[c] = s2b2 * y1 - s2a2 * y2;

                    frameWeightedSq += channelWeights[c] * (y2 * y2);
                }

                currentHopWeightedSum += frameWeightedSq;
                currentHopRawSum += frameRawSq * invCh;
                currentHopSampleCount++;

                if (currentHopSampleCount >= hopSamples) {
                    CommitHop();
                }
            }

            latestBlockTruePeakLinear = blockTp;
            if (blockTp > runningMaxTruePeakLinear) {
                runningMaxTruePeakLinear = blockTp;
            }
            latestBlockSamplePeak = blockSp;

            // Update instantaneous crest factor (True Peak - RMS) for W09/W11 reuse
            if (latestRmsDbfs > -100f && blockTp > 1e-6f) {
                float tpDb = TruePeakDetector.LinearToDbtp(blockTp);
                latestCrestFactorDb = MathF.Max(0f, tpDb - latestRmsDbfs);
            }
        }

        /// <summary>
        /// Stateless offline helper computing full ITU-R BS.1770-5 metrics (Integrated LUFS, Short-Term, LRA,
        /// 4x True Peak, RMS, Crest Factor) on any interleaved buffer. Directly reusable by W09 LGSR &amp; W11 Vocal Mix-Match.
        /// </summary>
        public static LoudnessAnalysisSnapshot MeasureBufferMetrics(
            float[] interleaved,
            int offset,
            int frameCount,
            int channels,
            int sampleRate) {
            LoudnessMeter meter = new LoudnessMeter(sampleRate, channels);
            meter.AnalyzeInterleaved(interleaved, offset, frameCount, channels);
            return meter.Snapshot;
        }

        void CommitHop() {
            double hopMeanWeighted = currentHopWeightedSum / currentHopSampleCount;
            double hopMeanRaw = currentHopRawSum / currentHopSampleCount;

            hopWeightedEnergy[hopWriteIndex] = hopMeanWeighted;
            hopRawEnergy[hopWriteIndex] = hopMeanRaw;
            hopWriteIndex++;
            if (hopWriteIndex >= ShortTermHops) {
                hopWriteIndex = 0;
            }
            if (filledHops < ShortTermHops) {
                filledHops++;
            }

            currentHopWeightedSum = 0.0;
            currentHopRawSum = 0.0;
            currentHopSampleCount = 0;

            // 400 ms Momentary loudness & unweighted RMS
            if (filledHops >= MomentaryHops) {
                double mEnergy = AverageLastHops(hopWeightedEnergy, MomentaryHops);
                double rawEnergy = AverageLastHops(hopRawEnergy, MomentaryHops);
                double mLufs = EnergyToLufs(mEnergy);
                latestMomentaryLufs = mLufs > -120.0 ? (float)mLufs : float.NaN;
                latestRmsDbfs = rawEnergy > 1e-12 ? (float)(10.0 * Math.Log10(rawEnergy)) : -120f;

                // Push every 100 ms overlapping 400 ms block into Integrated histogram (absolute gate -70 LUFS)
                if (mLufs > AbsoluteGateLufs) {
                    int bin = LufsToBin(mLufs);
                    integratedHist[bin]++;
                    totalIntegratedBlocks++;
                    latestIntegratedLufs = ComputeGatedIntegratedLufs();
                }
            }

            // 3.0 s Short-Term loudness & EBU Tech 3342 LRA
            if (filledHops >= ShortTermHops) {
                double sEnergy = AverageLastHops(hopWeightedEnergy, ShortTermHops);
                double sLufs = EnergyToLufs(sEnergy);
                latestShortTermLufs = sLufs > -120.0 ? (float)sLufs : float.NaN;

                if (sLufs > AbsoluteGateLufs) {
                    int bin = LufsToBin(sLufs);
                    shortTermHist[bin]++;
                    totalShortTermBlocks++;
                    latestLraLu = ComputeLoudnessRangeLu();
                }
            }
        }

        double AverageLastHops(double[] ring, int count) {
            double sum = 0.0;
            int idx = hopWriteIndex;
            for (int i = 0; i < count; i++) {
                idx--;
                if (idx < 0) {
                    idx = ShortTermHops - 1;
                }
                sum += ring[idx];
            }
            return sum / count;
        }

        float ComputeGatedIntegratedLufs() {
            if (totalIntegratedBlocks == 0) {
                return float.NaN;
            }
            // Stage 1: Average energy of all blocks above -70.0 LUFS absolute gate
            double absSumEnergy = 0.0;
            for (int b = 0; b < HistBinCount; b++) {
                long c = integratedHist[b];
                if (c > 0) {
                    absSumEnergy += c * BinCenterEnergy(b);
                }
            }
            double ungatedLufs = EnergyToLufs(absSumEnergy / totalIntegratedBlocks);
            double relThresholdLufs = ungatedLufs + IntegratedRelativeOffsetLu;
            int startBin = LufsToBin(relThresholdLufs);

            // Stage 2: Average energy of all blocks above relative gate (ungated - 10 LU)
            double relSumEnergy = 0.0;
            long relCount = 0;
            for (int b = startBin; b < HistBinCount; b++) {
                long c = integratedHist[b];
                if (c > 0) {
                    relSumEnergy += c * BinCenterEnergy(b);
                    relCount += c;
                }
            }
            if (relCount == 0) {
                return (float)ungatedLufs;
            }
            return (float)EnergyToLufs(relSumEnergy / relCount);
        }

        float ComputeLoudnessRangeLu() {
            if (totalShortTermBlocks < 2) {
                return 0f;
            }
            double absSumEnergy = 0.0;
            for (int b = 0; b < HistBinCount; b++) {
                long c = shortTermHist[b];
                if (c > 0) {
                    absSumEnergy += c * BinCenterEnergy(b);
                }
            }
            double meanShortTerm = EnergyToLufs(absSumEnergy / totalShortTermBlocks);
            double relGate = meanShortTerm + LraRelativeOffsetLu;
            int startBin = LufsToBin(relGate);

            long gatedCount = 0;
            for (int b = startBin; b < HistBinCount; b++) {
                gatedCount += shortTermHist[b];
            }
            if (gatedCount < 2) {
                return 0f;
            }

            long p10Target = Math.Max(1L, (long)Math.Ceiling(gatedCount * 0.10));
            long p95Target = Math.Max(1L, (long)Math.Ceiling(gatedCount * 0.95));
            double p10Lufs = BinCenterLufs(startBin);
            double p95Lufs = BinCenterLufs(startBin);
            long accum = 0;
            bool found10 = false;

            for (int b = startBin; b < HistBinCount; b++) {
                long c = shortTermHist[b];
                if (c == 0) {
                    continue;
                }
                accum += c;
                if (!found10 && accum >= p10Target) {
                    p10Lufs = BinCenterLufs(b);
                    found10 = true;
                }
                if (accum >= p95Target) {
                    p95Lufs = BinCenterLufs(b);
                    break;
                }
            }
            return (float)Math.Max(0.0, p95Lufs - p10Lufs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double EnergyToLufs(double energy) =>
            energy > 1e-13 ? LoudnessOffsetDb + 10.0 * Math.Log10(energy) : -140.0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int LufsToBin(double lufs) {
            int bin = (int)Math.Floor((lufs - HistMinLufs) / HistBinWidthLu);
            if (bin < 0) {
                return 0;
            }
            if (bin >= HistBinCount) {
                return HistBinCount - 1;
            }
            return bin;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double BinCenterLufs(int bin) => HistMinLufs + (bin + 0.5) * HistBinWidthLu;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double BinCenterEnergy(int bin) => Math.Pow(10.0, (BinCenterLufs(bin) - LoudnessOffsetDb) * 0.1);
    }
}
