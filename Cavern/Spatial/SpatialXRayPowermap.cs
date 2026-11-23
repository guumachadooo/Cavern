using System;
using System.Collections.Generic;
using System.Numerics;

namespace Cavern.Spatial {
    /// <summary>
    /// Spectral frequency band filter for the W13 3D Spatial X-Ray spherical energy powermap.
    /// </summary>
    public enum SpatialXRayBandMode {
        /// <summary>Full-band combined RGB acoustic energy (Low=Warm Amber, Mid=Emerald/Cyan, High=Blue/Violet).</summary>
        AllBands = 0,
        /// <summary>Low-frequency band (&lt; 250 Hz, bass &amp; LFE radiation).</summary>
        LowOnly = 1,
        /// <summary>Mid-frequency band (250 Hz – 2.5 kHz, vocals &amp; fundamental body).</summary>
        MidOnly = 2,
        /// <summary>High-frequency band (&gt; 2.5 kHz, air, cymbals, transients &amp; SPX/Coupling zone).</summary>
        HighOnly = 3
    }

    /// <summary>
    /// Zero-GC 3D spherical frequency-band directional energy powermap for the W13 Studio Spatial X-Ray Inspector,
    /// inspired by SPARTA Powermap and Flux SPAT Revolution acoustic visualization.
    /// Projects active <see cref="SpatialSourceDescriptor"/>s onto a 12 × 6 azimuth/elevation spherical grid
    /// relative to the listener's 6DoF pose using pure raised-cosine directional kernels (zero fake/random noise).
    /// Strict C# 8.0 (<c>netstandard2.1</c>).
    /// </summary>
    public sealed class SpatialXRayPowermap {
        /// <summary>Number of azimuth cells covering [-180°; +180°).</summary>
        public const int AzimuthBins = 12;

        /// <summary>Number of elevation cells covering [-75°; +75°].</summary>
        public const int ElevationBins = 6;

        /// <summary>Total spherical cells (<see cref="AzimuthBins"/> * <see cref="ElevationBins"/> = 72).</summary>
        public const int TotalCells = AzimuthBins * ElevationBins;

        readonly Vector3[] cellDirections = new Vector3[TotalCells];
        readonly float[] lowGrid = new float[TotalCells];
        readonly float[] midGrid = new float[TotalCells];
        readonly float[] highGrid = new float[TotalCells];
        readonly float[] totalGrid = new float[TotalCells];

        float peakCellEnergy;
        Vector3 dominantDirection = new Vector3(0f, 0f, 1f);

        /// <summary>Precomputed unit direction vector for each spherical cell in room coordinates.</summary>
        public Vector3[] CellDirections {
            get { return cellDirections; }
        }

        /// <summary>Low-band (&lt; 250 Hz) energy per spherical cell in [0; 1].</summary>
        public float[] LowEnergyGrid {
            get { return lowGrid; }
        }

        /// <summary>Mid-band (250 Hz – 2.5 kHz) energy per spherical cell in [0; 1].</summary>
        public float[] MidEnergyGrid {
            get { return midGrid; }
        }

        /// <summary>High-band (&gt; 2.5 kHz) energy per spherical cell in [0; 1].</summary>
        public float[] HighEnergyGrid {
            get { return highGrid; }
        }

        /// <summary>Total combined energy per spherical cell in [0; 1].</summary>
        public float[] TotalEnergyGrid {
            get { return totalGrid; }
        }

        /// <summary>Maximum unnormalized directional cell energy in the latest frame.</summary>
        public float PeakCellEnergy {
            get { return peakCellEnergy; }
        }

        /// <summary>Energy-weighted dominant Direction-of-Arrival unit vector in room coordinates.</summary>
        public Vector3 DominantDirection {
            get { return dominantDirection; }
        }

        /// <summary>
        /// Returns the unnormalized directional energy for <paramref name="cellIndex"/> in the selected frequency
        /// <paramref name="mode"/> (compatible with dividing by <see cref="PeakCellEnergy"/>).
        /// </summary>
        public float GetCellEnergy(int cellIndex, SpatialXRayBandMode mode) {
            if (cellIndex < 0 || cellIndex >= TotalCells) {
                return 0f;
            }
            switch (mode) {
                case SpatialXRayBandMode.LowOnly:
                    return lowGrid[cellIndex];
                case SpatialXRayBandMode.MidOnly:
                    return midGrid[cellIndex];
                case SpatialXRayBandMode.HighOnly:
                    return highGrid[cellIndex];
                default:
                    return lowGrid[cellIndex] + midGrid[cellIndex] + highGrid[cellIndex];
            }
        }

