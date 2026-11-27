using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.Waveforms;

namespace Cavern {
    public partial class Source {
        /// <summary>
        /// Samples required to match the listener's update rate after pitch changes.
        /// This is the number of samples that has to be rendered.
        /// </summary>
        protected int PitchedUpdateRate { get; private set; }

        /// <summary>
        /// Cumulative source samples consumed across rendered blocks since the last accumulator reset.
        /// </summary>
        public long CumulativeSourceSamples { get; private set; }

        /// <summary>
        /// The <see cref="Listener"/> this source is attached to.
        /// </summary>
        protected internal Listener listener;

        /// <summary>
        /// Cached node from <see cref="Listener.activeSources"/> for faster detach.
        /// </summary>
        internal LinkedListNode<Source> listenerNode;

        /// <summary>
        /// Distance simulator for virtualization.
        /// </summary>
        Distancer distancer;

        /// <summary>
        /// Pure-physics ISO 9613-1 atmospheric air absorption filter (lazy-allocated only when physical distance exceeds 5 m).
        /// </summary>
        AirAbsorptionFilter airAbsorptionFilter;

        /// <summary>
        /// Pure-physics 6DoF acoustic propagation delay line (343 m/s, lazy-allocated when enabled).
        /// </summary>
        AcousticPropagationDelayLine propagationDelayLine;

        /// <summary>
        /// <see cref="PitchedUpdateRate"/> without resampling.
        /// </summary>
        int baseUpdateRate;

        /// <summary>
        /// Actually used pitch multiplier including the Doppler effect.
        /// </summary>
        float calculatedPitch;

        /// <summary>
        /// Distance from the listener.
        /// </summary>
        float distance = float.NaN;

        /// <summary>
        /// <see cref="distance"/> in the previous frame, required for Doppler effect calculation.
        /// </summary>
        float lastDistance;

        /// <summary>
        /// Contains smoothed pitch for the doppler effect. Used to mitigate frames where v = 0, because audio FPS > video FPS.
        /// </summary>
        float lastDoppler = 1;

        /// <summary>
        /// Sample rate multiplier to match the system sample rate.
        /// </summary>
        float resampleMult;

        /// <summary>
        /// Fractional sample-rate conversion accumulator preventing cumulative truncation drift across blocks.
        /// </summary>
        double sampleRateRemainder;

        /// <summary>
        /// Exact rational numerator remainder (modulo <see cref="Listener.SampleRate"/>) for zero-drift integer sample-rate ratios.
        /// </summary>
        long sampleRateNumeratorRem;

        /// <summary>
        /// Last <see cref="TimeSamples"/> value observed at the end of <see cref="AdvanceTimeSlot"/> for external seek detection.
        /// </summary>
        int lastExpectedTimeSamples = -1;

        /// <summary>
        /// Stereo mix cache to save allocation times.
        /// </summary>
        float[] leftSamples = Array.Empty<float>(), rightSamples = Array.Empty<float>();

        /// <summary>
        /// Rendered output array kept to save allocation time.
        /// </summary>
        float[] rendered = Array.Empty<float>();

        /// <summary>
        /// Mono mix cache to save allocation times.
        /// </summary>
        float[] samples = Array.Empty<float>();

        /// <summary>
        /// Resampled output caches matching <see cref="Listener.UpdateRate"/> for zero steady-state allocations.
        /// </summary>
        float[] resampledSamples = Array.Empty<float>(),
            resampledLeft = Array.Empty<float>(),
            resampledRight = Array.Empty<float>();

        /// <summary>
        /// Stateful polyphase Kaiser-windowed sinc resamplers for 1D/3D mono and 1D stereo paths.
        /// </summary>
        PolyphaseSincResampler monoResampler, leftResampler, rightResampler;

        /// <summary>
        /// Tracks active clip, sample rate, and resampling transitions to flush resampler history cleanly.
        /// </summary>
        Clip lastResampleClip;
        int lastResampleListenerRate;
        bool wasResamplingMono, wasResamplingStereo;

        /// <summary>
        /// Remaining delay until starting playback.
        /// </summary>
        long delay;

