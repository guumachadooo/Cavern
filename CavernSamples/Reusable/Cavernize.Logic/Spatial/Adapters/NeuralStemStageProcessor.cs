using System.Numerics;

using Cavern;
using Cavern.Neural;
using Cavern.Restoration;
using Cavern.Spatial;
using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 2B stage processor (<c>Priority = 300</c>, <c>SupportsAtmosAware = true</c>) &amp; Pre-HRTF Virtual Speaker Bus
/// processor (<see cref="IPreHrtfBusProcessor"/>):
/// Coordinates MMSE Complex Coherent Projection &amp; Mid/Side Spatial Karaoke across all 1..32 Atmos virtual speaker feeds
/// BEFORE HRTF/ISM (with sample-rate-locked neural stereo/mono vocal references and automatic W09 Lossless Twin support),
/// 3D neural stem descriptor registration into <see cref="SpatialSceneHub"/>, and live vocal-activity handoff
/// to Slot 6B <see cref="SpatialDialogPostProcessingStage"/>.
/// </summary>
public sealed class NeuralStemStageProcessor : ISpatialStageProcessor, IPreHrtfBusProcessor {
    readonly object stateLock = new();
    SpatialKaraokeEngine karaokeEngine = new(Listener.DefaultSampleRate, 16);
    NeuralAudioConfig config = NeuralAudioConfig.Default;
    NeuralSeparatedTrack activeSpatialUpmixTrack;
    NeuralSeparatedTrack activeKaraokeTrack;
    ILosslessReferenceProvider losslessReference;
    SpatialProgramMetadata activeMetadata = SpatialProgramMetadata.CreateDefault();
    int sampleRate = Listener.DefaultSampleRate;
    int sourceTrackSampleRate = Listener.DefaultSampleRate;
    long preHrtfSampleCursor;
    int karaokeTailBlocksRemaining;

    // Direct zero-alloc neural vocal reference pointers (resampled/offset-aligned on the fly via Catmull-Rom in scratch!)
    float[] alignedLeadMono;
    float[] alignedLeadLeft;
    float[] alignedLeadRight;
    float[] alignedExtraLeadLeft;
    float[] alignedExtraLeadRight;
    float[] alignedBackingMono;
    int alignedRefSampleRate;
    double referenceSampleOffset;
    double referenceDriftPpm;

    // Pre-allocated zero-GC scratch arrays for real-time multichannel Karaoke masking
    readonly float[][] channelScratch = new float[SpatialKaraokeEngine.MaxSpatialChannels][];
    readonly bool[] lfeScratch = new bool[SpatialKaraokeEngine.MaxSpatialChannels];
    readonly bool[] frontCenterScratch = new bool[SpatialKaraokeEngine.MaxSpatialChannels];
    readonly bool[] strictCenterScratch = new bool[SpatialKaraokeEngine.MaxSpatialChannels];
    readonly SpatialSourceDescriptor[] preHrtfDescriptors = CreatePreHrtfDescriptorPool();
    float[] leadRefScratch = new float[2048];
    float[] leadRefRightScratch = new float[2048];
    float[] extraLeadLeftScratch = new float[2048];
    float[] extraLeadRightScratch = new float[2048];
    float[] backingRefScratch = new float[2048];
    float[] twinLeftScratch = new float[2048];
    float[] twinRightScratch = new float[2048];
    int upstreamBusLatencySamples;

    /// <summary>
    /// Optional sub-sample offset (in output-rate samples) applied when reusing cached neural stems
    /// across cross-variant versions of the same song (e.g. Dolby Atmos &lt;-&gt; FLAC with constant encoder lead-in delta).
    /// </summary>
    public double ReferenceSampleOffset {
        get {
            lock (stateLock) {
                return referenceSampleOffset;
            }
        }
        set {
            double safe = double.IsFinite(value) ? Math.Clamp(value, -960000.0, 960000.0) : 0.0;
            lock (stateLock) {
                if (Math.Abs(referenceSampleOffset - safe) > 0.25) {
                    referenceSampleOffset = safe;
                    karaokeEngine.ResetAdaptationState();
                }
            }
        }
    }

    /// <summary>
    /// B3: Optional linear clock drift (parts-per-million) applied alongside <see cref="ReferenceSampleOffset"/>
    /// when reusing cached neural stems across Dolby Atmos ↔ FLAC variants (<c>stemPos = outputPos * (1 + ppm*1e-6) + offset</c>).
    /// </summary>
    public double ReferenceDriftPpm {
        get {
            lock (stateLock) {
                return referenceDriftPpm;
            }
        }
        set {
            double safe = double.IsFinite(value) ? Math.Clamp(value, -500.0, 500.0) : 0.0;
            lock (stateLock) {
                if (Math.Abs(referenceDriftPpm - safe) > 0.05) {
                    referenceDriftPpm = safe;
                    karaokeEngine.ResetAdaptationState();
                }
            }
        }
    }

    /// <summary>
    /// Optional dynamic upstream bus latency provider in output samples (e.g., Slot 2C LGSR WOLA 512-sample delay)
    /// queried on every block so neural vocal stems stay 0.0-sample phase-locked whenever LGSR is active.
    /// </summary>
    public Func<int> UpstreamLatencySamplesProvider { get; set; }

