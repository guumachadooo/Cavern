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
/// Zero-lock, zero-GC Single-Producer Single-Consumer stereo float32 ring/handoff guard
/// with ARM64 acquire/release (<see cref="Volatile"/>) semantics and PDC fade-in recovery for async ONNX/post-proc gaps.
/// </summary>
public sealed class SpscAudioRingHandoff {
    const int PowerOfTwoFrames = 4096;
    const int FrameMask = PowerOfTwoFrames - 1;
    readonly float[] ring = new float[PowerOfTwoFrames * 2];
    long writeFrame;
    long readFrame;
    bool fadeInPending;

    public int CapacityFrames => PowerOfTwoFrames - 1;

    public int AvailableReadFrames {
        get {
            long w = Volatile.Read(ref writeFrame);
            long r = Volatile.Read(ref readFrame);
            return (int)Math.Clamp(w - r, 0, CapacityFrames);
        }
    }

    public int AvailableWriteFrames => CapacityFrames - AvailableReadFrames;

    public void Reset() {
        Volatile.Write(ref readFrame, 0);
        Volatile.Write(ref writeFrame, 0);
        fadeInPending = false;
    }

    public void MarkAsyncGap() {
        fadeInPending = true;
    }

    public int Write(float[] src, int srcFrameOffset, int frameCount) {
        if (src == null || frameCount <= 0) {
            return 0;
        }
        long r = Volatile.Read(ref readFrame);
        long w = Volatile.Read(ref writeFrame);
        int free = (int)Math.Clamp(CapacityFrames - (w - r), 0, CapacityFrames);
        int take = Math.Min(frameCount, free);
        if (take <= 0) {
            return 0;
        }
        for (int i = 0; i < take; i++) {
            int dstIdx = ((int)((w + i) & FrameMask)) * 2;
            int srcIdx = (srcFrameOffset + i) * 2;
            float l = src[srcIdx];
            float rCh = src[srcIdx + 1];
            if (!float.IsFinite(l)) l = 0f;
            if (!float.IsFinite(rCh)) rCh = 0f;
            ring[dstIdx] = Math.Clamp(l, -1f, 1f);
            ring[dstIdx + 1] = Math.Clamp(rCh, -1f, 1f);
        }
        Volatile.Write(ref writeFrame, w + take);
        return take;
    }

