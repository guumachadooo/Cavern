using System.Diagnostics;
using System.Numerics;

using Android;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;

using Cavern.Spatial;

using Cavernize.Logic.Spatial;

using Java.Util;

namespace CavernizeAndroid.Services;

/// <summary>
/// Slot 4 hardware head-tracking provider for Samsung Galaxy Buds (Pro / Buds2 / Buds2 Pro / Buds3 / Buds3 Pro / FE)
/// streaming native 6-axis Game Rotation Vector (GRV) quaternions over Bluetooth Classic RFCOMM SPP,
/// curated from <c>timschneeb/GalaxyBudsClient</c> and <c>timschneeb/BudsPro-Headtracking</c>.
/// Feeds <see cref="HeadPosePredictor"/> for zero-GC audio-thread consumption with Bluetooth latency look-ahead.
/// </summary>
public sealed class GalaxyBudsHeadTrackerProvider : IHeadTrackerProvider {
    static readonly string[] CandidateUuids = [
        GalaxyBudsProtocol.UuidSppNew,
        GalaxyBudsProtocol.UuidSppStandard,
        GalaxyBudsProtocol.UuidSppLegacy
    ];

    readonly Context context;
    readonly HeadPosePredictor predictor = new();
    readonly object lifecycleLock = new();

    CancellationTokenSource cts;
    Thread workerThread;
    BluetoothSocket activeSocket;

    volatile bool connected;
    volatile bool isWorn = true;
    volatile bool disposed;
    string connectedDeviceName = string.Empty;
    string statusDetail = string.Empty;

    /// <inheritdoc/>
    public string ProviderId => "galaxy-buds-rfcomm";

    /// <inheritdoc/>
    public string DisplayName => string.IsNullOrEmpty(connectedDeviceName)
        ? "Galaxy Buds RFCOMM"
        : connectedDeviceName;

    /// <inheritdoc/>
    public bool IsConnected => connected;

    /// <inheritdoc/>
    public bool Supports6DoF => false;

    /// <summary>True while the earbuds report in-ear wearing state.</summary>
    public bool IsWorn => isWorn;

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

    public GalaxyBudsHeadTrackerProvider(Context context) => this.context = context.ApplicationContext ?? context;

    /// <inheritdoc/>
    public void Start() {
        lock (lifecycleLock) {
            if (disposed || workerThread != null) {
                return;
            }
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            workerThread = new Thread(() => RunLoop(token)) {
                Name = "CavernizeGalaxyBudsHT",
                IsBackground = true
            };
            workerThread.Start();
        }
    }

    /// <inheritdoc/>
    public void Stop() {
        Thread toJoin;
        BluetoothSocket sockToClose;
        lock (lifecycleLock) {
            cts?.Cancel();
            sockToClose = activeSocket;
            activeSocket = null;
            toJoin = workerThread;
            workerThread = null;
        }

        TrySendDetachQuietly(sockToClose);
        SafeCloseSocket(sockToClose);

        if (toJoin != null && toJoin.IsAlive && toJoin != Thread.CurrentThread) {
            toJoin.Join(1200);
        }

        lock (lifecycleLock) {
            cts?.Dispose();
            cts = null;
        }

        connected = false;
        SetStatus(string.Empty);
    }

    /// <inheritdoc/>
    public void Recenter() => predictor.Recenter();

    /// <inheritdoc/>
    public ListenerPose6DoF GetCurrentPose(float predictionHorizonMs = 0f) =>
        predictor.PredictPose(predictionHorizonMs, connected && isWorn);