    /// <summary>
    /// Static upstream bus latency in output samples (used when <see cref="UpstreamLatencySamplesProvider"/> is null).
    /// </summary>
    public int UpstreamBusLatencySamples {
        get {
            lock (stateLock) {
                return upstreamBusLatencySamples;
            }
        }
        set {
            lock (stateLock) {
                upstreamBusLatencySamples = value > 0 ? value : 0;
            }
        }
    }

    // Runtime & cache telemetry mirrored from platform installer service
    NeuralAudioDiagnostics runtimeSnapshot = NeuralAudioDiagnostics.Default;

    static SpatialSourceDescriptor[] CreatePreHrtfDescriptorPool() {
        SpatialSourceDescriptor[] pool = new SpatialSourceDescriptor[SpatialKaraokeEngine.MaxSpatialChannels];
        for (int i = 0; i < pool.Length; i++) {
            pool[i] = new SpatialSourceDescriptor { Index = i, Layer = SourceLayer.Bed };
        }
        return pool;
    }

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

    /// <summary>
    /// True while Spatial Karaoke is active OR completing its 512-sample clickless bypass fade-out ramp.
    /// </summary>
    public bool IsKaraokeActiveOrRamping {
        get {
            lock (stateLock) {
                return IsEnabled && ((config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f) || karaokeTailBlocksRemaining > 0);
            }
        }
    }

    /// <summary>Latest neural or coherence-derived vocal activity probability in [0; 1].</summary>
    public float VocalActivity => karaokeEngine.LatestVocalActivity;

    /// <summary>Active pre-separated full-mix neural stem bundle for AI Spatial Upmix (never overwritten by 2-stem Karaoke).</summary>
    public NeuralSeparatedTrack ActiveSpatialUpmixTrack {
        get {
            lock (stateLock) {
                return activeSpatialUpmixTrack;
            }
        }
    }

    /// <summary>Active pre-separated vocal/backing neural stem bundle dedicated to Spatial Karaoke.</summary>
    public NeuralSeparatedTrack ActiveKaraokeTrack {
        get {
            lock (stateLock) {
                return activeKaraokeTrack;
            }
        }
    }

    /// <summary>Active pre-separated neural stem bundle (prefers full-mix spatial stems, falls back to karaoke track).</summary>
    public NeuralSeparatedTrack ActiveSeparatedTrack {
        get {
            lock (stateLock) {
                return activeSpatialUpmixTrack ?? activeKaraokeTrack;
            }
        }
    }

    /// <summary>True when a full-mix separated track with non-vocal accompaniment stems is bound for AI Spatial Upmix.</summary>
    public bool HasPlayableSpatialUpmixStems {
        get {
            lock (stateLock) {
                return activeSpatialUpmixTrack?.Stems != null &&
                       activeSpatialUpmixTrack.Stems.Count > 0 &&
                       NeuralStemSpatializer.IsFullMixSpatialStemSet(activeSpatialUpmixTrack.Stems);
            }
        }
    }

    /// <summary>True when any separated track with playable PCM stems is bound.</summary>
    public bool HasPlayableSeparatedStems {
        get {
            lock (stateLock) {
                return (activeSpatialUpmixTrack?.Stems != null && activeSpatialUpmixTrack.Stems.Count > 0) ||
                       (activeKaraokeTrack?.Stems != null && activeKaraokeTrack.Stems.Count > 0);
            }
        }
    }

    /// <summary>True when an isolated neural vocal stem reference is loaded and active for Spatial Karaoke.</summary>
    public bool HasIsolatedNeuralVocalReference {
        get {
            lock (stateLock) {
                return (alignedLeadLeft != null && alignedLeadLeft.Length > 0) ||
                       (alignedLeadMono != null && alignedLeadMono.Length > 0);
            }
        }
    }

    /// <summary>
    /// Records the native sample rate of the currently playing source track so track sample positions
    /// map accurately into output sample-rate units (e.g. 44.1 kHz FLAC playing through 48 kHz Headphone Virtualizer).
    /// </summary>
    public void SetSourceTrackSampleRate(int trackSampleRate) {
        if (trackSampleRate < 8000) {
            return;
        }
        lock (stateLock) {
            sourceTrackSampleRate = trackSampleRate;
        }
    }

