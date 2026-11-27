using System;
using System.Collections.Generic;
using System.Numerics;

using Cavern.Remapping;
using Cavern.Spatial;

namespace Cavern.Neural {
    /// <summary>
    /// Canonical semantic classification for separated neural audio stems used to compute physical
    /// 3D stage placement and Hybrid Neural+DirAC upmix routing.
    /// </summary>
    public enum NeuralStemRole {
        /// <summary>Primary lead vocal stem (front-center anchor, dialogue-tagged, minimal decorrelation).</summary>
        LeadVocal = 0,

        /// <summary>Backing vocals / choir harmonies (wide elevated stereo arc).</summary>
        BackingVocals = 1,

        /// <summary>Combined vocals stem (front-center anchor).</summary>
        Vocals = 2,

        /// <summary>Percussion / drums stem (wide front stage with tight transient coherence).</summary>
        Drums = 3,

        /// <summary>Bass guitar / synth bass stem (100% phase-coherent ground front anchor, never decorrelated).</summary>
        Bass = 4,

        /// <summary>Guitar stem (front-side musical width).</summary>
        Guitar = 5,

        /// <summary>Piano / keys stem (wide front-elevated acoustic body).</summary>
        Piano = 6,

        /// <summary>Harmonic instruments / synths / ambience (routed through Hybrid DirAC/COMPASS surround &amp; height expansion).</summary>
        Other = 7,

        /// <summary>Exact energy-conserved residual (<c>mix - sum(stems)</c>).</summary>
        Residual = 8,

        /// <summary>Instrumental bed (<c>mix - vocals</c>).</summary>
        Instrumental = 9
    }

    /// <summary>
    /// Maps separated neural audio stems into physical ITU-R BS.2127-aligned 3D room objects
    /// (<see cref="SourceLayer.NeuralStem"/> / <see cref="SourceLayer.RestoredResidual"/>) and implements
    /// energy-conserving Hybrid Neural+DirAC upmixing (keeping vocals and bass rock-solid in front while
    /// expanding harmonic instruments and ambience into 7.1.4 surround and height layers via <see cref="ParametricStftAnalyzer"/>).
    /// </summary>
    public static class NeuralStemSpatializer {
        static int activeChoreoMode = (int)SpatialChoreoMode.Off;
        static SpatialChoreoTrack activeChoreoTrack;

        /// <summary>
        /// Active BP3 Spatial Object Cortex choreography mode.
        /// </summary>
        public static SpatialChoreoMode ActiveChoreoMode {
            get { return (SpatialChoreoMode)System.Threading.Volatile.Read(ref activeChoreoMode); }
            set { System.Threading.Volatile.Write(ref activeChoreoMode, (int)value); }
        }

        /// <summary>
        /// Active precomputed C2 ring-LUT choreography track (thread-safe snapshot).
        /// </summary>
        public static SpatialChoreoTrack ActiveChoreoTrack {
            get { return System.Threading.Volatile.Read(ref activeChoreoTrack); }
            private set { System.Threading.Volatile.Write(ref activeChoreoTrack, value); }
        }

        /// <summary>
        /// Atomically binds a precomputed <see cref="SpatialChoreoTrack"/> (or clears choreography when null).
        /// </summary>
        public static void SetChoreoTrack(SpatialChoreoTrack track) {
            ActiveChoreoTrack = track;
            if (track != null) {
                ActiveChoreoMode = track.Mode;
            }
        }

        /// <summary>
        /// Builds and binds a governed C2 ring-LUT <see cref="SpatialChoreoTrack"/> from separated stems,
        /// optional distilled <see cref="LyricMoodDigest"/> sections, and an optional semantic director.
        /// </summary>
        public static SpatialChoreoTrack BuildAndSetChoreoTrack(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode,
            IReadOnlyList<LyricMoodDigest> moodDigests = null,
            ISpatialSemanticDirector director = null) {
            SpatialChoreoTrack track = BuildChoreoTrack(stems, sampleRate, mode, moodDigests, director);
            SetChoreoTrack(track);
            return track;
        }

