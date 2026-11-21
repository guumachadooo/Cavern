using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

using Cavern.Spatial;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Selectable Slot 4 head-tracking input source.
/// </summary>
public enum HeadTrackerSourceKind {
    /// <summary>Manual 3D Room head gimbal and sliders (no hardware sensor).</summary>
    Manual = 0,
    /// <summary>Samsung Galaxy Buds (Pro / Buds2 / Buds2 Pro / Buds3 / Buds3 Pro / FE) RFCOMM SPP quaternion stream.</summary>
    GalaxyBudsRfcomm = 1,
    /// <summary>On-device Android IMU (GameRotationVector + Gyroscope with Fusion AHRS fallback).</summary>
    AndroidImu = 2,
    /// <summary>External BLE GATT (Nordic UART / Mr. Headtracker) or local UDP Open Sound Control (OSC) head tracker.</summary>
    BleOsc = 3
}

/// <summary>
/// Live diagnostic telemetry reported by the platform audio output pipeline (Slot 4 Low-Latency / USB DAC).
/// </summary>
public readonly struct AudioOutputDiagnostics {
    /// <summary>Active hardware output sample rate in Hz (e.g., 48000, 96000, 192000).</summary>
    public int SampleRate { get; init; }

    /// <summary>True when outputting unquantized 32-bit IEEE Float PCM (<c>Encoding.PcmFloat</c>).</summary>
    public bool IsFloat32 { get; init; }

    /// <summary>True when <c>AudioTrackPerformanceMode.LowLatency</c> was accepted by the audio HAL.</summary>
    public bool IsLowLatency { get; init; }

    /// <summary>True when routed explicitly to an attached USB DAC / USB-C audio interface.</summary>
    public bool IsUsbDacPreferred { get; init; }

    /// <summary>Human-readable preferred output device label.</summary>
    public string DeviceName { get; init; }

    /// <summary>Audio frames written per low-latency burst (256 in LowLatency, 1024 in Balanced).</summary>
    public int FramesPerBurst { get; init; }

    /// <summary>Estimated total end-to-end audio output latency in milliseconds (used by head-pose predictor).</summary>
    public float EstimatedLatencyMs { get; init; }

    /// <summary>Cumulative AudioTrack underrun counter reported by Android HAL.</summary>
    public int UnderrunCount { get; init; }

    /// <summary>Neutral default diagnostics snapshot before audio playback starts.</summary>
    public static AudioOutputDiagnostics Default => new() {
        SampleRate = 48000,
        IsFloat32 = true,
        IsLowLatency = false,
        IsUsbDacPreferred = false,
        DeviceName = string.Empty,
        FramesPerBurst = 1024,
        EstimatedLatencyMs = 35f,
        UnderrunCount = 0
    };
}

/// <summary>
/// Decoded head-tracking sample parsed from an OSC 1.0 UDP packet or BLE GATT text/binary notification.
/// </summary>
public readonly struct OscHeadPoseMessage {
    /// <summary>Unit orientation quaternion in Cavern room coordinates (+X right, +Y up, +Z forward).</summary>
    public Quaternion Orientation { get; init; }

    /// <summary>Optional 6DoF translational room offset (meters).</summary>
    public Vector3 Position { get; init; }

    /// <summary>True when <see cref="Position"/> was explicitly present in the message.</summary>
    public bool HasPosition { get; init; }

    /// <summary>True when a valid orientation was parsed.</summary>
    public bool IsValid { get; init; }
}

