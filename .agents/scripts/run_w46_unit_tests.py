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

import argparse
import fcntl
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CACHE_DIR = Path("/tmp/cavern_w46_tests_cache")
LOCK_PATH = Path("/tmp/cavern_proot_dotnet.lock")
FINGERPRINT_SRC_DIRS = (
    REPO_ROOT / "Cavern",
    REPO_ROOT / "Cavern.Format",
    REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic",
)


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
    <Compile Include="{ui_coordinator_cs}" Link="UiStateCoordinator.cs" />
  </ItemGroup>
</Project>
"""

TEST_PROGRAM_CS = r"""
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Text;
using Cavern;
using Cavern.CavernSettings;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Decoders;
using Cavern.Format.Environment;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.BaseClasses;
using Cavern.Format.Transcoders;
using Cavern.Format.Utilities;
using Cavern.Neural;
using Cavern.ProAudio;
using Cavern.Remapping;
using Cavern.Rendering;
using Cavern.Spatial;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Complex = Cavern.Utilities.Complex;
using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;
using CavernizeAndroid.Services;

string testFilter = Environment.GetEnvironmentVariable("W46_TEST_FILTER") ?? "";
int passed = 0;
void Assert(bool cond, string msg) {
    if (!cond) {
        throw new Exception("ASSERTION FAILED: " + msg);
    }
}

void RunTest(string name, Action test) {
    if (!string.IsNullOrEmpty(testFilter) && !name.Contains(testFilter, StringComparison.OrdinalIgnoreCase)) {
        return;
    }
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

// ---------- Test 12: W49 Sample-Rate Fractional Accumulator, 3-Min Drift (< 2 ms) & HeardSeconds ----------
RunTest("T12_W49_ClockDrift_3Min_44k48k_And_HeardSeconds", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();

    // Part A: Direct Source + Listener 3-minute (180s) synthetic render @ 44.1k->48k & 48k->44.1k
    Listener.HeadphoneVirtualizer = false;
    Listener.ReplaceChannels(2);
    {
        Listener l1 = new(false) { SampleRate = 48000, UpdateRate = 256 };
        Clip c1 = new(new float[44100 * 182], 1, 44100);
        Source s1 = new() { Clip = c1, SpatialBlend = 0f, Volume = 1f };
        l1.AttachSource(s1);
        for (int b = 0; b < 8437; b++) l1.Render(4);
        l1.Render(2); // 8437*4 + 2 = 33750 blocks * 256 = 8,640,000 samples = 180.000s @ 48 kHz
        double expected1 = 33750.0 * 256.0 * (44100.0 / 48000.0);
        double driftMs1 = Math.Abs(s1.TimeSamples - expected1) * 1000.0 / 44100.0;
        Assert(driftMs1 < 2.0, $"Direct Source 44.1k->48k 3-min drift {driftMs1:0.4} ms >= 2 ms");
        Assert(s1.CumulativeSourceSamples == 7938000L, $"Expected exact 7,938,000 consumed samples, got {s1.CumulativeSourceSamples}");
    }
    {
        Listener l2 = new(false) { SampleRate = 44100, UpdateRate = 256 };
        Clip c2 = new(new float[48000 * 182], 1, 48000);
        Source s2 = new() { Clip = c2, SpatialBlend = 0f, Volume = 1f };
        l2.AttachSource(s2);
        for (int b = 0; b < 7752; b++) l2.Render(4); // 31008 blocks * 256 = 7,938,048 samples @ 44.1 kHz
        double expected2 = 31008.0 * 256.0 * (48000.0 / 44100.0);
        double driftMs2 = Math.Abs(s2.TimeSamples - expected2) * 1000.0 / 48000.0;
        Assert(driftMs2 < 2.0, $"Direct Source 48k->44.1k 3-min drift {driftMs2:0.4} ms >= 2 ms");
    }

    // Part B1: Full RealtimePlayerEngine 3-minute render @ 44.1 kHz track -> 48 kHz HRIR virtualizer
    float[] renderBuf = new float[1024 * 2];
    using (SyntheticClockReader r44 = new(44100, 44100L * 200L))
    using (CavernizeTrack t44 = new(r44, Codec.PCM_LE, 0))
    using (RealtimePlayerEngine engine = new()) {
        engine.HeadphoneVirtualizer = true;
        engine.Load(t44, new UpmixingSettings(false), new RenderingSettings());
        engine.IsPlaying = true;
        for (int b = 0; b < 8437; b++) {
            int got = engine.RenderNextInterleavedBlock(renderBuf, 1024);
            Assert(got == 1024, $"Expected 1024 frames, got {got}");
        }
        int rem = engine.RenderNextInterleavedBlock(renderBuf, 512);
        Assert(rem == 512, $"Expected 512 frames, got {rem}");

        double engineDriftMs = Math.Abs(engine.CurrentSeconds - 180.0) * 1000.0;
        double decoderDriftMs = Math.Abs((r44.ClockRenderer.TotalDecodedSamples / 44100.0) - 180.0) * 1000.0;
        Assert(engineDriftMs < 2.0, $"Engine 44.1k->48k 3-min drift {engineDriftMs:0.4} ms >= 2 ms");
        Assert(decoderDriftMs < 2.0, $"Decoder 44.1k->48k 3-min drift {decoderDriftMs:0.4} ms >= 2 ms");
    }

    // Part B2: Full RealtimePlayerEngine 3-minute render @ 48 kHz track -> 44.1 kHz hardware override w/ HeadphoneVirtualizer=true
    using (SyntheticClockReader r48 = new(48000, 48000L * 200L))
    using (CavernizeTrack t48 = new(r48, Codec.PCM_LE, 0))
    using (RealtimePlayerEngine engine = new()) {
        engine.HeadphoneVirtualizer = true;
        engine.SetPreferredHardwareSampleRate(44100);
        engine.Load(t48, new UpmixingSettings(false), new RenderingSettings());
        Assert(engine.OutputSampleRate == 44100, $"Expected OutputSampleRate=44100 under HRIR hardware override, got {engine.OutputSampleRate}");
        Assert(Cavern.Virtualizer.VirtualizerFilter.FilterSampleRate == 48000, "HRIR FilterSampleRate must remain 48000 Hz.");
        engine.IsPlaying = true;
        for (int b = 0; b < 7752; b++) {
            int got = engine.RenderNextInterleavedBlock(renderBuf, 1024);
            Assert(got == 1024, $"Expected 1024 frames, got {got}");
        }
        double expectedSec = (7752.0 * 1024.0) / 44100.0;
        double engineDriftMs = Math.Abs(engine.CurrentSeconds - expectedSec) * 1000.0;
        double decoderDriftMs = Math.Abs((r48.ClockRenderer.TotalDecodedSamples / 48000.0) - expectedSec) * 1000.0;
        Assert(engineDriftMs < 2.0, $"Engine 48k->44.1k 3-min drift {engineDriftMs:0.4} ms >= 2 ms");
        Assert(decoderDriftMs < 2.0, $"Decoder 48k->44.1k 3-min drift {decoderDriftMs:0.4} ms >= 2 ms");
    }

    // Part C: Upmixer + StreamMaster alternating 235/236 PitchedUpdateRate stability (44.1k -> 48k)
    using (SyntheticClockReader rUpmix = new(44100, 44100L * 10L))
    using (CavernizeTrack tUpmix = new(rUpmix, Codec.PCM_LE, 0))
    using (RealtimePlayerEngine engine = new()) {
        engine.HeadphoneVirtualizer = true;
        engine.Load(tUpmix, new UpmixingSettings(true), new RenderingSettings());
        engine.IsPlaying = true;
        for (int b = 0; b < 94; b++) {
            int got = engine.RenderNextInterleavedBlock(renderBuf, 1024);
            Assert(got == 1024, $"Expected 1024 frames under upmixer, got {got}");
        }
        double expectedSec = (94.0 * 1024.0) / 48000.0;
        double upmixDriftMs = Math.Abs((rUpmix.ClockRenderer.TotalDecodedSamples / 44100.0) - expectedSec) * 1000.0;
        Assert(upmixDriftMs < 2.0, $"Upmixer 44.1k->48k drift {upmixDriftMs:0.4} ms >= 2 ms");
    }

    // Part D: HeardSeconds latency compensation, subBlockStaging accounting, & Seek flush hook
    using (SyntheticClockReader rLat = new(48000, 48000L * 60L))
    using (CavernizeTrack tLat = new(rLat, Codec.PCM_LE, 0))
    using (RealtimePlayerEngine engine = new()) {
        int flushCount = 0;
        engine.HeadphoneVirtualizer = true;
        engine.RegisterOutputFlushCallback(() => flushCount++);
        engine.Load(tLat, new UpmixingSettings(false), new RenderingSettings());
        Assert(flushCount == 1, $"Expected Load() to invoke flush callback once, got {flushCount}");

        engine.Seek(10.0);
        Assert(flushCount == 2, $"Expected Seek() to invoke flush callback, got {flushCount}");
        Assert(engine.OutputPipelineLatencySeconds == 0.0, "Seek() must reset OutputPipelineLatencySeconds to 0.");
        Assert(Math.Abs(engine.HeardSeconds - 10.0) < 0.0005, $"Expected HeardSeconds=10.0 after Seek, got {engine.HeardSeconds}");

        engine.IsPlaying = true;
        int got128 = engine.RenderNextInterleavedBlock(renderBuf, 128);
        Assert(got128 == 128, $"Expected 128 sub-block frames, got {got128}");
        engine.OutputPipelineLatencySeconds = 0.150;
        double expectedHeard = 10.0 + (256.0 - 128.0) / 48000.0 - 0.150;
        double heardErrMs = Math.Abs(engine.HeardSeconds - expectedHeard) * 1000.0;
        Assert(heardErrMs < 0.1, $"HeardSeconds latency error {heardErrMs:0.4} ms >= 0.1 ms");

        engine.Seek(0.02);
        engine.OutputPipelineLatencySeconds = 0.250;
        Assert(engine.HeardSeconds == 0.0, $"Expected HeardSeconds clamped to 0.0, got {engine.HeardSeconds}");

        engine.Seek(25.0);
        Assert(flushCount == 4, $"Expected flushCount=4 after seeks, got {flushCount}");
        Assert(engine.OutputPipelineLatencySeconds == 0.0, "Seek() must reset OutputPipelineLatencySeconds.");
        int postSeekGot = engine.RenderNextInterleavedBlock(renderBuf, 128);
        Assert(postSeekGot == 128, $"Expected 128 frames post-seek, got {postSeekGot}");
        double expectedPostSeekCur = 25.0 + 256.0 / 48000.0;
        Assert(Math.Abs(engine.CurrentSeconds - expectedPostSeekCur) < 0.0005, $"Post-seek subBlockStaging was not cleared cleanly (CurrentSeconds={engine.CurrentSeconds})");
    }
});

// ---------- Test 13: W50 Scarlett Gen 4 FCP Tables, FW Extraction & PreAmp Anti-Double Bypass ----------
RunTest("T13_W50_ScarlettGen4_FcpTables_And_PreAmpAntiDouble", () => {
    Assert((int)ProAudioMicSource.PhoneMic == 0, $"ProAudioMicSource.PhoneMic must be 0, got {(int)ProAudioMicSource.PhoneMic}");

    Assert(Scarlett2Protocol.TryResolveGen4ConfigTable(0x8219, 2417, out ScarlettGen4ConfigTable t2i2_2417), "2i2 Gen4 fw=2417 FCP table must resolve.");
    Assert(t2i2_2417.ParamBufAddr == 0xfcu && t2i2_2417.DirectMonitorGain.Offset == 0x2a4u && t2i2_2417.AirSwitch.Offset == 0x3eu,
        $"Unexpected 2i2 Gen4 fw=2417 offsets: param=0x{t2i2_2417.ParamBufAddr:X}, dmGain=0x{t2i2_2417.DirectMonitorGain.Offset:X}");
    Assert(Scarlett2Protocol.TryResolveGen4ConfigTable(0x8219, 2115, out ScarlettGen4ConfigTable t2i2_2115), "2i2 Gen4 fw=2115 FCP table must resolve.");
    Assert(t2i2_2115.DirectMonitorGain.Offset == 0x2a0u, $"Expected pre-2417 DirectMonitorGain 0x2a0, got 0x{t2i2_2115.DirectMonitorGain.Offset:X}");
    Assert(!Scarlett2Protocol.TryResolveGen4ConfigTable(0x8219, 2000, out _), "Pre-2115 2i2 Gen4 firmware must return false.");

    Assert(Scarlett2Protocol.TryResolveGen4ConfigTable(0x8218, 2200, out ScarlettGen4ConfigTable tSolo), "Solo Gen4 FCP table must resolve.");
    Assert(tSolo.ParamBufAddr == 0xd8u && tSolo.AirSwitch.Offset == 0x3eu && !tSolo.AutoGainSwitch.IsValid, "Unexpected Solo Gen4 table.");

    Assert(Scarlett2Protocol.TryResolveGen4ConfigTable(0x821A, 2200, out ScarlettGen4ConfigTable t4i4), "4i4 Gen4 FCP table must resolve.");
    Assert(t4i4.ParamBufAddr == 0x130u && t4i4.AirSwitch.Offset == 0x50u && !t4i4.DirectMonitor.IsValid, "Unexpected 4i4 Gen4 table.");

    byte[] init2Rx = Scarlett2Protocol.BuildCommandPacket(Scarlett2Protocol.CmdInit2, 1, new byte[12]);
    BinaryPrimitives.WriteUInt32LittleEndian(init2Rx.AsSpan(Scarlett2Protocol.PacketHeaderBytes + 8, 4), 2417u);
    Assert(Scarlett2Protocol.TryExtractFirmwareVersion(init2Rx, init2Rx.Length, out int extractedFw) && extractedFw == 2417,
        $"Expected extracted fw=2417, got {extractedFw}");
    byte[] badRx = (byte[])init2Rx.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(badRx.AsSpan(8, 4), 1u);
    Assert(!Scarlett2Protocol.TryExtractFirmwareVersion(badRx, badRx.Length, out _), "Non-zero error packet must fail FW extraction.");
    Assert(!Scarlett2Protocol.TryExtractFirmwareVersion(init2Rx, 20, out _), "Truncated packet must fail FW extraction.");

    float MeasureDspRms(HardwarePreAmpStateSnapshot snap, float freqHz, float amp) {
        LiveVocalChainDsp dsp = new(48000);
        VocalMixDnaProfile dna = VocalMixDnaProfile.CreateStudioDefault();
        dna.HarmonicWarmth = 1f;
        dna.ReverbWetMix = 0f;
        dna.DelayWetMix = 0f;
        VocalMacroSettings macros = new() {
            Enabled = true,
            MixMatchPct = 100f,
            WarmthPct = 100f,
            DynamicsPct = 0f,
            ReverbPct = 0f,
            EnableNoiseGate = false,
            EnableDeEsser = false,
            EnableSidechainPocketGlue = false
        };
        float[] matchDeltas = [0f, 0f, 0f, 0f, 0f, 4f, 4f, 0f];
        dsp.Configure(48000, dna, macros, matchDeltas, snap);

        int frames = 1024;
        float[] buf = new float[frames * 2];
        for (int i = 0; i < frames; i++) {
            float v = amp * MathF.Sin(2f * MathF.PI * freqHz * i / 48000f);
            buf[i * 2] = v;
            buf[i * 2 + 1] = v;
        }
        dsp.ProcessStereoBlockZeroGc(buf, 0, frames);
        double sumSq = 0.0;
        for (int i = frames / 2; i < frames; i++) {
            float v = buf[i * 2];
            sumSq += v * v;
        }
        return (float)Math.Sqrt(sumSq / (frames - frames / 2));
    }

    HardwarePreAmpStateSnapshot snapVerifiedUsb = new(ScarlettAirMode.PresenceAndDrive, driveActive: true, micSource: 1, isHardwareVerified: true);
    HardwarePreAmpStateSnapshot snapUnverifiedUsb = new(ScarlettAirMode.PresenceAndDrive, driveActive: true, micSource: 1, isHardwareVerified: false);
    HardwarePreAmpStateSnapshot snapVerifiedPhone = new(ScarlettAirMode.PresenceAndDrive, driveActive: true, micSource: 0, isHardwareVerified: true);

    float hfVerifiedUsb = MeasureDspRms(snapVerifiedUsb, 10000f, 0.02f);
    float hfUnverified = MeasureDspRms(snapUnverifiedUsb, 10000f, 0.02f);
    float hfPhone = MeasureDspRms(snapVerifiedPhone, 10000f, 0.02f);
    double airBypassDeltaDb = 20.0 * Math.Log10((hfUnverified + 1e-9) / (hfVerifiedUsb + 1e-9));
    Assert(MathF.Abs(hfUnverified - hfPhone) < 1e-4f, "PhoneMic (MicSource=0) and unverified USB must NOT bypass Air shelf.");
    Assert(airBypassDeltaDb > 1.5 && airBypassDeltaDb < 3.5, $"Expected ~2.5 dB Air anti-double bypass only when verified USB, got {airBypassDeltaDb:0.2} dB.");

    float driveVerifiedUsb = MeasureDspRms(snapVerifiedUsb, 500f, 0.70f);
    float driveUnverified = MeasureDspRms(snapUnverifiedUsb, 500f, 0.70f);
    float drivePhone = MeasureDspRms(snapVerifiedPhone, 500f, 0.70f);
    Assert(MathF.Abs(driveUnverified - drivePhone) < 1e-4f, "PhoneMic and unverified USB must still apply software Drive.");
    Assert(driveVerifiedUsb > 0.42f && driveUnverified < 0.28f,
        $"Expected Drive bypassed (linear ~0.495) only on verified USB, got verified={driveVerifiedUsb:0.000}, unverified={driveUnverified:0.000}");
});

// ---------- Test 14: W51 AutoEq ATH-M40x Bass Preservation & HarmanOeToJm1 Tilt ----------
RunTest("T14_W51_AutoEq_AthM40x_BassPreservation", () => {
    AutoEqHeadphoneProfile m40x = new() {
        Id = "oratory1990_over_ear_audio_technica_ath_m40x",
        Name = "Audio-Technica ATH-M40x",
        FormFactor = HeadphoneFormFactor.OverEar,
        SourceTarget = AutoEqSourceTarget.HarmanOverEar2018,
        ExplicitPreampDb = -5.6,
        HasExplicitPreamp = true,
        Bands = [
            new ParametricBand(ParametricFilterType.LowShelf, 105.0, 17.6, 0.7),
            new ParametricBand(ParametricFilterType.Peaking, 48.0, -16.9, 0.29),
            new ParametricBand(ParametricFilterType.Peaking, 348.0, 6.0, 0.87),
            new ParametricBand(ParametricFilterType.Peaking, 5150.0, 3.3, 4.1),
            new ParametricBand(ParametricFilterType.Peaking, 2979.0, 1.8, 1.99),
            new ParametricBand(ParametricFilterType.HighShelf, 10000.0, -6.9, 0.7),
            new ParametricBand(ParametricFilterType.Peaking, 1378.0, -1.4, 2.13),
            new ParametricBand(ParametricFilterType.Peaking, 796.0, 0.6, 1.67),
            new ParametricBand(ParametricFilterType.Peaking, 6306.0, 1.6, 5.1),
            new ParametricBand(ParametricFilterType.Peaking, 2225.0, 0.8, 3.7)
        ]
    };

    using AutoEqPostProcessingStage stage = new() { UserEnabled = true };
    stage.Configure(48000, 2);
    stage.ApplyConfiguration(m40x, AutoEqNeutralTarget.NativeAutoEqProfile, BinauralCompensationMode.DiffuseFieldCompensatedHrir);
    Assert(stage.IsEnabled && !stage.DoubleEqGuardActive, "NativeAutoEqProfile + DFC must be enabled without Raw double-EQ guard.");

    double nat1k = stage.EvaluateMagnitudeDb(1000.0, includePreamp: false);
    double nat80 = stage.EvaluateMagnitudeDb(80.0, includePreamp: false) - nat1k;
    double nat100 = stage.EvaluateMagnitudeDb(100.0, includePreamp: false) - nat1k;
    double nat120 = stage.EvaluateMagnitudeDb(120.0, includePreamp: false) - nat1k;
    double nat5k = stage.EvaluateMagnitudeDb(5000.0, includePreamp: false) - nat1k;
    Assert(nat80 > -4.5 && nat80 < 9.0, $"ATH-M40x 80 Hz delta vs 1 kHz out of bounds: {nat80:0.2} dB");
    Assert(nat100 > -4.5 && nat100 < 9.0, $"ATH-M40x 100 Hz delta vs 1 kHz out of bounds: {nat100:0.2} dB");
    Assert(nat120 > -4.5 && nat120 < 9.0, $"ATH-M40x 120 Hz delta vs 1 kHz out of bounds: {nat120:0.2} dB");
    Assert(stage.EffectivePreampDb >= -9.0, $"Sub-28Hz resonance damp should protect preamp headroom, got {stage.EffectivePreampDb:0.2} dB");

    stage.ApplyConfiguration(m40x, AutoEqNeutralTarget.DiffuseFieldNeutral, BinauralCompensationMode.DiffuseFieldCompensatedHrir);
    double df1k = stage.EvaluateMagnitudeDb(1000.0, includePreamp: false);
    double df80 = stage.EvaluateMagnitudeDb(80.0, includePreamp: false) - df1k;
    double df100 = stage.EvaluateMagnitudeDb(100.0, includePreamp: false) - df1k;
    Assert(nat80 - df80 > 5.0 && nat100 - df100 > 4.5, $"NativeAutoEqProfile must preserve >5 dB @80Hz / >4.5 dB @100Hz more bass than DiffuseFieldNeutral (nat={nat100:0.2}, df={df100:0.2}).");

    stage.ApplyConfiguration(m40x, AutoEqNeutralTarget.Bk5128Jm1Neutral, BinauralCompensationMode.DiffuseFieldCompensatedHrir);
    double jm1Mid1k = stage.EvaluateMagnitudeDb(1000.0, includePreamp: false);
    double jm1Bass100 = stage.EvaluateMagnitudeDb(100.0, includePreamp: false) - jm1Mid1k;
    double jm1_5k = stage.EvaluateMagnitudeDb(5000.0, includePreamp: false) - jm1Mid1k;
    Assert(jm1Bass100 > -4.0 && jm1Bass100 - df100 > 4.0, $"HarmanOeToJm1 must preserve natural bass ({jm1Bass100:0.2} dB vs DF {df100:0.2} dB).");
    Assert(jm1_5k < nat5k - 2.0, $"HarmanOeToJm1 must apply gentle studio in-room upper-mid/treble tilt ({jm1_5k:0.2} vs native {nat5k:0.2}).");
});

// ---------- Test 15: W51 3D Upmix Heights, Solo Preservation Across Rebuilds & Dynamic/Static Reset ----------
RunTest("T15_W51_3DUpmix_SoloPreservation_And_Reset", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();
    int sr = 48000;

    // Part A & B: CavernizeUpmixer height Y > 0 (IsBed=false), Solo survival across Seek & Upmix mode rebuilds
    using (SyntheticClockReader reader = new(sr, sr * 10L))
    using (CavernizeTrack track = new(reader, Codec.PCM_LE, 0))
    using (RealtimePlayerEngine engine = new()) {
        int rebuildCount = 0;
        engine.SourcesRebuilt += () => rebuildCount++;
        engine.HeadphoneVirtualizer = true;
        UpmixingSettings upmix = new(true) { MatrixUpmixing = false, Cavernize = true, ParametricStft = true };
        engine.Load(track, upmix, new RenderingSettings());
        Assert(rebuildCount == 1, $"Expected Load() to fire SourcesRebuilt once, got {rebuildCount}");
        Assert(engine.SourceItems.Count == 4, $"Expected 4 sources (2 ground + 2 height), got {engine.SourceItems.Count}");
        TrackSourceItem heightL = engine.SourceItems[2];
        Assert(!heightL.IsBed && heightL.Layer == SourceLayer.VirtualHeight && heightL.NormalizedPosition.Y > 0.5f,
            $"Expected VirtualHeight Y>0.5, got IsBed={heightL.IsBed}, Layer={heightL.Layer}, Y={heightL.NormalizedPosition.Y:0.2}");

        // Set Solo on heightL, Mute on FL, CustomPin on heightR
        heightL.IsSolo = true;
        engine.SourceItems[0].IsMuted = true;
        engine.SetSourceCustomPosition(engine.SourceItems[3], new Vector3(0.25f, 0.90f, -0.35f));

        // 1) Seek rebuilds Listener & Sources via ConfigureListener
        engine.Seek(1.0);
        Assert(rebuildCount == 2, $"Expected Seek() to fire SourcesRebuilt, got {rebuildCount}");
        Assert(engine.SourceItems.Count == 4 && engine.SourceItems[2].IsSolo, "Solo on heightL must survive Seek() ConfigureListener rebuild.");
        Assert(engine.SourceItems[0].IsMuted, "Mute on FL must survive Seek() ConfigureListener rebuild.");
        Assert(engine.SourceItems[3].HasCustomPosition && Vector3.Distance(engine.SourceItems[3].CustomNormalizedPosition, new Vector3(0.25f, 0.90f, -0.35f)) < 1e-4f,
            "CustomPosition on heightR must survive Seek() ConfigureListener rebuild.");

        // 2) Expand upmixer (4 -> 15 channels) and verify Solo survives & does NOT get muted by mismatched/new sources
        engine.ApplyUpmixingSettings(new UpmixingSettings(true) { MatrixUpmixing = true, Cavernize = true, ParametricStft = true });
        Assert(rebuildCount == 3, $"Expected ApplyUpmixingSettings to fire SourcesRebuilt, got {rebuildCount}");
        Assert(engine.SourceItems.Count > 4, $"Expected expanded matrix+cavernize sources, got {engine.SourceItems.Count}");
        Assert(engine.SourceItems.Count(x => x.IsSolo) == 1, $"Expected exactly 1 Solo item preserved after upmix expansion, got {engine.SourceItems.Count(x => x.IsSolo)}");
        Assert(engine.SourceItems.First(x => x.IsSolo).Layer == SourceLayer.VirtualHeight, "Preserved Solo target must remain on VirtualHeight layer after expansion.");

        // Verify audio block rendering applies Solo cleanly and static pin reset works
        engine.IsPlaying = true;
        float[] outBuf = new float[1024 * 2];
        int written = engine.RenderNextInterleavedBlock(outBuf, 1024);
        Assert(written == 1024, $"Expected 1024 frames rendered, got {written}");
        TrackSourceItem pinnedHeight = engine.SourceItems.First(x => x.HasCustomPosition);
        engine.ClearSourceCustomPosition(pinnedHeight);
        Assert(!pinnedHeight.HasCustomPosition, "Static height ResetCustomPosition must clear HasCustomPosition.");
        Assert(Vector3.Distance(pinnedHeight.NormalizedPosition, pinnedHeight.DefaultNormalizedPosition) < 1e-4f,
            "Static source ResetCustomPosition must restore DefaultNormalizedPosition.");
    }

    // Part C: Dynamic Atmos Object ResetCustomPosition must resume live trajectory without snapping to t=0 DefaultPosition
    using (SyntheticAtmosReader atmosReader = new(sr, sr * 10L))
    using (CavernizeTrack atmosTrack = new(atmosReader, Codec.EnhancedAC3, 0))
    using (RealtimePlayerEngine engine = new()) {
        engine.HeadphoneVirtualizer = true;
        engine.Load(atmosTrack, new UpmixingSettings(true) { MatrixUpmixing = true, Cavernize = true, AtmosAware = true }, new RenderingSettings());
        TrackSourceItem dynObj = engine.SourceItems.First(x => x.HasDynamicTrajectory);
        Vector3 t0Default = dynObj.DefaultNormalizedPosition;

        Vector3 liveTrajPos = new(0.82f, 0.66f, -0.45f);
        atmosTrack.Renderer.Objects[dynObj.Index].Position = liveTrajPos * Listener.EnvironmentSize;
        engine.SetSourceCustomPosition(dynObj, new Vector3(-0.88f, 0.12f, 0.85f));
        Assert(dynObj.HasCustomPosition, "Dynamic object should be custom pinned.");

        engine.IsPlaying = true;
        float[] buf = new float[1024 * 2];
        engine.RenderNextInterleavedBlock(buf, 1024);

        Vector3 newerTrajPos = new(0.18f, 0.92f, 0.30f);
        atmosTrack.Renderer.Objects[dynObj.Index].Position = newerTrajPos * Listener.EnvironmentSize;
        engine.ResetCustomPosition(dynObj);

        Assert(!dynObj.HasCustomPosition, "ResetCustomPosition must clear HasCustomPosition on dynamic Atmos object.");
        Assert(!atmosTrack.Renderer.Objects[dynObj.Index].PositionLocked, "Dynamic Atmos object PositionLocked must be false after reset.");
        Assert(Vector3.Distance(dynObj.NormalizedPosition, t0Default) > 0.25f,
            $"Dynamic object reset must NOT snap back to stale t=0 DefaultPosition ({t0Default}), got {dynObj.NormalizedPosition}");
        Assert(Vector3.Distance(dynObj.NormalizedPosition, newerTrajPos) < 0.05f,
            $"Expected dynamic object to resume live trajectory ({newerTrajPos}), got {dynObj.NormalizedPosition}");

        // Also verify ResetAllCustomPositions on dynamic object
        engine.SetSourceCustomPosition(dynObj, new Vector3(-0.5f, -0.2f, 0.5f));
        Vector3 latestTraj = new(-0.25f, 0.80f, -0.60f);
        atmosTrack.Renderer.Objects[dynObj.Index].Position = latestTraj * Listener.EnvironmentSize;
        engine.ResetAllCustomPositions();
        Assert(!dynObj.HasCustomPosition && Vector3.Distance(dynObj.NormalizedPosition, latestTraj) < 0.05f,
            "ResetAllCustomPositions must unlock dynamic object to latest trajectory cleanly.");
    }
});

// ---------- Test 16: W51 X-Ray Powermap Spherical Coherence Anti-Spin & Band Mode ----------
RunTest("T16_W51_XRayPowermap_CoherenceAntiSpin", () => {
    SpatialXRayPowermap map = new();
    Vector3 initialDir = map.DominantDirection;

    // 1. Symmetric diffuse field (R = 0 < 0.18): DominantDirection must NOT spin, DominantCoherence < 0.2
    SpatialSourceDescriptor[] diffuse = [
        new() { WorldPositionMeters = new Vector3(-5f, 0f, 5f), RmsLevel = 0.5f, LowBandEnergy = 0.2f, MidBandEnergy = 0.4f, HighBandEnergy = 0.2f },
        new() { WorldPositionMeters = new Vector3(5f, 0f, 5f), RmsLevel = 0.5f, LowBandEnergy = 0.2f, MidBandEnergy = 0.4f, HighBandEnergy = 0.2f },
        new() { WorldPositionMeters = new Vector3(-5f, 0f, -5f), RmsLevel = 0.5f, LowBandEnergy = 0.2f, MidBandEnergy = 0.4f, HighBandEnergy = 0.2f },
        new() { WorldPositionMeters = new Vector3(5f, 0f, -5f), RmsLevel = 0.5f, LowBandEnergy = 0.2f, MidBandEnergy = 0.4f, HighBandEnergy = 0.2f }
    ];
    for (int f = 0; f < 40; f++) {
        map.Update(diffuse, Vector3.Zero, 0.45f, SpatialXRayBandMode.AllBands);
    }
    Assert(map.DominantCoherence < 0.2f, $"Diffuse symmetric field coherence must be < 0.2, got {map.DominantCoherence:0.3}");
    Assert(Vector3.Distance(initialDir, map.DominantDirection) < 1e-5f,
        $"Diffuse symmetric field must hold steady DominantDirection without spinning (moved from {initialDir} to {map.DominantDirection}).");

    // 2. Focused directional source: smooth EMA convergence without 1-frame snap
    Vector3 targetWorld = new(8f, 2f, 0f);
    Vector3 targetDir = Vector3.Normalize(targetWorld);
    SpatialSourceDescriptor[] focused = [
        new() { WorldPositionMeters = targetWorld, RmsLevel = 0.8f, LowBandEnergy = 0.3f, MidBandEnergy = 0.7f, HighBandEnergy = 0.4f }
    ];
    map.Update(focused, Vector3.Zero, 0.45f, SpatialXRayBandMode.AllBands);
    float dot1Frame = Vector3.Dot(map.DominantDirection, targetDir);
    Assert(dot1Frame < 0.55f, $"Focused source must converge smoothly without 1-frame whip (dot after 1 frame = {dot1Frame:0.3}).");

    for (int f = 0; f < 140; f++) {
        map.Update(focused, Vector3.Zero, 0.45f, SpatialXRayBandMode.AllBands);
    }
    Assert(map.DominantCoherence > 0.75f, $"Focused field coherence must exceed 0.75, got {map.DominantCoherence:0.3}");
    Assert(Vector3.Distance(map.DominantDirection, targetDir) < 0.15f,
        $"Focused field must converge smoothly to target {targetDir}, got {map.DominantDirection}");

    // 3. Stateful 3-band crossover continuity across split blocks
    float[] full512 = new float[512];
    for (int i = 0; i < 512; i++) {
        full512[i] = 0.4f * MathF.Sin(2f * MathF.PI * 100f * i / 48000f) + 0.3f * MathF.Sin(2f * MathF.PI * 5000f * i / 48000f);
    }
    float lpL0 = 0f, lpM0 = 0f;
    SpatialXRayPowermap.EstimateThreeBandRms(full512, 48000, ref lpL0, ref lpM0, out float fullLow, out float fullMid, out float fullHigh);
    float[] partA = full512[..256];
    float[] partB = full512[256..];
    float lpL1 = 0f, lpM1 = 0f;
    SpatialXRayPowermap.EstimateThreeBandRms(partA, 48000, ref lpL1, ref lpM1, out float aL, out float aM, out float aH);
    SpatialXRayPowermap.EstimateThreeBandRms(partB, 48000, ref lpL1, ref lpM1, out float bL, out float bM, out float bH);
    Assert(MathF.Abs(lpL0 - lpL1) < 1e-5f && MathF.Abs(lpM0 - lpM1) < 1e-5f,
        "Stateful EstimateThreeBandRms must yield bit-consistent crossover state across split blocks.");
    double splitRmsLow = Math.Sqrt(0.5 * (aL * aL + bL * bL));
    Assert(Math.Abs(splitRmsLow - fullLow) < 1e-4, $"Split low RMS {splitRmsLow:0.5} diverged from single pass {fullLow:0.5}");

    SpatialSceneHub hub = new() { ActiveXRayBandMode = SpatialXRayBandMode.LowOnly };
    Assert(hub.ActiveXRayBandMode == SpatialXRayBandMode.LowOnly, "SpatialSceneHub must retain ActiveXRayBandMode.");
});

// ---------- Test 17: W50 Live Microphone Zero-Lag Sub-Block Staging Bypass ----------
RunTest("T17_W50_LiveMic_ZeroLagSubBlockBypass", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();
    int sr = 48000;
    using SyntheticClockReader reader = new(sr, sr * 10L);
    using CavernizeTrack track = new(reader, Codec.PCM_LE, 0);
    using RealtimePlayerEngine engine = new();
    engine.HeadphoneVirtualizer = true;
    engine.VocalChainBypassed = true;
    engine.Load(track, new UpmixingSettings(false), new RenderingSettings());
    engine.ApplyProAudioConfig(new ProAudioDuplexConfig {
        Enabled = true,
        LiveMicEnabled = true,
        MicRouting = LiveMicRoutingMode.StereoPair,
        MicMonitorLevel = 1f,
        BufferFrames = ProAudioBufferPreset.Frames64
    });
    engine.LiveMicBridge.SetStreamingActive(true, sr);
    engine.IsPlaying = true;

    float[] burst = new float[64 * 2];
    float[] micIn = new float[64 * 2];

    // Burst 1: push +0.25 live mic pulse -> MUST appear in first 64f burst immediately (0 staging lag!)
    Array.Fill(micIn, 0.25f);
    engine.LiveMicBridge.PushCapturedInterleaved(micIn, 0, 64, 2);
    int n1 = engine.RenderNextInterleavedBlock(burst, 64);
    Assert(n1 == 64, $"Expected 64 frames in burst 1, got {n1}");
    float peak1 = burst.Max(MathF.Abs);
    Assert(peak1 > 0.20f, $"Burst 1 must deliver live mic immediately without staging lag (peak={peak1:0.000}).");

    // Burst 2: push 0.0 silence -> backing drains from subBlockStaging with ZERO stale burst-1 mic bleed!
    Array.Clear(micIn, 0, micIn.Length);
    engine.LiveMicBridge.PushCapturedInterleaved(micIn, 0, 64, 2);
    int n2 = engine.RenderNextInterleavedBlock(burst, 64);
    Assert(n2 == 64, $"Expected 64 frames in burst 2, got {n2}");
    float peak2 = burst.Max(MathF.Abs);
    Assert(peak2 < 0.01f, $"Burst 2 must NOT leak staged burst-1 mic audio (peak={peak2:0.000}).");

    // Burst 3: push +0.45 live mic pulse -> MUST appear immediately in burst 3 while subBlockStaging still has backing frames!
    Array.Fill(micIn, 0.45f);
    engine.LiveMicBridge.PushCapturedInterleaved(micIn, 0, 64, 2);
    int n3 = engine.RenderNextInterleavedBlock(burst, 64);
    Assert(n3 == 64, $"Expected 64 frames in burst 3, got {n3}");
    float peak3 = burst.Max(MathF.Abs);
    Assert(peak3 > 0.35f, $"Burst 3 must mix live mic directly even while draining staged backing (peak={peak3:0.000}).");
});

// ---------- Test 18: StudioExportLifecycle & StreamingExportSink ----------
RunTest("T18_StudioExportLifecycle_StreamingExportSink", () => {
    string tempDir = Path.Combine(Path.GetTempPath(), "cavern_t18_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
        // 1. StudioCompressionTier + private <=256KB StreamingExportBufferPool cap
        string[] tierNames = Enum.GetNames(typeof(StudioCompressionTier));
        Assert(tierNames.Contains("MasterPcm") && tierNames.Contains("LosslessCompressed") && tierNames.Contains("PortableLossy"),
            "StudioCompressionTier must expose MasterPcm, LosslessCompressed, PortableLossy.");

        Type poolType = typeof(IStreamingExportSink).Assembly.GetType("Cavernize.Logic.Rendering.StreamingExportBufferPool");
        Assert(poolType != null, "StreamingExportBufferPool must exist in Cavernize.Logic.Rendering.");
        int maxBlockBytes = (int)poolType.GetField("MaxBlockBytes", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        int maxBlockFrames = (int)poolType.GetField("MaxBlockFrames", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert(maxBlockBytes <= 262144, $"Private pool block cap must be <= 256 KB, got {maxBlockBytes}");
        Assert(maxBlockFrames == 4096, $"Expected MaxBlockFrames == 4096, got {maxBlockFrames}");
        MethodInfo computeFrames = poolType.GetMethod("ComputeMaxFramesForChannels", BindingFlags.Public | BindingFlags.Static)!;
        int frames16ch = (int)computeFrames.Invoke(null, [16, 4])!;
        Assert(frames16ch > 0 && frames16ch * 16 * 4 <= 262144,
            $"16ch float chunk must stay <= 256 KB (got {frames16ch} frames = {frames16ch * 64} bytes).");

        // 2. DirectPcmStreamingSink NaN/Inf sanitization & clamping across multi-chunk 8192f write
        string wavPath = Path.Combine(tempDir, "sanitize_pcm.wav");
        const int totalFrames = 8192;
        float[] dirty = new float[totalFrames * 2];
        for (int i = 0; i < dirty.Length; i++) {
            dirty[i] = 0.25f * MathF.Sin(i * 0.03f);
        }
        dirty[0] = float.NaN;
        dirty[1] = float.PositiveInfinity;
        dirty[2] = float.NegativeInfinity;
        dirty[100] = 3.5f;
        dirty[101] = -3.5f;
        dirty[8200] = float.NaN;
        using (DirectPcmStreamingSink pcmSink = new(wavPath, 2, 48000, BitDepth.Float32, expectedFrames: totalFrames)) {
            pcmSink.WriteInterleavedBlock(dirty, totalFrames);
            Assert(pcmSink.FramesWritten == totalFrames, $"Expected {totalFrames} frames written, got {pcmSink.FramesWritten}");
        }
        using (RIFFWaveReader wavReader = new(wavPath)) {
            wavReader.ReadHeader();
            Assert(wavReader.ChannelCount == 2 && wavReader.Length == totalFrames,
                $"WAV header mismatch: ch={wavReader.ChannelCount}, len={wavReader.Length}");
            float[] readBack = new float[totalFrames * 2];
            wavReader.ReadBlock(readBack, 0, readBack.Length);
            Assert(readBack[0] == 0f && readBack[1] == 0f && readBack[2] == 0f && readBack[8200] == 0f,
                "NaN/Inf samples must sanitize to 0f.");
            Assert(MathF.Abs(readBack[100] - 1f) < 1e-4f && MathF.Abs(readBack[101] + 1f) < 1e-4f,
                "Out-of-range samples must clamp to [-1, 1].");
            for (int i = 0; i < readBack.Length; i++) {
                Assert(float.IsFinite(readBack[i]) && MathF.Abs(readBack[i]) <= 1.0001f, $"Invalid PCM sample at {i}: {readBack[i]}");
            }
        }

        // 3. DirectFlacStreamingSink native FLAC works standalone without LGSR twin block
        string flacPath = Path.Combine(tempDir, "native_standalone.flac");
        using (DirectFlacStreamingSink flacSink = new(flacPath, 2, 48000, BitDepth.Int24, 4096)) {
            flacSink.WriteInterleavedBlock(dirty, 4096);
            Assert(flacSink.FramesWritten == 4096, $"Expected 4096 FLAC frames, got {flacSink.FramesWritten}");
        }
        byte[] flacMagic = File.ReadAllBytes(flacPath);
        Assert(flacMagic.Length > 42 && flacMagic[0] == (byte)'f' && flacMagic[1] == (byte)'L' &&
               flacMagic[2] == (byte)'a' && flacMagic[3] == (byte)'C',
            "DirectFlacStreamingSink must emit valid native fLaC header without LGSR twin.");

        // 4. StudioBatchExporter IsCancelled & LosslessCompressed without twin
        int sr = 48000;
        using SyntheticClockReader trackReader = new(sr, sr);
        using CavernizeTrack track = new(trackReader, Codec.PCM_LE, 0);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        StudioBatchExportJob cancelJob = new() {
            TrackName = "cancel_test",
            OutputPath = Path.Combine(tempDir, "cancelled.wav"),
            CompressionTier = StudioCompressionTier.MasterPcm,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.Headphone3D,
                ApplyPersonalizedHeadphonePost = false
            }
        };
        StudioBatchExportResult cancelRes = StudioBatchExporter.ExportSingle(track, cancelJob, token: cts.Token);
        Assert(!cancelRes.Succeeded, "Cancelled export must report Succeeded == false.");
        Assert(cancelRes.IsCancelled || cancelRes.ErrorMessage.Contains("cancel", StringComparison.OrdinalIgnoreCase),
            $"Cancelled export must flag IsCancelled or cancel message (got IsCancelled={cancelRes.IsCancelled}, msg='{cancelRes.ErrorMessage}').");
        StudioBatchExportResult syntheticCancelRes = new() { Succeeded = false, IsCancelled = true, CompressionTier = StudioCompressionTier.LosslessCompressed };
        Assert(syntheticCancelRes.IsCancelled && syntheticCancelRes.CompressionTier == StudioCompressionTier.LosslessCompressed,
            "StudioBatchExportResult must expose IsCancelled & CompressionTier.");

        // 5. IsAdmCompressionLocked ADM PCM-only guard (probe if exposed on StudioBatchExporter or StudioComposableExportOptions)
        MethodInfo admLockMethod = typeof(StudioBatchExporter).GetMethod("IsAdmCompressionLocked", BindingFlags.Public | BindingFlags.Static)
            ?? typeof(StudioComposableExportOptions).GetMethod("IsAdmCompressionLocked", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        if (admLockMethod != null) {
            ParameterInfo[] ps = admLockMethod.GetParameters();
            if (admLockMethod.IsStatic && ps.Length == 2 && ps[0].ParameterType == typeof(StudioBaseOutputFormat) && ps[1].ParameterType == typeof(StudioCompressionTier)) {
                Assert((bool)admLockMethod.Invoke(null, [StudioBaseOutputFormat.DolbyAtmosAdm, StudioCompressionTier.LosslessCompressed])!, "ADM must lock LosslessCompressed.");
                Assert((bool)admLockMethod.Invoke(null, [StudioBaseOutputFormat.DolbyAtmosAdm, StudioCompressionTier.PortableLossy])!, "ADM must lock PortableLossy.");
                Assert(!(bool)admLockMethod.Invoke(null, [StudioBaseOutputFormat.DolbyAtmosAdm, StudioCompressionTier.MasterPcm])!, "ADM MasterPcm must not be locked.");
                Assert(!(bool)admLockMethod.Invoke(null, [StudioBaseOutputFormat.EclipsaIamf, StudioCompressionTier.LosslessCompressed])!, "EclipsaIamf must allow compression.");
            }
        }

        // 6. AudioFile .iamf / .amb routing + IamfEnvironmentWriter -> IamfDecoder roundtrip
        string iamfPath = Path.Combine(tempDir, "scene_hoa.iamf");
        string ambIamfPath = Path.Combine(tempDir, "scene_hoa.amb");
        Listener l = new(false) { SampleRate = 48000, UpdateRate = 1024, MaximumSources = 16 };
        Source s0 = new() { Position = new Vector3(1f, 0f, 1f) };
        l.AttachSource(s0);
        using (IamfEnvironmentWriter iw = new(iamfPath, l, 2048, BitDepth.Int24, useSceneBasedHoa: true, hoaOrder: 3, mixLabel: "TestHOA")) {
            iw.WriteNextFrame();
            iw.WriteNextFrame();
        }
        File.Copy(iamfPath, ambIamfPath, overwrite: true);
        using (AudioFile afIamf = new(iamfPath)) {
            Assert(afIamf.Tracks.Count == 1 && afIamf.Tracks[0].Codec == Codec.IAMF, "AudioFile must route .iamf to Codec.IAMF.");
        }
        using (AudioFile afAmb = new(ambIamfPath)) {
            Assert(afAmb.Tracks.Count == 1 && afAmb.Tracks[0].Codec == Codec.IAMF, "AudioFile must sniff IAMF-headered .amb to Codec.IAMF.");
        }
        using (FileStream fs = File.OpenRead(iamfPath)) {
            IamfDecoder dec = new(fs);
            Assert(dec.ChannelCount == 16 && dec.SampleRate == 48000 && dec.Info.ElementType == IamfAudioElementType.SceneBased,
                $"IamfDecoder roundtrip mismatch: ch={dec.ChannelCount}, sr={dec.SampleRate}, type={dec.Info.ElementType}");
            Assert(MathF.Abs(dec.Info.TruePeakDbtp - (-1.0f)) < 0.05f, $"Expected -1.0 dBTP MixPresentation metadata, got {dec.Info.TruePeakDbtp}");
        }
    } finally {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
});

// ---------- Test 19: SpatialChoreo & PsychoacousticGovernor ----------
RunTest("T19_SpatialChoreo_SpatialGovernor", () => {
    // 1. Lead Vocal |az|<=22deg, Bass |az|<=15deg & el==0
    float vAz = 135f, vEl = 65f, vRad = 0.2f;
    PsychoacousticGovernor.ClampRoleAngles(NeuralStemRole.LeadVocal, ref vAz, ref vEl, ref vRad);
    Assert(MathF.Abs(vAz) <= 22.0f && MathF.Abs(vEl) <= 25.0f && vRad >= 0.55f,
        $"LeadVocal clamp failed: az={vAz}, el={vEl}, rad={vRad}");

    float bAz = -110f, bEl = 45f, bRad = 0.1f;
    PsychoacousticGovernor.ClampRoleAngles(NeuralStemRole.Bass, ref bAz, ref bEl, ref bRad);
    Assert(MathF.Abs(bAz) <= 15.0f && bEl == 0.0f && bRad >= 0.65f,
        $"Bass clamp failed: az={bAz}, el={bEl}, rad={bRad}");

    // 2. Angular hysteresis (<2deg deadband) & slew cap (<=60 deg/s)
    Vector3 prev = PsychoacousticGovernor.SphericalToCartesianUnit(0f, 0f);
    Vector3 jitter = PsychoacousticGovernor.SphericalToCartesianUnit(1.1f, 0.2f);
    Vector3 held = PsychoacousticGovernor.ApplyAngularHysteresisAndSlew(prev, jitter, 0.25);
    Assert(Vector3.Distance(prev, held) < 1e-6f, "Sub-2deg jitter must be suppressed by hysteresis.");

    Vector3 farTarget = PsychoacousticGovernor.SphericalToCartesianUnit(90f, 0f);
    Vector3 slewed = PsychoacousticGovernor.ApplyAngularHysteresisAndSlew(prev, farTarget, 0.25);
    float movedDeg = MathF.Acos(Math.Clamp(Vector3.Dot(prev, slewed), -1f, 1f)) * (180f / MathF.PI);
    Assert(movedDeg <= 15.05f, $"Slew cap <=60 deg/s over 0.25s exceeded: moved={movedDeg:0.2} deg");

    // 3. 12x6 ERB repulsion separates co-located stems while keeping LeadVocal anchored
    Vector3[] dirs = [
        PsychoacousticGovernor.SphericalToCartesianUnit(0f, 0f),
        PsychoacousticGovernor.SphericalToCartesianUnit(0f, 0f)
    ];
    NeuralStemRole[] roles = [NeuralStemRole.LeadVocal, NeuralStemRole.Guitar];
    float[] energies = [0.9f, 0.8f];
    PsychoacousticGovernor.ApplyErb12x6Repulsion(dirs, roles, energies, 2);
    Assert(Vector3.Dot(dirs[0], dirs[1]) < 0.999f, "12x6 ERB repulsion must separate co-located Guitar from LeadVocal.");

    // 4. Layer1AudioForensics <= 8 cues/min & DeterministicRuleDirector
    int sr = 48000;
    int len = sr * 12; // 12s track -> <= ceil(12/60*8) or <= 8 cues/min
    float[] vocL = new float[len], vocR = new float[len], basL = new float[len], basR = new float[len];
    float[] gtrL = new float[len], gtrR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        float env = (i / (sr * 3)) % 2 == 0 ? 0.6f : 0.15f;
        vocL[i] = vocR[i] = env * MathF.Sin((float)(2 * Math.PI * 440 * t));
        basL[i] = basR[i] = 0.5f * MathF.Sin((float)(2 * Math.PI * 75 * t));
        gtrL[i] = 0.4f * MathF.Sin((float)(2 * Math.PI * 880 * t));
        gtrR[i] = 0.4f * MathF.Cos((float)(2 * Math.PI * 660 * t));
    }
    StereoStemBuffer[] stems = [
        new("vocals", vocL, vocR),
        new("bass", basL, basR),
        new("other", gtrL, gtrR)
    ];
    LyricMoodDigest[] moods = [new(LyricMoodTag.Anthem, 0.8f, 0.7f, 0, 0.0), new(LyricMoodTag.Intimate, 0.3f, 0.4f, 1, 6.0)];
    DirectorCue[] cues = Layer1AudioForensics.Analyze(stems, sr, SpatialChoreoMode.Cinematic, moods);
    Assert(cues.Length <= Math.Max(8, (int)Math.Ceiling(12.0 / 60.0 * 8.0) + 1), $"Cue budget exceeded: {cues.Length}");

    // 5. SpatialChoreoTrack EvaluatePositionZeroAlloc (0 B GC) & role bounds
    SpatialChoreoTrack track = SpatialChoreoTrack.Build(["vocals", "bass", "other"], cues, SpatialChoreoMode.Cinematic, 12.0);
    int vocIdx = track.FindStemIndex(NeuralStemRole.LeadVocal, "vocals");
    int basIdx = track.FindStemIndex(NeuralStemRole.Bass, "bass");
    Assert(vocIdx >= 0 && basIdx >= 0, "Failed to resolve LeadVocal / Bass indices in SpatialChoreoTrack.");

    track.EvaluatePositionZeroAlloc(vocIdx, 1.5, out _);
    track.EvaluatePositionZeroAlloc(basIdx, 1.5, out _);
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int k = 0; k < 5000; k++) {
        double t = (k % 120) * 0.1;
        Assert(track.EvaluatePositionZeroAlloc(vocIdx, t, out Vector3 vPos), "Vocal LUT eval failed.");
        Assert(track.EvaluatePositionZeroAlloc(basIdx, t, out Vector3 bPos), "Bass LUT eval failed.");
        PsychoacousticGovernor.CartesianToSphericalDeg(vPos, out float aV, out _);
        PsychoacousticGovernor.CartesianToSphericalDeg(bPos, out float aB, out float eB);
        if (!(MathF.Abs(aV) <= 22.05f)) Assert(false, $"Vocal azimuth violated bound: {aV}");
        if (!(MathF.Abs(aB) <= 15.05f && MathF.Abs(eB) <= 0.05f)) Assert(false, $"Bass bound violated: az={aB}, el={eB}");
    }
    long allocAfter = GC.GetAllocatedBytesForCurrentThread();
    Assert(allocAfter - allocBefore == 0, $"EvaluatePositionZeroAlloc allocated {allocAfter - allocBefore} bytes!");

    // 6. CNTC v2 FromSerialized roundtrip parity & BS.1770 -1.0 dBTP clamp
    Vector4[] lutCopy = new Vector4[track.LutEntryCount];
    Assert(track.CopyLutTo(lutCopy) == track.LutEntryCount, "CopyLutTo length mismatch.");
    SpatialChoreoTrack restored = SpatialChoreoTrack.FromSerialized(
        track.Mode, track.StepSec, track.DurationSec,
        track.StemRoles.ToArray(), track.StemNames.ToArray(),
        track.DirectorCues.ToArray(), lutCopy, track.FrameCount);
    Assert(restored != null, "FromSerialized returned null.");
    restored!.EvaluatePositionZeroAlloc(vocIdx, 4.25, out Vector3 rPos);
    track.EvaluatePositionZeroAlloc(vocIdx, 4.25, out Vector3 oPos);
    Assert(Vector3.Distance(rPos, oPos) < 1e-5f, $"FromSerialized LUT mismatch: {Vector3.Distance(rPos, oPos)}");

    float[][] hotBlock = [new float[256], new float[256]];
    Array.Fill(hotBlock[0], 1.6f);
    Array.Fill(hotBlock[1], -1.6f);
    PsychoacousticGovernor.ClampBlockHeadroomBs1770(hotBlock, 256);
    Assert(hotBlock[0].Max(MathF.Abs) <= PsychoacousticGovernor.MaxHeadroomLinear + 1e-5f,
        "ClampBlockHeadroomBs1770 failed -1.0 dBTP ceiling.");
});

// ---------- Test 20: 8 kHz..192 kHz MinPhase Render + Concurrent Head-Track Thread Isolation + Convolver Disposal ----------
RunTest("T20_LowSampleRate8kMinPhase_ConcurrentHeadTrackIsolation_And_ConvolverDisposal", () => {
    // 1. Low & high sample rates (8 kHz, 22.05 kHz, 192 kHz) with Minimum-Phase HRIR must render non-null, finite, non-silent audio
    int[] probeRates = [8000, 22050, 192000];
    foreach (int sr in probeRates) {
        using (VirtualizerFilter.BeginStaticStateScope()) {
            Listener.HeadphoneVirtualizer = true;
            Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
            Listener.EnvironmentType = Environments.Studio;
            VirtualizerFilter.Reset(minimumPhase: true);

            const int updateRate = 512;
            const int blocks = 12;
            float[] white = new float[updateRate * blocks];
            uint rng = 0x12345678u;
            for (int i = 0; i < white.Length; i++) {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                white[i] = ((rng & 0xFFFF) / 32768f - 1f) * 0.25f;
            }

            Listener listener = new(false) {
                SampleRate = sr,
                UpdateRate = updateRate,
                LFESeparation = true
            };
            Source src = new() {
                Clip = new Clip(white, 1, sr),
                Position = new Vector3(-10f, 0f, 0f),
                Volume = 1f
            };
            listener.AttachSource(src);

            double energyL = 0, energyR = 0;
            for (int b = 0; b < blocks; b++) {
                float[] rendered = listener.Render(1);
                Assert(rendered != null && rendered.Length >= updateRate * 2, $"Listener.Render returned null/empty at {sr} Hz");
                int stride = Math.Max(1, rendered.Length / updateRate);
                for (int i = 0; i < updateRate; i++) {
                    float l = rendered[i * stride];
                    float r = rendered[i * stride + 1];
                    Assert(float.IsFinite(l) && float.IsFinite(r), $"Non-finite sample at {sr} Hz");
                    if (b >= 4) {
                        energyL += l * l;
                        energyR += r * r;
                    }
                }
            }
            Assert(energyL > 1e-4 && energyR > 1e-6, $"Silent output at {sr} Hz (eL={energyL}, eR={energyR})");
            Assert(energyL > energyR * 1.5, $"Left-90 lateral cue collapsed at {sr} Hz (eL={energyL:F4}, eR={energyR:F4})");
        }
    }

    // 2. Concurrent live playback thread hammering global Listener + VirtualizerFilter statics MUST NOT corrupt isolated B7 head-tracking
    bool stopLive = false;
    Exception liveEx = null;
    Listener.HeadphoneVirtualizer = true;
    Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
    Listener.EnvironmentType = Environments.Studio;
    VirtualizerFilter.Reset(false);

    Task livePlaybackHammer = Task.Run(() => {
        try {
            Listener liveListener = new(false) { SampleRate = 48000, UpdateRate = 256, LFESeparation = true };
            float[] tone = new float[256 * 8];
            for (int i = 0; i < tone.Length; i++) tone[i] = 0.2f * MathF.Sin(2f * MathF.PI * 440f * i / 48000f);
            liveListener.AttachSource(new Source { Clip = new Clip(tone, 1, 48000), Position = new Vector3(0f, 0f, 10f), Volume = 1f, Loop = true });
            while (!Volatile.Read(ref stopLive)) {
                VirtualizerFilter.LiveListenerRotationDegrees = Vector3.Zero;
                VirtualizerFilter.LiveListenerNormalizedPosition = Vector3.Zero;
                Listener.HeadphoneVirtualizer = true;
                float[] outBuf = liveListener.Render(1);
                if (outBuf == null || outBuf.Length == 0 || !float.IsFinite(outBuf[0])) {
                    throw new Exception("Live playback thread received invalid frame during concurrent isolated probe");
                }
            }
        } catch (Exception ex) {
            liveEx = ex;
        }
    });

    try {
        for (int trial = 0; trial < 4; trial++) {
            using (VirtualizerFilter.BeginStaticStateScope()) {
                Listener.HeadphoneVirtualizer = true;
                Listener.EnvironmentSize = new Vector3(10f, 7f, 10f);
                Listener.EnvironmentType = Environments.Studio;
                VirtualizerFilter.Reset(false);
                VirtualizerFilter.ConfigureExternalization(false, IsmRoomPreset.MasteringStudio, IsmWallMaterial.StudioBroadband,
                    2, 1f, false, false, 20f, 50f, new Vector3(5f, 3.5f, 5f), false);

                float[] white = new float[512 * 10];
                uint rng = 42u;
                for (int i = 0; i < white.Length; i++) {
                    rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                    white[i] = ((rng & 0xFFFF) / 32768f - 1f) * 0.25f;
                }

                static (double eL, double eR) RenderYaw(float[] sig, float yawDeg) {
                    VirtualizerFilter.ResetActiveFilters();
                    VirtualizerFilter.LiveListenerRotationDegrees = new Vector3(0f, yawDeg, 0f);
                    Listener l = new(false) { SampleRate = 48000, UpdateRate = 512, LFESeparation = true, Rotation = new Vector3(0f, yawDeg, 0f) };
                    l.AttachSource(new Source { Clip = new Clip(sig, 1, 48000), Position = new Vector3(0f, 0f, 10f), Volume = 1f });
                    double eL = 0, eR = 0;
                    for (int b = 0; b < 10; b++) {
                        float[] ren = l.Render(1);
                        int stride = Math.Max(1, ren.Length / 512);
                        if (b >= 3) {
                            for (int i = 0; i < 512; i++) {
                                eL += ren[i * stride] * ren[i * stride];
                                eR += ren[i * stride + 1] * ren[i * stride + 1];
                            }
                        }
                    }
                    return (eL, eR);
                }

                (double posL, double posR) = RenderYaw(white, 45f);
                (double negL, double negR) = RenderYaw(white, -45f);
                double ildPosDb = 10.0 * Math.Log10((posL + 1e-12) / (posR + 1e-12));
                double ildNegDb = 10.0 * Math.Log10((negL + 1e-12) / (negR + 1e-12));
                Assert(ildPosDb > 3.0, $"Concurrent B7 Yaw+45 corrupted on trial {trial}: ILD={ildPosDb:F2} dB");
                Assert(ildNegDb < -3.0, $"Concurrent B7 Yaw-45 corrupted on trial {trial}: ILD={ildNegDb:F2} dB");
            }
        }
    } finally {
        Volatile.Write(ref stopLive, true);
        livePlaybackHammer.Wait();
    }
    Assert(liveEx == null, $"Live playback hammer failed: {liveEx}");

    // 3. PartitionedDualConvolver.Dispose() & VirtualizerFilter.DisposeChannels() keep GC heap bounded across 25 probe cycles
    float[] irL = new float[256];
    float[] irR = new float[256];
    irL[4] = 0.8f; irR[12] = 0.6f;
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long memBefore = GC.GetTotalMemory(true);
    for (int iter = 0; iter < 25; iter++) {
        VirtualChannel[] chs = new VirtualChannel[12];
        for (int c = 0; c < chs.Length; c++) {
            chs[c] = VirtualChannel.FromMeasured(0f, -90f + c * 15f, irL, irR, 48000, 0f, minimumPhase: (iter & 1) == 0);
        }
        VirtualizerFilter.DisposeChannels(chs);
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long memAfter = GC.GetTotalMemory(true);
    long deltaMb = (memAfter - memBefore) / (1024 * 1024);
    Assert(deltaMb < 16, $"Managed heap grew unexpectedly after 25 disposed probe cycles: delta={deltaMb} MB");
});

// ---------- Test 21: Stage 2 (T2) Bug #2 SpatialObjectCortex Anti-Oscillation, SlerpUnitShortest & Monotonic Orbit ----------
RunTest("T21_Stage2_SpatialObjectCortex_AntiOscillation_SlerpAndOrbit", () => {
    // 1. SlerpUnitShortest must never negate vectors into opposite hemisphere or collapse magnitude
    var slerpMethod = typeof(DirectorCue).Assembly
        .GetType("Cavern.Spatial.SpatialCortexMath")!
        .GetMethod("SlerpUnitShortest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Vector3 Slerp(Vector3 a, Vector3 b, float t) => (Vector3)slerpMethod.Invoke(null, [a, b, t])!;

    Vector3 a135 = Vector3.Normalize(new Vector3(-0.7071f, 0f, 0.7071f)); // -45 deg
    Vector3 b135 = Vector3.Normalize(new Vector3(0.7071f, 0f, -0.7071f)); // +135 deg (180 deg antipodal)
    Vector3 midAnti = Slerp(a135, b135, 0.5f);
    Assert(MathF.Abs(midAnti.Length() - 1f) < 1e-3f, $"Antipodal slerp must stay unit-length, got {midAnti.Length():F4}");
    Assert(MathF.Abs(midAnti.Y) < 0.05f, $"Horizontal antipodal slerp must sweep around horizontal perimeter, got Y={midAnti.Y:F3}");

    Vector3 vLeft = Vector3.Normalize(new Vector3(-0.866f, 0f, 0.5f));   // -60 deg
    Vector3 vBackRight = Vector3.Normalize(new Vector3(0.5f, 0f, -0.866f)); // +150 deg (150 deg from -60 -> dot = -0.866 < 0!)
    Vector3 mid150 = Slerp(vLeft, vBackRight, 0.5f);
    Assert(MathF.Abs(mid150.Length() - 1f) < 1e-3f, $"150 deg slerp must stay unit-length, got {mid150.Length():F4}");
    // Midpoint between -60 deg and +150 deg along shortest 150 deg arc MUST be around -135 deg (X < 0, Z < 0), NOT negated to +45 deg!
    Assert(Vector3.Dot(mid150, vLeft) > 0.1f && Vector3.Dot(mid150, vBackRight) > 0.1f,
        $"150 deg SlerpUnitShortest flipped into wrong hemisphere: mid=({mid150.X:F3},{mid150.Y:F3},{mid150.Z:F3})");

    // 2. Full 4-mode trajectory stability on transient 4-stem music fixture (matches S25U pre-fix repro)
    int sr = 48000;
    int len = sr * 18;
    string[] names = ["vocals", "bass", "drums", "other"];
    StereoStemBuffer[] stems = new StereoStemBuffer[4];
    for (int s = 0; s < 4; s++) {
        float[] l = new float[len];
        float[] r = new float[len];
        float panL = s == 2 ? 0.65f : (s == 3 ? 0.92f : 0.72f);
        float panR = s == 2 ? 0.95f : (s == 3 ? 0.58f : 0.72f);
        float freq = 180f + s * 310f;
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            // Realistic beat bursts every 1.5s to trigger Layer1AudioForensics transient cues
            double beatPhase = t % 1.5;
            float env = beatPhase < 0.25 ? 0.90f : 0.22f;
            float v = 0.40f * env * MathF.Sin((float)(2.0 * Math.PI * freq * t));
            l[i] = v * panL;
            r[i] = v * panR;
        }
        stems[s] = new StereoStemBuffer(names[s], l, r);
    }

    foreach (SpatialChoreoMode mode in new[] { SpatialChoreoMode.Subtle, SpatialChoreoMode.Musical, SpatialChoreoMode.Cinematic, SpatialChoreoMode.Orbit }) {
        DirectorCue[] cues = Layer1AudioForensics.Analyze(stems, sr, mode, null);
        SpatialChoreoTrack track = SpatialChoreoTrack.Build(names, cues, mode, 18.0, 0.05);

        for (int s = 0; s < names.Length; s++) {
            float prevAz = 0f;
            float prevDelta = 0f;
            int centerCrossings = 0;
            int reversals = 0;
            float minR = 999f;
            float totalUnwrappedAz = 0f;
            int steps = 360;
            for (int k = 0; k <= steps; k++) {
                double t = k * 0.05;
                track.EvaluatePositionZeroAlloc(s, t, out Vector3 p, false);
                float r = p.Length();
                Assert(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z), $"Non-finite pos in {mode}/{names[s]}");
                if (r < minR) minR = r;
                float az = MathF.Atan2(p.X, p.Z) * (180f / MathF.PI);
                if (k > 0) {
                    float dAz = az - prevAz;
                    while (dAz > 180f) dAz -= 360f;
                    while (dAz < -180f) dAz += 360f;
                    totalUnwrappedAz += dAz;
                    if (MathF.Abs(az) > 12f && MathF.Abs(prevAz) > 12f && MathF.Sign(az) != MathF.Sign(prevAz) && MathF.Abs(dAz) < 90f) {
                        centerCrossings++;
                    }
                    if (MathF.Abs(dAz) > 0.45f) {
                        if (MathF.Abs(prevDelta) > 0.45f && MathF.Sign(dAz) != MathF.Sign(prevDelta)) {
                            reversals++;
                        }
                        prevDelta = dAz;
                    }
                }
                prevAz = az;
            }
            Assert(minR >= 0.88f, $"{mode}/{names[s]} collapsed radially toward center: minR={minR:F3}");
            if (mode == SpatialChoreoMode.Subtle || mode == SpatialChoreoMode.Musical) {
                Assert(centerCrossings == 0, $"{mode}/{names[s]} oscillated across center (cc={centerCrossings})!");
            }
            if (mode == SpatialChoreoMode.Orbit && (names[s] == "drums" || names[s] == "other")) {
                Assert(reversals == 0, $"Orbit/{names[s]} reversed direction (rev={reversals}) instead of monotonic orbit!");
                Assert(MathF.Abs(totalUnwrappedAz) >= 120f, $"Orbit/{names[s]} stalled instead of orbiting (netAz={totalUnwrappedAz:F1} deg)");
            }
        }
    }
});

// ---------- Test 22: Stage 2 (T2) Bug #1 Zero-Freeze Hybrid 7.1.4 Rebuild Skip & Async Config Swaps ----------
RunTest("T22_Stage2_ZeroFreeze_Hybrid714NoRedundantRebuild_And_AsyncToggle", () => {
    int sr = 48000;
    int len = sr * 2;
    using SyntheticClockReader reader = new(sr, len);
    using CavernizeTrack track = new(reader, Codec.PCM_Float, 0);
    using RealtimePlayerEngine engine = new();

    UpmixingSettings upmix = new(true) { Cavernize = false, MatrixUpmixing = true, Effect = 0.5f };
    RenderingSettings render = new();
    engine.Load(track, upmix, render);

    float[] sig = new float[len];
    for (int i = 0; i < len; i++) sig[i] = 0.2f * MathF.Sin(2f * MathF.PI * 330f * i / sr);
    NeuralSeparatedTrack sep = new() {
        SourceTrackKey = "t22_test.flac",
        SampleRate = sr,
        Stems = [
            new StereoStemBuffer("vocals", sig, sig),
            new StereoStemBuffer("drums", sig, sig),
            new StereoStemBuffer("bass", sig, sig),
            new StereoStemBuffer("other", sig, sig)
        ]
    };
    engine.BindSeparatedNeuralTrack(sep);
    NeuralAudioConfig nCfg = new() {
        SpatialUpmixMode = NeuralSpatialUpmixMode.HybridNeuralDirAc
    };
    engine.ApplyNeuralConfig(nCfg);
    int initialBuilds = engine.Hybrid714BuildCount;
    Assert(initialBuilds == 1, $"Expected exactly 1 initial Hybrid 7.1.4 build, got {initialBuilds}");

    // Toggle UpMix / Cavernize rapidly 6 times with unchanged Effect: MUST NOT rebuild Hybrid 7.1.4!
    for (int i = 0; i < 6; i++) {
        upmix.Cavernize = (i & 1) == 0;
        upmix.MatrixUpmixing = (i % 3) != 0;
        engine.ApplyUpmixingSettingsAsync(upmix).GetAwaiter().GetResult();
    }
    Assert(engine.Hybrid714BuildCount == initialBuilds,
        $"Toggling Cavernize/MatrixUpmixing with unchanged Effect caused redundant Hybrid 7.1.4 rebuilds: {engine.Hybrid714BuildCount} != {initialBuilds}");

    // Changing spatial Effect via ApplyUpmixingSettingsAsync MUST trigger exactly 1 rebuild
    upmix.Effect = 0.85f;
    engine.ApplyUpmixingSettingsAsync(upmix).GetAwaiter().GetResult();
    Assert(engine.Hybrid714BuildCount == initialBuilds + 1,
        $"Expected exactly 1 rebuild after Effect change, got {engine.Hybrid714BuildCount}");

    // Toggling off and back to HybridNeuralDirAc via ApplyNeuralConfigAsync must stay non-blocking & deterministic
    engine.ApplyNeuralConfigAsync(new NeuralAudioConfig { SpatialUpmixMode = NeuralSpatialUpmixMode.Stems3DObjects }).GetAwaiter().GetResult();
    Assert(engine.Hybrid714BuildCount == initialBuilds + 1,
        $"Switching away from HybridNeuralDirAc should not rebuild Hybrid 7.1.4: got {engine.Hybrid714BuildCount}");
    engine.ApplyNeuralConfigAsync(new NeuralAudioConfig { SpatialUpmixMode = NeuralSpatialUpmixMode.HybridNeuralDirAc }).GetAwaiter().GetResult();
    Assert(engine.Hybrid714BuildCount >= initialBuilds + 1,
        $"Re-enabling HybridNeuralDirAc via ApplyNeuralConfigAsync succeeded cleanly (builds={engine.Hybrid714BuildCount})");
});

// ---------- Test 23: Stage 2 (T2) Bug #11 Global AI Headroom Worker Cap & Thermal Breathing Yield ----------
RunTest("T23_Stage2_GlobalAiHeadroom_WorkerCap_And_ThermalYield", () => {
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8) == 3, "8-core Balanced default must cap background DSP workers at 3 (<=37.5%).");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(12) == 3, "12-core Balanced default must cap background DSP workers at max 3.");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8, 2) == 4, "8-core Fast Max 75% must cap background DSP workers at 4 (50%).");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(12, 2) == 4, "12-core Fast Max 75% must cap background DSP workers at max 4.");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(4) == 2, "4-core device must cap at 2.");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(2) == 1, "2-core device must cap at 1.");

    try {
        MvsepAntiArtifactPipeline.GlobalThermalExtraYieldMsProvider = () => 0;
        int coolFast = MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(12, 20.0, 0);
        Assert(coolFast == 0, $"Fast cool chunk (<50ms) should not waste yield, got {coolFast}");
        int coolHeavy = MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(12, 300.0, 0);
        Assert(coolHeavy == 12, $"Heavy cool chunk should apply base yield 12ms, got {coolHeavy}");
        int verySlow = MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(12, 600.0, 0);
        Assert(verySlow == 32, $"Very slow chunk (>=500ms) should escalate to >=32ms, got {verySlow}");

        MvsepAntiArtifactPipeline.GlobalThermalExtraYieldMsProvider = () => 40;
        int thermalExtra = MvsepAntiArtifactPipeline.GlobalThermalExtraYieldMsProvider();
        int warmHeavy = MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(12, 300.0, thermalExtra);
        Assert(warmHeavy == 40, $"Thermal pressure (40ms) must raise yield above base 12ms -> 40ms, got {warmHeavy}");
        int warmFast = MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(12, 20.0, 60);
        Assert(warmFast == 30, $"Severe thermal pressure (60ms) on fast chunk must apply half-yield 30ms, got {warmFast}");
    } finally {
        MvsepAntiArtifactPipeline.GlobalThermalExtraYieldMsProvider = null;
    }
});

// =========================================================================
// TEST 24 (Stage 3 T3): CNTC v3 Lossless Compression, O(1) PeekHeader & v2 Compat
// =========================================================================
RunTest("T24_T3_CntcV3_LosslessCompression_PeekHeader_And_V2BackwardCompat", () => {
    const int sr = 44100;
    const int len = 44100 * 2; // 2.0 s realistic 4-stem stereo musical track
    float[] vocL = new float[len], vocR = new float[len];
    float[] drmL = new float[len], drmR = new float[len];
    float[] basL = new float[len], basR = new float[len];
    float[] othL = new float[len], othR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        float env = 0.7f + 0.3f * MathF.Sin((float)(2 * Math.PI * 1.5 * t));
        vocL[i] = env * (0.35f * MathF.Sin((float)(2 * Math.PI * 440.0 * t)) + 0.12f * MathF.Sin((float)(2 * Math.PI * 880.0 * t)));
        vocR[i] = vocL[i] * 0.98f + 0.01f * MathF.Sin((float)(2 * Math.PI * 660.0 * t));
        drmL[i] = 0.25f * MathF.Sin((float)(2 * Math.PI * 110.0 * t)) * MathF.Exp(-(float)((t % 0.5) * 8.0));
        drmR[i] = drmL[i] * 0.95f;
        basL[i] = basR[i] = 0.42f * MathF.Sin((float)(2 * Math.PI * 65.0 * t));
        othL[i] = 0.20f * MathF.Sin((float)(2 * Math.PI * 523.25 * t));
        othR[i] = 0.20f * MathF.Sin((float)(2 * Math.PI * 659.25 * t));
    }
    StereoStemBuffer[] stems = [
        new StereoStemBuffer("vocals", vocL, vocR),
        new StereoStemBuffer("drums", drmL, drmR),
        new StereoStemBuffer("bass", basL, basR),
        new StereoStemBuffer("other", othL, othR)
    ];
    NeuralSeparatedTrack orig = new() {
        SourceTrackKey = "content://media/audio/t24_song.flac",
        SampleRate = sr,
        Stems = stems,
        MonoLeadVocalRef = (float[])vocL.Clone(),
        DerivedFromLosslessTwin = true,
        PlanSignature = "bs_roformer_sw_6stem_fp16|ov=4",
        DisplayTitle = "Neon Horizon",
        ArtistName = "Cavern Orchestra",
        OriginTag = "MVSEP Cloud",
        ModelSummary = "BS-RoFormer 6-Stem"
    };
    DirectorCue[] cues = Layer1AudioForensics.Analyze(stems, sr, SpatialChoreoMode.Musical);
    SpatialChoreoTrack origChoreo = SpatialChoreoTrack.Build(["vocals", "drums", "bass", "other"], cues, SpatialChoreoMode.Musical, 2.0);

    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t24_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        static double ComputeSnrDb(float[] a, float[] b) {
            double sig = 0.0, err = 0.0;
            for (int i = 0; i < a.Length; i++) {
                sig += (double)a[i] * a[i];
                double d = a[i] - b[i];
                err += d * d;
            }
            return err < 1e-18 ? 180.0 : 10.0 * Math.Log10(sig / err);
        }

        // 1. Compact16Bit: SNR >= 90 dB, ratio >= 3.0x, O(1) PeekHeader exact, Choreo roundtrip
        string p16 = Path.Combine(tmpDir, "compact16.ntc");
        NeuralTrackCacheCodec.WriteTrackFile(p16, orig, origChoreo, NeuralTrackCacheCompressionMode.Compact16Bit);
        NeuralTrackCacheEntryInfo info16 = NeuralTrackCacheCodec.PeekHeader(p16);
        Assert(info16 != null, "PeekHeader(Compact16Bit) returned null");
        Assert(info16.FormatVersion == 3, $"Expected CNTC v3, got v{info16.FormatVersion}");
        Assert(info16.DisplayTitle == "Neon Horizon" && info16.ArtistName == "Cavern Orchestra", "PeekHeader metadata mismatch");
        Assert(info16.OriginTag == "MVSEP Cloud" && info16.StemCount == 4, "PeekHeader origin/stemCount mismatch");
        Assert(info16.CompressionMode == NeuralTrackCacheCompressionMode.Compact16Bit, "PeekHeader compression mode mismatch");
        Assert(info16.CompressionRatio >= 3.0, $"Expected Compact16Bit ratio >= 3.0x, got {info16.CompressionRatio:0.00}x");
        Assert(info16.SavedPercent >= 66.0, $"Expected Compact16Bit savings >= 66%, got {info16.SavedPercent:0.0}%");

        NeuralSeparatedTrack r16 = NeuralTrackCacheCodec.ReadTrackFile(p16, out SpatialChoreoTrack c16);
        Assert(r16 != null && r16.Stems.Count == 4 && c16 != null && c16.FrameCount == origChoreo.FrameCount, "Compact16Bit read/choreo mismatch");
        double snr16 = ComputeSnrDb(vocL, r16.Stems[0].Left);
        Assert(snr16 >= 90.0, $"Compact16Bit SNR must be >= 90 dB, got {snr16:0.0} dB");

        // 2. Studio24Bit: SNR >= 135 dB, ratio >= 2.0x
        string p24 = Path.Combine(tmpDir, "studio24.ntc");
        NeuralTrackCacheCodec.WriteTrackFile(p24, orig, null, NeuralTrackCacheCompressionMode.Studio24Bit);
        NeuralTrackCacheEntryInfo info24 = NeuralTrackCacheCodec.PeekHeader(p24);
        Assert(info24 != null && info24.CompressionRatio >= 2.0, $"Expected Studio24Bit ratio >= 2.0x, got {info24?.CompressionRatio:0.00}x");
        NeuralSeparatedTrack r24 = NeuralTrackCacheCodec.ReadTrackFile(p24, out _);
        double snr24 = ComputeSnrDb(vocL, r24.Stems[0].Left);
        Assert(snr24 >= 135.0, $"Studio24Bit SNR must be >= 135 dB, got {snr24:0.0} dB");

        // 3. RawFloat32: bit-exact 0.0f max diff
        string pRaw = Path.Combine(tmpDir, "raw32.ntc");
        NeuralTrackCacheCodec.WriteTrackFile(pRaw, orig, null, NeuralTrackCacheCompressionMode.RawFloat32);
        NeuralSeparatedTrack rRaw = NeuralTrackCacheCodec.ReadTrackFile(pRaw, out _);
        float maxRawDiff = 0f;
        for (int i = 0; i < len; i++) maxRawDiff = MathF.Max(maxRawDiff, MathF.Abs(vocL[i] - rRaw.Stems[0].Left[i]));
        Assert(maxRawDiff == 0f, $"RawFloat32 must be bit-exact (0.0 diff), got {maxRawDiff}");

        // 4. Backward compatibility: hand-craft a valid legacy CNTC v2 file and verify ReadTrackFile + PeekHeader
        string pV2 = Path.Combine(tmpDir, "legacy_v2.ntc");
        using (FileStream fs = new(pV2, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new(fs, Encoding.UTF8)) {
            bw.Write(NeuralTrackCacheCodec.FileMagic);
            bw.Write((ushort)2); // v2
            bw.Write((ushort)0); // reserved
            bw.Write(sr);
            bw.Write(0u); // flags
            byte[] keyBytes = Encoding.UTF8.GetBytes("legacy/v2_track.flac");
            bw.Write(keyBytes.Length); bw.Write(keyBytes);
            byte[] sigBytes = Encoding.UTF8.GetBytes("legacy_sig");
            bw.Write(sigBytes.Length); bw.Write(sigBytes);
            bw.Write(1); // 1 stem
            byte[] stName = Encoding.UTF8.GetBytes("vocals");
            bw.Write(stName.Length); bw.Write(stName);
            int nShort = 512;
            bw.Write(nShort);
            for (int i = 0; i < nShort; i++) bw.Write(vocL[i]);
            bw.Write(nShort);
            for (int i = 0; i < nShort; i++) bw.Write(vocR[i]);
        }
        NeuralTrackCacheEntryInfo infoV2 = NeuralTrackCacheCodec.PeekHeader(pV2);
        Assert(infoV2 != null && infoV2.FormatVersion == 2 && infoV2.StemCount == 1, "PeekHeader(legacy v2) failed");
        NeuralSeparatedTrack rV2 = NeuralTrackCacheCodec.ReadTrackFile(pV2, out _);
        Assert(rV2 != null && rV2.Stems.Count == 1 && rV2.Stems[0].Length == 512, "ReadTrackFile(legacy v2) failed");
        Assert(MathF.Abs(rV2.Stems[0].Left[100] - vocL[100]) < 1e-6f, "Legacy v2 sample mismatch");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// =========================================================================
// TEST 25 (Stage 3 T3): MVSEP Cloud REST Client, SSRF Guard & Token Redaction
// =========================================================================
RunTest("T25_T3_MvsepCloudClient_RestContract_SsrfGuard_And_TokenRedaction", () => {
    const string secretToken = "mvsep_super_secret_token_987654321";
    const int sr = 44100;
    const int n = 4410; // 0.1 s stereo test tone
    float[] mixL = new float[n], mixR = new float[n];
    float[] vocL = new float[n], vocR = new float[n];
    float[] instL = new float[n], instR = new float[n];
    for (int i = 0; i < n; i++) {
        double t = i / (double)sr;
        vocL[i] = vocR[i] = 0.35f * MathF.Sin((float)(2 * Math.PI * 440.0 * t));
        instL[i] = 0.25f * MathF.Sin((float)(2 * Math.PI * 190.0 * t));
        instR[i] = 0.25f * MathF.Cos((float)(2 * Math.PI * 190.0 * t));
        mixL[i] = vocL[i] + instL[i];
        mixR[i] = vocR[i] + instR[i];
    }
    byte[] vocWav = MvsepCloudClient.EncodeStereoWav16(vocL, vocR, sr);
    byte[] instWav = MvsepCloudClient.EncodeStereoWav16(instL, instR, sr);

    using MockMvsepHttpHandler handler = new(vocWav, instWav);
    using MvsepCloudClient client = new(handler);

    // 1. FetchAlgorithmsAsync
    IReadOnlyList<MvsepRemoteAlgorithmInfo> algos = client.FetchAlgorithmsAsync().GetAwaiter().GetResult();
    Assert(algos.Count == 2 && algos[0].RenderId == 40 && algos[1].RenderId == 63, $"Unexpected algorithms count={algos.Count}");

    // 2. Full SeparateTrackAsync pipeline (create -> waiting -> processing -> done -> download stems)
    MvsepCloudOptions opts = new() {
        ApiToken = secretToken,
        BaseUrl = MvsepCloudOptions.DefaultBaseUrl,
        OutputFormat = MvsepOutputFormat.Wav16Bit,
        PollIntervalMs = 20,
        MaxPollSeconds = 10
    };
    MvsepAlgorithmPreset preset = MvsepAlgorithmPreset.FindOrDefault("mvsep_bs_roformer_vocals");
    NeuralSeparatedTrack separated = client.SeparateTrackAsync(
        "track_mvsep_test", mixL, mixR, sr, preset, opts,
        displayTitle: "Cloud Song", artistName: "Cloud Artist").GetAwaiter().GetResult();
    Assert(separated != null && separated.Stems.Count == 2, $"Expected 2 stems from MVSEP mock, got {separated?.Stems.Count}");
    Assert(separated.OriginTag == "MVSEP Cloud" && separated.DisplayTitle == "Cloud Song", "Origin/Title metadata mismatch");
    Assert(handler.PollCount >= 3, $"Expected >=3 status polls (waiting/processing/done), got {handler.PollCount}");
    Assert(separated.Stems.Any(s => s.Name == "vocals") && separated.Stems.Any(s => s.Name == "instrumental"),
        "Canonical vocals + instrumental stem names expected");

    // 3. SSRF guard: reject non-HTTPS and non-allowlisted hosts
    bool rejectedEvil = false;
    try { MvsepCloudClient.ValidateOfficialBaseUri("https://evil.example.com/api"); } catch (ArgumentException) { rejectedEvil = true; }
    Assert(rejectedEvil, "SSRF guard failed to reject evil.example.com");
    bool rejectedHttp = false;
    try { MvsepCloudClient.ValidateOfficialBaseUri("http://mvsep.com/api"); } catch (ArgumentException) { rejectedHttp = true; }
    Assert(rejectedHttp, "SSRF guard failed to reject plaintext http://mvsep.com/api");
    bool rejectedDownload = false;
    try { client.DownloadStemBytesAsync("https://evil.example.com/stem.wav").GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejectedDownload = true; }
    Assert(rejectedDownload, "DownloadStemBytesAsync failed to reject non-allowlisted host");

    // 4. Token redaction & masking
    string redacted = MvsepCloudClient.RedactSecrets($"Auth error for token={secretToken} on mvsep", secretToken);
    Assert(!redacted.Contains(secretToken, StringComparison.Ordinal) && redacted.Contains("[REDACTED]", StringComparison.Ordinal),
        $"Token leaked in RedactSecrets: {redacted}");
    string masked = MvsepCloudClient.MaskToken(secretToken);
    Assert(!masked.Contains("secret_token", StringComparison.Ordinal) && masked.Contains("••••", StringComparison.Ordinal),
        $"Token insufficiently masked: {masked}");

    // 5. Cancellation support
    using CancellationTokenSource cts = new();
    cts.Cancel();
    bool cancelled = false;
    try {
        client.SeparateTrackAsync("cancel_test", mixL, mixR, sr, preset, opts, token: cts.Token).GetAwaiter().GetResult();
    } catch (OperationCanceledException) {
        cancelled = true;
    }
    Assert(cancelled, "Expected OperationCanceledException when CancellationToken is already cancelled");
});

// =========================================================================
// TEST 26 (Stage 3 T3): Pure Instrumental Routing, Studio Bundles & BS-RoFormer STFT
// =========================================================================
RunTest("T26_T3_PureInstrumental_StudioBundles_And_BsRoFormerHostStft", () => {
    const int sr = 44100;
    const int len = 44100; // 1.0 s
    float[] leadL = new float[len], leadR = new float[len];
    float[] backL = new float[len], backR = new float[len];
    float[] instL = new float[len], instR = new float[len];
    float[] mixL = new float[len], mixR = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        leadL[i] = leadR[i] = 0.35f * MathF.Sin((float)(2 * Math.PI * 440.0 * t));
        backL[i] = 0.20f * MathF.Sin((float)(2 * Math.PI * 660.0 * t));
        backR[i] = 0.20f * MathF.Cos((float)(2 * Math.PI * 660.0 * t));
        instL[i] = 0.28f * MathF.Sin((float)(2 * Math.PI * 190.0 * t));
        instR[i] = 0.28f * MathF.Cos((float)(2 * Math.PI * 190.0 * t));
        mixL[i] = leadL[i] + backL[i] + instL[i];
        mixR[i] = leadR[i] + backR[i] + instR[i];
    }

    // 1. NeuralStemStageProcessor safety: 2-stem kara_2 track MUST NOT sum mix-complement backing_vocals into target
    using (NeuralStemStageProcessor proc = new()) {
        proc.Configure(sr, SpatialProgramMetadata.CreateDefault());
        proc.SetSourceTrackSampleRate(sr);
        float[] complementL = new float[len], complementR = new float[len];
        for (int i = 0; i < len; i++) {
            complementL[i] = backL[i] + instL[i];
            complementR[i] = backR[i] + instR[i];
        }
        NeuralSeparatedTrack kara2Track = new() {
            SourceTrackKey = "kara2_safety",
            SampleRate = sr,
            Stems = [
                new StereoStemBuffer("lead_vocal", leadL, leadR),
                new StereoStemBuffer("backing_vocals", complementL, complementR)
            ]
        };
        proc.BindKaraokeReferenceTrack(kara2Track);
        proc.ApplyConfig(new NeuralAudioConfig {
            SpatialKaraokeEnabled = true,
            KaraokeVocalLevel = 0f,
            KaraokePreserveBackingVocals = false // Pure Instrumental toggle on 2-stem kara_2
        });
        float[][] bus = [(float[])mixL.Clone(), (float[])mixR.Clone()];
        SpatialSourceDescriptor[] desc = [
            new() { Index = 0, Layer = SourceLayer.Bed, NormalizedPosition = new Vector3(-0.7f, 0f, 0.7f) },
            new() { Index = 1, Layer = SourceLayer.Bed, NormalizedPosition = new Vector3(0.7f, 0f, 0.7f) }
        ];
        for (int off = 0; off < len; off += 1024) {
            int blk = Math.Min(1024, len - off);
            float[][] slice = [new float[blk], new float[blk]];
            Array.Copy(bus[0], off, slice[0], 0, blk);
            Array.Copy(bus[1], off, slice[1], 0, blk);
            proc.Process(new SpatialSceneFrame {
                SampleRate = sr,
                FrameCount = blk,
                TrackSamplePosition = off,
                SourceBuffers = slice,
                ActiveSourceBufferCount = 2,
                Sources = desc
            });
            Array.Copy(slice[0], 0, bus[0], off, blk);
            Array.Copy(slice[1], 0, bus[1], off, blk);
        }
        // Verify instrument 190 Hz energy remained intact (phase-invariant complex DFT; proves kara_2 complement was NOT subtracted!)
        double reIn = 0.0, imIn = 0.0, reOut = 0.0, imOut = 0.0;
        for (int i = 4096; i < len; i++) {
            double ang = 2.0 * Math.PI * 190.0 * i / sr;
            double c = Math.Cos(ang), s = Math.Sin(ang);
            reIn += mixL[i] * c; imIn += mixL[i] * s;
            reOut += bus[0][i] * c; imOut += bus[0][i] * s;
        }
        double magIn = Math.Sqrt(reIn * reIn + imIn * imIn);
        double magOut = Math.Sqrt(reOut * reOut + imOut * imOut);
        double instDeltaDb = 20.0 * Math.Log10(Math.Max(1e-9, magOut) / Math.Max(1e-9, magIn));
        Assert(Math.Abs(instDeltaDb) < 2.0, $"kara_2 mix-complement safety violated! Instrument 190 Hz shifted by {instDeltaDb:0.00} dB");
    }

    // 2. StudioKaraokeLeadBacking (2-stage cascade) + StudioFullBand6Stem (6-stem + vocal anchor)
    using MockT3MultiModelRuntime mockRt = new();
    NeuralPipelineOrchestrator orch = new(mockRt, mockRt);
    NeuralAudioConfig bundle2Cfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        StudioBundlePreset = NeuralEnsembleBundlePreset.StudioKaraokeLeadBacking,
        OverlapFactor = 2,
        EnableTta = false,
        EnablePhaseFix = false,
        EnforceEnergyConservation = true,
        TrackCacheEnabled = false
    };
    NeuralExecutionPlan plan2 = orch.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, bundle2Cfg);
    Assert(plan2.Steps.Count == 2, $"Expected 2-stage StudioKaraokeLeadBacking plan, got {plan2.Steps.Count} steps");
    (NeuralSeparatedTrack cascadeTrack, _, _) = orch.ExecuteTrackPipeline(
        "t26_cascade", 0L, 0L, mixL, mixR, sr, NeuralPipelineGoal.SpatialKaraoke, bundle2Cfg, displayTitle: "Cascade Test");
    Assert(cascadeTrack.Stems.Any(s => s.Name == "lead_vocal") &&
           cascadeTrack.Stems.Any(s => s.Name == "backing_vocals") &&
           cascadeTrack.Stems.Any(s => s.Name == "instrumental"),
        "2-stage cascade must emit lead_vocal + backing_vocals + instrumental");

    NeuralAudioConfig bundle6Cfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        StudioBundlePreset = NeuralEnsembleBundlePreset.StudioFullBand6Stem,
        OverlapFactor = 2,
        EnableTta = false,
        EnablePhaseFix = false,
        EnforceEnergyConservation = true,
        TrackCacheEnabled = false
    };
    NeuralExecutionPlan plan6 = orch.BuildPlan(NeuralPipelineGoal.StemSeparation, bundle6Cfg);
    Assert(plan6.Steps.Count == 2, $"Expected 2-step StudioFullBand6Stem plan (6-stem + vocal anchor), got {plan6.Steps.Count}");
    (NeuralSeparatedTrack sixTrack, _, _) = orch.ExecuteTrackPipeline(
        "t26_6stem", 0L, 0L, mixL, mixR, sr, NeuralPipelineGoal.StemSeparation, bundle6Cfg);
    Assert(sixTrack.Stems.Count >= 6, $"Expected >=6 stems from StudioFullBand6Stem, got {sixTrack.Stems.Count}");

    // 3. BsRoFormerHostStft dual-tensor STFT -> RunMultiInput -> iSTFT roundtrip (>= 30 dB reconstruction, 0 NaN/Inf)
    using MockBsRoFormerRuntime bsRt = new();
    NeuralAudioConfig bsCfg = new() {
        OverlapFactor = 2,
        EnableTta = false,
        EnablePhaseFix = false,
        EnforceEnergyConservation = false
    };
    (StereoStemBuffer[] bsStems, _) = NeuralAudioOfflinePipeline.RunSeparation(
        bsRt,
        ["bs_roformer_sw_6stem_fp16.onnx"],
        [["vocals"]],
        [1f],
        mixL, mixR, sr, bsCfg, chunkSamples: 32768);
    Assert(bsStems.Length == 1 && bsStems[0].Length == len, "BsRoFormerHostStft did not return expected stem length");
    Assert(bsRt.MultiInputCalls > 0, "BsRoFormerHostStft must dispatch through INeuralSession.RunMultiInput");
    double sigE = 0.0, errE = 0.0;
    for (int i = 2048; i < len - 2048; i++) {
        float y = bsStems[0].Left[i];
        Assert(float.IsFinite(y), $"Non-finite sample at {i} in BsRoFormerHostStft output");
        sigE += (double)mixL[i] * mixL[i];
        double d = mixL[i] - y;
        errE += d * d;
    }
    double bsSnr = errE < 1e-18 ? 180.0 : 10.0 * Math.Log10(sigE / errE);
    Assert(bsSnr >= 30.0, $"BsRoFormerHostStft identity STFT/iSTFT SNR must be >= 30 dB, got {bsSnr:0.0} dB");
});

// ---------- Test 27: Live Choreography Single Source of Truth, Hybrid 7.1.4 & LFE Exclusion, Pause/Unpause Stability ----------
RunTest("T27_LiveChoreography_SingleSourceOfTruth_And_Hybrid714Exclusion", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();
    int sr = 48000;
    int len = sr * 12; // 12s stereo track + 4 stems

    StereoStemBuffer MakeStem(string name, float freqL, float freqR) {
        float[] l = new float[len];
        float[] r = new float[len];
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            l[i] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * freqL * t));
            r[i] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * freqR * t));
        }
        return new StereoStemBuffer(name, l, r);
    }

    StereoStemBuffer[] stems = [
        MakeStem("vocals", 440f, 440f),
        MakeStem("bass", 75f, 75f),
        MakeStem("drums", 180f, 220f),
        MakeStem("other", 620f, 880f)
    ];
    NeuralSeparatedTrack sep = new() {
        SourceTrackKey = "t27_choreo_track",
        SampleRate = sr,
        Stems = stems
    };
    SpatialChoreoTrack choreo = NeuralStemSpatializer.BuildChoreoTrack(stems, sr, SpatialChoreoMode.Orbit);
    Assert(choreo != null && choreo.StemCount == 4, "Failed to build SpatialChoreoTrack for T27.");

    using SyntheticClockReader reader = new(sr, len);
    using CavernizeTrack track = new(reader, Codec.PCM_LE, 0);
    using RealtimePlayerEngine engine = new();
    engine.HeadphoneVirtualizer = true;
    engine.Load(track, new UpmixingSettings(true), new RenderingSettings());

    // Part A: HybridNeuralDirAc (IA 7.1.4) MUST NEVER collapse or move any of the 12 fixed speaker buses
    engine.ApplyNeuralConfig(new NeuralAudioConfig { SpatialUpmixMode = NeuralSpatialUpmixMode.HybridNeuralDirAc });
    engine.BindSpatialUpmixTrack(sep);
    engine.SceneHub.ActiveChoreoMode = SpatialChoreoMode.Orbit;
    engine.SceneHub.SetChoreoTrack(choreo);
    engine.SyncChoreographyPositions();
    Assert(engine.SourceItems.Count == 12, $"Expected 12 Hybrid 7.1.4 buses, got {engine.SourceItems.Count}");

    float[] outBuf = new float[1024 * 2];
    engine.IsPlaying = true;
    for (int b = 0; b < 40; b++) {
        engine.RenderNextInterleavedBlock(outBuf, 1024);
    }
    // Pause and explicitly probe SyncTelemetryFromTrackItems with playbackTimeSec > 0 (must still ignore fixed BedChannel buses!)
    engine.IsPlaying = false;
    engine.SyncChoreographyPositions();
    engine.SceneHub.SyncTelemetryFromTrackItems(engine.SourceItems, engine.EnvironmentSize, 4.5, loop: false);
    for (int i = 0; i < engine.SourceItems.Count; i++) {
        TrackSourceItem bus = engine.SourceItems[i];
        float drift = Vector3.Distance(bus.NormalizedPosition, bus.DefaultNormalizedPosition);
        Assert(drift < 1e-4f, $"Hybrid 7.1.4 bus '{bus.Name}' drifted by {drift:F4} under Orbit choreography!");
    }

    // Part B: Stems3DObjects MUST drive live Source.Position monotonically with ZERO tug-of-war, keep LFE pinned,
    // freeze cleanly on pause, preserve custom pins, and restore static anchors cleanly on Off.
    engine.ApplyNeuralConfig(new NeuralAudioConfig { SpatialUpmixMode = NeuralSpatialUpmixMode.Stems3DObjects });
    engine.Seek(0.0);
    engine.SceneHub.ActiveChoreoMode = SpatialChoreoMode.Orbit;
    engine.SceneHub.SetChoreoTrack(choreo);
    engine.SyncChoreographyPositions();

    TrackSourceItem lfeItem = engine.SourceItems.FirstOrDefault(x => x.IsLFE);
    Assert(lfeItem != null, "Expected dedicated AI LFE (.1) source in Stems3DObjects mode.");
    TrackSourceItem movingItem = engine.SourceItems.FirstOrDefault(x => x.Name.Contains("other", StringComparison.OrdinalIgnoreCase) && !x.IsLFE);
    Assert(movingItem != null, "Expected moving 'other' neural stem in Stems3DObjects mode.");
    TrackSourceItem pinnedItem = engine.SourceItems.FirstOrDefault(x => x.Name.Contains("drums", StringComparison.OrdinalIgnoreCase) && !x.IsLFE);
    Assert(pinnedItem != null, "Expected 'drums' neural stem in Stems3DObjects mode.");

    Vector3 customPin = new(-0.65f, 0.45f, -0.55f);
    engine.SetSourceCustomPosition(pinnedItem, customPin);

    engine.IsPlaying = true;
    Vector3 prevPos = movingItem.NormalizedPosition;
    Vector3 prevDelta = Vector3.Zero;
    float maxStep = 0f;
    float maxDistFromDefault = 0f;
    int sharpReversals = 0;
    for (int b = 0; b < 140; b++) { // ~3.0 seconds of continuous playback
        int written = engine.RenderNextInterleavedBlock(outBuf, 1024);
        Assert(written == 1024, $"Expected 1024 frames in Stems3DObjects block {b}, got {written}");

        Vector3 curPos = movingItem.NormalizedPosition;
        Vector3 step = curPos - prevPos;
        float stepLen = step.Length();
        if (stepLen > maxStep) maxStep = stepLen;
        float distDef = Vector3.Distance(curPos, movingItem.DefaultNormalizedPosition);
        if (distDef > maxDistFromDefault) maxDistFromDefault = distDef;
        if (b > 2 && prevDelta.Length() > 0.002f && stepLen > 0.002f) {
            float cosAngle = Vector3.Dot(Vector3.Normalize(prevDelta), Vector3.Normalize(step));
            if (cosAngle < -0.5f) {
                sharpReversals++;
            }
        }
        prevDelta = step;
        prevPos = curPos;

        // LFE and custom pin must remain strictly fixed every single block
        Assert(Vector3.Distance(lfeItem.NormalizedPosition, lfeItem.DefaultNormalizedPosition) < 1e-4f,
            $"AI LFE (.1) moved during choreography on block {b}: {lfeItem.NormalizedPosition}");
        Assert(Vector3.Distance(pinnedItem.NormalizedPosition, customPin) < 1e-4f,
            $"Custom-pinned stem '{pinnedItem.Name}' was overwritten by choreography on block {b}: {pinnedItem.NormalizedPosition}");
    }
    Assert(maxDistFromDefault > 0.08f, $"Expected Orbit choreography to visibly move '{movingItem.Name}' (maxDistFromDefault={maxDistFromDefault:F4}).");
    Assert(maxStep < 0.08f, $"Detected discontinuous position jump in '{movingItem.Name}': maxStep={maxStep:F4}");
    Assert(sharpReversals == 0, $"Detected back-and-forth tug-of-war oscillation in '{movingItem.Name}': sharpReversals={sharpReversals}");

    // Pause -> SyncChoreographyPositions -> Unpause continuity (must NOT snap to another point!)
    Vector3 posAtPause = movingItem.NormalizedPosition;
    engine.IsPlaying = false;
    engine.SyncChoreographyPositions();
    Assert(Vector3.Distance(movingItem.NormalizedPosition, posAtPause) < 0.01f,
        $"Paused position snapped away from {posAtPause} to {movingItem.NormalizedPosition}");
    engine.IsPlaying = true;
    engine.RenderNextInterleavedBlock(outBuf, 1024);
    Assert(Vector3.Distance(movingItem.NormalizedPosition, posAtPause) < 0.05f,
        $"Unpause caused position jump from {posAtPause} to {movingItem.NormalizedPosition}");

    // Turning Choreography Off MUST cleanly restore non-pinned neural stems to DefaultNormalizedPosition while keeping custom pin
    engine.IsPlaying = false;
    engine.SceneHub.ActiveChoreoMode = SpatialChoreoMode.Off;
    engine.SceneHub.SetChoreoTrack(null);
    engine.SyncChoreographyPositions();
    Assert(Vector3.Distance(movingItem.NormalizedPosition, movingItem.DefaultNormalizedPosition) < 1e-4f,
        $"Turning Choreography Off failed to restore '{movingItem.Name}' to default anchor: got {movingItem.NormalizedPosition} vs {movingItem.DefaultNormalizedPosition}");
    Assert(Vector3.Distance(pinnedItem.NormalizedPosition, customPin) < 1e-4f,
        "Turning Choreography Off must preserve user custom pin.");
});

// ---------- Test 28 (Stage 5 T5): Lossless FLAC + WAV/RF64 Metadata, Cover Art & Synced Lyrics Roundtrip ----------
RunTest("T28_T5_ExportMetadata_FlacAndWavRoundtrip", () => {
    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t28_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        int sr = 48000;
        int frames = 4096;
        float[] pcm = new float[frames * 2];
        for (int i = 0; i < frames; i++) {
            float s = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * i / sr));
            pcm[i * 2] = s;
            pcm[i * 2 + 1] = -s * 0.8f;
        }
        // Minimal valid JPEG header containing SOF0 (width=64, height=64) so TryReadImageDimensions parses exact dims
        byte[] fakeJpeg = [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x40, 0x00, 0x40, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xD9
        ];
        ExportMetadataPackage pkg = new() {
            Title = "Midnight Echoes",
            Artist = "Nova Horizon",
            AlbumArtist = "Nova Horizon",
            Album = "Orbital Sessions",
            TrackNumber = 3,
            TrackTotal = 12,
            DiscNumber = 1,
            Year = "2026",
            Genre = "Electronic",
            Comment = "Mastered in Cavernize",
            Encoder = "Cavernize",
            EmbedCoverArt = true,
            CoverArtBytes = fakeJpeg,
            CoverArtMimeType = "image/jpeg",
            EmbedLyrics = true,
            SyncedLyricsLrc = "[00:00.50]Drifting through the soundfield\n[00:02.25]Objects orbiting overhead"
        };

        // 1. FLAC in-place lossless VORBIS_COMMENT + PICTURE injection (zero audio re-encode!)
        string flacPath = Path.Combine(tmpDir, "test_t28.flac");
        using (DirectFlacStreamingSink flacSink = new(flacPath, 2, sr, BitDepth.Int16)) {
            flacSink.WriteInterleavedBlock(pcm, frames);
        }
        byte[] flacBefore = File.ReadAllBytes(flacPath);
        int audioOffsetBefore = FindFlacAudioFrameStart(flacBefore);
        Assert(audioOffsetBefore >= 42, $"Unexpected FLAC audio offset before embed: {audioOffsetBefore}");
        byte[] audioFramesBefore = flacBefore[audioOffsetBefore..];

        ExportMetadataEmbedResult flacRes = ExportMetadataEmbedder.EmbedInPlace(flacPath, pkg);
        Assert(flacRes.EmbeddedTags && flacRes.EmbeddedCoverArt && flacRes.EmbeddedLyrics,
            $"FLAC embed incomplete: meta={flacRes.EmbeddedTags}, art={flacRes.EmbeddedCoverArt}, lyr={flacRes.EmbeddedLyrics}, note={flacRes.Note}");

        byte[] flacAfter = File.ReadAllBytes(flacPath);
        Assert(flacAfter.Length > flacBefore.Length && Encoding.ASCII.GetString(flacAfter, 0, 4) == "fLaC", "Invalid FLAC magic after embed");
        int audioOffsetAfter = FindFlacAudioFrameStart(flacAfter);
        byte[] audioFramesAfter = flacAfter[audioOffsetAfter..];
        Assert(audioFramesBefore.AsSpan().SequenceEqual(audioFramesAfter), "FLAC audio frames were altered! Must be bit-exact zero re-encode.");
        string flacMetaAscii = Encoding.UTF8.GetString(flacAfter, 0, audioOffsetAfter);
        Assert(flacMetaAscii.Contains("TITLE=Midnight Echoes", StringComparison.Ordinal) &&
               flacMetaAscii.Contains("ARTIST=Nova Horizon", StringComparison.Ordinal) &&
               flacMetaAscii.Contains("ALBUM=Orbital Sessions", StringComparison.Ordinal) &&
               flacMetaAscii.Contains("LYRICS=[00:00.50]Drifting", StringComparison.Ordinal) &&
               flacMetaAscii.Contains("image/jpeg", StringComparison.Ordinal),
            "Missing expected Vorbis Comment / PICTURE fields in embedded FLAC.");

        // 2. WAV RIFF LIST/INFO + id3 (ID3v2.3 TIT2/TPE1/TALB/USLT/SYLT/APIC) injection
        string wavPath = Path.Combine(tmpDir, "test_t28.wav");
        using (DirectPcmStreamingSink wavSink = new(wavPath, 2, sr, BitDepth.Int16)) {
            wavSink.WriteInterleavedBlock(pcm, frames);
        }
        ExportMetadataEmbedResult wavRes = ExportMetadataEmbedder.EmbedInPlace(wavPath, pkg);
        Assert(wavRes.EmbeddedTags && wavRes.EmbeddedCoverArt && wavRes.EmbeddedLyrics,
            $"WAV embed incomplete: meta={wavRes.EmbeddedTags}, art={wavRes.EmbeddedCoverArt}, lyr={wavRes.EmbeddedLyrics}, note={wavRes.Note}");

        byte[] wavAfter = File.ReadAllBytes(wavPath);
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wavAfter.AsSpan(4, 4));
        Assert((long)riffSize + 8 == wavAfter.Length, $"WAV RIFF size mismatch: header={riffSize + 8}, actual={wavAfter.Length}");
        string wavLatin = Encoding.Latin1.GetString(wavAfter);
        Assert(wavLatin.Contains("LIST", StringComparison.Ordinal) && wavLatin.Contains("INFO", StringComparison.Ordinal) &&
               wavLatin.Contains("INAM", StringComparison.Ordinal) && wavLatin.Contains("IART", StringComparison.Ordinal) &&
               wavLatin.Contains("id3 ", StringComparison.Ordinal) && wavLatin.Contains("ID3", StringComparison.Ordinal) &&
               wavLatin.Contains("TIT2", StringComparison.Ordinal) && wavLatin.Contains("TPE1", StringComparison.Ordinal) &&
               wavLatin.Contains("USLT", StringComparison.Ordinal) && wavLatin.Contains("SYLT", StringComparison.Ordinal) &&
               wavLatin.Contains("APIC", StringComparison.Ordinal),
            "Missing expected LIST/INFO or ID3v2.3 frames (TIT2/TPE1/USLT/SYLT/APIC) in WAV.");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// ---------- Test 29 (Stage 5 T5): Export Filename Hygiene, Sanitization & Collision Resolution ----------
RunTest("T29_T5_ExportFileNameFormatter_HygieneAndCollision", () => {
    ExportMetadataPackage meta = new() {
        Title = "Starlight / Echoes: Live?",
        Artist = "Daft * Punk"
    };
    string admName = ExportFileNameFormatter.FormatStudioFileName(
        meta,
        "cavern_input_998123",
        StudioBaseOutputFormat.DolbyAtmosAdm,
        StudioCompressionTier.MasterPcm,
        enableAiUpmix: true,
        enableKaraoke: false,
        removeBackingVocals: false,
        includePerformance: false,
        enableLosslessTwin: false,
        dynamicChoreoMode: SpatialChoreoMode.Orbit,
        extensionWithDot: ".wav");
    Assert(admName == "Daft Punk - Starlight Echoes Live (Dolby Atmos ADM - AI - Motion Orbit).wav",
        $"Unexpected formatted Studio filename: '{admName}'");
    Assert(!admName.Contains("cavern_input", StringComparison.OrdinalIgnoreCase), "Internal opaque token leaked into filename.");

    string strippedRip = ExportFileNameFormatter.FormatClassicFileName(
        null,
        "04 - Neon_Dreams [24bit-96kHz FLAC] (WEB-DL)",
        "7.1.4 Cinema",
        ".flac");
    Assert(strippedRip == "Neon Dreams (7.1.4 Cinema).flac", $"Rip-tag cleanup failed: '{strippedRip}'");

    string opaqueFallback = ExportFileNameFormatter.FormatClassicFileName(null, "cavern_input_171234567", "Headphones 3D", ".wav");
    Assert(opaqueFallback == "Cavernize Master (Headphones 3D).wav", $"Opaque fallback mismatch: '{opaqueFallback}'");

    string longName = ExportFileNameFormatter.FormatStudioFileName(
        new ExportMetadataPackage { Title = new string('A', 180), Artist = new string('B', 80) },
        "fallback",
        StudioBaseOutputFormat.Headphone3D,
        StudioCompressionTier.LosslessCompressed,
        true, true, true, true, true, SpatialChoreoMode.Cinematic, ".flac");
    Assert(Path.GetFileNameWithoutExtension(longName).Length <= 120, $"Filename exceeded 120-char cap: {longName.Length}");

    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t29_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        string p1 = ExportFileNameFormatter.ResolveUniqueOutputPath(tmpDir, "Artist - Song (Headphone 3D).flac");
        File.WriteAllText(p1, "1");
        string p2 = ExportFileNameFormatter.ResolveUniqueOutputPath(tmpDir, "Artist - Song (Headphone 3D).flac");
        File.WriteAllText(p2, "2");
        string p3 = ExportFileNameFormatter.ResolveUniqueOutputPath(tmpDir, "Artist - Song (Headphone 3D).flac");
        Assert(Path.GetFileName(p2) == "Artist - Song (Headphone 3D) (2).flac", $"Unexpected (2) collision path: {p2}");
        Assert(Path.GetFileName(p3) == "Artist - Song (Headphone 3D) (3).flac", $"Unexpected (3) collision path: {p3}");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// ---------- Test 30 (Stage 5 T5): True Moving Semantic Atmos ADM BWF Objects + Stationary LFE + Embedded Tags ----------
RunTest("T30_T5_DynamicAtmosAdm_SemanticMovingObjects", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();
    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t30_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        int sr = 48000;
        int len = sr * 3; // 3.0 s stereo source so Orbit choreography produces rich time-varying ADM blocks
        string srcWav = Path.Combine(tmpDir, "source_stereo.wav");
        float[] srcPcm = new float[len * 2];
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            float bass = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 65.0 * t));
            float voc = 0.22f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * t));
            float synth = 0.18f * MathF.Sin((float)(2.0 * Math.PI * 780.0 * t));
            srcPcm[i * 2] = bass + voc + synth;
            srcPcm[i * 2 + 1] = bass + voc - synth * 0.7f;
        }
        using (DirectPcmStreamingSink w = new(srcWav, 2, sr, BitDepth.Int16)) {
            w.WriteInterleavedBlock(srcPcm, len);
        }

        using MockT3MultiModelRuntime mockRt = new();
        NeuralPipelineOrchestrator orch = new(mockRt, mockRt);
        using RIFFWaveReader reader = new(srcWav);
        reader.ReadHeader();
        using CavernizeTrack track = new(reader, Codec.PCM_LE, 0);

        string outAdm = Path.Combine(tmpDir, "Nova - Orbit (Dolby Atmos ADM - AI - Motion Orbit).wav");
        StudioBatchExportJob job = new() {
            TrackName = "Orbit",
            Preset = StudioExportPreset.AdmBwfItuBs2076,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.DolbyAtmosAdm,
                CompressionTier = StudioCompressionTier.MasterPcm,
                DynamicChoreoMode = SpatialChoreoMode.Orbit,
                EnableAiSpatialUpmix = true
            },
            DynamicChoreoMode = SpatialChoreoMode.Orbit,
            OutputPath = outAdm,
            BitDepth = BitDepth.Int16,
            NeuralRuntime = mockRt,
            NeuralOrchestrator = orch,
            NeuralConfig = new NeuralAudioConfig {
                OrchestrationMode = NeuralOrchestrationMode.Auto,
                StudioBundlePreset = NeuralEnsembleBundlePreset.StudioFullBand6Stem,
                OverlapFactor = 2,
                EnableTta = false,
                EnablePhaseFix = false,
                EnforceEnergyConservation = true,
                TrackCacheEnabled = false
            },
            Metadata = new ExportMetadataPackage {
                Title = "Orbit",
                Artist = "Nova",
                Album = "Spatial Lab",
                EmbedLyrics = true,
                SyncedLyricsLrc = "[00:00.50]Spinning in 3D space"
            }
        };

        StudioBatchExportResult res = StudioBatchExporter.ExportSingle(track, job);
        Assert(res.Succeeded && File.Exists(outAdm), $"Dynamic ADM export failed: {res.ErrorMessage}");
        Assert(res.EffectiveChoreoMode == SpatialChoreoMode.Orbit, $"Expected EffectiveChoreoMode=Orbit, got {res.EffectiveChoreoMode}");
        Assert(res.MetadataEmbedded && res.LyricsEmbedded, $"Expected MetadataEmbedded & LyricsEmbedded on ADM WAV");

        byte[] admBytes = File.ReadAllBytes(outAdm);
        string latin = Encoding.Latin1.GetString(admBytes);
        Assert(latin.Contains("axml", StringComparison.Ordinal) &&
               latin.Contains("chna", StringComparison.Ordinal) &&
               latin.Contains("dbmd", StringComparison.Ordinal),
            "ADM BWF chunks (axml/chna/dbmd) missing after metadata embed!");
        Assert(latin.Contains("LIST", StringComparison.Ordinal) && latin.Contains("id3 ", StringComparison.Ordinal),
            "Metadata LIST/INFO + id3 chunks missing on ADM BWF WAV!");

        // Parse ADM axml chunk to verify real time-varying Cartesian coordinates on moving objects!
        int axmlIdx = latin.IndexOf("<ebuCoreMain", StringComparison.Ordinal);
        Assert(axmlIdx >= 0, "Could not locate <ebuCoreMain> XML in ADM BWF.");
        int axmlEnd = latin.IndexOf("</ebuCoreMain>", axmlIdx, StringComparison.Ordinal);
        Assert(axmlEnd > axmlIdx, "Could not locate closing </ebuCoreMain> in ADM BWF.");
        string axml = latin.Substring(axmlIdx, (axmlEnd + "</ebuCoreMain>".Length) - axmlIdx);

        int movingFormats = 0;
        var cfMatches = System.Text.RegularExpressions.Regex.Matches(
            axml,
            @"<audioChannelFormat[^>]*typeDefinition=""Objects""[^>]*>(.*?)</audioChannelFormat>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match m in cfMatches) {
            var posMatches = System.Text.RegularExpressions.Regex.Matches(
                m.Groups[1].Value,
                @"<position coordinate=""X"">([^<]+)</position>\s*<position coordinate=""Y"">([^<]+)</position>\s*<position coordinate=""Z"">([^<]+)</position>");
            HashSet<string> distinctCoords = new(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match pm in posMatches) {
                distinctCoords.Add($"{pm.Groups[1].Value}|{pm.Groups[2].Value}|{pm.Groups[3].Value}");
            }
            if (distinctCoords.Count >= 4) {
                movingFormats++;
            }
        }
        Assert(movingFormats >= 2, $"Expected >=2 genuinely moving ADM Objects with >=4 distinct 3D positions, found {movingFormats}");
        Assert(axml.Contains("typeDefinition=\"DirectSpeakers\"", StringComparison.Ordinal) &&
               axml.Contains("LFE", StringComparison.OrdinalIgnoreCase),
            "Expected stationary LFE DirectSpeakers bed alongside moving ADM Objects.");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// ---------- Test 31 (Stage 5 T5): Dynamic Motion Eclipsa IAMF + Lossless FLAC Binaural Export w/ Cover & Lyrics ----------
RunTest("T31_T5_DynamicAtmosIamfAndBinaural_MotionAndTags", () => {
    using IDisposable virtScope = Cavern.Virtualizer.VirtualizerFilter.BeginStaticStateScope();
    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t31_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        int sr = 48000;
        int len = sr * 2; // 2.0 s stereo source
        string srcWav = Path.Combine(tmpDir, "source_t31.wav");
        float[] srcPcm = new float[len * 2];
        for (int i = 0; i < len; i++) {
            double t = i / (double)sr;
            srcPcm[i * 2] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 330.0 * t)) + 0.15f * MathF.Sin((float)(2.0 * Math.PI * 80.0 * t));
            srcPcm[i * 2 + 1] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * t)) + 0.15f * MathF.Sin((float)(2.0 * Math.PI * 80.0 * t));
        }
        using (DirectPcmStreamingSink w = new(srcWav, 2, sr, BitDepth.Int16)) {
            w.WriteInterleavedBlock(srcPcm, len);
        }

        byte[] fakeJpeg = [
            0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x20, 0x00, 0x20, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9
        ];
        ExportMetadataPackage meta = new() {
            Title = "Eclipse",
            Artist = "Cavern Lab",
            Album = "Tests",
            EmbedCoverArt = true,
            CoverArtBytes = fakeJpeg,
            CoverArtMimeType = "image/jpeg",
            EmbedLyrics = true,
            SyncedLyricsLrc = "[00:00.10]Binaural motion active"
        };

        using MockT3MultiModelRuntime mockRt = new();
        NeuralPipelineOrchestrator orch = new(mockRt, mockRt);
        using RIFFWaveReader reader1 = new(srcWav);
        reader1.ReadHeader();
        using CavernizeTrack track1 = new(reader1, Codec.PCM_LE, 0);

        // 1. Eclipsa IAMF with DynamicChoreoMode.Musical
        string outIamf = Path.Combine(tmpDir, "Cavern Lab - Eclipse (Eclipsa IAMF - AI - Motion Musical).iamf");
        StudioBatchExportJob iamfJob = new() {
            TrackName = "Eclipse",
            Preset = StudioExportPreset.EclipsaAudioIamf,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.EclipsaIamf,
                CompressionTier = StudioCompressionTier.MasterPcm,
                DynamicChoreoMode = SpatialChoreoMode.Musical,
                EnableAiSpatialUpmix = true
            },
            DynamicChoreoMode = SpatialChoreoMode.Musical,
            OutputPath = outIamf,
            BitDepth = BitDepth.Int16,
            NeuralRuntime = mockRt,
            NeuralOrchestrator = orch,
            NeuralConfig = new NeuralAudioConfig {
                OrchestrationMode = NeuralOrchestrationMode.Auto,
                StudioBundlePreset = NeuralEnsembleBundlePreset.StudioFullBand6Stem,
                OverlapFactor = 2,
                EnableTta = false,
                EnablePhaseFix = false,
                EnforceEnergyConservation = true,
                TrackCacheEnabled = false
            },
            Metadata = meta.Clone()
        };
        StudioBatchExportResult iamfRes = StudioBatchExporter.ExportSingle(track1, iamfJob);
        Assert(iamfRes.Succeeded && File.Exists(outIamf) && new FileInfo(outIamf).Length > 1024,
            $"Dynamic Eclipsa IAMF export failed: {iamfRes.ErrorMessage}");
        Assert(iamfRes.EffectiveChoreoMode == SpatialChoreoMode.Musical, $"IAMF EffectiveChoreoMode mismatch: {iamfRes.EffectiveChoreoMode}");

        // 2. Headphone 3D LosslessCompressed (.flac) with DynamicChoreoMode.Musical + Cover Art + Synced Lyrics
        using RIFFWaveReader reader2 = new(srcWav);
        reader2.ReadHeader();
        using CavernizeTrack track2 = new(reader2, Codec.PCM_LE, 0);
        string outFlac = Path.Combine(tmpDir, "Cavern Lab - Eclipse (Headphone 3D - Lossless - AI - Motion Musical).flac");
        StudioBatchExportJob flacJob = new() {
            TrackName = "Eclipse",
            Preset = StudioExportPreset.PersonalizedLosslessBinaural,
            ComposableOptions = new StudioComposableExportOptions {
                BaseFormat = StudioBaseOutputFormat.Headphone3D,
                CompressionTier = StudioCompressionTier.LosslessCompressed,
                DynamicChoreoMode = SpatialChoreoMode.Musical,
                EnableAiSpatialUpmix = true
            },
            DynamicChoreoMode = SpatialChoreoMode.Musical,
            OutputPath = outFlac,
            BitDepth = BitDepth.Int16,
            NeuralRuntime = mockRt,
            NeuralOrchestrator = orch,
            NeuralConfig = new NeuralAudioConfig {
                OrchestrationMode = NeuralOrchestrationMode.Auto,
                StudioBundlePreset = NeuralEnsembleBundlePreset.StudioFullBand6Stem,
                OverlapFactor = 2,
                EnableTta = false,
                EnablePhaseFix = false,
                EnforceEnergyConservation = true,
                TrackCacheEnabled = false
            },
            Metadata = meta.Clone()
        };
        StudioBatchExportResult flacRes = StudioBatchExporter.ExportSingle(track2, flacJob);
        Assert(flacRes.Succeeded && File.Exists(outFlac), $"Dynamic Headphone3D FLAC export failed: {flacRes.ErrorMessage}");
        Assert(flacRes.MetadataEmbedded && flacRes.CoverArtEmbedded && flacRes.LyricsEmbedded,
            $"Headphone3D FLAC metadata flags missing: meta={flacRes.MetadataEmbedded}, art={flacRes.CoverArtEmbedded}, lyr={flacRes.LyricsEmbedded}");
        Assert(double.IsFinite(flacRes.IntegratedLufs) && flacRes.IntegratedLufs > -70.0, $"Invalid Headphone3D LUFS: {flacRes.IntegratedLufs}");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// ---------- Test 32 (Stage 6 T6): ComponentBehaviorRegistry & SpatialSceneHub Governance ----------
RunTest("T32_T6_ComponentBehaviorRegistry_And_SceneHubGovernance", () => {
    SpatialSceneHub hub = new();
    ComponentBehaviorRegistry reg = hub.EnsureComponentBehaviorRegistry(48000);
    Assert(reg.Count >= 6, $"Expected >=6 registered profiles in SpatialSceneHub, got {reg.Count}");
    Assert(ReferenceEquals(reg, hub.ComponentRegistry), "Lazy ComponentRegistry getter must return cached instance.");

    string json = reg.ToCompactManifestJson();
    Assert(json.Contains("\"schema\":\"cavern-component-profiles-v1\"", StringComparison.Ordinal)
        && json.Contains("\"count\":6", StringComparison.Ordinal)
        && json.Contains("cavern.true_peak_limiter", StringComparison.Ordinal),
        $"Unexpected ComponentBehaviorRegistry manifest JSON: {json}");

    hub.IsMeasuredBrirActive = true;
    HardwarePreAmpStateSnapshot preAmp = new(
        airMode: ScarlettAirMode.PresenceAndDrive,
        driveActive: true,
        gainStepEpoch: 4,
        micSource: HardwarePreAmpStateSnapshot.MicSourceUsbInterface,
        isHardwareVerified: true);
    ChainGovernancePlan plan = hub.EvaluateActiveChainGovernance(
        [
            ComponentBehaviorProfiler.IdLiveVocalChain,
            ComponentBehaviorProfiler.IdAirHighShelf,
            ComponentBehaviorProfiler.IdTruePeakLimiter
        ],
        referenceInputRmsDb: -16.0f,
        preAmpSnapshot: preAmp);
    Assert(ReferenceEquals(plan, hub.ActiveChainGovernance), "ActiveChainGovernance must reflect latest evaluated plan.");
    Assert(plan.SkipSoftwareAir && plan.SkipSoftwareDrive && plan.SkipSyntheticRoom,
        "SceneHub governance must detect Scarlett Air+Drive and measured BRIR.");
    Assert(plan.IsTruePeakSafe && plan.PredictedPeakDbTp <= -0.95f,
        $"SceneHub governed peak must remain <= -1.0 dBTP, got {plan.PredictedPeakDbTp:0.2}");
    Assert(plan.ToCompactJson().Contains("\"tpSafe\":true", StringComparison.Ordinal),
        $"ChainGovernancePlan compact JSON missing tpSafe=true: {plan.ToCompactJson()}");
});

// ---------- Test 33 (Stage 6 T6): MultiRefFdkfShield, OmniscientVocalStateBus & AcousticConstraintGovernor ----------
RunTest("T33_T6_MultiRefFdkfShield_StateBus_And_HeadphoneBypass", () => {
    OmniscientVocalStateBus bus = new();
    OmniscientVocalStateSnapshot initial = bus.ReadSnapshot();
    Assert(initial.IsShieldBypassed && !initial.PhaseBActive && initial.GovernorMaxMakeupDb >= 11.9f,
        "Default OmniscientVocalStateBus snapshot must start passive/bypassed with PhaseBActive=false.");

    // Zero-GC publish/read roundtrip check
    OmniscientVocalStateSnapshot custom = new() {
        SpeakerBleedErleDb = 28.4f,
        EstimatedLoopGainLinear = 0.42f,
        EstimatedLoopMarginDb = 7.5f,
        GovernorMaxMakeupDb = 1.5f,
        ShadowCommitCount = 19,
        DoubleTalkActive = true,
        LastGainStepEpoch = 3,
        IsHeadphoneMonitoring = false,
        IsShieldBypassed = false,
        PhaseBActive = false
    };
    bus.PublishZeroGc(in custom);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 64; i++) {
        bus.PublishZeroGc(in custom);
        OmniscientVocalStateSnapshot r = bus.ReadSnapshot();
        Assert(r.ShadowCommitCount == 19 && r.DoubleTalkActive && !r.PhaseBActive, "StateBus snapshot corruption.");
    }
    long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert(alloc == 0, $"OmniscientVocalStateBus publish/read must allocate 0 B GC, got {alloc} B.");

    // AcousticConstraintGovernor band ceilings in speaker vs headphone mode
    AcousticConstraintGovernor gov = new(safetyMarginDb: 6.0f, headphoneMaxMakeupDb: 12.0f);
    float[] coupling = [0.2f, 0.35f, 0.70f, 0.25f, 0.15f, 0.10f, 0.08f, 0.05f];
    float spkMakeup;
    gov.EvaluateZeroGc(coupling, 12.0f, isHeadphoneMonitoring: false, out spkMakeup);
    Assert(spkMakeup < 0f && gov.CurrentLoopMarginDb >= 5.9f,
        $"Expected speaker governor to attenuate makeup below 0 dB for |H|=0.70, got makeup={spkMakeup:0.2}, margin={gov.CurrentLoopMarginDb:0.2}");

    float[] ceilings = new float[AcousticConstraintGovernor.SubBandCount];
    gov.CopyBandCeilingsZeroGc(ceilings);
    Assert(ceilings[2] < ceilings[7], "Modal resonance band [2] must have stricter gain ceiling than quiet band [7].");

    float hpMakeup;
    gov.EvaluateZeroGc(coupling, 12.0f, isHeadphoneMonitoring: true, out hpMakeup);
    Assert(MathF.Abs(hpMakeup - 12.0f) < 0.01f && gov.CurrentLoopMarginDb >= 20f,
        $"Headphone mode must relax governor back to +12 dB studio headroom, got {hpMakeup:0.2}");

    // MultiRefFdkfShield headphone auto-bypass bit-exactness
    MultiRefFdkfShield shield = new(128, 4, 3);
    float[] mic = new float[128];
    float[] orig = new float[128];
    float[] refBuf = new float[128];
    for (int i = 0; i < 128; i++) {
        mic[i] = orig[i] = 0.2f * MathF.Sin(i * 0.21f);
        refBuf[i] = 0.3f * MathF.Cos(i * 0.11f);
    }
    HardwarePreAmpStateSnapshot hpSnap = new(gainStepEpoch: 7, isHeadphoneMonitoring: true);
    shield.ProcessBlockZeroGc(mic, 0, refBuf, 0, refBuf, 0, refBuf, 0, hpSnap, gov, bus);
    Assert(shield.IsBypassed && shield.LastGainStepEpoch == 7, "Shield must bypass in headphone mode and track GainStepEpoch.");
    for (int i = 0; i < 128; i++) {
        Assert(mic[i] == orig[i], $"Headphone bypass altered sample[{i}].");
    }
});

// ---------- Test 34 (Stage 6 T6): Phase-A Side-by-Side Legacy Vocal Coexistence ----------
RunTest("T34_T6_PhaseA_SideBySide_LegacyVocalCoexistence", () => {
    using VocalMixMatchStageProcessor stage = new();
    Assert(ReferenceEquals(stage.PhaseBCalibrationHook, NullPhaseBCortexCalibrationHook.Instance),
        "VocalMixMatchStageProcessor must default to passive NullPhaseBCortexCalibrationHook.Instance in Phase A.");
    Assert(!stage.PhaseBCalibrationHook.IsCalibrated && stage.PhaseBCalibrationHook.GetPreferenceMakeupBiasDb() == 0f,
        "Passive Phase-A calibration hook must report IsCalibrated=false and 0 dB bias.");
    Assert(stage.VocalStateBus != null && !stage.VocalStateBus.ReadSnapshot().PhaseBActive,
        "VocalStateBus must be attached side-by-side with PhaseBActive=false.");

    // Verify legacy VocalMixMatchStageProcessor produces bit-exact identical output to standalone LiveVocalChainDsp
    const int sr = 48000;
    const int n = 256;
    VocalMixDnaProfile dna = VocalMixDnaProfile.CreateStudioDefault();
    VocalMacroSettings macros = new() {
        Enabled = true, Bypass = false, MixMatchPct = 100f, DynamicsPct = 80f, WarmthPct = 35f, ReverbPct = 20f
    };
    stage.Configure(sr, SpatialProgramMetadata.CreateDefault());
    stage.ApplyVocalProfile(dna, macros);

    LiveVocalChainDsp referenceDsp = new(sr);
    referenceDsp.Configure(sr, dna, macros);

    float[] a = new float[n * 2];
    float[] b = new float[n * 2];
    for (int i = 0; i < n; i++) {
        float s = 0.18f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * i / sr));
        a[i * 2] = a[i * 2 + 1] = s;
        b[i * 2] = b[i * 2 + 1] = s;
    }
    stage.ProcessLiveVocalZeroGc(a, 0, n);
    referenceDsp.ProcessStereoBlockZeroGc(b, 0, n);

    float maxDiff = 0f;
    for (int i = 0; i < a.Length; i++) {
        maxDiff = MathF.Max(maxDiff, MathF.Abs(a[i] - b[i]));
    }
    Assert(maxDiff < 1e-6f, $"Phase-A side-by-side vocal hook altered legacy DSP output: maxDiff={maxDiff}");
});

// ---------- Test 35 (BP UI/AI Q2): CNTC v3 PeekHeader SourceTrackKey/PlanSignature + Cache-First Reuse & ForceReextract ----------
RunTest("T35_BP_UiAi_Q2_CntcV3PeekHeader_And_CacheFirstReuse", () => {
    string tmpDir = Path.Combine(Path.GetTempPath(), "cavern_t35_cntc_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
        int sr = 44100;
        int n = 4096;
        float[] s = new float[n];
        for (int i = 0; i < n; i++) s[i] = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / sr);

        NeuralSeparatedTrack saved = new() {
            SourceTrackKey = "/storage/emulated/0/Music/Daft Punk - Instant Crush.flac",
            SampleRate = sr,
            PlanSignature = "v1|StemSeparation|MVSEP_Cloud_BSRoFormer6",
            OriginTag = "MVSEP Cloud",
            ModelSummary = "BS-RoFormer 6-Stem",
            DisplayTitle = "Instant Crush",
            ArtistName = "Daft Punk",
            Stems = [
                new StereoStemBuffer("vocals", s, s),
                new StereoStemBuffer("drums", s, s),
                new StereoStemBuffer("bass", s, s),
                new StereoStemBuffer("other", s, s)
            ]
        };

        string cntcPath = Path.Combine(tmpDir, "abc123def456.cnz");
        NeuralTrackCacheCodec.WriteTrackFile(cntcPath, saved, choreoTrack: null, NeuralTrackCacheCompressionMode.Compact16Bit);
        NeuralTrackCacheEntryInfo peek = NeuralTrackCacheCodec.PeekHeader(cntcPath);
        Assert(peek != null, "CNTC v3 PeekHeader returned null.");
        Assert(peek.SourceTrackKey == saved.SourceTrackKey, $"PeekHeader SourceTrackKey mismatch: '{peek.SourceTrackKey}'");
        Assert(peek.PlanSignature == saved.PlanSignature, $"PeekHeader PlanSignature mismatch: '{peek.PlanSignature}'");
        Assert(peek.DisplayTitle == "Instant Crush" && peek.ArtistName == "Daft Punk", "PeekHeader title/artist mismatch.");
        Assert(peek.StemCount == 4, $"Expected 4 stems in PeekHeader, got {peek.StemCount}");

        using MockT3MultiModelRuntime rt = new();
        MockCacheFirstTrackCache cache = new(saved);
        NeuralPipelineOrchestrator orch = new(rt, rt, cache);
        NeuralAudioConfig cfg = new() {
            OrchestrationMode = NeuralOrchestrationMode.Auto,
            TrackCacheEnabled = true
        };

        // Exact key misses (different model signature), but TryLoadBestForSourceTrack MUST return cache hit with 0 inference
        var hit = orch.ExecuteTrackPipeline(
            saved.SourceTrackKey,
            fileSizeBytes: 123456,
            lastModifiedTicks: 999999,
            mixLeft: s,
            mixRight: s,
            sampleRate: sr,
            goal: NeuralPipelineGoal.StemSeparation,
            config: cfg,
            displayTitle: "Instant Crush",
            artistName: "Daft Punk",
            forceReextract: false);
        Assert(hit.FromCache && ReferenceEquals(hit.Track, saved), "Expected cache-first reuse via TryLoadBestForSourceTrack on exact-key miss!");
        Assert(cache.BestLookupCalls == 1 && cache.SaveCalls == 0, $"Expected 1 best-lookup & 0 saves, got best={cache.BestLookupCalls}, save={cache.SaveCalls}");

        // Explicit forceReextract: true MUST bypass cache and execute fresh separation
        var fresh = orch.ExecuteTrackPipeline(
            saved.SourceTrackKey,
            fileSizeBytes: 123456,
            lastModifiedTicks: 999999,
            mixLeft: s,
            mixRight: s,
            sampleRate: sr,
            goal: NeuralPipelineGoal.StemSeparation,
            config: cfg,
            displayTitle: "Instant Crush",
            artistName: "Daft Punk",
            forceReextract: true);
        Assert(!fresh.FromCache && !ReferenceEquals(fresh.Track, saved), "forceReextract=true must bypass cached stems!");
        Assert(cache.SaveCalls == 1, $"Expected fresh extraction to persist new cache entry, got SaveCalls={cache.SaveCalls}");
    } finally {
        try { Directory.Delete(tmpDir, true); } catch { }
    }
});

// ---------- Test 36 (BP UI/AI Q4): Grouped Custom Ensemble Zero Chain-Lock & Auto Mode Isolation ----------
RunTest("T36_BP_UiAi_Q4_GroupedCustomEnsemble_ZeroChainLock", () => {
    using MockT3MultiModelRuntime rt = new();
    NeuralPipelineOrchestrator orch = new(rt, rt, trackCache: null);

    // Case 1: Manual mode with explicit VocalSeparation slot ONLY, even if legacy ActiveStemModelId still points to mock_6stem
    NeuralAudioConfig manualVocalsOnly = new() {
        OrchestrationMode = NeuralOrchestrationMode.Manual,
        ActiveStemModelId = "mock_6stem",
        TaskModelSlots = new Dictionary<NeuralModelTask, IReadOnlyList<string>> {
            [NeuralModelTask.VocalSeparation] = new[] { "mock_vocals_2s" },
            [NeuralModelTask.StemSeparation6] = Array.Empty<string>()
        }
    };
    NeuralExecutionPlan plan1 = orch.BuildPlan(NeuralPipelineGoal.StemSeparation, manualVocalsOnly);
    Assert(plan1.IsRunnable && plan1.Steps.Count == 1, $"Expected 1 explicit step, got {plan1.Steps.Count}");
    Assert(plan1.Steps[0].Models.Count == 1 && plan1.Steps[0].Models[0].Id == "mock_vocals_2s",
        $"Zero chain-lock violated: stale ActiveStemModelId resurrected mock_6stem! Got '{plan1.Steps[0].Models[0].Id}'");

    // Case 2: Manual mode with 2 models in the same task slot -> Ensemble -> removing 1 leaves exactly 1
    NeuralAudioConfig manualTwo = new() {
        OrchestrationMode = NeuralOrchestrationMode.Manual,
        ActiveStemModelId = "mock_6stem",
        TaskModelSlots = new Dictionary<NeuralModelTask, IReadOnlyList<string>> {
            [NeuralModelTask.StemSeparation6] = new[] { "mock_6stem" },
            [NeuralModelTask.VocalSeparation] = new[] { "mock_vocals_2s" }
        }
    };
    NeuralExecutionPlan plan2 = orch.BuildPlan(NeuralPipelineGoal.StemSeparation, manualTwo);
    Assert(plan2.Steps.Count >= 1 && plan2.Steps[0].Models[0].Id == "mock_6stem", "Multi-stem slot should take primary step when present.");

    // Case 3: Auto mode ignores stale manual slots and picks single optimal installed model cleanly
    NeuralAudioConfig autoCfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        TaskModelSlots = new Dictionary<NeuralModelTask, IReadOnlyList<string>> {
            [NeuralModelTask.VocalSeparation] = new[] { "mock_vocals_2s" }
        }
    };
    NeuralExecutionPlan autoPlan = orch.BuildPlan(NeuralPipelineGoal.StemSeparation, autoCfg);
    Assert(autoPlan.IsRunnable && autoPlan.Mode == NeuralOrchestrationMode.Auto, "Auto plan must be runnable in Auto mode.");
    Assert(autoPlan.Steps[0].Models.Count == 1 && autoPlan.Steps[0].Models[0].Id == "mock_6stem",
        $"Auto mode should pick optimal 6-stem checkpoint regardless of manual slot, got '{autoPlan.Steps[0].Models[0].Id}'");
});

// ---------- Test 37 (BP UI/AI Q5): Compute Headroom Governor (Eco / Balanced / Fast Max 75%) ----------
RunTest("T37_BP_UiAi_Q5_ComputeHeadroomGovernor_NonStarving", () => {
    int prevTier = MvsepAntiArtifactPipeline.GlobalComputeBudgetTier;
    try {
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8, 0) == 2, "8-core Eco must cap at 2 threads (<=25%).");
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8, 1) == 3, "8-core Balanced must cap at 3 threads (<=37.5%).");
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8, 2) == 4, "8-core Fast Max 75% must cap at 4 threads (never 100%).");
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(2, 2) == 1, "2-core device must cap at 1 background thread.");

        MvsepAntiArtifactPipeline.GlobalComputeBudgetTier = 0;
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8) == 2, "GlobalComputeBudgetTier=0 mismatch.");
        MvsepAntiArtifactPipeline.GlobalComputeBudgetTier = 1;
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8) == 3, "GlobalComputeBudgetTier=1 mismatch.");
        MvsepAntiArtifactPipeline.GlobalComputeBudgetTier = 2;
        Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8) == 4, "GlobalComputeBudgetTier=2 mismatch.");

        // Thermal + GPU breathing yield invariants
        Assert(MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(16, 25.0, 0) == 0, "Sub-50ms cool chunk must add 0ms yield.");
        Assert(MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(16, 200.0, 0) == 16, "Balanced 200ms chunk must yield >=16ms.");
        Assert(MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(16, 600.0, 0) == 32, "Heavy >=500ms chunk must yield >=32ms.");
        Assert(MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(16, 200.0, 60) == 60, "Thermal extra yield must elevate breathing yield.");
        Assert(MvsepAntiArtifactPipeline.ComputeEffectiveBreathingYieldMs(16, 200.0, 500) == 100, "Breathing yield must cap at 100ms.");
    } finally {
        MvsepAntiArtifactPipeline.GlobalComputeBudgetTier = prevTier;
    }
});

// ---------- Test 38 (BP UI/AI Q1 & Q3): Lyrics Alpha Clamp Safety & Motion Blur Velocity Curve ----------
RunTest("T38_BP_UiAi_Q1_Q3_AlphaClampSafety_And_VelocityBlurCurve", () => {
    // Q1: Replicate AppleMusicLyricsView clamp across extreme manualClarity=1.0 & low easedFocus=0.0..1.0
    for (int f = 0; f <= 100; f++) {
        float easedFocus = f / 100f;
        float manualClarity = 1.0f;
        int dimAlpha = (int)(255 * Math.Clamp(0.28f + 0.46f * manualClarity, 0.28f, 0.74f));
        int brightAlpha = Math.Max(dimAlpha, (int)(255 * Math.Clamp(0.28f + 0.72f * easedFocus, 0.30f, 1.0f)));
        Assert(brightAlpha >= dimAlpha, $"brightAlpha ({brightAlpha}) < dimAlpha ({dimAlpha}) at focus={easedFocus}");
        int clamped = Math.Clamp(180, dimAlpha, brightAlpha);
        Assert(clamped >= dimAlpha && clamped <= brightAlpha, "Clamped alpha out of bounds.");
    }

    // Q3: Replicate OneUiMotionBlur blended optical defocus + horizontal motion curve (density=3.0, width=1080px, 310ms fixed transition)
    static float ComputeBlurPx(float velPxPerSec, float density, float maxBlurDp = 14f) {
        if (!float.IsFinite(velPxPerSec) || !float.IsFinite(density) || density <= 0f) return 0f;
        float speedDpPerSec = MathF.Abs(velPxPerSec) / density;
        if (speedDpPerSec <= 240f) return 0f;
        float excess = speedDpPerSec - 240f;
        float rawDp = excess * 0.0036f;
        float maxDp = Math.Clamp(maxBlurDp, 3f, 20f);
        float curvedDp = maxDp * (1f - MathF.Exp(-rawDp / Math.Max(3.5f, maxDp * 0.72f)));
        float px = curvedDp * density;
        return px >= 0.75f ? px : 0f;
    }
    float density = 3.0f;
    Assert(ComputeBlurPx(0f, density) == 0f, "Rest velocity must produce 0px blur.");
    Assert(ComputeBlurPx(200f * density, density) == 0f, "Sub-deadzone velocity must produce 0px blur.");
    float v1Tab = (1080f / 0.310f);        // ~3484 px/s (~1161 dp/s)
    float v2Tabs = (2f * 1080f / 0.310f);  // ~6968 px/s (~2323 dp/s)
    float b1 = ComputeBlurPx(v1Tab, density);
    float b2 = ComputeBlurPx(v2Tabs, density);
    Assert(b1 > 0f && b2 > 0f, $"Expected positive blur for 1-tab ({b1}) and 2-tab ({b2}) jumps.");
    Assert(b2 >= b1 * 1.40f, $"2-tab peak blur ({b2:0.2}px) must be >= 140% of 1-tab peak blur ({b1:0.2}px).");
    float bMax = ComputeBlurPx(999999f, density, maxBlurDp: 14f);
    Assert(bMax <= 20f * density, $"Peak blur ({bMax}px) exceeded 20dp soft ceiling ({20f * density}px).");
    float blurY = Math.Clamp(Math.Clamp(b2, 0.75f, 52f) * 0.52f, 0.75f, 36f);
    Assert(blurY >= 0.75f && blurY < b2, $"Blended optical defocus Y ({blurY:0.2}px) must be >= 0.75px and softer than X ({b2:0.2}px).");
});

// ---------- Test 39 (W51.1 & W51.2): Zero-Alloc Karaoke Toggle, Blind Backing Selectivity & ReferenceSampleOffset ----------
RunTest("T39_W51_1_KaraokeZeroAllocToggle_BlindBackingSelectivity_And_Offset", () => {
    int sr = 48000;
    int frames = 4096;
    // Blind multichannel discrete-center selectivity test (5.1: FL, FR, FC, LFE, RL, RR)
    float[][] MakeTest51() {
        float[][] ch = new float[6][];
        for (int c = 0; c < 6; c++) ch[c] = new float[frames];
        for (int f = 0; f < frames; f++) {
            double t = f / (double)sr;
            float leadCenter = 0.55f * MathF.Sin((float)(2 * Math.PI * 520 * t));
            float backingWide = 0.35f * MathF.Sin((float)(2 * Math.PI * 1180 * t));
            ch[0][f] = backingWide + 0.42f * leadCenter; // FL: wide backing + harmonic vocal bleed
            ch[1][f] = -backingWide + 0.42f * leadCenter; // FR: wide backing + harmonic vocal bleed
            ch[2][f] = leadCenter;                        // FC: discrete center lead vocal
            ch[3][f] = 0f;                                // LFE
            ch[4][f] = 0.5f * backingWide + 0.25f * leadCenter; // RL
            ch[5][f] = -0.5f * backingWide + 0.25f * leadCenter; // RR
        }
        return ch;
    }

    bool[] isLfe = [false, false, false, true, false, false];
    bool[] frontMask = [true, true, true, false, false, false];
    bool[] strictCenter = [false, false, true, false, false, false];

    SpatialKaraokeEngine engKeep = new(sr) { IsEnabled = true, VocalLevel = 0f, PreserveBackingVocals = true };
    float[][] bufKeep = MakeTest51();
    engKeep.ProcessMultichannelInPlace(bufKeep, isLfe, frontMask, 6, frames, isStrictCenter: strictCenter);

    SpatialKaraokeEngine engPure = new(sr) { IsEnabled = true, VocalLevel = 0f, PreserveBackingVocals = false };
    float[][] bufPure = MakeTest51();
    engPure.ProcessMultichannelInPlace(bufPure, isLfe, frontMask, 6, frames, isStrictCenter: strictCenter);

    double keepSideEnergy = 0, pureSideEnergy = 0, keepCenterEnergy = 0;
    for (int f = frames / 2; f < frames; f++) {
        keepSideEnergy += bufKeep[0][f] * bufKeep[0][f] + bufKeep[1][f] * bufKeep[1][f];
        pureSideEnergy += bufPure[0][f] * bufPure[0][f] + bufPure[1][f] * bufPure[1][f];
        keepCenterEnergy += bufKeep[2][f] * bufKeep[2][f];
    }
    Assert(keepSideEnergy > pureSideEnergy * 1.15,
        $"Expected PreserveBackingVocals=true to preserve more non-center backing energy ({keepSideEnergy:F4} vs {pureSideEnergy:F4}).");
    engKeep.ResetAdaptationState();
    Assert(double.IsFinite(keepCenterEnergy), "Center energy must be finite.");

    // Zero-alloc toggle stress test on NeuralStemStageProcessor with 44.1 kHz stems -> 48 kHz output
    int stemSr = 44100;
    int stemLen = 44100 * 4;
    float[] leadL = new float[stemLen], leadR = new float[stemLen];
    float[] backL = new float[stemLen], backR = new float[stemLen];
    for (int i = 0; i < stemLen; i++) {
        double t = i / (double)stemSr;
        leadL[i] = 0.4f * MathF.Sin((float)(2 * Math.PI * 440 * t));
        leadR[i] = leadL[i];
        backL[i] = 0.25f * MathF.Sin((float)(2 * Math.PI * 660 * t));
        backR[i] = -backL[i];
    }
    NeuralSeparatedTrack sepTrack = new() {
        SourceTrackKey = "w51_test.flac",
        SampleRate = stemSr,
        Stems = [
            new StereoStemBuffer("lead_vocal", leadL, leadR),
            new StereoStemBuffer("backing_vocals", backL, backR)
        ]
    };
    NeuralStemStageProcessor proc = new();
    proc.Configure(48000, new SpatialProgramMetadata());
    proc.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = 0f, KaraokePreserveBackingVocals = true });
    proc.BindKaraokeReferenceTrack(sepTrack);
    proc.ReferenceSampleOffset = 512.5;
    Assert(Math.Abs(proc.ReferenceSampleOffset - 512.5) < 0.001, "ReferenceSampleOffset setter failed.");

    float[] block = new float[512 * 2];
    // Warm up scratch buffers once
    proc.ProcessStereoInterleavedKaraoke(block, 0, 512, 1024);
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int iter = 0; iter < 20; iter++) {
        bool keepBacking = (iter % 2) == 0;
        float vLvl = (iter % 4) * 0.25f;
        proc.ApplyConfig(new NeuralAudioConfig { SpatialKaraokeEnabled = true, KaraokeVocalLevel = vLvl, KaraokePreserveBackingVocals = keepBacking });
        for (int i = 0; i < 512; i++) {
            float s = 0.4f * MathF.Sin(2f * MathF.PI * 440f * (1024 + iter * 512 + i) / 48000f);
            block[i * 2] = s;
            block[i * 2 + 1] = s;
        }
        proc.ProcessStereoInterleavedKaraoke(block, 0, 512, 1024 + iter * 512);
        for (int i = 0; i < block.Length; i++) {
            if (!float.IsFinite(block[i])) {
                Assert(false, $"Non-finite sample after toggle iter {iter}.");
            }
        }
    }
    long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Assert(allocDelta < 128 * 1024, $"Expected <128 KB managed allocation across 20 vocal/backing toggles, got {allocDelta} bytes.");
});

// ---------- Test 40 (W51.3): Structured Plan Signature & AI Cache Config Diff Inspector ----------
RunTest("T40_W51_3_NeuralCacheConfigDiff_StructuredDiffs_And_LegacyFallback", () => {
    using MockT3MultiModelRuntime mockRt = new();
    NeuralPipelineOrchestrator orch = new(mockRt, mockRt);
    NeuralAudioConfig baseCfg = new NeuralAudioConfig {
        ExecutionBackend = NeuralExecutionBackend.OnDevice,
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        OverlapFactor = 2,
        EnableTta = false,
        EnablePhaseFix = true,
        EnforceEnergyConservation = true,
        KaraokePreserveBackingVocals = true
    }.Sanitize();

    NeuralExecutionPlan planA = orch.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, baseCfg);
    string sigA = planA.PlanSignature;
    Assert(sigA.Contains("|cfg:v1;", StringComparison.Ordinal), $"Expected structured cfg token in signature: {sigA}");

    NeuralTrackCacheEntryInfo entryA = new() {
        CacheKey = "k1",
        SourceTrackKey = "/storage/emulated/0/Music/01. Lavender Haze.m4a.mp4",
        PlanSignature = sigA,
        OriginTag = "Local AI",
        StemCount = 2,
        CompressedBytes = 12 * 1024 * 1024
    };

    NeuralCacheConfigComparison sameCmp = NeuralCacheConfigDiff.Compare(
        entryA, baseCfg, planA, "bs_roformer_vocals", currentUsesCloud: false, NeuralPipelineGoal.SpatialKaraoke);
    Assert(sameCmp.MatchesCurrentConfig, "Identical config must report MatchesCurrentConfig=true.");
    Assert(sameCmp.Differences.Count == 0, $"Expected 0 diffs, got {sameCmp.Differences.Count}.");
    Assert(sameCmp.SourceFormatLabel.Contains("Atmos", StringComparison.OrdinalIgnoreCase),
        $"Expected Atmos format label for .m4a.mp4, got '{sameCmp.SourceFormatLabel}'.");

    NeuralAudioConfig changedCfg = new NeuralAudioConfig {
        ExecutionBackend = NeuralExecutionBackend.OnDevice,
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        OverlapFactor = 4,
        EnableTta = true,
        EnablePhaseFix = true,
        EnforceEnergyConservation = true,
        KaraokePreserveBackingVocals = false
    }.Sanitize();
    NeuralExecutionPlan planChanged = orch.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, changedCfg);
    NeuralCacheConfigComparison diffCmp = NeuralCacheConfigDiff.Compare(
        entryA, changedCfg, planChanged, "bs_roformer_vocals", currentUsesCloud: false, NeuralPipelineGoal.SpatialKaraoke);
    Assert(!diffCmp.MatchesCurrentConfig, "Changed overlap/TTA/backing config must report MatchesCurrentConfig=false.");
    Assert(diffCmp.Differences.Any(d => d.FieldKey == "Overlap"), "Missing 'Overlap' diff.");
    Assert(diffCmp.Differences.Any(d => d.FieldKey == "TTA"), "Missing 'TTA' diff.");
    Assert(diffCmp.Differences.Any(d => d.FieldKey == "BackingVocals"), "Missing 'BackingVocals' diff.");

    NeuralAudioConfig cloudCfg = new NeuralAudioConfig {
        ExecutionBackend = NeuralExecutionBackend.MvsepCloud,
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        OverlapFactor = 2,
        KaraokePreserveBackingVocals = true
    }.Sanitize();
    NeuralCacheConfigComparison cloudCmp = NeuralCacheConfigDiff.Compare(
        entryA, cloudCfg, planA, "bs_roformer_vocals",
        currentUsesCloud: true, NeuralPipelineGoal.SpatialKaraoke);
    Assert(!cloudCmp.MatchesCurrentConfig && cloudCmp.Differences.Any(d => d.FieldKey == "Backend"),
        "Expected 'Backend' diff when switching Local AI -> MVSEP Cloud.");
});

// ---------- Test 41 (W51.6): Sample-0 Primed Loudness & Cold-Start Downward Safety Guard ----------
RunTest("T41_W51_6_Bs1770Sample0PrimedLoudness_And_ColdStartDownwardGuard", () => {
    int sr = 48000;
    int frames = 2048;
    // -7.0 LUFS loud FLAC under -18.0 LUFS target => needs -11.0 dB gain (0.2818x) on sample 0!
    Bs1770LoudnessPostProcessingStage primed = new();
    primed.Configure(sr, 2);
    primed.ApplyParameters(LoudnessNormalizationMode.AtmosMusicMinus18, respectDialnorm: false);
    primed.PrimeInitialTrackLoudnessLufs(-7.0f);

    float[] loudBuf = new float[frames * 2];
    for (int i = 0; i < frames; i++) {
        float s = 0.65f * MathF.Sin(2f * MathF.PI * 1000f * i / sr);
        loudBuf[i * 2] = s;
        loudBuf[i * 2 + 1] = s;
    }
    primed.ProcessInterleaved(loudBuf, 0, frames, 2, null);

    // Measure first 256 frames (5.3 ms) RMS vs input RMS (0.65 / sqrt(2) = 0.4596)
    double sumSq = 0;
    for (int i = 0; i < 256; i++) sumSq += loudBuf[i * 2] * loudBuf[i * 2];
    double outRms = Math.Sqrt(sumSq / 256.0);
    double inRms = 0.65 / Math.Sqrt(2.0);
    double appliedDb = 20.0 * Math.Log10(Math.Max(1e-9, outRms / inRms));
    Assert(Math.Abs(appliedDb - (-11.0)) <= 0.6,
        $"Primed sample-0 gain must be ~-11.0 dB (got {appliedDb:F2} dB — volume spike regression!).");

    // Unprimed cold-start downward-only safety guard:
    // 1) Loud -6 dBFS input must be attenuated downward quickly (within 40 ms) before 400 ms BS.1770 integrates
    Bs1770LoudnessPostProcessingStage coldLoud = new();
    coldLoud.Configure(sr, 2);
    coldLoud.ApplyParameters(LoudnessNormalizationMode.AtmosMusicMinus18, respectDialnorm: false);
    float[] coldLoudBuf = new float[2048 * 2];
    for (int i = 0; i < 2048; i++) {
        float s = 0.85f * MathF.Sin(2f * MathF.PI * 1000f * i / sr);
        coldLoudBuf[i * 2] = s;
        coldLoudBuf[i * 2 + 1] = s;
    }
    coldLoud.ProcessInterleaved(coldLoudBuf, 0, 2048, 2, null);
    double tailSq = 0;
    for (int i = 1536; i < 2048; i++) tailSq += coldLoudBuf[i * 2] * coldLoudBuf[i * 2];
    double tailDb = 20.0 * Math.Log10(Math.Max(1e-9, Math.Sqrt(tailSq / 512.0) / (0.85 / Math.Sqrt(2.0))));
    Assert(tailDb < -3.0, $"Cold-start downward safety guard failed to tame loud unprimed input (got {tailDb:F2} dB).");

    // 2) Quiet unprimed intro (-34 dBFS) must NEVER be boosted cold (appliedDb <= +0.25 dB)
    Bs1770LoudnessPostProcessingStage coldQuiet = new();
    coldQuiet.Configure(sr, 2);
    coldQuiet.ApplyParameters(LoudnessNormalizationMode.AtmosMusicMinus18, respectDialnorm: false);
    float[] coldQuietBuf = new float[2048 * 2];
    for (int i = 0; i < 2048; i++) {
        float s = 0.02f * MathF.Sin(2f * MathF.PI * 1000f * i / sr);
        coldQuietBuf[i * 2] = s;
        coldQuietBuf[i * 2 + 1] = s;
    }
    coldQuiet.ProcessInterleaved(coldQuietBuf, 0, 2048, 2, null);
    double quietSq = 0;
    for (int i = 1536; i < 2048; i++) quietSq += coldQuietBuf[i * 2] * coldQuietBuf[i * 2];
    double quietDb = 20.0 * Math.Log10(Math.Max(1e-9, Math.Sqrt(quietSq / 512.0) / (0.02 / Math.Sqrt(2.0))));
    Assert(quietDb <= 0.25, $"Cold-start safety guard must never boost quiet unprimed intros (got {quietDb:F2} dB).");
});

// ---------- Test 42 (W51.7): Linked SystemPlaybackVolume01 -> ISO 226 Coupling (Zero Double Attenuation) ----------
RunTest("T42_W51_7_SystemPlaybackVolume_Iso226Coupling_ZeroDoubleAttenuation", () => {
    using RealtimePlayerEngine engine = new();
    engine.Volume = 1.0f;
    engine.SystemPlaybackVolume01 = 0.35f;
    Assert(Math.Abs(engine.Volume - 1.0f) < 0.0001f, "Software DSP Volume must remain 1.0f when linked to Android Stream.Music.");
    Assert(Math.Abs(engine.SystemPlaybackVolume01 - 0.35f) < 0.0001f, "SystemPlaybackVolume01 setter failed.");
    engine.SystemPlaybackVolume01 = 1.5f;
    Assert(Math.Abs(engine.SystemPlaybackVolume01 - 1.0f) < 0.0001f, "SystemPlaybackVolume01 must clamp to [0..1].");
    engine.SystemPlaybackVolume01 = -0.2f;
    Assert(Math.Abs(engine.SystemPlaybackVolume01 - 0.0f) < 0.0001f, "SystemPlaybackVolume01 must clamp negative values to 0.");
});

// ---------- Test 43: RoFormer / Heavy ONNX Low-RAM Memory Discipline & Automatic Lightweight Fallback ----------
RunTest("T43_NeuralLowRamMemoryDisciplineAndFallback", () => {
    // 1. Catalog heavy-model detection & WithMemorySafeFallback() contract
    NeuralModelCatalogEntry roformer = new() {
        Id = "mel_band_roformer_vocals_kim",
        DisplayName = "Mel-Band RoFormer Vocals",
        FileName = "melband_roformer_vocals.onnx",
        ApproxSizeMb = 953f,
        IsMobileFast = false,
        Task = NeuralModelTask.VocalSeparation
    };
    NeuralModelCatalogEntry scnet = new() {
        Id = "scnet_4stem_fp16",
        DisplayName = "SCNet 4-Stem FP16",
        FileName = "scnet_4stem_fp16.onnx",
        ApproxSizeMb = 22f,
        IsMobileFast = true,
        Task = NeuralModelTask.StemSeparation4
    };
    Assert(NeuralModelCatalog.IsMemoryHeavyModel(roformer), "RoFormer 953 MB must be classified as memory-heavy.");
    Assert(!NeuralModelCatalog.IsMemoryHeavyModel(scnet), "SCNet 22 MB FP16 must be classified as lightweight.");
    Assert(NeuralModelCatalog.IsMemoryHeavyModel(scnet, 250L * 1024L * 1024L), "File size >= 200 MB must trigger heavy classification.");

    NeuralAudioConfig heavyCfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Manual,
        StudioBundlePreset = NeuralEnsembleBundlePreset.StudioFullBand6Stem,
        ActiveStemModelId = roformer.Id,
        OverlapFactor = 4,
        EnableTta = true,
        TaskModelSlots = new Dictionary<NeuralModelTask, IReadOnlyList<string>> {
            [NeuralModelTask.VocalSeparation] = [roformer.Id]
        }
    };
    NeuralAudioConfig safeCfg = heavyCfg.WithMemorySafeFallback();
    Assert(safeCfg.OrchestrationMode == NeuralOrchestrationMode.Auto, "WithMemorySafeFallback must reset OrchestrationMode to Auto.");
    Assert(safeCfg.StudioBundlePreset == NeuralEnsembleBundlePreset.FastSingle, "WithMemorySafeFallback must reset StudioBundlePreset to FastSingle.");
    Assert(safeCfg.OverlapFactor == MvsepPipelineOptions.MinOverlap, "WithMemorySafeFallback must clamp OverlapFactor to MinOverlap.");
    Assert(!safeCfg.EnableTta, "WithMemorySafeFallback must disable TTA.");
    Assert(safeCfg.ActiveStemModelId == NeuralModelCatalog.DefaultStemModelId, "WithMemorySafeFallback must reset ActiveStemModelId.");
    Assert(!safeCfg.GetModelsForTask(NeuralModelTask.VocalSeparation).Contains(roformer.Id), "WithMemorySafeFallback must drop heavy manual vocal slot.");

    // 2. BuildPlan(..., preferLightweightOnly: true) strictly prefers lightweight SCNet over installed heavy RoFormer
    using MockLowRamFallbackRuntime rt = new(includeLightweightModel: true);
    NeuralPipelineOrchestrator orch = new(rt, rt);
    NeuralExecutionPlan lightPlan = orch.BuildPlan(NeuralPipelineGoal.SpatialKaraoke, heavyCfg, preferLightweightOnly: true);
    Assert(lightPlan.Steps.Count == 1 && lightPlan.Steps[0].Models.Count == 1, "Lightweight plan must resolve to a single fast step.");
    Assert(lightPlan.Steps[0].Models[0].Id == "scnet_4stem_fp16", $"Expected lightweight fallback SCNet, got {lightPlan.Steps[0].Models[0].Id}");

    // 3. ExecuteTrackPipeline automatically catches InsufficientMemoryException from heavy RoFormer and completes via SCNet
    const int sr = 44100;
    const int n = 44100;
    float[] mixL = new float[n], mixR = new float[n];
    for (int i = 0; i < n; i++) {
        mixL[i] = 0.3f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * i / sr));
        mixR[i] = 0.3f * MathF.Cos((float)(2.0 * Math.PI * 220.0 * i / sr));
    }
    NeuralAudioConfig autoCfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        OverlapFactor = 2,
        EnableTta = false,
        TrackCacheEnabled = false
    };
    (NeuralSeparatedTrack recovered, _, bool fromCache) = orch.ExecuteTrackPipeline(
        "t43_lowram_track.flac", 0L, 0L, mixL, mixR, sr, NeuralPipelineGoal.SpatialKaraoke, autoCfg, displayTitle: "LowRAM Song");
    Assert(!fromCache && recovered != null && recovered.Stems.Count >= 4, "Expected recovered stems from lightweight fallback.");
    Assert(recovered.UsedMemorySafeFallback, "Recovered track must flag UsedMemorySafeFallback=true.");
    Assert(recovered.OriginTag == "Local AI (Low-RAM Fallback)", $"Unexpected OriginTag: {recovered.OriginTag}");
    Assert(!string.IsNullOrWhiteSpace(recovered.MemoryFallbackReason), "Recovered track must preserve MemoryFallbackReason.");
    Assert(rt.HeavyRejectCount == 1 && rt.LightSuccessCount == 1, $"Expected 1 heavy reject + 1 light success, got heavy={rt.HeavyRejectCount}, light={rt.LightSuccessCount}");

    // 4. When ONLY a heavy model is installed, ExecuteTrackPipeline must rethrow InsufficientMemoryException cleanly without infinite loop
    using MockLowRamFallbackRuntime heavyOnlyRt = new(includeLightweightModel: false);
    NeuralPipelineOrchestrator heavyOnlyOrch = new(heavyOnlyRt, heavyOnlyRt);
    bool threwMemEx = false;
    try {
        heavyOnlyOrch.ExecuteTrackPipeline(
            "t43_heavy_only.flac", 0L, 0L, mixL, mixR, sr, NeuralPipelineGoal.SpatialKaraoke, autoCfg);
    } catch (InsufficientMemoryException) {
        threwMemEx = true;
    }
    Assert(threwMemEx, "Expected InsufficientMemoryException when no lightweight fallback model is installed.");
    Assert(heavyOnlyRt.HeavyRejectCount == 1, $"Expected single attempt without retry loop, got {heavyOnlyRt.HeavyRejectCount}");
});

// =========================================================================
// T44: W53.1 Wave A (B2 Low-Band Sub-50Hz HP + Stem Align, B3 CrossVariantAligner + DriftPpm, B4 MVSEP 2-Stage Cascade)
// =========================================================================
RunTest("T44_W53_1_MvsepTwoStageCascade_LowBandFix_And_CrossVariantAligner", () => {
    const int sr = 44100;
    const int n = sr * 2; // 2.0 s
    float[] leadL = new float[n], leadR = new float[n];
    float[] backL = new float[n], backR = new float[n];
    float[] bassL = new float[n], bassR = new float[n];
    float[] drumsL = new float[n], drumsR = new float[n];
    float[] silentL = new float[n], silentR = new float[n];
    float[] vocL = new float[n], vocR = new float[n];
    float[] mixL = new float[n], mixR = new float[n];

    Random rng = new(53144);
    for (int i = 0; i < n; i++) {
        double t = i / (double)sr;
        float lead = 0.24f * MathF.Sin((float)(2 * Math.PI * 220.0 * t)) + 0.16f * MathF.Sin((float)(2 * Math.PI * 660.0 * t));
        float back = 0.14f * MathF.Sin((float)(2 * Math.PI * 330.0 * t + 0.6));
        float bass = 0.30f * MathF.Sin((float)(2 * Math.PI * 65.0 * t)) + 0.18f * (float)(rng.NextDouble() * 2.0 - 1.0) * ((i % 4410) < 120 ? 1f : 0.15f);
        float drums = 0.18f * MathF.Sin((float)(2 * Math.PI * 140.0 * t));
        leadL[i] = leadR[i] = lead;
        backL[i] = back;
        backR[i] = -back;
        vocL[i] = leadL[i] + backL[i];
        vocR[i] = leadR[i] + backR[i];
        bassL[i] = bassR[i] = bass;
        drumsL[i] = drumsR[i] = drums;
        mixL[i] = vocL[i] + bassL[i] + drumsL[i];
        mixR[i] = vocR[i] + bassR[i] + drumsR[i];
    }

    // 1. B4 MVSEP 2-Stage Custom Cascade: Stage 1 (sep_type=63) -> Stage 2 (sep_type=49, opt1=8, opt2=0 "Use as is" on isolated vocals ONLY)
    byte[] s1VocWav = MvsepCloudClient.EncodeStereoWav16(vocL, vocR, sr);
    byte[] s1BassWav = MvsepCloudClient.EncodeStereoWav16(bassL, bassR, sr);
    byte[] s1DrumsWav = MvsepCloudClient.EncodeStereoWav16(drumsL, drumsR, sr);
    byte[] s1SilentWav = MvsepCloudClient.EncodeStereoWav16(silentL, silentR, sr);
    byte[] s2LeadWav = MvsepCloudClient.EncodeStereoWav16(leadL, leadR, sr);
    byte[] s2BackWav = MvsepCloudClient.EncodeStereoWav16(backL, backR, sr);

    using MockMvsepCascadeHttpHandler cascadeHandler = new(s1VocWav, s1BassWav, s1DrumsWav, s1SilentWav, s2LeadWav, s2BackWav);
    using MvsepCloudClient client = new(cascadeHandler);
    MvsepCloudOptions opts = new() {
        ApiToken = "test_cascade_token_531",
        BaseUrl = MvsepCloudOptions.DefaultBaseUrl,
        OutputFormat = MvsepOutputFormat.Wav16Bit,
        PollIntervalMs = 20,
        MaxPollSeconds = 10
    };
    MvsepAlgorithmPreset cascadePreset = MvsepAlgorithmPreset.FindOrDefault("mvsep_cascade_6stem_karaoke");
    Assert(cascadePreset.IsTwoStageCascade && cascadePreset.SeparationType == 63, "mvsep_cascade_6stem_karaoke preset contract mismatch");

    List<string> createdHashes = [];
    List<string> terminalHashes = [];
    NeuralSeparatedTrack cascadeOut = client.SeparateTwoStageCascadeAsync(
        sourceTrackKey: "song_cascade.flac",
        mixLeft: mixL,
        mixRight: mixR,
        sampleRate: sr,
        stage1Preset: cascadePreset,
        options: opts,
        displayTitle: "Cascade Song",
        artistName: "Cavern",
        onStage1JobCreated: h => createdHashes.Add("s1:" + h),
        onStage1JobTerminal: h => terminalHashes.Add("s1:" + h),
        onStage2JobCreated: h => createdHashes.Add("s2:" + h),
        onStage2JobTerminal: h => terminalHashes.Add("s2:" + h)).GetAwaiter().GetResult();

    Assert(cascadeHandler.CreateCalls.Count == 2, $"Expected exactly 2 MVSEP create calls (Stage 1 + Stage 2), got {cascadeHandler.CreateCalls.Count}");
    Assert(cascadeHandler.CreateCalls[0].SepType == "63", $"Stage 1 must use sep_type=63, got {cascadeHandler.CreateCalls[0].SepType}");
    Assert(cascadeHandler.CreateCalls[1].SepType == "49" && cascadeHandler.CreateCalls[1].Opt1 == "8" && cascadeHandler.CreateCalls[1].Opt2 == "0",
        $"Stage 2 must use sep_type=49, add_opt1=8, add_opt2=0 ('Use as is'), got sep={cascadeHandler.CreateCalls[1].SepType}, o1={cascadeHandler.CreateCalls[1].Opt1}, o2={cascadeHandler.CreateCalls[1].Opt2}");

    // Prove Stage 2 uploaded ONLY isolated Stage-1 vocals (NOT full mix containing 65 Hz bass!)
    Assert(MvsepCloudClient.TryDecodePcmWavStereo(cascadeHandler.CreateCalls[1].AudioBytes, out float[] upL, out float[] upR, out int upSr),
        "Stage 2 uploaded payload must be valid PCM WAV");
    double mixRe = 0, mixIm = 0, upRe = 0, upIm = 0;
    for (int i = 0; i < n; i++) {
        double ang = 2 * Math.PI * 65.0 * i / sr;
        double c65 = Math.Cos(ang);
        double s65 = Math.Sin(ang);
        mixRe += mixL[i] * c65;
        mixIm += mixL[i] * s65;
        upRe += upL[i] * c65;
        upIm += upL[i] * s65;
    }
    double bassInMix = Math.Sqrt(mixRe * mixRe + mixIm * mixIm);
    double bassInStage2Upload = Math.Sqrt(upRe * upRe + upIm * upIm);
    double bassLeakRatio = bassInStage2Upload / Math.Max(1e-9, bassInMix);
    Assert(bassLeakRatio < 0.05, $"Stage 2 MUST upload vocals-only stem (not full mix!); 65 Hz bass leak ratio={bassLeakRatio:0.4}");

    // Verify V4.1 zero-duplicate vocal emission (Stage-1 combined 'vocals' stripped once split)
    // AND exact vocal energy conservation: lead_vocal + backing_vocals == Stage-1 isolated vocals (upL/upR)
    StereoStemBuffer outVoc = cascadeOut.FindStem("vocals");
    StereoStemBuffer outLead = cascadeOut.FindStem("lead_vocal");
    StereoStemBuffer outBack = cascadeOut.FindStem("backing_vocals");
    Assert(outVoc == null, "V4.1: Cascade output must NOT retain redundant Stage-1 'vocals' alongside 'lead_vocal' + 'backing_vocals'");
    Assert(outLead != null && outBack != null, "Cascade output must include lead_vocal and backing_vocals");
    Assert(cascadeOut.FindStem("piano") == null, "Silent piano stem must be pruned via IsStemAudible");
    Assert(cascadeOut.FindStem("bass") != null && cascadeOut.FindStem("drums") != null, "Audible Stage 1 stems (bass, drums) must be preserved");
    float maxConsErr = 0f;
    for (int i = 0; i < n; i++) {
        maxConsErr = MathF.Max(maxConsErr, MathF.Abs((outLead.Left[i] + outBack.Left[i]) - upL[i]));
        maxConsErr = MathF.Max(maxConsErr, MathF.Abs((outLead.Right[i] + outBack.Right[i]) - upR[i]));
    }
    Assert(maxConsErr < 1e-3f, $"Vocal energy conservation failed: max |lead+back - stage1_vocals| = {maxConsErr}");
    Assert(createdHashes.Contains("s1:cascade_s1") && createdHashes.Contains("s2:cascade_s2"), "Independent :s1/:s2 job creation callbacks missing");
    Assert(terminalHashes.Contains("s1:cascade_s1") && terminalHashes.Contains("s2:cascade_s2"), "Independent :s1/:s2 terminal callbacks missing");

    // 2. Verify Stage-1 reuse + Stage-2 cancel-safe resume spend 0 duplicate create calls!
    int createsBeforeResume = cascadeHandler.CreateCalls.Count;
    NeuralSeparatedTrack resumed = client.SeparateTwoStageCascadeAsync(
        sourceTrackKey: "song_cascade.flac",
        mixLeft: mixL,
        mixRight: mixR,
        sampleRate: sr,
        stage1Preset: cascadePreset,
        options: opts,
        preExtractedStage1Track: cascadeOut,
        existingStage2JobHash: "cascade_s2").GetAwaiter().GetResult();
    Assert(cascadeHandler.CreateCalls.Count == createsBeforeResume,
        $"Resuming Stage-2 hash with cached Stage-1 must issue 0 new create calls, issued {cascadeHandler.CreateCalls.Count - createsBeforeResume}");
    Assert(resumed.FindStem("lead_vocal") != null, "Resumed cascade must still produce lead_vocal");

    // 3. B2 Zero-phase sub-50 Hz high-pass check (attenuates 30 Hz >= 18 dB, preserves 110 Hz within 0.5 dB, 0-sample phase shift)
    float[] hpTest = new float[sr];
    float[] ref110 = new float[sr];
    for (int i = 0; i < sr; i++) {
        double t = i / (double)sr;
        float s110 = 0.5f * MathF.Sin((float)(2 * Math.PI * 110.0 * t));
        ref110[i] = s110;
        hpTest[i] = s110 + 0.5f * MathF.Sin((float)(2 * Math.PI * 30.0 * t));
    }
    MvsepCloudClient.ApplyZeroPhaseSubVocalHighPass(hpTest, sr, 50f);
    double p30Before = 0, p30After = 0, re110 = 0, im110 = 0, p110Ref = 0;
    for (int i = sr / 4; i < (3 * sr) / 4; i++) {
        double t = i / (double)sr;
        double s30 = Math.Sin(2 * Math.PI * 30.0 * t);
        double s110 = Math.Sin(2 * Math.PI * 110.0 * t);
        double c110 = Math.Cos(2 * Math.PI * 110.0 * t);
        p30Before += 0.5 * s30 * s30;
        p30After += hpTest[i] * s30;
        re110 += hpTest[i] * s110;
        im110 += hpTest[i] * c110;
        p110Ref += 0.5 * s110 * s110;
    }
    double att30Db = 20.0 * Math.Log10(Math.Max(1e-9, Math.Abs(p30After)) / Math.Max(1e-9, p30Before));
    double mag110 = Math.Sqrt(re110 * re110 + im110 * im110);
    double gain110Db = 20.0 * Math.Log10(Math.Max(1e-9, mag110) / Math.Max(1e-9, p110Ref));
    double phase110Deg = Math.Abs(Math.Atan2(im110, re110) * (180.0 / Math.PI));
    Assert(att30Db <= -18.0, $"Sub-50 Hz HP must attenuate 30 Hz by >= 18 dB, got {att30Db:F2} dB");
    Assert(Math.Abs(gain110Db) <= 0.5, $"Sub-50 Hz HP must preserve 110 Hz within +/-0.5 dB, got {gain110Db:F2} dB");
    Assert(phase110Deg <= 0.5, $"Zero-phase HP must preserve 110 Hz with ~0.0 deg phase shift, got {phase110Deg:F3} deg");

    // 4. B2 GCC-PHAT priming shift recovery (+576 samples MP3 padding -> aligned back to 0.0)
    const int primeDelay = 576;
    float[] delayedVocL = new float[n], delayedVocR = new float[n];
    float[] delayedBassL = new float[n], delayedBassR = new float[n];
    for (int i = primeDelay; i < n; i++) {
        delayedVocL[i] = vocL[i - primeDelay];
        delayedVocR[i] = vocR[i - primeDelay];
        delayedBassL[i] = bassL[i - primeDelay] + drumsL[i - primeDelay];
        delayedBassR[i] = bassR[i - primeDelay] + drumsR[i - primeDelay];
    }
    List<StereoStemBuffer> shiftedStems = [
        new StereoStemBuffer("vocals", delayedVocL, delayedVocR),
        new StereoStemBuffer("instrumental", delayedBassL, delayedBassR)
    ];
    double recoveredShift = MvsepCloudClient.AlignDownloadedStemsToReference(mixL, mixR, shiftedStems, sr);
    Assert(Math.Abs(recoveredShift - primeDelay) <= 1.0,
        $"AlignDownloadedStemsToReference must recover +{primeDelay}-sample priming delay, got {recoveredShift:0.2}");

    // 5. B3 ReferenceDriftPpm property clamping & ResolveForGoal cascade preference
    NeuralStemStageProcessor stage = new();
    stage.ReferenceDriftPpm = 25.0;
    Assert(Math.Abs(stage.ReferenceDriftPpm - 25.0) < 1e-6, "ReferenceDriftPpm setter mismatch");
    stage.ReferenceDriftPpm = 9999.0;
    Assert(Math.Abs(stage.ReferenceDriftPpm - 500.0) < 1e-6, "ReferenceDriftPpm clamp mismatch");
    MvsepAlgorithmPreset autoKara = MvsepAlgorithmPreset.ResolveForGoal(NeuralPipelineGoal.SpatialKaraoke, true, "mvsep_bs_roformer_vocals");
    Assert(autoKara.IsTwoStageCascade, $"Expected SpatialKaraoke+preserveBacking to auto-resolve 2-stage cascade, got {autoKara.Id}");
});

// T45: W53.2 Wave B (CNTC v3 Lossless Mono-Ref Dedup + Optimal Compaction + Incremental Stage-1 Vocal Reuse)
RunTest("T45_W53_2_CntcV3MonoRefDedup_CompactStorage_And_IncrementalVocalReuse", () => {
    int sr = 44100;
    int len = sr; // 1.0 s
    float[] leadL = new float[len], leadR = new float[len];
    float[] backL = new float[len], backR = new float[len];
    float[] instrL = new float[len], instrR = new float[len];
    float[] monoLead = new float[len], monoBack = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        leadL[i] = 0.35f * (float)Math.Sin(2.0 * Math.PI * 440.0 * t);
        leadR[i] = 0.31f * (float)Math.Sin(2.0 * Math.PI * 440.0 * t + 0.12);
        backL[i] = 0.18f * (float)Math.Sin(2.0 * Math.PI * 660.0 * t);
        backR[i] = 0.16f * (float)Math.Sin(2.0 * Math.PI * 660.0 * t + 0.25);
        instrL[i] = 0.42f * (float)Math.Sin(2.0 * Math.PI * 110.0 * t);
        instrR[i] = 0.39f * (float)Math.Sin(2.0 * Math.PI * 110.0 * t + 0.18);
        monoLead[i] = 0.5f * (leadL[i] + leadR[i]);
        monoBack[i] = 0.5f * (backL[i] + backR[i]);
    }
    List<StereoStemBuffer> karaStems = [
        new StereoStemBuffer("lead_vocal", leadL, leadR),
        new StereoStemBuffer("backing_vocals", backL, backR),
        new StereoStemBuffer("instrumental", instrL, instrR)
    ];
    NeuralSeparatedTrack track = new() {
        SourceTrackKey = "/music/wave_b_test.flac",
        DisplayTitle = "Wave B Dedup Test",
        ArtistName = "Cavern QA",
        OriginTag = "Local AI",
        ModelSummary = "TestLeadBack",
        PlanSignature = "goal:SpatialKaraoke|cfg:v1|backend=OnDevice|backing=KeepBacking",
        SampleRate = sr,
        Stems = karaStems,
        MonoLeadVocalRef = monoLead,
        MonoBackingVocalRef = monoBack
    };

    string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_t45_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmpDir);
    try {
        string rawFile = Path.Combine(tmpDir, "raw.ntc");
        string compactFile = Path.Combine(tmpDir, "compact.ntc");
        NeuralTrackCacheCodec.WriteTrackFile(rawFile, track, null, NeuralTrackCacheCompressionMode.RawFloat32);
        NeuralTrackCacheCodec.WriteTrackFile(compactFile, track, null, NeuralTrackCacheCompressionMode.Compact16Bit);
        long rawBytes = new FileInfo(rawFile).Length;
        long compactBytes = new FileInfo(compactFile).Length;
        Assert(compactBytes < rawBytes * 0.45,
            $"CNTC v3 Optimal + mono-ref dedup must shrink significantly vs RawFloat32 (compact={compactBytes}, raw={rawBytes})");

        NeuralSeparatedTrack reloaded = NeuralTrackCacheCodec.ReadTrackFile(compactFile, out _);
        Assert(reloaded != null && reloaded.MonoLeadVocalRef != null && reloaded.MonoBackingVocalRef != null,
            "CNTC v3 ReadTrackFile must transparently synthesize omitted derivable MonoLeadVocalRef & MonoBackingVocalRef");
        Assert(reloaded.MonoLeadVocalRef.Length == len && reloaded.MonoBackingVocalRef.Length == len,
            "Synthesized mono refs length mismatch");

        double sigPow = 0.0, errPow = 0.0;
        for (int i = 0; i < len; i++) {
            double s = monoLead[i];
            double e = reloaded.MonoLeadVocalRef[i] - s;
            sigPow += s * s;
            errPow += e * e;
        }
        double snrDb = 10.0 * Math.Log10(Math.Max(1e-12, sigPow) / Math.Max(1e-12, errPow));
        Assert(snrDb >= 85.0, $"Reconstructed deduplicated MonoLeadVocalRef SNR must be >= 85 dB, got {snrDb:0.1} dB");

        // Verify custom (dereverbed / non-derivable) mono ref is preserved verbatim (never deduplicated away)
        float[] customLead = new float[len];
        for (int i = 0; i < len; i++) {
            customLead[i] = 0.25f * (float)Math.Sin(2.0 * Math.PI * 880.0 * (i / (double)sr));
        }
        NeuralSeparatedTrack customTrack = new() {
            SourceTrackKey = track.SourceTrackKey,
            DisplayTitle = track.DisplayTitle,
            ArtistName = track.ArtistName,
            OriginTag = track.OriginTag,
            ModelSummary = track.ModelSummary,
            PlanSignature = track.PlanSignature,
            SampleRate = sr,
            Stems = karaStems,
            MonoLeadVocalRef = customLead,
            MonoBackingVocalRef = monoBack
        };
        string customFile = Path.Combine(tmpDir, "custom.ntc");
        NeuralTrackCacheCodec.WriteTrackFile(customFile, customTrack, null, NeuralTrackCacheCompressionMode.Compact16Bit);
        NeuralSeparatedTrack reloadedCustom = NeuralTrackCacheCodec.ReadTrackFile(customFile, out _);
        double cErr = 0.0, cSig = 0.0;
        for (int i = 0; i < len; i++) {
            double s = customLead[i];
            double e = reloadedCustom.MonoLeadVocalRef[i] - s;
            cSig += s * s;
            cErr += e * e;
        }
        double customSnrDb = 10.0 * Math.Log10(Math.Max(1e-12, cSig) / Math.Max(1e-12, cErr));
        Assert(customSnrDb >= 85.0, $"Custom dereverb MonoLeadVocalRef must be preserved intact (SNR={customSnrDb:0.1} dB)");

        // Verify in-place lossless compaction reclaims bytes from RawFloat32
        long reclaimed = NeuralTrackCacheCodec.CompactFileIfBeneficial(rawFile, NeuralTrackCacheCompressionMode.Compact16Bit);
        Assert(reclaimed > rawBytes / 2, $"CompactFileIfBeneficial must reclaim >50% from RawFloat32, got {reclaimed}/{rawBytes}");
        NeuralSeparatedTrack postCompact = NeuralTrackCacheCodec.ReadTrackFile(rawFile, out _);
        Assert(postCompact != null && postCompact.Stems.Count == 3 && postCompact.MonoLeadVocalRef != null,
            "Compacted .ntc file failed integrity reload");
    } finally {
        try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
    }

    // Verify incremental Stage-1 vocal reuse skips Stage-1 model execution completely in Local ONNX orchestrator
    using MockT3MultiModelRuntime mockRt = new();
    NeuralPipelineOrchestrator orch = new(mockRt, mockRt);
    NeuralAudioConfig cfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        StudioBundlePreset = NeuralEnsembleBundlePreset.StudioKaraokeLeadBacking,
        KaraokePreserveBackingVocals = true,
        TrackCacheEnabled = false
    };
    NeuralSeparatedTrack preExtractedStage1 = new() {
        SourceTrackKey = "/music/wave_b_reuse.flac",
        SampleRate = sr,
        Stems = [
            new StereoStemBuffer("vocals", leadL, leadR),
            new StereoStemBuffer("drums", instrL, instrR),
            new StereoStemBuffer("bass", backL, backR),
            new StereoStemBuffer("other", instrL, instrR)
        ]
    };
    (NeuralSeparatedTrack reusedOut, _, bool reusedFromCache) = orch.ExecuteTrackPipeline(
        sourceTrackKey: "/music/wave_b_reuse.flac",
        fileSizeBytes: 123456,
        lastModifiedTicks: 1000,
        mixLeft: instrL,
        mixRight: instrR,
        sampleRate: sr,
        goal: NeuralPipelineGoal.SpatialKaraoke,
        config: cfg,
        preExtractedTrack: preExtractedStage1);
    Assert(!reusedFromCache && reusedOut != null, "Incremental reuse should produce a new Stage-2 Karaoke track");
    Assert(reusedOut.FindStem("lead_vocal") != null && reusedOut.FindStem("backing_vocals") != null,
        "Incremental reuse output must contain lead_vocal and backing_vocals from Stage-2");
    Assert(reusedOut.FindStem("drums") != null && reusedOut.FindStem("bass") != null,
        "Incremental reuse must preserve instrumental stems from pre-extracted Stage-1 track");
    Assert(mockRt.CreatedSessionModelPaths.Count == 1 &&
           mockRt.CreatedSessionModelPaths[0].Contains("mock_karaoke_2s", StringComparison.OrdinalIgnoreCase),
        $"Expected ONLY Stage-2 karaoke session to run when Stage-1 vocals exist, ran: [{string.Join(", ", mockRt.CreatedSessionModelPaths)}]");
});

// T46: W53.3 Wave C (Stereo-Pan Azimuth Preservation for Fixed 3D Objects + Mood-Aware Choreography)
RunTest("T46_W53_3_StereoPanAzimuthEstimator_FixedObjectPan_And_MoodChoreo", () => {
    int sr = 48000;
    int len = sr * 2; // 2.0s so choreography has multiple keyframes
    float[] baseSig = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        baseSig[i] = 0.4f * (float)Math.Sin(2.0 * Math.PI * 520.0 * t)
                   + 0.15f * (float)Math.Sin(2.0 * Math.PI * 1040.0 * t);
    }

    // 1. Constant-power pan angle recovery across [-60°, +60°]
    float[] targetAngles = [-55f, -35f, -15f, 0f, 20f, 45f, 58f];
    foreach (float expectedAz in targetAngles) {
        double panNorm = expectedAz / 60.0;
        double phi = (panNorm + 1.0) * (Math.PI * 0.25);
        float gL = (float)Math.Cos(phi);
        float gR = (float)Math.Sin(phi);
        float[] l = new float[len], r = new float[len];
        for (int i = 0; i < len; i++) {
            l[i] = baseSig[i] * gL;
            r[i] = baseSig[i] * gR;
        }
        StereoPanEstimate est = StereoPanAzimuthEstimator.Estimate(new StereoStemBuffer("guitar", l, r));
        Assert(est.HasSignal, $"StereoPanEstimate must have signal for az={expectedAz}°");
        Assert(MathF.Abs(est.PanAzimuthDeg - expectedAz) <= 0.5f,
            $"StereoPanAzimuthEstimator error for {expectedAz}° too high: got {est.PanAzimuthDeg:0.00}°");
        Assert(est.InterChannelCoherence >= 0.99f && est.StereoWidth01 <= 0.05f,
            $"Coherent panned mono stem should have IC>=0.99 & width<=0.05, got IC={est.InterChannelCoherence:0.00}, width={est.StereoWidth01:0.00}");
    }

    // 2. Fixed 3D scene descriptors preserve exact descriptor count AND acoustic pan centroid (<= 1.0° error)
    float[] guitarL = new float[len], guitarR = new float[len];
    float[] pianoL = new float[len], pianoR = new float[len];
    float[] vocalL = new float[len], vocalR = new float[len];
    float[] bassL = new float[len], bassR = new float[len];
    double gPhi = ((-42.0 / 60.0) + 1.0) * (Math.PI * 0.25);
    double pPhi = ((36.0 / 60.0) + 1.0) * (Math.PI * 0.25);
    for (int i = 0; i < len; i++) {
        // Add small uncorrelated reverb tail to guitar to test stereo-width midpoint compensation
        float decorr = 0.06f * (float)Math.Sin(2.0 * Math.PI * 1730.0 * (i / (double)sr));
        guitarL[i] = baseSig[i] * (float)Math.Cos(gPhi) + decorr;
        guitarR[i] = baseSig[i] * (float)Math.Sin(gPhi) - decorr;
        pianoL[i] = baseSig[i] * (float)Math.Cos(pPhi);
        pianoR[i] = baseSig[i] * (float)Math.Sin(pPhi);
        vocalL[i] = baseSig[i] * 0.7071f;
        vocalR[i] = baseSig[i] * 0.7071f;
        bassL[i] = 0.4f * (float)Math.Sin(2.0 * Math.PI * 75.0 * (i / (double)sr));
        bassR[i] = bassL[i];
    }
    StereoStemBuffer guitarStem = new("guitar", guitarL, guitarR);
    StereoStemBuffer pianoStem = new("piano", pianoL, pianoR);
    List<StereoStemBuffer> stems = [
        new StereoStemBuffer("vocals", vocalL, vocalR),
        new StereoStemBuffer("bass", bassL, bassR),
        guitarStem,
        pianoStem
    ];
    Vector3 env = new(2f, 2f, 2f);
    SpatialSourceDescriptor[] descs = NeuralStemSpatializer.BuildSceneDescriptors(stems, env);
    Assert(descs.Length == 6, $"Expected 6 scene descriptors (1 vocal + 1 bass + 2 guitar + 2 piano), got {descs.Length}");

    static float DescriptorAzDeg(Vector3 pos) => MathF.Atan2(pos.X, pos.Z) * (180f / MathF.PI);
    StereoPanEstimate guitarPan = StereoPanAzimuthEstimator.Estimate(guitarStem);
    float gAzL = DescriptorAzDeg(descs[2].NormalizedPosition);
    float gAzR = DescriptorAzDeg(descs[3].NormalizedPosition);
    float gCentroid = 0.5f * (1f - guitarPan.PowerBalance) * gAzL + 0.5f * (1f + guitarPan.PowerBalance) * gAzR;
    Assert(MathF.Abs(gCentroid - guitarPan.PanAzimuthDeg) <= 1.0f,
        $"Guitar L/R power-weighted 3D centroid must match mix pan ({guitarPan.PanAzimuthDeg:0.00}°), got {gCentroid:0.00}° (L={gAzL:0.0}°, R={gAzR:0.0}°)");

    StereoPanEstimate pianoPan = StereoPanAzimuthEstimator.Estimate(pianoStem);
    float pAzL = DescriptorAzDeg(descs[4].NormalizedPosition);
    float pAzR = DescriptorAzDeg(descs[5].NormalizedPosition);
    float pCentroid = 0.5f * (1f - pianoPan.PowerBalance) * pAzL + 0.5f * (1f + pianoPan.PowerBalance) * pAzR;
    Assert(MathF.Abs(pCentroid - pianoPan.PanAzimuthDeg) <= 1.0f,
        $"Piano L/R power-weighted 3D centroid must match mix pan ({pianoPan.PanAzimuthDeg:0.00}°), got {pCentroid:0.00}°");

    // 3. Mood-aware expressive choreography orbits relative to measured stereo pan while respecting slew & safety bounds
    IReadOnlyList<LyricMoodDigest> intimateMoods = [new LyricMoodDigest(LyricMoodTag.Intimate, 0.22f, 0.30f, 0, 0.0)];
    IReadOnlyList<LyricMoodDigest> anthemMoods = [new LyricMoodDigest(LyricMoodTag.Anthem, 0.90f, 0.85f, 0, 0.0)];
    SpatialChoreoTrack trackOff = NeuralStemSpatializer.BuildChoreoTrack(stems, sr, SpatialChoreoMode.Off, intimateMoods);
    SpatialChoreoTrack trackIntimate = NeuralStemSpatializer.BuildChoreoTrack(stems, sr, SpatialChoreoMode.Cinematic, intimateMoods);
    SpatialChoreoTrack trackAnthem = NeuralStemSpatializer.BuildChoreoTrack(stems, sr, SpatialChoreoMode.Cinematic, anthemMoods);
    Assert(trackOff != null && trackIntimate != null && trackAnthem != null, "Choreo tracks must build cleanly");

    // Off mode must anchor guitar near its measured stereo pan (~ -42°) rather than hardcoded +55°
    Assert(trackOff.EvaluatePositionZeroAlloc(2, 0.5, out Vector3 offGuitarPos), "Off mode eval failed");
    PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(offGuitarPos), out float offGuitarAz, out _);
    Assert(MathF.Abs(offGuitarAz - guitarPan.PanAzimuthDeg) <= 2.5f,
        $"Choreo Off mode must preserve guitar stereo pan ({guitarPan.PanAzimuthDeg:0.0}°), got {offGuitarAz:0.0}°");

    // Anthem mood must produce higher vertical lift / wider stage than Intimate mood on guitar
    Assert(trackIntimate.EvaluatePositionZeroAlloc(2, 1.0, out Vector3 intGuitarPos), "Intimate eval failed");
    Assert(trackAnthem.EvaluatePositionZeroAlloc(2, 1.0, out Vector3 antGuitarPos), "Anthem eval failed");
    PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(intGuitarPos), out _, out float intEl);
    PsychoacousticGovernor.CartesianToSphericalDeg(Vector3.Normalize(antGuitarPos), out _, out float antEl);
    Assert(antEl > intEl + 1.5f,
        $"Anthem mood must elevate guitar more than Intimate mood (anthem={antEl:0.1}°, intimate={intEl:0.1}°)");
    Assert(intGuitarPos.Length() < antGuitarPos.Length(),
        $"Intimate mood must draw guitar closer than Anthem mood (intimateR={intGuitarPos.Length():0.00}, anthemR={antGuitarPos.Length():0.00})");
});

// T47: W53.4 Wave D (One UI 9 Modular Editable Quick-Panel Control Blocks — Persistence, Locks, Pinned-Top & Schema Merge)
RunTest("T47_W53_4_WaveD_ModularQuickPanelLayout_Persistence_Locks_And_Merge", () => {
    Cavernize.Logic.Models.ModularQuickPanelLayout playDef = Cavernize.Logic.Models.ModularQuickPanelLayout.CreateDefaultPlayLayout();
    Cavernize.Logic.Models.ModularQuickPanelLayout play = Cavernize.Logic.Models.ModularQuickPanelLayout.CreateDefaultPlayLayout();
    Assert(play.Blocks.Count == 8, $"Expected 8 default Play blocks, got {play.Blocks.Count}");
    Assert(!play.IsCustomized(playDef), "Fresh Play layout must not report customized");

    // Stage is pinned-top & locked: cannot move down, cannot hide, and Title cannot move above Stage
    Assert(!play.CanMoveDown(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Stage), "Stage must stay pinned at top");
    Assert(!play.MoveUp(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Title), "Title must not overtake pinned Stage");
    Assert(!play.SetVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Transport, false), "Locked Transport must refuse hiding");
    Assert(play.IsVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Transport), "Locked Transport must stay visible");

    // Reorder optional blocks & hide Badges
    Assert(play.MoveUp(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Volume), "Volume should move up cleanly");
    Assert(play.SetVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Badges, false), "Badges should hide cleanly");
    Assert(!play.IsVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Badges), "Badges visibility should be false");
    Assert(play.IsCustomized(playDef), "Modified Play layout must report customized");

    // JSON round-trip preserves exact custom order & visibility
    string savedJson = play.ToJson();
    Cavernize.Logic.Models.ModularQuickPanelLayout restored = Cavernize.Logic.Models.ModularQuickPanelLayout.FromJsonOrDefault(savedJson, playDef);
    Assert(restored.Blocks.Count == play.Blocks.Count, "Restored Play block count mismatch");
    for (int i = 0; i < play.Blocks.Count; i++) {
        Assert(restored.Blocks[i].Id == play.Blocks[i].Id && restored.Blocks[i].Visible == play.Blocks[i].Visible,
            $"Restored Play block[{i}] mismatch: expected {play.Blocks[i].Id}/{play.Blocks[i].Visible}, got {restored.Blocks[i].Id}/{restored.Blocks[i].Visible}");
    }

    // Corrupt / partial / hostile JSON payload (hiding locked block, unknown IDs, missing canonical blocks, unpinning stage)
    string hostileJson = "{\"SchemaVersion\":1,\"Blocks\":[{\"Id\":\"unknown_junk\",\"Visible\":true},{\"Id\":\"transport\",\"Visible\":false},{\"Id\":\"stage\",\"Visible\":false}]}";
    Cavernize.Logic.Models.ModularQuickPanelLayout merged = Cavernize.Logic.Models.ModularQuickPanelLayout.FromJsonOrDefault(hostileJson, playDef);
    Assert(merged.Blocks.Count == playDef.Blocks.Count, $"Merged layout must restore all {playDef.Blocks.Count} canonical blocks, got {merged.Blocks.Count}");
    Assert(merged.Blocks[0].Id == Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Stage, "Pinned Stage must always be forced back to index 0");
    Assert(merged.IsVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.PlayIds.Transport), "Locked Transport must stay visible even if JSON says false");

    // 3D Room layout customization & reset
    Cavernize.Logic.Models.ModularQuickPanelLayout roomDef = Cavernize.Logic.Models.ModularQuickPanelLayout.CreateDefaultRoomLayout();
    Cavernize.Logic.Models.ModularQuickPanelLayout room = Cavernize.Logic.Models.ModularQuickPanelLayout.CreateDefaultRoomLayout();
    Assert(room.Blocks.Count == 6, $"Expected 6 default 3D Room blocks, got {room.Blocks.Count}");
    Assert(!room.SetVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.RoomIds.RoomCanvas, false), "Locked 3D Room Canvas must refuse hiding");
    Assert(room.MoveUp(Cavernize.Logic.Models.ModularQuickPanelLayout.RoomIds.QuickPanel), "QuickPanel should move up");
    Assert(room.SetVisible(Cavernize.Logic.Models.ModularQuickPanelLayout.RoomIds.ChannelMixer, false), "ChannelMixer should hide");
    room.ResetFrom(roomDef);
    Assert(!room.IsCustomized(roomDef), "ResetFrom must restore default 3D Room layout");
});

// T48: W53.5 Wave E (Pure-C# W8A16 Per-Channel ONNX Weight-Only Quantizer & Mobile PSS Budget)
RunTest("T48_W53_5_WaveE_OnnxMobileQuantizer_W8A16_SQNR_And_PssBudget", () => {
    // Build a synthetic ONNX FP32 model with a 2D [64, 64] MatMul weight (4096 floats) + 1D [64] bias
    float[] weights = new float[64 * 64];
    for (int r = 0; r < 64; r++) {
        float rowScale = 0.25f + (r % 7) * 0.15f;
        for (int c = 0; c < 64; c++) {
            weights[r * 64 + c] = rowScale * MathF.Sin(0.19f * (r + 1) * (c + 1));
        }
    }
    float[] bias = new float[64];
    for (int i = 0; i < 64; i++) bias[i] = 0.05f * MathF.Cos(i * 0.3f);

    byte[] Build2DFloatTensor(string name, int rows, int cols, float[] vals) {
        using MemoryStream ms = new();
        WriteVarintField(ms, 1, (ulong)rows);
        WriteVarintField(ms, 1, (ulong)cols);
        WriteVarintField(ms, 2, 1); // FLOAT
        WriteStringField(ms, 8, name);
        byte[] raw = new byte[vals.Length * 4];
        for (int i = 0; i < vals.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), vals[i]);
        WriteLenField(ms, 9, raw);
        return ms.ToArray();
    }

    using MemoryStream graph = new();
    WriteLenField(graph, 1, BuildNode("MatMul", ["x", "fc1.weight"], ["mm_out"], Array.Empty<byte[]>()));
    WriteLenField(graph, 1, BuildNode("Add", ["mm_out", "fc1.bias"], ["y"], Array.Empty<byte[]>()));
    WriteLenField(graph, 5, Build2DFloatTensor("fc1.weight", 64, 64, weights));
    WriteLenField(graph, 5, BuildRawFloatTensor("fc1.bias", bias));
    WriteLenField(graph, 11, BuildValueInfo("x", 1));
    WriteLenField(graph, 12, BuildValueInfo("y", 1));

    using MemoryStream model = new();
    WriteVarintField(model, 1, 8);
    WriteLenField(model, 7, graph.ToArray());
    byte[] srcOnnx = model.ToArray();

    Cavernize.Logic.Spatial.OnnxW8A16QuantizationResult qRes =
        Cavernize.Logic.Spatial.OnnxMobileQuantizer.ConvertBytesToW8A16(srcOnnx, out byte[] w8a16Bytes);
    Assert(qRes.Success && w8a16Bytes != null && w8a16Bytes.Length > 64, $"W8A16 quantization failed: {qRes.Message}");
    Assert(qRes.QuantizedTensors == 1, $"Expected exactly 1 quantized 2D MatMul weight, got {qRes.QuantizedTensors}");
    Assert(qRes.PreservedTensors >= 1, $"Expected 1D bias tensor preserved, got {qRes.PreservedTensors}");
    Assert(qRes.QuantizedElements == 4096, $"Expected 4096 quantized weights, got {qRes.QuantizedElements}");
    Assert(qRes.CompressionRatio >= 2.5, $"Expected W8A16 compression >= 2.5x, got {qRes.CompressionRatio:F2}x");
    Assert(qRes.MinTensorSnrDb >= 35.0, $"Expected per-channel INT8 weight SQNR >= 35 dB, got {qRes.MinTensorSnrDb:F2} dB");

    string payloadAscii = Encoding.ASCII.GetString(w8a16Bytes);
    Assert(payloadAscii.Contains("DequantizeLinear", StringComparison.Ordinal), "W8A16 output must inject DequantizeLinear node");
    Assert(payloadAscii.Contains("fc1.weight__w8q", StringComparison.Ordinal), "W8A16 output must emit INT8 __w8q initializer");
    Assert(payloadAscii.Contains("fc1.weight__w8s", StringComparison.Ordinal), "W8A16 output must emit FP32 __w8s scale initializer");

    // Verify Auto mode selects W8A16 for >=200 MB checkpoints and keeps estimated PSS < 1400 MB mobile target
    long heavyModelBytes = 320L * 1024L * 1024L;
    Cavernize.Logic.Spatial.NeuralQuantizationMode autoHeavy = Cavernize.Logic.Spatial.OnnxMobileQuantizer.ResolveEffectiveMode(
        Cavernize.Logic.Spatial.NeuralQuantizationMode.Auto,
        heavyModelBytes,
        NeuralExecutionProvider.Cpu,
        isWebGpuOptimal: false);
    Assert(autoHeavy == Cavernize.Logic.Spatial.NeuralQuantizationMode.W8A16, $"Auto mode must select W8A16 for 320 MB model, got {autoHeavy}");
    double estPssMb = Cavernize.Logic.Spatial.OnnxMobileQuantizer.EstimatePeakPssMb(heavyModelBytes, autoHeavy);
    Assert(estPssMb < 1400.0, $"W8A16 estimated PSS ({estPssMb:F1} MB) must stay strictly below 1400 MB mobile target");
});

// T49: W53.6 Wave F (Physical Transaural XTC Speaker Cancellation, Zero-Alloc RT & Bit-Exact 0.0f Headphone Bypass KILL R3)
RunTest("T49_W53_6_WaveF_TransauralXtc_Cancellation_And_BitExactHeadphoneBypass", () => {
    int sr = 48000;
    int frames = 4096;
    float[] src = new float[frames * 2];
    for (int f = 0; f < frames; f++) {
        double t = f / (double)sr;
        // Left-only 1 kHz + 2 kHz probe signal
        src[f * 2] = 0.35f * MathF.Sin((float)(2.0 * Math.PI * 1000.0 * t))
                   + 0.20f * MathF.Sin((float)(2.0 * Math.PI * 2000.0 * t));
        src[f * 2 + 1] = 0f;
    }

    using Cavernize.Logic.Spatial.Adapters.TransauralXtcPostProcessingStage stage = new();
    stage.Configure(sr, 2);
    stage.ApplyConfig(new Cavernize.Logic.Spatial.TransauralXtcConfig {
        Enabled = true,
        Preset = Cavern.Virtualizer.TransauralSpeakerPreset.PhoneBuiltInLandscape,
        Strength = 0.90f,
        AutoBypassOnHeadphones = true
    });

    // 1. KILL R3: Headphone route must produce BIT-EXACT 0.0f pass-through on every float
    stage.UpdateOutputRoute(headphoneRoute: true, AudioDeviceTransport.WiredJack);
    Assert(stage.IsHeadphoneBypassEngaged && !stage.IsActivelyProcessing, "Headphone bypass must engage immediately on wired headphones");
    float[] hpBuf = (float[])src.Clone();
    stage.ProcessInterleaved(hpBuf, 0, frames, 2, default);
    for (int i = 0; i < hpBuf.Length; i++) {
        Assert(BitConverter.SingleToInt32Bits(hpBuf[i]) == BitConverter.SingleToInt32Bits(src[i]),
            $"KILL R3 VIOLATION: Headphone bypass mutated sample[{i}] ({src[i]} -> {hpBuf[i]})");
    }

    // 2. Speaker route: verify >= 12 dB ear-level crosstalk cancellation improvement under physical 2x2 speaker-to-ear matrix
    stage.UpdateOutputRoute(headphoneRoute: false, AudioDeviceTransport.BuiltinSpeaker);
    Assert(!stage.IsHeadphoneBypassEngaged && stage.IsActivelyProcessing, "XTC must activate on built-in stereo speakers");

    Cavern.Virtualizer.TransauralGeometryParams geo = Cavern.Virtualizer.TransauralXtcFilter.ComputeGeometry(
        Cavern.Virtualizer.TransauralSpeakerPreset.PhoneBuiltInLandscape);

    float[] rawEars = new float[frames * 2];
    Cavern.Virtualizer.TransauralXtcFilter.SimulatePhysicalSpeakerToEar(src, rawEars, frames, sr, geo);

    float[] xtcSpeakers = (float[])src.Clone();
    stage.ProcessInterleaved(xtcSpeakers, 0, frames, 2, default);
    float[] xtcEars = new float[frames * 2];
    Cavern.Virtualizer.TransauralXtcFilter.SimulatePhysicalSpeakerToEar(xtcSpeakers, xtcEars, frames, sr, geo);

    double MeasureChannelSeparationDb(float[] stereo, int startFrame, int endFrame) {
        double pL = 0.0, pR = 0.0;
        for (int f = startFrame; f < endFrame; f++) {
            float l = stereo[f * 2];
            float r = stereo[f * 2 + 1];
            Assert(float.IsFinite(l) && float.IsFinite(r), "Transaural XTC produced non-finite sample");
            pL += (double)l * l;
            pR += (double)r * r;
        }
        return 10.0 * Math.Log10((pL + 1e-18) / (pR + 1e-18));
    }

    double rawSepDb = MeasureChannelSeparationDb(rawEars, 512, frames);
    double xtcSepDb = MeasureChannelSeparationDb(xtcEars, 512, frames);
    double improvementDb = xtcSepDb - rawSepDb;
    Assert(improvementDb >= 12.0,
        $"Transaural XTC separation improvement too low: raw={rawSepDb:F2} dB, xtc={xtcSepDb:F2} dB, gain={improvementDb:F2} dB (need >= 12 dB)");

    // 3. Steady-state 0 B RT GC check
    float[] rtBlock = new float[1024 * 2];
    Array.Copy(src, rtBlock, rtBlock.Length);
    stage.ProcessInterleaved(rtBlock, 0, 1024, 2, default);
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    for (int iter = 0; iter < 16; iter++) {
        stage.ProcessInterleaved(rtBlock, 0, 1024, 2, default);
    }
    long rtAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    Assert(rtAlloc == 0, $"Transaural XTC RT processing must allocate 0 B GC, got {rtAlloc} B");
});

// T50: W55.1 V4.1 Reform (Stem Deduplication, Atmos 3D Vocal Gain Staging, Symmetric Backing L/R, Direct Discrete Karaoke & Quantization Threshold)
RunTest("T50_W55_1_V4Reform_StemDedup_GainStaging_SymmetricBacking_DiscreteKaraoke", () => {
    int sr = 48000;
    int len = sr; // 1.0s
    float[] lead = new float[len];
    float[] backL = new float[len];
    float[] backR = new float[len];
    float[] drums = new float[len];
    float[] bass = new float[len];
    float[] guitar = new float[len];
    float[] piano = new float[len];
    float[] other = new float[len];
    for (int i = 0; i < len; i++) {
        double t = i / (double)sr;
        lead[i] = 0.35f * MathF.Sin((float)(2.0 * Math.PI * 520.0 * t));
        // Narrow/near-mono backing vocals to verify symmetric L/R separation even when stereo decorrelation is near zero
        backL[i] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 660.0 * t));
        backR[i] = 0.25f * MathF.Sin((float)(2.0 * Math.PI * 660.0 * t + 0.05));
        drums[i] = 0.30f * MathF.Sin((float)(2.0 * Math.PI * 120.0 * t));
        bass[i] = 0.35f * MathF.Sin((float)(2.0 * Math.PI * 75.0 * t));
        guitar[i] = 0.22f * MathF.Sin((float)(2.0 * Math.PI * 330.0 * t));
        piano[i] = 0.22f * MathF.Sin((float)(2.0 * Math.PI * 440.0 * t));
        other[i] = 0.20f * MathF.Sin((float)(2.0 * Math.PI * 880.0 * t));
    }

    // 1. Stem deduplication: combined 'vocals' and 'instrumental' MUST be filtered when discrete lead_vocal + band stems exist
    StereoStemBuffer[] rawDuplicated = [
        new StereoStemBuffer("vocals", lead, lead),
        new StereoStemBuffer("lead_vocal", lead, lead),
        new StereoStemBuffer("backing_vocals", backL, backR),
        new StereoStemBuffer("instrumental", other, other),
        new StereoStemBuffer("drums", drums, drums),
        new StereoStemBuffer("bass", bass, bass),
        new StereoStemBuffer("guitar", guitar, guitar),
        new StereoStemBuffer("piano", piano, piano),
        new StereoStemBuffer("other", other, other),
    ];
    StereoStemBuffer[] filtered = NeuralStemSpatializer.FilterRenderableStems(rawDuplicated);
    Assert(filtered.Length == 7, $"Expected 7 deduplicated stems (vocals & instrumental stripped), got {filtered.Length}");
    Assert(!filtered.Any(s => string.Equals(s.Name, "vocals", StringComparison.OrdinalIgnoreCase)),
        "Redundant Stage-1 'vocals' stem must be stripped when 'lead_vocal' is present");
    Assert(!filtered.Any(s => string.Equals(s.Name, "instrumental", StringComparison.OrdinalIgnoreCase)),
        "Redundant 'instrumental' stem must be stripped when discrete band stems are present");

    // 2. Scene descriptors: zero duplicate vocal objects + symmetric BackingVocals L/R + calibrated Atmos 3D gains
    IReadOnlyList<SpatialSourceDescriptor> descs = NeuralStemSpatializer.BuildSceneDescriptors(rawDuplicated, new Vector3(10f, 7f, 10f));
    Assert(!descs.Any(d => d.Name.Equals("AI Vocals", StringComparison.OrdinalIgnoreCase)),
        "BuildSceneDescriptors must not emit duplicate 'AI Vocals' alongside 'AI Lead_vocal'");
    SpatialSourceDescriptor bvL = descs.FirstOrDefault(d => d.Name.Contains("Backing", StringComparison.OrdinalIgnoreCase) && d.Name.EndsWith(" L", StringComparison.Ordinal));
    SpatialSourceDescriptor bvR = descs.FirstOrDefault(d => d.Name.Contains("Backing", StringComparison.OrdinalIgnoreCase) && d.Name.EndsWith(" R", StringComparison.Ordinal));
    Assert(bvL != null && bvR != null, "Expected both BackingVocals L and BackingVocals R descriptors");
    Assert(bvL.NormalizedPosition.X < -0.30f && bvR.NormalizedPosition.X > 0.30f,
        $"Fixed BackingVocals L/R must be symmetrically separated (got L.X={bvL.NormalizedPosition.X:F2}, R.X={bvR.NormalizedPosition.X:F2})");
    Assert(MathF.Abs(bvL.NormalizedPosition.X + bvR.NormalizedPosition.X) < 0.15f,
        $"Fixed BackingVocals L/R must stay symmetric around center (L.X={bvL.NormalizedPosition.X:F2}, R.X={bvR.NormalizedPosition.X:F2})");

    // 3. Choreography: symmetric BackingVocals L/R across Musical, Cinematic, and Orbit modes
    foreach (SpatialChoreoMode mode in new[] { SpatialChoreoMode.Musical, SpatialChoreoMode.Cinematic, SpatialChoreoMode.Orbit }) {
        SpatialChoreoTrack choreo = NeuralStemSpatializer.BuildChoreoTrack(rawDuplicated, sr, mode);
        Assert(choreo != null && choreo.StemCount == 7, $"Expected 7 deduplicated choreo stems in {mode}, got {choreo?.StemCount ?? 0}");
        int bvIdx = -1;
        for (int s = 0; s < choreo.StemCount; s++) {
            if (choreo.StemRoles[s] == NeuralStemRole.BackingVocals) {
                bvIdx = s;
                break;
            }
        }
        Assert(bvIdx >= 0, $"BackingVocals role missing in {mode} choreo track");
        Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(choreo, mode, bvIdx, NeuralStemRole.BackingVocals, -1f, 0.5, out Vector3 posL),
            $"Failed to evaluate BackingVocals L in {mode}");
        Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(choreo, mode, bvIdx, NeuralStemRole.BackingVocals, +1f, 0.5, out Vector3 posR),
            $"Failed to evaluate BackingVocals R in {mode}");
        float sep = Vector3.Distance(posL, posR);
        Assert(sep > 0.70f, $"Choreo {mode} BackingVocals L/R glued together! dist={sep:F2}, L={posL}, R={posR}");
        if (mode != SpatialChoreoMode.Orbit) {
            Assert(posL.X < -0.25f && posR.X > 0.25f, $"Choreo {mode} BackingVocals L/R not straddling left/right! L={posL}, R={posR}");
        }
    }

    // 4. Atmos 3D gain staging: LeadVocal boosted for presence vs stereo accompaniment attenuation
    float leadGain = NeuralStemSpatializer.GetRecommendedStemSourceGain(NeuralStemRole.LeadVocal, 5);
    float guitarGain = NeuralStemSpatializer.GetRecommendedStemSourceGain(NeuralStemRole.Guitar, 5);
    float backingGain = NeuralStemSpatializer.GetRecommendedStemSourceGain(NeuralStemRole.BackingVocals, 5);
    Assert(leadGain > 1.25f, $"LeadVocal Atmos 3D gain should compensate center HRTF dip (>1.25), got {leadGain:F3}");
    Assert(guitarGain < 0.82f && backingGain < 0.90f, $"Stereo accompaniment gain should prevent chorus masking (guitar={guitarGain:F3}, backing={backingGain:F3})");

    // 5. MVSEP ResolveForGoal honors explicit preferredPresetId over StudioBundle fallback
    MvsepAlgorithmPreset explicitKara = MvsepAlgorithmPreset.ResolveForGoal(NeuralPipelineGoal.StemSeparation, true, "mvsep_cascade_6stem_karaoke");
    Assert(explicitKara != null && explicitKara.Id == "mvsep_cascade_6stem_karaoke", $"Expected explicit mvsep_cascade_6stem_karaoke, got {explicitKara?.Id}");

    // 6. Auto W8A16 threshold lowered to 120 MB so 150 MB models auto-quantize on mobile
    NeuralQuantizationMode auto150 = OnnxMobileQuantizer.ResolveEffectiveMode(
        NeuralQuantizationMode.Auto,
        150L * 1024L * 1024L,
        NeuralExecutionProvider.Cpu,
        isWebGpuOptimal: false);
    Assert(auto150 == NeuralQuantizationMode.W8A16, $"Expected 150 MB model to auto-select W8A16 (>=120 MB threshold), got {auto150}");
});

// ---------- Test 51 (Wave W56.4.2): Deterministic MemoryBudgetGovernor (Exact Tensor Math, 2600 MB Cap, Hysteresis, Zero Heuristics) ----------
RunTest("T51_W56_4_MemoryBudgetGovernor_TensorMath_Hysteresis_And_PssClamp", () => {
    const long MiB = 1024L * 1024L;

    // 1. Exact ONNX tensor dtype byte sizing & overflow-safe product
    Assert(MemoryBudgetGovernor.GetOnnxElementByteSize(MemoryBudgetGovernor.OnnxTypeFloat32) == 4, "ONNX FLOAT32 must be 4 bytes.");
    Assert(MemoryBudgetGovernor.GetOnnxElementByteSize(MemoryBudgetGovernor.OnnxTypeFloat16) == 2, "ONNX FLOAT16 must be 2 bytes.");
    Assert(MemoryBudgetGovernor.GetOnnxElementByteSize(MemoryBudgetGovernor.OnnxTypeUint8) == 1 &&
           MemoryBudgetGovernor.GetOnnxElementByteSize(MemoryBudgetGovernor.OnnxTypeInt8) == 1, "ONNX UINT8/INT8 must be 1 byte.");
    Assert(MemoryBudgetGovernor.GetOnnxElementByteSize(MemoryBudgetGovernor.OnnxTypeInt64) == 8, "ONNX INT64 must be 8 bytes.");
    long tensorBytes = MemoryBudgetGovernor.ComputeTensorBytes([1, 2, 1025, 256], MemoryBudgetGovernor.OnnxTypeFloat32);
    Assert(tensorBytes == 1L * 2L * 1025L * 256L * 4L, $"Unexpected FP32 tensor byte size: {tensorBytes}");
    Assert(MemoryBudgetGovernor.ComputeTensorBytes(ReadOnlySpan<long>.Empty, MemoryBudgetGovernor.OnnxTypeFloat32) == 0L, "Empty shape must return 0 safely.");
    Assert(MemoryBudgetGovernor.ComputeTensorBytes([1, -1, 1025], MemoryBudgetGovernor.OnnxTypeFloat32) == 1025L * 4L, "Dynamic -1 dim must clamp to 1 safely.");

    // 2. Hard 2600 MB single-process PSS cap invariant even on 24 GB RAM flagship
    DeviceMemorySnapshot snap24Gb = new() {
        TotalBytes = 24L * 1024L * MiB,
        AvailableBytes = 16L * 1024L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        IsLowMemory = false,
        ThermalStatusCode = 0,
        HadRecentLowMemoryExit = false,
        LastLowMemoryExitPssBytes = 0L,
        Source = "Synthetic"
    };
    long cap24Gb = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snap24Gb);
    Assert(cap24Gb == MemoryBudgetGovernor.HardSingleProcessPssCeilingBytes,
        $"Expected hard 2600 MB PSS cap on 24 GB RAM device, got {cap24Gb / MiB} MB");

    // 3. Thermal & own-UID LMK ApplicationExitInfo tightening
    DeviceMemorySnapshot snapThermal3 = new() {
        TotalBytes = 12L * 1024L * MiB,
        AvailableBytes = 6L * 1024L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        ThermalStatusCode = 3,
        Source = "Synthetic"
    };
    DeviceMemorySnapshot snapThermal4 = new() {
        TotalBytes = 12L * 1024L * MiB,
        AvailableBytes = 6L * 1024L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        ThermalStatusCode = 4,
        Source = "Synthetic"
    };
    DeviceMemorySnapshot snapLmk = new() {
        TotalBytes = 12L * 1024L * MiB,
        AvailableBytes = 6L * 1024L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        HadRecentLowMemoryExit = true,
        LastLowMemoryExitPssBytes = 1800L * MiB,
        Source = "Synthetic"
    };
    long capT3 = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapThermal3);
    long capT4 = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapThermal4);
    long capLmk = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapLmk);
    Assert(capT3 < cap24Gb && capT4 < capT3, $"Thermal cap should tighten monotonically: t0={cap24Gb / MiB}, t3={capT3 / MiB}, t4={capT4 / MiB}");
    Assert(capLmk <= (long)(1800L * MiB * 0.85), $"Own-UID LMK exit cap should stay <=85% of lastExitPss (1800 MB), got {capLmk / MiB} MB");

    // 4. ±150 MB anti-flap hysteresis preserves previous cap on small perturbations, but drops immediately on emergency thermal/LMK
    DeviceMemorySnapshot snapThermal2 = new() {
        TotalBytes = 12L * 1024L * MiB,
        AvailableBytes = 6L * 1024L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        ThermalStatusCode = 2, // -128 MB (<150 MB hysteresis band)
        Source = "Synthetic"
    };
    long capT2Unlatched = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapThermal2);
    long capT2Latched = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapThermal2, previousLatchedCapBytes: cap24Gb);
    Assert(capT2Unlatched == cap24Gb - 128L * MiB, $"Expected unlatched thermal=2 cap {cap24Gb / MiB - 128} MB, got {capT2Unlatched / MiB} MB");
    Assert(capT2Latched == cap24Gb, "Small <150 MB delta must stay latched to prevent cap flap.");
    long capT4Emergency = MemoryBudgetGovernor.ComputeEffectivePssCapBytes(snapThermal4, previousLatchedCapBytes: cap24Gb);
    Assert(capT4Emergency == capT4, "Emergency thermal >=4 must bypass hysteresis and tighten immediately.");

    // 5. ZERO filename heuristics: opaque-named RoFormer/large model classified heavy purely by tensor/weight math; small SCNet stays light
    NeuralModelCatalogEntry opaqueRoformer = new() {
        Id = "opaque_model_alpha",
        DisplayName = "Opaque Alpha",
        Task = NeuralModelTask.VocalSeparation,
        ContractKind = NeuralOnnxContractKind.MelBandRoFormerHostStft,
        FileName = "opaque_alpha.onnx",
        ApproxSizeMb = 228f,
        RecommendedChunkSamples = 352800,
        OutputStemNames = ["vocals", "instrumental"]
    };
    OnnxTensorMemoryProfile profRoformer = MemoryBudgetGovernor.ComputeTensorMemoryProfile(228L * MiB, opaqueRoformer, NeuralQuantizationMode.Fp32);
    Assert(profRoformer.IsMemoryHeavy, "Opaque-named RoFormer must classify heavy purely from tensor/weight profile (zero filename heuristics).");
    Assert(profRoformer.TotalEstimatedLoadBytes > 950L * MiB, $"Unexpectedly low RoFormer FP32 peak: {profRoformer.TotalEstimatedLoadBytes / MiB} MB");

    OnnxTensorMemoryProfile profQuant = MemoryBudgetGovernor.ComputeTensorMemoryProfile(228L * MiB, opaqueRoformer, NeuralQuantizationMode.W8A16);
    Assert(profQuant.QuantizedWeightBytes < profRoformer.QuantizedWeightBytes, "W8A16 quantization must reduce effective weight footprint.");

    NeuralModelCatalogEntry tinyScnet = new() {
        Id = "opaque_tiny_fast",
        DisplayName = "Opaque Tiny",
        Task = NeuralModelTask.StemSeparation4,
        ContractKind = NeuralOnnxContractKind.ScnetStft4Ch,
        FileName = "opaque_tiny.onnx",
        ApproxSizeMb = 28f,
        RecommendedChunkSamples = 132300,
        StftFftSize = 2048,
        StftHopSize = 1024,
        StftDimF = 1025,
        StftDimT = 128,
        OutputStemNames = ["drums", "bass", "other", "vocals"]
    };
    OnnxTensorMemoryProfile profTiny = MemoryBudgetGovernor.ComputeTensorMemoryProfile(28L * MiB, tinyScnet, NeuralQuantizationMode.Fp32);
    Assert(!profTiny.IsMemoryHeavy, $"Compact 28 MB SCNet must NOT be classified as memory-heavy (act={profTiny.ActivationWorkingSetBytes / MiB} MB).");

    // 6. End-to-end Evaluate decisions: LOW_MEMORY, PSS_CAP_EXCEEDED, INSUFFICIENT_RAM, OK
    DeviceMemorySnapshot lowMemSnap = new() {
        TotalBytes = 8L * 1024L * MiB,
        AvailableBytes = 400L * MiB,
        LowMemoryThresholdBytes = 512L * MiB,
        IsLowMemory = true,
        Source = "Synthetic"
    };
    MemoryBudgetDecision dLow = MemoryBudgetGovernor.Evaluate(lowMemSnap, 228L * MiB, opaqueRoformer, NeuralQuantizationMode.Fp32);
    Assert(!dLow.HasSufficientMemory && dLow.StatusCode == "LOW_MEMORY", $"Expected LOW_MEMORY rejection, got {dLow.StatusCode}");

    MemoryBudgetDecision dTooBig = MemoryBudgetGovernor.Evaluate(snap24Gb, 780L * MiB, opaqueRoformer, NeuralQuantizationMode.Fp32);
    Assert(!dTooBig.HasSufficientMemory && dTooBig.StatusCode == "PSS_CAP_EXCEEDED",
        $"Expected PSS_CAP_EXCEEDED rejection on 780 MB FP32 model, got {dTooBig.StatusCode} (est={dTooBig.EstimatedModelLoadBytes / MiB} MB, cap={dTooBig.EffectivePssCapBytes / MiB} MB)");

    DeviceMemorySnapshot tightAvailSnap = new() {
        TotalBytes = 8L * 1024L * MiB,
        AvailableBytes = 900L * MiB,
        LowMemoryThresholdBytes = 256L * MiB,
        IsLowMemory = false,
        Source = "Synthetic"
    };
    MemoryBudgetDecision dNoRam = MemoryBudgetGovernor.Evaluate(tightAvailSnap, 228L * MiB, opaqueRoformer, NeuralQuantizationMode.Fp32);
    Assert(!dNoRam.HasSufficientMemory && dNoRam.StatusCode == "INSUFFICIENT_RAM", $"Expected INSUFFICIENT_RAM rejection, got {dNoRam.StatusCode}");

    MemoryBudgetDecision dOk = MemoryBudgetGovernor.Evaluate(snap24Gb, 228L * MiB, opaqueRoformer, NeuralQuantizationMode.W8A16);
    Assert(dOk.HasSufficientMemory && dOk.StatusCode == "OK", $"Expected OK decision for W8A16 quantized RoFormer on 24 GB flagship, got {dOk.StatusCode}: {dOk.DiagnosticMessage}");
});

// ---------- Test 52 (Wave W57.1 Q1-Q2): CavernAmp SIMD FFT Parity, 64-bit TDF-II Biquad, Cytomic ZDF-SVF, LR4 Crossover ----------
RunTest("T52_W57_1_SimdFft_Biquad64_CytomicSvf_Lr4Crossover", () => {
    // 1. CavernAmp.IsMono() SIMD unlock + Measurements.FFT parity <= 1e-4 via MonoOverride A/B
    Assert(CavernAmp.MonoOverride == null, "MonoOverride must default to null.");
    if (Vector.IsHardwareAccelerated) {
        Assert(!CavernAmp.IsMono(), "CavernAmp.IsMono() must return false when hardware SIMD is accelerated.");
    }
    float[] fftSig = new float[1024];
    for (int i = 0; i < fftSig.Length; i++) {
        fftSig[i] = 0.45f * MathF.Sin(2f * MathF.PI * 17f * i / fftSig.Length) +
                    0.30f * MathF.Cos(2f * MathF.PI * 83f * i / fftSig.Length) +
                    0.15f * MathF.Sin(2f * MathF.PI * 211f * i / fftSig.Length);
    }
    Complex[] scalarFft, simdFft;
    try {
        CavernAmp.MonoOverride = true;
        Assert(CavernAmp.IsMono(), "MonoOverride=true must force scalar path.");
        using (FFTCache scalarCache = new(1024)) {
            scalarFft = fftSig.FFT(scalarCache);
        }
        CavernAmp.MonoOverride = false;
        Assert(!CavernAmp.IsMono(), "MonoOverride=false must force SIMD path.");
        using (FFTCache simdCache = new(1024)) {
            simdFft = fftSig.FFT(simdCache);
        }
    } finally {
        CavernAmp.MonoOverride = null;
    }
    float maxFftDiff = 0f;
    for (int i = 0; i < scalarFft.Length; i++) {
        float dr = MathF.Abs(scalarFft[i].Real - simdFft[i].Real);
        float di = MathF.Abs(scalarFft[i].Imaginary - simdFft[i].Imaginary);
        if (dr > maxFftDiff) maxFftDiff = dr;
        if (di > maxFftDiff) maxFftDiff = di;
    }
    Assert(maxFftDiff <= 1e-4f, $"Scalar vs SIMD FFT parity exceeded 1e-4: maxDiff={maxFftDiff:E3}");

    // 2. 64-bit double TDF-II BiquadFilter (PeakingEQ & Lowpass at 20 Hz @ 48 kHz): noise floor <= -110 dBFS
    const int sr = 48000;
    PeakingEQ peak20 = new(sr, 20.0, 0.7071067811865476, 6.0);
    Lowpass lp20 = new(sr, 20.0, 0.7071067811865476);
    int biquadLen = sr; // 1.0 s
    float[] sigPeak = new float[biquadLen];
    float[] sigLp = new float[biquadLen];
    double pz1 = 0, pz2 = 0, lz1 = 0, lz2 = 0;
    double pb0 = peak20.b0, pb1 = peak20.b1, pb2 = peak20.b2, pa1 = peak20.a1, pa2 = peak20.a2;
    // Compute exact 64-bit double reference directly from filter coefficients via reflection on double fields
    FieldInfo fB0d = typeof(BiquadFilter).GetField("b0d", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fB1d = typeof(BiquadFilter).GetField("b1d", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fB2d = typeof(BiquadFilter).GetField("b2d", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fA1d = typeof(BiquadFilter).GetField("a1d", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo fA2d = typeof(BiquadFilter).GetField("a2d", BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(fB0d != null && fA1d != null, "BiquadFilter 64-bit double coefficients must exist.");
    pb0 = (double)fB0d.GetValue(peak20); pb1 = (double)fB1d.GetValue(peak20); pb2 = (double)fB2d.GetValue(peak20);
    pa1 = (double)fA1d.GetValue(peak20); pa2 = (double)fA2d.GetValue(peak20);
    double lb0 = (double)fB0d.GetValue(lp20); double lb1 = (double)fB1d.GetValue(lp20); double lb2 = (double)fB2d.GetValue(lp20);
    double la1 = (double)fA1d.GetValue(lp20); double la2 = (double)fA2d.GetValue(lp20);
    double[] refPeak = new double[biquadLen];
    double[] refLp = new double[biquadLen];
    for (int i = 0; i < biquadLen; i++) {
        float inp = 0.5f * MathF.Sin(2f * MathF.PI * 20f * i / sr) + 0.1f * MathF.Sin(2f * MathF.PI * 1000f * i / sr);
        sigPeak[i] = inp;
        sigLp[i] = inp;
        double py = pb0 * inp + pz1;
        pz1 = pb1 * inp - pa1 * py + pz2;
        pz2 = pb2 * inp - pa2 * py;
        refPeak[i] = py;
        double ly = lb0 * inp + lz1;
        lz1 = lb1 * inp - la1 * ly + lz2;
        lz2 = lb2 * inp - la2 * ly;
        refLp[i] = ly;
    }
    peak20.Process(sigPeak);
    lp20.Process(sigLp);
    double peakNoiseSq = 0, lpNoiseSq = 0;
    for (int i = sr / 4; i < biquadLen; i++) {
        double ep = sigPeak[i] - refPeak[i];
        double el = sigLp[i] - refLp[i];
        peakNoiseSq += ep * ep;
        lpNoiseSq += el * el;
    }
    double peakNoiseDbfs = 10.0 * Math.Log10(peakNoiseSq / (biquadLen - sr / 4) + 1e-30);
    double lpNoiseDbfs = 10.0 * Math.Log10(lpNoiseSq / (biquadLen - sr / 4) + 1e-30);
    Assert(peakNoiseDbfs <= -110.0 && lpNoiseDbfs <= -110.0,
        $"20 Hz 64-bit TDF-II noise floor > -110 dBFS: peak={peakNoiseDbfs:F1} dBFS, lp={lpNoiseDbfs:F1} dBFS");

    // 3. CytomicSvfFilter live sweep (<= -80 dBFS zipper, 0 B RT alloc)
    CytomicSvfFilter svf = new(sr, CytomicSvfType.Lowpass, 400.0, 0.70710678, 0.0, 10.0);
    float[] svfBuf = new float[256];
    // Warmup
    for (int b = 0; b < 4; b++) {
        for (int i = 0; i < svfBuf.Length; i++) svfBuf[i] = 0.5f * MathF.Sin(2f * MathF.PI * 120f * (b * 256 + i) / sr);
        svf.Process(svfBuf);
    }
    double maxDiffError = 0.0;
    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    double prevOut = svfBuf[^1];
    for (int b = 4; b < 44; b++) {
        svf.CenterFreq = 400.0 + (b - 4) * 90.0; // rapid parameter modulation per 256-sample block
        for (int i = 0; i < svfBuf.Length; i++) {
            svfBuf[i] = 0.5f * MathF.Sin(2f * MathF.PI * 120f * (b * 256 + i) / sr);
        }
        svf.Process(svfBuf);
        for (int i = 0; i < svfBuf.Length; i++) {
            Assert(float.IsFinite(svfBuf[i]), "CytomicSvfFilter emitted non-finite sample during sweep.");
            if (i == 0) {
                double jump = Math.Abs(svfBuf[0] - prevOut);
                double expectedSlope = 0.5 * 2.0 * Math.PI * 120.0 / sr;
                double excess = Math.Max(0.0, jump - expectedSlope * 1.5);
                if (excess > maxDiffError) maxDiffError = excess;
            }
        }
        prevOut = svfBuf[^1];
    }
    long svfAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
    double zipperDbfs = 20.0 * Math.Log10(maxDiffError + 1e-12);
    Assert(svfAlloc == 0, $"CytomicSvfFilter allocated {svfAlloc} B during live sweep.");
    Assert(zipperDbfs <= -80.0, $"CytomicSvfFilter live sweep zipper discontinuity {zipperDbfs:F1} dBFS > -80 dBFS");

    // 4. LR4 Crossover (Order = 2): flat summation <= 0.05 dB ripple across passband
    Crossover xover = new(sr, 1000.0, 2);
    int impLen = 4096;
    float[] imp = new float[impLen];
    imp[0] = 1f;
    xover.Process(imp);
    float[] sum = new float[impLen];
    for (int i = 0; i < impLen; i++) {
        sum[i] = xover.LowOutput[i] + xover.HighOutput[i];
    }
    Complex[] sumFft = sum.FFT();
    double maxRippleDb = 0.0;
    for (int bin = 2; bin < impLen / 2 - 4; bin++) {
        double mag = Math.Sqrt(sumFft[bin].Real * sumFft[bin].Real + sumFft[bin].Imaginary * sumFft[bin].Imaginary);
        double db = Math.Abs(20.0 * Math.Log10(Math.Max(mag, 1e-9)));
        if (db > maxRippleDb) maxRippleDb = db;
    }
    Assert(maxRippleDb <= 0.05, $"LR4 Crossover sum ripple {maxRippleDb:F4} dB > 0.05 dB");
});

// ---------- Test 53 (Wave W57.1 Q1-Q2): PolyphaseSincResampler 44.1->48k, DirectionalRenderer BS.2127 VBAP, JOC Hilbert ----------
RunTest("T53_W57_1_PolyphaseSincResampler_DirectionalVbap_JocHilbert", () => {
    // 1. PolyphaseSincResampler 44.1->48 kHz stopband rejection >= 80 dB
    const int inSr = 44100;
    const int outSr = 48000;
    int inN = 4410; // 100 ms
    int outN = 4800;
    float[] inTone = new float[inN];
    // Feed 21 kHz tone (near 44.1k Nyquist): alias/image mirror at 44100 - 21000 = 23100 Hz in 48 kHz output
    for (int i = 0; i < inN; i++) {
        inTone[i] = MathF.Sin(2f * MathF.PI * 21000f * i / inSr);
    }
    PolyphaseSincResampler resamp = new();
    float[] outTone = new float[outN];
    double stepRatio = (double)inSr / outSr;
    resamp.Process(inTone, inN, outTone, outN, stepRatio);
    // Steady-state 4096-point FFT skipping initial HalfTaps=32 causal latency
    float[] steadyOut = new float[4096];
    Array.Copy(outTone, 64, steadyOut, 0, 4096);
    for (int i = 0; i < 4096; i++) {
        // Hann window to avoid spectral leakage
        steadyOut[i] *= 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / 4095f));
    }
    Complex[] spec = steadyOut.FFT();
    int sigBin = (int)Math.Round(21000.0 * 4096 / outSr);
    int imgBin = (int)Math.Round(23100.0 * 4096 / outSr);
    double sigPeak = 0.0, imgPeak = 0.0;
    for (int b = sigBin - 3; b <= sigBin + 3; b++) {
        double m = spec[b].Magnitude;
        if (m > sigPeak) sigPeak = m;
    }
    for (int b = imgBin - 3; b <= imgBin + 3; b++) {
        double m = spec[b].Magnitude;
        if (m > imgPeak) imgPeak = m;
    }
    double stopbandDb = 20.0 * Math.Log10((sigPeak + 1e-12) / (imgPeak + 1e-12));
    Assert(stopbandDb >= 80.0, $"PolyphaseSincResampler 44.1->48k stopband rejection {stopbandDb:F1} dB < 80 dB");

    // 2. Source 44.1->48 kHz streaming: 0 B steady-state GC, 0 boundary clicks, accounting for HalfTaps=32 causal latency
    using (VirtualizerFilter.BeginStaticStateScope()) {
        Listener.HeadphoneVirtualizer = false;
        Listener.ReplaceChannels(new Channel[] {
            new Channel(-30f, 0f),
            new Channel(30f, 0f)
        });
        Listener l = new(false) { SampleRate = 48000, UpdateRate = 480, AudioQuality = QualityModes.High };
        float[] srcPcm = new float[44100];
        for (int i = 0; i < srcPcm.Length; i++) {
            srcPcm[i] = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * i / 44100f);
        }
        Source src = new() { Clip = new Clip(srcPcm, 1, 44100), Position = new Vector3(0f, 0f, 5f), Volume = 1f, SpatialBlend = 1f, Loop = true };
        l.AttachSource(src);
        // Warmup past HalfTaps=32 causal latency + steady buffer growth
        for (int b = 0; b < 5; b++) l.Render(1);
        long allocBeforeSrc = GC.GetAllocatedBytesForCurrentThread();
        float prevL = 0f;
        double maxBoundaryJump = 0.0;
        for (int b = 0; b < 10; b++) {
            float[] ren = l.Render(1);
            if (b > 0) {
                double jump = Math.Abs(ren[0] - prevL);
                if (jump > maxBoundaryJump) maxBoundaryJump = jump;
            }
            prevL = ren[ren.Length - 2];
        }
        long srcAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeSrc;
        Assert(srcAlloc == 0, $"Source 44.1->48k allocated {srcAlloc} B in steady state.");
        Assert(maxBoundaryJump < 0.08, $"Source 44.1->48k boundary jump={maxBoundaryJump:F4} indicates click.");

        // 3. DirectionalRenderer: 0 B steady-state RT GC & UseBs2127Vbap=true
        DirectionalRenderer dirRen = new() { UseBs2127Vbap = true };
        float[] dirIn = new float[480];
        for (int i = 0; i < dirIn.Length; i++) dirIn[i] = 0.4f * MathF.Sin(2f * MathF.PI * 500f * i / 48000f);
        float[] dirOut = new float[480 * Listener.Channels.Length];
        Vector3 testDir = new(0.25f, 0.1f, 0.96f);
        dirRen.Render(l, src, testDir, dirIn, dirOut, 1f); // warmup
        long allocBeforeDir = GC.GetAllocatedBytesForCurrentThread();
        Array.Clear(dirOut, 0, dirOut.Length);
        dirRen.Render(l, src, testDir, dirIn, dirOut, 1f);
        long dirAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeDir;
        Assert(dirAlloc == 0, $"DirectionalRenderer(UseBs2127Vbap=true) allocated {dirAlloc} B.");
        double dirEnergy = 0.0;
        for (int i = 0; i < dirOut.Length; i++) dirEnergy += dirOut[i] * dirOut[i];
        Assert(dirEnergy > 1.0, $"DirectionalRenderer(UseBs2127Vbap=true) produced near-zero energy: {dirEnergy}");
    }

    // 4. JointObjectCodingApplier Hilbert phase shifter (<= 2.5 deg error around 90 deg, 0 B RT alloc, inline loop)
    Assembly fmtAsm = typeof(AudioReader).Assembly;
    Type hilbertType = fmtAsm.GetType("Cavern.Format.Decoders.EnhancedAC3.HilbertAllpassBranch");
    Type jocApplierType = fmtAsm.GetType("Cavern.Format.Decoders.EnhancedAC3.JointObjectCodingApplier");
    Assert(hilbertType != null && jocApplierType != null, "HilbertAllpassBranch and JointObjectCodingApplier types must exist in Cavern.Format.");
    Assert(jocApplierType.GetField("taskWaiter", BindingFlags.Instance | BindingFlags.NonPublic) == null,
        "JointObjectCodingApplier must run inline without ManualResetEventSlim taskWaiter.");

    Filter branchRef = (Filter)Activator.CreateInstance(hilbertType, new object[] { false });
    Filter branchQuad = (Filter)Activator.CreateInstance(hilbertType, new object[] { true });
    int hLen = 4096;
    float[] bufRef = new float[hLen];
    float[] bufQuad = new float[hLen];
    double[] testFreqs = { 200.0, 1000.0, 5000.0, 12000.0 };
    foreach (double fHz in testFreqs) {
        ((Cavern.Filters.Interfaces.IResettableFilter)branchRef).Reset();
        ((Cavern.Filters.Interfaces.IResettableFilter)branchQuad).Reset();
        for (int i = 0; i < hLen; i++) {
            float s = 0.5f * MathF.Sin(2f * MathF.PI * (float)fHz * i / 48000f);
            bufRef[i] = s;
            bufQuad[i] = s;
        }
        branchRef.Process(bufRef);
        branchQuad.Process(bufQuad);
        // Measure normalized cross-correlation (dot / (normA * normB)) in steady half: cos(deltaPhase) == 0 for 90 deg
        double dot = 0.0, nA = 0.0, nB = 0.0;
        for (int i = hLen / 2; i < hLen; i++) {
            dot += (double)bufRef[i] * bufQuad[i];
            nA += (double)bufRef[i] * bufRef[i];
            nB += (double)bufQuad[i] * bufQuad[i];
        }
        double cosPhi = Math.Clamp(dot / Math.Sqrt(nA * nB + 1e-24), -1.0, 1.0);
        double phaseDeg = Math.Acos(cosPhi) * (180.0 / Math.PI);
        double errDeg = Math.Abs(phaseDeg - 90.0);
        Assert(errDeg <= 2.5, $"HilbertAllpassBranch phase error at {fHz} Hz = {errDeg:F2} deg > 2.5 deg (measured {phaseDeg:F2} deg)");
    }
    long allocBeforeHilbert = GC.GetAllocatedBytesForCurrentThread();
    branchRef.Process(bufRef);
    branchQuad.Process(bufQuad);
    long hilbertAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeHilbert;
    Assert(hilbertAlloc == 0, $"HilbertAllpassBranch.Process allocated {hilbertAlloc} B.");

    // Exercise JointObjectCodingApplier.Apply inline with LastPairPhaseShift=true & 0 B steady-state alloc
    Type jocType = fmtAsm.GetType("Cavern.Format.Decoders.EnhancedAC3.JointObjectCoding");
    object joc = Activator.CreateInstance(jocType, nonPublic: true);
    jocType.GetProperty("ChannelCount").SetValue(joc, 5);
    jocType.GetProperty("ObjectCount").SetValue(joc, 2);
    jocType.GetProperty("LastPairPhaseShift").SetValue(joc, true);
    jocType.GetProperty("Gain").SetValue(joc, 1f);
    jocType.GetMethod("UpdateCache").Invoke(joc, null);
    bool[] objActive = (bool[])jocType.GetField("ObjectActive").GetValue(joc);
    objActive[0] = true; objActive[1] = true;
    float[][][][] interpMtx = (float[][][][])jocType.GetField("interpolatedMatrix", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(joc);
    for (int o = 0; o < 2; o++) {
        for (int ts = 0; ts < interpMtx[o].Length; ts++) {
            interpMtx[o][ts] = new float[5][];
            for (int c = 0; c < 5; c++) {
                interpMtx[o][ts][c] = new float[64];
                if (c == o) {
                    for (int sb = 0; sb < 64; sb++) interpMtx[o][ts][c][sb] = 1f;
                }
            }
        }
    }
    using IDisposable applier = (IDisposable)Activator.CreateInstance(jocApplierType, joc, 1536);
    FieldInfo mixMatrixField = jocApplierType.GetField("mixMatrix", BindingFlags.Instance | BindingFlags.NonPublic);
    FieldInfo timeslotField = jocApplierType.GetField("timeslot", BindingFlags.Instance | BindingFlags.NonPublic);
    mixMatrixField.SetValue(applier, interpMtx);
    MethodInfo applyMethod = jocApplierType.GetMethod("Apply");
    float[][] jocIn = new float[5][];
    for (int c = 0; c < 5; c++) {
        jocIn[c] = new float[64];
        for (int s = 0; s < 64; s++) jocIn[c][s] = 0.25f * MathF.Sin(2f * MathF.PI * 1000f * s / 48000f);
    }
    object[] applyArgs = new object[] { jocIn, joc };
    for (int w = 0; w < 3; w++) {
        timeslotField.SetValue(applier, 1);
        applyMethod.Invoke(applier, applyArgs); // warm up both Apply and .NET 8 MethodInvoker
    }
    timeslotField.SetValue(applier, 2);
    long allocBeforeJoc = GC.GetAllocatedBytesForCurrentThread();
    float[][] jocOut = (float[][])applyMethod.Invoke(applier, applyArgs);
    long jocAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeJoc;
    Assert(jocOut != null && jocOut.Length == 2, "JointObjectCodingApplier.Apply did not return 2 objects.");
    // Note: reflection Invoke boxes nothing when array args preallocated, expect 0 B managed allocation
    Assert(jocAlloc == 0, $"JointObjectCodingApplier.Apply allocated {jocAlloc} B in steady state.");
});

// ---------- Test 54 (Wave W55.2): SpatialChoreoTrack Stem Activity, SpatialChoreoRtGate, LyricMoodSpatialModulator, EarCandySpectralDetector ----------
RunTest("T54_W55_2_SpatialChoreoActivity_LyricMood_EarCandy", () => {
    // 1. SpatialChoreoTrack.BuildWithStemActivity: active-only Orbit, dead/silent stems locked static
    string[] stemNames = { "vocals", "bass", "drums", "other" };
    float[] homeAz = { 0f, 0f, -55f, 62f };
    bool[] hasSignal = { true, true, true, false }; // "other" (idx 3) is silent/dead
    SpatialChoreoTrack track = SpatialChoreoTrack.BuildWithStemActivity(
        stemNames,
        Array.Empty<DirectorCue>(),
        SpatialChoreoMode.Orbit,
        durationSec: 24.0,
        stepSec: 0.1,
        stemHomeAzimuthsDeg: homeAz,
        stemHasSignal: hasSignal);
    Assert(track.IsStemAudible(2), "Audible drums stem must be marked audible.");
    Assert(!track.IsStemAudible(3), "Dead/silent other stem must be marked inaudible.");

    Vector3 activePos0 = Vector3.Zero, activePos1 = Vector3.Zero;
    Vector3 deadPos0 = Vector3.Zero, deadPos1 = Vector3.Zero;
    Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(track, SpatialChoreoMode.Orbit, 2, NeuralStemRole.Drums, -1f, 1.0, out activePos0, true),
        "EvaluateStemPositionZeroAlloc failed for active drums.");
    Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(track, SpatialChoreoMode.Orbit, 2, NeuralStemRole.Drums, -1f, 6.0, out activePos1, true),
        "EvaluateStemPositionZeroAlloc failed for active drums at t=6s.");
    Assert(Vector3.Distance(activePos0, activePos1) > 0.05f, "Active non-anchor stem must orbit across time.");

    Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(track, SpatialChoreoMode.Orbit, 3, NeuralStemRole.Other, 1f, 1.0, out deadPos0, true),
        "EvaluateStemPositionZeroAlloc failed for dead stem.");
    Assert(NeuralStemSpatializer.EvaluateStemPositionZeroAlloc(track, SpatialChoreoMode.Orbit, 3, NeuralStemRole.Other, 1f, 6.0, out deadPos1, true),
        "EvaluateStemPositionZeroAlloc failed for dead stem at t=6s.");
    Assert(Vector3.Distance(deadPos0, deadPos1) < 1e-4f, "Dead/silent stem must remain locked static at its anchor.");

    // 2. SpatialChoreoRtGate: 0f on silent/anchor stems, smooth >0 on active stems, 0 B RT alloc
    SpatialChoreoRtGate rtGate = new();
    rtGate.UpdateAndEvaluateZeroAlloc(0, 0.25f, isAnchorRole: false); // warmup
    long gateAllocBefore = GC.GetAllocatedBytesForCurrentThread();
    float anchorGate = rtGate.UpdateAndEvaluateZeroAlloc(1, 0.25f, isAnchorRole: true);
    float silentGate = rtGate.UpdateAndEvaluateZeroAlloc(2, 0.0005f, isAnchorRole: false);
    float activeGate = rtGate.UpdateAndEvaluateZeroAlloc(0, 0.10f, isAnchorRole: false);
    long gateAlloc = GC.GetAllocatedBytesForCurrentThread() - gateAllocBefore;
    Assert(anchorGate == 0f && silentGate == 0f, $"Anchor/silent RT gate must be 0, got anchor={anchorGate}, silent={silentGate}");
    Assert(activeGate > 0.05f && activeGate <= 1f, $"Active stem RT gate expected in (0.05, 1], got {activeGate}");
    Assert(gateAlloc == 0, $"SpatialChoreoRtGate allocated {gateAlloc} B.");

    // 3. LyricMoodSpatialModulator.EvaluateZeroAlloc: arousal/valence/section modulation, zero raw lyric text, 0 B alloc
    FieldInfo[] digestFields = typeof(LyricMoodDigest).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    foreach (FieldInfo f in digestFields) {
        Assert(f.FieldType != typeof(string), $"LyricMoodDigest must never store raw lyric text strings (found {f.Name}).");
    }
    LyricMoodDigest[] digests = {
        new LyricMoodDigest(LyricMoodTag.Intimate, 0.20f, 0.30f, 0, 0.0),
        new LyricMoodDigest(LyricMoodTag.Anthem, 0.90f, 0.85f, 1, 10.0)
    };
    LyricSpatialResponseFx fxWarmup = LyricMoodSpatialModulator.EvaluateZeroAlloc(digests, 5.0, 20.0); // warmup
    long moodAllocBefore = GC.GetAllocatedBytesForCurrentThread();
    LyricSpatialResponseFx fxIntimate = LyricMoodSpatialModulator.EvaluateZeroAlloc(digests, 4.0, 20.0);
    LyricSpatialResponseFx fxBlend = LyricMoodSpatialModulator.EvaluateZeroAlloc(digests, 11.0, 20.0);
    LyricSpatialResponseFx fxAnthem = LyricMoodSpatialModulator.EvaluateZeroAlloc(digests, 15.0, 20.0);
    long moodAlloc = GC.GetAllocatedBytesForCurrentThread() - moodAllocBefore;
    Assert(moodAlloc == 0, $"LyricMoodSpatialModulator.EvaluateZeroAlloc allocated {moodAlloc} B.");
    Assert(fxIntimate.StageWidthScale < fxBlend.StageWidthScale && fxBlend.StageWidthScale < fxAnthem.StageWidthScale,
        $"Expected smooth stage width progression Intimate < Blend < Anthem: {fxIntimate.StageWidthScale:F2}, {fxBlend.StageWidthScale:F2}, {fxAnthem.StageWidthScale:F2}");
    Assert(fxAnthem.ElevationLiftDeg > fxIntimate.ElevationLiftDeg && fxAnthem.RoomReflectionScale > fxIntimate.RoomReflectionScale,
        "Anthem mood must elevate stage and expand room reflection scale relative to Intimate.");

    // 4. EarCandySpectralDetector: 0 B RT alloc & transient/spectral micro-object detection
    EarCandySpectralDetector ec = new();
    float[] stereoSparkle = new float[512 * 2];
    for (int i = 0; i < 512; i++) {
        float chime = 0.35f * MathF.Sin(2f * MathF.PI * 9500f * i / 48000f) * MathF.Exp(-i / 180f);
        stereoSparkle[i * 2] = chime;
        stereoSparkle[i * 2 + 1] = -chime * 0.8f;
    }
    EarCandyMicroNode[] ecSnap = new EarCandyMicroNode[EarCandySpectralDetector.MaxMicroObjects];
    ec.ProcessStereoInterleavedZeroAlloc(stereoSparkle, stereoSparkle.Length, 48000, 1.2f); // warmup
    long ecAllocBefore = GC.GetAllocatedBytesForCurrentThread();
    ec.ProcessStereoInterleavedZeroAlloc(stereoSparkle, stereoSparkle.Length, 48000, 1.2f);
    int copied = ec.CopySnapshotZeroAlloc(ecSnap, out float sparkle01, out int activeNodes);
    long ecAlloc = GC.GetAllocatedBytesForCurrentThread() - ecAllocBefore;
    Assert(ecAlloc == 0, $"EarCandySpectralDetector allocated {ecAlloc} B on RT thread.");
    Assert(copied > 0 && activeNodes > 0 && sparkle01 > 0.05f,
        $"Expected active ear-candy micro-objects for 9.5 kHz chime, got count={activeNodes}, sparkle={sparkle01:F3}");
});

// ---------- Test 55: W57.2 ListenerContext Isolation, Desktop Config Guard, VBR E-AC-3 Seek & Async FFmpeg ----------
RunTest("T55_W57_2_ListenerContext_VbrEac3Seek_AsyncFfmpeg", () => {
    // 1. Concurrent ListenerContext isolation: 7.1.4 Virtualizer playback vs 5.1 Direct export -> 0 cross-corruption
    ListenerContext defaultBefore = ListenerContext.Default;
    int defaultChBefore = defaultBefore.Channels.Length;
    bool defaultVirtBefore = defaultBefore.HeadphoneVirtualizer;

    ListenerContext playbackCtx = new() {
        EnvironmentType = Environments.Studio,
        HeadphoneVirtualizer = true,
        EnvironmentSize = new Vector3(12f, 8f, 12f)
    };
    playbackCtx.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref714));
    Listener playbackListener = new(playbackCtx, loadGlobals: false) {
        SampleRate = 48000,
        UpdateRate = 64,
        BypassInternalVirtualizer = true
    };

    ListenerContext exportCtx = new() {
        EnvironmentType = Environments.Home,
        HeadphoneVirtualizer = false,
        EnvironmentSize = new Vector3(5f, 3f, 5f)
    };
    exportCtx.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.GetStandardMatrix(6)));
    Listener exportListener = new(exportCtx, loadGlobals: false) { SampleRate = 48000, UpdateRate = 64 };

    Assert(playbackListener.Context.Channels.Length == 12, $"Expected 7.1.4 (12ch) playback context, got {playbackListener.Context.Channels.Length}");
    Assert(exportListener.Context.Channels.Length == 6, $"Expected 5.1 (6ch) export context, got {exportListener.Context.Channels.Length}");

    Exception workerEx = null;
    Task tPlayback = Task.Run(() => {
        try {
            for (int i = 0; i < 40; i++) {
                using ListenerContext.Scope scope = playbackCtx.EnterScope();
                Assert(ReferenceEquals(ListenerContext.Current, playbackCtx), "Playback thread lost ListenerContext scope");
                Assert(ListenerContext.Current.HeadphoneVirtualizer, "Playback context virtualizer flag corrupted");
                Assert(ListenerContext.Current.Channels.Length == 12, "Playback context channel count corrupted");
                float[] rendered = playbackListener.Render(1);
                Assert(rendered != null && rendered.Length == 12 * 64, "Playback render buffer layout mismatch");
            }
        } catch (Exception ex) { workerEx ??= ex; }
    });
    Task tExport = Task.Run(() => {
        try {
            for (int i = 0; i < 40; i++) {
                using ListenerContext.Scope scope = exportCtx.EnterScope();
                Assert(ReferenceEquals(ListenerContext.Current, exportCtx), "Export thread lost ListenerContext scope");
                Assert(!ListenerContext.Current.HeadphoneVirtualizer, "Export context virtualizer flag corrupted");
                Assert(ListenerContext.Current.Channels.Length == 6, "Export context channel count corrupted");
                float[] rendered = exportListener.Render(1);
                Assert(rendered != null && rendered.Length == 6 * 64, "Export render buffer layout mismatch");
            }
        } catch (Exception ex) { workerEx ??= ex; }
    });
    Task.WaitAll(tPlayback, tExport);
    if (workerEx != null) {
        throw workerEx;
    }
    Assert(ListenerContext.Default.Channels.Length == defaultChBefore &&
           ListenerContext.Default.HeadphoneVirtualizer == defaultVirtBefore,
        "Concurrent isolated contexts mutated ListenerContext.Default");

    // 2. ListenerContext.IsDesktopConfigSupported & %APPDATA% skip contract
    FieldInfo desktopFlagField = typeof(ListenerContext).GetField("IsDesktopConfigSupported", BindingFlags.Static | BindingFlags.NonPublic);
    MethodInfo detectMethod = typeof(ListenerContext).GetMethod("DetectNonDesktopPlatform", BindingFlags.Static | BindingFlags.NonPublic);
    FieldInfo nonDesktopMethodsField = typeof(ListenerContext).GetField("nonDesktopOsMethods", BindingFlags.Static | BindingFlags.NonPublic);
    Assert(desktopFlagField != null && detectMethod != null && nonDesktopMethodsField != null,
        "ListenerContext must expose IsDesktopConfigSupported / DetectNonDesktopPlatform guards");
    string[] mobileChecks = nonDesktopMethodsField.GetValue(null) as string[];
    Assert(mobileChecks != null && Array.IndexOf(mobileChecks, "IsAndroid") >= 0 && Array.IndexOf(mobileChecks, "IsIOS") >= 0,
        "ListenerContext.nonDesktopOsMethods must guard Android/iOS");
    Listener nonGlobalListener = new(exportCtx, loadGlobals: false);
    Assert(nonGlobalListener.Context == exportCtx, "Listener(context, loadGlobals: false) must preserve isolated context without %APPDATA%");

    // 3. Raw VBR EnhancedAC3Decoder.Seek (<= 1 frame drift across non-uniform frame sizes)
    int[] vbrFrameWords = [48, 64, 80, 52, 96, 56, 88, 60, 112, 68, 76, 64];
    byte[] vbrEac3 = BuildSyntheticVbrEac3Stream(vbrFrameWords);
    using MemoryStream eac3Ms = new(vbrEac3, writable: false);
    BlockBuffer<byte> eac3Buf = BlockBuffer<byte>.Create(eac3Ms, 4096);
    EnhancedAC3Decoder eac3Dec = new(eac3Buf, vbrEac3.Length);
    Assert(eac3Dec.IndexedFrameCount == vbrFrameWords.Length,
        $"Expected {vbrFrameWords.Length} indexed VBR E-AC-3 frames, got {eac3Dec.IndexedFrameCount}");
    Assert(eac3Dec.Length == vbrFrameWords.Length * 1536L,
        $"Expected exact VBR duration {vbrFrameWords.Length * 1536L}, got {eac3Dec.Length}");

    long[] seekTargets = [9 * 1536L + 256L, 3 * 1536L + 128L, 11 * 1536L];
    float[] postSeekPcm = new float[510];
    foreach (long target in seekTargets) {
        eac3Dec.Seek(target);
        long posAfterSeek = eac3Dec.Position;
        Assert(Math.Abs(posAfterSeek - target) <= eac3Dec.FrameSize,
            $"VBR E-AC-3 Seek({target}) drift exceeded 1 frame: Position={posAfterSeek}");
        eac3Dec.DecodeBlock(postSeekPcm, 0, postSeekPcm.Length);
        Assert(eac3Dec.Position == target + postSeekPcm.Length / Math.Max(1, eac3Dec.ChannelCount),
            $"VBR E-AC-3 post-seek DecodeBlock Position mismatch: {eac3Dec.Position}");
    }

    // 4. FFmpeg.FindExecutable + LaunchAsync progress + mid-run cancellation (0 orphan processes)
    if (!OperatingSystem.IsWindows()) {
        string shPath = VoidX.WPF.FFmpeg.FFmpeg.FindExecutable("sh");
        Assert(!string.IsNullOrEmpty(shPath) && File.Exists(shPath), "FFmpeg.FindExecutable(\"sh\") failed on POSIX");

        string tmpDir = Path.Combine(Path.GetTempPath(), $"cavern_w57_ffmpeg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try {
            string progressScript = Path.Combine(tmpDir, "mock_ffmpeg_progress.sh");
            File.WriteAllText(progressScript,
                "#!/bin/sh\n" +
                "echo 'out_time_us=500000'\n" +
                "echo 'out_time_us=1000000'\n" +
                "echo 'progress=end'\n" +
                "exit 0\n");
            File.SetUnixFileMode(progressScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert(VoidX.WPF.FFmpeg.FFmpeg.FindExecutable(progressScript) == Path.GetFullPath(progressScript),
                "FFmpeg.FindExecutable failed on direct script path");

            TestFfmpegRunner runner = new() { Location = progressScript };
            Assert(runner.Found, "TestFfmpegRunner.Found should be true for executable mock script");
            List<double> reported = [];
            object reportLock = new();
            IProgress<double> prog = new SyncProgress(v => { lock (reportLock) reported.Add(v); });
            bool ok = runner.LaunchAsync("-i dummy out.wav", CancellationToken.None, prog, totalDurationSeconds: 2.0).GetAwaiter().GetResult();
            Assert(ok && runner.LastExitCode == 0, $"FFmpeg.LaunchAsync progress run failed: {runner.LastError}");
            Assert(reported.Count >= 2 && reported[^1] == 1.0 && reported[0] > 0.0,
                $"FFmpeg.LaunchAsync progress values invalid: [{string.Join(", ", reported)}]");

            string pidFile = Path.Combine(tmpDir, "child.pid");
            string cancelScript = Path.Combine(tmpDir, "mock_ffmpeg_cancel.sh");
            File.WriteAllText(cancelScript,
                $"#!/bin/sh\necho $$ > \"{pidFile}\"\nsleep 30\n");
            File.SetUnixFileMode(cancelScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            TestFfmpegRunner cancelRunner = new() { Location = cancelScript };
            using CancellationTokenSource cts = new();
            Task<bool> cancelTask = cancelRunner.LaunchAsync("-i slow out.wav", cts.Token);
            for (int w = 0; w < 40 && !File.Exists(pidFile); w++) {
                Thread.Sleep(25);
            }
            Assert(File.Exists(pidFile), "Mock cancel FFmpeg process did not start in time");
            int spawnedPid = int.Parse(File.ReadAllText(pidFile).Trim());
            cts.Cancel();

            bool threwCancel = false;
            try {
                cancelTask.GetAwaiter().GetResult();
            } catch (OperationCanceledException) {
                threwCancel = true;
            }
            Assert(threwCancel, "FFmpeg.LaunchAsync did not throw OperationCanceledException on cancel");

            bool alive = true;
            for (int w = 0; w < 20; w++) {
                try {
                    using Process p = Process.GetProcessById(spawnedPid);
                    if (p.HasExited) { alive = false; break; }
                } catch (ArgumentException) {
                    alive = false;
                    break;
                }
                Thread.Sleep(25);
            }
            Assert(!alive, $"Orphan FFmpeg child process {spawnedPid} remained alive after cancellation");
        } finally {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
});

// ---------- Test 56: W55.3 UiStateCoordinator & Speaker/XTC BRIR+ISM Bypass ----------
RunTest("T56_W55_3_UiStateCoordinator_SpeakerXtcBypass", () => {
    // 1. UiStateCoordinator: IsUpdatingFromCoordinator, RunGuarded reentrancy prevention, coalesced NotifyChanged/FlushPending
    UiStateCoordinator coord = new();
    Assert(!coord.IsUpdatingFromCoordinator, "Coordinator must start outside update guard");

    List<Action> queuedBatches = [];
    coord.SetDispatcher(a => queuedBatches.Add(a));

    List<string> trace = [];
    coord.Subscribe(UiStateDomain.Room3D, () => {
        Assert(coord.IsUpdatingFromCoordinator, "IsUpdatingFromCoordinator must be true inside handler");
        trace.Add("Room3D");
        coord.NotifyChanged(UiStateDomain.Playback); // reentrant notification must not synchronously recurse
    }, order: 50);
    coord.Subscribe(UiStateDomain.Volume, () => {
        Assert(coord.IsUpdatingFromCoordinator, "IsUpdatingFromCoordinator must be true inside Volume handler");
        trace.Add("Volume");
    }, order: 10);
    coord.Subscribe(UiStateDomain.Playback, () => trace.Add("Playback"), order: 60);

    bool guardedFlag = false;
    coord.RunGuarded(() => {
        guardedFlag = coord.IsUpdatingFromCoordinator;
        coord.NotifyChanged(UiStateDomain.Volume); // must not flush immediately while guarded
    });
    Assert(guardedFlag && !coord.IsUpdatingFromCoordinator, "RunGuarded must scope IsUpdatingFromCoordinator cleanly");
    Assert(trace.Count == 0 && queuedBatches.Count == 0, "NotifyChanged inside RunGuarded must not dispatch synchronously");

    coord.NotifyChanged(UiStateDomain.Room3D);
    coord.NotifyChanged(UiStateDomain.Volume | UiStateDomain.Room3D);
    Assert(queuedBatches.Count == 1, $"Expected coalesced single dispatcher callback, got {queuedBatches.Count}");
    queuedBatches[0]();
    Assert(trace.SequenceEqual(["Volume", "Room3D", "Playback"]),
        $"Expected deterministic ordered + cascade flush [Volume, Room3D, Playback], got [{string.Join(", ", trace)}]");
    Assert(!coord.IsUpdatingFromCoordinator, "IsUpdatingFromCoordinator must reset after FlushPending");
    coord.Dispose();

    // 2. Speaker/XTC BRIR+ISM bypass in RealtimePlayerEngine
    using RealtimePlayerEngine engine = new() { HeadphoneVirtualizer = true };
    PhysicalExternalizationConfig ismCfg = new() {
        IsmRoomEnabled = true,
        RoomPreset = IsmRoomPreset.MasteringStudio,
        WallMaterial = IsmWallMaterial.ReflectiveWood,
        ReflectionOrder = 1,
        ReflectionLevel = 0.8f,
        NearFieldEnabled = true,
        AirAbsorptionEnabled = true
    };

    engine.UpdateOutputRouteForTransaural(isHeadphoneRoute: true);
    engine.ApplyTransauralXtcConfig(new TransauralXtcConfig { Enabled = false, AutoBypassOnHeadphones = true });
    engine.ApplyExternalizationConfig(ismCfg);
    Assert(engine.ExternalizationDiagnostics.IsIsmActive && VirtualizerFilter.IsmRoomEnabled,
        "Expected ISM active on headphone route without BRIR or XTC");

    // Switching to speaker route must bypass ISM + NearField and engage externalBrirGuard
    engine.UpdateOutputRouteForTransaural(isHeadphoneRoute: false);
    Assert(!engine.ExternalizationDiagnostics.IsIsmActive && !VirtualizerFilter.IsmRoomEnabled && VirtualizerFilter.ExternalBrirGuardActive,
        "Speaker route must bypass ISM room ambience and set ExternalBrirGuardActive");
    Assert(!VirtualizerFilter.NearFieldCompensationEnabled,
        "Speaker route must disable NearFieldCompensationEnabled");

    // Enabling speaker XTC keeps ISM bypassed
    engine.ApplyTransauralXtcConfig(new TransauralXtcConfig { Enabled = true, AutoBypassOnHeadphones = true });
    Assert(engine.TransauralXtcStage.IsActivelyProcessing && !engine.ExternalizationDiagnostics.IsIsmActive && !VirtualizerFilter.IsmRoomEnabled,
        "Active speaker XTC must keep ISM room ambience bypassed");

    // Returning to headphone route auto-bypasses XTC and restores ISM
    engine.UpdateOutputRouteForTransaural(isHeadphoneRoute: true);
    Assert(!engine.TransauralXtcStage.IsActivelyProcessing && engine.ExternalizationDiagnostics.IsIsmActive && VirtualizerFilter.IsmRoomEnabled,
        "Returning to headphone route must restore ISM room ambience and auto-bypass XTC");
});

if (!string.IsNullOrEmpty(testFilter) && passed == 0) {
    Console.WriteLine($"NO_CS_TESTS_MATCHED filter='{testFilter}'");
    return 0;
}
Console.WriteLine($"\nALL {passed} W46/W48/W49/W50/W51/BP1-3/T2/T3/T5/T6/BP_UI_AI UNIT TESTS PASSED (0 failures).");
return 0;

static byte[] BuildSyntheticVbrEac3Stream(int[] frameWords) {
    using MemoryStream ms = new();
    foreach (int words in frameWords) {
        byte[] frame = new byte[words * 2];
        int bitPos = 0;
        void WriteBits(int val, int bits) {
            for (int b = bits - 1; b >= 0; b--) {
                if (((val >> b) & 1) != 0) {
                    frame[bitPos >> 3] |= (byte)(1 << (7 - (bitPos & 7)));
                }
                bitPos++;
            }
        }
        // E-AC-3 header (54 bits): syncword=0x0B77, strmtyp=0, substream=0, frmsiz=words-1, fscod=0(48k), numblkscod=3(6blks), acmod=3(L/C/R), lfe=0, bsid=16
        WriteBits(0x0B77, 16);
        WriteBits(0, 2);
        WriteBits(0, 3);
        WriteBits(words - 1, 11);
        WriteBits(0, 2);
        WriteBits(3, 2);
        WriteBits(3, 3);
        WriteBits(0, 1);
        WriteBits(16, 5);
        WriteBits(24, 5); // dialnorm
        WriteBits(0, 1);  // compre
        WriteBits(0, 1);  // mixmdate
        WriteBits(0, 1);  // infomdate
        WriteBits(0, 1);  // addbsie
        // Audio frame header: expstre=1, ahte=0, snroffststr=0, flags=0, cplinu[0]=0, cplstre[1..5]=0
        WriteBits(1, 1);
        WriteBits(0, 1);
        WriteBits(0, 2);
        WriteBits(0, 8);
        WriteBits(0, 1); // cplinu[0] = 0
        WriteBits(0, 5); // cplstre[1..5] = 0
        // blk 0: chexpstr[0..2]=1(D15), blk 1..5: chexpstr=0(Reuse)
        for (int ch = 0; ch < 3; ch++) {
            WriteBits(1, 2);
        }
        WriteBits(0, 5 * 3 * 2);
        WriteBits(0, 3 * 5); // convexpstr[0..2]
        WriteBits(0, 6);     // frmcsnroffst
        WriteBits(0, 4);     // frmfsnroffst
        // 6 audio blocks (block 0 carries chbwcod=0 + 12*7+6 exponent bits per channel; blocks 1..5 reuse exponents, zero mantissas)
        for (int blk = 0; blk < 6; blk++) {
            WriteBits(0, 2); // dynrnge=0, spxinu=0
            if (blk == 0) {
                for (int ch = 0; ch < 3; ch++) {
                    WriteBits(0, 6); // chbwcod[ch] = 0 -> endmant = 37, nchgrps = 12
                }
                for (int ch = 0; ch < 3; ch++) {
                    WriteBits(0, 4);  // absexp
                    WriteBits(0, 84); // 12 groups * 7 bits
                    WriteBits(0, 2);  // gainrng
                }
            }
            WriteBits(0, 1); // convsnroffste=0
        }
        ms.Write(frame, 0, frame.Length);
    }
    return ms.ToArray();
}

static int FindFlacAudioFrameStart(byte[] flac) {
    int pos = 4;
    while (pos + 4 <= flac.Length) {
        byte hdr = flac[pos];
        bool isLast = (hdr & 0x80) != 0;
        int len = (flac[pos + 1] << 16) | (flac[pos + 2] << 8) | flac[pos + 3];
        pos += 4 + len;
        if (isLast) return pos;
    }
    return -1;
}

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
            Source bed = new Cavern.SpecialSources.StreamMasterSource(reader, i) {
                LFE = ch == ReferenceChannel.ScreenLFE,
                Position = ChannelPrototype.AlternativePositions[(int)ch] * env
            };
            objects.Add(bed);
        }
        // 2 dynamic moving 3D Dolby Atmos objects
        objects.Add(new Cavern.SpecialSources.StreamMasterSource(reader, Bed51.Length) { Position = new Vector3(-0.4f, 0.3f, 0.7f) * env });
        objects.Add(new Cavern.SpecialSources.StreamMasterSource(reader, Bed51.Length + 1) { Position = new Vector3(0.6f, 0.5f, -0.3f) * env });
        reader.SetupSources(objects, sampleRate);
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

sealed class SyntheticClockReader : AudioReader {
    public readonly SyntheticClockRenderer ClockRenderer;
    public override long Position { get; set; }

    public SyntheticClockReader(int sampleRate, long length) : base(Stream.Null) {
        SampleRate = sampleRate;
        Length = length;
        ChannelCount = 2;
        Bits = BitDepth.Float32;
        ClockRenderer = new SyntheticClockRenderer(sampleRate);
    }

    public override Renderer GetRenderer() => ClockRenderer;
    public override void ReadHeader() { }
    public override void ReadBlock(float[] samples, long from, long to) => Array.Clear(samples, (int)from, (int)(to - from));
    public override void Reset() => Position = 0;
}

sealed class SyntheticClockRenderer : Renderer {
    static readonly ReferenceChannel[] Stereo = [ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight];
    public long TotalDecodedSamples;

    public SyntheticClockRenderer(int sampleRate) : base(null) {
        for (int i = 0; i < Stereo.Length; i++) {
            Source src = new Cavern.SpecialSources.StreamMasterSource(reader, i) {
                Position = ChannelPrototype.AlternativePositions[(int)Stereo[i]] * Listener.EnvironmentSize,
                VolumeRolloff = Rolloffs.Disabled
            };
            objects.Add(src);
        }
        reader.SetupSources(objects, sampleRate);
        objectSamples = new float[Stereo.Length][];
        for (int i = 0; i < objectSamples.Length; i++) objectSamples[i] = new float[256];
    }

    public override ReferenceChannel[] GetChannels() => Stereo;

    public override void Update(int samples) {
        for (int i = 0; i < objectSamples.Length; i++) {
            if (objectSamples[i].Length != samples) objectSamples[i] = new float[samples];
        }
        TotalDecodedSamples += samples;
    }
}

sealed class MockMvsepHttpHandler(byte[] vocWav, byte[] instWav) : HttpMessageHandler {
    public int PollCount;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/app/algorithms", StringComparison.OrdinalIgnoreCase)) {
            string json = "[{\"render_id\":40,\"name\":\"BS-RoFormer\"},{\"render_id\":63,\"name\":\"BS-RoFormer 6-Stem\"}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
        if (path.EndsWith("/separation/create", StringComparison.OrdinalIgnoreCase)) {
            string json = "{\"success\":true,\"data\":{\"hash\":\"mockhash_t25\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
        if (path.EndsWith("/separation/get", StringComparison.OrdinalIgnoreCase)) {
            int p = Interlocked.Increment(ref PollCount);
            string json = p switch {
                1 => "{\"status\":\"waiting\",\"data\":{\"current_order\":2}}",
                2 => "{\"status\":\"processing\",\"data\":{\"current_order\":0}}",
                _ => "{\"status\":\"done\",\"data\":{\"files\":[{\"type\":\"vocals\",\"url\":\"https://files.mvsep.com/vocals.wav\",\"download\":\"vocals.wav\"},{\"type\":\"instrum\",\"url\":\"https://files.mvsep.com/instrum.wav\",\"download\":\"instrum.wav\"}]}}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
        if (path.EndsWith("/vocals.wav", StringComparison.OrdinalIgnoreCase)) {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(vocWav) });
        }
        if (path.EndsWith("/instrum.wav", StringComparison.OrdinalIgnoreCase)) {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(instWav) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class MockMvsepCascadeHttpHandler(
    byte[] s1VocWav,
    byte[] s1BassWav,
    byte[] s1DrumsWav,
    byte[] s1SilentWav,
    byte[] s2LeadWav,
    byte[] s2BackWav) : HttpMessageHandler {
    public sealed record CapturedCreateCall(string SepType, string Opt1, string Opt2, byte[] AudioBytes);
    public readonly List<CapturedCreateCall> CreateCalls = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        string path = request.RequestUri?.AbsolutePath ?? "";
        string query = request.RequestUri?.Query ?? "";
        if (path.EndsWith("/separation/create", StringComparison.OrdinalIgnoreCase)) {
            string sep = "", o1 = "", o2 = "";
            byte[] audioBytes = [];
            if (request.Content is MultipartFormDataContent mp) {
                foreach (HttpContent part in mp) {
                    string name = (part.Headers.ContentDisposition?.Name ?? "").Trim('"');
                    if (name == "sep_type") sep = await part.ReadAsStringAsync(cancellationToken);
                    else if (name == "add_opt1") o1 = await part.ReadAsStringAsync(cancellationToken);
                    else if (name == "add_opt2") o2 = await part.ReadAsStringAsync(cancellationToken);
                    else if (name == "audiofile") audioBytes = await part.ReadAsByteArrayAsync(cancellationToken);
                }
            }
            CreateCalls.Add(new CapturedCreateCall(sep, o1, o2, audioBytes));
            string hash = sep == "49" ? "cascade_s2" : "cascade_s1";
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent($"{{\"success\":true,\"data\":{{\"hash\":\"{hash}\"}}}}", Encoding.UTF8, "application/json")
            };
        }
        if (path.EndsWith("/separation/get", StringComparison.OrdinalIgnoreCase)) {
            if (query.Contains("cascade_s2", StringComparison.OrdinalIgnoreCase)) {
                string s2Json = "{\"status\":\"done\",\"data\":{\"files\":[{\"type\":\"vocals-lead\",\"url\":\"https://files.mvsep.com/s2_lead.wav\",\"download\":\"s2_lead.wav\"},{\"type\":\"vocals-back\",\"url\":\"https://files.mvsep.com/s2_back.wav\",\"download\":\"s2_back.wav\"}]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s2Json, Encoding.UTF8, "application/json") };
            }
            string s1Json = "{\"status\":\"done\",\"data\":{\"files\":[{\"type\":\"vocals\",\"url\":\"https://files.mvsep.com/s1_voc.wav\",\"download\":\"s1_voc.wav\"},{\"type\":\"bass\",\"url\":\"https://files.mvsep.com/s1_bass.wav\",\"download\":\"s1_bass.wav\"},{\"type\":\"drums\",\"url\":\"https://files.mvsep.com/s1_drums.wav\",\"download\":\"s1_drums.wav\"},{\"type\":\"piano\",\"url\":\"https://files.mvsep.com/s1_piano.wav\",\"download\":\"s1_piano.wav\"}]}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s1Json, Encoding.UTF8, "application/json") };
        }
        if (path.EndsWith("/s1_voc.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s1VocWav) };
        if (path.EndsWith("/s1_bass.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s1BassWav) };
        if (path.EndsWith("/s1_drums.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s1DrumsWav) };
        if (path.EndsWith("/s1_piano.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s1SilentWav) };
        if (path.EndsWith("/s2_lead.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s2LeadWav) };
        if (path.EndsWith("/s2_back.wav", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s2BackWav) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

sealed class MockT3MultiModelRuntime : INeuralInferenceRuntime, INeuralModelRegistry {
    readonly string tempDir;
    readonly NeuralModelCatalogEntry[] models;
    readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);

    public MockT3MultiModelRuntime() {
        tempDir = Path.Combine(Path.GetTempPath(), $"cavern_t26_models_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        models = [
            new NeuralModelCatalogEntry {
                Id = "mock_vocals_2s",
                DisplayName = "Mock Vocals 2S",
                FileName = "mock_vocals_2s.onnx",
                Task = NeuralModelTask.VocalSeparation,
                ContractKind = NeuralOnnxContractKind.WaveformStereo,
                ExpectedSampleRate = 44100,
                RecommendedChunkSamples = 44100,
                OutputStemNames = ["vocals", "instrumental"]
            },
            new NeuralModelCatalogEntry {
                Id = "mock_karaoke_2s",
                DisplayName = "Mock Karaoke Lead/Back",
                FileName = "mock_karaoke_2s.onnx",
                Task = NeuralModelTask.KaraokeLeadBacking,
                ContractKind = NeuralOnnxContractKind.WaveformStereo,
                ExpectedSampleRate = 44100,
                RecommendedChunkSamples = 44100,
                OutputStemNames = ["lead_vocal", "backing_vocals"]
            },
            new NeuralModelCatalogEntry {
                Id = "mock_6stem",
                DisplayName = "Mock Full-Band 6S",
                FileName = "mock_6stem.onnx",
                Task = NeuralModelTask.StemSeparation6,
                ContractKind = NeuralOnnxContractKind.WaveformStereo,
                ExpectedSampleRate = 44100,
                RecommendedChunkSamples = 44100,
                OutputStemNames = ["drums", "bass", "other", "vocals", "guitar", "piano"]
            }
        ];
        foreach (NeuralModelCatalogEntry m in models) {
            string p = Path.Combine(tempDir, m.FileName);
            File.WriteAllBytes(p, [0x08, 0x08]);
            paths[m.Id] = p;
        }
    }

    public List<string> CreatedSessionModelPaths { get; } = [];
    public NeuralRuntimeInfo GetRuntimeInfo() => new() { IsAvailable = true, ActiveProvider = "CPU" };
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() => models;
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() => models;
    public string ResolveInstalledModelPath(string modelId) => paths.TryGetValue(modelId ?? "", out string p) ? p : null;
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) {
        CreatedSessionModelPaths.Add(modelPath ?? "");
        return new MockWaveSession(modelPath);
    }
    public void Dispose() {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }

    sealed class MockWaveSession(string path) : INeuralSession {
        public string ModelPath => path;
        public string ExecutionProvider => "CPU";
        public IReadOnlyList<string> InputNames => ["input"];
        public IReadOnlyList<long[]> InputShapes => [[1L, 2L, -1L]];
        public IReadOnlyList<string> OutputNames => ["output"];
        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            int n = (int)inputTensor.Shape[2];
            float[] inp = inputTensor.Data;
            int stems = path.Contains("6stem", StringComparison.OrdinalIgnoreCase) ? 6 : 2;
            float[] outData = new float[stems * 2 * n];
            float scale = 1f / stems;
            for (int s = 0; s < stems; s++) {
                int baseIdx = s * 2 * n;
                for (int i = 0; i < n; i++) {
                    outData[baseIdx + i] = inp[i] * scale;
                    outData[baseIdx + n + i] = inp[n + i] * scale;
                }
            }
            return [new NeuralTensorFloat([1L, stems, 2L, n], outData)];
        }
        public void Dispose() { }
    }
}

sealed class MockBsRoFormerRuntime : INeuralInferenceRuntime {
    public int MultiInputCalls;
    public NeuralRuntimeInfo GetRuntimeInfo() => new() { IsAvailable = true, ActiveProvider = "WebGPU FP16" };
    public string ResolveInstalledModelPath(string modelId) => modelId + ".onnx";
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) => new MockBsSession(this, modelPath);
    public void Dispose() { }

    sealed class MockBsSession(MockBsRoFormerRuntime owner, string path) : INeuralSession {
        public string ModelPath => path;
        public string ExecutionProvider => "WebGPU FP16";
        public IReadOnlyList<string> InputNames => ["spec_real", "spec_imag"];
        public IReadOnlyList<long[]> InputShapes => [[1L, 2L, 1025L, -1L], [1L, 2L, 1025L, -1L]];
        public IReadOnlyList<string> OutputNames => ["out_spec_real", "out_spec_imag"];
        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) => [inputTensor];
        public NeuralTensorFloat[] RunMultiInput(IReadOnlyDictionary<string, NeuralTensorFloat> inputs) {
            Interlocked.Increment(ref owner.MultiInputCalls);
            NeuralTensorFloat srT = inputs["spec_real"];
            NeuralTensorFloat siT = inputs["spec_imag"];
            return [
                new NeuralTensorFloat((long[])srT.Shape.Clone(), (float[])srT.Data.Clone()),
                new NeuralTensorFloat((long[])siT.Shape.Clone(), (float[])siT.Data.Clone())
            ];
        }
        public void Dispose() { }
    }
}

sealed class MockCacheFirstTrackCache(NeuralSeparatedTrack cachedTrack) : INeuralTrackCache {
    public int BestLookupCalls;
    public int SaveCalls;
    public NeuralTrackCacheStats GetStats() => new() { EntryCount = 1, TotalBytes = 1024, MaxBytes = 1024 * 1024 };
    public bool TryLoad(string cacheKey, out NeuralSeparatedTrack track, CancellationToken token = default) {
        track = null;
        return false; // force exact-key miss to test cache-first source-track fallback
    }
    public bool TryLoadBestForSourceTrack(
        string sourceTrackKey,
        string displayTitle,
        string artistName,
        NeuralPipelineGoal goal,
        out NeuralSeparatedTrack track,
        CancellationToken token = default) {
        BestLookupCalls++;
        if (string.Equals(sourceTrackKey, cachedTrack.SourceTrackKey, StringComparison.OrdinalIgnoreCase)) {
            track = cachedTrack;
            return true;
        }
        track = null;
        return false;
    }
    public void Save(string cacheKey, NeuralSeparatedTrack track, CancellationToken token = default) => SaveCalls++;
    public void EvictToLimit(long maxBytes) { }
    public void Clear() { }
}

sealed class MockLowRamFallbackRuntime : INeuralInferenceRuntime, INeuralModelRegistry {
    readonly string tempDir;
    readonly NeuralModelCatalogEntry[] models;
    readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    public int HeavyRejectCount;
    public int LightSuccessCount;

    public MockLowRamFallbackRuntime(bool includeLightweightModel) {
        tempDir = Path.Combine(Path.GetTempPath(), $"cavern_t43_models_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        List<NeuralModelCatalogEntry> list = [
            new NeuralModelCatalogEntry {
                Id = "mel_band_roformer_vocals_kim",
                DisplayName = "Mel-Band RoFormer Vocals",
                FileName = "melband_roformer_vocals.onnx",
                ApproxSizeMb = 953f,
                IsMobileFast = false,
                Task = NeuralModelTask.VocalSeparation,
                ContractKind = NeuralOnnxContractKind.WaveformStereo,
                ExpectedSampleRate = 44100,
                RecommendedChunkSamples = 44100,
                OutputStemNames = ["vocals", "instrumental"]
            }
        ];
        if (includeLightweightModel) {
            list.Add(new NeuralModelCatalogEntry {
                Id = "scnet_4stem_fp16",
                DisplayName = "SCNet 4-Stem FP16",
                FileName = "mock_light_4stem.onnx",
                ApproxSizeMb = 22f,
                IsMobileFast = true,
                Task = NeuralModelTask.StemSeparation4,
                ContractKind = NeuralOnnxContractKind.WaveformStereo,
                ExpectedSampleRate = 44100,
                RecommendedChunkSamples = 44100,
                OutputStemNames = ["drums", "bass", "other", "vocals"]
            });
        }
        models = list.ToArray();
        foreach (NeuralModelCatalogEntry m in models) {
            string p = Path.Combine(tempDir, m.FileName);
            File.WriteAllBytes(p, [0x08, 0x08]);
            paths[m.Id] = p;
        }
    }

    public NeuralRuntimeInfo GetRuntimeInfo() => new() { IsAvailable = true, ActiveProvider = "CPU" };
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() => models;
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() => models;
    public string ResolveInstalledModelPath(string modelId) => paths.TryGetValue(modelId ?? "", out string p) ? p : null;

    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) {
        if (modelPath != null && modelPath.Contains("roformer", StringComparison.OrdinalIgnoreCase)) {
            Interlocked.Increment(ref HeavyRejectCount);
            throw new InsufficientMemoryException("Simulated ONNX memory guard: need=2520.8 MB, avail=2093.8 MB");
        }
        Interlocked.Increment(ref LightSuccessCount);
        return new MockLightWaveSession(modelPath);
    }

    public void Dispose() {
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }

    sealed class MockLightWaveSession(string path) : INeuralSession {
        public string ModelPath => path;
        public string ExecutionProvider => "CPU";
        public IReadOnlyList<string> InputNames => ["input"];
        public IReadOnlyList<long[]> InputShapes => [[1L, 2L, -1L]];
        public IReadOnlyList<string> OutputNames => ["output"];
        public NeuralTensorFloat[] RunSingleInput(NeuralTensorFloat inputTensor) {
            int n = (int)inputTensor.Shape[2];
            float[] inp = inputTensor.Data;
            float[] outData = new float[4 * 2 * n];
            for (int s = 0; s < 4; s++) {
                int baseIdx = s * 2 * n;
                for (int i = 0; i < n; i++) {
                    outData[baseIdx + i] = inp[i] * 0.25f;
                    outData[baseIdx + n + i] = inp[n + i] * 0.25f;
                }
            }
            return [new NeuralTensorFloat([1L, 4L, 2L, n], outData)];
        }
        public void Dispose() { }
    }
}

sealed class TestFfmpegRunner : VoidX.WPF.FFmpeg.FFmpeg {
    public override void UpdateStatusText(string text) { }
}

sealed class SyncProgress(Action<double> onReport) : IProgress<double> {
    public void Report(double value) => onReport(value);
}
"""


def _py_studio_export_lifecycle_check() -> None:
    studio_ui = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.StudioExport.cs"
    sink_cs = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "StreamingExportSink.cs"
    batch_cs = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "StudioBatchExporter.cs"

    ui_src = studio_ui.read_text(encoding="utf-8")
    assert 'S["LgsrMasterNeedsTwin"]' in ui_src, "Expected LgsrMasterNeedsTwin guard in MainActivity.StudioExport.cs"
    m_lgsr = re.search(r'if\s*\(([^;{}]+?)\)\s*\{\s*ShowError\(S\["LgsrMasterNeedsTwin"\]\);', ui_src, re.S)
    assert m_lgsr is not None, "Could not locate LgsrMasterNeedsTwin condition block"
    cond = m_lgsr.group(1)
    assert "EnableLosslessTwinRestoration" in cond and "LosslessCompressed" not in cond and "Flac" not in cond, (
        f"LgsrMasterNeedsTwin must be gated strictly on EnableLosslessTwinRestoration, not native FLAC: {cond}"
    )

    sink_src = sink_cs.read_text(encoding="utf-8")
    for token in ("enum StudioCompressionTier", "MasterPcm", "LosslessCompressed", "PortableLossy", "MaxBlockBytes = 256 * 1024", "SanitizeAndClamp"):
        assert token in sink_src, f"Missing {token} in StreamingExportSink.cs"

    batch_src = batch_cs.read_text(encoding="utf-8")
    assert "bool IsCancelled" in batch_src, "Missing IsCancelled on StudioBatchExportResult"
    assert "StudioCompressionTier CompressionTier" in batch_src, "Missing CompressionTier on StudioBatchExportJob/Result"


def _py_spatial_choreo_cntc_and_mood_check() -> None:
    codec_cs = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralTrackCacheCodec.cs"
    cache_cs = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NeuralTrackCacheService.cs"
    lyrics_cs = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LyricsService.cs"
    cortex_cs = REPO_ROOT / "Cavern" / "Spatial" / "SpatialObjectCortex.cs"

    codec_src = codec_cs.read_text(encoding="utf-8")
    for token in ("FormatVersion = 3", "MinSupportedVersion = 1", "FlagHasChoreoTrack = 1u << 4", "ver >= 2 && (flags & FlagHasChoreoTrack) != 0"):
        assert token in codec_src, f"Missing CNTC v1/v2/v3 contract token in NeuralTrackCacheCodec.cs: {token}"

    cache_src = cache_cs.read_text(encoding="utf-8")
    for token in ("NeuralTrackCacheCodec.WriteTrackFile", "NeuralTrackCacheCodec.ReadTrackFile", "NeuralTrackCacheCodec.PeekHeader"):
        assert token in cache_src, f"NeuralTrackCacheService.cs must delegate to {token}"

    lyrics_src = lyrics_cs.read_text(encoding="utf-8")
    assert "public static LyricMoodDigest[] ExtractMoodDigest(LyricsDocument doc)" in lyrics_src, (
        "ExtractMoodDigest signature missing in LyricsService.cs"
    )
    cortex_src = cortex_cs.read_text(encoding="utf-8")
    m_digest = re.search(r"public struct LyricMoodDigest\s*\{(.*?)\n\s*\}", cortex_src, re.S)
    assert m_digest is not None, "LyricMoodDigest struct not found in SpatialObjectCortex.cs"
    assert "string" not in m_digest.group(1), "LyricMoodDigest must never store raw string fields (zero raw text leakage)."


def _py_stage2_runtime_stability_contracts() -> None:
    tag_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AudioTagReader.cs").read_text(encoding="utf-8")
    for tok in ("public long DurationMs", "ParseMp4Mvhd", "blockType == 0 && blockLen >= 34", "avgBytesPerSec"):
        assert tok in tag_src, f"Missing {tok} in AudioTagReader.cs"

    lib_svc = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "MediaLibraryService.cs").read_text(encoding="utf-8")
    for tok in ('"library_cache_v5.json"', "public bool HasNoAudioTrack", "!current.HasNoAudioTrack"):
        assert tok in lib_svc, f"Missing {tok} in MediaLibraryService.cs"

    lib_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Library.cs").read_text(encoding="utf-8")
    for tok in ("NormalizeSearchFold", "MatchesSearchQuery", "ShouldFilterDrilldownTracks", "cachedSmartCleanEntries != null"):
        assert tok in lib_ui, f"Missing {tok} in MainActivity.PlayerMusic.Library.cs"

    lib_views = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.LibraryViews.cs").read_text(encoding="utf-8")
    assert lib_views.count("ShouldFilterDrilldownTracks") >= 3, "Expected ShouldFilterDrilldownTracks in Album, Artist, and Folder drilldowns"

    rt_tel = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AndroidRuntimeTelemetry.cs").read_text(encoding="utf-8")
    for tok in ("AttachFrameMetrics", "ReadHistoricalExitReasons", "BuildAudioXRunObject", "ApplyBackgroundThreadQoS", "GetAdaptiveThermalYieldMs"):
        assert tok in rt_tel, f"Missing {tok} in AndroidRuntimeTelemetry.cs"

    ort_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.cs").read_text(encoding="utf-8")
    assert "session.intra_op.allow_spinning" in ort_src, "Missing allow_spinning=0 in NativeOnnxRuntimeBridge.cs"

    audit_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.SelfAudit.cs").read_text(encoding="utf-8")
    assert "(vsyncSampled && dropped >= 4)" in audit_src, "SelfAudit must gate dropped>=4 SLOW check on vsyncSampled"


def _py_stage3_audio_ai_mvsep_cntc_contracts() -> None:
    settings_neural = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Neural.cs").read_text(encoding="utf-8")
    for tok in ("ShowMvsepCloudConfigModal", "ShowNeuralTrackCacheBrowserModal", "neuralExecBackendSelector", "neuralStudioBundleSelector", "neuralTrackCacheEnabledSwitch"):
        assert tok in settings_neural, f"Missing {tok} in MainActivity.Settings.Neural.cs"

    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    for tok in ("CanUseMvsepCloud", "TogglePureInstrumentalMode", "DecodeTempStemBytesWithFfmpeg", "MvsepCloudClient"):
        assert tok in wf_src, f"Missing {tok} in MainActivity.NeuralWorkflows.cs"

    studio_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.StudioExport.cs").read_text(encoding="utf-8")
    for tok in ("studioAiModelPickerBtn", "studioOptRemoveBackingSwitch", "StudioTagPureInstrFmt"):
        assert tok in studio_src, f"Missing {tok} in MainActivity.StudioExport.cs"
    assert "studioBundleSelector" not in studio_src, "Duplicate studioBundleSelector must be removed from MainActivity.StudioExport.cs"

    pm_core = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Core.cs").read_text(encoding="utf-8")
    for tok in ("musicLyricsPureInstrBtn", "TogglePureInstrumentalMode"):
        assert tok in pm_core, f"Missing {tok} in MainActivity.PlayerMusic.Core.cs"

    np_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.NowPlaying.cs").read_text(encoding="utf-8")
    for tok in ("musicLyricsPureInstrBtn", "SingDockPureInstrChip", "SingDockKeepBackingChip"):
        assert tok in np_src, f"Missing {tok} in MainActivity.PlayerMusic.NowPlaying.cs"


def _py_choreography_no_ui_audio_tug_of_war() -> None:
    player_tab = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerTab.cs").read_text(encoding="utf-8")
    m_canvas = re.search(r"void UpdateRoomCanvas\(\)\s*\{(.*?)\n    \}", player_tab, re.S)
    assert m_canvas is not None, "Could not locate UpdateRoomCanvas() in MainActivity.PlayerTab.cs"
    assert "SyncTelemetryFromTrackItems" not in m_canvas.group(1), (
        "UpdateRoomCanvas() must NOT mutate trackSourceItems via SyncTelemetryFromTrackItems on the UI thread (causes audio-vs-UI tug-of-war)."
    )
    assert "SyncChoreographyPositions()" in player_tab, (
        "MainActivity.PlayerTab.cs must call playerEngine.SyncChoreographyPositions() on choreography mode/track changes."
    )

    hub_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "SpatialSceneHub.cs").read_text(encoding="utf-8")
    assert "desc.IsLfe || desc.BedChannel.HasValue" in hub_src, (
        "SpatialSceneHub.RebuildChoreoMappingLocked must exclude IsLfe and fixed BedChannel sources."
    )


def _py_stage4_ux_motion_orbit_lyrics_contracts() -> None:
    store_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.cs").read_text(encoding="utf-8")
    for tok in ("RoomInvertOrbitDrag", "LyricsHighlightStyleIndex"):
        assert tok in store_src, f"Missing {tok} in AppSettingsStore.cs"
    assert re.search(r'GetBoolean\("roomInvertOrbitDrag",\s*false\)', store_src), (
        "RoomInvertOrbitDrag must default to false (direct intuitive finger-following drag)."
    )
    assert re.search(r'GetInt\("lyricsHighlightStyleIndex",\s*0\)', store_src), (
        "LyricsHighlightStyleIndex must default to 0 (SmoothGlow)."
    )

    room_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Views" / "RoomSimulatorView.cs").read_text(encoding="utf-8")
    for tok in ("public bool InvertOrbitDrag", "ComputeOrbitDelta", "invertDrag ? -1f : 1f"):
        assert tok in room_src, f"Missing {tok} in RoomSimulatorView.cs"
    assert "camYawDeg - stepX" not in room_src, "Legacy inverted horizontal orbit drag formula must be removed from RoomSimulatorView.cs"

    lyrics_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Views" / "MediaStageViews.cs").read_text(encoding="utf-8")
    for tok in (
        "public enum LyricsWordHighlightStyle",
        "SmoothGlow = 0",
        "MinimalClean = 1",
        "SoftWordPill = 2",
        "ComputeSmoothWordProgress",
        "ComputeActiveWordBellEnvelope",
        "wordHighlightStyle == LyricsWordHighlightStyle.SoftWordPill",
    ):
        assert tok in lyrics_src, f"Missing {tok} in MediaStageViews.cs"
    m_draw = re.search(r"void DrawSyllableSyncedLine\([^\)]*\)\s*\{(.*?)\n        \}", lyrics_src, re.S)
    assert m_draw is not None, "Could not locate DrawSyllableSyncedLine in MediaStageViews.cs"
    draw_body = m_draw.group(1)
    assert "new " not in draw_body, "DrawSyllableSyncedLine must remain zero-allocation (found 'new ' inside method body)."
    assert "SoftWordPill" in draw_body, "DrawSyllableSyncedLine must gate DrawRoundRect pill glow behind SoftWordPill opt-in style."

    main_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.cs").read_text(encoding="utf-8")
    nav_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Navigation.cs").read_text(encoding="utf-8")
    assert "workspaceChromeHost" in main_src and "CreateWorkspaceSourceBar()" in main_src, (
        "MainActivity.cs must hoist a single persistent workspaceChromeHost containing CreateWorkspaceSourceBar()."
    )
    for tok in ("UpdateWorkspaceChromeVisibility", "crossingPlayBoundary", "workspaceChromeAnimGen"):
        assert tok in nav_src, f"Missing {tok} in MainActivity.Navigation.cs"

    for tab_file in ("MainActivity.PlayerTab.cs", "MainActivity.ConverterTab.cs", "MainActivity.ExtractorTab.cs"):
        tab_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / tab_file).read_text(encoding="utf-8")
        assert "CreateWorkspaceSourceBar()" not in tab_src, (
            f"{tab_file} must NOT duplicate CreateWorkspaceSourceBar() (hoisted into persistent workspaceChromeHost)."
        )

    player_tab = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerTab.cs").read_text(encoding="utf-8")
    for tok in ("RefreshSpatialChoreoClarifierUi", "spatialChoreoTierBadgeText", "InvertOrbitDrag"):
        assert tok in player_tab, f"Missing {tok} in MainActivity.PlayerTab.cs"
    assert "roomInvertOrbitSwitch" not in player_tab, "Duplicate roomInvertOrbitSwitch must be removed from MainActivity.PlayerTab.cs"

    pm_core = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Core.cs").read_text(encoding="utf-8")
    pm_np = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.NowPlaying.cs").read_text(encoding="utf-8")
    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    ui_helpers = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.UiHelpers.cs").read_text(encoding="utf-8")

    assert "musicUpmixToggleBtn" in pm_core and "musicUpmixToggleBtn" in pm_np, "Missing unified musicUpmixToggleBtn in Play tab"
    assert "musicCavernizeToggleBtn" not in pm_core and "musicMatrixToggleBtn" not in pm_core, (
        "Overlapping musicCavernizeToggleBtn/musicMatrixToggleBtn pills must be consolidated into musicUpmixToggleBtn."
    )
    for tok in ("ShowPlayTabSpatialUpmixDialog", "ShowPlayTabSpatialHierarchyHelpDialog"):
        assert tok in wf_src, f"Missing {tok} in MainActivity.NeuralWorkflows.cs"
    assert "OneUiIconType.Info" in ui_helpers, "CreateInlineHelpBtn must use OneUiIconType.Info"

    settings_audio = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Audio.cs").read_text(encoding="utf-8")
    for tok in ("settingsRoomInvertOrbitSwitch", "lyricsHighlightSelector", "SyncRoomInvertOrbitUi"):
        assert tok in settings_audio, f"Missing {tok} in MainActivity.Settings.Audio.cs"

    oneui_ref = (REPO_ROOT / ".agents" / "docs" / "ONEUI9_REFERENCE.md").read_text(encoding="utf-8")
    assert "## 9. One UI 9 Motion" in oneui_ref and "workspaceChromeHost" in oneui_ref, (
        "ONEUI9_REFERENCE.md must document Section 9 One UI 9 Motion & Smart Chrome-Aware Tab Transitions."
    )

    t4_keys = (
        "PlayTabUpmixDirectShort",
        "PlayTabUpmixMatrixShort",
        "PlayTabUpmix3DShort",
        "PlayTabUpmixDialogDesc",
        "PlayTabSpatialGuideTitle",
        "PlayTabSpatialGuideBody",
        "RoomInvertOrbitTitle",
        "TipRoomInvertOrbit",
        "LyricsHighlightStyleTitle",
        "LyricsStyleSmoothGlow",
        "LyricsStyleMinimalClean",
        "LyricsStyleSoftPill",
        "TipLyricsHighlightStyle",
        "SpatialChoreoDescOff",
        "SpatialChoreoDescSubtle",
        "SpatialChoreoDescMusical",
        "SpatialChoreoDescCinematic",
        "SpatialChoreoDescOrbit",
        "SpatialTierDirect",
        "SpatialTierMatrix71",
        "SpatialTierCavernize3D",
        "SpatialTierAiStems3D",
        "SpatialTierAiHybrid714",
        "SpatialTierNativeAtmos",
        "SpatialTierAtmosAi",
        "SpatialChoreoClarifierFmt",
        "TipLoudnessNorm",
        "TipTruePeakCeiling",
        "TipIsmRoomPreset",
        "TipIsmWallMaterial",
        "TipIsmOrder",
        "TipMagLsMode",
        "TipMagLsCutoff",
        "TipNeuralProvider",
        "TipNeuralTrackCacheComp",
        "TipNeuralEnsemble",
        "TipNeuralConservation",
        "TipKaraokeLosslessTwin",
        "TipLgsrRejectFake",
        "TipHeadTrackerSource",
        "TipProAudioPath",
        "TipProAudioBuffer",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in t4_keys:
            assert f'["{key}"]' in lang_src, f"Missing T4 localization key '{key}' in {lang_file}"


def _py_stage5_studio_export_hygiene_and_locate_wiring() -> None:
    file_helper = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AndroidFileHelper.cs").read_text(encoding="utf-8")
    for tok in ("enum ExportStorageDestination", "MusicCavernize", "DownloadsCavernize", "PublishExportedAudio", "GetExportWorkingDir", "IsStandardAndroidMusicExtension", "MediaScannerConnection"):
        assert tok in file_helper, f"Missing {tok} in AndroidFileHelper.cs"

    tag_reader = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AudioTagReader.cs").read_text(encoding="utf-8")
    for tok in ("isRf64", "ds64", "OpusTags", "APETAGEX", "ParseOggOpusTags", "ParseApeV2FooterTags", "HasEmbeddedArtwork"):
        assert tok in tag_reader, f"Missing {tok} in AudioTagReader.cs"

    lyrics_svc = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LyricsService.cs").read_text(encoding="utf-8")
    assert "public static string SerializeToLrc(LyricsDocument doc)" in lyrics_svc, "Missing SerializeToLrc in LyricsService.cs"

    meta_helper = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "MediaMetadataHelper.cs").read_text(encoding="utf-8")
    assert "TryExtractCoverArtJpegBytes" in meta_helper, "Missing TryExtractCoverArtJpegBytes in MediaMetadataHelper.cs"

    store_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.cs").read_text(encoding="utf-8")
    for tok in ("StudioDynamicChoreoModeIndex", "StudioExportEmbedCoverArt", "StudioExportEmbedLyrics", "StudioExportFolderIndex"):
        assert tok in store_src, f"Missing {tok} in AppSettingsStore.cs"

    studio_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.StudioExport.cs").read_text(encoding="utf-8")
    for tok in ("studioDynamicMotionSelector", "studioOptEmbedCoverSwitch", "studioOptEmbedLyricsSwitch", "studioExportFolderSelector", "BuildExportMetadataPackageForActiveTrack", "PublishExportedAudio"):
        assert tok in studio_src, f"Missing {tok} in MainActivity.StudioExport.cs"
    assert '"Renderizados"' not in studio_src, "Legacy Renderizados folder must be removed from MainActivity.StudioExport.cs"

    conv_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.ConverterTab.cs").read_text(encoding="utf-8")
    for tok in ("locateExportBtn", "lastExportPathText", "OnLocateExportClicked", "ShowPublishedExportActions", "PublishExportedAudio", "ExportFileNameFormatter.FormatClassicFileName"):
        assert tok in conv_src, f"Missing {tok} in MainActivity.ConverterTab.cs"
    assert '"Renderizados"' not in conv_src, "Legacy Renderizados folder must be removed from MainActivity.ConverterTab.cs"

    t5_keys = (
        "StudioDynamicMotionLabel",
        "TipStudioDynamicMotion",
        "StudioExportDeliveryHeader",
        "StudioOptEmbedCoverTitle",
        "StudioOptEmbedCoverDesc",
        "StudioOptEmbedCoverReadyDesc",
        "StudioOptEmbedLyricsTitle",
        "StudioOptEmbedLyricsDesc",
        "StudioOptEmbedLyricsSyncedReadyDesc",
        "StudioOptEmbedLyricsPlainReadyDesc",
        "StudioExportFolderLabel",
        "TipStudioExportFolder",
        "StudioExportFolderMusic",
        "StudioExportFolderDownloads",
        "StudioExportFolderActiveFmt",
        "StudioExportFolderCompatNoteFmt",
        "StudioTagMotionFmt",
        "LocateExportBtn",
        "LocateExportTitle",
        "ExportSavedToFmt",
        "ExportCompatDownloadsBadge",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in t5_keys:
            assert f'["{key}"]' in lang_src, f"Missing T5 localization key '{key}' in {lang_file}"


def _py_stage6_full_app_sota_audit_and_phase_a_contracts() -> None:
    bp_path = REPO_ROOT / ".agents" / "docs" / "blueprints" / "BP_FULL_APP_SOTA_AUDIT.md"
    assert bp_path.is_file(), "Missing BP_FULL_APP_SOTA_AUDIT.md blueprint"
    bp_src = bp_path.read_text(encoding="utf-8")
    for tok in ("KEEP", "REFACTOR", "ComponentBehaviorProfile", "MultiRefFdkfShield", "AcousticConstraintGovernor", "StudioBatchExporter.cs", "GT-12", "GT-13"):
        assert tok in bp_src, f"Missing '{tok}' in BP_FULL_APP_SOTA_AUDIT.md"

    plan_tool = REPO_ROOT / ".agents" / "plugins" / "dynamic-fractal-planner" / "scripts" / "plan_tool.py"
    lint_proc = subprocess.run(
        [sys.executable, "-B", str(plan_tool), "blueprint", "lint", str(bp_path), "--strict"],
        cwd=str(REPO_ROOT), capture_output=True, text=True, check=False,
    )
    assert lint_proc.returncode == 0 and "BLUEPRINT PASS" in lint_proc.stdout, (
        f"BP_FULL_APP_SOTA_AUDIT.md failed strict blueprint lint: {lint_proc.stdout} {lint_proc.stderr}"
    )

    bridge_main = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LiveDiagnosticBridgeService.cs").read_text(encoding="utf-8")
    bridge_cmds = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LiveDiagnosticBridgeService.Commands.cs").read_text(encoding="utf-8")
    assert '"cortex_profiles"' in bridge_main, "cortex_profiles must be registered in LiveDiagnosticBridgeService.cs"
    assert 'case BridgeCommandOpcode.CortexProfiles:' in bridge_cmds and "BuildCavernDefaultRegistry" in bridge_cmds and "ToCompactManifestJson" in bridge_cmds, (
        "cortex_profiles handler missing in LiveDiagnosticBridgeService.Commands.cs"
    )

    # Enforce strict C# 8.0 (netstandard2.1) hygiene in T6 Cavern/ files
    for rel in ("Cavern/Spatial/ComponentBehaviorProfile.cs", "Cavern/VocalChain/AcousticFeedbackShieldFoundation.cs"):
        src = (REPO_ROOT / rel).read_text(encoding="utf-8")
        assert " is not " not in src, f"C# 9+ 'is not' found in netstandard2.1 file {rel}"
        assert "new()" not in src, f"C# 9+ target-typed 'new()' found in netstandard2.1 file {rel}"
        assert not re.search(r"^\s*namespace\s+[A-Za-z0-9_.]+\s*;\s*$", src, re.M), f"C# 10+ file-scoped namespace in {rel}"


def _py_stage7_ui_ai_org_motion_stability_contracts() -> None:
    bp_path = REPO_ROOT / ".agents" / "docs" / "blueprints" / "BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md"
    ledger_path = REPO_ROOT / ".agents" / "docs" / "archive" / "BLUEPRINTS_DONE_LEDGER.md"
    if bp_path.is_file():
        plan_tool = REPO_ROOT / ".agents" / "plugins" / "dynamic-fractal-planner" / "scripts" / "plan_tool.py"
        lint_proc = subprocess.run(
            [sys.executable, "-B", str(plan_tool), "blueprint", "lint", str(bp_path), "--strict"],
            cwd=str(REPO_ROOT), capture_output=True, text=True, check=False,
        )
        assert lint_proc.returncode == 0 and "BLUEPRINT PASS" in lint_proc.stdout, (
            f"BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md failed strict blueprint lint: {lint_proc.stdout} {lint_proc.stderr}"
        )
    else:
        assert ledger_path.is_file() and "BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY" in ledger_path.read_text(encoding="utf-8"), (
            "Missing BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md (neither active nor archived in BLUEPRINTS_DONE_LEDGER.md)"
        )

    # Q1: AppleMusicLyricsView clamp safety + MediaPlaybackNotificationManager >=500ms dedup
    media_views = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Views" / "MediaStageViews.cs").read_text(encoding="utf-8")
    assert "Math.Max(dimAlpha," in media_views, "Q1 failed: AppleMusicLyricsView must clamp brightAlpha >= dimAlpha"
    notif_mgr = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "MediaPlaybackNotificationManager.cs").read_text(encoding="utf-8")
    assert "500" in notif_mgr, "Q1 failed: MediaPlaybackNotificationManager must throttle updates >=500ms"

    # Q2: Cache-first stem reuse + collision safety + explicit forceReextract bypass
    cache_svc = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NeuralTrackCacheService.cs").read_text(encoding="utf-8")
    for tok in ("TryLoadBestForSourceTrack", "ScoreCacheEntryMatch", "ExtractNormalizedFileStem", "LosslessResidualRestore"):
        assert tok in cache_svc, f"Q2 failed: missing {tok} in NeuralTrackCacheService.cs"
    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    for tok in ("TryLoadBestForSourceTrack", "bool forceReextract = false", "AiCacheReextractCurrentBtn", "OpenAiSettingsBtn", "JumpToSettingsCategory(2)"):
        assert tok in wf_src, f"Q2/Q4 failed: missing {tok} in MainActivity.NeuralWorkflows.cs"

    # Q3: OneUiMotionBlur helper intact + W51.4 GPU hardware-layer transitions + clear on end/cancel + 1:1 album framing + NowPlaying overlay stack
    theme_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Theme" / "OneUiTheme.cs").read_text(encoding="utf-8")
    for tok in ("class OneUiMotionBlur", "ComputeHorizontalBlurRadiusPx", "ComputeBlendedBlurRadiiPx", "ComputeTransitionOpticalDepth", "CreateBlurEffect(clampedX, clampedY", "SetRenderEffect(null)", "EnableHardwareLayer"):
        assert tok in theme_src, f"Q3 failed: missing {tok} in OneUiTheme.cs"
    nav_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Navigation.cs").read_text(encoding="utf-8")
    for tok in ("OneUiMotionBlur.EnableHardwareLayer", "OneUiMotionBlur.ClearHardwareLayer", "OneUiMotionBlur.ClearMotionBlur"):
        assert tok in nav_src, f"Q3/W51.4 failed: missing {tok} in MainActivity.Navigation.cs"
    np_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.NowPlaying.cs").read_text(encoding="utf-8")
    for tok in ("OneUiMotionBlur.EnableHardwareLayer", "OneUiMotionBlur.ClearMotionBlur", "HideSoftInputFromWindow"):
        assert tok in np_src, f"Q3/W51.4 failed: missing {tok} in NowPlaying.cs"
    lib_views = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.LibraryViews.cs").read_text(encoding="utf-8")
    pm_core = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Core.cs").read_text(encoding="utf-8")
    assert "FillWidthSquare = true" in lib_views, "Q3 failed: album tile FillWidthSquare = true missing in LibraryViews.cs"
    for tok in ("RoundedSquareOutlineProvider", "BitmapShader", "FillWidthSquare", "musicSectionStack", "lastMusicScrollHeightPx"):
        assert tok in pm_core, f"Q3 failed: missing {tok} in MainActivity.PlayerMusic.Core.cs"

    # Log-audit stability regressions: CTS ownership, Scarlett/USB PhoneMic preservation + 140ms debounce, version-scoped SelfCheck
    for tok in ("myPrepCts.Dispose()", "myTwinCts.Dispose()", "catch (ObjectDisposedException)"):
        assert tok in wf_src, f"Stability regression: missing {tok} in MainActivity.NeuralWorkflows.cs"
    audio_out = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Audio" / "AndroidAudioOutput.cs").read_text(encoding="utf-8")
    for tok in ("routeDebounceUntilMs", "proAudioConfig.MicSource != ProAudioMicSource.PhoneMic"):
        assert tok in audio_out, f"Stability regression: missing {tok} in AndroidAudioOutput.cs"
    diag_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "SelfDiagnosticEngine.cs").read_text(encoding="utf-8")
    for tok in ("TryExtractLogSessionVersion", "currentVersionFatals", "priorVersionFatals"):
        assert tok in diag_src, f"Stability regression: missing {tok} in SelfDiagnosticEngine.cs"

    # Q4: Grouped Custom Ensemble, Locked Auto, AiComputeBudgetTier, & 4-tab AI IA
    store_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.cs").read_text(encoding="utf-8")
    for tok in ("AiComputeBudgetTier", "SyncPrimarySlotFromTaskSlots", "AndroidRuntimeTelemetry.SetComputeBudgetTier"):
        assert tok in store_src, f"Q4/Q5 failed: missing {tok} in AppSettingsStore.cs"
    neural_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Neural.cs").read_text(encoding="utf-8")
    for tok in ("neuralComputeBudgetSelector", "NeuralOrchestrationAutoLockedHint", "NeuralGroupMultiStem", "NeuralModelAutoLockedTag", "if (toggleEnsemble)"):
        assert tok in neural_ui, f"Q4/Q5 failed: missing {tok} in MainActivity.Settings.Neural.cs"
    player_tab = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerTab.cs").read_text(encoding="utf-8")
    studio_tab = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.StudioExport.cs").read_text(encoding="utf-8")
    assert "JumpToSettingsCategory(2)" in player_tab and "JumpToSettingsCategory(2)" in studio_tab, "Q4 failed: 3D Room / Studio AI settings shortcut missing"

    # Q5: Compute headroom governor across RuntimeTelemetry, NativeOnnxRuntimeBridge, FFmpeg
    telem_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AndroidRuntimeTelemetry.cs").read_text(encoding="utf-8")
    assert "SetComputeBudgetTier" in telem_src and "GlobalComputeBudgetTier" in telem_src, "Q5 failed: AndroidRuntimeTelemetry missing compute budget hook"
    ffmpeg_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "FFmpegInstallerService.cs").read_text(encoding="utf-8")
    assert "-threads" in ffmpeg_src, "Q5 failed: FFmpegInstallerService missing -threads cap"

    # Q6: Two-stage Lyrics credit stripper, fuzzy plain-lyrics alignment, PT-BR/ES mood tokens & 4-lang parity
    lyrics_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LyricsService.cs").read_text(encoding="utf-8")
    for tok in ("IsCreditLegendLine", "StripLeadingTrailingCredits", "AlignAndCleanWithPlainLyrics", "ComputeFuzzyLineSimilarity", "FoldDiacriticsLower", "\"saudade\"", "\"coracao\""):
        assert tok in lyrics_src, f"Q6 failed: missing {tok} in LyricsService.cs"
    meta_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "OnlineMetadataService.cs").read_text(encoding="utf-8")
    assert "AlignAndCleanWithPlainLyrics" in meta_src, "Q6 failed: OnlineMetadataService missing Stage-B plain-lyrics alignment"

    bp_ui_ai_keys = (
        "NeuralReextractStemsBtn",
        "OpenAiSettingsBtn",
        "OpenAiSettingsShortBtn",
        "NeuralComputeBudgetTitle",
        "NeuralComputeBudgetEco",
        "NeuralComputeBudgetBalanced",
        "NeuralComputeBudgetFast",
        "TipNeuralComputeBudget",
        "NeuralOrchestrationAutoLockedHint",
        "NeuralEnsembleCustomHint",
        "NeuralGroupMultiStem",
        "NeuralGroupVocals",
        "NeuralGroupKaraoke",
        "NeuralGroupDereverb",
        "NeuralGroupDebleed",
        "NeuralGroupRestore",
        "NeuralGroupActiveCountFmt",
        "NeuralGroupNoneActive",
        "NeuralModelAutoActiveTag",
        "NeuralModelAutoLockedTag",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in bp_ui_ai_keys:
            assert f'["{key}"]' in lang_src, f"Q6 failed: Missing localization key '{key}' in {lang_file}"


def _py_stage8_scarlett_proaudio_stability_contracts() -> None:
    usb_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "UsbProAudioDeviceManager.cs").read_text(encoding="utf-8")
    assert "QueryAndroidUsbAudioSampleRates(" in usb_src, "BUG #1 regression: UsbProAudioDeviceManager missing Android AudioManager sample-rate query"
    assert "ClaimInterface(acIf, false)" in usb_src, "BUG #1 regression: QueryUac2ClockSampleRatesLocked must use non-destructive ClaimInterface(acIf, false)"
    assert "ClaimInterface(acIf, true)" not in usb_src, "BUG #1 regression: ClaimInterface(acIf, true) detaches snd-usb-audio and breaks AAudio Path A!"
    assert "volatile UsbProAudioDeviceSnapshot snapshot" in usb_src and "volatile ScarlettHardwareState scarlettState" in usb_src, "BUG #5 regression: UsbProAudioDeviceManager snapshots must be volatile/lock-free"
    assert "EnsureScarlettHandshakeLocked" in usb_src and "attempt < 3" in usb_src, "BUG #7 regression: Scarlett Gen 4 FCP 3-attempt retry missing"

    out_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Audio" / "AndroidAudioOutput.cs").read_text(encoding="utf-8")
    for tok in ("activeHalBurstFrames", "adaptiveWatermarkBoostFrames", "lastObservedNativeXRuns", "needStartPlay", "WriteBlockZeroGc(preFrames)"):
        assert tok in out_src, f"BUG #2/#4 regression: missing {tok} in AndroidAudioOutput.cs"
    assert "transportAllowsLowLatTrack" in out_src, "BUG #2 regression: USB/BT AudioTrack must avoid fragile LowLatency mode"

    path_a = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Native" / "cavernize_proaudio" / "cavernize_path_a_aaudio.cpp").read_text(encoding="utf-8")
    for tok in ("cav_rb_apply_glitch_ramp", "rb_underrun_xruns", "playback_primed", "cached_out_xruns"):
        assert tok in path_a, f"BUG #3 regression: missing {tok} in cavernize_path_a_aaudio.cpp"
    fill_idx = path_a.index("void fill_status(")
    fill_body = path_a[fill_idx:fill_idx + 1200]
    assert "lock_guard" not in fill_body, "BUG #5 regression: Path A fill_status must remain lock-free"

    path_b = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Native" / "cavernize_proaudio" / "cavernize_path_b_libusb.cpp").read_text(encoding="utf-8")
    for tok in ("kIsoTransferCount = 6", "kTransientErrorAbortThreshold = 8", "cav_rb_apply_glitch_ramp", "attempt < 3"):
        assert tok in path_b, f"BUG #3/#7 regression: missing {tok} in cavernize_path_b_libusb.cpp"

    stream_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.Streaming.cs").read_text(encoding="utf-8")
    assert "telemetryFramesSincePublish" in stream_src and "SyncSourceNormalizedPositionsLocked()" in stream_src, "BUG #6 regression: RT 20 Hz telemetry throttle or block-accurate position sync missing"

    pro_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.ProAudio.cs").read_text(encoding="utf-8")
    assert "RunScarlettUsbAsync(" in pro_ui, "BUG #5 UI regression: Scarlett USB modal operations must run off UI thread"

    bridge_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.LiveBridge.cs").read_text(encoding="utf-8")
    for tok in ("ApplyProAudioConfig(store.BuildProAudioConfig())", "UsbDacNativeSampleRate"):
        assert tok in bridge_src, f"BUG #8 regression: missing {tok} in MainActivity.LiveBridge.cs"


def _py_stage9_w51_vocal_cache_ux_loudness_volume_contracts() -> None:
    # W51.1: Zero-alloc vocal/backing toggle & no OOM ONNX cascade
    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    m_toggle = re.search(r"void TogglePureInstrumentalMode\([^\)]*\)\s*\{(.*?)\n    \}", wf_src, re.S)
    assert m_toggle is not None, "W51.1: TogglePureInstrumentalMode not found"
    assert "PrepareActiveTrackNeuralStemsAsync" not in m_toggle.group(1), (
        "W51.1 OOM regression: TogglePureInstrumentalMode must NEVER auto-spawn ONNX extraction cascade!"
    )

    proc_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "Adapters" / "NeuralStemStageProcessor.cs").read_text(encoding="utf-8")
    m_rebuild = re.search(r"void RebuildAlignedReferencesLocked\(\)\s*\{(.*?)\n    \}", proc_src, re.S)
    assert m_rebuild is not None, "W51.1: RebuildAlignedReferencesLocked not found"
    assert "KaiserSincResampler" not in m_rebuild.group(1), (
        "W51.1 OOM/glitch regression: RebuildAlignedReferencesLocked must be zero-alloc (no KaiserSincResampler)!"
    )
    assert "ReferenceSampleOffset" in proc_src and "extraLeadLeftScratch" in proc_src, "W51.1/W51.2 missing zero-alloc scratch or offset"

    # W51.2: Unified Atmos <-> FLAC AI cache
    cache_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NeuralTrackCacheService.cs").read_text(encoding="utf-8")
    for tok in ("ExtractCanonicalSongStem", "FindEntriesForTrack", "DeleteEntriesForTrack"):
        assert tok in cache_src, f"W51.2 missing {tok} in NeuralTrackCacheService.cs"
    stream_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.Streaming.cs").read_text(encoding="utf-8")
    assert "preserveNeuralStems" in stream_src and "neuralReferenceOffset" in stream_src, "W51.2 missing HotSwapTrackSeamless stem preservation"
    loader_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.MediaLoader.cs").read_text(encoding="utf-8")
    assert "preserveNeuralStems: sameSong" in loader_src and "ComputeCrossVariantNeuralOffsetSamples" in loader_src, "W51.2 missing cross-variant handoff"

    # W51.3: Play & Karaoke AI Cache Inspector + 4-language parity
    for tok in ("AddSongAiCacheInspectorSection", "ShowKaraokeCacheInspectorDialog", "InspectActiveSongCacheState"):
        assert tok in wf_src, f"W51.3 missing {tok} in MainActivity.NeuralWorkflows.cs"
    pm_core = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Core.cs").read_text(encoding="utf-8")
    assert "musicLyricsCacheBtn" in pm_core and "ShowKaraokeCacheInspectorDialog" in pm_core, "W51.3 missing Karaoke cache button"
    w51_keys = (
        "AiCacheSectionTitle", "AiCacheSharedNote", "AiCacheStatusCachedFmt", "AiCacheStatusNotCached",
        "AiCacheConfigMatches", "AiCacheConfigDiffersHeader", "AiCacheRemoveBtn", "AiCacheReextractCurrentBtn",
        "AiCacheExtractNowBtn", "AiCacheRemovedToast", "SingDockCacheChip", "SingDockCacheDiffChip",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in w51_keys:
            assert f'["{key}"]' in lang_src, f"W51.3 missing L10n key '{key}' in {lang_file}"

    # W51.4: 120 Hz zero-jank transitions
    theme_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Theme" / "OneUiTheme.cs").read_text(encoding="utf-8")
    for tok in ("IsSuspendedForTransition", "ScheduleDeferredBlur", "public static bool EnableFullScreenRenderEffectBlur { get; set; }"):
        assert tok in theme_src, f"W51.4 missing {tok} in OneUiTheme.cs"
    nav_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Navigation.cs").read_text(encoding="utf-8")
    assert ".ScaleX(1f)" in nav_src and "IsSuspendedForTransition" in nav_src, "W51.4 missing GPU tab pill or blur suspension"

    # W51.5: First-3s startup stutter elimination
    loader_v42_src = loader_src + "\n" + (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.SpatialV42.cs").read_text(encoding="utf-8")
    out_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Audio" / "AndroidAudioOutput.cs").read_text(encoding="utf-8")
    assert "PreFillNativePlaybackRing(" in out_src, "W51.5 missing PreFillNativePlaybackRing in AndroidAudioOutput.cs"
    assert "startupDelayMs: 1400" in loader_v42_src, "W51.5 missing staggered LGSR startupDelayMs"

    # W51.6: Next-track loudness pre-scan & sample-0 priming
    tag_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AudioTagReader.cs").read_text(encoding="utf-8")
    assert "TryParseLoudnessTagToLufs" in tag_src and "TrackIntegratedLufs" in tag_src, "W51.6 missing tag loudness parser"
    qa_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AudioQualityAnalyzer.cs").read_text(encoding="utf-8")
    assert "PreScanTrackIntegratedLufsAsync" in qa_src and "track_loudness_lufs_v1.json" in qa_src, "W51.6 missing loudness pre-scanner/cache"
    assert "ScheduleNextTrackLoudnessPreScan" in loader_v42_src and "PrimeTrackLoudnessLufs" in loader_v42_src, "W51.6 missing next-track pre-scan wiring"

    # W51.7: Bidirectional App <-> Android System Media Volume link
    main_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.cs").read_text(encoding="utf-8")
    for tok in ("VolumeControlStream = Android.Media.Stream.Music", "SystemVolumeBroadcastReceiver", "SystemVolumeContentObserver"):
        assert tok in main_src, f"W51.7 missing {tok} in MainActivity.cs"
    modals_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Modals.cs").read_text(encoding="utf-8")
    for tok in ("IsDirectUsbSoftwareVolumeActive", "SyncUiFromSystemMediaVolume", "SetMasterVolumePercent", "StepMasterVolumeByDelta"):
        assert tok in modals_src, f"W51.7 missing {tok} in MainActivity.PlayerMusic.Modals.cs"


def _py_stage10_onnx_low_ram_discipline_and_qa_contracts() -> None:
    ort_main = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.cs").read_text(encoding="utf-8")
    for tok in (
        "IsMemoryHeavyModel",
        "MaxSingleProcessModelPssBytes",
        "MemoryBudgetGovernor.Evaluate",
        "CaptureDeviceMemorySnapshot",
        "fnDisableCpuMemArena",
        "fnDisableMemPattern",
        "session.disable_prepacking",
        "enableArenaShrinkage",
    ):
        assert tok in ort_main, f"ONNX low-RAM discipline missing '{tok}' in NativeOnnxRuntimeBridge.cs"
    # RoFormer must be excluded BEFORE webgpu substring check to avoid Adreno Dawn unified-RAM LMKD kills
    m_webgpu = re.search(r"static bool IsWebGpuOptimalModel\([^\)]*\)\s*\{(.*?)\n    \}", ort_main, re.S)
    assert m_webgpu is not None, "IsWebGpuOptimalModel not found in NativeOnnxRuntimeBridge.cs"
    webgpu_body = m_webgpu.group(1)
    roformer_idx = webgpu_body.find("roformer")
    webgpu_idx = webgpu_body.find('"webgpu"')
    assert roformer_idx != -1 and webgpu_idx != -1 and roformer_idx < webgpu_idx, (
        "IsWebGpuOptimalModel must exclude 'roformer' BEFORE matching 'webgpu'!"
    )

    ort_ep = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.ExecutionProviders.cs").read_text(encoding="utf-8")
    for tok in ("DisableCpuMemArena", "DisableMemPattern", "CreateRunOptions", "AddRunConfigEntry", "ReleaseRunOptions"):
        assert tok in ort_ep, f"Missing ORT C API binding '{tok}' in NativeOnnxRuntimeBridge.ExecutionProviders.cs"

    ort_sess = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.Session.cs").read_text(encoding="utf-8")
    assert "memory.enable_memory_arena_shrinkage" in ort_sess and "runOptionsHandle" in ort_sess, (
        "NativeOnnxRuntimeBridge.Session.cs missing per-Run arena shrinkage RunOptions"
    )

    orch_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralPipelineOrchestrator.cs").read_text(encoding="utf-8")
    for tok in ("bool preferLightweightOnly = false", "WithMemorySafeFallback()", "UsedMemorySafeFallback", "ArePlanModelsIdentical"):
        assert tok in orch_src, f"Missing '{tok}' in NeuralPipelineOrchestrator.cs"

    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    for tok in ("UsedMemorySafeFallback", "NeuralLowRamFallbackToast", "catch (InsufficientMemoryException", "NeuralLowRamNeedLightModelFmt"):
        assert tok in wf_src, f"Missing '{tok}' in MainActivity.NeuralWorkflows.cs"

    bridge_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.LiveBridge.cs").read_text(encoding="utf-8")
    assert "tabContainers" in bridge_src and "activeTab" in bridge_src, (
        "MainActivity.LiveBridge.cs must skip inactive tabContainers during DumpUiTreeJsonAsync"
    )

    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in ("NeuralLowRamFallbackToast", "NeuralLowRamNeedLightModelFmt"):
            assert f'["{key}"]' in lang_src, f"Missing L10n key '{key}' in {lang_file}"


def _py_stage11_w53_1_wave_a_contracts() -> None:
    # B2: Lower-Mid Anchor MAP + frequency-adaptive covariance + sub-50 Hz vocal guard + 2nd-order Wiener leadPurity
    sk_src = (REPO_ROOT / "Cavern" / "Neural" / "SpatialKaraokeEngine.cs").read_text(encoding="utf-8")
    for tok in (
        "neuralSubVocalWeight",
        "anchorPriorWeight",
        "AccumulateAnchorBin",
        "FinalizeChannelAnchors",
        "ComputeAdaptiveSmoothTauSec",
        "ComputeNeuralSubVocalWeight",
    ):
        assert tok in sk_src, f"W53.1 B2 missing '{tok}' in SpatialKaraokeEngine.cs"

    # B4: MVSEP 2-Stage Cascade + live-verified sub-model IDs + vocals-only opt2=0
    mv_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "MvsepCloudClient.cs").read_text(encoding="utf-8")
    for tok in (
        "IsTwoStageCascade",
        "KaraokeVocalsOnlyStage2",
        "SeparateTwoStageCascadeAsync",
        "ApplyZeroPhaseSubVocalHighPass",
        "AlignDownloadedStemsToReference",
        'Opt1 = "171"',
        'Opt2 = "0"',
    ):
        assert tok in mv_src, f"W53.1 B2/B4 missing '{tok}' in MvsepCloudClient.cs"

    # B3: CrossVariantVocalAligner + ReferenceDriftPpm + MainActivity wiring
    align_path = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "CrossVariantVocalAligner.cs"
    assert align_path.is_file(), "W53.1 B3 CrossVariantVocalAligner.cs missing"
    align_src = align_path.read_text(encoding="utf-8")
    for tok in ("CrossVariantAlignmentResult", "ShouldPreferSeparateExtraction", "SubSampleSyncEngine.Align"):
        assert tok in align_src, f"W53.1 B3 missing '{tok}' in CrossVariantVocalAligner.cs"

    proc_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "Adapters" / "NeuralStemStageProcessor.cs").read_text(encoding="utf-8")
    assert "public double ReferenceDriftPpm" in proc_src and "referenceDriftPpm" in proc_src, "W53.1 B3 missing ReferenceDriftPpm in NeuralStemStageProcessor.cs"

    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    for tok in ("SeparateTwoStageCascadeAsync", "CrossVariantVocalAligner.Align", ":s1", ":s2", "ReferenceDriftPpm"):
        assert tok in wf_src, f"W53.1 B3/B4 missing '{tok}' in MainActivity.NeuralWorkflows.cs"


def _py_stage12_w53_2_w53_3_wave_b_wave_c_contracts() -> None:
    # Wave C (B6): Stereo-Pan Azimuth Estimator + Fixed 3D Descriptors + Mood Choreography
    cortex_src = (REPO_ROOT / "Cavern" / "Spatial" / "SpatialObjectCortex.cs").read_text(encoding="utf-8")
    for tok in ("struct StereoPanEstimate", "class StereoPanAzimuthEstimator", "EstimateAllHomeAzimuthsDeg", "stemHomeAzimuthsDeg"):
        assert tok in cortex_src, f"W53.3 Wave C missing '{tok}' in SpatialObjectCortex.cs"

    spat_src = (REPO_ROOT / "Cavern" / "Neural" / "NeuralStemSpatializer.cs").read_text(encoding="utf-8")
    for tok in ("IReadOnlyList<StereoStemBuffer> stems", "GetPanPreservedAnchorPosition", "EstimateAllHomeAzimuthsDeg"):
        assert tok in spat_src, f"W53.3 Wave C missing '{tok}' in NeuralStemSpatializer.cs"

    proc_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "Adapters" / "NeuralStemStageProcessor.cs").read_text(encoding="utf-8")
    assert "NeuralStemSpatializer.BuildSceneDescriptors(stemTrack.Stems," in proc_src, (
        "W53.3 Wave C: NeuralStemStageProcessor must pass StereoStemBuffer list to BuildSceneDescriptors"
    )

    # Wave B (B7): Unified AI Sheet, Multi-Variant Cache, Lossless Compaction, Incremental Stage-1 Reuse
    codec_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralTrackCacheCodec.cs").read_text(encoding="utf-8")
    for tok in ("CanDeriveMonoRefFromStems", "SynthesizeMonoRefFromStems", "CompactFileIfBeneficial", "CompressionLevel.Optimal"):
        assert tok in codec_src, f"W53.2 Wave B missing '{tok}' in NeuralTrackCacheCodec.cs"

    orch_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralPipelineOrchestrator.cs").read_text(encoding="utf-8")
    assert "NeuralSeparatedTrack preExtractedTrack = null" in orch_src and "seededFromReusableVocals" in orch_src, (
        "W53.2 Wave B: NeuralPipelineOrchestrator missing preExtractedTrack Stage-1 skip"
    )

    cache_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NeuralTrackCacheService.cs").read_text(encoding="utf-8")
    for tok in ("TryFindBestReusableVocalTrack", "OptimizeCacheStorage"):
        assert tok in cache_src, f"W53.2 Wave B missing '{tok}' in NeuralTrackCacheService.cs"

    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    for tok in (
        "ShowUnifiedAiExtractionSheet",
        "ActivateCachedVariantAsync",
        "TryFindBestReusableVocalTrack",
        "OptimizeCacheStorage",
        "preExtractedTrack: reusableStage1",
    ):
        assert tok in wf_src, f"W53.2 Wave B missing '{tok}' in MainActivity.NeuralWorkflows.cs"

    # 4-Language parity check for all new Wave B UI keys
    w53_2_keys = (
        "AiCacheVariantsHeaderFmt", "AiCacheVariantRowFmt", "AiCacheVariantActiveBadge",
        "AiCacheVariantUseBtn", "AiCacheVariantDeleteBtn", "AiCacheVariantActivatedToast",
        "AiCacheVariantDeletedToast", "AiCacheExtractNewVariantBtn", "AiCacheRemoveAllVariantsBtn",
        "AiCacheOptimizeStorageBtn", "AiCacheOptimizeDoneToastFmt", "AiCacheOptimizeAlreadyCompactToast",
        "AiCacheIncrementalReuseHint", "KaraokePreserveBackingTitle",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in w53_2_keys:
            assert f'["{key}"]' in lang_src, f"W53.2 Wave B missing L10n key '{key}' in {lang_file}"


def _py_stage13_w53_4_to_w53_6_wave_d_e_f_contracts() -> None:
    # Wave D (B10): Modular One UI 9 quick-panel control blocks
    mod_model = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Models" / "ModularQuickPanelLayout.cs").read_text(encoding="utf-8")
    for tok in ("class ModularQuickPanelLayout", "CreateDefaultPlayLayout", "CreateDefaultRoomLayout", "FromJsonOrDefault"):
        assert tok in mod_model, f"W53.4 Wave D missing '{tok}' in ModularQuickPanelLayout.cs"

    mod_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.ModularBlocks.cs").read_text(encoding="utf-8")
    for tok in ("ShowModularLayoutEditorDialog", "ReorderLinearLayoutInPlace", "OneUiTheme.OnPrimary", "BaselineAligned = false"):
        assert tok in mod_ui, f"W53.4 Wave D missing '{tok}' in MainActivity.ModularBlocks.cs"

    # Wave E (B12): W8A16 ONNX quantizer + SoC-keyed QNN context cache + Settings wiring
    quant_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "OnnxMobileQuantizer.cs").read_text(encoding="utf-8")
    for tok in ("enum NeuralQuantizationMode", "ConvertBytesToW8A16", "ResolveEffectiveMode", "EstimatePeakPssMb", "DequantizeLinear"):
        assert tok in quant_src, f"W53.5 Wave E missing '{tok}' in OnnxMobileQuantizer.cs"

    qnn_mgr = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "OnnxQnnContextCacheManager.cs").read_text(encoding="utf-8")
    for tok in ("ComputeSocContextKey", "Build.Fingerprint", "ProbeDeviceQnnCapabilities", "ResolveOrBuildQuantizedModelPath"):
        assert tok in qnn_mgr, f"W53.5 Wave E missing '{tok}' in OnnxQnnContextCacheManager.cs"

    bridge_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.cs").read_text(encoding="utf-8")
    for tok in ("PreferredQuantizationMode", "QnnContextCacheEnabled", "OnnxQnnContextCacheManager.ResolveOrBuildQuantizedModelPath"):
        assert tok in bridge_src, f"W53.5 Wave E missing '{tok}' in NativeOnnxRuntimeBridge.cs"

    # Wave F (B13): Physical Transaural XTC + bit-exact headphone bypass + engine/route/settings wiring
    xtc_core = (REPO_ROOT / "Cavern" / "Virtualizer" / "TransauralXtcFilter.cs").read_text(encoding="utf-8")
    for tok in ("enum TransauralSpeakerPreset", "class TransauralXtcFilter", "SimulatePhysicalSpeakerToEar", "if (buffer == null || !enabled || bypassed"):
        assert tok in xtc_core, f"W53.6 Wave F missing '{tok}' in TransauralXtcFilter.cs"

    xtc_stage = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "Adapters" / "TransauralXtcPostProcessingStage.cs").read_text(encoding="utf-8")
    for tok in ("Priority => 820", "UpdateOutputRoute", "forceBitExactBypass"):
        assert tok in xtc_stage, f"W53.6 Wave F missing '{tok}' in TransauralXtcPostProcessingStage.cs"

    engine_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.cs").read_text(encoding="utf-8")
    for tok in ("TransauralXtcPostProcessingStage", "ApplyTransauralXtcConfig", "UpdateOutputRouteForTransaural"):
        assert tok in engine_src, f"W53.6 Wave F missing '{tok}' in RealtimePlayerEngine.cs"

    dev_mgr = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AudioDeviceProfileManager.cs").read_text(encoding="utf-8")
    assert "UpdateOutputRouteForTransaural" in dev_mgr and "IsSpeakerHardware" in dev_mgr, (
        "W53.6 Wave F missing automatic speaker/headphone route wiring in AudioDeviceProfileManager.cs"
    )

    # Full 4-Language parity check across all 52 new Wave D + Wave E + Wave F keys
    wave_def_keys = (
        "BtnDone", "ModLayoutCardTitle", "ModLayoutCardDesc", "ModLayoutEditPlayBtn", "ModLayoutEditRoomBtn",
        "ModLayoutPlayTitle", "ModLayoutRoomTitle", "ModLayoutSubtitle", "ModLayoutResetBtn", "ModLayoutResetToast",
        "ModBlockPinnedTag", "ModBlockEssentialTag",
        "ModBlockPlayStage", "ModBlockPlayTitle", "ModBlockPlaySeek", "ModBlockPlayBadges",
        "ModBlockPlayTransport", "ModBlockPlayVolume", "ModBlockPlaySpatialPills", "ModBlockPlayBottomActions",
        "ModBlockRoomSignalChain", "ModBlockRoomCanvas", "ModBlockRoomMiniTransport", "ModBlockRoomXRay",
        "ModBlockRoomQuickPanel", "ModBlockRoomMixer",
        "NeuralQuantModeTitle", "NeuralQuantAuto", "NeuralQuantFp32", "NeuralQuantFp16", "NeuralQuantW8A16", "TipNeuralQuantMode",
        "NeuralQnnCacheTitle", "TipNeuralQnnCache", "NeuralQnnCacheStatusFmt", "NeuralQnnCacheClearBtn", "NeuralQnnCacheClearedToast",
        "TransauralCardTitle", "TransauralEnableTitle", "TipTransauralXtc", "TransauralPresetTitle", "TipTransauralPreset",
        "TransauralPresetPhoneLand", "TransauralPresetPhonePort", "TransauralPresetDesktop", "TransauralPresetHiFi",
        "TransauralStrengthTitle", "TransauralAutoBypassTitle", "TipTransauralAutoBypass",
        "TransauralStatusOff", "TransauralStatusHeadphoneBypass", "TransauralStatusActive",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in wave_def_keys:
            assert f'["{key}"]' in lang_src, f"W53.4-W53.6 missing L10n key '{key}' in {lang_file}"


def _py_stage14_v4_1_reform_p0_contracts() -> None:
    # P0-1 & P0-3 & P0-4: Stem dedup, Atmos 3D gain staging, symmetric BackingVocals L/R
    spat_src = (REPO_ROOT / "Cavern" / "Neural" / "NeuralStemSpatializer.cs").read_text(encoding="utf-8")
    for tok in ("FilterRenderableStems", "FilterRenderableStemNames", "GetRecommendedStemSourceGain", "minHalfSpread"):
        assert tok in spat_src, f"V4.1 missing '{tok}' in NeuralStemSpatializer.cs"

    mv_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "MvsepCloudClient.cs").read_text(encoding="utf-8")
    assert "hasSplitLead" in mv_src, "V4.1 MvsepCloudClient must strip duplicate 'vocals' when 'lead_vocal' is present"

    # P0-2: Direct discrete-stem Karaoke attenuation + bypass of post-3D STFT SpatialKaraokeEngine
    sp_pipe = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.SpatialPipeline.cs").read_text(encoding="utf-8")
    for tok in ("FilterRenderableStems", "GetRecommendedStemSourceGain", "hasDiscreteLiveVocalSource", "liveNeuralSourceRoles", "KaraokePreserveBackingVocals"):
        assert tok in sp_pipe, f"V4.1 missing '{tok}' in RealtimePlayerEngine.SpatialPipeline.cs"
    sp_stream = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.Streaming.cs").read_text(encoding="utf-8")
    assert "directStemKaraokeActive" in sp_stream, "V4.1 RealtimePlayerEngine.Streaming.cs must bypass STFT karaoke when discrete live AI vocal stems are active"

    # P0-5: Silent AI objects fade out cleanly in 3D Room
    room_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Views" / "RoomSimulatorView.cs").read_text(encoding="utf-8")
    m_always = re.search(r"bool alwaysVisibleLayer =([^;]+);", room_src, re.S)
    assert m_always is not None, "V4.1 alwaysVisibleLayer not found in RoomSimulatorView.cs"
    assert "SourceLayer.NeuralStem" not in m_always.group(1), "V4.1 silent NeuralStem objects must fade out (remove from alwaysVisibleLayer)"

    # P0-6: 2-step destructive confirmation helper wired across all destructive actions + 4-lang parity
    ui_helpers = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.UiHelpers.cs").read_text(encoding="utf-8")
    assert "void ConfirmDestructiveAction(" in ui_helpers, "V4.1 ConfirmDestructiveAction helper missing in MainActivity.UiHelpers.cs"
    neural_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Neural.cs").read_text(encoding="utf-8")
    assert neural_ui.count("ConfirmDestructiveAction(") >= 7, "V4.1 MainActivity.Settings.Neural.cs must guard all 7 destructive actions with ConfirmDestructiveAction"
    wf_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.NeuralWorkflows.cs").read_text(encoding="utf-8")
    assert wf_src.count("ConfirmDestructiveAction(") >= 2, "V4.1 MainActivity.NeuralWorkflows.cs must guard variant/track cache deletions with ConfirmDestructiveAction"
    core_ui = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Core.cs").read_text(encoding="utf-8")
    assert "ConfirmDestructiveAction(" in core_ui, "V4.1 MainActivity.Settings.Core.cs must guard queue removal with ConfirmDestructiveAction"

    # P0-7: Quantization-aware RAM gate (keeping <=2600 MB cap) + Manual/Cloud isolation
    ort_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NativeOnnxRuntimeBridge.cs").read_text(encoding="utf-8")
    assert "EstimateQuantizedPayloadBytes(" in ort_src, "V4.1 NativeOnnxRuntimeBridge.cs must evaluate post-quantization payload size"
    orch_src = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Spatial" / "NeuralPipelineOrchestrator.cs").read_text(encoding="utf-8")
    assert "!isManual" in orch_src, "V4.1 NeuralPipelineOrchestrator must not override Manual mode with StudioBundlePreset"

    # P0-8: Persistent FilesDir/neural_tracks storage + tab-cancel visibility fix
    cache_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "NeuralTrackCacheService.cs").read_text(encoding="utf-8")
    assert "MigrateLegacyCacheDirIfNeeded" in cache_src and "context.FilesDir" in cache_src, (
        "V4.1 NeuralTrackCacheService must persist under FilesDir/neural_tracks with legacy CacheDir migration"
    )
    nav_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Navigation.cs").read_text(encoding="utf-8")
    assert "c.TranslationX = 0f" in nav_src and "c.Visibility = i == activeTab ? ViewStates.Visible : ViewStates.Gone" in nav_src, (
        "V4.1 MainActivity.Navigation.cs must reset TranslationX and Visibility on AnimationCancel"
    )

    v4_1_keys = (
        "ConfirmDeleteTitle", "ConfirmDeleteActionBtn", "ConfirmClearAllTracksMsg",
        "ConfirmClearModelsAndCacheMsg", "ConfirmDeleteModelMsgFmt", "ConfirmDeleteVariantMsgFmt",
        "ConfirmRemoveAllVariantsMsg", "ConfirmClearMvsepTokenMsg", "ConfirmClearQnnCacheMsg",
        "ConfirmRemoveQueueJobMsg",
    )
    for lang_file in ("AndroidStrings.cs", "AndroidStrings.PtBr.cs", "AndroidStrings.Hu.cs", "AndroidStrings.Zh.cs"):
        lang_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Language" / lang_file).read_text(encoding="utf-8")
        for key in v4_1_keys:
            assert f'["{key}"]' in lang_src, f"V4.1 missing L10n key '{key}' in {lang_file}"


def _py_stage15_w56_4_co_evolve_zero_static_contracts() -> None:
    """W56.4: Deterministic co_evolve.py parity across BridgeCmd, Packaging, SettingsSchema & RPE<->SBE DSP."""
    co_script = REPO_ROOT / ".agents" / "scripts" / "co_evolve.py"
    assert co_script.is_file(), "W56.4 .agents/scripts/co_evolve.py missing"

    # Run twice and verify deterministic hashes & <=250 ms latency budget
    p1 = subprocess.run([sys.executable, "-B", str(co_script), "--check", "--json"], cwd=str(REPO_ROOT), capture_output=True, text=True)
    assert p1.returncode == 0, f"W56.4 co_evolve.py --check failed: {p1.stdout}\n{p1.stderr}"
    d1 = json.loads(p1.stdout)
    p2 = subprocess.run([sys.executable, "-B", str(co_script), "--check", "--json"], cwd=str(REPO_ROOT), capture_output=True, text=True)
    assert p2.returncode == 0, f"W56.4 co_evolve.py second run failed: {p2.stderr}"
    d2 = json.loads(p2.stdout)

    assert d1["ok"] is True and d2["ok"] is True, f"W56.4 co_evolve returned errors: {d1.get('errors')}"
    assert d1["elapsedMs"] <= 250.0, f"W56.4 co_evolve exceeded 250 ms budget: {d1['elapsedMs']} ms"
    assert d1["domains"] == d2["domains"], "W56.4 co_evolve domain hashes/metadata must be bit-identical across runs"

    # BridgeCmd unification: 1:1 opcodes == catalog == switchCases, zero hardcoded tab `4`
    bc = d1["domains"]["bridge_cmd"]
    assert bc["opcodes"] == bc["catalog"] == bc["switchCases"] >= 54, f"W56.4 BridgeCmd mismatch: {bc}"
    assert len(bc["schemaHash"]) == 64, "W56.4 BridgeCmd schemaHash must be 64-char SHA-256 hex"

    # SettingsSchema: zero Activator.CreateInstance, true non-zero defaults preserved in generated AppSettingsStore.Schema.cs
    cmd_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "LiveDiagnosticBridgeService.Commands.cs").read_text(encoding="utf-8")
    assert "Activator.CreateInstance" not in cmd_src, "W56.4 Activator.CreateInstance must be removed from settings_reset"
    schema_cs = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "AppSettingsStore.Schema.cs").read_text(encoding="utf-8")
    for expected_reset in (
        'static s => s.PlayerVolume = 1.0f',
        'static s => s.HeadphoneVirtualizer = true',
        'static s => s.CloudLogRepo = "guumachadooo/Cavern"',
    ):
        assert expected_reset in schema_cs, f"W56.4 AppSettingsStore.Schema.cs missing true default reset: {expected_reset}"


def _py_stage16_w55_2_collapse_when_disabled_and_w57_1_contracts() -> None:
    """W55.2 + W57.1: Collapse-when-disabled choreo UI gate & JOC inline zero-ThreadPool invariants."""
    spatial_v42 = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.SpatialV42.cs").read_text(encoding="utf-8")
    assert "bool Has3DSpatializationForChoreo()" in spatial_v42, "W55.2 Has3DSpatializationForChoreo() missing in MainActivity.SpatialV42.cs"
    assert "ViewStates nextVis = has3D ? ViewStates.Visible : ViewStates.Gone;" in spatial_v42, (
        "W55.2 RefreshSpatialChoreoClarifierV42 must collapse choreography container to Gone when 3D spatialization is disabled"
    )
    assert "playerEngine.SceneHub.SetChoreoTrack(null);" in spatial_v42, (
        "W55.2 RefreshSpatialChoreoClarifierV42 must clear active choreo track when 3D spatialization is disabled"
    )

    joc_src = (REPO_ROOT / "Cavern.Format" / "Decoders" / "EnhancedAC3" / "JointObjectCodingApplier.cs").read_text(encoding="utf-8")
    assert "ThreadPool.QueueUserWorkItem" not in joc_src and "ManualResetEventSlim" not in joc_src, (
        "W57.1 JointObjectCodingApplier.cs must run inline without ThreadPool wakeups or ManualResetEventSlim"
    )
    assert "HilbertAllpassBranch" in joc_src, "W57.1 JointObjectCodingApplier.cs must implement HilbertAllpassBranch"


def _py_stage17_w55_3_and_w57_2_contracts() -> None:
    """W55.3 + W57.2: Hard control deduplication, UiStateCoordinator wiring, speaker/XTC BRIR+ISM bypass, ListenerContext & Async FFmpeg."""
    studio_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.StudioExport.cs").read_text(encoding="utf-8")
    assert "studioWorkflowSelector" not in studio_src, "W55.3 studioWorkflowSelector must be removed from MainActivity.StudioExport.cs"
    assert "studioBundleSelector" not in studio_src, "W55.3 studioBundleSelector must be removed from MainActivity.StudioExport.cs"
    assert "studioAiModelPickerBtn" in studio_src, "W55.3 studioAiModelPickerBtn must exist in MainActivity.StudioExport.cs"

    player_tab = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerTab.cs").read_text(encoding="utf-8")
    assert "roomInvertOrbitSwitch" not in player_tab, "W55.3 roomInvertOrbitSwitch must be removed from MainActivity.PlayerTab.cs"

    settings_audio = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Settings.Audio.cs").read_text(encoding="utf-8")
    assert "settingsRoomInvertOrbitSwitch" in settings_audio, "W55.3 settingsRoomInvertOrbitSwitch must be the canonical control in MainActivity.Settings.Audio.cs"

    coord_cs = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "UiStateCoordinator.cs").read_text(encoding="utf-8")
    for tok in ("IsUpdatingFromCoordinator", "RunGuarded", "NotifyChanged", "FlushPending"):
        assert tok in coord_cs, f"W55.3 UiStateCoordinator missing {tok}"

    main_coord = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Coordinator.cs").read_text(encoding="utf-8")
    for tok in ("UiStateCoordinator", "ShouldAutoBypassRoomAmbienceForSpeakers", "ApplyArbitratedExternalizationAndBrirBypass"):
        assert tok in main_coord, f"W55.3 MainActivity.Coordinator.cs missing {tok}"

    spatial_pipe = (REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Rendering" / "RealtimePlayerEngine.SpatialPipeline.cs").read_text(encoding="utf-8")
    assert "!transauralXtcPostStage.IsHeadphoneRoute || transauralXtcPostStage.IsActivelyProcessing" in spatial_pipe, (
        "W55.3 RealtimePlayerEngine.SpatialPipeline.cs must bypass BRIR+ISM on speaker/XTC routes"
    )

    listener_cs = (REPO_ROOT / "Cavern" / "Listener.cs").read_text(encoding="utf-8")
    assert "IsDesktopConfigSupported" in listener_cs and "ListenerContext" in listener_cs, "W57.2 ListenerContext/IsDesktopConfigSupported missing"

    eac3_cs = (REPO_ROOT / "Cavern.Format" / "Decoders" / "EnhancedAC3Decoder.cs").read_text(encoding="utf-8")
    assert "SeekFrameEntry" in eac3_cs and "IndexedFrameCount" in eac3_cs, "W57.2 VBR EnhancedAC3Decoder seek index missing"

    ffmpeg_cs = (REPO_ROOT / "CavernSamples" / "VoidX.WPF" / "FFmpeg" / "FFmpeg.cs").read_text(encoding="utf-8")
    for tok in ("FindExecutable", "LaunchAsync", "TerminateProcessTree"):
        assert tok in ffmpeg_cs, f"W57.2 FFmpeg.cs missing {tok}"


PYTHON_CHECKS = [
    ("PY_StudioExportLifecycle_StreamingExportSink_FlacVsLgsr", _py_studio_export_lifecycle_check),
    ("PY_SpatialChoreo_SpatialGovernor_CntcAndMoodDigest", _py_spatial_choreo_cntc_and_mood_check),
    ("PY_T2_Stage2_RuntimeStabilityAndLibraryContracts", _py_stage2_runtime_stability_contracts),
    ("PY_T3_Stage3_AudioAi_Mvsep_CntcV3_Contracts", _py_stage3_audio_ai_mvsep_cntc_contracts),
    ("PY_Choreography_NoUiAudioTugOfWar", _py_choreography_no_ui_audio_tug_of_war),
    ("PY_T4_Stage4_UxClarity_Motion_OrbitDrag_Lyrics_Contracts", _py_stage4_ux_motion_orbit_lyrics_contracts),
    ("PY_T5_StudioExportHygieneAndLocateWiring", _py_stage5_studio_export_hygiene_and_locate_wiring),
    ("PY_T6_FullAppSotaAudit_And_PhaseAContracts", _py_stage6_full_app_sota_audit_and_phase_a_contracts),
    ("PY_T7_BpUiAiOrgMotionStability_Q1_Q6_Contracts", _py_stage7_ui_ai_org_motion_stability_contracts),
    ("PY_T8_ScarlettProAudioStabilityContracts", _py_stage8_scarlett_proaudio_stability_contracts),
    ("PY_T9_W51_VocalCacheUxLoudnessVolumeContracts", _py_stage9_w51_vocal_cache_ux_loudness_volume_contracts),
    ("PY_T10_OnnxLowRamDisciplineAndQaContracts", _py_stage10_onnx_low_ram_discipline_and_qa_contracts),
    ("PY_T11_W53_1_WaveAContracts", _py_stage11_w53_1_wave_a_contracts),
    ("PY_T12_W53_2_W53_3_WaveB_WaveC_Contracts", _py_stage12_w53_2_w53_3_wave_b_wave_c_contracts),
    ("PY_T13_W53_4_W53_5_W53_6_WaveD_WaveE_WaveF_Contracts", _py_stage13_w53_4_to_w53_6_wave_d_e_f_contracts),
    ("PY_T14_W55_1_V4Reform_P0_Contracts", _py_stage14_v4_1_reform_p0_contracts),
    ("PY_T15_W56_4_CoEvolve_BridgeCmd_Packaging_SettingsSchema_DspParity", _py_stage15_w56_4_co_evolve_zero_static_contracts),
    ("PY_T16_W55_2_CollapseWhenDisabled_And_W57_1_JocInlineContracts", _py_stage16_w55_2_collapse_when_disabled_and_w57_1_contracts),
    ("PY_T17_W55_3_And_W57_2_Dedup_Coordinator_ListenerCtx_VbrEac3_Ffmpeg", _py_stage17_w55_3_and_w57_2_contracts),
]


def _compute_fingerprint(script_path: Path, dotnet: str, tfm: str) -> str:
    """Compute deterministic SHA-256 fingerprint over harness, toolchain, and C# sources."""
    h = hashlib.sha256()
    h.update(script_path.read_bytes())
    h.update(b"\0")
    h.update(dotnet.encode("utf-8"))
    h.update(b"\0")
    h.update(tfm.encode("utf-8"))
    h.update(b"\0")
    entries: list[tuple[str, int, int]] = []
    for base_dir in FINGERPRINT_SRC_DIRS:
        if not base_dir.is_dir():
            continue
        for dirpath, dirnames, filenames in os.walk(base_dir):
            dirnames[:] = sorted(d for d in dirnames if d not in ("bin", "obj", ".git"))
            for fname in filenames:
                if not (fname.endswith(".cs") or fname.endswith(".csproj")):
                    continue
                fpath = Path(dirpath) / fname
                st = fpath.stat()
                entries.append((fpath.relative_to(REPO_ROOT).as_posix(), st.st_size, st.st_mtime_ns))
    ui_coord_cs = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "UiStateCoordinator.cs"
    if ui_coord_cs.is_file():
        st = ui_coord_cs.stat()
        entries.append((ui_coord_cs.relative_to(REPO_ROOT).as_posix(), st.st_size, st.st_mtime_ns))
    entries.sort()
    for rel, size, mtime_ns in entries:
        h.update(f"{rel}\0{size}\0{mtime_ns}\n".encode("utf-8"))
    return h.hexdigest()


def _write_if_changed(path: Path, text: str) -> bool:
    if path.is_file() and path.read_text(encoding="utf-8") == text:
        return False
    path.write_text(text, encoding="utf-8")
    return True


def _find_cached_run_cmd(cache_dir: Path, tfm: str, asm_name: str, dotnet: str) -> list[str] | None:
    out_dir = cache_dir / "bin" / "Release" / tfm
    exe = out_dir / asm_name
    dll = out_dir / f"{asm_name}.dll"
    if exe.is_file() and os.access(exe, os.X_OK):
        return [str(exe)]
    if dll.is_file():
        return [dotnet, str(dll)]
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Wave W46+ Real .NET & Static Unit Test Harness")
    parser.add_argument("--filter", default="", help="Run only tests whose name contains this substring (case-insensitive)")
    parser.add_argument("--no-cache", action="store_true", help="Force full restore/rebuild and bypass incremental build cache")
    args = parser.parse_args()
    filt = args.filter.strip()

    py_matched = 0
    for name, fn in PYTHON_CHECKS:
        if filt and filt.lower() not in name.lower():
            continue
        py_matched += 1
        print(f"[TEST] {name} ... ", end="", flush=True)
        fn()
        print("PASS", flush=True)

    cs_names = re.findall(r'RunTest\("([^"]+)"', TEST_PROGRAM_CS)
    cs_should_run = (not filt) or any(filt.lower() in n.lower() for n in cs_names)
    if not cs_should_run:
        if py_matched == 0:
            print(f"[w46-tests] ERROR: --filter '{filt}' matched 0 tests.", file=sys.stderr)
            return 1
        print(f"\nALL {py_matched} MATCHED PYTHON INVARIANT TESTS PASSED (0 failures).")
        return 0

    dotnet = find_dotnet()
    tfm = detect_tfm(dotnet)
    cavern_csproj = REPO_ROOT / "Cavern" / "Cavern.csproj"
    logic_csproj = REPO_ROOT / "CavernSamples" / "Reusable" / "Cavernize.Logic" / "Cavernize.Logic.csproj"
    ui_coordinator_cs = REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Services" / "UiStateCoordinator.cs"

    env = os.environ.copy()
    env["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1"
    env["DOTNET_EnableDiagnostics"] = "0"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    dotnet_dir = str(Path(dotnet).parent)
    env["DOTNET_ROOT"] = dotnet_dir
    env["PATH"] = f"{dotnet_dir}:{env.get('PATH', '')}"
    env["W46_TEST_FILTER"] = filt
    if dotnet == "/root/.dotnet/dotnet" and platform.machine().lower() in ("aarch64", "arm64"):
        env.setdefault("DOTNET_GCHeapHardLimit", "20000000")

    LOCK_PATH.parent.mkdir(parents=True, exist_ok=True)
    with LOCK_PATH.open("a+", encoding="utf-8") as lock_file:
        try:
            fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print("[lock] waiting for active PRoot dotnet build...", file=sys.stderr, flush=True)
            fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX)
        try:
            CACHE_DIR.mkdir(parents=True, exist_ok=True)
            asm_name = "W46Tests"
            fp_path = CACHE_DIR / ".fingerprint"
            fingerprint = _compute_fingerprint(Path(__file__), dotnet, tfm)
            cached_fp = fp_path.read_text(encoding="utf-8").strip() if fp_path.is_file() else ""
            run_cmd = _find_cached_run_cmd(CACHE_DIR, tfm, asm_name, dotnet)

            if not args.no_cache and cached_fp == fingerprint and run_cmd is not None:
                print(f"[w46-tests] Cache hit ({tfm}); skipping dotnet build.", flush=True)
            else:
                csproj_path = CACHE_DIR / f"{asm_name}.csproj"
                program_path = CACHE_DIR / "Program.cs"
                assets_path = CACHE_DIR / "obj" / "project.assets.json"
                csproj_text = TEST_CSPROJ.format(
                    tfm=tfm,
                    cavern_csproj=cavern_csproj,
                    logic_csproj=logic_csproj,
                    ui_coordinator_cs=ui_coordinator_cs,
                )
                csproj_changed = _write_if_changed(csproj_path, csproj_text)
                _write_if_changed(program_path, TEST_PROGRAM_CS)

                base_build_cmd = [
                    dotnet,
                    "build",
                    str(csproj_path),
                    "-c",
                    "Release",
                    "-m:1",
                    "/p:UseSharedCompilation=false",
                    "--nologo",
                    "-v:minimal",
                ]
                use_no_restore = (not args.no_cache) and (not csproj_changed) and assets_path.is_file()
                build_cmd = base_build_cmd + (["--no-restore"] if use_no_restore else [])
                print(f"[w46-tests] Building: {' '.join(build_cmd)}", flush=True)
                build_proc = subprocess.run(build_cmd, cwd=str(REPO_ROOT), env=env)
                if build_proc.returncode != 0 and use_no_restore:
                    print("[w46-tests] --no-restore build failed; retrying without --no-restore...", flush=True)
                    build_proc = subprocess.run(base_build_cmd, cwd=str(REPO_ROOT), env=env)
                if build_proc.returncode != 0:
                    fp_path.unlink(missing_ok=True)
                    return build_proc.returncode
                fp_path.write_text(fingerprint, encoding="utf-8")
                run_cmd = _find_cached_run_cmd(CACHE_DIR, tfm, asm_name, dotnet)
                if run_cmd is None:
                    print(
                        f"[w46-tests] ERROR: Built executable not found under {CACHE_DIR / 'bin' / 'Release' / tfm}",
                        file=sys.stderr,
                    )
                    return 1

            print(f"[w46-tests] Running: {' '.join(run_cmd)}", flush=True)
            proc = subprocess.run(run_cmd, cwd=str(REPO_ROOT), env=env)
            return proc.returncode
        finally:
            fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)


if __name__ == "__main__":
    sys.exit(main())
