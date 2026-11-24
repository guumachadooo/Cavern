using System;

using Cavern.ProAudio;

namespace Cavern.VocalChain {
    /// <summary>
    /// Zero-latency, zero-GC mastering-class live vocal DSP rack (Slot 2D) implementing:
    /// Noise Gate/Expander -> 2nd-order HPF + Dynamic Proximity Tamer -> Formant-Safe Match EQ &amp; Air Shelf ->
    /// Split-Band 5-10 kHz Adaptive De-Esser -> Dual-Stage Serial Compressor (FET 1176 + Optical LA-2A T4 ballistics) ->
    /// 1st-Order ADAA Anti-Aliased Analog Saturation (<c>tanh</c> antiderivative <c>ln(cosh(x))</c>) ->
    /// Matched Stereo Delay + 8-Channel Modulated Hadamard FDN Reverb -> Dynamic 1-4 kHz Vocal-Pocket Sidechain Glue.
    /// Strict C# 8.0 (<c>netstandard2.1</c>) with zero steady-state allocations.
    /// </summary>
    public sealed class LiveVocalChainDsp {
        const int FdnChannels = 8;
        const int MaxFdnLineLength = 12288; // Supports up to 2857 * 4 = 11428 @ 192 kHz without comb collapse
        const int MaxDelaySamples = 96000;  // 500 ms @ 192 kHz (2000 ms @ 48 kHz)
        static readonly int[] FdnPrimeLengthsAt48k = new int[] {
            1103, 1307, 1523, 1747, 1993, 2267, 2551, 2857
        };

        int sampleRate = 48000;
        VocalMixDnaProfile dna = VocalMixDnaProfile.CreateStudioDefault();
        VocalMacroSettings macros = new VocalMacroSettings();

        // Biquad states (TDF-II)
        BiquadState hpfState;
        BiquadState lowShelfMatch;
        BiquadState midBellMatch;
        BiquadState airShelfMatch;
        BiquadState deEssBandpass;
        BiquadState pocketCarveL;
        BiquadState pocketCarveR;

        // Envelope followers & ballistics state
        float gateEnv;
        float gateGain = 1f;
        float deEssEnv;
        float fetEnv;
        float optoFastEnv;
        float optoSlowEnv;
        float voiceActivityEnv;

        // ADAA anti-aliased tanh saturation state (x[n-1], F1(x[n-1]), 4x interp prev, and HF-comp prev)
        float adaaPrevX;
        float adaaPrevF1;
        float adaaPrevRaw;
        float adaaPrevOut;

        // Stereo slapback delay + 8-channel Hadamard FDN reverb state (pre-allocated)
        readonly float[] delayBufL = new float[MaxDelaySamples];
        readonly float[] delayBufR = new float[MaxDelaySamples];
        int delayWritePos;

        readonly float[][] fdnLines = new float[FdnChannels][];
        readonly int[] fdnWritePos = new int[FdnChannels];
        readonly int[] fdnLengths = new int[FdnChannels];
        readonly float[] fdnFeedbackPerLine = new float[FdnChannels];
        readonly float[] fdnLpState = new float[FdnChannels];
        readonly float[] fdnTempA = new float[FdnChannels];
        readonly float[] fdnTempB = new float[FdnChannels];

        VocalChainTelemetry telemetry;
        HardwarePreAmpStateSnapshot hardwarePreAmp = HardwarePreAmpStateSnapshot.Empty;
        long lastGainStepEpoch;
        float cachedRawAirGainDb;
        bool appliedHwAirActive;

        /// <summary>Latest real-time metering snapshot.</summary>
        public VocalChainTelemetry Telemetry {
            get { return telemetry; }
        }

        /// <summary>Latest hardware pre-ADC gain-step epoch stored for Phase 3 <c>Slot 2D-SHIELD</c> FDKF covariance reset.</summary>
        public long LastGainStepEpoch {
            get { return lastGainStepEpoch; }
        }

        /// <summary>Latest hardware pre-ADC state snapshot bound to this DSP instance.</summary>
        public HardwarePreAmpStateSnapshot HardwarePreAmpState {
            get { return hardwarePreAmp; }
        }

        /// <summary>Creates and pre-allocates the zero-GC live vocal DSP rack.</summary>
        public LiveVocalChainDsp(int sampleRateHz = 48000) {
            for (int i = 0; i < FdnChannels; i++) {
                fdnLines[i] = new float[MaxFdnLineLength];
            }
            Configure(sampleRateHz, dna, macros);
        }

