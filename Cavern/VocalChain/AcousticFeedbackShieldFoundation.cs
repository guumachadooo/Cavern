using System;
using System.Threading;

using Cavern.ProAudio;

namespace Cavern.VocalChain {
    /// <summary>
    /// Immutable lock-free telemetry snapshot published on <see cref="OmniscientVocalStateBus"/>
    /// bridging Plane 2 hard real-time audio (&lt;1 ms/block, 0 B GC) and Plane 3 async control (15–30 ms).
    /// </summary>
    public struct OmniscientVocalStateSnapshot {
        /// <summary>Instantaneous Echo Return Loss Enhancement (dB) achieved by the Multi-Ref FDKF shield.</summary>
        public float SpeakerBleedErleDb;

        /// <summary>Estimated peak closed-loop speaker-to-mic acoustic coupling gain (linear).</summary>
        public float EstimatedLoopGainLinear;

        /// <summary>Estimated closed-loop Gain-Before-Feedback safety margin (dB).</summary>
        public float EstimatedLoopMarginDb;

        /// <summary>Maximum safe vocal makeup gain permitted by <see cref="AcousticConstraintGovernor"/> (dB).</summary>
        public float GovernorMaxMakeupDb;

        /// <summary>Total number of background-to-foreground shadow filter commits since reset.</summary>
        public long ShadowCommitCount;

        /// <summary>True when active vocal double-talk or near-end speech is detected (freezing foreground divergence).</summary>
        public bool DoubleTalkActive;

        /// <summary>Latest observed <see cref="HardwarePreAmpStateSnapshot.GainStepEpoch"/>.</summary>
        public long LastGainStepEpoch;

        /// <summary>True when headphone monitoring is active (relaxing speaker feedback ceilings to full studio headroom).</summary>
        public bool IsHeadphoneMonitoring;

        /// <summary>True when the speaker-bleed shield is bypassed (e.g., headphone-only mode or explicit bypass).</summary>
        public bool IsShieldBypassed;

        /// <summary>True only when deferred Phase-B user calibration / NPU director has been explicitly activated.</summary>
        public bool PhaseBActive;

        /// <summary>Neutral default snapshot with Phase B inactive and full legacy compatibility.</summary>
        public static OmniscientVocalStateSnapshot Default {
            get {
                return new OmniscientVocalStateSnapshot {
                    SpeakerBleedErleDb = 0f,
                    EstimatedLoopGainLinear = 0f,
                    EstimatedLoopMarginDb = 24f,
                    GovernorMaxMakeupDb = 12f,
                    ShadowCommitCount = 0,
                    DoubleTalkActive = false,
                    LastGainStepEpoch = 0,
                    IsHeadphoneMonitoring = true,
                    IsShieldBypassed = true,
                    PhaseBActive = false
                };
            }
        }
    }

    /// <summary>
    /// Lock-free Plane 2 &lt;-&gt; Plane 3 vocal cortex state bus.
    /// Zero heap allocations on <see cref="PublishZeroGc"/> and <see cref="ReadSnapshot"/>.
    /// </summary>
    public sealed class OmniscientVocalStateBus {
        // Double-buffered slots + atomic generation counter for tear-free zero-alloc struct exchange
        readonly OmniscientVocalStateSnapshot[] slots = new OmniscientVocalStateSnapshot[2];
        int activeSlot;

        /// <summary>Creates the lock-free vocal state bus initialized to safe Phase-A defaults.</summary>
        public OmniscientVocalStateBus() {
            OmniscientVocalStateSnapshot def = OmniscientVocalStateSnapshot.Default;
            slots[0] = def;
            slots[1] = def;
            activeSlot = 0;
        }

        /// <summary>Publishes a fresh snapshot from the real-time audio thread with zero heap allocations and zero locks.</summary>
        public void PublishZeroGc(in OmniscientVocalStateSnapshot snapshot) {
            int next = (Volatile.Read(ref activeSlot) ^ 1) & 1;
            slots[next] = snapshot;
            Volatile.Write(ref activeSlot, next);
        }

        /// <summary>Reads the latest published state snapshot lock-free without allocating.</summary>
        public OmniscientVocalStateSnapshot ReadSnapshot() {
            int idx = Volatile.Read(ref activeSlot) & 1;
            return slots[idx];
        }
    }

    /// <summary>
    /// Phase-B readiness contract for deferred interactive user calibration / preference tuning
    /// and optional local NPU semantic control. During Autonomous Phase A, <see cref="IsCalibrated"/>
    /// remains false and zero user prompts are ever triggered.
    /// </summary>
    public interface IPhaseBCortexCalibrationHook {
        /// <summary>Unique provider identifier.</summary>
        string ProviderId { get; }

        /// <summary>True only when the user has explicitly completed optional Phase-B calibration.</summary>
        bool IsCalibrated { get; }

        /// <summary>Optional bounded vocal-chain bias adjustment (-3..+3 dB) returned only when <see cref="IsCalibrated"/> is true.</summary>
        float GetPreferenceMakeupBiasDb();
    }

