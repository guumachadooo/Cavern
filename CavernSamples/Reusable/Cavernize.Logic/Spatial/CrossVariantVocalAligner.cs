using Cavern.Neural;
using Cavern.Restoration;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Result of B3 GCC-PHAT cross-variant vocal stem alignment between an actively playing track variant
/// (e.g., Dolby Atmos 7.1.4 or Stereo FLAC) and a cached <see cref="NeuralSeparatedTrack"/> extracted from
/// a sibling variant of the same song.
/// Mapping formula: <c>stemIndex = OffsetSamples + playingIndex * (1.0 + DriftPpm * 1e-6)</c>.
/// </summary>
public readonly struct CrossVariantAlignmentResult {
    /// <summary>Sub-sample intercept offset in playing-track sample rate.</summary>
    public double OffsetSamples { get; init; }

    /// <summary>Estimated linear clock drift in parts-per-million (ppm).</summary>
    public double DriftPpm { get; init; }

    /// <summary>GCC-PHAT alignment confidence in <c>[0; 1]</c>.</summary>
    public float Confidence { get; init; }

    /// <summary>True when <see cref="Confidence"/> passes the cross-variant alignment safety gate.</summary>
    public bool IsAligned { get; init; }

    /// <summary>
    /// True when alignment failed because the sibling variants appear to be incompatible masters/edits
    /// (e.g., radio edit vs extended Atmos mix) and separate per-variant vocal extraction should be preferred
    /// rather than applying mismatched stems at offset 0.
    /// </summary>
    public bool ShouldPreferSeparateExtraction { get; init; }

    /// <summary>Diagnostic summary of which stem probe achieved the alignment.</summary>
    public string ProbeSource { get; init; }

    /// <summary>Returns a failed/incompatible alignment result.</summary>
    public static CrossVariantAlignmentResult Failed(float confidence = 0f, string reason = "unaligned") => new() {
        OffsetSamples = 0.0,
        DriftPpm = 0.0,
        Confidence = confidence,
        IsAligned = false,
        ShouldPreferSeparateExtraction = true,
        ProbeSource = reason ?? "unaligned"
    };
}

/// <summary>
/// B3 Pure-C# GCC-PHAT cross-variant vocal stem aligner for separate-reference Atmos ↔ FLAC Spatial Karaoke.
/// Strictly NEVER sums FLAC and Atmos into a single waveform (<c>f(A+B) != f(A)+f(B)</c>); instead, computes
/// exact sub-sample offset + ppm clock drift between the playing variant's mono downmix and the cached sibling
/// stem bundle (preferring broadband stem-sum probe, with vocal-stem fallback) so <see cref="Adapters.NeuralStemStageProcessor"/>
/// phase-locks the cached vocal reference on the fly via zero-GC Catmull-Rom interpolation.
/// </summary>
public static class CrossVariantVocalAligner {
    /// <summary>Default minimum GCC-PHAT confidence required to trust cross-variant stem reuse.</summary>
    public const float DefaultMinConfidence = 0.28f;

