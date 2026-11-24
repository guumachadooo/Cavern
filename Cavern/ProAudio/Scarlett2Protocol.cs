using System;

namespace Cavern.ProAudio {
    /// <summary>
    /// Recognized Focusrite Scarlett hardware generation (Vendor ID <c>0x1235</c>).
    /// </summary>
    public enum ScarlettGeneration {
        /// <summary>Not a Focusrite Scarlett interface.</summary>
        None = 0,
        /// <summary>Scarlett 2i2 / Solo 2nd Gen (hardware front-panel switches; UAC2 streaming).</summary>
        Gen2 = 2,
        /// <summary>Scarlett 2i2 / Solo 3rd Gen (Scarlett2 USB vendor control for 48V, Air, Inst/Line, Direct Monitor, MSD exit, Meters).</summary>
        Gen3 = 3,
        /// <summary>Scarlett 2i2 / Solo / 4i4 4th Gen (Focusrite Control Protocol FCP; dynamic firmware-discovered control map).</summary>
        Gen4 = 4
    }

    /// <summary>
    /// Underlying USB vendor control protocol family used by a Focusrite Scarlett hardware generation.
    /// </summary>
    public enum ScarlettProtocolFamily {
        /// <summary>Not a Focusrite Scarlett device.</summary>
        None = 0,
        /// <summary>Standard UAC2 streaming only (Gen 2 physical front-panel switches).</summary>
        Uac2Only = 1,
        /// <summary>Linux ALSA <c>mixer_scarlett2.c</c> verified Gen 3 static offset table (<c>SET_DATA</c>/<c>GET_DATA</c>).</summary>
        Scarlett2Gen3 = 2,
        /// <summary>Focusrite Control Protocol (FCP) used by Gen 4 interfaces (requires dynamic firmware control table probe).</summary>
        FocusriteFcpGen4 = 3
    }

    /// <summary>
    /// Focusrite Scarlett Direct Monitor hardware routing mode.
    /// </summary>
    public enum ScarlettDirectMonitorMode {
        /// <summary>Direct hardware monitoring disabled (monitor solely through Cavernize low-latency spatial DSP).</summary>
        Off = 0,
        /// <summary>Mono direct monitor (Inputs 1 &amp; 2 summed to center).</summary>
        Mono = 1,
        /// <summary>Stereo direct monitor (Input 1 Left, Input 2 Right).</summary>
        Stereo = 2
    }

    /// <summary>
    /// Focusrite Scarlett Air analog preamp emulation mode.
    /// </summary>
    public enum ScarlettAirMode {
        /// <summary>Air circuit bypassed (clean transparent preamp).</summary>
        Off = 0,
        /// <summary>Presence Air (Gen 3 &amp; Gen 4 ISA transformer presence lift).</summary>
        Presence = 1,
        /// <summary>Presence + Harmonic Drive (Gen 4 dual-stage Air mode).</summary>
        PresenceAndDrive = 2
    }

    /// <summary>
    /// Focusrite Scarlett 4th Gen hardware Auto Gain calibration status.
    /// </summary>
    public enum ScarlettAutoGainStatus {
        /// <summary>Auto Gain is idle.</summary>
        Idle = 0,
        /// <summary>Hardware is actively listening to the input signal (~10 s window) to compute optimal gain.</summary>
        Running = 1,
        /// <summary>Optimal preamp gain was computed and applied cleanly.</summary>
        Succeeded = 2,
        /// <summary>Signal was too quiet, clipped continuously, or calibration was cancelled.</summary>
        Failed = 3
    }

    /// <summary>
    /// Snapshot of Focusrite Scarlett 2i2 / Solo / 4i4 hardware preamp, Auto Gain, Air, Clip Safe,
    /// Stereo Link, Direct Monitor, and output control state plus live peak meters.
    /// </summary>
    public sealed class ScarlettHardwareState {
        /// <summary>Detected Scarlett generation.</summary>
        public ScarlettGeneration Generation { get; set; }

        /// <summary>Human-readable model name (e.g., "Focusrite Scarlett 2i2 (4th Gen)").</summary>
        public string ModelName { get; set; } = string.Empty;

        /// <summary>USB Product ID.</summary>
        public int ProductId { get; set; }

        /// <summary>Underlying vendor control protocol family for this hardware generation.</summary>
        public ScarlettProtocolFamily ProtocolFamily {
            get {
                if (Generation == ScarlettGeneration.Gen2) {
                    return ScarlettProtocolFamily.Uac2Only;
                }
                if (Generation == ScarlettGeneration.Gen3) {
                    return ScarlettProtocolFamily.Scarlett2Gen3;
                }
                if (Generation == ScarlettGeneration.Gen4) {
                    return ScarlettProtocolFamily.FocusriteFcpGen4;
                }
                return ScarlettProtocolFamily.None;
            }
        }

        /// <summary>
        /// True when the connected Gen 4 Focusrite Control Protocol (FCP) control table was dynamically discovered
        /// from device firmware. False when unprobed so static offsets are never written blindly to Gen 4 NVRAM.
        /// </summary>
        public bool IsGen4FcpControlTableVerified { get; set; }

        /// <summary>True when USB vendor handshake/metering or software controls are supported (Gen 3 verified; Gen 4 gated by FCP probe).</summary>
        public bool SupportsSoftwareControls {
            get {
                return Generation == ScarlettGeneration.Gen3 ||
                       (Generation == ScarlettGeneration.Gen4 && IsGen4FcpControlTableVerified);
            }
        }

        /// <summary>True when hardware peak meter queries or live meter-assisted Auto Gain recommendations are supported.</summary>
        public bool SupportsMeteringAndGainAssist {
            get { return Generation == ScarlettGeneration.Gen3 || Generation == ScarlettGeneration.Gen4; }
        }

        /// <summary>True when per-channel independent 48V and digital preamp gain dB writes are verified (Gen 4 with FCP control map).</summary>
        public bool SupportsIndependentPreampGain {
            get { return Generation == ScarlettGeneration.Gen4 && IsGen4FcpControlTableVerified; }
        }

