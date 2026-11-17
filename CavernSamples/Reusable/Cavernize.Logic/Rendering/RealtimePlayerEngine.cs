using System.Numerics;

using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Listeners;
using Cavern.Utilities;
using Cavern.Virtualizer;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Real-time 3D spatial audio playback engine for <see cref="CavernizeTrack"/> with interactive room position,
/// listener head rotation, HRTF headphone virtualization, distance simulation, and per-source solo/mute.
/// </summary>
public sealed class RealtimePlayerEngine : IDisposable {
    const int updateRate = 256;
    const int framesPerChunk = 4; // 1024 samples per render block (~21.3 ms at 48 kHz)

    readonly object renderLock = new();

    CavernizeTrack currentTrack;
    UpmixingSettings upmixingSettings = new(true);
    RenderingSettings renderingSettings = new();
    Listener listener;
    Normalizer virtNormalizer;
    float[] stereoNormBuffer = [];
    bool virtualizerInitialized;

    /// <summary>
    /// Fired when non-looping playback reaches the end of the track.
    /// </summary>
    public event Action PlaybackCompleted;

    /// <summary>
    /// Active list of channels (beds) and dynamic 3D objects in the loaded track.
    /// </summary>
    public IReadOnlyList<TrackSourceItem> SourceItems => sourceItems;
    List<TrackSourceItem> sourceItems = [];

    /// <summary>
    /// Active output sample rate in Hz (48000 Hz when headphone virtualization is enabled).
    /// </summary>
    public int OutputSampleRate { get; private set; } = Listener.DefaultSampleRate;

    /// <summary>
    /// Total length of the loaded track in seconds.
    /// </summary>
    public double TotalSeconds => currentTrack != null && currentTrack.SampleRate > 0
        ? currentTrack.Length / (double)currentTrack.SampleRate
        : 0;

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    public double CurrentSeconds => currentTrack != null && currentTrack.SampleRate > 0
        ? currentTrackSample / (double)currentTrack.SampleRate
        : 0;

    long currentTrackSample;

    /// <summary>
    /// True when playback is currently active.
    /// </summary>
    public bool IsPlaying { get; set; }

    /// <summary>
    /// Restart playback automatically when reaching the end of the track.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// Enable Cavern's HRTF headphone spatial virtualizer for stereo output.
    /// </summary>
    public bool HeadphoneVirtualizer { get; set; } = true;

    /// <summary>
    /// Listener position inside the simulated room (in Cavern room coordinates, relative to <see cref="EnvironmentSize"/>).
    /// </summary>
    public Vector3 ListenerPosition { get; set; } = Vector3.Zero;

    /// <summary>
    /// Listener head rotation in Euler angles (degrees: X=Pitch, Y=Yaw, Z=Roll).
    /// </summary>
    public Vector3 ListenerRotation { get; set; } = Vector3.Zero;

    /// <summary>
    /// Physical half-dimensions of the simulated listening room.
    /// </summary>
    public Vector3 EnvironmentSize { get; set; } = new Vector3(10, 7, 10);

    /// <summary>
    /// Room shape/rendering mode.
    /// </summary>
    public Environments EnvironmentType { get; set; } = Environments.Home;

    /// <summary>
    /// Quality mode for spatial resampling and rendering.
    /// </summary>
    public QualityModes AudioQuality { get; set; } = QualityModes.High;

    /// <summary>
    /// Apply Cavern's HRTF ear-canal distance simulation (<see cref="Distancer"/>).
    /// </summary>
    public bool DistanceSimulation { get; set; } = true;

    /// <summary>
    /// Distance attenuation curve as listener moves relative to speakers/objects.
    /// </summary>
    public Rolloffs VolumeRolloff { get; set; } = Rolloffs.Disabled;

    /// <summary>
    /// Mute all fixed bed channels (render only dynamic 3D objects).
    /// </summary>
    public bool MuteBed { get; set; }

    /// <summary>
    /// Mute all ground-level sources (render only elevated height sources).
    /// </summary>
    public bool MuteGround { get; set; }

    /// <summary>
    /// Mute all dynamic 3D objects (render only fixed bed channels).
    /// </summary>
    public bool OnlyBeds { get; set; }

    /// <summary>
    /// Master volume multiplier.
    /// </summary>
    public float Volume { get; set; } = 1f;

    /// <summary>
    /// LFE channel volume multiplier.
    /// </summary>
    public float LFEVolume { get; set; } = 1f;

    /// <summary>
    /// Enable Cavern's dynamic normalizer/limiter.
    /// </summary>
    public bool NormalizerEnabled { get; set; } = true;

    /// <summary>
    /// When normalizing, never boost volume above 100%.
    /// </summary>
    public bool LimiterOnly { get; set; } = true;

    /// <summary>
    /// Loads a <see cref="CavernizeTrack"/> into the real-time spatial engine.
    /// </summary>
    public void Load(CavernizeTrack track, UpmixingSettings upmixing = null, RenderingSettings rendering = null) {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.Supported) {
            throw new NotSupportedException("Track format is not supported by Cavern.");
        }
        if (track.Codec == Codec.TrueHD) {
            throw new NotSupportedException("TrueHD MLP real-time playback requires an external decoder (truehdd).");
        }

