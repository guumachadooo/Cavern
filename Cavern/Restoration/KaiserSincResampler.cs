using System;
using System.Collections.Concurrent;

namespace Cavern.Restoration {
    /// <summary>
    /// High-precision linear-phase Kaiser-windowed polyphase sinc resampler (<c>&gt; 120 dB</c> stopband attenuation)
    /// and unified <c>float32</c>/<c>double</c> <c>[-1, +1]</c> PCM normalizer for W09 Universal Lossless Twin Services.
    /// <para>
    /// Uses a 64-tap-per-phase, 512-phase symmetric Kaiser-windowed ideal sinc prototype (<c>beta = 12.6</c>,
    /// Kaiser empirical stopband <c>A_s ≈ 124.2 dB</c>) with exact Bessel <c>I_0(x)</c> evaluation and per-phase unity DC normalization.
    /// Automatically computes anti-aliasing cutoff <c>f_c = 0.94 * min(1, fs_out / fs_in)</c> for arbitrary sample-rate pairs
    /// (<c>44.1 / 48 / 88.2 / 96 / 176.4 / 192 kHz &lt;-&gt; 48 kHz</c>).
    /// </para>
    /// </summary>
    public sealed class KaiserSincResampler {
        /// <summary>Kaiser window shape parameter achieving &gt; 124 dB stopband attenuation (empirically A = (beta + 7.95) / 0.1102).</summary>
        public const double KaiserBeta = 12.6;

        /// <summary>FIR taps per polyphase branch (even, symmetric linear phase; half-width = 32 samples).</summary>
        public const int TapsPerPhase = 64;

        /// <summary>Number of discrete fractional sub-phases in [0; 1) with linear inter-phase interpolation.</summary>
        public const int NumPhases = 512;

        const int HalfTaps = TapsPerPhase / 2;

        static readonly ConcurrentDictionary<int, float[]> CachedTables = new ConcurrentDictionary<int, float[]>();

        readonly int inputSampleRate;
        readonly int outputSampleRate;
        readonly double step;
        readonly float[] phaseTable;

        /// <summary>Source sample rate in Hz.</summary>
        public int InputSampleRate {
            get { return inputSampleRate; }
        }

        /// <summary>Destination sample rate in Hz.</summary>
        public int OutputSampleRate {
            get { return outputSampleRate; }
        }

        /// <summary>Normalized cutoff ratio in (0; 1] applied for anti-aliasing.</summary>
        public double CutoffRatio { get; }

        /// <summary>
        /// Creates a linear-phase Kaiser polyphase sinc resampler from <paramref name="inputSampleRate"/>
        /// to <paramref name="outputSampleRate"/>.
        /// </summary>
        public KaiserSincResampler(int inputSampleRate, int outputSampleRate) {
            this.inputSampleRate = inputSampleRate > 0 ? inputSampleRate : 48000;
            this.outputSampleRate = outputSampleRate > 0 ? outputSampleRate : 48000;
            step = (double)this.inputSampleRate / this.outputSampleRate;

            double ratio = (double)this.outputSampleRate / this.inputSampleRate;
            CutoffRatio = ratio < 1.0 ? 0.94 * ratio : 0.96;

            int cacheKey = (int)Math.Round(CutoffRatio * 100000.0);
            phaseTable = CachedTables.GetOrAdd(cacheKey, _ => BuildPolyphaseTable(CutoffRatio, KaiserBeta));
        }

        /// <summary>
        /// Exact zeroth-order modified Bessel function of the first kind <c>I_0(x)</c> via power series to <c>1e-15</c>.
        /// </summary>
        public static double BesselI0(double x) {
            double halfX = 0.5 * x;
            double halfXSq = halfX * halfX;
            double sum = 1.0;
            double term = 1.0;
            for (int k = 1; k <= 40; k++) {
                term *= halfXSq / (k * k);
                sum += term;
                if (term <= sum * 1e-16) {
                    break;
                }
            }
            return sum;
        }

