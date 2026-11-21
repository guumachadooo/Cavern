using System;
using System.Collections.Generic;

using Cavern.Channels;
using Cavern.Utilities;
using Cavern.Waveforms;

namespace Cavern.Virtualizer {
    /// <summary>
    /// Raw measured virtual-speaker HRIR/BRIR pair extracted from an Impulcifer, HeSuVi, SOFA, or Cavern WAV file
    /// prior to optional Slot 5B personalization (MagLS, Minimum-Phase + ITD, Woodworth-Schlosberg Biometric ITD).
    /// </summary>
    public sealed class MeasuredHrirSpeaker {
        /// <summary>Cavern vertical elevation angle in degrees (-45 = up, 0 = horizontal).</summary>
        public float ElevationDeg { get; }

        /// <summary>Cavern clockwise azimuth angle in degrees (-90 = left, +90 = right, 0 = front).</summary>
        public float AzimuthDeg { get; }

        /// <summary>Left-ear measured impulse response.</summary>
        public float[] LeftIr { get; }

        /// <summary>Right-ear measured impulse response.</summary>
        public float[] RightIr { get; }

        /// <summary>
        /// Creates a measured virtual-speaker impulse pair.
        /// </summary>
        public MeasuredHrirSpeaker(float elevationDeg, float azimuthDeg, float[] leftIr, float[] rightIr) {
            ElevationDeg = elevationDeg;
            AzimuthDeg = azimuthDeg;
            LeftIr = leftIr;
            RightIr = rightIr;
        }
    }

    /// <summary>
    /// Parsed HRIR/BRIR dataset returned by <see cref="ImpulciferHrirParser"/>.
    /// </summary>
    public sealed class ParsedWavHrirSet {
        /// <summary>Detected WAV HRIR convention label (e.g., "Impulcifer HeSuVi 14-ch", "Impulcifer 16-ch", "Cavern Stereo HRIR").</summary>
        public string FormatName { get; }

        /// <summary>Sample rate of the parsed impulse responses in Hz.</summary>
        public int SampleRate { get; }

        /// <summary>Maximum impulse tap length across all virtual speakers.</summary>
        public int TapCount { get; }

        /// <summary>True when the parsed impulses exceed <see cref="MinimumPhaseHrir.MaxAnechoicTaps"/> (measured room BRIR).</summary>
        public bool IsBrir => TapCount > MinimumPhaseHrir.MaxAnechoicTaps;

        /// <summary>Measured virtual speaker impulse pairs with global unity-energy L2 normalization applied.</summary>
        public MeasuredHrirSpeaker[] Speakers { get; }

        /// <summary>
        /// Creates a parsed WAV HRIR/BRIR result.
        /// </summary>
        public ParsedWavHrirSet(string formatName, int sampleRate, int tapCount, MeasuredHrirSpeaker[] speakers) {
            FormatName = formatName;
            SampleRate = sampleRate;
            TapCount = tapCount;
            Speakers = speakers;
        }
    }

    /// <summary>
    /// Enhanced HRIR/BRIR multichannel WAV parser curated from <c>jaakkopasanen/Impulcifer</c> and <c>HeSuVi</c>
    /// specifications. Supports HeSuVi 14-channel interleaved 7.1 WAV (<c>hesuvi.wav</c>), Impulcifer multichannel
    /// paired WAVs (10/12/14/16/22/24 channels), and standard 2-channel concatenated Cavern HRIR WAVs, preserving exact
    /// measured ITD, ILD, and channel balance via single-scalar global unity-energy L2 normalization.
    /// </summary>
    public static class ImpulciferHrirParser {
        /// <summary>
        /// Standard ITU / Impulcifer 7.1 speaker angles (Cavern convention: X = elevation, Y = clockwise azimuth)
        /// mapped to HeSuVi's exact 14-channel track indices:
        /// 0:FL-L, 1:FL-R, 2:SL-L, 3:SL-R, 4:BL-L, 5:BL-R, 6:FC-L, 7:FR-R, 8:FR-L, 9:SR-R, 10:SR-L, 11:BR-R, 12:BR-L, 13:FC-R.
        /// </summary>
        static readonly (float x, float y, int leftCh, int rightCh)[] heSuVi14Map = new (float, float, int, int)[] {
            (0f, -30f, 0, 1),    // Front Left
            (0f, 30f, 8, 7),     // Front Right (note HeSuVi's ipsilateral-first ch7=FR-R, ch8=FR-L!)
            (0f, 0f, 6, 13),     // Front Center
            (0f, -90f, 2, 3),    // Side Left
            (0f, 90f, 10, 9),    // Side Right (ch9=SR-R, ch10=SR-L)
            (0f, -135f, 4, 5),   // Back Left
            (0f, 135f, 12, 11)   // Back Right (ch11=BR-R, ch12=BR-L)
        };