        /// <summary>
        /// Filters out redundant combined stems (e.g. Stage-1 <c>vocals</c> when discrete <c>lead_vocal</c> is present,
        /// or combined <c>instrumental</c> when discrete band stems exist) so 3D object rendering never double-plays
        /// overlapping stems even on legacy cached <c>.ntc</c> bundles.
        /// </summary>
        public static StereoStemBuffer[] FilterRenderableStems(IReadOnlyList<StereoStemBuffer> stems) {
            if (stems == null || stems.Count == 0) {
                return Array.Empty<StereoStemBuffer>();
            }
            bool hasLeadVocal = false;
            bool hasDiscreteBand = false;
            for (int i = 0; i < stems.Count; i++) {
                StereoStemBuffer st = stems[i];
                if (st == null || st.Length <= 0) {
                    continue;
                }
                NeuralStemRole role = ClassifyStemName(st.Name);
                if (role == NeuralStemRole.LeadVocal) {
                    hasLeadVocal = true;
                } else if (role == NeuralStemRole.Drums || role == NeuralStemRole.Bass ||
                           role == NeuralStemRole.Guitar || role == NeuralStemRole.Piano ||
                           role == NeuralStemRole.Other) {
                    hasDiscreteBand = true;
                }
            }
            List<StereoStemBuffer> filtered = new List<StereoStemBuffer>(stems.Count);
            for (int i = 0; i < stems.Count; i++) {
                StereoStemBuffer st = stems[i];
                if (st == null || st.Length <= 0) {
                    continue;
                }
                NeuralStemRole role = ClassifyStemName(st.Name);
                if (hasLeadVocal && role == NeuralStemRole.Vocals) {
                    continue;
                }
                if (hasDiscreteBand && role == NeuralStemRole.Instrumental) {
                    continue;
                }
                filtered.Add(st);
            }
            return filtered.ToArray();
        }

        /// <summary>
        /// Filters redundant combined stem names using the same deduplication rules as
        /// <see cref="FilterRenderableStems(IReadOnlyList{StereoStemBuffer})"/>.
        /// </summary>
        public static string[] FilterRenderableStemNames(IReadOnlyList<string> stemNames) {
            if (stemNames == null || stemNames.Count == 0) {
                return Array.Empty<string>();
            }
            bool hasLeadVocal = false;
            bool hasDiscreteBand = false;
            for (int i = 0; i < stemNames.Count; i++) {
                NeuralStemRole role = ClassifyStemName(stemNames[i]);
                if (role == NeuralStemRole.LeadVocal) {
                    hasLeadVocal = true;
                } else if (role == NeuralStemRole.Drums || role == NeuralStemRole.Bass ||
                           role == NeuralStemRole.Guitar || role == NeuralStemRole.Piano ||
                           role == NeuralStemRole.Other) {
                    hasDiscreteBand = true;
                }
            }
            List<string> filtered = new List<string>(stemNames.Count);
            for (int i = 0; i < stemNames.Count; i++) {
                NeuralStemRole role = ClassifyStemName(stemNames[i]);
                if (hasLeadVocal && role == NeuralStemRole.Vocals) {
                    continue;
                }
                if (hasDiscreteBand && role == NeuralStemRole.Instrumental) {
                    continue;
                }
                filtered.Add(stemNames[i]);
            }
            return filtered.ToArray();
        }

        /// <summary>
        /// Computes static physics-calibrated per-role 3D object gain staging compensating for mono center-object
        /// HRTF pinna incidence versus multi-object lateral stereo band accumulation while preserving full Atmos dynamics.
        /// </summary>
        public static float GetRecommendedStemSourceGain(NeuralStemRole role, int stereoAccompanimentStemCount) {
            switch (role) {
                case NeuralStemRole.LeadVocal:
                case NeuralStemRole.Vocals:
                    // +2.5 dB front-center HRTF presence & mono-object parity against dual-source stereo band stems
                    return 1.3335214f;
                case NeuralStemRole.Bass:
                    return 1.0f;
                case NeuralStemRole.Drums:
                    // -1.0 dB per L/R side object
                    return 0.8912509f;
                case NeuralStemRole.BackingVocals:
                    // -1.5 dB per L/R side object so harmonies frame lead vocals without masking
                    return 0.8413951f;
                case NeuralStemRole.Residual:
                    // -2.0 dB per L/R side object
                    return 0.7943282f;
                default:
                    // Mild multi-stem lateral energy conservation (-2.5 dB on dense 6-stem ensembles, -1.5 dB on 2-4 stems)
                    return stereoAccompanimentStemCount >= 4 ? 0.7498942f : 0.8413951f;
            }
        }

        /// <summary>
        /// Builds a governed C2 ring-LUT <see cref="SpatialChoreoTrack"/> without mutating static global state.
        /// </summary>
        public static SpatialChoreoTrack BuildChoreoTrack(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode,
            IReadOnlyList<LyricMoodDigest> moodDigests = null,
            ISpatialSemanticDirector director = null) {
            StereoStemBuffer[] renderStems = FilterRenderableStems(stems);
            if (renderStems.Length == 0 || sampleRate < 8000) {
                return null;
            }
            int maxLen = 0;
            string[] names = new string[renderStems.Length];
            for (int i = 0; i < renderStems.Length; i++) {
                StereoStemBuffer st = renderStems[i];
                names[i] = st != null && !string.IsNullOrWhiteSpace(st.Name) ? st.Name : ("Stem " + (i + 1));
                if (st != null && st.Length > maxLen) {
                    maxLen = st.Length;
                }
            }
            if (maxLen <= 0) {
                return null;
            }
            double durationSec = maxLen / (double)sampleRate;
            ISpatialSemanticDirector activeDirector = director ?? new DeterministicRuleDirector();
            bool usedFallback;
            DirectorCue[] cues = activeDirector.GenerateCues(renderStems, sampleRate, mode, moodDigests, out usedFallback);
            float[] homeAzDegs = StereoPanAzimuthEstimator.EstimateAllHomeAzimuthsDeg(renderStems);
            return SpatialChoreoTrack.Build(names, cues, mode, durationSec, 0.05, homeAzDegs);
        }

