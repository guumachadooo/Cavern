using System.Collections.Concurrent;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// High-performance cached O(N log N) mixed-radix FFT/IFFT (Stockham auto-sort across factors 2, 3, 4, 5, and fallback primes)
/// with precomputed twiddle tables, zero per-frame heap allocations, and multithreaded PyTorch-compatible center-reflect
/// periodic-Hann STFT / squared-window overlap-add iSTFT.
/// Required for non-power-of-2 UVR MDX-Net frame sizes (5120, 6144, 7680) as well as 2048/4096/8192 (SCNet/RoFormer).
/// </summary>
public static class MixedRadixFft {
    sealed class StagePlan {
        public readonly int Radix;
        public readonly int S;
        public readonly int M;
        public readonly double[] TwReD;
        public readonly double[] TwImD;
        public readonly float[] TwReF;
        public readonly float[] TwImF;
        public readonly double[] RadCosD;
        public readonly double[] RadSinD;
        public readonly float[] RadCosF;
        public readonly float[] RadSinF;

        public StagePlan(int radix, int s, int m, double sign) {
            Radix = radix;
            S = s;
            M = m;
            int rs = radix * s;
            double baseAngle = sign * (2.0 * Math.PI / rs);

            if (radix == 2) {
                TwReD = new double[s];
                TwImD = new double[s];
                TwReF = new float[s];
                TwImF = new float[s];
                for (int q = 0; q < s; q++) {
                    double theta = baseAngle * q;
                    double c = Math.Cos(theta);
                    double sn = Math.Sin(theta);
                    TwReD[q] = c;
                    TwImD[q] = sn;
                    TwReF[q] = (float)c;
                    TwImF[q] = (float)sn;
                }
            } else if (radix == 4) {
                // Packed [w1, w2, w3] per q for contiguous L1-cache loads
                TwReD = new double[s * 3];
                TwImD = new double[s * 3];
                TwReF = new float[s * 3];
                TwImF = new float[s * 3];
                for (int q = 0; q < s; q++) {
                    double t1 = baseAngle * q;
                    double w1Re = Math.Cos(t1);
                    double w1Im = Math.Sin(t1);
                    double w2Re = w1Re * w1Re - w1Im * w1Im;
                    double w2Im = 2.0 * w1Re * w1Im;
                    double w3Re = w2Re * w1Re - w2Im * w1Im;
                    double w3Im = w2Re * w1Im + w2Im * w1Re;
                    int b = q * 3;
                    TwReD[b] = w1Re;
                    TwImD[b] = w1Im;
                    TwReD[b + 1] = w2Re;
                    TwImD[b + 1] = w2Im;
                    TwReD[b + 2] = w3Re;
                    TwImD[b + 2] = w3Im;

                    TwReF[b] = (float)w1Re;
                    TwImF[b] = (float)w1Im;
                    TwReF[b + 1] = (float)w2Re;
                    TwImF[b + 1] = (float)w2Im;
                    TwReF[b + 2] = (float)w3Re;
                    TwImF[b + 2] = (float)w3Im;
                }
            } else {
                TwReD = new double[s * radix];
                TwImD = new double[s * radix];
                TwReF = new float[s * radix];
                TwImF = new float[s * radix];
                for (int q = 0; q < s; q++) {
                    int row = q * radix;
                    for (int p = 0; p < radix; p++) {
                        double a = baseAngle * q * p;
                        double c = Math.Cos(a);
                        double sn = Math.Sin(a);
                        TwReD[row + p] = c;
                        TwImD[row + p] = sn;
                        TwReF[row + p] = (float)c;
                        TwImF[row + p] = (float)sn;
                    }
                }
                double radixAngle = sign * (2.0 * Math.PI / radix);
                RadCosD = new double[radix * radix];
                RadSinD = new double[radix * radix];
                RadCosF = new float[radix * radix];
                RadSinF = new float[radix * radix];
                for (int pp = 0; pp < radix; pp++) {
                    for (int p = 0; p < radix; p++) {
                        double a = radixAngle * pp * p;
                        double c = Math.Cos(a);
                        double sn = Math.Sin(a);
                        int idx = pp * radix + p;
                        RadCosD[idx] = c;
                        RadSinD[idx] = sn;
                        RadCosF[idx] = (float)c;
                        RadSinF[idx] = (float)sn;
                    }
                }
            }
        }
    }

