using System.Numerics;

using Cavern.ProAudio;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Zero-GC Slot 1 live microphone bridge coupling full-duplex USB UAC2 / AAudio capture threads
/// to <see cref="Rendering.RealtimePlayerEngine"/> and <see cref="SpatialSceneHub"/> (<see cref="SourceLayer.LiveMicrophone"/>).
/// Includes a lock-free SPSC ring buffer, channel routing (Mono Ch1, Mono Ch2, Stereo Pair), trim gain,
/// live peak/RMS dBFS metering, and a pure-physics MLS loopback round-trip latency calibration state machine.
/// </summary>
public sealed class LiveMicrophoneBridge : ILiveMicrophoneProvider {
    const int RingCapacityFrames = 8192; // ~170 ms @ 48 kHz; power-of-2 lock-free headroom
    const int MaxScratchFrames = 2048;

    readonly LockFreeAudioRingBuffer captureRing = new(RingCapacityFrames, 2);
    readonly float[] producerStereoScratch = new float[MaxScratchFrames * 2];
    readonly float[] consumerStereoScratch = new float[MaxScratchFrames * 2];

    // Pure-physics MLS loopback RTT calibration buffers (allocated only when user triggers calibration)
    readonly object calibLock = new();
    float[] calibProbe;
    float[] calibCaptureBuffer;
    int calibEmitCursor;
    int calibCaptureCursor;
    int calibSampleRate = 48000;
    volatile bool calibActive;
    Action<RoundTripLatencyResult> calibCompletedCallback;

    volatile bool isActive;
    volatile int sampleRate = 48000;
    volatile LiveMicRoutingMode routingMode = LiveMicRoutingMode.MonoChannel1;
    volatile float inputGainLinear = 1f;
    volatile float monitorLevel = 0.85f;
    volatile float rmsDbfs = -120f;
    volatile float peakDbfs = -120f;
    volatile float latestRttMs;
    Vector3 normalizedPosition = new(0f, 0.05f, 0.72f);

    /// <inheritdoc/>
    public bool IsActive => isActive;

    /// <inheritdoc/>
    public int SampleRate => sampleRate;

    /// <inheritdoc/>
    public Vector3 NormalizedPosition => normalizedPosition;

    /// <inheritdoc/>
    public float RmsDbfs => rmsDbfs;

    /// <inheritdoc/>
    public float PeakDbfs => peakDbfs;

    /// <summary>Active monitoring mix level in [0; 1.25].</summary>
    public float MonitorLevel => monitorLevel;

    /// <summary>True while an MLS loopback round-trip latency measurement is in progress.</summary>
    public bool IsCalibratingLatency => calibActive;

    /// <summary>Latest measured or estimated full-duplex round-trip latency in milliseconds.</summary>
    public float RoundTripLatencyMs {
        get => latestRttMs;
        set => latestRttMs = Math.Clamp(value, 0f, 500f);
    }

    /// <summary>Cumulative SPSC capture ring overruns.</summary>
    public long CaptureOverruns => captureRing.OverrunCount;

    /// <summary>Cumulative SPSC capture ring underruns.</summary>
    public long CaptureUnderruns => captureRing.UnderrunCount;

    /// <summary>Number of stereo mic frames currently queued in the lock-free capture ring.</summary>
    public int BufferedFrames => captureRing.AvailableFramesToRead;

    /// <summary>
    /// Applies updated W10 Pro-Audio live microphone configuration without heap allocation on the audio thread.
    /// </summary>
    public void ApplyConfig(ProAudioDuplexConfig config, int activeSampleRate) {
        ProAudioDuplexConfig safe = (config ?? ProAudioDuplexConfig.Default).Sanitize();
        sampleRate = Math.Clamp(activeSampleRate, 44100, 192000);
        routingMode = safe.MicRouting;
        inputGainLinear = MathF.Pow(10f, safe.MicInputGainDb / 20f);
        monitorLevel = safe.MicMonitorLevel;
        normalizedPosition = safe.MicNormalizedPosition;
        if (safe.CalibratedRoundTripMs > 0f) {
            latestRttMs = safe.CalibratedRoundTripMs;
        }
        bool shouldBeActive = safe.Enabled && safe.LiveMicEnabled && safe.MicRouting != LiveMicRoutingMode.Off;
        if (!shouldBeActive) {
            isActive = false;
            rmsDbfs = -120f;
            peakDbfs = -120f;
        }
    }

