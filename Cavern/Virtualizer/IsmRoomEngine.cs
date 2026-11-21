using System;
using System.Numerics;

using Cavern.Utilities;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Physically calibrated architectural room presets for the Allen &amp; Berkley (1979) Image Source Method (ISM).
    /// </summary>
    public enum IsmRoomPreset {
        /// <summary>ITU-R BS.1116 / IEC 60268-13 mastering control room (5.2 x 3.1 x 6.4 m).</summary>
        MasteringStudio = 0,
        /// <summary>Compact near-field studio mixing room (4.2 x 2.8 x 5.0 m).</summary>
        MixingRoom = 1,
        /// <summary>IEC 60268-13 domestic listening living room (5.0 x 2.7 x 4.2 m).</summary>
        LivingRoom = 2,
        /// <summary>Dolby / SMPTE theatrical screening stage (12.0 x 5.5 x 15.0 m).</summary>
        CinemaStage = 3,
        /// <summary>Uses the user's configured <see cref="Listener.EnvironmentSize"/> room dimensions.</summary>
        CustomEnvironment = 4
    }

    /// <summary>
    /// Real ISO 354 / Sabine 3-band acoustic wall absorption profiles (<c>0 &lt; alpha &lt; 1</c> across all bands).
    /// </summary>
    public enum IsmWallMaterial {
        /// <summary>Broadband mineral-wool studio panels + corner bass traps (alpha: 0.48 / 0.72 / 0.80).</summary>
        StudioBroadband = 0,
        /// <summary>Balanced acoustic diffuser/absorber mix room (alpha: 0.32 / 0.56 / 0.68).</summary>
        TreatedStudio = 1,
        /// <summary>Gypsum plaster walls with domestic carpet &amp; drapes (alpha: 0.18 / 0.34 / 0.46).</summary>
        LivingRoomPlaster = 2,
        /// <summary>Stretched cinema fabric over fiberglass + auditorium seating (alpha: 0.38 / 0.65 / 0.78).</summary>
        CinemaFabric = 3,
        /// <summary>Hardwood / plywood paneling with light treatment (alpha: 0.16 / 0.20 / 0.28).</summary>
        ReflectiveWood = 4
    }

    /// <summary>
    /// Three-band ISO 354 Sabine absorption coefficients (Low ~250 Hz, Mid ~1 kHz, High ~4 kHz).
    /// Every band strictly satisfies <c>0 &lt; alpha &lt; 1</c>.
    /// </summary>
    public readonly struct WallAbsorptionBands {
        /// <summary>Low-frequency absorption coefficient (~250 Hz).</summary>
        public readonly float Low;

        /// <summary>Mid-frequency absorption coefficient (~1 kHz).</summary>
        public readonly float Mid;

        /// <summary>High-frequency absorption coefficient (~4 kHz).</summary>
        public readonly float High;

        /// <summary>
        /// Constructs a validated 3-band absorption profile clamped strictly inside <c>(0.02, 0.98)</c>.
        /// </summary>
        public WallAbsorptionBands(float low, float mid, float high) {
            Low = Math.Clamp(low, 0.02f, 0.98f);
            Mid = Math.Clamp(mid, 0.02f, 0.98f);
            High = Math.Clamp(high, 0.02f, 0.98f);
        }

        /// <summary>
        /// Returns the exact pressure reflection factor <c>sqrt(1 - alpha)</c> for each band.
        /// </summary>
        public void GetReflectionCoefficients(out float rLow, out float rMid, out float rHigh) {
            rLow = MathF.Sqrt(1f - Low);
            rMid = MathF.Sqrt(1f - Mid);
            rHigh = MathF.Sqrt(1f - High);
        }

        /// <summary>
        /// Returns calibrated ISO 354 wall absorption bands for the given <paramref name="material"/>.
        /// </summary>
        public static WallAbsorptionBands FromMaterial(IsmWallMaterial material) {
            switch (material) {
                case IsmWallMaterial.StudioBroadband:
                    return new WallAbsorptionBands(0.48f, 0.72f, 0.80f);
                case IsmWallMaterial.TreatedStudio:
                    return new WallAbsorptionBands(0.32f, 0.56f, 0.68f);
                case IsmWallMaterial.LivingRoomPlaster:
                    return new WallAbsorptionBands(0.18f, 0.34f, 0.46f);
                case IsmWallMaterial.CinemaFabric:
                    return new WallAbsorptionBands(0.38f, 0.65f, 0.78f);
                case IsmWallMaterial.ReflectiveWood:
                    return new WallAbsorptionBands(0.16f, 0.20f, 0.28f);
                default:
                    return new WallAbsorptionBands(0.48f, 0.72f, 0.80f);
            }
        }
    }

    /// <summary>
    /// Shoebox room geometry and acoustic reverberation metrics (Sabine &amp; Norris-Eyring RT60).
    /// </summary>
    public readonly struct IsmRoomGeometry {
        /// <summary>Full interior room dimensions in meters (Width X, Height Y, Depth Z).</summary>
        public readonly Vector3 DimensionsMeters;

        /// <summary>Half-dimensions in meters (<c>DimensionsMeters * 0.5</c>).</summary>
        public readonly Vector3 HalfDimensionsMeters;

        /// <summary>Interior room volume in cubic meters.</summary>
        public readonly float VolumeCubicMeters;

        /// <summary>Total interior wall/floor/ceiling surface area in square meters.</summary>
        public readonly float SurfaceAreaSquareMeters;

        /// <summary>Mean mid-band Sabine absorption coefficient across all 6 surfaces.</summary>
        public readonly float MeanAlphaMid;

        /// <summary>Classical Sabine reverberation time <c>RT60 = 0.161 * V / (S * alpha)</c> in seconds.</summary>
        public readonly float SabineRt60Seconds;

        /// <summary>Norris-Eyring reverberation time <c>RT60 = 0.161 * V / (-S * ln(1 - alpha))</c> in seconds (more accurate for treated rooms).</summary>
        public readonly float EyringRt60Seconds;

        /// <summary>Wall absorption coefficients for side/front/back walls.</summary>
        public readonly WallAbsorptionBands WallAbsorption;

        /// <summary>Floor absorption coefficients (slightly more absorbent at HF due to carpet/furnishing).</summary>
        public readonly WallAbsorptionBands FloorAbsorption;

        /// <summary>Ceiling absorption coefficients (acoustic cloud/plaster).</summary>
        public readonly WallAbsorptionBands CeilingAbsorption;

        /// <summary>
        /// Builds a physical shoebox room geometry from preset, material, and optional custom <see cref="Listener.EnvironmentSize"/>.
        /// </summary>
        public IsmRoomGeometry(IsmRoomPreset preset, IsmWallMaterial material, Vector3 customEnvironmentHalfSize) {
            DimensionsMeters = ResolvePresetDimensions(preset, customEnvironmentHalfSize);
            HalfDimensionsMeters = DimensionsMeters * 0.5f;
            VolumeCubicMeters = DimensionsMeters.X * DimensionsMeters.Y * DimensionsMeters.Z;

            float areaX = 2f * DimensionsMeters.Y * DimensionsMeters.Z; // Left + Right walls
            float areaZ = 2f * DimensionsMeters.X * DimensionsMeters.Y; // Front + Back walls
            float areaFloor = DimensionsMeters.X * DimensionsMeters.Z;
            float areaCeiling = areaFloor;
            SurfaceAreaSquareMeters = areaX + areaZ + areaFloor + areaCeiling;

            WallAbsorption = WallAbsorptionBands.FromMaterial(material);
            FloorAbsorption = new WallAbsorptionBands(
                WallAbsorption.Low * 0.90f,
                Math.Min(0.95f, WallAbsorption.Mid * 1.08f),
                Math.Min(0.96f, WallAbsorption.High * 1.10f));
            CeilingAbsorption = new WallAbsorptionBands(
                WallAbsorption.Low * 0.95f,
                WallAbsorption.Mid,
                Math.Min(0.95f, WallAbsorption.High * 1.04f));

            float totalAbsMid = (areaX + areaZ) * WallAbsorption.Mid +
                areaFloor * FloorAbsorption.Mid +
                areaCeiling * CeilingAbsorption.Mid;
            MeanAlphaMid = Math.Clamp(totalAbsMid / Math.Max(1e-3f, SurfaceAreaSquareMeters), 0.02f, 0.98f);

            // Sabine & Eyring RT60 using 24*ln(10)/c ~= 0.161 s/m at 20 C
            SabineRt60Seconds = 0.161f * VolumeCubicMeters / Math.Max(1e-3f, SurfaceAreaSquareMeters * MeanAlphaMid);
            float eyringDenom = -SurfaceAreaSquareMeters * MathF.Log(1f - MeanAlphaMid);
            EyringRt60Seconds = 0.161f * VolumeCubicMeters / Math.Max(1e-3f, eyringDenom);
        }

        /// <summary>
        /// Resolves full room dimensions (Width X, Height Y, Depth Z in meters) for a given preset.
        /// </summary>
        public static Vector3 ResolvePresetDimensions(IsmRoomPreset preset, Vector3 customEnvironmentHalfSize) {
            switch (preset) {
                case IsmRoomPreset.MasteringStudio:
                    return new Vector3(5.2f, 3.1f, 6.4f);
                case IsmRoomPreset.MixingRoom:
                    return new Vector3(4.2f, 2.8f, 5.0f);
                case IsmRoomPreset.LivingRoom:
                    return new Vector3(5.0f, 2.7f, 4.2f);
                case IsmRoomPreset.CinemaStage:
                    return new Vector3(12.0f, 5.5f, 15.0f);
                case IsmRoomPreset.CustomEnvironment:
                    float lx = Math.Clamp(customEnvironmentHalfSize.X * 2f, 2.5f, 30f);
                    float ly = Math.Clamp(customEnvironmentHalfSize.Y * 2f, 2.2f, 16f);
                    float lz = Math.Clamp(customEnvironmentHalfSize.Z * 2f, 2.5f, 35f);
                    return new Vector3(lx, ly, lz);
                default:
                    return new Vector3(5.2f, 3.1f, 6.4f);
            }
        }
    }

    /// <summary>
    /// Precomputed descriptor of a single Allen &amp; Berkley mirror image reflection routed to the top-2 virtual HRIR speakers.
    /// </summary>
    internal struct IsmReflectionTap {
        public float DelaySamples;
        public float GainLow;
        public float GainMid;
        public float GainHigh;
        public int TargetChannelA;
        public int TargetChannelB;
        public float WeightA;
        public float WeightB;
    }

    /// <summary>
    /// Real-time zero-GC Allen &amp; Berkley (1979) 1st- and 2nd-order shoebox Image Source Method (ISM)
    /// early-reflection engine. Splits each virtual speaker into 3 complementary frequency bands,
    /// taps exact causal mirror-image delays with 3-band Sabine wall absorption and ISO 9613-1 air absorption,
    /// transforms arrival directions live with listener head rotation, and routes reflections into active
    /// <see cref="VirtualChannel"/> pre-HRIR buffers (zero extra FFT convolutions, unconditionally stable).
    /// </summary>
    public sealed class IsmRoomEngine {
        /// <summary>Number of 1st-order reflections in a 6-wall shoebox.</summary>
        public const int Order1ReflectionCount = 6;

        /// <summary>Total number of 1st + 2nd-order reflections in a 6-wall shoebox (6 + 18 = 24).</summary>
        public const int Order2ReflectionCount = 24;

        // Precomputed integer mirror index triples (nx, ny, nz) for order 1 (indices 0..5) and order 2 (indices 6..23)
        static readonly int[] imageNx = new int[] {
            -1,  1,  0,  0,  0,  0,
            -2,  2,  0,  0,  0,  0,
            -1, -1,  1,  1, -1, -1,  1,  1,  0,  0,  0,  0
        };
        static readonly int[] imageNy = new int[] {
             0,  0, -1,  1,  0,  0,
             0,  0, -2,  2,  0,  0,
            -1,  1, -1,  1,  0,  0,  0,  0, -1, -1,  1,  1
        };
        static readonly int[] imageNz = new int[] {
             0,  0,  0,  0, -1,  1,
             0,  0,  0,  0, -2,  2,
             0,  0,  0,  0, -1,  1, -1,  1, -1,  1, -1,  1
        };

        // 3-band complementary crossover frequencies (Low < 500 Hz, Mid 500..2500 Hz, High > 2500 Hz)
        const double lowMidCrossHz = 500.0;
        const double midHighCrossHz = 2500.0;

        // Maximum delay line length: 0.40 s covers 2nd-order reflections even in a 35 m cinema stage (~0.20 s)
        const float maxDelaySeconds = 0.40f;

        IsmRoomGeometry geometry;
        IsmRoomPreset preset = IsmRoomPreset.MasteringStudio;
        IsmWallMaterial material = IsmWallMaterial.StudioBroadband;
        Vector3 customHalfSize = new Vector3(10f, 7f, 10f);
        int reflectionOrder = 2;
        float reflectionLevel = 1.0f;
        float airTempC = AirAbsorptionFilter.DefaultTemperatureCelsius;
        float airRhPct = AirAbsorptionFilter.DefaultRelativeHumidityPct;
        bool airAbsorptionOnReflections = true;

        int configuredSampleRate;
        int configuredChannels;
        int delayBufferMask;
        int delayBufferLength;
        int writeIndex;

        // Per-channel 3-band circular delay lines & 1-pole complementary split states (zero-GC)
        float[][] delayLow;
        float[][] delayMid;
        float[][] delayHigh;
        float[] lpLowState;
        float[] lpMidState;
        float lpLowAlpha;
        float lpMidAlpha;

        // Scratch accumulation buffers (one per virtual channel, resized only on blockSize/channel count change)
        float[][] reflectionAccum;
        Vector3[] channelHeadUnits;
        bool[] channelIsSpatial;

        // Precomputed per-source-channel reflection routing table
        IsmReflectionTap[][] tapTable;
        int activeTapCount;
        bool geometryDirty = true;

        // Cached listener pose to detect rotation/position deltas for zero-allocation tap refresh
        Vector3 lastListenerRoomPos = new Vector3(float.NaN, float.NaN, float.NaN);
        Vector3 lastListenerRotationDeg = new Vector3(float.NaN, float.NaN, float.NaN);

        /// <summary>
        /// Active shoebox room geometry and Sabine/Eyring RT60 telemetry.
        /// </summary>
        public IsmRoomGeometry Geometry => geometry;

        /// <summary>
        /// Active reflection count per virtual speaker (6 for order 1, 24 for orders 1+2).
        /// </summary>
        public int ActiveReflectionCount => reflectionOrder >= 2 ? Order2ReflectionCount : Order1ReflectionCount;

        /// <summary>
        /// Constructs an ISM room engine initialized to the Mastering Studio + Broadband Absorber preset.
        /// </summary>
        public IsmRoomEngine() {
            geometry = new IsmRoomGeometry(preset, material, customHalfSize);
        }

        /// <summary>
        /// Computes the exact Allen &amp; Berkley mirror image coordinate for a centered 1D box <c>[-L/2, +L/2]</c>.
        /// </summary>
        public static float ComputeMirrorCoordinate(float sourceCoord, float boxLength, int mirrorIndex) {
            return mirrorIndex * boxLength + ((mirrorIndex & 1) != 0 ? -sourceCoord : sourceCoord);
        }

        /// <summary>
        /// Updates room preset, wall material, reflection order (1 or 2), physical level, and atmospheric parameters.
        /// Rebuilds geometry only when parameters actually change.
        /// </summary>
        public void Configure(
            IsmRoomPreset roomPreset,
            IsmWallMaterial wallMaterial,
            int order,
            float level,
            Vector3 customEnvironmentHalfSize,
            bool enableAirAbsorption,
            float temperatureCelsius,
            float relativeHumidityPct) {
            int clampedOrder = order <= 1 ? 1 : 2;
            float clampedLevel = Math.Clamp(level, 0f, 1.5f);
            float clampedTemp = Math.Clamp(temperatureCelsius, -10f, 45f);
            float clampedRh = Math.Clamp(relativeHumidityPct, 10f, 95f);

            bool changed =
                preset != roomPreset ||
                material != wallMaterial ||
                reflectionOrder != clampedOrder ||
                MathF.Abs(reflectionLevel - clampedLevel) > 1e-4f ||
                airAbsorptionOnReflections != enableAirAbsorption ||
                MathF.Abs(airTempC - clampedTemp) > 0.1f ||
                MathF.Abs(airRhPct - clampedRh) > 0.25f ||
                (roomPreset == IsmRoomPreset.CustomEnvironment && Vector3.DistanceSquared(customHalfSize, customEnvironmentHalfSize) > 1e-4f);

            if (!changed) {
                return;
            }

            preset = roomPreset;
            material = wallMaterial;
            reflectionOrder = clampedOrder;
            reflectionLevel = clampedLevel;
            customHalfSize = customEnvironmentHalfSize;
            airAbsorptionOnReflections = enableAirAbsorption;
            airTempC = clampedTemp;
            airRhPct = clampedRh;
            geometry = new IsmRoomGeometry(preset, material, customHalfSize);
            geometryDirty = true;
        }

        /// <summary>
        /// Clears all delay lines and crossover states (called on seek, profile switch, or disable).
        /// </summary>
        public void Reset() {
            if (delayLow != null) {
                for (int i = 0; i < delayLow.Length; i++) {
                    Array.Clear(delayLow[i], 0, delayLow[i].Length);
                    Array.Clear(delayMid[i], 0, delayMid[i].Length);
                    Array.Clear(delayHigh[i], 0, delayHigh[i].Length);
                }
            }
            if (lpLowState != null) {
                Array.Clear(lpLowState, 0, lpLowState.Length);
                Array.Clear(lpMidState, 0, lpMidState.Length);
            }
            writeIndex = 0;
        }

        /// <summary>
        /// Computes and injects exact 1st/2nd-order shoebox early reflections into <paramref name="channelBuffers"/>
        /// prior to HRIR convolution. Feed-forward only (zero feedback), zero heap allocations in steady state.
        /// </summary>
        public void ProcessPreHrir(
            float[][] channelBuffers,
            VirtualChannel[] activeChannels,
            int blockSize,
            int sampleRate,
            Vector3 listenerNormalizedPos,
            Vector3 listenerRotationDeg) {
            if (channelBuffers == null || activeChannels == null || blockSize <= 0 || sampleRate < 8000 || reflectionLevel <= 1e-4f) {
                return;
            }
            int channels = activeChannels.Length;
            EnsureAllocated(channels, blockSize, sampleRate, activeChannels);

            // Map normalized listener position [-1..1] safely inside physical shoebox (at least 0.35 m from every wall)
            Vector3 half = geometry.HalfDimensionsMeters;
            Vector3 listenerRoomPos = new Vector3(
                Math.Clamp(listenerNormalizedPos.X * half.X, -(half.X - 0.35f), half.X - 0.35f),
                Math.Clamp(listenerNormalizedPos.Y * half.Y, -(half.Y - 0.35f), half.Y - 0.35f),
                Math.Clamp(listenerNormalizedPos.Z * half.Z, -(half.Z - 0.35f), half.Z - 0.35f));

            if (geometryDirty ||
                Vector3.DistanceSquared(listenerRoomPos, lastListenerRoomPos) > 1e-4f ||
                Vector3.DistanceSquared(listenerRotationDeg, lastListenerRotationDeg) > 0.25f) {
                RebuildReflectionTaps(channels, sampleRate, listenerRoomPos, listenerRotationDeg);
                lastListenerRoomPos = listenerRoomPos;
                lastListenerRotationDeg = listenerRotationDeg;
                geometryDirty = false;
            }

            // 1. Clear feed-forward reflection accumulation buffers
            for (int ch = 0; ch < channels; ch++) {
                if (channelIsSpatial[ch]) {
                    Array.Clear(reflectionAccum[ch], 0, blockSize);
                }
            }

            // 2. Write each spatial channel's dry signal into its 3-band circular delay lines (exact complementary sum: L + M + H == x)
            int mask = delayBufferMask;
            float aLow = lpLowAlpha;
            float aMid = lpMidAlpha;
            int baseWrite = writeIndex;

            for (int ch = 0; ch < channels; ch++) {
                if (!channelIsSpatial[ch]) {
                    continue;
                }
                float[] src = channelBuffers[ch];
                float[] dL = delayLow[ch];
                float[] dM = delayMid[ch];
                float[] dH = delayHigh[ch];
                float sL = lpLowState[ch];
                float sM = lpMidState[ch];

                for (int s = 0; s < blockSize; s++) {
                    float x = src[s];
                    sL += aLow * (x - sL);
                    sM += aMid * (x - sM);
                    int idx = (baseWrite + s) & mask;
                    dL[idx] = sL;
                    dM[idx] = sM - sL;
                    dH[idx] = x - sM;
                }

                lpLowState[ch] = MathF.Abs(sL) > 1e-20f ? sL : 0f;
                lpMidState[ch] = MathF.Abs(sM) > 1e-20f ? sM : 0f;
            }

            // 3. Tap all active 1st/2nd-order image sources and route to head-tracked target virtual HRIR speakers
            int tapCount = activeTapCount;
            for (int srcCh = 0; srcCh < channels; srcCh++) {
                if (!channelIsSpatial[srcCh]) {
                    continue;
                }
                float[] dL = delayLow[srcCh];
                float[] dM = delayMid[srcCh];
                float[] dH = delayHigh[srcCh];
                IsmReflectionTap[] taps = tapTable[srcCh];

                for (int t = 0; t < tapCount; t++) {
                    IsmReflectionTap tap = taps[t];
                    int dstA = tap.TargetChannelA;
                    if (dstA < 0) {
                        continue;
                    }
                    int dstB = tap.TargetChannelB;
                    float wA = tap.WeightA;
                    float wB = tap.WeightB;
                    float gL = tap.GainLow;
                    float gM = tap.GainMid;
                    float gH = tap.GainHigh;

                    int dInt = (int)tap.DelaySamples;
                    float frac = tap.DelaySamples - dInt;
                    float oneMinusFrac = 1f - frac;

                    float[] accA = reflectionAccum[dstA];
                    float[] accB = dstB >= 0 && wB > 1e-4f ? reflectionAccum[dstB] : null;

                    for (int s = 0; s < blockSize; s++) {
                        int r0 = (baseWrite + s - dInt) & mask;
                        int r1 = (r0 - 1) & mask;

                        float vL = dL[r0] * oneMinusFrac + dL[r1] * frac;
                        float vM = dM[r0] * oneMinusFrac + dM[r1] * frac;
                        float vH = dH[r0] * oneMinusFrac + dH[r1] * frac;
                        float refl = vL * gL + vM * gM + vH * gH;

                        accA[s] += refl * wA;
                        if (accB != null) {
                            accB[s] += refl * wB;
                        }
                    }
                }
            }

            // 4. Advance circular write cursor and mix feed-forward reflections into pre-HRIR channel buffers
            writeIndex = (baseWrite + blockSize) & mask;
            for (int ch = 0; ch < channels; ch++) {
                if (!channelIsSpatial[ch]) {
                    continue;
                }
                float[] dst = channelBuffers[ch];
                float[] acc = reflectionAccum[ch];
                for (int s = 0; s < blockSize; s++) {
                    dst[s] += acc[s];
                }
            }
        }

        void EnsureAllocated(int channels, int blockSize, int sampleRate, VirtualChannel[] activeChannels) {
            int requiredDelay = Math.Max(2048, (int)Math.Ceiling(maxDelaySeconds * sampleRate));
            int pow2Delay = 1 << QMath.Log2Ceil(requiredDelay);

            bool needsRealloc = delayLow == null || configuredChannels != channels || delayBufferLength != pow2Delay || configuredSampleRate != sampleRate;
            if (needsRealloc) {
                configuredChannels = channels;
                configuredSampleRate = sampleRate;
                delayBufferLength = pow2Delay;
                delayBufferMask = pow2Delay - 1;
                writeIndex = 0;

                delayLow = new float[channels][];
                delayMid = new float[channels][];
                delayHigh = new float[channels][];
                reflectionAccum = new float[channels][];
                tapTable = new IsmReflectionTap[channels][];
                for (int i = 0; i < channels; i++) {
                    delayLow[i] = new float[pow2Delay];
                    delayMid[i] = new float[pow2Delay];
                    delayHigh[i] = new float[pow2Delay];
                    reflectionAccum[i] = new float[blockSize];
                    tapTable[i] = new IsmReflectionTap[Order2ReflectionCount];
                }
                lpLowState = new float[channels];
                lpMidState = new float[channels];
                channelHeadUnits = new Vector3[channels];
                channelIsSpatial = new bool[channels];

                // Exact 1-pole RC alphas for complementary 3-band split
                lpLowAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * lowMidCrossHz / sampleRate));
                lpMidAlpha = (float)(1.0 - Math.Exp(-2.0 * Math.PI * midHighCrossHz / sampleRate));
                geometryDirty = true;
            } else if (reflectionAccum[0].Length != blockSize) {
                for (int i = 0; i < channels; i++) {
                    reflectionAccum[i] = new float[blockSize];
                }
            }

            // Refresh channel unit vectors whenever layout may have updated
            for (int i = 0; i < channels; i++) {
                VirtualChannel vc = activeChannels[i];
                bool spatial = vc.active && !vc.isLfe;
                Vector3 unit = SphericalToUnit(vc.x, vc.y);
                if (channelIsSpatial[i] != spatial || Vector3.DistanceSquared(channelHeadUnits[i], unit) > 1e-5f) {
                    channelIsSpatial[i] = spatial;
                    channelHeadUnits[i] = unit;
                    geometryDirty = true;
                }
            }
        }

        void RebuildReflectionTaps(int channels, int sampleRate, Vector3 listenerRoomPos, Vector3 listenerRotationDeg) {
            Vector3 dims = geometry.DimensionsMeters;
            Vector3 half = geometry.HalfDimensionsMeters;
            double c = AirAbsorptionFilter.ComputeSpeedOfSound(airTempC);
            float maxDelaySamp = delayBufferLength - 4f;

            geometry.WallAbsorption.GetReflectionCoefficients(out float rWallL, out float rWallM, out float rWallH);
            geometry.FloorAbsorption.GetReflectionCoefficients(out float rFloorL, out float rFloorM, out float rFloorH);
            geometry.CeilingAbsorption.GetReflectionCoefficients(out float rCeilL, out float rCeilM, out float rCeilH);

            // Physical speaker ring radius: fits comfortably inside room walls (78% of smallest wall margin)
            float minHalf = Math.Min(half.X, Math.Min(half.Y, half.Z));
            float baseRadius = Math.Clamp(VirtualizerFilter.ReferenceDistance > 0.5f ? VirtualizerFilter.ReferenceDistance : 1.5f, 1.0f, 3.5f);
            float speakerRadius = Math.Min(baseRadius, Math.Max(0.55f, minHalf * 0.78f));

            activeTapCount = ActiveReflectionCount;

            for (int srcCh = 0; srcCh < channels; srcCh++) {
                if (!channelIsSpatial[srcCh]) {
                    continue;
                }

                // Virtual channel audio arrived from head-relative unit vector -> rotate to world room coordinates
                Vector3 worldDir = channelHeadUnits[srcCh].Rotate(listenerRotationDeg);
                Vector3 srcPos = listenerRoomPos + worldDir * speakerRadius;
                srcPos.X = Math.Clamp(srcPos.X, -(half.X - 0.15f), half.X - 0.15f);
                srcPos.Y = Math.Clamp(srcPos.Y, -(half.Y - 0.15f), half.Y - 0.15f);
                srcPos.Z = Math.Clamp(srcPos.Z, -(half.Z - 0.15f), half.Z - 0.15f);

                float directDist = Math.Max(0.25f, Vector3.Distance(srcPos, listenerRoomPos));
                IsmReflectionTap[] taps = tapTable[srcCh];

                for (int t = 0; t < activeTapCount; t++) {
                    int nx = imageNx[t];
                    int ny = imageNy[t];
                    int nz = imageNz[t];

                    Vector3 imgPos = new Vector3(
                        ComputeMirrorCoordinate(srcPos.X, dims.X, nx),
                        ComputeMirrorCoordinate(srcPos.Y, dims.Y, ny),
                        ComputeMirrorCoordinate(srcPos.Z, dims.Z, nz));

                    Vector3 deltaVec = imgPos - listenerRoomPos;
                    float reflDist = deltaVec.Length();
                    if (reflDist < directDist + 0.02f) {
                        reflDist = directDist + 0.02f;
                    }

                    float extraPathMeters = reflDist - directDist;
                    float delaySamples = (float)(extraPathMeters / c * sampleRate);
                    delaySamples = Math.Clamp(delaySamples, 1f, maxDelaySamp);

                    // Inverse-distance pressure scaling relative to direct sound
                    float distGain = (directDist / reflDist) * reflectionLevel;

                    // Exact per-wall reflection factor product
                    int wallBounces = Math.Abs(nx) + Math.Abs(nz);
                    float refL = PowInt(rWallL, wallBounces);
                    float refM = PowInt(rWallM, wallBounces);
                    float refH = PowInt(rWallH, wallBounces);

                    if (ny != 0) {
                        int absY = Math.Abs(ny);
                        if (absY == 1) {
                            if (ny < 0) { // Floor bounce
                                refL *= rFloorL;
                                refM *= rFloorM;
                                refH *= rFloorH;
                            } else { // Ceiling bounce
                                refL *= rCeilL;
                                refM *= rCeilM;
                                refH *= rCeilH;
                            }
                        } else { // 2nd-order floor + ceiling bounce
                            refL *= rFloorL * rCeilL;
                            refM *= rFloorM * rCeilM;
                            refH *= rFloorH * rCeilH;
                        }
                    }

                    // ISO 9613-1 atmospheric absorption across extra reflection path
                    float airL = 1f, airM = 1f, airH = 1f;
                    if (airAbsorptionOnReflections && extraPathMeters > 0.1f) {
                        airL = (float)AirAbsorptionFilter.ComputeLinearGain(250.0, extraPathMeters, airTempC, airRhPct);
                        airM = (float)AirAbsorptionFilter.ComputeLinearGain(1200.0, extraPathMeters, airTempC, airRhPct);
                        airH = (float)AirAbsorptionFilter.ComputeLinearGain(6000.0, extraPathMeters, airTempC, airRhPct);
                    }

                    // Transform arrival direction from world room frame back into listener head frame
                    Vector3 arrivalWorld = deltaVec / reflDist;
                    Vector3 arrivalHead = arrivalWorld.RotateInverse(listenerRotationDeg);
                    FindNearestTwoChannels(arrivalHead, channels, out int chA, out int chB, out float wA, out float wB);

                    taps[t] = new IsmReflectionTap {
                        DelaySamples = delaySamples,
                        GainLow = distGain * refL * airL,
                        GainMid = distGain * refM * airM,
                        GainHigh = distGain * refH * airH,
                        TargetChannelA = chA,
                        TargetChannelB = chB,
                        WeightA = wA,
                        WeightB = wB
                    };
                }
            }
        }

        void FindNearestTwoChannels(Vector3 dirHead, int channels, out int bestA, out int bestB, out float weightA, out float weightB) {
            bestA = -1;
            bestB = -1;
            float dotA = -2f;
            float dotB = -2f;

            for (int i = 0; i < channels; i++) {
                if (!channelIsSpatial[i]) {
                    continue;
                }
                float d = Vector3.Dot(dirHead, channelHeadUnits[i]);
                if (d > dotA) {
                    dotB = dotA;
                    bestB = bestA;
                    dotA = d;
                    bestA = i;
                } else if (d > dotB) {
                    dotB = d;
                    bestB = i;
                }
            }

            if (bestA < 0) {
                weightA = 0f;
                weightB = 0f;
                return;
            }
            if (bestB < 0 || dotB <= 0.2f) {
                weightA = 1f;
                weightB = 0f;
                return;
            }

            // Smooth equal-power interpolation between the two closest virtual HRIR speakers
            float aScore = Math.Max(0.001f, (dotA + 1f) * 0.5f);
            float bScore = Math.Max(0.001f, (dotB + 1f) * 0.5f);
            aScore *= aScore * aScore;
            bScore *= bScore * bScore;
            float norm = 1f / MathF.Sqrt(aScore * aScore + bScore * bScore);
            weightA = aScore * norm;
            weightB = bScore * norm;
        }

        static Vector3 SphericalToUnit(float elevDeg, float azimDeg) {
            float xRad = elevDeg * VectorExtensions.Deg2Rad;
            float yRad = azimDeg * VectorExtensions.Deg2Rad;
            float cosX = MathF.Cos(xRad);
            return new Vector3(MathF.Sin(yRad) * cosX, -MathF.Sin(xRad), MathF.Cos(yRad) * cosX);
        }

        static float PowInt(float value, int exp) {
            if (exp <= 0) {
                return 1f;
            }
            if (exp == 1) {
                return value;
            }
            if (exp == 2) {
                return value * value;
            }
            float res = 1f;
            for (int i = 0; i < exp; i++) {
                res *= value;
            }
            return res;
        }
    }
}
