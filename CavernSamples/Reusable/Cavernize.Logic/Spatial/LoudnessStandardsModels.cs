using Cavern.Filters;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// ITU-R BS.1770-5 / EBU R128 / Dolby Dialnorm loudness normalization target modes for Slot 6B.
/// </summary>
public enum LoudnessNormalizationMode {
    /// <summary>Bit-transparent metering only (0.00 dB unity gain applied).</summary>
    Off = 0,

    /// <summary>EBU R128 / ITU-R BS.1770-5 broadcast reference (-23.0 LUFS).</summary>
    EbuR128Minus23 = 1,

    /// <summary>Dolby Atmos Music / cinema spatial mastering reference (-18.0 LUFS).</summary>
    AtmosMusicMinus18 = 2,

    /// <summary>AES TD1008 / modern streaming reference (-14.0 LUFS).</summary>
    StreamingMinus14 = 3,

    /// <summary>ATSC A/52 Dolby Dialnorm bitstream reference (-31 dBFS anchor for Dolby streams, -27 LUFS fallback).</summary>
    DolbyDialnorm = 4
}

/// <summary>
/// Immutable configuration snapshot for Slot 6B (Psychoacoustics &amp; Studio Loudness Standards):
/// ISO 226:2023 Dynamic Equal-Loudness, ITU-R BS.1770-5 / EBU R128 loudness normalization + Dolby Dialnorm,
/// Spatial Dialog Enhancement, and 4x-oversampled True-Peak Brickwall Limiter.
/// </summary>
public sealed class PsychoacousticLoudnessConfig : IEquatable<PsychoacousticLoudnessConfig> {
    /// <summary>Neutral default configuration: zero coloration (ISO 226, norm, dialog OFF; 4x -0.1 dBTP limiter ON).</summary>
    public static PsychoacousticLoudnessConfig Default { get; } = new();

    /// <summary>Enables ISO 226:2023 dynamic equal-loudness compensation (Priority 300).</summary>
    public bool Iso226Enabled { get; init; }

    /// <summary>Studio reference playback level in phon (70..85, default 80 phon).</summary>
    public float Iso226ReferenceSplPhon { get; init; } = Iso226LoudnessFilter.DefaultReferenceSplPhon;

    /// <summary>Manual listening SPL in phon (40..95, default 68 phon) used when <see cref="Iso226TrackVolume"/> is false.</summary>
    public float Iso226ListeningSplPhon { get; init; } = Iso226LoudnessFilter.DefaultListeningSplPhon;

    /// <summary>When true, derives listening SPL automatically from master playback volume relative to reference SPL.</summary>
    public bool Iso226TrackVolume { get; init; } = true;

    /// <summary>Active ITU-R BS.1770-5 / EBU R128 / Dolby Dialnorm loudness normalization target.</summary>
    public LoudnessNormalizationMode NormalizationMode { get; init; } = LoudnessNormalizationMode.Off;

    /// <summary>
    /// When true, uses bitstream Dolby Dialnorm metadata (e.g., -27 or -31 LUFS) as the static programme loudness
    /// anchor to preserve 100% macro-dynamics without adaptive gain drift.
    /// </summary>
    public bool RespectDolbyDialnorm { get; init; } = true;

    /// <summary>Enables zero-smearing Spatial Dialog Enhancement (Path A spatial isolation / Path B LR4 Mid/Side).</summary>
    public bool DialogEnhancementEnabled { get; init; }

    /// <summary>Dialogue intelligibility boost in dB (0..6 dB, default 3 dB).</summary>
    public float DialogEnhancementDb { get; init; } = SpatialDialogEnhancer.DefaultBoostDb;

    /// <summary>Enables the final 4x-oversampled BS.1770-5 True-Peak brickwall limiter (Priority 900).</summary>
    public bool TruePeakLimiterEnabled { get; init; } = true;

    /// <summary>True-Peak brickwall ceiling in dBTP (-6.0..0.0 dBTP; default -0.1 dBTP lossless, -1.0 dBTP EBU R128).</summary>
    public float TruePeakCeilingDbtp { get; init; } = TruePeakLimiter.DefaultCeilingDbtp;

    /// <summary>Returns a clamped, validated copy of this configuration.</summary>
    public PsychoacousticLoudnessConfig Sanitize() => new() {
        Iso226Enabled = Iso226Enabled,
        Iso226ReferenceSplPhon = Math.Clamp(Iso226ReferenceSplPhon, Iso226LoudnessFilter.MinReferenceSplPhon, Iso226LoudnessFilter.MaxReferenceSplPhon),
        Iso226ListeningSplPhon = Math.Clamp(Iso226ListeningSplPhon, Iso226LoudnessFilter.MinListeningSplPhon, Iso226LoudnessFilter.MaxListeningSplPhon),
        Iso226TrackVolume = Iso226TrackVolume,
        NormalizationMode = Enum.IsDefined(NormalizationMode) ? NormalizationMode : LoudnessNormalizationMode.Off,
        RespectDolbyDialnorm = RespectDolbyDialnorm,
        DialogEnhancementEnabled = DialogEnhancementEnabled,
        DialogEnhancementDb = Math.Clamp(DialogEnhancementDb, SpatialDialogEnhancer.MinBoostDb, SpatialDialogEnhancer.MaxBoostDb),
        TruePeakLimiterEnabled = TruePeakLimiterEnabled,
        TruePeakCeilingDbtp = Math.Clamp(TruePeakCeilingDbtp, TruePeakLimiter.MinCeilingDbtp, TruePeakLimiter.MaxCeilingDbtp)
    };

