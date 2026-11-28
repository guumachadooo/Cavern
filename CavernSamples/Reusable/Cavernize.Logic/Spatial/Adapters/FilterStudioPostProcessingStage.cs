using System.Runtime.CompilerServices;
using System.Text.Json;

using Cavern;
using Cavern.Filters;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Zero-GC real-time FilterStudio post-processing stage (Priority 150: after AutoEq/HpTF, before ISO 226 &amp; True-Peak Limiter).
/// Runs click-free 64-bit <see cref="CytomicSvfFilter"/> cascades (including Vicanek Nyquist-decramped mode) and
/// feeds a lock-free 48-band logarithmic real-time spectrum array for the 120 Hz FilterStudio UI overlay.
/// </summary>
public sealed class FilterStudioPostProcessingStage : IPostProcessingStage {
    /// <summary>Number of logarithmically spaced spectrum visualizer bins (20 Hz..20 kHz).</summary>
    public const int SpectrumBinCount = 48;

    /// <summary>Maximum simultaneous interactive EQ bands supported in FilterStudio.</summary>
    public const int MaxBands = 16;

    readonly object stateLock = new();
    volatile FilterBankSnapshot activeSnapshot;
    bool isEnabled;
    bool nyquistMatched = true;
    int configuredSampleRate = Listener.DefaultSampleRate;
    int configuredChannels = 2;
    List<FilterStudioBandSpec> currentBands = CreateDefaultBands();

    // Lock-free 48-band log-frequency Goertzel-envelope meter (zero allocations on audio thread)
    readonly float[] spectrumDb = new float[SpectrumBinCount];
    readonly double[] goertzelCoeff = new double[SpectrumBinCount];
    readonly double[] goertzelS1 = new double[SpectrumBinCount];
    readonly double[] goertzelS2 = new double[SpectrumBinCount];
    int goertzelSampleRate;
    int goertzelBlockCount;
    const int GoertzelWindowFrames = 1024;

    sealed class FilterBankSnapshot {
        public readonly CytomicSvfFilter[][] FiltersByChannel; // [channel][band]
        public readonly float PreampLinear;
        public readonly double PreampDb;
        public readonly int SampleRate;
        public readonly int Channels;
        public readonly FilterStudioBandSpec[] Specs;

        public FilterBankSnapshot(
            CytomicSvfFilter[][] filtersByChannel,
            double preampDb,
            int sampleRate,
            int channels,
            FilterStudioBandSpec[] specs) {
            FiltersByChannel = filtersByChannel;
            PreampDb = preampDb;
            PreampLinear = (float)Math.Pow(10.0, preampDb / 20.0);
            SampleRate = sampleRate;
            Channels = channels;
            Specs = specs;
        }
    }

    public FilterStudioPostProcessingStage() {
        for (int i = 0; i < SpectrumBinCount; i++) {
            spectrumDb[i] = -72f;
        }
        RebuildGoertzelCoefficients(configuredSampleRate);
        RebuildSnapshotLocked();
    }

    /// <inheritdoc/>
    public string StageId => "filter-studio-svf";

    /// <inheritdoc/>
    public string DisplayName => "FilterStudio Cytomic SVF Parametric EQ";

    /// <inheritdoc/>
    public int Priority => 150;

    /// <inheritdoc/>
    public bool IsEnabled {
        get => isEnabled && activeSnapshot != null && activeSnapshot.Specs.Length > 0;
        set => isEnabled = value;
    }

    /// <summary>Raw user toggle state.</summary>
    public bool UserEnabled {
        get => isEnabled;
        set => isEnabled = value;
    }

    /// <summary>Global Vicanek/Orfanidis Nyquist-decramped mode toggle.</summary>
    public bool NyquistMatched {
        get => nyquistMatched;
        set {
            lock (stateLock) {
                if (nyquistMatched == value) {
                    return;
                }
                nyquistMatched = value;
                for (int i = 0; i < currentBands.Count; i++) {
                    currentBands[i].NyquistMatched = value;
                }
                UpdateOrRebuildFiltersLocked();
            }
        }
    }

    /// <summary>Active peak-safe preamp gain in dB.</summary>
    public double EffectivePreampDb => activeSnapshot?.PreampDb ?? 0.0;

    /// <summary>Configured sample rate.</summary>
    public int SampleRate => configuredSampleRate;