/// <summary>
/// Pure C# zero-dependency Open Sound Control (OSC 1.0 + <c>#bundle</c>) and BLE UART text/binary head-tracker parser
/// supporting Mr. Headtracker, IEM SceneRotator, SPARTA, Supperware, OpenTrack OSC, and custom ESP32/Teensy trackers
/// (<c>/quat</c>, <c>/ypr</c>, <c>/headtracker</c>, <c>/orientation</c>, <c>/pose</c>).
/// </summary>
public static class OscPacketParser {
    /// <summary>
    /// Attempts to parse an OSC 1.0 packet (single message or <c>#bundle</c>) into a normalized orientation and optional 6DoF position.
    /// </summary>
    public static bool TryParseOsc(ReadOnlySpan<byte> packet, out OscHeadPoseMessage result) {
        result = default;
        if (packet.Length < 8) {
            return false;
        }

        // Handle OSC #bundle container ("#bundle\0" + 8-byte timetag + [int32 size, element]*)
        if (packet.Length >= 20 &&
            packet[0] == (byte)'#' && packet[1] == (byte)'b' && packet[2] == (byte)'u' &&
            packet[3] == (byte)'n' && packet[4] == (byte)'d' && packet[5] == (byte)'l' &&
            packet[6] == (byte)'e' && packet[7] == 0) {
            int offset = 16; // skip "#bundle\0" + 8-byte NTP timetag
            bool found = false;
            while (offset + 4 <= packet.Length) {
                int elemLen = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
                offset += 4;
                if (elemLen <= 0 || offset + elemLen > packet.Length) {
                    break;
                }
                if (TryParseSingleOscMessage(packet.Slice(offset, elemLen), out OscHeadPoseMessage sub)) {
                    result = sub;
                    found = true;
                }
                offset += elemLen;
            }
            return found;
        }

        return TryParseSingleOscMessage(packet, out result);
    }

    static bool TryParseSingleOscMessage(ReadOnlySpan<byte> msg, out OscHeadPoseMessage result) {
        result = default;
        if (msg.Length < 8 || msg[0] != (byte)'/') {
            return false;
        }

        int addrEnd = msg.IndexOf((byte)0);
        if (addrEnd <= 1) {
            return false;
        }

        string address = Encoding.ASCII.GetString(msg.Slice(0, addrEnd));
        int pos = Align4(addrEnd + 1);
        if (pos >= msg.Length || msg[pos] != (byte)',') {
            return false;
        }

        int tagEnd = msg.Slice(pos).IndexOf((byte)0);
        if (tagEnd <= 1) {
            return false;
        }

        int tagCount = tagEnd - 1;
        ReadOnlySpan<byte> tags = msg.Slice(pos + 1, tagCount);
        pos = Align4(pos + tagEnd + 1);

        Span<float> values = stackalloc float[7];
        int valCount = 0;

        for (int i = 0; i < tags.Length && valCount < values.Length; i++) {
            byte tag = tags[i];
            if (tag == (byte)'f') {
                if (pos + 4 > msg.Length) {
                    return false;
                }
                int bits = BinaryPrimitives.ReadInt32BigEndian(msg.Slice(pos, 4));
                values[valCount++] = BitConverter.Int32BitsToSingle(bits);
                pos += 4;
            } else if (tag == (byte)'d') {
                if (pos + 8 > msg.Length) {
                    return false;
                }
                long bits = BinaryPrimitives.ReadInt64BigEndian(msg.Slice(pos, 8));
                values[valCount++] = (float)BitConverter.Int64BitsToDouble(bits);
                pos += 8;
            } else if (tag == (byte)'i') {
                if (pos + 4 > msg.Length) {
                    return false;
                }
                values[valCount++] = BinaryPrimitives.ReadInt32BigEndian(msg.Slice(pos, 4));
                pos += 4;
            } else {
                // Unsupported tag type in pose stream
                return false;
            }
        }

        return InterpretValues(address, values.Slice(0, valCount), out result);
    }

