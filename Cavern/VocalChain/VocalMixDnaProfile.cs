using System;
using System.Numerics;

namespace Cavern.VocalChain {
    /// <summary>
    /// Extracted song vocal mixing and mastering DNA produced by analyzing an isolated Lead Vocal stem
    /// (and optional Dereverb Dry/Wet pair) to drive zero-latency live microphone Auto-Vocal Mix-Match.
    /// </summary>
    public sealed class VocalMixDnaProfile {
        /// <summary>Center frequencies (Hz) of the 8 ERB-spaced formant-safe vocal analysis bands.</summary>
        public static readonly float[] AnalysisBandHz = new float[] {
            120f, 250f, 500f, 1000f, 2000f, 3800f, 6800f, 11500f
        };

        /// <summary>Band energies (dBFS relative to full-scale) across <see cref="AnalysisBandHz"/>.</summary>
        public float[] BandEnergyDb { get; set; } = new float[8];

        /// <summary>Overall spectral tilt in dB/octave (positive = brighter presence/air).</summary>
        public float SpectralTiltDbPerOct { get; set; }

        /// <summary>Estimated sibilance energy ratio in 5-10 kHz band relative to 1-3 kHz body (dB).</summary>
        public float SibilanceIndexDb { get; set; } = -6f;

        /// <summary>Target integrated vocal loudness relative to instrumental mix (LUFS).</summary>
        public float TargetVocalLufs { get; set; } = -14f;

        /// <summary>Measured vocal crest factor (Peak minus RMS in dB); lower values imply heavier compression.</summary>
        public float CrestFactorDb { get; set; } = 10.5f;

        /// <summary>Estimated harmonic saturation warmth index in [0; 1].</summary>
        public float HarmonicWarmth { get; set; } = 0.28f;

        /// <summary>Estimated reverb decay time RT60 in seconds (0.2..4.5 s).</summary>
        public float ReverbRt60Sec { get; set; } = 1.25f;

        /// <summary>Estimated reverb pre-delay in milliseconds (0..80 ms).</summary>
        public float ReverbPreDelayMs { get; set; } = 24f;

        /// <summary>Estimated wet-to-dry reverb send ratio in [0; 0.65].</summary>
        public float ReverbWetMix { get; set; } = 0.18f;

        /// <summary>High-frequency reverb damping cutoff frequency in Hz (2000..12000 Hz).</summary>
        public float ReverbDampingHz { get; set; } = 5800f;

        /// <summary>Detected slapback or rhythmic stereo delay tap time in milliseconds (0 = none).</summary>
        public float DelayTimeMs { get; set; }

        /// <summary>Detected stereo delay wet mix in [0; 0.35] (0 = none).</summary>
        public float DelayWetMix { get; set; }

        /// <summary>Estimated stereo width / doubling spread in [0; 1].</summary>
        public float StereoWidth { get; set; } = 0.25f;

        /// <summary>Extracted 3D Atmos normalized vocal position (X, Y, Z).</summary>
        public Vector3 VocalAtmosPosition { get; set; } = new Vector3(0f, 0.08f, 0.78f);

        /// <summary>True when this profile was extracted from an actual isolated song vocal stem (false = neutral studio baseline).</summary>
        public bool IsExtractedFromSong { get; set; }

        /// <summary>True when a Dereverb dry/wet neural decomposition contributed measured DRR/RT60 parameters.</summary>
        public bool HasMeasuredDereverb { get; set; }

        /// <summary>Measured physical Direct-to-Reverberant Ratio in dB (<c>NaN</c> when unmeasured).</summary>
        public float MeasuredDrrDb { get; set; } = float.NaN;

        /// <summary>Measured active-speech RMS level across analyzed windows (dBFS).</summary>
        public float ActiveSpeechRmsDbfs { get; set; } = -96f;

        /// <summary>Number of active vocal frames/windows that contributed to this profile.</summary>
        public int ActiveWindowCount { get; set; }

        /// <summary>Optional human-readable track/source summary associated with this extracted DNA.</summary>
        public string SourceTrackSummary { get; set; } = string.Empty;