        /// <summary>
        /// Keeps a value in the given array, if it's smaller than any of its contents.
        /// </summary>
        /// <param name="target">Array reference</param>
        /// <param name="value">Value to insert</param>
        static void BottomlistHandler(float[] target, float value) {
            int replace = -1;
            for (int record = 0; record < target.Length; record++) {
                if (target[record] > value) {
                    replace = replace == -1 ? record : (target[record] > target[replace] ? record : replace);
                }
            }
            if (replace != -1) {
                target[replace] = value;
            }
        }

        /// <summary>
        /// Get the next samples in the audio stream.
        /// </summary>
        protected internal virtual MultichannelWaveform GetSamples() {
            int channels = Clip.Channels;
            if (Rendered == null || Rendered.Channels != channels || Rendered[0].Length != PitchedUpdateRate) {
                Rendered = new MultichannelWaveform(channels, PitchedUpdateRate);
            }
            if (Loop) {
                Clip.GetData(Rendered, TimeSamples);
            } else {
                Clip.GetDataNonLooping(Rendered, TimeSamples);
            }

            AdvanceTimeSlot();
            return Rendered;
        }

        /// <summary>
        /// Cache the samples if the source should be rendered. This wouldn't be thread safe.
        /// </summary>
        /// <returns>The collection should be performed, as all requirements are met</returns>
        protected internal virtual bool Precollect() {
            if (delay > 0) {
                delay -= listener.UpdateRate;
                return false;
            }
            if (listener.sourceDistances.Contains(distance)) {
                if (listener.AudioQuality != QualityModes.Low) {
                    if (DopplerLevel == 0) {
                        calculatedPitch = Pitch;
                    } else {
                        float dopplerTarget = Pitch + lastDoppler * // c / (c - dv), dv = ds / dt
                            (SpeedOfSound / (SpeedOfSound - (lastDistance - distance) / listener.pulseDelta) - 1);
                        lastDoppler = Math.Clamp(QMath.Lerp(lastDoppler, dopplerTarget,
                            10 * listener.UpdateRate / (float)listener.SampleRate), 0, DopplerLevel);
                        calculatedPitch = Math.Clamp(lastDoppler, .5f, 3f);
                    }
                } else {
                    calculatedPitch = 1; // Disable any pitch change on low quality
                }

                if ((lastExpectedTimeSamples >= 0 && TimeSamples != lastExpectedTimeSamples) ||
                    !ReferenceEquals(Clip, lastResampleClip) || listener.SampleRate != lastResampleListenerRate) {
                    sampleRateRemainder = 0.0;
                    sampleRateNumeratorRem = 0;
                    ResetResamplers();
                    lastResampleClip = Clip;
                    lastResampleListenerRate = listener.SampleRate;
                }

                double exactMult = listener.SampleRate > 0 && Clip.SampleRate > 0 && listener.SampleRate != Clip.SampleRate
                    ? Clip.SampleRate / (double)listener.SampleRate
                    : 1.0;
                resampleMult = (float)exactMult;
                double exactBase = listener.UpdateRate * (double)calculatedPitch;
                baseUpdateRate = (int)exactBase;
                int targetUpdateRate;
                if (calculatedPitch == 1f && listener.SampleRate > 0 && Clip.SampleRate > 0) {
                    long exactNum = (long)listener.UpdateRate * Clip.SampleRate + sampleRateNumeratorRem;
                    targetUpdateRate = (int)(exactNum / listener.SampleRate);
                    sampleRateNumeratorRem = exactNum % listener.SampleRate;
                    if (targetUpdateRate < 1) {
                        targetUpdateRate = 1;
                    }
                    sampleRateRemainder = sampleRateNumeratorRem / (double)listener.SampleRate;
                } else {
                    double exactPitched = exactBase * exactMult + sampleRateRemainder;
                    targetUpdateRate = (int)Math.Floor(exactPitched);
                    if (targetUpdateRate < 1) {
                        targetUpdateRate = 1;
                    }
                    sampleRateRemainder = exactPitched - targetUpdateRate;
                    sampleRateNumeratorRem = 0;
                }
                PitchedUpdateRate = targetUpdateRate;
                if (samples.Length < PitchedUpdateRate) {
                    samples = new float[PitchedUpdateRate];
                }
                if (Clip.Channels == 2 && leftSamples.Length < PitchedUpdateRate) {
                    leftSamples = new float[PitchedUpdateRate];
                    rightSamples = new float[PitchedUpdateRate];
                }
                Rendered = GetSamples();
                if (Rendered != null && Rendered.Channels > 0 && Rendered[0] != null && Rendered[0].Length != PitchedUpdateRate) {
                    PitchedUpdateRate = Rendered[0].Length;
                    if (samples.Length < PitchedUpdateRate) {
                        samples = new float[PitchedUpdateRate];
                    }
                    if (Clip.Channels == 2 && leftSamples.Length < PitchedUpdateRate) {
                        leftSamples = new float[PitchedUpdateRate];
                        rightSamples = new float[PitchedUpdateRate];
                    }
                }
                if (resampledSamples.Length != listener.UpdateRate) {
                    resampledSamples = new float[listener.UpdateRate];
                }
                if (Clip.Channels == 2 && resampledLeft.Length != listener.UpdateRate) {
                    resampledLeft = new float[listener.UpdateRate];
                    resampledRight = new float[listener.UpdateRate];
                }
                CumulativeSourceSamples += PitchedUpdateRate;
                int channelLen = (listener != null ? listener.Context : ListenerContext.Current).Channels.Length;
                if (rendered.Length != channelLen * listener.UpdateRate) {
                    rendered = new float[channelLen * listener.UpdateRate];
                }
                return true;
            }
            Rendered = null;
            return false;
        }

