using System.Numerics;

using Cavern;
using Cavern.Neural;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 2B stage processor (<c>Priority = 300</c>, <c>SupportsAtmosAware = true</c>):
/// Coordinates zero-phase multichannel Spatial Karaoke across all 1..32 Atmos beds &amp; dynamic objects
/// (with automatic W09 Lossless Twin acceleration &amp; pre-separated neural vocal stem projection),
/// 3D neural stem descriptor registration into <see cref="SpatialSceneHub"/>, and live vocal-activity handoff
/// to Slot 6B <see cref="SpatialDialogPostProcessingStage"/>.
/// </summary>
public sealed class NeuralStemStageProcessor : ISpatialStageProcessor {
    readonly object stateLock = new();
    SpatialKaraokeEngine karaokeEngine = new(Listener.DefaultSampleRate, 16);
    NeuralAudioConfig config = NeuralAudioConfig.Default;
    NeuralSeparatedTrack activeSeparatedTrack;
    ILosslessReferenceProvider losslessReference;
    SpatialProgramMetadata activeMetadata = SpatialProgramMetadata.CreateDefault();
    int sampleRate = Listener.DefaultSampleRate;

    // Pre-allocated zero-GC scratch arrays for real-time multichannel Karaoke masking
    readonly float[][] channelScratch = new float[SpatialKaraokeEngine.MaxSpatialChannels][];
    readonly bool[] lfeScratch = new bool[SpatialKaraokeEngine.MaxSpatialChannels];
    readonly bool[] frontCenterScratch = new bool[SpatialKaraokeEngine.MaxSpatialChannels];
    float[] leadRefScratch = new float[2048];
    float[] backingRefScratch = new float[2048];
    float[] twinLeftScratch = new float[2048];
    float[] twinRightScratch = new float[2048];

    // Runtime & cache telemetry mirrored from platform installer service
    NeuralAudioDiagnostics runtimeSnapshot = NeuralAudioDiagnostics.Default;

    /// <inheritdoc/>
    public string StageId => "neural-slot2b-stems-karaoke";

    /// <inheritdoc/>
    public string DisplayName => "Neural Stems, MVSEP & Spatial Karaoke (Slot 2B)";

    /// <inheritdoc/>
    public int Priority => 300;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool SupportsAtmosAware => true;

    /// <summary>Active Slot 2B neural audio configuration.</summary>
    public NeuralAudioConfig Config {
        get {
            lock (stateLock) {
                return config;
            }
        }
    }

    /// <summary>True when Spatial Karaoke is actively attenuating vocals (enabled and vocal level &lt; 100%).</summary>
    public bool IsKaraokeActivelyAttenuating {
        get {
            lock (stateLock) {
                return IsEnabled && config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.999f;
            }
        }
    }

    /// <summary>Latest neural or coherence-derived vocal activity probability in [0; 1].</summary>
    public float VocalActivity => karaokeEngine.LatestVocalActivity;

    /// <summary>Active pre-separated neural stem bundle for the current track, or null if none loaded.</summary>
    public NeuralSeparatedTrack ActiveSeparatedTrack {
        get {
            lock (stateLock) {
                return activeSeparatedTrack;
            }
        }
    }

    /// <summary>Updates Slot 2B configuration cleanly without audio glitches.</summary>
    public void ApplyConfig(NeuralAudioConfig newConfig) {
        NeuralAudioConfig safe = (newConfig ?? NeuralAudioConfig.Default).Sanitize();
        lock (stateLock) {
            config = safe;
            karaokeEngine.IsEnabled = safe.SpatialKaraokeEnabled;
            karaokeEngine.VocalLevel = safe.KaraokeVocalLevel;
            karaokeEngine.PreserveBackingVocals = safe.KaraokePreserveBackingVocals;
        }
    }

    /// <summary>Instant smooth update of Karaoke Vocal Level in [0; 1] (0% = instrumental, 100% = bit-exact unity bypass).</summary>
    public void SetKaraokeVocalLevel(float level01) {
        float clamped = Math.Clamp(level01, 0f, 1f);
        lock (stateLock) {
            config = config.WithKaraokeVocalLevel(clamped);
            karaokeEngine.VocalLevel = clamped;
        }
    }

