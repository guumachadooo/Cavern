namespace Cavernize.Logic.Spatial;

/// <summary>
/// Exact O(N log N) mixed-radix FFT/IFFT (Stockham auto-sort across factors 2, 3, 4, 5, and fallback primes)
/// plus PyTorch-compatible center-reflect periodic-Hann STFT and squared-window overlap-add iSTFT.
/// Required for non-power-of-2 UVR MDX-Net frame sizes (5120, 6144, 7680) as well as 2048/4096/8192.
/// </summary>
public static class MixedRadixFft {
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

        double[] bufRe = new double[n];
        double[] bufIm = new double[n];
        double[] srcRe = real;
        double[] srcIm = imag;
        double[] dstRe = bufRe;
        double[] dstIm = bufIm;

        double sign = inverse ? 1.0 : -1.0;
        int s = 1;
        int remaining = n;

        while (remaining > 1) {
            int r = ChooseRadix(remaining);
            int m = remaining / r;
            int rs = r * s;
            double baseAngle = sign * (2.0 * Math.PI / rs);
            double radixAngle = sign * (2.0 * Math.PI / r);

            if (r == 2) {
                for (int q = 0; q < s; q++) {
                    double theta = baseAngle * q;
                    double wRe = Math.Cos(theta);
                    double wIm = Math.Sin(theta);
                    for (int k = 0; k < m; k++) {
                        int i0 = q + s * k;
                        int i1 = i0 + s * m;
                        double x0Re = srcRe[i0];
                        double x0Im = srcIm[i0];
                        double a1Re = srcRe[i1];
                        double a1Im = srcIm[i1];
                        double x1Re = a1Re * wRe - a1Im * wIm;
                        double x1Im = a1Re * wIm + a1Im * wRe;

                        int o0 = q + s * (2 * k);
                        int o1 = o0 + s;
                        dstRe[o0] = x0Re + x1Re;
                        dstIm[o0] = x0Im + x1Im;
                        dstRe[o1] = x0Re - x1Re;
                        dstIm[o1] = x0Im - x1Im;
                    }
                }
            } else if (r == 4) {
                double jSign = sign; // -1 for forward (-j), +1 for inverse (+j)
                for (int q = 0; q < s; q++) {
                    double t1 = baseAngle * q;
                    double w1Re = Math.Cos(t1);
                    double w1Im = Math.Sin(t1);
                    double w2Re = w1Re * w1Re - w1Im * w1Im;
                    double w2Im = 2.0 * w1Re * w1Im;
                    double w3Re = w2Re * w1Re - w2Im * w1Im;
                    double w3Im = w2Re * w1Im + w2Im * w1Re;

                    for (int k = 0; k < m; k++) {
                        int i0 = q + s * k;
                        int i1 = i0 + s * m;
                        int i2 = i1 + s * m;
                        int i3 = i2 + s * m;

                        double x0Re = srcRe[i0];
                        double x0Im = srcIm[i0];
                        double x1Re = srcRe[i1] * w1Re - srcIm[i1] * w1Im;
                        double x1Im = srcRe[i1] * w1Im + srcIm[i1] * w1Re;
                        double x2Re = srcRe[i2] * w2Re - srcIm[i2] * w2Im;
                        double x2Im = srcRe[i2] * w2Im + srcIm[i2] * w2Re;
                        double x3Re = srcRe[i3] * w3Re - srcIm[i3] * w3Im;
                        double x3Im = srcRe[i3] * w3Im + srcIm[i3] * w3Re;

                        double t0Re = x0Re + x2Re;
                        double t0Im = x0Im + x2Im;
                        double t1Re = x0Re - x2Re;
                        double t1Im = x0Im - x2Im;
                        double t2Re = x1Re + x3Re;
                        double t2Im = x1Im + x3Im;
                        double t3Re = x1Re - x3Re;
                        double t3Im = x1Im - x3Im;

                        // jSign * j * t3 = (-jSign * t3Im, +jSign * t3Re)
                        double jt3Re = -jSign * t3Im;
                        double jt3Im = jSign * t3Re;

                        int oBase = q + s * (4 * k);
                        dstRe[oBase] = t0Re + t2Re;
                        dstIm[oBase] = t0Im + t2Im;
                        dstRe[oBase + s] = t1Re + jt3Re;
                        dstIm[oBase + s] = t1Im + jt3Im;
                        dstRe[oBase + 2 * s] = t0Re - t2Re;
                        dstIm[oBase + 2 * s] = t0Im - t2Im;
                        dstRe[oBase + 3 * s] = t1Re - jt3Re;
                        dstIm[oBase + 3 * s] = t1Im - jt3Im;
                    }
                }
            } else {
                // General radix r (3, 5, or prime fallback)
                double[] twRe = new double[r];
                double[] twIm = new double[r];
                double[] radCos = new double[r * r];
                double[] radSin = new double[r * r];
                for (int pp = 0; pp < r; pp++) {
                    for (int p = 0; p < r; p++) {
                        double a = radixAngle * pp * p;
                        radCos[pp * r + p] = Math.Cos(a);
                        radSin[pp * r + p] = Math.Sin(a);
                    }
                }

                double[] tmpRe = new double[r];
                double[] tmpIm = new double[r];
                for (int q = 0; q < s; q++) {
                    for (int p = 0; p < r; p++) {
                        double a = baseAngle * q * p;
                        twRe[p] = Math.Cos(a);
                        twIm[p] = Math.Sin(a);
                    }
                    for (int k = 0; k < m; k++) {
                        for (int p = 0; p < r; p++) {
                            int idx = q + s * (k + m * p);
                            double sr = srcRe[idx];
                            double si = srcIm[idx];
                            tmpRe[p] = sr * twRe[p] - si * twIm[p];
                            tmpIm[p] = sr * twIm[p] + si * twRe[p];
                        }
                        int oBase = q + s * (r * k);
                        for (int pp = 0; pp < r; pp++) {
                            double sumRe = tmpRe[0];
                            double sumIm = tmpIm[0];
                            int row = pp * r;
                            for (int p = 1; p < r; p++) {
                                double c = radCos[row + p];
                                double sn = radSin[row + p];
                                sumRe += tmpRe[p] * c - tmpIm[p] * sn;
                                sumIm += tmpRe[p] * sn + tmpIm[p] * c;
                            }
                            dstRe[oBase + s * pp] = sumRe;
                            dstIm[oBase + s * pp] = sumIm;
                        }
                    }
                }
            }

            (srcRe, dstRe) = (dstRe, srcRe);
            (srcIm, dstIm) = (dstIm, srcIm);
            s *= r;
            remaining = m;
        }

