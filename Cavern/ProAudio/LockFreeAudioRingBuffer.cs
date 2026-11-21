using System;
using System.Threading;

namespace Cavern.ProAudio {
    /// <summary>
    /// Zero-GC, lock-free Single-Producer Single-Consumer (SPSC) circular audio sample buffer
    /// using power-of-two masking and acquire/release memory barriers (<see cref="Volatile"/>).
    /// Safe for real-time full-duplex audio threads (Slot 1 USB/AAudio input -> Slot 3/4 playback).
    /// </summary>
    public sealed class LockFreeAudioRingBuffer {
        readonly float[] buffer;
        readonly int capacityMask;
        readonly int capacity;
        readonly int channels;

        int writeIndex;
        int readIndex;
        long totalFramesWritten;
        long totalFramesRead;
        long overrunCount;
        long underrunCount;

        /// <summary>
        /// Interleaved channel count per frame (1 = mono, 2 = stereo).
        /// </summary>
        public int Channels {
            get { return channels; }
        }

        /// <summary>
        /// Maximum number of interleaved frames the ring buffer can hold without overwriting unread data.
        /// </summary>
        public int CapacityFrames {
            get { return (capacity / channels) - 1; }
        }

        /// <summary>
        /// Total frames written since creation or last <see cref="Reset"/>.
        /// </summary>
        public long TotalFramesWritten {
            get { return Volatile.Read(ref totalFramesWritten); }
        }

        /// <summary>
        /// Total frames read since creation or last <see cref="Reset"/>.
        /// </summary>
        public long TotalFramesRead {
            get { return Volatile.Read(ref totalFramesRead); }
        }

        /// <summary>
        /// Number of write calls that dropped frames because the consumer lagged behind.
        /// </summary>
        public long OverrunCount {
            get { return Volatile.Read(ref overrunCount); }
        }

        /// <summary>
        /// Number of read calls that returned fewer frames than requested because the producer lagged behind.
        /// </summary>
        public long UnderrunCount {
            get { return Volatile.Read(ref underrunCount); }
        }

        /// <summary>
        /// Number of complete frames currently available to read.
        /// </summary>
        public int AvailableFramesToRead {
            get {
                int w = Volatile.Read(ref writeIndex);
                int r = Volatile.Read(ref readIndex);
                int samples = (w - r) & capacityMask;
                return samples / channels;
            }
        }

        /// <summary>
        /// Number of complete frames currently free to write.
        /// </summary>
        public int AvailableFramesToWrite {
            get {
                int w = Volatile.Read(ref writeIndex);
                int r = Volatile.Read(ref readIndex);
                int used = (w - r) & capacityMask;
                int free = capacity - 1 - used;
                return free / channels;
            }
        }

        /// <summary>
        /// Current fill ratio in [0; 1].
        /// </summary>
        public float FillRatio {
            get {
                int maxFrames = CapacityFrames;
                if (maxFrames <= 0) {
                    return 0f;
                }
                return AvailableFramesToRead / (float)maxFrames;
            }
        }

        /// <summary>
        /// Creates a lock-free SPSC ring buffer holding at least <paramref name="minCapacityFrames"/> frames
        /// of <paramref name="channelCount"/> interleaved float32 samples.
        /// </summary>
        public LockFreeAudioRingBuffer(int minCapacityFrames, int channelCount) {
            if (channelCount < 1 || channelCount > 8) {
                throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be between 1 and 8.");
            }
            if (minCapacityFrames < 64) {
                minCapacityFrames = 64;
            }
            channels = channelCount;

            int minSamples = (minCapacityFrames + 1) * channelCount;
            int pow2 = 256;
            while (pow2 < minSamples && pow2 < (1 << 24)) {
                pow2 <<= 1;
            }
            capacity = pow2;
            capacityMask = pow2 - 1;
            buffer = new float[capacity];
        }