    /// <summary>Updates Slot 2B configuration cleanly without audio glitches or heap allocations.</summary>
    public void ApplyConfig(NeuralAudioConfig newConfig) {
        NeuralAudioConfig safe = (newConfig ?? NeuralAudioConfig.Default).Sanitize();
        lock (stateLock) {
            bool wasActive = config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f;
            bool nowActive = safe.SpatialKaraokeEnabled && safe.KaraokeVocalLevel < 0.9999f;
            bool backingModeChanged = config.KaraokePreserveBackingVocals != safe.KaraokePreserveBackingVocals;
            if (wasActive && !nowActive) {
                karaokeTailBlocksRemaining = 3; // Allow 512-sample clickless fade-out to settle cleanly
            } else if (!nowActive) {
                karaokeTailBlocksRemaining = 0;
            }
            config = safe;
            karaokeEngine.IsEnabled = safe.SpatialKaraokeEnabled;
            karaokeEngine.VocalLevel = safe.KaraokeVocalLevel;
            karaokeEngine.PreserveBackingVocals = safe.KaraokePreserveBackingVocals;
            if (backingModeChanged) {
                if (activeKaraokeTrack != null || activeSpatialUpmixTrack != null) {
                    RebuildAlignedReferencesLocked();
                }
                karaokeEngine.ResetAdaptationState();
            } else if (wasActive != nowActive) {
                karaokeEngine.ResetAdaptationState();
            }
        }
    }

    /// <summary>Instant smooth update of Karaoke Vocal Level in [0; 1] (0% = instrumental, 100% = bit-exact unity bypass).</summary>
    public void SetKaraokeVocalLevel(float level01) {
        float clamped = Math.Clamp(level01, 0f, 1f);
        lock (stateLock) {
            bool wasActive = config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f;
            bool nowActive = config.SpatialKaraokeEnabled && clamped < 0.9999f;
            if (wasActive && !nowActive) {
                karaokeTailBlocksRemaining = 3;
            } else if (!nowActive) {
                karaokeTailBlocksRemaining = 0;
            }
            config = config.WithKaraokeVocalLevel(clamped);
            karaokeEngine.VocalLevel = clamped;
        }
    }

    /// <summary>
    /// Binds a full-mix separated stem bundle exclusively for AI Spatial Upmix without clobbering any dedicated
    /// Karaoke vocal/backing track. If no dedicated Karaoke track is bound, its vocal stem also accelerates Karaoke.
    /// </summary>
    public void BindSpatialUpmixTrack(NeuralSeparatedTrack separatedTrack) {
        lock (stateLock) {
            activeSpatialUpmixTrack = separatedTrack;
            if (activeKaraokeTrack == null) {
                RebuildAlignedReferencesLocked();
                karaokeEngine.ResetAdaptationState();
            }
        }
    }

    /// <summary>
    /// Binds a vocal/backing separated stem bundle exclusively for Spatial Karaoke without ever overwriting
    /// full-mix AI Spatial Upmix stems.
    /// </summary>
    public void BindKaraokeReferenceTrack(NeuralSeparatedTrack separatedTrack) {
        lock (stateLock) {
            activeKaraokeTrack = separatedTrack;
            RebuildAlignedReferencesLocked();
            karaokeEngine.ResetAdaptationState();
        }
    }

    /// <summary>Clears both AI Spatial Upmix and Spatial Karaoke stem slots cleanly.</summary>
    public void ClearAllSeparatedTracks() {
        lock (stateLock) {
            activeSpatialUpmixTrack = null;
            activeKaraokeTrack = null;
            referenceSampleOffset = 0.0;
            referenceDriftPpm = 0.0;
            RebuildAlignedReferencesLocked();
            karaokeEngine.Reset();
        }
    }

