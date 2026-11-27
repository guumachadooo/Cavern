using System;
using System.Runtime.CompilerServices;

using Cavern.Filters;
using Cavern.Filters.Interfaces;
using Cavern.Utilities;

namespace Cavern.Format.Decoders.EnhancedAC3 {
    /// <summary>
    /// Zero-block-latency 4-stage <c>z^-2</c> IIR Hilbert allpass phase-difference branch (Olli Niemitalo 4+4 design).
    /// Paired reference and quadrature branches maintain exact unity magnitude and a
    /// 90-degree phase difference (<c>&lt;= 0.70 deg</c> error across 60 Hz..23.9 kHz at 48 kHz) with zero runtime allocations.
    /// </summary>
    sealed class HilbertAllpassBranch : Filter, IResettableFilter {
        const double DenormalGuard = 1e-200;

        static readonly double[] BranchASquaredCoeffs = {
            0.6923878 * 0.6923878,
            0.9360654322959 * 0.9360654322959,
            0.9882295226860 * 0.9882295226860,
            0.9987488452737 * 0.9987488452737
        };

        static readonly double[] BranchBSquaredCoeffs = {
            0.4021921162426 * 0.4021921162426,
            0.8561710882420 * 0.8561710882420,
            0.9722909545651 * 0.9722909545651,
            0.9952884791278 * 0.9952884791278
        };

        readonly double[] coeffs;
        readonly bool applyUnitDelay;
        readonly double[] x1 = new double[4];
        readonly double[] x2 = new double[4];
        readonly double[] y1 = new double[4];
        readonly double[] y2 = new double[4];
        double delayState;

        /// <summary>
        /// Creates a Hilbert allpass network branch.
        /// </summary>
        /// <param name="quadratureBranch">False for 0-deg reference branch A (with 1-sample phase alignment),
        /// true for +90-deg quadrature branch B</param>
        public HilbertAllpassBranch(bool quadratureBranch) {
            applyUnitDelay = !quadratureBranch;
            coeffs = quadratureBranch ? BranchBSquaredCoeffs : BranchASquaredCoeffs;
        }

        /// <inheritdoc/>
        public void Reset() {
            Array.Clear(x1, 0, x1.Length);
            Array.Clear(x2, 0, x2.Length);
            Array.Clear(y1, 0, y1.Length);
            Array.Clear(y2, 0, y2.Length);
            delayState = 0.0;
        }

        /// <inheritdoc/>
        public override void Process(float[] samples) => ProcessRange(samples, 0, 1);

        /// <inheritdoc/>
        public override void Process(float[] samples, int channel, int channels) => ProcessRange(samples, channel, channels);

        /// <inheritdoc/>
        public override object Clone() => new HilbertAllpassBranch(!applyUnitDelay);

