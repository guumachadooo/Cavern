using System.Numerics;

using Cavern.CavernSettings;
using Cavern.Remapping;
using Cavern.Spatial;

namespace Cavernize.Logic.Spatial.Adapters;

/// <summary>
/// Slot 2A processor bridging Cavern's DirAC/COMPASS parametric STFT upmixers
/// (<see cref="SurroundUpmixer"/>, <see cref="CavernizeUpmixer"/>, <see cref="AtmosAwareUpmixer"/>)
/// into the unified 6-slot spatial pipeline and feeding live <see cref="SoundfieldTelemetry"/>
/// (<c>Coherence</c>, <c>Diffuseness</c>, <c>DominantDoa</c>) to <see cref="SpatialSceneHub"/>.
/// </summary>
public sealed class ParametricUpmixerStageProcessor : ISpatialStageProcessor {
    UpmixingSettings settings;
    ISoundfieldTelemetryProvider telemetryProvider;
    SpatialProgramMetadata activeMetadata = SpatialProgramMetadata.CreateDefault();
    int sampleRate = 48000;

    /// <inheritdoc/>
    public string StageId => "stft-dirac-matrix";

    /// <inheritdoc/>
    public string DisplayName => "DirAC/COMPASS Parametric STFT Upmixer";

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool SupportsAtmosAware => true;

    /// <summary>
    /// Active upmixing configuration mirrored from the engine.
    /// </summary>
    public UpmixingSettings Settings => settings;

    /// <summary>
    /// Active sample rate configured for this stage.
    /// </summary>
    public int SampleRate => sampleRate;

    /// <summary>
    /// True when an active parametric STFT upmixer is supplying direct cross-spectral covariance telemetry.
    /// </summary>
    public bool HasDirectStftProvider => telemetryProvider != null;

    /// <summary>
    /// Creates a Slot 2A parametric STFT stage processor.
    /// </summary>
    public ParametricUpmixerStageProcessor(UpmixingSettings settings) {
        this.settings = settings ?? new UpmixingSettings(true);
    }

    /// <summary>
    /// Updates the mirrored <see cref="UpmixingSettings"/> reference without heap allocation.
    /// </summary>
    public void UpdateSettings(UpmixingSettings newSettings) {
        settings = newSettings ?? new UpmixingSettings(true);
    }

    /// <summary>
    /// Binds the active upmixer telemetry provider (null when no upmixer is attached to the active track).
    /// </summary>
    public void BindTelemetryProvider(ISoundfieldTelemetryProvider provider) {
        telemetryProvider = provider;
    }

    /// <inheritdoc/>
    public void Configure(int sampleRate, SpatialProgramMetadata metadata) {
        this.sampleRate = sampleRate > 0 ? sampleRate : 48000;
        activeMetadata = metadata;
    }

    /// <inheritdoc/>
    public void Process(SpatialSceneFrame frame) {
        if (frame == null || !IsEnabled) {
            return;
        }
        // Pre-render hook: actual STFT DSP executes inside Cavern Listener's pull graph via SurroundUpmixer /
        // CavernizeUpmixer / AtmosAwareUpmixer. SyncTelemetry pulls fresh block metrics right after Slot 5 renders.
        _ = activeMetadata;
    }

    /// <summary>
    /// Publishes live DirAC/COMPASS <c>Coherence</c>, <c>Diffuseness</c>, and <c>DominantDoa</c> into
    /// <paramref name="frame"/> without overwriting loudness or True Peak measurements from Slot 6.
    /// Falls back to multi-source acoustic intensity vector estimation when no STFT upmixer is active.
    /// </summary>
    public void SyncTelemetry(SpatialSceneFrame frame) {
        if (frame == null) {
            return;
        }

        SoundfieldTelemetry merged = frame.Telemetry;
        if (IsEnabled && telemetryProvider != null) {
            SoundfieldTelemetry stft = telemetryProvider.LatestTelemetry;
            merged.Coherence = stft.Coherence;
            merged.Diffuseness = stft.Diffuseness;
            merged.DominantDoa = stft.DominantDoa;
            frame.Telemetry = merged;
            return;
        }

        // Pure-physics spatial intensity vector fallback (DirAC formulation: psi = 1 - ||I|| / E_total)
        IReadOnlyList<SpatialSourceDescriptor> sources = frame.Sources;
        if (sources == null || sources.Count == 0) {
            return;
        }

        double totalEnergy = 0.0;
        double ix = 0.0, iy = 0.0, iz = 0.0;
        for (int i = 0; i < sources.Count; i++) {
            SpatialSourceDescriptor s = sources[i];
            if (s.IsLfe || s.IsMuted || s.IsDynamicallyFilteredOut) {
                continue;
            }
            float rms = s.RmsLevel;
            if (rms <= 1e-5f) {
                continue;
            }
            double energy = (double)rms * rms;
            Vector3 pos = s.HasCustomPosition ? s.CustomNormalizedPosition : s.NormalizedPosition;
            float len = pos.Length();
            if (len > 1e-4f) {
                float inv = 1f / len;
                ix += energy * (pos.X * inv);
                iy += energy * (pos.Y * inv);
                iz += energy * (pos.Z * inv);
            }
            totalEnergy += energy;
        }

        if (totalEnergy > 1e-10) {
            double intensityMag = Math.Sqrt(ix * ix + iy * iy + iz * iz);
            float coherence = (float)Math.Clamp(intensityMag / totalEnergy, 0.0, 1.0);
            merged.Coherence = coherence;
            merged.Diffuseness = 1f - coherence;
            merged.DominantDoa = intensityMag > 1e-8
                ? new Vector3((float)(ix / intensityMag), (float)(iy / intensityMag), (float)(iz / intensityMag))
                : Vector3.Zero;
        } else {
            merged.Coherence = 0f;
            merged.Diffuseness = 0f;
            merged.DominantDoa = Vector3.Zero;
        }
        frame.Telemetry = merged;
    }

    /// <inheritdoc/>
    public void Reset() {
        telemetryProvider?.Reset();
    }

    /// <inheritdoc/>
    public void Dispose() {
        telemetryProvider = null;
    }
}
