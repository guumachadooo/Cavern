using System;
using System.Collections.Generic;
using System.Numerics;

using Cavern.Channels;
using Cavern.Spatial;

namespace Cavern.Remapping {
    /// <summary>
    /// Atmos-Aware parametric STFT upmixer (Slot 2A) for hybrid Bed + Dynamic 3D Object streams
    /// (e.g., Dolby Atmos E-AC-3 JOC, TrueHD/DAMF Atmos, ADM BWF).
    /// Preserves 100% of native dynamic 3D objects, LFE, Front Center, and native height beds bit-transparently
    /// with sample-accurate delay alignment (including synchronized 3D object position history), while optionally:
    /// <list type="bullet">
    /// <item>Expanding missing surround/rear bed channels (2.0/3.1/5.1 beds -> 7.1) via DirAC/COMPASS STFT + orthogonal decorrelation.</item>
    /// <item>Enhancing sparse or silent ceiling segments with diffuse upper-band bed ambience that smoothly ducks whenever native Atmos overhead objects/beds are active.</item>
    /// </list>
    /// </summary>
    public sealed class AtmosAwareUpmixer : Upmixer, ISoundfieldTelemetryProvider, IDisposable {
        const float NativeHeightThresholdNormY = 0.15f;

        readonly IReadOnlyList<Source> inputSources;
        readonly int inputCount;
        readonly int bedCount;
        readonly bool expandMissingBeds;
        readonly bool enhanceSparseHeights;
        readonly float crossoverFrequency;

        // Bed channel index map inside inputSources (-1 if not present in the native bed)
        readonly int flIdx = -1;
        readonly int frIdx = -1;
        readonly int slIdx = -1;
        readonly int srIdx = -1;
        readonly int rlIdx = -1;
        readonly int rrIdx = -1;

        // Output indices for synthesized virtual matrix beds (-1 if native bed already exists or expansion disabled)
        readonly int virtualSlOutIdx = -1;
        readonly int virtualSrOutIdx = -1;
        readonly int virtualRlOutIdx = -1;
        readonly int virtualRrOutIdx = -1;

        // Output indices for synthesized sparse virtual heights (-1 if disabled)
        readonly int virtualTflOutIdx = -1;
        readonly int virtualTfrOutIdx = -1;
        readonly int virtualTslOutIdx = -1;
        readonly int virtualTsrOutIdx = -1;

        // Whether any active STFT processing modifies bed channels (triggers sample-exact alignment on pass-throughs)
        readonly bool needsStftAlignment;
        readonly bool stftStereoToSurround;
        readonly bool stftSurroundToRear;

        readonly ParametricStftAnalyzer frontMatrixStft;
        readonly ParametricStftAnalyzer surroundMatrixStft;
        readonly ParametricStftAnalyzer frontHeightStft;
        readonly ParametricStftAnalyzer surroundHeightStft;

        // Stage-matching 512-sample delay lines when matrix & height chain (keeps RL/RR & FL/FR phase-locked at 1024 samples)
        readonly FixedSampleDelayLine rearAlignDelayL;
        readonly FixedSampleDelayLine rearAlignDelayR;
        readonly FixedSampleDelayLine frontPreHeightDelayL;
        readonly FixedSampleDelayLine frontPreHeightDelayR;

        // Sample-accurate delay alignment for native beds & dynamic objects + position trajectory queue
        readonly FixedSampleDelayLine[] passThroughDelays;
        readonly Vector3[][] objectPositionHistory;
        readonly int[] objectPositionDelaySamples;
        int positionHistoryHead;
        int positionHistorySlots = MinPositionHistorySlots;
        const int MinPositionHistorySlots = 32; // Covers down to 32-sample bursts at 1024-sample dual-stage STFT latency

        // Smooth Atmos native-height activity ducker [0 = native overhead active, 1 = ceiling sparse/silent]
        float sparseHeightDuckFactor = 1f;

