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
            if (stemNames == null || stemNames.Count == 0) {
                return Array.Empty<SpatialSourceDescriptor>();
            }
            List<SpatialSourceDescriptor> list = new List<SpatialSourceDescriptor>(stemNames.Count * 2);
            int idx = startIndex;

            for (int i = 0; i < stemNames.Count; i++) {
                string rawName = stemNames[i] ?? ("Stem " + (i + 1));
                NeuralStemRole role = ClassifyStemName(rawName);
                SourceLayer layer = role == NeuralStemRole.Residual ? SourceLayer.RestoredResidual : SourceLayer.NeuralStem;

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
                        Gain = 1f,
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
                            Gain = 1f,
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
            if (stems == null || stems.Length == 0) {
                return Array.Empty<float[]>();
            }
            int length = stems[0].Length;
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

            for (int s = 0; s < stems.Length; s++) {
                StereoStemBuffer stem = stems[s];
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
