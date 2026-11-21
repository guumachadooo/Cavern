using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Cavern.Filters;
using Cavern.Spatial;
using Cavern.Utilities;

namespace Cavern.Remapping {
    /// <summary>
    /// Creates height information for ground sources using DirAC/COMPASS parametric STFT diffuse extraction
    /// (with unity energy conservation and orthogonal decorrelation) or the legacy <see cref="Cavernize"/> filter.
    /// </summary>
    public class CavernizeUpmixer : Upmixer, ISoundfieldTelemetryProvider, IDisposable {
        /// <summary>
        /// Height separation effect strength.
        /// </summary>
        public float Effect { get; set; } = .75f;

        /// <summary>
        /// Smoothness of object movements, [0;1].
        /// </summary>
        public float Smoothness { get; set; } = .8f;

        /// <summary>
        /// Keep the center channel from gaining height.
        /// </summary>
        public bool CenterStays = true;

        /// <summary>
        /// Use DirAC/COMPASS parametric STFT diffuse height extraction with orthogonal decorrelation (default: true).
        /// Set to false to use the legacy single-channel <see cref="Cavernize"/> filter.
        /// </summary>
        public bool UseParametricStft { get; set; } = true;

        /// <summary>
        /// Latest energy-weighted DirAC/COMPASS soundfield telemetry computed by the front-most ground pair.
        /// </summary>
        public SoundfieldTelemetry LatestTelemetry { get; private set; } = SoundfieldTelemetry.Empty();

        /// <summary>
        /// Legacy height separation filters for each channel (also used as fallback for unpaired non-center channels).
        /// </summary>
        readonly Cavernize[] filters;

        /// <summary>
        /// Output slot index of the elevated height counterpart for each input channel (-1 for LFE).
        /// </summary>
        readonly int[] heightOutputIndices;

        /// <summary>
        /// Symmetric left/right ground pairs processed jointly via parametric STFT diffuse extraction.
        /// </summary>
        readonly (int leftIndex, int rightIndex, ParametricStftAnalyzer analyzer)[] stftPairs;

        /// <summary>
        /// True when an input channel index belongs to one of <see cref="stftPairs"/>.
        /// </summary>
        readonly bool[] isInStftPair;

        /// <summary>
        /// Delay lines aligning pass-through LFE, center, and unpaired channels to STFT latency when parametric mode is active.
        /// </summary>
        readonly FixedSampleDelayLine[] alignDelays;

        /// <summary>
        /// Crossover frequency (Hz) below which all bass energy remains anchored on the ground layer.
        /// </summary>
        readonly float crossoverFrequency;

        /// <summary>
        /// Creates height information for ground sources. The default crossover frequency of 250 Hz will be used.
        /// </summary>
        /// <param name="sources">Mono sources to upconvert, not attached to any <see cref="Listener"/></param>
        /// <param name="sampleRate">Content sample rate</param>
        /// <remarks>Positions of sources are taken on creation and will stay there, regardless of their further movement.</remarks>
        public CavernizeUpmixer(IReadOnlyList<Source> sources, int sampleRate) : this(sources, sampleRate, 250) { }

