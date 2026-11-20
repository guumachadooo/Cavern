using Cavern.Filters;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Acoustic coupling form factor of a headphone or earphone (determines default AutoEq source target).
/// </summary>
public enum HeadphoneFormFactor {
    /// <summary>Circumaural over-ear headphones (AutoEq standard reference: Harman Over-Ear 2018).</summary>
    OverEar = 0,
    /// <summary>In-ear monitors / canalphones / TWS earbuds with tips (AutoEq standard reference: AutoEq In-Ear).</summary>
    InEar = 1,
    /// <summary>Supra-aural on-ear headphones (AutoEq standard reference: Harman Over-Ear 2018).</summary>
    OnEar = 2,
    /// <summary>Open-fit flat earbuds without ear-canal seal (AutoEq standard reference: AutoEq In-Ear).</summary>
    Earbud = 3
}

/// <summary>
/// Physical audio output transport detected on the host device for automatic per-device profile switching.
/// </summary>
public enum AudioDeviceTransport {
    Unknown = 0,
    BuiltinSpeaker = 1,
    WiredJack = 2,
    UsbDac = 3,
    BluetoothA2dp = 4,
    BleAudio = 5,
    HdmiOrLineOut = 6
}

/// <summary>
/// Lightweight searchable index entry representing one of the 8,850+ official laboratory AutoEq profiles.
/// </summary>
public sealed class AutoEqCatalogEntry {
    /// <summary>Stable normalized machine identifier derived from the official AutoEq result path.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable headphone / IEM model name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Measurement rig and source lab (e.g. "oratory1990/over-ear", "crinacle/Bruel &amp; Kjaer 4620 in-ear").</summary>
    public string MeasurementRig { get; set; } = string.Empty;

    /// <summary>Acoustic form factor (OverEar, InEar, OnEar, Earbud).</summary>
    public HeadphoneFormFactor FormFactor { get; set; } = HeadphoneFormFactor.OverEar;

    /// <summary>Quality priority score used to rank preferred laboratory rigs (oratory1990 &gt; B&amp;K 5128/GRAS &gt; 711 &gt; Rtings).</summary>
    public int SourceRank { get; set; } = 50;

    /// <summary>True when the full ParametricEQ filters are bundled offline inside the app.</summary>
    public bool IsBundledOffline { get; set; }

    /// <summary>URL-encoded relative path inside <c>jaakkopasanen/AutoEq/results/</c> for 1-tap on-demand download.</summary>
    public string RemoteRelativePath { get; set; } = string.Empty;

    /// <summary>Default source target baked into the original AutoEq profile.</summary>
    public AutoEqSourceTarget InferSourceTarget() =>
        FormFactor == HeadphoneFormFactor.InEar || FormFactor == HeadphoneFormFactor.Earbud
            ? AutoEqSourceTarget.AutoEqInEar
            : AutoEqSourceTarget.HarmanOverEar2018;
}

/// <summary>
/// Full parametric AutoEq compensation profile ready to load into Slot 6A (<see cref="Adapters.AutoEqPostProcessingStage"/>).
/// </summary>
public sealed class AutoEqHeadphoneProfile {
    /// <summary>Stable profile identifier (or "custom" for user-imported .txt files).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display model name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Measurement source / coupler rig.</summary>
    public string MeasurementRig { get; set; } = string.Empty;

    /// <summary>Acoustic form factor.</summary>
    public HeadphoneFormFactor FormFactor { get; set; } = HeadphoneFormFactor.OverEar;

    /// <summary>Original target curve baked into <see cref="Bands"/>.</summary>
    public AutoEqSourceTarget SourceTarget { get; set; } = AutoEqSourceTarget.HarmanOverEar2018;

    /// <summary>Explicit preamp gain (dB) from the original AutoEq / Equalizer APO file.</summary>
    public double ExplicitPreampDb { get; set; }

    /// <summary>True when <see cref="ExplicitPreampDb"/> was supplied by the source file.</summary>
    public bool HasExplicitPreamp { get; set; }

    /// <summary>Ordered parametric biquad bands.</summary>
    public ParametricBand[] Bands { get; set; } = Array.Empty<ParametricBand>();

    /// <summary>True when this profile came from the bundled offline laboratory package.</summary>
    public bool IsBundledOffline { get; set; }

    /// <summary>Optional local path when loaded from a custom user-imported Equalizer APO / AutoEq / Wavelet file.</summary>
    public string CustomFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Saved audio profile automatically recalled whenever a specific Bluetooth, USB DAC, 3.5mm, or Speaker output connects.
/// </summary>
public sealed class AudioDeviceProfileBinding {
    /// <summary>Normalized stable hardware route key (e.g. "bt:galaxy_buds2_pro", "usb:apple_usbc", "wired:35mm").</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>Human-readable hardware name reported by the OS (e.g. "Galaxy Buds2 Pro").</summary>
    public string DeviceDisplayName { get; set; } = string.Empty;

    /// <summary>Detected hardware output transport.</summary>
    public AudioDeviceTransport Transport { get; set; } = AudioDeviceTransport.Unknown;

    /// <summary>Saved AutoEq profile ID for this device (empty = none/flat).</summary>
    public string AutoEqProfileId { get; set; } = string.Empty;

    /// <summary>Cached AutoEq display name for UI readout.</summary>
    public string AutoEqProfileName { get; set; } = string.Empty;

    /// <summary>Whether AutoEq neutral compensation is active for this device.</summary>
    public bool AutoEqEnabled { get; set; }

    /// <summary>Saved <see cref="AutoEqNeutralTarget"/> index for this device.</summary>
    public int AutoEqTargetIndex { get; set; } = (int)AutoEqNeutralTarget.DiffuseFieldNeutral;

    /// <summary>Saved binaural HRIR/BRIR profile ID (e.g. "sadie_d1_dfc", "direct_20", or empty to keep current).</summary>
    public string BinauralProfileId { get; set; } = string.Empty;

    /// <summary>Saved head-tracking preference for this device (consumed by Wave W04 Slot 4 providers).</summary>
    public bool HeadTrackingEnabled { get; set; }

    /// <summary>True when the initial AutoEq assignment was created by automatic device-name matching.</summary>
    public bool AutoMatched { get; set; }
}
