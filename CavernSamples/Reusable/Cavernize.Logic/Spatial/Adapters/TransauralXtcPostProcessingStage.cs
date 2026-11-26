using System;

using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 6 post-processing stage (Priority 820, strictly before True-Peak Limiter at 900) implementing
/// physical Bauck-Cooper / BACCH transaural crosstalk cancellation (XTC) for built-in phone stereo speakers
/// and external stereo speakers, with guaranteed bit-exact (0.0f error) automatic headphone bypass.
/// </summary>
public sealed class TransauralXtcPostProcessingStage : IPostProcessingStage {
    readonly object syncRoot = new();
    readonly TransauralXtcFilter filter = new();
    TransauralXtcConfig config = TransauralXtcConfig.Default;
    int sampleRate = 48000;
    int channels = 2;
    volatile bool isHeadphoneRoute = true;
    volatile bool forceBitExactBypass = true;

    /// <inheritdoc />
    public string StageId => "transaural-xtc";

    /// <inheritdoc />
    public string DisplayName => "Physical Transaural XTC (Speakers)";

    /// <inheritdoc />
    public int Priority => 820;

    /// <inheritdoc />
    public bool IsEnabled {
        get => config.Enabled;
        set {
            lock (syncRoot) {
                config = new TransauralXtcConfig {
                    Enabled = value,
                    Preset = config.Preset,
                    Strength = config.Strength,
                    AutoBypassOnHeadphones = config.AutoBypassOnHeadphones,
                    CustomSpanMeters = config.CustomSpanMeters,
                    CustomDistanceMeters = config.CustomDistanceMeters
                }.Sanitize();
                ApplyFilterLocked();
            }
        }
    }

    /// <summary>Active transaural configuration.</summary>
    public TransauralXtcConfig Config {
        get {
            lock (syncRoot) {
                return config;
            }
        }
    }

    /// <summary>True when the current physical output route is classified as headphones/IEM/headset.</summary>
    public bool IsHeadphoneRoute => isHeadphoneRoute;

    /// <summary>True when bit-exact headphone bypass is currently engaged.</summary>
    public bool IsHeadphoneBypassEngaged => forceBitExactBypass && config.Enabled;

    /// <summary>True when transaural XTC is actively processing speaker output.</summary>
    public bool IsActivelyProcessing => !forceBitExactBypass && filter.IsActivelyProcessing;

    /// <summary>Applies updated speaker transaural XTC configuration safely.</summary>
    public void ApplyConfig(TransauralXtcConfig newConfig) {
        TransauralXtcConfig safe = (newConfig ?? TransauralXtcConfig.Default).Sanitize();
        lock (syncRoot) {
            config = safe;
            ApplyFilterLocked();
        }
    }

    /// <summary>
    /// Updates the physical output route state. Whenever <paramref name="headphoneRoute"/> is true and
    /// <see cref="TransauralXtcConfig.AutoBypassOnHeadphones"/> is enabled, bit-exact 0.0f bypass engages immediately.
    /// </summary>
    public void UpdateOutputRoute(bool headphoneRoute, AudioDeviceTransport transport = AudioDeviceTransport.Unknown) {
        lock (syncRoot) {
            bool prevBypass = forceBitExactBypass;
            isHeadphoneRoute = headphoneRoute;
            ApplyFilterLocked();
            if (prevBypass != forceBitExactBypass) {
                filter.Reset();
            }
        }
    }

    /// <summary>Builds an immutable live diagnostics snapshot for UI &amp; telemetry.</summary>
    public TransauralXtcDiagnostics GetDiagnostics() {
        lock (syncRoot) {
            TransauralGeometryParams geo = filter.Geometry;
            bool bypass = forceBitExactBypass;
            return new TransauralXtcDiagnostics {
                IsActive = !bypass && filter.IsActivelyProcessing,
                UserEnabled = config.Enabled,
                HeadphoneBypassEngaged = bypass && config.Enabled,
                IsHeadphoneRoute = isHeadphoneRoute,
                Preset = config.Preset,
                HalfAngleDegrees = geo.HalfAngleDegrees,
                ContralateralDelayUs = geo.ContralateralDelayUs,
                EstimatedCancellationDb = filter.EstimatedCancellationDb,
                Strength = config.Strength
            };
        }
    }

    /// <inheritdoc />
    public void Configure(int newSampleRate, int newChannels) {
        lock (syncRoot) {
            sampleRate = Math.Max(16000, newSampleRate);
            channels = Math.Max(1, newChannels);
            ApplyFilterLocked();
        }
    }

    /// <inheritdoc />
    public void ProcessInterleaved(float[] buffer, int offset, int frameCount, int channelCount, SpatialSceneFrame context) {
        // Lock-free fast path guaranteeing bit-exact 0.0f bypass on headphone routes or when disabled
        if (forceBitExactBypass || !config.Enabled || channelCount != 2 || frameCount <= 0 || buffer == null) {
            return;
        }
        lock (syncRoot) {
            if (forceBitExactBypass || !filter.IsActivelyProcessing) {
                return;
            }
            filter.ProcessInterleaved(buffer, offset, frameCount, channelCount);
        }
    }

    /// <inheritdoc />
    public void Reset() {
        lock (syncRoot) {
            filter.Reset();
        }
    }

    /// <inheritdoc />
    public IPostProcessingStage CloneForOfflineExport() {
        TransauralXtcPostProcessingStage clone = new();
        lock (syncRoot) {
            clone.sampleRate = sampleRate;
            clone.channels = channels;
            clone.isHeadphoneRoute = isHeadphoneRoute;
            clone.config = config.Sanitize();
            clone.ApplyFilterLocked();
        }
        return clone;
    }

    /// <inheritdoc />
    public void Dispose() {
        Reset();
    }

    void ApplyFilterLocked() {
        bool shouldBypass = !config.Enabled || (config.AutoBypassOnHeadphones && isHeadphoneRoute) || channels != 2;
        forceBitExactBypass = shouldBypass;
        filter.Configure(
            sampleRate,
            config.Preset,
            config.Strength,
            config.Enabled,
            shouldBypass,
            config.CustomSpanMeters,
            config.CustomDistanceMeters);
    }
}