        void ProcessRange(float[] samples, int start, int step) {
            if (samples == null || samples.Length == 0 || start < 0 || step <= 0 || start >= samples.Length) {
                return;
            }
            double c0 = coeffs[0], c1 = coeffs[1], c2 = coeffs[2], c3 = coeffs[3];
            double sx10 = x1[0], sx20 = x2[0], sy10 = y1[0], sy20 = y2[0];
            double sx11 = x1[1], sx21 = x2[1], sy11 = y1[1], sy21 = y2[1];
            double sx12 = x1[2], sx22 = x2[2], sy12 = y1[2], sy22 = y2[2];
            double sx13 = x1[3], sx23 = x2[3], sy13 = y1[3], sy23 = y2[3];
            double d = delayState;

            for (int i = start; i < samples.Length; i += step) {
                double v = samples[i];
                double y = c0 * (sy20 - v) + sx20 + DenormalGuard;
                sx20 = sx10; sx10 = v; sy20 = sy10; sy10 = y; v = y;

                y = c1 * (sy21 - v) + sx21 + DenormalGuard;
                sx21 = sx11; sx11 = v; sy21 = sy11; sy11 = y; v = y;

                y = c2 * (sy22 - v) + sx22 + DenormalGuard;
                sx22 = sx12; sx12 = v; sy22 = sy12; sy12 = y; v = y;

                y = c3 * (sy23 - v) + sx23 + DenormalGuard;
                sx23 = sx13; sx13 = v; sy23 = sy13; sy13 = y; v = y;

                if (applyUnitDelay) {
                    double prev = d;
                    d = v;
                    v = prev;
                }
                samples[i] = (float)v;
            }

            x1[0] = FlushDenormal(sx10); x2[0] = FlushDenormal(sx20); y1[0] = FlushDenormal(sy10); y2[0] = FlushDenormal(sy20);
            x1[1] = FlushDenormal(sx11); x2[1] = FlushDenormal(sx21); y1[1] = FlushDenormal(sy11); y2[1] = FlushDenormal(sy21);
            x1[2] = FlushDenormal(sx12); x2[2] = FlushDenormal(sx22); y1[2] = FlushDenormal(sy12); y2[2] = FlushDenormal(sy22);
            x1[3] = FlushDenormal(sx13); x2[3] = FlushDenormal(sx23); y1[3] = FlushDenormal(sy13); y2[3] = FlushDenormal(sy23);
            delayState = FlushDenormal(d);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double FlushDenormal(double v) => Math.Abs(v) < 1e-150 ? 0.0 : v;
    }

    /// <summary>
    /// Converts a channel-based audio stream and JOC to object output samples.
    /// </summary>
    class JointObjectCodingApplier : IDisposable {
        /// <summary>
        /// Cavern is run by a legacy Mono runtime without hardware SIMD; prefer scalar paths when true.
        /// </summary>
        readonly bool mono;

        /// <summary>
        /// Length of an AC-3 frame.
        /// </summary>
        readonly int frameSize;

        /// <summary>
        /// Recycled timeslot object output arrays.
        /// </summary>
        readonly float[][] timeslotCache;

        /// <summary>
        /// Recycled forward transformation result holder.
        /// </summary>
        readonly (float[] real, float[] imaginary)[] results;

        /// <summary>
        /// Recycled QMFB operation arrays.
        /// </summary>
        readonly (float[] real, float[] imaginary)[] qmfbCache;

        /// <summary>
        /// Recycled QMFB transform objects.
        /// </summary>
        readonly QuadratureMirrorFilterBank[] converters;

        /// <summary>
        /// Pre-allocated reference-branch Hilbert allpass filters for non-phase-shifted bed channels.
        /// </summary>
        readonly HilbertAllpassBranch[] referencePhaseFilters;

        /// <summary>
        /// Applies 90-degree quadrature Hilbert phase compensation to the last two channels when <see cref="JointObjectCoding.LastPairPhaseShift"/> is active.
        /// </summary>
        Filter[] phaseShift;

        /// <summary>
        /// Tracks whether <see cref="JointObjectCoding.LastPairPhaseShift"/> was active on the previous timeslot.
        /// </summary>
        bool lastPairPhaseShiftActive;

        /// <summary>
        /// Channels to objects matrix.
        /// </summary>
        float[][][][] mixMatrix;

        /// <summary>
        /// Next timeslot to read in the current JOC.
        /// </summary>
        int timeslot;