    /// <summary>Default passive Phase-A stub keeping 100% deterministic legacy behavior active side-by-side.</summary>
    public sealed class NullPhaseBCortexCalibrationHook : IPhaseBCortexCalibrationHook {
        /// <summary>Singleton passive Phase-A hook instance.</summary>
        public static readonly NullPhaseBCortexCalibrationHook Instance = new NullPhaseBCortexCalibrationHook();

        NullPhaseBCortexCalibrationHook() { }

        /// <inheritdoc/>
        public string ProviderId {
            get { return "phase-a-passive-stub"; }
        }

        /// <inheritdoc/>
        public bool IsCalibrated {
            get { return false; }
        }

        /// <inheritdoc/>
        public float GetPreferenceMakeupBiasDb() {
            return 0f;
        }
    }

    /// <summary>
    /// Closed-loop acoustic stability governor (<c>Phase 3 Pillar B</c>) preventing suicidal compressor/EQ
    /// feedback runaway when singing over live speakers. Dynamically computes per-band and total makeup-gain
    /// ceilings from FDKF-identified acoustic coupling <c>|H_room(w)|</c> so <c>|H_room(w) * G_total(w)| &lt;= 1 - m_safety</c>,
    /// and automatically relaxes to full studio freedom in headphone monitoring mode.
    /// Zero heap allocations on the real-time audio path.
    /// </summary>
    public sealed class AcousticConstraintGovernor {
        /// <summary>Number of monitored acoustic modal sub-bands.</summary>
        public const int SubBandCount = 8;

        /// <summary>Minimum closed-loop safety margin (dB) enforced in live speaker mode.</summary>
        public float SafetyMarginDb { get; set; }

        /// <summary>Maximum permitted vocal makeup gain in unrestricted headphone mode (dB).</summary>
        public float HeadphoneMaxMakeupDb { get; set; }

        /// <summary>Latest per-sub-band safe gain ceilings (dB).</summary>
        readonly float[] bandMaxGainDb = new float[SubBandCount];

        /// <summary>Latest overall safe makeup gain ceiling (dB).</summary>
        public float CurrentMaxSafeMakeupDb { get; private set; }

        /// <summary>Latest estimated closed-loop Gain-Before-Feedback margin (dB).</summary>
        public float CurrentLoopMarginDb { get; private set; }

        /// <summary>Latest estimated peak acoustic coupling magnitude across all modal bands.</summary>
        public float PeakRoomCouplingLinear { get; private set; }

        /// <summary>Creates a closed-loop <see cref="AcousticConstraintGovernor"/> with 6.0 dB default safety margin.</summary>
        public AcousticConstraintGovernor(float safetyMarginDb = 6.0f, float headphoneMaxMakeupDb = 12.0f) {
            SafetyMarginDb = Clamp(safetyMarginDb, 2.0f, 18.0f);
            HeadphoneMaxMakeupDb = Clamp(headphoneMaxMakeupDb, 0f, 18.0f);
            CurrentMaxSafeMakeupDb = HeadphoneMaxMakeupDb;
            CurrentLoopMarginDb = 24f;
            for (int i = 0; i < SubBandCount; i++) {
                bandMaxGainDb[i] = HeadphoneMaxMakeupDb;
            }
        }

        /// <summary>
        /// Updates closed-loop ceilings from the FDKF-identified room transfer magnitude <paramref name="subBandCouplingLinear"/>
        /// and hardware headphone monitoring state with zero heap allocations.
        /// </summary>
        public float EvaluateZeroGc(
            float[] subBandCouplingLinear,
            float requestedMakeupDb,
            bool isHeadphoneMonitoring,
            out float governedMakeupDb) {
            float req = Clamp(requestedMakeupDb, -18f, HeadphoneMaxMakeupDb);

            if (isHeadphoneMonitoring) {
                PeakRoomCouplingLinear = 0f;
                CurrentMaxSafeMakeupDb = HeadphoneMaxMakeupDb;
                CurrentLoopMarginDb = 24f;
                for (int i = 0; i < SubBandCount; i++) {
                    bandMaxGainDb[i] = HeadphoneMaxMakeupDb;
                }
                governedMakeupDb = req;
                return governedMakeupDb;
            }

            float peakH = 1e-4f;
            int n = subBandCouplingLinear != null ? Math.Min(SubBandCount, subBandCouplingLinear.Length) : 0;
            for (int i = 0; i < SubBandCount; i++) {
                float h = i < n && IsFinite(subBandCouplingLinear[i]) ? Math.Max(1e-4f, subBandCouplingLinear[i]) : 1e-4f;
                if (h > peakH) {
                    peakH = h;
                }
                float hDb = LinearToDb(h);
                // |H(w)| * G(w) <= 10^(-SafetyMarginDb/20) => G_max(dB) = -hDb - SafetyMarginDb
                bandMaxGainDb[i] = Clamp(-hDb - SafetyMarginDb, -18f, HeadphoneMaxMakeupDb);
            }

            PeakRoomCouplingLinear = peakH;
            float peakHDb = LinearToDb(peakH);
            float safeMaxDb = Clamp(-peakHDb - SafetyMarginDb, -18f, HeadphoneMaxMakeupDb);
            CurrentMaxSafeMakeupDb = safeMaxDb;
            governedMakeupDb = Math.Min(req, safeMaxDb);

            // Effective closed-loop margin = -(peakHDb + governedMakeupDb)
            CurrentLoopMarginDb = -(peakHDb + governedMakeupDb);
            return governedMakeupDb;
        }

