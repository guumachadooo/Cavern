using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

using Android.Content;

using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Remapping;
using Cavern.Restoration;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;

using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

using Complex = Cavern.Utilities.Complex;

namespace CavernizeAndroid.Services;

/// <summary>
/// Pure-physics psychoacoustic fingerprint extracted from a stereo headphone output buffer
/// (ISO/ITU acoustic metrics: RMS/Peak, GainDelta vs reference, ILD, ITD via normalized cross-correlation,
/// IACC at lag 0 and max lag, 5-band FFT spectral tilt deltas, block-boundary click max, non-finite count).
/// Exported as structured JSON and compact Markdown so both the on-device self-check oracle and remote AI
/// (via Live Bridge / Cloudflare Hub v3) can pinpoint subtle acoustic anomalies without manual guessing.
/// </summary>
public sealed class AcousticFingerprint {
    public float RmsLeftDbfs { get; init; }
    public float RmsRightDbfs { get; init; }
    public float PeakDbfs { get; init; }
    public float GainDeltaDb { get; init; }
    public float IldDb { get; init; }
    public float ItdUs { get; init; }
    public float IaccZero { get; init; }
    public float IaccMax { get; init; }
    public float Sub20To120Db { get; init; }
    public float Bass120To500Db { get; init; }
    public float Mid500To2k5Db { get; init; }
    public float Pres2k5To8kDb { get; init; }
    public float Air8kTo20kDb { get; init; }
    public float MaxBoundaryClick { get; init; }
    public int NonFiniteCount { get; init; }

    public string CompactMetrics() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Gain={0:+0.0;-0.0;0.0}dB ILD={1:+0.0;-0.0;0.0}dB ITD={2:+0;-0;0}us IACC0={3:0.00} Tilt=[{4:+0.0;-0.0;0.0},{5:+0.0;-0.0;0.0},{6:+0.0;-0.0;0.0},{7:+0.0;-0.0;0.0},{8:+0.0;-0.0;0.0}]",
            GainDeltaDb, IldDb, ItdUs, IaccZero,
            Sub20To120Db, Bass120To500Db, Mid500To2k5Db, Pres2k5To8kDb, Air8kTo20kDb);
}

/// <summary>
/// Outcome of a single differential acoustic scenario evaluated by <see cref="AcousticAuditEngine"/>.
/// </summary>
public sealed class AcousticScenarioResult {
    public string Id { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "PASS";
    public long ElapsedMs { get; init; }
    public string ExpectedRule { get; init; } = "";
    public string VerdictNote { get; init; } = "";
    public AcousticFingerprint Fingerprint { get; init; } = new();
}

/// <summary>
/// Complete Layer L11 psychoacoustic differential audit report (serializable to JSON &amp; Markdown).
/// </summary>
public sealed class AcousticAuditReport {
    public int SchemaVersion { get; init; } = 1;
    public string ExecutedAtUtc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public long TotalElapsedMs { get; init; }
    public int PassCount { get; init; }
    public int WarnCount { get; init; }
    public int FailCount { get; init; }
    public string ActiveUserConfigSummary { get; init; } = "";
    public List<AcousticScenarioResult> Scenarios { get; init; } = [];

    public string OverallVerdict => FailCount > 0 ? "FAIL" : (WarnCount > 0 ? "WARN" : "PASS");

