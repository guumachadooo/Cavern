using System;
using System.Numerics;

namespace Cavern.Spatial {
    /// <summary>
    /// Immutable 6-Degrees-of-Freedom listener pose (position, orientation, angular velocity, and timestamp)
    /// shared across head-tracking providers (Slot 4), the scene hub (Slot 3), and binaural renderers (Slot 5).
    /// </summary>
    public readonly struct ListenerPose6DoF {
        /// <summary>
        /// Neutral origin pose facing straight ahead with zero motion.
        /// </summary>
        public static readonly ListenerPose6DoF Identity = new ListenerPose6DoF(
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            Vector3.Zero,
            0L,
            0f,
            false);

        /// <summary>
        /// Listener position inside the simulated listening room (Cavern room coordinates: +X right, +Y up, +Z forward).
        /// </summary>
        public Vector3 Position { get; }

        /// <summary>
        /// Unit orientation quaternion representing listener head rotation (gimbal-lock-free).
        /// </summary>
        public Quaternion Orientation { get; }

        /// <summary>
        /// Orientation expressed as Euler angles in degrees (X = Pitch, Y = Yaw, Z = Roll), matching <see cref="Listener.Rotation"/>.
        /// </summary>
        public Vector3 EulerDegrees { get; }

        /// <summary>
        /// Measured angular velocity in radians per second (X = Pitch, Y = Yaw, Z = Roll) for short-horizon latency prediction.
        /// </summary>
        public Vector3 AngularVelocityRadPerSec { get; }

        /// <summary>
        /// Monotonic timestamp (Stopwatch ticks) when this pose was sampled.
        /// </summary>
        public long TimestampTicks { get; }

        /// <summary>
        /// Look-ahead prediction horizon in milliseconds already applied to <see cref="Orientation"/>.
        /// </summary>
        public float PredictionHorizonMs { get; }

        /// <summary>
        /// True when driven by an active hardware/sensor head tracker rather than manual UI sliders.
        /// </summary>
        public bool IsTrackingActive { get; }

        /// <summary>
        /// Creates a fully specified 6DoF listener pose.
        /// </summary>
        public ListenerPose6DoF(
            Vector3 position,
            Quaternion orientation,
            Vector3 eulerDegrees,
            Vector3 angularVelocityRadPerSec,
            long timestampTicks,
            float predictionHorizonMs,
            bool isTrackingActive) {
            Position = position;
            Orientation = orientation.LengthSquared() > 1e-8f ? Quaternion.Normalize(orientation) : Quaternion.Identity;
            EulerDegrees = eulerDegrees;
            AngularVelocityRadPerSec = angularVelocityRadPerSec;
            TimestampTicks = timestampTicks;
            PredictionHorizonMs = predictionHorizonMs;
            IsTrackingActive = isTrackingActive;
        }

        /// <summary>
        /// Constructs a pose from room position and Euler angles in degrees (X = Pitch, Y = Yaw, Z = Roll).
        /// </summary>
        public static ListenerPose6DoF FromPositionAndEuler(
            Vector3 position,
            Vector3 eulerDegrees,
            long timestampTicks = 0L,
            bool isTrackingActive = false) {
            Quaternion q = FromEulerDegrees(eulerDegrees);
            return new ListenerPose6DoF(
                position,
                q,
                eulerDegrees,
                Vector3.Zero,
                timestampTicks,
                0f,
                isTrackingActive);
        }

        /// <summary>
        /// Constructs a pose from room position and a unit orientation quaternion.
        /// </summary>
        public static ListenerPose6DoF FromPositionAndQuaternion(
            Vector3 position,
            Quaternion orientation,
            Vector3 angularVelocityRadPerSec,
            long timestampTicks,
            float predictionHorizonMs = 0f,
            bool isTrackingActive = true) {
            Quaternion norm = orientation.LengthSquared() > 1e-8f ? Quaternion.Normalize(orientation) : Quaternion.Identity;
            Vector3 euler = ToEulerDegrees(norm);
            return new ListenerPose6DoF(
                position,
                norm,
                euler,
                angularVelocityRadPerSec,
                timestampTicks,
                predictionHorizonMs,
                isTrackingActive);
        }

        /// <summary>
        /// Converts Cavern Euler angles in degrees (X = Pitch, Y = Yaw, Z = Roll) to a unit <see cref="Quaternion"/>.
        /// </summary>
        public static Quaternion FromEulerDegrees(Vector3 eulerDegrees) {
            const float degToRad = (float)(Math.PI / 180.0);
            float pitch = eulerDegrees.X * degToRad;
            float yaw = eulerDegrees.Y * degToRad;
            float roll = eulerDegrees.Z * degToRad;
            return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll));
        }

        /// <summary>
        /// Converts a unit <see cref="Quaternion"/> into Cavern Euler angles in degrees (X = Pitch, Y = Yaw, Z = Roll).
        /// </summary>
        public static Vector3 ToEulerDegrees(Quaternion q) {
            const float radToDeg = (float)(180.0 / Math.PI);
            Quaternion n = q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;

            float sinPitch = 2f * (n.W * n.X - n.Y * n.Z);
            if (sinPitch > 1f) {
                sinPitch = 1f;
            } else if (sinPitch < -1f) {
                sinPitch = -1f;
            }
            float pitch = MathF.Asin(sinPitch) * radToDeg;

            float sinYawCosPitch = 2f * (n.W * n.Y + n.Z * n.X);
            float cosYawCosPitch = 1f - 2f * (n.X * n.X + n.Y * n.Y);
            float yaw = MathF.Atan2(sinYawCosPitch, cosYawCosPitch) * radToDeg;

            float sinRollCosPitch = 2f * (n.W * n.Z + n.X * n.Y);
            float cosRollCosPitch = 1f - 2f * (n.X * n.X + n.Z * n.Z);
            float roll = MathF.Atan2(sinRollCosPitch, cosRollCosPitch) * radToDeg;

            return new Vector3(pitch, yaw, roll);
        }
    }
}
