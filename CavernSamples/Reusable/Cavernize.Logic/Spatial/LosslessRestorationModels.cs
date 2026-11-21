using Cavern.Restoration;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Verification and pairing state of the W09 Universal Lossless Twin Service for the active track.
/// </summary>
public enum LosslessTwinVerificationState {
    /// <summary>No lossless twin paired or searched for the current track.</summary>
    None = 0,

    /// <summary>Background discovery &amp; 3-stage GCC-PHAT synchronization in progress.</summary>
    Searching = 1,

    /// <summary>Automatically bypassed because the active spatial track is already lossless (TrueHD, DAMF/ADM, FLAC, WAV/PCM, LAF).</summary>
    BypassedAlreadyLossless = 2,

    /// <summary>Candidate lossless file rejected by <c>AudioQualityAnalyzer</c> as fake/transcoded lossless.</summary>
    BypassedFakeLossless = 3,

    /// <summary>Candidate lossless twin failed the 3-stage GCC-PHAT confidence gate (different master/remix/edit).</summary>
    BypassedLowConfidence = 4,

    /// <summary>Verified genuine lossless FLAC/WAV/ALAC twin sub-sample synchronized and ready for LGSR, Karaoke, Stems, Dialog &amp; Vocal Mix-Match.</summary>
    Synced = 5
}

/// <summary>
/// Immutable metadata snapshot describing a discovered lossless twin candidate paired by Slot 1.
/// </summary>
public sealed class LosslessTwinMetadata {
    /// <summary>Local filesystem path to the lossless twin file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Display filename of the lossless twin.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Codec family label (e.g., "FLAC", "WAV/PCM", "ALAC").</summary>
    public string CodecFamily { get; init; } = "FLAC";

    /// <summary>Native sample rate of the lossless twin before Kaiser sinc rate matching.</summary>
    public int NativeSampleRate { get; init; } = 48000;

    /// <summary>Effective bit depth verified by <c>AudioQualityAnalyzer</c> (16 or 24).</summary>
    public int EffectiveBitDepth { get; init; } = 16;

    /// <summary>True when flagged as upsampled Hi-Res (still usable at 48 kHz; surfaced transparently in diagnostics).</summary>
    public bool IsUpsampledHiRes { get; init; }

    /// <summary>Measured spectral bandwidth cutoff in Hz (0 if fast-heuristic only).</summary>
    public int SpectralCutoffHz { get; init; }

    /// <summary>Measured dynamic range in dB (0 if fast-heuristic only).</summary>
    public double DynamicRangeDb { get; init; }

    /// <summary>Compact one-line summary (e.g., "FLAC 24-bit / 44.1 kHz").</summary>
    public string FormatSummary => $"{CodecFamily} {EffectiveBitDepth}-bit / {NativeSampleRate / 1000.0:0.#} kHz";
}

/// <summary>
/// Immutable configuration snapshot for Slot 2C Lossless-Guided Atmos Restoration (<c>LGSR</c>) and the Universal Lossless Twin Service.
/// </summary>
public sealed class LgsrConfig : IEquatable<LgsrConfig> {
    /// <summary>Default studio-safe configuration (auto-discover genuine lossless twins, Pre-HRTF fusion 85%, Atmos Envelope Lock &amp; FLAC Clipping Guard ON).</summary>
    public static LgsrConfig Default { get; } = new();

    /// <summary>Enables real-time Tier 1 Pre-HRTF Bus lossless-guided spectral restoration when a synced twin is active.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Automatically discovers and synchronizes matching lossless FLAC/WAV/ALAC twins in the library on track load.</summary>
    public bool AutoDiscoverTwin { get; init; } = true;

    /// <summary>Master coherence spectral fusion strength in [0.0; 1.0] (default 0.85).</summary>
    public float FusionStrength { get; init; } = 0.85f;

    /// <summary>Locks lossless reference loudness to the live Atmos dynamic envelope (-18 LKFS / RMS ratio lock).</summary>
    public bool DynamicEnvelopeLock { get; init; } = true;