    /// <summary>Binds a pre-separated neural stem set (including Lead/Backing karaoke references) for the active track.</summary>
    public void BindSeparatedTrack(NeuralSeparatedTrack separatedTrack) {
        lock (stateLock) {
            activeSeparatedTrack = separatedTrack;
            karaokeEngine.Reset();
        }
    }

    /// <summary>Binds the optional W09 <see cref="ILosslessReferenceProvider"/> for lossless-accelerated Spatial Karaoke.</summary>
    public void BindLosslessReference(ILosslessReferenceProvider provider) {
        lock (stateLock) {
            losslessReference = provider;
        }
    }

    /// <summary>Updates platform runtime, orchestrator summary &amp; model/track cache status mirrored from Slot 7 services.</summary>
    public void UpdateRuntimeDiagnostics(
        bool isRuntimeInstalled,
        string runtimeVersion,
        string activeProviderName,
        bool isQnnAvailable,
        int installedModelCount,
        long cacheSizeBytes,
        float lastInferenceRtf = -1f,
        int trackCacheEntryCount = 0,
        long trackCacheSizeBytes = 0L,
        string orchestrationSummary = "") {
        lock (stateLock) {
            runtimeSnapshot.IsRuntimeInstalled = isRuntimeInstalled;
            runtimeSnapshot.RuntimeVersion = runtimeVersion ?? string.Empty;
            runtimeSnapshot.ActiveProviderName = string.IsNullOrWhiteSpace(activeProviderName)
                ? (isRuntimeInstalled ? "CPU" : "Not Installed")
                : activeProviderName;
            runtimeSnapshot.IsQnnAvailable = isQnnAvailable;
            runtimeSnapshot.InstalledModelCount = Math.Max(0, installedModelCount);
            runtimeSnapshot.CacheSizeBytes = Math.Max(0L, cacheSizeBytes);
            runtimeSnapshot.TrackCacheEntryCount = Math.Max(0, trackCacheEntryCount);
            runtimeSnapshot.TrackCacheSizeBytes = Math.Max(0L, trackCacheSizeBytes);
            runtimeSnapshot.OrchestrationSummary = orchestrationSummary ?? string.Empty;
            if (lastInferenceRtf >= 0f) {
                runtimeSnapshot.LastInferenceRtf = lastInferenceRtf;
            }
        }
    }

    /// <summary>Builds a fresh Slot 2B diagnostics snapshot for <see cref="SpatialSceneHub.NeuralDiagnostics"/>.</summary>
    public NeuralAudioDiagnostics BuildDiagnosticsSnapshot() {
        lock (stateLock) {
            bool hasTwin = config.UseLosslessTwinForKaraoke &&
                ((losslessReference != null && losslessReference.HasSyncedLosslessTwin) ||
                 (activeSeparatedTrack != null && activeSeparatedTrack.DerivedFromLosslessTwin));
            bool hasNeuralLead = activeSeparatedTrack?.MonoLeadVocalRef != null && activeSeparatedTrack.MonoLeadVocalRef.Length > 0;
            bool activeKaraoke = IsEnabled && config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.999f;

            return new NeuralAudioDiagnostics {
                IsRuntimeInstalled = runtimeSnapshot.IsRuntimeInstalled,
                RuntimeVersion = runtimeSnapshot.RuntimeVersion,
                ActiveProviderName = runtimeSnapshot.ActiveProviderName,
                IsQnnAvailable = runtimeSnapshot.IsQnnAvailable,
                InstalledModelCount = runtimeSnapshot.InstalledModelCount,
                CacheSizeBytes = runtimeSnapshot.CacheSizeBytes,
                TrackCacheEntryCount = runtimeSnapshot.TrackCacheEntryCount,
                TrackCacheSizeBytes = runtimeSnapshot.TrackCacheSizeBytes,
                OrchestrationSummary = runtimeSnapshot.OrchestrationSummary,
                IsKaraokeActive = activeKaraoke,
                KaraokeVocalLevel = config.KaraokeVocalLevel,
                HasNeuralVocalReference = hasNeuralLead,
                IsLosslessTwinAccelerated = hasTwin,
                ActiveNeuralStemCount = activeSeparatedTrack?.Stems?.Count ?? 0,
                VocalActivity = activeKaraoke ? karaokeEngine.LatestVocalActivity : 0f,
                LastDereverbDrrDb = activeSeparatedTrack?.DereverbAnalysis != null
                    ? activeSeparatedTrack.DereverbAnalysis.DirectToReverberantRatioDb
                    : float.NaN,
                LastInferenceRtf = runtimeSnapshot.LastInferenceRtf
            };
        }
    }

