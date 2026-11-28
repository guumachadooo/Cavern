using System.Globalization;
using System.Text;
using System.Text.Json;

using Android.Content;
using Android.Media;

using Cavern;
using Cavern.Filters;
using Cavern.Format;
using Cavern.ProAudio;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

using CavernizeAndroid.Audio;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Services;

/// <summary>
/// Summary of a single captured QuickEQ Farina ESS measurement position (1..5 points).
/// </summary>
public sealed class QuickEqTakeSummary {
    public int PointIndex { get; init; }
    public bool IsValid { get; init; }
    public double SnrDb { get; init; }
    public double Harmonic2Db { get; init; }
    public double Harmonic3Db { get; init; }
    public int SampleRate { get; init; }
    public double[] FrequenciesHz { get; init; } = Array.Empty<double>();
    public double[] MagnitudeDb { get; init; } = Array.Empty<double>();
    public string StatusNote { get; init; } = string.Empty;
}

/// <summary>
/// Coordinates multi-point (1..5 takes) QuickEQ room/speaker sweep capture, Farina ESS deconvolution,
/// FDW+ERB spatial averaging, parametric Cytomic SVF solving, <see cref="AppSettingsStore"/> persistence,
/// live <see cref="FilterStudioPostProcessingStage"/> application, and universal EQ/FIR export.
/// </summary>
public sealed class QuickEqCalibrationService {
    public const int MaxPoints = 5;
    public const double DefaultSweepDurationSec = 3.0;
    public const float DefaultSweepAmplitude = 0.25f;

    readonly object sessionLock = new();
    readonly List<QuickEqTakeSummary> takes = [];

    QuickEqTargetCurve targetCurve = QuickEqTargetCurve.Harman;
    int sessionSampleRate = 48000;
    double sweepDurationSec = DefaultSweepDurationSec;
    float[] cachedSweep, cachedInverse;
    int cachedSampleRate, captureInProgress;
    double cachedDurationSec;

    public int TakeCount { get { lock (sessionLock) return takes.Count; } }
    public bool IsCapturing => Volatile.Read(ref captureInProgress) != 0;
    public QuickEqTargetCurve TargetCurve {
        get { lock (sessionLock) return targetCurve; }
        set { lock (sessionLock) targetCurve = value; }
    }

    /// <summary>Resets captured takes and initializes a new multi-point QuickEQ calibration session.</summary>
    public void StartNewSession(QuickEqTargetCurve target = QuickEqTargetCurve.Harman, int sampleRate = 48000, double durationSec = DefaultSweepDurationSec) {
        lock (sessionLock) {
            takes.Clear();
            targetCurve = target;
            sessionSampleRate = Math.Clamp(sampleRate, 44100, 192000);
            sweepDurationSec = Math.Clamp(durationSec, 1.0, 6.0);
        }
    }

    public void ClearSession() { lock (sessionLock) takes.Clear(); }
    public bool RemoveLastTake() { lock (sessionLock) { if (takes.Count == 0) return false; takes.RemoveAt(takes.Count - 1); return true; } }
    public List<QuickEqTakeSummary> GetTakesSnapshot() { lock (sessionLock) return [.. takes]; }

