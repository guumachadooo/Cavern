using System;
using System.Numerics;

using Cavern.Channels;
using Cavern.Remapping.Exceptions;
using Cavern.Spatial;
using Cavern.Utilities;

namespace Cavern.Remapping {
    /// <summary>
    /// Creates 5.1 or 7.1 mixes from any legacy stream by DirAC/COMPASS-inspired parametric STFT upmixing
    /// (with orthogonal all-pass decorrelation and unity energy conservation) or legacy time-domain matrixing.
    /// Keeps any additional native channels intact and delay-aligned.
    /// </summary>
    public class SurroundUpmixer : Upmixer, ISoundfieldTelemetryProvider, IDisposable {
        /// <summary>
        /// Which input channel should be routed to which output without any modification.
        /// </summary>
        readonly int[] sourceRouting;

        /// <summary>
        /// The front channels are available and upmixing can commence.
        /// </summary>
        readonly bool frontsAvailable;

        /// <summary>
        /// Native Front Center channel is present in the input.
        /// </summary>
        readonly bool frontCenterAvailable;

        /// <summary>
        /// Native LFE channel is present in the input.
        /// </summary>
        readonly bool lfeAvailable;

        /// <summary>
        /// Both Front Center and LFE are available (legacy check preserved).
        /// </summary>
        readonly bool centerAvailable;

        /// <summary>
        /// The side channels are available and no stereo-to-side upmixing is needed for them.
        /// </summary>
        readonly bool sidesAvailable;

        /// <summary>
        /// The rear channels are available and no surround-to-rear expansion is needed for them.
        /// </summary>
        readonly bool rearsAvailable;

        /// <summary>
        /// Upmix up to 5.1 only.
        /// </summary>
        readonly bool mode51;

        /// <summary>
        /// Primary STFT analyzer for Front L/R decomposition and soundfield telemetry.
        /// </summary>
        readonly ParametricStftAnalyzer frontStft;

        /// <summary>
        /// Secondary STFT analyzer for 5.1 -> 7.1 diffuse surround-to-rear expansion.
        /// </summary>
        readonly ParametricStftAnalyzer surroundStft;

        /// <summary>
        /// Per-channel delay lines keeping pass-through channels sample-aligned with STFT latency (512 samples).
        /// </summary>
        readonly FixedSampleDelayLine[] alignDelays;

        /// <summary>
        /// Enable DirAC/COMPASS parametric STFT upmixing with orthogonal decorrelation (default: true).
        /// Set to false to use the legacy time-domain matrix equations.
        /// </summary>
        public bool UseParametricStft { get; set; } = true;

        /// <summary>
        /// Latest energy-weighted DirAC/COMPASS soundfield telemetry computed from the active stream.
        /// </summary>
        public SoundfieldTelemetry LatestTelemetry { get; private set; } = SoundfieldTelemetry.Empty();

        /// <summary>
        /// Creates 7.1 mixes from any legacy stream by parametric STFT or matrix upmixing. Keeps any additional channels, and
        /// uses Cavern's channel positions.
        /// </summary>
        /// <param name="sourceChannels">The channel that is present at each index in the input array</param>
        /// <param name="sampleRate">Content sample rate</param>
        public SurroundUpmixer(ReferenceChannel[] sourceChannels, int sampleRate) : this(sourceChannels, sampleRate, false, false) { }

