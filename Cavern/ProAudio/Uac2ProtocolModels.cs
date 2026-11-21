using System;
using System.Collections.Generic;

namespace Cavern.ProAudio {
    /// <summary>
    /// USB Isochronous synchronization mode per USB 2.0 Table 9-13 (bits 3..2 of <c>bmAttributes</c>).
    /// </summary>
    public enum UsbIsoSyncType {
        /// <summary>No synchronization (0x00).</summary>
        None = 0,
        /// <summary>Asynchronous: device audio clock is master, requiring explicit or implicit feedback (0x04).</summary>
        Asynchronous = 1,
        /// <summary>Adaptive: device adapts its internal PLL to host packet cadence (0x08).</summary>
        Adaptive = 2,
        /// <summary>Synchronous: device locks directly to USB Start-Of-Frame (0x0C).</summary>
        Synchronous = 3
    }

    /// <summary>
    /// USB Audio Class 2.0 (UAC2) PCM subframe packing format derived from <c>FORMAT_TYPE_I</c>
    /// (<c>bSubslotSize</c> and <c>bBitResolution</c>).
    /// </summary>
    public enum Uac2SampleFormat {
        /// <summary>Unsupported or unparsed format.</summary>
        Unknown = 0,
        /// <summary>16-bit signed little-endian PCM (2 bytes/sample).</summary>
        Pcm16Le = 1,
        /// <summary>24-bit signed little-endian packed PCM (3 bytes/sample).</summary>
        Pcm24Le3B = 2,
        /// <summary>24-bit signed PCM left/MSB-aligned or LSB-aligned in 32-bit slot (4 bytes/sample).</summary>
        Pcm24In32Le = 3,
        /// <summary>32-bit signed little-endian PCM (4 bytes/sample).</summary>
        Pcm32Le = 4,
        /// <summary>32-bit IEEE 754 floating-point PCM (4 bytes/sample).</summary>
        Float32Le = 5
    }

    /// <summary>
    /// Parsed UAC2 AudioStreaming alternate setting descriptor (either Playback OUT or Capture IN).
    /// </summary>
    public sealed class Uac2StreamAltSetting {
        /// <summary>USB interface number (<c>bInterfaceNumber</c>).</summary>
        public byte InterfaceNumber { get; set; }

        /// <summary>USB alternate setting index (<c>bAlternateSetting</c> &gt; 0 for active audio streaming).</summary>
        public byte AlternateSetting { get; set; }

        /// <summary>True for Capture (IN, device-to-host); false for Playback (OUT, host-to-device).</summary>
        public bool IsInput { get; set; }

        /// <summary>Associated AudioControl terminal ID (<c>bTerminalLink</c>).</summary>
        public byte TerminalLink { get; set; }

        /// <summary>Number of audio channels (<c>bNrChannels</c>).</summary>
        public byte Channels { get; set; }

        /// <summary>Bytes per audio sample slot on wire (<c>bSubslotSize</c>: 2, 3, or 4).</summary>
        public byte SubslotSizeBytes { get; set; }

        /// <summary>Significant bits per sample (<c>bBitResolution</c>: 16, 24, or 32).</summary>
        public byte BitResolution { get; set; }

        /// <summary>Decoded wire sample format.</summary>
        public Uac2SampleFormat Format { get; set; }

        /// <summary>Isochronous data endpoint address (<c>bEndpointAddress</c>).</summary>
        public byte DataEndpointAddress { get; set; }

        /// <summary>Maximum bytes per isochronous packet (<c>wMaxPacketSize</c> low 11 bits * transactions).</summary>
        public int MaxPacketSize { get; set; }

        /// <summary>Polling interval exponent (<c>bInterval</c>, 1 = every 125 us microframe in HS).</summary>
        public byte Interval { get; set; }

        /// <summary>Isochronous synchronization type (Async / Adaptive / Sync).</summary>
        public UsbIsoSyncType SyncType { get; set; }

        /// <summary>Explicit feedback IN endpoint address (0 when absent or implicit feedback is used).</summary>
        public byte FeedbackEndpointAddress { get; set; }

        /// <summary>True when asynchronous OUT endpoint uses paired IN data stream as implicit feedback clock.</summary>
        public bool UsesImplicitFeedback { get; set; }

