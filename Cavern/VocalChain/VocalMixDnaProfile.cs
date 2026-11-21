using System;
using System.Numerics;

namespace Cavern.VocalChain {
    /// <summary>
    /// Extracted song vocal mixing and mastering DNA produced by analyzing an isolated Lead Vocal stem
    /// (and optional Dereverb Dry/Wet pair) to drive zero-latency live microphone Auto-Vocal Mix-Match.
    /// </summary>
    public sealed class VocalMixDnaProfile {
        /// <summary>Center frequencies (Hz) of the 8 ERB-spaced formant-safe vocal analysis bands.</summary>
        public static readonly float[] AnalysisBandHz = new float[] {
            120f, 250f, 500f, 1000f, 2000f, 3800f, 6800f, 11500f
        };

        /// <summary>Band energies (dBFS relative to full-scale) across <see cref="AnalysisBandHz"/>.</summary>
        public float[] BandEnergyDb { get; set; } = new float[8];

        /// <summary>Overall spectral tilt in dB/octave (positive = brighter presence/air).</summary>
        public float SpectralTiltDbPerOct { get; set; }

        /// <summary>Estimated sibilance energy ratio in 5-10 kHz band relative to 1-3 kHz body (dB).</summary>
        public float SibilanceIndexDb { get; set; } = -6f;

        /// <summary>Target integrated vocal loudness relative to instrumental mix (LUFS).</summary>
        public float TargetVocalLufs { get; set; } = -14f;

        /// <summary>Measured vocal crest factor (Peak minus RMS in dB); lower values imply heavier compression.</summary>
        public float CrestFactorDb { get; set; } = 10.5f;

        /// <summary>Estimated harmonic saturation warmth index in [0; 1].</summary>
        public float HarmonicWarmth { get; set; } = 0.28f;

        /// <summary>Estimated reverb decay time RT60 in seconds (0.2..4.5 s).</summary>
        public float ReverbRt60Sec { get; set; } = 1.25f;

        /// <summary>Estimated reverb pre-delay in milliseconds (0..80 ms).</summary>
        public float ReverbPreDelayMs { get; set; } = 24f;

        /// <summary>Estimated wet-to-dry reverb send ratio in [0; 0.65].</summary>
        public float ReverbWetMix { get; set; } = 0.18f;

        /// <summary>High-frequency reverb damping cutoff frequency in Hz (2000..12000 Hz).</summary>
        public float ReverbDampingHz { get; set; } = 5800f;

        /// <summary>Detected slapback or rhythmic stereo delay tap time in milliseconds (0 = none).</summary>
        public float DelayTimeMs { get; set; } = 115f;

        /// <summary>Detected stereo delay wet mix in [0; 0.35].</summary>
        public float DelayWetMix { get; set; } = 0.08f;

        /// <summary>Estimated stereo width / doubling spread in [0; 1].</summary>
        public float StereoWidth { get; set; } = 0.25f;

        /// <summary>Extracted 3D Atmos normalized vocal position (X, Y, Z).</summary>
        public Vector3 VocalAtmosPosition { get; set; } = new Vector3(0f, 0.08f, 0.78f);

        /// <summary>Creates a transparent studio-default vocal DNA baseline.</summary>
        public static VocalMixDnaProfile CreateStudioDefault() {
            VocalMixDnaProfile profile = new VocalMixDnaProfile();
            profile.BandEnergyDb = new float[] { -26f, -18f, -15f, -14f, -13.5f, -12.5f, -16f, -20f };
            return profile;
        }
    }

    /// <summary>
    /// User-facing live vocal chain macro controls and bypass switches.
    /// </summary>
    public sealed class VocalMacroSettings {
        /// <summary>Master enable for Slot 2D live vocal chain.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Instant A/B bypass (passes clean dry mic with level match when true).</summary>
        public bool Bypass { get; set; }

        /// <summary>Mix Match macro strength in [0; 100]% (scales Match EQ, Dynamics, Saturation, and Space).</summary>
        public float MixMatchPct { get; set; } = 75f;

        /// <summary>Reverb &amp; Delay macro send multiplier in [0; 150]%.</summary>
        public float ReverbPct { get; set; } = 100f;

        /// <summary>Dual-stage FET + Optical compression intensity in [0; 150]%.</summary>
        public float DynamicsPct { get; set; } = 100f;

