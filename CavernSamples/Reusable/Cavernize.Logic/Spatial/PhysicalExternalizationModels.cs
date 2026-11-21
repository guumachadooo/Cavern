using System.Numerics;

using Cavern.Virtualizer;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Slot 5A physical externalization configuration combining real-time Allen &amp; Berkley shoebox ISM early reflections,
/// Duda &amp; Martens (1998) near-field spherical-head DVF (&lt; 1.0 m), and ISO 9613-1 atmospheric air absorption (&gt; 5.0 m).
/// </summary>
public readonly struct PhysicalExternalizationConfig : IEquatable<PhysicalExternalizationConfig> {
    /// <summary>Enables real-time 1st/2nd-order shoebox ISM early reflections (false by default for neutral anechoic parity).</summary>
    public bool IsmRoomEnabled { get; init; }

    /// <summary>Architectural shoebox room preset (or CustomEnvironment matching Cavern room dimensions).</summary>
    public IsmRoomPreset RoomPreset { get; init; }

    /// <summary>Real ISO 354 / Sabine wall absorption material preset.</summary>
    public IsmWallMaterial WallMaterial { get; init; }

    /// <summary>Image source reflection order (1 = 6 first-order, 2 = 24 first + second-order).</summary>
    public int ReflectionOrder { get; init; }

    /// <summary>Physical reflection amplitude multiplier (1.0 = exact physical level, range 0.0..1.5).</summary>
    public float ReflectionLevel { get; init; }

    /// <summary>Enables Duda &amp; Martens (1998) spherical-head DVF when sources approach &lt; 1.0 m (unity at &gt;= 1.0 m).</summary>
    public bool NearFieldEnabled { get; init; }

    /// <summary>Enables ISO 9613-1 atmospheric absorption for direct paths &gt; 5.0 m and ISM reflection extra paths.</summary>
    public bool AirAbsorptionEnabled { get; init; }

    /// <summary>Ambient air temperature in degrees Celsius (ISO 9613-1 default: 20 C).</summary>
    public float AirTemperatureCelsius { get; init; }

    /// <summary>Ambient relative humidity in percent (ISO 9613-1 default: 50 %).</summary>
    public float AirRelativeHumidityPct { get; init; }

    /// <summary>
    /// Zero-regression neutral default: ISM reflections disabled until opted in, while Near-Field DVF (&lt; 1 m)
    /// and ISO 9613-1 (&gt; 5 m) remain armed with exact 0.00 dB pass-through in the normal 1..5 m listening zone.
    /// </summary>
    public static PhysicalExternalizationConfig Default => new() {
        IsmRoomEnabled = false,
        RoomPreset = IsmRoomPreset.MasteringStudio,
        WallMaterial = IsmWallMaterial.StudioBroadband,
        ReflectionOrder = 2,
        ReflectionLevel = 1.0f,
        NearFieldEnabled = true,
        AirAbsorptionEnabled = true,
        AirTemperatureCelsius = AirAbsorptionFilter.DefaultTemperatureCelsius,
        AirRelativeHumidityPct = AirAbsorptionFilter.DefaultRelativeHumidityPct
    };

    /// <inheritdoc/>
    public bool Equals(PhysicalExternalizationConfig other) =>
        IsmRoomEnabled == other.IsmRoomEnabled &&
        RoomPreset == other.RoomPreset &&
        WallMaterial == other.WallMaterial &&
        ReflectionOrder == other.ReflectionOrder &&
        MathF.Abs(ReflectionLevel - other.ReflectionLevel) < 1e-4f &&
        NearFieldEnabled == other.NearFieldEnabled &&
        AirAbsorptionEnabled == other.AirAbsorptionEnabled &&
        MathF.Abs(AirTemperatureCelsius - other.AirTemperatureCelsius) < 0.05f &&
        MathF.Abs(AirRelativeHumidityPct - other.AirRelativeHumidityPct) < 0.1f;

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is PhysicalExternalizationConfig other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        IsmRoomEnabled,
        RoomPreset,
        WallMaterial,
        ReflectionOrder,
        NearFieldEnabled,
        AirAbsorptionEnabled);
}

/// <summary>
/// Live telemetry snapshot for Slot 5A physical externalization (exposed via <see cref="SpatialSceneHub"/> for UI readouts).
/// </summary>
public readonly struct PhysicalExternalizationDiagnostics {
    /// <summary>True when the user has enabled ISM room early reflections.</summary>
    public bool IsIsmRequested { get; init; }

    /// <summary>True when ISM reflections are actively rendering (requested, binaural active, and NOT bypassed by BRIR guard).</summary>
    public bool IsIsmActive { get; init; }

    /// <summary>True when a measured BRIR (&gt; 1024 taps or BRIR profile) automatically bypassed ISM to prevent double room acoustics.</summary>
    public bool IsBrirGuardBypassed { get; init; }

    /// <summary>True when Direct 2.0 stereo mode is active (binaural virtualizer off).</summary>
    public bool IsDirectStereoBypassed { get; init; }

    /// <summary>Active reflection count per virtual speaker (6 for order 1, 24 for orders 1+2, 0 when bypassed).</summary>
    public int ActiveReflectionCount { get; init; }

    /// <summary>Classical Sabine RT60 reverberation time in seconds for the active room preset and wall material.</summary>
    public float SabineRt60Seconds { get; init; }

    /// <summary>Norris-Eyring RT60 reverberation time in seconds for the active room preset and wall material.</summary>
    public float EyringRt60Seconds { get; init; }

    /// <summary>Full interior room dimensions in meters (Width, Height, Depth).</summary>
    public Vector3 RoomDimensionsMeters { get; init; }

    /// <summary>True when near-field Duda &amp; Martens DVF is armed.</summary>
    public bool NearFieldEnabled { get; init; }

    /// <summary>True when at least one source is currently closer than 1.0 m and engaging near-field DVF.</summary>
    public bool NearFieldEngaged { get; init; }

    /// <summary>True when ISO 9613-1 atmospheric air absorption is enabled.</summary>
    public bool AirAbsorptionEnabled { get; init; }

    /// <summary>Active ambient temperature in degrees Celsius.</summary>
    public float AirTemperatureCelsius { get; init; }

    /// <summary>Active ambient relative humidity in percent.</summary>
    public float AirRelativeHumidityPct { get; init; }

    /// <summary>Default neutral diagnostics snapshot.</summary>
    public static PhysicalExternalizationDiagnostics Default {
        get {
            IsmRoomGeometry geom = new(IsmRoomPreset.MasteringStudio, IsmWallMaterial.StudioBroadband, new Vector3(10f, 7f, 10f));
            return new PhysicalExternalizationDiagnostics {
                IsIsmRequested = false,
                IsIsmActive = false,
                IsBrirGuardBypassed = false,
                IsDirectStereoBypassed = false,
                ActiveReflectionCount = 0,
                SabineRt60Seconds = geom.SabineRt60Seconds,
                EyringRt60Seconds = geom.EyringRt60Seconds,
                RoomDimensionsMeters = geom.DimensionsMeters,
                NearFieldEnabled = true,
                NearFieldEngaged = false,
                AirAbsorptionEnabled = true,
                AirTemperatureCelsius = AirAbsorptionFilter.DefaultTemperatureCelsius,
                AirRelativeHumidityPct = AirAbsorptionFilter.DefaultRelativeHumidityPct
            };
        }
    }
}