        /// <summary>Copies per-sub-band gain ceilings (dB) into <paramref name="destination"/> zero-GC.</summary>
        public void CopyBandCeilingsZeroGc(float[] destination) {
            if (destination == null) {
                return;
            }
            int len = Math.Min(SubBandCount, destination.Length);
            for (int i = 0; i < len; i++) {
                destination[i] = bandMaxGainDb[i];
            }
        }

        static bool IsFinite(float v) {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static float Clamp(float v, float min, float max) {
            return v < min ? min : (v > max ? max : v);
        }

        static float LinearToDb(float lin) {
            return lin > 1e-6f ? (float)(20.0 * Math.Log10(lin)) : -120f;
        }
    }

    /// <summary>
    /// Plane 2 hard real-time (<c>&lt;1 ms/block</c>, <c>0 B/frame</c> GC, <c>0</c> locks)
    /// Multi-Reference Partitioned Frequency-Domain Kalman Filter (<c>FDKF</c>) with Two-Path Shadow Filter
    /// (Background/Foreground) and Scarlett Gen 4 <see cref="HardwarePreAmpStateSnapshot.GainStepEpoch"/>
    /// instant covariance reset (<c>Phase 3 Slot 2D-SHIELD</c> Phase-A foundation).
    /// </summary>
    public sealed class MultiRefFdkfShield {
        /// <summary>Default hop/block size in samples (128 samples = 2.67 ms @ 48 kHz).</summary>
        public const int DefaultBlockSize = 128;

        /// <summary>Maximum supported simultaneous reference channels (Music_L, Music_R, PostFx_Voice_Out).</summary>
        public const int MaxReferences = 3;

        readonly int blockSize;
        readonly int fftSize;
        readonly int halfBins;
        readonly int partitions;
        readonly int refCount;

        // Pre-allocated time-domain overlap-save buffers per reference
        readonly float[][] refPrevHalf;
        readonly float[] fftRe;
        readonly float[] fftIm;
        readonly float[] fgYRe;
        readonly float[] fgYIm;
        readonly float[] bgYRe;
        readonly float[] bgYIm;
        readonly float[] fgErrTime;
        readonly float[] bgErrTime;
        readonly float[] micBlockScratch;

        // Frequency-domain FDL (Frequency-Domain Delay Line): [ref][part][bin]
        readonly float[][][] xRe;
        readonly float[][][] xIm;
        readonly int[] fdlHead;

        // Foreground (committed) & Background (shadow adaptive) weights + diagonal Kalman covariance P
        readonly float[][][] wFgRe;
        readonly float[][][] wFgIm;
        readonly float[][][] wBgRe;
        readonly float[][][] wBgIm;
        readonly float[][][] covP;

        // Per-bin smoothed error & power trackers
        readonly float[] obsNoiseS;
        readonly float[] subBandCoupling = new float[AcousticConstraintGovernor.SubBandCount];

        // Twiddle tables for zero-alloc radix-2 FFT
        readonly float[] cosTable;
        readonly float[] sinTable;
        readonly int[] bitRev;

        float smoothedMicPow = 1e-6f;
        float smoothedFgErrPow = 1e-6f;
        float smoothedBgErrPow = 1e-6f;
        float smoothedRefPow = 1e-6f;
        float lastErleDb;
        long shadowCommitCount;
        long lastGainStepEpoch = -1;
        bool doubleTalkActive;
        bool isBypassed;

        // Kalman process-noise & forgetting parameters (tuned for rapid convergence + double-talk stability)
        const float ProcessNoiseFactor = 0.0008f;
        const float InitialCovariance = 0.25f;
        const float MinCovariance = 1e-5f;
        const float MaxCovariance = 1.5f;
        const float RegularizationEps = 1e-6f;

        /// <summary>Block/hop size in samples processed per FDKF update.</summary>
        public int BlockSize {
            get { return blockSize; }
        }

        /// <summary>Number of frequency-domain partitions (total modeled RIR length = <c>BlockSize * Partitions</c>).</summary>
        public int Partitions {
            get { return partitions; }
        }

        /// <summary>Number of active digital reference channels (2 or 3).</summary>
        public int ReferenceCount {
            get { return refCount; }
        }

        /// <summary>Latest measured Echo Return Loss Enhancement in dB.</summary>
        public float LastErleDb {
            get { return lastErleDb; }
        }

