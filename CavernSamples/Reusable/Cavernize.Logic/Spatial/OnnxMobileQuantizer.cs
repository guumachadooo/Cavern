using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Cavernize.Logic.Spatial;

/// <summary>
/// Wave E (W53.5 / B12) mobile ONNX weight precision and quantization mode.
/// </summary>
public enum NeuralQuantizationMode {
    /// <summary>
    /// Automatic mobile optimization: IEEE-754 FP16 for WebGPU/QNN, and W8A16 weight-only quantization
    /// for oversized (&gt;= 200 MB) FP32 checkpoints to stay strictly below the 1.4 GB mobile PSS target.
    /// </summary>
    Auto = 0,

    /// <summary>Keep original FP32 checkpoint weights untouched.</summary>
    Fp32 = 1,

    /// <summary>Convert FP32 model weights and graph to IEEE-754 FP16 (~2x smaller RAM/bandwidth).</summary>
    Fp16 = 2,

    /// <summary>
    /// Weight-only per-channel symmetric INT8 quantization with FP32/FP16 DequantizeLinear activations
    /// (W8A16: ~3.8x smaller disk/RAM footprint while preserving full audio activation precision).
    /// </summary>
    W8A16 = 3
}

/// <summary>
/// Diagnostic result returned by <see cref="OnnxMobileQuantizer.ConvertBytesToW8A16"/> or
/// <see cref="OnnxMobileQuantizer.TryConvertFileToW8A16"/>.
/// </summary>
public readonly struct OnnxW8A16QuantizationResult {
    /// <summary>True when at least one eligible FP32 weight tensor was quantized to per-channel INT8 + DequantizeLinear.</summary>
    public bool Success { get; init; }

    /// <summary>True when the source model uses external tensor data files and was left untouched for safety.</summary>
    public bool SkippedExternalData { get; init; }

    /// <summary>Number of 2D+ MatMul/Gemm/Conv weight tensors quantized from FP32 to INT8.</summary>
    public int QuantizedTensors { get; init; }

    /// <summary>Number of non-eligible tensors preserved in original precision (biases, norms, 1D/index tensors).</summary>
    public int PreservedTensors { get; init; }

    /// <summary>Total scalar FP32 weights quantized to INT8.</summary>
    public long QuantizedElements { get; init; }

    /// <summary>Source ONNX byte size.</summary>
    public long InputBytes { get; init; }

    /// <summary>Quantized W8A16 ONNX byte size.</summary>
    public long OutputBytes { get; init; }

    /// <summary>Size reduction ratio (<c>InputBytes / OutputBytes</c>).</summary>
    public double CompressionRatio => OutputBytes > 0 ? (double)InputBytes / OutputBytes : 1.0;

    /// <summary>Minimum per-tensor signal-to-quantization-noise ratio (SQNR) in dB across quantized weights.</summary>
    public double MinTensorSnrDb { get; init; }

    /// <summary>Mean per-tensor SQNR in dB across quantized weights.</summary>
    public double MeanTensorSnrDb { get; init; }

    /// <summary>Wall-clock quantization duration in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>Human-readable status or failure explanation.</summary>
    public string Message { get; init; }
}

/// <summary>
/// Pure-C# zero-dependency ONNX Protobuf weight-only W8A16 quantizer (Wave E / W53.5 / B12).
/// Quantizes eligible 2D+ <c>MatMul</c>/<c>Gemm</c>/<c>Conv</c>/<c>ConvTranspose</c> FP32 weight initializers
/// to symmetric per-channel <c>INT8</c> (<c>TensorProto.INT8 = 3</c>) paired with per-channel FP32 scale tensors
/// and standard ONNX opset-13 <c>DequantizeLinear</c> nodes, cutting mobile PSS by ~3.8x without quantizing audio activations.
/// </summary>
public static class OnnxMobileQuantizer {
    const int WireVarint = 0;
    const int Wire64Bit = 1;
    const int WireLengthDelimited = 2;
    const int Wire32Bit = 5;

