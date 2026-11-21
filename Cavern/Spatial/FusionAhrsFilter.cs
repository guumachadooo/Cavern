using System;
using System.Numerics;

namespace Cavern.Spatial {
    /// <summary>
    /// 6-axis Attitude and Heading Reference System (AHRS) complementary quaternion filter
    /// curated from xioTechnologies/Fusion (Seb Madgwick's FusionAhrs + FusionOffset) for
    /// drift-resistant IMU head tracking without magnetometer distortion.
    /// Operates purely in value types with zero heap allocations per update.
    /// </summary>
    public sealed class FusionAhrsFilter {
        const float InitialStartupGain = 10f;
        const float StartupPeriodSeconds = 3f;
        const float StationaryGyroThresholdRadPerSec = 0.05235988f; // ~3 deg/s

        Quaternion orientation = Quaternion.Identity;
        Vector3 gyroBiasRadPerSec = Vector3.Zero;

        float gain = 0.5f;
        float startupGain = InitialStartupGain;
        bool inStartup = true;

        float accelerationRejectionSquared;
        float rejectionTimeoutSeconds = 5f;
        float accelerationRecoveryTimer;
        float accelerationRecoveryThreshold = 5f;
        float stationaryTimerSeconds;
        bool accelerometerIgnored;
        bool hasSample;

        /// <summary>
        /// Current estimated unit orientation quaternion in sensor frame.
        /// </summary>
        public Quaternion Orientation => orientation;

        /// <summary>
        /// Current estimated stationary gyroscope bias (radians per second).
        /// </summary>
        public Vector3 GyroscopeBiasRadPerSec => gyroBiasRadPerSec;

        /// <summary>
        /// True when the latest update rejected linear/centrifugal acceleration spikes.
        /// </summary>
        public bool AccelerometerIgnored => accelerometerIgnored;

        /// <summary>
        /// Steady-state complementary filter gain (0.5 recommended by xioTechnologies/Fusion).
        /// </summary>
        public float Gain {
            get => gain;
            set => gain = value < 0f ? 0f : value;
        }

        /// <summary>
        /// Creates a Fusion 6-axis AHRS filter with optional steady-state gain and acceleration rejection angle (degrees).
        /// </summary>
        /// <param name="gain">Steady-state inclination feedback gain (default 0.5).</param>
        /// <param name="accelerationRejectionDegrees">Angular error threshold in degrees above which linear acceleration is rejected (default 10°).</param>
        /// <param name="rejectionTimeoutSeconds">Seconds before recovery re-engages accelerometer feedback (default 5 s).</param>
        public FusionAhrsFilter(
            float gain = 0.5f,
            float accelerationRejectionDegrees = 10f,
            float rejectionTimeoutSeconds = 5f) {
            this.gain = gain < 0f ? 0f : gain;
            this.rejectionTimeoutSeconds = rejectionTimeoutSeconds < 0.5f ? 0.5f : rejectionTimeoutSeconds;
            accelerationRecoveryThreshold = this.rejectionTimeoutSeconds;

            if (accelerationRejectionDegrees > 0f && accelerationRejectionDegrees < 180f) {
                float halfSin = 0.5f * (float)Math.Sin(accelerationRejectionDegrees * (Math.PI / 180.0));
                accelerationRejectionSquared = halfSin * halfSin;
            } else {
                accelerationRejectionSquared = float.MaxValue;
            }
        }

        /// <summary>
        /// Resets filter orientation, startup convergence ramp, and recovery state.
        /// </summary>
        public void Reset(bool resetGyroBias = false) {
            orientation = Quaternion.Identity;
            startupGain = InitialStartupGain;
            inStartup = true;
            accelerationRecoveryTimer = 0f;
            accelerationRecoveryThreshold = rejectionTimeoutSeconds;
            stationaryTimerSeconds = 0f;
            accelerometerIgnored = false;
            hasSample = false;
            if (resetGyroBias) {
                gyroBiasRadPerSec = Vector3.Zero;
            }
        }

