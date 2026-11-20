using System;

using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Transparent per-sample channel-linked soft-knee true-peak limiter and optional smooth normalizer.
    /// When <see cref="limiterOnly"/> is true, audio below -1 dBFS passes bit-transparently at unity gain (1.0x),
    /// while peaks above -1 dBFS are softly limited below -0.1 dBFS without block-boundary steps or pumping.
    /// </summary>
    public partial class Normalizer : Filter {
        /// <inheritdoc/>
        public override bool LinearTimeInvariant => false;

        /// <summary>
        /// Legacy gain increment per frame (retained for API compatibility; release smoothing uses <see cref="SampleRate"/>).
        /// </summary>
        public float decayFactor;

        /// <summary>
        /// Sample rate in Hz used to compute block-size-independent per-sample release smoothing.
        /// </summary>
        public int SampleRate { get; set; } = Listener.DefaultSampleRate;

        /// <summary>
        /// Interleaved channel count linked together during peak detection so stereo/spatial imaging never shifts.
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// Don't go over 0 dB gain. If true, acts strictly as a transparent clipping protector.
        /// </summary>
        public bool limiterOnly;

        /// <summary>
        /// Soft-knee onset threshold (-1.0 dBFS). Signals at or below this level pass at exact 1.0x in limiter-only mode.
        /// </summary>
        const float kneeStart = 0.891250938f;

        /// <summary>
        /// True-peak safety ceiling (-0.1 dBFS).
        /// </summary>
        const float ceiling = 0.988553095f;

        /// <summary>
        /// Soft-knee transition range (<see cref="ceiling"/> - <see cref="kneeStart"/>).
        /// </summary>
        const float kneeWidth = ceiling - kneeStart;

        /// <summary>
        /// Maximum upward gain (+12 dB) allowed when <see cref="limiterOnly"/> is false.
        /// </summary>
        const float maxNormalizerGain = 3.9810717f;

        /// <summary>
        /// Noise gate threshold (-60 dBFS) below which upward normalization holds steady instead of boosting silence.
        /// </summary>
        const float noiseGate = 0.001f;

        /// <summary>
        /// Current smoothed gain multiplier.
        /// </summary>
        float lastGain = 1f;

        /// <summary>
        /// Create a normalizer or transparent soft-knee limiter.
        /// </summary>
        public Normalizer(bool limiterOnly) => this.limiterOnly = limiterOnly;

        /// <inheritdoc/>
        public override void Process(float[] samples) {
            if (samples == null || samples.Length == 0) {
                return;
            }
            int channels = Channels > 0 ? Channels : 1;
            int sr = SampleRate >= 8000 ? SampleRate : Listener.DefaultSampleRate;

            // Smooth per-sample exponential release independent of caller block size (100 ms for limiter, 450 ms for AGC)
            float releaseTimeSec = limiterOnly ? 0.10f : 0.45f;
            float releaseAlpha = 1f - MathF.Exp(-1f / (releaseTimeSec * sr));

            for (int frame = 0; frame < samples.Length; frame += channels) {
                int end = Math.Min(frame + channels, samples.Length);
                float peak = 0f;
                for (int c = frame; c < end; c++) {
                    float abs = MathF.Abs(samples[c]);
                    if (abs > peak) {
                        peak = abs;
                    }
                }

                float targetGain;
                if (limiterOnly) {
                    if (peak <= kneeStart) {
                        targetGain = 1f;
                    } else {
                        float over = peak - kneeStart;
                        float compressed = kneeStart + kneeWidth * (over / (over + kneeWidth));
                        targetGain = compressed / peak;
                    }
                } else {
                    if (peak > noiseGate) {
                        float desired = MathF.Min(maxNormalizerGain, kneeStart / peak);
                        float projected = peak * desired;
                        if (projected > kneeStart) {
                            float over = projected - kneeStart;
                            float compressed = kneeStart + kneeWidth * (over / (over + kneeWidth));
                            desired = compressed / peak;
                        }
                        targetGain = desired;
                    } else {
                        targetGain = lastGain;
                    }
                }

                // Instantaneous attack when gain must drop (zero clipping), smooth exponential release when recovering
                if (targetGain < lastGain) {
                    lastGain = targetGain;
                } else {
                    lastGain += (targetGain - lastGain) * releaseAlpha;
                    if (limiterOnly && lastGain > 0.99999f) {
                        lastGain = 1f;
                    }
                }

                if (lastGain != 1f) {
                    for (int c = frame; c < end; c++) {
                        samples[c] *= lastGain;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override object Clone() => new Normalizer(limiterOnly) {
            SampleRate = SampleRate,
            Channels = Channels,
            decayFactor = decayFactor
        };
    }
}
