using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Android.Content;

using Cavernize.Logic.Spatial;

using Path = System.IO.Path;

namespace CavernizeAndroid.Services;


public sealed partial class NativeOnnxRuntimeBridge {
    sealed class NativeOnnxSession : INeuralSession {
        readonly NativeOnnxRuntimeBridge bridge;
        readonly object sessionLock = new();
        IntPtr sessionHandle;
        readonly string[] inputNames;
        readonly string[] outputNames;
        readonly long[][] inputShapes;
        readonly Dictionary<string, int> inputElementTypes;
        int runCount;
        long totalRunElapsedMs;
        bool disposed;

        public NativeOnnxSession(NativeOnnxRuntimeBridge bridge, string modelPath, IntPtr handle, string activeEp) {
            this.bridge = bridge;
            ModelPath = modelPath ?? string.Empty;
            sessionHandle = handle;
            ExecutionProvider = activeEp;
            try {
                inputNames = ReadNames(isInput: true);
                outputNames = ReadNames(isInput: false);
                (inputShapes, inputElementTypes) = ReadInputMetadata(inputNames);
            } catch {
                if (sessionHandle != IntPtr.Zero) {
                    bridge.fnReleaseSession?.Invoke(sessionHandle);
                    sessionHandle = IntPtr.Zero;
                }
                throw;
            }
        }

        public string ModelPath { get; }
        public string ExecutionProvider { get; }
        public IReadOnlyList<string> InputNames => inputNames;
        public IReadOnlyList<long[]> InputShapes => inputShapes;
        public IReadOnlyList<string> OutputNames => outputNames;

        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            ArgumentNullException.ThrowIfNull(inputTensor);
            if (inputNames.Length == 0) {
                throw new InvalidOperationException("ONNX model exposes no input tensors.");
            }
            Dictionary<string, NeuralTensorFloat> inputs = new(1, StringComparer.Ordinal) {
                [inputNames[0]] = inputTensor
            };
            IReadOnlyDictionary<string, NeuralTensorFloat> outputs = Run(inputs);
            NeuralTensorFloat[] ordered = new NeuralTensorFloat[outputNames.Length];
            for (int o = 0; o < outputNames.Length; o++) {
                outputs.TryGetValue(outputNames[o], out ordered[o]);
            }
            return ordered;
        }