        /// <summary>Updates sample rate, song vocal DNA profile, and user macro settings.</summary>
        public void Configure(int sampleRateHz, VocalMixDnaProfile profile, VocalMacroSettings macroSettings, float[] matchEqDeltasDb = null) {
            Configure(sampleRateHz, profile, macroSettings, matchEqDeltasDb, hardwarePreAmp);
        }

        /// <summary>Updates sample rate, song vocal DNA profile, user macro settings, and hardware pre-ADC preamp state.</summary>
        public void Configure(
            int sampleRateHz,
            VocalMixDnaProfile profile,
            VocalMacroSettings macroSettings,
            float[] matchEqDeltasDb,
            HardwarePreAmpStateSnapshot preAmpSnapshot) {
            sampleRate = Math.Max(16000, Math.Min(192000, sampleRateHz));
            dna = profile ?? VocalMixDnaProfile.CreateStudioDefault();
            macros = macroSettings ?? new VocalMacroSettings();
            hardwarePreAmp = preAmpSnapshot ?? HardwarePreAmpStateSnapshot.Empty;
            lastGainStepEpoch = hardwarePreAmp.GainStepEpoch;

            double srScale = sampleRate / 48000.0;
            for (int i = 0; i < FdnChannels; i++) {
                int len = (int)Math.Round(FdnPrimeLengthsAt48k[i] * srScale);
                if (len < 64) {
                    len = 64;
                }
                if (len >= fdnLines[i].Length) {
                    len = fdnLines[i].Length - 1;
                }
                fdnLengths[i] = len;
            }

            // Pure-physics Zero-Fake-DSP rule: strictly 0.0 dB flat neutral EQ unless calibrated Match EQ deltas are supplied.
            float matchScale = Clamp(macros.MixMatchPct / 100f, 0f, 1.5f);
            bool hasCalibratedMatchEq = matchEqDeltasDb != null && matchEqDeltasDb.Length >= 8;
            float lowDelta = hasCalibratedMatchEq && IsFinite(matchEqDeltasDb[1]) ? matchEqDeltasDb[1] : 0f;
            float midDelta = hasCalibratedMatchEq && IsFinite(matchEqDeltasDb[4]) ? matchEqDeltasDb[4] : 0f;
            float airDelta = hasCalibratedMatchEq && IsFinite(matchEqDeltasDb[5]) && IsFinite(matchEqDeltasDb[6])
                ? 0.5f * (matchEqDeltasDb[5] + matchEqDeltasDb[6])
                : 0f;
            cachedRawAirGainDb = airDelta * matchScale;
            appliedHwAirActive = IsHardwareAirActive(hardwarePreAmp);
            float effectiveAirDb = Clamp(cachedRawAirGainDb - (appliedHwAirActive ? 2.5f : 0f), -6f, 6f);

            hpfState.SetHighPass(sampleRate, 92f, 0.707f);
            lowShelfMatch.SetLowShelf(sampleRate, 220f, Clamp(lowDelta * matchScale, -6f, 6f), 0.75f);
            midBellMatch.SetPeaking(sampleRate, 2200f, Clamp(midDelta * matchScale, -6f, 6f), 1.1f);
            airShelfMatch.SetHighShelf(sampleRate, 8500f, effectiveAirDb, 0.71f);
            deEssBandpass.SetBandPass(sampleRate, 7200f, 1.6f);
            pocketCarveL.SetPeaking(sampleRate, 2400f, 0f, 1.0f);
            pocketCarveR.SetPeaking(sampleRate, 2400f, 0f, 1.0f);
        }

        /// <summary>
        /// Updates the hardware pre-ADC snapshot zero-GC on the audio thread and adapts the HF Air shelf only
        /// when Scarlett Air / MicSource compensation state changes.
        /// </summary>
        public void SetHardwarePreAmpState(HardwarePreAmpStateSnapshot preAmpSnapshot) {
            HardwarePreAmpStateSnapshot effective = preAmpSnapshot ?? HardwarePreAmpStateSnapshot.Empty;
            if (ReferenceEquals(effective, hardwarePreAmp) || effective.Equals(hardwarePreAmp)) {
                return;
            }
            hardwarePreAmp = effective;
            lastGainStepEpoch = effective.GainStepEpoch;
            bool hwAirActive = IsHardwareAirActive(effective);
            if (hwAirActive != appliedHwAirActive) {
                appliedHwAirActive = hwAirActive;
                float effectiveAirDb = Clamp(cachedRawAirGainDb - (hwAirActive ? 2.5f : 0f), -6f, 6f);
                airShelfMatch.SetHighShelf(sampleRate, 8500f, effectiveAirDb, 0.71f);
            }
        }