    static bool InterpretValues(string address, ReadOnlySpan<float> values, out OscHeadPoseMessage result) {
        result = default;
        if (values.Length < 3) {
            return false;
        }
        for (int i = 0; i < values.Length; i++) {
            if (!float.IsFinite(values[i])) {
                return false;
            }
        }

        string lower = address.ToLowerInvariant();

        // /quat (4 floats: W, X, Y, Z per IEM SceneRotator / SPARTA / Mr. Headtracker OSC spec)
        if (lower.Contains("quat") && values.Length >= 4) {
            Quaternion q = new(values[1], values[2], values[3], values[0]);
            if (q.LengthSquared() < 1e-6f) {
                return false;
            }
            result = new OscHeadPoseMessage {
                Orientation = Quaternion.Normalize(q),
                Position = Vector3.Zero,
                HasPosition = false,
                IsValid = true
            };
            return true;
        }

        // 6DoF /pose or /headtracker with 6 floats (X, Y, Z meters + Yaw, Pitch, Roll degrees)
        if ((lower.Contains("pose") || lower.Contains("6dof")) && values.Length >= 6) {
            Vector3 p = new(values[0], values[1], values[2]);
            // Cavern Euler convention: X = Pitch, Y = Yaw, Z = Roll
            Quaternion q = ListenerPose6DoF.FromEulerDegrees(new Vector3(values[4], values[3], values[5]));
            result = new OscHeadPoseMessage {
                Orientation = q,
                Position = p,
                HasPosition = true,
                IsValid = true
            };
            return true;
        }

        // /ypr, /headtracker, /orientation, /euler (3 floats: Yaw, Pitch, Roll in degrees)
        if (lower.Contains("ypr") || lower.Contains("headtracker") || lower.Contains("orientation") || lower.Contains("euler")) {
            if (values.Length == 4) {
                // 4-float quaternion fallback on generic /headtracker or /orientation
                Quaternion q4 = new(values[1], values[2], values[3], values[0]);
                if (q4.LengthSquared() >= 1e-6f) {
                    result = new OscHeadPoseMessage {
                        Orientation = Quaternion.Normalize(q4),
                        Position = Vector3.Zero,
                        HasPosition = false,
                        IsValid = true
                    };
                    return true;
                }
            }
            Quaternion qYpr = ListenerPose6DoF.FromEulerDegrees(new Vector3(values[1], values[0], values[2]));
            result = new OscHeadPoseMessage {
                Orientation = qYpr,
                Position = Vector3.Zero,
                HasPosition = false,
                IsValid = true
            };
            return true;
        }

        // Generic fallback: 4 floats -> WXYZ quaternion, 3 floats -> YPR degrees
        if (values.Length == 4) {
            Quaternion q4 = new(values[1], values[2], values[3], values[0]);
            if (q4.LengthSquared() >= 1e-6f) {
                result = new OscHeadPoseMessage {
                    Orientation = Quaternion.Normalize(q4),
                    Position = Vector3.Zero,
                    HasPosition = false,
                    IsValid = true
                };
                return true;
            }
        }

        Quaternion q3 = ListenerPose6DoF.FromEulerDegrees(new Vector3(values[1], values[0], values[2]));
        result = new OscHeadPoseMessage {
            Orientation = q3,
            Position = Vector3.Zero,
            HasPosition = false,
            IsValid = true
        };
        return true;
    }

