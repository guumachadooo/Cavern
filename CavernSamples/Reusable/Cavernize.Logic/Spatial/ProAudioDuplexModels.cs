using System.Numerics;

using Cavern.ProAudio;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Selectable Slot 1 / Slot 4 Pro-Audio full-duplex engine routing preference.
/// </summary>
public enum ProAudioEnginePath {
    /// <summary>Automatic priority cascade: Path B (libusb UAC2) -> Path A (AAudio Exclusive) -> Legacy AudioTrack.</summary>
    Auto = 0,
    /// <summary>Path B: Direct unrooted libusb-1.0 UAC2 isochronous full-duplex driver bypassing Android AudioFlinger.</summary>
    PathBDirectUsbUac2 = 1,
    /// <summary>Path A: Native Android AAudio / Oboe LOW_LATENCY + EXCLUSIVE full-duplex engine.</summary>
    PathAAudioExclusive = 2,
    /// <summary>Legacy zero-GC Android AudioTrack output-only fallback.</summary>
    LegacyAudioTrack = 3
}

/// <summary>
/// Active hardware driver backend currently serving audio playback and/or live microphone capture.
/// </summary>
public enum ActiveProAudioBackendKind {
    /// <summary>Standard zero-GC AudioTrack playback (output only).</summary>
    LegacyAudioTrack = 0,
    /// <summary>Path A: Native AAudio / Oboe Exclusive or Shared low-latency full-duplex engine.</summary>
    AAudioOboeFullDuplex = 1,
    /// <summary>Path B: Direct unrooted libusb-1.0 UAC2 isochronous full-duplex driver.</summary>
    LibUsbUac2Direct = 2
}

/// <summary>
/// Selectable low-latency buffer frame size for Path B UAC2 / Path A AAudio full-duplex operation.
/// </summary>
public enum ProAudioBufferPreset {
    /// <summary>Ultra-low latency 64 frames (~1.33 ms @ 48 kHz; recommended for Path B direct USB).</summary>
    Frames64 = 64,
    /// <summary>Balanced pro-audio 128 frames (~2.67 ms @ 48 kHz).</summary>
    Frames128 = 128,
    /// <summary>High-stability 256 frames (~5.33 ms @ 48 kHz; safest for heavy spatial DSP + neural Karaoke).</summary>
    Frames256 = 256
}

/// <summary>
/// Live USB/pro-audio microphone input channel selection routed into <see cref="SourceLayer.LiveMicrophone"/>.
/// </summary>
public enum LiveMicRoutingMode {
    /// <summary>Live microphone capture disabled.</summary>
    Off = 0,
    /// <summary>Mono XLR / Combo Input 1 (e.g., Scarlett 2i2 Mic 1) positioned as a 3D vocal object.</summary>
    MonoChannel1 = 1,
    /// <summary>Mono XLR / Combo Input 2 (e.g., Scarlett 2i2 Mic 2 / Inst 2) positioned as a 3D vocal object.</summary>
    MonoChannel2 = 2,
    /// <summary>Stereo input pair (Channels 1 + 2).</summary>
    StereoPair = 3
}

/// <summary>
/// Persistent user configuration for W10 Pro-Audio Full-Duplex USB Engine (Path B `libusb` UAC2 / Path A `AAudio/Oboe`)
/// and live microphone injection into <see cref="SpatialSceneHub"/>.
/// </summary>
public sealed class ProAudioDuplexConfig {
    /// <summary>Master toggle enabling Path B/A Pro-Audio full-duplex engine (when false, uses standard AudioTrack).</summary>
    public bool Enabled { get; set; }

    /// <summary>Preferred engine path (Auto, Path B Direct USB UAC2, Path A AAudio Exclusive, or Legacy).</summary>
    public ProAudioEnginePath PreferredPath { get; set; } = ProAudioEnginePath.Auto;

    /// <summary>Selectable low-latency burst size: 64, 128, or 256 frames.</summary>
    public ProAudioBufferPreset BufferFrames { get; set; } = ProAudioBufferPreset.Frames128;

    /// <summary>Native sample rate lock (0 = follow hardware/track native rate; or 44100/48000/96000/192000).</summary>
    public int LockedSampleRateHz { get; set; }

    /// <summary>Enables full-duplex live microphone capture routed into <see cref="SourceLayer.LiveMicrophone"/>.</summary>
    public bool LiveMicEnabled { get; set; }

    /// <summary>Input channel routing mode (Ch1 mono, Ch2 mono, or Stereo 1+2).</summary>
    public LiveMicRoutingMode MicRouting { get; set; } = LiveMicRoutingMode.MonoChannel1;

