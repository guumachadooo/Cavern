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

        // Focusrite Gen 4 interfaces use Focusrite Control Protocol (FCP) where control offsets are firmware-defined
        // rather than fixed constants. These nominal reference offsets are gated behind IsGen4FcpControlTableVerified
        // so Cavern never issues blind unverified SET_DATA writes to Gen 4 NVRAM.
        /// <summary>Gen 4 FCP reference offset (gated): MSD ("Easy Start") mode flag.</summary>
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