        // Scratch arrays for chaining matrix -> height cleanly without heap allocations
        float[] scratchFl = Array.Empty<float>();
        float[] scratchFr = Array.Empty<float>();
        float[] scratchSl = Array.Empty<float>();
        float[] scratchSr = Array.Empty<float>();

        /// <summary>
        /// Height enhancement strength for sparse/silent ceiling passages [0; 1].
        /// </summary>
        public float Effect { get; set; } = 0.75f;

        /// <summary>
        /// Smoothness of virtual height elevation transitions [0; 1].
        /// </summary>
        public float Smoothness { get; set; } = 0.8f;

        /// <summary>
        /// Current Atmos-aware ceiling ducking factor in [0; 1] (0 = native Atmos overhead active, 1 = sparse ceiling).
        /// </summary>
        public float SparseHeightActivityFactor => sparseHeightDuckFactor;

        /// <summary>
        /// Layer classification for every output source in <see cref="Upmixer.IntermediateSources"/>.
        /// </summary>
        public SourceLayer[] OutputLayers { get; }

        /// <summary>
        /// Nominal bed channel mapping for every output source in <see cref="Upmixer.IntermediateSources"/> (null for objects/heights).
        /// </summary>
        public ReferenceChannel?[] OutputBedChannels { get; }

        /// <summary>
        /// Latest energy-weighted DirAC/COMPASS soundfield telemetry computed from the front bed.
        /// </summary>
        public SoundfieldTelemetry LatestTelemetry { get; private set; } = SoundfieldTelemetry.Empty();