        /// <summary>Creates a deep copy safe for lock-free audio-thread config snapshots.</summary>
        public VocalMixDnaProfile Clone() {
            VocalMixDnaProfile copy = new VocalMixDnaProfile {
                SpectralTiltDbPerOct = SpectralTiltDbPerOct,
                SibilanceIndexDb = SibilanceIndexDb,
                TargetVocalLufs = TargetVocalLufs,
                CrestFactorDb = CrestFactorDb,
                HarmonicWarmth = HarmonicWarmth,
                ReverbRt60Sec = ReverbRt60Sec,
                ReverbPreDelayMs = ReverbPreDelayMs,
                ReverbWetMix = ReverbWetMix,
                ReverbDampingHz = ReverbDampingHz,
                DelayTimeMs = DelayTimeMs,
                DelayWetMix = DelayWetMix,
                StereoWidth = StereoWidth,
                VocalAtmosPosition = VocalAtmosPosition,
                IsExtractedFromSong = IsExtractedFromSong,
                HasMeasuredDereverb = HasMeasuredDereverb,
                MeasuredDrrDb = MeasuredDrrDb,
                ActiveSpeechRmsDbfs = ActiveSpeechRmsDbfs,
                ActiveWindowCount = ActiveWindowCount,
                SourceTrackSummary = SourceTrackSummary ?? string.Empty
            };
            copy.BandEnergyDb = new float[AnalysisBandHz.Length];
            if (BandEnergyDb != null) {
                int len = Math.Min(BandEnergyDb.Length, copy.BandEnergyDb.Length);
                Array.Copy(BandEnergyDb, copy.BandEnergyDb, len);
            }
            return copy;
        }

        /// <summary>Creates a transparent neutral studio baseline (zero artificial delay or EQ coloration).</summary>
        public static VocalMixDnaProfile CreateStudioDefault() {
            VocalMixDnaProfile profile = new VocalMixDnaProfile();
            profile.BandEnergyDb = new float[] { -26f, -18f, -15f, -14f, -13.5f, -12.5f, -16f, -20f };
            profile.DelayTimeMs = 0f;
            profile.DelayWetMix = 0f;
            profile.IsExtractedFromSong = false;
            profile.HasMeasuredDereverb = false;
            profile.MeasuredDrrDb = float.NaN;
            return profile;
        }
    }

    /// <summary>
    /// User-facing live vocal chain macro controls and bypass switches.
    /// </summary>
    public sealed class VocalMacroSettings {
        /// <summary>Master enable for Slot 2D live vocal chain.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Instant A/B bypass (passes clean dry mic with level match when true).</summary>
        public bool Bypass { get; set; }

        /// <summary>Mix Match macro strength in [0; 100]% (scales Match EQ, Dynamics, Saturation, and Space).</summary>
        public float MixMatchPct { get; set; } = 75f;

        /// <summary>Reverb &amp; Delay macro send multiplier in [0; 150]%.</summary>
        public float ReverbPct { get; set; } = 100f;

        /// <summary>Dual-stage FET + Optical compression intensity in [0; 150]%.</summary>
        public float DynamicsPct { get; set; } = 100f;

        /// <summary>Analog ADAA tube/tape saturation warmth in [0; 100]%.</summary>
        public float WarmthPct { get; set; } = 35f;

        /// <summary>Output trim gain in dB (-18..+12 dB).</summary>
        public float VocalLevelDb { get; set; }

        /// <summary>Enable dynamic 1-4 kHz vocal-pocket sidechain carving on the backing instrumental.</summary>
        public bool EnableSidechainPocketGlue { get; set; } = true;

        /// <summary>Enable noise gate / soft expander stage.</summary>
        public bool EnableNoiseGate { get; set; } = true;

        /// <summary>Enable split-band 5-10 kHz adaptive de-esser.</summary>
        public bool EnableDeEsser { get; set; } = true;

        /// <summary>Creates a deep copy safe for lock-free audio-thread config snapshots.</summary>
        public VocalMacroSettings Clone() {
            return new VocalMacroSettings {
                Enabled = Enabled,
                Bypass = Bypass,
                MixMatchPct = MixMatchPct,
                ReverbPct = ReverbPct,
                DynamicsPct = DynamicsPct,
                WarmthPct = WarmthPct,
                VocalLevelDb = VocalLevelDb,
                EnableSidechainPocketGlue = EnableSidechainPocketGlue,
                EnableNoiseGate = EnableNoiseGate,
                EnableDeEsser = EnableDeEsser
            };
        }
    }

