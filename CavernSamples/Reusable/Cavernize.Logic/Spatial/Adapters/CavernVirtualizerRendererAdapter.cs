using System.Numerics;

using Cavern;
using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 5 pass-through adapter wrapping Cavern's active <see cref="Listener"/> and <see cref="VirtualizerFilter"/>
/// so existing HRIR/BRIR/Direct 2.0 rendering operates identically through the <see cref="IBinauralRenderer"/> contract.
/// </summary>
public sealed class CavernVirtualizerRendererAdapter : IBinauralRenderer {
    Listener boundListener;
    bool binauralActive = true;
    int sampleRate = VirtualizerFilter.FilterSampleRate;
    string profileName = "Cavern Virtualizer";

    /// <inheritdoc/>
    public string RendererId => "cavern-virtualizer";

    /// <inheritdoc/>
    public string ProfileName => profileName;

    /// <inheritdoc/>
    public int SampleRate => boundListener != null ? boundListener.SampleRate : sampleRate;

    /// <inheritdoc/>
    public int ProfileVersion => VirtualizerFilter.ProfileVersion;

    /// <inheritdoc/>
    public bool IsBinauralActive => binauralActive;

    /// <summary>
    /// Updates the bound <see cref="Listener"/>, binaural state, and active profile display name.
    /// </summary>
    public void Bind(Listener listener, bool isBinauralActive, string activeProfileName = null) {
        boundListener = listener;
        binauralActive = isBinauralActive;
        if (listener != null) {
            sampleRate = listener.SampleRate;
        }
        if (!string.IsNullOrWhiteSpace(activeProfileName)) {
            profileName = activeProfileName;
        }
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, Vector3 environmentSize, Environments environmentType) {
        this.sampleRate = sampleRate > 0 ? sampleRate : Listener.DefaultSampleRate;
        Listener.EnvironmentType = environmentType;
        if (environmentSize.X > 0.1f && environmentSize.Y > 0.1f && environmentSize.Z > 0.1f) {
            Listener.EnvironmentSize = environmentSize;
        }
    }

    /// <inheritdoc/>
    public int RenderFrame(SpatialSceneFrame frame, float[] interleavedStereoOut, int outputOffset, int maxFrames) {
        if (boundListener == null || interleavedStereoOut == null || maxFrames <= 0) {
            return 0;
        }

        int updateRate = Math.Max(16, boundListener.UpdateRate);
        int chunks = maxFrames / updateRate;
        if (chunks <= 0) {
            return 0;
        }

        float[] rendered = boundListener.Render(chunks);
        if (rendered == null) {
            return 0;
        }

        int stride = Listener.Channels.Length;
        int blockFrames = chunks * updateRate;
        for (int s = 0; s < blockFrames; s++) {
            float left = rendered[s * stride];
            float right = stride > 1 ? rendered[s * stride + 1] : left;
            interleavedStereoOut[outputOffset + s * 2] = float.IsFinite(left) ? left : 0f;
            interleavedStereoOut[outputOffset + s * 2 + 1] = float.IsFinite(right) ? right : 0f;
        }

        if (frame != null) {
            frame.OutputFrameCount = blockFrames;
        }
        return blockFrames;
    }

    /// <inheritdoc/>
    public void Reset() {
        // VirtualizerFilter overlap-add state resets naturally when Listener is recreated on seek/profile change.
    }

    /// <inheritdoc/>
    public void Dispose() {
        boundListener = null;
    }
}