    /// <summary>
    /// Attempts to parse a BLE GATT notification payload (either OSC binary, compact 8-byte int16 quaternion
    /// scaled by 1/10000 or 1/16384, 6-byte int16 centidegree YPR, or ASCII CSV <c>"yaw,pitch,roll"</c> / <c>"w,x,y,z"</c>).
    /// </summary>
    public static bool TryParseBlePayload(ReadOnlySpan<byte> payload, out OscHeadPoseMessage result) {
        result = default;
        if (payload.Length == 0) {
            return false;
        }

        // 1. OSC binary over BLE
        if ((payload[0] == (byte)'/' || payload[0] == (byte)'#') && TryParseOsc(payload, out result)) {
            return true;
        }

        // 2. Compact 8-byte little-endian int16 quaternion [w, x, y, z]
        if (payload.Length == 8) {
            short w = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(0, 2));
            short x = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(2, 2));
            short y = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(4, 2));
            short z = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(6, 2));
            Quaternion q = new(x, y, z, w);
            if (q.LengthSquared() > 1f) {
                result = new OscHeadPoseMessage {
                    Orientation = Quaternion.Normalize(q),
                    Position = Vector3.Zero,
                    HasPosition = false,
                    IsValid = true
                };
                return true;
            }
        }

        // 3. Compact 6-byte little-endian int16 centidegree YPR [yaw*100, pitch*100, roll*100]
        if (payload.Length == 6) {
            float yaw = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(0, 2)) * 0.01f;
            float pitch = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(2, 2)) * 0.01f;
            float roll = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(4, 2)) * 0.01f;
            result = new OscHeadPoseMessage {
                Orientation = ListenerPose6DoF.FromEulerDegrees(new Vector3(pitch, yaw, roll)),
                Position = Vector3.Zero,
                HasPosition = false,
                IsValid = true
            };
            return true;
        }

        // 4. ASCII CSV over Nordic UART Service ("yaw,pitch,roll" or "w,x,y,z")
        string text = Encoding.ASCII.GetString(payload).Trim();
        if (text.Length >= 5 && text.Length <= 96) {
            string[] parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float roll)) {
                result = new OscHeadPoseMessage {
                    Orientation = ListenerPose6DoF.FromEulerDegrees(new Vector3(pitch, yaw, roll)),
                    Position = Vector3.Zero,
                    HasPosition = false,
                    IsValid = true
                };
                return true;
            }
            if (parts.Length == 4 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float qw) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float qx) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float qy) &&
                float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float qz)) {
                Quaternion q = new(qx, qy, qz, qw);
                if (q.LengthSquared() >= 1e-6f) {
                    result = new OscHeadPoseMessage {
                        Orientation = Quaternion.Normalize(q),
                        Position = Vector3.Zero,
                        HasPosition = false,
                        IsValid = true
                    };
                    return true;
                }
            }
        }

        return false;
    }

    static int Align4(int index) => (index + 3) & ~3;
}

/// <summary>
/// Portable binary frame encoder/decoder for Samsung Galaxy Buds RFCOMM SPP spatial head-tracking packets,
/// curated from <c>timschneeb/GalaxyBudsClient</c> and <c>timschneeb/BudsPro-Headtracking</c>.
/// Supports Buds Pro, Buds2, Buds2 Pro, Buds3, Buds3 Pro, Buds4, and Buds FE.
/// </summary>
public static class GalaxyBudsProtocol {
    /// <summary>Primary Samsung SPP RFCOMM UUID used by Galaxy Buds2 / Buds2 Pro / Buds3 / Buds3 Pro / FE.</summary>
    public const string UuidSppNew = "2e73a4ad-332d-41fc-90e2-16bef06523f2";

    /// <summary>Standard SPP RFCOMM UUID used by Galaxy Buds Pro.</summary>
    public const string UuidSppStandard = "00001101-0000-1000-8000-00805F9B34FB";

    /// <summary>Legacy Samsung SPP RFCOMM fallback UUID.</summary>
    public const string UuidSppLegacy = "00001102-0000-1000-8000-00805f9b34fd";

    /// <summary>Modern Galaxy Buds SPP Start-of-Message framing marker.</summary>
    public const byte Som = 0xFD;

    /// <summary>Modern Galaxy Buds SPP End-of-Message framing marker.</summary>
    public const byte Eom = 0xDD;

    /// <summary>Message ID: Enable/disable spatial audio sensor pipeline (payload: 1 = on, 0 = off).</summary>
    public const byte MsgSetSpatialAudio = 124;

    /// <summary>Message ID: Incoming spatial sensor telemetry (GRV quaternion, wear state, gyro calibration).</summary>
    public const byte MsgSpatialAudioData = 194;

    /// <summary>Message ID: Spatial sensor lifecycle control (Attach, Detach, KeepAlive).</summary>
    public const byte MsgSpatialAudioControl = 195;

    /// <summary>Control code: Attach spatial sensor stream.</summary>
    public const byte CtrlAttach = 0;

