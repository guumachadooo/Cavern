using System;
using System.Collections.Generic;
using System.Numerics;

using Cavern.Filters;
using Cavern.Utilities;
using Cavern.Utilities.Threading;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Zero-GC Pre-HRTF Virtual Speaker Bus processor hook (used by W09 Tier 1 Lossless-Guided Spatial Restoration).
    /// Runs directly on the de-interleaved virtual speaker feeds BEFORE ISM room reflections and HRIR convolution,
    /// preserving JOC phase cancellation and feeding restored HF harmonics into pinna/room cues.
    /// </summary>
    public interface IPreHrtfBusProcessor {
        /// <summary>
        /// Processes <paramref name="blockSize"/> samples across all <paramref name="channelCount"/> virtual speaker
        /// channels in place prior to ISM early reflections and HRIR convolution.
        /// </summary>
        void ProcessPreHrtfBus(float[][] virtualChannels, VirtualChannel[] channelLayout, int channelCount, int blockSize, int sampleRate);
    }

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
        /// Optional Pre-HRTF Virtual Speaker Bus processor invoked right before ISM room reflections and HRIR convolution
        /// (W09 Tier 1 LGSR spectral fusion preserving JOC cancellation). Set to null to bypass with zero overhead.
        /// </summary>
        public static IPreHrtfBusProcessor PreHrtfBusProcessor { get; set; }

        /// <summary>
        /// Captures all static virtualizer configuration (<c>SpatialChannels</c>, <c>FilterSampleRate</c>,
        /// <c>ReferenceDistance</c>, <c>PreHrtfBusProcessor</c>, and ISM/externalization room settings) and
        /// restores them on <see cref="IDisposable.Dispose"/> so offline Studio exports never collide with live playback.
        /// </summary>
        public static IDisposable BeginStaticStateScope() {
            lock (staticScopeLock) {
                return new StaticStateScope();
            }
        }

        static readonly object staticScopeLock = new object();

        sealed class StaticStateScope : IDisposable {
            readonly VirtualChannel[] savedSpatialChannels;
            readonly int savedFilterSampleRate;
            readonly float savedReferenceDistance;
            readonly IPreHrtfBusProcessor savedPreHrtfBusProcessor;
            readonly bool savedCenterWallEcho;
            readonly bool savedIsmRoomEnabled;
            readonly IsmRoomPreset savedIsmPreset;
            readonly IsmWallMaterial savedIsmWallMaterial;
            readonly int savedIsmReflectionOrder;
            readonly float savedIsmReflectionLevel;
            readonly bool savedNearFieldEnabled;
            readonly bool savedExternalBrirGuard;
            readonly IsmRoomGeometry savedActiveRoomGeometry;
            readonly Vector3 savedLiveListenerNormalizedPosition;
            readonly Vector3 savedLiveListenerRotationDegrees;
            readonly bool savedAirAbsorptionEnabled;
            readonly float savedAirAbsorptionTemp;
            readonly float savedAirAbsorptionRh;
            readonly Vector3 savedAirAbsorptionHalfSize;
            readonly float[] savedLiveChannelMinDistances;
            bool disposed;

            public StaticStateScope() {
                savedSpatialChannels = spatialChannels;
                savedFilterSampleRate = FilterSampleRate;
                savedReferenceDistance = ReferenceDistance;
                savedPreHrtfBusProcessor = PreHrtfBusProcessor;
                savedCenterWallEcho = CenterWallEcho;
                savedIsmRoomEnabled = IsmRoomEnabled;
                savedIsmPreset = IsmPreset;
                savedIsmWallMaterial = IsmWallMaterial;
                savedIsmReflectionOrder = IsmReflectionOrder;
                savedIsmReflectionLevel = IsmReflectionLevel;
                savedNearFieldEnabled = NearFieldCompensationEnabled;
                savedExternalBrirGuard = ExternalBrirGuardActive;
                savedActiveRoomGeometry = ActiveRoomGeometry;
                savedLiveListenerNormalizedPosition = LiveListenerNormalizedPosition;
                savedLiveListenerRotationDegrees = LiveListenerRotationDegrees;
                savedAirAbsorptionEnabled = AirAbsorptionFilter.GlobalEnabled;
                savedAirAbsorptionTemp = AirAbsorptionFilter.GlobalTemperatureCelsius;
                savedAirAbsorptionRh = AirAbsorptionFilter.GlobalRelativeHumidityPct;
                savedAirAbsorptionHalfSize = AirAbsorptionFilter.PhysicalRoomHalfSize;
                savedLiveChannelMinDistances = (float[])liveChannelMinDistances.Clone();
            }

            public void Dispose() {
                lock (staticScopeLock) {
                    if (disposed) {
                        return;
                    }
                    disposed = true;

                    bool profileChanged = !ReferenceEquals(spatialChannels, savedSpatialChannels) ||
                        FilterSampleRate != savedFilterSampleRate ||
                        ReferenceDistance != savedReferenceDistance;

                    spatialChannels = savedSpatialChannels;
                    ActiveMaxImpulseLength = ComputeMaxImpulseLength(savedSpatialChannels);
                    FilterSampleRate = savedFilterSampleRate;
                    ReferenceDistance = savedReferenceDistance;
                    PreHrtfBusProcessor = savedPreHrtfBusProcessor;
                    CenterWallEcho = savedCenterWallEcho;
                    IsmRoomEnabled = savedIsmRoomEnabled;
                    IsmPreset = savedIsmPreset;
                    IsmWallMaterial = savedIsmWallMaterial;
                    IsmReflectionOrder = savedIsmReflectionOrder;
                    IsmReflectionLevel = savedIsmReflectionLevel;
                    NearFieldCompensationEnabled = savedNearFieldEnabled;
                    ExternalBrirGuardActive = savedExternalBrirGuard;
                    ActiveRoomGeometry = savedActiveRoomGeometry;
                    LiveListenerNormalizedPosition = savedLiveListenerNormalizedPosition;
                    LiveListenerRotationDegrees = savedLiveListenerRotationDegrees;
                    AirAbsorptionFilter.GlobalEnabled = savedAirAbsorptionEnabled;
                    AirAbsorptionFilter.GlobalTemperatureCelsius = savedAirAbsorptionTemp;
                    AirAbsorptionFilter.GlobalRelativeHumidityPct = savedAirAbsorptionRh;
                    AirAbsorptionFilter.PhysicalRoomHalfSize = savedAirAbsorptionHalfSize;
                    Array.Copy(savedLiveChannelMinDistances, liveChannelMinDistances, liveChannelMinDistances.Length);

                    if (profileChanged) {
                        ProfileVersion++;
                    }
                }
            }
        }

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

        // Pre-allocated rational Catmull-Rom rate conversion state used when Listener.SampleRate != FilterSampleRate
        int rateSrcRate;
        int rateFltRate;
        int rateInBlockSize;
        int rateChannels;
        int rateBaseFltBlockSize;
        int rateStepIn;
        int rateModIn;
        ChannelRateConverter[] inRateConverters;
        ChannelRateConverter outRateConverterL;
        ChannelRateConverter outRateConverterR;
        float[] outFifoL;
        float[] outFifoR;
        int outFifoCapacity;
        int fifoReadL, fifoWriteL, fifoCountL;
        int fifoReadR, fifoWriteR, fifoCountR;
        RateSlotBuffers[] rateSlots;

        sealed class RateSlotBuffers {
            public float[][] Original;
            public float[][] Low;
            public float[][] Left;
            public float[][] Right;
            public float[] DelayedCenter;
            public float[] StereoL;
            public float[] StereoR;
        }

        sealed class ChannelRateConverter {
            float s0, s1, s2, s3;
            long accum;
            int stepNum, modDen;

            public void Configure(int srcRate, int dstRate) {
                int g = Gcd(srcRate, dstRate);
                stepNum = srcRate / g;
                modDen = dstRate / g;
                accum = 0;
                s0 = 0f;
                s1 = 0f;
                s2 = 0f;
                s3 = 0f;
            }

            internal int PredictOutputCount(int inSamples) {
                long num = (long)inSamples * modDen - accum;
                if (num <= 0) {
                    return 0;
                }
                return (int)((num + stepNum - 1) / stepNum);
            }

            internal int ConvertInterleavedChannel(float[] interleaved, int channelOffset, int stride, int inSamples, float[] dst) {
                int outIdx = 0;
                for (int i = 0; i < inSamples; i++) {
                    float x = interleaved[i * stride + channelOffset];
                    s0 = s1;
                    s1 = s2;
                    s2 = s3;
                    s3 = x;
                    while (accum < modDen) {
                        dst[outIdx++] = EvalCatmullRom(s0, s1, s2, s3, (float)accum / modDen);
                        accum += stepNum;
                    }
                    accum -= modDen;
                }
                return outIdx;
            }

            internal void ConvertToFifo(float[] src, int inSamples, float[] fifo, int capacity, ref int writeIdx, ref int count) {
                for (int i = 0; i < inSamples; i++) {
                    float x = src[i];
                    s0 = s1;
                    s1 = s2;
                    s2 = s3;
                    s3 = x;
                    while (accum < modDen) {
                        fifo[writeIdx] = EvalCatmullRom(s0, s1, s2, s3, (float)accum / modDen);
                        writeIdx++;
                        if (writeIdx >= capacity) {
                            writeIdx = 0;
                        }
                        count++;
                        accum += stepNum;
                    }
                    accum -= modDen;
                }
            }

            static float EvalCatmullRom(float s0, float s1, float s2, float s3, float t) {
                float c0 = s1;
                float c1 = 0.5f * (s2 - s0);
                float c2 = s0 - 2.5f * s1 + 2f * s2 - 0.5f * s3;
                float c3 = 0.5f * (s3 - s0) + 1.5f * (s1 - s2);
                return ((c3 * t + c2) * t + c1) * t + c0;
            }

            static int Gcd(int a, int b) {
                while (b != 0) {
                    int t = a % b;
                    a = b;
                    b = t;
                }
                return a > 0 ? a : 1;
            }
        }

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
            if (channels == null || channels.Length == 0) {
                Reset();
                return;
            }
            SpatialChannels = EnsureStereoLfeChannel(channels);
            FilterSampleRate = sampleRate > 0 ? sampleRate : defaultSampleRate;
            ReferenceDistance = referenceDistance > 0f && !float.IsNaN(referenceDistance) && !float.IsInfinity(referenceDistance)
                ? referenceDistance
                : defaultDistance;
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
                if (activeSet[channel].active) {
                    activeSet[channel].Filter?.Reset();
                    activeSet[channel].Crossover?.Reset();
                    activeSet[channel].LowDelay?.Reset();
                }
            }
            ismRoomEngine.Reset();
            wasIsmActive = false;
            rateSrcRate = 0;
        }

        /// <summary>
        /// Clears all convolution overlap-add buffers, crossovers, and delay lines in the active <see cref="SpatialChannels"/>
        /// so track switches, seeks, or isolated acoustic probes never bleed residual BRIR/HRIR tails.
        /// </summary>
        public static void ResetActiveFilters() {
            VirtualChannel[] activeSet = SpatialChannels;
            if (activeSet == null) {
                return;
            }
            for (int i = 0; i < activeSet.Length; i++) {
                if (activeSet[i].active) {
                    activeSet[i].Filter?.Reset();
                    activeSet[i].Crossover?.Reset();
                    activeSet[i].LowDelay?.Reset();
                }
            }
        }

        /// <summary>
        /// Apply the virtualizer on the <see cref="Listener"/>'s output,
        /// seamlessly converting sample rates when <paramref name="sampleRate"/> differs from <see cref="FilterSampleRate"/>
        /// so active impulses always process audio without silent drops or zeroing.
        /// </summary>
        public void Process(float[] output, int sampleRate) {
            if (output == null || output.Length == 0 || sampleRate <= 0) {
                return;
            }
            if (appliedVersion != ProfileVersion || boundChannels == null) {
                SetLayout();
            }
            VirtualChannel[] activeSet = boundChannels;
            int channels = Listener.Channels.Length;
            if (originalSplit == null || activeSet == null || channels <= 0) {
                return;
            }
            if (channels != activeSet.Length) {
                SetLayout();
                activeSet = boundChannels;
                if (activeSet == null || channels != activeSet.Length) {
                    FallbackStereoDownmix(output, channels);
                    return;
                }
            }
            int inBlockSize = output.Length / channels;
            if (inBlockSize <= 0) {
                return;
            }

            int fltRate = FilterSampleRate > 0 ? FilterSampleRate : sampleRate;
            if (sampleRate != fltRate) {
                ProcessRateMatched(output, activeSet, channels, inBlockSize, sampleRate, fltRate);
                return;
            }
            rateSrcRate = 0;

            blockSize = inBlockSize;
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

            RunVirtualizationStages(activeSet, channels, blockSize, sampleRate);

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

        void RunVirtualizationStages(VirtualChannel[] activeSet, int channels, int sampleCount, int sampleRate) {
            // Slot 2C (W09): Pre-HRTF Virtual Speaker Bus hook (Lossless-Guided Atmos Restoration preserving JOC cancellation)
            IPreHrtfBusProcessor preHrtfBus = PreHrtfBusProcessor;
            if (preHrtfBus != null) {
                preHrtfBus.ProcessPreHrtfBus(originalSplit, activeSet, channels, sampleCount, sampleRate);
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
                    sampleCount,
                    sampleRate,
                    LiveListenerNormalizedPosition,
                    LiveListenerRotationDegrees);
                wasIsmActive = true;
            } else if (wasIsmActive) {
                ismRoomEngine.Reset();
                wasIsmActive = false;
            }

            if (CenterWallEcho && center != unassigned && left != unassigned && right != unassigned) {
                Array.Copy(originalSplit[center], delayedCenter, sampleCount);
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
        }

        void ProcessRateMatched(float[] output, VirtualChannel[] activeSet, int channels, int inBlockSize, int srcRate, int fltRate) {
            EnsureRateConversionBuffers(channels, inBlockSize, srcRate, fltRate);

            int fltBlockSize = inRateConverters[0].PredictOutputCount(inBlockSize);
            int slotIdx = fltBlockSize == rateBaseFltBlockSize ? 0 : 1;
            RateSlotBuffers slot = rateSlots[slotIdx];

            for (int ch = 0; ch < channels; ch++) {
                int produced = inRateConverters[ch].ConvertInterleavedChannel(output, ch, channels, inBlockSize, slot.Original[ch]);
                if (produced < fltBlockSize) {
                    Array.Clear(slot.Original[ch], produced, fltBlockSize - produced);
                }
            }

            if (fltBlockSize > 0) {
                float[][] savedOrig = originalSplit;
                float[][] savedLow = lowSplit;
                float[][] savedLeft = leftSplit;
                float[][] savedRight = rightSplit;
                float[] savedDelayed = delayedCenter;
                int savedBlockSize = blockSize;

                originalSplit = slot.Original;
                lowSplit = slot.Low;
                leftSplit = slot.Left;
                rightSplit = slot.Right;
                delayedCenter = slot.DelayedCenter;
                blockSize = fltBlockSize;

                try {
                    RunVirtualizationStages(activeSet, channels, fltBlockSize, fltRate);
                } finally {
                    originalSplit = savedOrig;
                    lowSplit = savedLow;
                    leftSplit = savedLeft;
                    rightSplit = savedRight;
                    delayedCenter = savedDelayed;
                    blockSize = savedBlockSize;
                }

                Array.Clear(slot.StereoL, 0, fltBlockSize);
                Array.Clear(slot.StereoR, 0, fltBlockSize);
                for (int channel = 0; channel < channels; channel++) {
                    VirtualChannel ch = activeSet[channel];
                    if (ch.active) {
                        float[] leftSamples = slot.Left[channel];
                        float[] rightSamples = slot.Right[channel];
                        if (ch.Crossover != null) {
                            float[] lowSamples = slot.Low[channel];
                            for (int sample = 0; sample < fltBlockSize; sample++) {
                                float unspatialized = lowSamples[sample] * VirtualChannel.minus3dB;
                                slot.StereoL[sample] += leftSamples[sample] + unspatialized;
                                slot.StereoR[sample] += rightSamples[sample] + unspatialized;
                            }
                        } else {
                            for (int sample = 0; sample < fltBlockSize; sample++) {
                                slot.StereoL[sample] += leftSamples[sample];
                                slot.StereoR[sample] += rightSamples[sample];
                            }
                        }
                    } else if (ch.isLfe && ch.mixLfeToStereo) {
                        float[] lfeSamples = slot.Original[channel];
                        for (int sample = 0; sample < fltBlockSize; sample++) {
                            float lfe = lfeSamples[sample] * VirtualChannel.minus3dB;
                            slot.StereoL[sample] += lfe;
                            slot.StereoR[sample] += lfe;
                        }
                    }
                }

                outRateConverterL.ConvertToFifo(slot.StereoL, fltBlockSize, outFifoL, outFifoCapacity, ref fifoWriteL, ref fifoCountL);
                outRateConverterR.ConvertToFifo(slot.StereoR, fltBlockSize, outFifoR, outFifoCapacity, ref fifoWriteR, ref fifoCountR);
            }

            bool clearL = channels >= 1 && (activeSet[0].active || (activeSet[0].isLfe && activeSet[0].mixLfeToStereo));
            bool clearR = channels >= 2 && (activeSet[1].active || (activeSet[1].isLfe && activeSet[1].mixLfeToStereo));

            for (int sample = 0; sample < inBlockSize; sample++) {
                float sL = 0f;
                if (fifoCountL > 0) {
                    sL = outFifoL[fifoReadL++];
                    if (fifoReadL >= outFifoCapacity) {
                        fifoReadL = 0;
                    }
                    fifoCountL--;
                }
                float sR = 0f;
                if (fifoCountR > 0) {
                    sR = outFifoR[fifoReadR++];
                    if (fifoReadR >= outFifoCapacity) {
                        fifoReadR = 0;
                    }
                    fifoCountR--;
                }

                int outIdx = sample * channels;
                if (channels == 1) {
                    output[outIdx] = clearL ? 0.5f * (sL + sR) : output[outIdx] + 0.5f * (sL + sR);
                } else {
                    output[outIdx] = clearL ? sL : output[outIdx] + sL;
                    output[outIdx + 1] = clearR ? sR : output[outIdx + 1] + sR;
                    for (int ch = 2; ch < channels; ch++) {
                        if (activeSet[ch].active || (activeSet[ch].isLfe && activeSet[ch].mixLfeToStereo)) {
                            output[outIdx + ch] = 0f;
                        }
                    }
                }
            }
        }

        void EnsureRateConversionBuffers(int channels, int inBlockSize, int srcRate, int fltRate) {
            if (rateSrcRate == srcRate && rateFltRate == fltRate && rateInBlockSize == inBlockSize && rateChannels == channels && rateSlots != null) {
                return;
            }
            rateSrcRate = srcRate;
            rateFltRate = fltRate;
            rateInBlockSize = inBlockSize;
            rateChannels = channels;

            int g = srcRate;
            int b = fltRate;
            while (b != 0) {
                int t = g % b;
                g = b;
                b = t;
            }
            if (g <= 0) {
                g = 1;
            }
            rateStepIn = srcRate / g;
            rateModIn = fltRate / g;
            rateBaseFltBlockSize = (int)((long)inBlockSize * rateModIn / rateStepIn);

            inRateConverters = new ChannelRateConverter[channels];
            for (int ch = 0; ch < channels; ch++) {
                inRateConverters[ch] = new ChannelRateConverter();
                inRateConverters[ch].Configure(srcRate, fltRate);
            }
            outRateConverterL = new ChannelRateConverter();
            outRateConverterL.Configure(fltRate, srcRate);
            outRateConverterR = new ChannelRateConverter();
            outRateConverterR.Configure(fltRate, srcRate);

            outFifoCapacity = Math.Max(64, inBlockSize + Math.Max(16, (int)Math.Ceiling((double)srcRate / fltRate) * 2 + 16));
            outFifoL = new float[outFifoCapacity];
            outFifoR = new float[outFifoCapacity];
            fifoReadL = 0;
            fifoWriteL = 0;
            fifoCountL = 0;
            fifoReadR = 0;
            fifoWriteR = 0;
            fifoCountR = 0;

            rateSlots = new RateSlotBuffers[2];
            for (int s = 0; s < 2; s++) {
                int slotLen = Math.Max(1, rateBaseFltBlockSize + s);
                RateSlotBuffers slot = new RateSlotBuffers();
                slot.Original = new float[channels][];
                slot.Low = new float[channels][];
                slot.Left = new float[channels][];
                slot.Right = new float[channels][];
                for (int ch = 0; ch < channels; ch++) {
                    slot.Original[ch] = new float[slotLen];
                    slot.Low[ch] = new float[slotLen];
                    slot.Left[ch] = new float[slotLen];
                    slot.Right[ch] = new float[slotLen];
                }
                slot.DelayedCenter = new float[slotLen];
                slot.StereoL = new float[slotLen];
                slot.StereoR = new float[slotLen];
                rateSlots[s] = slot;
            }
        }

        static void FallbackStereoDownmix(float[] output, int channels) {
            if (channels <= 2) {
                return;
            }
            int block = output.Length / channels;
            for (int sample = 0; sample < block; sample++) {
                int baseIdx = sample * channels;
                float l = output[baseIdx];
                float r = output[baseIdx + 1];
                for (int ch = 2; ch < channels; ch++) {
                    float v = output[baseIdx + ch] * VirtualChannel.minus3dB;
                    l += v;
                    r += v;
                    output[baseIdx + ch] = 0f;
                }
                output[baseIdx] = l;
                output[baseIdx + 1] = r;
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