        /// <summary>
        /// Standard 5.0 / 7.0 / 7.1.4 paired sequential Impulcifer layouts (even channels = Left ear, odd channels = Right ear).
        /// </summary>
        static readonly (float x, float y)[] paired50Angles = new (float, float)[] {
            (0f, -30f), (0f, 30f), (0f, 0f), (0f, -110f), (0f, 110f)
        };

        static readonly (float x, float y)[] paired70Angles = new (float, float)[] {
            (0f, -30f), (0f, 30f), (0f, 0f), (0f, -90f), (0f, 90f), (0f, -135f), (0f, 135f)
        };

        static readonly (float x, float y)[] paired704Angles = new (float, float)[] {
            (0f, -30f), (0f, 30f), (0f, 0f),
            (0f, -90f), (0f, 90f), (0f, -135f), (0f, 135f),
            (-45f, -45f), (-45f, 45f), (-45f, -135f), (-45f, 135f)
        };

        /// <summary>
        /// Parses a multichannel WAV HRIR/BRIR (<c>hesuvi.wav</c>, Impulcifer <c>hrir.wav</c>, or 2-channel Cavern WAV)
        /// into normalized <see cref="MeasuredHrirSpeaker"/> pairs, automatically resampling to <paramref name="targetSampleRate"/>
        /// when requested (e.g., 48000 Hz) so 44.1 kHz / 96 kHz files work seamlessly with <see cref="VirtualizerFilter"/>.
        /// </summary>
        public static ParsedWavHrirSet Parse(MultichannelWaveform waveform, int sampleRate, int targetSampleRate = 0) {
            if (waveform == null || waveform.Channels <= 0) {
                throw new ArgumentException("Empty HRIR waveform.", nameof(waveform));
            }
            if (sampleRate <= 0) {
                throw new ArgumentException("Invalid HRIR sample rate.", nameof(sampleRate));
            }

            waveform.TrimStart();
            int chCount = waveform.Channels;
            List<MeasuredHrirSpeaker> speakers = new List<MeasuredHrirSpeaker>();
            string formatName;

            if (chCount == 14) {
                // Verify whether track 7 (FR-R) has higher energy than track 8 (FR-L) — signature of HeSuVi 14-ch layout
                // vs sequential 7.0 pairs (where ch6=SL-L, ch7=SL-R, etc.).
                bool isHeSuVi = LooksLikeHeSuVi14(waveform);
                if (isHeSuVi) {
                    formatName = "Impulcifer HeSuVi 14-ch (7.1)";
                    for (int i = 0; i < heSuVi14Map.Length; i++) {
                        (float x, float y, int lCh, int rCh) = heSuVi14Map[i];
                        speakers.Add(new MeasuredHrirSpeaker(x, y, CloneTrimmed(waveform[lCh]), CloneTrimmed(waveform[rCh])));
                    }
                } else {
                    formatName = "Impulcifer 14-ch Paired (7.0)";
                    for (int i = 0; i < paired70Angles.Length; i++) {
                        speakers.Add(new MeasuredHrirSpeaker(
                            paired70Angles[i].x,
                            paired70Angles[i].y,
                            CloneTrimmed(waveform[i * 2]),
                            CloneTrimmed(waveform[i * 2 + 1])));
                    }
                }
            } else if (chCount == 2) {
                formatName = "Cavern 2-ch Concatenated HRIR";
                ParseConcatenatedStereo(waveform, speakers);
            } else if (chCount == 10) {
                formatName = "Impulcifer 10-ch Paired (5.0)";
                for (int i = 0; i < paired50Angles.Length; i++) {
                    speakers.Add(new MeasuredHrirSpeaker(
                        paired50Angles[i].x,
                        paired50Angles[i].y,
                        CloneTrimmed(waveform[i * 2]),
                        CloneTrimmed(waveform[i * 2 + 1])));
                }
            } else if (chCount == 12 || chCount == 16 || chCount == 22 || chCount == 24 || chCount == 32) {
                // Paired L/R multichannel layout: skip LFE pair (index 3 in 5.1/7.1/7.1.4 if present)
                (float x, float y)[] layout;
                int lfeSpeakerIndex = -1;
                if (chCount == 12) {
                    formatName = "Impulcifer 12-ch Paired (5.1)";
                    layout = paired50Angles;
                    lfeSpeakerIndex = 3; // FL, FR, FC, [LFE], SL, SR
                } else if (chCount == 16) {
                    formatName = "Impulcifer 16-ch Paired (7.1)";
                    layout = paired70Angles;
                    lfeSpeakerIndex = 3; // FL, FR, FC, [LFE], SL, SR, BL, BR
                } else if (chCount == 22) {
                    formatName = "Impulcifer 22-ch Paired (7.0.4)";
                    layout = paired704Angles;
                } else if (chCount == 24) {
                    formatName = "Impulcifer 24-ch Paired (7.1.4)";
                    layout = paired704Angles;
                    lfeSpeakerIndex = 3;
                } else {
                    formatName = "Cavern 32-ch Paired (16-Speaker 3D)";
                    layout = BuildCavern16Angles();
                }

                int pairCount = chCount / 2;
                int layoutIdx = 0;
                for (int p = 0; p < pairCount && layoutIdx < layout.Length; p++) {
                    if (p == lfeSpeakerIndex) {
                        continue;
                    }
                    speakers.Add(new MeasuredHrirSpeaker(
                        layout[layoutIdx].x,
                        layout[layoutIdx].y,
                        CloneTrimmed(waveform[p * 2]),
                        CloneTrimmed(waveform[p * 2 + 1])));
                    layoutIdx++;
                }
            } else if (chCount % 2 == 0 && chCount >= 4) {
                // Generic even-channel paired horizontal ring
                int pairCount = chCount / 2;
                formatName = $"Multichannel {chCount}-ch Paired HRIR";
                ReferenceChannel[] std = ChannelPrototype.GetStandardMatrix(pairCount);
                for (int p = 0; p < pairCount; p++) {
                    ChannelPrototype proto = ChannelPrototype.Mapping[(int)std[p]];
                    if (proto.LFE) {
                        continue;
                    }
                    speakers.Add(new MeasuredHrirSpeaker(
                        proto.X,
                        proto.Y,
                        CloneTrimmed(waveform[p * 2]),
                        CloneTrimmed(waveform[p * 2 + 1])));
                }
            } else {
                throw new NotSupportedException($"Unsupported HRIR WAV channel count ({chCount}); expected 2, 10, 12, 14 (HeSuVi), 16, 22, 24, or 32 channels.");
            }

            if (speakers.Count == 0) {
                throw new InvalidOperationException("No valid speaker impulses found in WAV HRIR file.");
            }

            int outSampleRate = sampleRate;
            if (targetSampleRate > 0 && targetSampleRate != sampleRate) {
                for (int i = 0; i < speakers.Count; i++) {
                    MeasuredHrirSpeaker sp = speakers[i];
                    float[] resL = ResampleImpulse(sp.LeftIr, sampleRate, targetSampleRate);
                    float[] resR = ResampleImpulse(sp.RightIr, sampleRate, targetSampleRate);
                    speakers[i] = new MeasuredHrirSpeaker(sp.ElevationDeg, sp.AzimuthDeg, resL, resR);
                }
                outSampleRate = targetSampleRate;
            }

            // Single-scalar global unity-energy L2 normalization across all parsed speakers (preserves exact ILD & balance)
            double energySum = 0.0;
            int maxTaps = 0;
            for (int i = 0; i < speakers.Count; i++) {
                float[] l = speakers[i].LeftIr;
                float[] r = speakers[i].RightIr;
                if (l.Length > maxTaps) maxTaps = l.Length;
                if (r.Length > maxTaps) maxTaps = r.Length;
                double e = 0.0;
                for (int n = 0; n < l.Length; n++) e += (double)l[n] * l[n];
                for (int n = 0; n < r.Length; n++) e += (double)r[n] * r[n];
                energySum += e;
            }
            double meanEnergy = energySum / speakers.Count;
            if (meanEnergy > 1e-12) {
                float scale = (float)(1.0 / Math.Sqrt(meanEnergy));
                for (int i = 0; i < speakers.Count; i++) {
                    WaveformUtils.Gain(speakers[i].LeftIr, scale);
                    WaveformUtils.Gain(speakers[i].RightIr, scale);
                }
            }

            return new ParsedWavHrirSet(formatName, outSampleRate, maxTaps, speakers.ToArray());
        }