    /// <summary>
    /// Smart role-aware binding helper: full-mix stem bundles (containing Bass/Drums/Other/Instrumental) bind to
    /// <see cref="ActiveSpatialUpmixTrack"/>, vocal-only 2-stem bundles bind exclusively to <see cref="ActiveKaraokeTrack"/>
    /// without clobbering AI Spatial Upmix stems, and <c>null</c> clears both slots.
    /// </summary>
    public void BindSeparatedTrack(NeuralSeparatedTrack separatedTrack) {
        if (separatedTrack == null) {
            ClearAllSeparatedTracks();
            return;
        }
        if (NeuralStemSpatializer.IsFullMixSpatialStemSet(separatedTrack.Stems)) {
            BindSpatialUpmixTrack(separatedTrack);
        } else {
            BindKaraokeReferenceTrack(separatedTrack);
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
            NeuralSeparatedTrack vocalTrack = activeKaraokeTrack ?? activeSpatialUpmixTrack;
            NeuralSeparatedTrack primaryTrack = activeSpatialUpmixTrack ?? activeKaraokeTrack;
            bool hasTwin = config.UseLosslessTwinForKaraoke &&
                ((losslessReference != null && losslessReference.HasSyncedLosslessTwin) ||
                 (vocalTrack != null && vocalTrack.DerivedFromLosslessTwin) ||
                 (activeSpatialUpmixTrack != null && activeSpatialUpmixTrack.DerivedFromLosslessTwin));
            bool hasNeuralLead = (alignedLeadLeft != null && alignedLeadLeft.Length > 0) ||
                (alignedLeadMono != null && alignedLeadMono.Length > 0) ||
                (vocalTrack?.MonoLeadVocalRef != null && vocalTrack.MonoLeadVocalRef.Length > 0);
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
                ActiveNeuralStemCount = primaryTrack?.Stems?.Count ?? 0,
                VocalActivity = activeKaraoke ? karaokeEngine.LatestVocalActivity : 0f,
                LastDereverbDrrDb = vocalTrack?.DereverbAnalysis != null
                    ? vocalTrack.DereverbAnalysis.DirectToReverberantRatioDb
                    : (primaryTrack?.DereverbAnalysis != null ? primaryTrack.DereverbAnalysis.DirectToReverberantRatioDb : float.NaN),
                LastInferenceRtf = runtimeSnapshot.LastInferenceRtf
            };
        }
    }

    /// <summary>
    /// Builds 3D <see cref="SpatialSourceDescriptor"/> objects for all loaded neural stems ready for
    /// <see cref="SpatialSceneHub"/> placement (preferring full-mix spatial stems).
    /// </summary>
    public SpatialSourceDescriptor[] BuildActiveStemDescriptors(Vector3 environmentSize, int startIndex) {
        lock (stateLock) {
            NeuralSeparatedTrack stemTrack = activeSpatialUpmixTrack ?? activeKaraokeTrack;
            if (stemTrack == null || stemTrack.Stems == null || stemTrack.Stems.Count == 0) {
                return Array.Empty<SpatialSourceDescriptor>();
            }
            return NeuralStemSpatializer.BuildSceneDescriptors(stemTrack.Stems, environmentSize, startIndex);
        }
    }

    /// <summary>
    /// Pre-HRTF Virtual Speaker Bus entry point called directly via <see cref="VirtualizerFilter.PreHrtfBusProcessor"/>
    /// BEFORE ISM room reflections and HRIR binaural convolution, executing multichannel Spatial Karaoke
    /// across all virtual speaker feeds simultaneously while preserving LFE and spatial phase coherence.
    /// </summary>
    public void ProcessPreHrtfBus(
        float[][] virtualChannels,
        VirtualChannel[] channelLayout,
        int channelCount,
        int blockSize,
        int sampleRate) {
        if (virtualChannels == null || channelLayout == null || channelCount <= 0 || blockSize <= 0) {
            return;
        }

        long outputSamplePos;
        int count;
        lock (stateLock) {
            bool active = config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f;
            if (!IsEnabled || (!active && karaokeTailBlocksRemaining <= 0)) {
                return;
            }
            if (sampleRate >= 8000 && sampleRate != this.sampleRate) {
                ReconfigureEngineLocked(sampleRate);
            }
            outputSamplePos = preHrtfSampleCursor;
            preHrtfSampleCursor += blockSize;
            count = Math.Min(channelCount, Math.Min(channelLayout.Length, SpatialKaraokeEngine.MaxSpatialChannels));
            for (int c = 0; c < count; c++) {
                VirtualChannel vc = channelLayout[c];
                SpatialSourceDescriptor d = preHrtfDescriptors[c];
                d.IsLfe = vc.isLfe;
                bool isFront = !vc.isLfe && MathF.Abs(vc.y) <= 45f && MathF.Abs(vc.x) <= 35f;
                d.IsDialogue = isFront && MathF.Abs(vc.y) <= 12f && MathF.Abs(vc.x) <= 25f;
                d.NormalizedPosition = isFront ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 0f, -1f);
            }
        }

        ProcessMultichannelKaraokeAtOutputSample(virtualChannels, preHrtfDescriptors, count, blockSize, outputSamplePos);
    }

    /// <summary>
    /// Processes the rendered stereo output buffer in place when operating in Direct 2.0 stereo mode
    /// (true stereo L/R MMSE vocal subtraction when neural stems are loaded, or exact Complex Mid/Side + Percussive
    /// Guard in instant blind mode; bit-exact pass-through when disabled or VocalLevel == 1.0).
    /// </summary>
    public void ProcessStereoInterleavedKaraoke(float[] interleavedStereo, int offset, int frameCount, long trackSamplePos) {
        if (interleavedStereo == null || frameCount <= 0) {
            return;
        }
        lock (stateLock) {
            bool active = config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f;
            if (!IsEnabled || (!active && karaokeTailBlocksRemaining <= 0)) {
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
            strictCenterScratch[0] = false;
            strictCenterScratch[1] = false;

            int upstreamLatency = ResolveUpstreamBusLatencyLocked();
            long rawOutputSamplePos = MapTrackSampleToOutputSampleLocked(trackSamplePos);
            long outputSamplePos = rawOutputSamplePos - upstreamLatency;
            long compensatedTrackPos = trackSamplePos - MapOutputSampleToTrackSampleLocked(upstreamLatency);
            ResolveVocalReferencesLocked(
                outputSamplePos,
                compensatedTrackPos,
                frameCount,
                wantStereoLead: true,
                allowLosslessTwinBlindFallback: true,
                out float[] leadRefL,
                out float[] leadRefR,
                out float[] backingRef,
                out bool isFullMixTwin);

            karaokeEngine.ProcessMultichannelInPlace(
                channelScratch,
                lfeScratch,
                frontCenterScratch,
                channelCount: 2,
                sampleCount: frameCount,
                neuralLeadVocalRef: leadRefL,
                neuralBackingVocalRef: backingRef,
                isFullMixTwinReference: isFullMixTwin,
                neuralLeadVocalRefRight: leadRefR,
                isStrictCenter: strictCenterScratch);

            for (int i = 0; i < frameCount; i++) {
                int idx = offset + i * 2;
                interleavedStereo[idx] = chL[i];
                interleavedStereo[idx + 1] = chR[i];
            }
            if (!active && karaokeTailBlocksRemaining > 0) {
                karaokeTailBlocksRemaining--;
            }
        }
    }

    /// <summary>
    /// Processes discrete multichannel Atmos bed/object or Pre-HRTF virtual speaker buffers in place,
    /// preserving LFE and spatial coherence.
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
        long outputSamplePos;
        lock (stateLock) {
            outputSamplePos = MapTrackSampleToOutputSampleLocked(trackSamplePos);
        }
        ProcessMultichannelKaraokeAtOutputSample(sourceBuffers, descriptors, activeCount, frameCount, outputSamplePos, trackSamplePos);
    }

    void ProcessMultichannelKaraokeAtOutputSample(
        float[][] sourceBuffers,
        IReadOnlyList<SpatialSourceDescriptor> descriptors,
        int activeCount,
        int frameCount,
        long outputSamplePos,
        long? explicitTrackSamplePos = null) {
        if (sourceBuffers == null || activeCount <= 0 || frameCount <= 0) {
            return;
        }
        lock (stateLock) {
            bool active = config.SpatialKaraokeEnabled && config.KaraokeVocalLevel < 0.9999f;
            if (!IsEnabled || (!active && karaokeTailBlocksRemaining <= 0)) {
                return;
            }
            int count = Math.Min(activeCount, SpatialKaraokeEngine.MaxSpatialChannels);
            EnsureScratchCapacity(frameCount);

            for (int c = 0; c < count; c++) {
                bool isLfe = descriptors != null && c < descriptors.Count && descriptors[c].IsLfe;
                bool isStrict = descriptors != null && c < descriptors.Count && !isLfe && descriptors[c].IsDialogue;
                bool isFront = descriptors == null || c >= descriptors.Count ||
                    descriptors[c].IsDialogue ||
                    descriptors[c].NormalizedPosition.Z >= 0.35f;
                lfeScratch[c] = isLfe;
                frontCenterScratch[c] = isFront;
                strictCenterScratch[c] = isStrict;
            }

            int upstreamLatency = ResolveUpstreamBusLatencyLocked();
            long compensatedOutputPos = outputSamplePos - upstreamLatency;
            long twinSamplePos = explicitTrackSamplePos.HasValue
                ? (explicitTrackSamplePos.Value - MapOutputSampleToTrackSampleLocked(upstreamLatency))
                : MapOutputSampleToTrackSampleLocked(compensatedOutputPos);
            ResolveVocalReferencesLocked(
                compensatedOutputPos,
                twinSamplePos,
                frameCount,
                wantStereoLead: true,
                allowLosslessTwinBlindFallback: count == 2,
                out float[] leadRefL,
                out float[] leadRefR,
                out float[] backingRef,
                out bool isFullMixTwin);

            karaokeEngine.ProcessMultichannelInPlace(
                sourceBuffers,
                lfeScratch,
                frontCenterScratch,
                count,
                frameCount,
                leadRefL,
                backingRef,
                isFullMixTwinReference: isFullMixTwin,
                neuralLeadVocalRefRight: leadRefR,
                isStrictCenter: strictCenterScratch);

            if (!active && karaokeTailBlocksRemaining > 0) {
                karaokeTailBlocksRemaining--;
            }
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        int safeRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        lock (stateLock) {
            activeMetadata = metadata;
            if (safeRate != this.sampleRate) {
                ReconfigureEngineLocked(safeRate);
            }
        }
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        if (frame == null || !IsEnabled) {
            return;
        }
        lock (stateLock) {
            preHrtfSampleCursor = MapTrackSampleToOutputSampleLocked(frame.TrackSamplePosition);
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
            preHrtfSampleCursor = 0L;
            karaokeTailBlocksRemaining = 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (stateLock) {
            karaokeEngine.Dispose();
            activeSpatialUpmixTrack = null;
            activeKaraokeTrack = null;
            alignedLeadMono = null;
            alignedLeadLeft = null;
            alignedLeadRight = null;
            alignedExtraLeadLeft = null;
            alignedExtraLeadRight = null;
            alignedBackingMono = null;
            alignedRefSampleRate = 0;
            referenceSampleOffset = 0.0;
            losslessReference = null;
            karaokeTailBlocksRemaining = 0;
        }
    }

    void ReconfigureEngineLocked(int safeRate) {
        sampleRate = safeRate;
        karaokeEngine.Dispose();
        karaokeEngine = new SpatialKaraokeEngine(safeRate, 16) {
            IsEnabled = config.SpatialKaraokeEnabled,
            VocalLevel = config.KaraokeVocalLevel,
            PreserveBackingVocals = config.KaraokePreserveBackingVocals
        };
        if (activeKaraokeTrack != null || activeSpatialUpmixTrack != null) {
            RebuildAlignedReferencesLocked();
        }
    }

    /// <summary>
    /// Zero-allocation reference pointer binding: NEVER runs synchronous KaiserSincResampler or full-song array
    /// copies inside stateLock/renderLock (eliminates 160 MB/toggle OOM spikes &amp; RT audio stalls). Rate conversion
    /// (44.1 &lt;-&gt; 48 kHz), cross-variant sample-offset alignment, and lead+backing summing execute on-the-fly in
    /// pre-allocated scratch buffers via Catmull-Rom cubic interpolation.
    /// </summary>
    void RebuildAlignedReferencesLocked() {
        alignedLeadMono = null;
        alignedLeadLeft = null;
        alignedLeadRight = null;
        alignedExtraLeadLeft = null;
        alignedExtraLeadRight = null;
        alignedBackingMono = null;
        alignedRefSampleRate = sampleRate;

        NeuralSeparatedTrack vocalTrack = activeKaraokeTrack ?? activeSpatialUpmixTrack;
        if (vocalTrack == null) {
            return;
        }

        int srcSr = vocalTrack.SampleRate >= 8000 ? vocalTrack.SampleRate : sampleRate;
        bool preserveBacking = config.KaraokePreserveBackingVocals;
        FindVocalTargetStemsZeroAlloc(
            vocalTrack,
            preserveBacking,
            out StereoStemBuffer primaryVocal,
            out StereoStemBuffer extraBackingToRemove);

        float[] srcMonoLead = (!preserveBacking && (primaryVocal != null || extraBackingToRemove != null))
            ? null
            : vocalTrack.MonoLeadVocalRef;
        float[] srcBacking = preserveBacking ? vocalTrack.MonoBackingVocalRef : null;

        alignedLeadLeft = primaryVocal?.Left;
        alignedLeadRight = primaryVocal?.Right;
        alignedExtraLeadLeft = extraBackingToRemove?.Left;
        alignedExtraLeadRight = extraBackingToRemove?.Right;
        alignedLeadMono = (srcMonoLead != null && srcMonoLead.Length > 0) ? srcMonoLead : null;
        alignedBackingMono = (srcBacking != null && srcBacking.Length > 0) ? srcBacking : null;
        alignedRefSampleRate = srcSr;
    }

    static void FindVocalTargetStemsZeroAlloc(
        NeuralSeparatedTrack track,
        bool preserveBackingVocals,
        out StereoStemBuffer primaryStem,
        out StereoStemBuffer extraBackingStem) {
        primaryStem = null;
        extraBackingStem = null;
        if (track?.Stems == null || track.Stems.Count == 0) {
            return;
        }
        StereoStemBuffer leadStem = null;
        StereoStemBuffer fullVocalStem = null;
        StereoStemBuffer backingStem = null;
        bool hasDistinctAccompaniment = false;

        for (int i = 0; i < track.Stems.Count; i++) {
            StereoStemBuffer st = track.Stems[i];
            if (st == null || st.Length <= 0) {
                continue;
            }
            NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(st.Name);
            if (role == NeuralStemRole.LeadVocal && leadStem == null) {
                leadStem = st;
            } else if (role == NeuralStemRole.Vocals && fullVocalStem == null) {
                fullVocalStem = st;
            } else if (role == NeuralStemRole.BackingVocals && backingStem == null) {
                backingStem = st;
            } else if (role == NeuralStemRole.Instrumental || role == NeuralStemRole.Drums ||
                       role == NeuralStemRole.Bass || role == NeuralStemRole.Other) {
                hasDistinctAccompaniment = true;
            }
        }

        if (!preserveBackingVocals) {
            // Pure Instrumental (Remove Backing Vocals): remove ALL vocals (lead + backing) with 0 heap allocation.
            if (fullVocalStem != null) {
                primaryStem = fullVocalStem;
                return;
            }
            // Only combine lead + backing when backing is truly isolated (distinct instrumental/band stems exist —
            // never sum kara_2's 2-stem complement where backing_vocals contains all instruments!).
            if (leadStem != null && backingStem != null && hasDistinctAccompaniment && leadStem.Length == backingStem.Length) {
                primaryStem = leadStem;
                extraBackingStem = backingStem;
                return;
            }
            primaryStem = leadStem;
            return;
        }

        primaryStem = leadStem ?? fullVocalStem;
    }

    long MapTrackSampleToOutputSampleLocked(long trackSamplePos) {
        if (trackSamplePos <= 0 || sourceTrackSampleRate <= 0 || sourceTrackSampleRate == sampleRate) {
            return trackSamplePos;
        }
        return (long)Math.Round(trackSamplePos * (sampleRate / (double)sourceTrackSampleRate));
    }

    long MapOutputSampleToTrackSampleLocked(long outputSamplePos) {
        if (outputSamplePos <= 0 || sourceTrackSampleRate <= 0 || sourceTrackSampleRate == sampleRate) {
            return outputSamplePos;
        }
        return (long)Math.Round(outputSamplePos * (sourceTrackSampleRate / (double)sampleRate));
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
            leadRefRightScratch = new float[alloc];
            extraLeadLeftScratch = new float[alloc];
            extraLeadRightScratch = new float[alloc];
            backingRefScratch = new float[alloc];
            twinLeftScratch = new float[alloc];
            twinRightScratch = new float[alloc];
        }
    }

    int ResolveUpstreamBusLatencyLocked() {
        Func<int> provider = UpstreamLatencySamplesProvider;
        if (provider != null) {
            int dynamicLatency = provider();
            return dynamicLatency > 0 ? dynamicLatency : 0;
        }
        return upstreamBusLatencySamples;
    }

    static bool CopyWithLeadingZeroOffset(float[] src, int validLimit, long startPos, float[] dst, int frameCount) {
        if (src == null || src.Length == 0 || frameCount <= 0) {
            return false;
        }
        int maxLen = Math.Min(src.Length, validLimit);
        if (startPos >= maxLen) {
            return false;
        }
        if (startPos + frameCount <= 0) {
            Array.Clear(dst, 0, frameCount);
            return true;
        }
        int dstOffset = 0;
        long srcOffset = startPos;
        if (srcOffset < 0) {
            dstOffset = (int)(-srcOffset);
            Array.Clear(dst, 0, dstOffset);
            srcOffset = 0;
        }
        int remain = frameCount - dstOffset;
        int avail = (int)Math.Min(remain, maxLen - srcOffset);
        if (avail > 0) {
            Array.Copy(src, (int)srcOffset, dst, dstOffset, avail);
        }
        if (dstOffset + avail < frameCount) {
            Array.Clear(dst, dstOffset + avail, frameCount - (dstOffset + avail));
        }
        return true;
    }

    void ResolveVocalReferencesLocked(
        long outputSamplePos,
        long trackSamplePos,
        int frameCount,
        bool wantStereoLead,
        bool allowLosslessTwinBlindFallback,
        out float[] leadRefL,
        out float[] leadRefR,
        out float[] backingRef,
        out bool isFullMixTwinReference) {
        leadRefL = null;
        leadRefR = null;
        backingRef = null;
        isFullMixTwinReference = false;

        // Guard progressive streaming reads: only read neural stem references inside the permanently finalized sample window.
        // Apply cross-variant sub-sample referenceSampleOffset + referenceDriftPpm so Atmos <-> FLAC shared stems stay 0-sample phase-locked.
        NeuralSeparatedTrack vocalTrack = activeKaraokeTrack ?? activeSpatialUpmixTrack;
        int readyLimit = vocalTrack != null && vocalTrack.IsProgressiveStreaming
            ? vocalTrack.ReadySampleCount
            : int.MaxValue;
        double driftScale = 1.0 + referenceDriftPpm * 1e-6;
        double effectiveOutputPos = outputSamplePos * driftScale + referenceSampleOffset;
        bool hasFractionalOffset = Math.Abs(referenceSampleOffset - Math.Round(referenceSampleOffset)) > 1e-4;
        bool hasClockDrift = Math.Abs(referenceDriftPpm) > 1e-4;
        bool needsLiveRateMap = (alignedRefSampleRate > 0 && alignedRefSampleRate != sampleRate) || hasFractionalOffset || hasClockDrift;

        if (!needsLiveRateMap) {
            long intOutputPos = (long)Math.Round(effectiveOutputPos);
            long endPos = intOutputPos + frameCount;
            bool sampleWindowReady = endPos <= readyLimit;
            if (sampleWindowReady) {
                if (alignedLeadLeft != null && alignedLeadRight != null &&
                    CopyWithLeadingZeroOffset(alignedLeadLeft, readyLimit, intOutputPos, leadRefScratch, frameCount) &&
                    CopyWithLeadingZeroOffset(alignedLeadRight, readyLimit, intOutputPos, leadRefRightScratch, frameCount)) {
                    if (alignedExtraLeadLeft != null && alignedExtraLeadRight != null &&
                        CopyWithLeadingZeroOffset(alignedExtraLeadLeft, readyLimit, intOutputPos, extraLeadLeftScratch, frameCount) &&
                        CopyWithLeadingZeroOffset(alignedExtraLeadRight, readyLimit, intOutputPos, extraLeadRightScratch, frameCount)) {
                        for (int i = 0; i < frameCount; i++) {
                            leadRefScratch[i] += extraLeadLeftScratch[i];
                            leadRefRightScratch[i] += extraLeadRightScratch[i];
                        }
                    }
                    if (wantStereoLead) {
                        leadRefL = leadRefScratch;
                        leadRefR = leadRefRightScratch;
                    } else {
                        for (int i = 0; i < frameCount; i++) {
                            leadRefScratch[i] = 0.5f * (leadRefScratch[i] + leadRefRightScratch[i]);
                        }
                        leadRefL = leadRefScratch;
                    }
                } else if (alignedLeadMono != null &&
                    CopyWithLeadingZeroOffset(alignedLeadMono, readyLimit, intOutputPos, leadRefScratch, frameCount)) {
                    leadRefL = leadRefScratch;
                }

                if (config.KaraokePreserveBackingVocals && alignedBackingMono != null &&
                    CopyWithLeadingZeroOffset(alignedBackingMono, readyLimit, intOutputPos, backingRefScratch, frameCount)) {
                    backingRef = backingRefScratch;
                }
            }
        } else {
            double srRatio = (alignedRefSampleRate > 0 ? alignedRefSampleRate : sampleRate) / (double)sampleRate;
            double ratio = srRatio * driftScale;
            double srcStart = effectiveOutputPos * srRatio;
            int maxNeededIdx = (int)Math.Floor(srcStart + Math.Max(0, frameCount - 1) * ratio) + 2;
            bool sampleWindowReady = maxNeededIdx < readyLimit;
            if (sampleWindowReady) {
                if (alignedLeadLeft != null && alignedLeadRight != null &&
                    srcStart < alignedLeadLeft.Length && srcStart < alignedLeadRight.Length) {
                    int capL = Math.Min(alignedLeadLeft.Length, readyLimit);
                    int capR = Math.Min(alignedLeadRight.Length, readyLimit);
                    ResampleCatmullRomBlock(alignedLeadLeft, capL, srcStart, ratio, leadRefScratch, frameCount);
                    ResampleCatmullRomBlock(alignedLeadRight, capR, srcStart, ratio, leadRefRightScratch, frameCount);
                    if (alignedExtraLeadLeft != null && alignedExtraLeadRight != null &&
                        srcStart < alignedExtraLeadLeft.Length && srcStart < alignedExtraLeadRight.Length) {
                        int capExL = Math.Min(alignedExtraLeadLeft.Length, readyLimit);
                        int capExR = Math.Min(alignedExtraLeadRight.Length, readyLimit);
                        ResampleCatmullRomBlock(alignedExtraLeadLeft, capExL, srcStart, ratio, extraLeadLeftScratch, frameCount);
                        ResampleCatmullRomBlock(alignedExtraLeadRight, capExR, srcStart, ratio, extraLeadRightScratch, frameCount);
                        for (int i = 0; i < frameCount; i++) {
                            leadRefScratch[i] += extraLeadLeftScratch[i];
                            leadRefRightScratch[i] += extraLeadRightScratch[i];
                        }
                    }
                    if (wantStereoLead) {
                        leadRefL = leadRefScratch;
                        leadRefR = leadRefRightScratch;
                    } else {
                        for (int i = 0; i < frameCount; i++) {
                            leadRefScratch[i] = 0.5f * (leadRefScratch[i] + leadRefRightScratch[i]);
                        }
                        leadRefL = leadRefScratch;
                    }
                } else if (alignedLeadMono != null && srcStart < alignedLeadMono.Length) {
                    int cap = Math.Min(alignedLeadMono.Length, readyLimit);
                    ResampleCatmullRomBlock(alignedLeadMono, cap, srcStart, ratio, leadRefScratch, frameCount);
                    leadRefL = leadRefScratch;
                }

                if (config.KaraokePreserveBackingVocals && alignedBackingMono != null && srcStart < alignedBackingMono.Length) {
                    int cap = Math.Min(alignedBackingMono.Length, readyLimit);
                    ResampleCatmullRomBlock(alignedBackingMono, cap, srcStart, ratio, backingRefScratch, frameCount);
                    backingRef = backingRefScratch;
                }
            }
        }

        // W09 Lossless Twin blind guard: on stereo tracks without a neural stem, a synced lossless twin center-sum can
        // serve as a vocal-band reference. On multichannel/Atmos tracks (allowLosslessTwinBlindFallback == false), DO NOT
        // overwrite Atmos's discrete FrontCenter bed reference with a full-mix stereo sum!
        if (leadRefL == null && allowLosslessTwinBlindFallback && config.UseLosslessTwinForKaraoke &&
            losslessReference != null && losslessReference.HasSyncedLosslessTwin &&
            losslessReference.SampleRate == sampleRate &&
            trackSamplePos >= 0 &&
            losslessReference.TryReadAlignedStereoBlock(trackSamplePos, twinLeftScratch, twinRightScratch, frameCount)) {
            for (int i = 0; i < frameCount; i++) {
                leadRefScratch[i] = 0.5f * (twinLeftScratch[i] + twinRightScratch[i]);
            }
            leadRefL = leadRefScratch;
            isFullMixTwinReference = true;
        }
    }

    static void ResampleCatmullRomBlock(
        float[] src,
        int validLen,
        double srcStart,
        double ratio,
        float[] dst,
        int frameCount) {
        if (validLen <= 0) {
            Array.Clear(dst, 0, frameCount);
            return;
        }
        int lastIdx = validLen - 1;
        for (int i = 0; i < frameCount; i++) {
            double pos = srcStart + i * ratio;
            int idx = (int)Math.Floor(pos);
            if (idx < 0 || idx > lastIdx) {
                dst[i] = 0f;
                continue;
            }
            float t = (float)(pos - idx);
            float p0 = src[idx > 0 ? idx - 1 : 0];
            float p1 = src[idx];
            float p2 = src[idx + 1 <= lastIdx ? idx + 1 : lastIdx];
            float p3 = src[idx + 2 <= lastIdx ? idx + 2 : lastIdx];
            float t2 = t * t;
            float t3 = t2 * t;
            dst[i] = 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
    }
}