        /// <summary>
        /// Resets read/write pointers to empty state. Call only when producer and consumer are quiesced or synchronized.
        /// </summary>
        public void Reset() {
            Volatile.Write(ref readIndex, 0);
            Volatile.Write(ref writeIndex, 0);
            Volatile.Write(ref totalFramesWritten, 0);
            Volatile.Write(ref totalFramesRead, 0);
            Volatile.Write(ref overrunCount, 0);
            Volatile.Write(ref underrunCount, 0);
            Array.Clear(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Writes up to <paramref name="frameCount"/> interleaved frames from <paramref name="source"/> starting at <paramref name="sourceOffset"/>.
        /// Never blocks or allocates. Increments <see cref="OverrunCount"/> if fewer frames fit.
        /// </summary>
        public int Write(float[] source, int sourceOffset, int frameCount) {
            if (source == null || frameCount <= 0 || sourceOffset < 0) {
                return 0;
            }
            int maxSrcFrames = (source.Length - sourceOffset) / channels;
            if (frameCount > maxSrcFrames) {
                frameCount = maxSrcFrames;
            }
            if (frameCount <= 0) {
                return 0;
            }

            int w = Volatile.Read(ref writeIndex);
            int r = Volatile.Read(ref readIndex);
            int used = (w - r) & capacityMask;
            int freeSamples = capacity - 1 - used;
            int freeFrames = freeSamples / channels;

            if (frameCount > freeFrames) {
                Interlocked.Increment(ref overrunCount);
                frameCount = freeFrames;
                if (frameCount <= 0) {
                    return 0;
                }
            }

            int sampleCount = frameCount * channels;
            int firstPart = capacity - w;
            if (firstPart > sampleCount) {
                firstPart = sampleCount;
            }

            Array.Copy(source, sourceOffset, buffer, w, firstPart);
            int secondPart = sampleCount - firstPart;
            if (secondPart > 0) {
                Array.Copy(source, sourceOffset + firstPart, buffer, 0, secondPart);
            }

            int nextW = (w + sampleCount) & capacityMask;
            Volatile.Write(ref writeIndex, nextW);
            Interlocked.Add(ref totalFramesWritten, frameCount);
            return frameCount;
        }

        /// <summary>
        /// Reads up to <paramref name="frameCount"/> interleaved frames into <paramref name="destination"/> starting at <paramref name="destinationOffset"/>.
        /// Never blocks or allocates. If <paramref name="zeroFillMissing"/> is true, any missing trailing frames are zeroed.
        /// </summary>
        public int Read(float[] destination, int destinationOffset, int frameCount, bool zeroFillMissing = false) {
            if (destination == null || frameCount <= 0 || destinationOffset < 0) {
                return 0;
            }
            int maxDstFrames = (destination.Length - destinationOffset) / channels;
            if (frameCount > maxDstFrames) {
                frameCount = maxDstFrames;
            }
            if (frameCount <= 0) {
                return 0;
            }

            int w = Volatile.Read(ref writeIndex);
            int r = Volatile.Read(ref readIndex);
            int availSamples = (w - r) & capacityMask;
            int availFrames = availSamples / channels;

            int toReadFrames = frameCount;
            if (toReadFrames > availFrames) {
                Interlocked.Increment(ref underrunCount);
                toReadFrames = availFrames;
            }

            if (toReadFrames > 0) {
                int sampleCount = toReadFrames * channels;
                int firstPart = capacity - r;
                if (firstPart > sampleCount) {
                    firstPart = sampleCount;
                }

                Array.Copy(buffer, r, destination, destinationOffset, firstPart);
                int secondPart = sampleCount - firstPart;
                if (secondPart > 0) {
                    Array.Copy(buffer, 0, destination, destinationOffset + firstPart, secondPart);
                }

                int nextR = (r + sampleCount) & capacityMask;
                Volatile.Write(ref readIndex, nextR);
                Interlocked.Add(ref totalFramesRead, toReadFrames);
            }

            if (zeroFillMissing && toReadFrames < frameCount) {
                int zeroOffset = destinationOffset + toReadFrames * channels;
                int zeroSamples = (frameCount - toReadFrames) * channels;
                Array.Clear(destination, zeroOffset, zeroSamples);
            }

            return toReadFrames;
        }

        /// <summary>
        /// Discards up to <paramref name="frameCount"/> oldest frames from the consumer end without copying.
        /// Useful to drain accumulated startup latency on full-duplex streams.
        /// </summary>
        public int Skip(int frameCount) {
            if (frameCount <= 0) {
                return 0;
            }
            int w = Volatile.Read(ref writeIndex);
            int r = Volatile.Read(ref readIndex);
            int availFrames = ((w - r) & capacityMask) / channels;
            if (frameCount > availFrames) {
                frameCount = availFrames;
            }
            if (frameCount <= 0) {
                return 0;
            }
            int nextR = (r + frameCount * channels) & capacityMask;
            Volatile.Write(ref readIndex, nextR);
            Interlocked.Add(ref totalFramesRead, frameCount);
            return frameCount;
        }
    }
}