        public IReadOnlyDictionary<string, NeuralTensorFloat> Run(IReadOnlyDictionary<string, NeuralTensorFloat> inputs) {
            ArgumentNullException.ThrowIfNull(inputs);
            lock (sessionLock) {
                if (disposed || sessionHandle == IntPtr.Zero) {
                    throw new ObjectDisposedException(nameof(NativeOnnxSession));
                }

                int inCount = inputs.Count;
                IntPtr[] inNamePtrs = new IntPtr[inCount];
                IntPtr[] inValPtrs = new IntPtr[inCount];
                GCHandle[] pinnedInputs = new GCHandle[inCount];
                ushort[][] rentedFp16Inputs = new ushort[inCount][];
                double[][] rentedFp64Inputs = new double[inCount][];

                int outCount = outputNames.Length;
                IntPtr[] outNamePtrs = new IntPtr[outCount];
                IntPtr[] outValPtrs = new IntPtr[outCount];

                try {
                    int idx = 0;
                    foreach (KeyValuePair<string, NeuralTensorFloat> kv in inputs) {
                        NeuralTensorFloat tensor = kv.Value ?? throw new ArgumentException($"Null input tensor '{kv.Key}'.", nameof(inputs));
                        long expectedElems = ComputeCheckedElementCount(tensor.Shape, allowZero: false);
                        if (tensor.Data.Length < expectedElems) {
                            throw new ArgumentException(
                                $"ONNX input tensor '{kv.Key}' buffer underflow: expected {expectedElems} elements for shape [{string.Join(",", tensor.Shape)}], got {tensor.Data.Length}.");
                        }
                        int elemCount = (int)expectedElems;

                        inNamePtrs[idx] = Marshal.StringToCoTaskMemUTF8(kv.Key);
                        int elemType = inputElementTypes.TryGetValue(kv.Key, out int expectedType)
                            ? expectedType
                            : OnnxTensorElementDataTypeFloat;

                        UIntPtr byteLen;
                        if (elemType == OnnxTensorElementDataTypeFloat16) {
                            ushort[] fp16 = ArrayPool<ushort>.Shared.Rent(elemCount);
                            rentedFp16Inputs[idx] = fp16;
                            ConvertFloat32ToFloat16(tensor.Data, fp16, elemCount);
                            pinnedInputs[idx] = GCHandle.Alloc(fp16, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(ushort)));
                        } else if (elemType == OnnxTensorElementDataTypeDouble) {
                            double[] fp64 = ArrayPool<double>.Shared.Rent(elemCount);
                            rentedFp64Inputs[idx] = fp64;
                            for (int k = 0; k < elemCount; k++) {
                                float v = tensor.Data[k];
                                fp64[k] = float.IsFinite(v) ? v : 0.0;
                            }
                            pinnedInputs[idx] = GCHandle.Alloc(fp64, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(double)));
                        } else if (elemType == OnnxTensorElementDataTypeFloat) {
                            pinnedInputs[idx] = GCHandle.Alloc(tensor.Data, GCHandleType.Pinned);
                            byteLen = checked((UIntPtr)((ulong)elemCount * sizeof(float)));
                        } else {
                            throw new NotSupportedException($"ONNX input tensor '{kv.Key}' uses unsupported ONNXTensorElementDataType={elemType}.");
                        }

                        IntPtr dataPtr = pinnedInputs[idx].AddrOfPinnedObject();
                        bridge.CheckStatus(bridge.fnCreateTensor(
                            bridge.cpuMemoryInfo,
                            dataPtr,
                            byteLen,
                            tensor.Shape,
                            (UIntPtr)tensor.Shape.Length,
                            elemType,
                            out inValPtrs[idx]));
                        idx++;
                    }

                    for (int o = 0; o < outCount; o++) {
                        outNamePtrs[o] = Marshal.StringToCoTaskMemUTF8(outputNames[o]);
                    }

                    Stopwatch swRun = Stopwatch.StartNew();
                    bridge.CheckStatus(bridge.fnRun(
                        sessionHandle,
                        IntPtr.Zero,
                        inNamePtrs,
                        inValPtrs,
                        (UIntPtr)inCount,
                        outNamePtrs,
                        (UIntPtr)outCount,
                        outValPtrs));
                    swRun.Stop();
                    runCount++;
                    totalRunElapsedMs += swRun.ElapsedMilliseconds;

                    Dictionary<string, NeuralTensorFloat> results = new(outCount, StringComparer.Ordinal);
                    for (int o = 0; o < outCount; o++) {
                        IntPtr outVal = outValPtrs[o];
                        if (outVal == IntPtr.Zero) {
                            continue;
                        }
                        bridge.CheckStatus(bridge.fnGetTensorTypeAndShape(outVal, out IntPtr shapeInfo));
                        long[] dims;
                        int outElemType = OnnxTensorElementDataTypeFloat;
                        try {
                            if (bridge.fnGetTensorElementType != null) {
                                IntPtr typeSt = bridge.fnGetTensorElementType(shapeInfo, out int parsedType);
                                if (typeSt == IntPtr.Zero) {
                                    outElemType = parsedType;
                                } else {
                                    bridge.ReleaseStatusQuietly(typeSt);
                                }
                            }
                            bridge.CheckStatus(bridge.fnGetDimsCount(shapeInfo, out UIntPtr dimCount));
                            int dimCountInt = checked((int)dimCount);
                            if (dimCountInt < 0 || dimCountInt > 16) {
                                throw new InvalidDataException($"ONNX output tensor '{outputNames[o]}' has invalid rank {dimCountInt}.");
                            }
                            dims = new long[dimCountInt];
                            if (dims.Length > 0) {
                                bridge.CheckStatus(bridge.fnGetDims(shapeInfo, dims, dimCount));
                            }
                        } finally {
                            if (shapeInfo != IntPtr.Zero) {
                                bridge.fnReleaseTensorTypeAndShapeInfo?.Invoke(shapeInfo);
                            }
                        }

                        long checkedCount = ComputeCheckedElementCount(dims, allowZero: true);
                        int elemCount = checked((int)checkedCount);
                        float[] managed = elemCount > 0 ? new float[elemCount] : Array.Empty<float>();
                        if (elemCount > 0) {
                            bridge.CheckStatus(bridge.fnGetTensorData(outVal, out IntPtr rawPtr));
                            if (rawPtr == IntPtr.Zero) {
                                throw new InvalidDataException($"ONNX output tensor '{outputNames[o]}' returned a null native data pointer.");
                            }
                            if (outElemType == OnnxTensorElementDataTypeFloat16) {
                                short[] raw16 = ArrayPool<short>.Shared.Rent(elemCount);
                                try {
                                    Marshal.Copy(rawPtr, raw16, 0, elemCount);
                                    ConvertFloat16ToFloat32(raw16, managed, elemCount);
                                } finally {
                                    ArrayPool<short>.Shared.Return(raw16);
                                }
                            } else if (outElemType == OnnxTensorElementDataTypeDouble) {
                                double[] raw64 = ArrayPool<double>.Shared.Rent(elemCount);
                                try {
                                    Marshal.Copy(rawPtr, raw64, 0, elemCount);
                                    for (int k = 0; k < elemCount; k++) {
                                        float v = (float)raw64[k];
                                        managed[k] = float.IsFinite(v) ? v : 0f;
                                    }
                                } finally {
                                    ArrayPool<double>.Shared.Return(raw64);
                                }
                            } else if (outElemType == OnnxTensorElementDataTypeFloat) {
                                Marshal.Copy(rawPtr, managed, 0, elemCount);
                                SanitizeFiniteFloat32(managed, elemCount);
                            } else {
                                throw new NotSupportedException($"ONNX output tensor '{outputNames[o]}' uses unsupported ONNXTensorElementDataType={outElemType}.");
                            }
                        }
                        results[outputNames[o]] = new NeuralTensorFloat(dims, managed);
                    }

                    return results;
                } finally {
                    for (int i = 0; i < inCount; i++) {
                        if (inValPtrs[i] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(inValPtrs[i]);
                        if (pinnedInputs[i].IsAllocated) pinnedInputs[i].Free();
                        if (rentedFp16Inputs[i] != null) ArrayPool<ushort>.Shared.Return(rentedFp16Inputs[i]);
                        if (rentedFp64Inputs[i] != null) ArrayPool<double>.Shared.Return(rentedFp64Inputs[i]);
                        if (inNamePtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(inNamePtrs[i]);
                    }
                    for (int o = 0; o < outCount; o++) {
                        if (outValPtrs[o] != IntPtr.Zero) bridge.fnReleaseValue?.Invoke(outValPtrs[o]);
                        if (outNamePtrs[o] != IntPtr.Zero) Marshal.FreeCoTaskMem(outNamePtrs[o]);
                    }
                }
            }
        }

