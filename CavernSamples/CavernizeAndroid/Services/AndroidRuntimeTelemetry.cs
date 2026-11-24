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
    const double FrozenBudgetMs = 700.0;

    static readonly object frameLock = new();
    static readonly double[] frameTotalMsRing = new double[FrameRingCapacity];
    static int frameWriteIdx;
    static int frameCount;
    static long totalFramesObserved;
    static long totalJankFrames;
    static long totalFrozenFrames;
    static double maxFrameMsObserved;

    static Context cachedAppContext;
    static HandlerThread metricsThread;
    static Handler metricsHandler;
    static FrameMetricsListenerImpl activeFrameListener;
    static int lastLoggedThermalLevel = -1;

    /// <summary>
    /// Installs global background compute QoS and live Android thermal yield hooks into
    /// <see cref="MvsepAntiArtifactPipeline"/> so every AI / export / extraction tab inherits non-starving scheduling.
    /// </summary>
    public static void InstallGlobalComputeQoSHooks(Context context) {
        cachedAppContext = context?.ApplicationContext ?? context;
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
    /// Reads Android's current thermal status level (0=None, 1=Light, 2=Moderate, 3=Severe, 4=Critical, 5=Emergency, 6=Shutdown; -1 if unsupported).
    /// </summary>
    public static int GetThermalStatusCode(Context context) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29) || context == null) {
            return -1;
        }
        try {
            if (context.GetSystemService(Context.PowerService) is PowerManager pm) {
                return (int)pm.CurrentThermalStatus;
            }
        } catch {
            // Ignore
        }
        return -1;
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
    /// on a dedicated background <see cref="HandlerThread"/> to track live P50/P95/P99/jank%/frozen metrics.
    /// </summary>
    public static void AttachFrameMetrics(Activity activity) {
        if (activity == null || activity.Window == null || !OperatingSystem.IsAndroidVersionAtLeast(24)) {
            return;
        }
        try {
            DetachFrameMetrics(activity);
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

    static void RecordFrameDurationMs(double totalDurationMs) {
        if (!double.IsFinite(totalDurationMs) || totalDurationMs <= 0.0 || totalDurationMs > 30000.0) {
            return;
        }
        lock (frameLock) {
            frameTotalMsRing[frameWriteIdx] = totalDurationMs;
            frameWriteIdx = (frameWriteIdx + 1) % FrameRingCapacity;
            if (frameCount < FrameRingCapacity) {
                frameCount++;
            }
            totalFramesObserved++;
            if (totalDurationMs > JankBudgetMs) {
                totalJankFrames++;
            }
            if (totalDurationMs > FrozenBudgetMs) {
                totalFrozenFrames++;
            }
            if (totalDurationMs > maxFrameMsObserved) {
                maxFrameMsObserved = totalDurationMs;
            }
        }
    }

    /// <summary>Computes a point-in-time snapshot of live FrameMetrics statistics from the bounded ring buffer.</summary>
    public static object BuildFrameMetricsObject() {
        double[] copy;
        long total;
        long jank;
        long frozen;
        double maxObs;
        lock (frameLock) {
            copy = new double[frameCount];
            Array.Copy(frameTotalMsRing, copy, frameCount);
            total = totalFramesObserved;
            jank = totalJankFrames;
            frozen = totalFrozenFrames;
            maxObs = maxFrameMsObserved;
        }
        Array.Sort(copy);
        double p50 = Percentile(copy, 0.50);
        double p95 = Percentile(copy, 0.95);
        double p99 = Percentile(copy, 0.99);
        double windowMax = copy.Length > 0 ? copy[copy.Length - 1] : 0.0;
        double jankPct = total > 0 ? (jank * 100.0 / total) : 0.0;
        return new {
            supported = OperatingSystem.IsAndroidVersionAtLeast(24),
            listenerAttached = activeFrameListener != null,
            ringCount = copy.Length,
            ringCapacity = FrameRingCapacity,
            totalFrames = total,
            jankFrames = jank,
            jankPct = Math.Round(jankPct, 2),
            frozenFrames = frozen,
            p50Ms = Math.Round(p50, 2),
            p95Ms = Math.Round(p95, 2),
            p99Ms = Math.Round(p99, 2),
            windowMaxMs = Math.Round(windowMax, 2),
            sessionMaxMs = Math.Round(maxObs, 2)
        };
    }

    /// <summary>Reads up to <paramref name="maxCount"/> historical Android <c>ApplicationExitInfo</c> records (API &gt;= 30).</summary>
    public static List<object> ReadHistoricalExitReasons(Context context, int maxCount = 5) {
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
                        list.Add(new {
                            reasonCode = info.Reason,
                            reason = DescribeExitReason((ApplicationExitInfoReason)info.Reason),
                            status = info.Status,
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

    /// <summary>Builds the full JSON response for the Tier-0 Live Bridge <c>runtime_telemetry</c> command.</summary>
    public static string BuildRuntimeTelemetryJson(Context context, AndroidAudioOutput audioOutput) {
        Context ctx = context ?? cachedAppContext;
        int thermalCode = GetThermalStatusCode(ctx);
        int extraYieldMs = GetAdaptiveThermalYieldMs();
        return JsonSerializer.Serialize(new {
            ok = true,
            timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            apiLevel = (int)Build.VERSION.SdkInt,
            processorCount = System.Environment.ProcessorCount,
            computeQoS = new {
                headroomWorkerCap = MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(System.Environment.ProcessorCount),
                onnxAllowSpinning = false,
                thermalStatusCode = thermalCode,
                thermalStatus = DescribeThermalStatus(thermalCode),
                adaptiveThermalYieldMs = extraYieldMs
            },
            frameMetrics = BuildFrameMetricsObject(),
            audioXRuns = BuildAudioXRunObject(audioOutput),
            exitReasonsSupported = OperatingSystem.IsAndroidVersionAtLeast(30),
            exitReasons = ReadHistoricalExitReasons(ctx, 5)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    static double Percentile(double[] sorted, double p) {
        if (sorted == null || sorted.Length == 0) {
            return 0.0;
        }
        int idx = (int)Math.Floor((sorted.Length - 1) * Math.Clamp(p, 0.0, 1.0));
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

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

    [System.Runtime.Versioning.SupportedOSPlatform("android24.0")]
    sealed class FrameMetricsListenerImpl : Java.Lang.Object, Window.IOnFrameMetricsAvailableListener {
        public void OnFrameMetricsAvailable(Window window, FrameMetrics frameMetrics, int dropCountSinceLastInvocation) {
            if (frameMetrics == null) {
                return;
            }
            long totalNs = frameMetrics.GetMetric((int)FrameMetricsId.TotalDuration);
            if (totalNs > 0) {
                RecordFrameDurationMs(totalNs / 1_000_000.0);
            }
        }
    }
}
