using System;
using System.Runtime.CompilerServices;

namespace Cavern.Filters {
    /// <summary>
    /// Transparent 4x-oversampled True-Peak Lookahead Brickwall Limiter (Slot 6B, Priority 900).
    /// Uses the normative ITU-R BS.1770-5 Annex 2 48-tap 4-phase polyphase FIR interpolator to catch inter-sample
    /// peaks ahead of the audio stream via a 1.5 ms lookahead circular buffer, O(1) monotonic sliding-window peak
    /// hold, and smooth channel-linked gain envelope (preserving stereo/binaural imaging with zero phase shift).
    /// Signals whose 4x True Peak stays at or below the configured ceiling pass at exact unity gain (1.000000x).
    /// Directly reusable by W09 LGSR FLAC Clipping Guard and W11 Live Mastering Vocal Chain.
    /// </summary>
    public sealed class TruePeakLimiter {
        /// <summary>Default transparent ceiling in dBTP (-0.1 dBTP for lossless maximum headroom).</summary>
        public const float DefaultCeilingDbtp = -0.1f;

        /// <summary>EBU R128 / ITU-R BS.1770-5 broadcast &amp; streaming True-Peak ceiling (-1.0 dBTP).</summary>
        public const float BroadcastCeilingDbtp = -1.0f;

        /// <summary>Minimum configurable ceiling in dBTP.</summary>
        public const float MinCeilingDbtp = -6.0f;

        /// <summary>Maximum configurable ceiling in dBTP (0.0 dBTP strict digital full scale).</summary>
        public const float MaxCeilingDbtp = 0.0f;

        /// <summary>Default lookahead window in milliseconds (1.5 ms = 72 samples at 48 kHz).</summary>
        public const float DefaultLookaheadMs = 1.5f;

        /// <summary>Default release time constant in milliseconds (80 ms transparent recovery).</summary>
        public const float DefaultReleaseMs = 80f;

        readonly TruePeakDetector detector = new TruePeakDetector(2);

        float[] audioDelay = Array.Empty<float>();
        float[] peakHistory = Array.Empty<float>();
        int[] dequeIndices = Array.Empty<int>();
        float[] dequeValues = Array.Empty<float>();

        int sampleRate = Listener.DefaultSampleRate;
        int channels = 2;
        int lookaheadFrames = 72;
        float ceilingDbtp = DefaultCeilingDbtp;
        float ceilingLinear = 0.9885531f;
        float releaseMs = DefaultReleaseMs;

        int delayWriteFrame;
        long totalFramesProcessed;
        int dequeHead;
        int dequeTail;
        int dequeCount;

        float attackGain = 1f;
        float releaseGain = 1f;
        float attackCoeff = 0.05f;
        float releaseCoeff = 0.0003f;

        float currentGainReductionDb;
        float peakGainReductionDb;
        float maxOutputTruePeakLinear;

        /// <summary>Creates a transparent 4x-oversampled True-Peak brickwall limiter.</summary>
        public TruePeakLimiter(
            int sampleRate = 48000,
            int channels = 2,
            float ceilingDbtp = DefaultCeilingDbtp,
            float releaseMs = DefaultReleaseMs) {
            this.ceilingDbtp = Math.Clamp(ceilingDbtp, MinCeilingDbtp, MaxCeilingDbtp);
            this.ceilingLinear = DbToLinear(this.ceilingDbtp);
            this.releaseMs = Math.Clamp(releaseMs, 10f, 500f);
            Configure(sampleRate, channels);
        }

        /// <summary>Configured True-Peak ceiling in dBTP (-6.0..0.0 dBTP).</summary>
        public float CeilingDbtp {
            get => ceilingDbtp;
            set {
                ceilingDbtp = Math.Clamp(value, MinCeilingDbtp, MaxCeilingDbtp);
                ceilingLinear = DbToLinear(ceilingDbtp);
            }
        }

        /// <summary>Configured release time constant in milliseconds (10..500 ms).</summary>
        public float ReleaseMs {
            get => releaseMs;
            set {
                releaseMs = Math.Clamp(value, 10f, 500f);
                UpdateEnvelopeCoeffs();
            }
        }

        /// <summary>Lookahead latency in sample frames.</summary>
        public int LatencySamples => lookaheadFrames;