        /// <summary>True when hardware Auto Gain and per-channel Clip Safe writes are verified (Gen 4 with FCP control map).</summary>
        public bool SupportsAutoGain {
            get { return Generation == ScarlettGeneration.Gen4 && IsGen4FcpControlTableVerified; }
        }

        /// <summary>True when Stereo Channel 1-2 Linking writes are verified (Gen 4 2i2 / 4i4 with FCP control map).</summary>
        public bool SupportsStereoLink {
            get {
                return Generation == ScarlettGeneration.Gen4 &&
                       IsGen4FcpControlTableVerified &&
                       ProductId != Scarlett2Protocol.PidScarlettSoloGen4;
            }
        }

        /// <summary>True when Inputs 1 and 2 are stereo-linked (Gain, Air, 48V, Clip Safe, Inst, Auto Gain move together).</summary>
        public bool InputsLinked { get; set; }

        /// <summary>48V Phantom Power Channel 1 (shared with Ch 2 on Gen 3).</summary>
        public bool PhantomPowerCh1 { get; set; }

        /// <summary>48V Phantom Power Channel 2 (independent on Gen 4, linked to Ch 1 on Gen 3).</summary>
        public bool PhantomPowerCh2 { get; set; }

        /// <summary>Air preamp mode Channel 1.</summary>
        public ScarlettAirMode AirCh1 { get; set; }

        /// <summary>Air preamp mode Channel 2.</summary>
        public ScarlettAirMode AirCh2 { get; set; }

        /// <summary>Instrument Hi-Z mode Channel 1 (false = Line, true = Inst).</summary>
        public bool InstrumentModeCh1 { get; set; }

        /// <summary>Instrument Hi-Z mode Channel 2 (false = Line, true = Inst).</summary>
        public bool InstrumentModeCh2 { get; set; }

        /// <summary>Hardware direct monitoring mode (Off / Mono / Stereo).</summary>
        public ScarlettDirectMonitorMode DirectMonitor { get; set; }

        /// <summary>Direct Monitor Input vs Playback blend percentage (0 = 100% Input, 50 = Balanced, 100 = 100% Playback).</summary>
        public int DirectMonitorMixPct { get; set; } = 50;

        /// <summary>Input 1 digital preamp gain in dB (Gen 4: 0..69 dB).</summary>
        public float InputGainCh1Db { get; set; } = 24f;

        /// <summary>Input 2 digital preamp gain in dB (Gen 4: 0..69 dB).</summary>
        public float InputGainCh2Db { get; set; } = 24f;

        /// <summary>Gen 4 Clip Safe automatic overload protection for Channel 1.</summary>
        public bool ClipSafeCh1 { get; set; }

        /// <summary>Gen 4 Clip Safe automatic overload protection for Channel 2.</summary>
        public bool ClipSafeCh2 { get; set; }

        /// <summary>Backwards-compatible aggregate Clip Safe indicator (true when either channel is protected).</summary>
        public bool ClipSafeEnabled {
            get { return ClipSafeCh1 || ClipSafeCh2; }
            set {
                ClipSafeCh1 = value;
                ClipSafeCh2 = value;
            }
        }

        /// <summary>Gen 4 Auto Gain calibration status on Channel 1.</summary>
        public ScarlettAutoGainStatus AutoGainStatusCh1 { get; set; }

        /// <summary>Gen 4 Auto Gain calibration status on Channel 2.</summary>
        public ScarlettAutoGainStatus AutoGainStatusCh2 { get; set; }

        /// <summary>Main monitor output mute state.</summary>
        public bool OutputMute { get; set; }

        /// <summary>Main monitor output dim (-20 dB) state.</summary>
        public bool OutputDim { get; set; }

        /// <summary>Gen 4 virtual Loopback routing enable.</summary>
        public bool LoopbackEnabled { get; set; }

        /// <summary>Live hardware peak meter Channel 1 in dBFS (from <c>SCARLETT2_USB_GET_METER</c>).</summary>
        public float InputMeterCh1Dbfs { get; set; } = -120f;

        /// <summary>Live hardware peak meter Channel 2 in dBFS (from <c>SCARLETT2_USB_GET_METER</c>).</summary>
        public float InputMeterCh2Dbfs { get; set; } = -120f;

        /// <summary>Device firmware version reported by <c>CMD_INIT_2</c> (0 if unknown or unqueried).</summary>
        public int FirmwareVersion { get; set; }
    }