        /// <summary>
        /// Precomputes the 72 spherical unit direction vectors once at construction (zero runtime allocations).
        /// </summary>
        public SpatialXRayPowermap() {
            const float degToRad = (float)(Math.PI / 180.0);
            int idx = 0;
            for (int e = 0; e < ElevationBins; e++) {
                float elevDeg = -62.5f + e * 25f; // -62.5, -37.5, -12.5, +12.5, +37.5, +62.5 deg
                float elevRad = elevDeg * degToRad;
                float cosE = (float)Math.Cos(elevRad);
                float sinE = (float)Math.Sin(elevRad);

                for (int a = 0; a < AzimuthBins; a++) {
                    float azDeg = -180f + (a + 0.5f) * (360f / AzimuthBins);
                    float azRad = azDeg * degToRad;
                    cellDirections[idx] = new Vector3(
                        (float)Math.Sin(azRad) * cosE,
                        sinE,
                        (float)Math.Cos(azRad) * cosE);
                    idx++;
                }
            }
        }

        /// <summary>
        /// Computes 3-band Low (&lt; 250 Hz), Mid (250–2500 Hz), and High (&gt; 2500 Hz) RMS energies
        /// from a single audio block using exact zero-GC 1st-order crossover estimators (no allocations).
        /// </summary>
        public static void EstimateThreeBandRms(
            float[] samples,
            int sampleRate,
            out float lowRms,
            out float midRms,
            out float highRms) {
            lowRms = 0f;
            midRms = 0f;
            highRms = 0f;
            if (samples == null || samples.Length == 0 || sampleRate <= 0) {
                return;
            }

            float alphaLow = 1f - (float)Math.Exp(-2.0 * Math.PI * 250.0 / sampleRate);
            float alphaHigh = 1f - (float)Math.Exp(-2.0 * Math.PI * 2500.0 / sampleRate);

            float lpLow = 0f;
            float lpMid = 0f;
            double sumLow = 0.0;
            double sumMid = 0.0;
            double sumHigh = 0.0;

            for (int i = 0; i < samples.Length; i++) {
                float raw = samples[i];
                float x = IsFinite(raw) ? raw : 0f;
                lpLow += alphaLow * (x - lpLow);
                lpMid += alphaHigh * (x - lpMid);
                float mid = lpMid - lpLow;
                float high = x - lpMid;

                sumLow += lpLow * lpLow;
                sumMid += mid * mid;
                sumHigh += high * high;
            }

            double invN = 1.0 / samples.Length;
            float l = (float)Math.Sqrt(sumLow * invN);
            float m = (float)Math.Sqrt(sumMid * invN);
            float h = (float)Math.Sqrt(sumHigh * invN);
            lowRms = IsFinite(l) ? l : 0f;
            midRms = IsFinite(m) ? m : 0f;
            highRms = IsFinite(h) ? h : 0f;
        }