    /// <inheritdoc/>
    public bool Equals(PsychoacousticLoudnessConfig other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }
        if (other is null) {
            return false;
        }
        return Iso226Enabled == other.Iso226Enabled
            && MathF.Abs(Iso226ReferenceSplPhon - other.Iso226ReferenceSplPhon) < 0.05f
            && MathF.Abs(Iso226ListeningSplPhon - other.Iso226ListeningSplPhon) < 0.05f
            && Iso226TrackVolume == other.Iso226TrackVolume
            && NormalizationMode == other.NormalizationMode
            && RespectDolbyDialnorm == other.RespectDolbyDialnorm
            && DialogEnhancementEnabled == other.DialogEnhancementEnabled
            && MathF.Abs(DialogEnhancementDb - other.DialogEnhancementDb) < 0.05f
            && TruePeakLimiterEnabled == other.TruePeakLimiterEnabled
            && MathF.Abs(TruePeakCeilingDbtp - other.TruePeakCeilingDbtp) < 0.02f;
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is PsychoacousticLoudnessConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() {
        HashCode hc = new();
        hc.Add(Iso226Enabled);
        hc.Add(MathF.Round(Iso226ReferenceSplPhon, 1));
        hc.Add(MathF.Round(Iso226ListeningSplPhon, 1));
        hc.Add(Iso226TrackVolume);
        hc.Add(NormalizationMode);
        hc.Add(RespectDolbyDialnorm);
        hc.Add(DialogEnhancementEnabled);
        hc.Add(MathF.Round(DialogEnhancementDb, 1));
        hc.Add(TruePeakLimiterEnabled);
        hc.Add(MathF.Round(TruePeakCeilingDbtp, 2));
        return hc.ToHashCode();
    }
}

/// <summary>
/// Live telemetry snapshot for Slot 6B Psychoacoustics &amp; Studio Loudness Standards.
/// Published into <see cref="SpatialSceneHub.LoudnessDiagnostics"/> for Settings / Player HUD &amp; W09/W11 reuse.
/// </summary>
public struct LoudnessStandardsDiagnostics {
    /// <summary>Momentary loudness (400 ms) in LUFS, or NaN when inactive.</summary>
    public float MomentaryLufs { get; set; }

    /// <summary>Short-term loudness (3.0 s) in LUFS, or NaN when inactive.</summary>
    public float ShortTermLufs { get; set; }

    /// <summary>Two-stage gated Integrated loudness (BS.1770-5) in LUFS, or NaN when unmeasured.</summary>
    public float IntegratedLufs { get; set; }

    /// <summary>EBU Tech 3342 Loudness Range (LRA) in LU.</summary>
    public float LoudnessRangeLu { get; set; }

    /// <summary>Latest 4x-oversampled True Peak in dBTP.</summary>
    public float TruePeakDbtp { get; set; }

    /// <summary>Running maximum 4x True Peak in dBTP since last reset.</summary>
    public float MaxTruePeakDbtp { get; set; }

    /// <summary>Instantaneous Peak-to-RMS Crest Factor in dB.</summary>
    public float CrestFactorDb { get; set; }

    /// <summary>Detected bitstream Dolby Dialnorm in LUFS, or NaN when absent.</summary>
    public float DialnormLufs { get; set; }

    /// <summary>Current smooth BS.1770 / Dialnorm loudness normalization gain applied in dB.</summary>
    public float AppliedNormGainDb { get; set; }

    /// <summary>Current 4x True-Peak limiter gain reduction in dB (&lt;= 0.0 dB).</summary>
    public float LimiterGainReductionDb { get; set; }

    /// <summary>True when ISO 226:2023 compensation is actively shaping the spectrum (not in unity bypass).</summary>
    public bool Iso226Active { get; set; }

    /// <summary>Effective listening SPL in phon driving ISO 226:2023.</summary>
    public float Iso226EffectivePhon { get; set; }

    /// <summary>Maximum ISO 226:2023 bass/treble compensation boost in dB.</summary>
    public float Iso226MaxBoostDb { get; set; }

    /// <summary>RMS fitting error of the 6-band ISO 226:2023 IIR cascade in dB.</summary>
    public float Iso226FitRmsErrDb { get; set; }

    /// <summary>True when Spatial Dialog Enhancement is active.</summary>
    public bool DialogActive { get; set; }

    /// <summary>True when using Path A (discrete Center/object spatial isolation); false when using Path B stereo LR4 Mid/Side.</summary>
    public bool DialogIsSpatialMode { get; set; }

    /// <summary>Active Path B speech activity confidence [0; 1] (or 1.0 when Path A spatial isolation is engaged).</summary>
    public float DialogSpeechActivity { get; set; }

    /// <summary>Configured True-Peak brickwall ceiling in dBTP.</summary>
    public float TruePeakCeilingDbtp { get; set; }

    /// <summary>Returns a clean default diagnostic snapshot.</summary>
    public static LoudnessStandardsDiagnostics Default => new() {
        MomentaryLufs = float.NaN,
        ShortTermLufs = float.NaN,
        IntegratedLufs = float.NaN,
        LoudnessRangeLu = 0f,
        TruePeakDbtp = -120f,
        MaxTruePeakDbtp = -120f,
        CrestFactorDb = 0f,
        DialnormLufs = float.NaN,
        AppliedNormGainDb = 0f,
        LimiterGainReductionDb = 0f,
        Iso226Active = false,
        Iso226EffectivePhon = Iso226LoudnessFilter.DefaultReferenceSplPhon,
        Iso226MaxBoostDb = 0f,
        Iso226FitRmsErrDb = 0f,
        DialogActive = false,
        DialogIsSpatialMode = false,
        DialogSpeechActivity = 0f,
        TruePeakCeilingDbtp = TruePeakLimiter.DefaultCeilingDbtp
    };
}