    /// <summary>Plays a safe Farina ESS, captures room response via <see cref="LiveMicrophoneBridge"/>, deconvolves, and adds the point (1..5).</summary>
    public async Task<QuickEqTakeSummary> CaptureTakeAsync(
        RealtimePlayerEngine engine, AndroidAudioOutput audioOutput, CancellationToken ct = default) {
        if (Interlocked.CompareExchange(ref captureInProgress, 1, 0) != 0) {
            return new QuickEqTakeSummary { IsValid = false, StatusNote = "QuickEQ sweep capture already in progress." };
        }
        try {
            LiveMicrophoneBridge mic = engine?.LiveMicBridge;
            if (mic != null && (mic.IsCalibratingLatency || mic.IsCapturingVoiceprint || mic.IsRecordingPerformance)) {
                return new QuickEqTakeSummary { IsValid = false, StatusNote = "Microphone capture busy with another calibration." };
            }
            int sr; double dur;
            lock (sessionLock) {
                int fallbackSr = audioOutput != null ? audioOutput.ActiveCaptureSampleRate : sessionSampleRate;
                sr = mic != null && mic.IsActive && mic.SampleRate >= 44100
                    ? Math.Clamp(mic.SampleRate, 44100, 192000)
                    : Math.Clamp(fallbackSr, 44100, 48000);
                sessionSampleRate = sr; dur = sweepDurationSec;
            }
            (float[] sweep, float[] inverse) = GetOrBuildSweepPair(sr, dur);
            if (engine != null && engine.IsPlaying) engine.IsPlaying = false;
            float[] recordedMono = await Task.Run(() => PlayAndCaptureSweep(sweep, sr, engine, audioOutput, ct), ct).ConfigureAwait(false);
            if (recordedMono == null || recordedMono.Length < sweep.Length / 2) {
                return new QuickEqTakeSummary { IsValid = false, SampleRate = sr, StatusNote = "Microphone capture returned insufficient audio." };
            }
            return ProcessAndStoreTake(sweep, inverse, recordedMono, sr);
        } catch (OperationCanceledException) {
            return new QuickEqTakeSummary { IsValid = false, StatusNote = "QuickEQ sweep cancelled." };
        } catch (Exception ex) {
            AppLogger.Warn($"QuickEQ capture failed: {ex.Message}");
            return new QuickEqTakeSummary { IsValid = false, StatusNote = ex.Message };
        } finally {
            Interlocked.Exchange(ref captureInProgress, 0);
        }
    }

    /// <summary>Analyzes and stores an externally captured or synthetic mono sweep recording.</summary>
    public QuickEqTakeSummary AddOfflineRecordedTake(float[] recordedMono, int sampleRate) {
        int sr = Math.Clamp(sampleRate > 0 ? sampleRate : 48000, 8000, 192000);
        double dur; lock (sessionLock) dur = sweepDurationSec;
        (float[] sweep, float[] inverse) = GetOrBuildSweepPair(sr, dur);
        return ProcessAndStoreTake(sweep, inverse, recordedMono, sr);
    }

    /// <summary>Averages all captured points (1..5) in power domain and solves safety-clamped Cytomic SVF bands.</summary>
    public QuickEqCalibrationResult SolveCurrentSession(QuickEqTargetCurve? targetOverride = null, int maxBands = 8) {
        List<double[]> curves; double[] freqs; QuickEqTargetCurve target; double snrSum = 0.0;
        lock (sessionLock) {
            target = targetOverride ?? targetCurve; targetCurve = target;
            if (takes.Count == 0) return new QuickEqCalibrationResult { TargetCurve = target, PointCount = 0 };
            curves = new List<double[]>(takes.Count); freqs = takes[0].FrequenciesHz;
            for (int i = 0; i < takes.Count; i++) { curves.Add(takes[i].MagnitudeDb); snrSum += takes[i].SnrDb; }
        }
        double[] averagedDb = QuickEqSweepEngine.AverageMultiPointMeasurements(curves);
        QuickEqCalibrationResult result = QuickEqSweepEngine.SolveCorrectionBands(
            freqs, averagedDb, target, maxBands, QuickEqSweepEngine.HardMaxBoostDb, -12.0);
        result.PointCount = curves.Count;
        result.SnrDb = snrSum / curves.Count;
        return result;
    }