        /// <summary>
        /// Updates the 72-cell spherical 3-band energy powermap in place without heap allocations.
        /// </summary>
        public void Update(
            IReadOnlyList<SpatialSourceDescriptor> sources,
            Vector3 listenerWorldMeters,
            float temporalSmoothing = 0.45f) {
            float safeSmoothing = IsFinite(temporalSmoothing) ? temporalSmoothing : 0.45f;
            float smooth = Math.Max(0f, Math.Min(0.92f, safeSmoothing));
            float keep = smooth;
            float inject = 1f - smooth;

            if (!IsFiniteVector(listenerWorldMeters)) {
                listenerWorldMeters = Vector3.Zero;
            }

            for (int c = 0; c < TotalCells; c++) {
                lowGrid[c] = IsFinite(lowGrid[c]) ? lowGrid[c] * keep : 0f;
                midGrid[c] = IsFinite(midGrid[c]) ? midGrid[c] * keep : 0f;
                highGrid[c] = IsFinite(highGrid[c]) ? highGrid[c] * keep : 0f;
            }

            if (sources == null || sources.Count == 0) {
                peakCellEnergy = 0f;
                for (int c = 0; c < TotalCells; c++) {
                    totalGrid[c] = 0f;
                }
                return;
            }

            bool anySolo = false;
            for (int i = 0; i < sources.Count; i++) {
                if (sources[i].IsSolo) {
                    anySolo = true;
                    break;
                }
            }

            Vector3 weightedDir = Vector3.Zero;
            float totalWeight = 0f;

            for (int i = 0; i < sources.Count; i++) {
                SpatialSourceDescriptor src = sources[i];
                if (src.IsMuted || src.IsDynamicallyFilteredOut || (anySolo && !src.IsSolo)) {
                    continue;
                }

                float rms = src.RmsLevel;
                if (!IsFinite(rms) || rms <= 1e-4f || !IsFiniteVector(src.WorldPositionMeters)) {
                    continue;
                }

                Vector3 rel = src.WorldPositionMeters - listenerWorldMeters;
                float distSq = rel.LengthSquared();
                if (!IsFinite(distSq)) {
                    continue;
                }
                Vector3 dir = distSq > 1e-6f ? rel / (float)Math.Sqrt(distSq) : new Vector3(0f, 0f, 1f);
                if (!IsFiniteVector(dir)) {
                    continue;
                }

                float eLow = IsFinite(src.LowBandEnergy) && src.LowBandEnergy > 0f ? src.LowBandEnergy : (src.IsLfe ? rms : rms * 0.35f);
                float eMid = IsFinite(src.MidBandEnergy) && src.MidBandEnergy > 0f ? src.MidBandEnergy : (src.IsLfe ? rms * 0.12f : rms * 0.65f);
                float eHigh = IsFinite(src.HighBandEnergy) && src.HighBandEnergy > 0f ? src.HighBandEnergy : (src.IsLfe ? 0f : rms * 0.40f);

                // Acoustic power scales as RMS^2; diffuse sources widen the directional lobe
                float diffuse = IsFinite(src.Diffuse) ? Math.Max(0f, Math.Min(1f, src.Diffuse)) : 0f;
                float sharpness = 5.5f - diffuse * 3.0f;
                float pLow = eLow * eLow * inject;
                float pMid = eMid * eMid * inject;
                float pHigh = eHigh * eHigh * inject;

                float totalPow = pLow + pMid + pHigh;
                if (!IsFinite(totalPow) || totalPow <= 0f) {
                    continue;
                }
                weightedDir += dir * totalPow;
                totalWeight += totalPow;

                for (int c = 0; c < TotalCells; c++) {
                    float dot = Vector3.Dot(dir, cellDirections[c]);
                    if (!IsFinite(dot) || dot <= 0.05f) {
                        continue;
                    }
                    // Raised-cosine directional kernel (order ~5.5 for point sources, ~2.5 for diffuse)
                    float lobe = (float)Math.Pow(dot, sharpness);
                    if (!IsFinite(lobe)) {
                        continue;
                    }
                    lowGrid[c] += pLow * lobe;
                    midGrid[c] += pMid * lobe;
                    highGrid[c] += pHigh * lobe;
                }
            }

            // Energy-gated exponential smoothing on dominant Direction-of-Arrival vector (prevents 187 Hz block jitter)
            float dirAlpha = Math.Max(0.04f, Math.Min(0.28f, inject * 0.22f));
            if (IsFinite(totalWeight) && totalWeight > 1e-6f && IsFiniteVector(weightedDir) && weightedDir.LengthSquared() > 1e-8f) {
                Vector3 normDir = Vector3.Normalize(weightedDir);
                if (IsFiniteVector(normDir)) {
                    Vector3 blended = dominantDirection * (1f - dirAlpha) + normDir * dirAlpha;
                    float lenSq = blended.LengthSquared();
                    if (IsFinite(lenSq) && lenSq > 1e-8f) {
                        dominantDirection = Vector3.Normalize(blended);
                    } else {
                        dominantDirection = normDir;
                    }
                }
            }

            float maxEnergy = 1e-6f;
            for (int c = 0; c < TotalCells; c++) {
                float sum = lowGrid[c] + midGrid[c] + highGrid[c];
                if (!IsFinite(sum) || sum < 0f) {
                    lowGrid[c] = 0f;
                    midGrid[c] = 0f;
                    highGrid[c] = 0f;
                    sum = 0f;
                }
                totalGrid[c] = sum;
                if (sum > maxEnergy) {
                    maxEnergy = sum;
                }
            }
            // Fast attack / smooth release envelope on normalization peak so heatmap brightness never strobes
            if (!IsFinite(peakCellEnergy) || peakCellEnergy <= 1e-6f) {
                peakCellEnergy = maxEnergy;
            } else if (maxEnergy > peakCellEnergy) {
                peakCellEnergy += (maxEnergy - peakCellEnergy) * 0.30f;
            } else {
                peakCellEnergy += (maxEnergy - peakCellEnergy) * 0.06f;
            }
            float normPeak = Math.Max(0.02f, Math.Max(maxEnergy, peakCellEnergy));

            // Compress dynamic range smoothly for visual readability while keeping relative proportions physical
            float invNorm = 1f / normPeak;
            for (int c = 0; c < TotalCells; c++) {
                float normVal = (float)Math.Sqrt(Math.Min(1f, totalGrid[c] * invNorm));
                totalGrid[c] = IsFinite(normVal) ? normVal : 0f;
            }
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        static bool IsFiniteVector(Vector3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);
    }
}