    public int Read(float[] dst, int maxFrames) {
        if (dst == null || maxFrames <= 0) {
            return 0;
        }
        long w = Volatile.Read(ref writeFrame);
        long r = Volatile.Read(ref readFrame);
        int avail = (int)Math.Clamp(w - r, 0, CapacityFrames);
        int take = Math.Min(maxFrames, avail);
        if (take <= 0) {
            return 0;
        }
        for (int i = 0; i < take; i++) {
            int srcIdx = ((int)((r + i) & FrameMask)) * 2;
            int dstIdx = i * 2;
            dst[dstIdx] = ring[srcIdx];
            dst[dstIdx + 1] = ring[srcIdx + 1];
        }
        if (fadeInPending) {
            fadeInPending = false;
            int ramp = Math.Min(32, take);
            for (int i = 0; i < ramp; i++) {
                float gain = (i + 1) / (float)ramp;
                dst[i * 2] *= gain;
                dst[i * 2 + 1] *= gain;
            }
        }
        Volatile.Write(ref readFrame, r + take);
        return take;
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
public sealed class AndroidAudioOutput : IDisposable {
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

    AudioTrack audioTrack;
    AudioDeviceRouteInfo lastRouteInfo;
    AudioDeviceInfo preferredDevice;
    AudioDeviceTransport activeTransport = AudioDeviceTransport.Unknown;
    string preferredDeviceName = string.Empty;
    int preferredOutputDeviceId;
    int preferredInputDeviceId;

    ActiveProAudioBackendKind activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
    string lastProAudioStatusNote = string.Empty;
    int activeSampleRate;
    int activeRequestedSampleRate;
    bool activeIsFloat32 = true;
    bool activeIsLowLatency;
    int activeFramesPerWrite = LowLatencyFramesPerWrite;
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

    /// <summary>
    /// Binds <see cref="UsbProAudioDeviceManager"/> so Path B (<c>libusb-1.0</c> UAC2) can acquire the live
    /// unrooted Android <c>UsbDeviceConnection.FileDescriptor</c> and react to USB hot-plug / permission events.
    /// </summary>
    public void AttachUsbManager(UsbProAudioDeviceManager manager) {
        UsbProAudioDeviceManager prevMgr;
        bool isHeadphone;
        lock (lifecycleLock) {
            prevMgr = usbManager;
            if (prevMgr != null) {
                prevMgr.DeviceStateChanged -= OnUsbDeviceStateChanged;
                prevMgr.BeforeUsbConnectionClosing -= OnBeforeUsbConnectionClosing;
                prevMgr.PreAmpSnapshotChanged -= OnUsbPreAmpSnapshotChanged;
            }
            usbManager = manager;
            if (manager != null) {
                manager.DeviceStateChanged += OnUsbDeviceStateChanged;
                manager.BeforeUsbConnectionClosing += OnBeforeUsbConnectionClosing;
                manager.PreAmpSnapshotChanged += OnUsbPreAmpSnapshotChanged;
            }
            isHeadphone = ComputeIsHeadphoneMonitoringLocked();
            routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
            RequestRebuild(retryNative: true);
        }
        EnsureAudioDeviceCallbackRegistered();
        if (manager != null) {
            manager.UpdateHeadphoneMonitoring(isHeadphone);
            manager.UpdateMicSource(ProAudioConfig.MicSource);
        }
        PushEffectivePreAmpSnapshot(manager?.CurrentPreAmpSnapshot);
    }

    void EnsureAudioDeviceCallbackRegistered() {
        try {
            if (audioDeviceCallback != null) {
                return;
            }
            Context ctx = Android.App.Application.Context;
            if (ctx?.GetSystemService(Context.AudioService) is AudioManager am) {
                Handler h = new(Looper.MainLooper);
                AudioOutputDeviceCallback cb = new(this);
                am.RegisterAudioDeviceCallback(cb, h);
                audioDeviceCallbackManager = am;
                audioCallbackHandler = h;
                audioDeviceCallback = cb;
            }
        } catch {
            // Best effort outside Android AudioManager runtime
        }
    }

    void OnHardwareAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
        if (removedDevices == null || removedDevices.Length == 0) {
            return;
        }
        bool clearedEndpoint = false;
        lock (lifecycleLock) {
            for (int i = 0; i < removedDevices.Length; i++) {
                AudioDeviceInfo dev = removedDevices[i];
                if (dev == null) {
                    continue;
                }
                bool isUsb = dev.Type == AudioDeviceType.UsbDevice || dev.Type == AudioDeviceType.UsbHeadset;
                if (preferredInputDeviceId != 0 && (dev.Id == preferredInputDeviceId || isUsb)) {
                    preferredInputDeviceId = 0;
                    clearedEndpoint = true;
                }
                if (preferredOutputDeviceId != 0 && dev.Id == preferredOutputDeviceId) {
                    preferredOutputDeviceId = 0;
                    preferredDevice = null;
                    clearedEndpoint = true;
                }
                if (lastRouteInfo != null && (lastRouteInfo.InputDeviceId == dev.Id || lastRouteInfo.OutputDeviceId == dev.Id)) {
                    lastRouteInfo = null;
                    clearedEndpoint = true;
                }
            }
            if (clearedEndpoint) {
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
        }
        if (clearedEndpoint) {
            AppLogger.Info("AndroidAudioOutput: Cleared removed audio device endpoint IDs (InId=0) on OnAudioDevicesRemoved.");
            PublishProAudioDiagnostics();
        }
    }

    sealed class AudioOutputDeviceCallback(AndroidAudioOutput owner) : AudioDeviceCallback {
        public override void OnAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
            base.OnAudioDevicesRemoved(removedDevices);
            owner.OnHardwareAudioDevicesRemoved(removedDevices);
        }
    }

    /// <summary>
    /// Applies updated Slot 1 / Slot 4 Pro-Audio Full-Duplex USB settings (Path B / Path A / buffer size / live mic)
    /// and schedules a seamless backend reconfigure if needed.
    /// </summary>
    public void ApplyProAudioConfig(ProAudioDuplexConfig config) {
        ProAudioDuplexConfig clean = (config ?? ProAudioDuplexConfig.Default).Sanitize();
        UsbProAudioDeviceManager mgr;
        lock (lifecycleLock) {
            bool needRestart =
                proAudioConfig.Enabled != clean.Enabled ||
                proAudioConfig.PreferredPath != clean.PreferredPath ||
                proAudioConfig.BufferFrames != clean.BufferFrames ||
                proAudioConfig.LockedSampleRateHz != clean.LockedSampleRateHz ||
                proAudioConfig.LiveMicEnabled != clean.LiveMicEnabled ||
                proAudioConfig.MicSource != clean.MicSource ||
                (proAudioConfig.MicRouting == LiveMicRoutingMode.Off) != (clean.MicRouting == LiveMicRoutingMode.Off);

            proAudioConfig = clean;
            if (clean.MicSource == ProAudioMicSource.PhoneMic) {
                // Eliminate sticky USB InId (e.g. InId=705) whenever PhoneMic is selected
                AudioDeviceType curInType = lastRouteInfo?.NativeInputDevice?.Type ?? AudioDeviceType.Unknown;
                if (curInType != AudioDeviceType.BuiltinMic) {
                    preferredInputDeviceId = 0;
                }
            }
            engine.ApplyProAudioConfig(clean);

            // Sync hardware sample rate cleanly, clearing any stale lock when returning to Auto (0)
            int nextPreferredRate = ComputePreferredHardwareSampleRateLocked(clean, lastRouteInfo);
            engine.SetPreferredHardwareSampleRate(nextPreferredRate);

            if (needRestart) {
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
            mgr = usbManager;
        }
        mgr?.UpdateMicSource(clean.MicSource);
        PushEffectivePreAmpSnapshot(mgr?.CurrentPreAmpSnapshot);
        PublishProAudioDiagnostics();
    }

    /// <summary>
    /// Updates the active Android output &amp; input device metadata (from <see cref="AudioDeviceProfileManager"/>)
    /// to negotiate USB DAC native sample rates, <see cref="AudioTrack.SetPreferredDevice"/>, and Path A AAudio device IDs.
    /// </summary>
    public void UpdateOutputRoute(AudioDeviceRouteInfo routeInfo) {
        if (routeInfo == null) {
            return;
        }
        UsbProAudioDeviceManager mgr;
        bool isHeadphone;
        bool changed;
        lock (lifecycleLock) {
            AudioDeviceInfo nextPreferred = routeInfo.Transport == AudioDeviceTransport.UsbDac ? routeInfo.NativeDevice : null;
            bool forcePhoneMic = routeInfo.MicSource == ProAudioMicSource.PhoneMic ||
                                 proAudioConfig.MicSource == ProAudioMicSource.PhoneMic;
            int nextInId;
            if (forcePhoneMic) {
                bool isBuiltinIn = routeInfo.NativeInputDevice != null &&
                                   routeInfo.NativeInputDevice.Type == AudioDeviceType.BuiltinMic;
                nextInId = isBuiltinIn ? routeInfo.InputDeviceId : 0;
            } else {
                nextInId = routeInfo.InputDeviceId;
            }
            bool deviceChanged = !string.Equals(preferredDeviceName, routeInfo.DisplayName, StringComparison.Ordinal) ||
                                 activeTransport != routeInfo.Transport ||
                                 preferredOutputDeviceId != routeInfo.OutputDeviceId ||
                                 preferredInputDeviceId != nextInId ||
                                 lastRouteInfo?.MicSource != routeInfo.MicSource ||
                                 !ReferenceEquals(preferredDevice, nextPreferred);
            lastRouteInfo = routeInfo;
            preferredDevice = nextPreferred;
            activeTransport = routeInfo.Transport;
            preferredDeviceName = routeInfo.DisplayName ?? string.Empty;
            preferredOutputDeviceId = routeInfo.OutputDeviceId;
            preferredInputDeviceId = nextInId;

            int negotiatedRate = ComputePreferredHardwareSampleRateLocked(proAudioConfig, routeInfo);
            engine.SetPreferredHardwareSampleRate(negotiatedRate);

            if (deviceChanged) {
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
            changed = deviceChanged;
            isHeadphone = ComputeIsHeadphoneMonitoringLocked();
            mgr = usbManager;
        }
        mgr?.UpdateHeadphoneMonitoring(isHeadphone);
        PushEffectivePreAmpSnapshot(mgr?.CurrentPreAmpSnapshot);
        if (changed) {
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
            if (lastRouteInfo?.NativeInputDevice?.Type != AudioDeviceType.BuiltinMic) {
                preferredInputDeviceId = 0;
            }
            if (activeTransport == AudioDeviceTransport.UsbDac) {
                preferredOutputDeviceId = 0;
                preferredDevice = null;
            }
            StopNativeBackendLocked(forceStopNative: true);
            RequestRebuild(retryNative: true);
        }
    }

    void OnUsbPreAmpSnapshotChanged(HardwarePreAmpStateSnapshot snapshot) {
        PushEffectivePreAmpSnapshot(snapshot);
    }

    void OnUsbDeviceStateChanged(UsbProAudioDeviceSnapshot snap) {
        lock (lifecycleLock) {
            if (snap == null || !snap.IsAttached) {
                if (lastRouteInfo?.NativeInputDevice?.Type != AudioDeviceType.BuiltinMic) {
                    preferredInputDeviceId = 0;
                }
                routeFsm.NotifyUsbDetached();
                RequestRebuild(retryNative: true);
            } else if (proAudioConfig.Enabled) {
                routeFsm.AdvanceEpoch(AudioRouteFsmState.Debouncing);
                RequestRebuild(retryNative: true);
            }
        }
        PushEffectivePreAmpSnapshot();
        PublishProAudioDiagnostics(usbSnapOverride: snap);
    }

    void RequestRebuild(bool retryNative) {
        if (retryNative) {
            nativeFallbackLatched = false;
            nativeRetryRequested = true;
            consecutiveRapidNativeDisconnects = 0;
            suppressAAudioDuplexOnce = false;
        }
        trackRebuildRequested = true;
    }

    /// <summary>
    /// Negotiates the closest supported USB DAC sample rate (44.1..192 kHz) for the current track when USB DAC mode is active.
    /// </summary>
    int NegotiatePreferredUsbSampleRate(AudioDeviceRouteInfo routeInfo) {
        if (routeInfo.Transport != AudioDeviceTransport.UsbDac) {
            // Non-USB routes (Speaker, Wired 3.5mm, Bluetooth) share Android HAL 48 kHz clock to avoid 44.1<->48k AAudio teardowns
            return 48000;
        }
        if (!usbDacNativeSampleRate) {
            return 48000;
        }
        int[] supported = routeInfo.SupportedSampleRates;
        if (supported == null || supported.Length == 0) {
            return 0; // HAL accepts arbitrary stream rate directly
        }

        int desired = engine.HeadphoneVirtualizer
            ? (Cavern.Virtualizer.VirtualizerFilter.FilterSampleRate > 0 ? Cavern.Virtualizer.VirtualizerFilter.FilterSampleRate : 48000)
            : Math.Clamp(engine.OutputSampleRate, 44100, 192000);
        int bestRate = 0;
        int bestDelta = int.MaxValue;
        for (int i = 0; i < supported.Length; i++) {
            int rate = supported[i];
            if (rate < 44100 || rate > 192000) {
                continue;
            }
            if (rate == desired) {
                return rate;
            }
            int delta = Math.Abs(rate - desired);
            // Prefer exact integer multiples (e.g. 48 -> 96 kHz, 44.1 -> 88.2 kHz)
            if (desired > 0 && (rate % desired == 0 || desired % rate == 0)) {
                delta /= 2;
            }
            if (delta < bestDelta) {
                bestDelta = delta;
                bestRate = rate;
            }
        }
        return bestRate;
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
    /// Ensures live microphone PCM is actively captured into <see cref="RealtimePlayerEngine.LiveMicBridge"/>
    /// during Studio vocal performance recording, using Path A/B duplex when active or a zero-GC managed
    /// <see cref="AudioRecord"/> fallback when running on <see cref="ActiveProAudioBackendKind.LegacyAudioTrack"/>.
    /// </summary>
    public bool EnsureStudioRecordingCapture(bool active) {
        if (!active) {
            StopFallbackMicCapture();
            return true;
        }
        if (engine.LiveMicBridge.IsActive) {
            return true;
        }
        return StartFallbackMicCapture();
    }

    bool StartFallbackMicCapture() {
        ProAudioMicSource micSource;
        AudioDeviceRouteInfo routeSnap;
        int sr;
        lock (lifecycleLock) {
            if (fallbackMicRunning) {
                return true;
            }
            micSource = proAudioConfig.MicSource;
            routeSnap = lastRouteInfo;
            sr = activeSampleRate >= 44100 ? activeSampleRate : 48000;
            if (sr > 48000) {
                sr = 48000;
            }
        }

        // Resolve preferred Android input outside lifecycleLock so PhoneMic never hijacks USB capture
        AudioDeviceInfo preferredInput = routeSnap != null && routeSnap.MicSource == micSource
            ? routeSnap.NativeInputDevice
            : null;
        if (preferredInput == null) {
            try {
                if (Android.App.Application.Context?.GetSystemService(Android.Content.Context.AudioService) is AudioManager am) {
                    AudioDeviceInfo[] inputs = am.GetDevices(GetDevicesTargets.Inputs);
                    preferredInput = AudioDeviceProfileManager.SelectBestInputDevice(
                        inputs,
                        routeSnap?.Transport ?? AudioDeviceTransport.Unknown,
                        routeSnap?.DisplayName ?? string.Empty,
                        micSource);
                }
            } catch {
                // Ignore outside Android context
            }
        }

        lock (lifecycleLock) {
            if (fallbackMicRunning) {
                return true;
            }
            try {
                int minBytes = AudioRecord.GetMinBufferSize(sr, ChannelIn.Mono, AudioEncoding.Pcm16bit);
                if (minBytes <= 0) {
                    minBytes = 4096;
                }
                int bufBytes = Math.Max(minBytes * 2, fallbackMicPcm16.Length * sizeof(short) * 2);
                AudioRecord rec = new(AudioSource.Mic, sr, ChannelIn.Mono, AudioEncoding.Pcm16bit, bufBytes);
                if (rec.State != State.Initialized) {
                    rec.Release();
                    rec.Dispose();
                    return false;
                }
                if (preferredInput != null) {
                    rec.SetPreferredDevice(preferredInput);
                }
                rec.StartRecording();
                fallbackMicRecord = rec;
                fallbackMicRunning = true;
                fallbackMicOwnsStreamingFlag = true;
                engine.LiveMicBridge.SetStreamingActive(true, sr);
                fallbackMicThread = new Thread(() => FallbackMicCaptureLoop(rec)) {
                    Name = "CavernizeStudioMicRec",
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.AboveNormal
                };
                fallbackMicThread.Start();
                AppLogger.Info($"W48 Studio fallback AudioRecord started @ {sr} Hz mono (MicSource={micSource}).");
                return true;
            } catch (Exception ex) {
                AppLogger.Warn($"W48 StartFallbackMicCapture failed: {ex.Message}");
                StopFallbackMicCaptureLocked();
                return false;
            }
        }
    }

    void FallbackMicCaptureLoop(AudioRecord rec) {
        const float InvShort = 1f / 32768f;
        while (fallbackMicRunning) {
            try {
                int read = rec.Read(fallbackMicPcm16, 0, fallbackMicPcm16.Length);
                if (read > 0) {
                    for (int i = 0; i < read; i++) {
                        fallbackMicFloat[i] = fallbackMicPcm16[i] * InvShort;
                    }
                    engine.LiveMicBridge.PushCapturedInterleaved(fallbackMicFloat, 0, read, 1);
                } else if (read < 0) {
                    Thread.Sleep(8);
                }
            } catch {
                break;
            }
        }
    }

    void StopFallbackMicCapture() {
        Thread toJoin;
        lock (lifecycleLock) {
            fallbackMicRunning = false;
            toJoin = fallbackMicThread;
            fallbackMicThread = null;
            if (fallbackMicRecord != null) {
                try { fallbackMicRecord.Stop(); } catch { }
            }
        }
        if (toJoin != null && toJoin.IsAlive && toJoin != Thread.CurrentThread) {
            toJoin.Join(400);
        }
        lock (lifecycleLock) {
            StopFallbackMicCaptureLocked();
        }
    }

    void StopFallbackMicCaptureLocked() {
        fallbackMicRunning = false;
        if (fallbackMicRecord != null) {
            try { fallbackMicRecord.Stop(); } catch { }
            try { fallbackMicRecord.Release(); } catch { }
            try { fallbackMicRecord.Dispose(); } catch { }
            fallbackMicRecord = null;
        }
        if (fallbackMicOwnsStreamingFlag) {
            fallbackMicOwnsStreamingFlag = false;
            engine.LiveMicBridge.SetStreamingActive(false, Math.Max(44100, activeSampleRate));
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

            // Legacy AudioTrack path (zero-GC, output-only)
            if (legacyTrackFlushRequested) {
                ExecuteLegacyTrackFlush();
            }
            if (audioTrack != null && audioTrack.PlayState != PlayState.Playing) {
                try {
                    audioTrack.Play();
                } catch {
                    Thread.Sleep(15);
                    continue;
                }
            }

            int epochBeforeWrite = Volatile.Read(ref flushEpoch);
            int framesToRequest = activeFramesPerWrite;
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

        // 2. Pace producer with bounded write-ahead watermark (keeps RT duplex/mic <= 2.7-4 ms; music-only 4*burst)
        int burst = Math.Clamp(activeFramesPerWrite, 64, MaxPreallocatedFrames);
        bool duplexOrLiveMic = (proAudioConfig.Enabled &&
                                proAudioConfig.LiveMicEnabled &&
                                proAudioConfig.MicRouting != LiveMicRoutingMode.Off) ||
                               engine.LiveMicBridge.IsActive ||
                               engine.LiveMicBridge.IsCalibratingLatency ||
                               engine.LiveMicBridge.IsRecordingPerformance;
        int maxBuffered = duplexOrLiveMic ? Math.Max(burst * 2, 128) : Math.Max(burst * 4, 512);
        if (duplexOrLiveMic && engine.LiveMicBridge.BufferedFrames > maxBuffered) {
            engine.LiveMicBridge.TrimLatencyBacklog(maxBuffered);
        }
        int sr = Math.Max(44100, activeSampleRate);
        int bufferedFrames = NativeProAudioBridge.GetPlaybackBufferedFrames();
        int freeFrames = NativeProAudioBridge.GetPlaybackFreeFrames();
        if (freeFrames >= burst && bufferedFrames < maxBuffered) {
            int epochBefore = Volatile.Read(ref flushEpoch);
            if (spscRingHandoff.AvailableReadFrames < burst) {
                int rendered = engine.RenderNextInterleavedBlock(floatRenderBuffer, burst);
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

            int framesToWrite = spscRingHandoff.Read(spscPlaybackBuffer, Math.Min(burst, freeFrames));
            if (framesToWrite > 0) {
                int written = NativeProAudioBridge.WritePlayback(spscPlaybackBuffer, framesToWrite);
                if (written < 0) {
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
                int postBuffered = NativeProAudioBridge.GetPlaybackBufferedFrames();
                UpdateLiveOutputLatencySeconds((postBuffered / (double)sr) + Volatile.Read(ref cachedNativeHardwareLatencySec));
                nativeStallTicks = 0;
                long nowMs = System.Environment.TickCount64;
                if (consecutiveRapidNativeDisconnects > 0 && nowMs - lastNativeStartTickMs >= 2000) {
                    consecutiveRapidNativeDisconnects = 0;
                }
                if (nowMs - lastDiagPublishMs >= 350) {
                    lastDiagPublishMs = nowMs;
                    PublishDiagnostics();
                }
            } else {
                Thread.Sleep(2);
            }
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
                    Volatile.Write(ref cachedNativeHardwareLatencySec, ComputeNativeHardwareLatencySeconds(st, st.SampleRate >= 44100 ? st.SampleRate : sr));
                }
            }
            // Sub-millisecond yield while native callback drains OUT ring / respects watermark
            Thread.Sleep(1);
        }
    }

    void HandleNativeStreamDisconnect(string reason) {
        long uptimeMs = System.Environment.TickCount64 - lastNativeStartTickMs;
        bool wasPathA = activeBackend == ActiveProAudioBackendKind.AAudioOboeFullDuplex;
        if (uptimeMs < 1500) {
            consecutiveRapidNativeDisconnects++;
        } else {
            consecutiveRapidNativeDisconnects = 1;
        }

        if (consecutiveRapidNativeDisconnects >= 3) {
            if (wasPathA && !suppressAAudioDuplexOnce && proAudioConfig.LiveMicEnabled && proAudioConfig.MicRouting != LiveMicRoutingMode.Off) {
                // Built-in/HAL full-duplex AAudio rejected stream repeatedly; retry AAudio output-only once before AudioTrack
                suppressAAudioDuplexOnce = true;
                AppLogger.Warn($"Pro-Audio Path A disconnected rapidly ({reason}, streak={consecutiveRapidNativeDisconnects}); retrying AAudio output-only.");
                trackRebuildRequested = true;
                Thread.Sleep(180);
                return;
            }
            AppLogger.Warn($"Pro-Audio native backend disconnected repeatedly ({reason}, streak={consecutiveRapidNativeDisconnects}); falling back to AudioTrack until route/config changes.");
            nativeFallbackLatched = true;
            trackRebuildRequested = true;
            Thread.Sleep(120);
            return;
        }

        AppLogger.Warn($"Pro-Audio native backend reported disconnect ({reason}, streak={consecutiveRapidNativeDisconnects}); triggering auto-recovery.");
        trackRebuildRequested = true;
        Thread.Sleep(consecutiveRapidNativeDisconnects == 1 ? 150 : 320);
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
                            NativeProAudioStatus pbSt = default;
                            if (NativeProAudioBridge.TryGetStatus(out pbSt) && pbSt.SampleRate >= 44100) {
                                activeSampleRate = pbSt.SampleRate;
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
                            AppLogger.Info($"Pro-Audio Path B (libusb UAC2) active: {activeSampleRate} Hz, {burst}f, Duplex={needInput}, Device='{usbSnap?.DisplayName}'");
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
                            NativeProAudioStatus st = default;
                            if (NativeProAudioBridge.TryGetStatus(out st) && st.SampleRate >= 44100) {
                                activeSampleRate = st.SampleRate;
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
                            AppLogger.Info($"Pro-Audio Path A (AAudio Exclusive) active: {activeSampleRate} Hz (req={resolvedRate} Hz), {burst}f, Duplex={aaudioDuplex}, OutId={effectiveOutId}, InId={effectiveInId}, MicSource={cfg.MicSource}");
                        } else if (rc == -11 && !wantPathB && pathBCapable && acquiredUsbFd >= 0 && Volatile.Read(ref usbDetachEpoch) == detachEpoch) {
                            // Clean Path A (-11 / requestStart failure) -> Path B Direct USB fallback
                            int fbRc = NativeProAudioBridge.StartLibUsb(acquiredUsbFd, resolvedRate, burst, needInput);
                            if (fbRc == 0) {
                                activeBackend = ActiveProAudioBackendKind.LibUsbUac2Direct;
                                activeSampleRate = resolvedRate;
                                activeIsFloat32 = true;
                                activeIsLowLatency = true;
                                activeFramesPerWrite = burst;
                                NativeProAudioStatus pbSt = default;
                                if (NativeProAudioBridge.TryGetStatus(out pbSt) && pbSt.SampleRate >= 44100) {
                                    activeSampleRate = pbSt.SampleRate;
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

        // Phase 4: Notify Path B FD ownership & publish diagnostics OUTSIDE lifecycleLock!
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
        bool wantLowLatency = lowLatencyMode;

        // Attempt 1: Float32 + LowLatency (when enabled)
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

            // Low-latency uses 2 bursts (or HAL minimum); Balanced uses 4 bursts for rock-solid BT/background resilience
            int desiredBytes = burstFrames * (useLowLatency ? 2 : 4) * bytesPerFrame;
            int bufBytes = Math.Max(minBytes, desiredBytes);

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

    volatile string resolvedInputSourceName = string.Empty;

    static (int outId, int inId, string inputLabel) ResolveNativeEndpointIdsOutsideLock(
        ProAudioDuplexConfig cfg,
        AudioDeviceRouteInfo routeSnap,
        int curOutId,
        int curInId,
        bool needInput,
        bool usbAttached) {
        int outId = curOutId;
        int inId = cfg.MicSource == ProAudioMicSource.PhoneMic
            ? (routeSnap?.NativeInputDevice?.Type == AudioDeviceType.BuiltinMic ? routeSnap.NativeInputDevice.Id : 0)
            : curInId;
        string inLabel = routeSnap?.InputDisplayName ?? string.Empty;
        try {
            if (Android.App.Application.Context?.GetSystemService(Android.Content.Context.AudioService) is AudioManager am) {
                bool staleRouteInput = routeSnap == null || routeSnap.MicSource != cfg.MicSource || inId == 0;
                if (needInput && (staleRouteInput ||
                    (cfg.MicSource == ProAudioMicSource.PhoneMic &&
                     routeSnap?.NativeInputDevice != null &&
                     (routeSnap.NativeInputDevice.Type == AudioDeviceType.UsbDevice ||
                      routeSnap.NativeInputDevice.Type == AudioDeviceType.UsbHeadset)))) {
                    AudioDeviceInfo[] inputs = am.GetDevices(GetDevicesTargets.Inputs);
                    AudioDeviceInfo bestIn = AudioDeviceProfileManager.SelectBestInputDevice(
                        inputs,
                        routeSnap?.Transport ?? AudioDeviceTransport.Unknown,
                        routeSnap?.DisplayName ?? string.Empty,
                        cfg.MicSource);
                    if (bestIn != null) {
                        inId = bestIn.Id;
                        string rawName = bestIn.ProductName?.ToString()?.Trim() ?? string.Empty;
                        inLabel = bestIn.Type == AudioDeviceType.BuiltinMic &&
                                  (string.IsNullOrEmpty(rawName) || rawName.Equals("Android", StringComparison.OrdinalIgnoreCase))
                            ? "Phone Mic"
                            : rawName;
                    } else if (cfg.MicSource == ProAudioMicSource.PhoneMic) {
                        inId = 0;
                        inLabel = "Phone Mic";
                    }
                }
                if (outId == 0 && (routeSnap?.Transport == AudioDeviceTransport.UsbDac || usbAttached)) {
                    AudioDeviceInfo[] outputs = am.GetDevices(GetDevicesTargets.Outputs);
                    for (int i = 0; i < outputs.Length; i++) {
                        AudioDeviceInfo dev = outputs[i];
                        if (dev != null && dev.IsSink &&
                            (dev.Type == AudioDeviceType.UsbDevice || dev.Type == AudioDeviceType.UsbHeadset)) {
                            outId = dev.Id;
                            break;
                        }
                    }
                }
            }
        } catch {
            // Ignore outside Android audio manager context
        }
        return (outId, inId, inLabel);
    }

    bool ComputeIsHeadphoneMonitoringLocked() {
        if (activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct) {
            return true;
        }
        if (lastRouteInfo != null && lastRouteInfo.IsHeadphoneRoute) {
            return true;
        }
        return activeTransport == AudioDeviceTransport.UsbDac ||
               activeTransport == AudioDeviceTransport.WiredJack ||
               activeTransport == AudioDeviceTransport.BluetoothA2dp ||
               activeTransport == AudioDeviceTransport.BleAudio;
    }

    void PushEffectivePreAmpSnapshot(HardwarePreAmpStateSnapshot sourceSnap = null) {
        HardwarePreAmpStateSnapshot baseSnap = sourceSnap;
        if (baseSnap == null) {
            UsbProAudioDeviceManager mgr;
            lock (lifecycleLock) {
                mgr = usbManager;
            }
            baseSnap = mgr?.CurrentPreAmpSnapshot ?? HardwarePreAmpStateSnapshot.Empty;
        }
        bool isHeadphone;
        ProAudioMicSource micSource;
        lock (lifecycleLock) {
            isHeadphone = ComputeIsHeadphoneMonitoringLocked();
            micSource = proAudioConfig.MicSource;
        }
        HardwarePreAmpStateSnapshot merged = baseSnap.With(
            isHeadphoneMonitoring: isHeadphone,
            micSource: (int)micSource);
        engine.LiveMicBridge.UpdateHardwarePreAmpSnapshot(merged);
    }

    void PublishDiagnostics(UsbProAudioDeviceSnapshot usbSnapOverride = null) {
        if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack &&
            NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus nativeStatus)) {
            int sr = nativeStatus.SampleRate >= 44100 ? nativeStatus.SampleRate : Math.Max(44100, activeSampleRate);
            double hwLatSec = ComputeNativeHardwareLatencySeconds(nativeStatus, sr);
            Volatile.Write(ref cachedNativeHardwareLatencySec, hwLatSec);
            int curBuffered = NativeProAudioBridge.GetPlaybackBufferedFrames();
            double totalLatSec = (curBuffered / (double)sr) + hwLatSec;
            UpdateLiveOutputLatencySeconds(totalLatSec);
            float latencyMs = (float)(totalLatSec * 1000.0);
            UsbProAudioDeviceSnapshot usbSnap = usbSnapOverride ?? usbManager?.CurrentSnapshot;
            string devName = (activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct && usbSnap != null && !string.IsNullOrEmpty(usbSnap.DisplayName))
                ? usbSnap.DisplayName
                : preferredDeviceName;

            AudioOutputDiagnostics diag = new() {
                SampleRate = sr,
                IsFloat32 = nativeStatus.BitDepth >= 24,
                IsLowLatency = true,
                IsUsbDacPreferred = activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct ||
                                    (usbDacNativeSampleRate && activeTransport == AudioDeviceTransport.UsbDac),
                DeviceName = devName,
                FramesPerBurst = nativeStatus.BufferFrames > 0 ? nativeStatus.BufferFrames : activeFramesPerWrite,
                EstimatedLatencyMs = latencyMs,
                UnderrunCount = nativeStatus.OutputXRuns
            };

            Diagnostics = diag;
            engine.SceneHub.AudioDiagnostics = diag;
            DiagnosticsUpdated?.Invoke(diag);
            PublishProAudioDiagnostics(nativeStatus, usbSnap);
            return;
        }

        AudioTrack track = audioTrack;
        if (track != null) {
            int sr = Math.Max(44100, activeSampleRate);
            float bufferMs = 0f;
            int underruns = 0;
            try {
                int bufFrames = track.BufferSizeInFrames;
                if (bufFrames > 0) {
                    bufferMs = bufFrames * 1000f / sr;
                }
                underruns = track.UnderrunCount;
                _ = track.GetTimestamp(audioTimestamp);
            } catch {
                bufferMs = activeFramesPerWrite * 2000f / sr;
            }

            float transportBaseMs = GetTransportBaseLatencyMs();

            float estimatedLatencyMs = Math.Clamp(bufferMs * 0.65f + transportBaseMs, 8f, 120f);
            double liveLatMs = LiveOutputLatencySeconds * 1000.0;
            if (liveLatMs > 0.5) {
                estimatedLatencyMs = (float)liveLatMs;
            }
            AudioOutputDiagnostics diag = new() {
                SampleRate = sr,
                IsFloat32 = activeIsFloat32,
                IsLowLatency = activeIsLowLatency,
                IsUsbDacPreferred = usbDacNativeSampleRate && preferredDevice != null && activeTransport == AudioDeviceTransport.UsbDac,
                DeviceName = preferredDeviceName,
                FramesPerBurst = activeFramesPerWrite,
                EstimatedLatencyMs = estimatedLatencyMs,
                UnderrunCount = underruns
            };

            Diagnostics = diag;
            engine.SceneHub.AudioDiagnostics = diag;
            DiagnosticsUpdated?.Invoke(diag);
        }

        PublishProAudioDiagnostics(usbSnapOverride: usbSnapOverride);
    }

    /// <summary>
    /// Live legacy <see cref="AudioTrack.UnderrunCount"/> (API &gt;= 24) with fallback to cached diagnostics.
    /// </summary>
    public int LastAudioTrackUnderrunCount {
        get {
            AudioTrack track = audioTrack;
            if (track != null && OperatingSystem.IsAndroidVersionAtLeast(24)) {
                try {
                    return track.UnderrunCount;
                } catch {
                    // Fall through to cached diagnostics
                }
            }
            return Diagnostics.UnderrunCount;
        }
    }

    void PublishProAudioDiagnostics(NativeProAudioStatus? nativeStatus = null, UsbProAudioDeviceSnapshot usbSnapOverride = null) {
        UsbProAudioDeviceSnapshot usbSnap = usbSnapOverride ?? usbManager?.CurrentSnapshot;
        bool hasNative = nativeStatus.HasValue;
        NativeProAudioStatus st = nativeStatus ?? default;

        string syncModeText = st.SyncMode switch {
            1 => "Async Explicit Feedback (16.16)",
            2 => "Async Implicit Feedback",
            3 => "Adaptive USB Clock",
            _ => activeBackend == ActiveProAudioBackendKind.AAudioOboeFullDuplex
                ? (st.IsExclusive != 0 ? "AAudio EXCLUSIVE MMAP" : "AAudio Shared Low-Latency")
                : "AudioTrack Stream"
        };

        float calibratedMs = proAudioConfig.CalibratedRoundTripMs;
        bool isCalibrated = calibratedMs > 0.1f;
        float rttMs = isCalibrated
            ? calibratedMs
            : (hasNative && st.EstimatedRttMs > 0.1f ? st.EstimatedRttMs : Diagnostics.EstimatedLatencyMs * 2f);

        bool isFullDuplex = hasNative ? st.IsFullDuplex != 0 : engine.LiveMicBridge.IsActive;
        ProAudioMicSource micSource = proAudioConfig.MicSource;
        string activeInputLabel;
        if (activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct && isFullDuplex) {
            activeInputLabel = !string.IsNullOrEmpty(usbSnap?.DisplayName)
                ? $"{usbSnap.DisplayName} (USB UAC2)"
                : "USB Interface Mic (UAC2)";
        } else if (!string.IsNullOrEmpty(resolvedInputSourceName)) {
            activeInputLabel = resolvedInputSourceName;
        } else if (!string.IsNullOrEmpty(lastRouteInfo?.InputDisplayName)) {
            activeInputLabel = lastRouteInfo.InputDisplayName;
        } else {
            activeInputLabel = micSource == ProAudioMicSource.UsbInterfaceMic
                ? "USB Interface Mic"
                : (micSource == ProAudioMicSource.Auto ? "Auto Mic" : "Phone Mic");
        }

        string headphoneOutputLabel;
        if (activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct) {
            headphoneOutputLabel = !string.IsNullOrEmpty(usbSnap?.DisplayName)
                ? $"{usbSnap.DisplayName} (USB UAC2)"
                : "USB DAC Headphones (UAC2)";
        } else if (lastRouteInfo != null && lastRouteInfo.IsHeadphoneRoute) {
            headphoneOutputLabel = $"{lastRouteInfo.DisplayName} ({lastRouteInfo.Transport})";
        } else if (activeTransport == AudioDeviceTransport.BuiltinSpeaker) {
            headphoneOutputLabel = "Built-in Speaker";
        } else {
            headphoneOutputLabel = !string.IsNullOrEmpty(preferredDeviceName) ? preferredDeviceName : "Default Output";
        }

        ProAudioDuplexDiagnostics paDiag = new() {
            ActiveBackend = activeBackend,
            NativeLibraryLoaded = NativeProAudioBridge.IsAvailable,
            UsbInterfaceDetected = usbSnap?.IsAttached ?? false,
            UsbPermissionGranted = usbSnap?.HasPermission ?? false,
            IsFullDuplexActive = isFullDuplex,
            IsExclusiveHardwareBypass = hasNative && st.IsExclusive != 0,
            DeviceName = !string.IsNullOrEmpty(usbSnap?.DisplayName) ? usbSnap.DisplayName : preferredDeviceName,
            VendorId = usbSnap?.VendorId ?? 0,
            ProductId = usbSnap?.ProductId ?? 0,
            ScarlettGeneration = usbSnap?.ScarlettGeneration ?? ScarlettGeneration.None,
            SampleRate = hasNative && st.SampleRate >= 44100 ? st.SampleRate : Math.Max(44100, activeSampleRate),
            BitDepth = hasNative && st.BitDepth > 0 ? st.BitDepth : (activeIsFloat32 ? 32 : 16),
            BufferFrames = hasNative && st.BufferFrames > 0 ? st.BufferFrames : activeFramesPerWrite,
            ClockSyncMode = syncModeText,
            AsyncClockDriftPpm = hasNative ? st.AsyncDriftPpm : 0f,
            RoundTripLatencyMs = rttMs,
            IsLatencyCalibrated = isCalibrated,
            OutputXRuns = hasNative ? st.OutputXRuns : Diagnostics.UnderrunCount,
            InputXRuns = hasNative ? st.InputXRuns : 0,
            MicRmsDbfs = engine.LiveMicBridge.RmsDbfs,
            MicPeakDbfs = engine.LiveMicBridge.PeakDbfs,
            MicSource = micSource,
            ActiveInputSource = activeInputLabel,
            HeadphoneOutputMode = headphoneOutputLabel,
            StatusNote = lastProAudioStatusNote
        };

        ProAudioDiagnostics = paDiag;
        engine.SceneHub.ProAudioDiagnostics = paDiag;
        ProAudioDiagnosticsUpdated?.Invoke(paDiag);
    }

    int ComputePreferredHardwareSampleRateLocked(ProAudioDuplexConfig cfg, AudioDeviceRouteInfo routeInfo) {
        if (engine.HeadphoneVirtualizer) {
            return routeInfo != null ? NegotiatePreferredUsbSampleRate(routeInfo) : 48000;
        }
        return cfg.LockedSampleRateHz > 0
            ? cfg.LockedSampleRateHz
            : (routeInfo != null ? NegotiatePreferredUsbSampleRate(routeInfo) : 0);
    }

    void SyncPreferredHardwareRateForCurrentMode() {
        lock (lifecycleLock) {
            int nextRate = ComputePreferredHardwareSampleRateLocked(proAudioConfig, lastRouteInfo);
            engine.SetPreferredHardwareSampleRate(nextRate);
            RequestRebuild(retryNative: false);
        }
    }

    void UpdateLiveOutputLatencySeconds(double seconds) {
        double clamped = double.IsFinite(seconds) && seconds > 0.0 ? Math.Clamp(seconds, 0.0, 2.0) : 0.0;
        Volatile.Write(ref liveOutputLatencySeconds, clamped);
        engine.OutputPipelineLatencySeconds = clamped;
    }

    double ComputeNativeHardwareLatencySeconds(NativeProAudioStatus st, int sr) {
        int safeSr = Math.Max(44100, sr);
        float calibratedMs = proAudioConfig.CalibratedRoundTripMs;
        if (st.IsFullDuplex != 0 && calibratedMs > 0.1f) {
            return calibratedMs * 0.0005;
        }
        double hwMs;
        if (st.EstimatedRttMs > 0.1f) {
            hwMs = st.IsFullDuplex != 0 ? st.EstimatedRttMs * 0.5 : st.EstimatedRttMs;
        } else {
            int burst = st.BufferFrames > 0 ? st.BufferFrames : activeFramesPerWrite;
            hwMs = Math.Clamp(burst * 2000.0 / safeSr, 2.5, 40.0);
        }
        if (activeBackend == ActiveProAudioBackendKind.AAudioOboeFullDuplex && st.IsExclusive == 0) {
            if (activeTransport == AudioDeviceTransport.BluetoothA2dp) {
                hwMs = Math.Max(hwMs, 65.0);
            } else if (activeTransport == AudioDeviceTransport.BleAudio) {
                hwMs = Math.Max(hwMs, 32.0);
            }
        }
        return hwMs * 0.001;
    }

    float GetTransportBaseLatencyMs() => activeTransport switch {
        AudioDeviceTransport.UsbDac => activeIsLowLatency ? 6f : 14f,
        AudioDeviceTransport.WiredJack => activeIsLowLatency ? 8f : 16f,
        AudioDeviceTransport.BleAudio => 32f,
        AudioDeviceTransport.BluetoothA2dp => 65f,
        AudioDeviceTransport.HdmiOrLineOut => 24f,
        _ => activeIsLowLatency ? 12f : 22f
    };

    double QueryAudioManagerHalPeriodSeconds() {
        try {
            if (Android.App.Application.Context?.GetSystemService(Android.Content.Context.AudioService) is AudioManager am) {
                string srStr = am.GetProperty(AudioManager.PropertyOutputSampleRate);
                string fpbStr = am.GetProperty(AudioManager.PropertyOutputFramesPerBuffer);
                if (int.TryParse(srStr, out int amSr) && amSr >= 44100 &&
                    int.TryParse(fpbStr, out int amFpb) && amFpb > 0) {
                    return amFpb / (double)amSr;
                }
            }
        } catch {
            // Ignore outside Android audio manager context
        }
        return 0.0;
    }

    void ExecuteLegacyTrackFlush() {
        legacyTrackFlushRequested = false;
        AudioTrack track = audioTrack;
        if (track == null) {
            return;
        }
        try {
            if (track.PlayState == PlayState.Playing) {
                track.Pause();
            }
            track.Flush();
        } catch {
            // Ignore transient AudioTrack flush exceptions
        }
        audioTrackFramesWritten = 0;
    }

    void UpdateLegacyTrackLiveLatency(AudioTrack track) {
        if (track == null) {
            return;
        }
        int sr = Math.Max(44100, activeSampleRate);
        long maxTrackBufFrames = activeFramesPerWrite * 4L;
        double trackBufSec = 0.0;
        try {
            int bufFrames = track.BufferSizeInFrames;
            if (bufFrames > 0) {
                maxTrackBufFrames = bufFrames;
            }
            long headPos = (uint)track.PlaybackHeadPosition;
            if (audioTrackFramesWritten >= headPos) {
                long queued = Math.Clamp(audioTrackFramesWritten - headPos, 0L, maxTrackBufFrames);
                trackBufSec = queued / (double)sr;
            }
        } catch {
            trackBufSec = activeFramesPerWrite / (double)sr;
        }

        double halPeriodSec = Volatile.Read(ref cachedHalPeriodSec);
        double transportFloorSec = GetTransportBaseLatencyMs() * 0.001;
        double fallbackSec = trackBufSec + Math.Max(halPeriodSec, transportFloorSec);
        double measuredSec = fallbackSec;

        try {
            if (track.GetTimestamp(audioTimestamp) && audioTimestamp.FramePosition > 0) {
                double ageSec = (Java.Lang.JavaSystem.NanoTime() - audioTimestamp.NanoTime) * 1e-9;
                if (ageSec >= 0.0 && ageSec <= 0.5) {
                    double tsLatencySec = (audioTrackFramesWritten - audioTimestamp.FramePosition) / (double)sr - ageSec;
                    if (tsLatencySec >= 0.0 && tsLatencySec <= 0.75) {
                        double btFloor = (activeTransport == AudioDeviceTransport.BluetoothA2dp ||
                                          activeTransport == AudioDeviceTransport.BleAudio)
                            ? transportFloorSec * 0.5
                            : 0.0;
                        measuredSec = Math.Max(tsLatencySec, Math.Max(trackBufSec, btFloor));
                    }
                }
            }
        } catch {
            // Fall back to PlaybackHeadPosition + AudioManager/transport estimate
        }

        UpdateLiveOutputLatencySeconds(measuredSec);
    }

    void ReleaseTrack() {
        if (audioTrack != null) {
            try {
                audioTrack.Stop();
                audioTrack.Release();
                audioTrack.Dispose();
            } catch {
                // Ignore
            }
            audioTrack = null;
        }
        audioTrackFramesWritten = 0;
        legacyTrackFlushRequested = false;
        UpdateLiveOutputLatencySeconds(0.0);
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