    /// <summary>
    /// Live real-time metering telemetry produced by <see cref="LiveVocalChainDsp"/> for UI meters and Spatial X-Ray.
    /// </summary>
    public struct VocalChainTelemetry {
        /// <summary>Input vocal level before processing (dBFS).</summary>
        public float InputDbfs;

        /// <summary>Output vocal level after processing (dBFS).</summary>
        public float OutputDbfs;

        /// <summary>Active noise gate attenuation (dB, &lt;= 0).</summary>
        public float GateReductionDb;

        /// <summary>Active split-band de-esser reduction (dB, &lt;= 0).</summary>
        public float DeEsserReductionDb;

        /// <summary>Combined FET 1176 + Optical LA-2A gain reduction (dB, &lt;= 0).</summary>
        public float CompressorReductionDb;

        /// <summary>Active 1-4 kHz instrumental sidechain pocket ducking (dB, &lt;= 0).</summary>
        public float SidechainPocketDuckDb;
    }

    /// <summary>
    /// Extracts <see cref="VocalMixDnaProfile"/> parameters from an isolated lead vocal stem and optional dry/wet pair
    /// using zero-dependency pure-physics signal analysis (multi-window active-RMS Goertzel ERB band power,
    /// crest factor, DRR/RT60/damping, and envelope autocorrelation delay detection).
    /// </summary>
    public static class VocalMixProfileExtractor {
        const int MaxCandidateWindows = 240;
        const int MaxGoertzelActiveWindows = 32;
        const float MinVoiceprintRmsDbfs = -48f;

        /// <summary>
        /// Analyzes a mono/interleaved vocal stem buffer and optional dereverbed dry reference across the full track
        /// using active-RMS vocal windowing to build a <see cref="VocalMixDnaProfile"/>.
        /// </summary>
        public static VocalMixDnaProfile Analyze(
            float[] leadVocal,
            int channels,
            int sampleRate,
            float[] dereverbedDry = null,
            Vector3? atmosPosition = null,
            string sourceSummary = null) {
            VocalMixDnaProfile profile = VocalMixDnaProfile.CreateStudioDefault();
            if (atmosPosition.HasValue) {
                profile.VocalAtmosPosition = atmosPosition.Value;
            }
            if (!string.IsNullOrEmpty(sourceSummary)) {
                profile.SourceTrackSummary = sourceSummary;
            }
            if (leadVocal == null || leadVocal.Length < 256 || sampleRate < 8000) {
                return profile;
            }

            int ch = Math.Max(1, channels);
            int totalFrames = Math.Min(leadVocal.Length / ch, sampleRate * 300);
            if (totalFrames < 256) {
                return profile;
            }

            bool hasDry = dereverbedDry != null && dereverbedDry.Length >= totalFrames * ch;
            return AnalyzeCore(
                totalFrames,
                sampleRate,
                hasStereo: ch >= 2,
                hasDry: hasDry,
                readLeadL: i => leadVocal[i * ch],
                readLeadR: ch >= 2 ? (Func<int, float>)(i => leadVocal[i * ch + 1]) : (i => leadVocal[i * ch]),
                readDryL: hasDry ? (Func<int, float>)(i => dereverbedDry[i * ch]) : null,
                readDryR: hasDry ? (ch >= 2 ? (Func<int, float>)(i => dereverbedDry[i * ch + 1]) : (i => dereverbedDry[i * ch])) : null,
                profile: profile);
        }

