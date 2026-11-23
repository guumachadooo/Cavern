using Cavern;
using Cavern.Restoration;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 2C stage processor (<c>Priority = 400</c>, <c>SupportsAtmosAware = true</c>) &amp; Pre-HRTF Virtual Speaker Bus
/// processor (<see cref="IPreHrtfBusProcessor"/>) for W09 Lossless-Guided Atmos Restoration (<c>LGSR</c>).
/// <para>
/// Consumes Slot 1's <see cref="ILosslessReferenceProvider"/> and live per-block E-AC-3 <c>CouplingStartHz</c>/<c>SpxStartHz</c>
/// metadata, applying phase-preserving coherence spectral fusion &amp; HF Soft-Ratio ISS directly on the Pre-HRTF Virtual
/// Speaker Bus (preserving JOC cancellation, Atmos dynamics via Envelope Lock, and FLAC Clipping Guard), with automatic
/// bypass on already-lossless streams (TrueHD/ADM/FLAC/WAV/LAF).
/// </para>
/// </summary>
public sealed class LgsrStageProcessor : ISpatialStageProcessor, IPreHrtfBusProcessor {
    readonly object stateLock = new();
    LgsrSpectralFusionEngine fusionEngine = new(Listener.DefaultSampleRate, 16);
    LgsrConfig config = LgsrConfig.Default;
    ILosslessReferenceProvider losslessReference;
    SpatialProgramMetadata activeMetadata = SpatialProgramMetadata.CreateDefault();
    int sampleRate = Listener.DefaultSampleRate;

    // Track position cursor synchronized on every SpatialSceneFrame.BeginBlock prior to Listener.Render()
    long activeBlockTrackSamplePos;
    long preHrtfSampleCursor;
    bool activeTrackAlreadyLossless;
    bool? explicitLosslessOverride;
    bool lastBlockActivePreHrtf;

    // Pre-allocated zero-GC scratch arrays for Pre-HRTF Virtual Speaker Bus & Direct 2.0 stereo fallback
    readonly bool[] lfeScratch = new bool[LgsrSpectralFusionEngine.MaxBusChannels];
    readonly float[] panRightScratch = new float[LgsrSpectralFusionEngine.MaxBusChannels];
    readonly float[][] stereoBusScratch = new float[2][];
    float[] refLeftScratch = new float[2048];
    float[] refRightScratch = new float[2048];

    /// <inheritdoc/>
    public string StageId => "lgsr-slot2c-lossless-fusion";

    /// <inheritdoc/>
    public string DisplayName => "Lossless-Guided Atmos Restoration (Slot 2C LGSR)";

    /// <inheritdoc/>
    public int Priority => 400;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool SupportsAtmosAware => true;

    /// <summary>Active immutable Slot 2C LGSR configuration.</summary>
    public LgsrConfig Config {
        get {
            lock (stateLock) {
                return config;
            }
        }
    }

    /// <summary>True when LGSR is armed and actively restoring a lossy stream using a synced lossless twin.</summary>
    public bool IsActivelyRestoring {
        get {
            lock (stateLock) {
                return IsEnabled
                    && config.Enabled
                    && !(config.BypassOnAlreadyLossless && activeTrackAlreadyLossless)
                    && losslessReference != null
                    && losslessReference.HasSyncedLosslessTwin
                    && losslessReference.SyncConfidence >= config.MinSyncConfidence;
            }
        }
    }

    /// <summary>Exact WOLA latency in samples (<c>512</c>) when LGSR is armed and actively restoring, else <c>0</c>.</summary>
    public int ActiveLatencySamples => IsActivelyRestoring ? LgsrSpectralFusionEngine.LatencySamples : 0;

    /// <summary>
    /// Exact WOLA latency in samples (<c>512</c>) actually applied on the most recently processed Pre-HRTF or
    /// Direct 2.0 block (immediately readable by downstream <see cref="NeuralStemStageProcessor"/> on the same block).
    /// </summary>
    public int LastAppliedLatencySamples {
        get {
            lock (stateLock) {
                return lastBlockActivePreHrtf ? LgsrSpectralFusionEngine.LatencySamples : 0;
            }
        }
    }