        /// <summary>
        /// Creates an Atmos-Aware parametric STFT upmixer around <paramref name="sources"/>.
        /// </summary>
        /// <param name="sources">All input sources (static beds followed by dynamic 3D objects)</param>
        /// <param name="bedChannels">Static bed channel definitions corresponding to the first <c>bedChannels.Length</c> sources</param>
        /// <param name="sampleRate">Stream sample rate in Hz</param>
        /// <param name="expandMissingBeds">Synthesize missing surround/rear beds if the native Atmos bed is 2.0/3.1/5.1</param>
        /// <param name="enhanceSparseHeights">Gently elevate diffuse upper-band bed ambience when native overhead activity is sparse</param>
        /// <param name="crossoverFrequency">Low-frequency anchor below which no height elevation is applied (Hz)</param>
        public AtmosAwareUpmixer(
            IReadOnlyList<Source> sources,
            ReferenceChannel[] bedChannels,
            int sampleRate,
            bool expandMissingBeds,
            bool enhanceSparseHeights,
            int crossoverFrequency = 250) :
            base(ComputeOutputCount(sources, bedChannels, expandMissingBeds, enhanceSparseHeights), sampleRate) {
            inputSources = sources;
            inputCount = sources.Count;
            bedCount = bedChannels != null ? Math.Min(bedChannels.Length, inputCount) : 0;
            this.expandMissingBeds = expandMissingBeds;
            this.enhanceSparseHeights = enhanceSparseHeights;
            this.crossoverFrequency = crossoverFrequency > 80 ? crossoverFrequency : 250f;

            for (int i = 0; i < bedCount; i++) {
                ReferenceChannel ch = bedChannels[i];
                if (ch == ReferenceChannel.FrontLeft) {
                    flIdx = i;
                } else if (ch == ReferenceChannel.FrontRight) {
                    frIdx = i;
                } else if (ch == ReferenceChannel.SideLeft) {
                    slIdx = i;
                } else if (ch == ReferenceChannel.SideRight) {
                    srIdx = i;
                } else if (ch == ReferenceChannel.RearLeft) {
                    rlIdx = i;
                } else if (ch == ReferenceChannel.RearRight) {
                    rrIdx = i;
                }
            }

            bool hasFronts = flIdx >= 0 && frIdx >= 0;
            bool hasSides = slIdx >= 0 && srIdx >= 0;
            bool hasRears = rlIdx >= 0 && rrIdx >= 0;

            stftStereoToSurround = expandMissingBeds && hasFronts && !hasSides && !hasRears;
            stftSurroundToRear = expandMissingBeds && hasFronts && hasSides && !hasRears;

            int totalOutputs = IntermediateSources.Length;
            OutputLayers = new SourceLayer[totalOutputs];
            OutputBedChannels = new ReferenceChannel?[totalOutputs];

            // 0 .. inputCount - 1 mirror native beds and native dynamic 3D objects in original index order
            for (int i = 0; i < inputCount; i++) {
                IntermediateSources[i].Position = sources[i].Position;
                IntermediateSources[i].LFE = sources[i].LFE;
                if (i < bedCount) {
                    OutputLayers[i] = SourceLayer.Bed;
                    OutputBedChannels[i] = bedChannels[i];
                } else {
                    OutputLayers[i] = SourceLayer.NativeObject;
                    OutputBedChannels[i] = null;
                }
            }

            int nextSlot = inputCount;
            Vector3 envSize = Listener.EnvironmentSize;
            if (envSize.X <= 0.1f || envSize.Y <= 0.1f || envSize.Z <= 0.1f) {
                envSize = new Vector3(10f, 7f, 10f);
            }

            if (stftStereoToSurround) {
                virtualSlOutIdx = nextSlot++;
                virtualSrOutIdx = nextSlot++;
                virtualRlOutIdx = nextSlot++;
                virtualRrOutIdx = nextSlot++;
                AssignVirtualBedSlot(virtualSlOutIdx, ReferenceChannel.SideLeft, envSize);
                AssignVirtualBedSlot(virtualSrOutIdx, ReferenceChannel.SideRight, envSize);
                AssignVirtualBedSlot(virtualRlOutIdx, ReferenceChannel.RearLeft, envSize);
                AssignVirtualBedSlot(virtualRrOutIdx, ReferenceChannel.RearRight, envSize);
            } else if (stftSurroundToRear) {
                virtualRlOutIdx = nextSlot++;
                virtualRrOutIdx = nextSlot++;
                AssignVirtualBedSlot(virtualRlOutIdx, ReferenceChannel.RearLeft, envSize);
                AssignVirtualBedSlot(virtualRrOutIdx, ReferenceChannel.RearRight, envSize);
            }

            bool canFrontHeight = enhanceSparseHeights && hasFronts;
            bool canSurroundHeight = enhanceSparseHeights && (hasSides || virtualSlOutIdx >= 0);
            if (canFrontHeight) {
                virtualTflOutIdx = nextSlot++;
                virtualTfrOutIdx = nextSlot++;
                AssignVirtualHeightSlot(virtualTflOutIdx, sources[flIdx].Position, envSize);
                AssignVirtualHeightSlot(virtualTfrOutIdx, sources[frIdx].Position, envSize);
            }
            if (canSurroundHeight) {
                virtualTslOutIdx = nextSlot++;
                virtualTsrOutIdx = nextSlot++;
                Vector3 slPos = hasSides ? sources[slIdx].Position : IntermediateSources[virtualSlOutIdx].Position;
                Vector3 srPos = hasSides ? sources[srIdx].Position : IntermediateSources[virtualSrOutIdx].Position;
                AssignVirtualHeightSlot(virtualTslOutIdx, slPos, envSize);
                AssignVirtualHeightSlot(virtualTsrOutIdx, srPos, envSize);
            }

            frontMatrixStft = new ParametricStftAnalyzer(sampleRate);
            if (stftSurroundToRear) {
                surroundMatrixStft = new ParametricStftAnalyzer(sampleRate);
            }
            if (canFrontHeight) {
                frontHeightStft = new ParametricStftAnalyzer(sampleRate);
            }
            if (canSurroundHeight) {
                surroundHeightStft = new ParametricStftAnalyzer(sampleRate);
            }

            // Total STFT alignment depth: 1 stage (512) if matrix OR height runs, 2 stages (1024) if both chain
            bool matrixActive = stftStereoToSurround || stftSurroundToRear;
            bool heightActive = canFrontHeight || canSurroundHeight;
            needsStftAlignment = matrixActive || heightActive;
            int totalDelaySamples = (matrixActive && heightActive)
                ? ParametricStftAnalyzer.LatencySamples * 2
                : ParametricStftAnalyzer.LatencySamples;

            if (matrixActive && heightActive) {
                rearAlignDelayL = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
                rearAlignDelayR = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
                if (stftSurroundToRear && canFrontHeight) {
                    frontPreHeightDelayL = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
                    frontPreHeightDelayR = new FixedSampleDelayLine(ParametricStftAnalyzer.LatencySamples);
                }
            }

            passThroughDelays = new FixedSampleDelayLine[inputCount];
            objectPositionHistory = new Vector3[inputCount][];
            objectPositionDelaySamples = new int[inputCount];
            for (int i = 0; i < inputCount; i++) {
                passThroughDelays[i] = new FixedSampleDelayLine(totalDelaySamples);
                objectPositionHistory[i] = new Vector3[positionHistorySlots];
                for (int s = 0; s < positionHistorySlots; s++) {
                    objectPositionHistory[i][s] = sources[i].Position;
                }
                objectPositionDelaySamples[i] = totalDelaySamples;
            }

            SetupCollection(sources, sampleRate);
        }

