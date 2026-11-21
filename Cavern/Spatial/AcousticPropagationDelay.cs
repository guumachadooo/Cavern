using System;
using System.Numerics;

namespace Cavern.Spatial {
    /// <summary>
    /// Pure-physics 6DoF acoustic propagation delay at the standard speed of sound in dry air
    /// (<c>c = 343.0 m/s</c> at 20 °C, ISO 9613-1). Provides zero-GC fractional cubic-Lagrange delay lines
    /// with slew-rate-limited delay smoothing so walking in 6DoF <c>(X, Y, Z)</c> or dragging 3D objects/stems
    /// updates arrival time smoothly without clicks or zipper artifacts.
    /// Strict C# 8.0 (<c>netstandard2.1</c>).
    /// </summary>
    public sealed class AcousticPropagationDelayLine {
        /// <summary>
        /// Standard speed of sound in dry air at 20 °C (meters per second).
        /// </summary>
        public const float SpeedOfSoundMetersPerSec = 343.0f;

        /// <summary>
        /// Maximum supported physical propagation distance for real-time delay buffering (meters).
        /// 35 m at 343 m/s corresponds to ~102 ms (~4,900 samples at 48 kHz).
        /// </summary>
        public const float MaxBufferedDistanceMeters = 35.0f;

        /// <summary>
        /// Global toggle enabling 6DoF acoustic propagation delay inside <see cref="Source.Collect"/>.
        /// Disabled on LFE sources automatically to preserve subwoofer phase alignment.
        /// </summary>
        public static volatile bool GlobalEnabled;

        /// <summary>
        /// Maximum slew rate in delay samples per processed audio sample (prevents pitch clicks during fast walks).
        /// 0.015 samples/sample corresponds to ~5.1 m/s max acoustic shift rate.
        /// </summary>
        const float MaxDelaySlewPerSample = 0.015f;

        float[] ringBuffer = Array.Empty<float>();
        int ringMask;
        int writeIndex;
        int configuredSampleRate;
        float currentDelaySamples;
        bool initialized;

        /// <summary>
        /// Latest smoothed propagation delay currently applied in milliseconds.
        /// </summary>
        public float ActiveDelayMs {
            get {
                return configuredSampleRate > 0 ? currentDelaySamples * 1000f / configuredSampleRate : 0f;
            }
        }

        /// <summary>
        /// Computes physical Euclidean distance in meters between a source and listener position.
        /// </summary>
        public static float ComputeDistanceMeters(Vector3 sourceWorldMeters, Vector3 listenerWorldMeters) {
            return Vector3.Distance(sourceWorldMeters, listenerWorldMeters);
        }

        /// <summary>
        /// Computes exact acoustic time-of-flight delay in milliseconds (<c>d / 343.0 * 1000</c>).
        /// </summary>
        public static float ComputePropagationDelayMs(float distanceMeters) {
            if (distanceMeters <= 0f || float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters)) {
                return 0f;
            }
            return (distanceMeters / SpeedOfSoundMetersPerSec) * 1000f;
        }

        /// <summary>
        /// Computes exact acoustic time-of-flight delay in fractional audio samples at <paramref name="sampleRate"/>.
        /// </summary>
        public static float ComputePropagationDelaySamples(float distanceMeters, int sampleRate) {
            if (distanceMeters <= 0f || sampleRate <= 0 || float.IsNaN(distanceMeters)) {
                return 0f;
            }
            float clampedDist = distanceMeters > MaxBufferedDistanceMeters ? MaxBufferedDistanceMeters : distanceMeters;
            return (clampedDist / SpeedOfSoundMetersPerSec) * sampleRate;
        }

        /// <summary>
        /// Ensures the power-of-2 ring buffer can hold <see cref="MaxBufferedDistanceMeters"/> at <paramref name="sampleRate"/>.
        /// </summary>
        public void Configure(int sampleRate) {
            int safeRate = Math.Max(8000, Math.Min(192000, sampleRate));
            int needed = (int)Math.Ceiling((MaxBufferedDistanceMeters / SpeedOfSoundMetersPerSec) * safeRate) + 16;
            int size = 1024;
            while (size < needed) {
                size <<= 1;
            }
            if (ringBuffer.Length != size || configuredSampleRate != safeRate) {
                ringBuffer = new float[size];
                ringMask = size - 1;
                writeIndex = 0;
                configuredSampleRate = safeRate;
                currentDelaySamples = 0f;
                initialized = false;
            }
        }

        /// <summary>
        /// Clears the delay line history without reallocating memory.
        /// </summary>
        public void Reset() {
            if (ringBuffer.Length > 0) {
                Array.Clear(ringBuffer, 0, ringBuffer.Length);
            }
            writeIndex = 0;
            currentDelaySamples = 0f;
            initialized = false;
        }

        /// <summary>
        /// Applies smooth fractional propagation delay in place to a mono source block (zero-GC).
        /// Uses 4-point cubic Lagrange interpolation for phase-accurate sub-sample time-of-flight.
        /// </summary>
        public void ProcessInPlace(float[] samples, int offset, int count, float distanceMeters, int sampleRate) {
            if (samples == null || count <= 0) {
                return;
            }
            if (configuredSampleRate != sampleRate || ringBuffer.Length == 0) {
                Configure(sampleRate);
            }

            float maxDelay = ringMask - 4;
            float targetDelay = ComputePropagationDelaySamples(distanceMeters, configuredSampleRate);
            if (targetDelay > maxDelay) {
                targetDelay = maxDelay;
            }
            if (targetDelay < 0f) {
                targetDelay = 0f;
            }

            if (!initialized) {
                currentDelaySamples = targetDelay;
                initialized = true;
            }

            // Bit-transparent bypass when delay is effectively zero (< 0.05 ms) and already settled
            if (targetDelay < 1f && currentDelaySamples < 1f) {
                for (int i = 0; i < count; i++) {
                    ringBuffer[writeIndex] = samples[offset + i];
                    writeIndex = (writeIndex + 1) & ringMask;
                }
                currentDelaySamples = 0f;
                return;
            }

            int mask = ringMask;
            float[] buf = ringBuffer;
            int w = writeIndex;
            float cur = currentDelaySamples;

            for (int i = 0; i < count; i++) {
                float input = samples[offset + i];
                buf[w] = input;

                float diff = targetDelay - cur;
                if (diff > MaxDelaySlewPerSample) {
                    cur += MaxDelaySlewPerSample;
                } else if (diff < -MaxDelaySlewPerSample) {
                    cur -= MaxDelaySlewPerSample;
                } else {
                    cur = targetDelay;
                }

                float readPos = w - cur;
                int idx0 = (int)Math.Floor(readPos);
                float frac = readPos - idx0;

                // 4-point cubic Lagrange interpolation around [idx0 - 1, idx0, idx0 + 1, idx0 + 2]
                float xm1 = buf[(idx0 - 1) & mask];
                float x0 = buf[idx0 & mask];
                float x1 = buf[(idx0 + 1) & mask];
                float x2 = buf[(idx0 + 2) & mask];

                float c0 = x0;
                float c1 = x1 - (1f / 3f) * xm1 - 0.5f * x0 - (1f / 6f) * x2;
                float c2 = 0.5f * (xm1 + x1) - x0;
                float c3 = (1f / 6f) * (x2 - xm1) + 0.5f * (x0 - x1);

                samples[offset + i] = ((c3 * frac + c2) * frac + c1) * frac + c0;
                w = (w + 1) & mask;
            }

            writeIndex = w;
            currentDelaySamples = cur;
        }
    }
}