        /// <summary>
        /// Creates 5.1 or 7.1 mixes from any legacy stream by parametric STFT or matrix upmixing. Keeps any additional channels.
        /// </summary>
        /// <param name="sourceChannels">The channel that is present at each index in the input array</param>
        /// <param name="sampleRate">Content sample rate</param>
        /// <param name="mode51">Upmix up to 5.1 only, and don't create the rear channels</param>
        /// <param name="widen">Use the corners of the room for speaker placements
        /// instead of Cavern's internal positions (&quot;movie mode&quot;)</param>
        public SurroundUpmixer(ReferenceChannel[] sourceChannels, int sampleRate, bool mode51, bool widen) :
            base(matrixSize, sampleRate) {
            sourceRouting = new int[sourceChannels.Length];
            this.mode51 = mode51;

            bool[] bedAvailability = new bool[matrixSize];
            ReferenceChannel[] matrix = ChannelPrototype.GetStandardMatrix(matrixSize);
            for (int i = 0; i < sourceChannels.Length; i++) {
                sourceRouting[i] = -1;
                for (int j = 0; j < matrix.Length; j++) {
                    if (sourceChannels[i] == matrix[j]) {
                        if ((int)matrix[j] < matrixSize) {
                            bedAvailability[(int)matrix[j]] = true;
                        }
                        sourceRouting[i] = j;
                        break;
                    }
                }
                if (sourceRouting[i] == -1) {
                    throw new LegacyInputExpectedException();
                }
            }

            frontsAvailable = bedAvailability[(int)ReferenceChannel.FrontLeft] && bedAvailability[(int)ReferenceChannel.FrontRight];
            frontCenterAvailable = bedAvailability[(int)ReferenceChannel.FrontCenter];
            lfeAvailable = bedAvailability[(int)ReferenceChannel.ScreenLFE];
            centerAvailable = frontCenterAvailable && lfeAvailable;
            sidesAvailable = bedAvailability[(int)ReferenceChannel.SideLeft] && bedAvailability[(int)ReferenceChannel.SideRight];
            rearsAvailable = bedAvailability[(int)ReferenceChannel.RearLeft] && bedAvailability[(int)ReferenceChannel.RearRight];

            frontStft = new ParametricStftAnalyzer(sampleRate);
            if (sidesAvailable && !rearsAvailable && !mode51) {
                surroundStft = new ParametricStftAnalyzer(sampleRate);
            }
            alignDelays = new FixedSampleDelayLine[matrixSize];
            for (int i = 0; i < matrixSize; i++) {
                alignDelays[i] = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
            }

            Vector3[] positions = widen ? ChannelPrototype.ToAlternativePositions(matrix) : ChannelPrototype.ToPositions(matrix);
            if (mode51) {
                positions[(int)ReferenceChannel.SideLeft] = positions[(int)ReferenceChannel.RearLeft];
                positions[(int)ReferenceChannel.SideRight] = positions[(int)ReferenceChannel.RearRight];
            }
            for (int i = 0; i < positions.Length; i++) {
                IntermediateSources[i].Position = positions[i];
            }
            IntermediateSources[(int)ReferenceChannel.ScreenLFE].LFE = true;
        }

        /// <summary>
        /// Clears internal STFT overlap-add buffers, covariance states, decorrelators, and alignment delay lines.
        /// </summary>
        public void Reset() {
            frontStft.Reset();
            if (surroundStft != null) {
                surroundStft.Reset();
            }
            for (int i = 0; i < alignDelays.Length; i++) {
                alignDelays[i].Reset();
            }
            LatestTelemetry = SoundfieldTelemetry.Empty();
        }