    /// <summary>
    /// Aligns <paramref name="cachedTrack"/>'s stem timeline onto <paramref name="playingMono"/>'s timeline at
    /// <paramref name="playingSampleRate"/>. Returns <see cref="CrossVariantAlignmentResult.IsAligned"/> == false
    /// and <see cref="CrossVariantAlignmentResult.ShouldPreferSeparateExtraction"/> == true when the two variants
    /// are incompatible masters/edits.
    /// </summary>
    public static CrossVariantAlignmentResult Align(
        float[] playingMono,
        int playingSampleRate,
        NeuralSeparatedTrack cachedTrack,
        float maxSearchSeconds = 15f,
        float minConfidence = DefaultMinConfidence) {
        if (playingMono == null || playingMono.Length < 4096 || cachedTrack == null || playingSampleRate < 8000) {
            return CrossVariantAlignmentResult.Failed(0f, "invalid_input");
        }

        float[] fullStemSum = BuildStemProbeMono(cachedTrack, vocalOnly: false, playingSampleRate);
        float[] vocalProbe = BuildStemProbeMono(cachedTrack, vocalOnly: true, playingSampleRate);

        SubSampleSyncResult best = SubSampleSyncResult.Unsynced();
        string probeSource = "none";

        if (fullStemSum != null && fullStemSum.Length >= 4096) {
            SubSampleSyncResult fullRes = SubSampleSyncEngine.Align(
                playingMono,
                fullStemSum,
                playingSampleRate,
                maxMacroSearchSeconds: maxSearchSeconds,
                minConfidenceGate: minConfidence,
                windowCount: 12);
            if (fullRes.Confidence > best.Confidence) {
                best = fullRes;
                probeSource = "stem_sum";
            }
        }

        if ((!best.IsSynced || best.Confidence < 0.55f) && vocalProbe != null && vocalProbe.Length >= 4096) {
            SubSampleSyncResult vocRes = SubSampleSyncEngine.Align(
                playingMono,
                vocalProbe,
                playingSampleRate,
                maxMacroSearchSeconds: maxSearchSeconds,
                minConfidenceGate: minConfidence,
                windowCount: 12);
            if (vocRes.Confidence > best.Confidence) {
                best = vocRes;
                probeSource = "vocal_stem";
            }
        }

        if (!best.IsSynced || best.Confidence < minConfidence || !double.IsFinite(best.BaseOffsetSamples)) {
            return CrossVariantAlignmentResult.Failed(best.Confidence, $"{probeSource}_low_conf");
        }

        return new CrossVariantAlignmentResult {
            OffsetSamples = best.BaseOffsetSamples,
            DriftPpm = double.IsFinite(best.DriftPpm) ? Math.Clamp(best.DriftPpm, -500.0, 500.0) : 0.0,
            Confidence = best.Confidence,
            IsAligned = true,
            ShouldPreferSeparateExtraction = false,
            ProbeSource = probeSource
        };
    }

    /// <summary>
    /// Builds a mono probe signal from <paramref name="track"/> resampled to <paramref name="targetSampleRate"/>
    /// if needed (linear/Catmull-Rom), either summing non-redundant stems (broadband transient lock) or isolating vocals.
    /// </summary>
    public static float[] BuildStemProbeMono(NeuralSeparatedTrack track, bool vocalOnly, int targetSampleRate) {
        if (track == null || track.SampleRate < 8000 || targetSampleRate < 8000) {
            return null;
        }
        int srcSr = track.SampleRate;
        float[] srcMono = null;

        if (vocalOnly) {
            StereoStemBuffer voc = track.FindStem("vocals") ?? track.FindStem("lead_vocal");
            if (voc != null && voc.Length > 0) {
                srcMono = new float[voc.Length];
                for (int i = 0; i < voc.Length; i++) {
                    srcMono[i] = 0.5f * (voc.Left[i] + voc.Right[i]);
                }
            } else if (track.MonoLeadVocalRef != null && track.MonoLeadVocalRef.Length > 0) {
                srcMono = (float[])track.MonoLeadVocalRef.Clone();
            }
        } else if (track.Stems != null && track.Stems.Count > 0) {
            int maxLen = 0;
            bool hasCombinedVocals = track.FindStem("vocals") != null;
            for (int s = 0; s < track.Stems.Count; s++) {
                StereoStemBuffer st = track.Stems[s];
                if (st != null && st.Length > maxLen) {
                    maxLen = st.Length;
                }
            }
            if (maxLen > 0) {
                srcMono = new float[maxLen];
                for (int s = 0; s < track.Stems.Count; s++) {
                    StereoStemBuffer st = track.Stems[s];
                    if (st == null || st.Length <= 0) {
                        continue;
                    }
                    // Avoid double-counting vocals when both combined "vocals" and split "lead_vocal"/"backing_vocals" exist
                    if (hasCombinedVocals &&
                        (string.Equals(st.Name, "lead_vocal", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(st.Name, "backing_vocals", StringComparison.OrdinalIgnoreCase))) {
                        continue;
                    }
                    if (string.Equals(st.Name, "back_instrumental", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    int len = Math.Min(maxLen, st.Length);
                    for (int i = 0; i < len; i++) {
                        srcMono[i] += 0.5f * (st.Left[i] + st.Right[i]);
                    }
                }
            }
        }

        if (srcMono == null || srcMono.Length == 0) {
            return null;
        }
        if (srcSr == targetSampleRate) {
            return srcMono;
        }

        double ratio = srcSr / (double)targetSampleRate;
        int outLen = (int)Math.Floor(srcMono.Length / ratio);
        if (outLen <= 0) {
            return null;
        }
        float[] resampled = new float[outLen];
        for (int i = 0; i < outLen; i++) {
            resampled[i] = FarrowLagrangeDelay.SampleAt(srcMono, srcMono.Length, i * ratio);
        }
        return resampled;
    }
}