    /// <summary>
    /// Builds 3D <see cref="SpatialSourceDescriptor"/> objects for all loaded neural stems ready for
    /// <see cref="SpatialSceneHub"/> placement.
    /// </summary>
    public SpatialSourceDescriptor[] BuildActiveStemDescriptors(Vector3 environmentSize, int startIndex) {
        lock (stateLock) {
            if (activeSeparatedTrack == null || activeSeparatedTrack.Stems == null || activeSeparatedTrack.Stems.Count == 0) {
                return Array.Empty<SpatialSourceDescriptor>();
            }
            string[] names = new string[activeSeparatedTrack.Stems.Count];
            for (int i = 0; i < names.Length; i++) {
                names[i] = activeSeparatedTrack.Stems[i].Name;
            }
            return NeuralStemSpatializer.BuildSceneDescriptors(names, environmentSize, startIndex);
        }
    }

    /// <summary>
    /// Processes the rendered stereo output buffer in place when operating in real-time stereo/binaural Karaoke mode
    /// (zero-phase multichannel coherence Wiener masking; bit-exact pass-through when disabled or VocalLevel == 1.0).
    /// </summary>
    public void ProcessStereoInterleavedKaraoke(float[] interleavedStereo, int offset, int frameCount, long trackSamplePos) {
        if (interleavedStereo == null || frameCount <= 0) {
            return;
        }
        lock (stateLock) {
            if (!IsEnabled || !config.SpatialKaraokeEnabled || config.KaraokeVocalLevel >= 0.9999f) {
                return;
            }
            EnsureScratchCapacity(frameCount);
            float[] chL = channelScratch[0];
            float[] chR = channelScratch[1];
            for (int i = 0; i < frameCount; i++) {
                int idx = offset + i * 2;
                chL[i] = interleavedStereo[idx];
                chR[i] = interleavedStereo[idx + 1];
            }
            lfeScratch[0] = false;
            lfeScratch[1] = false;
            frontCenterScratch[0] = true;
            frontCenterScratch[1] = true;

            ResolveVocalReferencesLocked(trackSamplePos, frameCount, out float[] leadRef, out float[] backingRef);
            karaokeEngine.ProcessMultichannelInPlace(
                channelScratch,
                lfeScratch,
                frontCenterScratch,
                channelCount: 2,
                sampleCount: frameCount,
                neuralLeadVocalRef: leadRef,
                neuralBackingVocalRef: backingRef);

            for (int i = 0; i < frameCount; i++) {
                int idx = offset + i * 2;
                interleavedStereo[idx] = chL[i];
                interleavedStereo[idx + 1] = chR[i];
            }
        }
    }

