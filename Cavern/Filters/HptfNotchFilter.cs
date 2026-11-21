using System;

using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Individual Headphone-to-Ear-Canal Transfer Function (HpTF) half-wave standing-wave resonance notch filter
    /// targeting the occluded ear-canal peak in <c>6.0–10.0 kHz</c>.
    /// Uses zero-GC double-precision Orfanidis (1997) Nyquist-decramped peaking attenuation for real-time playback,
    /// provides a mathematically exact zero-phase symmetric FIR kernel builder, and includes a safe (-18 dBFS)
    /// pure-sine / log-sweep tone generator for rapid auditory ear-canal resonance discovery.
    /// </summary>
    public sealed class HptfNotchFilter {
        /// <summary>Minimum supported ear-canal resonance notch center frequency in Hz.</summary>
        public const float MinFrequencyHz = 6000f;

        /// <summary>Maximum supported ear-canal resonance notch center frequency in Hz.</summary>
        public const float MaxFrequencyHz = 10000f;

        /// <summary>Default ear-canal resonance center frequency in Hz (typical adult occluded half-wave peak).</summary>
        public const float DefaultFrequencyHz = 8000f;

        /// <summary>Maximum notch attenuation depth in dB (negative value).</summary>
        public const float MaxCutDb = -12.0f;

        /// <summary>Default moderate ear-canal notch attenuation in dB.</summary>
        public const float DefaultGainDb = -4.0f;

        /// <summary>Minimum Q factor for high-selectivity ear-canal resonance notches.</summary>
        public const float MinQ = 3.0f;

        /// <summary>Maximum Q factor for narrow ear-canal resonance notches.</summary>
        public const float MaxQ = 16.0f;

        /// <summary>Default Q factor matching typical IEM/closed-back ear-canal resonance bandwidth.</summary>
        public const float DefaultQ = 8.0f;

        readonly ParametricEqualizer eq;
        readonly object configLock = new object();

        bool enabled;
        float frequencyHz = DefaultFrequencyHz;
        float gainDb = DefaultGainDb;
        float qFactor = DefaultQ;
        int sampleRate = Listener.DefaultSampleRate;
        int channels = 2;

        /// <summary>
        /// Creates an HpTF ear-canal resonance notch filter (disabled by default for zero-regression neutrality).
        /// </summary>
        public HptfNotchFilter(int sampleRate = Listener.DefaultSampleRate, int channels = 2) {
            this.sampleRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
            this.channels = channels > 0 ? channels : 2;
            eq = new ParametricEqualizer(this.sampleRate, this.channels);
            eq.AutoPreampHeadroom = false; // Strictly attenuative notch (gain <= 0 dB) needs zero preamp loss
            eq.UseNyquistDeCramping = true;
        }

        /// <summary>True when the notch filter is enabled and attenuating (&lt; -0.05 dB).</summary>
        public bool IsActive => enabled && gainDb < -0.05f;

        /// <summary>Configured center frequency in Hz (6000..10000 Hz).</summary>
        public float FrequencyHz => frequencyHz;

        /// <summary>Configured notch attenuation in dB (-12..0 dB).</summary>
        public float GainDb => gainDb;

        /// <summary>Configured quality factor Q (3..16).</summary>
        public float Q => qFactor;

        /// <summary>Active sample rate in Hz.</summary>
        public int SampleRate => sampleRate;

        /// <summary>
        /// Atomically updates all HpTF ear-canal notch parameters without glitches.
        /// </summary>
        public void SetParameters(bool enabled, float frequencyHz, float gainDb, float qFactor) {
            lock (configLock) {
                this.enabled = enabled;
                this.frequencyHz = Math.Clamp(frequencyHz, MinFrequencyHz, MaxFrequencyHz);
                this.gainDb = Math.Clamp(gainDb, MaxCutDb, 0f);
                this.qFactor = Math.Clamp(qFactor, MinQ, MaxQ);
                RebuildLocked();
            }
        }

        /// <summary>
        /// Updates sample rate and channel count cleanly.
        /// </summary>
        public void Configure(int newSampleRate, int newChannels) {
            int sr = newSampleRate >= 8000 ? newSampleRate : Listener.DefaultSampleRate;
            int ch = newChannels > 0 ? newChannels : 2;
            lock (configLock) {
                sampleRate = sr;
                channels = ch;
                eq.Configure(sr, ch);
                RebuildLocked();
            }
        }

        /// <summary>
        /// Processes interleaved float samples in place with zero heap allocations.
        /// Bit-identical pass-through when disabled or at 0 dB depth.
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
            IsActive ? eq.EvaluateMagnitudeDb(freqHz, false) : 0.0;

        /// <summary>
        /// Resets internal filter delay state cleanly on seek or track switch.
        /// </summary>
        public void Reset() => eq.ResetState();

        /// <summary>
        /// Builds a mathematically symmetric zero-phase FIR kernel (linear-phase shifted by <c>(taps - 1) / 2</c>)
        /// matching the exact analog second-order peaking notch magnitude response at <paramref name="centerFreqHz"/>.
        /// Useful for offline HRIR baking or phase-neutral acoustic validation.
        /// </summary>
        public static float[] BuildZeroPhaseFirKernel(
            double centerFreqHz,
            double gainDb,
            double q,
            int sampleRate,
            int tapCount = 513) {
            int oddTaps = Math.Max(65, tapCount | 1);
            int half = oddTaps >> 1;
            int fftSize = 1 << QMath.Log2Ceil(Math.Max(1024, oddTaps * 4));
            int halfBins = fftSize >> 1;

            double fc = Math.Max(MinFrequencyHz, Math.Min(Math.Min(MaxFrequencyHz, sampleRate * 0.45), centerFreqHz));
            double cutDb = Math.Max(MaxCutDb, Math.Min(0.0, gainDb));
            double qClamped = Math.Max(MinQ, Math.Min(MaxQ, q));
            double gPeak = Math.Pow(10.0, cutDb / 20.0);

            // Real symmetric frequency magnitude -> exact zero-phase impulse centered at index 0, circularly shifted to half
            Complex[] spec = new Complex[fftSize];
            for (int k = 0; k <= halfBins; k++) {
                double f = (double)k * sampleRate / fftSize;
                double mag = EvaluateAnalogNotchMagnitude(f, fc, gPeak, qClamped);
                spec[k] = new Complex((float)mag, 0f);
                if (k > 0 && k < halfBins) {
                    spec[fftSize - k] = new Complex((float)mag, 0f);
                }
            }

            FFTCache cache = new ThreadSafeFFTCache(fftSize);
            spec.InPlaceIFFT(cache);

            // Extract symmetric windowed kernel centered at index 'half' (strictly h[half - m] == h[half + m])
            float[] kernel = new float[oddTaps];
            for (int i = 0; i < oddTaps; i++) {
                int rel = i - half;
                int srcIdx = rel < 0 ? fftSize + rel : rel;
                // Hann window preserves exact symmetry and tight main-lobe resolution for high-Q (Q 3..16) ear-canal notches
                double frac = (double)i / (oddTaps - 1);
                double win = 0.50 - 0.50 * Math.Cos(2.0 * Math.PI * frac);
                kernel[i] = (float)(spec[srcIdx].Real * win);
            }
            return kernel;
        }

        /// <summary>
        /// Evaluates the exact continuous-time second-order peaking notch magnitude response at frequency <paramref name="freqHz"/>.
        /// </summary>
        public static double EvaluateAnalogNotchMagnitude(double freqHz, double centerFreqHz, double linearGainAtCenter, double q) {
            if (freqHz <= 0.0 || centerFreqHz <= 0.0 || Math.Abs(linearGainAtCenter - 1.0) < 1e-6) {
                return 1.0;
            }
            double r = freqHz / centerFreqHz;
            double oneMinusR2 = 1.0 - r * r;
            double rOverQ = r / Math.Max(0.1, q);
            double num = oneMinusR2 * oneMinusR2 + (linearGainAtCenter * rOverQ) * (linearGainAtCenter * rOverQ);
            double den = oneMinusR2 * oneMinusR2 + rOverQ * rOverQ;
            return Math.Sqrt(num / Math.Max(1e-24, den));
        }

        /// <summary>
        /// Generates a safe, fade-windowed (-18 dBFS) stereo calibration sine tone at <paramref name="freqHz"/>
        /// so listeners can pinpoint their individual 6–10 kHz ear-canal standing-wave peak without discomfort.
        /// </summary>
        public static float[] GenerateCalibrationToneStereo(
            double freqHz,
            int sampleRate,
            double durationSeconds = 1.8,
            double amplitudeLinear = 0.12589254) { // -18 dBFS
            int frames = Math.Max(256, (int)Math.Round(sampleRate * Math.Max(0.2, Math.Min(6.0, durationSeconds))));
            int fadeFrames = Math.Min(frames / 6, Math.Max(64, sampleRate / 40)); // ~25 ms raised-cosine fade
            float[] stereo = new float[frames * 2];
            double phaseStep = 2.0 * Math.PI * Math.Max(200.0, Math.Min(sampleRate * 0.45, freqHz)) / sampleRate;

            for (int n = 0; n < frames; n++) {
                double env = 1.0;
                if (n < fadeFrames) {
                    env = 0.5 - 0.5 * Math.Cos(Math.PI * n / fadeFrames);
                } else if (n >= frames - fadeFrames) {
                    env = 0.5 - 0.5 * Math.Cos(Math.PI * (frames - 1 - n) / fadeFrames);
                }
                float sample = (float)(Math.Sin(phaseStep * n) * amplitudeLinear * env);
                stereo[n * 2] = sample;
                stereo[n * 2 + 1] = sample;
            }
            return stereo;
        }

        /// <summary>
        /// Generates a slow logarithmic sine sweep across <paramref name="startFreqHz"/>..<paramref name="endFreqHz"/>
        /// at a safe -18 dBFS level to identify individual HpTF ear-canal peaks by ear.
        /// </summary>
        public static float[] GenerateCalibrationSweepStereo(
            double startFreqHz,
            double endFreqHz,
            int sampleRate,
            double durationSeconds = 6.0,
            double amplitudeLinear = 0.12589254) {
            int frames = Math.Max(512, (int)Math.Round(sampleRate * Math.Max(0.5, Math.Min(15.0, durationSeconds))));
            int fadeFrames = Math.Min(frames / 10, Math.Max(64, sampleRate / 25));
            double f0 = Math.Max(1000.0, Math.Min(sampleRate * 0.42, startFreqHz));
            double f1 = Math.Max(f0 + 100.0, Math.Min(sampleRate * 0.45, endFreqHz));
            double logRatio = Math.Log(f1 / f0);
            double totalTime = frames / (double)sampleRate;
            double k = 2.0 * Math.PI * f0 * totalTime / logRatio;

            float[] stereo = new float[frames * 2];
            for (int n = 0; n < frames; n++) {
                double t = n / (double)sampleRate;
                double phase = k * (Math.Exp(logRatio * (t / totalTime)) - 1.0);
                double env = 1.0;
                if (n < fadeFrames) {
                    env = 0.5 - 0.5 * Math.Cos(Math.PI * n / fadeFrames);
                } else if (n >= frames - fadeFrames) {
                    env = 0.5 - 0.5 * Math.Cos(Math.PI * (frames - 1 - n) / fadeFrames);
                }
                float sample = (float)(Math.Sin(phase) * amplitudeLinear * env);
                stereo[n * 2] = sample;
                stereo[n * 2 + 1] = sample;
            }
            return stereo;
        }

        void RebuildLocked() {
            if (!enabled || gainDb >= -0.05f) {
                eq.Clear();
                return;
            }
            ParametricBand[] bands = new ParametricBand[] {
                new ParametricBand(ParametricFilterType.Peaking, frequencyHz, gainDb, qFactor, true)
            };
            eq.SetBands(bands, 0.0, true);
        }
    }
}