    /// <summary>Return a cloned snapshot of current FilterStudio band specs.</summary>
    public List<FilterStudioBandSpec> GetBandsSnapshot() {
        lock (stateLock) {
            List<FilterStudioBandSpec> copy = new(currentBands.Count);
            for (int i = 0; i < currentBands.Count; i++) {
                copy.Add(currentBands[i].Clone());
            }
            return copy;
        }
    }

    /// <summary>Replace all FilterStudio bands atomically; updates running SVF targets smoothly when band count matches.</summary>
    public void SetBands(IReadOnlyList<FilterStudioBandSpec> bands) {
        lock (stateLock) {
            currentBands.Clear();
            if (bands != null) {
                int count = Math.Min(bands.Count, MaxBands);
                for (int i = 0; i < count; i++) {
                    if (bands[i] != null) {
                        FilterStudioBandSpec spec = bands[i].Clone();
                        spec.NyquistMatched = nyquistMatched;
                        currentBands.Add(spec);
                    }
                }
            }
            UpdateOrRebuildFiltersLocked();
        }
    }

    /// <summary>
    /// Update a single band's parameters in-place with zero zipper noise (smooth TPT coefficient morphing).
    /// </summary>
    public void UpdateBandLive(int index, double frequencyHz, double gainDb, double q, CytomicSvfType? type = null, bool? enabled = null) {
        lock (stateLock) {
            if (index < 0 || index >= currentBands.Count) {
                return;
            }
            FilterStudioBandSpec b = currentBands[index];
            b.FrequencyHz = Math.Clamp(frequencyHz, 20.0, Math.Min(20000.0, configuredSampleRate * 0.48));
            b.GainDb = Math.Clamp(gainDb, -18.0, 18.0);
            b.Q = Math.Clamp(q, 0.15, 18.0);
            if (type.HasValue) {
                b.Type = type.Value;
            }
            if (enabled.HasValue) {
                b.Enabled = enabled.Value;
            }
            b.NyquistMatched = nyquistMatched;
            UpdateOrRebuildFiltersLocked();
        }
    }

    /// <summary>Evaluate composite magnitude (dB) and wrapped phase (radians) at <paramref name="freqHz"/>.</summary>
    public void EvaluateCompositeResponse(double freqHz, out double magnitudeDb, out double phaseRad) {
        FilterBankSnapshot snap = activeSnapshot;
        if (snap == null || snap.FiltersByChannel.Length == 0) {
            magnitudeDb = 0.0;
            phaseRad = 0.0;
            return;
        }
        CytomicSvfFilter[] ch0 = snap.FiltersByChannel[0];
        double sumDb = snap.PreampDb;
        double sumPhase = 0.0;
        for (int i = 0; i < ch0.Length; i++) {
            if (i < snap.Specs.Length && !snap.Specs[i].Enabled) {
                continue;
            }
            ch0[i].EvaluateComplex(freqHz, out double re, out double im);
            double mag = Math.Sqrt(re * re + im * im);
            sumDb += 20.0 * Math.Log10(Math.Max(mag, 1e-9));
            sumPhase += Math.Atan2(im, re);
        }
        // Wrap accumulated phase to [-pi, pi]
        double wrapped = (sumPhase + Math.PI) % (2.0 * Math.PI);
        if (wrapped < 0.0) {
            wrapped += 2.0 * Math.PI;
        }
        magnitudeDb = sumDb;
        phaseRad = wrapped - Math.PI;
    }

    /// <summary>Copy latest lock-free 48-bin spectrum levels (in dBFS, -72..0) into <paramref name="destination"/>.</summary>
    public void CopySpectrumDb(float[] destination) {
        if (destination == null) {
            return;
        }
        int n = Math.Min(destination.Length, SpectrumBinCount);
        for (int i = 0; i < n; i++) {
            destination[i] = spectrumDb[i];
        }
    }

    /// <summary>Serialize current bands to compact JSON for <see cref="AppSettingsStore"/>.</summary>
    public string SerializeBandsJson() {
        lock (stateLock) {
            return JsonSerializer.Serialize(currentBands);
        }
    }

    /// <summary>Load bands from persisted JSON, falling back to default 5-band mastering curve if empty/invalid.</summary>
    public void LoadBandsJson(string json) {
        if (string.IsNullOrWhiteSpace(json)) {
            SetBands(CreateDefaultBands());
            return;
        }
        try {
            List<FilterStudioBandSpec> parsed = JsonSerializer.Deserialize<List<FilterStudioBandSpec>>(json);
            if (parsed != null && parsed.Count > 0) {
                SetBands(parsed);
                return;
            }
        } catch {
            // Fall through to default bands
        }
        SetBands(CreateDefaultBands());
    }

