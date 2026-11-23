using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Diagnostic result returned by <see cref="OnnxFp16ModelConverter.TryConvertFileToFp16"/> or
/// <see cref="OnnxFp16ModelConverter.ConvertBytesToFp16"/>.
/// </summary>
public readonly struct OnnxFp16ConversionResult {
    /// <summary>True when an IEEE-754 FP16 ONNX graph was produced with at least one converted FP32 initializer.</summary>
    public bool Success { get; init; }

    /// <summary>True when the source model was already FP16 or contained no convertible FP32 initializers.</summary>
    public bool SkippedAlreadyFp16 { get; init; }

    /// <summary>True when the model uses external tensor data files and was left untouched for safety.</summary>
    public bool SkippedExternalData { get; init; }

    /// <summary>Number of FP32 initializers converted to IEEE-754 FP16.</summary>
    public int ConvertedInitializers { get; init; }

    /// <summary>Number of initializers that were already stored in FP16.</summary>
    public int ExistingFp16Initializers { get; init; }

    /// <summary>Number of graph inputs, outputs, and value_info entries rewritten from FLOAT to FLOAT16.</summary>
    public int ConvertedValueInfos { get; init; }

    /// <summary>Number of Cast/Constant/ConstantOfShape node attributes updated from FLOAT to FLOAT16.</summary>
    public int ConvertedNodeAttributes { get; init; }

    /// <summary>Total scalar FP32 elements converted to IEEE-754 FP16.</summary>
    public long ConvertedWeightElements { get; init; }

    /// <summary>Count of non-finite or out-of-range values clamped/sanitized into finite FP16 range ([-65504, +65504]).</summary>
    public long ClampedElements { get; init; }

    /// <summary>Source ONNX byte size.</summary>
    public long InputBytes { get; init; }

    /// <summary>Converted FP16 ONNX byte size (0 when not converted).</summary>
    public long OutputBytes { get; init; }

    /// <summary>Elapsed conversion wall time in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>Human-readable status or failure reason.</summary>
    public string Message { get; init; }
}

/// <summary>
/// Pure-C# zero-dependency ONNX Protobuf wire-format converter that transforms FP32 CNN/U-Net audio checkpoints
/// into IEEE-754 FP16 (<c>TensorProto.FLOAT16 = 10</c>) for WebGPU Vulkan acceleration without INT8 quantization.
/// Preserves graph structure, integer/index tensors, and <c>Resize</c>/<c>Upsample</c> FP32 scale constraints,
/// sanitizes non-finite weights, and clamps strictly into finite IEEE-754 Half bounds (<c>[-65504, +65504]</c>).
/// </summary>
public static class OnnxFp16ModelConverter {
    const int WireVarint = 0;
    const int Wire64Bit = 1;
    const int WireLengthDelimited = 2;
    const int Wire32Bit = 5;

    const int TensorDataTypeFloat = 1;
    const int TensorDataTypeFloat16 = 10;
    const int TensorDataLocationExternal = 1;

    const float Float16FiniteMax = 65504f;