    /// <summary>
    /// Updates the live microphone normalized 3D room position [-1; 1] immediately when dragged inside the 6DoF Room.
    /// </summary>
    public void SetNormalizedPosition(Vector3 normPos) {
        normalizedPosition = new Vector3(
            Math.Clamp(normPos.X, -1f, 1f),
            Math.Clamp(normPos.Y, -1f, 1f),
            Math.Clamp(normPos.Z, -1f, 1f));
    }

    /// <summary>
    /// Marks the live microphone transport active/inactive and flushes stale frames on startup.
    /// </summary>
    public void SetStreamingActive(bool active, int activeSampleRate) {
        sampleRate = Math.Clamp(activeSampleRate, 44100, 192000);
        if (active && !isActive) {
            captureRing.Reset();
        }
        isActive = active;
        if (!active) {
            rmsDbfs = -120f;
            peakDbfs = -120f;
        }
    }

    /// <summary>
    /// Drains excess startup backlog so the live microphone stays tightly sample-locked to playback (&lt;= 2 bursts).
    /// </summary>
    public void TrimLatencyBacklog(int maxQueuedFrames) {
        int clampedMax = Math.Max(64, maxQueuedFrames);
        int avail = captureRing.AvailableFramesToRead;
        if (avail > clampedMax) {
            captureRing.Skip(avail - clampedMax);
        }
    }

    /// <summary>
    /// Begins a pure-physics 9-bit MLS loopback round-trip latency calibration (~300 ms capture window).
    /// </summary>
    public void StartRoundTripCalibration(int activeSampleRate, Action<RoundTripLatencyResult> onCompleted) {
        int sr = Math.Clamp(activeSampleRate, 44100, 192000);
        int captureFrames = Math.Clamp((int)Math.Round(sr * 0.35), 8192, 96000);
        lock (calibLock) {
            calibSampleRate = sr;
            calibProbe = RoundTripLatencyCalibrator.GenerateMlsProbe(RoundTripLatencyCalibrator.DefaultProbeAmplitude);
            calibCaptureBuffer = new float[captureFrames];
            calibEmitCursor = 0;
            calibCaptureCursor = 0;
            calibCompletedCallback = onCompleted;
            calibActive = true;
        }
    }

    /// <summary>
    /// Cancels any active MLS loopback round-trip latency calibration.
    /// </summary>
    public void CancelRoundTripCalibration() {
        lock (calibLock) {
            calibActive = false;
            calibCompletedCallback = null;
        }
    }