        /// <summary>Zeroes real-time metering telemetry cleanly when bypassed or when live mic is inactive.</summary>
        public void ClearTelemetry(bool preserveLevels = false) {
            if (!preserveLevels) {
                telemetry.InputDbfs = -120f;
                telemetry.OutputDbfs = -120f;
            }
            telemetry.GateReductionDb = 0f;
            telemetry.DeEsserReductionDb = 0f;
            telemetry.CompressorReductionDb = 0f;
            telemetry.SidechainPocketDuckDb = 0f;
        }

        /// <summary>Clears all filter memories, envelopes, and FDN reverb tails cleanly.</summary>
        public void Reset() {
            hpfState.Reset();
            lowShelfMatch.Reset();
            midBellMatch.Reset();
            airShelfMatch.Reset();
            deEssBandpass.Reset();
            pocketCarveL.Reset();
            pocketCarveR.Reset();
            gateEnv = 0f;
            gateGain = 1f;
            deEssEnv = 0f;
            fetEnv = 0f;
            optoFastEnv = 0f;
            optoSlowEnv = 0f;
            voiceActivityEnv = 0f;
            adaaPrevX = 0f;
            adaaPrevF1 = 0f;
            adaaPrevRaw = 0f;
            adaaPrevOut = 0f;
            Array.Clear(delayBufL, 0, delayBufL.Length);
            Array.Clear(delayBufR, 0, delayBufR.Length);
            delayWritePos = 0;
            for (int i = 0; i < FdnChannels; i++) {
                Array.Clear(fdnLines[i], 0, fdnLines[i].Length);
                fdnWritePos[i] = 0;
                fdnLpState[i] = 0f;
            }
            ClearTelemetry(preserveLevels: false);
        }

