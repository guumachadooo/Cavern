using System;
using System.Diagnostics;
using System.Numerics;

namespace Cavern.Spatial {
    /// <summary>
    /// Thread-safe, zero-GC short-horizon 3DoF/6DoF head-pose processor and latency predictor.
    /// Applies tare/recenter calibration, optional gentle auto-recentering toward sustained gaze,
    /// exponentially filtered angular velocity &amp; damped acceleration estimation, and Lie-group
    /// quaternion exponential extrapolation (<c>q * exp(0.5 * (w*dt + 0.5*a*dt^2))</c>) to compensate
    /// Bluetooth/audio output buffer latency without overshoot or gimbal lock.
    /// </summary>
    public sealed class HeadPosePredictor {
        const float MaxPredictionSeconds = 0.120f;
        const float MaxAngularSpeedRadPerSec = 12f;
        const float MaxAngularAccelRadPerSecSq = 40f;
        const float VelocityDeadbandRadPerSec = 0.025f;
        const float AutoRecenterMotionThresholdRadPerSec = 0.14f; // ~8 deg/s
        const float AccelerationDamping = 0.35f;

        readonly object syncLock = new object();

        Quaternion rawOrientation = Quaternion.Identity;
        Quaternion referenceOrientation = Quaternion.Identity;
        Quaternion relativeOrientation = Quaternion.Identity;
        Quaternion previousRelativeOrientation = Quaternion.Identity;

        Vector3 position = Vector3.Zero;
        Vector3 angularVelocityRadPerSec = Vector3.Zero;
        Vector3 angularAccelRadPerSecSq = Vector3.Zero;
        long lastSampleTicks;
        bool hasSample;
        bool hasReference;
        float stationarySeconds;

        /// <summary>
        /// Slowly drifts the forward reference toward the listener's sustained head direction when resting still.
        /// </summary>
        public bool AutoRecenterEnabled { get; set; } = true;

        /// <summary>
        /// Time constant (seconds) of the slow auto-recenter exponential drift (default 14 seconds).
        /// </summary>
        public float AutoRecenterTimeConstantSeconds { get; set; } = 14f;

        /// <summary>
        /// Removes head tilt (Z roll) from the output pose when enabled, keeping horizon level.
        /// </summary>
        public bool LockRoll { get; set; }

        /// <summary>
        /// Smoothing factor (0..1) for angular velocity EMA used in latency extrapolation (default 0.38).
        /// </summary>
        public float VelocitySmoothingAlpha { get; set; } = 0.38f;

        /// <summary>
        /// Resets orientation, reference frame, and angular derivatives.
        /// </summary>
        public void Reset() {
            lock (syncLock) {
                rawOrientation = Quaternion.Identity;
                referenceOrientation = Quaternion.Identity;
                relativeOrientation = Quaternion.Identity;
                previousRelativeOrientation = Quaternion.Identity;
                position = Vector3.Zero;
                angularVelocityRadPerSec = Vector3.Zero;
                angularAccelRadPerSecSq = Vector3.Zero;
                lastSampleTicks = 0L;
                hasSample = false;
                hasReference = false;
                stationarySeconds = 0f;
            }
        }

        /// <summary>
        /// Captures the latest raw head orientation as the new 0° forward reference (tare).
        /// </summary>
        public void Recenter() {
            lock (syncLock) {
                if (hasSample) {
                    referenceOrientation = rawOrientation;
                    hasReference = true;
                    relativeOrientation = Quaternion.Identity;
                    previousRelativeOrientation = Quaternion.Identity;
                    angularVelocityRadPerSec = Vector3.Zero;
                    angularAccelRadPerSecSq = Vector3.Zero;
                    stationarySeconds = 0f;
                } else {
                    hasReference = false;
                }
            }
        }

        /// <summary>
        /// Ingests a new sensor orientation quaternion (in Cavern coordinates) with optional measured gyroscope angular velocity
        /// and optional 6DoF room position. Lock-held work is constant-time and zero-allocation.
        /// </summary>
        public void PushSample(
            Quaternion measuredOrientation,
            Vector3? measuredAngularVelocityRadPerSec,
            Vector3 newPosition,
            long timestampTicks) {
            if (!IsFinite(measuredOrientation) || measuredOrientation.LengthSquared() < 1e-8f) {
                return;
            }

            Quaternion qRaw = Quaternion.Normalize(measuredOrientation);
            if (qRaw.W < 0f) {
                qRaw = new Quaternion(-qRaw.X, -qRaw.Y, -qRaw.Z, -qRaw.W);
            }

            long nowTicks = timestampTicks > 0L ? timestampTicks : Stopwatch.GetTimestamp();

            lock (syncLock) {
                position = IsFinite(newPosition) ? newPosition : Vector3.Zero;
                rawOrientation = qRaw;

                if (!hasReference) {
                    referenceOrientation = qRaw;
                    hasReference = true;
                }

                float dt = 0f;
                if (hasSample && lastSampleTicks > 0L && nowTicks > lastSampleTicks) {
                    dt = (float)((nowTicks - lastSampleTicks) / (double)Stopwatch.Frequency);
                    if (dt > 0.25f) {
                        dt = 0f;
                    }
                }

                Quaternion qRel = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Conjugate(referenceOrientation), qRaw));
                if (qRel.W < 0f) {
                    qRel = new Quaternion(-qRel.X, -qRel.Y, -qRel.Z, -qRel.W);
                }

