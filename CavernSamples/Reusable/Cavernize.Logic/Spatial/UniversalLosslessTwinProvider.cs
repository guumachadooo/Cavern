using Cavern.Restoration;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Universal Slot 1 Lossless Twin Service implementing <see cref="ILosslessReferenceProvider"/> (W09).
/// Unifies a verified lossless FLAC/WAV/ALAC stereo reference to <c>float32 [-1, +1]</c>, matches sample rate via
/// <see cref="KaiserSincResampler"/> (<c>&gt; 120 dB</c> stopband), runs 3-stage <see cref="SubSampleSyncEngine"/>
/// (Macro <c>±15 s</c> + Sliding <c>GCC-PHAT &lt; 0.05 samp</c> + Theil-Sen ppm drift tracker), and serves zero-GC
/// fractionally aligned stereo blocks via <see cref="FarrowLagrangeDelay"/> to ALL downstream stages:
/// <list type="bullet">
///   <item>Slot 2C <see cref="Adapters.LgsrStageProcessor"/> (Tier 1 Pre-HRTF Coherence Fusion &amp; Tier 2 EM-MWF Studio Master)</item>
///   <item>Slot 2B <see cref="Adapters.NeuralStemStageProcessor"/> (Spatial Karaoke 1x lossless vocal reference &amp; Clean Stems)</item>
///   <item>Slot 6B <see cref="Adapters.SpatialDialogPostProcessingStage"/> (Lossless-assisted speech coherence)</item>
///   <item>W11 Auto-Vocal Mix-Match &amp; W13 Spatial X-Ray Inspector</item>
/// </list>
/// </summary>
public sealed class UniversalLosslessTwinProvider : ILosslessReferenceProvider, IDisposable {
    sealed class SyncSnapshot {
        public static readonly SyncSnapshot Empty = new(
            LosslessTwinVerificationState.None,
            null,
            48000,
            SubSampleSyncResult.Unsynced(),
            null,
            null);

        public LosslessTwinVerificationState VerificationState { get; }
        public LosslessTwinMetadata Metadata { get; }
        public int TargetSampleRate { get; }
        public SubSampleSyncResult SyncResult { get; }
        public float[] ResampledLeft { get; }
        public float[] ResampledRight { get; }

        public bool IsReadyForAudioThread =>
            VerificationState == LosslessTwinVerificationState.Synced
            && SyncResult.IsSynced
            && ResampledLeft != null
            && ResampledRight != null
            && ResampledLeft.Length >= 4
            && ResampledRight.Length == ResampledLeft.Length;

        public SyncSnapshot(
            LosslessTwinVerificationState verificationState,
            LosslessTwinMetadata metadata,
            int targetSampleRate,
            SubSampleSyncResult syncResult,
            float[] resampledLeft,
            float[] resampledRight) {
            VerificationState = verificationState;
            Metadata = metadata;
            TargetSampleRate = targetSampleRate > 0 ? targetSampleRate : 48000;
            SyncResult = syncResult;
            ResampledLeft = resampledLeft;
            ResampledRight = resampledRight;
        }
    }

    readonly object writeLock = new();
    volatile SyncSnapshot activeSnapshot = SyncSnapshot.Empty;

    /// <inheritdoc/>
    public bool HasSyncedLosslessTwin => activeSnapshot.IsReadyForAudioThread;

    /// <inheritdoc/>
    public string TwinFilePath => activeSnapshot.Metadata?.FilePath ?? string.Empty;

    /// <inheritdoc/>
    public int SampleRate => activeSnapshot.TargetSampleRate;

    /// <inheritdoc/>
    public float SyncConfidence => activeSnapshot.SyncResult.Confidence;

    /// <summary>Current verification state (Synced, BypassedAlreadyLossless, BypassedFakeLossless, BypassedLowConfidence, etc.).</summary>
    public LosslessTwinVerificationState VerificationState => activeSnapshot.VerificationState;

    /// <summary>Metadata of the paired lossless twin candidate (or null when unpaired).</summary>
    public LosslessTwinMetadata Metadata => activeSnapshot.Metadata;

    /// <summary>Complete 3-stage sub-sample alignment &amp; Theil-Sen drift solution.</summary>
    public SubSampleSyncResult SyncResult => activeSnapshot.SyncResult;