        /// <summary>Preference score favoring 32-bit/24-bit over 16-bit and stereo (2ch) for clean pro-audio duplex.</summary>
        public int QualityScore {
            get {
                int score = BitResolution * 10 + SubslotSizeBytes * 4;
                if (Format == Uac2SampleFormat.Float32Le) {
                    score += 40;
                } else if (Format == Uac2SampleFormat.Pcm32Le || Format == Uac2SampleFormat.Pcm24In32Le) {
                    score += 25;
                } else if (Format == Uac2SampleFormat.Pcm24Le3B) {
                    score += 20;
                }
                if (Channels == 2) {
                    score += 15;
                }
                return score;
            }
        }
    }

    /// <summary>
    /// Parsed UAC2 device topology extracted from raw USB configuration descriptors.
    /// </summary>
    public sealed class Uac2DeviceTopology {
        /// <summary>True when a valid USB Audio Class 2.0 (<c>bInterfaceProtocol == 0x20</c>) AudioControl interface was found.</summary>
        public bool IsUac2 { get; set; }

        /// <summary>AudioControl interface number (<c>bInterfaceNumber</c>).</summary>
        public byte AudioControlInterface { get; set; }

        /// <summary>Primary UAC2 Clock Source Entity ID (<c>bClockID</c>) for <c>CS_SAM_FREQ_CONTROL</c>.</summary>
        public byte ClockSourceId { get; set; }

        /// <summary>True when the Clock Source advertises programmable sampling frequency (<c>bmControls &amp; 0x03 == 0x03</c>).</summary>
        public bool ClockProgrammable { get; set; }

        /// <summary>Vendor-specific interface number (<c>bInterfaceClass == 0xFF</c>), or -1 if absent (used by Scarlett 2i2).</summary>
        public int VendorControlInterface { get; set; } = -1;

        /// <summary>Discovered Playback (OUT) active alternate settings.</summary>
        public List<Uac2StreamAltSetting> PlaybackSettings { get; } = new List<Uac2StreamAltSetting>();

        /// <summary>Discovered Capture (IN) active alternate settings.</summary>
        public List<Uac2StreamAltSetting> CaptureSettings { get; } = new List<Uac2StreamAltSetting>();

        /// <summary>True when both at least one OUT and one IN streaming setting exist (full-duplex capable).</summary>
        public bool SupportsFullDuplex {
            get { return PlaybackSettings.Count > 0 && CaptureSettings.Count > 0; }
        }
    }

    /// <summary>
    /// Clean-room USB-IF Audio Class 2.0 (UAC2) specification helpers:
    /// descriptor topology parser, Clock Source <c>CS_SAM_FREQ_CONTROL</c> range parser,
    /// asynchronous 16.16 (HS) / 10.14 (FS) feedback fixed-point pacing accumulator,
    /// and bit-exact PCM16 / PCM24 / PCM32 / Float32 wire packers.
    /// </summary>
    public static class Uac2Protocol {
        /// <summary>USB Audio Class code (0x01).</summary>
        public const byte UsbClassAudio = 0x01;
        /// <summary>USB Vendor-Specific Class code (0xFF).</summary>
        public const byte UsbClassVendor = 0xFF;
        /// <summary>AudioControl interface subclass (0x01).</summary>
        public const byte AudioSubClassControl = 0x01;
        /// <summary>AudioStreaming interface subclass (0x02).</summary>
        public const byte AudioSubClassStreaming = 0x02;
        /// <summary>UAC2 protocol identifier (IP_VERSION_02_00 = 0x20).</summary>
        public const byte ProtocolUac2 = 0x20;

        /// <summary>Standard USB Interface descriptor type (0x04).</summary>
        public const byte DescTypeInterface = 0x04;
        /// <summary>Standard USB Endpoint descriptor type (0x05).</summary>
        public const byte DescTypeEndpoint = 0x05;
        /// <summary>Class-Specific Interface descriptor type (0x24).</summary>
        public const byte DescTypeCsInterface = 0x24;

        /// <summary>UAC2 AC Clock Source descriptor subtype (0x0A).</summary>
        public const byte AcSubtypeClockSource = 0x0A;
        /// <summary>UAC2 AS General descriptor subtype (0x01).</summary>
        public const byte AsSubtypeGeneral = 0x01;
        /// <summary>UAC2 AS Format Type descriptor subtype (0x02).</summary>
        public const byte AsSubtypeFormatType = 0x02;

