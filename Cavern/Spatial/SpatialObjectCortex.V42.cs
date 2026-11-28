using System;
using System.Collections.Generic;
using System.Numerics;

using Cavern.Neural;

namespace Cavern.Spatial {
    public static partial class Layer1AudioForensics {
        internal const float StemAudibleRmsThreshold = 0.0025f;
        internal const float WindowActiveRmsThreshold = 0.0040f;
        const int ActivityEnvelopeBins = 96;

        internal sealed class AnalyzedStemActivitySnapshot {
            public DirectorCue[] CuesRef;
            public float[] HomeAzRef;
            public int StemCount;
            public double DurationSec;
            public bool[] IsAudible;
            public float[] RmsEnvelope;
            public float[] HfEnvelope;
            public int EnvelopeBins;
        }

        [ThreadStatic]
        static AnalyzedStemActivitySnapshot lastAnalyzedSnapshot;

        [ThreadStatic]
        static float[] lastEstimatedHomeAzRef;

        [ThreadStatic]
        static bool[] lastEstimatedHasSignal;

        internal static void RecordEstimatedHomeSignals(float[] homeAzRef, bool[] hasSignal) {
            lastEstimatedHomeAzRef = homeAzRef;
            lastEstimatedHasSignal = hasSignal;
        }

        internal static void RecordAnalyzedStemActivity(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            double durationSec,
            DirectorCue[] cuesRef,
            StereoPanEstimate[] panEstimates) {
            if (stems == null || stems.Count == 0 || sampleRate <= 0) {
                return;
            }
            int stemCount = stems.Count;
            bool[] isAudible = new bool[stemCount];
            int envBins = ActivityEnvelopeBins;
            float[] rmsEnv = new float[stemCount * envBins];
            float[] hfEnv = new float[stemCount * envBins];

            for (int s = 0; s < stemCount; s++) {
                StereoStemBuffer stem = stems[s];
                if (stem == null || stem.Left == null || stem.Right == null || stem.Length <= 0) {
                    continue;
                }
                int len = Math.Min(stem.Length, Math.Min(stem.Left.Length, stem.Right.Length));
                if (len <= 0) {
                    continue;
                }

                double totalSq = 0.0;
                int totalCount = 0;
                for (int b = 0; b < envBins; b++) {
                    int b0 = (int)((long)b * len / envBins);
                    int b1 = Math.Max(b0 + 1, (int)((long)(b + 1) * len / envBins));
                    if (b1 > len) {
                        b1 = len;
                    }
                    int step = Math.Max(1, (b1 - b0) / 64);
                    double sumSq = 0.0;
                    double sumDiffSq = 0.0;
                    float prevM = 0f;
                    int cnt = 0;
                    for (int i = b0; i < b1; i += step) {
                        float l = stem.Left[i];
                        float r = stem.Right[i];
                        float m = 0.5f * ((SpatialCortexMath.IsFinite(l) ? l : 0f) + (SpatialCortexMath.IsFinite(r) ? r : 0f));
                        sumSq += (double)m * m;
                        float d = m - prevM;
                        sumDiffSq += (double)d * d;
                        prevM = m;
                        cnt++;
                    }
                    float binRms = cnt > 0 ? (float)Math.Sqrt(sumSq / cnt) : 0f;
                    float binHf = cnt > 0 && sumSq > 1e-10 ? SpatialCortexMath.Clamp01((float)Math.Sqrt(sumDiffSq / Math.Max(1e-10, sumSq)) * 0.5f) : 0f;
                    rmsEnv[s * envBins + b] = binRms;
                    hfEnv[s * envBins + b] = binHf;
                    totalSq += sumSq;
                    totalCount += cnt;
                }

                float overallRms = totalCount > 0 ? (float)Math.Sqrt(totalSq / totalCount) : 0f;
                bool panSig = panEstimates != null && s < panEstimates.Length && panEstimates[s].HasSignal;
                isAudible[s] = overallRms >= StemAudibleRmsThreshold && (panEstimates == null || panSig);
            }

            lastAnalyzedSnapshot = new AnalyzedStemActivitySnapshot {
                CuesRef = cuesRef,
                HomeAzRef = null,
                StemCount = stemCount,
                DurationSec = durationSec,
                IsAudible = isAudible,
                RmsEnvelope = rmsEnv,
                HfEnvelope = hfEnv,
                EnvelopeBins = envBins
            };
        }

        internal static AnalyzedStemActivitySnapshot MatchActivitySnapshot(DirectorCue[] cuesRef, float[] homeAzRef, int stemCount) {
            AnalyzedStemActivitySnapshot snap = lastAnalyzedSnapshot;
            if (snap != null && snap.StemCount == stemCount) {
                if (cuesRef != null && cuesRef.Length > 0 && ReferenceEquals(snap.CuesRef, cuesRef)) {
                    return snap;
                }
                if (homeAzRef != null && homeAzRef.Length == stemCount && ReferenceEquals(snap.HomeAzRef, homeAzRef)) {
                    return snap;
                }
            }
            if (homeAzRef != null && lastEstimatedHasSignal != null && ReferenceEquals(lastEstimatedHomeAzRef, homeAzRef) && lastEstimatedHasSignal.Length == stemCount) {
                bool[] copySig = new bool[stemCount];
                Array.Copy(lastEstimatedHasSignal, copySig, stemCount);
                return new AnalyzedStemActivitySnapshot {
                    HomeAzRef = homeAzRef,
                    StemCount = stemCount,
                    IsAudible = copySig,
                    EnvelopeBins = 0
                };
            }
            return null;
        }
    }

    public sealed partial class SpatialChoreoTrack {
        readonly bool[] stemAudibleFlags;

        /// <summary>Per-stem audibility flags detected from source RMS energy (silent/dead stems remain anchored).</summary>
        public IReadOnlyList<bool> StemAudibleFlags {
            get { return stemAudibleFlags; }
        }

        /// <summary>True when stem <paramref name="stemIdx"/> carries audible signal energy above the silence floor.</summary>
        public bool IsStemAudible(int stemIdx) {
            if (stemAudibleFlags == null || stemIdx < 0 || stemIdx >= stemAudibleFlags.Length) {
                return true;
            }
            return stemAudibleFlags[stemIdx];
        }

        /// <summary>
        /// Builds a choreography track with explicit per-stem audibility flags and optional RMS/HF envelopes
        /// so dead or silent stems stay anchored and never orbit aimlessly.
        /// </summary>
        public static SpatialChoreoTrack BuildWithStemActivity(
            IReadOnlyList<string> stemNames,
            DirectorCue[] cues,
            SpatialChoreoMode mode,
            double durationSec,
            double stepSec,
            float[] stemHomeAzimuthsDeg,
            bool[] stemHasSignal,
            float[] stemRmsEnvelope = null,
            float[] stemHfEnvelope = null,
            int envelopeBins = 0) {
            if (stemNames == null || stemNames.Count == 0) {
                return new SpatialChoreoTrack(
                    mode,
                    0.05,
                    0.0,
                    Array.Empty<NeuralStemRole>(),
                    Array.Empty<string>(),
                    Array.Empty<DirectorCue>(),
                    Array.Empty<Vector4>(),
                    0,
                    Array.Empty<bool>());
            }

            double safeStep = SpatialCortexMath.ClampDouble(stepSec, 0.02, 0.25);
            double safeDur = SpatialCortexMath.ClampDouble(durationSec, safeStep, 7200.0);
            int frameCount = Math.Max(2, (int)Math.Ceiling(safeDur / safeStep) + 1);
            int stemCount = stemNames.Count;

            NeuralStemRole[] roles = new NeuralStemRole[stemCount];
            string[] names = new string[stemCount];
            for (int s = 0; s < stemCount; s++) {
                names[s] = stemNames[s] ?? ("Stem " + (s + 1));
                roles[s] = NeuralStemSpatializer.ClassifyStemName(names[s]);
            }

            bool[] audibleStems;
            float[] rmsEnv;
            float[] hfEnv;
            int envBins;
            ResolveStemActivityForBuild(
                cues,
                stemHomeAzimuthsDeg,
                stemHasSignal,
                stemRmsEnvelope,
                stemHfEnvelope,
                envelopeBins,
                stemCount,
                out audibleStems,
                out rmsEnv,
                out hfEnv,
                out envBins);

            DirectorCue[] safeCues = cues ?? Array.Empty<DirectorCue>();
            List<DirectorCue> sortedCues = new List<DirectorCue>(safeCues.Length);
            for (int i = 0; i < safeCues.Length; i++) {
                DirectorCue c = safeCues[i];
                float az = c.TargetAzDeg;
                float el = c.TargetElDeg;
                float rad = c.Radius;
                PsychoacousticGovernor.ClampRoleAngles(c.StemRole, ref az, ref el, ref rad);
                sortedCues.Add(new DirectorCue(c.TimeSec, c.StemRole, az, el, rad, c.Energy01));
            }
            sortedCues.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));