        /// <summary>
        /// Analyzes planar stereo (or mono) vocal stem buffers directly without allocating an interleaved copy.
        /// </summary>
        public static VocalMixDnaProfile AnalyzePlanar(
            float[] leadLeft,
            float[] leadRight,
            int sampleRate,
            float[] dryLeft = null,
            float[] dryRight = null,
            Vector3? atmosPosition = null,
            string sourceSummary = null) {
            VocalMixDnaProfile profile = VocalMixDnaProfile.CreateStudioDefault();
            if (atmosPosition.HasValue) {
                profile.VocalAtmosPosition = atmosPosition.Value;
            }
            if (!string.IsNullOrEmpty(sourceSummary)) {
                profile.SourceTrackSummary = sourceSummary;
            }
            if (leadLeft == null || leadLeft.Length < 256 || sampleRate < 8000) {
                return profile;
            }

            bool hasRight = leadRight != null && leadRight.Length >= leadLeft.Length;
            int totalFrames = Math.Min(leadLeft.Length, sampleRate * 300);
            if (totalFrames < 256) {
                return profile;
            }

            bool hasDry = dryLeft != null && dryLeft.Length >= totalFrames;
            bool hasDryRight = hasDry && dryRight != null && dryRight.Length >= totalFrames;
            return AnalyzeCore(
                totalFrames,
                sampleRate,
                hasStereo: hasRight,
                hasDry: hasDry,
                readLeadL: i => leadLeft[i],
                readLeadR: hasRight ? (Func<int, float>)(i => leadRight[i]) : (i => leadLeft[i]),
                readDryL: hasDry ? (Func<int, float>)(i => dryLeft[i]) : null,
                readDryR: hasDry ? (hasDryRight ? (Func<int, float>)(i => dryRight[i]) : (i => dryLeft[i])) : null,
                profile: profile);
        }

        /// <summary>
        /// Returns true when a captured microphone voiceprint contains sufficient active speech energy
        /// to compute reliable formant-safe Match EQ deltas without fitting room noise.
        /// </summary>
        public static bool IsSufficientVoiceprintSignal(VocalMixDnaProfile micProfile) {
            return micProfile != null &&
                micProfile.ActiveWindowCount >= 3 &&
                micProfile.ActiveSpeechRmsDbfs >= MinVoiceprintRmsDbfs;
        }

        /// <summary>
        /// Computes formant-safe bounded Match EQ deltas (in dB, capped at +/- 6 dB) between song vocal DNA and user mic voiceprint.
        /// </summary>
        public static float[] ComputeFormantSafeMatchEqDb(float[] songBandDb, float[] micBandDb, float maxGainDb = 6f) {
            int count = VocalMixDnaProfile.AnalysisBandHz.Length;
            float[] deltas = new float[count];
            if (songBandDb == null || micBandDb == null || songBandDb.Length < count || micBandDb.Length < count) {
                return deltas;
            }
            // Remove global level offset so Match EQ shapes timbre without shifting overall loudness
            float songAvg = 0f;
            float micAvg = 0f;
            for (int i = 0; i < count; i++) {
                songAvg += songBandDb[i];
                micAvg += micBandDb[i];
            }
            songAvg /= count;
            micAvg /= count;

            for (int i = 0; i < count; i++) {
                float rawDelta = (songBandDb[i] - songAvg) - (micBandDb[i] - micAvg);
                // Damp extreme sub/air bands slightly for natural voice preservation
                float safetyWeight = (i == 0 || i == count - 1) ? 0.55f : 0.78f;
                deltas[i] = Clamp(rawDelta * safetyWeight, -maxGainDb, maxGainDb);
            }
            return deltas;
        }