        /// <summary>
        /// Creates a converter from a channel-based audio stream and JOC to object output samples.
        /// </summary>
        public JointObjectCodingApplier(JointObjectCoding joc, int frameSize) {
            mono = CavernAmp.IsMono();
            int maxChannels = JointObjectCodingTables.inputMatrix.Length;
            int objects = joc.ObjectCount;
            this.frameSize = frameSize;

            timeslotCache = new float[objects][];
            results = new (float[], float[])[maxChannels];
            qmfbCache = new (float[], float[])[objects];
            for (int obj = 0; obj < objects; obj++) {
                timeslotCache[obj] = new float[QuadratureMirrorFilterBank.subbands];
                qmfbCache[obj] = (new float[QuadratureMirrorFilterBank.subbands], new float[QuadratureMirrorFilterBank.subbands]);
            }

            int converterCount = Math.Max(maxChannels, objects);
            converters = new QuadratureMirrorFilterBank[converterCount];
            for (int i = 0; i < converterCount; i++) {
                converters[i] = new QuadratureMirrorFilterBank();
            }

            referencePhaseFilters = new HilbertAllpassBranch[maxChannels];
            for (int i = 0; i < maxChannels; i++) {
                referencePhaseFilters[i] = new HilbertAllpassBranch(false);
            }
            phaseShift = new Filter[] {
                new HilbertAllpassBranch(true),
                new HilbertAllpassBranch(true)
            };
        }

        /// <summary>
        /// Gets the audio samples of each object for the next timeslot.
        /// </summary>
        public unsafe float[][] Apply(float[][] input, JointObjectCoding joc) {
            if (timeslot == 0) {
                mixMatrix = joc.GetMixingMatrices(frameSize);
            }

            if (joc.LastPairPhaseShift) {
                if (!lastPairPhaseShiftActive) {
                    for (int i = 0; i < referencePhaseFilters.Length; i++) {
                        referencePhaseFilters[i].Reset();
                    }
                    for (int i = 0; i < phaseShift.Length; i++) {
                        ((HilbertAllpassBranch)phaseShift[i]).Reset();
                    }
                    lastPairPhaseShiftActive = true;
                }
                int startChannel = joc.ChannelCount - 2;
                for (int ch = 0; ch < startChannel; ch++) {
                    referencePhaseFilters[ch].Process(input[ch]);
                }
                for (int ch = startChannel; ch < joc.ChannelCount; ch++) {
                    phaseShift[ch - startChannel].Process(input[ch]);
                }
            } else if (lastPairPhaseShiftActive) {
                lastPairPhaseShiftActive = false;
            }

            // Inline forward QMFB transformations (zero ThreadPool wakeups/allocations per 64-sample timeslot)
            for (int ch = 0; ch < joc.ChannelCount; ch++) {
                fixed (float* pInput = input[ch]) {
                    results[ch] = !mono
                        ? converters[ch].ProcessForward(pInput)
                        : converters[ch].ProcessForward_Mono(pInput);
                }
            }

            // Inline inverse QMFB transformations
            int objects = joc.ObjectCount;
            for (int obj = 0; obj < objects; obj++) {
                if (CavernAmp.Available) {
                    ProcessObject_Amp(joc, obj, mixMatrix[obj][timeslot], joc.Gain);
                } else if (!mono) {
                    ProcessObject(joc, obj, mixMatrix[obj][timeslot], joc.Gain);
                } else {
                    ProcessObject_Mono(joc, obj, mixMatrix[obj][timeslot], joc.Gain);
                }
            }

            if (++timeslot == input.Length) {
                timeslot = 0;
            }
            return timeslotCache;
        }

        /// <summary>
        /// Free up resources used by this object.
        /// </summary>
        public void Dispose() { }

        /// <summary>
        /// Mixes channel-based samples by a matrix to the objects.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void ProcessObject(JointObjectCoding joc, int obj, float[][] mixMatrix, float gain) {
            (float[] real, float[] imaginary) = qmfbCache[obj];
            fixed (float* channelReal = results[0].real, channelImaginary = results[0].imaginary, channelMatrix = mixMatrix[0]) {
                QMath.MultiplyAndSet(channelReal, channelMatrix, real, QuadratureMirrorFilterBank.subbands);
                QMath.MultiplyAndSet(channelImaginary, channelMatrix, imaginary, QuadratureMirrorFilterBank.subbands);
            }
            for (int ch = 1; ch < joc.ChannelCount; ch++) {
                fixed (float* channelReal = results[ch].real, channelImaginary = results[ch].imaginary, channelMatrix = mixMatrix[ch]) {
                    QMath.MultiplyAndAdd(channelReal, channelMatrix, real, QuadratureMirrorFilterBank.subbands);
                    QMath.MultiplyAndAdd(channelImaginary, channelMatrix, imaginary, QuadratureMirrorFilterBank.subbands);
                }
            }
            converters[obj].ProcessInverse(qmfbCache[obj], timeslotCache[obj]);
            if (gain != 1) {
                WaveformUtils.Gain(timeslotCache[obj], gain);
            }
        }

