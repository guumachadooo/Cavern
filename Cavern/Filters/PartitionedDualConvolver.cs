using System;

using Cavern.Filters.Interfaces;
using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Performs two uniform partitioned overlap-add FFT convolutions for the cost of one.
    /// Supports both short HRIRs (128-512 taps) and long BRIRs (e.g. 0.3s / 14,400+ taps)
    /// in real time with zero extra block-buffering latency for any input buffer size.
    /// </summary>
    public class PartitionedDualConvolver : IResettableFilter {
        /// <summary>
        /// Maximum partition size in samples for long impulse responses.
        /// </summary>
        const int maxPartitionSize = 512;

        /// <summary>
        /// Minimum partition size in samples.
        /// </summary>
        const int minPartitionSize = 128;

        /// <summary>
        /// Samples processed per partition block (<see cref="fftSize"/> / 2).
        /// </summary>
        readonly int partitionSize;

        /// <summary>
        /// FFT length (<c>2 * partitionSize</c>).
        /// </summary>
        readonly int fftSize;

        /// <summary>
        /// Number of impulse partitions.
        /// </summary>
        readonly int partitionCount;

        /// <summary>
        /// Frequency-domain partitions of <c>impulse1 + i * impulse2</c>.
        /// </summary>
        readonly Complex[][] filterPartitions;

        /// <summary>
        /// Frequency-domain delay line (FDL) holding spectra of past completed input blocks.
        /// </summary>
        readonly Complex[][] fdl;

        /// <summary>
        /// Time-domain accumulator for the current partial block when callers supply blocks smaller than <see cref="partitionSize"/>.
        /// </summary>
        readonly float[] currentBlock;

        /// <summary>
        /// Scratch buffer for forward FFT and spectral accumulation.
        /// </summary>
        readonly Complex[] workSpectrum;

        /// <summary>
        /// Scratch buffer for frequency-domain multiply-accumulate and inverse FFT.
        /// </summary>
        readonly Complex[] accumSpectrum;

        /// <summary>
        /// Overlap-add output buffer of length <c>2 * partitionSize</c>.
        /// </summary>
        readonly Complex[] overlap;

        /// <summary>
        /// Thread-safe FFT cache for <see cref="fftSize"/>.
        /// </summary>
        readonly FFTCache cache;

        /// <summary>
        /// Ring-buffer write index in <see cref="fdl"/> (points to slot for the next completed block).
        /// </summary>
        int fdlHead;

        /// <summary>
        /// Number of samples accumulated in <see cref="currentBlock"/> for the active partial block (<c>0 .. partitionSize - 1</c>).
        /// </summary>
        int curFill;

        /// <summary>
        /// Constructs a partitioned dual convolver with no extra delay.
        /// </summary>
        public PartitionedDualConvolver(float[] impulse1, float[] impulse2) : this(impulse1, impulse2, 0, 0) { }

        /// <summary>
        /// Constructs a partitioned dual convolver with optional integer sample delays baked into the impulses.
        /// </summary>
        public PartitionedDualConvolver(float[] impulse1, float[] impulse2, int delay1, int delay2) {
            if (delay1 < 0) {
                delay1 = 0;
            }
            if (delay2 < 0) {
                delay2 = 0;
            }
            int len1 = impulse1.Length + delay1;
            int len2 = impulse2.Length + delay2;
            int totalLength = Math.Max(1, Math.Max(len1, len2));

            if (totalLength <= maxPartitionSize) {
                int pow2 = 1 << QMath.Log2Ceil(totalLength);
                partitionSize = Math.Max(minPartitionSize, pow2);
            } else {
                partitionSize = maxPartitionSize;
            }

            fftSize = partitionSize << 1;
            partitionCount = (totalLength + partitionSize - 1) / partitionSize;
            cache = new ThreadSafeFFTCache(fftSize);
            filterPartitions = new Complex[partitionCount][];

            for (int p = 0; p < partitionCount; p++) {
                Complex[] part = new Complex[fftSize];
                int offset = p * partitionSize;
                for (int i = 0; i < partitionSize; i++) {
                    int idx = offset + i;
                    int src1 = idx - delay1;
                    int src2 = idx - delay2;
                    float re = src1 >= 0 && src1 < impulse1.Length ? impulse1[src1] : 0f;
                    float im = src2 >= 0 && src2 < impulse2.Length ? impulse2[src2] : 0f;
                    part[i] = new Complex(re, im);
                }
                part.InPlaceFFT(cache);
                filterPartitions[p] = part;
            }

            if (partitionCount > 1) {
                fdl = new Complex[partitionCount][];
                for (int p = 0; p < partitionCount; p++) {
                    fdl[p] = new Complex[fftSize];
                }
            } else {
                fdl = Array.Empty<Complex[]>();
            }

            currentBlock = new float[partitionSize];
            workSpectrum = new Complex[fftSize];
            accumSpectrum = new Complex[fftSize];
            overlap = new Complex[fftSize];
        }

        /// <summary>
        /// Applies dual convolution on both arrays of samples.
        /// </summary>
        /// <param name="samplesInOut">Input samples; overwritten with <c>input * impulse1</c>.</param>
        /// <param name="samplesOut">Destination array filled with <c>input * impulse2</c>.</param>
        public void Process(float[] samplesInOut, float[] samplesOut) {
            int pos = 0;
            int total = samplesOut.Length;
            while (pos < total) {
                int remainingInBlock = partitionSize - curFill;
                int step = Math.Min(total - pos, remainingInBlock);
                if (curFill == 0 && step == partitionSize) {
                    ProcessFullBlock(samplesInOut, samplesOut, pos);
                } else {
                    ProcessPartialBlock(samplesInOut, samplesOut, pos, step);
                }
                pos += step;
            }
        }

        /// <inheritdoc/>
        public void Reset() {
            overlap.Clear();
            Array.Clear(currentBlock, 0, currentBlock.Length);
            for (int p = 0; p < fdl.Length; p++) {
                fdl[p].Clear();
            }
            fdlHead = 0;
            curFill = 0;
        }

        /// <summary>
        /// Fast path when processing an aligned <see cref="partitionSize"/> block: 1 forward FFT + 1 inverse IFFT.
        /// </summary>
        void ProcessFullBlock(float[] samplesInOut, float[] samplesOut, int offset) {
            Complex[] xSpec = partitionCount > 1 ? fdl[fdlHead] : workSpectrum;
            for (int i = 0; i < partitionSize; i++) {
                xSpec[i] = new Complex(samplesInOut[offset + i], 0f);
            }
            for (int i = partitionSize; i < fftSize; i++) {
                xSpec[i].Clear();
            }
            xSpec.InPlaceFFT(cache);

            Complex[] h0 = filterPartitions[0];
            for (int k = 0; k < fftSize; k++) {
                float xr = xSpec[k].Real, xi = xSpec[k].Imaginary;
                float hr = h0[k].Real, hi = h0[k].Imaginary;
                accumSpectrum[k] = new Complex(xr * hr - xi * hi, xr * hi + xi * hr);
            }

            for (int p = 1; p < partitionCount; p++) {
                int slot = fdlHead - p;
                if (slot < 0) {
                    slot += partitionCount;
                }
                Complex[] pastX = fdl[slot];
                Complex[] hp = filterPartitions[p];
                for (int k = 0; k < fftSize; k++) {
                    float xr = pastX[k].Real, xi = pastX[k].Imaginary;
                    float hr = hp[k].Real, hi = hp[k].Imaginary;
                    accumSpectrum[k].Real += xr * hr - xi * hi;
                    accumSpectrum[k].Imaginary += xr * hi + xi * hr;
                }
            }

            accumSpectrum.InPlaceIFFT(cache);

            for (int i = 0; i < partitionSize; i++) {
                samplesInOut[offset + i] = overlap[i].Real + accumSpectrum[i].Real;
                samplesOut[offset + i] = overlap[i].Imaginary + accumSpectrum[i].Imaginary;
                overlap[i] = overlap[partitionSize + i];
                overlap[i].Real += accumSpectrum[partitionSize + i].Real;
                overlap[i].Imaginary += accumSpectrum[partitionSize + i].Imaginary;
                overlap[partitionSize + i].Clear();
            }

            if (partitionCount > 1) {
                fdlHead++;
                if (fdlHead == partitionCount) {
                    fdlHead = 0;
                }
            }
        }

        /// <summary>
        /// Exact zero-latency path for sub-partition chunks (e.g. non-multiple update rates or final flush buffers).
        /// </summary>
        void ProcessPartialBlock(float[] samplesInOut, float[] samplesOut, int offset, int count) {
            if (curFill == 0 && partitionCount > 1) {
                accumSpectrum.Clear();
                for (int p = 1; p < partitionCount; p++) {
                    int slot = fdlHead - p;
                    if (slot < 0) {
                        slot += partitionCount;
                    }
                    Complex[] pastX = fdl[slot];
                    Complex[] hp = filterPartitions[p];
                    for (int k = 0; k < fftSize; k++) {
                        float xr = pastX[k].Real, xi = pastX[k].Imaginary;
                        float hr = hp[k].Real, hi = hp[k].Imaginary;
                        accumSpectrum[k].Real += xr * hr - xi * hi;
                        accumSpectrum[k].Imaginary += xr * hi + xi * hr;
                    }
                }
                accumSpectrum.InPlaceIFFT(cache);
                for (int i = 0; i < fftSize; i++) {
                    overlap[i].Real += accumSpectrum[i].Real;
                    overlap[i].Imaginary += accumSpectrum[i].Imaginary;
                }
            }

            for (int i = 0; i < count; i++) {
                float s = samplesInOut[offset + i];
                currentBlock[curFill + i] = s;
                workSpectrum[i] = new Complex(s, 0f);
            }
            for (int i = count; i < fftSize; i++) {
                workSpectrum[i].Clear();
            }

            workSpectrum.ConvolveInPlace(filterPartitions[0], cache);

            int addLen = Math.Min(fftSize - curFill, count + partitionSize);
            for (int i = 0; i < addLen; i++) {
                overlap[curFill + i].Real += workSpectrum[i].Real;
                overlap[curFill + i].Imaginary += workSpectrum[i].Imaginary;
            }
            // Note: because curFill + count <= partitionSize, curFill + count + partitionSize <= 2 * partitionSize == fftSize,
            // so all non-zero linear convolution samples (count + partitionSize - 1) fit cleanly inside overlap[0..fftSize-1].

            for (int i = 0; i < count; i++) {
                samplesInOut[offset + i] = overlap[curFill + i].Real;
                samplesOut[offset + i] = overlap[curFill + i].Imaginary;
            }

            curFill += count;
            if (curFill == partitionSize) {
                if (partitionCount > 1) {
                    Complex[] xSpec = fdl[fdlHead];
                    for (int i = 0; i < partitionSize; i++) {
                        xSpec[i] = new Complex(currentBlock[i], 0f);
                    }
                    for (int i = partitionSize; i < fftSize; i++) {
                        xSpec[i].Clear();
                    }
                    xSpec.InPlaceFFT(cache);
                    fdlHead++;
                    if (fdlHead == partitionCount) {
                        fdlHead = 0;
                    }
                }
                Array.Copy(overlap, partitionSize, overlap, 0, partitionSize);
                Array.Clear(overlap, partitionSize, partitionSize);
                curFill = 0;
            }
        }
    }
}