    /// <summary>Total sample count of the rate-matched lossless reference buffer.</summary>
    public int ReferenceLengthSamples {
        get {
            float[] left = activeSnapshot.ResampledLeft;
            return left != null ? left.Length : 0;
        }
    }

    /// <summary>Updates the verification state cleanly (e.g., Searching, BypassedAlreadyLossless, BypassedFakeLossless) and clears stale buffers.</summary>
    public void SetState(LosslessTwinVerificationState state, LosslessTwinMetadata candidateMetadata = null) {
        lock (writeLock) {
            SyncSnapshot prev = activeSnapshot;
            if (state == LosslessTwinVerificationState.Synced && prev.IsReadyForAudioThread) {
                activeSnapshot = new SyncSnapshot(
                    state,
                    candidateMetadata ?? prev.Metadata,
                    prev.TargetSampleRate,
                    prev.SyncResult,
                    prev.ResampledLeft,
                    prev.ResampledRight);
            } else {
                activeSnapshot = new SyncSnapshot(
                    state,
                    candidateMetadata,
                    prev.TargetSampleRate,
                    SubSampleSyncResult.Unsynced(),
                    null,
                    null);
            }
        }
    }

    /// <summary>
    /// Unifies a decoded lossless stereo twin (<paramref name="losslessLeft"/>, <paramref name="losslessRight"/> at
    /// <paramref name="losslessSampleRate"/>) to <paramref name="atmosSampleRate"/> via linear-phase Kaiser polyphase sinc
    /// resampling (<c>&gt; 120 dB</c> stopband) and runs 3-stage <see cref="SubSampleSyncEngine"/> against the Atmos mono probe.
    /// </summary>
    /// <param name="candidateMeta">Verified lossless candidate metadata.</param>
    /// <param name="atmosProbeMono">Mono downmix of the active Atmos track starting at <paramref name="atmosProbeStartSample"/>.</param>
    /// <param name="atmosProbeStartSample">Sample index inside the Atmos track where <paramref name="atmosProbeMono"/> begins (typically 0).</param>
    /// <param name="atmosSampleRate">Target playback sample rate of the Atmos track (typically 48000 Hz).</param>
    /// <param name="losslessLeft">Decoded unified float32 Left channel of the lossless twin.</param>
    /// <param name="losslessRight">Decoded unified float32 Right channel of the lossless twin.</param>
    /// <param name="losslessSampleRate">Native sample rate of the lossless twin (e.g., 44100, 48000, 96000, 192000 Hz).</param>
    /// <param name="minConfidenceGate">Confidence threshold in [0.15; 0.90] required to accept the twin.</param>
    /// <returns>True when the twin passed the 3-stage sync gate and is now live for all consumers.</returns>
    public bool InitializeAndSync(
        LosslessTwinMetadata candidateMeta,
        float[] atmosProbeMono,
        long atmosProbeStartSample,
        int atmosSampleRate,
        float[] losslessLeft,
        float[] losslessRight,
        int losslessSampleRate,
        float minConfidenceGate = 0.35f) {
        if (losslessLeft == null || losslessRight == null || atmosProbeMono == null ||
            losslessLeft.Length < SubSampleSyncEngine.GccFftSize || atmosProbeMono.Length < SubSampleSyncEngine.GccFftSize) {
            SetState(LosslessTwinVerificationState.BypassedLowConfidence, candidateMeta);
            return false;
        }

        int targetSr = atmosSampleRate > 0 ? atmosSampleRate : 48000;
        int srcSr = losslessSampleRate > 0 ? losslessSampleRate : targetSr;

        // 1. Format unification & linear-phase Kaiser polyphase sinc resampling (>120 dB stopband)
        KaiserSincResampler.ResampleStereo(losslessLeft, losslessRight, srcSr, targetSr, out float[] alignedL, out float[] alignedR);

        // 2. Build mono downmix of the rate-matched lossless twin for 3-stage sub-sample alignment
        int len = Math.Min(alignedL.Length, alignedR.Length);
        float[] losslessMono = new float[len];
        for (int i = 0; i < len; i++) {
            losslessMono[i] = 0.5f * (alignedL[i] + alignedR[i]);
        }

        // 3. Slice a matching lossless probe region if atmosProbeStartSample > 0 so macro ±15s stays centered
        float[] losslessProbe = losslessMono;
        long losslessProbeStart = 0L;
        if (atmosProbeStartSample > 0) {
            long pad = (long)(SubSampleSyncEngine.DefaultMaxMacroSearchSeconds * targetSr);
            long start = Math.Max(0L, atmosProbeStartSample - pad);
            long end = Math.Min(len, atmosProbeStartSample + atmosProbeMono.Length + pad);
            if (end - start >= SubSampleSyncEngine.GccFftSize) {
                losslessProbeStart = start;
                int sliceLen = (int)(end - start);
                losslessProbe = new float[sliceLen];
                Array.Copy(losslessMono, (int)start, losslessProbe, 0, sliceLen);
            }
        }

        // 4. Run 3-stage alignment (Macro ±15s + Sliding GCC-PHAT <0.05 samp + Theil-Sen ppm drift)
        SubSampleSyncResult result = SubSampleSyncEngine.Align(
            atmosProbeMono,
            losslessProbe,
            targetSr,
            SubSampleSyncEngine.DefaultMaxMacroSearchSeconds,
            Math.Clamp(minConfidenceGate, 0.15f, 0.90f));

        // Translate probe-relative intercept back to global track sample index 0:
        // lossless(losslessProbeStart + result.Base + (t - atmosStart) * (1 + drift)) == atmos(t)
        // => globalBaseOffset = losslessProbeStart + result.Base - atmosProbeStartSample * (1 + drift)
        double rateScale = 1.0 + result.DriftPpm * 1e-6;
        result.BaseOffsetSamples = losslessProbeStart + result.BaseOffsetSamples - atmosProbeStartSample * rateScale;
        result.MacroOffsetSamples = losslessProbeStart + result.MacroOffsetSamples - atmosProbeStartSample;

        // Publish immutable snapshot atomically (single volatile reference write, ARM64 release semantics)
        lock (writeLock) {
            if (result.IsSynced) {
                activeSnapshot = new SyncSnapshot(
                    LosslessTwinVerificationState.Synced,
                    candidateMeta,
                    targetSr,
                    result,
                    alignedL,
                    alignedR);
            } else {
                activeSnapshot = new SyncSnapshot(
                    LosslessTwinVerificationState.BypassedLowConfidence,
                    candidateMeta,
                    targetSr,
                    result,
                    null,
                    null);
            }
        }
        return result.IsSynced;
    }