    /// <summary>Creates the Slot 2C LGSR stage processor.</summary>
    public LgsrStageProcessor() {
        stereoBusScratch[0] = new float[2048];
        stereoBusScratch[1] = new float[2048];
        ApplyConfig(LgsrConfig.Default);
    }

    /// <summary>Applies a validated <see cref="LgsrConfig"/> cleanly without audio glitches.</summary>
    public void ApplyConfig(LgsrConfig newConfig) {
        LgsrConfig safe = (newConfig ?? LgsrConfig.Default).Sanitize();
        lock (stateLock) {
            config = safe;
            fusionEngine.IsEnabled = safe.Enabled;
            fusionEngine.FusionStrength = safe.FusionStrength;
            fusionEngine.DynamicEnvelopeLock = safe.DynamicEnvelopeLock;
            fusionEngine.ClippingGuardEnabled = safe.ClippingGuardEnabled;
            fusionEngine.UseBitstreamGuide = safe.UseBitstreamCouplingSpxGuide;
        }
    }

    /// <summary>Binds the Slot 1 <see cref="ILosslessReferenceProvider"/> (typically <see cref="UniversalLosslessTwinProvider"/>).</summary>
    public void BindLosslessReference(ILosslessReferenceProvider provider) {
        lock (stateLock) {
            losslessReference = provider;
            fusionEngine.Reset();
            lastBlockActivePreHrtf = false;
        }
    }

    /// <summary>Updates whether the active track is natively lossless (TrueHD, DAMF/ADM, FLAC, WAV/PCM, LAF).</summary>
    public void SetActiveTrackLosslessStatus(bool isAlreadyLossless) {
        lock (stateLock) {
            explicitLosslessOverride = isAlreadyLossless;
            activeTrackAlreadyLossless = isAlreadyLossless;
            if (isAlreadyLossless) {
                lastBlockActivePreHrtf = false;
            }
        }
    }