        /// <summary>
        /// Get the input samples, place the upmixed targets in space, and return their samples.
        /// </summary>
        protected override float[][] UpdateSources(int samplesPerSource) {
            float[][] input = OnSamplesNeeded(samplesPerSource);
            for (int i = 0; i < output.Length; i++) {
                Array.Clear(output[i], 0, samplesPerSource);
            }
            for (int i = 0; i < input.Length; i++) {
                if (sourceRouting[i] != -1) {
                    Array.Copy(input[i], output[sourceRouting[i]], input[i].Length);
                }
            }

            if (!frontsAvailable) {
                return output;
            }

            if (!UseParametricStft) {
                return UpdateSourcesLegacy(samplesPerSource);
            }

            int flIdx = (int)ReferenceChannel.FrontLeft;
            int frIdx = (int)ReferenceChannel.FrontRight;
            int fcIdx = (int)ReferenceChannel.FrontCenter;
            int lfeIdx = (int)ReferenceChannel.ScreenLFE;
            int slIdx = (int)ReferenceChannel.SideLeft;
            int srIdx = (int)ReferenceChannel.SideRight;
            int rlIdx = (int)ReferenceChannel.RearLeft;
            int rrIdx = (int)ReferenceChannel.RearRight;

            if (!sidesAvailable && !rearsAvailable) {
                // Stereo / 2.1 / 3.0 / 3.1 -> 5.1 or 7.1 via DirAC/COMPASS parametric STFT + orthogonal decorrelation
                bool extractCenter = !frontCenterAvailable;
                frontStft.ProcessStereoToSurround(
                    output[flIdx],
                    output[frIdx],
                    output[flIdx],
                    output[frIdx],
                    extractCenter ? output[fcIdx] : null,
                    output[slIdx],
                    output[srIdx],
                    mode51 ? null : output[rlIdx],
                    mode51 ? null : output[rrIdx],
                    samplesPerSource,
                    extractCenter,
                    mode51);
                if (frontCenterAvailable) {
                    alignDelays[fcIdx].Process(output[fcIdx], output[fcIdx], samplesPerSource);
                }
                if (lfeAvailable) {
                    alignDelays[lfeIdx].Process(output[lfeIdx], output[lfeIdx], samplesPerSource);
                }
                LatestTelemetry = frontStft.LatestTelemetry;
                return output;
            }

            if (sidesAvailable && !rearsAvailable && !mode51 && surroundStft != null) {
                // 5.1 -> 7.1: Keep direct side events locked on SL/SR; split diffuse surround wash orthogonally to RL/RR
                frontStft.AnalyzeOnly(output[flIdx], output[frIdx], samplesPerSource);
                alignDelays[flIdx].Process(output[flIdx], output[flIdx], samplesPerSource);
                alignDelays[frIdx].Process(output[frIdx], output[frIdx], samplesPerSource);
                if (frontCenterAvailable) {
                    alignDelays[fcIdx].Process(output[fcIdx], output[fcIdx], samplesPerSource);
                } else {
                    ExtractBalancedCenter();
                }
                if (lfeAvailable) {
                    alignDelays[lfeIdx].Process(output[lfeIdx], output[lfeIdx], samplesPerSource);
                }
                surroundStft.ProcessSurroundSplitToRear(
                    output[slIdx],
                    output[srIdx],
                    output[slIdx],
                    output[srIdx],
                    output[rlIdx],
                    output[rrIdx],
                    samplesPerSource);
                LatestTelemetry = frontStft.LatestTelemetry;
                return output;
            }

            if (!sidesAvailable && rearsAvailable) {
                // Quad / 4.1 (FL, FR, RL, RR): extract balanced center if missing and power-preserving side interpolation
                frontStft.AnalyzeOnly(output[flIdx], output[frIdx], samplesPerSource);
                if (!frontCenterAvailable) {
                    ExtractBalancedCenter();
                }
                PowerPreservingMixOf2(ReferenceChannel.FrontLeft, ReferenceChannel.RearLeft, ReferenceChannel.SideLeft, samplesPerSource);
                PowerPreservingMixOf2(ReferenceChannel.FrontRight, ReferenceChannel.RearRight, ReferenceChannel.SideRight, samplesPerSource);
                LatestTelemetry = frontStft.LatestTelemetry;
                return output;
            }

            // Native 7.1 (or 5.1 in mode51): bit-transparent pass-through with live soundfield telemetry
            frontStft.AnalyzeOnly(output[flIdx], output[frIdx], samplesPerSource);
            LatestTelemetry = frontStft.LatestTelemetry;
            return output;
        }

        float[][] UpdateSourcesLegacy(int samplesPerSource) {
            frontStft.AnalyzeOnly(output[(int)ReferenceChannel.FrontLeft], output[(int)ReferenceChannel.FrontRight], samplesPerSource);
            LatestTelemetry = frontStft.LatestTelemetry;

            if (!centerAvailable) {
                ExtractBalancedCenter();
            }

            if (!sidesAvailable) {
                if (rearsAvailable) {
                    MonoMixOf2(ReferenceChannel.FrontLeft, ReferenceChannel.RearLeft, ReferenceChannel.SideLeft);
                    MonoMixOf2(ReferenceChannel.FrontRight, ReferenceChannel.RearRight, ReferenceChannel.SideRight);
                } else {
                    DifferenceOf2(ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.SideLeft);
                    DifferenceOf2(ReferenceChannel.FrontRight, ReferenceChannel.FrontLeft, ReferenceChannel.SideRight);
                }
            }

            if (!rearsAvailable && !mode51) {
                WaveformUtils.Insert(output[(int)ReferenceChannel.SideLeft], output[(int)ReferenceChannel.RearLeft], .5f);
                WaveformUtils.Insert(output[(int)ReferenceChannel.SideRight], output[(int)ReferenceChannel.RearRight], .5f);
            }

            return output;
        }