    /// <summary>Control code: Detach spatial sensor stream.</summary>
    public const byte CtrlDetach = 1;

    /// <summary>Control code: Earbuds confirmed spatial attach.</summary>
    public const byte CtrlAttachSuccess = 2;

    /// <summary>Control code: Earbuds confirmed spatial detach.</summary>
    public const byte CtrlDetachSuccess = 3;

    /// <summary>Control code: Periodic 2000 ms keep-alive heartbeat.</summary>
    public const byte CtrlKeepAlive = 4;

    /// <summary>Spatial data event ID: Game Rotation Vector (GRV) 4 x int16 quaternion scaled by 1/10000.</summary>
    public const byte DataBudGrv = 32;

    /// <summary>Spatial data event ID: Earbud placed in ear.</summary>
    public const byte DataWearOn = 33;

    /// <summary>Spatial data event ID: Earbud removed from ear.</summary>
    public const byte DataWearOff = 34;

    /// <summary>Spatial data event ID: Gyroscope bias calibration report.</summary>
    public const byte DataBudGyrocal = 35;

    static readonly ushort[] Crc16Table = BuildCrc16CcittTable();

    /// <summary>
    /// Computes CRC16-CCITT (polynomial 0x1021, initial value 0x0000) matching Samsung Galaxy Buds SPP framing.
    /// </summary>
    public static ushort ComputeCrc16Ccitt(ReadOnlySpan<byte> data) {
        int crc = 0;
        for (int i = 0; i < data.Length; i++) {
            crc = Crc16Table[((crc >> 8) ^ data[i]) & 0xFF] ^ ((crc << 8) & 0xFFFF);
        }
        return (ushort)(crc & 0xFFFF);
    }

    /// <summary>
    /// Computes CRC16-CCITT over <paramref name="msgId"/> followed by <paramref name="payload"/>.
    /// </summary>
    public static ushort ComputeCrc16Ccitt(byte msgId, ReadOnlySpan<byte> payload) {
        int crc = Crc16Table[msgId];
        for (int i = 0; i < payload.Length; i++) {
            crc = Crc16Table[((crc >> 8) ^ payload[i]) & 0xFF] ^ ((crc << 8) & 0xFFFF);
        }
        return (ushort)(crc & 0xFFFF);
    }

    /// <summary>
    /// Encodes a Galaxy Buds modern SPP request packet (<c>SOM | Header(2B) | MsgId | Payload | CRC16(2B LE) | EOM</c>).
    /// </summary>
    public static byte[] EncodeRequest(byte msgId, ReadOnlySpan<byte> payload, bool isResponse = false) {
        int size = 1 + payload.Length + 2; // MsgId + Payload + CRC16
        byte[] packet = new byte[1 + 2 + size + 1];
        packet[0] = Som;

        ushort header = (ushort)(size & 0x03FF);
        if (isResponse) {
            header |= 0x1000;
        }
        packet[1] = (byte)(header & 0xFF);
        packet[2] = (byte)((header >> 8) & 0xFF);

        packet[3] = msgId;
        if (payload.Length > 0) {
            payload.CopyTo(packet.AsSpan(4));
        }

        ushort crc = ComputeCrc16Ccitt(msgId, payload);
        int crcPos = 4 + payload.Length;
        // Samsung SPP writes CRC16 little-endian (low byte first, high byte second)
        packet[crcPos] = (byte)(crc & 0xFF);
        packet[crcPos + 1] = (byte)((crc >> 8) & 0xFF);
        packet[crcPos + 2] = Eom;
        return packet;
    }

