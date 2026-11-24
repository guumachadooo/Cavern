using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;

using Cavern.ProAudio;

using Cavernize.Logic.Spatial;

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
    ScarlettGen4ConfigTable gen4ConfigTable;
    ProAudioMicSource configuredMicSource = ProAudioMicSource.PhoneMic;
    bool headphoneMonitoringActive;
    long gainStepEpoch;
    volatile HardwarePreAmpStateSnapshot preAmpSnapshot = HardwarePreAmpStateSnapshot.Empty;
    ushort scarlettSeq = 1;
    bool scarlettInitialized;
    volatile bool pathBOwnsFileDescriptor;
    bool permissionReceiverRegistered;
    bool usbSystemReceiverRegistered;
    bool disposed;

    /// <summary>Raised on the main thread when a USB audio interface attaches, detaches, or gets permission.</summary>
    public event Action<UsbProAudioDeviceSnapshot> DeviceStateChanged;

    /// <summary>Raised outside internal locks whenever the hardware pre-ADC state snapshot changes.</summary>
    public event Action<HardwarePreAmpStateSnapshot> PreAmpSnapshotChanged;

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

    /// <summary>Latest lock-free Focusrite / USB pre-ADC hardware state snapshot.</summary>
    public HardwarePreAmpStateSnapshot CurrentPreAmpSnapshot => preAmpSnapshot;

    /// <summary>Updates the active microphone hardware source policy and publishes an updated pre-ADC snapshot.</summary>
    public void UpdateMicSource(ProAudioMicSource micSource) {
        ProAudioMicSource clean = Enum.IsDefined(typeof(ProAudioMicSource), micSource)
            ? micSource
            : ProAudioMicSource.PhoneMic;
        HardwarePreAmpStateSnapshot prev;
        HardwarePreAmpStateSnapshot next;
        lock (syncLock) {
            if (configuredMicSource == clean) {
                return;
            }
            configuredMicSource = clean;
            prev = preAmpSnapshot;
            next = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
            preAmpSnapshot = next;
        }
        PublishPreAmpSnapshotIfChanged(next, prev);
    }

    /// <summary>Updates whether audio monitoring is routed to headphones/IEMs rather than an open loudspeaker.</summary>
    public void UpdateHeadphoneMonitoring(bool isHeadphoneMonitoring) {
        HardwarePreAmpStateSnapshot prev;
        HardwarePreAmpStateSnapshot next;
        lock (syncLock) {
            if (headphoneMonitoringActive == isHeadphoneMonitoring) {
                return;
            }
            headphoneMonitoringActive = isHeadphoneMonitoring;
            prev = preAmpSnapshot;
            next = BuildPreAmpSnapshotLocked(incrementGainEpoch: false);
            preAmpSnapshot = next;
        }
        PublishPreAmpSnapshotIfChanged(next, prev);
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

        UsbProAudioDeviceSnapshot updated = null;
        HardwarePreAmpStateSnapshot prevPreAmp = null;
        HardwarePreAmpStateSnapshot nextPreAmp = null;

        for (int attempt = 0; attempt < 2; attempt++) {
            UsbDeviceConnection connToClose = null;
            lock (syncLock) {
                UsbDevice best = FindBestAudioUsbDevice();
                if (best == null) {
                    connToClose = DetachConnectionLocked(resetDevice: true);
                    scarlettState = new ScarlettHardwareState();
                    updated = new UsbProAudioDeviceSnapshot();
                    snapshot = updated;
                    prevPreAmp ??= preAmpSnapshot;
                    nextPreAmp = BuildPreAmpSnapshotLocked(incrementGainEpoch: connToClose != null);
                    preAmpSnapshot = nextPreAmp;
                } else {
                    bool sameDevice = IsSameUsbDevice(activeDevice, best);
                    if (!sameDevice) {
                        connToClose = DetachConnectionLocked(resetDevice: false);
                        activeDevice = best;
                        scarlettState = new ScarlettHardwareState();
                    }
                    if (connToClose == null) {
                        bool hasPerm = usbManager.HasPermission(best);
                        prevPreAmp ??= preAmpSnapshot;
                        if (hasPerm && autoOpenIfPermitted && activeConnection == null) {
                            TryOpenActiveDeviceLocked();
                        }
                        nextPreAmp = BuildPreAmpSnapshotLocked(incrementGainEpoch: false);
                        preAmpSnapshot = nextPreAmp;
                        updated = BuildSnapshotLocked(best, hasPerm);
                        snapshot = updated;
                    }
                }
            }
            if (connToClose != null) {
                ReleaseConnectionOutsideLock(connToClose, notifyDetached: true);
                if (updated != null) {
                    break;
                }
                continue;
            }
            break;
        }

        updated ??= new UsbProAudioDeviceSnapshot();
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
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
        HardwarePreAmpStateSnapshot prevPreAmp = null;
        HardwarePreAmpStateSnapshot nextPreAmp = null;
        int fd = -1;
        lock (syncLock) {
            if (activeDevice == null) {
                activeDevice = FindBestAudioUsbDevice();
            }
            if (activeDevice != null && usbManager != null && usbManager.HasPermission(activeDevice)) {
                if (activeConnection == null) {
                    prevPreAmp = preAmpSnapshot;
                    TryOpenActiveDeviceLocked();
                    nextPreAmp = preAmpSnapshot;
                }
                fd = activeConnection != null ? activeConnection.FileDescriptor : -1;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return fd;
    }

    /// <summary>
    /// Initializes Focusrite Scarlett2 host control mode (Step 0 + <c>CMD_INIT_1</c> + <c>CMD_INIT_2</c> +
    /// Gen 4 FCP table verification) on Gen 3/4 devices and optionally disables MSD ("Easy Start") mode so
    /// the interface operates in full UAC2 pro-audio mode.
    /// </summary>
    public bool TryInitializeScarlettControl(bool exitMsdMode = false) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ready;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            ready = EnsureScarlettReadyLocked(out int vendorIf);
            if (ready && exitMsdMode) {
                bool msdWritten = scarlettState.Generation == ScarlettGeneration.Gen4
                    ? (gen4ConfigTable != null && gen4ConfigTable.Msd.IsValid &&
                       WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.Msd, 0, 0))
                    : WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetMsdMode, new byte[] { 0 });
                if (msdWritten) {
                    SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdConfigSave, null, out _);
                }
            }
            nextPreAmp = preAmpSnapshot;
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ready;
    }

    /// <summary>Sets Focusrite Scarlett 48V Phantom Power (shared on Gen 3, independent Ch1/Ch2 on Gen 4).</summary>
    public bool SetScarlettPhantomPower(bool ch1Enabled, bool ch2Enabled) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (gen4ConfigTable == null || !gen4ConfigTable.PhantomPower.IsValid) {
                    nextPreAmp = preAmpSnapshot;
                    ok = false;
                } else {
                    bool hasCh2 = scarlettState.ProductId != Scarlett2Protocol.PidScarlettSoloGen4;
                    bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.PhantomPower, 0, ch1Enabled ? 1 : 0);
                    bool ok2 = !hasCh2 || WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.PhantomPower, 1, ch2Enabled ? 1 : 0);
                    if (ok1) {
                        scarlettState.PhantomPowerCh1 = ch1Enabled;
                        if (!hasCh2) {
                            scarlettState.PhantomPowerCh2 = ch1Enabled;
                        }
                    }
                    if (hasCh2 && ok2) {
                        scarlettState.PhantomPowerCh2 = ch2Enabled;
                    }
                    ok = ok1 && ok2;
                    if (ok1 || (hasCh2 && ok2)) {
                        preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                    }
                    nextPreAmp = preAmpSnapshot;
                }
            } else {
                // Gen 3 2i2 has a single shared 48V phantom switch
                bool shared = ch1Enabled || ch2Enabled;
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetPhantomPower,
                    new byte[] { (byte)(shared ? 1 : 0) });
                if (ok) {
                    scarlettState.PhantomPowerCh1 = shared;
                    scarlettState.PhantomPowerCh2 = shared;
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Sets Focusrite Scarlett Air analog preamp emulation mode per channel.</summary>
    public bool SetScarlettAirMode(ScarlettAirMode ch1, ScarlettAirMode ch2) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (gen4ConfigTable == null || !gen4ConfigTable.AirSwitch.IsValid) {
                    nextPreAmp = preAmpSnapshot;
                    ok = false;
                } else {
                    bool hasCh2 = scarlettState.ProductId != Scarlett2Protocol.PidScarlettSoloGen4;
                    ScarlettAirMode safe1 = (ScarlettAirMode)Math.Clamp((int)ch1, 0, 2);
                    ScarlettAirMode safe2 = (ScarlettAirMode)Math.Clamp((int)ch2, 0, 2);
                    bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AirSwitch, 0, (int)safe1);
                    bool ok2 = !hasCh2 || WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AirSwitch, 1, (int)safe2);
                    if (ok1) {
                        scarlettState.AirCh1 = safe1;
                        if (!hasCh2) {
                            scarlettState.AirCh2 = safe1;
                        }
                    }
                    if (hasCh2 && ok2) {
                        scarlettState.AirCh2 = safe2;
                    }
                    ok = ok1 && ok2;
                    if (ok1 || (hasCh2 && ok2)) {
                        preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                    }
                    nextPreAmp = preAmpSnapshot;
                }
            } else {
                byte b1 = ch1 != ScarlettAirMode.Off ? (byte)1 : (byte)0;
                byte b2 = ch2 != ScarlettAirMode.Off ? (byte)1 : (byte)0;
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetAirSwitch, new byte[] { b1, b2 });
                if (ok) {
                    scarlettState.AirCh1 = ch1 != ScarlettAirMode.Off ? ScarlettAirMode.Presence : ScarlettAirMode.Off;
                    scarlettState.AirCh2 = ch2 != ScarlettAirMode.Off ? ScarlettAirMode.Presence : ScarlettAirMode.Off;
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Sets Focusrite Scarlett Instrument Hi-Z / Line level switch per channel.</summary>
    public bool SetScarlettInstrumentMode(bool instCh1, bool instCh2) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (gen4ConfigTable == null || !gen4ConfigTable.LevelSwitch.IsValid) {
                    nextPreAmp = preAmpSnapshot;
                    ok = false;
                } else {
                    bool hasCh2 = scarlettState.ProductId != Scarlett2Protocol.PidScarlettSoloGen4;
                    bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.LevelSwitch, 0, instCh1 ? 1 : 0);
                    bool ok2 = !hasCh2 || WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.LevelSwitch, 1, instCh2 ? 1 : 0);
                    if (ok1) {
                        scarlettState.InstrumentModeCh1 = instCh1;
                        if (!hasCh2) {
                            scarlettState.InstrumentModeCh2 = instCh1;
                        }
                    }
                    if (hasCh2 && ok2) {
                        scarlettState.InstrumentModeCh2 = instCh2;
                    }
                    ok = ok1 && ok2;
                    if (ok1 || (hasCh2 && ok2)) {
                        preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                    }
                    nextPreAmp = preAmpSnapshot;
                }
            } else {
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetLevelSwitch,
                    new byte[] { (byte)(instCh1 ? 1 : 0), (byte)(instCh2 ? 1 : 0) });
                if (ok) {
                    scarlettState.InstrumentModeCh1 = instCh1;
                    scarlettState.InstrumentModeCh2 = instCh2;
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Sets Focusrite Scarlett Direct Monitor hardware routing (Off / Mono / Stereo).</summary>
    public bool SetScarlettDirectMonitor(ScarlettDirectMonitorMode mode) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (gen4ConfigTable == null || !gen4ConfigTable.DirectMonitor.IsValid) {
                    nextPreAmp = preAmpSnapshot;
                    ok = false;
                } else {
                    ScarlettDirectMonitorMode cleanMode = (ScarlettDirectMonitorMode)Math.Clamp((int)mode, 0, 2);
                    ok = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.DirectMonitor, 0, (int)cleanMode);
                    if (ok) {
                        scarlettState.DirectMonitor = cleanMode;
                        preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                    }
                    nextPreAmp = preAmpSnapshot;
                }
            } else {
                ScarlettDirectMonitorMode cleanMode = (ScarlettDirectMonitorMode)Math.Clamp((int)mode, 0, 2);
                ok = WriteScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetDirectMonitor, new byte[] { (byte)cleanMode });
                if (ok) {
                    scarlettState.DirectMonitor = cleanMode;
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Sets Gen 4 Scarlett 2i2 software-controlled digital preamp gain (0..69 dB) for Ch 1 &amp; Ch 2.</summary>
    public bool SetScarlettGen4PreampGain(float gainCh1Db, float gainCh2Db) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (scarlettState.Generation != ScarlettGeneration.Gen4 ||
                !EnsureScarlettReadyLocked(out int vendorIf) ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.InputGain.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                int g1 = Math.Clamp((int)MathF.Round(gainCh1Db), 0, 69);
                int g2 = Math.Clamp((int)MathF.Round(gainCh2Db), 0, 69);
                bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.InputGain, 0, g1);
                bool ok2 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.InputGain, 1, g2);
                if (ok1) {
                    scarlettState.InputGainCh1Db = g1;
                }
                if (ok2) {
                    scarlettState.InputGainCh2Db = g2;
                }
                ok = ok1 && ok2;
                if (ok1 || ok2) {
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Toggles Gen 4 Scarlett 2i2 Clip Safe automatic preamp overload guard (linked convenience overload).</summary>
    public bool SetScarlettGen4ClipSafe(bool enabled) {
        return SetScarlettGen4ClipSafe(enabled, enabled);
    }

    /// <summary>Toggles Gen 4 Scarlett 2i2/4i4 Clip Safe automatic preamp overload guard independently per channel.</summary>
    public bool SetScarlettGen4ClipSafe(bool ch1Enabled, bool ch2Enabled) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (scarlettState.Generation != ScarlettGeneration.Gen4 ||
                !EnsureScarlettReadyLocked(out int vendorIf) ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.SafeSwitch.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                bool eff2 = scarlettState.InputsLinked ? ch1Enabled : ch2Enabled;
                bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.SafeSwitch, 0, ch1Enabled ? 1 : 0);
                bool ok2 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.SafeSwitch, 1, eff2 ? 1 : 0);
                if (ok1) {
                    scarlettState.ClipSafeCh1 = ch1Enabled;
                }
                if (ok2) {
                    scarlettState.ClipSafeCh2 = eff2;
                }
                ok = ok1 && ok2;
                if (ok1 || ok2) {
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Links or unlinks Scarlett Gen 4 Channels 1 &amp; 2 as a synchronized stereo preamp pair.</summary>
    public bool SetScarlettGen4InputLink(bool linked) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf) ||
                !scarlettState.SupportsStereoLink ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.InputLinkSwitch.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                ok = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.InputLinkSwitch, 0, linked ? 1 : 0);
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
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>
    /// Starts Gen 4 Focusrite Scarlett hardware Auto Gain calibration (10-second listening window).
    /// Pass <paramref name="channelIndex"/> = 0 for Ch 1, 1 for Ch 2, or -1 for both / linked stereo pair.
    /// </summary>
    public bool StartScarlettGen4AutoGain(int channelIndex) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf) ||
                !scarlettState.SupportsAutoGain ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.AutoGainSwitch.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                bool run1 = channelIndex <= 0 || scarlettState.InputsLinked;
                bool run2 = channelIndex == 1 || channelIndex < 0 || scarlettState.InputsLinked;
                bool ok1 = !run1 || WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AutoGainSwitch, 0, 1);
                bool ok2 = !run2 || WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AutoGainSwitch, 1, 1);
                ok = (run1 ? ok1 : true) && (run2 ? ok2 : true);
                if (run1 && ok1) {
                    scarlettState.AutoGainStatusCh1 = ScarlettAutoGainStatus.Running;
                }
                if (run2 && ok2) {
                    scarlettState.AutoGainStatusCh2 = ScarlettAutoGainStatus.Running;
                }
                if ((run1 && ok1) || (run2 && ok2)) {
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Cancels any in-progress Gen 4 Scarlett hardware Auto Gain calibration.</summary>
    public bool CancelScarlettGen4AutoGain() {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf) ||
                !scarlettState.SupportsAutoGain ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.AutoGainSwitch.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                bool ok1 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AutoGainSwitch, 0, 0);
                bool ok2 = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.AutoGainSwitch, 1, 0);
                ok = ok1 && ok2;
                if (ok1 || ok2) {
                    if (scarlettState.AutoGainStatusCh1 == ScarlettAutoGainStatus.Running) {
                        scarlettState.AutoGainStatusCh1 = ScarlettAutoGainStatus.Idle;
                    }
                    if (scarlettState.AutoGainStatusCh2 == ScarlettAutoGainStatus.Running) {
                        scarlettState.AutoGainStatusCh2 = ScarlettAutoGainStatus.Idle;
                    }
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Polls Gen 4 Auto Gain status and reads back the resulting preamp gains when finished.</summary>
    public bool PollScarlettGen4AutoGainStatus(out ScarlettAutoGainStatus ch1Status, out ScarlettAutoGainStatus ch2Status) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            ch1Status = scarlettState.AutoGainStatusCh1;
            ch2Status = scarlettState.AutoGainStatusCh2;
            if (!EnsureScarlettReadyLocked(out int vendorIf) ||
                !scarlettState.SupportsAutoGain ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.AutoGainStatus.IsValid) {
                nextPreAmp = preAmpSnapshot;
                ok = false;
            } else {
                ScarlettAutoGainStatus old1 = scarlettState.AutoGainStatusCh1;
                ScarlettAutoGainStatus old2 = scarlettState.AutoGainStatusCh2;
                float oldGain1 = scarlettState.InputGainCh1Db;
                float oldGain2 = scarlettState.InputGainCh2Db;

                bool r1 = ReadScarlettConfigItemLocked(vendorIf, gen4ConfigTable.AutoGainStatus, 0, out int s1);
                bool r2 = ReadScarlettConfigItemLocked(vendorIf, gen4ConfigTable.AutoGainStatus, 1, out int s2);
                if (r1) {
                    ch1Status = (ScarlettAutoGainStatus)Math.Clamp(s1, 0, 3);
                    scarlettState.AutoGainStatusCh1 = ch1Status;
                }
                if (r2) {
                    ch2Status = (ScarlettAutoGainStatus)Math.Clamp(s2, 0, 3);
                    scarlettState.AutoGainStatusCh2 = ch2Status;
                }
                ok = r1 || r2;
                if (ok &&
                    (ch1Status == ScarlettAutoGainStatus.Succeeded ||
                     ch2Status == ScarlettAutoGainStatus.Succeeded ||
                     old1 == ScarlettAutoGainStatus.Running ||
                     old2 == ScarlettAutoGainStatus.Running) &&
                    gen4ConfigTable.InputGain.IsValid) {
                    if (ReadScarlettConfigItemLocked(vendorIf, gen4ConfigTable.InputGain, 0, out int g1)) {
                        scarlettState.InputGainCh1Db = Math.Clamp(g1, 0, 69);
                    }
                    if (ReadScarlettConfigItemLocked(vendorIf, gen4ConfigTable.InputGain, 1, out int g2)) {
                        scarlettState.InputGainCh2Db = Math.Clamp(g2, 0, 69);
                    }
                }
                bool changed = old1 != ch1Status ||
                               old2 != ch2Status ||
                               !oldGain1.Equals(scarlettState.InputGainCh1Db) ||
                               !oldGain2.Equals(scarlettState.InputGainCh2Db);
                if (changed) {
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
    }

    /// <summary>Sets Direct Monitor Input vs Playback blend percentage (0..100%) on Gen 4 interfaces.</summary>
    public bool SetScarlettDirectMonitorMix(int mixPct) {
        lock (syncLock) {
            if (scarlettState.Generation != ScarlettGeneration.Gen4 ||
                !EnsureScarlettReadyLocked(out int vendorIf) ||
                gen4ConfigTable == null ||
                !gen4ConfigTable.DirectMonitorGain.IsValid) {
                return false;
            }
            int clamped = Math.Clamp(mixPct, 0, 100);
            // Map 0% (100% Input) -> 0 dB (0), 100% (100% Playback) -> -80 dB (-160 in 0.5 dB mixer steps)
            short gainHalfDb = clamped >= 99
                ? (short)-160
                : (short)Math.Clamp((int)MathF.Round(-160f * (clamped / 100f) * (clamped / 100f)), -160, 0);
            bool ok = WriteScarlettConfigItemLocked(vendorIf, gen4ConfigTable, gen4ConfigTable.DirectMonitorGain, 0, gainHalfDb);
            if (ok) {
                scarlettState.DirectMonitorMixPct = clamped;
            }
            return ok;
        }
    }

    /// <summary>Sets main monitor output Mute and Dim (-20 dB) switches when supported by the verified device table.</summary>
    public bool SetScarlettOutputMuteDim(bool mute, bool dim) {
        lock (syncLock) {
            // Verified Scarlett 2i2/Solo/4i4 Gen 4 FCP config tables do not expose software output mute/dim offsets;
            // never write unverified fallback offsets (0x3C/0x3E collide with LevelSwitch/AirSwitch on 2i2 Gen 4).
            return false;
        }
    }

    /// <summary>Enables or disables virtual Loopback routing when supported by the verified device table.</summary>
    public bool SetScarlettLoopback(bool enabled) {
        lock (syncLock) {
            // Verified Gen 4 config tables route Loopback via mixer mux rather than a single NVRAM flag offset.
            return false;
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
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool anyOk;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            if (!EnsureScarlettReadyLocked(out int vendorIf)) {
                nextPreAmp = preAmpSnapshot;
                anyOk = false;
            } else {
                anyOk = ReadScarlettHardwareStateLocked(vendorIf);
                if (anyOk) {
                    preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: false);
                }
                nextPreAmp = preAmpSnapshot;
            }
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return anyOk;
    }

    bool ReadScarlettHardwareStateLocked(int vendorIf) {
        bool anyOk = false;
        if (scarlettState.Generation == ScarlettGeneration.Gen4) {
            ScarlettGen4ConfigTable table = gen4ConfigTable;
            if (table == null || !scarlettState.IsGen4FcpControlTableVerified) {
                return false;
            }
            bool hasCh2 = scarlettState.ProductId != Scarlett2Protocol.PidScarlettSoloGen4;

            if (table.PhantomPower.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.PhantomPower, 0, out int p1)) {
                scarlettState.PhantomPowerCh1 = p1 != 0;
                if (!hasCh2) {
                    scarlettState.PhantomPowerCh2 = scarlettState.PhantomPowerCh1;
                }
                anyOk = true;
            }
            if (hasCh2 && table.PhantomPower.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.PhantomPower, 1, out int p2)) {
                scarlettState.PhantomPowerCh2 = p2 != 0;
                anyOk = true;
            }

            if (table.AirSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.AirSwitch, 0, out int a1)) {
                scarlettState.AirCh1 = (ScarlettAirMode)Math.Clamp(a1, 0, 2);
                if (!hasCh2) {
                    scarlettState.AirCh2 = scarlettState.AirCh1;
                }
                anyOk = true;
            }
            if (hasCh2 && table.AirSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.AirSwitch, 1, out int a2)) {
                scarlettState.AirCh2 = (ScarlettAirMode)Math.Clamp(a2, 0, 2);
                anyOk = true;
            }

            if (table.LevelSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.LevelSwitch, 0, out int l1)) {
                scarlettState.InstrumentModeCh1 = l1 != 0;
                if (!hasCh2) {
                    scarlettState.InstrumentModeCh2 = scarlettState.InstrumentModeCh1;
                }
                anyOk = true;
            }
            if (hasCh2 && table.LevelSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.LevelSwitch, 1, out int l2)) {
                scarlettState.InstrumentModeCh2 = l2 != 0;
                anyOk = true;
            }

            if (table.InputGain.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.InputGain, 0, out int g1)) {
                scarlettState.InputGainCh1Db = Math.Clamp(g1, 0, 69);
                anyOk = true;
            }
            if (hasCh2 && table.InputGain.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.InputGain, 1, out int g2)) {
                scarlettState.InputGainCh2Db = Math.Clamp(g2, 0, 69);
                anyOk = true;
            }

            if (table.SafeSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.SafeSwitch, 0, out int s1)) {
                scarlettState.ClipSafeCh1 = s1 != 0;
                anyOk = true;
            }
            if (hasCh2 && table.SafeSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.SafeSwitch, 1, out int s2)) {
                scarlettState.ClipSafeCh2 = s2 != 0;
                anyOk = true;
            }

            if (table.InputLinkSwitch.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.InputLinkSwitch, 0, out int lnk)) {
                scarlettState.InputsLinked = lnk != 0;
                anyOk = true;
            }

            if (table.DirectMonitor.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.DirectMonitor, 0, out int dm)) {
                scarlettState.DirectMonitor = (ScarlettDirectMonitorMode)Math.Clamp(dm, 0, 2);
                anyOk = true;
            }

            if (table.DirectMonitorGain.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.DirectMonitorGain, 0, out int dmGain)) {
                if (dmGain <= 0 && dmGain >= -160) {
                    double frac = Math.Sqrt(Math.Clamp(-dmGain / 160.0, 0.0, 1.0));
                    scarlettState.DirectMonitorMixPct = Math.Clamp((int)Math.Round(frac * 100.0), 0, 100);
                } else if (dmGain > 0 && dmGain <= 100) {
                    scarlettState.DirectMonitorMixPct = dmGain;
                }
                anyOk = true;
            }

            if (table.AutoGainStatus.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.AutoGainStatus, 0, out int ag1)) {
                scarlettState.AutoGainStatusCh1 = (ScarlettAutoGainStatus)Math.Clamp(ag1, 0, 3);
                anyOk = true;
            }
            if (hasCh2 && table.AutoGainStatus.IsValid && ReadScarlettConfigItemLocked(vendorIf, table.AutoGainStatus, 1, out int ag2)) {
                scarlettState.AutoGainStatusCh2 = (ScarlettAutoGainStatus)Math.Clamp(ag2, 0, 3);
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
            if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetLevelSwitch, 2, out off, out len) && len >= 2) {
                scarlettState.InstrumentModeCh1 = scarlettRxScratch[off] != 0;
                scarlettState.InstrumentModeCh2 = scarlettRxScratch[off + 1] != 0;
                anyOk = true;
            }
            if (ReadScarlettConfigLocked(vendorIf, Scarlett2Protocol.Gen3OffsetDirectMonitor, 1, out off, out len) && len >= 1) {
                scarlettState.DirectMonitor = (ScarlettDirectMonitorMode)Math.Clamp((int)scarlettRxScratch[off], 0, 2);
                anyOk = true;
            }
        }
        return anyOk;
    }

    /// <summary>
    /// Polls Focusrite Scarlett hardware input peak meters via <see cref="Scarlett2Protocol.CmdGetMeter"/>
    /// and updates <see cref="ScarlettHardwareState.InputMeterCh1Dbfs"/> / <see cref="ScarlettHardwareState.InputMeterCh2Dbfs"/>.
    /// </summary>
    public bool PollScarlettHardwareMeters(out float ch1Dbfs, out float ch2Dbfs) {
        HardwarePreAmpStateSnapshot prevPreAmp;
        HardwarePreAmpStateSnapshot nextPreAmp;
        bool ok = false;
        lock (syncLock) {
            prevPreAmp = preAmpSnapshot;
            ch1Dbfs = scarlettState.InputMeterCh1Dbfs;
            ch2Dbfs = scarlettState.InputMeterCh2Dbfs;
            if (scarlettState.SupportsMeteringAndGainAssist && EnsureScarlettHandshakeLocked(out int vendorIf)) {
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
                    ok = count > 0;
                }
            }
            nextPreAmp = preAmpSnapshot;
        }
        PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
        return ok;
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
        if (!EnsureScarlettHandshakeLocked(out vendorIf)) {
            return false;
        }
        return scarlettState.SupportsSoftwareControls;
    }

    bool EnsureScarlettHandshakeLocked(out int vendorIf) {
        vendorIf = snapshot.Topology?.VendorControlInterface ?? -1;
        if (vendorIf < 0 ||
            (scarlettState.Generation != ScarlettGeneration.Gen4 && !scarlettState.SupportsSoftwareControls)) {
            return false;
        }
        if (!scarlettInitialized) {
            if (!SendScarlettInitStep0Locked(vendorIf)) {
                return false;
            }
            Thread.Sleep(20);
            if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit1, null, out _)) {
                return false;
            }
            if (!SendScarlettCommandLocked(vendorIf, Scarlett2Protocol.CmdInit2, null, out int init2PayloadLen)) {
                return false;
            }
            int init2TotalLen = Scarlett2Protocol.PacketHeaderBytes + init2PayloadLen;
            if (Scarlett2Protocol.TryExtractFirmwareVersion(scarlettRxScratch, init2TotalLen, out int fw)) {
                scarlettState.FirmwareVersion = fw;
            }
            scarlettInitialized = true;

            if (scarlettState.Generation == ScarlettGeneration.Gen4) {
                if (Scarlett2Protocol.TryResolveGen4ConfigTable(scarlettState.ProductId, scarlettState.FirmwareVersion, out ScarlettGen4ConfigTable table)) {
                    bool airOk = table.AirSwitch.IsValid &&
                                 ReadScarlettConfigItemLocked(vendorIf, table.AirSwitch, 0, out int air0) &&
                                 air0 >= 0 && air0 <= 2;
                    bool gainOk = !table.InputGain.IsValid ||
                                  (ReadScarlettConfigItemLocked(vendorIf, table.InputGain, 0, out int gain0) &&
                                   gain0 >= 0 && gain0 <= 69);
                    if (airOk && gainOk) {
                        gen4ConfigTable = table;
                        scarlettState.IsGen4FcpControlTableVerified = true;
                        ReadScarlettHardwareStateLocked(vendorIf);
                        preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                        AppLogger.Info(
                            $"Scarlett Gen 4 FCP verified: {scarlettState.ModelName} " +
                            $"(PID=0x{scarlettState.ProductId:X4}, FW={scarlettState.FirmwareVersion}, ParamBuf=0x{table.ParamBufAddr:X})");
                    } else {
                        gen4ConfigTable = null;
                        scarlettState.IsGen4FcpControlTableVerified = false;
                        AppLogger.Warn(
                            $"Scarlett Gen 4 FCP sanity GET_DATA check failed (FW={scarlettState.FirmwareVersion}); " +
                            "keeping controls safely locked.");
                    }
                } else {
                    gen4ConfigTable = null;
                    scarlettState.IsGen4FcpControlTableVerified = false;
                    AppLogger.Info(
                        $"Scarlett Gen 4 FCP table unavailable for PID=0x{scarlettState.ProductId:X4} " +
                        $"FW={scarlettState.FirmwareVersion}; metering & Gain Assist remain active.");
                }
            } else {
                ReadScarlettHardwareStateLocked(vendorIf);
                preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
            }
        }
        return true;
    }

    bool ReadScarlettConfigItemLocked(int vendorIf, ScarlettConfigItem item, int channelIndex, out int value) {
        value = 0;
        if (!item.IsValid) {
            return false;
        }
        int sizeBytes = item.SizeBytes;
        if (sizeBytes <= 0 || sizeBytes > 2) {
            return false;
        }
        uint addr = item.Offset + (uint)Math.Max(0, channelIndex) * (uint)sizeBytes;
        if (!ReadScarlettConfigLocked(vendorIf, addr, (uint)sizeBytes, out int off, out int len) || len < sizeBytes) {
            return false;
        }
        if (sizeBytes == 1) {
            value = scarlettRxScratch[off];
        } else {
            value = (short)(scarlettRxScratch[off] | (scarlettRxScratch[off + 1] << 8));
        }
        return true;
    }

    bool WriteScarlettConfigItemLocked(
        int vendorIf,
        ScarlettGen4ConfigTable table,
        ScarlettConfigItem item,
        int channelIndex,
        int value) {
        if (table == null || !item.IsValid) {
            return false;
        }
        int sizeBytes = item.SizeBytes;
        if (sizeBytes <= 0 || sizeBytes > 2) {
            return false;
        }
        byte[] valBytes = sizeBytes == 1
            ? new byte[] { (byte)(value & 0xFF) }
            : new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

        if (item.UseParamBuf) {
            uint pbuf = table.ParamBufAddr;
            byte idx = (byte)Math.Clamp(channelIndex, 0, 255);
            if (!WriteScarlettConfigLocked(vendorIf, pbuf + 1u, new byte[] { idx })) {
                return false;
            }
            if (!WriteScarlettConfigLocked(vendorIf, pbuf, valBytes)) {
                return false;
            }
        } else {
            uint addr = item.Offset + (uint)Math.Max(0, channelIndex) * (uint)sizeBytes;
            if (!WriteScarlettConfigLocked(vendorIf, addr, valBytes)) {
                return false;
            }
        }

        if (item.ActivateCmd != 0) {
            return SendActivateConfigLocked(vendorIf, (uint)item.ActivateCmd);
        }
        return true;
    }

    bool SendActivateConfigLocked(int vendorIf, uint activateCmd) {
        ushort seq = NextScarlettSeqLocked();
        byte[] tx = Scarlett2Protocol.BuildActivateConfigPacket(seq, activateCmd);
        int rxLen = TransferScarlettPacketLocked(vendorIf, tx, scarlettRxScratch);
        return rxLen >= Scarlett2Protocol.PacketHeaderBytes &&
               Scarlett2Protocol.TryParseResponseHeader(scarlettRxScratch, rxLen, Scarlett2Protocol.CmdDataActivate, seq, out _, out _);
    }

    bool SendScarlettInitStep0Locked(int vendorIf) {
        if (IsPathBTransportActive()) {
            int rxBytes = NativeProAudioBridge.Scarlett2InitStep0(vendorIf, scarlettRxScratch, 24);
            if (rxBytes < 0) {
                AppLogger.Warn($"Scarlett2InitStep0 (Path B) returned {rxBytes}");
                return false;
            }
            return true;
        }
        if (activeConnection == null || activeDevice == null) {
            return false;
        }
        UsbInterface vendorUsbIf = FindInterfaceById(activeDevice, vendorIf);
        bool claimed = false;
        try {
            if (vendorUsbIf != null) {
                claimed = activeConnection.ClaimInterface(vendorUsbIf, true);
            }
            int rxBytes = activeConnection.ControlTransfer(
                (UsbAddressing)Uac2Protocol.ReqTypeClassInterfaceIn,
                Scarlett2Protocol.UsbReqCmdInit,
                0,
                vendorIf,
                scarlettRxScratch,
                24,
                250);
            if (rxBytes < 0) {
                AppLogger.Warn($"Scarlett2InitStep0 (EP0) returned {rxBytes}");
                return false;
            }
            return true;
        } catch (Exception ex) {
            AppLogger.Warn($"Scarlett2InitStep0 ControlTransfer error: {ex.Message}");
            return false;
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

    bool IsPathBTransportActive() =>
        pathBOwnsFileDescriptor ||
        (NativeProAudioBridge.TryGetStatus(out NativeProAudioStatus st) &&
         st.ActiveBackend == NativeProAudioBridge.BackendPathBLibUsb);

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
        if (IsPathBTransportActive()) {
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
        UsbDeviceConnection conn = null;
        try {
            conn = usbManager.OpenDevice(activeDevice);
            if (conn == null) {
                AppLogger.Warn($"UsbProAudioDeviceManager: OpenDevice returned null for {FormatDeviceName(activeDevice)}");
                return;
            }
            byte[] rawDesc = conn.GetRawDescriptors();
            Uac2DeviceTopology topo = Uac2Protocol.ParseConfigurationDescriptor(rawDesc);

            int[] rates = Array.Empty<int>();
            if (topo.IsUac2 && topo.ClockSourceId != 0) {
                rates = QueryUac2ClockSampleRatesLocked(conn, activeDevice, topo);
            }

            ScarlettGeneration gen = Scarlett2Protocol.IdentifyDevice(activeDevice.VendorId, activeDevice.ProductId, out string scarlettModel);
            activeConnection = conn;
            conn = null;
            scarlettInitialized = false;
            gen4ConfigTable = null;
            scarlettState = new ScarlettHardwareState {
                Generation = gen,
                ModelName = scarlettModel,
                ProductId = activeDevice.ProductId
            };

            snapshot = new UsbProAudioDeviceSnapshot {
                IsAttached = true,
                HasPermission = true,
                IsUac2 = topo.IsUac2,
                SupportsFullDuplex = topo.SupportsFullDuplex,
                VendorId = activeDevice.VendorId,
                ProductId = activeDevice.ProductId,
                DisplayName = string.IsNullOrEmpty(scarlettModel) ? FormatDeviceName(activeDevice) : scarlettModel,
                DevicePath = activeDevice.DeviceName ?? string.Empty,
                FileDescriptor = activeConnection.FileDescriptor,
                Topology = topo,
                ScarlettGeneration = gen,
                SupportedSampleRates = rates
            };

            if ((gen == ScarlettGeneration.Gen3 || gen == ScarlettGeneration.Gen4) && topo.VendorControlInterface >= 0) {
                try {
                    EnsureScarlettHandshakeLocked(out _);
                } catch (Exception initEx) {
                    AppLogger.Warn($"Scarlett initial handshake deferred: {initEx.Message}");
                }
            }
            if (!scarlettInitialized) {
                preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
            }

            AppLogger.Info(
                $"USB Pro-Audio opened: '{snapshot.DisplayName}' (VID=0x{snapshot.VendorId:X4}, PID=0x{snapshot.ProductId:X4}, " +
                $"UAC2={topo.IsUac2}, Duplex={topo.SupportsFullDuplex}, ClockID={topo.ClockSourceId}, " +
                $"VendorIf={topo.VendorControlInterface}, Scarlett={gen}, FW={scarlettState.FirmwareVersion}, " +
                $"Gen4FcpVerified={scarlettState.IsGen4FcpControlTableVerified}, Rates=[{string.Join(",", rates)}], FD={activeConnection.FileDescriptor})");
        } catch (Exception ex) {
            AppLogger.Error("UsbProAudioDeviceManager.TryOpenActiveDeviceLocked", ex);
            if (conn != null) {
                CloseRawConnectionSafely(conn);
            }
            if (activeConnection != null) {
                CloseRawConnectionSafely(activeConnection);
                activeConnection = null;
            }
            pathBOwnsFileDescriptor = false;
            scarlettInitialized = false;
            gen4ConfigTable = null;
        }
    }

    int[] QueryUac2ClockSampleRatesLocked(UsbDeviceConnection conn, UsbDevice dev, Uac2DeviceTopology topo) {
        UsbInterface acIf = FindInterfaceById(dev, topo.AudioControlInterface);
        bool claimed = false;
        try {
            if (acIf != null) {
                claimed = conn.ClaimInterface(acIf, true);
            }
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
        } finally {
            if (claimed && acIf != null) {
                try {
                    conn.ReleaseInterface(acIf);
                } catch {
                    // Ignore release failures on abrupt USB disconnect
                }
            }
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

    static bool IsSameUsbDevice(UsbDevice a, UsbDevice b) {
        if (a == null || b == null) {
            return false;
        }
        return a.DeviceId == b.DeviceId &&
               a.VendorId == b.VendorId &&
               a.ProductId == b.ProductId;
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
        } else if (action == UsbManager.ActionUsbDeviceAttached) {
            ScanAttachedDevices(autoOpenIfPermitted: true);
        } else if (action == UsbManager.ActionUsbDeviceDetached) {
            UsbDevice detachedDev;
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
                detachedDev = intent.GetParcelableExtra(UsbManager.ExtraDevice, Java.Lang.Class.FromType(typeof(UsbDevice))) as UsbDevice;
            } else {
#pragma warning disable CS0618, CA1422
                detachedDev = intent.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
#pragma warning restore CS0618, CA1422
            }

            bool matchesActive;
            lock (syncLock) {
                matchesActive = detachedDev == null
                    ? activeDevice != null || activeConnection != null
                    : (IsSameUsbDevice(activeDevice, detachedDev) ||
                       (activeDevice != null &&
                        activeDevice.VendorId == detachedDev.VendorId &&
                        activeDevice.ProductId == detachedDev.ProductId &&
                        string.Equals(activeDevice.DeviceName, detachedDev.DeviceName, StringComparison.Ordinal)));
            }

            if (matchesActive) {
                // Break syncLock <-> lifecycleLock AB-BA deadlock: notify native Path B lock-free FIRST,
                // snapshot & clear connection under syncLock, RELEASE syncLock, then fire BeforeUsbConnectionClosing!
                NativeProAudioBridge.NotifyUsbDetached();
                UsbDeviceConnection connToClose;
                HardwarePreAmpStateSnapshot prevPreAmp;
                HardwarePreAmpStateSnapshot nextPreAmp;
                lock (syncLock) {
                    connToClose = DetachConnectionLocked(resetDevice: true);
                    scarlettState = new ScarlettHardwareState();
                    snapshot = new UsbProAudioDeviceSnapshot();
                    prevPreAmp = preAmpSnapshot;
                    nextPreAmp = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
                    preAmpSnapshot = nextPreAmp;
                }
                if (connToClose != null) {
                    InvokeBeforeUsbConnectionClosingSafely();
                    CloseRawConnectionSafely(connToClose);
                }
                PublishPreAmpSnapshotIfChanged(nextPreAmp, prevPreAmp);
            }
            ScanAttachedDevices(autoOpenIfPermitted: true);
        }
    }

    HardwarePreAmpStateSnapshot BuildPreAmpSnapshotLocked(bool incrementGainEpoch) {
        if (incrementGainEpoch) {
            gainStepEpoch++;
        }
        bool verified = scarlettState.Generation == ScarlettGeneration.Gen3 ||
                        (scarlettState.Generation == ScarlettGeneration.Gen4 && scarlettState.IsGen4FcpControlTableVerified);
        ScarlettAirMode air = scarlettState.AirCh1 != ScarlettAirMode.Off ? scarlettState.AirCh1 : scarlettState.AirCh2;
        bool driveActive = scarlettState.AirCh1 == ScarlettAirMode.PresenceAndDrive ||
                           scarlettState.AirCh2 == ScarlettAirMode.PresenceAndDrive;
        bool autoGainActive = scarlettState.AutoGainStatusCh1 == ScarlettAutoGainStatus.Running ||
                              scarlettState.AutoGainStatusCh2 == ScarlettAutoGainStatus.Running;
        bool clipSafeActive = scarlettState.ClipSafeCh1 || scarlettState.ClipSafeCh2;
        float gainDb = scarlettState.Generation == ScarlettGeneration.Gen4 && scarlettState.SupportsIndependentPreampGain
            ? scarlettState.InputGainCh1Db
            : 0f;
        return new HardwarePreAmpStateSnapshot(
            airMode: air,
            driveActive: driveActive,
            autoGainActive: autoGainActive,
            clipSafeActive: clipSafeActive,
            preampGainDb: gainDb,
            gainStepEpoch: gainStepEpoch,
            isHeadphoneMonitoring: headphoneMonitoringActive,
            micSource: (int)configuredMicSource,
            isHardwareVerified: verified);
    }

    void PublishPreAmpSnapshotIfChanged(HardwarePreAmpStateSnapshot nextSnapshot, HardwarePreAmpStateSnapshot prevSnapshot) {
        if (nextSnapshot != null && !nextSnapshot.Equals(prevSnapshot)) {
            try {
                PreAmpSnapshotChanged?.Invoke(nextSnapshot);
            } catch (Exception ex) {
                AppLogger.Warn($"UsbProAudioDeviceManager.PreAmpSnapshotChanged: {ex.Message}");
            }
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
    /// Raised synchronously outside <c>syncLock</c> immediately BEFORE <c>UsbDeviceConnection.Close()</c> closes
    /// the native USB file descriptor. Allows Path B (<c>libusb-1.0</c>) to stop its event thread and release the FD
    /// cleanly, preventing Android <c>fdsan</c> <c>SIGABRT</c> and <c>syncLock &lt;-&gt; lifecycleLock</c> deadlocks.
    /// </summary>
    public event Action BeforeUsbConnectionClosing;

    UsbDeviceConnection DetachConnectionLocked(bool resetDevice) {
        UsbDeviceConnection conn = activeConnection;
        activeConnection = null;
        pathBOwnsFileDescriptor = false;
        scarlettInitialized = false;
        gen4ConfigTable = null;
        if (resetDevice) {
            activeDevice = null;
        }
        return conn;
    }

    void ReleaseConnectionOutsideLock(UsbDeviceConnection connToClose, bool notifyDetached) {
        if (notifyDetached) {
            NativeProAudioBridge.NotifyUsbDetached();
        }
        if (connToClose != null) {
            InvokeBeforeUsbConnectionClosingSafely();
            CloseRawConnectionSafely(connToClose);
        }
    }

    void InvokeBeforeUsbConnectionClosingSafely() {
        try {
            BeforeUsbConnectionClosing?.Invoke();
        } catch (Exception ex) {
            AppLogger.Warn($"UsbProAudioDeviceManager: BeforeUsbConnectionClosing listener error: {ex.Message}");
        }
    }

    static void CloseRawConnectionSafely(UsbDeviceConnection conn) {
        if (conn == null) {
            return;
        }
        try {
            conn.Close();
            conn.Dispose();
        } catch {
            // Ignore close errors on already-detached USB descriptors
        }
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
        UsbDeviceConnection connToClose;
        lock (syncLock) {
            connToClose = DetachConnectionLocked(resetDevice: true);
            scarlettState = new ScarlettHardwareState();
            snapshot = new UsbProAudioDeviceSnapshot();
            preAmpSnapshot = BuildPreAmpSnapshotLocked(incrementGainEpoch: true);
        }
        ReleaseConnectionOutsideLock(connToClose, notifyDetached: connToClose != null);
        GC.SuppressFinalize(this);
    }

    sealed class UsbBroadcastReceiver(UsbProAudioDeviceManager owner) : BroadcastReceiver {
        public override void OnReceive(Context context, Intent intent) {
            owner.OnUsbBroadcastReceived(intent);
        }
    }
}