        static int ComputeOutputCount(
            IReadOnlyList<Source> sources,
            ReferenceChannel[] bedChannels,
            bool expandMissingBeds,
            bool enhanceSparseHeights) {
            int count = sources.Count;
            int beds = bedChannels != null ? Math.Min(bedChannels.Length, count) : 0;
            bool hasFl = false, hasFr = false, hasSl = false, hasSr = false, hasRl = false, hasRr = false;
            for (int i = 0; i < beds; i++) {
                ReferenceChannel ch = bedChannels[i];
                if (ch == ReferenceChannel.FrontLeft) {
                    hasFl = true;
                } else if (ch == ReferenceChannel.FrontRight) {
                    hasFr = true;
                } else if (ch == ReferenceChannel.SideLeft) {
                    hasSl = true;
                } else if (ch == ReferenceChannel.SideRight) {
                    hasSr = true;
                } else if (ch == ReferenceChannel.RearLeft) {
                    hasRl = true;
                } else if (ch == ReferenceChannel.RearRight) {
                    hasRr = true;
                }
            }
            bool hasFronts = hasFl && hasFr;
            bool hasSides = hasSl && hasSr;
            bool hasRears = hasRl && hasRr;

            int extra = 0;
            bool willAddSides = expandMissingBeds && hasFronts && !hasSides && !hasRears;
            if (willAddSides) {
                extra += 4; // SL, SR, RL, RR
            } else if (expandMissingBeds && hasFronts && hasSides && !hasRears) {
                extra += 2; // RL, RR
            }
            if (enhanceSparseHeights && hasFronts) {
                extra += 2; // Virtual TFL, TFR
            }
            if (enhanceSparseHeights && (hasSides || willAddSides)) {
                extra += 2; // Virtual TSL, TSR
            }
            return count + extra;
        }

        void AssignVirtualBedSlot(int outIdx, ReferenceChannel channel, Vector3 envSize) {
            Vector3 normPos = ChannelPrototype.AlternativePositions[(int)channel];
            IntermediateSources[outIdx].Position = normPos * envSize;
            OutputLayers[outIdx] = SourceLayer.VirtualMatrix;
            OutputBedChannels[outIdx] = channel;
        }

        void AssignVirtualHeightSlot(int outIdx, Vector3 groundPos, Vector3 envSize) {
            IntermediateSources[outIdx].Position = new Vector3(groundPos.X, envSize.Y * 0.5f, groundPos.Z);
            OutputLayers[outIdx] = SourceLayer.VirtualHeight;
            OutputBedChannels[outIdx] = null;
        }

