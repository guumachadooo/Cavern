using System.Numerics;
using System.Threading;

using Cavern.Neural;
using Cavern.ProAudio;
using Cavern.Spatial;
using Cavern.VocalChain;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Slot 2D spatial stage processor coupling <see cref="LiveMicrophoneBridge"/> and <see cref="LiveVocalChainDsp"/>
/// to perform zero-latency Auto-Vocal Mix-Match and inject the processed vocal stream into <see cref="SpatialSceneHub"/>.
/// Uses lock-free immutable config snapshots so UI updates never race the real-time audio thread.
/// </summary>
public sealed class VocalMixMatchStageProcessor : ISpatialStageProcessor {
    readonly LiveVocalChainDsp dsp = new();
    readonly object stateLock = new();

    VocalMixDnaProfile activeDna = VocalMixDnaProfile.CreateStudioDefault();
    VocalMacroSettings activeMacros = new();
    float[] micVoiceprintBandDb;
    float[] matchEqDeltasDb;
    int configuredSampleRate = 48000;
    volatile bool isBypassed;
    long configVersion;
    long appliedConfigVersion;
    volatile VocalConfigSnapshot pendingSnapshot;
    volatile HardwarePreAmpStateSnapshot activePreAmpSnapshot = HardwarePreAmpStateSnapshot.Empty;
    volatile int telemetryClearedFlag;

    /// <summary>Creates the Slot 2D live vocal processor with a clean neutral studio baseline (0 dB flat EQ).</summary>
    public VocalMixMatchStageProcessor() {
        PublishSnapshotLocked();
    }

    /// <inheritdoc/>
    public string StageId => "vocal-mix-match-2d";

    /// <inheritdoc/>
    public string DisplayName => "Auto-Vocal Mix-Match & Live Mastering Chain";

    /// <inheritdoc/>
    public int Priority => 450;

    /// <summary>Instant A/B bypass flag for W13 Spatial X-Ray HUD &amp; Lyrics Sing dock without destroying loaded vocal DNA.</summary>
    public bool IsBypassed {
        get => isBypassed;
        set {
            lock (stateLock) {
                if (isBypassed == value && activeMacros.Bypass == value) {
                    return;
                }
                isBypassed = value;
                activeMacros.Bypass = value;
                PublishSnapshotLocked();
            }
        }
    }

    /// <inheritdoc/>
    public bool IsEnabled {
        get {
            VocalConfigSnapshot snap = pendingSnapshot;
            return snap != null ? (snap.Macros.Enabled && !snap.Bypass) : (activeMacros.Enabled && !isBypassed);
        }
        set {
            lock (stateLock) {
                if (activeMacros.Enabled == value) {
                    return;
                }
                activeMacros.Enabled = value;
                PublishSnapshotLocked();
            }
        }
    }

    /// <inheritdoc/>
    public bool SupportsAtmosAware => true;

    /// <summary>Latest real-time vocal rack telemetry (input/output dBFS, Gate, De-Esser, Opto+FET GR, Sidechain Pocket).</summary>
    public VocalChainTelemetry Telemetry {
        get {
            if (telemetryClearedFlag != 0) {
                return new VocalChainTelemetry { InputDbfs = -120f, OutputDbfs = -120f };
            }
            VocalChainTelemetry tel = dsp.Telemetry;
            if (isBypassed) {
                tel.GateReductionDb = 0f;
                tel.DeEsserReductionDb = 0f;
                tel.CompressorReductionDb = 0f;
                tel.SidechainPocketDuckDb = 0f;
            }
            return tel;
        }
    }

    /// <summary>Latest lock-free Focusrite / USB hardware pre-ADC state snapshot bound to this vocal stage.</summary>
    public HardwarePreAmpStateSnapshot HardwarePreAmpSnapshot => activePreAmpSnapshot;

    /// <summary>Latest hardware pre-ADC gain-step epoch stored for Phase 3 <c>Slot 2D-SHIELD</c> FDKF reset.</summary>
    public long LastGainStepEpoch => activePreAmpSnapshot?.GainStepEpoch ?? dsp.LastGainStepEpoch;