    /// <summary>
    /// Verified Focusrite Control Protocol (FCP) Gen 4 configuration item descriptor matching
    /// Linux ALSA <c>sound/usb/mixer_scarlett2.c</c>.
    /// </summary>
    public readonly struct ScarlettConfigItem : IEquatable<ScarlettConfigItem> {
        /// <summary>NVRAM/config byte offset within the device parameter memory space.</summary>
        public uint Offset { get; }

        /// <summary>Control bit width (typically 8 or 16 bits).</summary>
        public int SizeBits { get; }

        /// <summary>Activation command ID passed to <see cref="Scarlett2Protocol.CmdDataActivate"/> after writing (0 if none).</summary>
        public int ActivateCmd { get; }

        /// <summary>True when writes stage through <see cref="ScarlettGen4ConfigTable.ParamBufAddr"/> before activation.</summary>
        public bool UseParamBuf { get; }

        /// <summary>True when this item is populated for the active Gen 4 hardware model.</summary>
        public bool IsValid {
            get { return SizeBits > 0; }
        }

        /// <summary>Control byte width rounded up from <see cref="SizeBits"/>.</summary>
        public int SizeBytes {
            get { return (SizeBits + 7) / 8; }
        }

        /// <summary>Creates an immutable Scarlett Gen 4 FCP configuration item descriptor.</summary>
        public ScarlettConfigItem(uint offset, int sizeBits, int activateCmd, bool useParamBuf = false) {
            Offset = offset;
            SizeBits = sizeBits;
            ActivateCmd = activateCmd;
            UseParamBuf = useParamBuf;
        }

        /// <inheritdoc/>
        public bool Equals(ScarlettConfigItem other) {
            return Offset == other.Offset &&
                   SizeBits == other.SizeBits &&
                   ActivateCmd == other.ActivateCmd &&
                   UseParamBuf == other.UseParamBuf;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj) {
            return obj is ScarlettConfigItem other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode() {
            unchecked {
                int hash = (int)Offset;
                hash = (hash * 397) ^ SizeBits;
                hash = (hash * 397) ^ ActivateCmd;
                hash = (hash * 397) ^ (UseParamBuf ? 1 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Firmware-resolved Focusrite Scarlett 4th Gen FCP configuration offset table derived from
    /// Linux kernel <c>sound/usb/mixer_scarlett2.c</c>.
    /// </summary>
    public sealed class ScarlettGen4ConfigTable {
        /// <summary>USB Product ID associated with this Gen 4 config table.</summary>
        public int ProductId { get; }

        /// <summary>Firmware version used to resolve firmware-conditional offsets.</summary>
        public int FirmwareVersion { get; }

        /// <summary>Base parameter staging buffer address (<c>parambuf</c>) used by items with <see cref="ScarlettConfigItem.UseParamBuf"/>.</summary>
        public uint ParamBufAddr { get; }

        /// <summary>MSD ("Easy Start") mode toggle.</summary>
        public ScarlettConfigItem Msd { get; }

        /// <summary>Direct Monitor routing switch (Off/Mono/Stereo on Solo / 2i2).</summary>
        public ScarlettConfigItem DirectMonitor { get; }

        /// <summary>Direct Monitor level gain (16-bit on Solo / 2i2; firmware-conditional on 2i2 Gen 4).</summary>
        public ScarlettConfigItem DirectMonitorGain { get; }

        /// <summary>Hardware Auto Gain trigger switch (2i2 / 4i4).</summary>
        public ScarlettConfigItem AutoGainSwitch { get; }

        /// <summary>Hardware Auto Gain live status readout (2i2 / 4i4).</summary>
        public ScarlettConfigItem AutoGainStatus { get; }

        /// <summary>48V Phantom Power switch.</summary>
        public ScarlettConfigItem PhantomPower { get; }

        /// <summary>Digital preamp gain control (2i2 / 4i4).</summary>
        public ScarlettConfigItem InputGain { get; }

        /// <summary>Instrument Hi-Z / Line level switch.</summary>
        public ScarlettConfigItem LevelSwitch { get; }

        /// <summary>Clip Safe automatic overload guard switch (2i2 / 4i4).</summary>
        public ScarlettConfigItem SafeSwitch { get; }

        /// <summary>Air analog/DSP preamp emulation switch (Off / Presence / Presence+Drive).</summary>
        public ScarlettConfigItem AirSwitch { get; }

        /// <summary>Channel selector switch for shared front-panel preamp controls (2i2 / 4i4).</summary>
        public ScarlettConfigItem InputSelectSwitch { get; }

        /// <summary>Channels 1-2 stereo link switch (2i2 / 4i4).</summary>
        public ScarlettConfigItem InputLinkSwitch { get; }

        /// <summary>Diagnostic note documenting firmware gating and <see cref="Scarlett2Protocol.CmdInfoDevmap"/> fallback.</summary>
        public string FallbackNote { get; }

        /// <summary>Constructs an immutable verified Gen 4 FCP configuration offset table.</summary>
        public ScarlettGen4ConfigTable(
            int productId,
            int firmwareVersion,
            uint paramBufAddr,
            ScarlettConfigItem msd,
            ScarlettConfigItem directMonitor,
            ScarlettConfigItem directMonitorGain,
            ScarlettConfigItem autoGainSwitch,
            ScarlettConfigItem autoGainStatus,
            ScarlettConfigItem phantomPower,
            ScarlettConfigItem inputGain,
            ScarlettConfigItem levelSwitch,
            ScarlettConfigItem safeSwitch,
            ScarlettConfigItem airSwitch,
            ScarlettConfigItem inputSelectSwitch,
            ScarlettConfigItem inputLinkSwitch,
            string fallbackNote = "") {
            ProductId = productId;
            FirmwareVersion = firmwareVersion;
            ParamBufAddr = paramBufAddr;
            Msd = msd;
            DirectMonitor = directMonitor;
            DirectMonitorGain = directMonitorGain;
            AutoGainSwitch = autoGainSwitch;
            AutoGainStatus = autoGainStatus;
            PhantomPower = phantomPower;
            InputGain = inputGain;
            LevelSwitch = levelSwitch;
            SafeSwitch = safeSwitch;
            AirSwitch = airSwitch;
            InputSelectSwitch = inputSelectSwitch;
            InputLinkSwitch = inputLinkSwitch;
            FallbackNote = fallbackNote ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable, lock-free hardware pre-ADC state snapshot published by Focusrite Scarlett / USB audio managers
    /// so Slot 2D Vocal Chain avoids double-processing hardware Air/Drive and future Phase 3 <c>Slot 2D-SHIELD</c>
    /// resets FDKF covariance cleanly on preamp gain steps.
    /// </summary>
    public sealed class HardwarePreAmpStateSnapshot : IEquatable<HardwarePreAmpStateSnapshot> {
        /// <summary>Mic source integer constant matching built-in Phone Mic (<c>0</c>).</summary>
        public const int MicSourcePhone = 0;

        /// <summary>Mic source integer constant matching external USB Audio Interface Mic (<c>1</c>).</summary>
        public const int MicSourceUsbInterface = 1;

        /// <summary>Mic source integer constant matching automatic source selection (<c>2</c>).</summary>
        public const int MicSourceAuto = 2;

        static readonly HardwarePreAmpStateSnapshot empty = new HardwarePreAmpStateSnapshot();

        /// <summary>Neutral unverified singleton snapshot when no verified USB preamp is active.</summary>
        public static HardwarePreAmpStateSnapshot Empty {
            get { return empty; }
        }

        /// <summary>Active hardware Air preamp coloration mode.</summary>
        public ScarlettAirMode AirMode { get; }

        /// <summary>True when hardware harmonic Drive saturation stage is engaged.</summary>
        public bool DriveActive { get; }

        /// <summary>True when hardware Auto Gain calibration is currently running.</summary>
        public bool AutoGainActive { get; }

        /// <summary>True when hardware Clip Safe overload protection is active.</summary>
        public bool ClipSafeActive { get; }

        /// <summary>Active hardware preamp gain in dB.</summary>
        public float PreampGainDb { get; }

        /// <summary>Monotonic counter incremented whenever hardware preamp gain steps (for Phase 3 <c>2D-SHIELD</c> FDKF reset).</summary>
        public long GainStepEpoch { get; }

        /// <summary>True when monitoring output is routed to headphones/IEMs rather than an open loudspeaker.</summary>
        public bool IsHeadphoneMonitoring { get; }

        /// <summary>Active microphone source index (<c>0 = PhoneMic</c>, <c>1 = UsbInterfaceMic</c>, <c>2 = Auto</c>).</summary>
        public int MicSource { get; }

        /// <summary>True when hardware telemetry came from a verified USB vendor/FCP control probe.</summary>
        public bool IsHardwareVerified { get; }

        /// <summary>Creates an immutable pre-ADC hardware state snapshot.</summary>
        public HardwarePreAmpStateSnapshot(
            ScarlettAirMode airMode = ScarlettAirMode.Off,
            bool driveActive = false,
            bool autoGainActive = false,
            bool clipSafeActive = false,
            float preampGainDb = 0f,
            long gainStepEpoch = 0L,
            bool isHeadphoneMonitoring = false,
            int micSource = MicSourcePhone,
            bool isHardwareVerified = false) {
            AirMode = airMode;
            DriveActive = driveActive;
            AutoGainActive = autoGainActive;
            ClipSafeActive = clipSafeActive;
            PreampGainDb = float.IsNaN(preampGainDb) || float.IsInfinity(preampGainDb) ? 0f : preampGainDb;
            GainStepEpoch = gainStepEpoch;
            IsHeadphoneMonitoring = isHeadphoneMonitoring;
            MicSource = micSource;
            IsHardwareVerified = isHardwareVerified;
        }

        /// <summary>Returns a new immutable snapshot with only the specified properties modified.</summary>
        public HardwarePreAmpStateSnapshot With(
            ScarlettAirMode? airMode = null,
            bool? driveActive = null,
            bool? autoGainActive = null,
            bool? clipSafeActive = null,
            float? preampGainDb = null,
            long? gainStepEpoch = null,
            bool? isHeadphoneMonitoring = null,
            int? micSource = null,
            bool? isHardwareVerified = null) {
            return new HardwarePreAmpStateSnapshot(
                airMode.HasValue ? airMode.Value : AirMode,
                driveActive.HasValue ? driveActive.Value : DriveActive,
                autoGainActive.HasValue ? autoGainActive.Value : AutoGainActive,
                clipSafeActive.HasValue ? clipSafeActive.Value : ClipSafeActive,
                preampGainDb.HasValue ? preampGainDb.Value : PreampGainDb,
                gainStepEpoch.HasValue ? gainStepEpoch.Value : GainStepEpoch,
                isHeadphoneMonitoring.HasValue ? isHeadphoneMonitoring.Value : IsHeadphoneMonitoring,
                micSource.HasValue ? micSource.Value : MicSource,
                isHardwareVerified.HasValue ? isHardwareVerified.Value : IsHardwareVerified);
        }

        /// <inheritdoc/>
        public bool Equals(HardwarePreAmpStateSnapshot other) {
            if (other == null) {
                return false;
            }
            if (ReferenceEquals(this, other)) {
                return true;
            }
            return AirMode == other.AirMode &&
                   DriveActive == other.DriveActive &&
                   AutoGainActive == other.AutoGainActive &&
                   ClipSafeActive == other.ClipSafeActive &&
                   PreampGainDb.Equals(other.PreampGainDb) &&
                   GainStepEpoch == other.GainStepEpoch &&
                   IsHeadphoneMonitoring == other.IsHeadphoneMonitoring &&
                   MicSource == other.MicSource &&
                   IsHardwareVerified == other.IsHardwareVerified;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj) {
            return Equals(obj as HardwarePreAmpStateSnapshot);
        }

        /// <inheritdoc/>
        public override int GetHashCode() {
            unchecked {
                int hash = (int)AirMode;
                hash = (hash * 397) ^ (DriveActive ? 1 : 0);
                hash = (hash * 397) ^ (AutoGainActive ? 1 : 0);
                hash = (hash * 397) ^ (ClipSafeActive ? 1 : 0);
                hash = (hash * 397) ^ PreampGainDb.GetHashCode();
                hash = (hash * 397) ^ GainStepEpoch.GetHashCode();
                hash = (hash * 397) ^ (IsHeadphoneMonitoring ? 1 : 0);
                hash = (hash * 397) ^ MicSource;
                hash = (hash * 397) ^ (IsHardwareVerified ? 1 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Clean-room Focusrite Scarlett USB Vendor Control Protocol codec based strictly on the public Linux kernel
    /// ALSA specification (<c>sound/usb/mixer_scarlett2.c</c>) and <c>geoffreybennett/alsa-scarlett-gui</c>.
    /// Supports Focusrite Scarlett 2i2 Gen 2 (<c>0x8202</c>), Gen 3 (<c>0x8210</c>), Gen 4 (<c>0x8219</c>),
    /// 4i4 Gen 4 (<c>0x821A</c>), and Solo Gen 2/3/4 under Focusrite Vendor ID <c>0x1235</c>.
    /// </summary>
    public static class Scarlett2Protocol {
        /// <summary>Focusrite-Novation USB Vendor ID.</summary>
        public const int VendorIdFocusrite = 0x1235;

        /// <summary>Focusrite Scarlett 2i2 (2nd Gen) USB Product ID.</summary>
        public const int PidScarlett2i2Gen2 = 0x8202;
        /// <summary>Focusrite Scarlett Solo (2nd Gen) USB Product ID.</summary>
        public const int PidScarlettSoloGen2 = 0x8205;
        /// <summary>Focusrite Scarlett 2i2 (3rd Gen) USB Product ID.</summary>
        public const int PidScarlett2i2Gen3 = 0x8210;
        /// <summary>Focusrite Scarlett Solo (3rd Gen) USB Product ID.</summary>
        public const int PidScarlettSoloGen3 = 0x8211;
        /// <summary>Focusrite Scarlett Solo (4th Gen) USB Product ID.</summary>
        public const int PidScarlettSoloGen4 = 0x8218;
        /// <summary>Focusrite Scarlett 2i2 (4th Gen) USB Product ID.</summary>
        public const int PidScarlett2i2Gen4 = 0x8219;
        /// <summary>Focusrite Scarlett 4i4 (4th Gen) USB Product ID.</summary>
        public const int PidScarlett4i4Gen4 = 0x821A;

        /// <summary>USB control transfer bRequest: Step 0 protocol readiness / init query (IN, 0xA1, 24 bytes).</summary>
        public const byte UsbReqCmdInit = 0x00;
        /// <summary>USB control transfer bRequest: send Scarlett2 command request (OUT, 0x21).</summary>
        public const byte UsbReqCmdSend = 0x02;
        /// <summary>USB control transfer bRequest: read Scarlett2 command response (IN, 0xA1).</summary>
        public const byte UsbReqCmdRecv = 0x03;

        /// <summary>Fixed header size of <c>struct scarlett2_usb_packet</c> in bytes (cmd, size, seq, error, pad).</summary>
        public const int PacketHeaderBytes = 16;

        /// <summary>Command ID: protocol handshake / init step 1.</summary>
        public const uint CmdInit1 = 0x00000000u;
        /// <summary>Command ID: protocol init step 2 (enable host control mode).</summary>
        public const uint CmdInit2 = 0x00000002u;
        /// <summary>Command ID: save active configuration to non-volatile NVRAM.</summary>
        public const uint CmdConfigSave = 0x00000006u;
        /// <summary>Command ID: query live hardware level meters (uint16 linear peaks).</summary>
        public const uint CmdGetMeter = 0x00001001u;
        /// <summary>Command ID: read configuration data block (<c>SCARLETT2_USB_GET_DATA</c>).</summary>
        public const uint CmdGetData = 0x00800000u;
        /// <summary>Command ID: write configuration data block (<c>SCARLETT2_USB_SET_DATA</c>).</summary>
        public const uint CmdSetData = 0x00800001u;
        /// <summary>Command ID: commit/activate staged Gen 4 FCP configuration parameter (<c>SCARLETT2_USB_DATA_CMD</c>).</summary>
        public const uint CmdDataActivate = 0x00800002u;
        /// <summary>Command ID: query Gen 4 FCP device configuration memory map (<c>SCARLETT2_USB_INFO_DEVMAP</c>).</summary>
        public const uint CmdInfoDevmap = 0x0080000Cu;

        // Public mixer_scarlett2.c Gen 3 Scarlett 2i2 configuration offsets (clean-room spec)
        /// <summary>Gen 3 2i2 NVRAM/config offset: MSD ("Easy Start") mode flag (0 = off / normal audio mode).</summary>
        public const uint Gen3OffsetMsdMode = 0x00000017u;
        /// <summary>Gen 3 2i2 config offset: Level switch (Inst/Line) per channel (2 bytes).</summary>
        public const uint Gen3OffsetLevelSwitch = 0x0000000Eu;
        /// <summary>Gen 3 2i2 config offset: Air mode switch per channel (2 bytes).</summary>
        public const uint Gen3OffsetAirSwitch = 0x00000010u;
        /// <summary>Gen 3 2i2 config offset: Shared 48V Phantom Power switch (1 byte).</summary>
        public const uint Gen3OffsetPhantomPower = 0x00000012u;
        /// <summary>Gen 3 2i2 config offset: Direct Monitor mode (0=Off, 1=Mono, 2=Stereo).</summary>
        public const uint Gen3OffsetDirectMonitor = 0x00000014u;

        // Nominal fallback Gen 4 reference offsets (used strictly when firmware table verification is gated off or
        // awaiting CmdInfoDevmap 0x0080000C / TryResolveGen4ConfigTable resolution; never written blindly to NVRAM
        // while IsGen4FcpControlTableVerified is false). For 2i2 Gen 4 (0x8219), verified static FCP offsets require fw >= 2115.
        /// <summary>Gen 4 FCP fallback reference offset (gated): MSD ("Easy Start") mode flag.</summary>
        public const uint Gen4OffsetMsdMode = 0x00000008u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel 48V Phantom Power (2 bytes).</summary>
        public const uint Gen4OffsetPhantomPower = 0x00000010u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel Air mode (0=Off, 1=Presence, 2=Presence+Drive, 2 bytes).</summary>
        public const uint Gen4OffsetAirMode = 0x00000012u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel Instrument Hi-Z / Line switch (2 bytes).</summary>
        public const uint Gen4OffsetLevelSwitch = 0x00000014u;
        /// <summary>Gen 4 FCP reference offset (gated): Direct Monitor mode (0=Off, 1=Mono, 2=Stereo).</summary>
        public const uint Gen4OffsetDirectMonitor = 0x00000018u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel digital preamp gain (int16 dB, 0..69 dB).</summary>
        public const uint Gen4OffsetPreampGainDb = 0x00000020u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel Clip Safe toggle (2 bytes).</summary>
        public const uint Gen4OffsetClipSafe = 0x00000028u;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel Auto Gain trigger/cancel flag (2 bytes: 0=Stop, 1=Start).</summary>
        public const uint Gen4OffsetAutoGainEnable = 0x0000002Cu;
        /// <summary>Gen 4 FCP reference offset (gated): Per-channel Auto Gain status readout (2 bytes: ScarlettAutoGainStatus).</summary>
        public const uint Gen4OffsetAutoGainStatus = 0x00000030u;
        /// <summary>Gen 4 FCP reference offset (gated): Stereo Link Channels 1-2 flag (1 byte).</summary>
        public const uint Gen4OffsetInputLink = 0x00000034u;
        /// <summary>Gen 4 FCP reference offset (gated): Direct Monitor Input/Playback blend (0..100, 1 byte).</summary>
        public const uint Gen4OffsetDirectMonitorMix = 0x00000038u;
        /// <summary>Gen 4 FCP reference offset (gated): Main Monitor Output Mute flag (1 byte).</summary>
        public const uint Gen4OffsetOutputMute = 0x0000003Cu;
        /// <summary>Gen 4 FCP reference offset (gated): Main Monitor Output Dim (-20 dB) flag (1 byte).</summary>
        public const uint Gen4OffsetOutputDim = 0x0000003Eu;
        /// <summary>Gen 4 FCP reference offset (gated): Virtual Loopback routing toggle (1 byte).</summary>
        public const uint Gen4OffsetLoopback = 0x00000040u;

        /// <summary>
        /// Identifies a Focusrite Scarlett device from its USB Vendor ID and Product ID.
        /// </summary>
        public static ScarlettGeneration IdentifyDevice(int vendorId, int productId, out string modelName) {
            modelName = string.Empty;
            if (vendorId != VendorIdFocusrite) {
                return ScarlettGeneration.None;
            }
            switch (productId) {
                case PidScarlett2i2Gen2:
                    modelName = "Focusrite Scarlett 2i2 (2nd Gen)";
                    return ScarlettGeneration.Gen2;
                case PidScarlettSoloGen2:
                    modelName = "Focusrite Scarlett Solo (2nd Gen)";
                    return ScarlettGeneration.Gen2;
                case PidScarlett2i2Gen3:
                    modelName = "Focusrite Scarlett 2i2 (3rd Gen)";
                    return ScarlettGeneration.Gen3;
                case PidScarlettSoloGen3:
                    modelName = "Focusrite Scarlett Solo (3rd Gen)";
                    return ScarlettGeneration.Gen3;
                case PidScarlett2i2Gen4:
                    modelName = "Focusrite Scarlett 2i2 (4th Gen)";
                    return ScarlettGeneration.Gen4;
                case PidScarlettSoloGen4:
                    modelName = "Focusrite Scarlett Solo (4th Gen)";
                    return ScarlettGeneration.Gen4;
                case PidScarlett4i4Gen4:
                    modelName = "Focusrite Scarlett 4i4 (4th Gen)";
                    return ScarlettGeneration.Gen4;
                default:
                    return ScarlettGeneration.None;
            }
        }

        /// <summary>
        /// Computes an optimal Focusrite Gen 4 preamp gain (0..69 dB) targeting <paramref name="targetPeakDbfs"/>
        /// (default -12 dBFS vocal headroom) from a measured peak level and current preamp gain.
        /// </summary>
        public static float ComputeRecommendedAutoGainDb(float measuredPeakDbfs, float currentGainDb, float targetPeakDbfs = -12f) {
            if (measuredPeakDbfs <= -95f) {
                return currentGainDb;
            }
            float delta = targetPeakDbfs - measuredPeakDbfs;
            float recommended = currentGainDb + delta;
            if (recommended < 0f) {
                return 0f;
            }
            if (recommended > 69f) {
                return 69f;
            }
            return (float)Math.Round(recommended);
        }

        /// <summary>
        /// Encodes a Focusrite Scarlett2 USB command packet (<c>struct scarlett2_usb_packet</c>) in little-endian wire order.
        /// </summary>
        public static byte[] BuildCommandPacket(uint cmd, ushort seq, byte[] payload) {
            int payloadLen = payload != null ? payload.Length : 0;
            byte[] packet = new byte[PacketHeaderBytes + payloadLen];
            WriteUInt32Le(packet, 0, cmd);
            WriteUInt16Le(packet, 4, (ushort)payloadLen);
            WriteUInt16Le(packet, 6, seq);
            WriteUInt32Le(packet, 8, 0u);  // error = 0
            WriteUInt32Le(packet, 12, 0u); // pad = 0
            if (payloadLen > 0) {
                Buffer.BlockCopy(payload, 0, packet, PacketHeaderBytes, payloadLen);
            }
            return packet;
        }

        /// <summary>
        /// Builds a <see cref="CmdSetData"/> packet writing <paramref name="valueBytes"/> to <paramref name="configOffset"/>.
        /// </summary>
        public static byte[] BuildSetDataPacket(ushort seq, uint configOffset, byte[] valueBytes) {
            int valLen = valueBytes != null ? valueBytes.Length : 0;
            byte[] payload = new byte[8 + valLen];
            WriteUInt32Le(payload, 0, configOffset);
            WriteUInt32Le(payload, 4, (uint)valLen);
            if (valLen > 0) {
                Buffer.BlockCopy(valueBytes, 0, payload, 8, valLen);
            }
            return BuildCommandPacket(CmdSetData, seq, payload);
        }

        /// <summary>
        /// Builds a <see cref="CmdGetData"/> packet reading <paramref name="length"/> bytes from <paramref name="configOffset"/>.
        /// </summary>
        public static byte[] BuildGetDataPacket(ushort seq, uint configOffset, uint length) {
            byte[] payload = new byte[8];
            WriteUInt32Le(payload, 0, configOffset);
            WriteUInt32Le(payload, 4, length);
            return BuildCommandPacket(CmdGetData, seq, payload);
        }

        /// <summary>
        /// Builds a <see cref="CmdGetMeter"/> request packet for <paramref name="meterCount"/> hardware peak meters.
        /// </summary>
        public static byte[] BuildGetMeterPacket(ushort seq, ushort meterCount) {
            byte[] payload = new byte[4];
            WriteUInt16Le(payload, 0, 0);
            WriteUInt16Le(payload, 2, meterCount);
            return BuildCommandPacket(CmdGetMeter, seq, payload);
        }

        /// <summary>
        /// Builds a <see cref="CmdDataActivate"/> packet committing a staged Gen 4 FCP configuration write
        /// using <paramref name="activateCmd"/> from <see cref="ScarlettConfigItem.ActivateCmd"/>.
        /// </summary>
        public static byte[] BuildActivateConfigPacket(ushort seq, uint activateCmd) {
            byte[] payload = new byte[4];
            WriteUInt32Le(payload, 0, activateCmd);
            return BuildCommandPacket(CmdDataActivate, seq, payload);
        }

        /// <summary>
        /// Builds a <see cref="CmdInfoDevmap"/> (<c>0x0080000C</c>) packet querying Gen 4 FCP device map info.
        /// </summary>
        public static byte[] BuildInfoDevmapPacket(ushort seq) {
            return BuildCommandPacket(CmdInfoDevmap, seq, null);
        }

        /// <summary>
        /// Extracts the integer firmware version from a <see cref="CmdInit2"/> response packet
        /// (primary <c>PacketHeaderBytes + 8</c>, with safe fallbacks at <c>+4</c> and <c>+12</c> if zero).
        /// </summary>
        public static bool TryExtractFirmwareVersion(byte[] rxPacket, int rxLength, out int firmwareVersion) {
            firmwareVersion = 0;
            if (rxPacket == null ||
                rxLength < PacketHeaderBytes + 8 ||
                rxLength > rxPacket.Length) {
                return false;
            }
            uint cmd = ReadUInt32Le(rxPacket, 0);
            uint errorCode = ReadUInt32Le(rxPacket, 8);
            if (errorCode != 0u || (cmd != 0u && cmd != CmdInit2)) {
                return false;
            }
            if (rxLength >= PacketHeaderBytes + 12) {
                uint rawFw = ReadUInt32Le(rxPacket, PacketHeaderBytes + 8);
                if (rawFw > 0u && rawFw <= int.MaxValue) {
                    firmwareVersion = (int)rawFw;
                    return true;
                }
            }
            uint altFw4 = ReadUInt32Le(rxPacket, PacketHeaderBytes + 4);
            if (altFw4 >= 100u && altFw4 <= 999999u) {
                firmwareVersion = (int)altFw4;
                return true;
            }
            if (rxLength >= PacketHeaderBytes + 16) {
                uint altFw12 = ReadUInt32Le(rxPacket, PacketHeaderBytes + 12);
                if (altFw12 >= 100u && altFw12 <= 999999u) {
                    firmwareVersion = (int)altFw12;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves the Linux ALSA <c>mixer_scarlett2.c</c>-verified Focusrite Control Protocol (FCP) Gen 4
        /// configuration offset table for <paramref name="productId"/> and <paramref name="firmwareVersion"/>.
        /// Returns <c>false</c> for unrecognized PIDs or unverified pre-2115 2i2 Gen 4 firmware so callers fall back
        /// safely to <see cref="CmdInfoDevmap"/> probing or read-only metering rather than writing blind NVRAM offsets.
        /// </summary>
        public static bool TryResolveGen4ConfigTable(int productId, int firmwareVersion, out ScarlettGen4ConfigTable table) {
            table = null;
            switch (productId) {
                case PidScarlett2i2Gen4: {
                    // Verified mixer_scarlett2.c 2i2 Gen 4 table applies to firmware >= 2115
                    // (DirectMonitorGain relocated from 0x2a0 to 0x2a4 in fw >= 2417).
                    if (firmwareVersion < 2115) {
                        return false;
                    }
                    uint dmGainOffset = firmwareVersion >= 2417 ? 0x2a4u : 0x2a0u;
                    table = new ScarlettGen4ConfigTable(
                        productId: productId,
                        firmwareVersion: firmwareVersion,
                        paramBufAddr: 0xfcu,
                        msd: new ScarlettConfigItem(0x49u, 8, 4, false),
                        directMonitor: new ScarlettConfigItem(0x14au, 8, 16, true),
                        directMonitorGain: new ScarlettConfigItem(dmGainOffset, 16, 36, false),
                        autoGainSwitch: new ScarlettConfigItem(0x135u, 8, 10, true),
                        autoGainStatus: new ScarlettConfigItem(0x137u, 8, 0, false),
                        phantomPower: new ScarlettConfigItem(0x48u, 8, 11, true),
                        inputGain: new ScarlettConfigItem(0x4bu, 8, 12, true),
                        levelSwitch: new ScarlettConfigItem(0x3cu, 8, 13, true),
                        safeSwitch: new ScarlettConfigItem(0x147u, 8, 14, true),
                        airSwitch: new ScarlettConfigItem(0x3eu, 8, 15, true),
                        inputSelectSwitch: new ScarlettConfigItem(0x14bu, 8, 17, true),
                        inputLinkSwitch: new ScarlettConfigItem(0x14eu, 8, 18, true),
                        fallbackNote: "2i2 Gen4 verified for fw>=2115 (DirectMonitorGain 0x2a0 pre-2417, 0x2a4 >=2417); fallback to CmdInfoDevmap (0x0080000C) on older firmware.");
                    return true;
                }
                case PidScarlettSoloGen4: {
                    if (firmwareVersion < 0) {
                        return false;
                    }
                    table = new ScarlettGen4ConfigTable(
                        productId: productId,
                        firmwareVersion: firmwareVersion,
                        paramBufAddr: 0xd8u,
                        msd: new ScarlettConfigItem(0x47u, 8, 4, false),
                        directMonitor: new ScarlettConfigItem(0x108u, 8, 12, true),
                        directMonitorGain: new ScarlettConfigItem(0x232u, 16, 26, false),
                        autoGainSwitch: default(ScarlettConfigItem),
                        autoGainStatus: default(ScarlettConfigItem),
                        phantomPower: new ScarlettConfigItem(0x46u, 8, 9, true),
                        inputGain: default(ScarlettConfigItem),
                        levelSwitch: new ScarlettConfigItem(0x3du, 8, 10, true),
                        safeSwitch: default(ScarlettConfigItem),
                        airSwitch: new ScarlettConfigItem(0x3eu, 8, 11, true),
                        inputSelectSwitch: default(ScarlettConfigItem),
                        inputLinkSwitch: default(ScarlettConfigItem),
                        fallbackNote: "Solo Gen4 verified mixer_scarlett2.c table; analog gain pot and no AutoGain/Safe/Link.");
                    return true;
                }
                case PidScarlett4i4Gen4: {
                    if (firmwareVersion < 0) {
                        return false;
                    }
                    table = new ScarlettGen4ConfigTable(
                        productId: productId,
                        firmwareVersion: firmwareVersion,
                        paramBufAddr: 0x130u,
                        msd: new ScarlettConfigItem(0x5cu, 8, 4, false),
                        directMonitor: default(ScarlettConfigItem),
                        directMonitorGain: default(ScarlettConfigItem),
                        autoGainSwitch: new ScarlettConfigItem(0x13eu, 8, 10, true),
                        autoGainStatus: new ScarlettConfigItem(0x140u, 8, 0, false),
                        phantomPower: new ScarlettConfigItem(0x5au, 8, 11, true),
                        inputGain: new ScarlettConfigItem(0x5eu, 8, 12, true),
                        levelSwitch: new ScarlettConfigItem(0x4eu, 8, 13, true),
                        safeSwitch: new ScarlettConfigItem(0x150u, 8, 14, true),
                        airSwitch: new ScarlettConfigItem(0x50u, 8, 15, true),
                        inputSelectSwitch: new ScarlettConfigItem(0x153u, 8, 16, true),
                        inputLinkSwitch: new ScarlettConfigItem(0x156u, 8, 17, true),
                        fallbackNote: "4i4 Gen4 verified mixer_scarlett2.c table; matrix mixer routing replaces DirectMonitor switch.");
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolves a candidate Gen 4 FCP configuration table for live hardware readback verification when
        /// <see cref="CmdInit2"/> reported firmware 0 (unknown) on a known Gen 4 PID. Callers MUST still verify
        /// multiple independent control registers via <c>CmdGetData</c> before setting <c>IsGen4FcpControlTableVerified</c>.
        /// </summary>
        public static bool TryGetGen4ProbeCandidateTable(int productId, int firmwareVersion, out ScarlettGen4ConfigTable table) {
            if (TryResolveGen4ConfigTable(productId, firmwareVersion, out table)) {
                return true;
            }
            if (productId == PidScarlett2i2Gen4 && firmwareVersion == 0) {
                table = new ScarlettGen4ConfigTable(
                    productId: productId,
                    firmwareVersion: 0,
                    paramBufAddr: 0xfcu,
                    msd: new ScarlettConfigItem(0x49u, 8, 4, false),
                    directMonitor: new ScarlettConfigItem(0x14au, 8, 16, true),
                    directMonitorGain: new ScarlettConfigItem(0x2a4u, 16, 36, false),
                    autoGainSwitch: new ScarlettConfigItem(0x135u, 8, 10, true),
                    autoGainStatus: new ScarlettConfigItem(0x137u, 8, 0, false),
                    phantomPower: new ScarlettConfigItem(0x48u, 8, 11, true),
                    inputGain: new ScarlettConfigItem(0x4bu, 8, 12, true),
                    levelSwitch: new ScarlettConfigItem(0x3cu, 8, 13, true),
                    safeSwitch: new ScarlettConfigItem(0x147u, 8, 14, true),
                    airSwitch: new ScarlettConfigItem(0x3eu, 8, 15, true),
                    inputSelectSwitch: new ScarlettConfigItem(0x14bu, 8, 17, true),
                    inputLinkSwitch: new ScarlettConfigItem(0x14eu, 8, 18, true),
                    fallbackNote: "2i2 Gen4 probe candidate (FW unknown); requires live multi-register GET_DATA verification.");
                return true;
            }
            table = null;
            return false;
        }

        /// <summary>
        /// Validates and decodes a <c>struct scarlett2_usb_packet</c> response header returned by the Scarlett interface.
        /// </summary>
        public static bool TryParseResponseHeader(
            byte[] response,
            int length,
            uint expectedCmd,
            ushort expectedSeq,
            out uint errorCode,
            out int payloadSize) {
            errorCode = 0xFFFFFFFFu;
            payloadSize = 0;
            if (response == null || length < PacketHeaderBytes) {
                return false;
            }
            uint cmd = ReadUInt32Le(response, 0);
            int size = response[4] | (response[5] << 8);
            ushort seq = (ushort)(response[6] | (response[7] << 8));
            errorCode = ReadUInt32Le(response, 8);

            if (cmd != expectedCmd || seq != expectedSeq || errorCode != 0u) {
                return false;
            }
            if (PacketHeaderBytes + size > length) {
                return false;
            }
            payloadSize = size;
            return true;
        }

        /// <summary>
        /// Converts a Focusrite 16-bit unsigned linear hardware peak meter value (<c>0..65535</c>) into dBFS (<c>-120..0 dBFS</c>).
        /// </summary>
        public static float MeterRawToDbfs(ushort rawPeak) {
            if (rawPeak == 0) {
                return -120f;
            }
            double linear = rawPeak / 65535.0;
            double db = 20.0 * Math.Log10(linear);
            if (db < -120.0) {
                return -120f;
            }
            if (db > 0.0) {
                return 0f;
            }
            return (float)db;
        }

        /// <summary>
        /// Extracts up to <paramref name="maxChannels"/> hardware peak meters (dBFS) from a validated <see cref="CmdGetMeter"/> response.
        /// </summary>
        public static int ParseMeterResponseDbfs(byte[] response, int length, float[] outDbfs, int maxChannels) {
            if (response == null || outDbfs == null || length < PacketHeaderBytes + 2 || maxChannels <= 0) {
                return 0;
            }
            int payloadBytes = length - PacketHeaderBytes;
            int count = payloadBytes / 2;
            if (count > maxChannels) {
                count = maxChannels;
            }
            if (count > outDbfs.Length) {
                count = outDbfs.Length;
            }
            for (int i = 0; i < count; i++) {
                int off = PacketHeaderBytes + i * 2;
                ushort raw = (ushort)(response[off] | (response[off + 1] << 8));
                outDbfs[i] = MeterRawToDbfs(raw);
            }
            return count;
        }

        static void WriteUInt16Le(byte[] dst, int offset, ushort val) {
            dst[offset] = (byte)(val & 0xFF);
            dst[offset + 1] = (byte)((val >> 8) & 0xFF);
        }

        static void WriteUInt32Le(byte[] dst, int offset, uint val) {
            dst[offset] = (byte)(val & 0xFF);
            dst[offset + 1] = (byte)((val >> 8) & 0xFF);
            dst[offset + 2] = (byte)((val >> 16) & 0xFF);
            dst[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        static uint ReadUInt32Le(byte[] src, int offset) {
            return (uint)(src[offset] |
                (src[offset + 1] << 8) |
                (src[offset + 2] << 16) |
                (src[offset + 3] << 24));
        }
    }
}
