using Android.Content;
using Android.Media;
using Android.OS;

using Cavern.ProAudio;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Services;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Audio;

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
/// Transport configuration, USB/AudioDevice callbacks, fallback studio mic capture, and live diagnostics/telemetry.
/// </summary>
public sealed partial class AndroidAudioOutput {
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

    void OnUsbPreAmpSnapshotChanged(HardwarePreAmpStateSnapshot snapshot) {
        PushEffectivePreAmpSnapshot(snapshot);
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
            if (nativeStatus.BufferFrames >= 64) {
                activeHalBurstFrames = nativeStatus.BufferFrames;
            }
            if (nativeStatus.OutputXRuns > lastObservedNativeXRuns) {
                lastObservedNativeXRuns = nativeStatus.OutputXRuns;
                int step = Math.Max(activeHalBurstFrames, sr / 100);
                adaptiveWatermarkBoostFrames = Math.Min(adaptiveWatermarkBoostFrames + step, (int)(sr * 0.032));
            }
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
}