        /// <summary>Total number of background-to-foreground shadow commits.</summary>
        public long ShadowCommitCount {
            get { return shadowCommitCount; }
        }

        /// <summary>True when near-end vocal double-talk was detected on the latest block.</summary>
        public bool DoubleTalkActive {
            get { return doubleTalkActive; }
        }

        /// <summary>Latest observed hardware preamp gain-step epoch.</summary>
        public long LastGainStepEpoch {
            get { return lastGainStepEpoch; }
        }

        /// <summary>True when speaker-bleed cancellation was bypassed (e.g., headphone mode).</summary>
        public bool IsBypassed {
            get { return isBypassed; }
        }

        /// <summary>
        /// Creates and pre-allocates a zero-GC Multi-Reference Partitioned FDKF + Two-Path Shadow Filter.
        /// </summary>
        public MultiRefFdkfShield(int blockSize = DefaultBlockSize, int partitions = 8, int referenceCount = 3) {
            this.blockSize = NextPowerOfTwo(Math.Max(64, Math.Min(512, blockSize)));
            this.fftSize = this.blockSize * 2;
            this.halfBins = this.fftSize / 2 + 1;
            this.partitions = Math.Max(2, Math.Min(24, partitions));
            this.refCount = Math.Max(1, Math.Min(MaxReferences, referenceCount));

            refPrevHalf = new float[refCount][];
            xRe = new float[refCount][][];
            xIm = new float[refCount][][];
            wFgRe = new float[refCount][][];
            wFgIm = new float[refCount][][];
            wBgRe = new float[refCount][][];
            wBgIm = new float[refCount][][];
            covP = new float[refCount][][];
            fdlHead = new int[refCount];

            for (int r = 0; r < refCount; r++) {
                refPrevHalf[r] = new float[this.blockSize];
                xRe[r] = new float[this.partitions][];
                xIm[r] = new float[this.partitions][];
                wFgRe[r] = new float[this.partitions][];
                wFgIm[r] = new float[this.partitions][];
                wBgRe[r] = new float[this.partitions][];
                wBgIm[r] = new float[this.partitions][];
                covP[r] = new float[this.partitions][];
                for (int p = 0; p < this.partitions; p++) {
                    xRe[r][p] = new float[halfBins];
                    xIm[r][p] = new float[halfBins];
                    wFgRe[r][p] = new float[halfBins];
                    wFgIm[r][p] = new float[halfBins];
                    wBgRe[r][p] = new float[halfBins];
                    wBgIm[r][p] = new float[halfBins];
                    covP[r][p] = new float[halfBins];
                }
            }

            fftRe = new float[fftSize];
            fftIm = new float[fftSize];
            fgYRe = new float[halfBins];
            fgYIm = new float[halfBins];
            bgYRe = new float[halfBins];
            bgYIm = new float[halfBins];
            fgErrTime = new float[this.blockSize];
            bgErrTime = new float[this.blockSize];
            micBlockScratch = new float[this.blockSize];
            obsNoiseS = new float[halfBins];

            cosTable = new float[fftSize / 2];
            sinTable = new float[fftSize / 2];
            for (int i = 0; i < fftSize / 2; i++) {
                double ang = -2.0 * Math.PI * i / fftSize;
                cosTable[i] = (float)Math.Cos(ang);
                sinTable[i] = (float)Math.Sin(ang);
            }
            bitRev = BuildBitReverseTable(fftSize);

            Reset();
        }

        /// <summary>Clears all filter weights, delay lines, and resets Kalman covariance cleanly.</summary>
        public void Reset() {
            for (int r = 0; r < refCount; r++) {
                Array.Clear(refPrevHalf[r], 0, blockSize);
                fdlHead[r] = 0;
                for (int p = 0; p < partitions; p++) {
                    Array.Clear(xRe[r][p], 0, halfBins);
                    Array.Clear(xIm[r][p], 0, halfBins);
                    Array.Clear(wFgRe[r][p], 0, halfBins);
                    Array.Clear(wFgIm[r][p], 0, halfBins);
                    Array.Clear(wBgRe[r][p], 0, halfBins);
                    Array.Clear(wBgIm[r][p], 0, halfBins);
                    for (int k = 0; k < halfBins; k++) {
                        covP[r][p][k] = InitialCovariance;
                    }
                }
            }
            for (int k = 0; k < halfBins; k++) {
                obsNoiseS[k] = 1e-4f;
            }
            Array.Clear(subBandCoupling, 0, subBandCoupling.Length);
            smoothedMicPow = 1e-6f;
            smoothedFgErrPow = 1e-6f;
            smoothedBgErrPow = 1e-6f;
            smoothedRefPow = 1e-6f;
            lastErleDb = 0f;
            shadowCommitCount = 0;
            doubleTalkActive = false;
        }

