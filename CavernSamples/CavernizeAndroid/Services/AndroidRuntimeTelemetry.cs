using System.Globalization;
using System.Text.Json;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;

using Cavern.Neural;
using CavernizeAndroid.Audio;

namespace CavernizeAndroid.Services;

/// <summary>
/// Unified Stage-2 (T2) Android Runtime Telemetry &amp; Global Background Compute QoS service:
/// <list type="bullet">
/// <item><b>Global AI/Export QoS &amp; Thermal Yield (Bug #11):</b> lowers heavy worker thread OS nice priority to
/// <see cref="Android.OS.ThreadPriority.Background"/> and adapts inter-chunk breathing yield to Android
/// <see cref="PowerManager.CurrentThermalStatus"/> (API &gt;= 29) so UI/OS and RT audio never starve.</item>
/// <item><b>FrameMetrics Telemetry (H2, API &gt;= 24):</b> background <see cref="HandlerThread"/> listener keeping a
/// 240-frame ring buffer (&lt;4 KB) for live P50/P95/P99 frame times, jank% (&gt;16.67 ms), and frozen frames (&gt;700 ms).</item>
/// <item><b>ApplicationExitInfo Telemetry (H2, API &gt;= 30):</b> reads up to 5 historical process exit reasons
/// (ANR / LOW_MEMORY / CRASH / CRASH_NATIVE / USER_REQUESTED) with PSS/RSS.</item>
/// <item><b>AAudio &amp; AudioTrack XRun Counters (H2):</b> exposes live native AAudio output/input XRuns alongside
/// legacy <see cref="Android.Media.AudioTrack.UnderrunCount"/>.</item>
/// </list>
/// </summary>
public static class AndroidRuntimeTelemetry {
    const int FrameRingCapacity = 240;
    const double JankBudgetMs = 16.67;
    const double Jank120HzBudgetMs = 12.0;
    const double FrozenBudgetMs = 700.0;
    const int MaxStrictModeViolations = 64;

    static readonly DateTime processStartUtc = DateTime.UtcNow;
    static readonly object frameLock = new();
    static readonly double[] frameTotalMsRing = new double[FrameRingCapacity];
    static readonly double[] frameCpuMsRing = new double[FrameRingCapacity];
    static int frameWriteIdx;
    static int frameCount;
    static long totalFramesObserved;
    static long totalJankFrames;
    static long total120HzJankFrames;
    static long totalDeadlineMissedFrames;
    static long totalFrozenFrames;
    static long totalDroppedFrames;
    static double maxFrameMsObserved;
    static float cachedRefreshRateHz = 60f;
    static DateTime frameWindowStartUtc = DateTime.UtcNow;

    static readonly object strictLock = new();
    static readonly Queue<StrictModeViolationEntry> strictViolations = new(MaxStrictModeViolations + 4);
    static bool strictModeEnabled;
    static long totalStrictModeViolations;
    static StrictMode.ThreadPolicy savedThreadPolicy;
    static StrictMode.VmPolicy savedVmPolicy;
    static Java.Util.Concurrent.IExecutor strictExecutor;
    static Java.Lang.Object strictThreadListener;
    static Java.Lang.Object strictVmListener;

    static Context cachedAppContext;
    static HandlerThread metricsThread;
    static Handler metricsHandler;
    static FrameMetricsListenerImpl activeFrameListener;
    static ThermalStatusListenerImpl activeThermalListener;
    static int cachedThermalStatus = -1;
    static int lastLoggedThermalLevel = -1;

    static int computeBudgetTier = 1; // 0 = Eco, 1 = Balanced (default), 2 = Fast (Max 75%)

    sealed class StrictModeViolationEntry {
        public string Utc { get; init; } = "";
        public string Kind { get; init; } = "thread";
        public string Violation { get; init; } = "";
        public string StackTop { get; init; } = "";
    }

    /// <summary>UTC process startup timestamp for external crash/restart correlation.</summary>
    public static DateTime ProcessStartUtc => processStartUtc;

    /// <summary>Active AI compute budget tier (0 = Eco, 1 = Balanced, 2 = Fast Max 75%).</summary>
    public static int ComputeBudgetTier => Volatile.Read(ref computeBudgetTier);

    /// <summary>Updates the global CPU/GPU/NPU headroom budget tier across native ONNX, DSP pipelines, and FFmpeg.</summary>
    public static void SetComputeBudgetTier(int tier) {
        int clamped = Math.Clamp(tier, 0, 2);
        Volatile.Write(ref computeBudgetTier, clamped);
        MvsepAntiArtifactPipeline.GlobalComputeBudgetTier = clamped;
    }

    /// <summary>
    /// Returns the Qualcomm QNN HTP NPU performance profile for the active compute budget tier.
    /// Never uses <c>burst</c> so the NPU &amp; SoC thermal envelope preserve headroom for the OS and 120 Hz UI.
    /// </summary>
    public static string GetQnnPerformanceMode(int? tierOverride = null) {
        int tier = Math.Clamp(tierOverride ?? ComputeBudgetTier, 0, 2);
        return tier switch {
            0 => "power_saver",
            2 => "sustained_high_performance",
            _ => "balanced"
        };
    }