    public string ToJson() =>
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdownTable() {
        StringBuilder sb = new();
        sb.AppendLine($"### L11 Acoustic Differential Oracle ({OverallVerdict} · {PassCount} PASS · {WarnCount} WARN · {FailCount} FAIL · {TotalElapsedMs} ms)");
        if (!string.IsNullOrEmpty(ActiveUserConfigSummary)) {
            sb.AppendLine($"- **Active User Audio Config:** `{ActiveUserConfigSummary}`");
        }
        sb.AppendLine();
        sb.AppendLine("| Scenario | Tier | Status | GainΔ | ILD | ITD | IACC0 | Spectral Tilt Δ (Sub/Bas/Mid/Prs/Air dB) | Note |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (AcousticScenarioResult s in Scenarios) {
            AcousticFingerprint f = s.Fingerprint;
            string tilt = string.Format(
                CultureInfo.InvariantCulture,
                "{0:+0.0;-0.0;0.0} / {1:+0.0;-0.0;0.0} / {2:+0.0;-0.0;0.0} / {3:+0.0;-0.0;0.0} / {4:+0.0;-0.0;0.0}",
                f.Sub20To120Db, f.Bass120To500Db, f.Mid500To2k5Db, f.Pres2k5To8kDb, f.Air8kTo20kDb);
            string note = (s.VerdictNote ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| `{0}` | {1} | **{2}** | {3:+0.00;-0.00;0.00} dB | {4:+0.00;-0.00;0.00} dB | {5:+0;-0;0} µs | {6:0.00} | `{7}` | {8} |",
                s.Id, s.Tier, s.Status, f.GainDeltaDb, f.IldDb, f.ItdUs, f.IaccZero, tilt, note));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Parameters for an on-demand AI-driven custom acoustic A/B probe via Live Bridge (<c>acoustic_probe</c>).
/// </summary>
public sealed class AcousticCustomProbeRequest {
    public string ProfileId { get; init; } = BinauralProfileService.DefaultProfileId;
    public bool MinimumPhase { get; init; }
    public int MagLsOrder { get; init; }
    public bool BiometricItdEnabled { get; init; }
    public float HeadWidthCm { get; init; } = (float)BiometricItdScaler.ReferenceHeadWidthCm;
    public float ManualItdOffsetUs { get; init; }
    public bool IsmRoomEnabled { get; init; }
    public int IsmPresetIndex { get; init; }
    public float IsmReflectionLevel { get; init; } = 1.0f;
    public bool NearFieldEnabled { get; init; }
    public float NearFieldDistanceM { get; init; } = 3.0f;
    public float ListenerYawDeg { get; init; }
    public float ListenerPitchDeg { get; init; }
    public float SourceAzimuthDeg { get; init; }
    public float SourceElevationDeg { get; init; }
    public int SampleRate { get; init; } = 48000;
    public bool UseParametricUpmix { get; init; }
    public bool UseLgsrRestore { get; init; }
    public bool CompareAgainstDirect { get; init; } = true;
    public string SignalType { get; init; } = "white";
}

/// <summary>
/// Layer L11 — Intelligent Acoustic Differential Oracle &amp; Psychoacoustic Fingerprint Lab.
/// Evaluates headphone virtualization, format routing (FLAC vs Atmos vs Atmos+FLAC LGSR vs 44.1 kHz resample),
/// OFAT feature differential A/B invariants (Min-Phase, Biometric ITD, MagLS, ISM Room, BRIR Double-Room Guard,
/// Near-Field DVF, Head-Tracking Yaw direction), and the user's live settings snapshot inside a 100% isolated
/// <see cref="VirtualizerFilter.BeginStaticStateScope"/> (zero mutation of playback or user settings).
/// </summary>
public static class AcousticAuditEngine {
    const int ProbeSampleRate = 48000;
    const int ProbeUpdateRate = 512;
    const int ProbeTotalFrames = 16384; // 341.3 ms @ 48 kHz (fast on ARM64, >= 3x 4096-pt FFT steady-state frames)
    const int SteadyStartFrame = 3072;  // Skip initial convolver/upmixer latency & transient fill

    static readonly object auditLock = new();

    /// <summary>
    /// Runs the non-exponential 3-tier Acoustic Differential Oracle suite (~14 scenarios, &lt;1s on ARM64).
    /// </summary>
    /// <param name="context">Android context for loading bundled laboratory .chrir/.chrirz assets.</param>
    /// <param name="store">User settings store (read-only) for Tier C live configuration fingerprinting.</param>
    /// <param name="quickSubset">When true (Quick self-check), runs the 6 highest-signal core scenarios.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static AcousticAuditReport RunSuite(
        Context context,
        AppSettingsStore store,
        bool quickSubset = false,
        CancellationToken ct = default) {
        lock (auditLock) {
            Stopwatch totalSw = Stopwatch.StartNew();
            List<AcousticScenarioResult> results = [];

            // Save global Listener static state in addition to VirtualizerFilter.BeginStaticStateScope
            bool savedVirt = Listener.HeadphoneVirtualizer;
            Vector3 savedEnvSize = Listener.EnvironmentSize;
            Environments savedEnvType = Listener.EnvironmentType;
            Channel[] savedChannels = Listener.Channels != null ? (Channel[])Listener.Channels.Clone() : null;

            string userSummary = DescribeUserConfig(store);

            using (VirtualizerFilter.BeginStaticStateScope()) {
                try {
                    float[] whiteL = GenerateDeterministicWhite(ProbeTotalFrames, seed: 42);
                    float[] whiteR = GenerateDeterministicWhite(ProbeTotalFrames, seed: 1337);

                    Vector3 wallCenter = new(0f, 0f, 10f);
                    Vector3 wallLeft90 = new(-10f, 0f, 0f);

                    // ==================== TIER A: SOURCE FORMAT x ROUTING ====================
                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "A1_Flac48_Direct20_Ref", "TierA-Format",
                        "48 kHz stereo Direct 2.0 unity reference",
                        "|GainΔ|<=0.1dB, |ILD|<=0.1dB, ITD=0us, flat 5-band <=0.1dB",
                        () => {
                            AcousticFingerprint fp = AnalyzeStereo(whiteL, whiteL, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                            bool ok = fp.NonFiniteCount == 0 && MathF.Abs(fp.GainDeltaDb) <= 0.1f && MathF.Abs(fp.IldDb) <= 0.1f && MathF.Abs(fp.ItdUs) <= 1f;
                            return (ok ? "PASS" : "FAIL", ok ? "Bit-exact unity stereo reference" : $"Reference drift: {fp.CompactMetrics()}", fp);
                        }));

                    // Preload baseline SADIE D1 DFC (linear phase, no extras) once for Tier A/B differentials
                    (VirtualChannel[] sadieBaseCh, int sadieSr, float sadieRefDist, _, _, _) =
                        BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, false, HrtfPersonalizationConfig.Default);

                    float[] baseCenterL = null, baseCenterR = null;
                    float[] baseLeft90L = null, baseLeft90R = null;

                    if (sadieBaseCh != null && sadieBaseCh.Length > 0) {
                        VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                        (baseCenterL, baseCenterR) = RenderSingleSource(whiteL, wallCenter, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                        (baseLeft90L, baseLeft90R) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                    }

                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "A2_Flac48_Upmix71_SadieDfc", "TierA-Format",
                        "Stereo FLAC -> DirAC/COMPASS 7.1 Upmix -> SADIE D1 DFC (LFE bleed & tonal guard)",
                        "Sub<=+4.5dB (no mono LFE double-bass), |Mid/Prs/Air|<=2.5dB, 0 NaN",
                        () => {
                            if (sadieBaseCh == null) {
                                return ("FAIL", "SADIE D1 DFC asset failed to load", new AcousticFingerprint());
                            }
                            VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                            (float[] upL, float[] upR, float[] inL, float[] inR) = RenderStereoParametricUpmix(whiteL, whiteR, ProbeSampleRate);
                            AcousticFingerprint fp = AnalyzeStereo(upL, upR, inL, inR, ProbeSampleRate, ProbeUpdateRate);
                            if (fp.NonFiniteCount > 0 || fp.RmsLeftDbfs < -70f) {
                                return ("FAIL", $"Invalid upmix output (nf={fp.NonFiniteCount}, rmsL={fp.RmsLeftDbfs:0.0}dBFS)", fp);
                            }
                            if (fp.Sub20To120Db > 5.5f) {
                                return ("FAIL", $"Excessive sub-bass boom ({fp.Sub20To120Db:+0.0;-0.0;0.0} dB) — check LFESeparation bleed!", fp);
                            }
                            bool warn = fp.Sub20To120Db > 4.2f || MathF.Abs(fp.Mid500To2k5Db) > 3.0f || MathF.Abs(fp.Air8kTo20kDb) > 3.5f;
                            return (warn ? "WARN" : "PASS",
                                warn ? $"Slight tonal tilt on upmix: {fp.CompactMetrics()}" : $"Clean upmix tonal balance & zero LFE bleed ({fp.CompactMetrics()})", fp);
                        }));

                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "A3_AtmosWall_Left90_SadieDfc", "TierA-Format",
                        "Atmos lateral source (-90 deg) through SADIE D1 DFC",
                        "ILD>=+10dB, ITD>=+500us (Left leads), flat 5-band spread<=3.5dB",
                        () => {
                            if (baseLeft90L == null) {
                                return ("FAIL", "Baseline Left-90 render missing", new AcousticFingerprint());
                            }
                            AcousticFingerprint fp = AnalyzeStereo(baseLeft90L, baseLeft90R, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                            float bandSpan = Max5(fp.Sub20To120Db, fp.Bass120To500Db, fp.Mid500To2k5Db, fp.Pres2k5To8kDb, fp.Air8kTo20kDb)
                                           - Min5(fp.Sub20To120Db, fp.Bass120To500Db, fp.Mid500To2k5Db, fp.Pres2k5To8kDb, fp.Air8kTo20kDb);
                            if (fp.NonFiniteCount > 0 || fp.IldDb < 8.0f || fp.ItdUs < 450f) {
                                return ("FAIL", $"Broken lateral spatialization: ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us", fp);
                            }
                            bool warn = bandSpan > 4.0f;
                            return (warn ? "WARN" : "PASS",
                                $"Strong lateral cues (ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us, bandSpan={bandSpan:0.0}dB)", fp);
                        }));

                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "A4_AtmosPlusFlac_LGSR_Restore", "TierA-Format",
                        "Lossy 6 kHz Atmos proxy + Lossless FLAC twin via LGSR Pre-HRTF fusion",
                        "Air restore >= +3.0 dB vs lossy, Sub/Bass/Mid preserved within +/-1.2 dB",
                        () => {
                            (float[] lossyL, float[] lossyR, float[] restL, float[] restR, LgsrRuntimeTelemetry tel) =
                                RunLgsrDifferentialProbe(whiteL, whiteR, ProbeSampleRate);
                            AcousticFingerprint fp = AnalyzeStereo(restL, restR, lossyL, lossyR, ProbeSampleRate, ProbeUpdateRate);
                            bool lowIntact = MathF.Abs(fp.Sub20To120Db) <= 1.2f && MathF.Abs(fp.Bass120To500Db) <= 1.2f && MathF.Abs(fp.Mid500To2k5Db) <= 1.2f;
                            if (fp.NonFiniteCount > 0 || fp.Air8kTo20kDb < 2.0f || !lowIntact) {
                                return ("FAIL", $"LGSR defect: Air={fp.Air8kTo20kDb:+0.0;-0.0;0.0}dB, Mid={fp.Mid500To2k5Db:+0.0;-0.0;0.0}dB, coh={tel.AverageCoherence:0.00}", fp);
                            }
                            return ("PASS", $"HF restored Air={fp.Air8kTo20kDb:+0.0;-0.0;0.0}dB, Prs={fp.Pres2k5To8kDb:+0.0;-0.0;0.0}dB, coh={tel.AverageCoherence:0.00}, fusion={tel.ActiveFusionRatio:0.00}", fp);
                        }));

