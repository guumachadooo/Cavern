using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

using Android;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.Content.PM;

using Cavern.Spatial;

using Cavernize.Logic.Spatial;

using CavernizeAndroid.Language;

using Java.Util;

namespace CavernizeAndroid.Services;

/// <summary>
/// Slot 4 external head-tracking provider supporting both local UDP Open Sound Control (OSC 1.0 + <c>#bundle</c>)
/// streams (IEM SceneRotator, SPARTA, Supperware, OpenTrack, Mr. Headtracker Bridge) and Bluetooth Low Energy (BLE)
/// GATT Nordic UART / head-tracker notifications. Supports both 3DoF orientation and 6DoF <c>/pose</c> streams.
/// </summary>
public sealed class BleOscHeadTrackerProvider : IHeadTrackerProvider {
    static readonly UUID NordicUartServiceUuid = UUID.FromString("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly UUID NordicUartTxCharUuid = UUID.FromString("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    static readonly UUID ClientCharConfigUuid = UUID.FromString("00002902-0000-1000-8000-00805f9b34fb");

    readonly Context context;
    readonly HeadPosePredictor predictor = new();
    readonly object lifecycleLock = new();

    CancellationTokenSource cts;
    Thread udpThread;
    UdpClient udpClient;

    BluetoothLeScanner bleScanner;
    BleScanCallbackImpl bleScanCallback;
    BluetoothGatt activeGatt;
    BleGattCallbackImpl bleGattCallback;

    volatile bool running;
    volatile bool hasRecentPackets;
    volatile bool supports6DoF;
    volatile bool bleConnected;
    volatile bool disposed;
    long lastPacketMs;
    int listenPort = 9000;
    string connectedPeerLabel = string.Empty;
    string statusDetail = string.Empty;

    /// <inheritdoc/>
    public string ProviderId => "ble-osc";

    /// <inheritdoc/>
    public string DisplayName => !string.IsNullOrEmpty(connectedPeerLabel)
        ? connectedPeerLabel
        : $"BLE / OSC (UDP :{listenPort})";

    /// <inheritdoc/>
    public bool IsConnected => running && (bleConnected || (hasRecentPackets && Environment.TickCount64 - Volatile.Read(ref lastPacketMs) < 3500));

    /// <inheritdoc/>
    public bool Supports6DoF => supports6DoF;

    /// <summary>UDP port bound for incoming OSC 1.0 packets (default 9000).</summary>
    public int ListenPort {
        get => listenPort;
        set => listenPort = Math.Clamp(value, 1024, 65535);
    }

    /// <summary>Latest diagnostic status message for UI display.</summary>
    public string StatusDetail => statusDetail;

    /// <summary>Controls slow gaze-drift auto-recentering inside <see cref="HeadPosePredictor"/>.</summary>
    public bool AutoRecenterEnabled {
        get => predictor.AutoRecenterEnabled;
        set => predictor.AutoRecenterEnabled = value;
    }

    /// <summary>Suppresses head roll tilt when enabled.</summary>
    public bool LockRoll {
        get => predictor.LockRoll;
        set => predictor.LockRoll = value;
    }

    /// <summary>Raised whenever connection state or diagnostic status changes.</summary>
    public event Action StatusChanged;

    public BleOscHeadTrackerProvider(Context context) => this.context = context.ApplicationContext ?? context;

    /// <inheritdoc/>
    public void Start() {
        lock (lifecycleLock) {
            if (disposed || running) {
                return;
            }
            running = true;
            hasRecentPackets = false;
            supports6DoF = false;
            bleConnected = false;
            connectedPeerLabel = string.Empty;
            predictor.Reset();

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            udpThread = new Thread(() => RunUdpOscLoop(token)) {
                Name = "CavernizeOscHT",
                IsBackground = true
            };
            udpThread.Start();

            TryStartBleScanLocked();
        }
        SetStatus(string.Format(AndroidStrings.Active["HtBleOscListening"], listenPort));
    }

    /// <inheritdoc/>
    public void Stop() {
        Thread toJoin;
        UdpClient clientToClose;
        lock (lifecycleLock) {
            running = false;
            cts?.Cancel();
            clientToClose = udpClient;
            udpClient = null;
            toJoin = udpThread;
            udpThread = null;
            StopBleLocked();
        }

        try {
            clientToClose?.Close();
            clientToClose?.Dispose();
        } catch {
            // Ignore
        }

        if (toJoin != null && toJoin.IsAlive && toJoin != Thread.CurrentThread) {
            toJoin.Join(1000);
        }

        lock (lifecycleLock) {
            cts?.Dispose();
            cts = null;
        }

        hasRecentPackets = false;
        bleConnected = false;
        connectedPeerLabel = string.Empty;
        SetStatus(string.Empty);
    }

    /// <inheritdoc/>
    public void Recenter() => predictor.Recenter();

    /// <inheritdoc/>
    public ListenerPose6DoF GetCurrentPose(float predictionHorizonMs = 0f) =>
        predictor.PredictPose(predictionHorizonMs, IsConnected);

    void RunUdpOscLoop(CancellationToken token) {
        try {
            UdpClient client = new(listenPort);
            client.Client.ReceiveTimeout = 500;
            lock (lifecycleLock) {
                if (token.IsCancellationRequested) {
                    client.Dispose();
                    return;
                }
                udpClient = client;
            }

            IPEndPoint remote = new(IPAddress.Any, 0);
            AppLogger.Info($"BleOscHeadTracker listening for OSC packets on UDP :{listenPort}.");

            while (!token.IsCancellationRequested) {
                try {
                    byte[] data = client.Receive(ref remote);
                    if (data == null || data.Length == 0) {
                        continue;
                    }
                    if (OscPacketParser.TryParseOsc(data, out OscHeadPoseMessage msg) && msg.IsValid) {
                        IngestPoseMessage(msg, $"OSC ({remote.Address}:{listenPort})");
                    }
                } catch (SocketException sx) when (sx.SocketErrorCode == SocketError.TimedOut) {
                    // Normal polling timeout
                } catch (ObjectDisposedException) {
                    break;
                } catch (SocketException) {
                    if (token.IsCancellationRequested) {
                        break;
                    }
                }
            }
        } catch (Exception ex) {
            if (!token.IsCancellationRequested) {
                AppLogger.Warn($"BleOscHeadTracker UDP bind failed on :{listenPort}: {ex.Message}");
                SetStatus(string.Format(AndroidStrings.Active["HtBleOscUdpBusy"], listenPort, ex.Message));
            }
        }
    }

    void IngestPoseMessage(OscHeadPoseMessage msg, string peerLabel) {
        bool wasLive = hasRecentPackets;
        if (msg.HasPosition) {
            supports6DoF = true;
        }
        predictor.PushSample(msg.Orientation, null, msg.Position, Stopwatch.GetTimestamp());
        Volatile.Write(ref lastPacketMs, Environment.TickCount64);
        hasRecentPackets = true;
        if (!wasLive || !string.Equals(connectedPeerLabel, peerLabel, StringComparison.Ordinal)) {
            connectedPeerLabel = peerLabel;
            SetStatus(string.Format(AndroidStrings.Active["HtBleOscLive"], peerLabel, supports6DoF ? " (6DoF)" : string.Empty));
        }
    }

    void TryStartBleScanLocked() {
        if (!HasBlePermissions()) {
            AppLogger.Info("BleOscHeadTracker BLE scan skipped: required Bluetooth/Location permission not granted.");
            return;
        }
        try {
            if (bleScanner != null && bleScanCallback != null) {
                try {
                    bleScanner.StopScan(bleScanCallback);
                } catch {
                    // Ignore
                }
                bleScanCallback = null;
            }
            BluetoothManager mgr = context.GetSystemService(Context.BluetoothService) as BluetoothManager;
            BluetoothAdapter adapter = mgr?.Adapter;
            if (adapter == null || !adapter.IsEnabled) {
                return;
            }
            bleScanner = adapter.BluetoothLeScanner;
            if (bleScanner == null) {
                return;
            }
            bleScanCallback = new BleScanCallbackImpl(OnBleDeviceDiscovered);
            ScanSettings settings = new ScanSettings.Builder()
                .SetScanMode(Android.Bluetooth.LE.ScanMode.LowLatency)
                .Build();
            bleScanner.StartScan(null, settings, bleScanCallback);
            AppLogger.Info("BleOscHeadTracker BLE scanner started for external head trackers.");
        } catch (Exception ex) {
            AppLogger.Warn($"BleOscHeadTracker BLE scan skipped: {ex.Message}");
        }
    }

    void OnBleDeviceDiscovered(BluetoothDevice device) {
        if (!running || device == null || !HasBlePermissions()) {
            return;
        }
        string name;
        try {
            name = device.Name ?? string.Empty;
        } catch (Java.Lang.SecurityException sx) {
            AppLogger.Warn($"BleOscHeadTracker BLE permission check failed reading device name: {sx.Message}");
            return;
        }
        bool looksLikeHeadTracker =
            name.Contains("Headtracker", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Head Tracker", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mr. Head", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Supperware", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("OpenTrack", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("BNO08", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeHeadTracker) {
            return;
        }

        lock (lifecycleLock) {
            if (!running || activeGatt != null) {
                return;
            }
            try {
                if (bleScanner != null && bleScanCallback != null) {
                    bleScanner.StopScan(bleScanCallback);
                }
            } catch {
                // Ignore
            }
            try {
                bleGattCallback = new BleGattCallbackImpl(this, name);
                activeGatt = device.ConnectGatt(context, autoConnect: false, bleGattCallback, BluetoothTransports.Le);
                SetStatus(string.Format(AndroidStrings.Active["HtBleConnecting"], name));
            } catch (Java.Lang.SecurityException sx) {
                AppLogger.Warn($"BleOscHeadTracker ConnectGatt denied: {sx.Message}");
                activeGatt = null;
                bleGattCallback = null;
            }
        }
    }

    void OnBleNotification(byte[] payload, string deviceName) {
        if (!running || payload == null || payload.Length == 0) {
            return;
        }
        if (OscPacketParser.TryParseBlePayload(payload, out OscHeadPoseMessage msg) && msg.IsValid) {
            IngestPoseMessage(msg, $"BLE • {deviceName}");
        }
    }

    void OnBleConnectionChanged(bool isConnected, string deviceName) {
        bleConnected = isConnected;
        if (isConnected) {
            connectedPeerLabel = $"BLE • {deviceName}";
            SetStatus(string.Format(AndroidStrings.Active["HtBleConnected"], deviceName));
        } else if (running) {
            lock (lifecycleLock) {
                try {
                    activeGatt?.Close();
                    activeGatt?.Dispose();
                } catch {
                    // Ignore
                }
                activeGatt = null;
                bleGattCallback = null;
                if (running) {
                    TryStartBleScanLocked();
                }
            }
            SetStatus(string.Format(AndroidStrings.Active["HtBleOscListening"], listenPort));
        }
    }

    void StopBleLocked() {
        try {
            if (bleScanner != null && bleScanCallback != null) {
                bleScanner.StopScan(bleScanCallback);
            }
        } catch {
            // Ignore
        }
        bleScanCallback = null;
        bleScanner = null;

        try {
            activeGatt?.Disconnect();
            activeGatt?.Close();
            activeGatt?.Dispose();
        } catch {
            // Ignore
        }
        activeGatt = null;
        bleGattCallback = null;
    }

    bool HasBlePermissions() {
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return context.CheckSelfPermission(Manifest.Permission.BluetoothConnect) == Permission.Granted &&
                   context.CheckSelfPermission(Manifest.Permission.BluetoothScan) == Permission.Granted;
        }
        // API 26-30 require runtime location permission for BLE scanning.
        return context.CheckSelfPermission(Manifest.Permission.AccessFineLocation) == Permission.Granted;
    }