    /// <summary>
    /// Attempts to extract and CRC-verify one complete Galaxy Buds SPP frame from a byte stream buffer.
    /// Returns the number of consumed bytes (or 0 if more data is required).
    /// </summary>
    public static int TryExtractFrame(
        ReadOnlySpan<byte> buffer,
        out byte msgId,
        out ReadOnlySpan<byte> payload,
        out bool crcValid) {
        msgId = 0;
        payload = ReadOnlySpan<byte>.Empty;
        crcValid = false;

        int somIdx = buffer.IndexOf(Som);
        if (somIdx < 0) {
            return buffer.Length; // Discard garbage bytes before SOM
        }
        if (somIdx > 0) {
            return somIdx; // Advance caller to SOM byte
        }
        if (buffer.Length < 7) {
            return 0; // Need at least SOM + HDR(2) + ID(1) + CRC(2) + EOM(1)
        }

        ushort header = (ushort)(buffer[1] | (buffer[2] << 8));
        int size = header & 0x03FF;
        int totalLen = 3 + size + 1;
        if (size < 3 || size > 512) {
            return 1; // Corrupt header -> skip this SOM and resync
        }
        if (buffer.Length < totalLen) {
            return 0; // Wait for remaining bytes of this packet
        }
        if (buffer[totalLen - 1] != Eom) {
            return 1; // Invalid EOM -> skip false SOM and resync
        }

        msgId = buffer[3];
        int payloadLen = size - 3;
        payload = buffer.Slice(4, payloadLen);

        // Verify CRC16-CCITT (wire stores LE: crcLo = buffer[crcPos], crcHi = buffer[crcPos + 1])
        int crcPos = 4 + payloadLen;
        ushort wireCrc = (ushort)(buffer[crcPos] | (buffer[crcPos + 1] << 8));
        ushort expectedCrc = ComputeCrc16Ccitt(msgId, payload);
        crcValid = wireCrc == expectedCrc;
        return totalLen;
    }

    /// <summary>
    /// Decodes a <see cref="MsgSpatialAudioData"/> (<c>0xC2</c>) payload into a unit quaternion (Cavern room coordinates)
    /// and wear state. Returns true when a valid <see cref="DataBudGrv"/> quaternion was decoded.
    /// </summary>
    public static bool TryDecodeBudGrvQuaternion(
        ReadOnlySpan<byte> payload,
        out Quaternion orientation,
        out byte eventId,
        out bool isWorn) {
        orientation = Quaternion.Identity;
        eventId = 0;
        isWorn = true;

        if (payload.Length == 0) {
            return false;
        }

        eventId = payload[0];
        if (eventId == DataWearOn) {
            isWorn = true;
            return false;
        }
        if (eventId == DataWearOff) {
            isWorn = false;
            return false;
        }
        if (eventId != DataBudGrv || payload.Length < 10) {
            return false;
        }

        // GalaxyBudsClient SpatialAudioDataDecoder: 4 x LE int16 divided by 10000.0f
        float qx = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(1, 2)) / 10000.0f;
        float qy = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(3, 2)) / 10000.0f;
        float qz = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(5, 2)) / 10000.0f;
        float qw = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(7, 2)) / 10000.0f;
        isWorn = payload[9] == 0;

        // Remap Samsung earbud IMU frame (X right, Y forward, Z up) -> Cavern frame (+X right, +Y up, +Z forward)
        Quaternion q = new(qx, qz, -qy, qw);
        if (q.LengthSquared() < 1e-6f) {
            return false;
        }
        orientation = Quaternion.Normalize(q);
        return true;
    }

    /// <summary>
    /// Returns true if a Bluetooth device name matches a Samsung Galaxy Buds model supporting spatial head tracking.
    /// </summary>
    public static bool IsSupportedGalaxyBudsName(string deviceName) {
        if (string.IsNullOrWhiteSpace(deviceName)) {
            return false;
        }
        return deviceName.Contains("Galaxy Buds", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Buds Pro", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Buds2", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Buds3", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Buds4", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("Buds FE", StringComparison.OrdinalIgnoreCase);
    }

    static ushort[] BuildCrc16CcittTable() {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++) {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++) {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) : (crc << 1);
            }
            table[i] = (ushort)(crc & 0xFFFF);
        }
        return table;
    }
}
