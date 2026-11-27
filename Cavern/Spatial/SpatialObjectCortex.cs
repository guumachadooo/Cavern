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
            for (int i = 0; i < stems.Count; i++) {
                StereoPanEstimate est = Estimate(stems[i], stageHalfWidthDeg);
                result[i] = est.HasSignal ? est.PanAzimuthDeg : float.NaN;
            }
            return result;
        }
    }

    /// <summary>
    /// Deterministic 0 MB Layer-1 audio forensics engine: extracts spectral-flux beat/onset peaks,
    /// Self-Similarity Matrix (SSM) structural section boundaries, and per-stem RMS/spectral centroid
    /// features to produce bounded musical choreography cues (&lt;= 8 cues/min).
    /// </summary>
    public static class Layer1AudioForensics {
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

            return EnforceCueRateBudget(rawCues, durationSec, MaxCuesPerMinute);
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

    /// <summary>
    /// Precomputed C2-continuous spherical ring lookup table (ring-LUT) delivering zero-allocation,
    /// sub-0.10 ms/block 3D unit-SLERP shortest-arc object choreography.
    /// </summary>
    public sealed class SpatialChoreoTrack {
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
            int frameCount) {
            Mode = mode;
            StepSec = stepSec;
            DurationSec = durationSec;
            this.stemRoles = stemRoles;
            this.stemNames = stemNames;
            this.directorCues = directorCues;
            this.lut = lut;
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
            if (stemNames == null || stemNames.Count == 0) {
                return new SpatialChoreoTrack(
                    mode,
                    0.05,
                    0.0,
                    Array.Empty<NeuralStemRole>(),
                    Array.Empty<string>(),
                    Array.Empty<DirectorCue>(),
                    Array.Empty<Vector4>(),
                    0);
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

            // Step 1: Sample keyframe curves & apply C2 cubic B-spline interpolation per stem
            int orbitingTotal = 0;
            for (int s = 0; s < stemCount; s++) {
                NeuralStemRole r = roles[s];
                if (r != NeuralStemRole.LeadVocal && r != NeuralStemRole.Vocals && r != NeuralStemRole.Bass) {
                    orbitingTotal++;
                }
            }
            int orbitingSlot = 0;
            for (int s = 0; s < stemCount; s++) {
                NeuralStemRole r = roles[s];
                bool isAnchored = r == NeuralStemRole.LeadVocal || r == NeuralStemRole.Vocals || r == NeuralStemRole.Bass;
                float orbitPhaseOffsetDeg = (!isAnchored && orbitingTotal > 0)
                    ? (orbitingSlot++ * (360f / orbitingTotal))
                    : 0f;
                List<DirectorCue> stemCues = CollectStemCues(sortedCues, r, baseAnchors[s], safeDur, mode, orbitPhaseOffsetDeg, hasMeasuredHome[s]);
                SampleStemC2Curve(stemCues, r, lut, s * frameCount, frameCount, safeStep);
            }

            // Step 2: Frame-wise 12x6 ERB repulsion across concurrent stems
            // (Skip in Orbit mode so phase-spaced perimeter sweeps never bounce back at az=0 deg, and skip in Off mode
            // so stationary objects preserve their exact measured stereo mix pan azimuth without artificial outward push.)
            if (mode != SpatialChoreoMode.Orbit && mode != SpatialChoreoMode.Off) {
                Vector3[] frameDirs = new Vector3[stemCount];
                float[] frameEnergies = new float[stemCount];
                for (int f = 0; f < frameCount; f++) {
                    for (int s = 0; s < stemCount; s++) {
                        Vector4 v = lut[s * frameCount + f];
                        frameDirs[s] = new Vector3(v.X, v.Y, v.Z);
                        frameEnergies[s] = v.W;
                    }
                    PsychoacousticGovernor.ApplyErb12x6Repulsion(frameDirs, roles, frameEnergies, stemCount);
                    for (int s = 0; s < stemCount; s++) {
                        int off = s * frameCount + f;
                        float rad = lut[off].W;
                        lut[off] = new Vector4(frameDirs[s].X, frameDirs[s].Y, frameDirs[s].Z, rad);
                    }
                }
            }

            // Step 3: Enforce <= 60 deg/s slew cap and final role angle clamps on the precomputed C2 LUT
            // (0 deg LUT deadband avoids 2.0 deg staircase stalls at 20 Hz while preserving smooth motion)
            for (int s = 0; s < stemCount; s++) {
                int baseIdx = s * frameCount;
                NeuralStemRole role = roles[s];
                for (int f = 1; f < frameCount; f++) {
                    Vector4 prev = lut[baseIdx + f - 1];
                    Vector4 curr = lut[baseIdx + f];
                    Vector3 prevDir = new Vector3(prev.X, prev.Y, prev.Z);
                    Vector3 currDir = new Vector3(curr.X, curr.Y, curr.Z);
                    Vector3 governed = PsychoacousticGovernor.ApplyAngularHysteresisAndSlew(prevDir, currDir, safeStep, hysteresisDeg: 0f);
                    float az;
                    float el;
                    float rad = curr.W;
                    PsychoacousticGovernor.CartesianToSphericalDeg(governed, out az, out el);
                    PsychoacousticGovernor.ClampRoleAngles(role, ref az, ref el, ref rad);
                    Vector3 finalDir = PsychoacousticGovernor.SphericalToCartesianUnit(az, el);
                    lut[baseIdx + f] = new Vector4(finalDir.X, finalDir.Y, finalDir.Z, rad);
                }
            }

            return new SpatialChoreoTrack(mode, safeStep, safeDur, roles, names, sortedCues.ToArray(), lut, frameCount);
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
            // If span exceeds 55 deg, insert intermediate perimeter waypoints so SLERP tracks the outer arc cleanly
            float absDelta = MathF.Abs(deltaAz);
            if (absDelta > 55f) {
                // Prefer sweeping around the outer side rather than cutting across 0 deg front center
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

            // Piecewise spherical interpolation followed by C2 uniform cubic B-spline smoothing
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

            // C2 cubic B-spline kernel [1/6, 4/6, 1/6] on normalized room vectors (2 passes -> C2 smooth tangent & curvature)
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
}
