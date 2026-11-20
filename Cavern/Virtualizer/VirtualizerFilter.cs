using System;
using System.Collections.Generic;

using Cavern.Filters;
using Cavern.Utilities;
using Cavern.Utilities.Threading;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Convolution filters for each ear and virtual channel to simulate a spatial environment.
    /// </summary>
    public partial class VirtualizerFilter {
        /// <summary>
        /// Sample rate of the recorded HRIR filters. Only this system sample rate is allowed for virtualization.
        /// </summary>
        public static int FilterSampleRate { get; private set; } = defaultSampleRate;

        /// <summary>
        /// The active filter set's impulses were measured at this distance, meters.
        /// </summary>
        public static float ReferenceDistance { get; private set; } = defaultDistance;

        /// <summary>
        /// Monotonically increasing version counter incremented whenever the active virtualizer profile changes.
        /// </summary>
        public static int ProfileVersion { get; private set; }

        /// <summary>
        /// Legacy artificial 7.5 ms center wall echo hack. Disabled by default so laboratory HRIR/BRIR profiles stay neutral.
        /// </summary>
        public static bool CenterWallEcho { get; set; }

        /// <summary>
        /// Number of virtual channels.
        /// </summary>
        public static int VirtualChannels => SpatialChannels.Length;

        /// <summary>
        /// Active virtualized channels and their corresponding filters.
        /// </summary>
        static VirtualChannel[] SpatialChannels {
            get => spatialChannels ??= GetDefaultHRIRSet();
            set => spatialChannels = value;
        }
        static VirtualChannel[] spatialChannels;

        /// <summary>
        /// Snapshot of the channel array bound to this filter instance during <see cref="Process"/>.
        /// </summary>
        VirtualChannel[] boundChannels;

        /// <summary>
        /// Profile version applied in <see cref="SetLayout"/>.
        /// </summary>
        int appliedVersion = -1;

        /// <summary>
        /// Cache of each output channel.
        /// </summary>
        float[][] originalSplit;

        /// <summary>
        /// Cache of each crossover lowpass output channel (separate from <see cref="originalSplit"/> to avoid aliasing).
        /// </summary>
        float[][] lowSplit;

        /// <summary>
        /// Cache of each output channel for one ear.
        /// </summary>
        float[][] leftSplit, rightSplit;

        /// <summary>
        /// Length of split arrays.
        /// </summary>
        int blockSize;

        /// <summary>
        /// Cached channel IDs for optional legacy center wall echo.
        /// </summary>
        int left = unassigned, right = unassigned, center = unassigned;

        /// <summary>
        /// Optional legacy 7.5 ms -20 dB delay of center into fronts (inactive unless <see cref="CenterWallEcho"/> is true).
        /// </summary>
        Delay centerDelay;

        /// <summary>
        /// Delayed center channel signal for optional legacy center wall echo.
        /// </summary>
        float[] delayedCenter;

        /// <summary>
        /// Parallel executor of channel filtering.
        /// </summary>
        readonly Parallelizer processor;

        /// <summary>
        /// Convolution filters for each ear and virtual channel to simulate a spatial environment.
        /// </summary>
        public VirtualizerFilter() => processor = new Parallelizer(ProcessChannel);

        /// <summary>
        /// Apply a new set of HRIR filters. The reference distance of the sound sources from the subject will be 1 meter.
        /// </summary>
        public static void Override(VirtualChannel[] channels, int sampleRate) => Override(channels, sampleRate, 1);

        /// <summary>
        /// Apply a new set of HRIR filters, and specify what distance from the subject was the speaker when they were created.
        /// Ensures a dedicated LFE virtual channel is present at the end of the array for symmetric stereo LFE downmix.
        /// </summary>
        public static void Override(VirtualChannel[] channels, int sampleRate, float referenceDistance) {
            SpatialChannels = EnsureStereoLfeChannel(channels);
            FilterSampleRate = sampleRate;
            ReferenceDistance = referenceDistance;
            ProfileVersion++;
        }

        /// <summary>
        /// Restore the default HRIR filter set.
        /// </summary>
        public static void Reset() => Reset(false);

        /// <summary>
        /// Restore the default HRIR filter set, optionally converted to mathematical Minimum-Phase + ITD.
        /// </summary>
        public static void Reset(bool minimumPhase) {
            VirtualChannel[] defaults;
            lock (resetLock) {
                VirtualChannel.applyMinimumPhaseOnSubpar = minimumPhase;
                try {
                    defaults = GetDefaultHRIRSet();
                } finally {
                    VirtualChannel.applyMinimumPhaseOnSubpar = false;
                }
            }
            SpatialChannels = defaults;
            FilterSampleRate = defaultSampleRate;
            ReferenceDistance = defaultDistance;
            ProfileVersion++;
        }

        static readonly object resetLock = new object();

        /// <summary>
        /// Change the virtualized layout based on <see cref="Listener.Channels"/>, with added virtualized heights. Ground channels
        /// (including physical LFE) will not be virtualized. This can be used for conveying height information on legacy surround systems.
        /// </summary>
        public static void SetupForSpeakers() {
            List<VirtualChannel> channels = new List<VirtualChannel>();

            // Add ground channels (non-virtualized, preserving physical LFE flag)
            Channel[] source = Listener.Channels;
            for (int i = 0; i < source.Length; i++) {
                if (source[i].X == 0 || source[i].LFE) {
                    channels.Add(new VirtualChannel(source[i].X, source[i].Y, source[i].LFE, false));
                } else {
                    throw new NonGroundChannelPresentException();
                }
            }

            // Add height channels (virtualized)
            VirtualChannel[] current = SpatialChannels;
            for (int i = 0; i < current.Length; i++) {
                if (current[i].x != 0 && !current[i].isLfe) {
                    channels.Add(current[i]);
                }
            }

            SpatialChannels = channels.ToArray();
            ProfileVersion++;
        }

        /// <summary>
        /// Set up virtual channel set for the virtualization filters.
        /// </summary>
        public void SetLayout() {
            VirtualChannel[] activeSet = SpatialChannels;
            int version = ProfileVersion;
            bool setAgain = centerDelay == null || appliedVersion != version || boundChannels != activeSet;
            if (!setAgain && Listener.Channels.Length == activeSet.Length) {
                for (int channel = 0; channel < activeSet.Length; channel++) {
                    if (Listener.Channels[channel].X != activeSet[channel].x ||
                        Listener.Channels[channel].Y != activeSet[channel].y ||
                        Listener.Channels[channel].LFE != activeSet[channel].isLfe) {
                        setAgain = true;
                        break;
                    }
                }
            } else {
                setAgain = true;
            }
            if (!setAgain) {
                return;
            }

            boundChannels = activeSet;
            appliedVersion = version;
            centerDelay = new Delay(.0075f, FilterSampleRate);
            left = unassigned;
            right = unassigned;
            center = unassigned;

            Channel[] newChannels = new Channel[activeSet.Length];
            for (int channel = 0; channel < activeSet.Length; channel++) {
                newChannels[channel] = new Channel(activeSet[channel].x, activeSet[channel].y, activeSet[channel].isLfe);
                if (!activeSet[channel].isLfe && activeSet[channel].x == 0) {
                    if (activeSet[channel].y == 0) {
                        center = channel;
                    } else if (activeSet[channel].y == -45) {
                        left = channel;
                    } else if (activeSet[channel].y == 45) {
                        right = channel;
                    }
                }
            }
            Listener.ReplaceChannels(newChannels);
            originalSplit = new float[activeSet.Length][];
            lowSplit = new float[activeSet.Length][];
            leftSplit = new float[activeSet.Length][];
            rightSplit = new float[activeSet.Length][];
            for (int channel = 0; channel < activeSet.Length; channel++) {
                originalSplit[channel] = Array.Empty<float>();
                lowSplit[channel] = Array.Empty<float>();
                leftSplit[channel] = Array.Empty<float>();
                rightSplit[channel] = Array.Empty<float>();
            }
        }

        /// <summary>
        /// Apply the virtualizer on the <see cref="Listener"/>'s output,
        /// if the configuration matches the virtualization layout and filter sample rate.
        /// </summary>
        public void Process(float[] output, int sampleRate) {
            if (appliedVersion != ProfileVersion || boundChannels == null) {
                SetLayout();
            }
            VirtualChannel[] activeSet = boundChannels;
            int channels = Listener.Channels.Length;
            if (originalSplit == null || activeSet == null || channels != activeSet.Length || sampleRate != FilterSampleRate) {
                return;
            }
            blockSize = output.Length / channels;
            if (blockSize <= 0) {
                return;
            }

            if (originalSplit[0].Length != blockSize) {
                for (int channel = 0; channel < channels; channel++) {
                    originalSplit[channel] = new float[blockSize];
                    lowSplit[channel] = new float[blockSize];
                    leftSplit[channel] = new float[blockSize];
                    rightSplit[channel] = new float[blockSize];
                }
                delayedCenter = new float[blockSize];
            }

            for (int sample = 0, outSample = 0; sample < blockSize; sample++) {
                for (int channel = 0; channel < channels; channel++, outSample++) {
                    originalSplit[channel][sample] = output[outSample];
                }
            }

            if (CenterWallEcho && center != unassigned && left != unassigned && right != unassigned) {
                Array.Copy(originalSplit[center], delayedCenter, blockSize);
                centerDelay.Process(delayedCenter); // Add 7.5 ms delay
                WaveformUtils.Gain(delayedCenter, .1f); // -20 dB gain
                WaveformUtils.Mix(delayedCenter, originalSplit[left]);
                WaveformUtils.Mix(delayedCenter, originalSplit[right]);
            }
            processor.ForUnchecked(0, channels);

            // Stereo downmix (or speaker passthrough for non-virtualized ground/sub channels)
            output.Clear();
            for (int channel = 0; channel < channels; channel++) {
                VirtualChannel ch = activeSet[channel];
                if (ch.active) {
                    float[] leftSamples = leftSplit[channel],
                        rightSamples = rightSplit[channel];
                    if (ch.Crossover != null) {
                        float[] lowSamples = lowSplit[channel];
                        for (int sample = 0; sample < blockSize; sample++) {
                            float unspatialized = lowSamples[sample] * VirtualChannel.minus3dB;
                            int outIdx = sample * channels;
                            output[outIdx] += leftSamples[sample] + unspatialized;
                            output[outIdx + 1] += rightSamples[sample] + unspatialized;
                        }
                    } else {
                        for (int sample = 0; sample < blockSize; sample++) {
                            int outIdx = sample * channels;
                            output[outIdx] += leftSamples[sample];
                            output[outIdx + 1] += rightSamples[sample];
                        }
                    }
                } else if (ch.isLfe && ch.mixLfeToStereo) {
                    // Dedicated LFE path: already 120 Hz lowpassed and scaled by Listener.LFEVolume; equal-power -3 dB to L/R
                    float[] lfeSamples = originalSplit[channel];
                    for (int sample = 0; sample < blockSize; sample++) {
                        float lfe = lfeSamples[sample] * VirtualChannel.minus3dB;
                        int outIdx = sample * channels;
                        output[outIdx] += lfe;
                        output[outIdx + 1] += lfe;
                    }
                } else {
                    WaveformUtils.Mix(originalSplit[channel], output, channel, channels, 1);
                }
            }
        }

        /// <summary>
        /// Split (when crossover is enabled) and convolve a single virtual channel by ID.
        /// </summary>
        void ProcessChannel(int channel) {
            VirtualChannel ch = boundChannels[channel];
            if (!ch.active) {
                return;
            }

            if (ch.Crossover != null) {
                ch.Crossover.Process(originalSplit[channel]);
                Array.Copy(ch.Crossover.LowOutput, lowSplit[channel], blockSize);
                if (ch.LowDelay != null) {
                    ch.LowDelay.Process(lowSplit[channel]);
                }
                Array.Copy(ch.Crossover.HighOutput, leftSplit[channel], blockSize);
            } else {
                Array.Copy(originalSplit[channel], leftSplit[channel], blockSize);
            }

            ch.Filter.Process(leftSplit[channel], rightSplit[channel]);
        }

        /// <summary>
        /// Appends a dedicated stereo-downmixed LFE channel at the end of a virtualizer set if none is present.
        /// Keeping LFE as the final element preserves <see cref="Listener.IsSymmetric"/> via <see cref="Channels.SymmetryCalculator"/>.
        /// </summary>
        static VirtualChannel[] EnsureStereoLfeChannel(VirtualChannel[] channels) {
            if (channels == null || channels.Length == 0) {
                return channels;
            }
            for (int i = 0; i < channels.Length; i++) {
                if (channels[i].isLfe) {
                    return channels;
                }
            }
            VirtualChannel[] withLfe = new VirtualChannel[channels.Length + 1];
            Array.Copy(channels, withLfe, channels.Length);
            withLfe[channels.Length] = VirtualChannel.CreateLFE(true);
            return withLfe;
        }

        /// <summary>
        /// Marker instead of a channel ID when a channel was not set.
        /// </summary>
        const int unassigned = -1;
    }
}
