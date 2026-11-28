using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

using Cavern.Neural;

namespace Cavern.Spatial {
    /// <summary>
    /// Choreography intensity and motion grammar for BP3 Spatial Object Cortex.
    /// </summary>
    public enum SpatialChoreoMode {
        /// <summary>Static ITU-R BS.2127 anchors (zero dynamic choreography).</summary>
        Off = 0,
        /// <summary>Gentle stage breathing within narrow frontal/elevated arcs.</summary>
        Subtle = 1,
        /// <summary>Beat- and section-aligned musical expansion driven by audio forensics and mood digests.</summary>
        Musical = 2,
        /// <summary>Wide 3D surround and height arcs for dramatic sections.</summary>
        Cinematic = 3,
        /// <summary>Smooth continuous orbital progression of harmonic/ambient stems around the listener.</summary>
        Orbit = 4
    }

    /// <summary>
    /// Distilled emotional/structural mood classification derived locally from lyrics without exposing raw text.
    /// </summary>
    public enum LyricMoodTag {
        /// <summary>Low-arousal, serene or ambient passage.</summary>
        Calm = 0,
        /// <summary>High-tension, urgent, or dramatic build-up.</summary>
        Tense = 1,
        /// <summary>Close, personal, mid/low-arousal vocal focus.</summary>
        Intimate = 2,
        /// <summary>High-energy, wide celebratory chorus or climax.</summary>
        Anthem = 3
    }

    /// <summary>
    /// Privacy- and prompt-injection-safe distilled lyric mood summary for a track section.
    /// Contains strictly enums and bounded numeric scalars (zero raw lyric text).
    /// </summary>
    public struct LyricMoodDigest {
        /// <summary>Classified emotional/spatial mood tag for this section.</summary>
        public LyricMoodTag Tag { get; set; }

        /// <summary>Normalized energy/arousal intensity in [0; 1].</summary>
        public float Arousal01 { get; set; }

        /// <summary>Normalized emotional valence in [0; 1] (0 = dark/minor, 1 = bright/uplifting).</summary>
        public float Valence01 { get; set; }

        /// <summary>Zero-based section index within the track.</summary>
        public int SectionIndex { get; set; }

        /// <summary>Section start timestamp in seconds.</summary>
        public double StartTimeSec { get; set; }

        /// <summary>Initializes a distilled <see cref="LyricMoodDigest"/> entry.</summary>
        public LyricMoodDigest(LyricMoodTag tag, float arousal01, float valence01, int sectionIndex, double startTimeSec = 0.0) {
            Tag = tag;
            Arousal01 = SpatialCortexMath.Clamp01(arousal01);
            Valence01 = SpatialCortexMath.Clamp01(valence01);
            SectionIndex = sectionIndex < 0 ? 0 : sectionIndex;
            StartTimeSec = double.IsNaN(startTimeSec) || double.IsInfinity(startTimeSec) || startTimeSec < 0.0 ? 0.0 : startTimeSec;
        }
    }

    /// <summary>
    /// Single semantic choreography waypoint emitted by <see cref="Layer1AudioForensics"/> or <see cref="ISpatialSemanticDirector"/>.
    /// </summary>
    public struct DirectorCue {
        /// <summary>Target timestamp in seconds.</summary>
        public double TimeSec { get; set; }

        /// <summary>Target semantic stem role governed by this cue.</summary>
        public NeuralStemRole StemRole { get; set; }

        /// <summary>Target azimuth in degrees [-180; 180] (0 = front center, + = right, - = left).</summary>
        public float TargetAzDeg { get; set; }

        /// <summary>Target elevation in degrees [-75; 75] (0 = ear-level horizon).</summary>
        public float TargetElDeg { get; set; }

        /// <summary>Normalized room radius in [0.35; 1.0].</summary>
        public float Radius { get; set; }

        /// <summary>Normalized acoustic/section prominence in [0; 1].</summary>
        public float Energy01 { get; set; }

        /// <summary>Initializes a <see cref="DirectorCue"/>.</summary>
        public DirectorCue(double timeSec, NeuralStemRole stemRole, float targetAzDeg, float targetElDeg, float radius, float energy01) {
            TimeSec = double.IsNaN(timeSec) || double.IsInfinity(timeSec) || timeSec < 0.0 ? 0.0 : timeSec;
            StemRole = stemRole;
            TargetAzDeg = SpatialCortexMath.WrapAzimuthDeg(targetAzDeg);
            TargetElDeg = SpatialCortexMath.Clamp(targetElDeg, -75f, 75f);
            Radius = SpatialCortexMath.Clamp(radius, 0.35f, 1.0f);
            Energy01 = SpatialCortexMath.Clamp01(energy01);
        }
    }

    /// <summary>
    /// Optional Plane 1B INT4 HTP constrained-JSON semantic director hook.
    /// Must never receive raw lyric text and operates under a strict 256-token DFA budget.
    /// </summary>
    public interface IPlane1BSemanticHook {
        /// <summary>True when the quantized local NPU/HTP director model is warm and ready for inference.</summary>
        bool IsWarm { get; }

        /// <summary>Executes constrained-grammar JSON inference strictly capped at <paramref name="maxTokens"/> (&lt;= 256).</summary>
        string InferConstrainedJson(string structuredNumericPrompt, int maxTokens);
    }

    /// <summary>
    /// Semantic choreography cue generator contract.
    /// </summary>
    public interface ISpatialSemanticDirector {
        /// <summary>Identifier of the active director implementation.</summary>
        string DirectorId { get; }

        /// <summary>True when an optional neural semantic backend is warm; deterministic rule backend always operates.</summary>
        bool IsModelReady { get; }

        /// <summary>
        /// Generates bounded musical <see cref="DirectorCue"/> waypoints (&lt;= 8 cues/min) from separated stems
        /// and optional distilled <see cref="LyricMoodDigest"/> summaries.
        /// </summary>
        DirectorCue[] GenerateCues(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode,
            IReadOnlyList<LyricMoodDigest> moodDigests,
            out bool usedFallback);
    }

    internal static class SpatialCortexMath {
        public const float DegToRad = (float)(Math.PI / 180.0);
        public const float RadToDeg = (float)(180.0 / Math.PI);

        public static float Clamp(float value, float min, float max) {
            if (float.IsNaN(value) || float.IsInfinity(value)) {
                return min;
            }
            return value < min ? min : (value > max ? max : value);
        }

        public static float Clamp01(float value) {
            return Clamp(value, 0f, 1f);
        }

        public static double ClampDouble(double value, double min, double max) {
            if (double.IsNaN(value) || double.IsInfinity(value)) {
                return min;
            }
            return value < min ? min : (value > max ? max : value);
        }

        public static bool IsFinite(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsFiniteVector(Vector3 v) {
            return IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
        }

        public static float WrapAzimuthDeg(float azDeg) {
            if (!IsFinite(azDeg)) {
                return 0f;
            }
            float wrapped = azDeg % 360f;
            if (wrapped > 180f) {
                wrapped -= 360f;
            } else if (wrapped < -180f) {
                wrapped += 360f;
            }
            return wrapped;
        }

        public static Vector3 NormalizeSafe(Vector3 v, Vector3 fallback) {
            if (!IsFiniteVector(v)) {
                return fallback;
            }
            float lenSq = v.LengthSquared();
            if (lenSq <= 1e-10f || !IsFinite(lenSq)) {
                return fallback;
            }
            return v / MathF.Sqrt(lenSq);
        }

        public static Vector3 SlerpUnitShortest(Vector3 u0, Vector3 u1, float t) {
            float alpha = Clamp01(t);
            Vector3 a = NormalizeSafe(u0, new Vector3(0f, 0f, 1f));
            Vector3 b = NormalizeSafe(u1, a);
            float dot = Clamp(Vector3.Dot(a, b), -1f, 1f);
            // Unit 3D room directions are NOT double-cover quaternions (-u != u):
            // never negate b when dot < 0, otherwise >90 deg paths flip to the opposite hemisphere.
            if (dot > 0.9995f) {
                return NormalizeSafe(a + (b - a) * alpha, a);
            }
            if (dot < -0.9995f) {
                // Antipodal (180 deg): sweep smoothly along a deterministic horizontal great circle
                Vector3 ortho = Vector3.Cross(Vector3.UnitY, a);
                if (ortho.LengthSquared() <= 1e-5f) {
                    ortho = Vector3.Cross(Vector3.UnitX, a);
                }
                ortho = NormalizeSafe(ortho, Vector3.UnitX);
                float angle = alpha * MathF.PI;
                return NormalizeSafe(a * MathF.Cos(angle) + ortho * MathF.Sin(angle), a);
            }
            float theta = MathF.Acos(dot);
            float sinTheta = MathF.Sin(theta);
            if (sinTheta <= 1e-6f) {
                return a;
            }
            float w0 = MathF.Sin((1f - alpha) * theta) / sinTheta;
            float w1 = MathF.Sin(alpha * theta) / sinTheta;
            return NormalizeSafe(a * w0 + b * w1, a);
        }
    }