    /// <summary>Clears any explicit lossless override so <see cref="Configure"/> derives status from decoder metadata.</summary>
    public void ClearExplicitLosslessStatus() {
        lock (stateLock) {
            explicitLosslessOverride = null;
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        int safeRate = sampleRate >= 8000 ? sampleRate : Listener.DefaultSampleRate;
        lock (stateLock) {
            activeMetadata = metadata;
            activeTrackAlreadyLossless = explicitLosslessOverride ?? !metadata.IsLossySpatialStream;
            if (safeRate != this.sampleRate) {
                this.sampleRate = safeRate;
                fusionEngine.Dispose();
                fusionEngine = new LgsrSpectralFusionEngine(safeRate, 16) {
                    IsEnabled = config.Enabled,
                    FusionStrength = config.FusionStrength,
                    DynamicEnvelopeLock = config.DynamicEnvelopeLock,
                    ClippingGuardEnabled = config.ClippingGuardEnabled,
                    UseBitstreamGuide = config.UseBitstreamCouplingSpxGuide
                };
            }
            fusionEngine.BitstreamCouplingHz = metadata.CouplingStartHz;
            fusionEngine.BitstreamSpxHz = metadata.SpxStartHz;
        }
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        if (frame == null || !IsEnabled) {
            return;
        }
        lock (stateLock) {
            activeMetadata = frame.Metadata;
            activeBlockTrackSamplePos = frame.TrackSamplePosition;
            preHrtfSampleCursor = frame.TrackSamplePosition;
            fusionEngine.BitstreamCouplingHz = frame.Metadata.CouplingStartHz;
            fusionEngine.BitstreamSpxHz = frame.Metadata.SpxStartHz;

            // Mirror latest LGSR & bitstream telemetry into SpatialSceneFrame.Telemetry
            LgsrRuntimeTelemetry rt = fusionEngine.Telemetry;
            SoundfieldTelemetry tel = frame.Telemetry;
            tel.LgsrCoherence = lastBlockActivePreHrtf ? rt.AverageCoherence : 0f;
            tel.LgsrFusionRatio = lastBlockActivePreHrtf ? rt.ActiveFusionRatio : 0f;
            tel.LgsrCouplingHz = frame.Metadata.CouplingStartHz;
            tel.LgsrSpxHz = frame.Metadata.SpxStartHz;
            if (losslessReference is UniversalLosslessTwinProvider twin && twin.HasSyncedLosslessTwin) {
                SubSampleSyncResult sr = twin.SyncResult;
                tel.LgsrSyncOffsetSamples = (float)sr.BaseOffsetSamples;
                tel.LgsrDriftPpm = (float)sr.DriftPpm;
            }
            frame.Telemetry = tel;
        }
    }

    /// <summary>
    /// Pre-HRTF Virtual Speaker Bus entry point called directly by <see cref="VirtualizerFilter.Process"/>
    /// right after de-interleaving virtual speaker feeds and BEFORE ISM reflections &amp; HRIR convolution.
    /// Zero heap allocations in steady-state playback.
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

        lock (stateLock) {
            if (!IsEnabled || !config.Enabled || (config.BypassOnAlreadyLossless && activeTrackAlreadyLossless)) {
                lastBlockActivePreHrtf = false;
                return;
            }
            ILosslessReferenceProvider provider = losslessReference;
            if (provider == null || !provider.HasSyncedLosslessTwin || provider.SyncConfidence < config.MinSyncConfidence) {
                lastBlockActivePreHrtf = false;
                return;
            }

            EnsureScratchCapacity(blockSize);
            long samplePos = preHrtfSampleCursor;
            preHrtfSampleCursor += blockSize;

            if (!provider.TryReadAlignedStereoBlock(samplePos, refLeftScratch, refRightScratch, blockSize)) {
                lastBlockActivePreHrtf = false;
                return;
            }

            int count = Math.Min(channelCount, LgsrSpectralFusionEngine.MaxBusChannels);
            for (int c = 0; c < count; c++) {
                VirtualChannel vc = channelLayout[c];
                lfeScratch[c] = vc.isLfe;
                float yawRad = vc.y * VectorExtensions.Deg2Rad;
                float pitchRad = vc.x * VectorExtensions.Deg2Rad;
                panRightScratch[c] = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
            }

            fusionEngine.ProcessPreHrtfBusInPlace(
                virtualChannels,
                lfeScratch,
                panRightScratch,
                count,
                blockSize,
                refLeftScratch,
                refRightScratch,
                provider.SyncConfidence);
            lastBlockActivePreHrtf = true;
        }
    }

    /// <summary>
    /// Direct 2.0 stereo fallback when Headphone Virtualizer is bypassed (ensures lossless-guided restoration still
    /// works cleanly in stereo speaker / USB DAC Direct 2.0 mode without double-processing in binaural mode).
    /// </summary>
    public void ProcessDirectStereoFallback(float[] interleavedStereo, int offset, int frameCount, long trackSamplePos) {
        if (interleavedStereo == null || frameCount <= 0) {
            return;
        }
        lock (stateLock) {
            if (!IsEnabled || !config.Enabled || (config.BypassOnAlreadyLossless && activeTrackAlreadyLossless)) {
                lastBlockActivePreHrtf = false;
                return;
            }
            ILosslessReferenceProvider provider = losslessReference;
            if (provider == null || !provider.HasSyncedLosslessTwin || provider.SyncConfidence < config.MinSyncConfidence) {
                lastBlockActivePreHrtf = false;
                return;
            }

            EnsureScratchCapacity(frameCount);
            if (!provider.TryReadAlignedStereoBlock(trackSamplePos, refLeftScratch, refRightScratch, frameCount)) {
                lastBlockActivePreHrtf = false;
                return;
            }

            float[] chL = stereoBusScratch[0];
            float[] chR = stereoBusScratch[1];
            for (int i = 0; i < frameCount; i++) {
                int idx = offset + i * 2;
                chL[i] = interleavedStereo[idx];
                chR[i] = interleavedStereo[idx + 1];
            }
            lfeScratch[0] = false;
            lfeScratch[1] = false;
            panRightScratch[0] = -1f;
            panRightScratch[1] = 1f;

            fusionEngine.ProcessPreHrtfBusInPlace(
                stereoBusScratch,
                lfeScratch,
                panRightScratch,
                2,
                frameCount,
                refLeftScratch,
                refRightScratch,
                provider.SyncConfidence);

            for (int i = 0; i < frameCount; i++) {
                int idx = offset + i * 2;
                interleavedStereo[idx] = chL[i];
                interleavedStereo[idx + 1] = chR[i];
            }
            lastBlockActivePreHrtf = true;
        }
    }