    void SetStatus(string text) {
        statusDetail = text ?? string.Empty;
        StatusChanged?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    sealed class BleScanCallbackImpl(Action<BluetoothDevice> onDiscovered) : ScanCallback {
        public override void OnScanResult(ScanCallbackType callbackType, ScanResult result) {
            base.OnScanResult(callbackType, result);
            if (result?.Device != null) {
                onDiscovered?.Invoke(result.Device);
            }
        }
    }

    sealed class BleGattCallbackImpl(BleOscHeadTrackerProvider owner, string deviceName) : BluetoothGattCallback {
        public override void OnConnectionStateChange(BluetoothGatt gatt, GattStatus status, ProfileState newState) {
            base.OnConnectionStateChange(gatt, status, newState);
            if (newState == ProfileState.Connected) {
                owner.OnBleConnectionChanged(true, deviceName);
                gatt?.DiscoverServices();
            } else if (newState == ProfileState.Disconnected) {
                owner.OnBleConnectionChanged(false, deviceName);
            }
        }

        public override void OnServicesDiscovered(BluetoothGatt gatt, GattStatus status) {
            base.OnServicesDiscovered(gatt, status);
            if (status != GattStatus.Success || gatt == null) {
                return;
            }
            BluetoothGattService svc = gatt.GetService(NordicUartServiceUuid);
            BluetoothGattCharacteristic tx = svc?.GetCharacteristic(NordicUartTxCharUuid);
            if (tx == null) {
                // Fallback: locate first notify-capable characteristic across services
                IList<BluetoothGattService> services = gatt.Services;
                if (services != null) {
                    for (int s = 0; s < services.Count && tx == null; s++) {
                        IList<BluetoothGattCharacteristic> chars = services[s].Characteristics;
                        if (chars == null) continue;
                        for (int c = 0; c < chars.Count; c++) {
                            if ((chars[c].Properties & GattProperty.Notify) != 0) {
                                tx = chars[c];
                                break;
                            }
                        }
                    }
                }
            }
            if (tx == null) {
                return;
            }

            gatt.SetCharacteristicNotification(tx, enable: true);
            BluetoothGattDescriptor cccd = tx.GetDescriptor(ClientCharConfigUuid);
            if (cccd != null) {
                byte[] enableVal = BluetoothGattDescriptor.EnableNotificationValue?.ToArray() ?? [0x01, 0x00];
                if (OperatingSystem.IsAndroidVersionAtLeast(33)) {
                    gatt.WriteDescriptor(cccd, enableVal);
                } else {
#pragma warning disable CS0618, CA1422
                    cccd.SetValue(enableVal);
                    gatt.WriteDescriptor(cccd);
#pragma warning restore CS0618, CA1422
                }
            }
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) {
            base.OnCharacteristicChanged(gatt, characteristic, value);
            owner.OnBleNotification(value, deviceName);
        }

#pragma warning disable CS0618, CA1422
        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic) {
            base.OnCharacteristicChanged(gatt, characteristic);
            owner.OnBleNotification(characteristic?.GetValue(), deviceName);
        }
#pragma warning restore CS0618, CA1422
    }
}
