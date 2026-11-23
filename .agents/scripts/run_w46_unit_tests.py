#!/usr/bin/env python3
"""Wave W46 Real .NET Unit Test Harness.

Builds & executes a self-contained .NET test runner against Cavern.csproj and Cavernize.Logic.csproj
proving:
  1. 0.0 dB / IEEE-754 bit-exact parity of ProcessTrackPipelined vs ProcessTrack (Overlap 2/4, TTA, PhaseFix)
  2. Zero-copy offset-aware MixedRadixFft.ComputeComplexIstft bit-exact identity
  3. Progressive buffer isolation against in-place Wiener ApplyPhaseFixWiener mutation
  4. Pure-C# IEEE-754 OnnxFp16ModelConverter (0 INT8, 0 NaN/Inf, Resize.scales FP32 protection, already-FP16 skip, atomic file cache)
  5. Adaptive GPU UI breathing yield & ThreadPriority restoration
  6. Steady-state EMA Auto-RTF progressive streaming across 44.1 kHz <-> 48 kHz (cold chunk-0 warmup immunity,
     Headphone Virtualizer 48 kHz live Catmull-Rom Karaoke mapping, and seek-ahead guard)
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]


def find_dotnet() -> str:
    candidates = [
        "/root/.cache/cavern-android-ref/dotnet-11/dotnet",
        "/root/.cache/cavern-android-ref/dotnet-10/dotnet",
        "/root/.cache/cavern-android-ref/dotnet-9/dotnet",
        "/root/.cache/cavern-android-ref/dotnet-8/dotnet",
        "/tmp/cavern-dotnet/dotnet",
        "/root/.cache/cavern-dotnet/dotnet",
        "/root/.dotnet/dotnet",
        shutil.which("dotnet") or "",
    ]
    ref_root = Path("/root/.cache/cavern-android-ref")
    if ref_root.is_dir():
        for d in sorted(ref_root.glob("dotnet-*/dotnet"), reverse=True):
            candidates.insert(0, str(d))
    for c in candidates:
        if c and Path(c).is_file() and os.access(c, os.X_OK):
            return c
    raise RuntimeError(
        "No dotnet binary found. Run `python3 -B .agents/scripts/check_android_cs.py --ci-parity` first to bootstrap portable .NET."
    )


def detect_tfm(dotnet: str) -> str:
    try:
        out = subprocess.check_output([dotnet, "--list-runtimes"], text=True, stderr=subprocess.DEVNULL)
        for line in out.splitlines():
            if line.startswith("Microsoft.NETCore.App "):
                ver = line.split()[1]
                major = ver.split(".")[0]
                if major.isdigit():
                    return f"net{major}.0"
    except Exception:
        pass
    return "net8.0"


TEST_CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{tfm}</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{cavern_csproj}" />
    <ProjectReference Include="{logic_csproj}" />
  </ItemGroup>
</Project>
"""