        /// <summary>
        /// Clears all STFT overlap-add states, decorrelators, delay lines, and ceiling ducking filters.
        /// </summary>
        public void Reset() {
            frontMatrixStft.Reset();
            if (surroundMatrixStft != null) {
                surroundMatrixStft.Reset();
            }
            if (frontHeightStft != null) {
                frontHeightStft.Reset();
            }
            if (surroundHeightStft != null) {
                surroundHeightStft.Reset();
            }
            if (rearAlignDelayL != null) {
                rearAlignDelayL.Reset();
                rearAlignDelayR.Reset();
            }
            if (frontPreHeightDelayL != null) {
                frontPreHeightDelayL.Reset();
                frontPreHeightDelayR.Reset();
            }
            for (int i = 0; i < passThroughDelays.Length; i++) {
                passThroughDelays[i].Reset();
            }
            sparseHeightDuckFactor = 1f;
            LatestTelemetry = SoundfieldTelemetry.Empty();
        }

        /// <inheritdoc/>
        protected override float[][] UpdateSources(int samplesPerSource) {
            float[][] input = OnSamplesNeeded(samplesPerSource);
            EnsureScratch(samplesPerSource);

            Vector3 envSize = Listener.EnvironmentSize;
            float invEnvY = envSize.Y > 0.1f ? 1f / envSize.Y : 0.142857f;

            // 1. Sync live 3D positions of all native beds & dynamic Atmos objects (with matched latency history if STFT active)
            int requiredLagSlots = needsStftAlignment
                ? Math.Max(1, (int)Math.Round(objectPositionDelaySamples[0] / (double)Math.Max(1, samplesPerSource)))
                : 0;
            if (needsStftAlignment && requiredLagSlots + 2 > positionHistorySlots) {
                EnsurePositionHistoryCapacity(requiredLagSlots + 2);
            }
            int historyLagSlots = needsStftAlignment
                ? Math.Min(positionHistorySlots - 1, requiredLagSlots)
                : 0;
            int readSlot = (positionHistoryHead - historyLagSlots + positionHistorySlots) % positionHistorySlots;

            double nativeHeightEnergy = 0.0;
            double groundBedEnergy = 0.0;

            for (int i = 0; i < inputCount; i++) {
                Vector3 livePos = inputSources[i].Position;
                objectPositionHistory[i][positionHistoryHead] = livePos;
                IntermediateSources[i].Position = needsStftAlignment ? objectPositionHistory[i][readSlot] : livePos;

                // Measure native height activity vs ground bed energy for Atmos-Aware ceiling ducking
                float normY = livePos.Y * invEnvY;
                double chEnergy = ComputeMeanSquare(input[i], samplesPerSource);
                if (!inputSources[i].LFE) {
                    if (normY > NativeHeightThresholdNormY) {
                        nativeHeightEnergy += chEnergy;
                    } else if (i < bedCount) {
                        groundBedEnergy += chEnergy;
                    }
                }
            }
            positionHistoryHead = (positionHistoryHead + 1) % positionHistorySlots;

            // 2. Update smooth Atmos-Aware sparse height ducking factor (fast attack ~80 ms, gentle release ~250 ms)
            double totalMeasured = nativeHeightEnergy + groundBedEnergy;
            float targetSparse = 1f;
            if (totalMeasured > 1e-10) {
                float nativeRatio = (float)(nativeHeightEnergy / totalMeasured);
                targetSparse = 1f - nativeRatio * 3.0f;
                if (targetSparse < 0f) {
                    targetSparse = 0f;
                } else if (targetSparse > 1f) {
                    targetSparse = 1f;
                }
            }
            float duckAlpha = targetSparse < sparseHeightDuckFactor ? 0.25f : 0.08f;
            sparseHeightDuckFactor += (targetSparse - sparseHeightDuckFactor) * duckAlpha;

            // 3. Delay-align all native pass-through channels (objects, LFE, center, native height beds) bit-transparently
            for (int i = 0; i < inputCount; i++) {
                bool processedByMatrix =
                    (stftStereoToSurround && (i == flIdx || i == frIdx)) ||
                    (stftSurroundToRear && (i == slIdx || i == srIdx));
                bool processedByHeight =
                    (frontHeightStft != null && (i == flIdx || i == frIdx)) ||
                    (surroundHeightStft != null && (i == slIdx || i == srIdx));

                if (!processedByMatrix && !processedByHeight) {
                    if (needsStftAlignment) {
                        passThroughDelays[i].Process(input[i], output[i], samplesPerSource);
                    } else {
                        Array.Copy(input[i], 0, output[i], 0, samplesPerSource);
                    }
                }
            }

            // 4. Stage 2A-Matrix: Expand missing surround/rear beds only (never overwriting native FrontCenter or LFE)
            float[] flStage = flIdx >= 0 ? input[flIdx] : null;
            float[] frStage = frIdx >= 0 ? input[frIdx] : null;
            float[] slStage = slIdx >= 0 ? input[slIdx] : null;
            float[] srStage = srIdx >= 0 ? input[srIdx] : null;

            if (stftStereoToSurround) {
                frontMatrixStft.ProcessStereoToSurround(
                    input[flIdx],
                    input[frIdx],
                    scratchFl,
                    scratchFr,
                    null, // Preserve native Atmos FrontCenter untouched
                    scratchSl,
                    scratchSr,
                    output[virtualRlOutIdx],
                    output[virtualRrOutIdx],
                    samplesPerSource,
                    false,
                    false);
                if (rearAlignDelayL != null) {
                    rearAlignDelayL.Process(output[virtualRlOutIdx], output[virtualRlOutIdx], samplesPerSource);
                    rearAlignDelayR.Process(output[virtualRrOutIdx], output[virtualRrOutIdx], samplesPerSource);
                }
                flStage = scratchFl;
                frStage = scratchFr;
                slStage = scratchSl;
                srStage = scratchSr;
                LatestTelemetry = frontMatrixStft.LatestTelemetry;
            } else if (stftSurroundToRear && surroundMatrixStft != null) {
                frontMatrixStft.AnalyzeOnly(input[flIdx], input[frIdx], samplesPerSource);
                LatestTelemetry = frontMatrixStft.LatestTelemetry;

                // Keep FL/FR phase-locked with surroundMatrixStft (+ height stage if chained)
                if (frontHeightStft == null) {
                    passThroughDelays[flIdx].Process(input[flIdx], output[flIdx], samplesPerSource);
                    passThroughDelays[frIdx].Process(input[frIdx], output[frIdx], samplesPerSource);
                } else {
                    frontPreHeightDelayL.Process(input[flIdx], scratchFl, samplesPerSource);
                    frontPreHeightDelayR.Process(input[frIdx], scratchFr, samplesPerSource);
                    flStage = scratchFl;
                    frStage = scratchFr;
                }

                surroundMatrixStft.ProcessSurroundSplitToRear(
                    input[slIdx],
                    input[srIdx],
                    scratchSl,
                    scratchSr,
                    output[virtualRlOutIdx],
                    output[virtualRrOutIdx],
                    samplesPerSource);
                if (rearAlignDelayL != null) {
                    rearAlignDelayL.Process(output[virtualRlOutIdx], output[virtualRlOutIdx], samplesPerSource);
                    rearAlignDelayR.Process(output[virtualRrOutIdx], output[virtualRrOutIdx], samplesPerSource);
                }
                slStage = scratchSl;
                srStage = scratchSr;
            } else if (flIdx >= 0 && frIdx >= 0) {
                frontMatrixStft.AnalyzeOnly(input[flIdx], input[frIdx], samplesPerSource);
                LatestTelemetry = frontMatrixStft.LatestTelemetry;
            }

            // 5. Stage 2A-Height: Atmos-Aware sparse height enhancement (automatically ducks when native Atmos heights play)
            float effectiveHeight = Effect * sparseHeightDuckFactor;
            if (frontHeightStft != null && flStage != null && frStage != null) {
                frontHeightStft.ProcessGroundToHeightPair(
                    flStage,
                    frStage,
                    output[flIdx],
                    output[frIdx],
                    output[virtualTflOutIdx],
                    output[virtualTfrOutIdx],
                    samplesPerSource,
                    effectiveHeight,
                    Smoothness,
                    crossoverFrequency,
                    out float frontElevation);
                float liftY = frontElevation * envSize.Y;
                IntermediateSources[virtualTflOutIdx].Position = IntermediateSources[flIdx].Position + new Vector3(0f, liftY, 0f);
                IntermediateSources[virtualTfrOutIdx].Position = IntermediateSources[frIdx].Position + new Vector3(0f, liftY, 0f);
            } else if (stftStereoToSurround) {
                Array.Copy(flStage, 0, output[flIdx], 0, samplesPerSource);
                Array.Copy(frStage, 0, output[frIdx], 0, samplesPerSource);
            }

            int slTargetOut = slIdx >= 0 ? slIdx : virtualSlOutIdx;
            int srTargetOut = srIdx >= 0 ? srIdx : virtualSrOutIdx;
            if (surroundHeightStft != null && slStage != null && srStage != null && slTargetOut >= 0 && srTargetOut >= 0) {
                surroundHeightStft.ProcessGroundToHeightPair(
                    slStage,
                    srStage,
                    output[slTargetOut],
                    output[srTargetOut],
                    output[virtualTslOutIdx],
                    output[virtualTsrOutIdx],
                    samplesPerSource,
                    effectiveHeight,
                    Smoothness,
                    crossoverFrequency,
                    out float surrElevation);
                float liftY = surrElevation * envSize.Y;
                IntermediateSources[virtualTslOutIdx].Position = IntermediateSources[slTargetOut].Position + new Vector3(0f, liftY, 0f);
                IntermediateSources[virtualTsrOutIdx].Position = IntermediateSources[srTargetOut].Position + new Vector3(0f, liftY, 0f);
            } else if (slStage != null && srStage != null && (stftStereoToSurround || stftSurroundToRear)) {
                Array.Copy(slStage, 0, output[slTargetOut], 0, samplesPerSource);
                Array.Copy(srStage, 0, output[srTargetOut], 0, samplesPerSource);
            }

            return output;
        }