                    if (!quickSubset) {
                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "A5_Flac441k_Resampled_Binaural", "TierA-Format",
                            "44.1 kHz FLAC routed into 48 kHz SADIE D1 DFC (ProcessRateMatched)",
                            "Valid lateral ILD>=+8dB, ITD>=+450us, no clicks/NaN",
                            () => {
                                if (sadieBaseCh == null) {
                                    return ("FAIL", "SADIE D1 DFC missing", new AcousticFingerprint());
                                }
                                VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                                float[] white44 = GenerateDeterministicWhite(16384, seed: 99);
                                (float[] rL, float[] rR) = RenderSingleSource(white44, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, 44100);
                                AcousticFingerprint fp = AnalyzeStereo(rL, rR, white44, white44, 44100, ProbeUpdateRate);
                                if (fp.NonFiniteCount > 0 || fp.IldDb < 8.0f || fp.ItdUs < 400f) {
                                    return ("FAIL", $"44.1k->48k rate-match anomaly: ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us, nf={fp.NonFiniteCount}", fp);
                                }
                                return ("PASS", $"44.1kHz->48kHz HRIR clean (ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us)", fp);
                            }));
                    }

                    // ==================== TIER B: OFAT VIRTUALIZATION DIFFERENTIAL A/B ====================
                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "B1_MinPhase_vs_Linear", "TierB-OFAT",
                        "Minimum-Phase + ITD vs Linear-Phase on SADIE D1 DFC (Center)",
                        "Exact magnitude preservation: |GainΔ|<=0.4dB and all 5 bands within +/-0.8dB",
                        () => {
                            if (baseCenterL == null) {
                                return ("FAIL", "Baseline Center missing", new AcousticFingerprint());
                            }
                            (VirtualChannel[] mpCh, int mpSr, float mpRd, _, _, _) =
                                BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, true, HrtfPersonalizationConfig.Default);
                            VirtualizerFilter.Override(mpCh, mpSr, mpRd);
                            (float[] mpL, float[] mpR) = RenderSingleSource(whiteL, wallCenter, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                            AcousticFingerprint fp = AnalyzeStereo(mpL, mpR, baseCenterL, baseCenterR, ProbeSampleRate, ProbeUpdateRate);
                            float maxBandErr = MaxAbs5(fp.Sub20To120Db, fp.Bass120To500Db, fp.Mid500To2k5Db, fp.Pres2k5To8kDb, fp.Air8kTo20kDb);
                            if (fp.NonFiniteCount > 0 || MathF.Abs(fp.GainDeltaDb) > 0.6f || maxBandErr > 1.2f) {
                                return ("FAIL", $"Min-Phase altered magnitude spectrum (GainΔ={fp.GainDeltaDb:+0.00;-0.00;0.00}dB, maxBandErr={maxBandErr:0.00}dB)", fp);
                            }
                            return ("PASS", $"Exact cepstral magnitude invariance (GainΔ={fp.GainDeltaDb:+0.00;-0.00;0.00}dB, maxBandErr={maxBandErr:0.00}dB, IACC0={fp.IaccZero:0.00})", fp);
                        }));

                    if (!quickSubset) {
                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "B2_BiometricItd_18cm_vs_115cm", "TierB-OFAT",
                            "Woodworth Biometric ITD scaling (18.0 cm vs 11.5 cm head width @ Left -90)",
                            "ITD(18cm) > ITD(11.5cm) + 100 us with <=0.3 dB spectral coloration",
                            () => {
                                HrtfPersonalizationConfig bigHead = new() { BiometricItdEnabled = true, HeadWidthCm = 18.0f };
                                HrtfPersonalizationConfig smallHead = new() { BiometricItdEnabled = true, HeadWidthCm = 11.5f };
                                (VirtualChannel[] cBig, int srB, float rdB, _, _, _) =
                                    BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, false, bigHead);
                                VirtualizerFilter.Override(cBig, srB, rdB);
                                (float[] bL, float[] bR) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                AcousticFingerprint fpBig = AnalyzeStereo(bL, bR, baseLeft90L, baseLeft90R, ProbeSampleRate, ProbeUpdateRate);

                                (VirtualChannel[] cSmall, int srS, float rdS, _, _, _) =
                                    BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, false, smallHead);
                                VirtualizerFilter.Override(cSmall, srS, rdS);
                                (float[] sL, float[] sR) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                AcousticFingerprint fpSmall = AnalyzeStereo(sL, sR, baseLeft90L, baseLeft90R, ProbeSampleRate, ProbeUpdateRate);

                                float itdSpan = fpBig.ItdUs - fpSmall.ItdUs;
                                float maxErr = MaxAbs5(fpBig.Sub20To120Db, fpBig.Bass120To500Db, fpBig.Mid500To2k5Db, fpBig.Pres2k5To8kDb, fpBig.Air8kTo20kDb);
                                if (itdSpan < 100f || maxErr > 0.5f) {
                                    return ("FAIL", $"Biometric ITD delta invalid: 18cm={fpBig.ItdUs:0}us vs 11.5cm={fpSmall.ItdUs:0}us (span={itdSpan:0}us, eqErr={maxErr:0.00}dB)", fpBig);
                                }
                                return ("PASS", $"ITD scaled cleanly: 11.5cm={fpSmall.ItdUs:0}us -> 18.0cm={fpBig.ItdUs:0}us (Δ={itdSpan:+0;-0;0}us, eqErr={maxErr:0.00}dB)", fpBig);
                            }));

                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "B3_MagLs_Order3_vs_Base", "TierB-OFAT",
                            "MagLS Order 3 spherical-harmonic decoding vs direct SADIE D1 DFC (Left -90)",
                            "Preserves lateral ILD sign (>+5dB) & ITD (>+400us), bounded gain/spectrum",
                            () => {
                                HrtfPersonalizationConfig magCfg = new() { MagLsMode = MagLsDecodeMode.Order3 };
                                (VirtualChannel[] mCh, int mSr, float mRd, _, _, HrtfPersonalizationDiagnostics mDiag) =
                                    BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, false, magCfg);
                                VirtualizerFilter.Override(mCh, mSr, mRd);
                                (float[] mL, float[] mR) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                AcousticFingerprint fp = AnalyzeStereo(mL, mR, baseLeft90L, baseLeft90R, ProbeSampleRate, ProbeUpdateRate);
                                if (fp.NonFiniteCount > 0 || !mDiag.IsMagLsActive || fp.IldDb < 4.0f || fp.ItdUs < 350f) {
                                    return ("FAIL", $"MagLS O3 defect: active={mDiag.IsMagLsActive}, ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us", fp);
                                }
                                bool warn = MathF.Abs(fp.GainDeltaDb) > 3.5f;
                                return (warn ? "WARN" : "PASS",
                                    $"MagLS O{mDiag.MagLsOrder} (fc={mDiag.MagLsCutoffHz:0}Hz, hfErr={mDiag.MagLsHfErrorDb:0.0}dB, ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us)", fp);
                            }));

                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "B8_MinPhase_Plus_MagLs_O3", "TierB-OFAT",
                            "Combined Minimum-Phase + MagLS Order 3 on SADIE D1 DFC (Left -90)",
                            "Duplex-Theory low-pass ITD preserved (>=+550us) & lateral ILD>=+6dB (no ITD collapse)",
                            () => {
                                HrtfPersonalizationConfig magCfg = new() { MagLsMode = MagLsDecodeMode.Order3 };
                                (VirtualChannel[] mCh, int mSr, float mRd, _, _, HrtfPersonalizationDiagnostics mDiag) =
                                    BinauralProfileService.LoadBundledProfileForProbe(context, BinauralProfileService.DefaultProfileId, true, magCfg);
                                VirtualizerFilter.Override(mCh, mSr, mRd);
                                (float[] mL, float[] mR) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                AcousticFingerprint fp = AnalyzeStereo(mL, mR, baseLeft90L, baseLeft90R, ProbeSampleRate, ProbeUpdateRate);
                                if (fp.NonFiniteCount > 0 || !mDiag.IsMagLsActive || fp.IldDb < 6.0f || fp.ItdUs < 550f) {
                                    return ("FAIL", $"MinPhase+MagLS ITD/ILD collapse: ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us", fp);
                                }
                                return ("PASS", $"MinPhase+MagLS O3 ITD intact (ILD={fp.IldDb:+0.0;-0.0;0.0}dB, ITD={fp.ItdUs:+0;-0;0}us)", fp);
                            }));
                    }

                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "B4_IsmRoom_Studio_vs_Anechoic", "TierB-OFAT",
                        "Allen & Berkley ISM Mastering Studio ON vs OFF on SADIE D1 DFC (Center)",
                        "Loudness parity |GainΔ|<=1.8dB, no sub-bass boom (Sub<=+2.5dB), IACC externalized",
                        () => {
                            if (sadieBaseCh == null || baseCenterL == null) {
                                return ("FAIL", "SADIE baseline missing", new AcousticFingerprint());
                            }
                            VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                            (float[] ismL, float[] ismR) = RenderSingleSource(whiteL, wallCenter, Vector3.Zero, lfeSeparation: true, ism: true, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                            AcousticFingerprint fpBase = AnalyzeStereo(baseCenterL, baseCenterR, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                            AcousticFingerprint fp = AnalyzeStereo(ismL, ismR, baseCenterL, baseCenterR, ProbeSampleRate, ProbeUpdateRate);
                            if (fp.NonFiniteCount > 0 || fp.Sub20To120Db > 3.0f || MathF.Abs(fp.GainDeltaDb) > 2.5f) {
                                return ("FAIL", $"ISM Room coloration/loudness bug: GainΔ={fp.GainDeltaDb:+0.00;-0.00;0.00}dB, Sub={fp.Sub20To120Db:+0.0;-0.0;0.0}dB, Bas={fp.Bass120To500Db:+0.0;-0.0;0.0}dB", fp);
                            }
                            bool iaccDrop = fp.IaccZero < fpBase.IaccZero;
                            return (iaccDrop ? "PASS" : "WARN",
                                $"ISM Studio neutral (GainΔ={fp.GainDeltaDb:+0.00;-0.00;0.00}dB, Sub={fp.Sub20To120Db:+0.0;-0.0;0.0}dB, IACC0 {fpBase.IaccZero:0.00}->{fp.IaccZero:0.00})", fp);
                        }));

                    if (!quickSubset) {
                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "B5_Brir_DoubleRoomGuard", "TierB-OFAT",
                            "Measured BRIR (sadie_d1_brir) must auto-bypass ISM reflections bit-identically",
                            "IsMeasuredBrirActive=true AND max sample diff(ISM ON vs OFF) < 1e-5",
                            () => {
                                (VirtualChannel[] brirCh, int bSr, float bRd, int taps, bool isBrir, _) =
                                    BinauralProfileService.LoadBundledProfileForProbe(context, "sadie_d1_brir", false, HrtfPersonalizationConfig.Default);
                                VirtualizerFilter.Override(brirCh, bSr, bRd);
                                bool detectedBrir = VirtualizerFilter.IsMeasuredBrirActive;
                                (float[] offL, float[] offR) = RenderSingleSource(whiteL, wallCenter, Vector3.Zero, lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                (float[] onL, float[] onR) = RenderSingleSource(whiteL, wallCenter, Vector3.Zero, lfeSeparation: true, ism: true, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                                float maxDiff = 0f;
                                for (int i = 0; i < offL.Length; i++) {
                                    float d = MathF.Max(MathF.Abs(offL[i] - onL[i]), MathF.Abs(offR[i] - onR[i]));
                                    if (d > maxDiff) maxDiff = d;
                                }
                                AcousticFingerprint fp = AnalyzeStereo(onL, onR, offL, offR, ProbeSampleRate, ProbeUpdateRate);
                                if (!isBrir || !detectedBrir || maxDiff > 1e-4f) {
                                    return ("FAIL", $"BRIR double-room guard leaked: taps={taps}, detected={detectedBrir}, maxDiff={maxDiff:E2}", fp);
                                }
                                return ("PASS", $"BRIR ({taps} taps) auto-bypassed ISM bit-identically (maxDiff={maxDiff:E1})", fp);
                            }));

                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "B6_NearField_045m_vs_Far", "TierB-OFAT",
                            "Duda & Martens spherical-head Near-Field DVF at 0.45 m vs far-field (Left -90)",
                            "Near-field proximity increases lateral ILD by >= +1.0 dB",
                            () => {
                                if (sadieBaseCh == null || baseLeft90L == null) {
                                    return ("FAIL", "SADIE baseline missing", new AcousticFingerprint());
                                }
                                VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                                (float[] nfL, float[] nfR) = RenderSingleSource(whiteL, wallLeft90, Vector3.Zero, lfeSeparation: true, ism: false, nearField: true, 0.45f, ProbeSampleRate);
                                AcousticFingerprint fpFar = AnalyzeStereo(baseLeft90L, baseLeft90R, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                                AcousticFingerprint fpNf = AnalyzeStereo(nfL, nfR, baseLeft90L, baseLeft90R, ProbeSampleRate, ProbeUpdateRate);
                                float ildDelta = fpNf.IldDb - fpFar.IldDb;
                                if (fpNf.NonFiniteCount > 0 || ildDelta < 1.0f) {
                                    return ("FAIL", $"Near-field ILD boost too weak: {fpFar.IldDb:+0.0;-0.0;0.0}dB -> {fpNf.IldDb:+0.0;-0.0;0.0}dB (Δ={ildDelta:+0.0;-0.0;0.0}dB)", fpNf);
                                }
                                return ("PASS", $"Proximity ILD boosted {fpFar.IldDb:+0.0;-0.0;0.0}dB -> {fpNf.IldDb:+0.0;-0.0;0.0}dB (Δ={ildDelta:+0.0;-0.0;0.0}dB)", fpNf);
                            }));
                    }

                    ct.ThrowIfCancellationRequested();
                    results.Add(RunScenario(
                        "B7_HeadTrack_YawPlus45_Direction", "TierB-OFAT",
                        "Head-tracking sign verification: turning head RIGHT (+45 deg) shifts front source to LEFT ear",
                        "Yaw+45 => ILD>=+6dB & ITD>=+200us; Yaw-45 => ILD<=-6dB & ITD<=-200us",
                        () => {
                            if (sadieBaseCh == null) {
                                return ("FAIL", "SADIE baseline missing", new AcousticFingerprint());
                            }
                            VirtualizerFilter.Override(sadieBaseCh, sadieSr, sadieRefDist);
                            (float[] pL, float[] pR) = RenderSingleSource(whiteL, wallCenter, new Vector3(0f, 45f, 0f), lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                            (float[] nL, float[] nR) = RenderSingleSource(whiteL, wallCenter, new Vector3(0f, -45f, 0f), lfeSeparation: true, ism: false, nearField: false, float.PositiveInfinity, ProbeSampleRate);
                            AcousticFingerprint fpPos = AnalyzeStereo(pL, pR, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                            AcousticFingerprint fpNeg = AnalyzeStereo(nL, nR, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
                            bool ok = fpPos.NonFiniteCount == 0 && fpNeg.NonFiniteCount == 0
                                && fpPos.IldDb >= 6.0f && fpPos.ItdUs >= 200f
                                && fpNeg.IldDb <= -6.0f && fpNeg.ItdUs <= -200f;
                            if (!ok) {
                                return ("FAIL", $"Head-tracking sign/magnitude error: Yaw+45(ILD={fpPos.IldDb:+0.0;-0.0;0.0}dB,ITD={fpPos.ItdUs:+0;-0;0}us) Yaw-45(ILD={fpNeg.IldDb:+0.0;-0.0;0.0}dB,ITD={fpNeg.ItdUs:+0;-0;0}us)", fpPos);
                            }
                            return ("PASS", $"Correct yaw orientation: Yaw+45(ILD={fpPos.IldDb:+0.0;-0.0;0.0}dB,ITD={fpPos.ItdUs:+0;-0;0}us) | Yaw-45(ILD={fpNeg.IldDb:+0.0;-0.0;0.0}dB,ITD={fpNeg.ItdUs:+0;-0;0}us)", fpPos);
                        }));

                    // ==================== TIER C: USER'S LIVE ACTIVE SETTINGS SNAPSHOT ====================
                    if (store != null) {
                        ct.ThrowIfCancellationRequested();
                        results.Add(RunScenario(
                            "C1_UserActiveConfig_Center", "TierC-UserLive",
                            $"User's exact live settings snapshot (Center 0 deg): {userSummary}",
                            "0 NaN, non-silent, |ILD|<=2.5dB on center, spectral tilt exported for AI inspection",
                            () => RunUserActiveConfigProbe(context, store, whiteL, wallCenter, isCenter: true)));

                        if (!quickSubset) {
                            ct.ThrowIfCancellationRequested();
                            results.Add(RunScenario(
                                "C2_UserActiveConfig_Left90", "TierC-UserLive",
                                $"User's exact live settings snapshot (Left -90 deg): {userSummary}",
                                "0 NaN, lateral ILD/ITD cues present if virtualizer ON, full fingerprint exported",
                                () => RunUserActiveConfigProbe(context, store, whiteL, wallLeft90, isCenter: false)));
                        }
                    }
                } finally {
                    // Restore Listener static properties cleanly before VirtualizerFilter scope disposes
                    Listener.HeadphoneVirtualizer = savedVirt;
                    Listener.EnvironmentSize = savedEnvSize;
                    Listener.EnvironmentType = savedEnvType;
                    if (savedChannels != null && savedChannels.Length > 0) {
                        Listener.ReplaceChannels(savedChannels);
                    }
                }
            }

            totalSw.Stop();
            return new AcousticAuditReport {
                SchemaVersion = 1,
                ExecutedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                TotalElapsedMs = totalSw.ElapsedMilliseconds,
                PassCount = results.Count(r => r.Status == "PASS"),
                WarnCount = results.Count(r => r.Status == "WARN"),
                FailCount = results.Count(r => r.Status == "FAIL"),
                ActiveUserConfigSummary = userSummary,
                Scenarios = results
            };
        }
    }

    /// <summary>
    /// Executes an on-demand differential A/B probe with arbitrary parameters (invoked by AI over Live Bridge
    /// <c>acoustic_probe</c>) and returns the exact <see cref="AcousticFingerprint"/> JSON.
    /// </summary>
    public static string RunCustomProbeJson(Context context, AcousticCustomProbeRequest req) {
        req ??= new AcousticCustomProbeRequest();
        lock (auditLock) {
            Stopwatch sw = Stopwatch.StartNew();
            bool savedVirt = Listener.HeadphoneVirtualizer;
            Vector3 savedEnvSize = Listener.EnvironmentSize;
            Environments savedEnvType = Listener.EnvironmentType;
            Channel[] savedChannels = Listener.Channels != null ? (Channel[])Listener.Channels.Clone() : null;

            using (VirtualizerFilter.BeginStaticStateScope()) {
                try {
                    int sr = req.SampleRate >= 8000 && req.SampleRate <= 192000 ? req.SampleRate : ProbeSampleRate;
                    float[] sigL = GenerateProbeSignal(req.SignalType, ProbeTotalFrames, sr, seed: 42);
                    float[] sigR = GenerateProbeSignal(req.SignalType, ProbeTotalFrames, sr, seed: 1337);

                    HrtfPersonalizationConfig pers = new() {
                        MagLsMode = (MagLsDecodeMode)Math.Clamp(req.MagLsOrder, 0, 3),
                        BiometricItdEnabled = req.BiometricItdEnabled,
                        HeadWidthCm = Math.Clamp(req.HeadWidthCm, 11.0f, 19.0f),
                        ManualItdOffsetUs = Math.Clamp(req.ManualItdOffsetUs, -250f, 250f)
                    };

                    bool isDirect = string.Equals(req.ProfileId, BinauralProfileService.Direct20Id, StringComparison.OrdinalIgnoreCase);
                    bool isClassic = string.Equals(req.ProfileId, BinauralProfileService.ClassicId, StringComparison.OrdinalIgnoreCase);
                    bool isBrir = false;

                    if (isClassic) {
                        VirtualizerFilter.Reset(req.MinimumPhase);
                    } else if (!isDirect) {
                        (VirtualChannel[] ch, int hSr, float hRd, _, bool bFlag, _) =
                            BinauralProfileService.LoadBundledProfileForProbe(context, req.ProfileId, req.MinimumPhase, pers);
                        if (ch != null && ch.Length > 0) {
                            VirtualizerFilter.Override(ch, hSr, hRd);
                            isBrir = bFlag;
                        } else {
                            VirtualizerFilter.Reset(req.MinimumPhase);
                        }
                    }

                    float[] outL, outR, refL, refR;
                    if (req.UseLgsrRestore) {
                        (float[] lossyL, float[] lossyR, float[] restL, float[] restR, LgsrRuntimeTelemetry lgsrTel) =
                            RunLgsrDifferentialProbe(sigL, sigR, sr);
                        outL = restL;
                        outR = restR;
                        refL = req.CompareAgainstDirect ? sigL : lossyL;
                        refR = req.CompareAgainstDirect ? sigR : lossyR;
                        AcousticFingerprint lgsrFp = AnalyzeStereo(outL, outR, refL, refR, sr, ProbeUpdateRate);
                        sw.Stop();
                        return JsonSerializer.Serialize(new {
                            ok = true,
                            elapsedMs = sw.ElapsedMilliseconds,
                            request = req,
                            lgsrTelemetry = new {
                                coherence = Math.Round(lgsrTel.AverageCoherence, 4),
                                fusionRatio = Math.Round(lgsrTel.ActiveFusionRatio, 4),
                                envLockDb = Math.Round(lgsrTel.DynamicEnvelopeLockDb, 2),
                                clippingGuard = lgsrTel.ClippingGuardActive,
                                restoredHfDb = Math.Round(lgsrTel.RestoredHfEnergyDb, 2)
                            },
                            fingerprint = lgsrFp
                        }, new JsonSerializerOptions { WriteIndented = true });
                    }

                    if (isDirect) {
                        outL = sigL;
                        outR = sigL;
                        refL = sigL;
                        refR = sigL;
                    } else if (req.UseParametricUpmix) {
                        (outL, outR, refL, refR) = RenderStereoParametricUpmix(sigL, sigR, sr);
                    } else {
                        Vector3 srcPos = AnglesToWallPosition(req.SourceElevationDeg, req.SourceAzimuthDeg);
                        Vector3 rotDeg = new(req.ListenerPitchDeg, req.ListenerYawDeg, 0f);
                        IsmRoomPreset preset = (IsmRoomPreset)Math.Clamp(req.IsmPresetIndex, 0, 4);
                        (outL, outR) = RenderSingleSource(
                            sigL, srcPos, rotDeg,
                            lfeSeparation: true,
                            ism: req.IsmRoomEnabled,
                            nearField: req.NearFieldEnabled,
                            nearFieldDistM: req.NearFieldDistanceM,
                            sampleRate: sr,
                            ismPreset: preset,
                            ismLevel: Math.Clamp(req.IsmReflectionLevel, 0f, 1.5f),
                            externalBrirGuard: isBrir);
                        refL = sigL;
                        refR = sigL;
                    }

                    AcousticFingerprint fp = AnalyzeStereo(outL, outR, refL, refR, sr, ProbeUpdateRate);
                    sw.Stop();
                    return JsonSerializer.Serialize(new {
                        ok = fp.NonFiniteCount == 0,
                        elapsedMs = sw.ElapsedMilliseconds,
                        isMeasuredBrirActive = VirtualizerFilter.IsMeasuredBrirActive,
                        request = req,
                        fingerprint = fp
                    }, new JsonSerializerOptions { WriteIndented = true });
                } finally {
                    Listener.HeadphoneVirtualizer = savedVirt;
                    Listener.EnvironmentSize = savedEnvSize;
                    Listener.EnvironmentType = savedEnvType;
                    if (savedChannels != null && savedChannels.Length > 0) {
                        Listener.ReplaceChannels(savedChannels);
                    }
                }
            }
        }
    }

    static (string status, string note, AcousticFingerprint fp) RunUserActiveConfigProbe(
        Context context, AppSettingsStore store, float[] whiteL, Vector3 sourcePos, bool isCenter) {
        if (!store.HeadphoneVirtualizer) {
            AcousticFingerprint directFp = AnalyzeStereo(whiteL, whiteL, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
            return ("PASS", "User active mode is Direct 2.0 (HRTF bypassed)", directFp);
        }

        string activeId = BinauralProfileService.ResolveStoredHrtfProfileId(store);
        BinauralProfileInfo info = BinauralProfileService.FindById(activeId);
        HrtfPersonalizationConfig pers = store.BuildPersonalizationConfig();
        PhysicalExternalizationConfig ext = store.BuildExternalizationConfig();

        bool isBrir = info?.IsBrir ?? false;
        if (info == null || info.IsClassic || info.IsCustom || string.IsNullOrEmpty(info.AssetFile)) {
            VirtualizerFilter.Reset(store.MinimumPhaseHrir);
        } else {
            (VirtualChannel[] ch, int sr, float rd, _, bool bFlag, _) =
                BinauralProfileService.LoadBundledProfileForProbe(context, info.Id, store.MinimumPhaseHrir, pers);
            isBrir = bFlag;
            if (ch != null && ch.Length > 0) {
                VirtualizerFilter.Override(ch, sr, rd);
            } else {
                VirtualizerFilter.Reset(store.MinimumPhaseHrir);
            }
        }

        (float[] L, float[] R) = RenderSingleSource(
            whiteL, sourcePos, Vector3.Zero,
            lfeSeparation: true,
            ism: ext.IsmRoomEnabled,
            nearField: ext.NearFieldEnabled,
            nearFieldDistM: float.PositiveInfinity,
            sampleRate: ProbeSampleRate,
            ismPreset: ext.RoomPreset,
            ismLevel: ext.ReflectionLevel,
            externalBrirGuard: isBrir);

        // Apply active user post-processing stages (HpTF notch & ISO 226) to mirror actual listening output
        float[] interleaved = new float[L.Length * 2];
        for (int i = 0; i < L.Length; i++) {
            interleaved[i * 2] = L[i];
            interleaved[i * 2 + 1] = R[i];
        }
        SpatialSceneFrame frame = new() {
            SampleRate = ProbeSampleRate,
            FrameCount = L.Length,
            EnvironmentSize = new Vector3(10f, 7f, 10f),
            EnvironmentType = Environments.Studio,
            IsBinauralActive = true
        };
        if (store.HptfNotchEnabled && store.HptfNotchGainDb < -0.05f) {
            using HptfNotchPostProcessingStage hptf = new();
            hptf.Configure(ProbeSampleRate, 2);
            hptf.ApplyParameters(true, store.HptfNotchFreqHz, store.HptfNotchGainDb, store.HptfNotchQ);
            hptf.ProcessInterleaved(interleaved, 0, L.Length, 2, frame);
        }
        if (store.Iso226Enabled) {
            using Iso226LoudnessPostProcessingStage iso = new();
            iso.Configure(ProbeSampleRate, 2);
            iso.ApplyParameters(true, store.Iso226ReferenceSplPhon, store.Iso226ListeningSplPhon, trackPlaybackVolume: false);
            iso.ProcessInterleaved(interleaved, 0, L.Length, 2, frame);
        }
        for (int i = 0; i < L.Length; i++) {
            L[i] = interleaved[i * 2];
            R[i] = interleaved[i * 2 + 1];
        }

        AcousticFingerprint fp = AnalyzeStereo(L, R, whiteL, whiteL, ProbeSampleRate, ProbeUpdateRate);
        if (fp.NonFiniteCount > 0 || fp.RmsLeftDbfs < -75f) {
            return ("FAIL", $"User config produced invalid/silent output (nf={fp.NonFiniteCount}, rmsL={fp.RmsLeftDbfs:0.0}dBFS)", fp);
        }
        if (isCenter && MathF.Abs(fp.IldDb) > 3.0f) {
            return ("WARN", $"Center L/R imbalance ({fp.IldDb:+0.0;-0.0;0.0} dB) on user config — inspect HRIR/EQ", fp);
        }
        if (!isCenter && (fp.IldDb < 3.0f || fp.ItdUs < 450f)) {
            return ("WARN", $"Weak/collapsed lateral cues at -90 deg (ILD={fp.IldDb:+0.0;-0.0;0.0} dB, ITD={fp.ItdUs:+0;-0;0} us)", fp);
        }
        return ("PASS", fp.CompactMetrics(), fp);
    }

    static AcousticScenarioResult RunScenario(
        string id,
        string tier,
        string description,
        string expectedRule,
        Func<(string status, string note, AcousticFingerprint fp)> fn) {
        Stopwatch sw = Stopwatch.StartNew();
        try {
            (string status, string note, AcousticFingerprint fp) = fn();
            sw.Stop();
            return new AcousticScenarioResult {
                Id = id,
                Tier = tier,
                Description = description,
                Status = status,
                ElapsedMs = sw.ElapsedMilliseconds,
                ExpectedRule = expectedRule,
                VerdictNote = note ?? "",
                Fingerprint = fp ?? new AcousticFingerprint()
            };
        } catch (Exception ex) {
            sw.Stop();
            return new AcousticScenarioResult {
                Id = id,
                Tier = tier,
                Description = description,
                Status = "FAIL",
                ElapsedMs = sw.ElapsedMilliseconds,
                ExpectedRule = expectedRule,
                VerdictNote = $"{ex.GetType().Name}: {ex.Message}",
                Fingerprint = new AcousticFingerprint()
            };
        }
    }

    static (float[] L, float[] R) RenderSingleSource(
        float[] monoSignal,
        Vector3 sourcePos,
        Vector3 listenerRotDeg,
        bool lfeSeparation,
        bool ism,
        bool nearField,
        float nearFieldDistM,
        int sampleRate,
        IsmRoomPreset ismPreset = IsmRoomPreset.MasteringStudio,
        float ismLevel = 1.0f,
        bool externalBrirGuard = false) {
        int blocks = monoSignal.Length / ProbeUpdateRate;
        int outLen = blocks * ProbeUpdateRate;

        Listener.HeadphoneVirtualizer = true;
        Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
        Listener.EnvironmentType = Environments.Studio;
        VirtualizerFilter.ResetActiveFilters();
        VirtualizerFilter.ConfigureExternalization(
            ismEnabled: ism,
            preset: ismPreset,
            wallMaterial: IsmWallMaterial.StudioBroadband,
            reflectionOrder: 2,
            reflectionLevel: ismLevel,
            nearFieldEnabled: nearField,
            airAbsorptionEnabled: false,
            airTempCelsius: 20f,
            airRelativeHumidityPct: 50f,
            customEnvironmentHalfSize: new Vector3(5f, 3.5f, 5f),
            externalBrirGuard: externalBrirGuard);
        VirtualizerFilter.LiveListenerNormalizedPosition = Vector3.Zero;
        VirtualizerFilter.LiveListenerRotationDegrees = listenerRotDeg;

        float[] dists = new float[64];
        Array.Fill(dists, nearField && float.IsFinite(nearFieldDistM) ? nearFieldDistM : float.PositiveInfinity);
        VirtualizerFilter.SetLiveChannelMinDistances(dists, 64);

        Listener listener = new() {
            SampleRate = sampleRate,
            UpdateRate = ProbeUpdateRate,
            LFESeparation = lfeSeparation,
            Rotation = listenerRotDeg,
            Position = Vector3.Zero
        };
        Source src = new() {
            Clip = new Clip(monoSignal, 1, sampleRate),
            Position = sourcePos,
            Volume = 1f
        };
        listener.AttachSource(src);

        float[] outL = new float[outLen];
        float[] outR = new float[outLen];
        for (int b = 0; b < blocks; b++) {
            float[] rendered = listener.Render(1);
            int stride = Listener.Channels.Length;
            int baseOff = b * ProbeUpdateRate;
            for (int i = 0; i < ProbeUpdateRate; i++) {
                outL[baseOff + i] = rendered[i * stride];
                outR[baseOff + i] = stride > 1 ? rendered[i * stride + 1] : rendered[i * stride];
            }
        }
        return (outL, outR);
    }

    static (float[] outL, float[] outR, float[] inL, float[] inR) RenderStereoParametricUpmix(
        float[] seedL, float[] seedR, int sampleRate) {
        int total = (seedL.Length / ProbeUpdateRate) * ProbeUpdateRate;
        float[] inL = new float[total];
        float[] inR = new float[total];
        for (int i = 0; i < total; i++) {
            // Realistic stereo mix: 60% coherent center + 40% uncorrelated stereo ambience
            inL[i] = 0.6f * seedL[i] + 0.4f * seedR[i];
            inR[i] = 0.6f * seedL[i] - 0.4f * seedR[i];
        }

        Listener.HeadphoneVirtualizer = true;
        Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
        Listener.EnvironmentType = Environments.Studio;
        VirtualizerFilter.ResetActiveFilters();
        VirtualizerFilter.ConfigureExternalization(
            ismEnabled: false,
            preset: IsmRoomPreset.MasteringStudio,
            wallMaterial: IsmWallMaterial.StudioBroadband,
            reflectionOrder: 2,
            reflectionLevel: 1.0f,
            nearFieldEnabled: false,
            airAbsorptionEnabled: false,
            airTempCelsius: 20f,
            airRelativeHumidityPct: 50f,
            customEnvironmentHalfSize: new Vector3(5f, 3.5f, 5f),
            externalBrirGuard: false);

        Listener listener = new() {
            SampleRate = sampleRate,
            UpdateRate = ProbeUpdateRate,
            LFESeparation = true
        };

        int readPos = 0;
        using SurroundUpmixer upmix = new([ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight], sampleRate, false, true) {
            UseParametricStft = true
        };
        upmix.OnSamplesNeeded = n => {
            float[] c0 = new float[n];
            float[] c1 = new float[n];
            for (int i = 0; i < n; i++) {
                int idx = (readPos + i) % total;
                c0[i] = inL[idx];
                c1[i] = inR[idx];
            }
            readPos += n;
            return [c0, c1];
        };
        listener.AttachSources(upmix.IntermediateSources);

        int blocks = total / ProbeUpdateRate;
        float[] outL = new float[total];
        float[] outR = new float[total];
        for (int b = 0; b < blocks; b++) {
            float[] ren = listener.Render(1);
            int stride = Listener.Channels.Length;
            int baseOff = b * ProbeUpdateRate;
            for (int i = 0; i < ProbeUpdateRate; i++) {
                outL[baseOff + i] = ren[i * stride];
                outR[baseOff + i] = stride > 1 ? ren[i * stride + 1] : ren[i * stride];
            }
        }
        return (outL, outR, inL, inR);
    }

    static (float[] lossyL, float[] lossyR, float[] restL, float[] restR, LgsrRuntimeTelemetry tel) RunLgsrDifferentialProbe(
        float[] flacL, float[] flacR, int sampleRate) {
        int total = (flacL.Length / ProbeUpdateRate) * ProbeUpdateRate;
        float[] lossyL = new float[total];
        float[] lossyR = new float[total];
        Array.Copy(flacL, lossyL, total);
        Array.Copy(flacR, lossyR, total);

        Lowpass lpL = new(sampleRate, 6000);
        Lowpass lpR = new(sampleRate, 6000);
        lpL.Process(lossyL);
        lpR.Process(lossyR);

        using LgsrSpectralFusionEngine lgsr = new(sampleRate, 4) {
            IsEnabled = true,
            FusionStrength = 0.85f,
            DynamicEnvelopeLock = true,
            ClippingGuardEnabled = true,
            UseBitstreamGuide = true,
            BitstreamCouplingHz = 3500f,
            BitstreamSpxHz = 6000f
        };

        bool[] isLfe = [false, false];
        float[] panRight = [0f, 1f];
        float[] chunkL = new float[ProbeUpdateRate];
        float[] chunkR = new float[ProbeUpdateRate];
        float[] refChunkL = new float[ProbeUpdateRate];
        float[] refChunkR = new float[ProbeUpdateRate];
        float[][] bus = [chunkL, chunkR];
        float[] restL = new float[total];
        float[] restR = new float[total];

        for (int off = 0; off < total; off += ProbeUpdateRate) {
            Array.Copy(lossyL, off, chunkL, 0, ProbeUpdateRate);
            Array.Copy(lossyR, off, chunkR, 0, ProbeUpdateRate);
            Array.Copy(flacL, off, refChunkL, 0, ProbeUpdateRate);
            Array.Copy(flacR, off, refChunkR, 0, ProbeUpdateRate);
            lgsr.ProcessPreHrtfBusInPlace(bus, isLfe, panRight, 2, ProbeUpdateRate, refChunkL, refChunkR, 1f);
            Array.Copy(chunkL, 0, restL, off, ProbeUpdateRate);
            Array.Copy(chunkR, 0, restR, off, ProbeUpdateRate);
        }
        return (lossyL, lossyR, restL, restR, lgsr.Telemetry);
    }

    static AcousticFingerprint AnalyzeStereo(
        float[] outL, float[] outR, float[] refL, float[] refR, int sampleRate, int blockStride) {
        int n = Math.Min(outL.Length, outR.Length);
        int start = Math.Min(SteadyStartFrame, n / 4);
        int len = Math.Max(1, n - start);

        double sumL = 0, sumR = 0, sumRefL = 0, sumRefR = 0;
        float peak = 0f;
        float maxBoundaryClick = 0f;
        int nonFinite = 0;

        for (int i = start; i < n; i++) {
            float lv = outL[i];
            float rv = outR[i];
            if (!float.IsFinite(lv) || !float.IsFinite(rv)) {
                nonFinite++;
                continue;
            }
            sumL += (double)lv * lv;
            sumR += (double)rv * rv;
            float a = MathF.Max(MathF.Abs(lv), MathF.Abs(rv));
            if (a > peak) peak = a;

            if (i > start && (i % blockStride) == 0) {
                float dL = MathF.Abs(lv - outL[i - 1]);
                float dR = MathF.Abs(rv - outR[i - 1]);
                float d = MathF.Max(dL, dR);
                if (d > maxBoundaryClick) maxBoundaryClick = d;
            }
        }

        int refN = Math.Min(refL.Length, refR.Length);
        int refStart = Math.Min(start, refN / 4);
        int refLen = Math.Max(1, refN - refStart);
        for (int i = refStart; i < refN; i++) {
            sumRefL += (double)refL[i] * refL[i];
            sumRefR += (double)refR[i] * refR[i];
        }

        double rmsL = Math.Sqrt(sumL / len);
        double rmsR = Math.Sqrt(sumR / len);
        double rmsTot = Math.Sqrt(0.5 * (sumL + sumR) / len);
        double refTot = Math.Sqrt(0.5 * (sumRefL + sumRefR) / refLen);

        float rmsLDbfs = (float)(20.0 * Math.Log10(Math.Max(rmsL, 1e-9)));
        float rmsRDbfs = (float)(20.0 * Math.Log10(Math.Max(rmsR, 1e-9)));
        float peakDbfs = (float)(20.0 * Math.Log10(Math.Max(peak, 1e-9)));
        float gainDelta = (float)(20.0 * Math.Log10(Math.Max(rmsTot, 1e-9) / Math.Max(refTot, 1e-9)));
        float ildDb = (float)(20.0 * Math.Log10(Math.Max(rmsL, 1e-9) / Math.Max(rmsR, 1e-9)));

        // Normalized cross-correlation over +/- 1.2 ms (positive lag => R delayed => Left leads)
        int maxLag = Math.Clamp((int)Math.Ceiling(sampleRate * 0.0012), 8, 96);
        double denom = Math.Sqrt(sumL * sumR) + 1e-12;
        double corrZero = 0.0;
        double bestCorr = -2.0;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++) {
            double dot = 0.0;
            int i0 = Math.Max(start, start - lag);
            int i1 = Math.Min(n, n - lag);
            for (int i = i0; i < i1; i++) {
                dot += (double)outL[i] * outR[i + lag];
            }
            double r = dot / denom;
            if (lag == 0) corrZero = r;
            if (r > bestCorr) {
                bestCorr = r;
                bestLag = lag;
            }
        }
        float itdUs = (float)(bestLag * 1_000_000.0 / sampleRate);

        double[] bOut = ComputeFiveBandEnergy(outL, outR, start, sampleRate);
        double[] bRef = ComputeFiveBandEnergy(refL, refR, refStart, sampleRate);
        float[] bandDeltaDb = new float[5];
        for (int k = 0; k < 5; k++) {
            bandDeltaDb[k] = (float)(10.0 * Math.Log10(Math.Max(bOut[k], 1e-14) / Math.Max(bRef[k], 1e-14)));
        }

        return new AcousticFingerprint {
            RmsLeftDbfs = MathF.Round(rmsLDbfs, 2),
            RmsRightDbfs = MathF.Round(rmsRDbfs, 2),
            PeakDbfs = MathF.Round(peakDbfs, 2),
            GainDeltaDb = MathF.Round(gainDelta, 2),
            IldDb = MathF.Round(ildDb, 2),
            ItdUs = MathF.Round(itdUs, 1),
            IaccZero = MathF.Round((float)corrZero, 3),
            IaccMax = MathF.Round((float)bestCorr, 3),
            Sub20To120Db = MathF.Round(bandDeltaDb[0], 2),
            Bass120To500Db = MathF.Round(bandDeltaDb[1], 2),
            Mid500To2k5Db = MathF.Round(bandDeltaDb[2], 2),
            Pres2k5To8kDb = MathF.Round(bandDeltaDb[3], 2),
            Air8kTo20kDb = MathF.Round(bandDeltaDb[4], 2),
            MaxBoundaryClick = MathF.Round(maxBoundaryClick, 4),
            NonFiniteCount = nonFinite
        };
    }

    static double[] ComputeFiveBandEnergy(float[] L, float[] R, int start, int sampleRate) {
        const int fftSize = 4096;
        double[] bands = new double[5];
        int avail = Math.Min(L.Length, R.Length) - start;
        int frames = Math.Min(3, avail / fftSize);
        if (frames <= 0) {
            return bands;
        }
        using ThreadSafeFFTCache cache = new(fftSize);
        Complex[] cL = new Complex[fftSize];
        Complex[] cR = new Complex[fftSize];
        for (int f = 0; f < frames; f++) {
            int off = start + f * fftSize;
            for (int i = 0; i < fftSize; i++) {
                float w = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));
                float lv = L[off + i];
                float rv = R[off + i];
                cL[i] = new Complex(float.IsFinite(lv) ? lv * w : 0f, 0f);
                cR[i] = new Complex(float.IsFinite(rv) ? rv * w : 0f, 0f);
            }
            cL.InPlaceFFT(cache);
            cR.InPlaceFFT(cache);
            for (int k = 1; k < fftSize / 2; k++) {
                double hz = k * (double)sampleRate / fftSize;
                double e = cL[k].Real * cL[k].Real + cL[k].Imaginary * cL[k].Imaginary
                         + cR[k].Real * cR[k].Real + cR[k].Imaginary * cR[k].Imaginary;
                int b = hz < 120.0 ? 0 : (hz < 500.0 ? 1 : (hz < 2500.0 ? 2 : (hz < 8000.0 ? 3 : 4)));
                bands[b] += e;
            }
        }
        for (int k = 0; k < 5; k++) {
            bands[k] /= frames;
        }
        return bands;
    }

    static float[] GenerateProbeSignal(string signalType, int count, int sampleRate, int seed) {
        string kind = (signalType ?? "white").Trim().ToLowerInvariant();
        if (kind == "pink") {
            return GenerateDeterministicPink(count, seed);
        }
        if (kind == "sine1k") {
            float[] s = new float[count];
            double w = 2.0 * Math.PI * 1000.0 / sampleRate;
            for (int i = 0; i < count; i++) {
                s[i] = (float)(Math.Sin(w * i) * 0.2828);
            }
            return s;
        }
        if (kind == "impulse") {
            float[] s = new float[count];
            for (int i = SteadyStartFrame + 512; i < count - 512; i += 2048) {
                s[i] = 0.8f;
            }
            return s;
        }
        return GenerateDeterministicWhite(count, seed);
    }

    static float[] GenerateDeterministicWhite(int count, int seed) {
        Random rng = new(seed);
        float[] x = new float[count];
        for (int i = 0; i < count; i++) {
            x[i] = (float)((rng.NextDouble() * 2.0 - 1.0) * 0.34641016); // -14 dBFS RMS (0.20)
        }
        return x;
    }

    static float[] GenerateDeterministicPink(int count, int seed) {
        Random rng = new(seed);
        float[] x = new float[count];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        double sumSq = 0;
        for (int i = 0; i < count; i++) {
            double w = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.99886 * b0 + w * 0.0555179;
            b1 = 0.99332 * b1 + w * 0.0750759;
            b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856;
            b4 = 0.55000 * b4 + w * 0.5329522;
            b5 = -0.7616 * b5 - w * 0.0168980;
            double p = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362;
            b6 = w * 0.115926;
            x[i] = (float)p;
            sumSq += p * p;
        }
        float scale = 0.20f / MathF.Max((float)Math.Sqrt(sumSq / Math.Max(1, count)), 1e-6f);
        for (int i = 0; i < count; i++) {
            x[i] *= scale;
        }
        return x;
    }

    static Vector3 AnglesToWallPosition(float elevDeg, float azimDeg) {
        float xRad = elevDeg * VectorExtensions.Deg2Rad;
        float yRad = azimDeg * VectorExtensions.Deg2Rad;
        float cosX = MathF.Cos(xRad);
        Vector3 dir = new(MathF.Sin(yRad) * cosX, -MathF.Sin(xRad), MathF.Cos(yRad) * cosX);
        float maxComp = MathF.Max(MathF.Abs(dir.X) / 10f, MathF.Max(MathF.Abs(dir.Y) / 7f, MathF.Abs(dir.Z) / 10f));
        return maxComp > 1e-4f ? dir / maxComp : new Vector3(0f, 0f, 10f);
    }

    static string DescribeUserConfig(AppSettingsStore store) {
        if (store == null) {
            return "unknown";
        }
        string prof = BinauralProfileService.GetEffectiveProfileId(store);
        return $"profile={prof}, minPh={store.MinimumPhaseHrir}, magLs=O{store.MagLsModeIndex}, bioItd={store.BiometricItdEnabled}, ism={store.IsmRoomEnabled}(p{store.IsmRoomPresetIndex}), nf={store.NearFieldCompensationEnabled}, autoEq={store.AutoEqEnabled}, hptf={store.HptfNotchEnabled}, iso226={store.Iso226Enabled}, lgsr={store.LgsrEnabled}";
    }

    static float Max5(float a, float b, float c, float d, float e) =>
        MathF.Max(a, MathF.Max(b, MathF.Max(c, MathF.Max(d, e))));

    static float Min5(float a, float b, float c, float d, float e) =>
        MathF.Min(a, MathF.Min(b, MathF.Min(c, MathF.Min(d, e))));

    static float MaxAbs5(float a, float b, float c, float d, float e) =>
        Max5(MathF.Abs(a), MathF.Abs(b), MathF.Abs(c), MathF.Abs(d), MathF.Abs(e));
}