        static VocalMixDnaProfile AnalyzeCore(
            int totalFrames,
            int sampleRate,
            bool hasStereo,
            bool hasDry,
            Func<int, float> readLeadL,
            Func<int, float> readLeadR,
            Func<int, float> readDryL,
            Func<int, float> readDryR,
            VocalMixDnaProfile profile) {
            int windowLen = Math.Min(4096, totalFrames);
            int maxPossibleStarts = Math.Max(1, totalFrames - windowLen + 1);
            int candidateCount = Math.Min(MaxCandidateWindows, Math.Max(1, totalFrames / Math.Max(512, windowLen / 2)));
            int[] candidateStarts = new int[candidateCount];
            double[] candidateRms = new double[candidateCount];

            double maxWinRms = 0.0;
            int loudestWindowIdx = 0;
            for (int w = 0; w < candidateCount; w++) {
                int start = candidateCount == 1
                    ? 0
                    : (int)(((long)w * (maxPossibleStarts - 1)) / (candidateCount - 1));
                candidateStarts[w] = start;

                double winSq = 0.0;
                int stride = windowLen >= 2048 ? 2 : 1;
                int count = 0;
                for (int i = 0; i < windowLen; i += stride) {
                    int idx = start + i;
                    float m = 0.5f * (readLeadL(idx) + readLeadR(idx));
                    winSq += m * m;
                    count++;
                }
                double rmsW = count > 0 ? Math.Sqrt(winSq / count) : 0.0;
                candidateRms[w] = rmsW;
                if (rmsW > maxWinRms) {
                    maxWinRms = rmsW;
                    loudestWindowIdx = w;
                }
            }

            // Select active vocal windows: >= -52 dBFS and within -18 dB of the track's peak vocal window
            double activeFloor = Math.Max(0.0025, maxWinRms * 0.125);
            int[] activeIndices = new int[candidateCount];
            int activeCount = 0;
            for (int w = 0; w < candidateCount; w++) {
                if (candidateRms[w] >= activeFloor) {
                    activeIndices[activeCount++] = w;
                }
            }
            if (activeCount == 0 && maxWinRms > 1e-6) {
                activeIndices[activeCount++] = loudestWindowIdx;
            }

            profile.ActiveWindowCount = activeCount;
            if (activeCount == 0) {
                profile.ActiveSpeechRmsDbfs = -96f;
                return profile;
            }

            // Pick up to MaxGoertzelActiveWindows evenly distributed active windows across the full track
            int evalCount = Math.Min(MaxGoertzelActiveWindows, activeCount);
            double activeMidSqSum = 0.0;
            double activeSideSqSum = 0.0;
            long activeSamples = 0;
            float activePeak = 1e-6f;
            int bandCount = VocalMixDnaProfile.AnalysisBandHz.Length;
            double[] weightedBandPower = new double[bandCount];
            double totalWeight = 0.0;

            double drySqTotal = 0.0;
            double wetSqTotal = 0.0;
            double wetMidBandPower = 0.0;
            double wetHighBandPower = 0.0;

            for (int e = 0; e < evalCount; e++) {
                int wIdx = evalCount == 1
                    ? activeIndices[0]
                    : activeIndices[(e * (activeCount - 1)) / (evalCount - 1)];
                int start = candidateStarts[wIdx];
                double weight = Math.Max(1e-6, candidateRms[wIdx] * candidateRms[wIdx]);
                totalWeight += weight;

                for (int i = 0; i < windowLen; i++) {
                    int idx = start + i;
                    float l = readLeadL(idx);
                    float r = readLeadR(idx);
                    float m = 0.5f * (l + r);
                    float s = 0.5f * (l - r);
                    float absM = Math.Abs(m);
                    if (absM > activePeak) {
                        activePeak = absM;
                    }
                    activeMidSqSum += m * m;
                    activeSideSqSum += s * s;
                    activeSamples++;

                    if (hasDry) {
                        float dm = 0.5f * (readDryL(idx) + readDryR(idx));
                        float tail = m - dm;
                        drySqTotal += dm * dm;
                        wetSqTotal += tail * tail;
                    }
                }

                for (int b = 0; b < bandCount; b++) {
                    double p = MeasureGoertzelPowerRange(readLeadL, readLeadR, start, windowLen, sampleRate, VocalMixDnaProfile.AnalysisBandHz[b]);
                    weightedBandPower[b] += p * weight;
                }

                if (hasDry) {
                    wetMidBandPower += MeasureGoertzelDiffPowerRange(readLeadL, readLeadR, readDryL, readDryR, start, windowLen, sampleRate, 1000f) * weight;
                    wetHighBandPower += MeasureGoertzelDiffPowerRange(readLeadL, readLeadR, readDryL, readDryR, start, windowLen, sampleRate, 6800f) * weight;
                }
            }

            double activeRms = activeSamples > 0 ? Math.Sqrt(activeMidSqSum / activeSamples) : 0.0;
            float rmsDbfs = activeRms > 1e-6 ? (float)(20.0 * Math.Log10(activeRms)) : -96f;
            float peakDbfs = (float)(20.0 * Math.Log10(activePeak));
            profile.ActiveSpeechRmsDbfs = Clamp(rmsDbfs, -96f, 0f);
            profile.TargetVocalLufs = Clamp(rmsDbfs - 0.7f, -28f, -8f);
            profile.CrestFactorDb = Clamp(peakDbfs - rmsDbfs, 4.5f, 20f);
            profile.StereoWidth = hasStereo && activeMidSqSum > 1e-9
                ? Clamp((float)Math.Sqrt(activeSideSqSum / (activeMidSqSum + activeSideSqSum)) * 1.4f, 0f, 1f)
                : 0.15f;

            for (int b = 0; b < bandCount; b++) {
                double avgPower = totalWeight > 0.0 ? weightedBandPower[b] / totalWeight : 0.0;
                profile.BandEnergyDb[b] = avgPower > 1e-12
                    ? Clamp((float)(10.0 * Math.Log10(avgPower)), -72f, 0f)
                    : -72f;
            }

            // Compute spectral tilt (presence/air 3.8-6.8 kHz minus low-mid 250-500 Hz)
            float lowMid = 0.5f * (profile.BandEnergyDb[1] + profile.BandEnergyDb[2]);
            float presenceAir = 0.5f * (profile.BandEnergyDb[5] + profile.BandEnergyDb[6]);
            profile.SpectralTiltDbPerOct = Clamp((presenceAir - lowMid) / 4f, -4f, 4f);
            profile.SibilanceIndexDb = Clamp(profile.BandEnergyDb[6] - profile.BandEnergyDb[3], -18f, 6f);

            // Real physical Dereverb dry/wet analysis across active vocal windows
            if (hasDry && drySqTotal > 1e-9 && wetSqTotal > 1e-12) {
                double ratio = wetSqTotal / (drySqTotal + wetSqTotal);
                double drrDb = 10.0 * Math.Log10(drySqTotal / wetSqTotal);
                profile.HasMeasuredDereverb = true;
                profile.MeasuredDrrDb = Clamp((float)drrDb, -12f, 30f);
                profile.ReverbWetMix = Clamp((float)ratio * 0.85f, 0.04f, 0.55f);
                profile.ReverbRt60Sec = Clamp(0.45f + (float)ratio * 3.2f, 0.30f, 3.8f);
                if (wetMidBandPower > 1e-14 && wetHighBandPower > 1e-14) {
                    float wetSlopeDb = (float)(10.0 * Math.Log10(wetHighBandPower / wetMidBandPower));
                    profile.ReverbDampingHz = Clamp(5600f * (float)Math.Pow(2.0, wetSlopeDb / 14.0), 2000f, 11500f);
                }
            }

            // Real envelope autocorrelation slapback / rhythmic delay estimation around the loudest active phrase
            int phraseCenter = candidateStarts[loudestWindowIdx] + windowLen / 2;
            EstimateSlapbackDelayAutocorr(
                readLeadL, readLeadR, totalFrames, sampleRate, phraseCenter,
                out float estDelayMs, out float estDelayWet);
            profile.DelayTimeMs = estDelayMs;
            profile.DelayWetMix = estDelayWet;

            // Map lower crest factor to analog saturation warmth target
            float compTightness = Clamp((14f - profile.CrestFactorDb) / 10f, 0f, 1f);
            profile.HarmonicWarmth = Clamp(0.18f + compTightness * 0.42f, 0.1f, 0.7f);
            profile.IsExtractedFromSong = true;

            return profile;
        }