        /// <summary>
        /// Resamples an impulse response cleanly using Cavern's Catmull-Rom / cubic interpolation with energy preservation.
        /// </summary>
        public static float[] ResampleImpulse(float[] ir, int fromRate, int toRate) {
            if (ir == null || ir.Length == 0 || fromRate <= 0 || toRate <= 0 || fromRate == toRate) {
                return ir != null ? (float[])ir.Clone() : Array.Empty<float>();
            }
            int newLen = Math.Max(16, (int)Math.Round(ir.Length * (double)toRate / fromRate));
            float[] resampled = Resample.CatmullRom(ir, newLen);
            // Preserve L2 impulse energy after sample-rate conversion
            double eIn = 0.0, eOut = 0.0;
            for (int i = 0; i < ir.Length; i++) eIn += (double)ir[i] * ir[i];
            for (int i = 0; i < resampled.Length; i++) eOut += (double)resampled[i] * resampled[i];
            if (eIn > 1e-12 && eOut > 1e-12) {
                float scale = (float)Math.Sqrt(eIn / eOut);
                WaveformUtils.Gain(resampled, scale);
            }
            return resampled;
        }

        /// <summary>
        /// Detects HeSuVi's signature 14-channel layout where channels 7 (FR-R), 9 (SR-R), 11 (BR-R) are ipsilateral
        /// right-ear impulses (higher energy than contralateral channels 8, 10, 12) and channels 6 &amp; 13 are symmetric Front Center.
        /// </summary>
        static bool LooksLikeHeSuVi14(MultichannelWaveform wf) {
            double e0 = Energy(wf[0]), e1 = Energy(wf[1]); // FL-L vs FL-R -> e0 > e1
            double e7 = Energy(wf[7]), e8 = Energy(wf[8]); // HeSuVi: FR-R vs FR-L -> e7 > e8
            double e9 = Energy(wf[9]), e10 = Energy(wf[10]); // HeSuVi: SR-R vs SR-L -> e9 > e10
            double e6 = Energy(wf[6]), e13 = Energy(wf[13]); // HeSuVi: FC-L ~= FC-R
            bool leftDominant0 = e0 > e1 * 1.05;
            bool rightSwapped = (e7 > e8 * 1.05) || (e9 > e10 * 1.05);
            double centerRatio = (e6 > 1e-12 && e13 > 1e-12) ? Math.Max(e6, e13) / Math.Min(e6, e13) : 99.0;
            return (leftDominant0 && rightSwapped) || (rightSwapped && centerRatio < 2.5);
        }

