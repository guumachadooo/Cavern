using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;

using Cavern.ProAudio;

using CavernizeAndroid.Audio;

namespace CavernizeAndroid.Services;

/// <summary>
/// Snapshot of an attached Android USB Host Audio Class 2.0 (UAC2) or Focusrite Scarlett interface.
/// </summary>
public sealed class UsbProAudioDeviceSnapshot {
    /// <summary>True when a compatible USB audio interface is currently attached.</summary>
    public bool IsAttached { get; init; }

    /// <summary>True when the user has granted Android <see cref="UsbManager"/> permission for this device.</summary>
    public bool HasPermission { get; init; }

    /// <summary>True when the device exposes a valid UAC2 (<c>bInterfaceProtocol == 0x20</c>) topology.</summary>
    public bool IsUac2 { get; init; }

    /// <summary>True when the device exposes both Playback (OUT) and Capture (IN) streaming interfaces.</summary>
    public bool SupportsFullDuplex { get; init; }

    /// <summary>USB Vendor ID.</summary>
    public int VendorId { get; init; }

    /// <summary>USB Product ID.</summary>
    public int ProductId { get; init; }

    /// <summary>Human-readable manufacturer + product display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Android USB device path (e.g. <c>/dev/bus/usb/001/002</c>).</summary>
    public string DevicePath { get; init; } = string.Empty;

    /// <summary>Active Linux file descriptor owned by <see cref="UsbDeviceConnection"/>, or -1 if closed.</summary>
    public int FileDescriptor { get; init; } = -1;

    /// <summary>Parsed UAC2 topology (or null until permission is granted and raw descriptors are read).</summary>
    public Uac2DeviceTopology Topology { get; init; }

    /// <summary>Detected Focusrite Scarlett generation (None, Gen2, Gen3, or Gen4).</summary>
    public ScarlettGeneration ScarlettGeneration { get; init; } = ScarlettGeneration.None;

