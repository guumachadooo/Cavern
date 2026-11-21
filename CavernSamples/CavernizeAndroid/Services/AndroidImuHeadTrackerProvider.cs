using System.Diagnostics;
using System.Numerics;

using Android.Content;
using Android.Hardware;

using Cavern.Spatial;

using Cavernize.Logic.Spatial;

namespace CavernizeAndroid.Services;

/// <summary>
/// Slot 4 on-device Android IMU head-tracking provider using hardware <see cref="SensorType.GameRotationVector"/>
/// (6-axis drift-resistant fusion without magnetometer interference) plus <see cref="SensorType.Gyroscope"/> angular
/// velocity for short-horizon latency prediction. Automatically falls back to <see cref="FusionAhrsFilter"/>
/// (Madgwick/xioTechnologies Fusion 6-axis AHRS) from raw Gyroscope + Accelerometer when GameRotationVector is absent.
/// </summary>
public sealed class AndroidImuHeadTrackerProvider : Java.Lang.Object, ISensorEventListener, IHeadTrackerProvider {
    readonly Context context;
    readonly SensorManager sensorManager;
    readonly HeadPosePredictor predictor = new();
    readonly FusionAhrsFilter fallbackAhrs = new();
    readonly object lifecycleLock = new();

    // Pre-allocated quaternion scratch array for zero-GC SensorManager.GetQuaternionFromVector
    readonly float[] quatScratch = new float[4];

    Sensor gameRotationSensor;
    Sensor gyroSensor;
    Sensor accelSensor;

    volatile bool running;
    volatile bool hasReceivedPacket;
    volatile bool usingFusionFallback;
    volatile bool disposed;

    Vector3 latestGyroRadPerSec = Vector3.Zero;
    Vector3 latestAccel = new(0f, 0f, 9.81f);
    bool hasGyroSample;
    long lastAhrsTimestampNs;
    string statusDetail = string.Empty;

    /// <inheritdoc/>
    public string ProviderId => "android-imu";

    /// <inheritdoc/>
    public string DisplayName => usingFusionFallback ? "Android IMU (Fusion AHRS)" : "Android IMU (GRV)";

    /// <inheritdoc/>
    public bool IsConnected => running && hasReceivedPacket;

    /// <inheritdoc/>
    public bool Supports6DoF => false;

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

    public AndroidImuHeadTrackerProvider(Context context) {
        this.context = context.ApplicationContext ?? context;
        sensorManager = this.context.GetSystemService(Context.SensorService) as SensorManager;
    }

    /// <inheritdoc/>
    public void Start() {
        lock (lifecycleLock) {
            if (disposed || running || sensorManager == null) {
                return;
            }

            predictor.Reset();
            fallbackAhrs.Reset();
            hasReceivedPacket = false;
            hasGyroSample = false;
            lastAhrsTimestampNs = 0L;

            gameRotationSensor = sensorManager.GetDefaultSensor(SensorType.GameRotationVector);
            gyroSensor = sensorManager.GetDefaultSensor(SensorType.Gyroscope);
            accelSensor = sensorManager.GetDefaultSensor(SensorType.Accelerometer);

            const int samplingUs = 10000; // 100 Hz target (10 ms)
            if (gameRotationSensor != null) {
                usingFusionFallback = false;
                sensorManager.RegisterListener(this, gameRotationSensor, (SensorDelay)samplingUs);
                if (gyroSensor != null) {
                    sensorManager.RegisterListener(this, gyroSensor, (SensorDelay)samplingUs);
                }
                running = true;
                SetStatus("Active • GameRotationVector");
                AppLogger.Info("AndroidImuHeadTracker started (GameRotationVector + Gyroscope).");
            } else if (gyroSensor != null && accelSensor != null) {
                usingFusionFallback = true;
                sensorManager.RegisterListener(this, gyroSensor, (SensorDelay)samplingUs);
                sensorManager.RegisterListener(this, accelSensor, (SensorDelay)samplingUs);
                running = true;
                SetStatus("Active • Fusion 6-Axis AHRS");
                AppLogger.Info("AndroidImuHeadTracker started (FusionAhrsFilter Gyro+Accel fallback).");
            } else {
                running = false;
                SetStatus("No IMU sensors available on device");
                AppLogger.Warn("AndroidImuHeadTracker: GameRotationVector and Gyroscope/Accelerometer unavailable.");
            }
        }
    }

