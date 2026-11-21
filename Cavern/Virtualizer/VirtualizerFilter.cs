using System;
using System.Collections.Generic;
using System.Numerics;

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
        /// Maximum impulse response tap length among active virtual channels.
        /// </summary>
        public static int ActiveMaxImpulseLength { get; private set; }

        /// <summary>
        /// True when the active virtualizer profile contains long measured room reflections
        /// (&gt; <see cref="MinimumPhaseHrir.MaxAnechoicTaps"/>, i.e. &gt; 1024 taps), automatically
        /// engaging the BRIR double-room bypass guard for synthetic ISM reflections.
        /// </summary>
        public static bool IsMeasuredBrirActive => ActiveMaxImpulseLength > MinimumPhaseHrir.MaxAnechoicTaps;

        /// <summary>
        /// Monotonically increasing version counter incremented whenever the active virtualizer profile changes.
        /// </summary>
        public static int ProfileVersion { get; private set; }

        /// <summary>
        /// Legacy artificial 7.5 ms center wall echo hack. Disabled by default so laboratory HRIR/BRIR profiles stay neutral.
        /// </summary>
        public static bool CenterWallEcho { get; set; }

        /// <summary>
        /// Enables real-time Allen &amp; Berkley shoebox ISM early reflections (Slot 5A).
        /// Disabled by default for zero-regression anechoic neutrality; automatically bypassed on measured BRIRs.
        /// </summary>
        public static bool IsmRoomEnabled { get; set; }

        /// <summary>
        /// Active room preset for Slot 5A ISM early reflections and physical room coordinate scaling.
        /// </summary>
        public static IsmRoomPreset IsmPreset { get; set; } = IsmRoomPreset.MasteringStudio;

        /// <summary>
        /// Active ISO 354 wall absorption material for Slot 5A ISM early reflections.
        /// </summary>
        public static IsmWallMaterial IsmWallMaterial { get; set; } = IsmWallMaterial.StudioBroadband;

        /// <summary>
        /// Reflection order for Slot 5A ISM early reflections (1 = 6 first-order, 2 = 24 first + second-order).
        /// </summary>
        public static int IsmReflectionOrder { get; set; } = 2;

        /// <summary>
        /// Linear physical reflection level multiplier (1.0 = exact physical level, clamped to 0..1.5).
        /// </summary>
        public static float IsmReflectionLevel { get; set; } = 1.0f;

        /// <summary>
        /// Enables pure-physics Duda &amp; Martens (1998) spherical-head near-field DVF for sources closer than 1.0 m.
        /// Strictly 0.00 dB unity bypass at &gt;= 1.0 m or when distance &gt;= <see cref="ReferenceDistance"/>.
        /// </summary>
        public static bool NearFieldCompensationEnabled { get; set; } = true;

        /// <summary>
        /// Additional BRIR guard flag mirrored from profile metadata (complements <see cref="IsMeasuredBrirActive"/>).
        /// </summary>
        public static bool ExternalBrirGuardActive { get; set; }

        /// <summary>
        /// Active physical shoebox geometry and Sabine/Eyring RT60 telemetry for UI/hub diagnostics.
        /// </summary>
        public static IsmRoomGeometry ActiveRoomGeometry { get; private set; } =
            new IsmRoomGeometry(IsmRoomPreset.MasteringStudio, IsmWallMaterial.StudioBroadband, new Vector3(10f, 7f, 10f));

        /// <summary>
        /// Live normalized listener position inside the room (<c>[-1, +1]</c>) fed by Slot 5 adapter.
        /// </summary>
        public static Vector3 LiveListenerNormalizedPosition { get; set; } = Vector3.Zero;

        /// <summary>
        /// Live listener Euler rotation in degrees (Pitch X, Yaw Y, Roll Z) fed by Slot 4/5 for head-tracked ISM reflections.
        /// </summary>
        public static Vector3 LiveListenerRotationDegrees { get; set; } = Vector3.Zero;

        /// <summary>
        /// True when any virtual channel actively engaged Duda &amp; Martens near-field DVF on the last processed block.
        /// </summary>
        public static bool LastBlockNearFieldActive { get; private set; }

        /// <summary>
        /// Updates all Slot 5A physical externalization parameters atomically and refreshes <see cref="ActiveRoomGeometry"/>
        /// and <see cref="AirAbsorptionFilter.PhysicalRoomHalfSize"/>.
        /// </summary>
        public static void ConfigureExternalization(
            bool ismEnabled,
            IsmRoomPreset preset,
            IsmWallMaterial wallMaterial,
            int reflectionOrder,
            float reflectionLevel,
            bool nearFieldEnabled,
            bool airAbsorptionEnabled,
            float airTempCelsius,
            float airRelativeHumidityPct,
            Vector3 customEnvironmentHalfSize,
            bool externalBrirGuard) {
            IsmRoomEnabled = ismEnabled;
            IsmPreset = preset;
            IsmWallMaterial = wallMaterial;
            IsmReflectionOrder = reflectionOrder <= 1 ? 1 : 2;
            IsmReflectionLevel = Math.Clamp(reflectionLevel, 0f, 1.5f);
            NearFieldCompensationEnabled = nearFieldEnabled;
            AirAbsorptionFilter.GlobalEnabled = airAbsorptionEnabled;
            AirAbsorptionFilter.GlobalTemperatureCelsius = airTempCelsius;
            AirAbsorptionFilter.GlobalRelativeHumidityPct = airRelativeHumidityPct;
            ExternalBrirGuardActive = externalBrirGuard;

            IsmRoomGeometry geom = new IsmRoomGeometry(preset, wallMaterial, customEnvironmentHalfSize);
            ActiveRoomGeometry = geom;
            AirAbsorptionFilter.PhysicalRoomHalfSize = geom.HalfDimensionsMeters;
        }

        /// <summary>
        /// Updates per-virtual-channel minimum physical source distances (meters) without heap allocations.
        /// Called on the audio thread immediately prior to <see cref="Listener.Render(int)"/>.
        /// </summary>
        public static void SetLiveChannelMinDistances(float[] distancesMeters, int count) {
            int limit = Math.Min(liveChannelMinDistances.Length, Math.Max(0, count));
            for (int i = 0; i < limit; i++) {
                liveChannelMinDistances[i] = distancesMeters != null && i < distancesMeters.Length
                    ? distancesMeters[i]
                    : float.PositiveInfinity;
            }
            for (int i = limit; i < liveChannelMinDistances.Length; i++) {
                liveChannelMinDistances[i] = float.PositiveInfinity;
            }
        }

        static readonly float[] liveChannelMinDistances = InitInfinityDistances(64);

        static float[] InitInfinityDistances(int count) {
            float[] arr = new float[count];
            for (int i = 0; i < count; i++) {
                arr[i] = float.PositiveInfinity;
            }
            return arr;
        }

        /// <summary>
        /// Number of virtual channels.
        /// </summary>
        public static int VirtualChannels => SpatialChannels.Length;

        /// <summary>
        /// Active virtualized channels and their corresponding filters.
        /// </summary>
        static VirtualChannel[] SpatialChannels {
            get {
                if (spatialChannels == null) {
                    spatialChannels = GetDefaultHRIRSet();
                    ActiveMaxImpulseLength = ComputeMaxImpulseLength(spatialChannels);
                }
                return spatialChannels;
            }
            set {
                spatialChannels = value;
                ActiveMaxImpulseLength = ComputeMaxImpulseLength(value);
            }
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
        /// Per-channel pure-physics Duda &amp; Martens (1998) near-field DVF filters (zero GC).
        /// </summary>
        NearFieldHeadFilter[] nearFieldFilters;

        /// <summary>
        /// Cached cosine of angle between each virtual channel and the right ear axis (+X).
        /// </summary>
        float[] channelCosRight;

        /// <summary>
        /// Real-time Allen &amp; Berkley shoebox ISM early-reflection engine owned by this filter instance.
        /// </summary>
        readonly IsmRoomEngine ismRoomEngine = new IsmRoomEngine();

        /// <summary>
        /// Tracks whether ISM was active on the previous block so disabling cleanly flushes delay lines.
        /// </summary>
        bool wasIsmActive;

        /// <summary>
        /// Cached speed of sound for the current audio block.
        /// </summary>
        double blockSpeedOfSound = 343.21;

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
        /// Computes the maximum impulse response length across all active virtual channels.
        /// </summary>
        static int ComputeMaxImpulseLength(VirtualChannel[] channels) {
            if (channels == null) {
                return 0;
            }
            int maxLen = 0;
            for (int i = 0; i < channels.Length; i++) {
                if (channels[i].active && channels[i].Filter != null) {
                    int len = channels[i].Filter.ImpulseLength;
                    if (len > maxLen) {
                        maxLen = len;
                    }
                }
            }
            return maxLen;
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
            nearFieldFilters = new NearFieldHeadFilter[activeSet.Length];
            channelCosRight = new float[activeSet.Length];
            for (int channel = 0; channel < activeSet.Length; channel++) {
                originalSplit[channel] = Array.Empty<float>();
                lowSplit[channel] = Array.Empty<float>();
                leftSplit[channel] = Array.Empty<float>();
                rightSplit[channel] = Array.Empty<float>();
                nearFieldFilters[channel] = new NearFieldHeadFilter();
                float xRad = activeSet[channel].x * VectorExtensions.Deg2Rad;
                float yRad = activeSet[channel].y * VectorExtensions.Deg2Rad;
                channelCosRight[channel] = MathF.Sin(yRad) * MathF.Cos(xRad);
            }
            ismRoomEngine.Reset();
            wasIsmActive = false;
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

            // Slot 5A: Pure-physics Allen & Berkley shoebox ISM early reflections (pre-HRIR, zero extra FFTs).
            // Automatically bypassed whenever a measured BRIR (> 1024 taps or flagged BRIR) is active.
            bool ismActive = IsmRoomEnabled && !IsMeasuredBrirActive && !ExternalBrirGuardActive;
            if (ismActive) {
                ismRoomEngine.Configure(
                    IsmPreset,
                    IsmWallMaterial,
                    IsmReflectionOrder,
                    IsmReflectionLevel,
                    Listener.EnvironmentSize,
                    AirAbsorptionFilter.GlobalEnabled,
                    AirAbsorptionFilter.GlobalTemperatureCelsius,
                    AirAbsorptionFilter.GlobalRelativeHumidityPct);
                ismRoomEngine.ProcessPreHrir(
                    originalSplit,
                    activeSet,
                    blockSize,
                    sampleRate,
                    LiveListenerNormalizedPosition,
                    LiveListenerRotationDegrees);
                wasIsmActive = true;
            } else if (wasIsmActive) {
                ismRoomEngine.Reset();
                wasIsmActive = false;
            }

            if (CenterWallEcho && center != unassigned && left != unassigned && right != unassigned) {
                Array.Copy(originalSplit[center], delayedCenter, blockSize);
                centerDelay.Process(delayedCenter); // Add 7.5 ms delay
                WaveformUtils.Gain(delayedCenter, .1f); // -20 dB gain
                WaveformUtils.Mix(delayedCenter, originalSplit[left]);
                WaveformUtils.Mix(delayedCenter, originalSplit[right]);
            }

            blockSpeedOfSound = AirAbsorptionFilter.ComputeSpeedOfSound(AirAbsorptionFilter.GlobalTemperatureCelsius);
            processor.ForUnchecked(0, channels);

            bool anyNearFieldActive = false;
            if (nearFieldFilters != null) {
                for (int ch = 0; ch < channels; ch++) {
                    if (nearFieldFilters[ch].IsActive) {
                        anyNearFieldActive = true;
                        break;
                    }
                }
            }
            LastBlockNearFieldActive = anyNearFieldActive;

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
        /// Split (when crossover is enabled) and convolve a single virtual channel by ID,
        /// followed by pure-physics Duda &amp; Martens (1998) near-field DVF when a source approaches &lt; 1.0 m.
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

            if (nearFieldFilters != null && channel < nearFieldFilters.Length) {
                float minDist = NearFieldCompensationEnabled && channel < liveChannelMinDistances.Length
                    ? liveChannelMinDistances[channel]
                    : float.PositiveInfinity;
                nearFieldFilters[channel].ProcessStereo(
                    leftSplit[channel],
                    rightSplit[channel],
                    blockSize,
                    minDist,
                    ReferenceDistance,
                    channelCosRight[channel],
                    FilterSampleRate,
                    blockSpeedOfSound);
            }
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