        /// <summary>
        /// Processes a mono/stereo live vocal buffer in place into stereo output (interleaved L/R) with zero heap allocations.
        /// Optionally carves a subtle 1-4 kHz vocal pocket into <paramref name="backingStereoInterleaved"/> when provided,
        /// and applies lock-free Scarlett / USB pre-ADC <paramref name="preAmpSnapshot"/> compensation when supplied.
        /// </summary>
        public void ProcessStereoBlockZeroGc(
            float[] vocalStereoInterleaved,
            int vocalOffset,
            int frameCount,
            float[] backingStereoInterleaved = null,
            int backingOffset = 0,
            HardwarePreAmpStateSnapshot preAmpSnapshot = null) {
            if (vocalStereoInterleaved == null || frameCount <= 0) {
                return;
            }

            if (preAmpSnapshot != null) {
                SetHardwarePreAmpState(preAmpSnapshot);
            }

            if (!macros.Enabled) {
                float disabledPeak = 1e-6f;
                for (int f = 0; f < frameCount; f++) {
                    int idx = vocalOffset + f * 2;
                    float p = Math.Max(Math.Abs(vocalStereoInterleaved[idx]), Math.Abs(vocalStereoInterleaved[idx + 1]));
                    if (p > disabledPeak) {
                        disabledPeak = p;
                    }
                }
                float db = LinearToDb(disabledPeak);
                telemetry.InputDbfs = db;
                telemetry.OutputDbfs = db;
                ClearTelemetry(preserveLevels: true);
                return;
            }

            float outputTrim = (float)Math.Pow(10.0, Clamp(macros.VocalLevelDb, -18f, 12f) / 20.0);
            if (macros.Bypass) {
                float bypassInPeak = 1e-6f;
                float bypassOutPeak = 1e-6f;
                for (int f = 0; f < frameCount; f++) {
                    int idx = vocalOffset + f * 2;
                    float inL = vocalStereoInterleaved[idx];
                    float inR = vocalStereoInterleaved[idx + 1];
                    float pIn = Math.Max(Math.Abs(inL), Math.Abs(inR));
                    if (pIn > bypassInPeak) {
                        bypassInPeak = pIn;
                    }
                    float outL = Clamp(inL * outputTrim, -0.99f, 0.99f);
                    float outR = Clamp(inR * outputTrim, -0.99f, 0.99f);
                    vocalStereoInterleaved[idx] = outL;
                    vocalStereoInterleaved[idx + 1] = outR;
                    float pOut = Math.Max(Math.Abs(outL), Math.Abs(outR));
                    if (pOut > bypassOutPeak) {
                        bypassOutPeak = pOut;
                    }
                }
                telemetry.InputDbfs = LinearToDb(bypassInPeak);
                telemetry.OutputDbfs = LinearToDb(bypassOutPeak);
                ClearTelemetry(preserveLevels: true);
                return;
            }

            float matchScale = Clamp(macros.MixMatchPct / 100f, 0f, 1.5f);
            float dynScale = Clamp(macros.DynamicsPct / 100f, 0f, 1.5f) * matchScale;
            float revScale = Clamp(macros.ReverbPct / 100f, 0f, 1.5f) * matchScale;
            // Avoid double-saturating Scarlett hardware Presence+Drive when USB mic is active
            bool hwDriveActive = IsHardwareDriveActive(hardwarePreAmp);
            float warmthDrive = hwDriveActive
                ? 1f
                : 1f + Clamp((macros.WarmthPct / 100f) * dna.HarmonicWarmth * 3.2f * matchScale, 0f, 3.5f);

            // Ballistics coefficients
            float gateAtk = ExpCoeff(0.002f);
            float gateRel = ExpCoeff(0.090f);
            float deEssAtk = ExpCoeff(0.0015f);
            float deEssRel = ExpCoeff(0.035f);
            float fetAtk = ExpCoeff(0.0004f); // 0.4 ms 1176 fast peak catch
            float fetRel = ExpCoeff(0.060f);
            float optoAtk = ExpCoeff(0.010f); // 10 ms LA-2A T4 optical attack
            float optoFastRel = ExpCoeff(0.065f);
            float optoSlowRel = ExpCoeff(1.100f); // T4 dual-stage slow memory tail

            // Exact per-line FDN feedback gain derived from physical RT60: g_i = 10^(-3 * L_i / (RT60 * fs))
            float rt60 = Clamp(dna.ReverbRt60Sec, 0.25f, 4.5f);
            for (int i = 0; i < FdnChannels; i++) {
                fdnFeedbackPerLine[i] = Clamp((float)Math.Pow(10.0, -3.0 * (fdnLengths[i] / (double)sampleRate) / rt60), 0.15f, 0.96f);
            }
            // Exact exponential one-pole damping coefficient: 1 - exp(-2*pi*fc/fs)
            float dampCutoff = Clamp(dna.ReverbDampingHz, 500f, sampleRate * 0.45f);
            float dampAlpha = Clamp((float)(1.0 - Math.Exp(-2.0 * Math.PI * dampCutoff / sampleRate)), 0.02f, 0.95f);
            float revWet = Clamp(dna.ReverbWetMix * revScale, 0f, 0.55f);
            bool hasDelay = dna.DelayTimeMs > 1f && dna.DelayWetMix > 1e-4f && revScale > 1e-4f;
            float delayWet = hasDelay ? Clamp(dna.DelayWetMix * revScale, 0f, 0.30f) : 0f;
            int delaySamplesL = hasDelay ? ClampInt((int)Math.Round(dna.DelayTimeMs * 0.001f * sampleRate), 32, MaxDelaySamples - 2) : 32;
            int delaySamplesR = hasDelay ? ClampInt((int)Math.Round(delaySamplesL * 1.12f), 32, MaxDelaySamples - 2) : 32;

            float inPeak = 1e-6f;
            float outPeak = 1e-6f;
            float worstGateDb = 0f;
            float worstDeEssDb = 0f;
            float worstCompDb = 0f;

            for (int f = 0; f < frameCount; f++) {
                int idx = vocalOffset + f * 2;
                float x = 0.5f * (vocalStereoInterleaved[idx] + vocalStereoInterleaved[idx + 1]);
                float absIn = Math.Abs(x);
                if (absIn > inPeak) {
                    inPeak = absIn;
                }

                // 1. Noise Gate / Soft Expander (-46 dBFS threshold)
                gateEnv = absIn > gateEnv
                    ? gateAtk * gateEnv + (1f - gateAtk) * absIn
                    : gateRel * gateEnv + (1f - gateRel) * absIn;
                float targetGate = 1f;
                if (macros.EnableNoiseGate && gateEnv < 0.005f) {
                    float ratio = gateEnv / 0.005f;
                    targetGate = 0.06f + 0.94f * ratio * ratio;
                }
                gateGain = 0.985f * gateGain + 0.015f * targetGate;
                x *= gateGain;
                float gateDb = LinearToDb(gateGain);
                if (gateDb < worstGateDb) {
                    worstGateDb = gateDb;
                }

                // 2. HPF + Formant-Safe Match EQ & Air Shelf (0 dB flat unless calibrated)
                x = hpfState.Process(x);
                x = lowShelfMatch.Process(x);
                x = midBellMatch.Process(x);
                x = airShelfMatch.Process(x);

                // 3. Split-Band 5-10 kHz Adaptive De-Esser
                if (macros.EnableDeEsser) {
                    float sib = Math.Abs(deEssBandpass.Process(x));
                    deEssEnv = sib > deEssEnv
                        ? deEssAtk * deEssEnv + (1f - deEssAtk) * sib
                        : deEssRel * deEssEnv + (1f - deEssRel) * sib;
                    float sibDb = LinearToDb(deEssEnv);
                    float sibExcess = Math.Max(0f, sibDb - (-24f));
                    float deEssRedDb = -Clamp(sibExcess * 0.55f, 0f, 8.5f);
                    if (deEssRedDb < worstDeEssDb) {
                        worstDeEssDb = deEssRedDb;
                    }
                    // Dynamic attenuation via split-band subtraction (zero coefficient churn)
                    float deEssLin = (float)Math.Pow(10.0, deEssRedDb / 20.0);
                    float sibSigned = deEssBandpass.LastOutput;
                    x -= sibSigned * (1f - deEssLin);
                }

                // 4. Dual-Stage Serial Compressor: FET 1176 -> Optical LA-2A (T4 dual release)
                float compRedDb = 0f;
                if (dynScale > 0.01f) {
                    float absPostEq = Math.Abs(x);
                    fetEnv = absPostEq > fetEnv
                        ? fetAtk * fetEnv + (1f - fetAtk) * absPostEq
                        : fetRel * fetEnv + (1f - fetRel) * absPostEq;
                    float fetDb = LinearToDb(fetEnv);
                    float fetThreshold = -16f;
                    float fetOver = Math.Max(0f, fetDb - fetThreshold);
                    float fetGrDb = -fetOver * (1f - 1f / 4f) * Math.Min(1.2f, dynScale);
                    float fetLin = (float)Math.Pow(10.0, fetGrDb / 20.0);
                    x *= fetLin;

                    float absPostFet = Math.Abs(x);
                    optoFastEnv = absPostFet > optoFastEnv
                        ? optoAtk * optoFastEnv + (1f - optoAtk) * absPostFet
                        : optoFastRel * optoFastEnv + (1f - optoFastRel) * absPostFet;
                    optoSlowEnv = absPostFet > optoSlowEnv
                        ? optoAtk * optoSlowEnv + (1f - optoAtk) * absPostFet
                        : optoSlowRel * optoSlowEnv + (1f - optoSlowRel) * absPostFet;
                    float optoCombined = 0.65f * optoFastEnv + 0.35f * optoSlowEnv;
                    float optoDb = LinearToDb(optoCombined);
                    float optoOver = Math.Max(0f, optoDb - (-20f));
                    float optoGrDb = -optoOver * (1f - 1f / 2.8f) * Math.Min(1.2f, dynScale);
                    float makeupDb = Clamp(0.45f * (-fetGrDb - optoGrDb), 0f, 6f);
                    float optoLin = (float)Math.Pow(10.0, (optoGrDb + makeupDb) / 20.0);
                    x *= optoLin;

                    compRedDb = fetGrDb + optoGrDb;
                    if (compRedDb < worstCompDb) {
                        worstCompDb = compRedDb;
                    }
                }

                // 5. 4x-Oversampled 1st-Order ADAA Anti-Aliased Analog Saturation (ChowDSP tanh antiderivative + exact HF boxcar de-tilt)
                if (warmthDrive > 1.02f) {
                    x = ProcessWarmthAdaa4x(x, warmthDrive);
                } else {
                    adaaPrevRaw = x;
                    adaaPrevOut = x;
                }

                // Track voice activity for instrumental sidechain pocket glue
                float voiceAbs = Math.Abs(x);
                voiceActivityEnv = voiceAbs > voiceActivityEnv
                    ? 0.96f * voiceActivityEnv + 0.04f * voiceAbs
                    : 0.9992f * voiceActivityEnv + 0.0008f * voiceAbs;

                // 6. Matched Stereo Delay (only when physically detected in Song DNA) + 8-Channel Hadamard FDN Reverb
                float tapL = 0f;
                float tapR = 0f;
                if (hasDelay) {
                    int readL = delayWritePos - delaySamplesL;
                    if (readL < 0) {
                        readL += MaxDelaySamples;
                    }
                    int readR = delayWritePos - delaySamplesR;
                    if (readR < 0) {
                        readR += MaxDelaySamples;
                    }
                    tapL = delayBufL[readL];
                    tapR = delayBufR[readR];
                    delayBufL[delayWritePos] = x + tapR * 0.22f;
                    delayBufR[delayWritePos] = x + tapL * 0.22f;
                    delayWritePos++;
                    if (delayWritePos >= MaxDelaySamples) {
                        delayWritePos = 0;
                    }
                }

                float revL = 0f;
                float revR = 0f;
                if (revWet > 1e-4f) {
                    ProcessHadamardFdn8(x, dampAlpha, out revL, out revR);
                }

                float dryL = x + tapL * delayWet + revL * revWet;
                float dryR = x + tapR * delayWet + revR * revWet;
                float outL = Clamp(dryL * outputTrim, -0.99f, 0.99f);
                float outR = Clamp(dryR * outputTrim, -0.99f, 0.99f);

                vocalStereoInterleaved[idx] = outL;
                vocalStereoInterleaved[idx + 1] = outR;

                float absOut = Math.Max(Math.Abs(outL), Math.Abs(outR));
                if (absOut > outPeak) {
                    outPeak = absOut;
                }
            }

            // 7. Dynamic 1-4 kHz Vocal-Pocket Sidechain Glue on instrumental backing (block-rate coefficient update)
            float pocketDuckDb = 0f;
            if (macros.EnableSidechainPocketGlue && backingStereoInterleaved != null) {
                float voiceDb = LinearToDb(voiceActivityEnv);
                float voiceOver = Math.Max(0f, voiceDb - (-32f));
                pocketDuckDb = -Clamp(voiceOver * 0.14f * matchScale, 0f, 3.5f);
                pocketCarveL.SetPeaking(sampleRate, 2400f, pocketDuckDb, 1.0f);
                pocketCarveR.SetPeaking(sampleRate, 2400f, pocketDuckDb, 1.0f);
                for (int f = 0; f < frameCount; f++) {
                    int bIdx = backingOffset + f * 2;
                    backingStereoInterleaved[bIdx] = pocketCarveL.Process(backingStereoInterleaved[bIdx]);
                    backingStereoInterleaved[bIdx + 1] = pocketCarveR.Process(backingStereoInterleaved[bIdx + 1]);
                }
            }

            telemetry.InputDbfs = LinearToDb(inPeak);
            telemetry.OutputDbfs = LinearToDb(outPeak);
            telemetry.GateReductionDb = worstGateDb;
            telemetry.DeEsserReductionDb = worstDeEssDb;
            telemetry.CompressorReductionDb = worstCompDb;
            telemetry.SidechainPocketDuckDb = pocketDuckDb;
        }