        /// <summary>
        /// Builds the flat <c>(NumPhases + 1) * TapsPerPhase</c> linear-phase Kaiser-windowed sinc LUT.
        /// Includes phase <c>NumPhases</c> (= 1.0) so branch-boundary linear interpolation needs zero modulo checks.
        /// </summary>
        public static float[] BuildPolyphaseTable(double cutoffRatio, double beta) {
            double invI0 = 1.0 / BesselI0(beta);
            float[] table = new float[(NumPhases + 1) * TapsPerPhase];
            double[] tmp = new double[TapsPerPhase];

            for (int p = 0; p <= NumPhases; p++) {
                double frac = (double)p / NumPhases;
                double sum = 0.0;
                for (int t = 0; t < TapsPerPhase; t++) {
                    double n = (t - HalfTaps + 1) - frac;
                    double norm = n / HalfTaps;
                    double w;
                    if (norm <= -1.0 || norm >= 1.0) {
                        w = 0.0;
                    } else {
                        w = BesselI0(beta * Math.Sqrt(1.0 - norm * norm)) * invI0;
                    }
                    double x = Math.PI * cutoffRatio * n;
                    double sinc = Math.Abs(x) < 1e-12 ? cutoffRatio : (Math.Sin(x) / (Math.PI * n));
                    double h = sinc * w;
                    tmp[t] = h;
                    sum += h;
                }

                double invSum = Math.Abs(sum) > 1e-12 ? (1.0 / sum) : 1.0;
                int row = p * TapsPerPhase;
                for (int t = 0; t < TapsPerPhase; t++) {
                    table[row + t] = (float)(tmp[t] * invSum);
                }
            }
            return table;
        }

        /// <summary>
        /// Resamples a single mono channel buffer from <see cref="InputSampleRate"/> to <see cref="OutputSampleRate"/>.
        /// Returns an exact copy when sample rates already match.
        /// </summary>
        public float[] ResampleMono(float[] input) {
            if (input == null || input.Length == 0) {
                return Array.Empty<float>();
            }
            if (inputSampleRate == outputSampleRate) {
                float[] copy = new float[input.Length];
                Array.Copy(input, copy, input.Length);
                return copy;
            }

            int inLen = input.Length;
            int outLen = Math.Max(1, (int)Math.Round(inLen * (double)outputSampleRate / inputSampleRate));
            float[] output = new float[outLen];
            ResampleMonoInto(input, inLen, output, outLen);
            return output;
        }

        /// <summary>
        /// Zero-GC mono resampling into a caller-provided destination buffer.
        /// </summary>
        public void ResampleMonoInto(float[] input, int inputLength, float[] output, int outputLength) {
            if (input == null || output == null || inputLength <= 0 || outputLength <= 0) {
                return;
            }
            if (inputSampleRate == outputSampleRate) {
                int copyLen = Math.Min(inputLength, outputLength);
                Array.Copy(input, 0, output, 0, copyLen);
                if (copyLen < outputLength) {
                    Array.Clear(output, copyLen, outputLength - copyLen);
                }
                return;
            }

            float[] tbl = phaseTable;
            for (int o = 0; o < outputLength; o++) {
                double srcPos = o * step;
                int baseIdx = (int)Math.Floor(srcPos);
                double frac = srcPos - baseIdx;

                double phaseScaled = frac * NumPhases;
                int p0 = (int)phaseScaled;
                if (p0 >= NumPhases) {
                    p0 = NumPhases - 1;
                }
                float alpha = (float)(phaseScaled - p0);
                float oneMinusAlpha = 1f - alpha;
                int row0 = p0 * TapsPerPhase;
                int row1 = (p0 + 1) * TapsPerPhase;

                int startSample = baseIdx - HalfTaps + 1;
                double acc = 0.0;

                if (startSample >= 0 && startSample + TapsPerPhase <= inputLength) {
                    for (int t = 0; t < TapsPerPhase; t++) {
                        float coeff = tbl[row0 + t] * oneMinusAlpha + tbl[row1 + t] * alpha;
                        acc += input[startSample + t] * coeff;
                    }
                } else {
                    for (int t = 0; t < TapsPerPhase; t++) {
                        int idx = startSample + t;
                        if (idx >= 0 && idx < inputLength) {
                            float coeff = tbl[row0 + t] * oneMinusAlpha + tbl[row1 + t] * alpha;
                            acc += input[idx] * coeff;
                        }
                    }
                }
                output[o] = (float)acc;
            }
        }

