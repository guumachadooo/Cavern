using System.Numerics;

using Cavern.Channels;

namespace Cavern.Spatial {
    /// <summary>
    /// Describes a single spatial audio source (bed channel, native 3D object, upmixed virtual source, neural stem,
    /// or HOA component) using ITU-R BS.2127-aligned attributes (position, gain, diffuse, divergence, importance).
    /// </summary>
    public sealed class SpatialSourceDescriptor {
        /// <summary>
        /// Zero-based index of the source inside the active scene.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Human-readable display label for Room 3D and Mixer UI.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Origin layer of this source (Bed, NativeObject, VirtualMatrix, VirtualHeight, NeuralStem, AmbisonicsHoa).
        /// </summary>
        public SourceLayer Layer { get; set; }

        /// <summary>
        /// Nominal loudspeaker label when <see cref="Layer"/> is <see cref="SourceLayer.Bed"/> or <see cref="SourceLayer.VirtualMatrix"/>.
        /// </summary>
        public ReferenceChannel? BedChannel { get; set; }

        /// <summary>
        /// True if this source carries dedicated low-frequency effects (.1 LFE).
        /// </summary>
        public bool IsLfe { get; set; }

        /// <summary>
        /// True when tagged as dialogue/center vocal content (used by Slot 6 Spatial Dialog Enhancement).
        /// </summary>
        public bool IsDialogue { get; set; }

        /// <summary>
        /// Normalized position inside the listening room in [-1; 1] on each axis.
        /// </summary>
        public Vector3 NormalizedPosition { get; set; }

        /// <summary>
        /// Physical position in meters relative to room center (for 6DoF propagation delay and ISM reflections).
        /// </summary>
        public Vector3 WorldPositionMeters { get; set; }

        /// <summary>
        /// Linear source gain multiplier (ITU-R BS.2127 gain, unity = 1.0).
        /// </summary>
        public float Gain { get; set; } = 1f;

        /// <summary>
        /// ITU-R BS.2127 diffuseness parameter in [0; 1].
        /// Direct energy scales by sqrt(1 - Diffuse) and decorrelated diffuse energy by sqrt(Diffuse).
        /// </summary>
        public float Diffuse { get; set; }

        /// <summary>
        /// ITU-R BS.2127 object divergence / spatial spread in [0; 1] (0 = point source).
        /// </summary>
        public float Divergence { get; set; }

        /// <summary>
        /// Relative perceptual importance in [0; 1] (ITU-R BS.2076 importance metadata).
        /// </summary>
        public float Importance { get; set; } = 1f;

        /// <summary>
        /// True when muted by the user in the mixer.
        /// </summary>
        public bool IsMuted { get; set; }

        /// <summary>
        /// True when soloed by the user in the mixer.
        /// </summary>
        public bool IsSolo { get; set; }

        /// <summary>
        /// True when temporarily muted by a dynamic filter (e.g., Dynamic Ceiling-Only mode).
        /// </summary>
        public bool IsDynamicallyFilteredOut { get; set; }

        /// <summary>
        /// True when the user manually dragged/repositioned this source in the 3D Room (W10).
        /// </summary>
        public bool HasCustomPosition { get; set; }

        /// <summary>
        /// User-defined normalized room position override when <see cref="HasCustomPosition"/> is true.
        /// </summary>
        public Vector3 CustomNormalizedPosition { get; set; }

        /// <summary>
        /// Latest block RMS amplitude [0; 1+] for real-time visualization and metering.
        /// </summary>
        public float RmsLevel { get; set; }

        /// <summary>
        /// Latest block sample peak amplitude [0; 1+] for headroom monitoring.
        /// </summary>
        public float PeakLevel { get; set; }
    }

    /// <summary>
    /// Programme-level spatial and loudness metadata provided by Slot 1 decoders (ITU-R BS.2076 / BS.2127 / BS.1770).
    /// </summary>
    public struct SpatialProgramMetadata {
        /// <summary>
        /// Programme classification (DirectSpeakers, Objects, Hoa, HybridBedAndObjects).
        /// </summary>
        public SpatialContentKind ContentKind { get; set; }

        /// <summary>
        /// Nominal bed channel count in the decoded stream.
        /// </summary>
        public int BedChannelCount { get; set; }

        /// <summary>
        /// Native dynamic object count in the decoded stream.
        /// </summary>
        public int DynamicObjectCount { get; set; }

        /// <summary>
        /// True when the decoded stream contains native dynamic 3D objects.
        /// </summary>
        public bool HasNativeObjects { get; set; }