        static void EstimateSlapbackDelayAutocorr(
            Func<int, float> readLeadL,
            Func<int, float> readLeadR,
            int totalFrames,
            int sampleRate,
            int centerFrame,
            out float delayMs,
            out float delayWetMix) {
            delayMs = 0f;
            delayWetMix = 0f;

            // Analyze up to 6 seconds around the strongest active vocal phrase using a 500 Hz (2 ms) RMS envelope
            int segFrames = Math.Min(totalFrames, sampleRate * 6);
            if (segFrames < (int)(sampleRate * 0.8)) {
                return;
            }
            int start = Math.Max(0, Math.Min(totalFrames - segFrames, centerFrame - segFrames / 2));
            int hop = Math.Max(1, sampleRate / 500); // 2 ms per envelope sample
            int envLen = segFrames / hop;
            int minLag = Math.Max(10, (int)Math.Round(0.045 * sampleRate / hop)); // 45 ms
            int maxLag = Math.Min(envLen / 3, (int)Math.Round(0.320 * sampleRate / hop)); // 320 ms
            if (maxLag <= minLag + 6) {
                return;
            }

            double[] env = new double[envLen];
            double mean = 0.0;
            for (int k = 0; k < envLen; k++) {
                int baseIdx = start + k * hop;
                double sq = 0.0;
                for (int j = 0; j < hop; j++) {
                    float m = 0.5f * (readLeadL(baseIdx + j) + readLeadR(baseIdx + j));
                    sq += m * m;
                }
                double v = Math.Sqrt(sq / hop);
                env[k] = v;
                mean += v;
            }
            mean /= envLen;

            double r0 = 0.0;
            for (int k = 0; k < envLen; k++) {
                env[k] -= mean;
                r0 += env[k] * env[k];
            }
            if (r0 <= 1e-9) {
                return;
            }

            double[] corr = new double[maxLag + 1];
            for (int lag = minLag - 2; lag <= maxLag; lag++) {
                if (lag < 1) {
                    continue;
                }
                double sum = 0.0;
                int n = envLen - lag;
                for (int k = 0; k < n; k++) {
                    sum += env[k] * env[k + lag];
                }
                corr[lag] = sum / r0;
            }

            int bestLag = -1;
            double bestPeak = 0.0;
            double bestProminence = 0.0;
            for (int lag = minLag; lag < maxLag; lag++) {
                double c = corr[lag];
                if (c < 0.32 || c <= corr[lag - 1] || c < corr[lag + 1]) {
                    continue;
                }
                // Measure local prominence against neighboring baseline excluding +/- 3 bins around peak
                double bgSum = 0.0;
                int bgCount = 0;
                int lo = Math.Max(minLag, lag - 12);
                int hi = Math.Min(maxLag, lag + 12);
                for (int k = lo; k <= hi; k++) {
                    if (Math.Abs(k - lag) > 3) {
                        bgSum += corr[k];
                        bgCount++;
                    }
                }
                double bg = bgCount > 0 ? bgSum / bgCount : 0.0;
                double prom = c - bg;
                if (prom >= 0.14 && c > bestPeak) {
                    bestPeak = c;
                    bestProminence = prom;
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && bestProminence >= 0.14) {
                float stepMs = (hop * 1000f) / sampleRate;
                delayMs = Clamp(bestLag * stepMs, 45f, 320f);
                delayWetMix = Clamp((float)((bestPeak - 0.25) * 0.45), 0.04f, 0.28f);
            }
        }

        static double MeasureGoertzelPowerRange(
            Func<int, float> readL,
            Func<int, float> readR,
            int start,
            int frames,
            int sampleRate,
            float targetHz) {
            double omega = 2.0 * Math.PI * targetHz / sampleRate;
            double coeff = 2.0 * Math.Cos(omega);
            double s0 = 0.0;
            double s1 = 0.0;
            double s2 = 0.0;
            double invDen = 1.0 / Math.Max(1, frames - 1);
            for (int i = 0; i < frames; i++) {
                double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i * invDen);
                int idx = start + i;
                double x = 0.5 * (readL(idx) + readR(idx)) * w;
                s0 = x + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Max(0.0, power / (frames * (double)frames));
        }

        static double MeasureGoertzelDiffPowerRange(
            Func<int, float> readLeadL,
            Func<int, float> readLeadR,
            Func<int, float> readDryL,
            Func<int, float> readDryR,
            int start,
            int frames,
            int sampleRate,
            float targetHz) {
            double omega = 2.0 * Math.PI * targetHz / sampleRate;
            double coeff = 2.0 * Math.Cos(omega);
            double s0 = 0.0;
            double s1 = 0.0;
            double s2 = 0.0;
            double invDen = 1.0 / Math.Max(1, frames - 1);
            for (int i = 0; i < frames; i++) {
                double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i * invDen);
                int idx = start + i;
                double leadM = 0.5 * (readLeadL(idx) + readLeadR(idx));
                double dryM = 0.5 * (readDryL(idx) + readDryR(idx));
                double x = (leadM - dryM) * w;
                s0 = x + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Max(0.0, power / (frames * (double)frames));
        }

        static float Clamp(float v, float min, float max) {
            if (v < min) {
                return min;
            }
            if (v > max) {
                return max;
            }
            return v;
        }
    }
}
