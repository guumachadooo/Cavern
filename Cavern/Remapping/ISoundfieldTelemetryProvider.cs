using Cavern.Spatial;

namespace Cavern.Remapping {
    /// <summary>
    /// Implemented by parametric spatial upmixers to expose live soundfield telemetry
    /// (<see cref="SoundfieldTelemetry.Coherence"/>, <see cref="SoundfieldTelemetry.Diffuseness"/>,
    /// <see cref="SoundfieldTelemetry.DominantDoa"/>) and stateful reset hooks to Slot 2A / Slot 3.
    /// </summary>
    public interface ISoundfieldTelemetryProvider {
        /// <summary>
        /// Most recent energy-weighted DirAC/COMPASS soundfield telemetry computed by this processor.
        /// </summary>
        SoundfieldTelemetry LatestTelemetry { get; }

        /// <summary>
        /// Clears internal STFT overlap-add buffers, smoothing states, and decorrelator delay lines.
        /// </summary>
        void Reset();
    }
}
