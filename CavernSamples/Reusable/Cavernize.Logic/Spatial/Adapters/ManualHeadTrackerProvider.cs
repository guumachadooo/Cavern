using System.Diagnostics;
using System.Numerics;

using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 4 default provider driven by manual UI controls (3D Room head gimbal and position controls).
/// Thread-safe, allocation-free on the audio thread, and seamlessly replaceable by hardware trackers in Wave W04.
/// </summary>
public sealed class ManualHeadTrackerProvider : IHeadTrackerProvider {
    readonly object poseLock = new();
    Vector3 position = Vector3.Zero;
    Vector3 eulerDegrees = Vector3.Zero;
    Vector3 recenterOffset = Vector3.Zero;
    long lastUpdateTicks;
    bool running = true;

    /// <inheritdoc/>
    public string ProviderId => "manual";

    /// <inheritdoc/>
    public string DisplayName => "Manual 3D Control";

    /// <inheritdoc/>
    public bool IsConnected => running;

    /// <inheritdoc/>
    public bool Supports6DoF => true;

    /// <summary>
    /// Updates manual position and Euler orientation (degrees: X = Pitch, Y = Yaw, Z = Roll).
    /// </summary>
    public void SetPose(Vector3 newPosition, Vector3 newEulerDegrees) {
        lock (poseLock) {
            position = newPosition;
            eulerDegrees = newEulerDegrees;
            lastUpdateTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <inheritdoc/>
    public void Start() => running = true;

    /// <inheritdoc/>
    public void Stop() => running = false;

    /// <inheritdoc/>
    public void Recenter() {
        lock (poseLock) {
            recenterOffset = eulerDegrees;
            lastUpdateTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Resets any manual recenter offset back to zero.
    /// </summary>
    public void ClearRecenter() {
        lock (poseLock) {
            recenterOffset = Vector3.Zero;
        }
    }

    /// <inheritdoc/>
    public ListenerPose6DoF GetCurrentPose(float predictionHorizonMs = 0f) {
        lock (poseLock) {
            Vector3 adjustedEuler = eulerDegrees - recenterOffset;
            return ListenerPose6DoF.FromPositionAndEuler(position, adjustedEuler, lastUpdateTicks, isTrackingActive: false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Stop();
}
