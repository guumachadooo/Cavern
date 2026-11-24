using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Android.Content;

using Cavern.Filters;
using Cavern.ProAudio;
using Cavern.Virtualizer;
using Cavern.VocalChain;

using CavernizeAndroid.Language;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;

/// <summary>
/// Single runtime anomaly recorded by <see cref="LiveChaosBugHunter"/> or captured live from
/// <see cref="AppLogger.CriticalEventLogged"/> during real user sessions or chaos hunts.
/// </summary>
public sealed class RuntimeAnomalyRecord {
    public string Utc { get; init; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public string Severity { get; init; } = "MEDIUM";
    public string Category { get; init; } = "";
    public string Source { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// In-app live anomaly tracker, zero-shell own-UID Android logcat reader, and high-speed concurrent
/// chaos/race-condition fuzzer exposed via Live Bridge commands (<c>logcat_tail</c>, <c>anomalies</c>,
/// <c>chaos_hunt</c>) to actively surface real-world Cavernize errors, warnings, and DSP races.
/// </summary>
public static class LiveChaosBugHunter {
    const int MaxRingCapacity = 160;
    static readonly object ringLock = new();
    static readonly Queue<RuntimeAnomalyRecord> anomalyRing = new(MaxRingCapacity + 4);
    static readonly SemaphoreSlim chaosGate = new(1, 1);
    static bool loggerHooked;

    /// <summary>
    /// Attaches a non-blocking listener to <see cref="AppLogger.CriticalEventLogged"/> once per process.
    /// </summary>
    public static void EnsureInitialized() {
        lock (ringLock) {
            if (loggerHooked) {
                return;
            }
            loggerHooked = true;
            AppLogger.CriticalEventLogged += OnCriticalLogLine;
        }
    }

    static void OnCriticalLogLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) {
            return;
        }
        string sev = line.Contains("[FATAL]", StringComparison.Ordinal) ? "CRITICAL" : "HIGH";
        RecordAnomaly(sev, "applogger_critical", "AppLogger", "runtime", line);
    }

    /// <summary>Record a structured anomaly into the bounded in-memory ring buffer.</summary>
    public static void RecordAnomaly(string severity, string category, string source, string scenario, string message) {
        lock (ringLock) {
            while (anomalyRing.Count >= MaxRingCapacity) {
                anomalyRing.Dequeue();
            }
            anomalyRing.Enqueue(new RuntimeAnomalyRecord {
                Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Severity = string.IsNullOrWhiteSpace(severity) ? "MEDIUM" : severity.Trim().ToUpperInvariant(),
                Category = category ?? "",
                Source = source ?? "",
                Scenario = scenario ?? "",
                Message = (message ?? "").Length > 400 ? message[..400] : (message ?? "")
            });
        }
    }

    /// <summary>Return recent runtime anomalies serialized as JSON (optionally clearing after read).</summary>
    public static string GetAnomaliesJson(int limit, bool clearAfterRead) {
        EnsureInitialized();
        List<RuntimeAnomalyRecord> snap;
        lock (ringLock) {
            snap = anomalyRing.TakeLast(Math.Clamp(limit, 1, MaxRingCapacity)).ToList();
            if (clearAfterRead) {
                anomalyRing.Clear();
            }
        }
        return JsonSerializer.Serialize(new {
            count = snap.Count,
            sessionErrors = AppLogger.SessionErrorCount,
            sessionWarns = AppLogger.SessionWarnCount,
            sessionFatals = AppLogger.SessionFatalCount,
            anomalies = snap.Select(a => new {
                utc = a.Utc,
                severity = a.Severity,
                category = a.Category,
                source = a.Source,
                scenario = a.Scenario,
                message = a.Message
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reads Cavernize's own-UID Android logcat cleanly via direct <c>/system/bin/logcat</c>
    /// <see cref="ProcessStartInfo.ArgumentList"/> (zero shell, filters benign .NET fork libsigchain noise).
    /// </summary>
    public static async Task<(bool ok, string json)> ReadOwnLogcatAsync(
        int lines,
        string minLevel,
        string buffer,
        CancellationToken ct) {
        EnsureInitialized();
        int clampedLines = Math.Clamp(lines, 10, 800);
        string lvl = (minLevel ?? "W").Trim().ToUpperInvariant();
        if (lvl is not ("V" or "D" or "I" or "W" or "E" or "F")) {
            lvl = "W";
        }
        string buf = (buffer ?? "main").Trim().ToLowerInvariant();
        if (buf is not ("main" or "system" or "crash" or "events")) {
            buf = "main";
        }

        ProcessStartInfo psi = new() {
            FileName = "/system/bin/logcat",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-d");
        if (buf != "main") {
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(buf);
        }
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(clampedLines.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("brief");
        psi.ArgumentList.Add($"*:{lvl}");

        try {
            using System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) {
                return (false, "Failed to start /system/bin/logcat.");
            }
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string stdout = await outTask.ConfigureAwait(false);
            string stderr = await errTask.ConfigureAwait(false);

            StringBuilder filtered = new();
            int keptLines = 0;
            foreach (string rawLine in stdout.Split('\n')) {
                string ln = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(ln) || ln.StartsWith("--------- beginning of", StringComparison.Ordinal)) {
                    continue;
                }
                // Strip harmless ART/libSystem.Native fork reset trace spawned by logcat itself
                if (ln.Contains("libsigchain", StringComparison.OrdinalIgnoreCase) &&
                    (ln.Contains("Setting SIGSEGV to SIG_DFL", StringComparison.OrdinalIgnoreCase) ||
                     ln.Contains("libsigchain.so", StringComparison.OrdinalIgnoreCase) ||
                     ln.Contains("libSystem.Native.so", StringComparison.OrdinalIgnoreCase))) {
                    continue;
                }
                filtered.AppendLine(ln);
                keptLines++;
            }

            return (proc.ExitCode == 0, JsonSerializer.Serialize(new {
                ok = proc.ExitCode == 0,
                buffer = buf,
                minLevel = lvl,
                lines = keptLines,
                output = filtered.ToString(),
                stderr = stderr.Length > 1000 ? stderr[..1000] : stderr
            }, new JsonSerializerOptions { WriteIndented = true }));
        } catch (System.OperationCanceledException) {
            return (false, "logcat_tail timed out.");
        } catch (Exception ex) {
            return (false, $"logcat_tail error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs high-speed in-process adversarial chaos tests directly inside the running Cavernize process
    /// (concurrent DSP/acoustic race hunting, malformed file probing, ring-buffer contention, and
    /// boundary edge-case fuzzing) without leaving any mutated user settings or temporary files behind.
    /// </summary>
    public static async Task<(bool ok, string json)> RunChaosHuntAsync(
        Context context,
        AppSettingsStore store,
        int rounds,
        CancellationToken ct) {
        EnsureInitialized();
        await chaosGate.WaitAsync(ct).ConfigureAwait(false);
        Stopwatch totalSw = Stopwatch.StartNew();
        List<RuntimeAnomalyRecord> found = [];
        int clampedRounds = Math.Clamp(rounds, 1, 20);
        int scenariosRun = 0;

        void AddFinding(string severity, string category, string scenario, string message) {
            RuntimeAnomalyRecord rec = new() {
                Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Severity = severity,
                Category = category,
                Source = "LiveChaosBugHunter",
                Scenario = scenario,
                Message = message
            };
            found.Add(rec);
            RecordAnomaly(severity, category, "LiveChaosBugHunter", scenario, message);
        }

        try {
            // 1. Concurrent DSP & Acoustic Race Hunter (2 parallel threads executing diverse probes simultaneously)
            scenariosRun++;
            try {
                Task[] workers = new Task[2];
                for (int w = 0; w < workers.Length; w++) {
                    int workerId = w;
                    workers[w] = Task.Run(() => {
                        int[] rates = [44100, 48000, 96000];
                        for (int r = 0; r < Math.Min(clampedRounds, 6); r++) {
                            ct.ThrowIfCancellationRequested();
                            AcousticCustomProbeRequest req = new() {
                                ProfileId = BinauralProfileService.DefaultProfileId,
                                MinimumPhase = (r + workerId) % 2 == 0,
                                MagLsOrder = (r + workerId) % 4,
                                BiometricItdEnabled = true,
                                HeadWidthCm = workerId == 0 ? 11.5f : 18.0f,
                                IsmRoomEnabled = r % 2 == 1,
                                NearFieldEnabled = r % 3 == 0,
                                NearFieldDistanceM = 0.45f,
                                ListenerYawDeg = workerId == 0 ? 45f : -45f,
                                SourceAzimuthDeg = -90f,
                                SampleRate = rates[(r + workerId) % rates.Length],
                                UseParametricUpmix = r % 2 == 0,
                                CompareAgainstDirect = true,
                                SignalType = r % 2 == 0 ? "white" : "pink"
                            };
                            string resJson = AcousticAuditEngine.RunCustomProbeJson(context, req);
                            if (resJson.Contains("NaN", StringComparison.Ordinal) ||
                                resJson.Contains("Infinity", StringComparison.Ordinal)) {
                                AddFinding("CRITICAL", "concurrent_dsp_nan", $"worker{workerId}_r{r}",
                                    "Concurrent AcousticCustomProbe emitted NaN/Infinity");
                            }
                            if (resJson.Contains("\"GainDb\": -16", StringComparison.OrdinalIgnoreCase) ||
                                resJson.Contains("\"gainDb\": -16", StringComparison.OrdinalIgnoreCase)) {
                                AddFinding("HIGH", "concurrent_dsp_silence_race", $"worker{workerId}_r{r}",
                                    "Concurrent AcousticCustomProbe collapsed to -160dB silence (likely static Listener/Binaural race!)");
                            }
                        }
                    }, ct);
                }
                await Task.WhenAll(workers).ConfigureAwait(false);
            } catch (System.OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                AddFinding("HIGH", "concurrent_dsp_exception", "parallel_acoustic_probes",
                    $"Parallel acoustic probes threw {ex.GetType().Name}: {ex.Message}");
            }

            // 2. LiveVocalChainDsp & TruePeakLimiter Extreme Input Torture (silence, impulses, NaN guard, +24dBFS burst)
            scenariosRun++;
            try {
                LiveVocalChainDsp vocalDsp = new(48000);
                vocalDsp.Configure(48000, VocalMixDnaProfile.CreateStudioDefault(), new VocalMacroSettings {
                    Enabled = true,
                    MixMatchPct = 100f,
                    ReverbPct = 100f,
                    DynamicsPct = 100f,
                    WarmthPct = 100f,
                    VocalLevelDb = 12f,
                    EnableSidechainPocketGlue = true,
                    EnableNoiseGate = true,
                    EnableDeEsser = true
                });
                float[] vBuf = new float[4096];
                float[] bBuf = new float[4096];
                // Hot +24 dBFS alternating impulse + DC offset burst
                for (int i = 0; i < vBuf.Length; i++) {
                    vBuf[i] = (i % 16 == 0) ? 16.0f : ((i % 2 == 0) ? 0.5f : -0.5f);
                    bBuf[i] = (i % 8 == 0) ? -12.0f : 0.25f;
                }
                Stopwatch vSw = Stopwatch.StartNew();
                vocalDsp.ProcessStereoBlockZeroGc(vBuf, 0, 2048, bBuf, 0);
                vSw.Stop();
                int nf = 0;
                float peak = 0f;
                for (int i = 0; i < vBuf.Length; i++) {
                    if (!float.IsFinite(vBuf[i])) {
                        nf++;
                    } else if (MathF.Abs(vBuf[i]) > peak) {
                        peak = MathF.Abs(vBuf[i]);
                    }
                }
                if (nf > 0) {
                    AddFinding("CRITICAL", "vocal_dsp_nan", "hot_24dbfs_burst",
                        $"LiveVocalChainDsp produced {nf} non-finite sample(s) on +24dBFS burst");
                }
                if (vSw.Elapsed.TotalMilliseconds > 28.0) {
                    AddFinding("MEDIUM", "vocal_dsp_rt_budget", "hot_24dbfs_burst",
                        $"LiveVocalChainDsp took {vSw.Elapsed.TotalMilliseconds:0.0}ms for 42.7ms block (>65% RT budget)");
                }
            } catch (Exception ex) {
                AddFinding("HIGH", "vocal_dsp_exception", "hot_24dbfs_burst",
                    $"LiveVocalChainDsp threw {ex.GetType().Name}: {ex.Message}");
            }

            // 3. SPSC LockFreeAudioRingBuffer Multi-Threaded Torture (10,000 frames)
            scenariosRun++;
            try {
                LockFreeAudioRingBuffer ring = new(512, 2);
                const int totalFrames = 10_000;
                int checksumMismatch = 0;
                Task producer = Task.Run(() => {
                    float[] chunk = new float[64 * 2];
                    int writtenTotal = 0;
                    while (writtenTotal < totalFrames) {
                        int n = Math.Min(64, totalFrames - writtenTotal);
                        for (int i = 0; i < n; i++) {
                            float v = (writtenTotal + i) * 0.001f;
                            chunk[i * 2] = v;
                            chunk[i * 2 + 1] = -v;
                        }
                        int w = ring.Write(chunk, 0, n);
                        if (w == 0) {
                            Thread.Yield();
                        } else {
                            writtenTotal += w;
                        }
                    }
                }, ct);
                Task consumer = Task.Run(() => {
                    float[] chunk = new float[48 * 2];
                    int readTotal = 0;
                    while (readTotal < totalFrames) {
                        int n = Math.Min(48, totalFrames - readTotal);
                        int r = ring.Read(chunk, 0, n, zeroFillMissing: false);
                        if (r == 0) {
                            Thread.Yield();
                            continue;
                        }
                        for (int i = 0; i < r; i++) {
                            float expected = (readTotal + i) * 0.001f;
                            if (MathF.Abs(chunk[i * 2] - expected) > 1e-4f || MathF.Abs(chunk[i * 2 + 1] + expected) > 1e-4f) {
                                checksumMismatch++;
                            }
                        }
                        readTotal += r;
                    }
                }, ct);
                await Task.WhenAll(producer, consumer).ConfigureAwait(false);
                if (checksumMismatch > 0) {
                    AddFinding("CRITICAL", "ringbuffer_data_corruption", "spsc_10k_frames",
                        $"LockFreeAudioRingBuffer had {checksumMismatch} sample mismatch(es) under concurrent SPSC stress");
                }
            } catch (System.OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                AddFinding("HIGH", "ringbuffer_exception", "spsc_10k_frames",
                    $"LockFreeAudioRingBuffer threw {ex.GetType().Name}: {ex.Message}");
            }

            // 4. Malformed Media File Analyzer Torture (zero-byte, truncated RIFF, garbage FLAC)
            scenariosRun++;
            string fuzzDir = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "chaos_inapp");
            try {
                Directory.CreateDirectory(fuzzDir);
                (string name, byte[] bytes)[] badFiles = [
                    ("empty.flac", []),
                    ("trunc.wav", Encoding.ASCII.GetBytes("RIFF\x24\x00\x00\x00WAVEfmt ")),
                    ("garbage.flac", Encoding.ASCII.GetBytes("fLaC\x00\x00\x00\x22BAD_HEADER_BYTES")),
                    ("garbage.mp3", Encoding.ASCII.GetBytes("ID3\x03\x00\x00\x00\x00\x00\x00NOT_MP3"))
                ];
                foreach ((string fname, byte[] data) in badFiles) {
                    string p = Path.Combine(fuzzDir, fname);
                    File.WriteAllBytes(p, data);
                    try {
                        LibraryTrackItem item = new() { Title = fname, FilePath = p, FileSizeBytes = data.Length };
                        AudioQualityReport rep = AudioQualityAnalyzer.EvaluateFast(item, context, allowIo: true);
                        if (rep == null) {
                            AddFinding("MEDIUM", "analyzer_null_report", $"malformed_{fname}",
                                $"AudioQualityAnalyzer.EvaluateFast returned null on {fname}");
                        }
                    } catch (Exception ex) {
                        AddFinding("HIGH", "analyzer_crash_malformed", $"malformed_{fname}",
                            $"AudioQualityAnalyzer threw {ex.GetType().Name} on {fname}: {ex.Message}");
                    }
                }
            } finally {
                try {
                    if (Directory.Exists(fuzzDir)) {
                        Directory.Delete(fuzzDir, recursive: true);
                    }
                } catch {
                    // Best effort cleanup
                }
            }

            // 5. Edge-case Lookup & Localization Fuzzing (empty/null/garbage IDs must never throw)
            scenariosRun++;
            try {
                _ = BinauralProfileService.FindById(null);
                _ = BinauralProfileService.FindById("");
                _ = BinauralProfileService.FindById("nonexistent_profile_id_999");
                string missingKey = "ChaosProbe_" + Guid.NewGuid().ToString("N")[..6];
                _ = AndroidStrings.Active[""];
                _ = AndroidStrings.Active[missingKey];
            } catch (Exception ex) {
                AddFinding("HIGH", "lookup_edge_exception", "null_garbage_lookups",
                    $"Service lookup threw {ex.GetType().Name}: {ex.Message}");
            }

            totalSw.Stop();
            return (true, JsonSerializer.Serialize(new {
                ok = true,
                rounds = clampedRounds,
                scenariosRun,
                elapsedMs = totalSw.ElapsedMilliseconds,
                anomalyCount = found.Count,
                anomalies = found.Select(a => new {
                    utc = a.Utc,
                    severity = a.Severity,
                    category = a.Category,
                    source = a.Source,
                    scenario = a.Scenario,
                    message = a.Message
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
        } finally {
            chaosGate.Release();
        }
    }
}