        /// <summary>Analog ADAA tube/tape saturation warmth in [0; 100]%.</summary>
        public float WarmthPct { get; set; } = 35f;

        /// <summary>Output trim gain in dB (-18..+12 dB).</summary>
        public float VocalLevelDb { get; set; }

        /// <summary>Enable dynamic 1-4 kHz vocal-pocket sidechain carving on the backing instrumental.</summary>
        public bool EnableSidechainPocketGlue { get; set; } = true;

        /// <summary>Enable noise gate / soft expander stage.</summary>
        public bool EnableNoiseGate { get; set; } = true;

        /// <summary>Enable split-band 5-10 kHz adaptive de-esser.</summary>
        public bool EnableDeEsser { get; set; } = true;
    }

    /// <summary>
    /// Live real-time metering telemetry produced by <see cref="LiveVocalChainDsp"/> for UI meters and Spatial X-Ray.
    /// </summary>
    public struct VocalChainTelemetry {
        /// <summary>Input vocal level before processing (dBFS).</summary>
        public float InputDbfs;

        /// <summary>Output vocal level after processing (dBFS).</summary>
        public float OutputDbfs;

        /// <summary>Active noise gate attenuation (dB, &lt;= 0).</summary>
        public float GateReductionDb;

        /// <summary>Active split-band de-esser reduction (dB, &lt;= 0).</summary>
        public float DeEsserReductionDb;

        /// <summary>Combined FET 1176 + Optical LA-2A gain reduction (dB, &lt;= 0).</summary>
        public float CompressorReductionDb;

        /// <summary>Active 1-4 kHz instrumental sidechain pocket ducking (dB, &lt;= 0).</summary>
        public float SidechainPocketDuckDb;
    }

    /// <summary>
    /// Extracts <see cref="VocalMixDnaProfile"/> parameters from an isolated lead vocal stem and optional dry/wet pair
    /// using zero-dependency pure-physics signal analysis (Goertzel ERB band power, crest factor, DRR/RT60, delay autocorrelation).
    /// </summary>
    public static class VocalMixProfileExtractor {
        /// <summary>
        /// Analyzes a mono/interleaved vocal stem buffer and optional dereverbed dry reference to build a <see cref="VocalMixDnaProfile"/>.
        /// </summary>
        public static VocalMixDnaProfile Analyze(
            float[] leadVocal,
            int channels,
            int sampleRate,
            float[] dereverbedDry = null,
            Vector3? atmosPosition = null) {
            VocalMixDnaProfile profile = VocalMixDnaProfile.CreateStudioDefault();
            if (atmosPosition.HasValue) {
                profile.VocalAtmosPosition = atmosPosition.Value;
            }
            if (leadVocal == null || leadVocal.Length < 256 || sampleRate < 8000) {
                return profile;
            }

            int ch = Math.Max(1, channels);
            int totalFrames = Math.Min(leadVocal.Length / ch, sampleRate * 12);
            if (totalFrames < 256) {
                return profile;
            }

            double sumSq = 0.0;
            float peak = 1e-6f;
            double midSumSq = 0.0;
            double sideSumSq = 0.0;
            for (int i = 0; i < totalFrames; i++) {
                float l = leadVocal[i * ch];
                float r = ch >= 2 ? leadVocal[i * ch + 1] : l;
                float m = 0.5f * (l + r);
                float s = 0.5f * (l - r);
                float absM = Math.Abs(m);
                if (absM > peak) {
                    peak = absM;
                }
                sumSq += m * m;
                midSumSq += m * m;
                sideSumSq += s * s;
            }

            double rms = Math.Sqrt(sumSq / totalFrames);
            float rmsDbfs = rms > 1e-6 ? (float)(20.0 * Math.Log10(rms)) : -96f;
            float peakDbfs = (float)(20.0 * Math.Log10(peak));
            profile.TargetVocalLufs = Clamp(rmsDbfs - 0.7f, -28f, -8f);
            profile.CrestFactorDb = Clamp(peakDbfs - rmsDbfs, 4.5f, 20f);
            profile.StereoWidth = ch >= 2 && midSumSq > 1e-9
                ? Clamp((float)Math.Sqrt(sideSumSq / (midSumSq + sideSumSq)) * 1.4f, 0f, 1f)
                : 0.15f;

            // Measure 8 ERB-spaced vocal bands via Goertzel power over a 4096-frame active segment
            int windowLen = Math.Min(4096, totalFrames);
            for (int b = 0; b < VocalMixDnaProfile.AnalysisBandHz.Length; b++) {
                float freq = VocalMixDnaProfile.AnalysisBandHz[b];
                double bandPower = MeasureGoertzelPower(leadVocal, ch, windowLen, sampleRate, freq);
                profile.BandEnergyDb[b] = bandPower > 1e-12
                    ? Clamp((float)(10.0 * Math.Log10(bandPower)), -72f, 0f)
                    : -72f;
            }

            // Compute spectral tilt (high bands 3.8-11.5 kHz minus low-mid 250-500 Hz)
            float lowMid = 0.5f * (profile.BandEnergyDb[1] + profile.BandEnergyDb[2]);
            float presenceAir = 0.5f * (profile.BandEnergyDb[5] + profile.BandEnergyDb[6]);
            profile.SpectralTiltDbPerOct = Clamp((presenceAir - lowMid) / 4f, -4f, 4f);
            profile.SibilanceIndexDb = Clamp(profile.BandEnergyDb[6] - profile.BandEnergyDb[3], -18f, 6f);

            // Estimate wet reverb mix & RT60 from dry/wet difference when Dereverb reference is provided
            if (dereverbedDry != null && dereverbedDry.Length >= totalFrames * ch) {
                double drySq = 0.0;
                double wetSq = 0.0;
                for (int i = 0; i < totalFrames; i++) {
                    float w = leadVocal[i * ch];
                    float d = dereverbedDry[i * ch];
                    float tail = w - d;
                    drySq += d * d;
                    wetSq += tail * tail;
                }
                double ratio = drySq > 1e-9 ? wetSq / (drySq + wetSq) : 0.15;
                profile.ReverbWetMix = Clamp((float)ratio * 0.85f, 0.05f, 0.55f);
                profile.ReverbRt60Sec = Clamp(0.55f + (float)ratio * 3.2f, 0.35f, 3.8f);
            }

            // Map lower crest factor + stronger high-mid harmonics to subtle analog warmth target
            float compTightness = Clamp((14f - profile.CrestFactorDb) / 10f, 0f, 1f);
            profile.HarmonicWarmth = Clamp(0.18f + compTightness * 0.42f, 0.1f, 0.7f);

            return profile;
        }