    void RunLoop(CancellationToken token) {
        byte[] readChunk = new byte[512];
        byte[] streamBuffer = new byte[2048];

        while (!token.IsCancellationRequested) {
            if (!HasBluetoothConnectPermission()) {
                connected = false;
                SetStatus("BLUETOOTH_CONNECT permission required");
                WaitInterruptible(token, 2000);
                continue;
            }

            BluetoothDevice target = FindBondedGalaxyBuds();
            if (target == null) {
                connected = false;
                connectedDeviceName = string.Empty;
                SetStatus("No paired Galaxy Buds found");
                WaitInterruptible(token, 2500);
                continue;
            }

            connectedDeviceName = target.Name ?? "Galaxy Buds";
            SetStatus($"Connecting to {connectedDeviceName}...");

            BluetoothSocket socket = TryOpenRfcommSocket(target);
            if (socket == null) {
                connected = false;
                SetStatus($"Waiting for {connectedDeviceName}...");
                WaitInterruptible(token, 2500);
                continue;
            }

            lock (lifecycleLock) {
                if (token.IsCancellationRequested) {
                    SafeCloseSocket(socket);
                    return;
                }
                activeSocket = socket;
            }

            try {
                predictor.Reset();
                Stream input = socket.InputStream;
                Stream output = socket.OutputStream;
                if (input == null || output == null) {
                    throw new IOException("RFCOMM streams unavailable.");
                }

                // Handshake: enable spatial sensor pipeline + attach GRV stream
                SendPacket(output, GalaxyBudsProtocol.MsgSetSpatialAudio, [1]);
                SendPacket(output, GalaxyBudsProtocol.MsgSpatialAudioControl, [GalaxyBudsProtocol.CtrlAttach]);

                connected = true;
                isWorn = true;
                SetStatus($"Connected • {connectedDeviceName}");
                AppLogger.Info($"GalaxyBudsHeadTracker connected to '{connectedDeviceName}'.");

                int buffered = 0;
                long lastKeepAliveMs = Environment.TickCount64;
                long lastPacketMs = lastKeepAliveMs;

                while (!token.IsCancellationRequested && socket.IsConnected) {
                    long nowMs = Environment.TickCount64;
                    if (nowMs - lastKeepAliveMs >= 2000) {
                        SendPacket(output, GalaxyBudsProtocol.MsgSpatialAudioControl, [GalaxyBudsProtocol.CtrlKeepAlive]);
                        lastKeepAliveMs = nowMs;
                    }

                    int read = input.Read(readChunk, 0, readChunk.Length);
                    if (read <= 0) {
                        break;
                    }

                    if (buffered + read > streamBuffer.Length) {
                        // Guard against malformed flood by dropping oldest prefix
                        int drop = Math.Min(buffered, buffered + read - streamBuffer.Length);
                        Buffer.BlockCopy(streamBuffer, drop, streamBuffer, 0, buffered - drop);
                        buffered -= drop;
                    }
                    Buffer.BlockCopy(readChunk, 0, streamBuffer, buffered, read);
                    buffered += read;

                    int cursor = 0;
                    while (cursor < buffered) {
                        ReadOnlySpan<byte> slice = streamBuffer.AsSpan(cursor, buffered - cursor);
                        int consumed = GalaxyBudsProtocol.TryExtractFrame(
                            slice,
                            out byte msgId,
                            out ReadOnlySpan<byte> payload,
                            out bool crcValid);
                        if (consumed <= 0) {
                            break;
                        }
                        cursor += consumed;
                        if (!crcValid) {
                            continue;
                        }

                        if (msgId == GalaxyBudsProtocol.MsgSpatialAudioData) {
                            if (GalaxyBudsProtocol.TryDecodeBudGrvQuaternion(
                                payload,
                                out Quaternion q,
                                out byte eventId,
                                out bool worn)) {
                                isWorn = worn;
                                lastPacketMs = Environment.TickCount64;
                                if (worn) {
                                    predictor.PushSample(q, null, Vector3.Zero, Stopwatch.GetTimestamp());
                                }
                            } else if (eventId == GalaxyBudsProtocol.DataWearOn) {
                                isWorn = true;
                            } else if (eventId == GalaxyBudsProtocol.DataWearOff) {
                                isWorn = false;
                            }
                        } else if (msgId == GalaxyBudsProtocol.MsgSpatialAudioControl && payload.Length > 0) {
                            if (payload[0] == GalaxyBudsProtocol.CtrlDetachSuccess) {
                                break;
                            }
                        }
                    }

                    if (cursor > 0) {
                        int remaining = buffered - cursor;
                        if (remaining > 0) {
                            Buffer.BlockCopy(streamBuffer, cursor, streamBuffer, 0, remaining);
                        }
                        buffered = remaining;
                    }

                    if (Environment.TickCount64 - lastPacketMs > 8000) {
                        // Re-attach if stream went silent while socket remained open
                        SendPacket(output, GalaxyBudsProtocol.MsgSpatialAudioControl, [GalaxyBudsProtocol.CtrlAttach]);
                        lastPacketMs = Environment.TickCount64;
                    }
                }
            } catch (Exception ex) {
                if (!token.IsCancellationRequested) {
                    AppLogger.Warn($"GalaxyBudsHeadTracker stream ended ({connectedDeviceName}): {ex.Message}");
                }
            } finally {
                connected = false;
                lock (lifecycleLock) {
                    if (ReferenceEquals(activeSocket, socket)) {
                        activeSocket = null;
                    }
                }
                TrySendDetachQuietly(socket);
                SafeCloseSocket(socket);
                SetStatus($"Reconnecting to {connectedDeviceName}...");
            }

            WaitInterruptible(token, 2000);
        }
    }