        if (!ReferenceEquals(srcRe, real)) {
            Array.Copy(srcRe, real, n);
            Array.Copy(srcIm, imag, n);
        }

        if (inverse) {
            double invN = 1.0 / n;
            for (int i = 0; i < n; i++) {
                real[i] *= invN;
                imag[i] *= invN;
            }
        }
    }

    /// <summary>
    /// Computes center-reflect periodic-Hann STFT matching <c>torch.stft(..., center=True, pad_mode='reflect')</c>.
    /// Outputs planar <c>[dimF, frames]</c> real and imaginary buffers.
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

        outRe = new float[bins * frames];
        outIm = new float[bins * frames];

        double[] window = CreatePeriodicHannWindow(nFft);
        double[] fftRe = new double[nFft];
        double[] fftIm = new double[nFft];

        for (int f = 0; f < frames; f++) {
            int start = f * hop;
            for (int n = 0; n < nFft; n++) {
                int srcIdx = ReflectIndex(start + n - pad, sigLen);
                fftRe[n] = (sigLen > 0 ? signal[srcIdx] : 0.0) * window[n];
                fftIm[n] = 0.0;
            }

            Transform(fftRe, fftIm, nFft, inverse: false);

            for (int b = 0; b < bins; b++) {
                int dst = b * frames + f;
                outRe[dst] = (float)fftRe[b];
                outIm[dst] = (float)fftIm[b];
            }
        }

        return frames;
    }

    /// <summary>
    /// Computes center-trimmed squared-window OLA inverse STFT matching <c>torch.istft(..., center=True, length=targetLen)</c>.
    /// Input buffers are planar <c>[dimF, frames]</c>.
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
        double[] ola = new double[outTotalLen];
        double[] winSum = new double[outTotalLen];
        double[] window = CreatePeriodicHannWindow(nFft);
        double[] fftRe = new double[nFft];
        double[] fftIm = new double[nFft];

        for (int f = 0; f < frames; f++) {
            Array.Clear(fftRe, 0, nFft);
            Array.Clear(fftIm, 0, nFft);

            if (maxValidBins > 0) {
                fftRe[0] = specRe[f];
                fftIm[0] = 0.0;
            }

            int interiorLimit = Math.Min(maxValidBins, halfN);
            for (int k = 1; k < interiorLimit; k++) {
                int idx = k * frames + f;
                double r = specRe[idx];
                double im = specIm[idx];
                fftRe[k] = r;
                fftIm[k] = im;
                fftRe[nFft - k] = r;
                fftIm[nFft - k] = -im;
            }

            if ((nFft & 1) == 0 && halfN < maxValidBins) {
                int nyqIdx = halfN * frames + f;
                fftRe[halfN] = specRe[nyqIdx];
                fftIm[halfN] = 0.0;
            }

            Transform(fftRe, fftIm, nFft, inverse: true);

            int start = f * hop;
            for (int n = 0; n < nFft; n++) {
                double w = window[n];
                ola[start + n] += fftRe[n] * w;
                winSum[start + n] += w * w;
            }
        }

        float[] output = new float[targetLen];
        int usable = Math.Min(targetLen, Math.Max(0, outTotalLen - pad));
        for (int i = 0; i < usable; i++) {
            int src = pad + i;
            double ws = winSum[src];
            output[i] = ws > 1e-8 ? (float)(ola[src] / ws) : 0f;
        }
        return output;
    }

    /// <summary>Creates a periodic Hann window matching <c>torch.hann_window(nFft, periodic=True)</c>.</summary>
    static double[] CreatePeriodicHannWindow(int nFft) {
        double[] w = new double[nFft];
        double factor = 2.0 * Math.PI / nFft;
        for (int n = 0; n < nFft; n++) {
            w[n] = 0.5 * (1.0 - Math.Cos(factor * n));
        }
        return w;
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