        /// <summary>
        /// Evaluates 1st-order Analytically Differentiated Anti-Aliased (ADAA) <c>tanh</c> waveshaper using
        /// exact antiderivative <c>F1(x) = ln(cosh(x))</c> with numerically stable midpoint fallback.
        /// </summary>
        public float ProcessAdaaTanh(float x) {
            float f1 = LogCosh(x);
            float diff = x - adaaPrevX;
            float y;
            if (Math.Abs(diff) < 1e-5f) {
                y = (float)Math.Tanh(0.5 * (x + adaaPrevX));
            } else {
                y = (f1 - adaaPrevF1) / diff;
            }
            adaaPrevX = x;
            adaaPrevF1 = f1;
            return y;
        }

        float ProcessWarmthAdaa4x(float x, float drive) {
            float delta = x - adaaPrevRaw;
            float y = 0f;
            for (int s = 1; s <= 4; s++) {
                y = ProcessAdaaTanh((adaaPrevRaw + 0.25f * s * delta) * drive);
            }
            y /= drive;
            // Cancel the linear-regime 4x ADAA boxcar response (0.875 + 0.125*z^-1) so 10-20 kHz air stays flat
            float outSample = (y - 0.125f * adaaPrevOut) * (1f / 0.875f);
            adaaPrevRaw = x;
            adaaPrevOut = outSample;
            return outSample;
        }

