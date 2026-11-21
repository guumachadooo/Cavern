namespace Cavern.Spatial {
    /// <summary>
    /// Classifies the origin layer of a spatial audio source inside the unified 6-slot scene hub.
    /// Aligns with ITU-R BS.2127 (DirectSpeakers / Objects / HOA) plus virtual upmix and neural stem layers.
    /// </summary>
    public enum SourceLayer {
        /// <summary>
        /// Fixed speaker-bed channel (ITU-R BS.2127 DirectSpeakers, e.g., L, R, C, LFE, SL, SR, TFL).
        /// </summary>
        Bed = 0,

        /// <summary>
        /// Native dynamic 3D object from the decoded bitstream (e.g., Dolby Atmos, ADM BWF, IAMF objects).
        /// </summary>
        NativeObject = 1,

        /// <summary>
        /// Ground-plane surround channel synthesized by parametric/matrix upmixing (Slot 2A).
        /// </summary>
        VirtualMatrix = 2,

        /// <summary>
        /// Elevated height source synthesized by 3D/Atmos-aware upmixing (Slot 2A).
        /// </summary>
        VirtualHeight = 3,

        /// <summary>
        /// Source-separated musical stem positioned in 3D space (Slot 2B, e.g., Vocals, Drums, Bass, Other).
        /// </summary>
        NeuralStem = 4,

        /// <summary>
        /// Scene-based Higher-Order Ambisonics (HOA) spherical harmonic component (ITU-R BS.2127 HOA).
        /// </summary>
        AmbisonicsHoa = 5,

        /// <summary>
        /// Energy-conserved spatial residual (mix minus separated stems, or cleaned Atmos-exclusive residual in W09).
        /// </summary>
        RestoredResidual = 6,

        /// <summary>
        /// Live USB/pro-audio microphone 3D object injected into the scene (W10/W11).
        /// </summary>
        LiveMicrophone = 7
    }

    /// <summary>
    /// High-level programme content classification matching ITU-R BS.2076 / BS.2127 typeDefinitions.
    /// </summary>
    public enum SpatialContentKind {
        /// <summary>
        /// Channel-based audio mapped directly to nominal loudspeaker positions (Stereo, 5.1, 7.1, 7.1.4).
        /// </summary>
        DirectSpeakers = 0,

        /// <summary>
        /// Purely object-based spatial programme with dynamic positional metadata.
        /// </summary>
        Objects = 1,

        /// <summary>
        /// Scene-based Higher-Order Ambisonics programme (ACN/SN3D or FuMa).
        /// </summary>
        Hoa = 2,

        /// <summary>
        /// Hybrid programme combining fixed bed channels and dynamic 3D objects (e.g., Dolby Atmos).
        /// </summary>
        HybridBedAndObjects = 3
    }

    /// <summary>
    /// Ambisonics spherical harmonic channel ordering scheme (for Slot 1 / Slot 5 HOA support).
    /// </summary>
    public enum HoaChannelOrder {
        /// <summary>
        /// AmbiX ACN (Ambisonic Channel Number) ordering.
        /// </summary>
        Acn = 0,

        /// <summary>
        /// Legacy Furse-Malham ordering.
        /// </summary>
        FuMa = 1
    }

    /// <summary>
    /// Ambisonics spherical harmonic normalization convention.
    /// </summary>
    public enum HoaNormalization {
        /// <summary>
        /// Not an HOA stream.
        /// </summary>
        None = 0,

        /// <summary>
        /// Schmidt semi-normalized (SN3D, AmbiX / ITU-R BS.2076 default).
        /// </summary>
        Sn3d = 1,

        /// <summary>
        /// Full 3D orthonormalization (N3D).
        /// </summary>
        N3d = 2,

        /// <summary>
        /// Legacy Furse-Malham maxN normalization.
        /// </summary>
        FuMa = 3
    }
}