        /// <summary>
        /// Zero-allocation evaluation of a stem's normalized 3D room position from the currently active
        /// <see cref="ActiveChoreoTrack"/> (falling back to static ITU-R BS.2127 anchors when Off or unbound).
        /// </summary>
        public static bool EvaluateStemPositionZeroAlloc(
            int stemIdx,
            NeuralStemRole role,
            float sideSign,
            double timeSec,
            out Vector3 normalizedPos,
            bool loop = false) {
            return EvaluateStemPositionZeroAlloc(ActiveChoreoTrack, ActiveChoreoMode, stemIdx, role, sideSign, timeSec, out normalizedPos, loop);
        }

        static float GetChoreoStereoHalfAngleRad(NeuralStemRole role) {
            float deg;
            switch (role) {
                case NeuralStemRole.BackingVocals:
                    deg = 42f;
                    break;
                case NeuralStemRole.Drums:
                    deg = 28f;
                    break;
                case NeuralStemRole.Guitar:
                case NeuralStemRole.Piano:
                    deg = 32f;
                    break;
                default:
                    deg = 38f;
                    break;
            }
            return deg * (MathF.PI / 180f);
        }

        /// <summary>
        /// Zero-allocation evaluation of a stem's normalized 3D room position from <paramref name="track"/>
        /// using 3D unit-SLERP shortest-arc interpolation with symmetric stereo-arc spread and 0 B GC.
        /// </summary>
        public static bool EvaluateStemPositionZeroAlloc(
            SpatialChoreoTrack track,
            SpatialChoreoMode mode,
            int stemIdx,
            NeuralStemRole role,
            float sideSign,
            double timeSec,
            out Vector3 normalizedPos,
            bool loop = false) {
            if (track == null || mode == SpatialChoreoMode.Off || track.Mode == SpatialChoreoMode.Off ||
                !track.EvaluatePositionZeroAlloc(stemIdx, timeSec, out Vector3 corePos, loop)) {
                normalizedPos = GetAnchorPosition(role, sideSign);
                return false;
            }
            if (MathF.Abs(sideSign) > 0.01f && role != NeuralStemRole.LeadVocal && role != NeuralStemRole.Vocals && role != NeuralStemRole.Bass) {
                // Rotate L/R symmetrically around the vertical Y axis so Backing L/R and stereo pairs span a wide balanced arc
                float angleRad = GetChoreoStereoHalfAngleRad(role) * (sideSign > 0f ? 1f : -1f);
                float cosA = MathF.Cos(angleRad);
                float sinA = MathF.Sin(angleRad);
                float rx = corePos.X * cosA + corePos.Z * sinA;
                float rz = -corePos.X * sinA + corePos.Z * cosA;
                corePos = new Vector3(rx, corePos.Y, rz);
                float len = corePos.Length();
                if (len > 1f) {
                    corePos /= len;
                }
            }
            normalizedPos = corePos;
            return true;
        }

        /// <summary>
        /// Infers the semantic <see cref="NeuralStemRole"/> from a stem file or model output name.
        /// </summary>
        public static NeuralStemRole ClassifyStemName(string stemName) {
            if (string.IsNullOrWhiteSpace(stemName)) {
                return NeuralStemRole.Other;
            }
            string lower = stemName.Trim().ToLowerInvariant();
            if (lower.Contains("lead")) {
                return NeuralStemRole.LeadVocal;
            }
            if (lower.Contains("back") || lower.Contains("harmony") || lower.Contains("choir")) {
                return NeuralStemRole.BackingVocals;
            }
            if (lower.Contains("vocal") || lower.Contains("voice") || lower.Contains("speech") || lower.Contains("dry")) {
                return NeuralStemRole.Vocals;
            }
            if (lower.Contains("drum") || lower.Contains("perc")) {
                return NeuralStemRole.Drums;
            }
            if (lower.Contains("bass") || lower.Contains("sub")) {
                return NeuralStemRole.Bass;
            }
            if (lower.Contains("guitar")) {
                return NeuralStemRole.Guitar;
            }
            if (lower.Contains("piano") || lower.Contains("key")) {
                return NeuralStemRole.Piano;
            }
            if (lower.Contains("residual") || lower.Contains("wet")) {
                return NeuralStemRole.Residual;
            }
            if (lower.Contains("inst") || lower.Contains("karaoke") || lower.Contains("no_vocal")) {
                return NeuralStemRole.Instrumental;
            }
            return NeuralStemRole.Other;
        }

