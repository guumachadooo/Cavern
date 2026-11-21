using Android;
using Android.Content;
using Android.Content.PM;

using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;

using CavernizeAndroid.Audio;

namespace CavernizeAndroid.Services;

/// <summary>
/// Coordinates Slot 4 head-tracking providers (<see cref="GalaxyBudsHeadTrackerProvider"/>,
/// <see cref="AndroidImuHeadTrackerProvider"/>, <see cref="BleOscHeadTrackerProvider"/>, and manual fallback),
/// synchronizes settings with <see cref="RealtimePlayerEngine"/> and <see cref="AndroidAudioOutput"/>,
/// and surfaces concise status updates to the One UI 9 Settings and 3D Room views.
/// </summary>
public sealed class HeadTrackingManager : IDisposable {
    readonly Context context;
    readonly AppSettingsStore store;
    readonly RealtimePlayerEngine engine;
    readonly AndroidAudioOutput audioOutput;

    readonly GalaxyBudsHeadTrackerProvider galaxyBudsProvider;
    readonly AndroidImuHeadTrackerProvider androidImuProvider;
    readonly BleOscHeadTrackerProvider bleOscProvider;

    IHeadTrackerProvider activeExternal;
    HeadTrackerSourceKind activeSource = HeadTrackerSourceKind.Manual;
    int activeOscPort = 9000;
    bool disposed;

    /// <summary>Raised whenever active tracker, connection state, or status detail changes.</summary>
    public event Action StatusChanged;

    /// <summary>Currently active head-tracker source kind.</summary>
    public HeadTrackerSourceKind ActiveSource => activeSource;

    /// <summary>True when an external/sensor head tracker is enabled and actively streaming live poses.</summary>
    public bool IsLive => activeExternal != null && activeExternal.IsConnected;

    /// <summary>True when the active external provider supports 6DoF translational coordinates.</summary>
    public bool Supports6DoF => activeExternal?.Supports6DoF ?? false;

    /// <summary>Human-readable status detail for UI display.</summary>
    public string StatusSummary {
        get {
            if (!store.HeadTrackingEnabled || activeSource == HeadTrackerSourceKind.Manual || activeExternal == null) {
                return string.Empty;
            }
            if (activeExternal is GalaxyBudsHeadTrackerProvider buds) {
                return buds.StatusDetail;
            }
            if (activeExternal is AndroidImuHeadTrackerProvider imu) {
                return imu.StatusDetail;
            }
            if (activeExternal is BleOscHeadTrackerProvider osc) {
                return osc.StatusDetail;
            }
            return activeExternal.DisplayName;
        }
    }

    public HeadTrackingManager(
        Context context,
        AppSettingsStore store,
        RealtimePlayerEngine engine,
        AndroidAudioOutput audioOutput) {
        this.context = context.ApplicationContext ?? context;
        this.store = store;
        this.engine = engine;
        this.audioOutput = audioOutput;

        galaxyBudsProvider = new GalaxyBudsHeadTrackerProvider(this.context);
        androidImuProvider = new AndroidImuHeadTrackerProvider(this.context);
        bleOscProvider = new BleOscHeadTrackerProvider(this.context);

        galaxyBudsProvider.StatusChanged += RaiseStatusChanged;
        androidImuProvider.StatusChanged += RaiseStatusChanged;
        bleOscProvider.StatusChanged += RaiseStatusChanged;
    }

