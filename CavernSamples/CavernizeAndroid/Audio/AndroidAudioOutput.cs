using Android.Content;
using Android.Media;
using Android.OS;

using Cavern.ProAudio;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Audio;

/// <summary>Lifecycle states for the single-coordinator <see cref="AudioRouteFsm"/>.</summary>
public enum AudioRouteFsmState {
    Idle = 0,
    Debouncing = 1,
    Starting = 2,
    Streaming = 3,
    Draining = 4,
    HalClosing = 5,
    Fallback = 6
}

/// <summary>
/// Single-coordinator finite state machine with monotonic <c>long routeEpoch</c> CAS
/// cancelling stale 300 ms route-debounce timers and guarding idempotent startup/detach transitions.
/// </summary>
public sealed class AudioRouteFsm : IDisposable {
    long routeEpoch;
    int stateInt = (int)AudioRouteFsmState.Idle;
    CancellationTokenSource debounceCts;
    readonly object timerLock = new();

    /// <summary>Monotonically increasing route epoch incremented on every route/USB/config mutation.</summary>
    public long RouteEpoch => Interlocked.Read(ref routeEpoch);

    /// <summary>Current FSM state.</summary>
    public AudioRouteFsmState State => (AudioRouteFsmState)Volatile.Read(ref stateInt);

    /// <summary>Advances <c>routeEpoch</c>, cancels any pending debounce timer, and moves atomically to <paramref name="nextState"/>.</summary>
    public long AdvanceEpoch(AudioRouteFsmState nextState) {
        CancelDebounceTimer();
        long epoch = Interlocked.Increment(ref routeEpoch);
        Interlocked.Exchange(ref stateInt, (int)nextState);
        return epoch;
    }

    /// <summary>Performs a lock-free CAS transition only when <c>routeEpoch</c> matches <paramref name="expectedEpoch"/>.</summary>
    public bool TryTransition(long expectedEpoch, AudioRouteFsmState expectedState, AudioRouteFsmState nextState) {
        if (Interlocked.Read(ref routeEpoch) != expectedEpoch) {
            return false;
        }
        return Interlocked.CompareExchange(ref stateInt, (int)nextState, (int)expectedState) == (int)expectedState &&
               Interlocked.Read(ref routeEpoch) == expectedEpoch;
    }

    /// <summary>Idempotent startup guard preventing concurrent duplicate stream starts on the same epoch.</summary>
    public bool TryBeginStartup(out long epoch) {
        epoch = Interlocked.Read(ref routeEpoch);
        int cur = Volatile.Read(ref stateInt);
        if (cur == (int)AudioRouteFsmState.Starting || cur == (int)AudioRouteFsmState.Streaming) {
            return false;
        }
        return Interlocked.CompareExchange(ref stateInt, (int)AudioRouteFsmState.Starting, cur) == cur;
    }

    /// <summary>Cancels any stale 300 ms debounce timer and schedules <paramref name="onCommit"/> for the new epoch.</summary>
    public long ScheduleDebouncedRoute(int delayMs, Action<long> onCommit) {
        CancellationTokenSource nextCts = new();
        CancellationTokenSource prev;
        lock (timerLock) {
            prev = debounceCts;
            debounceCts = nextCts;
        }
        try {
            prev?.Cancel();
            prev?.Dispose();
        } catch {
            // Ignore
        }

        long epoch = Interlocked.Increment(ref routeEpoch);
        Interlocked.Exchange(ref stateInt, (int)AudioRouteFsmState.Debouncing);
        int clampedDelay = Math.Clamp(delayMs, 0, 2000);
        CancellationToken token = nextCts.Token;
        _ = Task.Run(async () => {
            try {
                if (clampedDelay > 0) {
                    await Task.Delay(clampedDelay, token).ConfigureAwait(false);
                }
                if (!token.IsCancellationRequested && Interlocked.Read(ref routeEpoch) == epoch) {
                    onCommit?.Invoke(epoch);
                }
            } catch (System.OperationCanceledException) {
                // Cancelled by newer route epoch
            }
        });
        return epoch;
    }

    /// <summary>Advances epoch immediately on USB detach so stale route timers and pending opens abort.</summary>
    public long NotifyUsbDetached() => AdvanceEpoch(AudioRouteFsmState.Draining);

    void CancelDebounceTimer() {
        CancellationTokenSource prev;
        lock (timerLock) {
            prev = debounceCts;
            debounceCts = null;
        }
        try {
            prev?.Cancel();
            prev?.Dispose();
        } catch {
            // Ignore
        }
    }

    public void Dispose() {
        CancelDebounceTimer();
    }
}

/// <summary>
/// Unified Slot 1 / Slot 4 zero-GC low-latency real-time audio backend streaming <see cref="RealtimePlayerEngine"/>
/// with a three-tier hardware cascade:
/// <list type="number">
/// <item><b>Path B (<c>libusb-1.0</c> Direct USB UAC2)</b>: Unrooted <c>UsbManager</c> FD full-duplex isochronous driver bypassing Android AudioFlinger (64/128/256f, 24/32-bit, async 16.16/10.14 feedback).</item>
/// <item><b>Path A (<c>AAudio / Oboe</c> Exclusive Full-Duplex)</b>: Native Android NDK <c>LOW_LATENCY</c> + <c>EXCLUSIVE</c> MMAP output-driven callback with live microphone capture.</item>
/// <item><b>Legacy <c>AudioTrack</c></b>: Zero-GC 32-bit Float / PCM16 low-latency fallback for universal compatibility.</item>
/// </list>
/// </summary>
public sealed partial class AndroidAudioOutput : IDisposable {
    const int BalancedFramesPerWrite = 1024;    // 21.3 ms at 48 kHz (4 x 256)
    const int LowLatencyFramesPerWrite = 256;   //  5.3 ms at 48 kHz (1 x 256)
    const int MaxPreallocatedFrames = 1024;

    static readonly int[] StandardHighResRates = [44100, 48000, 88200, 96000, 176400, 192000];

    readonly RealtimePlayerEngine engine;
    readonly object lifecycleLock = new();
    readonly AudioRouteFsm routeFsm = new();
    readonly SpscAudioRingHandoff spscRingHandoff = new();

    // Pre-allocated zero-GC audio thread buffers (never reallocated inside AudioLoop)
    readonly float[] floatRenderBuffer = new float[MaxPreallocatedFrames * 2];
    readonly float[] spscPlaybackBuffer = new float[MaxPreallocatedFrames * 2];
    readonly float[] captureInterleavedBuffer = new float[MaxPreallocatedFrames * 2];
    readonly short[] pcm16FallbackBuffer = new short[MaxPreallocatedFrames * 2];
    readonly AudioTimestamp audioTimestamp = new();

    AudioManager audioDeviceCallbackManager;
    AudioDeviceCallback audioDeviceCallback;
    Handler audioCallbackHandler;

    UsbProAudioDeviceManager usbManager;
    ProAudioDuplexConfig proAudioConfig = ProAudioDuplexConfig.Default;

    Thread renderThread;
    volatile bool running;
    volatile bool lowLatencyMode = true;
    volatile bool usbDacNativeSampleRate = true;
    volatile bool trackRebuildRequested;
    volatile bool nativeFallbackLatched;
    volatile bool nativeRetryRequested;
    long routeDebounceUntilMs;