    bool HasBluetoothConnectPermission() {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return true;
        }
        return context.CheckSelfPermission(Manifest.Permission.BluetoothConnect) == Permission.Granted;
    }

    BluetoothDevice FindBondedGalaxyBuds() {
        try {
            BluetoothManager mgr = context.GetSystemService(Context.BluetoothService) as BluetoothManager;
            BluetoothAdapter adapter = mgr?.Adapter;
            if (adapter == null || !adapter.IsEnabled) {
                return null;
            }
            ICollection<BluetoothDevice> bonded = adapter.BondedDevices;
            if (bonded == null) {
                return null;
            }
            foreach (BluetoothDevice dev in bonded) {
                if (dev != null && GalaxyBudsProtocol.IsSupportedGalaxyBudsName(dev.Name)) {
                    return dev;
                }
            }
        } catch (Exception ex) {
            AppLogger.Warn($"GalaxyBudsHeadTracker scan error: {ex.Message}");
        }
        return null;
    }

    static BluetoothSocket TryOpenRfcommSocket(BluetoothDevice device) {
        for (int i = 0; i < CandidateUuids.Length; i++) {
            UUID uuid = UUID.FromString(CandidateUuids[i]);
            if (TryConnectSingle(device, uuid, secure: true, out BluetoothSocket sock) ||
                TryConnectSingle(device, uuid, secure: false, out sock)) {
                return sock;
            }
        }
        return null;
    }

    static bool TryConnectSingle(BluetoothDevice device, UUID uuid, bool secure, out BluetoothSocket socket) {
        socket = null;
        try {
            BluetoothSocket candidate = secure
                ? device.CreateRfcommSocketToServiceRecord(uuid)
                : device.CreateInsecureRfcommSocketToServiceRecord(uuid);
            if (candidate == null) {
                return false;
            }
            candidate.Connect();
            if (candidate.IsConnected) {
                socket = candidate;
                return true;
            }
            SafeCloseSocket(candidate);
        } catch {
            // Try next UUID or insecure fallback
        }
        return false;
    }

    static void SendPacket(Stream output, byte msgId, ReadOnlySpan<byte> payload) {
        byte[] frame = GalaxyBudsProtocol.EncodeRequest(msgId, payload);
        output.Write(frame, 0, frame.Length);
        output.Flush();
    }

    static void TrySendDetachQuietly(BluetoothSocket socket) {
        if (socket == null) {
            return;
        }
        try {
            if (socket.IsConnected && socket.OutputStream != null) {
                SendPacket(socket.OutputStream, GalaxyBudsProtocol.MsgSpatialAudioControl, [GalaxyBudsProtocol.CtrlDetach]);
                SendPacket(socket.OutputStream, GalaxyBudsProtocol.MsgSetSpatialAudio, [0]);
            }
        } catch {
            // Ignore shutdown write failures
        }
    }

    static void SafeCloseSocket(BluetoothSocket socket) {
        if (socket == null) {
            return;
        }
        try {
            socket.Close();
            socket.Dispose();
        } catch {
            // Ignore
        }
    }

    static void WaitInterruptible(CancellationToken token, int delayMs) {
        try {
            token.WaitHandle.WaitOne(delayMs);
        } catch {
            // Ignore
        }
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
}