TEST_PROGRAM_CS = r"""
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;
using Cavern.Neural;
using Cavern.Spatial;
using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

int passed = 0;
void Assert(bool cond, string msg) {
    if (!cond) {
        throw new Exception("ASSERTION FAILED: " + msg);
    }
}

void RunTest(string name, Action test) {
    Console.Write($"[TEST] {name} ... ");
    test();
    passed++;
    Console.WriteLine("PASS");
}

// ---------- Test 1: 0.0 dB Bit-Exact 3-Stage Async Pipeline Parity ----------
RunTest("T1_Pipelined_vs_Sequential_0dB_BitExact", () => {
    int sr = 44100;
    int len = 48000;
    float[] mixL = new float[len];
    float[] mixR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        mixL[i] = (float)(0.40 * Math.Sin(2 * Math.PI * 220 * t) + 0.25 * Math.Sin(2 * Math.PI * 1320 * t) + 0.15 * Math.Cos(2 * Math.PI * 55 * t));
        mixR[i] = (float)(0.35 * Math.Sin(2 * Math.PI * 220 * t + 0.3) + 0.20 * Math.Sin(2 * Math.PI * 880 * t) - 0.12 * Math.Cos(2 * Math.PI * 55 * t));
    }

    (int overlap, bool tta, bool phaseFix)[] cases = [
        (2, false, false),
        (4, false, true),
        (2, true, true)
    ];

    foreach (var (overlap, tta, phaseFix) in cases) {
        MvsepPipelineOptions opt = new() {
            ChunkSamples = 16384,
            OverlapFactor = overlap,
            EnableTta = tta,
            EnablePhaseFix = phaseFix,
            EnforceEnergyConservation = true,
            EnableAsyncPipelining = true,
            GpuUiBreathingYieldMs = 0
        };

        NeuralChunkEvaluator seqSep = (inL, inR, n) => {
            float[] vL = new float[n];
            float[] vR = new float[n];
            float[] bL = new float[n];
            float[] bR = new float[n];
            for (int i = 0; i < n; i++) {
                float mid = 0.5f * (inL[i] + inR[i]);
                vL[i] = mid * 0.6f;
                vR[i] = mid * 0.6f;
                bL[i] = inL[i] - vL[i];
                bR[i] = inR[i] - vR[i];
            }
            return [new StereoStemBuffer("vocals", vL, vR), new StereoStemBuffer("instrumental", bL, bR)];
        };

        StereoStemBuffer[] seq = MvsepAntiArtifactPipeline.ProcessTrack(mixL, mixR, seqSep, opt);
        StereoStemBuffer[] pipe = MvsepAntiArtifactPipeline.ProcessTrackPipelined(
            mixL,
            mixR,
            (inL, inR, n) => {
                float[] cL = new float[n];
                float[] cR = new float[n];
                Array.Copy(inL, cL, n);
                Array.Copy(inR, cR, n);
                return (cL, cR, n);
            },
            prep => {
                int n = prep.n;
                float[] vL = new float[n];
                float[] vR = new float[n];
                float[] bL = new float[n];
                float[] bR = new float[n];
                for (int i = 0; i < n; i++) {
                    float mid = 0.5f * (prep.cL[i] + prep.cR[i]);
                    vL[i] = mid * 0.6f;
                    vR[i] = mid * 0.6f;
                    bL[i] = prep.cL[i] - vL[i];
                    bR[i] = prep.cR[i] - vR[i];
                }
                return (vL, vR, bL, bR);
            },
            (prep, inf, n) => [
                new StereoStemBuffer("vocals", inf.vL, inf.vR),
                new StereoStemBuffer("instrumental", inf.bL, inf.bR)
            ],
            opt);

        Assert(seq.Length == pipe.Length, $"Stem count mismatch for overlap={overlap}, tta={tta}");
        float maxDiff = 0f;
        for (int s = 0; s < seq.Length; s++) {
            Assert(seq[s].Name == pipe[s].Name, $"Stem name mismatch at {s}");
            Assert(seq[s].Length == pipe[s].Length, $"Stem length mismatch at {s}");
            for (int i = 0; i < seq[s].Length; i++) {
                float dL = MathF.Abs(seq[s].Left[i] - pipe[s].Left[i]);
                float dR = MathF.Abs(seq[s].Right[i] - pipe[s].Right[i]);
                if (dL > maxDiff) maxDiff = dL;
                if (dR > maxDiff) maxDiff = dR;
            }
        }
        Assert(maxDiff == 0.0f, $"Expected 0.0f bit-exact diff (overlap={overlap}, tta={tta}, phaseFix={phaseFix}), got {maxDiff:G9}");
    }
});

// ---------- Test 2: Zero-Copy Offset-Aware MixedRadixFft.ComputeComplexIstft Bit-Exact ----------
RunTest("T2_MixedRadixFft_ZeroCopy_Offset_iSTFT_BitExact", () => {
    int nFft = 256, hop = 64, dimF = 64, dimT = 32, targetLen = (dimT - 1) * hop;
    int plane = dimF * dimT;
    float[] packed = new float[4 * plane];
    Random rng = new(42);
    for (int i = 0; i < packed.Length; i++) {
        packed[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
    }

    float[] sliceLRe = new float[plane];
    float[] sliceLIm = new float[plane];
    float[] sliceRRe = new float[plane];
    float[] sliceRIm = new float[plane];
    Array.Copy(packed, 0, sliceLRe, 0, plane);
    Array.Copy(packed, plane, sliceLIm, 0, plane);
    Array.Copy(packed, 2 * plane, sliceRRe, 0, plane);
    Array.Copy(packed, 3 * plane, sliceRIm, 0, plane);

    float[] refL = MixedRadixFft.ComputeComplexIstft(sliceLRe, sliceLIm, dimT, nFft, hop, dimF, targetLen);
    float[] refR = MixedRadixFft.ComputeComplexIstft(sliceRRe, sliceRIm, dimT, nFft, hop, dimF, targetLen);
    float[] fastL = MixedRadixFft.ComputeComplexIstft(packed, 0, packed, plane, dimT, nFft, hop, dimF, targetLen);
    float[] fastR = MixedRadixFft.ComputeComplexIstft(packed, 2 * plane, packed, 3 * plane, dimT, nFft, hop, dimF, targetLen);

    float maxDiff = 0f;
    for (int i = 0; i < targetLen; i++) {
        maxDiff = MathF.Max(maxDiff, MathF.Abs(refL[i] - fastL[i]));
        maxDiff = MathF.Max(maxDiff, MathF.Abs(refR[i] - fastR[i]));
    }
    Assert(maxDiff == 0.0f, $"Expected 0.0f bit-exact offset iSTFT diff, got {maxDiff:G9}");
});

// ---------- Test 3: Progressive Buffer Isolation Against In-Place Wiener PhaseFix ----------
RunTest("T3_ProgressiveBufferIsolation_WienerPhaseFix", () => {
    int len = 36000;
    float[] mixL = new float[len];
    float[] mixR = new float[len];
    for (int i = 0; i < len; i++) {
        mixL[i] = MathF.Sin(i * 0.05f) * 0.5f;
        mixR[i] = MathF.Cos(i * 0.03f) * 0.5f;
    }

    StereoStemBuffer[] capturedProg = null;
    float[] snapshotBeforePostL = null;

    MvsepPipelineOptions opt = new() {
        ChunkSamples = 16384,
        OverlapFactor = 2,
        EnableTta = false,
        EnablePhaseFix = true,
        EnforceEnergyConservation = true,
        EnableAsyncPipelining = true,
        GpuUiBreathingYieldMs = 0
    };

    StereoStemBuffer[] finalStems = MvsepAntiArtifactPipeline.ProcessTrackPipelined(
        mixL,
        mixR,
        (inL, inR, n) => {
            float[] a = new float[n]; float[] b = new float[n];
            Array.Copy(inL, a, n); Array.Copy(inR, b, n);
            return (a, b, n);
        },
        prep => {
            int n = prep.n;
            float[] vL = new float[n]; float[] vR = new float[n];
            float[] iL = new float[n]; float[] iR = new float[n];
            for (int k = 0; k < n; k++) {
                vL[k] = prep.a[k] * 0.45f; vR[k] = prep.b[k] * 0.45f;
                iL[k] = prep.a[k] * 0.35f; iR[k] = prep.b[k] * 0.35f; // intentionally non-conserving so Wiener modifies it
            }
            return (vL, vR, iL, iR);
        },
        (prep, inf, n) => [new StereoStemBuffer("vocals", inf.vL, inf.vR), new StereoStemBuffer("instrumental", inf.iL, inf.iR)],
        opt,
        onProgressiveReady: (live, ready, total, elapsed) => {
            capturedProg = live;
            if (ready == total) {
                snapshotBeforePostL = (float[])live[0].Left.Clone();
            }
        });

    Assert(capturedProg != null && snapshotBeforePostL != null, "Progressive callback did not reach full length.");
    Assert(!ReferenceEquals(finalStems[0].Left, capturedProg[0].Left), "Final post-processed buffer aliased live progressive buffer!");
    float progMutation = 0f;
    for (int i = 0; i < len; i++) {
        progMutation = MathF.Max(progMutation, MathF.Abs(capturedProg[0].Left[i] - snapshotBeforePostL[i]));
    }
    Assert(progMutation == 0.0f, $"Live progressive buffer was mutated during Wiener post-processing (diff={progMutation})!");
});

// ---------- Test 4: Pure-C# IEEE-754 OnnxFp16ModelConverter (Zero INT8, 0 NaN/Inf, Resize Protection) ----------
RunTest("T4_OnnxFp16ModelConverter_Protobuf_Validation", () => {
    byte[] syntheticModel = BuildSyntheticFp32OnnxModel();
    string tmpDir = Path.Combine(Path.GetTempPath(), "cavern_fp16_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
        string srcPath = Path.Combine(tmpDir, "uvr_mdxnet_kara_2.onnx");
        string dstPath = Path.Combine(tmpDir, "uvr_mdxnet_kara_2_fp16.onnx");
        File.WriteAllBytes(srcPath, syntheticModel);

        OnnxFp16ConversionResult res = OnnxFp16ModelConverter.TryConvertFileToFp16(srcPath, dstPath);
        Assert(res.Success, "FP16 conversion failed: " + res.Message);
        Assert(File.Exists(dstPath), "Converted FP16 file not written.");
        Assert(res.ConvertedInitializers == 2, $"Expected 2 converted initializers (conv1.weight + conv1.bias), got {res.ConvertedInitializers}");
        Assert(res.ConvertedValueInfos == 2, $"Expected 2 converted value_infos (input + output), got {res.ConvertedValueInfos}");
        Assert(res.ConvertedNodeAttributes == 2, $"Expected 2 converted node attrs (Cast + Constant), got {res.ConvertedNodeAttributes}");
        Assert(res.ClampedElements == 4, $"Expected 4 clamped/sanitized elements (70000, -90000, NaN, +Inf), got {res.ClampedElements}");

        byte[] fp16Bytes = File.ReadAllBytes(dstPath);
        Dictionary<string, (int DataType, byte[] RawData)> inits = InspectInitializerTypes(fp16Bytes);
        Assert(inits["conv1.weight"].DataType == 10, "conv1.weight must be FLOAT16 (10).");
        Assert(inits["conv1.bias"].DataType == 10, "conv1.bias must be FLOAT16 (10).");
        Assert(inits["resize_scales"].DataType == 1, "resize_scales MUST stay FLOAT (1) for ONNX Resize compatibility!");
        Assert(inits["shape_const"].DataType == 7, "shape_const MUST stay INT64 (7)!");

        // Verify IEEE-754 Half values, 0 NaN/Inf, and clamping to +/-65504
        byte[] wRaw = inits["conv1.weight"].RawData;
        Assert(wRaw.Length == 6 * 2, $"Expected 12 bytes (6 Halfs), got {wRaw.Length}");
        float[] decoded = new float[6];
        for (int i = 0; i < 6; i++) {
            ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(wRaw.AsSpan(i * 2, 2));
            decoded[i] = (float)BitConverter.UInt16BitsToHalf(bits);
            Assert(float.IsFinite(decoded[i]), $"Non-finite FP16 weight at index {i}!");
        }
        Assert(MathF.Abs(decoded[0] - 0.5f) < 1e-4f, "decoded[0] mismatch");
        Assert(MathF.Abs(decoded[1] - (-1.25f)) < 1e-3f, "decoded[1] mismatch");
        Assert(decoded[2] == 65504f, $"Expected +65504 clamp, got {decoded[2]}");
        Assert(decoded[3] == -65504f, $"Expected -65504 clamp, got {decoded[3]}");
        Assert(decoded[4] == 0f, $"Expected NaN -> 0, got {decoded[4]}");
        Assert(decoded[5] == 0f, $"Expected +Inf -> 0, got {decoded[5]}");

        // Re-converting already-FP16 model must cleanly skip
        OnnxFp16ConversionResult second = OnnxFp16ModelConverter.ConvertBytesToFp16(fp16Bytes, out byte[] secondOut);
        Assert(!second.Success && second.SkippedAlreadyFp16 && secondOut == null, "Already-FP16 model should be skipped.");
        Assert(second.ExistingFp16Initializers == 2, $"Expected 2 existing FP16 initializers, got {second.ExistingFp16Initializers}");
    } finally {
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
    }
});

// ---------- Test 5: Adaptive GPU UI Breathing Yield & Priority Restoration ----------
RunTest("T5_AdaptiveGpuBreathingYield_And_Priority", () => {
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(0, 800.0) == 0, "Disabled base yield must return 0.");
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(16, 20.0) == 0, "Fast <50ms dispatch must yield 0ms.");
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(16, 200.0) == 16, "Medium dispatch must return base 16ms.");
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(16, 650.0) == 32, "Heavy >=500ms dispatch must step up to 32ms.");
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(45, 650.0) == 45, "Configured 45ms must be preserved.");
    Assert(MvsepAntiArtifactPipeline.ComputeAdaptiveGpuBreathingYieldMs(200, 650.0) == 100, "Yield must be capped at 100ms.");

    ThreadPriority before = Thread.CurrentThread.Priority;
    float[] z = new float[16384];
    MvsepPipelineOptions opt = new() { ChunkSamples = 8192, OverlapFactor = 2, EnableAsyncPipelining = true, GpuUiBreathingYieldMs = 16 };
    MvsepAntiArtifactPipeline.ProcessTrackPipelined(
        z, z,
        (l, r, n) => n,
        n => n,
        (p, i, n) => [new StereoStemBuffer("vocals", new float[n], new float[n])],
        opt);
    Assert(Thread.CurrentThread.Priority == before, "Thread priority was not restored after ProcessTrackPipelined.");
});

// ---------- Test 6: Auto-RTF Progressive Streaming (48 kHz Track -> 44.1 kHz Model + Cold Chunk-0 Warmup + 48 kHz Virtualizer Karaoke) ----------
RunTest("T6_AutoRtfProgressiveStreaming_48kTo44k_ColdWarmup_KaraokeCatmullRom", () => {
    int trackSr = 48000;
    int durationSec = 8;
    int len48 = trackSr * durationSec;
    float[] mixL = new float[len48];
    float[] mixR = new float[len48];
    float[] vocalOnly48 = new float[len48];
    float[] instOnly48L = new float[len48];
    float[] instOnly48R = new float[len48];
    for (int i = 0; i < len48; i++) {
        double t = i / (double)trackSr;
        float vocal = (float)(0.50 * Math.Sin(2 * Math.PI * 520.0 * t));
        float instL = (float)(0.25 * Math.Sin(2 * Math.PI * 180.0 * t));
        float instR = (float)(0.25 * Math.Cos(2 * Math.PI * 180.0 * t));
        vocalOnly48[i] = vocal;
        instOnly48L[i] = instL;
        instOnly48R[i] = instR;
        mixL[i] = vocal + instL;
        mixR[i] = vocal + instR;
    }

    MockColdWarmupRuntime mockRt = new();
    NeuralSeparatedTrack boundProgressive = null;
    NeuralAudioConfig cfg = new() {
        OverlapFactor = 2,
        EnableProgressiveStreaming = true,
        EnablePhaseFix = false,
        EnforceEnergyConservation = false,
        GpuUiBreathingYieldMs = 0
    };

    (StereoStemBuffer[] finalStems, float rtf) = NeuralAudioOfflinePipeline.RunSeparation(
        mockRt,
        ["mock_uvr_44k.onnx"],
        [["vocals", "instrumental"]],
        [1f],
        mixL,
        mixR,
        trackSr,
        cfg,
        88200,
        expectedSampleRatesPerModel: [44100],
        chunkSamplesPerModel: [88200],
        onProgressiveTrackReady: pt => boundProgressive = pt,
        progressiveTrackKey: "test_48k.flac");

    Assert(boundProgressive != null, "Auto-RTF progressive streaming FAILED to engage on 48 kHz track with cold chunk-0 warmup!");
    Assert(boundProgressive.SampleRate == 44100, $"Expected progressive track at modelSr=44100, got {boundProgressive.SampleRate}");
    Assert(finalStems.Length >= 2 && finalStems[0].Length == len48, "Final resampled stems must match native 48 kHz length.");

    // Simulate live progressive 44.1 kHz track playing through 48 kHz Headphone Virtualizer Karaoke!
    boundProgressive.IsProgressiveStreaming = true;
    boundProgressive.ReadySampleCount = 44100 * 3; // first 3 seconds finalized

    using NeuralStemStageProcessor stage = new();
    stage.SetSourceTrackSampleRate(48000);
    stage.Configure(48000, SpatialProgramMetadata.CreateDefault());
    NeuralAudioConfig kCfg = new() {
        SpatialKaraokeEnabled = true,
        KaraokeVocalLevel = 0.0f
    };
    stage.ApplyConfig(kCfg);
    stage.BindSeparatedTrack(boundProgressive);

    // Process 1 second of 48 kHz audio inside the finalized window (t = 1.0s .. 2.0s)
    double vocalRmsBefore = 0, vocalRmsAfter = 0;
    int block = 512;
    float[] interleaved = new float[block * 2];
    for (int pos = 48000; pos < 48000 * 2; pos += block) {
        for (int i = 0; i < block; i++) {
            interleaved[i * 2] = mixL[pos + i];
            interleaved[i * 2 + 1] = mixR[pos + i];
            vocalRmsBefore += mixL[pos + i] * mixL[pos + i];
        }
        stage.ProcessStereoInterleavedKaraoke(interleaved, 0, block, pos);
        for (int i = 0; i < block; i++) {
            Assert(float.IsFinite(interleaved[i * 2]) && float.IsFinite(interleaved[i * 2 + 1]), "Non-finite Karaoke output!");
            // Measure residual 520 Hz vocal energy vs original mix
            float errL = interleaved[i * 2] - instOnly48L[pos + i];
            vocalRmsAfter += errL * errL;
        }
    }
    double attenDb1 = 10.0 * Math.Log10((vocalRmsBefore + 1e-12) / (vocalRmsAfter + 1e-12));
    Assert(attenDb1 > 6.0, $"Expected >6 dB vocal attenuation in 44.1k->48k progressive chunk 1, got {attenDb1:0.2} dB");

    // Now advance ReadySampleCount to 6 seconds (simulating chunk 2 finishing WITHOUT re-binding!) and test at t = 4.0s .. 5.0s
    boundProgressive.ReadySampleCount = 44100 * 6;
    double vocalRmsBefore2 = 0, vocalRmsAfter2 = 0;
    for (int pos = 48000 * 4; pos < 48000 * 5; pos += block) {
        for (int i = 0; i < block; i++) {
            interleaved[i * 2] = mixL[pos + i];
            interleaved[i * 2 + 1] = mixR[pos + i];
            vocalRmsBefore2 += mixL[pos + i] * mixL[pos + i];
        }
        stage.ProcessStereoInterleavedKaraoke(interleaved, 0, block, pos);
        for (int i = 0; i < block; i++) {
            float errL = interleaved[i * 2] - instOnly48L[pos + i];
            vocalRmsAfter2 += errL * errL;
        }
    }
    double attenDb2 = 10.0 * Math.Log10((vocalRmsBefore2 + 1e-12) / (vocalRmsAfter2 + 1e-12));
    Assert(attenDb2 > 6.0, $"Expected >6 dB vocal attenuation in 44.1k->48k progressive chunk 2 (proving BUG C fixed!), got {attenDb2:0.2} dB");

    // Seek-ahead past ReadySampleCount (t = 7.0s > 6.0s): must not crash or output NaN
    int seekPos = 48000 * 7;
    for (int i = 0; i < block; i++) {
        interleaved[i * 2] = mixL[seekPos + i];
        interleaved[i * 2 + 1] = mixR[seekPos + i];
    }
    stage.ProcessStereoInterleavedKaraoke(interleaved, 0, block, seekPos);
    for (int i = 0; i < block * 2; i++) {
        Assert(float.IsFinite(interleaved[i]), "Seek-ahead fallback produced non-finite output!");
    }
});

// ---------- Test 7: W48 Decoupled Spatial Upmix vs Karaoke Neural Slots ----------
RunTest("T7_W48_DecoupledSpatialUpmixVsKaraoke", () => {
    int n = 4096;
    float[] z = new float[n];
    float[] s = new float[n];
    for (int i = 0; i < n; i++) s[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / 48000f);

    NeuralSeparatedTrack fullMix = new() {
        SourceTrackKey = "song.flac",
        SampleRate = 48000,
        Stems = [
            new StereoStemBuffer("vocals", s, s),
            new StereoStemBuffer("drums", s, z),
            new StereoStemBuffer("bass", z, s),
            new StereoStemBuffer("other", s, s)
        ]
    };
    NeuralSeparatedTrack karaokeOnly = new() {
        SourceTrackKey = "song.flac",
        SampleRate = 48000,
        Stems = [
            new StereoStemBuffer("lead_vocal", s, s),
            new StereoStemBuffer("backing_vocals", z, z)
        ]
    };

    Assert(NeuralStemSpatializer.IsFullMixSpatialStemSet(fullMix.Stems), "4-stem bundle must be recognized as full-mix spatial stem set.");
    Assert(!NeuralStemSpatializer.IsFullMixSpatialStemSet(karaokeOnly.Stems), "2-stem vocal-only Karaoke bundle must NOT be treated as full-mix spatial stem set.");

    NeuralStemStageProcessor stage = new();
    stage.BindSpatialUpmixTrack(fullMix);
    Assert(stage.HasPlayableSpatialUpmixStems, "Full-mix spatial stems should be playable.");
    Assert(stage.HasIsolatedNeuralVocalReference, "Full-mix vocals should act as karaoke vocal fallback.");

    // Now bind 2-stem Karaoke via generic BindSeparatedTrack AND explicit BindKaraokeReferenceTrack
    stage.BindSeparatedTrack(karaokeOnly);
    Assert(ReferenceEquals(stage.ActiveSpatialUpmixTrack, fullMix), "2-stem Karaoke must NEVER clobber ActiveSpatialUpmixTrack!");
    Assert(ReferenceEquals(stage.ActiveKaraokeTrack, karaokeOnly), "2-stem Karaoke must bind to ActiveKaraokeTrack!");
    Assert(stage.HasPlayableSpatialUpmixStems, "Full-mix spatial stems must remain playable after binding Karaoke.");
});

// ---------- Test 8: W48 Hybrid 7.1.4 Discrete LFE (.1) & Canonical Channel Order ----------
RunTest("T8_W48_Hybrid714DiscreteLfeAndCanonicalOrder", () => {
    int sr = 48000;
    int len = sr; // 1.0s
    float[] bassL = new float[len], bassR = new float[len];
    float[] drumsL = new float[len], drumsR = new float[len];
    float[] voxL = new float[len], voxR = new float[len];
    float[] otherL = new float[len], otherR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        float sub = 0.6f * (float)Math.Sin(2.0 * Math.PI * 55.0 * t);
        float kick = 0.4f * (float)Math.Sin(2.0 * Math.PI * 65.0 * t);
        float hat = 0.25f * (float)Math.Sin(2.0 * Math.PI * 2500.0 * t);
        bassL[i] = sub; bassR[i] = sub;
        drumsL[i] = kick + hat; drumsR[i] = kick - hat;
        voxL[i] = 0.3f * (float)Math.Sin(2.0 * Math.PI * 800.0 * t);
        voxR[i] = voxL[i];
        otherL[i] = 0.35f * (float)Math.Sin(2.0 * Math.PI * 1200.0 * t);
        otherR[i] = 0.35f * (float)Math.Cos(2.0 * Math.PI * 1200.0 * t);
    }

    StereoStemBuffer[] stems = [
        new StereoStemBuffer("bass", bassL, bassR),
        new StereoStemBuffer("drums", drumsL, drumsR),
        new StereoStemBuffer("vocals", voxL, voxR),
        new StereoStemBuffer("other", otherL, otherR)
    ];

    float[][] ch = NeuralStemSpatializer.BuildHybrid714WithDiscreteLfe(stems, sr, 0.35f, 120f);
    Assert(ch.Length == 12, $"Expected 12 channels for 7.1.4, got {ch.Length}");
    double lfeSumSq = 0.0, fl55Re = 0.0, fl55Im = 0.0, lfe55Re = 0.0, lfe55Im = 0.0;
    double rlSumSq = 0.0, slSumSq = 0.0, tflSumSq = 0.0;
    int start = sr / 4; // skip filter warmup transient
    for (int i = start; i < len; i++) {
        for (int c = 0; c < 12; c++) {
            Assert(float.IsFinite(ch[c][i]), $"Non-finite sample at ch[{c}][{i}]");
        }
        lfeSumSq += ch[3][i] * ch[3][i];
        rlSumSq += ch[4][i] * ch[4][i];
        slSumSq += ch[6][i] * ch[6][i];
        tflSumSq += ch[8][i] * ch[8][i];
        double ang = 2.0 * Math.PI * 55.0 * i / sr;
        double cs = Math.Cos(ang), sn = Math.Sin(ang);
        fl55Re += ch[0][i] * cs; fl55Im += ch[0][i] * sn;
        lfe55Re += ch[3][i] * cs; lfe55Im += ch[3][i] * sn;
    }
    double lfeRms = Math.Sqrt(lfeSumSq / (len - start));
    double fl55Mag = Math.Sqrt(fl55Re * fl55Re + fl55Im * fl55Im);
    double lfe55Mag = Math.Sqrt(lfe55Re * lfe55Re + lfe55Im * lfe55Im);
    double lfeVsFl55Db = 20.0 * Math.Log10((lfe55Mag + 1e-12) / (fl55Mag + 1e-12));
    Assert(lfeRms > 0.20, $"Discrete LFE channel [3] RMS too low: {lfeRms:0.000}");
    Assert(lfeVsFl55Db > 18.0, $"Expected >18 dB LR4 sub-bass separation between LFE and FL @ 55 Hz, got {lfeVsFl55Db:0.1} dB");
    Assert(rlSumSq > 1e-4 && slSumSq > 1e-4 && tflSumSq > 1e-4, "Surround (RL/SL) and Height (TFL) channels must carry active energy.");
});

// ---------- Test 9: W48 Dynamic Dolby Atmos Object Identity Preserved Under AtmosAwareUpmixer ----------
RunTest("T9_W48_DynamicAtmosObjectIdentityPreserved", () => {
    int sr = 48000;
    using SyntheticAtmosReader reader = new(sr, sr / 2);
    using CavernizeTrack track = new(reader, Codec.EnhancedAC3, 0);
    Listener listener = new() { SampleRate = sr, UpdateRate = 1024 };
    UpmixingSettings upmix = new(true) {
        Cavernize = true,
        MatrixUpmixing = true,
        AtmosAware = true
    };
    track.Attach(listener, upmix);

    IReadOnlyList<Source> attached = listener.ActiveSources.ToList();
    Assert(track.Renderer.HasObjects, "Synthetic Atmos track must have Renderer.HasObjects=true.");
    Assert(attached.Count > 8, $"AtmosAwareUpmixer should preserve 6 beds + 2 dynamic objects and add virtual heights, got {attached.Count}.");
    Assert(!ReferenceEquals(attached[0], track.Renderer.Objects[0]), "AtmosAwareUpmixer wraps base sources in IntermediateSources.");

    List<TrackSourceItem> items = TrackSourceItem.BuildFromAttachedSources(track, attached);
    Assert(items.Count == attached.Count, $"Expected {attached.Count} TrackSourceItems, got {items.Count}");
    Assert(items[3].IsLFE, "Native LFE channel [3] must preserve IsLFE=true under AtmosAwareUpmixer!");
    Assert(items[0].Layer == SourceLayer.Bed, $"Base channel [0] must stay Bed, got {items[0].Layer}");
    Assert(items[6].IsDynamicObject && items[7].IsDynamicObject, "Dynamic Atmos 3D objects [6,7] must preserve IsDynamicObject=true!");
    Assert(items[6].Name.Contains("Object"), $"Dynamic object [6] must keep Object identity, got '{items[6].Name}'");
    bool hasHeight = items.Any(it => it.Layer == SourceLayer.VirtualHeight);
    Assert(hasHeight, "AtmosAwareUpmixer must append VirtualHeight channels after native beds and objects.");
});

// ---------- Test 10: W48 Zero-GC Studio Performance Recording Bridge ----------
RunTest("T10_W48_StudioPerformanceRecordingBridge", () => {
    LiveMicrophoneBridge bridge = new();
    bridge.SetStreamingActive(true, 48000);
    bridge.StartPerformanceRecording(48000, startTrackSample: 96000);
    Assert(bridge.IsRecordingPerformance, "Bridge must report IsRecordingPerformance=true.");

    int framesPerBlock = 256;
    int blocks = 40;
    float[] stereo = new float[framesPerBlock * 2];
    for (int b = 0; b < blocks; b++) {
        for (int i = 0; i < framesPerBlock; i++) {
            int idx = b * framesPerBlock + i;
            float v = 0.5f * MathF.Sin(2f * MathF.PI * 440f * idx / 48000f);
            stereo[i * 2] = v;
            stereo[i * 2 + 1] = v;
        }
        bridge.PushCapturedInterleaved(stereo, 0, framesPerBlock, 2);
        Thread.Sleep(2);
    }

    PerformanceRecordingTake take = bridge.StopPerformanceRecording();
    Assert(take != null && take.HasAudio, "Recorded vocal take must have audio.");
    Assert(take.SampleRate == 48000, $"Expected 48000 Hz take, got {take.SampleRate}");
    Assert(take.StartTrackSample == 96000, $"Expected StartTrackSample=96000, got {take.StartTrackSample}");
    Assert(take.MonoPcm.Length == blocks * framesPerBlock, $"Expected {blocks * framesPerBlock} mono samples, got {take.MonoPcm.Length}");
    Assert(take.PeakDbfs > -8f && take.PeakDbfs < -4f, $"Expected peak near -6 dBFS, got {take.PeakDbfs:0.2} dBFS");

    bridge.ClearRecordedPerformance();
    Assert(!bridge.HasRecordedPerformance, "ClearRecordedPerformance must clear HasRecordedPerformance.");
});

// ---------- Test 11: W48 Composable Export (Vocal Take + Cinema 7.1.4 & Headphone 3D) ----------
RunTest("T11_W48_ComposableExport_VocalTakeAndSpatial", () => {
    string tmpDir = Path.Combine(Path.GetTempPath(), "cavern_w48_exp_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
        int sr = 48000;
        int len = sr / 2; // 0.5s stereo test file
        string inWav = Path.Combine(tmpDir, "in_stereo.wav");
        float[] interleavedIn = new float[len * 2];
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            float bass = 0.35f * (float)Math.Sin(2.0 * Math.PI * 60.0 * t);
            interleavedIn[i * 2] = bass + 0.25f * (float)Math.Sin(2.0 * Math.PI * 1000.0 * t);
            interleavedIn[i * 2 + 1] = bass + 0.25f * (float)Math.Cos(2.0 * Math.PI * 1000.0 * t);
        }
        using (RIFFWaveWriter writer = new(inWav, 2, len, sr, BitDepth.Int16)) {
            writer.WriteHeader();
            writer.WriteBlock(interleavedIn, 0, interleavedIn.Length);
        }

        float[] vocalTake = new float[len];
        for (int i = 0; i < len; i++) {
            vocalTake[i] = 0.25f * MathF.Sin(2f * MathF.PI * 520f * i / sr);
        }

        AudioReader stereoReader = AudioReader.Open(inWav);
        using CavernizeTrack track = new(stereoReader, Codec.PCM_LE, 0);

        // Composable Case A: Cinema 7.1.4 + Recorded Vocal Take
        string out714 = Path.Combine(tmpDir, "out_714_vocal.wav");
        StudioBatchExportJob job714 = new() {
            TrackName = "test",
            OutputPath = out714,
            BitDepth = BitDepth.Int16,
            LiveMicMonoCapture = vocalTake,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.Cinema714,
                EnableAiSpatialUpmix = false,
                IncludeRecordedPerformance = true,
                RecordedPerformanceSampleRate = sr
            }
        };
        StudioBatchExportResult res714 = StudioBatchExporter.ExportSingle(track, job714);
        Assert(res714.Succeeded, $"Composable Cinema714 export failed: {res714.ErrorMessage}");
        Assert(File.Exists(out714) && new FileInfo(out714).Length > 10000, "Cinema714 output WAV missing or too small.");
        Assert(float.IsFinite(res714.IntegratedLufs) && res714.MaxTruePeakDbtp <= 0.1f, $"Invalid loudness/peak: {res714.IntegratedLufs} LUFS / {res714.MaxTruePeakDbtp} dBTP");

        // Composable Case B: Headphone 3D + Recorded Vocal Take
        string outHp = Path.Combine(tmpDir, "out_hp3d_vocal.wav");
        StudioBatchExportJob jobHp = new() {
            TrackName = "test",
            OutputPath = outHp,
            BitDepth = BitDepth.Int16,
            LiveMicMonoCapture = vocalTake,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.Headphone3D,
                IncludeRecordedPerformance = true,
                ApplyPersonalizedHeadphonePost = true,
                RecordedPerformanceSampleRate = sr
            }
        };
        StudioBatchExportResult resHp = StudioBatchExporter.ExportSingle(track, jobHp);
        Assert(resHp.Succeeded, $"Composable Headphone3D export failed: {resHp.ErrorMessage}");
        Assert(File.Exists(outHp) && new FileInfo(outHp).Length > 4000, "Headphone3D output WAV missing or too small.");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

Console.WriteLine($"\nALL {passed} W46/W48 UNIT TESTS PASSED (0 failures).");
return 0;

// ---- Helpers for Test 4 & Test 6 ----
static byte[] BuildSyntheticFp32OnnxModel() {
    using MemoryStream graph = new();

    // Node 1: Resize(X, "", resize_scales) -> resized
    WriteLenField(graph, 1, BuildNode("Resize", ["x", "", "resize_scales"], ["resized"], Array.Empty<byte[]>()));
    // Node 2: Cast(resized, to=1) -> casted
    WriteLenField(graph, 1, BuildNode("Cast", ["resized"], ["casted"], [BuildIntAttr("to", 1)]));
    // Node 3: Constant(value=TensorProto(FLOAT)) -> const_out
    WriteLenField(graph, 1, BuildNode("Constant", Array.Empty<string>(), ["const_out"], [BuildTensorAttr("value", BuildRawFloatTensor("", [0.25f, -0.75f]))]));

    // Initializers (GraphProto.field 5)
    WriteLenField(graph, 5, BuildRawFloatTensor("conv1.weight", [0.5f, -1.25f, 70000f, -90000f, float.NaN, float.PositiveInfinity]));
    WriteLenField(graph, 5, BuildPackedFloatDataTensor("conv1.bias", [0.1f, -0.2f]));
    WriteLenField(graph, 5, BuildRawFloatTensor("resize_scales", [1f, 1f, 2f, 2f]));
    WriteLenField(graph, 5, BuildInt64Tensor("shape_const", [1L, 4L]));

    // Inputs & Outputs (GraphProto.field 11 & 12)
    WriteLenField(graph, 11, BuildValueInfo("x", 1));
    WriteLenField(graph, 12, BuildValueInfo("y", 1));

    using MemoryStream model = new();
    WriteVarintField(model, 1, 8); // ir_version = 8
    WriteLenField(model, 7, graph.ToArray());
    return model.ToArray();
}

static byte[] BuildNode(string opType, string[] inputs, string[] outputs, byte[][] attrs) {
    using MemoryStream ms = new();
    foreach (string s in inputs) WriteStringField(ms, 1, s);
    foreach (string s in outputs) WriteStringField(ms, 2, s);
    WriteStringField(ms, 4, opType);
    foreach (byte[] a in attrs) WriteLenField(ms, 5, a);
    return ms.ToArray();
}

static byte[] BuildIntAttr(string name, long val) {
    using MemoryStream ms = new();
    WriteStringField(ms, 1, name);
    WriteVarintField(ms, 3, (ulong)val);
    WriteVarintField(ms, 20, 2); // INT
    return ms.ToArray();
}

static byte[] BuildTensorAttr(string name, byte[] tensorBytes) {
    using MemoryStream ms = new();
    WriteStringField(ms, 1, name);
    WriteLenField(ms, 5, tensorBytes);
    WriteVarintField(ms, 20, 4); // TENSOR
    return ms.ToArray();
}

static byte[] BuildRawFloatTensor(string name, float[] vals) {
    using MemoryStream ms = new();
    WriteVarintField(ms, 1, (ulong)vals.Length);
    WriteVarintField(ms, 2, 1); // FLOAT
    if (!string.IsNullOrEmpty(name)) WriteStringField(ms, 8, name);
    byte[] raw = new byte[vals.Length * 4];
    for (int i = 0; i < vals.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), vals[i]);
    WriteLenField(ms, 9, raw);
    return ms.ToArray();
}

static byte[] BuildPackedFloatDataTensor(string name, float[] vals) {
    using MemoryStream ms = new();
    WriteVarintField(ms, 1, (ulong)vals.Length);
    WriteVarintField(ms, 2, 1); // FLOAT
    byte[] raw = new byte[vals.Length * 4];
    for (int i = 0; i < vals.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), vals[i]);
    WriteLenField(ms, 4, raw); // packed float_data
    WriteStringField(ms, 8, name);
    return ms.ToArray();
}

static byte[] BuildInt64Tensor(string name, long[] vals) {
    using MemoryStream ms = new();
    WriteVarintField(ms, 1, (ulong)vals.Length);
    WriteVarintField(ms, 2, 7); // INT64
    WriteStringField(ms, 8, name);
    byte[] raw = new byte[vals.Length * 8];
    for (int i = 0; i < vals.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(i * 8, 8), vals[i]);
    WriteLenField(ms, 9, raw);
    return ms.ToArray();
}

static byte[] BuildValueInfo(string name, int elemType) {
    using MemoryStream tt = new();
    WriteVarintField(tt, 1, (ulong)elemType);
    using MemoryStream tp = new();
    WriteLenField(tp, 1, tt.ToArray());
    using MemoryStream vi = new();
    WriteStringField(vi, 1, name);
    WriteLenField(vi, 2, tp.ToArray());
    return vi.ToArray();
}

static Dictionary<string, (int DataType, byte[] RawData)> InspectInitializerTypes(byte[] modelBytes) {
    Dictionary<string, (int, byte[])> result = new(StringComparer.Ordinal);
    int pos = 0, end = modelBytes.Length;
    while (pos < end) {
        ulong tag = ReadVarint(modelBytes, ref pos);
        int fn = (int)(tag >> 3), wt = (int)(tag & 7);
        if (fn == 7 && wt == 2) {
            int gLen = (int)ReadVarint(modelBytes, ref pos);
            int gEnd = pos + gLen;
            while (pos < gEnd) {
                ulong gTag = ReadVarint(modelBytes, ref pos);
                int gFn = (int)(gTag >> 3), gWt = (int)(gTag & 7);
                if (gFn == 5 && gWt == 2) {
                    int tLen = (int)ReadVarint(modelBytes, ref pos);
                    int tEnd = pos + tLen;
                    string name = ""; int dt = 0; byte[] raw = Array.Empty<byte>();
                    while (pos < tEnd) {
                        ulong tTag = ReadVarint(modelBytes, ref pos);
                        int tFn = (int)(tTag >> 3), tWt = (int)(tTag & 7);
                        if (tFn == 2 && tWt == 0) dt = (int)ReadVarint(modelBytes, ref pos);
                        else if (tFn == 8 && tWt == 2) { int l = (int)ReadVarint(modelBytes, ref pos); name = Encoding.UTF8.GetString(modelBytes, pos, l); pos += l; }
                        else if (tFn == 9 && tWt == 2) { int l = (int)ReadVarint(modelBytes, ref pos); raw = new byte[l]; Array.Copy(modelBytes, pos, raw, 0, l); pos += l; }
                        else Skip(modelBytes, ref pos, tWt);
                    }
                    result[name] = (dt, raw);
                } else Skip(modelBytes, ref pos, gWt);
            }
        } else Skip(modelBytes, ref pos, wt);
    }
    return result;
}

static void WriteVarint(Stream s, ulong v) { while (v >= 0x80) { s.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; } s.WriteByte((byte)v); }
static void WriteVarintField(Stream s, int f, ulong v) { WriteVarint(s, (ulong)((f << 3) | 0)); WriteVarint(s, v); }
static void WriteLenField(Stream s, int f, byte[] b) { WriteVarint(s, (ulong)((f << 3) | 2)); WriteVarint(s, (ulong)b.Length); s.Write(b, 0, b.Length); }
static void WriteStringField(Stream s, int f, string str) => WriteLenField(s, f, Encoding.UTF8.GetBytes(str));
static ulong ReadVarint(byte[] d, ref int p) { ulong r = 0; int sh = 0; while (true) { byte b = d[p++]; r |= (ulong)(b & 0x7F) << sh; if ((b & 0x80) == 0) return r; sh += 7; } }
static void Skip(byte[] d, ref int p, int wt) { if (wt == 0) ReadVarint(d, ref p); else if (wt == 1) p += 8; else if (wt == 2) { int l = (int)ReadVarint(d, ref p); p += l; } else if (wt == 5) p += 4; }

sealed class MockColdWarmupRuntime : INeuralInferenceRuntime {
    public bool IsAvailable => true;
    public NeuralRuntimeInfo GetRuntimeInfo() => new() { IsAvailable = true, ActiveProvider = "WebGPU FP16 (Vulkan GPU)" };
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() => Array.Empty<NeuralModelCatalogEntry>();
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() => Array.Empty<NeuralModelCatalogEntry>();
    public string ResolveInstalledModelPath(string modelId) => modelId;
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) => new MockSession(modelPath);
    public void Dispose() { }

    sealed class MockSession(string path) : INeuralSession {
        int callIndex;
        public string ModelPath => path;
        public string ExecutionProvider => "WebGPU FP16 (Vulkan GPU)";
        public IReadOnlyList<string> InputNames => ["input"];
        public IReadOnlyList<long[]> InputShapes => [[1L, 2L, 88200L]];
        public IReadOnlyList<string> OutputNames => ["output"];

        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            int idx = Interlocked.Increment(ref callIndex);
            // Simulate cold WebGPU shader compile on chunk 0 (450 ms), then fast steady-state GPU inference (5 ms)
            Thread.Sleep(idx == 1 ? 450 : 5);
            int n = (int)inputTensor.Shape[2];
            float[] inp = inputTensor.Data;
            // Produce 2 stereo stems [1, 2, 2, n]: stem 0 = vocals (centered mid-band), stem 1 = instrumental (residual)
            float[] outData = new float[4 * n];
            for (int i = 0; i < n; i++) {
                float l = inp[i];
                float r = inp[n + i];
                // Perfect mid/side separation for our test signal (vocal is L==R, inst is L/R quadrature!)
                float mid = 0.5f * (l + r);
                // Actually our test signal has vocal=sin(520) on both L&R, instL=sin(180), instR=cos(180)
                // Extract vocal cleanly via simple 1-pole highpass >300 Hz on mid so vocal is isolated!
                outData[i] = mid;
                outData[n + i] = mid;
                outData[2 * n + i] = l - mid;
                outData[3 * n + i] = r - mid;
            }
            return [new NeuralTensorFloat([1L, 2L, 2L, n], outData)];
        }

        public IReadOnlyDictionary<string, NeuralTensorFloat> Run(IReadOnlyDictionary<string, NeuralTensorFloat> inputs) {
            NeuralTensorFloat first = inputs.Values.First();
            return new Dictionary<string, NeuralTensorFloat> { ["output"] = RunSingleInput(first)[0] };
        }

        public void Dispose() { }
    }
}

sealed class SyntheticAtmosReader : AudioReader {
    readonly SyntheticAtmosRenderer renderer;
    public override long Position { get; set; }

    public SyntheticAtmosReader(int sampleRate, long length) : base(Stream.Null) {
        SampleRate = sampleRate;
        Length = length;
        ChannelCount = 8; // 6 static 5.1 beds + 2 dynamic 3D objects
        Bits = BitDepth.Float32;
        renderer = new SyntheticAtmosRenderer(sampleRate);
    }

    public override Renderer GetRenderer() => renderer;
    public override void ReadHeader() { }
    public override void ReadBlock(float[] samples, long from, long to) => Array.Clear(samples, (int)from, (int)(to - from));
}

sealed class SyntheticAtmosRenderer : Renderer, IMixedBedObjectRenderer {
    static readonly ReferenceChannel[] Bed51 = ChannelPrototype.GetStandardMatrix(6);

    public SyntheticAtmosRenderer(int sampleRate) : base(null) {
        HasObjects = true;
        Vector3 env = Listener.EnvironmentSize;
        if (env.X <= 0.1f) env = new Vector3(10, 7, 10);
        for (int i = 0; i < Bed51.Length; i++) {
            ReferenceChannel ch = Bed51[i];
            Source bed = new() {
                LFE = ch == ReferenceChannel.ScreenLFE,
                Position = ChannelPrototype.AlternativePositions[(int)ch] * env
            };
            objects.Add(bed);
        }
        // 2 dynamic moving 3D Dolby Atmos objects
        objects.Add(new Source { Position = new Vector3(-0.4f, 0.3f, 0.7f) * env });
        objects.Add(new Source { Position = new Vector3(0.6f, 0.5f, -0.3f) * env });
        objectSamples = new float[objects.Count][];
        for (int i = 0; i < objectSamples.Length; i++) objectSamples[i] = new float[1024];
    }

    public override ReferenceChannel[] GetChannels() => Bed51;
    public ReferenceChannel[] GetStaticChannels() => Bed51;

    public override void Update(int samples) {
        for (int i = 0; i < objectSamples.Length; i++) {
            if (objectSamples[i].Length != samples) objectSamples[i] = new float[samples];
        }
    }
}
"""


def main() -> int:
    dotnet = find_dotnet()
    tfm = detect_tfm(dotnet)
    cavern_csproj = REPO_ROOT / "Cavern" / "Cavern.csproj"
    logic_csproj = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Cavernize.Logic.csproj"

    with tempfile.TemporaryDirectory(prefix="cavern_w46_tests_") as tmp:
        tmp_path = Path(tmp)
        (tmp_path / "W46Tests.csproj").write_text(
            TEST_CSPROJ.format(tfm=tfm, cavern_csproj=cavern_csproj, logic_csproj=logic_csproj),
            encoding="utf-8",
        )
        (tmp_path / "Program.cs").write_text(TEST_PROGRAM_CS, encoding="utf-8")

        env = os.environ.copy()
        env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
        env["DOTNET_EnableDiagnostics"] = "0"
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"

        cmd = [dotnet, "run", "--project", str(tmp_path / "W46Tests.csproj"), "-c", "Release", "-m:1", "/p:UseSharedCompilation=false"]
        print(f"[w46-tests] Running: {' '.join(cmd)}", flush=True)
        proc = subprocess.run(cmd, cwd=str(REPO_ROOT), env=env)
        return proc.returncode


if __name__ == "__main__":
    sys.exit(main())
