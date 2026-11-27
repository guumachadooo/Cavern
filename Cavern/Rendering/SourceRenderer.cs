using System;
using System.Numerics;

using Cavern.Filters;
using Cavern.Utilities;

namespace Cavern.Rendering {
    /// <summary>
    /// Represents a technique for mixing <see cref="Source"/>s to <see cref="Listener"/> environments.
    /// </summary>
    public abstract class SourceRenderer {
        /// <summary>
        /// Mix the audio <paramref name="source"/> to its <see cref="Listener"/>.
        /// </summary>
        /// <param name="listener">Rendering environment</param>
        /// <param name="source">The audio source to render</param>
        /// <param name="direction">The direction of the <see cref="Source"/> from the <see cref="Listener"/>,
        /// in a space where the <see cref="Listener"/> is the origin</param>
        /// <param name="samples">Samples rendered by <see cref="Source.Precollect"/></param>
        /// <param name="rendered">The per-<see cref="Source"/> output mix for the <see cref="Listener.Channels"/></param>
        /// <param name="gain">Mixing volume in linear gain, not decibels</param>
        public abstract void Render(Listener listener, Source source, Vector3 direction, float[] samples, float[] rendered, float gain);

        /// <summary>
        /// Mix the input <paramref name="samples"/> to the LFE tracks of the <paramref name="rendered"/> result.
        /// The <paramref name="gain"/> shouldn't account for the -10 dB LFE mixing level, it will be calculated here.
        /// </summary>
        protected static void MixToLFE(float[] samples, float[] rendered, float gain) {
            Channel[] channels = Listener.Channels;
            for (int channel = 0; channel < channels.Length; channel++) {
                if (channels[channel].LFE) {
                    WaveformUtils.Mix(samples, rendered, channel, channels.Length, gain);
                }
            }
        }

        /// <summary>
        /// Mix the input <paramref name="samples"/> to LFE tracks, or cleanly fold dedicated LFE sources into the
        /// front L/R speakers (with 120 Hz lowpass and <see cref="Listener.LFEVolume"/>) when the active speaker
        /// layout has no dedicated LFE channel (e.g. Direct 2.0, 2.0, 4.0, 4.0.4).
        /// </summary>
        protected static void MixToLFE(Listener listener, bool isLfeSource, float[] samples, float[] rendered, float gain) {
            Channel[] channels = (listener != null ? listener.Context : ListenerContext.Current).Channels;
            bool hasLfe = false;
            for (int channel = 0; channel < channels.Length; channel++) {
                if (channels[channel].LFE) {
                    WaveformUtils.Mix(samples, rendered, channel, channels.Length, gain);
                    hasLfe = true;
                }
            }
            if (hasLfe || !isLfeSource || channels.Length == 0 || listener == null) {
                return;
            }

            float[] lfeSamples = samples;
            if (!listener.DirectLFE && listener.fallbackLfeLowpass != null) {
                if (listener.fallbackLfeBuffer == null || listener.fallbackLfeBuffer.Length != samples.Length) {
                    listener.fallbackLfeBuffer = new float[samples.Length];
                }
                Array.Copy(samples, listener.fallbackLfeBuffer, samples.Length);
                listener.fallbackLfeLowpass.Process(listener.fallbackLfeBuffer);
                lfeSamples = listener.fallbackLfeBuffer;
            }

            if (channels.Length == 1) {
                WaveformUtils.Mix(lfeSamples, rendered, 0, 1, gain * listener.LFEVolume);
            } else {
                int frontL = 0, frontR = 1;
                float bestL = float.MaxValue, bestR = float.MaxValue;
                for (int i = 0; i < channels.Length; i++) {
                    if (channels[i].X == 0 && channels[i].Y < 0 && channels[i].Y >= -60) {
                        float dist = MathF.Abs(channels[i].Y + 30f);
                        if (dist < bestL) {
                            bestL = dist;
                            frontL = i;
                        }
                    } else if (channels[i].X == 0 && channels[i].Y > 0 && channels[i].Y <= 60) {
                        float dist = MathF.Abs(channels[i].Y - 30f);
                        if (dist < bestR) {
                            bestR = dist;
                            frontR = i;
                        }
                    }
                }
                const float equalPower = 0.70710678118f; // -3 dB per front speaker = 0 dB total LFE acoustic power
                float mixGain = gain * listener.LFEVolume * equalPower;
                WaveformUtils.Mix(lfeSamples, rendered, frontL, channels.Length, mixGain);
                WaveformUtils.Mix(lfeSamples, rendered, frontR, channels.Length, mixGain);
            }
        }
    }
}