    /// <summary>
    /// Measured stereo mix azimuth, inter-channel coherence, and perceived stereo width for a separated stem.
    /// Used to preserve original stereo pan placement for fixed 3D objects (&lt;= 5 deg error) and to anchor
    /// dynamic choreography relative to the stem's natural mix position.
    /// </summary>
    public struct StereoPanEstimate {
        /// <summary>Estimated constant-power stereo pan azimuth in degrees (0 = center, - = left, + = right).</summary>
        public float PanAzimuthDeg { get; set; }

        /// <summary>Normalized constant-power pan position in [-1; +1] (-1 = hard left, 0 = center, +1 = hard right).</summary>
        public float PanNormalized { get; set; }

        /// <summary>Power-weighted L/R energy balance <c>(sumR - sumL) / (sumL + sumR)</c> in [-1; +1].</summary>
        public float PowerBalance { get; set; }

        /// <summary>Normalized zero-lag inter-channel correlation (IC) in [-1; +1] (+1 = coherent point source, 0 = diffuse stereo).</summary>
        public float InterChannelCoherence { get; set; }

        /// <summary>Estimated stereo spread width in [0; 1] (0 = tight mono/panned point source, 1 = wide decorrelated stereo).</summary>
        public float StereoWidth01 { get; set; }

        /// <summary>Confidence in [0; 1] reflecting active signal energy and panning clarity.</summary>
        public float Confidence01 { get; set; }

        /// <summary>True when the stem contains audible non-silent stereo PCM energy.</summary>
        public bool HasSignal { get; set; }
    }

    /// <summary>
    /// Deterministic constant-power stereo pan azimuth and inter-channel coherence (IC) estimator.
    /// Maps constant-power stereo L/R stems back onto Cavern's virtual front stage azimuths with sub-degree accuracy
    /// while preserving wide stereo spread for decorrelated ambient stems.
    /// </summary>
    public static class StereoPanAzimuthEstimator {
        /// <summary>Default virtual stereo stage half-width in degrees (<c>[-60; +60]</c> deg ITU/Cavern wide stage).</summary>
        public const float DefaultStageHalfWidthDeg = 60f;

        /// <summary>
        /// Estimates the constant-power stereo pan azimuth, power balance, inter-channel coherence, and stereo width
        /// of <paramref name="stem"/>.
        /// </summary>
        public static StereoPanEstimate Estimate(StereoStemBuffer stem, float stageHalfWidthDeg = DefaultStageHalfWidthDeg) {
            float safeStage = SpatialCortexMath.Clamp(stageHalfWidthDeg, 20f, 90f);
            if (stem == null || stem.Left == null || stem.Right == null || stem.Length <= 0) {
                return new StereoPanEstimate {
                    PanAzimuthDeg = 0f,
                    PanNormalized = 0f,
                    PowerBalance = 0f,
                    InterChannelCoherence = 0f,
                    StereoWidth01 = 0.5f,
                    Confidence01 = 0f,
                    HasSignal = false
                };
            }

            int len = Math.Min(stem.Length, Math.Min(stem.Left.Length, stem.Right.Length));
            if (len <= 0) {
                return new StereoPanEstimate {
                    PanAzimuthDeg = 0f,
                    PanNormalized = 0f,
                    PowerBalance = 0f,
                    InterChannelCoherence = 0f,
                    StereoWidth01 = 0.5f,
                    Confidence01 = 0f,
                    HasSignal = false
                };
            }

            // Strided energy integration (up to ~16384 frames covers full song dynamics with <0.1 ms cost)
            int step = Math.Max(1, len / 16384);
            double sumL = 0.0;
            double sumR = 0.0;
            double sumLR = 0.0;
            int counted = 0;

            for (int i = 0; i < len; i += step) {
                float l = stem.Left[i];
                float r = stem.Right[i];
                if (!SpatialCortexMath.IsFinite(l)) {
                    l = 0f;
                }
                if (!SpatialCortexMath.IsFinite(r)) {
                    r = 0f;
                }
                sumL += (double)l * l;
                sumR += (double)r * r;
                sumLR += (double)l * r;
                counted++;
            }

            double totalEnergy = sumL + sumR;
            double meanPower = counted > 0 ? totalEnergy / counted : 0.0;
            if (totalEnergy <= 1e-12 || meanPower <= 1e-11) {
                return new StereoPanEstimate {
                    PanAzimuthDeg = 0f,
                    PanNormalized = 0f,
                    PowerBalance = 0f,
                    InterChannelCoherence = 0f,
                    StereoWidth01 = 0.5f,
                    Confidence01 = 0f,
                    HasSignal = false
                };
            }

            double rmsL = Math.Sqrt(sumL / counted);
            double rmsR = Math.Sqrt(sumR / counted);
            // Constant-power panning law inverse: L = cos(phi), R = sin(phi) where phi in [0, pi/2]
            // phi = 0 -> hard Left (-1), phi = pi/4 -> Center (0), phi = pi/2 -> hard Right (+1)
            double phiRms = Math.Atan2(rmsR, rmsL);
            double denom = Math.Sqrt(sumL * sumR);
            float ic = denom > 1e-12
                ? SpatialCortexMath.Clamp((float)(sumLR / denom), -1f, 1f)
                : 1f; // Pure single-channel hard-panned stem is a coherent point source

            // Stokes / 2x2 covariance principal-eigenvector pan extraction:
            // For a coherent panned source [cos(phi), sin(phi)] + isotropic uncorrelated stereo reverb sigma^2*I,
            // Q = sumL - sumR = Ps*cos(2*phi) and U = 2*sumLR = Ps*sin(2*phi) are invariant to isotropic reverb!
            double stokesQ = sumL - sumR;
            double stokesU = 2.0 * sumLR;
            double polarizedNorm = Math.Sqrt(stokesQ * stokesQ + stokesU * stokesU);
            float degreeOfPolarization = SpatialCortexMath.Clamp01((float)(polarizedNorm / totalEnergy));
            double phi = phiRms;
            if (sumLR > 0.0 && ic > 0.05f && degreeOfPolarization > 0.20f) {
                double phiPca = 0.5 * Math.Atan2(stokesU, stokesQ);
                float pcaWeight = SpatialCortexMath.Clamp01((degreeOfPolarization - 0.20f) / 0.30f)
                    * SpatialCortexMath.Clamp01((ic - 0.05f) / 0.15f);
                phi = phiRms + (phiPca - phiRms) * pcaWeight;
            }

            float panNorm = SpatialCortexMath.Clamp((float)((phi / (Math.PI * 0.25)) - 1.0), -1f, 1f);
            float panAzDeg = panNorm * safeStage;
            float powerBalance = SpatialCortexMath.Clamp((float)((sumR - sumL) / totalEnergy), -1f, 1f);

            // Point sources (high positive IC or hard-panned) collapse to tight width; balanced decorrelated stems stay wide
            float decorrelation = SpatialCortexMath.Clamp01(1f - Math.Max(0f, ic));
            float centerWeight = SpatialCortexMath.Clamp01(1f - MathF.Abs(panNorm));
            float stereoWidth = SpatialCortexMath.Clamp01(decorrelation * (0.25f + 0.75f * centerWeight));
            float confidence = SpatialCortexMath.Clamp01(0.45f + 0.55f * Math.Max(MathF.Abs(panNorm), Math.Max(0f, ic)));

            return new StereoPanEstimate {
                PanAzimuthDeg = panAzDeg,
                PanNormalized = panNorm,
                PowerBalance = powerBalance,
                InterChannelCoherence = ic,
                StereoWidth01 = stereoWidth,
                Confidence01 = confidence,
                HasSignal = true
            };
        }

        /// <summary>
        /// Computes continuous stereo-pan home azimuths (degrees) for every stem in <paramref name="stems"/>,
        /// returning <see cref="float.NaN"/> for silent/unpanned stems so role defaults apply cleanly.
        /// </summary>
        public static float[] EstimateAllHomeAzimuthsDeg(
            IReadOnlyList<StereoStemBuffer> stems,
            float stageHalfWidthDeg = DefaultStageHalfWidthDeg) {
            if (stems == null || stems.Count == 0) {
                return Array.Empty<float>();
            }
            float[] result = new float[stems.Count];
            bool[] hasSignal = new bool[stems.Count];
            for (int i = 0; i < stems.Count; i++) {
                StereoPanEstimate est = Estimate(stems[i], stageHalfWidthDeg);
                hasSignal[i] = est.HasSignal;
                result[i] = est.HasSignal ? est.PanAzimuthDeg : float.NaN;
            }
            Layer1AudioForensics.RecordEstimatedHomeSignals(result, hasSignal);
            return result;
        }
    }