        /// <summary>
        /// Process the source and returns a mix to be added to the output.
        /// </summary>
        protected internal virtual float[] Collect() {
            if (PositionLocked) {
                Position = lockedPosition;
            }
            // Preparations, clean environment
            ListenerContext ctx = listener != null ? listener.Context : ListenerContext.Current;
            Channel[] channels = ctx.Channels;
            int updateRate = listener.UpdateRate;
            Array.Clear(rendered, 0, rendered.Length);

            // Render audio if not muted
            if (!Mute) {
                int clipChannels = Clip.Channels;
                bool need1D = SpatialBlend != 1;
                bool need3D = SpatialBlend != 0 && distance < listener.Range;
                bool needMonoPath = need3D || (need1D && clipChannels != 2);
                bool needStereoPath = need1D && clipChannels == 2;
                bool needsResample = PitchedUpdateRate != updateRate ||
                    (listener.SampleRate > 0 && Clip.SampleRate > 0 && Clip.SampleRate != listener.SampleRate) ||
                    calculatedPitch != 1f;
                double stepRatio = listener.SampleRate > 0 && Clip.SampleRate > 0
                    ? (Clip.SampleRate / (double)listener.SampleRate) * calculatedPitch
                    : (calculatedPitch != 1f ? calculatedPitch : (updateRate > 0 ? (double)PitchedUpdateRate / updateRate : 1.0));

                // Mono downmix / extraction and zero-alloc stateful Kaiser-sinc resampling
                if (needMonoPath) {
                    if (listener.AudioQuality >= QualityModes.High && clipChannels != 1) { // Mono downmix above medium quality
                        Array.Clear(samples, 0, PitchedUpdateRate);
                        for (int channel = 0; channel < clipChannels; channel++) {
                            WaveformUtils.Mix(Rendered[channel], samples);
                        }
                        float invChannels = 1f / clipChannels;
                        for (int i = 0; i < PitchedUpdateRate; i++) {
                            samples[i] *= invChannels;
                        }
                    } else { // First channel only otherwise
                        Array.Copy(Rendered[0], samples, PitchedUpdateRate);
                    }

                    if (resampledSamples.Length != updateRate) {
                        resampledSamples = new float[updateRate];
                    }
                    if (needsResample) {
                        (monoResampler ??= new PolyphaseSincResampler()).Process(samples, PitchedUpdateRate, resampledSamples, updateRate, stepRatio);
                        wasResamplingMono = true;
                    } else {
                        if (wasResamplingMono) {
                            monoResampler?.Reset();
                            wasResamplingMono = false;
                        }
                        Array.Copy(samples, 0, resampledSamples, 0, updateRate);
                    }
                }

                // 1D renderer
                if (need1D) {
                    float volume1D = Volume * (1f - SpatialBlend);
                    // 1:1 mix for non-stereo sources
                    if (clipChannels != 2) {
                        float gain = volume1D / channels.Length;
                        for (int channel = 0; channel < channels.Length; channel++) {
                            WaveformUtils.Mix(resampledSamples, rendered, channel, channels.Length, gain);
                        }
                    }

                    // Full side mix for stereo sources
                    else if (needStereoPath) {
                        Array.Copy(Rendered[0], leftSamples, PitchedUpdateRate);
                        Array.Copy(Rendered[1], rightSamples, PitchedUpdateRate);
                        if (resampledLeft.Length != updateRate) {
                            resampledLeft = new float[updateRate];
                            resampledRight = new float[updateRate];
                        }
                        if (needsResample) {
                            (leftResampler ??= new PolyphaseSincResampler()).Process(leftSamples, PitchedUpdateRate, resampledLeft, updateRate, stepRatio);
                            (rightResampler ??= new PolyphaseSincResampler()).Process(rightSamples, PitchedUpdateRate, resampledRight, updateRate, stepRatio);
                            wasResamplingStereo = true;
                        } else {
                            if (wasResamplingStereo) {
                                leftResampler?.Reset();
                                rightResampler?.Reset();
                                wasResamplingStereo = false;
                            }
                            Array.Copy(leftSamples, 0, resampledLeft, 0, updateRate);
                            Array.Copy(rightSamples, 0, resampledRight, 0, updateRate);
                        }
                        Stereo1DMix(resampledLeft, resampledRight, volume1D, ctx);
                    }
                }

                // 3D mix, if the source is in range
                if (need3D) {
                    Vector3 position = screenLocked
                        ? new Vector3(ctx.ScreenSize.X * Position.X, ctx.ScreenSize.Y * Position.Y, Position.Z + 1)
                        : Position;
                    Vector3 direction = (position - listener.Position).RotateInverse(listener.Rotation);
                    float rolloffDistance = GetRolloff();
                    baseUpdateRate = updateRate;
                    // Apply filter if set
                    SpatialFilter?.Process(resampledSamples);

                    // Pure-physics ISO 9613-1 atmospheric air absorption for physical source distances > 5.0 m (unity bypass <= 5.0 m)
                    if (AirAbsorptionFilter.GlobalEnabled && !LFE) {
                        Vector3 physDelta = (position - listener.Position) * ctx.EnvironmentSizeInverse * AirAbsorptionFilter.PhysicalRoomHalfSize;
                        float physicalDist = physDelta.Length();
                        if (physicalDist > AirAbsorptionFilter.DirectPathThresholdMeters) {
                            if (airAbsorptionFilter == null) {
                                airAbsorptionFilter = new AirAbsorptionFilter();
                            }
                            airAbsorptionFilter.ConfigureDirectPath(
                                physicalDist,
                                listener.SampleRate,
                                AirAbsorptionFilter.GlobalTemperatureCelsius,
                                AirAbsorptionFilter.GlobalRelativeHumidityPct);
                            airAbsorptionFilter.Process(resampledSamples, 0, resampledSamples.Length);
                        } else if (airAbsorptionFilter != null && airAbsorptionFilter.IsActive) {
                            airAbsorptionFilter.ConfigureDirectPath(
                                0f,
                                listener.SampleRate,
                                AirAbsorptionFilter.GlobalTemperatureCelsius,
                                AirAbsorptionFilter.GlobalRelativeHumidityPct);
                        }
                    }

                    // Pure-physics 6DoF acoustic time-of-flight propagation delay at 343 m/s (skip on dedicated LFE)
                    if (AcousticPropagationDelayLine.GlobalEnabled && !LFE) {
                        if (propagationDelayLine == null) {
                            propagationDelayLine = new AcousticPropagationDelayLine();
                        }
                        Vector3 propDelta = (position - listener.Position) * ctx.EnvironmentSizeInverse * AirAbsorptionFilter.PhysicalRoomHalfSize;
                        float propDistMeters = propDelta.Length();
                        propagationDelayLine.ProcessInPlace(resampledSamples, 0, resampledSamples.Length, propDistMeters, listener.SampleRate);
                    } else if (propagationDelayLine != null && propagationDelayLine.ActiveDelayMs > 0f) {
                        propagationDelayLine.Reset();
                    }

                    // Distance simulation for HRTF (skip on dedicated LFE subwoofer sources)
                    if (DistanceSimulation && ctx.HeadphoneVirtualizer && !LFE) {
                        distancer ??= new Distancer(this) {
                            trueGain = false
                        };
                        distancer.Generate(direction.X > 0, resampledSamples);
                    }

                    // Final mixing
                    ctx.Renderer.Render(listener, this, direction, resampledSamples, rendered, Volume * rolloffDistance * SpatialBlend);
                }
            }
            return rendered;
        }