        /// <summary>
        /// Mixes channel-based samples by a matrix to the objects.
        /// This version of the function is faster when <see cref="CavernAmp"/> is available.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void ProcessObject_Amp(JointObjectCoding joc, int obj, float[][] mixMatrix, float gain) {
            (float[] real, float[] imaginary) = qmfbCache[obj];
            fixed (float* pReal = real, pImaginary = imaginary) {
                fixed (float* channelReal = results[0].real, channelImaginary = results[0].imaginary, channelMatrix = mixMatrix[0]) {
                    CavernAmp.MultiplyAndSet(channelReal, channelMatrix, pReal, QuadratureMirrorFilterBank.subbands);
                    CavernAmp.MultiplyAndSet(channelImaginary, channelMatrix, pImaginary, QuadratureMirrorFilterBank.subbands);
                }
                for (int ch = 1; ch < joc.ChannelCount; ch++) {
                    fixed (float* channelReal = results[ch].real, channelImaginary = results[ch].imaginary, channelMatrix = mixMatrix[ch]) {
                        CavernAmp.MultiplyAndAdd(channelReal, channelMatrix, pReal, QuadratureMirrorFilterBank.subbands);
                        CavernAmp.MultiplyAndAdd(channelImaginary, channelMatrix, pImaginary, QuadratureMirrorFilterBank.subbands);
                    }
                }
            }
            if (!mono) {
                converters[obj].ProcessInverse(qmfbCache[obj], timeslotCache[obj]);
            } else {
                fixed (float* pReal = real, pImaginary = imaginary) {
                    converters[obj].ProcessInverse_Amp(pReal, pImaginary, timeslotCache[obj]);
                }
            }
            if (gain != 1) {
                WaveformUtils.Gain(timeslotCache[obj], gain);
            }
        }

        /// <summary>
        /// Mixes channel-based samples by a matrix to the objects.
        /// This version of the function is faster only in a Mono runtime (like Unity).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void ProcessObject_Mono(JointObjectCoding joc, int obj, float[][] mixMatrix, float gain) {
            (float[] real, float[] imaginary) = qmfbCache[obj];
            fixed (float* pReal = real, pImaginary = imaginary) {
                fixed (float* channelReal = results[0].real, channelImaginary = results[0].imaginary, channelMatrix = mixMatrix[0]) {
                    QMath.MultiplyAndSet_Mono(channelReal, channelMatrix, pReal, QuadratureMirrorFilterBank.subbands);
                    QMath.MultiplyAndSet_Mono(channelImaginary, channelMatrix, pImaginary, QuadratureMirrorFilterBank.subbands);
                }
                for (int ch = 1; ch < joc.ChannelCount; ch++) {
                    fixed (float* channelReal = results[ch].real, channelImaginary = results[ch].imaginary, channelMatrix = mixMatrix[ch]) {
                        QMath.MultiplyAndAdd_Mono(channelReal, channelMatrix, pReal, QuadratureMirrorFilterBank.subbands);
                        QMath.MultiplyAndAdd_Mono(channelImaginary, channelMatrix, pImaginary, QuadratureMirrorFilterBank.subbands);
                    }
                }
            }
            fixed (float* output = timeslotCache[obj]) {
                converters[obj].ProcessInverse_Mono(qmfbCache[obj], output);
            }
            if (gain != 1) {
                WaveformUtils.Gain(timeslotCache[obj], gain);
            }
        }
    }
}