    /// <summary>Updates the lock-free hardware pre-ADC state snapshot (Scarlett Air/Drive/ClipSafe/GainEpoch/MicSource).</summary>
    public void UpdateHardwarePreAmpSnapshot(HardwarePreAmpStateSnapshot snapshot) {
        activePreAmpSnapshot = snapshot ?? HardwarePreAmpStateSnapshot.Empty;
    }

    /// <summary>Currently loaded song vocal mix DNA profile.</summary>
    public VocalMixDnaProfile ActiveDna {
        get {
            lock (stateLock) {
                return activeDna.Clone();
            }
        }
    }

    /// <summary>True when a real song vocal DNA profile has been extracted from the active track.</summary>
    public bool HasExtractedSongDna {
        get {
            VocalConfigSnapshot snap = pendingSnapshot;
            return snap?.Dna?.IsExtractedFromSong == true;
        }
    }

    /// <summary>True when a calibrated 8-band microphone voiceprint is active for formant-safe Match EQ.</summary>
    public bool HasMicVoiceprint {
        get {
            lock (stateLock) {
                return micVoiceprintBandDb != null && micVoiceprintBandDb.Length == VocalMixDnaProfile.AnalysisBandHz.Length;
            }
        }
    }

    /// <summary>Returns a copy of the active 8-band Match EQ deltas (in dB), or null when neutral 0 dB EQ is active.</summary>
    public float[] GetActiveMatchEqDeltasDb() {
        lock (stateLock) {
            if (matchEqDeltasDb == null) {
                return null;
            }
            float[] copy = new float[matchEqDeltasDb.Length];
            Array.Copy(matchEqDeltasDb, copy, matchEqDeltasDb.Length);
            return copy;
        }
    }

    /// <summary>Returns a copy of the calibrated 8-band microphone voiceprint energies (dBFS), or null if uncalibrated.</summary>
    public float[] GetMicVoiceprintBandDb() {
        lock (stateLock) {
            if (micVoiceprintBandDb == null) {
                return null;
            }
            float[] copy = new float[micVoiceprintBandDb.Length];
            Array.Copy(micVoiceprintBandDb, copy, micVoiceprintBandDb.Length);
            return copy;
        }
    }

    /// <summary>Applies a song vocal DNA profile, user macros, and optional explicit Match EQ deltas atomically.</summary>
    public void ApplyVocalProfile(VocalMixDnaProfile dna, VocalMacroSettings macros, float[] matchEqDeltas = null) {
        lock (stateLock) {
            activeDna = (dna ?? VocalMixDnaProfile.CreateStudioDefault()).Clone();
            activeMacros = (macros ?? new VocalMacroSettings()).Clone();
            isBypassed = activeMacros.Bypass;
            if (matchEqDeltas != null && matchEqDeltas.Length >= VocalMixDnaProfile.AnalysisBandHz.Length) {
                matchEqDeltasDb = CloneFloatArray(matchEqDeltas, VocalMixDnaProfile.AnalysisBandHz.Length);
            } else if (micVoiceprintBandDb != null && activeDna.IsExtractedFromSong) {
                matchEqDeltasDb = VocalMixProfileExtractor.ComputeFormantSafeMatchEqDb(activeDna.BandEnergyDb, micVoiceprintBandDb);
            } else {
                matchEqDeltasDb = null;
            }
            PublishSnapshotLocked();
        }
    }

    /// <summary>Applies a freshly extracted Song Vocal DNA profile and recomputes Match EQ automatically if a mic voiceprint is loaded.</summary>
    public void ApplySongDna(VocalMixDnaProfile songDna, VocalMacroSettings macros = null) {
        lock (stateLock) {
            activeDna = (songDna ?? VocalMixDnaProfile.CreateStudioDefault()).Clone();
            if (macros != null) {
                activeMacros = macros.Clone();
                isBypassed = activeMacros.Bypass;
            }
            matchEqDeltasDb = (micVoiceprintBandDb != null && activeDna.IsExtractedFromSong)
                ? VocalMixProfileExtractor.ComputeFormantSafeMatchEqDb(activeDna.BandEnergyDb, micVoiceprintBandDb)
                : null;
            PublishSnapshotLocked();
        }
    }