        /// <summary>Overflow-safe <c>ln(cosh(x)) = |x| + ln(1 + e^(-2|x|)) - ln(2)</c>.</summary>
        public static float LogCosh(float x) {
            double ax = Math.Abs((double)x);
            return (float)(ax + Math.Log(1.0 + Math.Exp(-2.0 * ax)) - 0.6931471805599453);
        }

        void ProcessHadamardFdn8(float input, float dampAlpha, out float outL, out float outR) {
            for (int i = 0; i < FdnChannels; i++) {
                float[] line = fdnLines[i];
                int wp = fdnWritePos[i];
                int rp = wp - fdnLengths[i];
                if (rp < 0) {
                    rp += line.Length;
                }
                float delayed = line[rp];
                // One-pole lowpass damping inside feedback loop
                fdnLpState[i] += dampAlpha * (delayed - fdnLpState[i]);
                fdnTempA[i] = fdnLpState[i];
            }

            // Orthogonal 8x8 Fast Walsh-Hadamard Transform (norm factor 1/sqrt(8) = 0.35355339)
            const float norm = 0.35355339f;
            for (int i = 0; i < 8; i += 2) {
                fdnTempB[i] = fdnTempA[i] + fdnTempA[i + 1];
                fdnTempB[i + 1] = fdnTempA[i] - fdnTempA[i + 1];
            }
            for (int i = 0; i < 8; i += 4) {
                fdnTempA[i] = fdnTempB[i] + fdnTempB[i + 2];
                fdnTempA[i + 1] = fdnTempB[i + 1] + fdnTempB[i + 3];
                fdnTempA[i + 2] = fdnTempB[i] - fdnTempB[i + 2];
                fdnTempA[i + 3] = fdnTempB[i + 1] - fdnTempB[i + 3];
            }
            for (int i = 0; i < 4; i++) {
                float a = fdnTempA[i];
                float b = fdnTempA[i + 4];
                fdnTempB[i] = (a + b) * norm;
                fdnTempB[i + 4] = (a - b) * norm;
            }

            float sumL = 0f;
            float sumR = 0f;
            for (int i = 0; i < FdnChannels; i++) {
                float[] line = fdnLines[i];
                float inject = ((i & 1) == 0 ? input : -input) * 0.35f;
                line[fdnWritePos[i]] = inject + fdnTempB[i] * fdnFeedbackPerLine[i];
                fdnWritePos[i]++;
                if (fdnWritePos[i] >= line.Length) {
                    fdnWritePos[i] = 0;
                }
                if ((i & 1) == 0) {
                    sumL += fdnTempB[i];
                } else {
                    sumR += fdnTempB[i];
                }
            }
            outL = sumL * 0.35f;
            outR = sumR * 0.35f;
        }