            Vector4[] lut = new Vector4[stemCount * frameCount];
            Vector4[] baseAnchors = new Vector4[stemCount];
            bool[] hasMeasuredHome = new bool[stemCount];
            for (int s = 0; s < stemCount; s++) {
                float measuredAz = (stemHomeAzimuthsDeg != null && s < stemHomeAzimuthsDeg.Length)
                    ? stemHomeAzimuthsDeg[s]
                    : float.NaN;
                hasMeasuredHome[s] = SpatialCortexMath.IsFinite(measuredAz);
                baseAnchors[s] = GetDefaultStemAnchor(roles[s], s, stemCount, measuredAz);
            }

            // Step 1: Sample keyframe curves (with active-only strategic orbit) & C2 cubic B-spline interpolation
            int orbitingTotal = 0;
            for (int s = 0; s < stemCount; s++) {
                NeuralStemRole r = roles[s];
                if (audibleStems[s] && r != NeuralStemRole.LeadVocal && r != NeuralStemRole.Vocals && r != NeuralStemRole.Bass) {
                    orbitingTotal++;
                }
            }
            int orbitingSlot = 0;
            for (int s = 0; s < stemCount; s++) {
                NeuralStemRole r = roles[s];
                bool isAnchored = r == NeuralStemRole.LeadVocal || r == NeuralStemRole.Vocals || r == NeuralStemRole.Bass;
                float orbitPhaseOffsetDeg = (audibleStems[s] && !isAnchored && orbitingTotal > 0)
                    ? (orbitingSlot++ * (360f / orbitingTotal))
                    : 0f;
                List<DirectorCue> stemCues = CollectStemCuesV42(
                    sortedCues,
                    s,
                    r,
                    baseAnchors[s],
                    safeDur,
                    mode,
                    orbitPhaseOffsetDeg,
                    hasMeasuredHome[s],
                    audibleStems[s],
                    rmsEnv,
                    envBins);
                SampleStemC2Curve(stemCues, r, lut, s * frameCount, frameCount, safeStep);
            }

            // Step 1B: V4.2 expressive non-Orbit dynamics & silent-window gating before spatial collision repulsion
            ApplyV42ExpressiveAndActivityRefinement(
                lut,
                baseAnchors,
                roles,
                stemCount,
                frameCount,
                safeStep,
                mode,
                audibleStems,
                rmsEnv,
                hfEnv,
                envBins);

            // Step 2: Frame-wise 12x6 ERB repulsion across concurrent active stems
            if (mode != SpatialChoreoMode.Orbit && mode != SpatialChoreoMode.Off) {
                Vector3[] frameDirs = new Vector3[stemCount];
                float[] frameEnergies = new float[stemCount];
                for (int f = 0; f < frameCount; f++) {
                    for (int s = 0; s < stemCount; s++) {
                        Vector4 v = lut[s * frameCount + f];
                        frameDirs[s] = new Vector3(v.X, v.Y, v.Z);
                        frameEnergies[s] = audibleStems[s] ? v.W : 0f;
                    }
                    PsychoacousticGovernor.ApplyErb12x6Repulsion(frameDirs, roles, frameEnergies, stemCount);
                    for (int s = 0; s < stemCount; s++) {
                        int off = s * frameCount + f;
                        float rad = lut[off].W;
                        lut[off] = new Vector4(frameDirs[s].X, frameDirs[s].Y, frameDirs[s].Z, rad);
                    }
                }
            }

            // Step 3: Enforce <= 60 deg/s slew cap (both 3D great-circle & horizontal azimuth), role clamps, and keep dead/silent stems pinned to their anchors
            for (int s = 0; s < stemCount; s++) {
                int baseIdx = s * frameCount;
                NeuralStemRole role = roles[s];
                float maxAzStepDeg = (float)(PsychoacousticGovernor.MaxAngularSlewDegPerSec * safeStep);
                for (int f = 1; f < frameCount; f++) {
                    Vector4 prev = lut[baseIdx + f - 1];
                    Vector4 curr = lut[baseIdx + f];
                    Vector3 prevDir = new Vector3(prev.X, prev.Y, prev.Z);
                    Vector3 currDir = new Vector3(curr.X, curr.Y, curr.Z);
                    Vector3 governed = PsychoacousticGovernor.ApplyAngularHysteresisAndSlew(prevDir, currDir, safeStep, hysteresisDeg: 0f);
                    float prevAz;
                    float prevEl;
                    PsychoacousticGovernor.CartesianToSphericalDeg(prevDir, out prevAz, out prevEl);
                    float az;
                    float el;
                    float rad = curr.W;
                    PsychoacousticGovernor.CartesianToSphericalDeg(governed, out az, out el);
                    float dAz = SpatialCortexMath.WrapAzimuthDeg(az - prevAz);
                    if (MathF.Abs(dAz) > maxAzStepDeg) {
                        az = SpatialCortexMath.WrapAzimuthDeg(prevAz + MathF.Sign(dAz) * maxAzStepDeg);
                    }
                    PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                    Vector3 finalDir = PsychoacousticGovernor.SphericalToCartesianUnit(az, el);
                    lut[baseIdx + f] = new Vector4(finalDir.X, finalDir.Y, finalDir.Z, rad);
                }
            }