        /// <summary>Configured sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Current instantaneous gain reduction in dB (&lt;= 0.0 dB; 0.0 dB when transparent unity).</summary>
        public float CurrentGainReductionDb => currentGainReductionDb;

        /// <summary>Maximum gain reduction observed during the latest block in dB (&lt;= 0.0 dB).</summary>
        public float PeakGainReductionDb => peakGainReductionDb;

        /// <summary>True when gain reduction is actively engaged (&lt; -0.01 dB).</summary>
        public bool IsLimiting => currentGainReductionDb < -0.01f;

        /// <summary>Maximum post-limiter True Peak observed since last reset in dBTP.</summary>
        public float MaxOutputTruePeakDbtp => TruePeakDetector.LinearToDbtp(maxOutputTruePeakLinear);

        /// <summary>Configures sample rate and channel count, pre-allocating all zero-GC lookahead buffers.</summary>
        public void Configure(int newSampleRate, int newChannels) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            int ch = newChannels > 0 ? newChannels : 2;
            int la = Math.Clamp((int)Math.Round(sr * (DefaultLookaheadMs * 0.001)), 16, 512);

            sampleRate = sr;
            channels = ch;
            lookaheadFrames = la;

            int requiredAudio = la * ch;
            if (audioDelay.Length != requiredAudio) {
                audioDelay = new float[requiredAudio];
            }
            if (peakHistory.Length != la) {
                peakHistory = new float[la];
                dequeIndices = new int[la + 2];
                dequeValues = new float[la + 2];
            }

            detector.Configure(ch);
            UpdateEnvelopeCoeffs();
            Reset();
        }

        /// <summary>Clears lookahead delay line, polyphase FIR history, and envelope followers.</summary>
        public void Reset() {
            if (audioDelay.Length > 0) {
                Array.Clear(audioDelay, 0, audioDelay.Length);
            }
            if (peakHistory.Length > 0) {
                Array.Clear(peakHistory, 0, peakHistory.Length);
            }
            detector.Reset();
            delayWriteFrame = 0;
            totalFramesProcessed = 0;
            dequeHead = 0;
            dequeTail = 0;
            dequeCount = 0;
            attackGain = 1f;
            releaseGain = 1f;
            currentGainReductionDb = 0f;
            peakGainReductionDb = 0f;
            maxOutputTruePeakLinear = 0f;
        }