    /// <summary>
    /// Returns true if the requested head-tracking source requires runtime Bluetooth permissions on Android 12+.
    /// </summary>
    public bool NeedsBluetoothPermissions(HeadTrackerSourceKind source) {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) {
            return false;
        }
        if (source == HeadTrackerSourceKind.GalaxyBudsRfcomm) {
            return context.CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted;
        }
        if (source == HeadTrackerSourceKind.BleOsc) {
            return context.CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted ||
                   context.CheckSelfPermission(Manifest.Permission.BluetoothScan) != Permission.Granted;
        }
        return false;
    }

    /// <summary>
    /// Applies persisted Slot 4 head-tracking &amp; prediction settings to the active provider and <see cref="RealtimePlayerEngine"/>.
    /// </summary>
    public void ApplySettings() {
        if (disposed) {
            return;
        }

        // Sync predictor parameters across all hardware providers
        bool autoRecenter = store.HeadTrackingAutoRecenter;
        bool lockRoll = store.HeadTrackingLockRoll;
        galaxyBudsProvider.AutoRecenterEnabled = autoRecenter;
        galaxyBudsProvider.LockRoll = lockRoll;
        androidImuProvider.AutoRecenterEnabled = autoRecenter;
        androidImuProvider.LockRoll = lockRoll;
        bleOscProvider.AutoRecenterEnabled = autoRecenter;
        bleOscProvider.LockRoll = lockRoll;
        bleOscProvider.ListenPort = store.HeadTrackingOscPort;

        engine.HeadTrackingPredictionMs = Math.Clamp(store.HeadTrackingPredictionMs, 0f, 120f);

        HeadTrackerSourceKind desired = store.HeadTrackingEnabled
            ? (HeadTrackerSourceKind)Math.Clamp(store.HeadTrackerSourceIndex, 0, 3)
            : HeadTrackerSourceKind.Manual;

        IHeadTrackerProvider nextProvider = desired switch {
            HeadTrackerSourceKind.GalaxyBudsRfcomm => galaxyBudsProvider,
            HeadTrackerSourceKind.AndroidImu => androidImuProvider,
            HeadTrackerSourceKind.BleOsc => bleOscProvider,
            _ => null
        };

        bool oscPortChanged = ReferenceEquals(nextProvider, bleOscProvider) &&
                              ReferenceEquals(activeExternal, bleOscProvider) &&
                              bleOscProvider.ListenPort != activeOscPort;

        if (!ReferenceEquals(activeExternal, nextProvider) || oscPortChanged) {
            activeExternal?.Stop();
            activeExternal = nextProvider;
            activeSource = desired;
            activeOscPort = bleOscProvider.ListenPort;

            if (activeExternal != null) {
                engine.HeadTracker = activeExternal;
                activeExternal.Start();
            } else {
                engine.HeadTracker = null; // Reverts cleanly to ManualHeadTrackerProvider
            }
        } else {
            activeSource = desired;
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// Reacts to physical audio route changes (e.g., Galaxy Buds connected or USB DAC plugged in).
    /// </summary>
    public void OnAudioDeviceChanged(AudioDeviceRouteInfo device) {
        if (disposed || device == null) {
            return;
        }
        audioOutput?.UpdateOutputRoute(device);

        // If user enabled head tracking and Galaxy Buds just connected, ensure RFCOMM provider is running
        if (store.HeadTrackingEnabled &&
            GalaxyBudsProtocol.IsSupportedGalaxyBudsName(device.DisplayName) &&
            store.HeadTrackerSourceIndex == (int)HeadTrackerSourceKind.GalaxyBudsRfcomm) {
            ApplySettings();
        }
    }

    /// <summary>
    /// Captures the listener's current physical head orientation as 0° forward reference (tare).
    /// </summary>
    public void Recenter() {
        activeExternal?.Recenter();
        engine.HeadTracker?.Recenter();
        RaiseStatusChanged();
    }

    void RaiseStatusChanged() => StatusChanged?.Invoke();

    /// <inheritdoc/>
    public void Dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        galaxyBudsProvider.StatusChanged -= RaiseStatusChanged;
        androidImuProvider.StatusChanged -= RaiseStatusChanged;
        bleOscProvider.StatusChanged -= RaiseStatusChanged;

        activeExternal?.Stop();
        activeExternal = null;
        engine.HeadTracker = null;

        galaxyBudsProvider.Dispose();
        androidImuProvider.Dispose();
        bleOscProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
