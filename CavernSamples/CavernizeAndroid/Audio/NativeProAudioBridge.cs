using System.Runtime.InteropServices;

using CavernizeAndroid.Services;

namespace CavernizeAndroid.Audio;

/// <summary>
/// Native status snapshot returned by <c>cavernize_pa_get_status</c> in <c>libcavernize_proaudio.so</c>.
/// Field layout matches <c>struct CavProAudioStatus</c> in <c>cavernize_proaudio_api.h</c> verbatim.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeProAudioStatus {
    /// <summary>0 = None, 1 = Path A AAudio, 2 = Path B libusb UAC2.</summary>
    public int ActiveBackend;

    /// <summary>Active hardware sample rate in Hz.</summary>
    public int SampleRate;

    /// <summary>Wire bit depth (16, 24, or 32).</summary>
    public int BitDepth;

    /// <summary>Active burst / buffer size in frames.</summary>
    public int BufferFrames;

    /// <summary>1 when AAudio EXCLUSIVE MMAP or Path B Direct USB bypass is active.</summary>
    public int IsExclusive;

    /// <summary>1 when full-duplex live microphone capture is active.</summary>
    public int IsFullDuplex;

    /// <summary>0 = N/A, 1 = Async Explicit FB, 2 = Async Implicit FB, 3 = Adaptive.</summary>
    public int SyncMode;

    /// <summary>Cumulative playback underruns (output XRuns).</summary>
    public int OutputXRuns;

    /// <summary>Cumulative capture overruns/underruns (input XRuns).</summary>
    public int InputXRuns;

    /// <summary>Estimated hardware round-trip latency in milliseconds.</summary>
    public float EstimatedRttMs;

    /// <summary>Live asynchronous USB clock drift estimate in parts per million.</summary>
    public float AsyncDriftPpm;
}

/// <summary>
/// Safe managed P/Invoke bridge to <c>libcavernize_proaudio.so</c> (ARM64 native Path A AAudio Exclusive +
/// Path B unrooted <c>libusb-1.0</c> UAC2 isochronous full-duplex engine).
/// Gracefully degrades to no-op when the native library is absent (e.g. non-NDK local dev builds).
/// </summary>
public static class NativeProAudioBridge {
    const string LibraryName = "cavernize_proaudio";

    public const int BackendNone = 0;
    public const int BackendPathAAudio = 1;
    public const int BackendPathBLibUsb = 2;

    public const int CapabilityPathAAudio = 1 << 0;
    public const int CapabilityPathBLibUsb = 1 << 1;

    static readonly object InitLock = new();
    static bool initialized;
    static bool available;
    static int capabilities;
    static int abiVersion;

    /// <summary>True when <c>libcavernize_proaudio.so</c> loaded successfully and passed ABI version check.</summary>
    public static bool IsAvailable {
        get {
            EnsureInitialized();
            return available;
        }
    }

    /// <summary>Compiled native capability bitmask (bit 0 = Path A AAudio, bit 1 = Path B libusb UAC2).</summary>
    public static int Capabilities {
        get {
            EnsureInitialized();
            return capabilities;
        }
    }

    /// <summary>True when Path A (native AAudio LOW_LATENCY + EXCLUSIVE full-duplex) is compiled in.</summary>
    public static bool SupportsPathAAudio => (Capabilities & CapabilityPathAAudio) != 0;

    /// <summary>True when Path B (unrooted libusb-1.0 UAC2 full-duplex) is compiled in.</summary>
    public static bool SupportsPathBLibUsb => (Capabilities & CapabilityPathBLibUsb) != 0;

    /// <summary>Native library ABI version (100 = W10 v1.0), or 0 if unavailable.</summary>
    public static int AbiVersion {
        get {
            EnsureInitialized();
            return abiVersion;
        }
    }