    /// <summary>
    /// Deterministic 0 MB Layer-1 audio forensics engine: extracts spectral-flux beat/onset peaks,
    /// Self-Similarity Matrix (SSM) structural section boundaries, and per-stem RMS/spectral centroid
    /// features to produce bounded musical choreography cues (&lt;= 8 cues/min).
    /// </summary>
    public static partial class Layer1AudioForensics {
        /// <summary>Hard ceiling on choreography cues emitted per 60-second window.</summary>
        public const int MaxCuesPerMinute = 8;

        const int FftSize = 512;
        const int HalfBins = FftSize / 2;
        const int SsmBands = 12;
        const int MaxSsmFrames = 240;

        /// <summary>
        /// Analyzes separated stereo stems deterministically and emits &lt;= 8 cues/min of musical <see cref="DirectorCue"/> waypoints.
        /// </summary>
        public static DirectorCue[] Analyze(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode = SpatialChoreoMode.Musical,
            IReadOnlyList<LyricMoodDigest> moodDigests = null) {
            if (mode == SpatialChoreoMode.Off || stems == null || stems.Count == 0 || sampleRate < 8000) {
                return Array.Empty<DirectorCue>();
            }

            int maxSamples = 0;
            for (int i = 0; i < stems.Count; i++) {
                StereoStemBuffer stem = stems[i];
                if (stem != null && stem.Length > maxSamples) {
                    maxSamples = stem.Length;
                }
            }
            if (maxSamples <= 0) {
                return Array.Empty<DirectorCue>();
            }

            double durationSec = maxSamples / (double)sampleRate;
            if (durationSec < 0.25) {
                return Array.Empty<DirectorCue>();
            }

            float[] mixMono = BuildMixMono(stems, maxSamples);
            int hopSamples = Math.Max(256, sampleRate / 25); // 40 ms analysis frames
            double[] frameTimes;
            float[] flux = ComputeSpectralFluxOnsets(mixMono, sampleRate, hopSamples, FftSize, out frameTimes);
            double[] sections = DetectSsmSectionBoundaries(mixMono, sampleRate, 16);

            List<double> candidateTimes = CollectCandidateTimestamps(flux, frameTimes, sections, durationSec, mode);
            if (candidateTimes.Count == 0) {
                candidateTimes.Add(Math.Min(durationSec * 0.5, 2.0));
            }

            // Identify moveable non-anchor stems first, falling back to all stems if only 1 stem exists
            List<int> stemOrder = BuildChoreoStemOrder(stems);
            if (stemOrder.Count == 0) {
                return Array.Empty<DirectorCue>();
            }

            List<DirectorCue> rawCues = new List<DirectorCue>(candidateTimes.Count);
            double windowHalfSec = 1.2;

            // Measure each stem's continuous stereo pan azimuth once so dynamic choreography
            // anchors and orbits relative to the original stereo mix placement.
            StereoPanEstimate[] panEstimates = new StereoPanEstimate[stems.Count];
            float[] stemHomeAzimuths = new float[stems.Count];
            for (int i = 0; i < stems.Count; i++) {
                panEstimates[i] = StereoPanAzimuthEstimator.Estimate(stems[i]);
                NeuralStemRole sRole = stems[i] != null
                    ? NeuralStemSpatializer.ClassifyStemName(stems[i].Name)
                    : NeuralStemRole.Other;
                stemHomeAzimuths[i] = ResolveStemHomeAzimuthDeg(panEstimates[i], sRole, i);
            }

            for (int c = 0; c < candidateTimes.Count; c++) {
                double tSec = candidateTimes[c];
                int chosenStemIdx = SelectMostProminentStem(stems, stemOrder, sampleRate, tSec, windowHalfSec, c);
                StereoStemBuffer stem = stems[chosenStemIdx];
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stem.Name);

                float rms;
                float centroidHz;
                float subBassRatio;
                ComputeStemRmsAndCentroid(stem, sampleRate, Math.Max(0.0, tSec - windowHalfSec), Math.Min(durationSec, tSec + windowHalfSec), out rms, out centroidHz, out subBassRatio);

                LyricMoodDigest mood = ResolveMoodAtTime(moodDigests, tSec, durationSec);
                float energy01 = SpatialCortexMath.Clamp01(rms * 3.2f * (0.65f + 0.35f * mood.Arousal01));

                float azDeg;
                float elDeg;
                float radius;
                float progress01 = durationSec > 1e-3 ? SpatialCortexMath.Clamp01((float)(tSec / durationSec)) : 0f;
                ComputeTargetPolarForCue(
                    role,
                    mode,
                    mood,
                    stemHomeAzimuths[chosenStemIdx],
                    panEstimates[chosenStemIdx].StereoWidth01,
                    progress01,
                    centroidHz,
                    subBassRatio,
                    energy01,
                    out azDeg,
                    out elDeg,
                    out radius);

                rawCues.Add(new DirectorCue(tSec, role, azDeg, elDeg, radius, energy01));
            }

            DirectorCue[] budgeted = EnforceCueRateBudget(rawCues, durationSec, MaxCuesPerMinute);
            RecordAnalyzedStemActivity(stems, sampleRate, durationSec, budgeted, panEstimates);
            return budgeted;
        }

        /// <summary>
        /// Computes normalized half-wave rectified spectral flux onset envelope from mono PCM.
        /// </summary>
        public static float[] ComputeSpectralFluxOnsets(
            float[] mono,
            int sampleRate,
            int hopSamples,
            int fftSize,
            out double[] frameTimesSec) {
            if (mono == null || mono.Length == 0 || sampleRate <= 0) {
                frameTimesSec = Array.Empty<double>();
                return Array.Empty<float>();
            }
            int safeFft = NextPowerOfTwo(Math.Max(64, Math.Min(2048, fftSize)));
            int safeHop = Math.Max(64, hopSamples);
            int frameCount = mono.Length < safeFft ? 1 : 1 + (mono.Length - safeFft) / safeHop;

            float[] flux = new float[frameCount];
            frameTimesSec = new double[frameCount];
            float[] window = BuildHannWindow(safeFft);
            int half = safeFft / 2;
            float[] re = new float[safeFft];
            float[] im = new float[safeFft];
            float[] prevMag = new float[half];
            float maxFlux = 1e-6f;

            for (int f = 0; f < frameCount; f++) {
                int start = f * safeHop;
                frameTimesSec[f] = (start + safeFft * 0.5) / sampleRate;
                Array.Clear(re, 0, safeFft);
                Array.Clear(im, 0, safeFft);

                int limit = Math.Min(safeFft, mono.Length - start);
                for (int i = 0; i < limit; i++) {
                    float s = mono[start + i];
                    re[i] = SpatialCortexMath.IsFinite(s) ? s * window[i] : 0f;
                }

                FftInPlace(re, im, safeFft);

                float frameFlux = 0f;
                for (int k = 1; k < half; k++) {
                    float mag = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    float diff = mag - prevMag[k];
                    if (diff > 0f) {
                        frameFlux += diff;
                    }
                    prevMag[k] = mag;
                }
                flux[f] = frameFlux;
                if (frameFlux > maxFlux) {
                    maxFlux = frameFlux;
                }
            }

            float invMax = 1f / maxFlux;
            for (int f = 0; f < frameCount; f++) {
                flux[f] *= invMax;
            }
            return flux;
        }