        /// <summary>
        /// True when the decoded stream already contains elevated height beds or elevated objects.
        /// </summary>
        public bool HasNativeHeights { get; set; }

        /// <summary>
        /// True when the decoded stream carries a dedicated LFE (.1) channel.
        /// </summary>
        public bool HasLfe { get; set; }

        /// <summary>
        /// Dialogue normalization reference level in LUFS (e.g., -27 or -31 LUFS), or <see cref="float.NaN"/> if unspecified.
        /// </summary>
        public float DialnormLufs { get; set; }

        /// <summary>
        /// Integrated programme loudness in LUFS (ITU-R BS.1770-5), or <see cref="float.NaN"/> if unmeasured.
        /// </summary>
        public float IntegratedLoudnessLufs { get; set; }

        /// <summary>
        /// Ambisonics order (0 when not an HOA stream; 1..3 for FOA/HOA).
        /// </summary>
        public int HoaOrder { get; set; }

        /// <summary>
        /// Ambisonics channel ordering scheme when <see cref="HoaOrder"/> &gt; 0.
        /// </summary>
        public HoaChannelOrder HoaOrderScheme { get; set; }

        /// <summary>
        /// Ambisonics normalization convention when <see cref="HoaOrder"/> &gt; 0.
        /// </summary>
        public HoaNormalization HoaNormalization { get; set; }

        /// <summary>
        /// Live E-AC-3 channel coupling start frequency in Hz (0 when inactive or non-E-AC-3).
        /// </summary>
        public float CouplingStartHz { get; set; }

        /// <summary>
        /// Live E-AC-3 Spectral Extension (SPX) start frequency in Hz (0 when inactive or non-E-AC-3).
        /// </summary>
        public float SpxStartHz { get; set; }

        /// <summary>
        /// True when the decoded stream is a lossy spatial/surround codec (e.g., E-AC-3 JOC, AC-3) eligible for W09 LGSR.
        /// False for lossless streams (TrueHD, DAMF/ADM, FLAC, PCM, LAF).
        /// </summary>
        public bool IsLossySpatialStream { get; set; }

        /// <summary>
        /// Creates a default channel-based metadata descriptor with unspecified loudness tags.
        /// </summary>
        public static SpatialProgramMetadata CreateDefault() {
            return new SpatialProgramMetadata {
                ContentKind = SpatialContentKind.DirectSpeakers,
                DialnormLufs = float.NaN,
                IntegratedLoudnessLufs = float.NaN,
                HoaOrder = 0,
                HoaOrderScheme = HoaChannelOrder.Acn,
                HoaNormalization = HoaNormalization.None,
                CouplingStartHz = 0f,
                SpxStartHz = 0f,
                IsLossySpatialStream = false
            };
        }
    }

    /// <summary>
    /// Live acoustic telemetry snapshot updated per audio block for Room 3D X-Ray and loudness meters (W02 / W07 / W10).
    /// </summary>
    public struct SoundfieldTelemetry {
        /// <summary>
        /// Inter-channel / directional soundfield coherence in [0; 1] (1 = single coherent point source).
        /// </summary>
        public float Coherence { get; set; }

        /// <summary>
        /// Soundfield diffuseness estimate in [0; 1] (DirAC / COMPASS psi parameter).
        /// </summary>
        public float Diffuseness { get; set; }

        /// <summary>
        /// Dominant Direction-of-Arrival unit vector in room coordinates.
        /// </summary>
        public Vector3 DominantDoa { get; set; }

        /// <summary>
        /// Momentary loudness (400 ms window) in LUFS (ITU-R BS.1770-5), or <see cref="float.NaN"/> when inactive.
        /// </summary>
        public float MomentaryLufs { get; set; }

        /// <summary>
        /// Short-term loudness (3 s window) in LUFS (ITU-R BS.1770-5), or <see cref="float.NaN"/> when inactive.
        /// </summary>
        public float ShortTermLufs { get; set; }

        /// <summary>
        /// Maximum True Peak level in dBTP (4x-oversampled BS.1770-5 Annex 2 polyphase FIR).
        /// </summary>
        public float TruePeakDbfs { get; set; }

        /// <summary>
        /// Two-stage gated Integrated loudness in LUFS (ITU-R BS.1770-5), or <see cref="float.NaN"/> when unmeasured.
        /// </summary>
        public float IntegratedLufs { get; set; }

        /// <summary>
        /// EBU Tech 3342 Loudness Range (LRA) in LU (95th minus 10th percentile of gated 3 s blocks).
        /// </summary>
        public float LoudnessRangeLu { get; set; }