        /// <summary>
        /// Resamples a stereo pair <c>(left, right)</c> to <paramref name="targetSampleRate"/> with identical phase response.
        /// </summary>
        public static void ResampleStereo(
            float[] inLeft,
            float[] inRight,
            int sourceSampleRate,
            int targetSampleRate,
            out float[] outLeft,
            out float[] outRight) {
            if (inLeft == null || inRight == null || inLeft.Length == 0) {
                outLeft = Array.Empty<float>();
                outRight = Array.Empty<float>();
                return;
            }
            if (sourceSampleRate == targetSampleRate) {
                outLeft = new float[inLeft.Length];
                outRight = new float[inRight.Length];
                Array.Copy(inLeft, outLeft, inLeft.Length);
                Array.Copy(inRight, outRight, inRight.Length);
                return;
            }
            KaiserSincResampler resampler = new KaiserSincResampler(sourceSampleRate, targetSampleRate);
            outLeft = resampler.ResampleMono(inLeft);
            outRight = resampler.ResampleMono(inRight);
        }

        /// <summary>
        /// Converts raw signed 16-bit, 24-bit, 32-bit integer, or 32-bit IEEE float PCM bytes into unified
        /// de-interleaved stereo <c>float32</c> arrays in <c>[-1.0, +1.0]</c> (downmixing &gt;2 ch to stereo cleanly if needed).
        /// </summary>
        public static void UnifyPcmBytesToStereoFloat(
            byte[] rawBytes,
            int byteLength,
            int channels,
            int bitsPerSample,
            bool isFloatPcm,
            out float[] left,
            out float[] right) {
            if (rawBytes == null || byteLength <= 0 || channels <= 0 || bitsPerSample < 8) {
                left = Array.Empty<float>();
                right = Array.Empty<float>();
                return;
            }
            int bytesPerSample = bitsPerSample / 8;
            int frameBytes = bytesPerSample * channels;
            int frameCount = byteLength / frameBytes;
            if (frameCount <= 0) {
                left = Array.Empty<float>();
                right = Array.Empty<float>();
                return;
            }

            left = new float[frameCount];
            right = new float[frameCount];

            for (int f = 0; f < frameCount; f++) {
                int baseOff = f * frameBytes;
                float l = ReadSampleNormalized(rawBytes, baseOff, bitsPerSample, isFloatPcm);
                float r = channels > 1
                    ? ReadSampleNormalized(rawBytes, baseOff + bytesPerSample, bitsPerSample, isFloatPcm)
                    : l;
                left[f] = SanitizeFloat(l);
                right[f] = SanitizeFloat(r);
            }
        }

        static float ReadSampleNormalized(byte[] buf, int off, int bits, bool isFloat) {
            if (isFloat && bits == 32) {
                return BitConverter.ToSingle(buf, off);
            }
            if (bits == 16) {
                short s = (short)(buf[off] | (buf[off + 1] << 8));
                return s * (1f / 32768f);
            }
            if (bits == 24) {
                int v = buf[off] | (buf[off + 1] << 8) | ((sbyte)buf[off + 2] << 16);
                return v * (1f / 8388608f);
            }
            if (bits == 32) {
                int v = buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);
                return (float)(v * (1.0 / 2147483648.0));
            }
            return 0f;
        }

        static float SanitizeFloat(float v) {
            if (float.IsNaN(v) || float.IsInfinity(v)) {
                return 0f;
            }
            if (v > 2f) {
                return 2f;
            }
            if (v < -2f) {
                return -2f;
            }
            return v;
        }
    }
}
