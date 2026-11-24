using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Cavern.Filters;
using Cavern.ProAudio;
using Cavern.VocalChain;

namespace Cavern.Spatial {
    /// <summary>
    /// Signal-processing topology class used by the Omniscient Cortex to reason about phase,
    /// latency, and dynamic transfer behavior across chained DSP stages.
    /// </summary>
    public enum ComponentTopologyClass {
        /// <summary>Linear time-invariant minimum-phase filter (IIR/biquad/EQ).</summary>
        LinearMinimumPhase = 0,
        /// <summary>Linear time-invariant zero/linear-phase FIR or STFT filter.</summary>
        LinearZeroPhase = 1,
        /// <summary>Time-varying nonlinear dynamics processor (compressor, limiter, gate, normalizer).</summary>
        NonLinearDynamics = 2,
        /// <summary>Memoryless or ADAA harmonic waveshaper/saturator.</summary>
        NonLinearWaveshaper = 3,
        /// <summary>Multichannel binaural virtualizer or spatial upmixer.</summary>
        SpatialRenderer = 4,
        /// <summary>Feedback delay network or convolution room/reverb processor.</summary>
        ReverbFdn = 5,
        /// <summary>Hybrid multi-stage vocal or STFT spectral chain.</summary>
        HybridStft = 6
    }

    /// <summary>
    /// Bitmask describing physical acoustic/gain-staging responsibilities of a DSP component
    /// so <see cref="ComponentConflictGovernor"/> prevents cascading conflicts and redundant makeup gain.
    /// </summary>
    [Flags]
    public enum ComponentConflictFlags {
        /// <summary>No special inter-stage conflict tags.</summary>
        None = 0,
        /// <summary>Component applies automatic or dynamic makeup gain.</summary>
        MakeupGain = 1 << 0,
        /// <summary>Component boosts upper-treble / Air presence (&gt; 7 kHz).</summary>
        HighShelfAir = 1 << 1,
        /// <summary>Component adds harmonic saturation or analog drive.</summary>
        AnalogDrive = 1 << 2,
        /// <summary>Component applies low-frequency / bass loudness boost (&lt; 200 Hz).</summary>
        LowEndBassBoost = 1 << 3,
        /// <summary>Component adds synthetic room reflections or diffuse reverb tail.</summary>
        RoomEarlyReflections = 1 << 4,
        /// <summary>Component performs sub-bass / LFE crossover routing.</summary>
        SubBassCrossover = 1 << 5,
        /// <summary>Component enforces a hard ITU-R BS.1770 True-Peak ceiling.</summary>
        TruePeakCeiling = 1 << 6,
        /// <summary>Component applies vocal formant / dialog mid-band shaping.</summary>
        VocalFormantEq = 1 << 7
    }

    /// <summary>
    /// Machine-readable grey-box behavioral fingerprint of a single Cavern DSP component
    /// (auto-measured offline via deterministic impulse/step/multi-level probes — zero user training required).
    /// </summary>
    public sealed class ComponentBehaviorProfile {
        /// <summary>Canonical stable component identifier (e.g., <c>cavern.true_peak_limiter</c>).</summary>
        public string ComponentId { get; }

        /// <summary>Human-readable component name.</summary>
        public string DisplayName { get; }

        /// <summary>Functional subsystem category (e.g., <c>Dynamics</c>, <c>VocalChain</c>, <c>Psychoacoustics</c>).</summary>
        public string Category { get; }

        /// <summary>Linear/nonlinear phase &amp; memory topology classification.</summary>
        public ComponentTopologyClass Topology { get; }

        /// <summary>Measured or declared processing latency in samples at <see cref="SampleRateHz"/>.</summary>
        public int LatencySamples { get; }

        /// <summary>Measurement sample rate in Hz.</summary>
        public int SampleRateHz { get; }

        /// <summary>Input RMS levels (dBFS, ascending) at which the I/O transfer curve was measured.</summary>
        public float[] TransferCurveInputDb { get; }

        /// <summary>Measured steady-state output RMS levels (dBFS) corresponding to <see cref="TransferCurveInputDb"/>.</summary>
        public float[] TransferCurveOutputDb { get; }

        /// <summary>Estimated compression/limiting knee threshold (dBFS) where slope deviates from unity.</summary>
        public float DynamicKneeDb { get; }

        /// <summary>Effective compression/limiting ratio above <see cref="DynamicKneeDb"/> (1.0 = linear).</summary>
        public float EffectiveRatio { get; }

        /// <summary>Measured 10%-to-90% gain-reduction attack time in milliseconds.</summary>
        public float AttackMs { get; }

        /// <summary>Measured 90%-to-10% gain-recovery release time in milliseconds.</summary>
        public float ReleaseMs { get; }

        /// <summary>Nominal linear-regime gain delta at -24 dBFS input (dB).</summary>
        public float NominalGainDeltaDb { get; }

        /// <summary>Maximum upward gain/makeup boost observed across the transfer curve (dB).</summary>
        public float MaxMakeupGainDb { get; }

        /// <summary>Measured crest-factor change (OutputCrestDb - InputCrestDb) on high-level burst/pink probe.</summary>
        public float CrestDeltaDb { get; }

        /// <summary>Estimated total harmonic distortion ratio (linear, 0..1) on a -6 dBFS 1 kHz sine probe.</summary>
        public float HarmonicThdRatio { get; }

        /// <summary>Measured 1 kHz phase shift in degrees (-180..180) relative to latency-aligned input.</summary>
        public float PhaseShiftDeg1kHz { get; }

        /// <summary>Enforced or safe True-Peak headroom ceiling in dBTP.</summary>
        public float HeadroomCeilingDbTp { get; }