        /// <summary>
        /// Resets Kalman state-error covariance <c>P</c> to rapid-adaptation mode without erasing current weights
        /// (triggered instantaneously when Scarlett preamp <see cref="HardwarePreAmpStateSnapshot.GainStepEpoch"/> changes).
        /// </summary>
        public void ResetCovarianceZeroGc() {
            for (int r = 0; r < refCount; r++) {
                for (int p = 0; p < partitions; p++) {
                    float[] cov = covP[r][p];
                    for (int k = 0; k < halfBins; k++) {
                        cov[k] = InitialCovariance;
                    }
                }
            }
            smoothedFgErrPow = Math.Max(smoothedFgErrPow, smoothedMicPow * 0.5f);
            smoothedBgErrPow = smoothedFgErrPow;
        }

        /// <summary>
        /// Processes a mono microphone block of exact length <see cref="BlockSize"/> against 2 or 3
        /// time-aligned digital reference blocks (<paramref name="refMusicL"/>, <paramref name="refMusicR"/>,
        /// optional <paramref name="refVoiceFeedback"/>) with ZERO heap allocations and ZERO locks.
        /// Automatically bypasses in headphone mode and resets covariance on preamp gain steps.
        /// </summary>
        public void ProcessBlockZeroGc(
            float[] micInOut,
            int micOffset,
            float[] refMusicL,
            int refMusicLOffset,
            float[] refMusicR,
            int refMusicROffset,
            float[] refVoiceFeedback = null,
            int refVoiceOffset = 0,
            HardwarePreAmpStateSnapshot preAmp = null,
            AcousticConstraintGovernor governor = null,
            OmniscientVocalStateBus stateBus = null,
            bool forceBypass = false) {
            if (micInOut == null || micOffset < 0 || micOffset + blockSize > micInOut.Length) {
                return;
            }

            bool headphoneMode = preAmp != null && preAmp.IsHeadphoneMonitoring;
            if (preAmp != null) {
                if (lastGainStepEpoch >= 0 && preAmp.GainStepEpoch != lastGainStepEpoch) {
                    ResetCovarianceZeroGc();
                }
                lastGainStepEpoch = preAmp.GainStepEpoch;
            }

            if (forceBypass || headphoneMode) {
                isBypassed = true;
                doubleTalkActive = false;
                if (governor != null) {
                    float dummy;
                    governor.EvaluateZeroGc(subBandCoupling, governor.HeadphoneMaxMakeupDb, isHeadphoneMonitoring: true, out dummy);
                }
                PublishStateZeroGc(stateBus, governor, headphoneMode, bypassed: true);
                return;
            }
            isBypassed = false;

            // Step 1: Transform each active reference into the frequency-domain delay line (overlap-save 50%)
            float blockRefPow = 0f;
            blockRefPow += PushReferenceBlockZeroGc(0, refMusicL, refMusicLOffset);
            if (refCount >= 2) {
                blockRefPow += PushReferenceBlockZeroGc(1, refMusicR, refMusicROffset);
            }
            if (refCount >= 3) {
                blockRefPow += PushReferenceBlockZeroGc(2, refVoiceFeedback, refVoiceOffset);
            }
            blockRefPow /= (blockSize * Math.Max(1, refCount));

            // Step 2: Convolve foreground & background partitioned frequency-domain filters
            Array.Clear(fgYRe, 0, halfBins);
            Array.Clear(fgYIm, 0, halfBins);
            Array.Clear(bgYRe, 0, halfBins);
            Array.Clear(bgYIm, 0, halfBins);

            for (int r = 0; r < refCount; r++) {
                int head = fdlHead[r];
                for (int p = 0; p < partitions; p++) {
                    int slot = head - p;
                    if (slot < 0) {
                        slot += partitions;
                    }
                    float[] xr = xRe[r][slot];
                    float[] xi = xIm[r][slot];
                    float[] fwr = wFgRe[r][p];
                    float[] fwi = wFgIm[r][p];
                    float[] bwr = wBgRe[r][p];
                    float[] bwi = wBgIm[r][p];
                    for (int k = 0; k < halfBins; k++) {
                        float xrv = xr[k];
                        float xiv = xi[k];
                        fgYRe[k] += xrv * fwr[k] - xiv * fwi[k];
                        fgYIm[k] += xrv * fwi[k] + xiv * fwr[k];
                        bgYRe[k] += xrv * bwr[k] - xiv * bwi[k];
                        bgYIm[k] += xrv * bwi[k] + xiv * bwr[k];
                    }
                }
            }

            // Step 3: Inverse FFT to obtain time-domain echo estimates (second half = valid linear convolution)
            float blockMicPow = 0f;
            for (int i = 0; i < blockSize; i++) {
                float m = micInOut[micOffset + i];
                if (!IsFinite(m)) {
                    m = 0f;
                }
                micBlockScratch[i] = m;
                blockMicPow += m * m;
            }
            blockMicPow /= blockSize;

            InverseHalfSpectrumToSecondHalf(fgYRe, fgYIm);
            float blockFgErrPow = 0f;
            for (int i = 0; i < blockSize; i++) {
                float e = micBlockScratch[i] - fftRe[blockSize + i];
                fgErrTime[i] = e;
                blockFgErrPow += e * e;
            }
            blockFgErrPow /= blockSize;

            InverseHalfSpectrumToSecondHalf(bgYRe, bgYIm);
            float blockBgErrPow = 0f;
            for (int i = 0; i < blockSize; i++) {
                float e = micBlockScratch[i] - fftRe[blockSize + i];
                bgErrTime[i] = e;
                blockBgErrPow += e * e;
            }
            blockBgErrPow /= blockSize;

            // Step 4: Frequency-domain Kalman update strictly on the BACKGROUND shadow filter
            // (zero-padded first half + bgErrTime in second half -> gradient constraint via overlap-save)
            for (int i = 0; i < blockSize; i++) {
                fftRe[i] = 0f;
                fftIm[i] = 0f;
                fftRe[blockSize + i] = bgErrTime[i];
                fftIm[blockSize + i] = 0f;
            }
            FftInPlace(inverse: false);

            // Detect near-end vocal double-talk: mic energy significantly above predicted bleed or bg residual surge
            smoothedRefPow = 0.90f * smoothedRefPow + 0.10f * blockRefPow;
            smoothedMicPow = 0.85f * smoothedMicPow + 0.15f * blockMicPow;
            smoothedFgErrPow = 0.85f * smoothedFgErrPow + 0.15f * blockFgErrPow;
            smoothedBgErrPow = 0.85f * smoothedBgErrPow + 0.15f * blockBgErrPow;
            doubleTalkActive = blockRefPow > 1e-7f && blockFgErrPow > 0.35f * blockMicPow && blockBgErrPow > 0.30f * blockMicPow && shadowCommitCount > 6;

            if (blockRefPow > 1e-8f) {
                for (int k = 0; k < halfBins; k++) {
                    float eRe = fftRe[k];
                    float eIm = fftIm[k];
                    float eMagSq = eRe * eRe + eIm * eIm;

                    // Adaptive observation noise floor S_e[k] (slower rise when quiet, fast rise during double-talk)
                    float alphaS = eMagSq > obsNoiseS[k] ? 0.35f : 0.12f;
                    obsNoiseS[k] = (1f - alphaS) * obsNoiseS[k] + alphaS * eMagSq;

                    // Compute denominator = sum_{r,p} |X|^2 * P + S_e + eps
                    float denom = obsNoiseS[k] + RegularizationEps;
                    for (int r = 0; r < refCount; r++) {
                        int head = fdlHead[r];
                        for (int p = 0; p < partitions; p++) {
                            int slot = head - p;
                            if (slot < 0) {
                                slot += partitions;
                            }
                            float xrv = xRe[r][slot][k];
                            float xiv = xIm[r][slot][k];
                            float xPow = xrv * xrv + xiv * xiv;
                            denom += xPow * covP[r][p][k];
                        }
                    }

                    float invDenom = 1f / Math.Max(RegularizationEps, denom);
                    // Scale step slightly back during strong double-talk so shadow stays well-behaved
                    float dtScale = doubleTalkActive ? 0.35f : 0.92f;

                    for (int r = 0; r < refCount; r++) {
                        int head = fdlHead[r];
                        for (int p = 0; p < partitions; p++) {
                            int slot = head - p;
                            if (slot < 0) {
                                slot += partitions;
                            }
                            float xrv = xRe[r][slot][k];
                            float xiv = xIm[r][slot][k];
                            float xPow = xrv * xrv + xiv * xiv;
                            float pVal = covP[r][p][k];
                            float mu = dtScale * pVal * invDenom;

                            // W_bg += mu * conj(X) * E
                            float gRe = mu * (xrv * eRe + xiv * eIm);
                            float gIm = mu * (xrv * eIm - xiv * eRe);
                            wBgRe[r][p][k] += gRe;
                            wBgIm[r][p][k] += gIm;

                            // Diagonal Kalman covariance update + process noise Q for room drift tracking
                            float pNext = pVal * (1f - 0.5f * mu * xPow) + ProcessNoiseFactor * (wBgRe[r][p][k] * wBgRe[r][p][k] + wBgIm[r][p][k] * wBgIm[r][p][k] + 1e-4f);
                            covP[r][p][k] = Clamp(pNext, MinCovariance, MaxCovariance);
                        }
                    }
                }

                // Gradient constraint on background weights every block (time-domain projection onto first BlockSize taps)
                // Keeps overlap-save linear convolution exact with zero circular wrap aliasing.
                EnforceWeightGradientConstraintZeroGc();
            }

            // Step 5: Two-Path Shadow Filter Commit Gate
            // Commit background -> foreground ONLY when background strictly beats foreground under active reference.
            bool commitShadow = blockRefPow > 1e-7f &&
                                (blockBgErrPow < 0.85f * blockFgErrPow || smoothedBgErrPow < 0.90f * smoothedFgErrPow) &&
                                blockBgErrPow < blockMicPow * 0.98f;
            if (commitShadow) {
                CopyWeightsZeroGc(wBgRe, wBgIm, wFgRe, wFgIm);
                shadowCommitCount++;
                // Output improved background residual directly on commit frame
                for (int i = 0; i < blockSize; i++) {
                    micInOut[micOffset + i] = bgErrTime[i];
                }
                smoothedFgErrPow = smoothedBgErrPow;
            } else {
                // If background diverged violently during extreme double-talk, re-seed background from safe foreground
                if (smoothedBgErrPow > smoothedFgErrPow * 2.5f && shadowCommitCount > 2) {
                    CopyWeightsZeroGc(wFgRe, wFgIm, wBgRe, wBgIm);
                    smoothedBgErrPow = smoothedFgErrPow;
                }
                for (int i = 0; i < blockSize; i++) {
                    micInOut[micOffset + i] = fgErrTime[i];
                }
            }

            // Step 6: Update ERLE & identified acoustic room coupling |H_room(w)| for AcousticConstraintGovernor
            if (smoothedMicPow > 1e-8f && smoothedFgErrPow > 1e-10f) {
                lastErleDb = Clamp((float)(10.0 * Math.Log10(smoothedMicPow / smoothedFgErrPow)), -6f, 60f);
            }
            UpdateIdentifiedRoomCouplingZeroGc();
            if (governor != null) {
                float dummy;
                governor.EvaluateZeroGc(subBandCoupling, governor.HeadphoneMaxMakeupDb, isHeadphoneMonitoring: false, out dummy);
            }
            PublishStateZeroGc(stateBus, governor, headphoneMode: false, bypassed: false);
        }