    /// <summary>Persists calibrated bands to <see cref="AppSettingsStore"/> and activates them in <see cref="FilterStudioPostProcessingStage"/>.</summary>
    public static FilterStudioBandSpec[] ApplyAndSave(
        QuickEqCalibrationResult result, AppSettingsStore store, RealtimePlayerEngine engine, bool enable = true) {
        if (result == null || result.Bands == null) return Array.Empty<FilterStudioBandSpec>();
        FilterStudioBandSpec[] specs = QuickEqSweepEngine.ToExportBands(result.Bands);
        string json = JsonSerializer.Serialize(specs);

        if (store != null) {
            store.QuickEqBandsJson = json;
            store.QuickEqTargetIndex = (int)result.TargetCurve;
            store.QuickEqEnabled = enable;
            store.QuickEqLastSummary = string.Format(CultureInfo.InvariantCulture,
                "{0} pts · {1} · {2} bands · SNR {3:0.0} dB · Preamp {4:+0.0;-0.0;0.0} dB",
                Math.Max(1, result.PointCount), result.TargetCurve, specs.Length, result.SnrDb, result.PreampDb);
        }

        if (engine?.FilterStudioStage != null) {
            FilterStudioPostProcessingStage stage = engine.FilterStudioStage;
            stage.NyquistMatched = true;
            stage.SetBands(specs);
            stage.UserEnabled = enable;
        }
        return specs;
    }

    /// <summary>Deserializes saved QuickEQ bands from <see cref="AppSettingsStore"/>.</summary>
    public static List<FilterStudioBandSpec> LoadSavedBands(AppSettingsStore store) {
        if (store == null || string.IsNullOrWhiteSpace(store.QuickEqBandsJson)) return [];
        try {
            return JsonSerializer.Deserialize<List<FilterStudioBandSpec>>(store.QuickEqBandsJson) ?? [];
        } catch {
            return [];
        }
    }

    /// <summary>Applies persisted QuickEQ bands into <see cref="FilterStudioPostProcessingStage"/> when enabled.</summary>
    public static bool SyncSavedToEngine(AppSettingsStore store, RealtimePlayerEngine engine) {
        if (store == null || engine?.FilterStudioStage == null || !store.QuickEqEnabled) return false;
        List<FilterStudioBandSpec> saved = LoadSavedBands(store);
        if (saved.Count == 0) return false;
        FilterStudioPostProcessingStage stage = engine.FilterStudioStage;
        stage.NyquistMatched = true;
        stage.SetBands(saved);
        stage.UserEnabled = true;
        return true;
    }

    /// <summary>Exports QuickEQ bands to Equalizer APO, CamillaDSP, MiniDSP, PipeWire, or Minimum-Phase FIR WAV.</summary>
    public static string ExportToFile(Context context, IReadOnlyList<FilterStudioBandSpec> bands, EqExportFormat format,
        int sampleRate, ExportStorageDestination destination = ExportStorageDestination.DownloadsCavernize, double? preampOverrideDb = null) {
        if (bands == null || bands.Count == 0) throw new InvalidOperationException("No QuickEQ bands available to export.");
        int sr = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
        double preampDb = preampOverrideDb ?? EqExportFormatter.ComputeAutoPreampDb(bands, sr);
        string ext = format switch {
            EqExportFormat.CamillaDsp => "yml",
            EqExportFormat.PipeWire => "conf",
            EqExportFormat.FirWav => "wav",
            _ => "txt"
        };
        string baseDir = AndroidFileHelper.GetExportWorkingDir(context, destination, "." + ext, out _, out _);
        string outDir = Path.Combine(baseDir, "QuickEQ");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, $"Cavern_QuickEQ_{format}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.{ext}");