        /// <summary>Inter-stage conflict responsibility flags.</summary>
        public ComponentConflictFlags ConflictFlags { get; }

        /// <summary>Initializes an immutable <see cref="ComponentBehaviorProfile"/>.</summary>
        public ComponentBehaviorProfile(
            string componentId,
            string displayName,
            string category,
            ComponentTopologyClass topology,
            int latencySamples,
            int sampleRateHz,
            float[] transferCurveInputDb,
            float[] transferCurveOutputDb,
            float dynamicKneeDb,
            float effectiveRatio,
            float attackMs,
            float releaseMs,
            float nominalGainDeltaDb,
            float maxMakeupGainDb,
            float crestDeltaDb,
            float harmonicThdRatio,
            float phaseShiftDeg1kHz,
            float headroomCeilingDbTp,
            ComponentConflictFlags conflictFlags) {
            ComponentId = string.IsNullOrWhiteSpace(componentId) ? "cavern.unknown" : componentId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? ComponentId : displayName;
            Category = string.IsNullOrWhiteSpace(category) ? "DSP" : category;
            Topology = topology;
            LatencySamples = latencySamples < 0 ? 0 : latencySamples;
            SampleRateHz = sampleRateHz < 8000 ? 48000 : sampleRateHz;
            TransferCurveInputDb = CloneOrDefault(transferCurveInputDb, new float[] { -42f, -24f, -12f, -3f });
            TransferCurveOutputDb = CloneOrDefault(transferCurveOutputDb, TransferCurveInputDb);
            if (TransferCurveOutputDb.Length != TransferCurveInputDb.Length) {
                float[] resized = new float[TransferCurveInputDb.Length];
                int copyLen = Math.Min(TransferCurveOutputDb.Length, resized.Length);
                Array.Copy(TransferCurveOutputDb, resized, copyLen);
                for (int i = copyLen; i < resized.Length; i++) {
                    resized[i] = TransferCurveInputDb[i];
                }
                TransferCurveOutputDb = resized;
            }
            DynamicKneeDb = Sanitize(dynamicKneeDb, 0f);
            EffectiveRatio = Math.Max(0.25f, Sanitize(effectiveRatio, 1f));
            AttackMs = Math.Max(0f, Sanitize(attackMs, 0f));
            ReleaseMs = Math.Max(0f, Sanitize(releaseMs, 0f));
            NominalGainDeltaDb = Sanitize(nominalGainDeltaDb, 0f);
            MaxMakeupGainDb = Math.Max(0f, Sanitize(maxMakeupGainDb, 0f));
            CrestDeltaDb = Sanitize(crestDeltaDb, 0f);
            HarmonicThdRatio = SpatialCortexMath.Clamp(Sanitize(harmonicThdRatio, 0f), 0f, 1f);
            PhaseShiftDeg1kHz = SpatialCortexMath.WrapAzimuthDeg(Sanitize(phaseShiftDeg1kHz, 0f));
            HeadroomCeilingDbTp = Sanitize(headroomCeilingDbTp, -1.0f);
            ConflictFlags = conflictFlags;
        }

        /// <summary>
        /// Predicts steady-state output RMS level (dBFS) for an arbitrary input RMS level using
        /// piecewise-linear interpolation &amp; slope-matched extrapolation over the measured transfer curve.
        /// </summary>
        public float PredictOutputRmsDb(float inputRmsDb) {
            if (!SpatialCortexMath.IsFinite(inputRmsDb)) {
                return -120f;
            }
            float[] xs = TransferCurveInputDb;
            float[] ys = TransferCurveOutputDb;
            int n = xs.Length;
            if (n == 0) {
                return inputRmsDb + NominalGainDeltaDb;
            }
            if (n == 1) {
                return inputRmsDb + (ys[0] - xs[0]);
            }
            if (inputRmsDb <= xs[0]) {
                float lowSlope = (ys[1] - ys[0]) / Math.Max(1e-4f, xs[1] - xs[0]);
                return ys[0] + lowSlope * (inputRmsDb - xs[0]);
            }
            if (inputRmsDb >= xs[n - 1]) {
                float highSlope = (ys[n - 1] - ys[n - 2]) / Math.Max(1e-4f, xs[n - 1] - xs[n - 2]);
                float extrapolated = ys[n - 1] + highSlope * (inputRmsDb - xs[n - 1]);
                if ((ConflictFlags & ComponentConflictFlags.TruePeakCeiling) != 0 && extrapolated > HeadroomCeilingDbTp) {
                    return HeadroomCeilingDbTp;
                }
                return extrapolated;
            }
            for (int i = 0; i < n - 1; i++) {
                if (inputRmsDb >= xs[i] && inputRmsDb <= xs[i + 1]) {
                    float span = xs[i + 1] - xs[i];
                    float frac = span > 1e-5f ? (inputRmsDb - xs[i]) / span : 0f;
                    return ys[i] + (ys[i + 1] - ys[i]) * frac;
                }
            }
            return ys[n - 1];
        }