    sealed class MixedRadixPlan {
        public readonly int N;
        public readonly bool Inverse;
        public readonly double SignD;
        public readonly float SignF;
        public readonly double InvND;
        public readonly float InvNF;
        public readonly StagePlan[] Stages;
        public readonly int MaxGeneralRadix;

        public MixedRadixPlan(int n, bool inverse) {
            N = n;
            Inverse = inverse;
            SignD = inverse ? 1.0 : -1.0;
            SignF = inverse ? 1f : -1f;
            InvND = 1.0 / n;
            InvNF = (float)(1.0 / n);

            List<StagePlan> list = [];
            int s = 1;
            int remaining = n;
            int maxGen = 0;
            while (remaining > 1) {
                int r = ChooseRadix(remaining);
                int m = remaining / r;
                list.Add(new StagePlan(r, s, m, SignD));
                if (r != 2 && r != 4 && r > maxGen) {
                    maxGen = r;
                }
                s *= r;
                remaining = m;
            }
            Stages = list.ToArray();
            MaxGeneralRadix = maxGen;
        }
    }

    static readonly ConcurrentDictionary<(int N, bool Inverse), MixedRadixPlan> PlanCache = new();
    static readonly ConcurrentDictionary<int, float[]> HannWindowFloatCache = new();

    static MixedRadixPlan GetPlan(int n, bool inverse) =>
        PlanCache.GetOrAdd((n, inverse), static key => new MixedRadixPlan(key.N, key.Inverse));

    static float[] GetPeriodicHannWindowFloat(int nFft) =>
        HannWindowFloatCache.GetOrAdd(nFft, static n => {
            float[] w = new float[n];
            double factor = 2.0 * Math.PI / n;
            for (int i = 0; i < n; i++) {
                w[i] = (float)(0.5 * (1.0 - Math.Cos(factor * i)));
            }
            return w;
        });

    /// <summary>Computes an in-place forward (<paramref name="inverse"/> = false) or normalized inverse DFT of length <paramref name="n"/>.</summary>
    public static void Transform(double[] real, double[] imag, int n, bool inverse) {
        if (n <= 1) {
            return;
        }
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imag);
        if (real.Length < n || imag.Length < n) {
            throw new ArgumentException("FFT arrays must be at least length n.");
        }

