using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Cavern.Virtualizer;

using Cavernize.Logic.Spatial;

namespace CavernizeAndroid.Services;

/// <summary>
/// Pure C# AES69 SOFA (HDF5 v2/v3) reader for loading laboratory-measured HRIR and BRIR files directly on Android.
/// Supports full spherical-grid extraction for Slot 5B MagLS Ambisonics decoding, optional <c>Data.Delay</c>
/// compensation, automatic sample-rate conversion to 48 kHz, and Woodworth-Schlosberg biometric ITD scaling
/// while preserving exact measured ITD, ILD, and full-band frequency response without inventing synthetic filters.
/// </summary>
public static class SofaHrirReader {
    /// <summary>
    /// Cavern's 16 standard virtual speaker positions (elevation X where -45 = up, clockwise azimuth Y where -90 = left).
    /// </summary>
    static readonly (float x, float y)[] CavernSpeakers = [
        (0f, 0f), (-45f, 0f),
        (0f, -45f), (0f, 45f),
        (-45f, -45f), (-45f, 45f),
        (0f, -90f), (0f, 90f),
        (-45f, -90f), (-45f, 90f),
        (0f, -135f), (0f, 135f),
        (-45f, -135f), (-45f, 135f),
        (0f, 180f), (-45f, 180f)
    ];

    /// <summary>
    /// Front reference speaker indices in <see cref="CavernSpeakers"/>: Center (0,0), Front Left (0,-45), Front Right (0,+45).
    /// Used for single-scalar global unity-energy normalization across all 16 speakers.
    /// </summary>
    static readonly int[] FrontRefIndices = [0, 2, 3];

    /// <summary>
    /// Full spherical grid of measured HRIR/BRIR pairs extracted directly from an AES69 SOFA file.
    /// Used as exact input to <see cref="MagLsAmbisonicDecoder"/> and nearest-neighbor mapping.
    /// </summary>
    public sealed class SofaRawGrid {
        public double[] AzimuthsRad { get; init; }
        public double[] ElevationsRad { get; init; }
        public (double vx, double vy, double vz, double dist)[] UnitVectors { get; init; }
        public float[][] LeftIrs { get; init; }
        public float[][] RightIrs { get; init; }
        public int SampleRate { get; init; }
        public int OriginalSampleRate { get; init; }
        public float ReferenceDistance { get; init; }
        public int TapCount { get; init; }
        public bool IsBrir => TapCount > MinimumPhaseHrir.MaxAnechoicTaps;
    }

    /// <summary>
    /// Result of loading a SOFA HRIR/BRIR file.
    /// </summary>
    public readonly struct SofaLoadResult(
        VirtualChannel[] channels,
        int sampleRate,
        float referenceDistance,
        int tapCount,
        MagLsDesignMetrics? magLsMetrics = null,
        bool biometricItdApplied = false,
        bool brirBypassed = false,
        int measuredDirectionCount = 0) {
        public VirtualChannel[] Channels { get; } = channels;
        public int SampleRate { get; } = sampleRate;
        public float ReferenceDistance { get; } = referenceDistance;
        public int TapCount { get; } = tapCount;
        public MagLsDesignMetrics? MagLsMetrics { get; } = magLsMetrics;
        public bool BiometricItdApplied { get; } = biometricItdApplied;
        public bool BrirBypassed { get; } = brirBypassed;
        public int MeasuredDirectionCount { get; } = measuredDirectionCount;
        public bool IsBrir => TapCount > MinimumPhaseHrir.MaxAnechoicTaps;
    }

    /// <summary>
    /// Loads a .sofa (AES69 / HDF5) HRIR or BRIR file from disk and maps it to Cavern's 16 virtual speaker layout.
    /// </summary>
    public static SofaLoadResult Load(string filePath, bool minimumPhase = false) =>
        Load(filePath, minimumPhase, HrtfPersonalizationConfig.Default);