        /// <summary>UAC2 class request: CUR (0x01).</summary>
        public const byte RequestCur = 0x01;
        /// <summary>UAC2 class request: RANGE (0x02).</summary>
        public const byte RequestRange = 0x02;
        /// <summary>Clock Source Sampling Frequency Control Selector (0x01).</summary>
        public const byte ClockSamFreqControl = 0x01;

        /// <summary>Host-to-device class interface bmRequestType (0x21).</summary>
        public const byte ReqTypeClassInterfaceOut = 0x21;
        /// <summary>Device-to-host class interface bmRequestType (0xA1).</summary>
        public const byte ReqTypeClassInterfaceIn = 0xA1;

        /// <summary>Standard high-resolution audio sample rates supported by UAC2 pro-audio interfaces.</summary>
        public static readonly int[] StandardSampleRates = new int[] {
            44100, 48000, 88200, 96000, 176400, 192000
        };

        /// <summary>
        /// Parses a raw USB Configuration Descriptor byte block into a structured <see cref="Uac2DeviceTopology"/>.
        /// </summary>
        public static Uac2DeviceTopology ParseConfigurationDescriptor(byte[] raw) {
            Uac2DeviceTopology topo = new Uac2DeviceTopology();
            if (raw == null || raw.Length < 9) {
                return topo;
            }

            int pos = 0;
            int curIfClass = -1;
            int curIfSubClass = -1;
            int curIfProtocol = -1;
            byte curIfNum = 0;
            byte curAlt = 0;
            Uac2StreamAltSetting curStream = null;

            while (pos + 2 <= raw.Length) {
                int len = raw[pos];
                byte type = raw[pos + 1];
                if (len < 2 || pos + len > raw.Length) {
                    break;
                }

                if (type == DescTypeInterface && len >= 9) {
                    FlushStreamSetting(topo, curStream);
                    curStream = null;

                    curIfNum = raw[pos + 2];
                    curAlt = raw[pos + 3];
                    curIfClass = raw[pos + 5];
                    curIfSubClass = raw[pos + 6];
                    curIfProtocol = raw[pos + 7];

                    if (curIfClass == UsbClassAudio && curIfSubClass == AudioSubClassControl && curIfProtocol == ProtocolUac2) {
                        topo.IsUac2 = true;
                        topo.AudioControlInterface = curIfNum;
                    } else if (curIfClass == UsbClassVendor && topo.VendorControlInterface < 0) {
                        topo.VendorControlInterface = curIfNum;
                    } else if (curIfClass == UsbClassAudio && curIfSubClass == AudioSubClassStreaming &&
                               curIfProtocol == ProtocolUac2 && curAlt > 0) {
                        curStream = new Uac2StreamAltSetting {
                            InterfaceNumber = curIfNum,
                            AlternateSetting = curAlt
                        };
                    }
                } else if (type == DescTypeCsInterface && len >= 3) {
                    byte subtype = raw[pos + 2];
                    if (curIfClass == UsbClassAudio && curIfSubClass == AudioSubClassControl &&
                        curIfProtocol == ProtocolUac2 && subtype == AcSubtypeClockSource && len >= 8) {
                        byte clockId = raw[pos + 3];
                        byte bmControls = raw[pos + 5];
                        if (topo.ClockSourceId == 0 || (bmControls & 0x03) == 0x03) {
                            topo.ClockSourceId = clockId;
                            topo.ClockProgrammable = (bmControls & 0x03) == 0x03;
                        }
                    } else if (curStream != null && subtype == AsSubtypeGeneral && len >= 16) {
                        curStream.TerminalLink = raw[pos + 3];
                        byte formatType = raw[pos + 5];
                        uint formats = ReadUInt32Le(raw, pos + 6);
                        curStream.Channels = raw[pos + 10];
                        if (formatType == 1 && (formats & 0x04u) != 0u) {
                            curStream.Format = Uac2SampleFormat.Float32Le;
                        }
                    } else if (curStream != null && subtype == AsSubtypeFormatType && len >= 6) {
                        byte formatType = raw[pos + 3];
                        if (formatType == 1) {
                            curStream.SubslotSizeBytes = raw[pos + 4];
                            curStream.BitResolution = raw[pos + 5];
                            if (curStream.Format != Uac2SampleFormat.Float32Le) {
                                curStream.Format = ClassifyPcmFormat(curStream.SubslotSizeBytes, curStream.BitResolution);
                            }
                        }
                    }
                } else if (type == DescTypeEndpoint && len >= 7 && curStream != null) {
                    byte epAddr = raw[pos + 2];
                    byte bmAttr = raw[pos + 3];
                    int rawMaxPkt = raw[pos + 4] | (raw[pos + 5] << 8);
                    int basePkt = rawMaxPkt & 0x07FF;
                    int extraTx = (rawMaxPkt >> 11) & 0x03;
                    int maxPkt = basePkt * (1 + extraTx);
                    byte interval = raw[pos + 6];

                    int xferType = bmAttr & 0x03;
                    if (xferType == 0x01) { // Isochronous
                        int usage = (bmAttr >> 4) & 0x03;
                        bool isIn = (epAddr & 0x80) != 0;
                        if (usage == 0x01 && isIn) {
                            // Explicit feedback IN endpoint (bmAttributes usage = 01b)
                            curStream.FeedbackEndpointAddress = epAddr;
                        } else if (curStream.DataEndpointAddress == 0) {
                            curStream.DataEndpointAddress = epAddr;
                            curStream.IsInput = isIn;
                            curStream.MaxPacketSize = maxPkt;
                            curStream.Interval = interval;
                            curStream.SyncType = (UsbIsoSyncType)((bmAttr >> 2) & 0x03);
                            if (usage == 0x02) {
                                curStream.UsesImplicitFeedback = true;
                            }
                        }
                    }
                }

                pos += len;
            }

            FlushStreamSetting(topo, curStream);

            // Detect implicit feedback on async playback endpoints that lack an explicit feedback endpoint
            // when a matching capture stream exists on the same UAC2 device (e.g., Scarlett 2i2 / Focusrite / RME).
            if (topo.CaptureSettings.Count > 0) {
                for (int i = 0; i < topo.PlaybackSettings.Count; i++) {
                    Uac2StreamAltSetting pb = topo.PlaybackSettings[i];
                    if (pb.SyncType == UsbIsoSyncType.Asynchronous && pb.FeedbackEndpointAddress == 0) {
                        pb.UsesImplicitFeedback = true;
                    }
                }
            }

            return topo;
        }