        /// <summary>
        /// Advances the 6-axis AHRS filter with gyroscope (rad/s) and accelerometer (any consistent unit, e.g. m/s² or g)
        /// measurements over timestep <paramref name="deltaTimeSeconds"/>. Returns the updated unit quaternion.
        /// </summary>
        public Quaternion UpdateNoMagnetometer(Vector3 gyroscopeRadPerSec, Vector3 accelerometer, float deltaTimeSeconds) {
            if (deltaTimeSeconds <= 0f || deltaTimeSeconds > 0.5f ||
                !IsFinite(gyroscopeRadPerSec) || !IsFinite(accelerometer)) {
                return orientation;
            }

            // Online stationary gyroscope bias estimation (inspired by FusionOffset)
            float gyroMag = gyroscopeRadPerSec.Length();
            float accelMag = accelerometer.Length();
            if (gyroMag < StationaryGyroThresholdRadPerSec && accelMag > 0.1f) {
                stationaryTimerSeconds += deltaTimeSeconds;
                if (stationaryTimerSeconds >= 1.0f) {
                    float alpha = Math.Min(1f, deltaTimeSeconds * 0.25f);
                    gyroBiasRadPerSec += (gyroscopeRadPerSec - gyroBiasRadPerSec) * alpha;
                }
            } else {
                stationaryTimerSeconds = 0f;
            }

            Vector3 correctedGyro = gyroscopeRadPerSec - gyroBiasRadPerSec;

            // Startup high-gain convergence ramp (10.0 -> steady-state gain over 3 seconds)
            float activeGain;
            if (inStartup) {
                float rampStep = (InitialStartupGain - gain) * (deltaTimeSeconds / StartupPeriodSeconds);
                startupGain -= rampStep;
                if (startupGain > gain) {
                    activeGain = startupGain;
                } else {
                    inStartup = false;
                    activeGain = gain;
                }
            } else {
                activeGain = gain;
            }

            // Half angular velocity (0.5 * omega)
            Vector3 halfGyro = correctedGyro * 0.5f;

            // Half gravity estimated from current quaternion (ENU/NWU: +Z up -> third column of R^T scaled by 0.5)
            Vector3 halfGravity = new Vector3(
                orientation.X * orientation.Z - orientation.W * orientation.Y,
                orientation.Y * orientation.Z + orientation.W * orientation.X,
                orientation.W * orientation.W - 0.5f + orientation.Z * orientation.Z);

            // Half inclination feedback with linear-acceleration rejection
            Vector3 halfFeedback = Vector3.Zero;
            accelerometerIgnored = true;
            if (accelMag > 1e-4f) {
                Vector3 normAccel = accelerometer / accelMag;
                Vector3 halfResidual = Vector3.Cross(normAccel, halfGravity);

                bool withinThreshold = inStartup || !hasSample ||
                    halfResidual.LengthSquared() <= accelerationRejectionSquared;
                if (withinThreshold) {
                    accelerometerIgnored = false;
                    accelerationRecoveryTimer = Math.Max(0f, accelerationRecoveryTimer - 9f * deltaTimeSeconds);
                } else {
                    accelerationRecoveryTimer = Math.Min(rejectionTimeoutSeconds, accelerationRecoveryTimer + deltaTimeSeconds);
                }

                if (accelerationRecoveryTimer >= accelerationRecoveryThreshold) {
                    accelerationRecoveryThreshold = 0f;
                    accelerometerIgnored = false;
                } else {
                    accelerationRecoveryThreshold = rejectionTimeoutSeconds;
                }

                if (!accelerometerIgnored) {
                    halfFeedback = halfResidual;
                }
            }

            // Integrate quaternion derivative: q_dot = q * [0, halfGyro + gain * halfFeedback]
            Vector3 halfRate = halfGyro + halfFeedback * activeGain;
            Vector3 scaledRate = halfRate * deltaTimeSeconds;

            float qw = orientation.W;
            float qx = orientation.X;
            float qy = orientation.Y;
            float qz = orientation.Z;

            float dw = -qx * scaledRate.X - qy * scaledRate.Y - qz * scaledRate.Z;
            float dx = qw * scaledRate.X + qy * scaledRate.Z - qz * scaledRate.Y;
            float dy = qw * scaledRate.Y - qx * scaledRate.Z + qz * scaledRate.X;
            float dz = qw * scaledRate.Z + qx * scaledRate.Y - qy * scaledRate.X;

            Quaternion next = new Quaternion(qx + dx, qy + dy, qz + dz, qw + dw);
            float normSq = next.LengthSquared();
            orientation = normSq > 1e-12f ? Quaternion.Normalize(next) : Quaternion.Identity;
            if (orientation.W < 0f) {
                orientation = new Quaternion(-orientation.X, -orientation.Y, -orientation.Z, -orientation.W);
            }
            hasSample = true;
            return orientation;
        }

        static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
            !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
            !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }
}
