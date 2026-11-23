using Android.Media;

using Cavern.ProAudio;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Audio;

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

    // Pre-allocated zero-GC audio thread buffers (never reallocated inside AudioLoop)
    readonly float[] floatRenderBuffer = new float[MaxPreallocatedFrames * 2];
    readonly float[] captureInterleavedBuffer = new float[MaxPreallocatedFrames * 2];
    readonly short[] pcm16FallbackBuffer = new short[MaxPreallocatedFrames * 2];
    readonly AudioTimestamp audioTimestamp = new();

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

    public AndroidAudioOutput(RealtimePlayerEngine engine) => this.engine = engine;

    /// <summary>
    /// Binds <see cref="UsbProAudioDeviceManager"/> so Path B (<c>libusb-1.0</c> UAC2) can acquire the live
    /// unrooted Android <c>UsbDeviceConnection.FileDescriptor</c> and react to USB hot-plug / permission events.
    /// </summary>
    public void AttachUsbManager(UsbProAudioDeviceManager manager) {
        lock (lifecycleLock) {
            if (usbManager != null) {
                usbManager.DeviceStateChanged -= OnUsbDeviceStateChanged;
                usbManager.BeforeUsbConnectionClosing -= OnBeforeUsbConnectionClosing;
            }
            usbManager = manager;
            if (usbManager != null) {
                usbManager.DeviceStateChanged += OnUsbDeviceStateChanged;
                usbManager.BeforeUsbConnectionClosing += OnBeforeUsbConnectionClosing;
            }
            RequestRebuild(retryNative: true);
        }
    }

    /// <summary>
    /// Applies updated Slot 1 / Slot 4 Pro-Audio Full-Duplex USB settings (Path B / Path A / buffer size / live mic)
    /// and schedules a seamless backend reconfigure if needed.
    /// </summary>
    public void ApplyProAudioConfig(ProAudioDuplexConfig config) {
        ProAudioDuplexConfig clean = (config ?? ProAudioDuplexConfig.Default).Sanitize();
        lock (lifecycleLock) {
            bool needRestart =
                proAudioConfig.Enabled != clean.Enabled ||
                proAudioConfig.PreferredPath != clean.PreferredPath ||
                proAudioConfig.BufferFrames != clean.BufferFrames ||
                proAudioConfig.LockedSampleRateHz != clean.LockedSampleRateHz ||
                proAudioConfig.LiveMicEnabled != clean.LiveMicEnabled ||
                (proAudioConfig.MicRouting == LiveMicRoutingMode.Off) != (clean.MicRouting == LiveMicRoutingMode.Off);

            proAudioConfig = clean;
            engine.ApplyProAudioConfig(clean);

            // Sync hardware sample rate cleanly, clearing any stale lock when returning to Auto (0)
            int nextPreferredRate = clean.LockedSampleRateHz > 0
                ? clean.LockedSampleRateHz
                : (lastRouteInfo != null ? NegotiatePreferredUsbSampleRate(lastRouteInfo) : 0);
            engine.SetPreferredHardwareSampleRate(nextPreferredRate);

            if (needRestart) {
                RequestRebuild(retryNative: true);
            }
        }
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
        lock (lifecycleLock) {
            AudioDeviceInfo nextPreferred = routeInfo.Transport == AudioDeviceTransport.UsbDac ? routeInfo.NativeDevice : null;
            bool deviceChanged = !string.Equals(preferredDeviceName, routeInfo.DisplayName, StringComparison.Ordinal) ||
                                 activeTransport != routeInfo.Transport ||
                                 preferredOutputDeviceId != routeInfo.OutputDeviceId ||
                                 preferredInputDeviceId != routeInfo.InputDeviceId ||
                                 !ReferenceEquals(preferredDevice, nextPreferred);
            lastRouteInfo = routeInfo;
            preferredDevice = nextPreferred;
            activeTransport = routeInfo.Transport;
            preferredDeviceName = routeInfo.DisplayName ?? string.Empty;
            preferredOutputDeviceId = routeInfo.OutputDeviceId;
            preferredInputDeviceId = routeInfo.InputDeviceId;

            int negotiatedRate = proAudioConfig.LockedSampleRateHz > 0
                ? proAudioConfig.LockedSampleRateHz
                : NegotiatePreferredUsbSampleRate(routeInfo);
            engine.SetPreferredHardwareSampleRate(negotiatedRate);

            if (deviceChanged) {
                RequestRebuild(retryNative: true);
            }
        }
    }

    void OnBeforeUsbConnectionClosing() {
        // Must stop Path B libusb synchronously BEFORE Android closes the UsbDeviceConnection FD
        // to prevent fdsan SIGABRT / libusb UAF when unplugging USB DACs (e.g., Focusrite Scarlett).
        lock (lifecycleLock) {
            if (activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct) {
                AppLogger.Info("AndroidAudioOutput: USB interface detaching — stopping Path B (libusb) before FD close.");
                StopNativeBackendLocked();
                RequestRebuild(retryNative: true);
            }
        }
    }

    void OnUsbDeviceStateChanged(UsbProAudioDeviceSnapshot snap) {
        lock (lifecycleLock) {
            if (proAudioConfig.Enabled) {
                RequestRebuild(retryNative: true);
            }
        }
        PublishProAudioDiagnostics();
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

        int desired = Math.Clamp(engine.OutputSampleRate, 44100, 192000);
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
    /// Starts the real-time audio output thread if not already running.
    /// </summary>
    public void Start() {
        lock (lifecycleLock) {
            if (running) {
                return;
            }
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
        lock (lifecycleLock) {
            if (engine.IsLiveMonitoringOrCalibrating) {
                return;
            }
            if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
                StopNativeBackendLocked();
                return;
            }
            try {
                audioTrack?.Pause();
            } catch {
                // Ignore if uninitialized
            }
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
        lock (lifecycleLock) {
            if (fallbackMicRunning) {
                return true;
            }
            int sr = activeSampleRate >= 44100 ? activeSampleRate : 48000;
            if (sr > 48000) {
                sr = 48000;
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
                AppLogger.Info($"W48 Studio fallback AudioRecord started @ {sr} Hz mono.");
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
            threadToJoin = renderThread;
            renderThread = null;
        }

        if (threadToJoin != null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread) {
            threadToJoin.Join(1000);
        }

        lock (lifecycleLock) {
            StopNativeBackendLocked();
            ReleaseTrack();
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
                long nowMs = Environment.TickCount64;
                if (idleSinceMs == 0) {
                    idleSinceMs = nowMs;
                }
                if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
                    // Grace period prevents tearing down/reopening AAudio across brief seek/track-switch pauses
                    if (nowMs - idleSinceMs >= 1500) {
                        lock (lifecycleLock) {
                            StopNativeBackendLocked();
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
            if (audioTrack != null && audioTrack.PlayState != PlayState.Playing) {
                try {
                    audioTrack.Play();
                } catch {
                    Thread.Sleep(15);
                    continue;
                }
            }

            int framesToRequest = activeFramesPerWrite;
            int frames = engine.RenderNextInterleavedBlock(floatRenderBuffer, framesToRequest);
            if (frames > 0 && audioTrack != null && running) {
                int written = WriteBlockZeroGc(frames);
                if (written < 0) {
                    // Recover cleanly when Android invalidates AudioTrack (e.g. Bluetooth/USB route change)
                    ReconfigureBackend(targetRate);
                    try {
                        audioTrack?.Play();
                        WriteBlockZeroGc(frames);
                    } catch {
                        Thread.Sleep(20);
                    }
                } else {
                    long nowMs = Environment.TickCount64;
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

        // 2. Pace producer smoothly against native lock-free OUT ring free space
        int burst = Math.Clamp(activeFramesPerWrite, 64, MaxPreallocatedFrames);
        int freeFrames = NativeProAudioBridge.GetPlaybackFreeFrames();
        if (freeFrames >= burst) {
            int rendered = engine.RenderNextInterleavedBlock(floatRenderBuffer, burst);
            if (rendered > 0) {
                int written = NativeProAudioBridge.WritePlayback(floatRenderBuffer, rendered);
                if (written <= 0) {
                    // Native backend stopped or disconnected (e.g., USB unplugged / AAudio error) -> recover with bounded backoff
                    nativeStallTicks = 0;
                    HandleNativeStreamDisconnect("WritePlayback returned 0");
                    return;
                }
                nativeStallTicks = 0;
                long nowMs = Environment.TickCount64;
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
            // Detect AAudio AAUDIO_ERROR_DISCONNECTED / libusb halt (ActiveBackend == 0) while OUT ring stops draining
            nativeStallTicks++;
            if (nativeStallTicks >= 12) {
                nativeStallTicks = 0;
                if (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st) &&
                    st.ActiveBackend == NativeProAudioBridge.BackendNone) {
                    HandleNativeStreamDisconnect("ActiveBackend=0");
                    return;
                }
            }
            // Sub-millisecond yield while native callback drains OUT ring
            Thread.Sleep(1);
        }
    }

    void HandleNativeStreamDisconnect(string reason) {
        long uptimeMs = Environment.TickCount64 - lastNativeStartTickMs;
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
        lock (lifecycleLock) {
            StopNativeBackendLocked();
            ReleaseTrack();

            ProAudioDuplexConfig cfg = proAudioConfig;
            int resolvedRate = ResolveClosestSupportedRate(sampleRate);
            activeRequestedSampleRate = sampleRate;
            nativeStallTicks = 0;
            int burst = Math.Clamp((int)cfg.BufferFrames, 64, 256);
            bool needInput = (cfg.LiveMicEnabled && cfg.MicRouting != LiveMicRoutingMode.Off) ||
                             engine.LiveMicBridge.IsCalibratingLatency ||
                             engine.LiveMicBridge.IsRecordingPerformance;
            lastProAudioStatusNote = string.Empty;

            if (engine.HeadphoneVirtualizer && cfg.LockedSampleRateHz > 0 && cfg.LockedSampleRateHz != resolvedRate) {
                lastProAudioStatusNote = $"HRIR virtualization locks output to {resolvedRate} Hz (ignoring {cfg.LockedSampleRateHz} Hz lock).";
            }

            bool attemptedNative = false;
            if (cfg.Enabled && cfg.PreferredPath != ProAudioEnginePath.LegacyAudioTrack && !nativeFallbackLatched) {
                if (!NativeProAudioBridge.IsAvailable) {
                    lastProAudioStatusNote = "Native libcavernize_proaudio.so not bundled; using AudioTrack fallback.";
                    nativeFallbackLatched = true;
                } else {
                    attemptedNative = true;

                    // Cascade Step 1: Path B (Unrooted libusb-1.0 UAC2 Direct Full-Duplex)
                    bool wantPathB = cfg.PreferredPath == ProAudioEnginePath.Auto ||
                                     cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2;
                    if (wantPathB && NativeProAudioBridge.SupportsPathBLibUsb && usbManager != null) {
                        UsbProAudioDeviceSnapshot usbSnap = usbManager.CurrentSnapshot;
                        if (usbSnap.IsAttached && usbSnap.HasPermission && usbSnap.IsUac2) {
                            int fd = usbManager.AcquireOpenFileDescriptor();
                            if (fd >= 0) {
                                int rc = NativeProAudioBridge.StartLibUsb(fd, resolvedRate, burst, needInput);
                                if (rc == 0) {
                                    usbManager.NotifyPathBFileDescriptorState(ownedByPathB: true);
                                    activeBackend = ActiveProAudioBackendKind.LibUsbUac2Direct;
                                    activeSampleRate = resolvedRate;
                                    activeIsFloat32 = true;
                                    activeIsLowLatency = true;
                                    activeFramesPerWrite = burst;
                                    if (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus pbSt) && pbSt.SampleRate >= 44100) {
                                        activeSampleRate = pbSt.SampleRate;
                                    }
                                    if (!engine.HeadphoneVirtualizer && activeSampleRate != resolvedRate) {
                                        engine.SetPreferredHardwareSampleRate(activeSampleRate);
                                    }
                                    engine.LiveMicBridge.SetStreamingActive(needInput, activeSampleRate);
                                    lastNativeStartTickMs = Environment.TickCount64;
                                    AppLogger.Info($"Pro-Audio Path B (libusb UAC2) active: {activeSampleRate} Hz, {burst}f, Duplex={needInput}, Device='{usbSnap.DisplayName}'");
                                    PublishDiagnostics();
                                    return;
                                }
                                lastProAudioStatusNote = $"Path B libusb start returned {rc}; falling back to Path A AAudio.";
                                AppLogger.Warn(lastProAudioStatusNote);
                            }
                        } else if (cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2) {
                            lastProAudioStatusNote = usbSnap.IsAttached && !usbSnap.HasPermission
                                ? "USB audio interface attached; awaiting Android USB permission for Path B."
                                : "No UAC2 USB audio interface attached; falling back.";
                        }
                    }

                    // Cascade Step 2: Path A (Native Android AAudio LOW_LATENCY + EXCLUSIVE Full-Duplex)
                    bool wantPathA = cfg.PreferredPath == ProAudioEnginePath.Auto ||
                                     cfg.PreferredPath == ProAudioEnginePath.PathAAudioExclusive ||
                                     cfg.PreferredPath == ProAudioEnginePath.PathBDirectUsbUac2;
                    if (wantPathA && NativeProAudioBridge.SupportsPathAAudio) {
                        bool aaudioDuplex = needInput && !suppressAAudioDuplexOnce;
                        int rc = NativeProAudioBridge.StartAAudio(
                            resolvedRate,
                            burst,
                            aaudioDuplex,
                            preferredOutputDeviceId,
                            preferredInputDeviceId);
                        if (rc == 0) {
                            activeBackend = ActiveProAudioBackendKind.AAudioOboeFullDuplex;
                            activeSampleRate = resolvedRate;
                            activeIsFloat32 = true;
                            activeIsLowLatency = true;
                            activeFramesPerWrite = burst;
                            if (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st) && st.SampleRate >= 44100) {
                                activeSampleRate = st.SampleRate;
                            }
                            if (!engine.HeadphoneVirtualizer && activeSampleRate != resolvedRate) {
                                // Keep Direct 2.0 DSP rate matched to AAudio's negotiated hardware rate without thrashing
                                engine.SetPreferredHardwareSampleRate(activeSampleRate);
                            }
                            engine.LiveMicBridge.SetStreamingActive(aaudioDuplex, activeSampleRate);
                            lastNativeStartTickMs = Environment.TickCount64;
                            AppLogger.Info($"Pro-Audio Path A (AAudio Exclusive) active: {activeSampleRate} Hz (req={resolvedRate} Hz), {burst}f, Duplex={aaudioDuplex}, OutId={preferredOutputDeviceId}, InId={preferredInputDeviceId}");
                            PublishDiagnostics();
                            return;
                        }
                        lastProAudioStatusNote = $"Path A AAudio start returned {rc}; falling back to AudioTrack.";
                        AppLogger.Warn(lastProAudioStatusNote);
                    }
                }
            }

            // Cascade Step 3: Legacy Zero-GC AudioTrack Fallback
            if (attemptedNative) {
                // Latch fallback until config/device/USB state changes to prevent infinite ReconfigureBackend thrash loop
                nativeFallbackLatched = true;
            }
            activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
            engine.LiveMicBridge.SetStreamingActive(false, resolvedRate);
            BuildLegacyAudioTrackLocked(resolvedRate);
        }
    }

    void StopNativeBackendLocked() {
        if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack) {
            bool wasPathB = activeBackend == ActiveProAudioBackendKind.LibUsbUac2Direct;
            NativeProAudioBridge.Stop();
            activeBackend = ActiveProAudioBackendKind.LegacyAudioTrack;
            if (wasPathB) {
                usbManager?.NotifyPathBFileDescriptorState(ownedByPathB: false);
            }
            engine.LiveMicBridge.SetStreamingActive(false, Math.Max(44100, activeSampleRate));
        }
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
            if (usbDacNativeSampleRate && preferredDevice != null && activeTransport == AudioDeviceTransport.UsbDac) {
                audioTrack.SetPreferredDevice(preferredDevice);
            } else {
                audioTrack.SetPreferredDevice(null);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"AudioTrack.SetPreferredDevice skipped: {ex.Message}");
        }

        PublishDiagnostics();
        AppLogger.Info(
            $"AudioTrack ready: {activeSampleRate} Hz, {(activeIsFloat32 ? "Float32" : "PCM16")}, " +
            $"{(activeIsLowLatency ? "LowLatency" : "Balanced")} ({activeFramesPerWrite}f), " +
            $"Route='{preferredDeviceName}', EstLatency={Diagnostics.EstimatedLatencyMs:0.0} ms");
    }

    void PublishDiagnostics() {
        if (activeBackend != ActiveProAudioBackendKind.LegacyAudioTrack &&
            NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus nativeStatus)) {
            int sr = nativeStatus.SampleRate >= 44100 ? nativeStatus.SampleRate : Math.Max(44100, activeSampleRate);
            float latencyMs = nativeStatus.EstimatedRttMs > 0.1f
                ? nativeStatus.EstimatedRttMs * 0.5f
                : Math.Clamp(activeFramesPerWrite * 2000f / sr, 2.5f, 40f);
            UsbProAudioDeviceSnapshot usbSnap = usbManager?.CurrentSnapshot;
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
            PublishProAudioDiagnostics(nativeStatus);
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

            float transportBaseMs = activeTransport switch {
                AudioDeviceTransport.UsbDac => activeIsLowLatency ? 6f : 14f,
                AudioDeviceTransport.WiredJack => activeIsLowLatency ? 8f : 16f,
                AudioDeviceTransport.BleAudio => 32f,
                AudioDeviceTransport.BluetoothA2dp => 65f,
                AudioDeviceTransport.HdmiOrLineOut => 24f,
                _ => activeIsLowLatency ? 12f : 22f
            };

            float estimatedLatencyMs = Math.Clamp(bufferMs * 0.65f + transportBaseMs, 8f, 120f);
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

        PublishProAudioDiagnostics();
    }

    void PublishProAudioDiagnostics(NativeProAudioStatus? nativeStatus = null) {
        UsbProAudioDeviceSnapshot usbSnap = usbManager?.CurrentSnapshot;
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

        ProAudioDuplexDiagnostics paDiag = new() {
            ActiveBackend = activeBackend,
            NativeLibraryLoaded = NativeProAudioBridge.IsAvailable,
            UsbInterfaceDetected = usbSnap?.IsAttached ?? false,
            UsbPermissionGranted = usbSnap?.HasPermission ?? false,
            IsFullDuplexActive = hasNative ? st.IsFullDuplex != 0 : engine.LiveMicBridge.IsActive,
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
            StatusNote = lastProAudioStatusNote
        };

        ProAudioDiagnostics = paDiag;
        engine.SceneHub.ProAudioDiagnostics = paDiag;
        ProAudioDiagnosticsUpdated?.Invoke(paDiag);
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
    }

    public void Dispose() {
        Stop();
        lock (lifecycleLock) {
            if (usbManager != null) {
                usbManager.DeviceStateChanged -= OnUsbDeviceStateChanged;
                usbManager.BeforeUsbConnectionClosing -= OnBeforeUsbConnectionClosing;
                usbManager = null;
            }
        }
        GC.SuppressFinalize(this);
    }
}