        if (format == EqExportFormat.FirWav) {
            float[] ir = EqExportFormatter.RenderFirImpulse(bands, sr, 4096, preampDb);
            using RIFFWaveWriter writer = new(outPath, 1, ir.Length, sr, BitDepth.Float32);
            writer.WriteHeader();
            writer.WriteBlock(ir, 0, ir.Length);
        } else {
            string text = format switch {
                EqExportFormat.EqualizerApo => EqExportFormatter.ToEqualizerApo(bands, preampDb),
                EqExportFormat.CamillaDsp => EqExportFormatter.ToCamillaDspYaml(bands, sr, preampDb),
                EqExportFormat.MiniDsp => EqExportFormatter.ToMiniDspBiquads(bands, sr),
                _ => EqExportFormatter.ToPipeWireFilterChain(bands, preampDb)
            };
            File.WriteAllText(outPath, text, System.Text.Encoding.UTF8);
        }
        AndroidFileHelper.PublishToDownloads(context, outPath, "QuickEQ");
        return outPath;
    }

    (float[] sweep, float[] inverse) GetOrBuildSweepPair(int sr, double dur) {
        lock (sessionLock) {
            if (cachedSweep != null && cachedInverse != null && cachedSampleRate == sr && Math.Abs(cachedDurationSec - dur) < 1e-4) {
                return (cachedSweep, cachedInverse);
            }
            double fMax = Math.Min(20000.0, sr * 0.45);
            cachedSweep = QuickEqSweepEngine.GenerateExponentialSweep(20.0, fMax, dur, sr, DefaultSweepAmplitude);
            cachedInverse = QuickEqSweepEngine.GenerateInverseFilter(20.0, fMax, dur, sr, DefaultSweepAmplitude);
            cachedSampleRate = sr;
            cachedDurationSec = dur;
            return (cachedSweep, cachedInverse);
        }
    }

    QuickEqTakeSummary ProcessAndStoreTake(float[] sweep, float[] inverse, float[] recordedMono, int sr) {
        QuickEqDeconvResult deconv = QuickEqSweepEngine.DeconvolveImpulseResponse(sweep, inverse, recordedMono, sr);
        if (!deconv.IsValid) {
            return new QuickEqTakeSummary {
                IsValid = false, SnrDb = deconv.EstimatedSnrDb, Harmonic2Db = deconv.Harmonic2EnergyDb,
                Harmonic3Db = deconv.Harmonic3EnergyDb, SampleRate = sr,
                StatusNote = string.Format(CultureInfo.InvariantCulture, "Low sweep SNR ({0:0.0} dB). Raise volume or reduce ambient noise.", deconv.EstimatedSnrDb)
            };
        }

        double[] freqs = new double[QuickEqSweepEngine.DefaultSpectrumPoints];
        double[] magDb = new double[QuickEqSweepEngine.DefaultSpectrumPoints];
        QuickEqSweepEngine.ComputeFdwAndErbSpectrum(deconv.LinearImpulseResponse, sr, freqs, magDb);

        lock (sessionLock) {
            if (takes.Count >= MaxPoints) takes.RemoveAt(0);
            QuickEqTakeSummary summary = new() {
                PointIndex = takes.Count + 1, IsValid = true, SnrDb = deconv.EstimatedSnrDb,
                Harmonic2Db = deconv.Harmonic2EnergyDb, Harmonic3Db = deconv.Harmonic3EnergyDb,
                SampleRate = sr, FrequenciesHz = freqs, MagnitudeDb = magDb,
                StatusNote = string.Format(CultureInfo.InvariantCulture, "Point {0}: SNR {1:0.0} dB · H2 {2:0.0} dB",
                    takes.Count + 1, deconv.EstimatedSnrDb, deconv.Harmonic2EnergyDb)
            };
            takes.Add(summary);
            return summary;
        }
    }

    static float[] PlayAndCaptureSweep(float[] sweep, int sr, RealtimePlayerEngine engine, AndroidAudioOutput audioOutput, CancellationToken ct) {
        int leadFrames = (int)Math.Round(sr * 0.12), tailFrames = (int)Math.Round(sr * 0.35);
        float[] stereoPlay = new float[(leadFrames + sweep.Length + tailFrames) * 2];
        for (int i = 0; i < sweep.Length; i++) {
            int dst = (leadFrames + i) * 2;
            stereoPlay[dst] = stereoPlay[dst + 1] = sweep[i];
        }

        bool useBridge = audioOutput != null && engine?.LiveMicBridge != null && audioOutput.EnsureStudioRecordingCapture(true, sr);
        AudioRecord localRec = null;
        Thread localRecThread = null;
        bool localRecRunning = false;
        float[] localCapture = null;
        int localCursor = 0;

        try {
            if (useBridge) engine.LiveMicBridge.StartPerformanceRecording(sr, 0L);
            else {
                int minRecBytes = AudioRecord.GetMinBufferSize(sr, ChannelIn.Mono, AudioEncoding.Pcm16bit);
                localRec = new AudioRecord(AudioSource.Mic, sr, ChannelIn.Mono, AudioEncoding.Pcm16bit, Math.Max(minRecBytes * 2, 8192));
                if (localRec.State != State.Initialized) return null;
                localCapture = new float[leadFrames + sweep.Length + tailFrames + sr / 2];
                localRecRunning = true;
                localRec.StartRecording();
                localRecThread = new Thread(() => {
                    short[] buf = new short[1024];
                    const float inv = 1f / 32768f;
                    try {
                        while (Volatile.Read(ref localRecRunning) && localCursor < localCapture.Length) {
                            int read = localRec.Read(buf, 0, Math.Min(buf.Length, localCapture.Length - localCursor));
                            if (read <= 0) break;
                            for (int i = 0; i < read; i++) localCapture[localCursor + i] = buf[i] * inv;
                            localCursor += read;
                        }
                    } catch (ObjectDisposedException) {
                    } catch (Exception) {
                    }
                }) { IsBackground = true, Name = "QuickEqSweepRec" };
                localRecThread.Start();
            }

            PlayStereoSweepBlocking(stereoPlay, sr, ct);
            Thread.Sleep(120);
            if (useBridge) return engine.LiveMicBridge.StopPerformanceRecording()?.MonoPcm;
            Volatile.Write(ref localRecRunning, false);
            if (localRec != null) {
                try { localRec.Stop(); } catch { }
            }
            try { localRecThread?.Join(500); } catch { }
            if (localCapture == null || localCursor <= 0) return null;
            float[] trimmed = new float[localCursor];
            Array.Copy(localCapture, trimmed, localCursor);
            return trimmed;
        } finally {
            Volatile.Write(ref localRecRunning, false);
            if (useBridge) {
                if (engine?.LiveMicBridge?.IsRecordingPerformance == true) engine.LiveMicBridge.StopPerformanceRecording();
                audioOutput?.EnsureStudioRecordingCapture(false);
            }
            if (localRec != null) {
                try { localRec.Stop(); } catch { }
                try { localRecThread?.Join(500); } catch { }
                try { localRec.Release(); } catch { }
                try { localRec.Dispose(); } catch { }
            } else {
                try { localRecThread?.Join(500); } catch { }
            }
        }
    }

    static void PlayStereoSweepBlocking(float[] stereo, int sampleRate, CancellationToken ct) {
        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Stereo, AudioEncoding.PcmFloat);
        if (minBytes <= 0) return;
        AudioAttributes attrs = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build();
        AudioFormat fmt = new AudioFormat.Builder().SetEncoding(AudioEncoding.PcmFloat).SetChannelMask(ChannelOut.Stereo).SetSampleRate(sampleRate).Build();
        using AudioTrack track = new AudioTrack.Builder()
            .SetAudioAttributes(attrs).SetAudioFormat(fmt).SetTransferMode(AudioTrackMode.Stream)
            .SetBufferSizeInBytes(Math.Max(minBytes, 4096 * 2 * sizeof(float))).Build();
        if (track.State != AudioTrackState.Initialized) return;
        track.Play();
        int chunk = 2048 * 2;
        for (int offset = 0; offset < stereo.Length && !ct.IsCancellationRequested; offset += chunk) {
            track.Write(stereo, offset, Math.Min(chunk, stereo.Length - offset), WriteMode.Blocking);
        }
        try { track.Stop(); } catch { }
    }
}