        /// <summary>
        /// Creates height information for ground sources.
        /// </summary>
        /// <param name="sources">Mono sources to upconvert, not attached to any <see cref="Listener"/></param>
        /// <param name="sampleRate">Content sample rate</param>
        /// <param name="crossoverFrequency">Keep sounds below this frequency on the ground layer</param>
        /// <remarks>Positions of sources are taken on creation and will stay there, regardless of their further movement.</remarks>
        public CavernizeUpmixer(IReadOnlyList<Source> sources, int sampleRate, int crossoverFrequency) :
            base(sources.Sum(x => x.LFE ? 1 : 2), sampleRate) {
            this.crossoverFrequency = crossoverFrequency > 80 ? crossoverFrequency : 250f;
            int sourceCount = sources.Count;
            filters = new Cavernize[sourceCount];
            heightOutputIndices = new int[sourceCount];
            isInStftPair = new bool[sourceCount];
            alignDelays = new FixedSampleDelayLine[sourceCount];

            int heightSlot = sourceCount;
            for (int i = 0; i < sourceCount; i++) {
                IntermediateSources[i].Position = sources[i].Position;
                alignDelays[i] = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
                if (sources[i].LFE) {
                    IntermediateSources[i].LFE = true;
                    heightOutputIndices[i] = -1;
                } else {
                    filters[i] = new Cavernize(sampleRate, crossoverFrequency);
                    heightOutputIndices[i] = heightSlot++;
                }
            }

            // Discover symmetric L/R ground channel pairs (e.g. FL/FR, SL/SR, RL/RR) for joint DirAC/COMPASS analysis
            List<(int, int, ParametricStftAnalyzer)> pairsList = new List<(int, int, ParametricStftAnalyzer)>();
            for (int i = 0; i < sourceCount; i++) {
                if (isInStftPair[i] || sources[i].LFE) {
                    continue;
                }
                Vector3 posA = sources[i].Position;
                if (MathF.Abs(posA.X) < 0.05f) {
                    continue; // Center/midline channel stays unpaired
                }
                int bestMatch = -1;
                float bestDist = float.MaxValue;
                for (int j = i + 1; j < sourceCount; j++) {
                    if (isInStftPair[j] || sources[j].LFE) {
                        continue;
                    }
                    Vector3 posB = sources[j].Position;
                    if (MathF.Sign(posA.X) == MathF.Sign(posB.X)) {
                        continue;
                    }
                    float dx = MathF.Abs(posA.X + posB.X);
                    float dy = MathF.Abs(posA.Y - posB.Y);
                    float dz = MathF.Abs(posA.Z - posB.Z);
                    float dist = dx + dy + dz;
                    if (dx < 0.25f && dy < 0.25f && dz < 0.25f && dist < bestDist) {
                        bestDist = dist;
                        bestMatch = j;
                    }
                }
                if (bestMatch >= 0) {
                    int leftIdx = posA.X < 0f ? i : bestMatch;
                    int rightIdx = posA.X < 0f ? bestMatch : i;
                    isInStftPair[leftIdx] = true;
                    isInStftPair[rightIdx] = true;
                    pairsList.Add((leftIdx, rightIdx, new ParametricStftAnalyzer(sampleRate)));
                }
            }

            // Place front-most pair first so LatestTelemetry reflects the primary soundstage
            pairsList.Sort((a, b) => sources[b.Item1].Position.Z.CompareTo(sources[a.Item1].Position.Z));
            stftPairs = pairsList.ToArray();

            SetupCollection(sources, sampleRate);
        }

        /// <summary>
        /// Clears internal STFT overlap-add buffers, decorrelators, legacy filters, and alignment delay lines.
        /// </summary>
        public void Reset() {
            for (int i = 0; i < stftPairs.Length; i++) {
                stftPairs[i].analyzer.Reset();
            }
            for (int i = 0; i < filters.Length; i++) {
                if (filters[i] != null) {
                    filters[i].Reset();
                }
            }
            for (int i = 0; i < alignDelays.Length; i++) {
                alignDelays[i].Reset();
            }
            LatestTelemetry = SoundfieldTelemetry.Empty();
        }

        /// <summary>
        /// Get the input samples, place the upmixed targets in space, and return their samples.
        /// </summary>
        protected override float[][] UpdateSources(int samplesPerSource) {
            if (UseParametricStft && stftPairs.Length > 0) {
                return UpdateSourcesParametric(samplesPerSource);
            }
            return UpdateSourcesLegacy(samplesPerSource);
        }