    /// <summary>
    /// Processes discrete multichannel Atmos bed/object buffers in place (<see cref="SpatialSceneFrame.SourceBuffers"/>)
    /// when pre-virtualizer source buffers are populated, preserving LFE and 0.000° inter-channel phase coherence.
    /// </summary>
    public void ProcessMultichannelKaraoke(
        float[][] sourceBuffers,
        IReadOnlyList<SpatialSourceDescriptor> descriptors,
        int activeCount,
        int frameCount,
        long trackSamplePos) {
        if (sourceBuffers == null || activeCount <= 0 || frameCount <= 0) {
            return;
        }
        lock (stateLock) {
            if (!IsEnabled || !config.SpatialKaraokeEnabled || config.KaraokeVocalLevel >= 0.9999f) {
                return;
            }
            int count = Math.Min(activeCount, SpatialKaraokeEngine.MaxSpatialChannels);
            EnsureScratchCapacity(frameCount);

            for (int c = 0; c < count; c++) {
                bool isLfe = descriptors != null && c < descriptors.Count && descriptors[c].IsLfe;
                bool isFront = descriptors == null || c >= descriptors.Count ||
                    descriptors[c].IsDialogue ||
                    descriptors[c].NormalizedPosition.Z >= 0.35f;
                lfeScratch[c] = isLfe;
                frontCenterScratch[c] = isFront;
            }

            ResolveVocalReferencesLocked(trackSamplePos, frameCount, out float[] leadRef, out float[] backingRef);
            karaokeEngine.ProcessMultichannelInPlace(
                sourceBuffers,
                lfeScratch,
                frontCenterScratch,
                count,
                frameCount,
                leadRef,
                backingRef);
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        int safeRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        lock (stateLock) {
            activeMetadata = metadata;
            if (safeRate != this.sampleRate) {
                this.sampleRate = safeRate;
                karaokeEngine.Dispose();
                karaokeEngine = new SpatialKaraokeEngine(safeRate, 16) {
                    IsEnabled = config.SpatialKaraokeEnabled,
                    VocalLevel = config.KaraokeVocalLevel,
                    PreserveBackingVocals = config.KaraokePreserveBackingVocals
                };
            }
        }
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        if (frame == null || !IsEnabled) {
            return;
        }
        if (frame.SourceBuffers != null && frame.ActiveSourceBufferCount > 0 && frame.FrameCount > 0) {
            ProcessMultichannelKaraoke(
                frame.SourceBuffers,
                frame.Sources,
                frame.ActiveSourceBufferCount,
                frame.FrameCount,
                frame.TrackSamplePosition);
        }

        // Mirror Slot 2B vocal telemetry into frame
        SoundfieldTelemetry tel = frame.Telemetry;
        tel.VocalActivity = VocalActivity;
        tel.KaraokeVocalLevel = config.SpatialKaraokeEnabled ? config.KaraokeVocalLevel : 1f;
        frame.Telemetry = tel;
    }

    /// <inheritdoc/>
    public void Reset() {
        lock (stateLock) {
            karaokeEngine.Reset();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (stateLock) {
            karaokeEngine.Dispose();
            activeSeparatedTrack = null;
            losslessReference = null;
        }
    }

    void EnsureScratchCapacity(int frameCount) {
        if (channelScratch[0] == null || channelScratch[0].Length < frameCount) {
            int alloc = Math.Max(2048, frameCount);
            channelScratch[0] = new float[alloc];
            channelScratch[1] = new float[alloc];
        }
        if (leadRefScratch.Length < frameCount) {
            int alloc = Math.Max(2048, frameCount);
            leadRefScratch = new float[alloc];
            backingRefScratch = new float[alloc];
            twinLeftScratch = new float[alloc];
            twinRightScratch = new float[alloc];
        }
    }

    void ResolveVocalReferencesLocked(long trackSamplePos, int frameCount, out float[] leadRef, out float[] backingRef) {
        leadRef = null;
        backingRef = null;

        if (activeSeparatedTrack?.MonoLeadVocalRef != null && trackSamplePos >= 0) {
            float[] srcLead = activeSeparatedTrack.MonoLeadVocalRef;
            if (trackSamplePos < srcLead.Length) {
                int avail = (int)Math.Min(frameCount, srcLead.Length - trackSamplePos);
                Array.Copy(srcLead, (int)trackSamplePos, leadRefScratch, 0, avail);
                if (avail < frameCount) {
                    Array.Clear(leadRefScratch, avail, frameCount - avail);
                }
                leadRef = leadRefScratch;
            }
        }

        if (config.KaraokePreserveBackingVocals && activeSeparatedTrack?.MonoBackingVocalRef != null && trackSamplePos >= 0) {
            float[] srcBack = activeSeparatedTrack.MonoBackingVocalRef;
            if (trackSamplePos < srcBack.Length) {
                int avail = (int)Math.Min(frameCount, srcBack.Length - trackSamplePos);
                Array.Copy(srcBack, (int)trackSamplePos, backingRefScratch, 0, avail);
                if (avail < frameCount) {
                    Array.Clear(backingRefScratch, avail, frameCount - avail);
                }
                backingRef = backingRefScratch;
            }
        }

        // W09 Lossless Twin auto-acceleration: if no offline neural lead stem is attached yet, use the synced
        // lossless FLAC twin's clean center-sum as a higher-fidelity vocal coherence reference.
        if (leadRef == null && config.UseLosslessTwinForKaraoke &&
            losslessReference != null && losslessReference.HasSyncedLosslessTwin &&
            losslessReference.TryReadAlignedStereoBlock(trackSamplePos, twinLeftScratch, twinRightScratch, frameCount)) {
            for (int i = 0; i < frameCount; i++) {
                leadRefScratch[i] = 0.5f * (twinLeftScratch[i] + twinRightScratch[i]);
            }
            leadRef = leadRefScratch;
        }
    }
}