    /// <summary>Protects restored Atmos highs from hard-clipped / brickwalled FLAC transients via 4x True-Peak &amp; flat-top detection.</summary>
    public bool ClippingGuardEnabled { get; init; } = true;

    /// <summary>Uses live E-AC-3 <c>CouplingStartHz</c> and <c>SpxStartHz</c> from <c>EnhancedAC3Body</c> to target coupled/SPX bands.</summary>
    public bool UseBitstreamCouplingSpxGuide { get; init; } = true;

    /// <summary>Automatically bypasses LGSR when the active spatial stream is already lossless (TrueHD, DAMF/ADM, FLAC, WAV, LAF).</summary>
    public bool BypassOnAlreadyLossless { get; init; } = true;

    /// <summary>Automatically rejects candidate FLAC/WAV twins flagged as fake/transcoded lossless by <c>AudioQualityAnalyzer</c>.</summary>
    public bool RejectFakeLosslessTwins { get; init; } = true;

    /// <summary>Minimum 3-stage GCC-PHAT alignment confidence in [0.15; 0.90] required to engage spectral fusion.</summary>
    public float MinSyncConfidence { get; init; } = 0.35f;

    /// <summary>Enables Tier 3 <c>Apollo</c>/<c>AP-BWE</c> neural cleanup on Atmos-exclusive residuals (offline Studio Master &amp; unpaired lossy).</summary>
    public bool EnableTier3ResidualCleanup { get; init; }

    /// <summary>Returns a clamped, validated copy of this configuration.</summary>
    public LgsrConfig Sanitize() => new() {
        Enabled = Enabled,
        AutoDiscoverTwin = AutoDiscoverTwin,
        FusionStrength = Math.Clamp(FusionStrength, 0f, 1f),
        DynamicEnvelopeLock = DynamicEnvelopeLock,
        ClippingGuardEnabled = ClippingGuardEnabled,
        UseBitstreamCouplingSpxGuide = UseBitstreamCouplingSpxGuide,
        BypassOnAlreadyLossless = BypassOnAlreadyLossless,
        RejectFakeLosslessTwins = RejectFakeLosslessTwins,
        MinSyncConfidence = Math.Clamp(MinSyncConfidence, 0.15f, 0.90f),
        EnableTier3ResidualCleanup = EnableTier3ResidualCleanup
    };

    /// <inheritdoc/>
    public bool Equals(LgsrConfig other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }
        if (other is null) {
            return false;
        }
        return Enabled == other.Enabled
            && AutoDiscoverTwin == other.AutoDiscoverTwin
            && MathF.Abs(FusionStrength - other.FusionStrength) < 0.002f
            && DynamicEnvelopeLock == other.DynamicEnvelopeLock
            && ClippingGuardEnabled == other.ClippingGuardEnabled
            && UseBitstreamCouplingSpxGuide == other.UseBitstreamCouplingSpxGuide
            && BypassOnAlreadyLossless == other.BypassOnAlreadyLossless
            && RejectFakeLosslessTwins == other.RejectFakeLosslessTwins
            && MathF.Abs(MinSyncConfidence - other.MinSyncConfidence) < 0.002f
            && EnableTier3ResidualCleanup == other.EnableTier3ResidualCleanup;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is LgsrConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() {
        HashCode hc = new();
        hc.Add(Enabled);
        hc.Add(AutoDiscoverTwin);
        hc.Add(MathF.Round(FusionStrength, 3));
        hc.Add(DynamicEnvelopeLock);
        hc.Add(ClippingGuardEnabled);
        hc.Add(UseBitstreamCouplingSpxGuide);
        hc.Add(BypassOnAlreadyLossless);
        hc.Add(RejectFakeLosslessTwins);
        hc.Add(MathF.Round(MinSyncConfidence, 3));
        hc.Add(EnableTier3ResidualCleanup);
        return hc.ToHashCode();
    }
}

/// <summary>
/// Live telemetry snapshot for W09 Universal Lossless Twin Service and Slot 2C LGSR.
/// Published into <see cref="SpatialSceneHub.LgsrDiagnostics"/> for Settings, Audio Spec dialog, and W13 Spatial X-Ray HUD.
/// </summary>
public struct LgsrDiagnostics {
    /// <summary>Current verification/sync state of the Universal Lossless Twin Service.</summary>
    public LosslessTwinVerificationState State { get; set; }