    /// <summary>Stores a calibrated 8-band microphone voiceprint and computes formant-safe Match EQ against the active Song DNA.</summary>
    public float[] ApplyMicVoiceprint(float[] voiceprintBandDb, VocalMacroSettings macros = null) {
        lock (stateLock) {
            if (voiceprintBandDb != null && voiceprintBandDb.Length >= VocalMixDnaProfile.AnalysisBandHz.Length) {
                micVoiceprintBandDb = CloneFloatArray(voiceprintBandDb, VocalMixDnaProfile.AnalysisBandHz.Length);
                matchEqDeltasDb = activeDna.IsExtractedFromSong
                    ? VocalMixProfileExtractor.ComputeFormantSafeMatchEqDb(activeDna.BandEnergyDb, micVoiceprintBandDb)
                    : null;
            } else {
                micVoiceprintBandDb = null;
                matchEqDeltasDb = null;
            }
            if (macros != null) {
                activeMacros = macros.Clone();
                isBypassed = activeMacros.Bypass;
            }
            PublishSnapshotLocked();
            return matchEqDeltasDb != null ? CloneFloatArray(matchEqDeltasDb, matchEqDeltasDb.Length) : null;
        }
    }

    /// <summary>Clears any calibrated mic voiceprint so Match EQ returns cleanly to 0.0 dB flat neutral response.</summary>
    public void ClearMicVoiceprint(VocalMacroSettings macros = null) {
        lock (stateLock) {
            micVoiceprintBandDb = null;
            matchEqDeltasDb = null;
            if (macros != null) {
                activeMacros = macros.Clone();
                isBypassed = activeMacros.Bypass;
            }
            PublishSnapshotLocked();
        }
    }

    /// <summary>
    /// Extracts a real pure-physics <see cref="VocalMixDnaProfile"/> from a Slot 7A/7B <see cref="NeuralSeparatedTrack"/>
    /// using its isolated Lead/Vocals stem and optional Dereverb dry vocal decomposition.
    /// </summary>
    public static VocalMixDnaProfile ExtractSongVocalDna(
        NeuralSeparatedTrack separatedTrack,
        string trackSummary = null,
        Vector3? atmosPosition = null) {
        if (separatedTrack == null || separatedTrack.Stems == null || separatedTrack.Stems.Count == 0) {
            return null;
        }

        StereoStemBuffer vocalStem = null;
        for (int i = 0; i < separatedTrack.Stems.Count; i++) {
            StereoStemBuffer stem = separatedTrack.Stems[i];
            if (stem == null || stem.Left == null || stem.Left.Length < 256) {
                continue;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stem.Name);
            if (role == NeuralStemRole.LeadVocal) {
                vocalStem = stem;
                break;
            }
            if (role == NeuralStemRole.Vocals && vocalStem == null) {
                vocalStem = stem;
            }
        }

        if (vocalStem == null) {
            return null;
        }

        StereoStemBuffer dryVocal = separatedTrack.DereverbAnalysis?.DryVocal;
        return VocalMixProfileExtractor.AnalyzePlanar(
            vocalStem.Left,
            vocalStem.Right,
            separatedTrack.SampleRate,
            dryVocal?.Left,
            dryVocal?.Right,
            atmosPosition,
            trackSummary ?? separatedTrack.SourceTrackKey);
    }

    /// <summary>Updates target sample rate cleanly (used during paused standalone live monitoring).</summary>
    public void EnsureSampleRate(int sampleRate) {
        int clamped = Math.Clamp(sampleRate, 16000, 192000);
        if (clamped == configuredSampleRate) {
            return;
        }
        lock (stateLock) {
            if (clamped == configuredSampleRate) {
                return;
            }
            configuredSampleRate = clamped;
            PublishSnapshotLocked();
        }
    }