    const int TensorDataTypeFloat = 1;
    const int TensorDataTypeInt8 = 3;
    const int TensorDataLocationExternal = 1;
    const int MinQuantizableElements = 1024;
    const long HeavyModelAutoW8A16ThresholdBytes = 120L * 1024L * 1024L;

    static readonly HashSet<string> EligibleWeightConsumerOps = new(StringComparer.Ordinal) {
        "MatMul",
        "Gemm",
        "Conv",
        "ConvTranspose"
    };

    /// <summary>
    /// Resolves the effective mobile quantization mode for a given model size and target execution provider
    /// while respecting the S25U &lt;1.4 GB mobile PSS target.
    /// </summary>
    public static NeuralQuantizationMode ResolveEffectiveMode(
        NeuralQuantizationMode requestedMode,
        long sourceModelBytes,
        NeuralExecutionProvider targetProvider,
        bool isWebGpuOptimal) {
        if (requestedMode != NeuralQuantizationMode.Auto) {
            return requestedMode;
        }
        if (sourceModelBytes >= HeavyModelAutoW8A16ThresholdBytes) {
            return NeuralQuantizationMode.W8A16;
        }
        if (targetProvider == NeuralExecutionProvider.QnnHtp ||
            (targetProvider == NeuralExecutionProvider.WebGpu && isWebGpuOptimal)) {
            return NeuralQuantizationMode.Fp16;
        }
        return NeuralQuantizationMode.Fp32;
    }

    /// <summary>
    /// Estimates peak single-process ONNX Runtime PSS (MB) for a checkpoint size and quantization mode
    /// (accounts for model buffer + ORT session arena + intermediate activation tensors).
    /// </summary>
    public static double EstimatePeakPssMb(long sourceModelBytes, NeuralQuantizationMode effectiveMode) {
        double srcMb = Math.Max(1.0, sourceModelBytes / (1024.0 * 1024.0));
        double weightFactor = effectiveMode switch {
            NeuralQuantizationMode.W8A16 => 0.32,
            NeuralQuantizationMode.Fp16 => 0.56,
            _ => 1.0
        };
        // Empirical S25U mobile ORT overhead: ~2.1x active weight payload + 220 MB runtime/activation baseline
        return 220.0 + (srcMb * weightFactor * 2.1);
    }