    /// <summary>
    /// Converts a self-contained ONNX FP32 model file at <paramref name="sourcePath"/> into an IEEE-754 FP16 model
    /// written atomically to <paramref name="destinationPath"/>. Never uses INT8 quantization.
    /// </summary>
    public static OnnxFp16ConversionResult TryConvertFileToFp16(string sourcePath, string destinationPath) {
        Stopwatch sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) {
            return new OnnxFp16ConversionResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Source ONNX model file not found."
            };
        }
        if (string.IsNullOrWhiteSpace(destinationPath)) {
            return new OnnxFp16ConversionResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Destination FP16 cache path is empty."
            };
        }
        if (File.Exists(sourcePath + ".data") || File.Exists(sourcePath + "_data")) {
            return new OnnxFp16ConversionResult {
                Success = false,
                SkippedExternalData = true,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Skipped model with companion external tensor data file."
            };
        }

        string tempPath = null;
        try {
            byte[] srcBytes = File.ReadAllBytes(sourcePath);
            OnnxFp16ConversionResult res = ConvertBytesToFp16(srcBytes, out byte[] dstBytes);
            if (!res.Success || dstBytes == null || dstBytes.Length < 64) {
                return res;
            }

            string dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) {
                Directory.CreateDirectory(dir);
            }
            tempPath = $"{destinationPath}.tmp.{Guid.NewGuid():N}";
            File.WriteAllBytes(tempPath, dstBytes);
            File.Move(tempPath, destinationPath, overwrite: true);
            tempPath = null;

            return new OnnxFp16ConversionResult {
                Success = true,
                ConvertedInitializers = res.ConvertedInitializers,
                ExistingFp16Initializers = res.ExistingFp16Initializers,
                ConvertedValueInfos = res.ConvertedValueInfos,
                ConvertedNodeAttributes = res.ConvertedNodeAttributes,
                ConvertedWeightElements = res.ConvertedWeightElements,
                ClampedElements = res.ClampedElements,
                InputBytes = srcBytes.Length,
                OutputBytes = dstBytes.Length,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = res.Message
            };
        } catch (Exception ex) {
            return new OnnxFp16ConversionResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"FP16 conversion failed: {ex.Message}"
            };
        } finally {
            if (!string.IsNullOrEmpty(tempPath)) {
                try {
                    if (File.Exists(tempPath)) {
                        File.Delete(tempPath);
                    }
                } catch {
                    // Best-effort temp cleanup
                }
            }
        }
    }

    /// <summary>
    /// Converts raw ONNX Protobuf <paramref name="modelBytes"/> from FP32 to IEEE-754 FP16 in memory.
    /// </summary>
    public static OnnxFp16ConversionResult ConvertBytesToFp16(byte[] modelBytes, out byte[] fp16Bytes) {
        fp16Bytes = null;
        Stopwatch sw = Stopwatch.StartNew();
        if (modelBytes == null || modelBytes.Length < 16) {
            return new OnnxFp16ConversionResult {
                Success = false,
                InputBytes = modelBytes?.Length ?? 0,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Input ONNX buffer is empty or truncated."
            };
        }

        ConversionCounters stats = new();
        try {
            // Pre-allocate ~60% of input size (FP16 weights halve initializer payloads)
            int initialCap = Math.Max(256, (modelBytes.Length * 3) / 5);
            using MemoryStream output = new(initialCap);

            int pos = 0;
            int end = modelBytes.Length;
            bool sawGraph = false;

            while (pos < end) {
                int fieldStart = pos;
                ulong tag = ReadVarint(modelBytes, ref pos, end);
                int fieldNum = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);

                if (fieldNum == 7 && wireType == WireLengthDelimited) {
                    // ModelProto.graph (field 7)
                    int graphLen = ReadLength(modelBytes, ref pos, end);
                    int graphStart = pos;
                    pos += graphLen;
                    sawGraph = true;

                    byte[] rewrittenGraph = ConvertGraphProto(modelBytes, graphStart, graphLen, ref stats);
                    if (stats.HasExternalData) {
                        return new OnnxFp16ConversionResult {
                            Success = false,
                            SkippedExternalData = true,
                            InputBytes = modelBytes.Length,
                            ElapsedMs = sw.Elapsed.TotalMilliseconds,
                            Message = "Skipped model containing TensorProto external_data references."
                        };
                    }

                    WriteTag(output, 7, WireLengthDelimited);
                    WriteVarint(output, (ulong)rewrittenGraph.Length);
                    output.Write(rewrittenGraph, 0, rewrittenGraph.Length);
                } else {
                    SkipFieldValue(modelBytes, ref pos, end, wireType);
                    output.Write(modelBytes, fieldStart, pos - fieldStart);
                }
            }

            if (!sawGraph) {
                return new OnnxFp16ConversionResult {
                    Success = false,
                    InputBytes = modelBytes.Length,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Message = "ONNX ModelProto contains no GraphProto payload."
                };
            }

            if (stats.ConvertedInitializers == 0) {
                return new OnnxFp16ConversionResult {
                    Success = false,
                    SkippedAlreadyFp16 = true,
                    ExistingFp16Initializers = stats.ExistingFp16Initializers,
                    InputBytes = modelBytes.Length,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Message = stats.ExistingFp16Initializers > 0
                        ? $"Model already contains {stats.ExistingFp16Initializers} FP16 initializers (0 FP32 initializers)."
                        : "Model contains no convertible FP32 initializers."
                };
            }

            fp16Bytes = output.ToArray();
            sw.Stop();
            return new OnnxFp16ConversionResult {
                Success = true,
                ConvertedInitializers = stats.ConvertedInitializers,
                ExistingFp16Initializers = stats.ExistingFp16Initializers,
                ConvertedValueInfos = stats.ConvertedValueInfos,
                ConvertedNodeAttributes = stats.ConvertedNodeAttributes,
                ConvertedWeightElements = stats.ConvertedWeightElements,
                ClampedElements = stats.ClampedElements,
                InputBytes = modelBytes.Length,
                OutputBytes = fp16Bytes.Length,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"Converted {stats.ConvertedInitializers} FP32 initializers ({stats.ConvertedWeightElements:N0} weights), " +
                          $"{stats.ConvertedValueInfos} value_infos, {stats.ConvertedNodeAttributes} node attrs -> IEEE-754 FP16 " +
                          $"({modelBytes.Length / (1024.0 * 1024.0):0.0} MB -> {fp16Bytes.Length / (1024.0 * 1024.0):0.0} MB in {sw.ElapsedMilliseconds}ms)."
            };
        } catch (Exception ex) {
            fp16Bytes = null;
            return new OnnxFp16ConversionResult {
                Success = false,
                InputBytes = modelBytes.Length,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"ONNX protobuf parse/convert error: {ex.Message}"
            };
        }
    }

    struct ConversionCounters {
        public int ConvertedInitializers;
        public int ExistingFp16Initializers;
        public int ConvertedValueInfos;
        public int ConvertedNodeAttributes;
        public long ConvertedWeightElements;
        public long ClampedElements;
        public bool HasExternalData;
    }

    static byte[] ConvertGraphProto(byte[] data, int start, int length, ref ConversionCounters stats) {
        int end = start + length;

        // Pass 1: discover tensor names that MUST remain FP32 per ONNX operator type constraints
        // (e.g., Resize.scales, Resize.roi, Upsample.scales, NonMaxSuppression thresholds).
        HashSet<string> fp32ProtectedNames = CollectFp32ProtectedTensorNames(data, start, end);

        using MemoryStream output = new(Math.Max(256, (length * 3) / 5));
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (wireType == WireLengthDelimited && fieldNum == 5) {
                // GraphProto.initializer (TensorProto)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] converted = ConvertTensorProtoIfFloat(data, subStart, len, fp32ProtectedNames, isInitializer: true, ref stats);
                if (stats.HasExternalData) {
                    return Array.Empty<byte>();
                }
                if (converted != null) {
                    WriteTag(output, 5, WireLengthDelimited);
                    WriteVarint(output, (ulong)converted.Length);
                    output.Write(converted, 0, converted.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else if (wireType == WireLengthDelimited && (fieldNum == 11 || fieldNum == 12 || fieldNum == 13)) {
                // GraphProto.input (11), output (12), value_info (13) -> ValueInfoProto
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] converted = ConvertValueInfoProtoIfFloat(data, subStart, len, fp32ProtectedNames, ref stats);
                if (converted != null) {
                    WriteTag(output, fieldNum, WireLengthDelimited);
                    WriteVarint(output, (ulong)converted.Length);
                    output.Write(converted, 0, converted.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else if (wireType == WireLengthDelimited && fieldNum == 1) {
                // GraphProto.node (NodeProto)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] converted = ConvertNodeProtoIfNeeded(data, subStart, len, fp32ProtectedNames, ref stats);
                if (stats.HasExternalData) {
                    return Array.Empty<byte>();
                }
                if (converted != null) {
                    WriteTag(output, 1, WireLengthDelimited);
                    WriteVarint(output, (ulong)converted.Length);
                    output.Write(converted, 0, converted.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }

        return output.ToArray();
    }

    static HashSet<string> CollectFp32ProtectedTensorNames(byte[] data, int start, int end) {
        HashSet<string> protectedNames = new(StringComparer.Ordinal);
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref pos, end);
                InspectNodeForProtectedFp32Inputs(data, pos, pos + len, protectedNames);
                pos += len;
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }
        return protectedNames;
    }

    static void InspectNodeForProtectedFp32Inputs(byte[] data, int start, int end, HashSet<string> protectedNames) {
        string opType = string.Empty;
        List<string> inputs = null;
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref pos, end);
                inputs ??= new List<string>(4);
                inputs.Add(Encoding.UTF8.GetString(data, pos, len));
                pos += len;
            } else if (fieldNum == 4 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref pos, end);
                opType = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }

        if (inputs == null || inputs.Count == 0 || string.IsNullOrEmpty(opType)) {
            return;
        }

        if (string.Equals(opType, "Resize", StringComparison.Ordinal)) {
            // ONNX Resize(X, roi, scales, sizes): roi (1) and scales (2) must remain FP32
            if (inputs.Count > 1 && !string.IsNullOrEmpty(inputs[1])) protectedNames.Add(inputs[1]);
            if (inputs.Count > 2 && !string.IsNullOrEmpty(inputs[2])) protectedNames.Add(inputs[2]);
        } else if (string.Equals(opType, "Upsample", StringComparison.Ordinal)) {
            if (inputs.Count > 1 && !string.IsNullOrEmpty(inputs[1])) protectedNames.Add(inputs[1]);
        } else if (string.Equals(opType, "NonMaxSuppression", StringComparison.Ordinal)) {
            if (inputs.Count > 3 && !string.IsNullOrEmpty(inputs[3])) protectedNames.Add(inputs[3]);
            if (inputs.Count > 4 && !string.IsNullOrEmpty(inputs[4])) protectedNames.Add(inputs[4]);
        }
    }

    static byte[] ConvertTensorProtoIfFloat(
        byte[] data,
        int start,
        int length,
        HashSet<string> fp32ProtectedNames,
        bool isInitializer,
        ref ConversionCounters stats) {
        int end = start + length;
        int dataType = 0;
        int dataLocation = 0;
        bool hasExternalEntry = false;
        string tensorName = string.Empty;
        bool hasRawData = false;
        bool hasFloatData = false;

        int scan = start;
        while (scan < end) {
            ulong tag = ReadVarint(data, ref scan, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 2 && wireType == WireVarint) {
                dataType = (int)ReadVarint(data, ref scan, end);
            } else if (fieldNum == 8 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref scan, end);
                tensorName = Encoding.UTF8.GetString(data, scan, len);
                scan += len;
            } else if (fieldNum == 9 && wireType == WireLengthDelimited) {
                hasRawData = true;
                int len = ReadLength(data, ref scan, end);
                scan += len;
            } else if (fieldNum == 4 && (wireType == WireLengthDelimited || wireType == Wire32Bit)) {
                hasFloatData = true;
                SkipFieldValue(data, ref scan, end, wireType);
            } else if (fieldNum == 13 && wireType == WireLengthDelimited) {
                hasExternalEntry = true;
                int len = ReadLength(data, ref scan, end);
                scan += len;
            } else if (fieldNum == 14 && wireType == WireVarint) {
                dataLocation = (int)ReadVarint(data, ref scan, end);
            } else {
                SkipFieldValue(data, ref scan, end, wireType);
            }
        }

        if (dataLocation == TensorDataLocationExternal || hasExternalEntry) {
            stats.HasExternalData = true;
            return null;
        }

        if (dataType == TensorDataTypeFloat16) {
            if (isInitializer) {
                stats.ExistingFp16Initializers++;
            }
            return null;
        }

        if (dataType != TensorDataTypeFloat) {
            return null;
        }

        if (!string.IsNullOrEmpty(tensorName) && fp32ProtectedNames != null && fp32ProtectedNames.Contains(tensorName)) {
            return null;
        }

        // Convert FP32 tensor (either raw_data or float_data, or empty 0-element tensor) into FLOAT16 raw_data
        using MemoryStream output = new(Math.Max(64, (length * 3) / 5));
        List<float> collectedFloatData = hasFloatData && !hasRawData ? new List<float>() : null;

        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 2 && wireType == WireVarint) {
                ReadVarint(data, ref pos, end);
                WriteTag(output, 2, WireVarint);
                WriteVarint(output, TensorDataTypeFloat16);
            } else if (fieldNum == 9 && wireType == WireLengthDelimited) {
                int rawLen = ReadLength(data, ref pos, end);
                if ((rawLen & 3) != 0) {
                    throw new InvalidDataException($"TensorProto '{tensorName}' raw_data length {rawLen} is not a multiple of 4 bytes.");
                }
                int count = rawLen / 4;
                byte[] halfBytes = new byte[count * 2];
                ConvertFloat32LeBytesToFloat16LeBytes(data, pos, count, halfBytes, ref stats.ClampedElements);
                pos += rawLen;

                WriteTag(output, 9, WireLengthDelimited);
                WriteVarint(output, (ulong)halfBytes.Length);
                output.Write(halfBytes, 0, halfBytes.Length);
                stats.ConvertedWeightElements += count;
            } else if (fieldNum == 4 && wireType == WireLengthDelimited) {
                int packedLen = ReadLength(data, ref pos, end);
                if ((packedLen & 3) != 0) {
                    throw new InvalidDataException($"TensorProto '{tensorName}' packed float_data length {packedLen} is not a multiple of 4 bytes.");
                }
                if (collectedFloatData != null) {
                    int count = packedLen / 4;
                    for (int i = 0; i < count; i++) {
                        collectedFloatData.Add(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos + i * 4, 4)));
                    }
                }
                pos += packedLen;
            } else if (fieldNum == 4 && wireType == Wire32Bit) {
                if (pos + 4 > end) {
                    throw new InvalidDataException("Unexpected EOF while reading TensorProto float_data element.");
                }
                if (collectedFloatData != null) {
                    collectedFloatData.Add(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos, 4)));
                }
                pos += 4;
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }

        if (collectedFloatData != null && collectedFloatData.Count > 0) {
            byte[] halfBytes = new byte[collectedFloatData.Count * 2];
            for (int i = 0; i < collectedFloatData.Count; i++) {
                ushort h = Float32ToFiniteHalfBits(collectedFloatData[i], ref stats.ClampedElements);
                BinaryPrimitives.WriteUInt16LittleEndian(halfBytes.AsSpan(i * 2, 2), h);
            }
            WriteTag(output, 9, WireLengthDelimited);
            WriteVarint(output, (ulong)halfBytes.Length);
            output.Write(halfBytes, 0, halfBytes.Length);
            stats.ConvertedWeightElements += collectedFloatData.Count;
        }

        if (isInitializer) {
            stats.ConvertedInitializers++;
        }
        return output.ToArray();
    }

    static byte[] ConvertValueInfoProtoIfFloat(
        byte[] data,
        int start,
        int length,
        HashSet<string> fp32ProtectedNames,
        ref ConversionCounters stats) {
        int end = start + length;
        string name = string.Empty;
        int scan = start;
        while (scan < end) {
            ulong tag = ReadVarint(data, ref scan, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref scan, end);
                name = Encoding.UTF8.GetString(data, scan, len);
                scan += len;
                break;
            }
            SkipFieldValue(data, ref scan, end, wireType);
        }

        if (!string.IsNullOrEmpty(name) && fp32ProtectedNames != null && fp32ProtectedNames.Contains(name)) {
            return null;
        }

        bool modified = false;
        using MemoryStream output = new(length + 8);
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 2 && wireType == WireLengthDelimited) {
                // ValueInfoProto.type (TypeProto)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] convertedType = ConvertTypeProtoIfFloat(data, subStart, len);
                if (convertedType != null) {
                    modified = true;
                    WriteTag(output, 2, WireLengthDelimited);
                    WriteVarint(output, (ulong)convertedType.Length);
                    output.Write(convertedType, 0, convertedType.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }

        if (!modified) {
            return null;
        }
        stats.ConvertedValueInfos++;
        return output.ToArray();
    }

    static byte[] ConvertTypeProtoIfFloat(byte[] data, int start, int length) {
        int end = start + length;
        bool modified = false;
        using MemoryStream output = new(length + 4);
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                // TypeProto.tensor_type (TypeProto.Tensor)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] convertedTensorType = ConvertTensorTypeProtoIfFloat(data, subStart, len);
                if (convertedTensorType != null) {
                    modified = true;
                    WriteTag(output, 1, WireLengthDelimited);
                    WriteVarint(output, (ulong)convertedTensorType.Length);
                    output.Write(convertedTensorType, 0, convertedTensorType.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }
        return modified ? output.ToArray() : null;
    }

    static byte[] ConvertTensorTypeProtoIfFloat(byte[] data, int start, int length) {
        int end = start + length;
        bool modified = false;
        using MemoryStream output = new(length + 4);
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 1 && wireType == WireVarint) {
                int elemType = (int)ReadVarint(data, ref pos, end);
                if (elemType == TensorDataTypeFloat) {
                    modified = true;
                    WriteTag(output, 1, WireVarint);
                    WriteVarint(output, TensorDataTypeFloat16);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }
        return modified ? output.ToArray() : null;
    }

    static byte[] ConvertNodeProtoIfNeeded(
        byte[] data,
        int start,
        int length,
        HashSet<string> fp32ProtectedNames,
        ref ConversionCounters stats) {
        int end = start + length;
        string opType = string.Empty;
        bool outputIsProtected = false;

        int scan = start;
        while (scan < end) {
            ulong tag = ReadVarint(data, ref scan, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 4 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref scan, end);
                opType = Encoding.UTF8.GetString(data, scan, len);
                scan += len;
            } else if (fieldNum == 2 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref scan, end);
                string outName = Encoding.UTF8.GetString(data, scan, len);
                scan += len;
                if (fp32ProtectedNames != null && fp32ProtectedNames.Contains(outName)) {
                    outputIsProtected = true;
                }
            } else {
                SkipFieldValue(data, ref scan, end, wireType);
            }
        }

        if (outputIsProtected) {
            return null;
        }

        bool isCast = string.Equals(opType, "Cast", StringComparison.Ordinal);
        bool isConstant = string.Equals(opType, "Constant", StringComparison.Ordinal) ||
                          string.Equals(opType, "ConstantOfShape", StringComparison.Ordinal);
        if (!isCast && !isConstant) {
            return null;
        }

        bool modified = false;
        using MemoryStream output = new(length + 8);
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 5 && wireType == WireLengthDelimited) {
                // NodeProto.attribute (AttributeProto)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] convertedAttr = ConvertAttributeProtoIfNeeded(data, subStart, len, isCast, isConstant, ref stats);
                if (stats.HasExternalData) {
                    return null;
                }
                if (convertedAttr != null) {
                    modified = true;
                    stats.ConvertedNodeAttributes++;
                    WriteTag(output, 5, WireLengthDelimited);
                    WriteVarint(output, (ulong)convertedAttr.Length);
                    output.Write(convertedAttr, 0, convertedAttr.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }

        return modified ? output.ToArray() : null;
    }

    static byte[] ConvertAttributeProtoIfNeeded(
        byte[] data,
        int start,
        int length,
        bool isCast,
        bool isConstant,
        ref ConversionCounters stats) {
        int end = start + length;
        string attrName = string.Empty;
        int scan = start;
        while (scan < end) {
            ulong tag = ReadVarint(data, ref scan, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = ReadLength(data, ref scan, end);
                attrName = Encoding.UTF8.GetString(data, scan, len);
                scan += len;
                break;
            }
            SkipFieldValue(data, ref scan, end, wireType);
        }

        bool rewriteCastTo = isCast && string.Equals(attrName, "to", StringComparison.Ordinal);
        bool rewriteConstTensor = isConstant && string.Equals(attrName, "value", StringComparison.Ordinal);
        if (!rewriteCastTo && !rewriteConstTensor) {
            return null;
        }

        bool modified = false;
        using MemoryStream output = new(length + 8);
        int pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (rewriteCastTo && fieldNum == 3 && wireType == WireVarint) {
                // AttributeProto.i (int64)
                long val = unchecked((long)ReadVarint(data, ref pos, end));
                if (val == TensorDataTypeFloat) {
                    modified = true;
                    WriteTag(output, 3, WireVarint);
                    WriteVarint(output, TensorDataTypeFloat16);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else if (rewriteConstTensor && fieldNum == 5 && wireType == WireLengthDelimited) {
                // AttributeProto.t (TensorProto)
                int len = ReadLength(data, ref pos, end);
                int subStart = pos;
                pos += len;

                byte[] convertedTensor = ConvertTensorProtoIfFloat(data, subStart, len, fp32ProtectedNames: null, isInitializer: false, ref stats);
                if (stats.HasExternalData) {
                    return null;
                }
                if (convertedTensor != null) {
                    modified = true;
                    WriteTag(output, 5, WireLengthDelimited);
                    WriteVarint(output, (ulong)convertedTensor.Length);
                    output.Write(convertedTensor, 0, convertedTensor.Length);
                } else {
                    output.Write(data, fieldStart, pos - fieldStart);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                output.Write(data, fieldStart, pos - fieldStart);
            }
        }

        return modified ? output.ToArray() : null;
    }

    static void ConvertFloat32LeBytesToFloat16LeBytes(
        byte[] src,
        int srcOffset,
        int count,
        byte[] dst,
        ref long clampedElements) {
        long clamped = 0;
        for (int i = 0; i < count; i++) {
            float v = BinaryPrimitives.ReadSingleLittleEndian(src.AsSpan(srcOffset + i * 4, 4));
            ushort h = Float32ToFiniteHalfBits(v, ref clamped);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(i * 2, 2), h);
        }
        clampedElements += clamped;
    }

    /// <summary>
    /// Converts a single IEEE-754 binary32 float into finite IEEE-754 binary16 bits (<c>0</c> NaN/Inf guaranteed).
    /// </summary>
    public static ushort Float32ToFiniteHalfBits(float value, ref long clampedCounter) {
        if (!float.IsFinite(value)) {
            clampedCounter++;
            return 0;
        }
        if (value > Float16FiniteMax) {
            clampedCounter++;
            value = Float16FiniteMax;
        } else if (value < -Float16FiniteMax) {
            clampedCounter++;
            value = -Float16FiniteMax;
        }
        return BitConverter.HalfToUInt16Bits((Half)value);
    }

    static ulong ReadVarint(byte[] data, ref int pos, int end) {
        ulong result = 0;
        int shift = 0;
        while (pos < end) {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) {
                return result;
            }
            shift += 7;
            if (shift >= 64) {
                throw new InvalidDataException("Malformed protobuf varint (> 10 bytes).");
            }
        }
        throw new InvalidDataException("Unexpected EOF while reading protobuf varint.");
    }

    static int ReadLength(byte[] data, ref int pos, int end) {
        ulong raw = ReadVarint(data, ref pos, end);
        if (raw > (ulong)(end - pos)) {
            throw new InvalidDataException($"Protobuf length-delimited field ({raw} bytes) exceeds remaining payload ({end - pos} bytes).");
        }
        return (int)raw;
    }

    static void SkipFieldValue(byte[] data, ref int pos, int end, int wireType) {
        switch (wireType) {
            case WireVarint:
                ReadVarint(data, ref pos, end);
                break;
            case Wire64Bit:
                if (pos + 8 > end) {
                    throw new InvalidDataException("Unexpected EOF skipping 64-bit protobuf field.");
                }
                pos += 8;
                break;
            case WireLengthDelimited:
                int len = ReadLength(data, ref pos, end);
                pos += len;
                break;
            case Wire32Bit:
                if (pos + 4 > end) {
                    throw new InvalidDataException("Unexpected EOF skipping 32-bit protobuf field.");
                }
                pos += 4;
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    static void WriteTag(Stream s, int fieldNumber, int wireType) =>
        WriteVarint(s, (ulong)((fieldNumber << 3) | (wireType & 0x7)));

    static void WriteVarint(Stream s, ulong value) {
        while (value >= 0x80UL) {
            s.WriteByte((byte)((value & 0x7FUL) | 0x80UL));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