        /// <summary>
        /// Parses a UAC2 Clock Source <c>GET_RANGE</c> (<c>CS_SAM_FREQ_CONTROL</c>) payload into discrete supported sample rates.
        /// Format: <c>uint16 wNumSubRanges</c> followed by <c>[int32 min, int32 max, int32 res]</c> triplets.
        /// </summary>
        public static int[] ParseClockSampleRateRange(byte[] payload) {
            if (payload == null || payload.Length < 14) {
                return Array.Empty<int>();
            }
            int numRanges = payload[0] | (payload[1] << 8);
            List<int> rates = new List<int>();
            int offset = 2;
            for (int r = 0; r < numRanges && offset + 12 <= payload.Length; r++) {
                int minHz = (int)ReadUInt32Le(payload, offset);
                int maxHz = (int)ReadUInt32Le(payload, offset + 4);
                int resHz = (int)ReadUInt32Le(payload, offset + 8);
                offset += 12;

                if (minHz <= 0 || maxHz < minHz) {
                    continue;
                }
                if (minHz == maxHz || resHz <= 0) {
                    AddUniqueRate(rates, minHz);
                } else {
                    for (int i = 0; i < StandardSampleRates.Length; i++) {
                        int cand = StandardSampleRates[i];
                        if (cand >= minHz && cand <= maxHz && ((cand - minHz) % resHz) == 0) {
                            AddUniqueRate(rates, cand);
                        }
                    }
                }
            }
            rates.Sort();
            return rates.ToArray();
        }