    /// <summary>
    /// Computes an adaptive inter-chunk GPU/NPU duty-cycle yield (in ms) so continuous WebGPU (Vulkan) or QNN HTP
    /// dispatches always leave command-queue headroom for Android SurfaceFlinger and UI RenderThread.
    /// </summary>
    public static int GetAdaptiveAcceleratorYieldMs(long chunkElapsedMs, bool isGpuOrNpu, int? tierOverride = null) {
        int tier = Math.Clamp(tierOverride ?? ComputeBudgetTier, 0, 2);
        int thermalExtra = GetAdaptiveThermalYieldMs();
        if (!isGpuOrNpu) {
            return thermalExtra;
        }
        if (chunkElapsedMs < 12L) {
            return Math.Min(60, thermalExtra);
        }
        double dutyRatio = tier switch {
            0 => 0.65, // Eco: generous 65% breathing window between GPU/NPU dispatches
            2 => 0.20, // Fast (Max 75%): guarantees >=20% GPU/NPU headroom
            _ => 0.35  // Balanced (default): ~60% active duty, ~40% UI/OS headroom
        };
        int baseYield = (int)Math.Round(chunkElapsedMs * dutyRatio);
        int minYield = tier == 0 ? 28 : (tier == 2 ? 14 : 20);
        int maxYield = tier == 0 ? 180 : (tier == 2 ? 80 : 125);
        return Math.Clamp(baseYield + thermalExtra, minYield, maxYield);
    }

    /// <summary>
    /// Installs global background compute QoS and live Android thermal yield hooks into
    /// <see cref="MvsepAntiArtifactPipeline"/> so every AI / export / extraction tab inherits non-starving scheduling.
    /// </summary>
    public static void InstallGlobalComputeQoSHooks(Context context, int initialBudgetTier = 1) {
        cachedAppContext = context?.ApplicationContext ?? context;
        SetComputeBudgetTier(initialBudgetTier);
        AttachThermalStatusListener(cachedAppContext);
        MvsepAntiArtifactPipeline.GlobalBackgroundThreadQoSHook = ApplyBackgroundThreadQoS;
        MvsepAntiArtifactPipeline.GlobalThermalExtraYieldMsProvider = GetAdaptiveThermalYieldMs;
    }