    /// <summary>Digital input trim gain applied to the captured mic stream in dB (-18..+24 dB).</summary>
    public float MicInputGainDb { get; set; }

    /// <summary>Live microphone monitoring wet level in [0; 1] mixed sample-locked with Karaoke playback.</summary>
    public float MicMonitorLevel { get; set; } = 0.85f;

    /// <summary>Normalized 3D room position of the <see cref="SourceLayer.LiveMicrophone"/> object (default front-center stage).</summary>
    public Vector3 MicNormalizedPosition { get; set; } = new(0f, 0.05f, 0.72f);

    /// <summary>Calibrated round-trip latency offset in samples (populated by <see cref="RoundTripLatencyCalibrator"/>).</summary>
    public double CalibratedRoundTripSamples { get; set; }

    /// <summary>Calibrated round-trip latency in milliseconds (0 = uncalibrated).</summary>
    public float CalibratedRoundTripMs { get; set; }

    /// <summary>Returns a sanitized copy with clamped safe bounds.</summary>
    public ProAudioDuplexConfig Sanitize() {
        int buf = (int)BufferFrames;
        ProAudioBufferPreset safeBuf = buf <= 64
            ? ProAudioBufferPreset.Frames64
            : (buf <= 128 ? ProAudioBufferPreset.Frames128 : ProAudioBufferPreset.Frames256);

        int sr = LockedSampleRateHz;
        if (sr != 0 && sr != 44100 && sr != 48000 && sr != 88200 && sr != 96000 && sr != 176400 && sr != 192000) {
            sr = 0;
        }

        return new ProAudioDuplexConfig {
            Enabled = Enabled,
            PreferredPath = Enum.IsDefined(typeof(ProAudioEnginePath), PreferredPath) ? PreferredPath : ProAudioEnginePath.Auto,
            BufferFrames = safeBuf,
            LockedSampleRateHz = sr,
            LiveMicEnabled = LiveMicEnabled,
            MicRouting = Enum.IsDefined(typeof(LiveMicRoutingMode), MicRouting) ? MicRouting : LiveMicRoutingMode.MonoChannel1,
            MicInputGainDb = Math.Clamp(MicInputGainDb, -18f, 24f),
            MicMonitorLevel = Math.Clamp(MicMonitorLevel, 0f, 1.25f),
            MicNormalizedPosition = new Vector3(
                Math.Clamp(MicNormalizedPosition.X, -1f, 1f),
                Math.Clamp(MicNormalizedPosition.Y, -1f, 1f),
                Math.Clamp(MicNormalizedPosition.Z, -1f, 1f)),
            CalibratedRoundTripSamples = Math.Clamp(CalibratedRoundTripSamples, 0.0, 48000.0),
            CalibratedRoundTripMs = Math.Clamp(CalibratedRoundTripMs, 0f, 500f)
        };
    }

    /// <summary>Default conservative Pro-Audio configuration (Path A/B ready, safe 128f bursts).</summary>
    public static ProAudioDuplexConfig Default => new();
}

/// <summary>
/// Live diagnostic snapshot published by the active Slot 1 / Slot 4 Pro-Audio Full-Duplex engine.
/// </summary>
public sealed class ProAudioDuplexDiagnostics {
    /// <summary>Currently active backend (Path B Direct libusb UAC2, Path A AAudio Exclusive, or Legacy AudioTrack).</summary>
    public ActiveProAudioBackendKind ActiveBackend { get; init; } = ActiveProAudioBackendKind.LegacyAudioTrack;

    /// <summary>True when the native ARM64 <c>libcavernize_proaudio.so</c> engine loaded successfully.</summary>
    public bool NativeLibraryLoaded { get; init; }

    /// <summary>True when a UAC2 USB audio interface is currently attached and detected via <c>UsbManager</c>.</summary>
    public bool UsbInterfaceDetected { get; init; }

    /// <summary>True when the user has granted Android USB Host permission for the attached interface.</summary>
    public bool UsbPermissionGranted { get; init; }

    /// <summary>True when full-duplex live microphone capture is actively streaming.</summary>
    public bool IsFullDuplexActive { get; init; }

    /// <summary>True when Path A AAudio secured hardware EXCLUSIVE MMAP sharing or Path B bypassed AudioFlinger directly.</summary>
    public bool IsExclusiveHardwareBypass { get; init; }

    /// <summary>Human-readable connected USB / AAudio device name.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>USB Vendor ID (0 if non-USB).</summary>
    public int VendorId { get; init; }

    /// <summary>USB Product ID (0 if non-USB).</summary>
    public int ProductId { get; init; }