        /// <summary>
        /// Detects structural section boundary timestamps (in seconds) using a 12-band log-spectral
        /// Self-Similarity Matrix (SSM) and a Foote checkerboard novelty kernel along the diagonal.
        /// </summary>
        public static double[] DetectSsmSectionBoundaries(float[] mono, int sampleRate, int maxSections = 16) {
            if (mono == null || mono.Length == 0 || sampleRate < 8000 || maxSections <= 0) {
                return Array.Empty<double>();
            }
            double durationSec = mono.Length / (double)sampleRate;
            if (durationSec < 14.0) {
                return Array.Empty<double>();
            }

            double superFrameSec = Math.Max(0.75, durationSec / MaxSsmFrames);
            int numSuper = (int)Math.Min(MaxSsmFrames, Math.Max(4, Math.Floor(durationSec / superFrameSec)));
            int samplesPerSuper = Math.Max(FftSize, mono.Length / numSuper);

            float[] features = new float[numSuper * SsmBands];
            float[] window = BuildHannWindow(FftSize);
            float[] re = new float[FftSize];
            float[] im = new float[FftSize];

            for (int s = 0; s < numSuper; s++) {
                int center = Math.Min(mono.Length - 1, s * samplesPerSuper + samplesPerSuper / 2);
                int start = Math.Max(0, Math.Min(mono.Length - FftSize, center - FftSize / 2));
                Array.Clear(re, 0, FftSize);
                Array.Clear(im, 0, FftSize);
                int limit = Math.Min(FftSize, mono.Length - start);
                for (int i = 0; i < limit; i++) {
                    float val = mono[start + i];
                    re[i] = SpatialCortexMath.IsFinite(val) ? val * window[i] : 0f;
                }
                FftInPlace(re, im, FftSize);

                int fBase = s * SsmBands;
                float normSq = 0f;
                for (int k = 1; k < HalfBins; k++) {
                    float mag = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    int band = (int)((MathF.Log(1f + k) / MathF.Log(1f + HalfBins)) * SsmBands);
                    if (band >= SsmBands) {
                        band = SsmBands - 1;
                    }
                    features[fBase + band] += mag;
                }
                for (int b = 0; b < SsmBands; b++) {
                    float logged = MathF.Log(1f + features[fBase + b]);
                    features[fBase + b] = logged;
                    normSq += logged * logged;
                }
                float invNorm = normSq > 1e-10f ? 1f / MathF.Sqrt(normSq) : 0f;
                for (int b = 0; b < SsmBands; b++) {
                    features[fBase + b] *= invNorm;
                }
            }

            // Checkerboard kernel half-width L = 3 (6x6 window) along the main diagonal
            const int L = 3;
            if (numSuper <= L * 2 + 2) {
                return Array.Empty<double>();
            }
            float[] novelty = new float[numSuper];
            float maxNov = 0f;
            float meanNov = 0f;
            int validCount = 0;

            for (int i = L; i < numSuper - L; i++) {
                float score = 0f;
                for (int u = -L; u < L; u++) {
                    for (int v = -L; v < L; v++) {
                        float kSign = ((u < 0) == (v < 0)) ? 1f : -1f;
                        float sim = CosineFeatureSim(features, (i + u) * SsmBands, (i + v) * SsmBands, SsmBands);
                        score += kSign * sim;
                    }
                }
                if (score < 0f) {
                    score = 0f;
                }
                novelty[i] = score;
                meanNov += score;
                validCount++;
                if (score > maxNov) {
                    maxNov = score;
                }
            }

            if (validCount == 0 || maxNov <= 1e-6f) {
                return Array.Empty<double>();
            }
            meanNov /= validCount;
            float threshold = Math.Max(meanNov * 1.25f, maxNov * 0.30f);

            List<double> boundaries = new List<double>();
            double lastBoundarySec = -100.0;
            const double minSectionSpacingSec = 10.0;

            for (int i = L + 1; i < numSuper - L - 1; i++) {
                float n = novelty[i];
                if (n >= threshold && n >= novelty[i - 1] && n >= novelty[i + 1]) {
                    double tSec = (i * samplesPerSuper) / (double)sampleRate;
                    if (tSec >= 6.0 && tSec <= durationSec - 4.0 && (tSec - lastBoundarySec) >= minSectionSpacingSec) {
                        boundaries.Add(tSec);
                        lastBoundarySec = tSec;
                        if (boundaries.Count >= maxSections) {
                            break;
                        }
                    }
                }
            }
            return boundaries.ToArray();
        }

        /// <summary>
        /// Computes window RMS, spectral centroid (Hz), and sub-120 Hz energy ratio for a single stereo stem.
        /// </summary>
        public static void ComputeStemRmsAndCentroid(
            StereoStemBuffer stem,
            int sampleRate,
            double startSec,
            double endSec,
            out float rms,
            out float centroidHz,
            out float subBassRatio) {
            rms = 0f;
            centroidHz = 1000f;
            subBassRatio = 0f;
            if (stem == null || stem.Length <= 0 || sampleRate <= 0) {
                return;
            }
            int s0 = Math.Max(0, Math.Min(stem.Length - 1, (int)Math.Floor(startSec * sampleRate)));
            int s1 = Math.Max(s0 + 1, Math.Min(stem.Length, (int)Math.Ceiling(endSec * sampleRate)));
            int count = s1 - s0;
            if (count <= 0) {
                return;
            }

            double sumSq = 0.0;
            for (int i = s0; i < s1; i++) {
                float m = 0.5f * (stem.Left[i] + stem.Right[i]);
                if (SpatialCortexMath.IsFinite(m)) {
                    sumSq += m * m;
                }
            }
            rms = (float)Math.Sqrt(sumSq / count);

            // Single centered 512-pt FFT for fast centroid & sub-bass ratio
            int center = (s0 + s1) / 2;
            int fftStart = Math.Max(0, Math.Min(stem.Length - FftSize, center - FftSize / 2));
            float[] window = BuildHannWindow(FftSize);
            float[] re = new float[FftSize];
            float[] im = new float[FftSize];
            int limit = Math.Min(FftSize, stem.Length - fftStart);
            for (int i = 0; i < limit; i++) {
                float m = 0.5f * (stem.Left[fftStart + i] + stem.Right[fftStart + i]);
                re[i] = SpatialCortexMath.IsFinite(m) ? m * window[i] : 0f;
            }
            FftInPlace(re, im, FftSize);

            double weightedFreqSum = 0.0;
            double magSum = 0.0;
            double subPow = 0.0;
            double totalPow = 0.0;
            double binHz = sampleRate / (double)FftSize;

            for (int k = 1; k < HalfBins; k++) {
                double freq = k * binHz;
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                double pow = mag * mag;
                weightedFreqSum += freq * mag;
                magSum += mag;
                totalPow += pow;
                if (freq <= 120.0) {
                    subPow += pow;
                }
            }

            if (magSum > 1e-9) {
                centroidHz = (float)(weightedFreqSum / magSum);
            }
            if (totalPow > 1e-12) {
                subBassRatio = SpatialCortexMath.Clamp01((float)(subPow / totalPow));
            }
        }

        /// <summary>
        /// Enforces sorted timestamps and the strict &lt;= <paramref name="maxCuesPerMinute"/> sliding 60-second rate cap.
        /// </summary>
        public static DirectorCue[] EnforceCueRateBudget(
            IReadOnlyList<DirectorCue> cues,
            double durationSec,
            int maxCuesPerMinute = MaxCuesPerMinute) {
            if (cues == null || cues.Count == 0 || maxCuesPerMinute <= 0) {
                return Array.Empty<DirectorCue>();
            }
            List<DirectorCue> sorted = new List<DirectorCue>(cues.Count);
            for (int i = 0; i < cues.Count; i++) {
                sorted.Add(cues[i]);
            }
            sorted.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));

            double safeDur = durationSec > 0.1 ? durationSec : 60.0;
            int totalCap = Math.Max(1, (int)Math.Floor((safeDur / 60.0) * maxCuesPerMinute + 1e-6));
            if (safeDur < 60.0) {
                totalCap = Math.Min(maxCuesPerMinute, Math.Max(1, (int)Math.Ceiling((safeDur / 60.0) * maxCuesPerMinute)));
            }