    /// <summary>Default 5-band zero-gain mastering starter curve (Sub, Low, Mid, Presence, Air).</summary>
    public static List<FilterStudioBandSpec> CreateDefaultBands() => [
        new(CytomicSvfType.LowShelf, 65.0, 0.0, 0.71, true, true),
        new(CytomicSvfType.Peak, 250.0, 0.0, 1.0, true, true),
        new(CytomicSvfType.Peak, 1000.0, 0.0, 1.0, true, true),
        new(CytomicSvfType.Peak, 4000.0, 0.0, 1.2, true, true),
        new(CytomicSvfType.HighShelf, 12000.0, 0.0, 0.71, true, true)
    ];

    /// <inheritdoc/>
    public void Configure(int sampleRate, int channels) {
        int sr = Math.Max(8000, sampleRate);
        int ch = Math.Clamp(channels, 1, 16);
        lock (stateLock) {
            if (configuredSampleRate == sr && configuredChannels == ch && activeSnapshot != null) {
                return;
            }
            configuredSampleRate = sr;
            configuredChannels = ch;
            RebuildGoertzelCoefficients(sr);
            RebuildSnapshotLocked();
        }
    }

    /// <inheritdoc/>
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channels, SpatialSceneFrame context) {
        if (buffer == null || frameCount <= 0 || channels <= 0) {
            return;
        }
        int sr = context != null && context.SampleRate >= 8000 ? context.SampleRate : configuredSampleRate;
        if (sr != configuredSampleRate || channels != configuredChannels) {
            Configure(sr, channels);
        }

        // Feed lightweight real-time spectrum meter even when FilterStudio EQ is bypassed (so UI overlay stays live)
        UpdateSpectrumMeter(buffer, offset, frameCount, channels);

        if (!isEnabled) {
            return;
        }
        FilterBankSnapshot snap = activeSnapshot;
        if (snap == null || snap.FiltersByChannel.Length < channels || snap.Specs.Length == 0) {
            return;
        }

        int totalSamples = Math.Min(frameCount * channels, buffer.Length - offset);
        if (totalSamples <= 0) {
            return;
        }

        float preamp = snap.PreampLinear;
        if (MathF.Abs(preamp - 1f) > 1e-5f) {
            for (int i = offset, end = offset + totalSamples; i < end; i++) {
                buffer[i] *= preamp;
            }
        }

        int endIdx = offset + totalSamples;
        for (int ch = 0; ch < channels; ch++) {
            CytomicSvfFilter[] bank = snap.FiltersByChannel[ch];
            for (int b = 0; b < bank.Length; b++) {
                if (!snap.Specs[b].Enabled) {
                    continue;
                }
                bank[b].ProcessSlice(buffer, offset + ch, endIdx, channels);
            }
        }
    }

    void UpdateSpectrumMeter(float[] buffer, int offset, int frameCount, int channels) {
        if (goertzelSampleRate != configuredSampleRate) {
            RebuildGoertzelCoefficients(configuredSampleRate);
        }
        // Decimate by 2 when frameCount is large so spectrum costs < 0.08 ms/block on S25U
        int maxFrames = Math.Min(frameCount, (buffer.Length - offset) / Math.Max(1, channels));
        for (int f = 0; f < maxFrames; f++) {
            int baseIdx = offset + f * channels;
            double mono = channels >= 2 ? 0.5 * (buffer[baseIdx] + buffer[baseIdx + 1]) : buffer[baseIdx];
            for (int k = 0; k < SpectrumBinCount; k++) {
                double s0 = mono + goertzelCoeff[k] * goertzelS1[k] - goertzelS2[k];
                goertzelS2[k] = goertzelS1[k];
                goertzelS1[k] = s0;
            }
            goertzelBlockCount++;
            if (goertzelBlockCount >= GoertzelWindowFrames) {
                double invN = 2.0 / GoertzelWindowFrames;
                for (int k = 0; k < SpectrumBinCount; k++) {
                    double s1 = goertzelS1[k];
                    double s2 = goertzelS2[k];
                    double power = s1 * s1 + s2 * s2 - goertzelCoeff[k] * s1 * s2;
                    double amp = Math.Sqrt(Math.Max(power, 0.0)) * invN;
                    float db = (float)Math.Clamp(20.0 * Math.Log10(Math.Max(amp, 1e-4)), -72.0, 0.0);
                    // Smooth attack/decay ballistics for 120 Hz visual stability
                    float prev = spectrumDb[k];
                    spectrumDb[k] = db > prev ? prev + (db - prev) * 0.65f : prev + (db - prev) * 0.28f;
                    goertzelS1[k] = 0.0;
                    goertzelS2[k] = 0.0;
                }
                goertzelBlockCount = 0;
            }
        }
    }

    void RebuildGoertzelCoefficients(int sr) {
        goertzelSampleRate = sr;
        goertzelBlockCount = 0;
        double minF = 24.0;
        double maxF = Math.Min(19500.0, sr * 0.46);
        double ratio = maxF / minF;
        for (int k = 0; k < SpectrumBinCount; k++) {
            double freq = minF * Math.Pow(ratio, k / (double)(SpectrumBinCount - 1));
            double w = 2.0 * Math.PI * freq / sr;
            goertzelCoeff[k] = 2.0 * Math.Cos(w);
            goertzelS1[k] = 0.0;
            goertzelS2[k] = 0.0;
        }
    }

    void UpdateOrRebuildFiltersLocked() {
        FilterBankSnapshot snap = activeSnapshot;
        if (snap != null &&
            snap.SampleRate == configuredSampleRate &&
            snap.Channels == configuredChannels &&
            snap.Specs.Length == currentBands.Count) {
            // Live click-free parameter update in-place!
            for (int b = 0; b < currentBands.Count; b++) {
                FilterStudioBandSpec src = currentBands[b];
                snap.Specs[b] = src.Clone();
                for (int ch = 0; ch < snap.Channels; ch++) {
                    CytomicSvfFilter f = snap.FiltersByChannel[ch][b];
                    f.NyquistMatched = src.NyquistMatched;
                    f.SetParameters(src.Type, src.FrequencyHz, src.Q, src.GainDb, immediate: false);
                }
            }
            return;
        }
        RebuildSnapshotLocked();
    }

    void RebuildSnapshotLocked() {
        int chCount = Math.Max(1, configuredChannels);
        int sr = Math.Max(8000, configuredSampleRate);
        FilterStudioBandSpec[] specs = new FilterStudioBandSpec[currentBands.Count];
        for (int i = 0; i < currentBands.Count; i++) {
            specs[i] = currentBands[i].Clone();
        }
        CytomicSvfFilter[][] byChannel = new CytomicSvfFilter[chCount][];
        for (int ch = 0; ch < chCount; ch++) {
            byChannel[ch] = new CytomicSvfFilter[specs.Length];
            for (int b = 0; b < specs.Length; b++) {
                CytomicSvfFilter f = new(sr, specs[b].Type, specs[b].FrequencyHz, specs[b].Q, specs[b].GainDb, 10.0) {
                    NyquistMatched = specs[b].NyquistMatched
                };
                byChannel[ch][b] = f;
            }
        }
        double preampDb = EqExportFormatter.ComputeAutoPreampDb(specs, sr);
        activeSnapshot = new FilterBankSnapshot(byChannel, preampDb, sr, chCount, specs);
    }

    /// <inheritdoc/>
    public void Reset() {
        FilterBankSnapshot snap = activeSnapshot;
        if (snap != null) {
            for (int ch = 0; ch < snap.FiltersByChannel.Length; ch++) {
                CytomicSvfFilter[] bank = snap.FiltersByChannel[ch];
                for (int b = 0; b < bank.Length; b++) {
                    bank[b].Reset();
                }
            }
        }
        for (int k = 0; k < SpectrumBinCount; k++) {
            goertzelS1[k] = 0.0;
            goertzelS2[k] = 0.0;
            spectrumDb[k] = -72f;
        }
        goertzelBlockCount = 0;
    }

    /// <inheritdoc/>
    public IPostProcessingStage CloneForOfflineExport() {
        FilterStudioPostProcessingStage clone = new() {
            UserEnabled = isEnabled,
            NyquistMatched = nyquistMatched
        };
        clone.Configure(configuredSampleRate, configuredChannels);
        clone.SetBands(GetBandsSnapshot());
        return clone;
    }

    /// <inheritdoc/>
    public void Dispose() {
        Reset();
    }
}