        lock (renderLock) {
            IsPlaying = false;
            currentTrack = track;
            upmixingSettings = upmixing ?? new UpmixingSettings(true);
            renderingSettings = rendering ?? new RenderingSettings();
            currentTrackSample = 0;

            ConfigureListener(resetToStart: true);
            sourceItems = TrackSourceItem.BuildFromTrack(track);
        }
    }

    /// <summary>
    /// Updates source items if Solo/Mute changed externally or refreshes active settings.
    /// </summary>
    public void ClearSoloMute() {
        lock (renderLock) {
            foreach (TrackSourceItem item in sourceItems) {
                item.IsMuted = false;
                item.IsSolo = false;
            }
        }
    }

    /// <summary>
    /// Seeks playback to <paramref name="seconds"/>.
    /// </summary>
    public void Seek(double seconds) {
        lock (renderLock) {
            if (currentTrack == null) {
                return;
            }
            long targetSample = (long)Math.Round(seconds * currentTrack.SampleRate);
            currentTrackSample = currentTrack.Seek(targetSample);
            ConfigureListener(resetToStart: false);
        }
    }

    /// <summary>
    /// Renders the next block of stereo interleaved float samples into <paramref name="stereoOutput"/>.
    /// Returns the number of stereo sample frames written (0 if stopped or finished).
    /// </summary>
    public int RenderNextInterleavedBlock(float[] stereoOutput, int maxStereoFrames) {
        if (stereoOutput == null || maxStereoFrames <= 0) {
            return 0;
        }

        bool reachedEnd = false;
        int totalFramesWritten = 0;

        lock (renderLock) {
            if (!IsPlaying || currentTrack == null || listener == null) {
                Array.Clear(stereoOutput, 0, Math.Min(stereoOutput.Length, maxStereoFrames * 2));
                return 0;
            }

            ApplyLiveParameters();

            while (totalFramesWritten + updateRate <= maxStereoFrames) {
                if (currentTrack.Length > 0 && currentTrackSample >= currentTrack.Length) {
                    if (Loop) {
                        currentTrackSample = currentTrack.Seek(0);
                        ConfigureListener(resetToStart: false);
                        ApplyLiveParameters();
                    } else {
                        IsPlaying = false;
                        reachedEnd = true;
                        break;
                    }
                }

                int remainingFrames = maxStereoFrames - totalFramesWritten;
                int chunks = Math.Min(framesPerChunk, remainingFrames / updateRate);
                if (chunks <= 0) {
                    chunks = 1;
                }

                ApplySourceMutesAndProperties();

                float[] rendered = listener.Render(chunks);
                if (rendered == null) {
                    break;
                }

                int stride = Listener.Channels.Length;
                int blockFrames = chunks * updateRate;
                int outOffset = totalFramesWritten * 2;
                for (int s = 0; s < blockFrames; s++) {
                    float left = rendered[s * stride];
                    float right = stride > 1 ? rendered[s * stride + 1] : left;
                    stereoOutput[outOffset + s * 2] = float.IsFinite(left) ? left : 0f;
                    stereoOutput[outOffset + s * 2 + 1] = float.IsFinite(right) ? right : 0f;
                }

                if (HeadphoneVirtualizer && NormalizerEnabled && virtNormalizer != null) {
                    virtNormalizer.limiterOnly = LimiterOnly;
                    virtNormalizer.decayFactor = 10f * blockFrames / listener.SampleRate;
                    int sliceLen = blockFrames * 2;
                    if (stereoNormBuffer.Length != sliceLen) {
                        stereoNormBuffer = new float[sliceLen];
                    }
                    Array.Copy(stereoOutput, outOffset, stereoNormBuffer, 0, sliceLen);
                    virtNormalizer.Process(stereoNormBuffer);
                    Array.Copy(stereoNormBuffer, 0, stereoOutput, outOffset, sliceLen);
                }

                for (int i = 0; i < blockFrames * 2; i++) {
                    stereoOutput[outOffset + i] = Math.Clamp(stereoOutput[outOffset + i], -1f, 1f);
                }

                double sampleAdvance = blockFrames * (currentTrack.SampleRate / (double)listener.SampleRate);
                currentTrackSample += (long)Math.Round(sampleAdvance);
                totalFramesWritten += blockFrames;

                UpdateSourceTelemetry();
            }
        }

        if (reachedEnd) {
            PlaybackCompleted?.Invoke();
        }
        return totalFramesWritten;
    }

    void ConfigureListener(bool resetToStart) {
        Listener.EnvironmentType = EnvironmentType;
        Listener.EnvironmentSize = EnvironmentSize;
        Listener.HeadphoneVirtualizer = false;

        if (renderingSettings.RoomCorrectionUsable && !HeadphoneVirtualizer) {
            listener = new ConvolvedListener(false) {
                ConvolutionClip = renderingSettings.RoomCorrection,
                UpdateRate = updateRate,
                AudioQuality = AudioQuality,
                MaximumSources = 256
            };
        } else {
            listener = new Listener(false) {
                UpdateRate = updateRate,
                AudioQuality = AudioQuality,
                MaximumSources = 256
            };
        }

        if (resetToStart) {
            currentTrack.Attach(listener, upmixingSettings);
        } else {
            currentTrack.AttachFromCurrentPosition(listener, upmixingSettings);
        }

        SetupOutputLayout();
    }

    void SetupOutputLayout() {
        if (HeadphoneVirtualizer) {
            Listener.HeadphoneVirtualizer = true;
            new VirtualizerFilter().SetLayout();
            listener.SampleRate = VirtualizerFilter.FilterSampleRate;
            virtNormalizer = new Normalizer(LimiterOnly) {
                decayFactor = 10f * updateRate / listener.SampleRate
            };
            virtualizerInitialized = true;
        } else {
            Listener.HeadphoneVirtualizer = false;
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref200));
            listener.SampleRate = renderingSettings.RoomCorrectionUsable
                ? renderingSettings.RoomCorrection.SampleRate
                : Math.Max(44100, currentTrack.SampleRate);
            virtualizerInitialized = false;
        }
        OutputSampleRate = listener.SampleRate;
    }

    void ApplyLiveParameters() {
        Listener.EnvironmentType = EnvironmentType;
        if (EnvironmentSize.X > 0.1f && EnvironmentSize.Y > 0.1f && EnvironmentSize.Z > 0.1f) {
            Listener.EnvironmentSize = EnvironmentSize;
        }

        if (HeadphoneVirtualizer != virtualizerInitialized) {
            SetupOutputLayout();
        }

        listener.Position = ListenerPosition;
        listener.Rotation = ListenerRotation;
        listener.AudioQuality = AudioQuality;
        listener.Range = Math.Max(50f, EnvironmentSize.Length() * 5f);

        float safeGain = currentTrack.Renderer.HasObjects ? 0.707f : 1f;
        listener.Volume = Volume * (renderingSettings?.Gain ?? 1f) * safeGain;
        listener.LFEVolume = LFEVolume;
        listener.Normalizer = NormalizerEnabled ? 1f : 0f;
        listener.LimiterOnly = LimiterOnly;
    }

    void ApplySourceMutesAndProperties() {
        Listener.HeadphoneVirtualizer = HeadphoneVirtualizer;

        IReadOnlyList<Source> rendererObjects = currentTrack.Renderer.Objects;
        bool anySolo = false;
        for (int i = 0; i < sourceItems.Count; i++) {
            if (sourceItems[i].IsSolo) {
                anySolo = true;
                break;
            }
        }

        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        if (listener.ActiveSources.Count == rendererObjects.Count && sourceItems.Count == rendererObjects.Count) {
            for (int i = 0; i < rendererObjects.Count; i++) {
                Source src = rendererObjects[i];
                TrackSourceItem item = sourceItems[i];
                Vector3 rawPos = src.Position * invEnv;

                bool mute = item.IsMuted || (anySolo && !item.IsSolo);
                if (MuteBed && item.IsBed) {
                    mute = true;
                }
                if (OnlyBeds && !item.IsBed) {
                    mute = true;
                }
                if (MuteGround && MathF.Abs(rawPos.Y) < 0.05f) {
                    mute = true;
                }

                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        } else {
            foreach (Source src in listener.ActiveSources) {
                Vector3 rawPos = src.Position * invEnv;
                bool isBedPos = MathF.Abs(rawPos.X) % 1 < .01f && MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z) % 1 < .01f;
                bool mute = (MuteBed && isBedPos) || (OnlyBeds && !isBedPos) || (MuteGround && MathF.Abs(rawPos.Y) < .05f);
                src.Mute = mute;
                src.DistanceSimulation = DistanceSimulation && HeadphoneVirtualizer;
                src.VolumeRolloff = VolumeRolloff;
            }
        }
    }

    void UpdateSourceTelemetry() {
        IReadOnlyList<Source> rendererObjects = currentTrack?.Renderer?.Objects;
        if (rendererObjects == null) {
            return;
        }
        Vector3 invEnv = Vector3.One / Listener.EnvironmentSize;
        int count = Math.Min(sourceItems.Count, rendererObjects.Count);
        for (int i = 0; i < count; i++) {
            Source src = rendererObjects[i];
            sourceItems[i].NormalizedPosition = src.Position * invEnv;
            float[] rendered = src.Rendered?[0];
            if (rendered != null && rendered.Length > 0 && !src.Mute) {
                double sumSq = 0;
                int step = Math.Max(1, rendered.Length / 16);
                int samples = 0;
                for (int s = 0; s < rendered.Length; s += step) {
                    float v = rendered[s];
                    sumSq += v * v;
                    samples++;
                }
                sourceItems[i].RmsLevel = MathF.Sqrt((float)(sumSq / Math.Max(1, samples)));
            } else {
                sourceItems[i].RmsLevel = 0f;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (renderLock) {
            IsPlaying = false;
            listener?.DetachAllSources();
            listener = null;
            currentTrack = null;
        }
        GC.SuppressFinalize(this);
    }
}