        /// <summary>
        /// Decodes a UAC2 asynchronous feedback endpoint value into frames-per-microframe (High-Speed 16.16)
        /// or frames-per-frame (Full-Speed 10.14). Returns 0.0 if packet is malformed.
        /// </summary>
        public static double DecodeFeedbackFramesPerInterval(byte[] packet, int length, bool isHighSpeed) {
            if (packet == null) {
                return 0.0;
            }
            if (isHighSpeed && length >= 4) {
                uint raw1616 = ReadUInt32Le(packet, 0);
                return raw1616 / 65536.0;
            }
            if (!isHighSpeed && length >= 3) {
                uint raw1014 = (uint)(packet[0] | (packet[1] << 8) | (packet[2] << 16));
                return raw1014 / 16384.0;
            }
            return 0.0;
        }

        /// <summary>
        /// Computes the next integer packet frame count using a 16.16 fixed-point phase accumulator so long-term
        /// average sample delivery matches the asynchronous DAC crystal rate to sub-ppm precision (zero clock drift).
        /// </summary>
        public static int AdvanceAsyncPacketFrames(ref uint phaseAccumulator1616, uint feedbackRate1616) {
            ulong sum = (ulong)phaseAccumulator1616 + feedbackRate1616;
            int frames = (int)(sum >> 16);
            phaseAccumulator1616 = (uint)(sum & 0xFFFFu);
            return frames;
        }

        /// <summary>
        /// Converts a nominal sample rate and USB microframe cadence (8000 Hz HS or 1000 Hz FS) into 16.16 fixed-point rate.
        /// </summary>
        public static uint NominalRateTo1616(int sampleRate, int packetsPerSecond) {
            if (sampleRate <= 0 || packetsPerSecond <= 0) {
                return 0u;
            }
            double fpp = sampleRate / (double)packetsPerSecond;
            return (uint)Math.Round(fpp * 65536.0);
        }