            PinSilentStemsToAnchors(lut, baseAnchors, audibleStems, stemCount, frameCount);
            return new SpatialChoreoTrack(mode, safeStep, safeDur, roles, names, sortedCues.ToArray(), lut, frameCount, audibleStems);
        }

        static void ResolveStemActivityForBuild(
            DirectorCue[] cues,
            float[] stemHomeAzimuthsDeg,
            bool[] explicitHasSignal,
            float[] explicitRmsEnv,
            float[] explicitHfEnv,
            int explicitEnvBins,
            int stemCount,
            out bool[] audibleStems,
            out float[] rmsEnv,
            out float[] hfEnv,
            out int envBins) {
            audibleStems = new bool[stemCount];
            rmsEnv = null;
            hfEnv = null;
            envBins = 0;

            if (explicitHasSignal != null && explicitHasSignal.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    audibleStems[s] = explicitHasSignal[s];
                }
                if (explicitRmsEnv != null && explicitEnvBins > 0 && explicitRmsEnv.Length >= stemCount * explicitEnvBins) {
                    rmsEnv = explicitRmsEnv;
                    hfEnv = explicitHfEnv;
                    envBins = explicitEnvBins;
                }
                return;
            }

            Layer1AudioForensics.AnalyzedStemActivitySnapshot snap = Layer1AudioForensics.MatchActivitySnapshot(cues, stemHomeAzimuthsDeg, stemCount);
            if (snap != null && snap.IsAudible != null && snap.IsAudible.Length >= stemCount) {
                for (int s = 0; s < stemCount; s++) {
                    audibleStems[s] = snap.IsAudible[s];
                }
                rmsEnv = snap.RmsEnvelope;
                hfEnv = snap.HfEnvelope;
                envBins = snap.EnvelopeBins;
                return;
            }

            for (int s = 0; s < stemCount; s++) {
                audibleStems[s] = true;
            }
        }

        static List<DirectorCue> CollectStemCuesV42(
            List<DirectorCue> allCues,
            int stemIdx,
            NeuralStemRole role,
            Vector4 anchor,
            double durationSec,
            SpatialChoreoMode mode,
            float orbitPhaseOffsetDeg,
            bool hasMeasuredHomeAz,
            bool stemIsAudible,
            float[] rmsEnvelope,
            int envBins) {
            float anchorAz;
            float anchorEl;
            PsychoacousticGovernor.CartesianToSphericalDeg(new Vector3(anchor.X, anchor.Y, anchor.Z), out anchorAz, out anchorEl);

            if (!stemIsAudible) {
                List<DirectorCue> silent = new List<DirectorCue>(2);
                silent.Add(new DirectorCue(0.0, role, anchorAz, anchorEl, anchor.W, 0f));
                silent.Add(new DirectorCue(durationSec, role, anchorAz, anchorEl, anchor.W, 0f));
                return silent;
            }

            bool isAnchoredRole = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass;
            if (mode == SpatialChoreoMode.Orbit && !isAnchoredRole && durationSec > 0.5) {
                return BuildActiveOrbitCues(stemIdx, role, anchor, anchorAz, anchorEl, durationSec, orbitPhaseOffsetDeg, hasMeasuredHomeAz, rmsEnvelope, envBins);
            }

            return CollectStemCues(allCues, role, anchor, durationSec, mode, orbitPhaseOffsetDeg, hasMeasuredHomeAz);
        }

        static List<DirectorCue> BuildActiveOrbitCues(
            int stemIdx,
            NeuralStemRole role,
            Vector4 anchor,
            float anchorAz,
            float anchorEl,
            double durationSec,
            float orbitPhaseOffsetDeg,
            bool hasMeasuredHomeAz,
            float[] rmsEnvelope,
            int envBins) {
            List<DirectorCue> list = new List<DirectorCue>();
            float startAz = hasMeasuredHomeAz && MathF.Abs(anchorAz) >= 3f
                ? anchorAz
                : SpatialCortexMath.WrapAzimuthDeg(35f + orbitPhaseOffsetDeg);
            float orbitDir = anchorAz < -3f ? -1f : 1f;
            float degPerSec = 22f;
            float totalSweepDeg = (float)Math.Min(360.0 * Math.Max(1.0, Math.Floor(durationSec / 16.0) + 1.0), degPerSec * durationSec);
            int steps = Math.Max(16, (int)Math.Ceiling(totalSweepDeg / 22.5f));
            bool hasEnv = rmsEnvelope != null && envBins > 0 && rmsEnvelope.Length >= (stemIdx + 1) * envBins;

            double activeWeightSum = 0.0;
            float[] stepWeights = hasEnv ? new float[steps] : null;
            if (hasEnv) {
                for (int i = 0; i < steps; i++) {
                    float fMid = (i + 0.5f) / steps;
                    float rms = SampleEnvelopeAtFrac(rmsEnvelope, stemIdx, envBins, fMid);
                    float gate = SpatialCortexMath.Clamp01((rms - 0.002f) / 0.012f);
                    stepWeights[i] = gate;
                    activeWeightSum += gate;
                }
            }

            float accumFrac = 0f;
            double invWeight = activeWeightSum > 1e-4 ? 1.0 / activeWeightSum : 1.0 / steps;
            for (int i = 0; i <= steps; i++) {
                if (i > 0) {
                    if (hasEnv && activeWeightSum > 1e-4) {
                        accumFrac += (float)(stepWeights[i - 1] * invWeight);
                    } else {
                        accumFrac = i / (float)steps;
                    }
                }
                double t = durationSec * (i / (float)steps);
                float az = SpatialCortexMath.WrapAzimuthDeg(startAz + orbitDir * totalSweepDeg * accumFrac);
                float frontClearanceEl = 18f * MathF.Max(0f, MathF.Cos(az * SpatialCortexMath.DegToRad));
                float el = anchorEl + frontClearanceEl + 8f * MathF.Sin(accumFrac * MathF.PI * 2f);
                float rad = anchor.W;
                PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                list.Add(new DirectorCue(t, role, az, el, rad, hasEnv ? (i < steps ? 0.35f + 0.55f * stepWeights[i] : 0.55f) : 0.65f));
            }
            return list;
        }

        static void ApplyV42ExpressiveAndActivityRefinement(
            Vector4[] lut,
            Vector4[] baseAnchors,
            NeuralStemRole[] roles,
            int stemCount,
            int frameCount,
            double stepSec,
            SpatialChoreoMode mode,
            bool[] audibleStems,
            float[] rmsEnvelope,
            float[] hfEnvelope,
            int envBins) {
            if (mode == SpatialChoreoMode.Off || mode == SpatialChoreoMode.Orbit) {
                return;
            }
            double totalDur = Math.Max(stepSec, (frameCount - 1) * stepSec);
            float modeAzSpan = mode == SpatialChoreoMode.Cinematic ? 28f : (mode == SpatialChoreoMode.Musical ? 15f : 7f);
            float modeElSpan = mode == SpatialChoreoMode.Cinematic ? 15f : (mode == SpatialChoreoMode.Musical ? 8f : 3.5f);
            float modeRadSpan = mode == SpatialChoreoMode.Cinematic ? 0.06f : (mode == SpatialChoreoMode.Musical ? 0.045f : 0.025f);

            for (int s = 0; s < stemCount; s++) {
                NeuralStemRole role = roles[s];
                int baseIdx = s * frameCount;
                if (!audibleStems[s]) {
                    continue;
                }
                Vector4 anchor = baseAnchors[s];
                float anchorAz;
                float anchorEl;
                PsychoacousticGovernor.CartesianToSphericalDeg(new Vector3(anchor.X, anchor.Y, anchor.Z), out anchorAz, out anchorEl);

                bool isAnchor = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass;
                bool isSymmetricBacking = role == NeuralStemRole.BackingVocals;
                bool preserveHemisphere = mode == SpatialChoreoMode.Subtle || mode == SpatialChoreoMode.Musical;
                float homeSide = anchorAz >= 0f ? 1f : -1f;
                float phaseOffset = s * 1.37f;
                bool hasEnv = rmsEnvelope != null && envBins > 0 && rmsEnvelope.Length >= (s + 1) * envBins;
                float prevGate = 1f;

                for (int f = 0; f < frameCount; f++) {
                    double t = f * stepSec;
                    float frac = (float)(t / totalDur);
                    float activityGate = 1f;
                    float dynBoost = 1f;
                    float hfLift = 0f;

                    if (hasEnv) {
                        float rms = SampleEnvelopeAtFrac(rmsEnvelope, s, envBins, frac);
                        float rawGate = SpatialCortexMath.Clamp01((rms - 0.002f) / 0.015f);
                        activityGate = prevGate + (rawGate - prevGate) * 0.28f;
                        prevGate = activityGate;
                        dynBoost = 0.65f + 0.65f * SpatialCortexMath.Clamp01(rms * 3.5f);
                        if (hfEnvelope != null && hfEnvelope.Length >= (s + 1) * envBins) {
                            hfLift = SampleEnvelopeAtFrac(hfEnvelope, s, envBins, frac);
                        }
                    }

                    Vector4 curr = lut[baseIdx + f];
                    float currAz;
                    float currEl;
                    PsychoacousticGovernor.CartesianToSphericalDeg(new Vector3(curr.X, curr.Y, curr.Z), out currAz, out currEl);
                    float currRad = curr.W;

                    // Expressive organic waves using non-commensurate golden-ratio frequencies
                    float wavePrimary = MathF.Sin((float)(t * 0.58 + phaseOffset));
                    float waveSecondary = MathF.Cos((float)(t * 0.37 - phaseOffset * 0.7f));
                    float breath = 0.5f + 0.5f * MathF.Sin((float)(t * 0.44 + phaseOffset));

                    float targetAz;
                    float targetEl;
                    float targetRad;

                    if (role == NeuralStemRole.Bass) {
                        // Ground-plane bass anchor: zero azimuth drift, gentle radial fullness breath only
                        targetAz = anchorAz;
                        targetEl = PsychoacousticGovernor.BassElevationDeg;
                        targetRad = Math.Max(0.89f, anchor.W + (breath - 0.5f) * 0.03f * activityGate);
                    } else if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals) {
                        // Tightly anchored front vocal: subtle elevation lift on musical/cinematic energy only
                        targetAz = anchorAz;
                        float vocalElBoost = mode == SpatialChoreoMode.Cinematic ? 3.5f * breath : (mode == SpatialChoreoMode.Musical ? 1.8f * breath : 0f);
                        targetEl = anchorEl + vocalElBoost * activityGate;
                        targetRad = Math.Max(0.90f, anchor.W - 0.02f * breath * activityGate);
                    } else if (isSymmetricBacking) {
                        // Keep BackingVocals near centered axis so symmetric L/R virtual spread remains balanced
                        targetAz = anchorAz + wavePrimary * (mode == SpatialChoreoMode.Cinematic ? 4f : 2.5f) * activityGate;
                        targetEl = currEl + (waveSecondary * 0.5f + 0.5f) * modeElSpan * 0.75f * activityGate + hfLift * 5f;
                        targetRad = Math.Max(0.89f, currRad + (breath - 0.35f) * modeRadSpan * activityGate);
                    } else {
                        float scaleAz = role == NeuralStemRole.Drums && mode == SpatialChoreoMode.Subtle ? 0.55f : 1.0f;
                        float azDelta = (0.65f * wavePrimary + 0.35f * waveSecondary) * modeAzSpan * scaleAz * dynBoost;
                        float elDelta = (0.5f * waveSecondary + 0.5f * breath) * modeElSpan * dynBoost + hfLift * 8f;
                        if (preserveHemisphere) {
                            // Never oscillate across center in Subtle/Musical
                            if (MathF.Abs(anchorAz) < 8f) {
                                float centeredLimit = mode == SpatialChoreoMode.Subtle ? 4.5f : 8.5f;
                                targetAz = SpatialCortexMath.Clamp(currAz + azDelta * 0.35f, -centeredLimit, centeredLimit);
                            } else {
                                float anchorMag = MathF.Max(14f, MathF.Abs(currAz));
                                float modulatedMag = SpatialCortexMath.Clamp(anchorMag + homeSide * azDelta * 0.7f, 14f, 138f);
                                targetAz = homeSide * modulatedMag;
                            }
                        } else {
                            // Cinematic allows dramatic wide stereo + rear/overhead sweep
                            targetAz = SpatialCortexMath.WrapAzimuthDeg(currAz + azDelta * (role == NeuralStemRole.Residual ? 1.25f : 1.0f));
                        }
                        targetEl = currEl + elDelta;
                        targetRad = Math.Max(0.885f, Math.Min(0.995f, currRad + (breath - 0.4f) * modeRadSpan));
                    }

                    // Gate back toward static anchor during silent gaps inside an otherwise active track
                    if (!isAnchor && activityGate < 0.999f) {
                        targetAz = anchorAz + SpatialCortexMath.WrapAzimuthDeg(targetAz - anchorAz) * activityGate;
                        targetEl = anchorEl + (targetEl - anchorEl) * activityGate;
                        targetRad = anchor.W + (targetRad - anchor.W) * activityGate;
                    }

                    PsychoacousticGovernor.ClampRoleAngles(role, ref targetAz, ref targetEl, ref targetRad);
                    targetRad = Math.Max(0.885f, targetRad);
                    Vector3 unit = PsychoacousticGovernor.SphericalToCartesianUnit(targetAz, targetEl);
                    lut[baseIdx + f] = new Vector4(unit.X, unit.Y, unit.Z, targetRad);
                }
            }
        }

        static void PinSilentStemsToAnchors(Vector4[] lut, Vector4[] baseAnchors, bool[] audibleStems, int stemCount, int frameCount) {
            for (int s = 0; s < stemCount; s++) {
                if (audibleStems[s]) {
                    continue;
                }
                int baseIdx = s * frameCount;
                Vector4 anchor = baseAnchors[s];
                for (int f = 0; f < frameCount; f++) {
                    lut[baseIdx + f] = anchor;
                }
            }
        }

        static float SampleEnvelopeAtFrac(float[] envelope, int stemIdx, int bins, float frac) {
            float pos = SpatialCortexMath.Clamp01(frac) * (bins - 1);
            int i0 = (int)pos;
            int i1 = Math.Min(bins - 1, i0 + 1);
            float alpha = pos - i0;
            int off = stemIdx * bins;
            return envelope[off + i0] * (1f - alpha) + envelope[off + i1] * alpha;
        }

        static void AppendWithArcSubdivision(
            List<DirectorCue> list,
            NeuralStemRole role,
            double targetTimeSec,
            float targetAzDeg,
            float targetElDeg,
            float targetRad,
            float energy01,
            float homeSide) {
            if (list.Count == 0) {
                list.Add(new DirectorCue(targetTimeSec, role, targetAzDeg, targetElDeg, targetRad, energy01));
                return;
            }
            DirectorCue prev = list[list.Count - 1];
            double dt = targetTimeSec - prev.TimeSec;
            if (dt <= 0.05) {
                list[list.Count - 1] = new DirectorCue(targetTimeSec, role, targetAzDeg, targetElDeg, targetRad, energy01);
                return;
            }
            float deltaAz = SpatialCortexMath.WrapAzimuthDeg(targetAzDeg - prev.TargetAzDeg);
            float absDelta = MathF.Abs(deltaAz);
            if (absDelta > 55f) {
                float midTest = SpatialCortexMath.WrapAzimuthDeg(prev.TargetAzDeg + deltaAz * 0.5f);
                if (MathF.Abs(midTest) < 25f && MathF.Abs(prev.TargetAzDeg) > 40f && MathF.Abs(targetAzDeg) > 40f) {
                    deltaAz = deltaAz >= 0f ? deltaAz - 360f : deltaAz + 360f;
                    absDelta = MathF.Abs(deltaAz);
                }
                int segs = Math.Max(2, (int)Math.Ceiling(absDelta / 45f));
                for (int s = 1; s < segs; s++) {
                    float frac = s / (float)segs;
                    double midT = prev.TimeSec + dt * frac;
                    float midAz = SpatialCortexMath.WrapAzimuthDeg(prev.TargetAzDeg + deltaAz * frac);
                    float midEl = prev.TargetElDeg + (targetElDeg - prev.TargetElDeg) * frac;
                    float midRad = prev.Radius + (targetRad - prev.Radius) * frac;
                    PsychoacousticGovernor.ClampRoleAngles(role, ref midAz, ref midEl, ref midRad);
                    list.Add(new DirectorCue(midT, role, midAz, midEl, midRad, energy01));
                }
            }
            list.Add(new DirectorCue(targetTimeSec, role, targetAzDeg, targetElDeg, targetRad, energy01));
        }

        static void SampleStemC2Curve(
            List<DirectorCue> keyframes,
            NeuralStemRole role,
            Vector4[] lut,
            int baseIdx,
            int frameCount,
            double stepSec) {
            int kCount = keyframes.Count;
            Vector4[] keyVec = new Vector4[kCount];
            for (int k = 0; k < kCount; k++) {
                Vector3 u = PsychoacousticGovernor.SphericalToCartesianUnit(keyframes[k].TargetAzDeg, keyframes[k].TargetElDeg);
                keyVec[k] = new Vector4(u.X, u.Y, u.Z, keyframes[k].Radius);
            }

            Vector4[] raw = new Vector4[frameCount];
            int cursor = 0;
            for (int f = 0; f < frameCount; f++) {
                double t = f * stepSec;
                while (cursor + 1 < kCount - 1 && keyframes[cursor + 1].TimeSec < t) {
                    cursor++;
                }
                int k0 = cursor;
                int k1 = Math.Min(kCount - 1, cursor + 1);
                double dt = keyframes[k1].TimeSec - keyframes[k0].TimeSec;
                float alpha = dt > 1e-6 ? SpatialCortexMath.Clamp01((float)((t - keyframes[k0].TimeSec) / dt)) : 0f;
                Vector3 u0 = new Vector3(keyVec[k0].X, keyVec[k0].Y, keyVec[k0].Z);
                Vector3 u1 = new Vector3(keyVec[k1].X, keyVec[k1].Y, keyVec[k1].Z);
                Vector3 u = SpatialCortexMath.SlerpUnitShortest(u0, u1, alpha);
                float rad = keyVec[k0].W + (keyVec[k1].W - keyVec[k0].W) * alpha;
                raw[f] = new Vector4(u.X, u.Y, u.Z, rad);
            }

            Vector4[] tmp = new Vector4[frameCount];
            for (int pass = 0; pass < 2; pass++) {
                for (int f = 0; f < frameCount; f++) {
                    Vector4 vPrev = raw[Math.Max(0, f - 1)];
                    Vector4 vCurr = raw[f];
                    Vector4 vNext = raw[Math.Min(frameCount - 1, f + 1)];
                    Vector3 blendedDir = (new Vector3(vPrev.X, vPrev.Y, vPrev.Z) +
                                          new Vector3(vCurr.X, vCurr.Y, vCurr.Z) * 4f +
                                          new Vector3(vNext.X, vNext.Y, vNext.Z)) * (1f / 6f);
                    Vector3 unit = SpatialCortexMath.NormalizeSafe(blendedDir, new Vector3(vCurr.X, vCurr.Y, vCurr.Z));
                    float rad = (vPrev.W + 4f * vCurr.W + vNext.W) * (1f / 6f);
                    tmp[f] = new Vector4(unit.X, unit.Y, unit.Z, rad);
                }
                Array.Copy(tmp, raw, frameCount);
            }

            for (int f = 0; f < frameCount; f++) {
                float az;
                float el;
                float rad = raw[f].W;
                PsychoacousticGovernor.CartesianToSphericalDeg(new Vector3(raw[f].X, raw[f].Y, raw[f].Z), out az, out el);
                PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                Vector3 clamped = PsychoacousticGovernor.SphericalToCartesianUnit(az, el);
                lut[baseIdx + f] = new Vector4(clamped.X, clamped.Y, clamped.Z, rad);
            }
        }
    }

    /// <summary>
    /// Live distilled-lyrics spatial and room-response modulation vector (strictly numeric; zero raw lyric text).
    /// </summary>
    public struct LyricSpatialResponseFx {
        /// <summary>True when active track mood digests drive live modulation.</summary>
        public bool HasMood { get; set; }

        /// <summary>Dominant mood tag for the current playback position.</summary>
        public LyricMoodTag Tag { get; set; }

        /// <summary>Smoothly interpolated arousal intensity in [0; 1].</summary>
        public float Arousal01 { get; set; }

        /// <summary>Smoothly interpolated valence brightness in [0; 1].</summary>
        public float Valence01 { get; set; }

        /// <summary>Horizontal stage width scale multiplier (around 1.0).</summary>
        public float StageWidthScale { get; set; }

        /// <summary>Vertical elevation lift in degrees for harmonic/vocal atmosphere.</summary>
        public float ElevationLiftDeg { get; set; }

        /// <summary>Normalized radial room distance scale multiplier (around 1.0).</summary>
        public float RadiusScale { get; set; }

        /// <summary>Multiplier for live ISM early-reflection ambience level.</summary>
        public float RoomReflectionScale { get; set; }

        /// <summary>Choreography kinetic responsiveness and ear-candy bloom multiplier.</summary>
        public float MotionEnergyGain { get; set; }

        /// <summary>Neutral pass-through modulation vector.</summary>
        public static LyricSpatialResponseFx Neutral {
            get {
                return new LyricSpatialResponseFx {
                    HasMood = false,
                    Tag = LyricMoodTag.Calm,
                    Arousal01 = 0.5f,
                    Valence01 = 0.5f,
                    StageWidthScale = 1f,
                    ElevationLiftDeg = 0f,
                    RadiusScale = 1f,
                    RoomReflectionScale = 1f,
                    MotionEnergyGain = 1f
                };
            }
        }
    }

    /// <summary>
    /// Zero-allocation audio-thread evaluator transforming distilled <see cref="LyricMoodDigest"/> timelines
    /// into smooth stage width, height lift, room radius, and ISM reflection modulation.
    /// </summary>
    public static class LyricMoodSpatialModulator {
        /// <summary>Smooth crossfade duration across mood section transitions in seconds.</summary>
        public const double CrossfadeWindowSec = 2.0;

        /// <summary>
        /// Evaluates interpolated live spatial/room response at <paramref name="playbackTimeSec"/> with zero heap allocations.
        /// </summary>
        public static LyricSpatialResponseFx EvaluateZeroAlloc(
            IReadOnlyList<LyricMoodDigest> digests,
            double playbackTimeSec,
            double trackDurationSec) {
            if (digests == null || digests.Count == 0) {
                return LyricSpatialResponseFx.Neutral;
            }
            double t = double.IsNaN(playbackTimeSec) || double.IsInfinity(playbackTimeSec) || playbackTimeSec < 0.0
                ? 0.0
                : playbackTimeSec;

            bool anyTimestamps = false;
            for (int i = 0; i < digests.Count; i++) {
                if (digests[i].StartTimeSec > 0.01) {
                    anyTimestamps = true;
                    break;
                }
            }

            int idx = 0;
            if (anyTimestamps) {
                for (int i = 0; i < digests.Count; i++) {
                    if (digests[i].StartTimeSec <= t + 0.01) {
                        idx = i;
                    } else {
                        break;
                    }
                }
            } else if (digests.Count > 1) {
                double safeDur = trackDurationSec > 1.0 ? trackDurationSec : Math.Max(30.0, digests.Count * 16.0);
                idx = Math.Min(digests.Count - 1, Math.Max(0, (int)Math.Floor((t / safeDur) * digests.Count)));
            }

            LyricMoodDigest curr = digests[idx];
            LyricSpatialResponseFx fx = BuildFxForDigest(curr);

            // Smoothly blend across section transition boundary over CrossfadeWindowSec
            if (idx > 0 && digests.Count > 1) {
                double boundarySec = anyTimestamps
                    ? curr.StartTimeSec
                    : ((idx / (double)digests.Count) * (trackDurationSec > 1.0 ? trackDurationSec : digests.Count * 16.0));
                double sinceBoundary = t - boundarySec;
                if (sinceBoundary >= 0.0 && sinceBoundary < CrossfadeWindowSec) {
                    LyricSpatialResponseFx prevFx = BuildFxForDigest(digests[idx - 1]);
                    float alpha = SpatialCortexMath.Clamp01((float)(sinceBoundary / CrossfadeWindowSec));
                    float smooth = alpha * alpha * (3f - 2f * alpha);
                    fx.Arousal01 = prevFx.Arousal01 + (fx.Arousal01 - prevFx.Arousal01) * smooth;
                    fx.Valence01 = prevFx.Valence01 + (fx.Valence01 - prevFx.Valence01) * smooth;
                    fx.StageWidthScale = prevFx.StageWidthScale + (fx.StageWidthScale - prevFx.StageWidthScale) * smooth;
                    fx.ElevationLiftDeg = prevFx.ElevationLiftDeg + (fx.ElevationLiftDeg - prevFx.ElevationLiftDeg) * smooth;
                    fx.RadiusScale = prevFx.RadiusScale + (fx.RadiusScale - prevFx.RadiusScale) * smooth;
                    fx.RoomReflectionScale = prevFx.RoomReflectionScale + (fx.RoomReflectionScale - prevFx.RoomReflectionScale) * smooth;
                    fx.MotionEnergyGain = prevFx.MotionEnergyGain + (fx.MotionEnergyGain - prevFx.MotionEnergyGain) * smooth;
                }
            }

            return fx;
        }

        static LyricSpatialResponseFx BuildFxForDigest(LyricMoodDigest digest) {
            float arousal = SpatialCortexMath.Clamp01(digest.Arousal01);
            float valence = SpatialCortexMath.Clamp01(digest.Valence01);
            float width;
            float elev;
            float radiusScale;
            float roomScale;
            float energyGain;

            switch (digest.Tag) {
                case LyricMoodTag.Intimate:
                    width = 0.68f + 0.08f * arousal;
                    elev = -3.5f + 2.5f * valence;
                    radiusScale = 0.85f + 0.04f * arousal;
                    roomScale = 0.62f + 0.10f * arousal;
                    energyGain = 0.58f + 0.14f * arousal;
                    break;
                case LyricMoodTag.Tense:
                    width = 1.12f + 0.12f * arousal;
                    elev = 3.5f + 7.5f * arousal;
                    radiusScale = 0.96f;
                    roomScale = 1.10f + 0.16f * arousal;
                    energyGain = 1.15f + 0.22f * arousal;
                    break;
                case LyricMoodTag.Anthem:
                    width = 1.24f + 0.18f * arousal;
                    elev = 6.5f + 10.5f * arousal + 3.0f * valence;
                    radiusScale = 1.04f + 0.04f * arousal;
                    roomScale = 1.24f + 0.20f * arousal;
                    energyGain = 1.24f + 0.26f * arousal;
                    break;
                default: // Calm
                    width = 0.90f + 0.06f * valence;
                    elev = 1.5f + 3.0f * valence;
                    radiusScale = 0.96f;
                    roomScale = 0.86f + 0.08f * valence;
                    energyGain = 0.70f + 0.12f * arousal;
                    break;
            }

            return new LyricSpatialResponseFx {
                HasMood = true,
                Tag = digest.Tag,
                Arousal01 = arousal,
                Valence01 = valence,
                StageWidthScale = width,
                ElevationLiftDeg = elev,
                RadiusScale = radiusScale,
                RoomReflectionScale = roomScale,
                MotionEnergyGain = energyGain
            };
        }
    }

    /// <summary>
    /// Snapshot of a single subtle DSP spectral/transient micro-object "ear-candy" node in 3D room coordinates.
    /// Value struct for zero-allocation telemetry transport between DSP and 120 Hz Canvas visualization.
    /// </summary>
    public struct EarCandyMicroNode {
        /// <summary>Origin source index (or virtual L/R slot in stereo fallback).</summary>
        public int SourceIndex;

        /// <summary>3D room azimuth in degrees [-180; 180].</summary>
        public float AzimuthDeg;

        /// <summary>3D room elevation in degrees [-62.5; 62.5].</summary>
        public float ElevationDeg;

        /// <summary>Normalized 3D room distance in [0.40; 1.0].</summary>
        public float Radius;

        /// <summary>Normalized shimmer/sparkle envelope in [0; 1].</summary>
        public float Sparkle01;

        /// <summary>Normalized high-frequency transient onset flux in [0; 1].</summary>
        public float TransientFlux01;

        /// <summary>Estimated high-frequency spectral centroid in kHz.</summary>
        public float CentroidKhz;

        /// <summary>Band classification: 0 = upper-mid harmonic, 1 = presence edge, 2 = air sparkle.</summary>
        public byte BandHint;

        /// <summary>True when node carries above-threshold ear-candy energy.</summary>
        public bool Active;
    }

    /// <summary>
    /// Zero-allocation (&lt; 0.3 ms/block, 0 B RT GC) spectral/transient micro-object detector
    /// highlighting subtle high-frequency sparkles, chimes, and upper harmonics across active 3D sources.
    /// </summary>
    public sealed class EarCandySpectralDetector {
        /// <summary>Maximum input sources tracked concurrently.</summary>
        public const int MaxSlots = 16;

        /// <summary>Maximum rendered ear-candy micro-object nodes exposed per snapshot.</summary>
        public const int MaxMicroObjects = 12;

        readonly float[] prevIn = new float[MaxSlots];
        readonly float[] prevHp = new float[MaxSlots];
        readonly float[] emaHf = new float[MaxSlots];
        readonly float[] sparkleEnv = new float[MaxSlots];
        readonly float[] fluxEnv = new float[MaxSlots];
        readonly float[] centroidKhz = new float[MaxSlots];
        readonly float[] sourceAz = new float[MaxSlots];
        readonly float[] sourceEl = new float[MaxSlots];
        readonly float[] sourceRad = new float[MaxSlots];
        readonly EarCandyMicroNode[] snapshot = new EarCandyMicroNode[MaxMicroObjects];

        int cachedSampleRate;
        float cachedHpfAlpha = 0.61f;
        float overallSparkle01;
        int activeNodeCount;

        /// <summary>Smoothed overall ear-candy sparkle level in [0; 1].</summary>
        public float OverallSparkle01 {
            get { return overallSparkle01; }
        }

        /// <summary>Count of active micro-object nodes in the latest telemetry snapshot.</summary>
        public int ActiveNodeCount {
            get { return activeNodeCount; }
        }

        /// <summary>Resets all filter and envelope state without allocating.</summary>
        public void Reset() {
            Array.Clear(prevIn, 0, MaxSlots);
            Array.Clear(prevHp, 0, MaxSlots);
            Array.Clear(emaHf, 0, MaxSlots);
            Array.Clear(sparkleEnv, 0, MaxSlots);
            Array.Clear(fluxEnv, 0, MaxSlots);
            Array.Clear(centroidKhz, 0, MaxSlots);
            Array.Clear(snapshot, 0, MaxMicroObjects);
            overallSparkle01 = 0f;
            activeNodeCount = 0;
        }

        /// <summary>
        /// Analyzes up to <see cref="MaxSlots"/> active mono source buffers in-place with zero heap allocations.
        /// </summary>
        public void ProcessSourcesZeroAlloc(
            float[][] sourceBuffers,
            int sourceCount,
            Vector3[] sourcePositions,
            bool[] skipSlots,
            int sampleRate,
            int frameCount,
            float moodEnergyGain = 1f) {
            if (sourceBuffers == null || sourceCount <= 0 || frameCount <= 0 || sampleRate <= 0) {
                DecayAllSlots();
                BuildSnapshot();
                return;
            }
            EnsureHpfCoeff(sampleRate);
            float gain = SpatialCortexMath.Clamp(moodEnergyGain, 0.5f, 1.5f);
            int slots = Math.Min(sourceCount, MaxSlots);
            int stride = frameCount > 256 ? Math.Max(1, frameCount / 256) : 1;

            for (int s = 0; s < MaxSlots; s++) {
                float[] buf = s < slots ? sourceBuffers[s] : null;
                bool skip = s >= slots || buf == null || buf.Length <= 0 || (skipSlots != null && s < skipSlots.Length && skipSlots[s]);
                if (skip) {
                    DecaySlot(s);
                    continue;
                }
                int limit = Math.Min(frameCount, buf.Length);
                if (limit <= 1) {
                    DecaySlot(s);
                    continue;
                }

                Vector3 pos = (sourcePositions != null && s < sourcePositions.Length)
                    ? sourcePositions[s]
                    : new Vector3(0f, 0f, 0.9f);
                float rad = SpatialCortexMath.Clamp(pos.Length(), 0.40f, 1.0f);
                float az;
                float el;
                PsychoacousticGovernor.CartesianToSphericalDeg(pos, out az, out el);
                sourceAz[s] = az;
                sourceEl[s] = el;
                sourceRad[s] = rad;

                float pIn = prevIn[s];
                float pHp = prevHp[s];
                double totalSq = 0.0;
                double hfSq = 0.0;
                int zcr = 0;
                bool lastSign = pHp >= 0f;
                int counted = 0;

                for (int i = 0; i < limit; i += stride) {
                    float x = buf[i];
                    if (!SpatialCortexMath.IsFinite(x)) {
                        x = 0f;
                    }
                    float hp = cachedHpfAlpha * (pHp + x - pIn);
                    pIn = x;
                    pHp = hp;
                    totalSq += (double)x * x;
                    hfSq += (double)hp * hp;
                    bool sgn = hp >= 0f;
                    if (sgn != lastSign) {
                        zcr++;
                        lastSign = sgn;
                    }
                    counted++;
                }
                prevIn[s] = pIn;
                prevHp[s] = pHp;

                UpdateSlotTelemetry(s, counted, totalSq, hfSq, zcr, sampleRate, stride, gain);
            }

            BuildSnapshot();
        }

        /// <summary>
        /// Zero-allocation interleaved stereo fallback when separated stems are unavailable.
        /// Projects harmonic left/right/air micro-objects cleanly onto the 3D hemisphere.
        /// </summary>
        public void ProcessStereoInterleavedZeroAlloc(
            float[] interleavedStereo,
            int totalFloats,
            int sampleRate,
            float moodEnergyGain = 1f) {
            ProcessStereoInterleavedZeroAlloc(interleavedStereo, 0, totalFloats, sampleRate, moodEnergyGain);
        }

        /// <summary>
        /// Zero-allocation interleaved stereo fallback starting at <paramref name="startFloatOffset"/>.
        /// </summary>
        public void ProcessStereoInterleavedZeroAlloc(
            float[] interleavedStereo,
            int startFloatOffset,
            int floatCount,
            int sampleRate,
            float moodEnergyGain = 1f) {
            if (interleavedStereo == null || startFloatOffset < 0 || floatCount < 4 ||
                startFloatOffset + 4 > interleavedStereo.Length || sampleRate <= 0) {
                DecayAllSlots();
                BuildSnapshot();
                return;
            }
            EnsureHpfCoeff(sampleRate);
            float gain = SpatialCortexMath.Clamp(moodEnergyGain, 0.5f, 1.5f);
            int available = Math.Min(floatCount, interleavedStereo.Length - startFloatOffset) & ~1;
            int frames = available >> 1;
            if (frames <= 1) {
                DecayAllSlots();
                BuildSnapshot();
                return;
            }
            int stride = frames > 256 ? Math.Max(1, frames / 256) : 1;

            // Virtual slots: 0 = Left Air, 1 = Right Air, 2 = Mid Shimmer, 3 = Side Ambient
            for (int v = 0; v < 4; v++) {
                float pIn = prevIn[v];
                float pHp = prevHp[v];
                double totalSq = 0.0;
                double hfSq = 0.0;
                int zcr = 0;
                bool lastSign = pHp >= 0f;
                int counted = 0;

                for (int f = 0; f < frames; f += stride) {
                    int idx = startFloatOffset + (f << 1);
                    float l = interleavedStereo[idx];
                    float r = interleavedStereo[idx + 1];
                    if (!SpatialCortexMath.IsFinite(l)) {
                        l = 0f;
                    }
                    if (!SpatialCortexMath.IsFinite(r)) {
                        r = 0f;
                    }
                    float x = v == 0 ? l : (v == 1 ? r : (v == 2 ? 0.5f * (l + r) : 0.5f * (l - r)));
                    float hp = cachedHpfAlpha * (pHp + x - pIn);
                    pIn = x;
                    pHp = hp;
                    totalSq += (double)x * x;
                    hfSq += (double)hp * hp;
                    bool sgn = hp >= 0f;
                    if (sgn != lastSign) {
                        zcr++;
                        lastSign = sgn;
                    }
                    counted++;
                }
                prevIn[v] = pIn;
                prevHp[v] = pHp;

                sourceAz[v] = v == 0 ? -42f : (v == 1 ? 42f : (v == 2 ? 0f : 108f));
                sourceEl[v] = v == 2 ? 22f : 16f;
                sourceRad[v] = v == 3 ? 0.94f : 0.88f;
                UpdateSlotTelemetry(v, counted, totalSq, hfSq, zcr, sampleRate, stride, gain);
            }

            for (int s = 4; s < MaxSlots; s++) {
                DecaySlot(s);
            }
            BuildSnapshot();
        }

        /// <summary>
        /// Copies active ear-candy micro-object nodes into a preallocated destination buffer (0 B GC).
        /// </summary>
        public int CopySnapshotZeroAlloc(EarCandyMicroNode[] destination, out float sparkle01, out int count) {
            sparkle01 = overallSparkle01;
            count = activeNodeCount;
            if (destination == null || destination.Length == 0 || activeNodeCount <= 0) {
                return 0;
            }
            int copyCount = Math.Min(activeNodeCount, destination.Length);
            for (int i = 0; i < copyCount; i++) {
                destination[i] = snapshot[i];
            }
            return copyCount;
        }

        void EnsureHpfCoeff(int sampleRate) {
            if (sampleRate == cachedSampleRate) {
                return;
            }
            cachedSampleRate = sampleRate;
            float dt = 1f / Math.Max(8000, sampleRate);
            float rc = 1f / (2f * MathF.PI * 3800f);
            cachedHpfAlpha = SpatialCortexMath.Clamp(rc / (rc + dt), 0.15f, 0.96f);
        }

        void UpdateSlotTelemetry(int slot, int counted, double totalSq, double hfSq, int zcr, int sampleRate, int stride, float moodGain) {
            if (counted <= 0 || totalSq <= 1e-10) {
                DecaySlot(slot);
                return;
            }
            float rms = (float)Math.Sqrt(totalSq / counted);
            if (rms < Layer1AudioForensics.StemAudibleRmsThreshold) {
                DecaySlot(slot);
                return;
            }

            float hfRms = (float)Math.Sqrt(hfSq / counted);
            float prevEma = emaHf[slot];
            float newEma = prevEma + (hfRms - prevEma) * 0.14f;
            emaHf[slot] = newEma;

            float hfRatio = SpatialCortexMath.Clamp01(hfRms / Math.Max(1e-4f, rms) * 1.45f);
            float flux = newEma > 1e-5f
                ? SpatialCortexMath.Clamp01((hfRms - newEma) / (newEma * 1.6f + 1e-4f) * 1.8f)
                : SpatialCortexMath.Clamp01(hfRms * 12f);

            float rawSparkle = SpatialCortexMath.Clamp01((0.55f * flux + 0.45f * hfRatio) * moodGain * SpatialCortexMath.Clamp01(rms * 5.0f));
            float prevSp = sparkleEnv[slot];
            float coeff = rawSparkle > prevSp ? 0.48f : 0.10f;
            sparkleEnv[slot] = prevSp + (rawSparkle - prevSp) * coeff;
            fluxEnv[slot] = fluxEnv[slot] + (flux - fluxEnv[slot]) * (flux > fluxEnv[slot] ? 0.55f : 0.15f);

            float effectiveSr = (float)sampleRate / Math.Max(1, stride);
            float estKhz = ((zcr / Math.Max(1f, counted)) * effectiveSr * 0.5f) * 0.001f;
            centroidKhz[slot] = SpatialCortexMath.Clamp(estKhz, 3.2f, 16.0f);
        }

        void DecaySlot(int slot) {
            sparkleEnv[slot] *= 0.84f;
            if (sparkleEnv[slot] < 0.005f) {
                sparkleEnv[slot] = 0f;
            }
            fluxEnv[slot] *= 0.80f;
        }

        void DecayAllSlots() {
            for (int s = 0; s < MaxSlots; s++) {
                DecaySlot(s);
            }
        }

        void BuildSnapshot() {
            int outCount = 0;
            float peakSparkle = 0f;
            float sumSparkle = 0f;

            for (int s = 0; s < MaxSlots && outCount < MaxMicroObjects; s++) {
                float sp = sparkleEnv[s];
                if (sp < 0.03f) {
                    continue;
                }
                if (sp > peakSparkle) {
                    peakSparkle = sp;
                }
                sumSparkle += sp;

                float khz = centroidKhz[s];
                byte band = khz >= 8.5f ? (byte)2 : (khz >= 5.2f ? (byte)1 : (byte)0);
                float phaseSide = (s & 1) == 0 ? 1f : -1f;
                float azShift = phaseSide * (4f + 10f * sp);
                float elLift = 10f + 24f * SpatialCortexMath.Clamp01((khz - 3.2f) / 10f) + 8f * fluxEnv[s];
                float az = SpatialCortexMath.WrapAzimuthDeg(sourceAz[s] + azShift);
                float el = SpatialCortexMath.Clamp(sourceEl[s] + elLift, -62.5f, 62.5f);
                float rad = SpatialCortexMath.Clamp(sourceRad[s] * (0.94f + 0.08f * fluxEnv[s]), 0.42f, 1.0f);

                snapshot[outCount++] = new EarCandyMicroNode {
                    SourceIndex = s,
                    AzimuthDeg = az,
                    ElevationDeg = el,
                    Radius = rad,
                    Sparkle01 = SpatialCortexMath.Clamp01(sp),
                    TransientFlux01 = SpatialCortexMath.Clamp01(fluxEnv[s]),
                    CentroidKhz = khz,
                    BandHint = band,
                    Active = true
                };
            }

            for (int i = outCount; i < MaxMicroObjects; i++) {
                snapshot[i].Active = false;
                snapshot[i].Sparkle01 = 0f;
            }

            activeNodeCount = outCount;
            overallSparkle01 = outCount > 0
                ? SpatialCortexMath.Clamp01(0.65f * peakSparkle + 0.35f * (sumSparkle / outCount))
                : 0f;
        }
    }

    /// <summary>
    /// Zero-allocation audio-thread RMS activity gate keeping silent stems static at their home anchors
    /// and ensuring smooth non-snapping re-entry when instruments return.
    /// </summary>
    public sealed class SpatialChoreoRtGate {
        /// <summary>Maximum number of stems tracked simultaneously.</summary>
        public const int MaxStems = 32;

        readonly float[] gate01 = new float[MaxStems];

        /// <summary>Resets all live gates to neutral.</summary>
        public void Reset() {
            for (int i = 0; i < MaxStems; i++) {
                gate01[i] = 0f;
            }
        }

        /// <summary>Returns the current smoothed [0; 1] gate value for <paramref name="slot"/> without advancing state.</summary>
        public float GetGateZeroAlloc(int slot) {
            return slot >= 0 && slot < MaxStems ? gate01[slot] : 0f;
        }

        /// <summary>Primes the smoothed gate for <paramref name="slot"/> to avoid initial-frame snaps before live RMS telemetry.</summary>
        public void PrimeSlotZeroAlloc(int slot, float value01) {
            if (slot >= 0 && slot < MaxStems) {
                gate01[slot] = SpatialCortexMath.Clamp01(value01);
            }
        }

        /// <summary>
        /// Evaluates a smoothed [0; 1] motion gate from instantaneous stem RMS energy with zero allocations.
        /// Returns 0 when the stem is silent/dead so it holds still at its anchor.
        /// </summary>
        public float UpdateAndEvaluateZeroAlloc(int slot, float rmsLinear, bool isAnchorRole) {
            if (slot < 0 || slot >= MaxStems) {
                return 0f;
            }
            if (isAnchorRole) {
                gate01[slot] = 0f;
                return 0f;
            }
            float safeRms = SpatialCortexMath.IsFinite(rmsLinear) && rmsLinear > 0f ? rmsLinear : 0f;
            float target = safeRms < Layer1AudioForensics.StemAudibleRmsThreshold
                ? 0f
                : SpatialCortexMath.Clamp01((safeRms - Layer1AudioForensics.StemAudibleRmsThreshold) / 0.015f);
            float curr = gate01[slot];
            float coeff = target > curr ? 0.30f : 0.05f;
            curr += (target - curr) * coeff;
            if (curr < 0.005f && target == 0f) {
                curr = 0f;
            }
            gate01[slot] = curr;
            return curr;
        }
    }

    /// <summary>
    /// Psychoacoustic spatial safety governor enforcing vocal/bass anchoring, angular slew-rate caps (&lt;= 60 deg/s),
    /// 2-degree deadband hysteresis, 12x6 ERB spherical cell repulsion (compatible with <see cref="SpatialXRayPowermap"/>),
    /// and ITU-R BS.1770 -1.0 dBTP True-Peak headroom clamping.
    /// </summary>
    public static class PsychoacousticGovernor {
        /// <summary>Maximum allowed azimuth magnitude for Lead Vocal / Vocals stems (degrees).</summary>
        public const float LeadVocalMaxAzimuthDeg = 22f;

        /// <summary>Maximum allowed elevation magnitude for Lead Vocal / Vocals stems (degrees).</summary>
        public const float LeadVocalMaxElevationDeg = 25f;

        /// <summary>Maximum allowed azimuth magnitude for sub-120 Hz mono Bass stems (degrees).</summary>
        public const float BassMaxAzimuthDeg = 15f;

        /// <summary>Fixed ground-plane elevation for Bass stems (degrees).</summary>
        public const float BassElevationDeg = 0f;

        /// <summary>Maximum allowed angular velocity along a shortest spherical arc (degrees/second).</summary>
        public const float MaxAngularSlewDegPerSec = 60f;

        /// <summary>Angular deadband hysteresis suppressing micro-jitter below 2 degrees.</summary>
        public const float HysteresisDeg = 2f;

        /// <summary>ITU-R BS.1770 True-Peak headroom ceiling in dBTP.</summary>
        public const float MaxHeadroomDbTp = -1.0f;

        /// <summary>Linear amplitude equivalent of -1.0 dBTP (<c>10^(-1/20)</c>).</summary>
        public const float MaxHeadroomLinear = 0.89125094f;

        static readonly Vector3[] ErbCellDirections = BuildErbCellDirections();

        /// <summary>
        /// Clamps target spherical coordinates according to strict role psychoacoustic invariants.
        /// </summary>
        public static void ClampRoleAngles(NeuralStemRole role, ref float azDeg, ref float elDeg, ref float radius) {
            azDeg = SpatialCortexMath.WrapAzimuthDeg(azDeg);
            elDeg = SpatialCortexMath.Clamp(elDeg, -62.5f, 62.5f);
            radius = SpatialCortexMath.Clamp(radius, 0.35f, 1.0f);

            if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals) {
                azDeg = SpatialCortexMath.Clamp(azDeg, -LeadVocalMaxAzimuthDeg, LeadVocalMaxAzimuthDeg);
                elDeg = SpatialCortexMath.Clamp(elDeg, -LeadVocalMaxElevationDeg, LeadVocalMaxElevationDeg);
                radius = SpatialCortexMath.Clamp(radius, 0.55f, 1.0f);
            } else if (role == NeuralStemRole.Bass) {
                azDeg = SpatialCortexMath.Clamp(azDeg, -BassMaxAzimuthDeg, BassMaxAzimuthDeg);
                elDeg = BassElevationDeg;
                radius = SpatialCortexMath.Clamp(radius, 0.65f, 1.0f);
            }
        }

        /// <summary>
        /// Converts azimuth and elevation in degrees to a unit 3D room direction matching <see cref="SpatialXRayPowermap"/>.
        /// </summary>
        public static Vector3 SphericalToCartesianUnit(float azDeg, float elDeg) {
            float azRad = azDeg * SpatialCortexMath.DegToRad;
            float elRad = elDeg * SpatialCortexMath.DegToRad;
            float cosE = MathF.Cos(elRad);
            return SpatialCortexMath.NormalizeSafe(
                new Vector3(MathF.Sin(azRad) * cosE, MathF.Sin(elRad), MathF.Cos(azRad) * cosE),
                new Vector3(0f, 0f, 1f));
        }

        /// <summary>
        /// Extracts azimuth and elevation in degrees from a 3D room direction vector.
        /// </summary>
        public static void CartesianToSphericalDeg(Vector3 dir, out float azDeg, out float elDeg) {
            Vector3 u = SpatialCortexMath.NormalizeSafe(dir, new Vector3(0f, 0f, 1f));
            azDeg = MathF.Atan2(u.X, u.Z) * SpatialCortexMath.RadToDeg;
            elDeg = MathF.Asin(SpatialCortexMath.Clamp(u.Y, -1f, 1f)) * SpatialCortexMath.RadToDeg;
        }

        /// <summary>
        /// Enforces the 2-degree deadband hysteresis and &lt;= 60 deg/s shortest-arc angular slew cap.
        /// </summary>
        public static Vector3 ApplyAngularHysteresisAndSlew(
            Vector3 prevUnit,
            Vector3 targetUnit,
            double deltaTimeSec,
            float hysteresisDeg = HysteresisDeg,
            float maxSlewDegPerSec = MaxAngularSlewDegPerSec) {
            Vector3 prev = SpatialCortexMath.NormalizeSafe(prevUnit, new Vector3(0f, 0f, 1f));
            Vector3 target = SpatialCortexMath.NormalizeSafe(targetUnit, prev);
            float dot = SpatialCortexMath.Clamp(Vector3.Dot(prev, target), -1f, 1f);
            float angleDeg = MathF.Acos(dot) * SpatialCortexMath.RadToDeg;

            if (angleDeg < hysteresisDeg) {
                return prev;
            }
            if (deltaTimeSec <= 0.0) {
                return target;
            }
            float maxStepDeg = (float)(maxSlewDegPerSec * deltaTimeSec);
            if (angleDeg <= maxStepDeg || maxStepDeg <= 1e-4f) {
                return angleDeg <= maxStepDeg ? target : prev;
            }
            float frac = SpatialCortexMath.Clamp01(maxStepDeg / angleDeg);
            return SpatialCortexMath.SlerpUnitShortest(prev, target, frac);
        }

        /// <summary>
        /// Projects active stem directions onto the 12x6 ERB spherical grid (matching <see cref="SpatialXRayPowermap"/>)
        /// and repels co-located non-anchor stems out of congested spatial ERB cells while preserving role bounds.
        /// </summary>
        public static void ApplyErb12x6Repulsion(
            Vector3[] stemUnitDirs,
            NeuralStemRole[] stemRoles,
            float[] stemEnergies,
            int count) {
            if (stemUnitDirs == null || stemRoles == null || count <= 1) {
                return;
            }

            float[] cellOcc = new float[SpatialXRayPowermap.TotalCells];
            int[] order = BuildPriorityOrder(stemRoles, count);

            for (int p = 0; p < order.Length; p++) {
                int idx = order[p];
                NeuralStemRole role = stemRoles[idx];
                Vector3 dir = SpatialCortexMath.NormalizeSafe(stemUnitDirs[idx], new Vector3(0f, 0f, 1f));
                float energy = stemEnergies != null && idx < stemEnergies.Length ? Math.Max(0.25f, stemEnergies[idx]) : 1f;

                // Evaluate local 12x6 cell congestion before placing this stem
                if (p > 0 && role != NeuralStemRole.LeadVocal && role != NeuralStemRole.Vocals && role != NeuralStemRole.Bass) {
                    float currentOcc = SampleErbOccupancy(cellOcc, dir);
                    if (currentOcc > 0.45f) {
                        Vector3 gradient = Vector3.Zero;
                        for (int c = 0; c < SpatialXRayPowermap.TotalCells; c++) {
                            float dot = Vector3.Dot(dir, ErbCellDirections[c]);
                            if (dot > 0.25f && cellOcc[c] > 0.1f) {
                                Vector3 away = dir * dot - ErbCellDirections[c];
                                gradient += away * (cellOcc[c] * dot);
                            }
                        }
                        if (gradient.LengthSquared() <= 1e-6f) {
                            // Break symmetric saddle-point co-location consistently toward the stem's own side
                            Vector3 refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
                            Vector3 lateral = SpatialCortexMath.NormalizeSafe(Vector3.Cross(refUp, dir), Vector3.UnitX);
                            float consistentSide = MathF.Abs(dir.X) > 0.02f
                                ? (dir.X >= 0f ? 1f : -1f)
                                : ((idx & 1) == 0 ? 1f : -1f);
                            gradient = lateral * consistentSide;
                        }
                        if (gradient.LengthSquared() > 1e-6f) {
                            // Proportional smooth push (no sudden step jump at occupancy=0.45 threshold)
                            float pushWeight = 0.28f * SpatialCortexMath.Clamp01((currentOcc - 0.45f) * 2.0f);
                            if (pushWeight > 1e-4f) {
                                Vector3 repelled = SpatialCortexMath.NormalizeSafe(dir + Vector3.Normalize(gradient) * pushWeight, dir);
                                float az;
                                float el;
                                float rad = 1f;
                                CartesianToSphericalDeg(repelled, out az, out el);
                                ClampRoleAngles(role, ref az, ref el, ref rad);
                                dir = SphericalToCartesianUnit(az, el);
                                stemUnitDirs[idx] = dir;
                            }
                        }
                    }
                }

                // Deposit raised-cosine occupancy into 12x6 ERB grid
                for (int c = 0; c < SpatialXRayPowermap.TotalCells; c++) {
                    float dot = Vector3.Dot(dir, ErbCellDirections[c]);
                    if (dot > 0.05f) {
                        float lobe = MathF.Pow(dot, 4.5f);
                        cellOcc[c] += energy * lobe;
                    }
                }
            }
        }

        /// <summary>
        /// Clamps a linear peak magnitude or scalar gain against the BS.1770 -1.0 dBTP headroom ceiling.
        /// </summary>
        public static float ClampHeadroomLinear(float value) {
            if (!SpatialCortexMath.IsFinite(value)) {
                return 0f;
            }
            if (value > MaxHeadroomLinear) {
                return MaxHeadroomLinear;
            }
            if (value < -MaxHeadroomLinear) {
                return -MaxHeadroomLinear;
            }
            return value;
        }

        /// <summary>
        /// Zero-allocation in-place BS.1770 -1.0 dBTP True-Peak safety clamp across an output block.
        /// Returns the linear gain scale applied (1.0 when already within headroom).
        /// </summary>
        public static float ClampBlockHeadroomBs1770(float[][] channels, int frameCount) {
            if (channels == null || channels.Length == 0 || frameCount <= 0) {
                return 1f;
            }
            float maxAbs = 0f;
            for (int c = 0; c < channels.Length; c++) {
                float[] buf = channels[c];
                if (buf == null) {
                    continue;
                }
                int limit = Math.Min(frameCount, buf.Length);
                for (int i = 0; i < limit; i++) {
                    float s = buf[i];
                    if (!SpatialCortexMath.IsFinite(s)) {
                        buf[i] = 0f;
                        continue;
                    }
                    float a = MathF.Abs(s);
                    if (a > maxAbs) {
                        maxAbs = a;
                    }
                    // Fast 2x midpoint cubic inter-sample peak guard for BS.1770 True-Peak safety
                    if (i >= 1 && i + 2 < limit) {
                        float p0 = buf[i - 1];
                        float p1 = buf[i];
                        float p2 = buf[i + 1];
                        float p3 = buf[i + 2];
                        float mid = 0.5625f * (p1 + p2) - 0.0625f * (p0 + p3);
                        float am = MathF.Abs(mid);
                        if (am > maxAbs) {
                            maxAbs = am;
                        }
                    }
                }
            }

            if (maxAbs <= MaxHeadroomLinear || maxAbs <= 1e-9f) {
                return 1f;
            }
            float scale = MaxHeadroomLinear / maxAbs;
            for (int c = 0; c < channels.Length; c++) {
                float[] buf = channels[c];
                if (buf == null) {
                    continue;
                }
                int limit = Math.Min(frameCount, buf.Length);
                for (int i = 0; i < limit; i++) {
                    buf[i] *= scale;
                }
            }
            return scale;
        }

        static Vector3[] BuildErbCellDirections() {
            Vector3[] dirs = new Vector3[SpatialXRayPowermap.TotalCells];
            int idx = 0;
            for (int e = 0; e < SpatialXRayPowermap.ElevationBins; e++) {
                float elevDeg = -62.5f + e * 25f;
                for (int a = 0; a < SpatialXRayPowermap.AzimuthBins; a++) {
                    float azDeg = -180f + (a + 0.5f) * (360f / SpatialXRayPowermap.AzimuthBins);
                    dirs[idx++] = SphericalToCartesianUnit(azDeg, elevDeg);
                }
            }
            return dirs;
        }

        static int[] BuildPriorityOrder(NeuralStemRole[] roles, int count) {
            int[] order = new int[count];
            for (int i = 0; i < count; i++) {
                order[i] = i;
            }
            Array.Sort(order, (a, b) => GetRolePriority(roles[a]).CompareTo(GetRolePriority(roles[b])));
            return order;
        }

        static int GetRolePriority(NeuralStemRole role) {
            switch (role) {
                case NeuralStemRole.LeadVocal:
                case NeuralStemRole.Vocals:
                    return 0;
                case NeuralStemRole.Bass:
                    return 1;
                case NeuralStemRole.Drums:
                    return 2;
                case NeuralStemRole.Guitar:
                case NeuralStemRole.Piano:
                    return 3;
                case NeuralStemRole.BackingVocals:
                    return 4;
                default:
                    return 5;
            }
        }

        static float SampleErbOccupancy(float[] cellOcc, Vector3 dir) {
            float sum = 0f;
            for (int c = 0; c < SpatialXRayPowermap.TotalCells; c++) {
                float dot = Vector3.Dot(dir, ErbCellDirections[c]);
                if (dot > 0.5f) {
                    sum += cellOcc[c] * dot;
                }
            }
            return sum;
        }
    }
}