    /// <inheritdoc/>
    public int PushCapturedInterleaved(float[] interleavedInput, int offset, int frameCount, int inputChannels) {
        if (interleavedInput == null || frameCount <= 0 || inputChannels <= 0) {
            return 0;
        }

        int writtenTotal = 0;
        int remaining = frameCount;
        int srcFrameOffset = 0;
        LiveMicRoutingMode mode = routingMode;
        float gain = inputGainLinear;

        float blockPeak = 0f;
        double blockSqSum = 0.0;
        int meteredSamples = 0;

        while (remaining > 0) {
            int chunk = Math.Min(remaining, MaxScratchFrames);
            for (int f = 0; f < chunk; f++) {
                int baseIdx = offset + (srcFrameOffset + f) * inputChannels;
                float ch0 = baseIdx < interleavedInput.Length ? interleavedInput[baseIdx] : 0f;
                float ch1 = (inputChannels >= 2 && baseIdx + 1 < interleavedInput.Length) ? interleavedInput[baseIdx + 1] : ch0;

                float outL;
                float outR;
                switch (mode) {
                    case LiveMicRoutingMode.MonoChannel1:
                        outL = ch0 * gain;
                        outR = outL;
                        break;
                    case LiveMicRoutingMode.MonoChannel2:
                        outL = ch1 * gain;
                        outR = outL;
                        break;
                    case LiveMicRoutingMode.StereoPair:
                        outL = ch0 * gain;
                        outR = ch1 * gain;
                        break;
                    default:
                        outL = 0f;
                        outR = 0f;
                        break;
                }

                producerStereoScratch[f * 2] = outL;
                producerStereoScratch[f * 2 + 1] = outR;

                float absL = MathF.Abs(outL);
                float absR = MathF.Abs(outR);
                float p = absL > absR ? absL : absR;
                if (p > blockPeak) {
                    blockPeak = p;
                }
                blockSqSum += 0.5 * (outL * outL + outR * outR);
                meteredSamples++;

                if (calibActive) {
                    RecordCalibrationSample(0.5f * (ch0 + ch1));
                }
            }

            if (isActive && mode != LiveMicRoutingMode.Off) {
                writtenTotal += captureRing.Write(producerStereoScratch, 0, chunk);
            }
            srcFrameOffset += chunk;
            remaining -= chunk;
        }

        if (meteredSamples > 0) {
            float rms = MathF.Sqrt((float)(blockSqSum / meteredSamples));
            rmsDbfs = rms > 1e-6f ? Math.Clamp(20f * MathF.Log10(rms), -120f, 0f) : -120f;
            peakDbfs = blockPeak > 1e-6f ? Math.Clamp(20f * MathF.Log10(blockPeak), -120f, 0f) : -120f;
        }

        return writtenTotal;
    }

    /// <inheritdoc/>
    public int PullRoutedStereoBlock(float[] stereoOut, int offset, int frameCount) {
        if (stereoOut == null || frameCount <= 0 || !isActive || routingMode == LiveMicRoutingMode.Off) {
            return 0;
        }
        return captureRing.Read(stereoOut, offset, frameCount, zeroFillMissing: true);
    }

    /// <summary>
    /// Mixes the live microphone stream sample-locked into <paramref name="stereoOutput"/> with 3D azimuth panning
    /// relative to <paramref name="listenerPose"/> (or straight stereo when <see cref="LiveMicRoutingMode.StereoPair"/> is active).
    /// Also injects the MLS loopback calibration probe cleanly when calibration is armed.
    /// Zero-GC on the real-time audio thread.
    /// </summary>
    public bool MixIntoStereoOutputZeroGc(
        float[] stereoOutput,
        int outOffset,
        int frameCount,
        ListenerPose6DoF listenerPose) {
        if (stereoOutput == null || frameCount <= 0) {
            return false;
        }

        // Priority 1: MLS Loopback Calibration probe replaces output cleanly while measuring hardware RTT
        if (calibActive) {
            EmitCalibrationBlock(stereoOutput, outOffset, frameCount);
            return true;
        }

        if (!isActive || routingMode == LiveMicRoutingMode.Off || monitorLevel <= 1e-4f) {
            return false;
        }

        // Keep capture backlog tightly bounded (< 3 blocks) to guarantee sample-locked Karaoke monitoring
        TrimLatencyBacklog(Math.Max(256, frameCount * 2));

        // Compute equal-power 3D azimuth panning from live mic room position relative to head-tracked listener orientation
        float gainL;
        float gainR;
        if (routingMode == LiveMicRoutingMode.StereoPair) {
            gainL = monitorLevel;
            gainR = monitorLevel;
        } else {
            Vector3 rel = normalizedPosition - listenerPose.Position;
            Quaternion invOrient = Quaternion.Conjugate(listenerPose.Orientation);
            Vector3 headLocal = Vector3.Transform(rel, invOrient);
            float horiz = MathF.Sqrt(headLocal.X * headLocal.X + headLocal.Z * headLocal.Z);
            float pan = horiz > 1e-4f ? Math.Clamp(headLocal.X / horiz, -1f, 1f) : 0f;
            float angle = (pan + 1f) * (MathF.PI * 0.25f); // [0; pi/2] equal-power pan law
            gainL = monitorLevel * MathF.Cos(angle);
            gainR = monitorLevel * MathF.Sin(angle);
        }

        int done = 0;
        while (done < frameCount) {
            int chunk = Math.Min(frameCount - done, MaxScratchFrames);
            int read = captureRing.Read(consumerStereoScratch, 0, chunk, zeroFillMissing: true);
            if (read <= 0 && captureRing.AvailableFramesToRead == 0) {
                break;
            }
            for (int f = 0; f < chunk; f++) {
                int dst = outOffset + (done + f) * 2;
                float micL = consumerStereoScratch[f * 2];
                float micR = consumerStereoScratch[f * 2 + 1];
                if (routingMode == LiveMicRoutingMode.StereoPair) {
                    stereoOutput[dst] += micL * gainL;
                    stereoOutput[dst + 1] += micR * gainR;
                } else {
                    float mono = 0.5f * (micL + micR);
                    stereoOutput[dst] += mono * gainL;
                    stereoOutput[dst + 1] += mono * gainR;
                }
            }
            done += chunk;
        }

        return done > 0;
    }