        /// <summary>
        /// Computes formant-safe bounded Match EQ deltas (in dB, capped at +/- 6 dB) between song vocal DNA and user mic voiceprint.
        /// </summary>
        public static float[] ComputeFormantSafeMatchEqDb(float[] songBandDb, float[] micBandDb, float maxGainDb = 6f) {
            int count = VocalMixDnaProfile.AnalysisBandHz.Length;
            float[] deltas = new float[count];
            if (songBandDb == null || micBandDb == null || songBandDb.Length < count || micBandDb.Length < count) {
                return deltas;
            }
            // Remove global level offset so Match EQ shapes timbre without shifting overall loudness
            float songAvg = 0f;
            float micAvg = 0f;
            for (int i = 0; i < count; i++) {
                songAvg += songBandDb[i];
                micAvg += micBandDb[i];
            }
            songAvg /= count;
            micAvg /= count;

            for (int i = 0; i < count; i++) {
                float rawDelta = (songBandDb[i] - songAvg) - (micBandDb[i] - micAvg);
                // Damp extreme sub/air bands slightly for natural voice preservation
                float safetyWeight = (i == 0 || i == count - 1) ? 0.55f : 0.78f;
                deltas[i] = Clamp(rawDelta * safetyWeight, -maxGainDb, maxGainDb);
            }
            return deltas;
        }

        static double MeasureGoertzelPower(float[] interleaved, int ch, int frames, int sampleRate, float targetHz) {
            double omega = 2.0 * Math.PI * targetHz / sampleRate;
            double coeff = 2.0 * Math.Cos(omega);
            double s0 = 0.0;
            double s1 = 0.0;
            double s2 = 0.0;
            for (int i = 0; i < frames; i++) {
                double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / Math.Max(1, frames - 1));
                double x = interleaved[i * ch] * w;
                s0 = x + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return Math.Max(0.0, power / (frames * (double)frames));
        }

        static float Clamp(float v, float min, float max) {
            if (v < min) {
                return min;
            }
            if (v > max) {
                return max;
            }
            return v;
        }
    }
}