        /// <summary>
        /// Makes sure if <see cref="Precollect"/> is called immediately after this function, it will return true.
        /// </summary>
        protected void ForcePrecollect() => listener.sourceDistances[0] = distance;

        /// <summary>
        /// Calculate distance from the <see cref="Listener"/> and choose the closest sources to play.
        /// </summary>
        internal void Precalculate() {
            if (PositionLocked) {
                Position = lockedPosition;
            }
            if (Renderable) {
                lastDistance = distance;
                distance = Vector3.Distance(Position, listener.Position);
                if (float.IsNaN(lastDistance)) {
                    lastDistance = distance;
                }
                BottomlistHandler(listener.sourceDistances, distance);
            } else {
                distance = float.NaN;
            }
        }

        /// <summary>
        /// Resets the fractional sample-rate conversion accumulator and cumulative consumed sample counter.
        /// </summary>
        public void ResetSampleRateAccumulator() {
            sampleRateRemainder = 0.0;
            sampleRateNumeratorRem = 0;
            lastExpectedTimeSamples = -1;
            CumulativeSourceSamples = 0;
            ResetResamplers();
        }

        /// <summary>
        /// Clears stateful polyphase resampler histories on seek, clip switch, or sample rate change.
        /// </summary>
        void ResetResamplers() {
            monoResampler?.Reset();
            leftResampler?.Reset();
            rightResampler?.Reset();
            wasResamplingMono = false;
            wasResamplingStereo = false;
        }