        static void ParseConcatenatedStereo(MultichannelWaveform hrir, List<MeasuredHrirSpeaker> target) {
            hrir.Normalize();
            int lPeak = -1, rPeak = -1;
            float[] l = hrir[0], r = hrir[1];
            for (int i = 0; i < l.Length; i++) {
                if (MathF.Abs(l[i]) > .5f || MathF.Abs(r[i]) > .5f) {
                    if (lPeak == -1) {
                        lPeak = i;
                    } else if (rPeak == -1) {
                        rPeak = i;
                        break;
                    }
                    i += 64;
                }
            }
            int step = rPeak > lPeak ? rPeak - lPeak : l.Length;
            MultichannelWaveform[] splits = hrir.Split(step);
            int lastToKeep = splits.Length;
            while (lastToKeep > 1 && splits[lastToKeep - 1].IsMute()) {
                lastToKeep--;
            }
            if (splits.Length != lastToKeep) {
                MultichannelWaveform[] trimmed = new MultichannelWaveform[lastToKeep];
                Array.Copy(splits, trimmed, lastToKeep);
                splits = trimmed;
            }
            for (int i = 0; i < splits.Length; i++) {
                splits[i].TrimEnd();
            }
            if (splits.Length % 2 == 1 && splits.Length >= 3) {
                MultichannelWaveform[] expanded = new MultichannelWaveform[splits.Length + 1];
                Array.Copy(splits, expanded, 3);
                expanded[3] = expanded[2];
                Array.Copy(splits, 3, expanded, 4, splits.Length - 3);
                splits = expanded;
            }

            ReferenceChannel[] std = ChannelPrototype.GetStandardMatrix(splits.Length);
            for (int i = 0; i < splits.Length; i++) {
                ChannelPrototype proto = ChannelPrototype.Mapping[(int)std[i]];
                if (proto.LFE) {
                    continue;
                }
                target.Add(new MeasuredHrirSpeaker(proto.X, proto.Y, (float[])splits[i][0].Clone(), (float[])splits[i][1].Clone()));
            }
        }

        static (float x, float y)[] BuildCavern16Angles() {
            return new (float, float)[] {
                (0f, 0f), (-45f, 0f),
                (0f, -45f), (0f, 45f),
                (-45f, -45f), (-45f, 45f),
                (0f, -90f), (0f, 90f),
                (-45f, -90f), (-45f, 90f),
                (0f, -135f), (0f, 135f),
                (-45f, -135f), (-45f, 135f),
                (0f, 180f), (-45f, 180f)
            };
        }

        static float[] CloneTrimmed(float[] channel) {
            int end = channel.Length - 1;
            while (end > 31 && MathF.Abs(channel[end]) < 1e-6f) {
                end--;
            }
            int len = end + 1;
            float[] copy = new float[len];
            Array.Copy(channel, 0, copy, 0, len);
            return copy;
        }

        static double Energy(float[] samples) {
            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++) {
                double v = samples[i];
                sum += v * v;
            }
            return sum;
        }
    }
}