        void EnsureScratch(int samplesPerSource) {
            if (scratchFl.Length != samplesPerSource) {
                scratchFl = new float[samplesPerSource];
                scratchFr = new float[samplesPerSource];
                scratchSl = new float[samplesPerSource];
                scratchSr = new float[samplesPerSource];
            }
        }

        void EnsurePositionHistoryCapacity(int requiredSlots) {
            if (requiredSlots <= positionHistorySlots) {
                return;
            }
            int nextCap = positionHistorySlots;
            while (nextCap < requiredSlots) {
                nextCap *= 2;
            }
            for (int i = 0; i < inputCount; i++) {
                Vector3[] oldHist = objectPositionHistory[i];
                Vector3[] grown = new Vector3[nextCap];
                Vector3 oldestVal = oldHist[positionHistoryHead];
                int pad = nextCap - positionHistorySlots;
                for (int s = 0; s < pad; s++) {
                    grown[s] = oldestVal;
                }
                for (int s = 0; s < positionHistorySlots; s++) {
                    grown[pad + s] = oldHist[(positionHistoryHead + s) % positionHistorySlots];
                }
                objectPositionHistory[i] = grown;
            }
            positionHistoryHead = 0;
            positionHistorySlots = nextCap;
        }

        static double ComputeMeanSquare(float[] samples, int count) {
            if (samples == null || count <= 0) {
                return 0.0;
            }
            int n = count < samples.Length ? count : samples.Length;
            double sum = 0.0;
            for (int i = 0; i < n; i++) {
                float v = samples[i];
                sum += (double)v * v;
            }
            return sum / n;
        }

        /// <inheritdoc/>
        public void Dispose() {
            frontMatrixStft.Dispose();
            if (surroundMatrixStft != null) {
                surroundMatrixStft.Dispose();
            }
            if (frontHeightStft != null) {
                frontHeightStft.Dispose();
            }
            if (surroundHeightStft != null) {
                surroundHeightStft.Dispose();
            }
        }
    }
}