        /// <summary>Copies the FDKF-identified per-sub-band acoustic coupling magnitude <c>|H_room(w)|</c> zero-GC.</summary>
        public void CopyIdentifiedCouplingZeroGc(float[] destination) {
            if (destination == null) {
                return;
            }
            int len = Math.Min(subBandCoupling.Length, destination.Length);
            for (int i = 0; i < len; i++) {
                destination[i] = subBandCoupling[i];
            }
        }

        float PushReferenceBlockZeroGc(int refIdx, float[] refBuf, int offset) {
            int head = fdlHead[refIdx] + 1;
            if (head >= partitions) {
                head = 0;
            }
            fdlHead[refIdx] = head;

            float[] prev = refPrevHalf[refIdx];
            float pow = 0f;
            bool hasData = refBuf != null && offset >= 0 && offset + blockSize <= refBuf.Length;

            for (int i = 0; i < blockSize; i++) {
                fftRe[i] = prev[i];
                fftIm[i] = 0f;
                float s = hasData ? refBuf[offset + i] : 0f;
                if (!IsFinite(s)) {
                    s = 0f;
                }
                fftRe[blockSize + i] = s;
                fftIm[blockSize + i] = 0f;
                prev[i] = s;
                pow += s * s;
            }

            FftInPlace(inverse: false);
            float[] dstRe = xRe[refIdx][head];
            float[] dstIm = xIm[refIdx][head];
            for (int k = 0; k < halfBins; k++) {
                dstRe[k] = fftRe[k];
                dstIm[k] = fftIm[k];
            }
            return pow;
        }