    /// <summary>True when Tier 1 Pre-HRTF spectral fusion is actively restoring audio on the current stream.</summary>
    public bool IsActivePreHrtf { get; set; }

    /// <summary>Display filename of the paired lossless twin (empty when none).</summary>
    public string TwinFileName { get; set; }

    /// <summary>Compact format summary of the paired lossless twin (e.g., "FLAC 24-bit / 44.1 kHz").</summary>
    public string TwinFormatSummary { get; set; }

    /// <summary>Sub-sample alignment offset in samples (<c>losslessIndex - atmosIndex</c>).</summary>
    public double SyncOffsetSamples { get; set; }

    /// <summary>Sub-sample alignment offset in milliseconds.</summary>
    public float SyncOffsetMs { get; set; }

    /// <summary>Theil-Sen estimated clock drift in parts-per-million (ppm).</summary>
    public float DriftPpm { get; set; }

    /// <summary>3-stage GCC-PHAT alignment confidence in [0; 1].</summary>
    public float SyncConfidence { get; set; }

    /// <summary>Live E-AC-3 channel coupling start frequency in Hz (0 when inactive or non-E-AC-3).</summary>
    public float CouplingStartHz { get; set; }

    /// <summary>Live E-AC-3 Spectral Extension (SPX) start frequency in Hz (0 when inactive or non-E-AC-3).</summary>
    public float SpxStartHz { get; set; }

    /// <summary>Live mean Magnitude-Squared Coherence <c>C_xy(t, f)</c> in [0; 1].</summary>
    public float AverageCoherence { get; set; }

    /// <summary>Live effective Pre-HRTF spectral fusion blend ratio in [0; 1].</summary>
    public float ActiveFusionRatio { get; set; }

    /// <summary>Live Atmos Dynamic Envelope Lock gain applied to the lossless twin in dB.</summary>
    public float DynamicEnvelopeLockDb { get; set; }

    /// <summary>True when the FLAC Brickwall / Clipping Guard actively attenuated HF injection on recent transients.</summary>
    public bool ClippingGuardActive { get; set; }

    /// <summary>Latest restored high-frequency spectral energy delta in dB.</summary>
    public float RestoredHfEnergyDb { get; set; }

    /// <summary>Returns a clean default diagnostic snapshot.</summary>
    public static LgsrDiagnostics Default => new() {
        State = LosslessTwinVerificationState.None,
        IsActivePreHrtf = false,
        TwinFileName = string.Empty,
        TwinFormatSummary = string.Empty,
        SyncOffsetSamples = 0.0,
        SyncOffsetMs = 0f,
        DriftPpm = 0f,
        SyncConfidence = 0f,
        CouplingStartHz = 0f,
        SpxStartHz = 0f,
        AverageCoherence = 0f,
        ActiveFusionRatio = 0f,
        DynamicEnvelopeLockDb = 0f,
        ClippingGuardActive = false,
        RestoredHfEnergyDb = 0f
    };

    /// <summary>Builds human-readable open-source curation attribution for W09 LGSR &amp; sync algorithms.</summary>
    public static string BuildAttributionNotice() =>
        "Slot 1/2C Universal Lossless Twin & LGSR Open-Source Curation:\n\n" +
        "• sigsep/norbert (Iterative EM Multichannel Wiener Filter / EM-MWF) — MIT License\n" +
        "• synctoolbox & audalign (Multi-Scale Onset Macro Sync + Sliding GCC-PHAT + Theil-Sen Drift) — MIT License\n" +
        "• Kaiser-Windowed Linear-Phase Polyphase Sinc Resampler (>120 dB Stopband) & 4-pt Cubic Farrow Delay\n" +
        "• ATSC A/52 & ETSI TS 102 366 Live E-AC-3 Coupling/SPX Bitstream Guidance\n" +
        "• JusperLee/Apollo & yxlu-0102/AP-BWE (Tier 3 Residual Neural Cleanup) — MIT / Apache-2.0 License";
}
