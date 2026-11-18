using Android.Media;

using Cavernize.Logic.Rendering;

using AudioEncoding = Android.Media.Encoding;

namespace CavernizeAndroid.Audio;

/// <summary>
/// Streams real-time stereo PCM float samples from <see cref="RealtimePlayerEngine"/> to Android's <see cref="AudioTrack"/>.
/// </summary>
public sealed class AndroidAudioOutput : IDisposable {
    const int FramesPerWrite = 1024; // Must be a multiple of RealtimePlayerEngine's updateRate (256)

    readonly RealtimePlayerEngine engine;
    readonly object lifecycleLock = new();

    Thread renderThread;
    volatile bool running;
    AudioTrack audioTrack;
    int activeSampleRate;

    public AndroidAudioOutput(RealtimePlayerEngine engine) => this.engine = engine;

    /// <summary>
    /// Starts the audio output thread if not already running.
    /// </summary>
    public void Start() {
        lock (lifecycleLock) {
            if (running) {
                return;
            }
            running = true;
            renderThread = new Thread(AudioLoop) {
                Name = "CavernizeAudioOut",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest
            };
            renderThread.Start();
        }
    }

    /// <summary>
    /// Pauses playback hardware cleanly.
    /// </summary>
    public void Pause() {
        lock (lifecycleLock) {
            try {
                audioTrack?.Pause();
            } catch {
                // Ignore if uninitialized
            }
        }
    }

    /// <summary>
    /// Stops and tears down the audio output thread and <see cref="AudioTrack"/>.
    /// </summary>
    public void Stop() {
        Thread threadToJoin;
        lock (lifecycleLock) {
            running = false;
            threadToJoin = renderThread;
            renderThread = null;
        }

        if (threadToJoin != null && threadToJoin.IsAlive && threadToJoin != Thread.CurrentThread) {
            threadToJoin.Join(1000);
        }

        lock (lifecycleLock) {
            ReleaseTrack();
        }
    }

    void AudioLoop() {
        float[] buffer = new float[FramesPerWrite * 2];
        while (running) {
            if (!engine.IsPlaying) {
                if (audioTrack != null && audioTrack.PlayState == PlayState.Playing) {
                    try {
                        audioTrack.Pause();
                    } catch {
                        // Ignore
                    }
                }
                Thread.Sleep(15);
                continue;
            }

            int targetRate = engine.OutputSampleRate;
            if (audioTrack == null || activeSampleRate != targetRate) {
                RecreateTrack(targetRate);
            }

            if (audioTrack != null && audioTrack.PlayState != PlayState.Playing) {
                try {
                    audioTrack.Play();
                } catch {
                    Thread.Sleep(20);
                    continue;
                }
            }

            int frames = engine.RenderNextInterleavedBlock(buffer, FramesPerWrite);
            if (frames > 0 && audioTrack != null && running) {
                int written = audioTrack.Write(buffer, 0, frames * 2, WriteMode.Blocking);
                if (written < 0) {
                    // Recover cleanly when Android invalidates AudioTrack (e.g. Bluetooth/headphone route change)
                    RecreateTrack(targetRate);
                    try {
                        audioTrack?.Play();
                        audioTrack?.Write(buffer, 0, frames * 2, WriteMode.Blocking);
                    } catch {
                        Thread.Sleep(25);
                    }
                }
            } else {
                Thread.Sleep(10);
            }
        }
    }

    void RecreateTrack(int sampleRate) {
        ReleaseTrack();
        activeSampleRate = sampleRate;

        AudioAttributes attrs = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)
            .SetContentType(AudioContentType.Music)
            .Build();

        AudioFormat format = new AudioFormat.Builder()
            .SetEncoding(AudioEncoding.PcmFloat)
            .SetChannelMask(ChannelOut.Stereo)
            .SetSampleRate(sampleRate)
            .Build();

        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Stereo, AudioEncoding.PcmFloat);
        int bufBytes = Math.Max(minBytes, FramesPerWrite * 4 * 2 * sizeof(float));

        audioTrack = new AudioTrack.Builder()
            .SetAudioAttributes(attrs)
            .SetAudioFormat(format)
            .SetTransferMode(AudioTrackMode.Stream)
            .SetBufferSizeInBytes(bufBytes)
            .Build();
    }

    void ReleaseTrack() {
        if (audioTrack != null) {
            try {
                audioTrack.Stop();
                audioTrack.Release();
                audioTrack.Dispose();
            } catch {
                // Ignore
            }
            audioTrack = null;
        }
    }

    public void Dispose() {
        Stop();
        GC.SuppressFinalize(this);
    }
}