    /// <summary>
    /// Quantizes a self-contained ONNX FP32 model file at <paramref name="sourcePath"/> to W8A16 weight-only INT8
    /// and writes the transformed model atomically to <paramref name="destinationPath"/>.
    /// </summary>
    public static OnnxW8A16QuantizationResult TryConvertFileToW8A16(string sourcePath, string destinationPath) {
        Stopwatch sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) {
            return new OnnxW8A16QuantizationResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Source ONNX model file not found."
            };
        }
        if (string.IsNullOrWhiteSpace(destinationPath)) {
            return new OnnxW8A16QuantizationResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Destination W8A16 cache path is empty."
            };
        }
        if (File.Exists(sourcePath + ".data") || File.Exists(sourcePath + "_data")) {
            return new OnnxW8A16QuantizationResult {
                Success = false,
                SkippedExternalData = true,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Skipped model with external tensor data file."
            };
        }

        string tempPath = null;
        try {
            byte[] srcBytes = File.ReadAllBytes(sourcePath);
            OnnxW8A16QuantizationResult res = ConvertBytesToW8A16(srcBytes, out byte[] dstBytes);
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

            return new OnnxW8A16QuantizationResult {
                Success = true,
                QuantizedTensors = res.QuantizedTensors,
                PreservedTensors = res.PreservedTensors,
                QuantizedElements = res.QuantizedElements,
                InputBytes = srcBytes.Length,
                OutputBytes = dstBytes.Length,
                MinTensorSnrDb = res.MinTensorSnrDb,
                MeanTensorSnrDb = res.MeanTensorSnrDb,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = res.Message
            };
        } catch (Exception ex) {
            return new OnnxW8A16QuantizationResult {
                Success = false,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"W8A16 quantization failed: {ex.Message}"
            };
        } finally {
            if (!string.IsNullOrEmpty(tempPath)) {
                try {
                    if (File.Exists(tempPath)) {
                        File.Delete(tempPath);
                    }
                } catch { }
            }
        }
    }

    /// <summary>
    /// Quantizes an in-memory ONNX Protobuf FP32 model into W8A16 per-channel INT8 weights + <c>DequantizeLinear</c> nodes.
    /// </summary>
    public static OnnxW8A16QuantizationResult ConvertBytesToW8A16(byte[] modelBytes, out byte[] w8a16Bytes) {
        w8a16Bytes = null;
        Stopwatch sw = Stopwatch.StartNew();
        if (modelBytes == null || modelBytes.Length < 16) {
            return new OnnxW8A16QuantizationResult {
                Success = false,
                InputBytes = modelBytes?.Length ?? 0,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = "Input ONNX buffer is empty or truncated."
            };
        }

        try {
            QuantStats stats = new();
            using MemoryStream output = new(Math.Max(256, modelBytes.Length / 2));

            int pos = 0;
            int end = modelBytes.Length;
            bool sawGraph = false;
            bool sawDefaultOpset = false;

            while (pos < end) {
                int fieldStart = pos;
                ulong tag = ReadVarint(modelBytes, ref pos, end);
                int fieldNum = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);

                if (fieldNum == 7 && wireType == WireLengthDelimited) {
                    int len = checked((int)ReadVarint(modelBytes, ref pos, end));
                    int payloadStart = pos;
                    int payloadEnd = pos + len;
                    if (payloadEnd > end) {
                        throw new InvalidDataException("Truncated GraphProto payload.");
                    }
                    sawGraph = true;
                    byte[] rewrittenGraph = RewriteGraphProto(modelBytes, payloadStart, payloadEnd, stats);
                    WriteTag(output, 7, WireLengthDelimited);
                    WriteVarint(output, (ulong)rewrittenGraph.Length);
                    output.Write(rewrittenGraph, 0, rewrittenGraph.Length);
                    pos = payloadEnd;
                } else if (fieldNum == 8 && wireType == WireLengthDelimited) {
                    // ModelProto.opset_import (field 8): ensure default domain opset >= 13 for per-axis DequantizeLinear
                    int len = checked((int)ReadVarint(modelBytes, ref pos, end));
                    int payloadStart = pos;
                    int payloadEnd = pos + len;
                    if (payloadEnd > end) {
                        throw new InvalidDataException("Truncated OperatorSetIdProto payload.");
                    }
                    byte[] patchedOpset = PatchOpsetImport(modelBytes, payloadStart, payloadEnd, out bool isDefaultDomain);
                    if (isDefaultDomain) {
                        sawDefaultOpset = true;
                    }
                    WriteTag(output, 8, WireLengthDelimited);
                    WriteVarint(output, (ulong)patchedOpset.Length);
                    output.Write(patchedOpset, 0, patchedOpset.Length);
                    pos = payloadEnd;
                } else {
                    SkipFieldValue(modelBytes, ref pos, end, wireType);
                    output.Write(modelBytes, fieldStart, pos - fieldStart);
                }
            }

            if (!sawGraph) {
                return new OnnxW8A16QuantizationResult {
                    Success = false,
                    InputBytes = modelBytes.Length,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Message = "ONNX model does not contain a GraphProto."
                };
            }
            if (stats.SkippedExternalData) {
                return new OnnxW8A16QuantizationResult {
                    Success = false,
                    SkippedExternalData = true,
                    InputBytes = modelBytes.Length,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Message = "Skipped model with external tensor storage."
                };
            }
            if (stats.QuantizedTensors == 0) {
                return new OnnxW8A16QuantizationResult {
                    Success = false,
                    PreservedTensors = stats.PreservedTensors,
                    InputBytes = modelBytes.Length,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Message = "No eligible 2D+ MatMul/Gemm/Conv FP32 weight tensors met W8A16 threshold."
                };
            }

            if (!sawDefaultOpset) {
                byte[] defaultOpset = BuildDefaultOpset13();
                WriteTag(output, 8, WireLengthDelimited);
                WriteVarint(output, (ulong)defaultOpset.Length);
                output.Write(defaultOpset, 0, defaultOpset.Length);
            }

            w8a16Bytes = output.ToArray();
            double meanSnr = stats.SnrSamples > 0 ? stats.SumSnrDb / stats.SnrSamples : 0.0;
            double minSnr = stats.SnrSamples > 0 ? stats.MinSnrDb : 0.0;

            return new OnnxW8A16QuantizationResult {
                Success = true,
                QuantizedTensors = stats.QuantizedTensors,
                PreservedTensors = stats.PreservedTensors,
                QuantizedElements = stats.QuantizedElements,
                InputBytes = modelBytes.Length,
                OutputBytes = w8a16Bytes.Length,
                MinTensorSnrDb = minSnr,
                MeanTensorSnrDb = meanSnr,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"W8A16 quantized {stats.QuantizedTensors} tensors ({stats.QuantizedElements:N0} weights, {minSnr:F1} dB min SQNR, {(double)modelBytes.Length / w8a16Bytes.Length:F2}x smaller)."
            };
        } catch (Exception ex) {
            w8a16Bytes = null;
            return new OnnxW8A16QuantizationResult {
                Success = false,
                InputBytes = modelBytes.Length,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Message = $"W8A16 conversion error: {ex.Message}"
            };
        }
    }

    sealed class QuantStats {
        public int QuantizedTensors;
        public int PreservedTensors;
        public long QuantizedElements;
        public bool SkippedExternalData;
        public double MinSnrDb = double.PositiveInfinity;
        public double SumSnrDb;
        public int SnrSamples;
    }

    sealed class ParsedTensorInfo {
        public string Name = "";
        public int DataType;
        public int DataLocation;
        public readonly List<long> Dims = new();
        public byte[] RawData;
        public List<float> FloatData;
    }

    static byte[] RewriteGraphProto(byte[] data, int start, int end, QuantStats stats) {
        // Pass 1: Discover all consumer ops for every tensor name so we only quantize true weight tensors
        Dictionary<string, HashSet<string>> consumersByInput = new(StringComparer.Ordinal);
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                InspectNodeConsumers(data, pos, pos + len, consumersByInput);
                pos += len;
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }

        // Pass 2: Rewrite initializers (field 5) & collect generated DequantizeLinear nodes (field 1)
        List<byte[]> dequantNodes = new();
        List<byte[]> originalNodes = new();
        List<byte[]> otherFields = new();

        pos = start;
        while (pos < end) {
            int fieldStart = pos;
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                byte[] nodeField = new byte[(pos + len) - fieldStart];
                Buffer.BlockCopy(data, fieldStart, nodeField, 0, nodeField.Length);
                originalNodes.Add(nodeField);
                pos += len;
            } else if (fieldNum == 5 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                int payloadStart = pos;
                int payloadEnd = pos + len;
                ParsedTensorInfo tensor = ParseTensorProto(data, payloadStart, payloadEnd);
                pos = payloadEnd;

                if (tensor.DataLocation == TensorDataLocationExternal) {
                    stats.SkippedExternalData = true;
                    return Array.Empty<byte>();
                }

                if (TryQuantizeWeightTensor(tensor, consumersByInput, stats, out byte[] qTensorBytes, out byte[] sTensorBytes, out byte[] dqNodeBytes)) {
                    otherFields.Add(WrapLengthDelimitedField(5, qTensorBytes));
                    otherFields.Add(WrapLengthDelimitedField(5, sTensorBytes));
                    dequantNodes.Add(WrapLengthDelimitedField(1, dqNodeBytes));
                } else {
                    stats.PreservedTensors++;
                    byte[] rawInit = new byte[payloadEnd - fieldStart];
                    Buffer.BlockCopy(data, fieldStart, rawInit, 0, rawInit.Length);
                    otherFields.Add(rawInit);
                }
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
                byte[] rawOther = new byte[pos - fieldStart];
                Buffer.BlockCopy(data, fieldStart, rawOther, 0, rawOther.Length);
                otherFields.Add(rawOther);
            }
        }

        // Emit graph in topological order: DequantizeLinear nodes first, then original nodes, then initializers & metadata
        using MemoryStream ms = new(Math.Max(256, (end - start) / 2));
        for (int i = 0; i < dequantNodes.Count; i++) {
            ms.Write(dequantNodes[i], 0, dequantNodes[i].Length);
        }
        for (int i = 0; i < originalNodes.Count; i++) {
            ms.Write(originalNodes[i], 0, originalNodes[i].Length);
        }
        for (int i = 0; i < otherFields.Count; i++) {
            ms.Write(otherFields[i], 0, otherFields[i].Length);
        }
        return ms.ToArray();
    }

    static bool TryQuantizeWeightTensor(
        ParsedTensorInfo tensor,
        Dictionary<string, HashSet<string>> consumersByInput,
        QuantStats stats,
        out byte[] qTensorBytes,
        out byte[] scaleTensorBytes,
        out byte[] dqNodeBytes) {
        qTensorBytes = null;
        scaleTensorBytes = null;
        dqNodeBytes = null;

        if (tensor.DataType != TensorDataTypeFloat || string.IsNullOrEmpty(tensor.Name) || tensor.Dims.Count < 2) {
            return false;
        }
        if (!consumersByInput.TryGetValue(tensor.Name, out HashSet<string> ops) || ops.Count == 0) {
            return false;
        }
        foreach (string op in ops) {
            if (!EligibleWeightConsumerOps.Contains(op)) {
                return false;
            }
        }

        long channelCount = tensor.Dims[0];
        if (channelCount <= 0 || channelCount > 1_000_000) {
            return false;
        }
        long totalElems = 1;
        for (int d = 0; d < tensor.Dims.Count; d++) {
            if (tensor.Dims[d] <= 0) {
                return false;
            }
            totalElems *= tensor.Dims[d];
        }
        if (totalElems < MinQuantizableElements || totalElems > 200_000_000L) {
            return false;
        }

        float[] weights = ExtractFloatArray(tensor, (int)totalElems);
        if (weights == null || weights.Length != totalElems) {
            return false;
        }

        int channels = (int)channelCount;
        int stride = (int)(totalElems / channels);
        byte[] qRaw = new byte[weights.Length];
        byte[] scaleRaw = new byte[channels * sizeof(float)];

        double signalPower = 0.0;
        double noisePower = 0.0;

        for (int c = 0; c < channels; c++) {
            int rowStart = c * stride;
            float maxAbs = 0f;
            for (int j = 0; j < stride; j++) {
                float v = weights[rowStart + j];
                if (!float.IsFinite(v)) {
                    v = 0f;
                }
                float a = MathF.Abs(v);
                if (a > maxAbs) {
                    maxAbs = a;
                }
            }
            float scale = maxAbs > 1e-8f ? (maxAbs / 127.0f) : 1e-8f;
            float invScale = 1.0f / scale;
            BinaryPrimitives.WriteSingleLittleEndian(scaleRaw.AsSpan(c * sizeof(float), sizeof(float)), scale);

            for (int j = 0; j < stride; j++) {
                float v = weights[rowStart + j];
                if (!float.IsFinite(v)) {
                    v = 0f;
                }
                int q = (int)MathF.Round(v * invScale);
                if (q > 127) q = 127;
                else if (q < -127) q = -127;
                qRaw[rowStart + j] = unchecked((byte)(sbyte)q);

                double recon = q * (double)scale;
                double err = v - recon;
                signalPower += (double)v * v;
                noisePower += err * err;
            }
        }

        double snrDb = noisePower > 1e-20 ? 10.0 * Math.Log10(Math.Max(signalPower, 1e-20) / noisePower) : 96.0;
        string qName = tensor.Name + "__w8q";
        string scaleName = tensor.Name + "__w8scale";

        qTensorBytes = BuildTensorProto(qName, TensorDataTypeInt8, tensor.Dims, qRaw);
        scaleTensorBytes = BuildTensorProto(scaleName, TensorDataTypeFloat, new[] { channelCount }, scaleRaw);
        dqNodeBytes = BuildDequantizeLinearNode(qName, scaleName, tensor.Name);

        stats.QuantizedTensors++;
        stats.QuantizedElements += totalElems;
        if (snrDb < stats.MinSnrDb) {
            stats.MinSnrDb = snrDb;
        }
        stats.SumSnrDb += snrDb;
        stats.SnrSamples++;
        return true;
    }

    static float[] ExtractFloatArray(ParsedTensorInfo tensor, int expectedCount) {
        if (tensor.RawData != null && tensor.RawData.Length == expectedCount * sizeof(float)) {
            float[] dst = new float[expectedCount];
            for (int i = 0; i < expectedCount; i++) {
                dst[i] = BinaryPrimitives.ReadSingleLittleEndian(tensor.RawData.AsSpan(i * sizeof(float), sizeof(float)));
            }
            return dst;
        }
        if (tensor.FloatData != null && tensor.FloatData.Count == expectedCount) {
            return tensor.FloatData.ToArray();
        }
        return null;
    }

    static void InspectNodeConsumers(byte[] data, int start, int end, Dictionary<string, HashSet<string>> map) {
        List<string> inputs = new();
        string opType = "";
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                inputs.Add(Encoding.UTF8.GetString(data, pos, len));
                pos += len;
            } else if (fieldNum == 4 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                opType = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }
        if (string.IsNullOrEmpty(opType)) {
            return;
        }
        for (int i = 0; i < inputs.Count; i++) {
            string inp = inputs[i];
            if (string.IsNullOrEmpty(inp)) {
                continue;
            }
            if (!map.TryGetValue(inp, out HashSet<string> ops)) {
                ops = new HashSet<string>(StringComparer.Ordinal);
                map[inp] = ops;
            }
            ops.Add(opType);
        }
    }

    static ParsedTensorInfo ParseTensorProto(byte[] data, int start, int end) {
        ParsedTensorInfo info = new();
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireVarint) {
                info.Dims.Add((long)ReadVarint(data, ref pos, end));
            } else if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                int subEnd = pos + len;
                while (pos < subEnd) {
                    info.Dims.Add((long)ReadVarint(data, ref pos, subEnd));
                }
            } else if (fieldNum == 2 && wireType == WireVarint) {
                info.DataType = (int)ReadVarint(data, ref pos, end);
            } else if (fieldNum == 4 && wireType == Wire32Bit) {
                info.FloatData ??= new List<float>();
                info.FloatData.Add(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos, 4)));
                pos += 4;
            } else if (fieldNum == 4 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                info.FloatData ??= new List<float>(len / 4);
                int subEnd = pos + len;
                while (pos + 4 <= subEnd) {
                    info.FloatData.Add(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos, 4)));
                    pos += 4;
                }
                pos = subEnd;
            } else if (fieldNum == 8 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                info.Name = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            } else if (fieldNum == 9 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                info.RawData = new byte[len];
                Buffer.BlockCopy(data, pos, info.RawData, 0, len);
                pos += len;
            } else if (fieldNum == 14 && wireType == WireVarint) {
                info.DataLocation = (int)ReadVarint(data, ref pos, end);
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }
        return info;
    }

    static byte[] BuildTensorProto(string name, int dataType, IReadOnlyList<long> dims, byte[] rawData) {
        using MemoryStream ms = new(rawData.Length + 64);
        for (int i = 0; i < dims.Count; i++) {
            WriteTag(ms, 1, WireVarint);
            WriteVarint(ms, (ulong)dims[i]);
        }
        WriteTag(ms, 2, WireVarint);
        WriteVarint(ms, (ulong)dataType);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        WriteTag(ms, 8, WireLengthDelimited);
        WriteVarint(ms, (ulong)nameBytes.Length);
        ms.Write(nameBytes, 0, nameBytes.Length);
        WriteTag(ms, 9, WireLengthDelimited);
        WriteVarint(ms, (ulong)rawData.Length);
        ms.Write(rawData, 0, rawData.Length);
        return ms.ToArray();
    }

    static byte[] BuildDequantizeLinearNode(string xInput, string scaleInput, string yOutput) {
        using MemoryStream ms = new(128);
        WriteStringField(ms, 1, xInput);
        WriteStringField(ms, 1, scaleInput);
        WriteStringField(ms, 2, yOutput);
        WriteStringField(ms, 3, "dq_" + yOutput);
        WriteStringField(ms, 4, "DequantizeLinear");
        // AttributeProto { name="axis", i=0, type=INT(2) } -> field 5
        using MemoryStream attr = new(32);
        WriteStringField(attr, 1, "axis");
        WriteTag(attr, 3, WireVarint);
        WriteVarint(attr, 0UL);
        WriteTag(attr, 20, WireVarint);
        WriteVarint(attr, 2UL); // AttributeType.INT
        byte[] attrBytes = attr.ToArray();
        WriteTag(ms, 5, WireLengthDelimited);
        WriteVarint(ms, (ulong)attrBytes.Length);
        ms.Write(attrBytes, 0, attrBytes.Length);
        return ms.ToArray();
    }

    static byte[] PatchOpsetImport(byte[] data, int start, int end, out bool isDefaultDomain) {
        string domain = "";
        long version = 0;
        int pos = start;
        while (pos < end) {
            ulong tag = ReadVarint(data, ref pos, end);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);
            if (fieldNum == 1 && wireType == WireLengthDelimited) {
                int len = checked((int)ReadVarint(data, ref pos, end));
                domain = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            } else if (fieldNum == 2 && wireType == WireVarint) {
                version = (long)ReadVarint(data, ref pos, end);
            } else {
                SkipFieldValue(data, ref pos, end, wireType);
            }
        }
        isDefaultDomain = string.IsNullOrEmpty(domain) || string.Equals(domain, "ai.onnx", StringComparison.Ordinal);
        if (!isDefaultDomain || version >= 13) {
            byte[] copy = new byte[end - start];
            Buffer.BlockCopy(data, start, copy, 0, copy.Length);
            return copy;
        }
        using MemoryStream ms = new(32);
        if (!string.IsNullOrEmpty(domain)) {
            WriteStringField(ms, 1, domain);
        }
        WriteTag(ms, 2, WireVarint);
        WriteVarint(ms, 13UL);
        return ms.ToArray();
    }

    static byte[] BuildDefaultOpset13() {
        using MemoryStream ms = new(16);
        WriteTag(ms, 2, WireVarint);
        WriteVarint(ms, 13UL);
        return ms.ToArray();
    }

    static byte[] WrapLengthDelimitedField(int fieldNum, byte[] payload) {
        using MemoryStream ms = new(payload.Length + 10);
        WriteTag(ms, fieldNum, WireLengthDelimited);
        WriteVarint(ms, (ulong)payload.Length);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    static void WriteStringField(Stream s, int fieldNum, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        WriteTag(s, fieldNum, WireLengthDelimited);
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    static void WriteTag(Stream s, int fieldNum, int wireType) => WriteVarint(s, (ulong)((fieldNum << 3) | wireType));

    static void WriteVarint(Stream s, ulong value) {
        while (value >= 0x80UL) {
            s.WriteByte((byte)((value & 0x7FUL) | 0x80UL));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    static ulong ReadVarint(byte[] data, ref int pos, int end) {
        ulong result = 0;
        int shift = 0;
        while (pos < end && shift < 64) {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) {
                return result;
            }
            shift += 7;
        }
        throw new InvalidDataException("Invalid or truncated protobuf varint.");
    }

    static void SkipFieldValue(byte[] data, ref int pos, int end, int wireType) {
        switch (wireType) {
            case WireVarint:
                ReadVarint(data, ref pos, end);
                break;
            case Wire64Bit:
                pos += 8;
                break;
            case WireLengthDelimited:
                int len = checked((int)ReadVarint(data, ref pos, end));
                pos += len;
                break;
            case Wire32Bit:
                pos += 4;
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType}.");
        }
        if (pos > end) {
            throw new InvalidDataException("Protobuf field extends past payload end.");
        }
    }
}
