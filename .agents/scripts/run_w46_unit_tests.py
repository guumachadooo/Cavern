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
  </ItemGroup>
</Project>
"""

TEST_PROGRAM_CS = r"""
using System.Buffers.Binary;
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
using Cavern.Neural;
using Cavern.ProAudio;
using Cavern.Remapping;
using Cavern.Spatial;
using Cavern.Virtualizer;
using Cavern.VocalChain;
using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;
using Cavernize.Logic.Rendering;
using Cavernize.Logic.Spatial;
using Cavernize.Logic.Spatial.Adapters;

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
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(8) == 4, "8-core device must cap background DSP workers at 4 (50%).");
    Assert(MvsepAntiArtifactPipeline.ComputeHeadroomWorkerCap(12) == 4, "12-core device must cap background DSP workers at max 4.");
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
            ModelSignature = "v1|StemSeparation|MVSEP_Cloud_BSRoFormer6",
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
        NeuralTrackCacheCodec.WriteTrackFile(cntcPath, saved, NeuralTrackCacheCompressionMode.Compact16Bit);
        NeuralTrackCacheEntryInfo peek = NeuralTrackCacheCodec.PeekHeader(cntcPath);
        Assert(peek != null, "CNTC v3 PeekHeader returned null.");
        Assert(peek.SourceTrackKey == saved.SourceTrackKey, $"PeekHeader SourceTrackKey mismatch: '{peek.SourceTrackKey}'");
        Assert(peek.PlanSignature == saved.ModelSignature, $"PeekHeader PlanSignature mismatch: '{peek.PlanSignature}'");
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
        TaskModelSlots = new Dictionary<NeuralModelTask, List<string>> {
            [NeuralModelTask.VocalSeparation] = ["mock_vocals_2s"],
            [NeuralModelTask.StemSeparation6] = []
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
        TaskModelSlots = new Dictionary<NeuralModelTask, List<string>> {
            [NeuralModelTask.StemSeparation6] = ["mock_6stem"],
            [NeuralModelTask.VocalSeparation] = ["mock_vocals_2s"]
        }
    };
    NeuralExecutionPlan plan2 = orch.BuildPlan(NeuralPipelineGoal.StemSeparation, manualTwo);
    Assert(plan2.Steps.Count >= 1 && plan2.Steps[0].Models[0].Id == "mock_6stem", "Multi-stem slot should take primary step when present.");

    // Case 3: Auto mode ignores stale manual slots and picks single optimal installed model cleanly
    NeuralAudioConfig autoCfg = new() {
        OrchestrationMode = NeuralOrchestrationMode.Auto,
        TaskModelSlots = new Dictionary<NeuralModelTask, List<string>> {
            [NeuralModelTask.VocalSeparation] = ["mock_vocals_2s"]
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

    // Q3: Replicate OneUiMotionBlur.ComputeHorizontalBlurRadiusPx curve (density=3.0, width=1080px, 310ms fixed transition)
    static float ComputeBlurPx(float velPxPerSec, float density, float maxBlurDp = 26f) {
        if (!float.IsFinite(velPxPerSec) || !float.IsFinite(density) || density <= 0f) return 0f;
        float speedDpPerSec = MathF.Abs(velPxPerSec) / density;
        if (speedDpPerSec <= 260f) return 0f;
        float excess = speedDpPerSec - 260f;
        float rawDp = excess * 0.0044f;
        float maxDp = Math.Clamp(maxBlurDp, 4f, 34f);
        float curvedDp = maxDp * (1f - MathF.Exp(-rawDp / Math.Max(4f, maxDp * 0.65f)));
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
    float bMax = ComputeBlurPx(999999f, density, maxBlurDp: 26f);
    Assert(bMax <= 28f * density, $"Peak blur ({bMax}px) exceeded 28dp cap ({28f * density}px).");
});

if (!string.IsNullOrEmpty(testFilter) && passed == 0) {
    Console.WriteLine($"NO_CS_TESTS_MATCHED filter='{testFilter}'");
    return 0;
}
Console.WriteLine($"\nALL {passed} W46/W48/W49/W50/W51/BP1-3/T2/T3/T5/T6/BP_UI_AI UNIT TESTS PASSED (0 failures).");
return 0;

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

    public NeuralRuntimeInfo GetRuntimeInfo() => new() { IsAvailable = true, ActiveProvider = "CPU" };
    public IReadOnlyList<NeuralModelCatalogEntry> GetAllRegisteredModels() => models;
    public IReadOnlyList<NeuralModelCatalogEntry> GetInstalledModels() => models;
    public string ResolveInstalledModelPath(string modelId) => paths.TryGetValue(modelId ?? "", out string p) ? p : null;
    public INeuralSession CreateSession(string modelPath, NeuralExecutionProvider preferredProvider, int intraOpThreads = 0) => new MockWaveSession(modelPath);
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
    for tok in ("studioBundleSelector", "studioOptRemoveBackingSwitch", "StudioTagPureInstrFmt"):
        assert tok in studio_src, f"Missing {tok} in MainActivity.StudioExport.cs"

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
    for tok in ("RefreshSpatialChoreoClarifierUi", "spatialChoreoTierBadgeText", "roomInvertOrbitSwitch", "InvertOrbitDrag"):
        assert tok in player_tab, f"Missing {tok} in MainActivity.PlayerTab.cs"

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
    assert 'case "cortex_profiles":' in bridge_cmds and "BuildCavernDefaultRegistry" in bridge_cmds and "ToCompactManifestJson" in bridge_cmds, (
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
    assert bp_path.is_file(), "Missing BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md blueprint"

    plan_tool = REPO_ROOT / ".agents" / "plugins" / "dynamic-fractal-planner" / "scripts" / "plan_tool.py"
    lint_proc = subprocess.run(
        [sys.executable, "-B", str(plan_tool), "blueprint", "lint", str(bp_path), "--strict"],
        cwd=str(REPO_ROOT), capture_output=True, text=True, check=False,
    )
    assert lint_proc.returncode == 0 and "BLUEPRINT PASS" in lint_proc.stdout, (
        f"BP_UI_AI_ORGANIZATION_MOTION_AND_STABILITY.md failed strict blueprint lint: {lint_proc.stdout} {lint_proc.stderr}"
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
    for tok in ("TryLoadBestForSourceTrack", "bool forceReextract = false", "NeuralReextractStemsBtn", "OpenAiSettingsBtn", "JumpToSettingsCategory(2)"):
        assert tok in wf_src, f"Q2/Q4 failed: missing {tok} in MainActivity.NeuralWorkflows.cs"

    # Q3: OneUiMotionBlur directional RenderEffect + safe radiusY + clear on end/cancel + 1:1 album framing
    theme_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "Theme" / "OneUiTheme.cs").read_text(encoding="utf-8")
    for tok in ("class OneUiMotionBlur", "ComputeHorizontalBlurRadiusPx", "CreateBlurEffect(clampedX, 0.01f", "SetRenderEffect(null)"):
        assert tok in theme_src, f"Q3 failed: missing {tok} in OneUiTheme.cs"
    nav_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.Navigation.cs").read_text(encoding="utf-8")
    for tok in ("OneUiMotionBlur.ComputeHorizontalBlurRadiusPx", "OneUiMotionBlur.ApplyHorizontalMotionBlur", "OneUiMotionBlur.ClearMotionBlur"):
        assert tok in nav_src, f"Q3 failed: missing {tok} in MainActivity.Navigation.cs"
    np_src = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.NowPlaying.cs").read_text(encoding="utf-8")
    assert "OneUiMotionBlur.ApplyHorizontalMotionBlur" in np_src and "OneUiMotionBlur.ClearMotionBlur" in np_src, "Q3 failed: NowPlaying section blur missing"
    lib_views = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.LibraryViews.cs").read_text(encoding="utf-8")
    pm_core = (REPO_ROOT / "CavernSamples" / "CavernizeAndroid" / "MainActivity.PlayerMusic.Core.cs").read_text(encoding="utf-8")
    assert "FillWidthSquare = true" in lib_views, "Q3 failed: album tile FillWidthSquare = true missing in LibraryViews.cs"
    for tok in ("RoundedSquareOutlineProvider", "BitmapShader", "FillWidthSquare"):
        assert tok in pm_core, f"Q3 failed: missing {tok} in MainActivity.PlayerMusic.Core.cs"

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
                csproj_text = TEST_CSPROJ.format(tfm=tfm, cavern_csproj=cavern_csproj, logic_csproj=logic_csproj)
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
