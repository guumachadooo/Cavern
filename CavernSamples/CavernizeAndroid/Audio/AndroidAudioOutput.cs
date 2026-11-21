using Android.Media;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Audio;

/// <summary>
/// Zero-GC, low-latency real-time stereo PCM audio output streaming <see cref="RealtimePlayerEngine"/>
/// to Android's <see cref="AudioTrack"/> with <see cref="AudioTrackPerformanceMode.LowLatency"/>,
/// 32-bit IEEE Float (<see cref="AudioEncoding.PcmFloat"/>) priority with PCM16 fallback,
/// USB DAC preferred device routing + native sample-rate negotiation (44.1..192 kHz),
/// and real-time latency estimation for Slot 4 head-tracking prediction.
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
    readonly short[] pcm16FallbackBuffer = new short[MaxPreallocatedFrames * 2];
    readonly AudioTimestamp audioTimestamp = new();

    Thread renderThread;
    volatile bool running;
    volatile bool lowLatencyMode = true;
    volatile bool usbDacNativeSampleRate = true;
    volatile bool trackRebuildRequested;

    AudioTrack audioTrack;
    AudioDeviceInfo preferredDevice;
    AudioDeviceTransport activeTransport = AudioDeviceTransport.Unknown;
    string preferredDeviceName = string.Empty;
    int activeSampleRate;
    bool activeIsFloat32 = true;
    bool activeIsLowLatency;
    int activeFramesPerWrite = LowLatencyFramesPerWrite;
    int timestampCounter;

    /// <summary>
    /// Enables <see cref="AudioTrackPerformanceMode.LowLatency"/> and 256-frame bursts (~5.3 ms @ 48 kHz).
    /// </summary>
    public bool LowLatencyMode {
        get => lowLatencyMode;
        set {
            if (lowLatencyMode != value) {
                lowLatencyMode = value;
                trackRebuildRequested = true;
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
                trackRebuildRequested = true;
            }
        }
    }

    /// <summary>
    /// Latest live output diagnostics snapshot (sample rate, Float32, LowLatency, USB DAC, estimated latency, underruns).
    /// </summary>
    public AudioOutputDiagnostics Diagnostics { get; private set; } = AudioOutputDiagnostics.Default;

    /// <summary>
    /// Raised whenever output track format, sample rate, or latency mode changes (background thread safe).
    /// </summary>
    public event Action<AudioOutputDiagnostics> DiagnosticsUpdated;

    public AndroidAudioOutput(RealtimePlayerEngine engine) => this.engine = engine;

    /// <summary>
    /// Updates the active Android output device metadata (from <see cref="AudioDeviceProfileManager"/>)
    /// to negotiate USB DAC native sample rates and apply <see cref="AudioTrack.SetPreferredDevice"/>.
    /// </summary>
    public void UpdateOutputRoute(AudioDeviceRouteInfo routeInfo) {
        if (routeInfo == null) {
            return;
        }
        lock (lifecycleLock) {
            bool deviceChanged = !string.Equals(preferredDeviceName, routeInfo.DisplayName, StringComparison.Ordinal) ||
                                 activeTransport != routeInfo.Transport ||
                                 !ReferenceEquals(preferredDevice, routeInfo.NativeDevice);
            preferredDevice = routeInfo.Transport == AudioDeviceTransport.UsbDac ? routeInfo.NativeDevice : null;
            activeTransport = routeInfo.Transport;
            preferredDeviceName = routeInfo.DisplayName ?? string.Empty;

            int negotiatedRate = NegotiatePreferredUsbSampleRate(routeInfo);
            engine.SetPreferredHardwareSampleRate(negotiatedRate);

            if (deviceChanged) {
                trackRebuildRequested = true;
            }
        }
    }

    /// <summary>
    /// Negotiates the closest supported USB DAC sample rate (44.1..192 kHz) for the current track when USB DAC mode is active.
    /// </summary>
    int NegotiatePreferredUsbSampleRate(AudioDeviceRouteInfo routeInfo) {
        if (!usbDacNativeSampleRate || routeInfo.Transport != AudioDeviceTransport.UsbDac) {
            return 0;
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
    /// Pauses playback hardware cleanly.
    /// </summary>
    public void Pause() {
        lock (lifecycleLock) {
            try {
                audioTrack?.Pause();
            } catch {
                // Ignore if uninitialized
            }
        }
    }

    /// <summary>
    /// Stops and tears down the audio output thread and <see cref="AudioTrack"/>.
    /// </summary>
    public void Stop() {
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
            if (!engine.IsPlaying) {
                if (audioTrack != null && audioTrack.PlayState == PlayState.Playing) {
                    try {
                        audioTrack.Pause();
                    } catch {
                        // Ignore
                    }
                }
                Thread.Sleep(12);
                continue;
            }

            int targetRate = Math.Clamp(engine.OutputSampleRate, 44100, 192000);
            if (audioTrack == null || activeSampleRate != targetRate || trackRebuildRequested) {
                trackRebuildRequested = false;
                RecreateTrack(targetRate);
            }

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
                    RecreateTrack(targetRate);
                    try {
                        audioTrack?.Play();
                        WriteBlockZeroGc(frames);
                    } catch {
                        Thread.Sleep(20);
                    }
                } else {
                    timestampCounter++;
                    if (timestampCounter >= 32) {
                        timestampCounter = 0;
                        PublishDiagnostics();
                    }
                }
            } else {
                Thread.Sleep(8);
            }
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

    void RecreateTrack(int sampleRate) {
        lock (lifecycleLock) {
            ReleaseTrack();
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
        AudioTrack track = audioTrack;
        if (track == null) {
            return;
        }

        int sr = Math.Max(44100, activeSampleRate);
        float bufferMs = 0f;
        int underruns = 0;
        try {
            int bufFrames = track.BufferSizeInFrames;
            if (bufFrames > 0) {
                bufferMs = bufFrames * 1000f / sr;
            }
            underruns = track.UnderrunCount;
            // Query hardware presentation timestamp without allocation when playing
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
        GC.SuppressFinalize(this);
    }
}