        void EnforceWeightGradientConstraintZeroGc() {
            for (int r = 0; r < refCount; r++) {
                for (int p = 0; p < partitions; p++) {
                    float[] wr = wBgRe[r][p];
                    float[] wi = wBgIm[r][p];
                    InverseHalfSpectrumToSecondHalf(wr, wi);
                    // Zero second half (keep only causal first BlockSize taps), then re-FFT
                    for (int i = blockSize; i < fftSize; i++) {
                        fftRe[i] = 0f;
                        fftIm[i] = 0f;
                    }
                    for (int i = 0; i < blockSize; i++) {
                        fftIm[i] = 0f;
                    }
                    FftInPlace(inverse: false);
                    for (int k = 0; k < halfBins; k++) {
                        wr[k] = fftRe[k];
                        wi[k] = fftIm[k];
                    }
                }
            }
        }

        void UpdateIdentifiedRoomCouplingZeroGc() {
            // Sum coherent partition frequency responses on the feedback/primary speaker reference
            int feedbackRef = refCount >= 3 ? 2 : 0;
            int binsPerBand = Math.Max(1, (halfBins - 1) / AcousticConstraintGovernor.SubBandCount);
            for (int b = 0; b < AcousticConstraintGovernor.SubBandCount; b++) {
                int k0 = 1 + b * binsPerBand;
                int k1 = (b == AcousticConstraintGovernor.SubBandCount - 1) ? halfBins : Math.Min(halfBins, k0 + binsPerBand);
                float maxMag = 0f;
                for (int k = k0; k < k1; k++) {
                    // Upper bound on acoustic transfer gain across partitions at bin k
                    float sumRe = 0f;
                    float sumIm = 0f;
                    float rssMagSq = 0f;
                    for (int p = 0; p < partitions; p++) {
                        float wr = wFgRe[feedbackRef][p][k];
                        float wi = wFgIm[feedbackRef][p][k];
                        sumRe += wr;
                        sumIm += wi;
                        rssMagSq += wr * wr + wi * wi;
                    }
                    float cohMag = MathF.Sqrt(sumRe * sumRe + sumIm * sumIm);
                    float rssMag = MathF.Sqrt(rssMagSq);
                    float mag = Math.Max(cohMag, rssMag);
                    if (mag > maxMag) {
                        maxMag = mag;
                    }
                }
                subBandCoupling[b] = maxMag;
            }
        }