    /// <summary>
    /// Loads a .sofa (AES69 / HDF5) HRIR or BRIR file from disk with Slot 5B MagLS &amp; Biometric ITD personalization.
    /// </summary>
    public static SofaLoadResult Load(string filePath, bool minimumPhase, HrtfPersonalizationConfig personalization) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            throw new FileNotFoundException("SOFA file not found.", filePath);
        }
        byte[] data = File.ReadAllBytes(filePath);
        return Parse(data, minimumPhase, personalization);
    }

    /// <summary>
    /// Loads the complete measured spherical grid from a .sofa file on disk (optionally resampled to 48 kHz).
    /// </summary>
    public static SofaRawGrid LoadRawGrid(string filePath, int targetSampleRate = VirtualizerFilter.FilterSampleRate) {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
            throw new FileNotFoundException("SOFA file not found.", filePath);
        }
        return ParseRawGrid(File.ReadAllBytes(filePath), targetSampleRate);
    }

    /// <summary>
    /// Parses raw SOFA (HDF5 v2/v3) file bytes into the complete spherical measurement grid.
    /// Applies optional AES69 <c>Data.Delay</c> interaural offsets and resamples non-48 kHz files (e.g. 96 kHz SONICOM
    /// or 44.1 kHz HUTUBS) to <paramref name="targetSampleRate"/> using energy-preserving linear resampling.
    /// </summary>
    public static SofaRawGrid ParseRawGrid(byte[] data, int targetSampleRate = VirtualizerFilter.FilterSampleRate) {
        Hdf5Reader reader = new(data);
        Dictionary<string, ulong> links = reader.ListGroup(reader.RootAddress);

        if (!links.TryGetValue("Data.SamplingRate", out ulong srAddr) ||
            !links.TryGetValue("SourcePosition", out ulong spAddr) ||
            !links.TryGetValue("Data.IR", out ulong irAddr)) {
            throw new InvalidDataException("SOFA file is missing required datasets (Data.SamplingRate, SourcePosition, or Data.IR).");
        }

        (_, double[] srVals) = reader.ReadFloatArray(srAddr);
        if (srVals.Length == 0 || srVals[0] <= 0) {
            throw new InvalidDataException("Invalid Data.SamplingRate in SOFA file.");
        }
        int originalSampleRate = (int)Math.Round(srVals[0]);

        (int[] spDims, double[] spVals) = reader.ReadFloatArray(spAddr);
        (int[] irDims, double[] irVals) = reader.ReadFloatArray(irAddr);

        if (irDims.Length != 3 || irDims[1] < 2 || irDims[2] <= 0) {
            throw new InvalidDataException($"Unsupported Data.IR dimensions ({string.Join("x", irDims)}); expected [M, 2, N].");
        }

        int mCount = irDims[0];
        int rCount = irDims[1];
        int rawTapCount = irDims[2];
        if (spDims.Length < 2 || spDims[0] < mCount || spDims[1] < 3) {
            throw new InvalidDataException($"Unsupported SourcePosition dimensions ({string.Join("x", spDims)}).");
        }

        // Optional AES69 Data.Delay dataset: shape [1, R] or [M, R] in samples
        int[] delayDims = null;
        double[] delayVals = null;
        if (links.TryGetValue("Data.Delay", out ulong delayAddr)) {
            try {
                (delayDims, delayVals) = reader.ReadFloatArray(delayAddr);
            } catch {
                delayDims = null;
                delayVals = null;
            }
        }

        // Detect spherical (az, el, r > 0) vs Cartesian (x, y, z) coordinates
        bool looksCartesian = false;
        double minCol2 = double.MaxValue;
        double maxAbs01 = 0;
        for (int i = 0; i < mCount; i++) {
            double c0 = spVals[i * spDims[1]];
            double c1 = spVals[i * spDims[1] + 1];
            double c2 = spVals[i * spDims[1] + 2];
            if (c2 < minCol2) minCol2 = c2;
            double a0 = Math.Abs(c0), a1 = Math.Abs(c1);
            if (a0 > maxAbs01) maxAbs01 = a0;
            if (a1 > maxAbs01) maxAbs01 = a1;
        }
        if (minCol2 < -1e-4 || maxAbs01 <= 10.0) {
            looksCartesian = true;
        }

        double[] azRads = new double[mCount];
        double[] elRads = new double[mCount];
        (double vx, double vy, double vz, double dist)[] unitVectors = new (double, double, double, double)[mCount];
        double distSum = 0;
        for (int i = 0; i < mCount; i++) {
            double c0 = spVals[i * spDims[1]];
            double c1 = spVals[i * spDims[1] + 1];
            double c2 = spVals[i * spDims[1] + 2];
            if (looksCartesian) {
                double r = Math.Sqrt(c0 * c0 + c1 * c1 + c2 * c2);
                if (r > 1e-9) {
                    double vx = c0 / r, vy = c1 / r, vz = c2 / r;
                    unitVectors[i] = (vx, vy, vz, r);
                    azRads[i] = Math.Atan2(vy, vx);
                    elRads[i] = Math.Asin(Math.Clamp(vz, -1.0, 1.0));
                    distSum += r;
                } else {
                    unitVectors[i] = (1, 0, 0, 1);
                    azRads[i] = 0;
                    elRads[i] = 0;
                    distSum += 1.0;
                }
            } else {
                double azRad = c0 * (Math.PI / 180.0);
                double elRad = c1 * (Math.PI / 180.0);
                double cosEl = Math.Cos(elRad);
                double dist = c2 > 0 ? c2 : 1.0;
                unitVectors[i] = (cosEl * Math.Cos(azRad), cosEl * Math.Sin(azRad), Math.Sin(elRad), dist);
                azRads[i] = azRad;
                elRads[i] = elRad;
                distSum += dist;
            }
        }

        int finalSampleRate = targetSampleRate > 0 ? targetSampleRate : originalSampleRate;
        bool needsResample = finalSampleRate != originalSampleRate;
        float[][] leftIrs = new float[mCount][];
        float[][] rightIrs = new float[mCount][];
        int maxFinalTap = 0;

        for (int i = 0; i < mCount; i++) {
            float[] l = new float[rawTapCount];
            float[] r = new float[rawTapCount];
            long baseIdx = (long)i * rCount * rawTapCount;
            long rightOffset = baseIdx + rawTapCount;
            for (int n = 0; n < rawTapCount; n++) {
                double lv = irVals[baseIdx + n];
                double rv = irVals[rightOffset + n];
                l[n] = double.IsFinite(lv) ? (float)lv : 0f;
                r[n] = double.IsFinite(rv) ? (float)rv : 0f;
            }

            // Apply relative interaural Data.Delay if present in the SOFA file (common in minimum-phase / KEMAR SOFAs)
            if (delayVals != null && delayDims != null && delayDims.Length >= 2 && delayDims[1] >= 2) {
                int delayRow = delayDims[0] > i ? i : 0;
                double dL = delayVals[delayRow * delayDims[1]];
                double dR = delayVals[delayRow * delayDims[1] + 1];
                if (double.IsFinite(dL) && double.IsFinite(dR)) {
                    double diff = dR - dL;
                    if (Math.Abs(diff) > 1e-4 && Math.Abs(diff) < rawTapCount) {
                        int extra = (int)Math.Ceiling(Math.Abs(diff)) + 4;
                        int delayedLen = rawTapCount + extra;
                        if (diff > 0) {
                            l = PadZero(l, delayedLen);
                            r = BiometricItdScaler.ApplyCausalFractionalDelay(r, diff, delayedLen);
                        } else {
                            l = BiometricItdScaler.ApplyCausalFractionalDelay(l, -diff, delayedLen);
                            r = PadZero(r, delayedLen);
                        }
                    }
                }
            }

            if (needsResample) {
                l = ImpulciferHrirParser.ResampleImpulse(l, originalSampleRate, finalSampleRate);
                r = ImpulciferHrirParser.ResampleImpulse(r, originalSampleRate, finalSampleRate);
            }

            leftIrs[i] = l;
            rightIrs[i] = r;
            if (l.Length > maxFinalTap) maxFinalTap = l.Length;
            if (r.Length > maxFinalTap) maxFinalTap = r.Length;
        }

        // Ensure uniform tap length across all M directions for MagLS FFT matrices
        for (int i = 0; i < mCount; i++) {
            if (leftIrs[i].Length < maxFinalTap) leftIrs[i] = PadZero(leftIrs[i], maxFinalTap);
            if (rightIrs[i].Length < maxFinalTap) rightIrs[i] = PadZero(rightIrs[i], maxFinalTap);
        }

        return new SofaRawGrid {
            AzimuthsRad = azRads,
            ElevationsRad = elRads,
            UnitVectors = unitVectors,
            LeftIrs = leftIrs,
            RightIrs = rightIrs,
            SampleRate = finalSampleRate,
            OriginalSampleRate = originalSampleRate,
            ReferenceDistance = (float)Math.Clamp(distSum / Math.Max(1, mCount), 0.25, 10.0),
            TapCount = maxFinalTap
        };
    }

    /// <summary>
    /// Parses raw SOFA (HDF5 v2/v3) file bytes into Cavern <see cref="VirtualChannel"/> instances.
    /// </summary>
    public static SofaLoadResult Parse(byte[] data, bool minimumPhase = false) =>
        Parse(data, minimumPhase, HrtfPersonalizationConfig.Default);

    /// <summary>
    /// Parses raw SOFA (HDF5 v2/v3) file bytes into Cavern <see cref="VirtualChannel"/> instances with
    /// optional Slot 5B MagLS Ambisonics decoding (over the full measured spherical grid) and
    /// Woodworth-Schlosberg biometric ITD scaling. Automatically bypasses both on measured BRIRs (&gt; 1024 taps).
    /// </summary>
    public static SofaLoadResult Parse(byte[] data, bool minimumPhase, HrtfPersonalizationConfig personalization) {
        SofaRawGrid grid = ParseRawGrid(data, VirtualizerFilter.FilterSampleRate);
        int mCount = grid.AzimuthsRad.Length;
        bool isBrir = grid.IsBrir;

        bool wantsMagLs = personalization.MagLsMode != MagLsDecodeMode.Off;
        bool wantsBioItd = personalization.BiometricItdEnabled &&
            !BiometricItdScaler.IsBypass(personalization.HeadWidthCm, personalization.ManualItdOffsetUs);
        bool brirBypassed = isBrir && (wantsMagLs || wantsBioItd || minimumPhase);

        float[][] leftEars;
        float[][] rightEars;
        MagLsDesignMetrics? magLsMetrics = null;
        double refDistSum = 0;

        if (!isBrir && wantsMagLs && mCount >= 4) {
            float[] targetElevs = new float[CavernSpeakers.Length];
            float[] targetAzs = new float[CavernSpeakers.Length];
            for (int s = 0; s < CavernSpeakers.Length; s++) {
                targetElevs[s] = CavernSpeakers[s].x;
                targetAzs[s] = CavernSpeakers[s].y;
            }
            MagLsAmbisonicDecoder.SynthesizeVirtualSpeakerHrirs(
                grid.AzimuthsRad,
                grid.ElevationsRad,
                grid.LeftIrs,
                grid.RightIrs,
                targetElevs,
                targetAzs,
                grid.SampleRate,
                (int)personalization.MagLsMode,
                out leftEars,
                out rightEars,
                out MagLsDesignMetrics metrics,
                personalization.MagLsCutoffHz);
            magLsMetrics = metrics;
            refDistSum = grid.ReferenceDistance * CavernSpeakers.Length;
        } else {
            leftEars = new float[CavernSpeakers.Length][];
            rightEars = new float[CavernSpeakers.Length][];
            for (int s = 0; s < CavernSpeakers.Length; s++) {
                (float cx, float cy) = CavernSpeakers[s];
                // Cavern: x = elevation (-45 is UP +45), y = clockwise azimuth (-90 is LEFT, +90 is RIGHT)
                // SOFA: az = counter-clockwise (+90 is LEFT, 270/-90 is RIGHT), el = +UP
                double targetAzRad = (((-cy) % 360.0 + 360.0) % 360.0) * (Math.PI / 180.0);
                double targetElRad = (-cx) * (Math.PI / 180.0);
                double cosEl = Math.Cos(targetElRad);
                double tx = cosEl * Math.Cos(targetAzRad);
                double ty = cosEl * Math.Sin(targetAzRad);
                double tz = Math.Sin(targetElRad);

                int bestIdx = 0;
                double bestDot = -2.0;
                for (int i = 0; i < mCount; i++) {
                    double dot = tx * grid.UnitVectors[i].vx + ty * grid.UnitVectors[i].vy + tz * grid.UnitVectors[i].vz;
                    if (dot > bestDot) {
                        bestDot = dot;
                        bestIdx = i;
                    }
                }
                refDistSum += grid.UnitVectors[bestIdx].dist;
                leftEars[s] = (float[])grid.LeftIrs[bestIdx].Clone();
                rightEars[s] = (float[])grid.RightIrs[bestIdx].Clone();
            }
        }

        // Single-scalar global unity-energy normalization across front reference speakers
        double frontEnergy = 0;
        for (int i = 0; i < FrontRefIndices.Length; i++) {
            int idx = FrontRefIndices[i];
            double e = 0;
            float[] l = leftEars[idx];
            float[] r = rightEars[idx];
            int len = Math.Min(l.Length, r.Length);
            for (int n = 0; n < len; n++) {
                e += (double)l[n] * l[n] + (double)r[n] * r[n];
            }
            frontEnergy += e;
        }
        frontEnergy /= FrontRefIndices.Length;
        if (frontEnergy > 1e-12) {
            float scale = (float)(1.0 / Math.Sqrt(frontEnergy));
            for (int s = 0; s < CavernSpeakers.Length; s++) {
                float[] l = leftEars[s];
                float[] r = rightEars[s];
                for (int n = 0; n < l.Length; n++) l[n] *= scale;
                for (int n = 0; n < r.Length; n++) r[n] *= scale;
            }
        }

        bool applyBioItd = !isBrir && wantsBioItd;
        bool applyMinPhase = minimumPhase && !isBrir;
        VirtualChannel[] channels = new VirtualChannel[CavernSpeakers.Length];
        int maxOutTap = grid.TapCount;
        for (int s = 0; s < CavernSpeakers.Length; s++) {
            float[] irL = leftEars[s];
            float[] irR = rightEars[s];
            if (applyBioItd) {
                BiometricItdScaler.ApplyToPair(
                    irL,
                    irR,
                    CavernSpeakers[s].x,
                    CavernSpeakers[s].y,
                    grid.SampleRate,
                    personalization.HeadWidthCm,
                    personalization.ManualItdOffsetUs,
                    out irL,
                    out irR);
                if (irL.Length > maxOutTap) maxOutTap = irL.Length;
            }
            channels[s] = VirtualChannel.FromMeasured(
                CavernSpeakers[s].x,
                CavernSpeakers[s].y,
                irL,
                irR,
                grid.SampleRate,
                crossoverFrequency: 0f,
                minimumPhase: applyMinPhase);
        }

        float refDist = (float)Math.Clamp(refDistSum / CavernSpeakers.Length, 0.25, 10.0);
        return new SofaLoadResult(
            channels,
            grid.SampleRate,
            refDist,
            maxOutTap,
            magLsMetrics,
            applyBioItd,
            brirBypassed,
            mCount);
    }

    static float[] PadZero(float[] src, int targetLen) {
        if (src.Length >= targetLen) {
            return src;
        }
        float[] dst = new float[targetLen];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    sealed class Hdf5Reader {
        static readonly byte[] Signature = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];

        readonly byte[] d;
        readonly int szOff;
        readonly int szLen;
        readonly ulong undefAddr;

        public ulong RootAddress { get; }

        public Hdf5Reader(byte[] data) {
            d = data ?? throw new ArgumentNullException(nameof(data));
            if (d.Length < 48) {
                throw new InvalidDataException("File is too small to be a valid SOFA (HDF5) file.");
            }
            for (int i = 0; i < Signature.Length; i++) {
                if (d[i] != Signature[i]) {
                    throw new InvalidDataException("Not a valid SOFA/HDF5 file (missing HDF5 header signature).");
                }
            }

            byte superblockVer = d[8];
            if (superblockVer == 2 || superblockVer == 3) {
                szOff = d[9];
                szLen = d[10];
                if (szOff is not (4 or 8) || szLen is not (4 or 8)) {
                    throw new InvalidDataException($"Unsupported HDF5 offset/length sizes ({szOff}/{szLen}).");
                }
                undefAddr = szOff == 8 ? ulong.MaxValue : uint.MaxValue;
                RootAddress = ReadUOff(12 + 3 * szOff);
            } else {
                throw new InvalidDataException($"Unsupported HDF5 superblock version {superblockVer} (expected v2 or v3 SOFA file).");
            }
        }

        bool IsUndef(ulong addr) => addr == undefAddr || addr >= (ulong)d.Length;

        ulong ReadUInt(int pos, int bytes) {
            ulong v = 0;
            for (int i = 0; i < bytes; i++) {
                v |= (ulong)d[pos + i] << (8 * i);
            }
            return v;
        }

        ulong ReadUOff(int pos) => ReadUInt(pos, szOff);
        ulong ReadULen(int pos) => ReadUInt(pos, szLen);

        static ulong ReadUIntFrom(byte[] buf, int pos, int bytes) {
            ulong v = 0;
            for (int i = 0; i < bytes; i++) {
                v |= (ulong)buf[pos + i] << (8 * i);
            }
            return v;
        }

        bool MatchTag(int pos, string tag) {
            if (pos < 0 || pos + 4 > d.Length) return false;
            return d[pos] == tag[0] && d[pos + 1] == tag[1] && d[pos + 2] == tag[2] && d[pos + 3] == tag[3];
        }

        List<(byte type, byte[] data)> ParseObjectHeader(ulong addr) {
            int pos = (int)addr;
            if (!MatchTag(pos, "OHDR")) {
                throw new InvalidDataException($"Expected HDF5 v2 object header (OHDR) at offset {addr}.");
            }
            byte flags = d[pos + 5];
            pos += 6;
            if ((flags & 0x20) != 0) pos += 16; // timestamps
            if ((flags & 0x10) != 0) pos += 4;  // phase change values
            int szBytes = 1 << (flags & 0x03);
            int chunk0Sz = (int)ReadUInt(pos, szBytes);
            pos += szBytes;
            bool trackOrder = (flags & 0x04) != 0;

            List<(byte, byte[])> msgs = [];
            void WalkChunk(int start, int length) {
                int p = start;
                int end = Math.Min(start + length, d.Length);
                while (p + 4 <= end) {
                    byte mt = d[p];
                    ushort msz = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 1, 2));
                    p += 4 + (trackOrder ? 2 : 0);
                    if (p + msz > end) break;
                    byte[] md = new byte[msz];
                    Buffer.BlockCopy(d, p, md, 0, msz);
                    p += msz;
                    if (mt == 0x10 && md.Length >= szOff + szLen) {
                        ulong cOff = ReadUIntFrom(md, 0, szOff);
                        ulong cLen = ReadUIntFrom(md, szOff, szLen);
                        int ci = (int)cOff;
                        if (MatchTag(ci, "OCHK") && cLen >= 8) {
                            WalkChunk(ci + 4, (int)cLen - 8);
                        }
                    } else if (mt != 0x00) {
                        msgs.Add((mt, md));
                    }
                }
            }

            WalkChunk(pos, chunk0Sz);
            return msgs;
        }

        (string name, ulong? objAddr) ParseLinkMessage(byte[] md) {
            if (md.Length < 4) return (null, null);
            byte fl = md[1];
            int p = 2;
            byte ltype = 0;
            if ((fl & 0x08) != 0) { ltype = md[p]; p += 1; }
            if ((fl & 0x04) != 0) { p += 8; }
            if ((fl & 0x10) != 0) { p += 1; }
            int nszBytes = 1 << (fl & 0x03);
            int nsz = (int)ReadUIntFrom(md, p, nszBytes);
            p += nszBytes;
            if (p + nsz > md.Length) return (null, null);
            string name = Encoding.UTF8.GetString(md, p, nsz);
            p += nsz;
            if (ltype == 0 && p + szOff <= md.Length) {
                return (name, ReadUIntFrom(md, p, szOff));
            }
            return (name, null);
        }

        Func<byte[], byte[]> ReadFractalHeapManaged(ulong fhAddr) {
            int p = (int)fhAddr;
            if (!MatchTag(p, "FRHP")) {
                throw new InvalidDataException("Invalid HDF5 Fractal Heap header (FRHP).");
            }
            p += 5; // sig + ver
            ushort heapIdLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
            ushort ioFilterLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2, 2));
            p += 4;
            p += 1 + 4 + szLen * 2 + szLen + szOff + szLen * 8;
            ushort tableWidth = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
            p += 2;
            ulong startBlockSz = ReadULen(p); p += szLen;
            ulong maxDirectSz = ReadULen(p); p += szLen;
            ushort maxHeapBits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
            p += 4; // maxHeapBits + startNumRows
            ulong rootBlockAddr = ReadUOff(p); p += szOff;
            ushort curRootRows = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));

            int offBytes = (maxHeapBits + 7) / 8;
            int lenBytes = heapIdLen - 1 - offBytes;
            List<(ulong boff, int addr, int sz)> blocks = [];

            void LoadDirectBlock(ulong addr, ulong sz) {
                if (IsUndef(addr)) return;
                int bi = (int)addr;
                if (!MatchTag(bi, "FHDB")) return;
                ulong boff = ReadUInt(bi + 5 + szOff, offBytes);
                blocks.Add((boff, bi, (int)sz));
            }

            void LoadIndirectBlock(ulong addr, int nrows) {
                if (IsUndef(addr)) return;
                int ip = (int)addr;
                if (!MatchTag(ip, "FHIB")) return;
                ip += 5 + szOff + offBytes;
                int maxDblockRows = (int)Math.Log2(Math.Max(1.0, (double)maxDirectSz / Math.Max(1UL, startBlockSz))) + 2;
                for (int r = 0; r < nrows; r++) {
                    ulong rowBsz = r <= 1 ? startBlockSz : (startBlockSz << (r - 1));
                    for (int c = 0; c < tableWidth; c++) {
                        ulong caddr = ReadUOff(ip);
                        ip += szOff;
                        if (ioFilterLen > 0 && r < maxDblockRows) {
                            ip += szLen + 4;
                        }
                        if (IsUndef(caddr)) continue;
                        if (r < maxDblockRows) {
                            LoadDirectBlock(caddr, rowBsz);
                        } else {
                            int subRows = (int)Math.Log2(Math.Max(1.0, (double)rowBsz / Math.Max(1UL, startBlockSz * tableWidth))) + 1;
                            LoadIndirectBlock(caddr, subRows);
                        }
                    }
                }
            }

            if (curRootRows == 0) {
                LoadDirectBlock(rootBlockAddr, startBlockSz);
            } else {
                LoadIndirectBlock(rootBlockAddr, curRootRows);
            }

            return hid => {
                ulong off = ReadUIntFrom(hid, 1, offBytes);
                int ln = (int)ReadUIntFrom(hid, 1 + offBytes, lenBytes);
                for (int i = 0; i < blocks.Count; i++) {
                    var (boff, baddr, bsz) = blocks[i];
                    if (off >= boff && off < boff + (ulong)bsz) {
                        int rel = (int)(off - boff);
                        byte[] res = new byte[ln];
                        Buffer.BlockCopy(d, baddr + rel, res, 0, Math.Min(ln, d.Length - (baddr + rel)));
                        return res;
                    }
                }
                throw new InvalidDataException($"Fractal heap object offset {off} not found.");
            };
        }

        static int BitLength(int val) {
            int bits = 0;
            uint u = (uint)Math.Max(0, val);
            while (u > 0) { bits++; u >>= 1; }
            return bits;
        }

        (List<byte[]> recs, byte btype) ReadBTree2Records(ulong btAddr) {
            int p = (int)btAddr;
            if (!MatchTag(p, "BTHD")) {
                throw new InvalidDataException("Invalid HDF5 v2 B-Tree header (BTHD).");
            }
            byte btype = d[p + 5];
            p += 6;
            int nodeSz = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
            ushort recSz = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4, 2));
            ushort depth = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 6, 2));
            p += 10; // nodeSz(4) + recSz(2) + depth(2) + split(1) + merge(1)
            ulong rootAddr = ReadUOff(p);
            p += szOff;
            ushort numRootRecs = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));

            List<byte[]> recs = [];
            void WalkNode(ulong addr, int nrecs, int curDepth) {
                if (IsUndef(addr) || nrecs <= 0) return;
                int np = (int)addr;
                bool isInternal = MatchTag(np, "BTIN");
                np += 6;
                for (int i = 0; i < nrecs; i++) {
                    byte[] rec = new byte[recSz];
                    Buffer.BlockCopy(d, np, rec, 0, recSz);
                    recs.Add(rec);
                    np += recSz;
                }
                if (isInternal && curDepth > 0) {
                    int[] maxN = new int[curDepth];
                    long[] maxTot = new long[curDepth];
                    maxN[0] = (nodeSz - 10) / recSz;
                    maxTot[0] = maxN[0];
                    for (int lv = 1; lv < curDepth; lv++) {
                        int szNr = (BitLength(maxN[lv - 1]) + 7) / 8;
                        int szTr = lv > 1 ? ((int)Math.Ceiling(Math.Log2(maxTot[lv - 1] + 1)) + 7) / 8 : 0;
                        int ptrSz = szOff + szNr + szTr;
                        int mn = (nodeSz - 10 - ptrSz) / (recSz + ptrSz);
                        maxN[lv] = mn;
                        maxTot[lv] = mn + (long)(mn + 1) * maxTot[lv - 1];
                    }
                    int cSzNr = (BitLength(maxN[curDepth - 1]) + 7) / 8;
                    int cSzTr = curDepth > 1 ? ((int)Math.Ceiling(Math.Log2(maxTot[curDepth - 1] + 1)) + 7) / 8 : 0;
                    for (int i = 0; i <= nrecs; i++) {
                        ulong caddr = ReadUOff(np);
                        np += szOff;
                        int cnrec = (int)ReadUInt(np, cSzNr);
                        np += cSzNr + cSzTr;
                        WalkNode(caddr, cnrec, curDepth - 1);
                    }
                }
            }

            WalkNode(rootAddr, numRootRecs, depth);
            return (recs, btype);
        }

        public Dictionary<string, ulong> ListGroup(ulong addr) {
            List<(byte type, byte[] data)> msgs = ParseObjectHeader(addr);
            Dictionary<string, ulong> links = new(StringComparer.Ordinal);
            foreach (var (mt, md) in msgs) {
                if (mt == 0x06) {
                    var (name, oaddr) = ParseLinkMessage(md);
                    if (!string.IsNullOrEmpty(name) && oaddr.HasValue) {
                        links[name] = oaddr.Value;
                    }
                } else if (mt == 0x02 && md.Length >= 2 + 2 * szOff) {
                    byte fl = md[1];
                    int p = 2 + ((fl & 1) != 0 ? 8 : 0);
                    ulong fhAddr = ReadUIntFrom(md, p, szOff); p += szOff;
                    ulong btAddr = ReadUIntFrom(md, p, szOff);
                    if (!IsUndef(fhAddr) && !IsUndef(btAddr)) {
                        Func<byte[], byte[]> getObj = ReadFractalHeapManaged(fhAddr);
                        var (recs, btype) = ReadBTree2Records(btAddr);
                        int hidStart = btype == 5 ? 4 : 8;
                        foreach (byte[] r in recs) {
                            if (r.Length <= hidStart) continue;
                            byte[] hid = new byte[r.Length - hidStart];
                            Buffer.BlockCopy(r, hidStart, hid, 0, hid.Length);
                            byte[] lmd = getObj(hid);
                            var (name, oaddr) = ParseLinkMessage(lmd);
                            if (!string.IsNullOrEmpty(name) && oaddr.HasValue) {
                                links[name] = oaddr.Value;
                            }
                        }
                    }
                }
            }
            return links;
        }

        static byte[] Unshuffle(byte[] raw, int esz) {
            if (esz <= 1) return raw;
            int n = raw.Length / esz;
            byte[] result = new byte[raw.Length];
            for (int b = 0; b < esz; b++) {
                int srcOff = b * n;
                for (int i = 0; i < n; i++) {
                    result[i * esz + b] = raw[srcOff + i];
                }
            }
            return result;
        }

        static byte[] ZlibDecompress(byte[] compressed) {
            using MemoryStream ms = new(compressed);
            using ZLibStream zs = new(ms, CompressionMode.Decompress);
            using MemoryStream output = new();
            zs.CopyTo(output);
            return output.ToArray();
        }

        static double[] DecodeFloats(byte[] raw, int esz) {
            int count = raw.Length / esz;
            double[] vals = new double[count];
            if (esz == 8) {
                for (int i = 0; i < count; i++) {
                    vals[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8, 8));
                }
            } else if (esz == 4) {
                for (int i = 0; i < count; i++) {
                    vals[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4, 4));
                }
            } else {
                throw new InvalidDataException($"Unsupported SOFA numeric element size ({esz} bytes).");
            }
            return vals;
        }

        public (int[] dims, double[] values) ReadFloatArray(ulong addr) {
            List<(byte type, byte[] data)> msgs = ParseObjectHeader(addr);
            int[] dims = null;
            int esz = 8;
            byte[] layout = null;
            List<(ushort fid, uint[] cds)> filters = [];

            foreach (var (mt, md) in msgs) {
                if (mt == 0x01 && md.Length >= 4) {
                    byte ver = md[0];
                    byte dsRank = md[1];
                    int p = ver == 1 ? 8 : 4;
                    dims = new int[dsRank];
                    for (int i = 0; i < dsRank; i++) {
                        dims[i] = (int)ReadUIntFrom(md, p + i * szLen, szLen);
                    }
                } else if (mt == 0x03 && md.Length >= 8) {
                    esz = (int)BinaryPrimitives.ReadUInt32LittleEndian(md.AsSpan(4, 4));
                } else if (mt == 0x08) {
                    layout = md;
                } else if (mt == 0x0b && md.Length >= 2) {
                    byte ver = md[0];
                    byte nf = md[1];
                    int p = ver == 2 ? 2 : 8;
                    for (int f = 0; f < nf; f++) {
                        ushort fid = BinaryPrimitives.ReadUInt16LittleEndian(md.AsSpan(p, 2)); p += 2;
                        ushort nlen = 0;
                        if (ver == 1 || fid >= 256) {
                            nlen = BinaryPrimitives.ReadUInt16LittleEndian(md.AsSpan(p, 2)); p += 2;
                        }
                        ushort ncd = BinaryPrimitives.ReadUInt16LittleEndian(md.AsSpan(p + 2, 2)); p += 4;
                        if (nlen > 0) {
                            p += ver == 2 ? nlen : ((nlen + 7) & ~7);
                        }
                        uint[] cds = new uint[ncd];
                        for (int c = 0; c < ncd; c++) {
                            cds[c] = BinaryPrimitives.ReadUInt32LittleEndian(md.AsSpan(p + 4 * c, 4));
                        }
                        p += 4 * ncd;
                        if (ver == 1 && (ncd & 1) != 0) p += 4;
                        filters.Add((fid, cds));
                    }
                }
            }

            if (dims == null || layout == null || layout.Length < 2) {
                throw new InvalidDataException("Incomplete HDF5 dataset metadata in SOFA file.");
            }

            int totalElems = 1;
            for (int i = 0; i < dims.Length; i++) totalElems *= dims[i];

            byte lver = layout[0];
            byte lcls = layout[1];
            if (lcls == 1) {
                ulong off = ReadUIntFrom(layout, 2, szOff);
                int byteLen = totalElems * esz;
                byte[] raw = new byte[byteLen];
                Buffer.BlockCopy(d, (int)off, raw, 0, byteLen);
                return (dims, DecodeFloats(raw, esz));
            }

            if (lcls != 2 || lver != 3) {
                throw new InvalidDataException($"Unsupported HDF5 data layout v{lver}/class {lcls} in SOFA file.");
            }

            int ndimsChunk = layout[2]; // rank + 1
            ulong btreeAddr = ReadUIntFrom(layout, 3, szOff);
            int lp = 3 + szOff;
            int rank = dims.Length;
            int[] chunkDims = new int[rank];
            for (int i = 0; i < rank; i++) {
                chunkDims[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(lp + 4 * i, 4));
            }

            List<(int[] coords, int nbytes, uint fmask, ulong caddr)> chunks = [];
            void WalkBTree1(ulong nodeAddr) {
                if (IsUndef(nodeAddr)) return;
                int np = (int)nodeAddr;
                if (!MatchTag(np, "TREE")) {
                    throw new InvalidDataException("Invalid HDF5 v1 B-Tree node (TREE).");
                }
                byte nlevel = d[np + 5];
                ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(np + 6, 2));
                np += 8 + 2 * szOff;
                for (int i = 0; i < entries; i++) {
                    int chunkBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(np, 4));
                    uint filtMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(np + 4, 4));
                    int[] coords = new int[rank];
                    for (int k = 0; k < rank; k++) {
                        coords[k] = (int)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(np + 8 + 8 * k, 8));
                    }
                    np += 8 + 8 * ndimsChunk;
                    ulong childAddr = ReadUOff(np);
                    np += szOff;
                    if (nlevel == 0) {
                        chunks.Add((coords, chunkBytes, filtMask, childAddr));
                    } else {
                        WalkBTree1(childAddr);
                    }
                }
            }

            WalkBTree1(btreeAddr);

            double[] output = new double[totalElems];
            int[] strides = new int[rank];
            int[] cStrides = new int[rank];
            strides[rank - 1] = 1;
            cStrides[rank - 1] = 1;
            for (int i = rank - 2; i >= 0; i--) {
                strides[i] = strides[i + 1] * dims[i + 1];
                cStrides[i] = cStrides[i + 1] * chunkDims[i + 1];
            }

            foreach (var (coords, nbytes, fmask, caddr) in chunks) {
                byte[] raw = new byte[nbytes];
                Buffer.BlockCopy(d, (int)caddr, raw, 0, nbytes);
                for (int fi = filters.Count - 1; fi >= 0; fi--) {
                    if ((fmask & (1u << fi)) != 0) continue;
                    var (fid, cds) = filters[fi];
                    if (fid == 1) {
                        raw = ZlibDecompress(raw);
                    } else if (fid == 2) {
                        raw = Unshuffle(raw, cds.Length > 0 ? (int)cds[0] : esz);
                    } else {
                        throw new InvalidDataException($"Unsupported HDF5 filter id {fid} in SOFA file.");
                    }
                }
                double[] vals = DecodeFloats(raw, esz);
                if (rank == 1) {
                    int c0 = coords[0];
                    int n0 = Math.Min(chunkDims[0], dims[0] - c0);
                    Array.Copy(vals, 0, output, c0, n0);
                } else if (rank == 2) {
                    int c0 = coords[0], c1 = coords[1];
                    int n0 = Math.Min(chunkDims[0], dims[0] - c0);
                    int n1 = Math.Min(chunkDims[1], dims[1] - c1);
                    for (int i0 = 0; i0 < n0; i0++) {
                        Array.Copy(vals, i0 * cStrides[0], output, (c0 + i0) * strides[0] + c1, n1);
                    }
                } else if (rank == 3) {
                    int c0 = coords[0], c1 = coords[1], c2 = coords[2];
                    int n0 = Math.Min(chunkDims[0], dims[0] - c0);
                    int n1 = Math.Min(chunkDims[1], dims[1] - c1);
                    int n2 = Math.Min(chunkDims[2], dims[2] - c2);
                    for (int i0 = 0; i0 < n0; i0++) {
                        for (int i1 = 0; i1 < n1; i1++) {
                            int dst = (c0 + i0) * strides[0] + (c1 + i1) * strides[1] + c2;
                            int src = i0 * cStrides[0] + i1 * cStrides[1];
                            Array.Copy(vals, src, output, dst, n2);
                        }
                    }
                }
            }

            return (dims, output);
        }
    }
}