    /// <summary>
    /// Lowers the calling background worker thread's OS scheduling priority so Android SurfaceFlinger/UI and
    /// <c>CavernizeAudioOut</c> (<c>UrgentAudio</c>) never experience CPU starvation during AI/export workloads.
    /// </summary>
    public static void ApplyBackgroundThreadQoS() {
        try {
            if (Looper.MainLooper != null && Looper.MyLooper() == Looper.MainLooper) {
                return;
            }
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.Background);
        } catch {
            // Ignore on restricted environments
        }
        try {
            if (System.Threading.Thread.CurrentThread.Priority > System.Threading.ThreadPriority.BelowNormal) {
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            }
        } catch {
            // Ignore
        }
    }

    /// <summary>
    /// Returns the recommended additional inter-chunk breathing yield in milliseconds (0..100 ms)
    /// derived from Android's live thermal status (<c>PowerManager.CurrentThermalStatus</c>, API &gt;= 29).
    /// </summary>
    public static int GetAdaptiveThermalYieldMs() {
        int level = GetThermalStatusCode(cachedAppContext);
        if (level >= 3 && level != lastLoggedThermalLevel) {
            lastLoggedThermalLevel = level;
            AppLogger.Warn($"ComputeQoS: elevated Android thermal status ({DescribeThermalStatus(level)}) -> throttling background AI yield.");
        } else if (level <= 1 && lastLoggedThermalLevel >= 3) {
            lastLoggedThermalLevel = level;
        }
        return level switch {
            <= 1 => 0,   // None / Light
            2 => 20,     // Moderate
            3 => 60,     // Severe
            _ => 100     // Critical / Emergency / Shutdown
        };
    }

    /// <summary>
    /// Attaches Android's sanctioned <see cref="PowerManager.IOnThermalStatusChangedListener"/> (API &gt;= 29)
    /// once per process so <see cref="GetThermalStatusCode"/> reflects live thermal callbacks with zero IPC overhead.
    /// </summary>
    public static void AttachThermalStatusListener(Context context) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || context == null || activeThermalListener != null) {
            return;
        }
        try {
            if (context.GetSystemService(Context.PowerService) is PowerManager pm) {
                Volatile.Write(ref cachedThermalStatus, (int)pm.CurrentThermalStatus);
                ThermalStatusListenerImpl listener = new();
                pm.AddThermalStatusListener(listener);
                activeThermalListener = listener;
            }
        } catch {
            // Fall back cleanly to CurrentThermalStatus polling
        }
    }

    sealed class ThermalStatusListenerImpl : Java.Lang.Object, PowerManager.IOnThermalStatusChangedListener {
        public void OnThermalStatusChanged(ThermalStatus status) =>
            Volatile.Write(ref cachedThermalStatus, (int)status);
    }

    /// <summary>
    /// Reads Android's current thermal status level (0=None, 1=Light, 2=Moderate, 3=Severe, 4=Critical, 5=Emergency, 6=Shutdown; -1 if unsupported).
    /// </summary>
    public static int GetThermalStatusCode(Context context) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) {
            return -1;
        }
        int cached = Volatile.Read(ref cachedThermalStatus);
        if (cached >= 0 && activeThermalListener != null) {
            return cached;
        }
        if (context == null) {
            return cached;
        }
        try {
            if (context.GetSystemService(Context.PowerService) is PowerManager pm) {
                int current = (int)pm.CurrentThermalStatus;
                Volatile.Write(ref cachedThermalStatus, current);
                return current;
            }
        } catch {
            // Ignore
        }
        return cached;
    }

    /// <summary>Returns a compact human-readable label for an Android thermal status code.</summary>
    public static string DescribeThermalStatus(int code) => code switch {
        0 => "NONE",
        1 => "LIGHT",
        2 => "MODERATE",
        3 => "SEVERE",
        4 => "CRITICAL",
        5 => "EMERGENCY",
        6 => "SHUTDOWN",
        _ => "UNSUPPORTED"
    };

    /// <summary>
    /// Attaches a zero-UI-overhead <see cref="Window.IOnFrameMetricsAvailableListener"/> (API &gt;= 24)
    /// on a dedicated background <see cref="HandlerThread"/> to track live 120 Hz P50/P95/P99/CPU/jank/frozen metrics.
    /// </summary>
    public static void AttachFrameMetrics(Activity activity) {
        if (activity == null || activity.Window == null || !OperatingSystem.IsAndroidVersionAtLeast(24)) {
            return;
        }
        try {
            DetachFrameMetrics(activity);
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) {
                float hz = activity.Display?.RefreshRate ?? 60f;
                if (float.IsFinite(hz) && hz >= 24f && hz <= 240f) {
                    lock (frameLock) {
                        cachedRefreshRateHz = hz;
                    }
                }
            }
            metricsThread = new HandlerThread("CavernizeFrameMetrics", (int)Android.OS.ThreadPriority.Background);
            metricsThread.Start();
            Looper looper = metricsThread.Looper;
            if (looper == null) {
                metricsThread.QuitSafely();
                metricsThread.Dispose();
                metricsThread = null;
                return;
            }
            metricsHandler = new Handler(looper);
            activeFrameListener = new FrameMetricsListenerImpl();
            activity.Window.AddOnFrameMetricsAvailableListener(activeFrameListener, metricsHandler);
        } catch (Exception ex) {
            AppLogger.Warn($"AttachFrameMetrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Detaches the active <see cref="Window.IOnFrameMetricsAvailableListener"/> and stops its background thread cleanly.
    /// </summary>
    public static void DetachFrameMetrics(Activity activity) {
        try {
            if (OperatingSystem.IsAndroidVersionAtLeast(24) && activity?.Window != null && activeFrameListener != null) {
                activity.Window.RemoveOnFrameMetricsAvailableListener(activeFrameListener);
            }
        } catch {
            // Ignore
        }
        try {
            activeFrameListener?.Dispose();
        } catch {
            // Ignore
        }
        activeFrameListener = null;
        try {
            metricsHandler?.Dispose();
        } catch {
            // Ignore
        }
        metricsHandler = null;
        try {
            metricsThread?.QuitSafely();
            metricsThread?.Dispose();
        } catch {
            // Ignore
        }
        metricsThread = null;
    }

    /// <summary>
    /// Clears the active FrameMetrics sampling window so Q2 motion gates measure fresh interaction frames
    /// without stale cold-startup spikes.
    /// </summary>
    public static void ResetFrameMetricsWindow() {
        lock (frameLock) {
            Array.Clear(frameTotalMsRing, 0, frameTotalMsRing.Length);
            Array.Clear(frameCpuMsRing, 0, frameCpuMsRing.Length);
            frameWriteIdx = 0;
            frameCount = 0;
            totalFramesObserved = 0;
            totalJankFrames = 0;
            total120HzJankFrames = 0;
            totalDeadlineMissedFrames = 0;
            totalFrozenFrames = 0;
            totalDroppedFrames = 0;
            maxFrameMsObserved = 0.0;
            frameWindowStartUtc = DateTime.UtcNow;
        }
    }

    static void RecordFrameSample(double totalDurationMs, double cpuWorkMs, bool missedDeadline, int droppedCount) {
        if (!double.IsFinite(totalDurationMs) || totalDurationMs <= 0.0 || totalDurationMs > 30000.0) {
            return;
        }
        double safeCpu = double.IsFinite(cpuWorkMs) && cpuWorkMs > 0.0 && cpuWorkMs <= 30000.0
            ? cpuWorkMs
            : totalDurationMs;
        lock (frameLock) {
            frameTotalMsRing[frameWriteIdx] = totalDurationMs;
            frameCpuMsRing[frameWriteIdx] = safeCpu;
            frameWriteIdx = (frameWriteIdx + 1) % FrameRingCapacity;
            if (frameCount < FrameRingCapacity) {
                frameCount++;
            }
            totalFramesObserved++;
            if (totalDurationMs > JankBudgetMs) {
                totalJankFrames++;
            }
            if (totalDurationMs > Jank120HzBudgetMs || missedDeadline) {
                total120HzJankFrames++;
            }
            if (missedDeadline) {
                totalDeadlineMissedFrames++;
            }
            if (totalDurationMs > FrozenBudgetMs) {
                totalFrozenFrames++;
            }
            if (droppedCount > 0) {
                totalDroppedFrames += droppedCount;
            }
            if (totalDurationMs > maxFrameMsObserved) {
                maxFrameMsObserved = totalDurationMs;
            }
        }
    }

    /// <summary>Computes a point-in-time snapshot of live 120 Hz FrameMetrics statistics from the bounded ring buffer.</summary>
    public static object BuildFrameMetricsObject() {
        double[] totalCopy;
        double[] cpuCopy;
        long total;
        long jank;
        long jank120;
        long deadlineMissed;
        long frozen;
        long dropped;
        double maxObs;
        float refreshHz;
        DateTime winStart;
        lock (frameLock) {
            totalCopy = new double[frameCount];
            cpuCopy = new double[frameCount];
            Array.Copy(frameTotalMsRing, totalCopy, frameCount);
            Array.Copy(frameCpuMsRing, cpuCopy, frameCount);
            total = totalFramesObserved;
            jank = totalJankFrames;
            jank120 = total120HzJankFrames;
            deadlineMissed = totalDeadlineMissedFrames;
            frozen = totalFrozenFrames;
            dropped = totalDroppedFrames;
            maxObs = maxFrameMsObserved;
            refreshHz = cachedRefreshRateHz;
            winStart = frameWindowStartUtc;
        }
        Array.Sort(totalCopy);
        Array.Sort(cpuCopy);
        double p50 = Percentile(totalCopy, 0.50);
        double p95 = Percentile(totalCopy, 0.95);
        double p99 = Percentile(totalCopy, 0.99);
        double windowMax = totalCopy.Length > 0 ? totalCopy[^1] : 0.0;
        double cpuP50 = Percentile(cpuCopy, 0.50);
        double cpuP95 = Percentile(cpuCopy, 0.95);
        double cpuP99 = Percentile(cpuCopy, 0.99);
        double cpuWindowMax = cpuCopy.Length > 0 ? cpuCopy[^1] : 0.0;
        double jankPct = total > 0 ? (jank * 100.0 / total) : 0.0;
        double jank120Pct = total > 0 ? (jank120 * 100.0 / total) : 0.0;
        double targetBudgetMs = refreshHz > 1f ? (1000.0 / refreshHz) : JankBudgetMs;
        return new {
            supported = OperatingSystem.IsAndroidVersionAtLeast(24),
            listenerAttached = activeFrameListener != null,
            windowStartUtc = winStart.ToString("O", CultureInfo.InvariantCulture),
            refreshRateHz = Math.Round(refreshHz, 1),
            targetFrameBudgetMs = Math.Round(targetBudgetMs, 2),
            ringCount = totalCopy.Length,
            ringCapacity = FrameRingCapacity,
            totalFrames = total,
            jankFrames = jank,
            jankPct = Math.Round(jankPct, 2),
            jank120HzFrames = jank120,
            jank120HzPct = Math.Round(jank120Pct, 2),
            deadlineMissedFrames = deadlineMissed,
            droppedFrames = dropped,
            frozenFrames = frozen,
            p50Ms = Math.Round(p50, 2),
            p95Ms = Math.Round(p95, 2),
            p99Ms = Math.Round(p99, 2),
            windowMaxMs = Math.Round(windowMax, 2),
            sessionMaxMs = Math.Round(maxObs, 2),
            cpuP50Ms = Math.Round(cpuP50, 2),
            cpuP95Ms = Math.Round(cpuP95, 2),
            cpuP99Ms = Math.Round(cpuP99, 2),
            cpuWindowMaxMs = Math.Round(cpuWindowMax, 2)
        };
    }

    /// <summary>
    /// Enables or disables the opt-in Q2 <see cref="StrictMode"/> main-thread/VM violation detector.
    /// Must be invoked on the Android main UI thread so <see cref="StrictMode.ThreadPolicy"/> binds to the UI looper.
    /// </summary>
    public static Task<(bool ok, string json)> ConfigureQaStrictModeAsync(
        bool enable,
        bool detectDisk = true,
        bool detectNetwork = true,
        bool detectLeaks = false,
        bool clearHistory = false) {
        if (clearHistory) {
            ClearStrictModeViolations();
        }
        Looper mainLooper = Looper.MainLooper;
        if (mainLooper == null) {
            return Task.FromResult((false, "Android MainLooper unavailable."));
        }
        TaskCompletionSource<(bool, string)> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action apply = () => {
            try {
                ApplyStrictModeOnMainThread(enable, detectDisk, detectNetwork, detectLeaks);
                string json = JsonSerializer.Serialize(new {
                    ok = true,
                    strictMode = BuildStrictModeObject()
                }, new JsonSerializerOptions { WriteIndented = true });
                tcs.TrySetResult((true, json));
            } catch (Exception ex) {
                tcs.TrySetResult((false, $"StrictMode config error: {ex.GetType().Name}: {ex.Message}"));
            }
        };
        if (Looper.MyLooper() == mainLooper) {
            apply();
        } else {
            new Handler(mainLooper).Post(apply);
        }
        return tcs.Task;
    }

    static void ApplyStrictModeOnMainThread(bool enable, bool detectDisk, bool detectNetwork, bool detectLeaks) {
        lock (strictLock) {
            if (!enable) {
                if (savedThreadPolicy != null) {
                    StrictMode.SetThreadPolicy(savedThreadPolicy);
                } else {
                    StrictMode.SetThreadPolicy(StrictMode.ThreadPolicy.Lax);
                }
                if (savedVmPolicy != null) {
                    StrictMode.SetVmPolicy(savedVmPolicy);
                }
                strictModeEnabled = false;
                return;
            }

            savedThreadPolicy ??= StrictMode.GetThreadPolicy();
            savedVmPolicy ??= StrictMode.GetVmPolicy();

            StrictMode.ThreadPolicy.Builder tb = new StrictMode.ThreadPolicy.Builder().PenaltyLog();
            if (detectDisk) {
                tb.DetectDiskReads().DetectDiskWrites();
            }
            if (detectNetwork) {
                tb.DetectNetwork();
            }
            tb.DetectCustomSlowCalls();
            if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                strictExecutor ??= new InlineStrictModeExecutor();
                strictThreadListener ??= new StrictModeThreadListenerImpl();
                tb.PenaltyListener(strictExecutor, (StrictMode.IOnThreadViolationListener)strictThreadListener);
            }
            StrictMode.SetThreadPolicy(tb.Build());

            if (detectLeaks) {
                StrictMode.VmPolicy.Builder vb = new StrictMode.VmPolicy.Builder()
                    .DetectLeakedClosableObjects()
                    .DetectActivityLeaks()
                    .DetectLeakedRegistrationObjects()
                    .PenaltyLog();
                if (OperatingSystem.IsAndroidVersionAtLeast(28)) {
                    strictExecutor ??= new InlineStrictModeExecutor();
                    strictVmListener ??= new StrictModeVmListenerImpl();
                    vb.PenaltyListener(strictExecutor, (StrictMode.IOnVmViolationListener)strictVmListener);
                }
                StrictMode.SetVmPolicy(vb.Build());
            }
            strictModeEnabled = true;
        }
    }

    /// <summary>Clears recorded QA StrictMode violations.</summary>
    public static void ClearStrictModeViolations() {
        lock (strictLock) {
            strictViolations.Clear();
            totalStrictModeViolations = 0;
        }
    }

    internal static void RecordStrictModeViolation(string kind, string violationMessage, string stackTop) {
        string cleanMsg = string.IsNullOrWhiteSpace(violationMessage) ? "StrictModeViolation" : violationMessage.Trim();
        if (cleanMsg.Length > 260) {
            cleanMsg = cleanMsg[..260];
        }
        string cleanStack = (stackTop ?? "").Trim();
        if (cleanStack.Length > 220) {
            cleanStack = cleanStack[..220];
        }
        lock (strictLock) {
            totalStrictModeViolations++;
            while (strictViolations.Count >= MaxStrictModeViolations) {
                strictViolations.Dequeue();
            }
            strictViolations.Enqueue(new StrictModeViolationEntry {
                Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Kind = kind ?? "thread",
                Violation = cleanMsg,
                StackTop = cleanStack
            });
        }
        LiveChaosBugHunter.RecordAnomaly(
            "HIGH",
            "strict_mode_violation",
            "StrictMode",
            kind ?? "thread",
            string.IsNullOrEmpty(cleanStack) ? cleanMsg : $"{cleanMsg} @ {cleanStack}");
    }

    /// <summary>Returns a point-in-time snapshot of opt-in QA StrictMode state and recorded violations.</summary>
    public static object BuildStrictModeObject() {
        bool enabled;
        long totalCount;
        List<object> items;
        lock (strictLock) {
            enabled = strictModeEnabled;
            totalCount = totalStrictModeViolations;
            items = strictViolations.Select(v => (object)new {
                utc = v.Utc,
                kind = v.Kind,
                violation = v.Violation,
                stackTop = v.StackTop
            }).ToList();
        }
        return new {
            supported = OperatingSystem.IsAndroidVersionAtLeast(28),
            enabled,
            violationCount = totalCount,
            recentCount = items.Count,
            violations = items
        };
    }

    /// <summary>Reads up to <paramref name="maxCount"/> historical Android <c>ApplicationExitInfo</c> records (API &gt;= 30).</summary>
    public static List<object> ReadHistoricalExitReasons(Context context, int maxCount = 8) {
        List<object> list = [];
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || context == null) {
            return list;
        }
        try {
            if (context.GetSystemService(Context.ActivityService) is ActivityManager am) {
                IList<ApplicationExitInfo> reasons = am.GetHistoricalProcessExitReasons(context.PackageName, 0, Math.Clamp(maxCount, 1, 10));
                if (reasons != null) {
                    for (int i = 0; i < reasons.Count; i++) {
                        ApplicationExitInfo info = reasons[i];
                        if (info == null) {
                            continue;
                        }
                        string tsUtc = info.Timestamp > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                            : "";
                        int imp = (int)info.Importance;
                        list.Add(new {
                            pid = info.Pid,
                            processName = info.ProcessName ?? "",
                            importance = imp,
                            importanceName = DescribeImportance(imp),
                            reasonCode = info.Reason,
                            reason = DescribeExitReason((ApplicationExitInfoReason)info.Reason),
                            status = info.Status,
                            timestampMs = info.Timestamp,
                            timestampUtc = tsUtc,
                            pssMb = Math.Round(info.Pss / 1024.0, 2),
                            rssMb = Math.Round(info.Rss / 1024.0, 2),
                            description = info.Description ?? ""
                        });
                    }
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"ReadHistoricalExitReasons: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Inspects own-UID historical Android <c>ApplicationExitInfo</c> records (API &gt;= 30, last 72h)
    /// to detect recent <c>REASON_LOW_MEMORY</c> kills for <see cref="Cavernize.Logic.Spatial.MemoryBudgetGovernor"/>.
    /// Uses sanctioned ActivityManager API only (zero procfs PSI reads — SELinux API 35 compliant).
    /// </summary>
    public static bool TryGetRecentLowMemoryExitInfo(
        Context context,
        out bool hadRecentLowMemoryExit,
        out long lastLowMemoryExitPssBytes,
        long lookbackWindowMs = 72L * 3600L * 1000L) {
        hadRecentLowMemoryExit = false;
        lastLowMemoryExitPssBytes = 0L;
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || context == null) {
            return false;
        }
        try {
            if (context.GetSystemService(Context.ActivityService) is ActivityManager am) {
                IList<ApplicationExitInfo> reasons = am.GetHistoricalProcessExitReasons(context.PackageName, 0, 5);
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (reasons != null) {
                    for (int i = 0; i < reasons.Count; i++) {
                        ApplicationExitInfo info = reasons[i];
                        if (info == null) {
                            continue;
                        }
                        if ((ApplicationExitInfoReason)info.Reason == ApplicationExitInfoReason.LowMemory) {
                            bool withinWindow = info.Timestamp <= 0L || (nowMs - info.Timestamp) <= lookbackWindowMs;
                            if (withinWindow) {
                                hadRecentLowMemoryExit = true;
                                lastLowMemoryExitPssBytes = info.Pss > 0L ? info.Pss * 1024L : 0L;
                                return true;
                            }
                        }
                    }
                }
                return true;
            }
        } catch {
            // Best-effort own-UID telemetry
        }
        return false;
    }

    /// <summary>Builds the unified AAudio + AudioTrack XRun telemetry payload.</summary>
    public static object BuildAudioXRunObject(AndroidAudioOutput audioOutput) {
        int aaudioOutXRuns = 0;
        int aaudioInXRuns = 0;
        bool nativeEngineRunning = false;
        string activePath = "AudioTrack";
        int bufferFrames = 0;
        int nativeSr = 0;
        try {
            if (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st)) {
                aaudioOutXRuns = st.OutputXRuns;
                aaudioInXRuns = st.InputXRuns;
                nativeEngineRunning = st.ActiveBackend != 0;
                activePath = st.ActiveBackend == 1 ? "AAudio" : (st.ActiveBackend == 2 ? "USB-Direct" : "AudioTrack");
                bufferFrames = st.BufferFrames;
                nativeSr = st.SampleRate;
            }
        } catch {
            // Native engine not initialized
        }
        int audioTrackUnderruns = audioOutput != null ? audioOutput.LastAudioTrackUnderrunCount : 0;
        return new {
            activePath,
            nativeEngineRunning,
            sampleRate = nativeSr,
            bufferFrames,
            aaudioOutputXRuns = aaudioOutXRuns,
            aaudioInputXRuns = aaudioInXRuns,
            audioTrackUnderruns,
            totalOutputUnderruns = aaudioOutXRuns + audioTrackUnderruns
        };
    }

    /// <summary>Counts currently open Linux file descriptors in <c>/proc/self/fd</c> (-1 if unavailable).</summary>
    public static int ReadOpenFdCount() {
        try {
            if (System.IO.Directory.Exists("/proc/self/fd")) {
                int count = 0;
                foreach (string _ in System.IO.Directory.EnumerateFileSystemEntries("/proc/self/fd")) {
                    count++;
                }
                return count;
            }
        } catch {
            // Restricted environment
        }
        return -1;
    }

    /// <summary>Counts active OS threads for this process via <c>/proc/self/task</c> (with managed fallback).</summary>
    public static int ReadThreadCount() {
        try {
            if (System.IO.Directory.Exists("/proc/self/task")) {
                int count = 0;
                foreach (string _ in System.IO.Directory.EnumerateFileSystemEntries("/proc/self/task")) {
                    count++;
                }
                return count;
            }
        } catch {
            // Fall back to Process.Threads
        }
        try {
            return System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        } catch {
            return -1;
        }
    }

    /// <summary>Reads current process Total PSS, Native PSS, Dalvik PSS, Other PSS, and Native Heap allocated (all in MB).</summary>
    public static (double totalPssMb, double nativePssMb, double dalvikPssMb, double otherPssMb, double nativeHeapAllocMb) ReadProcessMemoryBreakdownMb() {
        double totalPss = -1.0;
        double nativePss = -1.0;
        double dalvikPss = -1.0;
        double otherPss = -1.0;
        double nativeHeap = -1.0;
        try {
            using Android.OS.Debug.MemoryInfo dmi = new();
            Android.OS.Debug.GetMemoryInfo(dmi);
            totalPss = Math.Round(dmi.TotalPss / 1024.0, 2);
            nativePss = Math.Round(dmi.NativePss / 1024.0, 2);
            dalvikPss = Math.Round(dmi.DalvikPss / 1024.0, 2);
            otherPss = Math.Round(dmi.OtherPss / 1024.0, 2);
        } catch {
            // Ignore
        }
        try {
            nativeHeap = Math.Round(Android.OS.Debug.NativeHeapAllocatedSize / (1024.0 * 1024.0), 2);
        } catch {
            // Ignore
        }
        return (totalPss, nativePss, dalvikPss, otherPss, nativeHeap);
    }

    /// <summary>
    /// Builds a compact Q3 dirty-state &amp; resource soak snapshot (Total/Native PSS, managed GC heap,
    /// <c>/proc/self/fd</c> open FD count, OS thread count, View tree node count, active dialogs,
    /// <c>isOperationRunning</c>, and session error/fatal counts).
    /// </summary>
    public static string BuildSoakSnapshotJson(
        Context context,
        bool forceGc = false,
        int viewCount = -1,
        int clickableViewCount = -1,
        int activeDialogs = -1,
        bool isOperationRunning = false,
        int activeTab = -1) {
        if (forceGc) {
            try {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            } catch {
                // Ignore
            }
        }
        (double totalPssMb, double nativePssMb, double dalvikPssMb, double otherPssMb, double nativeHeapAllocMb) =
            ReadProcessMemoryBreakdownMb();
        double managedGcMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2);
        int openFdCount = ReadOpenFdCount();
        int threadCount = ReadThreadCount();
        bool lowMem = false;
        long availRamMb = -1;
        try {
            if ((context ?? cachedAppContext)?.GetSystemService(Context.ActivityService) is ActivityManager am) {
                ActivityManager.MemoryInfo mi = new();
                am.GetMemoryInfo(mi);
                lowMem = mi.LowMemory;
                availRamMb = mi.AvailMem / (1024 * 1024);
            }
        } catch {
            // Ignore
        }
        return JsonSerializer.Serialize(new {
            ok = true,
            pid = Android.OS.Process.MyPid(),
            forceGc,
            nativePssMb = totalPssMb,
            nativeOnlyPssMb = nativePssMb,
            dalvikPssMb,
            otherPssMb,
            nativeHeapAllocMb,
            managedGcMb,
            availRamMb,
            lowMemory = lowMem,
            openFdCount,
            threadCount,
            viewCount,
            clickableViewCount,
            activeDialogs,
            isOperationRunning,
            activeTab,
            sessionWarnCount = AppLogger.SessionWarnCount,
            sessionErrorCount = AppLogger.SessionErrorCount,
            sessionFatalCount = AppLogger.SessionFatalCount,
            utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Builds the full JSON response for the Tier-0 Live Bridge <c>runtime_telemetry</c> command.</summary>
    public static string BuildRuntimeTelemetryJson(Context context, AndroidAudioOutput audioOutput, int maxExitReasons = 8) {
        Context ctx = context ?? cachedAppContext;
        int thermalCode = GetThermalStatusCode(ctx);
        int extraYieldMs = GetAdaptiveThermalYieldMs();
        (double totalPssMb, _, _, _, double nativeHeapAllocMb) = ReadProcessMemoryBreakdownMb();
        return JsonSerializer.Serialize(new {
            ok = true,
            pid = Android.OS.Process.MyPid(),
            processStartUtc = processStartUtc.ToString("O", CultureInfo.InvariantCulture),
            timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            apiLevel = (int)Build.VERSION.SdkInt,
            processorCount = System.Environment.ProcessorCount,
            processHealth = new {
                nativePssMb = totalPssMb,
                nativeHeapAllocMb,
                managedGcMb = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 2),
                openFdCount = ReadOpenFdCount(),
                threadCount = ReadThreadCount()
            },
            computeQoS = new {
                headroomWorkerCap = MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(System.Environment.ProcessorCount),
                onnxAllowSpinning = false,
                thermalStatusCode = thermalCode,
                thermalStatus = DescribeThermalStatus(thermalCode),
                adaptiveThermalYieldMs = extraYieldMs
            },
            frameMetrics = BuildFrameMetricsObject(),
            strictMode = BuildStrictModeObject(),
            audioXRuns = BuildAudioXRunObject(audioOutput),
            exitReasonsSupported = OperatingSystem.IsAndroidVersionAtLeast(30),
            exitReasons = ReadHistoricalExitReasons(ctx, maxExitReasons)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    static double Percentile(double[] sorted, double p) {
        if (sorted == null || sorted.Length == 0) {
            return 0.0;
        }
        int idx = (int)Math.Floor((sorted.Length - 1) * Math.Clamp(p, 0.0, 1.0));
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    static string DescribeImportance(int importance) => importance switch {
        <= 100 => "FOREGROUND",
        <= 125 => "FOREGROUND_SERVICE",
        <= 200 => "VISIBLE",
        <= 230 => "PERCEPTIBLE",
        <= 300 => "SERVICE",
        <= 325 => "TOP_SLEEPING",
        <= 350 => "CANT_SAVE_STATE",
        <= 400 => "CACHED",
        _ => "GONE"
    };

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    static string DescribeExitReason(ApplicationExitInfoReason reason) => reason switch {
        ApplicationExitInfoReason.ExitSelf => "EXIT_SELF",
        ApplicationExitInfoReason.Signaled => "SIGNALED",
        ApplicationExitInfoReason.LowMemory => "LOW_MEMORY",
        ApplicationExitInfoReason.Crash => "CRASH",
        ApplicationExitInfoReason.CrashNative => "CRASH_NATIVE",
        ApplicationExitInfoReason.Anr => "ANR",
        ApplicationExitInfoReason.InitializationFailure => "INITIALIZATION_FAILURE",
        ApplicationExitInfoReason.PermissionChange => "PERMISSION_CHANGE",
        ApplicationExitInfoReason.ExcessiveResourceUsage => "EXCESSIVE_RESOURCE_USAGE",
        ApplicationExitInfoReason.UserRequested => "USER_REQUESTED",
        ApplicationExitInfoReason.UserStopped => "USER_STOPPED",
        ApplicationExitInfoReason.DependencyDied => "DEPENDENCY_DIED",
        ApplicationExitInfoReason.Other => "OTHER",
        _ => reason.ToString().ToUpperInvariant()
    };

    sealed class InlineStrictModeExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor {
        public void Execute(Java.Lang.IRunnable command) {
            try {
                command?.Run();
            } catch {
                // Never crash caller on StrictMode callback
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    sealed class StrictModeThreadListenerImpl : Java.Lang.Object, StrictMode.IOnThreadViolationListener {
        public void OnThreadViolation(Android.OS.Strictmode.Violation v) {
            string msg = v != null ? $"{v.Class?.SimpleName ?? "ThreadViolation"}: {v.Message}" : "ThreadViolation";
            string top = ExtractTopStackFrame(v);
            RecordStrictModeViolation("thread", msg, top);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    sealed class StrictModeVmListenerImpl : Java.Lang.Object, StrictMode.IOnVmViolationListener {
        public void OnVmViolation(Android.OS.Strictmode.Violation v) {
            string msg = v != null ? $"{v.Class?.SimpleName ?? "VmViolation"}: {v.Message}" : "VmViolation";
            string top = ExtractTopStackFrame(v);
            RecordStrictModeViolation("vm", msg, top);
        }
    }

    static string ExtractTopStackFrame(Java.Lang.Throwable t) {
        if (t == null) {
            return "";
        }
        try {
            Java.Lang.StackTraceElement[] frames = t.GetStackTrace();
            if (frames == null || frames.Length == 0) {
                return "";
            }
            for (int i = 0; i < frames.Length; i++) {
                string s = frames[i]?.ToString() ?? "";
                if (!s.Contains("android.os.StrictMode", StringComparison.OrdinalIgnoreCase) &&
                    !s.Contains("java.lang.Thread", StringComparison.OrdinalIgnoreCase)) {
                    return s;
                }
            }
            return frames[0]?.ToString() ?? "";
        } catch {
            return "";
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android24.0")]
    sealed class FrameMetricsListenerImpl : Java.Lang.Object, Window.IOnFrameMetricsAvailableListener {
        public void OnFrameMetricsAvailable(Window window, FrameMetrics frameMetrics, int dropCountSinceLastInvocation) {
            if (frameMetrics == null) {
                return;
            }
            long totalNs = frameMetrics.GetMetric((int)FrameMetricsId.TotalDuration);
            if (totalNs <= 0) {
                return;
            }
            long cpuNs =
                Math.Max(0L, frameMetrics.GetMetric((int)FrameMetricsId.InputHandlingDuration)) +
                Math.Max(0L, frameMetrics.GetMetric((int)FrameMetricsId.AnimationDuration)) +
                Math.Max(0L, frameMetrics.GetMetric((int)FrameMetricsId.LayoutMeasureDuration)) +
                Math.Max(0L, frameMetrics.GetMetric((int)FrameMetricsId.DrawDuration)) +
                Math.Max(0L, frameMetrics.GetMetric((int)FrameMetricsId.SyncDuration));
            bool missedDeadline = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
                long deadlineNs = frameMetrics.GetMetric((int)FrameMetricsId.Deadline);
                if (deadlineNs > 0 && totalNs > deadlineNs) {
                    missedDeadline = true;
                }
            }
            RecordFrameSample(totalNs / 1_000_000.0, cpuNs / 1_000_000.0, missedDeadline, dropCountSinceLastInvocation);
        }
    }
}