        void PublishStateZeroGc(OmniscientVocalStateBus bus, AcousticConstraintGovernor governor, bool headphoneMode, bool bypassed) {
            if (bus == null) {
                return;
            }
            OmniscientVocalStateSnapshot snap = new OmniscientVocalStateSnapshot {
                SpeakerBleedErleDb = lastErleDb,
                EstimatedLoopGainLinear = governor != null ? governor.PeakRoomCouplingLinear : 0f,
                EstimatedLoopMarginDb = governor != null ? governor.CurrentLoopMarginDb : 24f,
                GovernorMaxMakeupDb = governor != null ? governor.CurrentMaxSafeMakeupDb : 12f,
                ShadowCommitCount = shadowCommitCount,
                DoubleTalkActive = doubleTalkActive,
                LastGainStepEpoch = lastGainStepEpoch,
                IsHeadphoneMonitoring = headphoneMode,
                IsShieldBypassed = bypassed,
                PhaseBActive = false
            };
            bus.PublishZeroGc(in snap);
        }

        void InverseHalfSpectrumToSecondHalf(float[] halfRe, float[] halfIm) {
            fftRe[0] = halfRe[0];
            fftIm[0] = 0f;
            int nyq = fftSize / 2;
            fftRe[nyq] = halfRe[nyq];
            fftIm[nyq] = 0f;
            for (int k = 1; k < nyq; k++) {
                float r = halfRe[k];
                float im = halfIm[k];
                fftRe[k] = r;
                fftIm[k] = im;
                fftRe[fftSize - k] = r;
                fftIm[fftSize - k] = -im;
            }
            FftInPlace(inverse: true);
        }

        void FftInPlace(bool inverse) {
            int n = fftSize;
            for (int i = 0; i < n; i++) {
                int j = bitRev[i];
                if (i < j) {
                    float tr = fftRe[i];
                    fftRe[i] = fftRe[j];
                    fftRe[j] = tr;
                    float ti = fftIm[i];
                    fftIm[i] = fftIm[j];
                    fftIm[j] = ti;
                }
            }

            float sign = inverse ? -1f : 1f;
            for (int len = 2; len <= n; len <<= 1) {
                int half = len >> 1;
                int step = n / len;
                for (int i = 0; i < n; i += len) {
                    int tw = 0;
                    for (int k = 0; k < half; k++) {
                        float wRe = cosTable[tw];
                        float wIm = sign * sinTable[tw];
                        int u = i + k;
                        int v = u + half;
                        float tRe = fftRe[v] * wRe - fftIm[v] * wIm;
                        float tIm = fftRe[v] * wIm + fftIm[v] * wRe;
                        fftRe[v] = fftRe[u] - tRe;
                        fftIm[v] = fftIm[u] - tIm;
                        fftRe[u] += tRe;
                        fftIm[u] += tIm;
                        tw += step;
                    }
                }
            }

            if (inverse) {
                float invN = 1f / n;
                for (int i = 0; i < n; i++) {
                    fftRe[i] *= invN;
                    fftIm[i] *= invN;
                }
            }
        }

        void CopyWeightsZeroGc(float[][][] srcRe, float[][][] srcIm, float[][][] dstRe, float[][][] dstIm) {
            for (int r = 0; r < refCount; r++) {
                for (int p = 0; p < partitions; p++) {
                    Array.Copy(srcRe[r][p], dstRe[r][p], halfBins);
                    Array.Copy(srcIm[r][p], dstIm[r][p], halfBins);
                }
            }
        }

        static int[] BuildBitReverseTable(int n) {
            int[] table = new int[n];
            int bits = 0;
            for (int temp = n; temp > 1; temp >>= 1) {
                bits++;
            }
            for (int i = 0; i < n; i++) {
                int rev = 0;
                int x = i;
                for (int b = 0; b < bits; b++) {
                    rev = (rev << 1) | (x & 1);
                    x >>= 1;
                }
                table[i] = rev;
            }
            return table;
        }

        static int NextPowerOfTwo(int v) {
            int p = 1;
            while (p < v) {
                p <<= 1;
            }
            return p;
        }

        static bool IsFinite(float v) {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        static float Clamp(float v, float min, float max) {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