    static void EnsureInitialized() {
        if (initialized) {
            return;
        }
        lock (InitLock) {
            if (initialized) {
                return;
            }
            initialized = true;
            try {
                if (NativeLibrary.TryLoad(LibraryName, typeof(NativeProAudioBridge).Assembly, null, out _)) {
                    abiVersion = NativeGetVersion();
                    capabilities = NativeGetCapabilities();
                    available = abiVersion >= 100 && capabilities != 0;
                    AppLogger.Info($"NativeProAudioBridge loaded: ABI={abiVersion}, Caps=0x{capabilities:X} (PathA={SupportsPathAAudio}, PathB={SupportsPathBLibUsb})");
                } else {
                    available = false;
                    AppLogger.Info("NativeProAudioBridge: libcavernize_proaudio.so not present; AudioTrack fallback active.");
                }
            } catch (Exception ex) {
                available = false;
                capabilities = 0;
                AppLogger.Warn($"NativeProAudioBridge load skipped: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Path A: Opens and starts native AAudio <c>LOW_LATENCY</c> + <c>EXCLUSIVE</c> full-duplex streams.
    /// Returns 0 on success, negative error code on failure.
    /// </summary>
    public static int StartAAudio(int sampleRate, int bufferFrames, bool enableInput, int outputDeviceId, int inputDeviceId) {
        if (!SupportsPathAAudio) {
            return -1;
        }
        try {
            return NativeStartAAudio(sampleRate, bufferFrames, enableInput ? 1 : 0, outputDeviceId, inputDeviceId);
        } catch (Exception ex) {
            AppLogger.Error("NativeProAudioBridge.StartAAudio", ex);
            return -2;
        }
    }

    /// <summary>
    /// Path B: Opens and starts unrooted <c>libusb-1.0</c> UAC2 isochronous full-duplex driver via Android <c>UsbManager</c> FD.
    /// Returns 0 on success, negative error code on failure.
    /// </summary>
    public static int StartLibUsb(int usbFd, int sampleRate, int bufferFrames, bool enableInput) {
        if (!SupportsPathBLibUsb || usbFd < 0) {
            return -1;
        }
        try {
            return NativeStartLibUsb(usbFd, sampleRate, bufferFrames, enableInput ? 1 : 0);
        } catch (Exception ex) {
            AppLogger.Error("NativeProAudioBridge.StartLibUsb", ex);
            return -2;
        }
    }

    /// <summary>Stops whichever native backend (Path A or Path B) is currently running.</summary>
    public static void Stop() {
        if (!IsAvailable) {
            return;
        }
        try {
            NativeStop();
        } catch (Exception ex) {
            AppLogger.Warn($"NativeProAudioBridge.Stop: {ex.Message}");
        }
    }

    /// <summary>
    /// Lock-free notification that the active USB audio device was detached by Android <c>UsbManager</c>.
    /// Marks Path B <c>libusb</c> as disconnected without waiting on mutexes.
    /// </summary>
    public static void NotifyUsbDetached() {
        if (!IsAvailable) {
            return;
        }
        try {
            NativeNotifyUsbDetached();
        } catch (EntryPointNotFoundException) {
            // Graceful no-op if an older native binary is loaded
        } catch (Exception ex) {
            AppLogger.Warn($"NativeProAudioBridge.NotifyUsbDetached: {ex.Message}");
        }
    }

    /// <summary>Writes stereo interleaved float32 playback frames into the native lock-free OUT ring buffer.</summary>
    public static int WritePlayback(float[] stereoInterleaved, int frameCount) {
        if (!IsAvailable || stereoInterleaved == null || frameCount <= 0) {
            return 0;
        }
        return NativeWritePlayback(stereoInterleaved, frameCount);
    }

    /// <summary>Reads captured stereo interleaved float32 microphone frames from the native lock-free IN ring buffer.</summary>
    public static int ReadCapture(float[] stereoInterleaved, int maxFrames) {
        if (!IsAvailable || stereoInterleaved == null || maxFrames <= 0) {
            return 0;
        }
        return NativeReadCapture(stereoInterleaved, maxFrames);
    }

    /// <summary>Returns free frames available in the native playback ring buffer.</summary>
    public static int GetPlaybackFreeFrames() {
        if (!IsAvailable) {
            return 0;
        }
        return NativeGetPlaybackFreeFrames();
    }

    /// <summary>Returns stereo frames currently buffered in the native playback ring buffer.</summary>
    public static int GetPlaybackBufferedFrames() {
        if (!IsAvailable) {
            return 0;
        }
        try {
            return NativeGetPlaybackBufferedFrames();
        } catch (EntryPointNotFoundException) {
            return 0;
        }
    }

    /// <summary>Requests a lock-free SPSC-safe flush of all queued frames in the native playback ring buffer.</summary>
    public static void FlushPlayback() {
        if (!IsAvailable) {
            return;
        }
        try {
            NativeFlushPlayback();
        } catch (EntryPointNotFoundException) {
            // Graceful no-op if an older native binary is loaded
        }
    }

    /// <summary>Queries live hardware status, latency, clock drift ppm, and XRun counts.</summary>
    public static bool TryGetStatus(out NativeProAudioStatus status) {
        status = default;
        if (!IsAvailable) {
            return false;
        }
        try {
            return NativeGetStatus(out status) == 0;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Sends Focusrite Scarlett2/Gen4 vendor init step 0 (<c>bmRequestType=0xA1</c>, <c>bRequest=0x00</c>, 24-byte read)
    /// over the active Path B <c>libusb</c> handle. Returns bytes received in <paramref name="rxPacket"/>, or negative on error.
    /// </summary>
    public static int Scarlett2InitStep0(int vendorInterface, byte[] rxPacket, int rxCap) {
        if (!SupportsPathBLibUsb || rxCap < 0 || (rxCap > 0 && rxPacket == null)) {
            return -1;
        }
        try {
            return NativeScarlett2InitStep0(vendorInterface, rxPacket, rxCap);
        } catch (EntryPointNotFoundException) {
            return -1;
        } catch (Exception ex) {
            AppLogger.Warn($"NativeProAudioBridge.Scarlett2InitStep0: {ex.Message}");
            return -2;
        }
    }

    /// <summary>
    /// Sends Focusrite Scarlett2/Gen4 vendor init step 0 (<c>bmRequestType=0xA1</c>, <c>bRequest=0x00</c>, 24-byte read)
    /// over the active Path B <c>libusb</c> handle.
    /// </summary>
    public static int Scarlett2InitStep0(int vendorInterface, byte[] rxPacket) {
        return Scarlett2InitStep0(vendorInterface, rxPacket, rxPacket?.Length ?? 0);
    }

    /// <summary>
    /// Sends a Focusrite Scarlett2 vendor USB control command over the active Path B <c>libusb</c> handle.
    /// Returns bytes received in <paramref name="rxPacket"/>, or negative on error.
    /// </summary>
    public static int Scarlett2Xfer(int vendorInterface, byte[] txPacket, int txLen, byte[] rxPacket, int rxCap) {
        if (!SupportsPathBLibUsb || txPacket == null || rxPacket == null || txLen <= 0 || rxCap <= 0) {
            return -1;
        }
        try {
            return NativeScarlett2Xfer(vendorInterface, txPacket, txLen, rxPacket, rxCap);
        } catch (Exception ex) {
            AppLogger.Warn($"NativeProAudioBridge.Scarlett2Xfer: {ex.Message}");
            return -2;
        }
    }

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_get_version", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeGetVersion();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_get_capabilities", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeGetCapabilities();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_start_aaudio", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeStartAAudio(int sampleRate, int bufferFrames, int enableInput, int outputDeviceId, int inputDeviceId);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_start_libusb", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeStartLibUsb(int usbFd, int sampleRate, int bufferFrames, int enableInput);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_stop", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeStop();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_notify_usb_detached", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeNotifyUsbDetached();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_write_playback", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeWritePlayback([In] float[] stereoInterleaved, int frameCount);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_read_capture", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeReadCapture([Out] float[] stereoInterleaved, int maxFrames);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_get_playback_free_frames", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeGetPlaybackFreeFrames();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_get_playback_buffered_frames", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeGetPlaybackBufferedFrames();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_flush_playback", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFlushPlayback();

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_get_status", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeGetStatus(out NativeProAudioStatus outStatus);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_scarlett2_init_step0", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeScarlett2InitStep0(int vendorInterface, [Out] byte[] rxPacket, int rxCap);

    [DllImport(LibraryName, EntryPoint = "cavernize_pa_scarlett2_xfer", CallingConvention = CallingConvention.Cdecl)]
    static extern int NativeScarlett2Xfer(int vendorInterface, [In] byte[] txPacket, int txLen, [Out] byte[] rxPacket, int rxCap);
}