        static bool IsHardwareAirActive(HardwarePreAmpStateSnapshot preAmp) {
            return preAmp != null &&
                   preAmp.IsHardwareVerified &&
                   preAmp.MicSource == HardwarePreAmpStateSnapshot.MicSourceUsbInterface &&
                   preAmp.AirMode != ScarlettAirMode.Off;
        }

        static bool IsHardwareDriveActive(HardwarePreAmpStateSnapshot preAmp) {
            return preAmp != null &&
                   preAmp.IsHardwareVerified &&
                   preAmp.MicSource == HardwarePreAmpStateSnapshot.MicSourceUsbInterface &&
                   (preAmp.DriveActive || preAmp.AirMode == ScarlettAirMode.PresenceAndDrive);
        }

        static bool IsFinite(float v) {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        float ExpCoeff(float timeSec) {
            return (float)Math.Exp(-1.0 / Math.Max(1.0, timeSec * sampleRate));
        }

        static float LinearToDb(float linear) {
            return linear > 1e-6f ? Clamp((float)(20.0 * Math.Log10(linear)), -120f, 6f) : -120f;
        }

        static float Clamp(float v, float min, float max) {
            return v < min ? min : (v > max ? max : v);
        }

        static int ClampInt(int v, int min, int max) {
            return v < min ? min : (v > max ? max : v);
        }

        struct BiquadState {
            float b0, b1, b2, a1, a2;
            float z1, z2;
            public float LastOutput;

            public void Reset() {
                z1 = 0f;
                z2 = 0f;
                LastOutput = 0f;
            }

            public float Process(float x) {
                float y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                LastOutput = y;
                return y;
            }

            public void SetHighPass(int fs, float fc, float q) {
                double w0 = 2.0 * Math.PI * Clamp(fc, 20f, fs * 0.45f) / fs;
                double c = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double a0 = 1.0 + alpha;
                b0 = (float)(((1.0 + c) * 0.5) / a0);
                b1 = (float)(-(1.0 + c) / a0);
                b2 = (float)(((1.0 + c) * 0.5) / a0);
                a1 = (float)((-2.0 * c) / a0);
                a2 = (float)((1.0 - alpha) / a0);
            }

            public void SetBandPass(int fs, float fc, float q) {
                double w0 = 2.0 * Math.PI * Clamp(fc, 20f, fs * 0.45f) / fs;
                double c = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double a0 = 1.0 + alpha;
                b0 = (float)(alpha / a0);
                b1 = 0f;
                b2 = (float)(-alpha / a0);
                a1 = (float)((-2.0 * c) / a0);
                a2 = (float)((1.0 - alpha) / a0);
            }

            public void SetPeaking(int fs, float fc, float gainDb, float q) {
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * Clamp(fc, 20f, fs * 0.45f) / fs;
                double c = Math.Cos(w0);
                double alpha = Math.Sin(w0) / (2.0 * q);
                double a0 = 1.0 + alpha / a;
                b0 = (float)((1.0 + alpha * a) / a0);
                b1 = (float)((-2.0 * c) / a0);
                b2 = (float)((1.0 - alpha * a) / a0);
                a1 = (float)((-2.0 * c) / a0);
                a2 = (float)((1.0 - alpha / a) / a0);
            }

            public void SetLowShelf(int fs, float fc, float gainDb, float slope) {
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * Clamp(fc, 20f, fs * 0.45f) / fs;
                double c = Math.Cos(w0);
                double alpha = Math.Sin(w0) * 0.5 * Math.Sqrt((a + 1.0 / a) * (1.0 / slope - 1.0) + 2.0);
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                double a0 = (a + 1.0) + (a - 1.0) * c + twoSqrtAAlpha;
                b0 = (float)((a * ((a + 1.0) - (a - 1.0) * c + twoSqrtAAlpha)) / a0);
                b1 = (float)((2.0 * a * ((a - 1.0) - (a + 1.0) * c)) / a0);
                b2 = (float)((a * ((a + 1.0) - (a - 1.0) * c - twoSqrtAAlpha)) / a0);
                a1 = (float)((-2.0 * ((a - 1.0) + (a + 1.0) * c)) / a0);
                a2 = (float)(((a + 1.0) + (a - 1.0) * c - twoSqrtAAlpha) / a0);
            }

            public void SetHighShelf(int fs, float fc, float gainDb, float slope) {
                double a = Math.Pow(10.0, gainDb / 40.0);
                double w0 = 2.0 * Math.PI * Clamp(fc, 20f, fs * 0.45f) / fs;
                double c = Math.Cos(w0);
                double alpha = Math.Sin(w0) * 0.5 * Math.Sqrt((a + 1.0 / a) * (1.0 / slope - 1.0) + 2.0);
                double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
                double a0 = (a + 1.0) - (a - 1.0) * c + twoSqrtAAlpha;
                b0 = (float)((a * ((a + 1.0) + (a - 1.0) * c + twoSqrtAAlpha)) / a0);
                b1 = (float)((-2.0 * a * ((a - 1.0) + (a + 1.0) * c)) / a0);
                b2 = (float)((a * ((a + 1.0) + (a - 1.0) * c - twoSqrtAAlpha)) / a0);
                a1 = (float)((2.0 * ((a - 1.0) - (a + 1.0) * c)) / a0);
                a2 = (float)(((a + 1.0) - (a - 1.0) * c - twoSqrtAAlpha) / a0);
            }
        }
    }
}