    /// <summary>
    /// Builds a <see cref="SpatialSourceDescriptor"/> representing this live microphone inside <see cref="SpatialSceneHub"/>
    /// (<see cref="SourceLayer.LiveMicrophone"/>).
    /// </summary>
    public SpatialSourceDescriptor BuildSceneDescriptor(Vector3 environmentSize, int index, string label = "Live Mic") {
        float linRms = rmsDbfs > -119f ? MathF.Pow(10f, rmsDbfs / 20f) : 0f;
        float linPeak = peakDbfs > -119f ? MathF.Pow(10f, peakDbfs / 20f) : 0f;
        return new SpatialSourceDescriptor {
            Index = index,
            Name = label,
            Layer = SourceLayer.LiveMicrophone,
            IsLfe = false,
            IsDialogue = true,
            NormalizedPosition = normalizedPosition,
            WorldPositionMeters = normalizedPosition * environmentSize,
            Gain = monitorLevel,
            Diffuse = 0f,
            Divergence = 0f,
            Importance = 1f,
            RmsLevel = linRms,
            PeakLevel = linPeak
        };
    }

    void EmitCalibrationBlock(float[] stereoOutput, int outOffset, int frameCount) {
        Array.Clear(stereoOutput, outOffset, frameCount * 2);
        lock (calibLock) {
            if (!calibActive || calibProbe == null) {
                return;
            }
            for (int f = 0; f < frameCount; f++) {
                float s = calibEmitCursor < calibProbe.Length ? calibProbe[calibEmitCursor] : 0f;
                calibEmitCursor++;
                stereoOutput[outOffset + f * 2] = s;
                stereoOutput[outOffset + f * 2 + 1] = s;
            }
        }
    }

    void RecordCalibrationSample(float sample) {
        RoundTripLatencyResult? finished = null;
        Action<RoundTripLatencyResult> cb = null;
        lock (calibLock) {
            if (!calibActive || calibCaptureBuffer == null || calibProbe == null) {
                return;
            }
            if (calibCaptureCursor < calibCaptureBuffer.Length) {
                calibCaptureBuffer[calibCaptureCursor++] = sample;
            }
            if (calibCaptureCursor >= calibCaptureBuffer.Length) {
                calibActive = false;
                int maxLag = Math.Min(calibCaptureBuffer.Length - calibProbe.Length - 1, calibSampleRate / 4);
                RoundTripLatencyResult result = RoundTripLatencyCalibrator.MeasureRoundTripLatency(
                    calibProbe,
                    calibCaptureBuffer,
                    calibSampleRate,
                    minLagSamples: 16,
                    maxLagSamples: Math.Max(64, maxLag));
                if (result.IsValid) {
                    latestRttMs = (float)result.LatencyMs;
                }
                finished = result;
                cb = calibCompletedCallback;
                calibCompletedCallback = null;
            }
        }
        if (finished.HasValue && cb != null) {
            RoundTripLatencyResult val = finished.Value;
            _ = Task.Run(() => cb(val));
        }
    }
}