    /// <summary>Builds a fresh diagnostic snapshot for <see cref="SpatialSceneHub.LgsrDiagnostics"/>.</summary>
    public LgsrDiagnostics BuildDiagnosticsSnapshot() {
        lock (stateLock) {
            LgsrRuntimeTelemetry rt = fusionEngine.Telemetry;
            LosslessTwinVerificationState state = LosslessTwinVerificationState.None;
            string twinName = string.Empty;
            string twinFmt = string.Empty;
            double offsetSamples = 0.0;
            float offsetMs = 0f;
            float driftPpm = 0f;
            float confidence = 0f;

            if (activeTrackAlreadyLossless && config.BypassOnAlreadyLossless) {
                state = LosslessTwinVerificationState.BypassedAlreadyLossless;
            } else if (losslessReference is UniversalLosslessTwinProvider twin) {
                state = twin.VerificationState;
                twinName = twin.Metadata?.FileName ?? string.Empty;
                twinFmt = twin.Metadata?.FormatSummary ?? string.Empty;
                SubSampleSyncResult sr = twin.SyncResult;
                offsetSamples = sr.BaseOffsetSamples;
                offsetMs = sampleRate > 0 ? (float)(sr.BaseOffsetSamples * 1000.0 / sampleRate) : 0f;
                driftPpm = (float)sr.DriftPpm;
                confidence = sr.Confidence;
            } else if (losslessReference != null && losslessReference.HasSyncedLosslessTwin) {
                state = LosslessTwinVerificationState.Synced;
                twinName = Path.GetFileName(losslessReference.TwinFilePath ?? string.Empty);
                confidence = losslessReference.SyncConfidence;
            }

            return new LgsrDiagnostics {
                State = state,
                IsActivePreHrtf = lastBlockActivePreHrtf && state == LosslessTwinVerificationState.Synced,
                TwinFileName = twinName,
                TwinFormatSummary = twinFmt,
                SyncOffsetSamples = offsetSamples,
                SyncOffsetMs = offsetMs,
                DriftPpm = driftPpm,
                SyncConfidence = confidence,
                CouplingStartHz = activeMetadata.CouplingStartHz > 0f ? activeMetadata.CouplingStartHz : rt.EffectiveCouplingHz,
                SpxStartHz = activeMetadata.SpxStartHz > 0f ? activeMetadata.SpxStartHz : rt.EffectiveSpxHz,
                AverageCoherence = lastBlockActivePreHrtf ? rt.AverageCoherence : 0f,
                ActiveFusionRatio = lastBlockActivePreHrtf ? rt.ActiveFusionRatio : 0f,
                DynamicEnvelopeLockDb = lastBlockActivePreHrtf ? rt.DynamicEnvelopeLockDb : 0f,
                ClippingGuardActive = lastBlockActivePreHrtf && rt.ClippingGuardActive,
                RestoredHfEnergyDb = lastBlockActivePreHrtf ? rt.RestoredHfEnergyDb : 0f
            };
        }
    }

    /// <inheritdoc/>
    public void Reset() {
        lock (stateLock) {
            fusionEngine.Reset();
            activeBlockTrackSamplePos = 0L;
            preHrtfSampleCursor = 0L;
            lastBlockActivePreHrtf = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (stateLock) {
            fusionEngine.Dispose();
            losslessReference = null;
            lastBlockActivePreHrtf = false;
        }
    }

    void EnsureScratchCapacity(int count) {
        if (refLeftScratch.Length < count) {
            int alloc = Math.Max(2048, count);
            refLeftScratch = new float[alloc];
            refRightScratch = new float[alloc];
            stereoBusScratch[0] = new float[alloc];
            stereoBusScratch[1] = new float[alloc];
        }
    }
}