        static void ConvertFloat32ToFloat16(float[] src, ushort[] dst, int count) {
            const int Block = 16384;
            if (count < 65536) {
                for (int k = 0; k < count; k++) {
                    float v = src[k];
                    if (!float.IsFinite(v)) v = 0f;
                    else if (v > Float16FiniteMax) v = Float16FiniteMax;
                    else if (v < -Float16FiniteMax) v = -Float16FiniteMax;
                    dst[k] = BitConverter.HalfToUInt16Bits((Half)v);
                }
                return;
            }
            int blocks = (count + Block - 1) / Block;
            Parallel.For(0, blocks, b => {
                int start = b * Block;
                int end = Math.Min(count, start + Block);
                for (int k = start; k < end; k++) {
                    float v = src[k];
                    if (!float.IsFinite(v)) v = 0f;
                    else if (v > Float16FiniteMax) v = Float16FiniteMax;
                    else if (v < -Float16FiniteMax) v = -Float16FiniteMax;
                    dst[k] = BitConverter.HalfToUInt16Bits((Half)v);
                }
            });
        }

        static void ConvertFloat16ToFloat32(short[] src, float[] dst, int count) {
            const int Block = 16384;
            if (count < 65536) {
                for (int k = 0; k < count; k++) {
                    float v = (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)src[k]));
                    dst[k] = float.IsFinite(v) ? v : 0f;
                }
                return;
            }
            int blocks = (count + Block - 1) / Block;
            Parallel.For(0, blocks, b => {
                int start = b * Block;
                int end = Math.Min(count, start + Block);
                for (int k = start; k < end; k++) {
                    float v = (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)src[k]));
                    dst[k] = float.IsFinite(v) ? v : 0f;
                }
            });
        }

        static void SanitizeFiniteFloat32(float[] buf, int count) {
            const int Block = 32768;
            if (count < 131072) {
                for (int k = 0; k < count; k++) {
                    if (!float.IsFinite(buf[k])) {
                        buf[k] = 0f;
                    }
                }
                return;
            }
            int blocks = (count + Block - 1) / Block;
            Parallel.For(0, blocks, b => {
                int start = b * Block;
                int end = Math.Min(count, start + Block);
                for (int k = start; k < end; k++) {
                    if (!float.IsFinite(buf[k])) {
                        buf[k] = 0f;
                    }
                }
            });
        }

        string[] ReadNames(bool isInput) {
            SessionGetCountDelegate countFn = isInput ? bridge.fnGetInputCount : bridge.fnGetOutputCount;
            SessionGetNameDelegate nameFn = isInput ? bridge.fnGetInputName : bridge.fnGetOutputName;
            bridge.CheckStatus(countFn(sessionHandle, out UIntPtr count));
            int n = checked((int)count);
            if (n < 0 || n > 256) {
                throw new InvalidDataException($"ONNX model reported an invalid tensor count ({n}).");
            }
            string[] names = new string[n];
            for (int i = 0; i < n; i++) {
                bridge.CheckStatus(nameFn(sessionHandle, (UIntPtr)i, bridge.defaultAllocator, out IntPtr namePtr));
                try {
                    names[i] = namePtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(namePtr) ?? $"tensor_{i}") : $"tensor_{i}";
                } finally {
                    if (namePtr != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(bridge.fnAllocatorFree(bridge.defaultAllocator, namePtr));
                    }
                }
            }
            return names;
        }

        (long[][] Shapes, Dictionary<string, int> ElementTypes) ReadInputMetadata(string[] names) {
            int count = names.Length;
            long[][] shapes = new long[count][];
            Dictionary<string, int> types = new(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++) {
                shapes[i] = Array.Empty<long>();
                types[names[i]] = OnnxTensorElementDataTypeFloat;
            }
            if (bridge.fnGetInputTypeInfo == null || bridge.fnCastTypeInfoToTensorInfo == null) {
                return (shapes, types);
            }
            for (int i = 0; i < count; i++) {
                IntPtr typeInfo = IntPtr.Zero;
                try {
                    IntPtr st = bridge.fnGetInputTypeInfo(sessionHandle, (UIntPtr)i, out typeInfo);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    if (typeInfo == IntPtr.Zero) {
                        continue;
                    }
                    st = bridge.fnCastTypeInfoToTensorInfo(typeInfo, out IntPtr tensorInfo);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    if (tensorInfo == IntPtr.Zero) {
                        continue;
                    }
                    if (bridge.fnGetTensorElementType != null) {
                        IntPtr typeSt = bridge.fnGetTensorElementType(tensorInfo, out int elemType);
                        if (typeSt == IntPtr.Zero && elemType > 0) {
                            types[names[i]] = elemType;
                        } else {
                            bridge.ReleaseStatusQuietly(typeSt);
                        }
                    }
                    st = bridge.fnGetDimsCount(tensorInfo, out UIntPtr dimCount);
                    if (st != IntPtr.Zero) {
                        bridge.ReleaseStatusQuietly(st);
                        continue;
                    }
                    int dimCountInt = checked((int)dimCount);
                    if (dimCountInt < 0 || dimCountInt > 16) {
                        continue;
                    }
                    long[] dims = new long[dimCountInt];
                    if (dims.Length > 0) {
                        st = bridge.fnGetDims(tensorInfo, dims, dimCount);
                        if (st != IntPtr.Zero) {
                            bridge.ReleaseStatusQuietly(st);
                            continue;
                        }
                    }
                    shapes[i] = dims;
                } catch {
                    // Leave empty fallback shape if runtime fails to inspect this slot
                } finally {
                    if (typeInfo != IntPtr.Zero) {
                        bridge.fnReleaseTypeInfo?.Invoke(typeInfo);
                    }
                }
            }
            return (shapes, types);
        }

        public void Dispose() {
            bool shouldReleaseRef = false;
            lock (sessionLock) {
                if (!disposed) {
                    disposed = true;
                    shouldReleaseRef = true;
                    if (sessionHandle != IntPtr.Zero) {
                        bridge.fnReleaseSession?.Invoke(sessionHandle);
                        sessionHandle = IntPtr.Zero;
                    }
                }
            }
            if (runCount > 0) {
                double avgMs = totalRunElapsedMs / (double)runCount;
                AppLogger.Info($"ONNX session finished [{ExecutionProvider}] model='{Path.GetFileName(ModelPath)}' chunks={runCount} avg={avgMs:0}ms total={totalRunElapsedMs / 1000.0:0.0}s");
            }
            if (shouldReleaseRef) {
                bridge.ReleaseSessionRef();
            }
        }
    }
}