            List<DirectorCue> accepted = new List<DirectorCue>(Math.Min(sorted.Count, totalCap));
            for (int i = 0; i < sorted.Count && accepted.Count < totalCap; i++) {
                DirectorCue cue = sorted[i];
                int inLast60s = 0;
                for (int j = accepted.Count - 1; j >= 0; j--) {
                    if (cue.TimeSec - accepted[j].TimeSec < 60.0 - 1e-6) {
                        inLast60s++;
                    } else {
                        break;
                    }
                }
                if (inLast60s < maxCuesPerMinute) {
                    accepted.Add(cue);
                }
            }
            return accepted.ToArray();
        }

        static float[] BuildMixMono(IReadOnlyList<StereoStemBuffer> stems, int maxSamples) {
            float[] mix = new float[maxSamples];
            for (int s = 0; s < stems.Count; s++) {
                StereoStemBuffer st = stems[s];
                if (st == null || st.Length <= 0) {
                    continue;
                }
                int len = Math.Min(maxSamples, st.Length);
                for (int i = 0; i < len; i++) {
                    float m = 0.5f * (st.Left[i] + st.Right[i]);
                    if (SpatialCortexMath.IsFinite(m)) {
                        mix[i] += m;
                    }
                }
            }
            return mix;
        }

        static List<double> CollectCandidateTimestamps(
            float[] flux,
            double[] frameTimes,
            double[] sections,
            double durationSec,
            SpatialChoreoMode mode) {
            List<double> times = new List<double>();
            for (int i = 0; i < sections.Length; i++) {
                times.Add(sections[i]);
            }

            int targetBudget = Math.Max(2, (int)Math.Floor((durationSec / 60.0) * MaxCuesPerMinute));
            if (mode == SpatialChoreoMode.Orbit) {
                int steps = Math.Min(targetBudget, Math.Max(2, (int)Math.Ceiling(durationSec / 8.0)));
                double step = durationSec / steps;
                for (int k = 0; k < steps; k++) {
                    double t = k * step + step * 0.25;
                    if (t < durationSec - 0.5 && !HasNearbyTimestamp(times, t, 4.0)) {
                        times.Add(t);
                    }
                }
            }

            if (flux != null && frameTimes != null && flux.Length > 2) {
                double minGap = Math.Max(7.5, durationSec / Math.Max(1, targetBudget));
                // Rank local spectral-flux onset peaks
                List<int> peakIndices = new List<int>();
                for (int f = 1; f < flux.Length - 1; f++) {
                    if (flux[f] > 0.22f && flux[f] >= flux[f - 1] && flux[f] >= flux[f + 1]) {
                        peakIndices.Add(f);
                    }
                }
                peakIndices.Sort((a, b) => flux[b].CompareTo(flux[a]));
                for (int p = 0; p < peakIndices.Count && times.Count < targetBudget; p++) {
                    double t = frameTimes[peakIndices[p]];
                    if (!HasNearbyTimestamp(times, t, minGap)) {
                        times.Add(t);
                    }
                }
            }

            times.Sort();
            return times;
        }

        static bool HasNearbyTimestamp(List<double> existing, double candidateSec, double minGapSec) {
            for (int i = 0; i < existing.Count; i++) {
                if (Math.Abs(existing[i] - candidateSec) < minGapSec) {
                    return true;
                }
            }
            return false;
        }

        static List<int> BuildChoreoStemOrder(IReadOnlyList<StereoStemBuffer> stems) {
            List<int> dynamicStems = new List<int>(stems.Count);
            List<int> anchorStems = new List<int>(stems.Count);
            for (int i = 0; i < stems.Count; i++) {
                if (stems[i] == null || stems[i].Length <= 0) {
                    continue;
                }
                NeuralStemRole role = NeuralStemSpatializer.ClassifyStemName(stems[i].Name);
                if (role == NeuralStemRole.Bass || role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals) {
                    anchorStems.Add(i);
                } else {
                    dynamicStems.Add(i);
                }
            }
            if (dynamicStems.Count > 0) {
                return dynamicStems;
            }
            return anchorStems;
        }

        static int SelectMostProminentStem(
            IReadOnlyList<StereoStemBuffer> stems,
            List<int> stemOrder,
            int sampleRate,
            double tSec,
            double halfWinSec,
            int cueIndex) {
            if (stemOrder.Count == 1) {
                return stemOrder[0];
            }
            int bestIdx = stemOrder[cueIndex % stemOrder.Count];
            float bestScore = -1f;
            for (int i = 0; i < stemOrder.Count; i++) {
                int idx = stemOrder[i];
                StereoStemBuffer st = stems[idx];
                int s0 = Math.Max(0, Math.Min(st.Length - 1, (int)((tSec - halfWinSec) * sampleRate)));
                int s1 = Math.Max(s0 + 1, Math.Min(st.Length, (int)((tSec + halfWinSec) * sampleRate)));
                int step = Math.Max(1, (s1 - s0) / 128);
                double sumSq = 0.0;
                int n = 0;
                for (int k = s0; k < s1; k += step) {
                    float m = 0.5f * (st.Left[k] + st.Right[k]);
                    sumSq += m * m;
                    n++;
                }
                float rms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
                // Slight round-robin tie-breaker so multiple instruments share choreography naturally
                float roundRobinBias = (i == (cueIndex % stemOrder.Count)) ? 1.15f : 1.0f;
                float score = rms * roundRobinBias;
                if (score > bestScore) {
                    bestScore = score;
                    bestIdx = idx;
                }
            }
            return bestIdx;
        }

        static LyricMoodDigest ResolveMoodAtTime(IReadOnlyList<LyricMoodDigest> moodDigests, double tSec, double durationSec) {
            if (moodDigests == null || moodDigests.Count == 0) {
                return new LyricMoodDigest(LyricMoodTag.Calm, 0.5f, 0.5f, 0, 0.0);
            }
            LyricMoodDigest best = moodDigests[0];
            bool anyTimestamps = false;
            for (int i = 0; i < moodDigests.Count; i++) {
                if (moodDigests[i].StartTimeSec > 0.01) {
                    anyTimestamps = true;
                    break;
                }
            }
            if (anyTimestamps) {
                for (int i = 0; i < moodDigests.Count; i++) {
                    if (moodDigests[i].StartTimeSec <= tSec + 0.01) {
                        best = moodDigests[i];
                    }
                }
                return best;
            }
            int idx = (int)Math.Min(moodDigests.Count - 1, Math.Floor((tSec / Math.Max(1.0, durationSec)) * moodDigests.Count));
            return moodDigests[Math.Max(0, idx)];
        }

        static float ResolveStemHomeAzimuthDeg(StereoPanEstimate est, NeuralStemRole role, int stemIdx) {
            if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals) {
                return est.HasSignal ? SpatialCortexMath.Clamp(est.PanAzimuthDeg, -18f, 18f) : 0f;
            }
            if (role == NeuralStemRole.Bass) {
                return est.HasSignal ? SpatialCortexMath.Clamp(est.PanAzimuthDeg, -12f, 12f) : 0f;
            }
            if (est.HasSignal && MathF.Abs(est.PanAzimuthDeg) >= 4f) {
                return est.PanAzimuthDeg;
            }
            float fallbackSide = (stemIdx & 1) == 0 ? 1f : -1f;
            return fallbackSide * GetRoleHomeAzimuthMag(role);
        }

        static float GetRoleHomeAzimuthMag(NeuralStemRole role) {
            switch (role) {
                case NeuralStemRole.Drums:
                    return 28f;
                case NeuralStemRole.Piano:
                    return 44f;
                case NeuralStemRole.BackingVocals:
                    return 52f;
                case NeuralStemRole.Guitar:
                    return 58f;
                case NeuralStemRole.Residual:
                    return 108f;
                default:
                    return 64f;
            }
        }

        static void ComputeTargetPolarForCue(
            NeuralStemRole role,
            SpatialChoreoMode mode,
            LyricMoodDigest mood,
            float homeAzDeg,
            float stereoWidth01,
            float progress01,
            float centroidHz,
            float subBassRatio,
            float energy01,
            out float azDeg,
            out float elDeg,
            out float radius) {
            float side = homeAzDeg >= 0f ? 1f : -1f;
            float homeMag = MathF.Abs(homeAzDeg);
            // Expressive lyrics-mood multipliers: Intimate narrows & draws closer, Anthem widens & lifts, Tense adds kinetic tension
            float moodWidth = mood.Tag == LyricMoodTag.Anthem ? 1.32f
                : (mood.Tag == LyricMoodTag.Intimate ? 0.56f
                : (mood.Tag == LyricMoodTag.Tense ? 1.16f : 0.88f));
            float moodElevBias = mood.Tag == LyricMoodTag.Anthem ? 12f * (0.4f + 0.6f * mood.Arousal01)
                : (mood.Tag == LyricMoodTag.Intimate ? -3f
                : (mood.Tag == LyricMoodTag.Tense ? 7f * mood.Arousal01 : 2f));
            float moodRadiusDelta = mood.Tag == LyricMoodTag.Intimate ? -0.14f
                : (mood.Tag == LyricMoodTag.Anthem ? 0.06f
                : (mood.Tag == LyricMoodTag.Tense ? -0.05f : -0.02f));
            float tenseOsc = mood.Tag == LyricMoodTag.Tense
                ? MathF.Sin(progress01 * MathF.PI * 4f) * (8f + 10f * energy01)
                : 0f;
            float highBrightness = SpatialCortexMath.Clamp01((centroidHz - 500f) / 3500f);
            float widthFactor = 0.78f + 0.35f * SpatialCortexMath.Clamp01(stereoWidth01);

            switch (mode) {
                case SpatialChoreoMode.Subtle:
                    // Gentle expressive breathing anchored directly around the stem's measured stereo pan
                    float subtleDelta = (3f + 11f * energy01) * moodWidth * widthFactor + tenseOsc * 0.35f;
                    azDeg = side * Math.Min(115f, Math.Max(4f, homeMag + subtleDelta));
                    elDeg = 3f + 11f * highBrightness * (1f - subBassRatio) + moodElevBias * 0.45f;
                    radius = 0.90f + moodRadiusDelta * 0.6f + 0.04f * energy01;
                    break;
                case SpatialChoreoMode.Cinematic:
                    // Wide dramatic arc launching from original mix pan, blooming vertically & laterally on anthems
                    float cinematicSweep = homeMag + (14f + 42f * progress01 + 26f * energy01) * moodWidth * widthFactor + tenseOsc;
                    azDeg = SpatialCortexMath.WrapAzimuthDeg(side * Math.Min(164f, Math.Max(6f, cinematicSweep)));
                    elDeg = 8f + 32f * highBrightness * (1f - subBassRatio) + moodElevBias;
                    radius = 0.88f + moodRadiusDelta + 0.10f * energy01;
                    break;
                case SpatialChoreoMode.Orbit:
                    // Continuous monotonic orbital progression anchored at the stem's original stereo mix azimuth
                    azDeg = SpatialCortexMath.WrapAzimuthDeg(homeAzDeg + side * progress01 * 300f);
                    elDeg = 10f + (16f + 8f * mood.Arousal01) * MathF.Sin(progress01 * MathF.PI * 2f) * (1f - subBassRatio) + moodElevBias * 0.5f;
                    radius = 0.88f + moodRadiusDelta * 0.5f;
                    break;
                default: // Musical
                    // Lyrics- and energy-coupled expansion strictly around the stem's measured mix hemisphere
                    float musicalDelta = (5f + 26f * energy01) * moodWidth * widthFactor + tenseOsc * 0.65f;
                    azDeg = side * Math.Min(132f, Math.Max(4f, homeMag + musicalDelta));
                    elDeg = 5f + 22f * highBrightness * (1f - subBassRatio) + moodElevBias * 0.75f;
                    radius = 0.89f + moodRadiusDelta * 0.85f + 0.05f * energy01;
                    break;
            }

            PsychoacousticGovernor.ClampRoleAngles(role, ref azDeg, ref elDeg, ref radius);
        }

        static float CosineFeatureSim(float[] features, int offA, int offB, int bands) {
            float dot = 0f;
            for (int b = 0; b < bands; b++) {
                dot += features[offA + b] * features[offB + b];
            }
            return dot;
        }

        static float[] BuildHannWindow(int size) {
            float[] w = new float[size];
            for (int i = 0; i < size; i++) {
                w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
            }
            return w;
        }

        static int NextPowerOfTwo(int v) {
            int p = 1;
            while (p < v) {
                p <<= 1;
            }
            return p;
        }

        static void FftInPlace(float[] re, float[] im, int n) {
            int j = 0;
            for (int i = 0; i < n; i++) {
                if (i < j) {
                    float tr = re[i];
                    re[i] = re[j];
                    re[j] = tr;
                    float ti = im[i];
                    im[i] = im[j];
                    im[j] = ti;
                }
                int m = n >> 1;
                while (m >= 1 && j >= m) {
                    j -= m;
                    m >>= 1;
                }
                j += m;
            }
            for (int len = 2; len <= n; len <<= 1) {
                double ang = -2.0 * Math.PI / len;
                float wLenRe = (float)Math.Cos(ang);
                float wLenIm = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len) {
                    float wRe = 1f;
                    float wIm = 0f;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++) {
                        int u = i + k;
                        int v = u + half;
                        float tRe = re[v] * wRe - im[v] * wIm;
                        float tIm = re[v] * wIm + im[v] * wRe;
                        re[v] = re[u] - tRe;
                        im[v] = im[u] - tIm;
                        re[u] += tRe;
                        im[u] += tIm;
                        float nextRe = wRe * wLenRe - wIm * wLenIm;
                        wIm = wRe * wLenIm + wIm * wLenRe;
                        wRe = nextRe;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Deterministic rule-based semantic director with optional Plane 1B INT4 HTP hook
    /// (strict DFA cap &lt;= 256 tokens, &lt;= 8 cues/min, zero raw lyric exposure, automatic fallback to Layer-1 forensics).
    /// </summary>
    public sealed class DeterministicRuleDirector : ISpatialSemanticDirector {
        /// <summary>Maximum constrained-JSON token budget for optional Plane 1B INT4 HTP generation.</summary>
        public const int MaxDfaTokenBudget = 256;

        /// <summary>Hard ceiling on cues per minute.</summary>
        public const int MaxCuesPerMinute = Layer1AudioForensics.MaxCuesPerMinute;

        readonly IPlane1BSemanticHook plane1bHook;

        /// <summary>Initializes the deterministic rule director with an optional Plane 1B INT4 HTP semantic hook.</summary>
        public DeterministicRuleDirector(IPlane1BSemanticHook plane1bHook = null) {
            this.plane1bHook = plane1bHook;
        }

        /// <inheritdoc/>
        public string DirectorId {
            get { return plane1bHook != null && plane1bHook.IsWarm ? "plane1b-int4-htp+layer1" : "deterministic-layer1-forensics"; }
        }

        /// <inheritdoc/>
        public bool IsModelReady {
            get { return plane1bHook != null && plane1bHook.IsWarm; }
        }

        /// <inheritdoc/>
        public DirectorCue[] GenerateCues(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode,
            IReadOnlyList<LyricMoodDigest> moodDigests,
            out bool usedFallback) {
            usedFallback = true;
            if (mode == SpatialChoreoMode.Off || stems == null || stems.Count == 0 || sampleRate < 8000) {
                usedFallback = false;
                return Array.Empty<DirectorCue>();
            }

            DirectorCue[] forensicsCues = Layer1AudioForensics.Analyze(stems, sampleRate, mode, moodDigests);
            double durationSec = ComputeMaxDurationSec(stems, sampleRate);

            if (plane1bHook != null && plane1bHook.IsWarm) {
                try {
                    string numericPrompt = BuildSanitizedNumericPrompt(stems, sampleRate, mode, moodDigests, forensicsCues);
                    string json = plane1bHook.InferConstrainedJson(numericPrompt, MaxDfaTokenBudget);
                    DirectorCue[] parsed;
                    if (TryParseConstrainedCueJson(json, durationSec, out parsed) && parsed.Length > 0) {
                        usedFallback = false;
                        return parsed;
                    }
                } catch {
                    // Automatic deterministic fallback on cold/invalid Plane 1B output
                }
            }

            usedFallback = true;
            return forensicsCues;
        }

        /// <summary>
        /// Builds a strictly numeric/enum summary prompt (never includes raw lyric strings).
        /// </summary>
        internal static string BuildSanitizedNumericPrompt(
            IReadOnlyList<StereoStemBuffer> stems,
            int sampleRate,
            SpatialChoreoMode mode,
            IReadOnlyList<LyricMoodDigest> moodDigests,
            DirectorCue[] forensicsCues) {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"mode\":").Append((int)mode)
              .Append(",\"sr\":").Append(sampleRate)
              .Append(",\"stems\":[");
            for (int i = 0; i < stems.Count; i++) {
                if (i > 0) {
                    sb.Append(',');
                }
                NeuralStemRole role = stems[i] != null ? NeuralStemSpatializer.ClassifyStemName(stems[i].Name) : NeuralStemRole.Other;
                sb.Append((int)role);
            }
            sb.Append("],\"moods\":[");
            if (moodDigests != null) {
                int cap = Math.Min(16, moodDigests.Count);
                for (int i = 0; i < cap; i++) {
                    if (i > 0) {
                        sb.Append(',');
                    }
                    LyricMoodDigest m = moodDigests[i];
                    sb.Append('[').Append((int)m.Tag).Append(',')
                      .Append(m.Arousal01.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                      .Append(m.Valence01.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                      .Append(m.SectionIndex).Append(']');
                }
            }
            sb.Append("],\"seed_cues\":").Append(forensicsCues != null ? forensicsCues.Length : 0).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Strict bounded DFA-style parser for constrained JSON cue arrays (<c>[{"t":...,"r":...,"az":...,"el":...,"rad":...,"e":...}]</c>).
        /// Enforces &lt;= 8 cues/min and finite numeric bounds.
        /// </summary>
        public static bool TryParseConstrainedCueJson(string json, double durationSec, out DirectorCue[] cues) {
            cues = Array.Empty<DirectorCue>();
            if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) {
                return false;
            }
            // Reject unexpected control or injection payload size
            List<DirectorCue> parsed = new List<DirectorCue>();
            int idx = 0;
            int len = json.Length;

            while (idx < len) {
                int objStart = json.IndexOf('{', idx);
                if (objStart < 0) {
                    break;
                }
                int objEnd = json.IndexOf('}', objStart + 1);
                if (objEnd < 0) {
                    return false;
                }
                string slice = json.Substring(objStart + 1, objEnd - objStart - 1);
                double t = 0.0;
                int roleInt = (int)NeuralStemRole.Other;
                float az = 0f;
                float el = 0f;
                float rad = 0.9f;
                float e = 0.5f;
                bool hasT = false;
                bool hasAz = false;

                string[] pairs = slice.Split(',');
                for (int p = 0; p < pairs.Length; p++) {
                    int colon = pairs[p].IndexOf(':');
                    if (colon <= 0) {
                        continue;
                    }
                    string k = pairs[p].Substring(0, colon).Trim().Trim('"');
                    string v = pairs[p].Substring(colon + 1).Trim().Trim('"');
                    if (k == "t" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double td) && td >= 0.0 && !double.IsNaN(td) && !double.IsInfinity(td)) {
                        t = td;
                        hasT = true;
                    } else if (k == "r" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ri) && Enum.IsDefined(typeof(NeuralStemRole), ri)) {
                        roleInt = ri;
                    } else if (k == "az" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float azf) && SpatialCortexMath.IsFinite(azf)) {
                        az = azf;
                        hasAz = true;
                    } else if (k == "el" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float elf) && SpatialCortexMath.IsFinite(elf)) {
                        el = elf;
                    } else if (k == "rad" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float radf) && SpatialCortexMath.IsFinite(radf)) {
                        rad = radf;
                    } else if (k == "e" && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float ef) && SpatialCortexMath.IsFinite(ef)) {
                        e = ef;
                    }
                }

                if (hasT && hasAz) {
                    parsed.Add(new DirectorCue(t, (NeuralStemRole)roleInt, az, el, rad, e));
                }
                idx = objEnd + 1;
            }

            if (parsed.Count == 0) {
                return false;
            }
            cues = Layer1AudioForensics.EnforceCueRateBudget(parsed, durationSec, MaxCuesPerMinute);
            return cues.Length > 0;
        }

        static double ComputeMaxDurationSec(IReadOnlyList<StereoStemBuffer> stems, int sampleRate) {
            int maxSamples = 0;
            for (int i = 0; i < stems.Count; i++) {
                if (stems[i] != null && stems[i].Length > maxSamples) {
                    maxSamples = stems[i].Length;
                }
            }
            return sampleRate > 0 ? maxSamples / (double)sampleRate : 0.0;
        }
    }

    /// <summary>
    /// Precomputed C2-continuous spherical ring lookup table (ring-LUT) delivering zero-allocation,
    /// sub-0.10 ms/block 3D unit-SLERP shortest-arc object choreography.
    /// </summary>
    public sealed partial class SpatialChoreoTrack {
        readonly NeuralStemRole[] stemRoles;
        readonly string[] stemNames;
        readonly DirectorCue[] directorCues;
        readonly Vector4[] lut; // (unitX, unitY, unitZ, radius) indexed by [stemIdx * frameCount + frameIdx]

        /// <summary>Active choreography mode used to build this track.</summary>
        public SpatialChoreoMode Mode { get; }

        /// <summary>Uniform LUT sampling step in seconds.</summary>
        public double StepSec { get; }

        /// <summary>Total track duration in seconds.</summary>
        public double DurationSec { get; }

        /// <summary>Number of stems represented in this LUT.</summary>
        public int StemCount { get; }

        /// <summary>Number of time samples per stem in the ring-LUT.</summary>
        public int FrameCount { get; }

        /// <summary>Semantic stem role per stem index.</summary>
        public IReadOnlyList<NeuralStemRole> StemRoles {
            get { return stemRoles; }
        }

        /// <summary>Original stem names per stem index.</summary>
        public IReadOnlyList<string> StemNames {
            get { return stemNames; }
        }

        /// <summary>Governed director cues used to synthesize this LUT.</summary>
        public IReadOnlyList<DirectorCue> DirectorCues {
            get { return directorCues; }
        }

        SpatialChoreoTrack(
            SpatialChoreoMode mode,
            double stepSec,
            double durationSec,
            NeuralStemRole[] stemRoles,
            string[] stemNames,
            DirectorCue[] directorCues,
            Vector4[] lut,
            int frameCount,
            bool[] stemAudibleFlags = null) {
            Mode = mode;
            StepSec = stepSec;
            DurationSec = durationSec;
            this.stemRoles = stemRoles;
            this.stemNames = stemNames;
            this.directorCues = directorCues;
            this.lut = lut;
            this.stemAudibleFlags = stemAudibleFlags;
            StemCount = stemRoles.Length;
            FrameCount = frameCount;
        }

        /// <summary>
        /// Builds a governed, C2-smoothed, ERB-repelled ring-LUT choreography track from stem names and cues.
        /// </summary>
        public static SpatialChoreoTrack Build(
            IReadOnlyList<string> stemNames,
            DirectorCue[] cues,
            SpatialChoreoMode mode,
            double durationSec,
            double stepSec = 0.05) {
            return Build(stemNames, cues, mode, durationSec, stepSec, stemHomeAzimuthsDeg: null);
        }

        /// <summary>
        /// Builds a governed, C2-smoothed, ERB-repelled ring-LUT choreography track from stem names, cues,
        /// and optional measured per-stem stereo pan azimuths (degrees) so objects anchor and orbit relative
        /// to their original stereo mix placement.
        /// </summary>
        public static SpatialChoreoTrack Build(
            IReadOnlyList<string> stemNames,
            DirectorCue[] cues,
            SpatialChoreoMode mode,
            double durationSec,
            double stepSec,
            float[] stemHomeAzimuthsDeg) {
            return BuildWithStemActivity(stemNames, cues, mode, durationSec, stepSec, stemHomeAzimuthsDeg, stemHasSignal: null);
        }

        /// <summary>
        /// Zero-allocation audio-thread position evaluation using 3D unit-SLERP shortest-arc interpolation.
        /// Guaranteed 0 B heap allocation and &lt; 0.10 ms per block.
        /// </summary>
        public bool EvaluatePositionZeroAlloc(int stemIdx, double timeSec, out Vector3 pos) {
            return EvaluatePositionZeroAlloc(stemIdx, timeSec, out pos, loop: false);
        }

        /// <summary>
        /// Zero-allocation audio-thread position evaluation using 3D unit-SLERP shortest-arc interpolation
        /// with optional ring-LUT wraparound for looped playback.
        /// </summary>
        public bool EvaluatePositionZeroAlloc(int stemIdx, double timeSec, out Vector3 pos, bool loop) {
            if (stemIdx < 0 || stemIdx >= StemCount || FrameCount <= 0 || lut.Length < (stemIdx + 1) * FrameCount) {
                pos = new Vector3(0f, 0f, 1f);
                return false;
            }

            int baseIdx = stemIdx * FrameCount;
            double t = double.IsNaN(timeSec) || double.IsInfinity(timeSec) ? 0.0 : timeSec;
            if (FrameCount == 1 || Mode == SpatialChoreoMode.Off) {
                Vector4 vOnly = lut[baseIdx];
                pos = new Vector3(vOnly.X, vOnly.Y, vOnly.Z) * vOnly.W;
                return true;
            }

            double samplePos = t / StepSec;
            int i0;
            int i1;
            float frac;

            if (loop) {
                double span = FrameCount;
                samplePos = samplePos % span;
                if (samplePos < 0.0) {
                    samplePos += span;
                }
                i0 = (int)Math.Floor(samplePos);
                if (i0 >= FrameCount) {
                    i0 = 0;
                }
                i1 = i0 + 1;
                if (i1 >= FrameCount) {
                    i1 = 0;
                }
                frac = (float)(samplePos - i0);
            } else {
                if (samplePos <= 0.0) {
                    Vector4 vStart = lut[baseIdx];
                    pos = new Vector3(vStart.X, vStart.Y, vStart.Z) * vStart.W;
                    return true;
                }
                int last = FrameCount - 1;
                if (samplePos >= last) {
                    Vector4 vEnd = lut[baseIdx + last];
                    pos = new Vector3(vEnd.X, vEnd.Y, vEnd.Z) * vEnd.W;
                    return true;
                }
                i0 = (int)Math.Floor(samplePos);
                i1 = i0 + 1;
                frac = (float)(samplePos - i0);
            }

            Vector4 v0 = lut[baseIdx + i0];
            Vector4 v1 = lut[baseIdx + i1];
            Vector3 u0 = new Vector3(v0.X, v0.Y, v0.Z);
            Vector3 u1 = new Vector3(v1.X, v1.Y, v1.Z);
            Vector3 dir = SpatialCortexMath.SlerpUnitShortest(u0, u1, frac);
            float radius = v0.W + (v1.W - v0.W) * frac;
            pos = dir * radius;
            return true;
        }

        /// <summary>
        /// Resolves a stem index by semantic role and optional stem name (setup-time helper, not for hot loop).
        /// </summary>
        public int FindStemIndex(NeuralStemRole role, string stemName = null) {
            if (!string.IsNullOrWhiteSpace(stemName)) {
                for (int i = 0; i < stemNames.Length; i++) {
                    if (string.Equals(stemNames[i], stemName, StringComparison.OrdinalIgnoreCase)) {
                        return i;
                    }
                }
            }
            for (int i = 0; i < stemRoles.Length; i++) {
                if (stemRoles[i] == role) {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Returns the raw precomputed LUT array length (<c>StemCount * FrameCount</c>).</summary>
        public int LutEntryCount {
            get { return lut.Length; }
        }

        /// <summary>Copies the internal <see cref="Vector4"/> LUT entries into <paramref name="destination"/> for persistence.</summary>
        public int CopyLutTo(Vector4[] destination) {
            if (destination == null || destination.Length < lut.Length) {
                return 0;
            }
            Array.Copy(lut, destination, lut.Length);
            return lut.Length;
        }

        /// <summary>Reconstructs a precomputed <see cref="SpatialChoreoTrack"/> from serialized CNTC v2 cache data.</summary>
        public static SpatialChoreoTrack FromSerialized(
            SpatialChoreoMode mode,
            double stepSec,
            double durationSec,
            NeuralStemRole[] stemRoles,
            string[] stemNames,
            DirectorCue[] cues,
            Vector4[] lut,
            int frameCount) {
            if (stemRoles == null || stemNames == null || lut == null || frameCount <= 0 ||
                stemRoles.Length != stemNames.Length || lut.Length != stemRoles.Length * frameCount) {
                return null;
            }
            return new SpatialChoreoTrack(
                mode,
                SpatialCortexMath.ClampDouble(stepSec, 0.01, 1.0),
                SpatialCortexMath.ClampDouble(durationSec, 0.0, 14400.0),
                stemRoles,
                stemNames,
                cues ?? Array.Empty<DirectorCue>(),
                lut,
                frameCount);
        }

        static Vector4 GetDefaultStemAnchor(NeuralStemRole role, int stemIdx, int totalStems, float measuredHomeAzDeg = float.NaN) {
            float az;
            float el;
            float rad = 0.92f;
            float spreadSide = (stemIdx & 1) == 0 ? 1f : -1f;
            switch (role) {
                case NeuralStemRole.LeadVocal:
                case NeuralStemRole.Vocals:
                    az = 0f;
                    el = 4.5f;
                    rad = 0.98f;
                    break;
                case NeuralStemRole.Bass:
                    az = 0f;
                    el = 0f;
                    rad = 0.95f;
                    break;
                case NeuralStemRole.Drums:
                    az = 0f;
                    el = 2f;
                    rad = 0.95f;
                    break;
                case NeuralStemRole.BackingVocals:
                    az = 0f;
                    el = 18f;
                    rad = 0.92f;
                    break;
                case NeuralStemRole.Guitar:
                    az = spreadSide * 56f;
                    el = 4f;
                    rad = 0.94f;
                    break;
                case NeuralStemRole.Piano:
                    az = spreadSide * 44f;
                    el = 12f;
                    rad = 0.94f;
                    break;
                case NeuralStemRole.Residual:
                    az = spreadSide * 112f;
                    el = 26f;
                    rad = 0.96f;
                    break;
                default:
                    float slot = totalStems > 1 ? (stemIdx / (float)(totalStems - 1)) * 2f - 1f : 0.6f;
                    az = slot * 72f;
                    el = 15f;
                    rad = 0.90f;
                    break;
            }
            if (SpatialCortexMath.IsFinite(measuredHomeAzDeg)) {
                // Anchor directly on measured stereo mix azimuth (preserve symmetric center for BackingVocals/Drums unless strongly panned)
                float minPanThreshold = (role == NeuralStemRole.BackingVocals || role == NeuralStemRole.Drums) ? 8f : 3f;
                if (role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass || MathF.Abs(measuredHomeAzDeg) >= minPanThreshold) {
                    az = measuredHomeAzDeg;
                }
            }
            PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
            Vector3 unit = PsychoacousticGovernor.SphericalToCartesianUnit(az, el);
            return new Vector4(unit.X, unit.Y, unit.Z, rad);
        }

        static List<DirectorCue> CollectStemCues(
            List<DirectorCue> allCues,
            NeuralStemRole role,
            Vector4 anchor,
            double durationSec,
            SpatialChoreoMode mode,
            float orbitPhaseOffsetDeg = 0f,
            bool hasMeasuredHomeAz = false) {
            float anchorAz;
            float anchorEl;
            PsychoacousticGovernor.CartesianToSphericalDeg(new Vector3(anchor.X, anchor.Y, anchor.Z), out anchorAz, out anchorEl);
            bool isAnchoredRole = role == NeuralStemRole.LeadVocal || role == NeuralStemRole.Vocals || role == NeuralStemRole.Bass;

            List<DirectorCue> list = new List<DirectorCue>();

            if (mode == SpatialChoreoMode.Orbit && !isAnchoredRole && durationSec > 0.5) {
                // Continuous monotonic perimeter orbit launching from measured mix pan (or evenly phase-spaced fallback)
                float startAz = hasMeasuredHomeAz && MathF.Abs(anchorAz) >= 3f
                    ? anchorAz
                    : SpatialCortexMath.WrapAzimuthDeg(35f + orbitPhaseOffsetDeg);
                float orbitDir = anchorAz < -3f ? -1f : 1f;
                float degPerSec = 22f; // smooth <= 22 deg/s well inside 60 deg/s slew limit
                float totalSweepDeg = (float)Math.Min(360.0 * Math.Max(1.0, Math.Floor(durationSec / 16.0) + 1.0), degPerSec * durationSec);
                int steps = Math.Max(12, (int)Math.Ceiling(totalSweepDeg / 30f));
                for (int i = 0; i <= steps; i++) {
                    float frac = i / (float)steps;
                    double t = durationSec * frac;
                    float az = SpatialCortexMath.WrapAzimuthDeg(startAz + orbitDir * totalSweepDeg * frac);
                    float frontClearanceEl = 18f * MathF.Max(0f, MathF.Cos(az * SpatialCortexMath.DegToRad));
                    float el = anchorEl + frontClearanceEl + 8f * MathF.Sin(frac * MathF.PI * 2f);
                    float rad = anchor.W;
                    PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                    list.Add(new DirectorCue(t, role, az, el, rad, 0.65f));
                }
                return list;
            }

            list.Add(new DirectorCue(0.0, role, anchorAz, anchorEl, anchor.W, 0.5f));

            if (mode != SpatialChoreoMode.Off) {
                float homeSide = anchorAz >= 0f ? 1f : -1f;
                for (int i = 0; i < allCues.Count; i++) {
                    DirectorCue c = allCues[i];
                    if (c.StemRole == role) {
                        float az = c.TargetAzDeg;
                        float el = c.TargetElDeg;
                        float rad = c.Radius;
                        // In Subtle & Musical modes, keep dynamic cues inside the stem's natural hemisphere
                        // so cues never pull the stem back-and-forth across 0 deg front center.
                        if (!isAnchoredRole && (mode == SpatialChoreoMode.Subtle || mode == SpatialChoreoMode.Musical)) {
                            if (MathF.Abs(az) < 12f || (az * homeSide) < 0f) {
                                az = homeSide * MathF.Max(MathF.Abs(anchorAz), MathF.Abs(az));
                            }
                        }
                        PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                        AppendWithArcSubdivision(list, role, c.TimeSec, az, el, rad, c.Energy01, homeSide);
                    } else if (!isAnchoredRole &&
                               (c.StemRole == NeuralStemRole.Other || c.StemRole == NeuralStemRole.Residual)) {
                        // Gentle sympathetic motion strictly on the stem's own side (zero center-crossing pull)
                        float cueMag = MathF.Abs(c.TargetAzDeg);
                        float anchorMag = MathF.Abs(anchorAz);
                        float coupledAz = homeSide * (anchorMag + (cueMag - anchorMag) * 0.25f);
                        float coupledEl = anchorEl + (c.TargetElDeg - anchorEl) * 0.25f;
                        float coupledRad = anchor.W + (c.Radius - anchor.W) * 0.35f;
                        PsychoacousticGovernor.ClampRoleAngles(role, ref coupledAz, ref coupledEl, ref coupledRad);
                        AppendWithArcSubdivision(list, role, c.TimeSec, coupledAz, coupledEl, coupledRad, c.Energy01 * 0.5f, homeSide);
                    }
                }
            }

            if (list.Count == 1 || list[list.Count - 1].TimeSec < durationSec - 0.1) {
                // Hold the settled end orientation instead of yanking the object back toward center at track end
                DirectorCue last = list[list.Count - 1];
                list.Add(new DirectorCue(durationSec, role, last.TargetAzDeg, last.TargetElDeg, last.Radius, 0.4f));
            }
            return list;
        }
    }
}