        /// <summary>Serializes this behavioral profile into a compact deterministic JSON string (zero external dependencies).</summary>
        public string ToCompactJson() {
            StringBuilder sb = new StringBuilder(384);
            sb.Append("{\"id\":\"").Append(EscapeJson(ComponentId))
              .Append("\",\"name\":\"").Append(EscapeJson(DisplayName))
              .Append("\",\"cat\":\"").Append(EscapeJson(Category))
              .Append("\",\"topo\":\"").Append(Topology.ToString())
              .Append("\",\"lat\":").Append(LatencySamples)
              .Append(",\"sr\":").Append(SampleRateHz)
              .Append(",\"kneeDb\":").Append(Fmt(DynamicKneeDb))
              .Append(",\"ratio\":").Append(Fmt(EffectiveRatio))
              .Append(",\"atkMs\":").Append(Fmt(AttackMs))
              .Append(",\"relMs\":").Append(Fmt(ReleaseMs))
              .Append(",\"nomGainDb\":").Append(Fmt(NominalGainDeltaDb))
              .Append(",\"maxMakeupDb\":").Append(Fmt(MaxMakeupGainDb))
              .Append(",\"crestDeltaDb\":").Append(Fmt(CrestDeltaDb))
              .Append(",\"thd\":").Append(HarmonicThdRatio.ToString("0.0000", CultureInfo.InvariantCulture))
              .Append(",\"phase1kDeg\":").Append(Fmt(PhaseShiftDeg1kHz))
              .Append(",\"ceilDbTp\":").Append(Fmt(HeadroomCeilingDbTp))
              .Append(",\"flags\":").Append((int)ConflictFlags)
              .Append(",\"io\":[");
            for (int i = 0; i < TransferCurveInputDb.Length; i++) {
                if (i > 0) {
                    sb.Append(',');
                }
                sb.Append('[').Append(Fmt(TransferCurveInputDb[i])).Append(',').Append(Fmt(TransferCurveOutputDb[i])).Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static float[] CloneOrDefault(float[] src, float[] fallback) {
            if (src == null || src.Length == 0) {
                float[] copyFb = new float[fallback.Length];
                Array.Copy(fallback, copyFb, fallback.Length);
                return copyFb;
            }
            float[] copy = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                copy[i] = Sanitize(src[i], -24f);
            }
            return copy;
        }

        static float Sanitize(float value, float fallback) {
            return SpatialCortexMath.IsFinite(value) ? value : fallback;
        }

        static string Fmt(float v) {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        static string EscapeJson(string s) {
            if (string.IsNullOrEmpty(s)) {
                return string.Empty;
            }
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>
    /// Thread-safe registry holding auto-characterized <see cref="ComponentBehaviorProfile"/> entries
    /// for every active Cavern DSP block.
    /// </summary>
    public sealed class ComponentBehaviorRegistry {
        readonly object syncLock = new object();
        readonly Dictionary<string, ComponentBehaviorProfile> profiles =
            new Dictionary<string, ComponentBehaviorProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of registered component profiles.</summary>
        public int Count {
            get {
                lock (syncLock) {
                    return profiles.Count;
                }
            }
        }

        /// <summary>Registers or updates a component behavioral profile.</summary>
        public void Register(ComponentBehaviorProfile profile) {
            if (profile == null) {
                return;
            }
            lock (syncLock) {
                profiles[profile.ComponentId] = profile;
            }
        }

        /// <summary>Attempts to retrieve a profile by its canonical component ID.</summary>
        public bool TryGet(string componentId, out ComponentBehaviorProfile profile) {
            profile = null;
            if (string.IsNullOrWhiteSpace(componentId)) {
                return false;
            }
            lock (syncLock) {
                return profiles.TryGetValue(componentId, out profile);
            }
        }

        /// <summary>Returns an immutable snapshot array of all registered profiles sorted by ID.</summary>
        public ComponentBehaviorProfile[] GetSnapshot() {
            lock (syncLock) {
                ComponentBehaviorProfile[] list = new ComponentBehaviorProfile[profiles.Count];
                int idx = 0;
                foreach (KeyValuePair<string, ComponentBehaviorProfile> kv in profiles) {
                    list[idx++] = kv.Value;
                }
                Array.Sort(list, (a, b) => string.Compare(a.ComponentId, b.ComponentId, StringComparison.OrdinalIgnoreCase));
                return list;
            }
        }

        /// <summary>Exports the full registry as compact JSON for diagnostics or Phase-B Cortex ingestion.</summary>
        public string ToCompactManifestJson() {
            ComponentBehaviorProfile[] snapshot = GetSnapshot();
            StringBuilder sb = new StringBuilder(snapshot.Length * 360 + 64);
            sb.Append("{\"schema\":\"cavern-component-profiles-v1\",\"count\":").Append(snapshot.Length).Append(",\"profiles\":[");
            for (int i = 0; i < snapshot.Length; i++) {
                if (i > 0) {
                    sb.Append(',');
                }
                sb.Append(snapshot[i].ToCompactJson());
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Deterministic conflict-resolution and gain-staging plan emitted by <see cref="ComponentConflictGovernor"/>.
    /// </summary>
    public sealed class ChainGovernancePlan {
        /// <summary>Recommended pre-chain or inter-stage trim (dB, &lt;= 0) to prevent headroom runaway.</summary>
        public float RecommendedTrimDb { get; }

        /// <summary>Maximum safe additional makeup gain allowed across dynamic stages (dB).</summary>
        public float MaxSafeMakeupDb { get; }

        /// <summary>Predicted net steady-state RMS gain delta across the evaluated chain at reference input (dB).</summary>
        public float PredictedNetGainDb { get; }

        /// <summary>Predicted output RMS level (dBFS) at the evaluated reference input.</summary>
        public float PredictedOutputRmsDb { get; }

        /// <summary>Predicted peak output level (dBTP) accounting for cumulative crest/makeup.</summary>
        public float PredictedPeakDbTp { get; }

        /// <summary>Detected inter-stage conflict flags requiring deduplication or clamping.</summary>
        public ComponentConflictFlags DetectedConflicts { get; }

        /// <summary>True when hardware pre-ADC Air is active and software Air shelf should be subtracted/skipped.</summary>
        public bool SkipSoftwareAir { get; }

        /// <summary>True when hardware pre-ADC Drive is active and software analog saturation should be bypassed.</summary>
        public bool SkipSoftwareDrive { get; }

        /// <summary>True when a measured room BRIR is active and synthetic ISM/FDN early reflections should be bypassed.</summary>
        public bool SkipSyntheticRoom { get; }

        /// <summary>True when the governed chain satisfies the ITU-R BS.1770 -1.0 dBTP headroom invariant.</summary>
        public bool IsTruePeakSafe { get; }

        /// <summary>Number of stages evaluated in this plan.</summary>
        public int EvaluatedStageCount { get; }

        /// <summary>Initializes a <see cref="ChainGovernancePlan"/>.</summary>
        public ChainGovernancePlan(
            float recommendedTrimDb,
            float maxSafeMakeupDb,
            float predictedNetGainDb,
            float predictedOutputRmsDb,
            float predictedPeakDbTp,
            ComponentConflictFlags detectedConflicts,
            bool skipSoftwareAir,
            bool skipSoftwareDrive,
            bool skipSyntheticRoom,
            bool isTruePeakSafe,
            int evaluatedStageCount) {
            RecommendedTrimDb = recommendedTrimDb;
            MaxSafeMakeupDb = maxSafeMakeupDb;
            PredictedNetGainDb = predictedNetGainDb;
            PredictedOutputRmsDb = predictedOutputRmsDb;
            PredictedPeakDbTp = predictedPeakDbTp;
            DetectedConflicts = detectedConflicts;
            SkipSoftwareAir = skipSoftwareAir;
            SkipSoftwareDrive = skipSoftwareDrive;
            SkipSyntheticRoom = skipSyntheticRoom;
            IsTruePeakSafe = isTruePeakSafe;
            EvaluatedStageCount = evaluatedStageCount;
        }

        /// <summary>Compact JSON representation for bridge diagnostics and logging.</summary>
        public string ToCompactJson() {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"stages\":{0},\"trimDb\":{1:0.00},\"maxMakeupDb\":{2:0.00},\"netGainDb\":{3:0.00},\"outRmsDb\":{4:0.00},\"peakDbTp\":{5:0.00},\"conflicts\":{6},\"skipAir\":{7},\"skipDrive\":{8},\"skipRoom\":{9},\"tpSafe\":{10}}}",
                EvaluatedStageCount,
                RecommendedTrimDb,
                MaxSafeMakeupDb,
                PredictedNetGainDb,
                PredictedOutputRmsDb,
                PredictedPeakDbTp,
                (int)DetectedConflicts,
                SkipSoftwareAir ? "true" : "false",
                SkipSoftwareDrive ? "true" : "false",
                SkipSyntheticRoom ? "true" : "false",
                IsTruePeakSafe ? "true" : "false");
        }
    }

    /// <summary>
    /// Deterministic offline auto-profiler that characterizes real Cavern DSP components via impulse,
    /// multi-level sine, step-burst, and crest probes into <see cref="ComponentBehaviorProfile"/> fingerprints.
    /// Zero user input or manual calibration required.
    /// </summary>
    public static class ComponentBehaviorProfiler {
        /// <summary>Canonical ID for <see cref="TruePeakLimiter"/>.</summary>
        public const string IdTruePeakLimiter = "cavern.true_peak_limiter";

        /// <summary>Canonical ID for <see cref="Iso226LoudnessFilter"/>.</summary>
        public const string IdIso226Loudness = "cavern.iso226_loudness";

        /// <summary>Canonical ID for <see cref="SpatialDialogEnhancer"/>.</summary>
        public const string IdSpatialDialog = "cavern.spatial_dialog_enhancer";

        /// <summary>Canonical ID for <see cref="Normalizer"/>.</summary>
        public const string IdNormalizer = "cavern.normalizer";

        /// <summary>Canonical ID for <see cref="LiveVocalChainDsp"/>.</summary>
        public const string IdLiveVocalChain = "cavern.live_vocal_chain";

        /// <summary>Canonical ID for high-shelf Air / presence EQ stage.</summary>
        public const string IdAirHighShelf = "cavern.air_high_shelf";

        static readonly float[] DefaultProbeLevelsDb = new float[] { -42f, -30f, -24f, -18f, -12f, -6f, -3f };

        /// <summary>
        /// Auto-characterizes an arbitrary mono DSP processor callback via deterministic impulse,
        /// multi-level 1 kHz sine, step-burst ballistics, and harmonic distortion probes.
        /// </summary>
        /// <param name="componentId">Canonical ID.</param>
        /// <param name="displayName">Display name.</param>
        /// <param name="category">Category label.</param>
        /// <param name="topology">Topology classification.</param>
        /// <param name="conflictFlags">Conflict responsibility flags.</param>
        /// <param name="sampleRateHz">Sample rate in Hz (e.g. 48000).</param>
        /// <param name="headroomCeilingDbTp">Expected headroom ceiling in dBTP.</param>
        /// <param name="createFreshProcessor">Factory returning a fresh stateless/reset mono processor action (in-place buffer).</param>
        public static ComponentBehaviorProfile ProfileMonoProcessor(
            string componentId,
            string displayName,
            string category,
            ComponentTopologyClass topology,
            ComponentConflictFlags conflictFlags,
            int sampleRateHz,
            float headroomCeilingDbTp,
            Func<Action<float[]>> createFreshProcessor) {
            int sr = Math.Max(8000, sampleRateHz);
            float[] inLevels = DefaultProbeLevelsDb;
            float[] outLevels = new float[inLevels.Length];

            // 1. Measure exact group-delay / impulse peak latency (samples)
            int impLen = Math.Min(4096, sr / 4);
            float[] impBuf = new float[impLen];
            impBuf[64] = 0.25f;
            Action<float[]> impProc = createFreshProcessor();
            impProc(impBuf);
            int peakIdx = 64;
            float peakVal = 0f;
            for (int i = 0; i < impLen; i++) {
                float a = MathF.Abs(impBuf[i]);
                if (a > peakVal) {
                    peakVal = a;
                    peakIdx = i;
                }
            }
            int measuredLatency = Math.Max(0, peakIdx - 64);

            // 2. Measure steady-state I/O transfer curve across [-42 .. -3 dBFS] using 1 kHz sine
            //    (measure the tail half after envelopes settle so ballistics don't bias steady-state curve)
            int toneLen = Math.Max(4096, (int)(sr * 0.35));
            int settleOffset = toneLen / 2;
            float maxMakeupDb = 0f;
            float nominalGainDb = 0f;
            float thdRatio = 0f;
            float phase1kDeg = 0f;
            float crestDeltaDb = 0f;

            for (int lv = 0; lv < inLevels.Length; lv++) {
                float targetRmsDb = inLevels[lv];
                float rmsLin = DbToLinear(targetRmsDb);
                float amp = rmsLin * MathF.Sqrt(2f);
                float[] buf = new float[toneLen];
                double w = 2.0 * Math.PI * 1000.0 / sr;
                for (int i = 0; i < toneLen; i++) {
                    buf[i] = amp * (float)Math.Sin(w * i);
                }

                Action<float[]> proc = createFreshProcessor();
                proc(buf);

                float outRms = ComputeRms(buf, settleOffset, toneLen - settleOffset);
                float outPeak = ComputePeak(buf, settleOffset, toneLen - settleOffset);
                float outDb = LinearToDb(outRms);
                outLevels[lv] = outDb;

                float gainDelta = outDb - targetRmsDb;
                if (gainDelta > maxMakeupDb) {
                    maxMakeupDb = gainDelta;
                }
                if (Math.Abs(targetRmsDb - (-24f)) < 0.5f) {
                    nominalGainDb = gainDelta;
                }
                if (Math.Abs(targetRmsDb - (-6f)) < 0.5f) {
                    float inCrestDb = LinearToDb(amp) - targetRmsDb; // ~3.01 dB for sine
                    float outCrestDb = LinearToDb(outPeak) - outDb;
                    crestDeltaDb = outCrestDb - inCrestDb;
                    MeasureHarmonicAndPhase1k(buf, settleOffset, toneLen - settleOffset, sr, measuredLatency, out thdRatio, out phase1kDeg);
                }
            }

            // 3. Extract dynamic knee & effective ratio from transfer curve slopes
            float lowSlope = (outLevels[2] - outLevels[0]) / Math.Max(1e-3f, inLevels[2] - inLevels[0]);
            float highSlope = (outLevels[inLevels.Length - 1] - outLevels[inLevels.Length - 3]) /
                Math.Max(1e-3f, inLevels[inLevels.Length - 1] - inLevels[inLevels.Length - 3]);
            float effectiveRatio = highSlope > 0.02f ? Math.Max(1f, lowSlope / highSlope) : 20f;
            float kneeDb = 0f;
            for (int i = 1; i < inLevels.Length; i++) {
                float localSlope = (outLevels[i] - outLevels[i - 1]) / Math.Max(1e-3f, inLevels[i] - inLevels[i - 1]);
                if (localSlope < lowSlope * 0.85f) {
                    kneeDb = inLevels[i - 1];
                    break;
                }
            }

            // 4. Measure attack/release ballistics on a -30 dBFS -> -3 dBFS -> -30 dBFS 1 kHz step burst
            float atkMs;
            float relMs;
            MeasureStepBallistics(createFreshProcessor(), sr, out atkMs, out relMs);

            return new ComponentBehaviorProfile(
                componentId,
                displayName,
                category,
                topology,
                measuredLatency,
                sr,
                inLevels,
                outLevels,
                kneeDb,
                effectiveRatio,
                atkMs,
                relMs,
                nominalGainDb,
                maxMakeupDb,
                crestDeltaDb,
                thdRatio,
                phase1kDeg,
                headroomCeilingDbTp,
                conflictFlags);
        }

        /// <summary>
        /// Builds and populates a complete <see cref="ComponentBehaviorRegistry"/> by running deterministic
        /// offline probes against real Cavern DSP classes (zero hardcoded mock curves, zero user action).
        /// </summary>
        public static ComponentBehaviorRegistry BuildCavernDefaultRegistry(int sampleRateHz = 48000) {
            int sr = Math.Max(16000, sampleRateHz);
            ComponentBehaviorRegistry reg = new ComponentBehaviorRegistry();

            // 1. ITU-R BS.1770 True-Peak Limiter (-1.0 dBTP ceiling)
            reg.Register(ProfileMonoProcessor(
                IdTruePeakLimiter,
                "True-Peak Limiter (-1.0 dBTP)",
                "Dynamics",
                ComponentTopologyClass.NonLinearDynamics,
                ComponentConflictFlags.TruePeakCeiling,
                sr,
                -1.0f,
                () => {
                    TruePeakLimiter limiter = new TruePeakLimiter(sr, 1, -1.0f, 80f);
                    return buf => limiter.ProcessInterleaved(buf, 0, buf.Length, 1);
                }));

            // 2. ISO 226:2023 Equal-Loudness Compensator (-17 phon quiet listening compensation)
            reg.Register(ProfileMonoProcessor(
                IdIso226Loudness,
                "ISO 226:2023 Loudness Compensation",
                "Psychoacoustics",
                ComponentTopologyClass.LinearMinimumPhase,
                ComponentConflictFlags.LowEndBassBoost | ComponentConflictFlags.HighShelfAir,
                sr,
                -1.0f,
                () => {
                    Iso226LoudnessFilter iso = new Iso226LoudnessFilter(sr, 1);
                    iso.SetParameters(true, 85f, 68f, false);
                    return buf => iso.ProcessInterleaved(buf, 0, buf.Length, 1);
                }));

            // 3. Spatial Dialog Enhancer (+3.0 dB speech clarity via stereo center-coherent Mid/Side path)
            reg.Register(ProfileMonoProcessor(
                IdSpatialDialog,
                "Spatial Dialog Enhancer",
                "Psychoacoustics",
                ComponentTopologyClass.LinearMinimumPhase,
                ComponentConflictFlags.VocalFormantEq | ComponentConflictFlags.MakeupGain,
                sr,
                -1.0f,
                () => {
                    SpatialDialogEnhancer dlg = new SpatialDialogEnhancer();
                    dlg.Configure(sr);
                    dlg.SetParameters(true, 3.0f);
                    return buf => {
                        float[] stereo = new float[buf.Length * 2];
                        for (int i = 0; i < buf.Length; i++) {
                            stereo[i * 2] = buf[i];
                            stereo[i * 2 + 1] = buf[i];
                        }
                        dlg.ProcessStereoInterleaved(stereo, 0, buf.Length);
                        for (int i = 0; i < buf.Length; i++) {
                            buf[i] = 0.5f * (stereo[i * 2] + stereo[i * 2 + 1]);
                        }
                    };
                }));

            // 4. Cavern Normalizer (adaptive upward/downward gain rider)
            reg.Register(ProfileMonoProcessor(
                IdNormalizer,
                "Adaptive Normalizer",
                "Dynamics",
                ComponentTopologyClass.NonLinearDynamics,
                ComponentConflictFlags.MakeupGain,
                sr,
                -1.0f,
                () => {
                    Normalizer norm = new Normalizer(true);
                    return buf => norm.Process(buf);
                }));

            // 5. Live Vocal Mastering Chain (FET 1176 + Opto LA-2A + ADAA Warmth + FDN Reverb)
            reg.Register(ProfileMonoProcessor(
                IdLiveVocalChain,
                "Live Vocal Chain (FET+Opto+ADAA)",
                "VocalChain",
                ComponentTopologyClass.HybridStft,
                ComponentConflictFlags.MakeupGain | ComponentConflictFlags.AnalogDrive |
                ComponentConflictFlags.HighShelfAir | ComponentConflictFlags.RoomEarlyReflections,
                sr,
                -1.0f,
                () => {
                    LiveVocalChainDsp vocal = new LiveVocalChainDsp(sr);
                    VocalMixDnaProfile dna = VocalMixDnaProfile.CreateStudioDefault();
                    VocalMacroSettings macros = new VocalMacroSettings {
                        Enabled = true,
                        Bypass = false,
                        MixMatchPct = 100f,
                        DynamicsPct = 100f,
                        WarmthPct = 45f,
                        ReverbPct = 0f,
                        EnableNoiseGate = false,
                        EnableDeEsser = true,
                        EnableSidechainPocketGlue = false
                    };
                    vocal.Configure(sr, dna, macros);
                    return buf => {
                        float[] stereo = new float[buf.Length * 2];
                        for (int i = 0; i < buf.Length; i++) {
                            stereo[i * 2] = buf[i];
                            stereo[i * 2 + 1] = buf[i];
                        }
                        vocal.ProcessStereoBlockZeroGc(stereo, 0, buf.Length);
                        for (int i = 0; i < buf.Length; i++) {
                            buf[i] = 0.5f * (stereo[i * 2] + stereo[i * 2 + 1]);
                        }
                    };
                }));

            // 6. High-Shelf Air EQ (+2.5 dB @ 8.5 kHz)
            reg.Register(ProfileMonoProcessor(
                IdAirHighShelf,
                "High-Shelf Air (+2.5 dB @ 8.5 kHz)",
                "Equalizer",
                ComponentTopologyClass.LinearMinimumPhase,
                ComponentConflictFlags.HighShelfAir,
                sr,
                -1.0f,
                () => {
                    HighShelf shelf = new HighShelf(sr, 8500.0, 0.71, 2.5);
                    return buf => shelf.Process(buf);
                }));

            return reg;
        }

        static void MeasureStepBallistics(Action<float[]> proc, int sr, out float attackMs, out float releaseMs) {
            int seg = Math.Max(1024, sr / 5); // 200 ms per segment
            int total = seg * 3;
            float[] buf = new float[total];
            float lowAmp = DbToLinear(-30f) * MathF.Sqrt(2f);
            float highAmp = DbToLinear(-3f) * MathF.Sqrt(2f);
            double w = 2.0 * Math.PI * 1000.0 / sr;
            for (int i = 0; i < total; i++) {
                float a = (i >= seg && i < seg * 2) ? highAmp : lowAmp;
                buf[i] = a * (float)Math.Sin(w * i);
            }
            proc(buf);

            int win = Math.Max(32, sr / 500); // 2 ms envelope window
            int steadyHighStart = seg + (int)(seg * 0.7);
            float steadyHighRms = Math.Max(1e-6f, ComputeRms(buf, steadyHighStart, seg - (int)(seg * 0.7)));
            float firstHighRms = Math.Max(1e-6f, ComputeRms(buf, seg, win));

            attackMs = 0f;
            if (firstHighRms > steadyHighRms * 1.08f) {
                float target = steadyHighRms + 0.1f * (firstHighRms - steadyHighRms);
                for (int off = 0; off < seg - win; off += win) {
                    if (ComputeRms(buf, seg + off, win) <= target) {
                        attackMs = (off * 1000f) / sr;
                        break;
                    }
                }
            }

            int steadyLowStart = seg * 2 + (int)(seg * 0.75);
            float steadyLowRms = Math.Max(1e-6f, ComputeRms(buf, steadyLowStart, seg - (int)(seg * 0.75)));
            float firstLowRms = Math.Max(1e-6f, ComputeRms(buf, seg * 2, win));
            releaseMs = 0f;
            if (firstLowRms < steadyLowRms * 0.92f) {
                float target = steadyLowRms - 0.1f * (steadyLowRms - firstLowRms);
                for (int off = 0; off < seg - win; off += win) {
                    if (ComputeRms(buf, seg * 2 + off, win) >= target) {
                        releaseMs = (off * 1000f) / sr;
                        break;
                    }
                }
            }
        }

        static void MeasureHarmonicAndPhase1k(
            float[] buf,
            int offset,
            int length,
            int sr,
            int latencySamples,
            out float thdRatio,
            out float phaseDeg) {
            double c1, s1, c2, s2, c3, s3;
            GoertzelSinCos(buf, offset, length, sr, 1000.0, latencySamples, out c1, out s1);
            GoertzelSinCos(buf, offset, length, sr, 2000.0, latencySamples, out c2, out s2);
            GoertzelSinCos(buf, offset, length, sr, 3000.0, latencySamples, out c3, out s3);

            double mag1 = Math.Sqrt(c1 * c1 + s1 * s1);
            double mag2 = Math.Sqrt(c2 * c2 + s2 * s2);
            double mag3 = Math.Sqrt(c3 * c3 + s3 * s3);

            thdRatio = mag1 > 1e-9
                ? SpatialCortexMath.Clamp((float)(Math.Sqrt(mag2 * mag2 + mag3 * mag3) / mag1), 0f, 1f)
                : 0f;
            // Input was sin(w * i) -> projection on sin is s1, on cos is c1; phase shift = atan2(c1, s1)
            phaseDeg = mag1 > 1e-9
                ? SpatialCortexMath.WrapAzimuthDeg((float)(Math.Atan2(c1, s1) * SpatialCortexMath.RadToDeg))
                : 0f;
        }

        static void GoertzelSinCos(
            float[] x,
            int offset,
            int length,
            int sr,
            double freqHz,
            int latencySamples,
            out double cosProj,
            out double sinProj) {
            double w = 2.0 * Math.PI * freqHz / sr;
            double cSum = 0.0;
            double sSum = 0.0;
            int end = Math.Min(x.Length, offset + length);
            int n = Math.Max(1, end - offset);
            for (int i = offset; i < end; i++) {
                double phase = w * (i - latencySamples);
                double v = x[i];
                cSum += v * Math.Cos(phase);
                sSum += v * Math.Sin(phase);
            }
            cosProj = 2.0 * cSum / n;
            sinProj = 2.0 * sSum / n;
        }

        static float ComputeRms(float[] x, int offset, int length) {
            int end = Math.Min(x.Length, offset + length);
            int n = Math.Max(1, end - offset);
            double sum = 0.0;
            for (int i = offset; i < end; i++) {
                float s = x[i];
                if (SpatialCortexMath.IsFinite(s)) {
                    sum += s * s;
                }
            }
            return (float)Math.Sqrt(sum / n);
        }

        static float ComputePeak(float[] x, int offset, int length) {
            int end = Math.Min(x.Length, offset + length);
            float peak = 0f;
            for (int i = offset; i < end; i++) {
                float a = MathF.Abs(x[i]);
                if (SpatialCortexMath.IsFinite(a) && a > peak) {
                    peak = a;
                }
            }
            return peak;
        }

        static float DbToLinear(float db) {
            return (float)Math.Pow(10.0, db / 20.0);
        }

        static float LinearToDb(float lin) {
            return lin > 1e-7f ? (float)(20.0 * Math.Log10(lin)) : -140f;
        }
    }

    /// <summary>
    /// Inter-stage DSP conflict resolver and headroom governor consuming <see cref="ComponentBehaviorProfile"/>
    /// measurements + optional <see cref="HardwarePreAmpStateSnapshot"/> telemetry to prevent redundant makeup gain,
    /// double Scarlett Air/Drive saturation, double room reflections, and BS.1770 True-Peak clipping.
    /// </summary>
    public static class ComponentConflictGovernor {
        /// <summary>Default target ITU-R BS.1770 True-Peak ceiling (dBTP).</summary>
        public const float TargetCeilingDbTp = -1.0f;

        /// <summary>
        /// Evaluates an ordered chain of DSP components at <paramref name="referenceInputRmsDb"/>
        /// (with assumed program crest factor <paramref name="programCrestDb"/>) and emits a conflict-free
        /// <see cref="ChainGovernancePlan"/>.
        /// </summary>
        public static ChainGovernancePlan EvaluateChain(
            IReadOnlyList<ComponentBehaviorProfile> orderedChain,
            float referenceInputRmsDb = -18.0f,
            float programCrestDb = 12.0f,
            HardwarePreAmpStateSnapshot preAmpSnapshot = null,
            bool isMeasuredBrirActive = false) {
            int count = orderedChain != null ? orderedChain.Count : 0;
            float runningRmsDb = SpatialCortexMath.IsFinite(referenceInputRmsDb) ? referenceInputRmsDb : -18.0f;
            float runningCrestDb = SpatialCortexMath.Clamp(programCrestDb, 3.0f, 24.0f);
            float cumulativeMakeupDb = 0f;
            float strictestCeilingDbTp = TargetCeilingDbTp;

            int makeupStageCount = 0;
            int airStageCount = 0;
            int driveStageCount = 0;
            int bassBoostStageCount = 0;
            int roomStageCount = isMeasuredBrirActive ? 1 : 0;
            bool hasTruePeakGuard = false;

            bool hwAirActive = preAmpSnapshot != null &&
                               preAmpSnapshot.IsHardwareVerified &&
                               preAmpSnapshot.MicSource == HardwarePreAmpStateSnapshot.MicSourceUsbInterface &&
                               preAmpSnapshot.AirMode != ScarlettAirMode.Off;
            bool hwDriveActive = preAmpSnapshot != null &&
                                 preAmpSnapshot.IsHardwareVerified &&
                                 preAmpSnapshot.MicSource == HardwarePreAmpStateSnapshot.MicSourceUsbInterface &&
                                 (preAmpSnapshot.DriveActive || preAmpSnapshot.AirMode == ScarlettAirMode.PresenceAndDrive);
            if (hwAirActive) {
                airStageCount++;
            }
            if (hwDriveActive) {
                driveStageCount++;
            }

            ComponentConflictFlags conflicts = ComponentConflictFlags.None;

            for (int i = 0; i < count; i++) {
                ComponentBehaviorProfile p = orderedChain[i];
                if (p == null) {
                    continue;
                }

                bool stageAir = (p.ConflictFlags & ComponentConflictFlags.HighShelfAir) != 0;
                bool stageDrive = (p.ConflictFlags & ComponentConflictFlags.AnalogDrive) != 0;
                bool stageRoom = (p.ConflictFlags & ComponentConflictFlags.RoomEarlyReflections) != 0;
                bool stageMakeup = (p.ConflictFlags & ComponentConflictFlags.MakeupGain) != 0;
                bool stageBass = (p.ConflictFlags & ComponentConflictFlags.LowEndBassBoost) != 0;
                bool stageTp = (p.ConflictFlags & ComponentConflictFlags.TruePeakCeiling) != 0;

                if (stageAir) {
                    airStageCount++;
                    if (airStageCount > 1) {
                        conflicts |= ComponentConflictFlags.HighShelfAir;
                    }
                }
                if (stageDrive) {
                    driveStageCount++;
                    if (driveStageCount > 1) {
                        conflicts |= ComponentConflictFlags.AnalogDrive;
                    }
                }
                if (stageRoom) {
                    roomStageCount++;
                    if (roomStageCount > 1) {
                        conflicts |= ComponentConflictFlags.RoomEarlyReflections;
                    }
                }
                if (stageBass) {
                    bassBoostStageCount++;
                    if (bassBoostStageCount > 1) {
                        conflicts |= ComponentConflictFlags.LowEndBassBoost;
                    }
                }
                if (stageMakeup) {
                    makeupStageCount++;
                    cumulativeMakeupDb += p.MaxMakeupGainDb;
                    if (makeupStageCount > 1) {
                        conflicts |= ComponentConflictFlags.MakeupGain;
                    }
                }
                if (stageTp) {
                    hasTruePeakGuard = true;
                    if (p.HeadroomCeilingDbTp < strictestCeilingDbTp) {
                        strictestCeilingDbTp = p.HeadroomCeilingDbTp;
                    }
                }

                // Propagate predicted RMS & crest across this measured transfer curve
                float nextRmsDb = p.PredictOutputRmsDb(runningRmsDb);
                runningCrestDb = SpatialCortexMath.Clamp(runningCrestDb + p.CrestDeltaDb, 3.0f, 24.0f);
                runningRmsDb = nextRmsDb;
            }

            float rawPredictedPeakDbTp = runningRmsDb + runningCrestDb;
            if (hasTruePeakGuard && rawPredictedPeakDbTp > strictestCeilingDbTp) {
                rawPredictedPeakDbTp = strictestCeilingDbTp;
            }

            float overshootDb = rawPredictedPeakDbTp - strictestCeilingDbTp;
            float recommendedTrimDb = overshootDb > 0f ? -overshootDb : 0f;
            if (overshootDb > 0f) {
                conflicts |= ComponentConflictFlags.TruePeakCeiling;
            }

            // Cap remaining safe makeup gain so governed peak stays <= -1.0 dBTP
            float headroomAvailableDb = Math.Max(0f, strictestCeilingDbTp - (runningRmsDb + recommendedTrimDb + runningCrestDb));
            float maxSafeMakeupDb = SpatialCortexMath.Clamp(
                headroomAvailableDb + (makeupStageCount > 1 ? 0f : 3.0f),
                0f,
                6.0f);
            if (cumulativeMakeupDb > 6.0f) {
                maxSafeMakeupDb = Math.Min(maxSafeMakeupDb, Math.Max(0f, 6.0f - (cumulativeMakeupDb - 6.0f)));
            }

            float governedOutRmsDb = runningRmsDb + recommendedTrimDb;
            float governedPeakDbTp = Math.Min(strictestCeilingDbTp, governedOutRmsDb + runningCrestDb);
            float netGainDb = governedOutRmsDb - referenceInputRmsDb;
            bool tpSafe = governedPeakDbTp <= TargetCeilingDbTp + 0.05f;

            return new ChainGovernancePlan(
                recommendedTrimDb,
                maxSafeMakeupDb,
                netGainDb,
                governedOutRmsDb,
                governedPeakDbTp,
                conflicts,
                skipSoftwareAir: hwAirActive,
                skipSoftwareDrive: hwDriveActive,
                skipSyntheticRoom: isMeasuredBrirActive && roomStageCount > 1,
                isTruePeakSafe: tpSafe,
                evaluatedStageCount: count);
        }
    }
}
