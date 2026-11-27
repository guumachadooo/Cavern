using System;
using System.Numerics;
using System.Runtime.CompilerServices;

using Cavern.Utilities;

namespace Cavern.Rendering {
    /// <summary>
    /// Mixes <see cref="Source"/>s to <see cref="Listener.Channels"/> based on which channels are boundary to the direction vector.
    /// </summary>
    public class DirectionalRenderer : SourceRenderer {
        /// <summary>
        /// When true, uses ITU-R BS.2127 3D triplet / 2D pair Vector Base Amplitude Panning (VBAP) instead of
        /// Cavern's default directional power-law matching. Defaults to false for exact legacy parity.
        /// </summary>
        public bool UseBs2127Vbap { get; set; }

        /// <summary>
        /// Thread-local scratch buffer eliminating per-block channel gain array allocations while preserving thread safety.
        /// </summary>
        [ThreadStatic]
        static float[] angleMatchScratch;

        /// <summary>
        /// Retrieve or grow the thread-local scratch buffer for channel gain calculations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float[] GetAngleMatchScratch(int channels) {
            float[] scratch = angleMatchScratch;
            if (scratch == null || scratch.Length < channels) {
                scratch = new float[channels];
                angleMatchScratch = scratch;
            }
            return scratch;
        }

        /// <summary>
        /// Angle match calculations (exact <see cref="MathF.Acos"/> or linearized fast mode) into <paramref name="angleMatches"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void CalculateAngleMatches(Channel[] channels, float[] angleMatches, Vector3 direction, bool linearize, bool theatre) {
            float dirMagnitudeRecip = 1f / (direction.Length() + .0001f);
            for (int channel = 0; channel < channels.Length; channel++) {
                Channel currentChannel = channels[channel];
                if (!currentChannel.LFE) {
                    float dot = Vector3.Dot(direction, currentChannel.SphericalPos) * dirMagnitudeRecip;
                    float sample = linearize
                        ? 1.570796326f + 1.570796326f * dot
                        : MathF.PI - MathF.Acos(Math.Clamp(dot, -1f, 1f));
                    sample *= sample;
                    sample *= sample;
                    sample *= sample; // Angle match modifier function is x^8
                    if (theatre) {
                        sample *= sample; // Theatre angle match modifier function is x^16
                    }
                    angleMatches[channel] = sample;
                } else {
                    angleMatches[channel] = 0f;
                }
            }
        }

        /// <summary>
        /// ITU-R BS.2127 3D triplet / 2D pair Vector Base Amplitude Panning (VBAP) into <paramref name="angleMatches"/>.
        /// </summary>
        static void CalculateBs2127Vbap(Channel[] channels, float[] angleMatches, Vector3 direction) {
            int count = channels.Length;
            Array.Clear(angleMatches, 0, count);

            int nonLfeCount = 0;
            int singleIdx = -1;
            for (int i = 0; i < count; i++) {
                if (!channels[i].LFE) {
                    nonLfeCount++;
                    singleIdx = i;
                }
            }
            if (nonLfeCount == 0) {
                return;
            }
            if (nonLfeCount == 1) {
                angleMatches[singleIdx] = 1f;
                return;
            }

            float dirLen = direction.Length();
            if (dirLen <= 1e-6f) {
                float uniform = 1f / MathF.Sqrt(nonLfeCount);
                for (int i = 0; i < count; i++) {
                    if (!channels[i].LFE) {
                        angleMatches[i] = uniform;
                    }
                }
                return;
            }

            Vector3 u = direction / dirLen;
            for (int i = 0; i < count; i++) {
                if (!channels[i].LFE && Vector3.Dot(u, channels[i].SphericalPos) >= 0.99999f) {
                    angleMatches[i] = 1f;
                    return;
                }
            }

            // 3D triplet search (Pulkki 1997 / ITU-R BS.2127)
            int bestI = -1, bestJ = -1, bestK = -1;
            float bestWA = 0f, bestWB = 0f, bestWC = 0f;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < count - 2; i++) {
                if (channels[i].LFE) {
                    continue;
                }
                Vector3 a = channels[i].SphericalPos;
                float ua = Vector3.Dot(u, a);
                for (int j = i + 1; j < count - 1; j++) {
                    if (channels[j].LFE) {
                        continue;
                    }
                    Vector3 b = channels[j].SphericalPos;
                    float ub = Vector3.Dot(u, b);
                    for (int k = j + 1; k < count; k++) {
                        if (channels[k].LFE) {
                            continue;
                        }
                        Vector3 c = channels[k].SphericalPos;
                        Vector3 bc = Vector3.Cross(b, c);
                        float det = Vector3.Dot(a, bc);
                        if (Math.Abs(det) <= 1e-4f) {
                            continue;
                        }
                        float invDet = 1f / det;
                        float wA = Vector3.Dot(u, bc) * invDet;
                        float wB = Vector3.Dot(a, Vector3.Cross(u, c)) * invDet;
                        float wC = Vector3.Dot(a, Vector3.Cross(b, u)) * invDet;
                        if (wA >= -1e-4f && wB >= -1e-4f && wC >= -1e-4f) {
                            float uc = Vector3.Dot(u, c);
                            float minDot = Math.Min(ua, Math.Min(ub, uc));
                            float score = minDot + 0.01f * (ua + ub + uc);
                            if (score > bestScore) {
                                bestScore = score;
                                bestI = i;
                                bestJ = j;
                                bestK = k;
                                bestWA = Math.Max(0f, wA);
                                bestWB = Math.Max(0f, wB);
                                bestWC = Math.Max(0f, wC);
                            }
                        }
                    }
                }
            }

            if (bestI >= 0) {
                float norm = MathF.Sqrt(bestWA * bestWA + bestWB * bestWB + bestWC * bestWC);
                if (norm > 1e-6f) {
                    float invNorm = 1f / norm;
                    angleMatches[bestI] = bestWA * invNorm;
                    angleMatches[bestJ] = bestWB * invNorm;
                    angleMatches[bestK] = bestWC * invNorm;
                    return;
                }
            }

            // 2D pair VBAP fallback (planar layouts or outside 3D hull)
            bestI = -1;
            bestJ = -1;
            bestScore = float.NegativeInfinity;
            for (int i = 0; i < count - 1; i++) {
                if (channels[i].LFE) {
                    continue;
                }
                Vector3 a = channels[i].SphericalPos;
                float ua = Vector3.Dot(u, a);
                for (int j = i + 1; j < count; j++) {
                    if (channels[j].LFE) {
                        continue;
                    }
                    Vector3 b = channels[j].SphericalPos;
                    float ab = Vector3.Dot(a, b);
                    float det2 = 1f - ab * ab;
                    if (det2 <= 1e-4f) {
                        continue;
                    }
                    float ub = Vector3.Dot(u, b);
                    float wA = (ua - ab * ub) / det2;
                    float wB = (ub - ab * ua) / det2;
                    if (wA >= -1e-4f && wB >= -1e-4f && (wA * ua + wB * ub) > 0f) {
                        float score = Math.Min(ua, ub) + 0.01f * (ua + ub);
                        if (score > bestScore) {
                            bestScore = score;
                            bestI = i;
                            bestJ = j;
                            bestWA = Math.Max(0f, wA);
                            bestWB = Math.Max(0f, wB);
                        }
                    }
                }
            }

            if (bestI >= 0) {
                float norm = MathF.Sqrt(bestWA * bestWA + bestWB * bestWB);
                if (norm > 1e-6f) {
                    float invNorm = 1f / norm;
                    angleMatches[bestI] = bestWA * invNorm;
                    angleMatches[bestJ] = bestWB * invNorm;
                    return;
                }
            }

            // Nearest speaker fallback (symmetric tie split)
            float maxDot = float.NegativeInfinity;
            for (int i = 0; i < count; i++) {
                if (!channels[i].LFE) {
                    float d = Vector3.Dot(u, channels[i].SphericalPos);
                    if (d > maxDot) {
                        maxDot = d;
                    }
                }
            }
            int ties = 0;
            for (int i = 0; i < count; i++) {
                if (!channels[i].LFE && maxDot - Vector3.Dot(u, channels[i].SphericalPos) <= 1e-4f) {
                    angleMatches[i] = 1f;
                    ties++;
                }
            }
            if (ties > 1) {
                float tieGain = 1f / MathF.Sqrt(ties);
                for (int i = 0; i < count; i++) {
                    if (angleMatches[i] != 0f) {
                        angleMatches[i] = tieGain;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override void Render(Listener listener, Source source, Vector3 direction, float[] samples, float[] rendered, float gain) {
            if (source.LFE || !listener.LFESeparation) {
                MixToLFE(listener, source.LFE, samples, rendered, gain);
                if (source.LFE) {
                    return;
                }
            }

            // Angle match calculations (zero-alloc reusable scratch)
            ListenerContext ctx = listener != null ? listener.Context : ListenerContext.Current;
            Channel[] channels = ctx.Channels;
            float[] angleMatches = GetAngleMatchScratch(channels.Length);
            bool theatre = ctx.EnvironmentType == Environments.Theatre;
            if (UseBs2127Vbap) {
                CalculateBs2127Vbap(channels, angleMatches, direction);
            } else {
                CalculateAngleMatches(channels, angleMatches, direction, listener.AudioQuality < QualityModes.High, theatre);
            }

            // Object size extension
            if (source.Size != 0) {
                if (UseBs2127Vbap) {
                    int nonLfeCount = 0;
                    for (int channel = 0; channel < channels.Length; channel++) {
                        if (!channels[channel].LFE) {
                            nonLfeCount++;
                        }
                    }
                    float uniform = nonLfeCount > 0 ? 1f / MathF.Sqrt(nonLfeCount) : 0f;
                    for (int channel = 0; channel < channels.Length; channel++) {
                        if (!channels[channel].LFE) {
                            angleMatches[channel] = QMath.Lerp(angleMatches[channel], uniform, source.Size);
                        }
                    }
                } else {
                    float maxAngleMatch = angleMatches[0];
                    for (int channel = 1; channel < channels.Length; channel++) {
                        if (maxAngleMatch < angleMatches[channel]) {
                            maxAngleMatch = angleMatches[channel];
                        }
                    }
                    for (int channel = 0; channel < channels.Length; channel++) {
                        angleMatches[channel] = QMath.Lerp(angleMatches[channel], maxAngleMatch, source.Size);
                    }
                }
            }

            // Only use the closest 3 speakers on non-Perfect qualities or in Theatre mode (legacy directional mode)
            if (!UseBs2127Vbap && (listener.AudioQuality != QualityModes.Perfect || theatre)) {
                float top0 = 0, top1 = 0, top2 = 0;
                for (int channel = 0; channel < channels.Length; channel++) {
                    if (!channels[channel].LFE) {
                        float match = angleMatches[channel];
                        if (top0 < match) {
                            top2 = top1;
                            top1 = top0;
                            top0 = match;
                        } else if (top1 < match) {
                            top2 = top1;
                            top1 = match;
                        } else if (top2 < match) {
                            top2 = match;
                        }
                    }
                }
                for (int channel = 0; channel < channels.Length; channel++) {
                    if (!channels[channel].LFE && angleMatches[channel] != top0 && angleMatches[channel] != top1 && angleMatches[channel] != top2) {
                        angleMatches[channel] = 0;
                    }
                }
            }
            float totalAngleMatch = 0;
            for (int channel = 0; channel < channels.Length; channel++) {
                totalAngleMatch += angleMatches[channel] * angleMatches[channel];
            }
            totalAngleMatch = MathF.Sqrt(totalAngleMatch);
            if (totalAngleMatch <= 1e-12f) {
                return;
            }

            // Place in sphere, write data to output channels
            float gain3D = gain / totalAngleMatch;
            for (int channel = 0; channel < channels.Length; channel++) {
                if (!channels[channel].LFE && angleMatches[channel] != 0) {
                    WaveformUtils.Mix(samples, rendered, channel, channels.Length, gain3D * angleMatches[channel]);
                }
            }
        }
    }
}