    /// <inheritdoc/>
    public bool TryReadAlignedStereoBlock(long trackSamplePosition, float[] outLeft, float[] outRight, int frameCount) {
        // Single atomic volatile snapshot read on real-time audio thread (ARM64 acquire semantics, zero torn structs, zero GC)
        SyncSnapshot snap = activeSnapshot;
        if (!snap.IsReadyForAudioThread) {
            return false;
        }
        return FarrowLagrangeDelay.ReadAlignedStereoBlock(
            snap.ResampledLeft,
            snap.ResampledRight,
            trackSamplePosition,
            snap.SyncResult.BaseOffsetSamples,
            snap.SyncResult.DriftPpm,
            outLeft,
            outRight,
            frameCount);
    }

    /// <summary>
    /// Materializes a full sample-aligned stereo lossless buffer matching <paramref name="atmosLengthSamples"/>
    /// for offline consumers (W09 Tier 2 EM-MWF Studio Master, W08 Clean Neural Stems, W11 Vocal Mix-Match).
    /// </summary>
    public bool TryExtractFullAlignedStereo(int atmosLengthSamples, out float[] alignedLeft, out float[] alignedRight) {
        alignedLeft = null;
        alignedRight = null;
        if (atmosLengthSamples <= 0) {
            return false;
        }
        SyncSnapshot snap = activeSnapshot;
        if (!snap.IsReadyForAudioThread) {
            return false;
        }

        alignedLeft = new float[atmosLengthSamples];
        alignedRight = new float[atmosLengthSamples];
        FarrowLagrangeDelay.ReadAlignedStereoBlock(
            snap.ResampledLeft,
            snap.ResampledRight,
            0L,
            snap.SyncResult.BaseOffsetSamples,
            snap.SyncResult.DriftPpm,
            alignedLeft,
            alignedRight,
            atmosLengthSamples);
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (writeLock) {
            activeSnapshot = SyncSnapshot.Empty;
        }
    }
}
