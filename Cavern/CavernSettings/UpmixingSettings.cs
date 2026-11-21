namespace Cavern.CavernSettings {
    /// <summary>
    /// Holds how to apply the Cavernize 3D upmixing method.
    /// </summary>
    public class UpmixingSettings {
        /// <summary>
        /// Fill 7.1 channels from whatever lower configuration is available, with basic matrices.
        /// </summary>
        public virtual bool MatrixUpmixing { get; set; }

        /// <summary>
        /// Apply height to all input channels. Recommended to combine with <see cref="MatrixUpmixing"/> to fill the entire space,
        /// as configurations like stereo would only fill the front wall when only this option is enabled.
        /// </summary>
        public virtual bool Cavernize { get; set; }

        /// <summary>
        /// Amplitude of the heights created with <see cref="Cavernize"/>.
        /// </summary>
        public virtual float Effect { get; set; }

        /// <summary>
        /// Larger values mean slower object movements for the <see cref="Cavernize"/> effect.
        /// </summary>
        public virtual float Smoothness { get; set; }

        /// <summary>
        /// Use the DirAC/COMPASS-inspired parametric STFT core with orthogonal decorrelation for Matrix 7.1 and Cavernize 3D.
        /// When false, falls back to the legacy time-domain upmixer behavior.
        /// </summary>
        public virtual bool ParametricStft { get; set; } = true;

        /// <summary>
        /// Preserve native Dolby Atmos / object-based beds, heights, and dynamic objects transparently when upmixing is active,
        /// expanding only missing surround beds or sparse/silent ceiling passages with sample-accurate delay alignment.
        /// </summary>
        public virtual bool AtmosAware { get; set; } = true;

        /// <summary>
        /// Holds how to apply the Cavernize 3D upmixing method.
        /// </summary>
        public UpmixingSettings(bool loadDefaults) {
            if (loadDefaults) {
                MatrixUpmixing = false;
                Cavernize = false;
                Effect = .75f;
                Smoothness = .8f;
                ParametricStft = true;
                AtmosAware = true;
            }
        }
    }
}