                Vector3 instOmega;
                if (measuredAngularVelocityRadPerSec.HasValue && IsFinite(measuredAngularVelocityRadPerSec.Value)) {
                    instOmega = measuredAngularVelocityRadPerSec.Value;
                } else if (hasSample && dt > 1e-4f) {
                    instOmega = ComputeAngularVelocity(previousRelativeOrientation, qRel, dt);
                } else {
                    instOmega = Vector3.Zero;
                }

                // Clamp physical head-turn speed ceiling and suppress micro-jitter deadband
                float speed = instOmega.Length();
                if (speed < VelocityDeadbandRadPerSec) {
                    instOmega = Vector3.Zero;
                } else if (speed > MaxAngularSpeedRadPerSec) {
                    instOmega *= MaxAngularSpeedRadPerSec / speed;
                }

                Vector3 prevOmega = angularVelocityRadPerSec;
                float alpha = Clamp(VelocitySmoothingAlpha, 0.05f, 1f);
                angularVelocityRadPerSec = hasSample && dt > 0f
                    ? prevOmega + (instOmega - prevOmega) * alpha
                    : instOmega;

                if (hasSample && dt > 1e-4f) {
                    Vector3 rawAccel = (angularVelocityRadPerSec - prevOmega) / dt;
                    float accelMag = rawAccel.Length();
                    if (accelMag > MaxAngularAccelRadPerSecSq) {
                        rawAccel *= MaxAngularAccelRadPerSecSq / accelMag;
                    }
                    angularAccelRadPerSecSq += (rawAccel - angularAccelRadPerSecSq) * 0.25f;
                } else {
                    angularAccelRadPerSecSq = Vector3.Zero;
                }