        MixedRadixPlan plan = GetPlan(n, inverse);
        double[] bufRe = new double[n];
        double[] bufIm = new double[n];
        double[] tmpRe = plan.MaxGeneralRadix > 0 ? new double[plan.MaxGeneralRadix] : null;
        double[] tmpIm = plan.MaxGeneralRadix > 0 ? new double[plan.MaxGeneralRadix] : null;
        ExecutePlanDouble(real, imag, bufRe, bufIm, tmpRe, tmpIm, plan);
    }

    static void ExecutePlanDouble(
        double[] real,
        double[] imag,
        double[] bufRe,
        double[] bufIm,
        double[] tmpRe,
        double[] tmpIm,
        MixedRadixPlan plan) {
        int n = plan.N;
        double[] srcRe = real;
        double[] srcIm = imag;
        double[] dstRe = bufRe;
        double[] dstIm = bufIm;
        double jSign = plan.SignD;

        for (int st = 0; st < plan.Stages.Length; st++) {
            StagePlan stage = plan.Stages[st];
            int r = stage.Radix;
            int s = stage.S;
            int m = stage.M;
            int sm = s * m;

            if (r == 2) {
                double[] twRe = stage.TwReD;
                double[] twIm = stage.TwImD;
                for (int q = 0; q < s; q++) {
                    double wRe = twRe[q];
                    double wIm = twIm[q];
                    int i0 = q;
                    int o0 = q;
                    int stepO = 2 * s;
                    for (int k = 0; k < m; k++, i0 += s, o0 += stepO) {
                        int i1 = i0 + sm;
                        double x0Re = srcRe[i0];
                        double x0Im = srcIm[i0];
                        double a1Re = srcRe[i1];
                        double a1Im = srcIm[i1];
                        double x1Re = a1Re * wRe - a1Im * wIm;
                        double x1Im = a1Re * wIm + a1Im * wRe;

                        dstRe[o0] = x0Re + x1Re;
                        dstIm[o0] = x0Im + x1Im;
                        dstRe[o0 + s] = x0Re - x1Re;
                        dstIm[o0 + s] = x0Im - x1Im;
                    }
                }
            } else if (r == 4) {
                double[] twRe = stage.TwReD;
                double[] twIm = stage.TwImD;
                int sm2 = sm + sm;
                int sm3 = sm2 + sm;
                int s2 = s + s;
                int s3 = s2 + s;
                int stepO = 4 * s;
                for (int q = 0; q < s; q++) {
                    int tb = q * 3;
                    double w1Re = twRe[tb];
                    double w1Im = twIm[tb];
                    double w2Re = twRe[tb + 1];
                    double w2Im = twIm[tb + 1];
                    double w3Re = twRe[tb + 2];
                    double w3Im = twIm[tb + 2];

                    int i0 = q;
                    int oBase = q;
                    for (int k = 0; k < m; k++, i0 += s, oBase += stepO) {
                        int i1 = i0 + sm;
                        int i2 = i0 + sm2;
                        int i3 = i0 + sm3;

                        double x0Re = srcRe[i0];
                        double x0Im = srcIm[i0];
                        double a1Re = srcRe[i1];
                        double a1Im = srcIm[i1];
                        double x1Re = a1Re * w1Re - a1Im * w1Im;
                        double x1Im = a1Re * w1Im + a1Im * w1Re;
                        double a2Re = srcRe[i2];
                        double a2Im = srcIm[i2];
                        double x2Re = a2Re * w2Re - a2Im * w2Im;
                        double x2Im = a2Re * w2Im + a2Im * w2Re;
                        double a3Re = srcRe[i3];
                        double a3Im = srcIm[i3];
                        double x3Re = a3Re * w3Re - a3Im * w3Im;
                        double x3Im = a3Re * w3Im + a3Im * w3Re;

                        double t0Re = x0Re + x2Re;
                        double t0Im = x0Im + x2Im;
                        double t1Re = x0Re - x2Re;
                        double t1Im = x0Im - x2Im;
                        double t2Re = x1Re + x3Re;
                        double t2Im = x1Im + x3Im;
                        double t3Re = x1Re - x3Re;
                        double t3Im = x1Im - x3Im;

                        double jt3Re = -jSign * t3Im;
                        double jt3Im = jSign * t3Re;

                        dstRe[oBase] = t0Re + t2Re;
                        dstIm[oBase] = t0Im + t2Im;
                        dstRe[oBase + s] = t1Re + jt3Re;
                        dstIm[oBase + s] = t1Im + jt3Im;
                        dstRe[oBase + s2] = t0Re - t2Re;
                        dstIm[oBase + s2] = t0Im - t2Im;
                        dstRe[oBase + s3] = t1Re - jt3Re;
                        dstIm[oBase + s3] = t1Im - jt3Im;
                    }
                }
            } else {
                double[] twRe = stage.TwReD;
                double[] twIm = stage.TwImD;
                double[] radCos = stage.RadCosD;
                double[] radSin = stage.RadSinD;
                int stepO = r * s;
                for (int q = 0; q < s; q++) {
                    int twRow = q * r;
                    int iBase = q;
                    int oBase = q;
                    for (int k = 0; k < m; k++, iBase += s, oBase += stepO) {
                        int idx = iBase;
                        for (int p = 0; p < r; p++, idx += sm) {
                            double sr = srcRe[idx];
                            double si = srcIm[idx];
                            double wr = twRe[twRow + p];
                            double wi = twIm[twRow + p];
                            tmpRe[p] = sr * wr - si * wi;
                            tmpIm[p] = sr * wi + si * wr;
                        }
                        int outIdx = oBase;
                        for (int pp = 0; pp < r; pp++, outIdx += s) {
                            double sumRe = tmpRe[0];
                            double sumIm = tmpIm[0];
                            int row = pp * r;
                            for (int p = 1; p < r; p++) {
                                double c = radCos[row + p];
                                double sn = radSin[row + p];
                                sumRe += tmpRe[p] * c - tmpIm[p] * sn;
                                sumIm += tmpRe[p] * sn + tmpIm[p] * c;
                            }
                            dstRe[outIdx] = sumRe;
                            dstIm[outIdx] = sumIm;
                        }
                    }
                }
            }

            (srcRe, dstRe) = (dstRe, srcRe);
            (srcIm, dstIm) = (dstIm, srcIm);
        }

        if (!ReferenceEquals(srcRe, real)) {
            Array.Copy(srcRe, real, n);
            Array.Copy(srcIm, imag, n);
        }

        if (plan.Inverse) {
            double invN = plan.InvND;
            for (int i = 0; i < n; i++) {
                real[i] *= invN;
                imag[i] *= invN;
            }
        }
    }

    static void ExecutePlanFloat(
        float[] real,
        float[] imag,
        float[] bufRe,
        float[] bufIm,
        float[] tmpRe,
        float[] tmpIm,
        MixedRadixPlan plan) {
        int n = plan.N;
        float[] srcRe = real;
        float[] srcIm = imag;
        float[] dstRe = bufRe;
        float[] dstIm = bufIm;
        float jSign = plan.SignF;

        for (int st = 0; st < plan.Stages.Length; st++) {
            StagePlan stage = plan.Stages[st];
            int r = stage.Radix;
            int s = stage.S;
            int m = stage.M;
            int sm = s * m;

            if (r == 2) {
                float[] twRe = stage.TwReF;
                float[] twIm = stage.TwImF;
                int stepO = 2 * s;
                for (int q = 0; q < s; q++) {
                    float wRe = twRe[q];
                    float wIm = twIm[q];
                    int i0 = q;
                    int o0 = q;
                    for (int k = 0; k < m; k++, i0 += s, o0 += stepO) {
                        int i1 = i0 + sm;
                        float x0Re = srcRe[i0];
                        float x0Im = srcIm[i0];
                        float a1Re = srcRe[i1];
                        float a1Im = srcIm[i1];
                        float x1Re = a1Re * wRe - a1Im * wIm;
                        float x1Im = a1Re * wIm + a1Im * wRe;

                        dstRe[o0] = x0Re + x1Re;
                        dstIm[o0] = x0Im + x1Im;
                        dstRe[o0 + s] = x0Re - x1Re;
                        dstIm[o0 + s] = x0Im - x1Im;
                    }
                }
            } else if (r == 4) {
                float[] twRe = stage.TwReF;
                float[] twIm = stage.TwImF;
                int sm2 = sm + sm;
                int sm3 = sm2 + sm;
                int s2 = s + s;
                int s3 = s2 + s;
                int stepO = 4 * s;
                for (int q = 0; q < s; q++) {
                    int tb = q * 3;
                    float w1Re = twRe[tb];
                    float w1Im = twIm[tb];
                    float w2Re = twRe[tb + 1];
                    float w2Im = twIm[tb + 1];
                    float w3Re = twRe[tb + 2];
                    float w3Im = twIm[tb + 2];

                    int i0 = q;
                    int oBase = q;
                    for (int k = 0; k < m; k++, i0 += s, oBase += stepO) {
                        int i1 = i0 + sm;
                        int i2 = i0 + sm2;
                        int i3 = i0 + sm3;

                        float x0Re = srcRe[i0];
                        float x0Im = srcIm[i0];
                        float a1Re = srcRe[i1];
                        float a1Im = srcIm[i1];
                        float x1Re = a1Re * w1Re - a1Im * w1Im;
                        float x1Im = a1Re * w1Im + a1Im * w1Re;
                        float a2Re = srcRe[i2];
                        float a2Im = srcIm[i2];
                        float x2Re = a2Re * w2Re - a2Im * w2Im;
                        float x2Im = a2Re * w2Im + a2Im * w2Re;
                        float a3Re = srcRe[i3];
                        float a3Im = srcIm[i3];
                        float x3Re = a3Re * w3Re - a3Im * w3Im;
                        float x3Im = a3Re * w3Im + a3Im * w3Re;

                        float t0Re = x0Re + x2Re;
                        float t0Im = x0Im + x2Im;
                        float t1Re = x0Re - x2Re;
                        float t1Im = x0Im - x2Im;
                        float t2Re = x1Re + x3Re;
                        float t2Im = x1Im + x3Im;
                        float t3Re = x1Re - x3Re;
                        float t3Im = x1Im - x3Im;

                        float jt3Re = -jSign * t3Im;
                        float jt3Im = jSign * t3Re;

                        dstRe[oBase] = t0Re + t2Re;
                        dstIm[oBase] = t0Im + t2Im;
                        dstRe[oBase + s] = t1Re + jt3Re;
                        dstIm[oBase + s] = t1Im + jt3Im;
                        dstRe[oBase + s2] = t0Re - t2Re;
                        dstIm[oBase + s2] = t0Im - t2Im;
                        dstRe[oBase + s3] = t1Re - jt3Re;
                        dstIm[oBase + s3] = t1Im - jt3Im;
                    }
                }
            } else if (r == 3) {
                // Fast unrolled Radix-3 butterfly (critical for 6144 = 3 * 2048 & 7680 = 3 * 5 * 512)
                float[] twRe = stage.TwReF;
                float[] twIm = stage.TwImF;
                float c3 = stage.RadCosF[4]; // cos(±2*pi/3) = -0.5
                float s3 = stage.RadSinF[4]; // sin(±2*pi/3) = ±0.8660254
                int sm2 = sm + sm;
                int s2 = s + s;
                int stepO = 3 * s;
                for (int q = 0; q < s; q++) {
                    int twRow = q * 3;
                    float w1Re = twRe[twRow + 1];
                    float w1Im = twIm[twRow + 1];
                    float w2Re = twRe[twRow + 2];
                    float w2Im = twIm[twRow + 2];
                    int i0 = q;
                    int oBase = q;
                    for (int k = 0; k < m; k++, i0 += s, oBase += stepO) {
                        int i1 = i0 + sm;
                        int i2 = i0 + sm2;
                        float x0Re = srcRe[i0];
                        float x0Im = srcIm[i0];
                        float a1Re = srcRe[i1];
                        float a1Im = srcIm[i1];
                        float x1Re = a1Re * w1Re - a1Im * w1Im;
                        float x1Im = a1Re * w1Im + a1Im * w1Re;
                        float a2Re = srcRe[i2];
                        float a2Im = srcIm[i2];
                        float x2Re = a2Re * w2Re - a2Im * w2Im;
                        float x2Im = a2Re * w2Im + a2Im * w2Re;

                        float sum12Re = x1Re + x2Re;
                        float sum12Im = x1Im + x2Im;
                        float dif12Re = x1Re - x2Re;
                        float dif12Im = x1Im - x2Im;

                        float mRe = x0Re + c3 * sum12Re;
                        float mIm = x0Im + c3 * sum12Im;
                        float nRe = -s3 * dif12Im;
                        float nIm = s3 * dif12Re;

                        dstRe[oBase] = x0Re + sum12Re;
                        dstIm[oBase] = x0Im + sum12Im;
                        dstRe[oBase + s] = mRe + nRe;
                        dstIm[oBase + s] = mIm + nIm;
                        dstRe[oBase + s2] = mRe - nRe;
                        dstIm[oBase + s2] = mIm - nIm;
                    }
                }
            } else {
                float[] twRe = stage.TwReF;
                float[] twIm = stage.TwImF;
                float[] radCos = stage.RadCosF;
                float[] radSin = stage.RadSinF;
                int stepO = r * s;
                for (int q = 0; q < s; q++) {
                    int twRow = q * r;
                    int iBase = q;
                    int oBase = q;
                    for (int k = 0; k < m; k++, iBase += s, oBase += stepO) {
                        int idx = iBase;
                        for (int p = 0; p < r; p++, idx += sm) {
                            float sr = srcRe[idx];
                            float si = srcIm[idx];
                            float wr = twRe[twRow + p];
                            float wi = twIm[twRow + p];
                            tmpRe[p] = sr * wr - si * wi;
                            tmpIm[p] = sr * wi + si * wr;
                        }
                        int outIdx = oBase;
                        for (int pp = 0; pp < r; pp++, outIdx += s) {
                            float sumRe = tmpRe[0];
                            float sumIm = tmpIm[0];
                            int row = pp * r;
                            for (int p = 1; p < r; p++) {
                                float c = radCos[row + p];
                                float sn = radSin[row + p];
                                sumRe += tmpRe[p] * c - tmpIm[p] * sn;
                                sumIm += tmpRe[p] * sn + tmpIm[p] * c;
                            }
                            dstRe[outIdx] = sumRe;
                            dstIm[outIdx] = sumIm;
                        }
                    }
                }
            }

            (srcRe, dstRe) = (dstRe, srcRe);
            (srcIm, dstIm) = (dstIm, srcIm);
        }

        if (!ReferenceEquals(srcRe, real)) {
            Array.Copy(srcRe, real, n);
            Array.Copy(srcIm, imag, n);
        }

        if (plan.Inverse) {
            float invN = plan.InvNF;
            for (int i = 0; i < n; i++) {
                real[i] *= invN;
                imag[i] *= invN;
            }
        }
    }

    sealed class FloatFftScratch {
        public readonly float[] Re;
        public readonly float[] Im;
        public readonly float[] BufRe;
        public readonly float[] BufIm;
        public readonly float[] TmpRe;
        public readonly float[] TmpIm;

        public FloatFftScratch(MixedRadixPlan plan) {
            Re = new float[plan.N];
            Im = new float[plan.N];
            BufRe = new float[plan.N];
            BufIm = new float[plan.N];
            TmpRe = plan.MaxGeneralRadix > 0 ? new float[plan.MaxGeneralRadix] : null;
            TmpIm = plan.MaxGeneralRadix > 0 ? new float[plan.MaxGeneralRadix] : null;
        }
    }

    /// <summary>
    /// Computes center-reflect periodic-Hann STFT matching <c>torch.stft(..., center=True, pad_mode='reflect')</c>.
    /// Outputs planar <c>[dimF, frames]</c> real and imaginary buffers using cached plans and parallel frame execution.
    /// </summary>
    public static int ComputeComplexStft(
        float[] signal,
        int nFft,
        int hop,
        int dimF,
        out float[] outRe,
        out float[] outIm) {
        ArgumentNullException.ThrowIfNull(signal);
        int sigLen = signal.Length;
        int pad = nFft / 2;
        int paddedLen = sigLen + 2 * pad;
        int frames = 1 + (paddedLen - nFft) / hop;
        int bins = Math.Min(dimF, nFft / 2 + 1);

        float[] reArr = new float[bins * frames];
        float[] imArr = new float[bins * frames];
        outRe = reArr;
        outIm = imArr;

        MixedRadixPlan plan = GetPlan(nFft, inverse: false);
        float[] window = GetPeriodicHannWindowFloat(nFft);

        void ProcessRange(int fromFrame, int toFrame, FloatFftScratch scratch) {
            float[] fftRe = scratch.Re;
            float[] fftIm = scratch.Im;
            for (int f = fromFrame; f < toFrame; f++) {
                int start = f * hop;
                int rawStart = start - pad;
                if (rawStart >= 0 && rawStart + nFft <= sigLen) {
                    // Fast interior path without reflection branches
                    for (int n = 0; n < nFft; n++) {
                        fftRe[n] = signal[rawStart + n] * window[n];
                        fftIm[n] = 0f;
                    }
                } else {
                    for (int n = 0; n < nFft; n++) {
                        int srcIdx = ReflectIndex(rawStart + n, sigLen);
                        fftRe[n] = (sigLen > 0 ? signal[srcIdx] : 0f) * window[n];
                        fftIm[n] = 0f;
                    }
                }

                ExecutePlanFloat(fftRe, fftIm, scratch.BufRe, scratch.BufIm, scratch.TmpRe, scratch.TmpIm, plan);

                for (int b = 0; b < bins; b++) {
                    int dst = b * frames + f;
                    reArr[dst] = fftRe[b];
                    imArr[dst] = fftIm[b];
                }
            }
        }

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        if (workers > 1 && frames >= 32) {
            int chunk = (frames + workers - 1) / workers;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w => {
                int from = w * chunk;
                int to = Math.Min(frames, from + chunk);
                if (from < to) {
                    ProcessRange(from, to, new FloatFftScratch(plan));
                }
            });
        } else {
            ProcessRange(0, frames, new FloatFftScratch(plan));
        }

        return frames;
    }

    /// <summary>
    /// Computes center-trimmed squared-window OLA inverse STFT matching <c>torch.istft(..., center=True, length=targetLen)</c>.
    /// Input buffers are planar <c>[dimF, frames]</c>. Uses cached plans and parallel frame inverse transforms.
    /// </summary>
    public static float[] ComputeComplexIstft(
        float[] specRe,
        float[] specIm,
        int frames,
        int nFft,
        int hop,
        int dimF,
        int targetLen) {
        ArgumentNullException.ThrowIfNull(specRe);
        ArgumentNullException.ThrowIfNull(specIm);
        if (targetLen <= 0 || frames <= 0) {
            return Array.Empty<float>();
        }

        int halfN = nFft / 2;
        int maxValidBins = Math.Min(dimF, halfN + 1);
        int pad = halfN;
        int outTotalLen = (frames - 1) * hop + nFft;
        int interiorLimit = Math.Min(maxValidBins, halfN);
        bool hasNyquist = (nFft & 1) == 0 && halfN < maxValidBins;

        MixedRadixPlan plan = GetPlan(nFft, inverse: true);
        float[] window = GetPeriodicHannWindowFloat(nFft);

        float[] ola = new float[outTotalLen];
        float[] winSum = new float[outTotalLen];

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        if (workers > 1 && frames >= 32) {
            // Compute time-domain windowed frames in parallel into a flat buffer, then overlap-add linearly (zero lock contention)
            float[] frameTimeBuf = new float[frames * nFft];
            int chunk = (frames + workers - 1) / workers;
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w => {
                int from = w * chunk;
                int to = Math.Min(frames, from + chunk);
                if (from >= to) {
                    return;
                }
                FloatFftScratch scratch = new(plan);
                float[] fftRe = scratch.Re;
                float[] fftIm = scratch.Im;
                for (int f = from; f < to; f++) {
                    FillInverseSpectrum(specRe, specIm, frames, f, nFft, halfN, maxValidBins, interiorLimit, hasNyquist, fftRe, fftIm);
                    ExecutePlanFloat(fftRe, fftIm, scratch.BufRe, scratch.BufIm, scratch.TmpRe, scratch.TmpIm, plan);
                    int dstBase = f * nFft;
                    for (int n = 0; n < nFft; n++) {
                        frameTimeBuf[dstBase + n] = fftRe[n] * window[n];
                    }
                }
            });

            for (int f = 0; f < frames; f++) {
                int start = f * hop;
                int srcBase = f * nFft;
                for (int n = 0; n < nFft; n++) {
                    float w = window[n];
                    ola[start + n] += frameTimeBuf[srcBase + n];
                    winSum[start + n] += w * w;
                }
            }
        } else {
            FloatFftScratch scratch = new(plan);
            float[] fftRe = scratch.Re;
            float[] fftIm = scratch.Im;
            for (int f = 0; f < frames; f++) {
                FillInverseSpectrum(specRe, specIm, frames, f, nFft, halfN, maxValidBins, interiorLimit, hasNyquist, fftRe, fftIm);
                ExecutePlanFloat(fftRe, fftIm, scratch.BufRe, scratch.BufIm, scratch.TmpRe, scratch.TmpIm, plan);
                int start = f * hop;
                for (int n = 0; n < nFft; n++) {
                    float w = window[n];
                    ola[start + n] += fftRe[n] * w;
                    winSum[start + n] += w * w;
                }
            }
        }

        float[] output = new float[targetLen];
        int usable = Math.Min(targetLen, Math.Max(0, outTotalLen - pad));
        for (int i = 0; i < usable; i++) {
            int src = pad + i;
            float ws = winSum[src];
            output[i] = ws > 1e-8f ? (ola[src] / ws) : 0f;
        }
        return output;
    }

    static void FillInverseSpectrum(
        float[] specRe,
        float[] specIm,
        int frames,
        int f,
        int nFft,
        int halfN,
        int maxValidBins,
        int interiorLimit,
        bool hasNyquist,
        float[] fftRe,
        float[] fftIm) {
        Array.Clear(fftRe, 0, nFft);
        Array.Clear(fftIm, 0, nFft);

        if (maxValidBins > 0) {
            fftRe[0] = specRe[f];
            fftIm[0] = 0f;
        }

        for (int k = 1; k < interiorLimit; k++) {
            int idx = k * frames + f;
            float r = specRe[idx];
            float im = specIm[idx];
            fftRe[k] = r;
            fftIm[k] = im;
            fftRe[nFft - k] = r;
            fftIm[nFft - k] = -im;
        }

        if (hasNyquist) {
            int nyqIdx = halfN * frames + f;
            fftRe[halfN] = specRe[nyqIdx];
            fftIm[halfN] = 0f;
        }
    }

    static int ChooseRadix(int n) {
        if ((n & 3) == 0) return 4;
        if (n % 5 == 0) return 5;
        if (n % 3 == 0) return 3;
        if ((n & 1) == 0) return 2;
        for (int f = 7; f * f <= n; f += 2) {
            if (n % f == 0) {
                return f;
            }
        }
        return n;
    }

    static int ReflectIndex(int idx, int length) {
        if (length <= 1) {
            return 0;
        }
        while (idx < 0 || idx >= length) {
            if (idx < 0) {
                idx = -idx;
            }
            if (idx >= length) {
                idx = 2 * length - 2 - idx;
            }
        }
        return idx;
    }
}