        /// <summary>
        /// Extracts Front Center from Front Left/Right using a linear power-preserving matrix:
        /// <c>C = (L + R) / 2</c>, <c>L' = mid * 0.7071 + side</c>, <c>R' = mid * 0.7071 - side</c>.
        /// Preserves exact 0 dB acoustic power for both mono center vocals (<c>L'^2 + R'^2 + C^2 = L^2 + R^2</c>)
        /// and hard-panned stereo signals while keeping <c>(L' - R') / 2 == side</c> intact for surround derivation.
        /// </summary>
        void ExtractBalancedCenter() {
            const float midKeep = 0.70710678118f; // 1 / sqrt(2)
            float[] left = output[(int)ReferenceChannel.FrontLeft];
            float[] right = output[(int)ReferenceChannel.FrontRight];
            float[] center = output[(int)ReferenceChannel.FrontCenter];
            for (int i = 0; i < left.Length; i++) {
                float mid = (left[i] + right[i]) * 0.5f;
                float side = (left[i] - right[i]) * 0.5f;
                center[i] = mid;
                left[i] = mid * midKeep + side;
                right[i] = mid * midKeep - side;
            }
        }

        /// <summary>
        /// Power-preserving interpolation creating a side channel from front and rear while scaling sources to conserve energy.
        /// </summary>
        void PowerPreservingMixOf2(ReferenceChannel source1, ReferenceChannel source2, ReferenceChannel target, int count) {
            const float keepGain = 0.86602540378f; // sqrt(0.75)
            const float sendGain = 0.35355339059f; // sqrt(0.125) per source -> 0.75 + 0.75 + 2*0.25 = 2.0 (unity on uncorrelated)
            float[] s1 = output[(int)source1];
            float[] s2 = output[(int)source2];
            float[] dst = output[(int)target];
            for (int i = 0; i < count; i++) {
                float a = s1[i];
                float b = s2[i];
                dst[i] = (a + b) * sendGain;
                s1[i] = a * keepGain;
                s2[i] = b * keepGain;
            }
        }

        /// <summary>
        /// Mix a channel from two others by the (<paramref name="source1"/> + <paramref name="source2"/>) / 2 formula.
        /// </summary>
        void MonoMixOf2(ReferenceChannel source1, ReferenceChannel source2, ReferenceChannel target) {
            float[] targetArray = output[(int)target];
            output[(int)source1].CopyTo(targetArray);
            WaveformUtils.Mix(output[(int)source2], targetArray);
            WaveformUtils.Gain(targetArray, .5f);
        }

        /// <summary>
        /// Mix a channel from two others by the (<paramref name="source1"/> - <paramref name="source2"/>) / 2 formula.
        /// </summary>
        void DifferenceOf2(ReferenceChannel source1, ReferenceChannel source2, ReferenceChannel target) {
            float[] targetArray = output[(int)target];
            output[(int)source1].CopyTo(targetArray);
            output[(int)source2].SubtractFrom(targetArray);
            WaveformUtils.Gain(targetArray, .5f);
        }

        /// <inheritdoc/>
        public void Dispose() {
            frontStft.Dispose();
            if (surroundStft != null) {
                surroundStft.Dispose();
            }
        }

        /// <summary>
        /// Number of output channels, the corresponding standard matrix will be used.
        /// </summary>
        /// <remarks>Some values are hardcoded, this must always be 8.</remarks>
        public static readonly int matrixSize = 8;
    }
}