                // Gentle auto-recenter when listener holds head steady facing a new direction
                if (AutoRecenterEnabled && dt > 0f) {
                    if (angularVelocityRadPerSec.Length() < AutoRecenterMotionThresholdRadPerSec) {
                        stationarySeconds += dt;
                        if (stationarySeconds >= 1.5f) {
                            float tau = Math.Max(2f, AutoRecenterTimeConstantSeconds);
                            float blend = 1f - (float)Math.Exp(-dt / tau);
                            referenceOrientation = Quaternion.Normalize(Quaternion.Slerp(referenceOrientation, qRaw, blend));
                            qRel = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Conjugate(referenceOrientation), qRaw));
                            if (qRel.W < 0f) {
                                qRel = new Quaternion(-qRel.X, -qRel.Y, -qRel.Z, -qRel.W);
                            }
                        }
                    } else {
                        stationarySeconds = 0f;
                    }
                }

                previousRelativeOrientation = qRel;
                relativeOrientation = qRel;
                lastSampleTicks = nowTicks;
                hasSample = true;
            }
        }

        /// <summary>
        /// Evaluates the current listener pose extrapolated forward by <paramref name="predictionHorizonMs"/>
        /// plus sample age (capped to 120 ms total look-ahead) for low-latency audio-thread consumption.
        /// </summary>
        public ListenerPose6DoF PredictPose(float predictionHorizonMs, bool isTrackingActive, long nowTicks = 0L) {
            Quaternion qBase;
            Vector3 pos;
            Vector3 omega;
            Vector3 accel;
            long sampleTicks;
            bool valid;

            lock (syncLock) {
                qBase = relativeOrientation;
                pos = position;
                omega = angularVelocityRadPerSec;
                accel = angularAccelRadPerSecSq;
                sampleTicks = lastSampleTicks;
                valid = hasSample;
            }

            if (!valid) {
                return ListenerPose6DoF.Identity;
            }

            long queryTicks = nowTicks > 0L ? nowTicks : Stopwatch.GetTimestamp();
            float ageSec = sampleTicks > 0L && queryTicks > sampleTicks
                ? (float)((queryTicks - sampleTicks) / (double)Stopwatch.Frequency)
                : 0f;

            // Decay velocity smoothly if sensor stream stalls (> 80 ms without packets)
            if (ageSec > 0.08f) {
                float stallDecay = (float)Math.Exp(-(ageSec - 0.08f) * 8f);
                omega *= stallDecay;
                accel *= stallDecay;
                ageSec = 0.08f;
            }

            float extraSec = Clamp(predictionHorizonMs * 0.001f, 0f, MaxPredictionSeconds);
            float totalHorizonSec = Clamp(ageSec + extraSec, 0f, MaxPredictionSeconds);

            Quaternion qPred = ExtrapolateQuaternion(qBase, omega, accel, totalHorizonSec);
            Vector3 euler = ListenerPose6DoF.ToEulerDegrees(qPred);
            if (LockRoll && Math.Abs(euler.Z) > 1e-3f) {
                euler = new Vector3(euler.X, euler.Y, 0f);
                qPred = ListenerPose6DoF.FromEulerDegrees(euler);
            }

            return new ListenerPose6DoF(
                pos,
                qPred,
                euler,
                omega,
                queryTicks,
                totalHorizonSec * 1000f,
                isTrackingActive);
        }

        /// <summary>
        /// Extrapolates unit quaternion <paramref name="q"/> forward by <paramref name="dtSeconds"/> using exact SO(3)
        /// exponential map <c>q * exp(0.5 * (omega * dt + 0.5 * dampedAccel * dt^2))</c>.
        /// </summary>
        public static Quaternion ExtrapolateQuaternion(
            Quaternion q,
            Vector3 omegaRadPerSec,
            Vector3 angularAccelRadPerSecSq,
            float dtSeconds) {
            if (dtSeconds <= 1e-6f) {
                return q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
            }

            Vector3 deltaTheta = omegaRadPerSec * dtSeconds +
                angularAccelRadPerSecSq * (0.5f * AccelerationDamping * dtSeconds * dtSeconds);
            float angle = deltaTheta.Length();
            if (angle < 1e-6f) {
                return q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
            }

            // Cap maximum single-step lookahead angle to 35 degrees to guarantee stability
            const float maxStepRad = 0.61086524f;
            if (angle > maxStepRad) {
                deltaTheta *= maxStepRad / angle;
                angle = maxStepRad;
            }

            float halfAngle = 0.5f * angle;
            float sinHalfOverAngle = (float)Math.Sin(halfAngle) / angle;
            Quaternion deltaQ = new Quaternion(
                deltaTheta.X * sinHalfOverAngle,
                deltaTheta.Y * sinHalfOverAngle,
                deltaTheta.Z * sinHalfOverAngle,
                (float)Math.Cos(halfAngle));

            Quaternion result = Quaternion.Multiply(q, deltaQ);
            return result.LengthSquared() > 1e-8f ? Quaternion.Normalize(result) : Quaternion.Identity;
        }

        /// <summary>
        /// Computes body-frame angular velocity vector (rad/s) between two consecutive unit quaternions over <paramref name="dtSeconds"/>.
        /// </summary>
        public static Vector3 ComputeAngularVelocity(Quaternion qPrev, Quaternion qCurr, float dtSeconds) {
            if (dtSeconds <= 1e-6f) {
                return Vector3.Zero;
            }

            Quaternion dq = Quaternion.Multiply(Quaternion.Conjugate(qPrev), qCurr);
            if (dq.W < 0f) {
                dq = new Quaternion(-dq.X, -dq.Y, -dq.Z, -dq.W);
            }

            float sinHalf = (float)Math.Sqrt(dq.X * dq.X + dq.Y * dq.Y + dq.Z * dq.Z);
            if (sinHalf < 1e-6f) {
                return Vector3.Zero;
            }

            float angle = 2f * (float)Math.Atan2(sinHalf, dq.W);
            float scale = angle / (sinHalf * dtSeconds);
            return new Vector3(dq.X * scale, dq.Y * scale, dq.Z * scale);
        }

        static float Clamp(float value, float min, float max) {
            if (value < min) {
                return min;
            }
            return value > max ? max : value;
        }

        static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
            !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
            !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);

        static bool IsFinite(Quaternion q) =>
            !float.IsNaN(q.X) && !float.IsInfinity(q.X) &&
            !float.IsNaN(q.Y) && !float.IsInfinity(q.Y) &&
            !float.IsNaN(q.Z) && !float.IsInfinity(q.Z) &&
            !float.IsNaN(q.W) && !float.IsInfinity(q.W);
    }
}