        /// <summary>
        /// Packs interleaved float32 samples in [-1; 1] into UAC2 wire bytes (<paramref name="format"/>).
        /// Zero-GC, bit-exact clamping. Returns total bytes written.
        /// </summary>
        public static int PackFloatToUac2(
            float[] src,
            int srcSampleOffset,
            byte[] dst,
            int dstByteOffset,
            int sampleCount,
            Uac2SampleFormat format) {
            if (src == null || dst == null || sampleCount <= 0) {
                return 0;
            }
            int outPos = dstByteOffset;
            switch (format) {
                case Uac2SampleFormat.Pcm16Le:
                    for (int i = 0; i < sampleCount; i++) {
                        float v = ClampUnit(src[srcSampleOffset + i]);
                        int s = (int)Math.Round(v * 32767.0f);
                        dst[outPos++] = (byte)(s & 0xFF);
                        dst[outPos++] = (byte)((s >> 8) & 0xFF);
                    }
                    return sampleCount * 2;
                case Uac2SampleFormat.Pcm24Le3B:
                    for (int i = 0; i < sampleCount; i++) {
                        float v = ClampUnit(src[srcSampleOffset + i]);
                        int s = (int)Math.Round(v * 8388607.0f);
                        dst[outPos++] = (byte)(s & 0xFF);
                        dst[outPos++] = (byte)((s >> 8) & 0xFF);
                        dst[outPos++] = (byte)((s >> 16) & 0xFF);
                    }
                    return sampleCount * 3;
                case Uac2SampleFormat.Pcm24In32Le:
                case Uac2SampleFormat.Pcm32Le:
                    for (int i = 0; i < sampleCount; i++) {
                        double v = ClampUnit(src[srcSampleOffset + i]);
                        int s = (int)Math.Round(v * 2147483647.0);
                        if (format == Uac2SampleFormat.Pcm24In32Le) {
                            s &= unchecked((int)0xFFFFFF00u);
                        }
                        dst[outPos++] = (byte)(s & 0xFF);
                        dst[outPos++] = (byte)((s >> 8) & 0xFF);
                        dst[outPos++] = (byte)((s >> 16) & 0xFF);
                        dst[outPos++] = (byte)((s >> 24) & 0xFF);
                    }
                    return sampleCount * 4;
                case Uac2SampleFormat.Float32Le:
                    for (int i = 0; i < sampleCount; i++) {
                        int bits = BitConverter.SingleToInt32Bits(ClampUnit(src[srcSampleOffset + i]));
                        dst[outPos++] = (byte)(bits & 0xFF);
                        dst[outPos++] = (byte)((bits >> 8) & 0xFF);
                        dst[outPos++] = (byte)((bits >> 16) & 0xFF);
                        dst[outPos++] = (byte)((bits >> 24) & 0xFF);
                    }
                    return sampleCount * 4;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Unpacks UAC2 wire bytes (<paramref name="format"/>) into normalized float32 samples in [-1; 1].
        /// Zero-GC. Returns total float samples written.
        /// </summary>
        public static int UnpackUac2ToFloat(
            byte[] src,
            int srcByteOffset,
            float[] dst,
            int dstSampleOffset,
            int sampleCount,
            Uac2SampleFormat format) {
            if (src == null || dst == null || sampleCount <= 0) {
                return 0;
            }
            int inPos = srcByteOffset;
            switch (format) {
                case Uac2SampleFormat.Pcm16Le:
                    for (int i = 0; i < sampleCount; i++) {
                        short s = (short)(src[inPos] | (src[inPos + 1] << 8));
                        inPos += 2;
                        dst[dstSampleOffset + i] = s / 32768.0f;
                    }
                    return sampleCount;
                case Uac2SampleFormat.Pcm24Le3B:
                    for (int i = 0; i < sampleCount; i++) {
                        int s = src[inPos] | (src[inPos + 1] << 8) | (src[inPos + 2] << 16);
                        if ((s & 0x800000) != 0) {
                            s |= unchecked((int)0xFF000000u);
                        }
                        inPos += 3;
                        dst[dstSampleOffset + i] = s / 8388608.0f;
                    }
                    return sampleCount;
                case Uac2SampleFormat.Pcm24In32Le:
                case Uac2SampleFormat.Pcm32Le:
                    for (int i = 0; i < sampleCount; i++) {
                        int s = src[inPos] | (src[inPos + 1] << 8) | (src[inPos + 2] << 16) | (src[inPos + 3] << 24);
                        inPos += 4;
                        dst[dstSampleOffset + i] = (float)(s / 2147483648.0);
                    }
                    return sampleCount;
                case Uac2SampleFormat.Float32Le:
                    for (int i = 0; i < sampleCount; i++) {
                        int bits = src[inPos] | (src[inPos + 1] << 8) | (src[inPos + 2] << 16) | (src[inPos + 3] << 24);
                        inPos += 4;
                        dst[dstSampleOffset + i] = BitConverter.Int32BitsToSingle(bits);
                    }
                    return sampleCount;
                default:
                    return 0;
            }
        }

        static void FlushStreamSetting(Uac2DeviceTopology topo, Uac2StreamAltSetting stream) {
            if (stream == null || stream.DataEndpointAddress == 0 || stream.Format == Uac2SampleFormat.Unknown) {
                return;
            }
            if (stream.IsInput) {
                topo.CaptureSettings.Add(stream);
            } else {
                topo.PlaybackSettings.Add(stream);
            }
        }

        static Uac2SampleFormat ClassifyPcmFormat(byte subslotSize, byte bitRes) {
            if (subslotSize == 2 && bitRes <= 16) {
                return Uac2SampleFormat.Pcm16Le;
            }
            if (subslotSize == 3 && bitRes <= 24) {
                return Uac2SampleFormat.Pcm24Le3B;
            }
            if (subslotSize == 4) {
                return bitRes <= 24 ? Uac2SampleFormat.Pcm24In32Le : Uac2SampleFormat.Pcm32Le;
            }
            return Uac2SampleFormat.Unknown;
        }

        static void AddUniqueRate(List<int> list, int hz) {
            if (hz < 8000 || hz > 384000) {
                return;
            }
            for (int i = 0; i < list.Count; i++) {
                if (list[i] == hz) {
                    return;
                }
            }
            list.Add(hz);
        }

        static uint ReadUInt32Le(byte[] data, int offset) {
            return (uint)(data[offset] |
                (data[offset + 1] << 8) |
                (data[offset + 2] << 16) |
                (data[offset + 3] << 24));
        }

        static float ClampUnit(float v) {
            if (v > 1f) {
                return 1f;
            }
            if (v < -1f) {
                return -1f;
            }
            return v;
        }
    }
}
