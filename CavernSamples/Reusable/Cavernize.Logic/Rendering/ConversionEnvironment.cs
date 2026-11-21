using Cavern;
using Cavern.Format.Renderers;
using Cavern.Listeners;
using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Handles main steps of the Cavernize process while being extensible.
/// </summary>
public sealed class ConversionEnvironment {
    /// <summary>
    /// Playback environment used for rendering.
    /// </summary>
    public Listener Listener { get; private set; }

    /// <summary>
    /// Keeper of loaded files and settings.
    /// </summary>
    readonly ICavernizeApp app;

    /// <summary>
    /// Handles main steps of the Cavernize process while being extensible.
    /// </summary>
    /// <param name="app">Application implementation keeping</param>
    public ConversionEnvironment(ICavernizeApp app) {
        this.app = app;
        Listener = new() { // Create a listener, which triggers the loading of saved environment settings
            UpdateRate = 64,
            AudioQuality = QualityModes.Perfect
        };
    }

    /// <summary>
    /// Set up the listening environment for playing back the specified track.
    /// </summary>
    public void AttachToListener(CavernizeTrack target) {
        app.RenderTarget.Apply(app.RenderingSettings.SurroundSwap);
        if (app.RenderTarget is not VirtualizerRenderTarget && app.ExportFormat.MaxChannels < Listener.Channels.Length) {
            throw new OverMaxChannelsException(Listener.Channels.Length, app.ExportFormat.MaxChannels);
        }

        if (app.RenderingSettings.SpeakerVirtualizer) {
            VirtualizerFilter.SetupForSpeakers();
            Listener.SampleRate = VirtualizerFilter.FilterSampleRate;
        }

        // Prevent height limiting without double-attenuating E-AC-3 JOC (EnhancedAC3Renderer already applies 0.707f per object)
        float safeGain = target.Renderer.HasObjects && target.Renderer is not EnhancedAC3Renderer
            ? app.RenderTarget.GetSafeGain()
            : 1f;
        Listener.Volume = app.RenderingSettings.Gain * safeGain;
        Listener.LFEVolume = app.RenderingSettings.LFEVolume;
        Listener.LFESeparation = app.RenderTarget is VirtualizerRenderTarget || !HasPhysicalLfe(Listener.Channels);

        if (app.RenderTarget is VirtualizerRenderTarget) {
            if (app.RenderingSettings.RoomCorrection != null && app.RenderingSettings.RoomCorrection.SampleRate != VirtualizerFilter.FilterSampleRate) {
                throw new SampleRateMismatchException();
            }
            Listener.SampleRate = VirtualizerFilter.FilterSampleRate;
        } else {
            Listener.SampleRate = app.RenderingSettings.RoomCorrection?.SampleRate ?? target.SampleRate;
        }

        if (app.RenderingSettings.RoomCorrectionUsable) {
            Listener = new ConvolvedListener(false) {
                SampleRate = Listener.SampleRate,
                UpdateRate = Listener.UpdateRate,
                AudioQuality = Listener.AudioQuality,
                Volume = Listener.Volume,
                LFEVolume = Listener.LFEVolume,
                LFESeparation = Listener.LFESeparation,
                ConvolutionClip = app.RenderingSettings.RoomCorrection
            };
        }

        Reset();
        target.Attach(Listener, app.UpmixingSettings);

        if (app.RenderTarget is VirtualizerRenderTarget || app.RenderingSettings.SpeakerVirtualizer) {
            Listener.SampleRate = VirtualizerFilter.FilterSampleRate;
        } else if (app.RenderingSettings.RoomCorrection != null) {
            Listener.SampleRate = app.RenderingSettings.RoomCorrection.SampleRate;
        }
    }

    /// <summary>
    /// Checks whether any channel in <paramref name="channels"/> is marked as a dedicated LFE subwoofer.
    /// </summary>
    static bool HasPhysicalLfe(Channel[] channels) {
        if (channels == null) {
            return false;
        }
        for (int i = 0; i < channels.Length; i++) {
            if (channels[i].LFE) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reset the environment, prepare for next attachment.
    /// </summary>
    public void Reset() => Listener.DetachAllSources();
}