    /// <summary>Detected Focusrite Scarlett generation (None, Gen2, Gen3, Gen4).</summary>
    public ScarlettGeneration ScarlettGeneration { get; init; } = ScarlettGeneration.None;

    /// <summary>Active hardware sample rate in Hz.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Active wire bit depth (16, 24, or 32).</summary>
    public int BitDepth { get; init; } = 32;

    /// <summary>Active audio burst / buffer size in frames (64, 128, 256, or 1024).</summary>
    public int BufferFrames { get; init; } = 256;

    /// <summary>Active UAC2 asynchronous synchronization mode (Explicit Feedback, Implicit Feedback, Adaptive, or N/A).</summary>
    public string ClockSyncMode { get; init; } = string.Empty;

    /// <summary>Live asynchronous USB clock drift estimate in ppm relative to nominal sample rate.</summary>
    public float AsyncClockDriftPpm { get; init; }

    /// <summary>Estimated or MLS-calibrated round-trip latency in milliseconds.</summary>
    public float RoundTripLatencyMs { get; init; }

    /// <summary>True when <see cref="RoundTripLatencyMs"/> was physically measured via MLS loopback calibration.</summary>
    public bool IsLatencyCalibrated { get; init; }

    /// <summary>Cumulative playback underruns (output XRuns).</summary>
    public int OutputXRuns { get; init; }

    /// <summary>Cumulative capture overruns/underruns (input XRuns).</summary>
    public int InputXRuns { get; init; }

    /// <summary>Live microphone RMS level in dBFS.</summary>
    public float MicRmsDbfs { get; init; } = -120f;

    /// <summary>Live microphone peak level in dBFS.</summary>
    public float MicPeakDbfs { get; init; } = -120f;

    /// <summary>Human-readable fallback or error explanation when Path B could not be opened.</summary>
    public string StatusNote { get; init; } = string.Empty;

    /// <summary>Neutral default diagnostics snapshot.</summary>
    public static ProAudioDuplexDiagnostics Default => new();

    /// <summary>
    /// Open-source and open-specification attribution notice for W10 Pro-Audio USB &amp; Scarlett 2i2 protocols.
    /// </summary>
    public static string BuildAttributionNotice() =>
        "W10 Pro-Audio Full-Duplex USB Engine — Open Specs & Clean-Room References:\n" +
        "• USB-IF Audio Device Class Specification 2.0 (UAC2): Clock Source CS_SAM_FREQ_CONTROL, Isochronous Async 16.16/10.14 Feedback, FORMAT_TYPE_I.\n" +
        "• libusb/libusb (LGPL-2.1): Unrooted Android UsbManager FD bridge (LIBUSB_OPTION_NO_DEVICE_DISCOVERY + libusb_wrap_sys_device).\n" +
        "• Android NDK AAudio & google/oboe (Apache-2.0): LOW_LATENCY + EXCLUSIVE MMAP full-duplex output-driven callback + MLS loopback RTT calibration.\n" +
        "• Linux Kernel sound/usb/mixer_scarlett2.c & geoffreybennett/alsa-scarlett-gui (GPL-2.0/3.0 public protocol spec): Focusrite Scarlett 2i2 (Gen 2/3/4, VID 0x1235) USB vendor control protocol (48V, Air, Inst/Line, Direct Monitor, Hardware Peak Meters).";
}

/// <summary>
/// Slot 1 contract providing lock-free, sample-locked live microphone audio frames into <see cref="SpatialSceneHub"/>
/// (<see cref="SourceLayer.LiveMicrophone"/>) and <see cref="Rendering.RealtimePlayerEngine"/>.
/// </summary>
public interface ILiveMicrophoneProvider {
    /// <summary>True when live microphone streaming is currently active and pushing frames.</summary>
    bool IsActive { get; }

    /// <summary>Active hardware input sample rate in Hz.</summary>
    int SampleRate { get; }

    /// <summary>Current normalized 3D room position of the live microphone source.</summary>
    Vector3 NormalizedPosition { get; }

    /// <summary>Latest measured input RMS level in dBFS.</summary>
    float RmsDbfs { get; }

    /// <summary>Latest measured input peak level in dBFS.</summary>
    float PeakDbfs { get; }

    /// <summary>Pushes newly captured interleaved stereo/mono input frames from the USB/AAudio thread (zero-GC, lock-free).</summary>
    int PushCapturedInterleaved(float[] interleavedInput, int offset, int frameCount, int inputChannels);

    /// <summary>Pulls sample-locked stereo interleaved live microphone frames on the playback render thread (zero-GC, lock-free).</summary>
    int PullRoutedStereoBlock(float[] stereoOut, int offset, int frameCount);
}