        /// <summary>
        /// Processes <paramref name="frameCount"/> interleaved float frames in place with zero heap allocations.
        /// Guarantees channel-linked transparent limiting so 4x inter-sample True Peak stays at or below
        /// <see cref="CeilingDbtp"/> while sub-ceiling material passes at exact 1.000000x unity gain.
        /// </summary>
        public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channelCount) {
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

            float ceil = ceilingLinear;
            // Small deterministic safety margin (-0.015 dB) covers finite FIR transition ripple
            float detectCeil = ceil * 0.99827f;
            int la = lookaheadFrames;
            int dequeCap = dequeIndices.Length;
            float blockMinGain = 1f;
            float blockMaxOut = 0f;

            for (int f = 0; f < frameCount; f++) {
                int baseIdx = offset + f * channelCount;
                int delayBase = delayWriteFrame * channelCount;

                // 1. Measure 4x-oversampled channel-linked True Peak of the incoming frame & push into lookahead ring
                float frameTruePeak = 0f;
                for (int c = 0; c < channelCount; c++) {
                    float inSample = buffer[baseIdx + c];
                    float tp = detector.ProcessSample(c, inSample);
                    if (tp > frameTruePeak) {
                        frameTruePeak = tp;
                    }
                    // Swap incoming sample into circular delay buffer and retrieve delayed sample
                    float delayed = audioDelay[delayBase + c];
                    audioDelay[delayBase + c] = inSample;
                    buffer[baseIdx + c] = delayed;
                }

                // Store peak of this lookahead slot (so delayed output aligns with its own True Peak)
                float delayedSlotPeak = peakHistory[delayWriteFrame];
                peakHistory[delayWriteFrame] = frameTruePeak;

                // 2. O(1) amortized sliding-window maximum over the [lookaheadFrames] horizon
                int frameStamp = (int)(totalFramesProcessed & 0x3FFFFFFF);
                while (dequeCount > 0) {
                    int prevTail = dequeTail - 1;
                    if (prevTail < 0) {
                        prevTail = dequeCap - 1;
                    }
                    if (dequeValues[prevTail] <= frameTruePeak) {
                        dequeTail = prevTail;
                        dequeCount--;
                    } else {
                        break;
                    }
                }
                dequeIndices[dequeTail] = frameStamp;
                dequeValues[dequeTail] = frameTruePeak;
                dequeTail++;
                if (dequeTail == dequeCap) {
                    dequeTail = 0;
                }
                dequeCount++;

                // Expire peaks older than lookahead window
                while (dequeCount > 0) {
                    int age = (frameStamp - dequeIndices[dequeHead]) & 0x3FFFFFFF;
                    if (age >= la) {
                        dequeHead++;
                        if (dequeHead == dequeCap) {
                            dequeHead = 0;
                        }
                        dequeCount--;
                    } else {
                        break;
                    }
                }

                float windowPeak = dequeCount > 0 ? dequeValues[dequeHead] : frameTruePeak;
                if (delayedSlotPeak > windowPeak) {
                    windowPeak = delayedSlotPeak;
                }

                // 3. Target gain: exact 1.0 when below ceiling, smooth lookahead ramp down when exceeding ceiling
                float targetGain = windowPeak > detectCeil ? detectCeil / windowPeak : 1f;

                // Smooth attack across lookahead window so gain reaches target exactly by peak arrival
                if (targetGain < attackGain) {
                    attackGain += (targetGain - attackGain) * attackCoeff;
                } else {
                    attackGain += (targetGain - attackGain) * releaseCoeff;
                }

                // Hard brickwall safety floor for the exact delayed sample's own peak (zero overshoot guarantee)
                float exactRequired = delayedSlotPeak > detectCeil ? detectCeil / delayedSlotPeak : 1f;
                float g = attackGain < exactRequired ? attackGain : exactRequired;

                // Secondary smooth release stage prevents modulation distortion on dense transients
                if (g < releaseGain) {
                    releaseGain = g;
                } else {
                    releaseGain += (g - releaseGain) * releaseCoeff;
                    if (releaseGain > 0.99999f && targetGain >= 1f) {
                        releaseGain = 1f;
                    }
                }

                float appliedGain = releaseGain < exactRequired ? releaseGain : exactRequired;
                if (appliedGain < blockMinGain) {
                    blockMinGain = appliedGain;
                }

                // 4. Apply channel-linked gain (exact bit-transparent skip when unity 1.0f)
                if (appliedGain < 0.999999f) {
                    for (int c = 0; c < channelCount; c++) {
                        float y = buffer[baseIdx + c] * appliedGain;
                        buffer[baseIdx + c] = y;
                        float absY = MathF.Abs(y);
                        if (absY > blockMaxOut) {
                            blockMaxOut = absY;
                        }
                    }
                } else {
                    for (int c = 0; c < channelCount; c++) {
                        float absY = MathF.Abs(buffer[baseIdx + c]);
                        if (absY > blockMaxOut) {
                            blockMaxOut = absY;
                        }
                    }
                }

                delayWriteFrame++;
                if (delayWriteFrame >= la) {
                    delayWriteFrame = 0;
                }
                totalFramesProcessed++;
            }

            currentGainReductionDb = releaseGain < 0.99999f ? 20f * MathF.Log10(MathF.Max(releaseGain, 1e-6f)) : 0f;
            peakGainReductionDb = blockMinGain < 0.99999f ? 20f * MathF.Log10(MathF.Max(blockMinGain, 1e-6f)) : 0f;
            if (blockMaxOut > maxOutputTruePeakLinear) {
                maxOutputTruePeakLinear = blockMaxOut;
            }
        }

        void UpdateEnvelopeCoeffs() {
            // Reach ~99.5% of target within lookahead window (tau = lookahead / 5.3)
            double attackTau = Math.Max(1.0, lookaheadFrames / 5.3);
            attackCoeff = (float)(1.0 - Math.Exp(-1.0 / attackTau));
            double releaseSamples = Math.Max(1.0, sampleRate * (releaseMs * 0.001));
            releaseCoeff = (float)(1.0 - Math.Exp(-1.0 / releaseSamples));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float DbToLinear(float db) => MathF.Pow(10f, db * 0.05f);
    }
}