        float[][] UpdateSourcesParametric(int samplesPerSource) {
            float[][] input = OnSamplesNeeded(samplesPerSource);
            float envY = Listener.EnvironmentSize.Y;

            // 1. Process symmetric L/R ground pairs with DirAC/COMPASS diffuse height extraction + orthogonal decorrelation
            for (int p = 0; p < stftPairs.Length; p++) {
                int lIdx = stftPairs[p].leftIndex;
                int rIdx = stftPairs[p].rightIndex;
                int hLIdx = heightOutputIndices[lIdx];
                int hRIdx = heightOutputIndices[rIdx];
                stftPairs[p].analyzer.ProcessGroundToHeightPair(
                    input[lIdx],
                    input[rIdx],
                    output[lIdx],
                    output[rIdx],
                    output[hLIdx],
                    output[hRIdx],
                    samplesPerSource,
                    Effect,
                    Smoothness,
                    crossoverFrequency,
                    out float elevation);

                float liftY = elevation * envY;
                IntermediateSources[hLIdx].Position = IntermediateSources[lIdx].Position + new Vector3(0f, liftY, 0f);
                IntermediateSources[hRIdx].Position = IntermediateSources[rIdx].Position + new Vector3(0f, liftY, 0f);

                if (p == 0) {
                    LatestTelemetry = stftPairs[p].analyzer.LatestTelemetry;
                }
            }

            // 2. Keep LFE, Center (CenterStays), and any unpaired channels delay-aligned to exact 512-sample STFT latency
            bool centerToStay = CenterStays;
            for (int i = 0; i < input.Length; i++) {
                if (isInStftPair[i]) {
                    continue;
                }
                if (IntermediateSources[i].LFE) {
                    alignDelays[i].Process(input[i], output[i], samplesPerSource);
                    continue;
                }
                int hIdx = heightOutputIndices[i];
                Vector3 position = IntermediateSources[i].Position;
                bool isCenter = centerToStay && MathF.Abs(position.X) < 0.01f && MathF.Abs(position.Y) < 0.01f && position.Z > 0f;
                if (isCenter) {
                    centerToStay = false;
                    alignDelays[i].Process(input[i], output[i], samplesPerSource);
                    Array.Clear(output[hIdx], 0, samplesPerSource);
                    IntermediateSources[hIdx].Position = position;
                } else {
                    alignDelays[i].Process(input[i], output[i], samplesPerSource);
                    filters[i].Effect = Effect;
                    filters[i].CalculateSmoothingFactor(samplesPerSource, Smoothness);
                    filters[i].Process(output[i]);
                    filters[i].GroundLevel.CopyTo(output[i]);
                    filters[i].HeightLevel.CopyTo(output[hIdx]);
                    IntermediateSources[hIdx].Position = position + new Vector3(0f, filters[i].Height * envY, 0f);
                }
            }

            return output;
        }

        float[][] UpdateSourcesLegacy(int samplesPerSource) {
            int firstFilter = -1;
            for (int i = 0; i < filters.Length; i++) {
                if (filters[i] != null) {
                    firstFilter = i;
                    break;
                }
            }
            if (firstFilter >= 0) {
                filters[firstFilter].Effect = Effect;
                filters[firstFilter].CalculateSmoothingFactor(samplesPerSource, Smoothness);
            }

            bool centerToStay = CenterStays;
            for (int i = 0; i < filters.Length; i++) {
                if (filters[i] == null) {
                    continue;
                }

                Vector3 position = IntermediateSources[i].Position;
                if (centerToStay && position.X == 0 && position.Y == 0 && position.Z > 0) {
                    filters[i].Effect = 0;
                    centerToStay = false;
                } else {
                    filters[i].Effect = Effect;
                }
                if (firstFilter >= 0) {
                    filters[i].SmoothFactor = filters[firstFilter].SmoothFactor;
                }
            }

            float[][] input = OnSamplesNeeded(samplesPerSource);
            int pair = input.Length;
            for (int i = 0; i < input.Length; i++) {
                if (IntermediateSources[i].LFE) {
                    Array.Copy(input[i], output[i], input[i].Length);
                } else {
                    filters[i].Process(input[i]);
                    filters[i].GroundLevel.CopyTo(output[i]);
                    filters[i].HeightLevel.CopyTo(output[pair]);
                    IntermediateSources[pair].Position = IntermediateSources[i].Position + new Vector3(0, filters[i].Height * Listener.EnvironmentSize.Y, 0);
                    pair++;
                }
            }
            return output;
        }

        /// <inheritdoc/>
        public void Dispose() {
            for (int i = 0; i < stftPairs.Length; i++) {
                stftPairs[i].analyzer.Dispose();
            }
        }
    }
}
