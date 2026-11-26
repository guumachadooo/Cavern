using System;

using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Configuration for Wave F (W53.6 / B13) Physical Transaural Crosstalk Cancellation (XTC)
/// over built-in smartphone stereo speakers and external stereo speakers.
/// </summary>
public sealed class TransauralXtcConfig {
    /// <summary>Enables transaural crosstalk cancellation when playing on stereo loudspeakers.</summary>
    public bool Enabled { get; init; }

    /// <summary>Physical speaker geometry preset (Phone Landscape, Phone Portrait, Desktop Monitors, Hi-Fi, Custom).</summary>
    public TransauralSpeakerPreset Preset { get; init; } = TransauralSpeakerPreset.PhoneBuiltInLandscape;

    /// <summary>Cancellation depth in [0.0, 1.0] (default 0.85).</summary>
    public float Strength { get; init; } = 0.85f;

    /// <summary>
    /// Safety invariant (KILL R3): automatically engage bit-exact (0.0f error) bypass whenever a headphone,
    /// IEM, USB-DAC headset, or Bluetooth headset route is active. Always defaults to true.
    /// </summary>
    public bool AutoBypassOnHeadphones { get; init; } = true;

    /// <summary>Custom speaker span in meters (used when <see cref="Preset"/> is Custom).</summary>
    public float CustomSpanMeters { get; init; } = 0.155f;

    /// <summary>Custom listener distance in meters (used when <see cref="Preset"/> is Custom).</summary>
    public float CustomDistanceMeters { get; init; } = 0.40f;

    /// <summary>Safe default configuration (disabled until enabled by user, headphone auto-bypass ON).</summary>
    public static TransauralXtcConfig Default => new();

    /// <summary>Returns a sanitized copy with clamped physical parameters.</summary>
    public TransauralXtcConfig Sanitize() {
        TransauralSpeakerPreset safePreset = Enum.IsDefined(typeof(TransauralSpeakerPreset), Preset)
            ? Preset
            : TransauralSpeakerPreset.PhoneBuiltInLandscape;
        return new TransauralXtcConfig {
            Enabled = Enabled,
            Preset = safePreset,
            Strength = Math.Clamp(float.IsFinite(Strength) ? Strength : 0.85f, 0f, 1f),
            AutoBypassOnHeadphones = AutoBypassOnHeadphones,
            CustomSpanMeters = Math.Clamp(float.IsFinite(CustomSpanMeters) ? CustomSpanMeters : 0.155f, 0.08f, 4.0f),
            CustomDistanceMeters = Math.Clamp(float.IsFinite(CustomDistanceMeters) ? CustomDistanceMeters : 0.40f, 0.25f, 5.0f)
        };
    }
}

/// <summary>
/// Live telemetry snapshot emitted by <see cref="Adapters.TransauralXtcPostProcessingStage"/>.
/// </summary>
public sealed class TransauralXtcDiagnostics {
    /// <summary>True when transaural XTC is actively processing stereo loudspeaker output.</summary>
    public bool IsActive { get; init; }

    /// <summary>True when user has enabled transaural XTC in settings.</summary>
    public bool UserEnabled { get; init; }

    /// <summary>True when bit-exact headphone auto-bypass is currently engaged.</summary>
    public bool HeadphoneBypassEngaged { get; init; }

    /// <summary>True when the current physical output route is classified as headphones/IEM/headset.</summary>
    public bool IsHeadphoneRoute { get; init; }

    /// <summary>Active speaker geometry preset.</summary>
    public TransauralSpeakerPreset Preset { get; init; } = TransauralSpeakerPreset.PhoneBuiltInLandscape;

    /// <summary>Computed speaker half-angle in degrees.</summary>
    public float HalfAngleDegrees { get; init; }

    /// <summary>Computed contralateral propagation delay in microseconds.</summary>
    public float ContralateralDelayUs { get; init; }

    /// <summary>Theoretical mid-band crosstalk attenuation in dB.</summary>
    public float EstimatedCancellationDb { get; init; }

    /// <summary>Active cancellation strength in [0, 1].</summary>
    public float Strength { get; init; }
}