    /// <summary>Discrete sample rates queried from UAC2 Clock Source, or empty if unavailable.</summary>
    public int[] SupportedSampleRates { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Unrooted Android USB Host (<see cref="UsbManager"/>) manager for Slot 1 / Slot 4 Pro-Audio interfaces:
/// discovers UAC2 DACs/interfaces, requests Android USB permission without root, exposes the live
/// <see cref="UsbDeviceConnection.FileDescriptor"/> to Path B (<c>libusb_wrap_sys_device</c>), and provides
/// clean-room Focusrite Scarlett 2i2 Gen 2/3/4 hardware vendor control (48V Phantom Power, Air, Inst/Line,
/// Direct Monitor, Gen 4 Digital Preamp Gain / Clip Safe, MSD exit, and hardware peak meter polling).
/// Works seamlessly under both Path B (<c>libusb</c>) and Path A (<c>AAudio</c>).
/// </summary>
public sealed class UsbProAudioDeviceManager : IDisposable {
    const string ActionUsbPermission = "hu.sbence.cavern.cavernize.USB_PROAUDIO_PERMISSION";
    const int ControlTransferTimeoutMs = 500;

    readonly Context context;
    readonly UsbManager usbManager;
    readonly Handler mainHandler;
    readonly object syncLock = new();
    readonly UsbBroadcastReceiver permissionReceiver;
    readonly UsbBroadcastReceiver usbSystemReceiver;
    readonly byte[] scarlettRxScratch = new byte[256];
    readonly float[] scarlettMeterScratch = new float[8];

    UsbDevice activeDevice;
    UsbDeviceConnection activeConnection;
    UsbProAudioDeviceSnapshot snapshot = new();
    ScarlettHardwareState scarlettState = new();
    ushort scarlettSeq = 1;
    bool scarlettInitialized;
    volatile bool pathBOwnsFileDescriptor;
    bool permissionReceiverRegistered;
    bool usbSystemReceiverRegistered;
    bool disposed;

    /// <summary>Raised on the main thread when a USB audio interface attaches, detaches, or gets permission.</summary>
    public event Action<UsbProAudioDeviceSnapshot> DeviceStateChanged;

    /// <summary>Latest attached USB audio interface snapshot.</summary>
    public UsbProAudioDeviceSnapshot CurrentSnapshot {
        get {
            lock (syncLock) {
                return snapshot;
            }
        }
    }

    /// <summary>Latest Focusrite Scarlett 2i2 hardware control state and live peak meters.</summary>
    public ScarlettHardwareState ScarlettState {
        get {
            lock (syncLock) {
                return scarlettState;
            }
        }
    }

    public UsbProAudioDeviceManager(Context context) {
        this.context = context.ApplicationContext ?? context;
        usbManager = this.context.GetSystemService(Context.UsbService) as UsbManager;
        mainHandler = new Handler(Looper.MainLooper);
        permissionReceiver = new UsbBroadcastReceiver(this);
        usbSystemReceiver = new UsbBroadcastReceiver(this);
    }

    /// <summary>Registers Android USB attach/detach/permission receivers and performs an initial bus scan.</summary>
    public void Start() {
        if (disposed || usbManager == null || (permissionReceiverRegistered && usbSystemReceiverRegistered)) {
            return;
        }
        try {
            if (!permissionReceiverRegistered) {
                IntentFilter permFilter = new(ActionUsbPermission);
                RegisterReceiverCompat(permissionReceiver, permFilter, exported: false);
                permissionReceiverRegistered = true;
            }
            if (!usbSystemReceiverRegistered) {
                IntentFilter usbFilter = new();
                usbFilter.AddAction(UsbManager.ActionUsbDeviceAttached);
                usbFilter.AddAction(UsbManager.ActionUsbDeviceDetached);
                RegisterReceiverCompat(usbSystemReceiver, usbFilter, exported: true);
                usbSystemReceiverRegistered = true;
            }
            ScanAttachedDevices(autoOpenIfPermitted: true);
        } catch (Exception ex) {
            AppLogger.Error("UsbProAudioDeviceManager.Start", ex);
        }
    }

    void RegisterReceiverCompat(BroadcastReceiver target, IntentFilter filter, bool exported) {
        // API 33 (Tiramisu) introduced ReceiverFlags; API 34+ (UpsideDownCake) throws without Exported/NotExported.
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
            context.RegisterReceiver(target, filter, exported ? ReceiverFlags.Exported : ReceiverFlags.NotExported);
        } else {
            context.RegisterReceiver(target, filter);
        }
    }

    /// <summary>
    /// Scans all attached Android USB Host devices for UAC2 audio interfaces or Focusrite Scarlett hardware.
    /// </summary>
    public UsbProAudioDeviceSnapshot ScanAttachedDevices(bool autoOpenIfPermitted = true) {
        if (disposed || usbManager == null) {
            return new UsbProAudioDeviceSnapshot();
        }

        UsbProAudioDeviceSnapshot updated;
        lock (syncLock) {
            UsbDevice best = FindBestAudioUsbDevice();
            if (best == null) {
                CloseConnectionLocked();
                activeDevice = null;
                scarlettState = new ScarlettHardwareState();
                updated = new UsbProAudioDeviceSnapshot();
                snapshot = updated;
            } else {
                bool sameDevice = activeDevice != null &&
                                  activeDevice.DeviceId == best.DeviceId &&
                                  activeDevice.VendorId == best.VendorId &&
                                  activeDevice.ProductId == best.ProductId;
                if (!sameDevice) {
                    CloseConnectionLocked();
                    activeDevice = best;
                    scarlettInitialized = false;
                }

                bool hasPerm = usbManager.HasPermission(best);
                if (hasPerm && autoOpenIfPermitted && activeConnection == null) {
                    TryOpenActiveDeviceLocked();
                }

                updated = BuildSnapshotLocked(best, hasPerm);
                snapshot = updated;
            }
        }

        NotifyDeviceStateChanged(updated);
        return updated;
    }

    /// <summary>
    /// Prompts Android's system USB Host permission dialog for the currently attached audio interface.
    /// Returns true if permission is already granted or a permission dialog was dispatched.
    /// </summary>
    public bool RequestUsbPermission() {
        if (disposed || usbManager == null) {
            return false;
        }
        UsbDevice target;
        lock (syncLock) {
            target = activeDevice ?? FindBestAudioUsbDevice();
            activeDevice = target;
        }
        if (target == null) {
            return false;
        }
        if (usbManager.HasPermission(target)) {
            ScanAttachedDevices(autoOpenIfPermitted: true);
            return true;
        }

        try {
            Intent intent = new(ActionUsbPermission);
            intent.SetPackage(context.PackageName);
            PendingIntentFlags flags = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                : PendingIntentFlags.UpdateCurrent;
            PendingIntent pending = PendingIntent.GetBroadcast(context, 0x51A0, intent, flags);
            usbManager.RequestPermission(target, pending);
            AppLogger.Info($"UsbProAudioDeviceManager: Requested Android USB permission for {FormatDeviceName(target)} (VID=0x{target.VendorId:X4}, PID=0x{target.ProductId:X4})");
            return true;
        } catch (Exception ex) {
            AppLogger.Error("UsbProAudioDeviceManager.RequestUsbPermission", ex);
            return false;
        }
    }

    /// <summary>
    /// Coordinates exclusive ownership of the open Linux USB file descriptor with native Path B (<c>libusb</c>)
    /// so <see cref="UsbDeviceConnection.ControlTransfer(UsbAddressing, int, int, int, byte[], int, int)"/> never contends on the same <c>usbfs</c> FD while Path B is active.
    /// </summary>
    public void NotifyPathBFileDescriptorState(bool ownedByPathB) {
        lock (syncLock) {
            pathBOwnsFileDescriptor = ownedByPathB;
        }
    }

    /// <summary>
    /// Ensures the active permitted USB audio device is opened and returns its raw Linux file descriptor
    /// for Path B (<c>libusb_wrap_sys_device</c>), or -1 if unavailable.
    /// </summary>
    public int AcquireOpenFileDescriptor() {
        lock (syncLock) {
            if (activeDevice == null) {
                activeDevice = FindBestAudioUsbDevice();
            }
            if (activeDevice == null || usbManager == null || !usbManager.HasPermission(activeDevice)) {
                return -1;
            }
            if (activeConnection == null) {
                TryOpenActiveDeviceLocked();
            }
            return activeConnection != null ? activeConnection.FileDescriptor : -1;
        }
    }

    /// <summary>
    /// Initializes Focusrite Scarlett2 host control mode (<c>CMD_INIT_1</c> + <c>CMD_INIT_2</c>) on verified
    /// control-table devices (Gen 3, or Gen 4 once FCP table discovery is verified) and optionally disables
    /// MSD ("Easy Start") mode so the interface operates in full UAC2 pro-audio mode.
    /// </summary>
    public bool TryInitializeScarlettControl(bool exitMsdMode = false) {
        lock (syncLock) {
            if (!scarlettState.SupportsSoftwareControls) {
                return false;
            }
            int vendorIf = snapshot.Topology?.VendorControlInterface ?? -1;
            if (vendorIf < 0) {
                return false;
            }

            if (!scarlettInitialized) {
                if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit1, null, out _)) {
                    return false;
                }
                if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit2, null, out _)) {
                    return false;
                }
                scarlettInitialized = true;
            }

            if (exitMsdMode) {
                uint msdOffset = scarlettState.Generation == ScarlettGeneration.Gen4
                    ? Scarlett2Protocol.Gen4OffsetMsdMode
                    : Scarlett2Protocol.Gen3OffsetMsdMode;
                if (WriteScarlettConfigLocked(vendorIf, msdOffset, new byte[] { 0 })) {
                    SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdConfigSave, null, out _);
                }
            }
            return true;
        }
    }

    /// <summary>Sets Focusrite Scarlett 48V Phantom Power (shared on Gen 3, independent Ch1/Ch2 on Gen 4).</summary>
    public bool SetScarlettPhantomPower(bool ch1Enabled, bool ch2Enabled) {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool ok;
            if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetPhantomPower,
                    new byte[] { (byte)(ch1Enabled ? 1 : 0), (byte)(ch2Enabled ? 1 : 0) });
                if (ok) {
                    scarlettState.PhantomPowerCh1 = ch1Enabled;
                    scarlettState.PhantomPowerCh2 = ch2Enabled;
                }
            } else {
                // Gen 3 2i2 has a single shared 48V phantom switch
                bool shared = ch1Enabled || ch2Enabled;
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetPhantomPower,
                    new byte[] { (byte)(shared ? 1 : 0) });
                if (ok) {
                    scarlettState.PhantomPowerCh1 = shared;
                    scarlettState.PhantomPowerCh2 = shared;
                }
            }
            return ok;
        }
    }

    /// <summary>Sets Focusrite Scarlett Air analog preamp emulation mode per channel.</summary>
    public bool SetScarlettAirMode(ScarlettAirMode ch1, ScarlettAirMode ch2) {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool ok;
            if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetAirMode,
                    new byte[] { (byte)ch1, (byte)ch2 });
            } else {
                byte b1 = ch1 != ScarlettAirMode.Off ? (byte)1 : (byte)0;
                byte b2 = ch2 != ScarlettAirMode.Off ? (byte)1 : (byte)0;
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetAirSwitch, new byte[] { b1, b2 });
            }
            if (ok) {
                scarlettState.AirCh1 = ch1;
                scarlettState.AirCh2 = ch2;
            }
            return ok;
        }
    }

    /// <summary>Sets Focusrite Scarlett Instrument Hi-Z / Line level switch per channel.</summary>
    public bool SetScarlettInstrumentMode(bool instCh1, bool instCh2) {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            uint offset = scarlettState.Generation == ScarlettGeneration.Gen4
                ? Scarlett2Protocol.Gen4OffsetLevelSwitch
                : Scarlett2Protocol.Gen3OffsetLevelSwitch;
            bool ok = WriteScarlettConfigLocked(vendorIf, offset,
                new byte[] { (byte)(instCh1 ? 1 : 0), (byte)(instCh2 ? 1 : 0) });
            if (ok) {
                scarlettState.InstrumentModeCh1 = instCh1;
                scarlettState.InstrumentModeCh2 = instCh2;
            }
            return ok;
        }
    }

    /// <summary>Sets Focusrite Scarlett Direct Monitor hardware routing (Off / Mono / Stereo).</summary>
    public bool SetScarlettDirectMonitor(ScarlettDirectMonitorMode mode) {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            uint offset = scarlettState.Generation == ScarlettGeneration.Gen4
                ? Scarlett2Protocol.Gen4OffsetDirectMonitor
                : Scarlett2Protocol.Gen3OffsetDirectMonitor;
            bool ok = WriteScarlettConfigLocked(vendorIf, offset, new byte[] { (byte)mode });
            if (ok) {
                scarlettState.DirectMonitor = mode;
            }
            return ok;
        }
    }

    /// <summary>Sets Gen 4 Scarlett 2i2 software-controlled digital preamp gain (0..69 dB) for Ch 1 &amp; Ch 2.</summary>
    public bool SetScarlettGen4PreampGain(float gainCh1Db, float gainCh2Db) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            short g1 = (short)Math.Clamp((int)MathF.Round(gainCh1Db), 0, 69);
            short g2 = (short)Math.Clamp((int)MathF.Round(gainCh2Db), 0, 69);
            byte[] payload = new byte[] {
                (byte)(g1 & 0xFF), (byte)((g1 >> 8) & 0xFF),
                (byte)(g2 & 0xFF), (byte)((g2 >> 8) & 0xFF)
            };
            bool ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetPreampGainDb, payload);
            if (ok) {
                scarlettState.InputGainCh1Db = g1;
                scarlettState.InputGainCh2Db = g2;
            }
            return ok;
        }
    }

    /// <summary>Toggles Gen 4 Scarlett 2i2 Clip Safe automatic preamp overload guard (linked convenience overload).</summary>
    public bool SetScarlettGen4ClipSafe(bool enabled) {
        return SetScarlettGen4ClipSafe(enabled, enabled);
    }

    /// <summary>Toggles Gen 4 Scarlett 2i2/4i4 Clip Safe automatic preamp overload guard independently per channel.</summary>
    public bool SetScarlettGen4ClipSafe(bool ch1Enabled, bool ch2Enabled) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool eff2 = scarlettState.InputsLinked ? ch1Enabled : ch2Enabled;
            bool ok = WriteScarlettConfigLocked(
                vendorIf,
                Scarlett2Protocol.Gen4OffsetClipSafe,
                new byte[] { (byte)(ch1Enabled ? 1 : 0), (byte)(eff2 ? 1 : 0) });
            if (ok) {
                scarlettState.ClipSafeCh1 = ch1Enabled;
                scarlettState.ClipSafeCh2 = eff2;
            }
            return ok;
        }
    }

    /// <summary>Links or unlinks Scarlett Gen 4 Channels 1 &amp; 2 as a synchronized stereo preamp pair.</summary>
    public bool SetScarlettGen4InputLink(bool linked) {
        lock (syncLock) {
            if (!scarlettState.SupportsStereoLink || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetInputLink, new byte[] { (byte)(linked ? 1 : 0) });
            if (ok) {
                scarlettState.InputsLinked = linked;
                if (linked) {
                    // Mirror Ch 1 settings cleanly onto Ch 2 when stereo link engages
                    scarlettState.InputGainCh2Db = scarlettState.InputGainCh1Db;
                    scarlettState.AirCh2 = scarlettState.AirCh1;
                    scarlettState.ClipSafeCh2 = scarlettState.ClipSafeCh1;
                    scarlettState.InstrumentModeCh2 = scarlettState.InstrumentModeCh1;
                    scarlettState.PhantomPowerCh2 = scarlettState.PhantomPowerCh1;
                }
            }
            return ok;
        }
    }

    /// <summary>
    /// Starts Gen 4 Focusrite Scarlett hardware Auto Gain calibration (10-second listening window).
    /// Pass <paramref name="channelIndex"/> = 0 for Ch 1, 1 for Ch 2, or -1 for both / linked stereo pair.
    /// </summary>
    public bool StartScarlettGen4AutoGain(int channelIndex) {
        lock (syncLock) {
            if (!scarlettState.SupportsAutoGain || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool run1 = channelIndex <= 0 || scarlettState.InputsLinked;
            bool run2 = channelIndex == 1 || channelIndex < 0 || scarlettState.InputsLinked;
            bool ok = WriteScarlettConfigLocked(
                vendorIf,
                Scarlett2Protocol.Gen4OffsetAutoGainEnable,
                new byte[] { (byte)(run1 ? 1 : 0), (byte)(run2 ? 1 : 0) });
            if (ok) {
                if (run1) {
                    scarlettState.AutoGainStatusCh1 = ScarlettAutoGainStatus.Running;
                }
                if (run2) {
                    scarlettState.AutoGainStatusCh2 = ScarlettAutoGainStatus.Running;
                }
            }
            return ok;
        }
    }

    /// <summary>Cancels any in-progress Gen 4 Scarlett hardware Auto Gain calibration.</summary>
    public bool CancelScarlettGen4AutoGain() {
        lock (syncLock) {
            if (!scarlettState.SupportsAutoGain || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetAutoGainEnable, new byte[] { 0, 0 });
            if (ok) {
                if (scarlettState.AutoGainStatusCh1 == ScarlettAutoGainStatus.Running) {
                    scarlettState.AutoGainStatusCh1 = ScarlettAutoGainStatus.Idle;
                }
                if (scarlettState.AutoGainStatusCh2 == ScarlettAutoGainStatus.Running) {
                    scarlettState.AutoGainStatusCh2 = ScarlettAutoGainStatus.Idle;
                }
            }
            return ok;
        }
    }

    /// <summary>Polls Gen 4 Auto Gain status and reads back the resulting preamp gains when finished.</summary>
    public bool PollScarlettGen4AutoGainStatus(out ScarlettAutoGainStatus ch1Status, out ScarlettAutoGainStatus ch2Status) {
        lock (syncLock) {
            ch1Status = scarlettState.AutoGainStatusCh1;
            ch2Status = scarlettState.AutoGainStatusCh2;
            if (!scarlettState.SupportsAutoGain || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetAutoGainStatus, 2, out int payloadOff, out int payloadLen) && payloadLen >= 2) {
                ch1Status = (ScarlettAutoGainStatus)Math.Clamp((int)scarlettRxScratch[payloadOff], 0, 3);
                ch2Status = (ScarlettAutoGainStatus)Math.Clamp((int)scarlettRxScratch[payloadOff + 1], 0, 3);
                scarlettState.AutoGainStatusCh1 = ch1Status;
                scarlettState.AutoGainStatusCh2 = ch2Status;

                if (ch1Status == ScarlettAutoGainStatus.Succeeded || ch2Status == ScarlettAutoGainStatus.Succeeded) {
                    if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetPreampGainDb, 4, out int gOff, out int gLen) && gLen >= 4) {
                        short g1 = (short)(scarlettRxScratch[gOff] | (scarlettRxScratch[gOff + 1] << 8));
                        short g2 = (short)(scarlettRxScratch[gOff + 2] | (scarlettRxScratch[gOff + 3] << 8));
                        scarlettState.InputGainCh1Db = Math.Clamp((int)g1, 0, 69);
                        scarlettState.InputGainCh2Db = Math.Clamp((int)g2, 0, 69);
                    }
                }
                return true;
            }
            return false;
        }
    }

    /// <summary>Sets Direct Monitor Input vs Playback blend percentage (0..100%) on Gen 4 interfaces.</summary>
    public bool SetScarlettDirectMonitorMix(int mixPct) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            int clamped = Math.Clamp(mixPct, 0, 100);
            bool ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetDirectMonitorMix, new byte[] { (byte)clamped });
            if (ok) {
                scarlettState.DirectMonitorMixPct = clamped;
            }
            return ok;
        }
    }

    /// <summary>Sets main monitor output Mute and Dim (-20 dB) switches on Gen 4 Scarlett interfaces.</summary>
    public bool SetScarlettOutputMuteDim(bool mute, bool dim) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool okMute = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetOutputMute, new byte[] { (byte)(mute ? 1 : 0) });
            bool okDim = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetOutputDim, new byte[] { (byte)(dim ? 1 : 0) });
            if (okMute) {
                scarlettState.OutputMute = mute;
            }
            if (okDim) {
                scarlettState.OutputDim = dim;
            }
            return okMute || okDim;
        }
    }

    /// <summary>Enables or disables Gen 4 Scarlett virtual Loopback routing.</summary>
    public bool SetScarlettLoopback(bool enabled) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 || !EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetLoopback, new byte[] { (byte)(enabled ? 1 : 0) });
            if (ok) {
                scarlettState.LoopbackEnabled = enabled;
            }
            return ok;
        }
    }

    /// <summary>Saves the active Focusrite Scarlett hardware settings into non-volatile NVRAM (<c>CmdConfigSave</c>).</summary>
    public bool SaveScarlettConfigToNvram() {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            return SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdConfigSave, null, out _);
        }
    }

    /// <summary>Reads current hardware switches and preamp gains from the Scarlett device into <see cref="ScarlettState"/>.</summary>
    public bool ReadScarlettHardwareState() {
        lock (syncLock) {
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            bool anyOk = false;
            if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetPhantomPower, 2, out int off, out int len) && len >= 2) {
                    scarlettState.PhantomPowerCh1 = scarlettRxScratch[off] != 0;
                    scarlettState.PhantomPowerCh2 = scarlettRxScratch[off + 1] != 0;
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetAirMode, 2, out off, out len) && len >= 2) {
                    scarlettState.AirCh1 = (ScarlettAirMode)Math.Clamp((int)scarlettRxScratch[off], 0, 2);
                    scarlettState.AirCh2 = (ScarlettAirMode)Math.Clamp((int)scarlettRxScratch[off + 1], 0, 2);
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetLevelSwitch, 2, out off, out len) && len >= 2) {
                    scarlettState.InstrumentModeCh1 = scarlettRxScratch[off] != 0;
                    scarlettState.InstrumentModeCh2 = scarlettRxScratch[off + 1] != 0;
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetPreampGainDb, 4, out off, out len) && len >= 4) {
                    short g1 = (short)(scarlettRxScratch[off] | (scarlettRxScratch[off + 1] << 8));
                    short g2 = (short)(scarlettRxScratch[off + 2] | (scarlettRxScratch[off + 3] << 8));
                    scarlettState.InputGainCh1Db = Math.Clamp((int)g1, 0, 69);
                    scarlettState.InputGainCh2Db = Math.Clamp((int)g2, 0, 69);
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetClipSafe, 2, out off, out len) && len >= 2) {
                    scarlettState.ClipSafeCh1 = scarlettRxScratch[off] != 0;
                    scarlettState.ClipSafeCh2 = scarlettRxScratch[off + 1] != 0;
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen4OffsetInputLink, 1, out off, out len) && len >= 1) {
                    scarlettState.InputsLinked = scarlettRxScratch[off] != 0;
                    anyOk = true;
                }
            } else {
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetPhantomPower, 1, out int off, out int len) && len >= 1) {
                    bool p = scarlettRxScratch[off] != 0;
                    scarlettState.PhantomPowerCh1 = p;
                    scarlettState.PhantomPowerCh2 = p;
                    anyOk = true;
                }
                if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetAirSwitch, 2, out off, out len) && len >= 2) {
                    scarlettState.AirCh1 = scarlettRxScratch[off] != 0 ? ScarlettAirMode.Presence : ScarlettAirMode.Off;
                    scarlettState.AirCh2 = scarlettRxScratch[off + 1] != 0 ? ScarlettAirMode.Presence : ScarlettAirMode.Off;
                    anyOk = true;
                }
            }
            return anyOk;
        }
    }

    /// <summary>
    /// Polls Focusrite Scarlett hardware input peak meters via <see cref="Scarlett2Protocol.CmdGetMeter"/>
    /// and updates <see cref="ScarlettHardwareState.InputMeterCh1Dbfs"/> / <see cref="ScarlettHardwareState.InputMeterCh2Dbfs"/>.
    /// </summary>
    public bool PollScarlettHardwareMeters(out float ch1Dbfs, out float ch2Dbfs) {
        lock (syncLock) {
            ch1Dbfs = scarlettState.InputMeterCh1Dbfs;
            ch2Dbfs = scarlettState.InputMeterCh2Dbfs;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                return false;
            }
            ushort seq = NextScarlettSeqLocked();
            byte[] tx = Scarlett2Protocol.BuildGetMeterPacket(seq, 4);
            int rxLen = TransferScarlettPacketLocked(vendorIf, tx, scarlettRxScratch);
            if (rxLen >= Scarlett2Protocol.PacketHeaderBytes &&
                Scarlett2Protocol.TryParseResponseHeader(scarlettRxScratch, rxLen, Scarlett2Protocol.CmdGetMeter, seq, out _, out _)) {
                int count = Scarlett2Protocol.ParseMeterResponseDbfs(scarlettRxScratch, rxLen, scarlettMeterScratch, 2);
                if (count >= 1) {
                    scarlettState.InputMeterCh1Dbfs = scarlettMeterScratch[0];
                    ch1Dbfs = scarlettMeterScratch[0];
                }
                if (count >= 2) {
                    scarlettState.InputMeterCh2Dbfs = scarlettMeterScratch[1];
                    ch2Dbfs = scarlettMeterScratch[1];
                }
                return count > 0;
            }
            return false;
        }
    }

    /// <summary>
    /// Computes the recommended preamp gain (0..69 dB) to target <paramref name="targetPeakDbfs"/> using
    /// <see cref="Scarlett2Protocol.ComputeRecommendedAutoGainDb"/>. Uses live Scarlett hardware meter readings when
    /// available and falls back cleanly to <paramref name="fallbackLiveMicPeakDbfs"/> from <see cref="Cavernize.Logic.Spatial.LiveMicrophoneBridge"/>.
    /// </summary>
    public float ComputeRecommendedSoftwareAutoGainDb(
        int channelIndex,
        float fallbackLiveMicPeakDbfs,
        float targetPeakDbfs = -12f) {
        lock (syncLock) {
            float hwPeak = channelIndex == 1 ? scarlettState.InputMeterCh2Dbfs : scarlettState.InputMeterCh1Dbfs;
            float measuredPeak = hwPeak > -115f ? hwPeak : fallbackLiveMicPeakDbfs;
            float currentGain = channelIndex == 1 ? scarlettState.InputGainCh2Db : scarlettState.InputGainCh1Db;
            return Scarlett2Protocol.ComputeRecommendedAutoGainDb(measuredPeak, currentGain, targetPeakDbfs);
        }
    }

    bool EnsureScarlettReadyLocked(out int vendorIf) {
        vendorIf = snapshot.Topology?.VendorControlInterface ?? -1;
        if (vendorIf < 0 || !scarlettState.SupportsSoftwareControls) {
            return false;
        }
        if (!scarlettInitialized) {
            if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit1, null, out _)) {
                return false;
            }
            if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit2, null, out _)) {
                return false;
            }
            scarlettInitialized = true;
        }
        return true;
    }

    bool ReadScarlettConfigLocked(int vendorIf, uint offset, uint length, out int payloadOffset, out int payloadLen) {
        payloadOffset = Scarlett2Protocol.PacketHeaderBytes;
        payloadLen = 0;
        ushort seq = NextScarlettSeqLocked();
        byte[] tx = Scarlett2Protocol.BuildGetDataPacket(seq, offset, length);
        int rxLen = TransferScarlettPacketLocked(vendorIf, tx, scarlettRxScratch);
        return rxLen >= Scarlett2Protocol.PacketHeaderBytes &&
               Scarlett2Protocol.TryParseResponseHeader(scarlettRxScratch, rxLen, Scarlett2Protocol.CmdGetData, seq, out _, out payloadLen);
    }

    bool WriteScarlettConfigLocked(int vendorIf, uint offset, byte[] valueBytes) {
        ushort seq = NextScarlettSeqLocked();
        byte[] tx = Scarlett2Protocol.BuildSetDataPacket(seq, offset, valueBytes);
        int rxLen = TransferScarlettPacketLocked(vendorIf, tx, scarlettRxScratch);
        return rxLen >= Scarlett2Protocol.PacketHeaderBytes &&
               Scarlett2Protocol.TryParseResponseHeader(scarlettRxScratch, rxLen, Scarlett2Protocol.CmdSetData, seq, out _, out _);
    }

    bool SendScarlettCommandLocked(int vendorIf, uint cmd, byte[] payload, out int payloadSize) {
        payloadSize = 0;
        ushort seq = NextScarlettSeqLocked();
        byte[] tx = Scarlett2Protocol.BuildCommandPacket(cmd, seq, payload);
        int rxLen = TransferScarlettPacketLocked(vendorIf, tx, scarlettRxScratch);
        return rxLen >= Scarlett2Protocol.PacketHeaderBytes &&
               Scarlett2Protocol.TryParseResponseHeader(scarlettRxScratch, rxLen, cmd, seq, out _, out payloadSize);
    }

    /// <summary>
    /// Dual-transport Scarlett2 vendor control exchange: routes exclusively through Path B <c>libusb</c> while Path B
    /// owns the USB file descriptor (preventing dual-FD <c>usbfs</c> contention), otherwise claims the vendor
    /// <see cref="UsbInterface"/> before invoking Android <see cref="UsbDeviceConnection.ControlTransfer(UsbAddressing, int, int, int, byte[], int, int)"/> and releases
    /// it immediately afterward so Scarlett controls work cleanly under Path A (AAudio) or when idle.
    /// </summary>
    int TransferScarlettPacketLocked(int vendorIf, byte[] tx, byte[] rx) {
        // 1. While Path B libusb owns the FD, NEVER issue direct Android UsbDeviceConnection transfers on the same FD
        if (pathBOwnsFileDescriptor ||
            (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st) &&
             st.ActiveBackend == NativeProAudioBridge.BackendPathBLibUsb)) {
            return NativeProAudioBridge.Scarlett2Xfer(vendorIf, tx, tx.Length, rx, rx.Length);
        }

        // 2. Direct Android UsbDeviceConnection control EP0 transfer (works under Path A AAudio or idle)
        if (activeConnection == null) {
            if (activeDevice == null || usbManager == null || !usbManager.HasPermission(activeDevice)) {
                return -1;
            }
            TryOpenActiveDeviceLocked();
        }
        if (activeConnection == null || activeDevice == null) {
            return -1;
        }

        UsbInterface vendorUsbIf = FindInterfaceById(activeDevice, vendorIf);
        bool claimed = false;
        try {
            if (vendorUsbIf != null) {
                claimed = activeConnection.ClaimInterface(vendorUsbIf, true);
            }
            int sent = activeConnection.ControlTransfer(
                (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceOut,
                Scarlett2Protocol.UsbReqCmdSend,
                0,
                vendorIf,
                tx,
                tx.Length,
                ControlTransferTimeoutMs);
            if (sent < 0) {
                return sent;
            }
            return activeConnection.ControlTransfer(
                (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceIn,
                Scarlett2Protocol.UsbReqCmdRecv,
                0,
                vendorIf,
                rx,
                rx.Length,
                ControlTransferTimeoutMs);
        } catch (Exception ex) {
            AppLogger.Warn($"Scarlett2 ControlTransfer error: {ex.Message}");
            return -2;
        } finally {
            if (claimed && vendorUsbIf != null) {
                try {
                    activeConnection.ReleaseInterface(vendorUsbIf);
                } catch {
                    // Ignore release failures on abrupt USB disconnect
                }
            }
        }
    }

    static UsbInterface FindInterfaceById(UsbDevice dev, int interfaceId) {
        if (dev == null || interfaceId < 0) {
            return null;
        }
        for (int i = 0; i < dev.InterfaceCount; i++) {
            UsbInterface iface = dev.GetInterface(i);
            if (iface != null && iface.Id == interfaceId) {
                return iface;
            }
        }
        return null;
    }

    ushort NextScarlettSeqLocked() {
        scarlettSeq++;
        if (scarlettSeq == 0) {
            scarlettSeq = 1;
        }
        return scarlettSeq;
    }

    void TryOpenActiveDeviceLocked() {
        if (activeDevice == null || usbManager == null) {
            return;
        }
        try {
            UsbDeviceConnection conn = usbManager.OpenDevice(activeDevice);
            if (conn == null) {
                AppLogger.Warn($"UsbProAudioDeviceManager: OpenDevice returned null for {FormatDeviceName(activeDevice)}");
                return;
            }
            activeConnection = conn;
            byte[] rawDesc = conn.GetRawDescriptors();
            Uac2DeviceTopology topo = Uac2Protocol.ParseConfigurationDescriptor(rawDesc);

            int[] rates = Array.Empty<int>();
            if (topo.IsUac2 && topo.ClockSourceId != 0) {
                rates = QueryUac2ClockSampleRatesLocked(conn, topo);
            }

            ScarlettGeneration gen = Scarlett2Protocol.IdentifyDevice(activeDevice.VendorId, activeDevice.ProductId, out string scarlettModel);
            scarlettState = new ScarlettHardwareState {
                Generation = gen,
                ModelName = scarlettModel,
                ProductId = activeDevice.ProductId
            };
            scarlettInitialized = false;

            snapshot = new UsbProAudioDeviceSnapshot {
                IsAttached = true,
                HasPermission = true,
                IsUac2 = topo.IsUac2,
                SupportsFullDuplex = topo.SupportsFullDuplex,
                VendorId = activeDevice.VendorId,
                ProductId = activeDevice.ProductId,
                DisplayName = string.IsNullOrEmpty(scarlettModel) ? FormatDeviceName(activeDevice) : scarlettModel,
                DevicePath = activeDevice.DeviceName ?? string.Empty,
                FileDescriptor = conn.FileDescriptor,
                Topology = topo,
                ScarlettGeneration = gen,
                SupportedSampleRates = rates
            };

            AppLogger.Info(
                $"USB Pro-Audio opened: '{snapshot.DisplayName}' (VID=0x{snapshot.VendorId:X4}, PID=0x{snapshot.ProductId:X4}, " +
                $"UAC2={topo.IsUac2}, Duplex={topo.SupportsFullDuplex}, ClockID={topo.ClockSourceId}, " +
                $"VendorIf={topo.VendorControlInterface}, Scarlett={gen}, Rates=[{string.Join(",", rates)}], FD={conn.FileDescriptor})");
        } catch (Exception ex) {
            AppLogger.Error("UsbProAudioDeviceManager.TryOpenActiveDeviceLocked", ex);
            CloseConnectionLocked();
        }
    }

    int[] QueryUac2ClockSampleRatesLocked(UsbDeviceConnection conn, Uac2DeviceTopology topo) {
        try {
            int wValue = Uac2Protocol.ClockSamFreqControl << 8;
            int wIndex = (topo.ClockSourceId << 8) | topo.AudioControlInterface;

            // Phase 1 (UAC2 spec §5.2.3.1.2 / Linux sound/usb/clock.c): read 2-byte wNumSubRanges first so strict
            // XMOS / Focusrite Scarlett Gen 4 firmware returns exact range count without STALLing on oversized wLength.
            byte[] hdr = new byte[2];
            int hdrRead = conn.ControlTransfer(
                (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceIn,
                Uac2Protocol.RequestRange,
                wValue,
                wIndex,
                hdr,
                hdr.Length,
                ControlTransferTimeoutMs);
            if (hdrRead == 2) {
                int numRanges = hdr[0] | (hdr[1] << 8);
                if (numRanges > 0 && numRanges <= 32) {
                    int exactLen = 2 + numRanges * 12;
                    byte[] exactBuf = new byte[exactLen];
                    int got = conn.ControlTransfer(
                        (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceIn,
                        Uac2Protocol.RequestRange,
                        wValue,
                        wIndex,
                        exactBuf,
                        exactBuf.Length,
                        ControlTransferTimeoutMs);
                    if (got >= 14) {
                        byte[] trimmed = new byte[got];
                        Array.Copy(exactBuf, 0, trimmed, 0, got);
                        return Uac2Protocol.ParseClockSampleRateRange(trimmed);
                    }
                }
            }

            // Phase 2 fallback: single-shot 256-byte GET_RANGE for firmwares that reject 2-byte partial reads
            byte[] buf = new byte[256];
            int n = conn.ControlTransfer(
                (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceIn,
                Uac2Protocol.RequestRange,
                wValue,
                wIndex,
                buf,
                buf.Length,
                ControlTransferTimeoutMs);
            if (n >= 14) {
                byte[] trimmed = new byte[n];
                Array.Copy(buf, 0, trimmed, 0, n);
                return Uac2Protocol.ParseClockSampleRateRange(trimmed);
            }
        } catch (Exception ex) {
            AppLogger.Warn($"UAC2 Clock GET_RANGE skipped: {ex.Message}");
        }
        return Array.Empty<int>();
    }

    UsbProAudioDeviceSnapshot BuildSnapshotLocked(UsbDevice dev, bool hasPerm) {
        if (hasPerm && activeConnection != null && snapshot != null &&
            snapshot.VendorId == dev.VendorId && snapshot.ProductId == dev.ProductId && snapshot.HasPermission) {
            return snapshot;
        }
        ScarlettGeneration gen = Scarlett2Protocol.IdentifyDevice(dev.VendorId, dev.ProductId, out string scarlettModel);
        bool looksUac2 = HasUac2Interface(dev);
        return new UsbProAudioDeviceSnapshot {
            IsAttached = true,
            HasPermission = hasPerm,
            IsUac2 = looksUac2,
            SupportsFullDuplex = false,
            VendorId = dev.VendorId,
            ProductId = dev.ProductId,
            DisplayName = string.IsNullOrEmpty(scarlettModel) ? FormatDeviceName(dev) : scarlettModel,
            DevicePath = dev.DeviceName ?? string.Empty,
            FileDescriptor = activeConnection != null ? activeConnection.FileDescriptor : -1,
            Topology = snapshot?.Topology,
            ScarlettGeneration = gen,
            SupportedSampleRates = snapshot?.SupportedSampleRates ?? Array.Empty<int>()
        };
    }

    UsbDevice FindBestAudioUsbDevice() {
        IDictionary<string, UsbDevice> map = usbManager?.DeviceList;
        if (map == null || map.Count == 0) {
            return null;
        }
        UsbDevice best = null;
        int bestScore = -1;
        foreach (UsbDevice dev in map.Values) {
            if (dev == null) {
                continue;
            }
            int score = ScoreUsbAudioCandidate(dev);
            if (score > bestScore) {
                bestScore = score;
                best = dev;
            }
        }
        return bestScore > 0 ? best : null;
    }

    static int ScoreUsbAudioCandidate(UsbDevice dev) {
        int score = 0;
        if (Scarlett2Protocol.IdentifyDevice(dev.VendorId, dev.ProductId, out _) != ScarlettGeneration.None) {
            score += 200;
        }
        bool hasAudio = false;
        bool hasUac2 = false;
        for (int i = 0; i < dev.InterfaceCount; i++) {
            UsbInterface iface = dev.GetInterface(i);
            if (iface == null) {
                continue;
            }
            if ((int)iface.InterfaceClass == Uac2Protocol.UsbClassAudio) {
                hasAudio = true;
                if (iface.InterfaceProtocol == Uac2Protocol.ProtocolUac2) {
                    hasUac2 = true;
                }
            }
        }
        if (hasUac2) {
            score += 100;
        } else if (hasAudio) {
            score += 40;
        }
        return score;
    }

    static bool HasUac2Interface(UsbDevice dev) {
        for (int i = 0; i < dev.InterfaceCount; i++) {
            UsbInterface iface = dev.GetInterface(i);
            if (iface != null &&
                (int)iface.InterfaceClass == Uac2Protocol.UsbClassAudio &&
                iface.InterfaceProtocol == Uac2Protocol.ProtocolUac2) {
                return true;
            }
        }
        return false;
    }

    static string FormatDeviceName(UsbDevice dev) {
        string mfg = dev.ManufacturerName?.Trim() ?? string.Empty;
        string prod = dev.ProductName?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(prod)) {
            if (!string.IsNullOrEmpty(mfg) && !prod.StartsWith(mfg, StringComparison.OrdinalIgnoreCase)) {
                return $"{mfg} {prod}";
            }
            return prod;
        }
        return $"USB Audio (0x{dev.VendorId:X4}:0x{dev.ProductId:X4})";
    }

    void OnUsbBroadcastReceived(Intent intent) {
        if (disposed || intent == null) {
            return;
        }
        string action = intent.Action ?? string.Empty;
        if (action == ActionUsbPermission) {
            bool granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
            AppLogger.Info($"UsbProAudioDeviceManager: USB permission result granted={granted}");
            ScanAttachedDevices(autoOpenIfPermitted: granted);
        } else if (action == UsbManager.ActionUsbDeviceAttached || action == UsbManager.ActionUsbDeviceDetached) {
            ScanAttachedDevices(autoOpenIfPermitted: true);
        }
    }

    void NotifyDeviceStateChanged(UsbProAudioDeviceSnapshot snap) {
        mainHandler.Post(() => {
            if (!disposed) {
                DeviceStateChanged?.Invoke(snap);
            }
        });
    }

    /// <summary>
    /// Raised synchronously immediately BEFORE <c>UsbDeviceConnection.Close()</c> closes the native USB file descriptor.
    /// Allows Path B (<c>libusb-1.0</c>) to stop its event thread and release the FD cleanly, preventing Android <c>fdsan</c> <c>SIGABRT</c>.
    /// </summary>
    public event Action BeforeUsbConnectionClosing;

    void CloseConnectionLocked() {
        if (activeConnection != null) {
            try {
                BeforeUsbConnectionClosing?.Invoke();
            } catch (Exception ex) {
                AppLogger.Warn($"UsbProAudioDeviceManager: BeforeUsbConnectionClosing listener error: {ex.Message}");
            }
            pathBOwnsFileDescriptor = false;
            try {
                activeConnection.Close();
                activeConnection.Dispose();
            } catch {
                // Ignore
            }
            activeConnection = null;
        } else {
            pathBOwnsFileDescriptor = false;
        }
        scarlettInitialized = false;
    }

    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        if (permissionReceiverRegistered) {
            try {
                context.UnregisterReceiver(permissionReceiver);
            } catch {
                // Ignore
            }
            permissionReceiverRegistered = false;
        }
        if (usbSystemReceiverRegistered) {
            try {
                context.UnregisterReceiver(usbSystemReceiver);
            } catch {
                // Ignore
            }
            usbSystemReceiverRegistered = false;
        }
        lock (syncLock) {
            CloseConnectionLocked();
            activeDevice = null;
        }
        GC.SuppressFinalize(this);
    }

    sealed class UsbBroadcastReceiver(UsbProAudioDeviceManager owner) : BroadcastReceiver {
        public override void OnReceive(Context context, Intent intent) {
            owner.OnUsbBroadcastReceived(intent);
        }
    }
}