    AudioTrack audioTrack;
    AudioDeviceRouteInfo lastRouteInfo;
    AudioDeviceInfo preferredDevice;
    AudioDeviceTransport activeTransport = AudioDeviceTransport.Unknown;
    string preferredDeviceName = string.Empty;
    int preferredOutputDeviceId;
    int preferredInputDeviceId;

    ActiveProAudioBackendKind activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
    string lastProAudioStatusNote = string.Empty;
    volatile string resolvedInputSourceName = string.Empty;
    int activeSampleRate;
    int activeRequestedSampleRate;
    bool activeIsFloat32 = true;
    bool activeIsLowLatency;
    int activeFramesPerWrite = LowLatencyFramesPerWrite;
    int activeHalBurstFrames = 128;
    int adaptiveWatermarkBoostFrames;
    int lastObservedNativeXRuns;
    long lastDiagPublishMs;
    long idleSinceMs;
    long lastNativeStartTickMs;
    int nativeStallTicks;
    int consecutiveRapidNativeDisconnects;
    volatile bool suppressAAudioDuplexOnce;
    double liveOutputLatencySeconds;
    double cachedNativeHardwareLatencySec;
    double cachedHalPeriodSec;
    long audioTrackFramesWritten;
    int flushEpoch;
    int usbDetachEpoch;
    volatile bool legacyTrackFlushRequested;
    bool lastHeadphoneVirtualizer;

    // W48: Zero-GC fallback AudioRecord capture for Studio vocal recording when Path A/B duplex is inactive
    readonly short[] fallbackMicPcm16 = new short[1024];
    readonly float[] fallbackMicFloat = new float[1024];
    AudioRecord fallbackMicRecord;
    Thread fallbackMicThread;
    volatile bool fallbackMicRunning;
    bool fallbackMicOwnsStreamingFlag;

    /// <summary>
    /// Enables <see cref="AudioTrackPerformanceMode.LowLatency"/> and 256-frame bursts (~5.3 ms @ 48 kHz).
    /// </summary>
    public bool LowLatencyMode {
        get => lowLatencyMode;
        set {
            if (lowLatencyMode != value) {
                lowLatencyMode = value;
                RequestRebuild(retryNative: true);
            }
        }
    }

    /// <summary>
    /// Enables USB DAC preferred routing and native high-resolution sample-rate negotiation (44.1..192 kHz).
    /// </summary>
    public bool UsbDacNativeSampleRate {
        get => usbDacNativeSampleRate;
        set {
            if (usbDacNativeSampleRate != value) {
                usbDacNativeSampleRate = value;
                engine.UsbDacNativeSampleRateEnabled = value;
                RequestRebuild(retryNative: true);
            }
        }
    }

    /// <summary>
    /// Active Slot 1 / Slot 4 Pro-Audio Full-Duplex configuration.
    /// </summary>
    public ProAudioDuplexConfig ProAudioConfig {
        get {
            lock (lifecycleLock) {
                return proAudioConfig;
            }
        }
    }

    /// <summary>
    /// Latest live output diagnostics snapshot (sample rate, Float32, LowLatency, USB DAC, estimated latency, underruns).
    /// </summary>
    public AudioOutputDiagnostics Diagnostics { get; private set; } = AudioOutputDiagnostics.Default;

    /// <summary>
    /// Latest live Slot 1 / Slot 4 Pro-Audio Full-Duplex diagnostics snapshot (Path B / Path A / Legacy).
    /// </summary>
    public ProAudioDuplexDiagnostics ProAudioDiagnostics { get; private set; } = ProAudioDuplexDiagnostics.Default;

    /// <summary>
    /// Raised whenever output track format, sample rate, or latency mode changes (background thread safe).
    /// </summary>
    public event Action<AudioOutputDiagnostics> DiagnosticsUpdated;

    /// <summary>
    /// Raised whenever Pro-Audio Full-Duplex backend diagnostics update (background thread safe).
    /// </summary>
    public event Action<ProAudioDuplexDiagnostics> ProAudioDiagnosticsUpdated;

    /// <summary>
    /// Live estimated downstream output pipeline latency in seconds (ring buffer + hardware/HAL/Bluetooth delay).
    /// </summary>
    public double LiveOutputLatencySeconds => Volatile.Read(ref liveOutputLatencySeconds);

    /// <summary>Single-coordinator route FSM with monotonic <c>routeEpoch</c> CAS.</summary>
    public AudioRouteFsm RouteFsm => routeFsm;

    public AndroidAudioOutput(RealtimePlayerEngine engine) {
        this.engine = engine;
        if (this.engine != null) {
            lastHeadphoneVirtualizer = this.engine.HeadphoneVirtualizer;
            this.engine.RegisterOutputFlushCallback(FlushOutputBuffer);
        }
    }