        /// <summary>
        /// Creates ITU-R BS.2127-aligned 3D <see cref="SpatialSourceDescriptor"/> objects for each separated stereo stem
        /// (left/right pair or centered mono anchor) ready for injection into <c>SpatialSceneHub</c>.
        /// </summary>
        public static SpatialSourceDescriptor[] BuildSceneDescriptors(
            IReadOnlyList<string> stemNames,
            Vector3 environmentSize,
            int startIndex = 0) {
            string[] renderNames = FilterRenderableStemNames(stemNames);
            if (renderNames.Length == 0) {
                return Array.Empty<SpatialSourceDescriptor>();
            }
            int stereoAccompCount = 0;
            for (int i = 0; i < renderNames.Length; i++) {
                NeuralStemRole r = ClassifyStemName(renderNames[i]);
                if (r != NeuralStemRole.LeadVocal && r != NeuralStemRole.Vocals && r != NeuralStemRole.Bass) {
                    stereoAccompCount++;
                }
            }
            List<SpatialSourceDescriptor> list = new List<SpatialSourceDescriptor>(renderNames.Length * 2);
            int idx = startIndex;

            for (int i = 0; i < renderNames.Length; i++) {
                string rawName = renderNames[i] ?? ("Stem " + (i + 1));
                NeuralStemRole role = ClassifyStemName(rawName);
                SourceLayer layer = role == NeuralStemRole.Residual ? SourceLayer.RestoredResidual : SourceLayer.NeuralStem;
                float recGain = GetRecommendedStemSourceGain(role, stereoAccompCount);

                if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass) {
                    Vector3 normPos = GetAnchorPosition(role, sideSign: 0f);
                    list.Add(new SpatialSourceDescriptor {
                        Index = idx++,
                        Name = FormatDisplayLabel(rawName, role, 0),
                        Layer = layer,
                        IsDialogue = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals,
                        IsLfe = false,
                        NormalizedPosition = normPos,
                        WorldPositionMeters = normPos * environmentSize,
                        Gain = recGain,
                        Diffuse = role == NeuralStemRole.Bass ? 0f : 0.05f,
                        Divergence = role == NeuralStemRole.Bass ? 0f : 0.08f,
                        Importance = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals ? 1f : 0.9f
                    });
                } else {
                    float diffuse = GetDefaultDiffuseness(role);
                    for (int side = -1; side <= 1; side += 2) {
                        Vector3 normPos = GetAnchorPosition(role, sideSign: side);
                        list.Add(new SpatialSourceDescriptor {
                            Index = idx++,
                            Name = FormatDisplayLabel(rawName, role, side),
                            Layer = layer,
                            IsDialogue = role == NeuralStemRole.BackingVocals,
                            IsLfe = false,
                            NormalizedPosition = normPos,
                            WorldPositionMeters = normPos * environmentSize,
                            Gain = recGain,
                            Diffuse = diffuse,
                            Divergence = diffuse * 0.5f,
                            Importance = 0.75f
                        });
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Creates ITU-R BS.2127-aligned 3D <see cref="SpatialSourceDescriptor"/> objects from separated stereo stems,
        /// preserving each stem's measured constant-power stereo pan azimuth (&lt;= 5 deg centroid error) and natural
        /// inter-channel stereo width when dynamic choreography is disabled (fixed 3D objects).
        /// </summary>
        public static SpatialSourceDescriptor[] BuildSceneDescriptors(
            IReadOnlyList<StereoStemBuffer> stems,
            Vector3 environmentSize,
            int startIndex = 0) {
            StereoStemBuffer[] renderStems = FilterRenderableStems(stems);
            if (renderStems.Length == 0) {
                return Array.Empty<SpatialSourceDescriptor>();
            }
            int stereoAccompCount = 0;
            for (int i = 0; i < renderStems.Length; i++) {
                NeuralStemRole r = ClassifyStemName(renderStems[i].Name);
                if (r != NeuralStemRole.LeadVocal && r != NeuralStemRole.Vocals && r != NeuralStemRole.Bass) {
                    stereoAccompCount++;
                }
            }
            List<SpatialSourceDescriptor> list = new List<SpatialSourceDescriptor>(renderStems.Length * 2);
            int idx = startIndex;

            for (int i = 0; i < renderStems.Length; i++) {
                StereoStemBuffer stem = renderStems[i];
                string rawName = stem != null && !string.IsNullOrWhiteSpace(stem.Name) ? stem.Name : ("Stem " + (i + 1));
                NeuralStemRole role = ClassifyStemName(rawName);
                SourceLayer layer = role == NeuralStemRole.Residual ? SourceLayer.RestoredResidual : SourceLayer.NeuralStem;
                StereoPanEstimate pan = StereoPanAzimuthEstimator.Estimate(stem);
                float recGain = GetRecommendedStemSourceGain(role, stereoAccompCount);

                if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass) {
                    Vector3 normPos = GetPanPreservedAnchorPosition(role, pan, sideSign: 0f);
                    list.Add(new SpatialSourceDescriptor {
                        Index = idx++,
                        Name = FormatDisplayLabel(rawName, role, 0),
                        Layer = layer,
                        IsDialogue = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals,
                        IsLfe = false,
                        NormalizedPosition = normPos,
                        WorldPositionMeters = normPos * environmentSize,
                        Gain = recGain,
                        Diffuse = role == NeuralStemRole.Bass ? 0f : 0.05f,
                        Divergence = role == NeuralStemRole.Bass ? 0f : 0.08f,
                        Importance = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals ? 1f : 0.9f
                    });
                } else {
                    float baseDiffuse = GetDefaultDiffuseness(role);
                    float diffuse = pan.HasSignal
                        ? Math.Min(baseDiffuse, baseDiffuse * (0.25f + 0.75f * pan.StereoWidth01))
                        : baseDiffuse;
                    for (int side = -1; side <= 1; side += 2) {
                        Vector3 normPos = GetPanPreservedAnchorPosition(role, pan, sideSign: side);
                        list.Add(new SpatialSourceDescriptor {
                            Index = idx++,
                            Name = FormatDisplayLabel(rawName, role, side),
                            Layer = layer,
                            IsDialogue = role == NeuralStemRole.BackingVocals,
                            IsLfe = false,
                            NormalizedPosition = normPos,
                            WorldPositionMeters = normPos * environmentSize,
                            Gain = recGain,
                            Diffuse = diffuse,
                            Divergence = diffuse * 0.5f,
                            Importance = 0.75f
                        });
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Performs Hybrid Neural+DirAC 7.1.4 upmixing on separated stereo stems with exact unity energy conservation:
        /// <list type="bullet">
        ///   <item><b>Vocals / Lead Vocal:</b> anchored tightly to Front Center (C) with zero surround smearing.</item>
        ///   <item><b>Bass:</b> anchored 100% phase-coherently to Front L/R (never decorrelated).</item>
        ///   <item><b>Drums:</b> anchored across Front L/R with slight side presence for crisp transient impact.</item>
        ///   <item><b>Backing Vocals / Other / Guitar / Piano / Residual:</b> routed through <see cref="ParametricStftAnalyzer"/>
        ///   DirAC/COMPASS decomposition into FL, FR, C, SL, SR, RL, RR + orthogonal elevated heights (TFL, TFR, TRL, TRR).</item>
        /// </list>
        /// Output channel order: <c>[0=FL, 1=FR, 2=C, 3=SL, 4=SR, 5=RL, 6=RR, 7=TFL, 8=TFR, 9=TRL, 10=TRR]</c> (11 full-range buses).
        /// </summary>
        public static float[][] ProcessHybridNeuralDirAc714(
            StereoStemBuffer[] stems,
            int sampleRate,
            float heightExpansion = 0.35f) {
            StereoStemBuffer[] renderStems = FilterRenderableStems(stems);
            if (renderStems.Length == 0) {
                return Array.Empty<float[]>();
            }
            int length = renderStems[0].Length;
            const int busCount = 11;
            float[][] buses = new float[busCount][];
            for (int b = 0; b < busCount; b++) {
                buses[b] = new float[length];
            }
            if (length == 0) {
                return buses;
            }

            if (heightExpansion < 0f) {
                heightExpansion = 0f;
            } else if (heightExpansion > 0.75f) {
                heightExpansion = 0.75f;
            }

            // Combine ambient/harmonic stems for DirAC surround + height expansion, while anchoring Vocals/Bass/Drums cleanly
            float[] diracInL = new float[length];
            float[] diracInR = new float[length];
            bool hasDiracContent = false;

            const float invSqrt2 = 0.70710678f;

            for (int s = 0; s < renderStems.Length; s++) {
                StereoStemBuffer stem = renderStems[s];
                if (stem == null || stem.Length != length) {
                    continue;
                }
                NeuralStemRole role = ClassifyStemName(stem.Name);
                if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals) {
                    // Energy-preserving Mid/Side split: coherent vocal mono -> Center (C), stereo vocal reverb -> FL/FR
                    for (int n = 0; n < length; n++) {
                        float l = stem.Left[n];
                        float r = stem.Right[n];
                        float mid = (l + r) * invSqrt2; // |mid|^2 + |side|^2 == |l|^2 + |r|^2 identically!
                        float side = (l - r) * invSqrt2;
                        buses[2][n] += mid;
                        buses[0][n] += side * invSqrt2;
                        buses[1][n] -= side * invSqrt2;
                    }
                } else if (role == NeuralStemRole.Bass) {
                    // 100% phase-coherent front anchor (zero surround/height smearing of low frequencies)
                    for (int n = 0; n < length; n++) {
                        buses[0][n] += stem.Left[n];
                        buses[1][n] += stem.Right[n];
                    }
                } else if (role == NeuralStemRole.Drums) {
                    // Tight front stage (90% energy FL/FR) + subtle wide body (10% energy SL/SR) -> exact unit energy!
                    float gFront = MathF.Sqrt(0.90f);
                    float gSide = MathF.Sqrt(0.10f);
                    for (int n = 0; n < length; n++) {
                        buses[0][n] += stem.Left[n] * gFront;
                        buses[1][n] += stem.Right[n] * gFront;
                        buses[3][n] += stem.Left[n] * gSide;
                        buses[4][n] += stem.Right[n] * gSide;
                    }
                } else {
                    // BackingVocals, Guitar, Piano, Other, Residual, Instrumental -> DirAC/COMPASS 7.1 + Height aura
                    hasDiracContent = true;
                    for (int n = 0; n < length; n++) {
                        diracInL[n] += stem.Left[n];
                        diracInR[n] += stem.Right[n];
                    }
                }
            }

            if (hasDiracContent) {
                float[] dFL = new float[length];
                float[] dFR = new float[length];
                float[] dC = new float[length];
                float[] dSL = new float[length];
                float[] dSR = new float[length];
                float[] dRL = new float[length];
                float[] dRR = new float[length];

                using (ParametricStftAnalyzer stft = new ParametricStftAnalyzer(sampleRate)) {
                    stft.ProcessStereoToSurround(
                        diracInL, diracInR,
                        dFL, dFR, dC, dSL, dSR, dRL, dRR,
                        length,
                        extractCenter: true,
                        mode51: false);
                }

                // Exact power-conserving ground/height split of diffuse surround & rear layers:
                // gGround^2 + gHeight^2 == 1.0 identically (zero energy inflation!)
                float gHeight = MathF.Sqrt(heightExpansion);
                float gGround = MathF.Sqrt(1f - heightExpansion);

                OrthogonalDecorrelator heightDecor = new OrthogonalDecorrelator(4, sampleRate);
                float[] tfl = new float[length];
                float[] tfr = new float[length];
                float[] trl = new float[length];
                float[] trr = new float[length];
                for (int n = 0; n < length; n++) {
                    tfl[n] = dSL[n] * gHeight;
                    tfr[n] = dSR[n] * gHeight;
                    trl[n] = dRL[n] * gHeight;
                    trr[n] = dRR[n] * gHeight;
                }
                heightDecor.ProcessInPlace(0, tfl, length);
                heightDecor.ProcessInPlace(1, tfr, length);
                heightDecor.ProcessInPlace(2, trl, length);
                heightDecor.ProcessInPlace(3, trr, length);

                for (int n = 0; n < length; n++) {
                    buses[0][n] += dFL[n];
                    buses[1][n] += dFR[n];
                    buses[2][n] += dC[n];
                    buses[3][n] += dSL[n] * gGround;
                    buses[4][n] += dSR[n] * gGround;
                    buses[5][n] += dRL[n] * gGround;
                    buses[6][n] += dRR[n] * gGround;
                    buses[7][n] += tfl[n];
                    buses[8][n] += tfr[n];
                    buses[9][n] += trl[n];
                    buses[10][n] += trr[n];
                }
            }

            return buses;
        }

        /// <summary>
        /// Returns true when <paramref name="stems"/> contains at least one non-vocal accompaniment/instrument stem
        /// (Bass, Drums, Guitar, Piano, Other, Instrumental, or Residual), distinguishing full-mix spatial upmix
        /// bundles from 2-stem Karaoke vocal-only models (Lead + Backing).
        /// </summary>
        public static bool IsFullMixSpatialStemSet(IReadOnlyList<StereoStemBuffer> stems) {
            if (stems == null || stems.Count == 0) {
                return false;
            }
            for (int i = 0; i < stems.Count; i++) {
                StereoStemBuffer stem = stems[i];
                if (stem == null || stem.Length <= 0) {
                    continue;
                }
                NeuralStemRole role = ClassifyStemName(stem.Name);
                if (role != NeuralStemRole.LeadVocal &&
                    role != NeuralStemRole.BackingVocals &&
                    role != NeuralStemRole.Vocals) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs <see cref="ProcessHybridNeuralDirAc714"/> and applies a complementary 4th-order Linkwitz-Riley
        /// (24 dB/oct, default 120 Hz) crossover across all 7 ground channels so mains remain cleanly high-passed
        /// while sub-120 Hz bass &amp; kick-drum energy routes to a discrete ScreenLFE (.1) channel with exact
        /// canonical 7.1.4 channel ordering:
        /// <c>[0=FL, 1=FR, 2=C, 3=LFE, 4=RL, 5=RR, 6=SL, 7=SR, 8=TFL, 9=TFR, 10=TRL, 11=TRR]</c>.
        /// </summary>
        public static float[][] BuildHybrid714WithDiscreteLfe(
            StereoStemBuffer[] stems,
            int sampleRate,
            float heightExpansion = 0.35f,
            float lfeCutoffHz = 120f) {
            float[][] raw11 = ProcessHybridNeuralDirAc714(stems, sampleRate, heightExpansion);
            if (raw11.Length < 11) {
                return Array.Empty<float[]>();
            }
            int length = raw11[0] != null ? raw11[0].Length : 0;
            float[][] full12 = new float[12][];
            if (length == 0) {
                for (int c = 0; c < 12; c++) {
                    full12[c] = Array.Empty<float>();
                }
                return full12;
            }

            int safeSr = sampleRate >= 8000 ? sampleRate : 48000;
            float[] lfeSum = new float[length];
            float[][] groundHp = new float[7][];
            for (int b = 0; b < 7; b++) {
                float[] srcBus = raw11[b];
                if (srcBus == null || srcBus.Length == 0) {
                    groundHp[b] = new float[length];
                    continue;
                }
                float[] hpBus;
                float[] lpBus;
                SplitLinkwitzRiley4Lfe(srcBus, safeSr, lfeCutoffHz, out hpBus, out lpBus);
                groundHp[b] = hpBus;
                // Mono fold weights conserve bass + kick sub-120 Hz energy without overloading discrete .1 headroom
                float weight = b <= 2 ? 0.50f : 0.35f;
                int limit = length < lpBus.Length ? length : lpBus.Length;
                for (int n = 0; n < limit; n++) {
                    lfeSum[n] += lpBus[n] * weight;
                }
            }

            // Canonical ITU/SMPTE/Cavern Layout714 order: [FL, FR, C, LFE, RL, RR, SL, SR, TFL, TFR, TRL, TRR]
            // ProcessHybridNeuralDirAc714 order:          [0=FL, 1=FR, 2=C, 3=SL, 4=SR, 5=RL, 6=RR, 7=TFL, 8=TFR, 9=TRL, 10=TRR]
            full12[0] = groundHp[0]; // FL
            full12[1] = groundHp[1]; // FR
            full12[2] = groundHp[2]; // FC
            full12[3] = lfeSum;      // ScreenLFE (.1)
            full12[4] = groundHp[5]; // RL
            full12[5] = groundHp[6]; // RR
            full12[6] = groundHp[3]; // SL
            full12[7] = groundHp[4]; // SR
            full12[8] = raw11[7];    // TFL
            full12[9] = raw11[8];    // TFR
            full12[10] = raw11[9];   // TRL
            full12[11] = raw11[10];  // TRR
            return full12;
        }

        /// <summary>
        /// Exact 4th-order Linkwitz-Riley (LR4 = cascaded 2nd-order Butterworth) complementary crossover
        /// splitting <paramref name="input"/> at <paramref name="cutoffHz"/> into high-passed mains and discrete LFE (.1).
        /// </summary>
        public static void SplitLinkwitzRiley4Lfe(
            float[] input,
            int sampleRate,
            float cutoffHz,
            out float[] highPass,
            out float[] lowPass) {
            int n = input != null ? input.Length : 0;
            highPass = new float[n];
            lowPass = new float[n];
            if (n == 0 || sampleRate <= 0) {
                return;
            }
            double maxFc = sampleRate * 0.25;
            if (maxFc > 250.0) {
                maxFc = 250.0;
            }
            double fc = cutoffHz;
            if (fc < 40.0) {
                fc = 40.0;
            } else if (fc > maxFc) {
                fc = maxFc;
            }

            double w0 = 2.0 * Math.PI * fc / sampleRate;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            // Butterworth 2nd-order Q = 1/sqrt(2); two cascaded sections yield exact LR4 (-6 dB @ fc, 0 deg phase sum)
            double alpha = sinW / (2.0 * 0.7071067811865476);
            double a0 = 1.0 + alpha;
            double a1 = (-2.0 * cosW) / a0;
            double a2 = (1.0 - alpha) / a0;

            double lpB0 = ((1.0 - cosW) * 0.5) / a0;
            double lpB1 = (1.0 - cosW) / a0;
            double lpB2 = lpB0;

            double hpB0 = ((1.0 + cosW) * 0.5) / a0;
            double hpB1 = -(1.0 + cosW) / a0;
            double hpB2 = hpB0;

            double lp1x1 = 0.0, lp1x2 = 0.0, lp1y1 = 0.0, lp1y2 = 0.0;
            double lp2x1 = 0.0, lp2x2 = 0.0, lp2y1 = 0.0, lp2y2 = 0.0;
            double hp1x1 = 0.0, hp1x2 = 0.0, hp1y1 = 0.0, hp1y2 = 0.0;
            double hp2x1 = 0.0, hp2x2 = 0.0, hp2y1 = 0.0, hp2y2 = 0.0;

            for (int i = 0; i < n; i++) {
                double x = input[i];
                if (double.IsNaN(x) || double.IsInfinity(x)) {
                    x = 0.0;
                }

                double l1 = lpB0 * x + lpB1 * lp1x1 + lpB2 * lp1x2 - a1 * lp1y1 - a2 * lp1y2;
                lp1x2 = lp1x1; lp1x1 = x; lp1y2 = lp1y1; lp1y1 = l1;
                double l2 = lpB0 * l1 + lpB1 * lp2x1 + lpB2 * lp2x2 - a1 * lp2y1 - a2 * lp2y2;
                lp2x2 = lp2x1; lp2x1 = l1; lp2y2 = lp2y1; lp2y1 = l2;
                lowPass[i] = (float)l2;

                double h1 = hpB0 * x + hpB1 * hp1x1 + hpB2 * hp1x2 - a1 * hp1y1 - a2 * hp1y2;
                hp1x2 = hp1x1; hp1x1 = x; hp1y2 = hp1y1; hp1y1 = h1;
                double h2 = hpB0 * h1 + hpB1 * hp2x1 + hpB2 * hp2x2 - a1 * hp2y1 - a2 * hp2y2;
                hp2x2 = hp2x1; hp2x1 = h1; hp2y2 = hp2y1; hp2y1 = h2;
                highPass[i] = (float)h2;
            }
        }

        static Vector3 GetAnchorPosition(NeuralStemRole role, float sideSign) {
            switch (role) {
                case NeuralStemRole.LeadVocal:
                case NeuralStemRole.Vocals:
                    return new Vector3(0f, 0.08f, 1.0f);
                case NeuralStemRole.BackingVocals:
                    return new Vector3(0.68f * sideSign, 0.30f, 0.65f);
                case NeuralStemRole.Bass:
                    return new Vector3(0f, -0.15f, 0.95f);
                case NeuralStemRole.Drums:
                    return new Vector3(0.50f * sideSign, 0f, 0.85f);
                case NeuralStemRole.Guitar:
                    return new Vector3(0.82f * sideSign, 0.05f, 0.55f);
                case NeuralStemRole.Piano:
                    return new Vector3(0.70f * sideSign, 0.18f, 0.72f);
                case NeuralStemRole.Residual:
                    return new Vector3(0.88f * sideSign, 0.45f, -0.35f);
                default:
                    return new Vector3(0.85f * sideSign, 0.25f, 0.20f);
            }
        }

        static Vector3 GetPanPreservedAnchorPosition(NeuralStemRole role, StereoPanEstimate pan, float sideSign) {
            if (!pan.HasSignal) {
                return GetAnchorPosition(role, sideSign);
            }

            Vector3 refAnchor = GetAnchorPosition(role, sideSign == 0f ? 0f : 1f);
            float refAz;
            float refEl;
            PsychoacousticGovernor.CartesianToSphericalDeg(refAnchor, out refAz, out refEl);
            float refRad = refAnchor.Length();
            if (refRad < 0.35f || refRad > 1.0f) {
                refRad = 0.94f;
            }

            if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass) {
                float az = pan.PanAzimuthDeg;
                float el = refEl;
                float rad = refRad;
                PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                return PsychoacousticGovernor.SphericalToCartesianUnit(az, el) * rad;
            }

            // Compute stereo half-spread proportional to measured inter-channel stereo width,
            // with role-calibrated spatial separation floors so BackingVocals L/R never collapse
            // onto a single glued point while preserving the exact power-weighted pan centroid.
            float maxHalfSpread = Math.Min(MathF.Abs(refAz), 58f);
            if (maxHalfSpread < 12f) {
                maxHalfSpread = 32f;
            }
            float minHalfSpread = role == NeuralStemRole.BackingVocals
                ? 28f
                : (role == NeuralStemRole.Other || role == NeuralStemRole.Residual ? 16f : 0f);
            float halfSpreadDeg = Math.Max(minHalfSpread, maxHalfSpread * SpatialCortexMath.Clamp01(pan.StereoWidth01));
            // Power-balance midpoint compensation: wL*(mid - s) + wR*(mid + s) == pan.PanAzimuthDeg identically
            float midAz = pan.PanAzimuthDeg - pan.PowerBalance * halfSpreadDeg;
            float targetAz = SpatialCortexMath.Clamp(midAz + sideSign * halfSpreadDeg, -110f, 110f);
            float targetEl = refEl;
            float targetRad = refRad;
            PsychoacousticGovernor.ClampRoleAngles(role, ref targetAz, ref targetEl, ref targetRad);
            return PsychoacousticGovernor.SphericalToCartesianUnit(targetAz, targetEl) * targetRad;
        }

        static float GetDefaultDiffuseness(NeuralStemRole role) {
            switch (role) {
                case NeuralStemRole.Drums:
                    return 0.10f;
                case NeuralStemRole.Guitar:
                case NeuralStemRole.Piano:
                    return 0.25f;
                case NeuralStemRole.BackingVocals:
                    return 0.38f;
                case NeuralStemRole.Residual:
                    return 0.65f;
                default:
                    return 0.45f;
            }
        }

        static string FormatDisplayLabel(string rawName, NeuralStemRole role, int side) {
            string suffix = side < 0 ? " L" : (side > 0 ? " R" : string.Empty);
            string clean = string.IsNullOrWhiteSpace(rawName) ? role.ToString() : rawName.Trim();
            if (clean.Length > 0 && char.IsLower(clean[0])) {
                clean = char.ToUpperInvariant(clean[0]) + clean.Substring(1);
            }
            return "AI " + clean + suffix;
        }
    }
}