        /// <summary>
        /// Instantaneous Peak-to-RMS Crest Factor in dB (True Peak - 400 ms RMS).
        /// </summary>
        public float CrestFactorDb { get; set; }

        /// <summary>
        /// Active 4x True-Peak Limiter gain reduction in dB (&lt;= 0.0 dB).
        /// </summary>
        public float LimiterGainReductionDb { get; set; }

        /// <summary>
        /// Active ITU-R BS.1770-5 / Dolby Dialnorm loudness normalization gain currently applied in dB.
        /// </summary>
        public float AppliedLoudnessGainDb { get; set; }

        /// <summary>
        /// Active bitstream Dolby Dialnorm level in LUFS (e.g., -27 or -31 LUFS), or <see cref="float.NaN"/> when absent.
        /// </summary>
        public float ActiveDialnormLufs { get; set; }

        /// <summary>
        /// Live neural or coherence-derived vocal activity probability in [0; 1] (Slot 2B Spatial Karaoke / W07 Dialog hook).
        /// </summary>
        public float VocalActivity { get; set; }

        /// <summary>
        /// Active Spatial Karaoke vocal gain level in [0; 1] (1.0 = full vocals / bit-transparent bypass, 0.0 = instrumental).
        /// </summary>
        public float KaraokeVocalLevel { get; set; }

        /// <summary>
        /// W09 LGSR mean Magnitude-Squared Coherence <c>C_xy(t, f)</c> in [0; 1] against synced lossless FLAC twin.
        /// </summary>
        public float LgsrCoherence { get; set; }

        /// <summary>
        /// W09 LGSR active Pre-HRTF spectral fusion ratio in [0; 1].
        /// </summary>
        public float LgsrFusionRatio { get; set; }

        /// <summary>
        /// W09 GCC-PHAT sub-sample alignment offset in samples (lossless vs. Atmos).
        /// </summary>
        public float LgsrSyncOffsetSamples { get; set; }

        /// <summary>
        /// W09 Theil-Sen estimated clock drift in ppm.
        /// </summary>
        public float LgsrDriftPpm { get; set; }

        /// <summary>
        /// Live E-AC-3 channel coupling start frequency in Hz feeding LGSR &amp; W13 X-Ray HUD.
        /// </summary>
        public float LgsrCouplingHz { get; set; }

        /// <summary>
        /// Live E-AC-3 Spectral Extension (SPX) start frequency in Hz feeding LGSR &amp; W13 X-Ray HUD.
        /// </summary>
        public float LgsrSpxHz { get; set; }

        /// <summary>
        /// Live W10 USB/AAudio full-duplex microphone RMS level in dBFS (-120..0 dBFS).
        /// </summary>
        public float LiveMicRmsDbfs { get; set; }

        /// <summary>
        /// Live W10 USB/AAudio full-duplex microphone peak level in dBFS (-120..0 dBFS).
        /// </summary>
        public float LiveMicPeakDbfs { get; set; }

        /// <summary>
        /// Active W10 full-duplex round-trip latency in milliseconds (measured via MLS loopback or hardware timestamps).
        /// </summary>
        public float FullDuplexRoundTripMs { get; set; }

        /// <summary>
        /// True when a live microphone stream (<see cref="SourceLayer.LiveMicrophone"/>) is actively routed into the scene.
        /// </summary>
        public bool IsLiveMicActive { get; set; }

        /// <summary>
        /// Returns an empty telemetry snapshot.
        /// </summary>
        public static SoundfieldTelemetry Empty() {
            return new SoundfieldTelemetry {
                Coherence = 0f,
                Diffuseness = 0f,
                DominantDoa = Vector3.Zero,
                MomentaryLufs = float.NaN,
                ShortTermLufs = float.NaN,
                IntegratedLufs = float.NaN,
                LoudnessRangeLu = 0f,
                TruePeakDbfs = -120f,
                CrestFactorDb = 0f,
                LimiterGainReductionDb = 0f,
                AppliedLoudnessGainDb = 0f,
                ActiveDialnormLufs = float.NaN,
                VocalActivity = 0f,
                KaraokeVocalLevel = 1f,
                LgsrCoherence = 0f,
                LgsrFusionRatio = 0f,
                LgsrSyncOffsetSamples = 0f,
                LgsrDriftPpm = 0f,
                LgsrCouplingHz = 0f,
                LgsrSpxHz = 0f,
                LiveMicRmsDbfs = -120f,
                LiveMicPeakDbfs = -120f,
                FullDuplexRoundTripMs = 0f,
                IsLiveMicActive = false
            };
        }
    }
}