    /// <summary>
    /// Flushes queued output buffers immediately (SPSC-safe for native Path A/B and thread-safe for AudioTrack)
    /// and clears stale output latency state across seeks and track changes.
    /// </summary>
    public void FlushOutputBuffer() {
        Interlocked.Increment(ref flushEpoch);
        spscRingHandoff.Reset();
        if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
            NativeProAudioBridge.FlushPlayback();
        } else {
            legacyTrackFlushRequested = true;
        }
        UpdateLiveOutputLatencySeconds(0.0);
    }

    void OnHardwareAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
        if (removedDevices == null || removedDevices.Length == 0) {
            return;
        }
        bool clearedEndpoint = false;
        int outIdAfter = 0;
        int inIdAfter = 0;
        lock (lifecycleLock) {
            for (int i = 0; i < removedDevices.Length; i++) {
                AudioDeviceInfo dev = removedDevices[i];
                if (dev == null) {
                    continue;
                }
                bool isUsb = dev.Type == AudioDeviceType.UsbDevice || dev.Type == AudioDeviceType.UsbHeadset;
                if (preferredInputDeviceId != 0 &&
                    (dev.Id == preferredInputDeviceId || (isUsb && proAudioConfig.MicSource != ProAudioMicSource.PhoneMic))) {
                    preferredInputDeviceId = 0;
                    clearedEndpoint = true;
                }
                if (preferredOutputDeviceId != 0 &&
                    (dev.Id == preferredOutputDeviceId || (isUsb && activeTransport == AudioDeviceTransport.UsbDac))) {
                    preferredOutputDeviceId = 0;
                    preferredDevice = null;
                    clearedEndpoint = true;
                }
                if (lastRouteInfo != null && (lastRouteInfo.InputDeviceId == dev.Id || lastRouteInfo.OutputDeviceId == dev.Id)) {
                    lastRouteInfo = null;
                    clearedEndpoint = true;
                }
            }
            outIdAfter = preferredOutputDeviceId;
            inIdAfter = preferredInputDeviceId;
            if (clearedEndpoint) {
                Volatile.Write(ref routeDebounceUntilMs, System.Environment.TickCount64 + 140);
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
        }
        if (clearedEndpoint) {
            AppLogger.Info($"AndroidAudioOutput: Cleared removed audio device endpoint IDs (OutId={outIdAfter}, InId={inIdAfter}) on OnAudioDevicesRemoved (140 ms debounce).");
            PublishProAudioDiagnostics();
        }
    }

    void OnBeforeUsbConnectionClosing() {
        // Must stop Path B libusb synchronously BEFORE Android closes the UsbDeviceConnection FD
        // to prevent fdsan SIGABRT / libusb UAF when unplugging USB DACs (e.g., Focusrite Scarlett).
        // Break lifecycleLock <-> syncLock AB-BA deadlock: NEVER call usbManager while holding lifecycleLock,
        // and always stop the native backend safely even if the stream is transitioning or winding down.
        Interlocked.Increment(ref usbDetachEpoch);
        routeFsm.NotifyUsbDetached();
        lock (lifecycleLock) {
            AppLogger.Info("AndroidAudioOutput: USB interface detaching — stopping native backend before FD close.");
            if (proAudioConfig.MicSource != ProAudioMicSource.PhoneMic &&
                lastRouteInfo?.NativeInputDevice?.Type != AudioDeviceType.BuiltinMic) {
                preferredInputDeviceId = 0;
            }
            if (activeTransport == AudioDeviceTransport.UsbDac) {
                preferredOutputDeviceId = 0;
                preferredDevice = null;
            }
            Volatile.Write(ref routeDebounceUntilMs, System.Environment.TickCount64 + 140);
            StopNativeBackendLocked(forceStopNative: true);
            RequestRebuild(retryNative: true);
        }
    }

    void OnUsbDeviceStateChanged(UsbProAudioDeviceSnapshot snap) {
        lock (lifecycleLock) {
            if (snap == null || !snap.IsAttached) {
                if (proAudioConfig.MicSource != ProAudioMicSource.PhoneMic &&
                    lastRouteInfo?.NativeInputDevice?.Type != AudioDeviceType.BuiltinMic) {
                    preferredInputDeviceId = 0;
                }
                if (activeTransport == AudioDeviceTransport.UsbDac) {
                    preferredOutputDeviceId = 0;
                    preferredDevice = null;
                }
                Volatile.Write(ref routeDebounceUntilMs, System.Environment.TickCount64 + 140);
                routeFsm.NotifyUsbDetached();
                RequestRebuild(retryNative: true);
            } else if (proAudioConfig.Enabled) {
                Volatile.Write(ref routeDebounceUntilMs, System.Environment.TickCount64 + 140);
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
        }
        PushEffectivePreAmpSnapshot();
        PublishProAudioDiagnostics(usbSnapOverride: snap);
    }

    /// <summary>
    /// Starts the real-time audio output thread if not already running (idempotent startup guard).
    /// </summary>
    public void Start() {
        EnsureAudioDeviceCallbackRegistered();
        lock (lifecycleLock) {
            if (running && renderThread != null && renderThread.IsAlive) {
                return;
            }
            routeFsm.TryBeginStartup(out _);
            nativeFallbackLatched = false;
            nativeRetryRequested = true;
            nativeStallTicks = 0;
            Diagnostics = Diagnostics with { UnderrunCount = 0 };
            running = true;
            renderThread = new Thread(AudioLoop) {
                Name = "CavernizeAudioOut",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest
            };
            renderThread.Start();
        }
    }

    /// <summary>
    /// Pauses playback hardware cleanly (unless standalone live mic monitoring or MLS RTT calibration is active).
    /// </summary>
    public void Pause() {
        UsbProAudioDeviceManager mgr = null;
        bool wasPathB = false;
        lock (lifecycleLock) {
            if (engine.IsLiveMonitoringOrCalibrating) {
                return;
            }
            if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
                wasPathB = StopNativeBackendLocked();
                mgr = usbManager;
            } else {
                try {
                    audioTrack?.Pause();
                } catch {
                    // Ignore if uninitialized
                }
                return;
            }
        }
        if (wasPathB) {
            mgr?.NotifyPathBFileDescriptorState(ownedByPathB: false);
        }
    }

    /// <summary>
    /// Stops and tears down the audio output thread, native Path B/A streams, and <see cref="AudioTrack"/>.
    /// </summary>
    public void Stop() {
        StopFallbackMicCapture();
        Thread threadToJoin;
        lock (lifecycleLock) {
            running = false;
            routeFsm.AdvanceEpoch(AudioRouteFsmState.Idle);
            threadToJoin = renderThread;
            renderThread = null;
        }

        if (threadToJoin != null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread) {
            threadToJoin.Join(1000);
        }

        bool wasPathB;
        UsbProAudioDeviceManager mgr;
        lock (lifecycleLock) {
            wasPathB = StopNativeBackendLocked();
            mgr = usbManager;
            ReleaseTrack();
            Diagnostics = Diagnostics with { UnderrunCount = 0 };
        }
        if (wasPathB) {
            mgr?.NotifyPathBFileDescriptorState(ownedByPathB: false);
        }
    }

    void AudioLoop() {
        try {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
        } catch {
            // Best-effort real-time priority elevation
        }

        while (running) {
            bool needAudioActive = engine.IsPlaying || engine.IsLiveMonitoringOrCalibrating;
            if (!needAudioActive) {
                if (legacyTrackFlushRequested && activeBackend == ActiveProAudioBackendKind.LegacyAudioTrack) {
                    ExecuteLegacyTrackFlush();
                }
                long nowMs = System.Environment.TickCount64;
                if (idleSinceMs == 0) {
                    idleSinceMs = nowMs;
                }
                if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
                    // Grace period prevents tearing down/reopening AAudio across brief seek/track-switch pauses
                    if (nowMs - idleSinceMs >= 1500) {
                        bool wasPathB;
                        UsbProAudioDeviceManager mgr;
                        lock (lifecycleLock) {
                            wasPathB = StopNativeBackendLocked();
                            mgr = usbManager;
                        }
                        if (wasPathB) {
                            mgr?.NotifyPathBFileDescriptorState(ownedByPathB: false);
                        }
                        PublishProAudioDiagnostics();
                    }
                } else if (audioTrack != null && audioTrack.PlayState == PlayState.Playing) {
                    try {
                        audioTrack.Pause();
                    } catch {
                        // Ignore
                    }
                }
                Thread.Sleep(12);
                continue;
            }
            idleSinceMs = 0;

            bool curVirt = engine.HeadphoneVirtualizer;
            if (curVirt != lastHeadphoneVirtualizer) {
                lastHeadphoneVirtualizer = curVirt;
                SyncPreferredHardwareRateForCurrentMode();
            }

            int targetRate = ResolveTargetSampleRate();
            bool shouldUseNative = ShouldAttemptNativeProAudio();

            if (trackRebuildRequested ||
                activeRequestedSampleRate != targetRate ||
                (shouldUseNative && activeBackend == ActiveProAudioBackendKind.LegacyAudioTrack) ||
                (!shouldUseNative && activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) ||
                (activeBackend == ActiveProAudioBackendKind.LegacyAudioTrack && audioTrack == null)) {
                long debounceUntil = Volatile.Read(ref routeDebounceUntilMs);
                if (debounceUntil > 0 && System.Environment.TickCount64 < debounceUntil) {
                    Thread.Sleep(12);
                    continue;
                }
                Volatile.Write(ref routeDebounceUntilMs, 0);
                trackRebuildRequested = false;
                if (nativeRetryRequested) {
                    nativeRetryRequested = false;
                    nativeFallbackLatched = false;
                }
                ReconfigureBackend(targetRate);
            }

            if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
                PumpNativeFullDuplexIteration();
                continue;
            }

            // Legacy AudioTrack path (zero-GC, output-only) with startup pre-fill to prevent initial underrun burst
            if (legacyTrackFlushRequested) {
                ExecuteLegacyTrackFlush();
            }
            bool needStartPlay = audioTrack != null && audioTrack.PlayState != PlayState.Playing;
            int epochBeforeWrite = Volatile.Read(ref flushEpoch);
            int framesToRequest = activeFramesPerWrite;
            if (needStartPlay && audioTrack != null) {
                // Pre-fill 2 blocks into stopped AudioTrack before Play() so USB/HAL never starts dry
                for (int pre = 0; pre < 2 && running; pre++) {
                    int preFrames = engine.RenderNextInterleavedBlock(floatRenderBuffer, framesToRequest);
                    if (preFrames <= 0 || Volatile.Read(ref flushEpoch) != epochBeforeWrite) {
                        break;
                    }
                    int preWritten = WriteBlockZeroGc(preFrames);
                    if (preWritten > 0) {
                        audioTrackFramesWritten += preWritten / 2;
                    }
                }
                try {
                    audioTrack.Play();
                } catch {
                    Thread.Sleep(15);
                    continue;
                }
                continue;
            }
            int frames = engine.RenderNextInterleavedBlock(floatRenderBuffer, framesToRequest);
            if (frames > 0 && audioTrack != null && running) {
                if (Volatile.Read(ref flushEpoch) != epochBeforeWrite) {
                    continue;
                }
                int written = WriteBlockZeroGc(frames);
                if (written < 0) {
                    // Recover cleanly when Android invalidates AudioTrack (e.g. Bluetooth/USB route change)
                    ReconfigureBackend(targetRate);
                    try {
                        audioTrack?.Play();
                        int retryWritten = WriteBlockZeroGc(frames);
                        if (retryWritten > 0) {
                            audioTrackFramesWritten += retryWritten / 2;
                            UpdateLegacyTrackLiveLatency(audioTrack);
                        }
                    } catch {
                        Thread.Sleep(20);
                    }
                } else {
                    audioTrackFramesWritten += written / 2;
                    UpdateLegacyTrackLiveLatency(audioTrack);
                    long nowMs = System.Environment.TickCount64;
                    if (nowMs - lastDiagPublishMs >= 350) {
                        lastDiagPublishMs = nowMs;
                        PublishDiagnostics();
                    }
                }
            } else {
                Thread.Sleep(8);
            }
        }
    }

    int ResolveTargetSampleRate() {
        // When headphone HRTF virtualization is active, VirtualizerFilter renders strictly at FilterSampleRate (48 kHz).
        // Never clock the hardware stream at a mismatched LockedSampleRateHz (prevents 2x pitch / chipmunk distortion).
        if (engine.HeadphoneVirtualizer) {
            return Math.Clamp(engine.OutputSampleRate, 44100, 192000);
        }
        int locked = proAudioConfig.LockedSampleRateHz;
        if (locked >= 44100 && locked <= 192000) {
            return locked;
        }
        return Math.Clamp(engine.OutputSampleRate, 44100, 192000);
    }

    bool ShouldAttemptNativeProAudio() {
        ProAudioDuplexConfig cfg = proAudioConfig;
        return cfg.Enabled &&
               cfg.PreferredPath != ProAudioEnginePath.LegacyAudioTrack &&
               NativeProAudioBridge.IsAvailable &&
               !nativeFallbackLatched;
    }

    void PumpNativeFullDuplexIteration() {
        // 1. Drain captured live microphone frames from native lock-free IN ring -> Slot 1 LiveMicrophoneBridge
        int captured = NativeProAudioBridge.ReadCapture(captureInterleavedBuffer, MaxPreallocatedFrames);
        if (captured > 0) {
            engine.LiveMicBridge.PushCapturedInterleaved(captureInterleavedBuffer, 0, captured, 2);
        }

        // 2. SR-scaled & HAL-burst-aware dual watermark:
        //    - Live Mic Duplex: tight bounded 8..22 ms (tracks HAL burst + SR cleanly)
        //    - Music Playback: generous 36..68 ms headroom so 96/192 kHz Atmos (384f HAL burst) NEVER starves under thermal load
        int sr = Math.Max(44100, activeSampleRate);
        int burst = Math.Clamp(activeFramesPerWrite, 64, MaxPreallocatedFrames);
        int halBurst = Math.Clamp(activeHalBurstFrames, 64, MaxPreallocatedFrames);
        bool duplexOrLiveMic = (proAudioConfig.Enabled &&
                                proAudioConfig.LiveMicEnabled &&
                                proAudioConfig.MicRouting != LiveMicRoutingMode.Off) ||
                               engine.LiveMicBridge.IsActive ||
                               engine.LiveMicBridge.IsCalibratingLatency ||
                               engine.LiveMicBridge.IsRecordingPerformance;

        int maxBuffered;
        int writeChunk;
        if (duplexOrLiveMic) {
            int baseDuplex = Math.Max(Math.Max(burst, halBurst) * 2, (int)(sr * 0.008));
            int capDuplex = Math.Max(baseDuplex, (int)(sr * 0.022));
            maxBuffered = Math.Clamp(baseDuplex + adaptiveWatermarkBoostFrames, 128, capDuplex);
            writeChunk = burst;
            if (engine.LiveMicBridge.BufferedFrames > maxBuffered) {
                engine.LiveMicBridge.TrimLatencyBacklog(maxBuffered);
            }
        } else {
            int baseMusic = Math.Max(Math.Max(burst, halBurst) * 4, (int)(sr * 0.036));
            // W51.5: Keep generous >= 52 ms ring floor during first 3.5 s of cold playback start to absorb decoder/JIT/UI bursts
            bool inStartupWarmup = (System.Environment.TickCount64 - lastNativeStartTickMs) < 3500;
            if (inStartupWarmup) {
                baseMusic = Math.Max(baseMusic, (int)(sr * 0.052));
            }
            int capMusic = Math.Min(12288, Math.Max(baseMusic, (int)(sr * 0.068)));
            maxBuffered = Math.Clamp(baseMusic + adaptiveWatermarkBoostFrames, 512, capMusic);
            // Align playback rendering to full Cavern DSP blocks (256..512f) to avoid sub-block lock/copy thrash
            writeChunk = Math.Clamp(Math.Max(256, Math.Min(512, halBurst)), 256, MaxPreallocatedFrames);
        }

        int bufferedFrames = NativeProAudioBridge.GetPlaybackBufferedFrames();
        int freeFrames = NativeProAudioBridge.GetPlaybackFreeFrames();
        bool wroteAny = false;
        int maxChunksPerIter = duplexOrLiveMic ? 2 : 8;

        // 3. Multi-chunk drain-to-watermark loop (refills ring smoothly without sleeping between bursts!)
        for (int chunkIdx = 0; chunkIdx < maxChunksPerIter && running; chunkIdx++) {
            if (bufferedFrames >= maxBuffered || freeFrames < writeChunk) {
                break;
            }
            int epochBefore = Volatile.Read(ref flushEpoch);
            if (spscRingHandoff.AvailableReadFrames < writeChunk) {
                int rendered = engine.RenderNextInterleavedBlock(floatRenderBuffer, writeChunk);
                if (rendered > 0) {
                    if (Volatile.Read(ref flushEpoch) != epochBefore) {
                        spscRingHandoff.Reset();
                        return;
                    }
                    spscRingHandoff.Write(floatRenderBuffer, 0, rendered);
                } else {
                    spscRingHandoff.MarkAsyncGap();
                }
            }

            int framesToWrite = spscRingHandoff.Read(spscPlaybackBuffer, Math.Min(writeChunk, freeFrames));
            if (framesToWrite <= 0) {
                break;
            }
            int written = NativeProAudioBridge.WritePlayback(spscPlaybackBuffer, framesToWrite);
            if (written < 0) {
                // Intentional stop or track rebuild already in progress -> never emit bogus disconnect warning/double-rebuild
                if (trackRebuildRequested || !running) {
                    return;
                }
                // Explicit native backend disconnect/stop (< 0) -> trigger recovery
                nativeStallTicks = 0;
                HandleNativeStreamDisconnect($"WritePlayback={written}");
                return;
            }
            if (written == 0) {
                // Transient EAGAIN / ring full (never treat 0 as immediate disconnect; check stall ticks)
                nativeStallTicks++;
                if (nativeStallTicks >= 24 && NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st) &&
                    st.ActiveBackend == NativeProAudioBridge.BackendNone) {
                    nativeStallTicks = 0;
                    HandleNativeStreamDisconnect("ActiveBackend=0 on WritePlayback=0");
                    return;
                }
                Thread.Sleep(1);
                return;
            }
            wroteAny = true;
            bufferedFrames = NativeProAudioBridge.GetPlaybackBufferedFrames();
            freeFrames = NativeProAudioBridge.GetPlaybackFreeFrames();
        }

        if (wroteAny) {
            if (!duplexOrLiveMic && bufferedFrames < halBurst) {
                // Near-starvation detected proactively -> bump adaptive headroom immediately
                int step = Math.Max(halBurst, sr / 100);
                adaptiveWatermarkBoostFrames = Math.Min(adaptiveWatermarkBoostFrames + step, (int)(sr * 0.032));
            }
            UpdateLiveOutputLatencySeconds((bufferedFrames / (double)sr) + Volatile.Read(ref cachedNativeHardwareLatencySec));
            nativeStallTicks = 0;
            long nowMs = System.Environment.TickCount64;
            if (consecutiveRapidNativeDisconnects > 0 && nowMs - lastNativeStartTickMs >= 2000) {
                consecutiveRapidNativeDisconnects = 0;
            }
            if (nowMs - lastDiagPublishMs >= 350) {
                lastDiagPublishMs = nowMs;
                PublishDiagnostics();
            }
            if (bufferedFrames >= maxBuffered) {
                Thread.Sleep(1);
            }
        } else if (bufferedFrames < maxBuffered) {
            // Engine paused/buffering or no frames available yet
            Thread.Sleep(2);
        } else {
            UpdateLiveOutputLatencySeconds((bufferedFrames / (double)sr) + Volatile.Read(ref cachedNativeHardwareLatencySec));
            // Detect AAudio AAUDIO_ERROR_DISCONNECTED / libusb halt (ActiveBackend == 0) while OUT ring stops draining
            nativeStallTicks++;
            if (nativeStallTicks >= 12) {
                nativeStallTicks = 0;
                if (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st)) {
                    if (st.ActiveBackend == NativeProAudioBridge.BackendNone) {
                        HandleNativeStreamDisconnect("ActiveBackend=0");
                        return;
                    }
                    if (st.BufferFrames >= 64) {
                        activeHalBurstFrames = st.BufferFrames;
                    }
                    Volatile.Write(ref cachedNativeHardwareLatencySec, ComputeNativeHardwareLatencySeconds(st, st.SampleRate >= 44100 ? st.SampleRate : sr));
                }
            }
            // Sub-millisecond yield while native callback drains OUT ring / respects watermark
            Thread.Sleep(1);
        }
    }

    void ReconfigureBackend(int sampleRate) {
        // Phase 1: Stop active stream and snapshot state under lifecycleLock (ZERO calls to usbManager inside lock!)
        bool prevWasPathB;
        UsbProAudioDeviceManager mgr;
        ProAudioDuplexConfig cfg;
        AudioDeviceRouteInfo routeSnap;
        int curOutId;
        int curInId;
        bool suppressDuplex;
        bool fallbackLatched;
        int resolvedRate;
        long entryEpoch = routeFsm.AdvanceEpoch(AudioRouteFsmState.Starting);
        spscRingHandoff.Reset();

        lock (lifecycleLock) {
            prevWasPathB = StopNativeBackendLocked();
            ReleaseTrack();
            mgr = usbManager;
            cfg = proAudioConfig;
            routeSnap = lastRouteInfo;
            curOutId = preferredOutputDeviceId;
            curInId = preferredInputDeviceId;
            suppressDuplex = suppressAAudioDuplexOnce;
            fallbackLatched = nativeFallbackLatched;
            resolvedRate = ResolveClosestSupportedRate(sampleRate);
            activeRequestedSampleRate = sampleRate;
            nativeStallTicks = 0;
        }

        // Phase 2: Snapshot USB state & acquire Path B FD outside lifecycleLock (prevents AB-BA deadlock!)
        if (prevWasPathB) {
            mgr?.NotifyPathBFileDescriptorState(ownedByPathB: false);
        }

        int detachEpoch = Volatile.Read(ref usbDetachEpoch);
        UsbProAudioDeviceSnapshot usbSnap = mgr?.CurrentSnapshot;
        int burst = Math.Clamp((int)cfg.BufferFrames, 64, 256);
        bool needInput = (cfg.LiveMicEnabled && cfg.MicRouting != LiveMicRoutingMode.Off) ||
                         engine.LiveMicBridge.IsCalibratingLatency ||
                         engine.LiveMicBridge.IsRecordingPerformance;
        bool micBlocksPathB = needInput && cfg.MicSource == ProAudioMicSource.PhoneMic;
        bool usbAttachedAndPermitted = usbSnap != null && usbSnap.IsAttached && usbSnap.HasPermission && usbSnap.IsUac2;
        bool usbSupportsDuplex = usbSnap != null && usbSnap.SupportsFullDuplex;

        (int effectiveOutId, int effectiveInId, string resolvedInputLabel) = ResolveNativeEndpointIdsOutsideLock(
            cfg,
            routeSnap,
            curOutId,
            curInId,
            needInput,
            usbSnap != null && usbSnap.IsAttached);

        bool canAttemptNative = cfg.Enabled &&
                                cfg.PreferredPath != ProAudioEnginePath.LegacyAudioTrack &&
                                !fallbackLatched &&
                                NativeProAudioBridge.IsAvailable;
        bool pathBCapable = canAttemptNative &&
                            NativeProAudioBridge.SupportsPathBLibUsb &&
                            !micBlocksPathB &&
                            (!needInput || usbSupportsDuplex);
        bool wantPathB = pathBCapable &&
                         (cfg.PreferredPath == ProAudioEnginePath.Auto ||
                          cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2);

        int acquiredUsbFd = -1;
        if (pathBCapable && usbAttachedAndPermitted && mgr != null) {
            acquiredUsbFd = mgr.AcquireOpenFileDescriptor();
        }

        // Abort stale route if a newer routeEpoch / USB detach arrived during Phase 2
        if (routeFsm.RouteEpoch != entryEpoch || Volatile.Read(ref usbDetachEpoch) != detachEpoch) {
            trackRebuildRequested = true;
            return;
        }

        // Phase 3: Start chosen backend under lifecycleLock (never calling usbManager inside lock!)
        bool startedPathB = false;
        bool startedNative = false;
        string statusNote = string.Empty;

        if (engine.HeadphoneVirtualizer && cfg.LockedSampleRateHz > 0 && cfg.LockedSampleRateHz != resolvedRate) {
            statusNote = $"HRIR virtualization locks output to {resolvedRate} Hz (ignoring {cfg.LockedSampleRateHz} Hz lock).";
        } else if (micBlocksPathB && usbSnap != null && usbSnap.IsAttached && cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2) {
            statusNote = "Phone Mic active — routing asymmetric duplex via Path A AAudio (Out=USB, In=Phone Mic).";
        }

        lock (lifecycleLock) {
            if (!running) {
                return;
            }
            if (routeFsm.RouteEpoch != entryEpoch || Volatile.Read(ref usbDetachEpoch) != detachEpoch) {
                trackRebuildRequested = true;
                return;
            }
            lastProAudioStatusNote = statusNote;
            if (!string.IsNullOrEmpty(resolvedInputLabel)) {
                resolvedInputSourceName = resolvedInputLabel;
            }
            if (effectiveOutId != 0) {
                preferredOutputDeviceId = effectiveOutId;
            }
            if (needInput) {
                preferredInputDeviceId = effectiveInId;
            } else if (cfg.MicSource == ProAudioMicSource.PhoneMic) {
                preferredInputDeviceId = 0;
            }

            if (cfg.Enabled && cfg.PreferredPath != ProAudioEnginePath.LegacyAudioTrack && !fallbackLatched) {
                if (!NativeProAudioBridge.IsAvailable) {
                    lastProAudioStatusNote = "Native libcavernize_proaudio.so not bundled; using AudioTrack fallback.";
                    nativeFallbackLatched = true;
                } else {
                    // Cascade Step 1: Path B (Unrooted libusb-1.0 UAC2 Direct Full-Duplex)
                    if (wantPathB && acquiredUsbFd >= 0 && Volatile.Read(ref usbDetachEpoch) == detachEpoch) {
                        int rc = NativeProAudioBridge.StartLibUsb(acquiredUsbFd, resolvedRate, burst, needInput);
                        if (rc == 0) {
                            activeBackend = ActiveProAudioBackendKind.LibUsbUac2Direct;
                            activeSampleRate = resolvedRate;
                            activeIsFloat32 = true;
                            activeIsLowLatency = true;
                            activeFramesPerWrite = burst;
                            activeHalBurstFrames = burst;
                            adaptiveWatermarkBoostFrames = 0;
                            lastObservedNativeXRuns = 0;
                            NativeProAudioStatus pbSt = default;
                            if (NativeProAudioBridge.TryGetStatus(out pbSt)) {
                                if (pbSt.SampleRate >= 44100) {
                                    activeSampleRate = pbSt.SampleRate;
                                }
                                if (pbSt.BufferFrames >= 64) {
                                    activeHalBurstFrames = pbSt.BufferFrames;
                                }
                            }
                            if (activeSampleRate != resolvedRate) {
                                engine.SetPreferredHardwareSampleRate(activeSampleRate);
                                activeRequestedSampleRate = activeSampleRate;
                            }
                            Volatile.Write(ref cachedNativeHardwareLatencySec, ComputeNativeHardwareLatencySeconds(pbSt, activeSampleRate));
                            UpdateLiveOutputLatencySeconds(Volatile.Read(ref cachedNativeHardwareLatencySec));
                            engine.LiveMicBridge.SetStreamingActive(needInput, activeSampleRate);
                            lastNativeStartTickMs = System.Environment.TickCount64;
                            startedPathB = true;
                            startedNative = true;
                            routeFsm.TryTransition(entryEpoch, AudioRouteFsmState.Starting, AudioRouteFsmState.Streaming);
                            AppLogger.Info($"Pro-Audio Path B (libusb UAC2) active: {activeSampleRate} Hz, {burst}f (HAL={activeHalBurstFrames}f), Duplex={needInput}, Device='{usbSnap?.DisplayName}'");
                        } else {
                            lastProAudioStatusNote = $"Path B libusb start returned {rc}; falling back to Path A AAudio.";
                            AppLogger.Warn(lastProAudioStatusNote);
                        }
                    } else if (cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2 && !micBlocksPathB) {
                        if (usbSnap != null && usbSnap.IsAttached && !usbSnap.HasPermission) {
                            lastProAudioStatusNote = "USB audio interface attached; awaiting Android USB permission for Path B.";
                        } else if (usbSnap == null || !usbSnap.IsAttached) {
                            lastProAudioStatusNote = "No UAC2 USB audio interface attached; falling back.";
                        } else if (needInput && !usbSupportsDuplex) {
                            lastProAudioStatusNote = "USB DAC has no capture interface; routing asymmetric duplex via Path A AAudio.";
                        }
                    }

                    // Cascade Step 2: Path A (Native Android AAudio LOW_LATENCY + EXCLUSIVE Full-Duplex)
                    bool wantPathA = !startedNative &&
                                     (cfg.PreferredPath == ProAudioEnginePath.Auto ||
                                      cfg.PreferredPath == ProAudioEnginePath.PathAAudioExclusive ||
                                      cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2);
                    if (wantPathA && NativeProAudioBridge.SupportsPathAAudio) {
                        bool aaudioDuplex = needInput && !suppressDuplex;
                        int rc = NativeProAudioBridge.StartAAudio(
                            resolvedRate,
                            burst,
                            aaudioDuplex,
                            effectiveOutId,
                            effectiveInId);
                        if (rc == 0) {
                            activeBackend = ActiveProAudioBackendKind.AAudioOboeFullDuplex;
                            activeSampleRate = resolvedRate;
                            activeIsFloat32 = true;
                            activeIsLowLatency = true;
                            activeFramesPerWrite = burst;
                            activeHalBurstFrames = burst;
                            adaptiveWatermarkBoostFrames = 0;
                            lastObservedNativeXRuns = 0;
                            NativeProAudioStatus st = default;
                            if (NativeProAudioBridge.TryGetStatus(out st)) {
                                if (st.SampleRate >= 44100) {
                                    activeSampleRate = st.SampleRate;
                                }
                                if (st.BufferFrames >= 64) {
                                    activeHalBurstFrames = st.BufferFrames;
                                }
                            }
                            if (activeSampleRate != resolvedRate) {
                                // Keep DSP rate matched to AAudio's negotiated hardware rate even in HRIR mode (VirtualizerFilter handles rate conversion)
                                engine.SetPreferredHardwareSampleRate(activeSampleRate);
                                activeRequestedSampleRate = activeSampleRate;
                            }
                            Volatile.Write(ref cachedNativeHardwareLatencySec, ComputeNativeHardwareLatencySeconds(st, activeSampleRate));
                            UpdateLiveOutputLatencySeconds(Volatile.Read(ref cachedNativeHardwareLatencySec));
                            engine.LiveMicBridge.SetStreamingActive(aaudioDuplex, activeSampleRate);
                            lastNativeStartTickMs = System.Environment.TickCount64;
                            startedNative = true;
                            routeFsm.TryTransition(entryEpoch, AudioRouteFsmState.Starting, AudioRouteFsmState.Streaming);
                            AppLogger.Info($"Pro-Audio Path A (AAudio Exclusive) active: {activeSampleRate} Hz (req={resolvedRate} Hz), {burst}f (HAL={activeHalBurstFrames}f), Duplex={aaudioDuplex}, OutId={effectiveOutId}, InId={effectiveInId}, MicSource={cfg.MicSource}");
                        } else if (rc == -11 && !wantPathB && pathBCapable && acquiredUsbFd >= 0 && Volatile.Read(ref usbDetachEpoch) == detachEpoch) {
                            // Clean Path A (-11 / requestStart failure) -> Path B Direct USB fallback
                            int fbRc = NativeProAudioBridge.StartLibUsb(acquiredUsbFd, resolvedRate, burst, needInput);
                            if (fbRc == 0) {
                                activeBackend = ActiveProAudioBackendKind.LibUsbUac2Direct;
                                activeSampleRate = resolvedRate;
                                activeIsFloat32 = true;
                                activeIsLowLatency = true;
                                activeFramesPerWrite = burst;
                                activeHalBurstFrames = burst;
                                adaptiveWatermarkBoostFrames = 0;
                                lastObservedNativeXRuns = 0;
                                NativeProAudioStatus pbSt = default;
                                if (NativeProAudioBridge.TryGetStatus(out pbSt)) {
                                    if (pbSt.SampleRate >= 44100) {
                                        activeSampleRate = pbSt.SampleRate;
                                    }
                                    if (pbSt.BufferFrames >= 64) {
                                        activeHalBurstFrames = pbSt.BufferFrames;
                                    }
                                }
                                Volatile.Write(ref cachedNativeHardwareLatencySec, ComputeNativeHardwareLatencySeconds(pbSt, activeSampleRate));
                                UpdateLiveOutputLatencySeconds(Volatile.Read(ref cachedNativeHardwareLatencySec));
                                engine.LiveMicBridge.SetStreamingActive(needInput, activeSampleRate);
                                lastNativeStartTickMs = System.Environment.TickCount64;
                                startedPathB = true;
                                startedNative = true;
                                routeFsm.TryTransition(entryEpoch, AudioRouteFsmState.Starting, AudioRouteFsmState.Streaming);
                                lastProAudioStatusNote = "Path A AAudio returned -11; switched cleanly to Path B Direct USB UAC2.";
                                AppLogger.Info(lastProAudioStatusNote);
                            } else {
                                lastProAudioStatusNote = $"Path A AAudio start returned {rc} and Path B fallback returned {fbRc}; falling back to AudioTrack.";
                                AppLogger.Warn(lastProAudioStatusNote);
                            }
                        } else {
                            lastProAudioStatusNote = $"Path A AAudio start returned {rc}; falling back to AudioTrack.";
                            AppLogger.Warn(lastProAudioStatusNote);
                        }
                    }
                }
            }

            if (!startedNative) {
                if (canAttemptNative) {
                    // Latch fallback until config/device/USB state changes to prevent infinite ReconfigureBackend thrash loop
                    nativeFallbackLatched = true;
                }
                routeFsm.TryTransition(entryEpoch, AudioRouteFsmState.Starting, AudioRouteFsmState.Fallback);
                activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
                engine.LiveMicBridge.SetStreamingActive(false, resolvedRate);
                BuildLegacyAudioTrackLocked(resolvedRate);
            }
        }

        // Phase 4: Pre-prime native playback ring immediately (before UI diagnostic events) to prevent cold-start stutter!
        if (startedNative && engine.IsPlaying && !needInput && running) {
            PreFillNativePlaybackRing(activeSampleRate);
        }

        // Phase 5: Notify Path B FD ownership & publish diagnostics OUTSIDE lifecycleLock!
        if (startedPathB) {
            if (Volatile.Read(ref usbDetachEpoch) == detachEpoch) {
                mgr?.NotifyPathBFileDescriptorState(ownedByPathB: true);
            } else {
                lock (lifecycleLock) {
                    StopNativeBackendLocked(forceStopNative: true);
                }
                RequestRebuild(retryNative: true);
            }
        }
        PushEffectivePreAmpSnapshot(mgr?.CurrentPreAmpSnapshot);
        PublishDiagnostics(usbSnap);
    }

    /// <summary>
    /// Pre-fills 3..4 DSP blocks (~24..40 ms) directly into the native AAudio/libusb playback ring right after stream open
    /// so the HAL real-time callback never wakes up on a 0-frame ring during the first seconds of playback.
    /// </summary>
    void PreFillNativePlaybackRing(int sr) {
        try {
            int safeSr = Math.Max(44100, sr);
            int targetFrames = Math.Clamp((int)(safeSr * 0.036), 1024, 4096);
            int chunk = 512;
            int epochBefore = Volatile.Read(ref flushEpoch);
            for (int i = 0; i < 6 && running; i++) {
                int buffered = NativeProAudioBridge.GetPlaybackBufferedFrames();
                int free = NativeProAudioBridge.GetPlaybackFreeFrames();
                if (buffered >= targetFrames || free < chunk) {
                    break;
                }
                int rendered = engine.RenderNextInterleavedBlock(floatRenderBuffer, Math.Min(chunk, free));
                if (rendered <= 0 || Volatile.Read(ref flushEpoch) != epochBefore) {
                    break;
                }
                int written = NativeProAudioBridge.WritePlayback(floatRenderBuffer, rendered);
                if (written <= 0) {
                    break;
                }
            }
        } catch {
            // Best-effort startup pre-prime
        }
    }

    bool StopNativeBackendLocked(bool forceStopNative = false) {
        bool wasPathB = activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct;
        if (forceStopNative || activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
            NativeProAudioBridge.Stop();
            activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
            engine.LiveMicBridge.SetStreamingActive(false, Math.Max(44100, activeSampleRate));
            UpdateLiveOutputLatencySeconds(0.0);
        }
        return wasPathB;
    }

    int WriteBlockZeroGc(int frames) {
        int sampleCount = frames * 2;
        if (activeIsFloat32) {
            return audioTrack.Write(floatRenderBuffer, 0, sampleCount, WriteMode.Blocking);
        }
        // Zero-GC float -> PCM16 fallback using pre-allocated buffer
        for (int i = 0; i < sampleCount; i++) {
            float clamped = Math.Clamp(floatRenderBuffer[i], -1f, 1f);
            pcm16FallbackBuffer[i] = (short)MathF.Round(clamped * 32767f);
        }
        return audioTrack.Write(pcm16FallbackBuffer, 0, sampleCount, WriteMode.Blocking);
    }

    void BuildLegacyAudioTrackLocked(int sampleRate) {
        activeSampleRate = ResolveClosestSupportedRate(sampleRate);
        // On USB DAC / Bluetooth Legacy AudioTrack fallback, AudioTrackPerformanceMode.LowLatency forces a fragile
        // ~10 ms FastTrack pipe that causes severe underruns (73/sec observed on S25U+Scarlett); use Balanced mode!
        bool transportAllowsLowLatTrack = activeTransport != AudioDeviceTransport.UsbDac &&
                                          activeTransport != AudioDeviceTransport.BluetoothA2dp &&
                                          activeTransport != AudioDeviceTransport.BleAudio;
        bool wantLowLatency = lowLatencyMode && transportAllowsLowLatTrack;

        // Attempt 1: Float32 + LowLatency (when enabled & safe for transport)
        if (TryBuildTrack(activeSampleRate, useFloat32: true, useLowLatency: wantLowLatency)) {
            FinalizeTrackSetup();
            return;
        }

        // Attempt 2: Float32 + Balanced (some USB/BT HAL drivers reject LowLatency on Float32 or high SR)
        if (wantLowLatency && TryBuildTrack(activeSampleRate, useFloat32: true, useLowLatency: false)) {
            FinalizeTrackSetup();
            return;
        }

        // Attempt 3: PCM16 + LowLatency (legacy HAL fallback)
        if (TryBuildTrack(activeSampleRate, useFloat32: false, useLowLatency: wantLowLatency)) {
            FinalizeTrackSetup();
            return;
        }

        // Attempt 4: Guaranteed safe PCM16 48 kHz Balanced fallback
        activeSampleRate = 48000;
        TryBuildTrack(activeSampleRate, useFloat32: false, useLowLatency: false);
        FinalizeTrackSetup();
    }

    int ResolveClosestSupportedRate(int requestedRate) {
        int clamped = Math.Clamp(requestedRate, 44100, 192000);
        for (int i = 0; i < StandardHighResRates.Length; i++) {
            if (StandardHighResRates[i] == clamped) {
                return clamped;
            }
        }
        return clamped;
    }

    bool TryBuildTrack(int sampleRate, bool useFloat32, bool useLowLatency) {
        try {
            AudioEncoding encoding = useFloat32 ? AudioEncoding.PcmFloat : AudioEncoding.Pcm16bit;
            int bytesPerFrame = 2 * (useFloat32 ? sizeof(float) : sizeof(short));
            int burstFrames = useLowLatency ? LowLatencyFramesPerWrite : BalancedFramesPerWrite;

            AudioAttributes attrs = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)
                .SetContentType(AudioContentType.Music)
                .Build();

            AudioFormat format = new AudioFormat.Builder()
                .SetEncoding(encoding)
                .SetChannelMask(ChannelOut.Stereo)
                .SetSampleRate(sampleRate)
                .Build();

            int minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Stereo, encoding);
            if (minBytes <= 0) {
                return false;
            }

            // Guarantee >= 55 ms on USB DAC / Balanced fallback (>= 28 ms on LowLatency) & >= 2x HAL minimum
            int minDurationFrames = (useLowLatency && activeTransport != AudioDeviceTransport.UsbDac)
                ? (int)(sampleRate * 0.028)
                : (int)(sampleRate * 0.055);
            int desiredFrames = Math.Max(burstFrames * (useLowLatency ? 4 : 6), minDurationFrames);
            int desiredBytes = desiredFrames * bytesPerFrame;
            int bufBytes = Math.Max(minBytes * 2, desiredBytes);

            AudioTrack.Builder builder = new AudioTrack.Builder()
                .SetAudioAttributes(attrs)
                .SetAudioFormat(format)
                .SetTransferMode(AudioTrackMode.Stream)
                .SetBufferSizeInBytes(bufBytes);

            if (OperatingSystem.IsAndroidVersionAtLeast(26)) {
                builder.SetPerformanceMode(useLowLatency
                    ? AudioTrackPerformanceMode.LowLatency
                    : AudioTrackPerformanceMode.None);
            }

            AudioTrack built = builder.Build();
            if (built.State != AudioTrackState.Initialized) {
                built.Release();
                built.Dispose();
                return false;
            }

            audioTrack = built;
            activeIsFloat32 = useFloat32;
            activeIsLowLatency = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? built.PerformanceMode == AudioTrackPerformanceMode.LowLatency
                : false;
            activeFramesPerWrite = activeIsLowLatency ? LowLatencyFramesPerWrite : BalancedFramesPerWrite;
            return true;
        } catch (Exception ex) {
            AppLogger.Warn($"AudioTrack build fallback (SR={sampleRate}, Float32={useFloat32}, LowLat={useLowLatency}): {ex.Message}");
            return false;
        }
    }

    void FinalizeTrackSetup() {
        if (audioTrack == null) {
            return;
        }
        try {
            int actualTrackRate = audioTrack.SampleRate;
            if (actualTrackRate >= 44100 && actualTrackRate <= 192000 && actualTrackRate != activeSampleRate) {
                activeSampleRate = actualTrackRate;
            }
            if (activeSampleRate != activeRequestedSampleRate) {
                engine.SetPreferredHardwareSampleRate(activeSampleRate);
                activeRequestedSampleRate = activeSampleRate;
            }
            audioTrackFramesWritten = 0;
            legacyTrackFlushRequested = false;
            Volatile.Write(ref cachedHalPeriodSec, QueryAudioManagerHalPeriodSeconds());

            if (usbDacNativeSampleRate && preferredDevice != null && activeTransport == AudioDeviceTransport.UsbDac) {
                audioTrack.SetPreferredDevice(preferredDevice);
            } else {
                audioTrack.SetPreferredDevice(null);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"AudioTrack.SetPreferredDevice skipped: {ex.Message}");
        }

        AppLogger.Info(
            $"AudioTrack ready: {activeSampleRate} Hz, {(activeIsFloat32 ? "Float32" : "PCM16")}, " +
            $"{(activeIsLowLatency ? "LowLatency" : "Balanced")} ({activeFramesPerWrite}f), " +
            $"Route='{preferredDeviceName}'");
    }

    public void Dispose() {
        engine?.RegisterOutputFlushCallback(null);
        Stop();
        routeFsm.Dispose();
        if (audioDeviceCallbackManager != null && audioDeviceCallback != null) {
            try {
                audioDeviceCallbackManager.UnregisterAudioDeviceCallback(audioDeviceCallback);
            } catch {
                // Ignore
            }
            audioDeviceCallback = null;
            audioDeviceCallbackManager = null;
        }
        audioCallbackHandler = null;
        lock (lifecycleLock) {
            if (usbManager != null) {
                usbManager.DeviceStateChanged -= OnUsbDeviceStateChanged;
                usbManager.BeforeUsbConnectionClosing -= OnBeforeUsbConnectionClosing;
                usbManager.PreAmpSnapshotChanged -= OnUsbPreAmpSnapshotChanged;
                usbManager = null;
            }
        }
        GC.SuppressFinalize(this);
    }
}