    /// <summary>Zeroes stale telemetry cleanly when live microphone capture is idle/inactive.</summary>
    public void ClearTelemetryIfIdle() {
        if (telemetryClearedFlag == 0) {
            dsp.ClearTelemetry(preserveLevels: false);
            telemetryClearedFlag = 1;
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        EnsureSampleRate(sampleRate);
    }

    /// <summary>
    /// Processes a live stereo mic buffer in place on the real-time audio thread with zero heap allocations,
    /// applying any pending immutable UI config snapshot atomically before DSP execution.
    /// Optionally ducks 1-4 kHz in <paramref name="backingStereoInterleaved"/> when playing a track.
    /// </summary>
    public void ProcessLiveVocalZeroGc(
        float[] vocalStereoInterleaved,
        int vocalOffset,
        int frameCount,
        float[] backingStereoInterleaved = null,
        int backingOffset = 0,
        HardwarePreAmpStateSnapshot preAmpSnapshot = null) {
        if (vocalStereoInterleaved == null || frameCount <= 0) {
            return;
        }

        if (preAmpSnapshot != null) {
            activePreAmpSnapshot = preAmpSnapshot;
        }
        HardwarePreAmpStateSnapshot effectivePreAmp = preAmpSnapshot ?? activePreAmpSnapshot;

        VocalConfigSnapshot snap = pendingSnapshot;
        if (snap != null && snap.Version != appliedConfigVersion) {
            dsp.Configure(snap.SampleRate, snap.Dna, snap.Macros, snap.MatchEqDeltasDb, effectivePreAmp);
            appliedConfigVersion = snap.Version;
        }

        telemetryClearedFlag = 0;
        dsp.ProcessStereoBlockZeroGc(
            vocalStereoInterleaved,
            vocalOffset,
            frameCount,
            backingStereoInterleaved,
            backingOffset,
            effectivePreAmp);
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        // Scene-frame hook ensures Atmos vocal placement metadata stays synchronized with active DNA
        // unless the user manually pinned the Live Microphone inside the 6DoF 3D Room.
        VocalConfigSnapshot snap = pendingSnapshot;
        if (frame == null || snap == null || !snap.Macros.Enabled) {
            return;
        }
        IReadOnlyList<SpatialSourceDescriptor> sources = frame.Sources;
        for (int i = 0; i < sources.Count; i++) {
            if (sources[i].Layer == SourceLayer.LiveMicrophone && !sources[i].HasCustomPosition) {
                sources[i].NormalizedPosition = snap.Dna.VocalAtmosPosition;
                break;
            }
        }
    }

    /// <inheritdoc/>
    public void Reset() {
        dsp.Reset();
        telemetryClearedFlag = 1;
    }

    /// <inheritdoc/>
    public void Dispose() {
        dsp.Reset();
        telemetryClearedFlag = 1;
    }

    void PublishSnapshotLocked() {
        long ver = Interlocked.Increment(ref configVersion);
        pendingSnapshot = new VocalConfigSnapshot(
            ver,
            configuredSampleRate,
            activeDna.Clone(),
            activeMacros.Clone(),
            matchEqDeltasDb != null ? CloneFloatArray(matchEqDeltasDb, matchEqDeltasDb.Length) : null,
            isBypassed);
    }

    static float[] CloneFloatArray(float[] src, int len) {
        float[] dst = new float[len];
        Array.Copy(src, dst, len);
        return dst;
    }

    sealed class VocalConfigSnapshot(
        long version,
        int sampleRate,
        VocalMixDnaProfile dna,
        VocalMacroSettings macros,
        float[] matchEqDeltasDb,
        bool bypass) {
        public readonly long Version = version;
        public readonly int SampleRate = sampleRate;
        public readonly VocalMixDnaProfile Dna = dna;
        public readonly VocalMacroSettings Macros = macros;
        public readonly float[] MatchEqDeltasDb = matchEqDeltasDb;
        public readonly bool Bypass = bypass;
    }
}