    /// <inheritdoc/>
    public void Stop() {
        lock (lifecycleLock) {
            if (!running) {
                return;
            }
            running = false;
            hasReceivedPacket = false;
            try {
                sensorManager?.UnregisterListener(this);
            } catch {
                // Ignore
            }
        }
        SetStatus(string.Empty);
    }

    /// <inheritdoc/>
    public void Recenter() => predictor.Recenter();

    /// <inheritdoc/>
    public ListenerPose6DoF GetCurrentPose(float predictionHorizonMs = 0f) =>
        predictor.PredictPose(predictionHorizonMs, IsConnected);

    /// <inheritdoc/>
    public void OnAccuracyChanged(Sensor sensor, SensorStatus accuracy) { }

    /// <inheritdoc/>
    public void OnSensorChanged(SensorEvent e) {
        if (!running || e?.Values == null || e.Values.Count < 3) {
            return;
        }

        SensorType type = e.Sensor?.Type ?? 0;
        if (type == SensorType.Gyroscope) {
            // Remap Android device axes (+X right, +Y up, +Z out-of-screen) -> Cavern (+X right, +Y up, +Z forward)
            Vector3 cavGyro = new(e.Values[0], e.Values[1], -e.Values[2]);
            latestGyroRadPerSec = cavGyro;
            hasGyroSample = true;

            if (usingFusionFallback) {
                long ts = e.Timestamp;
                if (lastAhrsTimestampNs > 0L && ts > lastAhrsTimestampNs) {
                    float dt = (float)((ts - lastAhrsTimestampNs) * 1e-9);
                    Quaternion qSensor = fallbackAhrs.UpdateNoMagnetometer(
                        new Vector3(e.Values[0], e.Values[1], e.Values[2]),
                        latestAccel,
                        dt);
                    Quaternion qCavern = RemapAndroidQuaternionToCavern(qSensor);
                    PushAndMarkLive(qCavern, cavGyro);
                }
                lastAhrsTimestampNs = ts;
            }
            return;
        }

        if (type == SensorType.Accelerometer) {
            latestAccel = new Vector3(e.Values[0], e.Values[1], e.Values[2]);
            return;
        }

        if (type == SensorType.GameRotationVector) {
            float[] rawRv = [
                e.Values[0],
                e.Values[1],
                e.Values[2],
                e.Values.Count >= 4 ? e.Values[3] : 0f
            ];
            SensorManager.GetQuaternionFromVector(quatScratch, rawRv);
            // Android GetQuaternionFromVector returns [w, x, y, z]
            Quaternion qAndroid = new(quatScratch[1], quatScratch[2], quatScratch[3], quatScratch[0]);
            Quaternion qCavern = RemapAndroidQuaternionToCavern(qAndroid);
            Vector3? gyro = hasGyroSample ? latestGyroRadPerSec : null;
            PushAndMarkLive(qCavern, gyro);
        }
    }

    void PushAndMarkLive(Quaternion qCavern, Vector3? gyroRadPerSec) {
        predictor.PushSample(qCavern, gyroRadPerSec, Vector3.Zero, Stopwatch.GetTimestamp());
        if (!hasReceivedPacket) {
            hasReceivedPacket = true;
            StatusChanged?.Invoke();
        }
    }

    static Quaternion RemapAndroidQuaternionToCavern(Quaternion qAndroid) {
        // Android ENU/device sensor frame (+X right, +Y up, +Z toward user) -> Cavern (+X right, +Y up, +Z forward)
        Quaternion remapped = new(qAndroid.X, qAndroid.Z, -qAndroid.Y, qAndroid.W);
        return remapped.LengthSquared() > 1e-8f ? Quaternion.Normalize(remapped) : Quaternion.Identity;
    }

    void SetStatus(string text) {
        statusDetail = text ?? string.Empty;
        StatusChanged?.Invoke();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (!disposed) {
            disposed = true;
            if (disposing) {
                Stop();
            }
        }
        base.Dispose(disposing);
    }
}