        /// <summary>
        /// Goes to the part in the clip which will be played on the next sample collection.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AdvanceTimeSlot() {
            TimeSamples += PitchedUpdateRate;
            if (TimeSamples >= Clip.Samples) {
                if (Loop) {
                    TimeSamples %= Clip.Samples;
                } else {
                    TimeSamples = 0;
                    IsPlaying = false;
                }
            }
            lastExpectedTimeSamples = TimeSamples;
        }

        /// <summary>
        /// Render the stereo side mix.
        /// </summary>
        void Stereo1DMix(float[] left, float[] right, float volume1D, ListenerContext ctx) {
            float leftVolume = volume1D / ctx.LeftChannels,
                rightVolume = volume1D / ctx.RightChannels;
            if (stereoPan < 0) {
                rightVolume *= 1 - stereoPan * stereoPan;
            } else if (stereoPan > 0) {
                leftVolume *= 1 - stereoPan * stereoPan;
            }
            float halfVolume1D = volume1D * .5f;
            Channel[] channels = ctx.Channels;
            for (int channel = 0; channel < channels.Length; ++channel) {
                if (channels[channel].LFE) {
                    if (!listener.LFESeparation || LFE) {
                        WaveformUtils.Mix(left, rendered, channel, channels.Length, halfVolume1D);
                        WaveformUtils.Mix(right, rendered, channel, channels.Length, halfVolume1D);
                    }
                } else if (!LFE) {
                    if (channels[channel].Y < 0) {
                        WaveformUtils.Mix(left, rendered, channel, channels.Length, leftVolume);
                    } else if (channels[channel].Y > 0) {
                        WaveformUtils.Mix(right, rendered, channel, channels.Length, rightVolume);
                    }
                }
            }
        }

        /// <summary>
        /// Reference sound velocity in m/s (dry air, 25.4 degrees Celsius).
        /// </summary>
        public static readonly float SpeedOfSound = 346.74f;
    }
}
